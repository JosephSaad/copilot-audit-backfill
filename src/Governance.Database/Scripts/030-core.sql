-- Registry: funding owners, cost centers, services, control templates, patterns, workloads, bindings,
-- envelopes, commercial inventory, environment classes and approved capacity posture.
SET NOCOUNT ON;

IF OBJECT_ID('core.FundingOwner') IS NULL
CREATE TABLE core.FundingOwner
(
    FundingOwnerId        int IDENTITY CONSTRAINT PK_FundingOwner PRIMARY KEY,
    PublicId              uniqueidentifier NOT NULL CONSTRAINT DF_FundingOwner_PublicId DEFAULT NEWSEQUENTIALID() CONSTRAINT UX_FundingOwner_PublicId UNIQUE,
    Code                  varchar(32)   NOT NULL CONSTRAINT UX_FundingOwner_Code UNIQUE,
    Name                  nvarchar(200) NOT NULL,
    OwnerType             varchar(20)   NOT NULL CONSTRAINT CK_FundingOwner_Type CHECK (OwnerType IN ('LOB','ENTERPRISE_SHARED')),
    ParentFundingOwnerId  int           NULL CONSTRAINT FK_FundingOwner_Parent REFERENCES core.FundingOwner(FundingOwnerId),
    BusinessOwnerPersonId int           NULL CONSTRAINT FK_FundingOwner_Owner REFERENCES sec.Person(PersonId),
    FinancePartnerPersonId int          NULL CONSTRAINT FK_FundingOwner_Finance REFERENCES sec.Person(PersonId),
    FinanceNode           nvarchar(100) NULL,
    Status                varchar(20)   NOT NULL CONSTRAINT CK_FundingOwner_Status CHECK (Status IN ('DRAFT','ACTIVE','SUSPENDED','RETIRED')),
    IsCriticalShared      bit           NOT NULL CONSTRAINT DF_FundingOwner_Critical DEFAULT 0,
    NotificationRecipientsConfirmed bit NOT NULL CONSTRAINT DF_FundingOwner_Recipients DEFAULT 0,
    AttributionTested     bit           NOT NULL CONSTRAINT DF_FundingOwner_AttrTested DEFAULT 0,
    EffectiveFrom         date          NOT NULL,
    EffectiveTo           date          NULL,
    RowVer                rowversion,
    SysStart              datetime2 GENERATED ALWAYS AS ROW START HIDDEN NOT NULL,
    SysEnd                datetime2 GENERATED ALWAYS AS ROW END   HIDDEN NOT NULL,
    PERIOD FOR SYSTEM_TIME (SysStart, SysEnd)
)
WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE = core.FundingOwnerHistory));

IF OBJECT_ID('core.CostCenter') IS NULL
CREATE TABLE core.CostCenter
(
    CostCenterId     int IDENTITY CONSTRAINT PK_CostCenter PRIMARY KEY,
    PublicId         uniqueidentifier NOT NULL CONSTRAINT DF_CostCenter_PublicId DEFAULT NEWSEQUENTIALID(),
    Code             nvarchar(100) NOT NULL CONSTRAINT UX_CostCenter_Code UNIQUE,
    Name             nvarchar(200) NOT NULL,
    FinanceNode      nvarchar(100) NULL,
    FundingOwnerId   int           NOT NULL CONSTRAINT FK_CostCenter_FundingOwner REFERENCES core.FundingOwner(FundingOwnerId),
    ValidatedByFinance bit         NOT NULL CONSTRAINT DF_CostCenter_Validated DEFAULT 0,
    ValidatedByPersonId int        NULL,
    EffectiveFrom    date          NOT NULL,
    EffectiveTo      date          NULL,
    RowVer           rowversion,
    SysStart         datetime2 GENERATED ALWAYS AS ROW START HIDDEN NOT NULL,
    SysEnd           datetime2 GENERATED ALWAYS AS ROW END   HIDDEN NOT NULL,
    PERIOD FOR SYSTEM_TIME (SysStart, SysEnd)
)
WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE = core.CostCenterHistory));

-- Entra division and cost-center values that map to a funding owner.
IF OBJECT_ID('core.AttributionRule') IS NULL
CREATE TABLE core.AttributionRule
(
    AttributionRuleId int IDENTITY CONSTRAINT PK_AttributionRule PRIMARY KEY,
    FundingOwnerId    int           NOT NULL CONSTRAINT FK_AttributionRule_FundingOwner REFERENCES core.FundingOwner(FundingOwnerId),
    MatchDivision     nvarchar(200) NULL,
    MatchCostCenter   nvarchar(100) NULL,
    MatchDepartment   nvarchar(200) NULL,
    Priority          int           NOT NULL CONSTRAINT DF_AttributionRule_Priority DEFAULT 100,
    EffectiveFrom     date          NOT NULL,
    EffectiveTo       date          NULL,
    SysStart          datetime2 GENERATED ALWAYS AS ROW START HIDDEN NOT NULL,
    SysEnd            datetime2 GENERATED ALWAYS AS ROW END   HIDDEN NOT NULL,
    PERIOD FOR SYSTEM_TIME (SysStart, SysEnd),
    CONSTRAINT CK_AttributionRule_Any CHECK (MatchDivision IS NOT NULL OR MatchCostCenter IS NOT NULL OR MatchDepartment IS NOT NULL)
)
WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE = core.AttributionRuleHistory));

IF OBJECT_ID('core.AttributionOverride') IS NULL
CREATE TABLE core.AttributionOverride
(
    AttributionOverrideId int IDENTITY CONSTRAINT PK_AttributionOverride PRIMARY KEY,
    PersonId       int           NOT NULL CONSTRAINT FK_AttributionOverride_Person REFERENCES sec.Person(PersonId),
    FundingOwnerId int           NOT NULL CONSTRAINT FK_AttributionOverride_FundingOwner REFERENCES core.FundingOwner(FundingOwnerId),
    CostCenterId   int           NULL CONSTRAINT FK_AttributionOverride_CostCenter REFERENCES core.CostCenter(CostCenterId),
    Reason         nvarchar(400) NOT NULL,
    EffectiveFrom  date          NOT NULL,
    EffectiveTo    date          NULL,
    CreatedByPersonId int        NOT NULL,
    SysStart       datetime2 GENERATED ALWAYS AS ROW START HIDDEN NOT NULL,
    SysEnd         datetime2 GENERATED ALWAYS AS ROW END   HIDDEN NOT NULL,
    PERIOD FOR SYSTEM_TIME (SysStart, SysEnd)
)
WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE = core.AttributionOverrideHistory));

-- Approved service catalogue (A1). Tenant availability and institutional status are separate (C01).
IF OBJECT_ID('core.Service') IS NULL
CREATE TABLE core.Service
(
    ServiceId           int IDENTITY CONSTRAINT PK_Service PRIMARY KEY,
    PublicId            uniqueidentifier NOT NULL CONSTRAINT DF_Service_PublicId DEFAULT NEWSEQUENTIALID() CONSTRAINT UX_Service_PublicId UNIQUE,
    Code                varchar(40)    NOT NULL CONSTRAINT UX_Service_Code UNIQUE,
    Name                nvarchar(200)  NOT NULL,
    Harness             nvarchar(100)  NULL,
    RouteType           varchar(20)    NOT NULL CONSTRAINT CK_Service_Route CHECK (RouteType IN ('PPAC_ENVIRONMENT','M365_POLICY','ENTRA_PACKAGE')),
    TenantAvailability  varchar(20)    NOT NULL CONSTRAINT CK_Service_Availability CHECK (TenantAvailability IN ('GA','PREVIEW','NOT_AVAILABLE')),
    InstitutionalStatus varchar(20)    NOT NULL CONSTRAINT CK_Service_Status CHECK (InstitutionalStatus IN ('APPROVED','PILOT','NOT_APPROVED','RETIRED')),
    IsCustomerFacing    bit            NOT NULL CONSTRAINT DF_Service_Customer DEFAULT 0,
    Audiences           nvarchar(400)  NOT NULL,
    DataClasses         nvarchar(400)  NOT NULL,   -- highest data class approved, comma list
    AccessTiers         nvarchar(200)  NOT NULL,
    OperationalOwnerPersonId int       NULL CONSTRAINT FK_Service_OpsOwner REFERENCES sec.Person(PersonId),
    ReviewOn            date           NULL,
    Description         nvarchar(2000) NULL,
    RowVer              rowversion,
    SysStart            datetime2 GENERATED ALWAYS AS ROW START HIDDEN NOT NULL,
    SysEnd              datetime2 GENERATED ALWAYS AS ROW END   HIDDEN NOT NULL,
    PERIOD FOR SYSTEM_TIME (SysStart, SysEnd)
)
WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE = core.ServiceHistory));

-- Control templates per service route (A3), versioned, two approvers (SoD6).
IF OBJECT_ID('core.ControlTemplate') IS NULL
CREATE TABLE core.ControlTemplate
(
    TemplateId       int IDENTITY CONSTRAINT PK_ControlTemplate PRIMARY KEY,
    PublicId         uniqueidentifier NOT NULL CONSTRAINT DF_ControlTemplate_PublicId DEFAULT NEWSEQUENTIALID(),
    ServiceId        int            NOT NULL CONSTRAINT FK_ControlTemplate_Service REFERENCES core.Service(ServiceId),
    VersionNo        int            NOT NULL,
    Status           varchar(20)    NOT NULL CONSTRAINT CK_ControlTemplate_Status CHECK (Status IN ('DRAFT','PENDING','ACTIVE','RETIRED')),
    SettingsJson     nvarchar(max)  NOT NULL CONSTRAINT CK_ControlTemplate_Json CHECK (ISJSON(SettingsJson) = 1),
    PermittedExceptions nvarchar(1000) NULL,
    OperatorRoles    nvarchar(400)  NOT NULL,
    Notes            nvarchar(2000) NULL,
    CreatedByPersonId int           NULL,
    PlatformOwnerApprovalPersonId int NULL,
    SteeringApprovalPersonId int    NULL,
    ActivatedUtc     datetime2      NULL,
    RowVer           rowversion,
    CONSTRAINT UX_ControlTemplate_Version UNIQUE (ServiceId, VersionNo)
);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_ControlTemplate_OneActive')
    CREATE UNIQUE INDEX UX_ControlTemplate_OneActive ON core.ControlTemplate (ServiceId) WHERE Status = 'ACTIVE';

IF OBJECT_ID('core.ControlCheck') IS NULL
CREATE TABLE core.ControlCheck
(
    ControlCheckId int IDENTITY CONSTRAINT PK_ControlCheck PRIMARY KEY,
    TemplateId     int            NOT NULL CONSTRAINT FK_ControlCheck_Template REFERENCES core.ControlTemplate(TemplateId),
    CheckCode      varchar(40)    NOT NULL,
    Description    nvarchar(1000) NOT NULL,
    Method         varchar(20)    NOT NULL CONSTRAINT CK_ControlCheck_Method CHECK (Method IN ('READBACK','ATTESTATION','TEST_EVIDENCE')),
    SettingKey     varchar(100)   NULL,      -- key in SettingsJson compared by read-back
    IsDenyCase     bit            NOT NULL CONSTRAINT DF_ControlCheck_Deny DEFAULT 0,
    CaveatCode     varchar(10)    NULL,
    SortOrder      int            NOT NULL,
    CONSTRAINT UX_ControlCheck UNIQUE (TemplateId, CheckCode)
);

-- Approved patterns (B4): what a delegated approver may approve without specialist review.
IF OBJECT_ID('core.ApprovedPattern') IS NULL
CREATE TABLE core.ApprovedPattern
(
    PatternId        int IDENTITY CONSTRAINT PK_ApprovedPattern PRIMARY KEY,
    Code             varchar(40)   NOT NULL CONSTRAINT UX_ApprovedPattern_Code UNIQUE,
    Name             nvarchar(200) NOT NULL,
    ServiceId        int           NOT NULL CONSTRAINT FK_ApprovedPattern_Service REFERENCES core.Service(ServiceId),
    Audiences        nvarchar(200) NOT NULL,       -- comma list of allowed audiences
    MaxDataClass     varchar(20)   NOT NULL CONSTRAINT CK_ApprovedPattern_Data CHECK (MaxDataClass IN ('PUBLIC','INTERNAL','CONFIDENTIAL','RESTRICTED')),
    ActionScopes     nvarchar(200) NOT NULL,       -- READ_ONLY, INTERNAL_ACTIONS, EXTERNAL_ACTIONS
    LifecycleStages  nvarchar(100) NOT NULL,
    TriggerTypes     nvarchar(200) NOT NULL,
    Tiers            nvarchar(100) NOT NULL,
    MaxCreditsPerMonth decimal(18,2) NULL,
    IsActive         bit           NOT NULL CONSTRAINT DF_ApprovedPattern_Active DEFAULT 1,
    Description      nvarchar(1000) NULL
);

-- Environment classes carry the institutional default agent limit (G8, E6).
IF OBJECT_ID('core.EnvironmentClass') IS NULL
CREATE TABLE core.EnvironmentClass
(
    EnvironmentClassCode varchar(30)   NOT NULL CONSTRAINT PK_EnvironmentClass PRIMARY KEY,
    Name                 nvarchar(100) NOT NULL,
    Purpose              nvarchar(400) NOT NULL,
    DefaultAgentLimitCredits decimal(18,2) NOT NULL,
    NotificationThresholdPct int       NOT NULL,
    TenantPoolAllowed    bit           NOT NULL,
    PaygAllowed          bit           NOT NULL,
    DefaultAllocationCredits decimal(18,2) NOT NULL
);

IF OBJECT_ID('core.Workload') IS NULL
CREATE TABLE core.Workload
(
    WorkloadId        int IDENTITY CONSTRAINT PK_Workload PRIMARY KEY,
    PublicId          uniqueidentifier NOT NULL CONSTRAINT DF_Workload_PublicId DEFAULT NEWSEQUENTIALID() CONSTRAINT UX_Workload_PublicId UNIQUE,
    Name              nvarchar(200) NOT NULL,
    WorkloadType      varchar(30)   NOT NULL CONSTRAINT CK_Workload_Type CHECK (WorkloadType IN ('SERVICE_ENTITLEMENT','AGENT','ENVIRONMENT','AGENT_FLOW','OTHER')),
    FundingOwnerId    int           NOT NULL CONSTRAINT FK_Workload_FundingOwner REFERENCES core.FundingOwner(FundingOwnerId),
    CostCenterId      int           NULL CONSTRAINT FK_Workload_CostCenter REFERENCES core.CostCenter(CostCenterId),
    SolutionClass     varchar(30)   NULL CONSTRAINT CK_Workload_Class CHECK (SolutionClass IN ('PRODUCTIVITY_SOLUTION','BUSINESS_APPLICATION')),
    LifecycleStage    varchar(20)   NULL CONSTRAINT CK_Workload_Stage CHECK (LifecycleStage IN ('LOCAL','DEVELOPMENT','TEST','PRODUCTION')),
    ServiceId         int           NULL CONSTRAINT FK_Workload_Service REFERENCES core.Service(ServiceId),
    Criticality       varchar(20)   NULL CONSTRAINT CK_Workload_Criticality CHECK (Criticality IN ('LOW','MEDIUM','HIGH','CRITICAL')),
    TriggerType       varchar(30)   NULL CONSTRAINT CK_Workload_Trigger CHECK (TriggerType IN ('USER_INITIATED','SCHEDULED','AUTONOMOUS','CUSTOMER_TRIGGERED')),
    DataBoundary      nvarchar(400) NULL,
    DataClass         varchar(20)   NULL CONSTRAINT CK_Workload_DataClass CHECK (DataClass IN ('PUBLIC','INTERNAL','CONFIDENTIAL','RESTRICTED')),
    Audience          nvarchar(400) NULL,
    EnvironmentClassCode varchar(30) NULL CONSTRAINT FK_Workload_EnvClass REFERENCES core.EnvironmentClass(EnvironmentClassCode),
    ProductOwnerPersonId int        NULL CONSTRAINT FK_Workload_ProductOwner REFERENCES sec.Person(PersonId),
    SponsorPersonId   int           NULL CONSTRAINT FK_Workload_Sponsor REFERENCES sec.Person(PersonId),
    SupportContact    nvarchar(200) NULL,
    State             varchar(20)   NOT NULL CONSTRAINT CK_Workload_State CHECK (State IN ('DISCOVERED','REGISTERED','LOCAL','DEVELOPMENT','TEST','PRODUCTION','SUSPENDED','RETIRING','RETIRED')),
    ExpiresOn         date          NULL,
    NextReviewOn      date          NULL,
    ComplianceCaseRef nvarchar(200) NULL,       -- optional Copilot Agent Kit case (X12)
    RowVer            rowversion,
    SysStart          datetime2 GENERATED ALWAYS AS ROW START HIDDEN NOT NULL,
    SysEnd            datetime2 GENERATED ALWAYS AS ROW END   HIDDEN NOT NULL,
    PERIOD FOR SYSTEM_TIME (SysStart, SysEnd)
)
WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE = core.WorkloadHistory));
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Workload_FundingOwner')
    CREATE INDEX IX_Workload_FundingOwner ON core.Workload (FundingOwnerId, State);

-- One binding row per Microsoft object (C04): no universal policy object.
IF OBJECT_ID('core.WorkloadBinding') IS NULL
CREATE TABLE core.WorkloadBinding
(
    BindingId     int IDENTITY CONSTRAINT PK_WorkloadBinding PRIMARY KEY,
    WorkloadId    int            NOT NULL CONSTRAINT FK_WorkloadBinding_Workload REFERENCES core.Workload(WorkloadId),
    FundingOwnerId int           NOT NULL,   -- copied from the workload for row-level security
    ObjectType    varchar(30)    NOT NULL CONSTRAINT CK_WorkloadBinding_Type CHECK (ObjectType IN
                    ('PP_ENVIRONMENT','CS_AGENT','AGENT_FLOW','CHAT_CREDIT_POLICY','SPENDING_POLICY','ACCESS_PACKAGE','ENTRA_GROUP','REGISTRY_AGENT','ENVIRONMENT_GROUP','BILLING_POLICY')),
    ExternalId    nvarchar(200)  NOT NULL,
    DisplayName   nvarchar(400)  NULL,
    EffectiveFrom date           NOT NULL,
    EffectiveTo   date           NULL,
    RowVer        rowversion,
    SysStart      datetime2 GENERATED ALWAYS AS ROW START HIDDEN NOT NULL,
    SysEnd        datetime2 GENERATED ALWAYS AS ROW END   HIDDEN NOT NULL,
    PERIOD FOR SYSTEM_TIME (SysStart, SysEnd)
)
WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE = core.WorkloadBindingHistory));
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_WorkloadBinding_External')
    CREATE INDEX IX_WorkloadBinding_External ON core.WorkloadBinding (ObjectType, ExternalId) INCLUDE (WorkloadId, EffectiveTo);

-- Downstream meters per workload (C-DM).
IF OBJECT_ID('core.WorkloadMeter') IS NULL
CREATE TABLE core.WorkloadMeter
(
    WorkloadMeterId int IDENTITY CONSTRAINT PK_WorkloadMeter PRIMARY KEY,
    WorkloadId    int           NOT NULL CONSTRAINT FK_WorkloadMeter_Workload REFERENCES core.Workload(WorkloadId),
    MeterCode     varchar(40)   NOT NULL,   -- FOUNDRY, FABRIC, CONNECTOR, POWER_AUTOMATE, GITHUB_ACTIONS, THIRD_PARTY
    Description   nvarchar(400) NULL,
    CONSTRAINT UX_WorkloadMeter UNIQUE (WorkloadId, MeterCode)
);

-- Funding envelopes: an internal budget per funding owner, service and period (A4). Not a Microsoft setting.
IF OBJECT_ID('core.FundingEnvelope') IS NULL
CREATE TABLE core.FundingEnvelope
(
    EnvelopeId     int IDENTITY CONSTRAINT PK_FundingEnvelope PRIMARY KEY,
    PublicId       uniqueidentifier NOT NULL CONSTRAINT DF_FundingEnvelope_PublicId DEFAULT NEWSEQUENTIALID(),
    FundingOwnerId int           NOT NULL CONSTRAINT FK_FundingEnvelope_FundingOwner REFERENCES core.FundingOwner(FundingOwnerId),
    ServiceId      int           NOT NULL CONSTRAINT FK_FundingEnvelope_Service REFERENCES core.Service(ServiceId),
    PeriodStart    date          NOT NULL,
    PeriodEnd      date          NOT NULL,
    Amount         decimal(18,2) NOT NULL CONSTRAINT CK_FundingEnvelope_Amount CHECK (Amount >= 0),
    Unit           varchar(20)   NOT NULL CONSTRAINT CK_FundingEnvelope_Unit CHECK (Unit IN ('COPILOT_CREDIT','P3_COMMIT_UNIT','USD','CAD','EUR','GBP')),
    Status         varchar(20)   NOT NULL CONSTRAINT CK_FundingEnvelope_Status CHECK (Status IN ('DRAFT','ACTIVE','CLOSED')),
    ApprovedDecisionId bigint    NULL,
    RowVer         rowversion,
    SysStart       datetime2 GENERATED ALWAYS AS ROW START HIDDEN NOT NULL,
    SysEnd         datetime2 GENERATED ALWAYS AS ROW END   HIDDEN NOT NULL,
    PERIOD FOR SYSTEM_TIME (SysStart, SysEnd),
    CONSTRAINT CK_FundingEnvelope_Period CHECK (PeriodEnd >= PeriodStart)
)
WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE = core.FundingEnvelopeHistory));

-- Commitments against an envelope (approved requests). Headroom = Amount - sum(active commitments).
IF OBJECT_ID('core.EnvelopeCommitment') IS NULL
CREATE TABLE core.EnvelopeCommitment
(
    CommitmentId  int IDENTITY CONSTRAINT PK_EnvelopeCommitment PRIMARY KEY,
    EnvelopeId    int           NOT NULL CONSTRAINT FK_EnvelopeCommitment_Envelope REFERENCES core.FundingEnvelope(EnvelopeId),
    FundingOwnerId int          NOT NULL,
    RequestId     bigint        NULL,
    WorkloadId    int           NULL,
    Amount        decimal(18,2) NOT NULL,
    Unit          varchar(20)   NOT NULL,
    Status        varchar(20)   NOT NULL CONSTRAINT CK_EnvelopeCommitment_Status CHECK (Status IN ('ACTIVE','RELEASED')),
    CreatedUtc    datetime2     NOT NULL CONSTRAINT DF_EnvelopeCommitment_Created DEFAULT SYSUTCDATETIME()
);

-- Commercial inventory (A4): licences, packs, P3, PAYG scopes and who holds purchase authority.
IF OBJECT_ID('core.CommercialItem') IS NULL
CREATE TABLE core.CommercialItem
(
    CommercialItemId int IDENTITY CONSTRAINT PK_CommercialItem PRIMARY KEY,
    PublicId        uniqueidentifier NOT NULL CONSTRAINT DF_CommercialItem_PublicId DEFAULT NEWSEQUENTIALID(),
    ItemType        varchar(20)    NOT NULL CONSTRAINT CK_CommercialItem_Type CHECK (ItemType IN ('LICENCE','PACK','P3','PAYG_SCOPE')),
    Name            nvarchar(200)  NOT NULL,
    Quantity        decimal(18,2)  NULL,
    Unit            varchar(20)    NULL CONSTRAINT CK_CommercialItem_Unit CHECK (Unit IN ('COPILOT_CREDIT','P3_COMMIT_UNIT','SEAT','USD','CAD','EUR','GBP')),
    StartOn         date           NULL,
    EndOn           date           NULL,
    RenewalOn       date           NULL,
    AutoRenew       bit            NOT NULL CONSTRAINT DF_CommercialItem_AutoRenew DEFAULT 0,
    BillingScope    nvarchar(400)  NULL,     -- subscription, billing policy or tenant
    ContractRef     nvarchar(200)  NULL,
    PurchaseAuthorityPersonId int  NULL CONSTRAINT FK_CommercialItem_Authority REFERENCES sec.Person(PersonId),
    ReconciledToContract bit       NOT NULL CONSTRAINT DF_CommercialItem_Reconciled DEFAULT 0,
    Notes           nvarchar(2000) NULL,
    RowVer          rowversion,
    SysStart        datetime2 GENERATED ALWAYS AS ROW START HIDDEN NOT NULL,
    SysEnd          datetime2 GENERATED ALWAYS AS ROW END   HIDDEN NOT NULL,
    PERIOD FOR SYSTEM_TIME (SysStart, SysEnd)
)
WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE = core.CommercialItemHistory));

-- Approved capacity posture per governed environment (E2 to E6). Compared with read-back by the drift job.
IF OBJECT_ID('core.ApprovedPosture') IS NULL
CREATE TABLE core.ApprovedPosture
(
    PostureId         int IDENTITY CONSTRAINT PK_ApprovedPosture PRIMARY KEY,
    WorkloadId        int           NOT NULL CONSTRAINT FK_ApprovedPosture_Workload REFERENCES core.Workload(WorkloadId),
    FundingOwnerId    int           NOT NULL,
    EnvironmentId     nvarchar(100) NOT NULL,
    AllocationCredits decimal(18,2) NOT NULL,
    TenantPoolEnabled bit           NOT NULL,
    PaygLinked        bit           NOT NULL,
    BillingPolicyId   nvarchar(100) NULL,
    AgentLimitCredits decimal(18,2) NULL,
    GroupRuleLocksTenantPool bit    NOT NULL CONSTRAINT DF_ApprovedPosture_Lock DEFAULT 0,   -- M4, C15
    DecisionId        bigint        NULL,
    EffectiveFrom     date          NOT NULL,
    EffectiveTo       date          NULL,
    SysStart          datetime2 GENERATED ALWAYS AS ROW START HIDDEN NOT NULL,
    SysEnd            datetime2 GENERATED ALWAYS AS ROW END   HIDDEN NOT NULL,
    PERIOD FOR SYSTEM_TIME (SysStart, SysEnd)
)
WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE = core.ApprovedPostureHistory));

-- Qualified licence determinations per scenario (C7, C09): no blanket rule.
IF OBJECT_ID('core.LicenceDetermination') IS NULL
CREATE TABLE core.LicenceDetermination
(
    LicenceDeterminationId int IDENTITY CONSTRAINT PK_LicenceDetermination PRIMARY KEY,
    Scenario      nvarchar(400)  NOT NULL,
    ServiceId     int            NULL CONSTRAINT FK_LicenceDetermination_Service REFERENCES core.Service(ServiceId),
    Determination nvarchar(2000) NOT NULL,
    IncludedInLicence bit        NOT NULL,
    DeterminedByPersonId int     NOT NULL,
    DeterminedOn  date           NOT NULL,
    SourceRef     nvarchar(400)  NULL
);
GO
