-- Web smoke fixture: synthetic personas and a minimal foundation, enough to open intake and walk J2 end to end.
-- For local page checks only (the full demo comes from tools/Governance.Seed). All people are synthetic (@contoso.test).
SET NOCOUNT ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

DECLARE @today date = CAST(SYSUTCDATETIME() AS date);

MERGE sec.Person AS t
USING (VALUES
 ('po@contoso.test',     N'Pat Owner',          N'Digital Workplace', N'Digital', 'CC-9001'),
 ('steer@contoso.test',  N'Sam Steering',       N'Program Office',    N'Digital', 'CC-9001'),
 ('admin@contoso.test',  N'Ada Admin',          N'Digital Workplace', N'Digital', 'CC-9001'),
 ('intake@contoso.test', N'Ian Intake',         N'Digital Workplace', N'Digital', 'CC-9001'),
 ('ops1@contoso.test',   N'Olu Operator',       N'Platform Operations', N'Digital', 'CC-9001'),
 ('ops2@contoso.test',   N'Oona Operator',      N'Platform Operations', N'Digital', 'CC-9001'),
 ('arch@contoso.test',   N'Ari Architect',      N'Architecture',      N'Digital', 'CC-9001'),
 ('risk@contoso.test',   N'Rita Risk',          N'Security and Risk', N'Risk',    'CC-9002'),
 ('finops@contoso.test', N'Fin Lead',           N'FinOps',            N'Finance', 'CC-9003'),
 ('comm@contoso.test',   N'Cam Commercial',     N'Procurement',       N'Finance', 'CC-9003'),
 ('src@contoso.test',    N'Sol Sourcing',       N'Procurement',       N'Finance', 'CC-9003'),
 ('audit@contoso.test',  N'Aud Auditor',        N'Internal Audit',    N'Audit',   'CC-9004'),
 ('rbo@contoso.test',    N'Rhea Retail',        N'Retail Banking',    N'Retail',  'CC-1001'),
 ('rda1@contoso.test',   N'Dan Delegate',       N'Retail Banking',    N'Retail',  'CC-1001'),
 ('rda2@contoso.test',   N'Dee Delegate',       N'Retail Banking',    N'Retail',  'CC-1001'),
 ('req@contoso.test',    N'Rex Requester',      N'Retail Banking',    N'Retail',  'CC-1001'),
 ('esbo@contoso.test',   N'Eve Shared',         N'Enterprise IT',     N'Digital', 'CC-9001')
) AS s (Upn, Name, Department, Division, CostCenter)
ON t.UserPrincipalName = s.Upn
WHEN NOT MATCHED THEN INSERT (UserPrincipalName, DisplayName, Mail, Department, Division, CostCenterCode, AccountEnabled, IsSynthetic)
     VALUES (s.Upn, s.Name, s.Upn, s.Department, s.Division, s.CostCenter, 1, 1);

DECLARE @po int = (SELECT PersonId FROM sec.Person WHERE UserPrincipalName = 'po@contoso.test');
DECLARE @steer int = (SELECT PersonId FROM sec.Person WHERE UserPrincipalName = 'steer@contoso.test');
DECLARE @ops1 int = (SELECT PersonId FROM sec.Person WHERE UserPrincipalName = 'ops1@contoso.test');
DECLARE @rbo int = (SELECT PersonId FROM sec.Person WHERE UserPrincipalName = 'rbo@contoso.test');
DECLARE @rda1 int = (SELECT PersonId FROM sec.Person WHERE UserPrincipalName = 'rda1@contoso.test');
DECLARE @rda2 int = (SELECT PersonId FROM sec.Person WHERE UserPrincipalName = 'rda2@contoso.test');
DECLARE @esbo int = (SELECT PersonId FROM sec.Person WHERE UserPrincipalName = 'esbo@contoso.test');
DECLARE @finops int = (SELECT PersonId FROM sec.Person WHERE UserPrincipalName = 'finops@contoso.test');
DECLARE @comm int = (SELECT PersonId FROM sec.Person WHERE UserPrincipalName = 'comm@contoso.test');

IF NOT EXISTS (SELECT 1 FROM core.FundingOwner WHERE Code = 'RETAIL')
    INSERT core.FundingOwner (Code, Name, OwnerType, BusinessOwnerPersonId, FinancePartnerPersonId, Status, NotificationRecipientsConfirmed, AttributionTested, EffectiveFrom)
    VALUES ('RETAIL', N'Retail Banking', 'LOB', @rbo, @finops, 'ACTIVE', 1, 1, DATEADD(MONTH, -6, @today));
IF NOT EXISTS (SELECT 1 FROM core.FundingOwner WHERE Code = 'ES-IT')
    INSERT core.FundingOwner (Code, Name, OwnerType, BusinessOwnerPersonId, Status, IsCriticalShared, NotificationRecipientsConfirmed, AttributionTested, EffectiveFrom)
    VALUES ('ES-IT', N'Enterprise Shared IT', 'ENTERPRISE_SHARED', @esbo, 'ACTIVE', 1, 1, 1, DATEADD(MONTH, -6, @today));
DECLARE @retail int = (SELECT FundingOwnerId FROM core.FundingOwner WHERE Code = 'RETAIL');
DECLARE @esit int = (SELECT FundingOwnerId FROM core.FundingOwner WHERE Code = 'ES-IT');

IF NOT EXISTS (SELECT 1 FROM core.CostCenter WHERE Code = 'CC-1001')
    INSERT core.CostCenter (Code, Name, FundingOwnerId, ValidatedByFinance, EffectiveFrom) VALUES ('CC-1001', N'Retail digital channels', @retail, 1, DATEADD(MONTH, -6, @today));
IF NOT EXISTS (SELECT 1 FROM core.CostCenter WHERE Code = 'CC-9001')
    INSERT core.CostCenter (Code, Name, FundingOwnerId, ValidatedByFinance, EffectiveFrom) VALUES ('CC-9001', N'Enterprise IT platforms', @esit, 1, DATEADD(MONTH, -6, @today));
IF NOT EXISTS (SELECT 1 FROM core.AttributionRule WHERE FundingOwnerId = @retail)
    INSERT core.AttributionRule (FundingOwnerId, MatchDivision, Priority, EffectiveFrom) VALUES (@retail, N'Retail', 10, DATEADD(MONTH, -6, @today));

-- Roles
DECLARE @roles TABLE (Upn varchar(100), RoleCode varchar(40), ScopeType varchar(20), Fo int NULL);
INSERT @roles VALUES
 ('po@contoso.test','PLATFORM_OWNER','TENANT',NULL), ('steer@contoso.test','STEERING','TENANT',NULL), ('admin@contoso.test','WORKBENCH_ADMIN','TENANT',NULL),
 ('intake@contoso.test','INTAKE_ANALYST','TENANT',NULL), ('ops1@contoso.test','PLATFORM_OPERATOR','TENANT',NULL), ('ops2@contoso.test','PLATFORM_OPERATOR','TENANT',NULL),
 ('arch@contoso.test','ARCHITECT','TENANT',NULL), ('risk@contoso.test','RISK_REVIEWER','TENANT',NULL), ('finops@contoso.test','FINOPS_LEAD','TENANT',NULL),
 ('finops@contoso.test','FINOPS_ANALYST','TENANT',NULL), ('comm@contoso.test','COMMERCIAL_APPROVER','TENANT',NULL), ('src@contoso.test','SOURCING_AGENT','TENANT',NULL),
 ('audit@contoso.test','AUDITOR','TENANT',NULL),
 ('rbo@contoso.test','LOB_BUSINESS_OWNER','FUNDING_OWNER',@retail), ('rda1@contoso.test','DELEGATED_APPROVER','FUNDING_OWNER',@retail),
 ('rda2@contoso.test','DELEGATED_APPROVER','FUNDING_OWNER',@retail), ('req@contoso.test','PRODUCT_OWNER','FUNDING_OWNER',@retail),
 ('esbo@contoso.test','SHARED_SERVICE_OWNER','FUNDING_OWNER',@esit);
INSERT sec.RoleAssignment (PersonId, RoleCode, ScopeType, ScopeId, EffectiveFrom, Source, AcceptedUtc, ReviewDueOn)
SELECT p.PersonId, r.RoleCode, r.ScopeType, r.Fo, DATEADD(MONTH, -6, @today), 'SEED', SYSUTCDATETIME(), DATEADD(YEAR, 1, @today)
FROM @roles r JOIN sec.Person p ON p.UserPrincipalName = r.Upn
WHERE NOT EXISTS (SELECT 1 FROM sec.RoleAssignment x WHERE x.PersonId = p.PersonId AND x.RoleCode = r.RoleCode AND (x.ScopeId = r.Fo OR (x.ScopeId IS NULL AND r.Fo IS NULL)));

-- Catalogue, templates, pattern
IF NOT EXISTS (SELECT 1 FROM core.Service WHERE Code = 'CS-STD')
    INSERT core.Service (Code, Name, Harness, RouteType, TenantAvailability, InstitutionalStatus, Audiences, DataClasses, AccessTiers, OperationalOwnerPersonId, ReviewOn, Description)
    VALUES ('CS-STD', N'Copilot Studio (standard harness)', N'Standard', 'PPAC_ENVIRONMENT', 'GA', 'APPROVED', 'EMPLOYEES_TEAM,EMPLOYEES_LOB,EMPLOYEES_ALL', 'PUBLIC,INTERNAL,CONFIDENTIAL', 'Standard', @ops1, DATEADD(MONTH, 6, @today), N'Agents built in Copilot Studio on the standard harness.');
IF NOT EXISTS (SELECT 1 FROM core.Service WHERE Code = 'CHAT')
    INSERT core.Service (Code, Name, Harness, RouteType, TenantAvailability, InstitutionalStatus, Audiences, DataClasses, AccessTiers, OperationalOwnerPersonId, ReviewOn)
    VALUES ('CHAT', N'Microsoft 365 Copilot Chat', NULL, 'M365_POLICY', 'GA', 'APPROVED', 'EMPLOYEES_ALL', 'PUBLIC,INTERNAL', 'Standard,Extended', @ops1, DATEADD(MONTH, 6, @today));
DECLARE @cs int = (SELECT ServiceId FROM core.Service WHERE Code = 'CS-STD');
DECLARE @chat int = (SELECT ServiceId FROM core.Service WHERE Code = 'CHAT');

IF NOT EXISTS (SELECT 1 FROM core.ControlTemplate WHERE ServiceId = @cs)
BEGIN
    INSERT core.ControlTemplate (ServiceId, VersionNo, Status, SettingsJson, OperatorRoles, PlatformOwnerApprovalPersonId, SteeringApprovalPersonId, ActivatedUtc)
    VALUES (@cs, 1, 'ACTIVE', N'{"allocationCredits":"5000","tenantPoolEnabled":"false","agentLimitCredits":"2000"}', 'PLATFORM_OPERATOR', @po, @steer, SYSUTCDATETIME());
    DECLARE @t1 int = SCOPE_IDENTITY();
    INSERT core.ControlCheck (TemplateId, CheckCode, Description, Method, SettingKey, IsDenyCase, CaveatCode, SortOrder) VALUES
     (@t1, 'ALLOC', N'Allocation matches the approved posture.', 'READBACK', 'allocationCredits', 0, NULL, 10),
     (@t1, 'POOL', N'Draw from tenant capacity is off, so the allocation is a cap.', 'READBACK', 'tenantPoolEnabled', 0, 'C15', 20),
     (@t1, 'DENY', N'A test agent past its limit is refused (allow 15 minutes for enforcement).', 'ATTESTATION', NULL, 1, 'C08', 30),
     (@t1, 'FLOWS', N'Agent flows called by the agent are checked separately.', 'TEST_EVIDENCE', NULL, 0, 'C11', 40);
END
IF NOT EXISTS (SELECT 1 FROM core.ControlTemplate WHERE ServiceId = @chat)
BEGIN
    INSERT core.ControlTemplate (ServiceId, VersionNo, Status, SettingsJson, OperatorRoles, PlatformOwnerApprovalPersonId, SteeringApprovalPersonId, ActivatedUtc)
    VALUES (@chat, 1, 'ACTIVE', N'{"scopeGroup":"CGW Test - Copilot Chat - Retail Banking - Standard","perUserLimit":"100","autoApplyNewServices":"false"}', 'PLATFORM_OPERATOR', @po, @steer, SYSUTCDATETIME());
    DECLARE @t2 int = SCOPE_IDENTITY();
    INSERT core.ControlCheck (TemplateId, CheckCode, Description, Method, SettingKey, IsDenyCase, CaveatCode, SortOrder) VALUES
     (@t2, 'SCOPE', N'The credit policy is scoped to the LOB tier group only.', 'ATTESTATION', 'scopeGroup', 0, 'C14', 10),
     (@t2, 'AUTOAPPLY', N'Auto-apply new services is off.', 'ATTESTATION', 'autoApplyNewServices', 0, 'C16', 20),
     (@t2, 'DENY', N'A user outside the group gets no credits from this policy.', 'ATTESTATION', NULL, 1, NULL, 30);
END
IF NOT EXISTS (SELECT 1 FROM core.ApprovedPattern WHERE Code = 'CS-DEV-INTERNAL')
    INSERT core.ApprovedPattern (Code, Name, ServiceId, Audiences, MaxDataClass, ActionScopes, LifecycleStages, TriggerTypes, Tiers, MaxCreditsPerMonth)
    VALUES ('CS-DEV-INTERNAL', N'Internal agent in development', @cs, 'EMPLOYEES_TEAM,EMPLOYEES_LOB', 'INTERNAL', 'READ_ONLY,INTERNAL_ACTIONS', 'LOCAL,DEVELOPMENT,TEST', 'USER_INITIATED', '*', 3000);

-- Envelopes and delegations
IF NOT EXISTS (SELECT 1 FROM core.FundingEnvelope WHERE FundingOwnerId = @retail AND ServiceId = @cs)
    INSERT core.FundingEnvelope (FundingOwnerId, ServiceId, PeriodStart, PeriodEnd, Amount, Unit, Status) VALUES (@retail, @cs, DATEFROMPARTS(YEAR(@today), 1, 1), DATEFROMPARTS(YEAR(@today), 12, 31), 100000, 'COPILOT_CREDIT', 'ACTIVE');
IF NOT EXISTS (SELECT 1 FROM core.FundingEnvelope WHERE FundingOwnerId = @retail AND ServiceId = @chat)
    INSERT core.FundingEnvelope (FundingOwnerId, ServiceId, PeriodStart, PeriodEnd, Amount, Unit, Status) VALUES (@retail, @chat, DATEFROMPARTS(YEAR(@today), 1, 1), DATEFROMPARTS(YEAR(@today), 12, 31), 20000, 'COPILOT_CREDIT', 'ACTIVE');
IF NOT EXISTS (SELECT 1 FROM core.FundingEnvelope WHERE FundingOwnerId = @esit AND ServiceId = @cs)
    INSERT core.FundingEnvelope (FundingOwnerId, ServiceId, PeriodStart, PeriodEnd, Amount, Unit, Status) VALUES (@esit, @cs, DATEFROMPARTS(YEAR(@today), 1, 1), DATEFROMPARTS(YEAR(@today), 12, 31), 50000, 'COPILOT_CREDIT', 'ACTIVE');
DECLARE @env int = (SELECT EnvelopeId FROM core.FundingEnvelope WHERE FundingOwnerId = @retail AND ServiceId = @cs);
IF NOT EXISTS (SELECT 1 FROM sec.Delegation WHERE FundingOwnerId = @retail)
BEGIN
    INSERT sec.Delegation (FundingOwnerId, DelegatePersonId, GrantedByPersonId, MaxCreditsPerRequest, EffectiveFrom, EffectiveTo, Status) VALUES (@retail, @rda1, @rbo, 5000, DATEADD(MONTH, -1, @today), DATEADD(YEAR, 1, @today), 'ACTIVE');
    INSERT sec.DelegationEnvelope (DelegationId, EnvelopeId) VALUES (SCOPE_IDENTITY(), @env);
    INSERT sec.Delegation (FundingOwnerId, DelegatePersonId, GrantedByPersonId, MaxCreditsPerRequest, EffectiveFrom, EffectiveTo, Status) VALUES (@retail, @rda2, @rbo, 5000, DATEADD(MONTH, -1, @today), DATEADD(YEAR, 1, @today), 'ACTIVE');
    INSERT sec.DelegationEnvelope (DelegationId, EnvelopeId) VALUES (SCOPE_IDENTITY(), @env);
END

IF NOT EXISTS (SELECT 1 FROM core.CommercialItem)
    INSERT core.CommercialItem (ItemType, Name, Quantity, Unit, StartOn, EndOn, RenewalOn, BillingScope, ContractRef, PurchaseAuthorityPersonId, ReconciledToContract)
    VALUES ('P3', N'Copilot Credit P3 plan 2026', 1000000, 'P3_COMMIT_UNIT', DATEFROMPARTS(YEAR(@today), 1, 1), DATEFROMPARTS(YEAR(@today), 12, 31), DATEFROMPARTS(YEAR(@today), 12, 1), N'Tenant billing account', N'EA-2026-001', @comm, 1);

IF NOT EXISTS (SELECT 1 FROM intg.AccessPackageMap)
    INSERT intg.AccessPackageMap (AccessPackageId, DisplayName, FundingOwnerId, ServiceId, Tier) VALUES (NEWID(), N'Copilot Chat - RETAIL - Standard', @retail, @chat, N'Standard');

-- A successful inventory run so setup step 6 counts; RACI v1 approved by two people; standards approved; intake open.
IF NOT EXISTS (SELECT 1 FROM intg.SyncRun WHERE Integration = 'X4')
    INSERT intg.SyncRun (Integration, Mode, TriggeredByPersonId, FinishedUtc, Status, ItemsRead, Message) VALUES ('X4', 'TEST_DOUBLE', @ops1, SYSUTCDATETIME(), 'SUCCEEDED', 0, N'Fixture run.');
UPDATE sec.RaciVersion SET Status = 'ACTIVE', PlatformOwnerApprovalPersonId = @po, PlatformOwnerApprovalUtc = SYSUTCDATETIME(),
       SteeringApprovalPersonId = @steer, SteeringApprovalUtc = SYSUTCDATETIME(), ActivatedUtc = SYSUTCDATETIME()
WHERE VersionNo = 1 AND Status <> 'ACTIVE';
IF NOT EXISTS (SELECT 1 FROM ref.Setting WHERE SettingKey = 'Setup.StandardsApprovedUtc')
    INSERT ref.Setting (SettingKey, SettingValue, Description) VALUES ('Setup.StandardsApprovedUtc', CONVERT(varchar(40), SYSUTCDATETIME(), 126), N'Fixture.');
UPDATE ref.Setting SET SettingValue = 'true' WHERE SettingKey = 'Intake.Open';

COMMIT TRANSACTION;
