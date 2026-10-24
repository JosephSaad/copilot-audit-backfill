/*
    Copilot audit backfill - finalize
    ------------------------------------------------------------------------
    1. Refuses to run unless every window is Loaded and the range has no gaps.
    2. Builds audit.CopilotAudit (clustered columnstore) from each Loaded
       window's FINAL attempt only - rows from interrupted attempts are ignored.
    3. Removes duplicates by RecordId (records sitting exactly on a window
       boundary can be returned by both neighbouring searches).
    4. Builds audit.CopilotInteraction: a slim, shredded table for adoption
       reporting (Power BI etc.), plus audit.vCopilotDailyUsage.
    5. Creates audit.vCopilotAuditRecord in the Purview export's shape
       (RecordId, CreationDate, RecordType, Operation, UserId, AuditData,
       AssociatedAdminUnits, AssociatedAdminUnitsNames) so SQL written against
       the portal CSV import can point at it unchanged.
       AssociatedAdminUnitsNames is always NULL: the Graph auditLogRecord
       resource exposes one administrativeUnits collection, of IDs only, with
       no display names. The portal export resolves those IDs itself. The
       column is present so the shape matches; resolve the names from Entra
       if you need them.

    ActivityDate uses @ReportingTimeZone (below) for day boundaries; all
    other timestamps stay UTC.

    Re-runnable: both tables are rebuilt from staging every time, as long as
    staging still holds every row the windows loaded. Once it has been dropped
    (as the end of this script suggests) or purged, finalize refuses: it would
    rebuild audit.CopilotAudit from what is left, which is nothing.
    Single batch (no GO), so an error stops everything in SSMS too.
    Requires SQL Server 2017+ (NVARCHAR(MAX) in a columnstore).
    Expect tens of minutes for tens of millions of rows; it is a one-off.
*/
SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @ReportingTimeZone SYSNAME = N'Eastern Standard Time';   -- Windows zone name; handles EST/EDT
-- The zone Install-CopilotAudit.ps1 -ReportingTimeZone stored, when there is one.
IF OBJECT_ID(N'audit.ReportingTimeZone', N'FN') IS NOT NULL
    EXEC sys.sp_executesql N'SET @z = audit.ReportingTimeZone();', N'@z SYSNAME OUTPUT', @z = @ReportingTimeZone OUTPUT;
DECLARE @msg NVARCHAR(400);

/* ---- 1. Guard ---------------------------------------------------------- */

IF (SELECT compatibility_level FROM sys.databases WHERE name = DB_NAME()) < 130
BEGIN
    RAISERROR('Finalize needs database compatibility level 130+ (for OPENJSON). ALTER DATABASE ... SET COMPATIBILITY_LEVEL = 130 or higher.', 16, 1);
    RETURN;
END;

DECLARE @rangeStart DATETIME2(0), @rangeEnd DATETIME2(0), @completed DATETIME2(0);
SELECT @rangeStart = RangeStartUtc, @rangeEnd = RangeEndUtc, @completed = CompletedUtc
FROM audit.CopilotBackfillRange WHERE Id = 1;

IF @rangeStart IS NULL
BEGIN
    RAISERROR('Finalize refused: no backfill has been started in this database.', 16, 1);
    RETURN;
END;
IF @completed IS NULL
BEGIN
    RAISERROR('Finalize refused: the backfill has not completed. Re-run Invoke-CopilotAuditBackfill.ps1 to finish it.', 16, 1);
    RETURN;
END;

/* Finalize rebuilds from the backfill's staging table only. Once incremental
   loads have been appended, a rebuild would silently drop every day they added. */
IF OBJECT_ID(N'audit.CopilotLoad', N'U') IS NOT NULL
   AND EXISTS (SELECT 1 FROM audit.CopilotLoad WHERE MergedUtc IS NOT NULL)
BEGIN
    RAISERROR('Finalize refused: incremental loads have been merged into audit.CopilotAudit, and rebuilding from the backfill''s staging table would drop them. The tables are already final.', 16, 1);
    RETURN;
END;

/* The same for CSV imports (tools/Import-CopilotAuditCsv.ps1). Nested, not
   ANDed: those tables exist only where the importer has run, and a statement
   naming a missing table fails when it runs, whatever its other conditions. */
IF OBJECT_ID(N'audit.CopilotCsvImport', N'U') IS NOT NULL
BEGIN
    IF EXISTS (SELECT 1 FROM audit.CopilotCsvImport WHERE MergedUtc IS NOT NULL)
    BEGIN
        RAISERROR('Finalize refused: CSV imports have been merged into audit.CopilotAudit, and rebuilding from the backfill''s staging table would drop them. The tables are already final.', 16, 1);
        RETURN;
    END;
END;

IF OBJECT_ID(N'audit.fnShredCopilotInteraction', N'IF') IS NULL
BEGIN
    RAISERROR('Finalize refused: audit.fnShredCopilotInteraction is missing. Run sql/Create-CopilotAuditSchema.sql first (Invoke-CopilotAuditBackfill.ps1 does).', 16, 1);
    RETURN;
END;

DECLARE @unfinished INT =
    (SELECT COUNT(*) FROM audit.CopilotBackfillWindow WHERE Status NOT IN ('Loaded', 'Split'));
IF @unfinished > 0
BEGIN
    RAISERROR('Finalize refused: %d window(s) are not Loaded. Re-run the backfill to finish them first.', 16, 1, @unfinished);
    RETURN;
END;

DECLARE @gaps INT;
WITH w AS
(
    SELECT StartUtc, LAG(EndUtc) OVER (ORDER BY StartUtc) AS PrevEnd
    FROM audit.CopilotBackfillWindow
    WHERE Status = 'Loaded'
)
SELECT @gaps = COUNT(*) FROM w WHERE PrevEnd IS NOT NULL AND PrevEnd <> StartUtc;

-- ...and the loaded windows must reach both ends of the requested range.
IF ISNULL((SELECT MIN(StartUtc) FROM audit.CopilotBackfillWindow WHERE Status = 'Loaded'), '19000101') <> @rangeStart
   OR ISNULL((SELECT MAX(EndUtc) FROM audit.CopilotBackfillWindow WHERE Status = 'Loaded'), '19000101') <> @rangeEnd
    SET @gaps += 1;

IF @gaps > 0
BEGIN
    RAISERROR('Finalize refused: the Loaded windows have %d gap(s)/overlap(s). Inspect audit.CopilotBackfillWindow.', 16, 1, @gaps);
    RETURN;
END;

RAISERROR('Coverage verified: all windows loaded, no gaps.', 0, 1) WITH NOWAIT;

/* Staging must still hold what the windows loaded. The guards above read the
   window table only, and staging is dropped once finalize has run (the advice at the
   end of this script) or purged by -Task PurgeJson; the backfill creates it again,
   empty, on its next run. A finalize then dropped audit.CopilotAudit and rebuilt it
   from nothing, and said "Coverage verified". */
DECLARE @expected BIGINT, @unknown INT, @staged BIGINT, @existing BIGINT;
SELECT @expected = SUM(ISNULL(RowsLoaded, 0)), @unknown = COUNT(CASE WHEN RowsLoaded IS NULL THEN 1 END)
FROM audit.CopilotBackfillWindow WHERE Status = 'Loaded';
SELECT @staged = COUNT_BIG(*)
FROM audit.CopilotAuditStage AS s
JOIN audit.CopilotBackfillWindow AS w ON w.WindowId = s.WindowId AND w.Status = 'Loaded' AND w.Attempts = s.Attempt;
/* Counted, not read from sys.partitions: after a reorganize, a columnstore's row groups that were compressed are
   counted there twice until the tuple mover clears them. Dynamic, for a database where the table is not there yet. */
SET @existing = 0;
IF OBJECT_ID(N'audit.CopilotAudit', N'U') IS NOT NULL
    EXEC sys.sp_executesql N'SELECT @n = COUNT_BIG(*) FROM audit.CopilotAudit;', N'@n BIGINT OUTPUT', @n = @existing OUTPUT;

IF (@unknown = 0 AND @staged < @expected) OR (@staged = 0 AND @expected > 0) OR @existing > @staged
BEGIN
    RAISERROR('Finalize refused: the staging table holds %I64d of the %I64d rows the windows loaded, and audit.CopilotAudit holds %I64d. Staging was dropped or purged after an earlier finalize, and rebuilding from it would empty audit.CopilotAudit. The tables are final already: nothing needs doing.', 16, 1, @staged, @expected, @existing);
    RETURN;
END;

/* ---- 2. Final attempt of every Loaded window -> columnstore ------------ */

IF OBJECT_ID(N'audit.CopilotInteraction', N'U') IS NOT NULL DROP TABLE audit.CopilotInteraction;
IF OBJECT_ID(N'audit.CopilotAudit', N'U') IS NOT NULL DROP TABLE audit.CopilotAudit;

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
    AuditDataGz         VARBINARY(MAX)  NULL,       -- CAST(DECOMPRESS(AuditDataGz) AS NVARCHAR(MAX)); or use audit.vCopilotAuditRecord
    WindowId            BIGINT          NOT NULL,   -- lineage back to the control table
    LoadId              INT             NULL,       -- NULL: the backfill. Otherwise audit.CopilotLoad, and
                                                    -- WindowId is then a row of audit.CopilotLoadWindow
    CsvImportId         INT             NULL,       -- set: the row came from a CSV import (audit.CopilotCsvImport),
                                                    -- and WindowId is then its audit.CopilotCsvFile
    INDEX CCI_CopilotAudit CLUSTERED COLUMNSTORE
);

RAISERROR('Loading audit.CopilotAudit from staging (final attempts only)...', 0, 1) WITH NOWAIT;

INSERT INTO audit.CopilotAudit WITH (TABLOCK)
(
    RecordId, CreatedDateTime, RecordType, Operation, OrganizationId, UserType, UserId,
    UserPrincipalName, Service, ObjectId, ClientIp, AdministrativeUnits, AuditDataGz, WindowId
)
SELECT s.RecordId, s.CreatedDateTime, s.RecordType, s.Operation, s.OrganizationId, s.UserType, s.UserId,
       s.UserPrincipalName, s.Service, s.ObjectId, s.ClientIp, s.AdministrativeUnits, s.AuditDataGz, s.WindowId
FROM audit.CopilotAuditStage AS s
JOIN audit.CopilotBackfillWindow AS w
  ON  w.WindowId = s.WindowId
  AND w.Status   = 'Loaded'
  AND w.Attempts = s.Attempt;

DECLARE @loaded BIGINT = @@ROWCOUNT;
SET @msg = CONCAT(N'Loaded ', FORMAT(@loaded, 'N0'), N' rows.');
RAISERROR(@msg, 0, 1) WITH NOWAIT;

/* ---- 3. Boundary duplicates (the columnstore reads only RecordId) ------- */

WITH d AS
(
    SELECT ROW_NUMBER() OVER (PARTITION BY RecordId ORDER BY WindowId DESC) AS rn
    FROM audit.CopilotAudit
    WHERE RecordId IN (SELECT RecordId FROM audit.CopilotAudit GROUP BY RecordId HAVING COUNT(*) > 1)
)
DELETE FROM d WHERE rn > 1;

DECLARE @dupes BIGINT = @@ROWCOUNT;
SET @msg = CONCAT(N'Removed ', FORMAT(@dupes, 'N0'), N' duplicate row(s).');
RAISERROR(@msg, 0, 1) WITH NOWAIT;

ALTER INDEX CCI_CopilotAudit ON audit.CopilotAudit REORGANIZE WITH (COMPRESS_ALL_ROW_GROUPS = ON);

/* ---- 4. Reporting table -------------------------------------------------- */

CREATE TABLE audit.CopilotInteraction
(
    RecordId              VARCHAR(64)    NOT NULL,
    CreatedDateTime       DATETIME2(0)   NOT NULL,   -- UTC
    ActivityDate          DATE           NOT NULL,   -- calendar day in @ReportingTimeZone
    UserPrincipalName     NVARCHAR(512)  NULL,
    AppHost               NVARCHAR(100)  NULL,   -- Word, Teams, BizChat, Outlook ...
    AppIdentity           NVARCHAR(200)  NULL,   -- e.g. Copilot.MicrosoftCopilot.BizChat, Copilot.Studio.<agent>
    ClientRegion          NVARCHAR(20)   NULL,
    ModelName             NVARCHAR(200)  NULL,
    ModelProvider         NVARCHAR(200)  NULL,
    ThreadId              NVARCHAR(400)  NULL,
    PromptCount           INT            NULL,
    AccessedResourceCount INT            NULL,
    UsedWebSearch         BIT            NULL,
    INDEX CCI_CopilotInteraction CLUSTERED COLUMNSTORE
);

/* An activity database (it has audit.usp_ShredActivity) shreds its records into its own tables, and nothing
   there reads audit.CopilotInteraction: the table is left empty, and the hours a large history spent parsing every
   record's JSON for it are saved. */
DECLARE @activity BIT = CASE WHEN OBJECT_ID(N'audit.usp_ShredActivity', N'P') IS NULL THEN 0 ELSE 1 END;
IF @activity = 1
    RAISERROR('Activity database: audit.CopilotInteraction is not built here; the activity shred follows.', 0, 1) WITH NOWAIT;
ELSE
    RAISERROR('Shredding AuditData into audit.CopilotInteraction...', 0, 1) WITH NOWAIT;

/* audit.fnShredCopilotInteraction (Create-CopilotAuditSchema.sql) holds the
   JSON paths; incremental loads and CSV imports shred through it too. */

IF @activity = 0
INSERT INTO audit.CopilotInteraction WITH (TABLOCK)
(
    RecordId, CreatedDateTime, ActivityDate, UserPrincipalName, AppHost, AppIdentity, ClientRegion,
    ModelName, ModelProvider, ThreadId, PromptCount, AccessedResourceCount, UsedWebSearch
)
SELECT
    a.RecordId,
    CAST(a.CreatedDateTime AS DATETIME2(0)),
    CAST((a.CreatedDateTime AT TIME ZONE 'UTC') AT TIME ZONE @ReportingTimeZone AS DATE),
    COALESCE(a.UserPrincipalName, s.JsonUserId),
    s.AppHost, s.AppIdentity, s.ClientRegion, s.ModelName, s.ModelProvider, s.ThreadId,
    s.PromptCount, s.AccessedResourceCount, s.UsedWebSearch
FROM audit.CopilotAudit AS a
CROSS APPLY audit.fnShredCopilotInteraction(a.AuditDataGz) AS s;

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

/* ---- 5. Purview-export-shaped view ------------------------------------------ */

EXEC (N'
CREATE OR ALTER VIEW audit.vCopilotAuditRecord
AS
SELECT RecordId,
       CreatedDateTime                                            AS CreationDate,          -- UTC
       UPPER(LEFT(RecordType, 1)) + SUBSTRING(RecordType, 2, 127) AS RecordType,            -- copilotInteraction -> CopilotInteraction
       Operation,
       COALESCE(UserPrincipalName, UserId)                        AS UserId,
       CAST(DECOMPRESS(AuditDataGz) AS NVARCHAR(MAX))             AS AuditData,
       AdministrativeUnits                                        AS AssociatedAdminUnits,  -- semicolon-separated IDs
       CAST(NULL AS NVARCHAR(MAX))                                AS AssociatedAdminUnitsNames
FROM audit.CopilotAudit;');

/* ---- Summary --------------------------------------------------------------- */

SELECT
    (SELECT COUNT_BIG(*) FROM audit.CopilotAudit)                        AS FinalRows,
    @dupes                                                               AS DuplicatesRemoved,
    (SELECT MIN(CreatedDateTime) FROM audit.CopilotAudit)                AS OldestRecordUtc,
    (SELECT MAX(CreatedDateTime) FROM audit.CopilotAudit)                AS NewestRecordUtc,
    CASE WHEN @activity = 1 THEN (SELECT COUNT(DISTINCT UserPrincipalName) FROM audit.CopilotAudit)
         ELSE (SELECT COUNT(DISTINCT UserPrincipalName) FROM audit.CopilotInteraction) END AS DistinctUsers,
    (SELECT COUNT_BIG(*) FROM audit.CopilotAuditStage)                   AS StagingRows;

/* The mark the backfill reads before finalizing again: finalized, so a re-run leaves the tables alone. */
MERGE audit.PipelineSetting AS t
USING (SELECT N'FinalizedUtc' AS Name, CONVERT(NVARCHAR(30), SYSUTCDATETIME(), 126) AS Value) AS s ON t.Name = s.Name
WHEN MATCHED THEN UPDATE SET Value = s.Value, UpdatedUtc = SYSUTCDATETIME(), UpdatedBy = SUSER_SNAME()
WHEN NOT MATCHED THEN INSERT (Name, Value) VALUES (s.Name, s.Value);

RAISERROR('Finalize complete. Once verified, reclaim space with: DROP TABLE audit.CopilotAuditStage;', 0, 1) WITH NOWAIT;
