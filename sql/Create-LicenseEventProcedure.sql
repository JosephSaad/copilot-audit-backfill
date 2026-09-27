/*
    audit.usp_ShredLicenseEvents
    ------------------------------------------------------------------------
    Turns the AzureActiveDirectory records in audit.CopilotAudit into
    audit.LicenseEvent and audit.LicenseEventProperty.

    Run it in the database the licence-operations backfill loaded - a
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
    BEGIN
        RAISERROR('Rebuilding the licence event tables from scratch.', 0, 1) WITH NOWAIT;
        TRUNCATE TABLE audit.LicenseEventProperty;
        TRUNCATE TABLE audit.LicenseEventChunk;
        TRUNCATE TABLE audit.LicenseEvent;
        SET @SinceUtc = NULL;
    END;

    /* Chunk groups this run saw, reassembled once every batch is in. */
    DROP TABLE IF EXISTS #touched;
    CREATE TABLE #touched (ChunkGroupId NVARCHAR(100) NOT NULL PRIMARY KEY);

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

    SET @msg = CONCAT(N'Shred complete: ', FORMAT(@done, 'N0'), N' licence event(s); ',
                      FORMAT(@complete, 'N0'), N' of ', FORMAT(@groups, 'N0'),
                      N' chunk group(s) reassembled into ', FORMAT(@parsed, 'N0'), N' propert(ies).');
    RAISERROR(@msg, 0, 1) WITH NOWAIT;

    IF @groups > @complete
    BEGIN
        SET @msg = CONCAT(N'  ', FORMAT(@groups - @complete, 'N0'),
                          N' chunk group(s) are missing a fragment - part of the change lies outside the loaded range. They are kept and will complete on a later run.');
        RAISERROR(@msg, 0, 1) WITH NOWAIT;
    END;
END;
GO
