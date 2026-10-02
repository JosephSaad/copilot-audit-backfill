/*
    bi.usp_RefreshBi
    ------------------------------------------------------------------------
    Fills the bi star schema (Create-CopilotBiSchema.sql) from the audit
    tables. -Incremental calls it after each load; run it by hand after a
    backfill, or after changing a setting.

      @SinceUtc              Interactions from this UTC time are deleted and
                             rebuilt; older ones are left as they are. NULL
                             (the default) rebuilds them all. The first run
                             always rebuilds, whatever is passed.
      @ReportingTimeZone     The zone dates and hours are reported in. Use the
                             one the sessions were built with. Changing it
                             needs a full rebuild (@SinceUtc = NULL); a partial
                             refresh in a different zone is refused.
      @LicenseEventDatabase  One database holding the license events
                             (audit.vLicenseSkuChange), or several
                             comma-separated. The same value as
                             usp_BuildCopilotSessions takes.

    What it does, in order:

      1. License history: every SKU change from @LicenseEventDatabase, and
         from it, for each user, the periods they held a Copilot SKU.
      2. Sessions, directory profiles, and keys for any user, app or SKU
         they bring that has not been seen before.
      3. bi.DimDate, extended to cover the data.
      4. bi.FactInteraction: the window from @SinceUtc deleted, then written
         again from audit.RawLogFlattened a month at a time, adding keys for
         new users, apps, agents, models, license types and labels as it goes.
         bi.FactAccessedResource the same way, alongside, from
         audit.RawLogAccessedResource (2.2.0), with keys for new resource
         types, actions, sites and labels.
      5. Dimension attributes: directory profile, today's license, app
         category. Small; rewritten every run.
      6. bi.FactSession: only what the session builds since the last refresh
         replaced - every session after a full session build, else the days
         the incremental ones rebuilt (audit.CopilotSessionRun.RebuiltFrom).
         bi.FactLicenseChange and bi.FactLicensedDay: rebuilt whole; they are
         small next to the interactions.

    License at the time of use (FactInteraction.LicenseStatusKey,
    FactLicensedDay):

      * A user with Copilot SKU changes in the license history held a license
        at a moment if any Copilot SKU's latest change at or before it was an
        addition - or, before their first change, if that first change was a
        removal. The same rule as usp_BuildCopilotSessions.
      * A user with no change in the history falls back to the enrichment
        (audit.CopilotLicense): licensed from LicensedSinceUtc if they hold
        one today, else not licensed. Sessions use today's flag throughout
        instead, ignoring LicensedSinceUtc, so for such a user a session
        before that date can say Licensed where its interactions say not.
      * Neither: Unknown. So is a user the directory no longer has.

    A row's license status is fixed when the row is written. A license change
    that reaches the history later corrects only rows rebuilt after it; run
    with @SinceUtc = NULL (and fully refresh the model) to correct them all.

    Reads audit.RawLogFlattened, audit.CopilotLicense, audit.CopilotSession
    and audit.SessionAppCategory, so run Create-CopilotFlattenedSchema.sql
    and Create-CopilotSessionSchema.sql first (the session tables may be
    empty). audit.UserDirectory (on-premises AD, loaded by
    tools/Export-UsersWithHierarchy-ToSql.ps1) is optional: without it users
    have no directory attributes. Requires SQL Server 2017+.
*/
SET NOCOUNT ON;

IF OBJECT_ID(N'bi.usp_RefreshBi', N'P') IS NULL
    EXEC (N'CREATE PROCEDURE bi.usp_RefreshBi AS BEGIN SET NOCOUNT ON; END;');
GO

ALTER PROCEDURE bi.usp_RefreshBi
    @SinceUtc             DATETIME2(0)   = NULL,
    @ReportingTimeZone    SYSNAME        = NULL,
    @LicenseEventDatabase NVARCHAR(1000) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    SET @ReportingTimeZone = COALESCE(@ReportingTimeZone, audit.ReportingTimeZone());   -- the zone Install-CopilotAudit.ps1 -ReportingTimeZone stored

    DECLARE @started DATETIME2(0) = SYSUTCDATETIME(), @msg NVARCHAR(1000), @sql NVARCHAR(MAX);

    IF OBJECT_ID(N'bi.FactInteraction', N'U') IS NULL
        THROW 50010, N'The bi schema is missing - run sql/Create-CopilotBiSchema.sql first.', 1;
    IF COL_LENGTH(N'bi.DimUser', N'OrgDepth') IS NULL
        THROW 50016, N'bi.DimUser predates 2.0.0 - run sql/Create-CopilotBiSchema.sql again to upgrade it.', 1;
    IF COL_LENGTH(N'bi.RefreshRun', N'SessionRunId') IS NULL
        THROW 50017, N'bi.RefreshRun predates incremental sessions - run sql/Create-CopilotBiSchema.sql again to upgrade it.', 1;
    IF COL_LENGTH(N'audit.CopilotSessionRun', N'RebuiltFrom') IS NULL
        THROW 50018, N'audit.CopilotSessionRun predates incremental sessions - run sql/Create-CopilotSessionSchema.sql again.', 1;
    IF OBJECT_ID(N'audit.RawLogFlattened', N'U') IS NULL OR OBJECT_ID(N'audit.CopilotLicense', N'U') IS NULL
        THROW 50011, N'audit.RawLogFlattened is missing - run sql/Create-CopilotFlattenedSchema.sql and audit.usp_FlattenCopilotAudit first.', 1;
    IF OBJECT_ID(N'audit.CopilotSession', N'U') IS NULL OR OBJECT_ID(N'audit.SessionAppCategory', N'U') IS NULL
       OR OBJECT_ID(N'audit.CopilotSessionRun', N'U') IS NULL
        THROW 50012, N'audit.CopilotSession is missing - run sql/Create-CopilotSessionSchema.sql first (the tables may stay empty).', 1;
    IF NOT EXISTS (SELECT 1 FROM sys.time_zone_info WHERE name = @ReportingTimeZone)
        THROW 50013, N'@ReportingTimeZone is not a name in sys.time_zone_info.', 1;

    DECLARE @licDbs TABLE (Name SYSNAME NOT NULL PRIMARY KEY);
    IF @LicenseEventDatabase IS NOT NULL
        INSERT INTO @licDbs (Name)
        SELECT DISTINCT LTRIM(RTRIM(value)) FROM STRING_SPLIT(@LicenseEventDatabase, N',')
        WHERE LTRIM(RTRIM(value)) <> N'';
    IF EXISTS (SELECT 1 FROM @licDbs WHERE OBJECT_ID(QUOTENAME(Name) + N'.audit.vLicenseSkuChange', N'V') IS NULL)
        THROW 50014, N'A database in @LicenseEventDatabase has no audit.vLicenseSkuChange - run sql/Create-LicenseEventSchema.sql there.', 1;

    /* The first refresh builds everything, whatever window was asked for:
       an empty fact refreshed from yesterday would have nothing before it. */
    IF NOT EXISTS (SELECT 1 FROM bi.RefreshRun)
    BEGIN
        IF @SinceUtc IS NOT NULL
            RAISERROR('First refresh of the bi schema: building every interaction, not only the window asked for.', 0, 1) WITH NOWAIT;
        SET @SinceUtc = NULL;
    END;

    /* 2.4.9: a full rebuild that was stopped part way left months missing: this run finishes it. */
    IF @SinceUtc IS NOT NULL AND OBJECT_ID(N'bi.RebuildInProgress', N'U') IS NOT NULL AND EXISTS (SELECT 1 FROM bi.RebuildInProgress)
    BEGIN
        RAISERROR('A full rebuild of the bi schema did not finish: building every interaction, not only the window asked for.', 0, 1) WITH NOWAIT;
        SET @SinceUtc = NULL;
    END;

    /* Dates and hours already written were computed in the last run's zone;
       mixing zones inside one table would misplace every row near midnight. */
    DECLARE @lastZone SYSNAME = (SELECT TOP (1) ReportingTimeZone FROM bi.RefreshRun ORDER BY RunId DESC);
    IF @SinceUtc IS NOT NULL AND @lastZone <> @ReportingTimeZone
    BEGIN
        SET @msg = CONCAT(N'bi.FactInteraction was built in ', @lastZone, N'. Changing to ', @ReportingTimeZone,
                          N' needs a full rebuild: run with @SinceUtc = NULL.');
        THROW 50015, @msg, 1;
    END;

    DECLARE @sessionZone SYSNAME = (SELECT TOP (1) ReportingTimeZone FROM audit.CopilotSessionRun ORDER BY RunId DESC);
    IF @sessionZone IS NOT NULL AND @sessionZone <> @ReportingTimeZone
    BEGIN
        SET @msg = CONCAT(N'Warning: the sessions were built in ', @sessionZone, N', the interactions are being built in ',
                          @ReportingTimeZone, N'. Their dates will not line up near midnight.');
        RAISERROR(@msg, 0, 1) WITH NOWAIT;
    END;

    /* ---- 1. License history ---------------------------------------------- */

    /* Every SKU change, from every license database. The databases can
       overlap (each license backfill covers its own range), hence DISTINCT. */
    DROP TABLE IF EXISTS #chgAll;
    CREATE TABLE #chgAll
    (
        RecordId    VARCHAR(64)   COLLATE DATABASE_DEFAULT NOT NULL,
        ChangedUtc  DATETIME2(0)  NOT NULL,
        Upn         NVARCHAR(512) COLLATE DATABASE_DEFAULT NOT NULL,
        Sku         NVARCHAR(200) COLLATE DATABASE_DEFAULT NOT NULL,
        Added       BIT           NOT NULL,
        ActorUpn    NVARCHAR(512) COLLATE DATABASE_DEFAULT NULL
    );
    DECLARE @licDb SYSNAME;
    DECLARE licDbs CURSOR LOCAL FAST_FORWARD FOR SELECT Name FROM @licDbs;
    OPEN licDbs;
    FETCH NEXT FROM licDbs INTO @licDb;
    WHILE @@FETCH_STATUS = 0
    BEGIN
        SET @sql = N'INSERT INTO #chgAll (RecordId, ChangedUtc, Upn, Sku, Added, ActorUpn)
            SELECT c.RecordId, CAST(c.CreatedDateTime AS DATETIME2(0)), LEFT(c.TargetUpn, 512), LEFT(c.Sku, 200),
                   CASE WHEN c.Change = N''Added'' THEN 1 ELSE 0 END, LEFT(c.ActorUpn, 512)
            FROM ' + QUOTENAME(@licDb) + N'.audit.vLicenseSkuChange AS c
            WHERE c.TargetUpn IS NOT NULL AND c.Sku IS NOT NULL;';
        EXEC (@sql);
        FETCH NEXT FROM licDbs INTO @licDb;
    END;
    CLOSE licDbs;
    DEALLOCATE licDbs;

    DROP TABLE IF EXISTS #chg;
    SELECT DISTINCT RecordId, ChangedUtc, Upn, Sku, Added, ActorUpn INTO #chg FROM #chgAll;

    /* Today's license from the enrichment. SkuPartNumber, LicensedSinceUtc
       and DirectoryStatus arrive with Create-CopilotEnrichmentSchema.sql;
       a database without it has only the flag. */
    DROP TABLE IF EXISTS #cur;
    CREATE TABLE #cur
    (
        Upn              NVARCHAR(512) COLLATE DATABASE_DEFAULT NOT NULL PRIMARY KEY NONCLUSTERED,
        IsLic            BIT           NULL,
        Sku              NVARCHAR(100) COLLATE DATABASE_DEFAULT NULL,
        SinceUtc         DATETIME2(0)  NULL,
        DirectoryStatus  NVARCHAR(20)  COLLATE DATABASE_DEFAULT NULL
    );
    SET @sql = N'INSERT INTO #cur (Upn, IsLic, Sku, SinceUtc, DirectoryStatus)
        SELECT UserPrincipalName, IsCopilotLic, '
        + CASE WHEN COL_LENGTH(N'audit.CopilotLicense', N'SkuPartNumber')    IS NULL THEN N'NULL' ELSE N'SkuPartNumber'    END + N', '
        + CASE WHEN COL_LENGTH(N'audit.CopilotLicense', N'LicensedSinceUtc') IS NULL THEN N'NULL' ELSE N'LicensedSinceUtc' END + N', '
        + CASE WHEN COL_LENGTH(N'audit.CopilotLicense', N'DirectoryStatus')  IS NULL THEN N'NULL' ELSE N'DirectoryStatus'  END + N'
        FROM audit.CopilotLicense;';
    EXEC (@sql);

    /*
        The periods each user held a Copilot SKU, as [FromUtc, ToUtc).

        With history: per SKU, each change starts a period that lasts until
        the SKU's next change, held if the change was an addition; before the
        first change, held if that first change was a removal. Only the held
        periods are kept - a user in #known is licensed at a moment if a held
        period covers it, and not licensed otherwise.

        Without history, from the enrichment: licensed from LicensedSinceUtc
        (or throughout, if it is not known) when licensed today; never, when
        not. A user in neither is not in #known: Unknown.
    */
    DROP TABLE IF EXISTS #ev;
    SELECT DISTINCT Upn, Sku, ChangedUtc AS Ts, Added INTO #ev FROM #chg WHERE Sku LIKE N'%COPILOT%';

    DROP TABLE IF EXISTS #held;
    CREATE TABLE #held
    (
        Upn      NVARCHAR(512) COLLATE DATABASE_DEFAULT NOT NULL,
        FromUtc  DATETIME2(0)  NOT NULL,
        ToUtc    DATETIME2(0)  NOT NULL
    );
    INSERT INTO #held (Upn, FromUtc, ToUtc)
    SELECT q.Upn, q.FromUtc, q.ToUtc
    FROM (
        SELECT e.Upn, e.Ts AS FromUtc,
               LEAD(e.Ts, 1, CAST('99991231' AS DATETIME2(0))) OVER (PARTITION BY e.Upn, e.Sku ORDER BY e.Ts, e.Added) AS ToUtc,
               e.Added AS Held
        FROM #ev AS e
        UNION ALL
        SELECT f.Upn, CAST('00010101' AS DATETIME2(0)), f.Ts, 1 - CAST(f.Added AS INT)
        FROM (SELECT e.Upn, e.Ts, e.Added,
                     ROW_NUMBER() OVER (PARTITION BY e.Upn, e.Sku ORDER BY e.Ts, e.Added) AS rn
              FROM #ev AS e) AS f
        WHERE f.rn = 1
    ) AS q
    WHERE q.Held = 1 AND q.ToUtc > q.FromUtc;

    INSERT INTO #held (Upn, FromUtc, ToUtc)
    SELECT c.Upn, COALESCE(c.SinceUtc, CAST('00010101' AS DATETIME2(0))), CAST('99991231' AS DATETIME2(0))
    FROM #cur AS c
    WHERE c.IsLic = 1
      AND NOT EXISTS (SELECT 1 FROM #ev AS e WHERE e.Upn = c.Upn);

    /* Nonclustered: a UPN can be 1,024 bytes, over a clustered key's 900. */
    CREATE INDEX IX_held ON #held (Upn, FromUtc) INCLUDE (ToUtc);

    DROP TABLE IF EXISTS #known;
    CREATE TABLE #known (Upn NVARCHAR(512) COLLATE DATABASE_DEFAULT NOT NULL PRIMARY KEY NONCLUSTERED);
    INSERT INTO #known (Upn)
    SELECT Upn FROM #ev
    UNION
    SELECT Upn FROM #cur WHERE IsLic IS NOT NULL;

    /* ---- 2. Everything else the facts are built from ------------------------ */

    /*
        Which sessions changed since the last refresh. A full session build
        renumbers every SessionKey, so after one - or on a full refresh, or
        the first - bi.FactSession is rebuilt whole ('all'). After only
        incremental builds, just the days they rebuilt ('window', from the
        earliest RebuiltFrom); after none, nothing ('none').
    */
    DECLARE @sessRun INT = (SELECT MAX(RunId) FROM audit.CopilotSessionRun),
            @lastSessRun INT = (SELECT TOP (1) SessionRunId FROM bi.RefreshRun ORDER BY RunId DESC),
            @sessMode NVARCHAR(6), @sessFrom DATETIME2(0) = NULL;
    IF @SinceUtc IS NULL OR @lastSessRun IS NULL
       OR EXISTS (SELECT 1 FROM audit.CopilotSessionRun WHERE RunId > @lastSessRun AND RebuiltFrom IS NULL)
        SET @sessMode = N'all';
    ELSE IF @sessRun IS NULL OR @sessRun <= @lastSessRun
        SET @sessMode = N'none';
    ELSE
        SELECT @sessMode = N'window', @sessFrom = MIN(RebuiltFrom)
        FROM audit.CopilotSessionRun WHERE RunId > @lastSessRun;

    DROP TABLE IF EXISTS #sess;
    SELECT s.SessionKey, s.SessionStart_PromptSubmittedDateTime AS StartTs, s.SessionEnd_PromptSubmittedDateTime AS EndTs,
           s.UserUPN AS Upn, COALESCE(NULLIF(LTRIM(RTRIM(s.PrimaryApp)), N''), N'(unknown)') AS App,
           s.IsM365CopilotLicensed AS IsLic, s.PromptCount, s.InferredSessionSpanMinutes AS SpanMinutes
    INTO #sess
    FROM audit.CopilotSession AS s
    WHERE @sessMode = N'all' OR (@sessMode = N'window' AND s.SessionStart_PromptSubmittedDateTime >= @sessFrom)
    OPTION (RECOMPILE);

    /* Directory attributes from on-premises AD (audit.UserDirectory), when
       it has been loaded. Dynamic, so a database without it still compiles. */
    DROP TABLE IF EXISTS #profile;
    CREATE TABLE #profile
    (
        Upn             NVARCHAR(512) COLLATE DATABASE_DEFAULT NOT NULL PRIMARY KEY NONCLUSTERED,
        DisplayName     NVARCHAR(400) NULL, Division NVARCHAR(256) NULL, Department NVARCHAR(200) NULL,
        JobTitle        NVARCHAR(200) NULL, OfficeLocation NVARCHAR(200) NULL, City NVARCHAR(200) NULL,
        Country         NVARCHAR(200) NULL, CompanyName NVARCHAR(200) NULL, EmployeeType NVARCHAR(32) NULL,
        IsManager       BIT NULL, AccountEnabled BIT NULL, ManagerUpn NVARCHAR(512) NULL,
        ManagerName     NVARCHAR(256) NULL, OrgDepth TINYINT NULL,
        Mgr1 NVARCHAR(256) NULL, Mgr2 NVARCHAR(256) NULL, Mgr3 NVARCHAR(256) NULL, Mgr4 NVARCHAR(256) NULL, Mgr5 NVARCHAR(256) NULL, Mgr6 NVARCHAR(256) NULL, Mgr7 NVARCHAR(256) NULL, Mgr8 NVARCHAR(256) NULL, Mgr9 NVARCHAR(256) NULL, Mgr10 NVARCHAR(256) NULL, Mgr11 NVARCHAR(256) NULL, Mgr12 NVARCHAR(256) NULL, Mgr13 NVARCHAR(256) NULL, Mgr14 NVARCHAR(256) NULL, Mgr15 NVARCHAR(256) NULL
    );
    IF OBJECT_ID(N'audit.UserDirectory', N'U') IS NOT NULL
        EXEC (N'INSERT INTO #profile (Upn, DisplayName, Division, Department, JobTitle, OfficeLocation, City,
                                      Country, CompanyName, EmployeeType, IsManager, AccountEnabled, ManagerUpn,
                                      ManagerName, OrgDepth, Mgr1, Mgr2, Mgr3, Mgr4, Mgr5, Mgr6, Mgr7, Mgr8, Mgr9, Mgr10, Mgr11, Mgr12, Mgr13, Mgr14, Mgr15)
                SELECT UserPrincipalName, FullName, Division, Department, JobTitle, Office, City,
                       Country, Company, EmployeeType, IsManager, AccountEnabled, ManagerUPN,
                       ManagerName, OrgDepth, Mgr1, Mgr2, Mgr3, Mgr4, Mgr5, Mgr6, Mgr7, Mgr8, Mgr9, Mgr10, Mgr11, Mgr12, Mgr13, Mgr14, Mgr15
                FROM audit.UserDirectory;');

    /* Keys for users, apps and SKUs from those. The interactions add their
       own in step 4, a month at a time. */
    INSERT INTO bi.DimUser (UserPrincipalName)
    SELECT n.Upn
    FROM (SELECT Upn FROM #sess
          UNION SELECT Upn FROM #chg
          UNION SELECT Upn FROM #cur
          UNION SELECT Upn FROM #profile) AS n
    WHERE NOT EXISTS (SELECT 1 FROM bi.DimUser AS d WHERE d.UserPrincipalName = n.Upn);

    INSERT INTO bi.DimApp (App)
    SELECT n.App
    FROM (SELECT App FROM #sess
          UNION SELECT LEFT(App, 200) FROM audit.SessionAppCategory) AS n
    WHERE NOT EXISTS (SELECT 1 FROM bi.DimApp AS d WHERE d.App = n.App);

    INSERT INTO bi.DimSku (Sku, IsCopilot)
    SELECT DISTINCT c.Sku, CASE WHEN c.Sku LIKE N'%COPILOT%' THEN 1 ELSE 0 END FROM #chg AS c
    WHERE NOT EXISTS (SELECT 1 FROM bi.DimSku AS d WHERE d.Sku = c.Sku);

    /* ---- 3. Calendar ------------------------------------------------------- */
    /*
        From 1 January of the earliest year with data to 31 December of this
        year, in the reporting time zone. Dates are only ever added.
        1900-01-01 was a Monday and 1900-01-06 a Saturday, so the day and week
        arithmetic is right whatever DATEFIRST and the language say.
    */
    DECLARE @today DATE = CAST(SYSUTCDATETIME() AT TIME ZONE 'UTC' AT TIME ZONE @ReportingTimeZone AS DATE);
    DECLARE @firstUtc DATETIME2(0) = (SELECT MIN(v) FROM (VALUES
            ((SELECT MIN(UtcTimeStamp) FROM audit.RawLogFlattened)),
            ((SELECT MIN(ChangedUtc) FROM #chg))) AS x(v));
    DECLARE @firstDate DATE = COALESCE(CAST(CAST(@firstUtc AS DATETIME2(0)) AT TIME ZONE 'UTC' AT TIME ZONE @ReportingTimeZone AS DATE), @today);
    DECLARE @firstSession DATE = (SELECT CAST(MIN(SessionStart_PromptSubmittedDateTime) AS DATE) FROM audit.CopilotSession);
    IF @firstSession < @firstDate
        SET @firstDate = @firstSession;
    IF @firstDate > @today SET @firstDate = @today;
    DECLARE @d0 DATE = DATEFROMPARTS(YEAR(@firstDate), 1, 1), @d1 DATE = DATEFROMPARTS(YEAR(@today), 12, 31);

    WITH n AS (
        SELECT TOP (DATEDIFF(DAY, @d0, @d1) + 1)
               ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) - 1 AS i
        FROM sys.all_columns AS a CROSS JOIN sys.all_columns AS b
    )
    INSERT INTO bi.DimDate
        (DateKey, [Date], [Year], [Quarter], MonthNumber, [MonthName], YearMonth, DayOfWeekNumber, DayOfWeekName,
         IsWeekday, WeekEnding, WeekYear, WeekNumber, WeekLabel)
    SELECT YEAR(d.D) * 10000 + MONTH(d.D) * 100 + DAY(d.D), d.D, YEAR(d.D), DATEPART(QUARTER, d.D), MONTH(d.D),
           CHOOSE(MONTH(d.D), N'January', N'February', N'March', N'April', N'May', N'June', N'July',
                  N'August', N'September', N'October', N'November', N'December'),
           CONVERT(CHAR(7), d.D, 126),
           w.Dow,
           CHOOSE(w.Dow, N'Monday', N'Tuesday', N'Wednesday', N'Thursday', N'Friday', N'Saturday', N'Sunday'),
           CASE WHEN w.Dow <= 5 THEN 1 ELSE 0 END,
           w.WeekEnding, YEAR(w.WeekEnding), k.WeekNumber,
           CONCAT(YEAR(w.WeekEnding), N'-W', RIGHT(CONCAT(N'0', k.WeekNumber), 2))
    FROM n
    CROSS APPLY (SELECT DATEADD(DAY, n.i, @d0) AS D) AS d
    CROSS APPLY (SELECT DATEDIFF(DAY, '19000101', d.D) % 7 + 1 AS Dow,
                        DATEADD(DAY, 6 - DATEDIFF(DAY, '19000106', d.D) % 7, d.D) AS WeekEnding) AS w
    CROSS APPLY (SELECT DATEFROMPARTS(YEAR(w.WeekEnding), 1, 1) AS Jan1) AS j
    CROSS APPLY (SELECT DATEDIFF(DAY, DATEADD(DAY, 6 - DATEDIFF(DAY, '19000106', j.Jan1) % 7, j.Jan1), w.WeekEnding) / 7 + 1
                        AS WeekNumber) AS k
    WHERE NOT EXISTS (SELECT 1 FROM bi.DimDate AS x WHERE x.[Date] = d.D);

    /* ---- 4. Interactions --------------------------------------------------- */
    /*
        A month of audit.RawLogFlattened at a time: resolve its values once,
        add keys for anything new, write its fact rows. Month by month keeps
        tempdb and the log flat over tens of millions of rows.

        A partial refresh replaces its window in one transaction, so a model
        refreshing at the same time never sees it half-written. A full
        rebuild commits month by month instead; don't refresh the model while
        one runs.
    */
    DECLARE @interactions BIGINT = 0, @lo DATETIME2(0), @hi DATETIME2(0), @next DATETIME2(0);
    DECLARE @skipped BIGINT = (SELECT COUNT_BIG(*) FROM audit.RawLogFlattened WHERE UtcTimeStamp IS NULL);

    /* Two statements, not one with "@SinceUtc IS NULL OR ...": that form
       cannot seek the timestamp index, and scanned 40M rows every night. */
    IF @SinceUtc IS NULL
    BEGIN
        SELECT @lo = MIN(UtcTimeStamp), @hi = MAX(UtcTimeStamp) FROM audit.RawLogFlattened;
        IF @lo IS NOT NULL
            SET @lo = DATETIME2FROMPARTS(YEAR(@lo), MONTH(@lo), 1, 0, 0, 0, 0, 0);
    END
    ELSE
        SELECT @lo = @SinceUtc, @hi = MAX(UtcTimeStamp) FROM audit.RawLogFlattened WHERE UtcTimeStamp >= @SinceUtc;

    DECLARE @resources BIGINT = 0;
    DECLARE @hasResources BIT = CASE WHEN OBJECT_ID(N'audit.RawLogAccessedResource', N'U') IS NULL THEN 0 ELSE 1 END;

    IF @SinceUtc IS NOT NULL
    BEGIN
        BEGIN TRANSACTION;
        DELETE FROM bi.FactInteraction WHERE CreatedUtc >= @SinceUtc;
        DELETE FROM bi.FactAccessedResource WHERE CreatedUtc >= @SinceUtc;
    END
    ELSE
    BEGIN
        IF OBJECT_ID(N'bi.RebuildInProgress', N'U') IS NOT NULL AND NOT EXISTS (SELECT 1 FROM bi.RebuildInProgress)
            INSERT INTO bi.RebuildInProgress (StartedUtc) VALUES (SYSUTCDATETIME());
        TRUNCATE TABLE bi.FactInteraction;
        TRUNCATE TABLE bi.FactAccessedResource;
    END;

    WHILE @lo <= @hi
    BEGIN
        SET @next = DATEADD(MONTH, 1, @lo);

        DROP TABLE IF EXISTS #src;
        SELECT f.RecordId,
               f.UtcTimeStamp                                                                     AS Utc,
               COALESCE(NULLIF(LTRIM(RTRIM(LEFT(f.[User], 512))), N''), N'(unknown user)')          AS Upn,
               COALESCE(NULLIF(LTRIM(RTRIM(LEFT(f.App, 200))), N''), N'(unknown)')                 AS App,
               ag.AgentId, ag.AgentName, ag.AgentSource,
               COALESCE(NULLIF(LTRIM(RTRIM(f.ModelProvider)), N''), N'(not reported)')             AS ModelProvider,
               COALESCE(NULLIF(LTRIM(RTRIM(f.ModelName)), N''), N'(not reported)')                 AS ModelName,
               ISNULL(f.PromptCount, 0)                                                            AS PromptCount,
               ISNULL(f.AccessedResourceCount, 0)                                                  AS AccessedResourceCount,
               CAST(CASE WHEN f.AIPlugin LIKE N'%BingWebSearch%' THEN 1 ELSE 0 END AS BIT)           AS UsedWebSearch,
               CAST(CASE WHEN f.JailbreakDetected = N'true' THEN 1 ELSE 0 END AS BIT)                AS JailbreakDetected,
               CAST(CASE WHEN f.PromptInjectionDetected = N'true' THEN 1 ELSE 0 END AS BIT)          AS PromptInjectionDetected,
               CAST(CASE WHEN NULLIF(f.BlockedResourceUrls, N'') IS NOT NULL THEN 1 ELSE 0 END AS BIT) AS HasBlockedResource,
               CAST(CASE WHEN NULLIF(f.SensitivityLabelIds, N'') IS NOT NULL THEN 1 ELSE 0 END AS BIT) AS HasSensitivityLabel,
               COALESCE(NULLIF(LTRIM(RTRIM(f.CopilotEventData_LicenseType)), N''), N'(not reported)')  AS LicenseType,
               f.CopilotEventData_SensitivityLabelId                                               AS EventLabelId,
               ISNULL(f.MessageCount, 0)                                                           AS MessageCount,
               CAST(ISNULL(f.CopilotEventData_MemoryUpdated, 0) AS BIT)                             AS MemoryUpdated,
               CAST(CASE WHEN f.CopilotEventData_DLPEvaluationDeferred > 0 THEN 1 ELSE 0 END AS BIT)  AS DlpEvaluationDeferred,
               CAST(CASE WHEN NULLIF(f.CopilotEventData_TargetAgentName, N'') IS NOT NULL THEN 1 ELSE 0 END AS BIT) AS CalledAnotherAgent,
               NULLIF(LTRIM(RTRIM(LEFT(f.CopilotEventData_TargetAgentName, 400))), N'')              AS TargetAgentName,
               ai.Ident                                                                            AS Ident
        INTO #src
        FROM audit.RawLogFlattened AS f
        /* The agent: AgentId when the record has one, else the agent named
           in AppIdentity (see bi.DimAgent in Create-CopilotBiSchema.sql). */
        CROSS APPLY (SELECT NULLIF(LTRIM(RTRIM(f.AgentId)), N'')     AS Aid,
                            NULLIF(LTRIM(RTRIM(f.AppIdentity)), N'') AS Ident,
                            NULLIF(LTRIM(RTRIM(f.AgentName)), N'')   AS Aname) AS ai
        CROSS APPLY (SELECT CASE WHEN ai.Aid IS NOT NULL                   THEN N'Agent ID'
                                 WHEN ai.Ident LIKE N'Copilot.Studio.%'    THEN N'Copilot Studio'
                                 WHEN ai.Ident LIKE N'MicrosoftAgent.%'    THEN N'Microsoft'
                            END AS Src) AS asrc
        CROSS APPLY (SELECT COALESCE(ai.Aid, CASE WHEN asrc.Src IS NOT NULL THEN ai.Ident END, N'(none)') AS AgentId,
                            COALESCE(ai.Aname,
                                     /* the schema name after the tenant id, e.g. crd20_expenseClaimAutomation */
                                     CASE WHEN asrc.Src = N'Copilot Studio' AND CHARINDEX(N'-', REVERSE(ai.Ident)) > 1
                                          THEN RIGHT(ai.Ident, CHARINDEX(N'-', REVERSE(ai.Ident)) - 1) END,
                                     /* the last dotted part, e.g. BuiltIn_OutlookDraftsChatSteeringAgent */
                                     CASE WHEN asrc.Src = N'Microsoft' AND CHARINDEX(N'.', REVERSE(ai.Ident)) > 1
                                          THEN RIGHT(ai.Ident, CHARINDEX(N'.', REVERSE(ai.Ident)) - 1) END) AS AgentName,
                            COALESCE(asrc.Src, N'(no agent)') AS AgentSource) AS ag
        WHERE f.UtcTimeStamp >= @lo AND f.UtcTimeStamp < @next
        /* Planned per window: a plan cached from a month-sized full rebuild
           would scan the table for a two-day refresh. */
        OPTION (RECOMPILE);

        INSERT INTO bi.DimUser (UserPrincipalName)
        SELECT DISTINCT s.Upn FROM #src AS s
        WHERE NOT EXISTS (SELECT 1 FROM bi.DimUser AS d WHERE d.UserPrincipalName = s.Upn);

        INSERT INTO bi.DimApp (App)
        SELECT DISTINCT s.App FROM #src AS s
        WHERE NOT EXISTS (SELECT 1 FROM bi.DimApp AS d WHERE d.App = s.App);

        INSERT INTO bi.DimAgent (AgentId, AgentSource)
        SELECT s.AgentId, MIN(s.AgentSource) FROM #src AS s
        WHERE NOT EXISTS (SELECT 1 FROM bi.DimAgent AS d WHERE d.AgentId = s.AgentId)
        GROUP BY s.AgentId;

        INSERT INTO bi.DimModel (ModelProvider, ModelName)
        SELECT DISTINCT s.ModelProvider, s.ModelName FROM #src AS s
        WHERE NOT EXISTS (SELECT 1 FROM bi.DimModel AS d WHERE d.ModelProvider = s.ModelProvider AND d.ModelName = s.ModelName);

        INSERT INTO bi.DimLicenseType (LicenseType)
        SELECT DISTINCT s.LicenseType FROM #src AS s
        WHERE NOT EXISTS (SELECT 1 FROM bi.DimLicenseType AS d WHERE d.LicenseType = s.LicenseType);

        /* The accessed resources of this month's interactions, from the element table. */
        DROP TABLE IF EXISTS #rsrc;
        CREATE TABLE #rsrc (RecordId NVARCHAR(450) NOT NULL, Ordinal SMALLINT NOT NULL, ResourceType NVARCHAR(200) NOT NULL,
                            ResourceAction NVARCHAR(450) NOT NULL, Site NVARCHAR(400) NOT NULL, LabelId UNIQUEIDENTIFIER NULL,
                            IsBlocked BIT NOT NULL, XPIADetected BIT NOT NULL, FileType NVARCHAR(64) NOT NULL);
        IF @hasResources = 1
            INSERT INTO #rsrc
            SELECT r.RecordId, r.Ordinal,
                   LEFT(COALESCE(NULLIF(LTRIM(RTRIM(r.[Type])), N''), N'(not reported)'), 200),   -- each cut to the width it is stored in
                   LEFT(COALESCE(NULLIF(LTRIM(RTRIM(r.[Action])), N''), N'(not reported)'), 450),
                   LEFT(COALESCE(NULLIF(r.Site, N''), N'(none)'), 400),
                   r.SensitivityLabelId, r.IsBlocked, ISNULL(r.XPIADetected, 0),
                   /* 2.4.6: a file's extension, 1 to 8 letters and digits after the last dot of its name (not the first character); else its Type. */
                   CASE WHEN CHARINDEX(N'.', REVERSE(nm.N)) BETWEEN 2 AND 9 AND CHARINDEX(N'.', REVERSE(nm.N)) < LEN(nm.N)
                             AND RIGHT(nm.N, CHARINDEX(N'.', REVERSE(nm.N)) - 1) NOT LIKE N'%[^A-Za-z0-9]%'
                            THEN LOWER(RIGHT(nm.N, CHARINDEX(N'.', REVERSE(nm.N)) - 1))
                            ELSE LEFT(COALESCE(NULLIF(LTRIM(RTRIM(r.[Type])), N''), N'(not reported)'), 64) END
            FROM #src AS s
            JOIN audit.RawLogAccessedResource AS r ON r.RecordId = s.RecordId
            CROSS APPLY (SELECT RTRIM(r.Name) AS N) AS nm;

        /* 2.4.8: the interactions that read work content - a resource in SharePoint or OneDrive, Outlook or Teams,
           or a mail, a Teams message, a meeting or a connector item. The web pages a web search cites are listed as
           resources too, so "read a resource" alone does not say work content. Without the element table (a load
           from a CSV export), the count and the web search flag are all there is, as before 2.4.8. */
        DROP TABLE IF EXISTS #work;
        CREATE TABLE #work (RecordId NVARCHAR(450) COLLATE DATABASE_DEFAULT NOT NULL PRIMARY KEY);
        IF @hasResources = 1
            INSERT INTO #work (RecordId)
            SELECT DISTINCT r.RecordId FROM #rsrc AS r
            WHERE r.Site LIKE N'https://%.sharepoint.%' OR r.Site LIKE N'https://%.sharepoint-mil.%'
               OR r.Site LIKE N'https://outlook.office%' OR r.Site LIKE N'https://teams.microsoft.%'
               OR r.ResourceType IN (N'EmailMessage', N'TeamsMessage', N'Event', N'External');
        ELSE
            INSERT INTO #work (RecordId)
            SELECT s.RecordId FROM #src AS s WHERE s.AccessedResourceCount > 0 AND s.UsedWebSearch = 0;

        INSERT INTO bi.DimFileType (FileType)
        SELECT DISTINCT r.FileType FROM #rsrc AS r
        WHERE NOT EXISTS (SELECT 1 FROM bi.DimFileType AS d WHERE d.FileType = r.FileType);

        INSERT INTO bi.DimSensitivityLabel (LabelId)
        SELECT l.LabelId FROM (SELECT EventLabelId AS LabelId FROM #src UNION SELECT LabelId FROM #rsrc) AS l
        WHERE l.LabelId IS NOT NULL
          AND NOT EXISTS (SELECT 1 FROM bi.DimSensitivityLabel AS d WHERE d.LabelId = l.LabelId);

        INSERT INTO bi.DimResourceType (ResourceType)
        SELECT DISTINCT r.ResourceType FROM #rsrc AS r
        WHERE NOT EXISTS (SELECT 1 FROM bi.DimResourceType AS d WHERE d.ResourceType = r.ResourceType);

        INSERT INTO bi.DimResourceAction (ResourceAction)
        SELECT DISTINCT r.ResourceAction FROM #rsrc AS r
        WHERE NOT EXISTS (SELECT 1 FROM bi.DimResourceAction AS d WHERE d.ResourceAction = r.ResourceAction);

        INSERT INTO bi.DimSite (Site, Host, IsPersonal)
        SELECT x.Site,
               /* https://host */
               LEFT(x.Site, CASE WHEN CHARINDEX(N'/', x.Site + N'/', 9) > 0 THEN CHARINDEX(N'/', x.Site + N'/', 9) - 1 ELSE LEN(x.Site) END),
               CASE WHEN x.Site LIKE N'%/personal/%' THEN 1 ELSE 0 END
        FROM (SELECT DISTINCT Site FROM #rsrc) AS x
        WHERE NOT EXISTS (SELECT 1 FROM bi.DimSite AS d WHERE d.Site = x.Site);

        /* An agent keeps its key when renamed; the name is the latest seen.
           Months run in time order, so the last one to write wins. */
        UPDATE d
        SET AgentName = n.AgentName, AgentSource = n.AgentSource
        FROM bi.DimAgent AS d
        JOIN (SELECT AgentId, AgentName, AgentSource,
                     ROW_NUMBER() OVER (PARTITION BY AgentId ORDER BY Utc DESC, RecordId DESC) AS rn
              FROM #src WHERE AgentName IS NOT NULL) AS n
          ON n.AgentId = d.AgentId AND n.rn = 1;

        INSERT INTO bi.FactInteraction WITH (TABLOCK)
            (RecordId, CreatedUtc, ActivityHour, DateKey, HourOfDay, UserKey, AppKey, AgentKey, ModelKey, LicenseStatusKey,
             PromptCount, AccessedResourceCount, UsedWebSearch, JailbreakDetected, PromptInjectionDetected,
             HasBlockedResource, HasSensitivityLabel,
             LicenseTypeKey, SensitivityLabelKey, MessageCount, MemoryUpdated, DlpEvaluationDeferred, CalledAnotherAgent, TargetAgentName,
             PremiumUse, UseCaseKey, UseKind)
        SELECT s.RecordId, s.Utc,
               DATEADD(HOUR, DATEPART(HOUR, t.LocalTs), CAST(CAST(t.LocalTs AS DATE) AS DATETIME2(0))),
               YEAR(t.LocalTs) * 10000 + MONTH(t.LocalTs) * 100 + DAY(t.LocalTs),
               DATEPART(HOUR, t.LocalTs),
               u.UserKey, a.AppKey, g.AgentKey, m.ModelKey,
               ls.LicenseStatusKey,
               s.PromptCount, s.AccessedResourceCount, s.UsedWebSearch, s.JailbreakDetected,
               s.PromptInjectionDetected, s.HasBlockedResource, s.HasSensitivityLabel,
               lt.LicenseTypeKey, ISNULL(sl.SensitivityLabelKey, 1), s.MessageCount, s.MemoryUpdated,
               s.DlpEvaluationDeferred, s.CalledAnotherAgent, s.TargetAgentName,
               /* Premium use and the use-case key: defined here once (see bi.FactInteraction). */
               CASE WHEN ls.LicenseStatusKey = 1
                     AND (uk.UseKind = 1
                          OR (uk.UseKind IN (2, 3) AND a.AppCategory = N'In-App Copilot' AND a.App <> N'Copilot Studio')
                          OR (uk.UseKind = 3 AND a.AppCategory = N'Copilot Chat'))
                    THEN 1 ELSE 0 END,
               a.AppKey * 4 + CAST(s.UsedWebSearch AS INT) * 2 + CASE WHEN g.AgentId = N'(none)' THEN 0 ELSE 1 END,
               uk.UseKind
        FROM #src AS s
        CROSS APPLY (SELECT CAST(CAST(s.Utc AS DATETIME2(0)) AT TIME ZONE 'UTC' AT TIME ZONE @ReportingTimeZone AS DATETIME2(0)) AS LocalTs) AS t
        JOIN bi.DimUser  AS u ON u.UserPrincipalName = s.Upn
        JOIN bi.DimApp   AS a ON a.App = s.App
        JOIN bi.DimAgent AS g ON g.AgentId = s.AgentId
        JOIN bi.DimModel AS m ON m.ModelProvider = s.ModelProvider AND m.ModelName = s.ModelName
        JOIN bi.DimLicenseType AS lt ON lt.LicenseType = s.LicenseType
        LEFT JOIN bi.DimSensitivityLabel AS sl ON sl.LabelId = s.EventLabelId
        LEFT JOIN #known AS k ON k.Upn = s.Upn
        LEFT JOIN #work AS w ON w.RecordId = s.RecordId
        OUTER APPLY (SELECT TOP (1) 1 AS Held FROM #held AS x
                     WHERE x.Upn = s.Upn AND x.FromUtc <= s.Utc AND x.ToUtc > s.Utc) AS h
        /* 2.4.8: a record Copilot itself marked Premium was written under a license, whatever the dates say: the
           date Entra gives is when the current assignment began, which a reassignment moves forward. */
        CROSS APPLY (SELECT CAST(CASE WHEN s.LicenseType = N'Premium' THEN 1 WHEN k.Upn IS NULL THEN 0 WHEN h.Held = 1 THEN 1 ELSE 2 END AS TINYINT) AS LicenseStatusKey) AS ls
        CROSS APPLY (SELECT CAST(CASE WHEN s.Ident LIKE N'Copilot.Studio.%' THEN 0
                                      WHEN s.Ident LIKE N'Copilot.%.WebChat%' THEN CASE WHEN w.RecordId IS NOT NULL THEN 1 ELSE 0 END
                                      WHEN s.Ident LIKE N'Copilot.%.Bizchat%' OR s.Ident LIKE N'Copilot.%.Apps%' OR s.Ident LIKE N'MicrosoftAgent.%' THEN 1
                                      WHEN w.RecordId IS NOT NULL THEN 3 ELSE 2 END AS TINYINT) AS UseKind) AS uk
        ORDER BY s.Utc;
        SET @interactions += @@ROWCOUNT;

        INSERT INTO bi.FactAccessedResource WITH (TABLOCK)
            (RecordId, Ordinal, CreatedUtc, ActivityHour, DateKey, UserKey, AppKey,
             ResourceTypeKey, ResourceActionKey, SiteKey, SensitivityLabelKey, IsBlocked, XPIADetected, FileTypeKey)
        SELECT r.RecordId, r.Ordinal, s.Utc,
               DATEADD(HOUR, DATEPART(HOUR, t.LocalTs), CAST(CAST(t.LocalTs AS DATE) AS DATETIME2(0))),
               YEAR(t.LocalTs) * 10000 + MONTH(t.LocalTs) * 100 + DAY(t.LocalTs),
               u.UserKey, a.AppKey,
               rt.ResourceTypeKey, ra.ResourceActionKey, st.SiteKey, ISNULL(sl.SensitivityLabelKey, 1), r.IsBlocked, r.XPIADetected, ft.FileTypeKey
        FROM #rsrc AS r
        JOIN #src AS s ON s.RecordId = r.RecordId
        CROSS APPLY (SELECT CAST(CAST(s.Utc AS DATETIME2(0)) AT TIME ZONE 'UTC' AT TIME ZONE @ReportingTimeZone AS DATETIME2(0)) AS LocalTs) AS t
        JOIN bi.DimUser AS u ON u.UserPrincipalName = s.Upn
        JOIN bi.DimApp  AS a ON a.App = s.App
        JOIN bi.DimResourceType AS rt ON rt.ResourceType = r.ResourceType
        JOIN bi.DimResourceAction AS ra ON ra.ResourceAction = r.ResourceAction
        JOIN bi.DimSite AS st ON st.Site = r.Site
        JOIN bi.DimFileType AS ft ON ft.FileType = r.FileType
        LEFT JOIN bi.DimSensitivityLabel AS sl ON sl.LabelId = r.LabelId
        ORDER BY s.Utc;
        SET @resources += @@ROWCOUNT;

        SET @lo = @next;
    END;

    IF @SinceUtc IS NOT NULL
        COMMIT TRANSACTION;

    /* ---- 5. Dimension attributes ------------------------------------------ */

    UPDATE d
    SET DisplayName         = p.DisplayName,
        Division            = p.Division,
        Department          = p.Department,
        JobTitle            = p.JobTitle,
        OfficeLocation      = p.OfficeLocation,
        City                = p.City,
        Country             = p.Country,
        CompanyName         = p.CompanyName,
        EmployeeType        = p.EmployeeType,
        IsManager           = p.IsManager,
        AccountEnabled      = p.AccountEnabled,
        ManagerUpn          = p.ManagerUpn,
        ManagerName         = p.ManagerName,
        OrgDepth            = p.OrgDepth,
        OrgLevel1           = p.Mgr1,
        OrgLevel2           = p.Mgr2,
        OrgLevel3           = p.Mgr3,
        OrgLevel4           = p.Mgr4,
        OrgLevel5           = p.Mgr5,
        OrgLevel6           = p.Mgr6,
        OrgLevel7           = p.Mgr7,
        OrgLevel8           = p.Mgr8,
        OrgLevel9           = p.Mgr9,
        OrgLevel10          = p.Mgr10,
        OrgLevel11          = p.Mgr11,
        OrgLevel12          = p.Mgr12,
        OrgLevel13          = p.Mgr13,
        OrgLevel14          = p.Mgr14,
        OrgLevel15          = p.Mgr15,
        /* In AD: Found. Otherwise what the license lookup found in Entra. */
        DirectoryStatus     = COALESCE(CASE WHEN p.Upn IS NOT NULL THEN N'Found' END, c.DirectoryStatus, N'Not looked up'),
        CopilotLicenseToday = CASE c.IsLic WHEN 1 THEN N'Licensed' WHEN 0 THEN N'Not licensed' ELSE N'Unknown' END,
        CopilotSku          = c.Sku,
        LicensedSinceUtc    = c.SinceUtc
    FROM bi.DimUser AS d
    LEFT JOIN #profile AS p ON p.Upn = d.UserPrincipalName
    LEFT JOIN #cur     AS c ON c.Upn = d.UserPrincipalName;

    /* The same mapping the sessions use: unmapped apps are In-App Copilot. */
    DECLARE @recat TABLE (AppKey INT PRIMARY KEY);
    UPDATE d
    SET AppCategory = COALESCE(m.AppCategory, N'In-App Copilot')
    OUTPUT inserted.AppKey INTO @recat
    FROM bi.DimApp AS d
    LEFT JOIN audit.SessionAppCategory AS m ON m.App = d.App
    WHERE d.AppCategory <> COALESCE(m.AppCategory, N'In-App Copilot');

    /* Premium use depends on the category, so an app that changed category (or was new in this run
       and mapped to a category other than the default) has its stored flags set again, on every day,
       and the dashboard is told to roll up again from its first day. */
    DECLARE @recatFrom INT = NULL;
    IF EXISTS (SELECT 1 FROM @recat)
    BEGIN
        SET @recatFrom = (SELECT MIN(DateKey) FROM bi.FactInteraction WHERE AppKey IN (SELECT AppKey FROM @recat));
        UPDATE f
        SET PremiumUse = p.PremiumUse
        FROM bi.FactInteraction AS f
        JOIN bi.DimApp AS a ON a.AppKey = f.AppKey
        /* A row from before 2.4.8 has no UseKind: the category decides, as it did (the installer rebuilds bi once). */
        CROSS APPLY (SELECT ISNULL(f.UseKind, CASE WHEN f.AccessedResourceCount > 0 AND f.UsedWebSearch = 0 THEN 3 ELSE 2 END) AS UseKind) AS uk
        CROSS APPLY (SELECT CAST(CASE WHEN f.LicenseStatusKey = 1
                     AND (uk.UseKind = 1
                          OR (uk.UseKind IN (2, 3) AND a.AppCategory = N'In-App Copilot' AND a.App <> N'Copilot Studio')
                          OR (uk.UseKind = 3 AND a.AppCategory = N'Copilot Chat'))
                    THEN 1 ELSE 0 END AS BIT) AS PremiumUse) AS p
        WHERE f.AppKey IN (SELECT AppKey FROM @recat) AND f.PremiumUse <> p.PremiumUse;
    END;

    /* ---- 6. The smaller facts, rebuilt whole ------------------------------- */

    DECLARE @sessions INT, @changes INT, @days INT;

    BEGIN TRANSACTION;

    IF @sessMode = N'all'
        TRUNCATE TABLE bi.FactSession;
    ELSE IF @sessMode = N'window'
        DELETE FROM bi.FactSession WHERE SessionStart >= @sessFrom;
    INSERT INTO bi.FactSession
        (SessionKey, SessionStart, SessionEnd, DateKey, UserKey, AppKey, LicenseStatusKey, PromptCount, SpanMinutes, IsMultiTurn)
    SELECT s.SessionKey, s.StartTs, s.EndTs,
           YEAR(s.StartTs) * 10000 + MONTH(s.StartTs) * 100 + DAY(s.StartTs),
           u.UserKey, a.AppKey,
           CASE s.IsLic WHEN 1 THEN 1 WHEN 0 THEN 2 ELSE 0 END,
           s.PromptCount, s.SpanMinutes,
           CASE WHEN s.PromptCount > 1 THEN 1 ELSE 0 END
    FROM #sess AS s
    JOIN bi.DimUser AS u ON u.UserPrincipalName = s.Upn
    JOIN bi.DimApp  AS a ON a.App = s.App;
    SET @sessions = @@ROWCOUNT;

    TRUNCATE TABLE bi.FactLicenseChange;
    /* One row per record, SKU and direction, even if two license databases
       resolved the same record slightly differently. */
    INSERT INTO bi.FactLicenseChange (RecordId, ChangedUtc, DateKey, UserKey, SkuKey, Change, ChangedBy)
    SELECT r.RecordId, r.ChangedUtc,
           YEAR(t.LocalTs) * 10000 + MONTH(t.LocalTs) * 100 + DAY(t.LocalTs),
           r.UserKey, r.SkuKey, r.Change, r.ActorUpn
    FROM (SELECT c.RecordId, c.ChangedUtc, u.UserKey, k.SkuKey, c.ActorUpn,
                 CAST(CASE WHEN c.Added = 1 THEN 1 ELSE -1 END AS SMALLINT) AS Change,
                 ROW_NUMBER() OVER (PARTITION BY c.RecordId, k.SkuKey, c.Added ORDER BY c.ChangedUtc, u.UserKey) AS rn
          FROM #chg AS c
          JOIN bi.DimUser AS u ON u.UserPrincipalName = c.Upn
          JOIN bi.DimSku  AS k ON k.Sku = c.Sku) AS r
    CROSS APPLY (SELECT CAST(r.ChangedUtc AT TIME ZONE 'UTC' AT TIME ZONE @ReportingTimeZone AS DATETIME2(0)) AS LocalTs) AS t
    WHERE r.rn = 1;
    SET @changes = @@ROWCOUNT;

    /* Copilot Studio authoring events, from every license database that has them (2.3.0). */
    IF OBJECT_ID(N'bi.FactAgentEvent', N'U') IS NOT NULL
    BEGIN
        TRUNCATE TABLE bi.FactAgentEvent;
        DECLARE @agentDb SYSNAME, @agentSql NVARCHAR(MAX);
        DECLARE agentDbs CURSOR LOCAL FAST_FORWARD FOR SELECT Name FROM @licDbs;
        OPEN agentDbs;
        FETCH NEXT FROM agentDbs INTO @agentDb;
        WHILE @@FETCH_STATUS = 0
        BEGIN
            IF OBJECT_ID(QUOTENAME(@agentDb) + N'.audit.AgentEvent', N'U') IS NOT NULL
            BEGIN
                SET @agentSql = N'INSERT INTO bi.FactAgentEvent (RecordId, EventUtc, DateKey, Action, Operation, ActorUpn, ActorKey, BotId, BotSchemaName, ComponentName, ComponentType)
                    SELECT e.RecordId, e.CreatedUtc, YEAR(t.LocalTs) * 10000 + MONTH(t.LocalTs) * 100 + DAY(t.LocalTs), e.Action, e.Operation, e.ActorUpn,
                           u.UserKey, e.BotId, e.BotSchemaName, e.ComponentName, e.ComponentType
                    FROM ' + QUOTENAME(@agentDb) + N'.audit.AgentEvent AS e
                    CROSS APPLY (SELECT CAST(e.CreatedUtc AT TIME ZONE ''UTC'' AT TIME ZONE @z AS DATETIME2(0)) AS LocalTs) AS t
                    LEFT JOIN bi.DimUser AS u ON u.UserPrincipalName = e.ActorUpn
                    WHERE NOT EXISTS (SELECT 1 FROM bi.FactAgentEvent AS x WHERE x.RecordId = e.RecordId);';
                EXEC sys.sp_executesql @agentSql, N'@z SYSNAME', @z = @ReportingTimeZone;
            END;
            FETCH NEXT FROM agentDbs INTO @agentDb;
        END;
        CLOSE agentDbs;
        DEALLOCATE agentDbs;
    END;

    /* Licensed at the end of each day, from the first day with data to
       today: held at the instant before local midnight. */
    /*
        Held at the end of day d means held at the instant before local
        midnight: FromUtc before it and ToUtc at or after it - that is,
        d from FromUtc's local date to the day before ToUtc's. So each held
        period is turned into its range of DateKeys and seeks bi.DimDate,
        rather than every date being tested against every period.
        The ends are clamped first: '0001-01-01' and '9999-12-31' would
        overflow on conversion to a zone either side of UTC.
    */
    DECLARE @lowUtc  DATETIME2(0) = CAST(CAST(DATEADD(DAY, -1, @firstDate) AS DATETIME2(0)) AT TIME ZONE @ReportingTimeZone AT TIME ZONE 'UTC' AS DATETIME2(0)),
            @highUtc DATETIME2(0) = CAST(CAST(DATEADD(DAY,  2, @today)     AS DATETIME2(0)) AT TIME ZONE @ReportingTimeZone AT TIME ZONE 'UTC' AS DATETIME2(0)),
            @firstKey INT = YEAR(@firstDate) * 10000 + MONTH(@firstDate) * 100 + DAY(@firstDate),
            @todayKey INT = YEAR(@today) * 10000 + MONTH(@today) * 100 + DAY(@today);
    TRUNCATE TABLE bi.FactLicensedDay;
    INSERT INTO bi.FactLicensedDay (DateKey, UserKey)
    SELECT DISTINCT d.DateKey, u.UserKey
    FROM #held AS h
    CROSS APPLY (SELECT CASE WHEN h.FromUtc < @lowUtc  THEN @lowUtc  ELSE h.FromUtc END AS F,
                        CASE WHEN h.ToUtc   > @highUtc THEN @highUtc ELSE h.ToUtc   END AS T) AS c
    CROSS APPLY (SELECT CAST(CAST(c.F AT TIME ZONE 'UTC' AT TIME ZONE @ReportingTimeZone AS DATETIME2(0)) AS DATE) AS FirstDay,
                        DATEADD(DAY, -1, CAST(CAST(c.T AT TIME ZONE 'UTC' AT TIME ZONE @ReportingTimeZone AS DATETIME2(0)) AS DATE)) AS LastDay) AS r
    CROSS APPLY (SELECT YEAR(r.FirstDay) * 10000 + MONTH(r.FirstDay) * 100 + DAY(r.FirstDay) AS FirstKey,
                        YEAR(r.LastDay)  * 10000 + MONTH(r.LastDay)  * 100 + DAY(r.LastDay)  AS LastKey) AS k
    JOIN bi.DimDate AS d ON d.DateKey BETWEEN k.FirstKey AND k.LastKey
                        AND d.DateKey BETWEEN @firstKey AND @todayKey
    JOIN bi.DimUser AS u ON u.UserPrincipalName = h.Upn
    WHERE c.T > c.F;
    SET @days = @@ROWCOUNT;

    INSERT INTO bi.RefreshRun
        (StartedUtc, EndedUtc, SinceUtc, ReportingTimeZone, LicenseEventDatabase, InteractionRows, InteractionsSkipped,
         SessionRows, LicenseChangeRows, LicensedDayRows, SessionRunId, AccessedResourceRows, RecategorizedFromDateKey)
    VALUES (@started, SYSUTCDATETIME(), @SinceUtc, @ReportingTimeZone, @LicenseEventDatabase, @interactions, @skipped,
            @sessions, @changes, @days, @sessRun, @resources, @recatFrom);
    IF OBJECT_ID(N'bi.RebuildInProgress', N'U') IS NOT NULL AND @SinceUtc IS NULL
        DELETE FROM bi.RebuildInProgress;

    COMMIT TRANSACTION;

    SET @msg = CONCAT(N'bi refreshed', CASE WHEN @SinceUtc IS NULL THEN N' (full rebuild)'
                                           ELSE CONCAT(N' from ', CONVERT(NVARCHAR(16), @SinceUtc, 120), N'Z') END,
                      N': ', FORMAT(@interactions, 'N0'), N' interaction(s), ', FORMAT(@resources, 'N0'), N' accessed resource(s), ',
                      FORMAT(@sessions, 'N0'), N' session(s), ',
                      FORMAT(@changes, 'N0'), N' license change(s), ', FORMAT(@days, 'N0'), N' licensed user-day(s).');
    RAISERROR(@msg, 0, 1) WITH NOWAIT;
    IF @skipped > 0
    BEGIN
        SET @msg = CONCAT(N'  ', FORMAT(@skipped, 'N0'), N' flattened row(s) have no timestamp and are not in bi.FactInteraction.');
        RAISERROR(@msg, 0, 1) WITH NOWAIT;
    END;
    IF @LicenseEventDatabase IS NULL
        RAISERROR('  No @LicenseEventDatabase: license at the time of use comes from the enrichment''s LicensedSinceUtc only.', 0, 1) WITH NOWAIT;
END;
GO
