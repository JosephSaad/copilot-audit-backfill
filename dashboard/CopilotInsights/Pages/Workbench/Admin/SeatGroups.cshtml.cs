using Governance.Application;
using Governance.Application.Data;
using Governance.Application.Queries;
using Governance.Domain;
using Governance.Web.Infrastructure;
using Governance.Application.Commands;
using Governance.Application.Queries;
using Microsoft.AspNetCore.Mvc;

namespace Governance.Web.Pages.Admin;

/// <summary>Which Entra groups carry or describe Copilot seats, which SKUs count as Copilot, and the seat settings.</summary>
public class SeatGroupsModel(SeatQueries seats, SeatCommands commands, RegistryQueries registry, ReservationImport import) : GovPage
{
    /* "Start from Insights' reservations": the register's rows today, the funding owners, and who may confirm. */
    public IReadOnlyList<InsightsReservation> ImportRows { get; private set; } = Array.Empty<InsightsReservation>();
    public IReadOnlyList<ImportOwner> ImportOwners { get; private set; } = Array.Empty<ImportOwner>();
    public bool CanImport => Principal.Can(Permissions.RegistryFinance);
    /// <summary>Offered while the register has rows and a funding owner is still without a reservation.</summary>
    public bool ImportOffered => ImportRows.Count > 0 && ImportOwners.Any(o => !o.Reserved);

    [BindProperty(SupportsGet = true, Name = "edit")] public Guid? EditId { get; set; }
    public IReadOnlyList<SeatGroupRow> Groups { get; private set; } = Array.Empty<SeatGroupRow>();
    public IReadOnlyList<SeatSkuRow> Skus { get; private set; } = Array.Empty<SeatSkuRow>();
    public IReadOnlyList<SettingRow> Settings { get; private set; } = Array.Empty<SettingRow>();
    public IReadOnlyList<Option> Lobs { get; private set; } = Array.Empty<Option>();
    public SeatGroupRow? Editing { get; private set; }
    public bool CanEdit => Principal.Can(Permissions.AdminConfigure) || Principal.Can(Permissions.RegistryEdit);

    public static readonly IReadOnlyDictionary<string, (string Label, string Help)> Purposes = new Dictionary<string, (string, string)>
    {
        ["LICENSE"] = ("License group", "Group-based licensing assigns the Copilot add-on to its members. Shared by every LOB."),
        ["LOB_TRACKING"] = ("LOB tracking group", "Its members belong to one LOB: their seat is that LOB's and is charged to it."),
        ["EXCLUSION"] = ("Exclusion", "Its members should not hold a seat. A holder in it raises a finding once the purpose is confirmed."),
        ["CHAT_ONLY"] = ("Copilot Chat only", "People with Copilot Chat but not the paid license."),
        ["MANAGER"] = ("Managers or delegates", "Recorded for reference; no rule uses it."),
        ["OTHER"] = ("Other", "Recorded for reference; no rule uses it."),
    };

    public async Task OnGetAsync()
    {
        Groups = await seats.GroupsAsync();
        Skus = await seats.SkusAsync();
        Settings = await seats.SeatSettingRowsAsync();
        Lobs = await registry.FundingOwnerOptionsAsync();
        if (EditId is Guid g) Editing = Groups.FirstOrDefault(x => x.PublicId == g);
        try
        {
            ImportRows = await import.RowsAsync();
            if (ImportRows.Count > 0) ImportOwners = await import.OwnersAsync();
        }
        catch (Exception ex)
        {
            /* The rest of the page stands without the offer. */
            ShowErrorNow(Incident.Note(Log, ex, "reading Copilot Insights' reservation register"));
        }
    }

    /// <summary>
    /// Records each chosen row as its funding owner's first reservation, through the Workbench's own command (audited,
    /// allowed once a line of business). The rows are read again here, not taken from the form; a funding owner that
    /// already has a reservation is skipped and said.
    /// </summary>
    public async Task<IActionResult> OnPostImportAsync(List<string>? org, List<string>? owner)
    {
        if (!CanImport) { FlashError = "FinOps records a line of business's first seat reservation: someone with the FinOps role confirms this."; return RedirectToPage(); }
        var chosen = new Dictionary<string, Guid>(StringComparer.Ordinal);
        for (var i = 0; i < Math.Min(org?.Count ?? 0, owner?.Count ?? 0); i++)
            if (!string.IsNullOrWhiteSpace(org![i]) && Guid.TryParse(owner![i], out var g)) chosen[org[i]] = g;
        if (chosen.Count == 0) { FlashError = "Choose a funding owner for at least one row: rows left without one are not copied."; return RedirectToPage(); }
        var done = new List<string>();
        var notDone = new List<string>();
        try
        {
            var (record, skipped) = ReservationImport.Plan(await import.RowsAsync(), chosen, await import.OwnersAsync());
            notDone.AddRange(skipped);
            foreach (var item in record)
            {
                try
                {
                    await commands.RecordInitialReservationAsync(ReservationImport.Key(Idem, item.FundingOwner), item.FundingOwner, item.Seats, item.From, item.Notes);
                    done.Add($"{item.Name}: {item.Seats:#,0} seats from {item.From:yyyy-MM-dd}");
                }
                catch (DomainException ex) { notDone.Add($"{item.Name}: {ex.Message}"); }
            }
        }
        catch (Exception ex)
        {
            FlashError = Incident.Note(Log, ex, "starting the seat reservations from Copilot Insights' register") +
                         (done.Count > 0 ? $" Recorded before it stopped: {string.Join("; ", done)}." : "");
            return RedirectToPage();
        }
        if (done.Count > 0) Flash = $"First reservations recorded from Copilot Insights' register: {string.Join("; ", done)}. Later changes go through a seat reservation request.";
        if (notDone.Count > 0) FlashError = "Not recorded: " + string.Join(" ", notDone);
        return RedirectToPage();
    }

    public Task<IActionResult> OnPostSaveAsync(Guid? id, string displayName, string purpose, int? fundingOwnerId, string? oimRoleName, string source, bool purposeConfirmed, string? notes, Guid? entraGroupId)
        => Act(() => commands.SaveGroupAsync(Idem, id, new SeatGroupInput(displayName, purpose, fundingOwnerId, oimRoleName, source, purposeConfirmed, notes, entraGroupId)),
            id is null ? "Group added. The next license read finds it in Entra by name." : "Group mapping changed. The old mapping is kept in history.", () => RedirectToPage(new { edit = (Guid?)null }));

    public Task<IActionResult> OnPostEndAsync(Guid id) => Act(() => commands.EndGroupAsync(Idem, id), "Group mapping ended.");

    public Task<IActionResult> OnPostSkuAsync(Guid skuId, string partNumber, string? name, string kind, bool isActive, string? notes)
        => Act(() => commands.SaveSkuAsync(Idem, skuId, partNumber, name ?? partNumber, kind, isActive, notes), "SKU saved.");
}
