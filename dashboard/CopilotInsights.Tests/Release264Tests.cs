using CopilotInsights.Data;
using CopilotInsights.Infrastructure;
using CopilotInsights.Pages;
using CopilotInsights.Pages.Agents;
using CopilotInsights.Tests.Review2412A;
using Xunit;

namespace CopilotInsights.Tests;

/// <summary>An Admin grant limited to some of the Admin pages (Admin › Access, 2.6.4).</summary>
public class AdminPagesTests
{
    static HashSet<string> R(params string[] roles) => new(roles, StringComparer.OrdinalIgnoreCase);

    static Viewer V(string[]? roles = null, string? pages = null, bool outside = false) => new()
    {
        WindowsName = @"CONTOSO\me", Upn = "me@contoso.com", Level = outside ? 9 : 4, InDirectory = true, Roles = R(roles ?? []),
        MaxLevel = outside ? 7 : 0, Admitted = AccessRules.MayUseDashboard(outside ? 9 : 4, R(roles ?? []), outside ? 7 : 0),
        AdminPaths = AdminPages.Parse(pages),
    };

    static List<string> AdminPathsShown(Viewer v) =>
        Nav.For(v).Where(g => g.Persona == "Admin").SelectMany(g => g.Pages).Select(p => p.Path).ToList();

    [Fact]
    public void There_are_fifteen_admin_pages_to_choose_from()
    {
        /* 2.8.0 added Access profiles. */
        Assert.Equal(15, AdminPages.Choices.Count);
        Assert.All(AdminPages.Choices, p => Assert.True(p.Admin && p.Path.StartsWith("/admin/")));
        /* Each is a page of its own: the list is what Admin > Access offers as tick boxes. */
        Assert.Equal(15, AdminPages.Choices.Select(p => p.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void The_pages_ticked_are_kept_in_the_navigations_order_and_only_admin_pages()
    {
        Assert.Equal("/admin/health,/admin/audit", AdminPages.Format(["/Admin/Audit/", " /admin/health ", "/licensing/unused", null, "/admin/nothing"]));
        Assert.Equal(["/admin/audit", "/admin/health"], AdminPages.Parse("/admin/health, /ADMIN/AUDIT ,/leadership,").Order());
    }

    [Fact]
    public void None_ticked_and_all_ticked_are_both_the_whole_role()
    {
        Assert.Null(AdminPages.Format(null));
        Assert.Null(AdminPages.Format([]));
        Assert.Null(AdminPages.Format(["/licensing"]));
        Assert.Null(AdminPages.Format(AdminPages.Choices.Select(p => p.Path)));
        Assert.False(AdminPages.Limits(null));
        Assert.False(AdminPages.Limits("  "));
        Assert.True(AdminPages.Limits("/admin/health"));
    }

    [Fact]
    public void The_pages_are_named_as_the_navigation_names_them()
    {
        Assert.Equal("every Admin page", AdminPages.Names(null));
        Assert.Equal("Health, Audit log", AdminPages.Names("/admin/audit,/admin/health"));
        /* A value that names no Admin page limits the grant to nothing: it is not read as the whole role. */
        Assert.Equal("no pages", AdminPages.Names("/somewhere/else"));
        Assert.Empty(AdminPages.Parse("/somewhere/else"));
    }

    [Fact]
    public void A_limited_administrator_sees_and_may_change_only_their_pages()
    {
        var v = V(pages: "/admin/health,/admin/audit");
        Assert.False(v.IsAdmin);
        Assert.True(v.AnyAdmin);
        Assert.True(v.CanAdmin("/admin/health"));
        Assert.True(v.CanAdmin("/Admin/Audit/"));
        Assert.False(v.CanAdmin("/admin/access"));
        Assert.False(v.CanAdmin("/forecasting/budget"));
        Assert.False(v.CanAdmin(null));
        Assert.Equal(["/admin/health", "/admin/audit"], AdminPathsShown(v));
    }

    [Fact]
    public void A_full_administrator_keeps_every_page_and_everyone_else_none()
    {
        var full = V([Roles.Admin]);
        Assert.True(full.IsAdmin && full.AnyAdmin && full.CanAdmin("/admin/access") && full.CanAdmin("/forecasting/budget"));
        Assert.Equal(15, AdminPathsShown(full).Count);
        var none = V([Roles.Licensing]);
        Assert.False(none.AnyAdmin || none.CanAdmin("/admin/health"));
        Assert.Empty(AdminPathsShown(none));
    }

    [Fact]
    public void Outside_the_levels_a_limited_administrator_keeps_their_pages_and_nothing_else()
    {
        var v = V(pages: "/admin/directory", outside: true);
        Assert.False(v.Admitted);
        var groups = Nav.For(v);
        Assert.Equal("/admin/directory", Assert.Single(Assert.Single(groups).Pages).Path);
        Assert.Contains("Admin pages your Admin role covers", InsightsPage.LimitRefusal(v));
    }

    [Fact]
    public void A_limited_grant_adds_pages_and_not_the_admin_role()
    {
        var viewer = Source.Read("Infrastructure", "Viewer.cs");
        /* Since 3.1.0 the grants are read in AccessRules.Resolve, pure, which sign-in and the access check both use. */
        var roles = viewer[viewer.IndexOf("public static GrantedAccess Resolve(", StringComparison.Ordinal)..viewer.IndexOf("public static bool IsPersona(", StringComparison.Ordinal)];
        Assert.Contains("AdminPages.Limits(g.Pages)", roles);
        Assert.Contains("a.Paths.UnionWith(", roles);
        Assert.Contains("AccessRules.Resolve(", viewer[viewer.IndexOf(" RolesForAsync(string? upn", StringComparison.Ordinal)..]);
        /* A database from before 2.6.4 has no Pages column: every Admin grant there is the whole role, and nothing fails. */
        Assert.Contains("CASE WHEN COL_LENGTH(N'dash.RoleGrant', N'Pages') IS NULL THEN N'CAST(NULL AS NVARCHAR(1000))'", viewer);
        /* A whole grant beside a limited one wins. */
        Assert.Contains("AdminPaths = roles.Contains(Roles.Admin) ? new HashSet<string>() : paths", viewer);
        var limited = AccessRules.Resolve([new ViewerService.GrantRow("a", "User", Roles.Admin, "/admin/health", false, null)]);
        Assert.DoesNotContain(Roles.Admin, limited.Roles);
        Assert.Contains("/admin/health", limited.Paths);
    }

    [Fact]
    public void Only_a_full_administrator_grants_limits_or_removes_admin()
    {
        var page = Source.Read("Pages", "Admin", "Admin.cs");
        Assert.Contains("only a full administrator can grant Admin.", page);
        Assert.Contains("only a full administrator can remove Admin.", page);
        Assert.Contains("only a full administrator can remove Admin, and someone selected holds it.", page);
        Assert.Contains("you cannot limit your own Admin role", page);
        /* The Admin tick box is offered to full administrators only. */
        Assert.Contains("(r == Roles.Admin && Viewer.IsAdmin)", page);
    }

    [Fact]
    public void Every_admin_check_goes_by_the_page()
    {
        var page = Source.Read("Pages", "InsightsPage.cs");
        Assert.Contains("AdminOnly && !Viewer.CanAdmin(Request.Path)", page);
        Assert.Contains("if (!Viewer.CanAdmin(Request.Path))", Source.Method(page, "AdminChangeAsync"));
        Assert.Contains("page.Admin ? viewer.CanAdmin(page.Path)", Source.Read("Pages", "Nav.cs"));
        Assert.Contains("!viewer.Admitted && !viewer.AnyAdmin", Source.Read("Program.cs"));
        Assert.Contains("viewer.CanAdmin(\"/admin/\" + name)", Source.Read("Infrastructure", "DataApi.cs"));
    }

    [Fact]
    public void The_schema_adds_the_column_to_tables_already_there()
    {
        var sql = File.ReadAllText(Path.Combine(Repo.Root(), "sql", "Create-CopilotDashboardSchema.sql"));
        Assert.Contains("IF COL_LENGTH(N'dash.RoleGrant', N'Pages') IS NULL ALTER TABLE dash.RoleGrant ADD Pages NVARCHAR(1000) NULL;", sql);
    }
}

/// <summary>Filters on a list of the workbook's agents, each a column of the workbook (Agents › Agent inventory, 2.6.4).</summary>
public class WorkbookFilterTests
{
    [Fact]
    public void The_inventory_offers_created_in_and_a_filter_is_one_line()
    {
        var page = Source.Read("Pages", "OperationsReports.cs");
        Assert.Contains("new(\"createdin\", \"Created in\", \"CreatedIn\"),", page);
        var f = new WorkbookFilter("env", "Environment type", "EnvironmentSku");
        Assert.Equal(("env", "Environment type", "EnvironmentSku"), (f.Key, f.Label, f.Column));
    }

    [Fact]
    public void Only_a_value_the_column_holds_reaches_the_query_and_as_a_parameter()
    {
        var page = Source.Read("Pages", "Agents", "Workbook.cs");
        var load = Source.Method(page, "LoadFiltersAsync");
        Assert.Contains("choices.FirstOrDefault(c => string.Equals(c, asked, StringComparison.OrdinalIgnoreCase))", load);
        /* A column the loaded workbook does not have is not offered, and does not fail the page. */
        Assert.Contains("IF COL_LENGTH(N'dbo.AgentInventory', N'{f.Column}') IS NULL", load);
        Assert.Contains("= @wf{n})", page);
        Assert.Contains("args.Add($\"wf{n}\", chosen)", page);
    }

    [Fact]
    public void A_pages_own_filters_stay_on_its_links_and_on_no_other_pages()
    {
        var all = new HashSet<string>(Roles.All, StringComparer.OrdinalIgnoreCase);
        var v = new Viewer { WindowsName = @"CONTOSO\me", Upn = "me@contoso.com", Level = 1, InDirectory = true, Roles = all };
        var ctx = new ReportContext { Viewer = v, Days = 30, Start = new(2026, 9, 1), End = new(2026, 9, 30), MinGroupSize = 5, InactiveDays = 30, Kept = ("/agents/inventory", "createdin=Copilot%20Studio%20Lite") };
        Assert.Contains("createdin=Copilot%20Studio%20Lite", ctx.Url("/agents/inventory", days: 90));
        Assert.Contains("d=90", ctx.Url("/agents/inventory", days: 90));
        Assert.Contains("createdin=", ctx.Url("/Agents/Inventory"));
        Assert.DoesNotContain("createdin", ctx.Url("/agents/shared"));
        Assert.DoesNotContain("createdin", new ReportContext { Viewer = v, Days = 30, Start = new(2026, 9, 1), End = new(2026, 9, 30), MinGroupSize = 5, InactiveDays = 30 }.Url("/agents/inventory"));
    }
}

/// <summary>The checkout's root: the folder that holds sql, docs and dashboard.</summary>
static class Repo
{
    public static string Root()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (Directory.Exists(Path.Combine(d.FullName, "sql")) && Directory.Exists(Path.Combine(d.FullName, "docs")) && Directory.Exists(Path.Combine(d.FullName, "dashboard"))) return d.FullName;
        throw new DirectoryNotFoundException("the checkout's root was not found above the tests");
    }
}