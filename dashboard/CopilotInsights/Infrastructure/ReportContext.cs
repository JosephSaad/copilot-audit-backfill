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

    /// <summary>Today's date key where the data's days are cut (see <see cref="ReportContext.TodayKey"/>).</summary>
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

    /// <summary>The zone times are shown in and schedules run by: see <see cref="CopilotInsights.Data.Zones"/>.</summary>
    public async Task<TimeZoneInfo> ZoneAsync() => CopilotInsights.Data.Zones.Resolve(await StringAsync(CopilotInsights.Data.Zones.Setting), await ReportingTimeZoneAsync());

    /// <summary>
    /// Privacy mode (Admin › Privacy and retention): no page, export, email or API call names
    /// anyone but the viewer themselves, and bi.Users is pseudonymized for Power BI.
    /// </summary>
    /// <remarks>
    /// A read that fails is not "off": it was, for a minute and for every page and email on that server, so a
    /// failover or a missing grant turned privacy mode off without a word. Now only a database from before the setting
    /// (no table to read) is off; any other failure is on, logged, and kept for seconds so the next read tries again.
    /// Read for each request (see the class); the background work keeps it for a minute.
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
/// A value read once for a request, kept in the request's own items: the next request reads it again. Each
/// request is one viewer's, and a key is per tenant (see <see cref="TenantAccessor.Key"/>).
/// </summary>
public static class RequestMemo
{
    /* A page may read its data side by side (InsightsPage.Queries), so two of its reads can ask for the same value at
       once: the request's items are a plain dictionary, used under a lock, and the first read is the one both wait for. */
    public static async Task<T> GetAsync<T>(HttpContext request, string key, Func<Task<T>> read)
    {
        var k = "memo:" + key;
        Task<T> task;
        lock (request.Items)
        {
            if (request.Items.TryGetValue(k, out var found) && found is Task<T> known) task = known;
            else request.Items[k] = task = read();
        }
        try { return await task; }
        catch
        {
            lock (request.Items) { if (request.Items.TryGetValue(k, out var t) && ReferenceEquals(t, task)) request.Items.Remove(k); }
            throw;
        }
    }

    /// <summary>Read it again: this request changed it.</summary>
    public static void Forget(HttpContext? request, string key)
    {
        if (request is null) return;
        lock (request.Items) request.Items.Remove("memo:" + key);
    }
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
    /// <summary>It is fewer people than the minimum group size.</summary>
    TooSmall,
    /// <summary>Privacy mode is on, and it is not the viewer's own.</summary>
    Privacy,
}

/// <summary>
/// One point on a trend: a day (periods up to 30 days) or a Saturday-to-Friday week. Partial: a week the period covers
/// only in part, at its start or not yet over, or a day held back, one of the two newest days of records, which
/// may not all be in yet (see <see cref="ReportContext.DrawnTo"/>). The trend's rows keep it, for the period's
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
    /// <summary>The period choice that means every day with data, from the first record to the last complete day (3.1.3).</summary>
    public const int AllData = 0;

    /// <summary>The periods every report offers: 7 days to two years, and all the data there is (3.1.3: 730 and All data).</summary>
    public static readonly int[] DayChoices = [7, 14, 30, 90, 180, 365, 730, AllData];

    /// <summary>How a period choice reads in a list: "Last 90 days", or "All data".</summary>
    public static string ChoiceLabel(int choice) => choice == AllData ? "All data" : $"Last {choice:#,0} days";

    public required Viewer Viewer { get; init; }

    /// <summary>
    /// The days the period covers, start to end: for All data, every day from the first with data. What a query
    /// counts and divides by; <see cref="Choice"/> is what was chosen.
    /// </summary>
    public required int Days { get; init; }

    int? _choice;
    /// <summary>The period as chosen and kept in links: a number of days, or <see cref="AllData"/>.</summary>
    public int Choice { get => _choice ?? Days; init => _choice = value; }

    public bool IsAllData => Choice == AllData;

    /// <summary>The period in words for a sentence: "the last 90 days", or "all the data, since 14 Aug 2025".</summary>
    public string PeriodPhrase => IsAllData ? $"all the data, since {Start:d MMM yyyy}" : $"the last {Days:#,0} days";
    public required DateTime Start { get; init; }
    public required DateTime End { get; init; }     // the last complete day with data

    /// <summary>
    /// The last day with any record: today, where the load has reached its first hours, else <see cref="End"/>.
    /// Not given (a report made without the database): taken to be <see cref="End"/>.
    /// </summary>
    public DateTime? LastRecord { get; init; }

    /// <summary>
    /// How many days a daily trend holds back, counted from the last record's day and with it. Purview can deliver
    /// a record up to 48 hours after the event, which is why each nightly load reads the last 48 hours again: until then
    /// the two newest days may not all be in, and a daily line ended in a drop that was the load, not the use.
    /// </summary>
    public const int HeldBackDays = 2;

    /// <summary>The last day a daily trend draws: with records to 4 October, 2 October. The period's totals still run to <see cref="End"/>.</summary>
    public DateTime DrawnTo => (LastRecord ?? End).AddDays(-HeldBackDays);

    /// <summary>The subtree shown; null = the whole organization (allowed only for whole-organization viewers).</summary>
    public string? Root { get; init; }
    public string? RootName { get; init; }
    /// <summary>
    /// The part of the organization the address asked for, when it is not the one shown: nobody in the directory
    /// has that sign-in (an old link, someone who has left), or it is outside the viewer's own part. The page says so;
    /// it used to show "Whole organization" over a report of zeros, or the viewer's own part with no word of why.
    /// </summary>
    public string? OrgAsked { get; init; }

    /// <summary>Why <see cref="OrgAsked"/> is not the part shown.</summary>
    public OrgRefusal OrgWhy { get; init; }

    /// <summary>
    /// A part of the organization that was asked for and refused on a size or privacy rule. A page shows the
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

    /// <summary>Privacy mode was on when the report was made: units are not named after their managers.</summary>
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
    /// Today's date key in the zone the data's days are cut in: the day still arriving, which no period ends on.
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

    /* ---- The totals the refresh keeps --------------------------------------------------------------------------
       dash.usp_RefreshDashboard keeps each person's sums per week (dash.UserWeek) and per period a page offers
       (dash.UserPeriod), the same per app and per agent (dash.UserItemDay, dash.UserItemWeek, dash.UserItemPeriod), and
       each person's last day at each site (dash.SitePerson). A page reads them through the helpers below, which fall
       back to summing dash.UserDay or bi.FactInteraction where the refresh has not built them yet (a database upgraded
       but not refreshed since) or for a period that ends on a day they were not built for. The answer is the same
       either way: only the time differs. */

    /// <summary>Whether the refresh has built the totals (dash.UserWeek, dash.UserPeriod, dash.AdoptionDay, dash.UserActivity, dash.UserItemDay).</summary>
    public bool Rollups { get; init; }

    /// <summary>The period choices (<see cref="DayChoices"/>, 0 for All data) dash.UserPeriod holds ending on this context's last day.</summary>
    public IReadOnlySet<int> RolledDays { get; init; } = new HashSet<int>();

    /// <summary>The period choices dash.UserItemPeriod holds ending on this context's last day.</summary>
    public IReadOnlySet<int> RolledItemDays { get; init; } = new HashSet<int>();

    /// <summary>The columns of <see cref="PerPerson"/> and <see cref="PerPoint"/>: dash.UserPeriod's and dash.UserWeek's sums.</summary>
    public const string Sums = "Interactions, Prompts, AgentInteractions, WebGrounded, ChatInteractions, LicensedUse, PremiumUse, Jailbreak, PromptInjection, BlockedResource, LabeledContent, Sessions, MultiTurnSessions";

    static IEnumerable<string> SumColumns => Sums.Split(", ");

    static string Summed(string alias) => string.Join(", ", SumColumns.Select(c => $"SUM({alias}.{c}) AS {c}"));

    /// <summary>
    /// Each person's sums over a period ending on the period's last day, as a derived table with dash.UserPeriod's
    /// columns: UserKey, ActiveDays, PremiumDays, LastActiveKey, MostApps (the most apps in one day) and <see cref="Sums"/>.
    /// <paramref name="choice"/> is the window: the period chosen by default, or a fixed one (30 or 90 days) ending on its
    /// last day, whose first day key is in the parameter <paramref name="fromKey"/>. Read from dash.UserPeriod when the
    /// refresh built that period, else summed from dash.UserDay. A person with no day in it has no row.
    /// </summary>
    public string PerPerson(int? choice = null, string fromKey = "@k0")
    {
        var c = choice ?? Choice;
        return RolledDays.Contains(c)
            ? $"(SELECT up.UserKey, CAST(up.ActiveDays AS INT) AS ActiveDays, CAST(up.PremiumDays AS INT) AS PremiumDays, up.LastActiveKey, up.MostApps, {string.Join(", ", SumColumns.Select(s => "up." + s))} FROM dash.UserPeriod AS up WHERE up.EndKey = @k1 AND up.Days = {c})"
            : $@"(SELECT ud.UserKey, COUNT(CASE WHEN ud.Interactions > 0 THEN 1 END) AS ActiveDays, COUNT(CASE WHEN ud.PremiumUse > 0 THEN 1 END) AS PremiumDays,
        MAX(CASE WHEN ud.Interactions > 0 THEN ud.DateKey END) AS LastActiveKey, MAX(ud.AppsUsed) AS MostApps, {Summed("ud")}
    FROM dash.UserDay AS ud WHERE ud.DateKey BETWEEN {fromKey} AND @k1 GROUP BY ud.UserKey)";
    }

    /// <summary>
    /// Each person's sums at each trend point from <paramref name="from"/> (a date-key parameter) to the period's last
    /// day, as a derived table: Bucket (the day, or the week's Friday), UserKey, ActiveDays and <see cref="Sums"/>. A day is
    /// dash.UserDay's row; a week inside the range is dash.UserWeek's row, and a week the range cuts (the first or the last)
    /// is summed from its days in it. Before the totals are built, every week is summed from its days.
    /// </summary>
    public string PerPoint(string from = "@k0")
    {
        if (Daily)
            return $@"(SELECT d.[Date] AS Bucket, ud.UserKey, CASE WHEN ud.Interactions > 0 THEN 1 ELSE 0 END AS ActiveDays, {string.Join(", ", SumColumns.Select(c => "ud." + c))}
    FROM dash.UserDay AS ud JOIN bi.DimDate AS d ON d.DateKey = ud.DateKey WHERE ud.DateKey BETWEEN {from} AND @k1)";
        var cut = $"(SELECT w.WeekEnding FROM bi.DimDate AS w WHERE w.DateKey BETWEEN {from} AND @k1 GROUP BY w.WeekEnding HAVING COUNT(*) < 7)";
        var days = $@"SELECT d.WeekEnding AS Bucket, ud.UserKey, COUNT(CASE WHEN ud.Interactions > 0 THEN 1 END) AS ActiveDays, {Summed("ud")}
    FROM dash.UserDay AS ud JOIN bi.DimDate AS d ON d.DateKey = ud.DateKey
    WHERE ud.DateKey BETWEEN {from} AND @k1{(Rollups ? $" AND d.WeekEnding IN {cut}" : "")}
    GROUP BY d.WeekEnding, ud.UserKey";
        if (!Rollups) return $"({days})";
        /* A whole week inside the range has all seven of its days in it; the weeks are read between the first and the last
           Friday the range can hold whole, so the columnstore skips the rest. */
        return $@"(SELECT uw.WeekEnding AS Bucket, uw.UserKey, CAST(uw.ActiveDays AS INT) AS ActiveDays, {string.Join(", ", SumColumns.Select(c => "uw." + c))}
    FROM dash.UserWeek AS uw
    WHERE uw.WeekEnding BETWEEN DATEADD(DAY, 6, DATEFROMPARTS({from} / 10000, {from} / 100 % 100, {from} % 100)) AND DATEFROMPARTS(@k1 / 10000, @k1 / 100 % 100, @k1 % 100)
      AND uw.WeekEnding NOT IN {cut}
    UNION ALL
    {days})";
    }

    /* An app is kind A, an agent kind G (dash.UserItemDay); before the totals, the same from bi.FactInteraction, an agent
       being any but the "(none)" one. */
    static (string From, string Item, string Only) FactItem(char kind) => kind switch
    {
        'A' => ("bi.FactInteraction AS f", "f.AppKey", ""),
        'G' => ("bi.FactInteraction AS f", "f.AgentKey", " AND f.AgentKey IN (SELECT ag.AgentKey FROM bi.DimAgent AS ag WHERE ag.AgentId <> N'(none)')"),
        'U' => ("bi.FactInteraction AS f", "f.UseCaseKey", " AND f.UseCaseKey IS NOT NULL"),
        'L' => ("bi.FactInteraction AS f", "f.LicenseTypeKey", " AND f.LicenseTypeKey IS NOT NULL"),
        'M' => ("bi.FactInteraction AS f", "f.ModelKey", " AND f.ModelKey IS NOT NULL"),
        /* The three flags, one row each for the interactions that have it, as the refresh counts them. */
        'F' => ("bi.FactInteraction AS f CROSS APPLY (VALUES (CASE WHEN f.MemoryUpdated = 1 THEN 1 END), (CASE WHEN f.CalledAnotherAgent = 1 THEN 2 END), (CASE WHEN f.DlpEvaluationDeferred = 1 THEN 3 END)) AS fk (Item)", "fk.Item", " AND fk.Item IS NOT NULL"),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "An item kind is A, G, U, L, M or F."),
    };

    /// <summary>The kinds dash.UserItemDay holds besides apps and agents (setting DashItemKinds, written by the refresh).</summary>
    public string ItemKinds { get; init; } = "";

    /// <summary>Whether dash.UserItemDay holds this kind: apps and agents once the totals are built, the others once a refresh has added them.</summary>
    bool HasItems(char kind) => Rollups && (kind is 'A' or 'G' || ItemKinds.Contains(kind));

    /// <summary>
    /// Each person's use of each app (<paramref name="kind"/> 'A') or agent ('G') over the period, as a derived table:
    /// UserKey, Item (the app's or the agent's key), ActiveDays, LastActiveKey, Interactions, Prompts, Resources
    /// (resources read) and Web (interactions that searched the web). Read from dash.UserItemPeriod when the refresh built
    /// that period, else summed from dash.UserItemDay, or before the totals from bi.FactInteraction.
    /// <paramref name="choice"/> and <paramref name="fromKey"/> as for <see cref="PerPerson"/>.
    /// </summary>
    public string PerItem(char kind, int? choice = null, string fromKey = "@k0")
    {
        var c = choice ?? Choice;
        if (HasItems(kind) && RolledItemDays.Contains(c))
            return $@"(SELECT ip.UserKey, ip.Item, CAST(ip.ActiveDays AS INT) AS ActiveDays, ip.LastActiveKey, ip.Interactions, ip.Prompts, ip.Resources, ip.Web
    FROM dash.UserItemPeriod AS ip WHERE ip.EndKey = @k1 AND ip.Days = {c} AND ip.Kind = '{kind}')";
        if (HasItems(kind))
            return $@"(SELECT ui.UserKey, ui.Item, COUNT(*) AS ActiveDays, MAX(ui.DateKey) AS LastActiveKey, SUM(ui.Interactions) AS Interactions,
        SUM(ui.Prompts) AS Prompts, SUM(ui.Resources) AS Resources, SUM(ui.Web) AS Web
    FROM dash.UserItemDay AS ui WHERE ui.Kind = '{kind}' AND ui.DateKey BETWEEN {fromKey} AND @k1 GROUP BY ui.UserKey, ui.Item)";
        var (from, item, only) = FactItem(kind);
        return $@"(SELECT f.UserKey, {item} AS Item, COUNT(DISTINCT f.DateKey) AS ActiveDays, MAX(f.DateKey) AS LastActiveKey, COUNT(*) AS Interactions,
        ISNULL(SUM(f.PromptCount), 0) AS Prompts, ISNULL(SUM(f.AccessedResourceCount), 0) AS Resources, ISNULL(SUM(CAST(f.UsedWebSearch AS INT)), 0) AS Web
    FROM {from} WHERE f.DateKey BETWEEN {fromKey} AND @k1{only} GROUP BY f.UserKey, {item})";
    }

    /// <summary>
    /// Each person's use of each app or agent at each trend point from <paramref name="from"/>, one row per point, person
    /// and item: Bucket, UserKey, Item, Interactions. Counted, a point's rows are its people. A day is dash.UserItemDay's
    /// row, a whole week dash.UserItemWeek's, and a week the range cuts is summed from its days.
    /// </summary>
    public string PerItemPoint(char kind, string from = "@k0")
    {
        if (!HasItems(kind))
        {
            var (fromSql, item, only) = FactItem(kind);
            return $@"(SELECT {BucketSql("d")} AS Bucket, f.UserKey, {item} AS Item, COUNT(*) AS Interactions
    FROM {fromSql} JOIN bi.DimDate AS d ON d.DateKey = f.DateKey
    WHERE f.DateKey BETWEEN {from} AND @k1{only} GROUP BY {BucketSql("d")}, f.UserKey, {item})";
        }
        if (Daily)
            return $@"(SELECT d.[Date] AS Bucket, ui.UserKey, ui.Item, ui.Interactions
    FROM dash.UserItemDay AS ui JOIN bi.DimDate AS d ON d.DateKey = ui.DateKey WHERE ui.Kind = '{kind}' AND ui.DateKey BETWEEN {from} AND @k1)";
        var cut = $"(SELECT w.WeekEnding FROM bi.DimDate AS w WHERE w.DateKey BETWEEN {from} AND @k1 GROUP BY w.WeekEnding HAVING COUNT(*) < 7)";
        return $@"(SELECT uw.WeekEnding AS Bucket, uw.UserKey, uw.Item, uw.Interactions
    FROM dash.UserItemWeek AS uw
    WHERE uw.Kind = '{kind}' AND uw.WeekEnding BETWEEN DATEADD(DAY, 6, DATEFROMPARTS({from} / 10000, {from} / 100 % 100, {from} % 100)) AND DATEFROMPARTS(@k1 / 10000, @k1 / 100 % 100, @k1 % 100)
      AND uw.WeekEnding NOT IN {cut}
    UNION ALL
    SELECT d.WeekEnding, ui.UserKey, ui.Item, SUM(ui.Interactions)
    FROM dash.UserItemDay AS ui JOIN bi.DimDate AS d ON d.DateKey = ui.DateKey
    WHERE ui.Kind = '{kind}' AND ui.DateKey BETWEEN {from} AND @k1 AND d.WeekEnding IN {cut}
    GROUP BY d.WeekEnding, ui.UserKey, ui.Item)";
    }

    /// <summary>
    /// Whether dash.SitePerson answers for this context: it holds each person's last day at each site and their last
    /// before the data's last day, so it answers a period ending on either of the two days the totals end on, which the
    /// item periods being built for this context's last day shows it is.
    /// </summary>
    public bool SitePeople => RolledItemDays.Count > 0;

    /// <summary>The period choices dash.CreditUsePeriod holds ending on this context's last day; empty until a refresh builds it.</summary>
    public IReadOnlySet<int> RolledCreditDays { get; init; } = new HashSet<int>();

    /// <summary>Whether dash.CreditUseDay has been built.</summary>
    public bool CreditDays { get; init; }

    /// <summary>
    /// What the Copilot Credit estimate counts, per person and agent over the period (the "(none)" agent too), as a
    /// derived table: UserKey, AgentKey, I (interactions), A (with an agent), B (billed), BW (billed, reading work data),
    /// BA (billed, calling another agent), R (resources read), M (messages), Lic (1 when any was made while licensed) and
    /// AnyBilled. Read from dash.CreditUsePeriod when the refresh built that period, else summed from
    /// dash.CreditUseDay, or before either from bi.FactInteraction with the same rules. Not scoped: join the scope to it.
    /// </summary>
    public string PerCredit(int? choice = null, string fromKey = "@k0")
    {
        var c = choice ?? Choice;
        if (RolledCreditDays.Contains(c))
            return $"(SELECT cp.UserKey, cp.AgentKey, cp.I, cp.A, cp.B, cp.BW, cp.BA, cp.R, cp.M, CAST(cp.Lic AS INT) AS Lic, CAST(cp.AnyBilled AS INT) AS AnyBilled FROM dash.CreditUsePeriod AS cp WHERE cp.EndKey = @k1 AND cp.Days = {c})";
        if (CreditDays)
            return $@"(SELECT cu.UserKey, cu.AgentKey, SUM(CAST(cu.I AS BIGINT)) AS I, SUM(CAST(cu.A AS BIGINT)) AS A, SUM(CAST(cu.B AS BIGINT)) AS B,
        SUM(CAST(cu.BW AS BIGINT)) AS BW, SUM(CAST(cu.BA AS BIGINT)) AS BA, SUM(cu.R) AS R, SUM(cu.M) AS M, MAX(CAST(cu.Lic AS INT)) AS Lic, MAX(CAST(cu.AnyBilled AS INT)) AS AnyBilled
    FROM dash.CreditUseDay AS cu WHERE cu.DateKey BETWEEN {fromKey} AND @k1 GROUP BY cu.UserKey, cu.AgentKey)";
        /* The rules as dash.usp_RefreshDashboard applies them. Billed: an agent used by someone not licensed at the time
           (status 2; an unknown status is not taken for unlicensed), or in a Copilot Studio channel. */
        return $@"(SELECT fi.UserKey, fi.AgentKey, COUNT_BIG(*) AS I, SUM(CAST(x.IsAgent AS BIGINT)) AS A, SUM(CAST(x.Billed AS BIGINT)) AS B,
        SUM(CAST(x.Billed * x.Work AS BIGINT)) AS BW, SUM(CAST(x.Billed * x.Calls AS BIGINT)) AS BA,
        SUM(CAST(ISNULL(fi.AccessedResourceCount, 0) AS BIGINT)) AS R, SUM(CAST(ISNULL(fi.MessageCount, 1) AS BIGINT)) AS M,
        MAX(x.Lic) AS Lic, MAX(x.Billed) AS AnyBilled
    FROM bi.FactInteraction AS fi
    JOIN bi.DimAgent AS ag ON ag.AgentKey = fi.AgentKey
    JOIN bi.DimApp AS ap ON ap.AppKey = fi.AppKey
    CROSS APPLY (SELECT CASE WHEN ag.AgentSource = N'(no agent)' THEN 0 ELSE 1 END AS IsAgent,
                        CASE WHEN fi.LicenseStatusKey = 1 THEN 1 ELSE 0 END AS Lic,
                        CASE WHEN ag.AgentSource <> N'(no agent)' AND (fi.LicenseStatusKey = 2 OR ap.App = N'Copilot Studio') THEN 1 ELSE 0 END AS Billed,
                        CASE WHEN fi.AccessedResourceCount > 0 OR fi.UseKind IN (1, 3) THEN 1 ELSE 0 END AS Work,
                        CAST(ISNULL(fi.CalledAnotherAgent, 0) AS INT) AS Calls) AS x
    WHERE fi.DateKey BETWEEN {fromKey} AND @k1
    GROUP BY fi.UserKey, fi.AgentKey)";
    }

    /// <summary>SQL: the dash.SitePerson row under <paramref name="alias"/> reached its site between @k0 and @k1 (see <see cref="SitePeople"/>).</summary>
    public static string Reached(string alias) => $"CASE WHEN @k1 >= {alias}.AsOfKey THEN {alias}.LastKey ELSE {alias}.PrevKey END >= @k0";

    /// <summary>A join restricting a fact to the scope, on the given UserKey expression; empty for the whole organization.</summary>
    public string Scope(string userKeyExpr) =>
        Root is null ? "" : $"JOIN dash.fnScopeUsers(@root) AS sc ON sc.UserKey = {userKeyExpr}";

    /// <summary>
    /// People counts are of accounts enabled today (3.1.3). A person whose account has since been disabled, or who has
    /// left and is gone from the directory (no AccountEnabled at all), is not counted among the people, the active, the
    /// licensed or the adopted, and is not ranked; what they did still counts in every total of interactions, prompts
    /// and sessions. This is the join that keeps only them, on the given UserKey expression: an inner join to filter
    /// people (rankings, people-only queries), or with <paramref name="left"/> a left join whose alias is tested by
    /// <see cref="IsEnabled"/> inside the people counts of a query that also sums use.
    /// </summary>
    public static string EnabledJoin(string userKeyExpr, string alias = "en", bool left = false) =>
        $"{(left ? "LEFT " : "")}JOIN bi.DimUser AS {alias} ON {alias}.UserKey = {userKeyExpr} AND {alias}.AccountEnabled = 1";

    /// <summary>The test of a left <see cref="EnabledJoin"/>: the person's account is enabled today.</summary>
    public static string IsEnabled(string alias = "en") => $"{alias}.UserKey IS NOT NULL";

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
        var q = new List<string> { $"d={days ?? Choice}" };
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
    /// SQL for the size of a manager's part of the organization, against the minimum group size: the enabled
    /// people in it, the manager among them (dash.OrgClosure has a depth-0 row for each person). It was dash.Person's
    /// PeopleBelow + 1, which counts disabled accounts too, so a part of three people and five leavers was shown on its
    /// own where every other minimum counts only the people still here. A seek on the closure's key, which leads with
    /// the ancestor. Whether each person's account is enabled is on the closure itself (IX_DashOrgClosure_Part), written
    /// by the refresh with the pair: the list of parts on every page took 1 to 3 seconds at 210,000 people looking each
    /// person up in dash.Person, and 0.1 s read from the closure. A fallback to the lookup for rows without it made the
    /// plan slower than the lookup alone.
    /// </summary>
    public static string EnabledInPartSql(string upnExpr) =>
        $"(SELECT COUNT(*) FROM dash.OrgClosure AS ec WHERE ec.AncestorUpn = {upnExpr} AND ec.DescendantEnabled = 1)";

    /// <param name="forPage">
    /// A page a person is looking at: a part of the organization that is not in the directory falls back to the whole
    /// organization, with a note. The data API and the digests keep to what was asked for, and so to nothing:
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
        var minGroup = await settings.IntAsync("MinGroupSize", 10);
        var inactive = await settings.IntAsync("InactiveDays", 28);
        /* Set by an admin, or else found in the org chart by the refresh. */
        var top = await settings.StringAsync("OrgTopUpn") ?? await settings.StringAsync("OrgTopUpnDetected");

        /* The last complete day with data (today is still arriving), today being the day in the zone the data's days are
           cut in. It was SQL Server's own date: with the server on UTC and the days cut in Eastern Time, from
           8 p.m. Eastern the day still loading counted as complete, and every period ended on it. */
        var today = await settings.TodayKeyAsync();
        /* With it, the last day with any record, today's included: a daily trend stops two days before that one. */
        var (completeKey, lastKey) = (await db.QueryAsync<(int? Complete, int? Last)>(@"
SELECT (SELECT MAX(DateKey) FROM dash.UserDay WHERE DateKey < @today), (SELECT MAX(DateKey) FROM dash.UserDay);", new { today })).Single();
        var end = (completeKey ?? lastKey) is int k ? new DateTime(k / 10000, k / 100 % 100, k % 100) : ReportContext.Day(today).AddDays(-1);
        DateTime? lastRecord = lastKey is int l ? ReportContext.Day(l) : null;
        /* All data (3.1.3): from the first day with data to the end, so its days are counted, not taken as chosen. */
        var choice = days;
        if (days == ReportContext.AllData)
        {
            var firstKey = await db.ScalarAsync<int?>("SELECT MIN(DateKey) FROM dash.UserDay;");
            var first = firstKey is int f ? ReportContext.Day(f) : end;
            days = Math.Max(1, (end - first).Days + 1);
        }
        var start = end.AddDays(-(days - 1));

        /* Whose data: whole organization, or a subtree the viewer may see. */
        string? root;
        var why = OrgRefusal.None;
        if (viewer.OrgWide)
        {
            /* A part of the organization is someone in the directory, for the API and the digests too. They took
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

        /* A part of the organization other than the viewer's own is shown only when it is at least the minimum
           group size, and never in privacy mode, where it is named after its manager. Any sign-in in org= was taken: a
           person with nobody under them made every page, and the API, a report of that one person. A page falls back to
           the viewer's own part with a note; the API and the digests get nobody, and refuse. */
        var privacy = await settings.PrivacyModeAsync();
        /* The size of a part and the parts below a manager are the org chart's, which only the dashboard's refresh
           rewrites: they are kept with the report data under the data stamp. Counted on every request, the enabled
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

        var t = await RollupsAsync(ReportContext.Key(end));

        return new ReportContext
        {
            Viewer = viewer, Days = days, Choice = choice, Start = start, End = end, LastRecord = lastRecord, Root = root, RootName = rootName, OrgAsked = asked, OrgWhy = why,
            PrivacyMode = privacy,
            MinGroupSize = minGroup, InactiveDays = inactive, OrgTopUpn = top, Breadcrumb = crumbs, Children = children,
            Rollups = t.Built, RolledDays = t.Days, RolledItemDays = t.ItemDays, ItemKinds = t.Kinds, CreditDays = t.Credit, RolledCreditDays = t.CreditDays,
        };
    }

    /// <summary>What <see cref="RollupsAsync"/> found: which totals the refresh has built.</summary>
    public sealed record Totals(bool Built, IReadOnlySet<int> Days, IReadOnlySet<int> ItemDays, string Kinds, bool Credit, IReadOnlySet<int> CreditDays)
    {
        public static readonly Totals None = new(false, new HashSet<int>(), new HashSet<int>(), "", false, new HashSet<int>());
    }

    /// <summary>
    /// Whether the refresh has built the totals, and which period choices ending on <paramref name="endKey"/>
    /// dash.UserPeriod, dash.UserItemPeriod and dash.CreditUsePeriod hold: a seek or a short scan each, kept under the data
    /// stamp. A database without them (its refresh has not run since the upgrade) has none, and the pages sum the days as
    /// before.
    /// </summary>
    async Task<Totals> RollupsAsync(int endKey)
    {
        try
        {
            IEnumerable<(int Which, int Days, string? Kinds)> rows;
            using (QueryCache.Use())
                rows = await db.QueryAsync<(int Which, int Days, string? Kinds)>(@"
SELECT 1, p.Days, NULL FROM (VALUES (7), (14), (30), (90), (180), (365), (730), (0)) AS p (Days)
WHERE EXISTS (SELECT 1 FROM dash.UserPeriod AS up WHERE up.EndKey = @endKey AND up.Days = p.Days)
UNION ALL
SELECT 2, p.Days, NULL FROM (VALUES (7), (14), (30), (90), (180), (365), (730), (0)) AS p (Days)
WHERE EXISTS (SELECT 1 FROM dash.UserItemPeriod AS ip WHERE ip.EndKey = @endKey AND ip.Days = p.Days)
UNION ALL
SELECT 3, p.Days, NULL FROM (VALUES (7), (14), (30), (90), (180), (365), (730), (0)) AS p (Days)
WHERE EXISTS (SELECT 1 FROM dash.CreditUsePeriod AS cp WHERE cp.EndKey = @endKey AND cp.Days = p.Days)
UNION ALL
SELECT 4, 0, NULL WHERE EXISTS (SELECT 1 FROM dash.CreditUseDay)
UNION ALL
SELECT 5, 0, (SELECT Value FROM dash.Setting WHERE Name = 'DashItemKinds')
UNION ALL
SELECT 0, 0, NULL WHERE EXISTS (SELECT 1 FROM dash.UserActivity) AND EXISTS (SELECT 1 FROM dash.UserItemDay);", new { endKey });
            var list = rows.ToList();
            HashSet<int> Of(int which) => list.Where(r => r.Which == which).Select(r => r.Days).ToHashSet();
            return new(list.Any(r => r.Which == 0), Of(1), Of(2), list.FirstOrDefault(r => r.Which == 5).Kinds ?? "", list.Any(r => r.Which == 4), Of(3));
        }
        catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 208) { return Totals.None; }
    }
}

/// <summary>Writes dash.AuditLog: every page view, denial, export and admin change.</summary>
/// <remarks>
/// A write that fails is not swallowed. It was logged and the page, the file or the API answer went out all
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

/// <summary>The audit log could not be written: the request is refused rather than served unrecorded.</summary>
public sealed class AuditLogUnavailableException(Exception inner) : Exception("The audit log could not be written.", inner)
{
    /// <summary>What the viewer is told, with the 503.</summary>
    public const string Message503 = "Copilot Insights could not write its audit log, so nothing is shown: every page, export and API call is recorded there before it is served. " +
        "Try again in a minute. If it goes on, the database administrator should check the SQL Server: a full log or disk, a read-only database, " +
        "or the site's account losing its right to write dash.AuditLog.";
}
