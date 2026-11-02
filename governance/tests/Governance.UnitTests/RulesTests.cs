using Governance.Application.Requests;
using Governance.Application.Rules;
using Governance.Domain;
using static Governance.UnitTests.TestData;
using C = Governance.Application.Rules.PatternFit.Conditions;

namespace Governance.UnitTests;

public class CompletenessCheckTests
{
    private static CompletenessCheck.Facts J2(RequestPayload p, Service? s = null, string? foStatus = "ACTIVE", int? sponsor = 2, bool sponsorActive = true, params string[] dupes)
        => new(Journeys.NewWorkload, p, 1, sponsor, sponsorActive, foStatus, s ?? Studio(), null, dupes);

    [Fact] public void Complete_new_workload_passes() => Assert.True(CompletenessCheck.Evaluate(J2(MakerPayload())).Passed);

    [Fact]
    public void Missing_outcome_and_estimate_are_listed()
    {
        var p = MakerPayload(); p.BusinessOutcome = null; p.EstimateCreditsPerMonth = null;
        var r = CompletenessCheck.Evaluate(J2(p));
        Assert.False(r.Passed);
        Assert.Contains(r.Reasons, x => x.Contains("outcome"));
        Assert.Contains(r.Reasons, x => x.Contains("Copilot Credits"));
    }

    [Fact] public void No_sponsor_is_refused() => Assert.Contains(CompletenessCheck.Evaluate(J2(MakerPayload(), sponsor: null)).Reasons, x => x.Contains("sponsor"));

    [Fact] public void Disabled_sponsor_is_refused() => Assert.Contains(CompletenessCheck.Evaluate(J2(MakerPayload(), sponsorActive: false)).Reasons, x => x.Contains("disabled"));

    [Fact] public void Draft_funding_owner_is_refused() => Assert.Contains(CompletenessCheck.Evaluate(J2(MakerPayload(), foStatus: "DRAFT")).Reasons, x => x.Contains("not active"));

    [Fact]
    public void Unapproved_service_points_to_the_exception_journey()
    {
        var s = Studio(); s = new Service { ServiceId = 1, Name = s.Name, InstitutionalStatus = "NOT_APPROVED", TenantAvailability = "GA", RouteType = s.RouteType };
        Assert.Contains(CompletenessCheck.Evaluate(J2(MakerPayload(), s)).Reasons, x => x.Contains("J3"));
    }

    [Fact] public void Duplicates_are_listed() => Assert.Contains(CompletenessCheck.Evaluate(J2(MakerPayload(), dupes: "CCG-2026-000004")).Reasons, x => x.Contains("CCG-2026-000004"));

    [Fact]
    public void Exception_needs_deviation_and_expiry()
    {
        var r = CompletenessCheck.Evaluate(new(Journeys.Exception, new RequestPayload { Purpose = "x" }, 1, 2, true, "ACTIVE", null, null, Array.Empty<string>()));
        Assert.Contains(r.Reasons, x => x.Contains("outside the approved patterns"));
        Assert.Contains(r.Reasons, x => x.Contains("expires"));
    }

    [Fact]
    public void Capacity_change_needs_workload_kind_and_value()
    {
        var r = CompletenessCheck.Evaluate(new(Journeys.CapacityChange, new RequestPayload { Purpose = "x" }, 1, null, true, "ACTIVE", null, null, Array.Empty<string>()));
        Assert.Equal(3, r.Reasons.Count);
    }

    [Fact]
    public void Retirement_and_commercial_rules()
    {
        Assert.Equal(3, CompletenessCheck.Evaluate(new(Journeys.Retirement, new RequestPayload { Purpose = "x" }, 1, null, true, "ACTIVE", null, null, Array.Empty<string>())).Reasons.Count);
        Assert.Equal(3, CompletenessCheck.Evaluate(new(Journeys.Commercial, new RequestPayload { Purpose = "x" }, 1, null, true, "ACTIVE", null, null, Array.Empty<string>())).Reasons.Count);
    }
}

public class ClassificationCompleteTests
{
    private static Workload Full() => new()
    {
        FundingOwnerId = 1, CostCenterId = 2, SolutionClass = SolutionClasses.Productivity, LifecycleStage = LifecycleStages.Development, ServiceId = 3,
        Criticality = "LOW", TriggerType = TriggerTypes.UserInitiated, Audience = "My team", DataBoundary = "Internal",
    };

    [Fact] public void Complete_with_meters_declared_none() => Assert.True(ClassificationComplete.Evaluate(Full(), 0, true).Passed);
    [Fact] public void Complete_with_meters() => Assert.True(ClassificationComplete.Evaluate(Full(), 2, false).Passed);
    [Fact] public void Meters_must_be_listed_or_declared_none() => Assert.Contains(ClassificationComplete.Evaluate(Full(), 0, false).Reasons, x => x.Contains("meters"));

    [Fact]
    public void Every_independent_field_is_required()
    {
        var r = ClassificationComplete.Evaluate(new Workload { FundingOwnerId = 1 }, 0, true);
        Assert.Equal(8, r.Reasons.Count);   // cost center, class, stage, service, criticality, trigger, audience, boundary
    }
}

public class EnvelopeHeadroomTests
{
    [Fact]
    public void No_envelope_means_full_shortfall()
    {
        var r = EnvelopeHeadroom.Evaluate(null, 500);
        Assert.False(r.HasEnvelope);
        Assert.Equal(500, r.Shortfall);
    }

    [Fact]
    public void Headroom_subtracts_commitments()
    {
        var r = EnvelopeHeadroom.Evaluate(ActiveEnvelope(1000, 400), 800);
        Assert.Equal(600, r.Headroom);
        Assert.Equal(200, r.Shortfall);
    }

    [Fact] public void Draft_envelope_does_not_count() => Assert.False(EnvelopeHeadroom.Evaluate(new Envelope { Amount = 1000, Unit = Units.CopilotCredit, Status = "DRAFT" }, 1).HasEnvelope);

    [Fact]
    public void Units_are_never_converted_E8()
        => Assert.Throws<InvalidOperationException>(() => EnvelopeHeadroom.Evaluate(ActiveEnvelope(unit: "USD"), 100, Units.CopilotCredit));

    [Fact]
    public void P3_units_and_credits_do_not_mix_E8()
        => Assert.Throws<InvalidOperationException>(() => EnvelopeHeadroom.Evaluate(ActiveEnvelope(unit: Units.P3CommitUnit), 100, Units.CopilotCredit));
}

public class PatternFitTests
{
    [Fact]
    public void Maker_request_fits_the_pattern()
    {
        var r = Fit(MakerPayload());
        Assert.True(r.Fits);
        Assert.Equal(7, r.PatternId);
        Assert.Empty(r.FailedConditions);
    }

    public static IEnumerable<object[]> Misses() => new[]
    {
        new object[] { (Action<RequestPayload>)(p => p.RequestedLifecycleStage = LifecycleStages.Production), C.ProductionChange },
        new object[] { (Action<RequestPayload>)(p => p.CustomerFacing = true), C.CustomerFacing },
        new object[] { (Action<RequestPayload>)(p => p.Audience = Audiences.Customers), C.CustomerFacing },
        new object[] { (Action<RequestPayload>)(p => p.NewConnectorOrIntegration = true), C.Connector },
        new object[] { (Action<RequestPayload>)(p => p.NewBillingScope = true), C.BillingScope },
        new object[] { (Action<RequestPayload>)(p => p.UsesGitHubHarness = true), C.GitHubHarness },
        new object[] { (Action<RequestPayload>)(p => p.TriggerType = TriggerTypes.Autonomous), C.Trigger },
        new object[] { (Action<RequestPayload>)(p => p.TriggerType = TriggerTypes.Scheduled), C.Trigger },
        new object[] { (Action<RequestPayload>)(p => p.MaterialChange = true), C.MaterialChange },
        new object[] { (Action<RequestPayload>)(p => p.DataClass = DataClasses.Confidential), C.DataClass },
        new object[] { (Action<RequestPayload>)(p => p.ActionScope = ActionScopes.External), C.ActionScope },
        new object[] { (Action<RequestPayload>)(p => p.Audience = Audiences.AllEmployees), C.Audience },
        new object[] { (Action<RequestPayload>)(p => p.EstimateCreditsPerMonth = 2500), C.MonthlyCap },
    };

    [Theory, MemberData(nameof(Misses))]
    public void Each_condition_outside_the_pattern_fails(Action<RequestPayload> change, string expected)
    {
        var p = MakerPayload(); change(p);
        var r = Fit(p);
        Assert.False(r.Fits);
        Assert.Null(r.PatternId);
        Assert.Contains(expected, r.FailedConditions);
    }

    [Fact] public void Above_envelope_fails() => Assert.Contains(C.Envelope, Fit(MakerPayload(), envelope: Headroom(1000, 900, 800)).FailedConditions);
    [Fact] public void No_envelope_fails() => Assert.Contains(C.NoEnvelope, Fit(MakerPayload(), envelope: EnvelopeHeadroom.Evaluate(null, 800)).FailedConditions);

    [Fact]
    public void Preview_service_fails()
    {
        var s = new Service { ServiceId = 1, Harness = "Customer Conversation", TenantAvailability = "PREVIEW", InstitutionalStatus = "PILOT", RouteType = "PPAC_ENVIRONMENT" };
        Assert.Contains(C.Preview, Fit(MakerPayload(), s).FailedConditions);
    }

    [Fact]
    public void Unapproved_service_fails_as_not_in_catalogue()
    {
        var s = new Service { ServiceId = 1, TenantAvailability = "GA", InstitutionalStatus = "NOT_APPROVED", RouteType = "PPAC_ENVIRONMENT" };
        Assert.Contains(C.NotInCatalogue, Fit(MakerPayload(), s).FailedConditions);
    }

    [Fact]
    public void A_service_without_any_pattern_never_fits()
    {
        var r = PatternFit.Evaluate(new(Journeys.NewWorkload, MakerPayload(), Studio(9), null, new[] { MakerPattern() }, Headroom(), null));
        Assert.False(r.Fits);
    }

    [Fact]
    public void Inactive_pattern_is_ignored()
    {
        var pat = MakerPattern();
        var inactive = new ApprovedPattern { PatternId = pat.PatternId, ServiceId = 1, Audiences = pat.Audiences, MaxDataClass = pat.MaxDataClass, ActionScopes = pat.ActionScopes,
            LifecycleStages = pat.LifecycleStages, TriggerTypes = pat.TriggerTypes, Tiers = "*", IsActive = false };
        Assert.False(PatternFit.Evaluate(new(Journeys.NewWorkload, MakerPayload(), Studio(), null, new[] { inactive }, Headroom(), null)).Fits);
    }

    private static readonly PatternFit.EnvironmentLimits Dev = new("DEVELOPER", 2000, false, false);

    [Fact]
    public void Capacity_change_within_envelope_and_default_fits()
    {
        var p = new RequestPayload { ChangeKind = "AGENT_LIMIT", CurrentValue = 1000, RequestedValue = 1500 };
        Assert.True(Fit(p, journey: Journeys.CapacityChange, limits: Dev).Fits);
    }

    [Fact]
    public void Capacity_change_above_default_limit_fails()
        => Assert.Contains(C.AboveDefaultLimit, Fit(new RequestPayload { ChangeKind = "AGENT_LIMIT", RequestedValue = 4000 }, journey: Journeys.CapacityChange, limits: Dev).FailedConditions);

    [Fact]
    public void Tenant_pool_draw_is_never_routine()
        => Assert.Contains(C.TenantPool, Fit(new RequestPayload { ChangeKind = "ALLOCATION", RequestedValue = 100, RequestTenantPool = true }, journey: Journeys.CapacityChange, limits: Dev).FailedConditions);

    [Fact]
    public void Capacity_change_on_critical_workload_fails()
        => Assert.Contains(C.ProductionChange, Fit(new RequestPayload { ChangeKind = "ALLOCATION", RequestedValue = 100 }, journey: Journeys.CapacityChange, workload: new Workload { Criticality = "CRITICAL" }).FailedConditions);

    [Fact]
    public void Every_condition_has_a_plain_description()
    {
        foreach (var f in typeof(C).GetFields())
        {
            var code = (string)f.GetValue(null)!;
            Assert.NotEqual(code, PatternFit.Describe(code));
        }
    }
}

public class TriggeredDisciplinesTests
{
    [Theory]
    [InlineData(C.NotInCatalogue, Disciplines.Architecture, DecisionTypes.PlatformFeature)]
    [InlineData(C.DataClass, Disciplines.SecurityRisk, DecisionTypes.RiskAcceptance)]
    [InlineData(C.Connector, Disciplines.SecurityRisk, DecisionTypes.RiskAcceptance)]
    [InlineData(C.Connector, Disciplines.Architecture, DecisionTypes.PlatformFeature)]
    [InlineData(C.CustomerFacing, Disciplines.SecurityRisk, DecisionTypes.RiskAcceptance)]
    [InlineData(C.CustomerFacing, Disciplines.Platform, DecisionTypes.PreviewAdoption)]
    [InlineData(C.Preview, Disciplines.Platform, DecisionTypes.PreviewAdoption)]
    [InlineData(C.Envelope, Disciplines.Finance, DecisionTypes.OverageAuthorization)]
    [InlineData(C.BillingScope, Disciplines.Finance, DecisionTypes.OverageAuthorization)]
    [InlineData(C.TenantPool, Disciplines.Finance, DecisionTypes.OverageAuthorization)]
    [InlineData(C.ProductionChange, Disciplines.Operations, DecisionTypes.ContinuityAcceptance)]
    [InlineData(C.Trigger, Disciplines.Finance, DecisionTypes.BudgetApproval)]
    [InlineData(C.GitHubHarness, Disciplines.Operations, DecisionTypes.ContinuityAcceptance)]
    [InlineData(C.MaterialChange, Disciplines.Architecture, DecisionTypes.PlatformFeature)]
    public void Condition_opens_the_right_review(string condition, string discipline, string decisionType)
        => Assert.Contains(TriggeredDisciplines.Evaluate(new[] { condition }, Journeys.NewWorkload), r => r.Discipline == discipline && r.DecisionType == decisionType);

    [Fact]
    public void Only_triggered_disciplines_review()
    {
        var reviews = TriggeredDisciplines.Evaluate(new[] { C.Envelope }, Journeys.NewWorkload);
        Assert.All(reviews, r => Assert.Equal(Disciplines.Finance, r.Discipline));
    }

    [Fact]
    public void Overage_is_decided_by_the_business_owner_and_risk_by_security()
    {
        var reviews = TriggeredDisciplines.Evaluate(new[] { C.Envelope, C.Connector }, Journeys.NewWorkload);
        Assert.Equal(Roles.LobBusinessOwner, reviews.Single(r => r.DecisionType == DecisionTypes.OverageAuthorization).DeciderRole);
        Assert.Equal(Roles.RiskReviewer, reviews.Single(r => r.DecisionType == DecisionTypes.RiskAcceptance).DeciderRole);
    }

    [Fact]
    public void Same_review_is_not_opened_twice()
    {
        var reviews = TriggeredDisciplines.Evaluate(new[] { C.NotInCatalogue, C.Stage, C.Connector, C.MaterialChange }, Journeys.NewWorkload);
        Assert.Single(reviews, r => r.Discipline == Disciplines.Architecture && r.DecisionType == DecisionTypes.PlatformFeature);
    }

    [Fact]
    public void Exception_without_platform_review_gets_a_disposition()
        => Assert.Contains(TriggeredDisciplines.Evaluate(new[] { C.BillingScope }, Journeys.Exception), r => r.DecisionType == DecisionTypes.ExceptionDisposition && r.DeciderRole == Roles.PlatformOwner);

    [Fact]
    public void No_conditions_still_get_a_review()
        => Assert.Single(TriggeredDisciplines.Evaluate(Array.Empty<string>(), Journeys.NewWorkload));
}

public class DelegationValidTests
{
    private static readonly DateTime Today = new(2026, 10, 24);
    private static Delegation D(decimal? max = 2500, string status = "ACTIVE", DateTime? to = null) => new()
    {
        DelegationId = 1, FundingOwnerId = 1, DelegatePersonId = 5, GrantedByPersonId = 4, MaxCreditsPerRequest = max,
        EffectiveFrom = Today.AddDays(-10), EffectiveTo = to, Status = status,
    };

    [Fact] public void Within_delegation_and_envelope_passes() => Assert.True(DelegationValid.Evaluate(D(), new[] { 3 }, ActiveEnvelope(), 800, Today).Passed);
    [Fact] public void No_delegation_fails() => Assert.False(DelegationValid.Evaluate(null, Array.Empty<int>(), ActiveEnvelope(), 800, Today).Passed);
    [Fact] public void Revoked_delegation_fails() => Assert.False(DelegationValid.Evaluate(D(status: "REVOKED"), Array.Empty<int>(), ActiveEnvelope(), 800, Today).Passed);
    [Fact] public void Expired_delegation_fails() => Assert.False(DelegationValid.Evaluate(D(to: Today.AddDays(-1)), Array.Empty<int>(), ActiveEnvelope(), 800, Today).Passed);
    [Fact] public void Above_delegated_limit_fails() => Assert.Contains("above the delegated limit", DelegationValid.Evaluate(D(500), Array.Empty<int>(), ActiveEnvelope(), 800, Today).Reasons[0]);
    [Fact] public void Envelope_not_covered_fails() => Assert.Contains("does not cover", DelegationValid.Evaluate(D(), new[] { 99 }, ActiveEnvelope(), 800, Today).Reasons[0]);
    [Fact] public void No_envelope_fails() => Assert.False(DelegationValid.Evaluate(D(), Array.Empty<int>(), null, 800, Today).Passed);
    [Fact] public void No_headroom_fails() => Assert.Contains("headroom", DelegationValid.Evaluate(D(), Array.Empty<int>(), ActiveEnvelope(1000, 900), 800, Today).Reasons[0]);
}

public class GateAndPreviewTests
{
    [Theory]
    [InlineData(LifecycleStages.Production, "LOW", SolutionClasses.Productivity, true)]
    [InlineData(LifecycleStages.Development, "HIGH", SolutionClasses.BusinessApplication, true)]
    [InlineData(LifecycleStages.Development, "CRITICAL", SolutionClasses.BusinessApplication, true)]
    [InlineData(LifecycleStages.Development, "HIGH", SolutionClasses.Productivity, false)]
    [InlineData(LifecycleStages.Test, "MEDIUM", SolutionClasses.BusinessApplication, false)]
    public void Production_gate(string stage, string criticality, string cls, bool expected)
        => Assert.Equal(expected, ProductionGateRequired.Evaluate(stage, criticality, cls));

    [Fact] public void Customer_facing_needs_adoption_decision() => Assert.True(PreviewAdoption.Required(Studio(), true));
    [Fact] public void Preview_service_needs_adoption_decision() => Assert.True(PreviewAdoption.Required(new Service { TenantAvailability = "PREVIEW" }, false));
    [Fact] public void Pilot_service_needs_adoption_decision() => Assert.True(PreviewAdoption.Required(new Service { TenantAvailability = "GA", InstitutionalStatus = "PILOT" }, false));
    [Fact] public void Approved_ga_internal_service_does_not() => Assert.False(PreviewAdoption.Required(Studio(), false));
}

public class VerificationCompleteTests
{
    private static VerificationComplete.CheckResult Ok(string code, bool deny = false, string method = "READBACK", bool evidence = true)
        => new(code, method, deny, "x", "x", true, evidence);

    [Fact] public void All_checks_with_deny_case_and_second_verifier_pass() => Assert.True(VerificationComplete.Evaluate(new[] { Ok("A"), Ok("DENY", true) }, 1, 2, false).Passed);
    [Fact] public void No_checks_fail() => Assert.False(VerificationComplete.Evaluate(Array.Empty<VerificationComplete.CheckResult>(), 1, 2, false).Passed);
    [Fact] public void Missing_deny_case_fails() => Assert.Contains(VerificationComplete.Evaluate(new[] { Ok("A") }, 1, 2, false).Reasons, r => r.Contains("deny"));

    [Fact]
    public void Failed_check_shows_expected_and_actual()
    {
        var r = VerificationComplete.Evaluate(new[] { new VerificationComplete.CheckResult("ALLOCATION", "READBACK", false, "5000", "6000", false, true), Ok("D", true) }, 1, 2, false);
        Assert.Contains(r.Reasons, x => x.Contains("expected 5000") && x.Contains("6000"));
    }

    [Fact] public void Attestation_needs_evidence() => Assert.Contains(VerificationComplete.Evaluate(new[] { Ok("A", method: "ATTESTATION", evidence: false), Ok("D", true) }, 1, 2, false).Reasons, r => r.Contains("evidence"));
    [Fact] public void Configurer_alone_cannot_verify_SoD3() => Assert.Contains(VerificationComplete.Evaluate(new[] { Ok("A"), Ok("D", true) }, 5, 5, false).Reasons, r => r.Contains("SoD3"));
    [Fact] public void Configurer_with_automated_readback_passes() => Assert.True(VerificationComplete.Evaluate(new[] { Ok("A"), Ok("D", true) }, 5, 5, true).Passed);
}
