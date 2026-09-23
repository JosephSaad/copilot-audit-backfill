/*
    Copilot audit backfill - audit.usp_PurgeCopilotAuditJson
    ------------------------------------------------------------------------
    Retention for the bulky part of the archive: the compressed AuditData JSON.

    The JSON is what costs the disk (~1.5 KB per record; ~75 GB per 50M). The
    flattened and reporting tables are a fraction of that, so the usual policy
    is to drop the JSON past the retention horizon and keep the reporting rows
    indefinitely. @DeleteRows = 1 removes the records outright instead.

    Parameters:
      @RetainDays   required. Records with CreatedDateTime older than
                    SYSUTCDATETIME() - @RetainDays are in scope. Must be >= 1.
      @DeleteRows   0 (default) NULLs AuditDataGz and leaves the row, so
                    counts, the flattened table and reporting still work.
                    1 deletes the rows from audit.CopilotAudit entirely.
      @PurgeStaging 1 (default) also clears matching rows from
                    audit.CopilotAuditStage, which holds a second copy of the
                    same JSON until it is dropped.
      @PurgeFlattened 0 (default). 1 also deletes the matching
                    audit.RawLogFlattened rows - only with @DeleteRows = 1.
      @BatchSize    rows per transaction. Default 100,000.
      @WhatIf       1 (default) reports what would go and changes nothing.
                    Pass 0 to actually purge.

    @WhatIf defaults to 1 on purpose: this procedure destroys data that cannot
    be re-fetched once past Purview's own 180-day retention.

    Re-runnable. Requires SQL Server 2017+.
*/
SET NOCOUNT ON;

IF OBJECT_ID(N'audit.usp_PurgeCopilotAuditJson', N'P') IS NULL
    EXEC (N'CREATE PROCEDURE audit.usp_PurgeCopilotAuditJson AS BEGIN SET NOCOUNT ON; END;');
GO

ALTER PROCEDURE audit.usp_PurgeCopilotAuditJson
    @RetainDays     INT,
    @DeleteRows     BIT = 0,
    @PurgeStaging   BIT = 1,
    @PurgeFlattened BIT = 0,
    @BatchSize      INT = 100000,
    @WhatIf         BIT = 1
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF @RetainDays IS NULL OR @RetainDays < 1
    BEGIN
        RAISERROR('Purge refused: @RetainDays is required and must be at least 1.', 16, 1);
        RETURN;
    END;
    IF @BatchSize IS NULL OR @BatchSize < 1000 SET @BatchSize = 1000;
    IF @PurgeFlattened = 1 AND @DeleteRows = 0
    BEGIN
        RAISERROR('Purge refused: @PurgeFlattened = 1 needs @DeleteRows = 1, otherwise the flattened rows would be rebuilt from records that still exist.', 16, 1);
        RETURN;
    END;

    DECLARE @cutoff DATETIME2(7) = DATEADD(DAY, -@RetainDays, SYSUTCDATETIME());
    DECLARE @msg NVARCHAR(400), @n BIGINT, @total BIGINT = 0;

    SET @msg = CONCAT(N'Cutoff: records created before ', CONVERT(NVARCHAR(30), @cutoff, 126), N' UTC (',
                      @RetainDays, N' day retention).');
    RAISERROR(@msg, 0, 1) WITH NOWAIT;

    /* ---- What is in scope --------------------------------------------- */

    DECLARE @scope BIGINT =
        (SELECT COUNT_BIG(*) FROM audit.CopilotAudit
         WHERE CreatedDateTime < @cutoff AND (@DeleteRows = 1 OR AuditDataGz IS NOT NULL));
    DECLARE @keep BIGINT = (SELECT COUNT_BIG(*) FROM audit.CopilotAudit WHERE CreatedDateTime >= @cutoff);

    SET @msg = CONCAT(N'audit.CopilotAudit: ', FORMAT(@scope, 'N0'), N' record(s) in scope, ',
                      FORMAT(@keep, 'N0'), N' newer record(s) untouched.');
    RAISERROR(@msg, 0, 1) WITH NOWAIT;

    IF @WhatIf = 1
    BEGIN
        SELECT @cutoff                                                             AS CutoffUtc,
               @scope                                                              AS RecordsInScope,
               @keep                                                               AS RecordsRetained,
               CASE WHEN @DeleteRows = 1 THEN 'delete rows' ELSE 'NULL AuditDataGz' END AS Action,
               (SELECT MIN(CreatedDateTime) FROM audit.CopilotAudit)               AS OldestRecordUtc,
               (SELECT COUNT_BIG(*) FROM audit.CopilotAuditStage AS s
                WHERE s.CreatedDateTime < @cutoff)                                 AS StagingRowsInScope;
        RAISERROR('@WhatIf = 1: nothing was changed. Pass @WhatIf = 0 to purge.', 0, 1) WITH NOWAIT;
        RETURN;
    END;

    /* ---- audit.CopilotAudit -------------------------------------------- */

    IF @DeleteRows = 1
    BEGIN
        RAISERROR('Deleting records from audit.CopilotAudit...', 0, 1) WITH NOWAIT;
        WHILE 1 = 1
        BEGIN
            DELETE TOP (@BatchSize) FROM audit.CopilotAudit WHERE CreatedDateTime < @cutoff;
            SET @n = @@ROWCOUNT;
            SET @total += @n;
            IF @n = 0 BREAK;
            SET @msg = CONCAT(N'  ', FORMAT(@total, 'N0'), N' deleted.');
            RAISERROR(@msg, 0, 1) WITH NOWAIT;
        END;
    END
    ELSE
    BEGIN
        RAISERROR('Clearing AuditDataGz on aged records (rows are kept)...', 0, 1) WITH NOWAIT;
        WHILE 1 = 1
        BEGIN
            UPDATE TOP (@BatchSize) audit.CopilotAudit
               SET AuditDataGz = NULL
             WHERE CreatedDateTime < @cutoff AND AuditDataGz IS NOT NULL;
            SET @n = @@ROWCOUNT;
            SET @total += @n;
            IF @n = 0 BREAK;
            SET @msg = CONCAT(N'  ', FORMAT(@total, 'N0'), N' cleared.');
            RAISERROR(@msg, 0, 1) WITH NOWAIT;
        END;
    END;

    /*
        Both paths churn the clustered columnstore - a delete tombstones rows
        and an update is a delete plus an insert into the delta store. Squeeze
        the row groups back down so the space is actually returned.
    */
    IF @total > 0 AND OBJECT_ID(N'audit.CopilotAudit', N'U') IS NOT NULL
    BEGIN
        RAISERROR('Reorganizing the columnstore to reclaim space...', 0, 1) WITH NOWAIT;
        ALTER INDEX CCI_CopilotAudit ON audit.CopilotAudit REORGANIZE WITH (COMPRESS_ALL_ROW_GROUPS = ON);
    END;

    /* ---- staging: the second copy of the same JSON ---------------------- */

    IF @PurgeStaging = 1 AND OBJECT_ID(N'audit.CopilotAuditStage', N'U') IS NOT NULL
    BEGIN
        DECLARE @staged BIGINT = 0;
        RAISERROR('Deleting aged rows from audit.CopilotAuditStage...', 0, 1) WITH NOWAIT;
        WHILE 1 = 1
        BEGIN
            DELETE TOP (@BatchSize) FROM audit.CopilotAuditStage WHERE CreatedDateTime < @cutoff;
            SET @n = @@ROWCOUNT;
            SET @staged += @n;
            IF @n = 0 BREAK;
        END;
        SET @msg = CONCAT(N'  ', FORMAT(@staged, 'N0'), N' staging row(s) deleted.');
        RAISERROR(@msg, 0, 1) WITH NOWAIT;
    END;

    /* ---- flattened ------------------------------------------------------ */

    IF @PurgeFlattened = 1 AND OBJECT_ID(N'audit.RawLogFlattened', N'U') IS NOT NULL
    BEGIN
        DECLARE @flat BIGINT = 0;
        RAISERROR('Deleting aged rows from audit.RawLogFlattened...', 0, 1) WITH NOWAIT;
        WHILE 1 = 1
        BEGIN
            DELETE TOP (@BatchSize) FROM audit.RawLogFlattened WHERE UtcTimeStamp < @cutoff;
            SET @n = @@ROWCOUNT;
            SET @flat += @n;
            IF @n = 0 BREAK;
        END;
        SET @msg = CONCAT(N'  ', FORMAT(@flat, 'N0'), N' flattened row(s) deleted.');
        RAISERROR(@msg, 0, 1) WITH NOWAIT;
    END;

    SET @msg = CONCAT(N'Purge complete: ',
                      FORMAT(@total, 'N0'),
                      CASE WHEN @DeleteRows = 1 THEN N' record(s) deleted.' ELSE N' record(s) had their JSON cleared.' END);
    RAISERROR(@msg, 0, 1) WITH NOWAIT;

    SELECT @cutoff                                               AS CutoffUtc,
           @total                                                AS RecordsPurged,
           (SELECT COUNT_BIG(*) FROM audit.CopilotAudit)         AS RecordsRemaining,
           (SELECT COUNT_BIG(*) FROM audit.CopilotAudit
             WHERE AuditDataGz IS NOT NULL)                      AS RecordsStillHoldingJson;
END;
GO
