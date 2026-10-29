using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Governance.Application.Integrations;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Governance.Infrastructure;

/// <summary>
/// Microsoft Graph over HTTP with the Worker's certificate (app-only). Reads directory, entitlement and access review
/// data; sends mail from the one scoped mailbox and Teams activity notifications. Raw HTTP rather than the SDK keeps
/// every call visible, and lets the contract tests replay recorded responses.
/// </summary>
public sealed class GraphClient(HttpClient http, IOptions<GraphOptions> options, TenantSettings tenant, ILogger<GraphClient> log)
    : IDirectoryApi, IEntitlementApi, IAccessReviewApi, IMailSender, ITeamsActivitySender, ITeamsActivityBulkSender, ISeatLicenseApi
{
    private const string Integration = "Microsoft Graph";
    private const string FullSelect = "id,displayName,userPrincipalName,mail,jobTitle,department,accountEnabled,employeeOrgData,employeeLeaveDateTime,manager";
    // employeeLeaveDateTime needs User-LifeCycleInfo.Read.All; without it the sync falls back to this list.
    private const string BasicSelect = "id,displayName,userPrincipalName,mail,jobTitle,department,accountEnabled,employeeOrgData,manager";
    private static bool _leaveDateRefused;

    private string Base => options.Value.BaseUrl.TrimEnd('/');

    // ------------------------------------------------------------------------------------------
    // Directory (X1)
    // ------------------------------------------------------------------------------------------

    public async Task<DeltaPage<DirectoryUser>> UsersDeltaAsync(string? link, CancellationToken ct)
    {
        var url = link ?? $"{Base}/users/delta?$select={(_leaveDateRefused ? BasicSelect : FullSelect)}";
        using var response = await http.GetAsync(url, ct);
        if (response.StatusCode == HttpStatusCode.Forbidden && url.Contains("employeeLeaveDateTime", StringComparison.OrdinalIgnoreCase))
        {
            // The leave date needs another permission: start a full delta without it.
            _leaveDateRefused = true;
            log.LogInformation("employeeLeaveDateTime refused (needs User-LifeCycleInfo.Read.All); syncing users without it.");
            return await UsersDeltaAsync(null, ct);
        }
        if (link is not null && await IsResyncRequiredAsync(response, ct))
            throw new DeltaResyncRequiredException($"Graph asked for a full resync of the users delta (HTTP {(int)response.StatusCode}).");
        await Http.EnsureAsync(response, Integration, ct);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return ParseUsersDelta(doc.RootElement);
    }

    /// <summary>
    /// A saved delta link expired or is invalid: Graph answers 410 Gone (syncStateNotFound, resyncRequired) or a 400
    /// that names the token or sync state. Either way the caller drops the link and starts a full sync.
    /// </summary>
    public static async Task<bool> IsResyncRequiredAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.StatusCode == HttpStatusCode.Gone) return true;
        if (response.StatusCode != HttpStatusCode.BadRequest) return false;
        await response.Content.LoadIntoBufferAsync(ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        return body.Contains("syncStateNotFound", StringComparison.OrdinalIgnoreCase)
               || body.Contains("resyncRequired", StringComparison.OrdinalIgnoreCase)
               || body.Contains("syncStateInvalid", StringComparison.OrdinalIgnoreCase)
               || (body.Contains("token", StringComparison.OrdinalIgnoreCase) && body.Contains("delta", StringComparison.OrdinalIgnoreCase));
    }

    public async Task<DirectoryUser?> GetUserAsync(Guid id, CancellationToken ct)
    {
        using var response = await http.GetAsync($"{Base}/users/{id}?$select={BasicSelect.Replace(",manager", "")}", ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        await Http.EnsureAsync(response, Integration, ct);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var u = doc.RootElement;
        var org = u.Obj("employeeOrgData");
        return new DirectoryUser(id, false, u.Str("displayName"), u.Str("userPrincipalName"), u.Str("mail"), u.Str("jobTitle"), u.Str("department"),
            u.Bool("accountEnabled"), org.Str("division"), org.Str("costCenter"), null, null);
    }

    public static DeltaPage<DirectoryUser> ParseUsersDelta(JsonElement root)
    {
        var users = new List<DirectoryUser>();
        foreach (var u in root.GetProperty("value").EnumerateArray())
        {
            var id = u.GuidOf("id");
            if (id is null) continue;
            var removed = u.TryGetProperty("@removed", out _);
            var org = u.Obj("employeeOrgData");
            Guid? manager = null;
            if (u.TryGetProperty("manager@delta", out var md) && md.ValueKind == JsonValueKind.Array)
                foreach (var m in md.EnumerateArray()) { if (!m.TryGetProperty("@removed", out _)) manager = m.GuidOf("id"); }
            else if (u.Obj("manager").ValueKind == JsonValueKind.Object) manager = u.Obj("manager").GuidOf("id");
            users.Add(new DirectoryUser(id.Value, removed, u.Str("displayName"), u.Str("userPrincipalName"), u.Str("mail"), u.Str("jobTitle"), u.Str("department"),
                u.Bool("accountEnabled"), org.Str("division"), org.Str("costCenter"), u.Date("employeeLeaveDateTime"), manager));
        }
        return new DeltaPage<DirectoryUser>(users, root.Str("@odata.nextLink"), root.Str("@odata.deltaLink"));
    }

    public async Task<IReadOnlyList<Guid>> GroupMemberIdsAsync(Guid groupId, CancellationToken ct)
    {
        var ids = new List<Guid>();
        await foreach (var e in PagesAsync($"{Base}/groups/{groupId}/members?$select=id&$top=999", ct))
            if (e.GuidOf("id") is Guid g) ids.Add(g);
        return ids;
    }

    // ------------------------------------------------------------------------------------------
    // Entitlement management (X2)
    // ------------------------------------------------------------------------------------------

    public async Task<IReadOnlyList<AccessPackageAssignmentData>> AssignmentsAsync(Guid accessPackageId, CancellationToken ct)
    {
        var list = new List<AccessPackageAssignmentData>();
        var url = $"{Base}/identityGovernance/entitlementManagement/assignments?$filter=accessPackage/id eq '{accessPackageId}'&$expand=target";
        await foreach (var a in PagesAsync(url, ct)) list.Add(ParseAssignment(a, accessPackageId));
        return list;
    }

    public static AccessPackageAssignmentData ParseAssignment(JsonElement a, Guid accessPackageId)
    {
        var target = a.Obj("target");
        var schedule = a.Obj("schedule").Obj("expiration");
        return new AccessPackageAssignmentData(a.GuidOf("id") ?? Guid.Empty, accessPackageId, target.GuidOf("objectId"),
            target.Str("principalName") ?? target.Str("email"), a.Str("state") ?? "unknown",
            a.Date("expiredDateTime") ?? schedule.Date("endDateTime"), a.Date("createdDateTime") ?? a.Obj("schedule").Date("startDateTime"));
    }

    public async Task<IReadOnlyList<AssignmentRequestData>> AssignmentRequestsAsync(Guid accessPackageId, CancellationToken ct)
    {
        var list = new List<AssignmentRequestData>();
        var url = $"{Base}/identityGovernance/entitlementManagement/assignmentRequests?$filter=accessPackage/id eq '{accessPackageId}'&$expand=assignment,requestor";
        try
        {
            await foreach (var r in PagesAsync(url, ct))
                list.Add(new AssignmentRequestData(r.GuidOf("id") ?? Guid.Empty, r.Obj("assignment").GuidOf("id"), r.Obj("requestor").GuidOf("objectId"),
                    r.Str("requestType") ?? "", r.Str("state") ?? "", r.Str("justification"), r.Date("createdDateTime"), r.Date("completedDateTime")));
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.BadRequest)
        {
            // Requests only enrich the decision record with the justification; assignments are the source.
            log.LogInformation("Assignment requests filter not supported here; continuing with assignments only.");
        }
        return list;
    }

    public async Task<IReadOnlyList<ApprovalStageData>?> ApprovalStagesAsync(Guid assignmentRequestId, CancellationToken ct)
    {
        using var response = await http.GetAsync($"{Base}/identityGovernance/entitlementManagement/accessPackageAssignmentApprovals/{assignmentRequestId}/stages", ct);
        // No approval on the policy (404), or the role does not reach it (403): the caller records that the approver is unknown.
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden or HttpStatusCode.BadRequest)
        {
            log.LogInformation("Approval stages of request {Request} not readable (HTTP {Status}).", assignmentRequestId, (int)response.StatusCode);
            return null;
        }
        await Http.EnsureAsync(response, Integration, ct);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return ParseApprovalStages(doc.RootElement);
    }

    public static IReadOnlyList<ApprovalStageData> ParseApprovalStages(JsonElement root)
    {
        var list = new List<ApprovalStageData>();
        foreach (var s in root.GetProperty("value").EnumerateArray())
        {
            var by = s.Obj("reviewedBy");
            var byId = by.GuidOf("id");
            if (byId == Guid.Empty) byId = null;   // Graph uses the empty GUID while the stage is not reviewed
            list.Add(new ApprovalStageData(s.Str("id"), s.Str("reviewResult"), byId, by.Str("displayName"), s.Date("reviewedDateTime"), s.Str("justification"), s.Str("status")));
        }
        return list;
    }

    // ------------------------------------------------------------------------------------------
    // Access reviews (X3)
    // ------------------------------------------------------------------------------------------

    public async Task<IReadOnlyList<string>> InstanceIdsAsync(string definitionId, CancellationToken ct)
    {
        var ids = new List<string>();
        await foreach (var i in PagesAsync($"{Base}/identityGovernance/accessReviews/definitions/{Uri.EscapeDataString(definitionId)}/instances", ct))
            if (i.Str("id") is string id) ids.Add(id);
        return ids;
    }

    public async Task<IReadOnlyList<AccessReviewDecisionData>> DecisionsAsync(string definitionId, string instanceId, CancellationToken ct)
    {
        var list = new List<AccessReviewDecisionData>();
        await foreach (var d in PagesAsync($"{Base}/identityGovernance/accessReviews/definitions/{Uri.EscapeDataString(definitionId)}/instances/{Uri.EscapeDataString(instanceId)}/decisions", ct))
            list.Add(ParseDecision(d));
        return list;
    }

    public static AccessReviewDecisionData ParseDecision(JsonElement d)
        => new(d.Str("id") ?? "", d.Obj("principal").GuidOf("id"), d.Str("decision") ?? "NotReviewed", d.Obj("reviewedBy").GuidOf("id"),
            d.Date("reviewedDateTime"), d.Str("justification"));

    // ------------------------------------------------------------------------------------------
    // Mail (X9): MIME so the message carries an HTML part and a plain-text alternative
    // ------------------------------------------------------------------------------------------

    public async Task SendAsync(MailMessageData message, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(tenant.Mailbox)) throw new InvalidOperationException("No notification mailbox is configured.");
        var mime = BuildMime(tenant.Mailbox!, message);
        using var content = new StringContent(Convert.ToBase64String(Encoding.UTF8.GetBytes(mime)), Encoding.ASCII, "text/plain");
        using var response = await http.PostAsync($"{Base}/users/{Uri.EscapeDataString(tenant.Mailbox!)}/sendMail", content, ct);
        await Http.EnsureAsync(response, "Graph sendMail", ct);
    }

    public static string BuildMime(string from, MailMessageData m)
    {
        static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s), Base64FormattingOptions.InsertLineBreaks);
        var boundary = "cgw-" + Guid.NewGuid().ToString("n");
        var sb = new StringBuilder();
        sb.Append("From: ").Append(from).Append("\r\n");
        sb.Append("To: ").Append(string.Join(", ", m.To)).Append("\r\n");
        sb.Append("Subject: =?utf-8?B?").Append(Convert.ToBase64String(Encoding.UTF8.GetBytes(m.Subject))).Append("?=\r\n");
        if (m.CorrelationId is not null) sb.Append("X-Workbench-Correlation: ").Append(m.CorrelationId).Append("\r\n");
        sb.Append("MIME-Version: 1.0\r\n");
        sb.Append("Content-Type: multipart/alternative; boundary=\"").Append(boundary).Append("\"\r\n\r\n");
        sb.Append("--").Append(boundary).Append("\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Transfer-Encoding: base64\r\n\r\n").Append(B64(m.Text)).Append("\r\n");
        sb.Append("--").Append(boundary).Append("\r\nContent-Type: text/html; charset=utf-8\r\nContent-Transfer-Encoding: base64\r\n\r\n").Append(B64(m.Html)).Append("\r\n");
        sb.Append("--").Append(boundary).Append("--\r\n");
        return sb.ToString();
    }

    // ------------------------------------------------------------------------------------------
    // Teams activity feed (X10)
    // ------------------------------------------------------------------------------------------

    public async Task SendAsync(Guid recipientEntraObjectId, string activityType, string previewText, string itemPublicPath, IReadOnlyDictionary<string, string> templateParameters, CancellationToken ct)
    {
        var body = ActivityBody(activityType, previewText, itemPublicPath, templateParameters, recipients: null);
        using var response = await http.PostAsJsonAsync($"{Base}/users/{recipientEntraObjectId}/teamwork/sendActivityNotification", body, Http.Json, ct);
        await Http.EnsureAsync(response, "Teams activity feed", ct);
    }

    public async Task SendBulkAsync(IReadOnlyList<Guid> recipients, string activityType, string previewText, string itemPublicPath, IReadOnlyDictionary<string, string> templateParameters, CancellationToken ct)
    {
        foreach (var chunk in recipients.Chunk(100))
        {
            var body = ActivityBody(activityType, previewText, itemPublicPath, templateParameters, chunk);
            using var response = await http.PostAsJsonAsync($"{Base}/teamwork/sendActivityNotificationToRecipients", body, Http.Json, ct);
            await Http.EnsureAsync(response, "Teams activity feed", ct);
        }
    }

    /// <summary>The topic's webUrl must be a Teams deep link (R19): it opens the Workbench app's launcher tab on the item.</summary>
    public Dictionary<string, object?> ActivityBody(string activityType, string previewText, string itemPath, IReadOnlyDictionary<string, string> parameters, IEnumerable<Guid>? recipients)
    {
        var appId = tenant.WorkerClientId ?? "";
        var context = Uri.EscapeDataString(JsonSerializer.Serialize(new { subEntityId = itemPath }));
        var body = new Dictionary<string, object?>
        {
            ["topic"] = new { source = "text", value = "Copilot Governance Workbench", webUrl = $"https://teams.microsoft.com/l/entity/{appId}/launch?context={context}" },
            ["activityType"] = activityType,
            ["previewText"] = new { content = previewText.Length > 150 ? previewText[..150] : previewText },
            // {actor} is reserved: Teams fills it with the app's name, and sending it fails with "incorrect template parameter arity".
            ["templateParameters"] = parameters.Where(kv => !string.Equals(kv.Key, "actor", StringComparison.OrdinalIgnoreCase))
                                               .Select(kv => new { name = kv.Key, value = kv.Value }).ToArray(),
        };
        if (recipients is not null)
            body["recipients"] = recipients.Select(r => new Dictionary<string, object> { ["@odata.type"] = "microsoft.graph.aadUserNotificationRecipient", ["userId"] = r }).ToArray();
        return body;
    }

    // ------------------------------------------------------------------------------------------
    // Copilot seat licenses (X13): read-only
    // ------------------------------------------------------------------------------------------

    public async Task<IReadOnlyList<SubscribedSkuData>> SubscribedSkusAsync(CancellationToken ct)
    {
        var list = new List<SubscribedSkuData>();
        await foreach (var e in PagesAsync($"{Base}/subscribedSkus?$select=skuId,skuPartNumber,prepaidUnits,consumedUnits", ct))
            if (ParseSku(e) is { } sku) list.Add(sku);
        return list;
    }

    public static SubscribedSkuData? ParseSku(JsonElement e)
    {
        if (e.GuidOf("skuId") is not Guid id) return null;
        var units = e.Obj("prepaidUnits");
        return new SubscribedSkuData(id, e.Str("skuPartNumber") ?? id.ToString(), Int(units, "enabled"), Int(units, "warning"), Int(units, "suspended"), Int(e, "consumedUnits"));
    }

    private const string GroupSelect = "id,displayName,onPremisesSyncEnabled,assignedLicenses";

    public async Task<SeatGroupData?> FindGroupAsync(string displayName, CancellationToken ct)
    {
        var name = displayName.Replace("'", "''");
        var found = new List<JsonElement>();
        await foreach (var g in PagesAsync($"{Base}/groups?$filter=displayName eq '{Uri.EscapeDataString(name)}'&$select={GroupSelect}", ct))
            found.Add(g);
        if (found.Count == 0) return null;
        return ParseGroup(found[0]) with { DuplicateName = found.Count > 1 };
    }

    public async Task<SeatGroupData?> GetGroupAsync(Guid groupId, CancellationToken ct)
    {
        using var response = await http.GetAsync($"{Base}/groups/{groupId}?$select={GroupSelect}", ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        await Http.EnsureAsync(response, Integration, ct);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return ParseGroup(doc.RootElement);
    }

    public static SeatGroupData ParseGroup(JsonElement g)
    {
        var skus = new List<Guid>();
        if (g.Obj("assignedLicenses") is { ValueKind: JsonValueKind.Array } licenses)
            foreach (var l in licenses.EnumerateArray()) if (l.GuidOf("skuId") is Guid s) skus.Add(s);
        return new SeatGroupData(g.GuidOf("id") ?? Guid.Empty, g.Str("displayName") ?? "", g.Bool("onPremisesSyncEnabled"), skus);
    }

    public async Task<GroupMembersData> GroupMembersAsync(Guid groupId, CancellationToken ct)
    {
        var users = new List<Guid>();
        var nested = 0;
        await foreach (var e in PagesAsync($"{Base}/groups/{groupId}/members?$select=id&$top=999", ct))
        {
            var type = e.Str("@odata.type") ?? "#microsoft.graph.user";
            if (type.EndsWith(".group", StringComparison.OrdinalIgnoreCase)) nested++;
            else if (type.EndsWith(".user", StringComparison.OrdinalIgnoreCase) && e.GuidOf("id") is Guid id) users.Add(id);
        }
        return new GroupMembersData(users, nested);
    }

    public async Task<IReadOnlyList<LicensedUserData>> UsersWithSkuAsync(Guid skuId, CancellationToken ct)
    {
        var list = new List<LicensedUserData>();
        var url = $"{Base}/users?$filter=assignedLicenses/any(x:x/skuId eq {skuId})&$select=id,userPrincipalName,usageLocation,licenseAssignmentStates&$count=true&$top=999";
        // Filtering on assignedLicenses is an advanced query: it needs ConsistencyLevel: eventual and $count.
        await foreach (var u in PagesAsync(url, ct, eventual: true))
            if (ParseLicensedUser(u) is { } user) list.Add(user);
        return list;
    }

    public static LicensedUserData? ParseLicensedUser(JsonElement u)
    {
        if (u.GuidOf("id") is not Guid id) return null;
        var states = new List<LicenseStateData>();
        if (u.Obj("licenseAssignmentStates") is { ValueKind: JsonValueKind.Array } arr)
            foreach (var s in arr.EnumerateArray())
                if (s.GuidOf("skuId") is Guid sku)
                {
                    var error = s.Str("error");
                    states.Add(new LicenseStateData(sku, s.GuidOf("assignedByGroup"), s.Str("state"),
                        string.IsNullOrEmpty(error) || error == "None" ? null : error, s.Date("lastUpdatedDateTime")));
                }
        return new LicensedUserData(id, u.Str("userPrincipalName"), u.Str("usageLocation"), states);
    }

    public async Task<CopilotUsageData> CopilotUsageAsync(string period, CancellationToken ct)
    {
        // /copilot/reports is the current path; tenants that do not answer it yet answer the beta reports path.
        var p = Uri.EscapeDataString(period);
        var rows = new List<JsonElement>();
        try
        {
            // Without $format the report can answer with CSV (seen live: "Report Refresh Date,..."); ask for JSON.
            await foreach (var r in PagesAsync($"{Base}/copilot/reports/getMicrosoft365CopilotUsageUserDetail(period='{p}')?$format=application/json", ct)) rows.Add(r);
        }
        catch (Exception ex) when (ex is HttpRequestException { StatusCode: HttpStatusCode.NotFound or HttpStatusCode.BadRequest } or JsonException)
        {
            rows.Clear();
            var beta = Base.EndsWith("/v1.0", StringComparison.OrdinalIgnoreCase) ? Base[..^5] + "/beta" : Base;
            await foreach (var r in PagesAsync($"{beta}/reports/getMicrosoft365CopilotUsageUserDetail(period='{p}')?$format=application/json", ct)) rows.Add(r);
        }
        return ParseUsage(rows);
    }

    public static CopilotUsageData ParseUsage(IReadOnlyList<JsonElement> rows)
    {
        var users = new List<CopilotUsageRow>();
        DateTime? refresh = null;
        var named = 0;
        var hashed = 0;
        foreach (var r in rows)
        {
            refresh ??= r.Date("reportRefreshDate");
            var upn = r.Str("userPrincipalName");
            if (string.IsNullOrWhiteSpace(upn)) continue;
            if (upn.Contains('@')) named++; else hashed++;
            users.Add(new CopilotUsageRow(upn, r.Date("lastActivityDate")));
        }
        // Concealed names come back as hashes with no '@': they cannot be matched to anyone, so none are kept.
        var concealed = hashed > 0 && named == 0;
        return new CopilotUsageData(concealed ? Array.Empty<CopilotUsageRow>() : users.Where(u => u.UserPrincipalName.Contains('@')).ToList(), refresh, concealed);
    }

    private static int Int(JsonElement e, string name) => e.Dec(name) is decimal d ? (int)d : 0;

    // ------------------------------------------------------------------------------------------

    private async IAsyncEnumerable<JsonElement> PagesAsync(string url, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct, bool eventual = false)
    {
        string? next = url;
        var guard = 0;
        while (next is not null && guard++ < 1000)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, next);
            if (eventual) request.Headers.Add("ConsistencyLevel", "eventual");
            using var response = await http.SendAsync(request, ct);
            await Http.EnsureAsync(response, Integration, ct);
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            foreach (var v in doc.RootElement.GetProperty("value").EnumerateArray()) yield return v.Clone();
            next = doc.RootElement.Str("@odata.nextLink");
        }
    }
}
