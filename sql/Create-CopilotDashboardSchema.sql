/*
    Copilot Insights dashboard - schema
    ------------------------------------------------------------------------
    What the web dashboard (dashboard/CopilotInsights) needs on top of the bi
    star schema: who may see what, the settings and reference data its admin
    module edits, an audit log, and two things built by
    dash.usp_RefreshDashboard (Create-CopilotDashboardProcedure.sql):

      dash.Person       one row per person in audit.UserDirectory, with their
                        level below the configured top of the organization
      dash.OrgClosure   every (manager, person under them) pair at any depth,
                        by UPN - the security model's subtree lookup
      dash.UserDay      per person per day they used Copilot: interactions,
                        prompts, sessions, flags - so the pages read a small
                        rollup instead of scanning the facts, for any period
                        of 7 to 365 days

    The security model (enforced by the app, on every query):
      - A person granted Organization (Admin alone is not enough), the top of the organization
        (Setting OrgTopUpn) and anyone reporting directly to them see the
        whole organization.
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
    ('OrgTopUpn',    CAST(NULL AS NVARCHAR(400)), N'UPN of the top of the organization (the CEO). They and their direct reports see the whole organization. Empty: taken from the org chart (OrgTopUpnDetected).'),
    ('MinGroupSize', N'5',  N'Breakdowns hide groups with fewer people than this, folding them into "Smaller groups", so a group total cannot reveal one person.'),
    ('DefaultDays',  N'30', N'Days shown when a page opens: 7, 14, 30, 90, 180 or 365.'),
    ('InactiveDays', N'30', N'A licensed person with no Copilot use for this many days counts as not using their license.'),
    ('LicenseMonthlyCost', N'30', N'What one Copilot license costs a month, for the license recommendations.'),
    ('LicenseCurrency', N'USD', N'The currency LicenseMonthlyCost is in.'),
    ('MaxViewerLevel', N'0', N'Who can open the dashboard (2.4.4): 0 everyone in the directory; N levels 1 to N below the top, and anyone granted a role other than Admin. Set in Admin > Access.'),
    ('ProtectedLevels', N'5', N'Levels 1 to N of the organization are never on a revoke, reclaim, pay-as-you-go or training list (2.4.6); 0 for none. Set in Admin > Access.'),
    /* 2.6.6: the assumptions Leadership > Costs and savings values and prices with. */
    ('FabricReservedDiscount', N'0.41', N'2.6.6: what a one-year reservation takes off a Fabric capacity''s pay-as-you-go price, as a fraction (0.41 is 41%).'),
    ('FlowMinutesPerRun', N'3', N'2.6.6: the minutes one run of a cloud flow saves, where the flow inventory gives none for it.'),
    ('PowerBiCopilotMinutes', N'3', N'2.6.6: the minutes one Copilot in Power BI or Fabric request saves an analyst.'),
    ('CostIdleDays', N'30', N'2.6.6: a GitHub Copilot seat, a premium Power Platform license or a model deployment unused this many days counts as idle.'),
    /* 2.6.6: what Licensing > Credit use estimates Copilot Credits and tokens with. */
    ('CreditsPerAnswer', N'2', N'2.6.6: Copilot Credits a billed agent answer costs (a generative answer).'),
    ('CreditsPerGrounding', N'10', N'2.6.6: Copilot Credits grounding a billed answer in the organization''s data (Microsoft Graph) adds.'),
    ('CreditsPerAction', N'5', N'2.6.6: Copilot Credits an agent action adds; counted where an agent calls another.'),
    ('TokensPerTurn', N'1500', N'2.6.6: the tokens one Copilot turn takes before grounding (instructions, prompt, history), for the rough token estimate.'),
    ('TokensPerResource', N'1000', N'2.6.6: the tokens each file, mail or page read for grounding adds, for the rough token estimate.'),
    ('TokensPerAnswer', N'400', N'2.6.6: the tokens one answer gives back, for the rough token estimate.')
) AS s (Name, Value, Description)
ON t.Name = s.Name
WHEN NOT MATCHED THEN INSERT (Name, Value, Description) VALUES (s.Name, s.Value, s.Description);

/*
    2.6.6: what a license or unit of capacity costs a month, for Leadership > Costs and savings. A license is
    matched to audit.LicenseSku by its SKU part number (Item); the other items are units the page prices: a GB of
    Dataverse capacity over the entitlement, a pay-as-you-go Copilot Credit, a capacity pack, a GitHub Copilot
    seat. Seeded with list prices in USD as of 2026; a row an administrator changed is kept on every install.
    A license whose part number is not here is left out of the spend, and the page says how many are.
*/
IF OBJECT_ID(N'dash.CostPrice', N'U') IS NULL
    CREATE TABLE dash.CostPrice
    (
        Item         VARCHAR(64)     NOT NULL CONSTRAINT PK_DashCostPrice PRIMARY KEY,
        Area         NVARCHAR(40)    NOT NULL,   -- Microsoft 365 Copilot | GitHub Copilot | Power Automate | Power Apps | Power BI | Copilot Studio | Dataverse
        Name         NVARCHAR(200)   NOT NULL,
        Unit         NVARCHAR(40)    NOT NULL,   -- user, flow, GB, credit, pack, seat
        MonthlyPrice DECIMAL(19, 4)  NOT NULL,   -- a month, for a license; for a credit, each
        Currency     NVARCHAR(8)     NULL,       -- NULL: LicenseCurrency
        Note         NVARCHAR(400)   NULL,
        UpdatedBy    NVARCHAR(256)   NULL,
        UpdatedUtc   DATETIME2(0)    NOT NULL CONSTRAINT DF_DashCostPrice_Updated DEFAULT (SYSUTCDATETIME())
    );
MERGE dash.CostPrice AS t
USING (VALUES
    ('POWERAUTOMATE_ATTENDED_RPA',   N'Power Automate',  N'Power Automate Premium',             N'user',   15,    N'Premium connectors, desktop flows (attended RPA), process mining.'),
    ('FLOW_PER_USER',                N'Power Automate',  N'Power Automate per user (earlier plan)', N'user', 15,  NULL),
    ('POWERAUTOMATE_UNATTENDED_RPA', N'Power Automate',  N'Power Automate Process',             N'flow',   150,   N'One flow or bot, run unattended, whoever triggers it.'),
    ('FLOW_PER_PROCESS',             N'Power Automate',  N'Power Automate Process (earlier name)', N'flow', 150,  NULL),
    ('POWERAPPS_PER_USER',           N'Power Apps',      N'Power Apps Premium',                 N'user',   20,    NULL),
    ('POWERAPPS_PER_APP_NEW',        N'Power Apps',      N'Power Apps per app',                 N'user',   5,     NULL),
    ('PBI_PREMIUM_PER_USER',         N'Power BI',        N'Power BI Premium Per User',          N'user',   24,    NULL),
    ('POWER_BI_PRO',                 N'Power BI',        N'Power BI Pro',                       N'user',   14,    NULL),
    ('COPILOT_STUDIO_PAYG_CREDIT',   N'Copilot Studio',  N'Copilot Credit, pay-as-you-go',      N'credit', 0.01,  N'Billed through an Azure subscription.'),
    ('COPILOT_STUDIO_PACK',          N'Copilot Studio',  N'Copilot Credit capacity pack',       N'pack',   200,   N'25,000 credits a month, prepaid: 0.008 a credit.'),
    ('DATAVERSE_DATABASE_GB',        N'Dataverse',       N'Dataverse database capacity',        N'GB',     40,    N'Each GB over the entitlement.'),
    ('DATAVERSE_FILE_GB',            N'Dataverse',       N'Dataverse file capacity',            N'GB',     2,     N'Each GB over the entitlement.'),
    ('DATAVERSE_LOG_GB',             N'Dataverse',       N'Dataverse log capacity',             N'GB',     10,    N'Each GB over the entitlement.'),
    ('GITHUB_COPILOT_BUSINESS',      N'GitHub Copilot',  N'GitHub Copilot Business',            N'seat',   19,    NULL),
    ('GITHUB_COPILOT_ENTERPRISE',    N'GitHub Copilot',  N'GitHub Copilot Enterprise',          N'seat',   39,    NULL)
) AS s (Item, Area, Name, Unit, MonthlyPrice, Note)
ON t.Item = s.Item
WHEN NOT MATCHED THEN INSERT (Item, Area, Name, Unit, MonthlyPrice, Note, UpdatedBy) VALUES (s.Item, s.Area, s.Name, s.Unit, s.MonthlyPrice, s.Note, N'install');

/*
    2.6.6: the suites (Microsoft 365 and Office 365 E3, E5, E7): their price, their place in the family, and what of
    the priced items above they include. Costs and savings counts as AI spend only what is bought on top of a suite:
    an item a suite includes costs nothing more, and a standalone license of it on a suite's holder is paid twice. A
    suite's own price also buys security, compliance, Teams and more that cannot be split out, so no share of it is
    counted as AI spend; Licensing > Suite fit instead finds who a lower tier would serve (E7 to E5, E5 to E3).
    Includes is a comma-separated list of dash.CostPrice items, and COPILOT for Microsoft 365 Copilot; Covers says
    what else the suite brings, for the page. Prices are list prices in USD as Microsoft published them for July
    2026: set the agreement's in Admin > Targets and assumptions. A row an administrator changed is kept.
*/
IF OBJECT_ID(N'dash.CostSuite', N'U') IS NULL
    CREATE TABLE dash.CostSuite
    (
        SkuPattern   NVARCHAR(100)   NOT NULL CONSTRAINT PK_DashCostSuite PRIMARY KEY,   -- a LIKE pattern on the SKU part number
        Suite        NVARCHAR(100)   NOT NULL,
        Family       NVARCHAR(40)    NOT NULL,   -- Microsoft 365 | Office 365: a move down stays in the family
        Tier         TINYINT         NOT NULL,   -- 3, 5, 7
        MonthlyPrice DECIMAL(19, 4)  NOT NULL,   -- a user a month; 0: not known, so never a move's target
        Currency     NVARCHAR(8)     NULL,       -- NULL: LicenseCurrency
        Includes     NVARCHAR(400)   NOT NULL CONSTRAINT DF_DashCostSuite_Includes DEFAULT (N''),
        Covers       NVARCHAR(400)   NULL,
        Enabled      BIT             NOT NULL CONSTRAINT DF_DashCostSuite_Enabled DEFAULT (1),
        UpdatedBy    NVARCHAR(256)   NULL,
        UpdatedUtc   DATETIME2(0)    NOT NULL CONSTRAINT DF_DashCostSuite_Updated DEFAULT (SYSUTCDATETIME())
    );
MERGE dash.CostSuite AS t
USING (VALUES
    (N'SPE_E3%',            N'Microsoft 365 E3', N'Microsoft 365', 3, 39, N'',                     N'Power Apps and Power Automate with standard connectors, Copilot Chat, Intune, Entra ID P1. No Power BI Pro.'),
    (N'SPE_E5%',            N'Microsoft 365 E5', N'Microsoft 365', 5, 60, N'POWER_BI_PRO',         N'E3, with Power BI Pro, Defender for Office 365 and Endpoint P2, Purview, Entra ID P2, Teams Phone and audio conferencing.'),
    (N'Microsoft_365_E5%',  N'Microsoft 365 E5', N'Microsoft 365', 5, 60, N'POWER_BI_PRO',         N'E3, with Power BI Pro, Defender for Office 365 and Endpoint P2, Purview, Entra ID P2, Teams Phone and audio conferencing.'),
    (N'Microsoft_365_E7%',  N'Microsoft 365 E7', N'Microsoft 365', 7, 99, N'COPILOT,POWER_BI_PRO', N'E5, with Microsoft 365 Copilot, Agent 365 and the Entra Suite, as Microsoft announced it in 2026: check against your agreement.'),
    (N'M365_E7%',           N'Microsoft 365 E7', N'Microsoft 365', 7, 99, N'COPILOT,POWER_BI_PRO', N'E5, with Microsoft 365 Copilot, Agent 365 and the Entra Suite, as Microsoft announced it in 2026: check against your agreement.'),
    (N'SPE_E7%',            N'Microsoft 365 E7', N'Microsoft 365', 7, 99, N'COPILOT,POWER_BI_PRO', N'E5, with Microsoft 365 Copilot, Agent 365 and the Entra Suite, as Microsoft announced it in 2026: check against your agreement.'),
    (N'ENTERPRISEPACK%',    N'Office 365 E3',    N'Office 365',    3, 26, N'',                     N'Power Apps and Power Automate with standard connectors, Copilot Chat. No Power BI Pro.'),
    (N'ENTERPRISEPREMIUM%', N'Office 365 E5',    N'Office 365',    5, 41, N'POWER_BI_PRO',         N'Office 365 E3, with Power BI Pro, Defender for Office 365 P2, Purview, Teams Phone and audio conferencing.')
) AS s (SkuPattern, Suite, Family, Tier, MonthlyPrice, Includes, Covers)
ON t.SkuPattern = s.SkuPattern
WHEN NOT MATCHED THEN INSERT (SkuPattern, Suite, Family, Tier, MonthlyPrice, Includes, Covers, UpdatedBy)
    VALUES (s.SkuPattern, s.Suite, s.Family, s.Tier, s.MonthlyPrice, s.Includes, s.Covers, N'install');

/* 2.6.6: the license pool's suite mark, also added here so the dashboard reads it before the next license lookup
   runs the enrichment's own schema (Create-CopilotEnrichmentSchema.sql adds it too). */
IF OBJECT_ID(N'audit.LicenseSku', N'U') IS NOT NULL AND COL_LENGTH(N'audit.LicenseSku', N'CopilotInSuite') IS NULL
    ALTER TABLE audit.LicenseSku ADD CopilotInSuite BIT NULL;

/* 2.6.6: the people who hold Microsoft 365 Copilot only through a suite (E7) and not as the add-on as well: their
   Copilot cannot be taken off alone, so they are never on a list to revoke. A view the dashboard reads, defined in
   full once the license pool and its suite mark exist, and empty until then; Create-CopilotEnrichmentSchema.sql and
   Create-CopilotDashboardSchema.sql both write it, so whichever runs last with the tables there leaves the full one. */
IF SCHEMA_ID(N'dash') IS NOT NULL
BEGIN
    IF OBJECT_ID(N'audit.UserLicenseSku', N'U') IS NOT NULL AND COL_LENGTH(N'audit.LicenseSku', N'CopilotInSuite') IS NOT NULL
        EXEC (N'CREATE OR ALTER VIEW dash.CopilotSuiteOnly AS
SELECT DISTINCT su.UserPrincipalName AS Upn
FROM audit.UserLicenseSku AS su JOIN audit.LicenseSku AS ss ON ss.SkuId = su.SkuId
WHERE ss.CopilotInSuite = 1
  AND NOT EXISTS (SELECT 1 FROM audit.UserLicenseSku AS au JOIN audit.LicenseSku AS sa ON sa.SkuId = au.SkuId
                  WHERE au.UserPrincipalName = su.UserPrincipalName AND sa.IsCopilot = 1 AND ISNULL(sa.CopilotInSuite, 0) = 0);');
    ELSE IF OBJECT_ID(N'dash.CopilotSuiteOnly', N'V') IS NULL
        EXEC (N'CREATE VIEW dash.CopilotSuiteOnly AS SELECT CAST(NULL AS NVARCHAR(512)) AS Upn WHERE 1 = 0;');
END;

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

/* 2.6.4: an Admin grant can be limited to some of the Admin pages: their paths, comma-separated
   (/admin/health,/admin/audit). NULL, as on every grant before, is the whole role. Other roles leave it NULL. */
IF COL_LENGTH(N'dash.RoleGrant', N'Pages') IS NULL ALTER TABLE dash.RoleGrant ADD Pages NVARCHAR(1000) NULL;
/* The 2.6.4 build that was withdrawn kept such pages under a role of its own, Persona. Where a database has any, they
   become Admin grants limited to the same pages (an Admin grant the same person already holds wins), and the role's
   check goes back to the eight roles. */
IF EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_DashRoleGrant_Role' AND parent_object_id = OBJECT_ID(N'dash.RoleGrant') AND definition LIKE N'%Persona%')
BEGIN
    EXEC (N'DELETE p FROM dash.RoleGrant AS p
            WHERE p.Role = ''Persona'' AND (NULLIF(LTRIM(RTRIM(p.Pages)), N'''') IS NULL
                  OR EXISTS (SELECT 1 FROM dash.RoleGrant AS a WHERE a.Principal = p.Principal AND a.Role = ''Admin''));');
    ALTER TABLE dash.RoleGrant DROP CONSTRAINT CK_DashRoleGrant_Role;
    UPDATE dash.RoleGrant SET Role = 'Admin' WHERE Role = 'Persona';
    ALTER TABLE dash.RoleGrant ADD CONSTRAINT CK_DashRoleGrant_Role CHECK (Role IN
        ('Admin', 'Organization', 'Adoption', 'Licensing', 'Security', 'Compliance', 'Agents', 'Enablement'));
END;

/* Friendly names and owners for agents, set by an administrator: they come before the name the audit log gives. */
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
        LabeledContent   INT     NOT NULL,   -- interactions that touched sensitivity-labeled content
        Sessions          INT     NOT NULL,   -- sessions that started that day
        MultiTurnSessions INT     NOT NULL,
        PremiumUse        INT     NOT NULL CONSTRAINT DF_DashUserDay_Premium DEFAULT (0),   -- licensed interactions that needed the license (2.3.0)
        CONSTRAINT PK_DashUserDay PRIMARY KEY (DateKey, UserKey),
        INDEX IX_DashUserDay_User (UserKey, DateKey)
    );

/* 2.3.0: use that needs the Premium license. Zero until the rollups are rebuilt once (dash.usp_RefreshDashboard with no date). */
IF COL_LENGTH(N'dash.UserDay', N'PremiumUse') IS NULL
    ALTER TABLE dash.UserDay ADD PremiumUse INT NOT NULL CONSTRAINT DF_DashUserDay_Premium DEFAULT (0);

/*
    Sensitivity labels and SharePoint sites Copilot touched, per person per day:
    Label (a label id on content an interaction read), Site (the site of labeled
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

/*
    2.4.7: the interactions with a security signal, a few in every thousand, each with the record id the triage
    is kept under. Security > Flagged interactions lists them from here: reading the record ids of the flagged
    rows out of bi.FactInteraction costs a pass over that column for the whole fact (4 seconds at 40 million).
*/
IF OBJECT_ID(N'dash.FlaggedInteraction', N'U') IS NULL
BEGIN
    CREATE TABLE dash.FlaggedInteraction
    (
        DateKey                 INT           NOT NULL,
        UserKey                 INT           NOT NULL,
        RecordId                NVARCHAR(450) NOT NULL,
        ActivityHour            DATETIME2(0)  NOT NULL,
        AppKey                  INT           NOT NULL,
        AgentKey                INT           NOT NULL,
        JailbreakDetected       BIT           NOT NULL,
        PromptInjectionDetected BIT           NOT NULL,
        HasBlockedResource      BIT           NOT NULL
    );
    CREATE CLUSTERED INDEX CX_DashFlaggedInteraction ON dash.FlaggedInteraction (DateKey, UserKey);
END;

/*
    2.4.2: columnstore copies of the two rollups. The pages read a year of them
    for a whole organization at a time (6 and 12 million rows at 70,000 people),
    which the row-store keys scan in seconds and a columnstore in a fraction of
    one. The refresh truncates and bulk-loads both, which a columnstore takes.
*/
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dash.UserDay') AND name = N'NCCI_DashUserDay')
    EXEC (N'CREATE NONCLUSTERED COLUMNSTORE INDEX NCCI_DashUserDay ON dash.UserDay
          (DateKey, UserKey, Interactions, Prompts, AppsUsed, AgentInteractions, WebGrounded, ChatInteractions, LicensedUse,
           Jailbreak, PromptInjection, BlockedResource, LabeledContent, Sessions, MultiTurnSessions, PremiumUse);');
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dash.ResourceDay') AND name = N'NCCI_DashResourceDay')
    EXEC (N'CREATE NONCLUSTERED COLUMNSTORE INDEX NCCI_DashResourceDay ON dash.ResourceDay (DateKey, UserKey, Kind, Item, Interactions);');

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
/* 2.4.0: the latest bi.RefreshRun the rollups reflect, so the next refresh sees what bi changed since. */
IF COL_LENGTH(N'dash.RefreshRun', N'BiRunId') IS NULL
    ALTER TABLE dash.RefreshRun ADD BiRunId INT NULL;

/*
    The people a scope covers, as bi.DimUser keys: @RootUpn and everyone under
    them. The app passes the viewer's own UPN, or a manager inside their scope
    when they drill in; a whole-organization view with no drill-in does not
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

/*
    2.5.6: the agent inventory, from the Excel workbook tools/Import-AgentWorkbook.ps1 loads each night. One row per
    agent the workbook lists (the current agents), matched to the agent the audit log knows (AgentKey) where it can
    be. The workbook's sheets themselves are in dbo, as they come; these are what the reports read.
*/
IF OBJECT_ID(N'dash.AgentInventory', N'U') IS NULL
    CREATE TABLE dash.AgentInventory
    (
        InventoryAgentId  NVARCHAR(100)  NOT NULL CONSTRAINT PK_DashAgentInventory PRIMARY KEY,   -- the workbook's AgentId
        AgentKey          INT            NULL,           -- bi.DimAgent, when the audit log knows the agent
        AgentName         NVARCHAR(400)  NULL,
        OwnerUpn          NVARCHAR(256)  NULL,
        Environment       NVARCHAR(400)  NULL,
        SchemaName        NVARCHAR(400)  NULL,
        CreatedAt         DATETIME2(0)   NULL,
        LastPublishedAt   DATETIME2(0)   NULL,
        Channels          NVARCHAR(400)  NULL,
        ViewerUsers       INT            NULL,
        ViewerGroups      INT            NULL,
        EditorUsers       INT            NULL,
        EditorGroups      INT            NULL,
        IsShared          AS CAST(CASE WHEN ViewerUsers > 0 OR ViewerGroups > 0 THEN 1 ELSE 0 END AS BIT),
        InProduction      BIT            NOT NULL CONSTRAINT DF_DashAgentInventory_Prod DEFAULT (0),   -- listed on AgentsInProd
        LoadedUtc         DATETIME2(0)   NOT NULL CONSTRAINT DF_DashAgentInventory_Loaded DEFAULT (SYSUTCDATETIME()),
        INDEX IX_DashAgentInventory_AgentKey (AgentKey)
    );

/* Connected agents: which parent agent calls which child, from the workbook's ParentChildAgent sheet. */
IF OBJECT_ID(N'dash.AgentChildLink', N'U') IS NULL
    CREATE TABLE dash.AgentChildLink
    (
        LinkId                  INT IDENTITY(1, 1) NOT NULL CONSTRAINT PK_DashAgentChildLink PRIMARY KEY,
        ChildAgentName          NVARCHAR(400)  NOT NULL,
        ParentInventoryAgentId  NVARCHAR(100)  NULL,
        ParentAgentName         NVARCHAR(400)  NULL,
        ParentAgentKey          INT            NULL,     -- bi.DimAgent, through the parent's inventory row
        RelationshipType        NVARCHAR(200)  NULL,
        IsValid                 BIT            NULL
    );

/* 2.5.7: which child it is. A name alone is not: Copilot Studio calls a new child agent "Agent", in every parent. A
   child agent lives inside its parent (its own component id); a connected agent is an agent of its own, which several
   parents can call (its environment and schema name, and its row in the inventory where it has one). */
IF COL_LENGTH(N'dash.AgentChildLink', N'ChildKey') IS NULL ALTER TABLE dash.AgentChildLink ADD ChildKey NVARCHAR(450) NULL;
IF COL_LENGTH(N'dash.AgentChildLink', N'ChildKind') IS NULL ALTER TABLE dash.AgentChildLink ADD ChildKind NVARCHAR(20) NULL;   -- Child, Connected
IF COL_LENGTH(N'dash.AgentChildLink', N'ChildInventoryAgentId') IS NULL ALTER TABLE dash.AgentChildLink ADD ChildInventoryAgentId NVARCHAR(100) NULL;
IF COL_LENGTH(N'dash.AgentChildLink', N'ParentEnvironment') IS NULL ALTER TABLE dash.AgentChildLink ADD ParentEnvironment NVARCHAR(400) NULL;

/* 2.5.8: the settings the agent checks compare with the agents' use, and whether an agent runs on its own. An agent
   with triggers or flows (an autonomous agent) can work with nobody chatting to it, so no interaction is not "unused". */
IF COL_LENGTH(N'dash.AgentInventory', N'EnvironmentSku') IS NULL ALTER TABLE dash.AgentInventory ADD EnvironmentSku NVARCHAR(100) NULL;
IF COL_LENGTH(N'dash.AgentInventory', N'Authentication') IS NULL ALTER TABLE dash.AgentInventory ADD Authentication NVARCHAR(200) NULL;
IF COL_LENGTH(N'dash.AgentInventory', N'IsQuarantined') IS NULL ALTER TABLE dash.AgentInventory ADD IsQuarantined BIT NULL;
IF COL_LENGTH(N'dash.AgentInventory', N'IsWebSearchEnabled') IS NULL ALTER TABLE dash.AgentInventory ADD IsWebSearchEnabled BIT NULL;
IF COL_LENGTH(N'dash.AgentInventory', N'IsAutonomous') IS NULL
    ALTER TABLE dash.AgentInventory ADD IsAutonomous BIT NOT NULL CONSTRAINT DF_DashAgentInventory_Autonomous DEFAULT (0);

/* Each workbook load, for Admin > Health: when, which file, and what reached the tables. */
IF OBJECT_ID(N'dash.AgentInventoryRun', N'U') IS NULL
    CREATE TABLE dash.AgentInventoryRun
    (
        RunId           INT IDENTITY(1, 1) NOT NULL CONSTRAINT PK_DashAgentInventoryRun PRIMARY KEY,
        StartedUtc      DATETIME2(0)    NOT NULL CONSTRAINT DF_DashAgentInventoryRun_Started DEFAULT (SYSUTCDATETIME()),
        EndedUtc        DATETIME2(0)    NULL,
        Source          NVARCHAR(800)   NULL,            -- the file, and where it came from
        FileCreatedUtc  DATETIME2(0)    NULL,
        InventoryRows   INT             NULL,
        ProductionRows  INT             NULL,
        ChildLinkRows   INT             NULL,
        Matched         INT             NULL,            -- inventory agents the audit log knows
        Status          NVARCHAR(20)    NOT NULL CONSTRAINT DF_DashAgentInventoryRun_Status DEFAULT (N'Running'),
        Message         NVARCHAR(2000)  NULL
    );

/* Agents with their friendly names (2.5.6): the agent workbook's first, as the list of the current agents; then one set
   by hand in Admin, for an agent the workbook does not name (one since deleted); then the audit log's. */
EXEC (N'
CREATE OR ALTER VIEW dash.Agent
AS
SELECT a.AgentKey, a.AgentId, a.AgentSource,
       COALESCE(NULLIF(inv.AgentName, N''''), al.FriendlyName, a.AgentName, a.AgentId) AS AgentName,
       a.AgentName AS AuditName, COALESCE(inv.OwnerUpn, al.Owner) AS Owner,
       COALESCE(CAST(fd.[Date] AS DATETIME2(0)), a.FirstSeenUtc) AS FirstSeenUtc,   -- first used, in the reporting zone
       inv.InventoryAgentId, CAST(ISNULL(inv.IsShared, 0) AS BIT) AS IsShared, CAST(ISNULL(inv.InProduction, 0) AS BIT) AS InProduction
FROM bi.DimAgent AS a
LEFT JOIN dash.AgentAlias AS al ON al.AgentKey = a.AgentKey
LEFT JOIN dash.AgentUse AS u ON u.AgentKey = a.AgentKey
LEFT JOIN bi.DimDate AS fd ON fd.DateKey = u.FirstDateKey
OUTER APPLY (SELECT TOP (1) i.InventoryAgentId, i.AgentName, i.OwnerUpn, i.IsShared, i.InProduction
             FROM dash.AgentInventory AS i WHERE i.AgentKey = a.AgentKey
             ORDER BY i.InProduction DESC, i.LastPublishedAt DESC, i.InventoryAgentId) AS inv
/* 2.6.3: an agent the audit log names two ways is one agent here, under the identity its interactions are kept with. */
WHERE a.MergedIntoKey IS NULL;');
