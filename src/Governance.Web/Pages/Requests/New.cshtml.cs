using Dapper;
using Governance.Application.Commands;
using Governance.Application.Data;
using Governance.Application.Queries;
using Governance.Application.Requests;
using Governance.Domain;
using Governance.Web.Infrastructure;
using Microsoft.AspNetCore.Mvc;

namespace Governance.Web.Pages.Requests;

public class NewModel(RegistryQueries registry, WorkQueries work, RequestCommands commands, Db db) : GovPage
{
    [BindProperty(SupportsGet = true)] public string Journey { get; set; } = "";
    [BindProperty(SupportsGet = true)] public Guid? Id { get; set; }
    [BindProperty] public RequestInput Input { get; set; } = new();
    [BindProperty] public string? RowVerBase64 { get; set; }

    public JourneyTile? Tile { get; private set; }
    public bool IntakeOpen { get; private set; }
    public IReadOnlyList<Option> FundingOwners { get; private set; } = Array.Empty<Option>();
    public IReadOnlyList<Option> Services { get; private set; } = Array.Empty<Option>();
    public IReadOnlyList<Option> People { get; private set; } = Array.Empty<Option>();
    public IReadOnlyList<Option> Workloads { get; private set; } = Array.Empty<Option>();
    public IReadOnlyList<Option> EnvironmentClasses { get; private set; } = Array.Empty<Option>();
    public IReadOnlyDictionary<string, (string Title, string Guidance)> Caveats { get; private set; } = new Dictionary<string, (string, string)>();
    public string? AttributionBasis { get; private set; }
    public string? State { get; private set; }
    public string? ReturnReason { get; private set; }

    public static readonly IReadOnlyList<(string Code, string Label)> MeterChoices = new[]
    {
        ("FOUNDRY", "Azure AI Foundry models"), ("FABRIC", "Microsoft Fabric"), ("CONNECTOR", "Premium connectors"), ("POWER_AUTOMATE", "Power Automate flows"),
        ("GITHUB_ACTIONS", "GitHub Actions or Codespaces"), ("THIRD_PARTY", "Third-party APIs"),
    };

    public string Caveat(string code) => Caveats.TryGetValue(code, out var c) ? $"{code} {c.Title}: {c.Guidance}" : code;

    public async Task<IActionResult> OnGetAsync()
    {
        if (!Journeys.WithStages.Contains(Journey)) return Journey switch
        {
            Journeys.RoutineAccess => Redirect("/Requests/Access"),
            Journeys.ConsumptionEvent => Redirect("/Events/Report"),
            Journeys.Recertification => Redirect("/Recertification"),
            _ => NotFound(),
        };
        await LoadAsync();
        if (Id is Guid id)
        {
            var d = await work.RequestAsync(id);
            if (d is null || d.Request.RequesterPersonId != Principal.PersonId) return NotFound();
            var p = RequestPayload.FromJson(d.Request.PayloadJson);
            Input = RequestInput.From(d.Request, p, d.Workload?.PublicId);
            RowVerBase64 = d.Request.RowVer is null ? null : Convert.ToBase64String(d.Request.RowVer);
            State = d.Request.State;
            ReturnReason = d.Timeline.FirstOrDefault(t => t.ToState is "RETURNED" or "DECLINED")?.Text
                           ?? d.Decisions.FirstOrDefault(x => x.Outcome is "DECLINED" or "HOLD")?.RequiredEvidenceText;
        }
        else
        {
            var (fo, basis) = await registry.AttributeAsync(Principal.PersonId);
            Input.FundingOwnerId = fo;
            AttributionBasis = fo is null ? null : basis;
            Input.Meters = new List<string>();
        }
        return Page();
    }

    public Task<IActionResult> OnPostSaveAsync() => SaveAsync(submit: false);
    public Task<IActionResult> OnPostSubmitAsync() => SaveAsync(submit: true);

    private async Task<IActionResult> SaveAsync(bool submit)
    {
        if (!Journeys.WithStages.Contains(Journey)) return NotFound();
        if (Input.FundingOwnerId is null)
        {
            FlashError = "Choose the funding owner.";
            return RedirectToPage(new { Journey, Id });
        }
        var payload = Input.ToPayload();
        var rowVer = string.IsNullOrEmpty(RowVerBase64) ? null : Convert.FromBase64String(RowVerBase64);
        try
        {
            Guid publicId;
            if (Id is Guid existing)
            {
                await commands.UpdateDraftAsync(Idem, existing, Input.Title ?? "", Input.FundingOwnerId.Value, Input.ServiceId, Input.SponsorPersonId, Input.WorkloadPublicId, payload, rowVer);
                publicId = existing;
            }
            else
            {
                var created = await commands.CreateDraftAsync(Idem, Journey, Input.Title ?? "", Input.FundingOwnerId.Value, Input.ServiceId, Input.SponsorPersonId, Input.WorkloadPublicId, payload);
                publicId = Guid.Parse(created!);
            }
            if (submit)
            {
                await commands.SubmitAsync(Guid.NewGuid(), publicId);
                Flash = "Submitted. The intake team checks completeness and sponsorship next; you get an email at every step.";
            }
            else Flash = "Draft saved. Submit it when it is ready.";
            return Redirect($"/Requests/{publicId}");
        }
        catch (DomainException ex) { FlashError = ex.Message; }
        catch (ConcurrencyException ex) { FlashError = ex.Message; }
        catch (Exception ex) { FlashError = Incident.Note(Log, ex, "saving the request"); }
        await LoadAsync();
        return Page();
    }

    private async Task LoadAsync()
    {
        Tile = (await work.JourneysAsync()).FirstOrDefault(t => t.JourneyCode == Journey);
        IntakeOpen = await work.IntakeOpenAsync();
        FundingOwners = await registry.FundingOwnerOptionsAsync(activeOnly: false);
        Services = await registry.ServiceOptionsAsync(approvedOnly: Journey != Journeys.Exception);
        People = await registry.PeopleOptionsAsync();
        Workloads = await registry.WorkloadOptionsAsync();
        EnvironmentClasses = await registry.EnvironmentClassOptionsAsync();
        Caveats = await registry.CaveatsAsync();
        _ = db;
    }
}

public sealed class RequestInput
{
    public string? Title { get; set; }
    public int? FundingOwnerId { get; set; }
    public int? ServiceId { get; set; }
    public int? SponsorPersonId { get; set; }
    public Guid? WorkloadPublicId { get; set; }
    public string? Purpose { get; set; }
    public string? BusinessOutcome { get; set; }
    public bool MaterialChange { get; set; }
    public string? WorkloadName { get; set; }
    public string? WorkloadType { get; set; }
    public string? Audience { get; set; }
    public string? DataClass { get; set; }
    public string? ActionScope { get; set; }
    public string? RequestedLifecycleStage { get; set; }
    public string? TriggerType { get; set; }
    public string? Tier { get; set; }
    public string? EnvironmentClassCode { get; set; }
    public decimal? EstimateCreditsPerMonth { get; set; }
    public int? DurationMonths { get; set; }
    public bool CustomerFacing { get; set; }
    public bool NewConnectorOrIntegration { get; set; }
    public bool NewBillingScope { get; set; }
    public bool UsesGitHubHarness { get; set; }
    public List<string> Meters { get; set; } = new();
    public string? SupportContact { get; set; }
    public string? RequestedDeviation { get; set; }
    public string? ProposedCompensatingControls { get; set; }
    public DateTime? RequestedExpiry { get; set; }
    public string? ChangeKind { get; set; }
    public string? EnvironmentId { get; set; }
    public decimal? CurrentValue { get; set; }
    public decimal? RequestedValue { get; set; }
    public bool RequestTenantPool { get; set; }
    public string? RetirementReason { get; set; }
    public string? RetirementPlan { get; set; }
    public string? ReplacementWorkload { get; set; }
    public string? CommercialKind { get; set; }
    public decimal? Quantity { get; set; }
    public string? Unit { get; set; }
    public string? Rationale { get; set; }

    public RequestPayload ToPayload() => new()
    {
        Purpose = Purpose, BusinessOutcome = BusinessOutcome, MaterialChange = MaterialChange, WorkloadName = WorkloadName, WorkloadType = WorkloadType,
        Audience = Audience, DataClass = DataClass, ActionScope = ActionScope, RequestedLifecycleStage = RequestedLifecycleStage, TriggerType = TriggerType, Tier = Tier,
        EnvironmentClassCode = EnvironmentClassCode, EstimateCreditsPerMonth = EstimateCreditsPerMonth, DurationMonths = DurationMonths, CustomerFacing = CustomerFacing,
        NewConnectorOrIntegration = NewConnectorOrIntegration, NewBillingScope = NewBillingScope, UsesGitHubHarness = UsesGitHubHarness, Meters = Meters ?? new(),
        SupportContact = SupportContact, RequestedDeviation = RequestedDeviation, ProposedCompensatingControls = ProposedCompensatingControls, RequestedExpiry = RequestedExpiry,
        ChangeKind = ChangeKind, EnvironmentId = EnvironmentId, CurrentValue = CurrentValue, RequestedValue = RequestedValue, RequestTenantPool = RequestTenantPool,
        RetirementReason = RetirementReason, RetirementPlan = RetirementPlan, ReplacementWorkload = ReplacementWorkload, CommercialKind = CommercialKind, Quantity = Quantity,
        Unit = Unit, Rationale = Rationale,
    };

    public static RequestInput From(RequestRow r, RequestPayload p, Guid? workload) => new()
    {
        Title = r.Title, FundingOwnerId = r.FundingOwnerId, ServiceId = r.ServiceId, SponsorPersonId = r.SponsorPersonId, WorkloadPublicId = workload ?? p.ExistingWorkloadPublicId,
        Purpose = p.Purpose, BusinessOutcome = p.BusinessOutcome, MaterialChange = p.MaterialChange, WorkloadName = p.WorkloadName, WorkloadType = p.WorkloadType,
        Audience = p.Audience, DataClass = p.DataClass, ActionScope = p.ActionScope, RequestedLifecycleStage = p.RequestedLifecycleStage, TriggerType = p.TriggerType, Tier = p.Tier,
        EnvironmentClassCode = p.EnvironmentClassCode, EstimateCreditsPerMonth = p.EstimateCreditsPerMonth, DurationMonths = p.DurationMonths, CustomerFacing = p.CustomerFacing,
        NewConnectorOrIntegration = p.NewConnectorOrIntegration, NewBillingScope = p.NewBillingScope, UsesGitHubHarness = p.UsesGitHubHarness, Meters = p.Meters,
        SupportContact = p.SupportContact, RequestedDeviation = p.RequestedDeviation, ProposedCompensatingControls = p.ProposedCompensatingControls, RequestedExpiry = p.RequestedExpiry,
        ChangeKind = p.ChangeKind, EnvironmentId = p.EnvironmentId, CurrentValue = p.CurrentValue, RequestedValue = p.RequestedValue, RequestTenantPool = p.RequestTenantPool,
        RetirementReason = p.RetirementReason, RetirementPlan = p.RetirementPlan, ReplacementWorkload = p.ReplacementWorkload, CommercialKind = p.CommercialKind, Quantity = p.Quantity,
        Unit = p.Unit, Rationale = p.Rationale,
    };
}
