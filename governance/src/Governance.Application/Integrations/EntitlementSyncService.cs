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
public sealed class EntitlementSyncService(Db db, IEntitlementApi entitlements, IDirectoryApi directory, FindingWriter findings, OutboxWriter outbox, IClock clock,
    TenantSettings tenant, ILogger<EntitlementSyncService> log)
{
    private sealed record MapRow(Guid AccessPackageId, string DisplayName, int FundingOwnerId, int ServiceId, string Tier, Guid? PolicyGroupId);

    /// <summary>Who approved a delivered assignment, as read from Entra (or why it could not be read).</summary>
    public sealed record Approver(Guid? ObjectId, string? Name, DateTime? DecidedUtc, string? Justification, string? Unknown);

    // Graph calls run with no transaction open; each package is then written in its own short transaction.
    public Task<Guid> RunAsync(CancellationToken ct)
        => SyncRuns.RunAsync(db, IntegrationCodes.Entitlements, "APP_ONLY", null, async runId =>
        {
            var (map, activeRaci, entraPerson) = await db.InTransactionAsync(async uow =>
            {
                await BootstrapMapAsync(uow);
                var m = (await uow.QueryAsync<MapRow>("SELECT AccessPackageId, DisplayName, FundingOwnerId, ServiceId, Tier, PolicyGroupId FROM intg.AccessPackageMap")).ToList();
                return (m, await uow.ScalarAsync<int?>("SELECT RaciVersionId FROM sec.RaciVersion WHERE Status = 'ACTIVE'"),
                        await uow.ScalarAsync<int?>("SELECT PersonId FROM sec.Person WHERE UserPrincipalName = N'system:entra-import'"));
            }, ct);
            int read = 0, raised = 0;
            var unfundedKeys = new List<string>();
            var outsideKeys = new List<string>();

            foreach (var pkg in map)
            {
                // 1. Read from Graph.
                var assignments = await entitlements.AssignmentsAsync(pkg.AccessPackageId, ct);
                var requests = await entitlements.AssignmentRequestsAsync(pkg.AccessPackageId, ct);
                read += assignments.Count;
                var decided = (await db.InTransactionAsync(u => u.QueryAsync<Guid>(
                    "SELECT AssignmentId FROM intg.EntitlementAssignment WHERE AccessPackageId = @AccessPackageId AND DecisionId IS NOT NULL", new { pkg.AccessPackageId }), ct)).ToHashSet();
                var approvers = new Dictionary<Guid, Approver>();
                foreach (var a in assignments.Where(a => string.Equals(a.State, "delivered", StringComparison.OrdinalIgnoreCase) && !decided.Contains(a.Id)))
                    approvers[a.Id] = await ReadApproverAsync(MatchRequest(requests, a), ct);
                var unknownPeople = new List<DirectoryUser>();
                var known = (await db.InTransactionAsync(u => u.QueryAsync<Guid>(
                    "SELECT EntraObjectId FROM sec.Person WHERE EntraObjectId IN (SELECT CAST(value AS uniqueidentifier) FROM OPENJSON(@ids))",
                    new { ids = System.Text.Json.JsonSerializer.Serialize(approvers.Values.Where(x => x.ObjectId is not null).Select(x => x.ObjectId!.Value).Distinct()) }), ct)).ToHashSet();
                foreach (var id in approvers.Values.Where(x => x.ObjectId is Guid g && !known.Contains(g)).Select(x => x.ObjectId!.Value).Distinct())
                    if (await directory.GetUserAsync(id, ct) is DirectoryUser du) unknownPeople.Add(du);

                // 2. Write this package in one short transaction.
                raised += await db.InTransactionAsync(uow => WritePackageAsync(uow, runId, pkg, assignments, requests, approvers, unknownPeople,
                    activeRaci, entraPerson, unfundedKeys, outsideKeys), ct);
            }
            await db.InTransactionAsync(async uow =>
            {
                await findings.ResolveMissingAsync(uow, FindingTypes.AssignmentWithoutFunding, unfundedKeys, "Funded or removed (seen by entitlement sync).");
                await findings.ResolveMissingAsync(uow, FindingTypes.MembershipOutsideScope, outsideKeys, "Explained by an assignment or removed (seen by entitlement sync).");
            }, ct);
            if (activeRaci is null) log.LogWarning("No active RACI version: Entra assignments were read but not recorded as decisions.");
            return (read, raised, $"{map.Count} governed packages, {read} assignments, {raised} new findings.");
        }, ct);

    private static AssignmentRequestData? MatchRequest(IReadOnlyList<AssignmentRequestData> requests, AccessPackageAssignmentData a)
        => requests.Where(r => r.AssignmentId == a.Id || (r.TargetObjectId == a.TargetObjectId && r.RequestType is "userAdd" or "adminAdd"))
                   .OrderByDescending(r => r.CreatedUtc).FirstOrDefault();

    /// <summary>
    /// The person who decided: the reviewer of the last approved stage for a request that needed approval; the
    /// administrator who assigned it directly (adminAdd); otherwise unknown, with the reason.
    /// </summary>
    public async Task<Approver> ReadApproverAsync(AssignmentRequestData? req, CancellationToken ct)
    {
        if (req is null) return new Approver(null, null, null, null, "no assignment request was found in Entra");
        if (req.RequestType == "adminAdd")
            return req.TargetObjectId is Guid admin
                ? new Approver(admin, null, req.CompletedUtc ?? req.CreatedUtc, req.Justification, null)
                : new Approver(null, null, req.CompletedUtc, req.Justification, "the administrator who assigned it is not named by Entra");
        var stages = await entitlements.ApprovalStagesAsync(req.Id, ct);
        if (stages is null) return new Approver(null, null, req.CompletedUtc, req.Justification, "Entra did not return the approval stages to the catalog reader role");
        var approved = stages.Where(s => s.ReviewedById is not null && (s.ReviewResult ?? "").StartsWith("Approve", StringComparison.OrdinalIgnoreCase))
                             .OrderByDescending(s => s.ReviewedUtc).FirstOrDefault();
        return approved is null
            ? new Approver(null, null, req.CompletedUtc, req.Justification, stages.Count == 0 ? "the policy needed no approval" : "no approved stage names a reviewer")
            : new Approver(approved.ReviewedById, approved.ReviewedByName, approved.ReviewedUtc ?? req.CompletedUtc, approved.Justification ?? req.Justification, null);
    }

    private async Task<int> WritePackageAsync(Uow uow, long runId, MapRow pkg, IReadOnlyList<AccessPackageAssignmentData> assignments, IReadOnlyList<AssignmentRequestData> requests,
        IReadOnlyDictionary<Guid, Approver> approvers, IReadOnlyList<DirectoryUser> newPeople, int? activeRaci, int? entraPerson, List<string> unfundedKeys, List<string> outsideKeys)
    {
        var raised = 0;
        foreach (var u in newPeople)
            await uow.ExecuteAsync(
                """
                IF NOT EXISTS (SELECT 1 FROM sec.Person WHERE EntraObjectId = @Id)
                    INSERT sec.Person (EntraObjectId, UserPrincipalName, DisplayName, Mail, JobTitle, Department, Division, CostCenterCode, AccountEnabled, LastSyncedUtc, SyncRunId)
                    VALUES (@Id, @UserPrincipalName, COALESCE(@DisplayName, @UserPrincipalName, N'(no name)'), @Mail, @JobTitle, @Department, @Division, @CostCenter, COALESCE(@AccountEnabled, 1), SYSUTCDATETIME(), @runId);
                """, new { u.Id, u.UserPrincipalName, u.DisplayName, u.Mail, u.JobTitle, u.Department, u.Division, u.CostCenter, u.AccountEnabled, runId });

        foreach (var a in assignments)
        {
            var personId = a.TargetObjectId is Guid t ? await uow.ScalarAsync<int?>("SELECT PersonId FROM sec.Person WHERE EntraObjectId = @t", new { t }) : null;
            var req = MatchRequest(requests, a);
            approvers.TryGetValue(a.Id, out var approver);
            await uow.ExecuteAsync(
                """
                MERGE intg.EntitlementAssignment WITH (HOLDLOCK) AS t USING (SELECT @Id AS AssignmentId) AS s ON t.AssignmentId = s.AssignmentId
                WHEN MATCHED THEN UPDATE SET State = @State, ExpiresUtc = @ExpiresUtc, TargetPersonId = COALESCE(@personId, t.TargetPersonId),
                     RequestId = COALESCE(@RequestId, t.RequestId), Justification = COALESCE(@Justification, t.Justification),
                     ApproverObjectId = COALESCE(@ApproverObjectId, t.ApproverObjectId), LastSeenRunId = @runId
                WHEN NOT MATCHED THEN INSERT (AssignmentId, AccessPackageId, TargetObjectId, TargetPersonId, State, ExpiresUtc, CreatedUtc, RequestId, ApproverObjectId, Justification, LastSeenRunId)
                     VALUES (@Id, @AccessPackageId, @TargetObjectId, @personId, @State, @ExpiresUtc, @CreatedUtc, @RequestId, @ApproverObjectId, @Justification, @runId);
                """,
                new
                {
                    a.Id, a.State, a.ExpiresUtc, personId, RequestId = req?.Id, Justification = Cut(req?.Justification, 1000), runId,
                    AccessPackageId = pkg.AccessPackageId, a.TargetObjectId, a.CreatedUtc, ApproverObjectId = approver?.ObjectId,
                });

            if (!string.Equals(a.State, "delivered", StringComparison.OrdinalIgnoreCase)) continue;

            // The decision record (section 7.5: "every decision, including those imported from Entra"), once per assignment.
            var hasDecision = await uow.ScalarAsync<long?>("SELECT DecisionId FROM intg.EntitlementAssignment WITH (UPDLOCK) WHERE AssignmentId = @Id", new { a.Id });
            if (hasDecision is null && activeRaci is int raci && approver is not null)
            {
                var decider = approver.ObjectId is Guid oid ? await uow.ScalarAsync<int?>("SELECT PersonId FROM sec.Person WHERE EntraObjectId = @oid", new { oid }) : null;
                var unknownNote = decider is null
                    ? $" The approver is recorded as Microsoft Entra because {approver.Unknown ?? "the approver is not in the Workbench directory"}."
                    : "";
                if ((decider ?? entraPerson) is int who)
                {
                    var decisionId = await uow.ScalarAsync<long>(
                        """
                        INSERT wf.Decision (PublicId, FundingOwnerId, DecisionType, Outcome, DeciderPersonId, DeciderRoleCode, RaciVersionId, RaciActivityCode,
                                            ScopeText, ConditionsText, ReasonText, ExpiresOn, Source, SourceRef, DecidedAtUtc)
                        OUTPUT inserted.DecisionId
                        VALUES (NEWID(), @FundingOwnerId, 'ACCESS_ASSIGNMENT', 'APPROVED', @who, 'ENTRA_APPROVER', @raci, 'FUNDING_BUDGET',
                                @scope, NULL, @why, @ExpiresUtc, 'ENTRA', @sourceRef, @decided)
                        """,
                        new
                        {
                            pkg.FundingOwnerId, who, raci,
                            scope = Cut($"Access package {pkg.DisplayName} ({pkg.Tier}) for {a.TargetPrincipalName ?? a.TargetObjectId?.ToString() ?? "a user"}, approved in Entra My Access.{unknownNote}", 1000),
                            why = Cut(approver.Justification ?? req?.Justification, 2000), ExpiresUtc = a.ExpiresUtc?.Date,
                            sourceRef = (req?.Id ?? a.Id).ToString(), decided = approver.DecidedUtc ?? req?.CompletedUtc ?? a.CreatedUtc ?? clock.UtcNow,
                        });
                    await uow.ExecuteAsync("UPDATE intg.EntitlementAssignment SET DecisionId = @decisionId WHERE AssignmentId = @Id", new { decisionId, a.Id });
                }
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
                    LinkPath = "/workbench/Operations/Findings",
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
        return raised;
    }

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
