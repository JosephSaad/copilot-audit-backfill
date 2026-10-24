-- Workflow: definitions (operating model as data), requests, tasks, decisions (ledger), exceptions,
-- campaigns, ITSM tickets, command log for idempotency.
SET NOCOUNT ON;

IF OBJECT_ID('wf.SlaPolicy') IS NULL
CREATE TABLE wf.SlaPolicy
(
    SlaPolicyCode varchar(30)   NOT NULL CONSTRAINT PK_SlaPolicy PRIMARY KEY,
    Name          nvarchar(100) NOT NULL,
    Duration      int           NOT NULL,      -- 0 = no SLA
    Unit          varchar(10)   NOT NULL CONSTRAINT CK_SlaPolicy_Unit CHECK (Unit IN ('BD','H','D')),
    Calendar      varchar(20)   NOT NULL CONSTRAINT CK_SlaPolicy_Calendar CHECK (Calendar IN ('BUSINESS','CLOCK')),
    RemindAtPct1  int           NOT NULL CONSTRAINT DF_SlaPolicy_R1 DEFAULT 50,
    RemindAtPct2  int           NOT NULL CONSTRAINT DF_SlaPolicy_R2 DEFAULT 80
);

-- Escalation ladder per stage kind: level 1 is the first escalation at 100 percent of the SLA.
IF OBJECT_ID('wf.EscalationStep') IS NULL
CREATE TABLE wf.EscalationStep
(
    LadderCode varchar(30) NOT NULL,
    Level      int         NOT NULL,
    RoleCode   varchar(40) NOT NULL,
    ScopeKind  varchar(30) NOT NULL CONSTRAINT CK_EscalationStep_Scope CHECK (ScopeKind IN ('REQUEST_FUNDING_OWNER','TENANT')),
    CONSTRAINT PK_EscalationStep PRIMARY KEY (LadderCode, Level)
);

IF OBJECT_ID('wf.WorkflowDefinition') IS NULL
CREATE TABLE wf.WorkflowDefinition
(
    DefinitionId  int IDENTITY CONSTRAINT PK_WorkflowDefinition PRIMARY KEY,
    JourneyCode   varchar(10)   NOT NULL,      -- J1 to J8
    VersionNo     int           NOT NULL,
    Name          nvarchar(200) NOT NULL,
    Description   nvarchar(1000) NOT NULL,
    TypicalUser   nvarchar(200) NOT NULL,
    Turnaround    nvarchar(100) NOT NULL,
    IsActive      bit           NOT NULL,
    CONSTRAINT UX_WorkflowDefinition UNIQUE (JourneyCode, VersionNo)
);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_WorkflowDefinition_OneActive')
    CREATE UNIQUE INDEX UX_WorkflowDefinition_OneActive ON wf.WorkflowDefinition (JourneyCode) WHERE IsActive = 1;

IF OBJECT_ID('wf.StageDefinition') IS NULL
CREATE TABLE wf.StageDefinition
(
    DefinitionId      int           NOT NULL CONSTRAINT FK_StageDefinition_Definition REFERENCES wf.WorkflowDefinition(DefinitionId),
    StageCode         varchar(20)   NOT NULL,   -- B2, B5, D1 ...
    Name              nvarchar(200) NOT NULL,
    RaciActivityCode  varchar(60)   NULL,
    TaskKind          varchar(20)   NOT NULL CONSTRAINT CK_StageDefinition_Kind CHECK (TaskKind IN ('DECISION','WORK','VERIFICATION','SYSTEM','NONE')),
    TaskRoleCode      varchar(40)   NULL,       -- who gets the task; NULL = resolved from RACI A (decision) or R (work)
    DecisionType      varchar(40)   NULL,
    SlaPolicyCode     varchar(30)   NULL CONSTRAINT FK_StageDefinition_Sla REFERENCES wf.SlaPolicy(SlaPolicyCode),
    LadderCode        varchar(30)   NULL,
    AllowsDelegation  bit           NOT NULL CONSTRAINT DF_StageDefinition_Deleg DEFAULT 0,
    RequiredEvidence  nvarchar(400) NULL,       -- comma list of evidence types
    EntryRule         varchar(60)   NULL,
    ExitRule          varchar(60)   NULL,
    NotifyCodes       varchar(100)  NULL,
    Guidance          nvarchar(1000) NULL,
    SortOrder         int           NOT NULL,
    CONSTRAINT PK_StageDefinition PRIMARY KEY (DefinitionId, StageCode)
);

IF OBJECT_ID('wf.TransitionDefinition') IS NULL
CREATE TABLE wf.TransitionDefinition
(
    DefinitionId int         NOT NULL,
    FromStage    varchar(20) NOT NULL,
    Outcome      varchar(30) NOT NULL,
    ToStage      varchar(20) NOT NULL,
    ToState      varchar(30) NOT NULL,
    GuardRule    varchar(60) NULL,
    CONSTRAINT PK_TransitionDefinition PRIMARY KEY (DefinitionId, FromStage, Outcome),
    CONSTRAINT FK_TransitionDefinition_Stage FOREIGN KEY (DefinitionId, FromStage) REFERENCES wf.StageDefinition(DefinitionId, StageCode)
);

IF OBJECT_ID('wf.ConsultDefinition') IS NULL
CREATE TABLE wf.ConsultDefinition
(
    DefinitionId int         NOT NULL,
    StageCode    varchar(20) NOT NULL,
    FunctionCode varchar(30) NOT NULL,
    IsBlocking   bit         NOT NULL,
    CONSTRAINT PK_ConsultDefinition PRIMARY KEY (DefinitionId, StageCode, FunctionCode)
);

IF OBJECT_ID('wf.RequestNumber') IS NULL
CREATE SEQUENCE wf.RequestNumber AS bigint START WITH 1 INCREMENT BY 1;

IF OBJECT_ID('wf.Request') IS NULL
CREATE TABLE wf.Request
(
    RequestId         bigint IDENTITY CONSTRAINT PK_Request PRIMARY KEY,
    PublicId          uniqueidentifier NOT NULL CONSTRAINT DF_Request_PublicId DEFAULT NEWSEQUENTIALID() CONSTRAINT UX_Request_PublicId UNIQUE,
    RequestNumber     varchar(20)   NOT NULL CONSTRAINT UX_Request_Number UNIQUE,   -- CCG-2026-000123
    JourneyCode       varchar(10)   NOT NULL,
    DefinitionId      int           NOT NULL CONSTRAINT FK_Request_Definition REFERENCES wf.WorkflowDefinition(DefinitionId),
    State             varchar(30)   NOT NULL CONSTRAINT CK_Request_State CHECK (State IN
                        ('DRAFT','SUBMITTED','RETURNED','CLASSIFICATION','PATTERN_CHECK','ROUTINE_APPROVAL','SPECIALIST_REVIEW',
                         'APPROVED','APPROVED_WITH_CONDITIONS','CONSTRAINED_PILOT','ON_HOLD','DECLINED','FULFILLMENT','VERIFICATION',
                         'ACCEPTANCE','COMPLETED','CANCELLED','CLOSED')),
    CurrentStage      varchar(20)   NULL,
    Title             nvarchar(200) NOT NULL,
    Summary           nvarchar(2000) NULL,
    WorkloadId        int           NULL,
    FundingOwnerId    int           NOT NULL,
    ServiceId         int           NULL,
    RequesterPersonId int           NOT NULL CONSTRAINT FK_Request_Requester REFERENCES sec.Person(PersonId),
    SponsorPersonId   int           NULL CONSTRAINT FK_Request_Sponsor REFERENCES sec.Person(PersonId),
    PayloadJson       nvarchar(max) NOT NULL CONSTRAINT CK_Request_Payload CHECK (ISJSON(PayloadJson) = 1),
    EstimateCredits   decimal(18,2) NULL,
    EnvelopeId        int           NULL,
    PatternId         int           NULL,
    PatternResult     nvarchar(2000) NULL,
    TriggeredReviews  varchar(200)  NULL,
    CreatedUtc        datetime2     NOT NULL CONSTRAINT DF_Request_Created DEFAULT SYSUTCDATETIME(),
    SubmittedUtc      datetime2     NULL,
    ClosedUtc         datetime2     NULL,
    LastActivityUtc   datetime2     NOT NULL CONSTRAINT DF_Request_Activity DEFAULT SYSUTCDATETIME(),
    RowVer            rowversion
);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Request_FundingOwner')
    CREATE INDEX IX_Request_FundingOwner ON wf.Request (FundingOwnerId, State) INCLUDE (JourneyCode, CurrentStage);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Request_Requester')
    CREATE INDEX IX_Request_Requester ON wf.Request (RequesterPersonId) INCLUDE (State);

-- Every state change, append only.
IF OBJECT_ID('wf.RequestEvent') IS NULL
CREATE TABLE wf.RequestEvent
(
    RequestEventId bigint IDENTITY CONSTRAINT PK_RequestEvent PRIMARY KEY,
    RequestId      bigint        NOT NULL CONSTRAINT FK_RequestEvent_Request REFERENCES wf.Request(RequestId),
    FundingOwnerId int           NOT NULL,
    Utc            datetime2     NOT NULL CONSTRAINT DF_RequestEvent_Utc DEFAULT SYSUTCDATETIME(),
    PersonId       int           NULL,
    EventType      varchar(40)   NOT NULL,
    FromState      varchar(30)   NULL,
    ToState        varchar(30)   NULL,
    StageCode      varchar(20)   NULL,
    Text           nvarchar(2000) NULL
);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_RequestEvent_Request')
    CREATE INDEX IX_RequestEvent_Request ON wf.RequestEvent (RequestId, Utc);

IF OBJECT_ID('wf.Task') IS NULL
CREATE TABLE wf.Task
(
    TaskId            bigint IDENTITY CONSTRAINT PK_Task PRIMARY KEY,
    PublicId          uniqueidentifier NOT NULL CONSTRAINT DF_Task_PublicId DEFAULT NEWSEQUENTIALID() CONSTRAINT UX_Task_PublicId UNIQUE,
    RequestId         bigint        NULL CONSTRAINT FK_Task_Request REFERENCES wf.Request(RequestId),
    FundingOwnerId    int           NULL,
    StageCode         varchar(20)   NULL,
    TaskType          varchar(20)   NOT NULL CONSTRAINT CK_Task_Type CHECK (TaskType IN
                        ('DECISION','WORK','CONSULT','VERIFICATION','ATTESTATION','RECERTIFICATION','FINDING','EVENT','CLAIM','ACCEPT_ROLE','ACTIVATION')),
    Title             nvarchar(300) NOT NULL,
    AssignedRoleCode  varchar(40)   NULL,
    ScopeType         varchar(20)   NULL,
    ScopeId           int           NULL,
    AssignedPersonId  int           NULL CONSTRAINT FK_Task_Assignee REFERENCES sec.Person(PersonId),  -- direct assignment or claim
    ClaimedUtc        datetime2     NULL,
    Status            varchar(20)   NOT NULL CONSTRAINT CK_Task_Status CHECK (Status IN ('OPEN','DONE','CANCELLED')),
    IsBlocking        bit           NOT NULL CONSTRAINT DF_Task_Blocking DEFAULT 1,
    SlaPolicyCode     varchar(30)   NULL,
    LadderCode        varchar(30)   NULL,
    StartedUtc        datetime2     NOT NULL CONSTRAINT DF_Task_Started DEFAULT SYSUTCDATETIME(),
    DueUtc            datetime2     NULL,
    Reminder1Utc      datetime2     NULL,
    Reminder2Utc      datetime2     NULL,
    Reminded1         bit           NOT NULL CONSTRAINT DF_Task_R1 DEFAULT 0,
    Reminded2         bit           NOT NULL CONSTRAINT DF_Task_R2 DEFAULT 0,
    EscalationLevel   int           NOT NULL CONSTRAINT DF_Task_Esc DEFAULT 0,
    RelatedEntityType varchar(30)   NULL,       -- WORKLOAD, FINDING, CAMPAIGN_ITEM, EVENT, FUNDING_OWNER, ROLE_ASSIGNMENT, EXCEPTION
    RelatedEntityId   bigint        NULL,
    Outcome           varchar(30)   NULL,
    CompletedByPersonId int         NULL,
    CompletedUtc      datetime2     NULL,
    Notes             nvarchar(2000) NULL,
    RowVer            rowversion
);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Task_Open')
    CREATE INDEX IX_Task_Open ON wf.Task (Status, AssignedRoleCode, ScopeType, ScopeId) INCLUDE (DueUtc, AssignedPersonId, RequestId) WHERE Status = 'OPEN';
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Task_Request')
    CREATE INDEX IX_Task_Request ON wf.Task (RequestId, Status);

-- People removed from a task by segregation of duties, with the rule that excluded them.
IF OBJECT_ID('wf.TaskExclusion') IS NULL
CREATE TABLE wf.TaskExclusion
(
    TaskId   bigint      NOT NULL CONSTRAINT FK_TaskExclusion_Task REFERENCES wf.Task(TaskId),
    PersonId int         NOT NULL,
    RuleCode varchar(10) NOT NULL,
    CONSTRAINT PK_TaskExclusion PRIMARY KEY (TaskId, PersonId)
);

-- Every decision, including those imported from Entra, as an immutable ledger row (section 7.5).
IF OBJECT_ID('wf.Decision') IS NULL
CREATE TABLE wf.Decision
(
    DecisionId           bigint IDENTITY NOT NULL,
    PublicId             uniqueidentifier NOT NULL,
    RequestId            bigint        NULL,
    WorkloadId           int           NULL,
    FundingOwnerId       int           NULL,
    DecisionType         varchar(40)   NOT NULL,
    Outcome              varchar(30)   NOT NULL,
    DeciderPersonId      int           NOT NULL,
    DeciderRoleCode      varchar(40)   NOT NULL,
    DelegationId         int           NULL,
    OnBehalfOfPersonId   int           NULL,      -- out-of-office: the original holder
    RaciVersionId        int           NOT NULL,
    RaciActivityCode     varchar(60)   NOT NULL,
    StageCode            varchar(20)   NULL,
    ScopeText            nvarchar(1000) NOT NULL,
    ConditionsText       nvarchar(2000) NULL,
    ReasonText           nvarchar(2000) NULL,
    RequiredEvidenceText nvarchar(2000) NULL,     -- for decline and hold (B6)
    ExpiresOn            date          NULL,
    NextReviewOn         date          NULL,
    Source               varchar(30)   NOT NULL,  -- WORKBENCH, ENTRA, MIGRATED
    SourceRef            nvarchar(200) NULL,
    SupersedesDecisionId bigint        NULL,
    DecidedAtUtc         datetime2     NOT NULL
)
WITH (LEDGER = ON (APPEND_ONLY = ON));

IF OBJECT_ID('wf.DecisionCondition') IS NULL
CREATE TABLE wf.DecisionCondition
(
    DecisionId    bigint        NOT NULL,
    ConditionNo   int           NOT NULL,
    ConditionText nvarchar(1000) NOT NULL,
    OwnerPersonId int           NULL,
    DueOn         date          NULL
)
WITH (LEDGER = ON (APPEND_ONLY = ON));

-- Exception register (B7, B9, G6): compensating controls, owner and expiry.
IF OBJECT_ID('wf.ExceptionItem') IS NULL
CREATE TABLE wf.ExceptionItem
(
    ExceptionId          int IDENTITY CONSTRAINT PK_ExceptionItem PRIMARY KEY,
    PublicId             uniqueidentifier NOT NULL CONSTRAINT DF_ExceptionItem_PublicId DEFAULT NEWSEQUENTIALID() CONSTRAINT UX_ExceptionItem_PublicId UNIQUE,
    FundingOwnerId       int           NOT NULL,
    WorkloadId           int           NULL,
    RequestId            bigint        NULL,
    DecisionId           bigint        NULL,
    Title                nvarchar(200) NOT NULL,
    Description          nvarchar(2000) NOT NULL,
    CompensatingControls nvarchar(2000) NOT NULL,
    OwnerPersonId        int           NOT NULL CONSTRAINT FK_ExceptionItem_Owner REFERENCES sec.Person(PersonId),
    ExpiresOn            date          NOT NULL,
    NextReviewOn         date          NULL,
    Status               varchar(20)   NOT NULL CONSTRAINT CK_ExceptionItem_Status CHECK (Status IN ('ACTIVE','EXPIRED','CLOSED')),
    Notified30           bit NOT NULL CONSTRAINT DF_ExceptionItem_N30 DEFAULT 0,
    Notified7            bit NOT NULL CONSTRAINT DF_ExceptionItem_N7 DEFAULT 0,
    Notified1            bit NOT NULL CONSTRAINT DF_ExceptionItem_N1 DEFAULT 0,
    CreatedUtc           datetime2     NOT NULL CONSTRAINT DF_ExceptionItem_Created DEFAULT SYSUTCDATETIME(),
    ClosedUtc            datetime2     NULL,
    RowVer               rowversion
);

IF OBJECT_ID('wf.Comment') IS NULL
CREATE TABLE wf.Comment
(
    CommentId      bigint IDENTITY CONSTRAINT PK_Comment PRIMARY KEY,
    RequestId      bigint        NOT NULL CONSTRAINT FK_Comment_Request REFERENCES wf.Request(RequestId),
    FundingOwnerId int           NOT NULL,
    PersonId       int           NOT NULL,
    Body           nvarchar(2000) NOT NULL,
    Utc            datetime2     NOT NULL CONSTRAINT DF_Comment_Utc DEFAULT SYSUTCDATETIME()
);

-- Idempotency: one row per form post key; a repeat returns the first result.
IF OBJECT_ID('wf.CommandLog') IS NULL
CREATE TABLE wf.CommandLog
(
    IdempotencyKey uniqueidentifier NOT NULL CONSTRAINT PK_CommandLog PRIMARY KEY,
    PersonId       int           NOT NULL,
    Command        varchar(80)   NOT NULL,
    ResultRef      nvarchar(200) NULL,
    Utc            datetime2     NOT NULL CONSTRAINT DF_CommandLog_Utc DEFAULT SYSUTCDATETIME()
);

IF OBJECT_ID('wf.ItsmTicket') IS NULL
CREATE TABLE wf.ItsmTicket
(
    ItsmTicketId    bigint IDENTITY CONSTRAINT PK_ItsmTicket PRIMARY KEY,
    FundingOwnerId  int           NULL,
    RequestId       bigint        NULL,
    TaskId          bigint        NULL,
    EventId         int           NULL,
    Kind            varchar(20)   NOT NULL CONSTRAINT CK_ItsmTicket_Kind CHECK (Kind IN ('TASK','INCIDENT')),
    CorrelationId   uniqueidentifier NOT NULL CONSTRAINT UX_ItsmTicket_Correlation UNIQUE,
    ExternalNumber  nvarchar(60)  NULL,
    ExternalUrl     nvarchar(400) NULL,
    State           varchar(30)   NOT NULL,   -- PENDING_CREATE, NEW, IN_PROGRESS, RESOLVED, CLOSED, CANCELLED
    Payload         nvarchar(max) NOT NULL,
    ClosureNotes    nvarchar(2000) NULL,
    CreatedUtc      datetime2     NOT NULL CONSTRAINT DF_ItsmTicket_Created DEFAULT SYSUTCDATETIME(),
    LastPolledUtc   datetime2     NULL,
    ClosedUtc       datetime2     NULL,
    RowVer          rowversion
);

-- Recertification (I1, J6), re-attestation and other recurring campaigns.
IF OBJECT_ID('wf.Campaign') IS NULL
CREATE TABLE wf.Campaign
(
    CampaignId   int IDENTITY CONSTRAINT PK_Campaign PRIMARY KEY,
    PublicId     uniqueidentifier NOT NULL CONSTRAINT DF_Campaign_PublicId DEFAULT NEWSEQUENTIALID(),
    CampaignType varchar(20)   NOT NULL CONSTRAINT CK_Campaign_Type CHECK (CampaignType IN ('RECERTIFICATION','REATTESTATION','ROLE_REVIEW')),
    Name         nvarchar(200) NOT NULL,
    StartsOn     date          NOT NULL,
    DueOn        date          NOT NULL,
    Status       varchar(20)   NOT NULL CONSTRAINT CK_Campaign_Status CHECK (Status IN ('OPEN','CLOSED')),
    AccessReviewDefinitionId nvarchar(100) NULL,
    CreatedByPersonId int      NULL,
    CreatedUtc   datetime2     NOT NULL CONSTRAINT DF_Campaign_Created DEFAULT SYSUTCDATETIME()
);

IF OBJECT_ID('wf.CampaignItem') IS NULL
CREATE TABLE wf.CampaignItem
(
    CampaignItemId int IDENTITY CONSTRAINT PK_CampaignItem PRIMARY KEY,
    CampaignId     int           NOT NULL CONSTRAINT FK_CampaignItem_Campaign REFERENCES wf.Campaign(CampaignId),
    FundingOwnerId int           NOT NULL,
    WorkloadId     int           NULL,
    OwnerPersonId  int           NULL,
    Status         varchar(20)   NOT NULL CONSTRAINT CK_CampaignItem_Status CHECK (Status IN ('OPEN','DONE','OVERDUE')),
    Outcome        varchar(20)   NULL CONSTRAINT CK_CampaignItem_Outcome CHECK (Outcome IN ('RENEW','RESIZE','RECLASSIFY','RETIRE')),
    OutcomeEvidence nvarchar(2000) NULL,
    DecisionId     bigint        NULL,
    TaskId         bigint        NULL,
    CompletedUtc   datetime2     NULL
);
GO
