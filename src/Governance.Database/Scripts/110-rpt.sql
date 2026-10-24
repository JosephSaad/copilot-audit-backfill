-- Reporting views: governance KPIs (strategy slide 5, Appendix C) and read-only views for Power BI.
SET NOCOUNT ON;
GO
-- Workloads in scope for governance: anything not discovered-only or retired.
CREATE OR ALTER VIEW rpt.vWorkloadInScope
AS
SELECT w.WorkloadId, w.PublicId, w.Name, w.WorkloadType, w.FundingOwnerId, fo.Name AS FundingOwnerName, fo.OwnerType,
       w.CostCenterId, w.ServiceId, s.Name AS ServiceName, w.LifecycleStage, w.SolutionClass, w.Criticality, w.State,
       CASE WHEN w.CostCenterId IS NOT NULL AND fo.Status = 'ACTIVE' THEN 1 ELSE 0 END AS HasOwnership,
       CASE WHEN EXISTS (SELECT 1 FROM core.WorkloadBinding b WHERE b.WorkloadId = w.WorkloadId AND b.EffectiveTo IS NULL)
             AND (EXISTS (SELECT 1 FROM wf.Decision d
                          WHERE d.WorkloadId = w.WorkloadId
                            AND d.Outcome IN ('APPROVED','APPROVED_WITH_CONDITIONS','CONSTRAINED_PILOT','EXTEND')
                            AND (d.ExpiresOn IS NULL OR d.ExpiresOn >= CAST(SYSUTCDATETIME() AS date)))
                  OR EXISTS (SELECT 1 FROM wf.ExceptionItem e WHERE e.WorkloadId = w.WorkloadId AND e.Status = 'ACTIVE'))
            THEN 1 ELSE 0 END AS OnApprovedRoute
FROM core.Workload w
JOIN core.FundingOwner fo ON fo.FundingOwnerId = w.FundingOwnerId
LEFT JOIN core.Service s ON s.ServiceId = w.ServiceId
WHERE w.State NOT IN ('DISCOVERED','RETIRED');
GO
CREATE OR ALTER VIEW rpt.vKpi
AS
SELECT 'OWNERSHIP_COVERAGE' AS KpiCode, N'Financial ownership coverage' AS Name,
       CAST(SUM(HasOwnership) AS decimal(18,2)) AS Numerator, CAST(COUNT(*) AS decimal(18,2)) AS Denominator, 1 AS Phase
FROM rpt.vWorkloadInScope
UNION ALL
SELECT 'APPROVED_ROUTE', N'Approved-route compliance', SUM(OnApprovedRoute), COUNT(*), 1
FROM rpt.vWorkloadInScope
UNION ALL
SELECT 'THRESHOLD_RESPONSE', N'Threshold response within target',
       SUM(CASE WHEN AckUtc IS NOT NULL AND AckUtc <= AckDueUtc THEN 1 ELSE 0 END), COUNT(*), 1
FROM ops.ConsumptionEvent
WHERE ReportedUtc >= DATEADD(DAY, -90, SYSUTCDATETIME())
UNION ALL
SELECT 'SHARED_EXHAUSTION_RISK', N'Shared-service exhaustion events (90 days)',
       SUM(CASE WHEN CriticalServiceAffected = 1 AND EventClass IN ('EXHAUSTION','NO_PERMITTED_SOURCE') THEN 1 ELSE 0 END), COUNT(*), 1
FROM ops.ConsumptionEvent
WHERE ReportedUtc >= DATEADD(DAY, -90, SYSUTCDATETIME())
UNION ALL
SELECT 'RECERT_COMPLETION', N'Recertification completion',
       SUM(CASE WHEN ci.Status = 'DONE' THEN 1 ELSE 0 END), COUNT(*), 1
FROM wf.CampaignItem ci JOIN wf.Campaign c ON c.CampaignId = ci.CampaignId
WHERE c.CampaignType = 'RECERTIFICATION' AND c.DueOn <= DATEADD(DAY, 30, CAST(SYSUTCDATETIME() AS date))
UNION ALL
SELECT 'OUTCOME_EVIDENCE', N'Outcome evidence coverage',
       SUM(CASE WHEN ci.OutcomeEvidence IS NOT NULL AND LEN(ci.OutcomeEvidence) > 0 THEN 1 ELSE 0 END), COUNT(*), 1
FROM wf.CampaignItem ci JOIN wf.Campaign c ON c.CampaignId = ci.CampaignId
WHERE c.CampaignType = 'RECERTIFICATION' AND ci.Status = 'DONE'
UNION ALL
SELECT 'UNAPPROVED_FINDINGS', N'Open unapproved-usage findings', COUNT(*), NULL, 1
FROM ops.Finding
WHERE Status IN ('OPEN','ACKNOWLEDGED')
  AND FindingType IN ('UNREGISTERED_ENVIRONMENT','UNREGISTERED_AGENT','UNREGISTERED_FLOW','ASSIGNMENT_WITHOUT_FUNDING','UNMATCHED_REGISTRY_AGENT');
GO
-- Decisions for auditors and Power BI (no personal data beyond names).
CREATE OR ALTER VIEW rpt.vDecision
AS
SELECT d.PublicId, d.DecisionType, d.Outcome, d.DecidedAtUtc, d.Source, d.RaciActivityCode, d.RaciVersionId,
       p.DisplayName AS Decider, d.DeciderRoleCode, d.DelegationId, d.ScopeText, d.ConditionsText,
       d.ExpiresOn, d.NextReviewOn, r.RequestNumber, fo.Name AS FundingOwner
FROM wf.Decision d
JOIN sec.Person p ON p.PersonId = d.DeciderPersonId
LEFT JOIN wf.Request r ON r.RequestId = d.RequestId
LEFT JOIN core.FundingOwner fo ON fo.FundingOwnerId = d.FundingOwnerId;
GO
