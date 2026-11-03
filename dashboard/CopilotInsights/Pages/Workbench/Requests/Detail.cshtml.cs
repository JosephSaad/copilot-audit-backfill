using Governance.Application;
using Governance.Application.Data;
using Governance.Application.Queries;
using Governance.Domain;
using Governance.Web.Infrastructure;
using Governance.Application.Commands;
using Governance.Application.Queries;
using Governance.Application.Requests;
using Governance.Application.Workflow;
using Governance.Domain;
using Governance.Web.Infrastructure;
using Microsoft.AspNetCore.Mvc;

namespace Governance.Web.Pages.Requests;

public class DetailModel(WorkQueries work, RegistryQueries registry, RequestCommands commands, Application.Data.Db db) : GovPage
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
    /// <summary>B2 completeness on this request now: shown to the requester before submitting and prefilled in the intake return note.</summary>
    public IReadOnlyList<string> CompletenessGaps { get; private set; } = Array.Empty<string>();
    /// <summary>The envelope this request draws on, for the people deciding it.</summary>
    public EnvelopeHeadroomView? Envelope { get; private set; }
    /// <summary>J9: the seat change and the figures the Finance decider weighs (Section 4.4), computed today.</summary>
    public Application.Seats.SeatRequests.Facts? Seat { get; private set; }

    // D1: what the verifier entered, kept when the form is shown again after a refusal.
    public string? VerifyError { get; private set; }
    public Dictionary<int, (bool Passed, string? Actual)> PostedChecks { get; private set; } = new();
    public string? PostedConfigurationVersion { get; private set; }
    public string? PostedNotes { get; private set; }

    /// <summary>
    /// The estimate in plain words. Before the pattern check it is the monthly figure the requester gave; afterwards the
    /// request holds the whole commitment (a month's credits times the months it runs inside the envelope period).
    /// </summary>
    public string? EstimateText
    {
        get
        {
            var r = D.Request;
            if (r.EstimateCredits is not decimal total) return null;
            decimal monthly = r.JourneyCode == Journeys.CapacityChange
                ? Math.Max(0, (Payload.RequestedValue ?? 0) - (Payload.CurrentValue ?? 0))
                : Payload.EstimateCreditsPerMonth ?? 0;
            var evaluated = r.PatternResult is not null;
            if (!evaluated || monthly <= 0) return $"{Ui.Num(monthly > 0 ? monthly : total)} Copilot Credits a month (estimate)";
            var months = (int)Math.Round(total / monthly, MidpointRounding.AwayFromZero);
            return $"{Ui.Num(monthly)} a month × {months} month{(months == 1 ? "" : "s")} = {Ui.Num(total)} Copilot Credits committed if approved";
        }
    }

    public async Task<IActionResult> OnGetAsync()
    {
        var d = await work.RequestAsync(Id);
        if (d is null) return NotFound();
        await LoadAsync(d);
        if (d.Decisions.Count > 0 || d.Evidence.Count > 0) await AuditViewAsync("request.view", "Request", d.Request.RequestNumber);
        return Page();
    }

    private async Task LoadAsync(RequestDetail d)
    {
        D = d;
        Payload = RequestPayload.FromJson(d.Request.PayloadJson);
        var open = d.Tasks.Where(t => t.Status == TaskStatuses.Open).ToList();
        if ((IsRequester && d.Request.State is "DRAFT" or "RETURNED" or "DECLINED") || open.Any(t => t.StageCode == Stages.Completeness))
            CompletenessGaps = await work.CompletenessPreviewAsync(d.Request.RequestId);
        if (open.Any(t => t.StageCode == Stages.Classify))
        {
            CostCenters = await registry.CostCenterOptionsAsync(d.Request.FundingOwnerId);
            Services = await registry.ServiceOptionsAsync();
            EnvironmentClasses = await registry.EnvironmentClassOptionsAsync();
        }
        if (open.Any(t => t.TaskType == TaskTypes.Decision))
        {
            People = await registry.PeopleInScopeAsync(d.Request.FundingOwnerId);
            if (d.Request.EnvelopeId is int env) Envelope = await work.EnvelopeHeadroomAsync(env);
        }
        if (d.Request.JourneyCode == Journeys.SeatReservation && d.Request.State is not ("COMPLETED" or "CANCELLED" or "CLOSED" or "DECLINED"))
            Seat = await db.InTransactionAsync(u => Application.Seats.SeatRequests.FactsAsync(u, d.Request, DateTime.UtcNow));
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
    }

    /// <summary>Who the open task waits for: its role pool in scope, plus delegated approvers where the stage allows them.</summary>
    public string PoolText(TaskView t)
    {
        if (t.AssignedName is not null) return t.AssignedName;
        if (t.AssignedRoleCode == Roles.Requester) return "the requester";
        var role = (t.RoleName ?? Ui.Words(t.AssignedRoleCode)).ToLowerInvariant().Replace("lob ", "LOB ");
        var lob = Roles.BusinessOwnerRoles.Contains(t.AssignedRoleCode ?? "");
        var scoped = lob ? $" for {D.FundingOwnerName}" : "";
        var delegates = lob && t.StageCode == Stages.RoutineApproval ? ", or a delegated approver" : "";
        return $"{Ui.Article(role)} {role}{scoped}{delegates}";
    }

    /// <summary>Decision outcomes offered: only those the stage's transitions allow (hold and pilot only where they exist).</summary>
    public IReadOnlyList<(string Value, string Label)> DecisionOutcomes(TaskView t)
    {
        var from = D.Transitions.Where(x => x.FromStage == t.StageCode).Select(x => x.Outcome).ToHashSet();
        var list = new List<(string, string)> { ("APPROVED", t.StageCode == Stages.ProductionAcceptance ? "Accept" : "Approve"), ("APPROVED_WITH_CONDITIONS", t.StageCode == Stages.ProductionAcceptance ? "Accept with conditions" : "Approve with conditions") };
        if (from.Contains("CONSTRAINED_PILOT")) list.Add(("CONSTRAINED_PILOT", "Constrained pilot"));
        if (from.Contains("HOLD")) list.Add(("HOLD", D.Request.JourneyCode == Journeys.SeatReservation ? "Hold (until the window or more evidence)" : "Hold for evidence"));
        list.Add(("DECLINED", t.StageCode == Stages.ProductionAcceptance ? "Reject: back to build and test" : "Decline"));
        return list;
    }

    /// <summary>Intake's return note, prefilled with what the completeness check finds missing.</summary>
    public string? ReturnNotePrefill(TaskView t)
        => t.StageCode == Stages.Completeness && CompletenessGaps.Count > 0 ? "Missing: " + string.Join(" ", CompletenessGaps) : null;

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
        => Act(() => commands.CompleteAsync(Idem, task, outcome, notes, Upload(file), evidenceType), "The request moved on.");

    public Task<IActionResult> OnPostDecideAsync(Guid task, string outcome, string scope, string? conditions, string? reason, string? evidence,
        DateTime? expires, DateTime? review, string? conditionText, int? conditionOwner, DateTime? conditionDue, IFormFile? file, string? flexPoolReview)
    {
        var conds = string.IsNullOrWhiteSpace(conditionText) ? null : new List<ConditionInput> { new(conditionText, conditionOwner, conditionDue) };
        return Act(() => commands.DecideAsync(Idem, task, new DecisionInput(outcome, scope, conditions ?? conditionText, reason, evidence, expires, review, conds,
                FlexPoolReview: flexPoolReview), Upload(file)),
            _ => "Decision recorded in the ledger. The people involved are notified.");
    }

    public Task<IActionResult> OnPostClassifyAsync(int? costCenterId, string? solutionClass, string? lifecycleStage, int? serviceId, string? criticality, string? triggerType,
        string? dataBoundary, string? dataClass, string? audience, string? environmentClassCode, List<string>? meters, bool metersNone)
        => Act(() => commands.SaveClassificationAsync(Idem, Id, new ClassificationInput(costCenterId, solutionClass, lifecycleStage, serviceId, criticality, triggerType,
            dataBoundary, dataClass, audience, environmentClassCode, meters ?? new List<string>(), metersNone)), "Classification saved. Complete the task when every field is set.");

    /// <summary>
    /// D1. "Record: passed" is checked in full before anything is written; a refusal shows the form again with what was
    /// entered (files must be chosen again: browsers never refill file inputs). Only "Record: failed" writes a FAIL row.
    /// </summary>
    public async Task<IActionResult> OnPostVerifyAsync(Guid task, string? configurationVersion, string? notes, string intent)
    {
        var checks = new List<CheckInput>();
        foreach (var key in Request.Form.Keys.Where(k => k.StartsWith("check_", StringComparison.Ordinal)))
        {
            if (!int.TryParse(key["check_".Length..], out var id)) continue;
            var passed = Request.Form[key] == "pass";
            var actual = Request.Form[$"actual_{id}"].FirstOrDefault();
            checks.Add(new CheckInput(id, passed, actual, Upload(Request.Form.Files[$"file_{id}"])));
        }
        var failIntent = string.Equals(intent, "fail", StringComparison.OrdinalIgnoreCase);
        try
        {
            var r = await commands.VerifyAsync(Idem, task, checks, configurationVersion, notes, failIntent);
            Flash = r is "PASS" ? "Verification recorded: passed. The request moves on." : "Verification recorded: failed. The change goes back to fulfillment for a new ITSM task.";
            return RedirectToPage();
        }
        catch (DomainException ex) { VerifyError = ex.Message; }
        catch (ConcurrencyException ex) { VerifyError = ex.Message; }
        catch (Exception ex) { VerifyError = Incident.Note(Log, ex, "recording the verification"); }

        PostedChecks = checks.ToDictionary(c => c.ControlCheckId, c => (c.Passed, c.Actual));
        PostedConfigurationVersion = configurationVersion;
        PostedNotes = notes;
        var d = await work.RequestAsync(Id);
        if (d is null) return NotFound();
        await LoadAsync(d);
        return Page();
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
