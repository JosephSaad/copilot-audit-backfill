namespace CopilotInsights.Data;

/// <summary>An area of AI and automation spend on Leadership › Costs and savings (2.6.6), in the order the page shows them.</summary>
public static class CostAreas
{
    public const string Copilot = "Microsoft 365 Copilot", Studio = "Copilot Studio and AI Builder", Fabric = "Fabric and Copilot in Power BI",
        PowerBi = "Power BI licenses", Automate = "Power Automate", Apps = "Power Apps and Dataverse", GitHub = "GitHub Copilot", Foundry = "Foundry and Azure OpenAI";

    /// <summary>Every area, each with its chart color: the slot stays the area's whatever the order of the figures.</summary>
    public static readonly IReadOnlyList<(string Name, int Slot)> All =
        [(Copilot, 1), (Studio, 2), (Fabric, 3), (PowerBi, 4), (Automate, 5), (Apps, 6), (GitHub, 7), (Foundry, 8)];

    public static int Slot(string area) => All.FirstOrDefault(a => a.Name == area).Slot is var s and > 0 ? s : 8;
}

/// <summary>What one area costs a month, what can be saved in it, and the value it is estimated to return.</summary>
public sealed record CostArea(string Name, decimal Spend, decimal Saving, decimal? Value, string Basis, bool Loaded, string? Href = null);

/// <summary>Save: money to take back. Grow: value the spend could return and does not yet. Risk: spend not delivering what it should.</summary>
public enum OpportunityKind { Save, Grow, Risk }

/// <summary>
/// One thing to do, with what it is worth a month. Saving is money that stops being spent; AtStake, for a Grow or a
/// Risk, the monthly value or spend the issue holds back. Confidence: High where the data shows it outright (a seat
/// nobody has used in 30 days), Medium where a change follows from it (a smaller capacity), Low where it rests on an
/// assumption (a reservation's discount, minutes saved).
/// </summary>
public sealed record Opportunity(string Area, OpportunityKind Kind, string Title, string Evidence, string Action,
    decimal Saving, decimal AtStake, string Confidence, string? Href = null);

/// <summary>One Fabric capacity over the last 30 days it has data for, summarized from its days (see <see cref="CostRules.Summarize"/>).</summary>
public sealed record FabricCapacity(string Name, string? Sku, int CapacityUnits, string? Billing, int Days, decimal Cost,
    double AvgPercent, double PeakPercent, double WeekdayPercent, double WeekendPercent, double ActiveHours, int ThrottledMinutes,
    double CopilotShare = 0, long CopilotRequests = 0, int CopilotUsers = 0);

/// <summary>One day of a Fabric capacity, as audit.FabricCapacityDay keeps it.</summary>
public sealed record FabricDay(string CapacityId, DateTime Day, string? Name, string? Sku, int? CapacityUnits, string? Billing,
    decimal? ActiveHours, decimal? CuSecondsUsed, decimal? PeakPercent, int? ThrottledMinutes, decimal? Cost);

/// <summary>
/// The rules that turn what the sources say into savings and opportunities (2.6.6). Pure, for the tests: the page
/// reads the facts with <see cref="CostData"/> and hands them here.
/// </summary>
public static class CostRules
{
    /// <summary>A month, for a figure over a number of days: what those days come to over 30.</summary>
    public static decimal Monthly(decimal total, int days) => days <= 0 ? 0 : Math.Round(total * 30m / days, 2);

    /// <summary>
    /// A capacity's last 30 days, summarized: its cost over them, its average utilization over the hours it ran, the
    /// 95th percentile of its daily peaks (one bad day does not size a capacity), and weekdays against weekends.
    /// </summary>
    public static FabricCapacity Summarize(IReadOnlyList<FabricDay> days)
    {
        var last = days.OrderBy(d => d.Day).ToList();
        var first = last[^1];
        var cu = last.Select(d => d.CapacityUnits ?? 0).DefaultIfEmpty(0).Max();
        double Util(FabricDay d) => cu <= 0 || (d.ActiveHours ?? 24) <= 0 ? 0 : (double)(d.CuSecondsUsed ?? 0) / (cu * 3600.0 * (double)(d.ActiveHours ?? 24)) * 100;
        static bool Weekend(DateTime d) => d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
        var peaks = last.Select(d => (double)(d.PeakPercent ?? 0)).OrderBy(p => p).ToList();
        var p95 = peaks.Count == 0 ? 0 : peaks[(int)Math.Ceiling(peaks.Count * 0.95) - 1];
        double Avg(IEnumerable<FabricDay> s) => s.Select(Util).DefaultIfEmpty(0).Average();
        return new(first.Name ?? first.CapacityId, last.Select(d => d.Sku).LastOrDefault(s => s is not null), cu, last.Select(d => d.Billing).LastOrDefault(b => b is not null),
            last.Count, last.Sum(d => d.Cost ?? 0), Avg(last), p95, Avg(last.Where(d => !Weekend(d.Day))), Avg(last.Where(d => Weekend(d.Day))),
            last.Average(d => (double)(d.ActiveHours ?? 24)), last.Sum(d => d.ThrottledMinutes ?? 0));
    }

    /// <summary>
    /// What a capacity could save, at most one move each, the largest first:
    /// <list type="bullet">
    /// <item>idle (under 5% on average and peaks under 15%): pause it, or delete it; on a reservation, at renewal;</item>
    /// <item>oversized (peaks under 45%, on any SKU above F2): one size down halves the cost;</item>
    /// <item>pay-as-you-go running all day: a one-year reservation (<paramref name="reservedDiscount"/>), or pausing at
    /// weekends where they carry a quarter of a weekday's work or less, whichever saves more after the resize;</item>
    /// <item>throttled (an hour or more in the 30 days): a risk, not a saving, worth the time people waited.</item>
    /// </list>
    /// </summary>
    public static IEnumerable<Opportunity> Fabric(FabricCapacity c, decimal reservedDiscount, string cur)
    {
        var monthly = Monthly(c.Cost, c.Days);
        var reserved = string.Equals(c.Billing, "Reserved", StringComparison.OrdinalIgnoreCase);
        var name = $"{c.Name}{(c.Sku is null ? "" : $" ({c.Sku})")}";
        if (c.AvgPercent < 5 && c.PeakPercent < 15)
        {
            yield return new(CostAreas.Fabric, OpportunityKind.Save, $"Pause or remove the idle capacity {name}",
                $"{c.AvgPercent:0.#}% used on average and never over {c.PeakPercent:0}% in {c.Days} days, for {Money(monthly, cur)} a month.",
                reserved ? "Move its few items to another capacity and let the reservation lapse at renewal." : "Move its few items to another capacity, then pause it, or delete it.",
                reserved ? 0 : monthly, reserved ? monthly : 0, "High");
            yield break;
        }
        var left = monthly;
        var resized = false;
        if (c.PeakPercent < 45 && c.CapacityUnits > 2)
        {
            var save = Math.Round(monthly / 2, 2);
            left -= save; resized = true;
            yield return new(CostAreas.Fabric, OpportunityKind.Save, $"Size {name} down to F{c.CapacityUnits / 2}",
                $"Its busiest days peak at {c.PeakPercent:0}% of the capacity (95th percentile, {c.Days} days); half the size still leaves them under {c.PeakPercent * 2:0}%.",
                $"Change the SKU to F{c.CapacityUnits / 2} in the Azure portal; watch the Capacity Metrics app for a week.", reserved ? 0 : save, reserved ? save : 0, "Medium");
        }
        if (!reserved && c.ActiveHours >= 20)
        {
            var reserve = Math.Round(left * reservedDiscount, 2);
            var weekendLow = c.WeekdayPercent > 0 && c.WeekendPercent <= c.WeekdayPercent / 4;
            var pause = weekendLow ? Math.Round(left * 2m / 7m, 2) : 0;
            if (pause > reserve)
                yield return new(CostAreas.Fabric, OpportunityKind.Save, resized ? $"Pause {c.Name} at weekends, once at F{c.CapacityUnits / 2}" : $"Pause {name} at weekends",
                    $"Weekends use {c.WeekendPercent:0.#}% of it against {c.WeekdayPercent:0.#}% on weekdays, and it runs around the clock on pay-as-you-go.",
                    "Schedule a pause on Friday night and a resume on Monday morning (an Azure Automation runbook or Logic App).", pause, 0, "Medium");
            else if (reserve > 0)
                yield return new(CostAreas.Fabric, OpportunityKind.Save, resized ? $"Reserve {c.Name} for a year, once at F{c.CapacityUnits / 2}" : $"Reserve {name} for a year",
                    $"It runs {c.ActiveHours:0} hours a day on pay-as-you-go; a one-year reservation takes about {reservedDiscount:P0} off." +
                    (weekendLow ? $" Pausing at weekends instead would save {Money(pause, cur)}." : ""),
                    "Buy a Fabric capacity reservation of the same size (after any resize) in the Azure portal.", reserve, 0, "Low");
        }
        if (c.ThrottledMinutes >= 60)
            yield return new(CostAreas.Fabric, OpportunityKind.Risk, $"{name} was throttled for {c.ThrottledMinutes:#,0} minutes",
                $"Interactive work was delayed or refused on its busiest days (peaks to {c.PeakPercent:0}%)" +
                (c.CopilotShare >= 0.05 ? $"; Copilot in Power BI uses {c.CopilotShare:P0} of it." : "."),
                c.CopilotShare >= 0.05 ? "Give Copilot a Fabric Copilot capacity of its own, or move heavy refreshes off the busiest hours."
                                       : "Move heavy refreshes and notebooks off the busiest hours, or size up for month end only.",
                0, Math.Round(monthly * 0.1m, 2), "Medium");
    }

    /// <summary>
    /// Licenses held by people who have not used the product in the idle window: take them back. Only where the
    /// product's activity was loaded, so a missing export never reads as nobody using it.
    /// </summary>
    public static Opportunity? IdleLicenses(string area, string product, int holders, int idle, decimal price, string cur, string? href = null) =>
        idle <= 0 || price <= 0 ? null
        : new(area, OpportunityKind.Save, $"Reclaim {idle:#,0} idle {product} license{(idle == 1 ? "" : "s")}",
            $"{idle:#,0} of {holders:#,0} holders ({(decimal)idle / Math.Max(1, holders):P0}) have not used {ProductOf(product)} in the last 30 days, at {Money(price, cur)} each a month.",
            "Ask their managers, then remove the license; reassign it before buying more.", Math.Round(idle * price, 2), 0, "High", href);

    static string ProductOf(string license) => license.StartsWith("Power BI", StringComparison.Ordinal) ? "Power BI"
        : license.StartsWith("Power Automate", StringComparison.Ordinal) ? "Power Automate" : license.StartsWith("Power Apps", StringComparison.Ordinal) ? "Power Apps" : license;

    /// <summary>
    /// Power Automate Process licenses on flows that run less than a hundred times a month: an owner with a Premium
    /// license can run them under that (Process is for flows run unattended or for many people at once).
    /// </summary>
    public static Opportunity? LightProcessFlows(int flows, int ownersWithPremium, decimal processPrice, decimal premiumPrice, string cur)
    {
        if (flows <= 0 || processPrice <= 0) return null;
        var save = ownersWithPremium * processPrice + (flows - ownersWithPremium) * Math.Max(0, processPrice - premiumPrice);
        return new(CostAreas.Automate, OpportunityKind.Save, $"Move {flows:#,0} quiet flow{(flows == 1 ? "" : "s")} off Process licenses",
            $"{flows:#,0} flow{(flows == 1 ? " runs" : "s run")} under 100 times a month on a Process license at {Money(processPrice, cur)}; {ownersWithPremium:#,0} of {(flows == 1 ? "its owner holds" : "their owners hold")} Power Automate Premium already.",
            "Unassign the Process license from these flows and run them under the owner's Premium license.", Math.Round(save, 2), 0, "Medium");
    }

    /// <summary>
    /// Copilot Credits: pay-as-you-go beyond the packs, where enough to fill packs costs less as packs; and packs the
    /// agents did not use. A pack is 25,000 credits a month.
    /// </summary>
    public static Opportunity? Credits(decimal prepaidUsed, int packs, decimal paygCredits, decimal paygCost, decimal packPrice, string cur, int packCredits = 25000)
    {
        if (packPrice <= 0) return null;
        if (paygCredits >= packCredits)
        {
            var more = (int)Math.Floor(paygCredits / packCredits);
            var save = paygCost - more * packPrice;
            if (save > 0)
                return new(CostAreas.Studio, OpportunityKind.Save, $"Buy {more} more Copilot Credit pack{(more == 1 ? "" : "s")} instead of paying as you go",
                    $"{paygCredits:#,0} credits a month go beyond the {packs} prepaid pack{(packs == 1 ? "" : "s")}, billed pay-as-you-go at {Money(paygCost, cur)}; {more} pack{(more == 1 ? "" : "s")} cost{(more == 1 ? "s" : "")} {Money(more * packPrice, cur)}.",
                    "Add the packs in the Microsoft 365 admin center; keep pay-as-you-go on for the peaks.", Math.Round(save, 2), 0, "High", "/agents/credits");
        }
        var spare = packs - (int)Math.Ceiling(prepaidUsed / packCredits);
        if (spare > 0 && paygCredits <= 0)
            return new(CostAreas.Studio, OpportunityKind.Save, $"Cancel {spare} unused Copilot Credit pack{(spare == 1 ? "" : "s")}",
                $"The agents used {prepaidUsed:#,0} of the {packs * packCredits:#,0} prepaid credits in the last 30 days.",
                "Reduce the pack count at the next renewal.", spare * packPrice, 0, "Medium", "/agents/credits");
        return null;
    }

    /// <summary>Dataverse capacity used over the entitlement, billed as add-on capacity each month.</summary>
    public static Opportunity? DataverseOverage(string type, decimal entitledGb, decimal usedGb, decimal pricePerGb, string cur)
    {
        var over = usedGb - entitledGb;
        if (over <= 0 || pricePerGb <= 0) return null;
        var (what, action) = type switch
        {
            "Log" => ("audit logs", "Shorten the audit log retention of the busiest environments, and turn off auditing where nobody reads it."),
            "File" => ("attachments and files", "Move old note and email attachments to SharePoint or Azure Blob storage."),
            _ => ("tables", "Delete old system jobs, bulk-deletion and import logs, and trial or orphaned environments."),
        };
        return new(CostAreas.Apps, OpportunityKind.Save, $"Bring Dataverse {type.ToLowerInvariant()} capacity back under its entitlement",
            $"{usedGb:#,0.#} GB used against {entitledGb:#,0.#} GB entitled: {over:#,0.#} GB of {what} over, at {Money(pricePerGb, cur)} a GB.",
            action, Math.Round(over * pricePerGb, 2), 0, type == "Log" ? "High" : "Medium");
    }

    /// <summary>GitHub Copilot seats unused in the idle window, or never used once assigned that long ago.</summary>
    public static Opportunity? IdleSeats(int seats, int idle, decimal saving, string cur) =>
        idle <= 0 ? null
        : new(CostAreas.GitHub, OpportunityKind.Save, $"Remove {idle:#,0} idle GitHub Copilot seat{(idle == 1 ? "" : "s")}",
            $"{idle:#,0} of {seats:#,0} seats ({(decimal)idle / Math.Max(1, seats):P0}) have no Copilot activity in the last 30 days, or never had any.",
            "Remove the seats in the enterprise's Copilot policies; GitHub stops billing from the next cycle.", Math.Round(saving, 2), 0, "High");

    /// <summary>A Foundry or Azure OpenAI account that cost money in the last 30 days and answered no request.</summary>
    public static Opportunity? IdleAccount(string account, decimal cost30, long requests30, int idleDays, string cur) =>
        requests30 > 0 || cost30 <= 0 ? null
        : new(CostAreas.Foundry, OpportunityKind.Save, $"Release the idle AI account {account}",
            $"No requests in {idleDays} days, and {Money(cost30, cur)} charged in the last 30: provisioned throughput, or storage and networking left running.",
            "Delete the deployment, or the account if nothing else uses it; a provisioned (PTU) deployment bills every hour it exists.", Math.Round(cost30, 2), 0, "High");

    /// <summary>Money, whole units above a hundred: "10,800 USD", "15.50 USD".</summary>
    public static string Money(decimal v, string cur) => (Math.Abs(v) >= 100 ? v.ToString("#,0", System.Globalization.CultureInfo.InvariantCulture) : v.ToString("#,0.##", System.Globalization.CultureInfo.InvariantCulture)) + " " + cur;

    /// <summary>The opportunities in the order an executive acts on them: the largest monthly saving first, then the most at stake.</summary>
    public static List<Opportunity> Ranked(IEnumerable<Opportunity> all) =>
        [.. all.OrderBy(o => o.Kind).ThenByDescending(o => o.Saving).ThenByDescending(o => o.AtStake).ThenBy(o => o.Title, StringComparer.Ordinal)];
}
