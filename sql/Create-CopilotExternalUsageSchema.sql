/*
    Usage and cost of AI outside Microsoft 365 Copilot (2.6.2): first plumbing
    ------------------------------------------------------------------------
    Two more sources, each loaded by a tool of its own into tables of its own,
    beside the Copilot audit data and not mixed into it:

      GitHub Copilot (tools/Import-GitHubCopilotUsage.ps1), for an enterprise
      or an organization, from GitHub's REST API:
        audit.GitHubCopilotSeat      one row per seat: who holds it, since
                                     when, the plan, and when they last used it
        audit.GitHubCopilotDay       one row per day: active users, interactions,
                                     code generated and accepted, lines of code
        audit.GitHubCopilotUserDay   one row per person per day they used it,
                                     with the AI credits they used
        audit.GitHubCopilotCost      what GitHub billed for Copilot, a row per
                                     day, product, SKU and model

      Microsoft Foundry and Azure OpenAI (tools/Import-FoundryUsage.ps1), for
      the subscriptions named, from Azure Resource Manager, Azure Monitor and
      Cost Management:
        audit.FoundryDeployment      one row per model deployment as it stands
        audit.FoundryUsageDay        one row per deployment and model per day:
                                     requests, input and output tokens
        audit.FoundryCostDay         one row per resource and meter per day:
                                     what Azure charged

      audit.ExternalUsageRun         one row per run of either tool, for
                                     Admin > Health

    This is plumbing: the tables, the loads and their log. No report reads them
    yet, and nothing in bi or dash depends on them. A person here is a GitHub
    login, which is not a Microsoft 365 sign-in; nothing joins the two yet.

    Days are UTC days, as each service reports them, not the reporting time
    zone's. Amounts are in the currency the service bills in.

    Idempotent. Single batch (no GO). Requires SQL Server 2016+.
*/
SET NOCOUNT ON;

IF SCHEMA_ID(N'audit') IS NULL
    EXEC (N'CREATE SCHEMA audit AUTHORIZATION dbo;');

/* ---- GitHub Copilot ------------------------------------------------------- */

/* Every seat as GitHub lists it now. Replaced whole by each load of a scope. */
IF OBJECT_ID(N'audit.GitHubCopilotSeat', N'U') IS NULL
    CREATE TABLE audit.GitHubCopilotSeat
    (
        Scope                   NVARCHAR(120)  NOT NULL,   -- enterprise:<slug> or org:<name>
        Login                   NVARCHAR(100)  NOT NULL,   -- the GitHub login: not a Microsoft 365 sign-in
        UserId                  BIGINT         NULL,
        Organization            NVARCHAR(100)  NULL,       -- the organization the seat was assigned through
        AssigningTeam           NVARCHAR(200)  NULL,
        PlanType                VARCHAR(16)    NULL,       -- business | enterprise | unknown
        CreatedUtc              DATETIME2(0)   NULL,       -- when the seat was assigned
        LastActivityUtc         DATETIME2(0)   NULL,       -- NULL: never used, or telemetry is off in the editor
        LastActivityEditor      NVARCHAR(200)  NULL,
        LastAuthenticatedUtc    DATETIME2(0)   NULL,
        PendingCancellationDate DATE           NULL,
        LoadedUtc               DATETIME2(0)   NOT NULL CONSTRAINT DF_GitHubCopilotSeat_Loaded DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT PK_GitHubCopilotSeat PRIMARY KEY (Scope, Login)
    );

/* One row per day, from the organization-1-day or enterprise-1-day report. The whole record is kept in
   Json: it carries breakdowns by editor, feature, language and model that have no columns here yet. */
IF OBJECT_ID(N'audit.GitHubCopilotDay', N'U') IS NULL
    CREATE TABLE audit.GitHubCopilotDay
    (
        Scope                  NVARCHAR(120)  NOT NULL,
        [Day]                  DATE           NOT NULL,   -- a UTC day
        DailyActiveUsers       INT            NULL,
        WeeklyActiveUsers      INT            NULL,
        MonthlyActiveUsers     INT            NULL,
        MonthlyActiveChatUsers INT            NULL,
        MonthlyActiveAgentUsers INT           NULL,
        Interactions           BIGINT         NULL,       -- user_initiated_interaction_count
        CodeGenerations        BIGINT         NULL,       -- code_generation_activity_count
        CodeAcceptances        BIGINT         NULL,       -- code_acceptance_activity_count
        LocSuggestedToAdd      BIGINT         NULL,
        LocSuggestedToDelete   BIGINT         NULL,
        LocAdded               BIGINT         NULL,
        LocDeleted             BIGINT         NULL,
        Json                   NVARCHAR(MAX)  NULL,
        LoadedUtc              DATETIME2(0)   NOT NULL CONSTRAINT DF_GitHubCopilotDay_Loaded DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT PK_GitHubCopilotDay PRIMARY KEY (Scope, [Day])
    );

/* One row per person per day they used Copilot, from the users-1-day report. */
IF OBJECT_ID(N'audit.GitHubCopilotUserDay', N'U') IS NULL
    CREATE TABLE audit.GitHubCopilotUserDay
    (
        Scope           NVARCHAR(120)  NOT NULL,
        [Day]           DATE           NOT NULL,
        UserId          BIGINT         NOT NULL,
        Login           NVARCHAR(100)  NULL,
        Interactions    BIGINT         NULL,
        CodeGenerations BIGINT         NULL,
        CodeAcceptances BIGINT         NULL,
        LocAdded        BIGINT         NULL,
        LocDeleted      BIGINT         NULL,
        AiCreditsUsed   DECIMAL(19, 4) NULL,       -- 1 AI credit is 0.01 USD at GitHub's list price
        UsedChat        BIT            NULL,
        UsedAgent       BIT            NULL,
        UsedCli         BIT            NULL,
        LoadedUtc       DATETIME2(0)   NOT NULL CONSTRAINT DF_GitHubCopilotUserDay_Loaded DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT PK_GitHubCopilotUserDay PRIMARY KEY (Scope, [Day], UserId)
    );

/* What GitHub billed for Copilot: its billing usage for the scope, the lines whose product is Copilot. A
   month is replaced whole each time it is loaded, since an open month's lines change until it closes. */
IF OBJECT_ID(N'audit.GitHubCopilotCost', N'U') IS NULL
    CREATE TABLE audit.GitHubCopilotCost
    (
        CostId         BIGINT IDENTITY(1, 1) NOT NULL CONSTRAINT PK_GitHubCopilotCost PRIMARY KEY,
        Scope          NVARCHAR(120)  NOT NULL,
        UsageDate      DATE           NOT NULL,
        Product        NVARCHAR(100)  NULL,
        Sku            NVARCHAR(200)  NULL,
        Model          NVARCHAR(200)  NULL,
        Organization   NVARCHAR(100)  NULL,
        UnitType       NVARCHAR(50)   NULL,
        PricePerUnit   DECIMAL(19, 6) NULL,
        Quantity       DECIMAL(19, 4) NULL,
        GrossAmount    DECIMAL(19, 4) NULL,
        DiscountAmount DECIMAL(19, 4) NULL,
        NetAmount      DECIMAL(19, 4) NULL,       -- what was charged, in USD
        LoadedUtc      DATETIME2(0)   NOT NULL CONSTRAINT DF_GitHubCopilotCost_Loaded DEFAULT (SYSUTCDATETIME()),
        INDEX IX_GitHubCopilotCost_Date (Scope, UsageDate)
    );

/* ---- Microsoft Foundry and Azure OpenAI ----------------------------------- */

/* Every model deployment of the accounts read, as it stands. Replaced whole for a subscription by each load. */
IF OBJECT_ID(N'audit.FoundryDeployment', N'U') IS NULL
    CREATE TABLE audit.FoundryDeployment
    (
        SubscriptionId VARCHAR(36)    NOT NULL,
        ResourceGroup  NVARCHAR(90)   NOT NULL,
        Account        NVARCHAR(64)   NOT NULL,   -- the Microsoft.CognitiveServices account
        Deployment     NVARCHAR(64)   NOT NULL,
        Kind           NVARCHAR(50)   NULL,       -- OpenAI | AIServices
        Location       NVARCHAR(50)   NULL,
        ModelName      NVARCHAR(100)  NULL,
        ModelVersion   NVARCHAR(32)   NULL,
        ModelPublisher NVARCHAR(100)  NULL,
        SkuName        NVARCHAR(50)   NULL,       -- Standard, GlobalStandard, ProvisionedManaged ...
        Capacity       INT            NULL,
        LoadedUtc      DATETIME2(0)   NOT NULL CONSTRAINT DF_FoundryDeployment_Loaded DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT PK_FoundryDeployment PRIMARY KEY (SubscriptionId, ResourceGroup, Account, Deployment)
    );

/* One row per deployment, model and UTC day, from Azure Monitor's metrics of the account. A day is
   written again each time it is loaded: Azure Monitor keeps 93 days, so what is here is the history. */
IF OBJECT_ID(N'audit.FoundryUsageDay', N'U') IS NULL
    CREATE TABLE audit.FoundryUsageDay
    (
        SubscriptionId VARCHAR(36)    NOT NULL,
        ResourceGroup  NVARCHAR(90)   NOT NULL,
        Account        NVARCHAR(64)   NOT NULL,
        Deployment     NVARCHAR(64)   NOT NULL,
        ModelName      NVARCHAR(100)  NOT NULL,   -- '' where the metric does not say
        ModelVersion   NVARCHAR(32)   NOT NULL,
        [Day]          DATE           NOT NULL,
        Requests       BIGINT         NULL,
        InputTokens    BIGINT         NULL,
        OutputTokens   BIGINT         NULL,
        TotalTokens    BIGINT         NULL,
        LoadedUtc      DATETIME2(0)   NOT NULL CONSTRAINT DF_FoundryUsageDay_Loaded DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT PK_FoundryUsageDay PRIMARY KEY (SubscriptionId, ResourceGroup, Account, Deployment, ModelName, ModelVersion, [Day])
    );

/* One row per account, meter and day, from Cost Management: the cost Azure reports, before tax. Cost is by
   resource and meter, not by deployment. A day is written again each time it is loaded, since an open
   month's figures are estimates until the fifth day after it ends. */
IF OBJECT_ID(N'audit.FoundryCostDay', N'U') IS NULL
    CREATE TABLE audit.FoundryCostDay
    (
        SubscriptionId VARCHAR(36)    NOT NULL,
        ResourceGroup  NVARCHAR(90)   NOT NULL,
        Account        NVARCHAR(64)   NOT NULL,
        Meter          NVARCHAR(200)  NOT NULL,   -- such as "gpt-4o Input Tokens"; '' where none
        [Day]          DATE           NOT NULL,
        Cost           DECIMAL(19, 6) NOT NULL,
        Currency       NVARCHAR(8)    NULL,
        LoadedUtc      DATETIME2(0)   NOT NULL CONSTRAINT DF_FoundryCostDay_Loaded DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT PK_FoundryCostDay PRIMARY KEY (SubscriptionId, ResourceGroup, Account, Meter, [Day])
    );

/* ---- The loads' own log ---------------------------------------------------- */

/* One row per run of either tool, from its start, so a failure shows too. Admin > Health reads the last of
   each source. CredentialExpiresUtc is when the GitHub token used expires, where GitHub says. */
IF OBJECT_ID(N'audit.ExternalUsageRun', N'U') IS NULL
    CREATE TABLE audit.ExternalUsageRun
    (
        RunId                INT IDENTITY(1, 1) NOT NULL CONSTRAINT PK_ExternalUsageRun PRIMARY KEY,
        Source               VARCHAR(16)    NOT NULL,   -- GitHub | Foundry
        Scope                NVARCHAR(400)  NULL,       -- the enterprise or organization, or the subscriptions
        StartedUtc           DATETIME2(0)   NOT NULL CONSTRAINT DF_ExternalUsageRun_Started DEFAULT (SYSUTCDATETIME()),
        EndedUtc             DATETIME2(0)   NULL,
        Status               VARCHAR(16)    NOT NULL CONSTRAINT DF_ExternalUsageRun_Status DEFAULT ('Running'),   -- Running | Succeeded | Partial | Failed
        FromDay              DATE           NULL,
        ToDay                DATE           NULL,
        RowsWritten          INT            NULL,
        Detail               NVARCHAR(2000) NULL,       -- what was loaded, or why it failed
        CredentialExpiresUtc DATETIME2(0)   NULL,
        Host                 NVARCHAR(128)  NULL,
        INDEX IX_ExternalUsageRun_Source (Source, RunId)
    );

/* The dashboard's account reads them, where the dashboard's role exists. */
IF DATABASE_PRINCIPAL_ID(N'CopilotInsightsApp') IS NOT NULL
BEGIN
    GRANT SELECT ON audit.GitHubCopilotSeat TO CopilotInsightsApp;
    GRANT SELECT ON audit.GitHubCopilotDay TO CopilotInsightsApp;
    GRANT SELECT ON audit.GitHubCopilotUserDay TO CopilotInsightsApp;
    GRANT SELECT ON audit.GitHubCopilotCost TO CopilotInsightsApp;
    GRANT SELECT ON audit.FoundryDeployment TO CopilotInsightsApp;
    GRANT SELECT ON audit.FoundryUsageDay TO CopilotInsightsApp;
    GRANT SELECT ON audit.FoundryCostDay TO CopilotInsightsApp;
    GRANT SELECT ON audit.ExternalUsageRun TO CopilotInsightsApp;
END;
