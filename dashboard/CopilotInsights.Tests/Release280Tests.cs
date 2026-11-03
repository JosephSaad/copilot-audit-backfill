using System.Net;
using System.Security.Claims;
using CopilotInsights.Data;
using CopilotInsights.Infrastructure;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CopilotInsights.Tests;

/// <summary>2.8.0: Entra sign-in and groups, a role across the whole organization, and access profiles.</summary>
public class Release280Tests
{
    static string Root()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(d.FullName, "CHANGELOG.md"))) d = d.Parent!;
        return d.FullName;
    }

    static string Read(params string[] p) => File.ReadAllText(Path.Combine([Root(), .. p]));

    static Viewer V(string[] roles, string[]? wide = null, int? level = 5) => new()
    {
        WindowsName = "a@contoso.com", Upn = "a@contoso.com", Level = level, InDirectory = true,
        Roles = roles.ToHashSet(StringComparer.OrdinalIgnoreCase),
        OrgWideRoles = (wide ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase),
    };

    static ClaimsPrincipal Entra(params (string Type, string Value)[] claims) =>
        new(new ClaimsIdentity(claims.Select(c => new Claim(c.Type, c.Value)), "AuthenticationTypes.Federation"));

    /* ---- A role across the whole organization ---- */

    [Fact]
    public void A_role_across_the_organization_widens_its_own_section_only()
    {
        var v = V([Roles.Agents, Roles.Licensing], wide: [Roles.Agents]);
        Assert.False(v.OrgWide);
        Assert.True(v.ForSection(Roles.Agents).OrgWide);
        Assert.False(v.ForSection(Roles.Licensing).OrgWide);
        Assert.False(v.ForSection(null).OrgWide);   // Leadership, Regions and the other open sections
        Assert.False(v.OrgWide);                     // the viewer itself is never changed
    }

    [Fact]
    public void A_flag_without_the_role_or_on_a_non_persona_role_widens_nothing()
    {
        Assert.False(V([Roles.Licensing], wide: [Roles.Agents]).ForSection(Roles.Agents).OrgWide);
        Assert.True(AccessRules.IsPersona(Roles.Enablement));
        Assert.False(AccessRules.IsPersona(Roles.Admin));
        Assert.False(AccessRules.IsPersona(Roles.Organization));
        Assert.False(AccessRules.IsPersona("Nobody"));
    }

    [Fact]
    public void Whole_organization_viewers_are_left_as_they_are()
    {
        var top = V([Roles.Agents], wide: [Roles.Agents], level: 2);
        Assert.Same(top, top.ForSection(Roles.Agents));
    }

    [Fact]
    public void Pages_take_the_section_role_before_their_context_is_made()
    {
        var page = Read("dashboard", "CopilotInsights", "Pages", "InsightsPage.cs");
        var prepare = page[page.IndexOf("protected async Task<bool> PrepareAsync()", StringComparison.Ordinal)..];
        Assert.True(prepare.IndexOf(".ForSection(RequiredRole ?? Nav.SectionOf(Request.Path)?.Role)", StringComparison.Ordinal)
                    < prepare.IndexOf("S.Contexts.CreateAsync(Viewer, Request, forPage: true)", StringComparison.Ordinal));
    }

    /* ---- Entra sign-in: who the person is ---- */

    [Fact]
    public void An_entra_upn_is_refused_when_the_directory_names_another_account()
    {
        var oid = Guid.NewGuid();
        Assert.True(AccessRules.MayBeEntra(null, null, oid, null));                    // nothing recorded: the tenant's word
        Assert.True(AccessRules.MayBeEntra(oid, null, oid, null));
        Assert.False(AccessRules.MayBeEntra(Guid.NewGuid(), null, oid, null));         // the UPN moved to someone new
        Assert.True(AccessRules.MayBeEntra(null, "S-1-5-21-1", oid, null));            // no SID in the token: not held against them
        Assert.True(AccessRules.MayBeEntra(null, "S-1-5-21-1", oid, "s-1-5-21-1"));
        Assert.False(AccessRules.MayBeEntra(null, "S-1-5-21-1", oid, "S-1-5-21-2"));
    }

    [Fact]
    public void The_token_is_read_by_entra_ids_claim_names()
    {
        var oid = Guid.NewGuid();
        var u = Entra(("http://schemas.microsoft.com/identity/claims/objectidentifier", oid.ToString()), ("preferred_username", "ada@contoso.com"),
                      ("onprem_sid", "S-1-5-21-9"), ("roles", "Insights.BootstrapAdmin"));
        Assert.Equal(oid, EntraClaims.ObjectId(u));
        Assert.Equal("ada@contoso.com", EntraClaims.Upn(u));
        Assert.Equal("S-1-5-21-9", EntraClaims.OnPremisesSid(u));
        Assert.True(EntraClaims.HasAppRole(u, "insights.bootstrapadmin"));
        Assert.True(EntraClaims.IsEntra(u));
        /* The upn claim first; a preferred_username that is not an address is not a UPN. */
        Assert.Equal("ada.l@contoso.com", EntraClaims.Upn(Entra(("upn", "ada.l@contoso.com"), ("preferred_username", "ada@contoso.com"))));
        Assert.Null(EntraClaims.Upn(Entra(("preferred_username", "ada"))));
        Assert.False(EntraClaims.IsEntra(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "CONTOSO\\ada")], "Negotiate"))));
    }

    [Fact]
    public void Groups_in_the_token_are_used_unless_they_overflowed()
    {
        var g = Guid.NewGuid();
        Assert.Equal([g], EntraClaims.Groups(Entra(("oid", Guid.NewGuid().ToString()), ("groups", g.ToString()))));
        Assert.Null(EntraClaims.Groups(Entra(("oid", Guid.NewGuid().ToString()), ("hasgroups", "true"))));
        Assert.Null(EntraClaims.Groups(Entra(("oid", Guid.NewGuid().ToString()), ("_claim_names", "{\"groups\":\"src1\"}"))));
        Assert.Null(EntraClaims.Groups(Entra(("oid", Guid.NewGuid().ToString()))));
    }

    [Fact]
    public void An_entra_group_is_named_by_its_object_id()
    {
        var id = Guid.NewGuid();
        Assert.Equal("entra:" + id.ToString("D"), EntraPrincipal.Of(id));
        Assert.True(EntraPrincipal.TryParse("ENTRA:" + id, out var back) && back == id);
        Assert.False(EntraPrincipal.TryParse("CONTOSO\\Execs", out _));
        Assert.False(EntraPrincipal.TryParse("entra:not-a-guid", out _));
    }

    /* ---- Entra groups through Microsoft Graph ---- */

    static EntraGraph Graph(Func<HttpRequestMessage, HttpResponseMessage> answer, List<string>? asked = null) =>
        new(Options.Create(new InsightsOptions { Entra = new EntraOptions { TenantId = "t", ClientId = "c", CertificateThumbprint = "00" } }),
            new NoClients(), new MemoryCache(new MemoryCacheOptions()), NullLogger<EntraGraph>.Instance)
        {
            Fake = r => { asked?.Add(r.Content?.ReadAsStringAsync().Result ?? r.RequestUri!.ToString()); return answer(r); },
        };

    sealed class NoClients : IHttpClientFactory { public HttpClient CreateClient(string name) => throw new InvalidOperationException("no network in the tests"); }

    [Fact]
    public void Graph_is_asked_twenty_groups_at_a_time_and_the_answer_is_kept()
    {
        var groups = Enumerable.Range(0, 45).Select(_ => Guid.NewGuid()).ToList();
        var asked = new List<string>();
        var g = Graph(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($"{{\"value\":[\"{groups[3]}\",\"{groups[40]}\"]}}") }, asked);
        var person = Guid.NewGuid();
        var found = g.MemberOfAsync(person, groups).Result;
        Assert.Equal(3, asked.Count);
        Assert.Equal(new HashSet<Guid> { groups[3], groups[40] }, found);
        g.MemberOfAsync(person, groups).Wait();
        Assert.Equal(3, asked.Count);   // from memory for ten minutes
    }

    [Fact]
    public void Graph_failing_grants_nothing()
    {
        var g = Graph(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        Assert.Empty(g.MemberOfAsync(Guid.NewGuid(), [Guid.NewGuid()]).Result);
        Assert.Empty(g.FindGroupsAsync("Fin").Result);
        Assert.Null(g.GroupNameAsync(Guid.NewGuid()).Result);
    }

    [Fact]
    public void Graph_without_an_app_registration_is_not_asked()
    {
        var g = new EntraGraph(Options.Create(new InsightsOptions()), new NoClients(), new MemoryCache(new MemoryCacheOptions()), NullLogger<EntraGraph>.Instance);
        Assert.False(g.Available);
        Assert.Empty(g.MemberOfAsync(Guid.NewGuid(), [Guid.NewGuid()]).Result);
        Assert.True(new EntraOptions { TenantId = "t", ClientCredential = "ManagedIdentity" }.CanCallGraph);
        Assert.False(new EntraOptions { TenantId = "t", ClientId = "c" }.CanCallGraph);
    }

    [Fact]
    public void The_group_search_names_groups_for_the_grant_box()
    {
        var id = Guid.NewGuid();
        var g = Graph(r =>
        {
            Assert.Contains("displayName%2C%27O%27%27Brien%27", r.RequestUri!.AbsoluteUri);   // the quote doubled, as OData wants
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($"{{\"value\":[{{\"id\":\"{id}\",\"displayName\":\"O'Brien team\"}}]}}") };
        });
        Assert.Equal([(id, "O'Brien team")], g.FindGroupsAsync("O'Brien").Result);
    }

    /* ---- Access profiles ---- */

    [Fact]
    public void A_bundle_is_the_same_whatever_order_or_case_it_was_given_in()
    {
        var a = AccessProfiles.Key([new("licensing", true), new(Roles.Agents)]);
        var b = AccessProfiles.Key([new(Roles.Agents), new(Roles.Licensing, true)]);
        Assert.Equal(a, b);
        Assert.Equal("Licensing*|Agents", a);
        Assert.Equal(a, AccessProfiles.Key(AccessProfiles.FromKey(a)));
        /* The whole organization only on a persona role; pages only on Admin. */
        Assert.Equal("Organization", AccessProfiles.Key([new(Roles.Organization, true, "/admin/health")]));
        Assert.Equal("Admin(/admin/health,/admin/audit)", AccessProfiles.Key([new(Roles.Admin, Pages: "/admin/audit,/admin/health")]));
        Assert.Equal("Admin", AccessProfiles.Key([new(Roles.Admin, Pages: "/admin/audit"), new(Roles.Admin)]));   // the whole role wins
    }

    [Fact]
    public void A_bundle_reads_in_words()
    {
        Assert.Equal("Admin (Targets and assumptions, Reference data); Licensing across the organization",
            AccessProfiles.Describe([new(Roles.Admin, Pages: "/admin/reference,/admin/assumptions"), new(Roles.Licensing, true)]));
        Assert.Equal("Agents, whole organization", AccessProfiles.SuggestName([new(Roles.Agents, true)]));
        Assert.Equal("Security and Compliance", AccessProfiles.SuggestName([new(Roles.Compliance), new(Roles.Security)]));
    }

    [Fact]
    public void Profiles_are_offered_from_the_bundles_held_today_then_the_starters()
    {
        (string, ProfileRole)[] grants =
        [
            ("a@contoso.com", new(Roles.Agents, true)), ("CONTOSO\\agents", new(Roles.Agents, true)),
            ("b@contoso.com", new(Roles.Security)), ("b@contoso.com", new(Roles.Compliance)),
            ("boss@contoso.com", new(Roles.Admin)),                        // a full administrator alone: not a profile
            ("c@contoso.com", new(Roles.Licensing)),
        ];
        var offered = AccessProfiles.Proposals(grants, [[new(Roles.Licensing)]], ["Dashboard operator"]);
        Assert.Equal(["Agents, whole organization", "Security and Compliance"], offered.Where(p => !p.Starter).Select(p => p.Name));
        Assert.Equal(["a@contoso.com", "CONTOSO\\agents"], offered[0].Holders);
        /* A starter that gives what is offered already, or whose name is taken, is left out. */
        Assert.Equal(["License manager"], offered.Where(p => p.Starter).Select(p => p.Name));
        Assert.DoesNotContain(offered, p => AccessProfiles.Key(p.Roles) == "Licensing");
    }

    [Fact]
    public void A_request_link_is_https_only()
    {
        Assert.Null(AccessProfiles.RequestUrl("  "));
        Assert.Equal("https://myaccess.microsoft.com/@contoso.com#/access-packages/1", AccessProfiles.RequestUrl("https://myaccess.microsoft.com/@contoso.com#/access-packages/1"));
        Assert.Throws<ArgumentException>(() => AccessProfiles.RequestUrl("javascript:alert(1)"));
        Assert.Throws<ArgumentException>(() => AccessProfiles.RequestUrl("http://myaccess.microsoft.com/"));
    }

    [Fact]
    public void A_profile_that_gives_admin_is_for_full_administrators_only()
    {
        var page = Read("dashboard", "CopilotInsights", "Pages", "Admin", "Profiles.cs");
        Assert.Contains("only a full administrator can make a profile that gives Admin.", page);
        Assert.Contains("only a full administrator can change a profile that gives Admin.", page);
        Assert.Contains("only a full administrator can grant a profile that gives Admin.", page);
        Assert.Contains("only a full administrator can remove a profile that gives Admin.", page);
        Assert.Contains("someone holds this profile: remove it from them first.", page);
    }

    [Fact]
    public void Sign_in_expands_profile_grants_and_reads_each_grants_flag()
    {
        var viewer = Read("dashboard", "CopilotInsights", "Infrastructure", "Viewer.cs");
        Assert.Contains("FROM dash.ProfileGrant AS g JOIN dash.AccessProfileRole AS r ON r.ProfileId = g.ProfileId", viewer);
        Assert.Contains("CASE WHEN COL_LENGTH(N'dash.RoleGrant', N'OrgWide') IS NULL THEN N'CAST(0 AS BIT)'", viewer);
        Assert.Contains("if (g.OrgWide && AccessRules.IsPersona(g.Role)) orgWide.Add(g.Role);", viewer);
        /* AD groups under Entra sign-in come from the nightly list, never from the request. */
        Assert.Contains("SELECT GroupName FROM audit.GroupMember WHERE UserPrincipalName = @upn;", viewer);
    }

    [Fact]
    public void Every_change_to_profiles_is_in_the_audit_snapshot()
    {
        var audit = Read("dashboard", "CopilotInsights", "Infrastructure", "AuditChange.cs");
        Assert.Contains("N' profile ', p.Name", audit);
        Assert.Contains("N' gives ', r.Role", audit);
        Assert.Contains("\" across the organization\"", audit);
    }

    /* ---- The site's settings ---- */

    [Fact]
    public void Windows_stays_the_default_and_entra_needs_its_app_registration()
    {
        Assert.Equal(SignIn.Windows, new InsightsOptions().Authentication);
        var program = Read("dashboard", "CopilotInsights", "Program.cs");
        // 3.0.0 passes whether the Governance Workbench is set up, for its operator token.
        Assert.Contains("if (entraSignIn) EntraSignIn.Configure(builder.Services, insights, ", program);
        Assert.Contains("else builder.Services.AddAuthentication(NegotiateDefaults.AuthenticationScheme).AddNegotiate();", program);
        var e = Assert.Throws<InvalidOperationException>(() => EntraSignIn.Configure(new Microsoft.Extensions.DependencyInjection.ServiceCollection(), new InsightsOptions { Authentication = SignIn.Entra }));
        Assert.Contains("Insights:Entra:TenantId and Insights:Entra:ClientId", e.Message);
    }

    [Fact]
    public void The_apps_credential_is_what_the_setting_says()
    {
        Assert.Equal(Microsoft.Identity.Abstractions.CredentialSource.SignedAssertionFromManagedIdentity,
            EntraSignIn.Credential(new EntraOptions { ClientCredential = "ManagedIdentity" }).SourceType);
        var cert = EntraSignIn.Credential(new EntraOptions { CertificateThumbprint = "3F 2A" });
        Assert.Equal(Microsoft.Identity.Abstractions.CredentialSource.StoreWithThumbprint, cert.SourceType);
        Assert.Equal("3F2A", cert.CertificateThumbprint);
        Assert.Throws<InvalidOperationException>(() => EntraSignIn.Credential(new EntraOptions { ClientCredential = "Secret" }));
    }

    [Fact]
    public void Form_keys_on_azure_need_both_addresses()
    {
        var e = Assert.Throws<InvalidOperationException>(() => KeyRing.Configure(new Microsoft.Extensions.DependencyInjection.ServiceCollection(),
            new InsightsOptions { DataProtection = "Blob", DataProtectionBlobUri = "https://a.blob.core.windows.net/k/keys.xml" }));
        Assert.Contains("DataProtectionKeyVaultKeyId", e.Message);
        Assert.Null(KeyRing.Configure(new Microsoft.Extensions.DependencyInjection.ServiceCollection(), new InsightsOptions()));
    }

    /* ---- The schema ---- */

    [Fact]
    public void Every_schema_change_is_additive()
    {
        var dash = Read("sql", "Create-CopilotDashboardSchema.sql");
        Assert.Contains("IF COL_LENGTH(N'dash.RoleGrant', N'OrgWide') IS NULL", dash);
        Assert.Contains("ADD OrgWide BIT NOT NULL CONSTRAINT DF_DashRoleGrant_OrgWide DEFAULT (0);", dash);
        Assert.Contains("IF COL_LENGTH(N'dash.Person', N'ObjectId') IS NULL", dash);
        Assert.Contains("IF OBJECT_ID(N'dash.AccessProfile', N'U') IS NULL", dash);
        Assert.Contains("IF OBJECT_ID(N'dash.ProfileGrant', N'U') IS NULL", dash);
        Assert.Contains("IF COL_LENGTH(N'audit.CopilotLicense', N'EntraObjectId') IS NULL", Read("sql", "Create-CopilotEnrichmentSchema.sql"));
        var ops = Read("sql", "Create-CopilotOperationsSchema.sql");
        Assert.Contains("GRANT INSERT, UPDATE, DELETE ON dash.ProfileGrant TO CopilotInsightsApp;", ops);
        Assert.Contains("JOIN dash.AccessProfileRole AS pr ON pr.ProfileId = pg.ProfileId AND pr.Role = ''Organization''", ops);
    }

    [Fact]
    public void The_directory_load_leaves_entra_groups_to_the_dashboard()
    {
        var export = Read("tools", "Export-UsersWithHierarchy-ToSql.ps1");
        Assert.Contains("AND Principal NOT LIKE N''entra:%''", export);
        Assert.Contains("UNION SELECT Principal FROM dash.ProfileGrant", export);
    }
}
