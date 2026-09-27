/*
    Copilot audit backfill - agent build and publish activity
    ------------------------------------------------------------------------
    audit.AgentEvent and audit.usp_ShredAgentEvents, in the license-event
    database(s). The nightly license-event load also asks for Copilot Studio's
    authoring operations (BotCreate, BotUpdateOperation-BotPublish,
    BotUpdateOperation-BotShare, BotDelete, BotComponent*...), in the same
    audit searches, so they cost no extra search. This turns each into one row:
    who did what to which agent, and when.

      audit.AgentEvent   one row per authoring record: the action, the agent
                         (BotId, BotSchemaName), the maker, the component,
                         and the update details

    bi.usp_RefreshBi copies them into bi.FactAgentEvent from every license
    database, for the dashboard's Agents > Build and publish and Power BI.

    Operation names: https://learn.microsoft.com/microsoft-copilot-studio/admin-logging-copilot-studio

    Idempotent. Requires SQL Server 2017+ and Create-LicenseEventSchema.sql's
    audit.CopilotAudit.
*/
SET NOCOUNT ON;

IF OBJECT_ID(N'audit.AgentEvent', N'U') IS NULL
    CREATE TABLE audit.AgentEvent
    (
        RecordId         VARCHAR(64)     NOT NULL CONSTRAINT PK_AgentEvent PRIMARY KEY,
        CreatedUtc       DATETIME2(0)    NOT NULL,
        Operation        NVARCHAR(100)   NOT NULL,
        Action           VARCHAR(16)     NOT NULL,   -- Created | Published | Shared | Updated | Deleted | Component
        ActorUpn         NVARCHAR(512)   NULL,
        BotId            NVARCHAR(100)   NULL,
        BotSchemaName    NVARCHAR(200)   NULL,
        ComponentName    NVARCHAR(200)   NULL,       -- BotComponentSchemaName
        ComponentType    NVARCHAR(100)   NULL,
        ResultStatus     NVARCHAR(50)    NULL,
        UpdateDetails    NVARCHAR(4000)  NULL,       -- BotUpdateDetails / BotComponentUpdateDetails, as JSON
        ShreddedUtc      DATETIME2(0)    NOT NULL CONSTRAINT DF_AgentEvent_Shredded DEFAULT (SYSUTCDATETIME()),
        INDEX IX_AgentEvent_Created (CreatedUtc),
        INDEX IX_AgentEvent_Bot (BotSchemaName, CreatedUtc)
    );
GO

IF OBJECT_ID(N'audit.usp_ShredAgentEvents', N'P') IS NULL
    EXEC (N'CREATE PROCEDURE audit.usp_ShredAgentEvents AS RETURN 0;');
GO

ALTER PROCEDURE audit.usp_ShredAgentEvents
    @SinceUtc   DATETIME2(0) = NULL,
    @RebuildAll BIT          = 0
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    IF @RebuildAll = 1
    BEGIN
        TRUNCATE TABLE audit.AgentEvent;
        SET @SinceUtc = NULL;
    END;

    DROP TABLE IF EXISTS #ae;
    SELECT a.RecordId,
           CAST(a.CreatedDateTime AS DATETIME2(0))                                       AS CreatedUtc,
           LEFT(COALESCE(t.Operation, a.Operation), 100)                                 AS Operation,
           LEFT(COALESCE(t.UserId, a.UserPrincipalName, a.UserId), 512)                  AS ActorUpn,
           LEFT(t.BotId, 100)                                                            AS BotId,
           LEFT(t.BotSchemaName, 200)                                                    AS BotSchemaName,
           LEFT(t.BotComponentSchemaName, 200)                                           AS ComponentName,
           LEFT(t.BotComponentType, 100)                                                 AS ComponentType,
           LEFT(t.ResultStatus, 50)                                                      AS ResultStatus,
           LEFT(COALESCE(t.BotUpdateDetails, t.BotComponentUpdateDetails), 4000)          AS UpdateDetails
    INTO #ae
    FROM audit.CopilotAudit AS a
    CROSS APPLY (SELECT CAST(DECOMPRESS(a.AuditDataGz) AS NVARCHAR(MAX)) AS Doc) AS d
    OUTER APPLY OPENJSON(CASE WHEN ISJSON(d.Doc) = 1 THEN d.Doc ELSE N'{}' END)
        WITH (
            Operation                 NVARCHAR(100)  '$.Operation',
            UserId                    NVARCHAR(512)  '$.UserId',
            ResultStatus              NVARCHAR(50)   '$.ResultStatus',
            BotId                     NVARCHAR(100)  '$.BotId',
            BotSchemaName             NVARCHAR(200)  '$.BotSchemaName',
            BotComponentSchemaName    NVARCHAR(200)  '$.BotComponentSchemaName',
            BotComponentType          NVARCHAR(100)  '$.BotComponentType',
            BotUpdateDetails          NVARCHAR(MAX)  '$.BotUpdateDetails'          AS JSON,
            BotComponentUpdateDetails NVARCHAR(MAX)  '$.BotComponentUpdateDetails' AS JSON
        ) AS t
    WHERE a.Operation LIKE N'Bot%'
      AND (@SinceUtc IS NULL OR a.CreatedDateTime >= @SinceUtc);

    MERGE audit.AgentEvent WITH (HOLDLOCK) AS t
    USING (
        SELECT e.*,
               CASE WHEN e.Operation = N'BotCreate' THEN 'Created'
                    WHEN e.Operation LIKE N'%BotPublish%' THEN 'Published'
                    WHEN e.Operation LIKE N'%BotShare%' THEN 'Shared'
                    WHEN e.Operation IN (N'BotDelete', N'BotDeleteCleanup') THEN 'Deleted'
                    WHEN e.Operation LIKE N'BotComponent%' THEN 'Component'
                    ELSE 'Updated' END AS Action
        FROM #ae AS e
    ) AS s
    ON t.RecordId = s.RecordId
    WHEN MATCHED THEN UPDATE SET CreatedUtc = s.CreatedUtc, Operation = s.Operation, Action = s.Action, ActorUpn = s.ActorUpn,
        BotId = s.BotId, BotSchemaName = s.BotSchemaName, ComponentName = s.ComponentName, ComponentType = s.ComponentType,
        ResultStatus = s.ResultStatus, UpdateDetails = s.UpdateDetails, ShreddedUtc = SYSUTCDATETIME()
    WHEN NOT MATCHED THEN INSERT (RecordId, CreatedUtc, Operation, Action, ActorUpn, BotId, BotSchemaName, ComponentName, ComponentType, ResultStatus, UpdateDetails)
        VALUES (s.RecordId, s.CreatedUtc, s.Operation, s.Action, s.ActorUpn, s.BotId, s.BotSchemaName, s.ComponentName, s.ComponentType, s.ResultStatus, s.UpdateDetails);

    DECLARE @msg NVARCHAR(200) = CONCAT(N'Agent events: ', FORMAT(@@ROWCOUNT, 'N0'), N' record(s) shredded.');
    RAISERROR(@msg, 0, 1) WITH NOWAIT;
END;
GO
