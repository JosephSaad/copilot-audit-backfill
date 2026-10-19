namespace CopilotInsights.Data;

/// <summary>Where an organization stands against its reservation (2.6.8). Never a bare Over or Under.</summary>
public enum ReservationStatus { WithinReservation, AtReservation, OverReservation, NoReservation, DataException }

/// <summary>
/// The reservation and chargeback arithmetic (2.6.8), one way for the page, its exports and the stored snapshots, as the
/// client's Copilot reservation policy defines it. Every figure is worked out per organization and then summed: an
/// organization under its reservation never offsets another one over it.
/// <list type="bullet">
/// <item>Unused reserved capacity: MAX(Reserved - Allocated, 0).</item>
/// <item>Allocation over reservation: MAX(Allocated - Reserved, 0).</item>
/// <item>Billable licenses: Reserved + MAX(Allocated - Reserved, 0), that is MAX(Reserved, Allocated). The policy's
///   rule; the reserved-capacity proposal would bill on reservations alone, so the page says it awaits Finance.</item>
/// <item>Utilization: Allocated / Reserved; none (No Reservation) when nothing is reserved.</item>
/// <item>Monthly obligation: billable x the rate in force; remaining: the monthly obligation for each month left in
///   the calendar year after the current one.</item>
/// </list>
/// </summary>
public static class ReservationRules
{
    public static int Unused(int reserved, int allocated) => Math.Max(reserved - allocated, 0);
    public static int Over(int reserved, int allocated) => Math.Max(allocated - reserved, 0);
    public static int Billable(int reserved, int allocated) => reserved + Over(reserved, allocated);
    public static double? Utilization(int reserved, int allocated) => reserved == 0 ? null : allocated / (double)reserved;

    public static ReservationStatus Status(int reserved, int allocated, bool dataException) =>
        dataException ? ReservationStatus.DataException
        : reserved == 0 ? ReservationStatus.NoReservation
        : allocated < reserved ? ReservationStatus.WithinReservation
        : allocated == reserved ? ReservationStatus.AtReservation
        : ReservationStatus.OverReservation;

    public static string Label(ReservationStatus s) => s switch
    {
        ReservationStatus.WithinReservation => "Within reservation",
        ReservationStatus.AtReservation => "At reservation",
        ReservationStatus.OverReservation => "Over reservation",
        ReservationStatus.NoReservation => "No reservation",
        _ => "Data exception",
    };

    /// <summary>A month's obligation, or null when no rate is in force (Pending Finance Decision).</summary>
    public static decimal? Monthly(int billable, decimal? rate) => rate is { } r ? billable * r : null;

    /// <summary>The whole months left in the calendar year after the month of <paramref name="asOf"/> (2 on 19 October).</summary>
    public static int MonthsLeft(DateTime asOf) => 12 - asOf.Month;

    /// <summary>The obligation for the months left in the year, or null without a rate.</summary>
    public static decimal? Remaining(int billable, decimal? rate, DateTime asOf) => Monthly(billable, rate) is { } m ? m * MonthsLeft(asOf) : null;

    /// <summary>
    /// The purchase gate: no more licenses are bought until <paramref name="percent"/> of the unassigned licenses counted at
    /// the baseline are assigned. Returns how many of them are assigned now, the share, how many may still be left
    /// unassigned (the buffer), and whether the gate is reached.
    /// </summary>
    public static (int Assigned, double Share, int Buffer, bool Reached) Gate(int baselineUnassigned, int unassignedNow, int percent)
    {
        var assigned = Math.Max(0, baselineUnassigned - unassignedNow);
        /* As the policy counts it: 95% of 1,136 is 1,079 to assign, leaving a buffer of 57. */
        var buffer = baselineUnassigned - (int)Math.Floor(baselineUnassigned * percent / 100.0);
        var share = baselineUnassigned > 0 ? assigned / (double)baselineUnassigned : 0;
        return (assigned, share, buffer, baselineUnassigned > 0 && unassignedNow <= buffer);
    }
}
