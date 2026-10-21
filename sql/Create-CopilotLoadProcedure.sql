/*
    audit.usp_MergeCopilotLoad
    ------------------------------------------------------------------------
    Appends an incremental load (Invoke-CopilotAuditBackfill.ps1 -Incremental)
    to the finalized tables, instead of rebuilding them.

    Finalize-CopilotAudit.sql builds audit.CopilotAudit and
    audit.CopilotInteraction from scratch, which is right once, for the
    backfill, and wrong every day after: it rebuilds everything and knows only
    the backfill's range. This procedure takes the one load that has finished
    downloading and:

      1. Refuses unless every window of the load is Loaded and together they
         cover its range with no gap - the same checks finalize makes.
      2. Keeps each window's final attempt only, one row per RecordId.
      3. Drops the records audit.CopilotAudit already holds. Every load
         re-searches a lookback before its start, because Purview can deliver a
         record hours after the event; the records that were already there are
         counted, not added again.
      4. Appends the rest to audit.CopilotAudit (with LoadId for lineage) and
         their shredded rows to audit.CopilotInteraction, and marks the load
         merged, with how many audit searches it took - all in one transaction.
      5. Empties the load's window and staging tables for the next load.

    Re-runnable: with nothing waiting it reports so and returns. It never
    touches rows from the backfill or from earlier loads.

    One merge at a time: steps 1 to 4 hold an exclusive lock that
    audit.usp_MergeCopilotCsv takes too, so two merges never both find a
    record missing and both append it.

    Step 4 shreds through audit.fnShredCopilotInteraction, as finalize and CSV
    imports do, so a record reads the same whichever path loaded it.

    Requires SQL Server 2017+. Run sql/Create-CopilotAuditSchema.sql first (the
    orchestrator does), and finalize the backfill before the first load.
*/
SET NOCOUNT ON;

/* When the derived tables were brought up to date with a merged load (2.4.12).
   A load is marked merged before -Incremental refreshes the flattened table,
   sessions, bi and the dashboard from its start, and nothing recorded whether
   that refresh finished: the next load refreshed from its own start only, so
   after a few nights of a refresh failing, or one long catch-up load failing,
   the days between were never refreshed. NULL on a merged load is a refresh
   still owed, and -Incremental makes it up from the earliest one. Loads merged
   before the column existed are taken as refreshed. */
IF OBJECT_ID(N'audit.CopilotLoad', N'U') IS NOT NULL AND COL_LENGTH(N'audit.CopilotLoad', N'DerivedRefreshedUtc') IS NULL
BEGIN
    ALTER TABLE audit.CopilotLoad ADD DerivedRefreshedUtc DATETIME2(0) NULL;
    EXEC (N'UPDATE audit.CopilotLoad SET DerivedRefreshedUtc = MergedUtc WHERE MergedUtc IS NOT NULL;');
END;

/* How many audit searches a load took, and how many of them Microsoft Purview had to be asked for more than once
   (after 2.5.4). A load's windows are emptied once it is merged, so nothing was left to say what a night that
   finished had asked of the service: Admin > Health read "0 submitted" after every run that succeeded. NULL on a
   load merged before the columns existed. */
IF OBJECT_ID(N'audit.CopilotLoad', N'U') IS NOT NULL AND COL_LENGTH(N'audit.CopilotLoad', N'Searches') IS NULL
    ALTER TABLE audit.CopilotLoad ADD Searches INT NULL, SearchesResubmitted INT NULL;

IF OBJECT_ID(N'audit.usp_MergeCopilotLoad', N'P') IS NULL
    EXEC (N'CREATE PROCEDURE audit.usp_MergeCopilotLoad AS BEGIN SET NOCOUNT ON; END;');
GO

ALTER PROCEDURE audit.usp_MergeCopilotLoad
    @ReportingTimeZone SYSNAME = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    SET @ReportingTimeZone = COALESCE(@ReportingTimeZone, audit.ReportingTimeZone());   -- the zone Install-CopilotAudit.ps1 -ReportingTimeZone stored

    DECLARE @msg NVARCHAR(600), @loadId INT, @start DATETIME2(0), @end DATETIME2(0), @downloaded DATETIME2(0), @lock INT;

    /* One merge into audit.CopilotAudit at a time, this one or a CSV import's
       (2.4.12). Each looked for the records already there and then appended
       the rest, with no lock between: two at once, a console run beside the
       nightly or an import beside it, each missed what the other was
       appending and stored it a second time, and every flatten over those
       days then failed on the duplicate. The lock belongs to the transaction
       and is taken before the load is chosen, so a second merge waits, then
       finds the load merged or sees the first one's records. */
    BEGIN TRANSACTION;
    EXEC @lock = sys.sp_getapplock @Resource = N'audit.CopilotAudit:append', @LockMode = N'Exclusive',
                                   @LockOwner = N'Transaction', @LockTimeout = -1;
    IF @lock < 0
    BEGIN
        ROLLBACK TRANSACTION;
        RAISERROR('Merge refused: the merge lock on audit.CopilotAudit was not granted (%d). Run the same -Incremental command again.', 16, 1, @lock);
        RETURN;
    END;

    SELECT TOP (1) @loadId = LoadId, @start = RangeStartUtc, @end = RangeEndUtc, @downloaded = DownloadedUtc
    FROM audit.CopilotLoad
    WHERE MergedUtc IS NULL
    ORDER BY LoadId;

    IF @loadId IS NULL
    BEGIN
        ROLLBACK TRANSACTION;
        RAISERROR('Nothing to merge: no incremental load is waiting.', 0, 1) WITH NOWAIT;
        RETURN;
    END;
    IF @downloaded IS NULL
    BEGIN
        ROLLBACK TRANSACTION;
        RAISERROR('Merge refused: load %d has not finished downloading. Re-run the same -Incremental command to finish it.', 16, 1, @loadId);
        RETURN;
    END;
    IF OBJECT_ID(N'audit.CopilotAudit', N'U') IS NULL OR OBJECT_ID(N'audit.CopilotInteraction', N'U') IS NULL
    BEGIN
        ROLLBACK TRANSACTION;
        RAISERROR('Merge refused: audit.CopilotAudit does not exist yet. Finalize the backfill (-FinalizeOnly) before the first incremental load.', 16, 1);
        RETURN;
    END;
    IF OBJECT_ID(N'audit.fnShredCopilotInteraction', N'IF') IS NULL
    BEGIN
        ROLLBACK TRANSACTION;
        RAISERROR('Merge refused: audit.fnShredCopilotInteraction is missing. Run sql/Create-CopilotAuditSchema.sql first (Invoke-CopilotAuditBackfill.ps1 does).', 16, 1);
        RETURN;
    END;

    /* ---- 1. Coverage: the same checks finalize makes ---------------------- */

    DECLARE @unfinished INT = (SELECT COUNT(*) FROM audit.CopilotLoadWindow WHERE Status NOT IN ('Loaded', 'Split'));
    IF @unfinished > 0
    BEGIN
        ROLLBACK TRANSACTION;
        RAISERROR('Merge refused: %d window(s) of load %d are not Loaded.', 16, 1, @unfinished, @loadId);
        RETURN;
    END;

    DECLARE @gaps INT;
    WITH w AS
    (
        SELECT StartUtc, LAG(EndUtc) OVER (ORDER BY StartUtc) AS PrevEnd
        FROM audit.CopilotLoadWindow
        WHERE Status = 'Loaded'
    )
    SELECT @gaps = COUNT(*) FROM w WHERE PrevEnd IS NOT NULL AND PrevEnd <> StartUtc;

    IF ISNULL((SELECT MIN(StartUtc) FROM audit.CopilotLoadWindow WHERE Status = 'Loaded'), '19000101') <> @start
       OR ISNULL((SELECT MAX(EndUtc) FROM audit.CopilotLoadWindow WHERE Status = 'Loaded'), '19000101') <> @end
        SET @gaps += 1;

    IF @gaps > 0
    BEGIN
        ROLLBACK TRANSACTION;
        RAISERROR('Merge refused: the Loaded windows of load %d have %d gap(s)/overlap(s). Inspect audit.CopilotLoadWindow.', 16, 1, @loadId, @gaps);
        RETURN;
    END;

    /* Lineage for rows appended by a load. Finalize creates the column; a
       database finalized by an earlier version gets it here. */
    IF COL_LENGTH(N'audit.CopilotAudit', N'LoadId') IS NULL
        EXEC (N'ALTER TABLE audit.CopilotAudit ADD LoadId INT NULL;');

    /* ---- 2. The load's records: final attempt, one per RecordId ----------- */

    DROP TABLE IF EXISTS #cand;
    SELECT s.RecordId, s.CreatedDateTime, s.RecordType, s.Operation, s.OrganizationId, s.UserType, s.UserId,
           s.UserPrincipalName, s.Service, s.ObjectId, s.ClientIp, s.AdministrativeUnits, s.AuditDataGz, s.WindowId,
           ROW_NUMBER() OVER (PARTITION BY s.RecordId ORDER BY s.WindowId DESC) AS rn
    INTO #cand
    FROM audit.CopilotLoadStage AS s
    JOIN audit.CopilotLoadWindow AS w
      ON  w.WindowId = s.WindowId
      AND w.Status   = 'Loaded'
      AND w.Attempts = s.Attempt;

    DELETE FROM #cand WHERE rn > 1;             -- a record on a window boundary comes back from both
    DECLARE @staged BIGINT = (SELECT COUNT_BIG(*) FROM #cand);

    /* ---- 3. Skip what is already there ------------------------------------ */
    /*
        A search returns records by creation time, so a record in this load can
        only already be in audit.CopilotAudit with a CreatedDateTime inside the
        load's range. Bounding the probe by it (with a day's margin) lets the
        columnstore skip every row group outside it.
    */
    CREATE CLUSTERED INDEX CIX_cand ON #cand (RecordId);

    DELETE c
    FROM #cand AS c
    WHERE EXISTS (SELECT 1 FROM audit.CopilotAudit AS a
                  WHERE a.RecordId = c.RecordId
                    AND a.CreatedDateTime >= DATEADD(DAY, -1, @start)
                    AND a.CreatedDateTime <  DATEADD(DAY,  1, @end));

    DECLARE @added BIGINT = (SELECT COUNT_BIG(*) FROM #cand);

    /* ---- 4. Append, in the same transaction -------------------------------- */

    INSERT INTO audit.CopilotAudit
        (RecordId, CreatedDateTime, RecordType, Operation, OrganizationId, UserType, UserId,
         UserPrincipalName, Service, ObjectId, ClientIp, AdministrativeUnits, AuditDataGz, WindowId, LoadId)
    SELECT c.RecordId, c.CreatedDateTime, c.RecordType, c.Operation, c.OrganizationId, c.UserType, c.UserId,
           c.UserPrincipalName, c.Service, c.ObjectId, c.ClientIp, c.AdministrativeUnits, c.AuditDataGz, c.WindowId, @loadId
    FROM #cand AS c;

    /* 2.7.3: not in an activity database (it has audit.usp_ShredActivity): its records are shredded into its own
       tables, and nothing there reads audit.CopilotInteraction, so the JSON was parsed a second time for nothing. */
    IF OBJECT_ID(N'audit.usp_ShredActivity', N'P') IS NULL
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

    /* The searches the load took, kept with it: its windows are emptied below. One search per window, a window that
       was split included; resubmitted, where the service had to be asked for a window more than once. */
    DECLARE @searches INT, @resubmitted INT;
    SELECT @searches = COUNT(CASE WHEN Submissions > 0 THEN 1 END), @resubmitted = COUNT(CASE WHEN Submissions > 1 THEN 1 END)
    FROM audit.CopilotLoadWindow;

    UPDATE audit.CopilotLoad
    SET MergedUtc = SYSUTCDATETIME(), RowsStaged = @staged, RowsAdded = @added, RowsAlreadyLoaded = @staged - @added,
        Searches = @searches, SearchesResubmitted = @resubmitted
    WHERE LoadId = @loadId AND MergedUtc IS NULL;
    IF @@ROWCOUNT = 0   -- cannot happen under the lock; if it ever does, nothing is appended twice
        THROW 50120, N'Merge refused: the load was merged by another run meanwhile.', 1;

    COMMIT TRANSACTION;

    /* ---- 5. Ready for the next load ---------------------------------------- */

    TRUNCATE TABLE audit.CopilotLoadStage;
    TRUNCATE TABLE audit.CopilotLoadWindow;

    SET @msg = CONCAT(N'Load ', @loadId, N' merged: ', FORMAT(@staged, 'N0'), N' record(s) in ',
                      CONVERT(NVARCHAR(16), @start, 120), N'Z -> ', CONVERT(NVARCHAR(16), @end, 120), N'Z, ',
                      FORMAT(@added, 'N0'), N' new, ', FORMAT(@staged - @added, 'N0'), N' already loaded.');
    RAISERROR(@msg, 0, 1) WITH NOWAIT;

    SELECT @loadId AS LoadId, @start AS RangeStartUtc, @end AS RangeEndUtc,
           @staged AS RowsStaged, @added AS RowsAdded, @staged - @added AS RowsAlreadyLoaded;
END;
GO
