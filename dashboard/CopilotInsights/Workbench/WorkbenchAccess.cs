using System.Net;
using Dapper;
using Governance.Application.Data;
using Governance.Domain;

namespace Governance.Web.Infrastructure;

/// <summary>
/// The Workbench part of Admin › Access › Check someone's access: which Workbench pages their Insights roles open, and
/// the work the Workbench's own records give them (RACI roles, LOB seat admin, delegations).
/// </summary>
public static class WorkbenchAccess
{
    /// <summary>What each Workbench page role opens, in plain words.</summary>
    public static IReadOnlyList<string> PagesOpened(IReadOnlySet<string> pageRoles)
    {
        var list = new List<string>();
        if (pageRoles.Contains(AppRoles.User) || pageRoles.Contains(AppRoles.Central) || pageRoles.Contains(AppRoles.Admin)) list.Add("Ownership and approvals: their own requests, approvals and workloads");
        if (pageRoles.Contains(AppRoles.Registry) || pageRoles.Contains(AppRoles.Central) || pageRoles.Contains(AppRoles.Admin)) list.Add("Ownership registry: the parts of it they may see");
        if (pageRoles.Contains(AppRoles.Central)) list.Add("the approval queues, the whole registry, consumption events and findings");
        if (pageRoles.Contains(AppRoles.Operator)) list.Add("sync and freshness, and attestations");
        if (pageRoles.Contains(AppRoles.Auditor) || pageRoles.Contains(AppRoles.Central)) list.Add("the Workbench's reports and audit trail");
        if (pageRoles.Contains(AppRoles.Admin)) list.Add("the Workbench settings");
        return list;
    }

    /// <summary>
    /// The people with work in the Workbench today, by UPN: a RACI or seat role in force, or an active delegation. Their
    /// tasks arrive by email and open the Ownership and approvals pages. Null when the Workbench database does not answer.
    /// </summary>
    public static async Task<IReadOnlyList<string>?> PeopleWithWorkAsync(IServiceProvider services, CancellationToken ct)
    {
        try
        {
            await using var c = await services.GetRequiredService<Db>().OpenAsync(0, true, ct);
            var today = DateTime.UtcNow.Date;
            return (await c.QueryAsync<string>(
                """
                SELECT DISTINCT p.UserPrincipalName FROM sec.Person p
                WHERE p.AccountEnabled = 1 AND p.IsSynthetic = 0 AND p.UserPrincipalName IS NOT NULL
                  AND (EXISTS (SELECT 1 FROM sec.RoleAssignment ra WHERE ra.PersonId = p.PersonId AND ra.EffectiveFrom <= @today AND (ra.EffectiveTo IS NULL OR ra.EffectiveTo >= @today))
                       OR EXISTS (SELECT 1 FROM sec.Delegation d WHERE d.DelegatePersonId = p.PersonId AND d.Status = 'ACTIVE' AND d.EffectiveFrom <= @today AND (d.EffectiveTo IS NULL OR d.EffectiveTo >= @today)))
                """, new { today })).ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(WorkbenchAccess)).LogWarning(ex, "The people with Workbench work could not be read.");
            return null;
        }
    }

    public static async Task<string> DescribeAsync(IServiceProvider services, string upn, IReadOnlySet<string> insightsRoles, CancellationToken ct, IReadOnlySet<string>? sections = null)
    {
        static string E(string? s) => WebUtility.HtmlEncode(s ?? "");
        var pages = PagesOpened(PrincipalLoader.PageRoles(insightsRoles, sections));
        var html = $"<p>Governance Workbench pages: {(pages.Count == 0 ? "none" : E(string.Join("; ", pages)))}.</p>";
        try
        {
            await using var c = await services.GetRequiredService<Db>().OpenAsync(0, true, ct);
            var today = DateTime.UtcNow.Date;
            var roles = (await c.QueryAsync<(string Name, string? Scope)>(
                """
                SELECT rd.Name, fo.Name
                FROM sec.Person p JOIN sec.RoleAssignment ra ON ra.PersonId = p.PersonId JOIN sec.RoleDefinition rd ON rd.RoleCode = ra.RoleCode
                LEFT JOIN core.FundingOwner fo ON fo.FundingOwnerId = ra.ScopeId
                WHERE p.UserPrincipalName = @upn AND ra.EffectiveFrom <= @today AND (ra.EffectiveTo IS NULL OR ra.EffectiveTo >= @today)
                ORDER BY rd.Name, fo.Name
                """, new { upn, today })).ToList();
            var delegations = (await c.QueryAsync<string>(
                """
                SELECT fo.Name FROM sec.Person p JOIN sec.Delegation d ON d.DelegatePersonId = p.PersonId JOIN core.FundingOwner fo ON fo.FundingOwnerId = d.FundingOwnerId
                WHERE p.UserPrincipalName = @upn AND d.Status = 'ACTIVE' AND d.EffectiveFrom <= @today AND (d.EffectiveTo IS NULL OR d.EffectiveTo >= @today)
                """, new { upn, today })).Distinct().ToList();
            html += $"<p>Workbench roles (given in Workbench settings › Workbench roles, or by a line of business's owner): {(roles.Count == 0 ? "none" : E(string.Join(", ", roles.Select(r => r.Scope is null ? r.Name : $"{r.Name} for {r.Scope}"))))}." +
                    (delegations.Count == 0 ? "" : $" Delegated approver for {E(string.Join(", ", delegations))}.") + "</p>";
        }
        catch (Exception ex)
        {
            services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(WorkbenchAccess)).LogWarning(ex, "The Workbench roles of {Upn} could not be read.", upn);
            html += "<p>Workbench roles: not read; the Workbench database did not answer.</p>";
        }
        return html;
    }
}
