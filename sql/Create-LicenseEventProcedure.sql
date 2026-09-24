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
    @ReportingTimeZone SYSNAME   = N'Eastern Standard Time'
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF @BatchSize IS NULL OR @BatchSize < 1000 SET @BatchSize = 1000;

    DECLARE @msg NVARCHAR(400), @rows BIGINT = 0, @done BIGINT = 0, @batch INT = 1;

    IF @RebuildAll = 1
    BEGIN
        RAISERROR('Rebuilding the licence event tables from scratch.', 0, 1) WITH NOWAIT;
        TRUNCATE TABLE audit.LicenseEventProperty;
        TRUNCATE TABLE audit.LicenseEvent;
        SET @SinceUtc = NULL;
    END;

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
            t.[Target]
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
                [Target]           NVARCHAR(MAX)  '$.Target'             AS JSON
            ) AS t
        WHERE a.RecordId > @cursor AND a.RecordId <= @maxId
          AND (@SinceUtc IS NULL OR a.CreatedDateTime >= @SinceUtc);

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

        MERGE audit.LicenseEvent WITH (HOLDLOCK) AS t
        USING (
            SELECT e.RecordId, e.CreatedDateTime, e.ActivityDate, e.Operation, e.ResultStatus,
                   e.ActorUpn,
                   CASE WHEN e.TargetUpnRaw LIKE N'%@%' THEN e.TargetUpnRaw
                        ELSE g.TargetUpn END                          AS TargetUpn,
                   LEFT(e.TargetUpnRaw, 200)                          AS TargetObjectId,
                   e.CorrelationId
            FROM #evt AS e
            LEFT JOIN #tgt AS g ON g.RecordId = e.RecordId
        ) AS s
        ON t.RecordId = s.RecordId
        WHEN MATCHED THEN UPDATE SET
            t.CreatedDateTime = s.CreatedDateTime, t.ActivityDate = s.ActivityDate,
            t.Operation = s.Operation, t.ResultStatus = s.ResultStatus,
            t.ActorUpn = s.ActorUpn, t.TargetUpn = s.TargetUpn,
            t.TargetObjectId = s.TargetObjectId, t.CorrelationId = s.CorrelationId,
            t.ShreddedUtc = SYSUTCDATETIME()
        WHEN NOT MATCHED BY TARGET THEN INSERT
            (RecordId, CreatedDateTime, ActivityDate, Operation, ResultStatus,
             ActorUpn, TargetUpn, TargetObjectId, CorrelationId)
        VALUES
            (s.RecordId, s.CreatedDateTime, s.ActivityDate, s.Operation, s.ResultStatus,
             s.ActorUpn, s.TargetUpn, s.TargetObjectId, s.CorrelationId);

        SET @rows = @@ROWCOUNT;

        /* Properties are replaced wholesale for the batch: an audit record is
           immutable, so a re-run must not accumulate duplicates. */
        DELETE p
        FROM audit.LicenseEventProperty AS p
        JOIN #evt AS e ON e.RecordId = p.RecordId;

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

        SET @msg = CONCAT(N'  batch ', @batch, N': ', FORMAT(@rows, 'N0'), N' event(s), ',
                          FORMAT(@done, 'N0'), N' so far.');
        RAISERROR(@msg, 0, 1) WITH NOWAIT;
        SET @batch += 1;
    END;

    SET @msg = CONCAT(N'Shred complete: ', FORMAT(@done, 'N0'), N' licence event(s).');
    RAISERROR(@msg, 0, 1) WITH NOWAIT;
END;
GO
