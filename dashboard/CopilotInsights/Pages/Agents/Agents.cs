using CopilotInsights.Charts;
using CopilotInsights.Data;
using CopilotInsights.Infrastructure;
using Microsoft.AspNetCore.Html;

namespace CopilotInsights.Pages.Agents;

public sealed class IndexModel(PageServices s) : InsightsPage(s)
{
    public override string Title => "Agents in use";
    public override string Persona => "Agents";
    public override string Intro => "Every agent people used in the period: how many people, how often, whether they come back, and which agents are new.";

    sealed class Row
    {
        public int AgentKey { get; set; }
        public string AgentName { get; set; } = "";
        public string? AgentSource { get; set; }
        public string? Owner { get; set; }
        public DateTime FirstSeenUtc { get; set; }
        public long N { get; set; }
        public int People { get; set; }
        public int Returning { get; set; }
    }

    protected override async Task BuildAsync()
    {
        var rows = await Db.QueryAsync<Row>($@"
WITH u AS (SELECT f.AgentKey, f.UserKey, COUNT_BIG(*) AS N, COUNT(DISTINCT f.DateKey) AS Days
           FROM bi.FactInteraction AS f {Ctx.Scope("f.UserKey")}
           WHERE f.DateKey BETWEEN @k0 AND @k1 GROUP BY f.AgentKey, f.UserKey)
SELECT ag.AgentKey, ag.AgentName, ag.AgentSource, ag.Owner, ag.FirstSeenUtc, SUM(u.N) AS N, COUNT(*) AS People,
       COUNT(CASE WHEN u.Days > 1 THEN 1 END) AS Returning
FROM u JOIN dash.Agent AS ag ON ag.AgentKey = u.AgentKey
WHERE ag.AgentSource <> N'(no agent)'
GROUP BY ag.AgentKey, ag.AgentName, ag.AgentSource, ag.Owner, ag.FirstSeenUtc;", Ctx.Args());
        /* From the lead-in a 28-day average needs (2.5.0). */
        var trend = await Db.QueryAsync<(int AgentKey, DateTime Bucket, int People)>($@"
SELECT f.AgentKey, {Ctx.BucketSql("d")}, COUNT(DISTINCT f.UserKey)
FROM bi.FactInteraction AS f {Ctx.Scope("f.UserKey")}
JOIN bi.DimDate AS d ON d.DateKey = f.DateKey
JOIN bi.DimAgent AS a ON a.AgentKey = f.AgentKey AND a.AgentSource <> N'(no agent)'
WHERE f.DateKey BETWEEN @kt0 AND @k1
GROUP BY f.AgentKey, {Ctx.BucketSql("d")};", Ctx.Args());
        var peopleAt = trend.ToLookup(t => (t.AgentKey, t.Bucket), t => t.People);
        var isNew = (Row r) => r.FirstSeenUtc.Date >= Ctx.Start;

        Kpis.Add(new("Agents used", Num(rows.Count), $"{rows.Count(r => r.AgentSource == "Copilot Studio"):#,0} built in Copilot Studio"));
        Kpis.Add(new("New in the period", Num(rows.Count(isNew)), "first used in the period"));
        Kpis.Add(new("People using agents", Num(await Db.ScalarAsync<int>($@"
SELECT COUNT(DISTINCT ud.UserKey) FROM dash.UserDay AS ud {Ctx.Scope("ud.UserKey")}
WHERE ud.DateKey BETWEEN @k0 AND @k1 AND ud.AgentInteractions > 0;", Ctx.Args()))));
        Kpis.Add(new("Interactions through agents", Num(rows.Sum(r => r.N))));

        /* People per agent are a group at every point too (2.4.12): agents with fewer than the minimum in the period are
           left out, and a point under it is a gap, as the table's counts are. A point is the 28-day average (2.5.0), under
           the same rule: an average under the minimum is a gap (see TrendAverage.PeopleAtMin). */
        List<double?> Points(int agentKey) => Average(b => peopleAt[(agentKey, b.Key)].Sum()).Select(PeopleAtMin).ToList();
        var top = rows.Where(r => AtLeastMin(r.People) is not null).OrderByDescending(r => r.People).Take(6).ToList();
        Panels.Add(new("People using the top agents",
            Chart.Line(Labels(), top.Select((r, i) => new Series(r.AgentName, Points(r.AgentKey), i + 1)).ToList(), Fmt.Average, "People per agent, 28-day average"),
            $"The six agents with the most people in the period. A point where fewer than {Ctx.MinGroupSize} people used an agent {PerPoint}, on average, is left out. {TrendNote}"));

        var t = new Table
        {
            Headers = ["Agent", "Built in", "Owner", "First used", "People", "Came back", "Interactions", "Interactions per person", "Trend (people, 28-day average)"],
            Numeric = new HashSet<int> { 4, 5, 6, 7 }, EmptyText = "No agents were used in the period.",
        };
        foreach (var r in rows.OrderByDescending(r => r.People))
            t.Rows.Add([
                new Link(r.AgentName + (isNew(r) ? " (new)" : ""), Ctx.Url("/agents/detail", extra: $"agent={r.AgentKey}")), r.AgentSource, r.Owner, r.FirstSeenUtc.Date,
                AtLeastMin(r.People), AtLeastMin(r.People) is null ? null : Pct(Ratio(r.Returning, r.People)), r.N,
                AtLeastMin(r.People) is null ? null : Math.Round(r.N / (double)Math.Max(1, r.People), 1),
                AtLeastMin(r.People) is null ? null : Chart.Sparkline(Points(r.AgentKey), r.AgentName),
            ]);
        Panels.Add(new("All agents", t.Render(),
            $"Came back: the share of its people who used it on more than one day. People are shown only where {Ctx.MinGroupSize} or more used an agent. Trend: the average number of people {PerPoint}, over the 28 days up to each point; a point under {Ctx.MinGroupSize} is left out. Names and owners come from Admin › Reference data. Select an agent for its detail."));
    }
}

public sealed class DetailModel(PageServices s) : InsightsPage(s)
{
    public override string Title => AgentName ?? "Agent detail";
    public override string Persona => "Agents";
    public override string Intro => "One agent: who uses it, where, in which apps and how that is changing.";
    string? AgentName { get; set; }

    static readonly Dictionary<string, string> Sorts = new()
    {
        ["most"] = "x.N DESC", ["recent"] = "x.LastKey DESC", ["name"] = "du.DisplayName", ["dept"] = "du.Department, du.DisplayName",
        ["upn"] = "du.UserPrincipalName", ["days"] = "x.Days DESC, du.DisplayName, du.UserPrincipalName",
    };
    static readonly Dictionary<string, string> SortLabels = new() { ["most"] = "Most interactions", ["recent"] = "Most recent", ["name"] = "Name", ["dept"] = "Department" };

    sealed class Row
    {
        public string? DisplayName { get; set; }
        public string UserPrincipalName { get; set; } = "";
        public string? Department { get; set; }
        public long N { get; set; }
        public int Days { get; set; }
        public DateTime Last { get; set; }
        public int TotalRows { get; set; }
    }

    protected override async Task BuildAsync()
    {
        var agents = await Db.QueryAsync<(int AgentKey, string AgentName, string? AgentSource)>(
            "SELECT AgentKey, AgentName, AgentSource FROM dash.Agent WHERE AgentSource <> N'(no agent)' ORDER BY AgentName;");
        var key = int.TryParse(Request.Query["agent"], out var k) && agents.Any(a => a.AgentKey == k) ? k : (int?)null;
        var options = string.Concat(agents.Select(a =>
            $"<option value=\"{System.Net.WebUtility.HtmlEncode(Ctx.Url(Request.Path, extra: $"agent={a.AgentKey}"))}\"{(a.AgentKey == key ? " selected" : "")}>{System.Net.WebUtility.HtmlEncode(a.AgentName)} ({System.Net.WebUtility.HtmlEncode(a.AgentSource)})</option>"));
        /* With none chosen the box is the page: it says what to do, and where else an agent can be picked (2.4.10: the
           hint sat above it and said "above"). */
        Panels.Add(new(key is null ? "Choose an agent" : "Agent", new HtmlString(
            $"<select class=\"pick\" aria-label=\"Agent\" data-navigate><option value=\"\">Choose an agent</option>{options}</select>" +
            (key is null ? $"<p class=\"sub\">Or select an agent's name on <a href=\"{System.Net.WebUtility.HtmlEncode(Ctx.Url("/agents"))}\">Agents in use</a>.</p>" : ""))));
        if (key is null) return;
        AgentName = agents.First(a => a.AgentKey == key).AgentName;
        var args = Ctx.Args(new { agent = key });

        /* From the lead-in a 28-day average needs (2.5.0). */
        var tr = await Db.QueryAsync<(DateTime Bucket, int People, long N)>($@"
SELECT {Ctx.BucketSql("d")}, COUNT(DISTINCT f.UserKey), COUNT_BIG(*)
FROM bi.FactInteraction AS f {Ctx.Scope("f.UserKey")}
JOIN bi.DimDate AS d ON d.DateKey = f.DateKey
WHERE f.AgentKey = @agent AND f.DateKey BETWEEN @kt0 AND @k1
GROUP BY {Ctx.BucketSql("d")};", args);
        var trAt = tr.ToLookup(x => x.Bucket);
        var tot = (await Db.QueryAsync<(int People, long N, int Returning, int NewPeople)>($@"
WITH u AS (SELECT f.UserKey, COUNT_BIG(*) AS N, COUNT(DISTINCT f.DateKey) AS Days, MIN(f.DateKey) AS FirstKey
           FROM bi.FactInteraction AS f {Ctx.Scope("f.UserKey")} WHERE f.AgentKey = @agent AND f.DateKey <= @k1 GROUP BY f.UserKey),
p AS (SELECT f.UserKey, COUNT_BIG(*) AS N, COUNT(DISTINCT f.DateKey) AS Days
      FROM bi.FactInteraction AS f {Ctx.Scope("f.UserKey")} WHERE f.AgentKey = @agent AND f.DateKey BETWEEN @k0 AND @k1 GROUP BY f.UserKey)
SELECT (SELECT COUNT(*) FROM p), (SELECT ISNULL(SUM(N), 0) FROM p), (SELECT COUNT(*) FROM p WHERE Days > 1),
       (SELECT COUNT(*) FROM u WHERE FirstKey >= @k0);", args)).Single();
        /* Its people are a group: counted only at the minimum group size or more (2.4.12). */
        var few = tot.People > 0 && AtLeastMin(tot.People) is null;
        Kpis.Add(new("People", few ? "—" : Num(tot.People), few ? $"fewer than {Ctx.MinGroupSize} used it in the period" : "used it in the period"));
        Kpis.Add(new("New people", few ? "—" : Num(tot.NewPeople), "first used it in the period"));
        Kpis.Add(new("Came back", few ? "—" : Pct(Ratio(tot.Returning, tot.People)), "used it on more than one day"));
        Kpis.Add(new("Interactions", Num(tot.N), tot.People > 0 && !few ? $"{tot.N / (double)tot.People:0.0} per person" : null));

        /* Each point at the minimum group size or more (2.4.12), as the KPI above is: the 28-day average of people (2.5.0) under the same rule. */
        Panels.Add(new("People using it", Chart.Line(Labels(), [new Series("People", Average(b => trAt[b.Key].Sum(x => x.People)).Select(PeopleAtMin).ToList(), 1)], Fmt.Average, "People using the agent, 28-day average"),
            $"A point where fewer than {Ctx.MinGroupSize} people used it {PerPoint}, on average, is left out. {TrendNote}", "half"));
        Panels.Add(new("Interactions", Chart.Columns(Labels(), [new Series("Interactions", Average(b => trAt[b.Key].Sum(x => x.N)), 1)], Fmt.Average, "Agent interactions, 28-day average"),
            TrendNote, "half"));

        var depts = await Db.QueryAsync<(string Dept, int People, long N)>($@"
SELECT ISNULL(NULLIF(du.Department, N''), N'(no department)'), COUNT(DISTINCT f.UserKey), COUNT_BIG(*)
FROM bi.FactInteraction AS f {Ctx.Scope("f.UserKey")}
JOIN bi.DimUser AS du ON du.UserKey = f.UserKey
WHERE f.AgentKey = @agent AND f.DateKey BETWEEN @k0 AND @k1
GROUP BY ISNULL(NULLIF(du.Department, N''), N'(no department)');", args);
        /* The combined row is left out when even it is under the minimum (2.4.12): with every department small, it was
           everyone who used the agent, one or two people. */
        var shownDepts = FoldSmall(depts, d => d.People, small => ("Smaller departments", small.Sum(x => x.People), small.Sum(x => x.N)), out _)
            .Where(d => d.People >= Ctx.MinGroupSize).ToList();
        Panels.Add(new("By department", Chart.Bars(shownDepts.OrderByDescending(d => d.People).Take(15).Select(d => new Bar(d.Dept, d.People, Note: $"{d.N:#,0} interactions")).ToList(), Fmt.Count, "People by department"),
            $"People who used it. Departments where fewer than {Ctx.MinGroupSize} people used it are combined.", "half"));
        var apps = await Db.QueryAsync<(string App, long N)>($@"
SELECT COALESCE(vn.Name, a.App), COUNT_BIG(*) FROM bi.FactInteraction AS f {Ctx.Scope("f.UserKey")} JOIN bi.DimApp AS a ON a.AppKey = f.AppKey
LEFT JOIN dash.ValueName AS vn ON vn.ColumnId = 2 AND vn.Value = a.App
WHERE f.AgentKey = @agent AND f.DateKey BETWEEN @k0 AND @k1 GROUP BY a.App, vn.Name;", args);
        Panels.Add(new("Where it is used", Chart.Bars(apps.OrderByDescending(a => a.N).Select(a => new Bar(a.App, a.N)).ToList(), Fmt.Count, "Agent interactions by app"), null, "half"));

        if (!Viewer.Has(Roles.Agents) || PrivacyMode) return;   // the named users are for the Agents role only, and never in privacy mode
        var pg = new Paging(Request, Sorts, "most");
        var rows = await Db.QueryAsync<Row>($@"
WITH x AS (SELECT f.UserKey, COUNT_BIG(*) AS N, COUNT(DISTINCT f.DateKey) AS Days, MAX(f.DateKey) AS LastKey
           FROM bi.FactInteraction AS f {Ctx.Scope("f.UserKey")} WHERE f.AgentKey = @agent AND f.DateKey BETWEEN @k0 AND @k1 GROUP BY f.UserKey)
SELECT du.DisplayName, du.UserPrincipalName, du.Department, x.N, x.Days, dl.[Date] AS Last, COUNT(*) OVER () AS TotalRows
FROM x JOIN bi.DimUser AS du ON du.UserKey = x.UserKey JOIN bi.DimDate AS dl ON dl.DateKey = x.LastKey
WHERE (@q IS NULL OR du.DisplayName LIKE @q OR du.UserPrincipalName LIKE @q OR du.Department LIKE @q)
{pg.OrderAndPage()};", Ctx.Args(new { agent = key, q = pg.Like }));
        pg.Total = rows.FirstOrDefault()?.TotalRows ?? 0;
        var t = new Table { Headers = ["Name", "Sign-in", "Department", "Interactions", "Days", "Last used"], Numeric = new HashSet<int> { 3, 4 },
            Server = new(pg, Request, ["name", "upn", "dept", "most", "days", "recent"], new HashSet<int> { 3, 4, 5 }) };
        foreach (var r in rows) t.Rows.Add([r.DisplayName, r.UserPrincipalName, r.Department, r.N, r.Days, r.Last]);
        CsvTable = t;
        Panels.Add(new("Users", new HtmlString(pg.Controls(Request, SortLabels).Value + t.Render().Value), null, "full", CsvUrl()));
    }
}
