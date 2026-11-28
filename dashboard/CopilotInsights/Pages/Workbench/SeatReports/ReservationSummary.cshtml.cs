using Governance.Application.Queries;
using Governance.Application.Seats;
using Governance.Web.Infrastructure;
using Microsoft.AspNetCore.Mvc;

namespace Governance.Web.Pages.Reports;

/// <summary>
/// Each line of business's Copilot reservation against the seats its people hold today, and what that costs: the
/// reserved cost, the 12-month forecast, the actual cost (the reservation, or the seats held when more) and the overage.
/// </summary>
public class ReservationSummaryModel(SeatQueries seats) : GovPage
{
    /// <summary>One line of business, priced at the one rate per seat per month.</summary>
    public sealed record Line(string Name, string? Code, Guid? PublicId, int Reserved, int Allocated, decimal Rate)
    {
        public int Remaining => Reserved - Allocated;
        public int OverUnder => Allocated - Reserved;
        public decimal MonthlyReserved => Reserved * Rate;
        public decimal YearReserved => MonthlyReserved * 12;
        /// <summary>The reservation is paid in full; seats held above it are paid on top.</summary>
        public decimal MonthlyActual => Math.Max(Reserved, Allocated) * Rate;
        public decimal MonthlyOverage => MonthlyActual - MonthlyReserved;
        public decimal YearActual => MonthlyActual * 12;
        /// <summary>over, under or even: the actual cost's color.</summary>
        public string State => Allocated > Reserved ? "over" : Allocated < Reserved ? "under" : "even";
    }

    public IReadOnlyList<Line> Lines { get; private set; } = Array.Empty<Line>();
    public Line Total { get; private set; } = new("Total", null, null, 0, 0, 0);
    /// <summary>Seats no tracking group claims: held, but charged to no line of business.</summary>
    public int NoLob { get; private set; }
    public SeatSettings Settings { get; private set; } = SeatSettings.Default;
    public decimal MaxOverage => Lines.Count == 0 ? 0 : Lines.Max(l => l.MonthlyOverage);

    /// <summary>The lines from the seat position; pure, for the tests.</summary>
    public static IReadOnlyList<Line> Build(IEnumerable<SeatLobRow> rows, decimal rate) => rows
        .Where(r => r.FundingOwnerId is not null)
        .Select(r => new Line(r.Name, r.Code, r.PublicId, r.ReservedSeats, r.AssignedSeats, rate))
        .OrderBy(l => l.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

    private async Task LoadAsync()
    {
        Settings = await seats.SettingsAsync();
        var rows = await seats.LobsAsync();
        Lines = Build(rows, Settings.RatePerSeatMonth);
        NoLob = rows.Where(r => r.FundingOwnerId is null).Sum(r => r.AssignedSeats);
        Total = new Line("Total", null, null, Lines.Sum(l => l.Reserved), Lines.Sum(l => l.Allocated), Settings.RatePerSeatMonth);
    }

    public async Task<IActionResult> OnGetAsync()
    {
        if (!SeatAuditAccess.CanOpen(Principal)) return Forbid();
        await LoadAsync();
        return Page();
    }

    public async Task<IActionResult> OnGetCsvAsync()
    {
        if (!SeatAuditAccess.CanOpen(Principal)) return Forbid();
        await LoadAsync();
        // The total's monthly actual is the sum of the lines' (each pays its own overage), not the total's own maximum.
        return Csv(Lines.Select(l => new
        {
            Lob = l.Name, l.Code, ReservedLicenses = l.Reserved, AllocatedLicenses = l.Allocated, RemainingLicenses = l.Remaining, OverUnderReservation = l.OverUnder,
            MonthlyReservedCost = l.MonthlyReserved, TwelveMonthReservedForecast = l.YearReserved, MonthlyActualCost = l.MonthlyActual,
            MonthlyReservedCostOverage = l.MonthlyOverage, AnnualActualCost = l.YearActual, Settings.Currency,
        }), "reservation-summary");
    }
}
