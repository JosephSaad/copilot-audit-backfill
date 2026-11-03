-- Worker and integrations: additive only (guards everywhere, nothing dropped or narrowed).
SET NOCOUNT ON;

-- Last cost center seen per person, so the directory sync can tell when a role holder moved (section 5.8).
IF OBJECT_ID('intg.PersonCostCenterSeen') IS NULL
CREATE TABLE intg.PersonCostCenterSeen
(
    PersonId       int           NOT NULL CONSTRAINT PK_PersonCostCenterSeen PRIMARY KEY,
    CostCenterCode nvarchar(100) NULL,
    SeenUtc        datetime2     NOT NULL CONSTRAINT DF_PersonCostCenterSeen_Seen DEFAULT SYSUTCDATETIME()
);

-- Last licensing reading per environment, for the drift evaluator's "was it already like this" check.
IF COL_LENGTH('intg.AllocationReading', 'ReadUtc') IS NULL
    ALTER TABLE intg.AllocationReading ADD ReadUtc datetime2 NULL CONSTRAINT DF_AllocationReading_Read DEFAULT SYSUTCDATETIME();

-- Delivery attempts per recipient and channel: retries reuse the row instead of a second insert.
IF COL_LENGTH('ntf.Delivery', 'Attempts') IS NULL
    ALTER TABLE ntf.Delivery ADD Attempts int NOT NULL CONSTRAINT DF_Delivery_Attempts DEFAULT 1;

-- The last time the ITSM poller could not read a ticket, and why (a deleted ticket is marked NOT_FOUND for review
-- instead of stopping the poll for every ticket after it).
IF COL_LENGTH('wf.ItsmTicket', 'PollError') IS NULL ALTER TABLE wf.ItsmTicket ADD PollError nvarchar(400) NULL;
IF COL_LENGTH('wf.ItsmTicket', 'PollErrorUtc') IS NULL ALTER TABLE wf.ItsmTicket ADD PollErrorUtc datetime2 NULL;

-- How long the worker keeps its own bookkeeping (job runs, sent notices' bodies, delivery records).
IF NOT EXISTS (SELECT 1 FROM ref.Setting WHERE SettingKey = 'Retention.WorkerRecordDays')
    INSERT ref.Setting (SettingKey, SettingValue, Description)
    VALUES ('Retention.WorkerRecordDays', N'180', N'Days the worker keeps job runs and delivery records, and the full text of processed outbox items. Older ones are removed (outbox items keep their key, so nothing is sent twice) by the daily Retention job. Ledger and audit tables are never touched.');

-- The decider recorded on decisions imported from Entra when Entra does not expose the approver to the
-- catalog reader role (the decision itself was made in My Access, by a person, under Entra's policy).
IF NOT EXISTS (SELECT 1 FROM sec.Person WHERE UserPrincipalName = N'system:entra-import')
    INSERT sec.Person (UserPrincipalName, DisplayName, AccountEnabled, IsSynthetic)
    VALUES (N'system:entra-import', N'Microsoft Entra (approver in My Access)', 0, 1);

GRANT DELETE ON intg.PersonCostCenterSeen TO app_worker;
GO
