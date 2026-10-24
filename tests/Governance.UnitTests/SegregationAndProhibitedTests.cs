using Governance.Application.Rules;
using Governance.Domain;

namespace Governance.UnitTests;

public class SegregationOfDutiesTests
{
    private const int Requester = 1, Approver = 2, Configurer = 3, Other = 4;

    private static SegregationOfDuties.Context Ctx(int person, string taskType, string? stage = null, string? decisionType = null,
        bool admin = false, bool allowTwo = false, params (int, string)[] prior)
        => new(person, taskType, stage, decisionType, Requester, new[] { Approver }, Configurer, prior, admin, allowTwo);

    [Fact] public void SoD1_requester_cannot_decide() => Assert.Contains("SoD1", SegregationOfDuties.Check(Ctx(Requester, TaskTypes.Decision, Stages.RoutineApproval)).Reasons[0]);
    [Fact] public void SoD1_requester_can_do_their_own_work() => Assert.True(SegregationOfDuties.Check(Ctx(Requester, TaskTypes.Work, Stages.Remediate)).Passed);
    [Fact] public void SoD1_someone_else_can_decide() => Assert.True(SegregationOfDuties.Check(Ctx(Other, TaskTypes.Decision, Stages.RoutineApproval, DecisionTypes.RoutineApproval)).Passed);

    [Fact] public void SoD2_approver_cannot_prepare_fulfillment() => Assert.Contains("SoD2", SegregationOfDuties.Check(Ctx(Approver, TaskTypes.Work, Stages.PrepareFulfillment)).Reasons[0]);
    [Fact] public void SoD2_approver_cannot_verify() => Assert.Contains("SoD2", SegregationOfDuties.Check(Ctx(Approver, TaskTypes.Verification, Stages.Verify)).Reasons[0]);
    [Fact] public void SoD2_approver_may_do_unrelated_work() => Assert.True(SegregationOfDuties.Check(Ctx(Approver, TaskTypes.Work, Stages.BuildAndTest)).Passed);

    [Fact] public void SoD3_configurer_cannot_verify() => Assert.Contains("SoD3", SegregationOfDuties.Check(Ctx(Configurer, TaskTypes.Verification, Stages.Verify)).Reasons[0]);
    [Fact] public void SoD3_second_operator_can_verify() => Assert.True(SegregationOfDuties.Check(Ctx(Other, TaskTypes.Verification, Stages.Verify)).Passed);

    [Fact]
    public void SoD4_same_person_cannot_hold_budget_and_risk()
        => Assert.Contains("SoD4", SegregationOfDuties.Check(Ctx(Other, TaskTypes.Decision, Stages.Disposition, DecisionTypes.RiskAcceptance, prior: (Other, DecisionTypes.OverageAuthorization))).Reasons[0]);

    [Fact]
    public void SoD4_same_decision_type_again_is_allowed()
        => Assert.True(SegregationOfDuties.Check(Ctx(Other, TaskTypes.Decision, Stages.Disposition, DecisionTypes.RiskAcceptance, prior: (Other, DecisionTypes.RiskAcceptance))).Passed);

    [Fact]
    public void SoD4_policy_can_allow_two_types()
        => Assert.True(SegregationOfDuties.Check(Ctx(Other, TaskTypes.Decision, Stages.Disposition, DecisionTypes.RiskAcceptance, allowTwo: true, prior: (Other, DecisionTypes.BudgetApproval))).Passed);

    [Fact]
    public void SoD4_does_not_cover_platform_feature_and_preview_adoption()
        => Assert.True(SegregationOfDuties.Check(Ctx(Other, TaskTypes.Decision, Stages.Disposition, DecisionTypes.PreviewAdoption, prior: (Other, DecisionTypes.PlatformFeature))).Passed);

    [Fact]
    public void SoD4_purchase_and_budget_are_separate()
        => Assert.False(SegregationOfDuties.Check(Ctx(Other, TaskTypes.Decision, Stages.CommercialDecision, DecisionTypes.PurchaseApproval, prior: (Other, DecisionTypes.BudgetApproval))).Passed);

    [Fact] public void SoD5_admin_cannot_decide() => Assert.Contains("SoD5", SegregationOfDuties.Check(Ctx(Other, TaskTypes.Decision, Stages.RoutineApproval, admin: true)).Reasons[0]);
    [Fact] public void SoD5_admin_can_do_work() => Assert.True(SegregationOfDuties.Check(Ctx(Other, TaskTypes.Work, Stages.Completeness, admin: true)).Passed);

    [Fact] public void SoD6_needs_both_approvals() => Assert.False(SegregationOfDuties.TwoApprovers(1, null).Passed);
    [Fact] public void SoD6_needs_two_people() => Assert.Contains("different people", SegregationOfDuties.TwoApprovers(1, 1).Reasons[0]);
    [Fact] public void SoD6_two_people_pass() => Assert.True(SegregationOfDuties.TwoApprovers(1, 2).Passed);
}

/// <summary>Section 2.4 and 14.3: behaviors the Workbench never implements, as negative tests.</summary>
public class ProhibitedAssumptionsTests
{
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Allocation_is_never_a_cap_while_tenant_pool_or_payg_is_on(bool tenantPool, bool payg)
        => Assert.False(ProhibitedAssumptions.AllocationIsCap(tenantPool, payg));

    [Fact] public void Allocation_is_a_cap_only_with_both_off() => Assert.True(ProhibitedAssumptions.AllocationIsCap(false, false));

    [Theory]
    [InlineData("AzureBudget")]
    [InlineData("ActualCostAlert")]
    [InlineData("ForecastAlert")]
    [InlineData("PPAC overage notification")]
    public void A_budget_alert_is_never_a_stop_C08(string alert) => Assert.False(ProhibitedAssumptions.AlertStopsConsumption(alert));

    [Fact] public void An_agent_limit_never_covers_its_flows_C11() => Assert.False(ProhibitedAssumptions.AgentLimitCoversFlows());

    [Theory]
    [InlineData(10000, 1)]
    [InlineData(10000, 4)]
    [InlineData(0, 3)]
    public void Agent_consumption_is_never_inferred_from_the_environment(decimal consumed, int agents)
        => Assert.Null(ProhibitedAssumptions.AgentConsumptionFromEnvironment(consumed, agents));

    [Fact] public void Forecasts_never_purchase_C21() => Assert.False(ProhibitedAssumptions.ForecastMayTriggerPurchase());
    [Fact] public void Builders_never_select_billing_routes_A3() => Assert.False(ProhibitedAssumptions.BuilderMaySelectBillingRoute());

    [Theory]
    [InlineData("GA")]
    [InlineData("PREVIEW")]
    public void Availability_never_implies_approval_I3(string availability) => Assert.False(ProhibitedAssumptions.AvailableImpliesApproved(availability));

    [Fact] public void Beta_api_data_never_drives_a_control_C20() => Assert.False(ProhibitedAssumptions.MayUseForControl("GRAPH_BETA"));
    [Fact] public void Supported_sources_may_drive_controls() => Assert.True(ProhibitedAssumptions.MayUseForControl("POWER_PLATFORM"));
}
