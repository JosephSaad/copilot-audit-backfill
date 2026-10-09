namespace CopilotInsights.Data;

/// <summary>
/// The Admin pages a Persona grant opens (2.6.4), kept on its dash.RoleGrant row as their paths, comma-separated. Only
/// the pages of the Admin section can be named: anything else in the column is ignored. Pure, for the tests.
/// </summary>
public static class PersonaPages
{
    /// <summary>The Admin section's pages, in the navigation's order.</summary>
    public static IReadOnlyList<Pages.NavPage> Choices => Pages.Nav.Groups.First(g => g.Persona == "Admin").Pages;

    /// <summary>A request path as the list keeps it: lower case, no trailing slash.</summary>
    public static string Normalize(string? path) => (path ?? "").Trim().TrimEnd('/').ToLowerInvariant();

    /// <summary>The Admin pages a column value names, each once.</summary>
    public static HashSet<string> Parse(string? value)
    {
        var known = Choices.Select(p => Normalize(p.Path)).ToHashSet();
        return (value ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Normalize).Where(known.Contains).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The column value for the pages ticked, in the navigation's order; null when none of them is an Admin page.</summary>
    public static string? Format(IEnumerable<string?>? ticked)
    {
        var set = (ticked ?? []).Select(Normalize).ToHashSet();
        var list = Choices.Select(p => Normalize(p.Path)).Where(set.Contains).ToList();
        return list.Count == 0 ? null : string.Join(",", list);
    }

    /// <summary>The pages' names, for the grants table and messages.</summary>
    public static string Names(string? value)
    {
        var set = Parse(value);
        var names = Choices.Where(p => set.Contains(Normalize(p.Path))).Select(p => p.Title).ToList();
        return names.Count == 0 ? "no pages" : names.Count == Choices.Count ? "every Admin page" : string.Join(", ", names);
    }
}
