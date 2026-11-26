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
}
