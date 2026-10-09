using CopilotInsights.Data;
using CopilotInsights.Infrastructure;
using Xunit;

namespace CopilotInsights.Tests;

/// <summary>Removing every role of one person or group, or of several at once (Admin › Access, 2.6.3).</summary>
public class RoleGrantsTests
{
    [Fact]
    public void The_principals_asked_for_are_trimmed_and_named_once()
    {
        var list = RoleGrants.Clean([" ada@contoso.com ", "ADA@contoso.com", null, "", "  ", @"CONTOSO\Copilot-Leads", new string('x', 257)]);
        Assert.Equal(["ada@contoso.com", @"CONTOSO\Copilot-Leads"], list);
        Assert.Empty(RoleGrants.Clean(null));
    }

    [Fact]
    public void One_person_is_named_with_the_roles_they_held_in_the_order_roles_are_listed()
    {
        var two = Roles.All.Take(2).ToArray();
        var done = RoleGrants.Removed([("ada@contoso.com", two[1]), ("ada@contoso.com", two[0])], ["ada@contoso.com"]);
        Assert.Equal($"Removed every role from ada@contoso.com: {two[0]}, {two[1]}.", done);
    }

    [Fact]
    public void Someone_who_holds_no_role_is_said_to_hold_none()
    {
        Assert.Equal("Nothing removed: ada@contoso.com holds no role.", RoleGrants.Removed([], ["ada@contoso.com"]));
        Assert.Equal("Nothing removed: none of the 2 selected holds a role.", RoleGrants.Removed([], ["a@contoso.com", "b@contoso.com"]));
    }

    [Fact]
    public void Several_are_each_named_with_their_roles_and_those_without_any_are_counted()
    {
        var done = RoleGrants.Removed(
            [("b@contoso.com", Roles.All[0]), (@"CONTOSO\Leads", Roles.All[1]), ("B@contoso.com", Roles.All[1])],
            [@"CONTOSO\Leads", "b@contoso.com", "c@contoso.com"]);
        Assert.Equal($@"Removed every role from 2 people and groups: CONTOSO\Leads ({Roles.All[1]}); b@contoso.com ({Roles.All[0]}, {Roles.All[1]}). c@contoso.com held no role.", done);
    }

    [Fact]
    public void An_administrator_who_removes_their_own_roles_is_told_so()
    {
        var done = RoleGrants.Removed([("me@contoso.com", Roles.Admin), ("b@contoso.com", Roles.Admin)], ["me@contoso.com", "b@contoso.com"], "ME@contoso.com");
        Assert.EndsWith(" That includes your own account.", done);
        Assert.DoesNotContain("your own account", RoleGrants.Removed([("b@contoso.com", Roles.Admin)], ["b@contoso.com"], "me@contoso.com"));
    }

    [Fact]
    public void A_long_list_is_cut_to_what_the_audit_log_keeps_and_the_rest_counted()
    {
        var asked = Enumerable.Range(1, RoleGrants.MaxAtOnce).Select(i => $"person.number.{i:000}@a-long-domain-name.contoso.com").ToList();
        var done = RoleGrants.Removed(asked.Select(a => (a, Roles.Admin)).ToList(), asked);
        Assert.StartsWith($"Removed every role from {RoleGrants.MaxAtOnce} people and groups: person.number.001@", done);
        Assert.True(done.Length < 2000, $"the message is {done.Length} characters; the audit log keeps 2,000");
        Assert.Matches(@"; and \d+ more\.$", done);
    }

    [Fact]
    public void The_access_page_removes_by_principal_and_asks_first()
    {
        var page = File.ReadAllText(Path.Combine(Source(), "Pages", "Admin", "Admin.cs"));
        Assert.Contains("OnPostRevokeAllAsync(string? principal)", page);
        Assert.Contains("OnPostRevokeManyAsync(string[]? principal)", page);
        Assert.Contains("DELETE FROM dash.RoleGrant OUTPUT deleted.Principal, deleted.Role WHERE Principal IN @list;", page);
        /* Both are admin changes (refused to anyone else, and audited), and both buttons ask before they remove. */
        Assert.Contains("AdminChangeAsync(() => RemoveEveryRoleAsync(", page);
        Assert.Contains("data-confirm=\\\"{E($\"Remove every role from {principal}?", page);
        Assert.Contains("Button(\"Remove every role from the selected\", \"Remove every role from each person and group ticked?", page);
    }

    static string Source()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "CopilotInsights", "Pages"))) dir = dir.Parent;
        return Path.Combine(dir?.FullName ?? throw new DirectoryNotFoundException("the dashboard's source was not found above the tests"), "CopilotInsights");
    }
}
