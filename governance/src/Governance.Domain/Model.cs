namespace Governance.Domain;

// Rows as Dapper reads them. Property names match column names.

public sealed class Person
{
    public int PersonId { get; init; }
    public Guid PublicId { get; init; }
    public Guid? EntraObjectId { get; init; }
    public string? UserPrincipalName { get; init; }
    public string DisplayName { get; init; } = "";
    public string? Mail { get; init; }
    public string? JobTitle { get; init; }
    public string? Department { get; init; }
    public string? Division { get; init; }
    public string? CostCenterCode { get; init; }
    public int? ManagerPersonId { get; init; }
    public bool AccountEnabled { get; init; }
    public DateTime? LeaveDate { get; init; }
    public bool IsSynthetic { get; init; }
}

public sealed record RoleGrant(string RoleCode, string ScopeType, int? ScopeId);

public sealed class FundingOwner
{
    public int FundingOwnerId { get; init; }
    public Guid PublicId { get; init; }
    public string Code { get; init; } = "";
    public string Name { get; init; } = "";
    public string OwnerType { get; init; } = "";
    public int? ParentFundingOwnerId { get; init; }
    public int? BusinessOwnerPersonId { get; init; }
    public int? FinancePartnerPersonId { get; init; }
    public string? FinanceNode { get; init; }
    public string Status { get; init; } = "";
    public bool IsCriticalShared { get; init; }
    public bool NotificationRecipientsConfirmed { get; init; }
    public bool AttributionTested { get; init; }
    public DateTime EffectiveFrom { get; init; }
    public DateTime? EffectiveTo { get; init; }
    public byte[]? RowVer { get; init; }
}

public sealed class Service
{
    public int ServiceId { get; init; }
    public Guid PublicId { get; init; }
    public string Code { get; init; } = "";
    public string Name { get; init; } = "";
    public string? Harness { get; init; }
    public string RouteType { get; init; } = "";
    public string TenantAvailability { get; init; } = "";
    public string InstitutionalStatus { get; init; } = "";
    public bool IsCustomerFacing { get; init; }
    public string Audiences { get; init; } = "";
    public string DataClasses { get; init; } = "";
    public string AccessTiers { get; init; } = "";
    public int? OperationalOwnerPersonId { get; init; }
    public DateTime? ReviewOn { get; init; }
    public string? Description { get; init; }
}

public sealed class ApprovedPattern
{
    public int PatternId { get; init; }
    public string Code { get; init; } = "";
    public string Name { get; init; } = "";
    public int ServiceId { get; init; }
    public string Audiences { get; init; } = "";
    public string MaxDataClass { get; init; } = "";
    public string ActionScopes { get; init; } = "";
    public string LifecycleStages { get; init; } = "";
    public string TriggerTypes { get; init; } = "";
    public string Tiers { get; init; } = "";
    public decimal? MaxCreditsPerMonth { get; init; }
    public bool IsActive { get; init; }
}

public sealed class Workload
{
    public int WorkloadId { get; init; }
    public Guid PublicId { get; init; }
    public string Name { get; init; } = "";
    public string WorkloadType { get; init; } = "";
    public int FundingOwnerId { get; init; }
    public int? CostCenterId { get; init; }
    public string? SolutionClass { get; init; }
    public string? LifecycleStage { get; init; }
    public int? ServiceId { get; init; }
    public string? Criticality { get; init; }
    public string? TriggerType { get; init; }
    public string? DataBoundary { get; init; }
    public string? DataClass { get; init; }
    public string? Audience { get; init; }
    public string? EnvironmentClassCode { get; init; }
    public int? ProductOwnerPersonId { get; init; }
    public int? SponsorPersonId { get; init; }
    public string? SupportContact { get; init; }
    public string State { get; init; } = "";
    public DateTime? ExpiresOn { get; init; }
    public DateTime? NextReviewOn { get; init; }
    public string? ComplianceCaseRef { get; init; }
}

public sealed class Envelope
{
    public int EnvelopeId { get; init; }
    public Guid PublicId { get; init; }
    public int FundingOwnerId { get; init; }
    public int ServiceId { get; init; }
    public DateTime PeriodStart { get; init; }
    public DateTime PeriodEnd { get; init; }
    public decimal Amount { get; init; }
    public string Unit { get; init; } = "";
    public string Status { get; init; } = "";
    public decimal Committed { get; init; }   // computed in queries
}

public sealed class Delegation
{
    public int DelegationId { get; init; }
    public int FundingOwnerId { get; init; }
    public int DelegatePersonId { get; init; }
    public int GrantedByPersonId { get; init; }
    public decimal? MaxCreditsPerRequest { get; init; }
    public DateTime EffectiveFrom { get; init; }
    public DateTime? EffectiveTo { get; init; }
    public string Status { get; init; } = "";
}

public sealed class RequestRow
{
    public long RequestId { get; init; }
    public Guid PublicId { get; init; }
    public string RequestNumber { get; init; } = "";
    public string JourneyCode { get; init; } = "";
    public int DefinitionId { get; init; }
    public string State { get; init; } = "";
    public string? CurrentStage { get; init; }
    public string Title { get; init; } = "";
    public string? Summary { get; init; }
    public int? WorkloadId { get; init; }
    public int FundingOwnerId { get; init; }
    public int? ServiceId { get; init; }
    public int RequesterPersonId { get; init; }
    public int? SponsorPersonId { get; init; }
    public string PayloadJson { get; init; } = "{}";
    public decimal? EstimateCredits { get; init; }
    public int? EnvelopeId { get; init; }
    public int? PatternId { get; init; }
    public string? PatternResult { get; init; }
    public string? TriggeredReviews { get; init; }
    public DateTime CreatedUtc { get; init; }
    public DateTime? SubmittedUtc { get; init; }
    public DateTime? ClosedUtc { get; init; }
    public DateTime LastActivityUtc { get; init; }
    public int? ConfigurerPersonId { get; init; }
    public bool MetersDeclaredNone { get; init; }
    public byte[]? RowVer { get; init; }
}

public sealed class TaskRow
{
    public long TaskId { get; init; }
    public Guid PublicId { get; init; }
    public long? RequestId { get; init; }
    public int? FundingOwnerId { get; init; }
    public string? StageCode { get; init; }
    public string TaskType { get; init; } = "";
    public string Title { get; init; } = "";
    public string? AssignedRoleCode { get; init; }
    public string? ScopeType { get; init; }
    public int? ScopeId { get; init; }
    public int? AssignedPersonId { get; init; }
    public DateTime? ClaimedUtc { get; init; }
    public string Status { get; init; } = "";
    public bool IsBlocking { get; init; }
    public string? SlaPolicyCode { get; init; }
    public string? LadderCode { get; init; }
    public DateTime StartedUtc { get; init; }
    public DateTime? DueUtc { get; init; }
    public DateTime? Reminder1Utc { get; init; }
    public DateTime? Reminder2Utc { get; init; }
    public bool Reminded1 { get; init; }
    public bool Reminded2 { get; init; }
    public int EscalationLevel { get; init; }
    public string? RelatedEntityType { get; init; }
    public long? RelatedEntityId { get; init; }
    public string? Outcome { get; init; }
    public int? CompletedByPersonId { get; init; }
    public DateTime? CompletedUtc { get; init; }
    public string? Notes { get; init; }
    public string? DecisionType { get; init; }
    public string? RaciActivityCode { get; init; }
    public string? Discipline { get; init; }
    public string? Reason { get; init; }
    public byte[]? RowVer { get; init; }
}

public sealed class StageDefinition
{
    public int DefinitionId { get; init; }
    public string StageCode { get; init; } = "";
    public string Name { get; init; } = "";
    public string? RaciActivityCode { get; init; }
    public string TaskKind { get; init; } = "";
    public string? TaskRoleCode { get; init; }
    public string? DecisionType { get; init; }
    public string? SlaPolicyCode { get; init; }
    public string? LadderCode { get; init; }
    public bool AllowsDelegation { get; init; }
    public string? RequiredEvidence { get; init; }
    public string? EntryRule { get; init; }
    public string? ExitRule { get; init; }
    public string? NotifyCodes { get; init; }
    public string? Guidance { get; init; }
    public int SortOrder { get; init; }
}

public sealed class TransitionDefinition
{
    public int DefinitionId { get; init; }
    public string FromStage { get; init; } = "";
    public string Outcome { get; init; } = "";
    public string ToStage { get; init; } = "";
    public string ToState { get; init; } = "";
    public string? GuardRule { get; init; }
}

public sealed class SlaPolicy
{
    public string SlaPolicyCode { get; init; } = "";
    public string Name { get; init; } = "";
    public int Duration { get; init; }
    public string Unit { get; init; } = "";
    public string Calendar { get; init; } = "";
    public int RemindAtPct1 { get; init; }
    public int RemindAtPct2 { get; init; }
}

public sealed class RaciCell
{
    public int RaciVersionId { get; init; }
    public string ActivityCode { get; init; } = "";
    public string FunctionCode { get; init; } = "";
    public string RaciValue { get; init; } = "";
}

public sealed class RaciFunction
{
    public string FunctionCode { get; init; } = "";
    public string Name { get; init; } = "";
    public string ScopeType { get; init; } = "";
    public string? DecisionRoleCode { get; init; }
    public string? WorkRoleCode { get; init; }
    public string? ConsultRoleCode { get; init; }
    public int SortOrder { get; init; }
}

public sealed class DecisionRow
{
    public long DecisionId { get; init; }
    public Guid PublicId { get; init; }
    public long? RequestId { get; init; }
    public int? WorkloadId { get; init; }
    public int? FundingOwnerId { get; init; }
    public string DecisionType { get; init; } = "";
    public string Outcome { get; init; } = "";
    public int DeciderPersonId { get; init; }
    public string DeciderRoleCode { get; init; } = "";
    public int? DelegationId { get; init; }
    public int? OnBehalfOfPersonId { get; init; }
    public int RaciVersionId { get; init; }
    public string RaciActivityCode { get; init; } = "";
    public string? StageCode { get; init; }
    public string ScopeText { get; init; } = "";
    public string? ConditionsText { get; init; }
    public string? ReasonText { get; init; }
    public string? RequiredEvidenceText { get; init; }
    public DateTime? ExpiresOn { get; init; }
    public DateTime? NextReviewOn { get; init; }
    public string Source { get; init; } = "";
    public string? SourceRef { get; init; }
    public long? SupersedesDecisionId { get; init; }
    public DateTime DecidedAtUtc { get; init; }
}
