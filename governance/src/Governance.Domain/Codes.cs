namespace Governance.Domain;

// Codes stored in the database. They match the CHECK constraints and the reference seed.

public static class Journeys
{
    public const string RoutineAccess = "J1";
    public const string NewWorkload = "J2";
    public const string Exception = "J3";
    public const string CapacityChange = "J4";
    public const string ConsumptionEvent = "J5";
    public const string Recertification = "J6";
    public const string Retirement = "J7";
    public const string Commercial = "J8";
    public const string SeatReservation = "J9";

    /// <summary>Journeys that run as a request through the workflow engine.</summary>
    public static readonly IReadOnlySet<string> WithStages = new HashSet<string> { NewWorkload, Exception, CapacityChange, Retirement, Commercial, SeatReservation };
}

public static class Stages
{
    public const string End = "END";
    public const string Completeness = "B2";
    public const string Remediate = "B7";
    public const string Classify = "B3";
    public const string PatternCheck = "B4";
    public const string RoutineApproval = "B5";
    public const string SpecialistReview = "B8";
    public const string Disposition = "B9";
    public const string PrepareFulfillment = "B10";
    public const string Fulfillment = "FUL";
    public const string Verify = "D1";
    public const string BuildAndTest = "D2";
    public const string ProductionAcceptance = "D5";
    public const string RetirementDecision = "I4";
    public const string CloseCommitments = "I5";
    public const string FinOpsRecommendation = "F1";
    public const string CommercialDecision = "F4";
    public const string SourcingExecutes = "F5";
    public const string SeatDecision = "S4";
}

public static class RequestStates
{
    public const string Draft = "DRAFT";
    public const string Submitted = "SUBMITTED";
    public const string Returned = "RETURNED";
    public const string Classification = "CLASSIFICATION";
    public const string PatternCheck = "PATTERN_CHECK";
    public const string RoutineApproval = "ROUTINE_APPROVAL";
    public const string SpecialistReview = "SPECIALIST_REVIEW";
    public const string Approved = "APPROVED";
    public const string ApprovedWithConditions = "APPROVED_WITH_CONDITIONS";
    public const string ConstrainedPilot = "CONSTRAINED_PILOT";
    public const string OnHold = "ON_HOLD";
    public const string Declined = "DECLINED";
    public const string Fulfillment = "FULFILLMENT";
    public const string Verification = "VERIFICATION";
    public const string Acceptance = "ACCEPTANCE";
    public const string Completed = "COMPLETED";
    public const string Cancelled = "CANCELLED";
    public const string Closed = "CLOSED";

    public static readonly IReadOnlySet<string> Final = new HashSet<string> { Completed, Cancelled, Closed };

    public static string Label(string state) => state switch
    {
        Draft => "Draft",
        Submitted => "Submitted",
        Returned => "Returned to requester",
        Classification => "Classification",
        PatternCheck => "Pattern check",
        RoutineApproval => "Waiting for approval",
        SpecialistReview => "Specialist review",
        Approved => "Approved",
        ApprovedWithConditions => "Approved with conditions",
        ConstrainedPilot => "Constrained pilot",
        OnHold => "On hold",
        Declined => "Declined",
        Fulfillment => "Fulfillment",
        Verification => "Verification",
        Acceptance => "Production acceptance",
        Completed => "Completed",
        Cancelled => "Cancelled",
        Closed => "Closed",
        _ => state,
    };
}

public static class TaskKinds
{
    public const string Decision = "DECISION";
    public const string Work = "WORK";
    public const string Verification = "VERIFICATION";
    public const string System = "SYSTEM";
    public const string None = "NONE";
}

public static class TaskTypes
{
    public const string Decision = "DECISION";
    public const string Work = "WORK";
    public const string Consult = "CONSULT";
    public const string Verification = "VERIFICATION";
    public const string Attestation = "ATTESTATION";
    public const string Recertification = "RECERTIFICATION";
    public const string Finding = "FINDING";
    public const string Event = "EVENT";
    public const string Claim = "CLAIM";
    public const string AcceptRole = "ACCEPT_ROLE";
    public const string Activation = "ACTIVATION";
}

public static class TaskStatuses
{
    public const string Open = "OPEN";
    public const string Done = "DONE";
    public const string Cancelled = "CANCELLED";
}

public static class DecisionTypes
{
    public const string RoutineApproval = "ROUTINE_APPROVAL";
    public const string ExceptionDisposition = "EXCEPTION_DISPOSITION";
    public const string RiskAcceptance = "RISK_ACCEPTANCE";
    public const string BudgetApproval = "BUDGET_APPROVAL";
    public const string OverageAuthorization = "OVERAGE_AUTHORIZATION";
    public const string PurchaseApproval = "PURCHASE_APPROVAL";
    public const string RenewalApproval = "RENEWAL_APPROVAL";
    public const string ProductionAcceptance = "PRODUCTION_ACCEPTANCE";
    public const string ContinuityAcceptance = "CONTINUITY_ACCEPTANCE";
    public const string Recertification = "RECERTIFICATION";
    public const string Retirement = "RETIREMENT";
    public const string PreviewAdoption = "PREVIEW_ADOPTION";
    public const string PlatformFeature = "PLATFORM_FEATURE_APPROVAL";
    public const string EnvelopeApproval = "ENVELOPE_APPROVAL";
    public const string RaciActivation = "RACI_ACTIVATION";
    public const string TemplateActivation = "TEMPLATE_ACTIVATION";
    public const string AccessAssignment = "ACCESS_ASSIGNMENT";   // imported from Entra (J1)

    public static string Label(string type) => type switch
    {
        RoutineApproval => "Routine approval",
        ExceptionDisposition => "Exception disposition",
        RiskAcceptance => "Risk acceptance",
        BudgetApproval => "Budget approval",
        OverageAuthorization => "Overage authorization",
        PurchaseApproval => "Purchase approval",
        RenewalApproval => "Renewal approval",
        ProductionAcceptance => "Production acceptance",
        ContinuityAcceptance => "Continuity acceptance",
        Recertification => "Recertification",
        Retirement => "Retirement",
        PreviewAdoption => "Preview adoption",
        PlatformFeature => "Platform feature approval",
        EnvelopeApproval => "Envelope approval",
        RaciActivation => "RACI version activation",
        TemplateActivation => "Control template activation",
        AccessAssignment => "Access assignment (Entra)",
        _ => type,
    };
}

public static class Outcomes
{
    public const string Approved = "APPROVED";
    public const string ApprovedWithConditions = "APPROVED_WITH_CONDITIONS";
    public const string ConstrainedPilot = "CONSTRAINED_PILOT";
    public const string Extend = "EXTEND";
    public const string Hold = "HOLD";
    public const string Declined = "DECLINED";

    public static readonly IReadOnlySet<string> Positive = new HashSet<string> { Approved, ApprovedWithConditions, ConstrainedPilot, Extend };

    public static string Label(string outcome) => outcome switch
    {
        Approved => "Approved",
        ApprovedWithConditions => "Approved with conditions",
        ConstrainedPilot => "Constrained pilot",
        Extend => "Extended",
        Hold => "On hold",
        Declined => "Declined",
        _ => outcome,
    };
}

/// <summary>Outcomes a work or system stage reports to the engine (transition keys).</summary>
public static class StageOutcomes
{
    public const string Complete = "COMPLETE";
    public const string Return = "RETURN";
    public const string Resubmit = "RESUBMIT";
    public const string Withdraw = "WITHDRAW";
    public const string AutoClose = "AUTO_CLOSE";
    public const string Classified = "CLASSIFIED";
    public const string Fits = "FITS";
    public const string NoPattern = "NO_PATTERN";
    public const string Reviewed = "REVIEWED";
    public const string Ready = "READY";
    public const string TicketClosed = "TICKET_CLOSED";
    public const string TicketCancelled = "TICKET_CANCELLED";
    public const string Pass = "PASS";
    public const string PassGate = "PASS_GATE";
    public const string Fail = "FAIL";
    public const string Accepted = "ACCEPTED";
    public const string Rejected = "REJECTED";
    public const string Closed = "CLOSED";
    public const string Executed = "EXECUTED";
}

public static class Functions
{
    public const string BusinessOwner = "BUSINESS_OWNER";
    public const string PlatformOwner = "PLATFORM_OWNER";
    public const string Finance = "FINANCE";
    public const string Architecture = "ARCHITECTURE";
    public const string EngOps = "ENG_OPS";
    public const string SecurityRisk = "SECURITY_RISK";
    public const string Commercial = "COMMERCIAL";
    public const string Sourcing = "SOURCING";
    public const string Microsoft = "MICROSOFT";
}

public static class Roles
{
    public const string LobBusinessOwner = "LOB_BUSINESS_OWNER";
    public const string SharedServiceOwner = "SHARED_SERVICE_OWNER";
    public const string DelegatedApprover = "DELEGATED_APPROVER";
    public const string WorkloadSponsor = "WORKLOAD_SPONSOR";
    public const string ProductOwner = "PRODUCT_OWNER";
    public const string LobFinancePartner = "LOB_FINANCE_PARTNER";
    public const string LobChampion = "LOB_CHAMPION";
    public const string PlatformOwner = "PLATFORM_OWNER";
    public const string IntakeAnalyst = "INTAKE_ANALYST";
    public const string PlatformOperator = "PLATFORM_OPERATOR";
    public const string Architect = "ARCHITECT";
    public const string RiskReviewer = "RISK_REVIEWER";
    public const string FinOpsLead = "FINOPS_LEAD";
    public const string FinOpsAnalyst = "FINOPS_ANALYST";
    public const string CommercialApprover = "COMMERCIAL_APPROVER";
    public const string SourcingAgent = "SOURCING_AGENT";
    public const string LicensingOwner = "LICENSING_OWNER";
    public const string ReportingTeam = "REPORTING_TEAM";
    public const string Steering = "STEERING";
    public const string WorkbenchAdmin = "WORKBENCH_ADMIN";
    public const string Auditor = "AUDITOR";

    /// <summary>Pseudo-role: the request's requester (remediation tasks).</summary>
    public const string Requester = "REQUESTER";

    /// <summary>Roles that act for a funding owner's business owner (the A of BUSINESS_OWNER).</summary>
    public static readonly IReadOnlySet<string> BusinessOwnerRoles = new HashSet<string> { LobBusinessOwner, SharedServiceOwner };
}

/// <summary>Entra app roles: coarse page access. Scoped roles live in SQL.</summary>
public static class AppRoles
{
    public const string User = "Workbench.User";
    public const string Central = "Workbench.Central";
    public const string Operator = "Workbench.Operator";
    public const string Admin = "Workbench.Admin";
    public const string Auditor = "Workbench.Auditor";
}

public static class ScopeTypes
{
    public const string Tenant = "TENANT";
    public const string FundingOwner = "FUNDING_OWNER";
}

public static class WorkloadStates
{
    public const string Discovered = "DISCOVERED";
    public const string Registered = "REGISTERED";
    public const string Local = "LOCAL";
    public const string Development = "DEVELOPMENT";
    public const string Test = "TEST";
    public const string Production = "PRODUCTION";
    public const string Suspended = "SUSPENDED";
    public const string Retiring = "RETIRING";
    public const string Retired = "RETIRED";
}

public static class LifecycleStages
{
    public const string Local = "LOCAL";
    public const string Development = "DEVELOPMENT";
    public const string Test = "TEST";
    public const string Production = "PRODUCTION";
    public static readonly IReadOnlyList<string> All = new[] { Local, Development, Test, Production };
}

public static class SolutionClasses
{
    public const string Productivity = "PRODUCTIVITY_SOLUTION";
    public const string BusinessApplication = "BUSINESS_APPLICATION";
    public static readonly IReadOnlyList<string> All = new[] { Productivity, BusinessApplication };
}

public static class Criticalities
{
    public static readonly IReadOnlyList<string> All = new[] { "LOW", "MEDIUM", "HIGH", "CRITICAL" };
}

public static class TriggerTypes
{
    public const string UserInitiated = "USER_INITIATED";
    public const string Scheduled = "SCHEDULED";
    public const string Autonomous = "AUTONOMOUS";
    public const string CustomerTriggered = "CUSTOMER_TRIGGERED";
    public static readonly IReadOnlyList<string> All = new[] { UserInitiated, Scheduled, Autonomous, CustomerTriggered };
}

public static class DataClasses
{
    public const string Public = "PUBLIC";
    public const string Internal = "INTERNAL";
    public const string Confidential = "CONFIDENTIAL";
    public const string Restricted = "RESTRICTED";
    public static readonly IReadOnlyList<string> Ordered = new[] { Public, Internal, Confidential, Restricted };
    public static int Rank(string? dataClass) => dataClass is null ? -1 : Array.IndexOf((string[])Ordered, dataClass);
}

public static class Units
{
    public const string CopilotCredit = "COPILOT_CREDIT";
    public const string P3CommitUnit = "P3_COMMIT_UNIT";
    public const string Seat = "SEAT";
    public static string Label(string unit) => unit switch
    {
        CopilotCredit => "Copilot Credits",
        P3CommitUnit => "P3 commit units",
        Seat => "seats",
        _ => unit,
    };
}

public static class FindingTypes
{
    public const string UnregisteredEnvironment = "UNREGISTERED_ENVIRONMENT";
    public const string UnregisteredAgent = "UNREGISTERED_AGENT";
    public const string UnregisteredFlow = "UNREGISTERED_FLOW";
    public const string OwnerChange = "OWNER_CHANGE";
    public const string HarnessChange = "HARNESS_CHANGE";
    public const string EnvironmentTypeChange = "ENVIRONMENT_TYPE_CHANGE";
    public const string AllocationDrift = "ALLOCATION_DRIFT";
    public const string TenantPoolDrift = "TENANT_POOL_DRIFT";
    public const string PaygDrift = "PAYG_DRIFT";
    public const string AgentLimitDrift = "AGENT_LIMIT_DRIFT";
    public const string AssignmentWithoutFunding = "ASSIGNMENT_WITHOUT_FUNDING";
    public const string MembershipOutsideScope = "MEMBERSHIP_OUTSIDE_SCOPE";
    public const string Vacancy = "VACANCY";
    public const string StaleSync = "STALE_SYNC";
    public const string AttestationOverdue = "ATTESTATION_OVERDUE";
    public const string UnmatchedRegistryAgent = "UNMATCHED_REGISTRY_AGENT";
    public const string ExceptionExpired = "EXCEPTION_EXPIRED";
    // Copilot seat licenses
    public const string SeatOverReservation = "SEAT_OVER_RESERVATION";
    public const string SeatReservationsOverInventory = "SEAT_RESERVED_OVER_PURCHASED";
    public const string SeatNoLob = "SEAT_NO_LOB";
    public const string SeatTwoLobs = "SEAT_TWO_LOBS";
    public const string SeatTrackedNoLicense = "SEAT_TRACKED_NO_LICENSE";
    public const string SeatDirectAssignment = "SEAT_DIRECT_ASSIGNMENT";
    public const string SeatLicenseError = "SEAT_LICENSE_ERROR";
    public const string SeatLeaver = "SEAT_LEAVER";
    public const string SeatExcludedHolder = "SEAT_EXCLUDED_HOLDER";
    public const string SeatPaidTwice = "SEAT_PAID_TWICE";
    public const string SeatNestedGroup = "SEAT_NESTED_GROUP";
    public const string SeatGroupMissing = "SEAT_GROUP_MISSING";
    public const string SeatCostCenterMismatch = "SEAT_COST_CENTER_MISMATCH";
}

/// <summary>Copilot seat licenses reserved per LOB (core.SeatGroup, core.SeatReservation).</summary>
public static class SeatGroupPurposes
{
    public const string License = "LICENSE";
    public const string LobTracking = "LOB_TRACKING";
    public const string Exclusion = "EXCLUSION";
    public const string ChatOnly = "CHAT_ONLY";
    public const string Manager = "MANAGER";
    public const string Other = "OTHER";
}

public static class SeatChangeKinds
{
    public const string Initial = "INITIAL";
    public const string New = "NEW";
    public const string Increase = "INCREASE";
    public const string TrueDown = "TRUE_DOWN";
    public const string Correction = "CORRECTION";
}

public static class SeatSkuKinds
{
    public const string CopilotAddOn = "COPILOT_ADDON";
    public const string IncludesCopilot = "INCLUDES_COPILOT";
}

public static class SeatReclaimStatuses
{
    public const string Proposed = "PROPOSED";
    public const string Keep = "KEEP";
    public const string Reclaim = "RECLAIM";
    public const string Removed = "REMOVED";
    public const string Cancelled = "CANCELLED";
}

public static class DecisionSources
{
    public const string Workbench = "WORKBENCH";
    public const string Entra = "ENTRA";
    public const string Migrated = "MIGRATED";
}

/// <summary>Disciplines a specialist review (B8) can open, with the role that reviews and the decision it feeds.</summary>
public static class Disciplines
{
    public const string Architecture = "ARCHITECTURE";
    public const string SecurityRisk = "SECURITY_RISK";
    public const string Finance = "FINANCE";
    public const string Operations = "OPERATIONS";
    public const string Business = "BUSINESS";
    public const string Platform = "PLATFORM";
}

public static class Permissions
{
    public const string RequestApproveRoutine = "request.approve.routine";
    public const string RequestDecide = "request.decide";
    public const string RequestCreate = "request.create";
    public const string WorkloadView = "workload.view";
    public const string WorkloadEdit = "workload.edit";
    public const string FundingOwnerView = "fundingowner.view";
    public const string EnvelopeApprove = "envelope.approve";
    public const string EnvelopeManage = "envelope.manage";
    public const string DelegationManage = "delegation.manage";
    public const string RoleAssignLob = "role.assign.lob";
    public const string RoleAssignCentral = "role.assign.central";
    public const string RegistryEdit = "registry.edit";
    public const string RegistryFinance = "registry.finance";
    public const string RegistryClaim = "registry.claim";
    public const string RaciApprove = "raci.approve";
    public const string TemplateApprove = "template.approve";
    public const string TemplateDraft = "template.draft";
    public const string IntakeOpen = "intake.open";
    public const string Verify = "verify";
    public const string Attest = "attest";
    public const string SyncRun = "sync.run";
    public const string EventRespond = "event.respond";
    public const string Review = "review";
    public const string EvidenceUpload = "evidence.upload";
    public const string CommercialView = "commercial.view";
    public const string CommercialExecute = "commercial.execute";
    public const string LicenseDetermine = "license.determine";
    public const string AdminConfigure = "admin.configure";
    public const string AuditView = "audit.view";
    public const string AuditExport = "audit.export";
    public const string ReportsView = "reports.view";
}
