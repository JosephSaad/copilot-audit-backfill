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

        /* Whether the user held a Copilot license. Not in the audit record:
           filled from audit.CopilotLicense when that table exists (see the
           procedure), NULL otherwise.                                          */
        IsCopilotLic                    BIT             NULL,

        /* ---- Beyond the source table's columns, cheap to carry and hard to
           reconstruct later. Drop them if the shape has to match exactly. ---- */
        UtcTimeStamp                    DATETIME2(0)    NULL,   -- $.CreationTime, typed
        AppIdentity                     NVARCHAR(200)   NULL,   -- $.AppIdentity
        ThreadId                        NVARCHAR(400)   NULL,   -- $.CopilotEventData.ThreadId
        AgentId                         NVARCHAR(400)   NULL,   -- $.AgentId, else $.CopilotEventData.AgentId
        AgentName                       NVARCHAR(400)   NULL,   -- $.AgentName, the agent's friendly name, else $.CopilotEventData.AgentName
        ModelName                       NVARCHAR(200)   NULL,   -- ModelTransparencyDetails[0].ModelName
        ModelProvider                   NVARCHAR(200)   NULL,   -- ModelTransparencyDetails[0].ModelProviderName
        AccessedResourceCount           INT             NULL,
        PromptCount                     INT             NULL,
        FlattenedUtc                    DATETIME2(0)    NOT NULL
                                            CONSTRAINT DF_RawLogFlattened_Flattened DEFAULT (SYSUTCDATETIME())
    );

/*
    ---- Every other field of the record --------------------------------

    Each scalar the Copilot audit record carries gets a typed column, named
    after its JSON path (CopilotEventData.X as CopilotEventData_X), except
    seventeen that carry no information (audit.RawLogRecordShape, below). Types come
    from a profile of 19 million production records: identifiers that were
    GUID text in every row are UNIQUEIDENTIFIER (16 bytes rather than 72), and
    a value that does not parse as the type is left NULL rather than failing
    the batch. The small arrays are kept as JSON text (NULL when empty); the
    two large ones, AccessedResources and Contexts, get a row per element in
    audit.RawLogAccessedResource and audit.RawLogContext below.

    Added in place, so a 2.1.0 table keeps its rows; the new columns stay NULL
    until those records are flattened again (EXEC audit.usp_FlattenCopilotAudit
    once, with no @SinceUtc).
*/
DECLARE @cols TABLE (Name SYSNAME, Def NVARCHAR(200));
INSERT INTO @cols (Name, Def) VALUES
    /* top-level scalars ($.X) */
    (N'OrganizationId',                          N'UNIQUEIDENTIFIER NULL'),
    (N'UserKey',                                 N'UNIQUEIDENTIFIER NULL'),
    (N'ClientIP',                                N'VARCHAR(64) NULL'),
    (N'ApplicationName',                         N'NVARCHAR(200) NULL'),
    (N'PlatformAgentId',                         N'VARCHAR(256) NULL'),
    (N'ResultStatus',                            N'VARCHAR(64) NULL'),
    (N'AppAccessContext_APIId',                  N'VARCHAR(64) NULL'),
    /* $.CopilotEventData.X */
    (N'CopilotEventData_LicenseType',            N'VARCHAR(32) NULL'),
    (N'CopilotEventData_ConversationId',         N'VARCHAR(256) NULL'),
    (N'CopilotEventData_CorrelationId',          N'UNIQUEIDENTIFIER NULL'),
    (N'CopilotEventData_SensitivityLabelId',     N'UNIQUEIDENTIFIER NULL'),
    (N'CopilotEventData_TargetAgentName',        N'NVARCHAR(200) NULL'),
    (N'CopilotEventData_TargetPlatformAgentId',  N'VARCHAR(256) NULL'),
    (N'CopilotEventData_DLPEvaluationDeferred',  N'TINYINT NULL'),
    (N'CopilotEventData_MemoryUpdated',          N'BIT NULL'),
    (N'CopilotEventData_MemoryCorrelationId',    N'VARCHAR(64) NULL'),
    (N'CopilotEventData_FabricCapacityId',       N'UNIQUEIDENTIFIER NULL'),
    (N'CopilotEventData_PlatformAgentId',        N'VARCHAR(256) NULL'),
    (N'CopilotEventData_TaskId',                 N'UNIQUEIDENTIFIER NULL'),
    (N'ModelVersion',                            N'VARCHAR(32) NULL'),        -- ModelTransparencyDetails[0].ModelVersion
    (N'MessageCount',                            N'INT NULL'),                -- Messages[*], prompts and responses
    /* small arrays, as JSON text; NULL when empty */
    (N'CopilotEventData_AISystemPlugin',         N'NVARCHAR(MAX) NULL'),
    (N'CopilotEventData_ModelTransparencyDetails', N'NVARCHAR(MAX) NULL'),
    (N'CopilotEventData_ParticipatingAgents',    N'NVARCHAR(MAX) NULL'),
    (N'CopilotEventData_SourceThreadIds',        N'NVARCHAR(MAX) NULL');

DECLARE @n SYSNAME, @d NVARCHAR(200), @ddl NVARCHAR(600);
DECLARE c CURSOR LOCAL FAST_FORWARD FOR SELECT Name, Def FROM @cols;
OPEN c; FETCH NEXT FROM c INTO @n, @d;
WHILE @@FETCH_STATUS = 0
BEGIN
    IF COL_LENGTH(N'audit.RawLogFlattened', @n) IS NULL
    BEGIN
        SET @ddl = N'ALTER TABLE audit.RawLogFlattened ADD ' + QUOTENAME(@n) + N' ' + @d + N';';
        EXEC (@ddl);
    END;
    FETCH NEXT FROM c INTO @n, @d;
END;
CLOSE c; DEALLOCATE c;

/*
    Seventeen fields carry no information in a 19-million-record production
    profile: each has one value on every record where it appears, or is always
    empty (Operation, RecordType, Workload, UserType, Version,
    CopilotLogVersion, SubjectType, AgentVersion, AgentPlatform,
    AgentBlueprintId, AppAccessContext.ClientAppId, CopilotEventData.
    PlatformAgentType and .IsSpawnTask; AssociatedAdminUnits and its names,
    and CopilotEventData.MessageIds, .GuardrailDetails and .TargetTaskIds).
    They are not stored per row. The flattening counts each combination it
    sees here instead - the empty ones as whether they had anything - and
    says so if any of them ever takes a second value, so the day one starts
    to vary it is noticed rather than lost. OrganizationId has one value too,
    but stays on every row: it is what keeps a database holding more than one
    tenant correct.
*/
IF OBJECT_ID(N'audit.RawLogRecordShape', N'U') IS NULL
    CREATE TABLE audit.RawLogRecordShape
    (
        ShapeHash                           BINARY(32)       NOT NULL CONSTRAINT PK_RawLogRecordShape PRIMARY KEY,
        RecordType                          SMALLINT         NULL,
        Operation                           VARCHAR(64)      NULL,
        Workload                            VARCHAR(32)      NULL,
        UserType                            TINYINT          NULL,
        [Version]                           TINYINT          NULL,
        CopilotLogVersion                   VARCHAR(32)      NULL,
        SubjectType                         TINYINT          NULL,
        AgentVersion                        VARCHAR(32)      NULL,
        AgentPlatform                       VARCHAR(64)      NULL,
        AgentBlueprintId                    UNIQUEIDENTIFIER NULL,
        AppAccessContext_ClientAppId        UNIQUEIDENTIFIER NULL,
        CopilotEventData_PlatformAgentType  VARCHAR(64)      NULL,
        CopilotEventData_IsSpawnTask        BIT              NULL,
        HasAdminUnits                       BIT              NOT NULL,   -- AssociatedAdminUnits or its names had a value
        HasMessageIds                       BIT              NOT NULL,   -- CopilotEventData.MessageIds not empty
        HasGuardrailDetails                 BIT              NOT NULL,
        HasTargetTaskIds                    BIT              NOT NULL,
        Records                             BIGINT           NOT NULL,   -- flattened with this shape since the last full rebuild; a re-flatten counts again
        FirstSeenUtc                        DATETIME2(0)     NOT NULL CONSTRAINT DF_RawLogRecordShape_First DEFAULT (SYSUTCDATETIME()),
        LastSeenUtc                         DATETIME2(0)     NOT NULL CONSTRAINT DF_RawLogRecordShape_Last DEFAULT (SYSUTCDATETIME())
    );

/*
    One row per element of CopilotEventData.AccessedResources: what Copilot
    read, where, under which label and policy. 1.8 elements per record in the
    production profile, so about twice the rows of the flattened table; page
    compression keeps the repetitive URLs and types small.

    Blocked: the element's Status is present and not Success, or its Purview
    policy outcome is anything other than None. PolicyDetails arrives as
    JSON text inside the JSON; PolicyOutcomes lists the outcomes that were
    not None.
*/
IF OBJECT_ID(N'audit.RawLogAccessedResource', N'U') IS NULL
    CREATE TABLE audit.RawLogAccessedResource
    (
        RecordId            NVARCHAR(440)    NOT NULL,   -- audit.RawLogFlattened (440, not 450, so the key stays under 900 bytes)
        Ordinal             SMALLINT         NOT NULL,   -- position in the array, from 0
        [Action]            NVARCHAR(450)    NULL,       -- Read, Create ...
        [Type]              NVARCHAR(200)    NULL,       -- File, EmailMessage, TeamsMessage, a URL scheme ...
        Id                  NVARCHAR(1000)   NULL,
        Name                NVARCHAR(4000)   NULL,
        SiteUrl             NVARCHAR(MAX)    NULL,
        Site                NVARCHAR(400)    NULL,       -- SiteUrl cut to https://host/sites/name (or /personal/name, /teams/name)
        ListItemUniqueId    UNIQUEIDENTIFIER NULL,
        SensitivityLabelId  UNIQUEIDENTIFIER NULL,
        XPIADetected        BIT              NULL,       -- cross-prompt injection found in this resource
        [Status]            VARCHAR(64)      NULL,
        PolicyDetails       NVARCHAR(MAX)    NULL,
        PolicyOutcomes      NVARCHAR(400)    NULL,       -- the outcomes other than None, '; '-joined
        IsBlocked           BIT              NOT NULL,
        CONSTRAINT PK_RawLogAccessedResource PRIMARY KEY (RecordId, Ordinal) WITH (DATA_COMPRESSION = PAGE)
    );

/* One row per element of CopilotEventData.Contexts: where the user was (the open document, meeting, chat). */
IF OBJECT_ID(N'audit.RawLogContext', N'U') IS NULL
    CREATE TABLE audit.RawLogContext
    (
        RecordId     NVARCHAR(440)   NOT NULL,
        Ordinal      SMALLINT        NOT NULL,
        Id           NVARCHAR(4000)  NULL,
        [Type]       VARCHAR(64)     NULL,
        ContainerId  VARCHAR(64)     NULL,
        CONSTRAINT PK_RawLogContext PRIMARY KEY (RecordId, Ordinal) WITH (DATA_COMPRESSION = PAGE)
    );

/* Reporting filters hit the timestamp and the user far more than anything
   else. A rowstore table (not columnstore): the procedure MERGEs into it. */
IF OBJECT_ID(N'audit.RawLogFlattened', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes
                   WHERE object_id = OBJECT_ID(N'audit.RawLogFlattened') AND name = N'IX_RawLogFlattened_UtcTimeStamp')
    EXEC (N'CREATE INDEX IX_RawLogFlattened_UtcTimeStamp
            ON audit.RawLogFlattened (UtcTimeStamp) INCLUDE ([User], App);');

/*
    Optional license lookup. Create and populate it (from Entra/Graph, or a
    license report) and the flatten procedure fills IsCopilotLic; leave it out
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

/*
    The value catalog: every distinct value of the columns that carry an
    opaque code or a short list - label ids, agent and app ids, license types,
    model names, regions - so an administrator can give each one a readable
    name (dash.ValueName, Admin > Value names). The flatten procedure adds each
    batch's values (audit.usp_CatalogValues); a column that passes 10,000
    distinct values is marked Capped and stops collecting, since a list that
    long is not one to name by hand.

    Records counts every time a record is flattened, so a record re-flattened
    by the nightly lookback counts again: good for ranking, not for reporting.
*/
IF OBJECT_ID(N'audit.ValueCatalogColumn', N'U') IS NULL
    CREATE TABLE audit.ValueCatalogColumn
    (
        ColumnId     SMALLINT        NOT NULL CONSTRAINT PK_ValueCatalogColumn PRIMARY KEY,
        TableName    SYSNAME         NOT NULL,   -- in the audit schema
        ColumnName   SYSNAME         NOT NULL,
        Label        NVARCHAR(200)   NOT NULL,   -- what an administrator sees
        DistinctValues INT           NOT NULL CONSTRAINT DF_ValueCatalogColumn_Distinct DEFAULT (0),
        Capped       BIT             NOT NULL CONSTRAINT DF_ValueCatalogColumn_Capped DEFAULT (0),
        UpdatedUtc   DATETIME2(0)    NULL,
        CONSTRAINT UQ_ValueCatalogColumn UNIQUE (TableName, ColumnName)
    );
IF OBJECT_ID(N'audit.ValueCatalog', N'U') IS NULL
    CREATE TABLE audit.ValueCatalog
    (
        ColumnId     SMALLINT        NOT NULL,
        Value        NVARCHAR(400)   NOT NULL,
        Records      BIGINT          NOT NULL,
        FirstSeenUtc DATETIME2(0)    NOT NULL CONSTRAINT DF_ValueCatalog_First DEFAULT (SYSUTCDATETIME()),
        LastSeenUtc  DATETIME2(0)    NOT NULL CONSTRAINT DF_ValueCatalog_Last DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT PK_ValueCatalog PRIMARY KEY (ColumnId, Value)
    );
/*
    The agent catalog: every agent the audit log names, with the
    friendly name the log gave it last. A record names its agent in AppIdentity
    (Copilot.Studio.<...>, MicrosoftAgent.<...>), in AgentId, or in both, and
    carries the agent's readable name in AgentName - on some records, not all,
    and makers rename agents. One row per pair of AppIdentity and AgentId as
    they appear together (N'' where a record has none), kept by the flatten
    procedure for each batch (audit.usp_CatalogAgents), so the name is known
    from the whole history however short the window a later step reads.
    bi.usp_NameAgents names bi.DimAgent from it.

    Nothing here counts records: a record re-flattened by the nightly lookback
    would count again. FirstSeenUtc and LastSeenUtc are the records' own times.
*/
IF OBJECT_ID(N'audit.AgentCatalog', N'U') IS NULL
    CREATE TABLE audit.AgentCatalog
    (
        AgentCatalogId INT IDENTITY(1, 1) NOT NULL CONSTRAINT PK_AgentCatalog PRIMARY KEY,
        AppIdentity    NVARCHAR(200)   NOT NULL,   -- $.AppIdentity; N'' when the record has none
        AgentId        NVARCHAR(400)   NOT NULL,   -- $.AgentId; N'' when the record has none
        AgentName      NVARCHAR(400)   NULL,       -- $.AgentName on the latest record that carried one
        NameSeenUtc    DATETIME2(0)    NULL,       -- that record's time
        FirstSeenUtc   DATETIME2(0)    NULL,       -- the earliest record of this agent
        LastSeenUtc    DATETIME2(0)    NULL,       -- the latest
        CONSTRAINT UQ_AgentCatalog UNIQUE (AppIdentity, AgentId)
    );

/* The ids are stable: names given in dash.ValueName refer to them. */
MERGE audit.ValueCatalogColumn AS t
USING (VALUES
    (1,  N'RawLogFlattened',        N'OrganizationId',                          N'Tenant (OrganizationId)'),
    (2,  N'RawLogFlattened',        N'App',                                     N'App (AppHost)'),
    (3,  N'RawLogFlattened',        N'Location',                                N'Region (ClientRegion)'),
    (4,  N'RawLogFlattened',        N'AppIdentity',                             N'App identity'),
    (5,  N'RawLogFlattened',        N'ApplicationName',                         N'Application name'),
    (6,  N'RawLogFlattened',        N'AgentId',                                 N'Agent id'),
    (7,  N'RawLogFlattened',        N'PlatformAgentId',                         N'Platform agent id'),
    (8,  N'RawLogFlattened',        N'ResultStatus',                            N'Result status'),
    (9,  N'RawLogFlattened',        N'AppAccessContext_APIId',                  N'API id (AppAccessContext.APIId)'),
    (10, N'RawLogFlattened',        N'CopilotEventData_LicenseType',            N'License type'),
    (11, N'RawLogFlattened',        N'CopilotEventData_SensitivityLabelId',     N'Sensitivity label of the interaction'),
    (12, N'RawLogFlattened',        N'CopilotEventData_TargetAgentName',        N'Agent called (TargetAgentName)'),
    (13, N'RawLogFlattened',        N'CopilotEventData_TargetPlatformAgentId',  N'Agent called, platform id'),
    (14, N'RawLogFlattened',        N'CopilotEventData_FabricCapacityId',       N'Fabric capacity'),
    (15, N'RawLogFlattened',        N'CopilotEventData_PlatformAgentId',        N'Platform agent id (CopilotEventData)'),
    (16, N'RawLogFlattened',        N'ModelName',                               N'Model'),
    (17, N'RawLogFlattened',        N'ModelProvider',                           N'Model provider'),
    (18, N'RawLogFlattened',        N'ModelVersion',                            N'Model version'),
    (30, N'RawLogAccessedResource', N'SensitivityLabelId',                      N'Sensitivity label of a resource'),
    (31, N'RawLogAccessedResource', N'Type',                                    N'Resource type'),
    (32, N'RawLogAccessedResource', N'Action',                                  N'Resource action'),
    (33, N'RawLogAccessedResource', N'Status',                                  N'Resource status'),
    (40, N'RawLogContext',          N'Type',                                    N'Context type')
) AS s (ColumnId, TableName, ColumnName, Label)
ON t.ColumnId = s.ColumnId
WHEN MATCHED AND (t.Label <> s.Label OR t.TableName <> s.TableName OR t.ColumnName <> s.ColumnName)
    THEN UPDATE SET Label = s.Label, TableName = s.TableName, ColumnName = s.ColumnName
WHEN NOT MATCHED THEN INSERT (ColumnId, TableName, ColumnName, Label) VALUES (s.ColumnId, s.TableName, s.ColumnName, s.Label);
