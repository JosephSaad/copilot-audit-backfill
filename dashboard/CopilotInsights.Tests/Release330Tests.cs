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
        Assert.Equal(["/platforms/foundry", "/platforms/foundry/deployments", "/platforms/foundry/posture", "/platforms/github", "/platforms/github/seats", "/platforms/github/posture",
                      "/governance/ai-apps", "/governance/ai-apps/resource"], g.Pages.Select(p => p.Path));
        /* Only the list that names people needs the role. */
        Assert.Equal(["/platforms/github/seats"], g.Pages.Where(p => p.Role == Roles.Platforms).Select(p => p.Path));
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
        /* 3.5.0: the figure counts the reclaimable ones, as the page it opens does: those past the grace period. */
        Assert.Contains("var leavers = all.Count(r => !r.InGrace);", page);
        Assert.Contains("(await ops.LeaversAsync(Ctx.Root)).Count", Repo("dashboard", "CopilotInsights", "Pages", "Licensing", "Operations.cs"));
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

/// <summary>3.3.0, the AI platforms pages' rules: idle, retiring, under-used, a meter's model, the posture checks, idle seats, GitHub's recommendations.</summary>
public class PlatformRulesTests
{
    [Fact]
    public void A_deployment_is_idle_retiring_or_under_used_by_the_settings()
    {
        var newest = new DateTime(2026, 11, 20);
        Assert.True(PlatformRules.IsIdle(null, newest, 30));
        Assert.True(PlatformRules.IsIdle(new DateTime(2026, 10, 21), newest, 30));
        Assert.False(PlatformRules.IsIdle(new DateTime(2026, 10, 22), newest, 30));
        Assert.True(PlatformRules.IsRetiring(new DateTime(2027, 1, 1), newest, 90));
        Assert.False(PlatformRules.IsRetiring(new DateTime(2027, 6, 1), newest, 90));
        Assert.False(PlatformRules.IsRetiring(null, newest, 90));
        Assert.True(PlatformRules.IsPtuLow("ProvisionedManaged", 20, 50));
        Assert.False(PlatformRules.IsPtuLow("ProvisionedManaged", 70, 50));
        Assert.False(PlatformRules.IsPtuLow("GlobalStandard", 0, 50));
    }

    [Fact]
    public void A_meter_belongs_to_the_longest_model_it_names()
    {
        string[] models = ["gpt-4o", "gpt-4o-mini", "o3"];
        Assert.Equal("gpt-4o-mini", PlatformRules.ModelOfMeter("gpt-4o-mini Input Tokens", models));
        Assert.Equal("gpt-4o", PlatformRules.ModelOfMeter("gpt-4o tokens", models));
        Assert.Equal("o3", PlatformRules.ModelOfMeter("o3", models));
        Assert.Null(PlatformRules.ModelOfMeter("o3-pro Output Tokens", models));
        Assert.Null(PlatformRules.ModelOfMeter("Storage and networking", models));
    }

    [Fact]
    public void The_posture_checks_say_what_to_fix_and_unknown_stays_unknown()
    {
        var open = PlatformRules.Posture("Enabled", "Allow", 0, false, 0, 1, null);
        Assert.All(open, c => Assert.False(c.Passes));
        Assert.Contains("Reachable from any network", open[0].Text);
        var closed = PlatformRules.Posture("Enabled", "Deny", 2, true, 1, 0, "ann@contoso.com");
        Assert.All(closed, c => Assert.True(c.Passes));
        Assert.Contains("2 private endpoints", closed[0].Text);
        var unknown = PlatformRules.Posture(null, null, null, null, null, 0, "x");
        Assert.Null(unknown[0].Passes); Assert.Null(unknown[1].Passes); Assert.Null(unknown[2].Passes);
    }

    [Fact]
    public void A_seat_is_idle_as_Costs_and_savings_counts_it_and_GitHub_recommends_two_settings()
    {
        var now = new DateTime(2026, 11, 25);
        Assert.True(PlatformRules.IsIdleSeat(null, now.AddDays(-40), now, 30));
        Assert.False(PlatformRules.IsIdleSeat(null, now.AddDays(-5), now, 30));
        Assert.True(PlatformRules.IsIdleSeat(now.AddDays(-31), now.AddDays(-300), now, 30));
        Assert.False(PlatformRules.IsIdleSeat(now.AddDays(-2), null, now, 30));
        Assert.Equal("block", PlatformRules.Recommended("public_code_suggestions"));
        Assert.Equal("assign_selected", PlatformRules.Recommended("seat_management_setting"));
        Assert.Null(PlatformRules.Recommended("ide_chat"));
        Assert.Equal("Suggestions matching public code", PlatformRules.PolicyName("public_code_suggestions"));
        /* The seat list's idle filter is the same SQL as the overview's figure. */
        Assert.Equal(CopilotInsights.Pages.Platforms.GitHubSeatsModel.Shows["idle"].Sql, typeof(CopilotInsights.Pages.Platforms.GitHubPage)
            .GetField("IdleSql", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetValue(null));
    }
}
