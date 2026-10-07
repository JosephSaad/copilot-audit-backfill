using System.Net;
using CopilotInsights.Charts;
using CopilotInsights.Data;
using Microsoft.AspNetCore.Html;

namespace CopilotInsights.Pages.Apps;

public sealed class IndexModel(PageServices s) : InsightsPage(s)
{
    public override string Title => "Apps";
    public override string Persona => "App owners";
    public override string Intro => "Copilot in each Microsoft 365 app: how many people, how deeply, and whether they come back. Select an app for its detail.";

    sealed class Row
    {
        public int AppKey { get; set; }
        public string App { get; set; } = "";
        public string AppCategory { get; set; } = "";
        public int People { get; set; }
        public int Returning { get; set; }
        public long N { get; set; }
        public long Prompts { get; set; }
        public long Resources { get; set; }
    }

    protected override async Task BuildAsync()
    {
        /* Days counted as rows of a per-day grouping, and names joined rather than looked up by dash.fnValueName,
           which kept the whole statement to one thread (2.4.2): 7 seconds to 3 for a year of a whole organization. */
        var rows = await Db.QueryAsync<Row>($@"
WITH d AS (SELECT f.AppKey, f.UserKey, f.DateKey, COUNT_BIG(*) AS N, SUM(CAST(f.PromptCount AS BIGINT)) AS P, SUM(CAST(f.AccessedResourceCount AS BIGINT)) AS R
           FROM bi.FactInteraction AS f {Ctx.Scope("f.UserKey")}
           WHERE f.DateKey BETWEEN @k0 AND @k1 GROUP BY f.AppKey, f.UserKey, f.DateKey),
u AS (SELECT AppKey, UserKey, SUM(N) AS N, SUM(P) AS P, SUM(R) AS R, COUNT(*) AS Days FROM d GROUP BY AppKey, UserKey)
SELECT a.AppKey, COALESCE(vn.Name, a.App) AS App, a.AppCategory, COUNT(*) AS People, COUNT(CASE WHEN u.Days > 1 THEN 1 END) AS Returning,
       SUM(u.N) AS N, SUM(u.P) AS Prompts, SUM(u.R) AS Resources
FROM u JOIN bi.DimApp AS a ON a.AppKey = u.AppKey
LEFT JOIN dash.ValueName AS vn ON vn.ColumnId = 2 AND vn.Value = a.App
GROUP BY a.AppKey, a.App, vn.Name, a.AppCategory;", Ctx.Args());
        var trend = await Db.QueryAsync<(int AppKey, DateTime Bucket, int People)>($@"
SELECT f.AppKey, {Ctx.BucketSql("d")}, COUNT(DISTINCT f.UserKey)
FROM bi.FactInteraction AS f {Ctx.Scope("f.UserKey")} JOIN bi.DimDate AS d ON d.DateKey = f.DateKey
WHERE f.DateKey BETWEEN @k0 AND @k1 GROUP BY f.AppKey, {Ctx.BucketSql("d")};", Ctx.Args());
        var buckets = Buckets();
        var axis = Labels();
        var people = await Db.ScalarAsync<int>($"SELECT COUNT(DISTINCT ud.UserKey) FROM dash.UserDay AS ud {Ctx.Scope("ud.UserKey")} WHERE ud.DateKey BETWEEN @k0 AND @k1 AND ud.Interactions > 0;", Ctx.Args());
        Kpis.Add(new("Apps in use", Num(rows.Count)));
        var widest = rows.OrderByDescending(r => r.People).FirstOrDefault();
        if (widest is not null) Kpis.Add(new("Widest reach", widest.App, $"{Pct(Ratio(widest.People, people))} of active people"));
        var stickiest = rows.Where(r => r.People >= Ctx.MinGroupSize).OrderByDescending(r => r.Returning / (double)r.People).FirstOrDefault();
        if (stickiest is not null) Kpis.Add(new("Most return visits", stickiest.App, $"{Pct(Ratio(stickiest.Returning, stickiest.People))} of its people came back"));

        Panels.Add(new("Reach: share of active people using each app",
            Chart.Bars(rows.OrderByDescending(r => r.People).Select(r => new Bar(r.App, Ratio(r.People, people), Ctx.Url("/apps/detail", extra: $"app={r.AppKey}"), $"{r.People:#,0} people")).ToList(), Fmt.Percent, "Reach by app", 1),
            "Select an app for its detail.", "half"));
        Panels.Add(new("Depth: prompts per person",
            Chart.Bars(rows.Where(r => r.People >= Ctx.MinGroupSize).OrderByDescending(r => r.Prompts / (double)r.People).Select(r => new Bar(r.App, r.Prompts / (double)r.People, Note: $"{r.Prompts:#,0} prompts")).ToList(), Fmt.Decimal1, "Prompts per person by app"),
            "Apps with fewer people than the minimum group size are left out.", "half"));
        var t = new Table
        {
            Headers = ["App", "Category", "People", "Came back", "Interactions", "Prompts per person", "Resources read per interaction", "Trend (people)"],
            Numeric = new HashSet<int> { 2, 3, 4, 5, 6 },
        };
        foreach (var r in rows.OrderByDescending(r => r.People))
            t.Rows.Add([new Link(r.App, Ctx.Url("/apps/detail", extra: $"app={r.AppKey}")), r.AppCategory, r.People, Pct(Ratio(r.Returning, r.People)), r.N,
                        Math.Round(r.Prompts / (double)Math.Max(1, r.People), 1), Math.Round(r.Resources / (double)Math.Max(1, r.N), 2),
                        Chart.Sparkline(axis, buckets.Select(b => (double?)trend.Where(x => x.AppKey == r.AppKey && x.Bucket == b.Key).Sum(x => x.People)).ToList(), r.App)]);
        Panels.Add(new("Every app", WithTrendNote(t.Render()), "Came back: people who used Copilot in the app on more than one day."));
    }
}

public sealed class DetailModel(PageServices s) : InsightsPage(s)
{
    public override string Title => AppName ?? "App detail";
    public override string Persona => "App owners";
    public override string Intro => "One app: who uses Copilot in it, what they read with it, which agents run there, and how it is changing.";
    string? AppName { get; set; }

    protected override async Task BuildAsync()
    {
        var apps = await Db.QueryAsync<(int AppKey, string App)>("SELECT AppKey, App FROM bi.DimApp ORDER BY App;");
        var key = int.TryParse(Request.Query["app"], out var k) && apps.Any(a => a.AppKey == k) ? k : (int?)null;
        var options = string.Concat(apps.Select(a =>
            $"<option value=\"{WebUtility.HtmlEncode(Ctx.Url(Request.Path, extra: $"app={a.AppKey}"))}\"{(a.AppKey == key ? " selected" : "")}>{WebUtility.HtmlEncode(a.App)}</option>"));
        Panels.Add(new(key is null ? "Choose an app" : "App", new HtmlString(
            $"<select class=\"pick\" aria-label=\"App\" data-navigate><option value=\"\">Choose an app</option>{options}</select>" +
            (key is null ? $"<p class=\"sub\">Or select an app's name on <a href=\"{WebUtility.HtmlEncode(Ctx.Url("/apps"))}\">Apps</a>.</p>" : ""))));
        if (key is null) return;
        AppName = apps.First(a => a.AppKey == key).App;
        var args = Ctx.Args(new { app = key });

        var tr = await Db.QueryAsync<(DateTime Bucket, int People, long N, long Web)>($@"
SELECT {Ctx.BucketSql("d")}, COUNT(DISTINCT f.UserKey), COUNT_BIG(*), SUM(CAST(f.UsedWebSearch AS INT))
FROM bi.FactInteraction AS f {Ctx.Scope("f.UserKey")} JOIN bi.DimDate AS d ON d.DateKey = f.DateKey
WHERE f.AppKey = @app AND f.DateKey BETWEEN @k0 AND @k1 GROUP BY {Ctx.BucketSql("d")};", args);
        var buckets = Buckets();
        long n = tr.Sum(x => x.N);
        var people = await Db.ScalarAsync<int>($"SELECT COUNT(DISTINCT f.UserKey) FROM bi.FactInteraction AS f {Ctx.Scope("f.UserKey")} WHERE f.AppKey = @app AND f.DateKey BETWEEN @k0 AND @k1;", args);
        Kpis.Add(new("People who used it", Num(people)));
        Kpis.Add(new("Interactions", Num(n), people > 0 ? $"{n / (double)people:0.0} per person" : null));
        Kpis.Add(new("Searched the web", Pct(Ratio(tr.Sum(x => x.Web), n))));
        Panels.Add(new("People using Copilot in it", Chart.Line(Labels(), [new Series("People", buckets.Select(b => (double?)tr.Where(x => x.Bucket == b.Key).Sum(x => x.People)).ToList(), 1)], Fmt.Count, "People by period"), TrendNote, "half"));

        var types = await Db.QueryAsync<(string Type, long N)>($@"
SELECT COALESCE(vn.Name, t.ResourceType), COUNT_BIG(*) FROM bi.FactAccessedResource AS f {Ctx.Scope("f.UserKey")}
JOIN bi.DimResourceType AS t ON t.ResourceTypeKey = f.ResourceTypeKey
LEFT JOIN dash.ValueName AS vn ON vn.ColumnId = 31 AND vn.Value = t.ResourceType
WHERE f.AppKey = @app AND f.DateKey BETWEEN @k0 AND @k1 GROUP BY t.ResourceType, vn.Name;", args);
        Panels.Add(new("What Copilot read in it", Chart.Bars(types.OrderByDescending(t => t.N).Take(10).Select(t => new Bar(t.Type, t.N)).ToList(), Fmt.Count, "Resources by type"), null, "half"));

        var depts = await Db.QueryAsync<(string Dept, int People)>($@"
SELECT ISNULL(NULLIF(du.Department, N''), N'(no department)'), COUNT(DISTINCT f.UserKey)
FROM bi.FactInteraction AS f {Ctx.Scope("f.UserKey")} JOIN bi.DimUser AS du ON du.UserKey = f.UserKey
WHERE f.AppKey = @app AND f.DateKey BETWEEN @k0 AND @k1 GROUP BY ISNULL(NULLIF(du.Department, N''), N'(no department)');", args);
        var shown = FoldSmall(depts, d => d.People, small => ("Smaller departments", small.Sum(x => x.People)), out _);
        Panels.Add(new("By department", Chart.Bars(shown.OrderByDescending(d => d.People).Take(15).Select(d => new Bar(d.Dept, d.People)).ToList(), Fmt.Count, "People by department"),
            $"Departments with fewer than {Ctx.MinGroupSize} people using it are combined.", "half"));
        var agents = await Db.QueryAsync<(string Agent, long N)>($@"
SELECT ag.AgentName, COUNT_BIG(*) FROM bi.FactInteraction AS f {Ctx.Scope("f.UserKey")} JOIN dash.Agent AS ag ON ag.AgentKey = f.AgentKey
WHERE f.AppKey = @app AND f.DateKey BETWEEN @k0 AND @k1 AND ag.AgentSource <> N'(no agent)' GROUP BY ag.AgentName;", args);
        Panels.Add(new("Agents used in it", Chart.Bars(agents.OrderByDescending(a => a.N).Take(10).Select(a => new Bar(a.Agent, a.N)).ToList(), Fmt.Count, "Agents in the app"),
            agents.Count == 0 ? "No agents ran in this app in the period." : null, "half"));
    }
}
