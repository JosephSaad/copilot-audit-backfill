using CopilotInsights.Charts;
using CopilotInsights.Data;
using CopilotInsights.Infrastructure;
using Microsoft.AspNetCore.Html;

namespace CopilotInsights.Pages.Licensing;

public sealed class IndexModel(PageServices s, Metrics m, OpsData ops) : InsightsPage(s)
{
    public override string Title => "License inventory";
    public override string Persona => "Licensing";
    public override string Intro => "How many Microsoft 365 Copilot (Premium) licenses are held, how that changed in the period, and how many are in use.";

    protected override async Task BuildAsync()
    {
        /* The reads below are independent, each on its own connection: started together. */
        var tTask = m.TotalsAsync(Ctx);
        var trTask = m.TrendAsync(Ctx);
        var unitsTask = m.UnitsAsync(Ctx);
        var namesTask = SkuNames.LoadAsync(Db);
        var skusTask = Db.QueryAsync<(string Sku, int N)>($@"
SELECT ISNULL(NULLIF(du.CopilotSku, N''), N'(unknown SKU)'), COUNT(*)
FROM bi.FactLicensedDay AS ld {Ctx.Scope("ld.UserKey")}
JOIN bi.DimUser AS du ON du.UserKey = ld.UserKey
WHERE ld.DateKey = @k1 GROUP BY ISNULL(NULLIF(du.CopilotSku, N''), N'(unknown SKU)') ORDER BY 2 DESC;", Ctx.Args());
        /* Licenses held today without use in the inactive days: all of them, for the share in use; and the
           candidates to reclaim, counted as Unused licenses lists them (licensed at least the inactive days ago, the
           protected levels left out), so that figure and the list it opens agree. */
        var idleTask = Db.QueryAsync<(int Idle, int Unused)>($@"
SELECT COUNT(*),
       COUNT(CASE WHEN (du.LicensedSinceUtc IS NULL OR du.LicensedSinceUtc <= DATEADD(DAY, -@idle, @d1))
                   AND pl.Upn IS NULL THEN 1 END)
FROM bi.FactLicensedDay AS ld {Ctx.Scope("ld.UserKey")}
JOIN bi.DimUser AS du ON du.UserKey = ld.UserKey
{CopilotInsights.Data.ProtectedLevels.Join("pl", "du.UserPrincipalName")}   -- a join: a subquery cannot sit inside COUNT
WHERE ld.DateKey = @k1
  AND NOT {CopilotInsights.Pages.Forecasting.Renewal.UsedSince(Ctx, "ld.UserKey")};", Ctx.Args(new { idle = Ctx.InactiveDays }));
        var changesTask = Db.QueryAsync<(DateTime Bucket, int Added, int Removed)>($@"
SELECT {Ctx.BucketSql("d")}, SUM(CASE WHEN f.Change > 0 THEN 1 ELSE 0 END), SUM(CASE WHEN f.Change < 0 THEN 1 ELSE 0 END)
FROM bi.FactLicenseChange AS f {Ctx.Scope("f.UserKey")}
JOIN bi.DimSku AS k ON k.SkuKey = f.SkuKey AND k.IsCopilot = 1
JOIN bi.DimDate AS d ON d.DateKey = f.DateKey
WHERE f.DateKey BETWEEN @k0 AND @k1
GROUP BY {Ctx.BucketSql("d")};", Ctx.Args());
        await Task.WhenAll(tTask, trTask, unitsTask, namesTask, skusTask, idleTask, changesTask);
        var t = await tTask;
        var tr = await trTask;
        var (idle, unused) = (await idleTask).Single();
        var changes = await changesTask;
        var start = tr.FirstOrDefault()?.Licensed ?? 0;
        var added = changes.Sum(c => c.Added);
        var removed = changes.Sum(c => c.Removed);

        /* Licensed today and the trend are accounts enabled today (3.1.3); the licenses on disabled or departed
           accounts are still paid seats, so they are named beside it, not hidden. */
        var seats = t.LicensedNow + t.LicensedNotEnabled;
        Kpis.Add(new("Licensed today", Num(t.LicensedNow), $"enabled accounts; {(t.LicensedNow - start >= 0 ? "+" : "")}{t.LicensedNow - start:#,0} since the start of the period" +
            (t.LicensedNotEnabled > 0 ? $"; {t.LicensedNotEnabled:#,0} more on disabled accounts, seats to reclaim" : ""),
            Href: t.LicensedNotEnabled > 0 ? Open("/licensing/leavers") : null));
        Kpis.Add(new("Assigned in the period", Num(added), Href: Open("/licensing/changes")));
        Kpis.Add(new("Removed in the period", Num(removed), Href: Open("/licensing/changes")));
        /* Of every license held, those on disabled accounts included (idle counts them too), the share used. */
        Kpis.Add(new("Licenses in use", Pct(Ratio(seats - idle, seats)), $"of all {seats:#,0} held, disabled accounts included, whose holder used Copilot in the last {Ctx.InactiveDays} days"));
        Kpis.Add(new($"Unused for {Ctx.InactiveDays} days", Num(unused), $"held {Ctx.InactiveDays} days or more with no Copilot use, disabled accounts included: candidates to reclaim", unused > 0 ? "crit" : null, Href: Open("/licensing/unused")));

        await BridgeAsync(t.LicensedNow, t.LicensedNotEnabled);

        var buckets = Buckets();
        Panels.Add(new("Licenses held", Chart.Line(Labels(), [new Series("Licensed", tr.Select(w => (double?)w.Licensed).ToList(), 1, ToEnd: true)], Fmt.Count, "Licenses held"),
            $"People holding a Copilot license at the end of each day or week, counting accounts enabled today; licenses on disabled or departed accounts are left out. {ToEndNote} {TrendNote}", "half"));
        Panels.Add(new("Assigned and removed",
            Chart.Columns(Labels(), [
                new Series("Assigned", buckets.Select(b => (double?)changes.Where(c => c.Bucket == b.Key).Sum(c => c.Added)).ToList(), 1),
                new Series("Removed", buckets.Select(b => (double?)changes.Where(c => c.Bucket == b.Key).Sum(c => c.Removed)).ToList(), 2),
            ], Fmt.Count, "Licenses assigned and removed", stacked: false),
            "Copilot licenses assigned and removed each day or week, from the license history, disabled accounts included. A person moved from one Copilot product to another counts once in each.", "half"));

        var skus = await skusTask;
        var names = await namesTask;   // Product names, not part numbers
        Panels.Add(new("Licenses by product", Chart.Bars(skus.Select(k => new Bar(names.Name(k.Sku), k.N, Note: names.Knows(k.Sku) ? k.Sku : null)).ToList(), Fmt.Count, "Licenses by product"),
            "Copilot licenses held on the period's last day, by product, disabled accounts included; the product's part number (SKU) on hover. A product shown only by its part number can be named in Admin › Value names.", "half"));

        var units = FoldSmall(await unitsTask, u => u.People, r => Metrics.Combine(r, "Smaller groups"), out var folded)
            .Where(u => u.Licensed > 0).OrderByDescending(u => u.Licensed - u.ActiveLicensed).ToList();
        Panels.Add(new("License holders who did not use Copilot, by unit",
            Chart.Bars(units.Take(20).Select(u => new Bar(u.Name, u.Licensed - u.ActiveLicensed, u.Drillable ? Ctx.Url(Request.Path, org: u.Unit) : null,
                $"{u.Licensed - u.ActiveLicensed:#,0} of {u.Licensed:#,0} licensed", 2)).ToList(), Fmt.Count, "License holders who did not use Copilot, by unit"),
            "Licenses held today on enabled accounts whose holder did not use Copilot in the period, for each unit one level down the organization. Licenses on disabled accounts are listed on Licenses on disabled accounts." +
            (folded > 0 ? " " + FoldNote(folded, "unit") : ""), "half"));
    }

    /// <summary>
    /// From the add-on licenses bought to the licenses held, step by step. Three pages counted licenses three ways
    /// (License pool: assigned add-on seats; this page and Chargeback: everyone holding Copilot, a suite's included;
    /// Copilot reservations: held less those a suite covers), with nothing to show how one becomes the other.
    /// </summary>
    async Task BridgeAsync(int held, int notEnabled)
    {
        if (Ctx.Root is not null)
        {
            Panels.Add(new("From licenses bought to licenses held", new HtmlString("<p class=\"sub\">Licenses are bought for the whole tenant, so this is shown only with the whole organization in view.</p>"), null, "half"));
            return;
        }
        /* The add-on as Costs and savings and Copilot reservations count it: not a suite's Copilot, not Copilot Studio's. */
        var pool = (await ops.PoolAsync()).Where(p => p.IsCopilot && !p.SkuPartNumber.Contains("STUDIO", StringComparison.OrdinalIgnoreCase)).ToList();
        int bought = pool.Sum(p => p.Bought), assigned = pool.Sum(p => p.Consumed);
        var suiteOnly = await ops.HasAsync("dash.CopilotSuiteOnly") ? await Db.ScalarAsync<int>(@"
SELECT COUNT(DISTINCT ld.UserKey) FROM dash.CopilotSuiteOnly AS cso
JOIN bi.DimUser AS du ON du.UserPrincipalName = cso.Upn
JOIN bi.FactLicensedDay AS ld ON ld.UserKey = du.UserKey AND ld.DateKey = @k1;", Ctx.Args()) : 0;
        /* The seats held, disabled and departed accounts' included, to the enabled holders Licensed today counts. */
        var rest = held + notEnabled - assigned - suiteOnly;
        var bars = new List<Bar>
        {
            new("Copilot add-on licenses bought", bought, Href: Viewer.Has(Roles.Licensing) ? "/licensing/pool" : null, Slot: 1),
            new("Less: not assigned", -(bought - assigned), Slot: 8),
            new("Assigned", assigned, Slot: 1),
            new("Plus: Copilot through a suite", suiteOnly, Href: "/licensing/suites", Slot: 3),
        };
        if (rest != 0) bars.Add(new(rest > 0 ? "Plus: the rest of the difference" : "Less: the rest of the difference", rest, Slot: 8));
        if (notEnabled > 0) bars.Add(new("Less: on disabled accounts", -notEnabled, Href: Viewer.Has(Roles.Licensing) ? "/licensing/leavers" : null, Slot: 8));
        bars.Add(new("Licensed today", held, Slot: 7));
        Panels.Add(new("From licenses bought to licenses held", Chart.Bars(bars, Fmt.Count, "From bought to held"),
            "Starts from the tenant's Copilot add-on licenses bought and assigned today, as License pool counts them. Adds the people who get Copilot only " +
            "through a suite (E7, or another suite the price list marks), takes away the licenses on disabled or departed accounts, and ends at Licensed today: enabled accounts holding a license on the license history's last day. " +
            "The rest of the difference is licenses assigned or removed since that day, and accounts the history does not hold. " +
            "Chargeback charges the licenses held on enabled accounts; Copilot reservations counts them less those a suite covers.", "half"));
    }
}

public sealed class UnusedModel(PageServices s) : InsightsPage(s)
{
    public override string? OwnWindow => "today's licenses";
    public override string Title => "Unused licenses";
    public override string Persona => "Licensing";
    public override string? RequiredRole => Roles.Licensing;
    public override string Intro => "People holding a Microsoft 365 Copilot license who have not used Copilot for the number of days chosen below: candidates to reclaim or to follow up. Disabled accounts are included.";

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
OUTER APPLY ({(Ctx.Rollups
    ? "SELECT ua.LastActiveKey AS LastKey FROM dash.UserActivity AS ua WHERE ua.UserKey = ld.UserKey"   // each person's last day of use, kept by the refresh
    : "SELECT MAX(ud.DateKey) AS LastKey FROM dash.UserDay AS ud WHERE ud.UserKey = ld.UserKey AND ud.Interactions > 0")}) AS x
LEFT JOIN bi.DimDate AS dl ON dl.DateKey = x.LastKey
WHERE ld.DateKey = @k1 AND (x.LastKey IS NULL OR x.LastKey < @idleKey)
  AND (du.LicensedSinceUtc IS NULL OR du.LicensedSinceUtc <= DATEADD(DAY, -@idle, @d1))
  AND {CopilotInsights.Data.ProtectedLevels.Not("du.UserPrincipalName")}
  AND (@q IS NULL OR du.DisplayName LIKE @q OR du.UserPrincipalName LIKE @q OR du.Department LIKE @q)
{pg.OrderAndPage()};", Ctx.Args(new { q = pg.Like, idle = idleDays, idleKey = ReportContext.Key(Ctx.End.AddDays(-(idleDays - 1))) }));
        pg.Total = rows.FirstOrDefault()?.TotalRows ?? 0;

        var t = new Table
        {
            Headers = ["Name", "Sign-in", "Department", "Manager", "License", "Licensed since", "Last used", "Days since last use"],
            Numeric = new HashSet<int> { 7 }, EmptyText = "Every license is in use.",
            /* Idle longest puts the most days since last use first: never used, then the oldest. */
            Server = new(pg, Request, ["name", "upn", "dept", "manager", "sku", "since", "recent", "idle"], new HashSet<int> { 6, 7 }),
        };
        var names = await SkuNames.LoadAsync(Db);
        foreach (var r in rows)
            t.Rows.Add([r.DisplayName, r.UserPrincipalName, r.Department, r.ManagerName, names.Name(r.CopilotSku), r.LicensedSinceUtc?.Date,
                        r.LastUsed is null ? "Never" : r.LastUsed.Value.Date, r.LastUsed is null ? null : (Ctx.End - r.LastUsed.Value.Date).Days]);
        CsvTable = t;
        Kpis.Add(new($"Unused for {idleDays} days or more", Num(pg.Total), $"licenses held for at least {idleDays} days, so new ones are not counted", pg.Total > 0 ? "crit" : null));
        var choices = string.Join(" · ", new[] { 7, 14, 30, 60, 90 }.Select(d =>
            $"<a href=\"{System.Net.WebUtility.HtmlEncode(Ctx.Url(Request.Path, extra: $"idle={d}"))}\"{(d == idleDays ? " class=\"on\"" : "")}>{d} days</a>"));
        Panels.Add(new("People", new HtmlString($"<p class=\"sub\">Not used for: {choices}</p>" + await ProtectedNoteAsync() + pg.Controls(Request, SortLabels).Value + t.Render().Value),
            "Licenses held today whose holder has not used Copilot for the days chosen. Never: no Copilot use on record.", "full", CsvUrl()));
    }
}

public sealed class DemandModel(PageServices s, Metrics m) : InsightsPage(s)
{
    public override string Title => "Demand for licenses";
    public override string Persona => "Licensing";
    public override string Intro => "People without a Microsoft 365 Copilot license who use Copilot Chat, which needs no license: where demand for a license is.";

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

    /* The people who used Copilot in the period with no licensed use, from each person's sums over it. */
    string Unlicensed => $@"
a AS (SELECT p.UserKey, p.Interactions AS I, p.ActiveDays AS D FROM {Ctx.PerPerson()} AS p
      WHERE p.Interactions > 0 AND p.LicensedUse = 0)";

    protected override async Task BuildAsync()
    {
        /* The reads below do not depend on each other, each on its own connection: started together. */
        var (tr, depts, recorded) = await Queries(m.TrendAsync(Ctx),
            Db.QueryAsync<(string Dept, int People, int Users, int Heavy)>($@"
WITH {Unlicensed}
SELECT ISNULL(NULLIF(du.Department, N''), N'(no department)'), COUNT(*),
       COUNT(a.UserKey), COUNT(CASE WHEN a.D >= @heavy THEN 1 END)
FROM bi.DimUser AS du {Ctx.Scope("du.UserKey")}
LEFT JOIN a ON a.UserKey = du.UserKey
WHERE du.DirectoryStatus = N'Found' AND du.AccountEnabled = 1 AND du.CopilotLicenseToday <> N'Licensed'
GROUP BY ISNULL(NULLIF(du.Department, N''), N'(no department)');", Ctx.Args(new { heavy = Heavy })),
            /* Each person's records by license type first, then the enabled among them: counted distinct over every
               record joined to the directory, it took twice the time. */
            Db.QueryAsync<(string LicenseType, int People, long N)>($@"
WITH g AS (SELECT f.LicenseTypeKey, f.UserKey, COUNT_BIG(*) AS N
           FROM bi.FactInteraction AS f {Ctx.Scope("f.UserKey")}
           WHERE f.DateKey BETWEEN @k0 AND @k1
           GROUP BY f.LicenseTypeKey, f.UserKey)
SELECT l.LicenseType, COUNT(CASE WHEN {ReportContext.IsEnabled()} THEN 1 END), SUM(g.N)
FROM g
JOIN bi.DimLicenseType AS l ON l.LicenseTypeKey = g.LicenseTypeKey
{ReportContext.EnabledJoin("g.UserKey", left: true)}
GROUP BY l.LicenseType;", Ctx.Args()));
        var users = depts.Sum(d => d.Users);
        var heavy = depts.Sum(d => d.Heavy);
        Kpis.Add(new("Unlicensed Copilot Chat users", Num(users), "enabled accounts with no license that used Copilot Chat in the period"));
        Kpis.Add(new($"Used it on {Heavy} days or more", Num(heavy), "in the period: the strongest candidates for a license"));
        Kpis.Add(new("People without a license", Num(depts.Sum(d => d.People)), $"enabled accounts in {Here}, whether they used Copilot or not"));

        Panels.Add(new("Copilot Chat users without a license",
            Chart.Line(Labels(), [new Series("Copilot Chat only", tr.Select(w => (double?)w.ActiveUnlicensed).ToList(), 2)], Fmt.Count, "People without a license using Copilot Chat"),
            "People with an enabled account and no license who used Copilot Chat. " + TrendNote, "half"));
        var byDept = FoldSmall(depts, d => d.People, small => ("Smaller departments", small.Sum(x => x.People), small.Sum(x => x.Users), small.Sum(x => x.Heavy)), out _)
            .Where(d => d.Users > 0).OrderByDescending(d => d.Heavy).ThenByDescending(d => d.Users).Take(20).ToList();
        Panels.Add(new("Where the demand is",
            Chart.Bars(byDept.Select(d => new Bar(d.Dept, d.Heavy, Note: $"{d.Users:#,0} used it; {d.Heavy:#,0} on {Heavy}+ days; {d.People:#,0} unlicensed people", Slot: 2)).ToList(), Fmt.Count, "Heavy unlicensed users by department"),
            $"Unlicensed people who used Copilot Chat on {Heavy} days or more in the period, by department.", "half"));

        Panels.Add(new("License type Copilot recorded",
            Chart.Bars(recorded.OrderByDescending(r => r.N).Select(r => new Bar(r.LicenseType, r.People, Note: $"{r.N:#,0} interactions", Slot: r.LicenseType == "Premium" ? 1 : 2)).ToList(), Fmt.Count, "People by recorded license type"),
            "People with an enabled account, by the license type Copilot wrote into each of their interaction records: Premium for a Microsoft 365 Copilot license, Starter for Copilot Chat. This is Copilot's view at the moment of use, and can differ from the license history. A person who used both appears under each. The interaction counts on hover include disabled and departed accounts.", "half"));

        if (!Viewer.Has(Roles.Licensing) || PrivacyMode) return;   // the named list is for the Licensing role only, and never in privacy mode
        var pg = new Paging(Request, Sorts, "days");
        var rows = await Db.QueryAsync<Row>($@"
WITH {Unlicensed}
SELECT du.DisplayName, du.UserPrincipalName, du.Department, du.ManagerName, a.D AS ActiveDays, a.I AS Interactions, COUNT(*) OVER () AS TotalRows
FROM a JOIN bi.DimUser AS du ON du.UserKey = a.UserKey {Ctx.Scope("a.UserKey")}
WHERE du.CopilotLicenseToday <> N'Licensed' AND du.AccountEnabled = 1
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
