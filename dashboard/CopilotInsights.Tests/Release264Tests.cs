using CopilotInsights.Data;
using CopilotInsights.Infrastructure;
using CopilotInsights.Pages;
using Xunit;

namespace CopilotInsights.Tests;

/// <summary>Persona, a role for some of the Admin pages (2.6.4).</summary>
public class PersonaRoleTests
{
    static Viewer V(int? level, string[] roles, params string[] pages) =>
        new()
        {
            WindowsName = @"CONTOSO\me", Upn = "me@contoso.com", Level = level, InDirectory = true,
            Roles = new HashSet<string>(roles, StringComparer.OrdinalIgnoreCase),
            AdminPages = PersonaPages.Parse(string.Join(",", pages)),
            Admitted = AccessRules.MayUseDashboard(level, new HashSet<string>(roles, StringComparer.OrdinalIgnoreCase), 2),
        };

    static List<string> Paths(Viewer v) => Nav.For(v).SelectMany(g => g.Pages).Select(p => p.Path).ToList();

    [Fact]
    public void Persona_is_offered_with_the_fourteen_Admin_pages()
    {
        Assert.Contains(Roles.Persona, Roles.All);
        Assert.False(string.IsNullOrWhiteSpace(Roles.Describe[Roles.Persona]));
        Assert.Equal(14, PersonaPages.Choices.Count);
        Assert.All(PersonaPages.Choices, p => Assert.True(p.Admin && p.Path.StartsWith("/admin/", StringComparison.Ordinal)));
    }

    [Fact]
    public void Only_Admin_pages_are_kept_and_each_once()
    {
        var pages = PersonaPages.Parse(" /admin/health/ ,/ADMIN/Audit,/agents,/admin/health,,/admin/nowhere");
        Assert.Equal(new[] { "/admin/audit", "/admin/health" }, pages.OrderBy(p => p));
        Assert.Equal("/admin/health,/admin/audit", PersonaPages.Format(["/admin/audit", "/admin/health", "/licensing"]));
        Assert.Null(PersonaPages.Format(["/licensing"]));
        Assert.Null(PersonaPages.Format(null));
        Assert.Equal("Health, Audit log", PersonaPages.Names("/admin/audit,/admin/health"));
        Assert.Equal("every Admin page", PersonaPages.Names(string.Join(",", PersonaPages.Choices.Select(p => p.Path))));
        Assert.Equal("no pages", PersonaPages.Names(null));
    }

    [Fact]
    public void A_Persona_opens_only_the_Admin_pages_granted()
    {
        var v = V(3, [Roles.Persona], "/admin/health", "/admin/directory");
        Assert.False(v.IsAdmin);
        Assert.True(v.AnyAdmin);
        Assert.True(v.CanAdmin("/admin/health"));
        Assert.True(v.CanAdmin("/Admin/Directory/"));
        Assert.False(v.CanAdmin("/admin/access"));
        var admin = Paths(v).Where(p => p.StartsWith("/admin/", StringComparison.Ordinal)).ToList();
        Assert.Equal(new[] { "/admin/health", "/admin/directory" }, admin);
    }

    [Fact]
    public void An_administrator_still_opens_every_Admin_page()
    {
        var v = V(3, [Roles.Admin]);
        Assert.All(PersonaPages.Choices, p => Assert.True(v.CanAdmin(p.Path)));
        Assert.Equal(14, Paths(v).Count(p => p.StartsWith("/admin/", StringComparison.Ordinal)));
    }

    [Fact]
    public void Persona_alone_lets_no_one_past_the_dashboards_limit_but_keeps_its_Admin_pages()
    {
        var persona = new HashSet<string>([Roles.Persona], StringComparer.OrdinalIgnoreCase);
        Assert.False(AccessRules.MayUseDashboard(5, persona, 2));
        Assert.True(AccessRules.MayUseDashboard(5, new HashSet<string>([Roles.Persona, Roles.Agents], StringComparer.OrdinalIgnoreCase), 2));
        var v = V(5, [Roles.Persona], "/admin/audit");
        Assert.False(v.Admitted);
        Assert.Equal(new[] { "/admin/audit" }, Paths(v));
    }

    [Fact]
    public void Without_a_Persona_grant_no_Admin_page_opens()
    {
        var v = V(3, [Roles.Agents]);
        Assert.False(v.AnyAdmin);
        Assert.False(v.CanAdmin("/admin/health"));
        Assert.DoesNotContain(Paths(v), p => p.StartsWith("/admin/", StringComparison.Ordinal));
    }

    static string Source(params string[] parts) =>
        File.ReadAllText(Path.Combine([AppContext.BaseDirectory, "..", "..", "..", "..", "CopilotInsights", .. parts]));

    [Fact]
    public void Only_an_administrator_grants_or_removes_Admin_or_Persona()
    {
        var page = Source("Pages", "Admin", "Admin.cs");
        Assert.Contains("if (!Viewer.IsAdmin && roles.Any(Roles.AdminOnly)) throw new ArgumentException(\"only an administrator can grant Admin or Persona.\");", page);
        Assert.Contains("only an administrator can remove Admin or Persona.", page);
        Assert.Contains("only an administrator can remove Admin or Persona, and someone selected holds one.", page);
        /* Admin pages and their changes are checked by the page's own path, not by the Admin role alone. */
        var basePage = Source("Pages", "InsightsPage.cs");
        Assert.Contains("else if (AdminOnly && !Viewer.CanAdmin(Request.Path))", basePage);
        Assert.Contains("if (!Viewer.CanAdmin(Request.Path))", basePage);
    }
}

/// <summary>The Agents pages' Environment and Copilot type filters (2.6.4).</summary>
public class AgentFilterTests
{
    static ReportContext Ctx(string? env, string? type) => new()
    {
        Viewer = new() { WindowsName = @"CONTOSO\me", Upn = "me@contoso.com", Level = 1, InDirectory = true, Roles = new HashSet<string>() },
        Days = 30, Start = new DateTime(2026, 9, 1), End = new DateTime(2026, 9, 30), MinGroupSize = 5, InactiveDays = 30, AgentEnv = env, AgentType = type,
    };

    [Fact]
    public void The_filters_go_with_the_Agents_pages_only()
    {
        var c = Ctx("Production", "Agent Builder");
        Assert.Equal("/agents/shared?d=30&env=Production&ctype=Agent%20Builder", c.Url("/agents/shared"));
        Assert.Equal("/adoption?d=30", c.Url("/adoption"));
        Assert.Equal("/agents?d=30", Ctx(null, null).Url("/agents"));
    }

    [Fact]
    public void The_Copilot_types_offered_are_the_four_agreed()
    {
        Assert.Equal(new[] { "Copilot Studio", "Agent Builder", "Microsoft", "Other" }, ReportContext.AgentTypes.Select(t => t.Value));
        Assert.Contains(ReportContext.AgentTypes, t => t.Label == "Agent Builder (Copilot Studio lite)");
    }

    [Fact]
    public void The_agreed_pages_offer_the_agreed_filters()
    {
        static string Read(params string[] parts) =>
            File.ReadAllText(Path.Combine([AppContext.BaseDirectory, "..", "..", "..", "..", "CopilotInsights", "Pages", .. parts]));
        var both = "public override AgentFilters AgentFilter => AgentFilters.Both;";
        var env = "public override AgentFilters AgentFilter => AgentFilters.Environment;";
        /* Agents in use; inventory, shared and production (one base); owners; checks; credits; hand-offs. */
        Assert.Contains(both, Read("Agents", "Agents.cs"));
        Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(Read("Agents", "Workbook.cs"), System.Text.RegularExpressions.Regex.Escape(both)).Count);
        Assert.Contains(both, Read("Agents", "Credits.cs"));
        Assert.Contains("public override AgentFilters AgentFilter => AgentFilters.Both;", Read("MoreReports.cs"));
        /* Build and publish, and child agent usage (the parent's environment): the environment only. */
        Assert.Contains(env, Read("Agents", "Build.cs"));
        Assert.Contains(env, Read("Agents", "Workbook.cs"));
        /* Agent detail: neither. */
        var detail = Read("Agents", "Agents.cs");
        Assert.DoesNotContain("AgentFilter", detail[detail.IndexOf("class DetailModel", StringComparison.Ordinal)..]);
    }
}
