using System.Text.Json;
using Dapper;

namespace CopilotInsights.Infrastructure;

/// <summary>
/// What an audit row records of a change, beside its sentence (dash.AuditLog.Change): the values submitted, a secret's
/// masked, and the settings and role grants as they were before the change and after it, the ones that changed only.
/// </summary>
public static class AuditChange
{
    /// <summary>A form field whose value is never written down: passwords, secrets, tokens, keys and the webhook's address.</summary>
    public static bool Secret(string name) =>
        name.Contains("password", StringComparison.OrdinalIgnoreCase) || name.Contains("secret", StringComparison.OrdinalIgnoreCase)
        || name.Contains("token", StringComparison.OrdinalIgnoreCase) || name.Contains("webhook", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith("key", StringComparison.OrdinalIgnoreCase);

    /// <summary>The values submitted, by field, a secret's masked; the anti-forgery token left out. Pure, for the tests.</summary>
    public static SortedDictionary<string, string> Submitted(IEnumerable<(string Name, string Value)> form)
    {
        var d = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in form)
        {
            if (name.StartsWith("__", StringComparison.Ordinal)) continue;
            d[name] = Secret(name) ? (string.IsNullOrEmpty(value) ? "" : "(set)") : value.Length > 400 ? value[..400] + "…" : value;
        }
        return d;
    }

    /// <summary>The settings and the role grants, now: read inside the change's transaction, before it and after it.</summary>
    public sealed record Snapshot(Dictionary<string, string?> Settings, HashSet<string> Grants);

    public static async Task<Snapshot> SnapshotAsync(Db db)
    {
        var settings = (await db.QueryAsync<(string Name, string? Value)>("SELECT Name, Value FROM dash.Setting;")).ToDictionary(x => x.Name, x => x.Value, StringComparer.OrdinalIgnoreCase);
        /* A grant as one line: who, which role, and the Admin pages it is limited to where it is. */
        var grants = (await db.QueryAsync<dynamic>("SELECT * FROM dash.RoleGrant;"))
            .Select(g => (IDictionary<string, object?>)g)
            .Select(g => $"{g["Principal"]} {g["Role"]}" + (g.TryGetValue("Pages", out var p) && p is string s && s.Length > 0 ? $" ({s})" : ""))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new(settings, grants);
    }

    /// <summary>The record as JSON: submitted, and what changed in the settings and the grants. Pure, for the tests.</summary>
    public static string? Json(IReadOnlyDictionary<string, string> submitted, Snapshot? before, Snapshot? after)
    {
        var changed = new SortedDictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (before is not null && after is not null)
            foreach (var name in before.Settings.Keys.Union(after.Settings.Keys, StringComparer.OrdinalIgnoreCase))
            {
                before.Settings.TryGetValue(name, out var was);
                after.Settings.TryGetValue(name, out var now);
                if (string.Equals(was, now, StringComparison.Ordinal)) continue;
                changed[name] = Secret(name) ? new { before = "(hidden)", after = "(hidden)" } : new { before = was, after = now };
            }
        var granted = before is null || after is null ? [] : after.Grants.Except(before.Grants, StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToList();
        var revoked = before is null || after is null ? [] : before.Grants.Except(after.Grants, StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToList();
        if (submitted.Count == 0 && changed.Count == 0 && granted.Count == 0 && revoked.Count == 0) return null;
        return JsonSerializer.Serialize(new { submitted, settings = changed.Count == 0 ? null : changed, granted = granted.Count == 0 ? null : granted, revoked = revoked.Count == 0 ? null : revoked },
            new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
    }
}
