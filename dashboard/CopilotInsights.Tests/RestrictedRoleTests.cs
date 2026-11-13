using CopilotInsights.Infrastructure;
using CopilotInsights.Pages;
using Xunit;

namespace CopilotInsights.Tests;

/// <summary>
/// The Restricted role: its holder sees Start, Leadership and the sections their other roles open, nothing else; a
/// restricted viewer who sees the whole organization may choose to see all reports. Nobody else is changed.
/// </summary>
public class RestrictedRoleTests
{
    static Viewer V(int? level = 5, bool seeAll = false, IEnumerable<string>? orgWideRoles = null, params string[] roles) => new()
    {
        WindowsName = "C\\x", Upn = "x@contoso.com", InDirectory = true, Level = level,
        Roles = new HashSet<string>(roles, StringComparer.OrdinalIgnoreCase), SeeAllChosen = seeAll,
        OrgWideRoles = new HashSet<string>(orgWideRoles ?? [], StringComparer.OrdinalIgnoreCase),
    };

    static string[] Sections(Viewer v) => Nav.For(v).Select(g => g.Persona).ToArray();

    [Fact]
    public void Restricted_with_Agents_sees_Leadership_and_Agents_only()
    {
        Assert.Equal(["Leadership", "Agents"], Sections(V(roles: [Roles.Restricted, Roles.Agents])));
    }

    [Fact]
    public void Restricted_alone_sees_Leadership_only()
    {
        Assert.Equal(["Leadership"], Sections(V(roles: [Roles.Restricted])));
    }

    [Fact]
    public void Without_Restricted_nothing_changes()
    {
        var plain = Sections(V(roles: [Roles.Agents]));
        Assert.Contains("Regions", plain);
        Assert.Contains("People", plain);
        Assert.Contains("App owners", plain);
        Assert.Contains("Governance", plain);
        Assert.Contains("Agents", plain);
    }

    [Theory]
    [InlineData("/regions")]
    [InlineData("/regions/patterns")]
    [InlineData("/workforce")]
    [InlineData("/workforce/managers")]
    [InlineData("/apps")]
    [InlineData("/apps/detail")]
    [InlineData("/governance")]
    [InlineData("/governance/ai-apps")]
    public void Restricted_is_refused_the_unguarded_sections_at_their_address(string path)
    {
        var v = V(roles: [Roles.Restricted, Roles.Agents]);
        Assert.False(Nav.SectionOpen(path, v));
        Assert.NotNull(Nav.RestrictedOut(path, v));
        /* Everyone else still opens them. */
        Assert.True(Nav.SectionOpen(path, V(roles: [Roles.Agents])));
        Assert.Null(Nav.RestrictedOut(path, V(roles: [Roles.Agents])));
    }

    [Theory]
    [InlineData("/leadership")]
    [InlineData("/leadership/act")]
    [InlineData("/leadership/boardpack")]
    [InlineData("/agents")]
    [InlineData("/agents/detail")]
    public void Restricted_keeps_Leadership_and_their_role_sections(string path)
    {
        var v = V(roles: [Roles.Restricted, Roles.Agents]);
        Assert.True(Nav.SectionOpen(path, v));
        Assert.Null(Nav.RestrictedOut(path, v));
    }

    [Fact]
    public void A_role_section_the_viewer_lacks_is_refused_for_its_role_not_the_restriction()
    {
        var v = V(roles: [Roles.Restricted, Roles.Agents]);
        Assert.False(Nav.SectionOpen("/licensing/unused", v));
        Assert.Null(Nav.RestrictedOut("/licensing/unused", v));   // the page's own "needs the Licensing role" message applies
    }

    [Fact]
    public void Restricted_keeps_the_Admin_pages_an_administrator_has()
    {
        var v = V(roles: [Roles.Restricted, Roles.Admin]);
        Assert.Contains("Admin", Sections(v));
        Assert.DoesNotContain("Regions", Sections(v));
    }

    [Fact]
    public void Restricted_lets_no_one_past_the_level_limit()
    {
        Assert.False(AccessRules.MayUseDashboard(9, new HashSet<string> { Roles.Restricted }, 3));
        Assert.True(AccessRules.MayUseDashboard(9, new HashSet<string> { Roles.Restricted, Roles.Agents }, 3));   // Agents does, as before
    }

    [Fact]
    public void Restricted_is_not_a_persona_role_and_does_not_widen()
    {
        Assert.False(AccessRules.IsPersona(Roles.Restricted));
        Assert.False(AccessRules.SeesWholeOrganization(5, new HashSet<string> { Roles.Restricted }));
    }

    [Fact]
    public void The_Workbench_is_hidden_unless_a_Workbench_role_or_Admin()
    {
        Assert.True(V(roles: [Roles.Restricted, Roles.Agents]).WorkbenchHidden);
        Assert.False(V(roles: [Roles.Restricted, Roles.Governance]).WorkbenchHidden);
        Assert.False(V(roles: [Roles.Restricted, Roles.Admin]).WorkbenchHidden);
        Assert.False(V(roles: [Roles.Agents]).WorkbenchHidden);
    }

    [Theory]
    [InlineData(5, false, false)]   // level 5, no organization-wide grant: no choice
    [InlineData(2, false, true)]    // level 2 sees the whole organization
    [InlineData(1, false, true)]
    [InlineData(5, true, true)]     // a persona role granted across the whole organization
    public void See_all_reports_is_offered_only_with_organization_wide_visibility(int level, bool orgWideAgents, bool offered)
    {
        var v = V(level, orgWideRoles: orgWideAgents ? [Roles.Agents] : [], roles: [Roles.Restricted, Roles.Agents]);
        Assert.Equal(offered, v.CanSeeAll);
    }

    [Fact]
    public void The_Organization_role_offers_See_all_reports()
    {
        Assert.True(V(roles: [Roles.Restricted, Roles.Organization, Roles.Agents]).CanSeeAll);
    }

    [Fact]
    public void Choosing_See_all_reports_lifts_the_restriction_only_where_offered()
    {
        var eligible = V(roles: [Roles.Restricted, Roles.Organization, Roles.Agents], seeAll: true);
        Assert.False(eligible.Restricted);
        Assert.Contains("Regions", Sections(eligible));
        Assert.False(eligible.WorkbenchHidden);

        /* A cookie set by hand changes nothing for a viewer who may not see the whole organization. */
        var ineligible = V(roles: [Roles.Restricted, Roles.Agents], seeAll: true);
        Assert.True(ineligible.Restricted);
        Assert.Equal(["Leadership", "Agents"], Sections(ineligible));
    }

    [Fact]
    public void See_all_reports_never_opens_a_role_section_the_viewer_lacks()
    {
        var v = V(roles: [Roles.Restricted, Roles.Organization, Roles.Agents], seeAll: true);
        Assert.DoesNotContain("Licensing", Sections(v));
        Assert.DoesNotContain("Security", Sections(v));
    }

    [Fact]
    public void The_role_is_offered_and_described()
    {
        Assert.Contains(Roles.Restricted, Roles.All);
        Assert.False(string.IsNullOrWhiteSpace(Roles.Describe[Roles.Restricted]));
    }

    static string Root()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(d.FullName, "CHANGELOG.md"))) d = d.Parent!;
        return d.FullName;
    }

    [Fact]
    public void The_warm_up_builds_every_report_unrestricted()
    {
        var warmer = File.ReadAllText(Path.Combine(Root(), "dashboard", "CopilotInsights", "Infrastructure", "PageWarmer.cs"));
        Assert.Contains("Roles.All.Where(r => r != Roles.Admin && r != Roles.Restricted)", warmer);
    }

    [Fact]
    public void The_database_accepts_the_role_on_grants_and_profiles_and_widens_an_older_check()
    {
        var sql = File.ReadAllText(Path.Combine(Root(), "sql", "Create-CopilotDashboardSchema.sql"));
        Assert.Equal(5, System.Text.RegularExpressions.Regex.Matches(sql, "'GovernanceAuditor', 'Restricted'\\)\\)").Count);
        Assert.Contains("N'CK_DashRoleGrant_Role' AND parent_object_id = OBJECT_ID(N'dash.RoleGrant') AND definition NOT LIKE N'%Restricted%'", sql);
        Assert.Contains("N'CK_DashAccessProfileRole_Role' AND parent_object_id = OBJECT_ID(N'dash.AccessProfileRole') AND definition NOT LIKE N'%Restricted%'", sql);
    }
}
