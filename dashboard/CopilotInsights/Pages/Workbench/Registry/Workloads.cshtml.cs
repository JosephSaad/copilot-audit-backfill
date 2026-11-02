using Governance.Application;
using Governance.Application.Data;
using Governance.Application.Queries;
using Governance.Domain;
using Governance.Web.Infrastructure;
using Governance.Application.Commands;
using Governance.Application.Queries;
using Microsoft.AspNetCore.Mvc;

namespace Governance.Web.Pages.Registry;

public class WorkloadsModel(RegistryQueries registry) : GovPage
{
    [BindProperty(SupportsGet = true)] public string? State { get; set; }
    public Paged<WorkloadListItem> Items { get; private set; } = new();

    public async Task OnGetAsync() => Items = await registry.WorkloadsAsync(Query, State);
    public async Task<IActionResult> OnGetCsvAsync() => Csv((await registry.WorkloadsAsync(Query.All(), State)).Items, "workloads");
}

public class WorkloadModel(RegistryQueries registry, RegistryCommands commands) : GovPage
{
    [BindProperty(SupportsGet = true)] public Guid Id { get; set; }
    [BindProperty(SupportsGet = true)] public DateTime? AsOf { get; set; }
    public WorkloadDetail? D { get; private set; }
    public IReadOnlyList<Option> People { get; private set; } = Array.Empty<Option>();
    public bool CanEdit => D is not null && (Principal.Can(Permissions.WorkloadEdit, D.Workload.FundingOwnerId) || D.Workload.ProductOwnerPersonId == Principal.PersonId || Principal.Can(Permissions.RegistryEdit));
    public bool CanBind => Principal.Can(Permissions.Verify) || Principal.Can(Permissions.RegistryEdit);
    /// <summary>Only a first baseline is recorded here; changes go through a capacity or limit request (J4).</summary>
    public bool CanPosture => Principal.Can(Permissions.RegistryEdit) && D is not null && !D.Posture.Any(p => p.EffectiveTo is null);

    public async Task<IActionResult> OnGetAsync()
    {
        // The as-of view reads system time: what the register said at the end of that day (UTC).
        DateTime? asOfUtc = AsOf is DateTime a ? DateTime.SpecifyKind(a.Date.AddDays(1).AddTicks(-1), DateTimeKind.Utc) : null;
        D = await registry.WorkloadAsync(Id, asOfUtc);
        if (D is null) return AsOf is null ? NotFound() : Page();
        People = await registry.PeopleOptionsAsync();
        return Page();
    }

    public async Task<IActionResult> OnGetCsvAsync()
    {
        var d = await registry.WorkloadAsync(Id, null);
        return d is null ? NotFound() : Csv(d.Decisions, "workload-decisions");
    }

    public Task<IActionResult> OnPostSaveAsync(string name, int? productOwnerPersonId, int? sponsorPersonId, string? supportContact, string? complianceCaseRef)
        => Act(() => commands.SaveWorkloadBasicsAsync(Idem, Id, name, productOwnerPersonId, sponsorPersonId, supportContact, complianceCaseRef), "Saved.");

    public Task<IActionResult> OnPostBindAsync(string objectType, string externalId, string? displayName)
        => Act(() => commands.AddBindingAsync(Idem, Id, objectType, externalId, displayName), "Binding added. Inventory and licensing reads now compare it with the register.");

    public Task<IActionResult> OnPostUnbindAsync(int bindingId) => Act(() => commands.EndBindingAsync(Idem, bindingId), "Binding ended.");

    public Task<IActionResult> OnPostPostureAsync(string environmentId, decimal allocation, bool tenantPool, bool payg, string? billingPolicyId, decimal? agentLimit, bool groupRuleLock)
        => Act(() => commands.SavePostureAsync(Idem, Id, environmentId, allocation, tenantPool, payg, billingPolicyId, agentLimit, groupRuleLock), "Baseline posture recorded against the workload's current approval. The drift job compares it with the next licensing read; later changes go through a capacity or limit request (J4).");
}

public class EnvelopesModel(RegistryQueries registry, RegistryCommands commands) : GovPage
{
    public Paged<EnvelopeRow> Items { get; private set; } = new();
    public IReadOnlyList<Option> Owners { get; private set; } = Array.Empty<Option>();
    public IReadOnlyList<Option> Services { get; private set; } = Array.Empty<Option>();
    public bool CanManage => Principal.Can(Permissions.EnvelopeManage);
    public bool CanApprove(int fundingOwnerId) => Principal.Roles.Any(r => (r.RoleCode is Roles.LobBusinessOwner or Roles.SharedServiceOwner) && r.ScopeId == fundingOwnerId);

    public async Task OnGetAsync()
    {
        Items = await registry.EnvelopesAsync(Query);
        if (CanManage)
        {
            Owners = await registry.FundingOwnerOptionsAsync();
            Services = await registry.ServiceOptionsAsync(approvedOnly: true);
        }
    }

    public async Task<IActionResult> OnGetCsvAsync() => Csv((await registry.EnvelopesAsync(Query.All())).Items, "envelopes");

    public Task<IActionResult> OnPostCreateAsync(int fundingOwnerId, int serviceId, DateTime start, DateTime end, decimal amount, string unit)
        => Act(() => commands.CreateEnvelopeAsync(Idem, fundingOwnerId, serviceId, start, end, amount, unit), "Envelope drafted. The funding owner's business owner approves it.");

    public Task<IActionResult> OnPostApproveAsync(Guid envelope, bool approve, string? reason)
        => Act(() => commands.ApproveEnvelopeAsync(Idem, envelope, approve, reason), approve ? "Approved: the envelope is active." : "Declined.");

    public Task<IActionResult> OnPostCloseAsync(Guid envelope) => Act(() => commands.CloseEnvelopeAsync(Idem, envelope), "Envelope closed.");
}

public class CommercialModel(RegistryQueries registry, RegistryCommands commands) : GovPage
{
    [BindProperty(SupportsGet = true, Name = "edit")] public Guid? EditId { get; set; }
    public Paged<CommercialRow> Items { get; private set; } = new();
    public IReadOnlyList<Option> People { get; private set; } = Array.Empty<Option>();
    public CommercialRow? Editing { get; private set; }
    public bool CanEdit => Principal.Can(Permissions.RegistryFinance);

    public async Task<IActionResult> OnGetAsync()
    {
        if (!CanEdit && !Principal.Can(Permissions.CommercialView) && !Principal.IsCentral) return Forbid();
        Items = await registry.CommercialAsync(Query);
        People = await registry.PeopleOptionsAsync();
        if (EditId is Guid g) Editing = (await registry.CommercialAsync(Query.All())).Items.FirstOrDefault(x => x.PublicId == g);
        return Page();
    }

    public async Task<IActionResult> OnGetCsvAsync()
    {
        if (!CanEdit && !Principal.Can(Permissions.CommercialView) && !Principal.IsCentral) return Forbid();
        return Csv((await registry.CommercialAsync(Query.All())).Items, "commercial-inventory");
    }

    public Task<IActionResult> OnPostSaveAsync(Guid? id, string itemType, string name, decimal? quantity, string? unit, DateTime? startOn, DateTime? endOn, DateTime? renewalOn,
        bool autoRenew, string? billingScope, string? contractRef, int? purchaseAuthorityPersonId, bool reconciled, string? notes, string? rowVer)
        => Act(() => commands.SaveCommercialAsync(Idem, id, new CommercialInput(itemType, name, quantity, string.IsNullOrEmpty(unit) ? null : unit, startOn, endOn, renewalOn, autoRenew, billingScope,
                contractRef, purchaseAuthorityPersonId, reconciled, notes), string.IsNullOrEmpty(rowVer) ? null : Convert.FromBase64String(rowVer)),
            "Saved. Renewal reminders go to the Commercial Authority, FinOps and Sourcing 90, 60 and 30 days before (N18).", () => Redirect("/workbench/Registry/Commercial"));
}

public class DiscoveryModel(RegistryQueries registry, RegistryCommands commands) : GovPage
{
    public Paged<WorkloadListItem> Discovered { get; private set; } = new();
    public Paged<FindingRow> Unregistered { get; private set; } = new();
    public IReadOnlyList<Option> Owners { get; private set; } = Array.Empty<Option>();
    public IReadOnlyList<Option> People { get; private set; } = Array.Empty<Option>();
    public bool CanClaim => Principal.Can(Permissions.RegistryClaim) || Principal.Can(Permissions.RegistryEdit);

    /// <summary>Findings are read across every funding owner (system mode): central roles or people who claim only.</summary>
    private bool CanSee => Principal.IsCentral || CanClaim;

    public async Task<IActionResult> OnGetAsync()
    {
        if (!CanSee) return Forbid();
        Discovered = await registry.WorkloadsAsync(Query, WorkloadStates.Discovered);
        Unregistered = await registry.FindingsAsync(Query, "UNREGISTERED", openOnly: true);
        Owners = await registry.FundingOwnerOptionsAsync();
        People = await registry.PeopleOptionsAsync();
        return Page();
    }

    public async Task<IActionResult> OnGetCsvAsync()
        => CanSee ? Csv((await registry.FindingsAsync(Query.All(), "UNREGISTERED", true)).Items, "discovery") : Forbid();

    public Task<IActionResult> OnPostClaimAsync(Guid? workload, Guid? finding, int fundingOwnerId, int? productOwnerPersonId, string? name)
        => Act(() => commands.ClaimAsync(Idem, workload, finding, fundingOwnerId, productOwnerPersonId, name), _ => "Claimed and registered. Classify it through a request when it needs approval.",
            id => id is null ? RedirectToPage() : Redirect($"/workbench/Registry/Workloads/{id}"));
}
