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

/// <summary>The suites (2.6.6): what fits each holder of E3, E5 and E7, and what is paid twice or bought for nobody.</summary>
public class SuiteRulesTests
{
    static readonly Suite E3 = new("SPE_E3%", "Microsoft 365 E3", "Microsoft 365", 3, 39, new HashSet<string>(), null);
    static readonly Suite E5 = new("SPE_E5%", "Microsoft 365 E5", "Microsoft 365", 5, 60, new HashSet<string> { SuiteRules.PowerBiPro }, null);
    static readonly Suite E7 = new("Microsoft_365_E7%", "Microsoft 365 E7", "Microsoft 365", 7, 99, new HashSet<string> { SuiteRules.Copilot, SuiteRules.PowerBiPro }, null);
    static readonly Suite O3 = new("ENTERPRISEPACK%", "Office 365 E3", "Office 365", 3, 26, new HashSet<string>(), null);
    static readonly Suite[] All = [E3, E5, E7, O3];
    static readonly DateTime Today = new(2026, 10, 17);

    static SuiteFit Fit(Suite s, int copilotDays = 0, bool addOn = false, bool bi = false, Suite[]? also = null, bool? usesBi = null,
        bool prot = false, bool enabled = true, DateTime? since = null) =>
        SuiteRules.Fit(new SuiteHolder("a@contoso.com", s, copilotDays, addOn, bi, also ?? [], usesBi, prot, enabled, since ?? Today.AddDays(-200)),
            SuiteRules.Lower(s, All), 30, 14, Today, "USD");

    [Fact]
    public void The_next_suite_down_stays_in_its_family()
    {
        Assert.Same(E5, SuiteRules.Lower(E7, All));
        Assert.Same(E3, SuiteRules.Lower(E5, All));
        Assert.Null(SuiteRules.Lower(E3, All));
        Assert.Null(SuiteRules.Lower(O3, All));
    }

    [Fact]
    public void A_license_the_suite_includes_is_dropped_whatever_the_use()
    {
        var f = Fit(E7, copilotDays: 20, addOn: true, bi: true);
        Assert.Equal(SuiteMove.DropDuplicate, f.Move);
        Assert.Equal(44m, f.Saving);
        Assert.Equal("High", f.Confidence);
        Assert.Equal(60m, Fit(E7, copilotDays: 20, also: [E5]).Saving);
        /* Office 365 E3 under Microsoft 365 E5 is a copy too, and every copy counts. */
        Assert.Equal(26m, Fit(E5, usesBi: true, also: [O3]).Saving);
        Assert.Equal(60m + 26m, Fit(E7, copilotDays: 20, also: [E5, O3]).Saving);
        /* E3 includes no Power BI Pro: a standalone one on E3 is no copy. */
        Assert.NotEqual(SuiteMove.DropDuplicate, Fit(E3, bi: true, usesBi: true).Move);
    }

    [Fact]
    public void E7_without_Copilot_use_moves_to_E5_light_use_trains_first_and_regular_use_stays()
    {
        var down = Fit(E7, copilotDays: 0);
        Assert.Equal(SuiteMove.Downgrade, down.Move);
        Assert.Equal("Microsoft 365 E5", down.To);
        Assert.Equal(39m, down.Saving);
        Assert.Equal("Medium", down.Confidence);
        Assert.Contains("Agent 365", down.Check);

        var train = Fit(E7, copilotDays: 2);
        Assert.Equal(SuiteMove.Train, train.Move);
        Assert.Equal(0m, train.Saving);
        Assert.Equal(39m, train.AtStake);

        /* E5 and the add-on (60 + 30) cost 9 less than E7 (99): a low-confidence move, as Agent 365 and the Entra Suite are unseen. */
        var regular = Fit(E7, copilotDays: 12);
        Assert.Equal(SuiteMove.DowngradeWithCopilot, regular.Move);
        Assert.Equal(9m, regular.Saving);
        Assert.Equal("Low", regular.Confidence);
    }

    [Fact]
    public void E5_moves_to_E3_only_on_Power_BI_use_and_never_with_more_than_low_confidence()
    {
        var unused = Fit(E5, usesBi: false);
        Assert.Equal(SuiteMove.Downgrade, unused.Move);
        Assert.Equal(21m, unused.Saving);
        Assert.Equal("Low", unused.Confidence);
        Assert.Contains("security", unused.Check);

        var used = Fit(E5, usesBi: true);
        Assert.Equal(SuiteMove.DowngradeWithPowerBi, used.Move);
        Assert.Equal(7m, used.Saving);

        /* Activity not loaded: nobody's use is known, so no move. */
        Assert.Equal(SuiteMove.Keep, Fit(E5, usesBi: null).Move);
    }

    [Fact]
    public void Protected_new_and_disabled_holders_are_kept_or_taken_back()
    {
        Assert.Equal(SuiteMove.Keep, Fit(E7, prot: true).Move);
        Assert.Equal(SuiteMove.Keep, Fit(E7, since: Today.AddDays(-10)).Move);
        var gone = Fit(E5, enabled: false);
        Assert.Equal(SuiteMove.Reclaim, gone.Move);
        Assert.Equal(60m, gone.Saving);
        /* A disabled account gives back every suite it holds, protected or not, as on Licenses on disabled accounts. */
        Assert.Equal(99m + 60m, Fit(E7, enabled: false, prot: true, also: [E5]).Saving);
        /* A protected level is never offered anything else, a copy included, as on every other list. */
        Assert.Equal(SuiteMove.Keep, Fit(E7, addOn: true, prot: true).Move);
    }

    [Fact]
    public void Licenses_paid_twice_and_seats_bought_for_nobody_are_savings()
    {
        var twice = CostRules.HeldTwice(CostAreas.PowerBi, "Power BI Pro", "Microsoft 365 E5", 120, 14, "USD");
        Assert.NotNull(twice);
        Assert.Equal(1680m, twice!.Saving);
        Assert.Equal("High", twice.Confidence);
        Assert.Null(CostRules.HeldTwice(CostAreas.PowerBi, "Power BI Pro", "Microsoft 365 E5", 0, 14, "USD"));

        var spare = CostRules.Unassigned(CostAreas.Apps, "Power Apps Premium", 2000, 776, 20, "USD");
        Assert.Equal(24480m, spare!.Saving);
        Assert.Equal("Medium", spare.Confidence);
        Assert.Null(CostRules.Unassigned(CostAreas.Apps, "Power Apps Premium", 776, 776, 20, "USD"));
    }

    [Fact]
    public void A_Copilot_inside_a_suite_is_never_on_the_revoke_list()
    {
        Assert.Contains("dash.CopilotSuiteOnly", Pages.Forecasting.ActionsModel.Kinds["revoke"].Where);
        Assert.DoesNotContain("CopilotSuiteOnly", Pages.Forecasting.ActionsModel.Kinds["train"].Where);
        Assert.Contains(Nav.Groups.First(g => g.Persona == "Licensing").Pages, p => p.Path == "/licensing/suites");
        Assert.NotNull(Guidance.For("/licensing/suites"));
    }
}

/// <summary>The credit estimate (2.6.6): credits for what bills, tokens for everything, a month at a time, and a cap.</summary>
public class CreditRulesTests
{
    [Fact]
    public void Only_billed_answers_cost_credits_more_for_work_data_and_actions()
    {
        /* 100 interactions, 40 with an agent, 30 billed: 10 of them grounded in work data, 2 calling another agent. */
        var e = CreditRules.Estimate(new CreditUse(100, 40, 30, 10, 2, 50, 100), new CreditRates());
        Assert.Equal(30 * 2 + 10 * 10 + 2 * 5, e.Credits);
        Assert.Equal(1.7m, e.Cost);
        Assert.Equal(100 * 1500L + 50 * 1000L + 100 * 400L, e.Tokens);
        /* Nothing billed: no credits, but the tokens are still there. */
        var free = CreditRules.Estimate(new CreditUse(10, 10, 0, 0, 0, 0, 10), new CreditRates());
        Assert.Equal(0m, free.Credits);
        Assert.True(free.Tokens > 0);
    }

    [Fact]
    public void A_window_is_brought_to_thirty_days_and_a_cap_counts_who_reaches_it()
    {
        var m = CreditRules.Monthly(new CreditEstimate(150, 1.5m, 1000), 15);
        Assert.Equal(300m, m.Credits);
        Assert.Equal(2000L, m.Tokens);
        Assert.Equal((2, 700m), CreditRules.Cap([100m, 600m, 1100m], 500));
        Assert.Equal((0, 0m), CreditRules.Cap([100m, 600m], 0));
        Assert.Equal("1.2M", CreditRules.Tokens(1_200_000));
        Assert.Equal("340K", CreditRules.Tokens(340_000));
    }

    [Fact]
    public void The_page_is_for_licensing_and_in_its_menu()
    {
        Assert.Contains(Nav.Groups.First(g => g.Persona == "Licensing").Pages, p => p.Path == "/licensing/credit-use");
        Assert.NotNull(Guidance.For("/licensing/credit-use"));
        Assert.Contains("RequiredRole => Roles.Licensing", Source.Read("Pages", "Licensing", "CreditUse.cs"));
    }
}

/// <summary>The drill-downs (2.6.6): every headline figure on Costs and savings leads to the detail behind it.</summary>
public class CostDrillDownTests
{
    [Fact]
    public void Six_months_draw_six_labels()
    {
        var months = new[] { "Apr 2026", "May 2026", "Jun 2026", "Jul 2026", "Aug 2026", "Sep 2026" };
        var svg = CopilotInsights.Charts.Chart.Columns(months, [new CopilotInsights.Charts.Series("a", [1, 2, 3, 4, 5, 6], 1)], CopilotInsights.Charts.Fmt.Count, "six months").ToString()!;
        foreach (var m in months) Assert.Contains(m.Split(' ')[0], svg);
    }

    [Fact]
    public void Each_area_has_its_own_view_and_the_figures_lead_to_their_detail()
    {
        var page = Source.Read("Pages", "Leadership", "Costs.cs");
        Assert.Contains("RenderArea(v, pick)", page);           // ?area=<slot>: an area's own view
        Assert.Contains("conf={c}", page);                       // a confidence lists its savings alone
        Assert.Contains("\"#areas\"", page);                     // spend leads to every area
        Assert.Contains("/forecasting/outcomes", page);          // what was saved leads to the outcomes
        Assert.Contains("open(\"/licensing/suites\", (m.Move == SuiteMove.DropDuplicate", page);   // each suite move leads to its people
        Assert.Contains("to is null && area == Here ? E(text)", page);   // an area's view does not link to itself
        /* A link goes only where the viewer may go: every headline figure's link, and the page's own, through Open. */
        Assert.Contains("var to = href is null ? null : Open(href);", page);
        var pages = new DirectoryInfo(AppContext.BaseDirectory);
        while (!Directory.Exists(Path.Combine(pages.FullName, "CopilotInsights", "Pages"))) pages = pages.Parent!;
        foreach (var f in Directory.GetFiles(Path.Combine(pages.FullName, "CopilotInsights", "Pages"), "*.cs", SearchOption.AllDirectories))
            Assert.DoesNotContain("Href: Ctx.Url(", File.ReadAllText(f));
        var data = Source.Read("Data", "CostData.cs");
        foreach (var area in new[] { "CostAreas.Copilot", "CostAreas.Studio", "CostAreas.Fabric", "CostAreas.Automate", "CostAreas.Apps", "CostAreas.GitHub", "CostAreas.Foundry" })
            Assert.Contains($"details.Add(new({area}", data);
    }
}