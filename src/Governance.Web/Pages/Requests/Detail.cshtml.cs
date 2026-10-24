using Governance.Application.Commands;
using Governance.Application.Queries;
using Governance.Application.Requests;
using Governance.Application.Workflow;
using Governance.Domain;
using Governance.Web.Infrastructure;
using Microsoft.AspNetCore.Mvc;

namespace Governance.Web.Pages.Requests;

public class DetailModel(WorkQueries work, RegistryQueries registry, RequestCommands commands) : GovPage
{
    [BindProperty(SupportsGet = true)] public Guid Id { get; set; }

    public RequestDetail D { get; private set; } = new();
    public RequestPayload Payload { get; private set; } = new();
    public IReadOnlyList<Option> CostCenters { get; private set; } = Array.Empty<Option>();
    public IReadOnlyList<Option> Services { get; private set; } = Array.Empty<Option>();
    public IReadOnlyList<Option> EnvironmentClasses { get; private set; } = Array.Empty<Option>();
    public IReadOnlyList<Option> People { get; private set; } = Array.Empty<Option>();
    public TemplateRow? Template { get; private set; }
    public IReadOnlyList<ControlCheckRow> Checks { get; private set; } = Array.Empty<ControlCheckRow>();
    public Dictionary<string, string> Expected { get; private set; } = new();
    public PostureRow? Posture { get; private set; }
    public bool IsRequester => D.Request.RequesterPersonId == Principal.PersonId;

    public async Task<IActionResult> OnGetAsync()
    {
        var d = await work.RequestAsync(Id);
        if (d is null) return NotFound();
        D = d;
        Payload = RequestPayload.FromJson(d.Request.PayloadJson);
        var open = d.Tasks.Where(t => t.Status == TaskStatuses.Open).ToList();
        if (open.Any(t => t.StageCode == Stages.Classify))
        {
            CostCenters = await registry.CostCenterOptionsAsync(d.Request.FundingOwnerId);
            Services = await registry.ServiceOptionsAsync();
            EnvironmentClasses = await registry.EnvironmentClassOptionsAsync();
        }
        if (open.Any(t => t.TaskType == TaskTypes.Decision)) People = await registry.PeopleOptionsAsync();
        if (open.Any(t => t.TaskType == TaskTypes.Verification))
        {
            var serviceId = d.Request.ServiceId ?? d.Workload?.ServiceId;
            if (serviceId is int sid)
            {
                (Template, Checks) = await registry.ActiveTemplateForServiceAsync(sid);
                Expected = RequestCommands.Settings(Template?.SettingsJson);
            }
            if (d.Workload is not null)
                Posture = (await registry.WorkloadAsync(d.Workload.PublicId, null))?.Posture.FirstOrDefault(p => p.EffectiveTo is null);
        }
        if (d.Decisions.Count > 0 || d.Evidence.Count > 0) await AuditViewAsync("request.view", "Request", d.Request.RequestNumber);
        return Page();
    }

    /// <summary>A hint for the page: may this person act on the task? The engine decides for real.</summary>
    public bool CanAct(TaskView t)
    {
        if (t.Status != TaskStatuses.Open) return false;
        if (t.ExcludedText?.Contains(Principal.DisplayName) == true) return false;
        if (t.AssignedPersonId == Principal.PersonId) return true;
        if (t.AssignedPersonId is not null && t.ClaimedUtc is null) return false;
        if (t.AssignedRoleCode is null) return false;
        if (Principal.Roles.Any(r => r.RoleCode == t.AssignedRoleCode && (r.ScopeType == ScopeTypes.Tenant || r.ScopeId == D.Request.FundingOwnerId))) return true;
        return t.StageCode == Stages.RoutineApproval && Roles.BusinessOwnerRoles.Contains(t.AssignedRoleCode)
               && Principal.Roles.Any(r => r.RoleCode == Roles.DelegatedApprover && r.ScopeId == D.Request.FundingOwnerId);
    }

    public bool CanClaim(TaskView t) => CanAct(t) && t.AssignedPersonId is null && t.AssignedRoleCode != Roles.Requester;

    /// <summary>Outcomes a person chooses at this stage (automatic ones are left to the engine).</summary>
    public IReadOnlyList<(string Outcome, string Label)> Outcomes(TaskView t)
    {
        if (t.TaskType == TaskTypes.Consult) return new[] { ("CONSULTED", "Send my view") };
        if (t.StageCode == Stages.SpecialistReview) return new[] { ("REVIEWED", "Review complete") };
        if (t.RelatedEntityType == "HOLD") return new[] { ("EVIDENCE", "Evidence supplied") };
        var auto = new HashSet<string> { StageOutcomes.Fits, StageOutcomes.NoPattern, StageOutcomes.TicketClosed, StageOutcomes.TicketCancelled, StageOutcomes.AutoClose, StageOutcomes.PassGate, StageOutcomes.Pass, StageOutcomes.Fail, StageOutcomes.Resubmit, StageOutcomes.Withdraw };
        return D.Transitions.Where(x => x.FromStage == t.StageCode && !auto.Contains(x.Outcome))
            .Select(x => (x.Outcome, x.Outcome switch
            {
                StageOutcomes.Complete => "Complete: send to the next stage",
                StageOutcomes.Return => "Return to the requester",
                StageOutcomes.Classified => "Classification complete",
                StageOutcomes.Ready => "Ready",
                StageOutcomes.Closed => "Commitments closed",
                StageOutcomes.Executed => "Purchase executed",
                _ => Ui.Words(x.Outcome),
            })).ToList();
    }

    public Task<IActionResult> OnPostClaimAsync(Guid task) => Act(() => commands.ClaimAsync(Idem, task), "The task is yours. Others in the queue no longer see it as free.");
    public Task<IActionResult> OnPostReleaseAsync(Guid task) => Act(() => commands.ReleaseAsync(Idem, task), "Released back to the queue.");

    public Task<IActionResult> OnPostCompleteAsync(Guid task, string outcome, string? notes, IFormFile? file, string? evidenceType)
        => Act(() => commands.CompleteAsync(Idem, task, outcome, notes, Upload(file), evidenceType), "Done. The request moved on.");

    public Task<IActionResult> OnPostDecideAsync(Guid task, string outcome, string scope, string? conditions, string? reason, string? evidence,
        DateTime? expires, DateTime? review, string? conditionText, int? conditionOwner, DateTime? conditionDue, IFormFile? file)
    {
        var conds = string.IsNullOrWhiteSpace(conditionText) ? null : new List<ConditionInput> { new(conditionText, conditionOwner, conditionDue) };
        return Act(() => commands.DecideAsync(Idem, task, new DecisionInput(outcome, scope, conditions ?? conditionText, reason, evidence, expires, review, conds), Upload(file)),
            _ => "Decision recorded in the ledger. The people involved are notified.");
    }

    public Task<IActionResult> OnPostClassifyAsync(int? costCenterId, string? solutionClass, string? lifecycleStage, int? serviceId, string? criticality, string? triggerType,
        string? dataBoundary, string? dataClass, string? audience, string? environmentClassCode, List<string>? meters, bool metersNone)
        => Act(() => commands.SaveClassificationAsync(Idem, Id, new ClassificationInput(costCenterId, solutionClass, lifecycleStage, serviceId, criticality, triggerType,
            dataBoundary, dataClass, audience, environmentClassCode, meters ?? new List<string>(), metersNone)), "Classification saved. Complete the task when every field is set.");

    public Task<IActionResult> OnPostVerifyAsync(Guid task, string? configurationVersion, string? notes)
    {
        var checks = new List<CheckInput>();
        foreach (var key in Request.Form.Keys.Where(k => k.StartsWith("check_", StringComparison.Ordinal)))
        {
            if (!int.TryParse(key["check_".Length..], out var id)) continue;
            var passed = Request.Form[key] == "pass";
            var actual = Request.Form[$"actual_{id}"].FirstOrDefault();
            checks.Add(new CheckInput(id, passed, actual, Upload(Request.Form.Files[$"file_{id}"])));
        }
        return Act(() => commands.VerifyAsync(Idem, task, checks, configurationVersion, notes), r => r is "PASS" ? "Verified: the request moves on." : $"Verification recorded as failed. {r}");
    }

    public Task<IActionResult> OnPostUploadAsync(IFormFile? file, string evidenceType, string? description)
        => Act(async () =>
        {
            var f = Upload(file) ?? throw new Application.Data.DomainException("Choose a file.");
            await commands.UploadAsync(Idem, Id, f, evidenceType, description);
        }, "Evidence added. Its SHA-256 hash is in the ledger.");

    public Task<IActionResult> OnPostSubmitAsync() => Act(() => commands.SubmitAsync(Idem, Id), "Submitted.");
    public Task<IActionResult> OnPostWithdrawAsync() => Act(() => commands.WithdrawAsync(Idem, Id), "Withdrawn. The request is closed.");
}
