using CopilotInsights.Charts;
using CopilotInsights.Infrastructure;

namespace CopilotInsights.Pages.Licensing;

/// <summary>
/// How licenses move: how long people held one before it was taken away, whether it was
/// taken from someone still using it, whether it came back, and who made the changes.
/// All from the directory's own audit records (bi.FactLicenseChange): no extra permission.
/// </summary>
public sealed class ChangesModel(PageServices s) : InsightsPage(s)
{
    public override string Title => "License changes";
    public override string Persona => "Licensing";
    public override string Intro => "Every Copilot license assigned and removed in the period, from the directory's audit records: how long people had held a license when it was removed, whether they had used Copilot in the 30 days before, and whether it came back.";

    sealed class Removal
    {
        public int UserKey { get; set; }
        public string? Department { get; set; }
        public DateTime ChangedUtc { get; set; }
        public DateTime? SinceUtc { get; set; }
        public bool ActiveBefore { get; set; }
        public bool GivenBack { get; set; }
    }

    protected override async Task BuildAsync()
    {
        var copilot = "JOIN bi.DimSku AS k ON k.SkuKey = f.SkuKey AND k.IsCopilot = 1";
        var counts = (await Db.QueryAsync<(int Added, int Removed, int People)>($@"
SELECT ISNULL(SUM(CASE WHEN f.Change > 0 THEN 1 ELSE 0 END), 0), ISNULL(SUM(CASE WHEN f.Change < 0 THEN 1 ELSE 0 END), 0), COUNT(DISTINCT f.UserKey)
FROM bi.FactLicenseChange AS f {Ctx.Scope("f.UserKey")} {copilot}
WHERE f.DateKey BETWEEN @k0 AND @k1;", Ctx.Args())).First();

        /* Each removal: when that license was assigned, whether they used Copilot in the 30 days before, and whether a license came back later. */
        var removals = (await Db.QueryAsync<Removal>($@"
SELECT f.UserKey, NULLIF(u.Department, N'') AS Department, f.ChangedUtc, a.SinceUtc,
       CAST(CASE WHEN EXISTS (SELECT 1 FROM dash.UserDay AS ud JOIN bi.DimDate AS dd ON dd.DateKey = ud.DateKey
                              WHERE ud.UserKey = f.UserKey AND ud.Interactions > 0
                                AND dd.[Date] BETWEEN DATEADD(DAY, -30, CAST(f.ChangedUtc AS DATE)) AND CAST(f.ChangedUtc AS DATE)) THEN 1 ELSE 0 END AS BIT) AS ActiveBefore,
       CAST(CASE WHEN EXISTS (SELECT 1 FROM bi.FactLicenseChange AS b JOIN bi.DimSku AS bk ON bk.SkuKey = b.SkuKey AND bk.IsCopilot = 1
                              WHERE b.UserKey = f.UserKey AND b.Change > 0 AND b.ChangedUtc > f.ChangedUtc) THEN 1 ELSE 0 END AS BIT) AS GivenBack
FROM bi.FactLicenseChange AS f {Ctx.Scope("f.UserKey")} {copilot}
JOIN bi.DimUser AS u ON u.UserKey = f.UserKey
OUTER APPLY (SELECT TOP (1) p.ChangedUtc AS SinceUtc FROM bi.FactLicenseChange AS p
             WHERE p.UserKey = f.UserKey AND p.SkuKey = f.SkuKey AND p.Change > 0 AND p.ChangedUtc <= f.ChangedUtc
             ORDER BY p.ChangedUtc DESC) AS a
WHERE f.DateKey BETWEEN @k0 AND @k1 AND f.Change < 0;", Ctx.Args())).ToList();

        var tenures = removals.Where(r => r.SinceUtc is not null).Select(r => (r.ChangedUtc - r.SinceUtc!.Value).TotalDays).OrderBy(d => d).ToList();
        double? median = tenures.Count == 0 ? null : tenures[tenures.Count / 2];
        var active = removals.Count(r => r.ActiveBefore);
        var back = removals.Count(r => r.GivenBack);

        Kpis.Add(new("Licenses assigned", Num(counts.Added), $"{Num(counts.People)} accounts had a license assigned or removed, disabled ones included"));
        Kpis.Add(new("Licenses removed", Num(counts.Removed), "Copilot licenses taken away in the period"));
        Kpis.Add(new("Removed while in use", Num(active), removals.Count > 0 ? $"{Pct(Ratio(active, removals.Count))} of removals: the holder used Copilot in the 30 days before" : null, active > 0 ? "warn" : null));
        Kpis.Add(new("Given back later", Num(back), "removed in the period, then assigned again later"));
        if (median is not null) Kpis.Add(new("Median time held", $"{median:0} days", "from assignment to removal, where the assignment is in the license history"));

        if (removals.Count == 0)
        {
            Notice = "No Copilot licenses were removed in the period.";
        }
        else
        {
            (string Label, double Lo, double Hi)[] bands = [("Under 30 days", 0, 30), ("30-89 days", 30, 90), ("90-179 days", 90, 180), ("180-364 days", 180, 365), ("A year or more", 365, double.MaxValue)];
            var bars = bands.Select((b, i) => new Bar(b.Label, removals.Count(r => r.SinceUtc is not null && (r.ChangedUtc - r.SinceUtc.Value).TotalDays is var d && d >= b.Lo && d < b.Hi), Slot: 1)).ToList();
            var unknown = removals.Count(r => r.SinceUtc is null);
            if (unknown > 0) bars.Add(new Bar("Assigned before the license history starts", unknown, Slot: 3));
            Panels.Add(new("How long the license was held", Chart.Bars(bars, Fmt.Count, "Removals by time held"),
                "Time from the license's assignment to its removal. A license removed within 30 days usually means it went to the wrong person.", "half"));

            var inUse = new List<Bar>
            {
                new("Not used in the 30 days before", removals.Count - active, Slot: 1),
                new("Used in the 30 days before", active, Slot: 5),
            };
            Panels.Add(new("Were they still using it?", Chart.Bars(inUse, Fmt.Count, "Removals by recent use"),
                "A removal from someone still using Copilot is worth checking: a leaver, a move, or a reclaim that went too far.", "half"));

            /* By department, with departments under the minimum group size folded together. */
            var depts = removals.GroupBy(r => r.Department ?? "(no department)")
                .Select(g => (Name: g.Key, People: g.Select(r => r.UserKey).Distinct().Count(), Removed: g.Count(), Active: g.Count(r => r.ActiveBefore), Back: g.Count(r => r.GivenBack)))
                .ToList();
            var shown = FoldSmall(depts.OrderByDescending(d => d.Removed), d => d.People,
                small => ("Smaller groups", small.Sum(d => d.People), small.Sum(d => d.Removed), small.Sum(d => d.Active), small.Sum(d => d.Back)), out var folded);
            var dt = new Table { Headers = ["Department", "Removed", "Still in use", "Given back"], Numeric = new HashSet<int> { 1, 2, 3 } };
            foreach (var d in shown) dt.Rows.Add([d.Name, d.Removed, d.Active, d.Back]);
            dt.KeepLast = folded > 0 ? 1 : 0;
            CsvTable = dt;
            Panels.Add(new("Removals by department", dt.Render(), "Still in use: the holder used Copilot in the 30 days before the removal. Given back: a Copilot license was assigned again later." + (folded > 0 ? " " + FoldNote(folded, "department") : ""), "full", CsvUrl()));
        }

        /* Who made the changes names administrators, so it is for the Licensing role, and never in privacy mode. */
        if (Ctx.Viewer.Has(Roles.Licensing) && !PrivacyMode)
        {
            var admins = await Db.QueryAsync<(string By, int Added, int Removed, DateTime Last)>($@"
SELECT ISNULL(NULLIF(f.ChangedBy, N''), N'(not recorded)'), SUM(CASE WHEN f.Change > 0 THEN 1 ELSE 0 END), SUM(CASE WHEN f.Change < 0 THEN 1 ELSE 0 END), MAX(f.ChangedUtc)
FROM bi.FactLicenseChange AS f {Ctx.Scope("f.UserKey")} {copilot}
WHERE f.DateKey BETWEEN @k0 AND @k1
GROUP BY ISNULL(NULLIF(f.ChangedBy, N''), N'(not recorded)')
ORDER BY COUNT(*) DESC;", Ctx.Args());
            var at = new Table { Headers = ["Changed by", "Assigned", "Removed", "Last change"], Numeric = new HashSet<int> { 1, 2 }, EmptyText = "No changes in the period." };
            foreach (var a in admins) at.Rows.Add([a.By, a.Added, a.Removed, a.Last]);
            Panels.Add(new("Who made the changes", at.Render(),
                "The account the directory recorded for each change: an administrator, or a group-based licensing service. Shown only to the Licensing role."));
        }
    }
}
