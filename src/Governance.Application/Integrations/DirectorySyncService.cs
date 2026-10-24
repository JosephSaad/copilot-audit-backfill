using Governance.Application.Data;
using Governance.Application.Outbox;
using Governance.Domain;
using Microsoft.Extensions.Logging;

namespace Governance.Application.Integrations;

/// <summary>
/// X1 (every 15 minutes): users by Graph delta into sec.Person, governed group membership into intg.GroupMember,
/// central roles from mapped Entra groups (Source ENTRA_GROUP), and vacancy findings for leavers and role holders
/// who changed cost center (section 5.8). Synthetic test personas (no Entra object ID) are never touched.
/// </summary>
public sealed class DirectorySyncService(Db db, IDirectoryApi directory, FindingWriter findings, IClock clock, TenantSettings tenant, ILogger<DirectorySyncService> log)
{
    private const string DeltaKey = "graph.users.delta";

    public Task<Guid> RunAsync(CancellationToken ct)
        => SyncRuns.RunAsync(db, IntegrationCodes.Directory, "APP_ONLY", null, async (runId, uow) =>
        {
            var people = await SyncUsersAsync(uow, runId, ct);
            var memberships = await SyncGroupsAsync(uow, runId, ct);
            var roles = await SyncCentralRolesAsync(uow);
            var raised = await DetectVacanciesAsync(uow, runId);
            return (people + memberships, raised, $"{people} people changed, {memberships} memberships read, {roles} central role changes, {raised} findings.");
        }, ct);

    private async Task<int> SyncUsersAsync(Uow uow, long runId, CancellationToken ct)
    {
        var link = await uow.ScalarAsync<string>("SELECT DeltaLink FROM intg.DeltaToken WHERE TokenKey = @DeltaKey", new { DeltaKey });
        var changed = 0;
        var managers = new List<(Guid User, Guid Manager)>();
        while (true)
        {
            var page = await directory.UsersDeltaAsync(link, ct);
            foreach (var u in page.Items)
            {
                if (u.Removed)
                {
                    changed += await uow.ExecuteAsync("UPDATE sec.Person SET AccountEnabled = 0, LastSyncedUtc = @now, SyncRunId = @runId WHERE EntraObjectId = @Id AND AccountEnabled = 1",
                        new { u.Id, now = clock.UtcNow, runId });
                    continue;
                }
                // Delta pages carry only changed properties after the first sync: keep what we have for missing ones.
                changed += await uow.ExecuteAsync(
                    """
                    MERGE sec.Person AS t
                    USING (SELECT @Id AS EntraObjectId) AS s ON t.EntraObjectId = s.EntraObjectId
                    WHEN MATCHED THEN UPDATE SET
                        DisplayName = COALESCE(@DisplayName, t.DisplayName), UserPrincipalName = COALESCE(@UserPrincipalName, t.UserPrincipalName),
                        Mail = COALESCE(@Mail, t.Mail), JobTitle = COALESCE(@JobTitle, t.JobTitle), Department = COALESCE(@Department, t.Department),
                        Division = COALESCE(@Division, t.Division), CostCenterCode = COALESCE(@CostCenter, t.CostCenterCode),
                        AccountEnabled = COALESCE(@AccountEnabled, t.AccountEnabled), LeaveDate = COALESCE(@LeaveDate, t.LeaveDate),
                        LastSyncedUtc = @now, SyncRunId = @runId
                    WHEN NOT MATCHED THEN INSERT (EntraObjectId, UserPrincipalName, DisplayName, Mail, JobTitle, Department, Division, CostCenterCode, AccountEnabled, LeaveDate, LastSyncedUtc, SyncRunId)
                        VALUES (@Id, @UserPrincipalName, COALESCE(@DisplayName, @UserPrincipalName, N'(no name)'), @Mail, @JobTitle, @Department, @Division, @CostCenter, COALESCE(@AccountEnabled, 1), @LeaveDate, @now, @runId);
                    """,
                    new
                    {
                        u.Id, u.DisplayName, u.UserPrincipalName, u.Mail, u.JobTitle, u.Department, u.Division, u.CostCenter, u.AccountEnabled,
                        LeaveDate = u.LeaveDateTime?.Date, now = clock.UtcNow, runId,
                    });
                if (u.ManagerId is Guid m) managers.Add((u.Id, m));
            }
            if (page.NextLink is not null) { link = page.NextLink; continue; }
            if (page.DeltaLink is not null)
                await uow.ExecuteAsync(
                    "MERGE intg.DeltaToken AS t USING (SELECT @DeltaKey AS TokenKey) AS s ON t.TokenKey = s.TokenKey WHEN MATCHED THEN UPDATE SET DeltaLink = @DeltaLink, UpdatedUtc = SYSUTCDATETIME() WHEN NOT MATCHED THEN INSERT (TokenKey, DeltaLink) VALUES (@DeltaKey, @DeltaLink);",
                    new { DeltaKey, page.DeltaLink });
            break;
        }
        foreach (var (user, manager) in managers)
            await uow.ExecuteAsync("UPDATE p SET ManagerPersonId = m.PersonId FROM sec.Person p JOIN sec.Person m ON m.EntraObjectId = @manager WHERE p.EntraObjectId = @user",
                new { user, manager });
        return changed;
    }

    /// <summary>Membership of governed groups only: central role groups and policy scope groups (least privilege, section 6.5).</summary>
    private async Task<int> SyncGroupsAsync(Uow uow, long runId, CancellationToken ct)
    {
        var groups = (await uow.QueryAsync<Guid>(
            "SELECT EntraGroupId FROM sec.GroupRoleMap UNION SELECT PolicyGroupId FROM intg.AccessPackageMap WHERE PolicyGroupId IS NOT NULL")).ToHashSet();
        foreach (var pg in tenant.PolicyGroups)
            if (Guid.TryParse(pg.GroupId, out var g)) groups.Add(g);
        var read = 0;
        foreach (var groupId in groups)
        {
            IReadOnlyList<Guid> members;
            try { members = await directory.GroupMemberIdsAsync(groupId, ct); }
            catch (IntegrationPermissionException) { throw; }
            catch (Exception ex) { log.LogWarning(ex, "Group {GroupId} could not be read; keeping its last good membership.", groupId); continue; }
            foreach (var m in members)
            {
                await uow.ExecuteAsync(
                    "MERGE intg.GroupMember AS t USING (SELECT @groupId AS GroupId, @m AS MemberObjectId) AS s ON t.GroupId = s.GroupId AND t.MemberObjectId = s.MemberObjectId WHEN MATCHED THEN UPDATE SET LastSeenRunId = @runId WHEN NOT MATCHED THEN INSERT (GroupId, MemberObjectId, LastSeenRunId) VALUES (@groupId, @m, @runId);",
                    new { groupId, m, runId });
                read++;
            }
            await uow.ExecuteAsync("DELETE FROM intg.GroupMember WHERE GroupId = @groupId AND LastSeenRunId <> @runId", new { groupId, runId });
        }
        return read;
    }

    /// <summary>Central roles follow their mapped Entra groups: members gain the role, people who left the group lose it.</summary>
    private async Task<int> SyncCentralRolesAsync(Uow uow)
    {
        var today = clock.UtcNow.Date;
        var added = await uow.ExecuteAsync(
            """
            INSERT sec.RoleAssignment (PersonId, RoleCode, ScopeType, ScopeId, EffectiveFrom, Source, SourceGroupId, ReviewDueOn)
            SELECT DISTINCT p.PersonId, m.RoleCode, 'TENANT', NULL, @today, 'ENTRA_GROUP', m.EntraGroupId, DATEADD(YEAR, 1, @today)
            FROM sec.GroupRoleMap m
            JOIN intg.GroupMember gm ON gm.GroupId = m.EntraGroupId
            JOIN sec.Person p ON p.EntraObjectId = gm.MemberObjectId AND p.AccountEnabled = 1
            JOIN sec.RoleDefinition rd ON rd.RoleCode = m.RoleCode AND rd.ScopeType = 'TENANT'
            WHERE NOT EXISTS (SELECT 1 FROM sec.RoleAssignment ra
                              WHERE ra.PersonId = p.PersonId AND ra.RoleCode = m.RoleCode AND ra.ScopeType = 'TENANT'
                                AND ra.EffectiveFrom <= @today AND (ra.EffectiveTo IS NULL OR ra.EffectiveTo >= @today));
            """, new { today });
        var ended = await uow.ExecuteAsync(
            """
            UPDATE ra SET EffectiveTo = DATEADD(DAY, -1, @today)
            FROM sec.RoleAssignment ra
            JOIN sec.Person p ON p.PersonId = ra.PersonId
            WHERE ra.Source = 'ENTRA_GROUP' AND (ra.EffectiveTo IS NULL OR ra.EffectiveTo >= @today)
              AND NOT EXISTS (SELECT 1 FROM intg.GroupMember gm WHERE gm.GroupId = ra.SourceGroupId AND gm.MemberObjectId = p.EntraObjectId)
              AND EXISTS (SELECT 1 FROM sec.GroupRoleMap m WHERE m.EntraGroupId = ra.SourceGroupId);
            """, new { today });
        return added + ended;
    }

    /// <summary>Leavers and role holders whose cost center changed raise a vacancy (N20); funding owners without a business owner are suspended.</summary>
    private async Task<int> DetectVacanciesAsync(Uow uow, long runId)
    {
        var today = clock.UtcNow.Date;
        var raised = 0;
        var holders = await uow.QueryAsync<(int PersonId, string DisplayName, string RoleCode, string ScopeType, int? ScopeId, bool Enabled, DateTime? LeaveDate)>(
            """
            SELECT p.PersonId, p.DisplayName, ra.RoleCode, ra.ScopeType, ra.ScopeId, p.AccountEnabled, p.LeaveDate
            FROM sec.RoleAssignment ra JOIN sec.Person p ON p.PersonId = ra.PersonId
            WHERE p.EntraObjectId IS NOT NULL AND ra.EffectiveFrom <= @today AND (ra.EffectiveTo IS NULL OR ra.EffectiveTo >= @today)
              AND (p.AccountEnabled = 0 OR p.LeaveDate <= @today)
            """, new { today });
        foreach (var h in holders)
        {
            var id = await findings.RaiseAsync(uow, $"VACANCY:LEAVER:{h.PersonId}:{h.RoleCode}:{h.ScopeId}", new FindingWriter.NewFinding(
                FindingTypes.Vacancy, "HIGH", $"A leaver still holds {Label(h.RoleCode)}",
                $"{h.DisplayName} is disabled or past their leave date and still holds this role. Assign someone else and end the assignment.",
                FundingOwnerId: h.ScopeType == ScopeTypes.FundingOwner ? h.ScopeId : null, SyncRunId: runId));
            if (id is long fid)
            {
                raised++;
                await findings.NotifyAsync(uow, "N20", fid, $"Role vacancy: {Label(h.RoleCode)}", $"A leaver holds {Label(h.RoleCode)}; the role needs a new holder.",
                    Array.Empty<int>(), Targets(h.ScopeType, h.ScopeId), fundingOwnerId: h.ScopeId);
            }
        }

        // Cost center changes of LOB role holders: the person may no longer belong to the LOB they act for.
        var moved = await uow.QueryAsync<(int PersonId, string DisplayName, string RoleCode, int ScopeId, string? NewCc, string? OldCc)>(
            """
            SELECT p.PersonId, p.DisplayName, ra.RoleCode, ra.ScopeId, p.CostCenterCode AS NewCc, h.CostCenterCode AS OldCc
            FROM sec.Person p
            JOIN intg.PersonCostCenterSeen h ON h.PersonId = p.PersonId
            JOIN sec.RoleAssignment ra ON ra.PersonId = p.PersonId AND ra.ScopeType = 'FUNDING_OWNER'
                 AND ra.EffectiveFrom <= @today AND (ra.EffectiveTo IS NULL OR ra.EffectiveTo >= @today)
            WHERE p.EntraObjectId IS NOT NULL AND ISNULL(p.CostCenterCode, N'') <> ISNULL(h.CostCenterCode, N'')
            """, new { today });
        foreach (var m in moved)
        {
            var id = await findings.RaiseAsync(uow, $"VACANCY:MOVED:{m.PersonId}:{m.RoleCode}:{m.ScopeId}:{m.NewCc}", new FindingWriter.NewFinding(
                FindingTypes.Vacancy, "MEDIUM", $"A {Label(m.RoleCode)} changed cost center",
                $"{m.DisplayName} moved from cost center {m.OldCc ?? "(none)"} to {m.NewCc ?? "(none)"}. Confirm they still act for this funding owner.",
                FundingOwnerId: m.ScopeId, SyncRunId: runId));
            if (id is long fid)
            {
                raised++;
                await findings.NotifyAsync(uow, "N20", fid, $"Check role: {Label(m.RoleCode)}", "A role holder changed cost center; confirm the role still applies.",
                    Array.Empty<int>(), Targets(ScopeTypes.FundingOwner, m.ScopeId), fundingOwnerId: m.ScopeId);
            }
        }
        await uow.ExecuteAsync(
            """
            MERGE intg.PersonCostCenterSeen AS t
            USING (SELECT PersonId, CostCenterCode FROM sec.Person WHERE EntraObjectId IS NOT NULL) AS s ON t.PersonId = s.PersonId
            WHEN MATCHED AND ISNULL(t.CostCenterCode, N'') <> ISNULL(s.CostCenterCode, N'') THEN UPDATE SET CostCenterCode = s.CostCenterCode, SeenUtc = SYSUTCDATETIME()
            WHEN NOT MATCHED THEN INSERT (PersonId, CostCenterCode) VALUES (s.PersonId, s.CostCenterCode);
            """);

        // A funding owner without an active business owner is suspended for new requests (section 5.8).
        var orphaned = await uow.QueryAsync<(int FundingOwnerId, string Name)>(
            """
            SELECT fo.FundingOwnerId, fo.Name FROM core.FundingOwner fo
            WHERE fo.Status = 'ACTIVE'
              AND NOT EXISTS (SELECT 1 FROM sec.RoleAssignment ra JOIN sec.Person p ON p.PersonId = ra.PersonId
                              WHERE ra.ScopeType = 'FUNDING_OWNER' AND ra.ScopeId = fo.FundingOwnerId
                                AND ra.RoleCode IN ('LOB_BUSINESS_OWNER','SHARED_SERVICE_OWNER')
                                AND ra.EffectiveFrom <= @today AND (ra.EffectiveTo IS NULL OR ra.EffectiveTo >= @today)
                                AND p.AccountEnabled = 1 AND (p.LeaveDate IS NULL OR p.LeaveDate > @today))
            """, new { today });
        foreach (var fo in orphaned)
        {
            await uow.ExecuteAsync("UPDATE core.FundingOwner SET Status = 'SUSPENDED' WHERE FundingOwnerId = @FundingOwnerId AND Status = 'ACTIVE'", new { fo.FundingOwnerId });
            var id = await findings.RaiseAsync(uow, $"VACANCY:OWNER:{fo.FundingOwnerId}", new FindingWriter.NewFinding(
                FindingTypes.Vacancy, "HIGH", $"{fo.Name} has no active business owner",
                "The funding owner is suspended for new requests until a business owner is assigned. Existing workloads keep running.",
                FundingOwnerId: fo.FundingOwnerId, SyncRunId: runId));
            if (id is long fid)
            {
                raised++;
                await findings.NotifyAsync(uow, "N20", fid, $"{fo.Name} suspended: no business owner", "Assign a business owner to reopen requests for this funding owner.",
                    Array.Empty<int>(), new[] { new RoleTarget(Roles.PlatformOwner, ScopeTypes.Tenant, null) }, fundingOwnerId: fo.FundingOwnerId);
            }
        }
        if (raised > 0) log.LogInformation("Directory sync raised {Count} vacancy findings.", raised);
        return raised;
    }

    private static IEnumerable<RoleTarget> Targets(string scopeType, int? scopeId)
    {
        yield return new RoleTarget(Roles.PlatformOwner, ScopeTypes.Tenant, null);
        if (scopeType == ScopeTypes.FundingOwner && scopeId is int fo) yield return new RoleTarget(Roles.LobBusinessOwner, ScopeTypes.FundingOwner, fo);
    }

    private static string Label(string roleCode) => roleCode.Replace('_', ' ').ToLowerInvariant();
}
