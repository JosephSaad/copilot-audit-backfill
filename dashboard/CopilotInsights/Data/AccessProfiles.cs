using CopilotInsights.Infrastructure;

namespace CopilotInsights.Data;

/// <summary>One role an access profile gives: across the whole organization or not, and for Admin, its pages (null: every page).</summary>
public sealed record ProfileRole(string Role, bool OrgWide = false, string? Pages = null);

/// <summary>A bundle offered as a profile: from today's grants (who holds exactly it), or one of the starters.</summary>
public sealed record ProfileProposal(string Name, IReadOnlyList<ProfileRole> Roles, IReadOnlyList<string> Holders, bool Starter);

/// <summary>
/// Access profiles: named bundles of roles granted as one (Admin › Access profiles). What a bundle is, how it reads, and
/// the profiles offered from the grants already made. Pure, for the tests.
/// </summary>
public static class AccessProfiles
{
    /// <summary>A role as a profile keeps it: across the organization only for a persona role, pages only for Admin.</summary>
    public static ProfileRole Clean(ProfileRole r)
    {
        var role = Infrastructure.Roles.All.First(x => string.Equals(x, r.Role, StringComparison.OrdinalIgnoreCase));
        return new(role, r.OrgWide && AccessRules.IsPersona(role), role == Infrastructure.Roles.Admin ? AdminPages.Format(AdminPages.Parse(r.Pages)) : null);
    }

    /// <summary>The roles in the navigation's order, each once (a role given twice keeps the wider of the two).</summary>
    public static List<ProfileRole> Normalize(IEnumerable<ProfileRole> roles) =>
        roles.Where(r => Infrastructure.Roles.All.Contains(r.Role, StringComparer.OrdinalIgnoreCase)).Select(Clean)
            .GroupBy(r => r.Role)
            .Select(g => new ProfileRole(g.Key, g.Any(r => r.OrgWide), g.Any(r => r.Pages is null) ? null : AdminPages.Format(g.SelectMany(r => AdminPages.Parse(r.Pages)))))
            .OrderBy(r => Array.IndexOf(Infrastructure.Roles.All, r.Role)).ToList();

    /// <summary>The bundle as one string: two bundles that give the same are the same.</summary>
    public static string Key(IEnumerable<ProfileRole> roles) =>
        string.Join("|", Normalize(roles).Select(r => r.Role + (r.OrgWide ? "*" : "") + (r.Pages is null ? "" : $"({r.Pages})")));

    /// <summary>A bundle back from its key.</summary>
    public static List<ProfileRole> FromKey(string? key) =>
        Normalize((key ?? "").Split('|', StringSplitOptions.RemoveEmptyEntries).Select(p =>
        {
            var paren = p.IndexOf('(');
            var pages = paren >= 0 ? p[(paren + 1)..].TrimEnd(')') : null;
            var head = paren >= 0 ? p[..paren] : p;
            return new ProfileRole(head.TrimEnd('*'), head.EndsWith('*'), pages);
        }));

    /// <summary>What a bundle gives, in words: "Agents across the organization; Admin (Health, Audit log)".</summary>
    public static string Describe(IEnumerable<ProfileRole> roles)
    {
        var list = Normalize(roles);
        return list.Count == 0 ? "nothing" : string.Join("; ", list.Select(r =>
            r.Role + (r.OrgWide ? " across the organization" : "") + (r.Role == Infrastructure.Roles.Admin ? $" ({AdminPages.Names(r.Pages)})" : "")));
    }

    /// <summary>A name for a bundle nobody has named: its roles.</summary>
    public static string SuggestName(IEnumerable<ProfileRole> roles)
    {
        var list = Normalize(roles);
        var names = list.Select(r => r.Role == Infrastructure.Roles.Admin && r.Pages is not null ? "Admin pages" : r.Role).ToList();
        var name = names.Count switch { 0 => "Profile", 1 => names[0], _ => string.Join(", ", names[..^1]) + " and " + names[^1] };
        if (list.Any(r => r.OrgWide)) name += list.All(r => r.OrgWide || !AccessRules.IsPersona(r.Role)) ? ", whole organization" : ", partly whole organization";
        return name.Length <= 100 ? name : name[..100];
    }

    /// <summary>
    /// The plan's four starters, as an administrator would build them first. Their Admin pages are named by path, so a
    /// page that is not there leaves the starter with the rest.
    /// </summary>
    public static readonly IReadOnlyList<ProfileProposal> Starters =
    [
        new("Agent program lead", [new(Infrastructure.Roles.Agents, OrgWide: true)], [], true),
        new("License manager", [new(Infrastructure.Roles.Licensing, OrgWide: true), new(Infrastructure.Roles.Admin, Pages: "/admin/assumptions,/admin/reference")], [], true),
        new("Security reviewer", [new(Infrastructure.Roles.Security), new(Infrastructure.Roles.Compliance)], [], true),
        new("Dashboard operator", [new(Infrastructure.Roles.Admin, Pages: "/admin/health,/admin/notifications,/admin/audit")], [], true),
    ];

    /// <summary>The Governance Workbench's starters, offered when it is set up: its central team, its operators and its auditors.</summary>
    public static readonly IReadOnlyList<ProfileProposal> WorkbenchStarters =
    [
        new("Governance team", [new(Infrastructure.Roles.Governance)], [], true),
        new("Governance operator", [new(Infrastructure.Roles.GovernanceOperator)], [], true),
        new("Governance auditor", [new(Infrastructure.Roles.GovernanceAuditor)], [], true),
    ];

    /// <summary>
    /// The profiles offered: each bundle of roles that people and groups hold today role by role (most held first), then
    /// the starters, leaving out any bundle a profile already gives and any starter whose name is taken. A bundle that is
    /// only a full Admin is left out: administrators are few, and named one by one on purpose.
    /// </summary>
    public static IReadOnlyList<ProfileProposal> Proposals(IEnumerable<(string Principal, ProfileRole Role)> grants,
        IEnumerable<IEnumerable<ProfileRole>> existing, IEnumerable<string> existingNames, bool workbench = false)
    {
        var taken = existing.Select(Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var names = existingNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var fromGrants = grants.GroupBy(g => g.Principal, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Principal: g.Key, Roles: Normalize(g.Select(x => x.Role))))
            .Where(x => x.Roles.Count > 0 && !(x.Roles.Count == 1 && x.Roles[0] is { Role: Infrastructure.Roles.Admin, Pages: null }))
            .GroupBy(x => Key(x.Roles))
            .Where(g => !taken.Contains(g.Key))
            .Select(g => new ProfileProposal(SuggestName(g.First().Roles), g.First().Roles, g.Select(x => x.Principal).Order(StringComparer.OrdinalIgnoreCase).ToList(), false))
            .OrderByDescending(p => p.Holders.Count).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var seen = fromGrants.Select(p => Key(p.Roles)).ToHashSet();
        IEnumerable<ProfileProposal> starters = workbench ? [.. Starters, .. WorkbenchStarters] : Starters;
        return [.. fromGrants, .. starters.Where(s => !names.Contains(s.Name) && !taken.Contains(Key(s.Roles)) && seen.Add(Key(s.Roles)))];
    }

    /// <summary>A request link as kept: https only, and no longer than the column.</summary>
    public static string? RequestUrl(string? given)
    {
        var u = given?.Trim();
        if (string.IsNullOrEmpty(u)) return null;
        if (u.Length > 1000 || !Uri.TryCreate(u, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException("the request link must be a full https:// address, such as the access package's My Access link.");
        return uri.ToString();
    }
}
