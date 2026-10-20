/*
    audit.usp_ShredActivity (2.6.9)
    ------------------------------------------------------------------------
    Reads the records of the activity database (Create-ActivitySchema.sql)
    into audit.AIAppCall and audit.PlatformActivity.

      @SinceUtc  the records from then on (the nightly load passes its own
                 start, so the lookback's records are read again); NULL: every
                 record, the first time and after a history load.

    Each document is parsed once (OPENJSON ... WITH), in batches of @BatchSize
    records by RecordId, and each batch's rows replace the ones already there,
    so a run can be repeated. One run at a time (an application lock). A run
    marks that it is under way (ActivityShredInProgress in
    audit.PipelineSetting) and takes the mark away when it finishes; one
    stopped part way leaves it, and the next run reads every record again.

    Which table a record goes to is its Workload: ConnectedAIApp, PowerBI or
    PowerApps; from 2.7.0 its record type for Teams meeting AI notes
    (TeamCopilotInteraction, to audit.PlatformActivity as Teams AI notes) and
    data loss prevention (ComplianceDLP*, DLPEndpoint, to audit.DlpMatch). Any
    other record is left alone.

    The fields read are those Microsoft documents for the records, and those
    the first records seen carried. Where a field is named differently, it is
    read again from the stored JSON with @SinceUtc = NULL once this procedure
    is corrected: audit.CopilotAudit keeps every record whole.

    Requires SQL Server 2017+. Run sql/Create-ActivitySchema.sql first.
*/
SET NOCOUNT ON;
GO

IF OBJECT_ID(N'audit.usp_ShredActivity', N'P') IS NULL
    EXEC (N'CREATE PROCEDURE audit.usp_ShredActivity AS BEGIN SET NOCOUNT ON; END;');
GO

ALTER PROCEDURE audit.usp_ShredActivity
    @SinceUtc  DATETIME2(0) = NULL,
    @BatchSize INT          = 200000
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    IF @BatchSize IS NULL OR @BatchSize < 1000 SET @BatchSize = 1000;

    DECLARE @lock INT, @msg NVARCHAR(400), @cursor VARCHAR(64) = '', @maxId VARCHAR(64),
            @calls BIGINT = 0, @platform BIGINT = 0, @dlp BIGINT = 0, @batch INT = 1;

    EXEC @lock = sys.sp_getapplock @Resource = N'audit.usp_ShredActivity', @LockMode = 'Exclusive', @LockOwner = 'Session', @LockTimeout = -1;
    IF @lock < 0
        THROW 50030, N'Another shred of the activity records is running, and this one could not wait for it to finish.', 1;

    BEGIN TRY
    /* A shred that was stopped part way, or none that read every record yet (a history loaded and never shredded):
       every record is read. */
    IF EXISTS (SELECT 1 FROM audit.PipelineSetting WHERE Name = N'ActivityShredInProgress')
       OR NOT EXISTS (SELECT 1 FROM audit.PipelineSetting WHERE Name = N'ActivityShredCompleteUtc')
    BEGIN
        IF @SinceUtc IS NOT NULL
            RAISERROR('No shred of the activity records has read every record yet, or the last one did not finish: reading every record.', 0, 1) WITH NOWAIT;
        SET @SinceUtc = NULL;
        IF NOT EXISTS (SELECT 1 FROM audit.PipelineSetting WHERE Name = N'ActivityShredInProgress')
            INSERT INTO audit.PipelineSetting (Name, Value) VALUES (N'ActivityShredInProgress', N'');
    END
    ELSE
        INSERT INTO audit.PipelineSetting (Name, Value) VALUES (N'ActivityShredInProgress', ISNULL(CONVERT(NVARCHAR(30), @SinceUtc, 126), N''));

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

        DROP TABLE IF EXISTS #rec;
        SELECT a.RecordId, a.CreatedDateTime,
               /* 2.7.0: by record type first where the workload is shared with other records (Teams, DLP). */
               CASE WHEN a.RecordType LIKE N'teamCopilot%' THEN N'TeamsAI'
                    WHEN a.RecordType LIKE N'complianceDLP%' OR a.RecordType LIKE N'dlpEndpoint%' OR t.Operation IN (N'DlpRuleMatch', N'DlpRuleUndo', N'DlpInfo') THEN N'DLP'
                    ELSE COALESCE(t.Workload, CASE WHEN a.RecordType LIKE N'connectedAIApp%' THEN N'ConnectedAIApp'
                                         WHEN a.RecordType LIKE N'powerBI%' THEN N'PowerBI'
                                         WHEN a.RecordType LIKE N'powerApps%' THEN N'PowerApps' END) END AS Workload,
               LEFT(t.Workload, 64) AS SourceWorkload,
               LEFT(t.PolicyName, 400) AS PolicyName, LEFT(t.RuleName, 400) AS RuleName, LEFT(t.Severity, 32) AS Severity, pc.Policies,
               LEFT(COALESCE(t.Operation, a.Operation), 128) AS Operation,
               LEFT(COALESCE(t.UserId, a.UserPrincipalName, a.UserId), 256) AS UserId,
               TRY_CAST(t.UserType AS INT) AS UserType,
               LEFT(t.UserKey, 128) AS UserKey,
               LEFT(t.AppIdentity, 400) AS AppIdentity,
               LEFT(t.AppHost, 200) AS AppHost,
               LEFT(COALESCE(t.ApplicationName, t.EventApplicationName, t.AppName), 200) AS ApplicationName,
               LEFT(t.ContextId, 400) AS ContextId,
               LEFT(COALESCE(t.SubscriptionId, t.EventSubscriptionId, t.SubscriptionFlat), 64) AS SubscriptionId,
               LEFT(t.ThreadId, 400) AS ThreadId,
               t.Messages, t.AccessedResources,
               LEFT(COALESCE(t.ItemName, t.ReportName, t.DashboardName, t.DatasetName, t.AppName,
                             CASE WHEN ISJSON(t.AdditionalInfo) = 1 THEN COALESCE(JSON_VALUE(t.AdditionalInfo, '$.appDisplayName'), JSON_VALUE(t.AdditionalInfo, '$.appName')) END,
                             t.ObjectId), 400) AS ItemName,
               LEFT(COALESCE(t.WorkSpaceName, t.EnvironmentName,
                             CASE WHEN ISJSON(t.AdditionalInfo) = 1 THEN JSON_VALUE(t.AdditionalInfo, '$.environmentName') END), 400) AS Container
        INTO #rec
        FROM audit.CopilotAudit AS a
        OUTER APPLY OPENJSON(CASE WHEN ISJSON(CAST(DECOMPRESS(a.AuditDataGz) AS NVARCHAR(MAX))) = 1
                                  THEN CAST(DECOMPRESS(a.AuditDataGz) AS NVARCHAR(MAX)) ELSE N'{}' END)
            WITH (
                Workload             NVARCHAR(64)   '$.Workload',
                Operation            NVARCHAR(200)  '$.Operation',
                UserId               NVARCHAR(512)  '$.UserId',
                UserType             NVARCHAR(16)   '$.UserType',
                UserKey              NVARCHAR(200)  '$.UserKey',
                AppIdentity          NVARCHAR(400)  '$.AppIdentity',
                AppHost              NVARCHAR(200)  '$.CopilotEventData.AppHost',
                ApplicationName      NVARCHAR(200)  '$.ApplicationName',
                EventApplicationName NVARCHAR(200)  '$.CopilotEventData.ApplicationName',
                ContextId            NVARCHAR(400)  '$.CopilotEventData.Contexts[0].Id',
                SubscriptionId       NVARCHAR(64)   '$.Subscription.Id',
                EventSubscriptionId  NVARCHAR(64)   '$.CopilotEventData.Subscription.Id',
                SubscriptionFlat     NVARCHAR(64)   '$.SubscriptionId',
                ThreadId             NVARCHAR(400)  '$.CopilotEventData.ThreadId',
                Messages             NVARCHAR(MAX)  '$.CopilotEventData.Messages' AS JSON,
                AccessedResources    NVARCHAR(MAX)  '$.CopilotEventData.AccessedResources' AS JSON,
                ItemName             NVARCHAR(400)  '$.ItemName',
                ReportName           NVARCHAR(400)  '$.ReportName',
                DashboardName        NVARCHAR(400)  '$.DashboardName',
                DatasetName          NVARCHAR(400)  '$.DatasetName',
                AppName              NVARCHAR(400)  '$.AppName',
                ObjectId             NVARCHAR(400)  '$.ObjectId',
                WorkSpaceName        NVARCHAR(400)  '$.WorkSpaceName',
                EnvironmentName      NVARCHAR(400)  '$.EnvironmentName',
                AdditionalInfo       NVARCHAR(MAX)  '$.AdditionalInfo',
                PolicyName           NVARCHAR(400)  '$.PolicyDetails[0].PolicyName',
                RuleName             NVARCHAR(400)  '$.PolicyDetails[0].Rules[0].RuleName',
                Severity             NVARCHAR(32)   '$.PolicyDetails[0].Rules[0].Severity',
                PolicyDetails        NVARCHAR(MAX)  '$.PolicyDetails' AS JSON
            ) AS t
        OUTER APPLY (SELECT COUNT(*) AS Policies FROM OPENJSON(ISNULL(t.PolicyDetails, N'[]'))) AS pc
        WHERE a.RecordId > @cursor AND a.RecordId <= @maxId
          AND (@SinceUtc IS NULL OR a.CreatedDateTime >= @SinceUtc);

        BEGIN TRANSACTION;
        DELETE c FROM audit.AIAppCall AS c JOIN #rec AS r ON r.RecordId = c.RecordId;
        INSERT INTO audit.AIAppCall
            (RecordId, CreatedDateTime, UserId, UserType, UserKey, CallerKind, Caller, AppIdentity, AppHost, ApplicationName,
             [Resource], ContextId, SubscriptionId, ThreadId, Prompts, Responses, Failed)
        SELECT r.RecordId, r.CreatedDateTime, r.UserId, r.UserType, r.UserKey,
               k.CallerKind, CASE WHEN k.CallerKind = 'Person' THEN LOWER(r.UserId) ELSE N'unattributed' END,
               r.AppIdentity, r.AppHost, r.ApplicationName,
               /* ConnectedAIApp.AzureAI.<resource> or ConnectedAIApp.Entra.<app id>; else the endpoint's first label. */
               LEFT(CASE WHEN r.AppIdentity LIKE N'ConnectedAIApp.%.%'
                         THEN SUBSTRING(r.AppIdentity, CHARINDEX(N'.', r.AppIdentity, 16) + 1, 400)
                         WHEN r.ContextId IS NOT NULL
                         THEN LEFT(r.ContextId, CHARINDEX(N'.', r.ContextId + N'.') - 1)
                         ELSE ISNULL(r.AppIdentity, N'') END, 200),
               r.ContextId, r.SubscriptionId, r.ThreadId,
               ISNULL(m.Prompts, 0), ISNULL(m.Responses, 0),
               CAST(CASE WHEN EXISTS (SELECT 1 FROM OPENJSON(ISNULL(r.AccessedResources, N'[]')) WITH (Status NVARCHAR(32) '$.Status') AS x
                                      WHERE x.Status = N'Failure') THEN 1 ELSE 0 END AS BIT)
        FROM #rec AS r
        CROSS APPLY (SELECT CASE WHEN r.UserType = 0 AND r.UserId LIKE N'%@%' THEN 'Person' ELSE 'Unattributed' END AS CallerKind) AS k
        OUTER APPLY (SELECT SUM(CASE WHEN x.isPrompt = 1 THEN 1 ELSE 0 END) AS Prompts, SUM(CASE WHEN x.isPrompt = 1 THEN 0 ELSE 1 END) AS Responses
                     FROM OPENJSON(ISNULL(r.Messages, N'[]')) WITH (isPrompt BIT '$.isPrompt') AS x) AS m
        WHERE r.Workload = N'ConnectedAIApp';
        SET @calls += @@ROWCOUNT;

        DELETE p FROM audit.PlatformActivity AS p JOIN #rec AS r ON r.RecordId = p.RecordId;
        INSERT INTO audit.PlatformActivity (RecordId, CreatedDateTime, Product, Operation, UserId, UserType, ItemName, Container)
        SELECT r.RecordId, r.CreatedDateTime, CASE r.Workload WHEN N'PowerBI' THEN 'Power BI' WHEN N'TeamsAI' THEN 'Teams AI notes' ELSE 'Power Apps' END,
               r.Operation, LOWER(r.UserId), r.UserType, r.ItemName, r.Container
        FROM #rec AS r
        WHERE r.Workload IN (N'PowerBI', N'PowerApps', N'TeamsAI');
        SET @platform += @@ROWCOUNT;

        /* 2.7.0: data loss prevention matches. */
        DELETE m FROM audit.DlpMatch AS m JOIN #rec AS r ON r.RecordId = m.RecordId;
        INSERT INTO audit.DlpMatch (RecordId, CreatedDateTime, Workload, Operation, UserId, PolicyName, RuleName, Severity, Policies)
        SELECT r.RecordId, r.CreatedDateTime, r.SourceWorkload, r.Operation, LOWER(r.UserId), r.PolicyName, r.RuleName, r.Severity, ISNULL(r.Policies, 0)
        FROM #rec AS r
        WHERE r.Workload = N'DLP';
        SET @dlp += @@ROWCOUNT;
        COMMIT TRANSACTION;

        SET @cursor = @maxId;
        SET @msg = CONCAT(N'  batch ', @batch, N': ', FORMAT(@calls, 'N0'), N' AI app record(s) and ', FORMAT(@platform, 'N0'),
                          N' Power BI and Power Apps record(s) so far.');
        RAISERROR(@msg, 0, 1) WITH NOWAIT;
        SET @batch += 1;
    END;
    DROP TABLE IF EXISTS #rec;

    DELETE FROM audit.PipelineSetting WHERE Name = N'ActivityShredInProgress';
    IF @SinceUtc IS NULL
    BEGIN
        UPDATE audit.PipelineSetting SET Value = CONVERT(NVARCHAR(30), SYSUTCDATETIME(), 126), UpdatedUtc = SYSUTCDATETIME()
        WHERE Name = N'ActivityShredCompleteUtc';
        IF @@ROWCOUNT = 0
            INSERT INTO audit.PipelineSetting (Name, Value) VALUES (N'ActivityShredCompleteUtc', CONVERT(NVARCHAR(30), SYSUTCDATETIME(), 126));
    END;
    SET @msg = CONCAT(N'Activity shred complete: ', FORMAT(@calls, 'N0'), N' AI app record(s), ', FORMAT(@platform, 'N0'), N' Power BI, Power Apps and Teams AI notes record(s), ', FORMAT(@dlp, 'N0'), N' DLP record(s).');
    RAISERROR(@msg, 0, 1) WITH NOWAIT;
    EXEC sys.sp_releaseapplock @Resource = N'audit.usp_ShredActivity', @LockOwner = 'Session';
    END TRY
    BEGIN CATCH
        IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
        IF APPLOCK_MODE(N'public', N'audit.usp_ShredActivity', N'Session') <> N'NoLock'
            EXEC sys.sp_releaseapplock @Resource = N'audit.usp_ShredActivity', @LockOwner = 'Session';
        THROW;
    END CATCH;
END;
GO
