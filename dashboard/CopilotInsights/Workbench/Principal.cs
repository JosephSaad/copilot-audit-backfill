using Dapper;
using Governance.Application;
using Governance.Application.Data;
using Governance.Domain;
using Microsoft.AspNetCore.Authorization;
using InsightsRoles = CopilotInsights.Infrastructure.Roles;
using ViewerService = CopilotInsights.Infrastructure.ViewerService;

namespace Governance.Web.Infrastructure;

public sealed record TestingOptions(bool TestAuthentication, bool AllowActAs);

public static class Policies
{
    public const string Central = "Central";
    public const string Operator = "Operator";
    public const string Admin = "Admin";
    public const string PlatformGovernance = "PlatformGovernance";
    public const string Auditor = "Auditor";
}

/// <summary>The person acting in this request, loaded once by <see cref="PrincipalMiddleware"/>.</summary>
public sealed class WebPrincipal : ICurrentPrincipal
{
    public bool IsAuthenticated { get; set; }
    public int PersonId { get; set; }
    public int? RealPersonId { get; set; }
    public string DisplayName { get; set; } = "";
    public string? RealDisplayName { get; set; }
    public string? Upn { get; set; }
    public Guid PublicId { get; set; }
    public bool IsCentral { get; set; }
    public IReadOnlyList<RoleGrant> Roles { get; set; } = Array.Empty<RoleGrant>();
    /// <summary>The Workbench page roles (AppRoles), from the person's Copilot Insights grants (<see cref="PrincipalLoader.PageRoles"/>).</summary>
    public IReadOnlySet<string> AppRoles { get; set; } = new HashSet<string>();
    public IReadOnlySet<string> Permissions { get; set; } = new HashSet<string>();
    public IReadOnlyDictionary<int, IReadOnlySet<string>> ScopedPermissions { get; set; } = new Dictionary<int, IReadOnlySet<string>>();
    public IReadOnlyList<(int FundingOwnerId, string Name)> FundingOwners { get; set; } = Array.Empty<(int, string)>();
    public IReadOnlyList<(string RoleCode, string Name, string? Scope)> RoleLabels { get; set; } = Array.Empty<(string, string, string?)>();
    public bool ActingAs => RealPersonId is not null;
    /// <summary>Development only: the signed-in person (not a persona) is an Insights administrator and may act as a test persona.</summary>
    public bool CanActAs { get; set; }
    public bool HasCentral(params string[] roles) => roles.Any(r => Roles.Any(g => g.RoleCode == r));
}

/// <summary>
/// Maps the person Copilot Insights signed in (Windows or Entra ID) to sec.Person, creating it on first visit, and
/// loads their Workbench roles. Which Workbench pages they may open comes from their Insights grants (Admin › Access);
/// the work they may do on those pages (RACI roles, delegations, LOB seat admin) from the Workbench's own records.
/// </summary>
public sealed class PrincipalLoader(Db db, ViewerService viewers, TestingOptions testing, ILogger<PrincipalLoader> log)
{
    public const string ActAsCookie = "cgw.actas";

    /// <summary>The Workbench page roles a set of Insights roles gives. Pure, for the tests.</summary>
    public static HashSet<string> PageRoles(IReadOnlySet<string> insightsRoles)
    {
        bool Has(string r) => insightsRoles.Contains(r);
        var set = new HashSet<string> { AppRoles.User };
        if (Has(InsightsRoles.Governance) || Has(InsightsRoles.GovernanceOperator)) set.Add(AppRoles.Central);
        if (Has(InsightsRoles.GovernanceOperator)) set.Add(AppRoles.Operator);
        if (Has(InsightsRoles.GovernanceAuditor)) set.Add(AppRoles.Auditor);
        if (Has(InsightsRoles.Admin)) set.Add(AppRoles.Admin);
        return set;
    }

    /// <param name="create">Records a person seen for the first time. The menu on Insights' pages only reads (false).</param>
    public async Task LoadAsync(HttpContext http, WebPrincipal p, bool create = true)
    {
        if (http.User.Identity?.IsAuthenticated != true) return;
        var viewer = await viewers.GetAsync();
        /* A person is their Entra object ID or their UPN, as the directory records them. A Windows account the directory
           has not matched yet has neither: it gets no Workbench person (one made from the sign-in name would hold a
           DOMAIN\name as its address, and split from the real person once the directory matches them). */
        var upn = viewer.Upn;
        Guid? oid = CopilotInsights.Infrastructure.EntraClaims.IsEntra(http.User) ? CopilotInsights.Infrastructure.EntraClaims.ObjectId(http.User)
                  : upn is null ? null : await viewers.ObjectIdOfAsync(upn);
        if (oid is null && string.IsNullOrWhiteSpace(upn))
        {
            log.LogInformation("{Name} is not in the directory yet (no UPN or object ID): no Workbench person.", viewer.Name);
            return;
        }

        await using var c = await db.OpenAsync(0, true, http.RequestAborted);
        var person = await FindOrCreateAsync(c, oid, upn, viewer.Name, create, log);
        if (person is null) return;
        if (!person.AccountEnabled) { log.LogWarning("Disabled account {PersonId} signed in", person.PersonId); return; }

        var real = person;
        var realAppRoles = PageRoles(viewer.Roles);
        // Development-only persona switch, for a signed-in administrator only.
        p.CanActAs = testing.AllowActAs && realAppRoles.Contains(AppRoles.Admin);

        // Act as a synthetic test persona; every action records the real person.
        if (p.CanActAs && http.Request.Cookies.TryGetValue(ActAsCookie, out var actAs) && Guid.TryParse(actAs, out var personaId))
        {
            var persona = await c.QuerySingleOrDefaultAsync<Person>("SELECT * FROM sec.Person WHERE PublicId = @personaId AND IsSynthetic = 1 AND AccountEnabled = 1", new { personaId });
            if (persona is not null && persona.PersonId != real.PersonId)
            {
                p.RealPersonId = real.PersonId;
                p.RealDisplayName = real.DisplayName;
                person = persona;
            }
        }

        var today = DateTime.UtcNow.Date;
        var roles = (await c.QueryAsync<(string RoleCode, string ScopeType, int? ScopeId, bool IsCentral, string Name, string? ScopeName)>(
            """
            SELECT ra.RoleCode, ra.ScopeType, ra.ScopeId, rd.IsCentral, rd.Name, fo.Name
            FROM sec.RoleAssignment ra JOIN sec.RoleDefinition rd ON rd.RoleCode = ra.RoleCode
            LEFT JOIN core.FundingOwner fo ON fo.FundingOwnerId = ra.ScopeId
            WHERE ra.PersonId = @PersonId AND ra.EffectiveFrom <= @today AND (ra.EffectiveTo IS NULL OR ra.EffectiveTo >= @today)
            """, new { person.PersonId, today })).ToList();
        var perms = (await c.QueryAsync<(string RoleCode, string Permission)>("SELECT RoleCode, Permission FROM sec.RolePermission")).ToLookup(x => x.RoleCode, x => x.Permission);
        var delegations = (await c.QueryAsync<(int FundingOwnerId, string Name)>(
            """
            SELECT DISTINCT d.FundingOwnerId, fo.Name FROM sec.Delegation d JOIN core.FundingOwner fo ON fo.FundingOwnerId = d.FundingOwnerId
            WHERE d.DelegatePersonId = @PersonId AND d.Status = 'ACTIVE' AND d.EffectiveFrom <= @today AND (d.EffectiveTo IS NULL OR d.EffectiveTo >= @today)
            """, new { person.PersonId, today })).ToList();

        var tenant = new HashSet<string>();
        var scoped = new Dictionary<int, HashSet<string>>();
        foreach (var r in roles)
        {
            if (r.ScopeType == ScopeTypes.Tenant) tenant.UnionWith(perms[r.RoleCode]);
            else if (r.ScopeId is int fo)
            {
                if (!scoped.TryGetValue(fo, out var set)) scoped[fo] = set = new HashSet<string>();
                set.UnionWith(perms[r.RoleCode]);
            }
        }
        foreach (var d in delegations)
        {
            if (!scoped.TryGetValue(d.FundingOwnerId, out var set)) scoped[d.FundingOwnerId] = set = new HashSet<string>();
            set.UnionWith(perms[Governance.Domain.Roles.DelegatedApprover]);
        }

        p.IsAuthenticated = true;
        p.PersonId = person.PersonId;
        p.PublicId = person.PublicId;
        p.DisplayName = person.DisplayName;
        p.Upn = person.UserPrincipalName;
        p.Roles = roles.Select(r => new RoleGrant(r.RoleCode, r.ScopeType, r.ScopeId)).Concat(delegations.Select(d => new RoleGrant(Governance.Domain.Roles.DelegatedApprover, ScopeTypes.FundingOwner, d.FundingOwnerId))).ToList();
        p.RoleLabels = roles.Select(r => (r.RoleCode, r.Name, r.ScopeName)).Concat(delegations.Select(d => (Governance.Domain.Roles.DelegatedApprover, "Delegated approver (delegation)", (string?)d.Name))).ToList();
        p.IsCentral = roles.Any(r => r.IsCentral);
        p.Permissions = tenant;
        p.ScopedPermissions = scoped.ToDictionary(kv => kv.Key, kv => (IReadOnlySet<string>)kv.Value);
        p.FundingOwners = roles.Where(r => r.ScopeId is not null).Select(r => (r.ScopeId!.Value, r.ScopeName ?? "")).Concat(delegations).Distinct().ToList();

        // A persona's page roles come from its Workbench roles but never exceed what the signed-in administrator holds.
        p.AppRoles = p.ActingAs
            ? DeriveAppRoles(roles.Select(r => r.RoleCode).ToHashSet()).Where(realAppRoles.Contains).ToHashSet()
            : realAppRoles;
    }

    /// <summary>A test persona's page roles, from its Workbench roles (Development "act as" only).</summary>
    public static HashSet<string> DeriveAppRoles(IReadOnlySet<string> sqlRoles)
    {
        var set = new HashSet<string> { AppRoles.User };
        if (sqlRoles.Any(r => r is Governance.Domain.Roles.PlatformOwner or Governance.Domain.Roles.IntakeAnalyst or Governance.Domain.Roles.PlatformOperator or Governance.Domain.Roles.Architect
                or Governance.Domain.Roles.RiskReviewer or Governance.Domain.Roles.FinOpsLead or Governance.Domain.Roles.FinOpsAnalyst or Governance.Domain.Roles.CommercialApprover
                or Governance.Domain.Roles.SourcingAgent or Governance.Domain.Roles.LicensingOwner or Governance.Domain.Roles.ReportingTeam or Governance.Domain.Roles.Steering))
            set.Add(AppRoles.Central);
        if (sqlRoles.Any(r => r is Governance.Domain.Roles.PlatformOperator or Governance.Domain.Roles.PlatformOwner or Governance.Domain.Roles.IntakeAnalyst)) set.Add(AppRoles.Operator);
        if (sqlRoles.Contains(Governance.Domain.Roles.WorkbenchAdmin)) set.Add(AppRoles.Admin);
        if (sqlRoles.Contains(Governance.Domain.Roles.Auditor)) set.Add(AppRoles.Auditor);
        return set;
    }

    /// <summary>
    /// The person by object ID, else by UPN. A UPN row that already belongs to another object ID is someone else (a UPN
    /// given again to a new hire): never theirs. A first visit records the person, once even when two requests race.
    /// </summary>
    internal static async Task<Person?> FindOrCreateAsync(Microsoft.Data.SqlClient.SqlConnection c, Guid? objectId, string? upn, string name, bool create, ILogger log)
    {
        var p = objectId is not null ? await c.QuerySingleOrDefaultAsync<Person>("SELECT * FROM sec.Person WHERE EntraObjectId = @objectId", new { objectId }) : null;
        if (p is null && !string.IsNullOrEmpty(upn))
        {
            var byUpn = (await c.QueryAsync<Person>("SELECT * FROM sec.Person WHERE UserPrincipalName = @upn ORDER BY CASE WHEN EntraObjectId IS NULL THEN 1 ELSE 0 END, PersonId", new { upn })).ToList();
            p = objectId is null ? byUpn.FirstOrDefault() : byUpn.FirstOrDefault(x => x.EntraObjectId is null);
            if (p is null && objectId is not null && byUpn.Count > 0)
                log.LogWarning("{Upn} signed in with object ID {ObjectId}, but the Workbench records that UPN for another object ID: recorded as a new person.", upn, objectId);
            if (p is not null && p.EntraObjectId is null && objectId is not null && create)
                await c.ExecuteAsync("UPDATE sec.Person SET EntraObjectId = @objectId WHERE PersonId = @PersonId AND EntraObjectId IS NULL", new { objectId, p.PersonId });
        }
        if (p is null && create)
        {
            /* One insert even when two first requests race: the check and the insert hold the key range. */
            await c.ExecuteAsync(
                """
                SET TRANSACTION ISOLATION LEVEL SERIALIZABLE;
                BEGIN TRANSACTION;
                IF NOT EXISTS (SELECT 1 FROM sec.Person WITH (UPDLOCK, HOLDLOCK)
                               WHERE (@objectId IS NOT NULL AND EntraObjectId = @objectId) OR (@objectId IS NULL AND UserPrincipalName = @upn))
                    INSERT sec.Person (EntraObjectId, UserPrincipalName, DisplayName, Mail, AccountEnabled) VALUES (@objectId, @upn, @name, @upn, 1);
                COMMIT;
                SET TRANSACTION ISOLATION LEVEL READ COMMITTED;
                """, new { objectId, upn, name });
            p = objectId is not null
                ? await c.QuerySingleOrDefaultAsync<Person>("SELECT * FROM sec.Person WHERE EntraObjectId = @objectId", new { objectId })
                : await c.QueryFirstOrDefaultAsync<Person>("SELECT * FROM sec.Person WHERE UserPrincipalName = @upn ORDER BY PersonId", new { upn });
        }
        return p;
    }
}

public sealed class PrincipalMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext http, PrincipalLoader loader, WebPrincipal principal)
    {
        if (http.User.Identity?.IsAuthenticated == true)
        {
            /* In the Workbench's own tenant, whatever tenant the site's pages show (WorkbenchState). */
            var tenants = http.RequestServices.GetRequiredService<CopilotInsights.Infrastructure.TenantAccessor>();
            tenants.Use(http.RequestServices.GetRequiredService<WorkbenchState>().Home(tenants));
            await loader.LoadAsync(http, principal);
            /* Someone the Workbench cannot record (not in the directory yet) reaches only the page that says so. */
            if (!principal.IsAuthenticated && !IsOpenToAll(http.Request.Path))
            {
                http.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
        }
        await next(http);
    }

    static bool IsOpenToAll(PathString path) =>
        path.StartsWithSegments("/workbench/AccessDenied", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/workbench/Error", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/workbench/Teams/Launch", StringComparison.OrdinalIgnoreCase);
}

public sealed record AppRoleRequirement(params string[] AnyOf) : IAuthorizationRequirement;

/// <summary>Insights administrators, or the Platform Owner or Steering (RACI, intake switch, central roles, wording).</summary>
public sealed record PlatformGovernanceRequirement : IAuthorizationRequirement;

public sealed class PlatformGovernanceHandler(IHttpContextAccessor http) : AuthorizationHandler<PlatformGovernanceRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, PlatformGovernanceRequirement requirement)
    {
        var p = http.HttpContext?.RequestServices.GetService<WebPrincipal>();
        if (p is { IsAuthenticated: true } && (p.AppRoles.Contains(AppRoles.Admin) || p.HasCentral(Governance.Domain.Roles.PlatformOwner, Governance.Domain.Roles.Steering)))
            context.Succeed(requirement);
        return Task.CompletedTask;
    }
}

/// <summary>Checks the page roles of the acting person (from their Insights grants, or derived for a test persona).</summary>
public sealed class AppRoleHandler(IHttpContextAccessor http) : AuthorizationHandler<AppRoleRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, AppRoleRequirement requirement)
    {
        var p = http.HttpContext?.RequestServices.GetService<WebPrincipal>();
        if (p is { IsAuthenticated: true } && requirement.AnyOf.Any(p.AppRoles.Contains)) context.Succeed(requirement);
        return Task.CompletedTask;
    }
}
