using CopilotInsights.Charts;
using CopilotInsights.Data;

namespace CopilotInsights.Pages.Regions;

public sealed class IndexModel(PageServices s, Metrics m) : InsightsPage(s)
{
    public override string Title => "Countries and offices";
    public override string Persona => "Regions";
    public override string Intro => "Adoption by country, city and office, from Active Directory: where Copilot has landed and where it has not.";

    protected override async Task BuildAsync()
    {
        var countries = Groups.Fold(this, await m.ByAttributeAsync(Ctx, "Country"), Ctx.MinGroupSize, out var fc);
        var offices = Groups.Fold(this, await m.ByAttributeAsync(Ctx, "Office"), Ctx.MinGroupSize, out var fo);
        var top = countries.Where(c => c.Licensed > 0).OrderByDescending(c => c.ActiveLicensed / (double)c.Licensed).FirstOrDefault();
        var low = countries.Where(c => c.Licensed >= Ctx.MinGroupSize).OrderBy(c => c.ActiveLicensed / (double)c.Licensed).FirstOrDefault();
        Kpis.Add(new("Countries", Num(countries.Count(c => c.Name != "Smaller groups")), $"{offices.Count(o => o.Name != "Smaller groups"):#,0} offices"));
        if (top is not null) Kpis.Add(new("Country with the highest adoption", top.Name, Pct(Ratio(top.ActiveLicensed, top.Licensed))));
        if (low is not null) Kpis.Add(new("Country with the lowest adoption", low.Name, Pct(Ratio(low.ActiveLicensed, low.Licensed))));

        Panels.Add(new("Adoption by country", Groups.AdoptionBars(countries, "Adoption by country", Ctx.MinGroupSize),
            "Licensed people who used Copilot in the period." + (fc > 0 ? " " + FoldNote(fc, "country", "countries") : ""), "half"));
        Panels.Add(new("Adoption by office", Groups.AdoptionBars(offices, "Adoption by office", Ctx.MinGroupSize, 15),
            "The fifteen offices with the highest adoption." + (fo > 0 ? " " + FoldNote(fo, "office") : ""), "half"));

        var trend = await Db.QueryAsync<(string Country, DateTime Bucket, int Active)>($@"
SELECT ISNULL(NULLIF(du.Country, N''), N'(no country)'), {Ctx.BucketSql("d")}, COUNT(DISTINCT ud.UserKey)
FROM dash.UserDay AS ud {Ctx.Scope("ud.UserKey")}
JOIN bi.DimUser AS du ON du.UserKey = ud.UserKey
JOIN bi.DimDate AS d ON d.DateKey = ud.DateKey
WHERE ud.DateKey BETWEEN @k0 AND @k1 AND ud.Interactions > 0
GROUP BY ISNULL(NULLIF(du.Country, N''), N'(no country)'), {Ctx.BucketSql("d")};", Ctx.Args());
        var buckets = Buckets();
        var shown = countries.Where(c => c.Name != "Smaller groups").OrderByDescending(c => c.People).Take(5).ToList();
        Panels.Add(new("People using Copilot, by country",
            Chart.Line(Labels(), shown.Select((c, i) => new Series(c.Name, buckets.Select(b => (double?)trend.Where(t => t.Country == c.Name && t.Bucket == b.Key).Sum(t => t.Active)).ToList(), i + 1)).ToList(),
                Fmt.Count, "Active people by country"), $"The five countries with the most people. {TrendNote}"));
        var t = Groups.Table(countries, "Country", Ctx.MinGroupSize);
        CsvTable = t;
        Panels.Add(new("Every country", t.Render(), null, "full", CsvUrl()));
        Panels.Add(new("Every office", Groups.Table(offices, "Office", Ctx.MinGroupSize).Render()));
    }
}

public sealed class PatternsModel(PageServices s) : InsightsPage(s)
{
    public override string Title => "Working patterns";
    public override string Persona => "Regions";
    public override string Intro => $"When people use Copilot: by hour and weekday, in {DataZone}, and how much of it falls outside working hours.";

    protected override async Task BuildAsync()
    {
        var grid = await Db.QueryAsync<(int Dow, int Hour, long N)>($@"
SELECT d.DayOfWeekNumber, f.HourOfDay, COUNT_BIG(*)
FROM bi.FactInteraction AS f {Ctx.Scope("f.UserKey")}
JOIN bi.DimDate AS d ON d.DateKey = f.DateKey
WHERE f.DateKey BETWEEN @k0 AND @k1
GROUP BY d.DayOfWeekNumber, f.HourOfDay;", Ctx.Args());
        long all = grid.Sum(g => g.N);
        long after = grid.Where(g => g.Dow >= 6 || g.Hour < 8 || g.Hour >= 18).Sum(g => g.N);
        long weekend = grid.Where(g => g.Dow >= 6).Sum(g => g.N);
        var peak = grid.OrderByDescending(g => g.N).FirstOrDefault();
        string[] days = ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"];
        Kpis.Add(new("Outside 08:00-18:00 or at weekends", Pct(Ratio(after, all)), "of interactions"));
        Kpis.Add(new("At weekends", Pct(Ratio(weekend, all)), "of interactions, Saturday and Sunday"));
        if (all > 0) Kpis.Add(new("Busiest hour", $"{days[peak.Dow - 1]} {peak.Hour:00}:00", $"{Num(peak.N)} interactions"));

        var cells = new double?[7, 24];
        foreach (var g in grid) cells[g.Dow - 1, g.Hour] = g.N;
        Panels.Add(new("Interactions by weekday and hour", Chart.Heatmap(days, Enumerable.Range(0, 24).Select(h => h.ToString("00")).ToList(), cells, Fmt.Count, "Interactions by weekday and hour",
            unit: "interactions", unitOne: "interaction", column: h => $"{h}:00 to {h}:59"),
            $"Darker is busier. Hours are in {DataZone} for everyone, so offices in other time zones appear shifted."));

        var trend = await Db.QueryAsync<(DateTime Bucket, long All, long After)>($@"
SELECT {Ctx.BucketSql("d")}, COUNT_BIG(*), SUM(CASE WHEN d.DayOfWeekNumber >= 6 OR f.HourOfDay < 8 OR f.HourOfDay >= 18 THEN 1 ELSE 0 END)
FROM bi.FactInteraction AS f {Ctx.Scope("f.UserKey")}
JOIN bi.DimDate AS d ON d.DateKey = f.DateKey
WHERE f.DateKey BETWEEN @k0 AND @k1
GROUP BY {Ctx.BucketSql("d")};", Ctx.Args());
        var buckets = Buckets();
        Panels.Add(new("Share outside working hours",
            Chart.Line(Labels(), [new Series("Outside hours", buckets.Select(b => { var r = trend.FirstOrDefault(t => t.Bucket == b.Key); return Ratio(r.After, r.All); }).ToList(), 2)], Fmt.Percent, "After-hours share"),
            $"Interactions before 08:00, from 18:00, or at weekends. {TrendNote}", "half"));

        var apps = await Db.QueryAsync<(string App, int Hour, long N)>($@"
WITH g AS (SELECT f.AppKey, f.HourOfDay, COUNT_BIG(*) AS N FROM bi.FactInteraction AS f {Ctx.Scope("f.UserKey")}
           WHERE f.DateKey BETWEEN @k0 AND @k1 GROUP BY f.AppKey, f.HourOfDay)   -- 2.7.4: counted per key, then named
SELECT COALESCE(vn.Name, a.App), g.HourOfDay, SUM(g.N)
FROM g JOIN bi.DimApp AS a ON a.AppKey = g.AppKey
LEFT JOIN dash.ValueName AS vn ON vn.ColumnId = 2 AND vn.Value = a.App   -- 2.7.1: the app's name
GROUP BY COALESCE(vn.Name, a.App), g.HourOfDay;", Ctx.Args());
        var topApps = apps.GroupBy(a => a.App).OrderByDescending(g => g.Sum(x => x.N)).Take(4).Select(g => g.Key).ToList();
        var hours = Enumerable.Range(0, 24).Select(h => h.ToString("00")).ToList();
        Panels.Add(new("The day's shape, by app",
            Chart.Line(hours, topApps.Select((app, i) => { var tot = apps.Where(a => a.App == app).Sum(a => a.N);
                return new Series(app, Enumerable.Range(0, 24).Select(h => Ratio(apps.Where(a => a.App == app && a.Hour == h).Sum(a => a.N), tot)).ToList(), i + 1); }).ToList(),
                Fmt.Percent, "Share of each app's interactions by hour"),
            "Each of the four busiest apps' interactions by hour of day, as a share of its day.", "half"));
    }
}
