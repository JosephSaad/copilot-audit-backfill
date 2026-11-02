using Governance.Application.Commands;
using Governance.Application.Queries;
using Microsoft.AspNetCore.Mvc;

namespace Governance.Web.Pages.Events;

/// <summary>J5: anyone reports a consumption problem; Operations triages within the SLA for its severity.</summary>
public class ReportModel(RegistryQueries registry, OperationsCommands ops) : GovPage
{
    public IReadOnlyList<Option> FundingOwners { get; private set; } = Array.Empty<Option>();
    public IReadOnlyList<Option> Workloads { get; private set; } = Array.Empty<Option>();
    public int? DefaultFundingOwner { get; private set; }

    public async Task OnGetAsync()
    {
        FundingOwners = await registry.FundingOwnerOptionsAsync();
        Workloads = await registry.WorkloadOptionsAsync();
        DefaultFundingOwner = (await registry.AttributeAsync(Principal.PersonId)).FundingOwnerId;
    }

    public Task<IActionResult> OnPostAsync(int fundingOwnerId, Guid? workloadId, string eventClass, string severity, string title, string description, bool criticalService)
        => Act(() => ops.ReportEventAsync(Idem, fundingOwnerId, workloadId, eventClass, severity, title, description, criticalService),
            id => severity == "NORMAL" ? "Reported. Operations acknowledges within one business day." : "Reported. Operations is notified now and an ITSM incident is opened.",
            id => Principal.AppRoles.Contains(AppRoles.Central) && id is not null ? Redirect($"/Operations/Events/{id}") : RedirectToPage("/Start"));
}
