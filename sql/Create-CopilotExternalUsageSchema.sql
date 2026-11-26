/*
    Usage and cost of AI outside Microsoft 365 Copilot: first plumbing
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
        audit.GitHubIdentity         who each login is, from SCIM (3.3.0)
        audit.GitHubCopilotPolicy    each organization's Copilot settings (3.3.0)

      Microsoft Foundry and Azure OpenAI (tools/Import-FoundryUsage.ps1), for
      the subscriptions named, from Azure Resource Manager, Azure Monitor and
      Cost Management:
        audit.FoundryDeployment      one row per model deployment as it stands
        audit.FoundryUsageDay        one row per deployment and model per day:
                                     requests, input and output tokens
        audit.FoundryCostDay         one row per resource and meter per day:
                                     what Azure charged
        audit.FoundryAccount         each account's network, sign-in, logging
                                     and tags (3.3.0)
        audit.FoundryContentFilter   each account's content filters (3.3.0)
        audit.FoundryModelCatalog    when each deployed model retires (3.3.0)
        audit.FoundryAccountSeen     every AI account the app can see, and
                                     whether its subscription is loaded (3.3.0)

      audit.ExternalUsageRun         one row per run of either tool, for
                                     Admin > Health

      Microsoft Fabric, Copilot in Power BI and the Power Platform,
      from each source's own export (tools/Import-CostCsv.ps1), for
      Leadership > Costs and savings:
        audit.FabricCapacityDay      one row per capacity per day: size,
                                     use, peak, throttling and cost
        audit.FabricWorkloadDay      one row per capacity, day and workload,
                                     Copilot in Power BI among them
        audit.PowerAutomateFlow      every cloud flow: owner, premium or not,
                                     how it is licensed, its runs
        audit.PowerPlatformUserActivity  who used Power Apps, Power Automate
                                     and Copilot Studio lately
        audit.PowerPlatformCapacityDay   Dataverse capacity entitled and used
        audit.PowerPlatformCreditDay Copilot Studio and AI Builder credits

    From 2.6.6 Leadership > Costs and savings reads them all, beside the
    licenses; nothing in bi or dash depends on them. A person here is a GitHub
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

/* Who each GitHub login is (3.3.0), for the seats and usage by person: an enterprise's SCIM identities (Enterprise
   Managed Users: userName is the person's sign-in in the identity provider, the UPN). The login is worked out by
   GitHub's rule for managed users: the userName before any @, its characters other than letters, digits and
   hyphens made hyphens, then _ and the enterprise's short code. Replaced whole for a scope by each load. */
IF OBJECT_ID(N'audit.GitHubIdentity', N'U') IS NULL
    CREATE TABLE audit.GitHubIdentity
    (
        Scope        NVARCHAR(120)  NOT NULL,
        Upn          NVARCHAR(256)  NOT NULL,   -- the SCIM userName: the sign-in in Entra
        Login        NVARCHAR(100)  NULL,       -- the GitHub login it gives, where the short code is known
        ExternalId   NVARCHAR(256)  NULL,       -- the identity provider's id of the person
        DisplayName  NVARCHAR(256)  NULL,
        Active       BIT            NULL,       -- 0: deprovisioned, the account suspended
        Source       VARCHAR(16)    NOT NULL,   -- SCIM
        LoadedUtc    DATETIME2(0)   NOT NULL CONSTRAINT DF_GitHubIdentity_Loaded DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT PK_GitHubIdentity PRIMARY KEY (Scope, Upn)
    );

/* Each organization's Copilot settings as GitHub reports them (3.3.0): one row per organization and setting
   (public_code_suggestions, ide_chat, platform_chat, cli, seat_management_setting, plan_type), its state as GitHub
   words it. Replaced whole for a scope by each load. */
IF OBJECT_ID(N'audit.GitHubCopilotPolicy', N'U') IS NULL
    CREATE TABLE audit.GitHubCopilotPolicy
    (
        Scope        NVARCHAR(120)  NOT NULL,
        Organization NVARCHAR(100)  NOT NULL,
        Policy       NVARCHAR(100)  NOT NULL,
        [State]      NVARCHAR(100)  NULL,       -- enabled, disabled, allow, block, unconfigured ...
        LoadedUtc    DATETIME2(0)   NOT NULL CONSTRAINT DF_GitHubCopilotPolicy_Loaded DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT PK_GitHubCopilotPolicy PRIMARY KEY (Scope, Organization, Policy)
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

/* The content filter (RAI policy) each deployment uses, by name (3.3.0); NULL where Azure names none. */
IF COL_LENGTH(N'audit.FoundryDeployment', N'RaiPolicy') IS NULL ALTER TABLE audit.FoundryDeployment ADD RaiPolicy NVARCHAR(200) NULL;

/* How much of a provisioned (PTU) deployment's capacity was used each day, in percent (3.3.0): Azure Monitor's
   AzureOpenAIProvisionedManagedUtilizationV2, its daily average and highest. At 100 requests are refused (429).
   NULL for a deployment that is not provisioned. */
IF COL_LENGTH(N'audit.FoundryUsageDay', N'PtuUtilizationAvg') IS NULL ALTER TABLE audit.FoundryUsageDay ADD PtuUtilizationAvg DECIMAL(9, 4) NULL;
IF COL_LENGTH(N'audit.FoundryUsageDay', N'PtuUtilizationMax') IS NULL ALTER TABLE audit.FoundryUsageDay ADD PtuUtilizationMax DECIMAL(9, 4) NULL;

/* Every account read, as it stands (3.3.0): how it is reached and signed in to, its logging, and its tags (owner,
   cost center and app read from them, whatever their case). Replaced whole for a subscription by each load. */
IF OBJECT_ID(N'audit.FoundryAccount', N'U') IS NULL
    CREATE TABLE audit.FoundryAccount
    (
        SubscriptionId       VARCHAR(36)    NOT NULL,
        ResourceGroup        NVARCHAR(90)   NOT NULL,
        Account              NVARCHAR(64)   NOT NULL,
        Kind                 NVARCHAR(50)   NULL,       -- OpenAI | AIServices
        Location             NVARCHAR(50)   NULL,
        SkuName              NVARCHAR(50)   NULL,
        PublicNetworkAccess  VARCHAR(16)    NULL,       -- Enabled | Disabled
        NetworkDefaultAction VARCHAR(16)    NULL,       -- Allow | Deny: what a network not listed may do
        PrivateEndpoints     INT            NULL,
        DisableLocalAuth     BIT            NULL,       -- 1: keys refused, Entra sign-in only
        IdentityType         VARCHAR(40)    NULL,       -- the account's own managed identity, if any
        Owner                NVARCHAR(256)  NULL,       -- from an owner tag
        CostCenter           NVARCHAR(256)  NULL,       -- from a cost-center tag
        App                  NVARCHAR(256)  NULL,       -- from an app or application tag
        Tags                 NVARCHAR(4000) NULL,       -- every tag, as JSON
        DiagnosticSettings   INT            NULL,       -- how many send its logs somewhere; NULL: could not be read
        LogCategories        NVARCHAR(1000) NULL,       -- the log categories or groups they send
        LoadedUtc            DATETIME2(0)   NOT NULL CONSTRAINT DF_FoundryAccount_Loaded DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT PK_FoundryAccount PRIMARY KEY (SubscriptionId, ResourceGroup, Account)
    );

/* Each account's content filters (RAI policies) (3.3.0): the base it starts from, its mode, and whether any
   filter is set looser than Microsoft's default (off, not blocking, or blocking only high severity). Detail keeps
   every filter as JSON. Replaced whole for a subscription by each load. */
IF OBJECT_ID(N'audit.FoundryContentFilter', N'U') IS NULL
    CREATE TABLE audit.FoundryContentFilter
    (
        SubscriptionId VARCHAR(36)    NOT NULL,
        ResourceGroup  NVARCHAR(90)   NOT NULL,
        Account        NVARCHAR(64)   NOT NULL,
        Policy         NVARCHAR(200)  NOT NULL,
        BasePolicy     NVARCHAR(200)  NULL,
        Mode           NVARCHAR(50)   NULL,       -- Default | Asynchronous_filter | Blocking ...
        Relaxed        BIT            NOT NULL,
        Detail         NVARCHAR(MAX)  NULL,
        LoadedUtc      DATETIME2(0)   NOT NULL CONSTRAINT DF_FoundryContentFilter_Loaded DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT PK_FoundryContentFilter PRIMARY KEY (SubscriptionId, ResourceGroup, Account, Policy)
    );

/* The lifecycle of each model a deployment uses, where it is deployed (3.3.0): from Azure's model list of the
   location. SkuName '' is the model's own retirement; a row per SKU where that SKU retires on a date of its own.
   Replaced for a location by each load that reads it. */
IF OBJECT_ID(N'audit.FoundryModelCatalog', N'U') IS NULL
    CREATE TABLE audit.FoundryModelCatalog
    (
        Location        NVARCHAR(50)   NOT NULL,
        ModelName       NVARCHAR(100)  NOT NULL,
        ModelVersion    NVARCHAR(32)   NOT NULL,
        SkuName         NVARCHAR(50)   NOT NULL,   -- '' for the model as a whole
        LifecycleStatus NVARCHAR(50)   NULL,       -- GenerallyAvailable | Preview | Deprecating | Deprecated ...
        RetiresDate     DATE           NULL,       -- inference stops on this day
        LoadedUtc       DATETIME2(0)   NOT NULL CONSTRAINT DF_FoundryModelCatalog_Loaded DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT PK_FoundryModelCatalog PRIMARY KEY (Location, ModelName, ModelVersion, SkuName)
    );

/* Every Azure OpenAI and Foundry account the app can see, in any subscription (3.3.0), from Azure Resource Graph:
   Loaded says whether its subscription is one the load reads (FoundrySubscriptions). An account with Loaded 0 has
   usage and cost the dashboard does not count. Replaced whole by each load that reads it. */
IF OBJECT_ID(N'audit.FoundryAccountSeen', N'U') IS NULL
    CREATE TABLE audit.FoundryAccountSeen
    (
        SubscriptionId VARCHAR(36)    NOT NULL,
        ResourceGroup  NVARCHAR(90)   NOT NULL,
        Account        NVARCHAR(64)   NOT NULL,
        Kind           NVARCHAR(50)   NULL,
        Location       NVARCHAR(50)   NULL,
        Loaded         BIT            NOT NULL,
        LoadedUtc      DATETIME2(0)   NOT NULL CONSTRAINT DF_FoundryAccountSeen_Loaded DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT PK_FoundryAccountSeen PRIMARY KEY (SubscriptionId, ResourceGroup, Account)
    );

/* ---- The wider AI bill, from Cost Management ---- */
/*
    What every AI service costs in Azure, not only Foundry's accounts: Copilot Studio and the Power Platform
    billed to Azure, Microsoft 365 Copilot pay-as-you-go, AI Search, Machine Learning and Bot Service. Read by
    Import-FoundryUsage.ps1 at each scope named (a subscription, a billing account, an EA enrollment or
    department), as actual cost and as amortized cost (reservations spread over the days they cover); each
    scope's budgets, and Cost Management's forecast of the days left in the month. Each optional: a scope Azure
    refuses is named in the run's detail.
*/
IF OBJECT_ID(N'audit.AiCostDay', N'U') IS NULL
    CREATE TABLE audit.AiCostDay
    (
        RowId          BIGINT IDENTITY(1, 1) NOT NULL,
        Scope          NVARCHAR(300)  NOT NULL,       -- /subscriptions/<id>, /providers/Microsoft.Billing/billingAccounts/<id> ...
        [Day]          DATE           NOT NULL,
        CostType       VARCHAR(10)    NOT NULL,       -- Actual | Amortized
        Service        NVARCHAR(60)   NOT NULL,       -- Foundry and Azure OpenAI | Copilot Studio | Power Platform | ...
        ResourceType   NVARCHAR(120)  NULL,
        SubscriptionId VARCHAR(36)    NULL,
        ResourceGroup  NVARCHAR(90)   NULL,
        ResourceName   NVARCHAR(260)  NULL,
        Meter          NVARCHAR(200)  NULL,
        Cost           DECIMAL(19, 6) NOT NULL,
        Currency       VARCHAR(8)     NULL,
        LoadedUtc      DATETIME2(0)   NOT NULL CONSTRAINT DF_AiCostDay_Loaded DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT PK_AiCostDay PRIMARY KEY NONCLUSTERED (RowId),
        INDEX CIX_AiCostDay CLUSTERED (CostType, [Day], Service)
    );
IF OBJECT_ID(N'audit.AiBudget', N'U') IS NULL
    CREATE TABLE audit.AiBudget
    (
        Scope          NVARCHAR(300)  NOT NULL,
        Budget         NVARCHAR(150)  NOT NULL,
        Category       VARCHAR(16)    NULL,           -- Cost | Usage
        Amount         DECIMAL(19, 4) NULL,
        TimeGrain      VARCHAR(20)    NULL,           -- Monthly | Quarterly | Annually ...
        StartDate      DATE           NULL,
        EndDate        DATE           NULL,
        CurrentSpend   DECIMAL(19, 4) NULL,           -- spent so far in the budget's current period
        ForecastSpend  DECIMAL(19, 4) NULL,           -- Azure's forecast for the period's end
        Currency       VARCHAR(8)     NULL,
        Filter         NVARCHAR(4000) NULL,           -- the budget's own filter, as JSON
        LoadedUtc      DATETIME2(0)   NOT NULL CONSTRAINT DF_AiBudget_Loaded DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT PK_AiBudget PRIMARY KEY (Scope, Budget)
    );
IF OBJECT_ID(N'audit.AiCostForecastDay', N'U') IS NULL
    CREATE TABLE audit.AiCostForecastDay
    (
        Scope          NVARCHAR(300)  NOT NULL,
        [Day]          DATE           NOT NULL,
        Cost           DECIMAL(19, 6) NOT NULL,       -- Cost Management's forecast of the AI services' actual cost that day
        Currency       VARCHAR(8)     NULL,
        LoadedUtc      DATETIME2(0)   NOT NULL CONSTRAINT DF_AiCostForecastDay_Loaded DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT PK_AiCostForecastDay PRIMARY KEY (Scope, [Day])
    );

/* ---- Microsoft Fabric, Copilot in Power BI and the Power Platform ---- */
/*
    What the executive page Leadership > Costs and savings reads beside the licenses, GitHub and Foundry: each a
    table a source's own export fills (tools/Import-CostCsv.ps1 loads a CSV of it), keyed so a day or an item
    loaded again replaces itself. The Fabric Capacity Metrics app gives the capacity and workload days, the Power
    Platform admin center the capacity, credits and per-user activity, and an inventory of the tenant's flows (the
    CoE Starter Kit's, or the admin center's export) the flows. The licenses themselves (Power Automate, Power
    Apps, Fabric and Power BI) are already in audit.LicenseSku, from Graph; their prices are in dash.CostPrice.
*/

/* One row per Fabric capacity per day: its size, how much of it was used, how often it was throttled, and what
   it cost. ActiveHours is under 24 on a day it was paused for part of. */
IF OBJECT_ID(N'audit.FabricCapacityDay', N'U') IS NULL
    CREATE TABLE audit.FabricCapacityDay
    (
        CapacityId       NVARCHAR(200)  NOT NULL,   -- the capacity's id, or its name where the export has no id
        [Day]            DATE           NOT NULL,
        CapacityName     NVARCHAR(200)  NULL,
        Sku              VARCHAR(16)    NULL,       -- F2 ... F2048, or P1 ... P5
        CapacityUnits    INT            NULL,       -- the SKU's capacity units: F64 is 64
        Region           NVARCHAR(64)   NULL,
        Billing          VARCHAR(16)    NULL,       -- PayAsYouGo | Reserved
        ActiveHours      DECIMAL(5, 2)  NULL,       -- hours it ran: 24, or fewer on a day it was paused
        CuSecondsUsed    DECIMAL(19, 2) NULL,       -- capacity unit seconds used, after smoothing
        PeakPercent      DECIMAL(9, 2)  NULL,       -- the day's highest utilization, as a percentage of the capacity
        ThrottledMinutes INT            NULL,       -- minutes interactive work was delayed or rejected
        Cost             DECIMAL(19, 4) NULL,       -- what the day cost, in Currency
        Currency         NVARCHAR(8)    NULL,
        LoadedUtc        DATETIME2(0)   NOT NULL CONSTRAINT DF_FabricCapacityDay_Loaded DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT PK_FabricCapacityDay PRIMARY KEY (CapacityId, [Day])
    );

/* One row per capacity, day and workload: the capacity units each kind of work used. Copilot in Power BI and
   Copilot in Fabric are workloads of their own here (a name that starts with Copilot), so their share of the
   capacity, and so of its cost, can be told apart. */
IF OBJECT_ID(N'audit.FabricWorkloadDay', N'U') IS NULL
    CREATE TABLE audit.FabricWorkloadDay
    (
        CapacityId  NVARCHAR(200)  NOT NULL,
        [Day]       DATE           NOT NULL,
        Workload    NVARCHAR(64)   NOT NULL,   -- Power BI, Copilot in Power BI, Data Engineering, Data Warehouse ...
        CuSeconds   DECIMAL(19, 2) NULL,
        Operations  BIGINT         NULL,       -- queries, refreshes, notebook runs, Copilot requests
        Users       INT            NULL,       -- distinct people behind them that day
        LoadedUtc   DATETIME2(0)   NOT NULL CONSTRAINT DF_FabricWorkloadDay_Loaded DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT PK_FabricWorkloadDay PRIMARY KEY (CapacityId, [Day], Workload)
    );

/* Every cloud flow of the tenant as the inventory last listed it: who owns it, whether it needs a premium
   license, how it is licensed, and how much it ran in the last 30 days. MinutesSavedPerRun, where someone has
   estimated it for a flow, values its runs; else Admin's FlowMinutesPerRun does. */
IF OBJECT_ID(N'audit.PowerAutomateFlow', N'U') IS NULL
    CREATE TABLE audit.PowerAutomateFlow
    (
        EnvironmentName    NVARCHAR(200)  NOT NULL,
        FlowId             NVARCHAR(100)  NOT NULL,
        FlowName           NVARCHAR(400)  NULL,
        OwnerUpn           NVARCHAR(256)  NULL,
        IsPremium          BIT            NULL,       -- uses a premium connector, so its owner needs a premium license
        LicenseModel       VARCHAR(16)    NULL,       -- PerUser | Process | Standard
        [State]            VARCHAR(16)    NULL,       -- Started | Stopped | Suspended
        CreatedUtc         DATETIME2(0)   NULL,
        LastRunUtc         DATETIME2(0)   NULL,
        Runs30             INT            NULL,       -- runs in the last 30 days
        FailedRuns30       INT            NULL,
        MinutesSavedPerRun DECIMAL(9, 2)  NULL,
        LoadedUtc          DATETIME2(0)   NOT NULL CONSTRAINT DF_PowerAutomateFlow_Loaded DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT PK_PowerAutomateFlow PRIMARY KEY (EnvironmentName, FlowId)
    );

/* Who used each Power Platform product lately, from the admin center's usage export: with audit.UserLicenseSku,
   a premium license held by someone who does not use the product. */
IF OBJECT_ID(N'audit.PowerPlatformUserActivity', N'U') IS NULL
    CREATE TABLE audit.PowerPlatformUserActivity
    (
        UserPrincipalName NVARCHAR(256) NOT NULL,
        Product           VARCHAR(32)   NOT NULL,   -- Power Apps | Power Automate | Copilot Studio
        LastActiveUtc     DATETIME2(0)  NULL,
        ActiveDays30      INT           NULL,
        LoadedUtc         DATETIME2(0)  NOT NULL CONSTRAINT DF_PowerPlatformUserActivity_Loaded DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT PK_PowerPlatformUserActivity PRIMARY KEY (UserPrincipalName, Product)
    );

/* The tenant's Dataverse capacity each day it was read: what the licenses entitle it to and what it uses. Used
   over entitled is overage, billed as add-on capacity. */
IF OBJECT_ID(N'audit.PowerPlatformCapacityDay', N'U') IS NULL
    CREATE TABLE audit.PowerPlatformCapacityDay
    (
        [Day]        DATE           NOT NULL,
        CapacityType VARCHAR(16)    NOT NULL,   -- Database | File | Log
        EntitledGb   DECIMAL(19, 2) NULL,
        UsedGb       DECIMAL(19, 2) NULL,
        LoadedUtc    DATETIME2(0)   NOT NULL CONSTRAINT DF_PowerPlatformCapacityDay_Loaded DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT PK_PowerPlatformCapacityDay PRIMARY KEY ([Day], CapacityType)
    );

/* Copilot Credits (Copilot Studio's agents) and AI Builder credits used, per environment and day, and how they
   were paid for: from a prepaid capacity pack, or pay-as-you-go through an Azure subscription. */
IF OBJECT_ID(N'audit.PowerPlatformCreditDay', N'U') IS NULL
    CREATE TABLE audit.PowerPlatformCreditDay
    (
        [Day]           DATE           NOT NULL,
        EnvironmentName NVARCHAR(200)  NOT NULL,
        Product         VARCHAR(32)    NOT NULL,   -- Copilot Studio | AI Builder
        Billing         VARCHAR(16)    NOT NULL,   -- Prepaid | PayAsYouGo
        Credits         DECIMAL(19, 2) NULL,
        Cost            DECIMAL(19, 4) NULL,       -- what pay-as-you-go charged; for Prepaid, NULL (the pack is a license)
        Currency        NVARCHAR(8)    NULL,
        LoadedUtc       DATETIME2(0)   NOT NULL CONSTRAINT DF_PowerPlatformCreditDay_Loaded DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT PK_PowerPlatformCreditDay PRIMARY KEY ([Day], EnvironmentName, Product, Billing)
    );

/* ---- The AI gateway and the spending limits at their sources ---- */
/*
    Model tokens through the organization's AI gateway (Azure API Management's llm-emit-token-metric or
    azure-openai-emit-token-metric policy, sent to Application Insights), read by tools/Import-GatewayTokenUsage.ps1:
    counts only, by the dimensions the gateway stamps (the business unit, and optionally the model and agent).
    The spending limits set where the spend happens, read by their loaders: Copilot spending policies (Microsoft
    Graph beta), GitHub budgets, and the gateway's token quotas (llm-token-limit). Read only: nothing here sets a limit.
*/
IF OBJECT_ID(N'audit.GatewayTokenDay', N'U') IS NULL
    CREATE TABLE audit.GatewayTokenDay
    (
        RowId            BIGINT IDENTITY(1, 1) NOT NULL,
        Gateway          NVARCHAR(260)  NOT NULL,       -- the API Management service's name
        [Day]            DATE           NOT NULL,       -- a UTC day
        BusinessUnit     NVARCHAR(256)  NOT NULL,       -- the business-unit dimension's value; '' where the gateway stamped none
        Model            NVARCHAR(200)  NOT NULL,       -- '' where not stamped
        Agent            NVARCHAR(200)  NOT NULL,       -- '' where not stamped
        Api              NVARCHAR(200)  NOT NULL,       -- the API Management API's id; '' where not given
        PromptTokens     BIGINT         NULL,
        CompletionTokens BIGINT         NULL,
        TotalTokens      BIGINT         NULL,
        Calls            BIGINT         NULL,           -- metric records summed (one a call, as the policy emits them)
        LoadedUtc        DATETIME2(0)   NOT NULL CONSTRAINT DF_GatewayTokenDay_Loaded DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT PK_GatewayTokenDay PRIMARY KEY NONCLUSTERED (RowId),
        INDEX CIX_GatewayTokenDay CLUSTERED ([Day], Gateway)
    );
IF OBJECT_ID(N'audit.SpendLimitSource', N'U') IS NULL
    CREATE TABLE audit.SpendLimitSource
    (
        Source           VARCHAR(16)    NOT NULL,       -- Graph | GitHub | Gateway
        ExternalId       NVARCHAR(400)  NOT NULL,       -- the limit's id at its source
        Kind             VARCHAR(24)    NOT NULL,       -- CopilotUser | CopilotResource | GatewayQuota | GatewayRate | GitHubBudget
        Name             NVARCHAR(400)  NULL,
        ScopeType        VARCHAR(24)    NULL,           -- AllUsers | Group | Environment | Agent | CounterKey | Enterprise | Organization | CostCenter | User
        Scope            NVARCHAR(1000) NULL,           -- the group, environment, counter key, organization ... it applies to
        Unit             VARCHAR(8)     NOT NULL,       -- Credits | Tokens | USD
        Amount           DECIMAL(19, 4) NULL,
        Period           VARCHAR(16)    NULL,           -- Minute | Hourly | Daily | Weekly | Monthly | Yearly
        PerUser          BIT            NOT NULL CONSTRAINT DF_SpendLimitSource_PerUser DEFAULT (0),   -- the amount is each person's (or each counter key's)
        Raw              NVARCHAR(MAX)  NULL,           -- the limit as its source gave it, JSON or XML
        LoadedUtc        DATETIME2(0)   NOT NULL CONSTRAINT DF_SpendLimitSource_Loaded DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT PK_SpendLimitSource PRIMARY KEY (Source, ExternalId)
    );

/* ---- The loads' own log ---------------------------------------------------- */

/* One row per run of either tool, from its start, so a failure shows too. Admin > Health reads the last of
   each source. CredentialExpiresUtc is when the GitHub token used expires, where GitHub says. */
IF OBJECT_ID(N'audit.ExternalUsageRun', N'U') IS NULL
    CREATE TABLE audit.ExternalUsageRun
    (
        RunId                INT IDENTITY(1, 1) NOT NULL CONSTRAINT PK_ExternalUsageRun PRIMARY KEY,
        Source               VARCHAR(16)    NOT NULL,   -- GitHub | Foundry | Gateway | SpendLimits | AgentActions
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
    GRANT SELECT ON audit.FabricCapacityDay TO CopilotInsightsApp;
    GRANT SELECT ON audit.FabricWorkloadDay TO CopilotInsightsApp;
    GRANT SELECT ON audit.PowerAutomateFlow TO CopilotInsightsApp;
    GRANT SELECT ON audit.PowerPlatformUserActivity TO CopilotInsightsApp;
    GRANT SELECT ON audit.PowerPlatformCapacityDay TO CopilotInsightsApp;
    GRANT SELECT ON audit.PowerPlatformCreditDay TO CopilotInsightsApp;
    GRANT SELECT ON audit.GitHubIdentity TO CopilotInsightsApp;
    GRANT SELECT ON audit.GitHubCopilotPolicy TO CopilotInsightsApp;
    GRANT SELECT ON audit.FoundryAccount TO CopilotInsightsApp;
    GRANT SELECT ON audit.FoundryContentFilter TO CopilotInsightsApp;
    GRANT SELECT ON audit.FoundryModelCatalog TO CopilotInsightsApp;
    GRANT SELECT ON audit.FoundryAccountSeen TO CopilotInsightsApp;
    GRANT SELECT ON audit.AiCostDay TO CopilotInsightsApp;
    GRANT SELECT ON audit.AiBudget TO CopilotInsightsApp;
    GRANT SELECT ON audit.AiCostForecastDay TO CopilotInsightsApp;
    GRANT SELECT ON audit.GatewayTokenDay TO CopilotInsightsApp;
    GRANT SELECT ON audit.SpendLimitSource TO CopilotInsightsApp;
END;
