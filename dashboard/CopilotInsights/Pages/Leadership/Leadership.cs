using CopilotInsights.Charts;
using CopilotInsights.Data;

namespace CopilotInsights.Pages.Leadership;

public sealed class IndexModel(PageServices s, Metrics m) : InsightsPage(s)
{
    public override string Title => "Overview";
    public override string Persona => "Leadership";
    public override string Intro => "Is Copilot being used, by how many of the people who have it, and where. Totals only: no one is named.";

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

        Kpis.Add(new("People using Copilot", Num(t.Active), t.People > 0 ? $"of {t.People:#,0} people in {Here}" : "in the period", Href: Open("/licensing/ranking")));
        Kpis.Add(new("Premium licenses today", Num(t.LicensedNow), t.People > 0 ? $"{Pct(Ratio(t.LicensedNow, t.People))} of people" : null, Href: Open("/licensing")));
        Kpis.Add(new("Adoption", Pct(Ratio(t.ActiveLicensed, t.LicensedNow)), "Premium license holders who used Copilot in the period", Href: Open("/adoption")));
        Kpis.Add(new("Active in the last 7 days", Num(t.ActiveLast7), "people who used Copilot, licensed or not", Href: Open("/adoption")));
        Kpis.Add(new("Interactions", Num(t.Interactions), t.Active > 0 ? $"{t.Interactions / (double)t.Active:#,0.0} per active person" : null, Href: Open("/leadership/mix")));

        Panels.Add(new("People using Copilot",
            Chart.Line(x, [
                new Series("Premium license holders", tr.Select(w => (double?)w.Licensed).ToList(), 1, ToEnd: true),
                new Series("Used Copilot, with a license", tr.Select(w => (double?)w.ActiveLicensed).ToList(), 3),
                new Series("Free Copilot Chat only", tr.Select(w => (double?)w.ActiveUnlicensed).ToList(), 2),
            ], Fmt.Count, "Premium license holders, how many of them used Copilot, and people on the free Copilot Chat"),
            $"Premium license holders: holding a Premium Copilot license at the end of the day or week. {ToEndNote} Free Copilot Chat only: people without a license who used the free Copilot Chat. {TrendNote}"));
        Panels.Add(new("Adoption rate: Premium license holders",
            Chart.Line(x, [new Series("Adoption", tr.Select(w => Ratio(adopted.GetValueOrDefault(w.Bucket), w.Licensed)).ToList(), 1)], Fmt.Percent, "Adoption rate of Premium license holders"),
            $"Premium license holders only. Of the people holding a Premium license at each point, the share who used Copilot in the {Metrics.AdoptionDays} days up to it. " +
            $"A rolling {Metrics.AdoptionDays} days, so weekends and holidays do not dip it; how many used it each day is in People using Copilot. " +
            $"The first {Metrics.AdoptionDays} days of the data read low, with less than {Metrics.AdoptionDays} days behind them.", "half"));
        Panels.Add(new("Interactions",
            Chart.Columns(x, [new Series("Interactions", tr.Select(w => (double?)w.Interactions).ToList(), 1)], Fmt.Count, "Interactions"),
            $"An interaction is one prompt to Copilot and its answer, by anyone, licensed or not. {TrendNote}", "half"));

        var units = FoldSmall(await unitsTask, u => u.People, r => Metrics.Combine(r, "Smaller groups"), out var folded);
        Panels.Add(new(Ctx.Root is null && Ctx.OrgTopUpn is null ? "Adoption by division" : "Adoption in each part of the organization",
            Chart.Adoption(units.Select(u => new Chart.AdoptionGroup(u.Name, u.ActiveLicensed, u.Licensed, u.Drillable ? Ctx.Url(Request.Path, org: u.Unit) : null)),
                Ctx.MinGroupSize, "Adoption by unit", 40),
            $"Premium license holders who used Copilot in the period, for each part of the organization one level below. Parts with fewer than {Ctx.MinGroupSize} license holders are combined in the last bar. Select a name to look inside it. " +
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
            Kpis.Add(new("Unit with the highest adoption", ranked[0].Name, $"{Pct(Ratio(ranked[0].ActiveLicensed, ranked[0].Licensed))} of its license holders used Copilot", Href: Inside(ranked[0])));
            Kpis.Add(new("Unit with the lowest adoption", ranked[^1].Name, $"{Pct(Ratio(ranked[^1].ActiveLicensed, ranked[^1].Licensed))} of its license holders used Copilot", Href: Inside(ranked[^1])));
        }

        var t = new Table
        {
            Headers = ["Unit", "Department", "People", "License holders", "Used Copilot", "Adoption", "Days active per person", "Interactions per active person", "Sessions with a follow-up", "Trend (people using Copilot)"],
            Numeric = new HashSet<int> { 2, 3, 4, 5, 6, 7, 8 },
        };
        foreach (var u in shown)
        {
            var series = buckets.Select(b => (double?)trend.Where(r => r.Unit == u.Unit && r.Bucket == b.Key).Sum(r => r.Active)).ToList();
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
            folded > 0 ? FoldNote(folded, "unit") : null, "full", CsvUrl()));

        var big = shown.Where(u => u.Unit != "~small").ToList();
        var grid = new double?[big.Count, buckets.Count];
        for (var r = 0; r < big.Count; r++)
            for (var c = 0; c < buckets.Count; c++)
            {
                var a = trend.Where(x => x.Unit == big[r].Unit && x.Bucket == buckets[c].Key).Sum(x => x.Active);
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
    public override string Intro => "Where people use Copilot: which Microsoft 365 apps, Copilot Chat or inside the apps, and how much through agents.";

    protected override async Task BuildAsync()
    {
        var trTask = m.TrendAsync(Ctx);
        var totalsTask = m.TotalsAsync(Ctx);
        /* Counted per agent and person first, then named: grouped on the names over the fact, 120 million
           interactions took 13.7 s for 90 days; this way 1.6 s, the same rows. */
        var agentsTask = Db.QueryAsync<(string AgentName, long N, int People)>($@"
WITH g AS (SELECT f.AgentKey, f.UserKey, COUNT_BIG(*) AS N
           FROM bi.FactInteraction AS f {Ctx.Scope("f.UserKey")}
           WHERE f.DateKey BETWEEN @k0 AND @k1 AND f.AgentKey IN (SELECT AgentKey FROM dash.Agent WHERE AgentSource <> N'(no agent)')
           GROUP BY f.AgentKey, f.UserKey)
SELECT ag.AgentName, SUM(g.N), COUNT(DISTINCT g.UserKey)
FROM g JOIN dash.Agent AS ag ON ag.AgentKey = g.AgentKey
GROUP BY ag.AgentName ORDER BY 2 DESC;", Ctx.Args());
        var rows = await Db.QueryAsync<(DateTime Bucket, string App, long N)>($@"
WITH g AS (SELECT {Ctx.BucketSql("d")} AS Bucket, f.AppKey, COUNT_BIG(*) AS N
           FROM bi.FactInteraction AS f {Ctx.Scope("f.UserKey")}
           JOIN bi.DimDate AS d ON d.DateKey = f.DateKey
           WHERE f.DateKey BETWEEN @k0 AND @k1
           GROUP BY {Ctx.BucketSql("d")}, f.AppKey)
SELECT g.Bucket, COALESCE(vn.Name, a.App), SUM(g.N)
FROM g JOIN bi.DimApp AS a ON a.AppKey = g.AppKey
LEFT JOIN dash.ValueName AS vn ON vn.ColumnId = 2 AND vn.Value = a.App   -- 2.7.1: the app's name, as Apps shows it
GROUP BY g.Bucket, COALESCE(vn.Name, a.App);", Ctx.Args());
        /* Overview's Interactions opens this page, which had no figure of its own; it now leads with the same one. */
        var t = await totalsTask;
        var trAll = await trTask;
        long chat = trAll.Sum(w => w.Chat), viaAgent = trAll.Sum(w => w.Agent), counted = trAll.Sum(w => w.Interactions);
        Kpis.Add(new("Interactions", Num(t.Interactions), t.Active > 0 ? $"{t.Interactions / (double)t.Active:#,0.0} per active person" : null));
        Kpis.Add(new("In Copilot Chat", Pct(Ratio(chat, counted)), "of interactions; the rest inside the apps"));
        Kpis.Add(new("Through an agent", Pct(Ratio(viaAgent, counted)), "of interactions"));
        var buckets = Buckets();
        var x = Labels();
        var top = rows.GroupBy(r => r.App).OrderByDescending(g => g.Sum(r => r.N)).Select(g => g.Key).Take(6).ToList();
        var series = top.Select((app, i) => new Series(app, buckets.Select(b => (double?)rows.Where(r => r.App == app && r.Bucket == b.Key).Sum(r => r.N)).ToList(), i + 1)).ToList();
        if (rows.Any(r => !top.Contains(r.App)))
            series.Add(new Series("Other apps", buckets.Select(b => (double?)rows.Where(r => !top.Contains(r.App) && r.Bucket == b.Key).Sum(r => r.N)).ToList(), 8));
        Panels.Add(new("Interactions by app", Chart.Columns(x, series, Fmt.Count, "Interactions by app"),
            $"The six busiest apps; the rest are combined. Name an app shown by its code in Admin › Value names. {TrendNote}"));

        var tr = await trTask;
        Panels.Add(new("Copilot Chat and agents, share of interactions",
            Chart.Line(x, [
                new Series("Copilot Chat", tr.Select(w => Ratio(w.Chat, w.Interactions)).ToList(), 1),
                new Series("Through an agent", tr.Select(w => Ratio(w.Agent, w.Interactions)).ToList(), 2),
            ], Fmt.Percent, "Copilot Chat and agent share"), null, "half"));

        var byApp = rows.GroupBy(r => r.App).Select(g => new Bar(g.Key, g.Sum(r => r.N))).OrderByDescending(b => b.Value).ToList();
        Panels.Add(new("Interactions by app, whole period", Chart.Bars(byApp, Fmt.Count, "Interactions by app"), null, "half"));

        var agents = await agentsTask;
        Panels.Add(new("Most-used agents", Chart.Bars(agents.Take(10).Select(a => new Bar(a.AgentName, a.N, Note: AgentPeople(a.People))).ToList(), Fmt.Count, "Interactions by agent"),
            $"Agents' friendly names come from Admin › Reference data. How many people used an agent is shown only where it is {Ctx.MinGroupSize} or more."));
    }
}
