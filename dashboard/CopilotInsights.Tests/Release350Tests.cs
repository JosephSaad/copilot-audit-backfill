using CopilotInsights.Data;
using CopilotInsights.Pages;
using Xunit;

namespace CopilotInsights.Tests;

/// <summary>3.5.0: Agent 365 sizing.</summary>
public class Release350Tests
{
    static string Repo(params string[] path)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine([d.FullName, .. path]))) return File.ReadAllText(Path.Combine([d.FullName, .. path]));
        throw new FileNotFoundException(string.Join('/', path));
    }

    [Fact]
    public void Each_option_licenses_its_tier_and_the_narrower_ones_and_never_a_suite_holder_again()
    {
        var groups = new[]
        {
            new AgentLicensing.Group("Finance", AgentLicensing.Owner, false, 10),
            new AgentLicensing.Group("Finance", AgentLicensing.Owner, true, 2),       // owners holding E7: not priced
            new AgentLicensing.Group("Finance", AgentLicensing.Delegating, false, 40),
            new AgentLicensing.Group("Sales", AgentLicensing.User, false, 250),
            new AgentLicensing.Group("Sales", AgentLicensing.User, true, 8),
        };
        var o = AgentLicensing.Options(groups, 15m);
        Assert.Equal([10, 50, 300], o.Select(x => x.People));
        Assert.Equal([2, 2, 10], o.Select(x => x.Covered));
        Assert.Equal(150m, o[0].Month);
        Assert.Equal(1800m, o[0].Year);
        Assert.Equal(54000m, o[2].Year);
        Assert.Equal(54000m - 9000m, AgentLicensing.SavingAYear(o, AgentLicensing.Delegating));
        Assert.Equal(0m, AgentLicensing.SavingAYear(o, AgentLicensing.User));
        /* Nobody at all: three options of nobody, nothing to pay. */
        Assert.All(AgentLicensing.Options([], 15m), x => Assert.Equal(0m, x.Year));
    }

    [Fact]
    public void The_page_is_in_Licensing_names_owners_only_for_the_role_and_its_price_is_an_assumption()
    {
        var licensing = Nav.Groups.Single(g => g.Persona == "Licensing").Pages;
        var page = licensing.Single(p => p.Path == "/licensing/agent-365");
        Assert.Null(page.Role);   // counts for the section; the owners' names for the Licensing role, inside the page
        Assert.NotNull(Guidance.For("/licensing/agent-365"));
        Assert.NotNull(NavText.What(page));
        var src = Repo("dashboard", "CopilotInsights", "Pages", "Licensing", "Agent365.cs");
        Assert.Contains("if (Viewer.Has(Roles.Licensing) && !PrivacyMode)", src);
        Assert.Contains("AtLeastMin(d.O + d.D + d.U)", src);   // departments under the minimum group size left out
        Assert.Contains(Assumptions.Settings, d => d.Key == "Agent365MonthlyPrice" && d.Default == "15");
        Assert.Contains("('Agent365MonthlyPrice', N'15',", Repo("sql", "Create-CopilotDashboardSchema.sql"));
        Assert.Contains("| | Agent 365 sizing |", Repo("docs", "DASHBOARD.md"));
    }

    [Fact]
    public void A_disabled_accounts_license_is_reclaimable_only_after_the_grace_period_since_its_last_use()
    {
        Assert.Equal(90, OpsData.DefaultGraceDays);
        Assert.Contains(Assumptions.Settings, d => d.Key == "DisabledGraceDays" && d.Default == "90" && d.Page == Assumptions.Access);
        Assert.Contains("('DisabledGraceDays', N'90',", Repo("sql", "Create-CopilotDashboardSchema.sql"));
        var ops = Repo("dashboard", "CopilotInsights", "Data", "OpsData.cs");
        /* Gone from the directory or never used: no grace; otherwise the last use plus the days set. */
        Assert.Contains("OR lu.LastUsed IS NULL THEN NULL", ops);
        Assert.Contains("ELSE DATEADD(DAY, @grace, lu.LastUsed) END AS GraceEnds", ops);
        Assert.Contains("AND (@withGrace = 1 OR g.GraceEnds IS NULL OR g.GraceEnds <= td.[Date])", ops);
        /* Every list of licenses to take back keeps them out: the unused list and Suite fit too. */
        Assert.Contains("AND NOT (du.AccountEnabled = 0 AND du.DirectoryStatus <> N'NotFound' AND x.LastKey > @graceKey)", Repo("dashboard", "CopilotInsights", "Pages", "Licensing", "Licensing.cs"));
        Assert.Contains("THEN CASE WHEN {lastUse} > DATEADD(DAY, -@grace, @today) THEN 1 ELSE 0 END", Repo("dashboard", "CopilotInsights", "Data", "SuiteData.cs"));
        var admin = Repo("dashboard", "CopilotInsights", "Pages", "Admin", "Admin.cs");
        Assert.Contains("Input(\"DisabledGraceDays\"", admin);
        Assert.Contains("if (DisabledGraceDays is < 0 or > 365)", admin);
    }
}

/// <summary>3.5.0: model tokens by business unit, spend limits, agent policies.</summary>
public class Release350LimitsTests
{
    static string Repo(params string[] path)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine([d.FullName, .. path]))) return File.ReadAllText(Path.Combine([d.FullName, .. path]));
        throw new FileNotFoundException(string.Join('/', path));
    }

    static Dictionary<DateTime, decimal> Days(DateTime from, int n, decimal each) =>
        Enumerable.Range(0, n).ToDictionary(i => from.AddDays(i), _ => each);

    [Fact]
    public void A_limit_is_projected_at_the_last_seven_days_pace_and_reached_on_the_day_it_crosses()
    {
        /* 100 a day from the 1st of a 30-day month, a limit of 2,500: 2,000 by the 20th, at 100 a day it reaches
           2,500 on the 25th, and ends the month at 3,000. */
        var asOf = new DateTime(2026, 11, 20);
        var p = SpendLimits.Where(2500m, SpendLimits.Monthly, asOf, Days(new DateTime(2026, 10, 25), 27, 100m));
        Assert.Equal(2000m, p.Spent);
        Assert.Equal(3000m, p.Projected);
        Assert.Equal(new DateTime(2026, 11, 25), p.ReachedOn);
        Assert.False(p.Reached);
        Assert.Equal(10, p.DaysLeft);
        Assert.True(SpendLimits.Warn(p, asOf, SpendLimits.Monthly, 5, null));    // five days early
        Assert.False(SpendLimits.Warn(p, asOf, SpendLimits.Monthly, 6, null));
        /* Already over: reached on the day it crossed. */
        var over = SpendLimits.Where(1000m, SpendLimits.Monthly, asOf, Days(new DateTime(2026, 11, 1), 20, 100m));
        Assert.True(over.Reached);
        Assert.Equal(new DateTime(2026, 11, 10), over.ReachedOn);
        /* Under seven days of history: too early to project. */
        var young = SpendLimits.Where(1000m, SpendLimits.Monthly, asOf, Days(new DateTime(2026, 11, 17), 4, 100m));
        Assert.True(young.TooEarly);
        Assert.Null(young.Projected);
        /* A limit of 0 or none: never warned about. */
        Assert.False(SpendLimits.Warn(SpendLimits.Where(0m, SpendLimits.Monthly, asOf, Days(asOf.AddDays(-9), 10, 5m)), asOf, SpendLimits.Monthly, 5, 50));
    }

    [Fact]
    public void Periods_suggestions_and_short_limits()
    {
        Assert.Equal((new DateTime(2026, 11, 23), new DateTime(2026, 11, 29)), SpendLimits.PeriodOf(SpendLimits.Weekly, new DateTime(2026, 11, 26)));
        Assert.Equal((new DateTime(2026, 2, 1), new DateTime(2026, 2, 28)), SpendLimits.PeriodOf(SpendLimits.Monthly, new DateTime(2026, 2, 14)));
        Assert.True(SpendLimits.IsShort(SpendLimits.Minute));
        Assert.Null(SpendLimits.Suggest([100m, 120m, 130m, 140m, 150m], "Credits", false, 10));   // under six months
        var s = SpendLimits.Suggest([1000m, 1100m, 1200m, 1300m, 1400m, 1500m, 1600m], "Credits", false, 10);
        Assert.NotNull(s);
        Assert.True(s > 1600m);
        Assert.Equal(0m, s!.Value % 100m);
        Assert.Equal(3_000_000m, SpendLimits.RoundUp(2_000_001m, "Tokens"));
        Assert.Equal(20m, SpendLimits.RoundUp(11m, "USD"));
        Assert.Equal((0.75, 0.5), SpendLimits.ShortPeriods(100m, new List<decimal> { 10m, 80m, 100m, 120m }));
    }

    [Fact]
    public void A_business_unit_is_an_L2_and_L3_pair_and_a_route_is_checked()
    {
        Assert.Equal((2, 3), GatewayTokens.ParseLevels(null));
        Assert.Equal((2, 3), GatewayTokens.ParseLevels("3, 2"));
        Assert.Equal((3, 4), GatewayTokens.ParseLevels("3"));
        Assert.Equal("Riley Rossi › Aisha Murphy", GatewayTokens.UnitName("Riley Rossi", "r@contoso.com", "Aisha Murphy", "a@contoso.com"));
        Assert.Equal("Riley Rossi", GatewayTokens.UnitName("Riley Rossi", "r@contoso.com", null, null));
        var r = GatewayTokens.Check(" CC-1001 ", "r@contoso.com", "", new DateTime(2026, 11, 1), null, null);
        Assert.Equal("CC-1001", r.Value);
        Assert.Null(r.L3Upn);
        Assert.Throws<ArgumentException>(() => GatewayTokens.Check("CC-1", "riley", null, DateTime.Today, null, null));
        Assert.Throws<ArgumentException>(() => GatewayTokens.Check("CC-1", "r@contoso.com", null, new DateTime(2026, 11, 2), new DateTime(2026, 11, 1), null));
        /* Counts only: the page never prices a token. */
        var page = Repo("dashboard", "CopilotInsights", "Pages", "Leadership", "ModelTokens.cs");
        Assert.DoesNotContain("Money(", page);
        Assert.DoesNotContain("LicenseCurrency", page);
    }

    [Fact]
    public void Agent_policies_never_delete_and_a_new_owner_is_a_persons_decision()
    {
        Assert.True(AgentPolicies.MayAutoApprove(AgentPolicies.Notify));
        Assert.True(AgentPolicies.MayAutoApprove(AgentPolicies.Quarantine));
        Assert.False(AgentPolicies.MayAutoApprove(AgentPolicies.Reassign));
        var schema = Repo("sql", "Create-CopilotOperationsSchema.sql");
        Assert.Contains("CHECK (ActionKind IN ('Notify', 'Quarantine', 'Release', 'Reassign'))", schema);
        Assert.Contains("('AgentActionsEnabled',         N'0',", schema);
        var tool = Repo("tools", "Invoke-AgentActions.ps1");
        Assert.DoesNotContain("Method Delete", tool);
        Assert.Contains("AgentActionsEnabled", tool);
    }

    [Fact]
    public void The_three_pages_are_in_their_sections_and_documented()
    {
        foreach (var (section, path) in new[] { ("Leadership", "/leadership/model-tokens"), ("Forecasting", "/forecasting/spend-limits"), ("AI apps and controls", "/governance/agent-policies") })
        {
            var page = Nav.Groups.Single(g => g.Persona == section).Pages.Single(p => p.Path == path);
            Assert.NotNull(Guidance.For(path));
            Assert.NotNull(NavText.What(page));
        }
        var ops = Repo("docs", "OPERATIONS.md");
        Assert.Contains("## Model tokens, spend limits and agent policies", ops);
        Assert.Contains("docs/GATEWAY-LIMITS-AGENTS.md", ops);
        var pipe = Repo("Invoke-CopilotPipeline.ps1");
        Assert.Contains("'tools/Import-GatewayTokenUsage.ps1'", pipe);
        Assert.Contains("'tools/Import-SpendLimits.ps1'", pipe);
        Assert.Contains("'tools/Invoke-AgentActions.ps1'", pipe);
        /* An entered limit is checked. */
        Assert.Equal("Credits", Pages.Forecasting.SpendLimitsModel.CheckLimit("HR", "CopilotResource", "Environment", "HR Services", "500", "Monthly").Unit);
        Assert.Throws<ArgumentException>(() => Pages.Forecasting.SpendLimitsModel.CheckLimit("HR", "CopilotResource", "Environment", "", "500", "Monthly"));
        Assert.Throws<ArgumentException>(() => Pages.Forecasting.SpendLimitsModel.CheckLimit("HR", "CopilotResource", "Environment", "HR", "0", "Monthly"));
    }
}
