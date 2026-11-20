/*
    Copilot audit backfill - the weekly Copilot report
    ------------------------------------------------------------------------
    Creates, in the Copilot database:

      audit.WeeklyReportSend       one row per week the report was sent (or
                                   tried): what Send-CopilotWeeklyReport.ps1
                                   uses to send each week once, and to catch
                                   up after a missed Tuesday.
      bi.usp_WeeklyCopilotReport   the figures for one Saturday-to-Friday week,
                                   its all-time totals, and the weeks before it.

    Run once, after the release's own scripts (it reads the bi schema):
      sqlcmd -S <server> -d CopilotAudit -i Create-WeeklyReport.sql
    Re-runnable: the table is created only if missing, the procedure replaced.

    The two columns split people by license, as the Microsoft 365 admin
    center's two reports do:
      M365 Copilot   use made with a Copilot license (LicenseStatusKey = 1).
      Copilot Chat   use made without one (Not licensed, and Unknown: a
                     license the history could not place).

    Figures, for a week (Saturday 00:00 to Friday 23:59:59.9999 in the
    reporting time zone, the days bi.Calendar and every report use):
      Users enabled, M365 Copilot  people holding a Copilot license on the
                                   week's last day with license data.
      Users enabled, Copilot Chat  people seen in the audit log from the
                                   first day of data to the week's end
                                   (@ChatEnabled = 'AllUsers', the default),
                                   or only those seen using it without a
                                   license (@ChatEnabled = 'Unlicensed').
      Active users, M365 Copilot   people who used Copilot with a license in
                                   the week and still hold one on its last
                                   day: the dashboard's Active licensed users.
      Active users, Copilot Chat   people who used Copilot without a license
                                   in the week.
      Actions taken                prompts: SUM(PromptCount).
      Security alerts              interactions with a jailbreak attempt, a
                                   prompt injection or a blocked resource:
                                   the dashboard's Security signals.
    All time runs from the first day of data to the end of the week, so a
    report run again later gives the same figures.

    Result sets:
      1. The week: WeekStart, WeekEnding, WeekLabel, AllTimeFrom,
         DataThrough (last day with data), DataComplete (data reaches the
         Monday after the week, so records Purview delivers late are in),
         LicenseDate (the day the license count was taken).
      2. The table: Metric, then WeekM365, WeekChat, AllTimeM365, AllTimeChat.
      3. The weeks: one row per week, oldest first, ending with this one;
         WeekLabel is the Saturday it starts (M/d), WeekRange both ends.

    Requires SQL Server 2016+ (AT TIME ZONE).
*/
SET NOCOUNT ON;
GO

IF OBJECT_ID(N'audit.WeeklyReportSend', N'U') IS NULL
    CREATE TABLE audit.WeeklyReportSend
    (
        WeekEnding   DATE           NOT NULL CONSTRAINT PK_WeeklyReportSend PRIMARY KEY,
        Status       VARCHAR(16)    NOT NULL,   -- Sent, Failed
        Attempts     INT            NOT NULL CONSTRAINT DF_WeeklyReportSend_Attempts DEFAULT (0),
        FirstTryUtc  DATETIME2(0)   NOT NULL CONSTRAINT DF_WeeklyReportSend_First DEFAULT (SYSUTCDATETIME()),
        LastTryUtc   DATETIME2(0)   NOT NULL CONSTRAINT DF_WeeklyReportSend_Last DEFAULT (SYSUTCDATETIME()),
        SentUtc      DATETIME2(0)   NULL,
        Recipients   NVARCHAR(4000) NULL,
        DataComplete BIT            NULL,
        Detail       NVARCHAR(4000) NULL
    );
GO

CREATE OR ALTER PROCEDURE bi.usp_WeeklyCopilotReport
    @WeekEnding  DATE        = NULL,        -- the Friday; NULL = the last complete week
    @Weeks       INT         = 13,          -- weeks in the trend, this one included (13 = about 90 days)
    @ChatEnabled VARCHAR(16) = 'AllUsers'   -- AllUsers | Unlicensed
AS
BEGIN
    SET NOCOUNT ON;

    IF @Weeks IS NULL OR @Weeks < 1 OR @Weeks > 104
    BEGIN
        RAISERROR('@Weeks must be between 1 and 104.', 16, 1);
        RETURN;
    END;
    IF @ChatEnabled NOT IN ('AllUsers', 'Unlicensed')
    BEGIN
        RAISERROR('@ChatEnabled must be AllUsers or Unlicensed.', 16, 1);
        RETURN;
    END;

    /* ---- the week ----------------------------------------------------- */

    DECLARE @zone SYSNAME = audit.ReportingTimeZone();
    DECLARE @today DATE = CAST(SYSUTCDATETIME() AT TIME ZONE 'UTC' AT TIME ZONE @zone AS DATE);

    IF @WeekEnding IS NULL
        SELECT @WeekEnding = MAX(WeekEnding) FROM bi.DimDate WHERE WeekEnding < @today;

    IF @WeekEnding IS NULL OR NOT EXISTS (SELECT 1 FROM bi.DimDate WHERE [Date] = @WeekEnding AND WeekEnding = @WeekEnding)
    BEGIN
        RAISERROR('@WeekEnding must be a Friday in bi.DimDate (weeks run Saturday to Friday).', 16, 1);
        RETURN;
    END;

    DECLARE @weekStart DATE = DATEADD(DAY, -6, @WeekEnding);
    DECLARE @firstWeekEnd DATE = DATEADD(DAY, -7 * (@Weeks - 1), @WeekEnding);
    DECLARE @rangeStart DATE = DATEADD(DAY, -6, @firstWeekEnd);

    DECLARE @endKey INT = CONVERT(INT, CONVERT(CHAR(8), @WeekEnding, 112));
    DECLARE @startKey INT = CONVERT(INT, CONVERT(CHAR(8), @weekStart, 112));
    DECLARE @rangeKey INT = CONVERT(INT, CONVERT(CHAR(8), @rangeStart, 112));
    DECLARE @mondayKey INT = CONVERT(INT, CONVERT(CHAR(8), DATEADD(DAY, 3, @WeekEnding), 112));

    DECLARE @firstKey INT, @lastKey INT;
    SELECT @firstKey = MIN(DateKey), @lastKey = MAX(DateKey) FROM bi.FactInteraction;

    /* ---- one pass over the interactions in the trend's weeks ---------- */

    CREATE TABLE #use
    (
        WeekEnding DATE    NOT NULL,
        UserKey    INT     NOT NULL,
        IsLic      BIT     NOT NULL,
        Prompts    BIGINT  NOT NULL,
        Signals    BIGINT  NOT NULL,
        PRIMARY KEY (WeekEnding, UserKey, IsLic)
    );

    INSERT INTO #use (WeekEnding, UserKey, IsLic, Prompts, Signals)
    SELECT d.WeekEnding,
           f.UserKey,
           CASE WHEN f.LicenseStatusKey = 1 THEN 1 ELSE 0 END,
           SUM(CAST(f.PromptCount AS BIGINT)),
           SUM(CASE WHEN f.JailbreakDetected = 1 OR f.PromptInjectionDetected = 1
                      OR f.HasBlockedResource = 1 THEN 1 ELSE 0 END)
    FROM bi.FactInteraction AS f
    JOIN bi.DimDate AS d ON d.DateKey = f.DateKey
    WHERE f.DateKey BETWEEN @rangeKey AND @endKey
    GROUP BY d.WeekEnding, f.UserKey, CASE WHEN f.LicenseStatusKey = 1 THEN 1 ELSE 0 END;

    /* ---- the license count of each week: its last day with license data */

    CREATE TABLE #licDay (WeekEnding DATE NOT NULL PRIMARY KEY, DateKey INT NOT NULL);

    INSERT INTO #licDay (WeekEnding, DateKey)
    SELECT d.WeekEnding, MAX(l.DateKey)
    FROM bi.FactLicensedDay AS l
    JOIN bi.DimDate AS d ON d.DateKey = l.DateKey
    WHERE l.DateKey BETWEEN @rangeKey AND @endKey
    GROUP BY d.WeekEnding;

    CREATE TABLE #lic (WeekEnding DATE NOT NULL, UserKey INT NOT NULL, PRIMARY KEY (WeekEnding, UserKey));

    INSERT INTO #lic (WeekEnding, UserKey)
    SELECT DISTINCT w.WeekEnding, l.UserKey
    FROM #licDay AS w
    JOIN bi.FactLicensedDay AS l ON l.DateKey = w.DateKey;

    /* ---- when each person was first seen, for the cumulative Chat count */

    CREATE TABLE #first (UserKey INT NOT NULL PRIMARY KEY, FirstKey INT NOT NULL);

    INSERT INTO #first (UserKey, FirstKey)
    SELECT f.UserKey, MIN(f.DateKey)
    FROM bi.FactInteraction AS f
    WHERE f.DateKey <= @endKey
      AND (@ChatEnabled = 'AllUsers' OR f.LicenseStatusKey <> 1)
    GROUP BY f.UserKey;

    /* ---- the weeks ---------------------------------------------------- */

    CREATE TABLE #wk
    (
        WeekEnding     DATE    NOT NULL PRIMARY KEY,
        WeekStart      DATE    NOT NULL,
        M365Enabled    INT     NULL,
        M365Active     INT     NULL,
        M365Prompts    BIGINT  NULL,
        M365Signals    BIGINT  NULL,
        ChatEnabled    INT     NULL,
        ChatActive     INT     NULL,
        ChatPrompts    BIGINT  NULL,
        ChatSignals    BIGINT  NULL
    );

    INSERT INTO #wk (WeekEnding, WeekStart)
    SELECT DISTINCT d.WeekEnding, DATEADD(DAY, -6, d.WeekEnding)
    FROM bi.DimDate AS d
    WHERE d.WeekEnding BETWEEN @firstWeekEnd AND @WeekEnding;

    UPDATE w SET
        M365Enabled = (SELECT COUNT(*) FROM #lic AS l WHERE l.WeekEnding = w.WeekEnding),
        M365Active  = (SELECT COUNT(*) FROM #use AS u
                       JOIN #lic AS l ON l.WeekEnding = u.WeekEnding AND l.UserKey = u.UserKey
                       WHERE u.WeekEnding = w.WeekEnding AND u.IsLic = 1),
        M365Prompts = (SELECT ISNULL(SUM(u.Prompts), 0) FROM #use AS u WHERE u.WeekEnding = w.WeekEnding AND u.IsLic = 1),
        M365Signals = (SELECT ISNULL(SUM(u.Signals), 0) FROM #use AS u WHERE u.WeekEnding = w.WeekEnding AND u.IsLic = 1),
        ChatEnabled = (SELECT COUNT(*) FROM #first AS fs
                       WHERE fs.FirstKey <= CONVERT(INT, CONVERT(CHAR(8), w.WeekEnding, 112))),
        ChatActive  = (SELECT COUNT(*) FROM #use AS u WHERE u.WeekEnding = w.WeekEnding AND u.IsLic = 0),
        ChatPrompts = (SELECT ISNULL(SUM(u.Prompts), 0) FROM #use AS u WHERE u.WeekEnding = w.WeekEnding AND u.IsLic = 0),
        ChatSignals = (SELECT ISNULL(SUM(u.Signals), 0) FROM #use AS u WHERE u.WeekEnding = w.WeekEnding AND u.IsLic = 0)
    FROM #wk AS w;

    /* ---- all time: from the first day of data to the week's end -------- */

    DECLARE @atM365Enabled INT, @atM365Active INT, @atChatActive INT,
            @atM365Prompts BIGINT, @atChatPrompts BIGINT, @atM365Signals BIGINT, @atChatSignals BIGINT;

    SELECT @atM365Enabled = COUNT(DISTINCT l.UserKey)
    FROM bi.FactLicensedDay AS l
    WHERE l.DateKey <= @endKey;

    SELECT @atM365Active  = ISNULL(SUM(p.HasLic), 0),
           @atChatActive  = ISNULL(SUM(p.HasChat), 0),
           @atM365Prompts = ISNULL(SUM(p.LicPrompts), 0),
           @atChatPrompts = ISNULL(SUM(p.ChatPrompts), 0),
           @atM365Signals = ISNULL(SUM(p.LicSignals), 0),
           @atChatSignals = ISNULL(SUM(p.ChatSignals), 0)
    FROM (SELECT f.UserKey,
                 MAX(CASE WHEN f.LicenseStatusKey = 1 THEN 1 ELSE 0 END)                          AS HasLic,
                 MAX(CASE WHEN f.LicenseStatusKey <> 1 THEN 1 ELSE 0 END)                         AS HasChat,
                 SUM(CASE WHEN f.LicenseStatusKey = 1 THEN CAST(f.PromptCount AS BIGINT) ELSE 0 END)  AS LicPrompts,
                 SUM(CASE WHEN f.LicenseStatusKey <> 1 THEN CAST(f.PromptCount AS BIGINT) ELSE 0 END) AS ChatPrompts,
                 SUM(CASE WHEN f.LicenseStatusKey = 1 AND (f.JailbreakDetected = 1 OR f.PromptInjectionDetected = 1
                          OR f.HasBlockedResource = 1) THEN 1 ELSE 0 END)                         AS LicSignals,
                 SUM(CASE WHEN f.LicenseStatusKey <> 1 AND (f.JailbreakDetected = 1 OR f.PromptInjectionDetected = 1
                          OR f.HasBlockedResource = 1) THEN 1 ELSE 0 END)                         AS ChatSignals
          FROM bi.FactInteraction AS f
          WHERE f.DateKey <= @endKey
          GROUP BY f.UserKey) AS p;

    /* ---- 1. the week -------------------------------------------------- */

    SELECT @weekStart                                                        AS WeekStart,
           @WeekEnding                                                       AS WeekEnding,
           CONCAT(FORMAT(@weekStart, 'M/d'), N' ' + NCHAR(8211) + N' ', FORMAT(@WeekEnding, 'M/d/yyyy')) AS WeekLabel,
           CONVERT(DATE, CONVERT(CHAR(8), @firstKey), 112)                   AS AllTimeFrom,
           CONVERT(DATE, CONVERT(CHAR(8), @lastKey), 112)                    AS DataThrough,
           CAST(CASE WHEN @lastKey >= @mondayKey THEN 1 ELSE 0 END AS BIT)   AS DataComplete,
           CONVERT(DATE, CONVERT(CHAR(8), (SELECT DateKey FROM #licDay WHERE WeekEnding = @WeekEnding)), 112) AS LicenseDate,
           @zone                                                             AS ReportingTimeZone,
           @ChatEnabled                                                      AS ChatEnabledScope;

    /* ---- 2. the table ------------------------------------------------- */

    SELECT m.SortOrder, m.Metric, m.WeekM365, m.WeekChat, m.AllTimeM365, m.AllTimeChat
    FROM #wk AS w
    CROSS APPLY (VALUES
        (1, N'Number of Users Enabled',    CAST(w.M365Enabled AS BIGINT), CAST(w.ChatEnabled AS BIGINT), CAST(@atM365Enabled AS BIGINT), CAST(w.ChatEnabled AS BIGINT)),
        (2, N'Active Users',               CAST(w.M365Active AS BIGINT),  CAST(w.ChatActive AS BIGINT),  CAST(@atM365Active AS BIGINT),  CAST(@atChatActive AS BIGINT)),
        (3, N'Number of actions taken',    w.M365Prompts,                 w.ChatPrompts,                 @atM365Prompts,                 @atChatPrompts),
        (4, N'Security Alerts',            w.M365Signals,                 w.ChatSignals,                 @atM365Signals,                 @atChatSignals)
    ) AS m (SortOrder, Metric, WeekM365, WeekChat, AllTimeM365, AllTimeChat)
    WHERE w.WeekEnding = @WeekEnding
    ORDER BY m.SortOrder;

    /* ---- 3. the weeks ------------------------------------------------- */

    SELECT w.WeekStart, w.WeekEnding,
           FORMAT(w.WeekStart, 'M/d')                                          AS WeekLabel,   -- the axis label: the Saturday the week starts
           CONCAT(FORMAT(w.WeekStart, 'M/d'), NCHAR(8211), FORMAT(w.WeekEnding, 'M/d')) AS WeekRange,
           w.M365Enabled, w.M365Active, w.M365Prompts, w.M365Signals,
           w.ChatEnabled, w.ChatActive, w.ChatPrompts, w.ChatSignals
    FROM #wk AS w
    ORDER BY w.WeekEnding;
END;
GO
