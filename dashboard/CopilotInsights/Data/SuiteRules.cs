namespace CopilotInsights.Data;

/// <summary>
/// A suite as dash.CostSuite describes it: its family (Microsoft 365, Office 365), its tier (3, 5, 7), its price
/// a month, and the priced items it includes (COPILOT for Microsoft 365 Copilot, POWER_BI_PRO). The next suite down is
/// the same family's next lower tier with a price.
/// </summary>
public sealed record Suite(string Pattern, string Name, string Family, int Tier, decimal Price, IReadOnlySet<string> Includes, string? Covers)
{
    public bool HasCopilot => Includes.Contains(SuiteRules.Copilot);
    public bool HasPowerBi => Includes.Contains(SuiteRules.PowerBiPro);
}

/// <summary>
/// One person on a suite, and what the data says of them: days in the last 30 with use that needs the Copilot license;
/// whether they also hold the Copilot add-on, a standalone Power BI Pro, or other suites besides their highest; whether
/// they used Power BI in the window (null where the activity was not loaded); protected by level, account enabled, and
/// licensed for Copilot since when.
/// </summary>
public sealed record SuiteHolder(string Upn, Suite Suite, int CopilotDays, bool CopilotAddOn, bool PowerBiAddOn, IReadOnlyList<Suite> Others,
    bool? UsesPowerBi, bool Protected, bool Enabled, DateTime? CopilotSince);

/// <summary>What fits a suite holder better, if anything; CutSeats is a suite's seats bought and assigned to nobody (not a person's).</summary>
public enum SuiteMove { Keep, Reclaim, DropDuplicate, Train, Downgrade, DowngradeWithCopilot, DowngradeWithPowerBi, CutSeats }

/// <summary>
/// The move, the suite or license it leads to, what it saves a month (or, to train, the spend at stake), how sure the
/// data makes it, why, and what the data cannot see and someone must check first.
/// </summary>
public sealed record SuiteFit(SuiteMove Move, string? To, decimal Saving, decimal AtStake, string Confidence, string Why, string Check);

/// <summary>
/// Which suite fits each holder. Suites are not one product to keep or revoke: E7 is E5 with Microsoft 365
/// Copilot, Agent 365 and the Entra Suite; E5 is E3 with Power BI Pro, the advanced security and compliance (Defender
/// P2, Purview, Entra ID P2) and Teams Phone. The data shows Copilot and Power BI use, and nothing of the rest, so a
/// move down rests on the parts it can see and always names the parts it cannot. In order:
/// <list type="number">
/// <item>An account disabled or gone: its suites can be taken back (Medium: a mailbox kept on hold may still need one).
///   Everyone, as on Licenses on disabled accounts: nobody can use them, so this is not a recommendation about a person.</item>
/// <item>A protected level: keep, copies included, as on every other list (Admin › Access).</item>
/// <item>A license held twice (the Copilot add-on on E7, Power BI Pro on E5 or E7, any other suite at or below the
///   highest, Office 365 under Microsoft 365 too): drop the copies (High).</item>
/// <item>A Copilot license under 30 days old: keep.</item>
/// <item>E7: no Copilot use, move to E5 (Medium); light use (1 or 2 days), train first (Grow); regular use, keep, or E5
///   and the Copilot add-on where that costs less and Agent 365 and the Entra Suite go unused (Low).</item>
/// <item>E5: Power BI unused, E3 (Low); Power BI used, E3 and Power BI Pro where that costs less (Low); both only after
///   security and compliance agree. Power BI activity not loaded: keep, and say so.</item>
/// </list>
/// </summary>
public static class SuiteRules
{
    public const string Copilot = "COPILOT";
    public const string PowerBiPro = "POWER_BI_PRO";
    public const int NewLicenseDays = 30;

    const string CheckE7 = "Agent 365 and the Entra Suite come with E7 and their use is not in this data: check nobody relies on them.";
    const string CheckE5 = "E5's Defender for Office 365 and Endpoint P2, Purview, Entra ID P2, Teams Phone and audio conferencing are not in this data: agree the move with security, compliance and telephony first.";

    /// <summary>The same family's next suite down that has a price, or none.</summary>
    public static Suite? Lower(Suite s, IEnumerable<Suite> all) =>
        all.Where(x => x.Family == s.Family && x.Tier < s.Tier && x.Price > 0).OrderByDescending(x => x.Tier).FirstOrDefault();

    public static SuiteFit Fit(SuiteHolder h, Suite? lower, decimal copilotPrice, decimal powerBiPrice, DateTime today, string cur)
    {
        var s = h.Suite;
        if (!h.Enabled)
            return new(SuiteMove.Reclaim, null, s.Price + h.Others.Sum(o => o.Price), 0, "Medium",
                $"The account is disabled or gone, and still holds {string.Join(" and ", new[] { s.Name }.Concat(h.Others.Select(o => o.Name)))}.",
                "A mailbox kept on hold for retention may still need its license; an inactive mailbox does not.");
        if (h.Protected) return new(SuiteMove.Keep, null, 0, 0, "", "A protected level of the organization.", "");

        /* Paid twice: certain, whatever else is true of them. */
        var dup = 0m; var what = new List<string>();
        if (h.CopilotAddOn && s.HasCopilot) { dup += copilotPrice; what.Add("the Microsoft 365 Copilot add-on"); }
        if (h.PowerBiAddOn && s.HasPowerBi) { dup += powerBiPrice; what.Add("a standalone Power BI Pro"); }
        foreach (var two in h.Others.Where(o => o.Tier <= s.Tier)) { dup += two.Price; what.Add(two.Name); }
        if (dup > 0)
            return new(SuiteMove.DropDuplicate, null, dup, 0, "High", $"Holds {string.Join(" and ", what)} as well as {s.Name}, which already includes {(what.Count == 1 ? "it" : "them")}.",
                "Nothing: the suite covers it. Remove the extra license.");

        if (lower is null || lower.Price >= s.Price) return new(SuiteMove.Keep, null, 0, 0, "", $"No suite below {s.Name} with a lower price is set.", "");
        var step = s.Price - lower.Price;

        if (s.HasCopilot)
        {
            if (h.CopilotSince is { } since && (today - since).TotalDays < NewLicenseDays)
                return new(SuiteMove.Keep, null, 0, 0, "", $"Licensed for Copilot under {NewLicenseDays} days ago.", "");
            if (h.CopilotDays == 0)
                return new(SuiteMove.Downgrade, lower.Name, step, 0, "Medium",
                    $"No use that needs the Copilot license in the last 30 days: {lower.Name} costs {CostRules.Money(step, cur)} less a month.", CheckE7);
            if (h.CopilotDays <= Pages.Forecasting.ActionsModel.LightMax)
                return new(SuiteMove.Train, null, 0, step, "Medium",
                    $"Copilot's licensed features on {h.CopilotDays} day{(h.CopilotDays == 1 ? "" : "s")} in 30: train before deciding; {CostRules.Money(step, cur)} a month of {s.Name} over {lower.Name} rides on it.", CheckE7);
            var withCopilot = lower.Price + copilotPrice;
            if (withCopilot < s.Price)
                return new(SuiteMove.DowngradeWithCopilot, $"{lower.Name} and Microsoft 365 Copilot", s.Price - withCopilot, 0, "Low",
                    $"Uses Copilot on {h.CopilotDays} days in 30; {lower.Name} with the Copilot add-on costs {CostRules.Money(s.Price - withCopilot, cur)} less a month than {s.Name}.", CheckE7);
            return new(SuiteMove.Keep, null, 0, 0, "", $"Uses Copilot on {h.CopilotDays} days in 30.", "");
        }

        if (s.HasPowerBi)
        {
            if (h.UsesPowerBi is null) return new(SuiteMove.Keep, null, 0, 0, "", "Power BI activity is not loaded, so whether E5's Power BI Pro is used is not known.", "");
            if (h.UsesPowerBi == false)
                return new(SuiteMove.Downgrade, lower.Name, step, 0, "Low",
                    $"No Power BI use in the last 30 days: {lower.Name} costs {CostRules.Money(step, cur)} less a month.", CheckE5);
            var withBi = lower.Price + powerBiPrice;
            if (withBi < s.Price)
                return new(SuiteMove.DowngradeWithPowerBi, $"{lower.Name} and Power BI Pro", s.Price - withBi, 0, "Low",
                    $"Uses Power BI; {lower.Name} with Power BI Pro costs {CostRules.Money(s.Price - withBi, cur)} less a month than {s.Name}.", CheckE5);
            return new(SuiteMove.Keep, null, 0, 0, "", "Uses Power BI.", "");
        }
        return new(SuiteMove.Keep, null, 0, 0, "", $"{s.Name} is the lowest suite with a price set.", "");
    }

    /// <summary>How a move reads, for a list's column and a page's filter.</summary>
    public static string Label(SuiteMove m) => m switch
    {
        SuiteMove.Reclaim => "Take back", SuiteMove.DropDuplicate => "Remove the copy", SuiteMove.Train => "Train first",
        SuiteMove.Downgrade => "Move down", SuiteMove.DowngradeWithCopilot => "Move down, add Copilot",
        SuiteMove.DowngradeWithPowerBi => "Move down, add Power BI Pro", SuiteMove.CutSeats => "Cut unassigned seats", _ => "Keep",
    };
}
