using System.Text;
using Governance.Application.Commands;
using Governance.Application.Queries;
using Microsoft.AspNetCore.Mvc;

namespace Governance.Web.Pages.Registry;

public class CostCentersModel(RegistryQueries registry, RegistryCommands commands) : GovPage
{
    public Paged<CostCenterRow> Items { get; private set; } = new();
    public IReadOnlyList<Option> Owners { get; private set; } = Array.Empty<Option>();
    public bool CanEdit => Principal.Can(Permissions.RegistryFinance) || Principal.Can(Permissions.RegistryEdit);
    public bool IsFinance => Principal.Can(Permissions.RegistryFinance);

    public async Task OnGetAsync()
    {
        Items = await registry.CostCentersAsync(Query);
        Owners = await registry.FundingOwnerOptionsAsync();
    }

    public async Task<IActionResult> OnGetCsvAsync() => Csv((await registry.CostCentersAsync(Query.All())).Items, "cost-centers");

    public Task<IActionResult> OnPostSaveAsync(Guid? id, string code, string name, string? financeNode, int fundingOwnerId)
        => Act(() => commands.SaveCostCenterAsync(Idem, id, code, name, financeNode, fundingOwnerId, DateTime.UtcNow.Date), "Cost center saved. Finance validates it next.");

    public Task<IActionResult> OnPostValidateAsync(Guid id, bool valid) => Act(() => commands.ValidateCostCenterAsync(Idem, id, valid), valid ? "Validated." : "Validation removed.");

    public async Task<IActionResult> OnPostImportAsync(IFormFile? file)
    {
        if (file is null || file.Length == 0 || file.Length > 2 * 1024 * 1024) { FlashError = "Choose a CSV file up to 2 MB."; return RedirectToPage(); }
        using var reader = new StreamReader(file.OpenReadStream(), Encoding.UTF8);
        var csv = await reader.ReadToEndAsync();
        return await Act(() => commands.ImportCostCentersAsync(Idem, csv), s => "Import finished: " + s);
    }
}

public class ServicesModel(RegistryQueries registry) : GovPage
{
    public Paged<ServiceRow> Items { get; private set; } = new();
    public bool CanEdit => Principal.Can(Permissions.RegistryEdit);

    public async Task OnGetAsync() => Items = await registry.ServicesAsync(Query);
    public async Task<IActionResult> OnGetCsvAsync() => Csv((await registry.ServicesAsync(Query.All())).Items, "services");
}

public class ServiceEditModel(RegistryQueries registry, RegistryCommands commands) : GovPage
{
    [BindProperty(SupportsGet = true)] public Guid? Id { get; set; }
    [BindProperty] public ServiceForm Input { get; set; } = new();
    public IReadOnlyList<Option> People { get; private set; } = Array.Empty<Option>();

    public sealed class ServiceForm
    {
        public string Code { get; set; } = "";
        public string Name { get; set; } = "";
        public string? Harness { get; set; }
        public string RouteType { get; set; } = "PPAC_ENVIRONMENT";
        public string TenantAvailability { get; set; } = "GA";
        public string InstitutionalStatus { get; set; } = "NOT_APPROVED";
        public bool IsCustomerFacing { get; set; }
        public string Audiences { get; set; } = "EMPLOYEES_TEAM,EMPLOYEES_LOB";
        public string DataClasses { get; set; } = "PUBLIC,INTERNAL";
        public string AccessTiers { get; set; } = "Standard";
        public int? OperationalOwnerPersonId { get; set; }
        public DateTime? ReviewOn { get; set; }
        public string? Description { get; set; }
    }

    public async Task<IActionResult> OnGetAsync()
    {
        People = await registry.PeopleOptionsAsync();
        if (Id is Guid id)
        {
            var s = await registry.ServiceAsync(id);
            if (s is null) return NotFound();
            Input = new ServiceForm
            {
                Code = s.Code, Name = s.Name, Harness = s.Harness, RouteType = s.RouteType, TenantAvailability = s.TenantAvailability, InstitutionalStatus = s.InstitutionalStatus,
                IsCustomerFacing = s.IsCustomerFacing, Audiences = s.Audiences, DataClasses = s.DataClasses, AccessTiers = s.AccessTiers, OperationalOwnerPersonId = s.OperationalOwnerPersonId,
                ReviewOn = s.ReviewOn, Description = s.Description,
            };
        }
        return Page();
    }

    public Task<IActionResult> OnPostAsync()
        => Act(() => commands.SaveServiceAsync(Idem, Id, new ServiceInput(Input.Code, Input.Name, Input.Harness, Input.RouteType, Input.TenantAvailability, Input.InstitutionalStatus,
                Input.IsCustomerFacing, Input.Audiences, Input.DataClasses, Input.AccessTiers, Input.OperationalOwnerPersonId, Input.ReviewOn, Input.Description)),
            _ => "Service saved.", _ => Redirect("/Registry/Services"));
}

public class TemplatesModel(RegistryQueries registry, RegistryCommands commands) : GovPage
{
    public Paged<TemplateRow> Items { get; private set; } = new();
    public IReadOnlyList<Option> Services { get; private set; } = Array.Empty<Option>();
    public bool CanDraft => Principal.Can(Permissions.TemplateDraft) || Principal.Can(Permissions.RegistryEdit);

    public async Task OnGetAsync()
    {
        Items = await registry.TemplatesAsync(Query);
        Services = await registry.ServiceOptionsAsync();
    }

    public async Task<IActionResult> OnGetCsvAsync() => Csv((await registry.TemplatesAsync(Query.All())).Items, "control-templates", "ServiceName", "VersionNo", "Status", "Checks", "OperatorRoles", "ActivatedUtc");

    public Task<IActionResult> OnPostDraftAsync(int serviceId)
        => Act(() => commands.DraftTemplateAsync(Idem, serviceId), _ => "Draft created from the current version.", id => Redirect($"/Registry/Templates/{id}"));
}

public class TemplateModel(RegistryQueries registry, RegistryCommands commands) : GovPage
{
    [BindProperty(SupportsGet = true)] public Guid Id { get; set; }
    public TemplateRow T { get; private set; } = new();
    public IReadOnlyList<ControlCheckRow> Checks { get; private set; } = Array.Empty<ControlCheckRow>();
    public bool CanEdit => (Principal.Can(Permissions.TemplateDraft) || Principal.Can(Permissions.RegistryEdit)) && T.Status == "DRAFT";

    public async Task<IActionResult> OnGetAsync()
    {
        var (t, checks) = await registry.TemplateAsync(Id);
        if (t is null) return NotFound();
        T = t;
        Checks = checks;
        return Page();
    }

    public Task<IActionResult> OnPostSaveAsync(string settingsJson, string? permittedExceptions, string operatorRoles, string? notes, string? rowVer)
    {
        var checks = new List<CheckDefinitionInput>();
        var codes = Request.Form["checkCode"];
        for (var i = 0; i < codes.Count; i++)
        {
            var code = codes[i];
            if (string.IsNullOrWhiteSpace(code)) continue;
            checks.Add(new CheckDefinitionInput(code!, Request.Form["checkDescription"][i] ?? "", Request.Form["checkMethod"][i] ?? "ATTESTATION",
                Request.Form["checkKey"][i], Request.Form["checkDeny"].Contains(i.ToString()), Request.Form["checkCaveat"][i]));
        }
        return Act(() => commands.SaveTemplateAsync(Idem, Id, settingsJson, permittedExceptions, operatorRoles, notes, checks, string.IsNullOrEmpty(rowVer) ? null : Convert.FromBase64String(rowVer)),
            "Draft saved. The Platform Owner and Steering approve it to activate.");
    }

    public Task<IActionResult> OnPostApproveAsync(string asRole)
        => Act(() => commands.ApproveTemplateAsync(Idem, Id, asRole), "Approval recorded. The version activates after both approvals.");
}
