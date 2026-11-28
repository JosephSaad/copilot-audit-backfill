using Governance.Application;
using Governance.Application.Data;
using Governance.Application.Queries;
using Governance.Domain;
using Governance.Web.Infrastructure;
using Governance.Application.Commands;
using Governance.Application.Queries;
using Microsoft.AspNetCore.Mvc;

namespace Governance.Web.Pages.Registry;

public class FundingOwnersModel(RegistryQueries registry) : GovPage
{
    public Paged<FundingOwnerListItem> Items { get; private set; } = new();
    public bool CanEdit => Principal.Can(Permissions.RegistryFinance) || Principal.Can(Permissions.RegistryEdit);

    public async Task OnGetAsync() => Items = await registry.FundingOwnersAsync(Query);
    public async Task<IActionResult> OnGetCsvAsync() => Csv((await registry.FundingOwnersAsync(Query.All())).Items, "funding-owners");
}

public class FundingOwnerModel(RegistryQueries registry, RegistryCommands commands, AdminQueries admin, SeatCommands seatCommands, SeatQueries seatQueries) : GovPage
{
    /// <summary>Its active tracking group, for the link to Seat groups.</summary>
    public Guid? TrackingGroupId { get; private set; }
    [BindProperty(SupportsGet = true)] public Guid Id { get; set; }
    public FundingOwnerDetail D { get; private set; } = new();
    public IReadOnlyList<Option> People { get; private set; } = Array.Empty<Option>();
    public IReadOnlyList<AdminQueries.RoleOption> LobRoles { get; private set; } = Array.Empty<AdminQueries.RoleOption>();
    public int Fo => D.Owner.FundingOwnerId;
    public bool CanFinance => Principal.Can(Permissions.RegistryFinance) || Principal.Can(Permissions.RegistryEdit);
    public bool IsBusinessOwner => Principal.Roles.Any(r => (r.RoleCode is Roles.LobBusinessOwner or Roles.SharedServiceOwner) && r.ScopeId == Fo);
    public bool CanAssignLob => Principal.Can(Permissions.RoleAssignLob, Fo);

    /// <summary>
    /// The detail (envelopes, delegations, role holders, attribution rules) is read in system mode, so access is checked
    /// here: central roles, or a role in this funding owner that grants fundingowner.view (section 10.1).
    /// </summary>
    public static bool CanView(Governance.Web.Infrastructure.WebPrincipal p, int fundingOwnerId)
        => p.IsCentral || p.Can(Permissions.FundingOwnerView, fundingOwnerId);

    public async Task<IActionResult> OnGetAsync()
    {
        var d = await registry.FundingOwnerAsync(Id);
        if (d is null) return NotFound();
        if (!CanView(Principal, d.Owner.FundingOwnerId)) return Forbid();
        D = d;
        People = await registry.PeopleOptionsAsync();
        LobRoles = (await admin.RolesAsync()).Where(r => r.IsLob && r.RoleCode is not (Roles.LobBusinessOwner or Roles.SharedServiceOwner)
            /* Copilot seats only: the seat admin is the one line-of-business role that still does something. */
            && (!d.SeatsOnly || r.RoleCode == SeatAccess.LobSeatAdmin)).ToList();
        if (SeatsOnly) TrackingGroupId = await seatQueries.TrackingGroupOfAsync(D.Owner.FundingOwnerId);
        return Page();
    }

    public async Task<IActionResult> OnPostActivateAsync()
    {
        var d = await registry.FundingOwnerAsync(Id);
        if (d is null) return NotFound();
        return await Act(() => commands.ActivateFundingOwnerAsync(Idem, Id, d.Checklist), "Active. Its delegated approvers can approve routine requests within its envelopes.");
    }

    public Task<IActionResult> OnPostFlagAsync(string flag, bool value) => Act(() => commands.SetFundingOwnerFlagAsync(Idem, Id, flag, value), "Saved.");
    public Task<IActionResult> OnPostCentralAsync(bool central)
        => Act(() => seatCommands.SetCentralCostAsync(Idem, Id, central), central
            ? "Now a central cost: its seats show on their own row, charged to no line of business, and the hierarchy audit leaves them out unless its tracking group has an expected leader."
            : "Charged to this line of business again.");

    public Task<IActionResult> OnPostStatusAsync(string status, string reason) => Act(() => commands.SetFundingOwnerStatusAsync(Idem, Id, status, reason), $"Status set to {Ui.Words(status)}.");
    public Task<IActionResult> OnPostRuleAsync(string? division, string? costCenter, string? department, int priority)
        => Act(() => commands.AddAttributionRuleAsync(Idem, Id, division, costCenter, department, priority), "Rule added. Test it against the directory, then mark attribution tested.");
    public Task<IActionResult> OnPostEndRuleAsync(int ruleId) => Act(() => commands.EndAttributionRuleAsync(Idem, ruleId), "Rule ended.");
    public async Task<IActionResult> OnPostRoleAsync(int personId, string roleCode, DateTime? effectiveTo)
    {
        var d = await registry.FundingOwnerAsync(Id);
        return await Act(() => commands.AssignRoleAsync(Idem, personId, roleCode, d?.Owner.FundingOwnerId, effectiveTo), "Role assigned.");
    }
    public Task<IActionResult> OnPostEndRoleAsync(Guid role) => Act(() => commands.EndRoleAsync(Idem, role), "Role ended from today.");
    public Task<IActionResult> OnPostDelegateAsync(int delegatePersonId, decimal? maxCredits, DateTime from, DateTime? to, List<int>? envelopes, string? notes)
        => Act(() => commands.AddDelegationAsync(Idem, Id, delegatePersonId, maxCredits, from, to, envelopes ?? new List<int>(), notes), "Delegation granted. It is checked at every decision (SoD7).");
    public Task<IActionResult> OnPostRevokeAsync(Guid delegation) => Act(() => commands.RevokeDelegationAsync(Idem, delegation), "Delegation revoked.");
    public Task<IActionResult> OnPostApproveEnvelopeAsync(Guid envelope, bool approve, string? reason)
        => Act(() => commands.ApproveEnvelopeAsync(Idem, envelope, approve, reason), approve ? "Envelope approved and active." : "Envelope declined.");
}

public class FundingOwnerEditModel(RegistryQueries registry, RegistryCommands commands) : GovPage
{
    [BindProperty(SupportsGet = true)] public Guid? Id { get; set; }
    [BindProperty] public FundingOwnerForm Input { get; set; } = new();
    public IReadOnlyList<Option> People { get; private set; } = Array.Empty<Option>();
    public IReadOnlyList<Option> Owners { get; private set; } = Array.Empty<Option>();

    public sealed class FundingOwnerForm
    {
        public string Code { get; set; } = "";
        public string Name { get; set; } = "";
        public string OwnerType { get; set; } = "LOB";
        public int? ParentFundingOwnerId { get; set; }
        public int? BusinessOwnerPersonId { get; set; }
        public int? FinancePartnerPersonId { get; set; }
        public string? FinanceNode { get; set; }
        public bool IsCriticalShared { get; set; }
        public DateTime EffectiveFrom { get; set; } = DateTime.UtcNow.Date;
        public string? RowVer { get; set; }
    }

    public async Task<IActionResult> OnGetAsync()
    {
        if (!Principal.Can(Permissions.RegistryFinance) && !Principal.Can(Permissions.RegistryEdit)) return Forbid();
        People = await registry.PeopleOptionsAsync();
        Owners = await registry.FundingOwnerOptionsAsync();
        if (Id is Guid id)
        {
            var d = await registry.FundingOwnerAsync(id);
            if (d is null) return NotFound();
            var o = d.Owner;
            Input = new FundingOwnerForm
            {
                Code = o.Code, Name = o.Name, OwnerType = o.OwnerType, ParentFundingOwnerId = o.ParentFundingOwnerId, BusinessOwnerPersonId = o.BusinessOwnerPersonId,
                FinancePartnerPersonId = o.FinancePartnerPersonId, FinanceNode = o.FinanceNode, IsCriticalShared = o.IsCriticalShared, EffectiveFrom = o.EffectiveFrom,
                RowVer = o.RowVer is null ? null : Convert.ToBase64String(o.RowVer),
            };
        }
        return Page();
    }

    public Task<IActionResult> OnPostAsync()
        => Act(() => commands.SaveFundingOwnerAsync(Idem, Id, new FundingOwnerInput(Input.Code, Input.Name, Input.OwnerType, Input.ParentFundingOwnerId, Input.BusinessOwnerPersonId,
                Input.FinancePartnerPersonId, Input.FinanceNode, Input.IsCriticalShared, Input.EffectiveFrom), string.IsNullOrEmpty(Input.RowVer) ? null : Convert.FromBase64String(Input.RowVer)),
            id => SeatsOnly ? (Id is null ? "Added and active. Give it its AD tracking group on Seat groups next." : "Saved.")
                : "Saved. The business owner is asked to accept the role.", id => Redirect($"/workbench/Registry/FundingOwners/{id}"));
}
