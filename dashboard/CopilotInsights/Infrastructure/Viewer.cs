using System.Security.Claims;
using System.Security.Principal;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using CopilotInsights.Data;

namespace CopilotInsights.Infrastructure;

/// <summary>The roles an admin can grant. Admin runs the admin module; Organization sees everyone; the rest unlock a persona's named detail.</summary>
public static class Roles
{
    public const string Admin = "Admin", Organization = "Organization", Adoption = "Adoption", Licensing = "Licensing",
        Security = "Security", Compliance = "Compliance", Agents = "Agents", Enablement = "Enablement", Persona = "Persona";

    public static readonly string[] All = [Admin, Persona, Organization, Adoption, Licensing, Security, Compliance, Agents, Enablement];

    /// <summary>The roles that open Admin pages and nothing else (2.6.4): neither lets anyone past the dashboard's limit.</summary>
    public static bool AdminOnly(string role) =>
        string.Equals(role, Admin, StringComparison.OrdinalIgnoreCase) || string.Equals(role, Persona, StringComparison.OrdinalIgnoreCase);

    public static readonly Dictionary<string, string> Describe = new()
    {
        [Admin] = "Runs the admin module: access, settings, reference data, health and the audit log. Sees no usage data by itself.",
        [Persona] = "Runs the Admin pages ticked for them, and no others. Sees no usage data by itself, and cannot grant or remove Admin or Persona.",
        [Organization] = "Sees the whole organization's aggregates, whatever their place in it.",
        [Adoption] = "The Adoption section, and in it the named people who were licensed but have not started.",
        [Licensing] = "The Licensing and Forecasting sections, and in them the named unused licenses, unlicensed Copilot Chat users and license recommendations.",
        [Security] = "The Security section, and in it the individual interactions flagged for jailbreak, prompt injection or blocked resources.",
        [Compliance] = "The Compliance section, and in it who accessed which sensitivity-labeled or blocked resources.",
        [Agents] = "The Agents section, and in it the named people using each agent.",
        [Enablement] = "The Enablement section, and in it the named champions and people who may need support.",
    };
}

/// <summary>Who is looking, and what they may see.</summary>
public sealed class Viewer
{
    public required string WindowsName { get; init; }
    public string? Upn { get; init; }
    public string? DisplayName { get; init; }
    /// <summary>1 = the configured top of the organization, 2 = reports to them. Null when not under the top or no top is set.</summary>
    public int? Level { get; init; }
    public bool InDirectory { get; init; }
    public int PeopleBelow { get; init; }
    public required IReadOnlySet<string> Roles { get; init; }
    /// <summary>
    /// Whether the dashboard's limit (Admin > Access, MaxViewerLevel, 2.4.4) lets them in. An administrator
    /// who is not still reaches the Admin pages, so a wrong limit can always be put right.
    /// </summary>
    public bool Admitted { get; init; } = true;
    /// <summary>The limit in force when they signed in: 0 for none, else levels 1 to this.</summary>
    public int MaxLevel { get; init; }

    /// <summary>The Admin pages a Persona grant opens (2.6.4), by path: empty for everyone else.</summary>
    public IReadOnlySet<string> AdminPages { get; init; } = new HashSet<string>();

    /// <summary>A full administrator: every Admin page, and the only one who may grant Admin or Persona.</summary>
    public bool IsAdmin => Roles.Contains(Infrastructure.Roles.Admin);
    /// <summary>Whether the viewer may open and change this Admin page: an administrator, or a Persona granted it (2.6.4).</summary>
    public bool CanAdmin(string? path) => IsAdmin || AdminPages.Contains(PersonaPages.Normalize(path));
    /// <summary>Any Admin page at all.</summary>
    public bool AnyAdmin => IsAdmin || AdminPages.Count > 0;
    public bool OrgWide => AccessRules.SeesWholeOrganization(Level, Roles);
    public bool Has(string role) => Roles.Contains(role);
    public string Name => DisplayName ?? Upn ?? WindowsName;
}

/// <summary>The security model's two decisions, kept pure so they can be tested on their own.</summary>
public static class AccessRules
{
    /// <summary>Level 1 or 2 below the configured top, or granted Organization. Admin alone is not enough.</summary>
    public static bool SeesWholeOrganization(int? level, IReadOnlySet<string> roles) =>
        roles.Contains(Roles.Organization) || level is 1 or 2;

    /// <summary>
    /// The dashboard's limit (2.4.4): with <paramref name="maxLevel"/> 0 everyone may use it, as before;
    /// otherwise only levels 1 to <paramref name="maxLevel"/> below the configured top, and anyone granted
    /// a role other than Admin or Persona (Admin > Access is the list of exceptions). Administrators alone
    /// are not admitted: they keep the Admin pages, which show no usage data. The level comes from the
    /// directory record of the SID-checked sign-in, never from the request.
    /// </summary>
    public static bool MayUseDashboard(int? level, IReadOnlySet<string> roles, int maxLevel) =>
        maxLevel <= 0
        || level is { } l && l >= 1 && l <= maxLevel
        || roles.Any(r => !Roles.AdminOnly(r));

    /// <summary>
    /// The SID comparison: the directory records no SID for the person, or records exactly the SID in the
    /// account's Windows token. Pure, for the tests. Which way the person was found decides whether no SID
    /// is enough (see <see cref="MayBe"/>).
    /// </summary>
    public static bool SidMatches(string? recordedSid, string? tokenSid) =>
        string.IsNullOrWhiteSpace(recordedSid) || (tokenSid is not null && string.Equals(recordedSid.Trim(), tokenSid, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The SID rule (2.4.12): whether the signed-in account may be the person a UPN names, by how the UPN was found.
    /// By the account's SID in the directory: yes. From Insights:IdentityMap, which an administrator wrote: yes, unless
    /// the directory records a different SID for the person. From Active Directory's answer, or a sign-in name that is
    /// a UPN: only when the directory records the person's SID and it is this account's. Those answers are the account's
    /// own say, and in another domain or forest an account can carry anyone's UPN; with no SID on record they let it be
    /// anyone without one (a manager outside the directory table, an administrator granted by UPN). Pure, for the tests.
    /// </summary>
    public static bool MayBe(UpnSource source, string? recordedSid, string? tokenSid) => source switch
    {
        UpnSource.Sid => true,
        UpnSource.IdentityMap => SidMatches(recordedSid, tokenSid),
        _ => !string.IsNullOrWhiteSpace(recordedSid) && SidMatches(recordedSid, tokenSid),
    };

    /// <summary>
    /// Whether a viewer may look at the subtree under <paramref name="rootUpn"/>:
    /// anyone who sees the whole organization may; others only at themselves or
    /// someone under them (<paramref name="rootIsUnderViewer"/>, from dash.OrgClosure).
    /// </summary>
    public static bool MayViewSubtree(Viewer viewer, string rootUpn, bool rootIsUnderViewer) =>
        viewer.OrgWide
        || (viewer.Upn is not null && string.Equals(viewer.Upn, rootUpn, StringComparison.OrdinalIgnoreCase))
        || rootIsUnderViewer;
}

/// <summary>How a sign-in's UPN was found.</summary>
public enum UpnSource { Sid, IdentityMap, Directory }

/// <summary>Resolves the signed-in Windows account to a Viewer, once per request.</summary>
/// <remarks>
/// The viewer is always the account Windows authenticated: nothing in the URL, a cookie or a
/// header can change who they are. The UPN they are matched to is checked against the SID in
/// their Windows token wherever the directory records one (see <see cref="ResolveUpnAsync"/>).
/// </remarks>
public sealed class ViewerService(
    IHttpContextAccessor http, Db db, IMemoryCache cache, IOptions<InsightsOptions> options,
    TenantAccessor tenants, Settings settings, ILogger<ViewerService> log)
{
    readonly InsightsOptions _o = options.Value;
    Viewer? _viewer;

    public async Task<Viewer> GetAsync()
    {
        if (_viewer is not null) return _viewer;
        return _viewer = await ResolveAsync();
    }

    /// <summary>The viewer in the current tenant's database, not cached: for pages that look across tenants.</summary>
    public async Task<Viewer> ResolveAsync()
    {
        var ctx = http.HttpContext ?? throw new InvalidOperationException("No request.");
        var user = ctx.User;
        var windowsName = user.Identity?.Name ?? "(anonymous)";
        var upn = await ResolveUpnAsync(user, windowsName);

        var (roles, pages) = await RolesForAsync(upn, windowsName, user);
        if (IsBootstrapAdmin(upn, windowsName, user)) roles.Add(Roles.Admin);
        return await BuildAsync(windowsName, upn, roles, pages);
    }

    async Task<Viewer> BuildAsync(string windowsName, string? upn, HashSet<string> roles, HashSet<string> pages)
    {
        PersonRow? p = null;
        if (upn is not null)
            p = (await db.QueryAsync<PersonRow>(
                "SELECT DisplayName, Level, PeopleBelow FROM dash.Person WHERE Upn = @upn", new { upn })).FirstOrDefault();
        var max = Math.Clamp(await settings.IntAsync("MaxViewerLevel", 0), 0, 15);
        return new Viewer
        {
            WindowsName = windowsName, Upn = upn, DisplayName = p?.DisplayName, Level = p?.Level,
            InDirectory = p is not null, PeopleBelow = p?.PeopleBelow ?? 0, Roles = roles,
            MaxLevel = max, Admitted = AccessRules.MayUseDashboard(p?.Level, roles, max), AdminPages = pages,
        };
    }

    sealed record PersonRow(string? DisplayName, byte? Level, int PeopleBelow)
    {
        public int? LevelInt => Level;
    }

    /// <summary>The roles granted to the account, its UPN or its groups, and the Admin pages of every Persona grant among them.</summary>
    async Task<(HashSet<string> Roles, HashSet<string> Pages)> RolesForAsync(string? upn, string windowsName, ClaimsPrincipal? user)
    {
        /* Pages arrived in 2.6.4: until the schema script is run again, there are none. */
        var grants = await db.QueryAsync<(string Principal, string PrincipalType, string Role, string? Pages)>(@"
IF COL_LENGTH(N'dash.RoleGrant', N'Pages') IS NULL SELECT Principal, PrincipalType, Role, CAST(NULL AS NVARCHAR(1000)) AS Pages FROM dash.RoleGrant;
ELSE EXEC (N'SELECT Principal, PrincipalType, Role, Pages FROM dash.RoleGrant;');");
        var roles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in grants)
        {
            /* Whatever the grant says it is: a DOMAIN\name recorded as a user may be a group, and the other way round. */
            var hit = Same(g.Principal, upn) || Same(g.Principal, windowsName)
                      || (user is not null && IsInGroup(user, g.Principal));
            if (!hit) continue;
            roles.Add(g.Role);
            if (string.Equals(g.Role, Roles.Persona, StringComparison.OrdinalIgnoreCase)) pages.UnionWith(PersonaPages.Parse(g.Pages));
        }
        return (roles, pages);
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
    /// The viewer's UPN: the account's SID in the directory table first (no lookup at
    /// sign-in, so it works when the web servers are in another domain from the users),
    /// then the configured map, then Active Directory by the SID, then the sign-in name
    /// itself when it already is one. Whichever way it was found, the SID is checked
    /// (<see cref="AccessRules.MayBe"/>): a person whose directory record carries a SID can
    /// be signed in as only by the account with that SID, and an answer from Active
    /// Directory or the sign-in name makes the account no one whose SID is not on record
    /// (2.4.12), so no mapping, name or lookup can land one account on another person.
    /// Cached for ten minutes per account.
    /// </summary>
    async Task<string?> ResolveUpnAsync(ClaimsPrincipal user, string windowsName)
    {
        var sid = OperatingSystem.IsWindows() && user.Identity is WindowsIdentity w ? w.User?.Value : null;
        var key = tenants.Key("upn:" + (sid ?? windowsName));
        if (cache.TryGetValue(key, out string? known)) return known;
        string? upn = null;
        var source = UpnSource.Sid;
        if (sid is not null)
        {
            try { upn = await db.ScalarAsync<string?>("SELECT TOP (1) Upn FROM dash.Person WHERE Sid = @sid ORDER BY AccountEnabled DESC", new { sid }); }
            catch (Exception ex) { log.LogInformation("No SID match for {Name}: {Message}", windowsName, ex.Message); }
        }
        if (upn is null && _o.IdentityMap.TryGetValue(windowsName, out var mapped)) (upn, source) = (mapped, UpnSource.IdentityMap);
        if (upn is null && FromDirectory(user, windowsName) is { } found) (upn, source) = (found, UpnSource.Directory);
        if (upn is not null && source != UpnSource.Sid && !await SidAllowsAsync(upn, sid, source))
        {
            if (source == UpnSource.Directory)
                log.LogWarning("{Name} (SID {Sid}) is {Upn} by Active Directory or its sign-in name, but the people directory records no SID for {Upn} that is this account's: not signed in as them. " +
                               "Load the person with their SID, or map the account in Insights:IdentityMap.", windowsName, sid ?? "none", upn, upn);
            else
                log.LogWarning("{Name} (SID {Sid}) is mapped to {Upn}, whose directory record carries a different SID: not signed in as them.", windowsName, sid ?? "none", upn);
            upn = null;
        }
        cache.Set(key, upn, TimeSpan.FromMinutes(10));
        return upn;
    }

    /// <summary>Whether the signed-in account's SID may be this person, found this way (see <see cref="AccessRules.MayBe"/>).</summary>
    async Task<bool> SidAllowsAsync(string upn, string? sid, UpnSource source)
    {
        string? recorded;
        try { recorded = await db.ScalarAsync<string?>("SELECT TOP (1) Sid FROM dash.Person WHERE Upn = @upn", new { upn }); }
        catch (Exception ex) { log.LogWarning("Could not check the SID for {Upn}: {Message}", upn, ex.Message); return false; }
        return AccessRules.MayBe(source, recorded, sid);
    }

    string? FromDirectory(ClaimsPrincipal user, string windowsName)
    {
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
    }
}
