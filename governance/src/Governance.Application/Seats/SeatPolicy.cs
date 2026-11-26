using System.Globalization;
using Governance.Application.Data;
using Governance.Domain;

namespace Governance.Application.Seats;

/// <summary>
/// The Copilot seat reservation policy as rules (Sections 4.1, 4.4, 6.10, 6.12; CAL-04 to CAL-06; FIN-05, FIN-06).
/// Pure functions over <see cref="SeatSettings"/>, so the journey, the pages, the snapshot job and the tests
/// all compute the same answer.
/// </summary>
public static class SeatPolicy
{
    /// <summary>The EA anniversary on or after <paramref name="day"/>.</summary>
    public static DateTime NextAnniversary(SeatSettings s, DateTime day)
    {
        var d = day.Date;
        var thisYear = new DateTime(d.Year, s.AnniversaryMonth, Math.Min(s.AnniversaryDay, DateTime.DaysInMonth(d.Year, s.AnniversaryMonth)));
        return thisYear >= d ? thisYear : new DateTime(d.Year + 1, s.AnniversaryMonth, Math.Min(s.AnniversaryDay, DateTime.DaysInMonth(d.Year + 1, s.AnniversaryMonth)));
    }

    /// <summary>The first day a true-down can be approved for the next anniversary.</summary>
    public static DateTime TrueDownWindowOpens(SeatSettings s, DateTime day) => NextAnniversary(s, day).AddDays(-s.TrueDownWindowDays);

    /// <summary>A decrease can be approved only from <see cref="TrueDownWindowOpens"/> to the anniversary itself (CAL-04).</summary>
    public static bool IsTrueDownWindowOpen(SeatSettings s, DateTime day) => day.Date >= TrueDownWindowOpens(s, day);

    /// <summary>
    /// When an approved change takes effect. A true-down applies from the anniversary onward (CAL-05); an increase
    /// or new reservation follows the Finance parameter (decision date, or the first day of the next month).
    /// </summary>
    public static DateTime EffectiveDate(SeatSettings s, string changeKind, DateTime decisionDay)
    {
        var d = decisionDay.Date;
        if (changeKind == SeatChangeKinds.TrueDown) return NextAnniversary(s, d);
        return s.IncreaseEffective == "NEXT_MONTH" ? new DateTime(d.Year, d.Month, 1).AddMonths(1) : d;
    }

    /// <summary>The kind a request is, from the current and requested seats.</summary>
    public static string KindOf(int? currentSeats, int requestedSeats)
        => currentSeats is null || currentSeats == 0 ? SeatChangeKinds.New
         : requestedSeats > currentSeats ? SeatChangeKinds.Increase
         : requestedSeats < currentSeats ? SeatChangeKinds.TrueDown
         : SeatChangeKinds.Correction;

    /// <summary>
    /// Seats Digital Workplace and Finance can still reserve (Section 4.4): purchased, less what is already reserved,
    /// less the operational buffer, less approved demand not yet in effect. Negative means over-committed.
    /// </summary>
    public static int Unallocated(int purchased, int reservedTotal, int bufferSeats, int pendingIncrease)
        => purchased - reservedTotal - bufferSeats - pendingIncrease;

    /// <summary>Chargeable seats for a month: the full reservation (FIN-05) plus assignments above it (FIN-06).</summary>
    public static (int Over, int Charge) Charge(int reserved, int assigned)
    {
        var over = Math.Max(0, assigned - reserved);
        return (over, reserved + over);
    }

    public static decimal Amount(SeatSettings s, int chargeSeats) => Math.Round(chargeSeats * s.RatePerSeatMonth, 2, MidpointRounding.AwayFromZero);

    /// <summary>
    /// The purchase gate, one rule for the Workbench and for Copilot Insights' pages: no more seats are bought until
    /// <paramref name="percent"/> of the unassigned seats counted at the baseline are assigned. Returns how many of them
    /// are assigned now, the share, how many may still be left unassigned (the buffer), and whether the gate is reached.
    /// </summary>
    public static (int Assigned, double Share, int Buffer, bool Reached) PurchaseGate(int baselineUnassigned, int unassignedNow, int percent)
    {
        var assigned = Math.Max(0, baselineUnassigned - unassignedNow);
        /* As the policy counts it: 95% of 1,136 is 1,079 to assign, leaving a buffer of 57. */
        var buffer = baselineUnassigned - (int)Math.Floor(baselineUnassigned * percent / 100.0);
        var share = baselineUnassigned > 0 ? assigned / (double)baselineUnassigned : 0;
        return (assigned, share, buffer, baselineUnassigned > 0 && unassignedNow <= buffer);
    }

    /// <summary>
    /// The purchase gate in a sentence, the same on every page that shows it. The setting's name only for an
    /// administrator, who can change it.
    /// </summary>
    public static string PurchaseGateSentence(SeatSettings s, int purchased, int assigned, bool forAdmin = false)
    {
        var unassigned = Math.Max(0, purchased - assigned);
        if (s.GateBaselineUnassigned <= 0)
            return "No purchase-gate baseline is set yet: an administrator sets the unassigned count it starts from" + (forAdmin ? " (Seats.GateBaselineUnassigned)." : ".");
        var g = PurchaseGate(s.GateBaselineUnassigned, unassigned, s.PurchaseGatePercent);
        static string N(int n) => n.ToString("#,0", CultureInfo.InvariantCulture);
        return $"{N(g.Assigned)} of the {N(s.GateBaselineUnassigned)} unassigned seats counted at the baseline are now assigned ({g.Share.ToString("0%", CultureInfo.InvariantCulture)}). " +
               (g.Reached ? $"The {s.PurchaseGatePercent}% gate is reached: a purchase may be considered, with a buffer of {N(g.Buffer)}."
                          : $"The gate is {s.PurchaseGatePercent}%: {N(Math.Max(0, unassigned - g.Buffer))} more to assign before a purchase is considered, leaving a buffer of {N(g.Buffer)}.");
    }

    /// <summary>
    /// Who pays for the seats no line of business claims, in a sentence: <paramref name="ownerName"/> is the active
    /// funding owner Seats.NoLobChargedTo names, or null when it names none (empty, or a code that is not an active one).
    /// </summary>
    public static string NoLobSentence(SeatSettings s, string? ownerName, bool forAdmin = false)
    {
        var key = forAdmin ? " (Seats.NoLobChargedTo)" : "";
        if (ownerName is not null) return $"Seats no line of business claims are charged to {ownerName}, as seats above its reservation.";
        if (s.NoLobChargedTo.Length == 0) return $"Seats no line of business claims are charged to no line of business, a row of their own, until Finance names who pays{key}.";
        return $"The funding owner named to pay for seats no line of business claims is not an active one{(forAdmin ? $" (code {s.NoLobChargedTo})" : "")}, so they stay a row of their own, charged to no line of business{key}.";
    }

    /// <summary>One line of business's month as charged: its reservation, the seats its tracking group holds, and seats it pays for on top.</summary>
    public sealed record ChargeRow(int? FundingOwnerId, int ReservedSeats, int AssignedSeats, int ExtraSeats);

    /// <summary>
    /// The month's rows as charged. Seats no line of business claims go to the funding owner Finance named
    /// (<paramref name="noLobOwnerId"/>, an active one) as seats above its reservation, with a row of its own when it
    /// has none; its assigned seats stay its tracking group's. With none named they stay a row of their own.
    /// </summary>
    public static IReadOnlyList<ChargeRow> ChargeRows(IReadOnlyList<(int? FundingOwnerId, int ReservedSeats, int AssignedSeats)> rows, int? noLobOwnerId)
    {
        var all = rows.Select(r => new ChargeRow(r.FundingOwnerId, r.ReservedSeats, r.AssignedSeats, 0)).ToList();
        if (noLobOwnerId is not int owner) return all;
        var noLob = rows.Where(r => r.FundingOwnerId is null).Sum(r => r.AssignedSeats);
        var charged = all.Where(r => r.FundingOwnerId is not null).ToList();
        var i = charged.FindIndex(r => r.FundingOwnerId == owner);
        if (i >= 0) charged[i] = charged[i] with { ExtraSeats = noLob };
        else if (noLob > 0) charged.Add(new ChargeRow(owner, 0, 0, noLob));
        return charged;
    }

    /// <summary>A row's chargeable seats: its reservation, the seats held above it, and any it pays for on top.</summary>
    public static (int Over, int Charge) Charge(ChargeRow r)
    {
        var (over, charge) = Charge(r.FundingOwnerId is null ? 0 : r.ReservedSeats, r.AssignedSeats);
        return (over + r.ExtraSeats, charge + r.ExtraSeats);
    }
    /// <summary>The month whose snapshot is taken on <paramref name="day"/>: the month that just ended.</summary>
    public static DateTime SnapshotMonthFor(DateTime day) => new DateTime(day.Year, day.Month, 1).AddMonths(-1);
}

/// <summary>Seat settings from ref.Setting (keys Seats.*), with the seeded defaults when a key is missing.</summary>
public sealed record SeatSettings(
    int AnniversaryMonth,
    int AnniversaryDay,
    int TrueDownWindowDays,
    int BufferSeats,
    decimal RatePerSeatMonth,
    string Currency,
    string IncreaseEffective,
    string ChargebackYear,
    int InactiveDays,
    string UsagePeriod)
{
    public static readonly SeatSettings Default = new(4, 1, 45, 25, 30.00m, "USD", "DECISION_DATE", "CALENDAR", 60, "D90");

    /// <summary>Which funding owner (by code) pays for the seats no line of business claims; empty: none, a row of their own.</summary>
    public string NoLobChargedTo { get; init; } = "";
    /// <summary>The purchase gate's share to assign first, in percent.</summary>
    public int PurchaseGatePercent { get; init; } = 95;
    /// <summary>The unassigned seats counted when the gate started; 0 until an administrator sets it.</summary>
    public int GateBaselineUnassigned { get; init; }
    /// <summary>Where the seat read takes the seat groups' members: ENTRA, AD or AUTO (Seats.GroupMembersFrom).</summary>
    public string GroupMembersFrom { get; init; } = "AUTO";
    /// <summary>The AD domain of the seat groups (Seats.AdDomain); empty: Copilot Insights' users' domain, else the server's own.</summary>
    public string AdDomain { get; init; } = "";

    /// <summary>
    /// A seat setting's value as it is kept, or a refusal saying what it takes. Only the Seats.* settings the seat pages
    /// read. Pure, for the tests; whether a funding owner code exists is the command's check.
    /// </summary>
    public static string Check(string key, string? value)
    {
        var v = (value ?? "").Trim();
        static string Whole(string v, int min, int max, string what) =>
            int.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n >= min && n <= max ? n.ToString(CultureInfo.InvariantCulture)
                : throw new DomainException($"{what} is a whole number from {min} to {max}.");
        return key switch
        {
            "Seats.EaAnniversary" => v.Split('-') is [var m, var d] && int.TryParse(m, out var mm) && int.TryParse(d, out var dd) && mm is >= 1 and <= 12
                                     && dd >= 1 && dd <= DateTime.DaysInMonth(2025, mm)
                ? $"{mm:00}-{dd:00}" : throw new DomainException("The EA anniversary is month-day, for example 04-01."),
            "Seats.TrueDownWindowDays" => Whole(v, 0, 365, "The true-down window"),
            "Seats.OperationalBufferSeats" => Whole(v, 0, 1_000_000, "The buffer"),
            "Seats.InactiveDays" => Whole(v, 1, 730, "Inactive days"),
            "Seats.PurchaseGatePercent" => Whole(v, 1, 100, "The purchase gate"),
            "Seats.GateBaselineUnassigned" => Whole(v, 0, 10_000_000, "The gate's baseline"),
            "Seats.RatePerSeatMonth" => decimal.TryParse(v, NumberStyles.Number, CultureInfo.InvariantCulture, out var r) && r >= 0 && r < 100_000
                ? r.ToString("0.00", CultureInfo.InvariantCulture) : throw new DomainException("The rate is an amount, for example 30.00."),
            "Seats.Currency" => v.Length == 3 && v.All(char.IsAsciiLetter) ? v.ToUpperInvariant() : throw new DomainException("The currency is a three-letter code, for example USD."),
            "Seats.IncreaseEffective" => v.ToUpperInvariant() is "DECISION_DATE" or "NEXT_MONTH" ? v.ToUpperInvariant() : throw new DomainException("An increase takes effect at DECISION_DATE or NEXT_MONTH."),
            "Seats.ChargebackYear" => v.Length is > 0 and <= 20 && v.All(c => char.IsAsciiLetter(c) || c == '_') ? v.ToUpperInvariant() : throw new DomainException("The chargeback year is a word such as CALENDAR."),
            "Seats.UsagePeriod" => v.ToUpperInvariant() is "D7" or "D30" or "D90" or "D180" ? v.ToUpperInvariant() : throw new DomainException("The usage period is D7, D30, D90 or D180."),
            "Seats.NoLobChargedTo" => v.Length <= 50 ? v : throw new DomainException("A funding owner's code, or empty."),
            "Seats.GroupMembersFrom" => v.ToUpperInvariant() is "AUTO" or "AD" or "ENTRA" ? v.ToUpperInvariant() : throw new DomainException("Group members come from AUTO, AD or ENTRA."),
            "Seats.AdDomain" => v.Length == 0 || v.Length <= 253 && v.Split('.').All(l => l.Length is > 0 and <= 63 && l.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
                ? v.ToLowerInvariant() : throw new DomainException("The AD domain is a DNS name such as users.contoso.com, or empty."),
            _ => throw new DomainException("That setting is not a seat setting."),
        };
    }

    public static SeatSettings From(IReadOnlyDictionary<string, string> v)
    {
        var d = Default;
        var (m, day) = (d.AnniversaryMonth, d.AnniversaryDay);
        if (v.TryGetValue("Seats.EaAnniversary", out var a) && a.Split('-') is [var ms, var ds]
            && int.TryParse(ms, out var mm) && int.TryParse(ds, out var dd) && mm is >= 1 and <= 12 && dd is >= 1 and <= 31)
            (m, day) = (mm, dd);
        return new SeatSettings(
            m, day,
            Int(v, "Seats.TrueDownWindowDays", d.TrueDownWindowDays),
            Int(v, "Seats.OperationalBufferSeats", d.BufferSeats),
            v.TryGetValue("Seats.RatePerSeatMonth", out var r) && decimal.TryParse(r, NumberStyles.Number, CultureInfo.InvariantCulture, out var rate) ? rate : d.RatePerSeatMonth,
            v.GetValueOrDefault("Seats.Currency") is { Length: 3 } c ? c.ToUpperInvariant() : d.Currency,
            v.GetValueOrDefault("Seats.IncreaseEffective") is "NEXT_MONTH" ? "NEXT_MONTH" : "DECISION_DATE",
            v.GetValueOrDefault("Seats.ChargebackYear") ?? d.ChargebackYear,
            Int(v, "Seats.InactiveDays", d.InactiveDays),
            v.GetValueOrDefault("Seats.UsagePeriod") is "D7" or "D30" or "D90" or "D180" ? v["Seats.UsagePeriod"] : d.UsagePeriod)
        {
            NoLobChargedTo = v.GetValueOrDefault("Seats.NoLobChargedTo")?.Trim() ?? "",
            PurchaseGatePercent = Int(v, "Seats.PurchaseGatePercent", 95) is var g && g is >= 1 and <= 100 ? g : 95,
            GateBaselineUnassigned = Int(v, "Seats.GateBaselineUnassigned", 0),
            GroupMembersFrom = Integrations.AdGroups.Mode(v.GetValueOrDefault("Seats.GroupMembersFrom")),
            AdDomain = v.GetValueOrDefault("Seats.AdDomain")?.Trim() ?? "",
        };
    }

    public static async Task<SeatSettings> LoadAsync(Uow uow)
    {
        var rows = await uow.QueryAsync<(string SettingKey, string SettingValue)>("SELECT SettingKey, SettingValue FROM ref.Setting WHERE SettingKey LIKE 'Seats.%'");
        return From(rows.ToDictionary(r => r.SettingKey, r => r.SettingValue));
    }

    private static int Int(IReadOnlyDictionary<string, string> v, string key, int fallback)
        => v.TryGetValue(key, out var s) && int.TryParse(s, NumberStyles.AllowThousands | NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite, CultureInfo.InvariantCulture, out var n) && n >= 0 ? n : fallback;
}
