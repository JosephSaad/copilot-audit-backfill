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
public class SeatsModel(SeatQueries seats, CopilotInsights.Infrastructure.Db insights) : GovPage
{
    [BindProperty(SupportsGet = true, Name = "show")] public string? Show { get; set; }
    public SeatSummary Summary { get; private set; } = new();
    public IReadOnlyList<SeatLobRow> Lobs { get; private set; } = Array.Empty<SeatLobRow>();
    /// <summary>Seats no LOB claims: this page of them (50), and how many in all.</summary>
    public IReadOnlyList<SeatHolderRow> Unattributed { get; private set; } = Array.Empty<SeatHolderRow>();
    public int UnattributedTotal { get; private set; }
    public int UnattributedPages => Math.Max(1, (UnattributedTotal + SeatLobModel.HolderPageSize - 1) / SeatLobModel.HolderPageSize);
    public IReadOnlyDictionary<string, (string Name, string Upn)> DirectoryNames { get; private set; } = new Dictionary<string, (string Name, string Upn)>();
    public string NameOf(SeatHolderRow h) => SeatNames.NameOf(h, DirectoryNames);
    public string UpnOf(SeatHolderRow h) => SeatNames.UpnOf(h, DirectoryNames);
    public bool SeesAll => SeatAccess.SeesAll(Principal);

    public static bool CanOpen(Governance.Web.Infrastructure.WebPrincipal p) => p.SeesRegistry && (SeatAccess.SeesAll(p) || p.FundingOwnersWith(SeatAccess.View).Any());

    public async Task<IActionResult> OnGetAsync()
    {
        if (!CanOpen(Principal)) return Forbid();
        Summary = await seats.SummaryAsync();
        Lobs = await seats.LobsAsync();
        if (Show == "nolob" && SeesAll)
        {
            var all = ListSort.By((await seats.UnattributedAsync()).OrderBy(h => h.UserPrincipalName ?? "\uffff", StringComparer.OrdinalIgnoreCase), Sort, Desc, SeatLobModel.HolderKeys).ToList();
            UnattributedTotal = all.Count;
            PageNo = Math.Clamp(PageNo, 1, UnattributedPages);
            Unattributed = all.Skip((PageNo - 1) * SeatLobModel.HolderPageSize).Take(SeatLobModel.HolderPageSize).ToList();
            DirectoryNames = await SeatNames.ForAsync(insights, Log, Unattributed);
        }
        return Page();
    }

    public async Task<IActionResult> OnGetCsvAsync()
    {
        if (!CanOpen(Principal)) return Forbid();
        if (Show == "nolob")
        {
            if (!SeesAll) return Forbid();
            var all = await seats.UnattributedAsync();
            DirectoryNames = await SeatNames.ForAsync(insights, Log, all);
            return Csv(all.Select(h => new { DisplayName = NameOf(h), UserPrincipalName = UpnOf(h), HowAssigned = h.Direct ? "directly" : "license group", h.State, h.Error, LastCopilotActivity = h.LastActivityDate?.ToString("yyyy-MM-dd") }), "copilot-seats-no-lob");
        }
        var rows = (await seats.LobsAsync()).Select(r => new
        {
            Lob = r.Name, r.Code, r.TrackingGroup, Reserved = r.ReservedSeats, r.TrackingMembers, Assigned = r.AssignedSeats, Over = r.OverSeats,
            UnusedReserved = r.UnusedReserved, InactiveSeats = r.Inactive, PendingIncrease = r.PendingSeats, OpenFindings = r.OpenFindings,
        });
        return Csv(rows, "copilot-seats");
    }
}

/// <summary>One LOB: reservation against actual seats, history, holders, reclaim answers and seat admins.</summary>
public class SeatLobModel(SeatQueries seats, SeatCommands commands, CopilotInsights.Infrastructure.Db insights) : GovPage
{
    public const int HolderPageSize = 50;
    [BindProperty(SupportsGet = true)] public Guid Id { get; set; }
    [BindProperty(SupportsGet = true, Name = "holders")] public string? HolderFilter { get; set; }
    /// <summary>The page of the list on show (the Workbench list page number, ?page=).</summary>
    public int HolderPageNo { get; private set; } = 1;
    /// <summary>The people in the chosen list (with a seat, inactive, no seat, everyone), this page of them, and how many in all.</summary>
    public IReadOnlyList<SeatHolderRow> HolderPage { get; private set; } = Array.Empty<SeatHolderRow>();
    public int HolderTotal { get; private set; }
    public int HolderPages => Math.Max(1, (HolderTotal + HolderPageSize - 1) / HolderPageSize);
    /// <summary>Names from Copilot Insights' nightly AD directory, by sign-in, for people the Workbench has not recorded.</summary>
    public IReadOnlyDictionary<string, (string Name, string Upn)> DirectoryNames { get; private set; } = new Dictionary<string, (string Name, string Upn)>();
    public string NameOf(SeatHolderRow h) => SeatNames.NameOf(h, DirectoryNames);
    public string UpnOf(SeatHolderRow h) => SeatNames.UpnOf(h, DirectoryNames);

    /// <summary>The headings a holder list sorts by (?sort=).</summary>
    public static readonly IReadOnlyDictionary<string, Func<SeatHolderRow, IComparable?>> HolderKeys = new Dictionary<string, Func<SeatHolderRow, IComparable?>>
    {
        ["person"] = h => h.UserPrincipalName, ["seat"] = h => h.HasSeat, ["state"] = h => h.State, ["last"] = h => h.LastActivityDate,
        ["how"] = h => h.Direct,
    };

    /// <summary>The list a filter names, by sign-in; pure, for the tests.</summary>
    public static IEnumerable<SeatHolderRow> Filter(IEnumerable<SeatHolderRow> all, string? filter, int inactiveDays, DateTime today) => (filter switch
    {
        "noseat" => all.Where(h => !h.HasSeat),
        "inactive" => all.Where(h => h.HasSeat && (h.LastActivityDate is null || h.LastActivityDate < today.AddDays(-inactiveDays))),
        "all" => all,
        _ => all.Where(h => h.HasSeat),
    }).OrderBy(h => h.UserPrincipalName ?? "\uffff", StringComparer.OrdinalIgnoreCase);

    private Task<IReadOnlyDictionary<string, (string Name, string Upn)>> NamesAsync(IEnumerable<SeatHolderRow> rows) => SeatNames.ForAsync(insights, Log, rows);

    /// <summary>The reservation figure opened for a correction (?edit=), under the history.</summary>
    [BindProperty(SupportsGet = true, Name = "edit")] public Guid? EditId { get; set; }
    public SeatLobDetail D { get; private set; } = new();
    public IReadOnlyList<Option> People { get; private set; } = Array.Empty<Option>();
    public int InactiveDays { get; private set; } = 60;

    private bool HasLobRole(params string[] roles) => Principal.Roles.Any(r => roles.Contains(r.RoleCode) && r.ScopeId == D.FundingOwnerId);
    public bool CanBaseline => !SeatsOnly && Principal.Can(Permissions.RegistryFinance) && D.Reservations.Count == 0;
    /// <summary>Copilot seats only: the Platform Owner alone sets and corrects the reservation here (there is no request journey).</summary>
    public bool CanSetReservation => SeatsOnly && Principal.HasRole(Roles.PlatformOwner);
    /// <summary>The Platform Owner or FinOps changes the business owner here, at any time after the line of business was added.</summary>
    public bool CanSetOwner => Principal.Can(Permissions.RegistryFinance) || Principal.Can(Permissions.RegistryEdit);
    public bool CanAnswerReclaim => HasLobRole(Roles.LobBusinessOwner, Roles.SharedServiceOwner, Roles.DelegatedApprover);
    public bool CanManageAdmins => HasLobRole(SeatAccess.LobSeatAdmin, Roles.LobBusinessOwner, Roles.SharedServiceOwner) || Principal.HasRole(Roles.PlatformOwner);
    public bool CanAskChange => Principal.Can(Permissions.RequestCreate, D.FundingOwnerId) || HasLobRole(Roles.LobBusinessOwner, Roles.SharedServiceOwner, Roles.DelegatedApprover, Roles.LobFinancePartner, SeatAccess.LobSeatAdmin) || Principal.Can(Permissions.RegistryFinance);

    public async Task<IActionResult> OnGetAsync()
    {
        var d = await seats.LobAsync(Id);
        if (d is null) return NotFound();
        D = d;
        InactiveDays = (await seats.SettingsAsync()).InactiveDays;
        if (CanManageAdmins || CanSetOwner) People = await seats.PeopleAsync();
        var list = ListSort.By(Filter(d.Holders, HolderFilter, InactiveDays, DateTime.UtcNow.Date), Sort, Desc, HolderKeys).ToList();
        HolderTotal = list.Count;
        HolderPageNo = Math.Clamp(PageNo, 1, HolderPages);
        HolderPage = list.Skip((HolderPageNo - 1) * HolderPageSize).Take(HolderPageSize).ToList();
        DirectoryNames = await NamesAsync(HolderPage);
        return Page();
    }

    /// <summary>The list on show (with a seat, inactive, no seat or everyone), every page of it.</summary>
    public async Task<IActionResult> OnGetCsvAsync()
    {
        var d = await seats.LobAsync(Id);
        if (d is null) return NotFound();
        var days = (await seats.SettingsAsync()).InactiveDays;
        var list = ListSort.By(Filter(d.Holders, HolderFilter, days, DateTime.UtcNow.Date), Sort, Desc, HolderKeys).ToList();
        DirectoryNames = await NamesAsync(list);
        var which = HolderFilter is "noseat" or "inactive" or "all" ? HolderFilter : "seat";
        return Csv(list.Select(h => new { DisplayName = NameOf(h), UserPrincipalName = UpnOf(h), Seat = h.HasSeat ? "yes" : "no", h.State, h.Error, Direct = h.Direct ? "yes" : "no", LastCopilotActivity = h.LastActivityDate?.ToString("yyyy-MM-dd") }), $"seats-{d.Code}-{which}");
    }

    public Task<IActionResult> OnPostSetReservationAsync(int seatCount, DateTime effectiveFrom, string? notes)
        => Act(() => commands.SetReservationAsync(Idem, Id, seatCount, effectiveFrom, notes), $"Reservation set: {seatCount:N0} seats from {effectiveFrom:d MMM yyyy}.");

    public Task<IActionResult> OnPostDeleteReservationAsync(Guid reservation)
        => Act(() => commands.DeleteReservationAsync(Idem, reservation), "Deleted from the reservation history. The figure before it now runs on in its place.");

    public Task<IActionResult> OnPostEditReservationAsync(Guid reservation, int seatCount, DateTime effectiveFrom, string? notes)
        => Act(() => commands.EditReservationAsync(Idem, reservation, seatCount, effectiveFrom, notes), $"Reservation corrected: {seatCount:N0} seats from {effectiveFrom:d MMM yyyy}.");

    public Task<IActionResult> OnPostOwnerAsync(string? owner)
        => Act(() => commands.SetBusinessOwnerAsync(Idem, Id, owner), string.IsNullOrWhiteSpace(owner) ? "Business owner cleared." : "Business owner changed. The previous owner no longer holds the role.");

    public Task<IActionResult> OnPostBaselineAsync(int seatCount, DateTime effectiveFrom, string? notes)
        => Act(() => commands.RecordInitialReservationAsync(Idem, Id, seatCount, effectiveFrom, notes), "First reservation recorded. Later changes go through a seat reservation request.");

    public Task<IActionResult> OnPostReclaimAsync(Guid reclaim, bool take, string? reason)
        => Act(() => commands.DecideReclaimAsync(Idem, reclaim, take, reason), take ? "Reclaim sent: an ITSM task removes the OIM role. The seat shows as removed after the next license read." : "Kept. The person keeps the seat.");

    public Task<IActionResult> OnPostAddAdminAsync(string person)
        => Act(() => commands.AddSeatAdminAsync(Idem, Id, person), "Seat admin added. They can now track this LOB's seats and name other seat admins.");

    public Task<IActionResult> OnPostEndAdminAsync(Guid assignment)
        => Act(() => commands.EndSeatAdminAsync(Idem, assignment), "Seat admin ended.");
}

/// <summary>
/// Names for seat holders the Workbench has not recorded (it keeps only people who opened it or were named to a role):
/// read from the directory Copilot Insights loads from AD each night, by sign-in, else by directory object ID (a group
/// member without a seat has no sign-in from the license read). A failure leaves the names out.
/// </summary>
public static class SeatNames
{
    /// <summary>The names found, keyed by sign-in and by "oid:" + object ID; each with the sign-in the directory has.</summary>
    public static async Task<IReadOnlyDictionary<string, (string Name, string Upn)>> ForAsync(CopilotInsights.Infrastructure.Db insights, ILogger log, IEnumerable<SeatHolderRow> rows)
    {
        var missing = rows.Where(h => h.DisplayName is null).ToList();
        var upns = missing.Where(h => !string.IsNullOrWhiteSpace(h.UserPrincipalName)).Select(h => h.UserPrincipalName!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var oids = missing.Where(h => string.IsNullOrWhiteSpace(h.UserPrincipalName) && h.MemberObjectId != Guid.Empty).Select(h => h.MemberObjectId).Distinct().ToList();
        var map = new Dictionary<string, (string Name, string Upn)>(StringComparer.OrdinalIgnoreCase);
        if (upns.Count + oids.Count == 0) return map;
        try
        {
            var found = await insights.QueryAsync<(string Upn, string? DisplayName, Guid? ObjectId)>(
                """
                IF COL_LENGTH(N'dash.Person', N'ObjectId') IS NULL
                    SELECT p.Upn, p.DisplayName, CAST(NULL AS uniqueidentifier) AS ObjectId FROM dash.Person AS p WHERE p.Upn IN (SELECT j.value FROM OPENJSON(@upns) AS j);
                ELSE EXEC sp_executesql N'
                    SELECT p.Upn, p.DisplayName, p.ObjectId FROM dash.Person AS p WHERE p.Upn IN (SELECT j.value FROM OPENJSON(@upns) AS j)
                    UNION
                    SELECT p.Upn, p.DisplayName, p.ObjectId FROM dash.Person AS p WHERE p.ObjectId IN (SELECT TRY_CAST(j.value AS uniqueidentifier) FROM OPENJSON(@oids) AS j);',
                    N'@upns nvarchar(max), @oids nvarchar(max)', @upns = @upns, @oids = @oids;
                """,
                new { upns = System.Text.Json.JsonSerializer.Serialize(upns), oids = System.Text.Json.JsonSerializer.Serialize(oids) });
            foreach (var f in found.Where(f => !string.IsNullOrWhiteSpace(f.DisplayName)))
            {
                map.TryAdd(f.Upn, (f.DisplayName!, f.Upn));
                if (f.ObjectId is Guid o) map.TryAdd("oid:" + o.ToString("D"), (f.DisplayName!, f.Upn));
            }
        }
        catch (Exception ex)
        {
            Incident.Note(log, ex, "reading names from Copilot Insights directory");
        }
        return map;
    }

    private static (string Name, string Upn)? Find(SeatHolderRow h, IReadOnlyDictionary<string, (string Name, string Upn)> names)
        => h.UserPrincipalName is { Length: > 0 } u ? (names.TryGetValue(u, out var a) ? a : null)
           : names.TryGetValue("oid:" + h.MemberObjectId.ToString("D"), out var b) ? b : null;

    public static string NameOf(SeatHolderRow h, IReadOnlyDictionary<string, (string Name, string Upn)> names) => h.DisplayName
        ?? Find(h, names)?.Name
        ?? "Not in the directory";

    /// <summary>The sign-in: from the license read, else from the directory, else the directory object ID.</summary>
    public static string UpnOf(SeatHolderRow h, IReadOnlyDictionary<string, (string Name, string Upn)> names) => h.UserPrincipalName
        ?? Find(h, names)?.Upn
        ?? h.MemberObjectId.ToString("D");
}