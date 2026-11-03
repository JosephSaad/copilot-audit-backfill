using System.Globalization;
using Governance.Application;
using Governance.Application.Data;
using Governance.Application.Integrations;
using Governance.Application.Outbox;
using Governance.Application.Workflow;
using Governance.Domain;
using Microsoft.Extensions.Options;

namespace Governance.Worker.Jobs;

/// <summary>Daily (checked hourly; every notice is de-duplicated): the recurring parts of the operating model.</summary>
public sealed class CampaignSchedulerJob(IServiceScopeFactory scopes, IOptions<DbOptions> db, IOptions<WorkerOptions> worker, ILogger<CampaignSchedulerJob> log)
    : JobBase(scopes, db, worker, log)
{
    public override string Name => "CampaignScheduler";
    public override TimeSpan Interval => TimeSpan.FromHours(1);
    public override TimeSpan InitialDelay => TimeSpan.FromSeconds(20);
    protected override Task<string?> RunAsync(IServiceProvider s, CancellationToken ct) => new CampaignScheduler(s).RunAsync(ct)!;
}

public sealed class CampaignScheduler(IServiceProvider s)
{
    private readonly Db _db = s.GetRequiredService<Db>();
    private readonly IClock _clock = s.GetRequiredService<IClock>();
    private readonly OutboxWriter _outbox = s.GetRequiredService<OutboxWriter>();
    private readonly FindingWriter _findings = s.GetRequiredService<FindingWriter>();
    private readonly BusinessCalendarProvider _calendars = s.GetRequiredService<BusinessCalendarProvider>();

    public async Task<string> RunAsync(CancellationToken ct)
    {
        var a = await ExceptionsAsync(ct);
        var b = await CommercialAsync(ct);
        var c = await InventoryFreshnessAsync(ct);
        var d = await AttestationsAsync(ct);
        var e = await RecertificationAsync(ct);
        return $"{a} exception notices, {b} commercial notices, {c} inventory reminders, {d} attestation tasks or escalations, {e} overdue recertifications.";
    }

    /// <summary>N13 at 30, 7 and 1 days and on expiry. An expired exception that was the workload's only approved route suspends it.</summary>
    public async Task<int> ExceptionsAsync(CancellationToken ct)
    {
        var today = _clock.UtcNow.Date;
        var count = 0;
        var items = (await _db.InTransactionAsync(u => u.QueryAsync<(int ExceptionId, Guid PublicId, int FundingOwnerId, int? WorkloadId, string Title, int OwnerPersonId, DateTime ExpiresOn, long? DecisionId, bool Notified30, bool Notified7, bool Notified1)>(
            "SELECT ExceptionId, PublicId, FundingOwnerId, WorkloadId, Title, OwnerPersonId, ExpiresOn, DecisionId, Notified30, Notified7, Notified1 FROM wf.ExceptionItem WHERE Status = 'ACTIVE'"), ct)).ToList();
        foreach (var x in items)
        {
            var days = (x.ExpiresOn.Date - today).Days;
            await _db.InTransactionAsync(async u =>
            {
                var people = new List<int> { x.OwnerPersonId };
                if (x.DecisionId is long did && await u.ScalarAsync<int?>("SELECT DeciderPersonId FROM wf.Decision WHERE DecisionId = @did", new { did }) is int decider) people.Add(decider);
                if (days < 0)
                {
                    await u.ExecuteAsync("UPDATE wf.ExceptionItem SET Status = 'EXPIRED' WHERE ExceptionId = @ExceptionId AND Status = 'ACTIVE'", new { x.ExceptionId });
                    var fid = await _findings.RaiseAsync(u, $"EXCEPTION_EXPIRED:{x.ExceptionId}", new FindingWriter.NewFinding(FindingTypes.ExceptionExpired, "HIGH",
                        $"Exception expired: {x.Title}", "Renew it through the exception journey (J3) or bring the workload back inside an approved pattern.",
                        FundingOwnerId: x.FundingOwnerId, WorkloadId: x.WorkloadId));
                    var suspended = false;
                    if (x.WorkloadId is int wid)
                    {
                        var otherRoute = await u.ScalarAsync<int>(
                            """
                            SELECT (SELECT COUNT(*) FROM wf.ExceptionItem WHERE WorkloadId = @wid AND Status = 'ACTIVE')
                                 + (SELECT COUNT(*) FROM wf.Decision WHERE WorkloadId = @wid AND DecisionType <> 'EXCEPTION_DISPOSITION'
                                      AND Outcome IN ('APPROVED','APPROVED_WITH_CONDITIONS','CONSTRAINED_PILOT','EXTEND') AND (ExpiresOn IS NULL OR ExpiresOn >= @today))
                            """, new { wid, today });
                        if (otherRoute == 0)
                            suspended = await u.ExecuteAsync("UPDATE core.Workload SET State = 'SUSPENDED' WHERE WorkloadId = @wid AND State NOT IN ('RETIRED','RETIRING','SUSPENDED')", new { wid }) > 0;
                    }
                    await _outbox.EnqueueAsync(u, new OutboxMessage
                    {
                        EventType = "N13", FindingId = fid, FundingOwnerId = x.FundingOwnerId, PersonIds = people.Distinct().ToList(),
                        Subject = $"Exception expired: {x.Title}",
                        Text = suspended ? "The exception expired and it was the workload's only approved route, so the workload is suspended." : "The exception expired.",
                        LinkPath = "/workbench/Exceptions",
                    }, $"N13:{x.ExceptionId}:expired");
                    count++;
                    return;
                }
                // One notice for the tightest threshold reached (30, 7, 1 days); skipped thresholds are marked too.
                var threshold = days <= 1 ? 1 : days <= 7 ? 7 : days <= 30 ? 30 : 0;
                var sent = threshold switch { 1 => x.Notified1, 7 => x.Notified7, 30 => x.Notified30, _ => true };
                if (!sent)
                {
                    await u.ExecuteAsync(
                        "UPDATE wf.ExceptionItem SET Notified30 = 1, Notified7 = CASE WHEN @threshold <= 7 THEN 1 ELSE Notified7 END, Notified1 = CASE WHEN @threshold = 1 THEN 1 ELSE Notified1 END WHERE ExceptionId = @ExceptionId",
                        new { threshold, x.ExceptionId });
                    await _outbox.EnqueueAsync(u, new OutboxMessage
                    {
                        EventType = "N13", FundingOwnerId = x.FundingOwnerId, PersonIds = people.Distinct().ToList(),
                        Subject = $"Exception expires in {days} day{(days == 1 ? "" : "s")}: {x.Title}",
                        Text = "Renew it through the exception journey (J3) with fresh evidence, or plan the change that ends it.",
                        LinkPath = "/workbench/Exceptions", Facts = new() { ["expires"] = x.ExpiresOn.ToString("d MMM yyyy", CultureInfo.InvariantCulture) },
                    }, $"N13:{x.ExceptionId}:{threshold}");
                    count++;
                }
            }, ct);
        }
        return count;
    }

    /// <summary>N18 at 90, 60 and 30 days before a renewal or expiry (C06). The Workbench never renews anything itself (C21).</summary>
    public async Task<int> CommercialAsync(CancellationToken ct)
    {
        var today = _clock.UtcNow.Date;
        var items = (await _db.InTransactionAsync(u => u.QueryAsync<(int CommercialItemId, string Name, string ItemType, DateTime When, bool AutoRenew)>(
            """
            SELECT CommercialItemId, Name, ItemType, COALESCE(RenewalOn, EndOn) AS [When], AutoRenew FROM core.CommercialItem
            WHERE COALESCE(RenewalOn, EndOn) BETWEEN @today AND DATEADD(DAY, 90, @today)
            """, new { today }), ct)).ToList();
        var n = 0;
        foreach (var i in items)
        {
            var days = (i.When.Date - today).Days;
            var threshold = days <= 30 ? 30 : days <= 60 ? 60 : 90;
            if (await _db.InTransactionAsync(u => _outbox.EnqueueAsync(u, new OutboxMessage
                {
                    EventType = "N18",
                    Roles = { new RoleTarget(Roles.CommercialApprover, ScopeTypes.Tenant, null), new RoleTarget(Roles.FinOpsLead, ScopeTypes.Tenant, null), new RoleTarget(Roles.SourcingAgent, ScopeTypes.Tenant, null) },
                    Subject = $"{i.Name}: {(i.AutoRenew ? "auto-renews" : "ends")} in {days} days",
                    Text = "Prepare the commercial decision (J8): FinOps recommends, the commercial authority decides, Sourcing executes.",
                    LinkPath = "/workbench/Registry/Commercial", Facts = new() { ["date"] = i.When.ToString("d MMM yyyy", CultureInfo.InvariantCulture), ["type"] = i.ItemType },
                }, $"N18:{i.CommercialItemId}:{i.When:yyyyMMdd}:{threshold}"), ct)) n++;
        }
        return n;
    }

    /// <summary>N21: the inventory sync is operator-run (delegated only); remind Platform Operations weekly when it is older than 7 days.</summary>
    public async Task<int> InventoryFreshnessAsync(CancellationToken ct)
    {
        var (last, maxDays) = await _db.InTransactionAsync(async u => (
            await u.ScalarAsync<DateTime?>("SELECT MAX(FinishedUtc) FROM intg.SyncRun WHERE Integration = 'X4' AND Status = 'SUCCEEDED'"),
            int.TryParse(await u.ScalarAsync<string>("SELECT SettingValue FROM ref.Setting WHERE SettingKey = 'Inventory.MaxAgeDays'"), out var d) ? d : 7), ct);
        if (last is DateTime l && _clock.UtcNow - l < TimeSpan.FromDays(maxDays)) return 0;
        var week = ISOWeek.GetYear(_clock.UtcNow) * 100 + ISOWeek.GetWeekOfYear(_clock.UtcNow);
        return await _db.InTransactionAsync(u => _outbox.EnqueueAsync(u, new OutboxMessage
        {
            EventType = "N21", Queues = { "OPERATIONS" }, Roles = { new RoleTarget(Roles.PlatformOperator, ScopeTypes.Tenant, null) },
            Subject = last is null ? "Inventory has never been synced" : $"Inventory sync is {(int)(_clock.UtcNow - last.Value).TotalDays} days old",
            Text = "Run the Power Platform inventory sync from Operations > Sync. It runs under your sign-in because the API accepts delegated tokens only.",
            LinkPath = "/workbench/Operations/Sync",
        }, $"N21:{week}"), ct) ? 1 : 0;
    }

    /// <summary>
    /// N22: every governed Microsoft 365 policy (Copilot Chat credit policy, spending policy) is re-attested each month,
    /// because new services can join a policy while auto-apply is on (C16). Overdue tasks escalate after 5 business days.
    /// </summary>
    public async Task<int> AttestationsAsync(CancellationToken ct)
    {
        var now = _clock.UtcNow;
        var monthStart = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var count = 0;
        var bindings = (await _db.InTransactionAsync(u => u.QueryAsync<(int BindingId, int WorkloadId, int FundingOwnerId, string ObjectType, string? DisplayName, string ExternalId)>(
            """
            SELECT b.BindingId, b.WorkloadId, b.FundingOwnerId, b.ObjectType, b.DisplayName, b.ExternalId FROM core.WorkloadBinding b
            WHERE b.ObjectType IN ('CHAT_CREDIT_POLICY','SPENDING_POLICY') AND b.EffectiveTo IS NULL
              AND NOT EXISTS (SELECT 1 FROM evd.Attestation a WHERE a.BindingId = b.BindingId AND a.AttestedUtc >= @monthStart)
              AND NOT EXISTS (SELECT 1 FROM wf.Task t WHERE t.TaskType = 'ATTESTATION' AND t.RelatedEntityType = 'BINDING' AND t.RelatedEntityId = b.BindingId AND t.Status = 'OPEN')
            """, new { monthStart }), ct)).ToList();
        foreach (var b in bindings)
            await _db.InTransactionAsync(async u =>
            {
                var calendar = await _calendars.GetAsync(u);
                var due = calendar.AddBusinessDays(now, 5);
                var label = b.ObjectType == "CHAT_CREDIT_POLICY" ? "Copilot Chat credit policy" : "spending policy";
                var taskId = await u.ScalarAsync<long>(
                    """
                    INSERT wf.Task (FundingOwnerId, TaskType, Title, AssignedRoleCode, ScopeType, ScopeId, Status, IsBlocking, SlaPolicyCode, LadderCode, StartedUtc, DueUtc, RelatedEntityType, RelatedEntityId, Reason)
                    OUTPUT inserted.TaskId
                    VALUES (@FundingOwnerId, 'ATTESTATION', @title, 'PLATFORM_OPERATOR', 'TENANT', NULL, 'OPEN', 1, 'BD5', 'OPERATIONS', @now, @due, 'BINDING', @BindingId, @reason)
                    """,
                    new
                    {
                        b.FundingOwnerId, title = $"Monthly attestation: {label} {b.DisplayName ?? b.ExternalId}", now, due, b.BindingId,
                        reason = "Compare the policy in the Microsoft 365 admin center with the control template: scope groups, services, Auto-apply new services, limits, alert recipients, billing method. Attach the export or a screenshot.",
                    });
                await _outbox.EnqueueAsync(u, new OutboxMessage
                {
                    EventType = "N22", TaskId = taskId, FundingOwnerId = b.FundingOwnerId, Queues = { "OPERATIONS" },
                    Roles = { new RoleTarget(Roles.PlatformOperator, ScopeTypes.Tenant, null) },
                    Subject = $"Attestation due: {label} {b.DisplayName ?? b.ExternalId}", Text = "Monthly re-attestation, because new services can join a policy while Auto-apply is on (C16).",
                    LinkPath = "/workbench/Operations/Attestations", Facts = new() { ["due"] = due.ToString("o") },
                }, $"N22:{b.BindingId}:{now:yyyyMM}");
                count++;
            }, ct);

        var overdue = (await _db.InTransactionAsync(u => u.QueryAsync<(long TaskId, string Title)>(
            "SELECT TaskId, Title FROM wf.Task WHERE TaskType = 'ATTESTATION' AND Status = 'OPEN' AND DueUtc <= @now", new { now }), ct)).ToList();
        foreach (var t in overdue)
            if (await _db.InTransactionAsync(u => _outbox.EnqueueAsync(u, new OutboxMessage
                {
                    EventType = "N22", TaskId = t.TaskId, Roles = { new RoleTarget(Roles.PlatformOwner, ScopeTypes.Tenant, null) },
                    Subject = $"Attestation overdue: {t.Title}", Text = "A monthly attestation is past due. Unattested settings are not verified.",
                    LinkPath = "/workbench/Operations/Attestations",
                }, $"N22:overdue:{t.TaskId}"), ct)) count++;
        return count;
    }

    /// <summary>N14: recertification items past the campaign's due date become overdue and escalate to the Platform Owner.</summary>
    public async Task<int> RecertificationAsync(CancellationToken ct)
    {
        var today = _clock.UtcNow.Date;
        var items = (await _db.InTransactionAsync(u => u.QueryAsync<(int CampaignItemId, int FundingOwnerId, int? OwnerPersonId, string Name)>(
            """
            SELECT ci.CampaignItemId, ci.FundingOwnerId, ci.OwnerPersonId, c.Name FROM wf.CampaignItem ci JOIN wf.Campaign c ON c.CampaignId = ci.CampaignId
            WHERE ci.Status = 'OPEN' AND c.DueOn < @today AND c.CampaignType = 'RECERTIFICATION'
            """, new { today }), ct)).ToList();
        foreach (var i in items)
            await _db.InTransactionAsync(async u =>
            {
                await u.ExecuteAsync("UPDATE wf.CampaignItem SET Status = 'OVERDUE' WHERE CampaignItemId = @CampaignItemId AND Status = 'OPEN'", new { i.CampaignItemId });
                await _outbox.EnqueueAsync(u, new OutboxMessage
                {
                    EventType = "N14", FundingOwnerId = i.FundingOwnerId, PersonIds = i.OwnerPersonId is int p ? new() { p } : new(),
                    Roles = { new RoleTarget(Roles.PlatformOwner, ScopeTypes.Tenant, null), new RoleTarget(Roles.LobBusinessOwner, ScopeTypes.FundingOwner, i.FundingOwnerId) },
                    Subject = $"Recertification overdue: {i.Name}", Text = "Confirm need, audience, funding and outcome, or decide to retire.",
                    LinkPath = "/workbench/Recertification", ActivityType = "recertificationDue",
                }, $"N14:overdue:{i.CampaignItemId}");
            }, ct);
        return items.Count;
    }
}
