using Governance.Application.Data;
using Governance.Application.Outbox;
using Governance.Application.Rules;
using Governance.Domain;

namespace Governance.Application.Integrations;

/// <summary>
/// X5 (daily and on demand): reads the Copilot Credits allocation, the TenantPool rule and the billing policy link of
/// every governed environment, then the drift evaluator compares them with the approved posture (E2 to E6, G5).
/// App-only when the Worker holds the Power Platform reader role; the operator's token otherwise.
/// </summary>
public sealed class LicensingReadService(Db db, IPowerPlatformApi api, DriftEvaluator drift) : ILicensingRead
{
    public sealed record Posture(int WorkloadId, int FundingOwnerId, string EnvironmentId, decimal AllocationCredits, bool TenantPoolEnabled, bool PaygLinked,
        string? BillingPolicyId, decimal? AgentLimitCredits, bool GroupRuleLocksTenantPool, string WorkloadName);

    public Task<Guid> RunAsync(string? delegatedAccessToken, int? operatorPersonId, CancellationToken ct)
        => SyncRuns.RunAsync(db, IntegrationCodes.Licensing, delegatedAccessToken is null ? "APP_ONLY" : "DELEGATED", operatorPersonId, async (runId, uow) =>
        {
            var postures = (await uow.QueryAsync<Posture>(
                """
                SELECT p.WorkloadId, p.FundingOwnerId, p.EnvironmentId, p.AllocationCredits, p.TenantPoolEnabled, p.PaygLinked, p.BillingPolicyId,
                       p.AgentLimitCredits, p.GroupRuleLocksTenantPool, w.Name AS WorkloadName
                FROM core.ApprovedPosture p JOIN core.Workload w ON w.WorkloadId = p.WorkloadId
                WHERE p.EffectiveTo IS NULL AND w.State NOT IN ('RETIRED')
                """)).ToList();
            if (postures.Count == 0) return (0, 0, "No governed environments have an approved posture yet.");

            IReadOnlyDictionary<string, string> billing;
            string? billingNote = null;
            try { billing = await api.EnvironmentBillingPoliciesAsync(delegatedAccessToken, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                billing = new Dictionary<string, string>();
                billingNote = ex is IntegrationPermissionException
                    ? " Billing policies were refused (HTTP 403): PAYG links were not evaluated; check the Power Platform reader role."
                    : " Billing policy links could not be read; PAYG links were not evaluated.";
            }

            var readings = new List<(Posture P, AllocationData? A, string? Billing)>();
            foreach (var p in postures)
            {
                var a = await api.GetAllocationAsync(delegatedAccessToken, p.EnvironmentId, ct);
                billing.TryGetValue(p.EnvironmentId.ToLowerInvariant(), out var policy);
                readings.Add((p, a, policy));
                await uow.ExecuteAsync(
                    """
                    INSERT intg.AllocationReading (SyncRunId, EnvironmentId, AllocatedCredits, TenantPoolEnabled, BillingPolicyId, PaygLinked, ConsumedCredits, TenantPoolLockedByGroupRule, RawJson)
                    VALUES (@runId, @EnvironmentId, @Allocated, @Pool, @policy, @payg, @Consumed, @Locked, @Raw)
                    """,
                    new
                    {
                        runId, p.EnvironmentId, Allocated = a?.AllocatedCredits, Pool = a?.TenantPoolEnabled, policy, payg = billingNote is null ? policy is not null : (bool?)null,
                        Consumed = a?.ConsumedCredits, Locked = a?.TenantPoolLockedByGroupRule, Raw = a?.RawJson,
                    });
            }
            var raised = await drift.EvaluateAsync(uow, runId, readings, payglinkKnown: billingNote is null);
            return (readings.Count, raised, $"{readings.Count} environments read, {raised} drift findings.{billingNote}");
        }, ct);
}

/// <summary>
/// Compares actual with approved posture and raises findings (N10). An allocation is reported as a cap only when
/// tenant-pool draw is off and no PAYG plan is linked (E2 to E4); agent limits have no read-back here, so they stay
/// with attestation.
/// </summary>
public sealed class DriftEvaluator(FindingWriter findings)
{
    public async Task<int> EvaluateAsync(Uow uow, long runId, IReadOnlyList<(LicensingReadService.Posture P, AllocationData? A, string? Billing)> readings, bool payglinkKnown)
    {
        var raised = 0;
        var openKeys = new Dictionary<string, List<string>>
        {
            [FindingTypes.AllocationDrift] = new(), [FindingTypes.TenantPoolDrift] = new(), [FindingTypes.PaygDrift] = new(),
        };
        foreach (var (p, a, billing) in readings)
        {
            if (a is null) continue;
            if (a.AllocatedCredits is decimal alloc && alloc != p.AllocationCredits)
                raised += await RaiseAsync(uow, runId, p, FindingTypes.AllocationDrift, "Copilot Credits allocation differs from the approved value",
                    $"{p.AllocationCredits:N0} credits", $"{alloc:N0} credits", openKeys, "MEDIUM");
            if (a.TenantPoolEnabled is bool pool && pool != p.TenantPoolEnabled)
                raised += await RaiseAsync(uow, runId, p, FindingTypes.TenantPoolDrift,
                    pool ? "Draw from tenant capacity is on: the allocation is no longer a cap" : "Draw from tenant capacity is off, unlike the approved posture",
                    p.TenantPoolEnabled ? "on" : "off", pool ? "on" : "off", openKeys, pool ? "HIGH" : "MEDIUM");
            if (payglinkKnown)
            {
                var linked = billing is not null;
                if (linked != p.PaygLinked || (linked && p.BillingPolicyId is not null && !string.Equals(billing, p.BillingPolicyId, StringComparison.OrdinalIgnoreCase)))
                    raised += await RaiseAsync(uow, runId, p, FindingTypes.PaygDrift,
                        linked ? "A pay-as-you-go billing plan is linked: the allocation is no longer a cap" : "The approved pay-as-you-go link is missing",
                        p.PaygLinked ? $"linked to {p.BillingPolicyId ?? "a billing plan"}" : "not linked", linked ? $"linked to {billing}" : "not linked", openKeys, linked ? "HIGH" : "MEDIUM");
            }
        }
        foreach (var (type, keys) in openKeys)
            await findings.ResolveMissingAsync(uow, type, keys, "Read-back matches the approved posture.");
        return raised;
    }

    private async Task<int> RaiseAsync(Uow uow, long runId, LicensingReadService.Posture p, string type, string title, string expected, string actual,
        Dictionary<string, List<string>> openKeys, string severity)
    {
        var key = $"{type}:{p.EnvironmentId.ToLowerInvariant()}";
        openKeys[type].Add(key);
        var capNote = ProhibitedAssumptions.AllocationIsCap(type == FindingTypes.TenantPoolDrift ? actual == "on" : p.TenantPoolEnabled,
                                                           type == FindingTypes.PaygDrift ? actual.StartsWith("linked", StringComparison.Ordinal) : p.PaygLinked)
            ? "The allocation still caps consumption."
            : "While tenant-pool draw or PAYG is on, the allocation does not cap consumption (E2 to E4).";
        var id = await findings.RaiseAsync(uow, key, new FindingWriter.NewFinding(type, severity, $"{p.WorkloadName}: {title}",
            $"Read back from the Power Platform API and compared with the approved posture. {capNote} Correct it through an approved ITSM task, or approve the new value with a capacity change (J4).",
            FundingOwnerId: p.FundingOwnerId, WorkloadId: p.WorkloadId, ExternalId: p.EnvironmentId, Expected: expected, Actual: actual, SyncRunId: runId));
        if (id is not long fid) return 0;
        await findings.NotifyAsync(uow, "N10", fid, $"Drift: {p.WorkloadName}", title + ".",
            Array.Empty<int>(), new[] { new RoleTarget(Roles.PlatformOperator, ScopeTypes.Tenant, null) }, new[] { "OPERATIONS" }, p.FundingOwnerId);
        // Platform Owner and the workload's product owner are informed through the digest.
        foreach (var person in await uow.QueryAsync<int>(
                     """
                     SELECT ProductOwnerPersonId FROM core.Workload WHERE WorkloadId = @WorkloadId AND ProductOwnerPersonId IS NOT NULL
                     UNION SELECT ra.PersonId FROM sec.RoleAssignment ra WHERE ra.RoleCode = 'PLATFORM_OWNER' AND ra.ScopeType = 'TENANT' AND (ra.EffectiveTo IS NULL OR ra.EffectiveTo >= CAST(SYSUTCDATETIME() AS date))
                     """, new { p.WorkloadId }))
            await uow.ExecuteAsync("INSERT ntf.DigestEntry (PersonId, RuleCode, Text, Link) VALUES (@person, 'N10', @text, '/Operations/Drift')",
                new { person, text = $"Drift on {p.WorkloadName}: {title} (expected {expected}, found {actual})." });
        return 1;
    }
}
