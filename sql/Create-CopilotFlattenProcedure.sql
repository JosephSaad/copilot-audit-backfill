/*
    Copilot audit backfill - audit.usp_FlattenCopilotAudit
    ------------------------------------------------------------------------
    Shreds the CopilotEventData JSON into audit.RawLogFlattened, one row per
    audit record, with every informative scalar field of the record in its own
    column (2.2.0; the seventeen that carry no information are counted in
    audit.RawLogRecordShape instead), and the AccessedResources and Contexts arrays into
    audit.RawLogAccessedResource and audit.RawLogContext, one row per element.

    Reads audit.vCopilotAuditRecord, which has the Purview export's column
    shape (RecordId, CreationDate, RecordType, Operation, UserId, AuditData,
    AssociatedAdminUnits, AssociatedAdminUnitsNames). Any table with those
    columns works, so to flatten a portal CSV import instead, change the one
    FROM in the #batch load below to your own raw-log table.

    Parameters:
      @SinceUtc     only records at or after this UTC time. NULL = everything.
      @RebuildAll   1 truncates and rebuilds; 0 (default) MERGEs, so it is
                    safe to run incrementally on a schedule. 1 is refused
                    once the purge has cleared any record's JSON (2.4.12):
                    run with 0, which flattens every record again and keeps
                    what the purged ones were flattened to.
      @BatchSize    records per batch. Keeps the log, tempdb and lock
                    footprint flat on tens of millions of rows. Default
                    200,000.

    The agent (2.4.11): a record carries its agent's id and friendly name at
    the top ($.AgentId, $.AgentName), not inside CopilotEventData, where this
    procedure looked before and found nothing on any record. Both places are
    read now, and each batch's agents go into audit.AgentCatalog with the
    latest name the log gave each (audit.usp_CatalogAgents, below). Records
    flattened before 2.4.11 get theirs from audit.usp_BackfillAgentNames, once.

    Re-runnable and idempotent: a record already flattened is updated in place,
    keyed on RecordId, and its element rows replaced. A record whose JSON the
    purge procedure has cleared is skipped once it has been flattened, so a
    full re-flatten never blanks what was shredded while the JSON existed.

    ---- How it parses, and why it is shaped this way --------------------------

    Every JSON_VALUE or OPENJSON call against a document parses that whole
    document. Reading twenty-odd fields with twenty-odd calls therefore parses
    each record twenty-odd times, and the documents here are the expensive part
    of the row - they arrive gzipped and are decompressed by the view before
    any of this runs.

    So each batch parses in stages, widest first, and keeps the results:

      #batch  one OPENJSON over the whole document for the top-level scalars,
              and a second over the CopilotEventData fragment it returns for
              that object's scalars. Both pull the nested arrays out as JSON
              fragments in the same pass, so nothing below ever touches the
              full document again.
      #res    the AccessedResources fragment exploded once into rows.

    These are temp tables rather than CTEs on purpose: SQL Server does not
    materialise a CTE, so referencing one several times re-runs the OPENJSON
    underneath it, which is the cost this is trying to avoid. Everything after
    #res - including the DISTINCT lists, which STRING_AGG cannot express
    directly - is ordinary relational aggregation over rows that are already
    parsed.

    Requires SQL Server 2017+ (STRING_AGG, OPENJSON). Run
    sql/Create-CopilotFlattenedSchema.sql first.
*/
SET NOCOUNT ON;

/*
    audit.usp_CatalogValues: adds the distinct values of the cataloged columns
    (audit.ValueCatalogColumn) to audit.ValueCatalog. The flatten calls it for
    each batch while #batch exists, so only that batch's records are read.
    Called on its own it reads every flattened record; @Rebuild = 1 starts the
    catalog again (Invoke-CopilotMaintenance.ps1 -Task RebuildValueCatalog).
    A column stops collecting at 10,000 distinct values and is marked Capped.
*/
IF OBJECT_ID(N'audit.usp_CatalogValues', N'P') IS NULL
    EXEC (N'CREATE PROCEDURE audit.usp_CatalogValues AS RETURN 0;');
GO

ALTER PROCEDURE audit.usp_CatalogValues
    @Rebuild BIT = 0
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    IF OBJECT_ID(N'audit.ValueCatalogColumn', N'U') IS NULL RETURN;

    DECLARE @cap INT = 10000;
    DECLARE @inBatch BIT = CASE WHEN OBJECT_ID(N'tempdb..#batch') IS NULL THEN 0 ELSE 1 END;
    IF @Rebuild = 1 AND @inBatch = 0
    BEGIN
        TRUNCATE TABLE audit.ValueCatalog;
        UPDATE audit.ValueCatalogColumn SET DistinctValues = 0, Capped = 0, UpdatedUtc = NULL;
    END;

    /* In the database's collation (2.4.12): in tempdb's, a database whose collation differs from the server's
       failed every flatten at the first comparison with the catalog. So for #agents below. */
    CREATE TABLE #v (ColumnId SMALLINT NOT NULL, Value NVARCHAR(400) COLLATE DATABASE_DEFAULT NOT NULL, N BIGINT NOT NULL, PRIMARY KEY (ColumnId, Value));

    DECLARE @t SYSNAME, @vals NVARCHAR(MAX), @sql NVARCHAR(MAX);
    DECLARE tc CURSOR LOCAL FAST_FORWARD FOR
        SELECT DISTINCT TableName FROM audit.ValueCatalogColumn
        WHERE Capped = 0 AND OBJECT_ID(N'audit.' + QUOTENAME(TableName), N'U') IS NOT NULL;
    OPEN tc;
    FETCH NEXT FROM tc INTO @t;
    WHILE @@FETCH_STATUS = 0
    BEGIN
        /* One pass over the table for all its columns: each row unpivoted to (column, value). */
        SELECT @vals = STRING_AGG(CAST(CONCAT(N'(', c.ColumnId, N', ',
                   CASE WHEN ty.name = N'uniqueidentifier' THEN N'LOWER(CAST(t.' + QUOTENAME(c.ColumnName) + N' AS NVARCHAR(36)))'
                        ELSE N'LEFT(LTRIM(RTRIM(CAST(t.' + QUOTENAME(c.ColumnName) + N' AS NVARCHAR(400)))), 400)' END,
                   N')') AS NVARCHAR(MAX)), N', ')
        FROM audit.ValueCatalogColumn AS c
        JOIN sys.columns AS sc ON sc.object_id = OBJECT_ID(N'audit.' + QUOTENAME(c.TableName)) AND sc.name = c.ColumnName
        JOIN sys.types AS ty ON ty.user_type_id = sc.user_type_id
        WHERE c.TableName = @t AND c.Capped = 0;

        IF @vals IS NOT NULL
        BEGIN
            SET @sql = N'INSERT INTO #v (ColumnId, Value, N)
SELECT v.ColumnId, v.Value, COUNT_BIG(*)
FROM audit.' + QUOTENAME(@t) + N' AS t '
                + CASE WHEN @inBatch = 1 THEN N'JOIN #batch AS b ON b.RecordId = t.RecordId ' ELSE N'' END + N'
CROSS APPLY (VALUES ' + @vals + N') AS v (ColumnId, Value)
WHERE v.Value IS NOT NULL AND v.Value <> N''''
GROUP BY v.ColumnId, v.Value;';
            EXEC sys.sp_executesql @sql;
        END;
        FETCH NEXT FROM tc INTO @t;
    END;
    CLOSE tc;
    DEALLOCATE tc;

    BEGIN TRANSACTION;
    UPDATE t SET Records = t.Records + v.N, LastSeenUtc = SYSUTCDATETIME()
    FROM audit.ValueCatalog AS t
    JOIN #v AS v ON v.ColumnId = t.ColumnId AND v.Value = t.Value;

    /* New values, up to the cap: the most frequent first. */
    WITH n AS
    (
        SELECT v.ColumnId, v.Value, v.N, ROW_NUMBER() OVER (PARTITION BY v.ColumnId ORDER BY v.N DESC, v.Value) AS rn
        FROM #v AS v
        WHERE NOT EXISTS (SELECT 1 FROM audit.ValueCatalog AS t WHERE t.ColumnId = v.ColumnId AND t.Value = v.Value)
    )
    INSERT INTO audit.ValueCatalog (ColumnId, Value, Records)
    SELECT n.ColumnId, n.Value, n.N
    FROM n JOIN audit.ValueCatalogColumn AS c ON c.ColumnId = n.ColumnId
    WHERE n.rn <= @cap - c.DistinctValues;

    UPDATE c
    SET DistinctValues = ISNULL(k.N, 0),
        Capped = CASE WHEN ISNULL(k.N, 0) >= @cap THEN 1 ELSE 0 END,
        UpdatedUtc = SYSUTCDATETIME()
    FROM audit.ValueCatalogColumn AS c
    LEFT JOIN (SELECT ColumnId, COUNT(*) AS N FROM audit.ValueCatalog GROUP BY ColumnId) AS k ON k.ColumnId = c.ColumnId
    WHERE c.ColumnId IN (SELECT ColumnId FROM #v) OR @Rebuild = 1;
    COMMIT;
END;
GO

/*
    audit.usp_CatalogAgents: the agents named by a set of flattened records into
    audit.AgentCatalog, each with the latest friendly name the records give it.
    The flatten calls it for each batch while #batch exists, so only that
    batch's records are read; called on its own it reads every flattened record
    (one pass over audit.RawLogFlattened). Idempotent: the same records again
    change nothing. A name is replaced only by one from a record at least as
    recent, so a batch of older records never undoes a rename.
*/
IF OBJECT_ID(N'audit.usp_CatalogAgents', N'P') IS NULL
    EXEC (N'CREATE PROCEDURE audit.usp_CatalogAgents AS RETURN 0;');
GO

ALTER PROCEDURE audit.usp_CatalogAgents
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    IF OBJECT_ID(N'audit.AgentCatalog', N'U') IS NULL RETURN;

    DECLARE @from NVARCHAR(100) = CASE WHEN OBJECT_ID(N'tempdb..#batch') IS NULL THEN N'audit.RawLogFlattened' ELSE N'#batch' END;
    CREATE TABLE #agents (AppIdentity NVARCHAR(200) COLLATE DATABASE_DEFAULT NOT NULL, AgentId NVARCHAR(400) COLLATE DATABASE_DEFAULT NOT NULL,
                          AgentName NVARCHAR(400) COLLATE DATABASE_DEFAULT NULL,
                          NameSeenUtc DATETIME2(0) NULL, FirstSeenUtc DATETIME2(0) NULL, LastSeenUtc DATETIME2(0) NULL);

    /* One row per agent: its first and last record, and the name on the latest record that has one. */
    DECLARE @sql NVARCHAR(MAX) = N'
INSERT INTO #agents (AppIdentity, AgentId, AgentName, NameSeenUtc, FirstSeenUtc, LastSeenUtc)
SELECT x.AppIdentity, x.AgentId, MAX(CASE WHEN x.rn = 1 THEN x.AgentName END), MAX(CASE WHEN x.rn = 1 THEN x.Utc END), MIN(x.Utc), MAX(x.Utc)
FROM (SELECT v.AppIdentity, v.AgentId, v.AgentName, b.UtcTimeStamp AS Utc,
             CASE WHEN v.AgentName IS NULL THEN NULL
                  ELSE ROW_NUMBER() OVER (PARTITION BY v.AppIdentity, v.AgentId, CASE WHEN v.AgentName IS NULL THEN 0 ELSE 1 END
                                          ORDER BY b.UtcTimeStamp DESC, b.RecordId DESC) END AS rn
      FROM ' + @from + N' AS b
      CROSS APPLY (SELECT ISNULL(LTRIM(RTRIM(b.AppIdentity)), N'''') AS AppIdentity, ISNULL(LTRIM(RTRIM(b.AgentId)), N'''') AS AgentId,
                          NULLIF(LTRIM(RTRIM(b.AgentName)), N'''') AS AgentName) AS v
      WHERE v.AgentId <> N'''' OR v.AgentName IS NOT NULL OR v.AppIdentity LIKE N''Copilot.Studio.%'' OR v.AppIdentity LIKE N''MicrosoftAgent.%'') AS x
GROUP BY x.AppIdentity, x.AgentId;';
    EXEC sys.sp_executesql @sql;

    MERGE audit.AgentCatalog AS t
    USING #agents AS s ON t.AppIdentity = s.AppIdentity AND t.AgentId = s.AgentId
    WHEN MATCHED THEN UPDATE SET
        AgentName    = CASE WHEN s.AgentName IS NOT NULL AND (t.NameSeenUtc IS NULL OR s.NameSeenUtc >= t.NameSeenUtc) THEN s.AgentName ELSE t.AgentName END,
        NameSeenUtc  = CASE WHEN s.AgentName IS NOT NULL AND (t.NameSeenUtc IS NULL OR s.NameSeenUtc >= t.NameSeenUtc) THEN s.NameSeenUtc ELSE t.NameSeenUtc END,
        FirstSeenUtc = CASE WHEN t.FirstSeenUtc IS NULL OR s.FirstSeenUtc < t.FirstSeenUtc THEN s.FirstSeenUtc ELSE t.FirstSeenUtc END,
        LastSeenUtc  = CASE WHEN t.LastSeenUtc IS NULL OR s.LastSeenUtc > t.LastSeenUtc THEN s.LastSeenUtc ELSE t.LastSeenUtc END
    WHEN NOT MATCHED THEN INSERT (AppIdentity, AgentId, AgentName, NameSeenUtc, FirstSeenUtc, LastSeenUtc)
        VALUES (s.AppIdentity, s.AgentId, s.AgentName, s.NameSeenUtc, s.FirstSeenUtc, s.LastSeenUtc);
END;
GO

/*
    audit.usp_BackfillAgentNames: the agent's id and friendly name for records
    flattened before 2.4.11, which looked for them in the wrong place. Reads
    only the records of agents (AppIdentity names one) that still have no id or
    no name, takes $.AgentId and $.AgentName from each one's stored JSON, writes
    them to audit.RawLogFlattened, and catalogs the agents. Nothing else of the
    record is flattened again, so it takes minutes where a full re-flatten
    takes hours. A record whose JSON the purge cleared keeps what it has.

    Install-CopilotAudit.ps1 runs it once on an upgrade: it leaves a mark in
    audit.PipelineSetting (AgentNamesReadUtc), as does a flatten of every
    record. Safe to run again: it reads what is still empty, and changes
    nothing that is filled. Each batch's records and their agents commit
    together, so a run stopped part way reads its last batch again (2.4.12).

    @Others = 1 (2.4.12): the records of any other app that carry an agent's id,
    such as Copilot Chat running a declarative agent. bi keys those on the
    AgentId, which 2.4.11 read from its upgrade night on: before it they had
    no agent, so the same agent's history started that night. Read are the
    apps that carry one: those whose records flattened since show it, and
    those among the newest records still without one whose JSON has it; an
    app that carries none has nothing to read. Its mark is AgentIdsReadUtc,
    and it returns how many
    records gained an id: each of those changes agent, so bi is rebuilt once.
    Records flattened after AgentNamesReadUtc (by 2.4.11 or later, which read
    the id where it is) are not read again.

    Resumable (2.4.12): each batch saves where it got to and what it gained
    (<mark>Cursor, <mark>Gained in audit.PipelineSetting), and a run carries on
    from there; the mark clears them.
*/
IF OBJECT_ID(N'audit.usp_BackfillAgentNames', N'P') IS NULL
    EXEC (N'CREATE PROCEDURE audit.usp_BackfillAgentNames AS RETURN 0;');
GO

ALTER PROCEDURE audit.usp_BackfillAgentNames
    @BatchSize INT = 50000,
    @Others    BIT = 0
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    IF @BatchSize IS NULL OR @BatchSize < 1000 SET @BatchSize = 1000;

    DECLARE @msg NVARCHAR(400), @read BIGINT = 0, @named BIGINT = 0, @rows INT;
    DECLARE @cursor NVARCHAR(450) = N'', @maxId NVARCHAR(450);
    DECLARE @mark SYSNAME = CASE WHEN @Others = 1 THEN N'AgentIdsReadUtc' ELSE N'AgentNamesReadUtc' END;
    /* 2.4.12: where a run stopped part way got to, saved with each batch, and what it had gained. The read started
       from the first record again, and with @Others that is most of Copilot Chat's history: an install stopped
       after hours began again from zero, and a resumed run that gained nothing left bi unbuilt. */
    DECLARE @cursorName SYSNAME = @mark + N'Cursor', @gainedName SYSNAME = @mark + N'Gained', @before BIGINT = 0;
    /* With @Others: records flattened once the 2.4.11 flatten was in already read the agent's id where it is, so
       they are left out. AgentNamesReadUtc marks that (it is written after that release installed its flatten). */
    DECLARE @since DATETIME2(0);
    IF OBJECT_ID(N'audit.PipelineSetting', N'U') IS NOT NULL
    BEGIN
        SELECT @cursor = ISNULL(Value, N'') FROM audit.PipelineSetting WHERE Name = @cursorName;
        SELECT @before = ISNULL(TRY_CAST(Value AS BIGINT), 0) FROM audit.PipelineSetting WHERE Name = @gainedName;
        IF @Others = 1
            SELECT @since = TRY_CONVERT(DATETIME2(0), Value, 126) FROM audit.PipelineSetting WHERE Name = N'AgentNamesReadUtc';
    END;
    IF @cursor <> N''
    BEGIN
        SET @msg = CONCAT(N'  Carrying on where a run stopped part way (', FORMAT(@before, 'N0'), N' record(s) gained by then).');
        RAISERROR(@msg, 0, 1) WITH NOWAIT;
    END;

    /* The records to read, keyed once: an agent's, with its id or its name still empty; or, with @Others, another
       app's with no id, where that app's records flattened since 2.4.11 show one. */
    DROP TABLE IF EXISTS #todo;
    CREATE TABLE #todo (RecordId NVARCHAR(450) COLLATE DATABASE_DEFAULT NOT NULL);
    IF @Others = 0
        INSERT INTO #todo (RecordId)
        SELECT f.RecordId
        FROM audit.RawLogFlattened AS f
        WHERE (f.AppIdentity LIKE N'Copilot.Studio.%' OR f.AppIdentity LIKE N'MicrosoftAgent.%')
          AND (f.AgentId IS NULL OR f.AgentName IS NULL)
          AND f.RecordId > @cursor;
    ELSE
    BEGIN
        /* The apps that carry an agent's id: those whose records flattened since 2.4.11 show one, and those among the
           newest records still without one whose JSON has one (an upgrade from before 2.4.11 has flattened none since). */
        DROP TABLE IF EXISTS #apps;
        CREATE TABLE #apps (AppIdentity NVARCHAR(200) COLLATE DATABASE_DEFAULT NOT NULL PRIMARY KEY);
        INSERT INTO #apps (AppIdentity)
        SELECT DISTINCT g.AppIdentity
        FROM audit.RawLogFlattened AS g
        WHERE g.AgentId IS NOT NULL AND g.AppIdentity IS NOT NULL
          AND g.AppIdentity NOT LIKE N'Copilot.Studio.%' AND g.AppIdentity NOT LIKE N'MicrosoftAgent.%';
        INSERT INTO #apps (AppIdentity)
        SELECT DISTINCT s.AppIdentity
        FROM (SELECT TOP (50000) f.RecordId, f.AppIdentity
              FROM audit.RawLogFlattened AS f
              WHERE f.AgentId IS NULL AND f.AppIdentity IS NOT NULL
                AND f.AppIdentity NOT LIKE N'Copilot.Studio.%' AND f.AppIdentity NOT LIKE N'MicrosoftAgent.%'
                AND (@since IS NULL OR f.FlattenedUtc < @since)
              ORDER BY f.UtcTimeStamp DESC) AS s
        JOIN audit.vCopilotAuditRecord AS r ON r.RecordId = s.RecordId
        CROSS APPLY OPENJSON(CASE WHEN ISJSON(r.AuditData) = 1 THEN r.AuditData ELSE N'{}' END)
            WITH (TopId NVARCHAR(400) '$.AgentId', InId NVARCHAR(400) '$.CopilotEventData.AgentId') AS j
        WHERE COALESCE(NULLIF(j.InId, N''), NULLIF(j.TopId, N'')) IS NOT NULL
          AND NOT EXISTS (SELECT 1 FROM #apps AS a WHERE a.AppIdentity = s.AppIdentity);
        INSERT INTO #todo (RecordId)
        SELECT f.RecordId
        FROM audit.RawLogFlattened AS f
        JOIN #apps AS a ON a.AppIdentity = f.AppIdentity
        WHERE f.AgentId IS NULL
          AND (@since IS NULL OR f.FlattenedUtc < @since)
          AND f.RecordId > @cursor;
        SET @msg = CONCAT(N'  Agent ids: ', (SELECT COUNT(*) FROM #apps), N' other app(s) carry them; ', FORMAT((SELECT COUNT_BIG(*) FROM #todo), 'N0'), N' of their record(s) have none yet.');
        RAISERROR(@msg, 0, 1) WITH NOWAIT;
    END;
    CREATE CLUSTERED INDEX CIX_todo ON #todo (RecordId);

    WHILE 1 = 1
    BEGIN
        SET @maxId = NULL;
        ;WITH nextBatch AS (SELECT TOP (@BatchSize) k.RecordId FROM #todo AS k WHERE k.RecordId > @cursor ORDER BY k.RecordId)
        SELECT @maxId = MAX(RecordId) FROM nextBatch;
        IF @maxId IS NULL BREAK;

        /* #batch: the name audit.usp_CatalogAgents reads. What the record already has is kept; the JSON fills what is empty. */
        DROP TABLE IF EXISTS #batch;
        SELECT f.RecordId, f.AppIdentity, f.UtcTimeStamp,
               COALESCE(NULLIF(f.AgentId, N''),   LEFT(COALESCE(NULLIF(j.InId, N''),   NULLIF(j.TopId, N'')), 400))   AS AgentId,
               COALESCE(NULLIF(f.AgentName, N''), LEFT(COALESCE(NULLIF(j.InName, N''), NULLIF(j.TopName, N'')), 400)) AS AgentName,
               CAST(CASE WHEN (NULLIF(f.AgentId, N'') IS NULL AND COALESCE(NULLIF(j.InId, N''), NULLIF(j.TopId, N'')) IS NOT NULL)
                           OR (NULLIF(f.AgentName, N'') IS NULL AND COALESCE(NULLIF(j.InName, N''), NULLIF(j.TopName, N'')) IS NOT NULL)
                         THEN 1 ELSE 0 END AS BIT) AS Gained
        INTO #batch
        FROM #todo AS k
        JOIN audit.RawLogFlattened AS f ON f.RecordId = k.RecordId
        LEFT JOIN audit.vCopilotAuditRecord AS r ON r.RecordId = k.RecordId
        OUTER APPLY OPENJSON(CASE WHEN ISJSON(r.AuditData) = 1 THEN r.AuditData ELSE N'{}' END)
            WITH (TopId NVARCHAR(400) '$.AgentId', TopName NVARCHAR(400) '$.AgentName',
                  InId  NVARCHAR(400) '$.CopilotEventData.AgentId', InName NVARCHAR(400) '$.CopilotEventData.AgentName') AS j
        WHERE k.RecordId > @cursor AND k.RecordId <= @maxId
        OPTION (MAXDOP 0);
        SET @rows = @@ROWCOUNT;

        /* 2.4.12: the batch's records and its catalog step in one transaction. A run stopped between them kept the
           records' new ids and names but not their agents: the next run reads only what is still empty, so those
           names never reached the catalog, and bi kept the name read from the id. Now a stopped batch is read again. */
        BEGIN TRANSACTION;
        UPDATE f
        SET AgentId = b.AgentId, AgentName = b.AgentName
        FROM audit.RawLogFlattened AS f
        JOIN #batch AS b ON b.RecordId = f.RecordId
        WHERE b.Gained = 1;
        SET @named += @@ROWCOUNT;

        EXEC audit.usp_CatalogAgents;

        /* Where this run got to, with the batch it ends (2.4.12): a run stopped after it carries on from the next. */
        IF OBJECT_ID(N'audit.PipelineSetting', N'U') IS NOT NULL
        BEGIN
            UPDATE audit.PipelineSetting SET Value = @maxId, UpdatedUtc = SYSUTCDATETIME() WHERE Name = @cursorName;
            IF @@ROWCOUNT = 0 INSERT INTO audit.PipelineSetting (Name, Value) VALUES (@cursorName, @maxId);
            UPDATE audit.PipelineSetting SET Value = CONVERT(NVARCHAR(30), @before + @named), UpdatedUtc = SYSUTCDATETIME() WHERE Name = @gainedName;
            IF @@ROWCOUNT = 0 INSERT INTO audit.PipelineSetting (Name, Value) VALUES (@gainedName, CONVERT(NVARCHAR(30), @before + @named));
        END;
        COMMIT TRANSACTION;

        SET @read += @rows;
        SET @cursor = @maxId;
        DROP TABLE IF EXISTS #batch;
        SET @msg = CONCAT(N'  ', FORMAT(@read, 'N0'), N' agent record(s) read, ', FORMAT(@named, 'N0'), N' gained an id or a name.');
        RAISERROR(@msg, 0, 1) WITH NOWAIT;
    END;

    IF OBJECT_ID(N'audit.PipelineSetting', N'U') IS NOT NULL
    BEGIN
        BEGIN TRANSACTION;
        UPDATE audit.PipelineSetting SET Value = CONVERT(NVARCHAR(30), SYSUTCDATETIME(), 126), UpdatedUtc = SYSUTCDATETIME() WHERE Name = @mark;
        IF @@ROWCOUNT = 0 INSERT INTO audit.PipelineSetting (Name, Value) VALUES (@mark, CONVERT(NVARCHAR(30), SYSUTCDATETIME(), 126));
        DELETE FROM audit.PipelineSetting WHERE Name IN (@cursorName, @gainedName);
        COMMIT TRANSACTION;
    END;
    /* What this read gained, a run stopped before it included. */
    SET @named += @before;

    SET @msg = CONCAT(N'Agent names: ', FORMAT(@read, 'N0'), N' record(s) ', CASE WHEN @Others = 1 THEN N'of other apps' ELSE N'of agents' END, N' read, ',
                      FORMAT(@named, 'N0'), N' gained an id or a name; ',
                      (SELECT COUNT(*) FROM audit.AgentCatalog WHERE AgentName IS NOT NULL), N' of ', (SELECT COUNT(*) FROM audit.AgentCatalog), N' agent(s) in the catalog have a name.');
    RAISERROR(@msg, 0, 1) WITH NOWAIT;
    /* For Install-CopilotAudit.ps1: records of other apps that gained an id change agent, so bi is rebuilt once. */
    IF @Others = 1 SELECT @named AS Gained;
END;
GO

IF OBJECT_ID(N'audit.usp_FlattenCopilotAudit', N'P') IS NULL
    EXEC (N'CREATE PROCEDURE audit.usp_FlattenCopilotAudit AS BEGIN SET NOCOUNT ON; END;');
GO

ALTER PROCEDURE audit.usp_FlattenCopilotAudit
    @SinceUtc   DATETIME2(0) = NULL,
    @RebuildAll BIT          = 0,
    @BatchSize  INT          = 200000
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF @BatchSize IS NULL OR @BatchSize < 1000 SET @BatchSize = 1000;

    DECLARE @msg NVARCHAR(400), @rows BIGINT = 0, @done BIGINT = 0, @batch INT = 1;
    DECLARE @hasLicense BIT = CASE WHEN OBJECT_ID(N'audit.CopilotLicense', N'U') IS NULL THEN 0 ELSE 1 END;

    /* 2.4.12: a record whose JSON the purge cleared is kept as it was flattened (below), found by its flattened row.
       Emptying the table first left nothing to find it by, so every such record came back blank: every column the
       JSON gave NULL, no resources and no contexts, and nothing left to flatten it from again. */
    IF @RebuildAll = 1 AND OBJECT_ID(N'audit.CopilotAudit', N'U') IS NOT NULL
       AND EXISTS (SELECT 1 FROM audit.CopilotAudit WHERE AuditDataGz IS NULL)
    BEGIN
        RAISERROR('Rebuild refused: the purge has cleared the JSON of some records, and a rebuild would blank what they were flattened to. Run without @RebuildAll: every record is flattened again, and those keep what they have.', 16, 1);
        RETURN;
    END;

    IF @RebuildAll = 1
    BEGIN
        RAISERROR('Rebuilding audit.RawLogFlattened from scratch.', 0, 1) WITH NOWAIT;
        TRUNCATE TABLE audit.RawLogFlattened;
        IF OBJECT_ID(N'audit.RawLogAccessedResource', N'U') IS NOT NULL TRUNCATE TABLE audit.RawLogAccessedResource;
        IF OBJECT_ID(N'audit.RawLogContext', N'U') IS NOT NULL TRUNCATE TABLE audit.RawLogContext;
        IF OBJECT_ID(N'audit.RawLogRecordShape', N'U') IS NOT NULL TRUNCATE TABLE audit.RawLogRecordShape;
        IF OBJECT_ID(N'audit.usp_CatalogValues', N'P') IS NOT NULL EXEC audit.usp_CatalogValues @Rebuild = 1;
        /* Emptied: not flattened in full until this run finishes (2.4.12). */
        IF OBJECT_ID(N'audit.PipelineSetting', N'U') IS NOT NULL DELETE FROM audit.PipelineSetting WHERE Name = N'FullFlattenUtc';
        SET @SinceUtc = NULL;
    END;

    /*
        Work through the records in RecordId order, @BatchSize at a time. The
        cursor is the last RecordId done, so a batch that fails can be resumed
        by re-running: already-flattened records simply MERGE to themselves.
    */
    DECLARE @cursor NVARCHAR(450) = N'';
    DECLARE @maxId  NVARCHAR(450);

    /*
        The records to do, keyed once. Finding each batch's upper bound in the
        columnstore itself scans and sorts every remaining RecordId: about 95
        seconds a batch at 40M records, half the flatten. Keyed here it is a
        seek. Records that arrive while this runs are the next run's.
    */
    DROP TABLE IF EXISTS #todo;
    SELECT r.RecordId
    INTO #todo
    FROM audit.vCopilotAuditRecord AS r
    WHERE @SinceUtc IS NULL OR r.CreationDate >= @SinceUtc;
    CREATE CLUSTERED INDEX CIX_todo ON #todo (RecordId);

    WHILE 1 = 1
    BEGIN
        SET @maxId = NULL;

        ;WITH nextBatch AS
        (
            SELECT TOP (@BatchSize) k.RecordId
            FROM #todo AS k
            WHERE k.RecordId > @cursor
            ORDER BY k.RecordId
        )
        SELECT @maxId = MAX(RecordId) FROM nextBatch;

        IF @maxId IS NULL BREAK;   -- nothing left

        DROP TABLE IF EXISTS #batch;
        DROP TABLE IF EXISTS #raw;
        DROP TABLE IF EXISTS #res;
        DROP TABLE IF EXISTS #resagg;
        DROP TABLE IF EXISTS #actagg;
        DROP TABLE IF EXISTS #labagg;
        DROP TABLE IF EXISTS #ctxagg;
        DROP TABLE IF EXISTS #plugagg;
        DROP TABLE IF EXISTS #msgagg;

        /* ---- 0. Each record's JSON, decompressed once ------------------- */
        /*
            The view decompresses AuditData, and SQL Server may evaluate a
            view's expression once per reference. Materialising the batch here
            decompresses each record exactly once (in parallel), and lets the
            purge rule below drop records without touching their JSON again.
        */
        DROP TABLE IF EXISTS #raw;
        SELECT r.RecordId, r.CreationDate, r.UserId, r.AuditData, r.AssociatedAdminUnits, r.AssociatedAdminUnitsNames
        INTO #raw
        FROM audit.vCopilotAuditRecord AS r
        WHERE r.RecordId > @cursor AND r.RecordId <= @maxId
          AND (@SinceUtc IS NULL OR r.CreationDate >= @SinceUtc);

        /* JSON cleared by the purge: keep what was shredded while it existed. */
        DELETE w FROM #raw AS w
        WHERE w.AuditData IS NULL
          AND EXISTS (SELECT 1 FROM audit.RawLogFlattened AS f WHERE f.RecordId = w.RecordId);

        /* ---- 1. The only two passes over the document ------------------- */

        SELECT
            r.RecordId,

            /* scalars */
            LEFT(COALESCE(t.CreationTime, CONVERT(NVARCHAR(30), r.CreationDate, 126)), 4000)  AS [TimeStamp],
            /* Style 127, not 126: CreationTime carries a trailing Z, which only
               127 accepts. It also parses the unzoned fallback. */
            TRY_CONVERT(DATETIME2(0),
                COALESCE(t.CreationTime, CONVERT(NVARCHAR(30), r.CreationDate, 126)), 127)    AS UtcTimeStamp,
            LEFT(COALESCE(t.JsonUserId, r.UserId), 4000)                                      AS [User],
            LEFT(e.AppHost, 4000)                                                             AS App,
            t.ClientRegion                                                                    AS Location,
            LEFT(t.AppIdentity, 200)                                                          AS AppIdentity,
            LEFT(e.ThreadId, 400)                                                             AS ThreadId,
            /* The agent's id and friendly name sit at the top of the record (2.4.11);
               inside CopilotEventData, where the schema once put them, they win if present. */
            LEFT(COALESCE(NULLIF(e.AgentId, N''), NULLIF(t.AgentId, N'')), 400)                AS AgentId,
            LEFT(COALESCE(NULLIF(e.AgentName, N''), NULLIF(t.AgentName, N'')), 400)            AS AgentName,
            /* ModelTransparencyDetails is a short fragment by the time we read
               it, not the whole record. */
            LEFT(JSON_VALUE(e.ModelTransparencyDetails, '$[0].ModelName'), 200)                AS ModelName,
            LEFT(JSON_VALUE(e.ModelTransparencyDetails, '$[0].ModelProviderName'), 200)        AS ModelProvider,
            LEFT(JSON_VALUE(e.ModelTransparencyDetails, '$[0].ModelVersion'), 32)              AS ModelVersion,

            /* every other scalar (2.2.0): typed, NULL where a value does not parse */
            TRY_CONVERT(SMALLINT, t.RecordType)                                               AS RecordType,
            LEFT(t.Operation, 64)                                                             AS Operation,
            TRY_CONVERT(UNIQUEIDENTIFIER, t.OrganizationId)                                   AS OrganizationId,
            TRY_CONVERT(UNIQUEIDENTIFIER, t.UserKey)                                          AS UserKey,
            TRY_CONVERT(TINYINT, t.UserType)                                                  AS UserType,
            TRY_CONVERT(TINYINT, t.[Version])                                                 AS [Version],
            LEFT(t.Workload, 32)                                                              AS Workload,
            LEFT(t.ClientIP, 64)                                                              AS ClientIP,
            LEFT(t.CopilotLogVersion, 32)                                                     AS CopilotLogVersion,
            LEFT(t.AgentVersion, 32)                                                          AS AgentVersion,
            LEFT(t.ApplicationName, 200)                                                      AS ApplicationName,
            LEFT(t.AgentPlatform, 64)                                                         AS AgentPlatform,
            LEFT(t.PlatformAgentId, 256)                                                      AS PlatformAgentId,
            TRY_CONVERT(UNIQUEIDENTIFIER, t.AgentBlueprintId)                                 AS AgentBlueprintId,
            TRY_CONVERT(TINYINT, t.SubjectType)                                               AS SubjectType,
            LEFT(t.ResultStatus, 64)                                                          AS ResultStatus,
            LEFT(t.AppAccessContext_APIId, 64)                                                AS AppAccessContext_APIId,
            TRY_CONVERT(UNIQUEIDENTIFIER, t.AppAccessContext_ClientAppId)                     AS AppAccessContext_ClientAppId,
            LEFT(r.AssociatedAdminUnits, 4000)                                                AS AssociatedAdminUnits,
            LEFT(r.AssociatedAdminUnitsNames, 4000)                                           AS AssociatedAdminUnitsNames,
            LEFT(e.LicenseType, 32)                                                           AS CopilotEventData_LicenseType,
            LEFT(e.ConversationId, 256)                                                       AS CopilotEventData_ConversationId,
            TRY_CONVERT(UNIQUEIDENTIFIER, e.CorrelationId)                                    AS CopilotEventData_CorrelationId,
            TRY_CONVERT(UNIQUEIDENTIFIER, e.SensitivityLabelId)                               AS CopilotEventData_SensitivityLabelId,
            LEFT(e.TargetAgentName, 200)                                                      AS CopilotEventData_TargetAgentName,
            LEFT(e.TargetPlatformAgentId, 256)                                                AS CopilotEventData_TargetPlatformAgentId,
            TRY_CONVERT(TINYINT, e.DLPEvaluationDeferred)                                     AS CopilotEventData_DLPEvaluationDeferred,
            e.MemoryUpdated                                                                   AS CopilotEventData_MemoryUpdated,
            LEFT(e.MemoryCorrelationId, 64)                                                   AS CopilotEventData_MemoryCorrelationId,
            TRY_CONVERT(UNIQUEIDENTIFIER, e.FabricCapacityId)                                 AS CopilotEventData_FabricCapacityId,
            LEFT(e.PlatformAgentId, 256)                                                      AS CopilotEventData_PlatformAgentId,
            LEFT(e.PlatformAgentType, 64)                                                     AS CopilotEventData_PlatformAgentType,
            e.IsSpawnTask                                                                     AS CopilotEventData_IsSpawnTask,
            TRY_CONVERT(UNIQUEIDENTIFIER, e.TaskId)                                           AS CopilotEventData_TaskId,
            /* the small arrays as JSON text, NULL when empty */
            NULLIF(e.AISystemPlugin, N'[]')                                                   AS CopilotEventData_AISystemPlugin,
            NULLIF(e.ModelTransparencyDetails, N'[]')                                         AS CopilotEventData_ModelTransparencyDetails,
            NULLIF(e.MessageIds, N'[]')                                                       AS CopilotEventData_MessageIds,
            NULLIF(e.GuardrailDetails, N'[]')                                                 AS CopilotEventData_GuardrailDetails,
            NULLIF(e.ParticipatingAgents, N'[]')                                              AS CopilotEventData_ParticipatingAgents,
            NULLIF(e.TargetTaskIds, N'[]')                                                    AS CopilotEventData_TargetTaskIds,
            NULLIF(e.SourceThreadIds, N'[]')                                                  AS CopilotEventData_SourceThreadIds,

            /* nested arrays, carried forward as fragments */
            e.AccessedResources,
            e.AISystemPlugin,
            e.Contexts,
            e.Messages
        INTO #batch
        FROM #raw AS r
        /* A record whose AuditData is absent or not JSON still yields a row,
           with the JSON-derived columns NULL. */
        OUTER APPLY OPENJSON(CASE WHEN ISJSON(r.AuditData) = 1 THEN r.AuditData ELSE N'{}' END)
            WITH (
                CreationTime     NVARCHAR(64)   '$.CreationTime',
                JsonUserId       NVARCHAR(4000) '$.UserId',
                AppIdentity      NVARCHAR(200)  '$.AppIdentity',
                AgentId          NVARCHAR(400)  '$.AgentId',
                AgentName        NVARCHAR(400)  '$.AgentName',
                ClientRegion     NVARCHAR(100)  '$.ClientRegion',
                RecordType       NVARCHAR(20)   '$.RecordType',
                Operation        NVARCHAR(100)  '$.Operation',
                OrganizationId   NVARCHAR(100)  '$.OrganizationId',
                UserKey          NVARCHAR(100)  '$.UserKey',
                UserType         NVARCHAR(20)   '$.UserType',
                [Version]        NVARCHAR(20)   '$.Version',
                Workload         NVARCHAR(100)  '$.Workload',
                ClientIP         NVARCHAR(100)  '$.ClientIP',
                CopilotLogVersion NVARCHAR(100) '$.CopilotLogVersion',
                AgentVersion     NVARCHAR(100)  '$.AgentVersion',
                ApplicationName  NVARCHAR(400)  '$.ApplicationName',
                AgentPlatform    NVARCHAR(100)  '$.AgentPlatform',
                PlatformAgentId  NVARCHAR(400)  '$.PlatformAgentId',
                AgentBlueprintId NVARCHAR(100)  '$.AgentBlueprintId',
                SubjectType      NVARCHAR(20)   '$.SubjectType',
                ResultStatus     NVARCHAR(100)  '$.ResultStatus',
                AppAccessContext_APIId       NVARCHAR(100) '$.AppAccessContext.APIId',
                AppAccessContext_ClientAppId NVARCHAR(100) '$.AppAccessContext.ClientAppId',
                CopilotEventData NVARCHAR(MAX)  '$.CopilotEventData' AS JSON
            ) AS t
        OUTER APPLY OPENJSON(COALESCE(t.CopilotEventData, N'{}'))
            WITH (
                AppHost                  NVARCHAR(4000) '$.AppHost',
                ThreadId                 NVARCHAR(400)  '$.ThreadId',
                AgentId                  NVARCHAR(400)  '$.AgentId',
                AgentName                NVARCHAR(400)  '$.AgentName',
                LicenseType              NVARCHAR(100)  '$.LicenseType',
                ConversationId           NVARCHAR(400)  '$.ConversationId',
                CorrelationId            NVARCHAR(100)  '$.CorrelationId',
                SensitivityLabelId       NVARCHAR(100)  '$.SensitivityLabelId',
                TargetAgentName          NVARCHAR(400)  '$.TargetAgentName',
                TargetPlatformAgentId    NVARCHAR(400)  '$.TargetPlatformAgentId',
                DLPEvaluationDeferred    NVARCHAR(20)   '$.DLPEvaluationDeferred',
                MemoryUpdated            BIT            '$.MemoryUpdated',
                MemoryCorrelationId      NVARCHAR(100)  '$.MemoryCorrelationId',
                FabricCapacityId         NVARCHAR(100)  '$.FabricCapacityId',
                PlatformAgentId          NVARCHAR(400)  '$.PlatformAgentId',
                PlatformAgentType        NVARCHAR(100)  '$.PlatformAgentType',
                IsSpawnTask              BIT            '$.IsSpawnTask',
                TaskId                   NVARCHAR(100)  '$.TaskId',
                ModelTransparencyDetails NVARCHAR(MAX)  '$.ModelTransparencyDetails' AS JSON,
                AccessedResources        NVARCHAR(MAX)  '$.AccessedResources'        AS JSON,
                AISystemPlugin           NVARCHAR(MAX)  '$.AISystemPlugin'           AS JSON,
                Contexts                 NVARCHAR(MAX)  '$.Contexts'                 AS JSON,
                Messages                 NVARCHAR(MAX)  '$.Messages'                 AS JSON,
                MessageIds               NVARCHAR(MAX)  '$.MessageIds'               AS JSON,
                GuardrailDetails         NVARCHAR(MAX)  '$.GuardrailDetails'         AS JSON,
                ParticipatingAgents      NVARCHAR(MAX)  '$.ParticipatingAgents'      AS JSON,
                TargetTaskIds            NVARCHAR(MAX)  '$.TargetTaskIds'            AS JSON,
                SourceThreadIds          NVARCHAR(MAX)  '$.SourceThreadIds'          AS JSON
            ) AS e
        OPTION (MAXDOP 0);

        CREATE CLUSTERED INDEX CIX_batch ON #batch (RecordId);

        /* The fields that never vary: counted by combination, not stored per row. */
        MERGE audit.RawLogRecordShape AS t
        USING (
            SELECT h.ShapeHash, MAX(b.RecordType) AS RecordType, MAX(b.Operation) AS Operation, MAX(b.Workload) AS Workload,
                   MAX(b.UserType) AS UserType, MAX(b.[Version]) AS [Version], MAX(b.CopilotLogVersion) AS CopilotLogVersion,
                   MAX(b.SubjectType) AS SubjectType, MAX(b.AgentVersion) AS AgentVersion, MAX(b.AgentPlatform) AS AgentPlatform,
                   MAX(b.AgentBlueprintId) AS AgentBlueprintId, MAX(b.AppAccessContext_ClientAppId) AS ClientAppId,
                   MAX(b.CopilotEventData_PlatformAgentType) AS PlatformAgentType,
                   CAST(MAX(CAST(b.CopilotEventData_IsSpawnTask AS TINYINT)) AS BIT) AS IsSpawnTask,
                   CAST(MAX(CAST(f.HasAdminUnits AS TINYINT)) AS BIT) AS HasAdminUnits, CAST(MAX(CAST(f.HasMessageIds AS TINYINT)) AS BIT) AS HasMessageIds,
                   CAST(MAX(CAST(f.HasGuardrailDetails AS TINYINT)) AS BIT) AS HasGuardrailDetails, CAST(MAX(CAST(f.HasTargetTaskIds AS TINYINT)) AS BIT) AS HasTargetTaskIds, COUNT_BIG(*) AS Records
            FROM #batch AS b
            CROSS APPLY (SELECT CAST(CASE WHEN NULLIF(b.AssociatedAdminUnits, N'') IS NOT NULL OR NULLIF(b.AssociatedAdminUnitsNames, N'') IS NOT NULL THEN 1 ELSE 0 END AS BIT) AS HasAdminUnits,
                                CAST(CASE WHEN b.CopilotEventData_MessageIds IS NOT NULL THEN 1 ELSE 0 END AS BIT) AS HasMessageIds,
                                CAST(CASE WHEN b.CopilotEventData_GuardrailDetails IS NOT NULL THEN 1 ELSE 0 END AS BIT) AS HasGuardrailDetails,
                                CAST(CASE WHEN b.CopilotEventData_TargetTaskIds IS NOT NULL THEN 1 ELSE 0 END AS BIT) AS HasTargetTaskIds) AS f
            CROSS APPLY (SELECT CAST(HASHBYTES('SHA2_256', CONCAT_WS(N'|', ISNULL(CAST(b.RecordType AS NVARCHAR(10)), N'~'),
                    ISNULL(b.Operation, N'~'), ISNULL(b.Workload, N'~'), ISNULL(CAST(b.UserType AS NVARCHAR(5)), N'~'),
                    ISNULL(CAST(b.[Version] AS NVARCHAR(5)), N'~'), ISNULL(b.CopilotLogVersion, N'~'),
                    ISNULL(CAST(b.SubjectType AS NVARCHAR(5)), N'~'), ISNULL(b.AgentVersion, N'~'), ISNULL(b.AgentPlatform, N'~'),
                    ISNULL(CAST(b.AgentBlueprintId AS NVARCHAR(36)), N'~'), ISNULL(CAST(b.AppAccessContext_ClientAppId AS NVARCHAR(36)), N'~'),
                    ISNULL(b.CopilotEventData_PlatformAgentType, N'~'), ISNULL(CAST(b.CopilotEventData_IsSpawnTask AS NVARCHAR(1)), N'~'),
                    f.HasAdminUnits, f.HasMessageIds, f.HasGuardrailDetails, f.HasTargetTaskIds)) AS BINARY(32)) AS ShapeHash) AS h
            GROUP BY h.ShapeHash
        ) AS s
        ON t.ShapeHash = s.ShapeHash
        WHEN MATCHED THEN UPDATE SET t.Records += s.Records, t.LastSeenUtc = SYSUTCDATETIME()
        WHEN NOT MATCHED THEN INSERT
            (ShapeHash, RecordType, Operation, Workload, UserType, [Version], CopilotLogVersion, SubjectType, AgentVersion,
             AgentPlatform, AgentBlueprintId, AppAccessContext_ClientAppId, CopilotEventData_PlatformAgentType, CopilotEventData_IsSpawnTask,
             HasAdminUnits, HasMessageIds, HasGuardrailDetails, HasTargetTaskIds, Records)
        VALUES (s.ShapeHash, s.RecordType, s.Operation, s.Workload, s.UserType, s.[Version], s.CopilotLogVersion, s.SubjectType,
                s.AgentVersion, s.AgentPlatform, s.AgentBlueprintId, s.ClientAppId, s.PlatformAgentType, s.IsSpawnTask,
                s.HasAdminUnits, s.HasMessageIds, s.HasGuardrailDetails, s.HasTargetTaskIds, s.Records);

        /* ---- 2. AccessedResources, exploded once ------------------------ */

        SELECT b.RecordId,
               CAST(j.[key] AS SMALLINT)                                          AS Ordinal,
               /*
                   A precomputed plain column, because SQL Server only
                   reconciles several ordered aggregates in one scope when the
                   ORDER BY is a bare column reference. Any expression there -
                   LEFT(SiteUrl, 400) included - makes each aggregate's
                   ordering "incompatible" with the others even when the
                   expressions are identical. Materialising the key keeps the
                   sort cheap and the aggregates legal. CONCAT never returns
                   NULL, so the ordering stays deterministic.
               */
               CAST(LEFT(CONCAT(a.SiteUrl, N'|', a.Name), 400) AS NVARCHAR(400)) AS SortKey,
               a.Name,
               a.SiteUrl,
               a.[Action],
               a.SensitivityLabelId,
               a.XPIADetected,
               a.[Status],
               a.[Type],
               a.Id,
               a.ListItemUniqueId,
               /* Real records carry PolicyDetails as JSON text inside the JSON; older
                  shapes as an array. Either way it is kept as text. */
               COALESCE(NULLIF(a.PolicyDetailsText, N''), a.PolicyDetailsJson)   AS PolicyDetails,
               CAST(NULL AS NVARCHAR(400))                                        AS PolicyOutcomes
        INTO #res
        FROM #batch AS b
        CROSS APPLY OPENJSON(COALESCE(b.AccessedResources, N'[]')) AS j
        CROSS APPLY OPENJSON(j.[value])
            WITH (
                Name               NVARCHAR(MAX)  '$.Name',
                SiteUrl            NVARCHAR(MAX)  '$.SiteUrl',
                [Action]           NVARCHAR(4000) '$.Action',
                SensitivityLabelId NVARCHAR(4000) '$.SensitivityLabelId',
                XPIADetected       BIT            '$.XPIADetected',
                [Status]           NVARCHAR(100)  '$.Status',
                [Type]             NVARCHAR(400)  '$.Type',
                Id                 NVARCHAR(2000) '$.Id',
                ListItemUniqueId   NVARCHAR(100)  '$.listItemUniqueId',
                PolicyDetailsText  NVARCHAR(MAX)  '$.PolicyDetails',
                PolicyDetailsJson  NVARCHAR(MAX)  '$.PolicyDetails' AS JSON
            ) AS a
        WHERE j.[type] = 5;   -- objects only

        CREATE CLUSTERED INDEX CIX_res ON #res (RecordId, Ordinal);

        /* Policy outcomes other than None. Nearly every element says None, so
           only the rare ones that say something else are parsed. */
        UPDATE r SET PolicyOutcomes = o.Outcomes
        FROM #res AS r
        CROSS APPLY (
            SELECT LEFT(STRING_AGG(CAST(v.[value] AS NVARCHAR(MAX)), N'; '), 400) AS Outcomes
            FROM OPENJSON(r.PolicyDetails) WITH (PolicyOutcomes NVARCHAR(MAX) '$.PolicyOutcomes' AS JSON) AS pd
            CROSS APPLY OPENJSON(pd.PolicyOutcomes) AS v
            WHERE v.[value] NOT IN (N'None', N'')
        ) AS o
        WHERE r.PolicyDetails LIKE N'%PolicyOutcomes%'
          AND REPLACE(r.PolicyDetails, N'"PolicyOutcomes":["None"]', N'') LIKE N'%PolicyOutcomes%'
          AND ISJSON(r.PolicyDetails) = 1;

        /* ---- 3. Aggregate, all of it relational -------------------------

            Each aggregate is its own statement writing its own temp table,
            not a CTE. Two reasons, and both matter:

              * SQL Server rejects ordered aggregates with differing ORDER BY
                clauses "in the same scope", and CTEs feeding one statement
                count as that scope once they are flattened. Separate
                statements are unambiguously separate scopes, so each list can
                keep the ordering that suits it.
              * A CTE is not materialised, so referencing one repeatedly
                re-runs the OPENJSON underneath it - the cost this rewrite
                exists to remove.

            Everything below reads #batch or #res, which are already parsed.
        */

        SELECT RecordId,
               STRING_AGG(CASE WHEN Name IS NOT NULL THEN CAST(Name AS NVARCHAR(MAX)) END, N'; ')
                   WITHIN GROUP (ORDER BY SortKey)                       AS ResourceNames,
               STRING_AGG(CASE WHEN SiteUrl IS NOT NULL THEN CAST(SiteUrl AS NVARCHAR(MAX)) END, N'; ')
                   WITHIN GROUP (ORDER BY SortKey)                       AS ResourceUrls,
               STRING_AGG(CASE WHEN SiteUrl IS NOT NULL
                                AND (PolicyOutcomes IS NOT NULL
                                     OR ([Status] IS NOT NULL AND [Status] <> N'Success'))
                               THEN CAST(SiteUrl AS NVARCHAR(MAX)) END, N'; ')
                   WITHIN GROUP (ORDER BY SortKey)                       AS BlockedUrls,
               MAX(CASE WHEN XPIADetected = 1 THEN 1 ELSE 0 END)                 AS AnyXpia,
               COUNT(*)                                                          AS ResourceCount
        INTO #resagg
        FROM #res
        GROUP BY RecordId;

        /* STRING_AGG has no DISTINCT, so dedupe first. No JSON is touched. */
        SELECT RecordId,
               STRING_AGG(CAST([Action] AS NVARCHAR(MAX)), N'; ')
                   WITHIN GROUP (ORDER BY [Action])                             AS Actions
        INTO #actagg
        FROM (SELECT DISTINCT RecordId, [Action] FROM #res WHERE [Action] IS NOT NULL) AS d
        GROUP BY RecordId;

        SELECT RecordId,
               STRING_AGG(CAST(SensitivityLabelId AS NVARCHAR(MAX)), N'; ')
                   WITHIN GROUP (ORDER BY SensitivityLabelId)                   AS Labels
        INTO #labagg
        FROM (SELECT DISTINCT RecordId, SensitivityLabelId FROM #res WHERE SensitivityLabelId IS NOT NULL) AS d
        GROUP BY RecordId;

        /* The three short fragments, each exploded once and aggregated set-based
           rather than per row in a correlated subquery. */
        SELECT b.RecordId,
               LEFT(STRING_AGG(CAST(c.Id AS NVARCHAR(MAX)), N'; ')
                   WITHIN GROUP (ORDER BY c.Id)          , 4000)                AS Contexts
        INTO #ctxagg
        FROM #batch AS b
        CROSS APPLY OPENJSON(COALESCE(b.Contexts, N'[]')) WITH (Id NVARCHAR(MAX) '$.Id') AS c
        WHERE c.Id IS NOT NULL
        GROUP BY b.RecordId;

        SELECT RecordId,
               STRING_AGG(CAST(Id AS NVARCHAR(MAX)), N'; ')
                   WITHIN GROUP (ORDER BY Id)                                   AS Plugins
        INTO #plugagg
        FROM (
            SELECT DISTINCT b.RecordId, p.Id
            FROM #batch AS b
            CROSS APPLY OPENJSON(COALESCE(b.AISystemPlugin, N'[]')) WITH (Id NVARCHAR(4000) '$.Id') AS p
            WHERE p.Id IS NOT NULL
        ) AS d
        GROUP BY RecordId;

        /* Messages answers two questions, so it is read once and pivoted. */
        SELECT b.RecordId,
               CASE WHEN MAX(CASE WHEN x.JailbreakDetected = 1 THEN 1 ELSE 0 END) = 1
                    THEN N'true' ELSE N'false' END                               AS JailbreakDetected,
               COUNT(CASE WHEN x.isPrompt = 1 THEN 1 END)                        AS PromptCount,
               COUNT(x.isPrompt)                                                 AS MessageCount
        INTO #msgagg
        FROM #batch AS b
        CROSS APPLY OPENJSON(COALESCE(b.Messages, N'[]'))
             WITH (isPrompt BIT '$.isPrompt', JailbreakDetected BIT '$.JailbreakDetected') AS x
        GROUP BY b.RecordId;

        /* ---- 4. Merge. No aggregates left in this statement. ------------ */

        MERGE audit.RawLogFlattened WITH (HOLDLOCK) AS t
        USING (
            SELECT
                b.RecordId,
                b.[TimeStamp],
                b.UtcTimeStamp,
                b.[User],
                b.App,
                b.Location,
                b.AppIdentity,
                b.ThreadId,
                b.AgentId,
                b.AgentName,
                b.ModelName,
                b.ModelProvider,
                b.ModelVersion,
                b.OrganizationId,
                b.UserKey,
                b.ClientIP,
                b.ApplicationName,
                b.PlatformAgentId,
                b.AppAccessContext_APIId,
                b.ResultStatus,
                b.CopilotEventData_LicenseType,
                b.CopilotEventData_ConversationId,
                b.CopilotEventData_CorrelationId,
                b.CopilotEventData_SensitivityLabelId,
                b.CopilotEventData_TargetAgentName,
                b.CopilotEventData_TargetPlatformAgentId,
                b.CopilotEventData_DLPEvaluationDeferred,
                b.CopilotEventData_MemoryUpdated,
                b.CopilotEventData_MemoryCorrelationId,
                b.CopilotEventData_FabricCapacityId,
                b.CopilotEventData_PlatformAgentId,
                b.CopilotEventData_TaskId,
                b.CopilotEventData_AISystemPlugin,
                b.CopilotEventData_ModelTransparencyDetails,
                b.CopilotEventData_ParticipatingAgents,
                b.CopilotEventData_SourceThreadIds,
                ISNULL(mg.MessageCount, 0) AS MessageCount,
                cx.Contexts                                                      AS [App context],
                ra.ResourceNames                                                 AS [Accessed Resources],
                ra.ResourceUrls                                                  AS [Accessed Resource Locations],
                ra.BlockedUrls                                                   AS BlockedResourceUrls,
                aa.Actions                                                       AS [Action],
                la.Labels                                                        AS SensitivityLabelIds,
                pl.Plugins                                                       AS AIPlugin,
                ISNULL(mg.JailbreakDetected, N'false')                           AS JailbreakDetected,
                ISNULL(mg.PromptCount, 0)                                        AS PromptCount,
                CASE WHEN ISNULL(ra.AnyXpia, 0) = 1 THEN N'true' ELSE N'false' END AS PromptInjectionDetected,
                ISNULL(ra.ResourceCount, 0)                                      AS AccessedResourceCount,
                CASE WHEN @hasLicense = 1 THEN lic.IsCopilotLic END              AS IsCopilotLic
            FROM #batch AS b
            LEFT JOIN #resagg  AS ra ON ra.RecordId = b.RecordId
            LEFT JOIN #actagg  AS aa ON aa.RecordId = b.RecordId
            LEFT JOIN #labagg  AS la ON la.RecordId = b.RecordId
            LEFT JOIN #ctxagg  AS cx ON cx.RecordId = b.RecordId
            LEFT JOIN #plugagg AS pl ON pl.RecordId = b.RecordId
            LEFT JOIN #msgagg  AS mg ON mg.RecordId = b.RecordId
            OUTER APPLY (
                SELECT TOP (1) l.IsCopilotLic
                FROM audit.CopilotLicense AS l
                WHERE @hasLicense = 1 AND l.UserPrincipalName = b.[User]
            ) AS lic
        ) AS s
        ON t.RecordId = s.RecordId
        WHEN MATCHED THEN UPDATE SET
            t.[TimeStamp]                   = s.[TimeStamp],
            t.UtcTimeStamp                  = s.UtcTimeStamp,
            t.[User]                        = s.[User],
            t.App                           = s.App,
            t.Location                      = s.Location,
            t.[App context]                 = s.[App context],
            t.[Accessed Resources]          = s.[Accessed Resources],
            t.[Accessed Resource Locations] = s.[Accessed Resource Locations],
            t.[Action]                      = s.[Action],
            t.AIPlugin                      = s.AIPlugin,
            t.JailbreakDetected             = s.JailbreakDetected,
            t.PromptInjectionDetected       = s.PromptInjectionDetected,
            t.BlockedResourceUrls           = s.BlockedResourceUrls,
            t.SensitivityLabelIds           = s.SensitivityLabelIds,
            t.IsCopilotLic                  = s.IsCopilotLic,
            t.AppIdentity                   = s.AppIdentity,
            t.ThreadId                      = s.ThreadId,
            t.AgentId                       = s.AgentId,
            t.AgentName                     = s.AgentName,
            t.ModelName                     = s.ModelName,
            t.ModelProvider                 = s.ModelProvider,
            t.AccessedResourceCount         = s.AccessedResourceCount,
            t.PromptCount                   = s.PromptCount,
            t.ModelVersion = s.ModelVersion,
            t.OrganizationId = s.OrganizationId,
            t.UserKey = s.UserKey,
            t.ClientIP = s.ClientIP,
            t.ApplicationName = s.ApplicationName,
            t.PlatformAgentId = s.PlatformAgentId,
            t.AppAccessContext_APIId = s.AppAccessContext_APIId,
            t.ResultStatus = s.ResultStatus,
            t.CopilotEventData_LicenseType = s.CopilotEventData_LicenseType,
            t.CopilotEventData_ConversationId = s.CopilotEventData_ConversationId,
            t.CopilotEventData_CorrelationId = s.CopilotEventData_CorrelationId,
            t.CopilotEventData_SensitivityLabelId = s.CopilotEventData_SensitivityLabelId,
            t.CopilotEventData_TargetAgentName = s.CopilotEventData_TargetAgentName,
            t.CopilotEventData_TargetPlatformAgentId = s.CopilotEventData_TargetPlatformAgentId,
            t.CopilotEventData_DLPEvaluationDeferred = s.CopilotEventData_DLPEvaluationDeferred,
            t.CopilotEventData_MemoryUpdated = s.CopilotEventData_MemoryUpdated,
            t.CopilotEventData_MemoryCorrelationId = s.CopilotEventData_MemoryCorrelationId,
            t.CopilotEventData_FabricCapacityId = s.CopilotEventData_FabricCapacityId,
            t.CopilotEventData_PlatformAgentId = s.CopilotEventData_PlatformAgentId,
            t.CopilotEventData_TaskId = s.CopilotEventData_TaskId,
            t.CopilotEventData_AISystemPlugin = s.CopilotEventData_AISystemPlugin,
            t.CopilotEventData_ModelTransparencyDetails = s.CopilotEventData_ModelTransparencyDetails,
            t.CopilotEventData_ParticipatingAgents = s.CopilotEventData_ParticipatingAgents,
            t.CopilotEventData_SourceThreadIds = s.CopilotEventData_SourceThreadIds,
            t.MessageCount = s.MessageCount,
            t.FlattenedUtc                  = SYSUTCDATETIME()
        WHEN NOT MATCHED BY TARGET THEN INSERT
        (
            RecordId, [TimeStamp], UtcTimeStamp, [User], App, Location, [App context],
            [Accessed Resources], [Accessed Resource Locations], [Action], AIPlugin,
            JailbreakDetected, PromptInjectionDetected, BlockedResourceUrls, SensitivityLabelIds,
            IsCopilotLic, AppIdentity, ThreadId, AgentId, AgentName, ModelName, ModelProvider,
            AccessedResourceCount, PromptCount,
            ModelVersion, OrganizationId, UserKey, ClientIP, ApplicationName, PlatformAgentId, AppAccessContext_APIId, ResultStatus, CopilotEventData_LicenseType, CopilotEventData_ConversationId, CopilotEventData_CorrelationId, CopilotEventData_SensitivityLabelId, CopilotEventData_TargetAgentName, CopilotEventData_TargetPlatformAgentId, CopilotEventData_DLPEvaluationDeferred, CopilotEventData_MemoryUpdated, CopilotEventData_MemoryCorrelationId, CopilotEventData_FabricCapacityId, CopilotEventData_PlatformAgentId, CopilotEventData_TaskId, CopilotEventData_AISystemPlugin, CopilotEventData_ModelTransparencyDetails, CopilotEventData_ParticipatingAgents, CopilotEventData_SourceThreadIds, MessageCount
        )
        VALUES
        (
            s.RecordId, s.[TimeStamp], s.UtcTimeStamp, s.[User], s.App, s.Location, s.[App context],
            s.[Accessed Resources], s.[Accessed Resource Locations], s.[Action], s.AIPlugin,
            s.JailbreakDetected, s.PromptInjectionDetected, s.BlockedResourceUrls, s.SensitivityLabelIds,
            s.IsCopilotLic, s.AppIdentity, s.ThreadId, s.AgentId, s.AgentName, s.ModelName, s.ModelProvider,
            s.AccessedResourceCount, s.PromptCount,
            s.ModelVersion, s.OrganizationId, s.UserKey, s.ClientIP, s.ApplicationName, s.PlatformAgentId, s.AppAccessContext_APIId, s.ResultStatus, s.CopilotEventData_LicenseType, s.CopilotEventData_ConversationId, s.CopilotEventData_CorrelationId, s.CopilotEventData_SensitivityLabelId, s.CopilotEventData_TargetAgentName, s.CopilotEventData_TargetPlatformAgentId, s.CopilotEventData_DLPEvaluationDeferred, s.CopilotEventData_MemoryUpdated, s.CopilotEventData_MemoryCorrelationId, s.CopilotEventData_FabricCapacityId, s.CopilotEventData_PlatformAgentId, s.CopilotEventData_TaskId, s.CopilotEventData_AISystemPlugin, s.CopilotEventData_ModelTransparencyDetails, s.CopilotEventData_ParticipatingAgents, s.CopilotEventData_SourceThreadIds, s.MessageCount
        );

        SET @rows = @@ROWCOUNT;

        /* ---- 5. The element tables: this batch's rows replaced ---------- */

        DELETE ar FROM audit.RawLogAccessedResource AS ar JOIN #batch AS b ON b.RecordId = ar.RecordId;
        INSERT INTO audit.RawLogAccessedResource
            (RecordId, Ordinal, [Action], [Type], Id, Name, SiteUrl, Site, ListItemUniqueId, SensitivityLabelId,
             XPIADetected, [Status], PolicyDetails, PolicyOutcomes, IsBlocked)
        SELECT r.RecordId, r.Ordinal, LEFT(r.[Action], 450), LEFT(r.[Type], 200), LEFT(r.Id, 1000), LEFT(r.Name, 4000),
               NULLIF(r.SiteUrl, N''), LEFT(NULLIF(st.Site, N''), 400),
               TRY_CONVERT(UNIQUEIDENTIFIER, r.ListItemUniqueId), TRY_CONVERT(UNIQUEIDENTIFIER, r.SensitivityLabelId),
               r.XPIADetected, LEFT(r.[Status], 64), NULLIF(r.PolicyDetails, N''), r.PolicyOutcomes,
               CASE WHEN r.PolicyOutcomes IS NOT NULL OR (r.[Status] IS NOT NULL AND r.[Status] <> N'Success') THEN 1 ELSE 0 END
        FROM #res AS r
        /* The site: https://host/sites/name (or /personal/, /teams/), otherwise https://host. */
        CROSS APPLY (SELECT CHARINDEX(N'/', r.SiteUrl + N'/', 9) AS p) AS p1
        CROSS APPLY (SELECT CASE WHEN p1.p = 0 THEN 0 ELSE CHARINDEX(N'/', r.SiteUrl + N'/', p1.p + 1) END AS p) AS p2
        CROSS APPLY (SELECT CASE WHEN p2.p = 0 THEN 0 ELSE CHARINDEX(N'/', r.SiteUrl + N'/', p2.p + 1) END AS p) AS p3
        CROSS APPLY (SELECT CASE WHEN r.SiteUrl IS NULL THEN NULL
                                 WHEN SUBSTRING(r.SiteUrl, p1.p, 10) LIKE N'/sites/%' OR SUBSTRING(r.SiteUrl, p1.p, 10) LIKE N'/teams/%' OR SUBSTRING(r.SiteUrl, p1.p, 10) = N'/personal/'
                                      THEN CASE WHEN p3.p = 0 THEN r.SiteUrl ELSE LEFT(r.SiteUrl, p3.p - 1) END
                                 ELSE CASE WHEN p1.p = 0 THEN r.SiteUrl ELSE LEFT(r.SiteUrl, p1.p - 1) END END AS Site) AS st;

        DELETE rc FROM audit.RawLogContext AS rc JOIN #batch AS b ON b.RecordId = rc.RecordId;
        INSERT INTO audit.RawLogContext (RecordId, Ordinal, Id, [Type], ContainerId)
        SELECT b.RecordId, CAST(j.[key] AS SMALLINT), LEFT(c.Id, 4000), LEFT(c.[Type], 64), LEFT(c.ContainerId, 64)
        FROM #batch AS b
        CROSS APPLY OPENJSON(COALESCE(b.Contexts, N'[]')) AS j
        CROSS APPLY OPENJSON(j.[value]) WITH (Id NVARCHAR(MAX) '$.Id', [Type] NVARCHAR(200) '$.Type', ContainerId NVARCHAR(200) '$.ContainerId') AS c
        WHERE j.[type] = 5;

        /* This batch's values into the catalog, for Admin > Value names. */
        IF OBJECT_ID(N'audit.usp_CatalogValues', N'P') IS NOT NULL EXEC audit.usp_CatalogValues;

        /* And its agents, with the latest friendly name the log gave each (2.4.11). */
        EXEC audit.usp_CatalogAgents;

        SET @done += @rows;
        SET @cursor = @maxId;

        DROP TABLE IF EXISTS #batch;
        DROP TABLE IF EXISTS #raw;
        DROP TABLE IF EXISTS #res;
        DROP TABLE IF EXISTS #resagg;
        DROP TABLE IF EXISTS #actagg;
        DROP TABLE IF EXISTS #labagg;
        DROP TABLE IF EXISTS #ctxagg;
        DROP TABLE IF EXISTS #plugagg;
        DROP TABLE IF EXISTS #msgagg;

        SET @msg = CONCAT(N'  batch ', @batch, N': ', FORMAT(@rows, 'N0'), N' record(s), ',
                          FORMAT(@done, 'N0'), N' so far.');
        RAISERROR(@msg, 0, 1) WITH NOWAIT;
        SET @batch += 1;
    END;

    IF @hasLicense = 0
        RAISERROR('audit.CopilotLicense does not exist - IsCopilotLic left NULL.', 0, 1) WITH NOWAIT;

    /* Present or absent is expected (several appear only with an agent); a second value is news. */
    DECLARE @varying NVARCHAR(400);
    SELECT @varying = CONCAT_WS(N', ',
        CASE WHEN COUNT(DISTINCT RecordType) > 1 THEN N'RecordType' END, CASE WHEN COUNT(DISTINCT Operation) > 1 THEN N'Operation' END,
        CASE WHEN COUNT(DISTINCT Workload) > 1 THEN N'Workload' END, CASE WHEN COUNT(DISTINCT UserType) > 1 THEN N'UserType' END,
        CASE WHEN COUNT(DISTINCT [Version]) > 1 THEN N'Version' END, CASE WHEN COUNT(DISTINCT CopilotLogVersion) > 1 THEN N'CopilotLogVersion' END,
        CASE WHEN COUNT(DISTINCT SubjectType) > 1 THEN N'SubjectType' END, CASE WHEN COUNT(DISTINCT AgentVersion) > 1 THEN N'AgentVersion' END,
        CASE WHEN COUNT(DISTINCT AgentPlatform) > 1 THEN N'AgentPlatform' END, CASE WHEN COUNT(DISTINCT AgentBlueprintId) > 1 THEN N'AgentBlueprintId' END,
        CASE WHEN COUNT(DISTINCT AppAccessContext_ClientAppId) > 1 THEN N'AppAccessContext.ClientAppId' END,
        CASE WHEN COUNT(DISTINCT CopilotEventData_PlatformAgentType) > 1 THEN N'CopilotEventData.PlatformAgentType' END,
        CASE WHEN COUNT(DISTINCT CAST(CopilotEventData_IsSpawnTask AS TINYINT)) > 1 THEN N'CopilotEventData.IsSpawnTask' END,
        CASE WHEN MAX(CAST(HasAdminUnits AS TINYINT)) = 1 THEN N'AssociatedAdminUnits' END,
        CASE WHEN MAX(CAST(HasMessageIds AS TINYINT)) = 1 THEN N'CopilotEventData.MessageIds' END,
        CASE WHEN MAX(CAST(HasGuardrailDetails AS TINYINT)) = 1 THEN N'CopilotEventData.GuardrailDetails' END,
        CASE WHEN MAX(CAST(HasTargetTaskIds AS TINYINT)) = 1 THEN N'CopilotEventData.TargetTaskIds' END)
    FROM audit.RawLogRecordShape;
    IF @varying <> N''
    BEGIN
        SET @msg = CONCAT(N'Note: ', @varying, N' now carry information - a second value, or something where there was ',
                          N'always nothing (audit.RawLogRecordShape). They are not stored per row; if it matters, it needs a column.');
        RAISERROR(@msg, 0, 1) WITH NOWAIT;
    END;

    /* Every record was flattened by this version: none is left for audit.usp_BackfillAgentNames to read, either way.
       And the mark that every record has been flattened (2.4.12): Install-CopilotAudit.ps1 reads it, where it read
       "the flattened table has rows", which a first flatten stopped part way, or a nightly's few days, also gave. */
    IF @SinceUtc IS NULL AND OBJECT_ID(N'audit.PipelineSetting', N'U') IS NOT NULL
    BEGIN
        IF NOT EXISTS (SELECT 1 FROM audit.PipelineSetting WHERE Name = N'AgentNamesReadUtc')
            INSERT INTO audit.PipelineSetting (Name, Value) VALUES (N'AgentNamesReadUtc', CONVERT(NVARCHAR(30), SYSUTCDATETIME(), 126));
        IF NOT EXISTS (SELECT 1 FROM audit.PipelineSetting WHERE Name = N'AgentIdsReadUtc')
            INSERT INTO audit.PipelineSetting (Name, Value) VALUES (N'AgentIdsReadUtc', CONVERT(NVARCHAR(30), SYSUTCDATETIME(), 126));
        /* Nor is anything left of a read that stopped part way (2.4.12). */
        DELETE FROM audit.PipelineSetting WHERE Name IN (N'AgentNamesReadUtcCursor', N'AgentNamesReadUtcGained', N'AgentIdsReadUtcCursor', N'AgentIdsReadUtcGained');
        UPDATE audit.PipelineSetting SET Value = CONVERT(NVARCHAR(30), SYSUTCDATETIME(), 126), UpdatedUtc = SYSUTCDATETIME() WHERE Name = N'FullFlattenUtc';
        IF @@ROWCOUNT = 0 INSERT INTO audit.PipelineSetting (Name, Value) VALUES (N'FullFlattenUtc', CONVERT(NVARCHAR(30), SYSUTCDATETIME(), 126));
    END;

    SET @msg = CONCAT(N'Flatten complete: ', FORMAT(@done, 'N0'), N' record(s) in audit.RawLogFlattened.');
    RAISERROR(@msg, 0, 1) WITH NOWAIT;
END;
GO
