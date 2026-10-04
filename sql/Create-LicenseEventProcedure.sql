/*
    audit.usp_ShredLicenseEvents
    ------------------------------------------------------------------------
    Turns the AzureActiveDirectory records in audit.CopilotAudit into
    audit.LicenseEvent and audit.LicenseEventProperty.

    Run it in the database the license-operations backfill loaded - a
    different database from the Copilot one, because the backfill refuses to
    mix operation sets in a single database.

    Parses each document once into a temp table, the same way
    audit.usp_FlattenCopilotAudit does and for the same reason: a JSON_VALUE
    or OPENJSON call parses the whole document, so reading a dozen fields with
    a dozen calls parses each record a dozen times.

    Re-runnable: rows are keyed on RecordId and MERGEd in place.

    Requires SQL Server 2017+. Run sql/Create-LicenseEventSchema.sql first.
*/
SET NOCOUNT ON;

/*
    audit.usp_RefreshLicenseSkuChange
    ------------------------------------------------------------------------
    Keeps audit.LicenseSkuChange: the SKUs each license change added and
    removed, as audit.vLicenseSkuChangeFromRecords reads them from the records,
    worked out a set at a time and stored. The session build and the bi refresh
    read every one of them every night. Read from the records, over 4,000,000
    license events in the lab, that took 11 minutes each time; worked out here,
    two minutes once, and seconds a night after.

      @All  0 (the default): the events shredded since this last ran, with
            every record of their chunk groups and every record raised with
            any of those (one CorrelationId). A change can take its license
            lists or its user from, or give way to, a record raised with it,
            so a family is always worked out whole.
            1: every event, the table replaced. Also what happens the first
            time, and when more than a tenth of the events were shredded since
            the last run: reading them all once is then the quicker way.

      @SinceShreddedUtc  The caller's own shred began then: its events are
            taken whatever ran since. Another session's run in between, made
            while this shred was still reassembling its chunk groups, would
            otherwise have moved "since this last ran" past them.

    audit.usp_ShredLicenseEvents runs it at the end of every shred, so nothing
    else needs to. "Since this last ran" is read from audit.LicenseSkuChangeRun
    and each event's ShreddedUtc, so a run that failed leaves nothing behind:
    the next one takes its events too. After changing audit.LicenseEvent or its
    properties any other way, run it with @All = 1. While a shred is running,
    or after one was stopped part way (its mark, LicenseShredInProgress, is in
    audit.PipelineSetting), a run asked for by hand works out nothing unless
    @All = 1: that shred's events can still lack their properties.

    The rule, step by step, is the view's:
      * Only a record that is not a later fragment of a chunk group, and not
        an 'Update user' raised with a 'Change user license'.
      * Its license lists are its own AssignedLicense properties when it has
        any license-bearing property; else those of the first record raised
        with it, outside its chunk group, that has one.
      * Its user is its own, else the first one a record raised with it has.
      * A SKU in the new list and not the old was added; in the old and not
        the new, removed.
*/
IF OBJECT_ID(N'audit.usp_RefreshLicenseSkuChange', N'P') IS NULL
    EXEC (N'CREATE PROCEDURE audit.usp_RefreshLicenseSkuChange AS BEGIN SET NOCOUNT ON; END;');
GO

ALTER PROCEDURE audit.usp_RefreshLicenseSkuChange
    @All              BIT          = 0,
    @SinceShreddedUtc DATETIME2(0) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @started DATETIME2(0) = SYSUTCDATETIME(), @t0 DATETIME2(3) = SYSUTCDATETIME(),
            @since DATETIME2(0), @events INT, @changes INT, @msg NVARCHAR(400);
    SET @All = ISNULL(@All, 0);

    /* A shred that is running, or was stopped part way, has events without their properties yet: worked out now,
       their changes would be taken away. The shred runs this itself once it has finished (it says when it began),
       and @All = 1 is taken at its word. Until then the changes stay as the last shred that finished left them. */
    IF @All = 0 AND @SinceShreddedUtc IS NULL
       AND EXISTS (SELECT 1 FROM audit.PipelineSetting WHERE Name = N'LicenseShredInProgress')
    BEGIN
        RAISERROR('License SKU changes: left as they are, because a license shred is running or did not finish. The shred that finishes it works them out.', 0, 1) WITH NOWAIT;
        RETURN;
    END;

    /* Shredded since the last run began, with five minutes to spare: an event stamped as that run started is taken
       again rather than missed. Or since the caller's shred began, when that is earlier. No run yet: every event. */
    SET @since = (SELECT DATEADD(MINUTE, -5, MAX(StartedUtc)) FROM audit.LicenseSkuChangeRun);
    IF @since IS NULL
        SET @All = 1;
    ELSE IF @SinceShreddedUtc < @since
        SET @since = @SinceShreddedUtc;

    /* More than a tenth of the history shredded since (a full shred, a first load): every event is read once instead,
       which is quicker than seeking each family. Both counts are cheap: an index range, and the table's own row count. */
    IF @All = 0
       AND (SELECT COUNT_BIG(*) FROM audit.LicenseEvent AS e WHERE e.ShreddedUtc >= @since) * 10
           > ISNULL((SELECT SUM(p.rows) FROM sys.partitions AS p
                     WHERE p.object_id = OBJECT_ID(N'audit.LicenseEvent') AND p.index_id IN (0, 1)), 0)
        SET @All = 1;

    /* ---- 1. The events to work out, whole families at a time ------------- */

    /* In the database's collation throughout, as the shred's temp tables are. Each has a name of its own (#sku...):
       this is called from other batches, and a statement here would be compiled against a caller's temp table of
       the same name (the demo's own #ev, the first time). */
    DROP TABLE IF EXISTS #skuEvent;
    CREATE TABLE #skuEvent
    (
        RecordId        VARCHAR(64)   COLLATE DATABASE_DEFAULT NOT NULL PRIMARY KEY,
        CreatedDateTime DATETIME2(0)  NOT NULL,
        ActivityDate    DATE          NOT NULL,
        Operation       NVARCHAR(200) COLLATE DATABASE_DEFAULT NULL,
        ActorUpn        NVARCHAR(512) COLLATE DATABASE_DEFAULT NULL,
        TargetUpn       NVARCHAR(512) COLLATE DATABASE_DEFAULT NULL,
        CorrelationId   NVARCHAR(200) COLLATE DATABASE_DEFAULT NULL,
        ChunkGroupId    NVARCHAR(100) COLLATE DATABASE_DEFAULT NULL,
        ChunkSeq        INT           NULL
    );

    IF @All = 1
        INSERT INTO #skuEvent WITH (TABLOCK)
            (RecordId, CreatedDateTime, ActivityDate, Operation, ActorUpn, TargetUpn, CorrelationId, ChunkGroupId, ChunkSeq)
        SELECT e.RecordId, e.CreatedDateTime, e.ActivityDate, e.Operation, e.ActorUpn, e.TargetUpn, e.CorrelationId, e.ChunkGroupId, e.ChunkSeq
        FROM audit.LicenseEvent AS e;
    ELSE
    BEGIN
        INSERT INTO #skuEvent (RecordId, CreatedDateTime, ActivityDate, Operation, ActorUpn, TargetUpn, CorrelationId, ChunkGroupId, ChunkSeq)
        SELECT e.RecordId, e.CreatedDateTime, e.ActivityDate, e.Operation, e.ActorUpn, e.TargetUpn, e.CorrelationId, e.ChunkGroupId, e.ChunkSeq
        FROM audit.LicenseEvent AS e WITH (FORCESEEK)
        WHERE e.ShreddedUtc >= @since;

        /* Every record of their chunk groups: a group's properties are filed against its first record, which an
           earlier run may have shredded. Sought by its index: the night's events are few against the history. */
        INSERT INTO #skuEvent (RecordId, CreatedDateTime, ActivityDate, Operation, ActorUpn, TargetUpn, CorrelationId, ChunkGroupId, ChunkSeq)
        SELECT m.RecordId, m.CreatedDateTime, m.ActivityDate, m.Operation, m.ActorUpn, m.TargetUpn, m.CorrelationId, m.ChunkGroupId, m.ChunkSeq
        FROM (SELECT DISTINCT e.ChunkGroupId FROM #skuEvent AS e WHERE e.ChunkGroupId IS NOT NULL) AS g
        JOIN audit.LicenseEvent AS m WITH (FORCESEEK) ON m.ChunkGroupId = g.ChunkGroupId
        WHERE NOT EXISTS (SELECT 1 FROM #skuEvent AS x WHERE x.RecordId = m.RecordId);

        /* Every record raised with any of those. */
        INSERT INTO #skuEvent (RecordId, CreatedDateTime, ActivityDate, Operation, ActorUpn, TargetUpn, CorrelationId, ChunkGroupId, ChunkSeq)
        SELECT m.RecordId, m.CreatedDateTime, m.ActivityDate, m.Operation, m.ActorUpn, m.TargetUpn, m.CorrelationId, m.ChunkGroupId, m.ChunkSeq
        FROM (SELECT DISTINCT e.CorrelationId FROM #skuEvent AS e WHERE e.CorrelationId IS NOT NULL) AS k
        JOIN audit.LicenseEvent AS m WITH (FORCESEEK) ON m.CorrelationId = k.CorrelationId
        WHERE NOT EXISTS (SELECT 1 FROM #skuEvent AS x WHERE x.RecordId = m.RecordId);
    END;
    SET @events = (SELECT COUNT(*) FROM #skuEvent);

    CREATE INDEX IX_ev_Correlation ON #skuEvent (CorrelationId) INCLUDE (Operation, ChunkSeq, ChunkGroupId, CreatedDateTime, TargetUpn);

    /* ---- 2. Their license-bearing properties ----------------------------- */

    /* Every one marks its record as having license detail of its own; only AssignedLicense carries the lists, so only its
       values are copied (AssignedPlan's run to tens of kilobytes a record). */
    DROP TABLE IF EXISTS #skuProperty;
    CREATE TABLE #skuProperty
    (
        RecordId   VARCHAR(64)   COLLATE DATABASE_DEFAULT NOT NULL,
        Ordinal    INT           NOT NULL,
        IsList     BIT           NOT NULL,
        FromChunks INT           NOT NULL,
        OldValue   NVARCHAR(MAX) COLLATE DATABASE_DEFAULT NULL,
        NewValue   NVARCHAR(MAX) COLLATE DATABASE_DEFAULT NULL
    );
    /* Two statements: every property read once in a scan, or the families' properties sought by their key (left to
       choose, the second scanned every property for a night's few thousand events: two minutes, not seconds). */
    IF @All = 1
        INSERT INTO #skuProperty WITH (TABLOCK) (RecordId, Ordinal, IsList, FromChunks, OldValue, NewValue)
        SELECT p.RecordId, p.Ordinal,
               CASE WHEN p.[Name] = N'AssignedLicense' THEN 1 ELSE 0 END,
               CASE WHEN p.Source = 'chunks' THEN 1 ELSE 0 END,
               CASE WHEN p.[Name] = N'AssignedLicense' THEN p.OldValue END,
               CASE WHEN p.[Name] = N'AssignedLicense' THEN p.NewValue END
        FROM audit.LicenseEventProperty AS p
        WHERE p.[Name] LIKE N'%License%' OR p.[Name] LIKE N'%Plan%' OR p.[Name] LIKE N'%Sku%';
    ELSE
        INSERT INTO #skuProperty (RecordId, Ordinal, IsList, FromChunks, OldValue, NewValue)
        SELECT p.RecordId, p.Ordinal,
               CASE WHEN p.[Name] = N'AssignedLicense' THEN 1 ELSE 0 END,
               CASE WHEN p.Source = 'chunks' THEN 1 ELSE 0 END,
               CASE WHEN p.[Name] = N'AssignedLicense' THEN p.OldValue END,
               CASE WHEN p.[Name] = N'AssignedLicense' THEN p.NewValue END
        FROM #skuEvent AS e
        JOIN audit.LicenseEventProperty AS p WITH (FORCESEEK) ON p.RecordId = e.RecordId
        WHERE p.[Name] LIKE N'%License%' OR p.[Name] LIKE N'%Plan%' OR p.[Name] LIKE N'%Sku%';

    CREATE CLUSTERED INDEX CIX_lp ON #skuProperty (RecordId, Ordinal);

    /* ---- 3. The records that are changes --------------------------------- */

    DROP TABLE IF EXISTS #skuCandidate;
    SELECT e.RecordId, e.CreatedDateTime, e.ActivityDate, e.ActorUpn, e.TargetUpn, e.CorrelationId, e.ChunkGroupId,
           CAST(CASE WHEN EXISTS (SELECT 1 FROM #skuProperty AS p WHERE p.RecordId = e.RecordId) THEN 1 ELSE 0 END AS BIT) AS HasOwn
    INTO #skuCandidate
    FROM #skuEvent AS e
    WHERE (e.ChunkSeq IS NULL OR e.ChunkSeq = 1)
      /* Entra raises one change as a 'Change user license' and an 'Update user': it is the license change. */
      AND NOT (e.Operation = N'Update user'
               AND e.CorrelationId IS NOT NULL
               AND EXISTS (SELECT 1 FROM #skuEvent AS x
                           WHERE x.CorrelationId = e.CorrelationId
                             AND x.Operation = N'Change user license'
                             AND (x.ChunkSeq IS NULL OR x.ChunkSeq = 1)));

    CREATE UNIQUE CLUSTERED INDEX CIX_cand ON #skuCandidate (RecordId);

    /* ---- 4. Each one's license lists -------------------------------------- */

    DROP TABLE IF EXISTS #skuList;
    CREATE TABLE #skuList
    (
        RecordId   VARCHAR(64)   COLLATE DATABASE_DEFAULT NOT NULL,
        FromChunks INT           NOT NULL,
        OldValue   NVARCHAR(MAX) COLLATE DATABASE_DEFAULT NULL,
        NewValue   NVARCHAR(MAX) COLLATE DATABASE_DEFAULT NULL
    );

    /* Its own. */
    INSERT INTO #skuList (RecordId, FromChunks, OldValue, NewValue)
    SELECT c.RecordId, p.FromChunks, p.OldValue, p.NewValue
    FROM #skuCandidate AS c
    JOIN #skuProperty AS p ON p.RecordId = c.RecordId AND p.IsList = 1
    WHERE c.HasOwn = 1;

    /* Or, with no license-bearing property of its own, those of the first record raised with it that has one: never one
       of its own chunk group, whose records all share the CorrelationId. */
    INSERT INTO #skuList (RecordId, FromChunks, OldValue, NewValue)
    SELECT d.RecordId, p.FromChunks, p.OldValue, p.NewValue
    FROM (SELECT c.RecordId, o.RecordId AS DonorId,
                 ROW_NUMBER() OVER (PARTITION BY c.RecordId ORDER BY o.CreatedDateTime, o.RecordId) AS rn
          FROM #skuCandidate AS c
          JOIN #skuEvent AS o ON o.CorrelationId = c.CorrelationId
                       AND o.RecordId <> c.RecordId
                       AND (c.ChunkGroupId IS NULL OR o.ChunkGroupId IS NULL OR o.ChunkGroupId <> c.ChunkGroupId)
          WHERE c.HasOwn = 0
            AND c.CorrelationId IS NOT NULL
            AND EXISTS (SELECT 1 FROM #skuProperty AS h WHERE h.RecordId = o.RecordId)) AS d
    JOIN #skuProperty AS p ON p.RecordId = d.DonorId AND p.IsList = 1
    WHERE d.rn = 1;

    CREATE CLUSTERED INDEX CIX_lists ON #skuList (RecordId);

    /* ---- 5. The SKUs on each side ----------------------------------------- */

    /* Each list entry reads [SkuName=Microsoft_365_Copilot, AccountId=..., SkuId=..., DisabledPlans=[...]]. */
    DROP TABLE IF EXISTS #skuSide;
    CREATE TABLE #skuSide
    (
        RecordId VARCHAR(64)   COLLATE DATABASE_DEFAULT NOT NULL,
        IsNew    BIT           NOT NULL,
        Sku      NVARCHAR(200) COLLATE DATABASE_DEFAULT NOT NULL
    );
    INSERT INTO #skuSide (RecordId, IsNew, Sku)
    SELECT l.RecordId, 0, LEFT(s.Sku, 200)
    FROM #skuList AS l
    CROSS APPLY OPENJSON(CASE WHEN ISJSON(l.OldValue) = 1 THEN l.OldValue ELSE N'[]' END) AS j
    CROSS APPLY (SELECT CHARINDEX(N'SkuName=', j.[value]) AS At) AS a
    CROSS APPLY (SELECT CASE WHEN a.At > 0 THEN SUBSTRING(j.[value], a.At + 8,
                     CHARINDEX(N',', j.[value] + N',', a.At + 8) - a.At - 8) END AS Sku) AS s
    WHERE s.Sku IS NOT NULL
    UNION
    SELECT l.RecordId, 1, LEFT(s.Sku, 200)
    FROM #skuList AS l
    CROSS APPLY OPENJSON(CASE WHEN ISJSON(l.NewValue) = 1 THEN l.NewValue ELSE N'[]' END) AS j
    CROSS APPLY (SELECT CHARINDEX(N'SkuName=', j.[value]) AS At) AS a
    CROSS APPLY (SELECT CASE WHEN a.At > 0 THEN SUBSTRING(j.[value], a.At + 8,
                     CHARINDEX(N',', j.[value] + N',', a.At + 8) - a.At - 8) END AS Sku) AS s
    WHERE s.Sku IS NOT NULL;

    CREATE UNIQUE CLUSTERED INDEX CIX_sku ON #skuSide (RecordId, IsNew, Sku);

    /* ---- 6. What changed, and for whom ------------------------------------ */

    /* The user a record raised with it carries, for a change that carries none: the first, as the view takes it. */
    DROP TABLE IF EXISTS #skuPaired;
    SELECT x.CorrelationId, x.TargetUpn
    INTO #skuPaired
    FROM (SELECT o.CorrelationId, o.TargetUpn,
                 ROW_NUMBER() OVER (PARTITION BY o.CorrelationId ORDER BY o.CreatedDateTime, o.RecordId) AS rn
          FROM #skuEvent AS o
          WHERE o.TargetUpn IS NOT NULL
            AND o.CorrelationId IN (SELECT c.CorrelationId FROM #skuCandidate AS c
                                    WHERE c.TargetUpn IS NULL AND c.CorrelationId IS NOT NULL)) AS x
    WHERE x.rn = 1;

    DROP TABLE IF EXISTS #skuOut;
    SELECT c.RecordId, d.Sku, d.Change, c.CreatedDateTime, c.ActivityDate, c.ActorUpn,
           COALESCE(c.TargetUpn, pr.TargetUpn) AS TargetUpn, f.FromChunks
    INTO #skuOut
    FROM #skuCandidate AS c
    JOIN (SELECT l.RecordId, MAX(l.FromChunks) AS FromChunks FROM #skuList AS l GROUP BY l.RecordId) AS f ON f.RecordId = c.RecordId
    LEFT JOIN #skuPaired AS pr ON pr.CorrelationId = c.CorrelationId
    CROSS APPLY (
        SELECT n.Sku, N'Added' AS Change
        FROM #skuSide AS n
        WHERE n.RecordId = c.RecordId AND n.IsNew = 1
          AND NOT EXISTS (SELECT 1 FROM #skuSide AS o WHERE o.RecordId = c.RecordId AND o.IsNew = 0 AND o.Sku = n.Sku)
        UNION ALL
        SELECT o.Sku, N'Removed'
        FROM #skuSide AS o
        WHERE o.RecordId = c.RecordId AND o.IsNew = 0
          AND NOT EXISTS (SELECT 1 FROM #skuSide AS n WHERE n.RecordId = c.RecordId AND n.IsNew = 1 AND n.Sku = o.Sku)
    ) AS d;

    /* ---- 7. Kept: the families' rows replaced in one transaction ---------- */

    BEGIN TRANSACTION;
    IF @All = 1 AND HAS_PERMS_BY_NAME(N'audit.LicenseSkuChange', N'OBJECT', N'ALTER') = 1
        TRUNCATE TABLE audit.LicenseSkuChange;
    ELSE IF @All = 1
        DELETE FROM audit.LicenseSkuChange;
    ELSE
        DELETE k
        FROM audit.LicenseSkuChange AS k
        JOIN #skuEvent AS e ON e.RecordId = k.RecordId;

    INSERT INTO audit.LicenseSkuChange (RecordId, Sku, Change, CreatedDateTime, ActivityDate, ActorUpn, TargetUpn, FromChunks)
    SELECT o.RecordId, o.Sku, o.Change, o.CreatedDateTime, o.ActivityDate, o.ActorUpn, o.TargetUpn, o.FromChunks
    FROM #skuOut AS o;
    SET @changes = @@ROWCOUNT;

    INSERT INTO audit.LicenseSkuChangeRun (StartedUtc, EndedUtc, SinceUtc, Events, Changes)
    VALUES (@started, SYSUTCDATETIME(), CASE WHEN @All = 1 THEN NULL ELSE @since END, @events, @changes);
    COMMIT TRANSACTION;

    SET @msg = CONCAT(N'License SKU changes: ', FORMAT(@changes, 'N0'), N' kept for ', FORMAT(@events, 'N0'),
                      CASE WHEN @All = 1 THEN N' event(s), every one' ELSE N' event(s) shredded since the last run or raised with one' END,
                      N', in ', FORMAT(DATEDIFF(MILLISECOND, @t0, SYSUTCDATETIME()) / 1000.0, 'N1'), N' s.');
    RAISERROR(@msg, 0, 1) WITH NOWAIT;
END;
GO

IF OBJECT_ID(N'audit.usp_ShredLicenseEvents', N'P') IS NULL
    EXEC (N'CREATE PROCEDURE audit.usp_ShredLicenseEvents AS BEGIN SET NOCOUNT ON; END;');
GO

ALTER PROCEDURE audit.usp_ShredLicenseEvents
    @SinceUtc       DATETIME2(0) = NULL,
    @RebuildAll     BIT          = 0,
    @BatchSize      INT          = 200000,
    @ReportingTimeZone SYSNAME   = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    SET @ReportingTimeZone = COALESCE(@ReportingTimeZone, audit.ReportingTimeZone());   -- the zone Install-CopilotAudit.ps1 -ReportingTimeZone stored

    IF @BatchSize IS NULL OR @BatchSize < 1000 SET @BatchSize = 1000;

    DECLARE @msg NVARCHAR(400), @rows BIGINT = 0, @done BIGINT = 0, @batch INT = 1;

    IF @RebuildAll = 1
        SET @SinceUtc = NULL;

    /* 2.5.4: one shred at a time. Two at once would each take the other's mark (below) for a stopped shred's, and
       the first to finish would take away the mark the other still needs. A second shred waits for the first to
       finish, as a second bi refresh does. The lock is the session's, as a shred commits as it goes: released at
       the end, on an error (the CATCH at the end), or when the session closes. */
    DECLARE @lock INT;
    EXEC @lock = sys.sp_getapplock @Resource = N'audit.usp_ShredLicenseEvents', @LockMode = 'Exclusive', @LockOwner = 'Session', @LockTimeout = 0;
    IF @lock < 0
    BEGIN
        RAISERROR('Another shred of the license events is running: waiting for it to finish.', 0, 1) WITH NOWAIT;
        EXEC @lock = sys.sp_getapplock @Resource = N'audit.usp_ShredLicenseEvents', @LockMode = 'Exclusive', @LockOwner = 'Session', @LockTimeout = -1;
    END;
    IF @lock < 0
        THROW 50020, N'Another shred of the license events is running, and this one could not wait for it to finish.', 1;

    BEGIN TRY
    /* Every event this run writes is stamped at or after this (ShreddedUtc): the SKU changes at the end are told. */
    DECLARE @shredFrom DATETIME2(0) = DATEADD(SECOND, -1, SYSUTCDATETIME());

    /*
        2.5.4: a shred says that it is running (LicenseShredInProgress in audit.PipelineSetting, with the time it
        shreds from; empty: every record) and takes the mark away once it has finished and the SKU changes are
        worked out. Its statements commit one by one, so one that is stopped part way - between a batch's events
        and their properties, or before the chunk groups are reassembled - leaves events without their properties,
        and leaves its mark. One that is still running holds the lock above, so a mark found here is always one of
        a shred that was stopped: this one, whatever it was asked for, shreds what that one was shredding too.
        Until then the SKU changes sessions and bi read are those of the last shred that finished.
    */
    DECLARE @owed NVARCHAR(400), @stopped BIT = 0;
    SELECT @owed = Value, @stopped = 1 FROM audit.PipelineSetting WHERE Name = N'LicenseShredInProgress';
    IF @stopped = 1
    BEGIN
        IF TRY_CONVERT(DATETIME2(0), NULLIF(@owed, N''), 126) IS NULL
            SET @SinceUtc = NULL;
        ELSE IF TRY_CONVERT(DATETIME2(0), @owed, 126) < @SinceUtc
            SET @SinceUtc = TRY_CONVERT(DATETIME2(0), @owed, 126);
        SET @msg = CONCAT(N'An earlier shred did not finish: shredding ',
                          CASE WHEN @SinceUtc IS NULL THEN N'every record' ELSE CONCAT(N'from ', CONVERT(NVARCHAR(16), @SinceUtc, 120), N'Z') END,
                          N', to finish it.');
        RAISERROR(@msg, 0, 1) WITH NOWAIT;
        /* Its time stays: when the shred that was stopped began, which is how long events have been left so. */
        UPDATE audit.PipelineSetting SET Value = ISNULL(CONVERT(NVARCHAR(30), @SinceUtc, 126), N'')
        WHERE Name = N'LicenseShredInProgress';
    END
    ELSE
        INSERT INTO audit.PipelineSetting (Name, Value) VALUES (N'LicenseShredInProgress', ISNULL(CONVERT(NVARCHAR(30), @SinceUtc, 126), N''));

    IF @RebuildAll = 1
    BEGIN
        RAISERROR('Rebuilding the license event tables from scratch.', 0, 1) WITH NOWAIT;
        TRUNCATE TABLE audit.LicenseEventProperty;
        TRUNCATE TABLE audit.LicenseEventChunk;
        TRUNCATE TABLE audit.LicenseEvent;
    END;

    /* Chunk groups this run saw, reassembled once every batch is in. In the database's collation (2.4.12): in
       tempdb's, a database whose collation differs from the server's failed every shred at the first comparison. */
    DROP TABLE IF EXISTS #touched;
    CREATE TABLE #touched (ChunkGroupId NVARCHAR(100) COLLATE DATABASE_DEFAULT NOT NULL PRIMARY KEY);

    DECLARE @cursor VARCHAR(64) = '', @maxId VARCHAR(64);

    WHILE 1 = 1
    BEGIN
        SET @maxId = NULL;

        ;WITH nextBatch AS
        (
            SELECT TOP (@BatchSize) a.RecordId
            FROM audit.CopilotAudit AS a
            WHERE a.RecordId > @cursor
              AND (@SinceUtc IS NULL OR a.CreatedDateTime >= @SinceUtc)
            ORDER BY a.RecordId
        )
        SELECT @maxId = MAX(RecordId) FROM nextBatch;

        IF @maxId IS NULL BREAK;

        DROP TABLE IF EXISTS #evt;

        /* One parse per record: scalars, plus the arrays as fragments. */
        SELECT
            a.RecordId,
            a.CreatedDateTime,
            CAST((a.CreatedDateTime AT TIME ZONE 'UTC') AT TIME ZONE @ReportingTimeZone AS DATE) AS ActivityDate,
            /* Operation is sometimes recorded with a trailing period
               ('Change user license.'), so it is trimmed here rather than
               every query having to allow for both. */
            LEFT(RTRIM(REPLACE(COALESCE(t.Operation, a.Operation), N'.', N'')), 200)  AS Operation,
            LEFT(t.ResultStatus, 50)                                                  AS ResultStatus,
            LEFT(COALESCE(t.JsonUserId, a.UserPrincipalName, a.UserId), 512)          AS ActorUpn,
            LEFT(t.ObjectId, 512)                                                     AS TargetUpnRaw,
            /* Records raised for one change share InterSystemsId. */
            LEFT(COALESCE(t.InterSystemsId, t.IntraSystemId), 200)                    AS CorrelationId,
            t.ModifiedProperties,
            t.[Target],
            t.ExtendedProperties
        INTO #evt
        FROM audit.CopilotAudit AS a
        OUTER APPLY OPENJSON(
            CASE WHEN ISJSON(CAST(DECOMPRESS(a.AuditDataGz) AS NVARCHAR(MAX))) = 1
                 THEN CAST(DECOMPRESS(a.AuditDataGz) AS NVARCHAR(MAX)) ELSE N'{}' END)
            WITH (
                Operation          NVARCHAR(200)  '$.Operation',
                ResultStatus       NVARCHAR(50)   '$.ResultStatus',
                JsonUserId         NVARCHAR(512)  '$.UserId',
                ObjectId           NVARCHAR(512)  '$.ObjectId',
                InterSystemsId     NVARCHAR(200)  '$.InterSystemsId',
                IntraSystemId      NVARCHAR(200)  '$.IntraSystemId',
                ModifiedProperties NVARCHAR(MAX)  '$.ModifiedProperties' AS JSON,
                [Target]           NVARCHAR(MAX)  '$.Target'             AS JSON,
                ExtendedProperties NVARCHAR(MAX)  '$.ExtendedProperties' AS JSON
            ) AS t
        WHERE a.RecordId > @cursor AND a.RecordId <= @maxId
          AND (@SinceUtc IS NULL OR a.CreatedDateTime >= @SinceUtc)
          /* Copilot Studio's authoring records share the load (2.3.0): audit.usp_ShredAgentEvents takes them. */
          AND a.Operation NOT LIKE N'Bot%';

        CREATE CLUSTERED INDEX CIX_evt ON #evt (RecordId);

        /*
            The affected user. ObjectId usually carries it, but when the change
            was made through a path that leaves ObjectId as a GUID, the UPN is
            in Target[] instead - the entry whose ID looks like a UPN.
        */
        DROP TABLE IF EXISTS #tgt;
        SELECT e.RecordId,
               MAX(CASE WHEN tg.ID LIKE N'%@%' THEN LEFT(tg.ID, 512) END) AS TargetUpn
        INTO #tgt
        FROM #evt AS e
        CROSS APPLY OPENJSON(COALESCE(e.[Target], N'[]'))
             WITH (ID NVARCHAR(512) '$.ID') AS tg
        GROUP BY e.RecordId;

        /*
            Chunk fragments: an 'additionalDetails' entry shaped
            {"id", "seq", "b"}. The fragment is read NVARCHAR(MAX) through
            OPENJSON WITH - it is 10,000 characters, and JSON_VALUE would
            return NULL for anything over 4,000.
        */
        DROP TABLE IF EXISTS #chunk;
        SELECT e.RecordId,
               LEFT(c.GroupId, 100)       AS ChunkGroupId,
               TRY_CAST(c.Seq AS INT)     AS Seq,
               c.Body
        INTO #chunk
        FROM #evt AS e
        CROSS APPLY OPENJSON(COALESCE(e.ExtendedProperties, N'[]'))
             WITH ([Name] NVARCHAR(200) '$.Name', [Value] NVARCHAR(MAX) '$.Value') AS x
        CROSS APPLY OPENJSON(CASE WHEN ISJSON(x.[Value]) = 1 THEN x.[Value] ELSE N'{}' END)
             WITH (GroupId NVARCHAR(200) '$.id',
                   Seq     NVARCHAR(20)  '$.seq',
                   Body    NVARCHAR(MAX) '$.b') AS c
        WHERE x.[Name] = N'additionalDetails'
          AND c.GroupId IS NOT NULL AND TRY_CAST(c.Seq AS INT) IS NOT NULL AND c.Body IS NOT NULL;

        /* Both MERGEs below need unique source rows: one fragment per record,
           and one record per (group, seq). */
        ;WITH d AS (SELECT ROW_NUMBER() OVER (PARTITION BY RecordId ORDER BY Seq) AS rn FROM #chunk)
        DELETE FROM d WHERE rn > 1;
        ;WITH d AS (SELECT ROW_NUMBER() OVER (PARTITION BY ChunkGroupId, Seq ORDER BY RecordId) AS rn FROM #chunk)
        DELETE FROM d WHERE rn > 1;

        MERGE audit.LicenseEvent WITH (HOLDLOCK) AS t
        USING (
            SELECT e.RecordId, e.CreatedDateTime, e.ActivityDate, e.Operation, e.ResultStatus,
                   e.ActorUpn,
                   CASE WHEN e.TargetUpnRaw LIKE N'%@%' THEN e.TargetUpnRaw
                        ELSE g.TargetUpn END                          AS TargetUpn,
                   LEFT(e.TargetUpnRaw, 200)                          AS TargetObjectId,
                   e.CorrelationId,
                   ch.ChunkGroupId,
                   ch.Seq                                             AS ChunkSeq
            FROM #evt AS e
            LEFT JOIN #tgt AS g ON g.RecordId = e.RecordId
            LEFT JOIN #chunk AS ch ON ch.RecordId = e.RecordId
        ) AS s
        ON t.RecordId = s.RecordId
        WHEN MATCHED THEN UPDATE SET
            t.CreatedDateTime = s.CreatedDateTime, t.ActivityDate = s.ActivityDate,
            t.Operation = s.Operation, t.ResultStatus = s.ResultStatus,
            t.ActorUpn = s.ActorUpn, t.TargetUpn = s.TargetUpn,
            t.TargetObjectId = s.TargetObjectId, t.CorrelationId = s.CorrelationId,
            t.ChunkGroupId = s.ChunkGroupId, t.ChunkSeq = s.ChunkSeq,
            t.ShreddedUtc = SYSUTCDATETIME()
        WHEN NOT MATCHED BY TARGET THEN INSERT
            (RecordId, CreatedDateTime, ActivityDate, Operation, ResultStatus,
             ActorUpn, TargetUpn, TargetObjectId, CorrelationId, ChunkGroupId, ChunkSeq)
        VALUES
            (s.RecordId, s.CreatedDateTime, s.ActivityDate, s.Operation, s.ResultStatus,
             s.ActorUpn, s.TargetUpn, s.TargetObjectId, s.CorrelationId, s.ChunkGroupId, s.ChunkSeq);

        SET @rows = @@ROWCOUNT;

        MERGE audit.LicenseEventChunk WITH (HOLDLOCK) AS t
        USING #chunk AS s
        ON t.ChunkGroupId = s.ChunkGroupId AND t.Seq = s.Seq
        WHEN MATCHED THEN UPDATE SET t.RecordId = s.RecordId, t.Body = s.Body
        WHEN NOT MATCHED BY TARGET THEN INSERT (ChunkGroupId, Seq, RecordId, Body)
             VALUES (s.ChunkGroupId, s.Seq, s.RecordId, s.Body);

        INSERT INTO #touched (ChunkGroupId)
        SELECT DISTINCT c.ChunkGroupId FROM #chunk AS c
        WHERE NOT EXISTS (SELECT 1 FROM #touched AS t WHERE t.ChunkGroupId = c.ChunkGroupId);

        /* Properties are replaced wholesale for the batch: an audit record is
           immutable, so a re-run must not accumulate duplicates. Reassembled
           chunk properties are left alone here and replaced after the loop. */
        DELETE p
        FROM audit.LicenseEventProperty AS p
        JOIN #evt AS e ON e.RecordId = p.RecordId
        WHERE p.Source = 'record';

        INSERT INTO audit.LicenseEventProperty (RecordId, Ordinal, [Name], OldValue, NewValue)
        SELECT e.RecordId,
               CAST(mp.[key] AS INT),      -- the array index, which WITH cannot give
               LEFT(mpv.[Name], 400),
               mpv.OldValue,
               mpv.NewValue
        FROM #evt AS e
        CROSS APPLY OPENJSON(COALESCE(e.ModifiedProperties, N'[]')) AS mp
        CROSS APPLY OPENJSON(mp.[value])
             WITH ([Name]   NVARCHAR(400) '$.Name',
                   OldValue NVARCHAR(MAX) '$.OldValue',
                   NewValue NVARCHAR(MAX) '$.NewValue') AS mpv;

        SET @done += @rows;
        SET @cursor = @maxId;

        DROP TABLE IF EXISTS #evt;
        DROP TABLE IF EXISTS #tgt;
        DROP TABLE IF EXISTS #chunk;

        SET @msg = CONCAT(N'  batch ', @batch, N': ', FORMAT(@rows, 'N0'), N' event(s), ',
                          FORMAT(@done, 'N0'), N' so far.');
        RAISERROR(@msg, 0, 1) WITH NOWAIT;
        SET @batch += 1;
    END;

    /*
        Reassemble every chunk group this run touched, using all of its stored
        fragments - including ones an earlier run shredded. A group is complete
        when its seqs run 1..n without a gap; an incomplete one (a fragment
        outside the loaded range) is counted and left alone rather than parsed
        from a partial document.
    */
    DROP TABLE IF EXISTS #whole;
    SELECT c.ChunkGroupId,
           MAX(CASE WHEN c.Seq = 1 THEN c.RecordId END)                              AS CarrierRecordId,
           STRING_AGG(CAST(c.Body AS NVARCHAR(MAX)), N'') WITHIN GROUP (ORDER BY c.Seq) AS Payload
    INTO #whole
    FROM audit.LicenseEventChunk AS c
    JOIN #touched AS t ON t.ChunkGroupId = c.ChunkGroupId
    GROUP BY c.ChunkGroupId
    HAVING MIN(c.Seq) = 1 AND MAX(c.Seq) = COUNT(*);

    DECLARE @groups INT = (SELECT COUNT(*) FROM #touched),
            @complete INT = (SELECT COUNT(*) FROM #whole),
            @parsed INT;

    DELETE p
    FROM audit.LicenseEventProperty AS p
    JOIN #whole AS w ON w.CarrierRecordId = p.RecordId
    WHERE p.Source = 'chunks';

    /*
        The joined document's targetUpdatedProperties is itself a JSON string
        holding the Name/OldValue/NewValue list. OldValue and NewValue are
        arrays there rather than strings, so each entry is read key by key:
        OPENJSON's [value] gives an array back as its JSON text, which is the
        same form ModifiedProperties stores.
    */
    INSERT INTO audit.LicenseEventProperty (RecordId, Ordinal, [Name], OldValue, NewValue, Source)
    SELECT w.CarrierRecordId,
           10000 + CAST(item.[key] AS INT),
           LEFT(MAX(CASE WHEN f.[key] = N'Name' THEN f.[value] END), 400),
           MAX(CASE WHEN f.[key] = N'OldValue' THEN f.[value] END),
           MAX(CASE WHEN f.[key] = N'NewValue' THEN f.[value] END),
           'chunks'
    FROM #whole AS w
    CROSS APPLY OPENJSON(CASE WHEN ISJSON(w.Payload) = 1 THEN w.Payload ELSE N'{}' END)
         WITH (Tup NVARCHAR(MAX) '$.targetUpdatedProperties') AS d
    CROSS APPLY OPENJSON(CASE WHEN ISJSON(d.Tup) = 1 THEN d.Tup ELSE N'[]' END) AS item
    CROSS APPLY OPENJSON(item.[value]) AS f
    WHERE w.CarrierRecordId IS NOT NULL
    GROUP BY w.CarrierRecordId, item.[key];

    SET @parsed = @@ROWCOUNT;

    SET @msg = CONCAT(N'Shred complete: ', FORMAT(@done, 'N0'), N' license event(s); ',
                      FORMAT(@complete, 'N0'), N' of ', FORMAT(@groups, 'N0'),
                      N' chunk group(s) reassembled into ', FORMAT(@parsed, 'N0'), N' propert(ies).');
    RAISERROR(@msg, 0, 1) WITH NOWAIT;

    IF @groups > @complete
    BEGIN
        SET @msg = CONCAT(N'  ', FORMAT(@groups - @complete, 'N0'),
                          N' chunk group(s) are missing a fragment - part of the change lies outside the loaded range. They are kept and will complete on a later run.');
        RAISERROR(@msg, 0, 1) WITH NOWAIT;
    END;

    /* The SKUs each change added and removed, kept for the session build and the bi refresh to read: for what was
       shredded since they were last worked out and the records raised with it, or for every event after a full pass. */
    DECLARE @everyEvent BIT = CASE WHEN @SinceUtc IS NULL THEN 1 ELSE 0 END;
    EXEC audit.usp_RefreshLicenseSkuChange @All = @everyEvent, @SinceShreddedUtc = @shredFrom;

    /* Finished: the mark goes. A shred of every record leaves one of its own (LicenseShredCompleteUtc), which
       Install-CopilotAudit.ps1 looks for before it shreds every record again. */
    DELETE FROM audit.PipelineSetting WHERE Name = N'LicenseShredInProgress';
    IF @SinceUtc IS NULL
    BEGIN
        UPDATE audit.PipelineSetting SET Value = CONVERT(NVARCHAR(30), SYSUTCDATETIME(), 126), UpdatedUtc = SYSUTCDATETIME()
        WHERE Name = N'LicenseShredCompleteUtc';
        IF @@ROWCOUNT = 0
            INSERT INTO audit.PipelineSetting (Name, Value) VALUES (N'LicenseShredCompleteUtc', CONVERT(NVARCHAR(30), SYSUTCDATETIME(), 126));
    END;
    EXEC sys.sp_releaseapplock @Resource = N'audit.usp_ShredLicenseEvents', @LockOwner = 'Session';
    END TRY
    BEGIN CATCH
        /* The lock is the session's: a pooled connection would otherwise keep it, and the next shred wait. The mark
           stays: this shred did not finish, and the next one finishes it. */
        IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
        IF APPLOCK_MODE(N'public', N'audit.usp_ShredLicenseEvents', N'Session') <> N'NoLock'
            EXEC sys.sp_releaseapplock @Resource = N'audit.usp_ShredLicenseEvents', @LockOwner = 'Session';
        THROW;
    END CATCH;
END;
GO

/*
    An upgrade: license events shredded before audit.LicenseSkuChange existed have their SKU changes worked out here,
    once, without shredding them again. Then audit.vLicenseSkuChange reads the table; until this point, in such a
    database, it still read the records (Create-LicenseEventSchema.sql).
*/
IF NOT EXISTS (SELECT 1 FROM audit.LicenseSkuChangeRun) AND EXISTS (SELECT 1 FROM audit.LicenseEvent)
BEGIN
    RAISERROR('Working out the SKU changes of the license events already here, once (2.5.4): about two minutes for 4,000,000 events in the lab...', 0, 1) WITH NOWAIT;
    EXEC audit.usp_RefreshLicenseSkuChange @All = 1;
END;
GO

/* On the table from here on, when its rows are there: worked out above, or no event yet. Where that step failed and
   the script went on to this batch all the same (SSMS, and sqlcmd without -b, do), the view stays on the records. */
IF EXISTS (SELECT 1 FROM audit.LicenseSkuChangeRun) OR NOT EXISTS (SELECT 1 FROM audit.LicenseEvent)
    EXEC (N'
CREATE OR ALTER VIEW audit.vLicenseSkuChange
AS
SELECT RecordId, CreatedDateTime, ActivityDate, ActorUpn, TargetUpn, FromChunks, Sku, Change
FROM audit.LicenseSkuChange;');
GO
