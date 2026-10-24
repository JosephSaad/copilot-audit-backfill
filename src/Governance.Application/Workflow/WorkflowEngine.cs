using System.Text.Json;
using Governance.Application.Data;
using Governance.Application.Outbox;
using Governance.Application.Requests;
using Governance.Application.Rules;
using Governance.Application.Sla;
using Governance.Domain;
using Microsoft.Extensions.Logging;

namespace Governance.Application.Workflow;

/// <summary>What a decider records (section 7.5).</summary>
public sealed record DecisionInput(
    string Outcome,
    string ScopeText,
    string? ConditionsText,
    string? ReasonText,
    string? RequiredEvidenceText,
    DateTime? ExpiresOn,
    DateTime? NextReviewOn,
    IReadOnlyList<ConditionInput>? Conditions = null,
    Guid? OnBehalfOfPersonPublicId = null);

public sealed record ConditionInput(string Text, int? OwnerPersonId, DateTime? DueOn);

/// <summary>
/// The table-driven state machine (section 7). Each request is pinned to its definition version; stage,
/// transition, consult and RACI rows decide who acts and what follows. Every method runs inside the caller's
/// transaction and writes state, events, tasks, decisions and outbox rows together.
/// </summary>
public sealed class WorkflowEngine(
    IClock clock,
    BusinessCalendarProvider calendars,
    AssignmentResolver resolver,
    OutboxWriter outbox,
    AuditWriter audit,
    JourneyEffects effects)
{
    // ------------------------------------------------------------------------------------------
    // Drafts and submission
    // ------------------------------------------------------------------------------------------

    public async Task<RequestRow> CreateDraftAsync(Uow uow, ICurrentPrincipal actor, string journeyCode, string title, int fundingOwnerId,
        int? serviceId, int? sponsorPersonId, int? workloadId, RequestPayload payload)
    {
        if (!Journeys.WithStages.Contains(journeyCode)) throw new DomainException("This journey does not use a request.");
        if (string.IsNullOrWhiteSpace(title)) throw new DomainException("Give the request a title.");
        var defId = await uow.ScalarAsync<int?>("SELECT DefinitionId FROM wf.WorkflowDefinition WHERE JourneyCode = @journeyCode AND IsActive = 1", new { journeyCode })
                    ?? throw new DomainException("This journey has no active definition.");
        var seq = await uow.ScalarAsync<long>("SELECT NEXT VALUE FOR wf.RequestNumber");
        var number = $"CCG-{clock.UtcNow:yyyy}-{seq:000000}";
        var id = await uow.ScalarAsync<long>(
            """
            INSERT wf.Request (RequestNumber, JourneyCode, DefinitionId, State, Title, Summary, WorkloadId, FundingOwnerId, ServiceId,
                               RequesterPersonId, SponsorPersonId, PayloadJson, EstimateCredits, CreatedUtc, LastActivityUtc)
            OUTPUT inserted.RequestId
            VALUES (@number, @journeyCode, @defId, 'DRAFT', @title, @summary, @workloadId, @fundingOwnerId, @serviceId,
                    @requester, @sponsorPersonId, @json, @estimate, @now, @now)
            """,
            new
            {
                number, journeyCode, defId, title = Trim(title, 200), summary = payload.Purpose is null ? null : Trim(payload.Purpose, 2000),
                workloadId, fundingOwnerId, serviceId, requester = actor.PersonId, sponsorPersonId, json = payload.ToJson(),
                estimate = payload.EstimateCreditsPerMonth, now = clock.UtcNow,
            });
        await EventAsync(uow, id, fundingOwnerId, actor.PersonId, "CREATED", null, RequestStates.Draft, null, null);
        await audit.WriteAsync(uow, actor, "request.create", "Request", number, journeyCode);
        return await GetRequestAsync(uow, id);
    }

    public async Task UpdateDraftAsync(Uow uow, ICurrentPrincipal actor, long requestId, string title, int fundingOwnerId, int? serviceId,
        int? sponsorPersonId, int? workloadId, RequestPayload payload, byte[]? rowVer)
    {
        var r = await GetRequestAsync(uow, requestId);
        if (r.RequesterPersonId != actor.PersonId) throw new DomainException("Only the requester can edit this request.");
        if (r.State is not (RequestStates.Draft or RequestStates.Returned or RequestStates.Declined))
            throw new DomainException("The request can be edited only as a draft or after it was returned.");
        var n = await uow.ExecuteAsync(
            """
            UPDATE wf.Request SET Title = @title, Summary = @summary, FundingOwnerId = @fundingOwnerId, ServiceId = @serviceId,
                   SponsorPersonId = @sponsorPersonId, WorkloadId = COALESCE(@workloadId, WorkloadId), PayloadJson = @json,
                   EstimateCredits = @estimate, LastActivityUtc = @now
            WHERE RequestId = @requestId AND (@rowVer IS NULL OR RowVer = @rowVer)
            """,
            new
            {
                requestId, title = Trim(title, 200), summary = payload.Purpose is null ? null : Trim(payload.Purpose, 2000), fundingOwnerId, serviceId,
                sponsorPersonId, workloadId, json = payload.ToJson(), estimate = payload.EstimateCreditsPerMonth, now = clock.UtcNow, rowVer,
            });
        if (n == 0) throw new ConcurrencyException();
        await EventAsync(uow, requestId, fundingOwnerId, actor.PersonId, "EDITED", r.State, r.State, r.CurrentStage, null);
    }

    public async Task SubmitAsync(Uow uow, ICurrentPrincipal actor, long requestId)
    {
        await LockRequestAsync(uow, requestId);
        var r = await GetRequestAsync(uow, requestId);
        if (r.RequesterPersonId != actor.PersonId) throw new DomainException("Only the requester can submit this request.");
        if (r.State != RequestStates.Draft) throw new DomainException("Only a draft can be submitted.");
        var intakeOpen = await uow.ScalarAsync<string>("SELECT SettingValue FROM ref.Setting WHERE SettingKey = 'Intake.Open'");
        if (!string.Equals(intakeOpen, "true", StringComparison.OrdinalIgnoreCase))
            throw new DomainException("Intake is not open yet: the foundation steps (section 4.1) must be finished first.");

        var first = (await uow.QueryAsync<StageDefinition>(
            "SELECT TOP (1) * FROM wf.StageDefinition WHERE DefinitionId = @DefinitionId AND StageCode <> 'B7' ORDER BY SortOrder", new { r.DefinitionId }))
            .FirstOrDefault() ?? throw new DomainException("The journey has no stages.");

        var moved = await uow.ExecuteAsync("UPDATE wf.Request SET State = 'SUBMITTED', SubmittedUtc = @now, LastActivityUtc = @now WHERE RequestId = @requestId AND State = 'DRAFT'",
            new { requestId, now = clock.UtcNow });
        if (moved == 0) throw new ConcurrencyException();
        await EventAsync(uow, requestId, r.FundingOwnerId, actor.PersonId, "SUBMITTED", RequestStates.Draft, RequestStates.Submitted, null, "Submitted (B1).");
        await audit.WriteAsync(uow, actor, "request.submit", "Request", r.RequestNumber, null);

        var people = new List<int> { r.RequesterPersonId };
        if (r.SponsorPersonId is int s && s != r.RequesterPersonId) people.Add(s);
        await outbox.EnqueueAsync(uow, new OutboxMessage
        {
            EventType = "N01", RequestId = requestId, FundingOwnerId = r.FundingOwnerId, PersonIds = people, Queues = { "INTAKE" },
            Subject = $"{r.RequestNumber} submitted: {r.Title}",
            Text = "The request was submitted. The intake team checks completeness and sponsorship within 2 business days.",
            LinkPath = $"/Requests/{r.PublicId}",
        }, $"N01:{requestId}");

        await OpenStageAsync(uow, await GetRequestAsync(uow, requestId), first.StageCode, actor);
    }

    // ------------------------------------------------------------------------------------------
    // Tasks
    // ------------------------------------------------------------------------------------------

    public async Task ClaimAsync(Uow uow, ICurrentPrincipal actor, long taskId)
    {
        var t = await LockTaskAsync(uow, taskId);
        if (t.Status != TaskStatuses.Open) throw new DomainException("This task is already closed.");
        var stage = t.RequestId is long rid ? await StageForTaskAsync(uow, t, rid) : null;
        var (ok, _, _, reason) = await resolver.CanActAsync(uow, t, actor.PersonId, stage?.AllowsDelegation ?? false);
        if (!ok) throw new DomainException(reason ?? "You cannot claim this task.");
        var n = await uow.ExecuteAsync(
            "UPDATE wf.Task SET AssignedPersonId = @p, ClaimedUtc = @now WHERE TaskId = @taskId AND Status = 'OPEN' AND AssignedPersonId IS NULL",
            new { p = actor.PersonId, now = clock.UtcNow, taskId });
        if (n == 0) throw new ConcurrencyException();
        await audit.WriteAsync(uow, actor, "task.claim", "Task", t.PublicId, t.Title);
    }

    public async Task ReleaseAsync(Uow uow, ICurrentPrincipal actor, long taskId)
    {
        var t = await GetTaskAsync(uow, taskId);
        if (t.AssignedPersonId != actor.PersonId || t.ClaimedUtc is null) throw new DomainException("You have not claimed this task.");
        await uow.ExecuteAsync("UPDATE wf.Task SET AssignedPersonId = NULL, ClaimedUtc = NULL WHERE TaskId = @taskId", new { taskId });
        await audit.WriteAsync(uow, actor, "task.release", "Task", t.PublicId, t.Title);
    }

    /// <summary>Completes a work, review, consult or verification task with a stage outcome.</summary>
    public async Task CompleteWorkAsync(Uow uow, ICurrentPrincipal actor, long taskId, string outcome, string? notes)
    {
        var t = await LockTaskAsync(uow, taskId);
        if (t.Status != TaskStatuses.Open) throw new DomainException("This task is already closed.");
        if (t.TaskType == TaskTypes.Decision) throw new DomainException("Decision tasks are completed by recording a decision.");
        var r = t.RequestId is long rid ? await GetRequestAsync(uow, rid) : throw new DomainException("This task is not part of a request.");
        var stage = await StageForTaskAsync(uow, t, r.RequestId);
        await AuthorizeAsync(uow, actor, t, r, stage);
        if (t.TaskType != TaskTypes.Consult && t.StageCode != Stages.SpecialistReview && t.RelatedEntityType != "HOLD")
            await RequireTransitionAsync(uow, r, t.StageCode, outcome == StageOutcomes.Pass ? new[] { StageOutcomes.Pass, StageOutcomes.PassGate } : new[] { outcome });

        // Exit rules (section 7.1): the stage's rule must pass before the stage can close with a forward outcome.
        if (stage?.ExitRule == "CompletenessCheck" && outcome == StageOutcomes.Complete)
        {
            var check = await CompletenessAsync(uow, r);
            if (!check.Passed) throw new DomainException("Not complete yet; return it with these items instead: " + string.Join(" ", check.Reasons));
        }
        if (stage?.ExitRule == "VerificationComplete" && outcome == StageOutcomes.Pass)
        {
            var since = await uow.ScalarAsync<DateTime?>(
                "SELECT MAX(Utc) FROM wf.RequestEvent WHERE RequestId = @RequestId AND EventType = 'TRANSITION' AND StageCode = 'D1'", new { r.RequestId }) ?? DateTime.MinValue;
            var verified = await uow.QueryFirstOrDefaultAsync<(string Result, int VerifierPersonId)>(
                "SELECT TOP (1) Result, VerifierPersonId FROM evd.Verification WHERE RequestId = @RequestId AND VerifiedUtc >= @since ORDER BY VerificationId DESC",
                new { r.RequestId, since });
            if (verified.Result != "PASS")
                throw new DomainException("Record a passing verification first: every check of the control template, including the explicit deny case (P5).");
            if (verified.VerifierPersonId != actor.PersonId)
                throw new DomainException("The verification on record was made by someone else; the verifier completes this task.");
        }

        if (t.StageCode == Stages.Classify && outcome == StageOutcomes.Classified)
        {
            var w = r.WorkloadId is int wid ? await uow.QuerySingleOrDefaultAsync<Workload>("SELECT * FROM core.Workload WHERE WorkloadId = @wid", new { wid }) : null;
            if (w is null) throw new DomainException("Save the classification before completing this task.");
            var meters = await uow.ScalarAsync<int>("SELECT COUNT(*) FROM core.WorkloadMeter WHERE WorkloadId = @WorkloadId", new { w.WorkloadId });
            var check = ClassificationComplete.Evaluate(w, meters, r.MetersDeclaredNone);
            if (!check.Passed) throw new DomainException("Classification is not complete: " + string.Join(" ", check.Reasons));
        }

        await CloseTaskAsync(uow, t, actor.PersonId, outcome, notes);
        await audit.WriteAsync(uow, actor, "task.complete", "Task", t.PublicId, $"{t.StageCode} {outcome}");

        // Consults never move the stage; a review (B8) moves it when the last review closes; a hold task re-opens B9.
        if (t.TaskType == TaskTypes.Consult) return;
        if (t.StageCode == Stages.SpecialistReview)
        {
            if (await OpenCountAsync(uow, r.RequestId, Stages.SpecialistReview) == 0)
                await TransitionAsync(uow, r.RequestId, StageOutcomes.Reviewed, actor);
            return;
        }
        if (t.StageCode == Stages.Disposition && t.RelatedEntityType == "HOLD")
        {
            await uow.ExecuteAsync("UPDATE wf.Request SET State = 'SPECIALIST_REVIEW', LastActivityUtc = @now WHERE RequestId = @RequestId", new { r.RequestId, now = clock.UtcNow });
            await EventAsync(uow, r.RequestId, r.FundingOwnerId, actor.PersonId, "EVIDENCE_SUPPLIED", RequestStates.OnHold, RequestStates.SpecialistReview, Stages.Disposition, notes);
            await OpenDispositionAsync(uow, await GetRequestAsync(uow, r.RequestId), actor, onlyUndecided: true);
            return;
        }
        if (t.StageCode == Stages.Verify && t.TaskType == TaskTypes.Verification && outcome == StageOutcomes.Pass)
        {
            var w = r.WorkloadId is int wid2 ? await uow.QuerySingleOrDefaultAsync<Workload>("SELECT * FROM core.Workload WHERE WorkloadId = @wid2", new { wid2 }) : null;
            var p = RequestPayload.FromJson(r.PayloadJson);
            if (r.JourneyCode == Journeys.NewWorkload && ProductionGateRequired.Evaluate(p.RequestedLifecycleStage, w?.Criticality, w?.SolutionClass))
                outcome = StageOutcomes.PassGate;
        }
        await TransitionAsync(uow, r.RequestId, outcome, actor);
    }

    /// <summary>Records a decision on a decision task. One immutable ledger row per decision.</summary>
    public async Task<long> DecideAsync(Uow uow, ICurrentPrincipal actor, long taskId, DecisionInput input)
    {
        var t = await LockTaskAsync(uow, taskId);
        if (t.Status != TaskStatuses.Open) throw new DomainException("This task is already closed.");
        if (t.TaskType != TaskTypes.Decision) throw new DomainException("This task is not a decision.");
        if (t.StageCode != Stages.Disposition)
            await RequireTransitionAsync(uow, await GetRequestAsync(uow, t.RequestId!.Value), t.StageCode, new[] { StageOutcomeFor(t.StageCode, input.Outcome) });
        if (!Outcomes.Positive.Contains(input.Outcome) && input.Outcome is not (Outcomes.Hold or Outcomes.Declined))
            throw new DomainException("Choose an outcome.");
        if (string.IsNullOrWhiteSpace(input.ScopeText)) throw new DomainException("State the scope of the decision (service, audience, tier, duration).");
        if (input.Outcome is Outcomes.Declined or Outcomes.Hold)
        {
            // No dead ends (B6): a decline or hold always says why and what evidence is needed.
            if (string.IsNullOrWhiteSpace(input.ReasonText) || string.IsNullOrWhiteSpace(input.RequiredEvidenceText))
                throw new DomainException("A decline or hold needs the reason and the evidence needed to proceed.");
        }
        if (input.Outcome is Outcomes.ApprovedWithConditions or Outcomes.ConstrainedPilot && string.IsNullOrWhiteSpace(input.ConditionsText) && (input.Conditions?.Count ?? 0) == 0)
            throw new DomainException("List the conditions.");
        if (Outcomes.Positive.Contains(input.Outcome) && input.ExpiresOn is null && input.NextReviewOn is null)
            throw new DomainException("Every approval expires or has a review date.");
        if (t.DecisionType == DecisionTypes.ExceptionDisposition && Outcomes.Positive.Contains(input.Outcome) && input.ExpiresOn is null)
            throw new DomainException("An exception needs an expiry date.");

        var r = await GetRequestAsync(uow, t.RequestId!.Value);
        var stage = await StageForTaskAsync(uow, t, r.RequestId);
        var (roleUsed, delegationId) = await AuthorizeAsync(uow, actor, t, r, stage);

        // SoD7: a delegated approver acts only within an active delegation and envelope, checked now and stored.
        if (delegationId is int did)
        {
            var delegation = await uow.QuerySingleAsync<Delegation>("SELECT * FROM sec.Delegation WHERE DelegationId = @did", new { did });
            var envIds = (await uow.QueryAsync<int>("SELECT EnvelopeId FROM sec.DelegationEnvelope WHERE DelegationId = @did", new { did })).ToList();
            var envelope = await EnvelopeForAsync(uow, r);
            var check = DelegationValid.Evaluate(delegation, envIds, envelope, r.EstimateCredits ?? 0, clock.UtcNow);
            if (!check.Passed && Outcomes.Positive.Contains(input.Outcome))
                throw new DomainException(string.Join(" ", check.Reasons) + " Escalate to the business owner.");
        }

        var raciVersionId = await ActiveRaciVersionAsync(uow);
        var activity = t.RaciActivityCode ?? stage?.RaciActivityCode ?? "BUSINESS_CASE";
        var publicId = Guid.NewGuid();
        var decisionId = await uow.ScalarAsync<long>(
            """
            INSERT wf.Decision (PublicId, RequestId, WorkloadId, FundingOwnerId, DecisionType, Outcome, DeciderPersonId, DeciderRoleCode, DelegationId,
                                OnBehalfOfPersonId, RaciVersionId, RaciActivityCode, StageCode, ScopeText, ConditionsText, ReasonText, RequiredEvidenceText,
                                ExpiresOn, NextReviewOn, Source, DecidedAtUtc)
            OUTPUT inserted.DecisionId
            VALUES (@publicId, @RequestId, @WorkloadId, @FundingOwnerId, @DecisionType, @Outcome, @Decider, @Role, @delegationId,
                    @OnBehalf, @raciVersionId, @activity, @StageCode, @ScopeText, @ConditionsText, @ReasonText, @RequiredEvidenceText,
                    @ExpiresOn, @NextReviewOn, 'WORKBENCH', @now)
            """,
            new
            {
                publicId, r.RequestId, r.WorkloadId, r.FundingOwnerId, DecisionType = t.DecisionType ?? stage?.DecisionType ?? DecisionTypes.RoutineApproval,
                input.Outcome, Decider = actor.PersonId, Role = roleUsed ?? "UNKNOWN", delegationId,
                OnBehalf = (int?)null, raciVersionId, activity, t.StageCode, ScopeText = Trim(input.ScopeText, 1000),
                ConditionsText = TrimN(input.ConditionsText, 2000), ReasonText = TrimN(input.ReasonText, 2000), RequiredEvidenceText = TrimN(input.RequiredEvidenceText, 2000),
                ExpiresOn = input.ExpiresOn?.Date, NextReviewOn = input.NextReviewOn?.Date, now = clock.UtcNow,
            });
        var no = 0;
        foreach (var c in input.Conditions ?? Array.Empty<ConditionInput>())
            await uow.ExecuteAsync("INSERT wf.DecisionCondition (DecisionId, ConditionNo, ConditionText, OwnerPersonId, DueOn) VALUES (@decisionId, @no, @Text, @OwnerPersonId, @DueOn)",
                new { decisionId, no = ++no, Text = Trim(c.Text, 1000), c.OwnerPersonId, DueOn = c.DueOn?.Date });

        await CloseTaskAsync(uow, t, actor.PersonId, input.Outcome, input.ReasonText ?? input.ConditionsText);
        await audit.WriteAsync(uow, actor, "decision.record", "Decision", publicId, $"{r.RequestNumber} {t.DecisionType} {input.Outcome}");
        await EventAsync(uow, r.RequestId, r.FundingOwnerId, actor.PersonId, "DECISION", r.State, r.State, t.StageCode,
            $"{DecisionTypes.Label(t.DecisionType ?? "")}: {Outcomes.Label(input.Outcome)}");
        await NotifyDecisionAsync(uow, r, decisionId, t.DecisionType ?? "", input, activity);

        // Stage logic: B9 aggregates several decisions; D5 waits for blocking consults (checked in Authorize); others move at once.
        if (t.StageCode == Stages.Disposition)
        {
            await AfterDispositionDecisionAsync(uow, r, actor, input.Outcome);
            return decisionId;
        }
        await TransitionAsync(uow, r.RequestId, StageOutcomeFor(t.StageCode, input.Outcome), actor);
        return decisionId;
    }

    // ------------------------------------------------------------------------------------------
    // Events from the worker
    // ------------------------------------------------------------------------------------------

    /// <summary>The ITSM ticket closed: the request moves to Verification, never straight to Completed (X8).</summary>
    public async Task OnTicketClosedAsync(Uow uow, long itsmTicketId, string state, int? configurerPersonId, string? closureNotes)
    {
        var ticket = await uow.QuerySingleAsync<(long? RequestId, string Kind, string State)>(
            "SELECT RequestId, Kind, State FROM wf.ItsmTicket WHERE ItsmTicketId = @itsmTicketId", new { itsmTicketId });
        if (ticket.RequestId is not long requestId || ticket.Kind != "TASK") return;
        await LockRequestAsync(uow, requestId);
        var r = await GetRequestAsync(uow, requestId);
        if (r.CurrentStage != Stages.Fulfillment) return;
        await uow.ExecuteAsync("UPDATE wf.Request SET ConfigurerPersonId = @configurerPersonId WHERE RequestId = @requestId", new { configurerPersonId, requestId });
        await EventAsync(uow, requestId, r.FundingOwnerId, null, "ITSM_" + state, r.State, r.State, Stages.Fulfillment,
            closureNotes + (configurerPersonId is null ? " The person who configured it could not be matched to a person here; a second operator verifies with read-back or evidence for every check." : ""));

        // SoD2: the person who approved cannot be the one who configured. Back to fulfillment, with a finding.
        if (state != "CANCELLED" && configurerPersonId is int cp
            && await uow.ScalarAsync<int>("SELECT COUNT(*) FROM wf.Decision WHERE RequestId = @requestId AND DeciderPersonId = @cp AND Outcome IN ('APPROVED','APPROVED_WITH_CONDITIONS','CONSTRAINED_PILOT','EXTEND')", new { requestId, cp }) > 0)
        {
            await uow.ExecuteAsync(
                "INSERT ops.Finding (FindingType, Severity, FundingOwnerId, WorkloadId, Title, Details, Status, DedupeKey) VALUES ('SOD_VIOLATION', 'HIGH', @FundingOwnerId, @WorkloadId, @title, @details, 'OPEN', @key)",
                new
                {
                    r.FundingOwnerId, r.WorkloadId, title = $"{r.RequestNumber}: the approver configured the change",
                    details = "SoD2: the person who approved the request closed its fulfillment ticket. The change goes back to fulfillment for another operator; check what was configured.",
                    key = $"SOD2:{itsmTicketId}",
                });
            await TransitionAsync(uow, requestId, StageOutcomes.TicketCancelled, SystemPrincipal.Instance);
            return;
        }
        await TransitionAsync(uow, requestId, state == "CANCELLED" ? StageOutcomes.TicketCancelled : StageOutcomes.TicketClosed, SystemPrincipal.Instance);
    }

    /// <summary>B7: returned requests close after the configured business days without a response.</summary>
    public async Task AutoCloseAsync(Uow uow, long requestId)
    {
        await LockRequestAsync(uow, requestId);
        var r = await GetRequestAsync(uow, requestId);
        if (r.CurrentStage != Stages.Remediate) return;
        await TransitionAsync(uow, requestId, StageOutcomes.AutoClose, SystemPrincipal.Instance);
        var people = new List<int> { r.RequesterPersonId };
        if (r.SponsorPersonId is int s) people.Add(s);
        await outbox.EnqueueAsync(uow, new OutboxMessage
        {
            EventType = "N02", RequestId = requestId, FundingOwnerId = r.FundingOwnerId, PersonIds = people,
            Subject = $"{r.RequestNumber} closed without a response", Text = "The request was returned and closed after 10 business days without a response. Start a new request when you are ready.",
            LinkPath = $"/Requests/{r.PublicId}", ActivityType = "requestReturned",
        }, $"N02:autoclose:{requestId}");
    }

    public async Task WithdrawAsync(Uow uow, ICurrentPrincipal actor, long requestId)
    {
        await LockRequestAsync(uow, requestId);
        var r = await GetRequestAsync(uow, requestId);
        if (r.RequesterPersonId != actor.PersonId) throw new DomainException("Only the requester can withdraw this request.");
        if (r.State == RequestStates.Draft)
        {
            await uow.ExecuteAsync("UPDATE wf.Request SET State = 'CANCELLED', ClosedUtc = @now, LastActivityUtc = @now WHERE RequestId = @requestId", new { requestId, now = clock.UtcNow });
            await EventAsync(uow, requestId, r.FundingOwnerId, actor.PersonId, "WITHDRAWN", RequestStates.Draft, RequestStates.Cancelled, null, null);
            return;
        }
        if (r.CurrentStage != Stages.Remediate) throw new DomainException("A submitted request can be withdrawn only after it was returned to you.");
        var task = await uow.QueryFirstOrDefaultAsync<TaskRow>("SELECT * FROM wf.Task WHERE RequestId = @requestId AND StageCode = 'B7' AND Status = 'OPEN'", new { requestId });
        if (task is not null) await CloseTaskAsync(uow, task, actor.PersonId, StageOutcomes.Withdraw, null);
        await TransitionAsync(uow, requestId, StageOutcomes.Withdraw, actor);
    }

    // ------------------------------------------------------------------------------------------
    // Transitions and stages
    // ------------------------------------------------------------------------------------------

    public async Task TransitionAsync(Uow uow, long requestId, string outcome, ICurrentPrincipal actor)
    {
        var r = await GetRequestAsync(uow, requestId);
        var from = r.CurrentStage ?? throw new DomainException("The request is not in a stage.");
        var tr = await uow.QuerySingleOrDefaultAsync<TransitionDefinition>(
            "SELECT * FROM wf.TransitionDefinition WHERE DefinitionId = @DefinitionId AND FromStage = @from AND Outcome = @outcome",
            new { r.DefinitionId, from, outcome })
            ?? throw new DomainException($"Outcome {outcome} is not allowed at stage {from}.");

        // Close whatever is still open at the stage being left (other holders' copies, non-blocking consults).
        if (tr.ToStage != from)
            await uow.ExecuteAsync("UPDATE wf.Task SET Status = 'CANCELLED', CompletedUtc = @now, Notes = COALESCE(Notes, N'Closed when the stage moved on.') WHERE RequestId = @requestId AND StageCode = @from AND Status = 'OPEN'",
                new { requestId, from, now = clock.UtcNow });

        var final = tr.ToStage == Stages.End;
        var changed = await uow.ExecuteAsync(
            "UPDATE wf.Request SET State = @ToState, CurrentStage = @stage, LastActivityUtc = @now, ClosedUtc = CASE WHEN @final = 1 THEN @now ELSE ClosedUtc END WHERE RequestId = @requestId AND CurrentStage = @from",
            new { tr.ToState, stage = final ? null : tr.ToStage, now = clock.UtcNow, final, requestId, from });
        if (changed == 0) throw new ConcurrencyException();
        await EventAsync(uow, requestId, r.FundingOwnerId, actor.PersonId == 0 ? null : actor.PersonId, "TRANSITION", r.State, tr.ToState, tr.ToStage, outcome);

        await effects.ApplyAsync(uow, await GetRequestAsync(uow, requestId), from, outcome, tr.ToStage, actor);

        if (!final && !(tr.ToStage == from && tr.ToStage == Stages.Disposition))
            await OpenStageAsync(uow, await GetRequestAsync(uow, requestId), tr.ToStage, actor);
        else if (tr.ToStage == Stages.Disposition && outcome == Outcomes.Hold)
            await OpenHoldAsync(uow, await GetRequestAsync(uow, requestId), actor);
    }

    public async Task OpenStageAsync(Uow uow, RequestRow r, string stageCode, ICurrentPrincipal actor)
    {
        var stage = await uow.QuerySingleAsync<StageDefinition>(
            "SELECT * FROM wf.StageDefinition WHERE DefinitionId = @DefinitionId AND StageCode = @stageCode", new { r.DefinitionId, stageCode });
        await uow.ExecuteAsync("UPDATE wf.Request SET CurrentStage = @stageCode WHERE RequestId = @RequestId", new { stageCode, r.RequestId });
        await effects.OnEnterAsync(uow, r, stage, actor);
        r = await GetRequestAsync(uow, r.RequestId);

        switch (stage.TaskKind)
        {
            case TaskKinds.System when stageCode == Stages.PatternCheck:
                var fit = await effects.EvaluatePatternAsync(uow, r);
                await uow.ExecuteAsync("UPDATE wf.Request SET PatternId = @PatternId, PatternResult = @text, EnvelopeId = @env WHERE RequestId = @RequestId",
                    new { fit.Result.PatternId, text = JsonSerializer.Serialize(fit.Result.FailedConditions), env = fit.EnvelopeId, r.RequestId });
                await EventAsync(uow, r.RequestId, r.FundingOwnerId, null, "PATTERN_CHECK", r.State, r.State, stageCode,
                    fit.Result.Fits ? $"Fits approved pattern {fit.Result.PatternCode}." : "No approved pattern fits: " + string.Join(" ", fit.Result.FailedConditions.Select(PatternFit.Describe)));
                await TransitionAsync(uow, r.RequestId, fit.Result.Fits ? StageOutcomes.Fits : StageOutcomes.NoPattern, SystemPrincipal.Instance);
                return;
            case TaskKinds.System when stageCode == Stages.SpecialistReview:
                await OpenReviewsAsync(uow, r, stage, actor);
                return;
            case TaskKinds.System when stageCode == Stages.Disposition:
                await OpenDispositionAsync(uow, r, actor, onlyUndecided: false);
                return;
            case TaskKinds.None when stageCode == Stages.Fulfillment:
                await effects.CreateFulfillmentTicketAsync(uow, r);
                return;
            case TaskKinds.None:
            case TaskKinds.System:
                return;
        }

        var taskType = stage.TaskKind switch
        {
            TaskKinds.Decision => TaskTypes.Decision,
            TaskKinds.Verification => TaskTypes.Verification,
            _ => TaskTypes.Work,
        };
        var role = stage.TaskRoleCode ?? await RaciRoleAsync(uow, stage.RaciActivityCode, taskType == TaskTypes.Decision);
        var decisionType = stage.DecisionType;
        var activity = stage.RaciActivityCode;
        // J8: the decision type, RACI activity and decider follow the kind of commercial decision.
        if (r.JourneyCode == Journeys.Commercial && stageCode == Stages.CommercialDecision)
        {
            (decisionType, activity, role) = RequestPayload.FromJson(r.PayloadJson).CommercialKind switch
            {
                "RENEWAL" => (DecisionTypes.RenewalApproval, "P3_RENEWAL", Roles.CommercialApprover),
                "CRITICAL_SHARED_FUNDING" => (DecisionTypes.ContinuityAcceptance, "SHARED_CONTINUITY", Roles.LobBusinessOwner),   // F9: shared-service owner
                _ => (DecisionTypes.PurchaseApproval, "P3_PURCHASE", Roles.CommercialApprover),
            };
        }
        var title = stage.Name + (r.Title.Length > 0 ? $": {r.Title}" : "");
        var task = await CreateTaskAsync(uow, r, stage.StageCode, taskType, title, role, stage.SlaPolicyCode, stage.LadderCode,
            decisionType, activity, discipline: null, reason: stage.Guidance, stage.AllowsDelegation);

        // Consults this stage opens (blocking or not) and digest lines for informed parties.
        foreach (var c in await uow.QueryAsync<(string FunctionCode, bool IsBlocking, string? ConsultRoleCode)>(
                     """
                     SELECT cd.FunctionCode, cd.IsBlocking, f.ConsultRoleCode
                     FROM wf.ConsultDefinition cd JOIN sec.RaciFunction f ON f.FunctionCode = cd.FunctionCode
                     WHERE cd.DefinitionId = @DefinitionId AND cd.StageCode = @StageCode
                     """, new { r.DefinitionId, stage.StageCode }))
        {
            if (c.ConsultRoleCode is null) continue;
            await CreateTaskAsync(uow, r, stage.StageCode, TaskTypes.Consult, $"Consulted: {stage.Name}: {r.Title}", c.ConsultRoleCode, stage.SlaPolicyCode, null,
                null, stage.RaciActivityCode, c.FunctionCode, c.IsBlocking ? "Your answer is needed before the decision." : "Your view is welcome; the decision does not wait for it.", false, isBlocking: c.IsBlocking);
        }
        await InformAsync(uow, r, stage);
    }

    private async Task OpenReviewsAsync(Uow uow, RequestRow r, StageDefinition stage, ICurrentPrincipal actor)
    {
        var failed = string.IsNullOrEmpty(r.PatternResult) ? new List<string>() : JsonSerializer.Deserialize<List<string>>(r.PatternResult) ?? new();
        if (r.JourneyCode == Journeys.Exception) failed.AddRange(effects.ExceptionTriggers(RequestPayload.FromJson(r.PayloadJson)));
        var reviews = TriggeredDisciplines.Evaluate(failed, r.JourneyCode);
        await uow.ExecuteAsync("UPDATE wf.Request SET TriggeredReviews = @json WHERE RequestId = @RequestId",
            new { json = Trim(JsonSerializer.Serialize(reviews.Select(x => new { x.Discipline, x.DecisionType, x.ActivityCode, x.DeciderRole, x.ReviewerRole, x.Reason })), 200 * 100), r.RequestId });
        foreach (var rev in reviews.GroupBy(x => x.Discipline))
        {
            var first = rev.First();
            await CreateTaskAsync(uow, r, Stages.SpecialistReview, TaskTypes.Work, $"{DisciplineLabel(first.Discipline)} review: {r.Title}", first.ReviewerRole,
                stage.SlaPolicyCode, stage.LadderCode, null, first.ActivityCode, first.Discipline, string.Join(" ", rev.Select(x => x.Reason).Distinct()), false);
        }
        await EventAsync(uow, r.RequestId, r.FundingOwnerId, null, "REVIEWS_OPENED", r.State, r.State, Stages.SpecialistReview,
            "Reviews opened: " + string.Join(", ", reviews.Select(x => DisciplineLabel(x.Discipline)).Distinct()));
    }

    private async Task OpenDispositionAsync(Uow uow, RequestRow r, ICurrentPrincipal actor, bool onlyUndecided)
    {
        var stage = await uow.QuerySingleAsync<StageDefinition>("SELECT * FROM wf.StageDefinition WHERE DefinitionId = @DefinitionId AND StageCode = 'B9'", new { r.DefinitionId });
        var reviews = string.IsNullOrEmpty(r.TriggeredReviews)
            ? new List<ReviewRef>()
            : JsonSerializer.Deserialize<List<ReviewRef>>(r.TriggeredReviews, new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? new();
        if (reviews.Count == 0) reviews.Add(new ReviewRef("ARCHITECTURE", DecisionTypes.ExceptionDisposition, "PLATFORM_FEATURE", Roles.PlatformOwner, Roles.Architect, "Exception disposition."));
        var positive = onlyUndecided
            ? (await RoundDecisionsAsync(uow, r)).Where(d => Outcomes.Positive.Contains(d.Outcome)).Select(d => d.DecisionType).ToHashSet()
            : new HashSet<string>();
        foreach (var d in reviews.GroupBy(x => x.DecisionType))
        {
            if (positive.Contains(d.Key)) continue;
            var first = d.First();
            await CreateTaskAsync(uow, r, Stages.Disposition, TaskTypes.Decision, $"{DecisionTypes.Label(d.Key)}: {r.Title}", first.DeciderRole,
                stage.SlaPolicyCode, stage.LadderCode, d.Key, first.ActivityCode, first.Discipline, string.Join(" ", d.Select(x => x.Reason).Distinct()),
                allowsDelegation: false);
        }
    }

    private async Task OpenHoldAsync(Uow uow, RequestRow r, ICurrentPrincipal actor)
    {
        var last = await uow.QueryFirstOrDefaultAsync<DecisionRow>("SELECT TOP (1) * FROM wf.Decision WHERE RequestId = @RequestId AND Outcome = 'HOLD' ORDER BY DecisionId DESC", new { r.RequestId });
        await CreateTaskAsync(uow, r, Stages.Disposition, TaskTypes.Work, $"Supply the evidence asked for: {r.Title}", Roles.Requester, "BD10", null,
            null, "BUSINESS_CASE", null, last?.RequiredEvidenceText ?? "Supply the evidence the decider asked for.", false, relatedType: "HOLD");
    }

    private sealed record ReviewRef(string Discipline, string DecisionType, string ActivityCode, string DeciderRole, string ReviewerRole, string Reason);

    private async Task AfterDispositionDecisionAsync(Uow uow, RequestRow r, ICurrentPrincipal actor, string outcome)
    {
        // Wait until every decision task of this round is closed, then aggregate: one decline declines,
        // one hold holds, otherwise the most constrained positive outcome wins.
        if (await OpenCountAsync(uow, r.RequestId, Stages.Disposition, TaskTypes.Decision) > 0) return;
        var outcomes = (await RoundDecisionsAsync(uow, r)).Select(d => d.Outcome).ToList();
        if (outcomes.Count == 0) return;
        string agg =
            outcomes.Contains(Outcomes.Declined) ? Outcomes.Declined :
            outcomes.Contains(Outcomes.Hold) ? Outcomes.Hold :
            outcomes.Contains(Outcomes.ConstrainedPilot) ? Outcomes.ConstrainedPilot :
            outcomes.Contains(Outcomes.ApprovedWithConditions) ? Outcomes.ApprovedWithConditions : Outcomes.Approved;
        await TransitionAsync(uow, r.RequestId, agg, actor);
    }

    // ------------------------------------------------------------------------------------------
    // Concurrency and round helpers
    // ------------------------------------------------------------------------------------------

    /// <summary>Serializes every command on one request (read committed snapshot is on, so counts need a lock).</summary>
    private Task LockRequestAsync(Uow uow, long requestId)
        => uow.ExecuteAsync("SELECT RequestId FROM wf.Request WITH (UPDLOCK, HOLDLOCK) WHERE RequestId = @requestId", new { requestId });

    private async Task<TaskRow> LockTaskAsync(Uow uow, long taskId)
    {
        var requestId = await uow.ScalarAsync<long?>("SELECT RequestId FROM wf.Task WHERE TaskId = @taskId", new { taskId });
        if (requestId is long rid) await LockRequestAsync(uow, rid);
        return await GetTaskAsync(uow, taskId);
    }

    private static async Task RequireTransitionAsync(Uow uow, RequestRow r, string? stageCode, IEnumerable<string> outcomes)
    {
        var list = outcomes.ToArray();
        var n = await uow.ScalarAsync<int>("SELECT COUNT(*) FROM wf.TransitionDefinition WHERE DefinitionId = @DefinitionId AND FromStage = @stageCode AND Outcome IN @list",
            new { r.DefinitionId, stageCode, list });
        if (n == 0) throw new DomainException($"That outcome is not allowed at this stage ({stageCode}).");
    }

    private static string StageOutcomeFor(string? stageCode, string decisionOutcome) => (stageCode, decisionOutcome) switch
    {
        (Stages.ProductionAcceptance, var o) when Outcomes.Positive.Contains(o) => StageOutcomes.Accepted,
        (Stages.ProductionAcceptance, _) => StageOutcomes.Rejected,
        (_, var o) when Outcomes.Positive.Contains(o) => Outcomes.Approved,
        (_, Outcomes.Hold) => Outcomes.Hold,
        _ => Outcomes.Declined,
    };

    /// <summary>The latest decision per type in the current B9 round, limited to the types this round triggered.</summary>
    private static async Task<IReadOnlyList<DecisionRow>> RoundDecisionsAsync(Uow uow, RequestRow r)
    {
        var roundStart = await uow.ScalarAsync<DateTime?>(
            "SELECT MAX(Utc) FROM wf.RequestEvent WHERE RequestId = @RequestId AND EventType = 'TRANSITION' AND StageCode = 'B9' AND ISNULL(Text, '') <> 'HOLD'",
            new { r.RequestId }) ?? DateTime.MinValue;
        var types = string.IsNullOrEmpty(r.TriggeredReviews)
            ? new HashSet<string> { DecisionTypes.ExceptionDisposition }
            : (JsonSerializer.Deserialize<List<ReviewRef>>(r.TriggeredReviews, new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? new()).Select(x => x.DecisionType).ToHashSet();
        var rows = await uow.QueryAsync<DecisionRow>(
            "SELECT * FROM wf.Decision WHERE RequestId = @RequestId AND StageCode = 'B9' AND DecidedAtUtc >= @roundStart ORDER BY DecisionId",
            new { r.RequestId, roundStart });
        return rows.Where(d => types.Contains(d.DecisionType)).GroupBy(d => d.DecisionType).Select(g => g.Last()).ToList();
    }

    private async Task<RuleResult> CompletenessAsync(Uow uow, RequestRow r)
    {
        var p = RequestPayload.FromJson(r.PayloadJson);
        var foStatus = await uow.ScalarAsync<string>("SELECT Status FROM core.FundingOwner WHERE FundingOwnerId = @FundingOwnerId", new { r.FundingOwnerId });
        var sponsorActive = r.SponsorPersonId is int sp && await uow.ScalarAsync<bool>(
            "SELECT CAST(CASE WHEN AccountEnabled = 1 AND (LeaveDate IS NULL OR LeaveDate > @today) THEN 1 ELSE 0 END AS bit) FROM sec.Person WHERE PersonId = @sp",
            new { sp, today = clock.UtcNow.Date });
        var service = r.ServiceId is int sid ? await uow.QuerySingleOrDefaultAsync<Service>("SELECT * FROM core.Service WHERE ServiceId = @sid", new { sid }) : null;
        var workload = r.WorkloadId is int wid ? await uow.QuerySingleOrDefaultAsync<Workload>("SELECT * FROM core.Workload WHERE WorkloadId = @wid", new { wid }) : null;
        var duplicates = (await uow.QueryAsync<string>(
            """
            SELECT RequestNumber FROM wf.Request
            WHERE RequestId <> @RequestId AND JourneyCode = @JourneyCode AND FundingOwnerId = @FundingOwnerId AND Title = @Title
              AND State NOT IN ('DRAFT','COMPLETED','CANCELLED','CLOSED','DECLINED')
            """, new { r.RequestId, r.JourneyCode, r.FundingOwnerId, r.Title })).ToList();
        return CompletenessCheck.Evaluate(new CompletenessCheck.Facts(r.JourneyCode, p, r.RequesterPersonId, r.SponsorPersonId, sponsorActive, foStatus, service, workload, duplicates));
    }

    // ------------------------------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------------------------------

    private async Task<(string? RoleUsed, int? DelegationId)> AuthorizeAsync(Uow uow, ICurrentPrincipal actor, TaskRow t, RequestRow r, StageDefinition? stage)
    {
        var (ok, roleUsed, delegationId, reason) = await resolver.CanActAsync(uow, t, actor.PersonId, stage?.AllowsDelegation ?? false);
        if (!ok) throw new DomainException(reason ?? "You cannot act on this task.");

        var approvers = (await uow.QueryAsync<int>("SELECT DISTINCT DeciderPersonId FROM wf.Decision WHERE RequestId = @RequestId AND Outcome IN ('APPROVED','APPROVED_WITH_CONDITIONS','CONSTRAINED_PILOT','EXTEND')", new { r.RequestId })).ToList();
        var prior = (await uow.QueryAsync<(int, string)>("SELECT DeciderPersonId, DecisionType FROM wf.Decision WHERE RequestId = @RequestId", new { r.RequestId })).ToList();
        var isAdmin = await uow.ScalarAsync<int>(
            "SELECT COUNT(*) FROM sec.RoleAssignment WHERE PersonId = @p AND RoleCode = 'WORKBENCH_ADMIN' AND EffectiveFrom <= @today AND (EffectiveTo IS NULL OR EffectiveTo >= @today)",
            new { p = actor.PersonId, today = clock.UtcNow.Date }) > 0;
        var allowTwo = await uow.ScalarAsync<bool>("SELECT AllowSamePersonForTwoDecisionTypes FROM sec.SodSetting WHERE RuleCode = 'SOD4'");
        var sod = SegregationOfDuties.Check(new SegregationOfDuties.Context(actor.PersonId, t.TaskType, t.StageCode, t.DecisionType ?? stage?.DecisionType,
            r.RequesterPersonId, approvers, r.ConfigurerPersonId, prior, isAdmin, allowTwo));
        if (!sod.Passed && t.TaskType != TaskTypes.Consult && !(t.AssignedRoleCode == Roles.Requester))
            throw new DomainException(sod.Reasons[0]);

        // A decision waits for blocking consults (D5: FinOps and Operations).
        if (t.TaskType == TaskTypes.Decision)
        {
            var blocking = await uow.ScalarAsync<int>("SELECT COUNT(*) FROM wf.Task WHERE RequestId = @RequestId AND StageCode = @StageCode AND TaskType = 'CONSULT' AND IsBlocking = 1 AND Status = 'OPEN'", new { r.RequestId, t.StageCode });
            if (blocking > 0) throw new DomainException("Blocking consultations are still open; the decision waits for them.");
        }
        return (roleUsed, delegationId);
    }

    public async Task<TaskRow> CreateTaskAsync(Uow uow, RequestRow r, string stageCode, string taskType, string title, string roleCode, string? slaCode, string? ladder,
        string? decisionType, string? activityCode, string? discipline, string? reason, bool allowsDelegation, bool isBlocking = true, string? relatedType = null)
    {
        var effectiveRole = await resolver.EffectiveRoleAsync(uow, roleCode, r.FundingOwnerId);
        var scopeType = await resolver.ScopeTypeOfAsync(uow, effectiveRole);
        int? scopeId = scopeType == ScopeTypes.FundingOwner ? r.FundingOwnerId : null;
        int? assigned = roleCode == Roles.Requester ? r.RequesterPersonId : null;
        var sla = slaCode is null ? null : await uow.QuerySingleOrDefaultAsync<SlaPolicy>("SELECT * FROM wf.SlaPolicy WHERE SlaPolicyCode = @slaCode", new { slaCode });
        var calendar = await calendars.GetAsync(uow);
        var times = calendar.Compute(sla, clock.UtcNow);

        var task = await uow.QuerySingleAsync<TaskRow>(
            """
            INSERT wf.Task (RequestId, FundingOwnerId, StageCode, TaskType, Title, AssignedRoleCode, ScopeType, ScopeId, AssignedPersonId, Status, IsBlocking,
                            SlaPolicyCode, LadderCode, StartedUtc, DueUtc, Reminder1Utc, Reminder2Utc, DecisionType, RaciActivityCode, Discipline, Reason, RelatedEntityType)
            OUTPUT inserted.*
            VALUES (@RequestId, @FundingOwnerId, @stageCode, @taskType, @title, @role, @scopeType, @scopeId, @assigned, 'OPEN', @isBlocking,
                    @slaCode, @ladder, @now, @DueUtc, @Reminder1Utc, @Reminder2Utc, @decisionType, @activityCode, @discipline, @reason, @relatedType)
            """,
            new
            {
                r.RequestId, r.FundingOwnerId, stageCode, taskType, title = Trim(title, 300), role = roleCode == Roles.Requester ? Roles.Requester : effectiveRole,
                scopeType, scopeId, assigned, isBlocking, slaCode, ladder, now = clock.UtcNow, times.DueUtc, times.Reminder1Utc, times.Reminder2Utc,
                decisionType, activityCode, discipline, reason = TrimN(reason, 1000), relatedType,
            });

        // Segregation of duties exclusions recorded on the task (shown on the "Who does what" panel).
        if (taskType == TaskTypes.Decision)
        {
            await ExcludeAsync(uow, task.TaskId, r.RequesterPersonId, "SOD1");
            // The sponsor owns the request's purpose and outcome claims, so does not decide it either.
            if (r.SponsorPersonId is int sponsor && sponsor != r.RequesterPersonId) await ExcludeAsync(uow, task.TaskId, sponsor, "SOD1");
            foreach (var admin in await uow.QueryAsync<int>("SELECT DISTINCT PersonId FROM sec.RoleAssignment WHERE RoleCode = 'WORKBENCH_ADMIN' AND (EffectiveTo IS NULL OR EffectiveTo >= @today)", new { today = clock.UtcNow.Date }))
                await ExcludeAsync(uow, task.TaskId, admin, "SOD5");
        }
        if (stageCode is Stages.PrepareFulfillment || taskType == TaskTypes.Verification)
            foreach (var a in await uow.QueryAsync<int>("SELECT DISTINCT DeciderPersonId FROM wf.Decision WHERE RequestId = @RequestId AND Outcome IN ('APPROVED','APPROVED_WITH_CONDITIONS','CONSTRAINED_PILOT','EXTEND')", new { r.RequestId }))
                await ExcludeAsync(uow, task.TaskId, a, "SOD2");
        if (taskType == TaskTypes.Verification && r.ConfigurerPersonId is int cfg)
            await ExcludeAsync(uow, task.TaskId, cfg, "SOD3");

        // Vacancy: nobody eligible (section 7.3, step 6).
        if (assigned is null)
        {
            var holders = await resolver.HoldersAsync(uow, effectiveRole, scopeType, scopeId, allowsDelegation);
            var excluded = (await uow.QueryAsync<int>("SELECT PersonId FROM wf.TaskExclusion WHERE TaskId = @TaskId", new { task.TaskId })).ToHashSet();
            if (!holders.Any(h => !excluded.Contains(h)))
                await effects.RaiseVacancyAsync(uow, r, task, effectiveRole);
        }

        // Action notice to the people who can act; activity feed type for Teams.
        var notify = NoticeCodeFor(stageCode, taskType, r.JourneyCode);
        if (notify is not null)
        {
            await outbox.EnqueueAsync(uow, new OutboxMessage
            {
                EventType = notify, RequestId = r.RequestId, TaskId = task.TaskId, FundingOwnerId = r.FundingOwnerId,
                PersonIds = assigned is int a2 ? new List<int> { a2 } : new List<int>(),
                Roles = assigned is null ? new List<RoleTarget> { new(effectiveRole, scopeType, scopeId) } : new List<RoleTarget>(),
                Queues = QueueFor(effectiveRole),
                Subject = $"{r.RequestNumber}: {title}",
                Text = reason ?? title,
                LinkPath = $"/Requests/{r.PublicId}",
                ActivityType = assigned is not null && stageCode == Stages.Remediate ? "requestReturned" : "taskAssigned",
                Facts = new() { ["due"] = times.DueUtc?.ToString("o") ?? "", ["stage"] = stageCode },
            }, $"{notify}:task:{task.TaskId}");
        }
        return task;
    }

    private static string? NoticeCodeFor(string stageCode, string taskType, string journey) => (stageCode, taskType) switch
    {
        (Stages.Remediate, _) => "N02",
        (Stages.Classify, _) => "N03",
        (Stages.Completeness, _) => "N03",
        (Stages.RoutineApproval, _) => "N04",
        (Stages.SpecialistReview, _) => "N05",
        (Stages.Disposition, TaskTypes.Decision) => "N05",
        (Stages.Disposition, _) => "N02",
        (Stages.PrepareFulfillment, _) => "N07",
        (Stages.Verify, _) => "N07",
        (Stages.BuildAndTest, _) or (Stages.ProductionAcceptance, _) => "N09",
        (_, TaskTypes.Consult) => "N05",
        _ => "N04",
    };

    private static List<string> QueueFor(string role) => role switch
    {
        Roles.IntakeAnalyst => new() { "INTAKE" },
        Roles.Architect => new() { "ARCHITECTURE" },
        Roles.PlatformOperator => new() { "OPERATIONS" },
        Roles.FinOpsAnalyst or Roles.FinOpsLead => new() { "FINOPS" },
        Roles.RiskReviewer => new() { "RISK" },
        _ => new(),
    };

    public static string DisciplineLabel(string? d) => d switch
    {
        Disciplines.Architecture => "Architecture",
        Disciplines.SecurityRisk => "Security and Risk",
        Disciplines.Finance => "FinOps",
        Disciplines.Operations => "Operations",
        Disciplines.Platform => "Preview adoption",
        Disciplines.Business => "Business owner",
        _ => d ?? "",
    };

    private Task ExcludeAsync(Uow uow, long taskId, int personId, string rule)
        => uow.ExecuteAsync("IF NOT EXISTS (SELECT 1 FROM wf.TaskExclusion WHERE TaskId = @taskId AND PersonId = @personId) INSERT wf.TaskExclusion (TaskId, PersonId, RuleCode) VALUES (@taskId, @personId, @rule)",
            new { taskId, personId, rule });

    private async Task InformAsync(Uow uow, RequestRow r, StageDefinition stage)
    {
        if (stage.RaciActivityCode is null) return;
        var version = await ActiveRaciVersionAsync(uow);
        var informed = await uow.QueryAsync<(string FunctionCode, string? DecisionRoleCode, string ScopeType)>(
            """
            SELECT c.FunctionCode, f.DecisionRoleCode, f.ScopeType FROM sec.RaciCell c JOIN sec.RaciFunction f ON f.FunctionCode = c.FunctionCode
            WHERE c.RaciVersionId = @version AND c.ActivityCode = @RaciActivityCode AND c.RaciValue = 'I' AND f.DecisionRoleCode IS NOT NULL
            """, new { version, stage.RaciActivityCode });
        foreach (var i in informed)
        {
            var role = await resolver.EffectiveRoleAsync(uow, i.DecisionRoleCode!, r.FundingOwnerId);
            var scopeType = await resolver.ScopeTypeOfAsync(uow, role);
            foreach (var p in await resolver.HoldersAsync(uow, role, scopeType, scopeType == ScopeTypes.FundingOwner ? r.FundingOwnerId : null, false))
                await outbox.AddDigestAsync(uow, p, "N17", $"{r.RequestNumber} {r.Title}: now at {stage.Name}.", $"/Requests/{r.PublicId}");
        }
    }

    private async Task NotifyDecisionAsync(Uow uow, RequestRow r, long decisionId, string decisionType, DecisionInput input, string activity)
    {
        var people = new List<int> { r.RequesterPersonId };
        if (r.SponsorPersonId is int s && s != r.RequesterPersonId) people.Add(s);
        var text = $"{DecisionTypes.Label(decisionType)}: {Outcomes.Label(input.Outcome)}."
                   + (input.ConditionsText is { Length: > 0 } c ? $" Conditions: {c}" : "")
                   + (input.ReasonText is { Length: > 0 } why ? $" Reason: {why}" : "")
                   + (input.RequiredEvidenceText is { Length: > 0 } ev ? $" Evidence needed: {ev}" : "");
        await outbox.EnqueueAsync(uow, new OutboxMessage
        {
            EventType = "N06", RequestId = r.RequestId, DecisionId = decisionId, FundingOwnerId = r.FundingOwnerId, PersonIds = people,
            Subject = $"{r.RequestNumber}: {Outcomes.Label(input.Outcome)}", Text = text, LinkPath = $"/Requests/{r.PublicId}", ActivityType = "decisionRecorded",
        }, $"N06:{decisionId}");
        // The LOB business owner gets the decision in the daily digest.
        var ownerRole = await resolver.EffectiveRoleAsync(uow, Roles.LobBusinessOwner, r.FundingOwnerId);
        foreach (var p in await resolver.HoldersAsync(uow, ownerRole, ScopeTypes.FundingOwner, r.FundingOwnerId, false))
            await outbox.AddDigestAsync(uow, p, "N06", $"{r.RequestNumber} {r.Title}: {text}", $"/Requests/{r.PublicId}");
    }

    private async Task CloseTaskAsync(Uow uow, TaskRow t, int personId, string outcome, string? notes)
    {
        var n = await uow.ExecuteAsync(
            "UPDATE wf.Task SET Status = 'DONE', Outcome = @outcome, CompletedByPersonId = @personId, CompletedUtc = @now, Notes = COALESCE(@notes, Notes) WHERE TaskId = @TaskId AND Status = 'OPEN'",
            new { outcome, personId, now = clock.UtcNow, notes = TrimN(notes, 2000), t.TaskId });
        if (n == 0) throw new ConcurrencyException();
    }

    private Task<int> OpenCountAsync(Uow uow, long requestId, string stageCode, string? taskType = null)
        => uow.ScalarAsync<int>("SELECT COUNT(*) FROM wf.Task WHERE RequestId = @requestId AND StageCode = @stageCode AND Status = 'OPEN' AND TaskType <> 'CONSULT' AND (@taskType IS NULL OR TaskType = @taskType)",
            new { requestId, stageCode, taskType });

    private async Task<string> RaciRoleAsync(Uow uow, string? activity, bool decision)
    {
        if (activity is null) return Roles.PlatformOwner;
        var version = await ActiveRaciVersionAsync(uow);
        var role = await uow.ScalarAsync<string>(
            $"""
            SELECT TOP (1) {(decision ? "f.DecisionRoleCode" : "f.WorkRoleCode")}
            FROM sec.RaciCell c JOIN sec.RaciFunction f ON f.FunctionCode = c.FunctionCode
            WHERE c.RaciVersionId = @version AND c.ActivityCode = @activity AND c.RaciValue IN {(decision ? "('A','A/R')" : "('R','A/R')")}
            ORDER BY f.SortOrder
            """, new { version, activity });
        return role ?? Roles.PlatformOwner;
    }

    public async Task<int> ActiveRaciVersionAsync(Uow uow)
        => await uow.ScalarAsync<int?>("SELECT RaciVersionId FROM sec.RaciVersion WHERE Status = 'ACTIVE'")
           ?? throw new DomainException("No RACI version is active. Approve and activate RACI version 1 first (setup step 0).");

    private async Task<Envelope?> EnvelopeForAsync(Uow uow, RequestRow r)
        => r.EnvelopeId is int id
            ? await uow.QuerySingleOrDefaultAsync<Envelope>(
                "SELECT e.*, COALESCE((SELECT SUM(Amount) FROM core.EnvelopeCommitment c WHERE c.EnvelopeId = e.EnvelopeId AND c.Status = 'ACTIVE'), 0) AS Committed FROM core.FundingEnvelope e WHERE e.EnvelopeId = @id", new { id })
            : null;

    private async Task<StageDefinition?> StageForTaskAsync(Uow uow, TaskRow t, long requestId)
        => t.StageCode is null ? null : await uow.QuerySingleOrDefaultAsync<StageDefinition>(
            "SELECT s.* FROM wf.StageDefinition s JOIN wf.Request r ON r.DefinitionId = s.DefinitionId WHERE r.RequestId = @requestId AND s.StageCode = @StageCode",
            new { requestId, t.StageCode });

    public Task<RequestRow> GetRequestAsync(Uow uow, long requestId)
        => uow.QuerySingleAsync<RequestRow>("SELECT * FROM wf.Request WHERE RequestId = @requestId", new { requestId });

    public Task<TaskRow> GetTaskAsync(Uow uow, long taskId)
        => uow.QuerySingleAsync<TaskRow>("SELECT * FROM wf.Task WHERE TaskId = @taskId", new { taskId });

    public Task EventAsync(Uow uow, long requestId, int fundingOwnerId, int? personId, string type, string? from, string? to, string? stage, string? text)
        => uow.ExecuteAsync(
            "INSERT wf.RequestEvent (RequestId, FundingOwnerId, Utc, PersonId, EventType, FromState, ToState, StageCode, Text) VALUES (@requestId, @fundingOwnerId, @now, @personId, @type, @from, @to, @stage, @text)",
            new { requestId, fundingOwnerId, now = clock.UtcNow, personId, type, from, to, stage, text = TrimN(text, 2000) });

    private static string Trim(string s, int max) => s.Length <= max ? s : s[..max];
    private static string? TrimN(string? s, int max) => s is null ? null : Trim(s, max);
}
