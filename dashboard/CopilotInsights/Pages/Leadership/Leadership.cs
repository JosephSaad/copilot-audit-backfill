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
        var t = await m.TotalsAsync(Ctx);
        var tr = await m.TrendSeriesAsync(Ctx);
        var adopted = await m.AdoptedAsync(Ctx);
        var x = Labels();

        Kpis.Add(new("People using Copilot", Num(t.Active), t.People > 0 ? $"of {t.People:#,0} people in {Here}" : "in the period"));
        Kpis.Add(new("Premium licenses today", Num(t.LicensedNow), t.People > 0 ? $"{Pct(Ratio(t.LicensedNow, t.People))} of people" : null));
        Kpis.Add(new("Adoption", Pct(Ratio(t.ActiveLicensed, t.LicensedNow)), "Premium license holders who used Copilot in the period"));
        Kpis.Add(new("Active in the last 7 days", Num(t.ActiveLast7), t.LicensedNow > 0 ? $"{Pct(Ratio(t.ActiveLast7, t.LicensedNow))} of Premium license holders" : null));
        Kpis.Add(new("Interactions", Num(t.Interactions), t.Active > 0 ? $"{t.Interactions / (double)t.Active:#,0.0} per active person" : null));

        Panels.Add(new("People using Copilot",
            Chart.Line(x, [
                new Series("Premium license holders (as counted)", tr.Shown.Select(w => (double?)w.Licensed).ToList(), 1),
                new Series("Used Copilot, with a license", tr.Average(w => w.ActiveLicensed), 3),
                new Series("Free Copilot Chat only", tr.Average(w => w.ActiveUnlicensed), 2),
            ], Fmt.Average, "Premium license holders, how many of them used Copilot, and people on the free Copilot Chat"),
            $"Premium license holders: holding a Premium Copilot license at the end of the day or week, as counted, not averaged. Free Copilot Chat only: people without a license who used the free Copilot Chat. The two lines of use are averages. {TrendNote}"));
        Panels.Add(new("Adoption rate: Premium license holders",
            Chart.Line(x, [new Series("Adoption", tr.Shown.Select(w => Ratio(adopted.GetValueOrDefault(w.Bucket), w.Licensed)).ToList(), 1)], Fmt.Percent, "Adoption rate of Premium license holders"),
            $"Premium license holders only. Of the people holding a Premium license at each point, the share who used Copilot in the {Metrics.AdoptionDays} days up to it. " +
            $"A rolling {Metrics.AdoptionDays} days, so weekends and holidays do not dip it; how many used it each day is in People using Copilot. " +
            $"The first {Metrics.AdoptionDays} days of the data read low, with less than {Metrics.AdoptionDays} days behind them.", "half"));
        Panels.Add(new("Interactions",
            Chart.Columns(x, [new Series("Interactions", tr.Average(w => w.Interactions), 1)], Fmt.Average, "Interactions, 28-day average"),
            $"An interaction is one prompt to Copilot and its answer, by anyone, licensed or not. {TrendNote}", "half"));

        var units = FoldSmall(await m.UnitsAsync(Ctx), u => u.People, r => Metrics.Combine(r, "Smaller groups"), out var folded);
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
        var units = await m.UnitsAsync(Ctx);
        var activeAt = (await m.UnitsTrendAsync(Ctx)).ToLookup(r => (r.Unit, r.Bucket), r => r.Active);
        /* A unit's people active at each point, as the 28-day average (2.5.0), for its sparkline and the heatmap. */
        List<double?> UnitActive(string unit) => Average(b => activeAt[(unit, b.Key)].Sum());
        var buckets = Buckets();
        var shown = FoldSmall(units, u => u.People, r => Metrics.Combine(r, "Smaller groups"), out var folded);

        var t = new Table
        {
            Headers = ["Unit", "Department", "People", "License holders", "Used Copilot", "Adoption", "Days active per person", "Interactions per active person", "Sessions with a follow-up", "Trend (people using Copilot, 28-day average)"],
            Numeric = new HashSet<int> { 2, 3, 4, 5, 6, 7, 8 },
        };
        foreach (var u in shown)
        {
            var series = UnitActive(u.Unit);
            t.Rows.Add([
                u.Drillable ? new Link(u.Name, Ctx.Url(Request.Path, org: u.Unit)) : u.Name, u.Department, u.People, u.Licensed, u.Active,
                AdoptionAtMin(u.ActiveLicensed, u.Licensed), u.Active > 0 ? Math.Round(u.ActiveDays / (double)u.Active, 1) : null,
                u.Active > 0 ? Math.Round(u.Interactions / (double)u.Active, 1) : null, Pct(Ratio(u.MultiTurn, u.Sessions)),
                u.Unit == "~small" ? null : Chart.Sparkline(series, $"{u.Name}: active people"),
            ]);
        }
        t.KeepLast = folded > 0 ? 1 : 0;
        Panels.Add(new("Units", t.Render(),
            $"Trend: the average number of people using Copilot {PerPoint}, over the 28 days up to each point." + (folded > 0 ? " " + FoldNote(folded, "unit") : "")));

        var big = shown.Where(u => u.Unit != "~small").ToList();
        var grid = new double?[big.Count, buckets.Count];
        for (var r = 0; r < big.Count; r++)
        {
            var active = UnitActive(big[r].Unit);
            for (var c = 0; c < buckets.Count; c++)
                grid[r, c] = big[r].People > 0 && active[c] is double a ? a / big[r].People : null;
        }
        Panels.Add(new("Share of people active",
            Chart.Heatmap(big.Select(u => u.Name).ToList(), Labels(), grid, Fmt.Percent, "Share of each unit active", 1, unit: "of its people used Copilot"),
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
        /* The period's own totals choose the busiest apps and draw the bars; the columns are 28-day averages, which read
           the days before the period too (2.5.0). */
        var period = (await Db.QueryAsync<(string App, long N)>($@"
SELECT a.App, COUNT_BIG(*)
FROM bi.FactInteraction AS f {Ctx.Scope("f.UserKey")}
JOIN bi.DimApp AS a ON a.AppKey = f.AppKey
WHERE f.DateKey BETWEEN @k0 AND @k1
GROUP BY a.App;", Ctx.Args())).OrderByDescending(r => r.N).ToList();
        var rows = await Db.QueryAsync<(DateTime Bucket, string App, long N)>($@"
SELECT {Ctx.BucketSql("d")}, a.App, COUNT_BIG(*)
FROM bi.FactInteraction AS f {Ctx.Scope("f.UserKey")}
JOIN bi.DimDate AS d ON d.DateKey = f.DateKey
JOIN bi.DimApp AS a ON a.AppKey = f.AppKey
WHERE f.DateKey BETWEEN @kt0 AND @k1
GROUP BY {Ctx.BucketSql("d")}, a.App;", Ctx.Args());
        var x = Labels();
        var top = period.Take(6).Select(r => r.App).ToList();
        var appAt = rows.ToLookup(r => (r.App, r.Bucket), r => r.N);
        var otherAt = rows.Where(r => !top.Contains(r.App)).ToLookup(r => r.Bucket, r => r.N);
        var series = top.Select((app, i) => new Series(app, Average(b => appAt[(app, b.Key)].Sum()), i + 1)).ToList();
        /* "Other apps" where the trend has an app outside the six, which includes one used only before the period (the average reads those days). */
        if (otherAt.Count > 0)
            series.Add(new Series("Other apps", Average(b => otherAt[b.Key].Sum()), 8));
        Panels.Add(new("Interactions by app", Chart.Columns(x, series, Fmt.Average, "Interactions by app, 28-day average"),
            $"The six busiest apps; the rest are combined. Office and BizChat are Copilot Chat. {TrendNote}"));

        var tr = await m.TrendSeriesAsync(Ctx);
        Panels.Add(new("Copilot Chat and agents, share of interactions",
            Chart.Line(x, [
                new Series("Copilot Chat", tr.AverageRatio(w => w.Chat, w => w.Interactions), 1),
                new Series("Through an agent", tr.AverageRatio(w => w.Agent, w => w.Interactions), 2),
            ], Fmt.Percent, "Copilot Chat and agent share"), RatioNote, "half"));

        var byApp = period.Select(r => new Bar(r.App, r.N)).ToList();
        Panels.Add(new("Interactions by app, whole period", Chart.Bars(byApp, Fmt.Count, "Interactions by app"), null, "half"));

        var agents = await Db.QueryAsync<(string AgentName, long N, int People)>($@"
SELECT ag.AgentName, COUNT_BIG(*), COUNT(DISTINCT f.UserKey)
FROM bi.FactInteraction AS f {Ctx.Scope("f.UserKey")}
JOIN dash.Agent AS ag ON ag.AgentKey = f.AgentKey
WHERE f.DateKey BETWEEN @k0 AND @k1 AND ag.AgentSource <> N'(no agent)'
GROUP BY ag.AgentName ORDER BY 2 DESC;", Ctx.Args());
        Panels.Add(new("Most-used agents", Chart.Bars(agents.Take(10).Select(a => new Bar(a.AgentName, a.N, Note: AgentPeople(a.People))).ToList(), Fmt.Count, "Interactions by agent"),
            $"Agents' friendly names come from Admin › Reference data. How many people used an agent is shown only where it is {Ctx.MinGroupSize} or more."));
    }
}
