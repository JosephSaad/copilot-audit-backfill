-- Copilot seat licenses (the paid per-user Microsoft 365 Copilot license, "premium") reserved per LOB.
--   * Reservations are counted in seats, never in credits or money (unit SEAT).
--   * The license is granted through one shared group (for example GSGu_MCO_CopilotM365); each LOB has a
--     tracking group (GSGu_MCO_<LOB>Tracking) that says which LOB a seat belongs to. Both are on-premises
--     AD groups synced to Entra, provisioned by OIM roles through ServiceNow: the Workbench only reads them.
--   * Reservation policy: increases at any time; decreases (true-downs) only at the EA anniversary;
--     the LOB pays its full reservation until then, and seats above it are billed on top.
--   * Monthly chargeback snapshots; a certified snapshot never changes (later true-downs apply forward).
-- Additive only: nothing here drops, renames or narrows an existing object.
SET NOCOUNT ON;

-- Copilot SKUs this tenant uses: the add-on, and suites that already include Copilot (for example E7).
IF OBJECT_ID('core.SeatSku') IS NULL
CREATE TABLE core.SeatSku
(
    SkuId          uniqueidentifier NOT NULL CONSTRAINT PK_SeatSku PRIMARY KEY,
    SkuPartNumber  varchar(100)   NOT NULL,
    Name           nvarchar(200)  NOT NULL,
    Kind           varchar(20)    NOT NULL CONSTRAINT CK_SeatSku_Kind CHECK (Kind IN ('COPILOT_ADDON','INCLUDES_COPILOT')),
    IsActive       bit            NOT NULL CONSTRAINT DF_SeatSku_Active DEFAULT 1,
    Notes          nvarchar(1000) NULL
);

-- The groups that carry or describe Copilot seats. Purpose:
--   LICENSE       group-based licensing assigns the Copilot SKU to its members (shared by every LOB)
--   LOB_TRACKING  members belong to one LOB (FundingOwnerId); one active tracking group per LOB
--   EXCLUSION     members must not hold a seat (purpose to be confirmed per group)
--   CHAT_ONLY     Copilot Chat population without the paid license
--   MANAGER, OTHER  recorded for reference; no rule applies
IF OBJECT_ID('core.SeatGroup') IS NULL
CREATE TABLE core.SeatGroup
(
    SeatGroupId      int IDENTITY CONSTRAINT PK_SeatGroup PRIMARY KEY,
    PublicId         uniqueidentifier NOT NULL CONSTRAINT DF_SeatGroup_PublicId DEFAULT NEWSEQUENTIALID() CONSTRAINT UX_SeatGroup_PublicId UNIQUE,
    EntraGroupId     uniqueidentifier NULL,      -- null until the group is found in Entra by name
    DisplayName      nvarchar(256)  NOT NULL,
    Purpose          varchar(20)    NOT NULL CONSTRAINT CK_SeatGroup_Purpose CHECK (Purpose IN ('LICENSE','LOB_TRACKING','EXCLUSION','CHAT_ONLY','MANAGER','OTHER')),
    FundingOwnerId   int            NULL CONSTRAINT FK_SeatGroup_FundingOwner REFERENCES core.FundingOwner(FundingOwnerId),
    OimRoleName      nvarchar(300)  NULL,        -- the OIM role that provisions membership
    Source           varchar(20)    NOT NULL CONSTRAINT DF_SeatGroup_Source DEFAULT 'ON_PREM_AD' CONSTRAINT CK_SeatGroup_Source CHECK (Source IN ('ON_PREM_AD','CLOUD')),
    PurposeConfirmed bit            NOT NULL CONSTRAINT DF_SeatGroup_Confirmed DEFAULT 0,
    Notes            nvarchar(1000) NULL,
    EffectiveFrom    date           NOT NULL,
    EffectiveTo      date           NULL,
    RowVer           rowversion,
    SysStart         datetime2 GENERATED ALWAYS AS ROW START HIDDEN NOT NULL,
    SysEnd           datetime2 GENERATED ALWAYS AS ROW END   HIDDEN NOT NULL,
    PERIOD FOR SYSTEM_TIME (SysStart, SysEnd),
    CONSTRAINT CK_SeatGroup_Lob CHECK (Purpose <> 'LOB_TRACKING' OR FundingOwnerId IS NOT NULL)
)
WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE = core.SeatGroupHistory));
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_SeatGroup_OneTrackingPerLob')
    CREATE UNIQUE INDEX UX_SeatGroup_OneTrackingPerLob ON core.SeatGroup (FundingOwnerId) WHERE Purpose = 'LOB_TRACKING' AND EffectiveTo IS NULL;
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_SeatGroup_Name')
    CREATE UNIQUE INDEX UX_SeatGroup_Name ON core.SeatGroup (DisplayName) WHERE EffectiveTo IS NULL;

-- Seat reservation per LOB. One row per period: a change ends the open row and starts a new one, so the
-- row itself records the effective date, previous and new quantity, approver and flex-pool review (CAL-06).
-- Current reservation: EffectiveFrom <= today AND (EffectiveTo IS NULL OR EffectiveTo >= today).
IF OBJECT_ID('core.SeatReservation') IS NULL
CREATE TABLE core.SeatReservation
(
    SeatReservationId  int IDENTITY CONSTRAINT PK_SeatReservation PRIMARY KEY,
    PublicId           uniqueidentifier NOT NULL CONSTRAINT DF_SeatReservation_PublicId DEFAULT NEWSEQUENTIALID() CONSTRAINT UX_SeatReservation_PublicId UNIQUE,
    FundingOwnerId     int            NOT NULL CONSTRAINT FK_SeatReservation_FundingOwner REFERENCES core.FundingOwner(FundingOwnerId),
    Seats              int            NOT NULL CONSTRAINT CK_SeatReservation_Seats CHECK (Seats >= 0),
    PreviousSeats      int            NULL,
    ChangeKind         varchar(20)    NOT NULL CONSTRAINT CK_SeatReservation_Kind CHECK (ChangeKind IN ('INITIAL','NEW','INCREASE','TRUE_DOWN','CORRECTION')),
    EffectiveFrom      date           NOT NULL,
    EffectiveTo        date           NULL,
    RequestId          bigint         NULL,
    DecisionId         bigint         NULL,
    ApprovedByPersonId int            NULL CONSTRAINT FK_SeatReservation_Approver REFERENCES sec.Person(PersonId),
    FlexPoolReview     nvarchar(1000) NULL,
    Notes              nvarchar(1000) NULL,
    CreatedByPersonId  int            NULL,
    CreatedUtc         datetime2      NOT NULL CONSTRAINT DF_SeatReservation_Created DEFAULT SYSUTCDATETIME(),
    RowVer             rowversion,
    SysStart           datetime2 GENERATED ALWAYS AS ROW START HIDDEN NOT NULL,
    SysEnd             datetime2 GENERATED ALWAYS AS ROW END   HIDDEN NOT NULL,
    PERIOD FOR SYSTEM_TIME (SysStart, SysEnd),
    CONSTRAINT CK_SeatReservation_Period CHECK (EffectiveTo IS NULL OR EffectiveTo >= EffectiveFrom)
)
WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE = core.SeatReservationHistory));
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_SeatReservation_OneOpen')
    CREATE UNIQUE INDEX UX_SeatReservation_OneOpen ON core.SeatReservation (FundingOwnerId) WHERE EffectiveTo IS NULL;

-- Monthly chargeback per LOB (FundingOwnerId NULL: seats no LOB claims). The LOB pays its reservation
-- (FIN-05) plus seats assigned above it (FIN-06): ChargeSeats = ReservedSeats + OverSeats.
IF OBJECT_ID('core.SeatChargeSnapshot') IS NULL
CREATE TABLE core.SeatChargeSnapshot
(
    SnapshotId          bigint IDENTITY CONSTRAINT PK_SeatChargeSnapshot PRIMARY KEY,
    PublicId            uniqueidentifier NOT NULL CONSTRAINT DF_SeatChargeSnapshot_PublicId DEFAULT NEWSEQUENTIALID() CONSTRAINT UX_SeatChargeSnapshot_PublicId UNIQUE,
    PeriodMonth         date           NOT NULL CONSTRAINT CK_SeatChargeSnapshot_Month CHECK (DAY(PeriodMonth) = 1),
    FundingOwnerId      int            NULL CONSTRAINT FK_SeatChargeSnapshot_FundingOwner REFERENCES core.FundingOwner(FundingOwnerId),
    ReservedSeats       int            NOT NULL,
    AssignedSeats       int            NOT NULL,
    OverSeats           int            NOT NULL,
    ChargeSeats         int            NOT NULL,
    RatePerSeat         decimal(18,2)  NOT NULL,
    Currency            char(3)        NOT NULL,
    Amount              decimal(18,2)  NOT NULL,
    Status              varchar(20)    NOT NULL CONSTRAINT CK_SeatChargeSnapshot_Status CHECK (Status IN ('DRAFT','CERTIFIED')),
    SyncRunId           bigint         NULL,
    TakenUtc            datetime2      NOT NULL CONSTRAINT DF_SeatChargeSnapshot_Taken DEFAULT SYSUTCDATETIME(),
    CertifiedByPersonId int            NULL,
    CertifiedUtc        datetime2      NULL,
    RowVer              rowversion
);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_SeatChargeSnapshot_Period')
    CREATE UNIQUE INDEX UX_SeatChargeSnapshot_Period ON core.SeatChargeSnapshot (PeriodMonth, FundingOwnerId);

-- Seats proposed for reclaim (no Copilot activity for Seats.InactiveDays) and the business owner's answer.
-- RECLAIM opens an ITSM task to remove the OIM role; REMOVED is set when the read-back shows the seat gone.
IF OBJECT_ID('core.SeatReclaim') IS NULL
CREATE TABLE core.SeatReclaim
(
    SeatReclaimId     int IDENTITY CONSTRAINT PK_SeatReclaim PRIMARY KEY,
    PublicId          uniqueidentifier NOT NULL CONSTRAINT DF_SeatReclaim_PublicId DEFAULT NEWSEQUENTIALID() CONSTRAINT UX_SeatReclaim_PublicId UNIQUE,
    FundingOwnerId    int            NULL CONSTRAINT FK_SeatReclaim_FundingOwner REFERENCES core.FundingOwner(FundingOwnerId),
    PersonId          int            NOT NULL CONSTRAINT FK_SeatReclaim_Person REFERENCES sec.Person(PersonId),
    LastActivityDate  date           NULL,
    InactiveDays      int            NULL,
    Status            varchar(20)    NOT NULL CONSTRAINT CK_SeatReclaim_Status CHECK (Status IN ('PROPOSED','KEEP','RECLAIM','REMOVED','CANCELLED')),
    Reason            nvarchar(1000) NULL,
    DecidedByPersonId int            NULL,
    DecidedUtc        datetime2      NULL,
    TicketRef         nvarchar(100)  NULL,
    CreatedUtc        datetime2      NOT NULL CONSTRAINT DF_SeatReclaim_Created DEFAULT SYSUTCDATETIME(),
    ClosedUtc         datetime2      NULL,
    RowVer            rowversion
);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_SeatReclaim_OneOpen')
    CREATE UNIQUE INDEX UX_SeatReclaim_OneOpen ON core.SeatReclaim (PersonId) WHERE Status IN ('PROPOSED','RECLAIM');

------------------------------------------------------------------------------------------------
-- Read-back (worker, Graph, read-only)
------------------------------------------------------------------------------------------------
-- subscribedSkus per run: purchased (enabled) versus assigned (consumed) units.
IF OBJECT_ID('intg.SeatSkuReading') IS NULL
CREATE TABLE intg.SeatSkuReading
(
    SeatSkuReadingId bigint IDENTITY CONSTRAINT PK_SeatSkuReading PRIMARY KEY,
    SyncRunId        bigint           NOT NULL,
    SkuId            uniqueidentifier NOT NULL,
    SkuPartNumber    varchar(100)     NOT NULL,
    EnabledUnits     int              NOT NULL,
    WarningUnits     int              NOT NULL,
    SuspendedUnits   int              NOT NULL,
    ConsumedUnits    int              NOT NULL,
    ReadUtc          datetime2        NOT NULL CONSTRAINT DF_SeatSkuReading_Read DEFAULT SYSUTCDATETIME()
);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_SeatSkuReading_Run')
    CREATE INDEX IX_SeatSkuReading_Run ON intg.SeatSkuReading (SyncRunId) INCLUDE (SkuId, EnabledUnits, ConsumedUnits);

-- Current Copilot license states, one row per user, SKU and source (AssignedByGroupId all zeros = direct).
-- Rewritten by every seat read: rows not seen in the latest run are deleted.
IF OBJECT_ID('intg.SeatHolder') IS NULL
CREATE TABLE intg.SeatHolder
(
    MemberObjectId    uniqueidentifier NOT NULL,
    SkuId             uniqueidentifier NOT NULL,
    AssignedByGroupId uniqueidentifier NOT NULL,
    State             varchar(30)      NULL,      -- Active, ActiveWithError, Disabled, Error
    Error             varchar(50)      NULL,      -- CountViolation, MutuallyExclusiveViolation, DependencyViolation, ProhibitedInUsageLocationViolation, UniquenessViolation, Other
    UsageLocation     varchar(10)      NULL,
    UserPrincipalName nvarchar(256)    NULL,
    StateUpdatedUtc   datetime2        NULL,
    LastSeenRunId     bigint           NOT NULL,
    CONSTRAINT PK_SeatHolder PRIMARY KEY (MemberObjectId, SkuId, AssignedByGroupId)
);

-- Per seat group per run: whether it is found, synced from AD, and whether it nests other groups
-- (group-based licensing ignores members of nested groups).
IF OBJECT_ID('intg.SeatGroupReading') IS NULL
CREATE TABLE intg.SeatGroupReading
(
    SeatGroupReadingId bigint IDENTITY CONSTRAINT PK_SeatGroupReading PRIMARY KEY,
    SyncRunId          bigint           NOT NULL,
    SeatGroupId        int              NOT NULL,
    EntraGroupId       uniqueidentifier NULL,
    Found              bit              NOT NULL,
    OnPremisesSynced   bit              NULL,
    UserMembers        int              NULL,
    NestedGroups       int              NULL,
    LicensedSkuIds     nvarchar(1000)   NULL,      -- comma list of SKUs assigned to the group
    ReadUtc            datetime2        NOT NULL CONSTRAINT DF_SeatGroupReading_Read DEFAULT SYSUTCDATETIME()
);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_SeatGroupReading_Run')
    CREATE INDEX IX_SeatGroupReading_Run ON intg.SeatGroupReading (SyncRunId, SeatGroupId);

-- Microsoft 365 Copilot usage report, latest activity per user (Reports.Read.All; names must not be concealed).
IF OBJECT_ID('intg.CopilotActivity') IS NULL
CREATE TABLE intg.CopilotActivity
(
    UserPrincipalName nvarchar(256) NOT NULL CONSTRAINT PK_CopilotActivity PRIMARY KEY,
    LastActivityDate  date          NULL,
    ReportRefreshDate date          NULL,
    LastSeenRunId     bigint        NOT NULL
);

------------------------------------------------------------------------------------------------
-- Certified snapshots never change (CAL-05).
------------------------------------------------------------------------------------------------
GO
CREATE OR ALTER TRIGGER core.trg_SeatChargeSnapshot_Certified ON core.SeatChargeSnapshot
AFTER UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    IF EXISTS (SELECT 1 FROM deleted d WHERE d.Status = 'CERTIFIED')
    BEGIN
        THROW 51070, 'A certified seat chargeback snapshot cannot be changed or deleted. Later changes apply from their effective date onward.', 1;
    END
END;
GO
SET NOCOUNT ON;

------------------------------------------------------------------------------------------------
-- Settings (insert only: values the institution changed are kept).
------------------------------------------------------------------------------------------------
MERGE ref.Setting AS t
USING (VALUES
    ('Seats.EaAnniversary',          N'04-01', N'EA anniversary (month-day). Seat reservations go down only at this date (Section 4.1).'),
    ('Seats.TrueDownWindowDays',     N'45',    N'Days before the EA anniversary when a true-down can be approved. Outside the window a decrease is held and does not change the reservation (CAL-04).'),
    ('Seats.OperationalBufferSeats', N'25',    N'Seats kept back from reservation for joiners and urgent cases (Section 4.4).'),
    ('Seats.RatePerSeatMonth',       N'30.00', N'One chargeback rate per seat per month for every LOB. Placeholder: replace with the contracted price.'),
    ('Seats.Currency',               N'USD',   N'Currency of the seat chargeback rate.'),
    ('Seats.IncreaseEffective',      N'DECISION_DATE', N'When an approved increase takes effect: DECISION_DATE or NEXT_MONTH. Open Finance parameter (Section 6.10).'),
    ('Seats.ChargebackYear',         N'CALENDAR', N'Chargeback commitment period: CALENDAR (January to December). Open Finance parameter: how it lines up with the April EA cycle (Section 6.10).'),
    ('Seats.InactiveDays',           N'60',    N'A seat with no Copilot activity for this many days is proposed for reclaim.'),
    ('Seats.UsagePeriod',            N'D90',   N'Period read from the Copilot usage report (D7, D30, D90, D180).')
) AS s (SettingKey, SettingValue, Description)
ON t.SettingKey = s.SettingKey
WHEN NOT MATCHED THEN INSERT (SettingKey, SettingValue, Description) VALUES (s.SettingKey, s.SettingValue, s.Description)
WHEN MATCHED THEN UPDATE SET Description = s.Description;
GO
