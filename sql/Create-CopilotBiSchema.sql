/*
    The bi schema: a star schema for a Power BI semantic model
    ------------------------------------------------------------------------
    The audit tables are shaped for loading and auditing, not for a model: the
    flattened table has a row-sized text column for every list, users are UPN
    strings, and licence history lives in other databases. A semantic model
    over 20-40M interactions wants narrow facts with integer keys, small
    dimensions, and nothing high-cardinality it does not need. This is that
    layer. bi.usp_RefreshBi (Create-CopilotBiProcedure.sql) fills it, and each
    -Incremental run calls it after the other derived tables.

      Facts                          one row per
        bi.FactInteraction           Copilot interaction (the big one)
        bi.FactSession               inferred session (audit.CopilotSession)
        bi.FactLicenceChange         SKU added to or removed from a user
        bi.FactLicensedDay           user holding a Copilot licence, per day

      Dimensions
        bi.DimDate                   with the Saturday-Friday week of the rollup
        bi.DimUser                   directory attributes and today's licence
        bi.DimApp                    app and its Copilot Chat / In-App category
        bi.DimAgent, bi.DimModel, bi.DimSku, bi.DimLicenceStatus

    The views with plain names (bi.Interactions, bi.Users, bi.Calendar ...)
    are what the model imports: the same columns less lineage (RecordId,
    CreatedUtc, FirstSeenUtc). Import them, not the tables.

    Left out on purpose, and kept in audit.*: RecordId, ThreadId, app context,
    accessed resource names and URLs, sensitivity label IDs and the JSON.
    Each is close to one value per row, so in an import model they would cost
    more memory than everything else together. A DirectQuery table over
    audit.RawLogFlattened serves drill-through to a record if it is needed.

    Keys are stable: a user, app, agent, model or SKU keeps its key from the
    first refresh that saw it, so a model refreshing only recent partitions
    stays consistent with the partitions it did not refresh. Every fact key is
    NOT NULL; a missing user, app, agent or model maps to a named row
    ('(unknown user)', '(unknown)', '(no agent)', '(not reported)').

    Idempotent. Single batch (no GO). References no audit object, so it can be
    run before or after the rest. Requires SQL Server 2016+ (2017+ for the
    procedure).
*/
SET NOCOUNT ON;

IF SCHEMA_ID(N'bi') IS NULL
    EXEC (N'CREATE SCHEMA bi AUTHORIZATION dbo;');

/* ---- Dimensions ---------------------------------------------------------- */

IF OBJECT_ID(N'bi.DimDate', N'U') IS NULL
    CREATE TABLE bi.DimDate
    (
        DateKey          INT           NOT NULL CONSTRAINT PK_DimDate PRIMARY KEY,   -- yyyymmdd
        [Date]           DATE          NOT NULL,
        [Year]           SMALLINT      NOT NULL,
        [Quarter]        TINYINT       NOT NULL,
        MonthNumber      TINYINT       NOT NULL,
        [MonthName]      NVARCHAR(10)  NOT NULL,
        YearMonth        CHAR(7)       NOT NULL,   -- 2026-09
        DayOfWeekNumber  TINYINT       NOT NULL,   -- 1 = Monday ... 7 = Sunday
        DayOfWeekName    NVARCHAR(10)  NOT NULL,
        IsWeekday        BIT           NOT NULL,
        /* The weekly rollup's week: Saturday to Friday, labelled by the
           Friday; week 1 of a year ends on its first Friday. */
        WeekEnding       DATE          NOT NULL,
        WeekYear         SMALLINT      NOT NULL,
        WeekNumber       TINYINT       NOT NULL,
        WeekLabel        CHAR(8)       NOT NULL    -- 2026-W38
    );

IF OBJECT_ID(N'bi.DimUser', N'U') IS NULL
BEGIN
    CREATE TABLE bi.DimUser
    (
        UserKey              INT IDENTITY(1, 1) NOT NULL CONSTRAINT PK_DimUser PRIMARY KEY,
        UserPrincipalName    NVARCHAR(512)  NOT NULL CONSTRAINT UQ_DimUser_Upn UNIQUE,
        DisplayName          NVARCHAR(400)  NULL,
        Division             NVARCHAR(200)  NULL,
        Department           NVARCHAR(200)  NULL,
        JobTitle             NVARCHAR(200)  NULL,
        OfficeLocation       NVARCHAR(200)  NULL,
        City                 NVARCHAR(200)  NULL,
        Country              NVARCHAR(200)  NULL,
        CompanyName          NVARCHAR(200)  NULL,
        UserType             NVARCHAR(20)   NULL,   -- Member | Guest
        AccountEnabled       BIT            NULL,
        ManagerUpn           NVARCHAR(512)  NULL,
        /* Found | NotFound (left the directory) | Not looked up (no enrichment) */
        DirectoryStatus      NVARCHAR(20)   NOT NULL CONSTRAINT DF_DimUser_Directory DEFAULT (N'Not looked up'),
        /* Today's licence, from the enrichment. Licensed | Not licensed | Unknown.
           Licence at the time of use is on the facts, not here. */
        CopilotLicenceToday  NVARCHAR(12)   NOT NULL CONSTRAINT DF_DimUser_Licence DEFAULT (N'Unknown'),
        CopilotSku           NVARCHAR(100)  NULL,
        LicensedSinceUtc     DATETIME2(0)   NULL,
        FirstSeenUtc         DATETIME2(0)   NOT NULL CONSTRAINT DF_DimUser_FirstSeen DEFAULT (SYSUTCDATETIME())
    );
    INSERT INTO bi.DimUser (UserPrincipalName) VALUES (N'(unknown user)');
END;

IF OBJECT_ID(N'bi.DimApp', N'U') IS NULL
BEGIN
    CREATE TABLE bi.DimApp
    (
        AppKey        INT IDENTITY(1, 1) NOT NULL CONSTRAINT PK_DimApp PRIMARY KEY,
        App           NVARCHAR(200) NOT NULL CONSTRAINT UQ_DimApp_App UNIQUE,   -- Word, Teams, Office (Copilot Chat) ...
        AppCategory   NVARCHAR(50)  NOT NULL CONSTRAINT DF_DimApp_Category DEFAULT (N'In-App Copilot'),
        FirstSeenUtc  DATETIME2(0)  NOT NULL CONSTRAINT DF_DimApp_FirstSeen DEFAULT (SYSUTCDATETIME())
    );
    INSERT INTO bi.DimApp (App) VALUES (N'(unknown)');
END;

IF OBJECT_ID(N'bi.DimAgent', N'U') IS NULL
BEGIN
    CREATE TABLE bi.DimAgent
    (
        AgentKey      INT IDENTITY(1, 1) NOT NULL CONSTRAINT PK_DimAgent PRIMARY KEY,
        AgentId       NVARCHAR(400) NOT NULL CONSTRAINT UQ_DimAgent_Id UNIQUE,
        AgentName     NVARCHAR(400) NULL,   -- the latest name seen; agents get renamed
        FirstSeenUtc  DATETIME2(0)  NOT NULL CONSTRAINT DF_DimAgent_FirstSeen DEFAULT (SYSUTCDATETIME())
    );
    INSERT INTO bi.DimAgent (AgentId, AgentName) VALUES (N'(none)', N'(no agent)');
END;

IF OBJECT_ID(N'bi.DimModel', N'U') IS NULL
BEGIN
    CREATE TABLE bi.DimModel
    (
        ModelKey       INT IDENTITY(1, 1) NOT NULL CONSTRAINT PK_DimModel PRIMARY KEY,
        ModelProvider  NVARCHAR(200) NOT NULL,
        ModelName      NVARCHAR(200) NOT NULL,
        FirstSeenUtc   DATETIME2(0)  NOT NULL CONSTRAINT DF_DimModel_FirstSeen DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT UQ_DimModel UNIQUE (ModelProvider, ModelName)
    );
    INSERT INTO bi.DimModel (ModelProvider, ModelName) VALUES (N'(not reported)', N'(not reported)');
END;

IF OBJECT_ID(N'bi.DimSku', N'U') IS NULL
    CREATE TABLE bi.DimSku
    (
        SkuKey        INT IDENTITY(1, 1) NOT NULL CONSTRAINT PK_DimSku PRIMARY KEY,
        Sku           NVARCHAR(200) NOT NULL CONSTRAINT UQ_DimSku_Sku UNIQUE,   -- SkuPartNumber, e.g. Microsoft_365_Copilot
        IsCopilot     BIT           NOT NULL,
        FirstSeenUtc  DATETIME2(0)  NOT NULL CONSTRAINT DF_DimSku_FirstSeen DEFAULT (SYSUTCDATETIME())
    );

/* Licence held at the time of use. Unknown is kept apart from Not licensed:
   a user who has left the directory, with no licence history, is not known
   to have been unlicensed. */
IF OBJECT_ID(N'bi.DimLicenceStatus', N'U') IS NULL
BEGIN
    CREATE TABLE bi.DimLicenceStatus
    (
        LicenceStatusKey  TINYINT      NOT NULL CONSTRAINT PK_DimLicenceStatus PRIMARY KEY,
        LicenceStatus     NVARCHAR(20) NOT NULL
    );
    INSERT INTO bi.DimLicenceStatus (LicenceStatusKey, LicenceStatus)
    VALUES (0, N'Unknown'), (1, N'Licensed'), (2, N'Not licensed');
END;

/* ---- Facts --------------------------------------------------------------- */

/*
    One row per interaction. Clustered columnstore: rows arrive in time order,
    so a refresh's DELETE of its window and the model's partition queries both
    touch only the row groups for that period.
*/
IF OBJECT_ID(N'bi.FactInteraction', N'U') IS NULL
BEGIN
    CREATE TABLE bi.FactInteraction
    (
        RecordId                 NVARCHAR(450) NOT NULL,   -- lineage; not imported
        CreatedUtc               DATETIME2(0)  NOT NULL,   -- lineage and the refresh window; not imported
        /* Local time in the reporting time zone, truncated to the hour: the
           column for the model's incremental-refresh RangeStart/RangeEnd
           filter. An hour, not a second, so it has ~8,760 values a year
           rather than millions, and costs almost nothing in the model. */
        ActivityHour             DATETIME2(0)  NOT NULL,
        DateKey                  INT           NOT NULL,
        HourOfDay                TINYINT       NOT NULL,
        UserKey                  INT           NOT NULL,
        AppKey                   INT           NOT NULL,
        AgentKey                 INT           NOT NULL,
        ModelKey                 INT           NOT NULL,
        LicenceStatusKey         TINYINT       NOT NULL,
        PromptCount              INT           NOT NULL,
        AccessedResourceCount    INT           NOT NULL,
        UsedWebSearch            BIT           NOT NULL,
        JailbreakDetected        BIT           NOT NULL,
        PromptInjectionDetected  BIT           NOT NULL,
        HasBlockedResource       BIT           NOT NULL,
        HasSensitivityLabel      BIT           NOT NULL
    );
    CREATE CLUSTERED COLUMNSTORE INDEX CCI_FactInteraction ON bi.FactInteraction;
END;

/* One row per session in audit.CopilotSession. A session never crosses
   midnight (a new day starts a new one), so its start date is its only date. */
IF OBJECT_ID(N'bi.FactSession', N'U') IS NULL
    CREATE TABLE bi.FactSession
    (
        SessionKey           BIGINT        NOT NULL CONSTRAINT PK_FactSession PRIMARY KEY,
        SessionStart         DATETIME2(0)  NOT NULL,   -- reporting time zone
        SessionEnd           DATETIME2(0)  NOT NULL,
        DateKey              INT           NOT NULL,
        UserKey              INT           NOT NULL,
        AppKey               INT           NOT NULL,   -- the first prompt's app
        LicenceStatusKey     TINYINT       NOT NULL,
        PromptCount          INT           NOT NULL,
        SpanMinutes          DECIMAL(12, 2) NOT NULL,
        IsMultiTurn          BIT           NOT NULL
    );

IF OBJECT_ID(N'bi.FactLicenceChange', N'U') IS NULL
    CREATE TABLE bi.FactLicenceChange
    (
        RecordId     VARCHAR(64)    NOT NULL,   -- lineage; not imported
        ChangedUtc   DATETIME2(0)   NOT NULL,
        DateKey      INT            NOT NULL,
        UserKey      INT            NOT NULL,   -- whose licence changed
        SkuKey       INT            NOT NULL,
        Change       SMALLINT       NOT NULL,   -- +1 added, -1 removed
        ChangedBy    NVARCHAR(512)  NULL,       -- the admin who made it
        CONSTRAINT PK_FactLicenceChange PRIMARY KEY (RecordId, SkuKey, Change)
    );

/* The adoption denominator: one row per user per day they held a Copilot
   licence at the end of that day. Active licensed users / licensed users,
   and licensed users with no interactions, both come from this. */
IF OBJECT_ID(N'bi.FactLicensedDay', N'U') IS NULL
    CREATE TABLE bi.FactLicensedDay
    (
        DateKey  INT NOT NULL,
        UserKey  INT NOT NULL,
        CONSTRAINT PK_FactLicensedDay PRIMARY KEY (DateKey, UserKey)
    );

/* One row per refresh, with the settings it ran under. */
IF OBJECT_ID(N'bi.RefreshRun', N'U') IS NULL
    CREATE TABLE bi.RefreshRun
    (
        RunId                 INT IDENTITY(1, 1) NOT NULL CONSTRAINT PK_BiRefreshRun PRIMARY KEY,
        StartedUtc            DATETIME2(0)   NOT NULL,
        EndedUtc              DATETIME2(0)   NOT NULL,
        SinceUtc              DATETIME2(0)   NULL,       -- NULL = a full rebuild
        ReportingTimeZone     SYSNAME        NOT NULL,
        LicenceEventDatabase  NVARCHAR(1000) NULL,
        InteractionRows       BIGINT         NOT NULL,   -- written by this run
        InteractionsSkipped   BIGINT         NOT NULL,   -- no timestamp
        SessionRows           INT            NOT NULL,
        LicenceChangeRows     INT            NOT NULL,
        LicensedDayRows       INT            NOT NULL
    );

/* ---- What the model imports ---------------------------------------------- */

EXEC (N'CREATE OR ALTER VIEW bi.Interactions AS
SELECT ActivityHour, DateKey, HourOfDay, UserKey, AppKey, AgentKey, ModelKey, LicenceStatusKey,
       PromptCount, AccessedResourceCount, UsedWebSearch, JailbreakDetected, PromptInjectionDetected,
       HasBlockedResource, HasSensitivityLabel
FROM bi.FactInteraction;');

EXEC (N'CREATE OR ALTER VIEW bi.Sessions AS
SELECT SessionKey, SessionStart, SessionEnd, DateKey, UserKey, AppKey, LicenceStatusKey,
       PromptCount, SpanMinutes, IsMultiTurn
FROM bi.FactSession;');

EXEC (N'CREATE OR ALTER VIEW bi.LicenceChanges AS
SELECT ChangedUtc, DateKey, UserKey, SkuKey, Change, ChangedBy
FROM bi.FactLicenceChange;');

EXEC (N'CREATE OR ALTER VIEW bi.LicensedDays AS
SELECT DateKey, UserKey FROM bi.FactLicensedDay;');

EXEC (N'CREATE OR ALTER VIEW bi.Calendar AS
SELECT DateKey, [Date], [Year], [Quarter], MonthNumber, [MonthName], YearMonth, DayOfWeekNumber,
       DayOfWeekName, IsWeekday, WeekEnding, WeekYear, WeekNumber, WeekLabel
FROM bi.DimDate;');

EXEC (N'CREATE OR ALTER VIEW bi.Users AS
SELECT UserKey, UserPrincipalName, DisplayName, Division, Department, JobTitle, OfficeLocation, City,
       Country, CompanyName, UserType, AccountEnabled, ManagerUpn, DirectoryStatus, CopilotLicenceToday,
       CopilotSku, LicensedSinceUtc
FROM bi.DimUser;');

EXEC (N'CREATE OR ALTER VIEW bi.Apps AS
SELECT AppKey, App, AppCategory FROM bi.DimApp;');

EXEC (N'CREATE OR ALTER VIEW bi.Agents AS
SELECT AgentKey, AgentId, AgentName FROM bi.DimAgent;');

EXEC (N'CREATE OR ALTER VIEW bi.Models AS
SELECT ModelKey, ModelProvider, ModelName FROM bi.DimModel;');

EXEC (N'CREATE OR ALTER VIEW bi.Skus AS
SELECT SkuKey, Sku, IsCopilot FROM bi.DimSku;');

EXEC (N'CREATE OR ALTER VIEW bi.LicenceStatuses AS
SELECT LicenceStatusKey, LicenceStatus FROM bi.DimLicenceStatus;');
