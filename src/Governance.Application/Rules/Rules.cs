using Governance.Application.Requests;
using Governance.Domain;

namespace Governance.Application.Rules;

// The rules the workflow definitions reference (section 7.2). Pure functions: facts in, result out.

public sealed record RuleResult(bool Passed, IReadOnlyList<string> Reasons)
{
    public static readonly RuleResult Pass = new(true, Array.Empty<string>());
    public static RuleResult Fail(params string[] reasons) => new(false, reasons);
    public static RuleResult From(List<string> reasons) => reasons.Count == 0 ? Pass : new(false, reasons);
}

/// <summary>B2: required fields per journey, sponsor validity, funding owner status, catalogue eligibility, duplicates.</summary>
public static class CompletenessCheck
{
    public sealed record Facts(
        string JourneyCode,
        RequestPayload Payload,
        int RequesterPersonId,
        int? SponsorPersonId,
        bool SponsorActive,
        string? FundingOwnerStatus,
        Service? Service,
        Workload? ExistingWorkload,
        IReadOnlyList<string> DuplicateRequestNumbers);

    public static RuleResult Evaluate(Facts f)
    {
        var missing = new List<string>();
        var p = f.Payload;
        if (string.IsNullOrWhiteSpace(p.Purpose)) missing.Add("Describe the purpose.");
        if (f.FundingOwnerStatus is null) missing.Add("Choose the funding owner (LOB or Enterprise Shared).");
        else if (f.FundingOwnerStatus != "ACTIVE") missing.Add("The funding owner is not active; ask its business owner to complete activation.");

        switch (f.JourneyCode)
        {
            case Journeys.NewWorkload:
                if (f.SponsorPersonId is null) missing.Add("Name a workload sponsor.");
                else if (!f.SponsorActive) missing.Add("The sponsor's account is disabled or has left.");
                if (string.IsNullOrWhiteSpace(p.WorkloadName) && f.ExistingWorkload is null) missing.Add("Name the workload.");
                if (string.IsNullOrWhiteSpace(p.BusinessOutcome)) missing.Add("State the measurable business outcome.");
                if (f.Service is null) missing.Add("Choose a service from the catalogue.");
                else if (f.Service.InstitutionalStatus is "NOT_APPROVED" or "RETIRED" && !p.MaterialChange)
                    missing.Add($"{f.Service.Name} is not approved here; use the exception journey (J3) instead.");
                if (p.EstimateCreditsPerMonth is null) missing.Add("Estimate monthly Copilot Credits (a range is fine; write the middle).");
                if (string.IsNullOrWhiteSpace(p.RequestedLifecycleStage)) missing.Add("Choose the lifecycle stage you need.");
                break;
            case Journeys.Exception:
                if (f.SponsorPersonId is null) missing.Add("Name a sponsor.");
                if (string.IsNullOrWhiteSpace(p.RequestedDeviation)) missing.Add("Describe exactly what falls outside the approved patterns.");
                if (p.RequestedExpiry is null) missing.Add("Propose an expiry date: every exception expires.");
                break;
            case Journeys.CapacityChange:
                if (f.ExistingWorkload is null) missing.Add("Choose the workload or environment to change.");
                if (string.IsNullOrWhiteSpace(p.ChangeKind)) missing.Add("Choose what to change: allocation, agent limit or envelope.");
                if (p.RequestedValue is null) missing.Add("Give the requested value.");
                break;
            case Journeys.Retirement:
                if (f.ExistingWorkload is null) missing.Add("Choose the workload to retire.");
                if (string.IsNullOrWhiteSpace(p.RetirementReason)) missing.Add("Give the reason for retirement.");
                if (string.IsNullOrWhiteSpace(p.RetirementPlan)) missing.Add("Describe the retirement plan (users, data, dependencies).");
                break;
            case Journeys.Commercial:
                if (string.IsNullOrWhiteSpace(p.CommercialKind)) missing.Add("Choose the kind of commercial decision.");
                if (p.Quantity is null || string.IsNullOrWhiteSpace(p.Unit)) missing.Add("Give the quantity and its unit.");
                if (string.IsNullOrWhiteSpace(p.Rationale)) missing.Add("Give the rationale.");
                break;
            case Journeys.SeatReservation:
                if (p.RequestedSeats is null or < 0) missing.Add("Give the LOB's new total seat reservation.");
                else if (p.RequestedSeats == (p.CurrentSeats ?? 0)) missing.Add("The requested seats equal the current reservation: nothing changes.");
                if (string.IsNullOrWhiteSpace(p.SeatDemand)) missing.Add("Say who needs the seats and why (or why the seats can be given back).");
                break;
        }
        foreach (var d in f.DuplicateRequestNumbers) missing.Add($"Possible duplicate of {d}: link or withdraw one of them.");
        return RuleResult.From(missing);
    }
}

/// <summary>B3 exit: the four independent classification fields (C03), the trigger type and meters.</summary>
public static class ClassificationComplete
{
    public static RuleResult Evaluate(Workload w, int meterCount, bool metersDeclaredNone)
    {
        var gaps = new List<string>();
        if (w.FundingOwnerId <= 0) gaps.Add("Financial owner (LOB or Enterprise Shared).");
        if (w.CostCenterId is null) gaps.Add("Cost center.");
        if (string.IsNullOrEmpty(w.SolutionClass)) gaps.Add("Solution class.");
        if (string.IsNullOrEmpty(w.LifecycleStage)) gaps.Add("Lifecycle stage.");
        if (w.ServiceId is null) gaps.Add("Service.");
        if (string.IsNullOrEmpty(w.Criticality)) gaps.Add("Criticality.");
        if (string.IsNullOrEmpty(w.TriggerType)) gaps.Add("Trigger type.");
        if (string.IsNullOrEmpty(w.Audience)) gaps.Add("Audience.");
        if (string.IsNullOrEmpty(w.DataBoundary)) gaps.Add("Data boundary.");
        if (meterCount == 0 && !metersDeclaredNone) gaps.Add("Downstream meters (or confirm there are none).");
        return RuleResult.From(gaps);
    }
}

/// <summary>Envelope headroom in the envelope's own unit. Never converts units (E8).</summary>
public static class EnvelopeHeadroom
{
    public sealed record Result(bool HasEnvelope, decimal Headroom, decimal Shortfall, string? Unit);

    public static Result Evaluate(Envelope? envelope, decimal estimate, string estimateUnit = Units.CopilotCredit)
    {
        if (envelope is null || envelope.Status != "ACTIVE") return new Result(false, 0, estimate, null);
        if (!string.Equals(envelope.Unit, estimateUnit, StringComparison.Ordinal))
            throw new InvalidOperationException($"Envelope is in {envelope.Unit} and the estimate in {estimateUnit}; units are never converted without a dated rate (E8).");
        var headroom = envelope.Amount - envelope.Committed;
        return new Result(true, headroom, Math.Max(0, estimate - headroom), envelope.Unit);
    }
}

/// <summary>B4: does an approved pattern fit? Failed conditions become B8 triggers.</summary>
public static class PatternFit
{
    public sealed record Facts(
        string JourneyCode,
        RequestPayload Payload,
        Service? Service,
        Workload? Workload,
        IReadOnlyList<ApprovedPattern> Patterns,
        EnvelopeHeadroom.Result Envelope,
        EnvironmentLimits? Limits);

    public sealed record EnvironmentLimits(string ClassCode, decimal DefaultAgentLimit, bool TenantPoolAllowed, bool PaygAllowed);

    public sealed record Result(int? PatternId, string? PatternCode, IReadOnlyList<string> FailedConditions)
    {
        public bool Fits => PatternId is not null || (FailedConditions.Count == 0);
    }

    public static class Conditions
    {
        public const string NotInCatalogue = "SERVICE_NOT_APPROVED";
        public const string Preview = "PREVIEW_SERVICE";
        public const string CustomerFacing = "CUSTOMER_FACING";
        public const string DataClass = "SENSITIVE_DATA";
        public const string Connector = "NEW_CONNECTOR";
        public const string ActionScope = "ACTION_SCOPE";
        public const string Audience = "AUDIENCE";
        public const string Stage = "LIFECYCLE_STAGE";
        public const string Trigger = "AUTONOMOUS_OR_SCHEDULED";
        public const string GitHubHarness = "GITHUB_HARNESS";
        public const string Envelope = "ABOVE_ENVELOPE";
        public const string NoEnvelope = "NO_ENVELOPE";
        public const string BillingScope = "NEW_BILLING_SCOPE";
        public const string ProductionChange = "PRODUCTION_OR_CRITICAL";
        public const string MaterialChange = "MATERIAL_CHANGE";
        public const string AboveDefaultLimit = "ABOVE_DEFAULT_LIMIT";
        public const string TenantPool = "TENANT_POOL_REQUESTED";
        public const string MonthlyCap = "ABOVE_PATTERN_CAP";
    }

    public static Result Evaluate(Facts f)
    {
        var p = f.Payload;
        var failed = new List<string>();

        if (f.JourneyCode == Journeys.CapacityChange)
        {
            // J4: routine only within the envelope and the environment class default; tenant-pool draw is never routine.
            if (!f.Envelope.HasEnvelope) failed.Add(Conditions.NoEnvelope);
            else if (f.Envelope.Shortfall > 0) failed.Add(Conditions.Envelope);
            if (p.ChangeKind == "AGENT_LIMIT" && f.Limits is not null && p.RequestedValue > f.Limits.DefaultAgentLimit) failed.Add(Conditions.AboveDefaultLimit);
            if (p.RequestTenantPool) failed.Add(Conditions.TenantPool);
            if (f.Workload?.Criticality == "CRITICAL") failed.Add(Conditions.ProductionChange);
            return new Result(null, null, failed);
        }

        var service = f.Service;
        if (service is null || service.InstitutionalStatus is "NOT_APPROVED" or "RETIRED") failed.Add(Conditions.NotInCatalogue);
        if (service is not null && (service.TenantAvailability == "PREVIEW" || service.InstitutionalStatus == "PILOT")) failed.Add(Conditions.Preview);
        if (p.CustomerFacing || p.Audience == Audiences.Customers || service?.IsCustomerFacing == true) failed.Add(Conditions.CustomerFacing);
        if (p.NewConnectorOrIntegration) failed.Add(Conditions.Connector);
        if (p.NewBillingScope) failed.Add(Conditions.BillingScope);
        if (p.UsesGitHubHarness || (service?.Harness?.Contains("GitHub", StringComparison.OrdinalIgnoreCase) ?? false)) failed.Add(Conditions.GitHubHarness);
        if (p.TriggerType is TriggerTypes.Autonomous or TriggerTypes.Scheduled) failed.Add(Conditions.Trigger);
        if (p.MaterialChange) failed.Add(Conditions.MaterialChange);
        if (p.RequestedLifecycleStage == LifecycleStages.Production || f.Workload?.Criticality is "HIGH" or "CRITICAL") failed.Add(Conditions.ProductionChange);
        if (!f.Envelope.HasEnvelope) failed.Add(Conditions.NoEnvelope);
        else if (f.Envelope.Shortfall > 0) failed.Add(Conditions.Envelope);

        // A pattern fits when every request attribute is inside it.
        ApprovedPattern? match = null;
        var patternMisses = new List<string>();
        foreach (var pat in f.Patterns.Where(x => x.IsActive && x.ServiceId == service?.ServiceId))
        {
            var misses = new List<string>();
            if (!InList(pat.Audiences, p.Audience)) misses.Add(Conditions.Audience);
            if (DataClasses.Rank(p.DataClass) > DataClasses.Rank(pat.MaxDataClass)) misses.Add(Conditions.DataClass);
            if (!InList(pat.ActionScopes, p.ActionScope)) misses.Add(Conditions.ActionScope);
            if (!InList(pat.LifecycleStages, p.RequestedLifecycleStage)) misses.Add(Conditions.Stage);
            if (!InList(pat.TriggerTypes, p.TriggerType)) misses.Add(Conditions.Trigger);
            if (!string.IsNullOrEmpty(p.Tier) && !InList(pat.Tiers, p.Tier)) misses.Add(Conditions.Audience);
            if (pat.MaxCreditsPerMonth is decimal cap && (p.EstimateCreditsPerMonth ?? 0) > cap) misses.Add(Conditions.MonthlyCap);
            if (misses.Count == 0) { match = pat; break; }
            if (patternMisses.Count == 0 || misses.Count < patternMisses.Count) patternMisses = misses;
        }
        if (match is null) failed.AddRange(patternMisses.Count > 0 ? patternMisses : new List<string> { Conditions.NotInCatalogue });

        var distinct = failed.Distinct().ToList();
        return distinct.Count == 0 && match is not null
            ? new Result(match.PatternId, match.Code, Array.Empty<string>())
            : new Result(null, null, distinct.Count == 0 ? new List<string> { Conditions.NotInCatalogue } : distinct);
    }

    private static bool InList(string list, string? value)
        => value is not null && list.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                                    .Any(v => v == "*" || string.Equals(v, value, StringComparison.OrdinalIgnoreCase));

    public static string Describe(string condition) => condition switch
    {
        Conditions.NotInCatalogue => "The service or harness is not approved in the catalogue.",
        Conditions.Preview => "The service is in preview or pilot (C02).",
        Conditions.CustomerFacing => "Customers are in the audience (C13).",
        Conditions.DataClass => "The data class is above what the pattern allows.",
        Conditions.Connector => "A new connector or integration is involved.",
        Conditions.ActionScope => "The agent takes actions the pattern does not allow.",
        Conditions.Audience => "The audience or tier is outside the pattern.",
        Conditions.Stage => "The lifecycle stage is outside the pattern.",
        Conditions.Trigger => "The workload runs on a schedule or autonomously (C12).",
        Conditions.GitHubHarness => "The GitHub Copilot harness bills build and test usage (C12).",
        Conditions.Envelope => "The estimate is above the envelope's headroom.",
        Conditions.NoEnvelope => "The funding owner has no active envelope for this service.",
        Conditions.BillingScope => "A new billing scope (subscription, billing plan or policy) is needed.",
        Conditions.ProductionChange => "A production change or a high or critical service.",
        Conditions.MaterialChange => "A material change to an approved workload (I3).",
        Conditions.AboveDefaultLimit => "The agent limit is above the environment class default.",
        Conditions.TenantPool => "Drawing from tenant capacity removes the environment's cap (E3).",
        Conditions.MonthlyCap => "The monthly estimate is above the pattern's cap.",
        _ => condition,
    };
}

/// <summary>B8 entry: opens only the reviews the change needs (section 7.2 trigger table).</summary>
public static class TriggeredDisciplines
{
    /// <summary>A specialist review, and the decision it feeds at B9 (one accountable decision per type, SoD4).</summary>
    public sealed record Review(string Discipline, string ReviewerRole, string DecisionType, string ActivityCode, string DeciderRole, string Reason);

    public static IReadOnlyList<Review> Evaluate(IReadOnlyCollection<string> failed, string journeyCode)
    {
        var reviews = new List<Review>();
        void Add(Review r) { if (!reviews.Any(x => x.Discipline == r.Discipline && x.DecisionType == r.DecisionType)) reviews.Add(r); }
        bool Any(params string[] c) => c.Any(failed.Contains);
        const string C = PatternFit.Conditions.NotInCatalogue;

        if (Any(C, PatternFit.Conditions.Stage, PatternFit.Conditions.ActionScope, PatternFit.Conditions.Audience))
            Add(new(Disciplines.Architecture, Roles.Architect, DecisionTypes.PlatformFeature, "PLATFORM_FEATURE", Roles.PlatformOwner,
                "Service, harness or pattern outside the catalogue: Architecture reviews; the Platform Owner decides."));
        if (Any(PatternFit.Conditions.DataClass, PatternFit.Conditions.Connector))
        {
            Add(new(Disciplines.SecurityRisk, Roles.RiskReviewer, DecisionTypes.RiskAcceptance, "SECURITY_RISK", Roles.RiskReviewer,
                "Sensitive data or a new connector: Security and Risk decide risk acceptance."));
            Add(new(Disciplines.Architecture, Roles.Architect, DecisionTypes.PlatformFeature, "PLATFORM_FEATURE", Roles.PlatformOwner,
                "Sensitive data or a new connector: Architecture reviews the integration."));
        }
        if (Any(PatternFit.Conditions.CustomerFacing, PatternFit.Conditions.Preview))
        {
            Add(new(Disciplines.SecurityRisk, Roles.RiskReviewer, DecisionTypes.RiskAcceptance, "SECURITY_RISK", Roles.RiskReviewer,
                "Customer-facing or preview: Security and Risk decide risk acceptance."));
            Add(new(Disciplines.Platform, Roles.Architect, DecisionTypes.PreviewAdoption, "PLATFORM_FEATURE", Roles.PlatformOwner,
                "Customer-facing or preview: an explicit adoption decision with audience, test data, expiry and exit criteria (C8)."));
        }
        if (Any(PatternFit.Conditions.Envelope, PatternFit.Conditions.NoEnvelope, PatternFit.Conditions.BillingScope,
                PatternFit.Conditions.MonthlyCap, PatternFit.Conditions.AboveDefaultLimit, PatternFit.Conditions.TenantPool))
            Add(new(Disciplines.Finance, Roles.FinOpsAnalyst, DecisionTypes.OverageAuthorization, "OVERAGE", Roles.LobBusinessOwner,
                "Above the envelope or a new billing scope: FinOps reviews; the business owner authorizes the overage."));
        if (Any(PatternFit.Conditions.ProductionChange))
            Add(new(Disciplines.Operations, Roles.PlatformOperator, DecisionTypes.ContinuityAcceptance, "SHARED_CONTINUITY", Roles.LobBusinessOwner,
                "Production change or critical service: Operations reviews; the business owner accepts continuity."));
        if (Any(PatternFit.Conditions.Trigger, PatternFit.Conditions.GitHubHarness))
        {
            Add(new(Disciplines.Finance, Roles.FinOpsAnalyst, DecisionTypes.BudgetApproval, "FUNDING_BUDGET", Roles.LobBusinessOwner,
                "Autonomous, scheduled or GitHub harness: FinOps reviews the forecast; the business owner approves the budget."));
            Add(new(Disciplines.Operations, Roles.PlatformOperator, DecisionTypes.ContinuityAcceptance, "MONITORING_RESPONSE", Roles.PlatformOwner,
                "Autonomous, scheduled or GitHub harness: Operations confirms monitoring."));
        }
        if (Any(PatternFit.Conditions.MaterialChange))
            Add(new(Disciplines.Architecture, Roles.Architect, DecisionTypes.PlatformFeature, "PLATFORM_FEATURE", Roles.PlatformOwner,
                "Material change (I3): the change is re-reviewed and retested (D4)."));

        // J3 always ends with one exception disposition by the Platform Owner, on top of any typed decisions.
        if (journeyCode == Journeys.Exception && !reviews.Any(r => r.DecisionType == DecisionTypes.PlatformFeature))
            Add(new(Disciplines.Architecture, Roles.Architect, DecisionTypes.ExceptionDisposition, "PLATFORM_FEATURE", Roles.PlatformOwner,
                "Exception: Architecture reviews; the Platform Owner records the disposition."));
        if (reviews.Count == 0)
            Add(new(Disciplines.Architecture, Roles.Architect, DecisionTypes.ExceptionDisposition, "CLASSIFICATION_ROUTING", Roles.PlatformOwner,
                "No approved pattern fits: Architecture reviews; the Platform Owner decides."));
        return reviews;
    }
}

/// <summary>B5 and J4: the delegation and the envelope are checked at decision time and stored on the decision (SoD7).</summary>
public static class DelegationValid
{
    public static RuleResult Evaluate(Delegation? delegation, IReadOnlyCollection<int> delegationEnvelopeIds, Envelope? envelope, decimal amount, DateTime today)
    {
        if (delegation is null) return RuleResult.Fail("No active delegation for this approver.");
        if (delegation.Status != "ACTIVE" || delegation.EffectiveFrom.Date > today.Date || (delegation.EffectiveTo is DateTime to && to.Date < today.Date))
            return RuleResult.Fail("The delegation is not active today.");
        if (envelope is null || envelope.Status != "ACTIVE") return RuleResult.Fail("The funding owner has no active envelope for this service.");
        if (delegationEnvelopeIds.Count > 0 && !delegationEnvelopeIds.Contains(envelope.EnvelopeId))
            return RuleResult.Fail("The delegation does not cover this envelope.");
        if (delegation.MaxCreditsPerRequest is decimal max && amount > max)
            return RuleResult.Fail($"The request ({amount:N0}) is above the delegated limit ({max:N0}); the business owner decides.");
        if (envelope.Amount - envelope.Committed < amount)
            return RuleResult.Fail("The envelope has no headroom for this request.");
        return RuleResult.Pass;
    }
}

/// <summary>After D1: is a production acceptance gate (D5) required?</summary>
public static class ProductionGateRequired
{
    public static bool Evaluate(string? requestedStage, string? criticality, string? solutionClass)
        => requestedStage == LifecycleStages.Production
           || (criticality is "HIGH" or "CRITICAL" && solutionClass == SolutionClasses.BusinessApplication);
}

/// <summary>B8, C8: a preview or customer-facing route needs an explicit adoption decision (C02).</summary>
public static class PreviewAdoption
{
    public static bool Required(Service? service, bool customerFacing)
        => customerFacing || service is { TenantAvailability: "PREVIEW" } || service is { InstitutionalStatus: "PILOT" } || service is { IsCustomerFacing: true };
}

/// <summary>D1 exit: every check defined by the control template passed, including the explicit deny case.</summary>
public static class VerificationComplete
{
    public sealed record CheckResult(string CheckCode, string Method, bool IsDenyCase, string? Expected, string? Actual, bool Passed, bool EvidenceAttached);

    public static RuleResult Evaluate(IReadOnlyList<CheckResult> checks, int? configurerPersonId, int verifierPersonId, bool automatedReadBack)
    {
        var reasons = new List<string>();
        if (checks.Count == 0) reasons.Add("The control template defines no checks; nothing can be verified.");
        if (!checks.Any(c => c.IsDenyCase)) reasons.Add("The explicit deny case was not tested.");
        foreach (var c in checks.Where(c => !c.Passed)) reasons.Add($"{c.CheckCode}: expected {c.Expected ?? "pass"}, found {c.Actual ?? "no value"}.");
        foreach (var c in checks.Where(c => c.Method == "ATTESTATION" && !c.EvidenceAttached)) reasons.Add($"{c.CheckCode}: attestation needs evidence attached.");
        // SoD3: the configurer cannot be the only verifier, unless every check is an automated read-back.
        if (configurerPersonId == verifierPersonId && !automatedReadBack) reasons.Add("The person who configured it cannot be the only verifier (SoD3).");
        return RuleResult.From(reasons);
    }
}
