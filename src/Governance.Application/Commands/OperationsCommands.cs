using System.Text.Json;
using Governance.Application.Data;
using Governance.Application.Outbox;
using Governance.Application.Workflow;
using Governance.Domain;

namespace Governance.Application.Commands;

/// <summary>Operations (G1 to G5), findings, attestation (X6), recertification (I1) and the exception register.</summary>
/// <summary>
/// A draft created by the Workbench for someone else (a retirement decided at recertification): the request belongs to
/// <c>PersonId</c>; the audit trail records the person who caused it.
/// </summary>
public sealed class OnBehalfPrincipal(int personId, ICurrentPrincipal actor) : ICurrentPrincipal
{
    public bool IsAuthenticated => true;
    public int PersonId => personId;
    public int? RealPersonId => actor.RealPersonId ?? actor.PersonId;
    public string DisplayName => actor.DisplayName;
    public bool IsCentral => false;
    public IReadOnlyList<RoleGrant> Roles { get; } = Array.Empty<RoleGrant>();
    public IReadOnlySet<string> AppRoles { get; } = new HashSet<string>();
    public IReadOnlySet<string> Permissions { get; } = new HashSet<string>();
    public IReadOnlyDictionary<int, IReadOnlySet<string>> ScopedPermissions { get; } = new Dictionary<int, IReadOnlySet<string>>();
}

public sealed class OperationsCommands(CommandRunner run, DecisionRecorder decisions, EvidenceStore evidence, OutboxWriter outbox, BusinessCalendarProvider calendars, IClock clock, WorkflowEngine engine)
{
    private ICurrentPrincipal Me => run.Principal;

    // ---------------------------------------------------------------- consumption events (J5)

    public Task<string?> ReportEventAsync(Guid idem, int fundingOwnerId, Guid? workloadPublicId, string eventClass, string severity, string title, string description, bool criticalService)
        => run.RunAsync(idem, "event.report", async uow =>
        {
            if (eventClass is not ("THRESHOLD" or "DENIAL" or "EXHAUSTION" or "RUNAWAY" or "NO_PERMITTED_SOURCE" or "OTHER")) throw new DomainException("Choose what happened.");
            if (severity is not ("NORMAL" or "HIGH" or "CRITICAL")) throw new DomainException("Choose the severity.");
            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(description)) throw new DomainException("Describe what happened.");
            int? workloadId = workloadPublicId is Guid wp ? await uow.ScalarAsync<int?>("SELECT WorkloadId FROM core.Workload WHERE PublicId = @wp", new { wp }) : null;
            var slaCode = severity switch { "CRITICAL" => "EVT_CRITICAL", "HIGH" => "EVT_HIGH", _ => "EVT_NORMAL" };
            var sla = await uow.QuerySingleAsync<SlaPolicy>("SELECT * FROM wf.SlaPolicy WHERE SlaPolicyCode = @slaCode", new { slaCode });
            var due = (await calendars.GetAsync(uow)).Compute(sla, clock.UtcNow).DueUtc ?? clock.UtcNow.AddDays(1);
            var e = await uow.QuerySingleAsync<(int EventId, Guid PublicId)>(
                """
                INSERT ops.ConsumptionEvent (FundingOwnerId, WorkloadId, EventClass, Severity, Title, Description, ReportedByPersonId, AckDueUtc, CriticalServiceAffected, Status)
                OUTPUT inserted.EventId, inserted.PublicId
                VALUES (@fundingOwnerId, @workloadId, @eventClass, @severity, @title, @description, @me, @due, @criticalService, 'OPEN')
                """, new { fundingOwnerId, workloadId, eventClass, severity, title = CommandRunner.Trim(title, 300), description = CommandRunner.Trim(description, 2000), me = Me.PersonId, due, criticalService });
            // High and critical events open a linked ITSM incident (X8).
            if (severity is "HIGH" or "CRITICAL")
            {
                var payload = JsonSerializer.Serialize(new { shortDescription = $"Copilot consumption event: {title}", severity, eventClass, description, criticalService });
                var ticketId = await uow.ScalarAsync<long>(
                    "INSERT wf.ItsmTicket (FundingOwnerId, EventId, Kind, CorrelationId, State, Payload) OUTPUT inserted.ItsmTicketId VALUES (@fundingOwnerId, @EventId, 'INCIDENT', NEWID(), 'PENDING_CREATE', @payload)",
                    new { fundingOwnerId, e.EventId, payload });
                await outbox.EnqueueAsync(uow, new OutboxMessage { EventType = "ITSM.Create", ItsmTicketId = ticketId, EventId = e.EventId }, $"ITSM.Create:{ticketId}");
            }
            var roles = new List<RoleTarget> { new(Roles.PlatformOperator, ScopeTypes.Tenant, null) };
            if (criticalService) roles.Add(new RoleTarget(Roles.SharedServiceOwner, ScopeTypes.FundingOwner, fundingOwnerId));
            await outbox.EnqueueAsync(uow, new OutboxMessage
            {
                EventType = "N12", EventId = e.EventId, FundingOwnerId = fundingOwnerId, Roles = roles, Queues = { "OPERATIONS" },
                Subject = $"{severity} consumption event: {title}", Text = $"{description} Acknowledge by {due:u}.", LinkPath = $"/Operations/Events/{e.PublicId}",
            }, $"N12:{e.EventId}");
            await run.AuditAsync(uow, "event.report", "ConsumptionEvent", e.PublicId, $"{severity} {eventClass}");
            return e.PublicId.ToString();
        });

    /// <summary>G stages: acknowledge, respond, restore, review and close, in order.</summary>
    public Task AdvanceEventAsync(Guid idem, Guid publicId, string step, string? text)
        => run.RunAsync(idem, "event." + step, async uow =>
        {
            run.Require(Permissions.EventRespond, null, "Platform Operations responds to consumption events.");
            var e = await uow.QuerySingleOrDefaultAsync<(int EventId, string Status, DateTime? AckUtc)>("SELECT EventId, Status, AckUtc FROM ops.ConsumptionEvent WHERE PublicId = @publicId", new { publicId });
            if (e.EventId == 0) throw new DomainException("Event not found.");
            var now = clock.UtcNow;
            switch (step)
            {
                case "acknowledge" when e.Status == "OPEN":
                    await uow.ExecuteAsync("UPDATE ops.ConsumptionEvent SET AckUtc = @now, AckByPersonId = @me, Status = 'ACKNOWLEDGED' WHERE EventId = @EventId", new { now, me = Me.PersonId, e.EventId });
                    break;
                case "respond" when e.Status is "ACKNOWLEDGED" or "OPEN":
                    if (string.IsNullOrWhiteSpace(text)) throw new DomainException("Describe the action taken within your authority.");
                    await uow.ExecuteAsync("UPDATE ops.ConsumptionEvent SET ResponseAction = @text, AckUtc = COALESCE(AckUtc, @now), AckByPersonId = COALESCE(AckByPersonId, @me), Status = 'ACKNOWLEDGED' WHERE EventId = @EventId",
                        new { text = CommandRunner.Trim(text, 2000), now, me = Me.PersonId, e.EventId });
                    break;
                case "restore" when e.Status == "ACKNOWLEDGED":
                    await uow.ExecuteAsync("UPDATE ops.ConsumptionEvent SET RestoredUtc = @now, Status = 'RESTORED' WHERE EventId = @EventId", new { now, e.EventId });
                    break;
                case "close" when e.Status == "RESTORED":
                    if (string.IsNullOrWhiteSpace(text)) throw new DomainException("Record the review: cause, lessons and follow-ups.");
                    await uow.ExecuteAsync("UPDATE ops.ConsumptionEvent SET ReviewNotes = @text, Status = 'CLOSED' WHERE EventId = @EventId", new { text = CommandRunner.Trim(text, 2000), e.EventId });
                    break;
                default:
                    throw new DomainException("That step does not follow from the event's current status.");
            }
            await run.AuditAsync(uow, "event." + step, "ConsumptionEvent", publicId, text);
        });

    // ---------------------------------------------------------------- findings

    public Task UpdateFindingAsync(Guid idem, Guid publicId, string action, string? text)
        => run.RunAsync(idem, "finding." + action, async uow =>
        {
            run.RequireAny(Permissions.Verify, Permissions.RegistryEdit);
            var status = action switch { "acknowledge" => "ACKNOWLEDGED", "resolve" => "RESOLVED", "accept" => "ACCEPTED", _ => throw new DomainException("Unknown action.") };
            if (action is "resolve" or "accept" && string.IsNullOrWhiteSpace(text)) throw new DomainException(action == "accept" ? "Accepting a finding needs the reason and who accepted the risk." : "Say how it was resolved.");
            if (action == "accept" && !Me.HasRole(Roles.PlatformOwner)) throw new DomainException("Only the Platform Owner accepts a finding without fixing it.");
            var n = await uow.ExecuteAsync(
                "UPDATE ops.Finding SET Status = @status, Resolution = COALESCE(@text, Resolution), ResolvedUtc = CASE WHEN @status IN ('RESOLVED','ACCEPTED') THEN @now ELSE ResolvedUtc END, ResolvedByPersonId = CASE WHEN @status IN ('RESOLVED','ACCEPTED') THEN @me ELSE ResolvedByPersonId END WHERE PublicId = @publicId AND Status IN ('OPEN','ACKNOWLEDGED')",
                new { status, text = CommandRunner.TrimN(text, 1000), now = clock.UtcNow, me = Me.PersonId, publicId });
            if (n == 0) throw new DomainException("The finding is already closed.");
            await run.AuditAsync(uow, "finding." + action, "Finding", publicId, text);
        });

    // ---------------------------------------------------------------- attestation (X6)

    /// <summary>
    /// Operator attestation of Microsoft 365 settings without a production API. Evidence is required; the attested values
    /// are compared with the template's expected values, and "Auto-apply new services" is always attested (C16).
    /// </summary>
    public Task<string?> AttestAsync(Guid idem, int templateId, int? bindingId, string subject, IReadOnlyDictionary<string, string> attested, string reason, UploadedFile? file)
        => run.RunAsync(idem, "attestation.record", async uow =>
        {
            run.Require(Permissions.Attest, null, "Platform Operations attests settings.");
            if (file is null) throw new DomainException("Attach the export or screenshot: attestation needs evidence.");
            if (reason is not ("CHANGE" or "MONTHLY")) throw new DomainException("Say why you are attesting: after a change or the monthly check.");
            if (!attested.ContainsKey("autoApplyNewServices") || string.IsNullOrWhiteSpace(attested["autoApplyNewServices"]))
                throw new DomainException("Attest the Auto-apply new services setting (C16).");
            var t = await uow.QuerySingleOrDefaultAsync<(int TemplateId, string SettingsJson)>("SELECT TemplateId, SettingsJson FROM core.ControlTemplate WHERE TemplateId = @templateId", new { templateId });
            if (t.TemplateId == 0) throw new DomainException("Template not found.");
            var binding = bindingId is int b ? await uow.QuerySingleOrDefaultAsync<(int WorkloadId, int FundingOwnerId)>("SELECT WorkloadId, FundingOwnerId FROM core.WorkloadBinding WHERE BindingId = @b", new { b }) : default;
            var expected = RequestCommands.Settings(t.SettingsJson);
            var diffs = new List<string>();
            foreach (var (key, exp) in expected)
            {
                attested.TryGetValue(key, out var act);
                if (!string.Equals((exp ?? "").Trim(), (act ?? "").Trim(), StringComparison.OrdinalIgnoreCase)) diffs.Add($"{key}: expected {exp}, attested {act ?? "nothing"}");
            }
            var publicId = Guid.NewGuid();
            var id = await uow.ScalarAsync<long>(
                """
                INSERT evd.Attestation (PublicId, FundingOwnerId, WorkloadId, BindingId, TemplateId, Subject, AttestedJson, MatchesTemplate, Differences, AttestedByPersonId, AttestedUtc, Reason)
                OUTPUT inserted.AttestationId
                VALUES (@publicId, @fo, @wl, @bindingId, @templateId, @subject, @json, @matches, @diffs, @me, @now, @reason)
                """,
                new
                {
                    publicId, fo = binding.FundingOwnerId == 0 ? (int?)null : binding.FundingOwnerId, wl = binding.WorkloadId == 0 ? (int?)null : binding.WorkloadId, bindingId, templateId,
                    subject = CommandRunner.Trim(subject, 400), json = JsonSerializer.Serialize(attested), matches = diffs.Count == 0, diffs = diffs.Count == 0 ? null : CommandRunner.Trim(string.Join("; ", diffs), 2000),
                    me = Me.PersonId, now = clock.UtcNow, reason,
                });
            await evidence.SaveAsync(uow, file, "EXPORT", $"Attestation: {subject}", binding.FundingOwnerId == 0 ? null : binding.FundingOwnerId, new[] { new EvidenceLinkTarget("ATTESTATION", id) });
            if (diffs.Count > 0)
            {
                var key = $"POLICY_DRIFT:{templateId}:{bindingId}";
                await uow.ExecuteAsync(
                    """
                    IF NOT EXISTS (SELECT 1 FROM ops.Finding WHERE DedupeKey = @key AND Status IN ('OPEN','ACKNOWLEDGED'))
                    INSERT ops.Finding (FindingType, Severity, FundingOwnerId, WorkloadId, ExternalId, Title, Details, Status, DedupeKey)
                    VALUES ('POLICY_DRIFT', 'HIGH', @fo, @wl, @ext, @title, @details, 'OPEN', @key)
                    """,
                    new { key, fo = binding.FundingOwnerId == 0 ? (int?)null : binding.FundingOwnerId, wl = binding.WorkloadId == 0 ? (int?)null : binding.WorkloadId, ext = bindingId?.ToString(),
                          title = $"Attested settings differ from the template: {subject}", details = CommandRunner.Trim(string.Join("; ", diffs), 2000) });
            }
            await run.AuditAsync(uow, "attestation.record", "Attestation", publicId, diffs.Count == 0 ? "matches template" : $"{diffs.Count} differences");
            return diffs.Count == 0 ? "Matches the template." : "Differs from the template: " + string.Join("; ", diffs);
        });

    // ---------------------------------------------------------------- recertification (I1, J6)

    public Task<string?> CreateCampaignAsync(Guid idem, string name, DateTime startsOn, DateTime dueOn)
        => run.RunAsync(idem, "campaign.create", async uow =>
        {
            if (!Me.HasRole(Roles.PlatformOwner)) throw new DomainException("The Platform Owner opens recertification campaigns.");
            if (dueOn < startsOn) throw new DomainException("The campaign is due before it starts.");
            var id = await uow.ScalarAsync<int>("INSERT wf.Campaign (CampaignType, Name, StartsOn, DueOn, Status, CreatedByPersonId) OUTPUT inserted.CampaignId VALUES ('RECERTIFICATION', @name, @startsOn, @dueOn, 'OPEN', @me)",
                new { name = CommandRunner.Trim(name, 200), startsOn = startsOn.Date, dueOn = dueOn.Date, me = Me.PersonId });
            // One item per live workload; the owner is the product owner, or the funding owner's business owner.
            var count = await uow.ExecuteAsync(
                """
                INSERT wf.CampaignItem (CampaignId, FundingOwnerId, WorkloadId, OwnerPersonId, Status)
                SELECT @id, w.FundingOwnerId, w.WorkloadId, COALESCE(w.ProductOwnerPersonId, fo.BusinessOwnerPersonId), 'OPEN'
                FROM core.Workload w JOIN core.FundingOwner fo ON fo.FundingOwnerId = w.FundingOwnerId
                WHERE w.State IN ('REGISTERED','LOCAL','DEVELOPMENT','TEST','PRODUCTION','SUSPENDED')
                """, new { id });
            var owners = (await uow.QueryAsync<int>("SELECT DISTINCT OwnerPersonId FROM wf.CampaignItem WHERE CampaignId = @id AND OwnerPersonId IS NOT NULL", new { id })).ToList();
            await outbox.EnqueueAsync(uow, new OutboxMessage
            {
                EventType = "N14", PersonIds = owners, Subject = $"Recertification due by {dueOn:d MMM yyyy}: {name}",
                Text = "Confirm need, audience, funding and outcome for each workload you own: renew, resize, reclassify or retire.", LinkPath = "/Recertification", ActivityType = "recertificationDue",
            }, $"N14:campaign:{id}");
            await run.AuditAsync(uow, "campaign.create", "Campaign", id, $"{count} items");
            return count.ToString();
        });

    /// <summary>
    /// I1: the product owner proposes the outcome (R); the funding owner's business owner (or shared-service owner) decides
    /// it (A, section 5.6). A business owner may decide directly. A decided RETIRE drafts a retirement request (J7).
    /// Returns PROPOSED, DECIDED or RETIRE_DRAFTED:&lt;request public ID&gt;.
    /// </summary>
    public Task<string?> CompleteCampaignItemAsync(Guid idem, int campaignItemId, string outcome, string evidenceText, UploadedFile? file)
        => run.RunAsync(idem, "campaign.item", async uow =>
        {
            if (outcome is not ("RENEW" or "RESIZE" or "RECLASSIFY" or "RETIRE")) throw new DomainException("Choose renew, resize, reclassify or retire.");
            if (string.IsNullOrWhiteSpace(evidenceText)) throw new DomainException("State the outcome evidence: is the workload delivering what was promised?");
            var i = await uow.QuerySingleOrDefaultAsync<(int Id, int FundingOwnerId, int? WorkloadId, int? OwnerPersonId, string Status, string? WorkloadName, long? TaskId, int? ProductOwnerPersonId)>(
                """
                SELECT i.CampaignItemId, i.FundingOwnerId, i.WorkloadId, i.OwnerPersonId, i.Status, w.Name, i.TaskId, w.ProductOwnerPersonId
                FROM wf.CampaignItem i LEFT JOIN core.Workload w ON w.WorkloadId = i.WorkloadId WHERE i.CampaignItemId = @campaignItemId
                """, new { campaignItemId });
            if (i.Id == 0) throw new DomainException("Item not found.");
            if (i.Status == "DONE") throw new DomainException("This item is already recertified.");
            var isBusinessOwner = Me.HasRoleIn(Roles.LobBusinessOwner, i.FundingOwnerId) || Me.HasRoleIn(Roles.SharedServiceOwner, i.FundingOwnerId);
            if (i.OwnerPersonId != Me.PersonId && !isBusinessOwner) throw new DomainException("The workload's owner proposes and the business owner decides its recertification.");
            if (file is not null)
                await evidence.SaveAsync(uow, file, "OUTCOME", "Recertification outcome evidence.", i.FundingOwnerId, new[] { new EvidenceLinkTarget("CAMPAIGN_ITEM", i.Id) });

            if (!isBusinessOwner)
            {
                // The product owner proposes; a decision task goes to the business owner in scope.
                await uow.ExecuteAsync("UPDATE wf.CampaignItem SET Outcome = @outcome, OutcomeEvidence = @ev WHERE CampaignItemId = @Id",
                    new { outcome, ev = CommandRunner.Trim(evidenceText, 2000), i.Id });
                if (i.TaskId is null)
                {
                    var ownerRole = await uow.ScalarAsync<string>("SELECT CASE OwnerType WHEN 'ENTERPRISE_SHARED' THEN 'SHARED_SERVICE_OWNER' ELSE 'LOB_BUSINESS_OWNER' END FROM core.FundingOwner WHERE FundingOwnerId = @FundingOwnerId", new { i.FundingOwnerId });
                    var due = clock.UtcNow.AddDays(7);
                    var taskId = await uow.ScalarAsync<long>(
                        """
                        INSERT wf.Task (FundingOwnerId, StageCode, TaskType, Title, AssignedRoleCode, ScopeType, ScopeId, Status, IsBlocking, StartedUtc, DueUtc,
                                        RelatedEntityType, RelatedEntityId, DecisionType, RaciActivityCode, Reason)
                        OUTPUT inserted.TaskId
                        VALUES (@FundingOwnerId, 'I1', 'DECISION', @title, @ownerRole, 'FUNDING_OWNER', @FundingOwnerId, 'OPEN', 1, @now, @due,
                                'CAMPAIGN_ITEM', @Id, 'RECERTIFICATION', 'LIFECYCLE_RETIREMENT', @reason)
                        """,
                        new
                        {
                            i.FundingOwnerId, title = CommandRunner.Trim($"Decide the recertification of {i.WorkloadName}", 300), ownerRole, now = clock.UtcNow, due, i.Id,
                            reason = CommandRunner.Trim($"The owner proposes: {outcome.ToLowerInvariant()}. Evidence: {evidenceText}", 1000),
                        });
                    await uow.ExecuteAsync("UPDATE wf.CampaignItem SET TaskId = @taskId WHERE CampaignItemId = @Id", new { taskId, i.Id });
                    await outbox.EnqueueAsync(uow, new OutboxMessage
                    {
                        EventType = "N14", FundingOwnerId = i.FundingOwnerId, TaskId = taskId,
                        Roles = { new RoleTarget(ownerRole ?? Roles.LobBusinessOwner, ScopeTypes.FundingOwner, i.FundingOwnerId) },
                        Subject = $"Recertification to decide: {i.WorkloadName}", Text = $"The owner proposes to {outcome.ToLowerInvariant()} it. You decide.",
                        LinkPath = "/Recertification", ActivityType = "recertificationDue",
                    }, $"N14:item:{i.Id}");
                }
                await run.AuditAsync(uow, "campaign.propose", "CampaignItem", i.Id, outcome);
                return "PROPOSED";
            }

            // The business owner decides.
            var role = Me.HasRoleIn(Roles.SharedServiceOwner, i.FundingOwnerId) ? Roles.SharedServiceOwner : Roles.LobBusinessOwner;
            var decisionId = await decisions.RecordAsync(uow, DecisionTypes.Recertification, outcome == "RETIRE" ? Outcomes.Declined : Outcomes.Extend, role, "LIFECYCLE_RETIREMENT",
                $"Recertification of {i.WorkloadName}: {outcome.ToLowerInvariant()}", i.FundingOwnerId, i.WorkloadId, reason: evidenceText, nextReviewOn: outcome == "RETIRE" ? null : clock.UtcNow.Date.AddYears(1));
            await uow.ExecuteAsync("UPDATE wf.CampaignItem SET Status = 'DONE', Outcome = @outcome, OutcomeEvidence = @ev, DecisionId = @decisionId, CompletedUtc = @now WHERE CampaignItemId = @Id",
                new { outcome, ev = CommandRunner.Trim(evidenceText, 2000), decisionId, now = clock.UtcNow, i.Id });
            await uow.ExecuteAsync("UPDATE wf.Task SET Status = 'DONE', Outcome = @outcome, CompletedByPersonId = @me, CompletedUtc = @now WHERE RelatedEntityType = 'CAMPAIGN_ITEM' AND RelatedEntityId = @Id AND Status = 'OPEN'",
                new { outcome, me = Me.PersonId, now = clock.UtcNow, i.Id });
            if (i.WorkloadId is int wid && outcome != "RETIRE")
                await uow.ExecuteAsync("UPDATE core.Workload SET NextReviewOn = @next WHERE WorkloadId = @wid", new { next = clock.UtcNow.Date.AddYears(1), wid });
            await run.AuditAsync(uow, "campaign.item", "CampaignItem", i.Id, outcome);

            if (outcome == "RETIRE" && i.WorkloadId is int retireId)
            {
                // A retirement request (J7) is drafted for the workload's product owner (or the item's owner) to review and submit.
                var requester = i.ProductOwnerPersonId ?? i.OwnerPersonId ?? Me.PersonId;
                var onBehalf = new OnBehalfPrincipal(requester, Me);
                var payload = new Requests.RequestPayload
                {
                    Purpose = $"Retire {i.WorkloadName}: decided at recertification.",
                    ExistingWorkloadPublicId = await uow.ScalarAsync<Guid>("SELECT PublicId FROM core.Workload WHERE WorkloadId = @retireId", new { retireId }),
                    RetirementReason = CommandRunner.Trim($"Recertification decided to retire it. {evidenceText}", 1000),
                };
                var draft = await engine.CreateDraftAsync(uow, onBehalf, Journeys.Retirement, CommandRunner.Trim($"Retire {i.WorkloadName}", 200), i.FundingOwnerId,
                    null, Me.PersonId == requester ? null : Me.PersonId, retireId, payload);
                await outbox.EnqueueAsync(uow, new OutboxMessage
                {
                    EventType = "N06", RequestId = draft.RequestId, FundingOwnerId = i.FundingOwnerId, PersonIds = { requester },
                    Subject = $"{draft.RequestNumber}: retirement drafted for {i.WorkloadName}", Text = "Recertification decided to retire this workload. Add the retirement plan and submit the request.",
                    LinkPath = $"/Requests/{draft.PublicId}",
                }, $"N06:retire:{i.Id}");
                return $"RETIRE_DRAFTED:{draft.PublicId}";
            }
            return "DECIDED";
        });

    // ---------------------------------------------------------------- exceptions

    public Task CloseExceptionAsync(Guid idem, Guid publicId, string reason)
        => run.RunAsync(idem, "exception.close", async uow =>
        {
            var x = await uow.QuerySingleOrDefaultAsync<(int Id, int OwnerPersonId, string Status)>("SELECT ExceptionId, OwnerPersonId, Status FROM wf.ExceptionItem WHERE PublicId = @publicId", new { publicId });
            if (x.Id == 0) throw new DomainException("Exception not found.");
            if (x.Status != "ACTIVE") throw new DomainException("This exception is not active.");
            if (x.OwnerPersonId != Me.PersonId && !Me.HasRole(Roles.PlatformOwner)) throw new DomainException("The exception owner or the Platform Owner closes it.");
            if (string.IsNullOrWhiteSpace(reason)) throw new DomainException("Say why the exception is no longer needed.");
            await uow.ExecuteAsync("UPDATE wf.ExceptionItem SET Status = 'CLOSED', ClosedUtc = @now WHERE ExceptionId = @Id", new { now = clock.UtcNow, x.Id });
            await run.AuditAsync(uow, "exception.close", "Exception", publicId, reason);
        });
}
