-- Reference seed: the operating model as data. Safe to rerun: rows are upserted by their codes, and
-- RACI version 1 and workflow definition version 1 are created once and never overwritten after activation.
SET NOCOUNT ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

------------------------------------------------------------------------------------------------
-- Settings
------------------------------------------------------------------------------------------------
MERGE ref.Setting AS t
USING (VALUES
    ('Intake.Open',               N'false', N'Intake (B1) stays closed until setup steps 0 to 7 are complete.'),
    ('Calendar.TimeZone',         N'Eastern Standard Time', N'Institution time zone for business calendars.'),
    ('Sla.ReturnedAutoCloseDays', N'10',    N'Returned requests close after this many business days without a response (B7).'),
    ('Exception.NoticeDays',      N'30,7,1', N'Days before expiry for exception notices (N13).'),
    ('Commercial.NoticeDays',     N'90,60,30', N'Days before renewal or expiry for commercial notices (N18).'),
    ('Inventory.MaxAgeDays',      N'7',     N'Inventory sync older than this raises N21.'),
    ('Attestation.OverdueDays',   N'5',     N'Business days after the monthly attestation is due before escalation (N22).'),
    ('Evidence.MaxUploadMb',      N'20',    N'Largest evidence file accepted.'),
    ('Evidence.AllowedTypes',     N'.png,.jpg,.jpeg,.pdf,.csv,.json,.xlsx,.docx,.txt', N'Evidence file types accepted.'),
    ('Evidence.RetentionYears',   N'7',     N'Evidence content is purged after this many years; hash and metadata stay.'),
    ('Workbench.BaseUrl',         N'http://localhost:5450', N'Address used in notification links.'),
    ('Directory.StaleAfterRuns',  N'2',     N'Missed sync runs before the staleness banner shows.')
) AS s (SettingKey, SettingValue, Description)
ON t.SettingKey = s.SettingKey
WHEN NOT MATCHED THEN INSERT (SettingKey, SettingValue, Description) VALUES (s.SettingKey, s.SettingValue, s.Description)
WHEN MATCHED THEN UPDATE SET Description = s.Description;

------------------------------------------------------------------------------------------------
-- Caveat register (Appendix B)
------------------------------------------------------------------------------------------------
MERGE ref.Caveat AS t
USING (VALUES
 ('C01', N'Availability and approval', N'Tenant availability and institutional status are separate service fields.', N'A service can be available in the tenant and still not approved here.'),
 ('C02', N'Preview and customer adoption', N'Preview adoption rule; adoption decision with expiry.', N'Preview or customer-facing routes need an explicit adoption decision with audience, test data, expiry and exit criteria.'),
 ('C03', N'Independent classification', N'Four independent columns; no derived values.', N'Funding owner, solution class, lifecycle stage and service are answered separately; none is derived from another.'),
 ('C04', N'Control object boundaries', N'One binding row per Microsoft object type; no universal policy object.', N'An environment, an agent, a credit policy and an access package are different objects with different controls.'),
 ('C05', N'P3 financial benefit', N'P3 recorded as a commercial commitment; no personal or per-agent balances.', N'P3 commit units offset eligible usage at the billing scope; no agent owns a share.'),
 ('C06', N'Commercial terms and expiry', N'Renewal settings recorded; N18 reminders.', N'Packs and P3 plans expire; renewal needs a decision by the commercial authority.'),
 ('C07', N'Critical-service continuity', N'Continuity acceptance at D5; G4 checks.', N'Critical shared services need funding that does not depend on discretionary LOB envelopes.'),
 ('C08', N'Limits and alerts', N'Budget alerts never treated as stops; enforcement lag tested in D4.', N'An Azure budget or alert notifies; it does not stop Copilot Studio consumption.'),
 ('C09', N'License inclusion', N'License determination per scenario (C7); no blanket rule.', N'Whether a scenario is included in a license is decided per scenario by the licensing owner.'),
 ('C10', N'Cowork access and overlap', N'Attestation lists every policy that includes Cowork and its audience.', N'A person can be in several spending policies; only one applies.'),
 ('C11', N'Studio environment and agent flows', N'Separate flow check in D1 and D4.', N'An agent limit does not stop the agent flows it calls.'),
 ('C12', N'GitHub harness costs', N'FinOps review trigger; build and test usage treated as billable.', N'Building and testing with the GitHub Copilot harness consumes billable credits.'),
 ('C13', N'Customer workload costs', N'Customer forecast fields; no license-based assumption.', N'Customer traffic is not covered by employee licenses.'),
 ('C14', N'Chat policy scale', N'Cohort design within the 10-policy limit; separate attribution map.', N'A tenant has at most 10 Copilot Chat credit policies.'),
 ('C15', N'TenantPool automation and group rules', N'Group rule lock recorded; no automation in Phase 1.', N'An environment group rule can lock the tenant-pool setting (TenantPoolLockedByPolicy).'),
 ('C16', N'M365 policy defaults', N'Auto-apply setting in every attestation; monthly re-attestation.', N'Auto-apply new services is on by default; new services can join a policy without a change here.'),
 ('C17', N'Reporting latency and accounting', N'Freshness on every metric; actual and amortized kept separate (Phase 2).', N'Usage and cost data arrive late and in different bases.'),
 ('C18', N'Forecast and licensing economics', N'Comparison method recorded; no prompt-count thresholds.', N'Credits versus license is a method, not a prompt count.'),
 ('C19', N'Shared P3 scope', N'No claim of a reserved balance for any agent.', N'A P3 plan is shared at its billing scope.'),
 ('C20', N'Graph beta Cost API', N'Evidence only, behind a feature flag.', N'The beta Cost Management API is never used for a control decision.'),
 ('C21', N'Forecasts and control actions', N'No automated purchases or shutdowns.', N'Forecasts inform decisions; people decide purchases and shutdowns.'),
 ('C22', N'Scope boundary', N'Section 2.3 handoffs.', N'License migration, agent development, security governance and GL posting stay with their owners.')
) AS s (CaveatCode, Title, Control, Guidance)
ON t.CaveatCode = s.CaveatCode
WHEN NOT MATCHED THEN INSERT (CaveatCode, Title, Control, Guidance) VALUES (s.CaveatCode, s.Title, s.Control, s.Guidance)
WHEN MATCHED THEN UPDATE SET Title = s.Title, Control = s.Control, Guidance = s.Guidance;

------------------------------------------------------------------------------------------------
-- RACI functions and roles
------------------------------------------------------------------------------------------------
MERGE sec.RaciFunction AS t
USING (VALUES
 ('BUSINESS_OWNER', N'Business / shared owner',      'FUNDING_OWNER', 'LOB_BUSINESS_OWNER', 'PRODUCT_OWNER',     'LOB_BUSINESS_OWNER', 1),
 ('PLATFORM_OWNER', N'Platform owner (Digital Workplace)', 'TENANT',   'PLATFORM_OWNER',     'PLATFORM_OPERATOR', 'PLATFORM_OWNER', 2),
 ('FINANCE',        N'Finance / TBM / FinOps',       'TENANT',        'FINOPS_LEAD',        'FINOPS_ANALYST',    'FINOPS_ANALYST', 3),
 ('ARCHITECTURE',   N'Architecture',                 'TENANT',        'ARCHITECT',          'ARCHITECT',         'ARCHITECT', 4),
 ('ENG_OPS',        N'Engineering / Operations',     'TENANT',        'PLATFORM_OPERATOR',  'PLATFORM_OPERATOR', 'PLATFORM_OPERATOR', 5),
 ('SECURITY_RISK',  N'Security / Risk',              'TENANT',        'RISK_REVIEWER',      'RISK_REVIEWER',     'RISK_REVIEWER', 6),
 ('COMMERCIAL',     N'Commercial authority',         'TENANT',        'COMMERCIAL_APPROVER','COMMERCIAL_APPROVER','COMMERCIAL_APPROVER', 7),
 ('SOURCING',       N'Sourcing',                     'TENANT',        'SOURCING_AGENT',     'SOURCING_AGENT',    'SOURCING_AGENT', 8),
 ('MICROSOFT',      N'Microsoft',                    'EXTERNAL',      NULL,                 NULL,                NULL, 9)
) AS s (FunctionCode, Name, ScopeType, DecisionRoleCode, WorkRoleCode, ConsultRoleCode, SortOrder)
ON t.FunctionCode = s.FunctionCode
WHEN NOT MATCHED THEN INSERT (FunctionCode, Name, ScopeType, DecisionRoleCode, WorkRoleCode, ConsultRoleCode, SortOrder)
     VALUES (s.FunctionCode, s.Name, s.ScopeType, s.DecisionRoleCode, s.WorkRoleCode, s.ConsultRoleCode, s.SortOrder)
WHEN MATCHED THEN UPDATE SET Name = s.Name, ScopeType = s.ScopeType, DecisionRoleCode = s.DecisionRoleCode,
     WorkRoleCode = s.WorkRoleCode, ConsultRoleCode = s.ConsultRoleCode, SortOrder = s.SortOrder;

MERGE sec.RoleDefinition AS t
USING (VALUES
 -- LOB roles (scope: funding owner)
 ('LOB_BUSINESS_OWNER', N'LOB business owner', N'Business case and outcome; funding and annual budget; overage authorization; continuity acceptance; recertification and retirement decisions. Approves envelopes and delegations.', 'BUSINESS_OWNER', 'FUNDING_OWNER', 0, 1, 1, 10),
 ('SHARED_SERVICE_OWNER', N'Shared-service owner', N'Business owner of an Enterprise Shared funding owner; accepts continuity risk (F9, G4).', 'BUSINESS_OWNER', 'FUNDING_OWNER', 0, 1, 1, 11),
 ('DELEGATED_APPROVER', N'LOB delegated approver', N'Routine approvals within a delegated envelope (B5); approver for the LOB tier access package in Entra. Never their own request.', 'BUSINESS_OWNER', 'FUNDING_OWNER', 0, 1, 1, 12),
 ('WORKLOAD_SPONSOR', N'Workload sponsor', N'Sponsors a request; owns purpose and outcome claims; receives lifecycle notices.', 'BUSINESS_OWNER', 'FUNDING_OWNER', 0, 1, 0, 13),
 ('PRODUCT_OWNER', N'Product or solution owner', N'Classification inputs, support contact, criticality, outcome evidence, retirement plan.', 'BUSINESS_OWNER', 'FUNDING_OWNER', 0, 1, 0, 14),
 ('LOB_FINANCE_PARTNER', N'LOB finance partner', N'Validates cost centers and envelopes; reviews showback.', 'FINANCE', 'FUNDING_OWNER', 0, 1, 0, 15),
 ('LOB_CHAMPION', N'LOB champion', N'Adoption support; no approval rights.', NULL, 'FUNDING_OWNER', 0, 1, 0, 16),
 -- Central roles (scope: tenant)
 ('PLATFORM_OWNER', N'Platform Owner', N'Accountable for classification and routing, platform features, environment and policy pattern, implementation, monitoring and response, Microsoft evidence; owns the catalog and the Workbench as a product.', 'PLATFORM_OWNER', 'TENANT', 1, 0, 1, 20),
 ('INTAKE_ANALYST', N'Intake and entitlement analyst', N'Completeness and sponsorship checks (B2), duplicate detection, fulfillment preparation (B10).', 'ENG_OPS', 'TENANT', 1, 0, 1, 21),
 ('PLATFORM_OPERATOR', N'Platform operator', N'Executes configuration through ITSM tasks; verification (D1); inventory sync; event response (G1 to G5); attestation.', 'ENG_OPS', 'TENANT', 1, 0, 1, 22),
 ('ARCHITECT', N'Architect', N'Classification (B3); environment and policy pattern; capability evidence validation; specialist review (B8).', 'ARCHITECTURE', 'TENANT', 1, 0, 1, 23),
 ('RISK_REVIEWER', N'Risk reviewer', N'Security and risk acceptance, including privacy and model risk; preview adoption with Architecture (C8).', 'SECURITY_RISK', 'TENANT', 1, 0, 1, 24),
 ('FINOPS_LEAD', N'FinOps lead', N'Accountable for P3 evaluation, credits-versus-license review, forecast method, cost-center mapping, showback and reconciliation.', 'FINANCE', 'TENANT', 1, 0, 1, 25),
 ('FINOPS_ANALYST', N'FinOps analyst', N'Funding and overage analysis; envelopes; FinOps reviews.', 'FINANCE', 'TENANT', 1, 0, 1, 26),
 ('COMMERCIAL_APPROVER', N'Commercial approver', N'Approves P3 and pack purchases and renewals.', 'COMMERCIAL', 'TENANT', 1, 0, 1, 27),
 ('SOURCING_AGENT', N'Sourcing agent', N'Executes approved purchases; supports commercial evaluation.', 'SOURCING', 'TENANT', 1, 0, 1, 28),
 ('LICENSING_OWNER', N'Licensing owner', N'Qualified license check (C7); named-license comparison (F7).', NULL, 'TENANT', 1, 0, 1, 29),
 ('REPORTING_TEAM', N'Reporting and data team', N'Ledger collection and publication (Phase 2).', 'FINANCE', 'TENANT', 1, 0, 0, 30),
 ('STEERING', N'Program Office and Steering', N'Six-week validation, quarterly steering, KPI forums; approves RACI versions and templates with the Platform Owner.', NULL, 'TENANT', 1, 0, 1, 31),
 ('WORKBENCH_ADMIN', N'Workbench administrator', N'Configuration, integrations, templates, notification rules. No business approvals; cannot edit decisions (SoD5).', NULL, 'TENANT', 1, 0, 0, 32),
 ('AUDITOR', N'Auditor', N'Second and third line review: read-only access to decisions, evidence and audit; export.', NULL, 'TENANT', 1, 0, 0, 33)
) AS s (RoleCode, Name, Description, FunctionCode, ScopeType, IsCentral, IsLob, CanDecide, SortOrder)
ON t.RoleCode = s.RoleCode
WHEN NOT MATCHED THEN INSERT (RoleCode, Name, Description, FunctionCode, ScopeType, IsCentral, IsLob, CanDecide, SortOrder)
     VALUES (s.RoleCode, s.Name, s.Description, s.FunctionCode, s.ScopeType, s.IsCentral, s.IsLob, s.CanDecide, s.SortOrder)
WHEN MATCHED THEN UPDATE SET Name = s.Name, Description = s.Description, FunctionCode = s.FunctionCode, ScopeType = s.ScopeType,
     IsCentral = s.IsCentral, IsLob = s.IsLob, CanDecide = s.CanDecide, SortOrder = s.SortOrder;

DELETE FROM sec.RolePermission;
INSERT sec.RolePermission (RoleCode, Permission) VALUES
 ('LOB_BUSINESS_OWNER','fundingowner.view'),('LOB_BUSINESS_OWNER','envelope.approve'),('LOB_BUSINESS_OWNER','delegation.manage'),
 ('LOB_BUSINESS_OWNER','request.approve.routine'),('LOB_BUSINESS_OWNER','request.decide'),('LOB_BUSINESS_OWNER','role.assign.lob'),('LOB_BUSINESS_OWNER','workload.view'),
 ('SHARED_SERVICE_OWNER','fundingowner.view'),('SHARED_SERVICE_OWNER','envelope.approve'),('SHARED_SERVICE_OWNER','delegation.manage'),
 ('SHARED_SERVICE_OWNER','request.approve.routine'),('SHARED_SERVICE_OWNER','request.decide'),('SHARED_SERVICE_OWNER','role.assign.lob'),('SHARED_SERVICE_OWNER','workload.view'),
 ('DELEGATED_APPROVER','fundingowner.view'),('DELEGATED_APPROVER','request.approve.routine'),('DELEGATED_APPROVER','workload.view'),
 ('WORKLOAD_SPONSOR','request.create'),('WORKLOAD_SPONSOR','request.endorse'),('WORKLOAD_SPONSOR','workload.view'),
 ('PRODUCT_OWNER','request.create'),('PRODUCT_OWNER','workload.edit'),('PRODUCT_OWNER','evidence.upload'),('PRODUCT_OWNER','workload.view'),
 ('LOB_FINANCE_PARTNER','fundingowner.view'),('LOB_FINANCE_PARTNER','envelope.view'),('LOB_FINANCE_PARTNER','workload.view'),
 ('LOB_CHAMPION','fundingowner.view'),
 ('PLATFORM_OWNER','registry.edit'),('PLATFORM_OWNER','raci.approve'),('PLATFORM_OWNER','template.approve'),('PLATFORM_OWNER','request.decide'),('PLATFORM_OWNER','intake.open'),
 ('PLATFORM_OWNER','workload.view'),('PLATFORM_OWNER','fundingowner.view'),('PLATFORM_OWNER','role.assign.lob'),('PLATFORM_OWNER','role.assign.central'),
 ('INTAKE_ANALYST','queue.intake'),('INTAKE_ANALYST','workload.view'),
 ('PLATFORM_OPERATOR','queue.fulfillment'),('PLATFORM_OPERATOR','verify'),('PLATFORM_OPERATOR','attest'),('PLATFORM_OPERATOR','sync.run'),('PLATFORM_OPERATOR','event.respond'),('PLATFORM_OPERATOR','workload.view'),('PLATFORM_OPERATOR','registry.claim'),
 ('ARCHITECT','queue.classification'),('ARCHITECT','review'),('ARCHITECT','template.draft'),('ARCHITECT','workload.view'),
 ('RISK_REVIEWER','review'),('RISK_REVIEWER','request.decide'),('RISK_REVIEWER','workload.view'),
 ('FINOPS_LEAD','registry.finance'),('FINOPS_LEAD','envelope.manage'),('FINOPS_LEAD','request.decide'),('FINOPS_LEAD','review'),('FINOPS_LEAD','workload.view'),('FINOPS_LEAD','fundingowner.view'),
 ('FINOPS_ANALYST','registry.finance'),('FINOPS_ANALYST','envelope.manage'),('FINOPS_ANALYST','review'),('FINOPS_ANALYST','workload.view'),('FINOPS_ANALYST','fundingowner.view'),
 ('COMMERCIAL_APPROVER','request.decide'),('COMMERCIAL_APPROVER','commercial.view'),
 ('SOURCING_AGENT','commercial.view'),('SOURCING_AGENT','commercial.execute'),
 ('LICENSING_OWNER','license.determine'),
 ('STEERING','raci.approve'),('STEERING','template.approve'),('STEERING','reports.view'),
 ('WORKBENCH_ADMIN','admin.configure'),
 ('AUDITOR','audit.view'),('AUDITOR','audit.export'),('AUDITOR','reports.view');

------------------------------------------------------------------------------------------------
-- RACI activities (19-row matrix) and version 1
------------------------------------------------------------------------------------------------
MERGE sec.RaciActivity AS t
USING (VALUES
 ('BUSINESS_CASE',        N'Business case and outcome',                 N'B1, D5, I1',        N'Business case and measurable outcome', 1),
 ('CLASSIFICATION_ROUTING', N'Solution classification and routing',     N'B3, B4',            N'Solution classification, routing and platform feature approval', 2),
 ('PLATFORM_FEATURE',     N'Platform feature approval',                 N'B8, C8',            N'Solution classification, routing and platform feature approval', 3),
 ('SECURITY_RISK',        N'Security and risk acceptance',              N'B8, B9, C8',        N'Security and risk acceptance', 4),
 ('FUNDING_BUDGET',       N'Funding and annual budget',                 N'A2, B5, envelopes', N'Funding, annual budget and overage authorization', 5),
 ('P3_EVALUATION',        N'P3 evaluation and recommendation',          N'F1 to F3',          N'P3 evaluation and purchase recommendation', 6),
 ('P3_PURCHASE',          N'P3 and pack purchase approval',             N'F4, F6',            N'Footnote: Commercial authority approves, Sourcing executes', 7),
 ('P3_RENEWAL',           N'P3 and pack renewal approval',              N'F10, I2',           N'Footnote: Commercial authority approves, Sourcing executes', 8),
 ('CREDITS_VS_LICENSE',   N'Credits-versus-license review',             N'C7, F7, F8',        N'Consumption forecast, estimator and credits-versus-license review', 9),
 ('ENV_POLICY_PATTERN',   N'Environment and policy pattern',            N'A3, C1 to C9, E',   N'Environment and policy pattern', 10),
 ('COST_CENTER_MAPPING',  N'Subscription and cost-center mapping',      N'A2, C6, H3',        N'Cost-center mapping, showback, chargeback and reconciliation', 11),
 ('TECH_IMPLEMENTATION',  N'Technical implementation',                  N'B10, D1',           N'Technical implementation, monitoring and operational response', 12),
 ('FORECAST_METHOD',      N'Forecast and estimator methodology',        N'F2, H5',            N'Consumption forecast, estimator and credits-versus-license review', 13),
 ('MONITORING_RESPONSE',  N'Monitoring and operational response',       N'G1 to G5',          N'Technical implementation, monitoring and operational response', 14),
 ('SHOWBACK',             N'Showback, chargeback and reconciliation',   N'H4',                N'Cost-center mapping, showback, chargeback and reconciliation', 15),
 ('OVERAGE',              N'Overage budget authorization',              N'G3, J4',            N'Funding, annual budget and overage authorization', 16),
 ('SHARED_CONTINUITY',    N'Shared-service continuity acceptance',      N'D5, F9, G4',        N'Enterprise Shared continuity acceptance', 17),
 ('LIFECYCLE_RETIREMENT', N'Lifecycle review and retirement',           N'I1, I4',            N'Lifecycle recertification and retirement', 18),
 ('MS_EVIDENCE',          N'Microsoft capability evidence validation',  N'A1, B8, J1',        N'Microsoft capability evidence validation', 19)
) AS s (ActivityCode, Name, GovernsStages, SimplifiedRow, SortOrder)
ON t.ActivityCode = s.ActivityCode
WHEN NOT MATCHED THEN INSERT (ActivityCode, Name, GovernsStages, SimplifiedRow, SortOrder) VALUES (s.ActivityCode, s.Name, s.GovernsStages, s.SimplifiedRow, s.SortOrder)
WHEN MATCHED THEN UPDATE SET Name = s.Name, GovernsStages = s.GovernsStages, SimplifiedRow = s.SimplifiedRow, SortOrder = s.SortOrder;

IF NOT EXISTS (SELECT 1 FROM sec.RaciVersion WHERE VersionNo = 1)
BEGIN
    INSERT sec.RaciVersion (VersionNo, Status, Notes)
    VALUES (1, 'DRAFT', N'Seeded from the operating model''s 19-row matrix. OD13: the 19-row values are the source; Sourcing is C on lifecycle retirement and I on environment pattern, technical implementation, monitoring, Microsoft evidence and security risk acceptance.');

    DECLARE @v int = SCOPE_IDENTITY();
    -- Columns: BUSINESS_OWNER, PLATFORM_OWNER, FINANCE, ARCHITECTURE, ENG_OPS, SECURITY_RISK, COMMERCIAL, SOURCING, MICROSOFT
    DECLARE @m TABLE (ActivityCode varchar(60), Bo varchar(3), Po varchar(3), Fi varchar(3), Ar varchar(3), Eo varchar(3), Sr varchar(3), Co varchar(3), So varchar(3), Ms varchar(3));
    INSERT @m VALUES
     ('BUSINESS_CASE',         'A/R','C','C','C','I','I','I','I','I'),
     ('CLASSIFICATION_ROUTING','C','A','C','R','C','C','I','C','C'),
     ('PLATFORM_FEATURE',      'C','A','C','R','C','C','I','I','C'),
     ('SECURITY_RISK',         'C','C','I','C','C','A/R','I','I','C'),
     ('FUNDING_BUDGET',        'A/R','C','R','C','I','C','C','C','I'),
     ('P3_EVALUATION',         'C','C','A/R','C','I','C','C','R','C'),
     ('P3_PURCHASE',           'C','C','R','I','I','C','A','R','C'),
     ('P3_RENEWAL',            'C','C','R','I','C','I','A','R','C'),
     ('CREDITS_VS_LICENSE',    'C','R','A/R','C','I','C','C','C','C'),
     ('ENV_POLICY_PATTERN',    'I','A','C','R','R','C','I','I','C'),
     ('COST_CENTER_MAPPING',   'C','C','A/R','C','R','I','I','C','C'),
     ('TECH_IMPLEMENTATION',   'I','A','I','C','R','C','I','I','C'),
     ('FORECAST_METHOD',       'C','R','A/R','C','C','I','I','C','C'),
     ('MONITORING_RESPONSE',   'I','A','C','C','R','C','I','I','I'),
     ('SHOWBACK',              'C','C','A/R','I','R','I','I','C','C'),
     ('OVERAGE',               'A/R','C','R','C','C','I','C','I','I'),
     ('SHARED_CONTINUITY',     'A','C','C','C','R','C','I','I','I'),
     ('LIFECYCLE_RETIREMENT',  'A','R','C','C','R','C','I','C','I'),
     ('MS_EVIDENCE',           'I','A','C','R','C','C','I','I','C');

    INSERT sec.RaciCell (RaciVersionId, ActivityCode, FunctionCode, RaciValue)
    SELECT @v, m.ActivityCode, f.FunctionCode, f.Val
    FROM @m m
    CROSS APPLY (VALUES ('BUSINESS_OWNER', m.Bo), ('PLATFORM_OWNER', m.Po), ('FINANCE', m.Fi), ('ARCHITECTURE', m.Ar), ('ENG_OPS', m.Eo),
                        ('SECURITY_RISK', m.Sr), ('COMMERCIAL', m.Co), ('SOURCING', m.So), ('MICROSOFT', m.Ms)) AS f (FunctionCode, Val);
END

------------------------------------------------------------------------------------------------
-- Segregation of duties
------------------------------------------------------------------------------------------------
MERGE sec.SodSetting AS t
USING (VALUES
 ('SOD1', N'A requester cannot approve their own request.', 1, 0),
 ('SOD2', N'The approver of a request cannot configure it.', 1, 0),
 ('SOD3', N'The configurer cannot be the only verifier.', 1, 0),
 ('SOD4', N'Budget approval, risk acceptance and purchase authority are separate decision records.', 1, 0),
 ('SOD5', N'Workbench administrators cannot approve business decisions or change decision records.', 1, 0),
 ('SOD6', N'RACI and template changes need two approvers.', 1, 0),
 ('SOD7', N'Delegated approvers act only within active delegations and envelopes.', 1, 0)
) AS s (RuleCode, Description, IsEnforced, AllowSamePersonForTwoDecisionTypes)
ON t.RuleCode = s.RuleCode
WHEN NOT MATCHED THEN INSERT (RuleCode, Description, IsEnforced, AllowSamePersonForTwoDecisionTypes) VALUES (s.RuleCode, s.Description, s.IsEnforced, s.AllowSamePersonForTwoDecisionTypes)
WHEN MATCHED THEN UPDATE SET Description = s.Description;

------------------------------------------------------------------------------------------------
-- SLAs and escalation ladders (section 4.5; calibrate during the six-week validation)
------------------------------------------------------------------------------------------------
MERGE wf.SlaPolicy AS t
USING (VALUES
 ('NONE',     N'No SLA', 0, 'BD', 'BUSINESS'),
 ('SAME_DAY', N'Same business day', 1, 'BD', 'BUSINESS'),
 ('BD1',  N'1 business day', 1, 'BD', 'BUSINESS'),
 ('BD2',  N'2 business days', 2, 'BD', 'BUSINESS'),
 ('BD3',  N'3 business days', 3, 'BD', 'BUSINESS'),
 ('BD5',  N'5 business days', 5, 'BD', 'BUSINESS'),
 ('BD10', N'10 business days', 10, 'BD', 'BUSINESS'),
 ('BD15', N'15 business days', 15, 'BD', 'BUSINESS'),
 ('EVT_CRITICAL', N'Critical event: acknowledge within 1 hour, around the clock', 1, 'H', 'CLOCK'),
 ('EVT_HIGH',     N'High event: acknowledge within 4 hours', 4, 'H', 'CLOCK'),
 ('EVT_NORMAL',   N'Normal event: acknowledge within 1 business day', 1, 'BD', 'BUSINESS')
) AS s (SlaPolicyCode, Name, Duration, Unit, Calendar)
ON t.SlaPolicyCode = s.SlaPolicyCode
WHEN NOT MATCHED THEN INSERT (SlaPolicyCode, Name, Duration, Unit, Calendar) VALUES (s.SlaPolicyCode, s.Name, s.Duration, s.Unit, s.Calendar)
WHEN MATCHED THEN UPDATE SET Name = s.Name, Duration = s.Duration, Unit = s.Unit, Calendar = s.Calendar;

DELETE FROM wf.EscalationStep;
INSERT wf.EscalationStep (LadderCode, Level, RoleCode, ScopeKind) VALUES
 ('ROUTINE',    1, 'LOB_BUSINESS_OWNER', 'REQUEST_FUNDING_OWNER'),
 ('ROUTINE',    2, 'PLATFORM_OWNER',     'TENANT'),
 ('BUSINESS',   1, 'PLATFORM_OWNER',     'TENANT'),
 ('CENTRAL',    1, 'PLATFORM_OWNER',     'TENANT'),
 ('SPECIALIST', 1, 'PLATFORM_OWNER',     'TENANT'),
 ('OPERATIONS', 1, 'PLATFORM_OWNER',     'TENANT');

------------------------------------------------------------------------------------------------
-- Notification catalog (section 8.3)
------------------------------------------------------------------------------------------------
MERGE ntf.NotificationRule AS t
USING (VALUES
 ('N01', N'Request submitted', N'Requester (confirmation); sponsor; Intake queue', 'EMAIL,CHANNEL', N'Immediate', 1),
 ('N02', N'Request returned or remediation reminder', N'Requester; sponsor', 'EMAIL,ACTIVITY', N'Immediate; reminder day 5; auto-close day 10', 1),
 ('N03', N'Classification task opened', N'Architecture queue; Product Owner', 'CHANNEL,EMAIL', N'Reminders at 50 and 80 percent of SLA', 1),
 ('N04', N'Routine approval needed', N'LOB delegated approvers', 'EMAIL,ACTIVITY', N'Escalate to LOB business owner, then Platform Owner', 1),
 ('N05', N'Specialist review needed', N'Queues of the triggered disciplines', 'CHANNEL,EMAIL', N'Escalate to the discipline lead', 1),
 ('N06', N'Decision recorded', N'Requester; sponsor; LOB business owner (digest); Platform Operations when fulfillment follows', 'EMAIL', N'Immediate', 1),
 ('N07', N'Fulfillment task created', N'Platform Operations queue', 'CHANNEL,ITSM', N'ITSM assignment rules apply', 1),
 ('N08', N'Verification failed or evidence missing', N'Configurer; Platform Operations lead', 'EMAIL,ACTIVITY', N'Immediate', 1),
 ('N09', N'Production acceptance due', N'Service owner; FinOps; Operations', 'EMAIL', N'Reminders at 50 and 80 percent of SLA', 1),
 ('N10', N'Drift finding', N'Platform Operations (action); Platform Owner and workload owner (informed)', 'CHANNEL,EMAIL', N'Severity-based response target', 1),
 ('N11', N'Unregistered agent or environment discovered', N'Owner from inventory; Platform Operations', 'EMAIL', N'Claim and classify within 10 business days; escalate to the owner''s manager, then Platform Owner', 1),
 ('N12', N'Consumption event', N'Operations triage; shared-service owner when a critical service is affected; business owner (informed)', 'CHANNEL,EMAIL,ITSM', N'Acknowledgement targets from A5', 1),
 ('N13', N'Exception expiring or expired', N'Exception owner; decision owner', 'EMAIL', N'30, 7 and 1 days before expiry; on expiry', 1),
 ('N14', N'Recertification due or overdue', N'Business owner; Product Owner', 'EMAIL,ACTIVITY', N'Escalate to Platform Owner when overdue', 1),
 ('N15', N'Assignment without funding or attribution', N'LOB delegated approvers; Platform Operations', 'EMAIL', N'Daily until resolved', 1),
 ('N16', N'Envelope threshold reached (Phase 2)', N'LOB business owner; FinOps', 'EMAIL', N'80 and 100 percent of the envelope', 1),
 ('N17', N'Daily digest', N'Everyone with informed items', 'EMAIL', N'Daily', 0),
 ('N18', N'Commercial renewal or expiry approaching', N'Commercial Authority; FinOps; Sourcing', 'EMAIL', N'90, 60 and 30 days before', 1),
 ('N19', N'Integration failure or stale data', N'Workbench administrators; Platform Operations', 'CHANNEL,EMAIL', N'After two consecutive failures', 1),
 ('N20', N'Role vacancy or leaver holding a role', N'Platform Owner; LOB business owner', 'EMAIL', N'Immediate', 1),
 ('N21', N'Inventory sync overdue', N'Platform Operations', 'CHANNEL', N'When the last sync is older than 7 days', 1),
 ('N22', N'Attestation due or overdue', N'Operator group', 'EMAIL,CHANNEL', N'Monthly; escalate after 5 business days', 1)
) AS s (Code, EventName, Recipients, Channels, Timing, IsAction)
ON t.Code = s.Code
WHEN NOT MATCHED THEN INSERT (Code, EventName, Recipients, Channels, Timing, IsAction) VALUES (s.Code, s.EventName, s.Recipients, s.Channels, s.Timing, s.IsAction)
WHEN MATCHED THEN UPDATE SET EventName = s.EventName, Recipients = s.Recipients, Channels = s.Channels, Timing = s.Timing, IsAction = s.IsAction;

------------------------------------------------------------------------------------------------
-- Journeys, stages and transitions (version 1)
------------------------------------------------------------------------------------------------
MERGE wf.WorkflowDefinition AS t
USING (VALUES
 ('J1', N'Use an approved Copilot service', N'Routine access to Copilot Chat or Cowork through an Entra access package for your LOB tier.', N'Any employee', N'Entra approval, usually 2 business days', 0),
 ('J2', N'Build or register an agent, environment or flow', N'A new workload: intake, classification, approval, fulfillment through ITSM, verification and, for production, acceptance.', N'Maker, developer, product owner', N'About 2 to 3 weeks to a verified build environment', 1),
 ('J3', N'Ask for something outside the approved patterns', N'An exception: specialist review by the disciplines it triggers and one accountable disposition with conditions and expiry.', N'Sponsor, product owner', N'About 1 to 2 weeks', 1),
 ('J4', N'Change capacity, a limit or an envelope', N'More allocation, a higher agent limit or a bigger envelope, approved within the envelope or as an overage.', N'Product owner, LOB business owner', N'About 1 week', 1),
 ('J5', N'Report a consumption problem', N'Threshold notice, denial, exhaustion or runaway activity: Operations triages and responds.', N'Any user, Operations', N'Acknowledged within 1 hour (critical) to 1 business day', 0),
 ('J6', N'Review what I own', N'Recertify the workloads you own: need, audience, funding and outcome.', N'Business owner, product owner', N'15 business day campaign', 0),
 ('J7', N'Retire a workload', N'Controlled retirement: decision, access removed, allocations released, commitments closed.', N'Product owner', N'About 10 business days', 1),
 ('J8', N'Prepare a commercial decision', N'P3, pack, renewal or critical shared funding: FinOps recommends, Commercial Authority approves, Sourcing executes.', N'FinOps, Commercial Authority', N'About 2 weeks', 1),
 ('J9', N'Change a Copilot seat reservation', N'Reserve Copilot seats for your LOB, add seats at any time, or give seats back at the April EA anniversary. Finance decides after Digital Workplace confirms the seats are available.', N'LOB business owner or finance partner', N'About 1 week', 1)
) AS s (JourneyCode, Name, Description, TypicalUser, Turnaround, HasStages)
ON t.JourneyCode = s.JourneyCode AND t.VersionNo = 1
WHEN NOT MATCHED THEN INSERT (JourneyCode, VersionNo, Name, Description, TypicalUser, Turnaround, IsActive)
     VALUES (s.JourneyCode, 1, s.Name, s.Description, s.TypicalUser, s.Turnaround, 1)
WHEN MATCHED THEN UPDATE SET Name = s.Name, Description = s.Description, TypicalUser = s.TypicalUser, Turnaround = s.Turnaround;

-- Stages and transitions are rewritten only while no request uses version 1 of that journey.
DECLARE @defs TABLE (JourneyCode varchar(10), DefinitionId int);
INSERT @defs SELECT d.JourneyCode, d.DefinitionId FROM wf.WorkflowDefinition d
WHERE d.VersionNo = 1 AND NOT EXISTS (SELECT 1 FROM wf.Request r WHERE r.DefinitionId = d.DefinitionId);

DELETE c FROM wf.ConsultDefinition c JOIN @defs d ON d.DefinitionId = c.DefinitionId;
DELETE t FROM wf.TransitionDefinition t JOIN @defs d ON d.DefinitionId = t.DefinitionId;
DELETE s FROM wf.StageDefinition s JOIN @defs d ON d.DefinitionId = s.DefinitionId;

DECLARE @st TABLE (JourneyCode varchar(10), StageCode varchar(20), Name nvarchar(200), Activity varchar(60), Kind varchar(20), RoleCode varchar(40),
                   DecisionType varchar(40), Sla varchar(30), Ladder varchar(30), Deleg bit, Evidence nvarchar(400), EntryRule varchar(60), ExitRule varchar(60),
                   Notify varchar(100), Guidance nvarchar(1000), SortOrder int);
INSERT @st VALUES
 -- J2 New workload
 ('J2','B2', N'Completeness and sponsorship', 'CLASSIFICATION_ROUTING','WORK','INTAKE_ANALYST',NULL,'BD2','CENTRAL',0,NULL,NULL,'CompletenessCheck','N02,N03', N'Check the sponsor and funding owner are valid, the request is not a duplicate and the service is in the catalog.', 10),
 ('J2','B7', N'Remediate and resubmit', 'BUSINESS_CASE','WORK','REQUESTER',NULL,'BD10',NULL,0,NULL,NULL,NULL,'N02', N'Fix what was returned and resubmit, or withdraw. Earlier decisions stay on the record.', 15),
 ('J2','B3', N'Classify the workload', 'CLASSIFICATION_ROUTING','WORK','ARCHITECT',NULL,'BD3','SPECIALIST',0,NULL,NULL,'ClassificationComplete','N03', N'Answer the four classification fields independently (C03), the trigger type and the downstream meters.', 20),
 ('J2','B4', N'Approved pattern check', 'CLASSIFICATION_ROUTING','SYSTEM',NULL,NULL,'SAME_DAY',NULL,0,NULL,'PatternFit',NULL,'N04,N05', N'The rules engine checks the request against approved patterns and envelope headroom.', 30),
 ('J2','B5', N'Routine approval', 'FUNDING_BUDGET','DECISION','LOB_BUSINESS_OWNER','ROUTINE_APPROVAL','BD2','ROUTINE',1,NULL,'DelegationValid',NULL,'N04,N06', N'Approve within the delegated envelope, or decline with the reason and the evidence needed.', 40),
 ('J2','B8', N'Specialist review', 'PLATFORM_FEATURE','SYSTEM',NULL,NULL,'BD5','SPECIALIST',0,NULL,'TriggeredDisciplines',NULL,'N05', N'Only the disciplines this request triggers review it.', 50),
 ('J2','B9', N'Exception disposition', 'PLATFORM_FEATURE','SYSTEM',NULL,NULL,'BD2','SPECIALIST',0,NULL,NULL,NULL,'N06,N13', N'One accountable decision per decision type, with conditions, owner, expiry and next review.', 60),
 ('J2','B10', N'Prepare controlled fulfillment', 'TECH_IMPLEMENTATION','WORK','INTAKE_ANALYST',NULL,'BD1','CENTRAL',0,NULL,NULL,NULL,'N07', N'Create the ITSM task from the control template with the exact settings to apply.', 70),
 ('J2','FUL', N'Fulfillment in ITSM', 'TECH_IMPLEMENTATION','NONE',NULL,NULL,'BD3','OPERATIONS',0,NULL,NULL,NULL,'N07', N'Platform Operations configures in the admin center through the ITSM task. A closed ticket moves the request to verification.', 80),
 ('J2','D1', N'Provision and verify', 'TECH_IMPLEMENTATION','VERIFICATION','PLATFORM_OPERATOR',NULL,'BD3','OPERATIONS',0,'READBACK_OR_ATTESTATION',NULL,'VerificationComplete','N08', N'A second operator verifies by read-back or attestation, including the explicit deny case.', 90),
 ('J2','D2', N'Build and test evidence', 'BUSINESS_CASE','WORK','PRODUCT_OWNER',NULL,'NONE',NULL,0,'TEST_RESULT',NULL,NULL,'N09', N'Upload charging, attribution, exhaustion, overage, recovery and negative test results, including enforcement lag (C08, C11).', 95),
 ('J2','D5', N'Production acceptance', 'BUSINESS_CASE','DECISION','LOB_BUSINESS_OWNER','PRODUCTION_ACCEPTANCE','BD5','BUSINESS',0,'TEST_RESULT','ProductionGateRequired',NULL,'N09,N06', N'Funding, owner, support route, continuity treatment and test evidence are in place.', 100),
 -- J3 Exception
 ('J3','B2', N'Completeness and sponsorship', 'CLASSIFICATION_ROUTING','WORK','INTAKE_ANALYST',NULL,'BD2','CENTRAL',0,NULL,NULL,'CompletenessCheck','N02', N'Check the sponsor, the funding owner and what is being asked outside the patterns.', 10),
 ('J3','B7', N'Remediate and resubmit', 'BUSINESS_CASE','WORK','REQUESTER',NULL,'BD10',NULL,0,NULL,NULL,NULL,'N02', N'Fix what was returned and resubmit, or withdraw.', 15),
 ('J3','B8', N'Specialist review', 'PLATFORM_FEATURE','SYSTEM',NULL,NULL,'BD5','SPECIALIST',0,NULL,'TriggeredDisciplines',NULL,'N05', N'Only the disciplines this exception triggers review it.', 50),
 ('J3','B9', N'Exception disposition', 'PLATFORM_FEATURE','SYSTEM',NULL,NULL,'BD2','SPECIALIST',0,NULL,NULL,NULL,'N06,N13', N'One accountable decision per decision type; approved exceptions enter the register with compensating controls and expiry.', 60),
 -- J4 Capacity or limit change
 ('J4','B2', N'Completeness', 'FUNDING_BUDGET','WORK','INTAKE_ANALYST',NULL,'BD2','CENTRAL',0,NULL,NULL,'CompletenessCheck','N02', N'Check the workload, the current and requested values and the funding owner.', 10),
 ('J4','B7', N'Remediate and resubmit', 'BUSINESS_CASE','WORK','REQUESTER',NULL,'BD10',NULL,0,NULL,NULL,NULL,'N02', N'Fix what was returned and resubmit, or withdraw.', 15),
 ('J4','B4', N'Envelope and limit check', 'FUNDING_BUDGET','SYSTEM',NULL,NULL,'SAME_DAY',NULL,0,NULL,'PatternFit',NULL,'N04,N05', N'Within the envelope and the environment class default goes to routine approval; anything above goes to FinOps and the business owner.', 30),
 ('J4','B5', N'Routine approval', 'FUNDING_BUDGET','DECISION','LOB_BUSINESS_OWNER','BUDGET_APPROVAL','BD2','ROUTINE',1,NULL,'DelegationValid',NULL,'N04,N06', N'Approve within the envelope.', 40),
 ('J4','B8', N'Specialist review', 'OVERAGE','SYSTEM',NULL,NULL,'BD5','SPECIALIST',0,NULL,'TriggeredDisciplines',NULL,'N05', N'FinOps reviews the forecast; Operations reviews monitoring.', 50),
 ('J4','B9', N'Overage disposition', 'OVERAGE','SYSTEM',NULL,NULL,'BD2','SPECIALIST',0,NULL,NULL,NULL,'N06', N'The business owner authorizes the overage; other decision types are recorded separately.', 60),
 ('J4','B10', N'Prepare controlled fulfillment', 'TECH_IMPLEMENTATION','WORK','INTAKE_ANALYST',NULL,'BD1','CENTRAL',0,NULL,NULL,NULL,'N07', N'Create the ITSM task with the new allocation or limit.', 70),
 ('J4','FUL', N'Fulfillment in ITSM', 'TECH_IMPLEMENTATION','NONE',NULL,NULL,'BD3','OPERATIONS',0,NULL,NULL,NULL,'N07', N'Platform Operations changes the setting in PPAC through the ITSM task.', 80),
 ('J4','D1', N'Verify the change', 'TECH_IMPLEMENTATION','VERIFICATION','PLATFORM_OPERATOR',NULL,'BD3','OPERATIONS',0,'READBACK_OR_ATTESTATION',NULL,'VerificationComplete','N08', N'Read back the allocation, tenant-pool rule and limit; allow up to 15 minutes for credit availability to refresh.', 90),
 -- J7 Retirement
 ('J7','B2', N'Completeness', 'LIFECYCLE_RETIREMENT','WORK','INTAKE_ANALYST',NULL,'BD2','CENTRAL',0,NULL,NULL,'CompletenessCheck','N02', N'Check the workload, the reason and the retirement plan.', 10),
 ('J7','B7', N'Remediate and resubmit', 'BUSINESS_CASE','WORK','REQUESTER',NULL,'BD10',NULL,0,NULL,NULL,NULL,'N02', N'Fix what was returned and resubmit, or withdraw.', 15),
 ('J7','I4', N'Retirement decision', 'LIFECYCLE_RETIREMENT','DECISION','LOB_BUSINESS_OWNER','RETIREMENT','BD5','BUSINESS',0,NULL,NULL,NULL,'N06', N'The business owner decides; Finance and Sourcing are consulted on residual commitments.', 20),
 ('J7','B10', N'Prepare controlled retirement', 'TECH_IMPLEMENTATION','WORK','INTAKE_ANALYST',NULL,'BD1','CENTRAL',0,NULL,NULL,NULL,'N07', N'Create the ITSM task: disable, remove access, release allocations.', 70),
 ('J7','FUL', N'Retirement in ITSM', 'TECH_IMPLEMENTATION','NONE',NULL,NULL,'BD5','OPERATIONS',0,NULL,NULL,NULL,'N07', N'Platform Operations disables the workload through the ITSM task.', 80),
 ('J7','D1', N'Verify retirement', 'TECH_IMPLEMENTATION','VERIFICATION','PLATFORM_OPERATOR',NULL,'BD3','OPERATIONS',0,'READBACK_OR_ATTESTATION',NULL,'VerificationComplete','N08', N'Access removed and allocations released, read back or attested.', 90),
 ('J7','I5', N'Close residual commitments', 'LIFECYCLE_RETIREMENT','WORK','FINOPS_ANALYST',NULL,'BD5','CENTRAL',0,NULL,NULL,NULL,'N06', N'Close envelope commitments and record lessons against the template. Records are retained.', 95),
 -- J8 Commercial decision
 ('J8','F1', N'FinOps recommendation', 'P3_EVALUATION','WORK','FINOPS_ANALYST',NULL,'BD5','CENTRAL',0,NULL,NULL,NULL,'N18', N'Coverage, forecast and scenarios; the recommendation states units (credits, P3 commit units, currency) separately.', 10),
 ('J8','F4', N'Commercial authority decision', 'P3_PURCHASE','DECISION','COMMERCIAL_APPROVER','PURCHASE_APPROVAL','BD5','CENTRAL',0,NULL,NULL,NULL,'N06', N'Approve, decline or defer. The Workbench never purchases or renews anything itself (C21).', 20),
 ('J8','F5', N'Sourcing executes', 'P3_PURCHASE','WORK','SOURCING_AGENT',NULL,'BD10','CENTRAL',0,'CONTRACT',NULL,NULL,'N06', N'Execute the approved purchase and upload the order or contract as evidence.', 30),
 -- J9 Copilot seat reservation (seats, never credits): increases any time, true-downs only at the EA anniversary
 ('J9','B2', N'Completeness', 'FUNDING_BUDGET','WORK','INTAKE_ANALYST',NULL,'BD2','CENTRAL',0,NULL,NULL,'CompletenessCheck','N02', N'Check the LOB, the requested seats and who needs them.', 10),
 ('J9','B7', N'Remediate and resubmit', 'BUSINESS_CASE','WORK','REQUESTER',NULL,'BD10',NULL,0,NULL,NULL,NULL,'N02', N'Fix what was returned and resubmit, or withdraw.', 15),
 ('J9','S4', N'Reservation decision', 'FUNDING_BUDGET','DECISION','FINOPS_LEAD','BUDGET_APPROVAL','BD5','CENTRAL',0,NULL,NULL,'SeatReservationRules','N04,N06', N'Finance decides after Digital Workplace confirms unallocated seats, pending demand and the operational buffer. A decrease can be approved only in the window before the EA anniversary; until then hold it.', 20);

INSERT wf.StageDefinition (DefinitionId, StageCode, Name, RaciActivityCode, TaskKind, TaskRoleCode, DecisionType, SlaPolicyCode, LadderCode,
                           AllowsDelegation, RequiredEvidence, EntryRule, ExitRule, NotifyCodes, Guidance, SortOrder)
SELECT d.DefinitionId, s.StageCode, s.Name, s.Activity, s.Kind, s.RoleCode, s.DecisionType, s.Sla, s.Ladder, s.Deleg, s.Evidence, s.EntryRule, s.ExitRule, s.Notify, s.Guidance, s.SortOrder
FROM @st s JOIN @defs d ON d.JourneyCode = s.JourneyCode;

DECLARE @tr TABLE (JourneyCode varchar(10), FromStage varchar(20), Outcome varchar(30), ToStage varchar(20), ToState varchar(30), Guard varchar(60));
INSERT @tr VALUES
 ('J2','B2','COMPLETE','B3','CLASSIFICATION',NULL), ('J2','B2','RETURN','B7','RETURNED',NULL),
 ('J2','B7','RESUBMIT','B2','SUBMITTED',NULL),      ('J2','B7','WITHDRAW','END','CANCELLED',NULL), ('J2','B7','AUTO_CLOSE','END','CLOSED',NULL),
 ('J2','B3','CLASSIFIED','B4','PATTERN_CHECK',NULL),('J2','B3','RETURN','B7','RETURNED',NULL),
 ('J2','B4','FITS','B5','ROUTINE_APPROVAL',NULL),   ('J2','B4','NO_PATTERN','B8','SPECIALIST_REVIEW',NULL),
 ('J2','B5','APPROVED','B10','APPROVED',NULL),      ('J2','B5','DECLINED','B7','DECLINED',NULL),
 ('J2','B8','REVIEWED','B9','SPECIALIST_REVIEW',NULL),
 ('J2','B9','APPROVED','B10','APPROVED',NULL),      ('J2','B9','APPROVED_WITH_CONDITIONS','B10','APPROVED_WITH_CONDITIONS',NULL),
 ('J2','B9','CONSTRAINED_PILOT','B10','CONSTRAINED_PILOT',NULL), ('J2','B9','HOLD','B9','ON_HOLD',NULL), ('J2','B9','DECLINED','B7','DECLINED',NULL),
 ('J2','B10','READY','FUL','FULFILLMENT',NULL),
 ('J2','FUL','TICKET_CLOSED','D1','VERIFICATION',NULL), ('J2','FUL','TICKET_CANCELLED','B10','APPROVED',NULL),
 ('J2','D1','PASS','END','COMPLETED',NULL),         ('J2','D1','PASS_GATE','D2','ACCEPTANCE','ProductionGateRequired'), ('J2','D1','FAIL','B10','FULFILLMENT',NULL),
 ('J2','D2','READY','D5','ACCEPTANCE',NULL),
 ('J2','D5','ACCEPTED','END','COMPLETED',NULL),     ('J2','D5','REJECTED','D2','ACCEPTANCE',NULL),

 ('J3','B2','COMPLETE','B8','SPECIALIST_REVIEW',NULL), ('J3','B2','RETURN','B7','RETURNED',NULL),
 ('J3','B7','RESUBMIT','B2','SUBMITTED',NULL),      ('J3','B7','WITHDRAW','END','CANCELLED',NULL), ('J3','B7','AUTO_CLOSE','END','CLOSED',NULL),
 ('J3','B8','REVIEWED','B9','SPECIALIST_REVIEW',NULL),
 ('J3','B9','APPROVED','END','COMPLETED',NULL),     ('J3','B9','APPROVED_WITH_CONDITIONS','END','COMPLETED',NULL),
 ('J3','B9','CONSTRAINED_PILOT','END','COMPLETED',NULL), ('J3','B9','HOLD','B9','ON_HOLD',NULL), ('J3','B9','DECLINED','B7','DECLINED',NULL),

 ('J4','B2','COMPLETE','B4','PATTERN_CHECK',NULL),  ('J4','B2','RETURN','B7','RETURNED',NULL),
 ('J4','B7','RESUBMIT','B2','SUBMITTED',NULL),      ('J4','B7','WITHDRAW','END','CANCELLED',NULL), ('J4','B7','AUTO_CLOSE','END','CLOSED',NULL),
 ('J4','B4','FITS','B5','ROUTINE_APPROVAL',NULL),   ('J4','B4','NO_PATTERN','B8','SPECIALIST_REVIEW',NULL),
 ('J4','B5','APPROVED','B10','APPROVED',NULL),      ('J4','B5','DECLINED','B7','DECLINED',NULL),
 ('J4','B8','REVIEWED','B9','SPECIALIST_REVIEW',NULL),
 ('J4','B9','APPROVED','B10','APPROVED',NULL),      ('J4','B9','APPROVED_WITH_CONDITIONS','B10','APPROVED_WITH_CONDITIONS',NULL),
 ('J4','B9','CONSTRAINED_PILOT','B10','CONSTRAINED_PILOT',NULL), ('J4','B9','HOLD','B9','ON_HOLD',NULL), ('J4','B9','DECLINED','B7','DECLINED',NULL),
 ('J4','B10','READY','FUL','FULFILLMENT',NULL),
 ('J4','FUL','TICKET_CLOSED','D1','VERIFICATION',NULL), ('J4','FUL','TICKET_CANCELLED','B10','APPROVED',NULL),
 ('J4','D1','PASS','END','COMPLETED',NULL),         ('J4','D1','FAIL','B10','FULFILLMENT',NULL),

 ('J7','B2','COMPLETE','I4','ROUTINE_APPROVAL',NULL), ('J7','B2','RETURN','B7','RETURNED',NULL),
 ('J7','B7','RESUBMIT','B2','SUBMITTED',NULL),      ('J7','B7','WITHDRAW','END','CANCELLED',NULL), ('J7','B7','AUTO_CLOSE','END','CLOSED',NULL),
 ('J7','I4','APPROVED','B10','APPROVED',NULL),      ('J7','I4','DECLINED','END','DECLINED',NULL),
 ('J7','B10','READY','FUL','FULFILLMENT',NULL),
 ('J7','FUL','TICKET_CLOSED','D1','VERIFICATION',NULL), ('J7','FUL','TICKET_CANCELLED','B10','APPROVED',NULL),
 ('J7','D1','PASS','I5','VERIFICATION',NULL),       ('J7','D1','FAIL','B10','FULFILLMENT',NULL),
 ('J7','I5','CLOSED','END','COMPLETED',NULL),

 ('J8','F1','READY','F4','ROUTINE_APPROVAL',NULL),
 ('J8','F4','APPROVED','F5','APPROVED',NULL),       ('J8','F4','DECLINED','END','DECLINED',NULL), ('J8','F4','HOLD','F1','ON_HOLD',NULL),
 ('J8','F5','EXECUTED','END','COMPLETED',NULL),

 ('J9','B2','COMPLETE','S4','ROUTINE_APPROVAL',NULL), ('J9','B2','RETURN','B7','RETURNED',NULL),
 ('J9','B7','RESUBMIT','B2','SUBMITTED',NULL),      ('J9','B7','WITHDRAW','END','CANCELLED',NULL), ('J9','B7','AUTO_CLOSE','END','CLOSED',NULL),
 ('J9','S4','APPROVED','END','COMPLETED',NULL),     ('J9','S4','DECLINED','END','DECLINED',NULL), ('J9','S4','HOLD','S4','ON_HOLD',NULL);

INSERT wf.TransitionDefinition (DefinitionId, FromStage, Outcome, ToStage, ToState, GuardRule)
SELECT d.DefinitionId, t.FromStage, t.Outcome, t.ToStage, t.ToState, t.Guard
FROM @tr t JOIN @defs d ON d.JourneyCode = t.JourneyCode;

-- Consults the stage opens (C cells the stage needs an answer from); other C and I parties get the digest.
DECLARE @co TABLE (JourneyCode varchar(10), StageCode varchar(20), FunctionCode varchar(30), IsBlocking bit);
INSERT @co VALUES
 ('J2','D5','FINANCE',1), ('J2','D5','ENG_OPS',1),
 ('J7','I4','FINANCE',0), ('J7','I4','SOURCING',0),
 ('J8','F4','BUSINESS_OWNER',0), ('J8','F4','SECURITY_RISK',0),
 ('J9','S4','PLATFORM_OWNER',1), ('J9','S4','BUSINESS_OWNER',0);
INSERT wf.ConsultDefinition (DefinitionId, StageCode, FunctionCode, IsBlocking)
SELECT d.DefinitionId, c.StageCode, c.FunctionCode, c.IsBlocking
FROM @co c JOIN @defs d ON d.JourneyCode = c.JourneyCode;

------------------------------------------------------------------------------------------------
-- Environment classes (institutional default agent limit, G8)
------------------------------------------------------------------------------------------------
MERGE core.EnvironmentClass AS t
USING (VALUES
 ('DEVELOPER', N'Developer', N'Personal and team build environments; sponsor-funded tier with expiry.', 2000, 80, 0, 0, 5000),
 ('TEST',      N'Test', N'Shared test environments for negative and exhaustion tests.', 5000, 80, 0, 0, 10000),
 ('PRODUCTION',N'Production', N'Accepted workloads (D5).', 20000, 80, 0, 0, 50000),
 ('SHARED',    N'Enterprise shared', N'Critical shared services; continuity funding (F9).', 50000, 70, 1, 1, 100000)
) AS s (EnvironmentClassCode, Name, Purpose, DefaultAgentLimitCredits, NotificationThresholdPct, TenantPoolAllowed, PaygAllowed, DefaultAllocationCredits)
ON t.EnvironmentClassCode = s.EnvironmentClassCode
WHEN NOT MATCHED THEN INSERT (EnvironmentClassCode, Name, Purpose, DefaultAgentLimitCredits, NotificationThresholdPct, TenantPoolAllowed, PaygAllowed, DefaultAllocationCredits)
     VALUES (s.EnvironmentClassCode, s.Name, s.Purpose, s.DefaultAgentLimitCredits, s.NotificationThresholdPct, s.TenantPoolAllowed, s.PaygAllowed, s.DefaultAllocationCredits)
WHEN MATCHED THEN UPDATE SET Name = s.Name, Purpose = s.Purpose;

COMMIT TRANSACTION;
GO
