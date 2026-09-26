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
