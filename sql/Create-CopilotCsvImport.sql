/*
    Copilot audit - CSV import
    ------------------------------------------------------------------------
    Loads audit log exports from the Purview portal (CSV) into the same tables
    the Graph backfill fills, so everything downstream - the flattened table,
    sessions, the bi schema - works unchanged. tools/Import-CopilotAuditCsv.ps1
    runs this script, bulk-loads a folder of CSV files into
    audit.CopilotCsvStage, then calls audit.usp_MergeCopilotCsv.

      audit.CopilotCsvImport   one row per run of the importer
      audit.CopilotCsvFile     one row per file loaded, with its SHA-256, so a
                               file already imported is skipped next time
      audit.CopilotCsvReject   rows that could not be loaded, and why
      audit.CopilotCsvStage    the rows of the import being merged (a heap,
                               emptied after each merge)

    Exports can overlap in range and repeat each other, and they can overlap
    what the Graph backfill or -Incremental already loaded: the merge keeps one
    row per RecordId, and a record audit.CopilotAudit already holds wins.

    A database can start from CSV files alone. Then this script creates
    audit.CopilotAudit, audit.CopilotInteraction and their views - the same
    objects Finalize-CopilotAudit.sql builds - and the merge records the range
    the exports cover in audit.CopilotBackfillRange (Source = 'CSV'), so
    Invoke-CopilotAuditBackfill.ps1 -Incremental can carry on from its end.

    Idempotent. Requires SQL Server 2017+ and sql/Create-CopilotAuditSchema.sql
    (for audit.fnShredCopilotInteraction); the importer runs both.
*/
SET NOCOUNT ON;

IF SCHEMA_ID(N'audit') IS NULL
    EXEC (N'CREATE SCHEMA audit AUTHORIZATION dbo;');

IF OBJECT_ID(N'audit.CopilotCsvImport', N'U') IS NULL
    CREATE TABLE audit.CopilotCsvImport
    (
        ImportId            INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_CopilotCsvImport PRIMARY KEY,
        FolderPath          NVARCHAR(1024)  NOT NULL,
        Operations          NVARCHAR(400)   NOT NULL,   -- the database's operation set, as the backfill records it
        StartedUtc          DATETIME2(0)    NOT NULL CONSTRAINT DF_CopilotCsvImport_Started DEFAULT (SYSUTCDATETIME()),
        MergedUtc           DATETIME2(0)    NULL,
        FilesLoaded         INT             NULL,
        RowsRead            BIGINT          NULL,       -- data rows in the files
        RowsRejected        BIGINT          NULL,       -- see audit.CopilotCsvReject
        RowsOtherOperation  BIGINT          NULL,       -- valid, but not an operation this database holds
        RowsStaged          BIGINT          NULL,
        RowsDuplicate       BIGINT          NULL,       -- the same RecordId more than once across the files
        RowsAlreadyLoaded   BIGINT          NULL,       -- already in audit.CopilotAudit
        RowsAdded           BIGINT          NULL,
        EarliestAddedUtc    DATETIME2(0)    NULL,
        LatestAddedUtc      DATETIME2(0)    NULL,
        CoveredFromUtc      DATETIME2(0)    NULL,       -- the search range the exports were made for, if given
        CoveredToUtc        DATETIME2(0)    NULL
    );

IF OBJECT_ID(N'audit.CopilotCsvFile', N'U') IS NULL
    CREATE TABLE audit.CopilotCsvFile
    (
        FileId              INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_CopilotCsvFile PRIMARY KEY,
        ImportId            INT             NOT NULL,
        FileName            NVARCHAR(260)   NOT NULL,
        FullPath            NVARCHAR(1024)  NOT NULL,
        Sha256              CHAR(64)        NOT NULL,
        SizeBytes           BIGINT          NOT NULL,
        LastWriteUtc        DATETIME2(0)    NULL,
        RowsRead            BIGINT          NULL,
        RowsRejected        BIGINT          NULL,
        RowsOtherOperation  BIGINT          NULL,
        RowsStaged          BIGINT          NULL,
        EarliestRecordUtc   DATETIME2(0)    NULL,
        LatestRecordUtc     DATETIME2(0)    NULL,
        LoadedUtc           DATETIME2(0)    NOT NULL CONSTRAINT DF_CopilotCsvFile_Loaded DEFAULT (SYSUTCDATETIME()),
        INDEX IX_CopilotCsvFile_Sha256 (Sha256)
    );

IF OBJECT_ID(N'audit.CopilotCsvReject', N'U') IS NULL
    CREATE TABLE audit.CopilotCsvReject
    (
        FileId              INT             NOT NULL,
        LineNumber          BIGINT          NOT NULL,   -- where the row starts in the file
        RecordId            VARCHAR(64)     NULL,
        Reason              NVARCHAR(400)   NOT NULL,
        INDEX IX_CopilotCsvReject_File CLUSTERED (FileId, LineNumber)
    );

/* The shape of audit.CopilotAuditStage, with the file in place of the window. */
IF OBJECT_ID(N'audit.CopilotCsvStage', N'U') IS NULL
    CREATE TABLE audit.CopilotCsvStage
    (
        FileId              INT             NOT NULL,
        LineNumber          BIGINT          NOT NULL,
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
        AuditDataGz         VARBINARY(MAX)  NULL
    );

/* Which path recorded the backfill range: NULL is the Graph backfill, 'CSV' an
   import. Only a CSV-recorded range is widened by later imports. */
IF COL_LENGTH(N'audit.CopilotBackfillRange', N'Source') IS NULL
    ALTER TABLE audit.CopilotBackfillRange ADD Source VARCHAR(8) NULL;
/* The next batch reads Source, so it must compile after the column exists. */
GO

/*
    The final tables, for a database that starts from CSV files. Same DDL as
    Finalize-CopilotAudit.sql. Not created while a Graph backfill is started
    but not finalized: finalize builds them itself, from its staging table.
*/
IF OBJECT_ID(N'audit.CopilotAudit', N'U') IS NULL
   AND NOT EXISTS (SELECT 1 FROM audit.CopilotBackfillRange WHERE Id = 1 AND ISNULL(Source, '') <> 'CSV')
BEGIN
    CREATE TABLE audit.CopilotAudit
    (
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
        AuditDataGz         VARBINARY(MAX)  NULL,
        WindowId            BIGINT          NOT NULL,
        LoadId              INT             NULL,
        CsvImportId         INT             NULL,
        INDEX CCI_CopilotAudit CLUSTERED COLUMNSTORE
    );

    CREATE TABLE audit.CopilotInteraction
    (
        RecordId              VARCHAR(64)    NOT NULL,
        CreatedDateTime       DATETIME2(0)   NOT NULL,
        ActivityDate          DATE           NOT NULL,
        UserPrincipalName     NVARCHAR(512)  NULL,
        AppHost               NVARCHAR(100)  NULL,
        AppIdentity           NVARCHAR(200)  NULL,
        ClientRegion          NVARCHAR(20)   NULL,
        ModelName             NVARCHAR(200)  NULL,
        ModelProvider         NVARCHAR(200)  NULL,
        ThreadId              NVARCHAR(400)  NULL,
        PromptCount           INT            NULL,
        AccessedResourceCount INT            NULL,
        UsedWebSearch         BIT            NULL,
        INDEX CCI_CopilotInteraction CLUSTERED COLUMNSTORE
    );

    EXEC (N'
    CREATE OR ALTER VIEW audit.vCopilotDailyUsage
    AS
    SELECT ActivityDate,
           AppHost,
           COUNT(*)                          AS Interactions,
           COUNT(DISTINCT UserPrincipalName) AS ActiveUsers,
           SUM(PromptCount)                  AS Prompts,
           SUM(CAST(UsedWebSearch AS INT))   AS WebGroundedInteractions
    FROM audit.CopilotInteraction
    GROUP BY ActivityDate, AppHost;');

    EXEC (N'
    CREATE OR ALTER VIEW audit.vCopilotAuditRecord
    AS
    SELECT RecordId,
           CreatedDateTime                                            AS CreationDate,
           UPPER(LEFT(RecordType, 1)) + SUBSTRING(RecordType, 2, 127) AS RecordType,
           Operation,
           COALESCE(UserPrincipalName, UserId)                        AS UserId,
           CAST(DECOMPRESS(AuditDataGz) AS NVARCHAR(MAX))             AS AuditData,
           AdministrativeUnits                                        AS AssociatedAdminUnits,
           CAST(NULL AS NVARCHAR(MAX))                                AS AssociatedAdminUnitsNames
    FROM audit.CopilotAudit;');
END;

/* Lineage for imported rows, on a table finalized by an earlier version. */
IF OBJECT_ID(N'audit.CopilotAudit', N'U') IS NOT NULL AND COL_LENGTH(N'audit.CopilotAudit', N'CsvImportId') IS NULL
    ALTER TABLE audit.CopilotAudit ADD CsvImportId INT NULL;
IF OBJECT_ID(N'audit.CopilotAudit', N'U') IS NOT NULL AND COL_LENGTH(N'audit.CopilotAudit', N'LoadId') IS NULL
    ALTER TABLE audit.CopilotAudit ADD LoadId INT NULL;
GO

IF OBJECT_ID(N'audit.usp_MergeCopilotCsv', N'P') IS NULL
    EXEC (N'CREATE PROCEDURE audit.usp_MergeCopilotCsv AS BEGIN SET NOCOUNT ON; END;');
GO

/*
    audit.usp_MergeCopilotCsv - appends one import's staged rows.

      1. Keeps one row per RecordId across the import's files (exports that
         overlap repeat a record; which copy is kept does not matter, they are
         the same record).
      2. Drops the records audit.CopilotAudit already holds, from the Graph
         backfill, an -Incremental load or an earlier import.
      3. Appends the rest to audit.CopilotAudit (CsvImportId for lineage;
         WindowId holds the audit.CopilotCsvFile) and their shredded rows to
         audit.CopilotInteraction through audit.fnShredCopilotInteraction, the
         same as the other two paths.
      4. Records the range the exports cover, when given, so -Incremental can
         continue from its end: creates audit.CopilotBackfillRange for a
         database that starts from CSV, or widens one an earlier import made.
         A range the Graph backfill recorded is left alone.
      5. Marks the import merged and empties the staging table - all but the
         last in one transaction.

    Refuses while a Graph backfill is started but not finalized: finalize
    rebuilds audit.CopilotAudit from its own staging table and would drop the
    imported rows.
*/
ALTER PROCEDURE audit.usp_MergeCopilotCsv
    @ImportId           INT,
    @CoveredFromUtc     DATETIME2(0) = NULL,
    @CoveredToUtc       DATETIME2(0) = NULL,
    @ReportingTimeZone  SYSNAME      = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    SET @ReportingTimeZone = COALESCE(@ReportingTimeZone, audit.ReportingTimeZone());   -- the zone Install-CopilotAudit.ps1 -ReportingTimeZone stored

    DECLARE @msg NVARCHAR(800), @ops NVARCHAR(400), @merged DATETIME2(0);
    SELECT @ops = Operations, @merged = MergedUtc FROM audit.CopilotCsvImport WHERE ImportId = @ImportId;

    IF @ops IS NULL
        THROW 50101, N'Merge refused: no such CSV import.', 1;
    IF @merged IS NOT NULL
        THROW 50102, N'Merge refused: this import is already merged.', 1;
    IF OBJECT_ID(N'audit.fnShredCopilotInteraction', N'IF') IS NULL
        THROW 50103, N'Merge refused: audit.fnShredCopilotInteraction is missing. Run sql/Create-CopilotAuditSchema.sql first.', 1;
    IF NOT EXISTS (SELECT 1 FROM sys.time_zone_info WHERE name = @ReportingTimeZone)
        THROW 50104, N'Merge refused: @ReportingTimeZone is not a name in sys.time_zone_info.', 1;

    DECLARE @rangeOps NVARCHAR(400), @rangeSource VARCHAR(8), @rangeDone DATETIME2(0), @hasRange BIT = 0;
    SELECT @hasRange = 1, @rangeOps = Operations, @rangeSource = Source, @rangeDone = CompletedUtc
    FROM audit.CopilotBackfillRange WHERE Id = 1;

    IF @hasRange = 1 AND ISNULL(@rangeSource, '') <> 'CSV'
       AND (@rangeDone IS NULL OR OBJECT_ID(N'audit.CopilotAudit', N'U') IS NULL)
        THROW 50105, N'Merge refused: a Graph backfill has started in this database but is not finalized. Finish it and run -FinalizeOnly first; finalize rebuilds audit.CopilotAudit and would drop imported rows.', 1;
    IF @hasRange = 1 AND @rangeOps <> @ops
    BEGIN
        SET @msg = CONCAT(N'Merge refused: this database holds ''', @rangeOps, N''', and the import is of ''', @ops, N'''.');
        THROW 50106, @msg, 1;
    END;
    IF OBJECT_ID(N'audit.CopilotAudit', N'U') IS NULL OR OBJECT_ID(N'audit.CopilotInteraction', N'U') IS NULL
        THROW 50107, N'Merge refused: audit.CopilotAudit does not exist. Run sql/Create-CopilotCsvImport.sql.', 1;
    IF @CoveredToUtc IS NOT NULL AND @CoveredToUtc > SYSUTCDATETIME()
        THROW 50108, N'Merge refused: @CoveredToUtc is in the future.', 1;
    IF @CoveredFromUtc IS NOT NULL AND @CoveredToUtc IS NOT NULL AND @CoveredFromUtc >= @CoveredToUtc
        THROW 50109, N'Merge refused: @CoveredFromUtc must be before @CoveredToUtc.', 1;

    /* ---- 1. One row per RecordId across the import's files ------------------ */

    DROP TABLE IF EXISTS #cand;
    SELECT s.RecordId, s.CreatedDateTime, s.RecordType, s.Operation, s.OrganizationId, s.UserType, s.UserId,
           s.UserPrincipalName, s.Service, s.ObjectId, s.ClientIp, s.AdministrativeUnits, s.AuditDataGz,
           CAST(s.FileId AS BIGINT) AS WindowId,
           ROW_NUMBER() OVER (PARTITION BY s.RecordId ORDER BY s.FileId DESC, s.LineNumber DESC) AS rn
    INTO #cand
    FROM audit.CopilotCsvStage AS s
    JOIN audit.CopilotCsvFile AS f ON f.FileId = s.FileId AND f.ImportId = @ImportId;

    DECLARE @staged BIGINT = @@ROWCOUNT;
    DELETE FROM #cand WHERE rn > 1;
    DECLARE @duplicate BIGINT = @@ROWCOUNT;

    /* ---- 2. Skip what audit.CopilotAudit already holds ---------------------- */
    /*
        A record's creation time is fixed, so a copy already loaded has one in
        the import's range. Bounding the probe by it (with a day's margin) lets
        the columnstore skip row groups outside it.
    */
    CREATE CLUSTERED INDEX CIX_cand ON #cand (RecordId);

    DECLARE @lo DATETIME2(7), @hi DATETIME2(7);
    SELECT @lo = DATEADD(DAY, -1, MIN(CreatedDateTime)), @hi = DATEADD(DAY, 1, MAX(CreatedDateTime)) FROM #cand;

    DELETE c
    FROM #cand AS c
    WHERE EXISTS (SELECT 1 FROM audit.CopilotAudit AS a
                  WHERE a.RecordId = c.RecordId
                    AND a.CreatedDateTime >= @lo
                    AND a.CreatedDateTime <  @hi);
    DECLARE @already BIGINT = @@ROWCOUNT;
    DECLARE @added BIGINT = (SELECT COUNT_BIG(*) FROM #cand);
    DECLARE @first DATETIME2(0), @last DATETIME2(0);
    SELECT @first = MIN(CreatedDateTime), @last = MAX(CreatedDateTime) FROM #cand;

    /* ---- 3 and 4. Append, record coverage, in one transaction --------------- */

    BEGIN TRANSACTION;

    INSERT INTO audit.CopilotAudit
        (RecordId, CreatedDateTime, RecordType, Operation, OrganizationId, UserType, UserId,
         UserPrincipalName, Service, ObjectId, ClientIp, AdministrativeUnits, AuditDataGz, WindowId, LoadId, CsvImportId)
    SELECT c.RecordId, c.CreatedDateTime, c.RecordType, c.Operation, c.OrganizationId, c.UserType, c.UserId,
           c.UserPrincipalName, c.Service, c.ObjectId, c.ClientIp, c.AdministrativeUnits, c.AuditDataGz, c.WindowId,
           NULL, @ImportId
    FROM #cand AS c;

    INSERT INTO audit.CopilotInteraction
        (RecordId, CreatedDateTime, ActivityDate, UserPrincipalName, AppHost, AppIdentity, ClientRegion,
         ModelName, ModelProvider, ThreadId, PromptCount, AccessedResourceCount, UsedWebSearch)
    SELECT
        c.RecordId,
        CAST(c.CreatedDateTime AS DATETIME2(0)),
        CAST((c.CreatedDateTime AT TIME ZONE 'UTC') AT TIME ZONE @ReportingTimeZone AS DATE),
        COALESCE(c.UserPrincipalName, s.JsonUserId),
        s.AppHost, s.AppIdentity, s.ClientRegion, s.ModelName, s.ModelProvider, s.ThreadId,
        s.PromptCount, s.AccessedResourceCount, s.UsedWebSearch
    FROM #cand AS c
    CROSS APPLY audit.fnShredCopilotInteraction(c.AuditDataGz) AS s;

    /* Coverage. With no range recorded yet, the import's own data gives the
       start when @CoveredFromUtc is not passed: the day of its first record. */
    IF @CoveredToUtc IS NOT NULL
    BEGIN
        IF @hasRange = 0
            INSERT INTO audit.CopilotBackfillRange (Id, RangeStartUtc, RangeEndUtc, Operations, CompletedUtc, Source)
            VALUES (1,
                    COALESCE(@CoveredFromUtc, CAST(CAST(@first AS DATE) AS DATETIME2(0)), @CoveredToUtc),
                    @CoveredToUtc, @ops, SYSUTCDATETIME(), 'CSV');
        ELSE IF @rangeSource = 'CSV'
            UPDATE audit.CopilotBackfillRange
            SET RangeStartUtc = CASE WHEN @CoveredFromUtc < RangeStartUtc THEN @CoveredFromUtc ELSE RangeStartUtc END,
                RangeEndUtc   = CASE WHEN @CoveredToUtc   > RangeEndUtc   THEN @CoveredToUtc   ELSE RangeEndUtc   END,
                CompletedUtc  = SYSUTCDATETIME()
            WHERE Id = 1;
    END;

    UPDATE audit.CopilotCsvImport
    SET MergedUtc = SYSUTCDATETIME(),
        FilesLoaded = (SELECT COUNT(*) FROM audit.CopilotCsvFile WHERE ImportId = @ImportId),
        RowsRead = (SELECT SUM(RowsRead) FROM audit.CopilotCsvFile WHERE ImportId = @ImportId),
        RowsRejected = (SELECT SUM(RowsRejected) FROM audit.CopilotCsvFile WHERE ImportId = @ImportId),
        RowsOtherOperation = (SELECT SUM(RowsOtherOperation) FROM audit.CopilotCsvFile WHERE ImportId = @ImportId),
        RowsStaged = @staged, RowsDuplicate = @duplicate, RowsAlreadyLoaded = @already, RowsAdded = @added,
        EarliestAddedUtc = @first, LatestAddedUtc = @last,
        CoveredFromUtc = @CoveredFromUtc, CoveredToUtc = @CoveredToUtc
    WHERE ImportId = @ImportId;

    COMMIT TRANSACTION;

    TRUNCATE TABLE audit.CopilotCsvStage;

    SET @msg = CONCAT(N'CSV import ', @ImportId, N' merged: ', FORMAT(@staged, 'N0'), N' row(s) staged, ',
                      FORMAT(@duplicate, 'N0'), N' duplicate(s) across files, ', FORMAT(@already, 'N0'),
                      N' already loaded, ', FORMAT(@added, 'N0'), N' added.');
    RAISERROR(@msg, 0, 1) WITH NOWAIT;

    SELECT @ImportId AS ImportId, @staged AS RowsStaged, @duplicate AS RowsDuplicate, @already AS RowsAlreadyLoaded,
           @added AS RowsAdded, @first AS EarliestAddedUtc, @last AS LatestAddedUtc,
           (SELECT RangeStartUtc FROM audit.CopilotBackfillRange WHERE Id = 1) AS RangeStartUtc,
           (SELECT RangeEndUtc FROM audit.CopilotBackfillRange WHERE Id = 1) AS RangeEndUtc,
           (SELECT Source FROM audit.CopilotBackfillRange WHERE Id = 1) AS RangeSource;
END;
GO
