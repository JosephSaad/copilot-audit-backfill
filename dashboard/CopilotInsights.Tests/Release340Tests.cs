using CopilotInsights.Pages;
using CopilotInsights.Pages.Leadership;
using Xunit;

namespace CopilotInsights.Tests;

/// <summary>
/// 3.4.0: the wider AI bill (every AI service's actual and amortized cost, budgets and the month's forecast), the
/// daily brief, AI spend across Azure, My agents, and a color for each section.
/// </summary>
public class Release340Tests
{
    static string Repo(params string[] path)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine([d.FullName, .. path]))) return File.ReadAllText(Path.Combine([d.FullName, .. path]));
        throw new FileNotFoundException(string.Join('/', path));
    }

    [Fact]
    public void The_new_pages_are_in_their_sections_with_their_text()
    {
        var lead = Nav.Groups.Single(g => g.Persona == "Leadership").Pages.Select(p => p.Path).ToList();
        Assert.Equal("/leadership/brief", lead[0]);   // the executive's first page of the day
        Assert.Equal(lead.IndexOf("/leadership/costs") + 1, lead.IndexOf("/leadership/ai-spend"));
        var agents = Nav.Groups.Single(g => g.Persona == "Agents").Pages;
        Assert.Equal("/agents/mine", agents[0].Path);
        Assert.Null(agents[0].Role);   // counts only, at the minimum group size: no role
        foreach (var path in new[] { "/leadership/brief", "/leadership/ai-spend", "/agents/mine" })
        {
            Assert.NotNull(Guidance.For(path));
            var page = Nav.Groups.SelectMany(g => g.Pages).First(p => p.Path == path);
            Assert.NotNull(NavText.What(page));
        }
        var guide = Repo("docs", "DASHBOARD.md");
        Assert.Contains("| | Daily brief |", guide);
        Assert.Contains("| | AI spend across Azure |", guide);
        Assert.Contains("| Agents | My agents |", guide);
    }

    [Fact]
    public void A_scope_reads_as_its_kind_and_id()
    {
        Assert.Equal("Subscription 6b2c", AiSpendModel.ScopeName("/subscriptions/6b2c"));
        Assert.Equal("Billing account 1234:5678", AiSpendModel.ScopeName("/providers/Microsoft.Billing/billingAccounts/1234:5678"));
        Assert.Equal("Department 42", AiSpendModel.ScopeName("/providers/Microsoft.Billing/billingAccounts/1/departments/42"));
        Assert.Equal("odd", AiSpendModel.ScopeName("odd"));
    }

    [Fact]
    public void Each_section_has_a_color_in_both_themes()
    {
        Assert.Equal("ai-platforms", InsightsPage.SectionSlug("AI platforms"));
        Assert.Equal("app-owners", InsightsPage.SectionSlug("App owners"));
        Assert.Equal("", InsightsPage.SectionSlug(null));
        Assert.Equal("x-onload-y", InsightsPage.SectionSlug(" x\" onload=\"y "));   // only letters and hyphens reach the page
        var css = Repo("dashboard", "CopilotInsights", "wwwroot", "site.css");
        foreach (var g in Nav.Groups)
        {
            var slug = InsightsPage.SectionSlug(g.Persona);
            Assert.Contains($"body[data-section=\"{slug}\"], [data-grp=\"{g.Persona}\"] {{ --sec:", css);
            Assert.Contains($":root[data-theme=\"dark\"] body[data-section=\"{slug}\"]", css);
            Assert.Contains($":root:not([data-theme=\"light\"]) body[data-section=\"{slug}\"]", css);
        }
        Assert.Contains("data-section=\"@(page is null ? null : InsightsPage.SectionSlug(page.Persona))\"", Repo("dashboard", "CopilotInsights", "Pages", "Shared", "_Layout.cshtml"));
        /* Status tags carry their word, never color alone. */
        Assert.Contains(".tag.good", css);
        Assert.Contains(".tag.crit", css);
    }

    [Fact]
    public void The_wider_bill_is_three_tables_the_dashboard_may_read()
    {
        var sql = Repo("sql", "Create-CopilotExternalUsageSchema.sql");
        foreach (var t in new[] { "audit.AiCostDay", "audit.AiBudget", "audit.AiCostForecastDay" })
        {
            Assert.Contains($"IF OBJECT_ID(N'{t}', N'U') IS NULL", sql);
            Assert.Contains($"GRANT SELECT ON {t} TO CopilotInsightsApp;", sql);
        }
        /* The pages ask whether it is loaded before reading it: a database without it shows how to set it up. */
        var spend = Repo("dashboard", "CopilotInsights", "Pages", "Leadership", "AiSpend.cs");
        Assert.Contains("HasRowsAsync(\"audit.AiCostDay\")", spend);
        var brief = Repo("dashboard", "CopilotInsights", "Pages", "Leadership", "Brief.cs");
        Assert.Contains("TableHasRowsAsync(\"audit.AiCostDay\")", brief);
        /* Asked in two steps: a statement naming a table not there fails as it runs. */
        Assert.DoesNotContain("IS NOT NULL AND EXISTS (SELECT 1 FROM {table})", Repo("dashboard", "CopilotInsights", "Pages", "Platforms", "Foundry.cs"));
        /* Money shows to whole-organization viewers and the Licensing role only, as on the start page. */
        Assert.Contains("Viewer.OrgWide || Viewer.Has(Roles.Licensing) ? costs.BuildAsync(Ctx) : null", brief);
    }

    [Fact]
    public void My_agents_opens_another_owners_only_for_whole_organization_viewers()
    {
        var mine = Repo("dashboard", "CopilotInsights", "Pages", "Agents", "Mine.cs");
        Assert.Contains("Owner = asked.Length > 0 && (Viewer.OrgWide || Viewer.AnyAdmin) ? asked : Viewer.Upn;", mine);
        Assert.Contains("AtLeastMin(r.People)", mine);
    }
    [Fact]
    public void Each_statement_runs_with_parameters_of_its_own()
    {
        /* One Ctx.Args shared by reads run side by side broke Agent detail now and then ("a concurrent update was
           performed on this collection"): every statement the dashboard's Db runs is given a copy. */
        var db = Repo("dashboard", "CopilotInsights", "Infrastructure", "Db.cs");
        Assert.Contains("args is DynamicParameters d ? new DynamicParameters(d) : args", db);
        Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(db, @"new CommandDefinition\(PlanHints\.Apply\(sql, args\), Own\(args\)").Count);
        Assert.DoesNotContain("PlanHints.Apply(sql, args), args,", db);
        Assert.DoesNotContain("ExecuteScalarAsync<T>(sql, args)", db);
    }
    [Fact]
    public void The_weekly_email_says_the_AI_bill_when_it_is_loaded()
    {
        var weekly = Repo("tools", "Send-CopilotWeeklyReport.ps1");
        Assert.Contains("function Get-ReportAiBill", weekly);
        Assert.Contains("<b>AI bill.</b>", weekly);
        Assert.Contains("-Saving $saving -AiBill $aiBill", weekly);
        /* The table is named only once it is known to be there. */
        Assert.Contains("IF OBJECT_ID(N'audit.AiCostDay', N'U') IS NULL OR (SELECT Value FROM dash.Setting WHERE Name = N'WeeklyReportSavings') = N'0'", weekly);
        Assert.DoesNotContain("IS NULL OR NOT EXISTS (SELECT 1 FROM audit.AiCostDay)", weekly);
    }
    [Fact]
    public void The_briefs_sentences_are_emailed_and_tables_never_are()
    {
        var sender = Repo("dashboard", "CopilotInsights", "Infrastructure", "SubscriptionSender.cs");
        Assert.Contains("if (p.Email && found.Count == 0 && !html.Contains(\"<table\", StringComparison.OrdinalIgnoreCase))", sender);
        Assert.Contains("Regex.Replace(html, \"\\\\s*<a\\\\b[^>]*>.*?</a>\"", sender);   // links are left out: they would lead nowhere from a mailbox
        Assert.False(new Panel("x", new Microsoft.AspNetCore.Html.HtmlString("")).Email);   // off unless a page says so
        var brief = Repo("dashboard", "CopilotInsights", "Pages", "Leadership", "Brief.cs");
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(brief, "Email: true").Count);
        /* Only the brief's own sentences, which name no one: no other page sets it. */
        var pages = new DirectoryInfo(AppContext.BaseDirectory);
        while (!Directory.Exists(Path.Combine(pages.FullName, "CopilotInsights", "Pages"))) pages = pages.Parent!;
        foreach (var f in Directory.GetFiles(Path.Combine(pages.FullName, "CopilotInsights", "Pages"), "*.cs", SearchOption.AllDirectories))
            if (!f.EndsWith("Brief.cs", StringComparison.Ordinal)) Assert.DoesNotContain("Email: true", File.ReadAllText(f));
    }
}
