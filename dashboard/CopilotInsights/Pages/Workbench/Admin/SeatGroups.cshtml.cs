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
public class SeatGroupsModel(SeatQueries seats, SeatCommands commands, RegistryQueries registry) : GovPage
{
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
    }

    public Task<IActionResult> OnPostSaveAsync(Guid? id, string displayName, string purpose, int? fundingOwnerId, string? oimRoleName, string source, bool purposeConfirmed, string? notes, Guid? entraGroupId)
        => Act(() => commands.SaveGroupAsync(Idem, id, new SeatGroupInput(displayName, purpose, fundingOwnerId, oimRoleName, source, purposeConfirmed, notes, entraGroupId)),
            id is null ? "Group added. The next license read finds it in Entra by name." : "Group mapping changed. The old mapping is kept in history.", () => RedirectToPage(new { edit = (Guid?)null }));

    public Task<IActionResult> OnPostEndAsync(Guid id) => Act(() => commands.EndGroupAsync(Idem, id), "Group mapping ended.");

    public Task<IActionResult> OnPostSkuAsync(Guid skuId, string partNumber, string? name, string kind, bool isActive, string? notes)
        => Act(() => commands.SaveSkuAsync(Idem, skuId, partNumber, name ?? partNumber, kind, isActive, notes), "SKU saved.");
}
