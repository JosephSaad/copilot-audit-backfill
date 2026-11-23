using System.Globalization;
using System.Net;
using CopilotInsights.Data;
using CopilotInsights.Infrastructure;
using Microsoft.AspNetCore.Html;
using Microsoft.AspNetCore.Mvc;

namespace CopilotInsights.Pages.Licensing;

/// <summary>
/// The reverse of Better on pay-as-you-go (2.6.6 on the credit estimate): people without a Copilot license whose
/// agent use is billed in Copilot Credits at a cost a license would beat. The credits are Licensing › Credit use's
/// estimate (<see cref="CreditData"/>), so this list, Credit use and Agents › Agents costing credits agree.
/// </summary>
public sealed class LicenseForCreditsModel(PageServices s, CreditData creditData) : InsightsPage(s)
{
    public override string Title => "Better with a license";
    public override string Persona => "Licensing";
    public override string? RequiredRole => Roles.Licensing;
    public override string Intro => "People without a Microsoft 365 Copilot license whose agent use is billed in Copilot Credits (pay-as-you-go). Where their credits cost more a month than a license, the license is the cheaper way to pay for the same use, and it adds Copilot in the Microsoft 365 apps.";

    /* The keys the list sorts by; it is sorted in memory, as the estimate is worked out per person after the read. */
    static readonly Dictionary<string, string> Sorts = new()
    {
        ["credits"] = "", ["name"] = "", ["dept"] = "", ["upn"] = "", ["manager"] = "", ["use"] = "", ["agents"] = "", ["work"] = "", ["days"] = "",
    };
    static readonly Dictionary<string, string> SortLabels = new() { ["credits"] = "Most credits", ["name"] = "Name", ["dept"] = "Department" };

    sealed class Who
    {
        public int UserKey { get; set; }
        public string? ManagerName { get; set; }
        public int Days { get; set; }
    }

    sealed record Row(CreditPerson P, string? Manager, int Days);

    /// <summary>
    /// Everyone without a license today, account enabled, whose billed use is estimated at <paramref name="floor"/> or
    /// more a month: the people the page lists and its exports name.
    /// </summary>
    async Task<(List<Row> Rows, decimal Price, string Cur)> ReadAsync(decimal? floor)
    {
        var price = decimal.TryParse(await S.Settings.StringAsync("LicenseMonthlyCost"), NumberStyles.Number, CultureInfo.InvariantCulture, out var lp) ? lp : 30m;
        var v = await creditData.BuildAsync(Ctx);
        var min = floor ?? price;
        /* Unlicensed throughout the window, as Credit use counts them: someone licensed part of it is not billed for that part. */
        var billed = v.People.Where(p => !p.Licensed && p.Use.Billed > 0 && p.Month.Cost >= min).ToList();
        if (billed.Count == 0) return ([], price, v.Currency);
        var who = (await Db.QueryAsync<Who>($@"
SELECT du.UserKey, du.ManagerName, (SELECT COUNT(*) FROM dash.UserDay AS ud WHERE ud.UserKey = du.UserKey AND ud.DateKey BETWEEN @k0 AND @k1 AND ud.Interactions > 0) AS Days
FROM bi.DimUser AS du
WHERE du.UserKey IN (SELECT CAST(value AS INT) FROM STRING_SPLIT(@keys, ','))
  AND du.AccountEnabled = 1   -- 3.1.3: enabled today; a departed person (no AccountEnabled) is not listed
  AND NOT EXISTS (SELECT 1 FROM bi.FactLicensedDay AS ld WHERE ld.UserKey = du.UserKey AND ld.DateKey = @k1);",
            Ctx.Args(new { keys = string.Join(",", billed.Select(p => p.UserKey)) }), timeout: 300)).ToDictionary(w => w.UserKey);
        return (billed.Where(p => who.ContainsKey(p.UserKey)).Select(p => new Row(p, who[p.UserKey].ManagerName, who[p.UserKey].Days)).ToList(), price, v.Currency);
    }

    bool AllShown => Request.Query["show"].FirstOrDefault() == "all";

    protected override async Task BuildAsync()
    {
        var (rows, price, cur) = await ReadAsync(AllShown ? 0.01m : null);
        var pg = new Paging(Request, Sorts, "credits");
        var cost = rows.Sum(r => r.P.Month.Cost);
        Kpis.Add(new(AllShown ? "Paying in credits" : "Cheaper with a license", Num(rows.Count),
            AllShown ? "people without a license whose agent use billed credits" : $"people whose credits cost {price:#,0.##} {cur} a month or more, a license's price"));
        Kpis.Add(new($"Cost of their credits a month ({cur})", Num((long)cost), $"{Num((long)Math.Round(rows.Sum(r => r.P.Month.Credits)))} credits a month, the same estimate as Credit use",
            Href: Open("/licensing/credit-use"),
            Why: await WhyAsync("Each person's billed agent interactions in the period, brought to 30 days, × the credits each costs (an answer, plus grounding in work data, plus actions), × the pay-as-you-go credit's price.",
                "CreditsPerAnswer", "CreditsPerGrounding", "CreditsPerAction", "Price:COPILOT_STUDIO_PAYG_CREDIT")));
        if (!AllShown)
            Kpis.Add(new($"Saving a month ({cur})", Num((long)Math.Max(0, cost - rows.Count * price)), $"if they were licensed instead, at {price:#,0.##} {cur} a license a month",
                Why: await WhyAsync($"Their credits a month ({cost:#,0} {cur}) less a license each ({rows.Count:#,0} × {price:#,0.##} {cur}).", "LicenseMonthlyCost", "CreditsPerAnswer", "CreditsPerGrounding", "Price:COPILOT_STUDIO_PAYG_CREDIT")));

        IEnumerable<Row> shown = rows;
        if (pg.Search is { } q)
            shown = shown.Where(r => (r.P.Name ?? "").Contains(q, StringComparison.OrdinalIgnoreCase) || r.P.Upn.Contains(q, StringComparison.OrdinalIgnoreCase) || (r.P.Department ?? "").Contains(q, StringComparison.OrdinalIgnoreCase));
        var ordered = (pg.SortKey switch
        {
            "name" => shown.OrderBy(r => r.P.Name ?? r.P.Upn, StringComparer.CurrentCulture),
            "dept" => shown.OrderBy(r => r.P.Department ?? "", StringComparer.CurrentCulture).ThenBy(r => r.P.Name ?? r.P.Upn),
            "upn" => shown.OrderBy(r => r.P.Upn, StringComparer.OrdinalIgnoreCase),
            "manager" => shown.OrderBy(r => r.Manager ?? "", StringComparer.CurrentCulture).ThenBy(r => r.P.Name ?? r.P.Upn),
            "use" => shown.OrderByDescending(r => r.P.Use.Interactions).ThenBy(r => r.P.Name ?? r.P.Upn),
            "agents" => shown.OrderByDescending(r => r.P.Use.Billed).ThenBy(r => r.P.Name ?? r.P.Upn),
            "work" => shown.OrderByDescending(r => r.P.Use.BilledWork).ThenBy(r => r.P.Name ?? r.P.Upn),
            "days" => shown.OrderByDescending(r => r.Days).ThenBy(r => r.P.Name ?? r.P.Upn),
            _ => shown.OrderByDescending(r => r.P.Month.Cost).ThenBy(r => r.P.Name ?? r.P.Upn),
        }).ToList();
        if (pg.Reversed) ordered.Reverse();
        pg.Total = ordered.Count;
        var page = pg.ForExport ? ordered : ordered.Skip((pg.Page - 1) * Paging.PageSize).Take(Paging.PageSize).ToList();

        var t = new Table
        {
            Headers = ["Name", "Sign-in", "Department", "Manager", "Interactions", "Billed agent answers", "Of them, read work data", "Days used",
                       "Credits a month", $"Cost a month ({cur})", $"Against the license ({cur})"],
            Numeric = new HashSet<int> { 4, 5, 6, 7, 8, 9, 10 },
            EmptyText = AllShown ? "Nobody without a license has agent use billed in credits." : "Nobody without a license uses credits worth more than a license.",
            /* Credits, their cost and against the license all follow the estimate: one key. */
            Server = new(pg, Request, ["name", "upn", "dept", "manager", "use", "agents", "work", "days", "credits", "credits", "credits"], new HashSet<int> { 4, 5, 6, 7, 8, 9, 10 }),
        };
        foreach (var r in page)
            t.Rows.Add([r.P.Name, r.P.Upn, r.P.Department, r.Manager, r.P.Use.Interactions, r.P.Use.Billed, r.P.Use.BilledWork, r.Days,
                Math.Round(r.P.Month.Credits), Math.Round(r.P.Month.Cost, 2), Math.Round(r.P.Month.Cost - price, 2)]);
        CsvTable = t;
        var show = string.Join(" · ", new[] { ("cheaper", "Cheaper with a license"), ("all", "Everyone paying in credits") }.Select(o =>
            $"<a href=\"{WebUtility.HtmlEncode(Ctx.Url(Request.Path, extra: $"show={o.Item1}"))}\"{((o.Item1 == "all") == AllShown ? " class=\"on\"" : "")}>{o.Item2}</a>"));
        var exports = AllShown || rows.Count == 0 ? "" :
            $"<div class=\"sub\">License them: {CopilotInsights.Data.LicenseActions.Button(Ctx, Request.Path, "handler=bulk", Token(), TenantQuery, "Entra bulk add members file")} · " +
            $"{CopilotInsights.Data.LicenseActions.Button(Ctx, Request.Path, "handler=script", Token(), TenantQuery, "PowerShell script")}, recorded for License action outcomes.</div>";
        Panels.Add(new("People", new HtmlString($"<p class=\"sub\">Show: {show}</p>" + exports + pg.Controls(Request, SortLabels).Value + t.Render().Value),
            "Billed: answers from an agent to people with no Microsoft 365 Copilot license at the time, or in a Copilot Studio channel; Copilot Chat without an agent costs no credits, so it is not counted. " +
            "Interactions, billed answers and days used are for the period. Credits a month: the estimate of Licensing › Credit use, at the rates in Admin › Targets and assumptions, brought to 30 days; it errs high. " +
            "Listed: no license today, and an enabled account. Against the license: the cost a month less a license's price; above zero, the license is cheaper.", "full", CsvUrl()));
    }
    /* Posted, with the anti-forgery token (see LicenseActions.Button). */
    public Task<IActionResult> OnPostBulkAsync() => ExportAsync(false);
    public Task<IActionResult> OnPostScriptAsync() => ExportAsync(true);

    async Task<IActionResult> ExportAsync(bool script)
    {
        if (!await PrepareAsync()) return Page();
        var upns = (await ReadAsync(null)).Rows.Select(r => r.P.Upn).Order(StringComparer.OrdinalIgnoreCase).ToList();

        var skuId = (await HttpContext.RequestServices.GetRequiredService<CopilotInsights.Data.OpsData>().PoolAsync()).FirstOrDefault(p => p.IsCopilot)?.SkuId.ToString();
        var text = script
            ? CopilotInsights.Data.LicenseExport.Script("upgrade", upns, await S.Settings.StringAsync("LicenseGroupId"), skuId, Viewer.Upn ?? Viewer.WindowsName, DateTime.UtcNow)
            : CopilotInsights.Data.LicenseExport.BulkMembersCsv(upns);
        await CopilotInsights.Data.LicenseActions.RecordAsync(Db, "upgrade", upns, Viewer.Upn ?? Viewer.WindowsName);
        await S.Audit.WriteAsync(Viewer, "Export", Request.Path, Ctx.ScopeLabel, $"license for credits {(script ? "script" : "bulk file")}: {upns.Count} people");
        var name = $"copilot-license-for-credits-{DateTime.UtcNow:yyyyMMdd}";
        return File(CopilotInsights.Data.LicenseExport.FileBytes(text, script), script ? "text/plain" : "text/csv", script ? name + ".ps1" : name + ".csv");
    }
}
