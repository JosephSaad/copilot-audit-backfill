using System.Security.Claims;
using System.Security.Principal;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace CopilotInsights.Infrastructure;

/// <summary>The roles an admin can grant. Admin runs the admin module; Organization sees everyone; the rest unlock a persona's named detail.</summary>
public static class Roles
{
    public const string Admin = "Admin", Organization = "Organization", Adoption = "Adoption", Licensing = "Licensing",
        Security = "Security", Compliance = "Compliance", Agents = "Agents", Enablement = "Enablement";

    public static readonly string[] All = [Admin, Organization, Adoption, Licensing, Security, Compliance, Agents, Enablement];

    public static readonly Dictionary<string, string> Describe = new()
    {
        [Admin] = "Runs the admin module: access, settings, reference data, health and the audit log. Sees no usage data by itself.",
        [Organization] = "Sees the whole organisation's aggregates, whatever their place in it.",
        [Adoption] = "Sees named people who were licensed but have not started.",
        [Licensing] = "Sees named unused licences and named unlicensed Copilot Chat users.",
        [Security] = "Sees the individual interactions flagged for jailbreak, prompt injection or blocked resources.",
        [Compliance] = "Sees who accessed which sensitivity-labelled or blocked resources.",
        [Agents] = "Sees the named people using each agent.",
        [Enablement] = "Sees named champions and people who may need support.",
    };
}

/// <summary>Who is looking, and what they may see.</summary>
public sealed class Viewer
{
    public required string WindowsName { get; init; }
    public string? Upn { get; init; }
    public string? DisplayName { get; init; }
    /// <summary>1 = the configured top of the organisation, 2 = reports to them. Null when not under the top or no top is set.</summary>
    public int? Level { get; init; }
    public bool InDirectory { get; init; }
    public int PeopleBelow { get; init; }
    public required IReadOnlySet<string> Roles { get; init; }
    /// <summary>Set when an administrator is viewing as this person (Development only): the administrator's own name.</summary>
    public string? ViewingAsBy { get; init; }

    public bool IsAdmin => Roles.Contains(Infrastructure.Roles.Admin);
    public bool OrgWide => AccessRules.SeesWholeOrganisation(Level, Roles);
    public bool Has(string role) => Roles.Contains(role);
    public string Name => DisplayName ?? Upn ?? WindowsName;
}

/// <summary>The security model's two decisions, kept pure so they can be tested on their own.</summary>
public static class AccessRules
{
    /// <summary>Level 1 or 2 below the configured top, or granted Organization. Admin alone is not enough.</summary>
    public static bool SeesWholeOrganisation(int? level, IReadOnlySet<string> roles) =>
        roles.Contains(Roles.Organization) || level is 1 or 2;

    /// <summary>
    /// Whether a viewer may look at the subtree under <paramref name="rootUpn"/>:
    /// anyone who sees the whole organisation may; others only at themselves or
    /// someone under them (<paramref name="rootIsUnderViewer"/>, from dash.OrgClosure).
    /// </summary>
    public static bool MayViewSubtree(Viewer viewer, string rootUpn, bool rootIsUnderViewer) =>
        viewer.OrgWide
        || (viewer.Upn is not null && string.Equals(viewer.Upn, rootUpn, StringComparison.OrdinalIgnoreCase))
        || rootIsUnderViewer;
}

/// <summary>Resolves the signed-in Windows account to a Viewer, once per request.</summary>
public sealed class ViewerService(
    IHttpContextAccessor http, Db db, IMemoryCache cache, IOptions<InsightsOptions> options,
    IWebHostEnvironment env, ILogger<ViewerService> log)
{
    readonly InsightsOptions _o = options.Value;
    Viewer? _viewer;

    public async Task<Viewer> GetAsync()
    {
        if (_viewer is not null) return _viewer;
        var ctx = http.HttpContext ?? throw new InvalidOperationException("No request.");
        var user = ctx.User;
        var windowsName = user.Identity?.Name ?? "(anonymous)";
        var upn = ResolveUpn(user, windowsName);

        var roles = await RolesForAsync(upn, windowsName, user);
        if (IsBootstrapAdmin(upn, windowsName, user)) roles.Add(Roles.Admin);

        /* Development only: an administrator may look through someone else's eyes. */
        string? viewAs = null;
        if (env.IsDevelopment() && _o.AllowViewAs && roles.Contains(Roles.Admin))
        {
            viewAs = ctx.Request.Query["as"].FirstOrDefault() ?? ctx.Request.Cookies["insights-as"];
            if (ctx.Request.Query.ContainsKey("as"))
            {
                if (string.IsNullOrWhiteSpace(viewAs)) { ctx.Response.Cookies.Delete("insights-as"); viewAs = null; }
                else ctx.Response.Cookies.Append("insights-as", viewAs, new CookieOptions { HttpOnly = true, SameSite = SameSiteMode.Strict, IsEssential = true });
            }
        }
        if (!string.IsNullOrWhiteSpace(viewAs))
        {
            var asRoles = await RolesForAsync(viewAs, viewAs, null);
            return _viewer = await BuildAsync(viewAs, viewAs, asRoles, viewingAsBy: windowsName);
        }
        return _viewer = await BuildAsync(windowsName, upn, roles, null);
    }

    async Task<Viewer> BuildAsync(string windowsName, string? upn, HashSet<string> roles, string? viewingAsBy)
    {
        PersonRow? p = null;
        if (upn is not null)
            p = (await db.QueryAsync<PersonRow>(
                "SELECT DisplayName, Level, PeopleBelow FROM dash.Person WHERE Upn = @upn", new { upn })).FirstOrDefault();
        return new Viewer
        {
            WindowsName = windowsName, Upn = upn, DisplayName = p?.DisplayName, Level = p?.Level,
            InDirectory = p is not null, PeopleBelow = p?.PeopleBelow ?? 0, Roles = roles, ViewingAsBy = viewingAsBy,
        };
    }

    sealed record PersonRow(string? DisplayName, byte? Level, int PeopleBelow)
    {
        public int? LevelInt => Level;
    }

    async Task<HashSet<string>> RolesForAsync(string? upn, string windowsName, ClaimsPrincipal? user)
    {
        var grants = await db.QueryAsync<(string Principal, string PrincipalType, string Role)>(
            "SELECT Principal, PrincipalType, Role FROM dash.RoleGrant");
        var roles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in grants)
        {
            var hit = g.PrincipalType == "User"
                ? Same(g.Principal, upn) || Same(g.Principal, windowsName)
                : user is not null && IsInGroup(user, g.Principal);
            if (hit) roles.Add(g.Role);
        }
        return roles;
    }

    bool IsBootstrapAdmin(string? upn, string windowsName, ClaimsPrincipal user) =>
        _o.BootstrapAdmins.Any(b => Same(b, upn) || Same(b, windowsName) || IsInGroup(user, b));

    static bool Same(string a, string? b) => b is not null && string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    bool IsInGroup(ClaimsPrincipal user, string group)
    {
        if (!group.Contains('\\')) return false;   // DOMAIN\Group only
        try { return user.IsInRole(group); }
        catch (Exception ex) { log.LogDebug(ex, "Group check failed for {Group}", group); return false; }
    }

    /// <summary>
    /// The viewer's UPN: the configured map first, then Active Directory (by the
    /// account's SID), then the sign-in name itself when it already is one.
    /// Cached for ten minutes per account.
    /// </summary>
    string? ResolveUpn(ClaimsPrincipal user, string windowsName)
    {
        if (_o.IdentityMap.TryGetValue(windowsName, out var mapped)) return mapped;
        return cache.GetOrCreate("upn:" + windowsName, e =>
        {
            e.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10);
            if (_o.ResolveUpnFromDirectory && OperatingSystem.IsWindows() && user.Identity is WindowsIdentity wi && wi.User is not null)
            {
                try
                {
                    var domain = windowsName.Contains('\\') ? windowsName[..windowsName.IndexOf('\\')] : null;
                    using var pc = domain is null
                        ? new System.DirectoryServices.AccountManagement.PrincipalContext(System.DirectoryServices.AccountManagement.ContextType.Domain)
                        : new System.DirectoryServices.AccountManagement.PrincipalContext(System.DirectoryServices.AccountManagement.ContextType.Domain, domain);
                    using var up = System.DirectoryServices.AccountManagement.UserPrincipal.FindByIdentity(
                        pc, System.DirectoryServices.AccountManagement.IdentityType.Sid, wi.User.Value);
                    if (!string.IsNullOrWhiteSpace(up?.UserPrincipalName)) return up!.UserPrincipalName;
                }
                catch (Exception ex) { log.LogInformation("No directory UPN for {Name}: {Message}", windowsName, ex.Message); }
            }
            return windowsName.Contains('@') ? windowsName : null;
        });
    }
}
