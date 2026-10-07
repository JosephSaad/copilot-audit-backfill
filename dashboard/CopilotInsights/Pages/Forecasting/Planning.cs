using System.Globalization;
using System.Net;
using System.Text;
using CopilotInsights.Charts;
using CopilotInsights.Data;
using CopilotInsights.Infrastructure;
using Microsoft.AspNetCore.Html;
using Microsoft.AspNetCore.Mvc;

namespace CopilotInsights.Pages.Forecasting;

/* ---- Budget and renewal ------------------------------------------------------------------------ */

/// <summary>What the Copilot contracts cost, when they renew, and how many seats to renew at.</summary>
public sealed class BudgetModel(PageServices s, OpsData ops, Metrics metrics, OpsReports reports) : InsightsPage(s)
{
    public override string Title => "Budget and renewal";
    public override string Persona => "Forecasting";
    public override bool UsesFilters => false;
    public override string Intro => "The Copilot contracts (entered by an administrator below), what they cost a month and a year, when each renews, and how many seats the use supports at renewal: seats assigned, less those to revoke and on disabled accounts, plus the people ready to upgrade.";

    sealed class Contract
    {
        public int ContractId { get; set; }
        public string Name { get; set; } = "";
        public int Seats { get; set; }
        public decimal MonthlyPrice { get; set; }
        public DateTime StartDate { get; set; }
        public DateTime RenewalDate { get; set; }
        public string? Note { get; set; }
    }

    protected override async Task BuildAsync()
    {
        ShowDone();
        if (!Viewer.OrgWide && !Viewer.Has(Roles.Licensing))
        {
            Notice = "The budget is the whole tenant's, so it needs the whole-organization view or the Licensing role.";
            return;
        }
        if (!await ops.HasAsync("dash.LicenseContract")) { Notice = "Contracts cannot be recorded here yet. An administrator sets this up by running Install-CopilotAudit.ps1 -Dashboard from the current release."; return; }
        var contracts = await Db.QueryAsync<Contract>("SELECT ContractId, Name, Seats, MonthlyPrice, StartDate, RenewalDate, Note FROM dash.LicenseContract ORDER BY RenewalDate;");
        var (price, cur) = (decimal.TryParse(await S.Settings.StringAsync("LicenseMonthlyCost"), NumberStyles.Number, CultureInfo.InvariantCulture, out var p) ? p : 30m,
                            await S.Settings.StringAsync("LicenseCurrency") ?? "USD");
        var pool = (await ops.PoolAsync()).Where(x => x.IsCopilot).ToList();
        /* Every term the tenant's (2.4.12, see OpsReports.Tenant): for a viewer with the Licensing role and their own part
           only, the tenant's seats less its disabled accounts were set against their own part's revocations and upgrades. */
        var whole = OpsReports.Tenant(Ctx);
        var totals = await metrics.TotalsAsync(whole);
        var counts = await reports.ActionCountsAsync(whole);
        var leavers = (await ops.LeaversAsync(whole.Root)).Count;

        int seats = contracts.Sum(c => c.Seats), assigned = pool.Sum(x => x.Consumed), bought = pool.Sum(x => x.Bought);
        var monthly = contracts.Sum(c => c.Seats * c.MonthlyPrice);
        var next = contracts.Where(c => c.RenewalDate >= DateTime.Today).OrderBy(c => c.RenewalDate).FirstOrDefault();
        var keep = Math.Max(0, (pool.Count > 0 ? assigned : totals.LicensedNow) - counts.GetValueOrDefault("revoke") - leavers + counts.GetValueOrDefault("upgrade"));
        var avgPrice = seats > 0 ? monthly / seats : price;

        Kpis.Add(new("Seats under contract", Num(seats), contracts.Count == 0 ? "no contracts entered yet" : $"{Count(contracts.Count, "contract")}; the tenant shows {bought:#,0} bought"));
        Kpis.Add(new("License cost", $"{monthly:#,0} {cur}", $"a month; {monthly * 12:#,0} a year"));
        Kpis.Add(new("Next renewal", next is null ? "—" : next.RenewalDate.ToString("d MMM yyyy"),
            next is null ? null : $"{next.Name}, in {(next.RenewalDate - DateTime.Today).Days} days", next is not null && (next.RenewalDate - DateTime.Today).Days <= 90 ? "warn" : null));
        Kpis.Add(new("Seats the use supports", Num(keep), seats > 0 ? $"{keep - seats:+#,0;-#,0;0} against the contracts: {(keep - seats) * avgPrice * 12:+#,0;-#,0;0} {cur} a year" : null,
            seats > 0 && keep < seats ? "warn" : null));

        Panels.Add(new("How the renewal figure is made", Chart.Bars(
        [
            new("Assigned today", pool.Count > 0 ? assigned : totals.LicensedNow, Slot: 1),
            new("Less: unused, to revoke", -counts.GetValueOrDefault("revoke"), Href: Viewer.Has(Roles.Licensing) ? "/forecasting/actions?kind=revoke" : null, Slot: 8),
            new("Less: on disabled accounts", -leavers, Href: Viewer.Has(Roles.Licensing) ? "/licensing/leavers" : null, Slot: 8),
            new("Plus: ready to upgrade", counts.GetValueOrDefault("upgrade"), Href: Viewer.Has(Roles.Licensing) ? "/forecasting/actions?kind=upgrade" : null, Slot: 3),
            new("Seats the use supports", keep, Slot: 7),
        ], Fmt.Count, "Seats at renewal"),
            $"From the last 30 days: {totals.ActiveLicensed:#,0} licensed people used Copilot. Growth beyond today's use is on Forecasting > Forecast; rollout waves on Forecasting > Rollout waves.", "half"));

        var t = new Table { Headers = ["Contract", "Seats", $"Price a seat a month ({cur})", $"Cost a month ({cur})", $"Cost a year ({cur})", "Started", "Renews", "Days to renewal", "Note", ""], Numeric = new HashSet<int> { 1, 2, 3, 4, 7 }, EmptyText = "No contracts yet." };
        foreach (var c in contracts)
        {
            var days = (c.RenewalDate - DateTime.Today).Days;
            t.Rows.Add([c.Name, c.Seats, c.MonthlyPrice, c.Seats * c.MonthlyPrice, c.Seats * c.MonthlyPrice * 12, c.StartDate, c.RenewalDate,
                new HtmlString($"<span class=\"{(days < 0 ? "status-bad" : days <= 90 ? "status-warn" : "status-ok")}\">{days:#,0}</span>"), c.Note,
                Viewer.IsAdmin ? new HtmlString(Form("delcontract", Hidden("id", c.ContractId) + Button("Remove", $"Remove the contract {c.Name}?", quiet: true))) : null]);
        }
        CsvTable = t;
        var add = !Viewer.IsAdmin ? "" : "<h3>Add a contract</h3>" + Form("contract",
            Input("name", "Name", null, 24) + Input("seats", "Seats", null, 6, "number") + Input("price", $"Price a seat a month ({cur})", price.ToString("0.##", CultureInfo.InvariantCulture), 6) +
            Input("start", "Started", DateTime.Today.ToString("yyyy-MM-dd"), 10, "date") + Input("renewal", "Renews", DateTime.Today.AddYears(1).ToString("yyyy-MM-dd"), 10, "date") +
            Input("note", "Note", null, 24) + Button("Add"));
        Panels.Add(new("Contracts", new HtmlString(t.Render().Value + add),
            "Entered by an administrator from the agreements; the tenant's own count of seats bought is on Licensing > License pool.", "full", CsvUrl()));
    }

    public Task<IActionResult> OnPostContractAsync(string? name, int? seats, string? price, DateTime? start, DateTime? renewal, string? note) => AdminChangeAsync(async () =>
    {
        name = name?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > 200) throw new ArgumentException("give the contract a name.");
        if (seats is null or < 0 or > 10_000_000) throw new ArgumentException("seats is a whole number.");
        if (!decimal.TryParse(price, NumberStyles.Number, CultureInfo.InvariantCulture, out var pr) || pr < 0 || pr > 100000) throw new ArgumentException("the price is a number.");
        if (start is null || renewal is null || renewal <= start) throw new ArgumentException("the renewal date comes after the start.");
        await Db.ExecuteAsync("INSERT INTO dash.LicenseContract (Name, Seats, MonthlyPrice, StartDate, RenewalDate, Note, UpdatedBy) VALUES (@name, @seats, @pr, @start, @renewal, @note, @by);",
            new { name, seats, pr, start, renewal, note = string.IsNullOrWhiteSpace(note) ? null : note.Trim()[..Math.Min(400, note.Trim().Length)], by = Viewer.Upn ?? Viewer.WindowsName });
        return $"Contract \"{name}\" added: {seats:#,0} seats, renewing {renewal:yyyy-MM-dd}.";
    });

    public Task<IActionResult> OnPostDelcontractAsync(int id) => AdminChangeAsync(async () =>
        await Db.ExecuteAsync("DELETE FROM dash.LicenseContract WHERE ContractId = @id", new { id }) > 0 ? "Contract removed." : "That contract was already gone.");
}

/* ---- Rollout waves ----------------------------------------------------------------------------- */

/// <summary>Who to license next, in waves: the people already relying on the free Copilot Chat first, then those in departments where Copilot has taken hold.</summary>
public sealed class WavesModel(PageServices s, OpsData ops) : InsightsPage(s)
{
    public override string? OwnWindow => "the last 30 days";
    public override string Title => "Rollout waves";
    public override string Persona => "Forecasting";
    public override string? RequiredRole => Roles.Licensing;
    public override string Intro => "Everyone without a Premium license, in the order to license them: first those who use the free Copilot Chat most (they already rely on it), then those in departments where licensed people use Copilot most. Split into waves of a set size, each with a file to apply it.";

    static readonly Dictionary<string, string> Sorts = new()
    {
        ["rank"] = "c.[Rank]", ["name"] = "c.DisplayName", ["dept"] = "c.Department, c.[Rank]",
        ["upn"] = "c.UserPrincipalName", ["manager"] = "c.ManagerName, c.[Rank]", ["title"] = "c.JobTitle, c.[Rank]",
        ["days"] = "c.D DESC, c.[Rank]", ["inter"] = "c.I DESC, c.[Rank]", ["adoption"] = "c.DeptAdoption DESC, c.[Rank]",
    };

    sealed class Row
    {
        public int Rank { get; set; }
        public int Wave { get; set; }
        public string? DisplayName { get; set; }
        public string UserPrincipalName { get; set; } = "";
        public string? Department { get; set; }
        public string? ManagerName { get; set; }
        public string? JobTitle { get; set; }
        public int D { get; set; }
        public int I { get; set; }
        public double? DeptAdoption { get; set; }
        public int TotalRows { get; set; }
    }

    int Size() => int.TryParse(Request.Query["size"], out var n) && n is >= 1 and <= 100000 ? n : 0;

    async Task<(int Size, string? Dept)> OptionsAsync()
    {
        var size = Size();
        if (size == 0) size = await S.Settings.IntAsync("RolloutWaveSize", 50);
        var dept = Request.Query["dept"].FirstOrDefault();
        return (size, string.IsNullOrWhiteSpace(dept) ? null : dept.Trim());
    }

    string Sql(string select, string tail) => $@"
WITH a AS (SELECT ud.UserKey, COUNT(CASE WHEN ud.Interactions > 0 THEN 1 END) AS D, SUM(ud.Interactions) AS I
           FROM dash.UserDay AS ud WHERE ud.DateKey BETWEEN @k30 AND @k1 GROUP BY ud.UserKey),
lic AS (SELECT UserKey FROM bi.FactLicensedDay WHERE DateKey = @k1),
dept AS (SELECT ISNULL(NULLIF(du.Department, N''), N'(no department)') AS Dept, COUNT(lic.UserKey) AS L,
                COUNT(CASE WHEN lic.UserKey IS NOT NULL AND a.I > 0 THEN 1 END) AS AL
         FROM bi.DimUser AS du LEFT JOIN lic ON lic.UserKey = du.UserKey LEFT JOIN a ON a.UserKey = du.UserKey
         WHERE du.AccountEnabled = 1 AND du.DirectoryStatus = N'Found'
         GROUP BY ISNULL(NULLIF(du.Department, N''), N'(no department)')),
c AS (SELECT du.DisplayName, du.UserPrincipalName, du.Department, du.ManagerName, du.JobTitle, ISNULL(a.D, 0) AS D, ISNULL(a.I, 0) AS I,
             CASE WHEN dept.L >= @minGroup THEN 100.0 * dept.AL / dept.L END AS DeptAdoption,
             ROW_NUMBER() OVER (ORDER BY ISNULL(a.D, 0) DESC, CASE WHEN dept.L >= @minGroup THEN 1.0 * dept.AL / dept.L ELSE 0 END DESC, ISNULL(a.I, 0) DESC, du.UserPrincipalName) AS [Rank]
      FROM bi.DimUser AS du {Ctx.Scope("du.UserKey")}
      LEFT JOIN lic ON lic.UserKey = du.UserKey
      LEFT JOIN a ON a.UserKey = du.UserKey
      LEFT JOIN dept ON dept.Dept = ISNULL(NULLIF(du.Department, N''), N'(no department)')
      WHERE lic.UserKey IS NULL AND du.AccountEnabled = 1 AND du.DirectoryStatus = N'Found'
        AND (@dept IS NULL OR du.Department = @dept))
SELECT {select} FROM c {tail}";

    protected override async Task BuildAsync()
    {
        var (size, dept) = await OptionsAsync();
        var (price, cur) = (decimal.TryParse(await S.Settings.StringAsync("LicenseMonthlyCost"), NumberStyles.Number, CultureInfo.InvariantCulture, out var p) ? p : 30m,
                            await S.Settings.StringAsync("LicenseCurrency") ?? "USD");
        var pg = new Paging(Request, Sorts, "rank");
        var k30 = ReportContext.Key(Ctx.End.AddDays(-29));
        var rows = await Db.QueryAsync<Row>(Sql("c.*, (c.[Rank] - 1) / @size + 1 AS Wave, COUNT(*) OVER () AS TotalRows",
            $"WHERE (@q IS NULL OR c.DisplayName LIKE @q OR c.UserPrincipalName LIKE @q OR c.Department LIKE @q) {pg.OrderAndPage()};"),
            Ctx.Args(new { k30, size, dept, q = pg.Like }));
        pg.Total = rows.FirstOrDefault()?.TotalRows ?? 0;
        var waves = await Db.QueryAsync<(int Wave, int People, int UsingChat)>(Sql("(c.[Rank] - 1) / @size + 1, COUNT(*), COUNT(CASE WHEN c.D > 0 THEN 1 END)",
            "GROUP BY (c.[Rank] - 1) / @size + 1 ORDER BY 1;"), Ctx.Args(new { k30, size, dept }));
        var all = waves.Sum(w => w.People);
        Kpis.Add(new("People without a license", Num(all), $"{waves.Sum(w => w.UsingChat):#,0} of them use the free Copilot Chat"));
        Kpis.Add(new("Rollout waves", Num(waves.Count), $"of {size:#,0} people"));
        Kpis.Add(new("A wave costs", $"{size * price:#,0} {cur}", "a month"));
        Kpis.Add(new("Everyone licensed", $"{all * price:#,0} {cur}", "a month more"));

        var wt = new Table { Headers = ["Wave", "People", "Using Copilot Chat now", $"Added cost a month ({cur})", "Apply it"], Numeric = new HashSet<int> { 0, 1, 2, 3 }, Sortable = false };
        foreach (var w in waves.Take(12))
            wt.Rows.Add([w.Wave, w.People, w.UsingChat, w.People * price, new HtmlString(
                $"<span class=\"acts\">{LicenseActions.Button(Ctx, Request.Path, Extra(size, dept, $"wave={w.Wave}&handler=bulk"), Token(), TenantQuery, "Bulk add file")} · " +
                $"{LicenseActions.Button(Ctx, Request.Path, Extra(size, dept, $"wave={w.Wave}&handler=script"), Token(), TenantQuery, "PowerShell script")}</span>")]);
        var sizes = string.Join(" · ", new[] { 25, 50, 100, 250, 500, 1000 }.Select(n =>
            $"<a href=\"{E(Ctx.Url(Request.Path, extra: Extra(n, dept, null)))}\"{(n == size ? " class=\"on\"" : "")}>{n}</a>"));
        Panels.Add(new("Waves", new HtmlString($"<p class=\"sub\">People a wave: {sizes} (the default is set in Admin > Targets and assumptions).</p>" + wt.Render().Value),
            (waves.Count > 12 ? $"The first 12 of {waves.Count} waves. " : "") + "Each file is for a license administrator to review and apply; the dashboard changes nothing. Exports are recorded for Forecasting > License action outcomes.", "half"));

        var t = new Table
        {
            Headers = ["Order", "Wave", "Name", "Sign-in", "Department", "Manager", "Job title", "Days on Copilot Chat (30)", "Interactions (30)", "Department adoption"],
            Numeric = new HashSet<int> { 0, 1, 7, 8, 9 }, EmptyText = "Everyone here has a license.",
            /* A wave follows from the order, so both sort by it. */
            Server = new(pg, Request, ["rank", "rank", "name", "upn", "dept", "manager", "title", "days", "inter", "adoption"], new HashSet<int> { 7, 8, 9 }),
        };
        foreach (var r in rows)
            t.Rows.Add([r.Rank, r.Wave, r.DisplayName, r.UserPrincipalName, r.Department, r.ManagerName, r.JobTitle, r.D, r.I, r.DeptAdoption is null ? null : $"{r.DeptAdoption:0}%"]);
        CsvTable = t;
        Panels.Add(new("People, in order", new HtmlString(pg.Controls(Request, new Dictionary<string, string> { ["rank"] = "Order", ["name"] = "Name", ["dept"] = "Department" }).Value + t.Render().Value),
            "Department adoption: of the department's licensed people, the share who used Copilot in the last 30 days (shown for departments of at least the minimum group size).", "full", CsvUrl()));
    }

    static string Extra(int size, string? dept, string? more) =>
        $"size={size}" + (dept is null ? "" : "&dept=" + Uri.EscapeDataString(dept)) + (more is null ? "" : "&" + more);

    /* Posted, with the anti-forgery token (2.4.12, see LicenseActions.Button). */
    public Task<IActionResult> OnPostBulkAsync() => ExportAsync(false);
    public Task<IActionResult> OnPostScriptAsync() => ExportAsync(true);

    async Task<IActionResult> ExportAsync(bool script)
    {
        if (!await PrepareAsync()) return Page();
        var (size, dept) = await OptionsAsync();
        var wave = int.TryParse(Request.Query["wave"], out var w) && w >= 1 ? w : 1;
        var k30 = ReportContext.Key(Ctx.End.AddDays(-29));
        var upns = await Db.QueryAsync<string>(Sql("c.UserPrincipalName", "WHERE (c.[Rank] - 1) / @size + 1 = @wave ORDER BY c.[Rank];"), Ctx.Args(new { k30, size, dept, wave }));
        var skuId = (await ops.PoolAsync()).FirstOrDefault(p => p.IsCopilot)?.SkuId.ToString();
        var by = Viewer.Upn ?? Viewer.WindowsName;
        var text = script ? LicenseExport.Script("upgrade", upns, await S.Settings.StringAsync("LicenseGroupId"), skuId, by, DateTime.UtcNow) : LicenseExport.BulkMembersCsv(upns);
        await LicenseActions.RecordAsync(Db, "wave", upns, by);
        await S.Audit.WriteAsync(Viewer, "Export", Request.Path, Ctx.ScopeLabel, $"wave {wave} of {size} {(script ? "script" : "bulk file")}: {upns.Count} people");
        var name = $"copilot-wave-{wave}-{DateTime.UtcNow:yyyyMMdd}";
        return File(CopilotInsights.Data.LicenseExport.FileBytes(text, script), script ? "text/plain" : "text/csv", script ? name + ".ps1" : name + ".csv");
    }
}

/* ---- License action outcomes ------------------------------------------------------------------- */

/// <summary>Were the exported license changes applied, and what followed: the loop from recommendation to saving.</summary>
public sealed class OutcomesModel(PageServices s, OpsData ops) : InsightsPage(s)
{
    public override string Title => "License action outcomes";
    public override string Persona => "Forecasting";
    public override string? RequiredRole => Roles.Licensing;
    public override bool UsesFilters => false;
    public override string Intro => "Every license export from this dashboard (upgrades, revocations, licenses on disabled accounts, rollout waves), whether the change has happened since, and what followed: the licenses reclaimed, and whether the people given one use it.";

    sealed class Batch
    {
        public string Kind { get; set; } = "";
        public DateTime Day { get; set; }
        public string ExportedBy { get; set; } = "";
        public int People { get; set; }
        public int LicensedNow { get; set; }
        public int UsedSince { get; set; }
    }

    static readonly Dictionary<string, string> PendingSorts = new()
    {
        ["new"] = "o.Day DESC, o.DisplayName", ["name"] = "o.DisplayName",
        ["kind"] = "o.Kind, o.Day DESC, o.DisplayName, o.Upn", ["upn"] = "o.Upn, o.Day DESC, o.Kind", ["dept"] = "o.Department, o.DisplayName, o.Upn",
    };

    /* The exports' people within the viewer's scope (2.4.12): it read every export, so a manager with the Licensing
       role saw who anyone else had exported, across the organization. */
    string Base => $@"
WITH x AS (SELECT la.Kind, la.Upn, la.ExportedBy, la.ExportedUtc, CAST(la.ExportedUtc AS DATE) AS Day,
                  ROW_NUMBER() OVER (PARTITION BY la.Kind, la.Upn, CAST(la.ExportedUtc AS DATE) ORDER BY la.ActionId) AS n
           FROM dash.LicenseAction AS la {Ctx.PersonScope("la.Upn")}),
today AS (SELECT MAX(DateKey) AS K FROM bi.FactLicensedDay),
o AS (SELECT x.Kind, x.Day, x.ExportedBy, x.Upn, du.DisplayName, du.Department,
             CASE WHEN ld.UserKey IS NULL THEN 0 ELSE 1 END AS LicensedNow,
             CASE WHEN EXISTS (SELECT 1 FROM dash.UserDay AS ud WHERE ud.UserKey = du.UserKey AND ud.Interactions > 0
                               AND ud.DateKey > YEAR(x.Day) * 10000 + MONTH(x.Day) * 100 + DAY(x.Day)) THEN 1 ELSE 0 END AS UsedSince
      FROM x
      LEFT JOIN bi.DimUser AS du ON du.UserPrincipalName = x.Upn
      LEFT JOIN bi.FactLicensedDay AS ld ON ld.UserKey = du.UserKey AND ld.DateKey = (SELECT K FROM today)
      WHERE x.n = 1)";

    protected override async Task BuildAsync()
    {
        if (!await ops.HasAsync("dash.LicenseAction")) { Notice = "License exports are not recorded here yet. An administrator sets this up by running Install-CopilotAudit.ps1 -Dashboard from the current release."; return; }
        var (price, cur) = (decimal.TryParse(await S.Settings.StringAsync("LicenseMonthlyCost"), NumberStyles.Number, CultureInfo.InvariantCulture, out var p) ? p : 30m,
                            await S.Settings.StringAsync("LicenseCurrency") ?? "USD");
        var batches = await Db.QueryAsync<Batch>(Base + @"
SELECT Kind, Day, ExportedBy, COUNT(*) AS People, SUM(LicensedNow) AS LicensedNow, SUM(UsedSince) AS UsedSince
FROM o GROUP BY Kind, Day, ExportedBy ORDER BY Day DESC, Kind;", Ctx.Args());
        if (batches.Count == 0) { Notice = "No license exports yet. They are recorded when someone downloads a bulk file or script from Upgrade, train or revoke, Licenses on disabled accounts, or Rollout waves."; return; }

        int Done(Batch b) => b.Kind is "upgrade" or "wave" ? b.LicensedNow : b.People - b.LicensedNow;
        var reclaimed = batches.Where(b => b.Kind is "revoke" or "leaver" or "nonhuman").Sum(Done);
        var given = batches.Where(b => b.Kind is "upgrade" or "wave").ToList();
        Kpis.Add(new("People exported", Num(batches.Sum(b => b.People)), $"in {Count(batches.Count, "export")}"));
        Kpis.Add(new("Changes applied", Pct(Ratio(batches.Sum(Done), batches.Sum(b => b.People))), "the license is where the export said it should be"));
        Kpis.Add(new("Licenses reclaimed", Num(reclaimed), $"{reclaimed * price:#,0} {cur} a month saved"));
        Kpis.Add(new("New licenses in use", Pct(Ratio(given.Sum(b => b.UsedSince), given.Sum(b => b.LicensedNow))), "of those licensed from an upgrade or wave export, used Copilot since"));

        var t = new Table { Headers = ["Exported", "Action", "Exported by", "People", "Applied", "Share applied", "Used Copilot since", $"Cost change a month ({cur})"], Numeric = new HashSet<int> { 3, 4, 5, 6, 7 } };
        foreach (var b in batches)
            t.Rows.Add([b.Day, b.Kind switch { "upgrade" => "Upgrade", "revoke" => "Revoke unused", "leaver" => "Reclaim from disabled accounts", "nonhuman" => "Remove from non-human accounts", _ => "Rollout wave" },
                b.ExportedBy, b.People, Done(b), Pct(Ratio(Done(b), b.People)), b.Kind is "upgrade" or "wave" ? b.UsedSince : null,
                b.Kind is "upgrade" or "wave" ? Done(b) * price : -Done(b) * price]);
        Panels.Add(new("Exports", t.Render(), "Applied: for an upgrade or wave, the person holds a license today; for a revocation, they no longer do. A month: the cost added (positive) or saved (negative) by what has been applied."));

        var pg = new Paging(Request, PendingSorts, "new");
        var pending = await Db.QueryAsync<(DateTime Day, string Kind, string Upn, string? DisplayName, string? Department, int Total)>(Base + $@"
SELECT o.Day, o.Kind, o.Upn, o.DisplayName, o.Department, COUNT(*) OVER ()
FROM o WHERE ((o.Kind IN ('upgrade', 'wave') AND o.LicensedNow = 0) OR (o.Kind IN ('revoke', 'leaver') AND o.LicensedNow = 1))
  AND (@q IS NULL OR o.DisplayName LIKE @q OR o.Upn LIKE @q OR o.Department LIKE @q)
{pg.OrderAndPage()};", Ctx.Args(new { q = pg.Like }));
        pg.Total = pending.FirstOrDefault().Total;
        var pt = new Table
        {
            Headers = ["Exported", "Action", "Name", "Sign-in", "Department"], EmptyText = "Every exported change has been applied.",
            Server = new(pg, Request, ["new", "kind", "name", "upn", "dept"], new HashSet<int> { 0 }),
        };
        foreach (var r in pending) pt.Rows.Add([r.Day, r.Kind, r.DisplayName, r.Upn, r.Department]);
        CsvTable = pt;
        Panels.Add(new("Not applied yet", new HtmlString(pg.Controls(Request, new Dictionary<string, string> { ["new"] = "Newest", ["name"] = "Name" }).Value + pt.Render().Value),
            "License state is from the license tool's last nightly read.", "full", CsvUrl()));
    }
}
