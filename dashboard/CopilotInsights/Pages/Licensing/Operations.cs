using System.Globalization;
using System.Net;
using System.Text;
using CopilotInsights.Charts;
using CopilotInsights.Data;
using CopilotInsights.Infrastructure;
using Microsoft.AspNetCore.Html;
using Microsoft.AspNetCore.Mvc;

namespace CopilotInsights.Pages.Licensing;

/// <summary>The license price and currency, from Admin > Access.</summary>
static class Price
{
    public static async Task<(decimal Monthly, string Currency)> GetAsync(Settings s) =>
        (decimal.TryParse(await s.StringAsync("LicenseMonthlyCost"), NumberStyles.Number, CultureInfo.InvariantCulture, out var p) ? p : 30m,
         await s.StringAsync("LicenseCurrency") ?? "USD");
}

/// <summary>Seats bought against seats assigned against seats used: the renewal question.</summary>
public sealed class PoolModel(PageServices s, OpsData ops, Metrics metrics) : InsightsPage(s)
{
    public override string Title => "License pool";
    public override string Persona => "Licensing";
    public override string Intro => "Copilot seats bought, assigned and used, from the tenant's subscriptions (/subscribedSkus, read each night with the licenses). Seats bought but not assigned are paid for and idle; seats assigned but not used are the unused-license list.";

    protected override async Task BuildAsync()
    {
        if (!Viewer.OrgWide && !Viewer.Has(Roles.Licensing))
        {
            Notice = "The license pool is the whole tenant's, so it needs the whole-organisation view (the top of the organisation, or the Organization role) or the Licensing role.";
            return;
        }
        var pool = await ops.PoolAsync();
        if (pool.Count == 0)
        {
            Notice = "No license pool yet. It is written by the license tool each night (tools/Update-CopilotAuditEnrichment.ps1, 2.3.0 or later).";
            return;
        }
        var (price, cur) = await Price.GetAsync(S.Settings);
        var copilot = pool.Where(p => p.IsCopilot).ToList();
        int bought = copilot.Sum(p => p.Bought), assigned = copilot.Sum(p => p.Consumed);
        var whole = await S.Contexts.CreateAsync(Viewer, 30, null);
        var t = await metrics.TotalsAsync(whole);
        Kpis.Add(new("Copilot seats bought", Num(bought), $"{copilot.Sum(p => p.Warning):#,0} in the grace period after expiry"));
        Kpis.Add(new("Assigned", Num(assigned), bought > 0 ? $"{Pct(Ratio(assigned, bought))} of those bought" : null,
            bought > 0 && assigned >= bought * (await S.Settings.IntAsync("PoolWarnPercent", 95)) / 100.0 ? "warn" : null));
        Kpis.Add(new("Bought, not assigned", Num(bought - assigned), $"{(bought - assigned) * price:#,0} {cur} a month idle"));
        Kpis.Add(new("Used in the last 30 days", Num(t.ActiveLicensed), assigned > 0 ? $"{Pct(Ratio(t.ActiveLicensed, assigned))} of those assigned" : null));

        Panels.Add(new("Where the seats are", Chart.Bars(
        [
            new("Assigned and used (30 days)", t.ActiveLicensed, Slot: 3),
            new("Assigned, not used", Math.Max(0, assigned - t.ActiveLicensed), Href: Viewer.Has(Roles.Licensing) ? "/licensing/unused" : null, Slot: 5),
            new("Bought, not assigned", Math.Max(0, bought - assigned), Slot: 1),
        ], Fmt.Count, "Copilot seats by state"), "Used: held a license at the end of the 30 days and used Copilot while licensed.", "half"));

        var days = await Db.QueryAsync<(DateTime Day, int Bought, int Assigned)>(@"
SELECT SnapshotDate, SUM(Enabled + Warning), SUM(Consumed) FROM audit.LicenseSkuDay
WHERE IsCopilot = 1 AND SnapshotDate BETWEEN @d0 AND @d1 GROUP BY SnapshotDate ORDER BY SnapshotDate;", Ctx.Args());
        if (days.Count > 1)
            Panels.Add(new("Bought and assigned over time", Chart.Line(days.Select(d => d.Day.ToString("d MMM")).ToList(),
                [new Series("Bought", days.Select(d => (double?)d.Bought).ToList(), 1), new Series("Assigned", days.Select(d => (double?)d.Assigned).ToList(), 3)],
                Fmt.Count, "Copilot seats bought and assigned"), "One point per night the license tool ran.", "half"));

        var tb = new Table { Headers = ["License (SKU)", "Copilot", "Bought", "Assigned", "Available", "Grace period", "Suspended", "Read (UTC)"], Numeric = new HashSet<int> { 2, 3, 4, 5, 6 } };
        foreach (var p in pool) tb.Rows.Add([p.SkuPartNumber, p.IsCopilot ? "Yes" : "", p.Bought, p.Consumed, p.Available, p.Warning, p.Suspended, p.CapturedUtc]);
        CsvTable = tb;
        Panels.Add(new("Every subscription in the tenant", tb.Render(), $"Copilot SKU ids, for the license export script: {string.Join(", ", copilot.Select(p => $"{p.SkuPartNumber} {p.SkuId}"))}.", "full", CsvUrl()));
    }
}

/// <summary>What Copilot costs each department, and how much of it is unused.</summary>
public sealed class ChargebackModel(PageServices s, Metrics metrics) : InsightsPage(s)
{
    public override string Title => "Chargeback";
    public override string Persona => "Licensing";
    public override string Intro => "The monthly cost of the Copilot licenses each group holds, what share of it is used, and the cost per person using Copilot: for allocating the license cost to budgets.";

    static readonly string[] By = ["Department", "Division", "Company", "Country", "Office", "EmployeeType"];

    protected override async Task BuildAsync()
    {
        var by = Request.Query["by"].FirstOrDefault() is { } b && By.Contains(b) ? b : "Department";
        var (price, cur) = await Price.GetAsync(S.Settings);
        var rows = Groups.Fold(this, await metrics.ByAttributeAsync(Ctx, by), Ctx.MinGroupSize, out var folded);
        var licensed = rows.Sum(r => r.Licensed);
        var used = rows.Sum(r => r.ActiveLicensed);
        Kpis.Add(new("Licenses held", Num(licensed), $"{licensed * price:#,0} {cur} a month"));
        Kpis.Add(new("Used in the period", Num(used), licensed > 0 ? Pct(Ratio(used, licensed)) : null));
        Kpis.Add(new("Unused spend", $"{(licensed - used) * price:#,0} {cur}", "a month, on licenses not used in the period", licensed - used > 0 ? "warn" : null));
        Kpis.Add(new("Cost per person using it", used > 0 ? $"{licensed * price / used:#,0.##} {cur}" : "—", "a month"));

        var choices = string.Join(" · ", By.Select(x => $"<a href=\"{WebUtility.HtmlEncode(Ctx.Url(Request.Path, extra: "by=" + x))}\"{(x == by ? " class=\"on\"" : "")}>{x}</a>"));
        var t = new Table
        {
            Headers = [by, "People", "Licenses", $"Monthly cost ({cur})", "Used", "Unused", $"Unused cost ({cur})", $"Cost per person using it ({cur})"],
            Numeric = new HashSet<int> { 1, 2, 3, 4, 5, 6, 7 },
        };
        foreach (var r in rows.OrderByDescending(r => r.Licensed))
            t.Rows.Add([r.Name, r.People, r.Licensed, r.Licensed * price, r.ActiveLicensed, r.Licensed - r.ActiveLicensed, (r.Licensed - r.ActiveLicensed) * price,
                        r.ActiveLicensed > 0 ? Math.Round(r.Licensed * price / r.ActiveLicensed, 2) : null]);
        CsvTable = t;
        Panels.Add(new("Unused spend by group", Chart.Bars(rows.Where(r => r.Licensed > r.ActiveLicensed).OrderByDescending(r => r.Licensed - r.ActiveLicensed).Take(15)
            .Select(r => new Bar(r.Name, (double)((r.Licensed - r.ActiveLicensed) * price), Note: $"{r.Licensed - r.ActiveLicensed:#,0} of {r.Licensed:#,0} licenses unused")).ToList(), Fmt.Count, "Unused spend by group"),
            $"A month, in {cur}, at {price:#,0.##} a license (Admin > Access).", "half"));
        Panels.Add(new("By " + by.ToLowerInvariant(), new HtmlString($"<p class=\"sub\">Group by: {choices}</p>" + t.Render().Value),
            (folded > 0 ? $"{folded} group(s) smaller than {Ctx.MinGroupSize} people are combined. " : "") + "Used: holds a license at the end of the period and used Copilot while licensed.",
            "full", CsvUrl()));
    }
}

/// <summary>Copilot licenses on accounts that can no longer use them: straight savings, and a security loose end.</summary>
public sealed class LeaversModel(PageServices s, OpsData ops) : InsightsPage(s)
{
    public override string? OwnWindow => "today";
    public override string Title => "Licenses on disabled accounts";
    public override string Persona => "Licensing";
    public override string? RequiredRole => Roles.Licensing;
    public override bool UsesFilters => true;
    public override string Intro => "People holding a Copilot license today whose account is disabled in AD or Entra, or who have left the directory. Nobody can use these licenses: reclaim them.";

    static readonly Dictionary<string, string> Sorts = new() { ["name"] = "u.DisplayName", ["dept"] = "u.Department, u.DisplayName", ["since"] = "u.LicensedSinceUtc" };

    protected override async Task BuildAsync()
    {
        var (price, cur) = await Price.GetAsync(S.Settings);
        var pg = new Paging(Request, Sorts, "name");
        var rows = await ops.LeaversAsync(Ctx.Root, pg.Like, pg.OrderAndPage());
        pg.Total = rows.FirstOrDefault()?.TotalRows ?? 0;
        Kpis.Add(new("Licenses on disabled or departed accounts", Num(pg.Total), $"{pg.Total * price:#,0} {cur} a month", pg.Total > 0 ? "crit" : null));
        var t = new Table { Headers = ["Name", "Sign-in", "Department", "Manager", "Why", "Licensed since", "Last used"], EmptyText = "No licenses on disabled accounts." };
        foreach (var r in rows) t.Rows.Add([r.DisplayName, r.UserPrincipalName, r.Department, r.ManagerName, r.Reason, r.LicensedSinceUtc?.Date, r.LastUsed]);
        CsvTable = t;
        var exports = pg.Total == 0 ? "" :
            $"<p class=\"sub\">To reclaim them: <a href=\"{WebUtility.HtmlEncode(Request.Path + "?handler=bulk")}\">Entra bulk-remove file</a> · " +
            $"<a href=\"{WebUtility.HtmlEncode(Request.Path + "?handler=script")}\">PowerShell script</a> (run by a license administrator; the dashboard changes nothing).</p>";
        Panels.Add(new("People", new HtmlString(exports + pg.Controls(Request, new Dictionary<string, string> { ["name"] = "Name", ["dept"] = "Department", ["since"] = "Licensed longest" }).Value + t.Render().Value),
            "AD's account state comes from the nightly directory load, Entra's from the license tool.", "full", CsvUrl()));
    }

    public async Task<IActionResult> OnGetBulkAsync() => await ExportAsync(false);
    public async Task<IActionResult> OnGetScriptAsync() => await ExportAsync(true);

    async Task<IActionResult> ExportAsync(bool script)
    {
        if (!await PrepareAsync()) return Page();
        var upns = (await ops.LeaversAsync(Ctx.Root)).Select(r => r.UserPrincipalName).ToList();
        var skuId = (await ops.PoolAsync()).FirstOrDefault(p => p.IsCopilot)?.SkuId.ToString();
        var text = script
            ? LicenseExport.Script("revoke", upns, await S.Settings.StringAsync("LicenseGroupId"), skuId, Viewer.Upn ?? Viewer.WindowsName, DateTime.UtcNow)
            : LicenseExport.BulkMembersCsv(upns);
        await LicenseActions.RecordAsync(Db, "leaver", upns, Viewer.Upn ?? Viewer.WindowsName);
        await S.Audit.WriteAsync(Viewer, "Export", Request.Path, Ctx.ScopeLabel, $"{(script ? "script" : "bulk file")}: {upns.Count} licenses on disabled accounts");
        return File(Encoding.UTF8.GetBytes(text), script ? "text/plain" : "text/csv", script ? $"reclaim-copilot-licenses-{DateTime.UtcNow:yyyyMMdd}.ps1" : $"reclaim-copilot-licenses-{DateTime.UtcNow:yyyyMMdd}.csv");
    }
}
