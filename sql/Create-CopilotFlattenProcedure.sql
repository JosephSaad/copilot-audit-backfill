/*
    Copilot audit backfill - audit.usp_FlattenCopilotAudit
    ------------------------------------------------------------------------
    Shreds the CopilotEventData JSON into audit.RawLogFlattened, one row per
    audit record.

    Reads audit.vCopilotAuditRecord, which has the Purview export's column
    shape (RecordId, CreationDate, RecordType, Operation, UserId, AuditData,
    AssociatedAdminUnits, AssociatedAdminUnitsNames). Any table with those
    columns works, so to flatten a portal CSV import instead, change the one
    FROM below to your own raw-log table.

    Parameters:
      @SinceUtc     only records at or after this UTC time. NULL = everything.
      @RebuildAll   1 truncates and rebuilds; 0 (default) MERGEs, so it is
                    safe to run incrementally on a schedule.
      @BatchSize    records per MERGE. Keeps the log and lock footprint flat
                    on tens of millions of rows. Default 200,000.

    Re-runnable and idempotent: a record already flattened is updated in place,
    keyed on RecordId.

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

        ;WITH src AS
        (
            SELECT r.RecordId,
                   r.CreationDate,
                   r.UserId,
                   /* ISJSON guards a record whose AuditData is absent or not
                      JSON - JSON_VALUE would error under strict settings.   */
                   CASE WHEN ISJSON(r.AuditData) = 1 THEN r.AuditData END AS Doc
            FROM audit.vCopilotAuditRecord AS r
            WHERE r.RecordId > @cursor AND r.RecordId <= @maxId
              AND (@SinceUtc IS NULL OR r.CreationDate >= @SinceUtc)
        ),
        shredded AS
        (
            SELECT
                s.RecordId,

                /* ---- scalars ------------------------------------------- */
                LEFT(COALESCE(JSON_VALUE(s.Doc, '$.CreationTime'),
                              CONVERT(NVARCHAR(30), s.CreationDate, 126)), 4000)      AS [TimeStamp],
                /* Style 127, not 126: the audit record's CreationTime carries a
                   trailing Z, which only 127 accepts. It also parses the
                   unzoned fallback below. */
                TRY_CONVERT(DATETIME2(0),
                    COALESCE(JSON_VALUE(s.Doc, '$.CreationTime'),
                             CONVERT(NVARCHAR(30), s.CreationDate, 126)), 127)        AS UtcTimeStamp,
                LEFT(COALESCE(JSON_VALUE(s.Doc, '$.UserId'), s.UserId), 4000)         AS [User],
                LEFT(JSON_VALUE(s.Doc, '$.CopilotEventData.AppHost'), 4000)           AS App,
                JSON_VALUE(s.Doc, '$.ClientRegion')                                   AS Location,
                LEFT(JSON_VALUE(s.Doc, '$.AppIdentity'), 200)                         AS AppIdentity,
                LEFT(JSON_VALUE(s.Doc, '$.CopilotEventData.ThreadId'), 400)           AS ThreadId,
                LEFT(JSON_VALUE(s.Doc, '$.CopilotEventData.AgentId'), 400)            AS AgentId,
                LEFT(JSON_VALUE(s.Doc, '$.CopilotEventData.AgentName'), 400)          AS AgentName,
                LEFT(JSON_VALUE(s.Doc, '$.CopilotEventData.ModelTransparencyDetails[0].ModelName'), 200)         AS ModelName,
                LEFT(JSON_VALUE(s.Doc, '$.CopilotEventData.ModelTransparencyDetails[0].ModelProviderName'), 200) AS ModelProvider,

                /* ---- Contexts: where the user was --------------------- */
                (SELECT LEFT(STRING_AGG(CAST(c.Id AS NVARCHAR(MAX)), N'; ')
                                WITHIN GROUP (ORDER BY c.Id), 4000)
                 FROM OPENJSON(s.Doc, '$.CopilotEventData.Contexts')
                      WITH (Id NVARCHAR(MAX) '$.Id') AS c
                 WHERE c.Id IS NOT NULL)                                              AS [App context],

                /* ---- AccessedResources: names, locations, actions ------ */
                (SELECT STRING_AGG(CAST(a.Name AS NVARCHAR(MAX)), N'; ')
                                WITHIN GROUP (ORDER BY a.Name)
                 FROM OPENJSON(s.Doc, '$.CopilotEventData.AccessedResources')
                      WITH (Name NVARCHAR(MAX) '$.Name') AS a
                 WHERE a.Name IS NOT NULL)                                            AS [Accessed Resources],

                (SELECT STRING_AGG(CAST(a.SiteUrl AS NVARCHAR(MAX)), N'; ')
                                WITHIN GROUP (ORDER BY a.SiteUrl)
                 FROM OPENJSON(s.Doc, '$.CopilotEventData.AccessedResources')
                      WITH (SiteUrl NVARCHAR(MAX) '$.SiteUrl') AS a
                 WHERE a.SiteUrl IS NOT NULL)                                         AS [Accessed Resource Locations],

                /* Distinct actions, so a 40-file interaction does not
                   produce 'Read; Read; Read; ...'.                        */
                (SELECT STRING_AGG(CAST(d.Action AS NVARCHAR(MAX)), N'; ')
                                WITHIN GROUP (ORDER BY d.Action)
                 FROM (SELECT DISTINCT a.Action
                       FROM OPENJSON(s.Doc, '$.CopilotEventData.AccessedResources')
                            WITH (Action NVARCHAR(MAX) '$.Action') AS a
                       WHERE a.Action IS NOT NULL) AS d)                              AS [Action],

                (SELECT STRING_AGG(CAST(d.Id AS NVARCHAR(MAX)), N'; ')
                                WITHIN GROUP (ORDER BY d.Id)
                 FROM (SELECT DISTINCT p.Id
                       FROM OPENJSON(s.Doc, '$.CopilotEventData.AISystemPlugin')
                            WITH (Id NVARCHAR(MAX) '$.Id') AS p
                       WHERE p.Id IS NOT NULL) AS d)                                  AS AIPlugin,

                (SELECT STRING_AGG(CAST(d.SensitivityLabelId AS NVARCHAR(MAX)), N'; ')
                                WITHIN GROUP (ORDER BY d.SensitivityLabelId)
                 FROM (SELECT DISTINCT a.SensitivityLabelId
                       FROM OPENJSON(s.Doc, '$.CopilotEventData.AccessedResources')
                            WITH (SensitivityLabelId NVARCHAR(MAX) '$.SensitivityLabelId') AS a
                       WHERE a.SensitivityLabelId IS NOT NULL) AS d)                  AS SensitivityLabelIds,

                /* Resources Copilot was blocked or restricted from: a
                   PolicyDetails entry, or a non-success Status.            */
                (SELECT STRING_AGG(CAST(a.SiteUrl AS NVARCHAR(MAX)), N'; ')
                                WITHIN GROUP (ORDER BY a.SiteUrl)
                 FROM OPENJSON(s.Doc, '$.CopilotEventData.AccessedResources')
                      WITH (SiteUrl       NVARCHAR(MAX) '$.SiteUrl',
                            [Status]      NVARCHAR(100) '$.Status',
                            PolicyDetails NVARCHAR(MAX) '$.PolicyDetails' AS JSON) AS a
                 WHERE a.SiteUrl IS NOT NULL
                   AND (a.PolicyDetails IS NOT NULL
                        OR (a.[Status] IS NOT NULL AND a.[Status] <> N'Success')))    AS BlockedResourceUrls,

                /* ---- boolean flags, as text to match the source shape --- */
                CASE WHEN EXISTS (SELECT 1
                                  FROM OPENJSON(s.Doc, '$.CopilotEventData.Messages')
                                       WITH (JailbreakDetected BIT '$.JailbreakDetected') AS m
                                  WHERE m.JailbreakDetected = 1)
                     THEN N'true' ELSE N'false' END                                   AS JailbreakDetected,

                CASE WHEN EXISTS (SELECT 1
                                  FROM OPENJSON(s.Doc, '$.CopilotEventData.AccessedResources')
                                       WITH (XPIADetected BIT '$.XPIADetected') AS a
                                  WHERE a.XPIADetected = 1)
                     THEN N'true' ELSE N'false' END                                   AS PromptInjectionDetected,

                /* ---- counts -------------------------------------------- */
                (SELECT COUNT(*) FROM OPENJSON(s.Doc, '$.CopilotEventData.AccessedResources')) AS AccessedResourceCount,
                (SELECT COUNT(*) FROM OPENJSON(s.Doc, '$.CopilotEventData.Messages')
                                      WITH (isPrompt BIT '$.isPrompt') AS m
                  WHERE m.isPrompt = 1)                                               AS PromptCount
            FROM src AS s
        )
        MERGE audit.RawLogFlattened WITH (HOLDLOCK) AS t
        USING (
            SELECT sh.*,
                   CASE WHEN @hasLicence = 1 THEN lic.IsCopilotLic END AS IsCopilotLic
            FROM shredded AS sh
            OUTER APPLY (
                SELECT TOP (1) l.IsCopilotLic
                FROM audit.CopilotLicense AS l
                WHERE @hasLicence = 1 AND l.UserPrincipalName = sh.[User]
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
