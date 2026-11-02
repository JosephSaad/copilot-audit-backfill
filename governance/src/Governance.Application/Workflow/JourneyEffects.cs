using System.Text.Json;
using Governance.Application.Data;
using Governance.Application.Outbox;
using Governance.Application.Requests;
using Governance.Application.Rules;
using Governance.Application.Sla;
using Governance.Domain;

namespace Governance.Application.Workflow;

/// <summary>Loads the business calendar (time zone setting and holidays), cached for ten minutes.</summary>
public sealed class BusinessCalendarProvider(IClock clock)
{
    private BusinessCalendar? _cached;
    private DateTime _loadedUtc;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public async Task<BusinessCalendar> GetAsync(Uow uow)
    {
        if (_cached is not null && clock.UtcNow - _loadedUtc < TimeSpan.FromMinutes(10)) return _cached;
        await _lock.WaitAsync();
        try
        {
            if (_cached is not null && clock.UtcNow - _loadedUtc < TimeSpan.FromMinutes(10)) return _cached;
            var zone = await uow.ScalarAsync<string>("SELECT SettingValue FROM ref.Setting WHERE SettingKey = 'Calendar.TimeZone'") ?? "UTC";
            var holidays = (await uow.QueryAsync<DateTime>("SELECT HolidayDate FROM ref.Holiday")).Select(DateOnly.FromDateTime);
            _cached = BusinessCalendar.Create(zone, holidays);
            _loadedUtc = clock.UtcNow;
            return _cached;
        }
        finally { _lock.Release(); }
    }

    public void Invalidate() => _cached = null;
}

/// <summary>
/// What each journey changes outside the request when it moves: the workload record, envelope commitments,
/// the exception register, approved capacity posture, ITSM tickets. Kept apart from the state machine so the
/// engine stays generic.
/// </summary>
public sealed class JourneyEffects(IClock clock, OutboxWriter outbox)
{
    public sealed record PatternEvaluation(PatternFit.Result Result, int? EnvelopeId);

    // ------------------------------------------------------------------------------------------
    // Entering a stage
    // ------------------------------------------------------------------------------------------

    public async Task OnEnterAsync(Uow uow, RequestRow r, StageDefinition stage, ICurrentPrincipal actor)
    {
        // J2: the workload exists from classification on (Registered), so B3 classifies a real record.
        if (r.JourneyCode == Journeys.NewWorkload && stage.StageCode == Stages.Classify && r.WorkloadId is null)
        {
            var p = RequestPayload.FromJson(r.PayloadJson);
            var workloadId = await uow.ScalarAsync<int>(
                """
                INSERT core.Workload (Name, WorkloadType, FundingOwnerId, ServiceId, TriggerType, DataClass, Audience, LifecycleStage,
                                      EnvironmentClassCode, ProductOwnerPersonId, SponsorPersonId, SupportContact, State)
                OUTPUT inserted.WorkloadId
                VALUES (@Name, @Type, @FundingOwnerId, @ServiceId, @TriggerType, @DataClass, @Audience, @Stage,
                        @EnvClass, @Requester, @SponsorPersonId, @SupportContact, 'REGISTERED')
                """,
                new
                {
                    Name = (p.WorkloadName ?? r.Title).Length > 200 ? (p.WorkloadName ?? r.Title)[..200] : (p.WorkloadName ?? r.Title),
                    Type = p.WorkloadType ?? "AGENT", r.FundingOwnerId, r.ServiceId, p.TriggerType, p.DataClass,
                    Audience = p.Audience is null ? null : Audiences.Labels.GetValueOrDefault(p.Audience, p.Audience),
                    Stage = p.RequestedLifecycleStage, EnvClass = p.EnvironmentClassCode, Requester = r.RequesterPersonId, r.SponsorPersonId, p.SupportContact,
                });
            foreach (var m in p.Meters.Distinct())
                await uow.ExecuteAsync("INSERT core.WorkloadMeter (WorkloadId, MeterCode) VALUES (@workloadId, @m)", new { workloadId, m });
            await uow.ExecuteAsync("UPDATE wf.Request SET WorkloadId = @workloadId WHERE RequestId = @RequestId", new { workloadId, r.RequestId });
        }
    }

    public async Task<PatternEvaluation> EvaluatePatternAsync(Uow uow, RequestRow r)
    {
        var p = RequestPayload.FromJson(r.PayloadJson);
        var service = r.ServiceId is int sid ? await uow.QuerySingleOrDefaultAsync<Service>("SELECT * FROM core.Service WHERE ServiceId = @sid", new { sid }) : null;
        var workload = r.WorkloadId is int wid ? await uow.QuerySingleOrDefaultAsync<Workload>("SELECT * FROM core.Workload WHERE WorkloadId = @wid", new { wid }) : null;
        var serviceId = r.ServiceId ?? workload?.ServiceId;
        var patterns = (await uow.QueryAsync<ApprovedPattern>("SELECT * FROM core.ApprovedPattern WHERE IsActive = 1")).ToList();
        var today = clock.UtcNow.Date;
        var envelope = serviceId is null ? null : await uow.QueryFirstOrDefaultAsync<Envelope>(
            """
            SELECT TOP (1) e.*, COALESCE((SELECT SUM(c.Amount) FROM core.EnvelopeCommitment c WHERE c.EnvelopeId = e.EnvelopeId AND c.Status = 'ACTIVE'), 0) AS Committed
            FROM core.FundingEnvelope e
            WHERE e.FundingOwnerId = @FundingOwnerId AND e.ServiceId = @serviceId AND e.Status = 'ACTIVE' AND e.PeriodStart <= @today AND e.PeriodEnd >= @today
              AND e.Unit = 'COPILOT_CREDIT'   -- Phase 1 envelopes are in credits (OD12); other units need a dated rate (E8)
            ORDER BY e.PeriodEnd
            """, new { r.FundingOwnerId, serviceId, today });

        // A monthly figure is committed for the months it runs inside the envelope period, not once.
        // J4 asks for an increase: the envelope must hold the difference over the same months.
        decimal monthly = r.JourneyCode == Journeys.CapacityChange
            ? Math.Max(0, (p.RequestedValue ?? 0) - (p.CurrentValue ?? 0))
            : p.EstimateCreditsPerMonth ?? 0;
        var monthsLeft = envelope is null ? 12 : Math.Max(1, ((envelope.PeriodEnd.Year - today.Year) * 12) + envelope.PeriodEnd.Month - today.Month + 1);
        var months = Math.Min(p.DurationMonths is > 0 ? p.DurationMonths.Value : 12, monthsLeft);
        decimal estimate = monthly * months;
        var headroom = EnvelopeHeadroom.Evaluate(envelope, estimate, Units.CopilotCredit);

        PatternFit.EnvironmentLimits? limits = null;
        var envClass = p.EnvironmentClassCode ?? workload?.EnvironmentClassCode;
        if (envClass is not null)
            limits = await uow.QuerySingleOrDefaultAsync<PatternFit.EnvironmentLimits>(
                "SELECT EnvironmentClassCode AS ClassCode, DefaultAgentLimitCredits AS DefaultAgentLimit, TenantPoolAllowed, PaygAllowed FROM core.EnvironmentClass WHERE EnvironmentClassCode = @envClass", new { envClass });

        // EstimateCredits holds the amount committed against the envelope if approved (monthly x months).
        await uow.ExecuteAsync("UPDATE wf.Request SET EstimateCredits = @estimate WHERE RequestId = @RequestId", new { estimate, r.RequestId });

        var result = PatternFit.Evaluate(new PatternFit.Facts(r.JourneyCode, p, service, workload, patterns, headroom, limits));
        return new PatternEvaluation(result, envelope?.EnvelopeId);
    }

    /// <summary>J3: the conditions an exception request declares, so B8 opens the right reviews.</summary>
    public IEnumerable<string> ExceptionTriggers(RequestPayload p)
    {
        if (p.CustomerFacing || p.Audience == Audiences.Customers) yield return PatternFit.Conditions.CustomerFacing;
        if (p.NewConnectorOrIntegration) yield return PatternFit.Conditions.Connector;
        if (DataClasses.Rank(p.DataClass) >= DataClasses.Rank(DataClasses.Confidential)) yield return PatternFit.Conditions.DataClass;
        if (p.NewBillingScope) yield return PatternFit.Conditions.BillingScope;
        if (p.TriggerType is TriggerTypes.Autonomous or TriggerTypes.Scheduled) yield return PatternFit.Conditions.Trigger;
        if (p.UsesGitHubHarness) yield return PatternFit.Conditions.GitHubHarness;
        if (p.RequestedLifecycleStage == LifecycleStages.Production) yield return PatternFit.Conditions.ProductionChange;
        yield return PatternFit.Conditions.NotInCatalogue;
    }

    // ------------------------------------------------------------------------------------------
    // Fulfillment (B10 -> ITSM)
    // ------------------------------------------------------------------------------------------

    public async Task CreateFulfillmentTicketAsync(Uow uow, RequestRow r)
    {
        var p = RequestPayload.FromJson(r.PayloadJson);
        var decisions = (await uow.QueryAsync<(Guid PublicId, string DecisionType, string Outcome, string? ConditionsText)>(
            "SELECT PublicId, DecisionType, Outcome, ConditionsText FROM wf.Decision WHERE RequestId = @RequestId AND Outcome IN ('APPROVED','APPROVED_WITH_CONDITIONS','CONSTRAINED_PILOT','EXTEND') ORDER BY DecisionId",
            new { r.RequestId })).ToList();
        if (decisions.Count == 0) throw new DomainException("No decision record exists, so no fulfillment ticket is created (X8).");
        var serviceId = r.ServiceId ?? await uow.ScalarAsync<int?>("SELECT ServiceId FROM core.Workload WHERE WorkloadId = @WorkloadId", new { r.WorkloadId });
        var template = serviceId is null ? default : await uow.QueryFirstOrDefaultAsync<(int TemplateId, int VersionNo, string SettingsJson)>(
            "SELECT TemplateId, VersionNo, SettingsJson FROM core.ControlTemplate WHERE ServiceId = @serviceId AND Status = 'ACTIVE'", new { serviceId });
        var workload = r.WorkloadId is int wid ? await uow.QuerySingleOrDefaultAsync<Workload>("SELECT * FROM core.Workload WHERE WorkloadId = @wid", new { wid }) : null;

        var settings = r.JourneyCode switch
        {
            Journeys.CapacityChange => new Dictionary<string, object?>
            {
                ["change"] = p.ChangeKind, ["environmentId"] = p.EnvironmentId, ["from"] = p.CurrentValue, ["to"] = p.RequestedValue,
                ["tenantPool"] = p.RequestTenantPool ? "enabled (approved)" : "disabled",
            },
            Journeys.Retirement => new Dictionary<string, object?>
            {
                ["action"] = "Disable the workload, remove access, release allocations", ["workload"] = workload?.Name, ["plan"] = p.RetirementPlan,
            },
            _ => template.SettingsJson is null ? new() : JsonSerializer.Deserialize<Dictionary<string, object?>>(template.SettingsJson) ?? new(),
        };
        var correlation = Guid.NewGuid();
        var ticketPayload = JsonSerializer.Serialize(new
        {
            shortDescription = $"{r.RequestNumber}: {(r.JourneyCode == Journeys.Retirement ? "Retire" : "Configure")} {workload?.Name ?? r.Title}",
            requestNumber = r.RequestNumber,
            decisions = decisions.Select(d => new { id = d.PublicId, type = d.DecisionType, outcome = d.Outcome, conditions = d.ConditionsText }),
            controlTemplate = template.TemplateId == 0 ? null : new { id = template.TemplateId, version = template.VersionNo },
            settings,
            preApproved = true,
            note = "Pre-approved fulfillment. Do not approve again in ITSM. Closing this ticket sends the change to verification in the Workbench.",
        });
        var ticketId = await uow.ScalarAsync<long>(
            """
            INSERT wf.ItsmTicket (FundingOwnerId, RequestId, Kind, CorrelationId, State, Payload)
            OUTPUT inserted.ItsmTicketId
            VALUES (@FundingOwnerId, @RequestId, 'TASK', @correlation, 'PENDING_CREATE', @ticketPayload)
            """, new { r.FundingOwnerId, r.RequestId, correlation, ticketPayload });
        await outbox.EnqueueAsync(uow, new OutboxMessage { EventType = "ITSM.Create", ItsmTicketId = ticketId, RequestId = r.RequestId }, $"ITSM.Create:{ticketId}");
        await outbox.EnqueueAsync(uow, new OutboxMessage
        {
            EventType = "N07", RequestId = r.RequestId, FundingOwnerId = r.FundingOwnerId, Queues = { "OPERATIONS" },
            Roles = { new RoleTarget(Roles.PlatformOperator, ScopeTypes.Tenant, null) }, DigestOnly = false,
            Subject = $"{r.RequestNumber}: fulfillment task created", Text = "An ITSM task with the exact settings to apply was created. Configure in the admin center, then close the ticket.",
            LinkPath = $"/Requests/{r.PublicId}",
        }, $"N07:ticket:{ticketId}");
    }

    // ------------------------------------------------------------------------------------------
    // Transitions
    // ------------------------------------------------------------------------------------------

    public async Task ApplyAsync(Uow uow, RequestRow r, string fromStage, string outcome, string toStage, ICurrentPrincipal actor)
    {
        var p = RequestPayload.FromJson(r.PayloadJson);
        var today = clock.UtcNow.Date;
        var positive = Outcomes.Positive.Contains(outcome) || outcome == Outcomes.Approved;

        // An approval (B5, B9, I4, F4) commits the estimate against the envelope and dates the workload's next review.
        if (positive && fromStage is Stages.RoutineApproval or Stages.Disposition)
        {
            var last = await uow.QueryFirstOrDefaultAsync<DecisionRow>("SELECT TOP (1) * FROM wf.Decision WHERE RequestId = @RequestId ORDER BY DecisionId DESC", new { r.RequestId });
            if (r.EnvelopeId is int env && (r.EstimateCredits ?? 0) > 0
                && await uow.ScalarAsync<int>("SELECT COUNT(*) FROM core.EnvelopeCommitment WHERE RequestId = @RequestId AND Status = 'ACTIVE'", new { r.RequestId }) == 0)
            {
                var unit = await uow.ScalarAsync<string>("SELECT Unit FROM core.FundingEnvelope WHERE EnvelopeId = @env", new { env });
                await uow.ExecuteAsync("INSERT core.EnvelopeCommitment (EnvelopeId, FundingOwnerId, RequestId, WorkloadId, Amount, Unit, Status) VALUES (@env, @FundingOwnerId, @RequestId, @WorkloadId, @EstimateCredits, @unit, 'ACTIVE')",
                    new { env, r.FundingOwnerId, r.RequestId, r.WorkloadId, r.EstimateCredits, unit });
            }
            if (r.WorkloadId is int wid && last is not null)
                await uow.ExecuteAsync("UPDATE core.Workload SET NextReviewOn = COALESCE(@NextReviewOn, NextReviewOn), ExpiresOn = COALESCE(@ExpiresOn, ExpiresOn) WHERE WorkloadId = @wid",
                    new { last.NextReviewOn, last.ExpiresOn, wid });

            // J3: an approved exception enters the register with its compensating controls, owner and expiry.
            if (r.JourneyCode == Journeys.Exception && toStage == Stages.End)
            {
                // Every positive decision of the final B9 round contributes its conditions; the earliest expiry wins.
                var roundStart = await uow.ScalarAsync<DateTime?>(
                    "SELECT MAX(Utc) FROM wf.RequestEvent WHERE RequestId = @RequestId AND EventType = 'TRANSITION' AND StageCode = 'B9' AND ISNULL(Text, '') <> 'HOLD'",
                    new { r.RequestId }) ?? DateTime.MinValue;
                var round = (await uow.QueryAsync<DecisionRow>(
                    "SELECT * FROM wf.Decision WHERE RequestId = @RequestId AND StageCode = 'B9' AND DecidedAtUtc >= @roundStart AND Outcome IN ('APPROVED','APPROVED_WITH_CONDITIONS','CONSTRAINED_PILOT') ORDER BY DecisionId",
                    new { r.RequestId, roundStart })).GroupBy(d => d.DecisionType).Select(g => g.Last()).ToList();
                var disposition = round.FirstOrDefault(d => d.DecisionType == DecisionTypes.ExceptionDisposition)
                                  ?? round.FirstOrDefault(d => d.DecisionType == DecisionTypes.PlatformFeature) ?? round.LastOrDefault();
                var conditions = string.Join(" ", round.Where(d => !string.IsNullOrWhiteSpace(d.ConditionsText))
                                                       .Select(d => $"{DecisionTypes.Label(d.DecisionType)}: {d.ConditionsText!.Trim()}"));
                var expires = round.Where(d => d.ExpiresOn is not null).Select(d => d.ExpiresOn!.Value).DefaultIfEmpty(p.RequestedExpiry ?? today.AddMonths(6)).Min();
                await uow.ExecuteAsync(
                    """
                    INSERT wf.ExceptionItem (FundingOwnerId, WorkloadId, RequestId, DecisionId, Title, Description, CompensatingControls, OwnerPersonId, ExpiresOn, NextReviewOn, Status)
                    VALUES (@FundingOwnerId, @WorkloadId, @RequestId, @DecisionId, @Title, @Description, @Controls, @Owner, @expires, @NextReviewOn, 'ACTIVE')
                    """,
                    new
                    {
                        r.FundingOwnerId, r.WorkloadId, r.RequestId, disposition?.DecisionId, r.Title,
                        Description = p.RequestedDeviation ?? r.Summary ?? r.Title,
                        Controls = conditions.Length > 0 ? (conditions.Length > 2000 ? conditions[..2000] : conditions) : p.ProposedCompensatingControls ?? "None stated.",
                        Owner = r.SponsorPersonId ?? r.RequesterPersonId, expires,
                        NextReviewOn = round.Where(d => d.NextReviewOn is not null).Select(d => d.NextReviewOn).Min(),
                    });
            }
        }

        switch (r.JourneyCode, fromStage, outcome)
        {
            // J2 verified: the workload is live at the requested builder stage; a production request waits for D5 in Test.
            case (Journeys.NewWorkload, Stages.Verify, StageOutcomes.Pass):
                await SetWorkloadStateAsync(uow, r.WorkloadId, StageToState(p.RequestedLifecycleStage ?? LifecycleStages.Development), p.RequestedLifecycleStage);
                break;
            case (Journeys.NewWorkload, Stages.Verify, StageOutcomes.PassGate):
                await SetWorkloadStateAsync(uow, r.WorkloadId, WorkloadStates.Test, LifecycleStages.Test);
                break;
            case (Journeys.NewWorkload, Stages.ProductionAcceptance, StageOutcomes.Accepted):
                await SetWorkloadStateAsync(uow, r.WorkloadId, WorkloadStates.Production, LifecycleStages.Production);
                break;

            // J4 verified: the approved posture moves to the new value (versioned by effective date).
            case (Journeys.CapacityChange, Stages.Verify, StageOutcomes.Pass) when r.WorkloadId is int wid4:
                await UpdatePostureAsync(uow, r, wid4, p, today);
                break;

            // J9: an approved seat change writes the LOB's new reservation (effective date, previous, approver, flex-pool review).
            case (Journeys.SeatReservation, Stages.SeatDecision, Outcomes.Approved):
                await Seats.SeatRequests.ApplyAsync(uow, r, today);
                break;

            // J7: decision puts the workload in Retiring; closing commitments retires it and ends its bindings.
            case (Journeys.Retirement, Stages.RetirementDecision, Outcomes.Approved):
                await SetWorkloadStateAsync(uow, r.WorkloadId, WorkloadStates.Retiring, null);
                break;
            case (Journeys.Retirement, Stages.CloseCommitments, StageOutcomes.Closed) when r.WorkloadId is int wid7:
                await SetWorkloadStateAsync(uow, wid7, WorkloadStates.Retired, null);
                await uow.ExecuteAsync("UPDATE core.EnvelopeCommitment SET Status = 'RELEASED' WHERE WorkloadId = @wid7 AND Status = 'ACTIVE'", new { wid7 });
                await uow.ExecuteAsync("UPDATE core.WorkloadBinding SET EffectiveTo = @today WHERE WorkloadId = @wid7 AND EffectiveTo IS NULL", new { wid7, today });
                await uow.ExecuteAsync("UPDATE core.ApprovedPosture SET EffectiveTo = @today WHERE WorkloadId = @wid7 AND EffectiveTo IS NULL", new { wid7, today });
                break;
        }

        // Declined, withdrawn or closed requests release any commitment they held.
        if (toStage == Stages.End && outcome is StageOutcomes.Withdraw or StageOutcomes.AutoClose or Outcomes.Declined)
            await uow.ExecuteAsync("UPDATE core.EnvelopeCommitment SET Status = 'RELEASED' WHERE RequestId = @RequestId AND Status = 'ACTIVE'", new { r.RequestId });
    }

    private static string StageToState(string stage) => stage switch
    {
        LifecycleStages.Local => WorkloadStates.Local,
        LifecycleStages.Test => WorkloadStates.Test,
        LifecycleStages.Production => WorkloadStates.Test,   // production only through D5
        _ => WorkloadStates.Development,
    };

    private static Task SetWorkloadStateAsync(Uow uow, int? workloadId, string state, string? lifecycleStage)
        => workloadId is null ? Task.CompletedTask : uow.ExecuteAsync(
            "UPDATE core.Workload SET State = @state, LifecycleStage = COALESCE(@lifecycleStage, LifecycleStage) WHERE WorkloadId = @workloadId",
            new { workloadId, state, lifecycleStage });

    private async Task UpdatePostureAsync(Uow uow, RequestRow r, int workloadId, RequestPayload p, DateTime today)
    {
        if (p.ChangeKind == "ENVELOPE") return;   // envelopes change in the register, not in Microsoft settings
        var current = await uow.QueryFirstOrDefaultAsync<(int PostureId, string EnvironmentId, decimal AllocationCredits, bool TenantPoolEnabled, bool PaygLinked, string? BillingPolicyId, decimal? AgentLimitCredits, bool GroupRuleLocksTenantPool)>(
            "SELECT PostureId, EnvironmentId, AllocationCredits, TenantPoolEnabled, PaygLinked, BillingPolicyId, AgentLimitCredits, GroupRuleLocksTenantPool FROM core.ApprovedPosture WHERE WorkloadId = @workloadId AND EffectiveTo IS NULL",
            new { workloadId });
        var envId = p.EnvironmentId ?? current.EnvironmentId;
        if (envId is null) return;
        if (current.PostureId == 0)
        {
            // No posture yet: start from the environment class defaults, never from zero (a zero would raise false drift).
            var envClass = await uow.ScalarAsync<string>("SELECT EnvironmentClassCode FROM core.Workload WHERE WorkloadId = @workloadId", new { workloadId });
            var defaults = await uow.QuerySingleOrDefaultAsync<(decimal DefaultAllocationCredits, decimal DefaultAgentLimitCredits)>(
                "SELECT DefaultAllocationCredits, DefaultAgentLimitCredits FROM core.EnvironmentClass WHERE EnvironmentClassCode = @envClass", new { envClass });
            current.AllocationCredits = defaults.DefaultAllocationCredits;
            current.AgentLimitCredits = defaults.DefaultAgentLimitCredits == 0 ? null : defaults.DefaultAgentLimitCredits;
        }
        var decision = await uow.ScalarAsync<long?>("SELECT MAX(DecisionId) FROM wf.Decision WHERE RequestId = @RequestId", new { r.RequestId });
        if (current.PostureId != 0)
            await uow.ExecuteAsync("UPDATE core.ApprovedPosture SET EffectiveTo = @yesterday WHERE PostureId = @PostureId", new { yesterday = today.AddDays(-1), current.PostureId });
        await uow.ExecuteAsync(
            """
            INSERT core.ApprovedPosture (WorkloadId, FundingOwnerId, EnvironmentId, AllocationCredits, TenantPoolEnabled, PaygLinked, BillingPolicyId, AgentLimitCredits, GroupRuleLocksTenantPool, DecisionId, EffectiveFrom)
            VALUES (@workloadId, @FundingOwnerId, @envId, @alloc, @pool, @payg, @billing, @limit, @lock, @decision, @today)
            """,
            new
            {
                workloadId, r.FundingOwnerId, envId,
                alloc = p.ChangeKind == "ALLOCATION" ? p.RequestedValue ?? current.AllocationCredits : current.AllocationCredits,
                pool = p.RequestTenantPool || current.TenantPoolEnabled,
                payg = current.PaygLinked, billing = current.BillingPolicyId,
                limit = p.ChangeKind == "AGENT_LIMIT" ? p.RequestedValue : current.AgentLimitCredits,
                @lock = current.GroupRuleLocksTenantPool, decision, today,
            });
    }

    // ------------------------------------------------------------------------------------------
    // Vacancies (section 7.3 step 6, N20)
    // ------------------------------------------------------------------------------------------

    public async Task RaiseVacancyAsync(Uow uow, RequestRow r, TaskRow task, string roleCode)
    {
        var key = $"VACANCY:{roleCode}:{task.ScopeType}:{task.ScopeId}";
        var findingId = await uow.ScalarAsync<long?>(
            """
            IF NOT EXISTS (SELECT 1 FROM ops.Finding WHERE DedupeKey = @key AND Status IN ('OPEN','ACKNOWLEDGED'))
                INSERT ops.Finding (FindingType, Severity, FundingOwnerId, Title, Details, Status, DedupeKey)
                OUTPUT inserted.FindingId
                VALUES ('VACANCY', 'HIGH', @fo, @title, @details, 'OPEN', @key);
            """,
            new
            {
                key, fo = task.ScopeType == ScopeTypes.FundingOwner ? task.ScopeId : null,
                title = $"Nobody can act as {roleCode.Replace('_', ' ').ToLowerInvariant()}",
                details = $"{r.RequestNumber} is waiting at {task.StageCode} and no eligible person holds the role in this scope. The task escalates on its ladder until the role is filled.",
            });
        if (findingId is long fid)
            await outbox.EnqueueAsync(uow, new OutboxMessage
            {
                EventType = "N20", FindingId = fid, RequestId = r.RequestId, FundingOwnerId = r.FundingOwnerId,
                Roles = { new RoleTarget(Roles.PlatformOwner, ScopeTypes.Tenant, null), new RoleTarget(Roles.LobBusinessOwner, ScopeTypes.FundingOwner, r.FundingOwnerId) },
                Subject = $"Role vacancy: {roleCode.Replace('_', ' ').ToLowerInvariant()}", Text = $"{r.RequestNumber} is waiting because nobody holds this role in scope.",
                LinkPath = "/Operations/Findings",
            }, $"N20:{fid}");
    }
}
