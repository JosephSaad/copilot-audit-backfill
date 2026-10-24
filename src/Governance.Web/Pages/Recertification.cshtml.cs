using Governance.Application.Commands;
using Governance.Application.Queries;
using Microsoft.AspNetCore.Mvc;

namespace Governance.Web.Pages;

/// <summary>J6 and I1: owners recertify what they own; the Platform Owner runs campaigns.</summary>
public class RecertificationModel(OperationsQueries ops, OperationsCommands commands) : GovPage
{
    [BindProperty(SupportsGet = true, Name = "campaign")] public int? CampaignId { get; set; }
    [BindProperty(SupportsGet = true, Name = "all")] public bool All { get; set; }
    public IReadOnlyList<CampaignRow> Campaigns { get; private set; } = Array.Empty<CampaignRow>();
    public Paged<CampaignItemRow> Items { get; private set; } = new();
    public bool IsPlatformOwner => Principal.HasCentral(Roles.PlatformOwner);

    public async Task OnGetAsync()
    {
        Campaigns = await ops.CampaignsAsync();
        Items = await ops.CampaignItemsAsync(Query, CampaignId, All && (IsPlatformOwner || Principal.IsCentral) ? null : Principal.PersonId);
        if (Items.Total == 0 && !All && Principal.FundingOwners.Count > 0)
            Items = await ops.CampaignItemsAsync(Query, CampaignId, null);   // business owners see their funding owners' items (RLS)
    }

    public async Task<IActionResult> OnGetCsvAsync() => Csv((await ops.CampaignItemsAsync(Query.All(), CampaignId, null)).Items, "recertification");

    public Task<IActionResult> OnPostCreateAsync(string name, DateTime startsOn, DateTime dueOn)
        => Act(() => commands.CreateCampaignAsync(Idem, name, startsOn, dueOn), n => $"Campaign opened with {n} items. Owners are notified (N14).");

    public Task<IActionResult> OnPostCompleteAsync(int item, string outcome, string evidence, IFormFile? file)
        => Act(() => commands.CompleteCampaignItemAsync(Idem, item, outcome, evidence, Upload(file)),
            outcome == "RETIRE" ? "Recorded. Start the retirement journey (J7) for this workload." : "Recertified. The next review is set for a year from today.");
}
