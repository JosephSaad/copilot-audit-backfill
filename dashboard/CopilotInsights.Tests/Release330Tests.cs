using CopilotInsights.Data;
using CopilotInsights.Infrastructure;
using CopilotInsights.Pages.Governance;
using Xunit;

namespace CopilotInsights.Tests;

/// <summary>
/// 3.3.0, first part: a page per AI resource, the AI platforms section and the Platforms role, and License inventory's
/// figure that opened a page with another number.
/// </summary>
public class Release330Tests
{
    static string Repo(params string[] path)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine([d.FullName, .. path]))) return File.ReadAllText(Path.Combine([d.FullName, .. path]));
        throw new FileNotFoundException(string.Join('/', path));
    }

    [Fact]
    public void An_apps_cost_is_the_resources_cost_shared_by_calls()
    {
        Assert.Equal(16126.93m, AiResourceModel.CostShare(16897m, 1314536, 1377306));
        Assert.Equal(770.07m, AiResourceModel.CostShare(16897m, 62770, 1377306));
        /* The shares add up to the resource's cost, to the cent here. */
        Assert.Null(AiResourceModel.CostShare(null, 10, 100));
        Assert.Null(AiResourceModel.CostShare(100m, 0, 0));
    }

    [Fact]
    public void A_resource_page_needs_its_resource_chosen_before_it_is_sent()
    {
        Assert.Contains("a resource", Subscriptions.Missing("/governance/ai-apps/resource", "?d=30"));
        Assert.Null(Subscriptions.Missing("/governance/ai-apps/resource", "?d=30&r=contoso-openai-eus"));
        Assert.Contains("@page \"/governance/ai-apps/resource\"", Repo("dashboard", "CopilotInsights", "Pages", "Governance", "AiResource.cshtml"));
        /* Each resource on AI apps beyond Copilot opens it. */
        Assert.Contains("new Link(r.Resource, Ctx.Url(\"/governance/ai-apps/resource\"", Repo("dashboard", "CopilotInsights", "Pages", "Governance", "AiApps.cs"));
    }

    [Fact]
    public void AI_platforms_is_a_section_with_the_Platforms_role_for_named_lists()
    {
        var s = Sections.Find("platforms");
        Assert.NotNull(s);
        Assert.Equal("AI platforms", s!.Name);
        Assert.Equal(Roles.Platforms, s.NamedRole);
        Assert.Contains(Roles.Platforms, Roles.All);
        Assert.True(Roles.Admits(Roles.Platforms));
        Assert.Contains("GitHub Copilot", Roles.Describe[Roles.Platforms]);
        Assert.Equal([Sections.Platforms], Sections.OfRole(Roles.Platforms));
        var g = Assert.Single(Pages.Nav.Groups, x => x.Key == Sections.Platforms);
        Assert.Equal(["/governance/ai-apps", "/governance/ai-apps/resource"], g.Pages.Select(p => p.Path));
        /* The database accepts the role, widened beside the earlier ones; the maintenance script offers it and the section. */
        var sql = Repo("sql", "Create-CopilotDashboardSchema.sql");
        Assert.Contains("definition NOT LIKE N'%''Platforms''%'", sql);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(sql, "'Preview', 'Stories', 'Platforms'\\)\\);").Count);
        var m = Repo("tools", "Invoke-CopilotMaintenance.ps1");
        Assert.Contains("'Stories', 'Platforms')][string] $Role", m);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(m, "'governance', 'platforms', 'wb-ownership'").Count);
    }

    [Fact]
    public void Licensed_today_no_longer_opens_a_page_with_another_number()
    {
        var page = Repo("dashboard", "CopilotInsights", "Pages", "Licensing", "Licensing.cs");
        Assert.DoesNotContain("Href: t.LicensedNotEnabled > 0 ? Open(\"/licensing/leavers\") : null", page);
        Assert.Contains("Kpis.Add(new(\"On disabled accounts\", Num(leavers)", page);
        Assert.Contains("(await ops.LeaversAsync(Ctx.Root)).Count", page);
        Assert.Contains("'/licensing/license-for-credits|Cost of their credits'", Repo("tools", "dev", "ui", "Test-FigureLinks.ps1"));
    }
}

/// <summary>3.3.0, the loaders: the Data sources card names AI accounts in subscriptions the Foundry load does not read.</summary>
public class Release330LoaderTests
{
    [Fact]
    public void AI_accounts_outside_the_loaded_subscriptions_are_named_on_the_card()
    {
        Assert.Null(DataSources.OutsideText(0, 0));
        Assert.Equal("1 more AI account in 1 subscription not loaded: their usage and cost are not counted (add them to FoundrySubscriptions).", DataSources.OutsideText(1, 1));
        Assert.StartsWith("3 more AI accounts in 2 subscriptions", DataSources.OutsideText(3, 2));
    }
}
