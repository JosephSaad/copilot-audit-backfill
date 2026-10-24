using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Governance.Application.Integrations;
using Microsoft.Extensions.Options;

namespace Governance.Infrastructure;

/// <summary>
/// Power Platform API reads (X4 inventory with the operator's delegated token; X5 licensing app-only through the
/// Power Platform reader role, or delegated as a fallback). Read-only: allocationsV2 is only ever read here.
/// </summary>
public sealed class PowerPlatformClient(HttpClient http, IOptions<PowerPlatformOptions> options) : IPowerPlatformApi
{
    private string Base => options.Value.BaseUrl.TrimEnd('/');
    private string Version => options.Value.ApiVersion;

    public async Task<ResourceQueryPage> QueryResourcesAsync(string? delegatedToken, string body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{Base}/resourcequery/resources/query?api-version={Version}")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        Authorize(request, delegatedToken);
        using var response = await http.SendAsync(request, ct);
        await Http.EnsureAsync(response, "Power Platform inventory", ct);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return ParseQueryPage(doc.RootElement);
    }

    public static ResourceQueryPage ParseQueryPage(JsonElement root)
    {
        var data = new List<JsonElement>();
        if (root.TryGetProperty("data", out var d))
        {
            // The API returns rows as objects; tolerate the table form (columns + rows) as well.
            if (d.ValueKind == JsonValueKind.Array) data.AddRange(d.EnumerateArray().Select(x => x.Clone()));
            else if (d.ValueKind == JsonValueKind.Object && d.TryGetProperty("rows", out var rows) && d.TryGetProperty("columns", out var cols))
            {
                var names = cols.EnumerateArray().Select(c => c.Str("name") ?? "").ToList();
                foreach (var row in rows.EnumerateArray())
                {
                    var obj = new Dictionary<string, JsonElement>();
                    var i = 0;
                    foreach (var cell in row.EnumerateArray()) { if (i < names.Count) obj[names[i]] = cell.Clone(); i++; }
                    data.Add(JsonSerializer.SerializeToElement(obj));
                }
            }
        }
        var skip = root.Str("skipToken") ?? root.Str("$skipToken");
        long? total = root.TryGetProperty("totalRecords", out var t) && t.ValueKind == JsonValueKind.Number ? t.GetInt64() : null;
        return new ResourceQueryPage(data, skip, total);
    }

    public async Task<AllocationData?> GetAllocationAsync(string? delegatedToken, string environmentId, CancellationToken ct)
    {
        var filter = Uri.EscapeDataString($"environmentId eq '{environmentId}' and EntitlementId in ('MCSMessages')");
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{Base}/licensing/allocationsV2?api-version={Version}&$filter={filter}");
        Authorize(request, delegatedToken);
        using var response = await http.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        await Http.EnsureAsync(response, "Power Platform licensing", ct);
        var raw = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(raw);
        return ParseAllocation(doc.RootElement, environmentId, raw);
    }

    /// <summary>Finds the environment's Copilot Credits (MCSMessages) allocation and its TenantPool rule, whatever the envelope shape.</summary>
    public static AllocationData? ParseAllocation(JsonElement root, string environmentId, string raw)
    {
        IEnumerable<JsonElement> Scopes()
        {
            if (root.ValueKind == JsonValueKind.Array) return root.EnumerateArray();
            if (root.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.Array) return v.EnumerateArray();
            return new[] { root };
        }
        foreach (var scope in Scopes())
        {
            var env = scope.Obj("scope").Str("environmentId") ?? scope.Str("environmentId");
            if (env is not null && !string.Equals(env, environmentId, StringComparison.OrdinalIgnoreCase)) continue;
            var entitlements = scope.TryGetProperty("allocatedEntitlements", out var ae) ? ae : scope.Obj("entitlementAllocations");
            if (entitlements.ValueKind != JsonValueKind.Array) continue;
            foreach (var e in entitlements.EnumerateArray())
            {
                if (!string.Equals(e.Str("entitlementId"), "MCSMessages", StringComparison.OrdinalIgnoreCase)) continue;
                bool? pool = null, locked = null;
                if (e.TryGetProperty("enforcementRules", out var rules) && rules.ValueKind == JsonValueKind.Array)
                    foreach (var r in rules.EnumerateArray())
                        if (string.Equals(r.Str("ruleType"), "TenantPool", StringComparison.OrdinalIgnoreCase))
                        {
                            pool = r.Bool("enabled");
                            locked = r.Bool("lockedByPolicy") ?? r.Bool("isLocked");
                        }
                return new AllocationData(environmentId, e.Obj("allocation").Dec("quantity") ?? e.Dec("allocatedQuantity"), pool,
                    e.Dec("consumedQuantity") ?? e.Obj("consumption").Dec("quantity"), locked, raw);
            }
        }
        return new AllocationData(environmentId, null, null, null, null, raw);
    }

    /// <summary>Billing policies and their linked environments: environment ID (lower case) to billing policy ID.</summary>
    public async Task<IReadOnlyDictionary<string, string>> EnvironmentBillingPoliciesAsync(string? delegatedToken, CancellationToken ct)
    {
        var map = new Dictionary<string, string>();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{Base}/licensing/billingPolicies?api-version={Version}");
        Authorize(request, delegatedToken);
        using var response = await http.SendAsync(request, ct);
        await Http.EnsureAsync(response, "Power Platform billing policies", ct);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var policies = doc.RootElement.TryGetProperty("value", out var v) ? v : doc.RootElement;
        if (policies.ValueKind != JsonValueKind.Array) return map;
        foreach (var p in policies.EnumerateArray())
        {
            var id = p.Str("id");
            if (id is null) continue;
            using var envRequest = new HttpRequestMessage(HttpMethod.Get, $"{Base}/licensing/billingPolicies/{Uri.EscapeDataString(id)}/environments?api-version={Version}");
            Authorize(envRequest, delegatedToken);
            using var envResponse = await http.SendAsync(envRequest, ct);
            await Http.EnsureAsync(envResponse, "Power Platform billing policies", ct);
            using var envDoc = JsonDocument.Parse(await envResponse.Content.ReadAsStringAsync(ct));
            var envs = envDoc.RootElement.TryGetProperty("value", out var ev) ? ev : envDoc.RootElement;
            if (envs.ValueKind != JsonValueKind.Array) continue;
            foreach (var e in envs.EnumerateArray())
                if ((e.Str("environmentId") ?? e.Str("id")) is string envId) map[envId.ToLowerInvariant()] = id;
        }
        return map;
    }

    private static void Authorize(HttpRequestMessage request, string? delegatedToken)
    {
        if (!string.IsNullOrEmpty(delegatedToken)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", delegatedToken);
    }
}
