using CopilotInsights.Infrastructure;
using Xunit;

namespace CopilotInsights.Tests;

/// <summary>Who sees the whole organization, and whose part of it.</summary>
public class SecurityModelTests
{
    static HashSet<string> R(params string[] roles) => new(roles, StringComparer.OrdinalIgnoreCase);

    [Theory]
    [InlineData(1, true)]     // the top of the organization
    [InlineData(2, true)]     // reports to the top
    [InlineData(3, false)]
    [InlineData(7, false)]
    [InlineData(null, false)] // not under the top, or not in the directory
    public void Level_decides_whole_organization(int? level, bool expected) =>
        Assert.Equal(expected, AccessRules.SeesWholeOrganization(level, R()));

    [Fact]
    public void Organization_role_sees_everything_at_any_level()
    {
        Assert.True(AccessRules.SeesWholeOrganization(5, R(Roles.Organization)));
        Assert.True(AccessRules.SeesWholeOrganization(null, R("organization")));
    }

    [Fact]
    public void Admin_and_persona_roles_alone_do_not_widen_scope()
    {
        Assert.False(AccessRules.SeesWholeOrganization(4, R(Roles.Admin)));
        Assert.False(AccessRules.SeesWholeOrganization(4, R(Roles.Licensing, Roles.Security, Roles.Compliance, Roles.Adoption, Roles.Agents, Roles.Enablement)));
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
    public void Whole_organization_viewers_may_look_anywhere()
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
    static HashSet<string> R(params string[] roles) => new(roles, StringComparer.OrdinalIgnoreCase);

    static Viewer V(int? level, bool inDirectory = true, params string[] roles) =>
        new() { WindowsName = @"CONTOSO\me", Upn = "me@contoso.com", Level = level, InDirectory = inDirectory, Roles = new HashSet<string>(roles, StringComparer.OrdinalIgnoreCase),
                SectionKeys = Granted.Sections(roles) };

    /// <summary>A viewer granted these sections (a Sections grant) and these roles.</summary>
    static Viewer VS(int? level, string[] sections, params string[] roles) =>
        new() { WindowsName = @"CONTOSO\me", Upn = "me@contoso.com", Level = level, InDirectory = true, Roles = new HashSet<string>(roles, StringComparer.OrdinalIgnoreCase),
                SectionKeys = Granted.Sections(roles, sections) };

    static List<string> Paths(Viewer v) => CopilotInsights.Pages.Nav.For(v).SelectMany(g => g.Pages).Select(p => p.Path).ToList();

    [Fact]
    public void A_manager_without_roles_sees_no_named_lists_and_no_admin()
    {
        /* Nothing granted: nothing in the menu, Leadership included, whatever their level. */
        Assert.Empty(Paths(V(4)));
        var paths = Paths(VS(4, [Sections.Leadership, Sections.Licensing]));
        Assert.Contains("/leadership", paths);
        Assert.Contains("/licensing", paths);
        Assert.DoesNotContain("/licensing/unused", paths);
        Assert.DoesNotContain("/licensing/payg", paths);
        Assert.DoesNotContain("/licensing/swap", paths);
        Assert.DoesNotContain("/licensing/license-for-credits", paths);
        Assert.DoesNotContain("/licensing/fading", paths);
        Assert.DoesNotContain("/licensing/non-person", paths);
        Assert.DoesNotContain("/licensing/overlap", paths);
        Assert.DoesNotContain("/licensing/departures", paths);
        Assert.DoesNotContain("/security/flags", paths);
        Assert.DoesNotContain(CopilotInsights.Pages.Nav.For(VS(4, [Sections.Leadership])), g => g.Persona == "Admin");
    }

    [Theory]
    [InlineData("Adoption", "Adoption", "/adoption")]
    [InlineData("Licensing", "Licensing", "/licensing")]
    [InlineData("Forecasting", "Licensing", "/forecasting")]
    [InlineData("Security", "Security", "/security")]
    [InlineData("Compliance", "Compliance", "/compliance")]
    [InlineData("Agents", "Agents", "/agents")]
    [InlineData("Enablement", "Enablement", "/enablement")]
    public void A_section_with_a_role_is_hidden_altogether_without_it(string persona, string role, string aggregatePage)
    {
        /* Not even the aggregates, and not for the top of the organization either. */
        Assert.DoesNotContain(CopilotInsights.Pages.Nav.For(V(1)), g => g.Persona == persona);
        Assert.DoesNotContain(aggregatePage, Paths(V(1)));
        Assert.Contains(CopilotInsights.Pages.Nav.For(V(4, roles: role)), g => g.Persona == persona);
        Assert.Contains(aggregatePage, Paths(V(4, roles: role)));
        Assert.Equal(role, CopilotInsights.Pages.Nav.SectionOf(aggregatePage)?.Role);
    }

    [Fact]
    public void Every_section_needs_a_grant_whatever_the_level()
    {
        /* The top of the organization, a manager and someone at level 7 see nothing until a section is granted. */
        Assert.Empty(CopilotInsights.Pages.Nav.For(V(1)));
        Assert.Empty(CopilotInsights.Pages.Nav.For(V(5)));
        Assert.Empty(CopilotInsights.Pages.Nav.For(V(7)));
        var personas = CopilotInsights.Pages.Nav.For(VS(5, [Sections.Leadership, Sections.Regions, Sections.People, Sections.AppOwners, Sections.Governance])).Select(g => g.Persona).ToList();
        Assert.Equal(["Leadership", "Regions", "People", "App owners", "Governance"], personas);
        /* Only the sections ticked: Leadership alone is Leadership alone. */
        Assert.Equal(["Leadership"], CopilotInsights.Pages.Nav.For(VS(7, [Sections.Leadership])).Select(g => g.Persona));
        /* Every Insights section has a key a grant can tick, and every key is a section. */
        Assert.All(CopilotInsights.Pages.Nav.Groups.Where(g => g.Persona != "Admin"), g => Assert.NotNull(g.Key));
        Assert.Equal(Sections.All.Where(s => !s.Workbench).Select(s => s.Key), CopilotInsights.Pages.Nav.Groups.Where(g => g.Key is not null).Select(g => g.Key));
    }

    [Fact]
    public void Several_roles_open_several_sections()
    {
        var personas = CopilotInsights.Pages.Nav.For(V(4, roles: [Roles.Licensing, Roles.Enablement])).Select(g => g.Persona).ToList();
        Assert.Contains("Licensing", personas);
        Assert.Contains("Forecasting", personas);
        Assert.Contains("Enablement", personas);
        Assert.DoesNotContain("Security", personas);
    }

    [Fact]
    public void A_role_shows_its_own_named_list_only()
    {
        var paths = Paths(V(4, roles: Roles.Licensing));
        Assert.Contains("/licensing/unused", paths);
        Assert.Contains("/licensing/payg", paths);
        Assert.Contains("/licensing/swap", paths);
        Assert.Contains("/licensing/license-for-credits", paths);
        Assert.Contains("/licensing/fading", paths);
        Assert.Contains("/licensing/non-person", paths);
        Assert.Contains("/licensing/overlap", paths);
        Assert.Contains("/licensing/departures", paths);
        Assert.DoesNotContain("/adoption/not-started", paths);
    }

    [Fact]
    public void An_admin_outside_the_directory_sees_only_admin()
    {
        var groups = CopilotInsights.Pages.Nav.For(V(null, inDirectory: false, roles: Roles.Admin));
        Assert.Equal("Admin", Assert.Single(groups).Persona);
    }

    [Fact]
    public void There_are_no_personal_pages()
    {
        /* 2.4.4 removed My Copilot: its use, its email choices and saved views. */
        Assert.DoesNotContain(CopilotInsights.Pages.Nav.Groups, g => g.Persona == "My Copilot");
        Assert.DoesNotContain(CopilotInsights.Pages.Nav.Groups.SelectMany(g => g.Pages), p => p.Path == "/me" || p.Path.StartsWith("/me/"));
        Assert.Equal("Leadership", CopilotInsights.Pages.Nav.For(VS(5, Granted.All))[0].Persona);
        Assert.Contains("/licensing/changes", Paths(V(5, roles: Roles.Licensing)));   // in the Licensing section, which needs the role
        Assert.DoesNotContain("/licensing/changes", Paths(V(5)));
    }

    /* ---- The dashboard's level limit ------------------------------------------------ */

    [Theory]
    [InlineData(0, 1, true)]       // no limit: everyone, as before
    [InlineData(0, 12, true)]
    [InlineData(0, null, true)]
    [InlineData(7, 1, true)]       // the top
    [InlineData(7, 7, true)]       // the last level in
    [InlineData(7, 8, false)]      // the first level out
    [InlineData(7, null, false)]   // not under the top, or not in the directory
    [InlineData(1, 1, true)]
    [InlineData(1, 2, false)]
    [InlineData(15, 15, true)]
    [InlineData(15, 16, false)]
    public void The_limit_lets_in_levels_one_to_n(int max, int? level, bool expected) =>
        Assert.Equal(expected, AccessRules.MayUseDashboard(level, R(), max));

    [Theory]
    [InlineData("Organization")]
    [InlineData("Licensing")]
    [InlineData("Security")]
    [InlineData("enablement")]
    public void A_role_other_than_admin_lets_anyone_in(string role)
    {
        Assert.True(AccessRules.MayUseDashboard(11, R(role), 7));
        Assert.True(AccessRules.MayUseDashboard(null, R(role), 7));
    }

    [Fact]
    public void Admin_alone_does_not_let_anyone_in()
    {
        Assert.False(AccessRules.MayUseDashboard(9, R(Roles.Admin), 7));
        Assert.False(AccessRules.MayUseDashboard(null, R("ADMIN"), 7));
        Assert.True(AccessRules.MayUseDashboard(9, R(Roles.Admin, Roles.Licensing), 7));
    }

    static Viewer Outside(params string[] roles) =>
        new() { WindowsName = @"CONTOSO\me", Upn = "me@contoso.com", Level = 9, InDirectory = true, Roles = R(roles), MaxLevel = 7,
                Admitted = AccessRules.MayUseDashboard(9, R(roles), 7), SectionKeys = Granted.Sections(roles) };

    [Fact]
    public void Outside_the_levels_nothing_is_listed()
    {
        Assert.Empty(CopilotInsights.Pages.Nav.For(Outside()));
        Assert.Empty(CopilotInsights.Pages.Nav.For(Outside(), privacyMode: true, manyTenants: true));
    }

    [Fact]
    public void An_admin_outside_the_levels_keeps_the_admin_pages_only()
    {
        var groups = CopilotInsights.Pages.Nav.For(Outside(Roles.Admin));
        Assert.Equal("Admin", Assert.Single(groups).Persona);
        Assert.All(groups[0].Pages, p => Assert.True(p.Admin));
    }

    [Fact]
    public void A_role_holder_outside_the_levels_gets_the_usual_pages()
    {
        var v = Outside(Roles.Licensing);
        Assert.True(v.Admitted);
        Assert.Contains("/licensing/unused", Paths(v));
        /* Their role's sections only: Leadership is not granted to them, so it is not theirs (before 3.1.0 it was). */
        Assert.DoesNotContain("/leadership", Paths(v));
    }

    [Fact]
    public void The_refusal_names_the_levels_and_the_viewers_own()
    {
        var text = CopilotInsights.Pages.InsightsPage.LimitRefusal(Outside());
        Assert.Contains("levels 1 to 7", text);
        Assert.Contains("level 9", text);
        Assert.Contains("Admin pages", CopilotInsights.Pages.InsightsPage.LimitRefusal(Outside(Roles.Admin)));
    }

    [Fact]
    public void Nobody_sees_an_empty_group() =>
        Assert.All(CopilotInsights.Pages.Nav.For(VS(5, Granted.All)), g => Assert.NotEmpty(g.Pages));

    [Fact]
    public void Every_page_is_listed_for_someone_holding_every_role()
    {
        var pages = CopilotInsights.Pages.Nav.Groups.SelectMany(g => g.Pages).ToList();
        Assert.Equal(pages.Count(p => !p.ManyTenants), Paths(VS(1, Granted.All, Roles.All)).Count);
        /* With several tenants, the group overview too. */
        Assert.Equal(pages.Count, CopilotInsights.Pages.Nav.For(VS(1, Granted.All, Roles.All), manyTenants: true).SelectMany(g => g.Pages).Count());
    }
}

/// <summary>A Copilot license on a non-human account is a policy breach: the rules, and who sees the page.</summary>
public class NonHumanTests
{
    [Theory]
    [InlineData("bot-*", "bot-%")]
    [InlineData("*-SVC", "%-svc")]
    [InlineData("svc_app", "svc[_]app")]
    [InlineData("100%", "100[%]")]
    [InlineData("robot@contoso.com", "robot")]
    public void An_admin_pattern_becomes_a_literal_like_with_star_for_any_text(string pattern, string like) =>
        Assert.Equal(like, CopilotInsights.Data.NonHuman.Like(pattern));

    [Fact]
    public void Lists_split_on_commas_semicolons_and_lines_without_blanks_or_repeats() =>
        Assert.Equal(["SERVICE", "RESOURCE", "ROOM"], CopilotInsights.Data.NonHuman.Split("SERVICE, RESOURCE;\n ROOM,,service"));

    [Fact]
    public void The_reasons_read_in_words() =>
        Assert.Equal("Guest, In a service-account group", CopilotInsights.Data.NonHuman.Why(true, false, false, false, true));

    static Viewer V(params string[] roles) =>
        new() { WindowsName = @"CONTOSO\me", Upn = "me@contoso.com", Level = 3, InDirectory = true, Roles = new HashSet<string>(roles, StringComparer.OrdinalIgnoreCase),
                SectionKeys = Granted.Sections(roles) };

    static List<string> Paths(Viewer v) => CopilotInsights.Pages.Nav.For(v).SelectMany(g => g.Pages).Select(p => p.Path).ToList();

    [Theory]
    [InlineData("Security")]
    [InlineData("Compliance")]
    [InlineData("Licensing")]
    public void Security_compliance_and_licensing_each_see_the_breaches(string role)
    {
        /* One page, listed in each of the three sections (it was a copy in each). */
        var path = CopilotInsights.Pages.Nav.NonHuman;
        Assert.Contains(path, Paths(V(role)));
        Assert.DoesNotContain(path, Paths(V()));
        Assert.True(CopilotInsights.Pages.Nav.SectionOpen(path, V(role)));
        Assert.False(CopilotInsights.Pages.Nav.SectionOpen(path, V(Roles.Adoption)));
        Assert.Equal(role, CopilotInsights.Pages.Nav.SectionFor(path, V(role))?.Role);
    }

    [Fact]
    public void Privacy_mode_hides_the_named_breaches() =>
        Assert.DoesNotContain(CopilotInsights.Pages.Nav.For(V(Roles.Security, Roles.Compliance, Roles.Licensing), privacyMode: true).SelectMany(g => g.Pages),
            p => p.Path.Contains("non-"));
}

/// <summary>Levels 1 to N are never on a revoke or train list.</summary>
public class ProtectedLevelsTests
{
    [Fact]
    public void Train_and_revoke_leave_out_the_protected_levels_but_upgrade_does_not()
    {
        var kinds = CopilotInsights.Pages.Forecasting.ActionsModel.Kinds;
        Assert.Contains("dash.Person AS pl", kinds["train"].Where);
        Assert.Contains("dash.Person AS pl", kinds["revoke"].Where);
        Assert.DoesNotContain("dash.Person AS pl", kinds["upgrade"].Where);
    }

    [Fact]
    public void The_rule_reads_the_setting_with_five_as_its_default()
    {
        var sql = CopilotInsights.Data.ProtectedLevels.Not("du.UserPrincipalName");
        Assert.Contains("pl.Upn = du.UserPrincipalName", sql);
        Assert.Contains("pl.Level BETWEEN 1 AND ISNULL((SELECT TRY_CAST(ps.Value AS INT) FROM dash.Setting AS ps WHERE ps.Name = N'ProtectedLevels'), 5)", sql);
        Assert.StartsWith("NOT EXISTS", sql);
    }
}
