using Microsoft.Extensions.Caching.Memory;

namespace CopilotInsights.Infrastructure;

/// <summary>Settings from dash.Setting, cached for a minute.</summary>
public sealed class Settings(Db db, IMemoryCache cache, TenantAccessor tenants)
{
    public async Task<IReadOnlyDictionary<string, string?>> AllAsync() =>
        (await cache.GetOrCreateAsync(tenants.Key("dash-settings"), async e =>
        {
            e.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(1);
            var rows = await db.QueryAsync<(string Name, string? Value)>("SELECT Name, Value FROM dash.Setting");
            return (IReadOnlyDictionary<string, string?>) rows.ToDictionary(r => r.Name, r => r.Value, StringComparer.OrdinalIgnoreCase);
        }))!;

    public async Task<int> IntAsync(string name, int fallback) =>
        (await AllAsync()).TryGetValue(name, out var v) && int.TryParse(v, out var i) && i > 0 ? i : fallback;

    public async Task<string?> StringAsync(string name) =>
        (await AllAsync()).TryGetValue(name, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;

    public void Invalidate()
    {
        cache.Remove(tenants.Key("dash-settings"));
        cache.Remove(tenants.Key("privacy-mode"));
    }

    /// <summary>The time zone the data's days were cut in (audit.ReportingTimeZone()), or null on a database without it.</summary>
    public async Task<string?> ReportingTimeZoneAsync() =>
        await cache.GetOrCreateAsync(tenants.Key("reporting-zone"), async e =>
        {
            e.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5);
            try { return await db.ScalarAsync<string?>("SELECT audit.ReportingTimeZone();"); }
            catch (Microsoft.Data.SqlClient.SqlException) { return null; }
        });

    /// <summary>The zone times are shown in and schedules run by (2.4.10): see <see cref="CopilotInsights.Data.Zones"/>.</summary>
    public async Task<TimeZoneInfo> ZoneAsync() => CopilotInsights.Data.Zones.Resolve(await StringAsync(CopilotInsights.Data.Zones.Setting), await ReportingTimeZoneAsync());

    /// <summary>
    /// Privacy mode (Admin > Privacy and retention): no page, export, email or API call names
    /// anyone but the viewer themselves, and bi.Users is pseudonymized for Power BI.
    /// </summary>
    public async Task<bool> PrivacyModeAsync() =>
        await cache.GetOrCreateAsync(tenants.Key("privacy-mode"), async e =>
        {
            e.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(1);
            try { return await db.ScalarAsync<string?>("SELECT Value FROM audit.PipelineSetting WHERE Name = N'PrivacyMode'") == "Pseudonymize"; }
            catch (Microsoft.Data.SqlClient.SqlException) { return false; }
        });
}

/// <summary>An org unit on the breadcrumb or in the drill-down list.</summary>
public sealed record OrgUnit(string Upn, string Name, int PeopleBelow);

/// <summary>One point on a trend: a day (periods up to 30 days) or a Saturday-to-Friday week.</summary>
public sealed record Bucket(DateTime Key, string Label, bool Partial);

/// <summary>
/// What a page reports on: the last N complete days, and whose data. Built once
/// per request from the query string (d = days, org = the manager to drill
/// into) and the viewer's scope. Every query that touches people goes through
/// Scope() or PersonScope(), so the security model is applied in one place.
/// </summary>
public sealed class ReportContext
{
    public static readonly int[] DayChoices = [7, 14, 30, 90, 180, 365];

    public required Viewer Viewer { get; init; }
    public required int Days { get; init; }
    public required DateTime Start { get; init; }
    public required DateTime End { get; init; }     // the last complete day with data
    /// <summary>The subtree shown; null = the whole organization (allowed only for whole-organization viewers).</summary>
    public string? Root { get; init; }
    public string? RootName { get; init; }
    public required int MinGroupSize { get; init; }
    public required int InactiveDays { get; init; }
    public string? OrgTopUpn { get; init; }
    public IReadOnlyList<OrgUnit> Breadcrumb { get; init; } = [];
    public IReadOnlyList<OrgUnit> Children { get; init; } = [];

    /// <summary>Trends plot days for periods up to 30 days, weeks beyond.</summary>
    public bool Daily => Days <= 30;
    public int StartKey => Key(Start);
    public int EndKey => Key(End);
    public static int Key(DateTime d) => d.Year * 10000 + d.Month * 100 + d.Day;
    public static DateTime WeekEnding(DateTime d) => d.Date.AddDays(((int)DayOfWeek.Friday - (int)d.DayOfWeek + 7) % 7);

    /// <summary>The trend's points, oldest first. Weekly points are labeled by the Friday ending them.</summary>
    public IReadOnlyList<Bucket> Buckets()
    {
        if (Daily)
            return Enumerable.Range(0, Days).Select(i => Start.AddDays(i)).Select(d => new Bucket(d, d.ToString("d MMM"), false)).ToList();
        var list = new List<Bucket>();
        for (var we = WeekEnding(Start); we.AddDays(-6) <= End; we = we.AddDays(7))
            list.Add(new Bucket(we, "w/e " + we.ToString("d MMM"), we.AddDays(-6) < Start || we > End));
        return list;
    }

    /// <summary>SQL for a trend point from a bi.DimDate alias: the date, or its week ending.</summary>
    public string BucketSql(string dimDateAlias) => Daily ? $"{dimDateAlias}.[Date]" : $"{dimDateAlias}.WeekEnding";

    /// <summary>A join restricting a fact to the scope, on the given UserKey expression; empty for the whole organization.</summary>
    public string Scope(string userKeyExpr) =>
        Root is null ? "" : $"JOIN dash.fnScopeUsers(@root) AS sc ON sc.UserKey = {userKeyExpr}";

    /// <summary>The same, for directory people by UPN.</summary>
    public string PersonScope(string upnExpr) =>
        Root is null ? "" : $"JOIN dash.OrgClosure AS psc ON psc.AncestorUpn = @root AND psc.DescendantUpn = {upnExpr}";

    /// <summary>The standard parameters: @root, @d0/@d1 (dates), @k0/@k1 (date keys), @k7 (the last 7 days' first key), @minGroup, @inactiveKey.</summary>
    public Dapper.DynamicParameters Args(object? extra = null)
    {
        var d = new Dapper.DynamicParameters(new
        {
            root = Root, d0 = Start, d1 = End, k0 = StartKey, k1 = EndKey, k7 = Key(End.AddDays(-6)),
            minGroup = MinGroupSize, inactiveKey = Key(End.AddDays(-(InactiveDays - 1))), daily = Daily ? 1 : 0,
        });
        if (extra is not null) d.AddDynamicParams(extra);
        return d;
    }

    public string ScopeLabel => Root is null ? "Whole organization" : $"{RootName ?? Root} and their organization";
    public string PeriodLabel => $"{Start:d MMM yyyy} to {End:d MMM yyyy}";

    public string Url(string path, int? days = null, string? org = null, bool keepOrg = true, string? extra = null)
    {
        var q = new List<string> { $"d={days ?? Days}" };
        var o = org ?? (keepOrg ? Root : null);
        if (o is not null && !(o == Viewer.Upn && !Viewer.OrgWide)) q.Add("org=" + Uri.EscapeDataString(o));
        if (!string.IsNullOrEmpty(extra)) q.Add(extra);
        return path + "?" + string.Join("&", q);
    }
}

public sealed class ReportContextFactory(Db db, Settings settings)
{
    public Task<ReportContext> CreateAsync(Viewer viewer, HttpRequest request) =>
        CreateAsync(viewer, int.TryParse(request.Query["d"], out var dq) ? dq : null, request.Query["org"].FirstOrDefault());

    /// <summary>The same without a request: for email digests and the data API.</summary>
    public async Task<ReportContext> CreateAsync(Viewer viewer, int? requestedDays, string? requested)
    {
        var defaultDays = await settings.IntAsync("DefaultDays", 30);
        if (!ReportContext.DayChoices.Contains(defaultDays)) defaultDays = 30;
        var days = requestedDays is int dq && ReportContext.DayChoices.Contains(dq) ? dq : defaultDays;
        var minGroup = await settings.IntAsync("MinGroupSize", 5);
        var inactive = await settings.IntAsync("InactiveDays", 30);
        /* Set by an admin, or else found in the org chart by the refresh. */
        var top = await settings.StringAsync("OrgTopUpn") ?? await settings.StringAsync("OrgTopUpnDetected");

        /* The last complete day with data (today is still arriving). */
        var endKey = await db.ScalarAsync<int?>(@"
SELECT COALESCE(
    (SELECT MAX(DateKey) FROM dash.UserDay WHERE DateKey < YEAR(SYSDATETIME()) * 10000 + MONTH(SYSDATETIME()) * 100 + DAY(SYSDATETIME())),
    (SELECT MAX(DateKey) FROM dash.UserDay))");
        var end = endKey is int k ? new DateTime(k / 10000, k / 100 % 100, k % 100) : DateTime.Today.AddDays(-1);
        var start = end.AddDays(-(days - 1));

        /* Whose data: whole organization, or a subtree the viewer may see. */
        string? root;
        if (viewer.OrgWide)
            root = string.IsNullOrWhiteSpace(requested) ? null : requested;
        else
        {
            root = viewer.Upn ?? "(nobody)";
            if (!string.IsNullOrWhiteSpace(requested) && viewer.Upn is not null)
            {
                var under = await db.ScalarAsync<int>(
                    "SELECT COUNT(*) FROM dash.OrgClosure WHERE AncestorUpn = @v AND DescendantUpn = @r", new { v = viewer.Upn, r = requested });
                if (AccessRules.MayViewSubtree(viewer, requested, under > 0)) root = requested;
            }
        }

        string? rootName = null;
        var crumbs = new List<OrgUnit>();
        if (root is not null)
        {
            rootName = await db.ScalarAsync<string?>("SELECT COALESCE(DisplayName, Upn) FROM dash.Person WHERE Upn = @root", new { root }) ?? root;
            /* The chain from the viewer's own place (or the top) down to the root. */
            var stopAt = viewer.OrgWide ? null : viewer.Upn;
            var chain = await db.QueryAsync<(string Upn, string? Name, int PeopleBelow, int Depth)>(@"
SELECT p.Upn, p.DisplayName, p.PeopleBelow, c.Depth
FROM dash.OrgClosure AS c JOIN dash.Person AS p ON p.Upn = c.AncestorUpn
WHERE c.DescendantUpn = @root
  AND (@stop IS NULL OR EXISTS (SELECT 1 FROM dash.OrgClosure AS s WHERE s.AncestorUpn = @stop AND s.DescendantUpn = c.AncestorUpn))
ORDER BY c.Depth DESC", new { root, stop = stopAt });
            crumbs.AddRange(chain.Select(c => new OrgUnit(c.Upn, c.Name ?? c.Upn, c.PeopleBelow)));
        }

        var childOf = root ?? top;
        var children = childOf is null ? new List<OrgUnit>() :
            (await db.QueryAsync<(string Upn, string? Name, int PeopleBelow)>(
                "SELECT Upn, DisplayName, PeopleBelow FROM dash.Person WHERE ManagerUpn = @p AND PeopleBelow > 0 ORDER BY DisplayName",
                new { p = childOf })).Select(c => new OrgUnit(c.Upn, c.Name ?? c.Upn, c.PeopleBelow)).ToList();

        return new ReportContext
        {
            Viewer = viewer, Days = days, Start = start, End = end, Root = root, RootName = rootName,
            MinGroupSize = minGroup, InactiveDays = inactive, OrgTopUpn = top, Breadcrumb = crumbs, Children = children,
        };
    }
}

/// <summary>Writes dash.AuditLog: every page view, denial, export and admin change.</summary>
public sealed class AuditLog(Db db, IHttpContextAccessor http, ILogger<AuditLog> log)
{
    public async Task WriteAsync(Viewer viewer, string kind, string page, string? scope, string? detail)
    {
        try
        {
            var who = viewer.Upn ?? viewer.WindowsName;
            await db.ExecuteAsync(@"
INSERT INTO dash.AuditLog (Viewer, Kind, Page, Scope, Detail, ClientIp) VALUES (@who, @kind, @page, @scope, @detail, @ip)",
                new { who, kind, page, scope, detail = detail is { Length: > 2000 } ? detail[..2000] : detail,
                      ip = http.HttpContext?.Connection.RemoteIpAddress?.ToString() });
        }
        catch (Exception ex) { log.LogError(ex, "Could not write the audit log"); }
    }
}
