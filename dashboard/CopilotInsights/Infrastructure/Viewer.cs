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
        [Admin] = "Runs the admin module: access, settings, reference data, health and the audit log; or only the Admin pages ticked for them. Sees no usage data by itself.",
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
    /// Whether the dashboard's limit (Admin › Access, MaxViewerLevel, 2.4.4) lets them in. An administrator
    /// who is not still reaches the Admin pages, so a wrong limit can always be put right.
    /// </summary>
    public bool Admitted { get; init; } = true;
    /// <summary>The limit in force when they signed in: 0 for none, else levels 1 to this.</summary>
    public int MaxLevel { get; init; }

    /// <summary>
    /// The Admin pages an Admin grant limited to some pages opens, by path. Empty for everyone else, a full
    /// administrator among them: their Admin role is in <see cref="Roles"/>, a limited one's is not.
    /// </summary>
    public IReadOnlySet<string> AdminPaths { get; init; } = new HashSet<string>();

    /// <summary>A full administrator: every Admin page, and the only one who may grant, limit or remove Admin.</summary>
    public bool IsAdmin => Roles.Contains(Infrastructure.Roles.Admin);
    /// <summary>Whether the viewer may open and change this Admin page: a full administrator, or one limited to pages that include it.</summary>
    public bool CanAdmin(string? path) => IsAdmin || AdminPaths.Contains(CopilotInsights.Data.AdminPages.Normalize(path));
    /// <summary>Any Admin page at all.</summary>
    public bool AnyAdmin => IsAdmin || AdminPaths.Count > 0;
    public bool OrgWide => AccessRules.SeesWholeOrganization(Level, Roles) || WidenedForSection;
    public bool Has(string role) => Roles.Contains(role);
    public string Name => DisplayName ?? Upn ?? WindowsName;

    /// <summary>How they signed in: Windows or Entra (<see cref="SignIn"/>).</summary>
    public string SignedInWith { get; init; } = SignIn.Windows;

    /// <summary>
    /// The persona roles they hold across the whole organization (a grant, or an access profile, with that box ticked):
    /// those roles' pages show the whole organization, where otherwise they show the viewer's own part.
    /// </summary>
    public IReadOnlySet<string> OrgWideRoles { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Set on the viewer of one page whose section's role they hold across the whole organization.</summary>
    public bool WidenedForSection { get; private set; }

    /// <summary>
    /// This viewer as the page reached through <paramref name="role"/>'s section sees them: across the whole organization
    /// when they hold that role so; otherwise as they are. An Agents grant across the organization widens the Agents pages
    /// only, never Leadership or another persona's.
    /// </summary>
    public Viewer ForSection(string? role)
    {
        if (role is null || OrgWide || !OrgWideRoles.Contains(role) || !Roles.Contains(role)) return this;
        var v = (Viewer)MemberwiseClone();
        v.WidenedForSection = true;
        return v;
    }
}

/// <summary>The security model's two decisions, kept pure so they can be tested on their own.</summary>
public static class AccessRules
{
    /// <summary>A persona role, whose section "across the whole organization" can widen: not Admin, not Organization.</summary>
    public static bool IsPersona(string role) =>
        Roles.All.Contains(role, StringComparer.OrdinalIgnoreCase)
        && !string.Equals(role, Roles.Admin, StringComparison.OrdinalIgnoreCase) && !string.Equals(role, Roles.Organization, StringComparison.OrdinalIgnoreCase);

    /// <summary>Level 1 or 2 below the configured top, or granted Organization. Admin alone is not enough.</summary>
    public static bool SeesWholeOrganization(int? level, IReadOnlySet<string> roles) =>
        roles.Contains(Roles.Organization) || level is 1 or 2;

    /// <summary>
    /// The dashboard's limit: with <paramref name="maxLevel"/> 0 everyone may use it, as before;
    /// otherwise only levels 1 to <paramref name="maxLevel"/> below the configured top, and anyone granted
    /// a role other than Admin (Admin › Access is the list of exceptions). Administrators alone are not
    /// admitted: they keep the Admin pages, which show no usage data. The level comes from the directory
    /// record of the SID-checked sign-in, never from the request.
    /// </summary>
    public static bool MayUseDashboard(int? level, IReadOnlySet<string> roles, int maxLevel) =>
        maxLevel <= 0
        || level is { } l && l >= 1 && l <= maxLevel
        || roles.Any(r => !string.Equals(r, Roles.Admin, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The SID comparison: the directory records no SID for the person, or records exactly the SID in the
    /// account's Windows token. Pure, for the tests. Which way the person was found decides whether no SID
    /// is enough (see <see cref="MayBe"/>).
    /// </summary>
    public static bool SidMatches(string? recordedSid, string? tokenSid) =>
        string.IsNullOrWhiteSpace(recordedSid) || (tokenSid is not null && string.Equals(recordedSid.Trim(), tokenSid, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The SID rule: whether the signed-in account may be the person a UPN names, by how the UPN was found.
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
    /// The same rule for a sign-in through Entra ID, whose UPN the tenant itself vouches for: the person a UPN (or the
    /// identity map) names may be this account unless the directory records another account for them, by its Entra
    /// object ID or, when the token carries the on-premises SID, by that SID. A UPN given to someone new keeps the
    /// person who held it before out of reach. Pure, for the tests.
    /// </summary>
    public static bool MayBeEntra(Guid? recordedObjectId, string? recordedSid, Guid? tokenObjectId, string? tokenSid) =>
        (recordedObjectId is null || recordedObjectId == tokenObjectId)
        && (string.IsNullOrWhiteSpace(recordedSid) || string.IsNullOrWhiteSpace(tokenSid) || SidMatches(recordedSid, tokenSid));

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

/// <summary>What an Entra ID sign-in's token says, by the claim names Entra ID and ASP.NET Core give them. Pure, for the tests.</summary>
public static class EntraClaims
{
    static string? First(ClaimsPrincipal user, params string[] types) =>
        types.Select(t => user.FindFirst(t)?.Value).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim();

    public static Guid? ObjectId(ClaimsPrincipal user) =>
        Guid.TryParse(First(user, "oid", "http://schemas.microsoft.com/identity/claims/objectidentifier"), out var g) ? g : null;

    /// <summary>The on-premises SID, an optional claim a hybrid tenant's app registration can add.</summary>
    public static string? OnPremisesSid(ClaimsPrincipal user) => First(user, "onprem_sid");

    /// <summary>The UPN the tenant gives the account: upn, else preferred_username where it is an address.</summary>
    public static string? Upn(ClaimsPrincipal user) =>
        First(user, "upn", ClaimTypes.Upn) ?? (First(user, "preferred_username") is { } p && p.Contains('@') ? p : null);

    /// <summary>The name the audit log and the header show for the account.</summary>
    public static string SignInName(ClaimsPrincipal user) =>
        Upn(user) ?? First(user, "preferred_username", "name", ClaimTypes.Name) ?? "(anonymous)";

    public static bool HasAppRole(ClaimsPrincipal user, string role) =>
        user.FindAll("roles").Concat(user.FindAll(ClaimTypes.Role)).Any(c => string.Equals(c.Value, role, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The group IDs in the token, or null when it has none to give: no groups claim, or more groups than fit (the
    /// overage, where Entra ID names a Graph call instead). Then Graph is asked.
    /// </summary>
    public static IReadOnlySet<Guid>? Groups(ClaimsPrincipal user)
    {
        if (user.FindFirst("hasgroups") is not null || user.FindFirst("_claim_names")?.Value.Contains("groups") == true) return null;
        var g = user.FindAll("groups").Select(c => Guid.TryParse(c.Value, out var id) ? id : Guid.Empty).Where(id => id != Guid.Empty).ToHashSet();
        return g.Count == 0 ? null : g;
    }

    /// <summary>Whether this request was signed in through Entra ID (a cookie from it, or a token for the data API).</summary>
    public static bool IsEntra(ClaimsPrincipal user) => ObjectId(user) is not null && user.Identity is not WindowsIdentity;
}

/// <summary>Resolves the signed-in account to a Viewer, once per request.</summary>
/// <remarks>
/// The viewer is always the account Windows, or Entra ID, authenticated: nothing in the URL, a cookie or a header can
/// change who they are. The UPN they are matched to is checked against the SID in their Windows token wherever the
/// directory records one (see <see cref="ResolveUpnAsync"/>), and against the object ID (and the on-premises SID) in
/// their Entra ID token wherever it records those (see <see cref="ResolveEntraUpnAsync"/>).
/// </remarks>
public sealed class ViewerService(
    IHttpContextAccessor http, Db db, IMemoryCache cache, IOptions<InsightsOptions> options,
    TenantAccessor tenants, Settings settings, EntraGraph graph, ILogger<ViewerService> log)
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
        var entra = EntraClaims.IsEntra(user);
        var signInName = entra ? EntraClaims.SignInName(user) : user.Identity?.Name ?? "(anonymous)";
        var upn = entra ? await ResolveEntraUpnAsync(user, signInName) : await ResolveUpnAsync(user, signInName);

        var (roles, paths, orgWide) = await RolesForAsync(upn, signInName, user, entra);
        if (IsBootstrapAdmin(upn, signInName, user) || entra && EntraClaims.HasAppRole(user, _o.Entra.BootstrapAdminRole)) roles.Add(Roles.Admin);
        return await BuildAsync(signInName, upn, roles, paths, orgWide, entra ? SignIn.Entra : SignIn.Windows);
    }

    async Task<Viewer> BuildAsync(string signInName, string? upn, HashSet<string> roles, HashSet<string> paths, HashSet<string> orgWide, string signedInWith)
    {
        PersonRow? p = null;
        if (upn is not null)
            p = (await db.QueryAsync<PersonRow>(
                "SELECT DisplayName, Level, PeopleBelow FROM dash.Person WHERE Upn = @upn", new { upn })).FirstOrDefault();
        var max = Math.Clamp(await settings.IntAsync("MaxViewerLevel", 0), 0, 15);
        return new Viewer
        {
            WindowsName = signInName, Upn = upn, DisplayName = p?.DisplayName, Level = p?.Level,
            InDirectory = p is not null, PeopleBelow = p?.PeopleBelow ?? 0, Roles = roles,
            MaxLevel = max, Admitted = AccessRules.MayUseDashboard(p?.Level, roles, max),
            AdminPaths = roles.Contains(Roles.Admin) ? new HashSet<string>() : paths,
            OrgWideRoles = orgWide, SignedInWith = signedInWith,
        };
    }

    sealed record PersonRow(string? DisplayName, byte? Level, int PeopleBelow)
    {
        public int? LevelInt => Level;
    }

    /// <summary>One grant as it reaches a viewer: a role given directly, or one of an access profile's.</summary>
    public sealed record GrantRow(string Principal, string PrincipalType, string Role, string? Pages, bool OrgWide, int? ProfileId);

    /// <summary>
    /// Every grant, the role-by-role ones and each access profile's roles, with the columns a database from before them
    /// lacks given as they were: no pages (the whole Admin role), not across the organization, no profile.
    /// </summary>
    public static Task<List<GrantRow>> GrantsAsync(Db db) => db.QueryAsync<GrantRow>(@"
DECLARE @pages NVARCHAR(60) = CASE WHEN COL_LENGTH(N'dash.RoleGrant', N'Pages') IS NULL THEN N'CAST(NULL AS NVARCHAR(1000))' ELSE N'Pages' END;
DECLARE @org NVARCHAR(60) = CASE WHEN COL_LENGTH(N'dash.RoleGrant', N'OrgWide') IS NULL THEN N'CAST(0 AS BIT)' ELSE N'OrgWide' END;
DECLARE @sql NVARCHAR(MAX) = N'SELECT Principal, PrincipalType, Role, ' + @pages + N' AS Pages, ' + @org + N' AS OrgWide, CAST(NULL AS INT) AS ProfileId FROM dash.RoleGrant';
IF OBJECT_ID(N'dash.ProfileGrant', N'U') IS NOT NULL
    SET @sql += N' UNION ALL SELECT g.Principal, g.PrincipalType, r.Role, r.Pages, r.OrgWide, g.ProfileId
                   FROM dash.ProfileGrant AS g JOIN dash.AccessProfileRole AS r ON r.ProfileId = g.ProfileId';
EXEC sys.sp_executesql @sql;");

    /// <summary>
    /// The roles granted to the account, its UPN or its groups, directly or through an access profile; the Admin pages
    /// of every Admin grant among them that is limited to some (such a grant adds its pages and not the Admin role; a
    /// grant with no pages named is the whole role, whatever the others say); and the persona roles held across the
    /// whole organization.
    /// </summary>
    async Task<(HashSet<string> Roles, HashSet<string> Paths, HashSet<string> OrgWide)> RolesForAsync(string? upn, string signInName, ClaimsPrincipal user, bool entra)
    {
        var grants = await GrantsAsync(db);
        var held = await GroupsHeldAsync(grants, upn, user, entra);
        var roles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var orgWide = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in grants)
        {
            /* Whatever the grant says it is: a DOMAIN\name recorded as a user may be a group, and the other way round. */
            var hit = Same(g.Principal, upn) || Same(g.Principal, signInName) || held.Contains(g.Principal)
                      || (!entra && IsInGroup(user, g.Principal));
            if (!hit) continue;
            if (string.Equals(g.Role, Roles.Admin, StringComparison.OrdinalIgnoreCase) && CopilotInsights.Data.AdminPages.Limits(g.Pages))
                paths.UnionWith(CopilotInsights.Data.AdminPages.Parse(g.Pages));
            else roles.Add(g.Role);
            if (g.OrgWide && AccessRules.IsPersona(g.Role)) orgWide.Add(g.Role);
        }
        return (roles, paths, orgWide);
    }

    /// <summary>
    /// The groups named in grants that the person is in, by the grant's name for them. Entra groups (entra:&lt;ID&gt;)
    /// from the token's groups claim, else from Microsoft Graph; AD groups (DOMAIN\Group) from Windows at sign-in, or,
    /// signed in through Entra ID, from the members the nightly directory load records (audit.GroupMember).
    /// </summary>
    async Task<HashSet<string>> GroupsHeldAsync(List<GrantRow> grants, string? upn, ClaimsPrincipal user, bool entra)
    {
        var held = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var entraGroups = grants.Select(g => EntraPrincipal.TryParse(g.Principal, out var id) ? id : Guid.Empty).Where(id => id != Guid.Empty).Distinct().ToList();
        if (entraGroups.Count > 0)
        {
            var person = entra ? EntraClaims.ObjectId(user) : upn is null ? null : await PersonObjectIdAsync(upn);
            IReadOnlySet<Guid> inGroups = entra && EntraClaims.Groups(user) is { } fromToken ? fromToken
                : person is { } pid ? await graph.MemberOfAsync(pid, entraGroups) : new HashSet<Guid>();
            foreach (var id in entraGroups.Where(inGroups.Contains)) held.Add(EntraPrincipal.Of(id));
        }
        if (entra && upn is not null && grants.Any(g => g.Principal.Contains('\\')))
        {
            try
            {
                held.UnionWith(await db.QueryAsync<string>(
                    "IF OBJECT_ID(N'audit.GroupMember', N'U') IS NOT NULL SELECT GroupName FROM audit.GroupMember WHERE UserPrincipalName = @upn;", new { upn }));
            }
            catch (Microsoft.Data.SqlClient.SqlException ex) { log.LogWarning("Could not read the AD groups recorded for {Upn}: {Message}", upn, ex.Message); }
        }
        return held;
    }

    async Task<Guid?> PersonObjectIdAsync(string upn)
    {
        try
        {
            return await db.ScalarAsync<Guid?>(
                "IF COL_LENGTH(N'dash.Person', N'ObjectId') IS NOT NULL EXEC (N'SELECT TOP (1) ObjectId FROM dash.Person WHERE Upn = @upn') ELSE SELECT CAST(NULL AS UNIQUEIDENTIFIER);",
                new { upn });
        }
        catch (Microsoft.Data.SqlClient.SqlException) { return null; }
    }

    bool IsBootstrapAdmin(string? upn, string signInName, ClaimsPrincipal user) =>
        _o.BootstrapAdmins.Any(b => Same(b, upn) || Same(b, signInName) || (user.Identity is WindowsIdentity && IsInGroup(user, b)));

    static bool Same(string a, string? b) => b is not null && string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    bool IsInGroup(ClaimsPrincipal user, string group)
    {
        if (!group.Contains('\\')) return false;   // DOMAIN\Group only
        try { return user.IsInRole(group); }
        catch (Exception ex) { log.LogDebug(ex, "Group check failed for {Group}", group); return false; }
    }

    /// <summary>
    /// A sign-in through Entra ID's UPN: the person whose Entra object ID it is first, then the person whose on-premises SID
    /// the token carries (a hybrid tenant's optional onprem_sid claim), then the UPN the tenant gives it, or the identity
    /// map, unless the directory records another account for that person (<see cref="AccessRules.MayBeEntra"/>). Cached
    /// for ten minutes per account.
    /// </summary>
    async Task<string?> ResolveEntraUpnAsync(ClaimsPrincipal user, string signInName)
    {
        var oid = EntraClaims.ObjectId(user);
        var sid = EntraClaims.OnPremisesSid(user);
        var key = tenants.Key("upn-entra:" + (oid?.ToString() ?? signInName));
        if (cache.TryGetValue(key, out string? known)) return known;
        string? upn = null;
        try
        {
            if (oid is not null)
                upn = await db.ScalarAsync<string?>(
                    "IF COL_LENGTH(N'dash.Person', N'ObjectId') IS NOT NULL EXEC (N'SELECT TOP (1) Upn FROM dash.Person WHERE ObjectId = @oid ORDER BY AccountEnabled DESC', N'@oid UNIQUEIDENTIFIER', @oid);",
                    new { oid });
            if (upn is null && sid is not null)
                upn = await db.ScalarAsync<string?>("SELECT TOP (1) Upn FROM dash.Person WHERE Sid = @sid ORDER BY AccountEnabled DESC", new { sid });
        }
        catch (Exception ex) { log.LogInformation("No object ID or SID match for {Name}: {Message}", signInName, ex.Message); }
        if (upn is null)
        {
            var claimed = _o.IdentityMap.TryGetValue(signInName, out var mapped) ? mapped : EntraClaims.Upn(user);
            if (claimed is not null)
            {
                (Guid? ObjectId, string? Sid) recorded = (null, null);
                try
                {
                    recorded = (await db.QueryAsync<(Guid? ObjectId, string? Sid)>(
                        "IF COL_LENGTH(N'dash.Person', N'ObjectId') IS NOT NULL EXEC (N'SELECT TOP (1) ObjectId, Sid FROM dash.Person WHERE Upn = @upn', N'@upn NVARCHAR(256)', @upn) " +
                        "ELSE SELECT TOP (1) CAST(NULL AS UNIQUEIDENTIFIER) AS ObjectId, Sid FROM dash.Person WHERE Upn = @upn;", new { upn = claimed })).FirstOrDefault();
                }
                catch (Exception ex) { log.LogWarning("Could not check the directory record of {Upn}: {Message}", claimed, ex.Message); claimed = null; }
                if (claimed is not null && AccessRules.MayBeEntra(recorded.ObjectId, recorded.Sid, oid, sid)) upn = claimed;
                else if (claimed is not null)
                    log.LogWarning("{Name} (object ID {Oid}) gives the UPN {Upn}, whose directory record names another account: not signed in as them.", signInName, oid, claimed);
            }
        }
        cache.Set(key, upn, TimeSpan.FromMinutes(10));
        return upn;
    }

    /// <summary>
    /// The viewer's UPN: the account's SID in the directory table first (no lookup at
    /// sign-in, so it works when the web servers are in another domain from the users),
    /// then the configured map, then Active Directory by the SID, then the sign-in name
    /// itself when it already is one. Whichever way it was found, the SID is checked
    /// (<see cref="AccessRules.MayBe"/>): a person whose directory record carries a SID can
    /// be signed in as only by the account with that SID, and an answer from Active
    /// Directory or the sign-in name makes the account no one whose SID is not on record
    /// so no mapping, name or lookup can land one account on another person.
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
