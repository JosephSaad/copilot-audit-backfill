using Dapper;
using Governance.Application.Data;
using Governance.Application.Requests;
using Governance.Application.Workflow;
using Governance.Domain;
using Governance.Seed;

namespace Governance.IntegrationTests;

/// <summary>J9 end to end: a Copilot seat reservation change through the engine, as the personas would do it.</summary>
[Collection("sql")]
public sealed class SeatJourneyTests(SeededFixture fx) : IClassFixture<SeededFixture>
{
    private RequestFlows F => fx.Flows;
    private DateTime Today => fx.Host.Clock.UtcNow.Date;

    private void Reserve(string lob, int seats, int daysAgo = 60)
    {
        using var c = fx.Admin();
        c.Execute(
            """
            -- The seat demo seed gives some LOBs their own baseline; replace it with this test's.
            DELETE FROM core.SeatReservation WHERE FundingOwnerId = @fo AND RequestId IS NULL AND ISNULL(Notes, N'') <> N'Test baseline';
            IF NOT EXISTS (SELECT 1 FROM core.SeatReservation WHERE FundingOwnerId = @fo)
                INSERT core.SeatReservation (FundingOwnerId, Seats, ChangeKind, EffectiveFrom, Notes) VALUES (@fo, @seats, 'INITIAL', @from, N'Test baseline')
            """, new { fo = fx.Seed.FundingOwners[lob], seats, from = Today.AddDays(-daysAgo) });
    }

    private List<(int Seats, int? PreviousSeats, string ChangeKind, DateTime EffectiveFrom, DateTime? EffectiveTo, long? RequestId, long? DecisionId, int? ApprovedByPersonId, string? FlexPoolReview)> Rows(string lob)
    {
        using var c = fx.Admin();
        return c.Query<(int, int?, string, DateTime, DateTime?, long?, long?, int?, string?)>(
            "SELECT Seats, PreviousSeats, ChangeKind, EffectiveFrom, EffectiveTo, RequestId, DecisionId, ApprovedByPersonId, FlexPoolReview FROM core.SeatReservation WHERE FundingOwnerId = @fo ORDER BY EffectiveFrom, SeatReservationId",
            new { fo = fx.Seed.FundingOwners[lob] }).ToList();
    }

    private Task<RequestRow> SubmitAsync(string requester, string lob, string title, int requested)
        => F.SubmitAsync(requester, Journeys.SeatReservation, title, lob, null, null, null,
            new RequestPayload { RequestedSeats = requested, SeatDemand = "Claims and underwriting teams, from next month." });

    /// <summary>B2 complete, the Digital Workplace consult answered: the request waits for the Finance decision (S4).</summary>
    private async Task ToDecisionAsync(RequestRow r)
    {
        await F.CompleteAsync("intake", r.RequestId, Stages.Completeness, StageOutcomes.Complete);
        foreach (var consult in (await F.OpenTasksAsync(r.RequestId)).Where(t => t.TaskType == TaskTypes.Consult && t.IsBlocking))
            await F.CompleteTaskAsync("platformOwner", consult.TaskId, "CONSULTED", "Seats checked in the admin center.");
    }

    private DecisionInput Approve(string flex) => F.Approve("Copilot seats for the LOB.") with { FlexPoolReview = flex };

    [Fact]
    public async Task Increase_approved_writes_the_new_reservation_with_previous_quantity_and_approver()
    {
        Reserve("RETAIL", 100);
        var r = await SubmitAsync("RETAIL.finance", "RETAIL", "Retail seats up to 150", 150);
        var p = RequestPayload.FromJson(r.PayloadJson);
        Assert.Equal(100, p.CurrentSeats);
        Assert.Equal(SeatChangeKinds.Increase, p.SeatChangeKind);

        await F.CompleteAsync("intake", r.RequestId, Stages.Completeness, StageOutcomes.Complete);
        Assert.Equal(Stages.SeatDecision, (await F.GetAsync(r.RequestId)).CurrentStage);
        var waits = await Assert.ThrowsAsync<DomainException>(() => F.DecideAsync("finopsLead", r.RequestId, Stages.SeatDecision, Approve("x")));
        Assert.Contains("Blocking", waits.Message);
        foreach (var consult in (await F.OpenTasksAsync(r.RequestId)).Where(t => t.TaskType == TaskTypes.Consult && t.IsBlocking))
            await F.CompleteTaskAsync("platformOwner", consult.TaskId, "CONSULTED", "Seats checked.");

        var noFlex = await Assert.ThrowsAsync<DomainException>(() => F.DecideAsync("finopsLead", r.RequestId, Stages.SeatDecision, F.Approve("Seats.")));
        Assert.Contains("flex-pool review", noFlex.Message);
        var decisionId = await F.DecideAsync("finopsLead", r.RequestId, Stages.SeatDecision, Approve("120 unallocated, 25 buffer, no pending demand."));

        Assert.Equal(RequestStates.Completed, (await F.GetAsync(r.RequestId)).State);
        var rows = Rows("RETAIL");
        Assert.Equal(2, rows.Count);
        Assert.Equal(Today.AddDays(-1), rows[0].EffectiveTo);
        var now = rows[1];
        Assert.Equal((150, 100, SeatChangeKinds.Increase, Today), (now.Seats, now.PreviousSeats, now.ChangeKind, now.EffectiveFrom));
        Assert.Null(now.EffectiveTo);
        Assert.Equal(r.RequestId, now.RequestId);
        Assert.Equal(decisionId, now.DecisionId);
        Assert.Equal(fx.Seed.Person("finopsLead"), now.ApprovedByPersonId);
        Assert.Contains("120 unallocated", now.FlexPoolReview);
        using var c = fx.Admin();
        Assert.Equal(DecisionTypes.BudgetApproval, c.ExecuteScalar<string>("SELECT DecisionType FROM wf.Decision WHERE DecisionId = @decisionId", new { decisionId }));
        Assert.Equal("FUNDING_BUDGET", c.ExecuteScalar<string>("SELECT RaciActivityCode FROM wf.Decision WHERE DecisionId = @decisionId", new { decisionId }));
    }

    [Fact]
    public async Task True_down_outside_the_window_is_refused_then_held_without_changing_the_reservation()
    {
        using (var c = fx.Admin())
        {
            // Anniversary well away from today, so the 45-day window is closed.
            var far = Today.AddMonths(5);
            c.Execute("UPDATE ref.Setting SET SettingValue = @v WHERE SettingKey = 'Seats.EaAnniversary'", new { v = far.ToString("MM-dd") });
        }
        try
        {
            Reserve("WEALTH", 80);
            var r = await SubmitAsync("WEALTH.owner", "WEALTH", "Wealth seats down to 50", 50);
            Assert.Equal(SeatChangeKinds.TrueDown, RequestPayload.FromJson(r.PayloadJson).SeatChangeKind);
            await ToDecisionAsync(r);

            var ex = await Assert.ThrowsAsync<DomainException>(() => F.DecideAsync("finopsLead", r.RequestId, Stages.SeatDecision, Approve("Reviewed.")));
            Assert.Contains("Hold it until then", ex.Message);
            await F.DecideAsync("finopsLead", r.RequestId, Stages.SeatDecision,
                RequestFlows.Hold("Wealth true-down", "Decreases wait for the EA anniversary.", "Nothing: it is decided when the window opens."));

            var held = await F.GetAsync(r.RequestId);
            Assert.Equal(RequestStates.OnHold, held.State);
            Assert.Equal(Stages.SeatDecision, held.CurrentStage);
            var task = await F.TaskAsync(r.RequestId, Stages.SeatDecision, TaskTypes.Decision);
            Assert.True(task.DueUtc > Today.AddMonths(3), "A held decrease waits for the window, not for the 5-day SLA.");
            Assert.Single(await F.OpenTasksAsync(r.RequestId), t => t.TaskType == TaskTypes.Consult && t.IsBlocking);
            var rows = Rows("WEALTH");
            Assert.Single(rows);
            Assert.Equal(80, rows[0].Seats);
            Assert.Null(rows[0].EffectiveTo);
        }
        finally
        {
            using var c = fx.Admin();
            c.Execute("UPDATE ref.Setting SET SettingValue = '04-01' WHERE SettingKey = 'Seats.EaAnniversary'");
        }
    }

    [Fact]
    public async Task True_down_inside_the_window_takes_effect_at_the_anniversary()
    {
        var anniversary = Today.AddDays(20);
        using (var c = fx.Admin())
            c.Execute("UPDATE ref.Setting SET SettingValue = @v WHERE SettingKey = 'Seats.EaAnniversary'", new { v = anniversary.ToString("MM-dd") });
        try
        {
            Reserve("COMMERCIAL", 60);
            var r = await SubmitAsync("COMMERCIAL.finance", "COMMERCIAL", "Commercial seats down to 40", 40);
            await ToDecisionAsync(r);
            await F.DecideAsync("finopsLead", r.RequestId, Stages.SeatDecision, Approve("Twenty seats unused for 90 days; returned to the pool."));

            var rows = Rows("COMMERCIAL");
            Assert.Equal(2, rows.Count);
            Assert.Equal((60, anniversary.AddDays(-1)), (rows[0].Seats, rows[0].EffectiveTo));
            Assert.Equal((40, 60, SeatChangeKinds.TrueDown, anniversary), (rows[1].Seats, rows[1].PreviousSeats, rows[1].ChangeKind, rows[1].EffectiveFrom));
            // Until the anniversary the LOB keeps (and pays for) the full reservation (FIN-05).
            var current = await fx.Host.SystemAsync(u => Application.Seats.SeatRequests.CurrentSeatsAsync(u, fx.Seed.FundingOwners["COMMERCIAL"], Today));
            Assert.Equal(60, current);
            var after = await fx.Host.SystemAsync(u => Application.Seats.SeatRequests.CurrentSeatsAsync(u, fx.Seed.FundingOwners["COMMERCIAL"], anniversary));
            Assert.Equal(40, after);
        }
        finally
        {
            using var c = fx.Admin();
            c.Execute("UPDATE ref.Setting SET SettingValue = '04-01' WHERE SettingKey = 'Seats.EaAnniversary'");
        }
    }

    [Fact]
    public async Task A_requester_without_an_LOB_role_is_refused()
    {
        var ex = await Assert.ThrowsAsync<DomainException>(() => SubmitAsync("RETAIL.maker1", "RETAIL", "Maker asks for seats", 500));
        Assert.Contains("business owner", ex.Message);
        var other = await Assert.ThrowsAsync<DomainException>(() => SubmitAsync("WEALTH.finance", "RETAIL", "Wrong LOB asks for seats", 500));
        Assert.Contains("business owner", other.Message);
    }

    [Fact]
    public async Task The_requester_cannot_decide_their_own_seat_request()
    {
        using (var c = fx.Admin())
            c.Execute("INSERT sec.RoleAssignment (PersonId, RoleCode, ScopeType, ScopeId, EffectiveFrom, Source) VALUES (@p, 'FINOPS_LEAD', 'TENANT', NULL, @from, 'SEED')",
                new { p = fx.Seed.Person("RETAIL.owner"), from = Today.AddDays(-1) });
        try
        {
            var r = await SubmitAsync("RETAIL.owner", "RETAIL", "Owner asks for seats", 999);
            await ToDecisionAsync(r);
            var ex = await Assert.ThrowsAsync<DomainException>(() => F.DecideAsync("RETAIL.owner", r.RequestId, Stages.SeatDecision, Approve("Self-approval.")));
            Assert.Contains("SoD1", ex.Message);
        }
        finally
        {
            using var c = fx.Admin();
            c.Execute("UPDATE sec.RoleAssignment SET EffectiveTo = @y WHERE PersonId = @p AND RoleCode = 'FINOPS_LEAD' AND Source = 'SEED' AND ScopeType = 'TENANT'",
                new { p = fx.Seed.Person("RETAIL.owner"), y = Today.AddDays(-1) });
        }
    }
}
