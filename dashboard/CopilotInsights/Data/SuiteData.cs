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
/// <summary>A suite's seats in the license pool: bought (paid) and assigned.</summary>
public sealed record SuiteSeats(Suite Suite, int Bought, int Assigned);

public sealed record SuiteFitView(IReadOnlyList<Suite> Suites, IReadOnlyList<SuiteRow> Rows, bool PowerBiKnown, string Currency, decimal CopilotPrice, decimal PowerBiPrice,
    IReadOnlyList<SuiteSeats>? Seats = null);

/// <summary>
/// Reads who holds which suite: every SKU each person holds (audit.UserLicenseSku, from the license lookup),
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
    /// <summary>Suite fit, kept once worked out (see <see cref="Db.KeptAsync"/>).</summary>
    public Task<SuiteFitView> FitAsync(ReportContext c, bool scoped) => db.KeptAsync($"SuiteData.Fit:{scoped}", c, () => FitFreshAsync(c, scoped));

    async Task<SuiteFitView> FitFreshAsync(ReportContext c, bool scoped)
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
        /* Disabled or gone as Licenses on disabled accounts says it (OpsData.LeaversAsync): in AD, in Entra, or not found. */
        var hasEntra = await db.ScalarAsync<int>("SELECT CASE WHEN COL_LENGTH(N'audit.CopilotLicense', N'EntraAccountEnabled') IS NULL THEN 0 ELSE 1 END") == 1;
        var entra = hasEntra ? "cl.EntraAccountEnabled" : "CAST(NULL AS BIT)";
        var biKnown = await ops.HasAsync("audit.PowerPlatformUserActivity")
            && await db.ScalarAsync<int>("SELECT CASE WHEN EXISTS (SELECT 1 FROM audit.PowerPlatformUserActivity WHERE Product = 'Power BI') THEN 1 ELSE 0 END;") == 1;

        var k30 = ReportContext.Key(c.End.AddDays(-29));
        var activity = await Pages.Forecasting.ActionsModel.ActivitySqlAsync(db, c, k30);
        var protectedLevels = await ProtectedLevels.SqlLevelsAsync(settings);
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
       CASE WHEN {ProtectedLevels.Not("sh.Upn", protectedLevels)} THEN 0 ELSE 1 END AS Protected,
       CASE WHEN du.AccountEnabled = 0 OR du.DirectoryStatus = N'NotFound' OR cl.DirectoryStatus = N'NotFound' OR {entra} = 0 THEN 0 ELSE 1 END AS Enabled,
       du.LicensedSinceUtc AS CopilotSince
FROM sh
{(scoped ? "JOIN" : "LEFT JOIN")} bi.DimUser AS du ON du.UserPrincipalName = sh.Upn
{(scoped ? c.Scope("du.UserKey") : "")}
LEFT JOIN a ON a.UserKey = du.UserKey
LEFT JOIN addon AS ad ON ad.Upn = sh.Upn
LEFT JOIN pbi AS b ON b.UserPrincipalName = sh.Upn
LEFT JOIN audit.CopilotLicense AS cl ON cl.UserPrincipalName = sh.Upn;", c.Args(new { k30, idleDays }));

        var byPattern = suites.ToDictionary(s => s.Pattern, StringComparer.OrdinalIgnoreCase);
        var today = c.End.Date;
        var list = new List<SuiteRow>();
        foreach (var g in rows.GroupBy(r => r.Upn, StringComparer.OrdinalIgnoreCase))
        {
            /* A person on two suites (E5 left beside E7 after a move up, Office 365 E3 beside Microsoft 365 E5): the highest
               is theirs, Microsoft 365 before Office 365 at the same tier as it holds more; the others are copies. */
            var held = g.Select(r => byPattern.TryGetValue(r.SkuPattern, out var s) ? s : null).OfType<Suite>()
                .GroupBy(s => s.Name).Select(x => x.First()).OrderByDescending(s => s.Tier).ThenBy(s => s.Family == "Microsoft 365" ? 0 : 1).ToList();
            if (held.Count == 0) continue;
            var r = g.First();
            var h = new SuiteHolder(r.Upn, held[0], r.CopilotDays, r.CopilotAddOn == 1, r.PowerBiAddOn == 1, held.Skip(1).ToList(),
                r.PowerBi < 0 ? null : r.PowerBi == 1, r.Protected == 1, r.Enabled == 1, r.CopilotSince);
            list.Add(new(r.DisplayName, r.Upn, r.Department, h, SuiteRules.Fit(h, SuiteRules.Lower(held[0], suites), copilotPrice, biPrice, today, cur)));
        }
        /* Each suite's seats: bought and assigned, from the pool. They are the tenant's, so only where the whole organization is shown. */
        var seats = scoped && c.Root is not null ? [] : (await db.QueryAsync<(string SkuPattern, int Bought, int Assigned)>(@"
SELECT cs.SkuPattern, SUM(s.Enabled + s.Warning), SUM(s.Consumed)
FROM audit.LicenseSku AS s JOIN dash.CostSuite AS cs ON cs.Enabled = 1 AND s.SkuPartNumber LIKE cs.SkuPattern
GROUP BY cs.SkuPattern;")).Where(x => byPattern.ContainsKey(x.SkuPattern))
            .GroupBy(x => byPattern[x.SkuPattern].Name).Select(g => new SuiteSeats(byPattern[g.First().SkuPattern], g.Sum(x => x.Bought), g.Sum(x => x.Assigned))).ToList();
        return new(suites, list, biKnown, cur, copilotPrice, biPrice, seats);
    }

    /// <summary>
    /// The add-on a holder's suite already covers, as an AI saving, defined once for Costs and savings and Suite
    /// fit: the Copilot add-on beside E7, unless the revoke list takes it already (unused, licensed 30 days or more), and
    /// Power BI Pro beside E5 or E7; for enabled holders not at a protected level, as every list treats them.
    /// </summary>
    public static (bool Copilot, bool PowerBi) AddOnCopy(SuiteRow r, DateTime today)
    {
        var h = r.Holder;
        if (!h.Enabled || h.Protected) return (false, false);
        var revoked = h.CopilotDays == 0 && (h.CopilotSince is null || (today - h.CopilotSince.Value).TotalDays >= Pages.Forecasting.ActionsModel.RevokeAfterDays);
        return (h.CopilotAddOn && h.Suite.HasCopilot && !revoked, h.PowerBiAddOn && h.Suite.HasPowerBi);
    }

    /// <summary>
    /// The suites for Costs and savings and Suite fit's own figures, summed the same way for both: those held,
    /// and the moves. A copy of the Copilot add-on or of Power BI Pro is an AI saving, counted with its area on Costs and
    /// savings, so it is not a move here; a second suite is. Seats bought and assigned to nobody are a move too.
    /// </summary>
    public static SuiteSummary Summarize(SuiteFitView fit, decimal suiteCopilotValue, decimal sparePercent = 0)
    {
        /* Seats kept unassigned on purpose (SpareSeatPercent) are not cut. */
        int Beyond(int bought, int assigned) => Math.Max(0, bought - assigned - CostRules.Spare(bought, sparePercent));
        var held = fit.Rows.GroupBy(r => r.Holder.Suite.Name).Select(g =>
        {
            var s = g.First().Holder.Suite;
            var each = (s.HasCopilot ? fit.CopilotPrice : 0) + (s.HasPowerBi ? fit.PowerBiPrice : 0);
            var inc = string.Join(" and ", new[] { s.HasCopilot ? "Microsoft 365 Copilot" : null, s.HasPowerBi ? "Power BI Pro" : null }.OfType<string>());
            return new SuiteLine(s.Name, g.Count(), s.Price, inc, g.Count() * each);
        }).OrderByDescending(l => l.Holders).ToList();
        var moves = fit.Rows.Where(r => r.Fit.Move is not (SuiteMove.Keep or SuiteMove.DropDuplicate)).GroupBy(r => (r.Fit.Move, From: r.Holder.Suite.Name, r.Fit.To))
            .Select(g => new SuiteMoveLine(g.Key.Move, g.Key.From, g.Key.To, g.Count(), Math.Round(g.Sum(r => r.Fit.Saving), 2), Math.Round(g.Sum(r => r.Fit.AtStake), 2), g.First().Fit.Confidence))
            .Concat(fit.Rows.Where(r => r.Fit.Move == SuiteMove.DropDuplicate && r.Holder.Others.Any(o => o.Tier <= r.Holder.Suite.Tier))
                .GroupBy(r => r.Holder.Suite.Name)
                .Select(g => new SuiteMoveLine(SuiteMove.DropDuplicate, g.Key, null, g.Count(), Math.Round(g.Sum(r => r.Holder.Others.Where(o => o.Tier <= r.Holder.Suite.Tier).Sum(o => o.Price)), 2), 0, "High")))
            .Concat((fit.Seats ?? []).Where(x => Beyond(x.Bought, x.Assigned) > 0 && x.Suite.Price > 0)
                .Select(x => new SuiteMoveLine(SuiteMove.CutSeats, x.Suite.Name, null, Beyond(x.Bought, x.Assigned), Beyond(x.Bought, x.Assigned) * x.Suite.Price, 0, "Medium")))
            .OrderBy(m => m.Confidence switch { "High" => 0, "Medium" => 1, _ => 2 }).ThenByDescending(m => m.Saving + m.AtStake).ToList();
        return new(held, moves, held.Sum(l => l.IncludedValue), suiteCopilotValue, fit.PowerBiKnown);
    }
}
