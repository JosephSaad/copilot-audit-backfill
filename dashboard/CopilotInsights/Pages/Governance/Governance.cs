using CopilotInsights.Charts;
using CopilotInsights.Data;

namespace CopilotInsights.Pages.Governance;

public sealed class IndexModel(PageServices s, Metrics m) : InsightsPage(s)
{
    public override string Title => "Models and controls";
    public override string Persona => "Governance";
    public override string Intro => "Which models answer, how often Copilot searches the public web and where, Copilot Chat against Copilot in the apps, memory updates and deferred data-loss checks over time, and how many agents are in play.";

    protected override async Task BuildAsync()
    {
        var tr = await m.TrendAsync(Ctx);
        long all = tr.Sum(w => w.Interactions);
        /* Counted per model key first, then named (2.4.2): dash.fnValueName in the GROUP BY over the fact ran once a row. */
        var models = await Db.QueryAsync<(DateTime Bucket, string Model, long N)>($@"
WITH g AS (
    SELECT {Ctx.BucketSql("d")} AS Bucket, f.ModelKey, COUNT_BIG(*) AS N
    FROM bi.FactInteraction AS f {Ctx.Scope("f.UserKey")}
    JOIN bi.DimDate AS d ON d.DateKey = f.DateKey
    WHERE f.DateKey BETWEEN @k0 AND @k1
    GROUP BY {Ctx.BucketSql("d")}, f.ModelKey)
SELECT g.Bucket, CASE WHEN mo.ModelName = N'' THEN N'(not recorded)' ELSE mo.ModelProvider + N' ' + COALESCE(vn.Name, mo.ModelName) END, SUM(g.N)
FROM g JOIN bi.DimModel AS mo ON mo.ModelKey = g.ModelKey
LEFT JOIN dash.ValueName AS vn ON vn.ColumnId = 16 AND vn.Value = mo.ModelName
GROUP BY g.Bucket, CASE WHEN mo.ModelName = N'' THEN N'(not recorded)' ELSE mo.ModelProvider + N' ' + COALESCE(vn.Name, mo.ModelName) END;", Ctx.Args());
        var agents = await Db.QueryAsync<(DateTime Bucket, string Source, int N)>($@"
SELECT {Ctx.BucketSql("d")}, a.AgentSource, COUNT(DISTINCT f.AgentKey)
FROM bi.FactInteraction AS f {Ctx.Scope("f.UserKey")}
JOIN bi.DimDate AS d ON d.DateKey = f.DateKey
JOIN bi.DimAgent AS a ON a.AgentKey = f.AgentKey AND a.AgentSource <> N'(no agent)'
WHERE f.DateKey BETWEEN @k0 AND @k1
GROUP BY {Ctx.BucketSql("d")}, a.AgentSource;", Ctx.Args());
        var buckets = Buckets();

        Kpis.Add(new("Searched the web", Pct(Ratio(tr.Sum(w => w.Web), all)), "of interactions used web search"));
        Kpis.Add(new("In Copilot Chat", Pct(Ratio(tr.Sum(w => w.Chat), all)), "the rest is Copilot inside the apps"));
        Kpis.Add(new("Through an agent", Pct(Ratio(tr.Sum(w => w.Agent), all)), "of interactions were answered by an agent"));
        Kpis.Add(new("Models seen", Num(models.Select(x => x.Model).Distinct().Count())));
        /* Memory updates, calls to another agent and deferred data-loss checks at each point; their totals are the period's. */
        var controls = await Db.QueryAsync<(DateTime Bucket, long N, long Memory, long AgentCalls, long Dlp)>($@"
SELECT {Ctx.BucketSql("d")}, COUNT_BIG(*), SUM(CAST(f.MemoryUpdated AS INT)), SUM(CAST(f.CalledAnotherAgent AS INT)), SUM(CAST(f.DlpEvaluationDeferred AS INT))
FROM bi.FactInteraction AS f {Ctx.Scope("f.UserKey")} JOIN bi.DimDate AS d ON d.DateKey = f.DateKey
WHERE f.DateKey BETWEEN @k0 AND @k1 GROUP BY {Ctx.BucketSql("d")};", Ctx.Args());
        var flags = (N: controls.Sum(x => x.N), Memory: controls.Sum(x => x.Memory), AgentCalls: controls.Sum(x => x.AgentCalls), Dlp: controls.Sum(x => x.Dlp));
        Kpis.Add(new("Updated Copilot memory", Pct(Ratio(flags.Memory, flags.N)), "of interactions saved something to memory"));
        Kpis.Add(new("Called another agent", Pct(Ratio(flags.AgentCalls, flags.N)), "handed work to a second agent"));
        Kpis.Add(new("Data-loss check deferred", Num(flags.Dlp), "interactions Copilot marked as having their data-loss prevention (DLP) check deferred"));

        var top = models.GroupBy(x => x.Model).OrderByDescending(g => g.Sum(x => x.N)).Select(g => g.Key).Take(6).ToList();
        var series = top.Select((mdl, i) => new Series(mdl, buckets.Select(b =>
        {
            var total = models.Where(x => x.Bucket == b.Key).Sum(x => x.N);
            return Ratio(models.Where(x => x.Model == mdl && x.Bucket == b.Key).Sum(x => x.N), total);
        }).ToList(), i + 1)).ToList();
        Panels.Add(new("Model mix", Chart.Line(Labels(), series, Fmt.Percent, "Share of interactions by model", 1),
            $"Each model's share of interactions, from the audit record's model details. A step change is usually a Microsoft rollout. {TrendNote}"));

        Panels.Add(new("Interactions that searched the web",
            Chart.Line(Labels(), [new Series("Searched the web", tr.Select(w => Ratio(w.Web, w.Interactions)).ToList(), 1)], Fmt.Percent, "Share of interactions that searched the web"),
            "Interactions where Copilot searched the public web. Worth watching if web search should be off for some people.", "half"));
        Panels.Add(new("Copilot Chat and Copilot in the apps",
            Chart.Line(Labels(), [
                new Series("Copilot Chat", tr.Select(w => Ratio(w.Chat, w.Interactions)).ToList(), 1),
                new Series("In the apps", tr.Select(w => Ratio(w.Interactions - w.Chat, w.Interactions)).ToList(), 2),
            ], Fmt.Percent, "Chat and in-app share", 1), "App categories come from Admin › Reference data.", "half"));

        /* The controls over time and web search by unit: Governance › Copilot controls was a page of its own for these,
           beside this page's figures for the same three things. */
        Panels.Add(new("Interactions that updated Copilot memory",
            Chart.Line(Labels(), [new Series("Updated memory", buckets.Select(b => { var r = controls.FirstOrDefault(x => x.Bucket == b.Key); return Ratio(r.Memory, r.N); }).ToList(), 5)], Fmt.Percent, "Share of interactions that updated Copilot memory"),
            $"The share of interactions in which Copilot saved something to its memory of the person. {TrendNote}", "half"));
        Panels.Add(new("Deferred data-loss checks",
            Chart.Columns(Labels(), [new Series("Deferred", buckets.Select(b => (double?)controls.FirstOrDefault(x => x.Bucket == b.Key).Dlp).ToList(), 4)], Fmt.Count, "Data-loss checks deferred"),
            "Interactions that Copilot's own record marks as having their data-loss prevention (DLP) check deferred. Microsoft sets the mark; a rising count is worth raising with them.", "half"));
        var web = SmallGroups.Shown(await m.UnitsAsync(Ctx), u => u.People, Ctx.MinGroupSize).Where(u => u.Interactions > 0)
            .OrderByDescending(u => u.Web / (double)u.Interactions).ToList();
        Panels.Add(new("Web search by unit",
            Chart.Bars(web.Take(15).Select(u => new Bar(u.Name, u.Web / (double)u.Interactions, u.Drillable ? Ctx.Url(Request.Path, org: u.Unit) : null, $"{u.Web:#,0} of {u.Interactions:#,0}")).ToList(), Fmt.Percent, "Share of interactions that searched the web, by unit", 1),
            $"The share of each unit's interactions that searched the web, for each part of the organization one level below; units under {Ctx.MinGroupSize} people are left out. Select a name to look inside it."));

        var sources = agents.Select(a => a.Source).Distinct().OrderBy(x => x).ToList();
        Panels.Add(new("Agents in use",
            Chart.Columns(Labels(), sources.Select((src, i) => new Series(src, buckets.Select(b => (double?)agents.Where(a => a.Source == src && a.Bucket == b.Key).Sum(a => a.N)).ToList(), i + 1)).ToList(),
                Fmt.Count, "Distinct agents used"), "How many different agents were used in each day or week, by where they were built.", "half"));
        /* 2.7.0: Copilot by product, from the app each interaction names (its AppHost) and the agent behind it: Security
           Copilot, Copilot in Fabric and Power BI, Edge, Copilot Studio's agents, and Microsoft 365 Copilot for the rest. */
        var products = await Db.QueryAsync<(string Product, long N, int People)>($@"
WITH f AS (
    SELECT f.UserKey,
           CASE WHEN a.App LIKE N'%Security%' THEN N'Security Copilot'
                WHEN a.App LIKE N'%Fabric%' OR a.App LIKE N'%PowerBI%' OR a.App LIKE N'%Power BI%' THEN N'Copilot in Fabric and Power BI'
                /* Microsoft records Copilot in the Edge browser as AppHost Bing. */
                WHEN a.App LIKE N'%Edge%' OR a.App = N'Bing' THEN N'Copilot in Edge and Bing'
                WHEN ag.AgentSource = N'Copilot Studio' THEN N'Copilot Studio agents'
                WHEN ag.AgentSource IS NOT NULL AND ag.AgentSource <> N'(no agent)' THEN N'Other agents'
                ELSE N'Microsoft 365 Copilot' END AS Product
    FROM bi.FactInteraction AS f {Ctx.Scope("f.UserKey")}
    JOIN bi.DimApp AS a ON a.AppKey = f.AppKey
    LEFT JOIN bi.DimAgent AS ag ON ag.AgentKey = f.AgentKey
    WHERE f.DateKey BETWEEN @k0 AND @k1)
SELECT Product, COUNT_BIG(*), COUNT(DISTINCT UserKey) FROM f GROUP BY Product ORDER BY COUNT_BIG(*) DESC;", Ctx.Args());
        Panels.Add(new("Copilot by product", Chart.Bars(products.Select(p => new Bar(p.Product, p.N,
                Note: $"{(p.People >= Ctx.MinGroupSize ? p.People.ToString("#,0") : $"fewer than {Ctx.MinGroupSize}")} people")).ToList(), Fmt.Count, "Interactions by Copilot product"),
            "Which Copilot answered, from the app each interaction names and the agent behind it: Security Copilot (where its owner turned on Purview audit), Copilot in Fabric and Power BI, Copilot in Edge and Bing, Copilot Studio's agents, other agents, and Microsoft 365 Copilot in Word, Excel, Teams, Outlook and Copilot Chat.", "half"));

        var born = await Db.QueryAsync<(string Source, int N, int New)>(@"
SELECT a.AgentSource, COUNT(*), COUNT(CASE WHEN a.FirstSeenUtc >= @d0 THEN 1 END)
FROM dash.Agent AS a WHERE a.AgentSource <> N'(no agent)' AND a.FirstSeenUtc < DATEADD(DAY, 1, @d1)
GROUP BY a.AgentSource;", Ctx.Args());
        Panels.Add(new("Every agent seen, by where it was built",
            Chart.Bars(born.OrderByDescending(b => b.N).Select(b => new Bar(b.Source, b.N, Note: $"{b.New:#,0} first used in the period")).ToList(), Fmt.Count, "Agents known"),
            "Every agent the audit log has seen, whoever used it. A growing count with few users each is agent sprawl. This panel covers the whole organization whatever you are looking at: an agent does not belong to a part of the org chart.", "half"));
    }
}
