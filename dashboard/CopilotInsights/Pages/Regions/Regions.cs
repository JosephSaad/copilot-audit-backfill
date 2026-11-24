using CopilotInsights.Charts;
using CopilotInsights.Data;

namespace CopilotInsights.Pages.Regions;

public sealed class IndexModel(PageServices s, Metrics m) : InsightsPage(s)
{
    public override string Title => "Countries and offices";
    public override string Persona => "Regions";
    public override string Intro => "Adoption by country, city and office, from Active Directory: where Copilot has landed and where it has not. Adoption: of the license holders with an enabled account, the share who used Copilot with the license in the period.";

    protected override async Task BuildAsync()
    {
        /* The three reads are independent: side by side. The people active at each point are each person's sums at that
           point (a day, or a week the refresh summed), one row a person, where each point's people were a distinct count
           over its days. */
        var (byCountry, byOffice, trend) = await Queries(m.ByAttributeAsync(Ctx, "Country"), m.ByAttributeAsync(Ctx, "Office"),
            Db.QueryAsync<(string Country, DateTime Bucket, int Active)>($@"
SELECT ISNULL(NULLIF(du.Country, N''), N'(no country)'), p.Bucket, COUNT(*)
FROM {Ctx.PerPoint()} AS p {Ctx.Scope("p.UserKey")}
JOIN bi.DimUser AS du ON du.UserKey = p.UserKey
WHERE p.ActiveDays > 0 AND du.AccountEnabled = 1
GROUP BY ISNULL(NULLIF(du.Country, N''), N'(no country)'), p.Bucket;", Ctx.Args()));
        var countries = Groups.Fold(this, byCountry, Ctx.MinGroupSize, out var fc);
        var offices = Groups.Fold(this, byOffice, Ctx.MinGroupSize, out var fo);
        /* Both ends among the countries large enough to rank, as Departments and Organization rank theirs (the highest
           could be a country of one license holder, or the combined smaller ones). */
        var ranked = countries.Where(c => c.Name != "Smaller groups" && c.Licensed >= Ctx.MinGroupSize).OrderByDescending(c => c.ActiveLicensed / (double)c.Licensed).ToList();
        Kpis.Add(new("Countries", Num(countries.Count(c => c.Name != "Smaller groups")), $"{offices.Count(o => o.Name != "Smaller groups"):#,0} offices"));
        if (ranked.Count > 1)
        {
            Kpis.Add(new("Country with the highest adoption", ranked[0].Name, $"{Pct(Ratio(ranked[0].ActiveLicensed, ranked[0].Licensed))}, of countries with {Ctx.MinGroupSize} or more license holders"));
            Kpis.Add(new("Country with the lowest adoption", ranked[^1].Name, $"{Pct(Ratio(ranked[^1].ActiveLicensed, ranked[^1].Licensed))}, of countries with {Ctx.MinGroupSize} or more license holders"));
        }

        Panels.Add(new("Adoption by country", Groups.AdoptionBars(countries, "Adoption by country", Ctx.MinGroupSize),
            "License holders with an enabled account who used Copilot with the license in the period, as a share of all license holders there." + (fc > 0 ? " " + FoldNote(fc, "country", "countries") : ""), "half"));
        Panels.Add(new("Adoption by office", Groups.AdoptionBars(offices, "Adoption by office", Ctx.MinGroupSize, 15),
            "The fifteen offices with the highest adoption." + (fo > 0 ? " " + FoldNote(fo, "office") : ""), "half"));

        var buckets = Buckets();
        var shown = countries.Where(c => c.Name != "Smaller groups").OrderByDescending(c => c.People).Take(5).ToList();
        Panels.Add(new("People using Copilot, by country",
            Chart.Line(Labels(), shown.Select((c, i) => new Series(c.Name, buckets.Select(b => (double?)trend.Where(t => t.Country == c.Name && t.Bucket == b.Key).Sum(t => t.Active)).ToList(), i + 1)).ToList(),
                Fmt.Count, "Active people by country"), $"People with an enabled account and at least one interaction, in the five countries with the most people. {TrendNote}"));
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
        /* The three reads are independent: side by side, where one after another the page took their sum. Counted
           once per day, hour and app into a table for all three, the one read asked for hundreds of megabytes of memory
           at two years and waited for it on a busy server, slower than the three. */
        var (grid, trend, apps) = await Queries(Db.QueryAsync<(int Dow, int Hour, long N)>($@"
SELECT d.DayOfWeekNumber, f.HourOfDay, COUNT_BIG(*)
FROM bi.FactInteraction AS f {Ctx.Scope("f.UserKey")}
JOIN bi.DimDate AS d ON d.DateKey = f.DateKey
WHERE f.DateKey BETWEEN @k0 AND @k1
GROUP BY d.DayOfWeekNumber, f.HourOfDay;", Ctx.Args()),
            Db.QueryAsync<(DateTime Bucket, long All, long After)>($@"
SELECT {Ctx.BucketSql("d")}, COUNT_BIG(*), SUM(CASE WHEN d.DayOfWeekNumber >= 6 OR f.HourOfDay < 8 OR f.HourOfDay >= 18 THEN 1 ELSE 0 END)
FROM bi.FactInteraction AS f {Ctx.Scope("f.UserKey")}
JOIN bi.DimDate AS d ON d.DateKey = f.DateKey
WHERE f.DateKey BETWEEN @k0 AND @k1
GROUP BY {Ctx.BucketSql("d")};", Ctx.Args()),
            Db.QueryAsync<(string App, int Hour, long N)>($@"
WITH g AS (SELECT f.AppKey, f.HourOfDay, COUNT_BIG(*) AS N FROM bi.FactInteraction AS f {Ctx.Scope("f.UserKey")}
           WHERE f.DateKey BETWEEN @k0 AND @k1 GROUP BY f.AppKey, f.HourOfDay)   -- counted per key, then named
SELECT COALESCE(vn.Name, a.App), g.HourOfDay, SUM(g.N)
FROM g JOIN bi.DimApp AS a ON a.AppKey = g.AppKey
LEFT JOIN dash.ValueName AS vn ON vn.ColumnId = 2 AND vn.Value = a.App   -- the app's name
GROUP BY COALESCE(vn.Name, a.App), g.HourOfDay;", Ctx.Args()));
        long all = grid.Sum(g => g.N);
        long after = grid.Where(g => g.Dow >= 6 || g.Hour < 8 || g.Hour >= 18).Sum(g => g.N);
        long weekend = grid.Where(g => g.Dow >= 6).Sum(g => g.N);
        var peak = grid.OrderByDescending(g => g.N).FirstOrDefault();
        string[] days = ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"];
        Kpis.Add(new("Outside 08:00-18:00 or at weekends", Pct(Ratio(after, all)), "of interactions"));
        Kpis.Add(new("At weekends", Pct(Ratio(weekend, all)), "of interactions, Saturday and Sunday"));
        if (all > 0) Kpis.Add(new("Busiest hour", $"{days[peak.Dow - 1]} {peak.Hour:00}:00", $"{Num(peak.N)} interactions in that hour, over the period"));

        var cells = new double?[7, 24];
        foreach (var g in grid) cells[g.Dow - 1, g.Hour] = g.N;
        Panels.Add(new("Interactions by weekday and hour", Chart.Heatmap(days, Enumerable.Range(0, 24).Select(h => h.ToString("00")).ToList(), cells, Fmt.Count, "Interactions by weekday and hour",
            unit: "interactions", unitOne: "interaction", column: h => $"{h}:00 to {h}:59"),
            $"Darker is busier. Hours are in {DataZone} for everyone, so offices in other time zones appear shifted."));

        var buckets = Buckets();
        Panels.Add(new("Share outside working hours",
            Chart.Line(Labels(), [new Series("Outside hours", buckets.Select(b => { var r = trend.FirstOrDefault(t => t.Bucket == b.Key); return Ratio(r.After, r.All); }).ToList(), 2)], Fmt.Percent, "After-hours share"),
            $"Interactions before 08:00, from 18:00, or at weekends. {TrendNote}", "half"));

        var topApps = apps.GroupBy(a => a.App).OrderByDescending(g => g.Sum(x => x.N)).Take(4).Select(g => g.Key).ToList();
        var hours = Enumerable.Range(0, 24).Select(h => h.ToString("00")).ToList();
        Panels.Add(new("The day's shape, by app",
            Chart.Line(hours, topApps.Select((app, i) => { var tot = apps.Where(a => a.App == app).Sum(a => a.N);
                return new Series(app, Enumerable.Range(0, 24).Select(h => Ratio(apps.Where(a => a.App == app && a.Hour == h).Sum(a => a.N), tot)).ToList(), i + 1); }).ToList(),
                Fmt.Percent, "Share of each app's interactions by hour"),
            "Each of the four busiest apps' interactions by hour of day, as a share of its day.", "half"));
    }
}
