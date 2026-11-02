using Governance.Application;
using Governance.Application.Data;
using Governance.Application.Outbox;
using Governance.Application.Workflow;
using Governance.Domain;
using Microsoft.Extensions.Options;

namespace Governance.Worker.Jobs;

/// <summary>
/// Every minute (section 7.4): reminders at 50 and 80 percent of the SLA, escalation one level up the stage's ladder at
/// 100 percent, auto-close of returned requests after the configured business days, overdue acknowledgement of
/// consumption events, incidents for high and critical events, and overdue claims of discovered items.
/// </summary>
public sealed class SlaTimerJob(IServiceScopeFactory scopes, IOptions<DbOptions> db, IOptions<WorkerOptions> worker, ILogger<SlaTimerJob> log)
    : JobBase(scopes, db, worker, log)
{
    public override string Name => "SlaTimer";
    public override TimeSpan Interval => TimeSpan.FromMinutes(1);

    protected override Task<string?> RunAsync(IServiceProvider s, CancellationToken ct) => new SlaTimer(s).RunAsync(ct)!;
}

/// <summary>The SLA timer's work, separate from the hosting so tests drive it with a fixed clock.</summary>
public sealed class SlaTimer(IServiceProvider s)
{
    private readonly Db _db = s.GetRequiredService<Db>();
    private readonly IClock _clock = s.GetRequiredService<IClock>();
    private readonly OutboxWriter _outbox = s.GetRequiredService<OutboxWriter>();
    private readonly AssignmentResolver _resolver = s.GetRequiredService<AssignmentResolver>();
    private readonly BusinessCalendarProvider _calendars = s.GetRequiredService<BusinessCalendarProvider>();
    private readonly WorkflowEngine _engine = s.GetRequiredService<WorkflowEngine>();

    public async Task<string> RunAsync(CancellationToken ct)
    {
        var reminders = await RemindersAsync(ct);
        var escalated = await EscalateAsync(ct);
        var closed = await AutoCloseAsync(ct);
        var events = await EventsAsync(ct);
        var claims = await OverdueClaimsAsync(ct);
        return $"{reminders} reminders, {escalated} escalations, {closed} auto-closed, {events} event notices, {claims} overdue claims.";
    }

    public async Task<int> RemindersAsync(CancellationToken ct)
    {
        var now = _clock.UtcNow;
        var due = await WithRequestsAsync(
            "SELECT t.* FROM wf.Task t WHERE t.Status = 'OPEN' AND ((t.Reminder1Utc <= @now AND t.Reminded1 = 0) OR (t.Reminder2Utc <= @now AND t.Reminded2 = 0)) AND (t.DueUtc IS NULL OR t.DueUtc > @now)",
            now, ct);
        foreach (var (t, number, publicId) in due)
        {
            var second = t.Reminder2Utc <= now && !t.Reminded2;
            await _db.InTransactionAsync(async u =>
            {
                await _outbox.EnqueueAsync(u, new OutboxMessage
                {
                    EventType = NoticeFor(t), RequestId = t.RequestId, TaskId = t.TaskId, FundingOwnerId = t.FundingOwnerId,
                    PersonIds = t.AssignedPersonId is int p ? new() { p } : new(),
                    Roles = t.AssignedPersonId is null && t.AssignedRoleCode is not null ? new() { new RoleTarget(t.AssignedRoleCode, t.ScopeType ?? ScopeTypes.Tenant, t.ScopeId) } : new(),
                    Subject = $"Reminder{(number is null ? "" : $" {number}")}: {t.Title}",
                    Text = $"This task is {(second ? "80" : "50")} percent through its time. It escalates when the time runs out.",
                    LinkPath = publicId is Guid g ? $"/workbench/Requests/{g}" : "/workbench/MyWork", ActivityType = "taskAssigned",
                    Facts = new() { ["due"] = t.DueUtc?.ToString("o") ?? "", ["stage"] = t.StageCode ?? "" },
                }, $"REM{(second ? 2 : 1)}:{t.TaskId}:{t.EscalationLevel}");
                await u.ExecuteAsync(second
                    ? "UPDATE wf.Task SET Reminded1 = 1, Reminded2 = 1 WHERE TaskId = @TaskId"
                    : "UPDATE wf.Task SET Reminded1 = 1 WHERE TaskId = @TaskId", new { t.TaskId });
            }, ct);
        }
        return due.Count;
    }

    /// <summary>At 100 percent the task moves one level up its ladder: delegated approver, LOB business owner, Platform Owner.</summary>
    public async Task<int> EscalateAsync(CancellationToken ct)
    {
        var now = _clock.UtcNow;
        var overdue = await WithRequestsAsync(
            """
            SELECT t.* FROM wf.Task t
            WHERE t.Status = 'OPEN' AND t.DueUtc <= @now AND t.LadderCode IS NOT NULL
              AND EXISTS (SELECT 1 FROM wf.EscalationStep e WHERE e.LadderCode = t.LadderCode AND e.Level = t.EscalationLevel + 1)
            """, now, ct);
        foreach (var (t, number, publicId) in overdue)
        {
            await _db.InTransactionAsync(async u =>
            {
                var step = await u.QuerySingleAsync<(string RoleCode, string ScopeKind)>(
                    "SELECT RoleCode, ScopeKind FROM wf.EscalationStep WHERE LadderCode = @LadderCode AND Level = @level", new { t.LadderCode, level = t.EscalationLevel + 1 });
                var role = await _resolver.EffectiveRoleAsync(u, step.RoleCode, t.FundingOwnerId);
                var scopeType = step.ScopeKind == "TENANT" ? ScopeTypes.Tenant : ScopeTypes.FundingOwner;
                int? scopeId = scopeType == ScopeTypes.FundingOwner ? t.FundingOwnerId : null;
                var sla = t.SlaPolicyCode is null ? null : await u.QuerySingleOrDefaultAsync<SlaPolicy>("SELECT * FROM wf.SlaPolicy WHERE SlaPolicyCode = @SlaPolicyCode", new { t.SlaPolicyCode });
                var times = (await _calendars.GetAsync(u)).Compute(sla, now);
                var n = await u.ExecuteAsync(
                    """
                    UPDATE wf.Task SET AssignedRoleCode = @role, ScopeType = @scopeType, ScopeId = @scopeId,
                           AssignedPersonId = CASE WHEN AssignedRoleCode = 'REQUESTER' THEN AssignedPersonId END, ClaimedUtc = NULL,
                           EscalationLevel = EscalationLevel + 1, DueUtc = @DueUtc, Reminder1Utc = @Reminder1Utc, Reminder2Utc = @Reminder2Utc, Reminded1 = 0, Reminded2 = 0
                    WHERE TaskId = @TaskId AND Status = 'OPEN' AND EscalationLevel = @EscalationLevel
                    """, new { role, scopeType, scopeId, times.DueUtc, times.Reminder1Utc, times.Reminder2Utc, t.TaskId, t.EscalationLevel });
                if (n == 0) return;
                if (t.RequestId is long rid && t.FundingOwnerId is int fo)
                    await _engine.EventAsync(u, rid, fo, null, "ESCALATED", null, null, t.StageCode, $"{t.Title}: escalated to {role.Replace('_', ' ').ToLowerInvariant()} (level {t.EscalationLevel + 1}).");
                // An escalation always reaches people by email: queue-only notices (N07) become an action notice (N04).
                var code = NoticeFor(t) is "N07" ? "N04" : NoticeFor(t);
                await _outbox.EnqueueAsync(u, new OutboxMessage
                {
                    EventType = code, RequestId = t.RequestId, TaskId = t.TaskId, FundingOwnerId = t.FundingOwnerId,
                    Roles = { new RoleTarget(role, scopeType, scopeId) },
                    Subject = $"Escalated{(number is null ? "" : $" {number}")}: {t.Title}",
                    Text = "This task ran past its time and is now yours to act on or reassign.",
                    LinkPath = publicId is Guid g ? $"/workbench/Requests/{g}" : "/workbench/MyWork", ActivityType = "taskAssigned",
                    Facts = new() { ["due"] = times.DueUtc?.ToString("o") ?? "", ["stage"] = t.StageCode ?? "", ["escalation level"] = (t.EscalationLevel + 1).ToString() },
                }, $"ESC:{t.TaskId}:{t.EscalationLevel + 1}");
            }, ct);
        }
        return overdue.Count;
    }

    /// <summary>B7: returned requests close after Sla.ReturnedAutoCloseDays business days without a response (N02).</summary>
    public async Task<int> AutoCloseAsync(CancellationToken ct)
    {
        var (days, candidates, calendar) = await _db.InTransactionAsync(async u =>
        {
            var d = int.TryParse(await u.ScalarAsync<string>("SELECT SettingValue FROM ref.Setting WHERE SettingKey = 'Sla.ReturnedAutoCloseDays'"), out var x) ? x : 10;
            var c = (await u.QueryAsync<(long RequestId, DateTime StartedUtc)>(
                "SELECT t.RequestId, t.StartedUtc FROM wf.Task t JOIN wf.Request r ON r.RequestId = t.RequestId WHERE r.CurrentStage = 'B7' AND t.StageCode = 'B7' AND t.Status = 'OPEN'")).ToList();
            return (d, c, await _calendars.GetAsync(u));
        }, ct);
        var closed = 0;
        foreach (var c in candidates.Where(c => calendar.AddBusinessDays(c.StartedUtc, days) <= _clock.UtcNow))
        {
            await _db.InTransactionAsync(async u =>
            {
                await u.ExecuteAsync("UPDATE wf.Task SET Status = 'CANCELLED', CompletedUtc = @now, Notes = N'Closed automatically: no response.' WHERE RequestId = @RequestId AND StageCode = 'B7' AND Status = 'OPEN'",
                    new { c.RequestId, now = _clock.UtcNow });
                await _engine.AutoCloseAsync(u, c.RequestId);
            }, ct);
            closed++;
        }
        return closed;
    }

    /// <summary>Consumption events: overdue acknowledgement escalates to the Platform Owner; high and critical events get an ITSM incident.</summary>
    public async Task<int> EventsAsync(CancellationToken ct)
    {
        var now = _clock.UtcNow;
        var count = 0;
        var overdue = (await _db.InTransactionAsync(u => u.QueryAsync<(int EventId, Guid PublicId, int FundingOwnerId, string Title, string Severity)>(
            "SELECT EventId, PublicId, FundingOwnerId, Title, Severity FROM ops.ConsumptionEvent WHERE Status = 'OPEN' AND AckUtc IS NULL AND AckDueUtc <= @now", new { now }), ct)).ToList();
        foreach (var e in overdue)
            if (await _db.InTransactionAsync(u => _outbox.EnqueueAsync(u, new OutboxMessage
                {
                    EventType = "N12", EventId = e.EventId, FundingOwnerId = e.FundingOwnerId, Queues = { "OPERATIONS" },
                    Roles = { new RoleTarget(Roles.PlatformOwner, ScopeTypes.Tenant, null), new RoleTarget(Roles.PlatformOperator, ScopeTypes.Tenant, null) },
                    Subject = $"Not acknowledged in time: {e.Title}", Text = $"A {e.Severity.ToLowerInvariant()} consumption event passed its acknowledgement target.",
                    LinkPath = $"/workbench/Operations/Events/{e.PublicId}",
                }, $"N12:overdue:{e.EventId}"), ct)) count++;

        var needIncident = (await _db.InTransactionAsync(u => u.QueryAsync<(int EventId, int FundingOwnerId, string Title, string Severity, string Description)>(
            """
            SELECT e.EventId, e.FundingOwnerId, e.Title, e.Severity, e.Description FROM ops.ConsumptionEvent e
            WHERE e.Severity IN ('HIGH','CRITICAL') AND e.Status IN ('OPEN','ACKNOWLEDGED')
              AND NOT EXISTS (SELECT 1 FROM wf.ItsmTicket t WHERE t.EventId = e.EventId)
            """), ct)).ToList();
        foreach (var e in needIncident)
            await _db.InTransactionAsync(async u =>
            {
                var payload = System.Text.Json.JsonSerializer.Serialize(new { shortDescription = $"Copilot consumption {e.Severity.ToLowerInvariant()}: {e.Title}", description = e.Description, eventId = e.EventId });
                var ticket = await u.ScalarAsync<long>(
                    "INSERT wf.ItsmTicket (FundingOwnerId, EventId, Kind, CorrelationId, State, Payload) OUTPUT inserted.ItsmTicketId VALUES (@FundingOwnerId, @EventId, 'INCIDENT', NEWID(), 'PENDING_CREATE', @payload)",
                    new { e.FundingOwnerId, e.EventId, payload });
                await _outbox.EnqueueAsync(u, new OutboxMessage { EventType = "ITSM.Create", ItsmTicketId = ticket, EventId = e.EventId }, $"ITSM.Create:{ticket}");
                count++;
            }, ct);
        return count;
    }

    /// <summary>Discovered items not claimed in 10 business days escalate to the Platform Owner (N11).</summary>
    public async Task<int> OverdueClaimsAsync(CancellationToken ct)
    {
        var now = _clock.UtcNow;
        var overdue = (await _db.InTransactionAsync(u => u.QueryAsync<(long FindingId, string Title)>(
            "SELECT FindingId, Title FROM ops.Finding WHERE Status = 'OPEN' AND DueUtc <= @now AND FindingType IN ('UNREGISTERED_ENVIRONMENT','UNREGISTERED_AGENT','UNREGISTERED_FLOW')", new { now }), ct)).ToList();
        var n = 0;
        foreach (var f in overdue)
            if (await _db.InTransactionAsync(u => _outbox.EnqueueAsync(u, new OutboxMessage
                {
                    EventType = "N11", FindingId = f.FindingId, Roles = { new RoleTarget(Roles.PlatformOwner, ScopeTypes.Tenant, null) },
                    Subject = $"Not claimed in time: {f.Title}", Text = "Nobody claimed this item within 10 business days. Assign an owner or record why it is out of scope.",
                    LinkPath = "/workbench/Registry/Discovery",
                }, $"N11:overdue:{f.FindingId}"), ct)) n++;
        return n;
    }

    private async Task<List<(TaskRow T, string? RequestNumber, Guid? RequestPublicId)>> WithRequestsAsync(string sql, DateTime now, CancellationToken ct)
        => await _db.InTransactionAsync(async u =>
        {
            var list = new List<(TaskRow, string?, Guid?)>();
            foreach (var t in await u.QueryAsync<TaskRow>(sql, new { now }))
            {
                var r = t.RequestId is long rid
                    ? await u.QuerySingleOrDefaultAsync<(string RequestNumber, Guid PublicId)>("SELECT RequestNumber, PublicId FROM wf.Request WHERE RequestId = @rid", new { rid })
                    : default;
                list.Add((t, r.RequestNumber, r.RequestNumber is null ? null : r.PublicId));
            }
            return list;
        }, ct);

    private static string NoticeFor(TaskRow t) => (t.StageCode, t.TaskType) switch
    {
        (Stages.Remediate, _) => "N02",
        (Stages.Classify, _) or (Stages.Completeness, _) => "N03",
        (Stages.SpecialistReview, _) or (_, TaskTypes.Consult) => "N05",
        (Stages.Disposition, TaskTypes.Decision) => "N05",
        (Stages.PrepareFulfillment, _) or (Stages.Verify, _) => "N07",
        (Stages.BuildAndTest, _) or (Stages.ProductionAcceptance, _) => "N09",
        (_, TaskTypes.Attestation) => "N22",
        (_, TaskTypes.Recertification) => "N14",
        _ => "N04",
    };
}
