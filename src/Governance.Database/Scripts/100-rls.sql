-- Row-level security by funding owner: a second line of defense behind application authorization.
-- The application sets SESSION_CONTEXT (PersonId, IsCentral) read-only on every connection open.
-- Session context is trusted only for the application roles; gov_reader and db_owner see all rows
-- (a DBA can alter the policy anyway; tamper evidence comes from the ledger tables, not from RLS).
SET NOCOUNT ON;
GO
IF EXISTS (SELECT 1 FROM sys.security_policies WHERE name = 'FundingOwnerPolicy' AND schema_id = SCHEMA_ID('sec'))
    DROP SECURITY POLICY sec.FundingOwnerPolicy;
-- Dependents first: the request and task predicates bind to the funding owner predicate.
DROP FUNCTION IF EXISTS sec.fn_RequestAccess;
DROP FUNCTION IF EXISTS sec.fn_TaskAccess;
DROP FUNCTION IF EXISTS sec.fn_FundingOwnerAccess;
GO
CREATE FUNCTION sec.fn_FundingOwnerAccess (@FundingOwnerId int)
RETURNS TABLE
WITH SCHEMABINDING
AS
RETURN
    SELECT 1 AS AccessResult
    WHERE IS_MEMBER(N'gov_reader') = 1
       OR IS_MEMBER(N'db_owner') = 1
       OR (    (IS_MEMBER(N'app_web') = 1 OR IS_MEMBER(N'app_worker') = 1)
           AND (    CAST(SESSION_CONTEXT(N'IsCentral') AS bit) = 1
                OR EXISTS (SELECT 1
                           FROM sec.RoleAssignment AS ra
                           WHERE ra.PersonId  = CAST(SESSION_CONTEXT(N'PersonId') AS int)
                             AND ra.ScopeType = 'FUNDING_OWNER'
                             AND ra.ScopeId   = @FundingOwnerId
                             AND ra.EffectiveFrom <= CAST(SYSUTCDATETIME() AS date)
                             AND (ra.EffectiveTo IS NULL OR ra.EffectiveTo >= CAST(SYSUTCDATETIME() AS date)))
                OR EXISTS (SELECT 1
                           FROM sec.Delegation AS d
                           WHERE d.DelegatePersonId = CAST(SESSION_CONTEXT(N'PersonId') AS int)
                             AND d.FundingOwnerId = @FundingOwnerId
                             AND d.Status = 'ACTIVE'
                             AND d.EffectiveFrom <= CAST(SYSUTCDATETIME() AS date)
                             AND (d.EffectiveTo IS NULL OR d.EffectiveTo >= CAST(SYSUTCDATETIME() AS date)))));
GO
-- Requests: the funding owner's scope, plus the requester and sponsor themselves.
CREATE FUNCTION sec.fn_RequestAccess (@FundingOwnerId int, @RequesterPersonId int, @SponsorPersonId int)
RETURNS TABLE
WITH SCHEMABINDING
AS
RETURN
    SELECT 1 AS AccessResult
    FROM sec.fn_FundingOwnerAccess(@FundingOwnerId)
    UNION ALL
    SELECT 1
    WHERE (IS_MEMBER(N'app_web') = 1 OR IS_MEMBER(N'app_worker') = 1)
      AND CAST(SESSION_CONTEXT(N'PersonId') AS int) IN (@RequesterPersonId, @SponsorPersonId);
GO
-- Tasks: the funding owner's scope, tasks without a funding owner (central work), and the assignee.
CREATE FUNCTION sec.fn_TaskAccess (@FundingOwnerId int, @AssignedPersonId int)
RETURNS TABLE
WITH SCHEMABINDING
AS
RETURN
    SELECT 1 AS AccessResult
    FROM sec.fn_FundingOwnerAccess(@FundingOwnerId)
    UNION ALL
    SELECT 1
    WHERE (IS_MEMBER(N'app_web') = 1 OR IS_MEMBER(N'app_worker') = 1)
      AND (@FundingOwnerId IS NULL OR CAST(SESSION_CONTEXT(N'PersonId') AS int) = @AssignedPersonId);
GO
CREATE SECURITY POLICY sec.FundingOwnerPolicy
    ADD FILTER PREDICATE sec.fn_FundingOwnerAccess(FundingOwnerId) ON core.Workload,
    ADD FILTER PREDICATE sec.fn_FundingOwnerAccess(FundingOwnerId) ON core.WorkloadHistory,     -- RLS is not copied to history tables
    ADD BLOCK  PREDICATE sec.fn_FundingOwnerAccess(FundingOwnerId) ON core.Workload AFTER INSERT,
    ADD FILTER PREDICATE sec.fn_FundingOwnerAccess(FundingOwnerId) ON core.WorkloadBinding,
    ADD FILTER PREDICATE sec.fn_FundingOwnerAccess(FundingOwnerId) ON core.WorkloadBindingHistory,
    ADD FILTER PREDICATE sec.fn_FundingOwnerAccess(FundingOwnerId) ON core.FundingEnvelope,
    ADD FILTER PREDICATE sec.fn_FundingOwnerAccess(FundingOwnerId) ON core.FundingEnvelopeHistory,
    ADD FILTER PREDICATE sec.fn_FundingOwnerAccess(FundingOwnerId) ON core.EnvelopeCommitment,
    ADD FILTER PREDICATE sec.fn_FundingOwnerAccess(FundingOwnerId) ON core.ApprovedPosture,
    ADD FILTER PREDICATE sec.fn_FundingOwnerAccess(FundingOwnerId) ON core.ApprovedPostureHistory,
    ADD FILTER PREDICATE sec.fn_RequestAccess(FundingOwnerId, RequesterPersonId, SponsorPersonId) ON wf.Request,
    ADD BLOCK  PREDICATE sec.fn_RequestAccess(FundingOwnerId, RequesterPersonId, SponsorPersonId) ON wf.Request AFTER INSERT,
    ADD FILTER PREDICATE sec.fn_TaskAccess(FundingOwnerId, AssignedPersonId) ON wf.Task,
    ADD FILTER PREDICATE sec.fn_FundingOwnerAccess(FundingOwnerId) ON wf.ExceptionItem,
    ADD FILTER PREDICATE sec.fn_FundingOwnerAccess(FundingOwnerId) ON ops.ConsumptionEvent,
    ADD FILTER PREDICATE sec.fn_FundingOwnerAccess(FundingOwnerId) ON wf.CampaignItem
WITH (STATE = ON, SCHEMABINDING = ON);
GO
