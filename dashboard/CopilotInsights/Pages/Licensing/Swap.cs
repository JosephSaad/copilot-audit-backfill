using System.Globalization;
using System.Net;
using CopilotInsights.Infrastructure;
using CopilotInsights.Pages.Forecasting;
using Microsoft.AspNetCore.Html;
using Microsoft.AspNetCore.Mvc;

namespace CopilotInsights.Pages.Licensing;

/// <summary>
/// Idle licenses paired with the people who want one (2.4.3): each license the revoke rule would take back
/// goes to someone the upgrade rule would license, in the same department (or division, or anywhere in
/// scope), so the seat is reassigned rather than a new one bought. The rules are Upgrade, train or revoke's.
/// </summary>
public sealed class SwapModel(PageServices s) : InsightsPage(s)
{
    public override string Title => "Swap list";
    public override string Persona => "Licensing";
    public override string? RequiredRole => Roles.Licensing;
    public override string? OwnWindow => "the last 30 days";
    public override string Intro => "Idle Copilot licenses paired with the people who want one, in the same part of the organization: take the license from the first, give it to the second, and no new seat is bought.";

    internal static readonly Dictionary<string, (string Label, string Sql)> Within = new()
    {
        ["department"] = ("Same department", "ISNULL(NULLIF(du.Department, N''), N'(no department)')"),
        ["division"] = ("Same division", "ISNULL(NULLIF(du.Division, N''), N'(no division)')"),
        ["any"] = ("Anywhere", "N'(anywhere)'"),
    };

    sealed class Pair
    {
        public string Unit { get; set; } = "";
        public string? GiveName { get; set; }
        public string GiveUpn { get; set; } = "";
        public int GiveDays { get; set; }
        public DateTime? GiveLast { get; set; }
        public DateTime? GiveSince { get; set; }
        public string? TakeName { get; set; }
        public string? TakeUpn { get; set; }
        public int? TakeDays { get; set; }
        public int? TakeInteractions { get; set; }
        public string? TakeManager { get; set; }
    }

    sealed class Counts
    {
        public int Idle { get; set; }
        public int Wanting { get; set; }
        public int Swaps { get; set; }
    }

    /// <summary>Every idle license and every person wanting one, each numbered within their unit; pairs share a number.</summary>
    async Task<string> SidesSqlAsync(string within, int k30) => $@"
WITH {await ActionsModel.ActivitySqlAsync(Db, k30, Ctx.EndKey)},
lic AS (SELECT UserKey FROM bi.FactLicensedDay WHERE DateKey = @k1),
g AS (SELECT {Within[within].Sql} AS Unit, du.UserKey, du.DisplayName, du.UserPrincipalName, du.LicensedSinceUtc, ISNULL(a.D, 0) AS D,
             ROW_NUMBER() OVER (PARTITION BY {Within[within].Sql} ORDER BY ISNULL(a.D, 0), ISNULL(a.I, 0), du.UserPrincipalName) AS N
      FROM bi.DimUser AS du {Ctx.Scope("du.UserKey")}
      JOIN lic ON lic.UserKey = du.UserKey
      LEFT JOIN a ON a.UserKey = du.UserKey
      WHERE {ActionsModel.Kinds["revoke"].Where} AND ISNULL(du.AccountEnabled, 1) = 1),
t AS (SELECT {Within[within].Sql} AS Unit, du.UserKey, du.DisplayName, du.UserPrincipalName, du.ManagerName, a.D, a.I,
             ROW_NUMBER() OVER (PARTITION BY {Within[within].Sql} ORDER BY a.D DESC, a.I DESC, du.UserPrincipalName) AS N
      FROM bi.DimUser AS du {Ctx.Scope("du.UserKey")}
      LEFT JOIN lic ON lic.UserKey = du.UserKey
      JOIN a ON a.UserKey = du.UserKey
      WHERE {ActionsModel.Kinds["upgrade"].Where} AND ISNULL(du.AccountEnabled, 1) = 1)";

    async Task<List<Pair>> PairsAsync(string within, int k30) => await Db.QueryAsync<Pair>($@"
{await SidesSqlAsync(within, k30)}
SELECT g.Unit, g.DisplayName AS GiveName, g.UserPrincipalName AS GiveUpn, g.D AS GiveDays, dl.[Date] AS GiveLast, g.LicensedSinceUtc AS GiveSince,
       t.DisplayName AS TakeName, t.UserPrincipalName AS TakeUpn, t.D AS TakeDays, t.I AS TakeInteractions, t.ManagerName AS TakeManager
FROM g JOIN t ON t.Unit = g.Unit AND t.N = g.N
OUTER APPLY (SELECT MAX(ud.DateKey) AS LastKey FROM dash.UserDay AS ud WHERE ud.UserKey = g.UserKey AND ud.Interactions > 0) AS x
LEFT JOIN bi.DimDate AS dl ON dl.DateKey = x.LastKey
ORDER BY g.Unit, g.N;", Ctx.Args(new { k30 }), timeout: 300);

    string WithinOf() => Request.Query["within"].FirstOrDefault() is { } w && Within.ContainsKey(w) ? w : "department";

    protected override async Task BuildAsync()
    {
        var within = WithinOf();
        var k30 = ReportContext.Key(Ctx.End.AddDays(-29));
        var pairs = await PairsAsync(within, k30);
        var counts = (await Db.QueryAsync<Counts>($@"
{await SidesSqlAsync(within, k30)}
SELECT (SELECT COUNT(*) FROM g) AS Idle, (SELECT COUNT(*) FROM t) AS Wanting,
       (SELECT COUNT(*) FROM g JOIN t ON t.Unit = g.Unit AND t.N = g.N) AS Swaps;", Ctx.Args(new { k30 }), timeout: 300)).Single();

        var price = decimal.TryParse(await S.Settings.StringAsync("LicenseMonthlyCost"), NumberStyles.Number, CultureInfo.InvariantCulture, out var p) ? p : 30m;
        var cur = await S.Settings.StringAsync("LicenseCurrency") ?? "USD";
        Kpis.Add(new("Swaps possible", Num(counts.Swaps), $"{Within[within].Label.ToLowerInvariant()}"));
        Kpis.Add(new($"Not bought a month ({cur})", Num((long)(counts.Swaps * price)), $"{counts.Swaps:#,0} seats at {price:#,0.##}"));
        Kpis.Add(new("Idle licenses left over", Num(counts.Idle - counts.Swaps), "nobody near wants one: revoke them (Upgrade, train or revoke)"));
        Kpis.Add(new("Still wanting a license", Num(counts.Wanting - counts.Swaps), "no idle license near them: a seat to buy, or a wider match"));

        var t = new Table
        {
            Headers = [within switch { "division" => "Division", "any" => "Scope", _ => "Department" },
                       "Take from", "Sign-in", "Licensed since", "Days used, last 30 days", "Last used",
                       "Give to", "Sign-in", "Manager", "Days on free Copilot Chat, last 30 days", "Interactions"],
            Numeric = new HashSet<int> { 4, 9, 10 },
            EmptyText = "No idle license and person wanting one share a unit: widen the match, or see Upgrade, train or revoke.",
        };
        foreach (var r in pairs)
            t.Rows.Add([r.Unit, r.GiveName, r.GiveUpn, r.GiveSince?.Date, r.GiveDays, r.GiveLast is null ? "Never" : r.GiveLast.Value.Date,
                        r.TakeName, r.TakeUpn, r.TakeManager, r.TakeDays, r.TakeInteractions]);
        CsvTable = t;

        var choices = string.Join(" · ", Within.Select(kv =>
            $"<a href=\"{WebUtility.HtmlEncode(Ctx.Url(Request.Path, extra: $"within={kv.Key}"))}\"{(kv.Key == within ? " class=\"on\"" : "")}>{kv.Value.Label}</a>"));
        string Link(string side, string handler, string text) =>
            CopilotInsights.Data.LicenseActions.Button(Ctx, Request.Path, $"within={within}&side={side}&handler={handler}", Token(), TenantQuery, text);
        var exports = pairs.Count == 0 ? "" :
            $"<div class=\"sub\">Apply it, in this order: 1. take the licenses back, {Link("take", "bulk", "Entra bulk remove members file")} · {Link("take", "script", "PowerShell script")}; " +
            $"2. give them out, {Link("give", "bulk", "Entra bulk add members file")} · {Link("give", "script", "PowerShell script")}. " +
            "Both are recorded, so License action outcomes shows whether each change happened.</div>";
        Panels.Add(new("Pairs", new HtmlString($"<p class=\"sub\">Match: {choices}</p>" + await ProtectedNoteAsync() + exports + t.Render().Value),
            $"Take from: a Copilot license held at least {ActionsModel.RevokeAfterDays} days whose Premium features were not used in the last 30 days (the revoke rule). " +
            $"Give to: no license, and the free Copilot Chat used on {ActionsModel.UpgradeDays} or more of the last 30 days (the upgrade rule). " +
            "Within each unit the least-used license goes to the keenest user; people on either side left over are in the figures above.", "full", CsvUrl()));
    }

    /* Posted, with the anti-forgery token (2.4.12, see LicenseActions.Button). */
    public Task<IActionResult> OnPostBulkAsync() => ExportAsync(false);
    public Task<IActionResult> OnPostScriptAsync() => ExportAsync(true);

    /* The files carry the matched pairs only: taking back a license with nobody to give it to is the revoke list's job. */
    async Task<IActionResult> ExportAsync(bool script)
    {
        if (!await PrepareAsync()) return Page();
        var take = Request.Query["side"].FirstOrDefault() != "give";
        var kind = take ? "revoke" : "upgrade";
        var pairs = await PairsAsync(WithinOf(), ReportContext.Key(Ctx.End.AddDays(-29)));
        var upns = pairs.Select(x => take ? x.GiveUpn : x.TakeUpn!).OrderBy(u => u, StringComparer.OrdinalIgnoreCase).ToList();
        var skuId = (await HttpContext.RequestServices.GetRequiredService<CopilotInsights.Data.OpsData>().PoolAsync()).FirstOrDefault(x => x.IsCopilot)?.SkuId.ToString();
        var text = script
            ? CopilotInsights.Data.LicenseExport.Script(kind, upns, await S.Settings.StringAsync("LicenseGroupId"), skuId, Viewer.Upn ?? Viewer.WindowsName, DateTime.UtcNow)
            : CopilotInsights.Data.LicenseExport.BulkMembersCsv(upns);
        await CopilotInsights.Data.LicenseActions.RecordAsync(Db, kind, upns, Viewer.Upn ?? Viewer.WindowsName);
        await S.Audit.WriteAsync(Viewer, "Export", Request.Path, Ctx.ScopeLabel, $"swap {(take ? "take back" : "give out")} {(script ? "script" : "bulk file")}: {upns.Count} people");
        var name = $"copilot-swap-{(take ? "take" : "give")}-{DateTime.UtcNow:yyyyMMdd}";
        return File(CopilotInsights.Data.LicenseExport.FileBytes(text, script), script ? "text/plain" : "text/csv", script ? name + ".ps1" : name + ".csv");
    }
}
