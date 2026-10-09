/*
    The bi schema: a star schema for a Power BI semantic model
    ------------------------------------------------------------------------
    The audit tables are shaped for loading and auditing, not for a model: the
    flattened table has a row-sized text column for every list, users are UPN
    strings, and license history lives in other databases. A semantic model
    over 20-40M interactions wants narrow facts with integer keys, small
    dimensions, and nothing high-cardinality it does not need. This is that
    layer. bi.usp_RefreshBi (Create-CopilotBiProcedure.sql) fills it, and each
    -Incremental run calls it after the other derived tables.

      Facts                          one row per
        bi.FactInteraction           Copilot interaction (the big one)
        bi.FactSession               inferred session (audit.CopilotSession)
        bi.FactLicenseChange         SKU added to or removed from a user
        bi.FactLicensedDay           user holding a Copilot license, per day

      Dimensions
        bi.DimDate                   with the Saturday-Friday week of the rollup
        bi.DimUser                   AD attributes, the org chain, today's license
        bi.DimApp                    app and its Copilot Chat / In-App category
        bi.DimAgent, bi.DimModel, bi.DimSku, bi.DimLicenseStatus

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
        /* The weekly rollup's week: Saturday to Friday, labeled by the
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
        /* Today's license, from the enrichment. Licensed | Not licensed | Unknown.
           License at the time of use is on the facts, not here. */
        CopilotLicenseToday  NVARCHAR(12)   NOT NULL CONSTRAINT DF_DimUser_License DEFAULT (N'Unknown'),
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
    The agent an interaction went to. A record names its agent in AppIdentity:
    Copilot.Studio.<environment>-<tenant id>-<schema name> or
    Copilot.Studio.Declarative.T_<id> for a Copilot Studio agent,
    MicrosoftAgent.<name>.<...> for one of Microsoft's. Some records carry an
    AgentId as well and some of the same agent's do not (seen on two live
    tenants), so the agent is the one AppIdentity names, and AgentId only where
    AppIdentity names none (2.4.11; before, AgentId came first, which no real
    record had). AgentSource says which: bi.fnAgentIdentity, below, is the rule.

    AgentName is a label, not the identity: the friendly name the audit log
    gave the agent on its latest record that carries one ($.AgentName, kept for
    every agent in audit.AgentCatalog), else a name read from the id. A maker
    renaming an agent changes the label of the same AgentKey, so its history
    stays in one piece. bi.usp_NameAgents sets it, at the end of every refresh.
*/
IF OBJECT_ID(N'bi.DimAgent', N'U') IS NULL
BEGIN
    CREATE TABLE bi.DimAgent
    (
        AgentKey      INT IDENTITY(1, 1) NOT NULL CONSTRAINT PK_DimAgent PRIMARY KEY,
        AgentId       NVARCHAR(400) NOT NULL CONSTRAINT UQ_DimAgent_Id UNIQUE,   -- the AppIdentity that names the agent, else AgentId
        AgentName     NVARCHAR(400) NULL,   -- the latest friendly name the audit log gave it, else one read from the id
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

/* 2.4.11: whether AgentName is the audit log's own name for the agent (1), or one read from its id (0). */
IF COL_LENGTH(N'bi.DimAgent', N'NamedInAudit') IS NULL
    ALTER TABLE bi.DimAgent ADD NamedInAudit BIT NOT NULL CONSTRAINT DF_DimAgent_NamedInAudit DEFAULT (0);

/*
    2.6.3: one agent, two identities. The audit log names some agents two ways: a Copilot Studio agent made in
    Microsoft 365 Copilot is Copilot.Studio.Default-<tenant>-<its id> on some records and
    Copilot.Studio.Declarative.T_<its title id> on others, and nothing in a record says they are one agent. The agent
    workbook does: one row holds both ids. tools/Import-AgentWorkbook.ps1 sets MergedIntoKey on the row of the second
    identity, to the key of the agent it is another name for, and bi.usp_MergeAgents moves its interactions there.
    bi.usp_MergeAgents sets it itself for an id written with and without its prefix (a SharePoint agent) and for the
    variants of one of Microsoft's own agents, which need no workbook.
    From then on the agent is the one row with no MergedIntoKey: the refresh writes an interaction that arrives under
    either identity with that key, and bi.Agents and dash.Agent list it once. The row of the second identity stays,
    as the record of the name. NULL everywhere without a workbook.
*/
IF COL_LENGTH(N'bi.DimAgent', N'MergedIntoKey') IS NULL
    ALTER TABLE bi.DimAgent ADD MergedIntoKey INT NULL;

/*
    bi.fnAgentIdentity: who the agent of a record is, from its AppIdentity and
    AgentId. One row: the id bi.DimAgent keys on, where it came from, and the
    name to show when the audit log gives none.

      AppIdentity Copilot.Studio.<...>   that AppIdentity, 'Copilot Studio'. The name is the schema name after the
                                         tenant id (Copilot.Studio.Default-<tenant id>-crd20_expenseClaims gives
                                         crd20_expenseClaims); where the identity ends in an id instead
                                         (Copilot.Studio.Declarative.T_<id>), everything after Copilot.Studio.
      AppIdentity MicrosoftAgent.<...>   that AppIdentity, 'Microsoft'. The name is Microsoft's own, the part after
                                         MicrosoftAgent. (MicrosoftAgent.OutlookDraftsChatSteering.BuiltIn_<...>
                                         gives OutlookDraftsChatSteering).
      any other AppIdentity, an AgentId  the AgentId, 'Agent ID', no name of its own.
      neither                            '(none)', '(no agent)': Copilot itself.
*/
EXEC (N'CREATE OR ALTER FUNCTION bi.fnAgentIdentity (@AppIdentity NVARCHAR(400), @AgentId NVARCHAR(400))
RETURNS TABLE
AS
RETURN
SELECT COALESCE(CASE WHEN s.Src IS NOT NULL THEN a.Ident END, a.Aid, N''(none)'') AS AgentId,
       COALESCE(s.Src, CASE WHEN a.Aid IS NOT NULL THEN N''Agent ID'' END, N''(no agent)'') AS AgentSource,
       CAST(NULLIF(CASE s.Src
            WHEN N''Copilot Studio'' THEN
                CASE WHEN CHARINDEX(N''-'', r.Rest) > 0 AND r.Tail NOT LIKE REPLICATE(N''[0-9a-fA-F]'', 12) THEN r.Tail ELSE r.Rest END
            WHEN N''Microsoft'' THEN
                CASE WHEN CHARINDEX(N''.'', r.Rest) > 1 THEN LEFT(r.Rest, CHARINDEX(N''.'', r.Rest) - 1) ELSE r.Rest END
            END, N'''') AS NVARCHAR(400)) AS DerivedName
FROM (SELECT NULLIF(LTRIM(RTRIM(@AgentId)), N'''') AS Aid, NULLIF(LTRIM(RTRIM(@AppIdentity)), N'''') AS Ident) AS a
CROSS APPLY (SELECT CASE WHEN a.Ident LIKE N''Copilot.Studio.%''  THEN N''Copilot Studio''
                         WHEN a.Ident LIKE N''MicrosoftAgent.%''  THEN N''Microsoft'' END AS Src) AS s
/* Both prefixes are 15 characters. Rest: what follows the prefix. Tail: what follows the last hyphen. */
CROSS APPLY (SELECT STUFF(a.Ident, 1, 15, N'''') AS Rest,
                    CASE WHEN CHARINDEX(N''-'', REVERSE(a.Ident)) > 1 THEN RIGHT(a.Ident, CHARINDEX(N''-'', REVERSE(a.Ident)) - 1) END AS Tail) AS r;');

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

/* License held at the time of use. Unknown is kept apart from Not licensed:
   a user who has left the directory, with no license history, is not known
   to have been unlicensed. */
IF OBJECT_ID(N'bi.DimLicenseStatus', N'U') IS NULL
BEGIN
    CREATE TABLE bi.DimLicenseStatus
    (
        LicenseStatusKey  TINYINT      NOT NULL CONSTRAINT PK_DimLicenseStatus PRIMARY KEY,
        LicenseStatus     NVARCHAR(20) NOT NULL
    );
    INSERT INTO bi.DimLicenseStatus (LicenseStatusKey, LicenseStatus)
    VALUES (0, N'Unknown'), (1, N'Licensed'), (2, N'Not licensed');
END;

/* ---- 2.2.0: what the rest of the record says ----------------------------- */

/* CopilotEventData.LicenseType: the license the record itself says the user
   had (Premium, Starter ...). Beside LicenseStatus, which comes from the
   license history, not the record. */
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

/* What a resource Copilot read was, as a person would name it (2.4.6): a file's extension (docx, xlsx, pptx, csv ...)
   from AccessedResources[].Name, and for anything else its Type (EmailMessage, TeamsMessage, StreamVideo, whiteboard ...). */
IF OBJECT_ID(N'bi.DimFileType', N'U') IS NULL
BEGIN
    CREATE TABLE bi.DimFileType
    (
        FileTypeKey  INT IDENTITY(1, 1) NOT NULL CONSTRAINT PK_DimFileType PRIMARY KEY,
        FileType     NVARCHAR(64) NOT NULL CONSTRAINT UQ_DimFileType UNIQUE
    );
    INSERT INTO bi.DimFileType (FileType) VALUES (N'(not reported)');
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
        ResourceAction     NVARCHAR(450) NOT NULL CONSTRAINT UQ_DimResourceAction UNIQUE   -- as wide as audit.RawLogAccessedResource.[Action]
    );
    INSERT INTO bi.DimResourceAction (ResourceAction) VALUES (N'(not reported)');
END;
/* 2.4.1: 200 was narrower than the flattened [Action] (450), so a longer action failed the refresh. Widened in place. */
IF COL_LENGTH(N'bi.DimResourceAction', N'ResourceAction') < 900
BEGIN
    ALTER TABLE bi.DimResourceAction DROP CONSTRAINT UQ_DimResourceAction;
    ALTER TABLE bi.DimResourceAction ALTER COLUMN ResourceAction NVARCHAR(450) NOT NULL;
    ALTER TABLE bi.DimResourceAction ADD CONSTRAINT UQ_DimResourceAction UNIQUE (ResourceAction);
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
        LicenseStatusKey         TINYINT       NOT NULL,
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

/*
    PremiumUse and UseCaseKey (2.4.0), set by bi.usp_RefreshBi and nowhere else: every other place
    (the dashboard's rollups and pages, the bi views, Power BI) reads these columns, so each rule
    has one definition.
      PremiumUse  a licensed interaction that needed the Premium license (see UseKind, below).
      UseCaseKey  AppKey x 4 + web search x 2 + agent: the key into bi.UseCases.
    An app's category changes PremiumUse, so set categories before a build, or rebuild bi after.

    UseKind (2.4.8): what the record itself says about where Copilot ran, from its AppIdentity, and
    whether it read work content. NULL on a row written before 2.4.8 (the installer rebuilds bi once).
      0  not licensed use: web Copilot Chat (Copilot.M365Copilot.WebChat) that read no work content,
         or a Copilot Studio agent (Copilot.Studio.*), which can be metered without a license
      1  licensed use: Copilot inside an app (Copilot.M365Copilot.Apps), Copilot Chat in work mode
         (Copilot.M365Copilot.Bizchat) whether or not it read anything, a Microsoft agent
         (MicrosoftAgent.*), or web Copilot Chat that read work content
      2  the record names no such identity, and it read no work content: the app's category decides
         (In-App Copilot other than Copilot Studio is licensed use; Copilot Chat is not)
      3  as 2, but it read work content: licensed use in Copilot Chat too
    Work content is a resource in SharePoint or OneDrive, Outlook or Teams, or a mail, a Teams
    message, a meeting or a connector item. The web pages web search cites are listed as resources
    too, which is why "read a resource" alone says nothing.
    PremiumUse = licensed at the time AND (UseKind 1, or UseKind 2 or 3 in an in-app category, or
    UseKind 3 in Copilot Chat).
*/
IF COL_LENGTH(N'bi.FactInteraction', N'PremiumUse') IS NULL
    ALTER TABLE bi.FactInteraction ADD
        PremiumUse  BIT NOT NULL CONSTRAINT DF_FactInteraction_Premium DEFAULT (0),
        UseCaseKey  INT NOT NULL CONSTRAINT DF_FactInteraction_UseCase DEFAULT (0);
IF COL_LENGTH(N'bi.FactInteraction', N'UseKind') IS NULL
    ALTER TABLE bi.FactInteraction ADD UseKind TINYINT NULL;

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

/* The agent called, by name: the dashboard's hand-off report reads it here rather
   than joining 40M facts back to audit.RawLogFlattened. */
IF COL_LENGTH(N'bi.FactInteraction', N'TargetAgentName') IS NULL
    ALTER TABLE bi.FactInteraction ADD TargetAgentName NVARCHAR(400) NULL;

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
        XPIADetected         BIT           NOT NULL,
        FileTypeKey          INT           NOT NULL CONSTRAINT DF_FactAccessedResource_FileType DEFAULT (1)   -- 2.4.6
    );
    CREATE CLUSTERED COLUMNSTORE INDEX CCI_FactAccessedResource ON bi.FactAccessedResource;
END;
/* The file type (2.4.6): rows loaded before it are '(not reported)' until the installer refreshes bi in full once. */
IF COL_LENGTH(N'bi.FactAccessedResource', N'FileTypeKey') IS NULL
    ALTER TABLE bi.FactAccessedResource ADD FileTypeKey INT NOT NULL CONSTRAINT DF_FactAccessedResource_FileType DEFAULT (1);

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
        LicenseStatusKey     TINYINT       NOT NULL,
        PromptCount          INT           NOT NULL,
        SpanMinutes          DECIMAL(12, 2) NOT NULL,
        IsMultiTurn          BIT           NOT NULL
    );

IF OBJECT_ID(N'bi.FactLicenseChange', N'U') IS NULL
    CREATE TABLE bi.FactLicenseChange
    (
        RecordId     VARCHAR(64)    NOT NULL,   -- lineage; not imported
        ChangedUtc   DATETIME2(0)   NOT NULL,
        DateKey      INT            NOT NULL,
        UserKey      INT            NOT NULL,   -- whose license changed
        SkuKey       INT            NOT NULL,
        Change       SMALLINT       NOT NULL,   -- +1 added, -1 removed
        ChangedBy    NVARCHAR(512)  NULL,       -- the admin who made it
        CONSTRAINT PK_FactLicenseChange PRIMARY KEY (RecordId, SkuKey, Change)
    );

/* The adoption denominator: one row per user per day they held a Copilot
   license at the end of that day. Active licensed users / licensed users,
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
        LicenseEventDatabase  NVARCHAR(1000) NULL,
        InteractionRows       BIGINT         NOT NULL,   -- written by this run
        InteractionsSkipped   BIGINT         NOT NULL,   -- no timestamp
        SessionRows           INT            NOT NULL,   -- written by this run
        LicenseChangeRows     INT            NOT NULL,
        LicensedDayRows       INT            NOT NULL,
        SessionRunId          INT            NULL        -- the latest audit.CopilotSessionRun it reflects
    );

/*
    2.4.9: a full rebuild empties bi.FactInteraction and fills it month by month, outside a transaction (it is
    too large for one). Stopped part way, it leaves the months it did not reach missing, and nothing said so.
    bi.usp_RefreshBi puts a row here before it empties the fact and takes it out when it has finished; while
    a row is here, every refresh is a full one.
*/
IF OBJECT_ID(N'bi.RebuildInProgress', N'U') IS NULL
    CREATE TABLE bi.RebuildInProgress (StartedUtc DATETIME2(0) NOT NULL);

/* Incremental sessions: which session build a refresh caught up to. */
IF COL_LENGTH(N'bi.RefreshRun', N'SessionRunId') IS NULL
    ALTER TABLE bi.RefreshRun ADD SessionRunId INT NULL;

/* ---- What the model imports ---------------------------------------------- */

IF COL_LENGTH(N'bi.RefreshRun', N'AccessedResourceRows') IS NULL
    ALTER TABLE bi.RefreshRun ADD AccessedResourceRows BIGINT NULL;

/* 2.4.0: the first day with an interaction in an app whose category this run changed (NULL: none).
   Premium use and the dashboard's Copilot Chat counts depend on the category, so the dashboard's
   next refresh rolls up again from that day. */
IF COL_LENGTH(N'bi.RefreshRun', N'RecategorizedFromDateKey') IS NULL
    ALTER TABLE bi.RefreshRun ADD RecategorizedFromDateKey INT NULL;

/*
    PremiumUse (2.3.0; the rule is bi.FactInteraction.UseKind's since 2.4.8): a licensed interaction that needed the Premium license - Copilot inside Word, Excel,
    PowerPoint, Outlook or Teams (Copilot Studio agents can be metered without one), or Copilot Chat reading
    work content (files, mail, sites) rather than the web. The same rule as dash.UserDay.PremiumUse, which the
    dashboard's train and revoke recommendations use. UseCaseKey (2.3.0): the app, whether it searched the web,
    and whether an agent answered, as one key into bi.UseCases (the dashboard's use-case rules).
*/
EXEC (N'CREATE OR ALTER VIEW bi.Interactions AS
SELECT f.ActivityHour, f.DateKey, f.HourOfDay, f.UserKey, f.AppKey, f.AgentKey, f.ModelKey, f.LicenseStatusKey,
       f.LicenseTypeKey, f.SensitivityLabelKey,
       f.PromptCount, f.MessageCount, f.AccessedResourceCount, f.UsedWebSearch, f.JailbreakDetected, f.PromptInjectionDetected,
       f.HasBlockedResource, f.HasSensitivityLabel, f.MemoryUpdated, f.DlpEvaluationDeferred, f.CalledAnotherAgent,
       f.PremiumUse, f.UseCaseKey
FROM bi.FactInteraction AS f;');

EXEC (N'CREATE OR ALTER VIEW bi.AccessedResources AS
SELECT ActivityHour, DateKey, UserKey, AppKey, ResourceTypeKey, ResourceActionKey, SiteKey, SensitivityLabelKey,
       IsBlocked, XPIADetected, FileTypeKey
FROM bi.FactAccessedResource;');

EXEC (N'CREATE OR ALTER VIEW bi.FileTypes AS
SELECT FileTypeKey, FileType FROM bi.DimFileType;');

EXEC (N'CREATE OR ALTER VIEW bi.LicenseTypes AS
SELECT LicenseTypeKey, LicenseType FROM bi.DimLicenseType;');

EXEC (N'CREATE OR ALTER VIEW bi.SensitivityLabels AS
SELECT SensitivityLabelKey, COALESCE(LOWER(CAST(LabelId AS NVARCHAR(36))), N''(none)'') AS LabelId FROM bi.DimSensitivityLabel;');

EXEC (N'CREATE OR ALTER VIEW bi.ResourceTypes AS
SELECT ResourceTypeKey, ResourceType FROM bi.DimResourceType;');

EXEC (N'CREATE OR ALTER VIEW bi.ResourceActions AS
SELECT ResourceActionKey, ResourceAction FROM bi.DimResourceAction;');

/* A OneDrive site is named after its owner (/personal/firstname_lastname_...): hidden in privacy mode. */
EXEC (N'CREATE OR ALTER VIEW bi.Sites AS
SELECT SiteKey, CASE WHEN IsPersonal = 1 AND EXISTS (SELECT 1 FROM audit.PipelineSetting WHERE Name = N''PrivacyMode'' AND Value = N''Pseudonymize'') THEN N''OneDrive (personal)'' ELSE Site END AS Site, Host, IsPersonal
FROM bi.DimSite;');

EXEC (N'CREATE OR ALTER VIEW bi.Sessions AS
SELECT SessionKey, SessionStart, SessionEnd, DateKey, UserKey, AppKey, LicenseStatusKey,
       PromptCount, SpanMinutes, IsMultiTurn
FROM bi.FactSession;');

/* Copilot Studio authoring: one row per create, publish, share, update, delete or component
   change, copied whole from every license database's audit.AgentEvent at each refresh (2.3.0). */
IF OBJECT_ID(N'bi.FactAgentEvent', N'U') IS NULL
    CREATE TABLE bi.FactAgentEvent
    (
        RecordId      VARCHAR(64)     NOT NULL CONSTRAINT PK_FactAgentEvent PRIMARY KEY,
        EventUtc      DATETIME2(0)    NOT NULL,
        DateKey       INT             NOT NULL,
        Action        VARCHAR(16)     NOT NULL,
        Operation     NVARCHAR(100)   NOT NULL,
        ActorUpn      NVARCHAR(512)   NULL,
        ActorKey      INT             NULL,       -- bi.DimUser, where the maker is in it
        BotId         NVARCHAR(100)   NULL,
        BotSchemaName NVARCHAR(200)   NULL,
        ComponentName NVARCHAR(200)   NULL,
        ComponentType NVARCHAR(100)   NULL,
        INDEX IX_FactAgentEvent_Date (DateKey)
    );
EXEC (N'CREATE OR ALTER VIEW bi.AgentEvents AS
SELECT RecordId, EventUtc, DateKey, Action, Operation, ActorKey, BotId, BotSchemaName, ComponentName, ComponentType
FROM bi.FactAgentEvent;');

/* ChangedBy names the administrator: blank in privacy mode. */
EXEC (N'CREATE OR ALTER VIEW bi.LicenseChanges AS
SELECT ChangedUtc, DateKey, UserKey, SkuKey, Change, CASE WHEN EXISTS (SELECT 1 FROM audit.PipelineSetting WHERE Name = N''PrivacyMode'' AND Value = N''Pseudonymize'') THEN NULL ELSE ChangedBy END AS ChangedBy
FROM bi.FactLicenseChange;');

EXEC (N'CREATE OR ALTER VIEW bi.LicensedDays AS
SELECT DateKey, UserKey FROM bi.FactLicensedDay;');

EXEC (N'CREATE OR ALTER VIEW bi.Calendar AS
SELECT DateKey, [Date], [Year], [Quarter], MonthNumber, [MonthName], YearMonth, DayOfWeekNumber,
       DayOfWeekName, IsWeekday, WeekEnding, WeekYear, WeekNumber, WeekLabel
FROM bi.DimDate;');

/*
    People, with every name and sign-in pseudonymized when privacy mode is on
    (audit.PipelineSetting PrivacyMode = 'Pseudonymize', set in the dashboard's
    Admin > Privacy and retention): a salted hash, the same for the same person
    everywhere, so counts and org slicing still work but nobody is named.
*/
EXEC (N'CREATE OR ALTER VIEW bi.Users AS
SELECT u.UserKey,
       CASE WHEN pm.Pseudonymize = 1 AND u.UserPrincipalName IS NOT NULL THEN N''P-'' + CONVERT(NVARCHAR(16), HASHBYTES(''SHA2_256'', pm.Salt + LOWER(u.UserPrincipalName)), 2) ELSE u.UserPrincipalName END AS UserPrincipalName,
       CASE WHEN pm.Pseudonymize = 1 AND u.DisplayName IS NOT NULL THEN N''P-'' + CONVERT(NVARCHAR(16), HASHBYTES(''SHA2_256'', pm.Salt + LOWER(u.DisplayName)), 2) ELSE u.DisplayName END AS DisplayName,
       CASE WHEN pm.Pseudonymize = 1 AND u.ManagerUpn IS NOT NULL THEN N''P-'' + CONVERT(NVARCHAR(16), HASHBYTES(''SHA2_256'', pm.Salt + LOWER(u.ManagerUpn)), 2) ELSE u.ManagerUpn END AS ManagerUpn,
       CASE WHEN pm.Pseudonymize = 1 AND u.ManagerName IS NOT NULL THEN N''P-'' + CONVERT(NVARCHAR(16), HASHBYTES(''SHA2_256'', pm.Salt + LOWER(u.ManagerName)), 2) ELSE u.ManagerName END AS ManagerName,
       u.Division, u.Department, u.JobTitle, u.OfficeLocation, u.City,
       u.Country, u.CompanyName, u.EmployeeType, u.IsManager, u.AccountEnabled, u.OrgDepth,
       CASE WHEN pm.Pseudonymize = 1 AND u.OrgLevel1 IS NOT NULL THEN N''P-'' + CONVERT(NVARCHAR(16), HASHBYTES(''SHA2_256'', pm.Salt + LOWER(u.OrgLevel1)), 2) ELSE u.OrgLevel1 END AS OrgLevel1,
       CASE WHEN pm.Pseudonymize = 1 AND u.OrgLevel2 IS NOT NULL THEN N''P-'' + CONVERT(NVARCHAR(16), HASHBYTES(''SHA2_256'', pm.Salt + LOWER(u.OrgLevel2)), 2) ELSE u.OrgLevel2 END AS OrgLevel2,
       CASE WHEN pm.Pseudonymize = 1 AND u.OrgLevel3 IS NOT NULL THEN N''P-'' + CONVERT(NVARCHAR(16), HASHBYTES(''SHA2_256'', pm.Salt + LOWER(u.OrgLevel3)), 2) ELSE u.OrgLevel3 END AS OrgLevel3,
       CASE WHEN pm.Pseudonymize = 1 AND u.OrgLevel4 IS NOT NULL THEN N''P-'' + CONVERT(NVARCHAR(16), HASHBYTES(''SHA2_256'', pm.Salt + LOWER(u.OrgLevel4)), 2) ELSE u.OrgLevel4 END AS OrgLevel4,
       CASE WHEN pm.Pseudonymize = 1 AND u.OrgLevel5 IS NOT NULL THEN N''P-'' + CONVERT(NVARCHAR(16), HASHBYTES(''SHA2_256'', pm.Salt + LOWER(u.OrgLevel5)), 2) ELSE u.OrgLevel5 END AS OrgLevel5,
       CASE WHEN pm.Pseudonymize = 1 AND u.OrgLevel6 IS NOT NULL THEN N''P-'' + CONVERT(NVARCHAR(16), HASHBYTES(''SHA2_256'', pm.Salt + LOWER(u.OrgLevel6)), 2) ELSE u.OrgLevel6 END AS OrgLevel6,
       CASE WHEN pm.Pseudonymize = 1 AND u.OrgLevel7 IS NOT NULL THEN N''P-'' + CONVERT(NVARCHAR(16), HASHBYTES(''SHA2_256'', pm.Salt + LOWER(u.OrgLevel7)), 2) ELSE u.OrgLevel7 END AS OrgLevel7,
       CASE WHEN pm.Pseudonymize = 1 AND u.OrgLevel8 IS NOT NULL THEN N''P-'' + CONVERT(NVARCHAR(16), HASHBYTES(''SHA2_256'', pm.Salt + LOWER(u.OrgLevel8)), 2) ELSE u.OrgLevel8 END AS OrgLevel8,
       CASE WHEN pm.Pseudonymize = 1 AND u.OrgLevel9 IS NOT NULL THEN N''P-'' + CONVERT(NVARCHAR(16), HASHBYTES(''SHA2_256'', pm.Salt + LOWER(u.OrgLevel9)), 2) ELSE u.OrgLevel9 END AS OrgLevel9,
       CASE WHEN pm.Pseudonymize = 1 AND u.OrgLevel10 IS NOT NULL THEN N''P-'' + CONVERT(NVARCHAR(16), HASHBYTES(''SHA2_256'', pm.Salt + LOWER(u.OrgLevel10)), 2) ELSE u.OrgLevel10 END AS OrgLevel10,
       CASE WHEN pm.Pseudonymize = 1 AND u.OrgLevel11 IS NOT NULL THEN N''P-'' + CONVERT(NVARCHAR(16), HASHBYTES(''SHA2_256'', pm.Salt + LOWER(u.OrgLevel11)), 2) ELSE u.OrgLevel11 END AS OrgLevel11,
       CASE WHEN pm.Pseudonymize = 1 AND u.OrgLevel12 IS NOT NULL THEN N''P-'' + CONVERT(NVARCHAR(16), HASHBYTES(''SHA2_256'', pm.Salt + LOWER(u.OrgLevel12)), 2) ELSE u.OrgLevel12 END AS OrgLevel12,
       CASE WHEN pm.Pseudonymize = 1 AND u.OrgLevel13 IS NOT NULL THEN N''P-'' + CONVERT(NVARCHAR(16), HASHBYTES(''SHA2_256'', pm.Salt + LOWER(u.OrgLevel13)), 2) ELSE u.OrgLevel13 END AS OrgLevel13,
       CASE WHEN pm.Pseudonymize = 1 AND u.OrgLevel14 IS NOT NULL THEN N''P-'' + CONVERT(NVARCHAR(16), HASHBYTES(''SHA2_256'', pm.Salt + LOWER(u.OrgLevel14)), 2) ELSE u.OrgLevel14 END AS OrgLevel14,
       CASE WHEN pm.Pseudonymize = 1 AND u.OrgLevel15 IS NOT NULL THEN N''P-'' + CONVERT(NVARCHAR(16), HASHBYTES(''SHA2_256'', pm.Salt + LOWER(u.OrgLevel15)), 2) ELSE u.OrgLevel15 END AS OrgLevel15,
       u.DirectoryStatus, u.CopilotLicenseToday, u.CopilotSku, u.LicensedSinceUtc
FROM bi.DimUser AS u
CROSS JOIN (SELECT CAST(CASE WHEN EXISTS (SELECT 1 FROM audit.PipelineSetting WHERE Name = N''PrivacyMode'' AND Value = N''Pseudonymize'') THEN 1 ELSE 0 END AS BIT) AS Pseudonymize,
                   COALESCE((SELECT Value FROM audit.PipelineSetting WHERE Name = N''PseudonymSalt''), N'''') AS Salt) AS pm;');

EXEC (N'CREATE OR ALTER VIEW bi.Apps AS
SELECT AppKey, App, AppCategory FROM bi.DimApp;');

/* 2.6.3: an agent the audit log names two ways is listed once, under the identity its interactions are kept with. */
EXEC (N'CREATE OR ALTER VIEW bi.Agents AS
SELECT AgentKey, AgentId, AgentName, AgentSource, NamedInAudit FROM bi.DimAgent WHERE MergedIntoKey IS NULL;');

EXEC (N'CREATE OR ALTER VIEW bi.Models AS
SELECT ModelKey, ModelProvider, ModelName FROM bi.DimModel;');

EXEC (N'CREATE OR ALTER VIEW bi.Skus AS
SELECT SkuKey, Sku, IsCopilot FROM bi.DimSku;');

EXEC (N'CREATE OR ALTER VIEW bi.LicenseStatuses AS
SELECT LicenseStatusKey, LicenseStatus FROM bi.DimLicenseStatus;');
