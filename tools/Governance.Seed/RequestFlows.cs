using Governance.Application.Data;
using Governance.Application.Requests;
using Governance.Application.Workflow;
using Governance.Domain;

namespace Governance.Seed;

/// <summary>Drives requests through the real engine as personas: one transaction per step, as the web would.</summary>
public sealed class RequestFlows(EngineHost host, SeedResult seed)
{
    private WorkflowEngine E => host.Engine;

    public Task<RequestRow> DraftAsync(string requesterKey, string journey, string title, string lob, string? serviceCode, string? sponsorKey,
        int? workloadId, RequestPayload payload)
        => host.AsAsync(seed.As(requesterKey), u => E.CreateDraftAsync(u, seed.As(requesterKey), journey, title, seed.FundingOwners[lob],
            serviceCode is null ? null : seed.Services[serviceCode], sponsorKey is null ? null : seed.Person(sponsorKey), workloadId, payload));

    public async Task<RequestRow> SubmitAsync(string requesterKey, string journey, string title, string lob, string? serviceCode, string? sponsorKey,
        int? workloadId, RequestPayload payload)
    {
        var r = await DraftAsync(requesterKey, journey, title, lob, serviceCode, sponsorKey, workloadId, payload);
        await host.AsAsync(seed.As(requesterKey), u => E.SubmitAsync(u, seed.As(requesterKey), r.RequestId));
        return await GetAsync(r.RequestId);
    }

    public Task<RequestRow> GetAsync(long requestId) => host.SystemAsync(u => E.GetRequestAsync(u, requestId));

    /// <summary>The open task at a stage (optionally of a type, decision type or discipline).</summary>
    public async Task<TaskRow> TaskAsync(long requestId, string stage, string? taskType = null, string? decisionType = null, string? discipline = null)
    {
        var tasks = await OpenTasksAsync(requestId);
        return tasks.FirstOrDefault(t => t.StageCode == stage && (taskType is null || t.TaskType == taskType)
                                         && (decisionType is null || t.DecisionType == decisionType) && (discipline is null || t.Discipline == discipline))
               ?? throw new InvalidOperationException($"No open task at {stage} ({taskType} {decisionType} {discipline}) on request {requestId}. Open: "
                                                      + string.Join(", ", tasks.Select(t => $"{t.StageCode}/{t.TaskType}/{t.DecisionType}/{t.Discipline}")));
    }

    public Task<List<TaskRow>> OpenTasksAsync(long requestId)
        => host.SystemAsync(async u => (await u.QueryAsync<TaskRow>("SELECT * FROM wf.Task WHERE RequestId = @requestId AND Status = 'OPEN' ORDER BY TaskId", new { requestId })).ToList());

    public async Task CompleteAsync(string personaKey, long requestId, string stage, string outcome, string? notes = null, string? taskType = null, string? discipline = null)
    {
        var t = await TaskAsync(requestId, stage, taskType, discipline: discipline);
        await host.AsAsync(seed.As(personaKey), u => E.CompleteWorkAsync(u, seed.As(personaKey), t.TaskId, outcome, notes));
    }

    public Task CompleteTaskAsync(string personaKey, long taskId, string outcome, string? notes = null)
        => host.AsAsync(seed.As(personaKey), u => E.CompleteWorkAsync(u, seed.As(personaKey), taskId, outcome, notes));

    public async Task<long> DecideAsync(string personaKey, long requestId, string stage, DecisionInput input, string? decisionType = null)
    {
        var t = await TaskAsync(requestId, stage, TaskTypes.Decision, decisionType);
        return await host.AsAsync(seed.As(personaKey), u => E.DecideAsync(u, seed.As(personaKey), t.TaskId, input));
    }

    public DecisionInput Approve(string scope, string? conditions = null, int reviewMonths = 6, DateTime? expires = null)
        => new(conditions is null ? Outcomes.Approved : Outcomes.ApprovedWithConditions, scope, conditions, null, null,
            expires, host.Clock.UtcNow.Date.AddMonths(reviewMonths));

    public static DecisionInput Hold(string scope, string reason, string evidence) => new(Outcomes.Hold, scope, null, reason, evidence, null, null);

    public static DecisionInput Decline(string scope, string reason, string evidence) => new(Outcomes.Declined, scope, null, reason, evidence, null, null);

    /// <summary>B3: the architect records the four independent classification fields on the workload.</summary>
    public Task ClassifyAsync(long requestId, string costCenter, string solutionClass, string lifecycleStage, string criticality, string dataBoundary,
        string audience, bool metersNone = true)
        => host.SystemAsync(async u =>
        {
            var r = await E.GetRequestAsync(u, requestId);
            await u.ExecuteAsync(
                """
                UPDATE core.Workload SET CostCenterId = @cc, SolutionClass = @solutionClass, LifecycleStage = @lifecycleStage, Criticality = @criticality,
                       DataBoundary = @dataBoundary, Audience = @audience, ServiceId = COALESCE(ServiceId, @ServiceId), TriggerType = COALESCE(TriggerType, 'USER_INITIATED')
                WHERE WorkloadId = @WorkloadId
                """, new { cc = seed.CostCenters[costCenter], solutionClass, lifecycleStage, criticality, dataBoundary, audience, r.ServiceId, r.WorkloadId });
            await u.ExecuteAsync("UPDATE wf.Request SET MetersDeclaredNone = @metersNone WHERE RequestId = @requestId", new { metersNone, requestId });
        });

    /// <summary>Simulates the ITSM ticket closing (what the worker's poller does), with the configurer.</summary>
    public Task CloseTicketAsync(long requestId, string configurerKey, string state = "CLOSED")
        => host.SystemAsync(async u =>
        {
            var ticketId = await u.QuerySingleAsync<long>(
                "SELECT TOP (1) ItsmTicketId FROM wf.ItsmTicket WHERE RequestId = @requestId AND Kind = 'TASK' AND ClosedUtc IS NULL ORDER BY ItsmTicketId DESC", new { requestId });
            await u.ExecuteAsync("UPDATE wf.ItsmTicket SET State = @state, ExternalNumber = COALESCE(ExternalNumber, CONCAT('SCTASK', RIGHT('0000000' + CAST(@ticketId AS varchar(20)), 7))), ClosedUtc = SYSUTCDATETIME(), ClosureNotes = N'Configured as specified.' WHERE ItsmTicketId = @ticketId",
                new { state, ticketId });
            await E.OnTicketClosedAsync(u, ticketId, state, seed.Person(configurerKey), "Configured as specified in the ticket.");
        });
}
