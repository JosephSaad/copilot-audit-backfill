using CopilotInsights.Charts;
using CopilotInsights.Data;

namespace CopilotInsights.Pages.Governance;

public sealed class IndexModel(PageServices s, Metrics m) : InsightsPage(s)
{
    public override string Title => "Models and grounding";
    public override string Persona => "Governance";
    public override string Intro => "Which models answer, how much Copilot grounds on the public web, Copilot Chat against Copilot in the apps, and how many agents are in play.";

    protected override async Task BuildAsync()
    {
        var tr = await m.TrendAsync(Ctx);
        long all = tr.Sum(w => w.Interactions);
        var models = await Db.QueryAsync<(DateTime Bucket, string Model, long N)>($@"
SELECT {Ctx.BucketSql("d")}, CASE WHEN mo.ModelName = N'' THEN N'(not recorded)' ELSE mo.ModelProvider + N' ' + mo.ModelName END, COUNT_BIG(*)
FROM bi.FactInteraction AS f {Ctx.Scope("f.UserKey")}
JOIN bi.DimDate AS d ON d.DateKey = f.DateKey
JOIN bi.DimModel AS mo ON mo.ModelKey = f.ModelKey
WHERE f.DateKey BETWEEN @k0 AND @k1
GROUP BY {Ctx.BucketSql("d")}, CASE WHEN mo.ModelName = N'' THEN N'(not recorded)' ELSE mo.ModelProvider + N' ' + mo.ModelName END;", Ctx.Args());
        var agents = await Db.QueryAsync<(DateTime Bucket, string Source, int N)>($@"
SELECT {Ctx.BucketSql("d")}, a.AgentSource, COUNT(DISTINCT f.AgentKey)
FROM bi.FactInteraction AS f {Ctx.Scope("f.UserKey")}
JOIN bi.DimDate AS d ON d.DateKey = f.DateKey
JOIN bi.DimAgent AS a ON a.AgentKey = f.AgentKey AND a.AgentSource <> N'(no agent)'
WHERE f.DateKey BETWEEN @k0 AND @k1
GROUP BY {Ctx.BucketSql("d")}, a.AgentSource;", Ctx.Args());
        var buckets = Buckets();

        Kpis.Add(new("Grounded on the web", Pct(Ratio(tr.Sum(w => w.Web), all)), "of interactions used web search"));
        Kpis.Add(new("In Copilot Chat", Pct(Ratio(tr.Sum(w => w.Chat), all)), "the rest is Copilot inside the apps"));
        Kpis.Add(new("Through an agent", Pct(Ratio(tr.Sum(w => w.Agent), all))));
        Kpis.Add(new("Models seen", Num(models.Select(x => x.Model).Distinct().Count())));

        var top = models.GroupBy(x => x.Model).OrderByDescending(g => g.Sum(x => x.N)).Select(g => g.Key).Take(6).ToList();
        var series = top.Select((mdl, i) => new Series(mdl, buckets.Select(b =>
        {
            var total = models.Where(x => x.Bucket == b.Key).Sum(x => x.N);
            return Ratio(models.Where(x => x.Model == mdl && x.Bucket == b.Key).Sum(x => x.N), total);
        }).ToList(), i + 1)).ToList();
        Panels.Add(new("Model mix", Chart.Line(Labels(), series, Fmt.Percent, "Share of interactions by model", 1),
            $"Each model's share of interactions, from the audit record's model details. A step change is usually a Microsoft rollout. {TrendNote}"));

        Panels.Add(new("Web grounding",
            Chart.Line(Labels(), [new Series("Web grounded", tr.Select(w => Ratio(w.Web, w.Interactions)).ToList(), 1)], Fmt.Percent, "Web-grounded share"),
            "Interactions where Copilot searched the public web. Worth watching if web search should be off for some people.", "half"));
        Panels.Add(new("Copilot Chat and Copilot in the apps",
            Chart.Line(Labels(), [
                new Series("Copilot Chat", tr.Select(w => Ratio(w.Chat, w.Interactions)).ToList(), 1),
                new Series("In the apps", tr.Select(w => Ratio(w.Interactions - w.Chat, w.Interactions)).ToList(), 2),
            ], Fmt.Percent, "Chat and in-app share", 1), "App categories come from Admin › Reference data.", "half"));

        var sources = agents.Select(a => a.Source).Distinct().OrderBy(x => x).ToList();
        Panels.Add(new("Agents in use",
            Chart.Columns(Labels(), sources.Select((src, i) => new Series(src, buckets.Select(b => (double?)agents.Where(a => a.Source == src && a.Bucket == b.Key).Sum(a => a.N)).ToList(), i + 1)).ToList(),
                Fmt.Count, "Distinct agents used"), "Distinct agents used in each day or week, by where they come from.", "half"));
        var born = await Db.QueryAsync<(string Source, int N, int New)>(@"
SELECT a.AgentSource, COUNT(*), COUNT(CASE WHEN a.FirstSeenUtc >= @d0 THEN 1 END)
FROM dash.Agent AS a WHERE a.AgentSource <> N'(no agent)' AND a.FirstSeenUtc < DATEADD(DAY, 1, @d1)
GROUP BY a.AgentSource;", Ctx.Args());
        Panels.Add(new("Agent sprawl",
            Chart.Bars(born.OrderByDescending(b => b.N).Select(b => new Bar(b.Source, b.N, Note: $"{b.New:#,0} first used in the period")).ToList(), Fmt.Count, "Agents known"),
            "Every agent the audit log has seen, whoever used it, by source. This one is organisation-wide: agents are not owned by a part of the org chart.", "half"));
    }
}
