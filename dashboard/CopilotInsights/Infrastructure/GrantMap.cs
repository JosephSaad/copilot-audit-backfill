namespace CopilotInsights.Infrastructure;

/// <summary>
/// Every grant, and the AD groups named in them with the members the nightly directory load records (audit.GroupMember):
/// what reaches each person without their sign-in, for the manager digest, the access check and Who loses access. A
/// grant to an Entra group counts where its members are given (<see cref="For"/>'s entraGroups); sign-in itself asks
/// Windows and Entra ID, so it can differ from this until the next directory load.
/// </summary>
public sealed class GrantMap
{
    public IReadOnlyList<ViewerService.GrantRow> Grants { get; }
    readonly Dictionary<string, HashSet<string>> _groupsOf;

    /// <summary>Whether group members are recorded here at all (the directory load has run against this database).</summary>
    public bool MembersLoaded { get; }

    /// <summary>The Entra groups named in grants, by object ID.</summary>
    public IReadOnlyList<Guid> EntraGroups { get; }

    GrantMap(List<ViewerService.GrantRow> grants, Dictionary<string, HashSet<string>> groupsOf, bool loaded)
    {
        Grants = grants;
        _groupsOf = groupsOf;
        MembersLoaded = loaded;
        EntraGroups = [.. grants.Select(g => EntraPrincipal.TryParse(g.Principal, out var id) ? id : Guid.Empty).Where(id => id != Guid.Empty).Distinct()];
    }

    public static async Task<GrantMap> LoadAsync(Db db)
    {
        var grants = await ViewerService.GrantsAsync(db);
        var groups = grants.Where(g => g.Principal.Contains('\\')).Select(g => g.Principal).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var loaded = await db.ScalarAsync<int>("SELECT CASE WHEN OBJECT_ID(N'audit.GroupMember', N'U') IS NULL THEN 0 ELSE 1 END") == 1;
        var map = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        if (loaded && groups.Count > 0)
            foreach (var chunk in groups.Chunk(500))
                foreach (var (upn, group) in await db.QueryAsync<(string Upn, string Group)>(
                    "SELECT UserPrincipalName, GroupName FROM audit.GroupMember WHERE GroupName IN @chunk;", new { chunk }))
                {
                    if (!map.TryGetValue(upn, out var set)) map[upn] = set = new(StringComparer.OrdinalIgnoreCase);
                    set.Add(group);
                }
        return new(grants, map, loaded);
    }

    /// <summary>The AD groups named in grants that the directory load records this person in.</summary>
    public IReadOnlySet<string> GroupsOf(string upn) => _groupsOf.TryGetValue(upn, out var s) ? s : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Each grant that reaches the person, and how: "" by their own name, else the group it comes through.
    /// <paramref name="entraGroups"/> are the Entra groups they are known to be in (none known: Entra grants are left out).
    /// </summary>
    public IEnumerable<(ViewerService.GrantRow Grant, string Via)> Held(string upn, IReadOnlySet<Guid>? entraGroups = null)
    {
        var ad = GroupsOf(upn);
        foreach (var g in Grants)
        {
            if (string.Equals(g.Principal, upn, StringComparison.OrdinalIgnoreCase)) yield return (g, "");
            else if (ad.Contains(g.Principal)) yield return (g, g.Principal);
            else if (entraGroups is { Count: > 0 } && EntraPrincipal.TryParse(g.Principal, out var id) && entraGroups.Contains(id)) yield return (g, g.Principal);
        }
    }

    /// <summary>What the grants that reach the person give them (<see cref="AccessRules.Resolve"/>).</summary>
    public GrantedAccess For(string upn, IReadOnlySet<Guid>? entraGroups = null) => AccessRules.Resolve(Held(upn, entraGroups).Select(x => x.Grant));
}
