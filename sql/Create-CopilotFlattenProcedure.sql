/*
    Copilot audit backfill - audit.usp_FlattenCopilotAudit
    ------------------------------------------------------------------------
    Shreds the CopilotEventData JSON into audit.RawLogFlattened, one row per
    audit record.

    Reads audit.vCopilotAuditRecord, which has the Purview export's column
    shape (RecordId, CreationDate, RecordType, Operation, UserId, AuditData,
    AssociatedAdminUnits, AssociatedAdminUnitsNames). Any table with those
    columns works, so to flatten a portal CSV import instead, change the one
    FROM in the #batch load below to your own raw-log table.

    Parameters:
      @SinceUtc     only records at or after this UTC time. NULL = everything.
      @RebuildAll   1 truncates and rebuilds; 0 (default) MERGEs, so it is
                    safe to run incrementally on a schedule.
      @BatchSize    records per batch. Keeps the log, tempdb and lock
                    footprint flat on tens of millions of rows. Default
                    200,000.

    Re-runnable and idempotent: a record already flattened is updated in place,
    keyed on RecordId.

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
    DECLARE @hasLicence BIT = CASE WHEN OBJECT_ID(N'audit.CopilotLicense', N'U') IS NULL THEN 0 ELSE 1 END;

    IF @RebuildAll = 1
    BEGIN
        RAISERROR('Rebuilding audit.RawLogFlattened from scratch.', 0, 1) WITH NOWAIT;
        TRUNCATE TABLE audit.RawLogFlattened;
        SET @SinceUtc = NULL;
    END;

    /*
        Work through the records in RecordId order, @BatchSize at a time. The
        cursor is the last RecordId done, so a batch that fails can be resumed
        by re-running: already-flattened records simply MERGE to themselves.
    */
    DECLARE @cursor NVARCHAR(450) = N'';
    DECLARE @maxId  NVARCHAR(450);

    WHILE 1 = 1
    BEGIN
        SET @maxId = NULL;

        ;WITH nextBatch AS
        (
            SELECT TOP (@BatchSize) r.RecordId
            FROM audit.vCopilotAuditRecord AS r
            WHERE r.RecordId > @cursor
              AND (@SinceUtc IS NULL OR r.CreationDate >= @SinceUtc)
            ORDER BY r.RecordId
        )
        SELECT @maxId = MAX(RecordId) FROM nextBatch;

        IF @maxId IS NULL BREAK;   -- nothing left

        DROP TABLE IF EXISTS #batch;
        DROP TABLE IF EXISTS #res;
        DROP TABLE IF EXISTS #resagg;
        DROP TABLE IF EXISTS #actagg;
        DROP TABLE IF EXISTS #labagg;
        DROP TABLE IF EXISTS #ctxagg;
        DROP TABLE IF EXISTS #plugagg;
        DROP TABLE IF EXISTS #msgagg;

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
            LEFT(e.AgentId, 400)                                                              AS AgentId,
            LEFT(e.AgentName, 400)                                                            AS AgentName,
            /* ModelTransparencyDetails is a short fragment by the time we read
               it, not the whole record. */
            LEFT(JSON_VALUE(e.ModelTransparencyDetails, '$[0].ModelName'), 200)                AS ModelName,
            LEFT(JSON_VALUE(e.ModelTransparencyDetails, '$[0].ModelProviderName'), 200)        AS ModelProvider,

            /* nested arrays, carried forward as fragments */
            e.AccessedResources,
            e.AISystemPlugin,
            e.Contexts,
            e.Messages
        INTO #batch
        FROM audit.vCopilotAuditRecord AS r
        /* A record whose AuditData is absent or not JSON still yields a row,
           with the JSON-derived columns NULL. */
        OUTER APPLY OPENJSON(CASE WHEN ISJSON(r.AuditData) = 1 THEN r.AuditData ELSE N'{}' END)
            WITH (
                CreationTime     NVARCHAR(64)   '$.CreationTime',
                JsonUserId       NVARCHAR(4000) '$.UserId',
                AppIdentity      NVARCHAR(200)  '$.AppIdentity',
                ClientRegion     NVARCHAR(100)  '$.ClientRegion',
                CopilotEventData NVARCHAR(MAX)  '$.CopilotEventData' AS JSON
            ) AS t
        OUTER APPLY OPENJSON(COALESCE(t.CopilotEventData, N'{}'))
            WITH (
                AppHost                  NVARCHAR(4000) '$.AppHost',
                ThreadId                 NVARCHAR(400)  '$.ThreadId',
                AgentId                  NVARCHAR(400)  '$.AgentId',
                AgentName                NVARCHAR(400)  '$.AgentName',
                ModelTransparencyDetails NVARCHAR(MAX)  '$.ModelTransparencyDetails' AS JSON,
                AccessedResources        NVARCHAR(MAX)  '$.AccessedResources'        AS JSON,
                AISystemPlugin           NVARCHAR(MAX)  '$.AISystemPlugin'           AS JSON,
                Contexts                 NVARCHAR(MAX)  '$.Contexts'                 AS JSON,
                Messages                 NVARCHAR(MAX)  '$.Messages'                 AS JSON
            ) AS e
        WHERE r.RecordId > @cursor AND r.RecordId <= @maxId
          AND (@SinceUtc IS NULL OR r.CreationDate >= @SinceUtc);

        CREATE CLUSTERED INDEX CIX_batch ON #batch (RecordId);

        /* ---- 2. AccessedResources, exploded once ------------------------ */

        SELECT b.RecordId,
               a.Name,
               a.SiteUrl,
               a.[Action],
               a.SensitivityLabelId,
               a.XPIADetected,
               a.[Status],
               a.PolicyDetails
        INTO #res
        FROM #batch AS b
        CROSS APPLY OPENJSON(COALESCE(b.AccessedResources, N'[]'))
            WITH (
                Name               NVARCHAR(MAX)  '$.Name',
                SiteUrl            NVARCHAR(MAX)  '$.SiteUrl',
                [Action]           NVARCHAR(4000) '$.Action',
                SensitivityLabelId NVARCHAR(4000) '$.SensitivityLabelId',
                XPIADetected       BIT            '$.XPIADetected',
                [Status]           NVARCHAR(100)  '$.Status',
                PolicyDetails      NVARCHAR(MAX)  '$.PolicyDetails' AS JSON
            ) AS a;

        CREATE CLUSTERED INDEX CIX_res ON #res (RecordId);

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
                   WITHIN GROUP (ORDER BY LEFT(SiteUrl, 400), LEFT(Name, 400))   AS ResourceNames,
               STRING_AGG(CASE WHEN SiteUrl IS NOT NULL THEN CAST(SiteUrl AS NVARCHAR(MAX)) END, N'; ')
                   WITHIN GROUP (ORDER BY LEFT(SiteUrl, 400), LEFT(Name, 400))   AS ResourceUrls,
               STRING_AGG(CASE WHEN SiteUrl IS NOT NULL
                                AND (PolicyDetails IS NOT NULL
                                     OR ([Status] IS NOT NULL AND [Status] <> N'Success'))
                               THEN CAST(SiteUrl AS NVARCHAR(MAX)) END, N'; ')
                   WITHIN GROUP (ORDER BY LEFT(SiteUrl, 400), LEFT(Name, 400))   AS BlockedUrls,
               MAX(CASE WHEN XPIADetected = 1 THEN 1 ELSE 0 END)                 AS AnyXpia,
               COUNT(*)                                                          AS ResourceCount
        INTO #resagg
        FROM #res
        GROUP BY RecordId;

        /* STRING_AGG has no DISTINCT, so dedupe first. No JSON is touched. */
        SELECT RecordId,
               STRING_AGG(CAST([Action] AS NVARCHAR(MAX)), N'; ')
                   WITHIN GROUP (ORDER BY LEFT([Action], 400))                   AS Actions
        INTO #actagg
        FROM (SELECT DISTINCT RecordId, [Action] FROM #res WHERE [Action] IS NOT NULL) AS d
        GROUP BY RecordId;

        SELECT RecordId,
               STRING_AGG(CAST(SensitivityLabelId AS NVARCHAR(MAX)), N'; ')
                   WITHIN GROUP (ORDER BY LEFT(SensitivityLabelId, 400))         AS Labels
        INTO #labagg
        FROM (SELECT DISTINCT RecordId, SensitivityLabelId FROM #res WHERE SensitivityLabelId IS NOT NULL) AS d
        GROUP BY RecordId;

        /* The three short fragments, each exploded once and aggregated set-based
           rather than per row in a correlated subquery. */
        SELECT b.RecordId,
               LEFT(STRING_AGG(CAST(c.Id AS NVARCHAR(MAX)), N'; ')
                   WITHIN GROUP (ORDER BY LEFT(c.Id, 400)), 4000)                AS Contexts
        INTO #ctxagg
        FROM #batch AS b
        CROSS APPLY OPENJSON(COALESCE(b.Contexts, N'[]')) WITH (Id NVARCHAR(MAX) '$.Id') AS c
        WHERE c.Id IS NOT NULL
        GROUP BY b.RecordId;

        SELECT RecordId,
               STRING_AGG(CAST(Id AS NVARCHAR(MAX)), N'; ')
                   WITHIN GROUP (ORDER BY LEFT(Id, 400))                         AS Plugins
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
               COUNT(CASE WHEN x.isPrompt = 1 THEN 1 END)                        AS PromptCount
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
                CASE WHEN @hasLicence = 1 THEN lic.IsCopilotLic END              AS IsCopilotLic
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
                WHERE @hasLicence = 1 AND l.UserPrincipalName = b.[User]
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
            t.FlattenedUtc                  = SYSUTCDATETIME()
        WHEN NOT MATCHED BY TARGET THEN INSERT
        (
            RecordId, [TimeStamp], UtcTimeStamp, [User], App, Location, [App context],
            [Accessed Resources], [Accessed Resource Locations], [Action], AIPlugin,
            JailbreakDetected, PromptInjectionDetected, BlockedResourceUrls, SensitivityLabelIds,
            IsCopilotLic, AppIdentity, ThreadId, AgentId, AgentName, ModelName, ModelProvider,
            AccessedResourceCount, PromptCount
        )
        VALUES
        (
            s.RecordId, s.[TimeStamp], s.UtcTimeStamp, s.[User], s.App, s.Location, s.[App context],
            s.[Accessed Resources], s.[Accessed Resource Locations], s.[Action], s.AIPlugin,
            s.JailbreakDetected, s.PromptInjectionDetected, s.BlockedResourceUrls, s.SensitivityLabelIds,
            s.IsCopilotLic, s.AppIdentity, s.ThreadId, s.AgentId, s.AgentName, s.ModelName, s.ModelProvider,
            s.AccessedResourceCount, s.PromptCount
        );

        SET @rows = @@ROWCOUNT;
        SET @done += @rows;
        SET @cursor = @maxId;

        DROP TABLE IF EXISTS #batch;
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

    IF @hasLicence = 0
        RAISERROR('audit.CopilotLicense does not exist - IsCopilotLic left NULL.', 0, 1) WITH NOWAIT;

    SET @msg = CONCAT(N'Flatten complete: ', FORMAT(@done, 'N0'), N' record(s) in audit.RawLogFlattened.');
    RAISERROR(@msg, 0, 1) WITH NOWAIT;
END;
GO
