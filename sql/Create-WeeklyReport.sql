/*
    Copilot audit backfill - the weekly Copilot report
    ------------------------------------------------------------------------
    Creates, in the Copilot database:

      audit.WeeklyReportSend       one row per week the report was sent (or
                                   tried): what Send-CopilotWeeklyReport.ps1
                                   uses to send each week once, and to catch
                                   up after a missed Tuesday.
      bi.usp_WeeklyCopilotReport   the figures for one Saturday-to-Friday week
                                   and the weeks before it.

    Install-CopilotAudit.ps1 runs it (3.1.2). Re-runnable: the table is created
    only if missing, the procedure replaced.

    From 3.1.3 the report is the eight figures of a week (Saturday 00:00 to
    Friday 23:59:59.9999 in the reporting time zone, the days bi.Calendar and
    every report use), in two columns:
      M365 Copilot   use made with a Copilot license at the time
                     (LicenseStatusKey = 1).
      Copilot Chat   use made without one (Not licensed, and Unknown: a license
                     the history could not place).
    Only people whose AD account is enabled now are counted (bi.DimUser
    AccountEnabled = 1): accounts since disabled, people who have left and
    guests are left out of every figure.

      Number of Users Enabled  the enabled members of the AD group named for
                               each column (CopilotLicenseGroup,
                               CopilotChatGroup in copilot-audit.settings.psd1),
                               as the nightly AD load counted them on the last
                               day it ran in the week (audit.ReportGroupDay).
                               NULL for a week before the groups were counted.
      Active Users             people who used Copilot in the week, with a
                               license or without one.
      Number of actions taken  interactions: one per Copilot audit record.
      Security Alerts          interactions with a jailbreak attempt, a prompt
                               injection or a blocked resource: the dashboard's
                               Security signals.
    Someone licensed part-way through a week can be active in both columns;
    each interaction is in one only.

    @ChatEnabled is read no more (3.1.3): Users Enabled comes from the groups.
    It is kept so a caller that passes it still runs.

    Result sets:
      1. The week: WeekStart, WeekEnding, WeekLabel, DataThrough (last day
         with data), DataComplete (data reaches the Monday after the week, so
         records Purview delivers late are in), GroupsCounted (the day the
         groups' count was taken, or NULL), ReportingTimeZone, and the
         footnote's two counts: M365OutsideGroup (people who used Copilot with
         a license in the week but are not in the license group now) and
         ChatOutsideGroup (people who used it without one and are not in the
         Chat group now); NULL when that group's members were not recorded.
      2. The table: Metric, WeekM365, WeekChat.
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
    @ChatEnabled VARCHAR(16) = NULL         -- not read from 3.1.3; kept for callers that pass it
AS
BEGIN
    SET NOCOUNT ON;

    IF @Weeks IS NULL OR @Weeks < 1 OR @Weeks > 104
    BEGIN
        RAISERROR('@Weeks must be between 1 and 104.', 16, 1);
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

    DECLARE @lastKey INT = (SELECT MAX(DateKey) FROM bi.FactInteraction);
    DECLARE @hasGroups BIT = CASE WHEN OBJECT_ID(N'audit.ReportGroupDay', N'U') IS NULL THEN 0 ELSE 1 END;

    /* ---- one pass over the interactions of enabled accounts in the trend's weeks */

    CREATE TABLE #use
    (
        WeekEnding   DATE    NOT NULL,
        UserKey      INT     NOT NULL,
        IsLic        BIT     NOT NULL,
        Interactions BIGINT  NOT NULL,
        Signals      BIGINT  NOT NULL,
        PRIMARY KEY (WeekEnding, UserKey, IsLic)
    );

    INSERT INTO #use (WeekEnding, UserKey, IsLic, Interactions, Signals)
    SELECT d.WeekEnding,
           f.UserKey,
           CASE WHEN f.LicenseStatusKey = 1 THEN 1 ELSE 0 END,
           COUNT_BIG(*),
           SUM(CAST(CASE WHEN f.JailbreakDetected = 1 OR f.PromptInjectionDetected = 1
                           OR f.HasBlockedResource = 1 THEN 1 ELSE 0 END AS BIGINT))
    FROM bi.FactInteraction AS f
    JOIN bi.DimDate AS d ON d.DateKey = f.DateKey
    JOIN bi.DimUser AS u ON u.UserKey = f.UserKey AND u.AccountEnabled = 1
    WHERE f.DateKey BETWEEN @rangeKey AND @endKey
    GROUP BY d.WeekEnding, f.UserKey, CASE WHEN f.LicenseStatusKey = 1 THEN 1 ELSE 0 END;

    /* ---- the weeks ---------------------------------------------------- */

    CREATE TABLE #wk
    (
        WeekEnding      DATE    NOT NULL PRIMARY KEY,
        WeekStart       DATE    NOT NULL,
        M365Enabled     INT     NULL,
        M365Active      INT     NULL,
        M365Actions     BIGINT  NULL,
        M365Signals     BIGINT  NULL,
        ChatEnabled     INT     NULL,
        ChatActive      INT     NULL,
        ChatActions     BIGINT  NULL,
        ChatSignals     BIGINT  NULL,
        GroupsCounted   DATE    NULL
    );

    INSERT INTO #wk (WeekEnding, WeekStart)
    SELECT DISTINCT d.WeekEnding, DATEADD(DAY, -6, d.WeekEnding)
    FROM bi.DimDate AS d
    WHERE d.WeekEnding BETWEEN @firstWeekEnd AND @WeekEnding;

    UPDATE w SET
        M365Active  = (SELECT COUNT(*) FROM #use AS u WHERE u.WeekEnding = w.WeekEnding AND u.IsLic = 1),
        M365Actions = (SELECT ISNULL(SUM(u.Interactions), 0) FROM #use AS u WHERE u.WeekEnding = w.WeekEnding AND u.IsLic = 1),
        M365Signals = (SELECT ISNULL(SUM(u.Signals), 0) FROM #use AS u WHERE u.WeekEnding = w.WeekEnding AND u.IsLic = 1),
        ChatActive  = (SELECT COUNT(*) FROM #use AS u WHERE u.WeekEnding = w.WeekEnding AND u.IsLic = 0),
        ChatActions = (SELECT ISNULL(SUM(u.Interactions), 0) FROM #use AS u WHERE u.WeekEnding = w.WeekEnding AND u.IsLic = 0),
        ChatSignals = (SELECT ISNULL(SUM(u.Signals), 0) FROM #use AS u WHERE u.WeekEnding = w.WeekEnding AND u.IsLic = 0)
    FROM #wk AS w;

    /* Users Enabled: the groups' count on the last day the nightly AD load ran in the week. A load in the small
       hours of a Saturday counts the week that ended on the Friday, so the day after the week counts too. */
    IF @hasGroups = 1
        EXEC sys.sp_executesql N'
        UPDATE w SET
            GroupsCounted = g.SnapshotDate,
            M365Enabled   = g.M365,
            ChatEnabled   = g.Chat
        FROM #wk AS w
        CROSS APPLY (SELECT TOP (1) r.SnapshotDate,
                            (SELECT x.EnabledMembers FROM audit.ReportGroupDay AS x WHERE x.SnapshotDate = r.SnapshotDate AND x.Role = ''CopilotLicense'') AS M365,
                            (SELECT x.EnabledMembers FROM audit.ReportGroupDay AS x WHERE x.SnapshotDate = r.SnapshotDate AND x.Role = ''CopilotChat'') AS Chat
                     FROM audit.ReportGroupDay AS r
                     WHERE r.SnapshotDate > w.WeekStart AND r.SnapshotDate <= DATEADD(DAY, 1, w.WeekEnding)
                     ORDER BY r.SnapshotDate DESC) AS g;';

    /* ---- the footnote: who used Copilot outside the group of their column ---- */

    DECLARE @m365Outside INT, @chatOutside INT;
    IF @hasGroups = 1
        EXEC sys.sp_executesql N'
        SELECT @m = CASE WHEN EXISTS (SELECT 1 FROM audit.ReportGroupMember WHERE Role = ''CopilotLicense'') THEN
                        (SELECT COUNT(*) FROM #use AS x JOIN bi.DimUser AS u ON u.UserKey = x.UserKey
                         WHERE x.WeekEnding = @w AND x.IsLic = 1
                           AND NOT EXISTS (SELECT 1 FROM audit.ReportGroupMember AS g
                                           WHERE g.Role = ''CopilotLicense'' AND g.UserPrincipalName = u.UserPrincipalName)) END,
               @c = CASE WHEN EXISTS (SELECT 1 FROM audit.ReportGroupMember WHERE Role = ''CopilotChat'') THEN
                        (SELECT COUNT(*) FROM #use AS x JOIN bi.DimUser AS u ON u.UserKey = x.UserKey
                         WHERE x.WeekEnding = @w AND x.IsLic = 0
                           AND NOT EXISTS (SELECT 1 FROM audit.ReportGroupMember AS g
                                           WHERE g.Role = ''CopilotChat'' AND g.UserPrincipalName = u.UserPrincipalName)) END;',
            N'@w DATE, @m INT OUTPUT, @c INT OUTPUT', @w = @WeekEnding, @m = @m365Outside OUTPUT, @c = @chatOutside OUTPUT;

    /* ---- 1. the week -------------------------------------------------- */

    SELECT @weekStart                                                        AS WeekStart,
           @WeekEnding                                                       AS WeekEnding,
           CONCAT(FORMAT(@weekStart, 'M/d'), N' ' + NCHAR(8211) + N' ', FORMAT(@WeekEnding, 'M/d/yyyy')) AS WeekLabel,
           CONVERT(DATE, CONVERT(CHAR(8), @lastKey), 112)                    AS DataThrough,
           CAST(CASE WHEN @lastKey >= @mondayKey THEN 1 ELSE 0 END AS BIT)   AS DataComplete,
           (SELECT GroupsCounted FROM #wk WHERE WeekEnding = @WeekEnding)    AS GroupsCounted,
           @zone                                                             AS ReportingTimeZone,
           @m365Outside                                                      AS M365OutsideGroup,
           @chatOutside                                                      AS ChatOutsideGroup;

    /* ---- 2. the table ------------------------------------------------- */

    SELECT m.SortOrder, m.Metric, m.WeekM365, m.WeekChat
    FROM #wk AS w
    CROSS APPLY (VALUES
        (1, N'Number of Users Enabled', CAST(w.M365Enabled AS BIGINT), CAST(w.ChatEnabled AS BIGINT)),
        (2, N'Active Users',            CAST(w.M365Active AS BIGINT),  CAST(w.ChatActive AS BIGINT)),
        (3, N'Number of actions taken', w.M365Actions,                 w.ChatActions),
        (4, N'Security Alerts',         w.M365Signals,                 w.ChatSignals)
    ) AS m (SortOrder, Metric, WeekM365, WeekChat)
    WHERE w.WeekEnding = @WeekEnding
    ORDER BY m.SortOrder;

    /* ---- 3. the weeks ------------------------------------------------- */

    SELECT w.WeekStart, w.WeekEnding,
           FORMAT(w.WeekStart, 'M/d')                                          AS WeekLabel,   -- the axis label: the Saturday the week starts
           CONCAT(FORMAT(w.WeekStart, 'M/d'), NCHAR(8211), FORMAT(w.WeekEnding, 'M/d')) AS WeekRange,
           w.M365Enabled, w.M365Active, w.M365Actions, w.M365Signals,
           w.ChatEnabled, w.ChatActive, w.ChatActions, w.ChatSignals
    FROM #wk AS w
    ORDER BY w.WeekEnding;
END;
GO
