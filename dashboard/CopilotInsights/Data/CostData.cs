using System.Globalization;
using CopilotInsights.Infrastructure;

namespace CopilotInsights.Data;

/// <summary>Copilot in Power BI and Fabric over the last 30 days: its share of the capacity, and so of its cost, and what it does.</summary>
public sealed record CopilotInFabric(decimal Cost, double Share, long Requests, int Users, decimal Value, int Capacities);

/// <summary>One month's spend by area, for the trend.</summary>
public sealed record CostMonth(DateTime Month, IReadOnlyDictionary<string, decimal> ByArea);

/// <summary>Everything Leadership › Costs and savings shows, a month at a time.</summary>
public sealed record CostView(string Currency, IReadOnlyList<CostArea> Areas, IReadOnlyList<Opportunity> Opportunities, IReadOnlyList<CostMonth> Months,
    CopilotInFabric? CopilotInFabric, decimal RealizedSaving, int RealizedLicenses, IReadOnlyList<string> Sources, IReadOnlyList<string> Missing,
    decimal HourlyCost, SuiteSummary? Suites = null, IReadOnlyList<AreaDetail>? Details = null)
{
    public decimal Spend => Areas.Sum(a => a.Spend);
    public decimal Saving => Opportunities.Where(o => o.Kind == OpportunityKind.Save).Sum(o => o.Saving);
    public decimal Value => Areas.Sum(a => a.Value ?? 0);
}

/// <summary>
/// The detail behind one area's figures, for its drill-down on Costs and savings: one table of what it is made
/// of (each capacity, license, account, flow or kind of license), so the figure at the top can be traced to its rows.
/// </summary>
public sealed record AreaDetail(string Area, string Title, IReadOnlyList<string> Headers, IReadOnlyList<object?[]> Rows, IReadOnlySet<int> Numeric, string? Note = null);

/// <summary>A suite held: its holders, price, what it includes, and the list value of that, not counted as AI spend.</summary>
public sealed record SuiteLine(string Suite, int Holders, decimal Price, string Includes, decimal IncludedValue);

/// <summary>A move Licensing › Suite fit finds, summed: from which suite, to what, for how many, saving (or, to train, at stake) how much.</summary>
public sealed record SuiteMoveLine(SuiteMove Move, string From, string? To, int People, decimal Saving, decimal AtStake, string Confidence);

/// <summary>
/// The suites for Costs and savings: those held; the moves Suite fit finds, other than the copies (which are AI
/// savings, counted with their area); the list value of what the suites include (Copilot, Power BI Pro); and the part
/// of Copilot's value that comes from Copilot inside a suite, which is not set against the add-on's spend.
/// </summary>
public sealed record SuiteSummary(IReadOnlyList<SuiteLine> Held, IReadOnlyList<SuiteMoveLine> Moves, decimal IncludedValue, decimal CopilotInSuiteValue, bool PowerBiKnown)
{
    public decimal Saving => Moves.Sum(m => m.Saving);
}

/// <summary>
/// Reads what each source says about the last 30 days and the last six months, and hands it to <see cref="CostRules"/>
/// Every source is optional: a table that is not there, or holds nothing, leaves its area out and is named in
/// <see cref="CostView.Missing"/>, so the page never shows a zero that only means "not loaded".
/// </summary>
public sealed class CostData(Db db, Settings settings, OpsData ops, OpsReports reports, SuiteData suiteData)
{
    static decimal Dec(string? v, decimal fallback) => decimal.TryParse(v, NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? d : fallback;

    sealed class Price
    {
        public string Item { get; set; } = ""; public string Area { get; set; } = ""; public string Name { get; set; } = ""; public string Unit { get; set; } = ""; public decimal MonthlyPrice { get; set; }
        /* A volume price, and the Dataverse capacity a seat brings (NULL before the columns are installed). */
        public int? TierFromSeats { get; set; } public decimal? TierPrice { get; set; } public decimal? DatabaseGbPerSeat { get; set; } public decimal? FileGbPerSeat { get; set; }
        /* The share kept unassigned on purpose (NULL: the SpareSeatPercent setting), and a license bought for its capacity or volume price. */
        public decimal? SparePercent { get; set; } public bool? ForCapacity { get; set; }
        public CostRules.SeatPrice Seat => new(MonthlyPrice, TierFromSeats, TierPrice, DatabaseGbPerSeat ?? 0, FileGbPerSeat ?? 0);
    }
    sealed class Held { public string SkuPartNumber { get; set; } = ""; public int Holders { get; set; } public int Idle { get; set; } public int Known { get; set; } public int Consumed { get; set; } public int Bought { get; set; } public int Twice { get; set; } public string? TwiceIn { get; set; } }

    /// <summary>The cost model, kept once worked out (see <see cref="Db.KeptAsync"/>).</summary>
    public Task<CostView> BuildAsync(ReportContext c) => db.KeptAsync("CostData.Build", c, () => BuildFreshAsync(c));

    async Task<CostView> BuildFreshAsync(ReportContext c)
    {
        var cur = await settings.StringAsync("LicenseCurrency") ?? "USD";
        var copilotPrice = Dec(await settings.StringAsync("LicenseMonthlyCost"), 30m);
        var hourly = Dec(await settings.StringAsync("HourlyCost"), 50m);
        var discount = Dec(await settings.StringAsync("FabricReservedDiscount"), 0.41m);
        var flowMinutes = Dec(await settings.StringAsync("FlowMinutesPerRun"), 3m);
        var biMinutes = Dec(await settings.StringAsync("PowerBiCopilotMinutes"), 3m);
        var idleDays = (int)Math.Clamp(Dec(await settings.StringAsync("CostIdleDays"), 30m), 7, 180);
        /* Seats kept unassigned on purpose, for joiners and moves: not a saving. */
        var sparePercent = Math.Clamp(Dec(await settings.StringAsync("SpareSeatPercent"), 10m), 0, 100);

        var areas = new List<CostArea>();
        var opps = new List<Opportunity>();
        var details = new List<AreaDetail>();
        static HashSet<int> Nums(params int[] i) => [.. i];
        var sources = new List<string>();
        var missing = new List<string>();
        var prices = await ops.HasAsync("dash.CostPrice")
            ? (await db.QueryAsync<Price>(await db.ScalarAsync<int>("SELECT CASE WHEN COL_LENGTH(N'dash.CostPrice', N'DatabaseGbPerSeat') IS NULL THEN 0 ELSE 1 END;") == 1
                ? (await db.ScalarAsync<int>("SELECT CASE WHEN COL_LENGTH(N'dash.CostPrice', N'SparePercent') IS NULL THEN 0 ELSE 1 END;") == 1
                    ? "SELECT Item, Area, Name, Unit, MonthlyPrice, TierFromSeats, TierPrice, DatabaseGbPerSeat, FileGbPerSeat, SparePercent, ForCapacity FROM dash.CostPrice;"
                    : "SELECT Item, Area, Name, Unit, MonthlyPrice, TierFromSeats, TierPrice, DatabaseGbPerSeat, FileGbPerSeat FROM dash.CostPrice;")
                : "SELECT Item, Area, Name, Unit, MonthlyPrice FROM dash.CostPrice;")).ToDictionary(p => p.Item, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, Price>(StringComparer.OrdinalIgnoreCase);
        decimal PriceOf(string item, decimal fallback = 0) => prices.TryGetValue(item, out var p) ? p.MonthlyPrice : fallback;

        /* ---- Microsoft 365 Copilot: the licenses, their recommendations, and the value of the use ---- */
        var k30 = ReportContext.Key(c.End.AddDays(-29));
        var activity = await Pages.Forecasting.ActionsModel.ActivitySqlAsync(db, k30, c.EndKey);
        var kinds = Pages.Forecasting.ActionsModel.Kinds;
        /* S: licensed through a suite only (E7): paid for in the suite's price, not as the add-on. */
        var counts = (await db.QueryAsync<(int Licensed, int Revoke, int Train, int Upgrade, int Suite)>($@"
WITH {activity},
lic AS (SELECT UserKey FROM bi.FactLicensedDay WHERE DateKey = @k1)
SELECT COUNT(x.L), ISNULL(SUM(x.R), 0), ISNULL(SUM(x.T), 0), ISNULL(SUM(x.U), 0), ISNULL(SUM(x.S), 0)
FROM (SELECT lic.UserKey AS L,
             CASE WHEN lic.UserKey IS NOT NULL AND {CopilotSuites.OnlyInSuite("du.UserPrincipalName")} THEN 1 ELSE 0 END AS S,
             CASE WHEN {kinds["revoke"].Where} THEN 1 ELSE 0 END AS R,
             CASE WHEN {kinds["train"].Where} AND {CopilotSuites.NotOnlyInSuite("du.UserPrincipalName")} THEN 1 ELSE 0 END AS T,
             CASE WHEN {kinds["upgrade"].Where} THEN 1 ELSE 0 END AS U
      FROM bi.DimUser AS du
      LEFT JOIN a ON a.UserKey = du.UserKey
      LEFT JOIN lic ON lic.UserKey = du.UserKey
      WHERE ISNULL(du.AccountEnabled, 1) = 1) AS x;", c.Args(new { k30 }))).Single();
        var rate = Dec(await settings.StringAsync("PaygInteractionCost"), 0.12m);
        /* Pay-as-you-go candidates with some Premium use (none at all is a revocation, counted above): their license
           against what their metered use would cost in credits. The same rule as Licensing › Better on pay-as-you-go. */
        var payg = (await db.QueryAsync<(int People, long Metered)>($@"
WITH u AS (SELECT ud.UserKey, SUM(CAST(ud.Interactions AS BIGINT)) AS I, SUM(CAST(ud.PremiumUse AS BIGINT)) AS P, SUM(CAST(ud.AgentInteractions AS BIGINT)) AS A
           FROM dash.UserDay AS ud WHERE ud.DateKey BETWEEN @k30 AND @k1 GROUP BY ud.UserKey HAVING SUM(ud.Interactions) > 0)
SELECT COUNT(*), ISNULL(SUM(u.P + u.A), 0)
FROM u JOIN bi.FactLicensedDay AS ld ON ld.UserKey = u.UserKey AND ld.DateKey = @k1
JOIN bi.DimUser AS du ON du.UserKey = u.UserKey
WHERE (du.LicensedSinceUtc IS NULL OR du.LicensedSinceUtc < DATEADD(DAY, -30, @d1)) AND u.P > 0 AND u.P * 10 <= u.I
  AND {ProtectedLevels.Not("du.UserPrincipalName")} AND {CopilotSuites.NotOnlyInSuite("du.UserPrincipalName")}
  AND ISNULL(du.AccountEnabled, 1) = 1   -- a disabled account is a leaver, counted there
  AND NOT EXISTS (SELECT 1 FROM dash.CopilotSuiteHolder AS csh WHERE csh.Upn = du.UserPrincipalName);   -- the add-on beside E7 is a copy, counted there", c.Args(new { k30 }))).Single();
        /* A disabled account's Copilot inside a suite saves nothing alone: taking the suite back is Suite fit's. */
        var suiteOnly = (await db.QueryAsync<string>("SELECT Upn FROM dash.CopilotSuiteOnly;")).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var leavers = (await ops.LeaversAsync()).Count(l => !suiteOnly.Contains(l.UserPrincipalName));
        var fit = await suiteData.FitAsync(c, scoped: false);
        /* The Copilot add-on's seats in the license pool: bought (so paid) and assigned. A suite with Copilot inside is not one. */
        var pool = await ops.PoolAsync();
        var hasMark = await db.ScalarAsync<int>("SELECT CASE WHEN COL_LENGTH(N'audit.LicenseSku', N'CopilotInSuite') IS NULL THEN 0 ELSE 1 END;") == 1;
        var suiteSkus = hasMark && pool.Count > 0
            ? (await db.QueryAsync<string>("SELECT SkuPartNumber FROM audit.LicenseSku WHERE CopilotInSuite = 1;")).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var addOn = pool.Where(p => p.IsCopilot && !suiteSkus.Contains(p.SkuPartNumber) && !p.SkuPartNumber.Contains("STUDIO", StringComparison.OrdinalIgnoreCase)).ToList();
        var addOnBought = addOn.Sum(p => p.Bought);
        var addOnAssigned = addOn.Sum(p => p.Consumed);
        var value = await reports.ValueAsync(c);
        var covered = OpsReports.DaysCovered(c, await OpsReports.FirstDayAsync(db));
        var copilotValue = CostRules.Monthly(value.ByApp.Sum(x => x.Value), covered);
        var copilotSaving = 0m;
        /* The add-on is priced, Copilot inside a suite (E7) is not: it is in the suite's price, which cannot be
           split. The add-on's spend is the seats bought where the license pool has them (paid, assigned or not), else
           its holders; the value of Copilot is shared by holder, the suite holders' part going with the suite. */
        var standalone = counts.Licensed - counts.Suite;
        var copilotSeats = addOnBought > 0 ? Math.Max(addOnBought, standalone) : standalone;
        /* Copilot's value goes with the use: the share of licensed interactions in the period by people whose Copilot comes
           only with a suite is the suite's; the rest is the add-on's. */
        var use = (await db.QueryAsync<(long All, long Suite)>(@"
SELECT ISNULL(SUM(CAST(ud.Interactions AS BIGINT)), 0), ISNULL(SUM(CASE WHEN cso.Upn IS NOT NULL THEN CAST(ud.Interactions AS BIGINT) END), 0)
FROM dash.UserDay AS ud
JOIN bi.FactLicensedDay AS ld ON ld.UserKey = ud.UserKey AND ld.DateKey = ud.DateKey
JOIN bi.DimUser AS du ON du.UserKey = ud.UserKey
LEFT JOIN dash.CopilotSuiteOnly AS cso ON cso.Upn = du.UserPrincipalName
WHERE ud.DateKey BETWEEN @k0 AND @k1;", c.Args())).Single();
        var suiteUse = use.All > 0 ? (decimal)use.Suite / use.All : counts.Licensed > 0 ? (decimal)counts.Suite / counts.Licensed : 0;
        var suiteCopilotValue = Math.Round(copilotValue * suiteUse, 2);
        var addOnValue = copilotValue - suiteCopilotValue;
        /* The add-on held beside a suite with Copilot in it: paid twice. Those the revoke rule takes already (unused,
           licensed over 30 days, not protected) are counted there, not twice. */
        var today = c.End.Date;
        var twiceCopilot = fit.Rows.Count(r => SuiteData.AddOnCopy(r, today).Copilot);
        /* Power BI Pro beside a suite that includes it, as Suite fit counts it (protected people kept, disabled ones leavers). */
        var twicePowerBi = fit.Rows.Count(r => SuiteData.AddOnCopy(r, today).PowerBi);
        string SuitesWith(Func<Suite, bool> has) => string.Join(" and ", fit.Rows.Select(r => r.Holder.Suite).Where(has).Select(s => s.Name).Distinct().OrderBy(n => n, StringComparer.Ordinal));
        if (counts.Licensed > 0)
        {
            sources.Add("Microsoft 365 Copilot: the audit log and the licenses from Graph");
            if (counts.Revoke > 0)
                opps.Add(new(CostAreas.Copilot, OpportunityKind.Save, $"Revoke {counts.Revoke:#,0} unused Copilot license{(counts.Revoke == 1 ? "" : "s")}",
                    $"{counts.Revoke:#,0} people licensed over {Pages.Forecasting.ActionsModel.RevokeAfterDays} days used nothing that needs the license in the last 30, at {CostRules.Money(copilotPrice, cur)} each.",
                    "Export the list from Upgrade, train or revoke and remove the licenses; give them to the people who ask.", counts.Revoke * copilotPrice, 0, "High", "/forecasting/actions?kind=revoke"));
            if (leavers > 0)
                opps.Add(new(CostAreas.Copilot, OpportunityKind.Save, $"Take back {leavers:#,0} license{(leavers == 1 ? "" : "s")} from disabled accounts",
                    $"{leavers:#,0} Copilot license{(leavers == 1 ? " is" : "s are")} assigned to accounts that are disabled or gone.",
                    "Remove them: nobody can use them, and every day they are paid for.", leavers * copilotPrice, 0, "High", "/licensing/leavers"));
            var paygSaving = Math.Round(payg.People * copilotPrice - payg.Metered * rate, 2);
            if (payg.People > 0 && paygSaving > 0)
                opps.Add(new(CostAreas.Copilot, OpportunityKind.Save, $"Move {payg.People:#,0} light users to pay-as-you-go",
                    $"{payg.People:#,0} license holders used Premium for 10% or less of their Copilot use; credits for it would cost {CostRules.Money(payg.Metered * rate, cur)} a month against {CostRules.Money(payg.People * copilotPrice, cur)} in licenses.",
                    "Remove their licenses and let pay-as-you-go Copilot Chat cover them.", paygSaving, 0, "Medium", "/licensing/payg"));
            if (counts.Train > 0)
                opps.Add(new(CostAreas.Copilot, OpportunityKind.Grow, $"Train {counts.Train:#,0} license holders who barely use it",
                    $"{counts.Train:#,0} people used Copilot's licensed features on 1 or 2 days in the last 30: {CostRules.Money(counts.Train * copilotPrice, cur)} a month buying little.",
                    "Book them into a hands-on session with a champion from their department; review in 30 days.", 0, counts.Train * copilotPrice, "Medium", "/forecasting/actions?kind=train"));
            if (counts.Upgrade > 0)
                opps.Add(new(CostAreas.Copilot, OpportunityKind.Grow, $"License {counts.Upgrade:#,0} heavy free Copilot Chat users",
                    $"{counts.Upgrade:#,0} people without a license used free Copilot Chat on 6 or more days in the last 30: the work content they cannot reach is the value the license adds.",
                    "Hand them the licenses reclaimed above before buying more.", 0, counts.Upgrade * copilotPrice, "Low", "/forecasting/actions?kind=upgrade"));
            if (CostRules.HeldTwice(CostAreas.Copilot, "Microsoft 365 Copilot", SuitesWith(s => s.HasCopilot), twiceCopilot, copilotPrice, cur, "/licensing/suites?copy=addon") is { } tw) opps.Add(tw);
            if (CostRules.Unassigned(CostAreas.Copilot, "Microsoft 365 Copilot", addOnBought, addOnAssigned, copilotPrice, cur, spare: CostRules.Spare(addOnBought, sparePercent)) is { } un) opps.Add(un);
            copilotSaving = opps.Where(o => o.Area == CostAreas.Copilot && o.Kind == OpportunityKind.Save).Sum(o => o.Saving);
            var share = (double)(1 - suiteUse);
            if (copilotSeats > 0)
                areas.Add(new(CostAreas.Copilot, copilotSeats * copilotPrice, copilotSaving, addOnValue,
                    $"{copilotSeats:#,0} add-on licenses at {CostRules.Money(copilotPrice, cur)}" + (addOnBought > addOnAssigned ? $" ({addOnBought:#,0} bought, {addOnAssigned:#,0} assigned)" : "") +
                    (counts.Suite > 0 ? $"; {counts.Suite:#,0} more people hold Copilot inside a suite, in its price and not counted here" : "") +
                    $"; value: {value.ByApp.Sum(x => x.Hours) * 30 / covered * share:#,0} hours saved a month at {CostRules.Money(hourly, cur)} an hour" +
                    (counts.Suite > 0 ? ", the add-on holders' share of Copilot's use" : ""), true, "/leadership/value"));
            /* The detail: each count behind the figures, linked to the list that names the people. */
            details.Add(new(CostAreas.Copilot, "What the figures are made of", ["", "People or seats", $"A month ({cur})", "The list"], [
                ["Add-on licenses bought", addOnBought > 0 ? addOnBought : copilotSeats, copilotSeats * copilotPrice, new Pages.Link("License pool", "/licensing/pool")],
                ["Of them, assigned to nobody and kept spare on purpose", Math.Min(Math.Max(0, addOnBought - addOnAssigned), CostRules.Spare(addOnBought, sparePercent)), 0m, new Pages.Link("License pool", "/licensing/pool")],
                ["Of them, assigned to nobody beyond the spare", Math.Max(0, addOnBought - addOnAssigned - CostRules.Spare(addOnBought, sparePercent)), Math.Max(0, addOnBought - addOnAssigned - CostRules.Spare(addOnBought, sparePercent)) * copilotPrice, new Pages.Link("License pool", "/licensing/pool")],
                ["Licensed through a suite only (E7), in its price", counts.Suite, 0m, new Pages.Link("Suite fit", "/licensing/suites")],
                ["Unused in 30 days: to revoke", counts.Revoke, counts.Revoke * copilotPrice, new Pages.Link("Upgrade, train or revoke", "/forecasting/actions?kind=revoke")],
                ["On disabled accounts", leavers, leavers * copilotPrice, new Pages.Link("Licenses on disabled accounts", "/licensing/leavers")],
                ["Better on pay-as-you-go", payg.People, Math.Max(0, Math.Round(payg.People * copilotPrice - payg.Metered * rate, 2)), new Pages.Link("Better on pay-as-you-go", "/licensing/payg")],
                ["The add-on beside E7, paid twice", twiceCopilot, twiceCopilot * copilotPrice, new Pages.Link("Suite fit", "/licensing/suites?copy=addon")],
                ["Light users to train", counts.Train, counts.Train * copilotPrice, new Pages.Link("Upgrade, train or revoke", "/forecasting/actions?kind=train")],
                ["Heavy free Copilot Chat users to license", counts.Upgrade, counts.Upgrade * copilotPrice, new Pages.Link("Upgrade, train or revoke", "/forecasting/actions?kind=upgrade")],
            ], Nums(1, 2), "Savings for the first rows after the add-on; the last two are opportunities at stake, not savings."));
        }

        /* ---- Power Platform licenses from Graph, priced, with who has not used them ---- */
        var held = new List<Held>();
        if (await ops.HasAsync("audit.LicenseSku") && prices.Count > 0)
        {
            var hasActivity = await ops.HasAsync("audit.PowerPlatformUserActivity") && await db.ScalarAsync<int>("SELECT CASE WHEN EXISTS (SELECT 1 FROM audit.PowerPlatformUserActivity) THEN 1 ELSE 0 END;") == 1;
            var items = prices.Values.Where(p => p.Area is "Power Automate" or "Power Apps" or "Power BI" or "Copilot Studio").Select(p => p.Item).ToList();
            var hasFlows = await ops.HasAsync("audit.PowerAutomateFlow");
            var hasSuites = await ops.HasAsync("dash.CostSuite");
            /* A Power Automate Premium holder who owns a premium flow that ran in the 30 days uses the license, whatever the activity export says.
               A holder whose suite includes the license (Power BI Pro in E5 or E7) holds it twice: counted as that, not as idle. */
            held = await db.QueryAsync<Held>($@"
WITH flowowners AS ({(hasFlows ? "SELECT DISTINCT OwnerUpn FROM audit.PowerAutomateFlow WHERE IsPremium = 1 AND [State] = 'Started' AND Runs30 > 0" : "SELECT CAST(NULL AS NVARCHAR(256)) AS OwnerUpn WHERE 1 = 0")}),
known AS ({(hasActivity ? "SELECT DISTINCT Product FROM audit.PowerPlatformUserActivity" : "SELECT CAST(NULL AS VARCHAR(32)) AS Product WHERE 1 = 0")}),
x AS (SELECT s.SkuPartNumber, s.Consumed, s.Enabled + s.Warning AS Bought, u.UserPrincipalName AS Upn,
             CASE WHEN u.UserPrincipalName IS NOT NULL AND k.Product IS NOT NULL AND tw.Suite IS NULL
                       AND {(hasActivity ? "ISNULL(a.ActiveDays30, 0) = 0 AND (a.LastActiveUtc IS NULL OR a.LastActiveUtc < DATEADD(DAY, -@idleDays, SYSUTCDATETIME()))" : "1 = 0")}
                       AND NOT (p.Product = 'Power Automate' AND fo.OwnerUpn IS NOT NULL) THEN 1 ELSE 0 END AS Idle,
             CASE WHEN k.Product IS NOT NULL THEN 1 ELSE 0 END AS Known,
             CASE WHEN tw.Suite IS NOT NULL THEN 1 ELSE 0 END AS Twice, tw.Suite AS TwiceIn
      FROM audit.LicenseSku AS s
      CROSS APPLY (SELECT CASE WHEN s.SkuPartNumber LIKE N'POWERAUTOMATE%' OR s.SkuPartNumber LIKE N'FLOW%' THEN 'Power Automate'
                               WHEN s.SkuPartNumber LIKE N'POWERAPPS%' THEN 'Power Apps' WHEN s.SkuPartNumber LIKE N'%BI%' THEN 'Power BI' ELSE '' END AS Product) AS p
      LEFT JOIN audit.UserLicenseSku AS u ON u.SkuId = s.SkuId
      {(hasActivity ? "LEFT JOIN audit.PowerPlatformUserActivity AS a ON a.UserPrincipalName = u.UserPrincipalName AND a.Product = p.Product" : "")}
      LEFT JOIN flowowners AS fo ON fo.OwnerUpn = u.UserPrincipalName
      LEFT JOIN known AS k ON k.Product = p.Product
      /* The suite that includes this license, if the holder has one: a seek on the holder's own licenses (the primary key). */
      OUTER APPLY ({(hasSuites ? @"SELECT TOP (1) cs.Suite FROM audit.UserLicenseSku AS u2 JOIN audit.LicenseSku AS s2 ON s2.SkuId = u2.SkuId
                   JOIN dash.CostSuite AS cs ON cs.Enabled = 1 AND cs.Includes <> N'' AND s2.SkuPartNumber LIKE cs.SkuPattern
                   WHERE u2.UserPrincipalName = u.UserPrincipalName AND N',' + REPLACE(cs.Includes, N' ', N'') + N',' LIKE N'%,' + s.SkuPartNumber + N',%'" : "SELECT CAST(NULL AS NVARCHAR(100)) AS Suite WHERE 1 = 0")}) AS tw
      WHERE s.SkuPartNumber IN @items)
SELECT SkuPartNumber, MAX(Consumed) AS Consumed, MAX(Bought) AS Bought, COUNT(Upn) AS Holders, SUM(Idle) AS Idle, MAX(Known) AS Known,
       SUM(Twice) AS Twice, CASE WHEN MIN(TwiceIn) = MAX(TwiceIn) THEN MIN(TwiceIn) ELSE MIN(TwiceIn) + N' and ' + MAX(TwiceIn) END AS TwiceIn
FROM x GROUP BY SkuPartNumber;", new { items, idleDays });
            if (held.Count > 0) sources.Add("Power Platform licenses: Graph's license pool, priced from Admin's price list");
            if (held.Count > 0 && !hasActivity) missing.Add("Power Platform activity (who used Power BI, Power Apps and Power Automate): load it to find idle licenses");
        }
        /* The seats bought, which are paid whether assigned or not (the assigned, where the pool says fewer were bought). */
        decimal Spend(string area) => held.Where(h => prices.TryGetValue(h.SkuPartNumber, out var p) && p.Area == area).Sum(h => prices[h.SkuPartNumber].Seat.Monthly(Math.Max(h.Bought, h.Consumed)));
        /* The Dataverse capacity left before the tenant is over its entitlement, from the capacity report's last
           day: what cutting seats that bring capacity (Power Apps and Power Automate Premium) may give up. */
        CostRules.DataverseRoom? room = null;
        if (await ops.HasAsync("audit.PowerPlatformCapacityDay"))
        {
            var left = await db.QueryAsync<(string CapacityType, decimal Left)>(@"
SELECT CapacityType, ISNULL(EntitledGb, 0) - ISNULL(UsedGb, 0) FROM audit.PowerPlatformCapacityDay
WHERE [Day] = (SELECT MAX([Day]) FROM audit.PowerPlatformCapacityDay) AND CapacityType IN ('Database', 'File');");
            if (left.Count > 0)
                room = new(left.Where(x => x.CapacityType == "Database").Select(x => (decimal?)x.Left).FirstOrDefault(), left.Where(x => x.CapacityType == "File").Select(x => (decimal?)x.Left).FirstOrDefault(),
                           PriceOf("DATAVERSE_DATABASE_GB", 40), PriceOf("DATAVERSE_FILE_GB", 2));
        }
        /* The detail for a licensed area: each license, bought, assigned, idle and held twice, and what it costs. */
        void LicenseDetail(string area, string displayArea)
        {
            var rows = held.Where(h => prices.TryGetValue(h.SkuPartNumber, out var p) && p.Area == area).OrderByDescending(h => prices[h.SkuPartNumber].Seat.Monthly(Math.Max(h.Bought, h.Consumed)))
                .Select(h => new object?[] { prices[h.SkuPartNumber].Name, h.SkuPartNumber, h.Bought, h.Consumed, Math.Max(0, h.Bought - h.Consumed), h.Known > 0 ? h.Idle : null, h.Twice,
                    prices[h.SkuPartNumber].Seat.Each(Math.Max(h.Bought, h.Consumed)), prices[h.SkuPartNumber].Seat.Monthly(Math.Max(h.Bought, h.Consumed)) }).ToList();
            if (rows.Count > 0)
                details.Add(new(displayArea, "Each license", ["License", "SKU part number", "Bought", "Assigned", "Assigned to nobody", "Idle 30 days", "Held beside a suite with it", $"Price ({cur})", $"A month ({cur})"],
                    rows, Nums(2, 3, 4, 5, 6, 7, 8), "From Graph's license pool, priced from Admin's list. Idle: no use of the product in the window (a dash where its activity is not loaded)."));
        }
        void Idle(string area, string displayArea)
        {
            LicenseDetail(area, displayArea);
            foreach (var h in held.Where(h => prices.TryGetValue(h.SkuPartNumber, out var p) && p.Area == area))
            {
                var pr = prices[h.SkuPartNumber];
                /* Each cut priced as it would really save: the seats left at the volume price or not, and the Dataverse
                   capacity given up with them. The unassigned seats first (no one loses anything), then the idle ones. */
                var seats = Math.Max(h.Bought, h.Consumed);
                var price = pr.Seat.Each(seats);
                var spare = CostRules.Spare(h.Bought, pr.SparePercent ?? sparePercent);
                var forCapacity = pr.ForCapacity == true;
                var unCut = pr.Unit == "user" && !forCapacity ? CostRules.CutSeats(pr.Seat, seats, Math.Max(0, h.Bought - h.Consumed - spare), room, cur) : null;
                /* A license bought for its capacity or volume price: an idle seat is one to give someone else, not money back. */
                var idleCut = pr.Unit != "flow" && h.Known > 0 && !forCapacity ? CostRules.CutSeats(pr.Seat, seats - (unCut?.Seats ?? 0), h.Idle, room, cur) : null;
                if (pr.Unit != "flow" && h.Known > 0 && !forCapacity && CostRules.IdleLicenses(displayArea, pr.Name, h.Holders, h.Idle, price, cur, cut: idleCut) is { } o) opps.Add(o);
                var twice = h.SkuPartNumber.Equals("POWER_BI_PRO", StringComparison.OrdinalIgnoreCase) ? twicePowerBi : h.Twice;
                if (CostRules.HeldTwice(displayArea, pr.Name, h.TwiceIn ?? "the suite", twice, price, cur, "/licensing/suites?copy=addon") is { } tw) opps.Add(tw);
                /* Per-user licenses only: a Process license is assigned to a flow, a pack to the tenant. */
                if (pr.Unit == "user" && CostRules.Unassigned(displayArea, pr.Name, h.Bought, h.Consumed, price, cur, cut: unCut, spare: spare, forCapacity: forCapacity) is { } un) opps.Add(un);
            }
        }

        /* Power BI licenses */
        var biSpend = Spend("Power BI");
        if (biSpend > 0)
        {
            Idle("Power BI", CostAreas.PowerBi);
            areas.Add(new(CostAreas.PowerBi, biSpend, opps.Where(o => o.Area == CostAreas.PowerBi && o.Kind == OpportunityKind.Save).Sum(o => o.Saving), null,
                Basis(held, prices, "Power BI", cur), true));
        }

        /* ---- Power Automate: licenses, flows and their runs ---- */
        var paSpend = Spend("Power Automate");
        decimal? flowValue = null;
        if (await ops.HasAsync("audit.PowerAutomateFlow"))
        {
            var f = (await db.QueryAsync<(int Flows, int Started, int Premium, long Runs, decimal Hours, int LightProcess, int LightOwnersPremium, int Failing)>(@"
WITH prem AS (SELECT DISTINCT u.UserPrincipalName FROM audit.UserLicenseSku AS u JOIN audit.LicenseSku AS s ON s.SkuId = u.SkuId
              WHERE s.SkuPartNumber IN (N'POWERAUTOMATE_ATTENDED_RPA', N'FLOW_PER_USER'))
SELECT COUNT(*), SUM(CASE WHEN f.[State] = 'Started' THEN 1 ELSE 0 END), SUM(CASE WHEN f.IsPremium = 1 THEN 1 ELSE 0 END),
       ISNULL(SUM(CAST(f.Runs30 AS BIGINT)), 0),
       ISNULL(SUM((ISNULL(f.Runs30, 0) - ISNULL(f.FailedRuns30, 0)) * ISNULL(f.MinutesSavedPerRun, @minutes) / 60.0), 0),
       SUM(CASE WHEN f.LicenseModel = 'Process' AND ISNULL(f.Runs30, 0) < 100 THEN 1 ELSE 0 END),
       SUM(CASE WHEN f.LicenseModel = 'Process' AND ISNULL(f.Runs30, 0) < 100 AND pr.UserPrincipalName IS NOT NULL THEN 1 ELSE 0 END),
       SUM(CASE WHEN f.[State] = 'Started' AND f.Runs30 >= 10 AND f.FailedRuns30 * 3 >= f.Runs30 THEN 1 ELSE 0 END)
FROM audit.PowerAutomateFlow AS f LEFT JOIN prem AS pr ON pr.UserPrincipalName = f.OwnerUpn;", new { minutes = flowMinutes })).Single();
            if (f.Flows > 0)
            {
                sources.Add($"Power Automate: {f.Flows:#,0} cloud flows from the flow inventory");
                flowValue = Math.Round(f.Hours * hourly, 2);
                if (CostRules.LightProcessFlows(f.LightProcess, f.LightOwnersPremium, PriceOf("POWERAUTOMATE_UNATTENDED_RPA", 150), PriceOf("POWERAUTOMATE_ATTENDED_RPA", 15), cur) is { } lp) opps.Add(lp);
                if (f.Failing > 0)
                    opps.Add(new(CostAreas.Automate, OpportunityKind.Risk, $"Fix {f.Failing:#,0} flow{(f.Failing == 1 ? " failing a third of its" : "s failing a third of their")} runs or more",
                        $"{f.Failing:#,0} started flow{(f.Failing == 1 ? "" : "s")} failed on at least one run in three in the last 30 days: the work they automate is being done by hand, or not at all.",
                        "Send each owner the failure history from the Power Automate admin center; a failing premium flow still holds its license.", 0,
                        Math.Round(flowValue.Value * f.Failing / Math.Max(1, f.Started) , 2), "Medium"));
                /* The detail: the flows worth most, and those failing. */
                var top = await db.QueryAsync<(string Env, string Name, string? Owner, string? Model, int Runs, int Failed, decimal Minutes)>(@"
SELECT TOP (15) EnvironmentName, ISNULL(FlowName, FlowId), OwnerUpn, LicenseModel, ISNULL(Runs30, 0), ISNULL(FailedRuns30, 0), ISNULL(MinutesSavedPerRun, @minutes)
FROM audit.PowerAutomateFlow WHERE [State] = 'Started'
ORDER BY (ISNULL(Runs30, 0) - ISNULL(FailedRuns30, 0)) * ISNULL(MinutesSavedPerRun, @minutes) DESC, FlowName;", new { minutes = flowMinutes });
                if (top.Count > 0)
                    details.Add(new(CostAreas.Automate, "The flows worth most", ["Flow", "Environment", "Owner", "License", "Runs (30 days)", "Failed", "Minutes a run", $"Value a month ({cur})"],
                        [.. top.Select(x => new object?[] { x.Name, x.Env, x.Owner, x.Model, x.Runs, x.Failed, x.Minutes, Math.Round((x.Runs - x.Failed) * x.Minutes / 60m * hourly, 0) })],
                        Nums(4, 5, 6, 7), "Successful runs at the minutes each saves (the inventory's, or Admin's default), at the hourly cost."));
                var failing = await db.QueryAsync<(string Env, string Name, string? Owner, int Runs, int Failed)>(@"
SELECT TOP (15) EnvironmentName, ISNULL(FlowName, FlowId), OwnerUpn, Runs30, FailedRuns30
FROM audit.PowerAutomateFlow WHERE [State] = 'Started' AND Runs30 >= 10 AND FailedRuns30 * 3 >= Runs30
ORDER BY FailedRuns30 DESC, FlowName;");
                if (failing.Count > 0)
                    details.Add(new(CostAreas.Automate, "Flows failing a third of their runs or more", ["Flow", "Environment", "Owner", "Runs (30 days)", "Failed", "Failure rate"],
                        [.. failing.Select(x => new object?[] { x.Name, x.Env, x.Owner, x.Runs, x.Failed, $"{(double)x.Failed / Math.Max(1, x.Runs):P0}" })], Nums(3, 4),
                        "Send each owner the failure history from the Power Automate admin center."));
            }
        }
        if (paSpend > 0 || flowValue is not null)
        {
            Idle("Power Automate", CostAreas.Automate);
            areas.Add(new(CostAreas.Automate, paSpend, opps.Where(o => o.Area == CostAreas.Automate && o.Kind == OpportunityKind.Save).Sum(o => o.Saving), flowValue,
                Basis(held, prices, "Power Automate", cur) + (flowValue is null ? "" : $"; value: the flows' successful runs at {flowMinutes:0.#} minutes each unless set per flow"), true));
        }

        /* ---- Power Apps and Dataverse ---- */
        var appsSpend = Spend("Power Apps");
        var dvSpend = 0m;
        if (await ops.HasAsync("audit.PowerPlatformCapacityDay"))
        {
            var cap = await db.QueryAsync<(string CapacityType, decimal EntitledGb, decimal UsedGb)>(@"
SELECT CapacityType, ISNULL(EntitledGb, 0), ISNULL(UsedGb, 0) FROM audit.PowerPlatformCapacityDay
WHERE [Day] = (SELECT MAX([Day]) FROM audit.PowerPlatformCapacityDay);");
            if (cap.Count > 0) sources.Add("Dataverse capacity: the Power Platform admin center's capacity report");
            foreach (var x in cap)
            {
                var price = PriceOf($"DATAVERSE_{x.CapacityType.ToUpperInvariant()}_GB");
                dvSpend += Math.Max(0, x.UsedGb - x.EntitledGb) * price;
                if (CostRules.DataverseOverage(x.CapacityType, x.EntitledGb, x.UsedGb, price, cur) is { } o) opps.Add(o);
            }
            if (cap.Count > 0)
                details.Add(new(CostAreas.Apps, "Dataverse capacity", ["Capacity", "Entitled (GB)", "Used (GB)", "Over (GB)", $"Price a GB ({cur})", $"A month ({cur})"],
                    [.. cap.Select(x => new object?[] { x.CapacityType, x.EntitledGb, x.UsedGb, Math.Max(0, x.UsedGb - x.EntitledGb), PriceOf($"DATAVERSE_{x.CapacityType.ToUpperInvariant()}_GB"),
                        Math.Max(0, x.UsedGb - x.EntitledGb) * PriceOf($"DATAVERSE_{x.CapacityType.ToUpperInvariant()}_GB") })], Nums(1, 2, 3, 4, 5), "The admin center's capacity report, its last day."));
        }
        if (appsSpend > 0 || dvSpend > 0)
        {
            Idle("Power Apps", CostAreas.Apps);
            areas.Add(new(CostAreas.Apps, appsSpend + dvSpend, opps.Where(o => o.Area == CostAreas.Apps && o.Kind == OpportunityKind.Save).Sum(o => o.Saving), null,
                Basis(held, prices, "Power Apps", cur) + (dvSpend > 0 ? $"; Dataverse capacity over the entitlement, {CostRules.Money(dvSpend, cur)} a month" : ""), true));
        }

        /* ---- Copilot Studio and AI Builder credits ---- */
        var packs = held.Where(h => prices.TryGetValue(h.SkuPartNumber, out var p) && p.Area == "Copilot Studio").Sum(h => Math.Max(h.Bought, h.Consumed));
        var packPrice = PriceOf("COPILOT_STUDIO_PACK", 200);
        if (await ops.HasAsync("audit.PowerPlatformCreditDay"))
        {
            var cr = (await db.QueryAsync<(decimal Prepaid, decimal Payg, decimal PaygCost, int Days)>(@"
WITH last AS (SELECT MAX([Day]) AS D FROM audit.PowerPlatformCreditDay)
SELECT ISNULL(SUM(CASE WHEN Billing = 'Prepaid' AND Product = 'Copilot Studio' THEN Credits END), 0),
       ISNULL(SUM(CASE WHEN Billing = 'PayAsYouGo' THEN Credits END), 0), ISNULL(SUM(CASE WHEN Billing = 'PayAsYouGo' THEN Cost END), 0),
       COUNT(DISTINCT [Day])
FROM audit.PowerPlatformCreditDay WHERE [Day] > DATEADD(DAY, -30, (SELECT D FROM last));")).Single();
            if (cr.Days > 0)
            {
                sources.Add("Copilot Credits: the Power Platform admin center's consumption report");
                var paygCost = CostRules.Monthly(cr.PaygCost, cr.Days);
                /* A credit commitment in force on the last day: the metered credits draw on it (Forecasting › Credit commitment). */
                var committed = await ops.HasAsync("dash.CreditCommitment") && await db.ScalarAsync<int>(@"
SELECT CASE WHEN EXISTS (SELECT 1 FROM dash.CreditCommitment AS c CROSS JOIN (SELECT MAX([Day]) AS D FROM audit.PowerPlatformCreditDay) AS l
                         WHERE c.StartDate <= l.D AND c.EndDate >= l.D) THEN 1 ELSE 0 END;") == 1;
                if (CostRules.Credits(CostRules.Monthly(cr.Prepaid, cr.Days), packs, CostRules.Monthly(cr.Payg, cr.Days), paygCost, packPrice, cur, committed: committed) is { } o) opps.Add(o);
                areas.Add(new(CostAreas.Studio, packs * packPrice + paygCost, opps.Where(x => x.Area == CostAreas.Studio && x.Kind == OpportunityKind.Save).Sum(x => x.Saving), null,
                    $"{packs:#,0} prepaid pack{(packs == 1 ? "" : "s")} at {CostRules.Money(packPrice, cur)}, and {CostRules.Money(paygCost, cur)} of pay-as-you-go credits a month; the agents' value is in Microsoft 365 Copilot's", true, "/agents/credits"));
                details.Add(new(CostAreas.Studio, "Credits, billed", ["", "Credits a month", $"A month ({cur})", "Where to look"], [
                    ["Prepaid packs", packs * 25000m, packs * packPrice, new Pages.Link("Agents costing credits", "/agents/credits")],
                    ["Of them, used", Math.Round(CostRules.Monthly(cr.Prepaid, cr.Days)), null, new Pages.Link("Credit use", "/licensing/credit-use")],
                    ["Pay-as-you-go, beyond the packs", Math.Round(CostRules.Monthly(cr.Payg, cr.Days)), paygCost, new Pages.Link(committed ? "Credit commitment" : "Credit use", committed ? "/forecasting/credit-commitment" : "/licensing/credit-use")],
                    /* Where the credits are charged, by line of business. */
                    ["Charged to the lines of business", null, null, new Pages.Link("Credit chargeback", "/leadership/credit-chargeback")],
                ], Nums(1, 2), "What the Power Platform admin center billed. Licensing › Credit use estimates who used them, person by person, from the audit log." +
                    (committed ? " A credit commitment is in force: the pay-as-you-go credits draw on it (Forecasting › Credit commitment), so buying packs for them is no saving." : "")));
            }
        }

        /* ---- Fabric, and Copilot in Power BI ---- */
        CopilotInFabric? cif = null;
        if (await ops.HasAsync("audit.FabricCapacityDay"))
        {
            var days = await db.QueryAsync<FabricDay>(@"
WITH last AS (SELECT CapacityId, MAX([Day]) AS D FROM audit.FabricCapacityDay GROUP BY CapacityId)
SELECT f.CapacityId, CAST(f.[Day] AS DATETIME2) AS [Day], f.CapacityName AS Name, f.Sku, f.CapacityUnits, f.Billing, f.ActiveHours, f.CuSecondsUsed, f.PeakPercent, f.ThrottledMinutes, f.Cost
FROM audit.FabricCapacityDay AS f JOIN last AS l ON l.CapacityId = f.CapacityId AND f.[Day] > DATEADD(DAY, -30, l.D);");
            var copilot = await ops.HasAsync("audit.FabricWorkloadDay")
                ? await db.QueryAsync<(string CapacityId, decimal Copilot, decimal All, long Requests, int Users)>(@"
WITH last AS (SELECT CapacityId, MAX([Day]) AS D FROM audit.FabricWorkloadDay GROUP BY CapacityId)
SELECT w.CapacityId, ISNULL(SUM(CASE WHEN w.Workload LIKE N'Copilot%' THEN w.CuSeconds END), 0), ISNULL(SUM(w.CuSeconds), 0),
       ISNULL(SUM(CASE WHEN w.Workload LIKE N'Copilot%' THEN w.Operations END), 0), ISNULL(MAX(CASE WHEN w.Workload LIKE N'Copilot%' THEN w.Users END), 0)
FROM audit.FabricWorkloadDay AS w JOIN last AS l ON l.CapacityId = w.CapacityId AND w.[Day] > DATEADD(DAY, -30, l.D)
GROUP BY w.CapacityId;")
                : [];
            if (days.Count > 0)
            {
                sources.Add($"Fabric: {days.Select(d => d.CapacityId).Distinct().Count()} capacities from the Capacity Metrics app");
                var caps = days.GroupBy(d => d.CapacityId).Select(g =>
                {
                    var s = CostRules.Summarize(g.ToList());
                    var w = copilot.FirstOrDefault(x => x.CapacityId == g.Key);
                    return s with { CopilotShare = w.All > 0 ? (double)(w.Copilot / w.All) : 0, CopilotRequests = w.Requests, CopilotUsers = w.Users };
                }).ToList();
                foreach (var cap in caps) opps.AddRange(CostRules.Fabric(cap, discount, cur));
                details.Add(new(CostAreas.Fabric, "Each capacity", ["Capacity", "SKU", "Billing", $"A month ({cur})", "Average use", "Busiest days (95th percentile)", "Weekends", "Throttled (minutes)", "Copilot's share", "Copilot requests"],
                    [.. caps.OrderByDescending(x => x.Cost).Select(x => new object?[] { x.Name, x.Sku, x.Billing, Math.Round(CostRules.Monthly(x.Cost, x.Days)), $"{x.AvgPercent / 100:P0}", $"{x.PeakPercent / 100:P0}",
                        $"{x.WeekendPercent / 100:P0}", x.ThrottledMinutes, x.CopilotShare > 0 ? $"{x.CopilotShare:P1}" : null, x.CopilotRequests > 0 ? x.CopilotRequests : null })],
                    Nums(3, 7, 9), "The Capacity Metrics app's last 30 days of each capacity, with its cost from Azure Cost Management."));
                var spend = caps.Sum(x => CostRules.Monthly(x.Cost, x.Days));
                var copilotCost = caps.Sum(x => CostRules.Monthly(x.Cost, x.Days) * (decimal)x.CopilotShare);
                var requests = caps.Sum(x => x.CopilotRequests);
                decimal? biValue = null;
                if (requests > 0)
                {
                    biValue = Math.Round(requests * biMinutes / 60m * hourly, 2);
                    cif = new(Math.Round(copilotCost, 2), spend > 0 ? (double)(copilotCost / spend) : 0, requests, caps.Sum(x => x.CopilotUsers), biValue.Value, caps.Count(x => x.CopilotRequests > 0));
                }
                areas.Add(new(CostAreas.Fabric, spend, opps.Where(o => o.Area == CostAreas.Fabric && o.Kind == OpportunityKind.Save).Sum(o => o.Saving), biValue,
                    $"{caps.Count} capacit{(caps.Count == 1 ? "y" : "ies")}: {string.Join(", ", caps.Select(x => $"{x.Name} {x.Sku}"))}" +
                    (biValue is null ? "" : $"; value: Copilot in Power BI requests at {biMinutes:0.#} minutes each"), true));
            }
        }

        /* ---- GitHub Copilot ---- */
        if (await ops.HasAsync("audit.GitHubCopilotSeat"))
        {
            var g = (await db.QueryAsync<(int Seats, int Idle, decimal IdleSpend, decimal SeatSpend, decimal Billed, int BilledDays)>(@"
WITH s AS (SELECT CASE WHEN PlanType = 'enterprise' THEN @ent ELSE @bus END AS Price,
                  CASE WHEN (LastActivityUtc IS NULL AND (CreatedUtc IS NULL OR CreatedUtc < DATEADD(DAY, -@idleDays, SYSUTCDATETIME())))
                         OR LastActivityUtc < DATEADD(DAY, -@idleDays, SYSUTCDATETIME()) THEN 1 ELSE 0 END AS Idle
           FROM audit.GitHubCopilotSeat)
SELECT COUNT(*), ISNULL(SUM(Idle), 0), ISNULL(SUM(Idle * Price), 0), ISNULL(SUM(Price), 0),
       ISNULL((SELECT SUM(NetAmount) FROM audit.GitHubCopilotCost WHERE UsageDate > DATEADD(DAY, -30, (SELECT MAX(UsageDate) FROM audit.GitHubCopilotCost))), 0),
       ISNULL((SELECT COUNT(DISTINCT UsageDate) FROM audit.GitHubCopilotCost WHERE UsageDate > DATEADD(DAY, -30, (SELECT MAX(UsageDate) FROM audit.GitHubCopilotCost))), 0)
FROM s;", new { ent = PriceOf("GITHUB_COPILOT_ENTERPRISE", 39), bus = PriceOf("GITHUB_COPILOT_BUSINESS", 19), idleDays })).Single();
            if (g.Seats > 0)
            {
                sources.Add("GitHub Copilot: seats and the bill from GitHub's API");
                if (CostRules.IdleSeats(g.Seats, g.Idle, g.IdleSpend, cur) is { } o) opps.Add(o);
                var plans = await db.QueryAsync<(string? Plan, int Seats, int Idle)>(@"
SELECT PlanType, COUNT(*), SUM(CASE WHEN (LastActivityUtc IS NULL AND (CreatedUtc IS NULL OR CreatedUtc < DATEADD(DAY, -@idleDays, SYSUTCDATETIME())))
                                     OR LastActivityUtc < DATEADD(DAY, -@idleDays, SYSUTCDATETIME()) THEN 1 ELSE 0 END)
FROM audit.GitHubCopilotSeat GROUP BY PlanType;", new { idleDays });
                details.Add(new(CostAreas.GitHub, "Seats by plan", ["Plan", "Seats", "Idle", $"Price a seat ({cur})", $"Idle a month ({cur})"],
                    [.. plans.Select(p => { var price = p.Plan == "enterprise" ? PriceOf("GITHUB_COPILOT_ENTERPRISE", 39) : PriceOf("GITHUB_COPILOT_BUSINESS", 19);
                        return new object?[] { p.Plan ?? "(not given)", p.Seats, p.Idle, price, p.Idle * price }; })],
                    Nums(1, 2, 3, 4), "GitHub's seat list; idle: no Copilot activity in the window, or never since the seat was given."));
                var spend = g.BilledDays >= 20 ? CostRules.Monthly(g.Billed, g.BilledDays) : g.SeatSpend;
                areas.Add(new(CostAreas.GitHub, spend, opps.Where(x => x.Area == CostAreas.GitHub).Sum(x => x.Saving), null,
                    g.BilledDays >= 20 ? $"GitHub's bill for the last 30 days; {g.Seats:#,0} seats" : $"{g.Seats:#,0} seats at list price", true));
            }
        }

        /* ---- Foundry and Azure OpenAI ---- */
        if (await ops.HasAsync("audit.FoundryCostDay"))
        {
            var accts = await db.QueryAsync<(string Account, decimal Cost, int Days, long Requests)>(@"
WITH last AS (SELECT MAX([Day]) AS D FROM audit.FoundryCostDay),
cost AS (SELECT Account, SUM(Cost) AS Cost, COUNT(DISTINCT [Day]) AS Days FROM audit.FoundryCostDay WHERE [Day] > DATEADD(DAY, -30, (SELECT D FROM last)) GROUP BY Account),
req AS (SELECT Account, SUM(Requests) AS Requests FROM audit.FoundryUsageDay WHERE [Day] > DATEADD(DAY, -30, (SELECT D FROM last)) GROUP BY Account)
SELECT cost.Account, cost.Cost, cost.Days, ISNULL(req.Requests, 0) FROM cost LEFT JOIN req ON req.Account = cost.Account;");
            if (accts.Count > 0)
            {
                sources.Add($"Foundry and Azure OpenAI: {accts.Count} accounts from Azure Cost Management and Azure Monitor");
                foreach (var a in accts)
                    if (CostRules.IdleAccount(a.Account, CostRules.Monthly(a.Cost, a.Days), a.Requests, 30, cur) is { } o) opps.Add(o);
                details.Add(new(CostAreas.Foundry, "Each account", ["Account", $"A month ({cur})", "Requests (30 days)", $"A thousand requests ({cur})"],
                    [.. accts.OrderByDescending(a => a.Cost).Select(a => new object?[] { a.Account, Math.Round(CostRules.Monthly(a.Cost, a.Days)), a.Requests,
                        a.Requests > 0 ? (object)Math.Round(CostRules.Monthly(a.Cost, a.Days) / a.Requests * 1000, 2) : "nothing answered" })],
                    Nums(1, 2, 3), "Azure Cost Management's charges and Azure Monitor's requests, the last 30 days."));
                areas.Add(new(CostAreas.Foundry, accts.Sum(a => CostRules.Monthly(a.Cost, a.Days)), opps.Where(x => x.Area == CostAreas.Foundry).Sum(x => x.Saving), null,
                    $"Azure's charges for {accts.Count} account{(accts.Count == 1 ? "" : "s")}", true));
            }
        }

        /* Areas that read nothing: named, so an empty one is not taken for no spend. */
        foreach (var (name, _) in CostAreas.All)
            if (!areas.Any(a => a.Name == name))
                missing.Add(name switch
                {
                    CostAreas.Fabric => "Fabric capacities (the Capacity Metrics app's export)",
                    CostAreas.Studio => "Copilot Credits (the admin center's consumption export)",
                    CostAreas.GitHub => "GitHub Copilot (tools\\Import-GitHubCopilotUsage.ps1)",
                    CostAreas.Foundry => "Foundry and Azure OpenAI (tools\\Import-FoundryUsage.ps1)",
                    _ => name,
                });

        /* What was saved already: Copilot licenses exported to revoke, or from disabled accounts, that have gone. */
        var realized = await ops.HasAsync("dash.LicenseAction")
            ? await db.ScalarAsync<int>(@"
SELECT COUNT(DISTINCT la.Upn) FROM dash.LicenseAction AS la JOIN bi.DimUser AS du ON du.UserPrincipalName = la.Upn
WHERE la.Kind IN ('revoke', 'leaver', 'nonhuman')
  AND NOT EXISTS (SELECT 1 FROM bi.FactLicensedDay AS ld WHERE ld.UserKey = du.UserKey AND ld.DateKey = (SELECT MAX(DateKey) FROM bi.FactLicensedDay));")
            : 0;

        /* ---- The suites: held, what they include, and the moves Suite fit finds, summed as Suite fit sums them ---- */
        SuiteSummary? suiteSummary = null;
        if (fit.Rows.Count > 0)
        {
            sources.Add($"Suites: {fit.Rows.Count:#,0} holders of {string.Join(", ", fit.Rows.Select(r => r.Holder.Suite.Name).Distinct().OrderBy(n => n, StringComparer.Ordinal))}, priced from Admin's suite list");
            suiteSummary = SuiteData.Summarize(fit, suiteCopilotValue, sparePercent);
        }
        var ordered = areas.OrderBy(a => CostAreas.Slot(a.Name)).ToList();
        return new(cur, ordered, CostRules.Ranked(opps), await MonthsAsync(copilotPrice, prices.Values.ToList(), c), cif, realized * copilotPrice, realized, sources, missing, hourly, suiteSummary, details);
    }

    static string Basis(List<Held> held, Dictionary<string, Price> prices, string area, string cur) =>
        string.Join("; ", held.Where(h => prices.TryGetValue(h.SkuPartNumber, out var p) && p.Area == area && Math.Max(h.Bought, h.Consumed) > 0)
            .Select(h => $"{Math.Max(h.Bought, h.Consumed):#,0} {prices[h.SkuPartNumber].Name} at {CostRules.Money(prices[h.SkuPartNumber].Seat.Each(Math.Max(h.Bought, h.Consumed)), cur)}"
                         + (prices[h.SkuPartNumber].Seat.HasTier && Math.Max(h.Bought, h.Consumed) >= prices[h.SkuPartNumber].TierFromSeats ? $" (the volume price from {prices[h.SkuPartNumber].TierFromSeats:#,0} seats)" : "")
                         + (h.Bought > h.Consumed ? $" ({h.Bought:#,0} bought, {h.Consumed:#,0} assigned)" : "")
                         + (h.Twice > 0 ? $", {h.Twice:#,0} of them on people whose suite includes it" : "")));

    /// <summary>
    /// The last six whole months' spend by area: Copilot from the licensed days, the other licenses from the pool's
    /// daily snapshots, and the rest from what each source billed. A month a source does not cover is left out of it.
    /// </summary>
    async Task<List<CostMonth>> MonthsAsync(decimal copilotPrice, List<Price> prices, ReportContext c)
    {
        var first = new DateTime(c.End.Year, c.End.Month, 1).AddMonths(-6);
        var end = new DateTime(c.End.Year, c.End.Month, 1);
        var rows = new List<(DateTime Month, string Area, decimal Spend)>();
        var k0 = ReportContext.Key(first); var k1 = ReportContext.Key(end.AddDays(-1));
        foreach (var r in await db.QueryAsync<(int Ym, int PersonDays)>(
                     /* The add-on's days only: someone whose Copilot comes inside a suite today is left out of every month, an approximation where they moved to it in the window. */
                     @"SELECT ld.DateKey / 100, COUNT(*) FROM bi.FactLicensedDay AS ld JOIN bi.DimUser AS du ON du.UserKey = ld.UserKey
                       WHERE ld.DateKey BETWEEN @k0 AND @k1 AND NOT EXISTS (SELECT 1 FROM dash.CopilotSuiteOnly AS cso WHERE cso.Upn = du.UserPrincipalName)
                       GROUP BY ld.DateKey / 100;", new { k0, k1 }))
        {
            var m = new DateTime(r.Ym / 100, r.Ym % 100, 1);
            rows.Add((m, CostAreas.Copilot, r.PersonDays * copilotPrice / DateTime.DaysInMonth(m.Year, m.Month)));
        }
        async Task Add(string table, string sql, object? args = null)
        {
            if (!await ops.HasAsync(table)) return;
            rows.AddRange((await db.QueryAsync<(DateTime Month, string Area, decimal Spend)>(sql, args)).Select(x => (x.Month, x.Area, x.Spend)));
        }
        var priced = prices.Where(p => p.Area is "Power Automate" or "Power Apps" or "Power BI" or "Copilot Studio")
            .Select(p => new { p.Item, Area = p.Area switch { "Power Automate" => CostAreas.Automate, "Power Apps" => CostAreas.Apps, "Power BI" => CostAreas.PowerBi, _ => CostAreas.Studio }, p.MonthlyPrice, p.TierFromSeats, p.TierPrice }).ToList();
        if (priced.Count > 0)
            await Add("audit.LicenseSkuDay", @"
SELECT DATEFROMPARTS(YEAR(d.SnapshotDate), MONTH(d.SnapshotDate), 1), p.Area,
       AVG(CAST(d.Consumed AS DECIMAL(19, 4))) * CASE WHEN AVG(CAST(d.Consumed AS DECIMAL(19, 4))) >= MAX(p.TierFrom) THEN MAX(p.TierPrice) ELSE MAX(p.Price) END
FROM audit.LicenseSkuDay AS d JOIN OPENJSON(@json) WITH (Item NVARCHAR(100), Area NVARCHAR(60), Price DECIMAL(19, 4), TierFrom INT, TierPrice DECIMAL(19, 4)) AS p ON p.Item = d.SkuPartNumber
WHERE d.SnapshotDate >= @first AND d.SnapshotDate < @end
GROUP BY DATEFROMPARTS(YEAR(d.SnapshotDate), MONTH(d.SnapshotDate), 1), p.Area, d.SkuPartNumber;",
                new { first, end, json = System.Text.Json.JsonSerializer.Serialize(priced.Select(p => new { p.Item, p.Area, Price = p.MonthlyPrice, TierFrom = p.TierFromSeats, TierPrice = p.TierPrice ?? p.MonthlyPrice })) });
        await Add("audit.FabricCapacityDay", $"SELECT DATEFROMPARTS(YEAR([Day]), MONTH([Day]), 1), N'{CostAreas.Fabric}', SUM(Cost) FROM audit.FabricCapacityDay WHERE [Day] >= @first AND [Day] < @end GROUP BY DATEFROMPARTS(YEAR([Day]), MONTH([Day]), 1);", new { first, end });
        await Add("audit.FoundryCostDay", $"SELECT DATEFROMPARTS(YEAR([Day]), MONTH([Day]), 1), N'{CostAreas.Foundry}', SUM(Cost) FROM audit.FoundryCostDay WHERE [Day] >= @first AND [Day] < @end GROUP BY DATEFROMPARTS(YEAR([Day]), MONTH([Day]), 1);", new { first, end });
        await Add("audit.GitHubCopilotCost", $"SELECT DATEFROMPARTS(YEAR(UsageDate), MONTH(UsageDate), 1), N'{CostAreas.GitHub}', SUM(NetAmount) FROM audit.GitHubCopilotCost WHERE UsageDate >= @first AND UsageDate < @end GROUP BY DATEFROMPARTS(YEAR(UsageDate), MONTH(UsageDate), 1);", new { first, end });
        await Add("audit.PowerPlatformCreditDay", $"SELECT DATEFROMPARTS(YEAR([Day]), MONTH([Day]), 1), N'{CostAreas.Studio}', SUM(Cost) FROM audit.PowerPlatformCreditDay WHERE Billing = 'PayAsYouGo' AND [Day] >= @first AND [Day] < @end GROUP BY DATEFROMPARTS(YEAR([Day]), MONTH([Day]), 1);", new { first, end });
        var dv = prices.Where(p => p.Item.StartsWith("DATAVERSE_", StringComparison.OrdinalIgnoreCase)).Select(p => new { Type = p.Item.Split('_')[1], Price = p.MonthlyPrice }).ToList();
        if (dv.Count > 0)
            await Add("audit.PowerPlatformCapacityDay", $@"
SELECT DATEFROMPARTS(YEAR(d.[Day]), MONTH(d.[Day]), 1), N'{CostAreas.Apps}', AVG(CASE WHEN d.UsedGb > d.EntitledGb THEN d.UsedGb - d.EntitledGb ELSE 0 END) * MAX(p.Price)
FROM audit.PowerPlatformCapacityDay AS d JOIN OPENJSON(@json) WITH (Type NVARCHAR(16), Price DECIMAL(19, 4)) AS p ON p.Type = UPPER(d.CapacityType)
WHERE d.[Day] >= @first AND d.[Day] < @end
GROUP BY DATEFROMPARTS(YEAR(d.[Day]), MONTH(d.[Day]), 1), d.CapacityType;", new { first, end, json = System.Text.Json.JsonSerializer.Serialize(dv) });
        return [.. Enumerable.Range(0, 6).Select(i => first.AddMonths(i)).Select(m => new CostMonth(m,
            rows.Where(r => r.Month == m).GroupBy(r => r.Area).ToDictionary(g => g.Key, g => Math.Round(g.Sum(x => x.Spend), 2))))];
    }
}
