-- People, roles, role assignments, delegations, RACI matrix versions.
SET NOCOUNT ON;

IF OBJECT_ID('sec.Person') IS NULL
CREATE TABLE sec.Person
(
    PersonId         int IDENTITY CONSTRAINT PK_Person PRIMARY KEY,
    PublicId         uniqueidentifier NOT NULL CONSTRAINT DF_Person_PublicId DEFAULT NEWSEQUENTIALID() CONSTRAINT UX_Person_PublicId UNIQUE,
    EntraObjectId    uniqueidentifier NULL,
    UserPrincipalName nvarchar(256)   NULL,
    DisplayName      nvarchar(256)    NOT NULL,
    Mail             nvarchar(256)    NULL,
    JobTitle         nvarchar(200)    NULL,
    Department       nvarchar(200)    NULL,
    Division         nvarchar(200)    NULL,     -- employeeOrgData.division
    CostCenterCode   nvarchar(100)    NULL,     -- employeeOrgData.costCenter
    ManagerPersonId  int              NULL CONSTRAINT FK_Person_Manager REFERENCES sec.Person(PersonId),
    AccountEnabled   bit              NOT NULL CONSTRAINT DF_Person_Enabled DEFAULT 1,
    LeaveDate        date             NULL,
    IsSynthetic      bit              NOT NULL CONSTRAINT DF_Person_Synthetic DEFAULT 0,  -- test personas only
    LastSyncedUtc    datetime2        NULL,
    SyncRunId        bigint           NULL,
    RowVer           rowversion
);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_Person_Entra')
    CREATE UNIQUE INDEX UX_Person_Entra ON sec.Person (EntraObjectId) WHERE EntraObjectId IS NOT NULL;
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Person_Upn')
    CREATE INDEX IX_Person_Upn ON sec.Person (UserPrincipalName);

-- RACI functions (columns of the matrix).
IF OBJECT_ID('sec.RaciFunction') IS NULL
CREATE TABLE sec.RaciFunction
(
    FunctionCode varchar(30)   NOT NULL CONSTRAINT PK_RaciFunction PRIMARY KEY,
    Name         nvarchar(100) NOT NULL,
    ScopeType    varchar(20)   NOT NULL CONSTRAINT CK_RaciFunction_Scope CHECK (ScopeType IN ('TENANT','FUNDING_OWNER','EXTERNAL')),
    DecisionRoleCode varchar(40) NULL,   -- role that holds A for this function
    WorkRoleCode     varchar(40) NULL,   -- role that holds R
    ConsultRoleCode  varchar(40) NULL,   -- role that answers a consult (C)
    SortOrder    int           NOT NULL
);

-- Workbench roles. Coarse Entra app roles gate pages; these scoped roles decide who acts on what.
IF OBJECT_ID('sec.RoleDefinition') IS NULL
CREATE TABLE sec.RoleDefinition
(
    RoleCode     varchar(40)    NOT NULL CONSTRAINT PK_RoleDefinition PRIMARY KEY,
    Name         nvarchar(100)  NOT NULL,
    Description  nvarchar(1000) NOT NULL,
    FunctionCode varchar(30)    NULL CONSTRAINT FK_RoleDefinition_Function REFERENCES sec.RaciFunction(FunctionCode),
    ScopeType    varchar(20)    NOT NULL CONSTRAINT CK_RoleDefinition_Scope CHECK (ScopeType IN ('TENANT','FUNDING_OWNER')),
    IsCentral    bit            NOT NULL,   -- central roles see every funding owner
    IsLob        bit            NOT NULL,
    CanDecide    bit            NOT NULL,   -- false for administrators and auditors (SoD5)
    SortOrder    int            NOT NULL
);

IF OBJECT_ID('sec.RolePermission') IS NULL
CREATE TABLE sec.RolePermission
(
    RoleCode   varchar(40) NOT NULL CONSTRAINT FK_RolePermission_Role REFERENCES sec.RoleDefinition(RoleCode),
    Permission varchar(60) NOT NULL,
    CONSTRAINT PK_RolePermission PRIMARY KEY (RoleCode, Permission)
);

-- Role assignments with business-effective dates and system versioning.
IF OBJECT_ID('sec.RoleAssignment') IS NULL
CREATE TABLE sec.RoleAssignment
(
    RoleAssignmentId int IDENTITY CONSTRAINT PK_RoleAssignment PRIMARY KEY,
    PublicId        uniqueidentifier NOT NULL CONSTRAINT DF_RoleAssignment_PublicId DEFAULT NEWSEQUENTIALID(),
    PersonId        int          NOT NULL CONSTRAINT FK_RoleAssignment_Person REFERENCES sec.Person(PersonId),
    RoleCode        varchar(40)  NOT NULL CONSTRAINT FK_RoleAssignment_Role REFERENCES sec.RoleDefinition(RoleCode),
    ScopeType       varchar(20)  NOT NULL CONSTRAINT CK_RoleAssignment_Scope CHECK (ScopeType IN ('TENANT','FUNDING_OWNER')),
    ScopeId         int          NULL,       -- FundingOwnerId when ScopeType = FUNDING_OWNER
    EffectiveFrom   date         NOT NULL,
    EffectiveTo     date         NULL,
    Source          varchar(20)  NOT NULL CONSTRAINT CK_RoleAssignment_Source CHECK (Source IN ('WORKBENCH','ENTRA_GROUP','SEED')),
    SourceGroupId   uniqueidentifier NULL,
    AcceptedUtc     datetime2    NULL,       -- business owners accept the role (activation checklist item 1)
    ReviewDueOn     date         NULL,
    GrantedByPersonId int        NULL,
    RowVer          rowversion,
    SysStart        datetime2 GENERATED ALWAYS AS ROW START HIDDEN NOT NULL,
    SysEnd          datetime2 GENERATED ALWAYS AS ROW END   HIDDEN NOT NULL,
    PERIOD FOR SYSTEM_TIME (SysStart, SysEnd),
    CONSTRAINT CK_RoleAssignment_ScopeId CHECK ((ScopeType = 'TENANT' AND ScopeId IS NULL) OR (ScopeType = 'FUNDING_OWNER' AND ScopeId IS NOT NULL))
)
WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE = sec.RoleAssignmentHistory));
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_RoleAssignment_Person')
    CREATE INDEX IX_RoleAssignment_Person ON sec.RoleAssignment (PersonId, RoleCode) INCLUDE (ScopeType, ScopeId, EffectiveFrom, EffectiveTo);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_RoleAssignment_Role')
    CREATE INDEX IX_RoleAssignment_Role ON sec.RoleAssignment (RoleCode, ScopeType, ScopeId) INCLUDE (PersonId, EffectiveFrom, EffectiveTo);

-- Central roles synced from Entra security groups.
IF OBJECT_ID('sec.GroupRoleMap') IS NULL
CREATE TABLE sec.GroupRoleMap
(
    EntraGroupId uniqueidentifier NOT NULL,
    RoleCode     varchar(40)      NOT NULL CONSTRAINT FK_GroupRoleMap_Role REFERENCES sec.RoleDefinition(RoleCode),
    GroupName    nvarchar(256)    NULL,
    CONSTRAINT PK_GroupRoleMap PRIMARY KEY (EntraGroupId, RoleCode)
);

-- Delegation of routine approval (B5) from a business owner to a named approver, within envelopes.
IF OBJECT_ID('sec.Delegation') IS NULL
CREATE TABLE sec.Delegation
(
    DelegationId      int IDENTITY CONSTRAINT PK_Delegation PRIMARY KEY,
    PublicId          uniqueidentifier NOT NULL CONSTRAINT DF_Delegation_PublicId DEFAULT NEWSEQUENTIALID(),
    FundingOwnerId    int           NOT NULL,
    DelegatePersonId  int           NOT NULL CONSTRAINT FK_Delegation_Delegate REFERENCES sec.Person(PersonId),
    GrantedByPersonId int           NOT NULL CONSTRAINT FK_Delegation_Grantor REFERENCES sec.Person(PersonId),
    MaxCreditsPerRequest decimal(18,2) NULL,
    EffectiveFrom     date          NOT NULL,
    EffectiveTo       date          NULL,
    Status            varchar(20)   NOT NULL CONSTRAINT CK_Delegation_Status CHECK (Status IN ('ACTIVE','REVOKED')),
    Notes             nvarchar(1000) NULL,
    RowVer            rowversion,
    SysStart          datetime2 GENERATED ALWAYS AS ROW START HIDDEN NOT NULL,
    SysEnd            datetime2 GENERATED ALWAYS AS ROW END   HIDDEN NOT NULL,
    PERIOD FOR SYSTEM_TIME (SysStart, SysEnd)
)
WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE = sec.DelegationHistory));

IF OBJECT_ID('sec.DelegationEnvelope') IS NULL
CREATE TABLE sec.DelegationEnvelope
(
    DelegationId int NOT NULL CONSTRAINT FK_DelegationEnvelope_Delegation REFERENCES sec.Delegation(DelegationId),
    EnvelopeId   int NOT NULL,
    CONSTRAINT PK_DelegationEnvelope PRIMARY KEY (DelegationId, EnvelopeId)
);

-- RACI matrix as versioned configuration.
IF OBJECT_ID('sec.RaciVersion') IS NULL
CREATE TABLE sec.RaciVersion
(
    RaciVersionId   int IDENTITY CONSTRAINT PK_RaciVersion PRIMARY KEY,
    VersionNo       int           NOT NULL CONSTRAINT UX_RaciVersion_No UNIQUE,
    Status          varchar(20)   NOT NULL CONSTRAINT CK_RaciVersion_Status CHECK (Status IN ('DRAFT','PENDING','ACTIVE','RETIRED')),
    Notes           nvarchar(2000) NULL,
    CreatedByPersonId int         NULL,
    CreatedUtc      datetime2     NOT NULL CONSTRAINT DF_RaciVersion_Created DEFAULT SYSUTCDATETIME(),
    PlatformOwnerApprovalPersonId int NULL,
    PlatformOwnerApprovalUtc datetime2 NULL,
    SteeringApprovalPersonId int  NULL,
    SteeringApprovalUtc datetime2 NULL,
    ActivatedUtc    datetime2     NULL,
    RowVer          rowversion
);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_RaciVersion_OneActive')
    CREATE UNIQUE INDEX UX_RaciVersion_OneActive ON sec.RaciVersion (Status) WHERE Status = 'ACTIVE';

IF OBJECT_ID('sec.RaciActivity') IS NULL
CREATE TABLE sec.RaciActivity
(
    ActivityCode   varchar(60)   NOT NULL CONSTRAINT PK_RaciActivity PRIMARY KEY,
    Name           nvarchar(200) NOT NULL,
    GovernsStages  nvarchar(200) NOT NULL,
    SimplifiedRow  nvarchar(200) NOT NULL,
    SortOrder      int           NOT NULL
);

IF OBJECT_ID('sec.RaciCell') IS NULL
CREATE TABLE sec.RaciCell
(
    RaciVersionId int         NOT NULL CONSTRAINT FK_RaciCell_Version REFERENCES sec.RaciVersion(RaciVersionId),
    ActivityCode  varchar(60) NOT NULL CONSTRAINT FK_RaciCell_Activity REFERENCES sec.RaciActivity(ActivityCode),
    FunctionCode  varchar(30) NOT NULL CONSTRAINT FK_RaciCell_Function REFERENCES sec.RaciFunction(FunctionCode),
    RaciValue     varchar(3)  NOT NULL CONSTRAINT CK_RaciCell_Value CHECK (RaciValue IN ('R','A','A/R','C','I')),
    CONSTRAINT PK_RaciCell PRIMARY KEY (RaciVersionId, ActivityCode, FunctionCode)
);
-- The unique index stops a second A; activation checks that every activity has one.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_RaciCell_OneAccountable')
    CREATE UNIQUE INDEX UX_RaciCell_OneAccountable ON sec.RaciCell (RaciVersionId, ActivityCode) WHERE RaciValue IN ('A','A/R');

-- Policy switches for segregation of duties (SoD4: may one person hold two decision types on a request).
IF OBJECT_ID('sec.SodSetting') IS NULL
CREATE TABLE sec.SodSetting
(
    RuleCode    varchar(10)   NOT NULL CONSTRAINT PK_SodSetting PRIMARY KEY,
    Description nvarchar(400) NOT NULL,
    IsEnforced  bit           NOT NULL,
    AllowSamePersonForTwoDecisionTypes bit NOT NULL CONSTRAINT DF_SodSetting_Two DEFAULT 0
);

-- Distributed token cache for Microsoft.Identity.Web (delegated tokens for operator inventory sync).
IF OBJECT_ID('sec.TokenCache') IS NULL
CREATE TABLE sec.TokenCache
(
    Id                         nvarchar(449)  COLLATE SQL_Latin1_General_CP1_CS_AS NOT NULL CONSTRAINT PK_TokenCache PRIMARY KEY,
    Value                      varbinary(max) NOT NULL,
    ExpiresAtTime              datetimeoffset NOT NULL,
    SlidingExpirationInSeconds bigint         NULL,
    AbsoluteExpiration         datetimeoffset NULL
);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_TokenCache_Expires')
    CREATE INDEX IX_TokenCache_Expires ON sec.TokenCache (ExpiresAtTime);
GO
