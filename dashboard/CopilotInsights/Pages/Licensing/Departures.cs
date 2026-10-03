using System.Net;
using CopilotInsights.Infrastructure;
using Microsoft.AspNetCore.Html;
using Microsoft.AspNetCore.Mvc;

namespace CopilotInsights.Pages.Licensing;

/// <summary>
/// License holders with a leave date coming up, or already past on an account still enabled (2.4.3): AD's
/// accountExpires, from the directory load, and Entra's employeeLeaveDateTime, when the license tool reads it.
/// Reclaiming on the last day, rather than weeks later when the account is disabled, saves the weeks between.
/// </summary>
public sealed class DeparturesModel(PageServices s) : InsightsPage(s)
{
    public override string Title => "Departures ahead";
    public override string Persona => "Licensing";
    public override string? RequiredRole => Roles.Licensing;
    public override string? OwnWindow => "today's licenses and leave dates";
    public override string Intro => "People holding a Copilot license who are due to leave, from the account expiry in AD and the leave date in Entra, and those past the date whose account is still enabled. Reclaim each license on the last day, not weeks later.";

    static readonly int[] Aheads = [7, 30, 90];

    static readonly Dictionary<string, string> Sorts = new()
    {
        ["date"] = "x.LeaveUtc, x.DisplayName", ["name"] = "x.DisplayName, x.UserPrincipalName",
        ["dept"] = "x.Department, x.DisplayName", ["manager"] = "x.ManagerName, x.DisplayName",
        ["upn"] = "x.UserPrincipalName", ["source"] = "x.Source, x.DisplayName", ["since"] = "x.LicensedSinceUtc, x.DisplayName",
        ["recent"] = "x.LastKey DESC, x.DisplayName, x.UserPrincipalName",
    };
    static readonly Dictionary<string, string> SortLabels = new() { ["date"] = "Soonest", ["name"] = "Name", ["dept"] = "Department", ["manager"] = "Manager" };

    sealed class Row
    {
        public string? DisplayName { get; set; }
        public string UserPrincipalName { get; set; } = "";
        public string? Department { get; set; }
        public string? ManagerName { get; set; }
        public DateTime LeaveUtc { get; set; }
        public string Source { get; set; } = "";
        public DateTime? LicensedSinceUtc { get; set; }
        public DateTime? LastUsed { get; set; }
        public int Past { get; set; }
        public int TotalRows { get; set; }
    }

    /* The sources that exist in this database: an older one has neither column until the scripts of 2.4.3 run. */
    async Task<(string? Ad, string? Entra)> SourcesAsync()
    {
        var ad = await Db.ScalarAsync<int>("SELECT CASE WHEN COL_LENGTH(N'audit.UserDirectory', N'AccountExpiresUtc') IS NULL THEN 0 ELSE 1 END") == 1;
        var entra = await Db.ScalarAsync<int>("SELECT CASE WHEN COL_LENGTH(N'audit.CopilotLicense', N'EmployeeLeaveUtc') IS NULL THEN 0 ELSE 1 END") == 1;
        return (ad ? "ud.AccountExpiresUtc" : null, entra ? "cl.EmployeeLeaveUtc" : null);
    }

    string Cte(string? ad, string? entra)
    {
        var a = ad ?? "CAST(NULL AS DATETIME2(0))";
        var e = entra ?? "CAST(NULL AS DATETIME2(0))";
        return $@"
WITH l AS (SELECT du.UserKey, du.DisplayName, du.UserPrincipalName, du.Department, du.ManagerName, du.LicensedSinceUtc,
                  CASE WHEN {a} IS NOT NULL AND ({e} IS NULL OR {a} <= {e}) THEN {a} ELSE {e} END AS LeaveUtc,
                  CASE WHEN {a} IS NOT NULL AND {e} IS NOT NULL THEN N'AD and Entra' WHEN {a} IS NOT NULL THEN N'AD account expiry' ELSE N'Entra leave date' END AS Source
           FROM bi.FactLicensedDay AS ld {Ctx.Scope("ld.UserKey")}
           JOIN bi.DimUser AS du ON du.UserKey = ld.UserKey
           {(ad is null ? "" : "LEFT JOIN audit.UserDirectory AS ud ON ud.UserPrincipalName = du.UserPrincipalName")}
           {(entra is null ? "" : "LEFT JOIN audit.CopilotLicense AS cl ON cl.UserPrincipalName = du.UserPrincipalName")}
           WHERE ld.DateKey = @k1 AND ISNULL(du.AccountEnabled, 1) = 1 AND ({a} IS NOT NULL OR {e} IS NOT NULL)),
x AS (SELECT l.*, CASE WHEN l.LeaveUtc < SYSUTCDATETIME() THEN 1 ELSE 0 END AS Past,
             (SELECT MAX(ud2.DateKey) FROM dash.UserDay AS ud2 WHERE ud2.UserKey = l.UserKey AND ud2.Interactions > 0) AS LastKey
      FROM l WHERE l.LeaveUtc < DATEADD(DAY, @ahead, SYSUTCDATETIME()))";
    }

    int Ahead() => int.TryParse(Request.Query["ahead"], out var a) && Aheads.Contains(a) ? a : 30;

    protected override async Task BuildAsync()
    {
        var (ad, entra) = await SourcesAsync();
        if (ad is null && entra is null)
        {
            Notice = "No leave dates have been loaded yet. An administrator loads them with the directory load (the AD account expiry date) or the license tool with -IncludeLeaveDates (Entra's leave date), from the current release's scripts.";
            return;
        }
        var ahead = Ahead();
        var pg = new Paging(Request, Sorts, "date");
        var args = Ctx.Args(new { q = pg.Like, ahead });
        var rows = await Db.QueryAsync<Row>($@"{Cte(ad, entra)}
SELECT x.DisplayName, x.UserPrincipalName, x.Department, x.ManagerName, x.LeaveUtc, x.Source, x.LicensedSinceUtc, dl.[Date] AS LastUsed, x.Past,
       COUNT(*) OVER () AS TotalRows
FROM x LEFT JOIN bi.DimDate AS dl ON dl.DateKey = x.LastKey
WHERE (@q IS NULL OR x.DisplayName LIKE @q OR x.UserPrincipalName LIKE @q OR x.Department LIKE @q)
{pg.OrderAndPage()};", args);
        pg.Total = rows.FirstOrDefault()?.TotalRows ?? 0;
        var (past, coming) = (await Db.QueryAsync<(int Past, int Coming)>($"{Cte(ad, entra)} SELECT COUNT(CASE WHEN Past = 1 THEN 1 END), COUNT(CASE WHEN Past = 0 THEN 1 END) FROM x;", args)).Single();

        Kpis.Add(new("Past their date", Num(past), "account still enabled, license still held: reclaim now", past > 0 ? "crit" : null));
        Kpis.Add(new($"Leaving in {ahead} days", Num(coming), "reclaim on the last day"));
        Kpis.Add(new("Sources", (ad is null ? "" : "AD") + (ad is not null && entra is not null ? " and " : "") + (entra is null ? "" : "Entra"),
            entra is null ? "Entra's leave date: run the license tool with -IncludeLeaveDates" : null));

        var t = new Table
        {
            Headers = ["Name", "Sign-in", "Department", "Manager", "Leaves", "Date from", "Days to go", "Licensed since", "Last used"],
            Numeric = new HashSet<int> { 6 },
            EmptyText = $"Nobody with a license is due to leave in the next {ahead} days.",
            Server = new(pg, Request, ["name", "upn", "dept", "manager", "date", "source", "date", "since", "recent"], new HashSet<int> { 8 }),
        };
        var today = DateTime.UtcNow.Date;
        foreach (var r in rows)
            t.Rows.Add([r.DisplayName, r.UserPrincipalName, r.Department, r.ManagerName, r.LeaveUtc.Date, r.Source,
                        r.Past == 1 ? "Past" : (object)(r.LeaveUtc.Date - today).Days, r.LicensedSinceUtc?.Date, r.LastUsed is null ? "Never" : r.LastUsed.Value.Date]);
        CsvTable = t;
        var choices = string.Join(" · ", Aheads.Select(a =>
            $"<a href=\"{WebUtility.HtmlEncode(Ctx.Url(Request.Path, extra: $"ahead={a}"))}\"{(a == ahead ? " class=\"on\"" : "")}>Next {a} days</a>"));
        var exports = past == 0 ? "" :
            $"<p class=\"sub\">Past their date: <a href=\"{WebUtility.HtmlEncode(Ctx.Url(Request.Path, extra: "handler=bulk"))}\">Entra bulk remove members file</a> · " +
            $"<a href=\"{WebUtility.HtmlEncode(Ctx.Url(Request.Path, extra: "handler=script"))}\">PowerShell script</a>, recorded for License action outcomes.</p>";
        Panels.Add(new("People", new HtmlString($"<p class=\"sub\">Show: {choices}</p>" + exports + pg.Controls(Request, SortLabels).Value + t.Render().Value),
            "Leaves: the earlier of the account expiry set in AD (read by each directory load) and the leave date in Entra (read by the license tool with -IncludeLeaveDates). " +
            "Only accounts still enabled: disabled and deleted ones are on Licenses on disabled accounts. The files carry the people past their date only.", "full", CsvUrl()));
    }

    public Task<IActionResult> OnGetBulkAsync() => ExportAsync(false);
    public Task<IActionResult> OnGetScriptAsync() => ExportAsync(true);

    async Task<IActionResult> ExportAsync(bool script)
    {
        if (!await PrepareAsync()) return Page();
        var (ad, entra) = await SourcesAsync();
        if (ad is null && entra is null) return Page();
        var upns = await Db.QueryAsync<string>($"{Cte(ad, entra)} SELECT x.UserPrincipalName FROM x WHERE x.Past = 1 ORDER BY x.UserPrincipalName;", Ctx.Args(new { ahead = 0 }));
        var skuId = (await HttpContext.RequestServices.GetRequiredService<CopilotInsights.Data.OpsData>().PoolAsync()).FirstOrDefault(p => p.IsCopilot)?.SkuId.ToString();
        var text = script
            ? CopilotInsights.Data.LicenseExport.Script("revoke", upns, await S.Settings.StringAsync("LicenseGroupId"), skuId, Viewer.Upn ?? Viewer.WindowsName, DateTime.UtcNow)
            : CopilotInsights.Data.LicenseExport.BulkMembersCsv(upns);
        await CopilotInsights.Data.LicenseActions.RecordAsync(Db, "leaver", upns, Viewer.Upn ?? Viewer.WindowsName);
        await S.Audit.WriteAsync(Viewer, "Export", Request.Path, Ctx.ScopeLabel, $"departures past their date {(script ? "script" : "bulk file")}: {upns.Count} people");
        var name = $"copilot-departures-{DateTime.UtcNow:yyyyMMdd}";
        return File(CopilotInsights.Data.LicenseExport.FileBytes(text, script), script ? "text/plain" : "text/csv", script ? name + ".ps1" : name + ".csv");
    }
}
