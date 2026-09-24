/*
    Inferred sessions and the Saturday-Friday weekly rollup
    ------------------------------------------------------------------------
    A SQL port of the session script that used to read the Purview CSV export
    (v5.7.5, metric v5.5-prod-60m-12h-satfri-customweek-...). It reads
    audit.RawLogFlattened, whose columns are that export's columns, and writes
    the two outputs the script wrote as CSV:

      audit.CopilotSession        copilot_session_summary.csv, one row per session
      audit.CopilotWeeklyRollup   weekly_rollup.csv, one row per
                                  week x user x primary app x category x licence

    The views audit.vCopilotSessionSummary and audit.vCopilotWeeklyRollup carry
    the CSVs' exact column names and text formats (yyyy-MM-dd HH:mm:ss, TRUE /
    FALSE), so anything that read the files can read the views instead.

    audit.usp_BuildCopilotSessions (Create-CopilotSessionProcedure.sql) fills
    them. Its rules are the script's; where this data differs from the CSV the
    script was written for, the difference is a setting, and documented:

      * audit.SessionAppCategory maps App to 'Copilot Chat' / 'In-App Copilot'.
        The script tested App = 'Copilot Chat', the portal export's label. The
        Graph data carries host names instead ('Office' is Copilot Chat), so
        the literal test would call every session In-App. Edit the table to
        change the split; unmapped apps are 'In-App Copilot'.
      * The script keyed a session on user + app context, falling back to
        Location, and skipped a row with neither. On a live tenant 98% of rows
        had no app context and the script's rule skipped every Copilot Chat,
        Outlook and Teams row, so the procedure keys on the user by default.
        @ContextFallback = 'Location' reproduces the script exactly, and every
        build records its rule and its skips in audit.CopilotSessionRun.

    Division, Department, JobTitle and ManagerUPN come from audit.UserProfile
    (tools/Update-CopilotAuditEnrichment.ps1); Division is Entra's
    employeeOrgData.division, empty where the directory does not set it.

    Idempotent. Single batch (no GO). Requires SQL Server 2016+.
*/
SET NOCOUNT ON;

IF SCHEMA_ID(N'audit') IS NULL
    EXEC (N'CREATE SCHEMA audit AUTHORIZATION dbo;');

/* App -> category. Seeded once; edits survive re-running this script. */
IF OBJECT_ID(N'audit.SessionAppCategory', N'U') IS NULL
BEGIN
    CREATE TABLE audit.SessionAppCategory
    (
        App          NVARCHAR(200) NOT NULL CONSTRAINT PK_SessionAppCategory PRIMARY KEY,
        AppCategory  NVARCHAR(50)  NOT NULL
    );
    INSERT INTO audit.SessionAppCategory (App, AppCategory) VALUES
        (N'Copilot Chat',              N'Copilot Chat'),   -- the portal export's label
        (N'Office',                    N'Copilot Chat'),   -- Microsoft 365 Copilot Chat in the Graph data
        (N'OfficeCopilotSearchAnswer', N'Copilot Chat'),
        (N'BizChat',                   N'Copilot Chat'),
        (N'Edge',                      N'Copilot Chat');
END;

IF OBJECT_ID(N'audit.CopilotSession', N'U') IS NULL
    CREATE TABLE audit.CopilotSession
    (
        SessionKey                                    BIGINT IDENTITY(1, 1) NOT NULL CONSTRAINT PK_CopilotSession PRIMARY KEY,
        AppContext                                    NVARCHAR(4000) NULL,
        UserUPN                                       NVARCHAR(512)  NOT NULL,
        SessionOrdinal_Derived                        INT            NOT NULL,
        SessionStart_PromptSubmittedDateTime          DATETIME2(0)   NOT NULL,   -- reporting time zone
        SessionEnd_PromptSubmittedDateTime            DATETIME2(0)   NOT NULL,
        InferredSessionSpanMinutes                    DECIMAL(12, 2) NOT NULL,
        InferredSessionSpanHours                      DECIMAL(12, 2) NOT NULL,
        InferredSessionSpanDays                       DECIMAL(12, 6) NOT NULL,
        SessionTimeoutMinutes                         INT            NOT NULL,
        CalendarDaysTouched_InSessionSpan             INT            NOT NULL,
        PromptCount                                   INT            NOT NULL,
        SessionType                                   NVARCHAR(20)   NOT NULL,   -- Single-Turn / Multi-Turn
        PrimaryApp                                    NVARCHAR(200)  NULL,
        AppCategory                                   NVARCHAR(50)   NOT NULL,
        IsM365CopilotLicensed                         BIT            NULL,
        Division                                      NVARCHAR(400)  NULL,
        Department                                    NVARCHAR(400)  NULL,
        JobTitle                                      NVARCHAR(400)  NULL,
        ManagerUPN                                    NVARCHAR(1024) NULL,
        AvgMinutesPerPrompt_InferredSessionSpan       DECIMAL(12, 2) NOT NULL,
        AvgMinutesPerPromptPerCalendarDay_InferredSpan DECIMAL(12, 2) NOT NULL,
        AvgMinutesPerPrompt_NormalizedByCalendarDays  DECIMAL(12, 2) NOT NULL,
        MetricVersion                                 NVARCHAR(200)  NOT NULL
    );

IF OBJECT_ID(N'audit.CopilotSession', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'audit.CopilotSession') AND name = N'IX_CopilotSession_User')
    EXEC (N'CREATE INDEX IX_CopilotSession_User ON audit.CopilotSession (UserUPN, SessionStart_PromptSubmittedDateTime);');

IF OBJECT_ID(N'audit.CopilotWeeklyRollup', N'U') IS NULL
    CREATE TABLE audit.CopilotWeeklyRollup
    (
        WeekEnding                                DATE           NOT NULL,   -- a Friday; the week is Saturday-Friday
        WeekYear                                  INT            NOT NULL,
        WeekNumber                                INT            NOT NULL,   -- 1 = the week ending on the year's first Friday
        WeekLabel                                 NVARCHAR(10)   NOT NULL,   -- 2026-W38
        UserUPN                                   NVARCHAR(512)  NOT NULL,
        PrimaryApp                                NVARCHAR(200)  NULL,
        AppCategory                               NVARCHAR(50)   NOT NULL,
        IsM365CopilotLicensed                     BIT            NULL,
        WeeklySessionCount                        INT            NOT NULL,
        WeeklyPromptCount                         INT            NOT NULL,
        WeeklyInferredSessionSpanMinutes          DECIMAL(14, 2) NOT NULL,
        WeeklyInferredSessionSpanHours            DECIMAL(14, 2) NOT NULL,
        WeeklyInferredSessionSpanDays             DECIMAL(14, 6) NOT NULL,
        ActiveDaysWithUsageInWeek                 INT            NOT NULL,
        DistinctAppContextsInWeek                 INT            NOT NULL,
        FirstPromptSubmittedDateTime_InWeek       DATETIME2(0)   NOT NULL,
        LastPromptSubmittedDateTime_InWeek        DATETIME2(0)   NOT NULL,
        Division                                  NVARCHAR(400)  NULL,
        Department                                NVARCHAR(400)  NULL,
        JobTitle                                  NVARCHAR(400)  NULL,
        ManagerUPN                                NVARCHAR(1024) NULL,
        AvgMinutesPerPrompt_InferredSpan_Weekly   DECIMAL(12, 2) NOT NULL,
        MetricVersion                             NVARCHAR(200)  NOT NULL
    );

IF OBJECT_ID(N'audit.CopilotWeeklyRollup', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'audit.CopilotWeeklyRollup') AND name = N'IX_CopilotWeeklyRollup_Week')
    EXEC (N'CREATE INDEX IX_CopilotWeeklyRollup_Week ON audit.CopilotWeeklyRollup (WeekEnding, UserUPN);');

/* One row per build: the script's run_log.csv, with the settings it ran under. */
IF OBJECT_ID(N'audit.CopilotSessionRun', N'U') IS NULL
    CREATE TABLE audit.CopilotSessionRun
    (
        RunId                        INT IDENTITY(1, 1) NOT NULL CONSTRAINT PK_CopilotSessionRun PRIMARY KEY,
        RunStartUtc                  DATETIME2(0)  NOT NULL,
        RunEndUtc                    DATETIME2(0)  NOT NULL,
        RowsRead                     INT           NOT NULL,
        RowsWithOriginalAppContext   INT           NOT NULL,
        RowsRecoveredFromLocation    INT           NOT NULL,
        RowsSkippedMissingContext    INT           NOT NULL,
        RowsSkippedMissingUser       INT           NOT NULL,
        RowsSkippedBadTimestamp      INT           NOT NULL,
        SessionsWritten              INT           NOT NULL,
        WeeklyGroups                 INT           NOT NULL,
        SessionTimeoutMinutes        INT           NOT NULL,
        ContextFallback              NVARCHAR(20)  NOT NULL,
        ReportingTimeZone            NVARCHAR(128) NOT NULL,
        LicenceEventDatabase         NVARCHAR(1000) NULL,  -- NULL: licence flag is today's, from the enrichment
        MetricVersion                NVARCHAR(200) NOT NULL
    );

IF COL_LENGTH(N'audit.CopilotSessionRun', N'LicenceEventDatabase') IS NULL
    ALTER TABLE audit.CopilotSessionRun ADD LicenceEventDatabase NVARCHAR(1000) NULL;

/* ---- CSV-shaped views ---------------------------------------------------- */

EXEC (N'
CREATE OR ALTER VIEW audit.vCopilotSessionSummary
AS
SELECT AppContext,
       UserUPN,
       SessionOrdinal_Derived,
       CONVERT(varchar(19), SessionStart_PromptSubmittedDateTime, 120) AS SessionStart_PromptSubmittedDateTime,
       CONVERT(varchar(19), SessionEnd_PromptSubmittedDateTime, 120)   AS SessionEnd_PromptSubmittedDateTime,
       InferredSessionSpanMinutes,
       InferredSessionSpanHours,
       InferredSessionSpanDays,
       SessionTimeoutMinutes,
       CalendarDaysTouched_InSessionSpan,
       PromptCount,
       SessionType,
       PrimaryApp,
       AppCategory,
       CASE IsM365CopilotLicensed WHEN 1 THEN ''TRUE'' WHEN 0 THEN ''FALSE'' ELSE '''' END AS IsM365CopilotLicensed,
       Division, Department, JobTitle, ManagerUPN,
       AvgMinutesPerPrompt_InferredSessionSpan,
       AvgMinutesPerPromptPerCalendarDay_InferredSpan,
       AvgMinutesPerPrompt_NormalizedByCalendarDays,
       MetricVersion
FROM audit.CopilotSession;');

EXEC (N'
CREATE OR ALTER VIEW audit.vCopilotWeeklyRollup
AS
SELECT CONVERT(varchar(10), WeekEnding, 23) AS WeekEnding,
       WeekYear, WeekNumber, WeekLabel,
       UserUPN, PrimaryApp, AppCategory,
       CASE IsM365CopilotLicensed WHEN 1 THEN ''TRUE'' WHEN 0 THEN ''FALSE'' ELSE '''' END AS IsM365CopilotLicensed,
       WeeklySessionCount,
       WeeklyPromptCount,
       WeeklyInferredSessionSpanMinutes,
       WeeklyInferredSessionSpanHours,
       WeeklyInferredSessionSpanDays,
       ActiveDaysWithUsageInWeek,
       DistinctAppContextsInWeek,
       CONVERT(varchar(19), FirstPromptSubmittedDateTime_InWeek, 120) AS FirstPromptSubmittedDateTime_InWeek,
       CONVERT(varchar(19), LastPromptSubmittedDateTime_InWeek, 120)  AS LastPromptSubmittedDateTime_InWeek,
       Division, Department, JobTitle, ManagerUPN,
       AvgMinutesPerPrompt_InferredSpan_Weekly,
       MetricVersion
FROM audit.CopilotWeeklyRollup;');
