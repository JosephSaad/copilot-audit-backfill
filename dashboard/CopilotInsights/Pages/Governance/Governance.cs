using CopilotInsights.Charts;
using CopilotInsights.Data;

namespace CopilotInsights.Pages.Governance;

public sealed class IndexModel(PageServices s, Metrics m) : InsightsPage(s)
{
    public override string Title => "Models and controls";
    public override string Persona => "Governance";
    public override string Intro => "Which AI models answer, how often Copilot searches the public web and in which units, how much use is in the Copilot Chat app rather than inside Word, Outlook and the other apps, how often Copilot updates its memory or defers a data loss prevention check, and how many agents are in use. Shares are of interactions (one prompt and Copilot's answer) in the period.";

    protected override async Task BuildAsync()
    {
        /* Six independent reads, one of them over the fact: started together, awaited where used. */
        var trTask = m.TrendAsync(Ctx);
        var unitsTask = m.UnitsAsync(Ctx);
        /* The model mix and the controls in one pass over the fact, counted per trend point and model key, then named:
           dash.fnValueName in the GROUP BY over the fact ran once a row. A model key the model list lacks counts in the
           controls, not in the mix. Grouped by the day's key first, and put in trend points after: the fact is not joined. */
        var pointsTask = Db.QueryAsync<(DateTime Bucket, string? Model, long N, long Memory, long AgentCalls, long Dlp)>($@"
WITH g AS (
    SELECT f.DateKey, f.ModelKey, COUNT_BIG(*) AS N, SUM(CAST(f.MemoryUpdated AS INT)) AS Memory,
           SUM(CAST(f.CalledAnotherAgent AS INT)) AS AgentCalls, SUM(CAST(f.DlpEvaluationDeferred AS INT)) AS Dlp
    FROM bi.FactInteraction AS f {Ctx.Scope("f.UserKey")}
    WHERE f.DateKey BETWEEN @k0 AND @k1
    GROUP BY f.DateKey, f.ModelKey)
SELECT {Ctx.BucketSql("d")}, CASE WHEN mo.ModelKey IS NULL THEN NULL WHEN mo.ModelName = N'' THEN N'(not recorded)' ELSE mo.ModelProvider + N' ' + COALESCE(vn.Name, mo.ModelName) END,
       SUM(g.N), SUM(CAST(g.Memory AS BIGINT)), SUM(CAST(g.AgentCalls AS BIGINT)), SUM(CAST(g.Dlp AS BIGINT))
FROM g JOIN bi.DimDate AS d ON d.DateKey = g.DateKey
LEFT JOIN bi.DimModel AS mo ON mo.ModelKey = g.ModelKey
LEFT JOIN dash.ValueName AS vn ON vn.ColumnId = 16 AND vn.Value = mo.ModelName
GROUP BY {Ctx.BucketSql("d")}, CASE WHEN mo.ModelKey IS NULL THEN NULL WHEN mo.ModelName = N'' THEN N'(not recorded)' ELSE mo.ModelProvider + N' ' + COALESCE(vn.Name, mo.ModelName) END;", Ctx.Args());
        /* The agents used at each point, from each person's use of each agent the refresh keeps. */
        var agentsTask = Db.QueryAsync<(DateTime Bucket, string Source, int N)>($@"
SELECT u.Bucket, a.AgentSource, COUNT(DISTINCT u.Item)
FROM {Ctx.PerItemPoint('G')} AS u {Ctx.Scope("u.UserKey")}
JOIN bi.DimAgent AS a ON a.AgentKey = u.Item AND a.AgentSource <> N'(no agent)'
GROUP BY u.Bucket, a.AgentSource;", Ctx.Args());
        var productsTask = ProductsAsync();
        var bornTask = Db.QueryAsync<(string Source, int N, int New)>(@"
SELECT a.AgentSource, COUNT(*), COUNT(CASE WHEN a.FirstSeenUtc >= @d0 THEN 1 END)
FROM dash.Agent AS a WHERE a.AgentSource <> N'(no agent)' AND a.FirstSeenUtc < DATEADD(DAY, 1, @d1)
GROUP BY a.AgentSource;", Ctx.Args());
        await Task.WhenAll(trTask, unitsTask, pointsTask, agentsTask, productsTask, bornTask);
        var tr = await trTask;
        long all = tr.Sum(w => w.Interactions);
        var points = await pointsTask;
        var models = points.Where(x => x.Model is not null).Select(x => (x.Bucket, Model: x.Model!, x.N)).ToList();
        var agents = await agentsTask;
        var buckets = Buckets();

        Kpis.Add(new("Searched the web", Pct(Ratio(tr.Sum(w => w.Web), all)), "of interactions used web search"));
        Kpis.Add(new("In Copilot Chat", Pct(Ratio(tr.Sum(w => w.Chat), all)), "of interactions were in the Copilot Chat app; the rest in Copilot inside the apps"));
        Kpis.Add(new("Through an agent", Pct(Ratio(tr.Sum(w => w.Agent), all)), "of interactions were answered by an agent"));
        Kpis.Add(new("Models seen", Num(models.Select(x => x.Model).Distinct().Count()), "different models named in the period's audit records"));
        /* Memory updates, calls to another agent and deferred data-loss checks at each point; their totals are the period's. */
        var controls = points.GroupBy(x => x.Bucket)
            .Select(g => (Bucket: g.Key, N: g.Sum(x => x.N), Memory: g.Sum(x => x.Memory), AgentCalls: g.Sum(x => x.AgentCalls), Dlp: g.Sum(x => x.Dlp))).ToList();
        var flags = (N: controls.Sum(x => x.N), Memory: controls.Sum(x => x.Memory), AgentCalls: controls.Sum(x => x.AgentCalls), Dlp: controls.Sum(x => x.Dlp));
        Kpis.Add(new("Updated Copilot memory", Pct(Ratio(flags.Memory, flags.N)), "of interactions saved something to memory"));
        Kpis.Add(new("Called another agent", Pct(Ratio(flags.AgentCalls, flags.N)), "of interactions in which an agent handed work to another agent (Agents › Agent hand-offs)"));
        Kpis.Add(new("Data-loss check deferred", Num(flags.Dlp), "interactions whose data loss prevention (DLP) check Copilot marked as deferred"));

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
            "The share of interactions in which Copilot searched the public web. Worth watching if web search should be off for some people.", "half"));
        Panels.Add(new("Copilot Chat and Copilot in the apps",
            Chart.Line(Labels(), [
                new Series("Copilot Chat", tr.Select(w => Ratio(w.Chat, w.Interactions)).ToList(), 1),
                new Series("In the apps", tr.Select(w => Ratio(w.Interactions - w.Chat, w.Interactions)).ToList(), 2),
            ], Fmt.Percent, "Chat and in-app share", 1), "Where Copilot was used, not which license: Copilot Chat is the chat app, In the apps is Copilot inside Word, Excel, Outlook, Teams and the others. Which app is which is set in Admin › Reference data.", "half"));

        /* The controls over time and web search by unit: Governance › Copilot controls was a page of its own for these,
           beside this page's figures for the same three things. */
        Panels.Add(new("Interactions that updated Copilot memory",
            Chart.Line(Labels(), [new Series("Updated memory", buckets.Select(b => { var r = controls.FirstOrDefault(x => x.Bucket == b.Key); return Ratio(r.Memory, r.N); }).ToList(), 5)], Fmt.Percent, "Share of interactions that updated Copilot memory"),
            $"The share of interactions in which Copilot saved something to its memory of the person. {TrendNote}", "half"));
        Panels.Add(new("Deferred data-loss checks",
            Chart.Columns(Labels(), [new Series("Deferred", buckets.Select(b => (double?)controls.FirstOrDefault(x => x.Bucket == b.Key).Dlp).ToList(), 4)], Fmt.Count, "Data-loss checks deferred"),
            $"Interactions whose data loss prevention (DLP) check the Copilot audit record marks as deferred. Microsoft sets the mark; a rising count is worth raising with Microsoft. {TrendNote}", "half"));
        var web = SmallGroups.Shown(await unitsTask, u => u.People, Ctx.MinGroupSize).Where(u => u.Interactions > 0)
            .OrderByDescending(u => u.Web / (double)u.Interactions).ToList();
        Panels.Add(new("Web search by unit",
            Chart.Bars(web.Take(15).Select(u => new Bar(u.Name, u.Web / (double)u.Interactions, u.Drillable ? Ctx.Url(Request.Path, org: u.Unit) : null, $"{u.Web:#,0} of {u.Interactions:#,0}")).ToList(), Fmt.Percent, "Share of interactions that searched the web, by unit", 1),
            $"The share of each unit's interactions that searched the web, for each part of the organization one level below; units under {Ctx.MinGroupSize} people are left out. Select a name to look inside it."));

        var sources = agents.Select(a => a.Source).Distinct().OrderBy(x => x).ToList();
        Panels.Add(new("Agents in use",
            Chart.Columns(Labels(), sources.Select((src, i) => new Series(src, buckets.Select(b => (double?)agents.Where(a => a.Source == src && a.Bucket == b.Key).Sum(a => a.N)).ToList(), i + 1)).ToList(),
                Fmt.Count, "Distinct agents used"), "How many different agents were used each day or week, by where they were built.", "half"));
        var products = await productsTask;
        Panels.Add(new("Copilot by product", Chart.Bars(products.Select(p => new Bar(p.Product, p.N,
                Note: $"{(p.People >= Ctx.MinGroupSize ? p.People.ToString("#,0") : $"fewer than {Ctx.MinGroupSize}")} people")).ToList(), Fmt.Count, "Interactions by Copilot product"),
            "Which Copilot answered, from the app each interaction names and the agent behind it: Security Copilot (where its owner turned on Purview audit), Copilot in Fabric and Power BI, Copilot in Edge and Bing, Copilot Studio's agents, other agents, and Microsoft 365 Copilot in Word, Excel, Teams, Outlook and Copilot Chat. People: accounts enabled today; interactions count all use.", "half"));

        var born = await bornTask;
        Panels.Add(new("Every agent seen, by where it was built",
            Chart.Bars(born.OrderByDescending(b => b.N).Select(b => new Bar(b.Source, b.N, Note: $"{b.New:#,0} first used in the period")).ToList(), Fmt.Count, "Agents known"),
            "Every agent the audit log has seen, whoever used it. A growing count with few users each is agent sprawl. This panel covers the whole organization whatever you are looking at: an agent does not belong to a part of the org chart.", "half"));
    }

    /* Copilot by product, from the app each interaction names (its AppHost) and the agent behind it: Security
       Copilot, Copilot in Fabric and Power BI, Edge, Copilot Studio's agents, and Microsoft 365 Copilot for the rest. */
    /* An interaction's product is its app's where the app names one (Security, Fabric and Power BI, Edge and Bing),
       else its agent's (Copilot Studio's, or another), else Microsoft 365 Copilot. Worked out per person from the
       totals the refresh keeps per person and app, and per person and agent: an agent's interactions in an app that
       names its own product are taken off the agent's, from the fact, and only when such an app was used; what is left
       of a person's interactions is Microsoft 365 Copilot. The same counts as classifying each interaction. */
    Task<List<(string Product, long N, int People)>> ProductsAsync() => Db.QueryAsync<(string Product, long N, int People)>($@"
SELECT a.AppKey, p.Product INTO #sp
FROM bi.DimApp AS a
CROSS APPLY (SELECT CASE WHEN a.App LIKE N'%Security%' THEN N'Security Copilot'
                         WHEN a.App LIKE N'%Fabric%' OR a.App LIKE N'%PowerBI%' OR a.App LIKE N'%Power BI%' THEN N'Copilot in Fabric and Power BI'
                         /* Microsoft records Copilot in the Edge browser as AppHost Bing. */
                         WHEN a.App LIKE N'%Edge%' OR a.App = N'Bing' THEN N'Copilot in Edge and Bing' END AS Product) AS p
WHERE p.Product IS NOT NULL;
SELECT ag.AgentKey, p.Product INTO #ap
FROM bi.DimAgent AS ag
CROSS APPLY (SELECT CASE WHEN ag.AgentSource = N'Copilot Studio' THEN N'Copilot Studio agents'
                         WHEN ag.AgentSource IS NOT NULL AND ag.AgentSource <> N'(no agent)' THEN N'Other agents' END AS Product) AS p
WHERE p.Product IS NOT NULL;
SELECT u.UserKey, sp.Product, CAST(u.Interactions AS BIGINT) AS N INTO #x
FROM {Ctx.PerItem('A')} AS u {Ctx.Scope("u.UserKey")} JOIN #sp AS sp ON sp.AppKey = u.Item;
DECLARE @apps BIT = CASE WHEN EXISTS (SELECT 1 FROM #x) THEN 1 ELSE 0 END;
INSERT INTO #x (UserKey, Product, N)
SELECT u.UserKey, ap.Product, CAST(u.Interactions AS BIGINT)
FROM {Ctx.PerItem('G')} AS u {Ctx.Scope("u.UserKey")} JOIN #ap AS ap ON ap.AgentKey = u.Item;
IF @apps = 1
    INSERT INTO #x (UserKey, Product, N)
    SELECT f.UserKey, ap.Product, -COUNT_BIG(*)
    FROM bi.FactInteraction AS f {Ctx.Scope("f.UserKey")}
    JOIN #sp AS sp ON sp.AppKey = f.AppKey JOIN #ap AS ap ON ap.AgentKey = f.AgentKey
    WHERE f.DateKey BETWEEN @k0 AND @k1
    GROUP BY f.UserKey, ap.Product;
WITH p AS (SELECT x.UserKey, x.Product, SUM(x.N) AS N FROM #x AS x GROUP BY x.UserKey, x.Product),
r AS (
    SELECT p.UserKey, p.Product, p.N FROM p WHERE p.N > 0
    UNION ALL
    SELECT pp.UserKey, N'Microsoft 365 Copilot', CAST(pp.Interactions AS BIGINT) - ISNULL(o.N, 0)
    FROM {Ctx.PerPerson()} AS pp {Ctx.Scope("pp.UserKey")}
    LEFT JOIN (SELECT p.UserKey, SUM(p.N) AS N FROM p GROUP BY p.UserKey) AS o ON o.UserKey = pp.UserKey
    WHERE CAST(pp.Interactions AS BIGINT) - ISNULL(o.N, 0) > 0)
/* People are accounts enabled today; the interactions are everyone's. A person is in each product once. */
SELECT r.Product, SUM(r.N), COUNT(CASE WHEN {CopilotInsights.Infrastructure.ReportContext.IsEnabled()} THEN 1 END)
FROM r {CopilotInsights.Infrastructure.ReportContext.EnabledJoin("r.UserKey", left: true)} GROUP BY r.Product ORDER BY SUM(r.N) DESC;", Ctx.Args());
}
