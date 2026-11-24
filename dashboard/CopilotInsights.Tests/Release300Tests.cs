using System.Text.RegularExpressions;
using CopilotInsights.Data;
using CopilotInsights.Infrastructure;
using Governance.Web.Infrastructure;
using Xunit;
using AppRoles = Governance.Domain.AppRoles;

namespace CopilotInsights.Tests;

/// <summary>3.0.0: the Governance Workbench served by Copilot Insights, under /workbench, with Insights' sign-in and roles.</summary>
public class Release300Tests
{
    static string Root()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(d.FullName, "CHANGELOG.md"))) d = d.Parent!;
        return d.FullName;
    }

    static string Read(params string[] p) => File.ReadAllText(Path.Combine([Root(), .. p]));

    static IEnumerable<string> WorkbenchFiles(string pattern) =>
        Directory.GetFiles(Path.Combine(Root(), "dashboard", "CopilotInsights", "Pages", "Workbench"), pattern, SearchOption.AllDirectories);

    static HashSet<string> Set(params string[] r) => r.ToHashSet(StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void Insights_grants_give_the_Workbench_page_roles()
    {
        /* Since 3.1.0 the two everyday sections need a grant like every section: Insights roles alone open none of them. */
        Assert.Empty(PrincipalLoader.PageRoles(Set(Roles.Licensing, Roles.Organization)));
        Assert.Equal(Set(AppRoles.User), PrincipalLoader.PageRoles(Set(Roles.Licensing), Set(CopilotInsights.Infrastructure.Sections.WorkbenchOwnership)));
        Assert.Equal(Set(AppRoles.Registry), PrincipalLoader.PageRoles(Set(), Set(CopilotInsights.Infrastructure.Sections.WorkbenchRegistry)));
        /* The central roles and Admin open both, since their work runs through them; the auditor its reports only. */
        Assert.Equal(Set(AppRoles.User, AppRoles.Registry, AppRoles.Central), PrincipalLoader.PageRoles(Set(Roles.Governance)));
        Assert.Equal(Set(AppRoles.User, AppRoles.Registry, AppRoles.Central, AppRoles.Operator), PrincipalLoader.PageRoles(Set(Roles.GovernanceOperator)));
        Assert.Equal(Set(AppRoles.Auditor), PrincipalLoader.PageRoles(Set(Roles.GovernanceAuditor)));
        Assert.Equal(Set(AppRoles.User, AppRoles.Registry, AppRoles.Admin), PrincipalLoader.PageRoles(Set(Roles.Admin)));
    }

    [Fact]
    public void Workbench_roles_open_no_usage_data()
    {
        foreach (var r in Roles.Workbench)
        {
            Assert.Contains(r, Roles.All);
            Assert.False(string.IsNullOrWhiteSpace(Roles.Describe[r]));
            Assert.False(AccessRules.IsPersona(r));                       // never widens to the whole organization
            Assert.False(AccessRules.SeesWholeOrganization(9, Set(r)));
            Assert.False(AccessRules.MayUseDashboard(9, Set(r), 2));        // never past the level limit
        }
        Assert.True(AccessRules.MayUseDashboard(9, Set(Roles.Governance, Roles.Licensing), 2));
    }

    [Fact]
    public void Every_Workbench_address_starts_with_workbench()
    {
        var page = new Regex(@"^@page ""(?<route>[^""]+)""", RegexOptions.Multiline);
        foreach (var f in WorkbenchFiles("*.cshtml"))
        {
            var text = File.ReadAllText(f);
            if (page.Match(text) is { Success: true } m) Assert.StartsWith("/workbench/", m.Groups["route"].Value);
            // A link to another Workbench page names /workbench; nothing points at the site root's old Workbench addresses.
            foreach (Match h in Regex.Matches(text, @"(?:href|action)=""(/[^""]*)"""))
                Assert.True(h.Groups[1].Value.StartsWith("/workbench/", StringComparison.Ordinal), $"{Path.GetFileName(f)}: {h.Groups[1].Value}");
        }
        foreach (var f in WorkbenchFiles("*.cs"))
            foreach (Match r in Regex.Matches(File.ReadAllText(f), @"Redirect\(\$?""(/[^""]*)"""))
                Assert.StartsWith("/workbench/", r.Groups[1].Value);
    }

    [Fact]
    public void Notices_link_to_workbench_addresses()
    {
        var src = Path.Combine(Root(), "governance", "src");
        foreach (var f in Directory.GetFiles(src, "*.cs", SearchOption.AllDirectories).Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")))
            foreach (Match m in Regex.Matches(File.ReadAllText(f), @"LinkPath\s*=\s*\$?""(/[^""]*)"""))
                Assert.StartsWith("/workbench/", m.Groups[1].Value);
        Assert.DoesNotContain("/Operations/Drift", string.Concat(Directory.GetFiles(src, "*.cs", SearchOption.AllDirectories).Select(File.ReadAllText)));
    }

    [Fact]
    public void Off_unless_its_database_is_named_and_then_its_addresses_are_not_found()
    {
        var setup = Read("dashboard", "CopilotInsights", "Workbench", "WorkbenchSetup.cs");
        Assert.Contains("!string.IsNullOrWhiteSpace(cfg.GetConnectionString(\"Governance\"))", setup);
        Assert.Contains("ctx.Response.StatusCode = StatusCodes.Status404NotFound", setup);
        Assert.True(WorkbenchSetup.IsWorkbenchPath("/workbench/Start"));
        Assert.True(WorkbenchSetup.IsWorkbenchPath("/Workbench/Registry/Seats"));
        Assert.False(WorkbenchSetup.IsWorkbenchPath("/governance/ai-apps"));
        Assert.False(WorkbenchSetup.IsWorkbenchPath("/workbenches"));
        Assert.Empty(WorkbenchNav.For(new WebPrincipal { IsAuthenticated = true }, on: false));
    }

    [Fact]
    public void One_menu_shows_each_person_the_Workbench_sections_their_roles_open()
    {
        static IReadOnlyList<string> Sections(params string[] appRoles) =>
            WorkbenchNav.For(new WebPrincipal { IsAuthenticated = true, AppRoles = Set([.. appRoles]) }, on: true).Select(g => g.Persona).ToList();
        /* Nothing granted: no Workbench section; each everyday section by its own grant. */
        Assert.Empty(Sections());
        Assert.Equal(["Ownership and approvals"], Sections(AppRoles.User));
        Assert.Equal(["Ownership registry"], Sections(AppRoles.Registry));
        Assert.Equal(["Ownership and approvals", "Ownership registry"], Sections(AppRoles.User, AppRoles.Registry));
        Assert.Equal(["Governance operations"], Sections(AppRoles.Auditor));
        Assert.Equal(["Ownership and approvals", "Approval queues", "Ownership registry", "Governance operations"], Sections(AppRoles.Central));
        Assert.Contains("Workbench settings", Sections(AppRoles.Admin));
        var central = WorkbenchNav.For(new WebPrincipal { IsAuthenticated = true, AppRoles = Set(AppRoles.User, AppRoles.Central) }, true).SelectMany(g => g.Pages).ToList();
        Assert.DoesNotContain(central, p => p.Path.EndsWith("/Operations/Sync", StringComparison.Ordinal));   // operators only
        // Every page in the menu says what it is for, and its section is not one of Insights' own.
        var all = WorkbenchNav.For(new WebPrincipal { IsAuthenticated = true, AppRoles = Set(AppRoles.User, AppRoles.Central, AppRoles.Operator, AppRoles.Auditor, AppRoles.Admin) }, true);
        foreach (var p in all.SelectMany(g => g.Pages)) Assert.False(string.IsNullOrWhiteSpace(CopilotInsights.Pages.NavText.What(p)), p.Path);
        Assert.Empty(all.Select(g => g.Persona).Intersect(CopilotInsights.Pages.Nav.Groups.Select(g => g.Persona)));
        // The layout serves both kinds of page.
        var layout = Read("dashboard", "CopilotInsights", "Pages", "Shared", "_Layout.cshtml");
        Assert.Contains("WorkbenchNav.ForAsync(Context, WorkbenchMe, Workbench.On)", layout);
        Assert.Contains("<div class=\"wb\">", layout);
    }

    [Fact]
    public void Workbench_pages_use_the_site_layout_and_no_inline_script()
    {
        Assert.Contains("/Pages/Shared/_Layout.cshtml", Read("dashboard", "CopilotInsights", "Pages", "Workbench", "_ViewStart.cshtml"));
        Assert.False(File.Exists(Path.Combine(Root(), "dashboard", "CopilotInsights", "Pages", "Workbench", "Shared", "_Layout.cshtml")));
        foreach (var f in WorkbenchFiles("*.cshtml"))
            Assert.DoesNotMatch(@"<script(?![^>]*\bsrc=)", File.ReadAllText(f));
        // The style sheet styles the Workbench's content only, never the site's header or menu.
        var css = Read("dashboard", "CopilotInsights", "wwwroot", "workbench.css");
        foreach (Match rule in Regex.Matches(Regex.Replace(css, @"/\*.*?\*/", "", RegexOptions.Singleline), @"(?m)^\s*([^@\s{}][^{}]*)\{"))
            foreach (var selector in rule.Groups[1].Value.Split(','))
                Assert.StartsWith(".wb", selector.Trim());
    }

    [Fact]
    public void A_refused_Workbench_page_explains_itself_and_Teams_alone_may_frame_its_tab()
    {
        var program = Read("dashboard", "CopilotInsights", "Program.cs");
        Assert.Contains("WorkbenchStatusPages.UseWorkbenchStatusPages(app)", program);
        Assert.Contains("frame-ancestors https://teams.microsoft.com", program);
        Assert.Contains("if (!teams) h[\"X-Frame-Options\"] = \"DENY\";", program);
        Assert.Contains("an administrator grants in Admin › Access", Read("dashboard", "CopilotInsights", "Pages", "Workbench", "AccessDenied.cshtml"));
    }

    [Fact]
    public void Access_profiles_offer_the_Workbench_starters_only_when_it_is_set_up()
    {
        static IReadOnlyList<string> Names(bool on) => AccessProfiles.Proposals([], [], [], on).Select(p => p.Name).ToList();
        Assert.DoesNotContain("Governance team", Names(false));
        Assert.Contains("Governance team", Names(true));
        Assert.Contains("Governance auditor", Names(true));
        Assert.Contains("(workbenchOn || !Roles.IsWorkbench(r))", Read("dashboard", "CopilotInsights", "Pages", "Admin", "Admin.cs"));
        Assert.Contains("(workbenchOn || !Roles.IsWorkbench(r))", Read("dashboard", "CopilotInsights", "Pages", "Admin", "Profiles.cs"));
    }

    [Fact]
    public void The_role_checks_take_the_Workbench_roles_on_new_and_existing_databases()
    {
        var sql = Read("sql", "Create-CopilotDashboardSchema.sql");
        /* Widened since: Restricted (3.0.3, withdrawn before use and still accepted) and Sections (3.1.0). */
        Assert.Contains("definition NOT LIKE N'%''Sections''%'", sql);
        Assert.Equal(2, Regex.Matches(sql, "DROP CONSTRAINT CK_Dash(RoleGrant|AccessProfileRole)_Role;\\s+ALTER TABLE dash\\.\\w+ ADD CONSTRAINT CK_Dash\\w+_Role CHECK \\(Role IN\\s+\\('Admin', 'Organization', 'Adoption', 'Licensing', 'Security', 'Compliance', 'Agents', 'Enablement', 'Governance', 'GovernanceOperator', 'GovernanceAuditor', 'Restricted', 'Sections'\\)\\);\\s+END;").Count);
    }

    [Fact]
    public void Under_Entra_sign_in_the_Workbench_inventory_read_can_ask_for_the_operator_token()
    {
        Assert.Contains("if (workbench) web.EnableTokenAcquisitionToCallDownstreamApi([]).AddInMemoryTokenCaches();", Read("dashboard", "CopilotInsights", "Infrastructure", "EntraSignIn.cs"));
        Assert.Contains("WorkbenchSetup.Enabled(builder.Configuration)", Read("dashboard", "CopilotInsights", "Program.cs"));
        // Nothing points at the old standalone Workbench's port.
        foreach (var f in new[] { "governance/src/Governance.Infrastructure/Plumbing.cs", "governance/src/Governance.Notifications/EmailRenderer.cs", "governance/src/Governance.Worker/appsettings.json" })
            Assert.DoesNotContain("5450", Read(f.Split('/')));
    }

    [Fact]
    public void The_seat_read_prefers_Copilot_Insights_activity()
    {
        Assert.Contains("FROM dash.UserDay AS d JOIN bi.DimUser AS u", Governance.Application.Integrations.InsightsActivitySource.Sql);
        var setup = Read("dashboard", "CopilotInsights", "Workbench", "WorkbenchSetup.cs");
        Assert.Contains("new Governance.Application.Integrations.InsightsActivitySource(insights)", setup);
    }

    [Fact]
    public void Check_someones_access_names_the_Workbench_pages_and_roles()
    {
        var pages = WorkbenchAccess.PagesOpened(PrincipalLoader.PageRoles(Set(Roles.GovernanceOperator)));
        Assert.Contains(pages, p => p.Contains("approval queues"));
        Assert.Contains(pages, p => p.Contains("sync and freshness"));
        Assert.DoesNotContain(WorkbenchAccess.PagesOpened(PrincipalLoader.PageRoles(Set())), p => p.Contains("settings"));
        Assert.Contains("WorkbenchAccess.DescribeAsync(HttpContext.RequestServices, upn, roles", Read("dashboard", "CopilotInsights", "Pages", "Admin", "Admin.cs"));
    }

    [Fact]
    public void Review_the_Workbench_belongs_to_one_tenant_and_its_forms_need_no_tenant()
    {
        Assert.Contains("WorkbenchSetup.SelectTenantAsync(ctx, () => next())", Read("dashboard", "CopilotInsights", "Program.cs"));
        var setup = Read("dashboard", "CopilotInsights", "Workbench", "WorkbenchSetup.cs");
        Assert.Contains("tenants.Use(ctx.RequestServices.GetRequiredService<WorkbenchState>().Home(tenants));", setup);
        Assert.Contains("new WorkbenchState(on, cfg[\"Workbench:Tenant\"])", setup);
        var nav = Read("dashboard", "CopilotInsights", "Workbench", "WorkbenchNav.cs");
        Assert.Contains("if (!ctx.RequestServices.GetRequiredService<WorkbenchState>().OnFor(tenants)) return [];", nav);
        Assert.Contains("tenants.Current.Name", nav);                      // the menu cache key names the tenant
        Assert.Contains("PrincipalLoader.ActAsCookie", nav);               // and the test persona
        Assert.Contains("LoadAsync(ctx, me, create: false)", nav);         // an Insights page records no one
        Assert.DoesNotContain("groups = [];", nav);                        // a failure is not kept for two minutes
    }

    [Fact]
    public void Review_a_person_is_their_object_ID_or_UPN_and_a_reused_UPN_is_someone_else()
    {
        var p = Read("dashboard", "CopilotInsights", "Workbench", "Principal.cs");
        Assert.Contains("if (oid is null && string.IsNullOrWhiteSpace(upn))", p);
        Assert.DoesNotContain("viewer.Upn ?? viewer.WindowsName", p);
        Assert.Contains("objectId is null ? byUpn.FirstOrDefault() : byUpn.FirstOrDefault(x => x.EntraObjectId is null)", p);
        Assert.Contains("WITH (UPDLOCK, HOLDLOCK)", p);
        Assert.Contains("Insights:IdentityMap", Read("dashboard", "CopilotInsights", "Pages", "Workbench", "AccessDenied.cshtml"));
    }

    [Fact]
    public void Review_Teams_deep_links_open_their_item_and_nothing_else()
    {
        var id = Guid.NewGuid();
        Assert.Equal($"/workbench/Requests/{id}", Governance.Web.Pages.TeamsLaunchModel.Resolve($"/workbench/Requests/{id}"));
        Assert.Equal($"/workbench/Registry/Seats/{id}", Governance.Web.Pages.TeamsLaunchModel.Resolve($"/workbench/Registry/Seats/{id}"));
        Assert.Equal($"/workbench/Requests/{id}", Governance.Web.Pages.TeamsLaunchModel.Resolve(id.ToString()));
        Assert.Equal("/workbench/Start", Governance.Web.Pages.TeamsLaunchModel.Resolve("https://example.com/workbench/Start"));
        Assert.Equal("/workbench/Start", Governance.Web.Pages.TeamsLaunchModel.Resolve("//example.com/x"));
        Assert.Contains("~/workbench.css", Read("dashboard", "CopilotInsights", "Pages", "Workbench", "Teams", "Launch.cshtml"));
    }

    [Fact]
    public void Review_a_refused_download_returns_to_its_Workbench_page_only()
    {
        Assert.Equal("/workbench/Requests/x?a=1", Governance.Web.Pages.EvidenceModel.Back("/workbench/Requests/x?a=1"));
        Assert.Equal("/workbench/Start", Governance.Web.Pages.EvidenceModel.Back("https://evil.example/workbench/"));
        Assert.Equal("/workbench/Start", Governance.Web.Pages.EvidenceModel.Back("//evil.example"));
        Assert.Equal("/workbench/Start", Governance.Web.Pages.EvidenceModel.Back("/leadership"));
        Assert.Equal("/workbench/Start", Governance.Web.Pages.EvidenceModel.Back(null));
    }

    [Fact]
    public void Review_a_virtual_directory_named_workbench_is_a_clash()
    {
        Assert.True(CopilotInsights.Infrastructure.PathBaseLinks.Clashes("/workbench"));
    }

    [Fact]
    public void Review_seats_for_every_line_only_where_the_page_shows_the_whole_organization()
    {
        Assert.Contains("var whole = Ctx.Root is null && (Viewer.OrgWide || Viewer.Has(Roles.Licensing));", Read("dashboard", "CopilotInsights", "Pages", "Leadership", "Reservations.Workbench.cs"));
    }

    [Fact]
    public void Review_Workbench_roles_are_refused_on_the_server_while_it_is_off()
    {
        Assert.Contains("Roles.All.Contains(r) && r != Roles.Sections && (workbenchOn || !Roles.IsWorkbench(r))", Read("dashboard", "CopilotInsights", "Pages", "Admin", "Admin.cs"));
        Assert.Contains(".Where(r => r != Roles.Sections && (workbenchOn || !Roles.IsWorkbench(r)))", Read("dashboard", "CopilotInsights", "Pages", "Admin", "Profiles.cs"));
        /* The Workbench's sections too. */
        Assert.Contains("Where(k => workbenchOn || Sections.Find(k)?.Workbench != true)", Read("dashboard", "CopilotInsights", "Pages", "Admin", "Admin.cs"));
    }

    [Fact]
    public void Review_help_text_never_makes_a_page_wait_for_the_database()
    {
        var web = Read("dashboard", "CopilotInsights", "Workbench", "Web.cs");
        Assert.Contains("_ = Task.Run(async () => { try { await ReloadAsync(); }", web);
        Assert.DoesNotContain(".GetAwaiter().GetResult();\n            _overrides", web);
    }
}