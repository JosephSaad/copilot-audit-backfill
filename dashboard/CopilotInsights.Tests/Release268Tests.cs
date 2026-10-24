using System.Text.RegularExpressions;
using CopilotInsights.Data;
using CopilotInsights.Infrastructure;
using CopilotInsights.Pages;
using CopilotInsights.Tests.Review2412A;
using Xunit;

namespace CopilotInsights.Tests;

/// <summary>Copilot reservations: the policy's arithmetic, its examples, and where the figures come from.</summary>
public class ReservationRulesTests
{
    [Theory]
    /* The policy's core billing examples: reserved, allocated, overage, billable. */
    [InlineData(100, 80, 0, 100)]
    [InlineData(100, 100, 0, 100)]
    [InlineData(100, 110, 10, 110)]
    [InlineData(0, 25, 25, 25)]
    public void Billable_is_the_reservation_plus_any_allocation_over_it(int reserved, int allocated, int over, int billable)
    {
        Assert.Equal(over, ReservationRules.Over(reserved, allocated));
        Assert.Equal(billable, ReservationRules.Billable(reserved, allocated));
        Assert.Equal(Math.Max(reserved - allocated, 0), ReservationRules.Unused(reserved, allocated));
    }

    [Fact]
    public void Organizations_are_summed_one_by_one_so_one_under_never_offsets_one_over()
    {
        /* 20 unused in one, 10 over in another: 20 unused and 10 over, not 10 unused. */
        var a = new ReservationOrg("A", "A", null, null, null, 100, 80, 0, false, false);
        var b = new ReservationOrg("B", "B", null, null, null, 100, 110, 0, false, false);
        var v = new ReservationView(new DateTime(2026, 10, 19), null, [a, b], 0, 0, 18m, "USD");
        Assert.Equal(20, v.Unused);
        Assert.Equal(10, v.Over);
        Assert.Equal(210, v.Billable);
        Assert.Equal(3780m, v.Monthly);   // the Finance page's overlap example: 210 x 18
    }

    [Fact]
    public void Utilization_status_and_the_months_left()
    {
        Assert.Null(ReservationRules.Utilization(0, 25));
        Assert.Equal(0.8, ReservationRules.Utilization(100, 80));
        Assert.Equal(ReservationStatus.WithinReservation, ReservationRules.Status(100, 80, false));
        Assert.Equal(ReservationStatus.AtReservation, ReservationRules.Status(100, 100, false));
        Assert.Equal(ReservationStatus.OverReservation, ReservationRules.Status(100, 110, false));
        Assert.Equal(ReservationStatus.NoReservation, ReservationRules.Status(0, 25, false));
        Assert.Equal(ReservationStatus.DataException, ReservationRules.Status(100, 80, true));
        /* No bare Over, Under, Ratio, Cost or Variance. */
        foreach (var s in Enum.GetValues<ReservationStatus>()) Assert.Contains(' ', ReservationRules.Label(s));
        Assert.Equal(2, ReservationRules.MonthsLeft(new DateTime(2026, 10, 19)));
        Assert.Equal(0, ReservationRules.MonthsLeft(new DateTime(2026, 12, 5)));
        Assert.Null(ReservationRules.Monthly(100, null));   // no rate: Pending Finance Decision, never a figure
        Assert.Equal(3600m, ReservationRules.Remaining(100, 18m, new DateTime(2026, 10, 19)));
    }

    [Fact]
    public void The_purchase_gate_counts_as_the_policy_does()
    {
        /* 95% of the 1,136 unassigned: 1,079 to assign, a buffer of 57. */
        var g = ReservationRules.Gate(1136, 1136 - 1000, 95);
        Assert.Equal(1000, g.Assigned);
        Assert.Equal(57, g.Buffer);
        Assert.False(g.Reached);
        Assert.True(ReservationRules.Gate(1136, 57, 95).Reached);
        Assert.False(ReservationRules.Gate(0, 0, 95).Reached);
    }
}

public class ReservationSourceTests
{
    static string Sql() => File.ReadAllText(Path.Combine(Root(), "sql", "Create-CopilotOperationsSchema.sql"));
    static string Root()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(d.FullName, "CHANGELOG.md"))) d = d.Parent!;
        return d.FullName;
    }

    [Fact]
    public void A_person_counts_with_the_nearer_listed_leader_once()
    {
        var sql = Sql();
        Assert.Contains("CREATE OR ALTER FUNCTION dash.fnReservationMember (@AsOf DATE)", sql);
        Assert.Contains("ROW_NUMBER() OVER (PARTITION BY c.DescendantUpn ORDER BY c.Depth, r.ReservationId)", sql);
        Assert.Contains("WHERE m.n = 1", sql);
    }

    [Fact]
    public void A_snapshot_is_kept_once_and_the_nightly_run_takes_it()
    {
        var sql = Sql();
        Assert.Contains("IF EXISTS (SELECT 1 FROM dash.ReservationSnapshot WHERE SnapshotDate = @AsOf) RETURN;", sql);
        Assert.Contains("CONSTRAINT PK_DashReservationSnapshot PRIMARY KEY (SnapshotDate, OrgId)", sql);
        /* Only the Copilot add-on: Copilot held only inside a suite (E7) is not an allocation. */
        Assert.Contains("NOT EXISTS (SELECT 1 FROM dash.CopilotSuiteOnly AS so WHERE so.Upn = u.UserPrincipalName)", sql);
        var pipeline = File.ReadAllText(Path.Combine(Root(), "Invoke-CopilotPipeline.ps1"));
        Assert.Contains("if (-not $once -and $refused.Count -eq 0) { Invoke-ReservationSnapshot -Setting $T -Log $Log }", pipeline);   // Not after a history load; Nor after a refused load
        /* The app may edit the register, the rate and the Technology pool, never the snapshots. */
        Assert.Contains("GRANT INSERT, UPDATE, DELETE ON dash.Reservation TO CopilotInsightsApp;", sql);
        Assert.DoesNotContain("ON dash.ReservationSnapshot TO CopilotInsightsApp", sql);
    }

    [Fact]
    public void The_figures_are_for_everyone_and_the_people_for_the_Licensing_role()
    {
        var leadership = Nav.Groups.First(g => g.Persona == "Leadership");
        Assert.Contains(leadership.Pages, p => p.Path == "/leadership/reservations" && p.Role is null);
        Assert.Contains(Nav.Groups.First(g => g.Persona == "Licensing").Pages, p => p.Path == "/licensing/reservation-holders" && p.Role == Roles.Licensing);
        Assert.NotNull(Guidance.For("/leadership/reservations"));
        Assert.NotNull(Guidance.For("/licensing/reservation-holders"));
    }

    [Fact]
    public void No_subquery_sits_inside_an_aggregate()
    {
        /* SQL Server refuses it (error 130): Licensing answered 500 in 2.6.7 until it was a join. */
        foreach (var file in new[] { Source.Read("Data", "ReservationData.cs"), Source.Read("Pages", "Licensing", "ReservationHolders.cs"), Source.Read("Pages", "Leadership", "Reservations.cs") })
            Assert.DoesNotMatch(new Regex(@"(COUNT|SUM|MAX|MIN)\((CASE WHEN[^)]*(EXISTS|SELECT)|\(SELECT)", RegexOptions.IgnoreCase), file);
    }
}
