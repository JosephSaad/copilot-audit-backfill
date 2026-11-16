using CopilotInsights.Infrastructure;

namespace CopilotInsights.Tests;

/// <summary>
/// The sections a test viewer's grants open, as sign-in works them out (<see cref="AccessRules.Resolve"/>): each persona
/// role its own section, as it was granted before 3.1.0, and the section keys given besides, as a Sections grant opens them.
/// </summary>
static class Granted
{
    public static HashSet<string> Sections(IEnumerable<string> roles, params string[] sections)
    {
        var grants = roles.Select(r => new ViewerService.GrantRow("me@contoso.com", "User", r, null, false, null)).ToList();
        if (sections.Length > 0) grants.Add(new ViewerService.GrantRow("me@contoso.com", "User", Roles.Sections, null, false, null, string.Join(",", sections)));
        return new HashSet<string>(AccessRules.Resolve(grants).Sections, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>What these roles give, those in <paramref name="wide"/> granted across the whole organization.</summary>
    public static GrantedAccess Access(IEnumerable<string> roles, IEnumerable<string>? wide = null)
    {
        var w = (wide ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return AccessRules.Resolve(roles.Select(r => new ViewerService.GrantRow("me@contoso.com", "User", r, null, w.Contains(r), null)));
    }

    /// <summary>Every section of the menu, as a grant that ticks them all opens them.</summary>
    public static string[] All => [.. CopilotInsights.Infrastructure.Sections.Keys];
}
