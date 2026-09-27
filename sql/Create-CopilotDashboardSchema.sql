/*
    Copilot Insights dashboard - schema
    ------------------------------------------------------------------------
    What the web dashboard (dashboard/CopilotInsights) needs on top of the bi
    star schema: who may see what, the settings and reference data its admin
    module edits, an audit log, and two things built by
    dash.usp_RefreshDashboard (Create-CopilotDashboardProcedure.sql):

      dash.Person       one row per person in audit.UserDirectory, with their
                        level below the configured top of the organisation
      dash.OrgClosure   every (manager, person under them) pair at any depth,
                        by UPN - the security model's subtree lookup
      dash.UserDay      per person per day they used Copilot: interactions,
                        prompts, sessions, flags - so the pages read a small
                        rollup instead of scanning the facts, for any period
                        of 7 to 365 days

    The security model (enforced by the app, on every query):
      - A person granted Organization (Admin alone is not enough), the top of the organisation
        (Setting OrgTopUpn) and anyone reporting directly to them see the
        whole organisation.
      - Everyone else sees themselves and everyone under them, at any depth;
        someone with no reports sees only themselves.
      - Named or sensitive detail also needs the page's persona role
        (Adoption, Licensing, Security, Compliance, Agents, Enablement).
    OrgTopUpn is set in the admin module. Until it is, nobody is treated as
    Level 1 or 2 automatically: an account with no manager in AD is as often
    a service account as the CEO.

    Idempotent. Single batch. Requires SQL Server 2017+, and the bi schema
    (Create-CopilotBiSchema.sql) for dash.fnScopeUsers.
*/
SET NOCOUNT ON;

IF SCHEMA_ID(N'dash') IS NULL
    EXEC (N'CREATE SCHEMA dash AUTHORIZATION dbo;');

/* Settings the admin module edits. */
IF OBJECT_ID(N'dash.Setting', N'U') IS NULL
    CREATE TABLE dash.Setting
    (
        Name        VARCHAR(64)     NOT NULL CONSTRAINT PK_DashSetting PRIMARY KEY,
        Value       NVARCHAR(400)   NULL,
        Description NVARCHAR(400)   NULL,
        UpdatedBy   NVARCHAR(256)   NULL,
        UpdatedUtc  DATETIME2(0)    NOT NULL CONSTRAINT DF_DashSetting_Updated DEFAULT (SYSUTCDATETIME())
    );
MERGE dash.Setting AS t
USING (VALUES
    ('OrgTopUpn',    CAST(NULL AS NVARCHAR(400)), N'UPN of the top of the organisation (the CEO). They and their direct reports see the whole organisation. Empty: taken from the org chart (OrgTopUpnDetected).'),
    ('MinGroupSize', N'5',  N'Breakdowns hide groups with fewer people than this, folding them into "Smaller groups", so a group total cannot reveal one person.'),
    ('DefaultDays',  N'30', N'Days shown when a page opens: 7, 14, 30, 90, 180 or 365.'),
    ('InactiveDays', N'30', N'A licensed person with no Copilot use for this many days counts as not using their licence.'),
    ('LicenceMonthlyCost', N'30', N'What one Copilot licence costs a month, for the licence recommendations.'),
    ('LicenceCurrency', N'USD', N'The currency LicenceMonthlyCost is in.')
) AS s (Name, Value, Description)
ON t.Name = s.Name
WHEN NOT MATCHED THEN INSERT (Name, Value, Description) VALUES (s.Name, s.Value, s.Description);

/* Roles granted to people (by UPN) or AD groups (DOMAIN\Group). */
IF OBJECT_ID(N'dash.RoleGrant', N'U') IS NULL
    CREATE TABLE dash.RoleGrant
    (
        GrantId       INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_DashRoleGrant PRIMARY KEY,
        Principal     NVARCHAR(256)   NOT NULL,   -- a UPN, or DOMAIN\Group
        PrincipalType VARCHAR(8)      NOT NULL CONSTRAINT CK_DashRoleGrant_Type CHECK (PrincipalType IN ('User', 'Group')),
        Role          VARCHAR(32)     NOT NULL CONSTRAINT CK_DashRoleGrant_Role CHECK (Role IN
                          ('Admin', 'Organization', 'Adoption', 'Licensing', 'Security', 'Compliance', 'Agents', 'Enablement')),
        Note          NVARCHAR(400)   NULL,
        GrantedBy     NVARCHAR(256)   NOT NULL,
        GrantedUtc    DATETIME2(0)    NOT NULL CONSTRAINT DF_DashRoleGrant_Granted DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT UQ_DashRoleGrant UNIQUE (Principal, Role)
    );

/* Friendly names for agents, which the audit log names by schema name. */
IF OBJECT_ID(N'dash.AgentAlias', N'U') IS NULL
    CREATE TABLE dash.AgentAlias
    (
        AgentKey     INT             NOT NULL CONSTRAINT PK_DashAgentAlias PRIMARY KEY,   -- bi.DimAgent, whose keys are stable
        FriendlyName NVARCHAR(200)   NOT NULL,
        Owner        NVARCHAR(256)   NULL,
        Notes        NVARCHAR(400)   NULL,
        UpdatedBy    NVARCHAR(256)   NOT NULL,
        UpdatedUtc   DATETIME2(0)    NOT NULL CONSTRAINT DF_DashAgentAlias_Updated DEFAULT (SYSUTCDATETIME())
    );

/* Names for sensitivity labels, which the audit log records by ID only. */
IF OBJECT_ID(N'dash.SensitivityLabel', N'U') IS NULL
    CREATE TABLE dash.SensitivityLabel
    (
        LabelId     NVARCHAR(64)    NOT NULL CONSTRAINT PK_DashSensitivityLabel PRIMARY KEY,   -- the label GUID
        Name        NVARCHAR(200)   NOT NULL,
        Rank        TINYINT         NULL,       -- ordering, least to most sensitive
        UpdatedBy   NVARCHAR(256)   NOT NULL,
        UpdatedUtc  DATETIME2(0)    NOT NULL CONSTRAINT DF_DashSensitivityLabel_Updated DEFAULT (SYSUTCDATETIME())
    );

/* Every page view, denial, export and admin change. */
IF OBJECT_ID(N'dash.AuditLog', N'U') IS NULL
    CREATE TABLE dash.AuditLog
    (
        LogId     BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_DashAuditLog PRIMARY KEY,
        AtUtc     DATETIME2(0)    NOT NULL CONSTRAINT DF_DashAuditLog_At DEFAULT (SYSUTCDATETIME()),
        Viewer    NVARCHAR(256)   NOT NULL,   -- the resolved UPN, or the Windows name if none
        Kind      VARCHAR(12)     NOT NULL,   -- View | Denied | Export | Admin
        Page      NVARCHAR(200)   NULL,
        Scope     NVARCHAR(400)   NULL,       -- Organization, or the subtree root shown
        Detail    NVARCHAR(2000)  NULL,       -- filters, or what an admin change did
        ClientIp  VARCHAR(64)     NULL,
        INDEX IX_DashAuditLog_At (AtUtc),
        INDEX IX_DashAuditLog_Viewer (Viewer, AtUtc)
    );

/* Built by dash.usp_RefreshDashboard. */
IF OBJECT_ID(N'dash.Person', N'U') IS NULL
    CREATE TABLE dash.Person
    (
        Upn            NVARCHAR(256)   NOT NULL CONSTRAINT PK_DashPerson PRIMARY KEY,
        DisplayName    NVARCHAR(256)   NULL,
        ManagerUpn     NVARCHAR(256)   NULL,
        Department     NVARCHAR(200)   NULL,
        AccountEnabled BIT             NULL,
        Level          TINYINT         NULL,   -- 1 = OrgTopUpn, 2 = reports to them, ...; NULL when not under the top (or no top set)
        DirectReports  INT             NOT NULL,
        PeopleBelow    INT             NOT NULL,   -- everyone under them, at any depth
        INDEX IX_DashPerson_Manager (ManagerUpn)
    );

/* The person's Windows account SID, from audit.UserDirectory: how a sign-in is matched to a person. */
IF COL_LENGTH(N'dash.Person', N'Sid') IS NULL
    ALTER TABLE dash.Person ADD Sid VARCHAR(184) NULL;
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dash.Person') AND name = N'IX_DashPerson_Sid')
    EXEC (N'CREATE INDEX IX_DashPerson_Sid ON dash.Person (Sid);');

IF OBJECT_ID(N'dash.OrgClosure', N'U') IS NULL
    CREATE TABLE dash.OrgClosure
    (
        AncestorUpn   NVARCHAR(256)   NOT NULL,
        DescendantUpn NVARCHAR(256)   NOT NULL,
        Depth         TINYINT         NOT NULL,   -- 0 = the person themselves
        /* Nonclustered: two UPNs can exceed a clustered key's 900 bytes. It
           covers the lookup (ancestor to the people under them) on its own. */
        CONSTRAINT PK_DashOrgClosure PRIMARY KEY NONCLUSTERED (AncestorUpn, DescendantUpn)
    );

IF OBJECT_ID(N'dash.UserDay', N'U') IS NULL
    CREATE TABLE dash.UserDay
    (
        DateKey           INT     NOT NULL,   -- bi.DimDate, in the reporting time zone
        UserKey           INT     NOT NULL,   -- bi.DimUser
        Interactions      INT     NOT NULL,
        Prompts           INT     NOT NULL,
        AppsUsed          TINYINT NOT NULL,
        AgentInteractions INT     NOT NULL,   -- interactions with an agent, not Copilot itself
        WebGrounded       INT     NOT NULL,
        ChatInteractions  INT     NOT NULL,   -- in Copilot Chat apps (bi.DimApp.AppCategory)
        LicensedUse       INT     NOT NULL,   -- interactions made while licensed
        Jailbreak         INT     NOT NULL,
        PromptInjection   INT     NOT NULL,
        BlockedResource   INT     NOT NULL,
        LabelledContent   INT     NOT NULL,   -- interactions that touched sensitivity-labelled content
        Sessions          INT     NOT NULL,   -- sessions that started that day
        MultiTurnSessions INT     NOT NULL,
        CONSTRAINT PK_DashUserDay PRIMARY KEY (DateKey, UserKey),
        INDEX IX_DashUserDay_User (UserKey, DateKey)
    );

/*
    Sensitivity labels and SharePoint sites Copilot touched, per person per day:
    Label (a label id on content an interaction read), Site (the site of labelled
    content it read) and Blocked (the site of content it was blocked from). Sites
    are cut to https://host/sites/name, or /personal/name for OneDrive.
*/
IF OBJECT_ID(N'dash.ResourceDay', N'U') IS NULL
    CREATE TABLE dash.ResourceDay
    (
        DateKey      INT            NOT NULL,
        UserKey      INT            NOT NULL,
        Kind         VARCHAR(8)     NOT NULL CONSTRAINT CK_DashResourceDay_Kind CHECK (Kind IN ('Label', 'Site', 'Blocked')),
        Item         NVARCHAR(400)  NOT NULL,
        Interactions INT            NOT NULL,
        CONSTRAINT PK_DashResourceDay PRIMARY KEY (DateKey, Kind, UserKey, Item)
    );

/* When each agent was first and last used: bi.DimAgent.FirstSeenUtc is when a refresh first saw it. */
IF OBJECT_ID(N'dash.AgentUse', N'U') IS NULL
    CREATE TABLE dash.AgentUse
    (
        AgentKey     INT     NOT NULL CONSTRAINT PK_DashAgentUse PRIMARY KEY,
        FirstDateKey INT     NOT NULL,
        LastDateKey  INT     NOT NULL,
        Interactions BIGINT  NOT NULL
    );

IF OBJECT_ID(N'dash.RefreshRun', N'U') IS NULL
    CREATE TABLE dash.RefreshRun
    (
        RunId        INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_DashRefreshRun PRIMARY KEY,
        StartedUtc   DATETIME2(0)  NOT NULL,
        EndedUtc     DATETIME2(0)  NOT NULL,
        SinceUtc     DATETIME2(0)  NULL,       -- NULL: every day rebuilt
        FromDateKey  INT           NULL,
        People       INT           NOT NULL,
        ClosureRows  INT           NOT NULL,
        UserDayRows  INT           NOT NULL
    );

/*
    The people a scope covers, as bi.DimUser keys: @RootUpn and everyone under
    them. The app passes the viewer's own UPN, or a manager inside their scope
    when they drill in; a whole-organisation view with no drill-in does not
    call it at all.
*/
EXEC (N'
CREATE OR ALTER FUNCTION dash.fnScopeUsers (@RootUpn NVARCHAR(256))
RETURNS TABLE
AS
RETURN
SELECT u.UserKey
FROM dash.OrgClosure AS c
JOIN bi.DimUser AS u ON u.UserPrincipalName = c.DescendantUpn
WHERE c.AncestorUpn = @RootUpn;');

/* Agents with their friendly names. */
EXEC (N'
CREATE OR ALTER VIEW dash.Agent
AS
SELECT a.AgentKey, a.AgentId, a.AgentSource,
       COALESCE(al.FriendlyName, a.AgentName, a.AgentId) AS AgentName,
       a.AgentName AS AuditName, al.Owner,
       COALESCE(CAST(fd.[Date] AS DATETIME2(0)), a.FirstSeenUtc) AS FirstSeenUtc   -- first used, in the reporting zone
FROM bi.DimAgent AS a
LEFT JOIN dash.AgentAlias AS al ON al.AgentKey = a.AgentKey
LEFT JOIN dash.AgentUse AS u ON u.AgentKey = a.AgentKey
LEFT JOIN bi.DimDate AS fd ON fd.DateKey = u.FirstDateKey;');
