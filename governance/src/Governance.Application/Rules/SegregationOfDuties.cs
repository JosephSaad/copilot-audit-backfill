using Governance.Domain;

namespace Governance.Application.Rules;

/// <summary>Segregation of duties (section 5.7). Checked when a task is created (exclusions) and again when it is completed.</summary>
public static class SegregationOfDuties
{
    public sealed record Context(
        int PersonId,
        string TaskType,
        string? StageCode,
        string? DecisionType,
        int RequesterPersonId,
        IReadOnlyCollection<int> ApproverPersonIds,                 // people who recorded a positive decision on this request
        int? ConfigurerPersonId,                                    // from the ITSM closure
        IReadOnlyCollection<(int PersonId, string DecisionType)> PriorDecisions,
        bool PersonIsAdministrator,
        bool AllowSamePersonForTwoDecisionTypes);

    public static RuleResult Check(Context c)
    {
        var isDecision = c.TaskType == TaskTypes.Decision;
        // SoD1: a requester cannot approve their own request.
        if (isDecision && c.PersonId == c.RequesterPersonId)
            return RuleResult.Fail("You submitted this request, so you cannot decide it (SoD1).");
        // SoD5: administrators cannot approve business decisions.
        if (isDecision && c.PersonIsAdministrator)
            return RuleResult.Fail("Workbench administrators cannot record business decisions (SoD5).");
        // SoD2: the approver of a request cannot configure or prepare its fulfillment.
        var isFulfillment = c.StageCode is Stages.PrepareFulfillment or Stages.Fulfillment || c.TaskType == TaskTypes.Verification;
        if (isFulfillment && c.ApproverPersonIds.Contains(c.PersonId))
            return RuleResult.Fail("You approved this request, so you cannot prepare, configure or verify it (SoD2).");
        // SoD3: the configurer cannot be the only verifier.
        if (c.TaskType == TaskTypes.Verification && c.ConfigurerPersonId == c.PersonId)
            return RuleResult.Fail("You configured this change, so a second operator verifies it (SoD3).");
        // SoD4: budget approval, risk acceptance and purchase authority are separate records; by policy one person
        // does not hold two of them on the same request.
        if (isDecision && !c.AllowSamePersonForTwoDecisionTypes && c.DecisionType is not null
            && c.PriorDecisions.Any(d => d.PersonId == c.PersonId && d.DecisionType != c.DecisionType && SeparatedTypes.Contains(d.DecisionType) && SeparatedTypes.Contains(c.DecisionType)))
            return RuleResult.Fail("You already recorded a different decision type on this request; budget, risk and purchase decisions need different people (SoD4).");
        return RuleResult.Pass;
    }

    /// <summary>Decision types SoD4 keeps apart.</summary>
    public static readonly IReadOnlySet<string> SeparatedTypes = new HashSet<string>
    {
        DecisionTypes.BudgetApproval, DecisionTypes.OverageAuthorization, DecisionTypes.RoutineApproval,
        DecisionTypes.RiskAcceptance, DecisionTypes.PurchaseApproval, DecisionTypes.RenewalApproval,
    };

    /// <summary>SoD6: a RACI or template version needs two different approvers (Platform Owner and Steering).</summary>
    public static RuleResult TwoApprovers(int? platformOwnerApprover, int? steeringApprover)
    {
        if (platformOwnerApprover is null || steeringApprover is null) return RuleResult.Fail("Both the Platform Owner and Steering must approve (SoD6).");
        if (platformOwnerApprover == steeringApprover) return RuleResult.Fail("The two approvals must come from different people (SoD6).");
        return RuleResult.Pass;
    }
}

/// <summary>
/// Behaviors the Workbench never implements (section 2.4). Each is a function the rest of the code calls, so
/// the negative tests can prove the answer never changes.
/// </summary>
public static class ProhibitedAssumptions
{
    /// <summary>C08: an Azure budget or alert notifies; it never stops consumption.</summary>
    public static bool AlertStopsConsumption(string alertKind) => false;

    /// <summary>E2 to E4: an environment allocation is a cap only when tenant-pool draw is off and no PAYG billing plan is linked.</summary>
    public static bool AllocationIsCap(bool tenantPoolEnabled, bool paygLinked) => !tenantPoolEnabled && !paygLinked;

    /// <summary>C11: an agent limit does not stop the agent flows the agent calls.</summary>
    public static bool AgentLimitCoversFlows() => false;

    /// <summary>R01: per-agent threshold consumption is never inferred from environment aggregates.</summary>
    public static decimal? AgentConsumptionFromEnvironment(decimal environmentConsumed, int agentCount) => null;

    /// <summary>C21, F10: forecasts never trigger purchases, renewals or resizes.</summary>
    public static bool ForecastMayTriggerPurchase() => false;

    /// <summary>A3: builders never select billing routes or change billing associations.</summary>
    public static bool BuilderMaySelectBillingRoute() => false;

    /// <summary>I3: a capability is not approved because a platform exposes it.</summary>
    public static bool AvailableImpliesApproved(string tenantAvailability) => false;

    /// <summary>C20: beta API data never drives a control decision.</summary>
    public static bool MayUseForControl(string sourceSystem) => sourceSystem != "GRAPH_BETA";
}
