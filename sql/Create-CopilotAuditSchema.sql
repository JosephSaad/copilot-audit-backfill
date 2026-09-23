/*
    Copilot audit backfill - schema
    ------------------------------------------------------------------------
    Idempotent. Invoke-CopilotAuditBackfill.ps1 runs this automatically on
    every start; it is provided separately so a DBA can review or pre-create
    the objects. Single batch (no GO) so it runs the same from SSMS or code.

    Requires SQL Server 2016+. The finalize step (columnstore over
    NVARCHAR(MAX)) requires 2017+.

    Space: AuditData arrives gzip-compressed (AuditDataGz). Microsoft's
    documented sample records compress to ~1.1-1.3 KB each (2.5-9 KB raw), so
    plan ~1.5 KB per record in staging - about 75 GB for 50M records, versus
    125-450 GB uncompressed. The run summary prints the actual ratio. Drop the
    staging table once the finalized tables are verified.
*/
SET NOCOUNT ON;

IF SCHEMA_ID(N'audit') IS NULL
    EXEC (N'CREATE SCHEMA audit AUTHORIZATION dbo;');

/* The requested range. Finalize refuses to run until it is fully covered. */
IF OBJECT_ID(N'audit.CopilotBackfillRange', N'U') IS NULL
    CREATE TABLE audit.CopilotBackfillRange
    (
        Id              TINYINT         NOT NULL CONSTRAINT PK_CopilotBackfillRange PRIMARY KEY
                                                 CONSTRAINT CK_CopilotBackfillRange_Single CHECK (Id = 1),
        RangeStartUtc   DATETIME2(0)    NOT NULL,
        RangeEndUtc     DATETIME2(0)    NOT NULL,
        Operations      NVARCHAR(400)   NOT NULL,
        StartedUtc      DATETIME2(0)    NOT NULL CONSTRAINT DF_CopilotBackfillRange_Started DEFAULT (SYSUTCDATETIME()),
        CompletedUtc    DATETIME2(0)    NULL
    );

/* One row per time window: the backfill's durable state. Resume reads this. */
IF OBJECT_ID(N'audit.CopilotBackfillWindow', N'U') IS NULL
    CREATE TABLE audit.CopilotBackfillWindow
    (
        WindowId        BIGINT IDENTITY(1,1) NOT NULL
                            CONSTRAINT PK_CopilotBackfillWindow PRIMARY KEY CLUSTERED,
        ParentWindowId  BIGINT          NULL,       -- set when a window was split
        StartUtc        DATETIME2(0)    NOT NULL,
        EndUtc          DATETIME2(0)    NOT NULL,
        Status          VARCHAR(16)     NOT NULL,   -- Pending|Submitted|Ready|Downloading|Loaded|Split|Failed
        QueryId         VARCHAR(64)     NULL,       -- Graph auditLogQuery id
        Attempts        INT             NOT NULL CONSTRAINT DF_CopilotBackfillWindow_Attempts    DEFAULT (0),
        Submissions     INT             NOT NULL CONSTRAINT DF_CopilotBackfillWindow_Submissions DEFAULT (0),
        ApproxRecords   BIGINT          NULL,       -- service's approximateReturnedRecordCount
        RowsLoaded      BIGINT          NULL,
        SubmittedUtc    DATETIME2(0)    NULL,
        CompletedUtc    DATETIME2(0)    NULL,
        LastError       NVARCHAR(2000)  NULL,
        CreatedUtc      DATETIME2(0)    NOT NULL CONSTRAINT DF_CopilotBackfillWindow_Created DEFAULT (SYSUTCDATETIME()),
        UpdatedUtc      DATETIME2(0)    NOT NULL CONSTRAINT DF_CopilotBackfillWindow_Updated DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT CK_CopilotBackfillWindow_Range CHECK (EndUtc > StartUtc)
    );

/*
    Landing table. A HEAP on purpose: SqlBulkCopy with TABLOCK takes
    bulk-update (BU) locks on a heap, so several writers load it concurrently
    with minimal logging. Any index here would serialize them.

    Every row carries (WindowId, Attempt). A window interrupted mid-download
    is re-downloaded under a new Attempt number; finalize keeps only each
    Loaded window's final attempt, so partial loads never double-count.

    Column lengths must match RowSchema.MaxLength in CopilotAuditEngine.cs.
*/
IF OBJECT_ID(N'audit.CopilotAuditStage', N'U') IS NULL
    CREATE TABLE audit.CopilotAuditStage
    (
        WindowId            BIGINT          NOT NULL,
        Attempt             INT             NOT NULL,
        RecordId            VARCHAR(64)     NOT NULL,
        CreatedDateTime     DATETIME2(7)    NOT NULL,
        RecordType          VARCHAR(128)    NULL,
        Operation           VARCHAR(128)    NULL,
        OrganizationId      VARCHAR(64)     NULL,
        UserType            VARCHAR(64)     NULL,
        UserId              NVARCHAR(512)   NULL,
        UserPrincipalName   NVARCHAR(512)   NULL,
        Service             VARCHAR(128)    NULL,
        ObjectId            NVARCHAR(4000)  NULL,
        ClientIp            VARCHAR(128)    NULL,
        AdministrativeUnits NVARCHAR(4000)  NULL,
        AuditDataGz         VARBINARY(MAX)  NULL,   -- gzip of the full audit record JSON (UTF-16):
                                                    --   CAST(DECOMPRESS(AuditDataGz) AS NVARCHAR(MAX))
        LoadedUtc           DATETIME2(0)    NOT NULL CONSTRAINT DF_CopilotAuditStage_Loaded DEFAULT (SYSUTCDATETIME())
    );
