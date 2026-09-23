/*
    Copilot audit backfill - flattened reporting table
    ------------------------------------------------------------------------
    One row per audit record, with the CopilotEventData JSON shredded into
    columns. Column names and types match dbo.RawLogFlattened so existing
    reports can point here unchanged.

    Populated by audit.usp_FlattenCopilotAudit (sql/Create-CopilotFlattenProcedure.sql),
    which reads audit.vCopilotAuditRecord - the Purview-export-shaped view. It
    is keyed on RecordId, so the multi-valued parts of the record (a Copilot
    interaction can touch many resources) are aggregated into '; '-delimited
    lists rather than exploded into extra rows. Every JSON path below is from
    Microsoft's published Copilot audit schema:
      https://learn.microsoft.com/purview/audit-copilot
      https://learn.microsoft.com/office/office-365-management-api/copilot-schema

    Idempotent. Single batch (no GO) so it runs the same from SSMS or code.
    Requires SQL Server 2017+ (STRING_AGG).
*/
SET NOCOUNT ON;

IF SCHEMA_ID(N'audit') IS NULL
    EXEC (N'CREATE SCHEMA audit AUTHORIZATION dbo;');

IF OBJECT_ID(N'audit.RawLogFlattened', N'U') IS NULL
    CREATE TABLE audit.RawLogFlattened
    (
        /* $.Id - the audit record id, one row per record.                      */
        RecordId                        NVARCHAR(450)   NOT NULL
                                            CONSTRAINT PK_RawLogFlattened_RecordId PRIMARY KEY,

        /* $.CreationTime, kept as text in the source's shape. UtcTimeStamp
           below is the same instant typed, for anyone who wants to filter.     */
        [TimeStamp]                     NVARCHAR(4000)  NULL,

        /* $.UserId                                                             */
        [User]                          NVARCHAR(4000)  NULL,

        /* $.CopilotEventData.AppHost - Word, Teams, BizChat, Outlook, Bing ... */
        App                             NVARCHAR(4000)  NULL,

        /* $.ClientRegion - the user's region when they performed the operation */
        Location                        NVARCHAR(MAX)   NULL,

        /* $.CopilotEventData.Contexts[*].Id - where the user was during the
           interaction (the open document, meeting, channel or chat).           */
        [App context]                   NVARCHAR(4000)  NULL,

        /* $.CopilotEventData.AccessedResources[*].Name                         */
        [Accessed Resources]            NVARCHAR(MAX)   NULL,

        /* $.CopilotEventData.AccessedResources[*].SiteUrl                      */
        [Accessed Resource Locations]   NVARCHAR(MAX)   NULL,

        /* $.CopilotEventData.AccessedResources[*].Action - Read, Create, ...   */
        [Action]                        NVARCHAR(MAX)   NULL,

        /* $.CopilotEventData.AISystemPlugin[*].Id - BingWebSearch means the
           interaction was grounded on the public web.                          */
        AIPlugin                        NVARCHAR(MAX)   NULL,

        /* $.CopilotEventData.Messages[*].JailbreakDetected - 'true'/'false'
           kept as text to match the source column.                             */
        JailbreakDetected               NVARCHAR(5)     NULL,

        /* $.CopilotEventData.AccessedResources[*].XPIADetected - cross prompt
           injection attack detected in an accessed resource.                   */
        PromptInjectionDetected         NVARCHAR(5)     NULL,

        /* SiteUrl of any accessed resource carrying PolicyDetails or a failed
           Status - Copilot's access to it was blocked or restricted.           */
        BlockedResourceUrls             NVARCHAR(MAX)   NULL,

        /* $.CopilotEventData.AccessedResources[*].SensitivityLabelId           */
        SensitivityLabelIds             NVARCHAR(MAX)   NULL,

        /* Whether the user held a Copilot licence. Not in the audit record:
           filled from audit.CopilotLicense when that table exists (see the
           procedure), NULL otherwise.                                          */
        IsCopilotLic                    BIT             NULL,

        /* ---- Beyond the source table's columns, cheap to carry and hard to
           reconstruct later. Drop them if the shape has to match exactly. ---- */
        UtcTimeStamp                    DATETIME2(0)    NULL,   -- $.CreationTime, typed
        AppIdentity                     NVARCHAR(200)   NULL,   -- $.AppIdentity
        ThreadId                        NVARCHAR(400)   NULL,   -- $.CopilotEventData.ThreadId
        AgentId                         NVARCHAR(400)   NULL,   -- $.CopilotEventData.AgentId
        AgentName                       NVARCHAR(400)   NULL,   -- $.CopilotEventData.AgentName
        ModelName                       NVARCHAR(200)   NULL,   -- ModelTransparencyDetails[0].ModelName
        ModelProvider                   NVARCHAR(200)   NULL,   -- ModelTransparencyDetails[0].ModelProviderName
        AccessedResourceCount           INT             NULL,
        PromptCount                     INT             NULL,
        FlattenedUtc                    DATETIME2(0)    NOT NULL
                                            CONSTRAINT DF_RawLogFlattened_Flattened DEFAULT (SYSUTCDATETIME())
    );

/* Reporting filters hit the timestamp and the user far more than anything
   else. A rowstore table (not columnstore): the procedure MERGEs into it. */
IF OBJECT_ID(N'audit.RawLogFlattened', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes
                   WHERE object_id = OBJECT_ID(N'audit.RawLogFlattened') AND name = N'IX_RawLogFlattened_UtcTimeStamp')
    EXEC (N'CREATE INDEX IX_RawLogFlattened_UtcTimeStamp
            ON audit.RawLogFlattened (UtcTimeStamp) INCLUDE ([User], App);');

/*
    Optional licence lookup. Create and populate it (from Entra/Graph, or a
    licence report) and the flatten procedure fills IsCopilotLic; leave it out
    and IsCopilotLic stays NULL. Matched on UPN, case-insensitively.
*/
IF OBJECT_ID(N'audit.CopilotLicense', N'U') IS NULL
    CREATE TABLE audit.CopilotLicense
    (
        UserPrincipalName   NVARCHAR(512)   NOT NULL
                                CONSTRAINT PK_CopilotLicense PRIMARY KEY,
        /* Nullable on purpose: NULL means "not known", which is the honest
           answer for a user who appears in the audit data but has since left
           the directory. 0 would claim they were unlicensed. */
        IsCopilotLic        BIT             NULL,
        UpdatedUtc          DATETIME2(0)    NOT NULL CONSTRAINT DF_CopilotLicense_Updated DEFAULT (SYSUTCDATETIME())
    );
