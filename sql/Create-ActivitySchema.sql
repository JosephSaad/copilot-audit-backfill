/*
    The activity database (2.6.9): AI apps outside Copilot, Power BI and Power Apps
    ------------------------------------------------------------------------
    A database of its own, loaded by the same backfill with Purview record
    types rather than operations (Invoke-CopilotAuditBackfill.ps1 -RecordTypes):

      ConnectedAIAppInteraction  calls to Azure AI Foundry and Azure OpenAI from
                                 the organization's own apps, where Purview
                                 collects them (Foundry's "Powered by Microsoft
                                 Purview", or Defender for Cloud's data security
                                 for AI interactions); and Entra-registered apps
                                 through the Purview SDK
      PowerBIAudit               Power BI activity: reports viewed, exported,
                                 refreshed, shared
      PowerAppsApp               Power Apps launched and managed

    The records land in audit.CopilotAudit as every load's do (installed by
    Create-CopilotAuditSchema.sql and Create-CopilotLoadProcedure.sql here too);
    audit.usp_ShredActivity (Create-ActivityProcedure.sql) reads them into:

      audit.AIAppCall          one row per AI app record: the resource, the app,
                               the subscription, who called (a person, or no one
                               the record can name), the thread, failures
      audit.PlatformActivity   one row per Power BI or Power Apps record: who,
                               what, and on which item

    The Copilot database never reads these tables from the dashboard: its own
    audit.usp_RollupActivity (Create-CopilotActivityRollup.sql) copies what the
    dashboard shows into tables of the Copilot database each night.

    Nothing here holds prompt or response text: Purview's records carry none.
    Client IP addresses are not copied.

    Idempotent. Single batch (no GO). Requires SQL Server 2017+.
*/
SET NOCOUNT ON;

IF SCHEMA_ID(N'audit') IS NULL
    EXEC (N'CREATE SCHEMA audit AUTHORIZATION dbo;');

/* One row per ConnectedAIAppInteraction record. A record is one message: a prompt or a response, so calls are about
   half the rows, and distinct ThreadIds are the conversations. */
IF OBJECT_ID(N'audit.AIAppCall', N'U') IS NULL
    CREATE TABLE audit.AIAppCall
    (
        RecordId        VARCHAR(64)    NOT NULL,
        CreatedDateTime DATETIME2(0)   NOT NULL,   -- UTC
        UserId          NVARCHAR(256)  NULL,       -- a sign-in, or Guest where the app called with its own key or identity
        UserType        INT            NULL,       -- 0 a person; 10 Guest (no one the record can name)
        UserKey         NVARCHAR(128)  NULL,
        CallerKind      VARCHAR(16)    NOT NULL,   -- Person | Unattributed
        Caller          NVARCHAR(256)  NOT NULL,   -- the sign-in for a person; else 'unattributed'
        AppIdentity     NVARCHAR(400)  NULL,       -- ConnectedAIApp.AzureAI.<resource> or ConnectedAIApp.Entra.<app id>
        AppHost         NVARCHAR(200)  NULL,
        ApplicationName NVARCHAR(200)  NULL,
        [Resource]      NVARCHAR(200)  NOT NULL,   -- the Azure resource (retail-prod-aif-0147), or the app id; '' where neither
        ContextId       NVARCHAR(400)  NULL,       -- the endpoint, such as retail-prod-aif-0147.openai.azure.com
        SubscriptionId  VARCHAR(64)    NULL,
        ThreadId        NVARCHAR(400)  NULL,
        Prompts         INT            NOT NULL,
        Responses       INT            NOT NULL,
        Failed          BIT            NOT NULL,   -- a resource it reached reported Failure
        ShreddedUtc     DATETIME2(0)   NOT NULL CONSTRAINT DF_AIAppCall_Shredded DEFAULT (SYSUTCDATETIME()),
        INDEX CCI_AIAppCall CLUSTERED COLUMNSTORE
    );
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'audit.AIAppCall') AND name = N'UX_AIAppCall_Record')
    CREATE UNIQUE NONCLUSTERED INDEX UX_AIAppCall_Record ON audit.AIAppCall (RecordId);

/* One row per PowerBIAudit or PowerAppsApp record. */
IF OBJECT_ID(N'audit.PlatformActivity', N'U') IS NULL
    CREATE TABLE audit.PlatformActivity
    (
        RecordId        VARCHAR(64)    NOT NULL,
        CreatedDateTime DATETIME2(0)   NOT NULL,   -- UTC
        Product         VARCHAR(32)    NOT NULL,   -- Power BI | Power Apps
        Operation       NVARCHAR(128)  NULL,       -- ViewReport, ExportReport, LaunchPowerApp ...
        UserId          NVARCHAR(256)  NULL,
        UserType        INT            NULL,
        ItemName        NVARCHAR(400)  NULL,       -- the report, dashboard, dataset or app
        Container       NVARCHAR(400)  NULL,       -- the Power BI workspace, or the Power Apps environment
        ShreddedUtc     DATETIME2(0)   NOT NULL CONSTRAINT DF_PlatformActivity_Shredded DEFAULT (SYSUTCDATETIME()),
        INDEX CCI_PlatformActivity CLUSTERED COLUMNSTORE
    );
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'audit.PlatformActivity') AND name = N'UX_PlatformActivity_Record')
    CREATE UNIQUE NONCLUSTERED INDEX UX_PlatformActivity_Record ON audit.PlatformActivity (RecordId);

/* 2.7.0: one row per data loss prevention (DLP) record, where the activity database is asked for the DLP record types
   (ComplianceDLPSharePoint, ComplianceDLPExchange, DLPEndpoint): which policy and rule matched, for whom, in which
   workload. The first policy and rule of the record; no file or message is named. Teams meeting AI notes
   (TeamCopilotInteraction) go to audit.PlatformActivity, as the product Teams AI notes. */
IF OBJECT_ID(N'audit.DlpMatch', N'U') IS NULL
    CREATE TABLE audit.DlpMatch
    (
        RecordId        VARCHAR(64)    NOT NULL,
        CreatedDateTime DATETIME2(0)   NOT NULL,   -- UTC
        Workload        NVARCHAR(64)   NULL,       -- SharePoint, OneDrive, Exchange, Endpoint
        Operation       NVARCHAR(128)  NULL,       -- DlpRuleMatch, DlpRuleUndo, DlpInfo, or an endpoint activity that matched a policy
        UserId          NVARCHAR(256)  NULL,
        PolicyName      NVARCHAR(400)  NULL,
        RuleName        NVARCHAR(400)  NULL,
        Severity        NVARCHAR(32)   NULL,
        Policies        INT            NOT NULL,   -- the policies the record names
        ShreddedUtc     DATETIME2(0)   NOT NULL CONSTRAINT DF_DlpMatch_Shredded DEFAULT (SYSUTCDATETIME()),
        INDEX CCI_DlpMatch CLUSTERED COLUMNSTORE
    );
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'audit.DlpMatch') AND name = N'UX_DlpMatch_Record')
    CREATE UNIQUE NONCLUSTERED INDEX UX_DlpMatch_Record ON audit.DlpMatch (RecordId);
