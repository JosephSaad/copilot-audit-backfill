using System.Text.Json;
using Dapper;

namespace CopilotInsights.Infrastructure;

/// <summary>
/// What an audit row records of a change, beside its sentence (dash.AuditLog.Change): the values submitted, a secret's
/// masked, and the settings and role grants as they were before the change and after it, the ones that changed only.
/// </summary>
public static class AuditChange
{
    static readonly System.Text.RegularExpressions.Regex SecretName = new(
        @"password|secret|webhook|credential|connectionstring|apikey|api_key|accesskey|privatekey|bearer|token",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// A field or setting whose value is never written down: passwords, secrets, credentials, connection strings, access
    /// keys and tokens, and the webhook's address. Not the credit model's TokensPerTurn and the like, which are counts.
    /// </summary>
    public static bool Secret(string name) => SecretName.IsMatch(name) && !name.StartsWith("Tokens", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A value as written down: a secret's as "(set)" or empty; an address's query and anything after its path left
    /// out ("https://x.example/hook?(hidden)"), whatever the field is named, since a Teams or Logic Apps address carries
    /// its signature there; at most 400 characters.
    /// </summary>
    public static string Masked(string name, string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        if (Secret(name)) return "(set)";
        /* A success story's words stay with the story, so purging it removes them; an edit's earlier text is its own line. */
        if (string.Equals(name, "quote", StringComparison.OrdinalIgnoreCase)) return "(kept with the story)";
        if (Uri.TryCreate(value.Trim(), UriKind.Absolute, out var u) && u.Scheme is "http" or "https" && (u.Query.Length > 1 || u.UserInfo.Length > 0))
            return u.GetLeftPart(UriPartial.Path).Replace(u.UserInfo + "@", "", StringComparison.Ordinal) + "?(hidden)";
        return value.Length > 400 ? value[..400] + "…" : value;
    }

    /// <summary>The values submitted, by field, masked (<see cref="Masked"/>); the anti-forgery token left out. Pure, for the tests.</summary>
    public static SortedDictionary<string, string> Submitted(IEnumerable<(string Name, string Value)> form)
    {
        var d = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in form)
        {
            if (name.StartsWith("__", StringComparison.Ordinal)) continue;
            d[name] = Masked(name, value);
        }
        return d;
    }

    /// <summary>The settings and the role grants, now: read inside the change's transaction, before it and after it.</summary>
    public sealed record Snapshot(Dictionary<string, string?> Settings, HashSet<string> Grants);

    public static async Task<Snapshot> SnapshotAsync(Db db)
    {
        var settings = (await db.QueryAsync<(string Name, string? Value)>("SELECT Name, Value FROM dash.Setting;")).ToDictionary(x => x.Name, x => x.Value, StringComparer.OrdinalIgnoreCase);
        /* A grant as one line: who, which role, the Admin pages it is limited to where it is, and a section grant's sections. */
        var grants = (await db.QueryAsync<dynamic>("SELECT * FROM dash.RoleGrant;"))
            .Select(g => (IDictionary<string, object?>)g)
            .Select(g => $"{g["Principal"]} {g["Role"]}" + (g.TryGetValue("OrgWide", out var o) && o is true ? " across the organization" : "")
                         + (g.TryGetValue("Pages", out var p) && p is string s && s.Length > 0 ? $" ({s})" : "")
                         + (g.TryGetValue("Sections", out var sec) && sec is string ss && ss.Length > 0 ? $" [{ss}]" + (g.TryGetValue("NamedPeople", out var np) && np is true ? " with named people" : "") : ""))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        /* Access profiles, as what each gives and who holds it: a change to a profile reaches everyone who holds it. */
        grants.UnionWith(await db.QueryAsync<string>(@"
IF OBJECT_ID(N'dash.ProfileGrant', N'U') IS NOT NULL
    SELECT CONCAT(g.Principal, N' profile ', p.Name) FROM dash.ProfileGrant AS g JOIN dash.AccessProfile AS p ON p.ProfileId = g.ProfileId
    UNION ALL
    SELECT CONCAT(N'profile ', p.Name, N' gives ', r.Role, CASE WHEN r.OrgWide = 1 THEN N' across the organization' ELSE N'' END,
                  CASE WHEN NULLIF(r.Pages, N'') IS NOT NULL THEN CONCAT(N' (', r.Pages, N')') ELSE N'' END)
    FROM dash.AccessProfile AS p JOIN dash.AccessProfileRole AS r ON r.ProfileId = p.ProfileId
    UNION ALL
    SELECT CONCAT(N'profile ', p.Name, N' request ', ISNULL(p.RequestUrl, N'none'), N' owner ', ISNULL(p.OwnerUpn, N'none')) FROM dash.AccessProfile AS p;"));
        /* A profile's sections, where the database has them (3.1.0). */
        if (await db.ScalarAsync<int>("SELECT CASE WHEN COL_LENGTH(N'dash.AccessProfileRole', N'Sections') IS NULL THEN 0 ELSE 1 END;") == 1)
            grants.UnionWith(await db.QueryAsync<string>(@"
SELECT CONCAT(N'profile ', p.Name, N' sections ', r.Sections, CASE WHEN r.NamedPeople = 1 THEN N' with named people' ELSE N'' END)
FROM dash.AccessProfile AS p JOIN dash.AccessProfileRole AS r ON r.ProfileId = p.ProfileId WHERE r.Role = 'Sections';"));
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
                changed[name] = Secret(name) ? new { before = (string?)"(hidden)", after = (string?)"(hidden)" }
                    : new { before = was is null ? null : (string?)Masked(name, was), after = now is null ? null : (string?)Masked(name, now) };
            }
        var granted = before is null || after is null ? [] : after.Grants.Except(before.Grants, StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToList();
        var revoked = before is null || after is null ? [] : before.Grants.Except(after.Grants, StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToList();
        if (submitted.Count == 0 && changed.Count == 0 && granted.Count == 0 && revoked.Count == 0) return null;
        return JsonSerializer.Serialize(new { submitted, settings = changed.Count == 0 ? null : changed, granted = granted.Count == 0 ? null : granted, revoked = revoked.Count == 0 ? null : revoked },
            new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
    }
}
