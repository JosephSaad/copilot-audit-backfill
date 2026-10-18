using CopilotInsights.Data;
using CopilotInsights.Pages;
using CopilotInsights.Tests.Review2412A;
using Xunit;

namespace CopilotInsights.Tests;

/// <summary>The rules behind Leadership › Costs and savings (2.6.6): each finds what it should and nothing it should not.</summary>
public class CostRulesTests
{
    static FabricCapacity Cap(string name = "cap", string sku = "F32", int cu = 32, string billing = "PayAsYouGo", decimal cost = 4147, double avg = 15,
        double peak = 36, double weekday = 18, double weekend = 1, double hours = 24, int throttled = 0, double copilot = 0) =>
        new(name, sku, cu, billing, 30, cost, avg, peak, weekday, weekend, hours, throttled, copilot);

    [Fact]
    public void A_month_is_any_number_of_days_brought_to_thirty()
    {
        Assert.Equal(300m, CostRules.Monthly(150, 15));
        Assert.Equal(100m, CostRules.Monthly(100, 30));
        Assert.Equal(0m, CostRules.Monthly(100, 0));
    }

    [Fact]
    public void An_idle_capacity_is_paused_or_removed_and_nothing_else_is_suggested_for_it()
    {
        var o = Assert.Single(CostRules.Fabric(Cap(cost: 1140, avg: 0.2, peak: 6), 0.41m, "USD"));
        Assert.Equal(OpportunityKind.Save, o.Kind);
        Assert.Equal(1140m, o.Saving);
        Assert.Equal("High", o.Confidence);
        /* On a reservation the money is spent until renewal: at stake, not saved this month. */
        var r = Assert.Single(CostRules.Fabric(Cap(billing: "Reserved", cost: 1140, avg: 0.2, peak: 6), 0.41m, "USD"));
        Assert.Equal(0m, r.Saving);
        Assert.Equal(1140m, r.AtStake);
    }

    [Fact]
    public void An_oversized_capacity_is_halved_then_reserved_on_what_is_left()
    {
        var all = CostRules.Fabric(Cap(), 0.41m, "USD").ToList();
        Assert.Equal(2, all.Count);
        Assert.Equal("Size cap (F32) down to F16", all[0].Title);
        Assert.Equal(2073.5m, all[0].Saving);
        /* The reservation is priced on the smaller size: 41% of the half that is left, not of the whole. */
        Assert.Equal("Reserve cap for a year, once at F16", all[1].Title);
        Assert.Equal(Math.Round(2073.5m * 0.41m, 2), all[1].Saving);
    }

    [Fact]
    public void Weekend_pausing_wins_only_when_it_saves_more_than_a_reservation()
    {
        /* Weekends are 2/7 of the week, 28.6%: less than a 41% reservation, so the reservation is suggested. */
        Assert.Contains(CostRules.Fabric(Cap(peak: 60, weekend: 1), 0.41m, "USD"), o => o.Title.StartsWith("Reserve", StringComparison.Ordinal));
        /* With a smaller discount, pausing at weekends is the better move, and is the one suggested. */
        var cheap = CostRules.Fabric(Cap(peak: 60, weekend: 1), 0.20m, "USD").ToList();
        Assert.Contains(cheap, o => o.Title.StartsWith("Pause cap", StringComparison.Ordinal));
        Assert.DoesNotContain(cheap, o => o.Title.StartsWith("Reserve", StringComparison.Ordinal));
        /* Busy weekends: no pause, whatever the discount. */
        Assert.DoesNotContain(CostRules.Fabric(Cap(peak: 60, weekend: 12), 0.10m, "USD"), o => o.Title.StartsWith("Pause", StringComparison.Ordinal));
    }

    [Fact]
    public void A_busy_reserved_capacity_saves_nothing_and_its_throttling_is_a_risk()
    {
        var all = CostRules.Fabric(Cap(sku: "F64", cu: 64, billing: "Reserved", avg: 58, peak: 121, weekday: 70, weekend: 18, throttled: 648, copilot: 0.11), 0.41m, "USD").ToList();
        var risk = Assert.Single(all);
        Assert.Equal(OpportunityKind.Risk, risk.Kind);
        Assert.Contains("Fabric Copilot capacity", risk.Action);
        Assert.Contains("11%", risk.Evidence);
    }

    [Fact]
    public void A_capacity_is_summarized_from_its_days()
    {
        var days = Enumerable.Range(0, 28).Select(i => new FabricDay("c", new DateTime(2026, 9, 1).AddDays(i), "c", "F2", 2, "PayAsYouGo", 24,
            /* weekdays half used, weekends a tenth; 1 September 2026 is a Tuesday */
            (new DateTime(2026, 9, 1).AddDays(i).DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday ? 0.1m : 0.5m) * 2 * 86400, i == 27 ? 95 : 40, 0, 10)).ToList();
        var s = CostRules.Summarize(days);
        Assert.Equal(280m, s.Cost);
        Assert.Equal(50, s.WeekdayPercent, 3);
        Assert.Equal(10, s.WeekendPercent, 3);
        Assert.Equal(40, s.PeakPercent);   // one day at 95% is above the 95th percentile of 28 days
    }

    [Fact]
    public void Idle_licenses_are_counted_only_when_some_are_idle_and_priced()
    {
        var o = CostRules.IdleLicenses(CostAreas.PowerBi, "Power BI Pro", 1382, 172, 14, "USD")!;
        Assert.Equal(2408m, o.Saving);
        Assert.Contains("172 of 1,382 holders (12%) have not used Power BI", o.Evidence);
        Assert.Null(CostRules.IdleLicenses(CostAreas.PowerBi, "Power BI Pro", 1382, 0, 14, "USD"));
        Assert.Null(CostRules.IdleLicenses(CostAreas.PowerBi, "Power BI Pro", 1382, 50, 0, "USD"));
    }

    [Fact]
    public void A_quiet_process_flow_saves_its_license_or_the_difference()
    {
        Assert.Equal(1200m, CostRules.LightProcessFlows(8, 8, 150, 15, "USD")!.Saving);
        /* An owner without Premium needs one: the saving is the difference. */
        Assert.Equal(150m + 135m, CostRules.LightProcessFlows(2, 1, 150, 15, "USD")!.Saving);
        Assert.Null(CostRules.LightProcessFlows(0, 0, 150, 15, "USD"));
    }

    [Fact]
    public void Credits_past_the_packs_are_bought_as_packs_and_unused_packs_cancelled()
    {
        var more = CostRules.Credits(100000, 4, 58000, 580, 200, "USD")!;
        Assert.Equal("Buy 2 more Copilot Credit packs instead of paying as you go", more.Title);
        Assert.Equal(180m, more.Saving);
        /* Under a pack's worth of overflow: pay-as-you-go is cheaper, nothing to do. */
        Assert.Null(CostRules.Credits(100000, 4, 20000, 200, 200, "USD"));
        var spare = CostRules.Credits(40000, 4, 0, 0, 200, "USD")!;
        Assert.Equal("Cancel 2 unused Copilot Credit packs", spare.Title);
        Assert.Equal(400m, spare.Saving);
    }

    [Fact]
    public void Dataverse_overage_is_priced_by_the_gigabyte_with_an_action_for_its_kind()
    {
        var log = CostRules.DataverseOverage("Log", 20, 27.8m, 10, "USD")!;
        Assert.Equal(78m, log.Saving);
        Assert.Contains("audit log retention", log.Action);
        Assert.Equal("High", log.Confidence);
        Assert.Null(CostRules.DataverseOverage("Database", 122, 120, 40, "USD"));
    }

    [Fact]
    public void Idle_seats_and_idle_ai_accounts()
    {
        Assert.Equal(684m, CostRules.IdleSeats(180, 36, 684, "USD")!.Saving);
        Assert.Null(CostRules.IdleSeats(180, 0, 0, "USD"));
        Assert.Equal(10800m, CostRules.IdleAccount("ptu", 10800, 0, 30, "USD")!.Saving);
        Assert.Null(CostRules.IdleAccount("busy", 2400, 12000, 30, "USD"));
    }

    [Fact]
    public void Opportunities_rank_savings_first_largest_first()
    {
        var ranked = CostRules.Ranked([
            new("a", OpportunityKind.Grow, "grow", "", "", 0, 9000, "Low"),
            new("a", OpportunityKind.Save, "small", "", "", 10, 0, "High"),
            new("a", OpportunityKind.Risk, "risk", "", "", 0, 50, "Low"),
            new("a", OpportunityKind.Save, "large", "", "", 1000, 0, "High")]);
        Assert.Equal(["large", "small", "grow", "risk"], ranked.Select(o => o.Title));
    }

    [Fact]
    public void Money_is_whole_above_a_hundred()
    {
        Assert.Equal("10,800 USD", CostRules.Money(10800, "USD"));
        Assert.Equal("15.5 USD", CostRules.Money(15.5m, "USD"));
    }

    [Fact]
    public void Every_area_has_its_own_color_and_the_page_is_in_leadership()
    {
        Assert.Equal(8, CostAreas.All.Select(a => a.Slot).Distinct().Count());
        Assert.Contains(Nav.Groups.First(g => g.Persona == "Leadership").Pages, p => p.Path == "/leadership/costs");
        Assert.NotNull(Guidance.For("/leadership/costs"));
    }

    [Fact]
    public void The_page_is_for_the_whole_organization_or_licensing()
    {
        var page = Source.Read("Pages", "Leadership", "Costs.cs");
        Assert.Contains("if (!Viewer.OrgWide && !Viewer.Has(Roles.Licensing))", page);
        Assert.Contains("public override bool UsesTeam => false;", page);
    }
}
