using System.Net;
using System.Text;
using CopilotInsights.Charts;
using CopilotInsights.Data;
using CopilotInsights.Infrastructure;
using Microsoft.AspNetCore.Html;

namespace CopilotInsights.Pages.Forecasting;

/// <summary>The weekly history both forecasting pages project from: the last 52 complete Saturday-to-Friday weeks.</summary>
sealed class WeekRow
{
    public DateTime WeekEnding { get; set; }
    public int Active { get; set; }
    public int ActiveLicensed { get; set; }
    public int ChatOnly { get; set; }
    public int Licensed { get; set; }
    public long Interactions { get; set; }
}

static class Weekly
{
    public static DateTime LastCompleteWeek(DateTime end) =>
        end.DayOfWeek == DayOfWeek.Friday ? end.Date : ReportContext.WeekEnding(end).AddDays(-7);

    public static async Task<List<WeekRow>> HistoryAsync(Db db, ReportContext c, int weeks = 52)
    {
        var w1 = LastCompleteWeek(c.End);
        var w0 = w1.AddDays(-7 * (weeks - 1));
        var args = c.Args(new { w0 = w0.AddDays(-6), we0 = w0, w1, wk0 = ReportContext.Key(w0.AddDays(-6)), wk1 = ReportContext.Key(w1) });
        const string Weeks = @"wk AS (SELECT DISTINCT WeekEnding, YEAR(WeekEnding) * 10000 + MONTH(WeekEnding) * 100 + DAY(WeekEnding) AS EndKey
       FROM bi.DimDate WHERE [Date] BETWEEN @w0 AND @w1 AND DayOfWeekNumber = 5)";
        List<WeekRow> rows;
        if (c.Rollups)
        {
            /* Each person's whole weeks are dash.UserWeek's rows. Two reads side by side, each on its own connection:
               the licenses held at each week's end with the holders who used Copilot under one that week, and the
               week's use; summing every person's days of the year took 13 seconds of processor at 160 million. */
            const string Range = "uw.WeekEnding BETWEEN CAST(@we0 AS DATE) AND CAST(@w1 AS DATE)";
            /* They name weeks, not date keys, so PlanHints leaves them as they are: the hint it would give, given here. */
            var hint = c.Root is null ? PlanHints.Typical : PlanHints.Exact;
            var licensedTask = db.QueryAsync<(DateTime WeekEnding, int Licensed, int ActiveLicensed)>($@"
WITH {Weeks},
held AS (SELECT wk.WeekEnding, ld.UserKey
         FROM wk JOIN bi.FactLicensedDay AS ld ON ld.DateKey = wk.EndKey {c.Scope("ld.UserKey")} {ReportContext.EnabledJoin("ld.UserKey")}),
lic AS (SELECT held.WeekEnding, COUNT(*) AS Licensed FROM held GROUP BY held.WeekEnding),
used AS (SELECT held.WeekEnding, COUNT(*) AS ActiveLicensed
         FROM held JOIN dash.UserWeek AS uw ON uw.WeekEnding = held.WeekEnding AND uw.UserKey = held.UserKey
         WHERE {Range} AND uw.LicensedUse > 0
         GROUP BY held.WeekEnding)
SELECT wk.WeekEnding, ISNULL(l.Licensed, 0), ISNULL(u.ActiveLicensed, 0)
FROM wk LEFT JOIN lic AS l ON l.WeekEnding = wk.WeekEnding LEFT JOIN used AS u ON u.WeekEnding = wk.WeekEnding
ORDER BY wk.WeekEnding
{hint};", args);
            var useTask = db.QueryAsync<(DateTime WeekEnding, int Active, int ChatOnly, long Interactions)>($@"
SELECT uw.WeekEnding, COUNT(CASE WHEN uw.Interactions > 0 AND {ReportContext.IsEnabled()} THEN 1 END),
       COUNT(CASE WHEN uw.Interactions > 0 AND uw.LicensedUse = 0 AND {ReportContext.IsEnabled()} THEN 1 END),
       SUM(CAST(uw.Interactions AS BIGINT))
FROM dash.UserWeek AS uw {c.Scope("uw.UserKey")} {ReportContext.EnabledJoin("uw.UserKey", left: true)}
WHERE {Range}
GROUP BY uw.WeekEnding
{hint};", args);
            await Task.WhenAll(licensedTask, useTask);
            var use = (await useTask).ToDictionary(u => u.WeekEnding);
            rows = (await licensedTask).Select(l => use.TryGetValue(l.WeekEnding, out var u)
                ? new WeekRow { WeekEnding = l.WeekEnding, Licensed = l.Licensed, ActiveLicensed = l.ActiveLicensed, Active = u.Active, ChatOnly = u.ChatOnly, Interactions = u.Interactions }
                : new WeekRow { WeekEnding = l.WeekEnding, Licensed = l.Licensed, ActiveLicensed = l.ActiveLicensed }).ToList();
            return rows.SkipWhile(r => r.Active == 0 && r.Licensed == 0).ToList();
        }
        rows = await db.QueryAsync<WeekRow>($@"
WITH per AS (SELECT d.WeekEnding, ud.UserKey, SUM(ud.Interactions) AS I, SUM(ud.LicensedUse) AS L
             FROM dash.UserDay AS ud {c.Scope("ud.UserKey")}
             JOIN bi.DimDate AS d ON d.DateKey = ud.DateKey
             WHERE ud.DateKey BETWEEN @wk0 AND @wk1
             GROUP BY d.WeekEnding, ud.UserKey),
{Weeks},
/* Who held a license at each week's end, read at those days only and matched to the person's week by both keys. */
held AS (SELECT wk.WeekEnding, ld.UserKey FROM wk JOIN bi.FactLicensedDay AS ld ON ld.DateKey = wk.EndKey),
/* Each week's people in one pass over per (it was read once for each column, 5 seconds at 40 million).
   The people, licensed or not, are accounts enabled today, as Metrics.TrendAsync counts them; the interactions are everyone's. */
agg AS (SELECT per.WeekEnding, COUNT(CASE WHEN per.I > 0 AND {ReportContext.IsEnabled()} THEN 1 END) AS Active,
               COUNT(CASE WHEN per.L > 0 AND la.UserKey IS NOT NULL AND {ReportContext.IsEnabled()} THEN 1 END) AS ActiveLicensed,
               COUNT(CASE WHEN per.I > 0 AND per.L = 0 AND {ReportContext.IsEnabled()} THEN 1 END) AS ChatOnly,
               SUM(CAST(per.I AS BIGINT)) AS Interactions
        FROM per
        LEFT JOIN held AS la ON la.WeekEnding = per.WeekEnding AND la.UserKey = per.UserKey
        {ReportContext.EnabledJoin("per.UserKey", left: true)}
        GROUP BY per.WeekEnding),
lic AS (SELECT held.WeekEnding, COUNT(*) AS Licensed
        FROM held {c.Scope("held.UserKey")} {ReportContext.EnabledJoin("held.UserKey")}
        GROUP BY held.WeekEnding)
SELECT wk.WeekEnding, ISNULL(a.Active, 0) AS Active, ISNULL(a.ActiveLicensed, 0) AS ActiveLicensed, ISNULL(a.ChatOnly, 0) AS ChatOnly,
       ISNULL(l.Licensed, 0) AS Licensed, ISNULL(a.Interactions, 0) AS Interactions
FROM wk LEFT JOIN agg AS a ON a.WeekEnding = wk.WeekEnding LEFT JOIN lic AS l ON l.WeekEnding = wk.WeekEnding
ORDER BY wk.WeekEnding;", args);
        /* Leading weeks before any data are not history; start at the first week with a license or any use. */
        return rows.SkipWhile(r => r.Active == 0 && r.Licensed == 0).ToList();
    }

    public static string Label(DateTime weekEnding) => "w/e " + weekEnding.ToString("d MMM");
}

/// <summary>A department's licenses against its use over the last 30 days, for License optimization and the renewal figure.</summary>
public sealed class DeptRow
{
    public string Department { get; set; } = "";
    public int People { get; set; }      // enabled accounts (3.1.3)
    public int Held { get; set; }        // licenses held by enabled accounts
    public int HeldDisabled { get; set; } // licenses held by disabled or departed accounts: paid seats, to reclaim
    public int Active { get; set; }      // enabled holders who used Copilot in the last 30 days
    public int Idle { get; set; }        // unused as Licensing › Unused licenses lists them, disabled accounts included
    public int Light { get; set; }       // enabled holders
    public int Demand { get; set; }      // enabled accounts without a license
}

/// <summary>The renewal figure's parts: licenses held, used in 30 days, forecast in 13 weeks, heavy demand, the spare share.</summary>
public sealed record RenewalSize(int Held, int Active, int Projected, int Demand, decimal SparePercent, int Target);

/// <summary>
/// One renewal figure. License optimization's right size and Budget and renewal's seats to renew were worked out two
/// ways (a forecast with a fixed 10% margin, and today's seats less revocations and leavers), so the two pages disagreed by
/// hundreds of seats. Both are this now: the licensed people forecast to use Copilot each week in 13 weeks (or today's, if
/// more), plus the spare share kept on purpose (SpareSeatPercent, as Costs and savings and License pool keep it), plus the
/// heavy Copilot Chat users with no license who would use one now.
/// </summary>
public static class Renewal
{
    /// <summary>Days of use in the last 30 that make someone without a license heavy demand.</summary>
    public const int Heavy = 6;

    /// <summary>The licenses that cover the use. Pure, for the tests.</summary>
    public static int Target(int active, int projected, int demand, decimal sparePercent) =>
        (int)Math.Ceiling(Math.Max(projected, active) * (1 + Math.Clamp(sparePercent, 0, 100) / 100m)) + Math.Max(0, demand);

    public static async Task<decimal> SparePercentAsync(Settings settings) =>
        decimal.TryParse(await settings.StringAsync("SpareSeatPercent"), System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var v)
            ? Math.Clamp(v, 0, 100) : 10m;

    /// <summary>
    /// SQL: the people who used Copilot on @inactiveKey's day or after, as Unused licenses reads it. From each person's
    /// last day of use once the refresh keeps it (dash.UserActivity), which is the same people as reading every day since.
    /// </summary>
    internal static string Ever(ReportContext c) => c.Rollups
        ? "SELECT ua.UserKey FROM dash.UserActivity AS ua WHERE ua.LastActiveKey >= @inactiveKey"
        : "SELECT DISTINCT ud.UserKey FROM dash.UserDay AS ud WHERE ud.DateKey >= @inactiveKey AND ud.Interactions > 0";

    /// <summary>SQL: the person <paramref name="userKeyExpr"/> used Copilot on @inactiveKey's day or after (see <see cref="Ever"/>).</summary>
    internal static string UsedSince(ReportContext c, string userKeyExpr) => c.Rollups
        ? $"EXISTS (SELECT 1 FROM dash.UserActivity AS ua WHERE ua.UserKey = {userKeyExpr} AND ua.LastActiveKey >= @inactiveKey)"
        : $"EXISTS (SELECT 1 FROM dash.UserDay AS ud WHERE ud.UserKey = {userKeyExpr} AND ud.DateKey >= @inactiveKey AND ud.Interactions > 0)";

    public static async Task<List<DeptRow>> ByDepartmentAsync(Db db, ReportContext c) =>
        (await db.QueryAsync<DeptRow>($@"
WITH a AS (SELECT p.UserKey, p.ActiveDays AS D FROM {c.PerPerson(30, "@k30")} AS p),
ever AS ({Ever(c)})
/* People, holders, use, light users and demand are accounts enabled today; a license on a disabled or departed
   account is a paid seat, counted apart (HeldDisabled) and among the unused, as Unused licenses lists it. */
SELECT ISNULL(NULLIF(du.Department, N''), N'(no department)') AS Department,
       COUNT(CASE WHEN du.AccountEnabled = 1 THEN 1 END) AS People,
       COUNT(CASE WHEN ld.UserKey IS NOT NULL AND du.AccountEnabled = 1 THEN 1 END) AS Held,
       COUNT(CASE WHEN ld.UserKey IS NOT NULL AND ISNULL(du.AccountEnabled, 0) = 0 THEN 1 END) AS HeldDisabled,
       COUNT(CASE WHEN ld.UserKey IS NOT NULL AND a.D > 0 AND du.AccountEnabled = 1 THEN 1 END) AS Active,
       /* 2.6.7: unused as Licensing › Unused licenses lists them: a license newer than the inactive days is not yet idle. */
       COUNT(CASE WHEN ld.UserKey IS NOT NULL AND ev.UserKey IS NULL AND pl.Upn IS NULL
                   AND (du.LicensedSinceUtc IS NULL OR du.LicensedSinceUtc <= DATEADD(DAY, -@idle, @d1)) THEN 1 END) AS Idle,
       COUNT(CASE WHEN ld.UserKey IS NOT NULL AND a.D BETWEEN 1 AND 2 AND pl.Upn IS NULL AND du.AccountEnabled = 1 THEN 1 END) AS Light,
       COUNT(CASE WHEN ld.UserKey IS NULL AND a.D >= @heavy AND du.AccountEnabled = 1 THEN 1 END) AS Demand
FROM bi.DimUser AS du {c.Scope("du.UserKey")}
LEFT JOIN bi.FactLicensedDay AS ld ON ld.UserKey = du.UserKey AND ld.DateKey = @k1
LEFT JOIN a ON a.UserKey = du.UserKey
LEFT JOIN ever AS ev ON ev.UserKey = du.UserKey
{CopilotInsights.Data.ProtectedLevels.Join("pl", "du.UserPrincipalName")}
WHERE (du.DirectoryStatus = N'Found' AND du.AccountEnabled = 1) OR ld.UserKey IS NOT NULL OR a.D > 0
GROUP BY ISNULL(NULLIF(du.Department, N''), N'(no department)')
/* In name order: when the smallest departments are folded together and several are the same size, the one added
   is the first of them, which was whichever the plan returned first, so it changed with the plan. */
ORDER BY Department;", c.Args(new { k30 = ReportContext.Key(c.End.AddDays(-29)), heavy = Heavy, idle = c.InactiveDays }))).ToList();

    /// <summary>The renewal figure for a context: from its departments' rows when the page already has them.</summary>
    public static async Task<RenewalSize> SizeAsync(Db db, Settings settings, ReportContext c, IReadOnlyCollection<DeptRow>? rows = null) =>
        (await WithDepartmentsAsync(db, settings, c, rows)).Size;

    /// <summary>The departments' rows and the renewal figure, the two reads side by side (each on its own connection).</summary>
    public static async Task<(IReadOnlyCollection<DeptRow> Rows, RenewalSize Size)> WithDepartmentsAsync(Db db, Settings settings, ReportContext c, IReadOnlyCollection<DeptRow>? rows = null)
    {
        var histTask = Weekly.HistoryAsync(db, c);
        rows ??= await ByDepartmentAsync(db, c);
        int held = rows.Sum(r => r.Held), active = rows.Sum(r => r.Active), demand = rows.Sum(r => r.Demand);
        var hist = await histTask;
        var proj = hist.Count >= 8 ? Forecast.Holt(hist.Select(h => (double)h.ActiveLicensed).ToList(), 13) : null;
        var projected = proj is null ? active : (int)Math.Ceiling(proj.At(13));
        var spare = await SparePercentAsync(settings);
        return (rows, new(held, active, projected, demand, spare, Target(active, projected, demand, spare)));
    }
}

public sealed class IndexModel(PageServices s) : InsightsPage(s)
{
    public override string? OwnWindow => "the last 52 weeks, projected 13, 26 or 52 weeks ahead";
    public override string Title => "Forecast";
    public override string Persona => "Forecasting";
    public override string Intro => "Where use and licenses are heading, from the last year of weekly history, with how sure the projection can be.";
    public override bool UsesFilters => true;

    protected override async Task BuildAsync()
    {
        var horizon = int.TryParse(Request.Query["h"], out var hq) && hq is 13 or 26 or 52 ? hq : 26;
        var hist = await Weekly.HistoryAsync(Db, Ctx);
        var links = string.Join(" · ", new[] { 13, 26, 52 }.Select(h =>
            $"<a href=\"{WebUtility.HtmlEncode(Ctx.Url(Request.Path, extra: $"h={h}"))}\"{(h == horizon ? " class=\"on\"" : "")}>{h} weeks</a>"));
        Panels.Add(new("How far ahead", new HtmlString($"<p class=\"sub\">Forecast: {links}</p>"),
            "Each line continues its own recent trend, counting recent weeks more than old ones and leveling the trend off the further ahead it goes (damped-trend exponential smoothing). "
            + "The shaded band is where the figure should fall 19 times in 20; it widens the further ahead it goes. This projects history: a planned rollout or reclaim will move it."));
        if (hist.Count < 8)
        {
            Notice = $"A forecast needs at least 8 weeks of history; there are {hist.Count}.";
            return;
        }

        var labels = hist.Select(h => Weekly.Label(h.WeekEnding))
            .Concat(Enumerable.Range(1, horizon).Select(i => Weekly.Label(hist[^1].WeekEnding.AddDays(7 * i)))).ToList();
        (Series, IReadOnlyList<double>, IReadOnlyList<double>, IReadOnlyList<double>) Line(string name, int slot, Func<WeekRow, double> pick, double? ceiling = null)
        {
            var y = hist.Select(pick).ToList();
            var p = Forecast.Holt(y, horizon, ceiling)!;
            return (new Series(name, y.Select(v => (double?)v).ToList(), slot), p.Forecast, p.Low, p.High);
        }

        var licensed = Line("License holders", 1, h => h.Licensed);
        var active = Line("Used Copilot", 3, h => h.ActiveLicensed);
        var chat = Line("Free Copilot Chat only", 2, h => h.ChatOnly);
        var inter = Line("Interactions", 1, h => h.Interactions);

        double Ahead((Series, IReadOnlyList<double> F, IReadOnlyList<double>, IReadOnlyList<double>) l) => l.F[^1];
        var nowHeld = hist[^1].Licensed; var nowActive = hist[^1].ActiveLicensed;
        Kpis.Add(new($"Premium licenses in {horizon} weeks", Num(Math.Round(Ahead(licensed))), $"now {nowHeld:#,0} on enabled accounts, on the current trend"));
        Kpis.Add(new($"License holders using Copilot in {horizon} weeks", Num(Math.Round(Ahead(active))), $"in a week; now {nowActive:#,0} a week"));
        Kpis.Add(new("Weekly adoption then", Pct(Ratio(Ahead(active), Ahead(licensed))), $"license holders using Copilot in a week, of the licenses held; now {Pct(Ratio(nowActive, nowHeld))}"));
        Kpis.Add(new($"People on Copilot Chat without a license in {horizon} weeks", Num(Math.Round(Ahead(chat))), $"now {hist[^1].ChatOnly:#,0} a week: demand for a license"));

        Panels.Add(new("Premium license holders, and how many use Copilot each week",
            Chart.Projection(labels, hist.Count, [licensed, active], Fmt.Count, "Licenses and active licensed people, forecast"),
            "Each point is a Saturday-to-Friday week. Both lines count accounts enabled today; licenses on disabled or departed accounts are on Licensing › Licenses on disabled accounts. The gap between the lines is licenses not used that week; if it widens, licenses are being bought faster than they are taken up."));
        Panels.Add(new("People on Copilot Chat without a license",
            Chart.Projection(labels, hist.Count, [chat], Fmt.Count, "Unlicensed Copilot Chat users, forecast"),
            "People with an enabled account and no license who used Copilot Chat each week. A rising line is demand the license count does not yet meet.", "half"));
        Panels.Add(new("Interactions",
            Chart.Projection(labels, hist.Count, [inter], Fmt.Count, "Weekly interactions, forecast"), "Interactions (one prompt and Copilot's answer) each week, by anyone, licensed or not, disabled and departed accounts included.", "half"));

        /* Adoption rate, projected as a ratio of the two projections. */
        var rateHist = hist.Select(h => Ratio(h.ActiveLicensed, h.Licensed)).ToList();
        var rateF = licensed.Item2.Zip(active.Item2, (l, a) => l > 0 ? Math.Min(1, a / l) : 0).ToList();
        var rateLo = licensed.Item4.Zip(active.Item3, (l, a) => l > 0 ? Math.Min(1, a / l) : 0).ToList();
        var rateHi = licensed.Item3.Zip(active.Item4, (l, a) => l > 0 ? Math.Min(1, a / l) : 0).ToList();
        Panels.Add(new("Weekly adoption",
            Chart.Projection(labels, hist.Count, [(new Series("Adoption", rateHist, 3), rateF, rateLo, rateHi)], Fmt.Percent, "Weekly adoption, forecast"),
            "Licensed people active in the week, as a share of the licenses held, both on accounts enabled today. The band combines both forecasts' intervals, so it is wide."));
    }
}

public sealed class OptimizeModel(PageServices s) : InsightsPage(s)
{
    public override string Title => "License optimization";
    public override string Persona => "Forecasting";
    public override string? OwnWindow => "the last 30 days and the 13-week forecast";
    public override string Intro => "What to do with Copilot licenses: reclaim, reassign, coach or buy, with what each is worth. Based on the last 30 days and the 13-week forecast.";
    public override bool UsesFilters => true;

    protected override async Task BuildAsync()
    {
        var price = decimal.TryParse(await S.Settings.StringAsync("LicenseMonthlyCost"), System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var p) ? p : 30m;
        var currency = await S.Settings.StringAsync("LicenseCurrency") ?? "USD";
        string Money(decimal v) => $"{v:#,0} {currency}";
        /* The departments and the renewal figure as Budget and renewal works them out (see Renewal). */
        var (rows, size) = await Renewal.WithDepartmentsAsync(Db, S.Settings, Ctx);
        int held = size.Held, idle = rows.Sum(r => r.Idle), light = rows.Sum(r => r.Light), demand = size.Demand, active = size.Active;
        /* The licenses on disabled or departed accounts are paid seats too: the right size is set against every seat held. */
        var heldDisabled = rows.Sum(r => r.HeldDisabled);
        var (projectedActive, target) = (size.Projected, size.Target);
        var change = target - (held + heldDisabled);
        var reassign = Math.Min(idle, demand);

        Kpis.Add(new("Licenses held", Num(held), $"enabled accounts; {Pct(Ratio(active, held))} used in the last 30 days" + (heldDisabled > 0 ? $"; {heldDisabled:#,0} more on disabled accounts" : ""), Href: Open("/licensing")));
        Kpis.Add(new($"Unused for {Ctx.InactiveDays} days or more", Num(idle), $"{Money(idle * price)} a month", idle > 0 ? "crit" : null, Href: Open("/licensing/unused"),
            Why: await WhyAsync($"Holders with no use at all in {Ctx.InactiveDays} days ({idle:#,0}) × the Copilot license price = {Money(idle * price)} a month. Not the Revoke list on Upgrade, train or revoke, which counts holders with no Premium use in 30 days: some of them used Copilot Chat.", "InactiveDays", "LicenseMonthlyCost")));
        Kpis.Add(new("Heavy Copilot Chat users", Num(demand), "no license, used it on 6+ of the last 30 days", Href: Open("/forecasting/actions", extra: "kind=upgrade")));
        Kpis.Add(new("Right size in 13 weeks", Num(target), "licenses to cover forecast use, the spare share and heavy demand; " + (change == 0 ? "as held" : change > 0 ? $"{change:#,0} more than held" : $"{-change:#,0} fewer than held"),
            Why: heldDisabled > 0 ? await WhyAsync($"Set against every license held today ({held + heldDisabled:#,0}), the {heldDisabled:#,0} on disabled or departed accounts included: they are paid for until reclaimed.") : null));

        bool Can(string role) => Viewer.Has(role);
        string A(string text, string path, string role, string? extra = null) =>
            Can(role) ? $"<a href=\"{WebUtility.HtmlEncode(Ctx.Url(path, extra: extra))}\">{WebUtility.HtmlEncode(text)}</a>" : "";
        var recs = new StringBuilder("<div class=\"recs\">");
        void Rec(string cls, string title, string big, string text, string link = "")
            => recs.Append($"<div class=\"rec {cls}\"><h3>{WebUtility.HtmlEncode(title)}</h3><div class=\"big\">{WebUtility.HtmlEncode(big)}</div><p>{WebUtility.HtmlEncode(text)}{(link == "" ? "" : " " + link)}</p></div>");
        if (reassign > 0)
            Rec("save", "Revoke and reassign", $"{reassign:#,0} licenses",
                $"Move licenses unused for {Ctx.InactiveDays}+ days to the heavy Copilot Chat users who would use them. No new cost: the licenses are paid already, and {Money(reassign * price)} a month of unused spend turns into use. (Upgrade, train or revoke prices its Upgrade list as new licenses.)",
                A("Unused licenses", "/licensing/unused", Roles.Licensing) + " " + A("Upgrade list", "/forecasting/actions", Roles.Licensing, "kind=upgrade"));
        if (idle - reassign > 0)
            Rec("save", "Reclaim what is left", Money((idle - reassign) * price) + " a month",
                $"{idle - reassign:#,0} licenses unused for {Ctx.InactiveDays}+ days with no one waiting for them. Reclaim at renewal, or after a reminder.",
                A("Who they are", "/licensing/unused", Roles.Licensing));
        if (demand - reassign > 0)
            Rec("spend", "Upgrade proven Copilot Chat users", $"{demand - reassign:#,0} licenses",
                $"Heavy Copilot Chat users with no license and none free to move: {Money((demand - reassign) * price)} a month, for people already using Copilot most days.",
                A("Who they are", "/forecasting/actions", Roles.Licensing, "kind=upgrade"));
        if (light > 0)
            Rec("info", "Train before revoking", $"{light:#,0} people",
                "Licensed, but used Copilot on only one or two days in the last 30. Training or a champion usually does more than taking the license back. Upgrade, train or revoke's Train list counts days of Premium use instead, so its count differs.",
                A("Who to train", "/forecasting/actions", Roles.Licensing, "kind=train"));
        Rec(change > 0 ? "spend" : "save", "Right-size for the next quarter", change == 0 ? "Hold" : change > 0 ? $"+{change:#,0}" : $"{change:#,0}",
            $"On the forecast, about {projectedActive:#,0} licensed people will use Copilot each week in 13 weeks. With the {size.SparePercent:0.##}% kept spare and today's heavy demand, "
            + $"{target:#,0} licenses cover it: {(change >= 0 ? "a cost of " + Money(change * price) : "a saving of " + Money(-change * price))} a month against today. Budget and renewal sets the same figure against the contracts.");
        recs.Append("</div>");
        Panels.Add(new("Recommendations", new HtmlString(recs.ToString() + await ProtectedNoteAsync()),
            $"At {Money(price)} a license a month (set in Admin › Access). Links to named lists appear for people with the role."));

        var shown = FoldSmall(rows, r => r.People, small => new DeptRow
        {
            Department = "Smaller departments", People = small.Sum(r => r.People), Held = small.Sum(r => r.Held), HeldDisabled = small.Sum(r => r.HeldDisabled), Active = small.Sum(r => r.Active),
            Idle = small.Sum(r => r.Idle), Light = small.Sum(r => r.Light), Demand = small.Sum(r => r.Demand),
        }, out var folded).OrderByDescending(r => Math.Abs(r.Demand - r.Idle)).ThenBy(r => r.Department, StringComparer.Ordinal).ToList();
        Panels.Add(new("Unused licenses against demand, by department",
            Chart.Columns(shown.Take(15).Select(r => r.Department).ToList(), [
                new Series("Unused", shown.Take(15).Select(r => (double?)r.Idle).ToList(), 2),
                new Series("Heavy demand", shown.Take(15).Select(r => (double?)r.Demand).ToList(), 3),
            ], Fmt.Count, "Unused licenses and demand by department", stacked: false),
            "Where licenses sit unused and where people without one use Copilot Chat most. The departments with the biggest gap either way come first."));
        var t = new Table
        {
            Headers = ["Department", "People", "Licenses held", "On disabled accounts", "Licenses used, last 30 days", "Unused licenses", "Light users", "Heavy demand", "Suggested change in licenses", "Monthly effect"],
            Numeric = new HashSet<int> { 1, 2, 3, 4, 5, 6, 7, 8, 9 },
        };
        foreach (var r in shown)
        {
            var delta = r.Demand - r.Idle;
            t.Rows.Add([r.Department, r.People, r.Held, r.HeldDisabled, r.Active, r.Idle, r.Light, r.Demand, delta == 0 ? "—" : delta > 0 ? $"+{delta:#,0}" : $"{delta:#,0}",
                        delta == 0 ? "—" : (delta > 0 ? "+" : "-") + Money(Math.Abs(delta) * price)]);
        }
        CsvTable = t;
        Panels.Add(new("By department", t.Render(),
            "People, licenses held, licenses used, light users and heavy demand count accounts enabled today; On disabled accounts: licenses held by disabled or departed accounts, still paid for. " +
            "Unused licenses: held at least the unused-after days (Admin › Access) by someone who has not used Copilot within them, the protected levels left out, as Licensing › Unused licenses lists them, so licenses on disabled accounts are among them. Light users: license holders who used it on 1 or 2 days in the last 30. " +
            "Heavy demand: people without a license who used the free Copilot Chat on 6 or more of the last 30 days. Suggested change: heavy demand less unused licenses, within the department; negative is licenses to release." +
            (folded > 0 ? " " + FoldNote(folded, "department") : ""), "full", CsvUrl()));
    }
}
