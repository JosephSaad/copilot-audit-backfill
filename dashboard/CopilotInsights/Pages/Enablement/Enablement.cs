using CopilotInsights.Charts;
using CopilotInsights.Data;
using CopilotInsights.Infrastructure;
using Microsoft.AspNetCore.Html;

namespace CopilotInsights.Pages.Enablement;

/// <summary>
/// Engagement segments, by the days a person used Copilot against the working days in the period:
/// Power (half or more), Regular (a fifth or more, at least two), Occasional (two or more), One-off (one day),
/// Not started (licensed, no use in the period).
/// </summary>
static class Segments
{
    public static readonly string[] Order = ["Power", "Regular", "Occasional", "One-off", "Not started"];
    public static readonly int[] Slots = [3, 1, 4, 2, 8];

    public static string Sql(string days) => $@"CASE WHEN {days} >= CEILING(@w * 0.5) AND {days} >= 3 THEN N'Power'
                  WHEN {days} >= CEILING(@w * 0.2) AND {days} >= 2 THEN N'Regular'
                  WHEN {days} >= 2 THEN N'Occasional' WHEN {days} = 1 THEN N'One-off' ELSE N'Not started' END";

    /// <summary>Everyone licensed today or active in the period, with their active days.</summary>
    public static string People(ReportContext c) => $@"
act AS (SELECT ud.UserKey, COUNT(CASE WHEN ud.Interactions > 0 THEN 1 END) AS D, SUM(ud.Interactions) AS I,
               SUM(ud.Sessions) AS S, SUM(ud.MultiTurnSessions) AS MT, MAX(CASE WHEN ud.Interactions > 0 THEN ud.DateKey END) AS LastKey
        FROM dash.UserDay AS ud WHERE ud.DateKey BETWEEN @k0 AND @k1 GROUP BY ud.UserKey),
pop AS (SELECT du.UserKey, ISNULL(a.D, 0) AS D, ISNULL(a.I, 0) AS I, ISNULL(a.S, 0) AS S, ISNULL(a.MT, 0) AS MT, a.LastKey,
               {Sql("ISNULL(a.D, 0)")} AS Segment
        FROM bi.DimUser AS du {c.Scope("du.UserKey")}
        LEFT JOIN act AS a ON a.UserKey = du.UserKey
        WHERE a.I > 0 OR du.CopilotLicenseToday = N'Licensed')";

    public static async Task<int> WorkdaysAsync(Db db, ReportContext c) =>
        await db.ScalarAsync<int>("SELECT COUNT(*) FROM bi.DimDate WHERE DateKey BETWEEN @k0 AND @k1 AND IsWeekday = 1;", c.Args());
}

public sealed class IndexModel(PageServices s, Metrics m) : InsightsPage(s)
{
    public override string Title => "Engagement";
    public override string Persona => "Enablement";
    public override string Intro => "How deeply people use Copilot: habit, conversation rather than one-off questions, and breadth across apps. Use it to aim training.";

    protected override async Task BuildAsync()
    {
        var w = await Segments.WorkdaysAsync(Db, Ctx);
        var seg = await Db.QueryAsync<(string Segment, int N)>($"WITH {Segments.People(Ctx)} SELECT Segment, COUNT(*) FROM pop GROUP BY Segment;", Ctx.Args(new { w }));
        int Seg(string name) => seg.Where(x => x.Segment == name).Sum(x => x.N);
        var t = await m.TotalsAsync(Ctx);
        var tr = await m.TrendAsync(Ctx);

        Kpis.Add(new("Power users", Num(Seg("Power")), $"used it on half or more of the {w} working days"));
        Kpis.Add(new("Multi-turn sessions", Pct(Ratio(t.MultiTurn, t.Sessions)), "sessions with a follow-up prompt"));
        Kpis.Add(new("Prompts per session", t.Sessions > 0 ? (t.Prompts / (double)t.Sessions).ToString("0.0") : "—"));
        Kpis.Add(new("Need a nudge", Num(Seg("One-off") + Seg("Not started")), "licensed or active, but one day or none"));

        Panels.Add(new("Engagement segments",
            Chart.Bars(Segments.Order.Select((name, i) => new Bar(name, Seg(name), Viewer.Has(Roles.Enablement) ? Ctx.Url("/enablement/people", extra: "seg=" + name) : null, Slot: Segments.Slots[i])).ToList(), Fmt.Count, "People by segment"),
            $"Everyone licensed today or active in the period, by the days they used Copilot out of {w} working days. " +
            "Power: half or more. Regular: a fifth or more. Occasional: two days or more. One-off: one day.", "half"));

        var units = await Db.QueryAsync<(string Unit, string Segment, int N)>($@"
WITH {Segments.People(Ctx)}
SELECT ISNULL(NULLIF(du.Department, N''), N'(no department)'), p.Segment, COUNT(*)
FROM pop AS p JOIN bi.DimUser AS du ON du.UserKey = p.UserKey
GROUP BY ISNULL(NULLIF(du.Department, N''), N'(no department)'), p.Segment;", Ctx.Args(new { w }));
        var depts = units.GroupBy(u => u.Unit).Select(g => (Dept: g.Key, People: g.Sum(x => x.N))).ToList();
        var big = depts.Where(d => d.People >= Ctx.MinGroupSize).OrderByDescending(d => d.People).Take(12).Select(d => d.Dept).ToList();
        var tbl = new Table { Headers = ["Department", "People", .. Segments.Order], Numeric = new HashSet<int> { 1, 2, 3, 4, 5, 6 } };
        foreach (var d in big)
        {
            var n = units.Where(u => u.Unit == d).Sum(u => u.N);
            tbl.Rows.Add([d, n, .. Segments.Order.Select(sg => (object?)Pct(Ratio(units.Where(u => u.Unit == d && u.Segment == sg).Sum(u => u.N), n)))]);
        }
        Panels.Add(new("Segments by department", tbl.Render(), $"The twelve largest departments, as a share of each one's people. Departments under {Ctx.MinGroupSize} are left out.", "half"));

        Panels.Add(new("Conversations, not one-off questions",
            Chart.Line(Labels(), [new Series("Multi-turn share", tr.Select(x => Ratio(x.MultiTurn, x.Sessions)).ToList(), 1)], Fmt.Percent, "Multi-turn share"),
            "Sessions with more than one prompt, as a share of all sessions. A rising line means people are learning to iterate.", "half"));
        Panels.Add(new("Prompts per session",
            Chart.Line(Labels(), [new Series("Prompts per session", tr.Select(x => x.Sessions > 0 ? x.Prompts / (double?)x.Sessions : null).ToList(), 2)], Fmt.Decimal1, "Prompts per session"),
            TrendNote, "half"));

        var breadth = await Db.QueryAsync<(int Apps, int N)>($@"
WITH u AS (SELECT f.UserKey, COUNT(DISTINCT f.AppKey) AS Apps FROM bi.FactInteraction AS f {Ctx.Scope("f.UserKey")}
           WHERE f.DateKey BETWEEN @k0 AND @k1 GROUP BY f.UserKey)
SELECT CASE WHEN Apps >= 5 THEN 5 ELSE Apps END, COUNT(*) FROM u GROUP BY CASE WHEN Apps >= 5 THEN 5 ELSE Apps END ORDER BY 1;", Ctx.Args());
        Panels.Add(new("Breadth: apps used per person",
            Chart.Bars(breadth.Select(b => new Bar(b.Apps == 5 ? "5 or more apps" : b.Apps == 1 ? "1 app" : $"{b.Apps} apps", b.N)).ToList(), Fmt.Count, "Apps per active person"),
            "Active people by how many Microsoft 365 apps they used Copilot in. One app often means Copilot Chat only.", "half"));

        var apps = await Db.QueryAsync<(string App, int People)>($@"
SELECT dash.fnValueName(2, a.App), COUNT(DISTINCT f.UserKey) FROM bi.FactInteraction AS f {Ctx.Scope("f.UserKey")} JOIN bi.DimApp AS a ON a.AppKey = f.AppKey
WHERE f.DateKey BETWEEN @k0 AND @k1 GROUP BY a.App;", Ctx.Args());
        Panels.Add(new("People per app", Chart.Bars(apps.OrderByDescending(a => a.People).Select(a => new Bar(a.App, Ratio(a.People, t.Active), Note: $"{a.People:#,0} people")).ToList(), Fmt.Percent, "Share of active people per app", 1),
            "The share of active people who used Copilot in each app. Low shares for Excel or PowerPoint suggest where training would help.", "half"));
    }
}

public sealed class PeopleModel(PageServices s) : InsightsPage(s)
{
    public override string Title => "People";
    public override string Persona => "Enablement";
    public override string? RequiredRole => Roles.Enablement;
    public override string Intro => "Named people by engagement segment: power users to recruit as champions, and one-off or not-started people to invite to training.";

    static readonly Dictionary<string, string> Sorts = new()
    {
        ["days"] = "p.D DESC, p.I DESC", ["fewest"] = "p.D, du.DisplayName", ["name"] = "du.DisplayName",
        ["dept"] = "du.Department, du.DisplayName", ["manager"] = "du.ManagerName, du.DisplayName",
    };
    static readonly Dictionary<string, string> SortLabels = new()
    {
        ["days"] = "Most days", ["fewest"] = "Fewest days", ["name"] = "Name", ["dept"] = "Department", ["manager"] = "Manager",
    };

    sealed class Row
    {
        public string? DisplayName { get; set; }
        public string UserPrincipalName { get; set; } = "";
        public string? Department { get; set; }
        public string? JobTitle { get; set; }
        public string? ManagerName { get; set; }
        public string Segment { get; set; } = "";
        public int D { get; set; }
        public int I { get; set; }
        public int S { get; set; }
        public int MT { get; set; }
        public DateTime? Last { get; set; }
        public int TotalRows { get; set; }
    }

    protected override async Task BuildAsync()
    {
        var w = await Segments.WorkdaysAsync(Db, Ctx);
        var segParam = Request.Query["seg"].FirstOrDefault();
        var seg = Segments.Order.Contains(segParam) ? segParam : "Power";
        var pg = new Paging(Request, Sorts, seg is "Power" or "Regular" ? "days" : "dept");
        var rows = await Db.QueryAsync<Row>($@"
WITH {Segments.People(Ctx)}
SELECT du.DisplayName, du.UserPrincipalName, du.Department, du.JobTitle, du.ManagerName, p.Segment, p.D, p.I, p.S, p.MT,
       dl.[Date] AS Last, COUNT(*) OVER () AS TotalRows
FROM pop AS p JOIN bi.DimUser AS du ON du.UserKey = p.UserKey
LEFT JOIN bi.DimDate AS dl ON dl.DateKey = p.LastKey
WHERE p.Segment = @seg
  AND (@q IS NULL OR du.DisplayName LIKE @q OR du.UserPrincipalName LIKE @q OR du.Department LIKE @q)
{pg.OrderAndPage()};", Ctx.Args(new { w, seg, q = pg.Like }));
        pg.Total = rows.FirstOrDefault()?.TotalRows ?? 0;
        var t = new Table
        {
            Headers = ["Name", "Sign-in", "Department", "Job title", "Manager", "Days active", "Interactions", "Multi-turn sessions", "Last used"],
            Numeric = new HashSet<int> { 5, 6, 7 }, EmptyText = "Nobody in this segment.",
        };
        foreach (var r in rows)
            t.Rows.Add([r.DisplayName, r.UserPrincipalName, r.Department, r.JobTitle, r.ManagerName, r.D, r.I, Pct(Ratio(r.MT, r.S)), r.Last]);
        CsvTable = t;
        Kpis.Add(new(seg, Num(pg.Total), $"people, out of {w} working days in the period"));
        var tabs = string.Join(" · ", Segments.Order.Select(sg =>
            $"<a href=\"{System.Net.WebUtility.HtmlEncode(Ctx.Url(Request.Path, extra: "seg=" + Uri.EscapeDataString(sg)))}\"{(sg == seg ? " class=\"on\"" : "")}>{sg}</a>"));
        Panels.Add(new("People", new HtmlString($"<p class=\"sub\">Segment: {tabs}</p>" + pg.Controls(Request, SortLabels).Value + t.Render().Value),
            "Segments are defined on the Engagement page.", "full", CsvUrl()));
    }
}
