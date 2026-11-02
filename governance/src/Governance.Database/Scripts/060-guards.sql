-- Fixes from the core review. Additive and rerunnable: widen, index, guard, extend row-level security.
SET NOCOUNT ON;

-- 1. Triggered reviews hold several hundred characters of JSON.
IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('wf.Request') AND name = 'TriggeredReviews' AND max_length <> -1)
    ALTER TABLE wf.Request ALTER COLUMN TriggeredReviews nvarchar(max) NULL;
IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('wf.Request') AND name = 'PatternResult' AND max_length <> -1)
    ALTER TABLE wf.Request ALTER COLUMN PatternResult nvarchar(max) NULL;

-- 12. Decision lookups by request (ledger tables accept nonclustered indexes).
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Decision_Request' AND object_id = OBJECT_ID('wf.Decision'))
    CREATE INDEX IX_Decision_Request ON wf.Decision (RequestId) INCLUDE (DecisionType, Outcome, DeciderPersonId, StageCode, DecidedAtUtc);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Decision_Workload' AND object_id = OBJECT_ID('wf.Decision'))
    CREATE INDEX IX_Decision_Workload ON wf.Decision (WorkloadId) INCLUDE (Outcome, ExpiresOn);
GO

-- 10. An active or retired RACI version and an active control template cannot change in place:
-- a change is a new version with two approvers (P3, SoD6).
CREATE OR ALTER TRIGGER sec.trg_RaciCell_Frozen ON sec.RaciCell
AFTER INSERT, UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    IF EXISTS (SELECT 1 FROM (SELECT RaciVersionId FROM inserted UNION SELECT RaciVersionId FROM deleted) AS x
               JOIN sec.RaciVersion v ON v.RaciVersionId = x.RaciVersionId
               WHERE v.Status IN ('PENDING','ACTIVE','RETIRED'))
        THROW 51001, 'Cells of a submitted, active or retired RACI version cannot change. Draft a new version.', 1;
END;
GO
CREATE OR ALTER TRIGGER core.trg_ControlTemplate_Frozen ON core.ControlTemplate
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    IF EXISTS (SELECT 1 FROM inserted i JOIN deleted d ON d.TemplateId = i.TemplateId
               WHERE d.Status IN ('ACTIVE','RETIRED')
                 AND (i.SettingsJson <> d.SettingsJson OR i.ServiceId <> d.ServiceId OR i.VersionNo <> d.VersionNo
                      OR ISNULL(i.PermittedExceptions, N'') <> ISNULL(d.PermittedExceptions, N'') OR i.OperatorRoles <> d.OperatorRoles))
        THROW 51002, 'An active or retired control template cannot change. Draft a new version.', 1;
END;
GO
CREATE OR ALTER TRIGGER core.trg_ControlCheck_Frozen ON core.ControlCheck
AFTER INSERT, UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    IF EXISTS (SELECT 1 FROM (SELECT TemplateId FROM inserted UNION SELECT TemplateId FROM deleted) AS x
               JOIN core.ControlTemplate t ON t.TemplateId = x.TemplateId
               WHERE t.Status IN ('ACTIVE','RETIRED'))
        THROW 51003, 'Checks of an active or retired control template cannot change. Draft a new version.', 1;
END;
GO
