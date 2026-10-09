using CopilotInsights.Infrastructure;

namespace CopilotInsights.Data;

/// <summary>
/// Removing every role a person or group holds, for one or for many at once (Admin › Access, 2.6.3). The page deletes
/// the grants; this decides who was asked for and says what went. Pure, for the tests.
/// </summary>
public static class RoleGrants
{
    /// <summary>The most people and groups one request may name: a page of the grants table holds far fewer.</summary>
    public const int MaxAtOnce = 500;

    /// <summary>The most a message names before it counts the rest: the audit log keeps 2,000 characters of it.</summary>
    const int MessageRoom = 1500;

    /// <summary>The principals a request named: trimmed, each once whatever its case, none empty or longer than a principal can be.</summary>
    public static List<string> Clean(IEnumerable<string?>? principals) =>
        (principals ?? [])
            .Select(p => p?.Trim() ?? "")
            .Where(p => p.Length is > 0 and <= 256)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// What was removed, for the page and the audit log: each person or group with the roles they held, in the order
    /// asked for, then those who held none. <paramref name="self"/> is the administrator making the change.
    /// </summary>
    public static string Removed(IReadOnlyList<(string Principal, string Role)> gone, IReadOnlyList<string> asked, string? self = null)
    {
        var by = gone.GroupBy(g => g.Principal, StringComparer.OrdinalIgnoreCase)
                     .ToDictionary(g => g.Key, g => g.Select(x => x.Role).OrderBy(r => Array.IndexOf(Roles.All, r)).ToList(), StringComparer.OrdinalIgnoreCase);
        var held = asked.Where(by.ContainsKey).ToList();
        var none = asked.Where(a => !by.ContainsKey(a)).ToList();
        if (held.Count == 0)
            return asked.Count == 1 ? $"Nothing removed: {asked[0]} holds no role." : $"Nothing removed: none of the {asked.Count:#,0} selected holds a role.";

        var text = new System.Text.StringBuilder();
        if (held.Count == 1 && asked.Count == 1)
            text.Append($"Removed every role from {held[0]}: {string.Join(", ", by[held[0]])}.");
        else
        {
            text.Append($"Removed every role from {held.Count:#,0} {(held.Count == 1 ? "person or group" : "people and groups")}: ");
            var named = 0;
            foreach (var p in held)
            {
                var part = $"{(named > 0 ? "; " : "")}{p} ({string.Join(", ", by[p])})";
                if (text.Length + part.Length > MessageRoom) break;
                text.Append(part);
                named++;
            }
            text.Append(named < held.Count ? $"; and {held.Count - named:#,0} more." : ".");
        }
        if (none.Count > 0)
            text.Append(none.Count == 1 && text.Length + none[0].Length < MessageRoom ? $" {none[0]} held no role." : $" {none.Count:#,0} of those selected held no role.");
        if (self is not null && held.Contains(self, StringComparer.OrdinalIgnoreCase))
            text.Append(" That includes your own account.");
        return text.ToString();
    }
}
