/*
    audit.usp_BuildCopilotSessions
    ------------------------------------------------------------------------
    Rebuilds audit.CopilotSession and audit.CopilotWeeklyRollup from
    audit.RawLogFlattened, with the session script's rules:

      * A session belongs to one user - and, under the script's own rule, to
        one app context. Which applies is @ContextFallback (see below); every
        row it skips is counted.
      * The next prompt continues the session unless it comes 60 minutes or
        more after the previous one, or on a different calendar day in the
        reporting time zone. Either starts a new session.
      * A session's PrimaryApp and license flag are those of its first prompt;
        with @LicenseEventDatabase the flag is the license held at that moment.
        PromptCount is its number of rows. Single-Turn is one prompt.
      * Sessions roll up by the week their LAST prompt falls in. Weeks run
        Saturday to Friday and are labeled by the Friday; week 1 of a year
        ends on its first Friday.

    Where the port differs from the script, deliberately:

      * Rows are taken in time order. The script read them in file order and
        also started a new session on a timestamp earlier than the last one;
        in time order that cannot happen, so the rule has nothing to act on.
      * The script's metric version mentions 12h, but no 12-hour rule appears
        in its session logic, so none is applied here.
      * It is set-based over the table, in one transaction, rather than a
        restart-safe pass over files carrying open-session state. Readers
        never see a half-built table.

    @SinceUtc - incremental builds
      NULL (default) rebuilds every session and week. A time rebuilds only
      from local midnight of the day it falls on (in @ReportingTimeZone):
      a session never crosses midnight, so nothing before that day can
      change. The sessions from that midnight on are replaced, numbering
      continues from each stream's last kept session, and every week that
      contains a rebuilt day is recomputed from audit.CopilotSession. The
      result is the same as a full rebuild over the same rows - except that
      a kept session keeps the Division, Department, JobTitle, ManagerUPN and
      license flag it was built with, rather than being restated with today's.
      -Incremental passes the load's start. At 40M interactions a full build
      took 28 minutes and 24 GB of tempdb, growing with the history kept;
      an incremental one costs what the window costs.

      An incremental build is refused when the last build used different
      settings (timeout, fallback, time zone, metric version or license
      databases): the kept sessions would follow other rules. Build fully
      once after changing one. With no earlier build it builds everything.

    @ContextFallback
      'User'      (default) one session stream per user across all contexts;
                  AppContext is recorded as '(user)' and no row is skipped for
                  want of one
      'Location'  the script's rule: App context, else Location, else skip
      'None'      App context only; a row without one is skipped

    The default is not the script's rule, on evidence. The Graph data leaves
    App context empty on almost every row, and Location is only an
    environment tag ('prd') on agent traffic. On a live tenant the script's
    rule skipped every Copilot Chat, Outlook and Teams row - 507 of 1,145 - and
    kept almost nothing but agents. 'Location' reproduces the script exactly,
    for comparing against its CSVs; audit.CopilotSessionRun records which
    rule each build used.

    Requires SQL Server 2017+. Run sql/Create-CopilotSessionSchema.sql first,
    and audit.usp_FlattenCopilotAudit to fill audit.RawLogFlattened.
*/
SET NOCOUNT ON;

IF OBJECT_ID(N'audit.usp_BuildCopilotSessions', N'P') IS NULL
    EXEC (N'CREATE PROCEDURE audit.usp_BuildCopilotSessions AS BEGIN SET NOCOUNT ON; END;');
GO

ALTER PROCEDURE audit.usp_BuildCopilotSessions
    @SessionTimeoutMinutes INT           = 60,
    @ReportingTimeZone     SYSNAME       = NULL,
    @ContextFallback       NVARCHAR(20)  = N'User',
    @MetricVersion         NVARCHAR(200) = N'sql-1.0-60m-daysplit-satfri-customweek',
    @LicenseEventDatabase  NVARCHAR(1000) = NULL,  -- one database, or several comma-separated
    @SinceUtc              DATETIME2(0)   = NULL   -- NULL: everything; else from local midnight of that day
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    SET @ReportingTimeZone = COALESCE(@ReportingTimeZone, audit.ReportingTimeZone());   -- the zone Install-CopilotAudit.ps1 -ReportingTimeZone stored

    IF @ContextFallback NOT IN (N'Location', N'None', N'User')
        THROW 50001, N'@ContextFallback must be Location, None or User.', 1;
    IF @SessionTimeoutMinutes IS NULL OR @SessionTimeoutMinutes < 1
        THROW 50002, N'@SessionTimeoutMinutes must be at least 1.', 1;
    IF NOT EXISTS (SELECT 1 FROM sys.time_zone_info WHERE name = @ReportingTimeZone)
        THROW 50003, N'@ReportingTimeZone is not a name in sys.time_zone_info.', 1;
    DECLARE @licDbs TABLE (Name SYSNAME NOT NULL PRIMARY KEY);
    IF @LicenseEventDatabase IS NOT NULL
        INSERT INTO @licDbs (Name)
        SELECT DISTINCT LTRIM(RTRIM(value)) FROM STRING_SPLIT(@LicenseEventDatabase, N',')
        WHERE LTRIM(RTRIM(value)) <> N'';
    IF EXISTS (SELECT 1 FROM @licDbs WHERE OBJECT_ID(QUOTENAME(Name) + N'.audit.vLicenseSkuChange', N'V') IS NULL)
        THROW 50004, N'A database in @LicenseEventDatabase has no audit.vLicenseSkuChange - run sql/Create-LicenseEventSchema.sql there.', 1;
    /* The list as the run log keeps it and the check below compares it (2.4.12):
       each database once, trimmed, in order. The raw text was compared, so
       'A,B' after 'B,A' or 'A, B' - the same databases - counted as different
       settings and every incremental build after was refused. */
    SET @LicenseEventDatabase = (SELECT STRING_AGG(Name, N',') WITHIN GROUP (ORDER BY Name) FROM @licDbs);

    DECLARE @started DATETIME2(0) = SYSUTCDATETIME(), @msg NVARCHAR(600);

    IF COL_LENGTH(N'audit.CopilotSessionRun', N'RebuiltFrom') IS NULL
        THROW 50005, N'audit.CopilotSessionRun predates incremental builds - run sql/Create-CopilotSessionSchema.sql again.', 1;

    /* Incremental: from local midnight of @SinceUtc's day, if the last build
       followed the same rules. @rebuiltFrom is local, @fromUtc the same
       instant in UTC for the index on audit.RawLogFlattened. */
    DECLARE @rebuiltFrom DATETIME2(0) = NULL, @fromUtc DATETIME2(0) = NULL;
    IF @SinceUtc IS NOT NULL
    BEGIN
        DECLARE @lastRun INT, @lastTimeout INT, @lastFallback NVARCHAR(20), @lastZone NVARCHAR(128),
                @lastMetric NVARCHAR(200), @lastLic NVARCHAR(1000);
        SELECT TOP (1) @lastRun = RunId, @lastTimeout = SessionTimeoutMinutes, @lastFallback = ContextFallback,
                       @lastZone = ReportingTimeZone, @lastMetric = MetricVersion, @lastLic = LicenseEventDatabase
        FROM audit.CopilotSessionRun ORDER BY RunId DESC;
        /* A build before 2.4.12 logged the list as it was given. */
        SET @lastLic = (SELECT STRING_AGG(x.Name, N',') WITHIN GROUP (ORDER BY x.Name)
                        FROM (SELECT DISTINCT CAST(LTRIM(RTRIM(value)) AS SYSNAME) AS Name FROM STRING_SPLIT(@lastLic, N',')
                              WHERE LTRIM(RTRIM(value)) <> N'') AS x);

        IF @lastRun IS NULL
        BEGIN
            RAISERROR('No earlier session build: building every session, not only the days asked for.', 0, 1) WITH NOWAIT;
            SET @SinceUtc = NULL;
        END
        ELSE IF @lastTimeout <> @SessionTimeoutMinutes OR @lastFallback <> @ContextFallback
             OR @lastZone <> @ReportingTimeZone OR @lastMetric <> @MetricVersion
             OR ISNULL(@lastLic, N'') <> ISNULL(@LicenseEventDatabase, N'')
            THROW 50006, N'The last session build used different settings, and an incremental build would keep sessions built under them. Run once with @SinceUtc = NULL to rebuild everything.', 1;
        ELSE
        BEGIN
            SET @rebuiltFrom = CAST(CAST(CAST(CAST(@SinceUtc AS DATETIME2(0)) AT TIME ZONE 'UTC'
                                              AT TIME ZONE @ReportingTimeZone AS DATETIME2(0)) AS DATE) AS DATETIME2(0));
            SET @fromUtc = CAST(@rebuiltFrom AT TIME ZONE @ReportingTimeZone AT TIME ZONE 'UTC' AS DATETIME2(0));
        END;
    END;

    /* ---- 1. The rows, with the session context resolved ------------------ */

    DROP TABLE IF EXISTS #src;
    SELECT v.RecordId, v.UserUPN, v.TsUtc, v.Ts, v.App, v.Ctx, v.Loc, v.IsCopilotLic, k.KeyCtx,
           CAST(CASE WHEN @ContextFallback = N'Location' AND v.Ctx IS NULL AND v.Loc IS NOT NULL THEN 1 ELSE 0 END AS BIT) AS Recovered,
           /* The script's order of checks: context, then user, then timestamp. */
           CAST(CASE WHEN k.KeyCtx IS NULL  THEN 1
                     WHEN v.UserUPN IS NULL THEN 2
                     WHEN v.Ts IS NULL      THEN 3
                     ELSE 0 END AS TINYINT) AS Skip
    INTO #src
    FROM audit.RawLogFlattened AS f
    /* RecordId is NVARCHAR(900) in the flattened table, and a window ORDER BY
       key is limited to 900 bytes; audit record ids are GUIDs. */
    CROSS APPLY (SELECT CAST(f.RecordId AS VARCHAR(64))                                     AS RecordId,
                        NULLIF(LTRIM(RTRIM(f.[User])), N'')                                AS UserUPN,
                        CAST(f.UtcTimeStamp AS DATETIME2(0))                               AS TsUtc,
                        CAST(CAST(f.UtcTimeStamp AS DATETIME2(0)) AT TIME ZONE 'UTC'
                             AT TIME ZONE @ReportingTimeZone AS DATETIME2(0))               AS Ts,
                        NULLIF(LTRIM(RTRIM(f.App)), N'')                                   AS App,
                        NULLIF(LTRIM(RTRIM(CAST(f.[App context] AS NVARCHAR(4000)))), N'') AS Ctx,
                        NULLIF(LTRIM(RTRIM(CAST(f.Location AS NVARCHAR(4000)))), N'')      AS Loc,
                        f.IsCopilotLic) AS v
    CROSS APPLY (SELECT CASE @ContextFallback
                            WHEN N'Location' THEN COALESCE(v.Ctx, v.Loc)
                            WHEN N'None'     THEN v.Ctx
                            ELSE N'(user)' END AS KeyCtx) AS k
    WHERE @fromUtc IS NULL OR f.UtcTimeStamp >= @fromUtc
    OPTION (RECOMPILE);   -- planned for the window actually asked for, so a small one seeks

    DECLARE @read INT, @orig INT, @recovered INT, @noCtx INT, @noUser INT, @badTs INT;
    SELECT @read      = COUNT(*),
           @orig      = SUM(CASE WHEN Skip = 0 AND Ctx IS NOT NULL THEN 1 ELSE 0 END),
           @recovered = SUM(CASE WHEN Skip = 0 AND Recovered = 1 THEN 1 ELSE 0 END),
           @noCtx     = SUM(CASE WHEN Skip = 1 THEN 1 ELSE 0 END),
           @noUser    = SUM(CASE WHEN Skip = 2 THEN 1 ELSE 0 END),
           @badTs     = SUM(CASE WHEN Skip = 3 THEN 1 ELSE 0 END)
    FROM #src;

    /* ---- 2. Session boundaries ------------------------------------------- */
    /*
        Order and the gap are taken in UTC, the calendar day in the reporting
        zone (2.4.12). Both were local times, which a daylight-saving change
        moves: in the repeated autumn hour two prompts 110 minutes apart read
        as 50 and a later prompt sorted before an earlier one (taking the
        session's start and primary app), and in spring a 10-minute gap read
        as 70. Only sessions around a change come out differently; the local
        day still never goes backwards in UTC order.
    */

    DROP TABLE IF EXISTS #seq;
    SELECT l.RecordId, l.UserUPN, l.KeyCtx, l.TsUtc, l.Ts, l.App, l.IsCopilotLic,
           CASE WHEN l.PrevUtc IS NULL                                                     THEN 1
                WHEN DATEDIFF_BIG(SECOND, l.PrevUtc, l.TsUtc) >= @SessionTimeoutMinutes * 60 THEN 1
                WHEN CAST(l.PrevTs AS DATE) <> CAST(l.Ts AS DATE)                        THEN 1
                ELSE 0 END AS IsNew
    INTO #seq
    FROM (SELECT s.RecordId, s.UserUPN, s.KeyCtx, s.TsUtc, s.Ts, s.App, s.IsCopilotLic,
                 LAG(s.TsUtc) OVER (PARTITION BY s.UserUPN, s.KeyCtx ORDER BY s.TsUtc, s.RecordId) AS PrevUtc,
                 LAG(s.Ts)    OVER (PARTITION BY s.UserUPN, s.KeyCtx ORDER BY s.TsUtc, s.RecordId) AS PrevTs
          FROM #src AS s
          WHERE s.Skip = 0) AS l;

    DROP TABLE IF EXISTS #num;
    SELECT q.*,
           SUM(q.IsNew) OVER (PARTITION BY q.UserUPN, q.KeyCtx ORDER BY q.TsUtc, q.RecordId
                              ROWS UNBOUNDED PRECEDING) AS Ordinal
    INTO #num
    FROM #seq AS q;

    DROP TABLE IF EXISTS #first;
    SELECT DISTINCT n.UserUPN, n.KeyCtx, n.Ordinal,
           FIRST_VALUE(n.App) OVER (PARTITION BY n.UserUPN, n.KeyCtx, n.Ordinal ORDER BY n.TsUtc, n.RecordId
                                    ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW)          AS PrimaryApp,
           FIRST_VALUE(n.IsCopilotLic) OVER (PARTITION BY n.UserUPN, n.KeyCtx, n.Ordinal ORDER BY n.TsUtc, n.RecordId
                                    ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW)          AS IsLic
    INTO #first
    FROM #num AS n;

    /* ---- 3. Directory attributes (optional table) ------------------------ */
    /* From on-premises AD (audit.UserDirectory, loaded by
       tools/Export-UsersWithHierarchy-ToSql.ps1). Without it, NULL. */

    DROP TABLE IF EXISTS #profile;
    CREATE TABLE #profile (UserPrincipalName NVARCHAR(450) COLLATE DATABASE_DEFAULT NOT NULL PRIMARY KEY,
                           Division NVARCHAR(400) NULL, Department NVARCHAR(400) NULL, JobTitle NVARCHAR(400) NULL, ManagerUpn NVARCHAR(1024) NULL);
    IF OBJECT_ID(N'audit.UserDirectory', N'U') IS NOT NULL
        EXEC (N'INSERT INTO #profile (UserPrincipalName, Division, Department, JobTitle, ManagerUpn)
                SELECT UserPrincipalName, Division, Department, JobTitle, ManagerUPN FROM audit.UserDirectory;');

    /* ---- 3b. Copilot license history (optional) --------------------------- */
    /*
        With @LicenseEventDatabase, a session's license flag is whether the
        user held a Copilot SKU when it started, not today's enrichment value.
        Per SKU: the latest change at or before the start decides; with none,
        the first change after it does in reverse (a removal means it was held
        before). A user with no Copilot change in the history keeps today's
        flag - nothing changed while the history was being kept - so the
        history must reach up to the enrichment run, or a change after its end
        is missed and today's flag is used for everything before it. Limit: a
        second Copilot SKU held throughout, unchanged, while another changed,
        is not seen.
    */
    /* COLLATE DATABASE_DEFAULT as #profile and #base have it (2.4.12): without
       it the columns took tempdb's collation, and on a database whose own
       differs, the comparison with the sessions below failed every build
       (Msg 468), even with no license database. TsUtc is the change's UTC
       time, compared with the session's UTC start (2.4.12; see section 2). */
    DROP TABLE IF EXISTS #lic;
    CREATE TABLE #lic (UserUPN NVARCHAR(512) COLLATE DATABASE_DEFAULT NOT NULL, Sku NVARCHAR(200) COLLATE DATABASE_DEFAULT NOT NULL,
                       TsUtc DATETIME2(0) NOT NULL, Added BIT NOT NULL);
    /* Several databases are read together: each license backfill covers a
       fixed range, and a change after one's end - the most recent, often -
       is only in a later one. Overlap is harmless: each lookup below takes a
       single latest or earliest change. */
    DECLARE @licDb SYSNAME, @licSql NVARCHAR(MAX);
    DECLARE licDbs CURSOR LOCAL FAST_FORWARD FOR SELECT Name FROM @licDbs;
    OPEN licDbs;
    FETCH NEXT FROM licDbs INTO @licDb;
    WHILE @@FETCH_STATUS = 0
    BEGIN
        SET @licSql = N'INSERT INTO #lic (UserUPN, Sku, TsUtc, Added)
            SELECT c.TargetUpn, c.Sku, CAST(c.CreatedDateTime AS DATETIME2(0)),
                   CASE WHEN c.Change = N''Added'' THEN 1 ELSE 0 END
            FROM ' + QUOTENAME(@licDb) + N'.audit.vLicenseSkuChange AS c
            WHERE c.TargetUpn IS NOT NULL AND c.Sku LIKE N''%COPILOT%'';';
        EXEC sys.sp_executesql @licSql;
        FETCH NEXT FROM licDbs INTO @licDb;
    END;
    CLOSE licDbs;
    DEALLOCATE licDbs;

    /* ---- 4. Sessions ----------------------------------------------------- */

    /* Start and end are the first and last prompt in UTC, shown in the
       reporting zone; the span is measured in UTC (2.4.12). The end is never
       shown before the start (2.4.12): in the hour clocks go back, a session
       begun before the change and ended after it ended, in local time,
       before it began. */
    DROP TABLE IF EXISTS #sess;
    SELECT g.UserUPN, g.KeyCtx, g.Ordinal, g.StartUtc, g.EndUtc,
           z.StartTs,
           CASE WHEN z.EndTs < z.StartTs THEN z.StartTs ELSE z.EndTs END AS EndTs,
           g.Prompts
    INTO #sess
    FROM (SELECT n.UserUPN, n.KeyCtx, n.Ordinal,
                 MIN(n.TsUtc) AS StartUtc,
                 MAX(n.TsUtc) AS EndUtc,
                 COUNT(*)     AS Prompts
          FROM #num AS n
          GROUP BY n.UserUPN, n.KeyCtx, n.Ordinal) AS g
    CROSS APPLY (SELECT CAST(g.StartUtc AT TIME ZONE 'UTC' AT TIME ZONE @ReportingTimeZone AS DATETIME2(0)) AS StartTs,
                        CAST(g.EndUtc   AT TIME ZONE 'UTC' AT TIME ZONE @ReportingTimeZone AS DATETIME2(0)) AS EndTs) AS z;

    DROP TABLE IF EXISTS #histlic;
    SELECT s.UserUPN, s.KeyCtx, s.Ordinal,
           CASE WHEN NOT EXISTS (SELECT 1 FROM #lic AS l WHERE l.UserUPN = s.UserUPN) THEN NULL
                WHEN EXISTS (
                    SELECT 1
                    FROM (SELECT DISTINCT l.Sku FROM #lic AS l WHERE l.UserUPN = s.UserUPN) AS k
                    CROSS APPLY (SELECT COALESCE(
                        (SELECT TOP (1) CAST(b.Added AS INT) FROM #lic AS b
                         WHERE b.UserUPN = s.UserUPN AND b.Sku = k.Sku AND b.TsUtc <= s.StartUtc ORDER BY b.TsUtc DESC),
                        (SELECT TOP (1) 1 - CAST(a.Added AS INT) FROM #lic AS a
                         WHERE a.UserUPN = s.UserUPN AND a.Sku = k.Sku AND a.TsUtc > s.StartUtc ORDER BY a.TsUtc)) AS Held) AS h
                    WHERE h.Held = 1) THEN CAST(1 AS BIT)
                ELSE CAST(0 AS BIT) END AS HistLic
    INTO #histlic
    FROM #sess AS s;

    /* Incremental: each stream's numbering carries on from its last session
       before the rebuilt days. */
    DROP TABLE IF EXISTS #base;
    CREATE TABLE #base (UserUPN NVARCHAR(4000) COLLATE DATABASE_DEFAULT NOT NULL,
                        KeyCtx  NVARCHAR(4000) COLLATE DATABASE_DEFAULT NOT NULL,
                        MaxOrdinal INT NOT NULL);
    IF @rebuiltFrom IS NOT NULL
        INSERT INTO #base (UserUPN, KeyCtx, MaxOrdinal)
        SELECT k.UserUPN, k.KeyCtx, m.MaxOrdinal
        FROM (SELECT DISTINCT UserUPN, KeyCtx FROM #sess) AS k
        CROSS APPLY (SELECT MAX(c.SessionOrdinal_Derived) AS MaxOrdinal
                     FROM audit.CopilotSession AS c
                     WHERE c.UserUPN = k.UserUPN AND c.AppContext = k.KeyCtx
                       AND c.SessionStart_PromptSubmittedDateTime < @rebuiltFrom) AS m
        WHERE m.MaxOrdinal IS NOT NULL;

    DROP TABLE IF EXISTS #out;
    SELECT s.KeyCtx AS AppContext, s.UserUPN, CAST(s.Ordinal + ISNULL(b.MaxOrdinal, 0) AS INT) AS Ordinal, s.StartTs, s.EndTs, s.Prompts,
           m.SpanMinutes, d.CalendarDays,
           f.PrimaryApp,
           COALESCE(c.AppCategory, N'In-App Copilot') AS AppCategory,
           COALESCE(h.HistLic, f.IsLic) AS IsLic,
           p.Division, p.Department, p.JobTitle, p.ManagerUpn
    INTO #out
    FROM #sess AS s
    JOIN #first AS f ON f.UserUPN = s.UserUPN AND f.KeyCtx = s.KeyCtx AND f.Ordinal = s.Ordinal
    JOIN #histlic AS h ON h.UserUPN = s.UserUPN AND h.KeyCtx = s.KeyCtx AND h.Ordinal = s.Ordinal
    LEFT JOIN audit.SessionAppCategory AS c ON c.App = f.PrimaryApp
    LEFT JOIN #profile AS p ON p.UserPrincipalName = s.UserUPN
    LEFT JOIN #base AS b ON b.UserUPN = s.UserUPN AND b.KeyCtx = s.KeyCtx
    CROSS APPLY (SELECT CAST(DATEDIFF_BIG(SECOND, s.StartUtc, s.EndUtc) / 60.0 AS DECIMAL(14, 4)) AS SpanMinutes) AS m
    CROSS APPLY (SELECT DATEDIFF(DAY, s.StartTs, s.EndTs) + 1 AS CalendarDays) AS d;

    /* ---- 5. Replace the sessions, then the weeks they fall in ------------ */
    /*
        Full: both tables emptied and rebuilt. Incremental: the sessions from
        @rebuiltFrom on are replaced, and every week containing a rebuilt day
        is recomputed. Either way the weeks are computed from
        audit.CopilotSession, so a week that straddles @rebuiltFrom counts
        its kept sessions too, and both paths give the same numbers.

        Weeks run Saturday-Friday and are labeled by the Friday; a session
        belongs to the week its last prompt falls in, which - a session never
        crossing midnight - is the week of its day. 1900-01-06 was a
        Saturday, so this is right whatever DATEFIRST says.
    */
    DECLARE @sessions INT, @weeks INT, @wk0 DATE = NULL, @weekFrom DATETIME2(0) = NULL;
    IF @rebuiltFrom IS NOT NULL
    BEGIN
        SET @wk0 = DATEADD(DAY, 6 - DATEDIFF(DAY, '19000106', CAST(@rebuiltFrom AS DATE)) % 7, CAST(@rebuiltFrom AS DATE));
        SET @weekFrom = CAST(DATEADD(DAY, -6, @wk0) AS DATETIME2(0));   -- the Saturday that week starts
    END;

    BEGIN TRANSACTION;

    IF @rebuiltFrom IS NULL
        TRUNCATE TABLE audit.CopilotSession;
    ELSE
        DELETE FROM audit.CopilotSession WHERE SessionStart_PromptSubmittedDateTime >= @rebuiltFrom;

    INSERT INTO audit.CopilotSession
        (AppContext, UserUPN, SessionOrdinal_Derived,
         SessionStart_PromptSubmittedDateTime, SessionEnd_PromptSubmittedDateTime,
         InferredSessionSpanMinutes, InferredSessionSpanHours, InferredSessionSpanDays,
         SessionTimeoutMinutes, CalendarDaysTouched_InSessionSpan, PromptCount, SessionType,
         PrimaryApp, AppCategory, IsM365CopilotLicensed, Division, Department, JobTitle, ManagerUPN,
         AvgMinutesPerPrompt_InferredSessionSpan, AvgMinutesPerPromptPerCalendarDay_InferredSpan,
         AvgMinutesPerPrompt_NormalizedByCalendarDays, MetricVersion)
    SELECT o.AppContext, o.UserUPN, o.Ordinal, o.StartTs, o.EndTs,
           ROUND(o.SpanMinutes, 2), ROUND(o.SpanMinutes / 60, 2), ROUND(o.SpanMinutes / 1440, 6),
           @SessionTimeoutMinutes, o.CalendarDays, o.Prompts,
           CASE WHEN o.Prompts = 1 THEN N'Single-Turn' ELSE N'Multi-Turn' END,
           o.PrimaryApp, o.AppCategory, o.IsLic, o.Division, o.Department, o.JobTitle, o.ManagerUpn,
           ROUND(o.SpanMinutes / o.Prompts, 2),
           ROUND(o.SpanMinutes / o.Prompts / o.CalendarDays, 2),
           ROUND((o.SpanMinutes / o.CalendarDays) / o.Prompts, 2),
           @MetricVersion
    FROM #out AS o;
    SET @sessions = @@ROWCOUNT;

    IF @rebuiltFrom IS NULL
        TRUNCATE TABLE audit.CopilotWeeklyRollup;
    ELSE
        DELETE FROM audit.CopilotWeeklyRollup WHERE WeekEnding >= @wk0;

    INSERT INTO audit.CopilotWeeklyRollup
        (WeekEnding, WeekYear, WeekNumber, WeekLabel, UserUPN, PrimaryApp, AppCategory, IsM365CopilotLicensed,
         WeeklySessionCount, WeeklyPromptCount,
         WeeklyInferredSessionSpanMinutes, WeeklyInferredSessionSpanHours, WeeklyInferredSessionSpanDays,
         ActiveDaysWithUsageInWeek, DistinctAppContextsInWeek,
         FirstPromptSubmittedDateTime_InWeek, LastPromptSubmittedDateTime_InWeek,
         Division, Department, JobTitle, ManagerUPN,
         AvgMinutesPerPrompt_InferredSpan_Weekly, MetricVersion)
    SELECT k.WeekEnding, k.WeekYear, k.WeekNumber,
           CONCAT(k.WeekYear, N'-W', RIGHT(CONCAT(N'0', k.WeekNumber), 2)),
           k.UserUPN, k.PrimaryApp, k.AppCategory, k.IsLic,
           COUNT(*), SUM(k.Prompts),
           ROUND(SUM(k.SpanMinutes), 2), ROUND(SUM(k.SpanMinutes) / 60, 2), ROUND(SUM(k.SpanMinutes) / 1440, 6),
           COUNT(DISTINCT CAST(k.StartTs AS DATE)), COUNT(DISTINCT k.AppContext),
           MIN(k.StartTs), MAX(k.EndTs),
           MAX(k.Division), MAX(k.Department), MAX(k.JobTitle), MAX(k.ManagerUpn),
           ROUND(SUM(k.SpanMinutes) / SUM(k.Prompts), 2),
           @MetricVersion
    FROM (
        SELECT c.AppContext, c.UserUPN,
               c.SessionStart_PromptSubmittedDateTime AS StartTs, c.SessionEnd_PromptSubmittedDateTime AS EndTs,
               c.PromptCount AS Prompts, c.PrimaryApp, c.AppCategory, c.IsM365CopilotLicensed AS IsLic,
               c.Division, c.Department, c.JobTitle, c.ManagerUPN AS ManagerUpn,
               /* the exact span, not the rounded column: weeks sum it before rounding. A span
                  is whole seconds and the column keeps hundredths of a minute, within 0.3 s,
                  so the seconds come back from it exactly (2.4.12: they were taken from the
                  stored start and end, local times an hour out across a daylight-saving change). */
               CAST(CAST(ROUND(c.InferredSessionSpanMinutes * 60, 0) AS BIGINT) / 60.0 AS DECIMAL(14, 4)) AS SpanMinutes,
               w.WeekEnding, YEAR(w.WeekEnding) AS WeekYear,
               DATEDIFF(DAY, ff.FirstFriday, w.WeekEnding) / 7 + 1 AS WeekNumber
        FROM audit.CopilotSession AS c
        CROSS APPLY (SELECT CAST(c.SessionEnd_PromptSubmittedDateTime AS DATE) AS EndDay) AS e
        CROSS APPLY (SELECT DATEADD(DAY, 6 - DATEDIFF(DAY, '19000106', e.EndDay) % 7, e.EndDay) AS WeekEnding) AS w
        CROSS APPLY (SELECT DATEFROMPARTS(YEAR(w.WeekEnding), 1, 1) AS Jan1) AS j
        CROSS APPLY (SELECT DATEADD(DAY, 6 - DATEDIFF(DAY, '19000106', j.Jan1) % 7, j.Jan1) AS FirstFriday) AS ff
        WHERE @weekFrom IS NULL OR c.SessionStart_PromptSubmittedDateTime >= @weekFrom
    ) AS k
    GROUP BY k.WeekEnding, k.WeekYear, k.WeekNumber, k.UserUPN, k.PrimaryApp, k.AppCategory, k.IsLic
    OPTION (RECOMPILE);
    SET @weeks = @@ROWCOUNT;

    INSERT INTO audit.CopilotSessionRun
        (RunStartUtc, RunEndUtc, RowsRead, RowsWithOriginalAppContext, RowsRecoveredFromLocation,
         RowsSkippedMissingContext, RowsSkippedMissingUser, RowsSkippedBadTimestamp,
         SessionsWritten, WeeklyGroups, SessionTimeoutMinutes, ContextFallback, ReportingTimeZone, LicenseEventDatabase,
         MetricVersion, RebuiltFrom)
    VALUES (@started, SYSUTCDATETIME(), @read, ISNULL(@orig, 0), ISNULL(@recovered, 0),
            ISNULL(@noCtx, 0), ISNULL(@noUser, 0), ISNULL(@badTs, 0),
            @sessions, @weeks, @SessionTimeoutMinutes, @ContextFallback, @ReportingTimeZone, @LicenseEventDatabase,
            @MetricVersion, @rebuiltFrom);

    COMMIT TRANSACTION;

    SET @msg = CONCAT(N'Sessions built', CASE WHEN @rebuiltFrom IS NULL THEN N'' ELSE
                          CONCAT(N' from ', CONVERT(NVARCHAR(10), @rebuiltFrom, 23), N' (incremental)') END, N': ',
                      FORMAT(@read, 'N0'), N' row(s) read, ',
                      FORMAT(@sessions, 'N0'), N' session(s), ', FORMAT(@weeks, 'N0'), N' weekly group(s)',
                      CASE WHEN @rebuiltFrom IS NULL THEN N'' ELSE N' written' END, N'.');
    RAISERROR(@msg, 0, 1) WITH NOWAIT;
    IF ISNULL(@noCtx, 0) + ISNULL(@noUser, 0) + ISNULL(@badTs, 0) > 0
    BEGIN
        SET @msg = CONCAT(N'  Skipped: ', FORMAT(ISNULL(@noCtx, 0), 'N0'), N' with no context (',
                          @ContextFallback, N' fallback), ', FORMAT(ISNULL(@noUser, 0), 'N0'), N' with no user, ',
                          FORMAT(ISNULL(@badTs, 0), 'N0'), N' with no timestamp. ',
                          FORMAT(ISNULL(@recovered, 0), 'N0'), N' took their context from Location.');
        RAISERROR(@msg, 0, 1) WITH NOWAIT;
    END;
END;
GO
