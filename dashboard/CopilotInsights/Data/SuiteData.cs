using System.Globalization;
using CopilotInsights.Infrastructure;

namespace CopilotInsights.Data;

/// <summary>One suite holder, named, with what fits them (<see cref="SuiteRules.Fit"/>).</summary>
public sealed record SuiteRow(string? Name, string Upn, string? Department, SuiteHolder Holder, SuiteFit Fit);

/// <summary>
/// Every suite holder and what fits them, for Licensing › Suite fit and the suites card on Costs and savings. Suites is
/// dash.CostSuite's enabled rows; PowerBiKnown, whether Power BI activity is loaded at all (else nobody's use of E5's
/// Power BI Pro is known, and no E5 move is offered).
/// </summary>
public sealed record SuiteFitView(IReadOnlyList<Suite> Suites, IReadOnlyList<SuiteRow> Rows, bool PowerBiKnown, string Currency, decimal CopilotPrice, decimal PowerBiPrice);

/// <summary>
/// Reads who holds which suite (2.6.6): every SKU each person holds (audit.UserLicenseSku, from the license lookup),
/// matched to dash.CostSuite's patterns; their Copilot use in the last 30 days (the days that needed the license,
/// as Upgrade, train or revoke reads them); their Power BI use, where loaded; their add-ons; their level and account.
/// </summary>
public sealed class SuiteData(Db db, Settings settings, OpsData ops)
{
    static decimal Dec(string? v, decimal fallback) => decimal.TryParse(v, NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? d : fallback;

    sealed class SuiteRec { public string SkuPattern { get; set; } = ""; public string Suite { get; set; } = ""; public string Family { get; set; } = ""; public byte Tier { get; set; } public decimal MonthlyPrice { get; set; } public string Includes { get; set; } = ""; public string? Covers { get; set; } }
    sealed class Rec
    {
        public string Upn { get; set; } = ""; public string SkuPattern { get; set; } = ""; public string? DisplayName { get; set; } public string? Department { get; set; }
        public int CopilotDays { get; set; } public int CopilotAddOn { get; set; } public int PowerBiAddOn { get; set; } public int PowerBi { get; set; }
        public int Protected { get; set; } public int Enabled { get; set; } public DateTime? CopilotSince { get; set; }
    }

    /// <summary>The suites set in dash.CostSuite and turned on; none before 2.6.6's schema.</summary>
    public async Task<IReadOnlyList<Suite>> SuitesAsync()
    {
        if (!await ops.HasAsync("dash.CostSuite")) return [];
        var rows = await db.QueryAsync<SuiteRec>("SELECT SkuPattern, Suite, Family, Tier, MonthlyPrice, Includes, Covers FROM dash.CostSuite WHERE Enabled = 1;");
        return [.. rows.Select(r => new Suite(r.SkuPattern, r.Suite, r.Family, r.Tier, r.MonthlyPrice,
            r.Includes.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase), r.Covers))];
    }

    /// <summary>Every holder of a suite, in the team the context shows when <paramref name="scoped"/>, each with what fits them.</summary>
    public async Task<SuiteFitView> FitAsync(ReportContext c, bool scoped)
    {
        var cur = await settings.StringAsync("LicenseCurrency") ?? "USD";
        var copilotPrice = Dec(await settings.StringAsync("LicenseMonthlyCost"), 30m);
        var biPrice = 14m;
        if (await ops.HasAsync("dash.CostPrice"))
            biPrice = await db.ScalarAsync<decimal?>("SELECT MonthlyPrice FROM dash.CostPrice WHERE Item = N'POWER_BI_PRO';") ?? biPrice;
        var suites = await SuitesAsync();
        if (suites.Count == 0 || !await ops.HasAsync("audit.UserLicenseSku") || !await ops.HasAsync("audit.LicenseSku"))
            return new(suites, [], false, cur, copilotPrice, biPrice);
        var idleDays = (int)Math.Clamp(Dec(await settings.StringAsync("CostIdleDays"), 30m), 7, 180);
        var hasSuiteMark = await db.ScalarAsync<int>("SELECT CASE WHEN COL_LENGTH(N'audit.LicenseSku', N'CopilotInSuite') IS NULL THEN 0 ELSE 1 END;") == 1;
        var biKnown = await ops.HasAsync("audit.PowerPlatformUserActivity")
            && await db.ScalarAsync<int>("SELECT CASE WHEN EXISTS (SELECT 1 FROM audit.PowerPlatformUserActivity WHERE Product = 'Power BI') THEN 1 ELSE 0 END;") == 1;

        var k30 = ReportContext.Key(c.End.AddDays(-29));
        var activity = await Pages.Forecasting.ActionsModel.ActivitySqlAsync(db, k30, c.EndKey);
        var rows = await db.QueryAsync<Rec>($@"
WITH {activity},
sh AS (SELECT u.UserPrincipalName AS Upn, cs.SkuPattern
       FROM audit.UserLicenseSku AS u JOIN audit.LicenseSku AS s ON s.SkuId = u.SkuId
       JOIN dash.CostSuite AS cs ON cs.Enabled = 1 AND s.SkuPartNumber LIKE cs.SkuPattern),
addon AS (SELECT u.UserPrincipalName AS Upn,
                 MAX(CASE WHEN s.IsCopilot = 1 {(hasSuiteMark ? "AND ISNULL(s.CopilotInSuite, 0) = 0" : "")} AND s.SkuPartNumber NOT LIKE N'%STUDIO%' THEN 1 ELSE 0 END) AS Cop,
                 MAX(CASE WHEN s.SkuPartNumber = N'POWER_BI_PRO' THEN 1 ELSE 0 END) AS Bi
          FROM audit.UserLicenseSku AS u JOIN audit.LicenseSku AS s ON s.SkuId = u.SkuId
          WHERE u.UserPrincipalName IN (SELECT Upn FROM sh) GROUP BY u.UserPrincipalName),
pbi AS ({(biKnown ? "SELECT UserPrincipalName, MAX(CASE WHEN ISNULL(ActiveDays30, 0) > 0 OR LastActiveUtc >= DATEADD(DAY, -@idleDays, SYSUTCDATETIME()) THEN 1 ELSE 0 END) AS Used FROM audit.PowerPlatformUserActivity WHERE Product = 'Power BI' GROUP BY UserPrincipalName" : "SELECT CAST(NULL AS NVARCHAR(512)) AS UserPrincipalName, 0 AS Used WHERE 1 = 0")})
SELECT sh.Upn, sh.SkuPattern, du.DisplayName, du.Department, ISNULL(a.P, 0) AS CopilotDays,
       ISNULL(ad.Cop, 0) AS CopilotAddOn, ISNULL(ad.Bi, 0) AS PowerBiAddOn,
       {(biKnown ? "ISNULL(b.Used, 0)" : "-1")} AS PowerBi,
       CASE WHEN {ProtectedLevels.Not("sh.Upn")} THEN 0 ELSE 1 END AS Protected,
       CASE WHEN du.UserKey IS NOT NULL AND du.AccountEnabled = 0 THEN 0 ELSE 1 END AS Enabled,
       du.LicensedSinceUtc AS CopilotSince
FROM sh
{(scoped ? "JOIN" : "LEFT JOIN")} bi.DimUser AS du ON du.UserPrincipalName = sh.Upn
{(scoped ? c.Scope("du.UserKey") : "")}
LEFT JOIN a ON a.UserKey = du.UserKey
LEFT JOIN addon AS ad ON ad.Upn = sh.Upn
LEFT JOIN pbi AS b ON b.UserPrincipalName = sh.Upn;", c.Args(new { k30, idleDays }));

        var byPattern = suites.ToDictionary(s => s.Pattern, StringComparer.OrdinalIgnoreCase);
        var today = c.End.Date;
        var list = new List<SuiteRow>();
        foreach (var g in rows.GroupBy(r => r.Upn, StringComparer.OrdinalIgnoreCase))
        {
            /* A person on two suites (E5 left beside E7 after a move up): the higher is theirs, the lower a copy. */
            var held = g.Select(r => byPattern.TryGetValue(r.SkuPattern, out var s) ? s : null).OfType<Suite>()
                .GroupBy(s => s.Name).Select(x => x.First()).OrderByDescending(s => s.Tier).ToList();
            if (held.Count == 0) continue;
            var r = g.First();
            var h = new SuiteHolder(r.Upn, held[0], r.CopilotDays, r.CopilotAddOn == 1, r.PowerBiAddOn == 1, held.Skip(1).FirstOrDefault(),
                r.PowerBi < 0 ? null : r.PowerBi == 1, r.Protected == 1, r.Enabled == 1, r.CopilotSince);
            list.Add(new(r.DisplayName, r.Upn, r.Department, h, SuiteRules.Fit(h, SuiteRules.Lower(held[0], suites), copilotPrice, biPrice, today, cur)));
        }
        return new(suites, list, biKnown, cur, copilotPrice, biPrice);
    }
}
