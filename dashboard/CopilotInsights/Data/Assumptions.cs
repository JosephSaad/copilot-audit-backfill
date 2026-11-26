using System.Globalization;
using CopilotInsights.Infrastructure;

namespace CopilotInsights.Data;

/// <summary>Where an assumption is kept: a setting, a row of the price list or the suites, the reservation rate, or the minutes saved by app.</summary>
public enum AssumptionKind { Setting, Price, Suite, Rate, Minutes }

/// <summary>
/// One input a figure is worked out from: what it is, where an administrator changes it (the page and the field,
/// which <see cref="Assumptions.EditHref"/> opens at), what the installer set, and the figures that use it.
/// </summary>
public sealed record AssumptionDef(string Key, string Label, string Group, string Page, string Field, string UsedBy,
    string? Default = null, AssumptionKind Kind = AssumptionKind.Setting, string? Unit = null);

/// <summary>An assumption with its value now, who last changed it and when, and whether it is still what the installer set.</summary>
public sealed record AssumptionValue(AssumptionDef Def, string? Value, string? ChangedBy, DateTime? ChangedUtc, bool IsDefault)
{
    /// <summary>The value as a reader sees it: with its unit, or "not set".</summary>
    public string Shown => string.IsNullOrWhiteSpace(Value) ? "not set" : Def.Unit is { } u ? $"{Value} {u}" : Value;
}

/// <summary>
/// The assumptions register: every input a cost, saving, value or estimate is worked out from, in one list, with
/// the page an administrator changes it on. The inputs stay where they were (Admin › Access, Admin › Targets and
/// assumptions, Leadership › Copilot reservations); this is where they are found, and what a figure's "how it is worked
/// out" names. The price list's and the suites' rows are added from the database, one assumption each.
/// </summary>
public static class Assumptions
{
    public const string Access = "/admin/access", Targets = "/admin/assumptions", Reservations = "/leadership/reservations",
        CreditChargeback = "/leadership/credit-chargeback", CreditCommitment = "/forecasting/credit-commitment";

    public static readonly IReadOnlyList<AssumptionDef> Settings =
    [
        new("LicenseMonthlyCost", "Microsoft 365 Copilot license, a month", "Prices", Access, "LicenseMonthlyCost",
            "Costs and savings, Chargeback, Upgrade train or revoke, Better on pay-as-you-go, Swap list, Fading use, License optimization, Forecast, Board pack", "30"),
        new("LicenseCurrency", "Currency", "Prices", Access, "LicenseCurrency", "Every amount the dashboard shows", "USD"),
        new("PaygInteractionCost", "Pay-as-you-go cost of an interaction", "Prices", Access, "PaygInteractionCost", "Better on pay-as-you-go, Costs and savings", "0.12"),
        new("ReservationRate", "Copilot reservation rate in force, a license a month", "Prices", Reservations, "rate",
            "Copilot reservations (monthly and remaining obligation), month-end snapshots", null, AssumptionKind.Rate),
        new("InactiveDays", "Unused after", "Use and thresholds", Access, "InactiveDays", "Unused licenses, Use ranking, the license recommendations", "28", Unit: "days"),
        new("SpareSeatPercent", "Spare seats kept unassigned, of those bought", "Use and thresholds", Targets, "SpareSeatPercent", "Costs and savings (unassigned seats: Copilot and each license without its own share)", "10", Unit: "%"),
        new("CostIdleDays", "Idle after (seats, Power Platform licenses, AI accounts)", "Use and thresholds", Targets, "CostIdleDays", "Costs and savings, Suite fit", "30", Unit: "days"),
        new("MinGroupSize", "Minimum group size", "Use and thresholds", Access, "MinGroupSize", "Every breakdown: smaller groups are folded together", "10", Unit: "people"),
        new("MinutesSavedDefault", "Minutes one interaction saves (apps without their own figure)", "Value", Targets, "MinutesSavedDefault",
            "Value and time saved, Costs and savings (Copilot's value), Board pack", "6", Unit: "minutes"),
        new("AppMinutes", "Minutes saved, set for each app", "Value", Targets, "minutes", "Value and time saved, Costs and savings", null, AssumptionKind.Minutes),
        new("HourlyCost", "What an hour is worth", "Value", Targets, "HourlyCost", "Value and time saved, Costs and savings (every value)", "72"),
        new("FlowMinutesPerRun", "Minutes one flow run saves (default)", "Value", Targets, "FlowMinutesPerRun", "Costs and savings (Power Automate's value)", "3", Unit: "minutes"),
        new("PowerBiCopilotMinutes", "Minutes one Copilot in Power BI request saves", "Value", Targets, "PowerBiCopilotMinutes", "Costs and savings (Copilot in Fabric's value)", "3", Unit: "minutes"),
        new("FabricReservedDiscount", "Fabric reservation discount", "Prices", Targets, "FabricReservedDiscount", "Costs and savings (reserve a capacity)", "0.41"),
        new("CreditsPerAnswer", "Credits a billed agent answer costs", "Credits and tokens", Targets, "CreditsPerAnswer", "Credit use, Agents costing credits, Better with a license", "2", Unit: "credits"),
        new("CreditsPerGrounding", "Credits grounding in work data adds", "Credits and tokens", Targets, "CreditsPerGrounding", "Credit use, Agents costing credits, Better with a license", "10", Unit: "credits"),
        new("CreditsPerAction", "Credits an agent action adds", "Credits and tokens", Targets, "CreditsPerAction", "Credit use, Agents costing credits", "5", Unit: "credits"),
        new("TokensPerTurn", "Tokens a turn takes", "Credits and tokens", Targets, "TokensPerTurn", "Credit use (tokens)", "1500", Unit: "tokens"),
        new("TokensPerResource", "Tokens each resource read adds", "Credits and tokens", Targets, "TokensPerResource", "Credit use (tokens)", "1000", Unit: "tokens"),
        new("TokensPerAnswer", "Tokens an answer gives back", "Credits and tokens", Targets, "TokensPerAnswer", "Credit use (tokens)", "400", Unit: "tokens"),
        new("ReservationGatePercent", "Purchase gate: share to assign first", "Reservations", Reservations, "percent", "Copilot reservations (purchase gate)", "95", Unit: "%"),
        new("ReservationGateBaseline", "Purchase gate: unassigned licenses at the baseline", "Reservations", Reservations, "baseline", "Copilot reservations (purchase gate)", null),
        /* 2.7.2 */
        new("CreditChargeRate", "Copilot Credit charge rate, a credit", "Prices", CreditChargeback, "rate",
            "Credit chargeback (each line of business's charge), its month-end snapshots. Empty: the price list's pay-as-you-go credit", null),
        /* 3.5.0 */
        new("Agent365MonthlyPrice", "Agent 365, a user a month", "Prices", Targets, "Agent365MonthlyPrice", "Agent 365 sizing", "15"),
        new("CreditCommitmentAlertDays", "Credit commitment warning", "Use and thresholds", CreditCommitment, "alertDays",
            "Credit commitment (runs out early, or left unused), the weekly alert", "30", Unit: "days"),
    ];

    /// <summary>Where to change it: the page, opened at the field (site.js finds it from ?focus=).</summary>
    public static string EditHref(AssumptionDef d) => d.Page + "?focus=" + Uri.EscapeDataString(d.Field);

    public static AssumptionDef? Find(string key) => Settings.FirstOrDefault(d => d.Key == key);

    /// <summary>A price list row as an assumption: found on Targets and assumptions by its item (the form's hidden key).</summary>
    public static AssumptionDef Price(string item, string name, string area, string unit) =>
        new("Price:" + item, $"{name}, a {unit}", "Prices", Targets, "price|" + item, $"Costs and savings ({area})", null, AssumptionKind.Price);

    /// <summary>
    /// A suite's price as an assumption. A suite is sold under several SKUs, each priced on its own (E5 without audio
    /// conferencing, the no-Teams editions), so the label names the SKU: by the suite alone, E5 read as one row three times.
    /// </summary>
    public static AssumptionDef Suite(string pattern, string suite) =>
        new("Suite:" + pattern, $"{suite} ({SkuShown(pattern)}), a user a month", "Suites", Targets, "price|" + pattern, "Suite fit, Costs and savings (suites)", null, AssumptionKind.Suite);

    /// <summary>A SKU pattern as people read it: SPE[_]E7% is SPE_E7*.</summary>
    public static string SkuShown(string pattern) => pattern.Replace("[_]", "_").Replace("%", "*");

    /// <summary>
    /// What disagrees: the Copilot license's price as Access sets it against the reservation rate in force and
    /// the contracts' price on Budget and renewal. Each line says both values and where each is set. Pure, for the tests.
    /// </summary>
    public static IReadOnlyList<string> Conflicts(decimal? licensePrice, decimal? rate, DateTime? rateFrom, decimal? contractPrice, string cur)
    {
        var lines = new List<string>();
        static string M(decimal v) => v.ToString("#,0.##", CultureInfo.InvariantCulture);
        if (licensePrice is { } lp && rate is { } r && r != lp)
            lines.Add($"The Microsoft 365 Copilot price is {M(lp)} {cur} a month in Admin › Access, which Costs and savings, Chargeback and the license recommendations use, " +
                      $"and the reservation rate in force{(rateFrom is { } f ? $" since {f:d MMM yyyy}" : "")} is {M(r)} {cur} on Leadership › Copilot reservations. If {M(r)} is what is paid, set it in Access too.");
        if (licensePrice is { } lp2 && contractPrice is { } cp && cp != lp2)
            lines.Add($"The Copilot contracts on Forecasting › Budget and renewal average {M(cp)} {cur} a seat a month, against {M(lp2)} {cur} in Admin › Access.");
        return lines;
    }
}

/// <summary>Reads the register's values: the settings with who changed them, the price list and suites, the rate in force.</summary>
public sealed class AssumptionData(Db db, OpsData ops)
{
    sealed class Row { public string Name { get; set; } = ""; public string? Value { get; set; } public string? UpdatedBy { get; set; } public DateTime? UpdatedUtc { get; set; } }

    public async Task<IReadOnlyList<AssumptionValue>> AllAsync()
    {
        var list = new List<AssumptionValue>();
        var set = (await db.QueryAsync<Row>("SELECT Name, Value, UpdatedBy, UpdatedUtc FROM dash.Setting;")).ToDictionary(r => r.Name, StringComparer.OrdinalIgnoreCase);
        var rate = await RateAsync();
        var apps = await ops.HasAsync("dash.TimeSaving")
            ? (await db.QueryAsync<(int N, string? By, DateTime? At)>("SELECT COUNT(*), MAX(UpdatedBy), MAX(UpdatedUtc) FROM dash.TimeSaving;")).Single() : (0, null, null);
        foreach (var d in Assumptions.Settings)
        {
            switch (d.Kind)
            {
                case AssumptionKind.Rate:
                    list.Add(new(d, rate?.Rate.ToString("0.####", CultureInfo.InvariantCulture), rate?.By, rate?.At, false));
                    break;
                case AssumptionKind.Minutes:
                    list.Add(new(d, apps.N == 0 ? "none: every app uses the default" : $"{apps.N:#,0} app{(apps.N == 1 ? "" : "s")} with their own figure", apps.By, apps.At, apps.N == 0));
                    break;
                default:
                    var r = set.GetValueOrDefault(d.Key);
                    /* Never changed: the installer's row, which names no one. */
                    list.Add(new(d, r?.Value, r?.UpdatedBy, r?.UpdatedUtc, r is null || r.UpdatedBy is null));
                    break;
            }
        }
        if (await ops.HasAsync("dash.CostPrice"))
            foreach (var p in await db.QueryAsync<(string Item, string Area, string Name, string Unit, decimal Price, string? By, DateTime At)>(
                         "SELECT Item, Area, Name, Unit, MonthlyPrice, UpdatedBy, UpdatedUtc FROM dash.CostPrice ORDER BY Area, Name;"))
                list.Add(new(Assumptions.Price(p.Item, p.Name, p.Area, p.Unit), p.Price.ToString("0.####", CultureInfo.InvariantCulture), p.By == "install" ? null : p.By, p.By == "install" ? null : p.At, p.By is null or "install"));
        if (await ops.HasAsync("dash.CostSuite"))
            foreach (var s in await db.QueryAsync<(string Pattern, string Suite, decimal Price, bool Enabled, string? By, DateTime At)>(
                         "SELECT SkuPattern, Suite, MonthlyPrice, Enabled, UpdatedBy, UpdatedUtc FROM dash.CostSuite ORDER BY Family, Tier, SkuPattern;"))
                list.Add(new(Assumptions.Suite(s.Pattern, s.Suite), s.Price.ToString("0.##", CultureInfo.InvariantCulture) + (s.Enabled ? "" : " (off)"), s.By == "install" ? null : s.By, s.By == "install" ? null : s.At, s.By is null or "install"));
        return list;
    }

    /// <summary>The reservation rate in force today, and who set it, or null (2.6.8's table not installed, or no rate).</summary>
    public async Task<(decimal Rate, DateTime From, string? By, DateTime? At)?> RateAsync()
    {
        if (!await ops.HasAsync("dash.ReservationRate")) return null;
        var r = await db.QueryAsync<(decimal Rate, DateTime From, string? By, DateTime? At)>(
            "SELECT TOP (1) MonthlyRate, EffectiveFrom, UpdatedBy, UpdatedUtc FROM dash.ReservationRate WHERE EffectiveFrom <= CAST(GETDATE() AS DATE) ORDER BY EffectiveFrom DESC;");
        return r.Count == 0 ? null : r[0];
    }

    /// <summary>The register's conflicts now (see <see cref="Assumptions.Conflicts"/>).</summary>
    public async Task<IReadOnlyList<string>> ConflictsAsync(IReadOnlyList<AssumptionValue> values)
    {
        decimal? Dec(string key) => decimal.TryParse(values.FirstOrDefault(v => v.Def.Key == key)?.Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? d : null;
        var rate = await RateAsync();
        decimal? contract = null;
        if (await ops.HasAsync("dash.LicenseContract"))
            contract = (await db.QueryAsync<decimal?>("SELECT SUM(Seats * MonthlyPrice) / NULLIF(SUM(Seats), 0) FROM dash.LicenseContract WHERE RenewalDate IS NULL OR RenewalDate >= CAST(GETDATE() AS DATE);")).SingleOrDefault() is { } c ? Math.Round(c, 2) : null;
        return Assumptions.Conflicts(Dec("LicenseMonthlyCost"), rate?.Rate, rate?.From, contract, values.FirstOrDefault(v => v.Def.Key == "LicenseCurrency")?.Value ?? "USD");
    }
}
