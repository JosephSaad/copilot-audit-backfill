using CopilotInsights.Charts;
using CopilotInsights.Data;
using CopilotInsights.Infrastructure;
using Microsoft.AspNetCore.Html;

namespace CopilotInsights.Pages.Licensing;

public sealed class IndexModel(PageServices s, Metrics m) : InsightsPage(s)
{
    public override string Title => "License inventory";
    public override string Persona => "Licensing";
    public override string Intro => "How many Copilot licenses are assigned, how that changed, and how many are in use.";

    protected override async Task BuildAsync()
    {
        var t = await m.TotalsAsync(Ctx);
        var tr = await m.TrendAsync(Ctx);
        var unused = await Db.ScalarAsync<int>($@"
SELECT COUNT(*) FROM bi.FactLicensedDay AS ld {Ctx.Scope("ld.UserKey")}
WHERE ld.DateKey = @k1
  AND NOT EXISTS (SELECT 1 FROM dash.UserDay AS ud WHERE ud.UserKey = ld.UserKey AND ud.DateKey >= @inactiveKey AND ud.Interactions > 0)
  AND {CopilotInsights.Data.ProtectedLevels.Not("(SELECT du.UserPrincipalName FROM bi.DimUser AS du WHERE du.UserKey = ld.UserKey)")};", Ctx.Args());
        var changes = await Db.QueryAsync<(DateTime Bucket, int Added, int Removed)>($@"
SELECT {Ctx.BucketSql("d")}, SUM(CASE WHEN f.Change > 0 THEN 1 ELSE 0 END), SUM(CASE WHEN f.Change < 0 THEN 1 ELSE 0 END)
FROM bi.FactLicenseChange AS f {Ctx.Scope("f.UserKey")}
JOIN bi.DimSku AS k ON k.SkuKey = f.SkuKey AND k.IsCopilot = 1
JOIN bi.DimDate AS d ON d.DateKey = f.DateKey
WHERE f.DateKey BETWEEN @k0 AND @k1
GROUP BY {Ctx.BucketSql("d")};", Ctx.Args());
        var start = tr.FirstOrDefault()?.Licensed ?? 0;
        var added = changes.Sum(c => c.Added);
        var removed = changes.Sum(c => c.Removed);

        Kpis.Add(new("Licensed today", Num(t.LicensedNow), $"{(t.LicensedNow - start >= 0 ? "+" : "")}{t.LicensedNow - start:#,0} since the start of the period"));
        Kpis.Add(new("Assigned in the period", Num(added), Href: Open("/licensing/changes")));
        Kpis.Add(new("Removed in the period", Num(removed), Href: Open("/licensing/changes")));
        Kpis.Add(new("Licenses in use", Pct(Ratio(t.LicensedNow - unused, t.LicensedNow)), $"used in the last {Ctx.InactiveDays} days"));
        Kpis.Add(new($"Unused for {Ctx.InactiveDays} days", Num(unused), "candidates to reclaim", unused > 0 ? "crit" : null, Href: Open("/licensing/unused")));

        var buckets = Buckets();
        Panels.Add(new("Licenses held", Chart.Line(Labels(), [new Series("Licensed", tr.Select(w => (double?)w.Licensed).ToList(), 1)], Fmt.Count, "Licenses held"),
            $"People holding a Copilot license at the end of each day or week. {TrendNote}", "half"));
        Panels.Add(new("Assigned and removed",
            Chart.Columns(Labels(), [
                new Series("Assigned", buckets.Select(b => (double?)changes.Where(c => c.Bucket == b.Key).Sum(c => c.Added)).ToList(), 1),
                new Series("Removed", buckets.Select(b => (double?)changes.Where(c => c.Bucket == b.Key).Sum(c => c.Removed)).ToList(), 2),
            ], Fmt.Count, "Licenses assigned and removed", stacked: false),
            "From the license history. A person moved between two Copilot SKUs counts once each way.", "half"));

        var skus = await Db.QueryAsync<(string Sku, int N)>($@"
SELECT ISNULL(NULLIF(du.CopilotSku, N''), N'(unknown SKU)'), COUNT(*)
FROM bi.FactLicensedDay AS ld {Ctx.Scope("ld.UserKey")}
JOIN bi.DimUser AS du ON du.UserKey = ld.UserKey
WHERE ld.DateKey = @k1 GROUP BY ISNULL(NULLIF(du.CopilotSku, N''), N'(unknown SKU)') ORDER BY 2 DESC;", Ctx.Args());
        Panels.Add(new("Licenses by product (SKU)", Chart.Bars(skus.Select(k => new Bar(k.Sku, k.N)).ToList(), Fmt.Count, "Licenses by product"), "A SKU is the product name Microsoft sells the license under.", "half"));

        var units = FoldSmall(await m.UnitsAsync(Ctx), u => u.People, r => Metrics.Combine(r, "Smaller groups"), out var folded)
            .Where(u => u.Licensed > 0).OrderByDescending(u => u.Licensed - u.ActiveLicensed).ToList();
        Panels.Add(new("License holders who did not use Copilot, by unit",
            Chart.Bars(units.Take(20).Select(u => new Bar(u.Name, u.Licensed - u.ActiveLicensed, u.Drillable ? Ctx.Url(Request.Path, org: u.Unit) : null,
                $"{u.Licensed - u.ActiveLicensed:#,0} of {u.Licensed:#,0} licensed", 2)).ToList(), Fmt.Count, "Idle licenses by unit"),
            "Licenses held today by people who did not use Copilot in the period, for each part of the organization one level below." +
            (folded > 0 ? " " + FoldNote(folded, "unit") : ""), "half"));
    }
}

public sealed class UnusedModel(PageServices s) : InsightsPage(s)
{
    public override string? OwnWindow => "today's licenses";
    public override string Title => "Unused licenses";
    public override string Persona => "Licensing";
    public override string? RequiredRole => Roles.Licensing;
    public override string Intro => "People holding a Copilot license who have not used Copilot recently: candidates to reclaim or to follow up.";

    static readonly Dictionary<string, string> Sorts = new()
    {
        ["idle"] = "CASE WHEN x.LastKey IS NULL THEN 0 ELSE 1 END, x.LastKey, du.DisplayName",
        ["since"] = "du.LicensedSinceUtc, du.DisplayName", ["name"] = "du.DisplayName, du.UserPrincipalName",
        ["dept"] = "du.Department, du.DisplayName", ["manager"] = "du.ManagerName, du.DisplayName",
        ["upn"] = "du.UserPrincipalName", ["sku"] = "du.CopilotSku, du.DisplayName", ["recent"] = "x.LastKey DESC, du.DisplayName, du.UserPrincipalName",
    };
    static readonly Dictionary<string, string> SortLabels = new()
    {
        ["idle"] = "Idle longest", ["since"] = "Licensed longest", ["name"] = "Name", ["dept"] = "Department", ["manager"] = "Manager",
    };

    sealed class Row
    {
        public string? DisplayName { get; set; }
        public string UserPrincipalName { get; set; } = "";
        public string? Department { get; set; }
        public string? ManagerName { get; set; }
        public string? CopilotSku { get; set; }
        public DateTime? LicensedSinceUtc { get; set; }
        public DateTime? LastUsed { get; set; }
        public int TotalRows { get; set; }
    }

    protected override async Task BuildAsync()
    {
        var idleDays = int.TryParse(Request.Query["idle"], out var i) && i is 7 or 14 or 30 or 60 or 90 ? i : Ctx.InactiveDays;
        var pg = new Paging(Request, Sorts, "idle");
        var rows = await Db.QueryAsync<Row>($@"
SELECT du.DisplayName, du.UserPrincipalName, du.Department, du.ManagerName, du.CopilotSku, du.LicensedSinceUtc,
       dl.[Date] AS LastUsed, COUNT(*) OVER () AS TotalRows
FROM bi.FactLicensedDay AS ld {Ctx.Scope("ld.UserKey")}
JOIN bi.DimUser AS du ON du.UserKey = ld.UserKey
OUTER APPLY (SELECT MAX(ud.DateKey) AS LastKey FROM dash.UserDay AS ud WHERE ud.UserKey = ld.UserKey AND ud.Interactions > 0) AS x
LEFT JOIN bi.DimDate AS dl ON dl.DateKey = x.LastKey
WHERE ld.DateKey = @k1 AND (x.LastKey IS NULL OR x.LastKey < @idleKey)
  AND (du.LicensedSinceUtc IS NULL OR du.LicensedSinceUtc <= DATEADD(DAY, -@idle, @d1))
  AND {CopilotInsights.Data.ProtectedLevels.Not("du.UserPrincipalName")}
  AND (@q IS NULL OR du.DisplayName LIKE @q OR du.UserPrincipalName LIKE @q OR du.Department LIKE @q)
{pg.OrderAndPage()};", Ctx.Args(new { q = pg.Like, idle = idleDays, idleKey = ReportContext.Key(Ctx.End.AddDays(-(idleDays - 1))) }));
        pg.Total = rows.FirstOrDefault()?.TotalRows ?? 0;

        var t = new Table
        {
            Headers = ["Name", "Sign-in", "Department", "Manager", "SKU", "Licensed since", "Last used", "Days since last use"],
            Numeric = new HashSet<int> { 7 }, EmptyText = "Every license is in use.",
            /* Idle longest puts the most days since last use first: never used, then the oldest. */
            Server = new(pg, Request, ["name", "upn", "dept", "manager", "sku", "since", "recent", "idle"], new HashSet<int> { 6, 7 }),
        };
        foreach (var r in rows)
            t.Rows.Add([r.DisplayName, r.UserPrincipalName, r.Department, r.ManagerName, r.CopilotSku, r.LicensedSinceUtc?.Date,
                        r.LastUsed is null ? "Never" : r.LastUsed.Value.Date, r.LastUsed is null ? null : (Ctx.End - r.LastUsed.Value.Date).Days]);
        CsvTable = t;
        Kpis.Add(new($"Unused for {idleDays} days or more", Num(pg.Total), $"licensed at least {idleDays} days ago", pg.Total > 0 ? "crit" : null));
        var choices = string.Join(" · ", new[] { 7, 14, 30, 60, 90 }.Select(d =>
            $"<a href=\"{System.Net.WebUtility.HtmlEncode(Ctx.Url(Request.Path, extra: $"idle={d}"))}\"{(d == idleDays ? " class=\"on\"" : "")}>{d} days</a>"));
        Panels.Add(new("People", new HtmlString($"<p class=\"sub\">Not used for: {choices}</p>" + await ProtectedNoteAsync() + pg.Controls(Request, SortLabels).Value + t.Render().Value),
            "Today's licenses against the idle time you choose.", "full", CsvUrl()));
    }
}

public sealed class DemandModel(PageServices s, Metrics m) : InsightsPage(s)
{
    public override string Title => "Demand for licenses";
    public override string Persona => "Licensing";
    public override string Intro => "People without a license who use the free Copilot Chat: where demand for a license is.";

    sealed class Row
    {
        public string? DisplayName { get; set; }
        public string UserPrincipalName { get; set; } = "";
        public string? Department { get; set; }
        public string? ManagerName { get; set; }
        public int ActiveDays { get; set; }
        public int Interactions { get; set; }
        public int TotalRows { get; set; }
    }

    static readonly Dictionary<string, string> Sorts = new()
    {
        ["days"] = "a.D DESC, a.I DESC", ["use"] = "a.I DESC", ["name"] = "du.DisplayName", ["dept"] = "du.Department, du.DisplayName",
        ["upn"] = "du.UserPrincipalName", ["manager"] = "du.ManagerName, du.DisplayName",
    };
    static readonly Dictionary<string, string> SortLabels = new()
    {
        ["days"] = "Most days active", ["use"] = "Most interactions", ["name"] = "Name", ["dept"] = "Department",
    };

    const string Unlicensed = @"
a AS (SELECT ud.UserKey, SUM(ud.Interactions) AS I, COUNT(CASE WHEN ud.Interactions > 0 THEN 1 END) AS D
      FROM dash.UserDay AS ud WHERE ud.DateKey BETWEEN @k0 AND @k1 GROUP BY ud.UserKey
      HAVING SUM(ud.Interactions) > 0 AND SUM(ud.LicensedUse) = 0)";

    protected override async Task BuildAsync()
    {
        var tr = await m.TrendAsync(Ctx);
        var depts = await Db.QueryAsync<(string Dept, int People, int Users, int Heavy)>($@"
WITH {Unlicensed}
SELECT ISNULL(NULLIF(du.Department, N''), N'(no department)'), COUNT(*),
       COUNT(a.UserKey), COUNT(CASE WHEN a.D >= @heavy THEN 1 END)
FROM bi.DimUser AS du {Ctx.Scope("du.UserKey")}
LEFT JOIN a ON a.UserKey = du.UserKey
WHERE du.DirectoryStatus = N'Found' AND du.AccountEnabled = 1 AND du.CopilotLicenseToday <> N'Licensed'
GROUP BY ISNULL(NULLIF(du.Department, N''), N'(no department)');", Ctx.Args(new { heavy = Heavy }));
        var users = depts.Sum(d => d.Users);
        var heavy = depts.Sum(d => d.Heavy);
        Kpis.Add(new("Unlicensed Copilot Chat users", Num(users), "in the period"));
        Kpis.Add(new($"Used it on {Heavy} days or more", Num(heavy), "the strongest candidates for a license"));
        Kpis.Add(new("People without a license", Num(depts.Sum(d => d.People)), $"enabled accounts in {Here}"));

        Panels.Add(new("Copilot Chat users without a license",
            Chart.Line(Labels(), [new Series("Copilot Chat only", tr.Select(w => (double?)w.ActiveUnlicensed).ToList(), 2)], Fmt.Count, "People without a license using Copilot Chat"),
            TrendNote, "half"));
        var byDept = FoldSmall(depts, d => d.People, small => ("Smaller departments", small.Sum(x => x.People), small.Sum(x => x.Users), small.Sum(x => x.Heavy)), out _)
            .Where(d => d.Users > 0).OrderByDescending(d => d.Heavy).ThenByDescending(d => d.Users).Take(20).ToList();
        Panels.Add(new("Where the demand is",
            Chart.Bars(byDept.Select(d => new Bar(d.Dept, d.Heavy, Note: $"{d.Users:#,0} used it; {d.Heavy:#,0} on {Heavy}+ days; {d.People:#,0} unlicensed people", Slot: 2)).ToList(), Fmt.Count, "Heavy unlicensed users by department"),
            $"Unlicensed people who used Copilot Chat on {Heavy} days or more in the period, by department.", "half"));

        var recorded = await Db.QueryAsync<(string LicenseType, int People, long N)>($@"
SELECT l.LicenseType, COUNT(DISTINCT f.UserKey), COUNT_BIG(*)
FROM bi.FactInteraction AS f {Ctx.Scope("f.UserKey")}
JOIN bi.DimLicenseType AS l ON l.LicenseTypeKey = f.LicenseTypeKey
WHERE f.DateKey BETWEEN @k0 AND @k1
GROUP BY l.LicenseType;", Ctx.Args());
        Panels.Add(new("License type Copilot recorded",
            Chart.Bars(recorded.OrderByDescending(r => r.N).Select(r => new Bar(r.LicenseType, r.People, Note: $"{r.N:#,0} interactions", Slot: r.LicenseType == "Premium" ? 1 : 2)).ToList(), Fmt.Count, "People by recorded license type"),
            "People by the license type Copilot itself wrote into each of their records: Premium, or Starter for the free Copilot Chat. It is Copilot's own view at the moment of use, beside the license history. A person can appear under more than one.", "half"));

        if (!Viewer.Has(Roles.Licensing) || PrivacyMode) return;   // the named list is for the Licensing role only, and never in privacy mode
        var pg = new Paging(Request, Sorts, "days");
        var rows = await Db.QueryAsync<Row>($@"
WITH {Unlicensed}
SELECT du.DisplayName, du.UserPrincipalName, du.Department, du.ManagerName, a.D AS ActiveDays, a.I AS Interactions, COUNT(*) OVER () AS TotalRows
FROM a JOIN bi.DimUser AS du ON du.UserKey = a.UserKey {Ctx.Scope("a.UserKey")}
WHERE du.CopilotLicenseToday <> N'Licensed'
  AND (@q IS NULL OR du.DisplayName LIKE @q OR du.UserPrincipalName LIKE @q OR du.Department LIKE @q)
{pg.OrderAndPage()};", Ctx.Args(new { q = pg.Like }));
        pg.Total = rows.FirstOrDefault()?.TotalRows ?? 0;
        var t = new Table
        {
            Headers = ["Name", "Sign-in", "Department", "Manager", "Days active", "Interactions"], Numeric = new HashSet<int> { 4, 5 },
            Server = new(pg, Request, ["name", "upn", "dept", "manager", "days", "use"], new HashSet<int> { 4, 5 }),
        };
        foreach (var r in rows) t.Rows.Add([r.DisplayName, r.UserPrincipalName, r.Department, r.ManagerName, r.ActiveDays, r.Interactions]);
        CsvTable = t;
        Panels.Add(new("People without a license using Copilot Chat", new HtmlString(pg.Controls(Request, SortLabels).Value + t.Render().Value), null, "full", CsvUrl()));
    }

    int Heavy => Math.Max(2, Ctx.Days * 5 / 7 / 4);   // a quarter of the working days in the period
}
