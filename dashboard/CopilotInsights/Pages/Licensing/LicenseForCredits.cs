using System.Globalization;
using System.Net;
using CopilotInsights.Infrastructure;
using Microsoft.AspNetCore.Html;
using Microsoft.AspNetCore.Mvc;

namespace CopilotInsights.Pages.Licensing;

/// <summary>
/// The reverse of Better on pay-as-you-go (2.4.3): people without a Copilot license whose use is charged
/// in Copilot Credits (agents, and Copilot Chat over work content) at a rate a license would cost less than.
/// </summary>
public sealed class LicenseForCreditsModel(PageServices s) : InsightsPage(s)
{
    public override string Title => "Better with a license";
    public override string Persona => "Licensing";
    public override string? RequiredRole => Roles.Licensing;
    public override string Intro => "People without a Copilot license whose use is paid for in Copilot Credits: agents, and Copilot Chat over work files, mail and sites. Where their credits cost more a month than a license, the license is the cheaper way to pay for the same use, and it adds Copilot in the apps.";

    static readonly Dictionary<string, string> Sorts = new()
    {
        ["credits"] = "x.M DESC, x.DisplayName", ["name"] = "x.DisplayName, x.UserPrincipalName", ["dept"] = "x.Department, x.DisplayName",
    };
    static readonly Dictionary<string, string> SortLabels = new() { ["credits"] = "Most credits", ["name"] = "Name", ["dept"] = "Department" };

    sealed class Row
    {
        public string? DisplayName { get; set; }
        public string UserPrincipalName { get; set; } = "";
        public string? Department { get; set; }
        public string? ManagerName { get; set; }
        public long I { get; set; }
        public long M { get; set; }
        public long A { get; set; }
        public long W { get; set; }
        public int Days { get; set; }
        public int People { get; set; }
        public long AllM { get; set; }
        public int TotalRows { get; set; }
    }

    /* Metered: an interaction through an agent, or in Copilot Chat that read work content and did not search the web
       (the Premium rule's chat half), by someone who holds no license today. An agent grounded only in public data is
       not charged, so the estimate errs high; that is why the page shows the credits beside the license, not a verdict. */
    string Sql(string select, string tail) => $@"
WITH g AS (SELECT f.UserKey, f.AppKey, f.AgentKey, CAST(CASE WHEN f.AccessedResourceCount > 0 AND f.UsedWebSearch = 0 THEN 1 ELSE 0 END AS BIT) AS Work,
                  COUNT_BIG(*) AS N
           FROM bi.FactInteraction AS f {Ctx.Scope("f.UserKey")}
           WHERE f.DateKey BETWEEN @k0 AND @k1
           GROUP BY f.UserKey, f.AppKey, f.AgentKey, CAST(CASE WHEN f.AccessedResourceCount > 0 AND f.UsedWebSearch = 0 THEN 1 ELSE 0 END AS BIT)),
m AS (SELECT g.UserKey, SUM(g.N) AS I,
             SUM(CASE WHEN ag.AgentSource <> N'(no agent)' OR (a.AppCategory = N'Copilot Chat' AND g.Work = 1) THEN g.N ELSE 0 END) AS M,
             SUM(CASE WHEN ag.AgentSource <> N'(no agent)' THEN g.N ELSE 0 END) AS A,
             SUM(CASE WHEN ag.AgentSource = N'(no agent)' AND a.AppCategory = N'Copilot Chat' AND g.Work = 1 THEN g.N ELSE 0 END) AS W
      FROM g JOIN bi.DimApp AS a ON a.AppKey = g.AppKey JOIN bi.DimAgent AS ag ON ag.AgentKey = g.AgentKey
      GROUP BY g.UserKey),
d AS (SELECT ud.UserKey, COUNT(*) AS Days FROM dash.UserDay AS ud WHERE ud.DateKey BETWEEN @k0 AND @k1 AND ud.Interactions > 0 GROUP BY ud.UserKey),
x AS (SELECT du.DisplayName, du.UserPrincipalName, du.Department, du.ManagerName, m.I, m.M, m.A, m.W, ISNULL(d.Days, 0) AS Days,
             COUNT(*) OVER () AS People, SUM(m.M) OVER () AS AllM
      FROM m
      JOIN bi.DimUser AS du ON du.UserKey = m.UserKey
      LEFT JOIN bi.FactLicensedDay AS ld ON ld.UserKey = m.UserKey AND ld.DateKey = @k1
      LEFT JOIN d ON d.UserKey = m.UserKey
      WHERE ld.UserKey IS NULL AND ISNULL(du.AccountEnabled, 1) = 1 AND m.M * @rate * 30 >= @floor * @days)
{select}
{tail}";

    async Task<(decimal Price, decimal Rate, string Cur, int Days)> TermsAsync()
    {
        static decimal Dec(string? v, decimal fallback) => decimal.TryParse(v, NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? d : fallback;
        return (Dec(await S.Settings.StringAsync("LicenseMonthlyCost"), 30m), Dec(await S.Settings.StringAsync("PaygInteractionCost"), 0.12m),
                await S.Settings.StringAsync("LicenseCurrency") ?? "USD", (Ctx.End - Ctx.Start).Days + 1);
    }

    bool AllShown => Request.Query["show"].FirstOrDefault() == "all";

    protected override async Task BuildAsync()
    {
        var (price, rate, cur, days) = await TermsAsync();
        var floor = AllShown ? 0.01m : price;   // a month's credits at or above this are listed
        var pg = new Paging(Request, Sorts, "credits");
        var rows = await Db.QueryAsync<Row>(Sql("SELECT x.*, COUNT(*) OVER () AS TotalRows FROM x WHERE (@q IS NULL OR x.DisplayName LIKE @q OR x.UserPrincipalName LIKE @q OR x.Department LIKE @q)",
            pg.OrderAndPage() + ";"), Ctx.Args(new { q = pg.Like, rate, floor, days }), timeout: 300);
        pg.Total = rows.FirstOrDefault()?.TotalRows ?? 0;
        decimal Monthly(long m) => m * rate * 30m / days;

        var people = rows.FirstOrDefault()?.People ?? 0;
        var credits = Monthly(rows.FirstOrDefault()?.AllM ?? 0);
        Kpis.Add(new(AllShown ? "Paying in credits" : "Cheaper with a license", Num(people),
            AllShown ? "without a license, with metered use" : $"credits a month at or over the license's {price:#,0.##} {cur}"));
        Kpis.Add(new($"Credits a month ({cur})", Num((long)credits), "their metered use, scaled to 30 days"));
        if (!AllShown)
            Kpis.Add(new($"Saving a month ({cur})", Num((long)Math.Max(0, credits - people * price)), $"licensing them instead, at {price:#,0.##} each"));

        var t = new Table
        {
            Headers = ["Name", "Sign-in", "Department", "Manager", "Interactions", "Metered", "Through agents", "Chat over work content", "Days used",
                       $"Credits a month ({cur})", $"Against the license ({cur})"],
            Numeric = new HashSet<int> { 4, 5, 6, 7, 8, 9, 10 },
            EmptyText = AllShown ? "Nobody without a license has use charged in credits." : "Nobody without a license uses credits worth more than a license.",
        };
        foreach (var r in rows)
        {
            var c = Monthly(r.M);
            t.Rows.Add([r.DisplayName, r.UserPrincipalName, r.Department, r.ManagerName, r.I, r.M, r.A, r.W, r.Days, Math.Round(c, 2), Math.Round(c - price, 2)]);
        }
        CsvTable = t;
        var show = string.Join(" · ", new[] { ("cheaper", "Cheaper with a license"), ("all", "Everyone paying in credits") }.Select(o =>
            $"<a href=\"{WebUtility.HtmlEncode(Ctx.Url(Request.Path, extra: $"show={o.Item1}"))}\"{((o.Item1 == "all") == AllShown ? " class=\"on\"" : "")}>{o.Item2}</a>"));
        var exports = AllShown || pg.Total == 0 ? "" :
            $"<p class=\"sub\">License them: <a href=\"{WebUtility.HtmlEncode(Ctx.Url(Request.Path, extra: "handler=bulk"))}\">Entra bulk add members file</a> · " +
            $"<a href=\"{WebUtility.HtmlEncode(Ctx.Url(Request.Path, extra: "handler=script"))}\">PowerShell script</a>, recorded for License action outcomes.</p>";
        Panels.Add(new("People", new HtmlString($"<p class=\"sub\">Show: {show}</p>" + exports + pg.Controls(Request, SortLabels).Value + t.Render().Value),
            $"Metered: interactions through an agent, or in Copilot Chat that read work content without searching the web, by people with no Copilot license today. " +
            $"Credits a month: those interactions, scaled to 30 days, at {rate:#,0.00##} {cur} each (Admin > Settings); an agent grounded only in public data costs nothing, so this errs high. " +
            "Against the license: the credits less the license cost; above zero, the license is cheaper.", "full", CsvUrl()));
    }

    public Task<IActionResult> OnGetBulkAsync() => ExportAsync(false);
    public Task<IActionResult> OnGetScriptAsync() => ExportAsync(true);

    async Task<IActionResult> ExportAsync(bool script)
    {
        if (!await PrepareAsync()) return Page();
        var (price, rate, _, days) = await TermsAsync();
        var upns = await Db.QueryAsync<string>(Sql("SELECT x.UserPrincipalName FROM x", "ORDER BY x.UserPrincipalName;"),
            Ctx.Args(new { rate, floor = price, days }), timeout: 300);
        var skuId = (await HttpContext.RequestServices.GetRequiredService<CopilotInsights.Data.OpsData>().PoolAsync()).FirstOrDefault(p => p.IsCopilot)?.SkuId.ToString();
        var text = script
            ? CopilotInsights.Data.LicenseExport.Script("upgrade", upns, await S.Settings.StringAsync("LicenseGroupId"), skuId, Viewer.Upn ?? Viewer.WindowsName, DateTime.UtcNow)
            : CopilotInsights.Data.LicenseExport.BulkMembersCsv(upns);
        await CopilotInsights.Data.LicenseActions.RecordAsync(Db, "upgrade", upns, Viewer.Upn ?? Viewer.WindowsName);
        await S.Audit.WriteAsync(Viewer, "Export", Request.Path, Ctx.ScopeLabel, $"license for credits {(script ? "script" : "bulk file")}: {upns.Count} people");
        var name = $"copilot-license-for-credits-{DateTime.UtcNow:yyyyMMdd}";
        return File(System.Text.Encoding.UTF8.GetBytes(text), script ? "text/plain" : "text/csv", script ? name + ".ps1" : name + ".csv");
    }
}
