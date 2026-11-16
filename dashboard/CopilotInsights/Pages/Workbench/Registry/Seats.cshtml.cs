using Governance.Application;
using Governance.Application.Data;
using Governance.Application.Queries;
using Governance.Domain;
using Governance.Web.Infrastructure;
using Governance.Application.Commands;
using Governance.Application.Queries;
using Microsoft.AspNetCore.Mvc;

namespace Governance.Web.Pages.Registry;

/// <summary>Copilot seats across LOBs: what is bought, reserved and assigned, and what is left.</summary>
public class SeatsModel(SeatQueries seats) : GovPage
{
    [BindProperty(SupportsGet = true, Name = "show")] public string? Show { get; set; }
    public SeatSummary Summary { get; private set; } = new();
    public IReadOnlyList<SeatLobRow> Lobs { get; private set; } = Array.Empty<SeatLobRow>();
    public IReadOnlyList<SeatHolderRow> Unattributed { get; private set; } = Array.Empty<SeatHolderRow>();
    public bool SeesAll => SeatAccess.SeesAll(Principal);

    public static bool CanOpen(Governance.Web.Infrastructure.WebPrincipal p) => p.SeesRegistry && (SeatAccess.SeesAll(p) || p.FundingOwnersWith(SeatAccess.View).Any());

    public async Task<IActionResult> OnGetAsync()
    {
        if (!CanOpen(Principal)) return Forbid();
        Summary = await seats.SummaryAsync();
        Lobs = await seats.LobsAsync();
        if (Show == "nolob" && SeesAll) Unattributed = await seats.UnattributedAsync();
        return Page();
    }

    public async Task<IActionResult> OnGetCsvAsync()
    {
        if (!CanOpen(Principal)) return Forbid();
        var rows = (await seats.LobsAsync()).Select(r => new
        {
            Lob = r.Name, r.Code, r.TrackingGroup, Reserved = r.ReservedSeats, r.TrackingMembers, Assigned = r.AssignedSeats, Over = r.OverSeats,
            UnusedReserved = r.UnusedReserved, InactiveSeats = r.Inactive, PendingIncrease = r.PendingSeats, OpenFindings = r.OpenFindings,
        });
        return Csv(rows, "copilot-seats");
    }
}

/// <summary>One LOB: reservation against actual seats, history, holders, reclaim answers and seat admins.</summary>
public class SeatLobModel(SeatQueries seats, SeatCommands commands) : GovPage
{
    [BindProperty(SupportsGet = true)] public Guid Id { get; set; }
    [BindProperty(SupportsGet = true, Name = "holders")] public string? HolderFilter { get; set; }
    public SeatLobDetail D { get; private set; } = new();
    public IReadOnlyList<Option> People { get; private set; } = Array.Empty<Option>();
    public int InactiveDays { get; private set; } = 60;

    private bool HasLobRole(params string[] roles) => Principal.Roles.Any(r => roles.Contains(r.RoleCode) && r.ScopeId == D.FundingOwnerId);
    public bool CanBaseline => Principal.Can(Permissions.RegistryFinance) && D.Reservations.Count == 0;
    public bool CanAnswerReclaim => HasLobRole(Roles.LobBusinessOwner, Roles.SharedServiceOwner, Roles.DelegatedApprover);
    public bool CanManageAdmins => HasLobRole(SeatAccess.LobSeatAdmin, Roles.LobBusinessOwner, Roles.SharedServiceOwner) || Principal.HasRole(Roles.PlatformOwner);
    public bool CanAskChange => Principal.Can(Permissions.RequestCreate, D.FundingOwnerId) || HasLobRole(Roles.LobBusinessOwner, Roles.SharedServiceOwner, Roles.DelegatedApprover, Roles.LobFinancePartner, SeatAccess.LobSeatAdmin) || Principal.Can(Permissions.RegistryFinance);

    public async Task<IActionResult> OnGetAsync()
    {
        var d = await seats.LobAsync(Id);
        if (d is null) return NotFound();
        D = d;
        InactiveDays = (await seats.SettingsAsync()).InactiveDays;
        if (CanManageAdmins) People = await seats.PeopleAsync();
        return Page();
    }

    public async Task<IActionResult> OnGetCsvAsync()
    {
        var d = await seats.LobAsync(Id);
        if (d is null) return NotFound();
        return Csv(d.Holders.Select(h => new { h.DisplayName, h.UserPrincipalName, Seat = h.HasSeat ? "yes" : "no", h.State, h.Error, Direct = h.Direct ? "yes" : "no", LastCopilotActivity = h.LastActivityDate?.ToString("yyyy-MM-dd") }), $"seats-{d.Code}");
    }

    public Task<IActionResult> OnPostBaselineAsync(int seatCount, DateTime effectiveFrom, string? notes)
        => Act(() => commands.RecordInitialReservationAsync(Idem, Id, seatCount, effectiveFrom, notes), "First reservation recorded. Later changes go through a seat reservation request.");

    public Task<IActionResult> OnPostReclaimAsync(Guid reclaim, bool take, string? reason)
        => Act(() => commands.DecideReclaimAsync(Idem, reclaim, take, reason), take ? "Reclaim sent: an ITSM task removes the OIM role. The seat shows as removed after the next license read." : "Kept. The person keeps the seat.");

    public Task<IActionResult> OnPostAddAdminAsync(string person)
        => Act(() => commands.AddSeatAdminAsync(Idem, Id, person), "Seat admin added. They can now track this LOB's seats and name other seat admins.");

    public Task<IActionResult> OnPostEndAdminAsync(Guid assignment)
        => Act(() => commands.EndSeatAdminAsync(Idem, assignment), "Seat admin ended.");
}
