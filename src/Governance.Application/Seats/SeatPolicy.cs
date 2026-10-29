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
            v.GetValueOrDefault("Seats.UsagePeriod") is "D7" or "D30" or "D90" or "D180" ? v["Seats.UsagePeriod"] : d.UsagePeriod);
    }

    public static async Task<SeatSettings> LoadAsync(Uow uow)
    {
        var rows = await uow.QueryAsync<(string SettingKey, string SettingValue)>("SELECT SettingKey, SettingValue FROM ref.Setting WHERE SettingKey LIKE 'Seats.%'");
        return From(rows.ToDictionary(r => r.SettingKey, r => r.SettingValue));
    }

    private static int Int(IReadOnlyDictionary<string, string> v, string key, int fallback)
        => v.TryGetValue(key, out var s) && int.TryParse(s, out var n) && n >= 0 ? n : fallback;
}
