using Dapper;
using Governance.Application.Data;
using Governance.Application.Requests;
using Governance.Application.Workflow;
using Governance.Domain;
using Governance.Seed;

namespace Governance.IntegrationTests;

/// <summary>The workflow engine end to end on a seeded database, as the personas would use it.</summary>
[Collection("sql")]
public sealed class EngineTests(SeededFixture fx) : IClassFixture<SeededFixture>
{
    private RequestFlows F => fx.Flows;
    private DateTime Today => fx.Host.Clock.UtcNow.Date;

    private Task<RequestRow> SubmitMakerAsync(string requester, string lob, string title, decimal estimate = 800, string stage = LifecycleStages.Development,
        Action<RequestPayload>? change = null)
    {
        var p = SampleRequests.MakerAgent(title, stage, estimate);
        change?.Invoke(p);
        return F.SubmitAsync(requester, Journeys.NewWorkload, title, lob, "CS_STANDARD", $"{lob}.sponsor", null, p);
    }

    /// <summary>B2 complete and B3 classified: the request reaches B5 (fits) or B8 (no pattern).</summary>
    private async Task ToPatternCheckAsync(RequestRow r, string costCenter, string stage = LifecycleStages.Development, string criticality = "LOW",
        string cls = SolutionClasses.Productivity)
    {
        await F.CompleteAsync("intake", r.RequestId, Stages.Completeness, StageOutcomes.Complete);
        await F.ClassifyAsync(r.RequestId, costCenter, cls, stage, criticality, "Internal", "My team");
        await F.CompleteAsync("architect", r.RequestId, Stages.Classify, StageOutcomes.Classified);
    }

    private void Grant(string personaKey, string role, string? lob)
    {
        using var c = fx.Admin();
        c.Execute("INSERT sec.RoleAssignment (PersonId, RoleCode, ScopeType, ScopeId, EffectiveFrom, Source) VALUES (@p, @role, @st, @sid, @from, 'SEED')",
            new { p = fx.Seed.Person(personaKey), role, st = lob is null ? "TENANT" : "FUNDING_OWNER", sid = lob is null ? (int?)null : fx.Seed.FundingOwners[lob], from = Today.AddDays(-1) });
    }

    private void Revoke(string personaKey, string role)
    {
        using var c = fx.Admin();
        c.Execute("UPDATE sec.RoleAssignment SET EffectiveTo = @y WHERE PersonId = @p AND RoleCode = @role AND Source = 'SEED' AND EffectiveFrom = @from",
            new { p = fx.Seed.Person(personaKey), role, y = Today.AddDays(-1), from = Today.AddDays(-1) });
    }

    // --------------------------------------------------------------------------------------------

    [Fact]
    public async Task New_workload_happy_path_ends_completed_in_development()
    {
        var r = await SubmitMakerAsync("RETAIL.maker1", "RETAIL", "E2E happy path");
        await ToPatternCheckAsync(r, "RB-1100");
        Assert.Equal(Stages.RoutineApproval, (await F.GetAsync(r.RequestId)).CurrentStage);
        await F.DecideAsync("RETAIL.approver1", r.RequestId, Stages.RoutineApproval, F.Approve("Team, Developer tier, 800 credits a month."));
        await F.CompleteAsync("intake", r.RequestId, Stages.PrepareFulfillment, StageOutcomes.Ready);
        Assert.Equal(Stages.Fulfillment, (await F.GetAsync(r.RequestId)).CurrentStage);
        await F.CloseTicketAsync(r.RequestId, "operator1");
        Assert.Equal(RequestStates.Verification, (await F.GetAsync(r.RequestId)).State);
        await F.CompleteAsync("operator2", r.RequestId, Stages.Verify, StageOutcomes.Pass);

        var done = await F.GetAsync(r.RequestId);
        Assert.Equal(RequestStates.Completed, done.State);
        Assert.NotNull(done.ClosedUtc);
        using var c = fx.Admin();
        Assert.Equal(WorkloadStates.Development, c.ExecuteScalar<string>("SELECT State FROM core.Workload WHERE WorkloadId = @WorkloadId", new { done.WorkloadId }));
        Assert.Equal(800m, c.ExecuteScalar<decimal>("SELECT Amount FROM core.EnvelopeCommitment WHERE RequestId = @RequestId AND Status = 'ACTIVE'", new { done.RequestId }));
        var decision = c.QuerySingle<(string DeciderRoleCode, int? DelegationId, string Source)>("SELECT DeciderRoleCode, DelegationId, Source FROM wf.Decision WHERE RequestId = @RequestId", new { done.RequestId });
        Assert.Equal(Roles.DelegatedApprover, decision.DeciderRoleCode);
        Assert.NotNull(decision.DelegationId);
        Assert.Equal("WORKBENCH", decision.Source);
        Assert.True(c.ExecuteScalar<int>("SELECT COUNT(*) FROM ntf.Outbox WHERE EventType IN ('N01','N04','N06','N07','ITSM.Create') AND Payload LIKE '%\"requestId\":' + CAST(@RequestId AS varchar(20)) + '%'", new { done.RequestId }) >= 5);
    }

    [Fact]
    public async Task SoD1_the_requester_cannot_decide_their_own_request()
    {
        var r = await SubmitMakerAsync("COMMERCIAL.approver1", "COMMERCIAL", "SoD1 test");
        await ToPatternCheckAsync(r, "CB-3000");
        var ex = await Assert.ThrowsAsync<DomainException>(() => F.DecideAsync("COMMERCIAL.approver1", r.RequestId, Stages.RoutineApproval, F.Approve("x")));
        Assert.Contains("SoD1", ex.Message);
        await F.DecideAsync("COMMERCIAL.approver2", r.RequestId, Stages.RoutineApproval, F.Approve("x"));
    }

    [Fact]
    public async Task SoD2_the_approver_cannot_verify_and_SoD3_the_configurer_cannot_verify()
    {
        Grant("RETAIL.approver2", Roles.PlatformOperator, null);
        try
        {
            var r = await SubmitMakerAsync("RETAIL.maker2", "RETAIL", "SoD2 test", estimate: 500);
            await ToPatternCheckAsync(r, "RB-1000");
            await F.DecideAsync("RETAIL.approver2", r.RequestId, Stages.RoutineApproval, F.Approve("x"));
            await F.CompleteAsync("intake", r.RequestId, Stages.PrepareFulfillment, StageOutcomes.Ready);
            await F.CloseTicketAsync(r.RequestId, "operator1");
            var sod2 = await Assert.ThrowsAsync<DomainException>(() => F.CompleteAsync("RETAIL.approver2", r.RequestId, Stages.Verify, StageOutcomes.Pass));
            Assert.Contains("SoD2", sod2.Message);
            var sod3 = await Assert.ThrowsAsync<DomainException>(() => F.CompleteAsync("operator1", r.RequestId, Stages.Verify, StageOutcomes.Pass));
            Assert.Contains("SoD3", sod3.Message);
            await F.CompleteAsync("operator2", r.RequestId, Stages.Verify, StageOutcomes.Pass);
        }
        finally { Revoke("RETAIL.approver2", Roles.PlatformOperator); }
    }

    [Fact]
    public async Task SoD4_one_person_cannot_record_overage_and_risk_acceptance_on_one_request()
    {
        Grant("COMMERCIAL.owner", Roles.RiskReviewer, null);
        try
        {
            var r = await SubmitMakerAsync("COMMERCIAL.maker2", "COMMERCIAL", "SoD4 test", estimate: 1500, change: p => { p.NewConnectorOrIntegration = true; p.NewBillingScope = true; });
            await ToPatternCheckAsync(r, "CB-3000");
            Assert.Equal(Stages.SpecialistReview, (await F.GetAsync(r.RequestId)).CurrentStage);
            foreach (var t in await F.OpenTasksAsync(r.RequestId))
                await F.CompleteAsync(t.Discipline switch { Disciplines.SecurityRisk => "risk", Disciplines.Finance => "finopsAnalyst", _ => "architect" },
                    r.RequestId, Stages.SpecialistReview, "REVIEWED", discipline: t.Discipline);
            await F.DecideAsync("COMMERCIAL.owner", r.RequestId, Stages.Disposition, F.Approve("Overage within the year."), DecisionTypes.OverageAuthorization);
            var ex = await Assert.ThrowsAsync<DomainException>(() =>
                F.DecideAsync("COMMERCIAL.owner", r.RequestId, Stages.Disposition, F.Approve("Risk accepted."), DecisionTypes.RiskAcceptance));
            Assert.Contains("SoD4", ex.Message);
        }
        finally { Revoke("COMMERCIAL.owner", Roles.RiskReviewer); }
    }

    [Fact]
    public async Task SoD5_an_administrator_cannot_decide()
    {
        Grant("admin", Roles.LobBusinessOwner, "WEALTH");
        try
        {
            var r = await SubmitMakerAsync("WEALTH.maker1", "WEALTH", "SoD5 test", estimate: 300);
            await ToPatternCheckAsync(r, "WM-2100");
            var ex = await Assert.ThrowsAsync<DomainException>(() => F.DecideAsync("admin", r.RequestId, Stages.RoutineApproval, F.Approve("x")));
            Assert.Contains("SoD5", ex.Message);
        }
        finally { Revoke("admin", Roles.LobBusinessOwner); }
    }

    [Fact]
    public async Task SoD7_a_delegate_cannot_approve_above_their_delegated_limit()
    {
        var r = await SubmitMakerAsync("RETAIL.maker1", "RETAIL", "SoD7 test", estimate: 1800);
        await ToPatternCheckAsync(r, "RB-1100");
        var ex = await Assert.ThrowsAsync<DomainException>(() => F.DecideAsync("RETAIL.approver2", r.RequestId, Stages.RoutineApproval, F.Approve("x")));
        Assert.Contains("above the delegated limit", ex.Message);
        await F.DecideAsync("RETAIL.approver1", r.RequestId, Stages.RoutineApproval, F.Approve("x"));   // 2,500 limit
        using var c = fx.Admin();
        Assert.Equal(fx.Seed.Person("RETAIL.approver1"), c.ExecuteScalar<int>("SELECT DeciderPersonId FROM wf.Decision WHERE RequestId = @RequestId", new { r.RequestId }));
    }

    [Fact]
    public async Task A_decline_needs_the_reason_and_the_evidence_needed()
    {
        var r = await SubmitMakerAsync("WEALTH.maker2", "WEALTH", "Decline test");
        await ToPatternCheckAsync(r, "WM-2000");
        await Assert.ThrowsAsync<DomainException>(() => F.DecideAsync("WEALTH.owner", r.RequestId, Stages.RoutineApproval, RequestFlows.Decline("x", "", "")));
        await Assert.ThrowsAsync<DomainException>(() => F.DecideAsync("WEALTH.owner", r.RequestId, Stages.RoutineApproval, RequestFlows.Decline("x", "Not funded.", " ")));
        await F.DecideAsync("WEALTH.owner", r.RequestId, Stages.RoutineApproval, RequestFlows.Decline("x", "No business outcome.", "A measurable outcome and a sponsor."));
        var after = await F.GetAsync(r.RequestId);
        Assert.Equal(RequestStates.Declined, after.State);
        Assert.Equal(Stages.Remediate, after.CurrentStage);   // no dead end: the requester can remediate (B7)
    }

    [Fact]
    public async Task An_approval_needs_an_expiry_or_a_review_date()
    {
        var r = await SubmitMakerAsync("WEALTH.maker2", "WEALTH", "Expiry test");
        await ToPatternCheckAsync(r, "WM-2000");
        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            F.DecideAsync("WEALTH.owner", r.RequestId, Stages.RoutineApproval, new DecisionInput(Outcomes.Approved, "x", null, null, null, null, null)));
        Assert.Contains("expires", ex.Message);
    }

    [Fact]
    public async Task An_approved_exception_enters_the_register()
    {
        var p = SampleRequests.ExceptionPayload(false);
        p.RequestedExpiry = Today.AddMonths(6);
        var r = await F.SubmitAsync("RETAIL.product", Journeys.Exception, "E2E exception", "RETAIL", "CS_STANDARD", "RETAIL.sponsor", null, p);
        await F.CompleteAsync("intake", r.RequestId, Stages.Completeness, StageOutcomes.Complete);
        foreach (var t in await F.OpenTasksAsync(r.RequestId))
            await F.CompleteAsync(t.Discipline == Disciplines.SecurityRisk ? "risk" : "architect", r.RequestId, Stages.SpecialistReview, "REVIEWED", discipline: t.Discipline);
        foreach (var t in await F.OpenTasksAsync(r.RequestId))
            await F.DecideAsync(t.AssignedRoleCode == Roles.RiskReviewer ? "risk" : "platformOwner", r.RequestId, Stages.Disposition,
                F.Approve("Connector, read-only.", t.AssignedRoleCode == Roles.RiskReviewer ? null : "Weekly transcript review.", 3, Today.AddMonths(6)), t.DecisionType);
        var done = await F.GetAsync(r.RequestId);
        Assert.Equal(RequestStates.Completed, done.State);
        using var c = fx.Admin();
        var item = c.QuerySingle<(string Status, DateTime ExpiresOn, string CompensatingControls, int OwnerPersonId)>(
            "SELECT Status, ExpiresOn, CompensatingControls, OwnerPersonId FROM wf.ExceptionItem WHERE RequestId = @RequestId", new { r.RequestId });
        Assert.Equal("ACTIVE", item.Status);
        Assert.Equal(Today.AddMonths(6), item.ExpiresOn);
        Assert.False(string.IsNullOrWhiteSpace(item.CompensatingControls));
        Assert.Equal(fx.Seed.Person("RETAIL.sponsor"), item.OwnerPersonId);
    }

    [Fact(Skip = "core bug: JourneyEffects.ApplyAsync takes compensating controls from the latest positive B9 decision, so the conditions the Platform Owner set are lost when another decision (risk acceptance) is recorded after it.")]
    public async Task The_exception_register_keeps_the_conditions_of_the_disposition()
    {
        var p = SampleRequests.ExceptionPayload(false);
        p.RequestedExpiry = Today.AddMonths(6);
        var r = await F.SubmitAsync("RETAIL.product", Journeys.Exception, "E2E exception conditions", "RETAIL", "CS_STANDARD", "RETAIL.sponsor", null, p);
        await F.CompleteAsync("intake", r.RequestId, Stages.Completeness, StageOutcomes.Complete);
        foreach (var t in await F.OpenTasksAsync(r.RequestId))
            await F.CompleteAsync(t.Discipline == Disciplines.SecurityRisk ? "risk" : "architect", r.RequestId, Stages.SpecialistReview, "REVIEWED", discipline: t.Discipline);
        foreach (var t in await F.OpenTasksAsync(r.RequestId))   // the Platform Owner decides first, with conditions; risk second, without
            await F.DecideAsync(t.AssignedRoleCode == Roles.RiskReviewer ? "risk" : "platformOwner", r.RequestId, Stages.Disposition,
                F.Approve("Connector, read-only.", t.AssignedRoleCode == Roles.RiskReviewer ? null : "Weekly transcript review.", 3, Today.AddMonths(6)), t.DecisionType);
        using var c = fx.Admin();
        Assert.Contains("Weekly transcript review", c.ExecuteScalar<string>("SELECT CompensatingControls FROM wf.ExceptionItem WHERE RequestId = @RequestId", new { r.RequestId }));
    }

    [Fact]
    public async Task A_verified_capacity_change_versions_the_approved_posture()
    {
        var wid = fx.Seed.Workloads["retailDev"];
        var p = new RequestPayload { Purpose = "More allocation.", ChangeKind = "ALLOCATION", EnvironmentId = "9f1c2d4e-0000-4000-8000-00000000a001", CurrentValue = 5000, RequestedValue = 6000 };
        var r = await F.SubmitAsync("RETAIL.product", Journeys.CapacityChange, "E2E allocation change", "RETAIL", null, "RETAIL.sponsor", wid, p);
        await F.CompleteAsync("intake", r.RequestId, Stages.Completeness, StageOutcomes.Complete);
        Assert.Equal(Stages.RoutineApproval, (await F.GetAsync(r.RequestId)).CurrentStage);   // within envelope and class: routine
        await F.DecideAsync("RETAIL.approver1", r.RequestId, Stages.RoutineApproval, F.Approve("Allocation 6,000 credits."));
        await F.CompleteAsync("intake", r.RequestId, Stages.PrepareFulfillment, StageOutcomes.Ready);
        await F.CloseTicketAsync(r.RequestId, "operator1");
        await F.CompleteAsync("operator2", r.RequestId, Stages.Verify, StageOutcomes.Pass);
        Assert.Equal(RequestStates.Completed, (await F.GetAsync(r.RequestId)).State);
        using var c = fx.Admin();
        var rows = c.Query<(decimal AllocationCredits, DateTime? EffectiveTo, long? DecisionId)>(
            "SELECT AllocationCredits, EffectiveTo, DecisionId FROM core.ApprovedPosture WHERE WorkloadId = @wid ORDER BY PostureId", new { wid }).ToList();
        Assert.Equal(2, rows.Count);
        Assert.Equal(5000m, rows[0].AllocationCredits);
        Assert.NotNull(rows[0].EffectiveTo);
        Assert.Equal(6000m, rows[1].AllocationCredits);
        Assert.Null(rows[1].EffectiveTo);
        Assert.NotNull(rows[1].DecisionId);
    }

    [Fact]
    public async Task Retirement_retires_the_workload_and_releases_its_commitments()
    {
        var wid = fx.Seed.Workloads["wealthTest"];
        using (var c = fx.Admin())
            c.Execute("INSERT core.EnvelopeCommitment (EnvelopeId, FundingOwnerId, WorkloadId, Amount, Unit, Status) VALUES (@env, @fo, @wid, 400, 'COPILOT_CREDIT', 'ACTIVE')",
                new { env = fx.Seed.Envelopes[("WEALTH", "CS_STANDARD")], fo = fx.Seed.FundingOwners["WEALTH"], wid });
        var p = new RequestPayload { Purpose = "Replaced.", RetirementReason = "Replaced", RetirementPlan = "Notify testers; release allocation." };
        var r = await F.SubmitAsync("WEALTH.product", Journeys.Retirement, "E2E retirement", "WEALTH", null, "WEALTH.sponsor", wid, p);
        await F.CompleteAsync("intake", r.RequestId, Stages.Completeness, StageOutcomes.Complete);
        Assert.Contains(await F.OpenTasksAsync(r.RequestId), t => t.TaskType == TaskTypes.Consult && t.AssignedRoleCode == Roles.SourcingAgent && !t.IsBlocking);
        await F.DecideAsync("WEALTH.owner", r.RequestId, Stages.RetirementDecision, F.Approve("Retire the advisory test environment."));
        using (var c = fx.Admin()) Assert.Equal(WorkloadStates.Retiring, c.ExecuteScalar<string>("SELECT State FROM core.Workload WHERE WorkloadId = @wid", new { wid }));
        await F.CompleteAsync("intake", r.RequestId, Stages.PrepareFulfillment, StageOutcomes.Ready);
        await F.CloseTicketAsync(r.RequestId, "operator1");
        await F.CompleteAsync("operator2", r.RequestId, Stages.Verify, StageOutcomes.Pass);
        await F.CompleteAsync("finopsAnalyst", r.RequestId, Stages.CloseCommitments, StageOutcomes.Closed);
        Assert.Equal(RequestStates.Completed, (await F.GetAsync(r.RequestId)).State);
        using var c2 = fx.Admin();
        Assert.Equal(WorkloadStates.Retired, c2.ExecuteScalar<string>("SELECT State FROM core.Workload WHERE WorkloadId = @wid", new { wid }));
        Assert.Equal(0, c2.ExecuteScalar<int>("SELECT COUNT(*) FROM core.EnvelopeCommitment WHERE WorkloadId = @wid AND Status = 'ACTIVE'", new { wid }));
        Assert.Equal(0, c2.ExecuteScalar<int>("SELECT COUNT(*) FROM core.WorkloadBinding WHERE WorkloadId = @wid AND EffectiveTo IS NULL", new { wid }));
        Assert.Equal(0, c2.ExecuteScalar<int>("SELECT COUNT(*) FROM core.ApprovedPosture WHERE WorkloadId = @wid AND EffectiveTo IS NULL", new { wid }));
    }

    [Fact]
    public async Task Production_acceptance_waits_for_blocking_consults()
    {
        var r = await SubmitMakerAsync("WEALTH.maker1", "WEALTH", "E2E production", 1500, LifecycleStages.Production, p => p.Audience = Audiences.Lob);
        await ToPatternCheckAsync(r, "WM-2000", LifecycleStages.Production, "HIGH", SolutionClasses.BusinessApplication);
        foreach (var t in await F.OpenTasksAsync(r.RequestId))
            await F.CompleteAsync(t.Discipline == Disciplines.Operations ? "operator1" : "architect", r.RequestId, Stages.SpecialistReview, "REVIEWED", discipline: t.Discipline);
        foreach (var t in await F.OpenTasksAsync(r.RequestId))
            await F.DecideAsync(t.AssignedRoleCode == Roles.PlatformOwner ? "platformOwner" : "WEALTH.owner", r.RequestId, Stages.Disposition, F.Approve("Production, LOB."), t.DecisionType);
        await F.CompleteAsync("intake", r.RequestId, Stages.PrepareFulfillment, StageOutcomes.Ready);
        await F.CloseTicketAsync(r.RequestId, "operator1");
        await F.CompleteAsync("operator2", r.RequestId, Stages.Verify, StageOutcomes.Pass);
        Assert.Equal(Stages.BuildAndTest, (await F.GetAsync(r.RequestId)).CurrentStage);   // PASS became PASS_GATE
        await F.CompleteAsync("WEALTH.product", r.RequestId, Stages.BuildAndTest, StageOutcomes.Ready);

        var ex = await Assert.ThrowsAsync<DomainException>(() => F.DecideAsync("WEALTH.owner", r.RequestId, Stages.ProductionAcceptance, F.Approve("Accept.")));
        Assert.Contains("Blocking", ex.Message);
        foreach (var consult in (await F.OpenTasksAsync(r.RequestId)).Where(t => t.TaskType == TaskTypes.Consult))
            await F.CompleteTaskAsync(consult.AssignedRoleCode == Roles.FinOpsAnalyst ? "finopsAnalyst" : "operator1", consult.TaskId, "ANSWERED", "Confirmed.");
        await F.DecideAsync("WEALTH.owner", r.RequestId, Stages.ProductionAcceptance, F.Approve("Accept into production."));

        var done = await F.GetAsync(r.RequestId);
        Assert.Equal(RequestStates.Completed, done.State);
        using var c = fx.Admin();
        Assert.Equal(WorkloadStates.Production, c.ExecuteScalar<string>("SELECT State FROM core.Workload WHERE WorkloadId = @WorkloadId", new { done.WorkloadId }));
    }

    [Fact]
    public async Task Submit_is_refused_while_intake_is_closed()
    {
        using (var c = fx.Admin()) c.Execute("UPDATE ref.Setting SET SettingValue = 'false' WHERE SettingKey = 'Intake.Open'");
        try
        {
            var draft = await F.DraftAsync("RETAIL.maker1", Journeys.NewWorkload, "Intake closed", "RETAIL", "CS_STANDARD", "RETAIL.sponsor", null, SampleRequests.MakerAgent("x"));
            var ex = await Assert.ThrowsAsync<DomainException>(() => fx.Host.AsAsync(fx.Seed.As("RETAIL.maker1"), u => fx.Host.Engine.SubmitAsync(u, fx.Seed.As("RETAIL.maker1"), draft.RequestId)));
            Assert.Contains("Intake is not open", ex.Message);
            Assert.Equal(RequestStates.Draft, (await F.GetAsync(draft.RequestId)).State);
        }
        finally { using var c = fx.Admin(); c.Execute("UPDATE ref.Setting SET SettingValue = 'true' WHERE SettingKey = 'Intake.Open'"); }
    }

    [Fact]
    public async Task Returned_request_is_resubmitted_by_the_requester_only()
    {
        var r = await SubmitMakerAsync("COMMERCIAL.maker1", "COMMERCIAL", "Return test");
        await F.CompleteAsync("intake", r.RequestId, Stages.Completeness, StageOutcomes.Return, "State the outcome.");
        Assert.Equal(RequestStates.Returned, (await F.GetAsync(r.RequestId)).State);
        await Assert.ThrowsAsync<DomainException>(() => F.CompleteAsync("COMMERCIAL.maker2", r.RequestId, Stages.Remediate, StageOutcomes.Resubmit));
        await F.CompleteAsync("COMMERCIAL.maker1", r.RequestId, Stages.Remediate, StageOutcomes.Resubmit);
        var again = await F.GetAsync(r.RequestId);
        Assert.Equal(RequestStates.Submitted, again.State);
        Assert.Equal(Stages.Completeness, again.CurrentStage);
    }

    [Fact(Skip = "core bug: 200-seed-reference.sql sends J7 B2 COMPLETE to I4 with ToState CLASSIFICATION, so a retirement waiting for its decision shows as \"Classification\".")]
    public async Task A_retirement_waiting_for_its_decision_is_not_shown_as_classification()
    {
        var p = new RequestPayload { Purpose = "x", RetirementReason = "x", RetirementPlan = "x" };
        var r = await F.SubmitAsync("COMMERCIAL.product", Journeys.Retirement, "State label", "COMMERCIAL", null, "COMMERCIAL.sponsor", null, p);
        await F.CompleteAsync("intake", r.RequestId, Stages.Completeness, StageOutcomes.Complete);
        Assert.NotEqual(RequestStates.Classification, (await F.GetAsync(r.RequestId)).State);
    }

    [Fact(Skip = "core bug: J8 stage F4 always records PURCHASE_APPROVAL under P3_PURCHASE; a renewal (CommercialKind RENEWAL) should be RENEWAL_APPROVAL under P3_RENEWAL (19-row matrix).")]
    public async Task A_renewal_is_recorded_as_a_renewal_approval()
    {
        var p = new RequestPayload { Purpose = "Renew.", CommercialKind = "RENEWAL", Quantity = 1000, Unit = Units.P3CommitUnit, Rationale = "x" };
        var r = await F.SubmitAsync("finopsLead", Journeys.Commercial, "Renewal type", "ES-IT", null, null, null, p);
        await F.CompleteAsync("finopsAnalyst", r.RequestId, Stages.FinOpsRecommendation, StageOutcomes.Ready);
        var t = await F.TaskAsync(r.RequestId, Stages.CommercialDecision, TaskTypes.Decision);
        Assert.Equal(DecisionTypes.RenewalApproval, t.DecisionType);
        Assert.Equal("P3_RENEWAL", t.RaciActivityCode);
    }
}
