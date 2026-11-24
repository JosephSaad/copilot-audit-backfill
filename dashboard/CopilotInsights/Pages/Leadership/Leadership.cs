using CopilotInsights.Charts;
using CopilotInsights.Data;

namespace CopilotInsights.Pages.Leadership;

public sealed class IndexModel(PageServices s, Metrics m) : InsightsPage(s)
{
    public override string Title => "Overview";
    public override string Persona => "Leadership";
    public override string Intro => "Is Copilot being used, by how many of the people who have it, and where, over the period chosen at the top. "
        + "Premium is the paid Microsoft 365 Copilot license; without it people can still use the free Copilot Chat. Totals only: no one is named.";

    protected override async Task BuildAsync()
    {
        /* The four reads are independent and each opens its own connection: read together, not one after another. */
        var totalsTask = m.TotalsAsync(Ctx);
        var trendTask = m.TrendAsync(Ctx);
        var adoptedTask = m.AdoptedAsync(Ctx);
        var unitsTask = m.UnitsAsync(Ctx);
        await Task.WhenAll(totalsTask, trendTask, adoptedTask, unitsTask);
        var t = await totalsTask;
        var tr = await trendTask;
        var adopted = await adoptedTask;
        var x = Labels();

        Kpis.Add(new("People using Copilot", Num(t.Active), t.People > 0 ? $"had an interaction in the period, of {t.People:#,0} people in {Here}" : "had an interaction in the period", Href: Open("/licensing/ranking")));
        Kpis.Add(new("Premium licenses", Num(t.LicensedNow), (t.People > 0 ? $"{Pct(Ratio(t.LicensedNow, t.People))} of people; " : "") + LicensesAsOf(t), Href: Open("/licensing")));
        Kpis.Add(new("Adoption", Pct(Ratio(t.ActiveLicensed, t.LicensedNow)), "of Premium license holders used Copilot with the license in the period", Href: Open("/adoption")));
        Kpis.Add(new("Active in the last 7 days", Num(t.ActiveLast7), $"people who used Copilot in the 7 days to {Ctx.End:d MMM}, licensed or not", Href: Open("/adoption")));
        Kpis.Add(new("Interactions", Num(t.Interactions), t.Active > 0 ? $"{t.Interactions / (double)t.Active:#,0.0} per person using Copilot; includes use by accounts since disabled" : null, Href: Open("/leadership/mix")));

        Panels.Add(new("People using Copilot",
            Chart.Line(x, [
                new Series("Premium license holders", tr.Select(w => (double?)w.Licensed).ToList(), 1, ToEnd: true),
                new Series("Used Copilot with a Premium license", tr.Select(w => (double?)w.ActiveLicensed).ToList(), 3),
                new Series("Free Copilot Chat only", tr.Select(w => (double?)w.ActiveUnlicensed).ToList(), 2),
            ], Fmt.Count, "Premium license holders, how many of them used Copilot, and people on the free Copilot Chat"),
            $"Premium license holders: holding a Premium Copilot license at the end of the day or week. {ToEndNote} Used Copilot with a Premium license: holders who used it with the license that day or week. Free Copilot Chat only: people without the license who used the free Copilot Chat. {TrendNote}"));
        Panels.Add(new("Adoption rate: Premium license holders",
            Chart.Line(x, [new Series("Adoption", tr.Select(w => Ratio(adopted.GetValueOrDefault(w.Bucket), w.Licensed)).ToList(), 1)], Fmt.Percent, "Adoption rate of Premium license holders"),
            $"Premium license holders only. Of the people holding a Premium license at each point, the share who used Copilot in the {Metrics.AdoptionDays} days up to it. " +
            $"A rolling {Metrics.AdoptionDays} days, so weekends and holidays do not dip it; how many used it each day is in People using Copilot. " +
            $"The first {Metrics.AdoptionDays} days of the data read low, with less than {Metrics.AdoptionDays} days behind them.", "half"));
        Panels.Add(new("Interactions",
            Chart.Columns(x, [new Series("Interactions", tr.Select(w => (double?)w.Interactions).ToList(), 1)], Fmt.Count, "Interactions"),
            $"An interaction is one prompt to Copilot and its answer. Counts all use, licensed or not, including by people whose accounts are now disabled. {TrendNote}", "half"));

        var units = FoldSmall(await unitsTask, u => u.People, r => Metrics.Combine(r, "Smaller groups"), out var folded);
        Panels.Add(new(Ctx.Root is null && Ctx.OrgTopUpn is null ? "Adoption by division" : "Adoption in each part of the organization",
            Chart.Adoption(units.Select(u => new Chart.AdoptionGroup(u.Name, u.ActiveLicensed, u.Licensed, u.Drillable ? Ctx.Url(Request.Path, org: u.Unit) : null)),
                Ctx.MinGroupSize, "Adoption by unit", 40),
            $"The share of Premium license holders who used Copilot with the license in the period, for each part of the organization one level below. Parts with fewer than {Ctx.MinGroupSize} license holders are combined in the last bar. Select a name to look inside it. " +
            FoldNote(folded, "unit")));
    }
}

public sealed class OrgModel(PageServices s, Metrics m) : InsightsPage(s)
{
    public override string Title => "Organization";
    public override string Persona => "Leadership";
    public override string Intro => "Each part of the organization one level below, side by side. Select a name to look inside it.";

    protected override async Task BuildAsync()
    {
        var unitsTask = m.UnitsAsync(Ctx);
        var trendTask = m.UnitsTrendAsync(Ctx);
        await Task.WhenAll(unitsTask, trendTask);
        var units = await unitsTask;
        var trend = await trendTask;
        /* Each unit's people at each point, looked up: the table's trends and the heat map read every unit at every
           point, and a pass over all the trend's rows for each cell grew with units times points times rows. */
        var active = trend.GroupBy(r => (r.Unit, r.Bucket)).ToDictionary(g => g.Key, g => g.Sum(r => r.Active));
        int ActiveAt(string unit, DateTime bucket) => active.GetValueOrDefault((unit, bucket));
        var buckets = Buckets();
        var axis = Labels();
        var shown = FoldSmall(units, u => u.People, r => Metrics.Combine(r, "Smaller groups"), out var folded);
        /* The page had no headline: how many parts, and the two ends of adoption among those large enough to rank. */
        var ranked = shown.Where(u => u.Unit != "~small" && u.Licensed >= Ctx.MinGroupSize).OrderByDescending(u => u.ActiveLicensed / (double)u.Licensed).ToList();
        Kpis.Add(new("Units", Num(shown.Count(u => u.Unit != "~small")), $"one level below {Here}"));
        if (ranked.Count > 1)
        {
            /* Each opens that unit, as its name in the table does. */
            string? Inside(UnitRow u) => u.Drillable ? Ctx.Url(Request.Path, org: u.Unit) : null;
            Kpis.Add(new("Unit with the highest adoption", ranked[0].Name, $"{Pct(Ratio(ranked[0].ActiveLicensed, ranked[0].Licensed))} of its license holders used Copilot in the period", Href: Inside(ranked[0])));
            Kpis.Add(new("Unit with the lowest adoption", ranked[^1].Name, $"{Pct(Ratio(ranked[^1].ActiveLicensed, ranked[^1].Licensed))} of its license holders used Copilot in the period", Href: Inside(ranked[^1])));
        }

        var t = new Table
        {
            Headers = ["Unit", "Department", "People", "License holders", "Used Copilot", "Adoption", "Days active per person", "Interactions per active person", "Sessions with a follow-up", "Trend (people using Copilot)"],
            Numeric = new HashSet<int> { 2, 3, 4, 5, 6, 7, 8 },
        };
        foreach (var u in shown)
        {
            var series = buckets.Select(b => (double?)ActiveAt(u.Unit, b.Key)).ToList();
            t.Rows.Add([
                u.Drillable ? new Link(u.Name, Ctx.Url(Request.Path, org: u.Unit)) : u.Name, u.Department, u.People, u.Licensed, u.Active,
                AdoptionAtMin(u.ActiveLicensed, u.Licensed), u.Active > 0 ? Math.Round(u.ActiveDays / (double)u.Active, 1) : null,
                u.Active > 0 ? Math.Round(u.Interactions / (double)u.Active, 1) : null, Pct(Ratio(u.MultiTurn, u.Sessions)),
                u.Unit == "~small" ? null : Chart.Sparkline(axis, series, $"{u.Name}: active people"),
            ]);
        }
        t.KeepLast = folded > 0 ? 1 : 0;
        CsvTable = t;
        Panels.Add(new("Units", WithTrendNote(t.Render()),
            "Used Copilot: people who had an interaction in the period. Adoption: the share of license holders who used Copilot with the license. " +
            "Interactions per active person counts all use, including by people since disabled." + (folded > 0 ? " " + FoldNote(folded, "unit") : ""), "full", CsvUrl()));

        var big = shown.Where(u => u.Unit != "~small").ToList();
        var grid = new double?[big.Count, buckets.Count];
        for (var r = 0; r < big.Count; r++)
            for (var c = 0; c < buckets.Count; c++)
            {
                var a = ActiveAt(big[r].Unit, buckets[c].Key);
                grid[r, c] = big[r].People > 0 ? a / (double)big[r].People : null;
            }
        Panels.Add(new("Share of people active",
            Chart.Heatmap(big.Select(u => u.Name).ToList(), axis, grid, Fmt.Percent, "Share of each unit active", 1, unit: "of its people used Copilot"),
            $"Darker is a larger share of the unit's people using Copilot. {TrendNote}"));
    }
}

public sealed class MixModel(PageServices s, Metrics m) : InsightsPage(s)
{
    public override string Title => "Apps and agents";
    public override string Persona => "Leadership";
    public override string Intro => "Where people use Copilot: which Microsoft 365 apps, the Copilot Chat app or inside other apps, and how much through agents. Copilot Chat here is the chat app, with a Premium license or without one.";

    protected override async Task BuildAsync()
    {
        var trTask = m.TrendAsync(Ctx);
        var totalsTask = m.TotalsAsync(Ctx);
        /* Each person's use of each agent and app is read from the nightly totals (Ctx.PerItem, Ctx.PerItemPoint), not
           counted from the 160 million interactions: a few hundred thousand rows a period instead of a scan of the fact.
           The agents are listed with their names first: joined straight to dash.Agent, a view, its names were worked
           out row by row. An agent's people are counted on its own key, where each person has one row; a name that
           several keys share counts each person once, from their rows of all of them. */
        var agentsTask = Db.QueryAsync<(string AgentName, long N, int People)>($@"
SELECT ag.AgentKey, ag.AgentName, COUNT(*) OVER (PARTITION BY ag.AgentName) AS Keys INTO #ag FROM dash.Agent AS ag WHERE ag.AgentSource <> N'(no agent)';
WITH g AS (SELECT it.Item, it.UserKey, it.Interactions FROM {Ctx.PerItem('G')} AS it {Ctx.Scope("it.UserKey")})
SELECT ag.AgentName, SUM(x.N) AS N, SUM(x.People) AS People   -- people: enabled accounts (3.1.3)
FROM (SELECT g.Item, SUM(CAST(g.Interactions AS BIGINT)) AS N, COUNT(CASE WHEN {CopilotInsights.Infrastructure.ReportContext.IsEnabled()} THEN 1 END) AS People
      FROM g {CopilotInsights.Infrastructure.ReportContext.EnabledJoin("g.UserKey", left: true)}
      WHERE g.Item IN (SELECT AgentKey FROM #ag WHERE Keys = 1)
      GROUP BY g.Item) AS x
JOIN #ag AS ag ON ag.AgentKey = x.Item
GROUP BY ag.AgentName
UNION ALL
SELECT ag.AgentName, SUM(CAST(g.Interactions AS BIGINT)), COUNT(DISTINCT CASE WHEN {CopilotInsights.Infrastructure.ReportContext.IsEnabled()} THEN g.UserKey END)
FROM g JOIN #ag AS ag ON ag.AgentKey = g.Item AND ag.Keys > 1
{CopilotInsights.Infrastructure.ReportContext.EnabledJoin("g.UserKey", left: true)}
GROUP BY ag.AgentName
ORDER BY 2 DESC;", Ctx.Args());
        var rows = await Db.QueryAsync<(DateTime Bucket, string App, long N)>($@"
WITH g AS (SELECT pt.Bucket, pt.Item AS AppKey, SUM(CAST(pt.Interactions AS BIGINT)) AS N
           FROM {Ctx.PerItemPoint('A')} AS pt {Ctx.Scope("pt.UserKey")}
           GROUP BY pt.Bucket, pt.Item)
SELECT g.Bucket, COALESCE(vn.Name, a.App), SUM(g.N)
FROM g JOIN bi.DimApp AS a ON a.AppKey = g.AppKey
LEFT JOIN dash.ValueName AS vn ON vn.ColumnId = 2 AND vn.Value = a.App   -- 2.7.1: the app's name, as Apps shows it
GROUP BY g.Bucket, COALESCE(vn.Name, a.App);", Ctx.Args());
        /* Overview's Interactions opens this page, which had no figure of its own; it now leads with the same one. */
        var t = await totalsTask;
        var trAll = await trTask;
        long chat = trAll.Sum(w => w.Chat), viaAgent = trAll.Sum(w => w.Agent), counted = trAll.Sum(w => w.Interactions);
        Kpis.Add(new("Interactions", Num(t.Interactions), t.Active > 0 ? $"{t.Interactions / (double)t.Active:#,0.0} per person using Copilot; includes use by accounts since disabled" : null));
        Kpis.Add(new("In Copilot Chat", Pct(Ratio(chat, counted)), "of interactions were in the Copilot Chat app; the rest inside other apps"));
        Kpis.Add(new("Through an agent", Pct(Ratio(viaAgent, counted)), "of interactions were answered by an agent"));
        var buckets = Buckets();
        var x = Labels();
        var top = rows.GroupBy(r => r.App).OrderByDescending(g => g.Sum(r => r.N)).Select(g => g.Key).Take(6).ToList();
        var series = top.Select((app, i) => new Series(app, buckets.Select(b => (double?)rows.Where(r => r.App == app && r.Bucket == b.Key).Sum(r => r.N)).ToList(), i + 1)).ToList();
        if (rows.Any(r => !top.Contains(r.App)))
            series.Add(new Series("Other apps", buckets.Select(b => (double?)rows.Where(r => !top.Contains(r.App) && r.Bucket == b.Key).Sum(r => r.N)).ToList(), 8));
        Panels.Add(new("Interactions by app", Chart.Columns(x, series, Fmt.Count, "Interactions by app"),
            $"The six busiest apps; the rest are combined. An app shown by a code instead of a name can be named by an administrator in Admin › Value names. {TrendNote}"));

        var tr = await trTask;
        Panels.Add(new("Copilot Chat and agents, share of interactions",
            Chart.Line(x, [
                new Series("Copilot Chat", tr.Select(w => Ratio(w.Chat, w.Interactions)).ToList(), 1),
                new Series("Through an agent", tr.Select(w => Ratio(w.Agent, w.Interactions)).ToList(), 2),
            ], Fmt.Percent, "Copilot Chat and agent share"),
            $"Copilot Chat: interactions in the Copilot Chat app, with a Premium license or without. Through an agent: interactions an agent answered. {TrendNote}", "half"));

        var byApp = rows.GroupBy(r => r.App).Select(g => new Bar(g.Key, g.Sum(r => r.N))).OrderByDescending(b => b.Value).ToList();
        Panels.Add(new("Interactions by app, whole period", Chart.Bars(byApp, Fmt.Count, "Interactions by app"), null, "half"));

        var agents = await agentsTask;
        Panels.Add(new("Most-used agents", Chart.Bars(agents.Take(10).Select(a => new Bar(a.AgentName, a.N, Note: AgentPeople(a.People))).ToList(), Fmt.Count, "Interactions by agent"),
            $"Each bar is an agent's interactions in the period. Agents' friendly names come from Admin › Reference data. How many people used an agent is shown only where it is {Ctx.MinGroupSize} or more."));
    }
}
