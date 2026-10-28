using System.Net.Http.Headers;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace CopilotInsights.Infrastructure;

/// <summary>The identity the dashboard uses with Entra ID, Microsoft Graph and Azure: from <see cref="EntraOptions"/>.</summary>
public static class EntraCredentials
{
    public static TokenCredential For(EntraOptions e)
    {
        var host = new Uri(string.IsNullOrWhiteSpace(e.Instance) ? "https://login.microsoftonline.com/" : e.Instance);
        switch (e.ClientCredential.Trim().ToLowerInvariant())
        {
            case "managedidentity":
                return string.IsNullOrWhiteSpace(e.ManagedIdentityClientId)
                    ? new ManagedIdentityCredential(ManagedIdentityId.SystemAssigned)
                    : new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(e.ManagedIdentityClientId.Trim()));
            case "secret":
                return new ClientSecretCredential(Need(e.TenantId, "TenantId"), Need(e.ClientId, "ClientId"), Need(e.ClientSecret, "ClientSecret"),
                    new ClientSecretCredentialOptions { AuthorityHost = host });
            default:
                return new ClientCertificateCredential(Need(e.TenantId, "TenantId"), Need(e.ClientId, "ClientId"), Certificate(Need(e.CertificateThumbprint, "CertificateThumbprint")),
                    new ClientCertificateCredentialOptions { AuthorityHost = host });
        }
    }

    /// <summary>A certificate with its private key from Local Computer › Personal, else the site account's own store.</summary>
    public static X509Certificate2 Certificate(string thumbprint)
    {
        var t = thumbprint.Replace(" ", "").Trim();
        foreach (var location in new[] { StoreLocation.LocalMachine, StoreLocation.CurrentUser })
        {
            using var store = new X509Store(StoreName.My, location);
            store.Open(OpenFlags.ReadOnly);
            var found = store.Certificates.Find(X509FindType.FindByThumbprint, t, validOnly: false);
            if (found.Count > 0 && found[0].HasPrivateKey) return found[0];
        }
        throw new InvalidOperationException($"Insights:Entra:CertificateThumbprint names {t}, but no certificate with that thumbprint and its private key is in Local Computer › Personal " +
            "(or the account the site runs as cannot read its private key).");
    }

    static string Need(string? value, string name) =>
        string.IsNullOrWhiteSpace(value) ? throw new InvalidOperationException($"Insights:Entra:{name} is not set.") : value.Trim();
}

/// <summary>
/// What the dashboard asks Microsoft Graph about Entra groups, as the app registration (GroupMember.Read.All): which of
/// the groups named in grants a person is in, nested groups included, and the groups an administrator may pick. Asked on
/// the server, not read from the sign-in token, so a person in more than 200 groups is answered all the same. A
/// person's answer is kept for ten minutes, as a Windows sign-in's groups are.
/// </summary>
public sealed class EntraGraph(IOptions<InsightsOptions> options, IHttpClientFactory http, IMemoryCache cache, ILogger<EntraGraph> log)
{
    readonly EntraOptions _e = options.Value.Entra;
    TokenCredential? _credential;

    /// <summary>Whether the app registration is set up to call Graph.</summary>
    public bool Available => _e.CanCallGraph;

    /// <summary>For the tests: answers in place of Graph's.</summary>
    internal Func<HttpRequestMessage, HttpResponseMessage>? Fake { get; set; }

    async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (Fake is not null) return Fake(request);
        _credential ??= EntraCredentials.For(_e);
        var token = await _credential.GetTokenAsync(new TokenRequestContext([GraphUrl + "/.default"]), ct);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        return await http.CreateClient(nameof(EntraGraph)).SendAsync(request, ct);
    }

    string GraphUrl => (string.IsNullOrWhiteSpace(_e.GraphUrl) ? "https://graph.microsoft.com" : _e.GraphUrl).TrimEnd('/');

    /// <summary>The groups among <paramref name="groups"/> the person is in, nested groups included. Empty when Graph cannot be asked.</summary>
    public async Task<IReadOnlySet<Guid>> MemberOfAsync(Guid person, IReadOnlyCollection<Guid> groups, CancellationToken ct = default)
    {
        if (groups.Count == 0 || !Available && Fake is null) return new HashSet<Guid>();
        var key = $"entra-groups:{person}:{string.Join(",", groups.Order())}";
        if (cache.TryGetValue(key, out IReadOnlySet<Guid>? known) && known is not null) return known;
        var found = new HashSet<Guid>();
        try
        {
            /* Twenty groups a call, Graph's limit. */
            foreach (var chunk in groups.Distinct().Chunk(20))
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, $"{GraphUrl}/v1.0/users/{person}/checkMemberGroups")
                {
                    Content = new StringContent(JsonSerializer.Serialize(new { groupIds = chunk }), Encoding.UTF8, "application/json"),
                };
                using var res = await SendAsync(req, ct);
                if (res.StatusCode == System.Net.HttpStatusCode.NotFound) break;   // nobody by that ID in this tenant
                res.EnsureSuccessStatusCode();
                using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
                foreach (var v in doc.RootElement.GetProperty("value").EnumerateArray())
                    if (Guid.TryParse(v.GetString(), out var g)) found.Add(g);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            /* A group grant not honored this time is safer than one honored wrongly: logged, and asked again in a minute. */
            log.LogWarning(ex, "Could not ask Microsoft Graph which Entra groups {Person} is in: their Entra group grants are left out for now.", person);
            cache.Set(key, (IReadOnlySet<Guid>)found, TimeSpan.FromMinutes(1));
            return found;
        }
        cache.Set(key, (IReadOnlySet<Guid>)found, TimeSpan.FromMinutes(10));
        return found;
    }

    /// <summary>Groups whose name starts with <paramref name="q"/>, for the grant form: up to ten.</summary>
    public async Task<IReadOnlyList<(Guid Id, string Name)>> FindGroupsAsync(string q, CancellationToken ct = default)
    {
        q = q.Trim();
        if (q.Length < 2 || !Available && Fake is null) return [];
        var filter = Uri.EscapeDataString($"startswith(displayName,'{q.Replace("'", "''")}')");
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{GraphUrl}/v1.0/groups?$filter={filter}&$select=id,displayName&$top=10&$orderby=displayName");
        req.Headers.Add("ConsistencyLevel", "eventual");
        try
        {
            using var res = await SendAsync(req, ct);
            res.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
            return doc.RootElement.GetProperty("value").EnumerateArray()
                .Select(g => (Guid.TryParse(g.GetProperty("id").GetString(), out var id) ? id : Guid.Empty, g.GetProperty("displayName").GetString() ?? ""))
                .Where(g => g.Item1 != Guid.Empty).ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Could not search Entra groups for {Query}.", q);
            return [];
        }
    }

    /// <summary>A group's name, or null when there is no such group (or Graph cannot be asked).</summary>
    public async Task<string?> GroupNameAsync(Guid id, CancellationToken ct = default)
    {
        if (!Available && Fake is null) return null;
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{GraphUrl}/v1.0/groups/{id}?$select=displayName");
        try
        {
            using var res = await SendAsync(req, ct);
            if (!res.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
            return doc.RootElement.GetProperty("displayName").GetString();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Could not look up Entra group {Group}.", id);
            return null;
        }
    }
}

/// <summary>How an Entra group is named in a grant: entra:&lt;object ID&gt;. Pure, for the tests.</summary>
public static class EntraPrincipal
{
    public const string Prefix = "entra:";

    public static string Of(Guid id) => Prefix + id.ToString("D");

    public static bool TryParse(string? principal, out Guid id)
    {
        id = Guid.Empty;
        return principal is not null && principal.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) && Guid.TryParse(principal[Prefix.Length..], out id);
    }
}
