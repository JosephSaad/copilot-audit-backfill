using Microsoft.Extensions.Caching.Memory;

namespace CopilotInsights.Infrastructure;

/// <summary>
/// Settings from dash.Setting, and privacy mode: read from the database once for each request, and kept for a minute
/// for the background work (the alerts, the digests, the warm-up), which has no request.
/// </summary>
/// <remarks>
/// Read for each request since 2.4.12. They were kept for a minute everywhere, and a save cleared them only on the web
/// server that took it: with several web servers, privacy mode turned on through one left the others serving named
/// pages, files and API calls for up to a minute, and a new minimum group size or a report turned off the same. A table
/// of a few dozen short rows is read once a page now, so a change made on any web server applies to the next page on
/// all of them.
/// </remarks>
public sealed class Settings(Db db, IMemoryCache cache, TenantAccessor tenants, IHttpContextAccessor http, ILogger<Settings> log)
{
    public Task<IReadOnlyDictionary<string, string?>> AllAsync() =>
        http.HttpContext is { } request
            ? RequestMemo.GetAsync(request, tenants.Key("dash-settings"), ReadAllAsync)
            : cache.GetOrCreateAsync(tenants.Key("dash-settings"), async e =>
            {
                e.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(1);
                return await ReadAllAsync();
            })!;

    async Task<IReadOnlyDictionary<string, string?>> ReadAllAsync()
    {
        /* From the database itself, never a report page's cached reads. */
        using (QueryCache.Off())
        {
            var rows = await db.QueryAsync<(string Name, string? Value)>("SELECT Name, Value FROM dash.Setting");
            return rows.ToDictionary(r => r.Name, r => r.Value, StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>Today's date key where the data's days are cut (2.4.12, see <see cref="ReportContext.TodayKey"/>).</summary>
    public async Task<int> TodayKeyAsync() =>
        ReportContext.TodayKey(DateTime.UtcNow, CopilotInsights.Data.Zones.Resolve(null, await ReportingTimeZoneAsync()));

    public async Task<int> IntAsync(string name, int fallback) =>
        (await AllAsync()).TryGetValue(name, out var v) && int.TryParse(v, out var i) && i > 0 ? i : fallback;

    public async Task<string?> StringAsync(string name) =>
        (await AllAsync()).TryGetValue(name, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;

    public void Invalidate()
    {
        cache.Remove(tenants.Key("dash-settings"));
        cache.Remove(tenants.Key("privacy-mode"));
        RequestMemo.Forget(http.HttpContext, tenants.Key("dash-settings"));
        RequestMemo.Forget(http.HttpContext, tenants.Key("privacy-mode"));
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
    /// Privacy mode (Admin › Privacy and retention): no page, export, email or API call names
    /// anyone but the viewer themselves, and bi.Users is pseudonymized for Power BI.
    /// </summary>
    /// <remarks>
    /// A read that fails is not "off" (2.4.12): it was, for a minute and for every page and email on that server, so a
    /// failover or a missing grant turned privacy mode off without a word. Now only a database from before the setting
    /// (no table to read) is off; any other failure is on, logged, and kept for seconds so the next read tries again.
    /// Read for each request (2.4.12, see the class); the background work keeps it for a minute.
    /// </remarks>
    public Task<bool> PrivacyModeAsync() =>
        http.HttpContext is { } request
            ? RequestMemo.GetAsync(request, tenants.Key("privacy-mode"), async () => (await ReadPrivacyModeAsync()).On)
            : cache.GetOrCreateAsync(tenants.Key("privacy-mode"), async e =>
            {
                var (on, read) = await ReadPrivacyModeAsync();
                e.AbsoluteExpirationRelativeToNow = read ? TimeSpan.FromMinutes(1) : TimeSpan.FromSeconds(5);
                return on;
            });

    async Task<(bool On, bool Read)> ReadPrivacyModeAsync()
    {
        try
        {
            using (QueryCache.Off())
                return (await db.ScalarAsync<string?>("SELECT Value FROM audit.PipelineSetting WHERE Name = N'PrivacyMode'") == "Pseudonymize", true);
        }
        catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 208) { return (false, true); }   // no audit.PipelineSetting
        catch (Microsoft.Data.SqlClient.SqlException ex)
        {
            log.LogWarning(ex, "Could not read privacy mode, so it is treated as on until it can be read");
            return (true, false);
        }
    }
}

/// <summary>
/// A value read once for a request (2.4.12), kept in the request's own items: the next request reads it again. Each
/// request is one viewer's, and a key is per tenant (see <see cref="TenantAccessor.Key"/>).
/// </summary>
public static class RequestMemo
{
    public static async Task<T> GetAsync<T>(HttpContext request, string key, Func<Task<T>> read)
    {
        var k = "memo:" + key;
        if (request.Items.TryGetValue(k, out var found) && found is Task<T> known) return await known;
        var task = read();
        request.Items[k] = task;
        try { return await task; }
        catch { request.Items.Remove(k); throw; }
    }

    /// <summary>Read it again: this request changed it.</summary>
    public static void Forget(HttpContext? request, string key) => request?.Items.Remove("memo:" + key);
}

/// <summary>An org unit on the breadcrumb or in the drill-down list.</summary>
public sealed record OrgUnit(string Upn, string Name, int PeopleBelow);

/// <summary>Why a part of the organization asked for is not the one shown.</summary>
public enum OrgRefusal
{
    None,
    /// <summary>Nobody in the directory has that sign-in.</summary>
    NotFound,
    /// <summary>It is not in the viewer's part of the organization.</summary>
    Outside,
    /// <summary>It is fewer people than the minimum group size (2.4.12).</summary>
    TooSmall,
    /// <summary>Privacy mode is on, and it is not the viewer's own (2.4.12).</summary>
    Privacy,
}

/// <summary>
/// One point on a trend: a day (periods up to 30 days) or a Saturday-to-Friday week. Partial: a week the period covers
/// only in part, at its start or not yet over (2.5.2), or a day held back, one of the two newest days of records, which
/// may not all be in yet (2.5.3, see <see cref="ReportContext.DrawnTo"/>). The trend's rows keep it, for the period's
/// totals; its charts leave it out (see <see cref="CopilotInsights.Charts.Axis.Partial"/>).
/// </summary>
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

    /// <summary>
    /// The last day with any record (2.5.3): today, where the load has reached its first hours, else <see cref="End"/>.
    /// Not given (a report made without the database): taken to be <see cref="End"/>.
    /// </summary>
    public DateTime? LastRecord { get; init; }

    /// <summary>
    /// How many days a daily trend holds back, counted from the last record's day and with it (2.5.3). Purview can deliver
    /// a record up to 48 hours after the event, which is why each nightly load reads the last 48 hours again: until then
    /// the two newest days may not all be in, and a daily line ended in a drop that was the load, not the use.
    /// </summary>
    public const int HeldBackDays = 2;

    /// <summary>The last day a daily trend draws (2.5.3): with records to 4 October, 2 October. The period's totals still run to <see cref="End"/>.</summary>
    public DateTime DrawnTo => (LastRecord ?? End).AddDays(-HeldBackDays);

    /// <summary>The subtree shown; null = the whole organization (allowed only for whole-organization viewers).</summary>
    public string? Root { get; init; }
    public string? RootName { get; init; }
    /// <summary>
    /// The part of the organization the address asked for, when it is not the one shown (2.4.10): nobody in the directory
    /// has that sign-in (an old link, someone who has left), or it is outside the viewer's own part. The page says so;
    /// it used to show "Whole organization" over a report of zeros, or the viewer's own part with no word of why.
    /// </summary>
    public string? OrgAsked { get; init; }

    /// <summary>Why <see cref="OrgAsked"/> is not the part shown.</summary>
    public OrgRefusal OrgWhy { get; init; }

    /// <summary>
    /// A part of the organization that was asked for and refused on a size or privacy rule (2.4.12). A page shows the
    /// viewer's own part instead, with <see cref="OrgNote"/>; the data API and the digests refuse, and their scope is
    /// nobody, so nothing that forgets to check can show it.
    /// </summary>
    public bool OrgRefused => OrgWhy is OrgRefusal.TooSmall or OrgRefusal.Privacy;

    /// <summary>What the page says about <see cref="OrgAsked"/>. The same words whether or not such a person exists, to someone who may not see them.</summary>
    public string? OrgNote => OrgAsked is null ? null : OrgWhy switch
    {
        OrgRefusal.TooSmall => $"{OrgAsked} and the people under them are fewer than {MinGroupSize}, the minimum group size, so they are not shown on their own. This shows {Shown}.",
        OrgRefusal.Privacy => $"Privacy mode is on, so a report is not narrowed to a manager's part of the organization. This shows {Shown}.",
        _ => Root is null ? $"No one in the organization directory has the sign-in {OrgAsked}, so this shows the whole organization. They may have left, or the link may be an old one."
            : $"{OrgAsked} is not in your part of the organization, so this shows yours.",
    };

    string Shown => Root is null ? "the whole organization" : "your part of the organization";

    /// <summary>Privacy mode was on when the report was made (2.4.12): units are not named after their managers.</summary>
    public bool PrivacyMode { get; init; }
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
    public static DateTime Day(int key) => new(key / 10000, key / 100 % 100, key % 100);

    /// <summary>
    /// Today's date key in the zone the data's days are cut in (2.4.12): the day still arriving, which no period ends on.
    /// Pure, for the tests; <see cref="Settings.TodayKeyAsync"/> gives it for now and the data's zone.
    /// </summary>
    public static int TodayKey(DateTime utcNow, TimeZoneInfo zone) =>
        Key(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utcNow, DateTimeKind.Utc), zone).Date);
    public static DateTime WeekEnding(DateTime d) => d.Date.AddDays(((int)DayOfWeek.Friday - (int)d.DayOfWeek + 7) % 7);

    /// <summary>
    /// The trend's points, oldest first. Weekly points are labeled by the Friday ending them. A day after
    /// <see cref="DrawnTo"/> is marked partial, as a week the period covers only in part is: kept, and not drawn.
    /// </summary>
    public IReadOnlyList<Bucket> Buckets()
    {
        if (Daily)
            return Enumerable.Range(0, Days).Select(i => Start.AddDays(i)).Select(d => new Bucket(d, d.ToString("d MMM"), d > DrawnTo)).ToList();
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

    /// <summary>
    /// Query values a page keeps on every link to itself: its own filters, which choosing a period or a part of the
    /// organization must not drop. The page sets it; links to other pages do not carry it.
    /// </summary>
    public (string Path, string Query)? Kept { get; set; }

    /// <summary>The virtual directory the site answers under ("/CopilotDashboard"), or empty at the root: every address it writes starts with it.</summary>
    public string PathBase { get; set; } = "";

    public string Url(string path, int? days = null, string? org = null, bool keepOrg = true, string? extra = null)
    {
        var q = new List<string> { $"d={days ?? Days}" };
        var o = org ?? (keepOrg ? Root : null);
        if (o is not null && !(o == Viewer.Upn && !Viewer.OrgWide)) q.Add("org=" + Uri.EscapeDataString(o));
        if (Kept is { } k && string.Equals(path, k.Path, StringComparison.OrdinalIgnoreCase)) q.Add(k.Query);
        if (!string.IsNullOrEmpty(extra)) q.Add(extra);
        return (path.StartsWith('/') ? PathBase : "") + path + "?" + string.Join("&", q);
    }
}

public sealed class ReportContextFactory(Db db, Settings settings, DataStamp stamp)
{
    /// <summary>
    /// SQL for the size of a manager's part of the organization, against the minimum group size (2.4.12): the enabled
    /// people in it, the manager among them (dash.OrgClosure has a depth-0 row for each person). It was dash.Person's
    /// PeopleBelow + 1, which counts disabled accounts too, so a part of three people and five leavers was shown on its
    /// own where every other minimum counts only the people still here. A seek on the closure's key, which leads with
    /// the ancestor.
    /// </summary>
    public static string EnabledInPartSql(string upnExpr) =>
        $"(SELECT COUNT(*) FROM dash.OrgClosure AS ec JOIN dash.Person AS ep ON ep.Upn = ec.DescendantUpn WHERE ec.AncestorUpn = {upnExpr} AND ep.AccountEnabled = 1)";

    /// <param name="forPage">
    /// A page a person is looking at: a part of the organization that is not in the directory falls back to the whole
    /// organization, with a note (2.4.10). The data API and the digests keep to what was asked for, and so to nothing:
    /// a digest for a manager who has left must not start showing everyone.
    /// </param>
    public async Task<ReportContext> CreateAsync(Viewer viewer, HttpRequest request, bool forPage = false)
    {
        var c = await CreateAsync(viewer, int.TryParse(request.Query["d"], out var dq) ? dq : null, request.Query["org"].FirstOrDefault(), forPage);
        c.PathBase = request.PathBase.Value?.TrimEnd('/') ?? "";
        return c;
    }

    /// <summary>The same without a request: for email digests and the data API.</summary>
    public async Task<ReportContext> CreateAsync(Viewer viewer, int? requestedDays, string? requested, bool forPage = false)
    {
        requested = string.IsNullOrWhiteSpace(requested) ? null : requested.Trim();
        string? asked = null;
        var defaultDays = await settings.IntAsync("DefaultDays", 30);
        if (!ReportContext.DayChoices.Contains(defaultDays)) defaultDays = 30;
        var days = requestedDays is int dq && ReportContext.DayChoices.Contains(dq) ? dq : defaultDays;
        var minGroup = await settings.IntAsync("MinGroupSize", 5);
        var inactive = await settings.IntAsync("InactiveDays", 30);
        /* Set by an admin, or else found in the org chart by the refresh. */
        var top = await settings.StringAsync("OrgTopUpn") ?? await settings.StringAsync("OrgTopUpnDetected");

        /* The last complete day with data (today is still arriving), today being the day in the zone the data's days are
           cut in (2.4.12). It was SQL Server's own date: with the server on UTC and the days cut in Eastern Time, from
           8 p.m. Eastern the day still loading counted as complete, and every period ended on it. */
        var today = await settings.TodayKeyAsync();
        /* With it, the last day with any record, today's included (2.5.3): a daily trend stops two days before that one. */
        var (completeKey, lastKey) = (await db.QueryAsync<(int? Complete, int? Last)>(@"
SELECT (SELECT MAX(DateKey) FROM dash.UserDay WHERE DateKey < @today), (SELECT MAX(DateKey) FROM dash.UserDay);", new { today })).Single();
        var end = (completeKey ?? lastKey) is int k ? new DateTime(k / 10000, k / 100 % 100, k % 100) : ReportContext.Day(today).AddDays(-1);
        DateTime? lastRecord = lastKey is int l ? ReportContext.Day(l) : null;
        var start = end.AddDays(-(days - 1));

        /* Whose data: whole organization, or a subtree the viewer may see. */
        string? root;
        var why = OrgRefusal.None;
        if (viewer.OrgWide)
        {
            /* A part of the organization is someone in the directory, for the API and the digests too (2.4.12). They took
               any text: one longer than a sign-in can be was cut to 256 characters by dash.fnScopeUsers but not by the
               name looked up for the audit log, which then could not be written. Nobody, now, and the API returns nothing. */
            root = requested;
            if (root is not null && (root.Length > 256 || await db.ScalarAsync<int>("SELECT COUNT(*) FROM dash.Person WHERE Upn = @root", new { root }) == 0))
                (asked, root, why) = (root, forPage ? null : "(nobody)", OrgRefusal.NotFound);
        }
        else
        {
            root = viewer.Upn ?? "(nobody)";
            if (requested is not null && viewer.Upn is not null)
            {
                var under = await db.ScalarAsync<int>(
                    "SELECT COUNT(*) FROM dash.OrgClosure WHERE AncestorUpn = @v AND DescendantUpn = @r", new { v = viewer.Upn, r = requested });
                if (AccessRules.MayViewSubtree(viewer, requested, under > 0)) root = requested;
            }
            if (requested is not null && !string.Equals(root, requested, StringComparison.OrdinalIgnoreCase)) (asked, why) = (requested, OrgRefusal.Outside);
        }

        /* A part of the organization other than the viewer's own (2.4.12) is shown only when it is at least the minimum
           group size, and never in privacy mode, where it is named after its manager. Any sign-in in org= was taken: a
           person with nobody under them made every page, and the API, a report of that one person. A page falls back to
           the viewer's own part with a note; the API and the digests get nobody, and refuse. */
        var privacy = await settings.PrivacyModeAsync();
        /* The size of a part and the parts below a manager are the org chart's, which only the dashboard's refresh
           rewrites: they are kept with the report data under the data stamp (2.5.1). Counted on every request, the enabled
           people under each part below the top cost 0.4 seconds a page at 210,000 people, from memory or not. Who the
           viewer is and what they may open are still read from the database for every request. */
        await stamp.EnsureAsync();
        if (requested is not null && root is not null && why == OrgRefusal.None && !string.Equals(root, viewer.Upn, StringComparison.OrdinalIgnoreCase))
        {
            if (privacy) why = OrgRefusal.Privacy;
            else
            {
                int enabled;
                using (QueryCache.Use()) enabled = await db.ScalarAsync<int>("SELECT " + EnabledInPartSql("@root"), new { root });
                if (enabled < minGroup) why = OrgRefusal.TooSmall;
            }
            if (why != OrgRefusal.None)
                (asked, root) = (root, !forPage ? "(nobody)" : viewer.OrgWide ? null : viewer.Upn ?? "(nobody)");
        }
        /* What was asked for is shown back in the note: no longer than a sign-in can be. */
        if (asked is { Length: > 256 }) asked = asked[..256];

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
            /* In privacy mode the chain names only the viewer: the managers above them are other people. */
            if (privacy) crumbs.RemoveAll(c => !string.Equals(c.Upn, viewer.Upn, StringComparison.OrdinalIgnoreCase));
        }

        /* The parts to drill into: managers whose part reaches the minimum group size in enabled people, the rule a
           request is held to above; none in privacy mode, where each would be offered by its manager's name. */
        var childOf = privacy ? null : root ?? top;
        var children = new List<OrgUnit>();
        if (childOf is not null)
            using (QueryCache.Use())
                children = (await db.QueryAsync<(string Upn, string? Name, int PeopleBelow)>(
                    "SELECT p.Upn, p.DisplayName, p.PeopleBelow FROM dash.Person AS p WHERE p.ManagerUpn = @p AND p.PeopleBelow > 0 AND " +
                    EnabledInPartSql("p.Upn") + " >= @min ORDER BY p.DisplayName",
                    new { p = childOf, min = minGroup })).Select(c => new OrgUnit(c.Upn, c.Name ?? c.Upn, c.PeopleBelow)).ToList();

        return new ReportContext
        {
            Viewer = viewer, Days = days, Start = start, End = end, LastRecord = lastRecord, Root = root, RootName = rootName, OrgAsked = asked, OrgWhy = why,
            PrivacyMode = privacy,
            MinGroupSize = minGroup, InactiveDays = inactive, OrgTopUpn = top, Breadcrumb = crumbs, Children = children,
        };
    }
}

/// <summary>Writes dash.AuditLog: every page view, denial, export and admin change.</summary>
/// <remarks>
/// A write that fails is not swallowed (2.4.12). It was logged and the page, the file or the API answer went out all
/// the same, so a full log or disk, a read-only database or a missing grant served everything with no trace, and a
/// scope longer than its column (an org= of 400 characters on the API) did so on demand. Every value is now cut to its
/// column, a failed write is tried once more, and then the request is refused (<see cref="AuditLogUnavailableException"/>,
/// answered 503 in Program.cs): what is not in the audit log is not served.
/// </remarks>
public sealed class AuditLog(Db db, IHttpContextAccessor http, ILogger<AuditLog> log)
{
    /// <summary>How long to wait before the second try.</summary>
    public static TimeSpan RetryDelay { get; set; } = TimeSpan.FromMilliseconds(500);

    public Task WriteAsync(Viewer viewer, string kind, string page, string? scope, string? detail) => WriteAsync(viewer, kind, page, scope, detail, null);

    /// <param name="change">What a change submitted and altered, as JSON (see AuditChange), in dash.AuditLog.Change where the database has it.</param>
    public async Task WriteAsync(Viewer viewer, string kind, string page, string? scope, string? detail, string? change)
    {
        var args = new
        {
            who = Fit(viewer.Upn ?? viewer.WindowsName, 256), kind = Fit(kind, 12), page = Fit(page, 200), scope = Fit(scope, 400),
            detail = Fit(detail, 2000), ip = Fit(http.HttpContext?.Connection.RemoteIpAddress?.ToString(), 64), change,
        };
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await db.ExecuteAsync(change is null ? @"
INSERT INTO dash.AuditLog (Viewer, Kind, Page, Scope, Detail, ClientIp) VALUES (@who, @kind, @page, @scope, @detail, @ip)" : @"
IF COL_LENGTH(N'dash.AuditLog', N'Change') IS NULL
    INSERT INTO dash.AuditLog (Viewer, Kind, Page, Scope, Detail, ClientIp) VALUES (@who, @kind, @page, @scope, @detail, @ip);
ELSE
    EXEC sp_executesql N'INSERT INTO dash.AuditLog (Viewer, Kind, Page, Scope, Detail, ClientIp, Change) VALUES (@who, @kind, @page, @scope, @detail, @ip, @change)',
        N'@who NVARCHAR(256), @kind VARCHAR(12), @page NVARCHAR(200), @scope NVARCHAR(400), @detail NVARCHAR(2000), @ip VARCHAR(64), @change NVARCHAR(MAX)',
        @who, @kind, @page, @scope, @detail, @ip, @change;", args);
                return;
            }
            catch (Exception ex) when (attempt == 1)
            {
                log.LogWarning(ex, "Could not write the audit log; trying once more");
                await Task.Delay(RetryDelay);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Could not write the audit log, so the request is refused ({Kind} {Page} by {Viewer})", args.kind, args.page, args.who);
                throw new AuditLogUnavailableException(ex);
            }
        }
    }

    /// <summary>A value cut to the width of its column, so it can never be what makes the write fail.</summary>
    public static string? Fit(string? value, int max) => value is { } v && v.Length > max ? v[..max] : value;
}

/// <summary>The audit log could not be written (2.4.12): the request is refused rather than served unrecorded.</summary>
public sealed class AuditLogUnavailableException(Exception inner) : Exception("The audit log could not be written.", inner)
{
    /// <summary>What the viewer is told, with the 503.</summary>
    public const string Message503 = "Copilot Insights could not write its audit log, so nothing is shown: every page, export and API call is recorded there before it is served. " +
        "Try again in a minute. If it goes on, the database administrator should check the SQL Server: a full log or disk, a read-only database, " +
        "or the site's account losing its right to write dash.AuditLog.";
}
