using Governance.Application.Data;
using Governance.Application.Outbox;
using Governance.Domain;
using Microsoft.Extensions.Logging;

namespace Governance.Application.Integrations;

/// <summary>
/// X2 (every 15 minutes): access package assignments and requests for governed packages (journey J1).
/// A newly delivered assignment becomes a decision record with Source ENTRA, attributed to the package's funding
/// owner. Assignments without an active envelope raise ASSIGNMENT_WITHOUT_FUNDING (N15, daily); members of a policy
/// scope group that no assignment explains raise MEMBERSHIP_OUTSIDE_SCOPE. Entra already notifies approvers, so
/// nothing here notifies them again (section 8.2).
/// </summary>
public sealed class EntitlementSyncService(Db db, IEntitlementApi entitlements, FindingWriter findings, OutboxWriter outbox, IClock clock,
    TenantSettings tenant, ILogger<EntitlementSyncService> log)
{
    private sealed record MapRow(Guid AccessPackageId, string DisplayName, int FundingOwnerId, int ServiceId, string Tier, Guid? PolicyGroupId);

    public Task<Guid> RunAsync(CancellationToken ct)
        => SyncRuns.RunAsync(db, IntegrationCodes.Entitlements, "APP_ONLY", null, async (runId, uow) =>
        {
            await BootstrapMapAsync(uow);
            var map = (await uow.QueryAsync<MapRow>("SELECT AccessPackageId, DisplayName, FundingOwnerId, ServiceId, Tier, PolicyGroupId FROM intg.AccessPackageMap")).ToList();
            int read = 0, raised = 0;
            var activeRaci = await uow.ScalarAsync<int?>("SELECT RaciVersionId FROM sec.RaciVersion WHERE Status = 'ACTIVE'");
            var entraPerson = await uow.ScalarAsync<int?>("SELECT PersonId FROM sec.Person WHERE UserPrincipalName = N'system:entra-import'");
            var unfundedKeys = new List<string>();
            var outsideKeys = new List<string>();

            foreach (var pkg in map)
            {
                var assignments = await entitlements.AssignmentsAsync(pkg.AccessPackageId, ct);
                var requests = await entitlements.AssignmentRequestsAsync(pkg.AccessPackageId, ct);
                read += assignments.Count;
                foreach (var a in assignments)
                {
                    var personId = a.TargetObjectId is Guid t ? await uow.ScalarAsync<int?>("SELECT PersonId FROM sec.Person WHERE EntraObjectId = @t", new { t }) : null;
                    var req = requests.Where(r => r.AssignmentId == a.Id || (r.TargetObjectId == a.TargetObjectId && r.RequestType is "userAdd" or "adminAdd"))
                                      .OrderByDescending(r => r.CreatedUtc).FirstOrDefault();
                    await uow.ExecuteAsync(
                        """
                        MERGE intg.EntitlementAssignment AS t USING (SELECT @Id AS AssignmentId) AS s ON t.AssignmentId = s.AssignmentId
                        WHEN MATCHED THEN UPDATE SET State = @State, ExpiresUtc = @ExpiresUtc, TargetPersonId = COALESCE(@personId, t.TargetPersonId),
                             RequestId = COALESCE(@RequestId, t.RequestId), Justification = COALESCE(@Justification, t.Justification), LastSeenRunId = @runId
                        WHEN NOT MATCHED THEN INSERT (AssignmentId, AccessPackageId, TargetObjectId, TargetPersonId, State, ExpiresUtc, CreatedUtc, RequestId, Justification, LastSeenRunId)
                             VALUES (@Id, @AccessPackageId, @TargetObjectId, @personId, @State, @ExpiresUtc, @CreatedUtc, @RequestId, @Justification, @runId);
                        """,
                        new
                        {
                            a.Id, a.State, a.ExpiresUtc, personId, RequestId = req?.Id, Justification = Cut(req?.Justification, 1000), runId,
                            AccessPackageId = pkg.AccessPackageId, a.TargetObjectId, a.CreatedUtc,
                        });

                    if (!string.Equals(a.State, "delivered", StringComparison.OrdinalIgnoreCase)) continue;

                    // The decision record (section 7.5: "every decision, including those imported from Entra").
                    var hasDecision = await uow.ScalarAsync<long?>("SELECT DecisionId FROM intg.EntitlementAssignment WHERE AssignmentId = @Id", new { a.Id });
                    if (hasDecision is null && activeRaci is int raci && entraPerson is int decider)
                    {
                        var decisionId = await uow.ScalarAsync<long>(
                            """
                            INSERT wf.Decision (PublicId, FundingOwnerId, DecisionType, Outcome, DeciderPersonId, DeciderRoleCode, RaciVersionId, RaciActivityCode,
                                                ScopeText, ConditionsText, ReasonText, ExpiresOn, Source, SourceRef, DecidedAtUtc)
                            OUTPUT inserted.DecisionId
                            VALUES (NEWID(), @FundingOwnerId, 'ACCESS_ASSIGNMENT', 'APPROVED', @decider, 'ENTRA_APPROVER', @raci, 'FUNDING_BUDGET',
                                    @scope, NULL, @why, @ExpiresUtc, 'ENTRA', @sourceRef, @decided)
                            """,
                            new
                            {
                                pkg.FundingOwnerId, decider, raci,
                                scope = Cut($"Access package {pkg.DisplayName} ({pkg.Tier}) for {a.TargetPrincipalName ?? a.TargetObjectId?.ToString() ?? "a user"}", 1000),
                                why = Cut(req?.Justification, 2000), ExpiresUtc = a.ExpiresUtc?.Date,
                                sourceRef = (req?.Id ?? a.Id).ToString(), decided = req?.CompletedUtc ?? a.CreatedUtc ?? clock.UtcNow,
                            });
                        await uow.ExecuteAsync("UPDATE intg.EntitlementAssignment SET DecisionId = @decisionId WHERE AssignmentId = @Id", new { decisionId, a.Id });
                    }

                    // Funding: an assignment needs an active envelope for the service (section 4.4, J1 sequence).
                    var funded = await uow.ScalarAsync<int>(
                        "SELECT COUNT(*) FROM core.FundingEnvelope WHERE FundingOwnerId = @FundingOwnerId AND ServiceId = @ServiceId AND Status = 'ACTIVE' AND PeriodStart <= @today AND PeriodEnd >= @today",
                        new { pkg.FundingOwnerId, pkg.ServiceId, today = clock.UtcNow.Date }) > 0;
                    if (!funded)
                    {
                        var key = $"UNFUNDED:{a.Id}";
                        unfundedKeys.Add(key);
                        var id = await findings.RaiseAsync(uow, key, new FindingWriter.NewFinding(FindingTypes.AssignmentWithoutFunding, "MEDIUM",
                            $"Access without funding: {pkg.DisplayName}",
                            "Entra delivered this access package, but the funding owner has no active envelope for the service.",
                            FundingOwnerId: pkg.FundingOwnerId, ExternalId: a.Id.ToString(), SyncRunId: runId));
                        if (id is not null) raised++;
                        // N15 goes out daily until resolved.
                        await outbox.EnqueueAsync(uow, new OutboxMessage
                        {
                            EventType = "N15", FundingOwnerId = pkg.FundingOwnerId,
                            Roles = { new RoleTarget(Roles.DelegatedApprover, ScopeTypes.FundingOwner, pkg.FundingOwnerId), new RoleTarget(Roles.PlatformOperator, ScopeTypes.Tenant, null) },
                            Subject = $"Access without funding: {pkg.DisplayName}",
                            Text = "An Entra access package assignment has no active funding envelope behind it. Approve an envelope or remove the assignment.",
                            LinkPath = "/Operations/Drift",
                        }, $"N15:{pkg.FundingOwnerId}:{pkg.AccessPackageId}:{clock.UtcNow:yyyyMMdd}");
                    }
                }

                // Policy scope group members that no delivered assignment explains.
                if (pkg.PolicyGroupId is Guid groupId)
                {
                    var outside = await uow.QueryAsync<Guid>(
                        """
                        SELECT gm.MemberObjectId FROM intg.GroupMember gm
                        WHERE gm.GroupId = @groupId
                          AND NOT EXISTS (SELECT 1 FROM intg.EntitlementAssignment ea
                                          JOIN intg.AccessPackageMap m ON m.AccessPackageId = ea.AccessPackageId
                                          WHERE m.PolicyGroupId = @groupId AND ea.TargetObjectId = gm.MemberObjectId AND ea.State = 'delivered')
                        """, new { groupId });
                    foreach (var member in outside)
                    {
                        var key = $"OUTSIDE:{groupId}:{member}";
                        outsideKeys.Add(key);
                        var id = await findings.RaiseAsync(uow, key, new FindingWriter.NewFinding(FindingTypes.MembershipOutsideScope, "MEDIUM",
                            $"Group member without an access package: {pkg.DisplayName}",
                            "Someone is in the policy scope group but not through the governed access package, so no approval or funding stands behind it.",
                            FundingOwnerId: pkg.FundingOwnerId, ExternalId: member.ToString(), SyncRunId: runId));
                        if (id is not null) raised++;
                    }
                }
            }
            await findings.ResolveMissingAsync(uow, FindingTypes.AssignmentWithoutFunding, unfundedKeys, "Funded or removed (seen by entitlement sync).");
            await findings.ResolveMissingAsync(uow, FindingTypes.MembershipOutsideScope, outsideKeys, "Explained by an assignment or removed (seen by entitlement sync).");
            if (activeRaci is null) log.LogWarning("No active RACI version: Entra assignments were read but not recorded as decisions.");
            return (read, raised, $"{map.Count} governed packages, {read} assignments, {raised} new findings.");
        }, ct);

    /// <summary>
    /// When no package is mapped yet, map the setup script's packages by funding owner code (the LOB) and service name.
    /// The register remains the source: an administrator can change the map afterwards.
    /// </summary>
    private async Task BootstrapMapAsync(Uow uow)
    {
        if (await uow.ScalarAsync<int>("SELECT COUNT(*) FROM intg.AccessPackageMap") > 0) return;
        foreach (var pg in tenant.PolicyGroups)
        {
            if (!Guid.TryParse(pg.AccessPackageId, out var pkgId) || pg.Lob is null || pg.Service is null) continue;
            var fo = await uow.ScalarAsync<int?>("SELECT FundingOwnerId FROM core.FundingOwner WHERE Code = @Lob", new { pg.Lob });
            var svc = await uow.ScalarAsync<int?>("SELECT TOP (1) ServiceId FROM core.Service WHERE Name = @Service OR Name LIKE @like ORDER BY CASE WHEN Name = @Service THEN 0 ELSE 1 END",
                new { pg.Service, like = "%" + pg.Service + "%" });
            if (fo is null || svc is null) continue;
            await uow.ExecuteAsync(
                "INSERT intg.AccessPackageMap (AccessPackageId, DisplayName, FundingOwnerId, ServiceId, Tier, PolicyGroupId, CatalogId) VALUES (@pkgId, @name, @fo, @svc, @tier, @group, @catalog)",
                new
                {
                    pkgId, name = $"{pg.Service} - {pg.Lob} - {pg.Tier}", fo, svc, tier = pg.Tier ?? "Standard",
                    group = Guid.TryParse(pg.GroupId, out var g) ? g : (Guid?)null, catalog = Guid.TryParse(tenant.CatalogId, out var c) ? c : (Guid?)null,
                });
        }
    }

    private static string? Cut(string? s, int max) => s is null ? null : s.Length <= max ? s : s[..max];
}
