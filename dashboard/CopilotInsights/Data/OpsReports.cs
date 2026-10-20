using System.Net;
using System.Text;
using CopilotInsights.Infrastructure;

namespace CopilotInsights.Data;

public sealed class TargetRow
{
    public int TargetId { get; set; }
    public string? RootUpn { get; set; }
    public string? RootName { get; set; }
    public string? RootDepartment { get; set; }
    /// <summary>The people in the target's part of the organization, its manager included; for the minimum group size, not shown.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public int? RootSize { get; set; }
    public string Metric { get; set; } = "";
    public decimal TargetValue { get; set; }
    public DateTime StartDate { get; set; }
    public DateTime DueDate { get; set; }
    public string? Note { get; set; }
    /* Worked out: */
    public double? Baseline { get; set; }
    public double? Current { get; set; }
    public double? Expected { get; set; }
    public string Status { get; set; } = "";
}

public sealed record ValueRow(string Name, long Interactions, double Hours, decimal Value, int People = 0);

/// <summary>
/// A digest that may not be sent as set up (2.4.12): it is skipped until the next time it is due, with the reason as its
/// status. An ArgumentException, so Send now in Admin says "Not changed:" and why.
/// </summary>
public sealed class DigestRefused(string reason) : ArgumentException(reason);

/// <summary>Report figures the pages, the digests and the data API share, for any scope and period.</summary>
public sealed class OpsReports(Db db, Metrics metrics, OpsData ops, ReportContextFactory contexts, Settings settings)
{
    /// <summary>Who the digests and alerts see as: the whole organization, aggregates only.</summary>
    public static Viewer SystemViewer() => new()
    {
        WindowsName = "Copilot Insights (scheduled)", DisplayName = "Copilot Insights", InDirectory = false,
        Roles = new HashSet<string>([Roles.Organization], StringComparer.OrdinalIgnoreCase),
    };

    public Task<ReportContext> ContextAsync(int days, string? root) => contexts.CreateAsync(SystemViewer(), days, root);

    /// <summary>The same context for another window: <paramref name="days"/> days ending on <paramref name="end"/>.</summary>
    public static ReportContext Window(ReportContext c, DateTime end, int days) => new()
    {
        Viewer = c.Viewer, Days = days, Start = end.AddDays(-(days - 1)), End = end, LastRecord = c.LastRecord, Root = c.Root, RootName = c.RootName,
        MinGroupSize = c.MinGroupSize, InactiveDays = c.InactiveDays, OrgTopUpn = c.OrgTopUpn, PrivacyMode = c.PrivacyMode,
    };

    /// <summary>
    /// The whole organization over the last 30 days to <paramref name="c"/>'s end, for the tenant's own figures (2.4.12): the
    /// license pool and the budget are the tenant's, and are shown only to a viewer allowed them (the whole-organization view,
    /// or the Licensing role). Those pages asked for "the whole organization" from the context factory, which gives a viewer
    /// without the whole-organization view their own part instead, so a director with the Licensing role saw their own 36
    /// people's use set against the tenant's 1,269 assigned seats.
    /// </summary>
    public static ReportContext Tenant(ReportContext c) => new()
    {
        Viewer = c.Viewer, Days = 30, Start = c.End.AddDays(-29), End = c.End, LastRecord = c.LastRecord, Root = null,
        MinGroupSize = c.MinGroupSize, InactiveDays = c.InactiveDays, OrgTopUpn = c.OrgTopUpn, PrivacyMode = c.PrivacyMode,
    };

    /* ---- The days the data covers ------------------------------------------------------------ */

    /// <summary>The first day with data (dash.UserDay), or null when there is none yet.</summary>
    public static async Task<DateTime?> FirstDayAsync(Db db) =>
        await db.ScalarAsync<int?>("SELECT MIN(DateKey) FROM dash.UserDay;") is int k ? new DateTime(k / 10000, k / 100 % 100, k % 100) : null;

    /// <summary>
    /// Whether the data covers every day of <paramref name="c"/>'s period (2.4.12). The period before is set against the one
    /// shown only when it is: a 180-day period on 182 days of data compared 1,270 people with the 31 of the two days before
    /// it, "+3997%", on the Scorecard, the Board pack and the digests.
    /// </summary>
    public static bool Covers(ReportContext c, DateTime? firstDay) => firstDay is DateTime f && c.Start >= f;

    /// <summary>
    /// The days of <paramref name="c"/>'s period that the data covers: from its start, or from the first day with data when that
    /// is later, to its end (2.4.12). What a figure "a month" is scaled by: a 365-day period over 182 days of data halved
    /// everyone's credits a month.
    /// </summary>
    public static int DaysCovered(ReportContext c, DateTime? firstDay) =>
        Math.Max(1, (c.End - (firstDay is DateTime f && f > c.Start ? f : c.Start)).Days + 1);

    /// <summary>What a figure says in place of its change when the data does not cover all of the period before.</summary>
    public static string NoFullPeriodBefore(DateTime? firstDay) =>
        firstDay is null ? "no earlier data" : $"no full period before: the data starts on {firstDay:d MMM yyyy}";

    /// <summary>Why, in a sentence under a table whose figures for the period before are left out.</summary>
    public static string NotCovered(DateTime? firstDay) =>
        firstDay is null ? "there is no data yet" : $"the data starts on {firstDay:d MMM yyyy}, inside the period before";

    /* ---- Targets --------------------------------------------------------------------------- */

    static double? MetricOf(string metric, Totals t) => metric switch
    {
        "Adoption" => t.LicensedNow > 0 ? 100.0 * t.ActiveLicensed / t.LicensedNow : null,
        "ActiveShare" => t.People > 0 ? 100.0 * t.Active / t.People : null,
        _ => t.Active,
    };

    /// <summary>
    /// The targets a report on <paramref name="root"/> may show (2.4.12): with no root (the whole organization, which only a
    /// viewer of the whole organization has) every target; with one, those for a part of the organization inside it, whose
    /// managers are in <paramref name="under"/>, and not the whole organization's. Only the Targets page filtered them: the
    /// Board pack, the data API and a manager's Adoption digest showed every target, the whole organization's and other
    /// executives' parts, to people who may see neither.
    /// </summary>
    public static List<TargetRow> Visible(IEnumerable<TargetRow> all, string? root, bool wholeOrganization, IReadOnlySet<string> under) =>
        root is null ? (wholeOrganization ? all.ToList() : [])
                     : all.Where(t => t.RootUpn is not null && under.Contains(t.RootUpn)).ToList();

    /// <summary>
    /// A target for a part of the organization smaller than the minimum group size (2.4.12) shows no figures, unless it is the
    /// viewer's own part: a "People active" target on a team of three was that team's count of active people, on every page
    /// and in every email that listed it.
    /// </summary>
    public static bool TooSmall(TargetRow t, int minGroupSize, string? viewerUpn) =>
        t.RootUpn is not null && t.RootSize is int n && n < minGroupSize && !string.Equals(t.RootUpn, viewerUpn, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The targets for the part of the organization <paramref name="now"/> reports on (see <see cref="Visible"/>), each with its
    /// baseline (the 30 days before it started), where it stands now, and where it should be by now.
    /// In privacy mode a target for a manager's part of the organization is shown by their department, not their name (2.4.12).
    /// </summary>
    public async Task<List<TargetRow>> TargetsAsync(ReportContext now)
    {
        var all = await db.QueryAsync<TargetRow>(@"
SELECT t.TargetId, t.RootUpn, p.DisplayName AS RootName, p.Department AS RootDepartment, p.PeopleBelow + 1 AS RootSize, t.Metric, t.TargetValue, t.StartDate, t.DueDate, t.Note
FROM dash.Target AS t LEFT JOIN dash.Person AS p ON p.Upn = t.RootUpn ORDER BY t.DueDate, t.TargetId;");
        /* Only the targets' own managers are looked up under the root: not the whole of a large part of the organization. */
        var under = now.Root is null || all.All(t => t.RootUpn is null) ? new HashSet<string>() :
            (await db.QueryAsync<string>(@"
SELECT oc.DescendantUpn FROM dash.OrgClosure AS oc
WHERE oc.AncestorUpn = @root AND oc.DescendantUpn IN (SELECT t.RootUpn FROM dash.Target AS t WHERE t.RootUpn IS NOT NULL);", new { root = now.Root }))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var rows = Visible(all, now.Root, now.Viewer.OrgWide, under);
        foreach (var r in rows)
        {
            if (TooSmall(r, now.MinGroupSize, now.Viewer.Upn))
                r.Status = $"Fewer than {now.MinGroupSize} people: not shown";
            else
                await ProgressAsync(r, now);
            if (now.PrivacyMode && r.RootUpn is not null && !string.Equals(r.RootUpn, now.Viewer.Upn, StringComparison.OrdinalIgnoreCase))
                (r.RootName, r.RootUpn) = (string.IsNullOrWhiteSpace(r.RootDepartment) ? "A part of the organization" : $"A part of {r.RootDepartment}", null);
        }
        return rows;
    }

    /// <summary>A target's baseline, where it stands now (the last 30 days), where it should be by now, and its status.</summary>
    async Task ProgressAsync(TargetRow r, ReportContext now)
    {
        var scoped = new ReportContext
        {
            Viewer = now.Viewer, Days = 30, Start = now.End.AddDays(-29), End = now.End, LastRecord = now.LastRecord, Root = r.RootUpn,
            MinGroupSize = now.MinGroupSize, InactiveDays = now.InactiveDays, OrgTopUpn = now.OrgTopUpn, PrivacyMode = now.PrivacyMode,
        };
        r.Current = MetricOf(r.Metric, await metrics.TotalsAsync(scoped));
        var baseEnd = r.StartDate.Date.AddDays(-1);
        r.Baseline = baseEnd >= now.End.AddDays(-400) && baseEnd <= now.End ? MetricOf(r.Metric, await metrics.TotalsAsync(Window(scoped, baseEnd, 30))) : null;
        var span = (r.DueDate - r.StartDate).TotalDays;
        var gone = Math.Clamp((now.End - r.StartDate).TotalDays / span, 0, 1);
        var from = r.Baseline ?? 0;
        r.Expected = from + ((double)r.TargetValue - from) * gone;
        r.Status = r.Current is null ? "No data"
            : r.Current >= (double)r.TargetValue ? "Met"
            : now.End > r.DueDate ? "Missed"
            : r.Current >= r.Expected - 0.5 ? "On track" : "Behind";
    }

    /* ---- Value ------------------------------------------------------------------------------ */

    /// <summary>Hours saved and their value: interactions by app times the minutes each saves (dash.TimeSaving, else the default).</summary>
    public async Task<(List<ValueRow> ByApp, List<ValueRow> ByDepartment, decimal HourlyCost, decimal DefaultMinutes)> ValueAsync(ReportContext c)
    {
        var hourly = decimal.TryParse(await settings.StringAsync("HourlyCost"), System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var h) ? h : 50m;
        var dflt = decimal.TryParse(await settings.StringAsync("MinutesSavedDefault"), System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var m) ? m : 3m;
        var rows = await db.QueryAsync<(string App, string Dept, long N, decimal Minutes)>($@"
SELECT a.App, ISNULL(NULLIF(u.Department, N''), N'(no department)'), COUNT_BIG(*), ISNULL(ts.Minutes, @dflt)
FROM bi.FactInteraction AS f {c.Scope("f.UserKey")}
JOIN bi.DimApp AS a ON a.AppKey = f.AppKey
JOIN bi.DimUser AS u ON u.UserKey = f.UserKey
LEFT JOIN dash.TimeSaving AS ts ON ts.App = a.App
WHERE f.DateKey BETWEEN @k0 AND @k1
GROUP BY a.App, ISNULL(NULLIF(u.Department, N''), N'(no department)'), ts.Minutes;", c.Args(new { dflt }));
        ValueRow Roll(IGrouping<string, (string App, string Dept, long N, decimal Minutes)> g)
        {
            var hours = g.Sum(x => x.N * (double)x.Minutes) / 60;
            return new(g.Key, g.Sum(x => x.N), hours, (decimal)hours * hourly);
        }
        /* People per department, so a group too small to hide one person can be folded. */
        var people = (await db.QueryAsync<(string Dept, int People)>($@"
SELECT ISNULL(NULLIF(u.Department, N''), N'(no department)'), COUNT(DISTINCT f.UserKey)
FROM bi.FactInteraction AS f {c.Scope("f.UserKey")} JOIN bi.DimUser AS u ON u.UserKey = f.UserKey
WHERE f.DateKey BETWEEN @k0 AND @k1
GROUP BY ISNULL(NULLIF(u.Department, N''), N'(no department)');", c.Args())).ToDictionary(x => x.Dept, x => x.People);
        return (rows.GroupBy(r => r.App).Select(Roll).OrderByDescending(r => r.Hours).ToList(),
                rows.GroupBy(r => r.Dept).Select(g => Roll(g) with { People = people.GetValueOrDefault(g.Key) }).OrderByDescending(r => r.Hours).ToList(), hourly, dflt);
    }

    /// <summary>Departments too small to show on their own, as one row (see <see cref="SmallGroups"/>).</summary>
    public static ValueRow CombineValue(IReadOnlyList<ValueRow> rows) =>
        new("Smaller groups", rows.Sum(r => r.Interactions), rows.Sum(r => r.Hours), rows.Sum(r => r.Value), rows.Sum(r => r.People));

    /// <summary>
    /// The licenses held in the period, a person a day (bi.FactLicensedDay), in scope: what the period's license cost is priced
    /// on (2.4.12, see Licensing.Price). It was today's licenses times every day of the period, so a rollout during it, or a
    /// period reaching back before the licenses or the data, was charged in full against a value that was not.
    /// </summary>
    public async Task<long> LicenseDaysAsync(ReportContext c) =>
        await db.ScalarAsync<long>($"SELECT COUNT_BIG(*) FROM bi.FactLicensedDay AS ld {c.Scope("ld.UserKey")} WHERE ld.DateKey BETWEEN @k0 AND @k1;", c.Args());

    /* ---- License recommendations, counted ----------------------------------------------------- */

    public async Task<Dictionary<string, int>> ActionCountsAsync(ReportContext c)
    {
        var k30 = ReportContext.Key(c.End.AddDays(-29));
        var result = new Dictionary<string, int>();
        foreach (var (kind, def) in Pages.Forecasting.ActionsModel.Kinds)
            result[kind] = await db.ScalarAsync<int>($@"
WITH {await Pages.Forecasting.ActionsModel.ActivitySqlAsync(db, k30, c.EndKey)},
lic AS (SELECT UserKey FROM bi.FactLicensedDay WHERE DateKey = @k1)
SELECT COUNT(*) FROM bi.DimUser AS du {c.Scope("du.UserKey")}
LEFT JOIN a ON a.UserKey = du.UserKey LEFT JOIN lic ON lic.UserKey = du.UserKey
WHERE {def.Where} AND ISNULL(du.AccountEnabled, 1) = 1;", new { k30, k1 = c.EndKey, d1 = c.End, root = c.Root });
        return result;
    }

    /* ---- Digests ------------------------------------------------------------------------------ */

    public static readonly Dictionary<string, string> DigestKinds = new()
    {
        ["Leadership"] = "use, adoption and each part of the organization, against the period before",
        ["Adoption"] = "the funnel, targets, and days that stood out",
        ["Licensing"] = "the pool, recommendations, and licenses on disabled accounts",
        ["Security"] = "security and compliance signals, against the period before",
        ["Value"] = "hours saved and what they are worth",
    };

    /// <summary>
    /// Units ranked by adoption, as the Leadership Overview's chart ranks them (2.4.12, see Chart.Adoption): only units with
    /// at least the minimum group size of license holders, highest first; the rest one row at the end, which reaches the
    /// minimum too where it can. The digest applied the minimum to each unit's people, so a team of twelve with one license
    /// holder went out by email as "12 | 1 | 100%", whether that one person used Copilot.
    /// </summary>
    public static List<UnitRow> AdoptionRanking(IEnumerable<UnitRow> units, int min)
    {
        var (ranked, few) = SmallGroups.Split(units.Where(u => u.Licensed > 0), u => u.Licensed, min);
        var list = ranked.OrderByDescending(u => (double)u.ActiveLicensed / u.Licensed).ThenByDescending(u => u.Licensed).ToList();
        if (few.Count > 0) list.Add(Data.Metrics.Combine(few, few.Count == 1 ? "A smaller group" : $"{few.Count:#,0} smaller groups, combined"));
        return list;
    }

    /// <summary>A digest's subject, HTML and plain text. Aggregates only: no named person appears in email.</summary>
    public async Task<(string Subject, string Html, string Text)> DigestAsync(string report, int days, string? root)
    {
        var c = await ContextAsync(days, root);
        /* A digest for a manager's part of the organization is named after them, and is that part alone (2.4.12). */
        if (c.OrgWhy == OrgRefusal.Privacy)
            throw new DigestRefused("privacy mode is on, and this digest is for a manager's part of the organization, which is named after them");
        if (c.OrgWhy == OrgRefusal.TooSmall)
            throw new DigestRefused($"the part of the organization it is for has fewer than {c.MinGroupSize} people, the minimum group size");
        /* Nor for someone no longer in the directory (2.4.12): their scope is nobody, and it went out as "(nobody)" with
           zeros, reported as sent. */
        if (c.OrgWhy == OrgRefusal.NotFound)
            throw new DigestRefused("the person whose part of the organization it is for is no longer in the organization directory");
        var prev = Window(c, c.Start.AddDays(-1), c.Days);
        /* Set against the period before only where the data covers all of it (2.4.12, see Covers). */
        var first = await FirstDayAsync(db);
        var full = Covers(prev, first);
        var url = await settings.StringAsync("DashboardUrl");
        var e = new Email($"{report}: {c.ScopeLabel}, {c.PeriodLabel}");
        string Link(string path) => url is null ? "" : url.TrimEnd('/') + c.Url(path);
        void NotCompared()
        {
            if (!full) e.Paragraph($"These figures are not set against the {c.Days} days before: {NotCovered(first)}.");
        }

        switch (report)
        {
            case "Leadership":
            {
                var (t, p) = (await metrics.TotalsAsync(c), full ? await metrics.TotalsAsync(prev) : null);
                NotCompared();
                e.Kpi("People using Copilot", t.Active, p?.Active);
                e.Kpi("Licensed", t.LicensedNow, p?.LicensedNow);
                e.KpiPct("Adoption", Pct(t.ActiveLicensed, t.LicensedNow), p is null ? null : Pct(p.ActiveLicensed, p.LicensedNow));
                e.Kpi("Interactions", t.Interactions, p?.Interactions);
                var units = AdoptionRanking(SmallGroups.Fold(await metrics.UnitsAsync(c), u => u.People, r => Data.Metrics.Combine(r, "Smaller groups"), c.MinGroupSize, out _), c.MinGroupSize);
                e.Table("Adoption by unit", ["Unit", "People", "Licensed", "Adoption"],
                    units.Select(u => new object?[] { u.Name, u.People, u.Licensed, u.Licensed >= c.MinGroupSize ? PctText(u.ActiveLicensed, u.Licensed) : "—" }));
                e.Link(Link("/leadership"), "Open the leadership overview");
                break;
            }
            case "Adoption":
            {
                var (t, p) = (await metrics.TotalsAsync(c), full ? await metrics.TotalsAsync(prev) : null);
                NotCompared();
                e.Kpi("People using Copilot", t.Active, p?.Active);
                e.Kpi("Active in the last 7 days", t.ActiveLast7, p?.ActiveLast7);
                e.KpiPct("Adoption", Pct(t.ActiveLicensed, t.LicensedNow), p is null ? null : Pct(p.ActiveLicensed, p.LicensedNow));
                /* The targets for the digest's own part of the organization (2.4.12, see Visible). */
                var targets = await TargetsAsync(c);
                e.Table("Targets", ["Target", "For", "Now", "Should be by now", "Due", "Status"],
                    targets.Select(r => new object?[] { $"{Metrics(r.Metric)} {r.TargetValue:0.#}{(r.Metric == "ActivePeople" ? "" : "%")}", r.RootName ?? r.RootUpn ?? "Whole organization",
                        Fmt1(r.Current), Fmt1(r.Expected), r.DueDate.ToString("yyyy-MM-dd"), r.Status }));
                var series = await ops.DepartmentDaysAsync(c.End.AddDays(-34), c.End, c.MinGroupSize, c.Root);
                var odd = Anomalies.Find(series, c.End.AddDays(-6), await settings.IntAsync("AnomalyDropPercent", 50), await settings.IntAsync("AnomalySpikePercent", 200));
                e.Table("Days that stood out (last 7 days)", ["Department", "Day", "Interactions", "Usual", "Change"],
                    odd.Take(15).Select(a => new object?[] { a.Unit, a.Day.ToString("ddd d MMM"), a.Value, Math.Round(a.Baseline), $"{a.Change:+0%;-0%}" }));
                e.Link(Link("/adoption"), "Open adoption");
                break;
            }
            case "Licensing":
            {
                /* The pool is the whole tenant's: in the whole organization's digest only (2.4.12). A manager's digest carried it
                   to people never checked for the whole-organization view or the Licensing role, which the pool page asks for. */
                if (c.Root is null)
                {
                    var pool = (await ops.PoolAsync()).Where(r => r.IsCopilot).ToList();
                    var names = await SkuNames.LoadAsync(db);   // 2.7.1: product names, not part numbers
                    e.Table("Copilot license pool", ["License", "Bought", "Assigned", "Available"],
                        pool.Select(r => new object?[] { names.Name(r.SkuPartNumber), r.Bought, r.Consumed, r.Available }));
                }
                var counts = await ActionCountsAsync(c);
                var price = decimal.TryParse(await settings.StringAsync("LicenseMonthlyCost"), System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var pr) ? pr : 30m;
                var cur = await settings.StringAsync("LicenseCurrency") ?? "USD";
                e.Kpi("Upgrade candidates", counts.GetValueOrDefault("upgrade"), null, $"{counts.GetValueOrDefault("upgrade") * price:#,0} {cur} a month");
                e.Kpi("Train, then review", counts.GetValueOrDefault("train"), null);
                e.Kpi("Unused licenses to revoke", counts.GetValueOrDefault("revoke"), null, $"saves {counts.GetValueOrDefault("revoke") * price:#,0} {cur} a month");
                var leavers = await ops.LeaversAsync(c.Root);
                e.Kpi("Licenses on disabled or departed accounts", leavers.Count, null, leavers.Count > 0 ? $"{leavers.Count * price:#,0} {cur} a month" : null);
                e.Link(Link("/forecasting/optimize"), "Open license optimization");
                break;
            }
            case "Security":
            {
                var tr = await metrics.TrendAsync(c);
                var tp = full ? await metrics.TrendAsync(prev) : null;
                NotCompared();
                e.Kpi("Jailbreak attempts", tr.Sum(x => x.Jailbreak), tp?.Sum(x => x.Jailbreak));
                e.Kpi("Prompt injections", tr.Sum(x => x.Injection), tp?.Sum(x => x.Injection));
                e.Kpi("Blocked resources", tr.Sum(x => x.Blocked), tp?.Sum(x => x.Blocked));
                e.Kpi("Labeled content", tr.Sum(x => x.Labeled), tp?.Sum(x => x.Labeled));
                var units = SmallGroups.Shown(await metrics.UnitsAsync(c), u => u.People, c.MinGroupSize).OrderByDescending(u => u.Flags).Take(10).ToList();
                e.Table("Units with the most signals", ["Unit", "People", "Signals", "Labeled interactions"],
                    units.Select(u => new object?[] { u.Name, u.People, u.Flags, u.Labeled }));
                e.Link(Link("/security"), "Open security signals");
                break;
            }
            case "Value":
            {
                var v = await ValueAsync(c);
                var cur = await settings.StringAsync("LicenseCurrency") ?? "USD";
                e.Kpi("Hours saved (estimate)", (long)v.ByApp.Sum(x => x.Hours), null, $"at {v.DefaultMinutes:0.#} minutes an interaction unless set per app");
                e.Kpi($"Worth ({cur})", (long)v.ByApp.Sum(x => x.Value), null, $"at {v.HourlyCost:#,0.##} {cur} an hour");
                e.Table("By department", ["Department", "Interactions", "Hours", $"Worth ({cur})"],
                    SmallGroups.Shown(v.ByDepartment, r => r.People, c.MinGroupSize).Take(15).Select(r => new object?[] { r.Name, r.Interactions, Math.Round(r.Hours), Math.Round(r.Value) }));
                e.Link(Link("/leadership/value"), "Open the value report");
                break;
            }
            default:
                throw new ArgumentException($"no digest called {report}.");
        }
        e.Footer(url is null ? "Set the dashboard's address in Admin > Email to include links." : null);
        return (e.Subject, e.Html(), e.Text());
    }

    static string Metrics(string m) => m switch { "Adoption" => "Adoption", "ActiveShare" => "Share of people active", _ => "People active" };
    static double? Pct(int a, int b) => b > 0 ? 100.0 * a / b : null;
    static string PctText(int a, int b) => b > 0 ? $"{100.0 * a / b:0.#}%" : "—";
    static string Fmt1(double? v) => v is null ? "—" : $"{v:#,0.#}";
}

/// <summary>An HTML email with inline styles (mail clients ignore style sheets), and its plain-text twin.</summary>
public sealed class Email(string subject)
{
    public string Subject { get; } = subject;
    readonly StringBuilder _h = new(), _t = new();
    readonly List<string> _kpis = [];

    static string E(string? s) => WebUtility.HtmlEncode(s ?? "");
    static string Num(double v) => v.ToString("#,0.#");

    public void Kpi(string label, double value, double? previous, string? note = null) =>
        AddKpi(label, Num(value), previous is null ? note : Change(value, previous.Value, false) + (note is null ? "" : "; " + note));

    public void KpiPct(string label, double? value, double? previous) =>
        AddKpi(label, value is null ? "—" : $"{value:0.#}%", value is null || previous is null ? null : Change(value.Value, previous.Value, true));

    static string Change(double now, double before, bool points) =>
        points ? $"{now - before:+0.#;-0.#;0} points on the period before"
               : before == 0 ? "none in the period before" : $"{now / before - 1:+0%;-0%;0%} on the period before";

    void AddKpi(string label, string value, string? note)
    {
        _kpis.Add($"<td style=\"padding:10px 14px;border:1px solid #d7dce3;border-radius:6px;vertical-align:top\"><div style=\"font-size:12px;color:#5b6573\">{E(label)}</div>" +
                  $"<div style=\"font-size:22px;font-weight:600;color:#1b2430\">{E(value)}</div>{(note is null ? "" : $"<div style=\"font-size:12px;color:#5b6573\">{E(note)}</div>")}</td>");
        _t.AppendLine($"{label}: {value}{(note is null ? "" : $" ({note})")}");
    }

    void FlushKpis()
    {
        if (_kpis.Count == 0) return;
        _h.Append("<table role=\"presentation\" style=\"border-collapse:separate;border-spacing:8px\"><tr>").Append(string.Concat(_kpis)).Append("</tr></table>");
        _kpis.Clear();
    }

    public void Table(string title, string[] headers, IEnumerable<object?[]> rows)
    {
        FlushKpis();
        var list = rows.ToList();
        _h.Append($"<h3 style=\"font-size:15px;color:#1b2430;margin:18px 0 6px\">{E(title)}</h3>");
        _t.AppendLine().AppendLine(title);
        if (list.Count == 0) { _h.Append("<p style=\"color:#5b6573\">Nothing to show.</p>"); _t.AppendLine("  Nothing to show."); return; }
        _h.Append("<table style=\"border-collapse:collapse;font-size:13px\"><tr>");
        foreach (var hd in headers) _h.Append($"<th style=\"text-align:left;padding:4px 10px;border-bottom:1px solid #d7dce3;color:#5b6573\">{E(hd)}</th>");
        _h.Append("</tr>");
        foreach (var r in list)
        {
            _h.Append("<tr>");
            foreach (var v in r)
            {
                var s = v switch { null => "—", long or int => $"{v:#,0}", double d => Num(d), decimal m => m.ToString("#,0.#"), _ => v.ToString() ?? "" };
                _h.Append($"<td style=\"padding:4px 10px;border-bottom:1px solid #eef1f4\">{E(s)}</td>");
            }
            _h.Append("</tr>");
            _t.AppendLine("  " + string.Join(" | ", r.Select(v => v?.ToString() ?? "—")));
        }
        _h.Append("</table>");
    }

    public void Paragraph(string text)
    {
        FlushKpis();
        _h.Append($"<p style=\"font-size:14px;color:#1b2430;white-space:pre-wrap\">{E(text)}</p>");
        _t.AppendLine().AppendLine(text);
    }

    public void Link(string? href, string text)
    {
        if (string.IsNullOrEmpty(href)) return;
        FlushKpis();
        _h.Append($"<p><a href=\"{E(href)}\" style=\"color:#1a5fb4\">{E(text)}</a></p>");
        _t.AppendLine().AppendLine($"{text}: {href}");
    }

    public void Footer(string? extra, bool aggregates = true)
    {
        FlushKpis();
        var f = "From Copilot Insights." + (aggregates ? " Figures are aggregates; groups under the minimum size are not shown." : "") + (extra is null ? "" : " " + extra);
        _h.Append($"<p style=\"font-size:11px;color:#8a94a3;margin-top:22px\">{E(f)}</p>");
        _t.AppendLine().AppendLine(f);
    }

    public string Html()
    {
        FlushKpis();
        return "<!DOCTYPE html><html><body style=\"font-family:Segoe UI,Arial,sans-serif;margin:0;padding:18px;background:#ffffff\">" +
               $"<h2 style=\"font-size:18px;color:#1b2430;margin:0 0 8px\">{E(Subject)}</h2>{_h}</body></html>";
    }

    public string Text() => Subject + Environment.NewLine + new string('=', Math.Min(Subject.Length, 70)) + Environment.NewLine + _t;
}
