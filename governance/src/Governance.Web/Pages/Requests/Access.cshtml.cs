using Governance.Application.Queries;
using Microsoft.AspNetCore.Mvc;

namespace Governance.Web.Pages.Requests;

/// <summary>J1: routine access to an approved Microsoft 365 service through the LOB tier's Entra access package.</summary>
public class AccessModel(RegistryQueries registry, AccessQueries access, IConfiguration config) : GovPage
{
    [BindProperty(SupportsGet = true)] public int? FundingOwnerId { get; set; }
    public string? Basis { get; private set; }
    public string? FundingOwnerName { get; private set; }
    public IReadOnlyList<Option> FundingOwners { get; private set; } = Array.Empty<Option>();
    public IReadOnlyList<AccessOption> Options { get; private set; } = Array.Empty<AccessOption>();
    public IReadOnlyList<EntraDecision> Decisions { get; private set; } = Array.Empty<EntraDecision>();
    public string TenantDomain => config["Workbench:TenantDomain"] ?? "";

    public string MyAccessLink(Guid accessPackageId)
        => string.IsNullOrEmpty(TenantDomain) ? "https://myaccess.microsoft.com/" : $"https://myaccess.microsoft.com/@{TenantDomain}#/access-packages/{accessPackageId}";

    public async Task OnGetAsync()
    {
        FundingOwners = await registry.FundingOwnerOptionsAsync(activeOnly: true);
        if (FundingOwnerId is null)
        {
            var (fo, basis) = await registry.AttributeAsync(Principal.PersonId);
            FundingOwnerId = fo;
            Basis = basis;
        }
        FundingOwnerName = FundingOwners.FirstOrDefault(o => o.Value == FundingOwnerId?.ToString())?.Text;
        if (FundingOwnerId is int id) Options = await access.OptionsAsync(id);
        Decisions = await access.MyEntraDecisionsAsync();
    }
}
