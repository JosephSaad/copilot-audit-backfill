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
        bi.DimUser                   AD attributes, the org chain, today's licence
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
        EmployeeType         NVARCHAR(32)   NULL,   -- EMPLOYEE | CONTRACTOR ...
        IsManager            BIT            NULL,
        AccountEnabled       BIT            NULL,
        ManagerUpn           NVARCHAR(512)  NULL,
        ManagerName          NVARCHAR(256)  NULL,
        /* The management chain from AD: OrgLevel1 is the top (the CEO),
           OrgLevel<OrgDepth> the direct manager. A ragged hierarchy, top down. */
        OrgDepth             TINYINT        NULL,
        OrgLevel1            NVARCHAR(256)  NULL,
        OrgLevel2            NVARCHAR(256)  NULL,
        OrgLevel3            NVARCHAR(256)  NULL,
        OrgLevel4            NVARCHAR(256)  NULL,
        OrgLevel5            NVARCHAR(256)  NULL,
        OrgLevel6            NVARCHAR(256)  NULL,
        OrgLevel7            NVARCHAR(256)  NULL,
        OrgLevel8            NVARCHAR(256)  NULL,
        OrgLevel9            NVARCHAR(256)  NULL,
        OrgLevel10           NVARCHAR(256)  NULL,
        OrgLevel11           NVARCHAR(256)  NULL,
        OrgLevel12           NVARCHAR(256)  NULL,
        OrgLevel13           NVARCHAR(256)  NULL,
        OrgLevel14           NVARCHAR(256)  NULL,
        OrgLevel15           NVARCHAR(256)  NULL,
        /* Found (in AD) | NotFound (left Entra) | Not looked up (neither source) */
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

/* 2.0.0: user attributes come from on-premises AD (audit.UserDirectory), which
   adds the employee type, manager name and org chain; Entra's Member/Guest
   UserType goes. Existing tables are altered in place. */
IF OBJECT_ID(N'bi.DimUser', N'U') IS NOT NULL
BEGIN
    IF COL_LENGTH(N'bi.DimUser', N'EmployeeType') IS NULL ALTER TABLE bi.DimUser ADD EmployeeType NVARCHAR(32) NULL;
    IF COL_LENGTH(N'bi.DimUser', N'IsManager')    IS NULL ALTER TABLE bi.DimUser ADD IsManager BIT NULL;
    IF COL_LENGTH(N'bi.DimUser', N'ManagerName')  IS NULL ALTER TABLE bi.DimUser ADD ManagerName NVARCHAR(256) NULL;
    IF COL_LENGTH(N'bi.DimUser', N'OrgDepth')     IS NULL ALTER TABLE bi.DimUser ADD OrgDepth TINYINT NULL;
    IF COL_LENGTH(N'bi.DimUser', N'OrgLevel1') IS NULL ALTER TABLE bi.DimUser ADD OrgLevel1 NVARCHAR(256) NULL;
    IF COL_LENGTH(N'bi.DimUser', N'OrgLevel2') IS NULL ALTER TABLE bi.DimUser ADD OrgLevel2 NVARCHAR(256) NULL;
    IF COL_LENGTH(N'bi.DimUser', N'OrgLevel3') IS NULL ALTER TABLE bi.DimUser ADD OrgLevel3 NVARCHAR(256) NULL;
    IF COL_LENGTH(N'bi.DimUser', N'OrgLevel4') IS NULL ALTER TABLE bi.DimUser ADD OrgLevel4 NVARCHAR(256) NULL;
    IF COL_LENGTH(N'bi.DimUser', N'OrgLevel5') IS NULL ALTER TABLE bi.DimUser ADD OrgLevel5 NVARCHAR(256) NULL;
    IF COL_LENGTH(N'bi.DimUser', N'OrgLevel6') IS NULL ALTER TABLE bi.DimUser ADD OrgLevel6 NVARCHAR(256) NULL;
    IF COL_LENGTH(N'bi.DimUser', N'OrgLevel7') IS NULL ALTER TABLE bi.DimUser ADD OrgLevel7 NVARCHAR(256) NULL;
    IF COL_LENGTH(N'bi.DimUser', N'OrgLevel8') IS NULL ALTER TABLE bi.DimUser ADD OrgLevel8 NVARCHAR(256) NULL;
    IF COL_LENGTH(N'bi.DimUser', N'OrgLevel9') IS NULL ALTER TABLE bi.DimUser ADD OrgLevel9 NVARCHAR(256) NULL;
    IF COL_LENGTH(N'bi.DimUser', N'OrgLevel10') IS NULL ALTER TABLE bi.DimUser ADD OrgLevel10 NVARCHAR(256) NULL;
    IF COL_LENGTH(N'bi.DimUser', N'OrgLevel11') IS NULL ALTER TABLE bi.DimUser ADD OrgLevel11 NVARCHAR(256) NULL;
    IF COL_LENGTH(N'bi.DimUser', N'OrgLevel12') IS NULL ALTER TABLE bi.DimUser ADD OrgLevel12 NVARCHAR(256) NULL;
    IF COL_LENGTH(N'bi.DimUser', N'OrgLevel13') IS NULL ALTER TABLE bi.DimUser ADD OrgLevel13 NVARCHAR(256) NULL;
    IF COL_LENGTH(N'bi.DimUser', N'OrgLevel14') IS NULL ALTER TABLE bi.DimUser ADD OrgLevel14 NVARCHAR(256) NULL;
    IF COL_LENGTH(N'bi.DimUser', N'OrgLevel15') IS NULL ALTER TABLE bi.DimUser ADD OrgLevel15 NVARCHAR(256) NULL;
    IF COL_LENGTH(N'bi.DimUser', N'UserType') IS NOT NULL ALTER TABLE bi.DimUser DROP COLUMN UserType;
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

/*
    The agent an interaction went to. The record's CopilotEventData.AgentId
    is often empty - on a live tenant it was empty on every row, Copilot
    Studio agents included - and the agent is named in AppIdentity instead:
    Copilot.Studio.<environment>-<tenant id>-<schema name> for a Copilot
    Studio agent, MicrosoftAgent.<...>.<name> for one of Microsoft's. So the
    procedure takes AgentId when there is one and AppIdentity otherwise, and
    AgentSource says which.
*/
IF OBJECT_ID(N'bi.DimAgent', N'U') IS NULL
BEGIN
    CREATE TABLE bi.DimAgent
    (
        AgentKey      INT IDENTITY(1, 1) NOT NULL CONSTRAINT PK_DimAgent PRIMARY KEY,
        AgentId       NVARCHAR(400) NOT NULL CONSTRAINT UQ_DimAgent_Id UNIQUE,   -- AgentId, else AppIdentity
        AgentName     NVARCHAR(400) NULL,   -- the latest name seen; agents get renamed
        AgentSource   NVARCHAR(30)  NULL,   -- Copilot Studio | Microsoft | Agent ID | (no agent)
        FirstSeenUtc  DATETIME2(0)  NOT NULL CONSTRAINT DF_DimAgent_FirstSeen DEFAULT (SYSUTCDATETIME())
    );
    /* Dynamic: this script is one batch, compiled whole before it runs, and
       on a database with the older DimAgent a static reference to
       AgentSource would fail to compile before the ALTER below could add it. */
    EXEC (N'INSERT INTO bi.DimAgent (AgentId, AgentName, AgentSource) VALUES (N''(none)'', N''(no agent)'', N''(no agent)'');');
END;

/* AgentSource arrived after the first bi schema; add it in place. */
IF COL_LENGTH(N'bi.DimAgent', N'AgentSource') IS NULL
BEGIN
    ALTER TABLE bi.DimAgent ADD AgentSource NVARCHAR(30) NULL;
    EXEC (N'UPDATE bi.DimAgent SET AgentSource = N''(no agent)'' WHERE AgentId = N''(none)'';');
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

/* ---- 2.2.0: what the rest of the record says ----------------------------- */

/* CopilotEventData.LicenseType: the licence the record itself says the user
   had (Premium, Starter ...). Beside LicenceStatus, which comes from the
   licence history, not the record. */
IF OBJECT_ID(N'bi.DimLicenseType', N'U') IS NULL
BEGIN
    CREATE TABLE bi.DimLicenseType
    (
        LicenseTypeKey  INT IDENTITY(1, 1) NOT NULL CONSTRAINT PK_DimLicenseType PRIMARY KEY,
        LicenseType     NVARCHAR(32) NOT NULL CONSTRAINT UQ_DimLicenseType UNIQUE
    );
    INSERT INTO bi.DimLicenseType (LicenseType) VALUES (N'(not reported)');
END;

/* Sensitivity labels, by id: on the interaction (CopilotEventData.SensitivityLabelId)
   and on the resources it read. Names are not in the audit data; the
   dashboard's Admin > Reference data holds them (dash.SensitivityLabel). */
IF OBJECT_ID(N'bi.DimSensitivityLabel', N'U') IS NULL
BEGIN
    CREATE TABLE bi.DimSensitivityLabel
    (
        SensitivityLabelKey  INT IDENTITY(1, 1) NOT NULL CONSTRAINT PK_DimSensitivityLabel PRIMARY KEY,
        LabelId              UNIQUEIDENTIFIER NULL,   -- NULL: the '(none)' row
        FirstSeenUtc         DATETIME2(0) NOT NULL CONSTRAINT DF_DimSensitivityLabel_FirstSeen DEFAULT (SYSUTCDATETIME())
    );
    CREATE UNIQUE INDEX UQ_DimSensitivityLabel ON bi.DimSensitivityLabel (LabelId);
    INSERT INTO bi.DimSensitivityLabel (LabelId) VALUES (NULL);
END;

/* AccessedResources[].Type: File, EmailMessage, TeamsMessage, WebSearchQuery, a URL ... */
IF OBJECT_ID(N'bi.DimResourceType', N'U') IS NULL
BEGIN
    CREATE TABLE bi.DimResourceType
    (
        ResourceTypeKey  INT IDENTITY(1, 1) NOT NULL CONSTRAINT PK_DimResourceType PRIMARY KEY,
        ResourceType     NVARCHAR(200) NOT NULL CONSTRAINT UQ_DimResourceType UNIQUE
    );
    INSERT INTO bi.DimResourceType (ResourceType) VALUES (N'(not reported)');
END;

/* AccessedResources[].Action: Read, Create ... */
IF OBJECT_ID(N'bi.DimResourceAction', N'U') IS NULL
BEGIN
    CREATE TABLE bi.DimResourceAction
    (
        ResourceActionKey  INT IDENTITY(1, 1) NOT NULL CONSTRAINT PK_DimResourceAction PRIMARY KEY,
        ResourceAction     NVARCHAR(200) NOT NULL CONSTRAINT UQ_DimResourceAction UNIQUE
    );
    INSERT INTO bi.DimResourceAction (ResourceAction) VALUES (N'(not reported)');
END;

/* The site a resource lives on: https://host/sites/name (or /teams/, /personal/),
   otherwise the host. Whole URLs are left out on purpose: they are nearly one
   per row and would cost more than the rest of the model. */
IF OBJECT_ID(N'bi.DimSite', N'U') IS NULL
BEGIN
    CREATE TABLE bi.DimSite
    (
        SiteKey       INT IDENTITY(1, 1) NOT NULL CONSTRAINT PK_DimSite PRIMARY KEY,
        Site          NVARCHAR(400) NOT NULL CONSTRAINT UQ_DimSite UNIQUE,
        Host          NVARCHAR(400) NOT NULL,
        IsPersonal    BIT           NOT NULL,   -- a OneDrive (/personal/) site: it names its owner
        FirstSeenUtc  DATETIME2(0)  NOT NULL CONSTRAINT DF_DimSite_FirstSeen DEFAULT (SYSUTCDATETIME())
    );
    INSERT INTO bi.DimSite (Site, Host, IsPersonal) VALUES (N'(none)', N'(none)', 0);
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

/* 2.2.0 columns, added in place; rows written before them carry the defaults
   until the next full rebuild. */
IF COL_LENGTH(N'bi.FactInteraction', N'LicenseTypeKey') IS NULL
    ALTER TABLE bi.FactInteraction ADD
        LicenseTypeKey         INT     NOT NULL CONSTRAINT DF_FactInteraction_LicenseType DEFAULT (1),
        SensitivityLabelKey    INT     NOT NULL CONSTRAINT DF_FactInteraction_Label DEFAULT (1),   -- the interaction's own label
        MessageCount           INT     NOT NULL CONSTRAINT DF_FactInteraction_Messages DEFAULT (0),
        MemoryUpdated          BIT     NOT NULL CONSTRAINT DF_FactInteraction_Memory DEFAULT (0),
        DlpEvaluationDeferred  BIT     NOT NULL CONSTRAINT DF_FactInteraction_Dlp DEFAULT (0),
        CalledAnotherAgent     BIT     NOT NULL CONSTRAINT DF_FactInteraction_Target DEFAULT (0);   -- TargetAgentName set

/*
    One row per resource an interaction read (CopilotEventData.AccessedResources),
    from audit.RawLogAccessedResource: what kind, which site, which label,
    whether it was blocked or carried a prompt injection. Clustered
    columnstore, refreshed with the interactions it belongs to.
*/
IF OBJECT_ID(N'bi.FactAccessedResource', N'U') IS NULL
BEGIN
    CREATE TABLE bi.FactAccessedResource
    (
        RecordId             NVARCHAR(450) NOT NULL,   -- lineage; not imported
        Ordinal              SMALLINT      NOT NULL,   -- lineage; not imported
        CreatedUtc           DATETIME2(0)  NOT NULL,   -- lineage and the refresh window; not imported
        ActivityHour         DATETIME2(0)  NOT NULL,
        DateKey              INT           NOT NULL,
        UserKey              INT           NOT NULL,
        AppKey               INT           NOT NULL,
        ResourceTypeKey      INT           NOT NULL,
        ResourceActionKey    INT           NOT NULL,
        SiteKey              INT           NOT NULL,
        SensitivityLabelKey  INT           NOT NULL,
        IsBlocked            BIT           NOT NULL,
        XPIADetected         BIT           NOT NULL
    );
    CREATE CLUSTERED COLUMNSTORE INDEX CCI_FactAccessedResource ON bi.FactAccessedResource;
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
        SessionRows           INT            NOT NULL,   -- written by this run
        LicenceChangeRows     INT            NOT NULL,
        LicensedDayRows       INT            NOT NULL,
        SessionRunId          INT            NULL        -- the latest audit.CopilotSessionRun it reflects
    );

/* Incremental sessions: which session build a refresh caught up to. */
IF COL_LENGTH(N'bi.RefreshRun', N'SessionRunId') IS NULL
    ALTER TABLE bi.RefreshRun ADD SessionRunId INT NULL;

/* ---- What the model imports ---------------------------------------------- */

IF COL_LENGTH(N'bi.RefreshRun', N'AccessedResourceRows') IS NULL
    ALTER TABLE bi.RefreshRun ADD AccessedResourceRows BIGINT NULL;

EXEC (N'CREATE OR ALTER VIEW bi.Interactions AS
SELECT ActivityHour, DateKey, HourOfDay, UserKey, AppKey, AgentKey, ModelKey, LicenceStatusKey,
       LicenseTypeKey, SensitivityLabelKey,
       PromptCount, MessageCount, AccessedResourceCount, UsedWebSearch, JailbreakDetected, PromptInjectionDetected,
       HasBlockedResource, HasSensitivityLabel, MemoryUpdated, DlpEvaluationDeferred, CalledAnotherAgent
FROM bi.FactInteraction;');

EXEC (N'CREATE OR ALTER VIEW bi.AccessedResources AS
SELECT ActivityHour, DateKey, UserKey, AppKey, ResourceTypeKey, ResourceActionKey, SiteKey, SensitivityLabelKey,
       IsBlocked, XPIADetected
FROM bi.FactAccessedResource;');

EXEC (N'CREATE OR ALTER VIEW bi.LicenseTypes AS
SELECT LicenseTypeKey, LicenseType FROM bi.DimLicenseType;');

EXEC (N'CREATE OR ALTER VIEW bi.SensitivityLabels AS
SELECT SensitivityLabelKey, COALESCE(LOWER(CAST(LabelId AS NVARCHAR(36))), N''(none)'') AS LabelId FROM bi.DimSensitivityLabel;');

EXEC (N'CREATE OR ALTER VIEW bi.ResourceTypes AS
SELECT ResourceTypeKey, ResourceType FROM bi.DimResourceType;');

EXEC (N'CREATE OR ALTER VIEW bi.ResourceActions AS
SELECT ResourceActionKey, ResourceAction FROM bi.DimResourceAction;');

EXEC (N'CREATE OR ALTER VIEW bi.Sites AS
SELECT SiteKey, Site, Host, IsPersonal FROM bi.DimSite;');

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
       Country, CompanyName, EmployeeType, IsManager, AccountEnabled, ManagerUpn, ManagerName, OrgDepth,
       OrgLevel1, OrgLevel2, OrgLevel3, OrgLevel4, OrgLevel5, OrgLevel6, OrgLevel7, OrgLevel8, OrgLevel9, OrgLevel10, OrgLevel11, OrgLevel12, OrgLevel13, OrgLevel14, OrgLevel15,
       DirectoryStatus, CopilotLicenceToday, CopilotSku, LicensedSinceUtc
FROM bi.DimUser;');

EXEC (N'CREATE OR ALTER VIEW bi.Apps AS
SELECT AppKey, App, AppCategory FROM bi.DimApp;');

EXEC (N'CREATE OR ALTER VIEW bi.Agents AS
SELECT AgentKey, AgentId, AgentName, AgentSource FROM bi.DimAgent;');

EXEC (N'CREATE OR ALTER VIEW bi.Models AS
SELECT ModelKey, ModelProvider, ModelName FROM bi.DimModel;');

EXEC (N'CREATE OR ALTER VIEW bi.Skus AS
SELECT SkuKey, Sku, IsCopilot FROM bi.DimSku;');

EXEC (N'CREATE OR ALTER VIEW bi.LicenceStatuses AS
SELECT LicenceStatusKey, LicenceStatus FROM bi.DimLicenceStatus;');
