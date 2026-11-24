using CopilotInsights.Data;
using CopilotInsights.Infrastructure;
using CopilotInsights.Pages;
using Governance.Web.Infrastructure;
using CopilotInsights.Pages.Admin;
using CopilotInsights.Tests.Review2412A;
using Xunit;
using AppRoles = Governance.Domain.AppRoles;

namespace CopilotInsights.Tests;

/// <summary>
/// 3.1.0: every section of the menu needs a grant that opens it. A Sections grant names the sections, whole organization
/// or the holder's own team, and whether the lists that name people show; persona roles granted before keep their own
/// sections; levels decide how much a section shows, never whether it shows.
/// </summary>
public class Release310Tests
{
    static ViewerService.GrantRow Grant(string role, string? sections = null, bool wide = false, bool named = false, int? profile = null, string who = "me@contoso.com") =>
        new(who, "User", role, null, wide, profile, sections, named);

    static Viewer V(int? level, GrantedAccess a, int max = 0) => new()
    {
        WindowsName = @"CONTOSO\me", Upn = "me@contoso.com", Level = level, InDirectory = true, Roles = a.Roles, MaxLevel = max,
        Admitted = AccessRules.MayUseDashboard(level, a.Roles, max), SectionKeys = a.Sections, OrgWideSections = a.OrgWideSections, OrgWideRoles = a.OrgWideRoles,
    };

    static List<string> Personas(Viewer v) => Nav.For(v).Select(g => g.Persona).ToList();

    /// <summary>A file of the repository, by its path from the root.</summary>
    static string Repo(params string[] path)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine([d.FullName, .. path]))) return File.ReadAllText(Path.Combine([d.FullName, .. path]));
        throw new FileNotFoundException(string.Join('/', path));
    }

    [Fact]
    public void Someone_at_level_7_with_nothing_granted_sees_nothing()
    {
        /* The dashboard open to levels 1 to 6: level 7 is not let in, and with nothing granted sees nothing either way. */
        var none = AccessRules.Resolve([]);
        Assert.Empty(Nav.For(V(7, none, max: 6)));
        Assert.False(V(7, none, max: 6).SeesAny);
        /* Inside the levels, nothing granted is still nothing: no Leadership, Regions, People, App owners or Governance. */
        Assert.Empty(Nav.For(V(3, none, max: 6)));
        Assert.Empty(Nav.For(V(1, none)));
        /* Granted, level 7 sees exactly what was ticked, for their own team. */
        var some = AccessRules.Resolve([Grant(Roles.Sections, "leadership,agents")]);
        var v = V(7, some, max: 6);
        Assert.True(v.Admitted);
        Assert.Equal(["Leadership", "Agents"], Personas(v));
        Assert.False(v.OrgWide);
        Assert.False(v.ForSection(Sections.Leadership).OrgWide);
    }

    [Fact]
    public void A_section_grant_opens_its_sections_and_nothing_else()
    {
        var a = AccessRules.Resolve([Grant(Roles.Sections, "regions,licensing,nonsense")]);
        Assert.Equal(new[] { Sections.Licensing, Sections.Regions }.Order(), a.Sections.Order());
        Assert.Contains(Roles.Sections, a.Roles);
        /* Without named people: the Licensing section's figures, none of its lists that name people. */
        Assert.DoesNotContain(Roles.Licensing, a.Roles);
        var paths = Nav.For(V(4, a)).SelectMany(g => g.Pages).Select(p => p.Path).ToList();
        Assert.Contains("/licensing", paths);
        Assert.DoesNotContain("/licensing/unused", paths);
        Assert.DoesNotContain("/forecasting", paths);
        /* With named people: the lists of the ticked sections only. */
        var named = AccessRules.Resolve([Grant(Roles.Sections, "licensing", named: true)]);
        Assert.Contains("/licensing/unused", Nav.For(V(4, named)).SelectMany(g => g.Pages).Select(p => p.Path));
        /* A grant that ticks nothing gives nothing, and does not let anyone in. */
        Assert.Empty(AccessRules.Resolve([Grant(Roles.Sections, "")]).Roles);
    }

    [Fact]
    public void Roles_granted_before_keep_their_own_sections()
    {
        Assert.Equal([Sections.Licensing, Sections.Forecasting], Sections.OfRole(Roles.Licensing));
        Assert.Equal([Sections.Adoption], Sections.OfRole(Roles.Adoption));
        Assert.Empty(Sections.OfRole(Roles.Organization));
        Assert.Empty(Sections.OfRole(Roles.Admin));
        Assert.Empty(Sections.OfRole(Roles.Governance));
        var a = AccessRules.Resolve([Grant(Roles.Licensing, wide: true), Grant(Roles.Agents)]);
        Assert.Equal(["Licensing", "Agents", "Forecasting"], Personas(V(5, a)));
        Assert.True(V(5, a).ForSection(Sections.Forecasting).OrgWide);
        Assert.False(V(5, a).ForSection(Sections.Agents).OrgWide);
        /* Restricted, withdrawn before use, gives nothing. */
        Assert.Empty(AccessRules.Resolve([Grant("Restricted")]).Roles);
        Assert.Empty(AccessRules.Resolve([Grant("Restricted")]).Sections);
    }

    [Fact]
    public void Whole_organization_on_a_section_grant_widens_those_sections_only()
    {
        var a = AccessRules.Resolve([Grant(Roles.Sections, "leadership", wide: true), Grant(Roles.Sections, "adoption", who: @"CONTOSO\team")]);
        var v = V(6, a);
        Assert.False(v.OrgWide);
        Assert.True(v.ForSection(Sections.Leadership).OrgWide);
        Assert.False(v.ForSection(Sections.Adoption).OrgWide);
        /* Outside the directory, a section granted for the whole organization is listed; one for their own team is not. */
        var outside = new Viewer { WindowsName = @"CONTOSO\svc", Roles = a.Roles, SectionKeys = a.Sections, OrgWideSections = a.OrgWideSections, InDirectory = false };
        Assert.Equal(["Leadership"], Nav.For(outside).Select(g => g.Persona));
        /* Levels 1 and 2 see the whole organization in whatever they are granted, as before. */
        Assert.True(V(2, AccessRules.Resolve([Grant(Roles.Sections, "adoption")])).OrgWide);
        /* Organization opens no section by itself. */
        var org = AccessRules.Resolve([Grant(Roles.Organization)]);
        Assert.Empty(Nav.For(V(9, org)));
        Assert.True(V(9, org).OrgWide);
    }

    [Fact]
    public void The_keys_are_stored_in_the_menus_order_and_read_back()
    {
        Assert.Equal("leadership,licensing,wb-ownership", Sections.Format(["wb-ownership", "LICENSING", "leadership", "licensing", "bogus", null]));
        Assert.Null(Sections.Format([]));
        Assert.Equal([Sections.Leadership, Sections.Licensing], Sections.Parse(" licensing , leadership "));
        Assert.Equal("Leadership, Ownership and approvals", Sections.Names("wb-ownership,leadership"));
        /* Every Insights section of the menu has its key, in the menu's order; the Workbench's two come last. */
        Assert.Equal(Nav.Groups.Where(g => g.Key is not null).Select(g => g.Persona), Sections.All.Where(s => !s.Workbench).Select(s => s.Name));
        Assert.Equal([Sections.WorkbenchOwnership, Sections.WorkbenchRegistry], Sections.All.Where(s => s.Workbench).Select(s => s.Key));
        /* A section's named lists need the role its pages ask for. */
        foreach (var g in Nav.Groups.Where(g => g.Key is not null))
            foreach (var p in g.Pages.Where(p => p.Role is not null))
                Assert.Equal(p.Role, Sections.Find(g.Key)!.NamedRole);
    }

    [Fact]
    public void The_Workbench_everyday_sections_need_a_grant_or_a_central_role()
    {
        static HashSet<string> S(params string[] s) => s.ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Empty(PrincipalLoader.PageRoles(S()));
        Assert.Equal(S(AppRoles.User, AppRoles.Registry), PrincipalLoader.PageRoles(S(Roles.Sections), S(Sections.WorkbenchOwnership, Sections.WorkbenchRegistry)));
        var p = new WebPrincipal { IsAuthenticated = true, AppRoles = S(AppRoles.User) };
        Assert.True(p.SeesOwnership);
        Assert.False(p.SeesRegistry);
        Assert.True(new WebPrincipal { IsAuthenticated = true, AppRoles = S(AppRoles.Central) }.SeesRegistry);
        /* The pages are refused on the server, not only left out of the menu. */
        var setup = Source.Read("Workbench", "WorkbenchSetup.cs");
        Assert.Contains("o.Conventions.AuthorizeFolder($\"{PagesFolder}/Registry\", Policies.Registry);", setup);
        Assert.Contains("o.Conventions.AuthorizeFolder($\"{PagesFolder}/Requests\", Policies.Ownership);", setup);
        Assert.Contains("\"Start\", \"MyWork\", \"Me\", \"Recertification\", \"Exceptions\", \"Retirement\", \"Evidence\"", setup);
    }

    [Fact]
    public void Profiles_carry_sections_and_read_them_back()
    {
        var exec = AccessProfiles.SectionsOf(true, false, Sections.Leadership);
        var bundle = new List<ProfileRole> { exec, new(Roles.Admin, Pages: "/admin/health") };
        Assert.Equal(AccessProfiles.Key(bundle), AccessProfiles.Key(AccessProfiles.FromKey(AccessProfiles.Key(bundle))));
        Assert.Equal("Sections*[leadership]", AccessProfiles.Key([exec]));
        Assert.Equal("Leadership for the whole organization", AccessProfiles.Describe([exec]));
        Assert.Equal("Licensing, Forecasting for their own team, with named people",
            AccessProfiles.Describe([AccessProfiles.SectionsOf(false, true, Sections.Forecasting, Sections.Licensing)]));
        /* Two Sections rows become one, their sections joined. */
        var merged = AccessProfiles.Normalize([AccessProfiles.SectionsOf(false, false, Sections.Agents), AccessProfiles.SectionsOf(false, true, Sections.Adoption)]);
        Assert.Equal("adoption,agents", Assert.Single(merged).Sections);
        Assert.True(merged[0].NamedPeople);
        Assert.Contains(AccessProfiles.Starters, s => s.Name == "Executive");
        Assert.Contains(AccessProfiles.WorkbenchStarters, s => s.Name == "Line-of-business owner");
    }

    [Fact]
    public void The_data_api_digests_and_the_manager_digest_need_their_section()
    {
        var api = Source.Read("Infrastructure", "DataApi.cs");
        Assert.Contains("!viewer.Sees(section)", api);
        Assert.Contains("GetAsync()).ForSection(section);", api);
        Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(api, @"\}, section: Sections\.Leadership\)\);").Count);   // usage, units, value
        var delivery = Source.Read("Infrastructure", "Delivery.cs");
        Assert.Contains("(await ManagerAccessAsync(grantMap, graph, m.Upn)).Sections.Contains(Sections.Leadership)", delivery);
        /* The pre-cache builds every report: its stand-in viewer holds every section. */
        Assert.Contains("SectionKeys = new HashSet<string>(Sections.Keys, StringComparer.OrdinalIgnoreCase)", Source.Read("Infrastructure", "PageWarmer.cs"));
    }

    [Fact]
    public void The_grant_map_reads_ad_groups_as_the_directory_load_records_them()
    {
        var map = Source.Read("Infrastructure", "GrantMap.cs");
        Assert.Contains("SELECT UserPrincipalName, GroupName FROM audit.GroupMember WHERE GroupName IN @chunk;", map);
        Assert.Contains("AccessRules.Resolve(Held(upn, entraGroups)", map);
    }

    [Fact]
    public void Admin_access_grants_sections_and_says_what_changed()
    {
        var page = Source.Read("Pages", "Admin", "Admin.cs");
        Assert.Contains("SectionPicker.Boxes(ticked, workbenchOn)", page);
        Assert.Contains("'s sections were {Say(before[0].Sections, before[0].OrgWide, before[0].NamedPeople)}; they are now {Say(sections, orgWide, named)}.", page);
        Assert.Contains("tick at least one section, role or beta report, or choose a profile.", page);
        Assert.Contains("async Task WhoLosesAccessAsync(int max)", page);
        Assert.Contains("<h3>Sections they see</h3>", page);
        /* Every box carries its information icon, naming the reports behind it. */
        var boxes = SectionPicker.Boxes(new HashSet<string> { Sections.Leadership }, workbenchOn: true);
        Assert.Equal(Sections.All.Count, System.Text.RegularExpressions.Regex.Matches(boxes, "name=\"section\"").Count);
        Assert.Equal(Sections.All.Count, System.Text.RegularExpressions.Regex.Matches(boxes, "class=\"infob\"").Count);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(boxes, " checked"));
        Assert.DoesNotContain("wb-ownership", SectionPicker.Boxes(new HashSet<string>(), workbenchOn: false));
        Assert.Contains("Lists that name people, shown with Named people ticked: Unused licenses", SectionPicker.Tip(Sections.Find(Sections.Licensing)!));
    }

    [Fact]
    public void The_schema_adds_the_section_columns_and_the_role_value_only()
    {
        var sql = Repo("sql", "Create-CopilotDashboardSchema.sql");
        Assert.Contains("IF COL_LENGTH(N'dash.RoleGrant', N'Sections') IS NULL ALTER TABLE dash.RoleGrant ADD Sections NVARCHAR(1000) NULL;", sql);
        Assert.Contains("ALTER TABLE dash.RoleGrant ADD NamedPeople BIT NOT NULL CONSTRAINT DF_DashRoleGrant_NamedPeople DEFAULT (0);", sql);
        Assert.Contains("IF COL_LENGTH(N'dash.AccessProfileRole', N'Sections') IS NULL ALTER TABLE dash.AccessProfileRole ADD Sections NVARCHAR(1000) NULL;", sql);
        Assert.Contains("ALTER TABLE dash.AccessProfileRole ADD NamedPeople BIT NOT NULL CONSTRAINT DF_DashAccessProfileRole_NamedPeople DEFAULT (0);", sql);
    }

    [Fact]
    public void The_start_page_says_when_nothing_is_granted()
    {
        Assert.Contains("NoAccess = !Viewer.SeesAny && !Viewer.AnyAdmin && !Viewer.Roles.Any(Roles.IsWorkbench);", Source.Read("Pages", "Index.cshtml.cs"));
        Assert.Contains("<b>No access yet.</b>", Source.Read("Pages", "Index.cshtml"));
    }
}
