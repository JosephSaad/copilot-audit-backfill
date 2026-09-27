using CopilotInsights.Infrastructure;
using Xunit;

namespace CopilotInsights.Tests;

/// <summary>Who sees the whole organisation, and whose part of it.</summary>
public class SecurityModelTests
{
    static HashSet<string> R(params string[] roles) => new(roles, StringComparer.OrdinalIgnoreCase);

    [Theory]
    [InlineData(1, true)]     // the top of the organisation
    [InlineData(2, true)]     // reports to the top
    [InlineData(3, false)]
    [InlineData(7, false)]
    [InlineData(null, false)] // not under the top, or not in the directory
    public void Level_decides_whole_organisation(int? level, bool expected) =>
        Assert.Equal(expected, AccessRules.SeesWholeOrganisation(level, R()));

    [Fact]
    public void Organization_role_sees_everything_at_any_level()
    {
        Assert.True(AccessRules.SeesWholeOrganisation(5, R(Roles.Organization)));
        Assert.True(AccessRules.SeesWholeOrganisation(null, R("organization")));
    }

    [Fact]
    public void Admin_and_persona_roles_alone_do_not_widen_scope()
    {
        Assert.False(AccessRules.SeesWholeOrganisation(4, R(Roles.Admin)));
        Assert.False(AccessRules.SeesWholeOrganisation(4, R(Roles.Licensing, Roles.Security, Roles.Compliance, Roles.Adoption, Roles.Agents, Roles.Enablement)));
    }

    static Viewer V(int? level, string upn = "me@contoso.com", params string[] roles) =>
        new() { WindowsName = @"CONTOSO\me", Upn = upn, Level = level, InDirectory = true, Roles = R(roles) };

    [Fact]
    public void A_manager_may_look_inside_their_own_subtree_only()
    {
        var manager = V(3);
        Assert.True(AccessRules.MayViewSubtree(manager, "me@contoso.com", rootIsUnderViewer: false));
        Assert.True(AccessRules.MayViewSubtree(manager, "report@contoso.com", rootIsUnderViewer: true));
        Assert.False(AccessRules.MayViewSubtree(manager, "peer@contoso.com", rootIsUnderViewer: false));
        Assert.False(AccessRules.MayViewSubtree(manager, "ceo@contoso.com", rootIsUnderViewer: false));
    }

    [Fact]
    public void Upn_comparison_ignores_case() =>
        Assert.True(AccessRules.MayViewSubtree(V(4, "Me@Contoso.com"), "me@contoso.COM", rootIsUnderViewer: false));

    [Fact]
    public void Whole_organisation_viewers_may_look_anywhere()
    {
        Assert.True(AccessRules.MayViewSubtree(V(2), "anyone@contoso.com", rootIsUnderViewer: false));
        Assert.True(AccessRules.MayViewSubtree(V(6, roles: Roles.Organization), "anyone@contoso.com", rootIsUnderViewer: false));
    }

    [Fact]
    public void Every_role_is_described() =>
        Assert.All(Roles.All, r => Assert.False(string.IsNullOrWhiteSpace(Roles.Describe[r])));
}

/// <summary>What the navigation and the start page list: only what the viewer can open.</summary>
public class NavigationTests
{
    static Viewer V(int? level, bool inDirectory = true, params string[] roles) =>
        new() { WindowsName = @"CONTOSO\me", Upn = "me@contoso.com", Level = level, InDirectory = inDirectory, Roles = new HashSet<string>(roles, StringComparer.OrdinalIgnoreCase) };

    static List<string> Paths(Viewer v) => CopilotInsights.Pages.Nav.For(v).SelectMany(g => g.Pages).Select(p => p.Path).ToList();

    [Fact]
    public void A_manager_without_roles_sees_no_named_lists_and_no_admin()
    {
        var paths = Paths(V(4));
        Assert.Contains("/leadership", paths);
        Assert.DoesNotContain("/licensing/unused", paths);
        Assert.DoesNotContain("/security/flags", paths);
        Assert.DoesNotContain(CopilotInsights.Pages.Nav.For(V(4)), g => g.Persona == "Admin");
    }

    [Fact]
    public void A_role_shows_its_own_named_list_only()
    {
        var paths = Paths(V(4, roles: Roles.Licensing));
        Assert.Contains("/licensing/unused", paths);
        Assert.DoesNotContain("/adoption/not-started", paths);
    }

    [Fact]
    public void An_admin_outside_the_directory_sees_only_admin()
    {
        var groups = CopilotInsights.Pages.Nav.For(V(null, inDirectory: false, roles: Roles.Admin));
        Assert.Equal("Admin", Assert.Single(groups).Persona);
    }

    [Fact]
    public void Everyone_in_the_directory_gets_their_own_page_first()
    {
        var first = CopilotInsights.Pages.Nav.For(V(5))[0];
        Assert.Equal("My Copilot", first.Persona);
        Assert.Equal("/me", first.Pages[0].Path);
        Assert.Equal(["/me", "/me/email", "/me/views"], first.Pages.Select(p => p.Path));
        Assert.Contains("/licensing/changes", Paths(V(5)));   // aggregates; the administrators' names on it need Licensing
    }

    [Fact]
    public void Nobody_sees_an_empty_group() =>
        Assert.All(CopilotInsights.Pages.Nav.For(V(5)), g => Assert.NotEmpty(g.Pages));

    [Fact]
    public void Every_page_is_listed_for_someone_holding_every_role()
    {
        var pages = CopilotInsights.Pages.Nav.Groups.SelectMany(g => g.Pages).ToList();
        Assert.Equal(pages.Count(p => !p.ManyTenants), Paths(V(1, roles: Roles.All)).Count);
        /* With several tenants, the group overview too. */
        Assert.Equal(pages.Count, CopilotInsights.Pages.Nav.For(V(1, roles: Roles.All), manyTenants: true).SelectMany(g => g.Pages).Count());
    }
}
