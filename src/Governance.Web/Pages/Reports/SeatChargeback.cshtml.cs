using System.Globalization;
using Governance.Application.Commands;
using Governance.Application.Queries;
using Governance.Application.Seats;
using Microsoft.AspNetCore.Mvc;

namespace Governance.Web.Pages.Reports;

/// <summary>Monthly Copilot seat chargeback per LOB: reservation plus seats above it, at one rate.</summary>
public class SeatChargebackModel(SeatQueries seats, SeatCommands commands) : GovPage
{
    [BindProperty(SupportsGet = true, Name = "month")] public string? MonthText { get; set; }
    public IReadOnlyList<DateTime> Months { get; private set; } = Array.Empty<DateTime>();
    public DateTime? Month { get; private set; }
    public IReadOnlyList<SeatSnapshotRow> Rows { get; private set; } = Array.Empty<SeatSnapshotRow>();
    public SeatSettings Settings { get; private set; } = SeatSettings.Default;
    public bool CanCertify => Principal.HasRole(Roles.FinOpsLead);
    public bool HasDraft => Rows.Any(r => r.Status == "DRAFT");

    private async Task LoadAsync()
    {
        Settings = await seats.SettingsAsync();
        Months = await seats.SnapshotMonthsAsync();
        Month = DateTime.TryParseExact(MonthText, "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out var m) ? m : Months.FirstOrDefault() is { Year: > 1 } f ? f : null;
        if (Month is DateTime mm) Rows = await seats.SnapshotAsync(mm);
    }

    public async Task OnGetAsync() => await LoadAsync();

    public async Task<IActionResult> OnGetCsvAsync()
    {
        await LoadAsync();
        return Csv(Rows.Select(r => new
        {
            Month = Month?.ToString("yyyy-MM"), Lob = r.LobName, r.Code, Reserved = r.ReservedSeats, Assigned = r.AssignedSeats, Over = r.OverSeats, Charged = r.ChargeSeats,
            Rate = r.RatePerSeat, r.Currency, r.Amount, r.Status, CertifiedBy = r.CertifiedByName, Certified = r.CertifiedUtc?.ToString("yyyy-MM-dd"),
        }), $"seat-chargeback-{Month:yyyy-MM}");
    }

    public Task<IActionResult> OnPostCertifyAsync(string month)
    {
        var m = DateTime.ParseExact(month, "yyyy-MM", CultureInfo.InvariantCulture);
        return Act(() => commands.CertifyMonthAsync(Idem, m), n => $"{m:MMMM yyyy} certified ({n} rows). It cannot change from now on.", _ => RedirectToPage(new { month }));
    }
}
