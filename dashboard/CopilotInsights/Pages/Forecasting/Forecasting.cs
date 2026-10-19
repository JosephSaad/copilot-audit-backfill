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
        var rows = await db.QueryAsync<WeekRow>($@"
WITH per AS (SELECT d.WeekEnding, ud.UserKey, SUM(ud.Interactions) AS I, SUM(ud.LicensedUse) AS L
             FROM dash.UserDay AS ud {c.Scope("ud.UserKey")}
             JOIN bi.DimDate AS d ON d.DateKey = ud.DateKey
             WHERE ud.DateKey BETWEEN @wk0 AND @wk1
             GROUP BY d.WeekEnding, ud.UserKey),
wk AS (SELECT DISTINCT WeekEnding, YEAR(WeekEnding) * 10000 + MONTH(WeekEnding) * 100 + DAY(WeekEnding) AS EndKey
       FROM bi.DimDate WHERE [Date] BETWEEN @w0 AND @w1 AND DayOfWeekNumber = 5),
/* Each week's people in one pass over per (2.4.7: it was read once for each column, 5 seconds at 40 million). */
agg AS (SELECT per.WeekEnding, COUNT(CASE WHEN per.I > 0 THEN 1 END) AS Active,
               COUNT(CASE WHEN per.L > 0 AND la.UserKey IS NOT NULL THEN 1 END) AS ActiveLicensed,
               COUNT(CASE WHEN per.I > 0 AND per.L = 0 THEN 1 END) AS ChatOnly,
               SUM(CAST(per.I AS BIGINT)) AS Interactions
        FROM per JOIN wk ON wk.WeekEnding = per.WeekEnding
        LEFT JOIN bi.FactLicensedDay AS la ON la.UserKey = per.UserKey AND la.DateKey = wk.EndKey
        GROUP BY per.WeekEnding),
lic AS (SELECT wk.WeekEnding, COUNT(*) AS Licensed
        FROM wk JOIN bi.FactLicensedDay AS ld ON ld.DateKey = wk.EndKey {c.Scope("ld.UserKey")}
        GROUP BY wk.WeekEnding)
SELECT wk.WeekEnding, ISNULL(a.Active, 0) AS Active, ISNULL(a.ActiveLicensed, 0) AS ActiveLicensed, ISNULL(a.ChatOnly, 0) AS ChatOnly,
       ISNULL(l.Licensed, 0) AS Licensed, ISNULL(a.Interactions, 0) AS Interactions
FROM wk LEFT JOIN agg AS a ON a.WeekEnding = wk.WeekEnding LEFT JOIN lic AS l ON l.WeekEnding = wk.WeekEnding
ORDER BY wk.WeekEnding;", c.Args(new { w0 = w0.AddDays(-6), w1, wk0 = ReportContext.Key(w0.AddDays(-6)), wk1 = ReportContext.Key(w1) }));
        /* Leading weeks before any data are not history; start at the first week with a license or any use. */
        return rows.SkipWhile(r => r.Active == 0 && r.Licensed == 0).ToList();
    }

    public static string Label(DateTime weekEnding) => "w/e " + weekEnding.ToString("d MMM");
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
        Kpis.Add(new($"Premium licenses in {horizon} weeks", Num(Math.Round(Ahead(licensed))), $"now {nowHeld:#,0}, on the current trend"));
        Kpis.Add(new($"License holders using Copilot in {horizon} weeks", Num(Math.Round(Ahead(active))), $"now {nowActive:#,0} a week"));
        Kpis.Add(new("Weekly adoption then", Pct(Ratio(Ahead(active), Ahead(licensed))), $"now {Pct(Ratio(nowActive, nowHeld))}"));
        Kpis.Add(new($"People on the free Copilot Chat in {horizon} weeks", Num(Math.Round(Ahead(chat))), $"now {hist[^1].ChatOnly:#,0} a week: demand for a license"));

        Panels.Add(new("Premium license holders, and how many use Copilot each week",
            Chart.Projection(labels, hist.Count, [licensed, active], Fmt.Count, "Licenses and active licensed people, forecast"),
            "Each point is a Saturday-to-Friday week. The gap between the lines is licenses not used that week; if it widens, licenses are being bought faster than they are taken up."));
        Panels.Add(new("People on the free Copilot Chat",
            Chart.Projection(labels, hist.Count, [chat], Fmt.Count, "Unlicensed Copilot Chat users, forecast"),
            "People using the free Copilot Chat each week. A rising line is demand the license count does not yet meet.", "half"));
        Panels.Add(new("Interactions",
            Chart.Projection(labels, hist.Count, [inter], Fmt.Count, "Weekly interactions, forecast"), "Prompts and their answers each week, by anyone, licensed or not.", "half"));

        /* Adoption rate, projected as a ratio of the two projections. */
        var rateHist = hist.Select(h => Ratio(h.ActiveLicensed, h.Licensed)).ToList();
        var rateF = licensed.Item2.Zip(active.Item2, (l, a) => l > 0 ? Math.Min(1, a / l) : 0).ToList();
        var rateLo = licensed.Item4.Zip(active.Item3, (l, a) => l > 0 ? Math.Min(1, a / l) : 0).ToList();
        var rateHi = licensed.Item3.Zip(active.Item4, (l, a) => l > 0 ? Math.Min(1, a / l) : 0).ToList();
        Panels.Add(new("Weekly adoption",
            Chart.Projection(labels, hist.Count, [(new Series("Adoption", rateHist, 3), rateF, rateLo, rateHi)], Fmt.Percent, "Weekly adoption, forecast"),
            "Licensed people active in the week, as a share of licenses held. The band combines both forecasts' intervals, so it is wide."));
    }
}

public sealed class OptimizeModel(PageServices s) : InsightsPage(s)
{
    public override string Title => "License optimization";
    public override string Persona => "Forecasting";
    public override string? OwnWindow => "the last 30 days and the 13-week forecast";
    public override string Intro => "What to do with Copilot licenses: reclaim, reassign, coach or buy, with what each is worth. Based on the last 30 days and the 13-week forecast.";
    public override bool UsesFilters => true;

    sealed class DeptRow
    {
        public string Department { get; set; } = "";
        public int People { get; set; }
        public int Held { get; set; }
        public int Active { get; set; }
        public int Idle { get; set; }
        public int Light { get; set; }
        public int Demand { get; set; }
    }

    protected override async Task BuildAsync()
    {
        var price = decimal.TryParse(await S.Settings.StringAsync("LicenseMonthlyCost"), System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var p) ? p : 30m;
        var currency = await S.Settings.StringAsync("LicenseCurrency") ?? "USD";
        string Money(decimal v) => $"{v:#,0} {currency}";
        var k30 = ReportContext.Key(Ctx.End.AddDays(-29));

        var rows = await Db.QueryAsync<DeptRow>($@"
WITH a AS (SELECT ud.UserKey, COUNT(CASE WHEN ud.Interactions > 0 THEN 1 END) AS D, MAX(CASE WHEN ud.Interactions > 0 THEN ud.DateKey END) AS LastKey
           FROM dash.UserDay AS ud WHERE ud.DateKey BETWEEN @k30 AND @k1 GROUP BY ud.UserKey),
ever AS (SELECT DISTINCT ud.UserKey FROM dash.UserDay AS ud WHERE ud.DateKey >= @inactiveKey AND ud.Interactions > 0)
SELECT ISNULL(NULLIF(du.Department, N''), N'(no department)') AS Department,
       COUNT(*) AS People,
       COUNT(ld.UserKey) AS Held,
       COUNT(CASE WHEN ld.UserKey IS NOT NULL AND a.D > 0 THEN 1 END) AS Active,
       COUNT(CASE WHEN ld.UserKey IS NOT NULL AND ev.UserKey IS NULL AND pl.Upn IS NULL THEN 1 END) AS Idle,
       COUNT(CASE WHEN ld.UserKey IS NOT NULL AND a.D BETWEEN 1 AND 2 AND pl.Upn IS NULL THEN 1 END) AS Light,
       COUNT(CASE WHEN ld.UserKey IS NULL AND a.D >= @heavy THEN 1 END) AS Demand
FROM bi.DimUser AS du {Ctx.Scope("du.UserKey")}
LEFT JOIN bi.FactLicensedDay AS ld ON ld.UserKey = du.UserKey AND ld.DateKey = @k1
LEFT JOIN a ON a.UserKey = du.UserKey
LEFT JOIN ever AS ev ON ev.UserKey = du.UserKey
{CopilotInsights.Data.ProtectedLevels.Join("pl", "du.UserPrincipalName")}
WHERE (du.DirectoryStatus = N'Found' AND du.AccountEnabled = 1) OR ld.UserKey IS NOT NULL OR a.D > 0
GROUP BY ISNULL(NULLIF(du.Department, N''), N'(no department)');", Ctx.Args(new { k30, heavy = 6 }));

        int held = rows.Sum(r => r.Held), idle = rows.Sum(r => r.Idle), light = rows.Sum(r => r.Light), demand = rows.Sum(r => r.Demand), active = rows.Sum(r => r.Active);
        var hist = await Weekly.HistoryAsync(Db, Ctx);
        var proj = hist.Count >= 8 ? Forecast.Holt(hist.Select(h => (double)h.ActiveLicensed).ToList(), 13) : null;
        var projectedActive = proj is null ? active : (int)Math.Ceiling(proj.At(13));
        /* Enough for the people forecast to use a license each week in 13 weeks, a 10% margin for monthly-only users,
           and the heavy Copilot Chat users who would use one now. */
        var target = (int)Math.Ceiling(Math.Max(projectedActive, active) * 1.10) + demand;
        var change = target - held;
        var reassign = Math.Min(idle, demand);

        Kpis.Add(new("Licenses held", Num(held), $"{Pct(Ratio(active, held))} used in the last 30 days"));
        Kpis.Add(new($"Unused for {Ctx.InactiveDays} days or more", Num(idle), $"{Money(idle * price)} a month", idle > 0 ? "crit" : null, Href: Ctx.Url("/forecasting/actions", extra: "kind=revoke")));
        Kpis.Add(new("Heavy Copilot Chat users", Num(demand), "no license, used it on 6+ of the last 30 days", Href: Ctx.Url("/forecasting/actions", extra: "kind=upgrade")));
        Kpis.Add(new("Right size in 13 weeks", Num(target), change == 0 ? "as held" : change > 0 ? $"{change:#,0} more than held" : $"{-change:#,0} fewer than held"));

        bool Can(string role) => Viewer.Has(role);
        string A(string text, string path, string role, string? extra = null) =>
            Can(role) ? $"<a href=\"{WebUtility.HtmlEncode(Ctx.Url(path, extra: extra))}\">{WebUtility.HtmlEncode(text)}</a>" : "";
        var recs = new StringBuilder("<div class=\"recs\">");
        void Rec(string cls, string title, string big, string text, string link = "")
            => recs.Append($"<div class=\"rec {cls}\"><h3>{WebUtility.HtmlEncode(title)}</h3><div class=\"big\">{WebUtility.HtmlEncode(big)}</div><p>{WebUtility.HtmlEncode(text)}{(link == "" ? "" : " " + link)}</p></div>");
        if (reassign > 0)
            Rec("save", "Revoke and reassign", $"{reassign:#,0} licenses",
                $"Move licenses unused for {Ctx.InactiveDays}+ days to the heavy Copilot Chat users who would use them. Costs nothing; turns {Money(reassign * price)} a month of idle spend into use.",
                A("Revoke list", "/forecasting/actions", Roles.Licensing, "kind=revoke") + " " + A("Upgrade list", "/forecasting/actions", Roles.Licensing, "kind=upgrade"));
        if (idle - reassign > 0)
            Rec("save", "Reclaim what is left", Money((idle - reassign) * price) + " a month",
                $"{idle - reassign:#,0} licenses unused for {Ctx.InactiveDays}+ days with no one waiting for them. Reclaim at renewal, or after a reminder.",
                A("Who they are", "/forecasting/actions", Roles.Licensing, "kind=revoke"));
        if (demand - reassign > 0)
            Rec("spend", "Upgrade proven Copilot Chat users", $"{demand - reassign:#,0} licenses",
                $"Heavy Copilot Chat users with no license and none free to move: {Money((demand - reassign) * price)} a month, for people already using Copilot most days.",
                A("Who they are", "/forecasting/actions", Roles.Licensing, "kind=upgrade"));
        if (light > 0)
            Rec("info", "Train before revoking", $"{light:#,0} people",
                "Licensed, but used Copilot on only one or two days in the last 30. Training or a champion usually does more than taking the license back.",
                A("Who to train", "/forecasting/actions", Roles.Licensing, "kind=train"));
        Rec(change > 0 ? "spend" : "save", "Right-size for the next quarter", change == 0 ? "Hold" : change > 0 ? $"+{change:#,0}" : $"{change:#,0}",
            $"On the forecast, about {projectedActive:#,0} licensed people will use Copilot each week in 13 weeks. With a 10% margin and today's heavy demand, "
            + $"{target:#,0} licenses cover it: {(change >= 0 ? "a cost of " + Money(change * price) : "a saving of " + Money(-change * price))} a month against today.");
        recs.Append("</div>");
        Panels.Add(new("Recommendations", new HtmlString(recs.ToString() + await ProtectedNoteAsync()),
            $"At {Money(price)} a license a month (set in Admin › Access). Links to named lists appear for people with the role."));

        var shown = FoldSmall(rows, r => r.People, small => new DeptRow
        {
            Department = "Smaller departments", People = small.Sum(r => r.People), Held = small.Sum(r => r.Held), Active = small.Sum(r => r.Active),
            Idle = small.Sum(r => r.Idle), Light = small.Sum(r => r.Light), Demand = small.Sum(r => r.Demand),
        }, out var folded).OrderByDescending(r => Math.Abs(r.Demand - r.Idle)).ThenBy(r => r.Department, StringComparer.Ordinal).ToList();
        Panels.Add(new("Unused licenses against demand, by department",
            Chart.Columns(shown.Take(15).Select(r => r.Department).ToList(), [
                new Series("Unused", shown.Take(15).Select(r => (double?)r.Idle).ToList(), 2),
                new Series("Heavy demand", shown.Take(15).Select(r => (double?)r.Demand).ToList(), 3),
            ], Fmt.Count, "Unused licenses and demand by department", stacked: false),
            "Where licenses sit idle and where people without one use Copilot most. The departments with the biggest gap either way come first."));
        var t = new Table
        {
            Headers = ["Department", "People", "Licenses held", "Licenses used, last 30 days", "Unused licenses", "Light users", "Heavy demand", "Suggested change in licenses", "Monthly effect"],
            Numeric = new HashSet<int> { 1, 2, 3, 4, 5, 6, 7, 8 },
        };
        foreach (var r in shown)
        {
            var delta = r.Demand - r.Idle;
            t.Rows.Add([r.Department, r.People, r.Held, r.Active, r.Idle, r.Light, r.Demand, delta == 0 ? "—" : delta > 0 ? $"+{delta:#,0}" : $"{delta:#,0}",
                        delta == 0 ? "—" : (delta > 0 ? "+" : "-") + Money(Math.Abs(delta) * price)]);
        }
        CsvTable = t;
        Panels.Add(new("By department", t.Render(),
            "Unused licenses: held by someone who has not used Copilot within the unused-after days (Admin > Access). Light users: license holders who used it on 1 or 2 days in the last 30. " +
            "Heavy demand: people without a license who used the free Copilot Chat on 6 or more of the last 30 days. Suggested change: heavy demand less unused licenses, within the department; negative is licenses to release." +
            (folded > 0 ? " " + FoldNote(folded, "department") : ""), "full", CsvUrl()));
    }
}
