using System.Net;
using CopilotInsights.Infrastructure;
using Microsoft.AspNetCore.Html;
using Microsoft.AspNetCore.Mvc;

namespace CopilotInsights.Pages.Forecasting;

/// <summary>The recommendations, person by person: who to upgrade, who to train, whose license to take back.</summary>
public sealed class ActionsModel(PageServices s) : InsightsPage(s)
{
    public override string Title => "Upgrade, train or revoke";
    public override string Persona => "Forecasting";
    public override string? RequiredRole => Roles.Licensing;
    public override string Intro => "Named recommendations from the last 30 days: free Copilot Chat users to upgrade to a Premium license, "
        + "license holders who barely use Copilot to train, and licenses unused long enough to revoke.";

    public override string? OwnWindow => "the last 30 days, whatever the period";
    public const int UpgradeDays = 6;   // of the last 30, on the free Copilot Chat
    public const int LightMax = 2;      // days of use in 30 with a license: train
    public const int RevokeAfterDays = 30;

    /*
        a.D: days with any Copilot use in the last 30. a.P: days with use that needs the Premium license
        (Copilot inside Word, Excel, PowerPoint, Outlook or Teams, or Copilot Chat reading work content;
        dash.UserDay.PremiumUse). Train and revoke look at a.P: a license used only for web Copilot Chat,
        which the free tier gives, is a license not used.
    */
    internal static readonly Dictionary<string, (string Label, string Where, string Action)> Kinds = new()
    {
        ["upgrade"] = ("Upgrade to Premium", $"lic.UserKey IS NULL AND a.D >= {UpgradeDays}", "Upgrade to Premium"),
        ["train"] = ("Train", $"lic.UserKey IS NOT NULL AND a.P BETWEEN 1 AND {LightMax}", "Train, then review"),
        ["revoke"] = ("Revoke", $"lic.UserKey IS NOT NULL AND ISNULL(a.P, 0) = 0 AND (du.LicensedSinceUtc IS NULL OR du.LicensedSinceUtc <= DATEADD(DAY, -{RevokeAfterDays}, @d1))", "Revoke"),
    };

    /// <summary>
    /// The 30-day activity CTE the rules read. Premium days come from dash.UserDay.PremiumUse once the rollups have
    /// been rebuilt with 2.3.0; before that (no premium use recorded at all) they fall back to any use, the 2.2 rule,
    /// so an upgrade never turns every license into a revocation.
    /// </summary>
    internal static async Task<string> ActivitySqlAsync(Db db, int k30, int k1)
    {
        var ready = await db.ScalarAsync<int>(@"SELECT CASE WHEN COL_LENGTH(N'dash.UserDay', N'PremiumUse') IS NOT NULL
    AND EXISTS (SELECT 1 FROM dash.UserDay WHERE DateKey BETWEEN @k30 AND @k1 AND PremiumUse > 0) THEN 1 ELSE 0 END;", new { k30, k1 }) == 1;
        var p = ready ? "COUNT(CASE WHEN ud.PremiumUse > 0 THEN 1 END)" : "COUNT(CASE WHEN ud.Interactions > 0 THEN 1 END)";
        return $@"a AS (SELECT ud.UserKey, COUNT(CASE WHEN ud.Interactions > 0 THEN 1 END) AS D, {p} AS P, SUM(ud.Interactions) AS I
           FROM dash.UserDay AS ud WHERE ud.DateKey BETWEEN @k30 AND @k1 GROUP BY ud.UserKey)";
    }

    internal static async Task<bool> PremiumReadyAsync(Db db, int k30, int k1) => (await ActivitySqlAsync(db, k30, k1)).Contains("PremiumUse");

    static readonly Dictionary<string, string> Sorts = new()
    {
        ["strength"] = "Strength DESC, DisplayName", ["name"] = "DisplayName", ["dept"] = "Department, DisplayName", ["manager"] = "ManagerName, DisplayName",
    };
    static readonly Dictionary<string, string> SortLabels = new() { ["strength"] = "Strongest case first", ["name"] = "Name", ["dept"] = "Department", ["manager"] = "Manager" };

    sealed class Row
    {
        public int UserKey { get; set; }
        public string? DisplayName { get; set; }
        public string UserPrincipalName { get; set; } = "";
        public string? Department { get; set; }
        public string? ManagerName { get; set; }
        public int Days { get; set; }
        public int PremiumDays { get; set; }
        public int Interactions { get; set; }
        public DateTime? LastUsed { get; set; }
        public DateTime? LicensedSinceUtc { get; set; }
        public string? RecordedLicense { get; set; }
        public int Strength { get; set; }
        public int TotalRows { get; set; }
    }

    public Task<IActionResult> OnGetBulkAsync() => ExportAsync(false);
    public Task<IActionResult> OnGetScriptAsync() => ExportAsync(true);

    async Task<IActionResult> ExportAsync(bool script)
    {
        if (!await PrepareAsync()) return Page();
        var kind = Request.Query["kind"].FirstOrDefault() is "revoke" ? "revoke" : "upgrade";
        var k30 = ReportContext.Key(Ctx.End.AddDays(-29));
        var upns = await Db.QueryAsync<string>($@"
WITH {await ActivitySqlAsync(Db, k30, Ctx.EndKey)},
lic AS (SELECT UserKey FROM bi.FactLicensedDay WHERE DateKey = @k1)
SELECT du.UserPrincipalName
FROM bi.DimUser AS du {Ctx.Scope("du.UserKey")}
LEFT JOIN a ON a.UserKey = du.UserKey
LEFT JOIN lic ON lic.UserKey = du.UserKey
WHERE {Kinds[kind].Where} AND ISNULL(du.AccountEnabled, 1) = 1
ORDER BY du.UserPrincipalName;", Ctx.Args(new { k30 }));
        var skuId = (await HttpContext.RequestServices.GetRequiredService<CopilotInsights.Data.OpsData>().PoolAsync()).FirstOrDefault(p => p.IsCopilot)?.SkuId.ToString();
        var text = script
            ? CopilotInsights.Data.LicenseExport.Script(kind, upns, await S.Settings.StringAsync("LicenseGroupId"), skuId, Viewer.Upn ?? Viewer.WindowsName, DateTime.UtcNow)
            : CopilotInsights.Data.LicenseExport.BulkMembersCsv(upns);
        await CopilotInsights.Data.LicenseActions.RecordAsync(Db, kind, upns, Viewer.Upn ?? Viewer.WindowsName);
        await S.Audit.WriteAsync(Viewer, "Export", Request.Path, Ctx.ScopeLabel, $"{kind} {(script ? "script" : "bulk file")}: {upns.Count} people");
        var name = $"copilot-{kind}-{DateTime.UtcNow:yyyyMMdd}";
        return File(System.Text.Encoding.UTF8.GetBytes(text), script ? "text/plain" : "text/csv", script ? name + ".ps1" : name + ".csv");
    }

    protected override async Task BuildAsync()
    {
        var kind = Request.Query["kind"].FirstOrDefault() is { } k && Kinds.ContainsKey(k) ? k : "upgrade";
        var price = decimal.TryParse(await S.Settings.StringAsync("LicenseMonthlyCost"), System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var p) ? p : 30m;
        var currency = await S.Settings.StringAsync("LicenseCurrency") ?? "USD";
        var pg = new Paging(Request, Sorts, "strength");
        var k30 = ReportContext.Key(Ctx.End.AddDays(-29));
        var premiumReady = await PremiumReadyAsync(Db, k30, Ctx.EndKey);
        var rows = await Db.QueryAsync<Row>($@"
WITH {await ActivitySqlAsync(Db, k30, Ctx.EndKey)},
lic AS (SELECT UserKey FROM bi.FactLicensedDay WHERE DateKey = @k1),
base AS (
    SELECT du.UserKey, du.DisplayName, du.UserPrincipalName, du.Department, du.ManagerName, ISNULL(a.D, 0) AS Days, ISNULL(a.P, 0) AS PremiumDays, ISNULL(a.I, 0) AS Interactions,
           du.LicensedSinceUtc,
           CASE WHEN @kind = 'upgrade' THEN ISNULL(a.D, 0) * 100000 + ISNULL(a.I, 0)
                WHEN @kind = 'train' THEN 100000 - ISNULL(a.I, 0)
                ELSE DATEDIFF(DAY, ISNULL(du.LicensedSinceUtc, @d1), @d1) END AS Strength
    FROM bi.DimUser AS du {Ctx.Scope("du.UserKey")}
    LEFT JOIN a ON a.UserKey = du.UserKey
    LEFT JOIN lic ON lic.UserKey = du.UserKey
    WHERE {Kinds[kind].Where}
      AND ISNULL(du.AccountEnabled, 1) = 1
      AND (@q IS NULL OR du.DisplayName LIKE @q OR du.UserPrincipalName LIKE @q OR du.Department LIKE @q)
),
pick AS (SELECT base.*, COUNT(*) OVER () AS TotalRows FROM base {pg.OrderAndPage()})
SELECT pick.* FROM pick
ORDER BY {Sorts[pg.SortKey]};", Ctx.Args(new { k30, kind, q = pg.Like }));
        pg.Total = rows.FirstOrDefault()?.TotalRows ?? 0;

        /* The detail only for the page shown, in one pass for all of them: last use, and the
           license the latest record reports. Looked up person by person, the interaction fact
           (a columnstore) was scanned once per row: 100 seconds a page at 40M interactions. */
        if (rows.Count > 0)
        {
            var keys = rows.Select(r => r.UserKey).ToList();
            var detail = (await Db.QueryAsync<(int UserKey, DateTime? LastUsed, string? RecordedLicense)>(@"
WITH used AS (SELECT x.UserKey, MAX(x.DateKey) AS DateKey FROM dash.UserDay AS x WHERE x.UserKey IN @keys AND x.Interactions > 0 GROUP BY x.UserKey),
rec AS (SELECT f.UserKey, f.LicenseTypeKey, ROW_NUMBER() OVER (PARTITION BY f.UserKey ORDER BY f.DateKey DESC) AS n
        FROM bi.FactInteraction AS f WHERE f.UserKey IN @keys AND f.LicenseTypeKey <> 1)
SELECT k.UserKey, dl.[Date], dash.fnValueName(10, t.LicenseType)
FROM (SELECT DISTINCT UserKey FROM (SELECT UserKey FROM used UNION ALL SELECT UserKey FROM rec WHERE n = 1) AS u) AS k
LEFT JOIN used ON used.UserKey = k.UserKey
LEFT JOIN bi.DimDate AS dl ON dl.DateKey = used.DateKey
LEFT JOIN rec ON rec.UserKey = k.UserKey AND rec.n = 1
LEFT JOIN bi.DimLicenseType AS t ON t.LicenseTypeKey = rec.LicenseTypeKey;", new { keys })).ToDictionary(d => d.UserKey);
            foreach (var r in rows)
                if (detail.TryGetValue(r.UserKey, out var d)) { r.LastUsed = d.LastUsed; r.RecordedLicense = d.RecordedLicense; }
        }

        var t = new Table
        {
            Headers = ["Name", "Sign-in", "Department", "Manager", "Recommendation", "Days used (30)", "Days using Premium features (30)", "Interactions (30)", "Last used", "Licensed since", "License the records report", "Why"],
            Numeric = new HashSet<int> { 5, 6, 7 },
            EmptyText = "No one fits this recommendation.",
        };
        foreach (var r in rows)
        {
            var why = kind switch
            {
                "upgrade" => $"Used the free Copilot Chat on {r.Days} of the last 30 days",
                "train" => r.Days > r.PremiumDays
                    ? $"Licensed; used Premium features on {r.PremiumDays} day{(r.PremiumDays == 1 ? "" : "s")} in 30 (Copilot Chat on {r.Days - r.PremiumDays} more)"
                    : $"Licensed, but used Copilot on {r.Days} day{(r.Days == 1 ? "" : "s")} in 30",
                _ => r.Days > 0
                    ? $"Used only web Copilot Chat ({r.Days} day{(r.Days == 1 ? "" : "s")} in 30), which the free tier gives: nothing that needs the license"
                    : r.LastUsed is null
                    ? "Licensed" + (r.LicensedSinceUtc is null ? "" : $" {(Ctx.End - r.LicensedSinceUtc.Value.Date).Days} days ago") + " and never used"
                    : $"No use for {(Ctx.End - r.LastUsed.Value.Date).Days} days",
            };
            t.Rows.Add([r.DisplayName, r.UserPrincipalName, r.Department, r.ManagerName, Kinds[kind].Action, r.Days, r.PremiumDays, r.Interactions, r.LastUsed, r.LicensedSinceUtc?.Date, r.RecordedLicense, why]);
        }
        CsvTable = t;

        var effect = kind switch
        {
            "upgrade" => $"a cost of {pg.Total * price:#,0} {currency} a month",
            "revoke" => $"a saving of {pg.Total * price:#,0} {currency} a month",
            _ => "no change in cost; review again in 30 days",
        };
        Kpis.Add(new(Kinds[kind].Label, Num(pg.Total), $"people: {effect}", kind == "revoke" && pg.Total > 0 ? "crit" : null));
        var tabs = string.Join(" · ", Kinds.Select(kv =>
            $"<a href=\"{WebUtility.HtmlEncode(Ctx.Url(Request.Path, extra: "kind=" + kv.Key))}\"{(kv.Key == kind ? " class=\"on\"" : "")}>{kv.Value.Label}</a>"));
        var rule = kind switch
        {
            "upgrade" => $"No Premium license, and used the free Copilot Chat on {UpgradeDays} or more of the last 30 days: they already rely on it.",
            "train" => $"A Premium license whose Premium features (Copilot in Word, Excel, PowerPoint, Outlook or Teams, or chat over work content) were used on only 1 to {LightMax} days in the last 30: a short session or a champion usually changes that. Review again after 30 days.",
            _ => $"A Premium license held for at least {RevokeAfterDays} days whose Premium features were not used in the last 30, whether or not the person used web Copilot Chat (which the free tier gives). Offer training first if the role needs Copilot; otherwise revoke and reassign.",
        };
        /* Upgrade and revoke change a license: offer them as files an administrator applies. The dashboard changes nothing. */
        var exports = kind == "train" || pg.Total == 0 ? "" :
            $"<p class=\"sub\">Apply it: <a href=\"{WebUtility.HtmlEncode(Ctx.Url(Request.Path, extra: $"kind={kind}&handler=bulk"))}\">Entra bulk {(kind == "upgrade" ? "add" : "remove")} members file</a> · " +
            $"<a href=\"{WebUtility.HtmlEncode(Ctx.Url(Request.Path, extra: $"kind={kind}&handler=script"))}\">PowerShell script</a> for a license administrator to review and run " +
            $"({(await S.Settings.StringAsync("LicenseGroupId") is null ? "assigns the license directly; set a licensing group in Admin > Targets and assumptions to use group membership instead" : "through the licensing group")}).</p>";
        if (!premiumReady && kind != "upgrade")
            Notice = "Premium-feature use is not in the rollups yet, so train and revoke count any use, as before 2.3.0. An administrator rebuilds the rollups once (Admin > Health, Rebuild every day) to separate Premium features from web Copilot Chat.";
        Panels.Add(new("People", new HtmlString($"<p class=\"sub\">Recommendation: {tabs}</p><p class=\"sub\">{WebUtility.HtmlEncode(rule)}</p>" + exports + pg.Controls(Request, SortLabels).Value + t.Render().Value),
            "\"License the records report\" is what Copilot wrote into the person's latest audit record.", "full", CsvUrl()));
    }
}
