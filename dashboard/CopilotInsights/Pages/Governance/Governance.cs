using CopilotInsights.Charts;
using CopilotInsights.Data;

namespace CopilotInsights.Pages.Governance;

public sealed class IndexModel(PageServices s, Metrics m) : InsightsPage(s)
{
    public override string Title => "Models and web search";
    public override string Persona => "Governance";
    public override string Intro => "Which models answer, how often Copilot searches the public web, Copilot Chat against Copilot in the apps, and how many agents are in play.";

    protected override async Task BuildAsync()
    {
        /* The KPIs are the period's own figures; the charts' points are 28-day averages (2.5.0), read from the lead-in they need. */
        var sums = await m.SumsAsync(Ctx);
        var tr = await m.TrendSeriesAsync(Ctx);
        long all = sums.Interactions;
        /* Counted per model key first, then named (2.4.2): dash.fnValueName in the GROUP BY over the fact ran once a row. */
        const string modelName = "CASE WHEN mo.ModelName = N'' THEN N'(not recorded)' ELSE mo.ModelProvider + N' ' + COALESCE(vn.Name, mo.ModelName) END";
        var byModel = await Db.QueryAsync<(string Model, long N)>($@"
WITH g AS (
    SELECT f.ModelKey, COUNT_BIG(*) AS N
    FROM bi.FactInteraction AS f {Ctx.Scope("f.UserKey")}
    WHERE f.DateKey BETWEEN @k0 AND @k1
    GROUP BY f.ModelKey)
SELECT {modelName}, SUM(g.N)
FROM g JOIN bi.DimModel AS mo ON mo.ModelKey = g.ModelKey
LEFT JOIN dash.ValueName AS vn ON vn.ColumnId = 16 AND vn.Value = mo.ModelName
GROUP BY {modelName};", Ctx.Args());
        var models = await Db.QueryAsync<(DateTime Bucket, string Model, long N)>($@"
WITH g AS (
    SELECT {Ctx.BucketSql("d")} AS Bucket, f.ModelKey, COUNT_BIG(*) AS N
    FROM bi.FactInteraction AS f {Ctx.Scope("f.UserKey")}
    JOIN bi.DimDate AS d ON d.DateKey = f.DateKey
    WHERE f.DateKey BETWEEN @kt0 AND @k1
    GROUP BY {Ctx.BucketSql("d")}, f.ModelKey)
SELECT g.Bucket, {modelName}, SUM(g.N)
FROM g JOIN bi.DimModel AS mo ON mo.ModelKey = g.ModelKey
LEFT JOIN dash.ValueName AS vn ON vn.ColumnId = 16 AND vn.Value = mo.ModelName
GROUP BY g.Bucket, {modelName};", Ctx.Args());
        var agents = await Db.QueryAsync<(DateTime Bucket, string Source, int N)>($@"
SELECT {Ctx.BucketSql("d")}, a.AgentSource, COUNT(DISTINCT f.AgentKey)
FROM bi.FactInteraction AS f {Ctx.Scope("f.UserKey")}
JOIN bi.DimDate AS d ON d.DateKey = f.DateKey
JOIN bi.DimAgent AS a ON a.AgentKey = f.AgentKey AND a.AgentSource <> N'(no agent)'
WHERE f.DateKey BETWEEN @kt0 AND @k1
GROUP BY {Ctx.BucketSql("d")}, a.AgentSource;", Ctx.Args());

        Kpis.Add(new("Searched the web", Pct(Ratio(sums.Web, all)), "of interactions used web search"));
        Kpis.Add(new("In Copilot Chat", Pct(Ratio(sums.Chat, all)), "the rest is Copilot inside the apps"));
        Kpis.Add(new("Through an agent", Pct(Ratio(sums.Agent, all)), "of interactions were answered by an agent"));
        Kpis.Add(new("Models seen", Num(byModel.Count)));
        var flags = (await Db.QueryAsync<(long N, long Memory, long AgentCalls, long Dlp)>($@"
SELECT COUNT_BIG(*), SUM(CAST(f.MemoryUpdated AS INT)), SUM(CAST(f.CalledAnotherAgent AS INT)), SUM(CAST(f.DlpEvaluationDeferred AS INT))
FROM bi.FactInteraction AS f {Ctx.Scope("f.UserKey")}
WHERE f.DateKey BETWEEN @k0 AND @k1;", Ctx.Args())).Single();
        Kpis.Add(new("Updated Copilot memory", Pct(Ratio(flags.Memory, flags.N)), "of interactions saved something to memory"));
        Kpis.Add(new("Called another agent", Pct(Ratio(flags.AgentCalls, flags.N)), "handed work to a second agent"));
        Kpis.Add(new("Data-loss check deferred", Num(flags.Dlp), "interactions Copilot marked as having their data-loss prevention (DLP) check deferred"));

        var top = byModel.OrderByDescending(x => x.N).Select(x => x.Model).Take(6).ToList();
        var modelAt = models.ToLookup(x => (x.Model, x.Bucket), x => x.N);
        var allModelsAt = models.ToLookup(x => x.Bucket, x => x.N);
        var series = top.Select((mdl, i) => new Series(mdl, AverageRatio(b => modelAt[(mdl, b.Key)].Sum(), b => allModelsAt[b.Key].Sum()), i + 1)).ToList();
        Panels.Add(new("Model mix", Chart.Line(Labels(), series, Fmt.Percent, "Share of interactions by model", 1),
            $"Each model's share of interactions, from the audit record's model details. A step change is usually a Microsoft rollout. {RatioNote}"));

        Panels.Add(new("Interactions that searched the web",
            Chart.Line(Labels(), [new Series("Searched the web", tr.AverageRatio(w => w.Web, w => w.Interactions), 1)], Fmt.Percent, "Share of interactions that searched the web"),
            $"Interactions where Copilot searched the public web. Worth watching if web search should be off for some people. {RatioNote}", "half"));
        Panels.Add(new("Copilot Chat and Copilot in the apps",
            Chart.Line(Labels(), [
                new Series("Copilot Chat", tr.AverageRatio(w => w.Chat, w => w.Interactions), 1),
                new Series("In the apps", tr.AverageRatio(w => w.Interactions - w.Chat, w => w.Interactions), 2),
            ], Fmt.Percent, "Chat and in-app share", 1), $"App categories come from Admin › Reference data. {RatioNote}", "half"));

        var sources = agents.Select(a => a.Source).Distinct().OrderBy(x => x).ToList();
        var agentsAt = agents.ToLookup(a => (a.Source, a.Bucket), a => a.N);
        Panels.Add(new("Agents in use",
            Chart.Columns(Labels(), sources.Select((src, i) => new Series(src, Average(b => agentsAt[(src, b.Key)].Sum()), i + 1)).ToList(),
                Fmt.Average, "Distinct agents used, 28-day average"), $"How many different agents were used {PerPoint}, on average, by where they were built. {TrendNote}", "half"));
        var born = await Db.QueryAsync<(string Source, int N, int New)>(@"
SELECT a.AgentSource, COUNT(*), COUNT(CASE WHEN a.FirstSeenUtc >= @d0 THEN 1 END)
FROM dash.Agent AS a WHERE a.AgentSource <> N'(no agent)' AND a.FirstSeenUtc < DATEADD(DAY, 1, @d1)
GROUP BY a.AgentSource;", Ctx.Args());
        Panels.Add(new("Every agent seen, by where it was built",
            Chart.Bars(born.OrderByDescending(b => b.N).Select(b => new Bar(b.Source, b.N, Note: $"{b.New:#,0} first used in the period")).ToList(), Fmt.Count, "Agents known"),
            "Every agent the audit log has seen, whoever used it. A growing count with few users each is agent sprawl. This panel covers the whole organization whatever you are looking at: an agent does not belong to a part of the org chart.", "half"));
    }
}
