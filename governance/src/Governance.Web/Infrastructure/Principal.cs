using System.Security.Claims;
using System.Text.Encodings.Web;
using Dapper;
using Governance.Application;
using Governance.Application.Data;
using Governance.Domain;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

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
    public IReadOnlySet<string> AppRoles { get; set; } = new HashSet<string>();
    public IReadOnlySet<string> Permissions { get; set; } = new HashSet<string>();
    public IReadOnlyDictionary<int, IReadOnlySet<string>> ScopedPermissions { get; set; } = new Dictionary<int, IReadOnlySet<string>>();
    public IReadOnlyList<(int FundingOwnerId, string Name)> FundingOwners { get; set; } = Array.Empty<(int, string)>();
    public IReadOnlyList<(string RoleCode, string Name, string? Scope)> RoleLabels { get; set; } = Array.Empty<(string, string, string?)>();
    public bool ActingAs => RealPersonId is not null;
    /// <summary>Development only: the signed-in person (not a persona) holds Workbench.Admin and may act as a test persona.</summary>
    public bool CanActAs { get; set; }
    public bool HasCentral(params string[] roles) => roles.Any(r => Roles.Any(g => g.RoleCode == r));
}

/// <summary>Maps the signed-in user to sec.Person (creating it on first sign-in) and loads roles and permissions from SQL.</summary>
public sealed class PrincipalLoader(Db db, TestingOptions testing, ILogger<PrincipalLoader> log)
{
    public const string ActAsCookie = "cgw.actas";

    public async Task LoadAsync(HttpContext http, WebPrincipal p)
    {
        var user = http.User;
        if (user.Identity?.IsAuthenticated != true) return;
        var oid = user.FindFirstValue("http://schemas.microsoft.com/identity/claims/objectidentifier") ?? user.FindFirstValue("oid");
        var upn = user.FindFirstValue("preferred_username") ?? user.FindFirstValue(ClaimTypes.Upn) ?? user.FindFirstValue(ClaimTypes.Name);
        var name = user.FindFirstValue("name") ?? upn ?? "Unknown";

        await using var c = await db.OpenAsync(0, true, http.RequestAborted);
        var person = await FindOrCreateAsync(c, oid, upn, name);
        if (person is null) return;
        if (!person.AccountEnabled) { log.LogWarning("Disabled account {PersonId} signed in", person.PersonId); return; }

        var real = person;
        var today0 = DateTime.UtcNow.Date;
        var appRolesFromClaims = user.FindAll(ClaimTypes.Role).Select(r => r.Value).Concat(user.FindAll("roles").Select(r => r.Value)).ToHashSet();
        // The signed-in person's own app roles: Entra claims, or (loopback test sign-in) derived from their SQL roles.
        var realAppRoles = testing.TestAuthentication
            ? DeriveAppRoles((await c.QueryAsync<string>(
                "SELECT RoleCode FROM sec.RoleAssignment WHERE PersonId = @PersonId AND EffectiveFrom <= @today0 AND (EffectiveTo IS NULL OR EffectiveTo >= @today0)",
                new { real.PersonId, today0 })).ToHashSet())
            : appRolesFromClaims.Where(r => r.StartsWith("Workbench.", StringComparison.Ordinal)).ToHashSet();
        // Development-only persona switch, for a signed-in Workbench administrator only.
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
            set.UnionWith(perms[Roles.DelegatedApprover]);
        }

        p.IsAuthenticated = true;
        p.PersonId = person.PersonId;
        p.PublicId = person.PublicId;
        p.DisplayName = person.DisplayName;
        p.Upn = person.UserPrincipalName;
        p.Roles = roles.Select(r => new RoleGrant(r.RoleCode, r.ScopeType, r.ScopeId)).Concat(delegations.Select(d => new RoleGrant(Roles.DelegatedApprover, ScopeTypes.FundingOwner, d.FundingOwnerId))).ToList();
        p.RoleLabels = roles.Select(r => (r.RoleCode, r.Name, r.ScopeName)).Concat(delegations.Select(d => (Roles.DelegatedApprover, "Delegated approver (delegation)", (string?)d.Name))).ToList();
        p.IsCentral = roles.Any(r => r.IsCentral);
        p.Permissions = tenant;
        p.ScopedPermissions = scoped.ToDictionary(kv => kv.Key, kv => (IReadOnlySet<string>)kv.Value);
        p.FundingOwners = roles.Where(r => r.ScopeId is not null).Select(r => (r.ScopeId!.Value, r.ScopeName ?? "")).Concat(delegations).Distinct().ToList();

        // Entra app roles gate pages. A persona or the loopback test scheme derives them from its SQL roles.
        // A persona's app roles come from its SQL roles but never exceed what the signed-in administrator holds.
        p.AppRoles = p.ActingAs
            ? DeriveAppRoles(roles.Select(r => r.RoleCode).ToHashSet()).Where(realAppRoles.Contains).ToHashSet()
            : realAppRoles;
    }

    public static HashSet<string> DeriveAppRoles(IReadOnlySet<string> sqlRoles)
    {
        var set = new HashSet<string> { AppRoles.User };
        if (sqlRoles.Any(r => r is Roles.PlatformOwner or Roles.IntakeAnalyst or Roles.PlatformOperator or Roles.Architect or Roles.RiskReviewer or Roles.FinOpsLead
                or Roles.FinOpsAnalyst or Roles.CommercialApprover or Roles.SourcingAgent or Roles.LicensingOwner or Roles.ReportingTeam or Roles.Steering))
            set.Add(AppRoles.Central);
        if (sqlRoles.Any(r => r is Roles.PlatformOperator or Roles.PlatformOwner or Roles.IntakeAnalyst)) set.Add(AppRoles.Operator);
        if (sqlRoles.Contains(Roles.WorkbenchAdmin)) set.Add(AppRoles.Admin);
        if (sqlRoles.Contains(Roles.Auditor)) set.Add(AppRoles.Auditor);
        return set;
    }

    private static async Task<Person?> FindOrCreateAsync(Microsoft.Data.SqlClient.SqlConnection c, string? oid, string? upn, string name)
    {
        Guid? objectId = Guid.TryParse(oid, out var g) ? g : null;
        var p = objectId is not null ? await c.QuerySingleOrDefaultAsync<Person>("SELECT * FROM sec.Person WHERE EntraObjectId = @objectId", new { objectId }) : null;
        if (p is null && !string.IsNullOrEmpty(upn))
        {
            p = await c.QueryFirstOrDefaultAsync<Person>("SELECT * FROM sec.Person WHERE UserPrincipalName = @upn ORDER BY CASE WHEN EntraObjectId IS NULL THEN 1 ELSE 0 END", new { upn });
            if (p is not null && p.EntraObjectId is null && objectId is not null)
                await c.ExecuteAsync("UPDATE sec.Person SET EntraObjectId = @objectId WHERE PersonId = @PersonId", new { objectId, p.PersonId });
        }
        if (p is null && objectId is not null)
        {
            await c.ExecuteAsync("INSERT sec.Person (EntraObjectId, UserPrincipalName, DisplayName, Mail, AccountEnabled) VALUES (@objectId, @upn, @name, @upn, 1)", new { objectId, upn, name });
            p = await c.QuerySingleOrDefaultAsync<Person>("SELECT * FROM sec.Person WHERE EntraObjectId = @objectId", new { objectId });
        }
        return p;
    }
}

public sealed class PrincipalMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext http, PrincipalLoader loader, WebPrincipal principal)
    {
        if (http.User.Identity?.IsAuthenticated == true && !http.Request.Path.StartsWithSegments("/health"))
            await loader.LoadAsync(http, principal);
        await next(http);
    }
}

public sealed record AppRoleRequirement(params string[] AnyOf) : IAuthorizationRequirement;

/// <summary>Workbench administrators, or the Platform Owner or Steering (RACI, intake switch, central roles, wording).</summary>
public sealed record PlatformGovernanceRequirement : IAuthorizationRequirement;

public sealed class PlatformGovernanceHandler(IHttpContextAccessor http) : AuthorizationHandler<PlatformGovernanceRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, PlatformGovernanceRequirement requirement)
    {
        var p = http.HttpContext?.RequestServices.GetService<WebPrincipal>();
        if (p is { IsAuthenticated: true } && (p.AppRoles.Contains(AppRoles.Admin) || p.HasCentral(Roles.PlatformOwner, Roles.Steering)))
            context.Succeed(requirement);
        return Task.CompletedTask;
    }
}

/// <summary>Checks the app roles of the acting person (from Entra claims, or derived for a test persona).</summary>
public sealed class AppRoleHandler(IHttpContextAccessor http) : AuthorizationHandler<AppRoleRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, AppRoleRequirement requirement)
    {
        var p = http.HttpContext?.RequestServices.GetService<WebPrincipal>();
        if (p is { IsAuthenticated: true } && requirement.AnyOf.Any(p.AppRoles.Contains)) context.Succeed(requirement);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Development-only sign-in for automated page checks: loopback requests carrying X-Test-Upn are signed in as that
/// sec.Person. Registered only when Testing:TestAuthentication is true in Development.
/// </summary>
public sealed class TestAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "LoopbackTest";
    public const string Header = "X-Test-Upn";
    public const string CookiePrefix = "cgw.testupn.";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var remote = Context.Connection.RemoteIpAddress;
        if (remote is null || !System.Net.IPAddress.IsLoopback(remote)) return Task.FromResult(AuthenticateResult.NoResult());
        // Query string first (switching persona by URL always wins), then the header, then this port's cookie.
        var cookie = CookiePrefix + (Context.Request.Host.Port?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "default");
        var fromQuery = Request.Query["testUpn"].FirstOrDefault();
        var upn = !string.IsNullOrWhiteSpace(fromQuery) ? fromQuery : Request.Headers[Header].FirstOrDefault() ?? Request.Cookies[cookie];
        if (string.IsNullOrWhiteSpace(upn)) return Task.FromResult(AuthenticateResult.NoResult());
        if (!string.IsNullOrWhiteSpace(fromQuery))
            Response.Cookies.Append(cookie, fromQuery, new CookieOptions { HttpOnly = true, SameSite = SameSiteMode.Strict });
        var identity = new ClaimsIdentity(new[] { new Claim("preferred_username", upn), new Claim("name", upn) }, SchemeName);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = 401;
        return Response.WriteAsync("Test sign-in: send the X-Test-Upn header, or open any page with ?testUpn=<upn>.");
    }

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties)
    {
        Response.Redirect("/AccessDenied");
        return Task.CompletedTask;
    }
}
