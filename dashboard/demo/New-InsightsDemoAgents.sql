/*
    Copilot Insights demo: the agent workbook's nightly load, and Copilot Studio's authoring records. Run
    after the bi build and before the dashboard's refresh. New-InsightsDemoTenant.sql wrote the workbook's three
    sheets as tools/Import-AgentWorkbook.ps1 keeps them (dbo.AgentInventory, dbo.AgentsInProd, dbo.ParentChildAgent);
    this does what the import does next, in the import's own SQL where the demo needs it:

      - dash.AgentInventory: one row per agent, matched to the agent the audit log knows by the import's first two
        rules (the environment and schema name, or the title id of an agent made in Copilot Studio Lite), the ones the
        demo's agents need; in production where the AgentsInProd sheet lists it
      - dash.AgentChildLink: each parent agent and the agents it calls, told apart as the import tells them apart
      - the agents' names in the report tables (bi.usp_NameAgents), and a week of nightly loads in
        dash.AgentInventoryRun for Admin > Health
      - Copilot Studio's authoring records for the same agents (audit.AgentEvent): made, built, published, shared and
        changed, from their creation or the start of the records to their last publish, and two agents made and since
        deleted; copied into bi.FactAgentEvent as the bi build copies them
*/
SET NOCOUNT ON;

DECLARE @today DATE = CAST(SYSUTCDATETIME() AS DATE);
DECLARE @first DATETIME2(0) = CAST(DATEADD(DAY, -182, @today) AS DATETIME2(0));   -- the start of the records

DROP TABLE IF EXISTS #w, #m, #pc, #pk, #pl, #ck, #a, #k, #ev;

/* ---- 1. The inventory, as the import reads its sheet: every column as text, trimmed, typed ---- */
SELECT CAST(LEFT(NULLIF(LTRIM(RTRIM(CAST(w.AgentId AS NVARCHAR(4000)))), N''), 100) AS NVARCHAR(100)) AS Id,
       NULLIF(LTRIM(RTRIM(CAST(w.AgentName AS NVARCHAR(4000)))), N'') AS AgentName,
       NULLIF(LTRIM(RTRIM(CAST(w.OwnerIdUPN AS NVARCHAR(4000)))), N'') AS OwnerUpn,
       NULLIF(LTRIM(RTRIM(CAST(w.EnvironmentDisplayName AS NVARCHAR(4000)))), N'') AS Environment,
       NULLIF(LTRIM(RTRIM(CAST(w.SchemaName AS NVARCHAR(4000)))), N'') AS SchemaName,
       TRY_CONVERT(DATETIME2(0), NULLIF(LTRIM(RTRIM(CAST(w.CreatedAt AS NVARCHAR(4000)))), N'')) AS CreatedAt,
       TRY_CONVERT(DATETIME2(0), NULLIF(LTRIM(RTRIM(CAST(w.LastPublishedAt AS NVARCHAR(4000)))), N'')) AS LastPublishedAt,
       NULLIF(LTRIM(RTRIM(CAST(w.Channels AS NVARCHAR(4000)))), N'') AS Channels,
       TRY_CONVERT(INT, TRY_CONVERT(FLOAT, NULLIF(LTRIM(RTRIM(CAST(w.ViewerUsers AS NVARCHAR(4000)))), N''))) AS ViewerUsers,
       TRY_CONVERT(INT, TRY_CONVERT(FLOAT, NULLIF(LTRIM(RTRIM(CAST(w.ViewerGroups AS NVARCHAR(4000)))), N''))) AS ViewerGroups,
       TRY_CONVERT(INT, TRY_CONVERT(FLOAT, NULLIF(LTRIM(RTRIM(CAST(w.EditorUsers AS NVARCHAR(4000)))), N''))) AS EditorUsers,
       TRY_CONVERT(INT, TRY_CONVERT(FLOAT, NULLIF(LTRIM(RTRIM(CAST(w.EditorGroups AS NVARCHAR(4000)))), N''))) AS EditorGroups,
       NULLIF(LTRIM(RTRIM(CAST(w.TitleId AS NVARCHAR(4000)))), N'') AS TitleId,
       NULLIF(LTRIM(RTRIM(CAST(w.EnvironmentId AS NVARCHAR(4000)))), N'') AS EnvironmentId,
       NULLIF(LTRIM(RTRIM(CAST(w.EnvironmentSku AS NVARCHAR(4000)))), N'') AS EnvironmentSku,
       NULLIF(LTRIM(RTRIM(CAST(w.[Authentication] AS NVARCHAR(4000)))), N'') AS [Authentication],
       CASE WHEN NULLIF(LTRIM(RTRIM(CAST(w.IsQuarantined AS NVARCHAR(4000)))), N'') IN (N'True', N'Yes', N'1') THEN 1
            WHEN NULLIF(LTRIM(RTRIM(CAST(w.IsQuarantined AS NVARCHAR(4000)))), N'') IN (N'False', N'No', N'0') THEN 0 END AS IsQuarantined,
       CASE WHEN NULLIF(LTRIM(RTRIM(CAST(w.IsWebSearchEnabled AS NVARCHAR(4000)))), N'') IN (N'True', N'Yes', N'1') THEN 1
            WHEN NULLIF(LTRIM(RTRIM(CAST(w.IsWebSearchEnabled AS NVARCHAR(4000)))), N'') IN (N'False', N'No', N'0') THEN 0 END AS IsWebSearchEnabled,
       CASE WHEN NULLIF(LTRIM(RTRIM(CAST(w.Triggers AS NVARCHAR(4000)))), N'') IS NOT NULL
             AND NULLIF(LTRIM(RTRIM(CAST(w.Triggers AS NVARCHAR(4000)))), N'') NOT IN (N'0', N'[]', N'None') THEN 1 ELSE 0 END AS IsAutonomous,
       NULLIF(LTRIM(RTRIM(CAST(w.CreatedByUPN AS NVARCHAR(4000)))), N'') AS CreatedBy
INTO #w FROM dbo.AgentInventory AS w;
DELETE FROM #w WHERE Id IS NULL;
UPDATE #w SET TitleId = N'T_' + TitleId WHERE TitleId IS NOT NULL AND TitleId NOT LIKE N'T[_]%';

/* The import's rules 1 and 2: Copilot.Studio.<EnvironmentId>-<SchemaName>, or Copilot.Studio.Declarative.<TitleId>;
   an identity the audit log names two ways counts as the agent it was folded into. */
SELECT w.Id, MIN(COALESCE(d.MergedIntoKey, d.AgentKey)) AS AgentKey
INTO #m
FROM #w AS w
JOIN bi.DimAgent AS d ON d.AgentId = N'Copilot.Studio.' + w.EnvironmentId + N'-' + w.SchemaName
                      OR d.AgentId = N'Copilot.Studio.Declarative.' + w.TitleId OR d.AgentId = w.TitleId
GROUP BY w.Id;

DELETE FROM dash.AgentInventory;
INSERT INTO dash.AgentInventory (InventoryAgentId, AgentKey, AgentName, OwnerUpn, Environment, SchemaName, CreatedAt, LastPublishedAt,
                                 Channels, ViewerUsers, ViewerGroups, EditorUsers, EditorGroups, InProduction,
                                 EnvironmentSku, Authentication, IsQuarantined, IsWebSearchEnabled, IsAutonomous)
SELECT x.Id, m.AgentKey, LEFT(x.AgentName, 400), LEFT(x.OwnerUpn, 256), LEFT(x.Environment, 400), LEFT(x.SchemaName, 400), x.CreatedAt,
       x.LastPublishedAt, LEFT(x.Channels, 400), x.ViewerUsers, x.ViewerGroups, x.EditorUsers, x.EditorGroups,
       CASE WHEN EXISTS (SELECT 1 FROM dbo.AgentsInProd AS p WHERE LTRIM(RTRIM(CAST(p.AgentId AS NVARCHAR(100)))) = x.Id) THEN 1 ELSE 0 END,
       LEFT(x.EnvironmentSku, 100), LEFT(x.[Authentication], 200), x.IsQuarantined, x.IsWebSearchEnabled, x.IsAutonomous
FROM (SELECT *, ROW_NUMBER() OVER (PARTITION BY Id ORDER BY LastPublishedAt DESC) AS rn FROM #w) AS x
LEFT JOIN #m AS m ON m.Id = x.Id
WHERE x.rn = 1;

/* Agents of one name told apart by their environment, as the import does. */
WITH n AS (SELECT InventoryAgentId, AgentName, Environment, SchemaName,
                  COUNT(*) OVER (PARTITION BY LTRIM(RTRIM(AgentName))) AS SameName,
                  COUNT(*) OVER (PARTITION BY LTRIM(RTRIM(AgentName)), ISNULL(Environment, N'')) AS SameNameAndPlace
           FROM dash.AgentInventory WHERE NULLIF(LTRIM(RTRIM(AgentName)), N'') IS NOT NULL)
UPDATE n SET AgentName = LEFT(CONCAT(LTRIM(RTRIM(AgentName)), N' (', ISNULL(Environment, N'no environment'),
                                     CASE WHEN SameNameAndPlace > 1 THEN CONCAT(N', ', ISNULL(SchemaName, InventoryAgentId)) END, N')'), 400)
WHERE SameName > 1;

/* ---- 2. Parents and the agents they call, as the import reads the ParentChildAgent sheet ---- */
SELECT COALESCE(NULLIF(LTRIM(RTRIM(CAST(w.ChildAgentName AS NVARCHAR(4000)))), N''), NULLIF(LTRIM(RTRIM(CAST(w.ComponentName AS NVARCHAR(4000)))), N'')) AS Child,
       NULLIF(LTRIM(RTRIM(CAST(w.ParentAgentId AS NVARCHAR(4000)))), N'') AS ParentId,
       NULLIF(LTRIM(RTRIM(CAST(w.ParentAgentName AS NVARCHAR(4000)))), N'') AS ParentName,
       NULLIF(LTRIM(RTRIM(CAST(w.RelationshipType AS NVARCHAR(4000)))), N'') AS Rel,
       NULLIF(LTRIM(RTRIM(CAST(w.IsValidParentChildAgent AS NVARCHAR(4000)))), N'') AS Valid,
       NULLIF(LTRIM(RTRIM(CAST(w.ComponentId AS NVARCHAR(4000)))), N'') AS CompId,
       NULLIF(LTRIM(RTRIM(CAST(w.EnvironmentId AS NVARCHAR(4000)))), N'') AS EnvId,
       NULLIF(LTRIM(RTRIM(CAST(w.SchemaName AS NVARCHAR(4000)))), N'') AS FullSchema,
       NULLIF(LTRIM(RTRIM(CAST(w.EnvironmentSku AS NVARCHAR(4000)))), N'') AS EnvSku
INTO #pc FROM dbo.ParentChildAgent AS w;
DELETE FROM #pc WHERE Child IS NULL;

SELECT p.*, CASE WHEN p.Rel LIKE N'%connected%' THEN N'Connected' ELSE N'Child' END AS Kind,
       CASE WHEN p.FullSchema LIKE N'%.%' THEN RIGHT(p.FullSchema, CHARINDEX(N'.', REVERSE(p.FullSchema)) - 1) END AS ChildSchema,
       i.AgentKey AS ParentKey, COALESCE(NULLIF(i.AgentName, N''), p.ParentName, p.ParentId) AS ParentLabel,
       COALESCE(NULLIF(i.Environment, N''),
                NULLIF(CONCAT(p.EnvSku, CASE WHEN p.EnvSku IS NOT NULL AND p.EnvId IS NOT NULL THEN N' ' + NCHAR(183) + N' ' END, LEFT(p.EnvId, 8)), N''),
                N'no environment') AS ParentEnv
INTO #pk FROM #pc AS p LEFT JOIN dash.AgentInventory AS i ON i.InventoryAgentId = p.ParentId;
UPDATE k SET ParentLabel = LEFT(CONCAT(k.ParentLabel, N' (', k.ParentEnv, N')'), 400)
FROM #pk AS k
WHERE k.ParentLabel IN (SELECT ParentLabel FROM #pk GROUP BY ParentLabel HAVING COUNT(DISTINCT ISNULL(ParentId, N'')) > 1);

SELECT k.*, ci.Id AS ChildInv,
       CASE WHEN k.Kind = N'Connected' THEN COALESCE(N'agent:' + ci.Id, N'connected:' + ISNULL(k.EnvId, N'') + N'/' + COALESCE(k.ChildSchema, k.Child))
            ELSE N'child:' + COALESCE(k.CompId, ISNULL(k.ParentId, N'') + N'/' + COALESCE(k.ChildSchema, k.Child)) END AS ChildKey
INTO #pl
FROM #pk AS k
OUTER APPLY (SELECT TOP (1) w.Id FROM #w AS w WHERE k.Kind = N'Connected' AND w.EnvironmentId = k.EnvId AND w.SchemaName = k.ChildSchema) AS ci;

SELECT l.ChildKey, MIN(COALESCE(NULLIF(i.AgentName, N''), l.Child)) AS BaseName, MIN(l.Kind) AS Kind,
       MIN(l.ParentLabel) AS ParentLabel, MIN(l.ParentEnv) AS ParentEnv, MIN(COALESCE(l.CompId, l.ChildSchema, l.ChildKey)) AS Extra,
       CAST(NULL AS NVARCHAR(400)) AS Display
INTO #ck FROM #pl AS l LEFT JOIN dash.AgentInventory AS i ON i.InventoryAgentId = l.ChildInv GROUP BY l.ChildKey;
WITH n AS (SELECT Display, BaseName, Kind, ParentLabel, ParentEnv, COUNT(*) OVER (PARTITION BY BaseName) AS SameName FROM #ck)
UPDATE n SET Display = LEFT(CASE WHEN SameName = 1 THEN BaseName
                                 WHEN Kind = N'Child' THEN CONCAT(BaseName, N' (in ', ParentLabel, N')')
                                 ELSE CONCAT(BaseName, N' (', ISNULL(ParentEnv, N'no environment'), N')') END, 400);
WITH n AS (SELECT Display, Extra, COUNT(*) OVER (PARTITION BY Display) AS SameDisplay FROM #ck)
UPDATE n SET Display = LEFT(CONCAT(Display, N' ' + NCHAR(183) + N' ', Extra), 400) WHERE SameDisplay > 1;

DELETE FROM dash.AgentChildLink;
INSERT INTO dash.AgentChildLink (ChildAgentName, ParentInventoryAgentId, ParentAgentName, ParentAgentKey, RelationshipType, IsValid,
                                 ChildKey, ChildKind, ChildInventoryAgentId, ParentEnvironment)
SELECT c.Display, LEFT(l.ParentId, 100), LEFT(l.ParentLabel, 400), l.ParentKey, LEFT(l.Rel, 200),
       CASE WHEN l.Valid IN (N'Yes', N'True', N'1') THEN 1 WHEN l.Valid IN (N'No', N'False', N'0') THEN 0 END,
       LEFT(l.ChildKey, 450), l.Kind, l.ChildInv, LEFT(l.ParentEnv, 400)
FROM #pl AS l JOIN #ck AS c ON c.ChildKey = l.ChildKey;

/* ---- 3. The names in the report tables, and the loads Admin > Health lists ---- */
EXEC bi.usp_NameAgents;

/* A load each night of the last week, at 02:20 after the pipeline's first steps, of the workbook saved the evening
   before; the inventory had fewer agents on the nights before the newest were made. */
DELETE FROM dash.AgentInventoryRun;
WITH n AS (SELECT n FROM (VALUES (6), (5), (4), (3), (2), (1), (0)) AS v (n))
INSERT INTO dash.AgentInventoryRun (StartedUtc, EndedUtc, Source, FileCreatedUtc, InventoryRows, ProductionRows, ChildLinkRows, Matched, Status)
SELECT r.Started, DATEADD(SECOND, 31 + n.n * 4, r.Started),
       CONCAT(N'https://contoso.sharepoint.com/sites/ai-governance/Shared Documents/Agent inventory/Agent inventory ',
              CONVERT(NVARCHAR(10), DATEADD(DAY, -n.n - 1, @today), 120), N'.xlsx'),
       DATEADD(HOUR, 22, CAST(DATEADD(DAY, -n.n - 1, @today) AS DATETIME2(0))),
       (SELECT COUNT(*) FROM #w AS w WHERE w.CreatedAt < r.Started),
       (SELECT COUNT(*) FROM dbo.AgentsInProd),
       (SELECT COUNT(*) FROM dbo.ParentChildAgent),
       (SELECT COUNT(*) FROM dash.AgentInventory AS i WHERE i.AgentKey IS NOT NULL AND i.CreatedAt < r.Started),
       N'Succeeded'
FROM n CROSS APPLY (SELECT DATEADD(MINUTE, 140, CAST(DATEADD(DAY, -n.n, @today) AS DATETIME2(0))) AS Started) AS r
ORDER BY n.n DESC;

/* ---- 4. Copilot Studio's authoring records for the same agents ---- */

/* Each agent: who made it and owns it, a colleague who edits it where it has more than one editor, and when it was
   first published - the day it was made for an agent made in Copilot Studio Lite, else within its first fortnight. */
SELECT ROW_NUMBER() OVER (ORDER BY w.CreatedAt, w.Id) AS n, w.Id, w.SchemaName, w.CreatedAt, w.LastPublishedAt,
       COALESCE(w.CreatedBy, w.OwnerUpn) AS Maker, COALESCE(w.OwnerUpn, w.CreatedBy) AS Owner, co.Upn AS CoMaker,
       CASE WHEN ISNULL(w.ViewerUsers, 0) + ISNULL(w.ViewerGroups, 0) > 0 THEN 1 ELSE 0 END AS Shared,
       CASE WHEN w.LastPublishedAt IS NULL THEN NULL
            WHEN DATEDIFF(DAY, w.CreatedAt, w.LastPublishedAt) <= 3 THEN w.LastPublishedAt
            ELSE DATEADD(HOUR, 3, DATEADD(DAY, CASE WHEN DATEDIFF(DAY, w.CreatedAt, w.LastPublishedAt) < 16 THEN DATEDIFF(DAY, w.CreatedAt, w.LastPublishedAt) / 2
                                                    ELSE 5 + ABS(CHECKSUM(w.Id)) % 9 END, w.CreatedAt)) END AS FirstPublished,
       21 + ABS(CHECKSUM(w.SchemaName)) % 15 AS Every,
       w.[Authentication]
INTO #a
FROM #w AS w
LEFT JOIN audit.UserDirectory AS od ON od.UserPrincipalName = w.OwnerUpn
OUTER APPLY (SELECT TOP (1) d.UserPrincipalName AS Upn FROM audit.UserDirectory AS d
             WHERE ISNULL(w.EditorUsers, 0) >= 2 AND d.Department = od.Department AND d.AccountEnabled = 1 AND d.UserPrincipalName <> w.OwnerUpn
               AND d.OrgDepth >= 3
             ORDER BY HASHBYTES('MD5', CONCAT(N'comaker', d.UserPrincipalName, w.Id))) AS co;

CREATE TABLE #ev (n INT NOT NULL, BotId NVARCHAR(100) NOT NULL, BotSchemaName NVARCHAR(200) NULL, Ts DATETIME2(0) NOT NULL,
                  Operation NVARCHAR(100) NOT NULL, Action VARCHAR(16) NOT NULL, ActorUpn NVARCHAR(512) NULL,
                  ComponentName NVARCHAR(200) NULL, ComponentType NVARCHAR(100) NULL);
/* Made, with its first topics and knowledge, and changed before its first publish. */
INSERT INTO #ev
SELECT a.n, a.Id, a.SchemaName, x.Ts, x.Op, x.Action, a.Maker, x.Comp, x.CompType
FROM #a AS a
CROSS APPLY (VALUES (a.CreatedAt, N'BotCreate', 'Created', CAST(NULL AS NVARCHAR(200)), CAST(NULL AS NVARCHAR(100))),
                    (DATEADD(MINUTE, 20, a.CreatedAt), N'BotComponentCreate', 'Component', a.SchemaName + N'.topic.Greeting', N'Topic'),
                    (DATEADD(MINUTE, 55, a.CreatedAt), N'BotComponentCreate', 'Component', a.SchemaName + N'.knowledge.Primary', N'KnowledgeSource'),
                    (DATEADD(DAY, 1, a.CreatedAt), N'BotComponentCreate', 'Component', a.SchemaName + N'.topic.Escalate', N'Topic'),
                    (DATEADD(DAY, 2, a.CreatedAt), N'BotComponentUpdate', 'Component', a.SchemaName + N'.topic.Greeting', N'Topic')) AS x (Ts, Op, Action, Comp, CompType)
WHERE a.CreatedAt >= @first AND x.Ts < SYSUTCDATETIME()
  AND (x.Op = N'BotCreate' OR a.FirstPublished IS NULL OR x.Ts < a.FirstPublished);
/* Its first publish, and shared the day after where anyone but its editors can use it. */
INSERT INTO #ev
SELECT a.n, a.Id, a.SchemaName, x.Ts, x.Op, x.Action, a.Owner, NULL, NULL
FROM #a AS a
CROSS APPLY (VALUES (a.FirstPublished, N'BotUpdateOperation-BotPublish', 'Published'),
                    (DATEADD(DAY, 1, a.FirstPublished), N'BotUpdateOperation-BotShare', 'Shared')) AS x (Ts, Op, Action)
WHERE a.FirstPublished >= @first AND x.Ts < SYSUTCDATETIME() AND (x.Op LIKE N'%Publish' OR a.Shared = 1);
/* Then a change and a publish every three to five weeks, back from its last publish: the owner and the colleague who
   edits it in turn. */
SELECT TOP (9) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) - 1 AS k INTO #k FROM sys.all_columns;
INSERT INTO #ev
SELECT a.n, a.Id, a.SchemaName, x.Ts, x.Op, x.Action, CASE WHEN k.k % 2 = 1 AND a.CoMaker IS NOT NULL THEN a.CoMaker ELSE a.Owner END,
       x.Comp, x.CompType
FROM #a AS a
CROSS JOIN #k AS k
CROSS APPLY (SELECT DATEADD(DAY, -k.k * a.Every, a.LastPublishedAt) AS P) AS p
CROSS APPLY (VALUES (DATEADD(HOUR, -26, p.P), N'BotComponentUpdate', 'Component', a.SchemaName + CASE k.k % 3 WHEN 0 THEN N'.topic.Escalate' WHEN 1 THEN N'.knowledge.Primary' ELSE N'.topic.Greeting' END,
                     CASE k.k % 3 WHEN 1 THEN N'KnowledgeSource' ELSE N'Topic' END),
                    (p.P, N'BotUpdateOperation-BotPublish', 'Published', NULL, NULL)) AS x (Ts, Op, Action, Comp, CompType)
WHERE a.LastPublishedAt IS NOT NULL AND p.P > DATEADD(DAY, 2, a.FirstPublished) AND x.Ts >= @first;
/* Agents never published: still being built. One agent had its authentication turned off before it went live. */
INSERT INTO #ev
SELECT a.n, a.Id, a.SchemaName, DATEADD(DAY, 6, a.CreatedAt), N'BotUpdateOperation-BotNameUpdate', 'Updated', a.Maker, NULL, NULL
FROM #a AS a WHERE a.LastPublishedAt IS NULL AND a.CreatedAt >= @first AND DATEADD(DAY, 6, a.CreatedAt) < SYSUTCDATETIME();
INSERT INTO #ev
SELECT a.n, a.Id, a.SchemaName, DATEADD(HOUR, -2, a.FirstPublished), N'BotUpdateOperation-BotAuthUpdate', 'Updated', a.Owner, NULL, NULL
FROM #a AS a WHERE a.[Authentication] = N'No authentication' AND a.FirstPublished >= @first;

/* Two agents made and since deleted, which the workbook no longer lists: a holiday helper tried in the sandbox, and a
   canteen menu agent made in Copilot Studio Lite. */
DECLARE @eng NVARCHAR(256) = (SELECT TOP (1) UserPrincipalName FROM audit.UserDirectory WHERE Department = N'Engineering' AND OrgDepth = 4 AND AccountEnabled = 1 ORDER BY HASHBYTES('MD5', CONCAT(N'gone1', UserPrincipalName))),
        @fac NVARCHAR(256) = (SELECT TOP (1) UserPrincipalName FROM audit.UserDirectory WHERE Department = N'Facilities' AND OrgDepth = 4 AND AccountEnabled = 1 ORDER BY HASHBYTES('MD5', CONCAT(N'gone2', UserPrincipalName)));
INSERT INTO #ev
SELECT 900 + x.g, x.BotId, x.Schema_, DATEADD(MINUTE, x.Minutes, CAST(DATEADD(DAY, -x.Ago, @today) AS DATETIME2(0))), x.Op, x.Action, x.Actor, x.Comp, x.CompType
FROM (VALUES
    (1, N'7c1d0e2f-3a4b-4c5d-8e6f-0a1b2c3d4e01', N'cr9z3_holidayHelper', 121, 640, N'BotCreate', 'Created', @eng, NULL, NULL),
    (1, N'7c1d0e2f-3a4b-4c5d-8e6f-0a1b2c3d4e01', N'cr9z3_holidayHelper', 121, 700, N'BotComponentCreate', 'Component', @eng, N'cr9z3_holidayHelper.topic.Greeting', N'Topic'),
    (1, N'7c1d0e2f-3a4b-4c5d-8e6f-0a1b2c3d4e01', N'cr9z3_holidayHelper', 119, 610, N'BotComponentUpdate', 'Component', @eng, N'cr9z3_holidayHelper.topic.Greeting', N'Topic'),
    (1, N'7c1d0e2f-3a4b-4c5d-8e6f-0a1b2c3d4e01', N'cr9z3_holidayHelper', 64, 900, N'BotDelete', 'Deleted', @eng, NULL, NULL),
    (2, N'7c1d0e2f-3a4b-4c5d-8e6f-0a1b2c3d4e02', N'crd20_canteenMenu', 47, 780, N'BotCreate', 'Created', @fac, NULL, NULL),
    (2, N'7c1d0e2f-3a4b-4c5d-8e6f-0a1b2c3d4e02', N'crd20_canteenMenu', 47, 800, N'BotUpdateOperation-BotPublish', 'Published', @fac, NULL, NULL),
    (2, N'7c1d0e2f-3a4b-4c5d-8e6f-0a1b2c3d4e02', N'crd20_canteenMenu', 46, 600, N'BotUpdateOperation-BotShare', 'Shared', @fac, NULL, NULL),
    (2, N'7c1d0e2f-3a4b-4c5d-8e6f-0a1b2c3d4e02', N'crd20_canteenMenu', 12, 960, N'BotDelete', 'Deleted', @fac, NULL, NULL)
) AS x (g, BotId, Schema_, Ago, Minutes, Op, Action, Actor, Comp, CompType)
WHERE x.Actor IS NOT NULL;

/* What the Microsoft 365 admin center's administrators did to agents (CopilotAgentManagement): eight published
   agents deployed to people, one blocked and unblocked a week later, one removed, and the tenant's agent settings changed. */
DECLARE @itAdmin NVARCHAR(256) = (SELECT TOP (1) UserPrincipalName FROM audit.UserDirectory WHERE Department = N'Engineering' AND OrgDepth = 3 AND AccountEnabled = 1 ORDER BY HASHBYTES('MD5', CONCAT(N'itadmin', UserPrincipalName)));
INSERT INTO #ev
SELECT 2000 + p.n, p.Id, NULL, DATEADD(HOUR, 30 + p.n, p.FirstPublished), x.Op, x.Action, @itAdmin, p.SchemaName, NULL
FROM (SELECT TOP (10) ROW_NUMBER() OVER (ORDER BY a.FirstPublished DESC) AS n, a.Id, a.SchemaName, a.FirstPublished FROM #a AS a
      WHERE a.FirstPublished >= @first AND DATEADD(DAY, 10, a.FirstPublished) < SYSUTCDATETIME() ORDER BY a.FirstPublished DESC) AS p
CROSS APPLY (SELECT CASE WHEN p.n <= 8 THEN N'DeployedAgent' WHEN p.n = 9 THEN N'BlockedAgent' ELSE N'RemovedAgent' END AS Op,
                    CASE WHEN p.n <= 8 THEN 'Deployed' WHEN p.n = 9 THEN 'Blocked' ELSE 'Removed' END AS Action) AS x
WHERE @itAdmin IS NOT NULL
UNION ALL
SELECT 2100, p.Id, NULL, DATEADD(DAY, 7, DATEADD(HOUR, 39, p.FirstPublished)), N'UnblockedAgent', 'Unblocked', @itAdmin, p.SchemaName, NULL
FROM (SELECT TOP (10) ROW_NUMBER() OVER (ORDER BY a.FirstPublished DESC) AS n, a.Id, a.SchemaName, a.FirstPublished FROM #a AS a
      WHERE a.FirstPublished >= @first AND DATEADD(DAY, 10, a.FirstPublished) < SYSUTCDATETIME() ORDER BY a.FirstPublished DESC) AS p
WHERE p.n = 9 AND @itAdmin IS NOT NULL
UNION ALL
SELECT 2200, N'', NULL, DATEADD(DAY, -20, SYSUTCDATETIME()), N'UpdatedTenantSettings', 'Settings', @itAdmin, NULL, NULL WHERE @itAdmin IS NOT NULL;

DELETE FROM audit.AgentEvent;
INSERT INTO audit.AgentEvent (RecordId, CreatedUtc, Operation, Action, ActorUpn, BotId, BotSchemaName, ComponentName, ComponentType, ResultStatus)
SELECT CONCAT('demo-agent-', e.n, '-', ROW_NUMBER() OVER (PARTITION BY e.n ORDER BY e.Ts, e.Operation, e.ComponentName)),
       e.Ts, e.Operation, e.Action, e.ActorUpn, e.BotId, e.BotSchemaName, e.ComponentName, e.ComponentType, N'Succeeded'
FROM #ev AS e;

/* Into the bi schema as bi.usp_RefreshBi copies them, its days in the reporting zone. */
DECLARE @z SYSNAME = audit.ReportingTimeZone();
TRUNCATE TABLE bi.FactAgentEvent;
INSERT INTO bi.FactAgentEvent (RecordId, EventUtc, DateKey, Action, Operation, ActorUpn, ActorKey, BotId, BotSchemaName, ComponentName, ComponentType)
SELECT e.RecordId, e.CreatedUtc, YEAR(t.LocalTs) * 10000 + MONTH(t.LocalTs) * 100 + DAY(t.LocalTs), e.Action, e.Operation, e.ActorUpn,
       u.UserKey, e.BotId, e.BotSchemaName, e.ComponentName, e.ComponentType
FROM audit.AgentEvent AS e
CROSS APPLY (SELECT CAST(e.CreatedUtc AT TIME ZONE 'UTC' AT TIME ZONE @z AS DATETIME2(0)) AS LocalTs) AS t
LEFT JOIN bi.DimUser AS u ON u.UserPrincipalName = e.ActorUpn;

SELECT (SELECT COUNT(*) FROM dash.AgentInventory) AS Agents, (SELECT COUNT(*) FROM dash.AgentInventory WHERE AgentKey IS NOT NULL) AS Matched,
       (SELECT COUNT(*) FROM dash.AgentInventory WHERE InProduction = 1) AS InProduction, (SELECT COUNT(*) FROM dash.AgentChildLink) AS Links,
       (SELECT COUNT(*) FROM audit.AgentEvent) AS AuthoringEvents;
DROP TABLE IF EXISTS #w, #m, #pc, #pk, #pl, #ck, #a, #k, #ev;
