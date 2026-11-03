using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using Governance.Application.Integrations;
using Microsoft.Extensions.Options;

namespace Governance.Infrastructure;

public sealed class GraphOptions
{
    public string BaseUrl { get; set; } = "https://graph.microsoft.com/v1.0";
}

public sealed class PowerPlatformOptions
{
    public string BaseUrl { get; set; } = "https://api.powerplatform.com";
    public string ApiVersion { get; set; } = "2024-10-01";
}

public sealed class ItsmOptions
{
    /// <summary>ServiceNow-style table API base address (the mock ITSM locally: http://localhost:5470).</summary>
    public string BaseUrl { get; set; } = "";
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public string TaskTable { get; set; } = "sc_task";
    public string IncidentTable { get; set; } = "incident";
    public string AssignmentGroup { get; set; } = "Platform Operations";
}

public sealed class NotificationOptions
{
    /// <summary>Record: deliveries are written to ntf.Delivery with Status RECORDED and nothing leaves the server. Send: real delivery.</summary>
    public string Mode { get; set; } = "Record";
    /// <summary>Power Automate HTTP trigger URL. It carries a signature: never logged or printed.</summary>
    public string? ChannelFlowUrl { get; set; }
    public string ChannelFlowAudience { get; set; } = "https://service.flow.microsoft.com//.default";
    public string? SmtpHost { get; set; }
    public int SmtpPort { get; set; } = 25;
    public string? SmtpFrom { get; set; }
    public int MaxAttempts { get; set; } = 6;
    public string WorkbenchBaseUrl { get; set; } = "http://localhost:5291";
    public bool IsRecord => !string.Equals(Mode, "Send", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Finds the newest valid certificate with this subject (CurrentUser, then LocalMachine), so operations can rotate without a configuration change (R26).</summary>
public static class CertificateStore
{
    public static X509Certificate2 FindBySubject(string subject)
    {
        foreach (var location in new[] { StoreLocation.CurrentUser, StoreLocation.LocalMachine })
        {
            using var store = new X509Store(StoreName.My, location);
            store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
            var now = DateTime.Now;
            var cert = store.Certificates
                .Where(c => string.Equals(c.Subject, subject, StringComparison.OrdinalIgnoreCase) && c.HasPrivateKey && c.NotBefore <= now && c.NotAfter > now)
                .OrderByDescending(c => c.NotAfter)
                .FirstOrDefault();
            if (cert is not null) return cert;
        }
        throw new InvalidOperationException($"No valid certificate with subject {subject} and a private key in CurrentUser\\My or LocalMachine\\My.");
    }
}

/// <summary>App-only tokens for the Worker registration (certificate credential), cached until shortly before expiry.</summary>
public interface IAppTokenProvider
{
    Task<string> GetTokenAsync(string scope, CancellationToken ct);
}

public sealed class CertificateTokenProvider(TenantSettings tenant) : IAppTokenProvider
{
    private readonly ConcurrentDictionary<string, AccessToken> _cache = new();
    private readonly Lazy<TokenCredential> _credential = new(() =>
    {
        if (string.IsNullOrEmpty(tenant.TenantId) || string.IsNullOrEmpty(tenant.WorkerClientId) || string.IsNullOrEmpty(tenant.WorkerCertificateSubject))
            throw new InvalidOperationException("Tenant settings are missing the tenant ID, the Worker client ID or the Worker certificate subject.");
        var cert = CertificateStore.FindBySubject(tenant.WorkerCertificateSubject!);
        return new ClientCertificateCredential(tenant.TenantId, tenant.WorkerClientId, cert);
    });

    public async Task<string> GetTokenAsync(string scope, CancellationToken ct)
    {
        if (_cache.TryGetValue(scope, out var t) && t.ExpiresOn > DateTimeOffset.UtcNow.AddMinutes(5)) return t.Token;
        var token = await _credential.Value.GetTokenAsync(new TokenRequestContext(new[] { scope }), ct);
        _cache[scope] = token;
        return token.Token;
    }
}

/// <summary>Adds a correlation ID to every outbound call (section 6.6).</summary>
public sealed class CorrelationHandler : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var id = Guid.NewGuid().ToString();
        request.Headers.TryAddWithoutValidation("client-request-id", id);
        request.Headers.TryAddWithoutValidation("x-correlation-id", id);
        return base.SendAsync(request, ct);
    }
}

/// <summary>Adds the Worker's app-only bearer token when the request carries none (a delegated token wins).</summary>
public sealed class AppTokenHandler(IAppTokenProvider tokens, string scope) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        request.Headers.Authorization ??= new AuthenticationHeaderValue("Bearer", await tokens.GetTokenAsync(scope, ct));
        return await base.SendAsync(request, ct);
    }
}

internal static class Http
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Throws a permission exception for 401/403 (with no body text in the message) and a plain one for other failures.</summary>
    public static async Task EnsureAsync(HttpResponseMessage response, string integration, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new IntegrationPermissionException(integration, (int)response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(ct);
        var code = TryErrorCode(body);
        throw new HttpRequestException($"{integration} returned HTTP {(int)response.StatusCode}{(code is null ? "" : $" ({code})")}.", null, response.StatusCode);
    }

    private static string? TryErrorCode(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var e))
                return e.ValueKind == JsonValueKind.Object && e.TryGetProperty("code", out var c) ? c.GetString() : e.ToString();
        }
        catch (JsonException) { }
        return null;
    }

    public static string? Str(this JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public static bool? Bool(this JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;

    public static decimal? Dec(this JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.GetDecimal(),
            JsonValueKind.String when decimal.TryParse(v.GetString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var d) => d,
            _ => null,
        };
    }

    public static Guid? GuidOf(this JsonElement e, string name) => Guid.TryParse(e.Str(name), out var g) ? g : null;

    public static DateTime? Date(this JsonElement e, string name)
        => DateTime.TryParse(e.Str(name), System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var d) ? d : null;

    public static JsonElement Obj(this JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? v : default;
}
