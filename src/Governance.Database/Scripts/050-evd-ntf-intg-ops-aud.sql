-- Evidence (ledger metadata, regular content), notifications (outbox), integration snapshots,
-- operations (findings and events), audit (ledger).
SET NOCOUNT ON;

------------------------------------------------------------------------------------------------
-- evd
------------------------------------------------------------------------------------------------
-- File content in a regular table, so it can be purged at the end of the retention period while
-- its hash and metadata stay in the ledger.
IF OBJECT_ID('evd.EvidenceContent') IS NULL
CREATE TABLE evd.EvidenceContent
(
    ContentId  bigint IDENTITY CONSTRAINT PK_EvidenceContent PRIMARY KEY,
    Bytes      varbinary(max) NULL,
    PurgedUtc  datetime2      NULL
);

IF OBJECT_ID('evd.EvidenceItem') IS NULL
CREATE TABLE evd.EvidenceItem
(
    EvidenceId        bigint IDENTITY NOT NULL,
    PublicId          uniqueidentifier NOT NULL,
    ContentId         bigint        NULL,
    FundingOwnerId    int           NULL,
    EvidenceType      varchar(40)   NOT NULL,   -- SCREENSHOT, EXPORT, TEST_RESULT, API_RESPONSE, OUTCOME, CONTRACT, OTHER
    FileName          nvarchar(260) NULL,
    ContentType       varchar(100)  NULL,
    SizeBytes         bigint        NULL,
    Sha256            binary(32)    NULL,
    Description       nvarchar(1000) NULL,
    SourceSystem      varchar(40)   NOT NULL,   -- UPLOAD, POWER_PLATFORM, GRAPH, GRAPH_BETA, ITSM
    UploadedByPersonId int          NOT NULL,
    UploadedUtc       datetime2     NOT NULL
)
WITH (LEDGER = ON (APPEND_ONLY = ON));

IF OBJECT_ID('evd.EvidenceLink') IS NULL
CREATE TABLE evd.EvidenceLink
(
    EvidenceId bigint      NOT NULL,
    LinkType   varchar(20) NOT NULL,   -- DECISION, REQUEST, ATTESTATION, VERIFICATION, WORKLOAD, EVENT, CAMPAIGN_ITEM
    LinkId     bigint      NOT NULL,
    LinkedUtc  datetime2   NOT NULL,
    LinkedByPersonId int   NOT NULL
)
WITH (LEDGER = ON (APPEND_ONLY = ON));

-- Operator attestation of Microsoft 365 settings without a production API (X6).
IF OBJECT_ID('evd.Attestation') IS NULL
CREATE TABLE evd.Attestation
(
    AttestationId      bigint IDENTITY NOT NULL,
    PublicId           uniqueidentifier NOT NULL,
    FundingOwnerId     int           NULL,
    WorkloadId         int           NULL,
    BindingId          int           NULL,
    TemplateId         int           NULL,
    RequestId          bigint        NULL,
    Subject            nvarchar(400) NOT NULL,
    AttestedJson       nvarchar(max) NOT NULL,
    MatchesTemplate    bit           NOT NULL,
    Differences        nvarchar(2000) NULL,
    AttestedByPersonId int           NOT NULL,
    AttestedUtc        datetime2     NOT NULL,
    Reason             varchar(20)   NOT NULL    -- CHANGE, MONTHLY
)
WITH (LEDGER = ON (APPEND_ONLY = ON));

-- D1 verification: read-back or attestation, explicit deny case, second verifier (SoD3).
IF OBJECT_ID('evd.Verification') IS NULL
CREATE TABLE evd.Verification
(
    VerificationId     bigint IDENTITY NOT NULL,
    PublicId           uniqueidentifier NOT NULL,
    RequestId          bigint        NOT NULL,
    FundingOwnerId     int           NULL,
    TemplateId         int           NULL,
    ConfiguredByPersonId int         NULL,      -- from the ITSM closure
    VerifierPersonId   int           NOT NULL,
    Method             varchar(20)   NOT NULL,  -- READBACK, ATTESTATION, MIXED
    Result             varchar(10)   NOT NULL,  -- PASS, FAIL
    ChecksJson         nvarchar(max) NOT NULL,  -- per check: code, expected, actual, pass
    ConfigurationVersion nvarchar(100) NULL,
    VerifiedUtc        datetime2     NOT NULL
)
WITH (LEDGER = ON (APPEND_ONLY = ON));

------------------------------------------------------------------------------------------------
-- ntf
------------------------------------------------------------------------------------------------
IF OBJECT_ID('ntf.Outbox') IS NULL
CREATE TABLE ntf.Outbox
(
    OutboxId        bigint IDENTITY CONSTRAINT PK_Outbox PRIMARY KEY,
    EventType       varchar(60)    NOT NULL,     -- N01..N22 or an integration command (ITSM.Create ...)
    Payload         nvarchar(max)  NOT NULL CONSTRAINT CK_Outbox_Payload CHECK (ISJSON(Payload) = 1),
    DedupeKey       nvarchar(200)  NOT NULL,
    CorrelationId   uniqueidentifier NOT NULL CONSTRAINT DF_Outbox_Correlation DEFAULT NEWID(),
    CreatedUtc      datetime2      NOT NULL CONSTRAINT DF_Outbox_Created DEFAULT SYSUTCDATETIME(),
    AvailableAtUtc  datetime2      NOT NULL CONSTRAINT DF_Outbox_Available DEFAULT SYSUTCDATETIME(),
    LockedBy        nvarchar(100)  NULL,
    LockedUntilUtc  datetime2      NULL,
    Attempts        int            NOT NULL CONSTRAINT DF_Outbox_Attempts DEFAULT 0,
    ProcessedAtUtc  datetime2      NULL,
    DeadLetteredUtc datetime2      NULL,
    LastError       nvarchar(1000) NULL
);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Outbox_Pending')
    CREATE INDEX IX_Outbox_Pending ON ntf.Outbox (AvailableAtUtc) INCLUDE (LockedUntilUtc) WHERE ProcessedAtUtc IS NULL AND DeadLetteredUtc IS NULL;
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_Outbox_Dedupe')
    CREATE UNIQUE INDEX UX_Outbox_Dedupe ON ntf.Outbox (DedupeKey);

IF OBJECT_ID('ntf.NotificationRule') IS NULL
CREATE TABLE ntf.NotificationRule
(
    Code        varchar(10)    NOT NULL CONSTRAINT PK_NotificationRule PRIMARY KEY,   -- N01..N22
    EventName   nvarchar(200)  NOT NULL,
    Recipients  nvarchar(400)  NOT NULL,
    Channels    varchar(100)   NOT NULL,   -- EMAIL, ACTIVITY, CHANNEL, ITSM
    Timing      nvarchar(400)  NOT NULL,
    IsAction    bit            NOT NULL,   -- action notices go out at once; information goes to the digest
    IsEnabled   bit            NOT NULL CONSTRAINT DF_NotificationRule_Enabled DEFAULT 1
);

IF OBJECT_ID('ntf.Delivery') IS NULL
CREATE TABLE ntf.Delivery
(
    DeliveryId        bigint IDENTITY CONSTRAINT PK_Delivery PRIMARY KEY,
    OutboxId          bigint         NULL,
    RecipientPersonId int            NULL,
    RecipientAddress  nvarchar(256)  NULL,
    Channel           varchar(20)    NOT NULL,
    DedupeKey         nvarchar(300)  NOT NULL CONSTRAINT UX_Delivery_Dedupe UNIQUE,   -- event, recipient, channel
    Subject           nvarchar(400)  NULL,
    Status            varchar(20)    NOT NULL,   -- SENT, FAILED, SUPPRESSED, SKIPPED
    Detail            nvarchar(1000) NULL,
    Utc               datetime2      NOT NULL CONSTRAINT DF_Delivery_Utc DEFAULT SYSUTCDATETIME()
);

IF OBJECT_ID('ntf.Preference') IS NULL
CREATE TABLE ntf.Preference
(
    PersonId     int         NOT NULL CONSTRAINT PK_Preference PRIMARY KEY,
    ActivityFeed bit         NOT NULL CONSTRAINT DF_Preference_Activity DEFAULT 1,
    QuietStart   time        NULL,
    QuietEnd     time        NULL,
    DigestHour   int         NOT NULL CONSTRAINT DF_Preference_DigestHour DEFAULT 7
);

IF OBJECT_ID('ntf.DigestEntry') IS NULL
CREATE TABLE ntf.DigestEntry
(
    DigestEntryId bigint IDENTITY CONSTRAINT PK_DigestEntry PRIMARY KEY,
    PersonId      int            NOT NULL,
    RuleCode      varchar(10)    NOT NULL,
    Text          nvarchar(1000) NOT NULL,
    Link          nvarchar(400)  NULL,
    CreatedUtc    datetime2      NOT NULL CONSTRAINT DF_DigestEntry_Created DEFAULT SYSUTCDATETIME(),
    SentUtc       datetime2      NULL
);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_DigestEntry_Unsent')
    CREATE INDEX IX_DigestEntry_Unsent ON ntf.DigestEntry (PersonId) WHERE SentUtc IS NULL;

------------------------------------------------------------------------------------------------
-- intg
------------------------------------------------------------------------------------------------
IF OBJECT_ID('intg.SyncRun') IS NULL
CREATE TABLE intg.SyncRun
(
    SyncRunId         bigint IDENTITY CONSTRAINT PK_SyncRun PRIMARY KEY,
    PublicId          uniqueidentifier NOT NULL CONSTRAINT DF_SyncRun_PublicId DEFAULT NEWSEQUENTIALID() CONSTRAINT UX_SyncRun_PublicId UNIQUE,
    Integration       varchar(10)    NOT NULL,     -- X1..X12
    Mode              varchar(20)    NOT NULL,     -- APP_ONLY, DELEGATED, UPLOAD, TEST_DOUBLE
    TriggeredByPersonId int          NULL,
    StartedUtc        datetime2      NOT NULL CONSTRAINT DF_SyncRun_Started DEFAULT SYSUTCDATETIME(),
    FinishedUtc       datetime2      NULL,
    Status            varchar(20)    NOT NULL,     -- RUNNING, SUCCEEDED, FAILED, PARTIAL
    ItemsRead         int            NOT NULL CONSTRAINT DF_SyncRun_Items DEFAULT 0,
    FindingsRaised    int            NOT NULL CONSTRAINT DF_SyncRun_Findings DEFAULT 0,
    Message           nvarchar(1000) NULL           -- incident reference or summary, never raw error text for users
);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_SyncRun_Integration')
    CREATE INDEX IX_SyncRun_Integration ON intg.SyncRun (Integration, StartedUtc DESC) INCLUDE (Status, FinishedUtc);

IF OBJECT_ID('intg.DeltaToken') IS NULL
CREATE TABLE intg.DeltaToken
(
    TokenKey   varchar(100)   NOT NULL CONSTRAINT PK_DeltaToken PRIMARY KEY,
    DeltaLink  nvarchar(max)  NOT NULL,
    UpdatedUtc datetime2      NOT NULL CONSTRAINT DF_DeltaToken_Updated DEFAULT SYSUTCDATETIME()
);

-- X4 inventory snapshot (environments, agents with harness and owner, agent flows).
IF OBJECT_ID('intg.InventoryItem') IS NULL
CREATE TABLE intg.InventoryItem
(
    InventoryItemId  bigint IDENTITY CONSTRAINT PK_InventoryItem PRIMARY KEY,
    SyncRunId        bigint         NOT NULL CONSTRAINT FK_InventoryItem_Run REFERENCES intg.SyncRun(SyncRunId),
    ResourceType     varchar(80)    NOT NULL,
    ResourceId       nvarchar(200)  NOT NULL,
    DisplayName      nvarchar(400)  NULL,
    EnvironmentId    nvarchar(200)  NULL,
    EnvironmentName  nvarchar(400)  NULL,
    EnvironmentType  nvarchar(60)   NULL,
    OwnerObjectId    nvarchar(100)  NULL,
    Harness          nvarchar(100)  NULL,
    CreatedAt        datetime2      NULL,
    RawJson          nvarchar(max)  NULL
);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_InventoryItem_Run')
    CREATE INDEX IX_InventoryItem_Run ON intg.InventoryItem (SyncRunId, ResourceType) INCLUDE (ResourceId);

-- X5 licensing read per governed environment.
IF OBJECT_ID('intg.AllocationReading') IS NULL
CREATE TABLE intg.AllocationReading
(
    AllocationReadingId bigint IDENTITY CONSTRAINT PK_AllocationReading PRIMARY KEY,
    SyncRunId          bigint         NOT NULL CONSTRAINT FK_AllocationReading_Run REFERENCES intg.SyncRun(SyncRunId),
    EnvironmentId      nvarchar(200)  NOT NULL,
    AllocatedCredits   decimal(18,2)  NULL,
    TenantPoolEnabled  bit            NULL,
    BillingPolicyId    nvarchar(200)  NULL,
    PaygLinked         bit            NULL,
    ConsumedCredits    decimal(18,2)  NULL,
    TenantPoolLockedByGroupRule bit   NULL,
    RawJson            nvarchar(max)  NULL
);

-- X2 access package assignments for governed packages (upserted, history in the sync run).
IF OBJECT_ID('intg.AccessPackageMap') IS NULL
CREATE TABLE intg.AccessPackageMap
(
    AccessPackageId uniqueidentifier NOT NULL CONSTRAINT PK_AccessPackageMap PRIMARY KEY,
    DisplayName     nvarchar(256)    NOT NULL,
    FundingOwnerId  int              NOT NULL,
    ServiceId       int              NOT NULL,
    Tier            nvarchar(50)     NOT NULL,
    PolicyGroupId   uniqueidentifier NULL,
    CatalogId       uniqueidentifier NULL
);

IF OBJECT_ID('intg.EntitlementAssignment') IS NULL
CREATE TABLE intg.EntitlementAssignment
(
    AssignmentId     uniqueidentifier NOT NULL CONSTRAINT PK_EntitlementAssignment PRIMARY KEY,
    AccessPackageId  uniqueidentifier NOT NULL,
    TargetObjectId   uniqueidentifier NULL,
    TargetPersonId   int              NULL,
    State            varchar(30)      NOT NULL,
    ExpiresUtc       datetime2        NULL,
    CreatedUtc       datetime2        NULL,
    RequestId        uniqueidentifier NULL,
    ApproverObjectId uniqueidentifier NULL,
    Justification    nvarchar(1000)   NULL,
    DecisionId       bigint           NULL,     -- decision row recorded with Source = ENTRA
    LastSeenRunId    bigint           NOT NULL
);

IF OBJECT_ID('intg.GroupMember') IS NULL
CREATE TABLE intg.GroupMember
(
    GroupId       uniqueidentifier NOT NULL,
    MemberObjectId uniqueidentifier NOT NULL,
    LastSeenRunId bigint           NOT NULL,
    CONSTRAINT PK_GroupMember PRIMARY KEY (GroupId, MemberObjectId)
);

IF OBJECT_ID('intg.AccessReviewDecision') IS NULL
CREATE TABLE intg.AccessReviewDecision
(
    DecisionKey       nvarchar(200)    NOT NULL CONSTRAINT PK_AccessReviewDecision PRIMARY KEY,
    ReviewDefinitionId nvarchar(100)   NOT NULL,
    InstanceId        nvarchar(100)    NOT NULL,
    PrincipalObjectId uniqueidentifier NULL,
    Decision          varchar(30)      NOT NULL,
    ReviewedByObjectId uniqueidentifier NULL,
    ReviewedUtc       datetime2        NULL,
    Justification     nvarchar(1000)   NULL,
    CampaignId        int              NULL,
    SyncRunId         bigint           NOT NULL
);

-- X7 Agent Registry export rows.
IF OBJECT_ID('intg.RegistryRow') IS NULL
CREATE TABLE intg.RegistryRow
(
    RegistryRowId bigint IDENTITY CONSTRAINT PK_RegistryRow PRIMARY KEY,
    SyncRunId     bigint         NOT NULL CONSTRAINT FK_RegistryRow_Run REFERENCES intg.SyncRun(SyncRunId),
    AgentId       nvarchar(200)  NOT NULL,
    Name          nvarchar(400)  NULL,
    Platform      nvarchar(100)  NULL,
    OwnerUpn      nvarchar(256)  NULL,
    Status        nvarchar(60)   NULL,
    Audience      nvarchar(400)  NULL,
    Matched       bit            NOT NULL CONSTRAINT DF_RegistryRow_Matched DEFAULT 0
);

-- Last successful run of every worker job, for the job dashboard and stale alerts.
IF OBJECT_ID('intg.JobRun') IS NULL
CREATE TABLE intg.JobRun
(
    JobRunId     bigint IDENTITY CONSTRAINT PK_JobRun PRIMARY KEY,
    JobName      varchar(60)    NOT NULL,
    Node         nvarchar(100)  NOT NULL,
    StartedUtc   datetime2      NOT NULL,
    FinishedUtc  datetime2      NULL,
    Status       varchar(20)    NOT NULL,
    Message      nvarchar(1000) NULL
);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_JobRun_Job')
    CREATE INDEX IX_JobRun_Job ON intg.JobRun (JobName, StartedUtc DESC) INCLUDE (Status);

------------------------------------------------------------------------------------------------
-- ops
------------------------------------------------------------------------------------------------
IF OBJECT_ID('ops.Finding') IS NULL
CREATE TABLE ops.Finding
(
    FindingId      bigint IDENTITY CONSTRAINT PK_Finding PRIMARY KEY,
    PublicId       uniqueidentifier NOT NULL CONSTRAINT DF_Finding_PublicId DEFAULT NEWSEQUENTIALID() CONSTRAINT UX_Finding_PublicId UNIQUE,
    FindingType    varchar(40)    NOT NULL,
    Severity       varchar(10)    NOT NULL CONSTRAINT CK_Finding_Severity CHECK (Severity IN ('LOW','MEDIUM','HIGH','CRITICAL')),
    FundingOwnerId int            NULL,
    WorkloadId     int            NULL,
    ExternalId     nvarchar(200)  NULL,
    Title          nvarchar(300)  NOT NULL,
    Details        nvarchar(2000) NULL,
    Expected       nvarchar(400)  NULL,
    Actual         nvarchar(400)  NULL,
    Status         varchar(20)    NOT NULL CONSTRAINT CK_Finding_Status CHECK (Status IN ('OPEN','ACKNOWLEDGED','RESOLVED','ACCEPTED')),
    DedupeKey      nvarchar(300)  NOT NULL,
    SyncRunId      bigint         NULL,
    RaisedUtc      datetime2      NOT NULL CONSTRAINT DF_Finding_Raised DEFAULT SYSUTCDATETIME(),
    LastSeenUtc    datetime2      NOT NULL CONSTRAINT DF_Finding_Seen DEFAULT SYSUTCDATETIME(),
    DueUtc         datetime2      NULL,
    ResolvedUtc    datetime2      NULL,
    ResolvedByPersonId int        NULL,
    Resolution     nvarchar(1000) NULL,
    RowVer         rowversion
);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_Finding_OpenDedupe')
    CREATE UNIQUE INDEX UX_Finding_OpenDedupe ON ops.Finding (DedupeKey) WHERE Status IN ('OPEN','ACKNOWLEDGED');

IF OBJECT_ID('ops.ConsumptionEvent') IS NULL
CREATE TABLE ops.ConsumptionEvent
(
    EventId        int IDENTITY CONSTRAINT PK_ConsumptionEvent PRIMARY KEY,
    PublicId       uniqueidentifier NOT NULL CONSTRAINT DF_ConsumptionEvent_PublicId DEFAULT NEWSEQUENTIALID() CONSTRAINT UX_ConsumptionEvent_PublicId UNIQUE,
    FundingOwnerId int            NOT NULL,
    WorkloadId     int            NULL,
    EventClass     varchar(30)    NOT NULL CONSTRAINT CK_ConsumptionEvent_Class CHECK (EventClass IN ('THRESHOLD','DENIAL','EXHAUSTION','RUNAWAY','NO_PERMITTED_SOURCE','OTHER')),
    Severity       varchar(10)    NOT NULL CONSTRAINT CK_ConsumptionEvent_Severity CHECK (Severity IN ('NORMAL','HIGH','CRITICAL')),
    Title          nvarchar(300)  NOT NULL,
    Description    nvarchar(2000) NOT NULL,
    ReportedByPersonId int        NOT NULL,
    ReportedUtc    datetime2      NOT NULL CONSTRAINT DF_ConsumptionEvent_Reported DEFAULT SYSUTCDATETIME(),
    AckDueUtc      datetime2      NOT NULL,
    AckUtc         datetime2      NULL,
    AckByPersonId  int            NULL,
    ResponseAction nvarchar(2000) NULL,
    RestoredUtc    datetime2      NULL,
    ReviewNotes    nvarchar(2000) NULL,
    CriticalServiceAffected bit   NOT NULL CONSTRAINT DF_ConsumptionEvent_Critical DEFAULT 0,
    Status         varchar(20)    NOT NULL CONSTRAINT CK_ConsumptionEvent_Status CHECK (Status IN ('OPEN','ACKNOWLEDGED','RESTORED','CLOSED')),
    RowVer         rowversion
);

------------------------------------------------------------------------------------------------
-- aud
------------------------------------------------------------------------------------------------
IF OBJECT_ID('aud.AuditLog') IS NULL
CREATE TABLE aud.AuditLog
(
    AuditId       bigint IDENTITY NOT NULL,
    Utc           datetime2     NOT NULL,
    PersonId      int           NULL,
    ActingAsPersonId int        NULL,        -- test persona switch, Development only
    Action        varchar(60)   NOT NULL,
    EntityType    varchar(40)   NULL,
    EntityId      nvarchar(100) NULL,
    Detail        nvarchar(2000) NULL,
    CorrelationId varchar(64)   NULL,
    Outcome       varchar(20)   NOT NULL      -- OK, DENIED, FAILED
)
WITH (LEDGER = ON (APPEND_ONLY = ON));
GO
