-- Web application additions (additive only). Narrow deletes the web needs:
-- a draft template's checks are rewritten on save; classification rewrites a workload's meters;
-- Entra group to role mappings can be removed; an unapproved draft text block is replaced.
SET NOCOUNT ON;
GRANT DELETE ON core.ControlCheck  TO app_web;
GRANT DELETE ON core.WorkloadMeter TO app_web;
GRANT DELETE ON sec.GroupRoleMap   TO app_web;
GRANT DELETE ON ref.TextBlock      TO app_web;

-- The engine stores the triggered specialist reviews as JSON (discipline, decision type, activity, roles, reason);
-- varchar(200) truncated any real list (found by the web J3 walk). Widening only (schema only grows).
IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('wf.Request') AND name = 'TriggeredReviews' AND max_length <> -1)
    ALTER TABLE wf.Request ALTER COLUMN TriggeredReviews nvarchar(max) NULL;

-- My Work and the queues filter open tasks by role and scope.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Task_OpenByStage')
    CREATE INDEX IX_Task_OpenByStage ON wf.Task (StageCode, Status) INCLUDE (AssignedRoleCode, Discipline, DueUtc) WHERE Status = 'OPEN';
GO
