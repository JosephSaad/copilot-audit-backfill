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

    /// <summary>
    /// 2.6.7: the two segments Engagement's *Need a nudge* counts together, one-off and not started, listed as one on People
    /// by engagement so the figure and its list agree. Not a segment of its own: nobody is in it and in another.
    /// </summary>
    public const string Nudge = "Need a nudge";

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

    /// <summary>
    /// The working days of the period that the data covers (2.4.12): from its start, or the first day with data when that is
    /// later. The period's own weekdays were counted in bi.DimDate, which begins on 1 January of the first year with data,
    /// so a 365-day period over 130 working days of data counted 192, and the Power and Regular bars rose with it.
    /// </summary>
    public static async Task<int> WorkdaysAsync(Db db, ReportContext c) =>
        await db.ScalarAsync<int>(@"SELECT COUNT(*) FROM bi.DimDate WHERE DateKey BETWEEN @k0 AND @k1 AND IsWeekday = 1
  AND DateKey >= ISNULL((SELECT MIN(DateKey) FROM dash.UserDay), @k0);", c.Args());
}

/// <summary>
/// What "a session with a follow-up" counts, in the words the pages use (2.4.12): a session (a person's prompts with
/// less than an hour between them, on one day, in any app) with more than one prompt. The audit records say nothing of
/// which prompt answered which, so the pages no longer say the person replied to Copilot.
/// </summary>
public static class FollowUp
{
    public const string Kpi = "of sessions had more than one prompt: prompts less than an hour apart on one day, in any app";

    public const string Panel = "Sessions with a follow-up (more than one prompt, each less than an hour after the last, on one day, in any app), as a share of all sessions. " +
        "A rising line suggests people are going back to Copilot within a task instead of asking once. Two separate questions within the hour count too.";
}

public sealed class IndexModel(PageServices s, Metrics m) : InsightsPage(s)
{
    public override string Title => "Engagement";
    public override string Persona => "Enablement";
    public override string Intro => "How deeply people use Copilot: habit, conversation rather than one-off questions, and breadth across apps. Use it to aim training.";

    protected override async Task BuildAsync()
    {
        var w = await Segments.WorkdaysAsync(Db, Ctx);
        /* Everything after the working days is independent: started together, awaited where used (2.7.3). */
        var tTask = m.TotalsAsync(Ctx);
        var trTask = m.TrendAsync(Ctx);
        /* The segments, each department's and the people to nudge, from one pass over the population: it was built three
           times (2.7.3). People to nudge leave out the protected levels, as the list they open does. */
        var unitsTask = Db.QueryAsync<(string Unit, string Segment, int N, int Open)>($@"
WITH {Segments.People(Ctx)}
SELECT ISNULL(NULLIF(du.Department, N''), N'(no department)'), p.Segment, COUNT(*),
       COUNT(CASE WHEN pl.Upn IS NULL THEN 1 END)
FROM pop AS p JOIN bi.DimUser AS du ON du.UserKey = p.UserKey
{CopilotInsights.Data.ProtectedLevels.Join("pl", "du.UserPrincipalName")}   -- a join: a subquery cannot sit inside COUNT
GROUP BY ISNULL(NULLIF(du.Department, N''), N'(no department)'), p.Segment;", Ctx.Args(new { w }));
        var breadthTask = Db.QueryAsync<(int Apps, int N)>($@"
WITH u AS (SELECT f.UserKey, COUNT(DISTINCT f.AppKey) AS Apps FROM bi.FactInteraction AS f {Ctx.Scope("f.UserKey")}
           WHERE f.DateKey BETWEEN @k0 AND @k1 GROUP BY f.UserKey)
SELECT CASE WHEN Apps >= 5 THEN 5 ELSE Apps END, COUNT(*) FROM u GROUP BY CASE WHEN Apps >= 5 THEN 5 ELSE Apps END ORDER BY 1;", Ctx.Args());
        var appsTask = Db.QueryAsync<(string App, int People)>($@"
SELECT COALESCE(vn.Name, a.App), COUNT(DISTINCT f.UserKey) FROM bi.FactInteraction AS f {Ctx.Scope("f.UserKey")} JOIN bi.DimApp AS a ON a.AppKey = f.AppKey
LEFT JOIN dash.ValueName AS vn ON vn.ColumnId = 2 AND vn.Value = a.App
WHERE f.DateKey BETWEEN @k0 AND @k1 GROUP BY a.App, vn.Name;", Ctx.Args());
        await Task.WhenAll(tTask, trTask, unitsTask, breadthTask, appsTask);
        var units = await unitsTask;
        int Seg(string name) => units.Where(x => x.Segment == name).Sum(x => x.N);
        var t = await tTask;
        var tr = await trTask;

        Kpis.Add(new("Power users", Num(Seg("Power")), $"used it on half or more of the {w} working days", Href: Open("/enablement/people", extra: "seg=Power")));
        /* What is counted, not what it may mean (2.4.12): it said "the person replied to Copilot", but a session is a
           person's prompts less than an hour apart on one day, in any app, so two separate questions count too. */
        Kpis.Add(new("Sessions with a follow-up", Pct(Ratio(t.MultiTurn, t.Sessions)), FollowUp.Kpi));
        Kpis.Add(new("Prompts per session", t.Sessions > 0 ? (t.Prompts / (double)t.Sessions).ToString("0.0") : "—", "a session: prompts less than an hour apart, on one day"));
        var nudge = units.Where(x => x.Segment is "One-off" or "Not started").Sum(x => x.Open);
        Kpis.Add(new(Segments.Nudge, Num(nudge), "licensed or active, but one day or none", Href: Open("/enablement/people", extra: "seg=" + Uri.EscapeDataString(Segments.Nudge))));

        Panels.Add(new("Engagement segments",
            Chart.Bars(Segments.Order.Select((name, i) => new Bar(name, Seg(name), Viewer.Has(Roles.Enablement) ? Ctx.Url("/enablement/people", extra: "seg=" + name) : null, Slot: Segments.Slots[i])).ToList(), Fmt.Count, "People by segment"),
            $"Everyone licensed today or active in the period, by the days they used Copilot out of {w} working days. " +
            "Power: half or more. Regular: a fifth or more. Occasional: two days or more. One-off: one day.", "half"));

        var depts = units.GroupBy(u => u.Unit).Select(g => (Dept: g.Key, People: g.Sum(x => x.N))).ToList();
        var big = SmallGroups.Shown(depts, d => d.People, Ctx.MinGroupSize).OrderByDescending(d => d.People).Take(12).Select(d => d.Dept).ToList();
        var tbl = new Table { Headers = ["Department", "People", .. Segments.Order], Numeric = new HashSet<int> { 1, 2, 3, 4, 5, 6 } };
        foreach (var d in big)
        {
            var n = units.Where(u => u.Unit == d).Sum(u => u.N);
            tbl.Rows.Add([d, n, .. Segments.Order.Select(sg => (object?)Pct(Ratio(units.Where(u => u.Unit == d && u.Segment == sg).Sum(u => u.N), n)))]);
        }
        CsvTable = tbl;
        Panels.Add(new("Segments by department", tbl.Render(), $"The twelve largest departments, as a share of each one's people. Departments under {Ctx.MinGroupSize} are left out.", "half", CsvUrl()));

        Panels.Add(new("Conversations, not one-off questions",
            Chart.Line(Labels(), [new Series("With a follow-up", tr.Select(x => Ratio(x.MultiTurn, x.Sessions)).ToList(), 1)], Fmt.Percent, "Share of sessions with a follow-up"),
            FollowUp.Panel, "half"));
        Panels.Add(new("Prompts per session",
            Chart.Line(Labels(), [new Series("Prompts per session", tr.Select(x => x.Sessions > 0 ? x.Prompts / (double?)x.Sessions : null).ToList(), 2)], Fmt.Decimal1, "Prompts per session"),
            TrendNote, "half"));

        var breadth = await breadthTask;
        Panels.Add(new("Breadth: apps used per person",
            Chart.Bars(breadth.Select(b => new Bar(b.Apps == 5 ? "5 or more apps" : b.Apps == 1 ? "1 app" : $"{b.Apps} apps", b.N)).ToList(), Fmt.Count, "Apps per active person"),
            "Active people by how many Microsoft 365 apps they used Copilot in. One app often means Copilot Chat only.", "half"));

        var apps = await appsTask;
        Panels.Add(new("People per app", Chart.Bars(apps.OrderByDescending(a => a.People).Select(a => new Bar(a.App, Ratio(a.People, t.Active), Note: $"{a.People:#,0} people")).ToList(), Fmt.Percent, "Share of active people per app", 1),
            "The share of active people who used Copilot in each app. Low shares for Excel or PowerPoint suggest where training would help.", "half"));
    }
}

public sealed class PeopleModel(PageServices s) : InsightsPage(s)
{
    public override string Title => "People by engagement";
    public override string Persona => "Enablement";
    public override string? RequiredRole => Roles.Enablement;
    public override string Intro => "Named people by engagement segment: power users to recruit as champions, and one-off or not-started people to invite to training.";

    static readonly Dictionary<string, string> Sorts = new()
    {
        ["days"] = "p.D DESC, p.I DESC", ["fewest"] = "p.D, du.DisplayName", ["name"] = "du.DisplayName",
        ["dept"] = "du.Department, du.DisplayName", ["manager"] = "du.ManagerName, du.DisplayName",
        ["upn"] = "du.UserPrincipalName", ["title"] = "du.JobTitle, du.DisplayName, du.UserPrincipalName", ["inter"] = "p.I DESC, du.DisplayName, du.UserPrincipalName",
        ["mt"] = "CASE WHEN p.S > 0 THEN 1.0 * p.MT / p.S END DESC, du.DisplayName, du.UserPrincipalName", ["recent"] = "p.LastKey DESC, du.DisplayName, du.UserPrincipalName",
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
        var seg = segParam is not null && (Segments.Order.Contains(segParam) || segParam == Segments.Nudge) ? segParam : "Power";
        var nudged = seg is "One-off" or "Not started" or Segments.Nudge;   // the segments the protected levels are kept out of
        var pg = new Paging(Request, Sorts, seg is "Power" or "Regular" ? "days" : "dept");
        var rows = await Db.QueryAsync<Row>($@"
WITH {Segments.People(Ctx)}
SELECT du.DisplayName, du.UserPrincipalName, du.Department, du.JobTitle, du.ManagerName, p.Segment, p.D, p.I, p.S, p.MT,
       dl.[Date] AS Last, COUNT(*) OVER () AS TotalRows
FROM pop AS p JOIN bi.DimUser AS du ON du.UserKey = p.UserKey
LEFT JOIN bi.DimDate AS dl ON dl.DateKey = p.LastKey
WHERE (p.Segment = @seg OR (@seg = @nudge AND p.Segment IN (N'One-off', N'Not started')))
  AND (@nudged = 0 OR {CopilotInsights.Data.ProtectedLevels.Not("du.UserPrincipalName")})
  AND (@q IS NULL OR du.DisplayName LIKE @q OR du.UserPrincipalName LIKE @q OR du.Department LIKE @q)
{pg.OrderAndPage()};", Ctx.Args(new { w, seg, q = pg.Like, nudge = Segments.Nudge, nudged = nudged ? 1 : 0 }));
        pg.Total = rows.FirstOrDefault()?.TotalRows ?? 0;
        var t = new Table
        {
            Headers = ["Name", "Sign-in", "Department", "Job title", "Manager", "Days active", "Interactions", "Sessions with a follow-up", "Last used"],
            Numeric = new HashSet<int> { 5, 6, 7 }, EmptyText = "Nobody in this segment.",
            Server = new(pg, Request, ["name", "upn", "dept", "title", "manager", "days", "inter", "mt", "recent"], new HashSet<int> { 5, 6, 7, 8 }),
        };
        foreach (var r in rows)
            t.Rows.Add([r.DisplayName, r.UserPrincipalName, r.Department, r.JobTitle, r.ManagerName, r.D, r.I, Pct(Ratio(r.MT, r.S)), r.Last]);
        CsvTable = t;
        Kpis.Add(new(seg, Num(pg.Total), $"people, out of {w} working days in the period"));
        var tabs = string.Join(" · ", Segments.Order.Append(Segments.Nudge).Select(sg =>
            $"<a href=\"{System.Net.WebUtility.HtmlEncode(Ctx.Url(Request.Path, extra: "seg=" + Uri.EscapeDataString(sg)))}\"{(sg == seg ? " class=\"on\"" : "")}>{sg}</a>"));
        Panels.Add(new("People", new HtmlString($"<p class=\"sub\">Segment: {tabs}</p>" + (nudged ? await ProtectedNoteAsync() : "") + pg.Controls(Request, SortLabels).Value + t.Render().Value),
            "Segments are defined on the Engagement page. Need a nudge: one-off and not started together, as Engagement counts them.", "full", CsvUrl()));
    }
}
