namespace CopilotInsights.Data;

/// <summary>An area of AI and automation spend on Leadership › Costs and savings, in the order the page shows them.</summary>
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
/// The rules that turn what the sources say into savings and opportunities. Pure, for the tests: the page
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
    public static Opportunity? IdleLicenses(string area, string product, int holders, int idle, decimal price, string cur, string? href = null, Cut? cut = null) =>
        idle <= 0 || price <= 0 || cut is { Seats: <= 0 } ? null
        : new(area, OpportunityKind.Save, $"Reclaim {cut?.Seats ?? idle:#,0} idle {product} license{((cut?.Seats ?? idle) == 1 ? "" : "s")}",
            $"{idle:#,0} of {holders:#,0} holders ({(decimal)idle / Math.Max(1, holders):P0}) have not used {ProductOf(product)} in the last 30 days, at {Money(price, cur)} each a month." + (cut?.Note is { } n ? " " + n : ""),
            "Ask their managers, then remove the license; reassign it before buying more.", cut?.Saving ?? Math.Round(idle * price, 2), 0, cut?.Confidence ?? "High", href);

    /// <summary>
    /// A standalone license on people whose suite already includes it: Power BI Pro on E5 or E7, the Copilot
    /// add-on on E7. Paid twice, whatever they use: certain.
    /// </summary>
    public static Opportunity? HeldTwice(string area, string product, string suites, int people, decimal price, string cur, string? href = null) =>
        people <= 0 || price <= 0 ? null
        : new(area, OpportunityKind.Save, $"Remove {people:#,0} {product} license{(people == 1 ? "" : "s")} that {suites} already include{(suites.Contains(" and ") || suites.Contains(", ") ? "" : "s")}",
            $"{people:#,0} {(people == 1 ? "person holds" : "people hold")} a standalone {product} at {Money(price, cur)} a month on top of {suites.Replace(" and ", " or ")}, which include{(suites.Contains(" and ") || suites.Contains(", ") ? "" : "s")} it.",
            "Remove the standalone license; the suite keeps the same features.", Math.Round(people * price, 2), 0, "High", href);

    /// <summary>
    /// Licenses bought and assigned to nobody: paid for every month all the same. Medium, as most agreements
    /// let them go only at the renewal; assign them first where people are waiting.
    /// </summary>
    /// <summary>
    /// Seats bought and assigned to nobody beyond the spare kept on purpose (<paramref name="spare"/> seats, for
    /// joiners and moves), paid for every month all the same. None for a license bought for its capacity or volume
    /// price (<paramref name="forCapacity"/>). Medium, as most agreements let them go only at the renewal.
    /// </summary>
    public static Opportunity? Unassigned(string area, string product, int bought, int assigned, decimal price, string cur, string? href = "/licensing/pool", Cut? cut = null,
        int spare = 0, bool forCapacity = false)
    {
        var over = bought - assigned - Math.Max(0, spare);
        if (forCapacity || over <= 0 || price <= 0 || cut is { Seats: <= 0 }) return null;
        var seats = cut?.Seats ?? over;
        return new(area, OpportunityKind.Save, $"Cut {seats:#,0} unassigned {product} license{(seats == 1 ? "" : "s")}",
            $"{bought:#,0} bought and {assigned:#,0} assigned: {bought - assigned:#,0} paid for at {Money(price, cur)} a month with nobody on {(bought - assigned == 1 ? "it" : "them")}" +
            (spare > 0 ? $", {spare:#,0} of them kept spare on purpose, {over:#,0} beyond that." : ".") + (cut?.Note is { } n ? " " + n : ""),
            "Assign them to the people waiting for one; reduce the count at the next renewal for the rest.", cut?.Saving ?? Math.Round(over * price, 2), 0, cut?.Confidence ?? "Medium", href);
    }

    /// <summary>The seats kept spare on purpose: a share of those bought, rounded up.</summary>
    public static int Spare(int bought, decimal percent) => bought <= 0 || percent <= 0 ? 0 : (int)Math.Ceiling(bought * Math.Min(percent, 100) / 100m);

    /// <summary>
    /// What a cut of seats really saves: at the volume price where the count stays at or over the tier, and less
    /// where it takes the count under it, when every seat goes back to the list price; then less the add-on Dataverse
    /// capacity the tenant would have to buy once the seats' capacity is given up. Seats is how many to cut: all asked,
    /// or only down to the tier where crossing it would save less. Note says why, for the evidence; Confidence is
    /// lowered where the Dataverse capacity could not be checked.
    /// </summary>
    public sealed record Cut(int Seats, decimal Saving, string? Note, string? Confidence);

    /// <summary>A license's price: the list price, a volume price from a number of seats, and the Dataverse capacity a seat brings.</summary>
    public sealed record SeatPrice(decimal Price, int? TierFrom = null, decimal? TierPrice = null, decimal DatabaseGbPerSeat = 0, decimal FileGbPerSeat = 0)
    {
        /// <summary>What each seat costs a month at this count.</summary>
        public decimal Each(int seats) => TierFrom is { } t && TierPrice is { } tp && seats >= t ? tp : Price;
        public decimal Monthly(int seats) => Math.Max(0, seats) * Each(seats);
        public bool HasTier => TierFrom is > 0 && TierPrice is not null;
        public bool BringsCapacity => DatabaseGbPerSeat > 0 || FileGbPerSeat > 0;
    }

    /// <summary>
    /// The tenant's Dataverse capacity left before it is over its entitlement, from the latest day read: what a cut
    /// of seats that bring capacity may give up before the tenant pays for add-on capacity. Each cut takes from it in
    /// turn. Unknown (no capacity loaded): nothing is taken off, and the cut says the capacity was not checked.
    /// </summary>
    public sealed class DataverseRoom(decimal? databaseGb, decimal? fileGb, decimal databasePrice, decimal filePrice)
    {
        decimal? _db = databaseGb, _file = fileGb;
        /* Both sides, or the check is not made: an unknown side would count as room to spare. */
        public bool Known => _db is not null && _file is not null;

        /// <summary>The add-on capacity a month that giving up these seats' capacity would cost, and takes it from what is left.</summary>
        public decimal Take(SeatPrice p, int seats)
        {
            if (!p.BringsCapacity || seats <= 0 || !Known) return 0;
            decimal Over(ref decimal? left, decimal need) { if (left is not { } l) return 0; var over = Math.Max(0, need - Math.Max(0, l)); left = l - need; return over; }
            return Math.Round(Over(ref _db, seats * p.DatabaseGbPerSeat) * databasePrice + Over(ref _file, seats * p.FileGbPerSeat) * filePrice, 2);
        }
    }

    /// <summary>A cut of <paramref name="cut"/> of <paramref name="seats"/>, priced as <see cref="Cut"/> says.</summary>
    public static Cut CutSeats(SeatPrice p, int seats, int cut, DataverseRoom? room, string cur)
    {
        cut = Math.Clamp(cut, 0, Math.Max(0, seats));
        if (cut == 0) return new(0, 0, null, null);
        decimal Save(int n) => p.Monthly(seats) - p.Monthly(seats - n);
        var notes = new List<string>();
        if (p.HasTier && seats >= p.TierFrom!.Value && seats - cut < p.TierFrom.Value)
        {
            var toTier = seats - p.TierFrom.Value;
            if (Save(toTier) >= Save(cut))
            {
                notes.Add($"Cutting all {cut:#,0} would take the count under {p.TierFrom:#,0} seats, where every seat goes back from {Money(p.TierPrice!.Value, cur)} to {Money(p.Price, cur)}: " +
                          (toTier > 0 ? $"cut {toTier:#,0}, down to the volume price's {p.TierFrom:#,0}, and reassign the rest." : "keep them, and reassign them."));
                cut = toTier;
            }
            else notes.Add($"The count falls under {p.TierFrom:#,0} seats, so every seat left goes back to {Money(p.Price, cur)}: counted in the saving.");
        }
        var saving = Save(cut);
        string? confidence = null;
        if (p.BringsCapacity && cut > 0)
        {
            if (room is null || !room.Known)
            {
                notes.Add($"Each seat also brings Dataverse capacity ({p.DatabaseGbPerSeat * 1000:#,0} MB database, {p.FileGbPerSeat:0.##} GB file), and the capacity in use is not loaded, so whether the tenant would go over it is not checked.");
                confidence = "Low";
            }
            else if (room.Take(p, cut) is var extra and > 0)
            {
                notes.Add($"Giving up the seats' Dataverse capacity takes the tenant over its entitlement: about {Money(extra, cur)} a month of add-on capacity, taken off the saving.");
                saving -= extra;
            }
        }
        return new(cut, Math.Round(Math.Max(0, saving), 2), notes.Count == 0 ? null : string.Join(" ", notes), confidence);
    }

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
    public static Opportunity? Credits(decimal prepaidUsed, int packs, decimal paygCredits, decimal paygCost, decimal packPrice, string cur, int packCredits = 25000, bool committed = false)
    {
        if (packPrice <= 0) return null;
        /* Under a credit commitment (P3) the metered credits draw on what is already paid for: buying packs for them is no saving. */
        if (paygCredits >= packCredits && !committed)
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
