using System.Globalization;
using Governance.Application;
using Governance.Application.Commands;
using Governance.Application.Queries;
using Governance.Application.Seats;
using Governance.Domain;
using Governance.Web.Infrastructure;
using Microsoft.AspNetCore.Mvc;

namespace Governance.Web.Pages.Reports;

/// <summary>
/// Each line of business's Copilot seats and what they cost, month by month (?month=yyyy-MM). From Seats.TrackingStart
/// a month is built from its days: peak, low, average and month-end seats; the line pays the larger of its peak and its
/// reservation at the rate, the overage is the peak above the reservation, and the cumulative overage adds the months
/// since the last reset. A month stays a draft until the Platform Owner or FinOps certifies it; certified, it never
/// changes. Before tracking starts, today's live figures are shown.
/// </summary>
public class ReservationSummaryModel(SeatQueries seats, SeatCommands commands, SeatAudit audit) : GovPage
{
    /// <summary>One line of business today, priced at the one rate per seat per month.</summary>
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

    [BindProperty(SupportsGet = true, Name = "month")] public string? MonthText { get; set; }
    [BindProperty(SupportsGet = true, Name = "period")] public int? PeriodId { get; set; }
    [BindProperty(SupportsGet = true, Name = "editperiod")] public int? EditPeriodId { get; set; }
    public IReadOnlyList<SeatPeriod> Periods { get; private set; } = Array.Empty<SeatPeriod>();
    public SeatPeriod? Period { get; private set; }
    /// <summary>The whole period, when "Whole period" is chosen (month=all).</summary>
    public SeatPeriodView? PeriodView { get; private set; }
    /// <summary>The months to pick from: the chosen period's, up to this month (or every tracked month when there is no period).</summary>
    public IReadOnlyList<DateTime> PeriodMonths { get; private set; } = Array.Empty<DateTime>();
    public IReadOnlyList<Line> Lines { get; private set; } = Array.Empty<Line>();
    /// <summary>Seats no tracking group claims: held, but charged to no line of business.</summary>
    public int NoLob { get; private set; }
    /// <summary>Seats of central-cost lines of business today.</summary>
    public int Central { get; private set; }
    public SeatSettings Settings { get; private set; } = SeatSettings.Default;
    public decimal MaxOverage => Lines.Count == 0 ? 0 : Lines.Max(l => l.MonthlyOverage);
    public IReadOnlyList<DateTime> Months { get; private set; } = Array.Empty<DateTime>();
    public SeatMonthView? View { get; private set; }
    public bool CanCertify => Principal.HasRole(Roles.PlatformOwner) || Principal.HasRole(Roles.FinOpsLead) || Principal.Can(Permissions.RegistryFinance);
    public bool CanReopen => Principal.HasRole(Roles.PlatformOwner);

    /// <summary>The lines from the seat position, central cost left out; pure, for the tests.</summary>
    public static IReadOnlyList<Line> Build(IEnumerable<SeatLobRow> rows, decimal rate) => rows
        .Where(r => r.FundingOwnerId is not null && !r.IsCentralCost)
        .Select(r => new Line(r.Name, r.Code, r.PublicId, r.ReservedSeats, r.AssignedSeats, rate))
        .OrderBy(l => l.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

    public static DateTime? ParseMonth(string? text) => DateTime.TryParseExact(text, "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out var m) ? m : null;

    private async Task LoadAsync()
    {
        Settings = await seats.SettingsAsync();
        Months = await seats.SeatMonthsAsync();
        Periods = await seats.SeatPeriodsAsync();
        var picked = ParseMonth(MonthText);
        // The period: the one asked for, else the one holding the month asked for, else the one holding this month, else the latest.
        Period = Periods.FirstOrDefault(p => p.SeatPeriodId == PeriodId)
                 ?? (picked is DateTime pm ? SeatMonths.PeriodFor(Periods, pm) : null)
                 ?? (Months.Count > 0 ? SeatMonths.PeriodFor(Periods, Months[0]) : null)
                 ?? Periods.LastOrDefault();
        PeriodMonths = Period is { } per ? Months.Where(x => x >= per.StartMonth && x <= per.EndMonth).ToList() : Months;
        if (MonthText == "all" && Period is { } whole) PeriodView = await seats.SeatPeriodAsync(whole);
        else
        {
            var month = picked is DateTime m && PeriodMonths.Contains(m) ? m : PeriodMonths.FirstOrDefault();
            if (month != default) View = await seats.SeatMonthAsync(month);
        }
        var rows = await seats.LobsAsync();
        Lines = Build(rows, Settings.RatePerSeatMonth);
        NoLob = rows.Where(r => r.FundingOwnerId is null).Sum(r => r.AssignedSeats);
        Central = rows.Where(r => r.IsCentralCost).Sum(r => r.AssignedSeats);
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
        if (View is { Status: not "NOT_TRACKED" } v)
            return Csv(v.Lines.Select(l => new
            {
                Month = v.Month.ToString("yyyy-MM"), v.Status, Lob = l.Name, l.TrackingGroup, Kind = l.IsNoLob ? "No LOB" : l.IsCentralCost ? "Central cost" : "Charged",
                ReservedLicenses = l.Reserved, PeakSeats = l.PeakSeats, LowSeats = l.LowSeats, AverageSeats = l.AvgSeats, MonthEndSeats = l.MonthEndSeats,
                ChargedSeats = l.ChargeSeats, MonthlyCharge = l.Charge, Overage = l.Overage, CumulativeOverage = l.CumulativeOverage, Adjustments = l.Adjustments,
                DaysMeasured = l.DaysMeasured, v.Rate, v.Currency,
            }), $"reservation-summary-{v.Month:yyyy-MM}");
        return Csv(Lines.Select(l => new
        {
            Lob = l.Name, l.Code, ReservedLicenses = l.Reserved, AllocatedLicenses = l.Allocated, RemainingLicenses = l.Remaining, OverUnderReservation = l.OverUnder,
            MonthlyReservedCost = l.MonthlyReserved, TwelveMonthReservedForecast = l.YearReserved, MonthlyActualCost = l.MonthlyActual,
            MonthlyReservedCostOverage = l.MonthlyOverage, AnnualActualCost = l.YearActual, Settings.Currency,
        }), "reservation-summary");
    }

    /// <summary>Certifies the month with the hierarchy audit and Duplicate review frozen as they stand.</summary>
    public async Task<IActionResult> OnPostCertifyAsync(string month, string? reason)
    {
        if (ParseMonth(month) is not DateTime m) return BadRequest();
        string? auditJson = null, duplicatesJson = null;
        try
        {
            var o = await audit.GetAsync();
            auditJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                Metrics = SeatAuditModel.Summarize(o.Rows, o.NewHireDays),
                Groups = SeatAuditModel.ByGroup(o.Rows),
                o.TakenUtc,
            });
            duplicatesJson = System.Text.Json.JsonSerializer.Serialize(await seats.MultipleGroupsAsync());
        }
        catch (Exception ex) { Incident.Note(Log, ex, "freezing the hierarchy audit for a certified month"); }
        return await Act(() => commands.CertifySeatMonthAsync(Idem, m, reason, auditJson, duplicatesJson),
            $"{m:MMMM yyyy} certified. Its figures, the hierarchy audit and Duplicate review are kept as they are now.", () => RedirectToPage(new { month }));
    }

    public Task<IActionResult> OnPostSavePeriodAsync(int? periodId, string name, string startMonth, string endMonth)
        => ParseMonth(startMonth) is DateTime s && ParseMonth(endMonth) is DateTime e
            ? Act(() => commands.SavePeriodAsync(Idem, periodId, name, s, e), $"Period saved: {name}, {s:MMM yyyy} to {e:MMM yyyy}.", () => RedirectToPage(new { period = periodId }))
            : Task.FromResult<IActionResult>(BadRequest());

    public Task<IActionResult> OnPostDeletePeriodAsync(int periodId)
        => Act(() => commands.DeletePeriodAsync(Idem, periodId), "Period removed.", () => RedirectToPage());

    public Task<IActionResult> OnPostReopenAsync(string month, string? reason)
        => ParseMonth(month) is DateTime m
            ? Act(() => commands.ReopenSeatMonthAsync(Idem, m, reason), $"{m:MMMM yyyy} reopened: its figures are built from its days again.", () => RedirectToPage(new { month }))
            : Task.FromResult<IActionResult>(BadRequest());
}
