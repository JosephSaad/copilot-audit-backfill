/*
    dash.usp_RefreshDashboard
    ------------------------------------------------------------------------
    Rebuilds what the Copilot Insights dashboard reads besides the bi schema:

      1. dash.Person and dash.OrgClosure, whole, from audit.UserDirectory
         (on-premises AD) - so the security model follows today's
         organization. Small: one row per person, and one per (manager,
         person under them) pair.
      2. dash.UserDay from the bi facts, and dash.ResourceDay (labels and
         sites) from bi.FactAccessedResource, for the days from
         @SinceUtc's day on (every day when NULL), replaced in one transaction.
         Every day, too, when a full bi refresh has run since the last
         rollup, and from the first day of an app whose category a
         bi refresh changed.
      3. dash.AgentUse, whole: when each agent was first and last used.
      4. The totals the pages read: dash.UserItemDay (per person per app and
         agent) from @SinceUtc's day, as dash.UserDay; dash.UserWeek and
         dash.UserItemWeek from the week it falls in, dash.AdoptionDay from
         its day; dash.UserActivity, dash.UserPeriod, dash.UserItemPeriod and
         dash.SitePerson whole; each whole the first time.
      5. The columnstores over what it wrote, compressed, when the caller may
         alter the tables.

    Invoke-CopilotAuditBackfill.ps1 -Incremental and the CSV importer call it
    last, after bi.usp_RefreshBi, with the same @SinceUtc.

    While a full bi rebuild has not finished (bi.RebuildInProgress has a row:
    it is running, or was stopped part way), steps 2 and 3 are skipped and
    the rollups left as they are: the facts are half written. The
    first rollup after the rebuild has finished builds every day.

    The closure follows ManagerUPN up from each person. A manager missing from
    the directory (another employee type, say) still becomes an ancestor, so
    they can see their people when they sign in. A loop in the manager
    attributes stops at 40 levels and each pair keeps its shortest distance.

    The dashboard's app-pool account may run it (Admin > Health > Refresh)
    without ALTER: it truncates a table only when the caller may, and deletes
    its rows otherwise, which ownership chaining allows. That account needs
    EXECUTE on it, SELECT on bi (its first check looks for bi.FactInteraction)
    and SELECT on audit.UserDirectory, which it reads through dynamic SQL
    because the table is optional. Install-CopilotInsights.ps1 grants all three.

    Requires Create-CopilotDashboardSchema.sql and the bi schema.
*/
SET NOCOUNT ON;

IF OBJECT_ID(N'dash.usp_RefreshDashboard', N'P') IS NULL
    EXEC (N'CREATE PROCEDURE dash.usp_RefreshDashboard AS BEGIN SET NOCOUNT ON; END;');
GO

ALTER PROCEDURE dash.usp_RefreshDashboard
    @SinceUtc DATETIME2(0) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @started DATETIME2(0) = SYSUTCDATETIME(), @msg NVARCHAR(600);
    IF OBJECT_ID(N'bi.FactInteraction', N'U') IS NULL
        THROW 50201, N'The bi schema is missing: run sql/Create-CopilotBiSchema.sql and bi.usp_RefreshBi first.', 1;

    /* ---- 1. People, levels and the org closure ----------------------------- */

    DROP TABLE IF EXISTS #dir;
    CREATE TABLE #dir (Upn NVARCHAR(256) COLLATE DATABASE_DEFAULT NOT NULL PRIMARY KEY,
                       ManagerUpn NVARCHAR(256) COLLATE DATABASE_DEFAULT NULL, OrgDepth INT NULL);
    IF OBJECT_ID(N'audit.UserDirectory', N'U') IS NOT NULL
        EXEC (N'INSERT INTO #dir (Upn, ManagerUpn, OrgDepth)
                SELECT UserPrincipalName, NULLIF(NULLIF(ManagerUPN, N''''), UserPrincipalName), OrgDepth FROM audit.UserDirectory;');

    DROP TABLE IF EXISTS #clo;
    WITH c AS
    (
        SELECT d.Upn AS AncestorUpn, d.Upn AS DescendantUpn, 0 AS Depth
        FROM #dir AS d
        UNION ALL
        /* One step up: the current ancestor's own manager. A manager missing
           from #dir becomes an ancestor here and the walk stops there. */
        SELECT a.ManagerUpn, c.DescendantUpn, c.Depth + 1
        FROM c
        JOIN #dir AS a ON a.Upn = c.AncestorUpn
        WHERE a.ManagerUpn IS NOT NULL AND c.Depth < 40
    )
    SELECT AncestorUpn, DescendantUpn, MIN(Depth) AS Depth
    INTO #clo
    FROM c
    GROUP BY AncestorUpn, DescendantUpn
    OPTION (MAXRECURSION 41);

    DECLARE @top NVARCHAR(256) = (SELECT NULLIF(LTRIM(RTRIM(Value)), N'') FROM dash.Setting WHERE Name = 'OrgTopUpn');

    /* No top set: take it from the org chart - the one person with no manager
       who has at least half the directory under them - and record it, so the
       dashboard works with nothing configured. OrgTopUpn, when set, wins. */
    DECLARE @detected NVARCHAR(256) =
    (
        SELECT TOP (1) c.AncestorUpn
        FROM #clo AS c
        JOIN #dir AS d ON d.Upn = c.AncestorUpn AND d.ManagerUpn IS NULL
        GROUP BY c.AncestorUpn
        HAVING COUNT(*) * 2 >= (SELECT COUNT(*) FROM #dir)
        ORDER BY COUNT(*) DESC
    );
    MERGE dash.Setting AS t
    USING (SELECT 'OrgTopUpnDetected' AS Name, @detected AS Value) AS s ON t.Name = s.Name
    WHEN MATCHED THEN UPDATE SET Value = s.Value, UpdatedBy = N'dash.usp_RefreshDashboard', UpdatedUtc = SYSUTCDATETIME()
    WHEN NOT MATCHED THEN INSERT (Name, Value, Description, UpdatedBy)
        VALUES (s.Name, s.Value, N'The top of the organization found in the org chart, used while OrgTopUpn is empty. Written by each refresh.', N'dash.usp_RefreshDashboard');
    SET @top = COALESCE(@top, @detected);

    DROP TABLE IF EXISTS #person;
    CREATE TABLE #person (Upn NVARCHAR(256) COLLATE DATABASE_DEFAULT NOT NULL PRIMARY KEY, DisplayName NVARCHAR(256) NULL,
                          ManagerUpn NVARCHAR(256) NULL, Department NVARCHAR(200) NULL, AccountEnabled BIT NULL,
                          Sid VARCHAR(184) NULL);
    /* The SID column arrives with the 2.2.0 enrichment schema; a directory table from before it has none. */
    DECLARE @sidCol NVARCHAR(4) = CASE WHEN COL_LENGTH(N'audit.UserDirectory', N'Sid') IS NULL THEN N'NULL' ELSE N'Sid' END;
    IF OBJECT_ID(N'audit.UserDirectory', N'U') IS NOT NULL
        EXEC (N'INSERT INTO #person (Upn, DisplayName, ManagerUpn, Department, AccountEnabled, Sid)
                SELECT UserPrincipalName, FullName, NULLIF(NULLIF(ManagerUPN, N''''), UserPrincipalName), Department, AccountEnabled, ' + @sidCol + N'
                FROM audit.UserDirectory;');
    /* Managers outside the directory table still get a row, so they can sign in and see their people. */
    INSERT INTO #person (Upn)
    SELECT DISTINCT c.AncestorUpn FROM #clo AS c WHERE NOT EXISTS (SELECT 1 FROM #person AS p WHERE p.Upn = c.AncestorUpn);

    /* A level counts every manager between a person and the top, those without a UPN too. The closure runs
       on ManagerUPN, which passes over a manager without a UPN to the one above, so a person under such a manager
       right below the top came out at level 2, which sees the whole organization. The AD load's OrgDepth counts them
       all; the deeper of the two is taken, so a level is never nearer the top than the chart puts it. */
    DECLARE @topDepth INT = (SELECT OrgDepth FROM #dir WHERE Upn = @top);

    BEGIN TRANSACTION;
    DELETE FROM dash.OrgClosure;
    /* With each person's bi.DimUser key and whether their account is enabled, which the pages read with the pairs. */
    INSERT INTO dash.OrgClosure (AncestorUpn, DescendantUpn, Depth, DescendantKey, DescendantEnabled)
    SELECT c.AncestorUpn, c.DescendantUpn, c.Depth, u.UserKey, p.AccountEnabled
    FROM #clo AS c
    LEFT JOIN bi.DimUser AS u ON u.UserPrincipalName = c.DescendantUpn
    LEFT JOIN #person AS p ON p.Upn = c.DescendantUpn;

    DELETE FROM dash.Person;
    INSERT INTO dash.Person (Upn, DisplayName, ManagerUpn, Department, AccountEnabled, Sid, Level, DirectReports, PeopleBelow)
    SELECT p.Upn, p.DisplayName, p.ManagerUpn, p.Department, p.AccountEnabled, p.Sid,
           CASE WHEN @top IS NULL OR t.DescendantUpn IS NULL THEN NULL
                WHEN d.OrgDepth - @topDepth > t.Depth THEN d.OrgDepth - @topDepth + 1
                ELSE t.Depth + 1 END,
           (SELECT COUNT(*) FROM #clo AS c WHERE c.AncestorUpn = p.Upn AND c.Depth = 1),
           (SELECT COUNT(*) FROM #clo AS c WHERE c.AncestorUpn = p.Upn AND c.Depth >= 1)
    FROM #person AS p
    LEFT JOIN #clo AS t ON t.AncestorUpn = @top AND t.DescendantUpn = p.Upn
    LEFT JOIN #dir AS d ON d.Upn = p.Upn;
    /* Each person's Entra ID object ID, which a sign-in through Entra ID is matched by first. The column arrives with
       the dashboard schema, and its source with the enrichment schema: either may not be there yet. */
    IF COL_LENGTH(N'dash.Person', N'ObjectId') IS NOT NULL AND COL_LENGTH(N'audit.CopilotLicense', N'EntraObjectId') IS NOT NULL
        EXEC (N'UPDATE p SET ObjectId = l.EntraObjectId
                FROM dash.Person AS p JOIN audit.CopilotLicense AS l ON l.UserPrincipalName = p.Upn
                WHERE l.EntraObjectId IS NOT NULL;');
    COMMIT TRANSACTION;

    DECLARE @people INT = (SELECT COUNT(*) FROM dash.Person), @closure INT = (SELECT COUNT(*) FROM dash.OrgClosure);

    /* ---- 2. The daily rollup ------------------------------------------------- */

    /* Not while a full bi rebuild has not finished, running or stopped part way: the facts are half written.
       A rollup over them took every month the rebuild had not reached yet out of the rollups (the dashboard
       installer's refresh during an upgrade's rebuild, say), and the windowed rollups after it never put them back.
       The run that finishes the rebuild is a full one, so the first rollup after it builds every day (below). */
    DECLARE @rebuilding BIT = 0;
    IF OBJECT_ID(N'bi.RebuildInProgress', N'U') IS NOT NULL
        SET @rebuilding = CASE WHEN EXISTS (SELECT 1 FROM bi.RebuildInProgress) THEN 1 ELSE 0 END;
    IF @rebuilding = 1
    BEGIN
        SET @msg = CONCAT(N'Dashboard: org chart refreshed (', FORMAT(@people, 'N0'), N' people). The rollups are left as they are: a full ',
                          N'refresh of the bi schema has not finished (it is running, or was stopped part way). The next bi refresh finishes it, ',
                          N'and the first dashboard refresh after that rolls up every day.');
        RAISERROR(@msg, 0, 1) WITH NOWAIT;
        RETURN;
    END;

    DECLARE @fromKey INT = NULL;
    IF @SinceUtc IS NOT NULL
    BEGIN
        /* The bi facts are dated in the reporting zone of the last bi refresh. */
        DECLARE @zone SYSNAME = (SELECT TOP (1) ReportingTimeZone FROM bi.RefreshRun ORDER BY RunId DESC);
        DECLARE @localDay DATE = CAST(CAST(@SinceUtc AS DATETIMEOFFSET) AT TIME ZONE ISNULL(@zone, N'UTC') AS DATE);
        SET @fromKey = YEAR(@localDay) * 10000 + MONTH(@localDay) * 100 + DAY(@localDay);
    END;

    /* The bi refreshes since the last rollup, read before the facts are: a full rebuild that starts while this reads
       them is one of the next rollup's, which then builds every day. */
    DECLARE @biRun INT = (SELECT MAX(RunId) FROM bi.RefreshRun);
    DECLARE @lastBiRun INT = ISNULL((SELECT TOP (1) r.BiRunId FROM dash.RefreshRun AS r WHERE r.BiRunId IS NOT NULL ORDER BY r.RunId DESC), 0);

    /* A full bi refresh since the last rollup rewrote every interaction (a new rule for Premium or licensed
       use, license history that arrived late, another time zone, a stopped rebuild the nightly run finished), so
       every day is rolled up again. Before, only the window asked for was, and every older day kept the figures of
       the facts before it, for good: the dashboard and Power BI disagreed, and an upgrade's new Premium rule never
       reached the days before it (Forecasting's revoke list among them). */
    IF @fromKey IS NOT NULL AND EXISTS (SELECT 1 FROM bi.RefreshRun WHERE RunId > @lastBiRun AND SinceUtc IS NULL)
    BEGIN
        RAISERROR('A full refresh of the bi schema ran since the last rollup: rolling up every day.', 0, 1) WITH NOWAIT;
        SET @fromKey = NULL;
    END;

    /* A bi refresh since the last rollup that changed an app's category moves the start back to the
       first day that app was used: Premium use and ChatInteractions depend on the category. */
    IF @fromKey IS NOT NULL
    BEGIN
        DECLARE @recatFrom INT = (SELECT MIN(b.RecategorizedFromDateKey) FROM bi.RefreshRun AS b WHERE b.RunId > @lastBiRun);
        IF @recatFrom < @fromKey SET @fromKey = @recatFrom;
    END;

    DECLARE @noAgent INT = (SELECT AgentKey FROM bi.DimAgent WHERE AgentId = N'(none)');

    DROP TABLE IF EXISTS #day;
    WITH i AS
    (
        SELECT f.DateKey, f.UserKey,
               COUNT_BIG(*)                                                          AS Interactions,
               SUM(f.PromptCount)                                                    AS Prompts,
               COUNT(DISTINCT f.AppKey)                                              AS AppsUsed,
               SUM(CASE WHEN f.AgentKey <> ISNULL(@noAgent, -1) THEN 1 ELSE 0 END)   AS AgentInteractions,
               SUM(CAST(f.UsedWebSearch AS INT))                                     AS WebGrounded,
               SUM(CASE WHEN a.AppCategory = N'Copilot Chat' THEN 1 ELSE 0 END)      AS ChatInteractions,
               SUM(CASE WHEN f.LicenseStatusKey = 1 THEN 1 ELSE 0 END)               AS LicensedUse,
               SUM(CAST(f.PremiumUse AS INT))                                        AS PremiumUse,   -- set by bi.usp_RefreshBi, the rule's one definition
               SUM(CAST(f.JailbreakDetected AS INT))                                 AS Jailbreak,
               SUM(CAST(f.PromptInjectionDetected AS INT))                           AS PromptInjection,
               SUM(CAST(f.HasBlockedResource AS INT))                                AS BlockedResource,
               SUM(CAST(f.HasSensitivityLabel AS INT))                               AS LabeledContent
        FROM bi.FactInteraction AS f
        JOIN bi.DimApp AS a ON a.AppKey = f.AppKey
        WHERE @fromKey IS NULL OR f.DateKey >= @fromKey
        GROUP BY f.DateKey, f.UserKey
    ),
    s AS
    (
        SELECT s.DateKey, s.UserKey, COUNT(*) AS Sessions, SUM(CAST(s.IsMultiTurn AS INT)) AS MultiTurnSessions
        FROM bi.FactSession AS s
        WHERE @fromKey IS NULL OR s.DateKey >= @fromKey
        GROUP BY s.DateKey, s.UserKey
    )
    SELECT COALESCE(i.DateKey, s.DateKey) AS DateKey, COALESCE(i.UserKey, s.UserKey) AS UserKey,
           CAST(ISNULL(i.Interactions, 0) AS INT) AS Interactions, ISNULL(i.Prompts, 0) AS Prompts,
           CAST(ISNULL(i.AppsUsed, 0) AS TINYINT) AS AppsUsed,
           ISNULL(i.AgentInteractions, 0) AS AgentInteractions, ISNULL(i.WebGrounded, 0) AS WebGrounded,
           ISNULL(i.ChatInteractions, 0) AS ChatInteractions, ISNULL(i.LicensedUse, 0) AS LicensedUse, ISNULL(i.PremiumUse, 0) AS PremiumUse,
           ISNULL(i.Jailbreak, 0) AS Jailbreak, ISNULL(i.PromptInjection, 0) AS PromptInjection,
           ISNULL(i.BlockedResource, 0) AS BlockedResource, ISNULL(i.LabeledContent, 0) AS LabeledContent,
           ISNULL(s.Sessions, 0) AS Sessions, ISNULL(s.MultiTurnSessions, 0) AS MultiTurnSessions
    INTO #day
    FROM i
    FULL JOIN s ON s.DateKey = i.DateKey AND s.UserKey = i.UserKey
    OPTION (RECOMPILE);

    /* Labels and sites, from bi.FactAccessedResource: the label on each resource
       read, the site of labeled content, and the site of content Copilot was
       blocked from. Sites are already cut to https://host/sites/name there.
       Interactions, not resources: the fact has a row per resource, and
       each was counted, so an answer drawn from six files with one label added
       six to that label and its site, where the page's own count of labeled
       interactions added one, and the label alert fired on a single interaction. */
    DROP TABLE IF EXISTS #res;
    WITH r AS
    (
        SELECT f.DateKey, f.UserKey, f.RecordId, f.IsBlocked, l.LabelId, s.Site
        FROM bi.FactAccessedResource AS f
        JOIN bi.DimSensitivityLabel AS l ON l.SensitivityLabelKey = f.SensitivityLabelKey
        JOIN bi.DimSite AS s ON s.SiteKey = f.SiteKey
        WHERE (@fromKey IS NULL OR f.DateKey >= @fromKey)
          AND (l.LabelId IS NOT NULL OR f.IsBlocked = 1)
    ),
    items AS
    (
        SELECT DateKey, UserKey, RecordId, 'Label' AS Kind, LOWER(CAST(LabelId AS NVARCHAR(36))) AS Item FROM r WHERE LabelId IS NOT NULL
        UNION ALL
        SELECT DateKey, UserKey, RecordId, 'Site', Site FROM r WHERE LabelId IS NOT NULL AND Site <> N'(none)'
        UNION ALL
        SELECT DateKey, UserKey, RecordId, 'Blocked', Site FROM r WHERE IsBlocked = 1 AND Site <> N'(none)'
    )
    SELECT DateKey, UserKey, Kind, CAST(LEFT(Item, 400) AS NVARCHAR(400)) AS Item, COUNT(DISTINCT RecordId) AS Interactions
    INTO #res
    FROM items
    GROUP BY DateKey, UserKey, Kind, LEFT(Item, 400)
    OPTION (RECOMPILE);

    BEGIN TRANSACTION;
    IF @fromKey IS NULL AND HAS_PERMS_BY_NAME(N'dash.UserDay', N'OBJECT', N'ALTER') = 1
        TRUNCATE TABLE dash.UserDay;
    ELSE IF @fromKey IS NULL
        DELETE FROM dash.UserDay;
    ELSE
        DELETE FROM dash.UserDay WHERE DateKey >= @fromKey;
    INSERT INTO dash.UserDay WITH (TABLOCK)
        (DateKey, UserKey, Interactions, Prompts, AppsUsed, AgentInteractions, WebGrounded, ChatInteractions,
         LicensedUse, PremiumUse, Jailbreak, PromptInjection, BlockedResource, LabeledContent, Sessions, MultiTurnSessions)
    SELECT DateKey, UserKey, Interactions, Prompts, AppsUsed, AgentInteractions, WebGrounded, ChatInteractions,
           LicensedUse, PremiumUse, Jailbreak, PromptInjection, BlockedResource, LabeledContent, Sessions, MultiTurnSessions
    FROM #day;
    DECLARE @dayRows INT = @@ROWCOUNT;
    IF @fromKey IS NULL AND HAS_PERMS_BY_NAME(N'dash.ResourceDay', N'OBJECT', N'ALTER') = 1
        TRUNCATE TABLE dash.ResourceDay;
    ELSE IF @fromKey IS NULL
        DELETE FROM dash.ResourceDay;
    ELSE
        DELETE FROM dash.ResourceDay WHERE DateKey >= @fromKey;
    INSERT INTO dash.ResourceDay WITH (TABLOCK) (DateKey, UserKey, Kind, Item, Interactions)
    SELECT DateKey, UserKey, Kind, Item, Interactions FROM #res;
    /* The flagged interactions, for Security > Flagged interactions. From @fromKey on, or all of them when
       the table is empty, as it is the first time after an upgrade (a refresh of today alone would leave history out). */
    IF OBJECT_ID(N'dash.FlaggedInteraction', N'U') IS NOT NULL
    BEGIN
        DECLARE @flagFrom INT = CASE WHEN EXISTS (SELECT 1 FROM dash.FlaggedInteraction) THEN @fromKey ELSE NULL END;
        DELETE FROM dash.FlaggedInteraction WHERE @flagFrom IS NULL OR DateKey >= @flagFrom;
        INSERT INTO dash.FlaggedInteraction (DateKey, UserKey, RecordId, ActivityHour, AppKey, AgentKey, JailbreakDetected, PromptInjectionDetected, HasBlockedResource)
        SELECT f.DateKey, f.UserKey, f.RecordId, f.ActivityHour, f.AppKey, f.AgentKey, f.JailbreakDetected, f.PromptInjectionDetected, f.HasBlockedResource
        FROM bi.FactInteraction AS f
        WHERE (@flagFrom IS NULL OR f.DateKey >= @flagFrom)
          AND (f.JailbreakDetected = 1 OR f.PromptInjectionDetected = 1 OR f.HasBlockedResource = 1);
    END;
    DELETE FROM dash.AgentUse;
    INSERT INTO dash.AgentUse (AgentKey, FirstDateKey, LastDateKey, Interactions)
    SELECT AgentKey, MIN(DateKey), MAX(DateKey), COUNT_BIG(*) FROM bi.FactInteraction GROUP BY AgentKey;
    COMMIT TRANSACTION;

    /* Each person's use of each app and each agent per day (dash.UserItemDay), from the same day as dash.UserDay, or
       every day the first time. Apps and agents apart: most interactions have no agent, and are not read twice. Also
       per use case (U), license type Copilot recorded (L), model (M), and the interactions with each of three flags
       (F). The kinds the table holds are recorded (setting DashItemKinds): a database whose table was built with
       fewer kinds is built whole once, so the new kinds cover every day. */
    DECLARE @kinds NVARCHAR(40) = N'A,G,U,L,M,F';
    DECLARE @itemFrom INT = CASE WHEN EXISTS (SELECT 1 FROM dash.UserItemDay)
                                  AND EXISTS (SELECT 1 FROM dash.Setting WHERE Name = 'DashItemKinds' AND Value = @kinds) THEN @fromKey END;
    DROP TABLE IF EXISTS #item;
    SELECT f.DateKey, f.UserKey, CAST('A' AS CHAR(1)) AS Kind, f.AppKey AS Item, CAST(COUNT_BIG(*) AS INT) AS Interactions,
           ISNULL(SUM(f.PromptCount), 0) AS Prompts, ISNULL(SUM(f.AccessedResourceCount), 0) AS Resources,
           ISNULL(SUM(CAST(f.UsedWebSearch AS INT)), 0) AS Web
    INTO #item
    FROM bi.FactInteraction AS f
    WHERE @itemFrom IS NULL OR f.DateKey >= @itemFrom
    GROUP BY f.DateKey, f.UserKey, f.AppKey
    OPTION (RECOMPILE);
    INSERT INTO #item WITH (TABLOCK) (DateKey, UserKey, Kind, Item, Interactions, Prompts, Resources, Web)
    SELECT f.DateKey, f.UserKey, 'G', f.AgentKey, CAST(COUNT_BIG(*) AS INT), ISNULL(SUM(f.PromptCount), 0),
           ISNULL(SUM(f.AccessedResourceCount), 0), ISNULL(SUM(CAST(f.UsedWebSearch AS INT)), 0)
    FROM bi.FactInteraction AS f
    WHERE (@itemFrom IS NULL OR f.DateKey >= @itemFrom) AND f.AgentKey <> ISNULL(@noAgent, -1)
    GROUP BY f.DateKey, f.UserKey, f.AgentKey
    OPTION (RECOMPILE);
    INSERT INTO #item WITH (TABLOCK) (DateKey, UserKey, Kind, Item, Interactions, Prompts, Resources, Web)
    SELECT f.DateKey, f.UserKey, k.Kind, k.Item, CAST(COUNT_BIG(*) AS INT), ISNULL(SUM(f.PromptCount), 0),
           ISNULL(SUM(f.AccessedResourceCount), 0), ISNULL(SUM(CAST(f.UsedWebSearch AS INT)), 0)
    FROM bi.FactInteraction AS f
    CROSS APPLY (VALUES ('U', f.UseCaseKey), ('L', f.LicenseTypeKey), ('M', f.ModelKey),
                        ('F', CASE WHEN f.MemoryUpdated = 1 THEN 1 END), ('F', CASE WHEN f.CalledAnotherAgent = 1 THEN 2 END),
                        ('F', CASE WHEN f.DlpEvaluationDeferred = 1 THEN 3 END)) AS k (Kind, Item)
    WHERE (@itemFrom IS NULL OR f.DateKey >= @itemFrom) AND k.Item IS NOT NULL
    GROUP BY f.DateKey, f.UserKey, k.Kind, k.Item
    OPTION (RECOMPILE);
    /* Written whole, the table's columnstore is set aside and built again after, in one pass: kept up row by row, a
       first build of tens of millions of rows takes minutes in its delta stores. */
    DECLARE @itemWhole BIT = CASE WHEN @itemFrom IS NULL AND HAS_PERMS_BY_NAME(N'dash.UserItemDay', N'OBJECT', N'ALTER') = 1 THEN 1 ELSE 0 END;
    BEGIN TRANSACTION;
    IF @itemWhole = 1
    BEGIN
        ALTER INDEX NCCI_DashUserItemDay ON dash.UserItemDay DISABLE;
        TRUNCATE TABLE dash.UserItemDay;
    END;
    ELSE IF @itemFrom IS NULL
        DELETE FROM dash.UserItemDay;
    ELSE
        DELETE FROM dash.UserItemDay WHERE DateKey >= @itemFrom;
    INSERT INTO dash.UserItemDay WITH (TABLOCK) (DateKey, UserKey, Kind, Item, Interactions, Prompts, Resources, Web)
    SELECT DateKey, UserKey, Kind, Item, Interactions, Prompts, Resources, Web FROM #item;
    IF @itemWhole = 1
        ALTER INDEX NCCI_DashUserItemDay ON dash.UserItemDay REBUILD;
    MERGE dash.Setting AS t
    USING (SELECT 'DashItemKinds' AS Name, @kinds AS Value) AS s ON t.Name = s.Name
    WHEN MATCHED AND t.Value <> s.Value THEN UPDATE SET Value = s.Value, UpdatedBy = N'dash.usp_RefreshDashboard', UpdatedUtc = SYSUTCDATETIME()
    WHEN NOT MATCHED THEN INSERT (Name, Value, Description, UpdatedBy)
        VALUES (s.Name, s.Value, N'The kinds dash.UserItemDay holds. Written by each refresh: one built with fewer is built whole once.', N'dash.usp_RefreshDashboard');
    COMMIT TRANSACTION;
    DROP TABLE #item;

    /* What the Copilot Credit estimate counts, per person, agent and day (dash.CreditUseDay), from the same day as
       dash.UserItemDay, or every day the first time. Billed: an agent used by someone not licensed at the time
       (status 2; an unknown status is not taken for unlicensed), or in a Copilot Studio channel. */
    DECLARE @creditFrom INT = CASE WHEN EXISTS (SELECT 1 FROM dash.CreditUseDay) THEN @itemFrom END;
    DROP TABLE IF EXISTS #credit;
    SELECT fi.DateKey, fi.UserKey, fi.AgentKey,
           CAST(COUNT_BIG(*) AS INT) AS I, SUM(x.IsAgent) AS A, SUM(x.Billed) AS B, SUM(x.Billed * x.Work) AS BW, SUM(x.Billed * x.Calls) AS BA,
           SUM(CAST(ISNULL(fi.AccessedResourceCount, 0) AS BIGINT)) AS R, SUM(CAST(ISNULL(fi.MessageCount, 1) AS BIGINT)) AS M,
           CAST(MAX(x.Lic) AS TINYINT) AS Lic, CAST(MAX(x.Billed) AS TINYINT) AS AnyBilled
    INTO #credit
    FROM bi.FactInteraction AS fi
    JOIN bi.DimAgent AS ag ON ag.AgentKey = fi.AgentKey
    JOIN bi.DimApp AS ap ON ap.AppKey = fi.AppKey
    CROSS APPLY (SELECT CASE WHEN ag.AgentSource = N'(no agent)' THEN 0 ELSE 1 END AS IsAgent,
                        CASE WHEN fi.LicenseStatusKey = 1 THEN 1 ELSE 0 END AS Lic,
                        CASE WHEN ag.AgentSource <> N'(no agent)' AND (fi.LicenseStatusKey = 2 OR ap.App = N'Copilot Studio') THEN 1 ELSE 0 END AS Billed,
                        CASE WHEN fi.AccessedResourceCount > 0 OR fi.UseKind IN (1, 3) THEN 1 ELSE 0 END AS Work,
                        CAST(ISNULL(fi.CalledAnotherAgent, 0) AS INT) AS Calls) AS x
    WHERE @creditFrom IS NULL OR fi.DateKey >= @creditFrom
    GROUP BY fi.DateKey, fi.UserKey, fi.AgentKey
    OPTION (RECOMPILE);
    DECLARE @creditWhole BIT = CASE WHEN @creditFrom IS NULL AND HAS_PERMS_BY_NAME(N'dash.CreditUseDay', N'OBJECT', N'ALTER') = 1 THEN 1 ELSE 0 END;
    BEGIN TRANSACTION;
    IF @creditWhole = 1
    BEGIN
        ALTER INDEX NCCI_DashCreditUseDay ON dash.CreditUseDay DISABLE;
        TRUNCATE TABLE dash.CreditUseDay;
    END;
    ELSE IF @creditFrom IS NULL
        DELETE FROM dash.CreditUseDay;
    ELSE
        DELETE FROM dash.CreditUseDay WHERE DateKey >= @creditFrom;
    INSERT INTO dash.CreditUseDay WITH (TABLOCK) (DateKey, UserKey, AgentKey, I, A, B, BW, BA, R, M, Lic, AnyBilled)
    SELECT DateKey, UserKey, AgentKey, I, A, B, BW, BA, R, M, Lic, AnyBilled FROM #credit;
    IF @creditWhole = 1
        ALTER INDEX NCCI_DashCreditUseDay ON dash.CreditUseDay REBUILD;
    COMMIT TRANSACTION;
    DROP TABLE #credit;

    /* ---- 3. Totals of dash.UserDay the pages read ---------------------------- */

    /* Each is worked out from dash.UserDay as it now stands, into a temporary table, then put in place in a short
       transaction of its own: a page reading one waits for the swap, not for the sums. A page that reads one while
       another is still being swapped is kept under the data stamp from before, which this run's dash.RefreshRun row
       ends. Where the table is empty (the first refresh after an upgrade), or the run rebuilds every day, each is
       built whole. */
    DECLARE @maxKey INT = (SELECT MAX(DateKey) FROM dash.UserDay);
    DECLARE @minKey INT = (SELECT MIN(DateKey) FROM dash.UserDay);

    /* The week @fromKey falls in: the per app and agent weeks are rebuilt from it. */
    DECLARE @weekFrom DATE = NULL, @weekFromKey INT = NULL;
    IF @fromKey IS NOT NULL AND EXISTS (SELECT 1 FROM dash.UserWeek)
        SELECT @weekFrom = d.WeekEnding,
               @weekFromKey = (SELECT s.DateKey FROM bi.DimDate AS s WHERE s.[Date] = DATEADD(DAY, -6, d.WeekEnding))
        FROM bi.DimDate AS d WHERE d.DateKey = @fromKey;
    IF @weekFromKey IS NULL SET @weekFrom = NULL;
    /* By week, whole: with whether the person held a license on the week's last day, which bi.usp_RefreshBi can change
       for any day when it rebuilds the license days. */
    DROP TABLE IF EXISTS #week;
    WITH w AS (
    SELECT d.WeekEnding, ud.UserKey, CAST(COUNT(CASE WHEN ud.Interactions > 0 THEN 1 END) AS TINYINT) AS ActiveDays,
           SUM(ud.Interactions) AS Interactions, SUM(ud.Prompts) AS Prompts, SUM(ud.AgentInteractions) AS AgentInteractions,
           SUM(ud.WebGrounded) AS WebGrounded, SUM(ud.ChatInteractions) AS ChatInteractions, SUM(ud.LicensedUse) AS LicensedUse,
           SUM(ud.PremiumUse) AS PremiumUse, SUM(ud.Jailbreak) AS Jailbreak, SUM(ud.PromptInjection) AS PromptInjection,
           SUM(ud.BlockedResource) AS BlockedResource, SUM(ud.LabeledContent) AS LabeledContent, SUM(ud.Sessions) AS Sessions,
           SUM(ud.MultiTurnSessions) AS MultiTurnSessions
    FROM dash.UserDay AS ud
    JOIN bi.DimDate AS d ON d.DateKey = ud.DateKey
    GROUP BY d.WeekEnding, ud.UserKey)
    SELECT w.*, CAST(CASE WHEN ld.UserKey IS NULL THEN 0 ELSE 1 END AS BIT) AS HeldAtEnd
    INTO #week
    FROM w
    LEFT JOIN bi.FactLicensedDay AS ld ON ld.DateKey = CONVERT(INT, CONVERT(CHAR(8), w.WeekEnding, 112)) AND ld.UserKey = w.UserKey
    OPTION (RECOMPILE);

    /* The same per app and agent (dash.UserItemWeek), from the same week, or every week the first time and whenever
       dash.UserItemDay was just built whole (new kinds). */
    DECLARE @itemWeekFrom DATE = CASE WHEN @itemFrom IS NOT NULL AND EXISTS (SELECT 1 FROM dash.UserItemWeek) THEN @weekFrom END;
    DECLARE @itemWeekFromKey INT = CASE WHEN @itemWeekFrom IS NOT NULL THEN @weekFromKey END;
    DROP TABLE IF EXISTS #itemweek;
    SELECT d.WeekEnding, ui.UserKey, ui.Kind, ui.Item, CAST(COUNT(*) AS TINYINT) AS ActiveDays, SUM(ui.Interactions) AS Interactions,
           SUM(ui.Prompts) AS Prompts, SUM(ui.Resources) AS Resources, SUM(ui.Web) AS Web
    INTO #itemweek
    FROM dash.UserItemDay AS ui
    JOIN bi.DimDate AS d ON d.DateKey = ui.DateKey
    WHERE @itemWeekFromKey IS NULL OR ui.DateKey >= @itemWeekFromKey
    GROUP BY d.WeekEnding, ui.UserKey, ui.Kind, ui.Item
    OPTION (RECOMPILE);

    /* Adoption's 28 days: each day a person used Copilot with a license keeps them adopted until their next such
       day, or for 28 days, so each person-day is written once. From @fromKey on: a day looks back, never forward,
       so the days before it keep theirs; it reads the 27 days before @fromKey for them. */
    DECLARE @adoptFrom INT = CASE WHEN @fromKey IS NOT NULL AND EXISTS (SELECT 1 FROM dash.AdoptionDay) THEN @fromKey END;
    DECLARE @adoptFromN INT = (SELECT DATEDIFF(DAY, 0, [Date]) FROM bi.DimDate WHERE DateKey = @adoptFrom);
    IF @adoptFromN IS NULL SET @adoptFrom = NULL;
    DECLARE @useFromKey INT = (SELECT MIN(DateKey) FROM bi.DimDate WHERE DATEDIFF(DAY, 0, [Date]) >= @adoptFromN - 27);
    DROP TABLE IF EXISTS #adopt;
    WITH used AS (SELECT ud.UserKey, DATEDIFF(DAY, 0, d.[Date]) AS N
                  FROM dash.UserDay AS ud JOIN bi.DimDate AS d ON d.DateKey = ud.DateKey
                  WHERE ud.LicensedUse > 0 AND (@adoptFrom IS NULL OR ud.DateKey >= @useFromKey)),
    iv AS (SELECT u.UserKey, u.N, ISNULL(LEAD(u.N) OVER (PARTITION BY u.UserKey ORDER BY u.N) - u.N, 28) AS Gap FROM used AS u),
    k AS (SELECT n FROM (VALUES (0), (1), (2), (3), (4), (5), (6), (7), (8), (9), (10), (11), (12), (13), (14), (15), (16), (17), (18), (19),
                                (20), (21), (22), (23), (24), (25), (26), (27)) AS v (n)),
    cover AS (SELECT iv.UserKey, iv.N + k.n AS DayN FROM iv JOIN k ON k.n < iv.Gap)
    SELECT d.DateKey, c.UserKey
    INTO #adopt
    FROM cover AS c
    JOIN bi.DimDate AS d ON DATEDIFF(DAY, 0, d.[Date]) = c.DayN
    WHERE d.DateKey <= @maxKey AND (@adoptFrom IS NULL OR d.DateKey >= @adoptFrom)
    OPTION (RECOMPILE);

    /* Each person and each site they read labeled content from or were blocked at, with the last day they did and the
       last before @maxKey (dash.SitePerson), from dash.ResourceDay as it now stands: whole, as both move every day. */
    DROP TABLE IF EXISTS #siteperson;
    SELECT CAST(CASE r.Kind WHEN 'Site' THEN 'S' ELSE 'B' END AS CHAR(1)) AS Kind, s.SiteKey, r.UserKey,
           MAX(r.DateKey) AS LastKey, MAX(CASE WHEN r.DateKey < @maxKey THEN r.DateKey END) AS PrevKey
    INTO #siteperson
    FROM dash.ResourceDay AS r
    JOIN bi.DimSite AS s ON CAST(LEFT(s.Site, 400) AS NVARCHAR(400)) = r.Item
    WHERE r.Kind IN ('Site', 'Blocked')
    GROUP BY r.Kind, s.SiteKey, r.UserKey
    OPTION (RECOMPILE);

    /* Each person's first and last day of use, over every day. */
    DROP TABLE IF EXISTS #act;
    SELECT ud.UserKey, MIN(CASE WHEN ud.Interactions > 0 THEN ud.DateKey END) AS FirstActiveKey,
           MAX(CASE WHEN ud.Interactions > 0 THEN ud.DateKey END) AS LastActiveKey
    INTO #act
    FROM dash.UserDay AS ud
    GROUP BY ud.UserKey;

    /* Each person's sums over every period a page offers, ending on the last day with data or the day before it: a
       page's period ends on the last complete day, which is one of the two whether or not today has begun arriving.
       Days 0 is All data, from the first day with data. */
    DROP TABLE IF EXISTS #period;
    CREATE TABLE #period (EndKey INT NOT NULL, Days SMALLINT NOT NULL, UserKey INT NOT NULL, ActiveDays SMALLINT NOT NULL, PremiumDays SMALLINT NOT NULL,
                          LastActiveKey INT NULL, MostApps TINYINT NOT NULL, Interactions INT NOT NULL, Prompts INT NOT NULL, AgentInteractions INT NOT NULL, WebGrounded INT NOT NULL,
                          ChatInteractions INT NOT NULL, LicensedUse INT NOT NULL, PremiumUse INT NOT NULL, Jailbreak INT NOT NULL, PromptInjection INT NOT NULL,
                          BlockedResource INT NOT NULL, LabeledContent INT NOT NULL, Sessions INT NOT NULL, MultiTurnSessions INT NOT NULL, HeldUse INT NOT NULL);
    DROP TABLE IF EXISTS #itemperiod;
    CREATE TABLE #itemperiod (EndKey INT NOT NULL, Days SMALLINT NOT NULL, Kind CHAR(1) NOT NULL, Item INT NOT NULL, UserKey INT NOT NULL,
                              ActiveDays SMALLINT NOT NULL, LastActiveKey INT NOT NULL, Interactions INT NOT NULL, Prompts INT NOT NULL,
                              Resources INT NOT NULL, Web INT NOT NULL);
    DROP TABLE IF EXISTS #creditperiod;
    CREATE TABLE #creditperiod (EndKey INT NOT NULL, Days SMALLINT NOT NULL, UserKey INT NOT NULL, AgentKey INT NOT NULL, I BIGINT NOT NULL,
                                A BIGINT NOT NULL, B BIGINT NOT NULL, BW BIGINT NOT NULL, BA BIGINT NOT NULL, R BIGINT NOT NULL, M BIGINT NOT NULL,
                                Lic TINYINT NOT NULL, AnyBilled TINYINT NOT NULL);
    DECLARE @pEnd INT, @pDays SMALLINT, @pFrom INT;
    DECLARE periods CURSOR LOCAL FAST_FORWARD FOR
        SELECT e.EndKey, p.Days
        FROM (SELECT @maxKey AS EndKey UNION SELECT MAX(DateKey) FROM dash.UserDay WHERE DateKey < @maxKey) AS e
        CROSS JOIN (VALUES (7), (14), (30), (90), (180), (365), (730), (0)) AS p (Days)
        WHERE e.EndKey IS NOT NULL;
    OPEN periods;
    FETCH NEXT FROM periods INTO @pEnd, @pDays;
    WHILE @@FETCH_STATUS = 0
    BEGIN
        SET @pFrom = CASE WHEN @pDays = 0 THEN @minKey
                          ELSE CONVERT(INT, CONVERT(CHAR(8), DATEADD(DAY, 1 - @pDays, CONVERT(DATE, CONVERT(CHAR(8), @pEnd))), 112)) END;
        INSERT INTO #period WITH (TABLOCK)
        SELECT @pEnd, @pDays, ud.UserKey, COUNT(CASE WHEN ud.Interactions > 0 THEN 1 END), COUNT(CASE WHEN ud.PremiumUse > 0 THEN 1 END),
               MAX(CASE WHEN ud.Interactions > 0 THEN ud.DateKey END), MAX(ud.AppsUsed),
               SUM(ud.Interactions), SUM(ud.Prompts), SUM(ud.AgentInteractions), SUM(ud.WebGrounded), SUM(ud.ChatInteractions),
               SUM(ud.LicensedUse), SUM(ud.PremiumUse), SUM(ud.Jailbreak), SUM(ud.PromptInjection), SUM(ud.BlockedResource),
               SUM(ud.LabeledContent), SUM(ud.Sessions), SUM(ud.MultiTurnSessions),
               SUM(CASE WHEN ld.UserKey IS NOT NULL THEN ud.Interactions ELSE 0 END)   -- use on days holding a license
        FROM dash.UserDay AS ud
        LEFT JOIN bi.FactLicensedDay AS ld ON ld.DateKey = ud.DateKey AND ld.UserKey = ud.UserKey
        WHERE ud.DateKey BETWEEN @pFrom AND @pEnd
        GROUP BY ud.UserKey
        OPTION (RECOMPILE);
        INSERT INTO #itemperiod WITH (TABLOCK)
        SELECT @pEnd, @pDays, ui.Kind, ui.Item, ui.UserKey, COUNT(*), MAX(ui.DateKey), SUM(ui.Interactions), SUM(ui.Prompts), SUM(ui.Resources), SUM(ui.Web)
        FROM dash.UserItemDay AS ui
        WHERE ui.DateKey BETWEEN @pFrom AND @pEnd
        GROUP BY ui.Kind, ui.Item, ui.UserKey
        OPTION (RECOMPILE);
        INSERT INTO #creditperiod WITH (TABLOCK)
        SELECT @pEnd, @pDays, cu.UserKey, cu.AgentKey, SUM(CAST(cu.I AS BIGINT)), SUM(CAST(cu.A AS BIGINT)), SUM(CAST(cu.B AS BIGINT)),
               SUM(CAST(cu.BW AS BIGINT)), SUM(CAST(cu.BA AS BIGINT)), SUM(cu.R), SUM(cu.M), MAX(cu.Lic), MAX(cu.AnyBilled)
        FROM dash.CreditUseDay AS cu
        WHERE cu.DateKey BETWEEN @pFrom AND @pEnd
        GROUP BY cu.UserKey, cu.AgentKey
        OPTION (RECOMPILE);
        FETCH NEXT FROM periods INTO @pEnd, @pDays;
    END;
    CLOSE periods;
    DEALLOCATE periods;

    DECLARE @mayTruncate BIT = CASE WHEN HAS_PERMS_BY_NAME(N'dash.UserWeek', N'OBJECT', N'ALTER') = 1 THEN 1 ELSE 0 END;

    BEGIN TRANSACTION;
    IF @mayTruncate = 1 TRUNCATE TABLE dash.UserWeek; ELSE DELETE FROM dash.UserWeek;
    INSERT INTO dash.UserWeek WITH (TABLOCK)
        (WeekEnding, UserKey, ActiveDays, Interactions, Prompts, AgentInteractions, WebGrounded, ChatInteractions, LicensedUse, PremiumUse,
         Jailbreak, PromptInjection, BlockedResource, LabeledContent, Sessions, MultiTurnSessions, HeldAtEnd)
    SELECT WeekEnding, UserKey, ActiveDays, Interactions, Prompts, AgentInteractions, WebGrounded, ChatInteractions, LicensedUse, PremiumUse,
           Jailbreak, PromptInjection, BlockedResource, LabeledContent, Sessions, MultiTurnSessions, HeldAtEnd
    FROM #week;
    COMMIT TRANSACTION;

    BEGIN TRANSACTION;
    IF @adoptFrom IS NULL AND @mayTruncate = 1 TRUNCATE TABLE dash.AdoptionDay;
    ELSE IF @adoptFrom IS NULL DELETE FROM dash.AdoptionDay;
    ELSE DELETE FROM dash.AdoptionDay WHERE DateKey >= @adoptFrom;
    INSERT INTO dash.AdoptionDay WITH (TABLOCK) (DateKey, UserKey) SELECT DateKey, UserKey FROM #adopt;
    COMMIT TRANSACTION;

    BEGIN TRANSACTION;
    IF @mayTruncate = 1 TRUNCATE TABLE dash.UserActivity; ELSE DELETE FROM dash.UserActivity;
    INSERT INTO dash.UserActivity WITH (TABLOCK) (UserKey, FirstActiveKey, LastActiveKey) SELECT UserKey, FirstActiveKey, LastActiveKey FROM #act;
    IF @mayTruncate = 1 TRUNCATE TABLE dash.UserPeriod; ELSE DELETE FROM dash.UserPeriod;
    INSERT INTO dash.UserPeriod WITH (TABLOCK)
        (EndKey, Days, UserKey, ActiveDays, PremiumDays, LastActiveKey, MostApps, Interactions, Prompts, AgentInteractions, WebGrounded, ChatInteractions,
         LicensedUse, PremiumUse, Jailbreak, PromptInjection, BlockedResource, LabeledContent, Sessions, MultiTurnSessions, HeldUse)
    SELECT EndKey, Days, UserKey, ActiveDays, PremiumDays, LastActiveKey, MostApps, Interactions, Prompts, AgentInteractions, WebGrounded, ChatInteractions,
           LicensedUse, PremiumUse, Jailbreak, PromptInjection, BlockedResource, LabeledContent, Sessions, MultiTurnSessions, HeldUse
    FROM #period;
    COMMIT TRANSACTION;

    /* The whole organization's license holders on each day, those enabled today, and those of them adopted that day:
       after dash.AdoptionDay, whole, as the license days and the enabled flags are. */
    DROP TABLE IF EXISTS #held;
    SELECT ld.DateKey, COUNT(*) AS Held, COUNT(en.UserKey) AS HeldEnabled,
           COUNT(CASE WHEN en.UserKey IS NOT NULL AND ad.UserKey IS NOT NULL THEN 1 END) AS AdoptedEnabled
    INTO #held
    FROM bi.FactLicensedDay AS ld
    LEFT JOIN bi.DimUser AS en ON en.UserKey = ld.UserKey AND en.AccountEnabled = 1
    LEFT JOIN dash.AdoptionDay AS ad ON ad.DateKey = ld.DateKey AND ad.UserKey = ld.UserKey
    GROUP BY ld.DateKey
    OPTION (RECOMPILE);
    BEGIN TRANSACTION;
    DELETE FROM dash.HeldDay;
    INSERT INTO dash.HeldDay (DateKey, Held, HeldEnabled, AdoptedEnabled) SELECT DateKey, Held, HeldEnabled, AdoptedEnabled FROM #held;
    COMMIT TRANSACTION;

    BEGIN TRANSACTION;
    IF @itemWeekFrom IS NULL AND @mayTruncate = 1
    BEGIN
        ALTER INDEX NCCI_DashUserItemWeek ON dash.UserItemWeek DISABLE;   -- as dash.UserItemDay's, written whole
        TRUNCATE TABLE dash.UserItemWeek;
    END;
    ELSE IF @itemWeekFrom IS NULL DELETE FROM dash.UserItemWeek;
    ELSE DELETE FROM dash.UserItemWeek WHERE WeekEnding >= @itemWeekFrom;
    INSERT INTO dash.UserItemWeek WITH (TABLOCK) (WeekEnding, UserKey, Kind, Item, ActiveDays, Interactions, Prompts, Resources, Web)
    SELECT WeekEnding, UserKey, Kind, Item, ActiveDays, Interactions, Prompts, Resources, Web FROM #itemweek;
    IF @itemWeekFrom IS NULL AND @mayTruncate = 1
        ALTER INDEX NCCI_DashUserItemWeek ON dash.UserItemWeek REBUILD;
    COMMIT TRANSACTION;

    BEGIN TRANSACTION;
    IF @mayTruncate = 1 TRUNCATE TABLE dash.SitePerson; ELSE DELETE FROM dash.SitePerson;
    INSERT INTO dash.SitePerson WITH (TABLOCK) (Kind, SiteKey, UserKey, LastKey, PrevKey, AsOfKey)
    SELECT Kind, SiteKey, UserKey, LastKey, PrevKey, @maxKey FROM #siteperson;
    COMMIT TRANSACTION;

    /* Loaded in parallel, which a columnstore takes a large load fastest by. The threads of such a load have
       deadlocked on each other (dash.SitePerson's, when it was a columnstore): should SQL Server pick this load as the
       victim, it is loaded again on one thread. */
    DECLARE @periodTry INT = 0;
    WHILE 1 = 1
    BEGIN
        BEGIN TRY
            BEGIN TRANSACTION;
            IF @mayTruncate = 1 TRUNCATE TABLE dash.UserItemPeriod; ELSE DELETE FROM dash.UserItemPeriod;
            IF @periodTry = 0
                INSERT INTO dash.UserItemPeriod WITH (TABLOCK) (EndKey, Days, Kind, Item, UserKey, ActiveDays, LastActiveKey, Interactions, Prompts, Resources, Web)
                SELECT EndKey, Days, Kind, Item, UserKey, ActiveDays, LastActiveKey, Interactions, Prompts, Resources, Web FROM #itemperiod;
            ELSE
                INSERT INTO dash.UserItemPeriod WITH (TABLOCK) (EndKey, Days, Kind, Item, UserKey, ActiveDays, LastActiveKey, Interactions, Prompts, Resources, Web)
                SELECT EndKey, Days, Kind, Item, UserKey, ActiveDays, LastActiveKey, Interactions, Prompts, Resources, Web FROM #itemperiod
                OPTION (MAXDOP 1);
            COMMIT TRANSACTION;
            BREAK;
        END TRY
        BEGIN CATCH
            IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
            IF ERROR_NUMBER() <> 1205 OR @periodTry > 0 THROW;
            SET @periodTry = 1;
            RAISERROR('  The load of dash.UserItemPeriod was chosen as a deadlock victim: loading it again on one thread.', 0, 1) WITH NOWAIT;
        END CATCH;
    END;

    SET @periodTry = 0;
    WHILE 1 = 1
    BEGIN
        BEGIN TRY
            BEGIN TRANSACTION;
            IF @mayTruncate = 1 TRUNCATE TABLE dash.CreditUsePeriod; ELSE DELETE FROM dash.CreditUsePeriod;
            IF @periodTry = 0
                INSERT INTO dash.CreditUsePeriod WITH (TABLOCK) (EndKey, Days, UserKey, AgentKey, I, A, B, BW, BA, R, M, Lic, AnyBilled)
                SELECT EndKey, Days, UserKey, AgentKey, I, A, B, BW, BA, R, M, Lic, AnyBilled FROM #creditperiod;
            ELSE
                INSERT INTO dash.CreditUsePeriod WITH (TABLOCK) (EndKey, Days, UserKey, AgentKey, I, A, B, BW, BA, R, M, Lic, AnyBilled)
                SELECT EndKey, Days, UserKey, AgentKey, I, A, B, BW, BA, R, M, Lic, AnyBilled FROM #creditperiod
                OPTION (MAXDOP 1);
            COMMIT TRANSACTION;
            BREAK;
        END TRY
        BEGIN CATCH
            IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
            IF ERROR_NUMBER() <> 1205 OR @periodTry > 0 THROW;
            SET @periodTry = 1;
            RAISERROR('  The load of dash.CreditUsePeriod was chosen as a deadlock victim: loading it again on one thread.', 0, 1) WITH NOWAIT;
        END CATCH;
    END;

    /* ---- 4. The columnstores over what this run wrote ------------------------ */

    /* Rows written to a table with a columnstore beside it wait in a delta store, read row by row, until a million have
       gathered: the newest days, the ones the 7- to 30-day pages read. Compressing them is online and takes seconds,
       and is done before this run is recorded, so the pages built ahead after it read them compressed. It needs ALTER
       on the tables: a refresh run by an account without it leaves them for the next. */
    IF @mayTruncate = 1
    BEGIN
        ALTER INDEX NCCI_DashUserDay ON dash.UserDay REORGANIZE WITH (COMPRESS_ALL_ROW_GROUPS = ON);
        ALTER INDEX NCCI_DashResourceDay ON dash.ResourceDay REORGANIZE WITH (COMPRESS_ALL_ROW_GROUPS = ON);
        ALTER INDEX NCCI_DashUserWeek ON dash.UserWeek REORGANIZE WITH (COMPRESS_ALL_ROW_GROUPS = ON);
        ALTER INDEX NCCI_DashAdoptionDay ON dash.AdoptionDay REORGANIZE WITH (COMPRESS_ALL_ROW_GROUPS = ON);
        ALTER INDEX NCCI_DashUserItemDay ON dash.UserItemDay REORGANIZE WITH (COMPRESS_ALL_ROW_GROUPS = ON);
        ALTER INDEX NCCI_DashUserItemWeek ON dash.UserItemWeek REORGANIZE WITH (COMPRESS_ALL_ROW_GROUPS = ON);
        ALTER INDEX CCI_DashUserItemPeriod ON dash.UserItemPeriod REORGANIZE WITH (COMPRESS_ALL_ROW_GROUPS = ON);
        ALTER INDEX NCCI_DashCreditUseDay ON dash.CreditUseDay REORGANIZE WITH (COMPRESS_ALL_ROW_GROUPS = ON);
        ALTER INDEX CCI_DashCreditUsePeriod ON dash.CreditUsePeriod REORGANIZE WITH (COMPRESS_ALL_ROW_GROUPS = ON);
    END;

    INSERT INTO dash.RefreshRun (StartedUtc, EndedUtc, SinceUtc, FromDateKey, People, ClosureRows, UserDayRows, BiRunId)
    VALUES (@started, SYSUTCDATETIME(), @SinceUtc, @fromKey, @people, @closure, @dayRows, @biRun);

    SET @msg = CONCAT(N'Dashboard refreshed', CASE WHEN @fromKey IS NULL THEN N' (every day)' ELSE CONCAT(N' from ', @fromKey) END,
                      N': ', FORMAT(@people, 'N0'), N' people, ', FORMAT(@closure, 'N0'), N' org pairs, ',
                      FORMAT(@dayRows, 'N0'), N' person-days written.',
                      CASE WHEN @top IS NULL THEN N' No top of the organization: none is set, and no one person has half the directory under them, so nobody sees the whole organization by level.'
                           WHEN @detected = @top AND NOT EXISTS (SELECT 1 FROM dash.Setting WHERE Name = 'OrgTopUpn' AND NULLIF(LTRIM(RTRIM(Value)), N'') IS NOT NULL)
                                THEN CONCAT(N' Top of the organization, from the org chart: ', @top, N'.')
                           ELSE N'' END);
    RAISERROR(@msg, 0, 1) WITH NOWAIT;
END;
GO
