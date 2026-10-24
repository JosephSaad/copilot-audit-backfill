using Microsoft.Extensions.Caching.Memory;

namespace CopilotInsights.Infrastructure;

/// <summary>
/// The reports a person opens most, for the "Your most used" section at the top of the navigation and the
/// start page. Counted from the audit log the dashboard already keeps, the person's own page views over the last 90
/// days: nothing is saved for it, and nobody sets it up. Worked out once a day for each person, so the order does
/// not move about while they work.
/// </summary>
public sealed class MostUsed(Db db, IMemoryCache cache, TenantAccessor tenants)
{
    public const int Days = 90, Shown = 10, AtLeast = 3;

    /// <summary>The paths the viewer opened most, most opened first: more than are shown, so those they can no longer open can be left out.</summary>
    public async Task<IReadOnlyList<string>> PathsAsync(Viewer viewer)
    {
        var who = viewer.Upn ?? viewer.WindowsName;
        if (string.IsNullOrEmpty(who)) return [];
        return (await cache.GetOrCreateAsync(tenants.Key($"most-used:{DateTime.UtcNow:yyyyMMdd}:{who.ToLowerInvariant()}"), async e =>
        {
            e.AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(24);
            try
            {
                return (IReadOnlyList<string>) await db.QueryAsync<string>(@"
SELECT TOP (40) a.Page
FROM dash.AuditLog AS a
WHERE a.Viewer = @who AND a.Kind = 'View' AND a.AtUtc >= DATEADD(DAY, -@days, SYSUTCDATETIME()) AND a.Page IS NOT NULL
GROUP BY a.Page
ORDER BY COUNT(*) DESC, MAX(a.AtUtc) DESC, a.Page;", new { who, days = Days });
            }
            catch (Microsoft.Data.SqlClient.SqlException) { return []; }
        }))!;
    }

    /// <summary>
    /// Of those, the reports the viewer may open now, in the same order: at most ten, and none until they have opened
    /// three. <paramref name="groups"/> is the navigation as the viewer sees it, so a report they may not open, or one
    /// an administrator turned off, is not offered here either. Admin pages and the start page are not reports.
    /// </summary>
    public static IReadOnlyList<Pages.NavPage> Pick(IReadOnlyList<string> paths, IReadOnlyList<Pages.NavGroup> groups)
    {
        var open = groups.SelectMany(g => g.Pages).Where(p => !p.Admin)
            .GroupBy(p => p.Path, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var picked = paths.Select(p => p.Length > 1 ? p.TrimEnd('/') : p).Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(open.ContainsKey).Select(p => open[p]).Take(Shown).ToList();
        return picked.Count >= AtLeast ? picked : [];
    }
}
