using Governance.Application.Data;
using Governance.Domain;

namespace Governance.Application.Workflow;

/// <summary>
/// Resolves a RACI cell to people at run time (section 7.3): the role in scope today, active delegations
/// where the stage allows them, minus people excluded by segregation of duties.
/// </summary>
public sealed class AssignmentResolver(IClock clock)
{
    /// <summary>Business owner roles map to the shared-service owner for an Enterprise Shared funding owner.</summary>
    public async Task<string> EffectiveRoleAsync(Uow uow, string roleCode, int? fundingOwnerId)
    {
        if (roleCode == Roles.LobBusinessOwner && fundingOwnerId is int fo)
        {
            var type = await uow.ScalarAsync<string>("SELECT OwnerType FROM core.FundingOwner WHERE FundingOwnerId = @fo", new { fo });
            if (type == "ENTERPRISE_SHARED") return Roles.SharedServiceOwner;
        }
        return roleCode;
    }

    public async Task<string> ScopeTypeOfAsync(Uow uow, string roleCode)
        => roleCode == Roles.Requester ? ScopeTypes.FundingOwner
           : await uow.ScalarAsync<string>("SELECT ScopeType FROM sec.RoleDefinition WHERE RoleCode = @roleCode", new { roleCode }) ?? ScopeTypes.Tenant;

    /// <summary>People holding the role in the scope today, plus delegates when allowed.</summary>
    public async Task<IReadOnlyList<int>> HoldersAsync(Uow uow, string roleCode, string scopeType, int? scopeId, bool includeDelegates)
    {
        var today = clock.UtcNow.Date;
        var people = (await uow.QueryAsync<int>(
            """
            SELECT DISTINCT ra.PersonId
            FROM sec.RoleAssignment ra
            JOIN sec.Person p ON p.PersonId = ra.PersonId AND p.AccountEnabled = 1 AND (p.LeaveDate IS NULL OR p.LeaveDate > @today)
            WHERE ra.RoleCode = @roleCode
              AND (@scopeType = 'TENANT' AND ra.ScopeType = 'TENANT' OR ra.ScopeType = 'FUNDING_OWNER' AND ra.ScopeId = @scopeId)
              AND ra.EffectiveFrom <= @today AND (ra.EffectiveTo IS NULL OR ra.EffectiveTo >= @today)
            """, new { roleCode, scopeType, scopeId, today })).ToList();

        if (includeDelegates && scopeId is int fo && Roles.BusinessOwnerRoles.Contains(roleCode))
        {
            var delegates = await uow.QueryAsync<int>(
                """
                SELECT DISTINCT d.DelegatePersonId
                FROM sec.Delegation d
                JOIN sec.Person p ON p.PersonId = d.DelegatePersonId AND p.AccountEnabled = 1
                WHERE d.FundingOwnerId = @fo AND d.Status = 'ACTIVE'
                  AND d.EffectiveFrom <= @today AND (d.EffectiveTo IS NULL OR d.EffectiveTo >= @today)
                """, new { fo, today });
            people.AddRange(delegates.Where(d => !people.Contains(d)));
        }
        return people;
    }

    /// <summary>Is this person allowed to act on the task (holds the role in scope, is the assignee, or is a delegate)?</summary>
    public async Task<(bool Allowed, string? RoleUsed, int? DelegationId, string? Reason)> CanActAsync(Uow uow, TaskRow task, int personId, bool allowsDelegation)
    {
        var excluded = await uow.ScalarAsync<string>("SELECT RuleCode FROM wf.TaskExclusion WHERE TaskId = @TaskId AND PersonId = @personId", new { task.TaskId, personId });
        if (excluded is not null) return (false, null, null, SodText(excluded));

        if (task.AssignedPersonId == personId) return (true, task.AssignedRoleCode ?? "ASSIGNEE", null, null);
        if (task.AssignedPersonId is not null)
            return (false, null, null, task.ClaimedUtc is null ? "This task is assigned to someone else." : "Someone else has claimed this task; they can release it.");
        if (task.AssignedRoleCode is null) return (false, null, null, "This task has no role to act through.");

        var holders = await HoldersAsync(uow, task.AssignedRoleCode, task.ScopeType ?? ScopeTypes.Tenant, task.ScopeId, includeDelegates: false);
        if (holders.Contains(personId)) return (true, task.AssignedRoleCode, null, null);

        if (allowsDelegation && task.ScopeId is int fo && Roles.BusinessOwnerRoles.Contains(task.AssignedRoleCode))
        {
            var today = clock.UtcNow.Date;
            var delegationId = await uow.ScalarAsync<int?>(
                """
                SELECT TOP (1) DelegationId FROM sec.Delegation
                WHERE FundingOwnerId = @fo AND DelegatePersonId = @personId AND Status = 'ACTIVE'
                  AND EffectiveFrom <= @today AND (EffectiveTo IS NULL OR EffectiveTo >= @today)
                ORDER BY EffectiveFrom DESC
                """, new { fo, personId, today });
            if (delegationId is not null) return (true, Roles.DelegatedApprover, delegationId, null);
        }
        return (false, null, null, "You don't hold the role this task needs in this scope.");
    }

    public static string SodText(string rule) => rule switch
    {
        "SOD1" => "You submitted or sponsor this request, so you cannot decide it (SoD1).",
        "SOD2" => "You approved this request, so you cannot fulfill or verify it (SoD2).",
        "SOD3" => "You configured this change, so a second operator verifies it (SoD3).",
        "SOD4" => "You already recorded a different decision type on this request (SoD4).",
        "SOD5" => "Workbench administrators cannot record business decisions (SoD5).",
        _ => $"Segregation of duties ({rule}).",
    };
}
