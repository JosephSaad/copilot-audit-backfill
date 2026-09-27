/*
    dash.usp_RefreshDashboard
    ------------------------------------------------------------------------
    Rebuilds what the Copilot Insights dashboard reads besides the bi schema:

      1. dash.Person and dash.OrgClosure, whole, from audit.UserDirectory
         (on-premises AD) - so the security model follows today's
         organisation. Small: one row per person, and one per (manager,
         person under them) pair.
      2. dash.UserDay from the bi facts, and dash.ResourceDay (labels and
         sites) from bi.FactAccessedResource, for the days from
         @SinceUtc's day on (every day when NULL), replaced in one transaction.
      3. dash.AgentUse, whole: when each agent was first and last used.

    Invoke-CopilotAuditBackfill.ps1 -Incremental and the CSV importer call it
    last, after bi.usp_RefreshBi, with the same @SinceUtc.

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
                       ManagerUpn NVARCHAR(256) COLLATE DATABASE_DEFAULT NULL);
    IF OBJECT_ID(N'audit.UserDirectory', N'U') IS NOT NULL
        EXEC (N'INSERT INTO #dir (Upn, ManagerUpn)
                SELECT UserPrincipalName, NULLIF(NULLIF(ManagerUPN, N''''), UserPrincipalName) FROM audit.UserDirectory;');

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
        VALUES (s.Name, s.Value, N'The top of the organisation found in the org chart, used while OrgTopUpn is empty. Written by each refresh.', N'dash.usp_RefreshDashboard');
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

    BEGIN TRANSACTION;
    DELETE FROM dash.OrgClosure;
    INSERT INTO dash.OrgClosure (AncestorUpn, DescendantUpn, Depth)
    SELECT AncestorUpn, DescendantUpn, Depth FROM #clo;

    DELETE FROM dash.Person;
    INSERT INTO dash.Person (Upn, DisplayName, ManagerUpn, Department, AccountEnabled, Sid, Level, DirectReports, PeopleBelow)
    SELECT p.Upn, p.DisplayName, p.ManagerUpn, p.Department, p.AccountEnabled, p.Sid,
           CASE WHEN @top IS NULL THEN NULL ELSE t.Depth + 1 END,
           (SELECT COUNT(*) FROM #clo AS c WHERE c.AncestorUpn = p.Upn AND c.Depth = 1),
           (SELECT COUNT(*) FROM #clo AS c WHERE c.AncestorUpn = p.Upn AND c.Depth >= 1)
    FROM #person AS p
    LEFT JOIN #clo AS t ON t.AncestorUpn = @top AND t.DescendantUpn = p.Upn;
    COMMIT TRANSACTION;

    DECLARE @people INT = (SELECT COUNT(*) FROM dash.Person), @closure INT = (SELECT COUNT(*) FROM dash.OrgClosure);

    /* ---- 2. The daily rollup ------------------------------------------------- */

    DECLARE @fromKey INT = NULL;
    IF @SinceUtc IS NOT NULL
    BEGIN
        /* The bi facts are dated in the reporting zone of the last bi refresh. */
        DECLARE @zone SYSNAME = (SELECT TOP (1) ReportingTimeZone FROM bi.RefreshRun ORDER BY RunId DESC);
        DECLARE @localDay DATE = CAST(CAST(@SinceUtc AS DATETIMEOFFSET) AT TIME ZONE ISNULL(@zone, N'UTC') AS DATE);
        SET @fromKey = YEAR(@localDay) * 10000 + MONTH(@localDay) * 100 + DAY(@localDay);
    END;

    /* A bi refresh since the last rollup that corrected Premium use on earlier days (an app changed
       category) moves the start back to the first of them. */
    DECLARE @biRun INT = (SELECT MAX(RunId) FROM bi.RefreshRun);
    IF @fromKey IS NOT NULL
    BEGIN
        DECLARE @premFrom INT = (SELECT MIN(b.PremiumFromDateKey) FROM bi.RefreshRun AS b
                                 WHERE b.RunId > ISNULL((SELECT TOP (1) r.BiRunId FROM dash.RefreshRun AS r WHERE r.BiRunId IS NOT NULL ORDER BY r.RunId DESC), 0));
        IF @premFrom < @fromKey SET @fromKey = @premFrom;
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
               SUM(CAST(f.HasSensitivityLabel AS INT))                               AS LabelledContent
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
           ISNULL(i.BlockedResource, 0) AS BlockedResource, ISNULL(i.LabelledContent, 0) AS LabelledContent,
           ISNULL(s.Sessions, 0) AS Sessions, ISNULL(s.MultiTurnSessions, 0) AS MultiTurnSessions
    INTO #day
    FROM i
    FULL JOIN s ON s.DateKey = i.DateKey AND s.UserKey = i.UserKey
    OPTION (RECOMPILE);

    /* Labels and sites, from bi.FactAccessedResource: the label on each resource
       read, the site of labelled content, and the site of content Copilot was
       blocked from. Sites are already cut to https://host/sites/name there. */
    DROP TABLE IF EXISTS #res;
    WITH r AS
    (
        SELECT f.DateKey, f.UserKey, f.IsBlocked, l.LabelId, s.Site
        FROM bi.FactAccessedResource AS f
        JOIN bi.DimSensitivityLabel AS l ON l.SensitivityLabelKey = f.SensitivityLabelKey
        JOIN bi.DimSite AS s ON s.SiteKey = f.SiteKey
        WHERE (@fromKey IS NULL OR f.DateKey >= @fromKey)
          AND (l.LabelId IS NOT NULL OR f.IsBlocked = 1)
    ),
    items AS
    (
        SELECT DateKey, UserKey, 'Label' AS Kind, LOWER(CAST(LabelId AS NVARCHAR(36))) AS Item FROM r WHERE LabelId IS NOT NULL
        UNION ALL
        SELECT DateKey, UserKey, 'Site', Site FROM r WHERE LabelId IS NOT NULL AND Site <> N'(none)'
        UNION ALL
        SELECT DateKey, UserKey, 'Blocked', Site FROM r WHERE IsBlocked = 1 AND Site <> N'(none)'
    )
    SELECT DateKey, UserKey, Kind, CAST(LEFT(Item, 400) AS NVARCHAR(400)) AS Item, COUNT(*) AS Interactions
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
         LicensedUse, PremiumUse, Jailbreak, PromptInjection, BlockedResource, LabelledContent, Sessions, MultiTurnSessions)
    SELECT DateKey, UserKey, Interactions, Prompts, AppsUsed, AgentInteractions, WebGrounded, ChatInteractions,
           LicensedUse, PremiumUse, Jailbreak, PromptInjection, BlockedResource, LabelledContent, Sessions, MultiTurnSessions
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
    DELETE FROM dash.AgentUse;
    INSERT INTO dash.AgentUse (AgentKey, FirstDateKey, LastDateKey, Interactions)
    SELECT AgentKey, MIN(DateKey), MAX(DateKey), COUNT_BIG(*) FROM bi.FactInteraction GROUP BY AgentKey;
    COMMIT TRANSACTION;

    INSERT INTO dash.RefreshRun (StartedUtc, EndedUtc, SinceUtc, FromDateKey, People, ClosureRows, UserDayRows, BiRunId)
    VALUES (@started, SYSUTCDATETIME(), @SinceUtc, @fromKey, @people, @closure, @dayRows, @biRun);

    SET @msg = CONCAT(N'Dashboard refreshed', CASE WHEN @fromKey IS NULL THEN N' (every day)' ELSE CONCAT(N' from ', @fromKey) END,
                      N': ', FORMAT(@people, 'N0'), N' people, ', FORMAT(@closure, 'N0'), N' org pairs, ',
                      FORMAT(@dayRows, 'N0'), N' person-days written.',
                      CASE WHEN @top IS NULL THEN N' No top of the organisation: none is set, and no one person has half the directory under them, so nobody sees the whole organisation by level.'
                           WHEN @detected = @top AND NOT EXISTS (SELECT 1 FROM dash.Setting WHERE Name = 'OrgTopUpn' AND NULLIF(LTRIM(RTRIM(Value)), N'') IS NOT NULL)
                                THEN CONCAT(N' Top of the organisation, from the org chart: ', @top, N'.')
                           ELSE N'' END);
    RAISERROR(@msg, 0, 1) WITH NOWAIT;
END;
GO
