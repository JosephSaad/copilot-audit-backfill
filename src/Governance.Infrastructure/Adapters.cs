using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Mail;
using System.Text;
using System.Text.Json;
using Governance.Application.Integrations;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Governance.Infrastructure;

/// <summary>
/// X8: ServiceNow-style table API. Searches by correlation ID before creating, so a retried outbox row never makes
/// a second ticket. Works against the local mock ITSM and a real ServiceNow instance.
/// </summary>
public sealed class ServiceNowItsmClient(HttpClient http, IOptions<ItsmOptions> options) : IItsmClient
{
    private ItsmOptions O => options.Value;

    private HttpRequestMessage Request(HttpMethod method, string path, object? body = null)
    {
        if (string.IsNullOrWhiteSpace(O.BaseUrl)) throw new InvalidOperationException("Itsm:BaseUrl is not configured.");
        var r = new HttpRequestMessage(method, O.BaseUrl.TrimEnd('/') + path);
        if (!string.IsNullOrEmpty(O.Username))
            r.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{O.Username}:{O.Password}")));
        r.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (body is not null) r.Content = JsonContent.Create(body, options: Http.Json);
        return r;
    }

    public async Task<ItsmTicketRef?> FindByCorrelationAsync(Guid correlationId, CancellationToken ct)
    {
        foreach (var table in new[] { O.TaskTable, O.IncidentTable })
        {
            using var req = Request(HttpMethod.Get, $"/api/now/table/{table}?sysparm_query=correlation_id={correlationId}&sysparm_limit=1");
            using var res = await http.SendAsync(req, ct);
            await Http.EnsureAsync(res, "ITSM", ct);
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
            var rows = doc.RootElement.GetProperty("result");
            if (rows.GetArrayLength() > 0) return ToRef(rows[0], table);
        }
        return null;
    }

    public async Task<ItsmTicketRef> CreateAsync(ItsmCreate c, CancellationToken ct)
    {
        var table = c.Kind == "INCIDENT" ? O.IncidentTable : O.TaskTable;
        using var req = Request(HttpMethod.Post, $"/api/now/table/{table}", new Dictionary<string, string>
        {
            ["short_description"] = c.ShortDescription,
            ["description"] = c.Description,
            ["correlation_id"] = c.CorrelationId.ToString(),
            ["correlation_display"] = "Copilot Governance Workbench",
            ["assignment_group"] = string.IsNullOrEmpty(c.AssignmentGroup) ? O.AssignmentGroup : c.AssignmentGroup,
            ["urgency"] = c.Urgency,
            ["u_workbench_payload"] = c.PayloadJson,
        });
        using var res = await http.SendAsync(req, ct);
        await Http.EnsureAsync(res, "ITSM", ct);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
        return ToRef(doc.RootElement.GetProperty("result"), table);
    }

    public async Task<ItsmTicketState> GetAsync(string number, CancellationToken ct)
    {
        var table = number.StartsWith("INC", StringComparison.OrdinalIgnoreCase) ? O.IncidentTable : O.TaskTable;
        using var req = Request(HttpMethod.Get, $"/api/now/table/{table}?sysparm_query=number={Uri.EscapeDataString(number)}&sysparm_limit=1&sysparm_display_value=all");
        using var res = await http.SendAsync(req, ct);
        await Http.EnsureAsync(res, "ITSM", ct);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
        var rows = doc.RootElement.GetProperty("result");
        if (rows.GetArrayLength() == 0) throw new InvalidOperationException($"ITSM ticket {number} was not found.");
        return ParseState(rows[0]);
    }

    public static ItsmTicketState ParseState(JsonElement r)
        => new(Field(r, "number") ?? "", MapState(Field(r, "state")), Field(r, "closed_by"), Field(r, "close_notes"),
            DateTime.TryParse(Field(r, "closed_at"), System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var d) ? d : null);

    /// <summary>ServiceNow states: 1 New, 2 In progress, 3 Closed complete, 4 Closed incomplete, 6 Resolved, 7 Closed skipped (and 8 Canceled for incidents).</summary>
    public static string MapState(string? s) => s?.Trim().ToLowerInvariant() switch
    {
        "1" or "new" or "open" => "NEW",
        "2" or "in progress" or "work in progress" => "IN_PROGRESS",
        "6" or "resolved" => "RESOLVED",
        "3" or "closed" or "closed complete" => "CLOSED",
        "4" or "7" or "8" or "closed incomplete" or "closed skipped" or "canceled" or "cancelled" => "CANCELLED",
        null => "UNKNOWN",
        var other => other.ToUpperInvariant(),
    };

    /// <summary>Reads a field returned as a string, or as {value, display_value} with sysparm_display_value=all (prefers the raw value for state).</summary>
    private static string? Field(JsonElement r, string name)
    {
        if (!r.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Object => name == "closed_by" ? (v.Str("email") ?? v.Str("user_name") ?? v.Str("display_value") ?? v.Str("value")) : (v.Str("value") ?? v.Str("display_value")),
            _ => v.ToString(),
        };
    }

    private ItsmTicketRef ToRef(JsonElement r, string table)
    {
        var number = Field(r, "number") ?? "";
        var sysId = Field(r, "sys_id");
        var url = sysId is null ? null : $"{O.BaseUrl.TrimEnd('/')}/nav_to.do?uri={table}.do?sys_id={sysId}";
        return new ItsmTicketRef(number, url, MapState(Field(r, "state")));
    }
}

/// <summary>
/// X11: Adaptive Card to a team queue through the OAuth-protected Power Automate flow. Contract: POST
/// { queue, card, correlationId } with the Worker's app-only token for service.flow.microsoft.com. 202 posted;
/// 400 unknown queue (permanent); 401/403 refused (N19 after two in a row); 429 and 5xx retried by the resilience handler.
/// The flow URL carries a signature and is never logged.
/// </summary>
public sealed class FlowChannelPoster(HttpClient http, IOptions<NotificationOptions> options, IAppTokenProvider tokens, ILogger<FlowChannelPoster> log) : IChannelPoster
{
    private static int _consecutiveAuthFailures;
    public static int ConsecutiveAuthFailures => Volatile.Read(ref _consecutiveAuthFailures);

    public bool IsConfigured => !string.IsNullOrWhiteSpace(options.Value.ChannelFlowUrl);

    public static readonly IReadOnlySet<string> Queues = new HashSet<string> { "INTAKE", "ARCHITECTURE", "OPERATIONS", "FINOPS", "RISK" };

    public async Task PostAsync(string queue, string title, string text, string linkUrl, CancellationToken ct)
    {
        if (!IsConfigured) throw new InvalidOperationException("The channel flow is not configured.");
        var body = BuildRequest(queue, title, text, linkUrl, Guid.NewGuid(), facts: null);
        using var request = new HttpRequestMessage(HttpMethod.Post, options.Value.ChannelFlowUrl) { Content = JsonContent.Create(body, options: Http.Json) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await tokens.GetTokenAsync(options.Value.ChannelFlowAudience, ct));
        using var response = await http.SendAsync(request, ct);
        switch (response.StatusCode)
        {
            case HttpStatusCode.Accepted or HttpStatusCode.OK:
                Interlocked.Exchange(ref _consecutiveAuthFailures, 0);
                return;
            case HttpStatusCode.BadRequest:
                throw new PermanentDeliveryException($"The channel flow does not know queue {queue}.");
            case HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden:
                Interlocked.Increment(ref _consecutiveAuthFailures);
                log.LogWarning("The channel flow refused the Worker's token (HTTP {Status}).", (int)response.StatusCode);
                throw new IntegrationPermissionException("Teams channel flow", (int)response.StatusCode);
            default:
                throw new HttpRequestException($"The channel flow returned HTTP {(int)response.StatusCode}.", null, response.StatusCode);
        }
    }

    /// <summary>The request the flow expects: the queue, a complete Adaptive Card 1.4 (links back to the Workbench only, never approve buttons), a correlation ID.</summary>
    public static Dictionary<string, object> BuildRequest(string queue, string title, string text, string linkUrl, Guid correlationId, IReadOnlyDictionary<string, string>? facts)
    {
        var bodyItems = new List<object>
        {
            new Dictionary<string, object> { ["type"] = "TextBlock", ["text"] = title, ["weight"] = "Bolder", ["size"] = "Medium", ["wrap"] = true },
            new Dictionary<string, object> { ["type"] = "TextBlock", ["text"] = text, ["wrap"] = true },
        };
        if (facts is { Count: > 0 })
            bodyItems.Add(new Dictionary<string, object> { ["type"] = "FactSet", ["facts"] = facts.Select(f => new Dictionary<string, string> { ["title"] = f.Key, ["value"] = f.Value }).ToArray() });
        var card = new Dictionary<string, object>
        {
            ["type"] = "AdaptiveCard",
            ["$schema"] = "http://adaptivecards.io/schemas/adaptive-card.json",
            ["version"] = "1.4",
            ["body"] = bodyItems,
            ["actions"] = new object[] { new Dictionary<string, object> { ["type"] = "Action.OpenUrl", ["title"] = "Open in the Workbench", ["url"] = linkUrl } },
        };
        return new Dictionary<string, object> { ["queue"] = queue, ["card"] = card, ["correlationId"] = correlationId.ToString() };
    }
}

/// <summary>Graph first; the internal SMTP relay when Graph fails and a relay is configured (X9).</summary>
public sealed class FallbackMailSender(GraphClient graph, IOptions<NotificationOptions> options, TenantSettings tenant, ILogger<FallbackMailSender> log) : IMailSender
{
    public async Task SendAsync(MailMessageData message, CancellationToken ct)
    {
        try { await graph.SendAsync(message, ct); }
        catch (Exception ex) when (!string.IsNullOrEmpty(options.Value.SmtpHost) && ex is not OperationCanceledException)
        {
            log.LogWarning("Graph sendMail failed ({Type}); using the SMTP relay.", ex.GetType().Name);
            using var mail = new MailMessage { From = new MailAddress(options.Value.SmtpFrom ?? tenant.Mailbox ?? "workbench@localhost"), Subject = message.Subject };
            foreach (var to in message.To) mail.To.Add(to);
            mail.AlternateViews.Add(AlternateView.CreateAlternateViewFromString(message.Text, Encoding.UTF8, "text/plain"));
            mail.AlternateViews.Add(AlternateView.CreateAlternateViewFromString(message.Html, Encoding.UTF8, "text/html"));
            using var smtp = new SmtpClient(options.Value.SmtpHost, options.Value.SmtpPort);
            await smtp.SendMailAsync(mail, ct);
        }
    }
}

/// <summary>
/// Default evidence scanner: allow-listed extensions whose content matches the type (magic bytes; text must be text).
/// Plug the institution's anti-malware service in by registering another <see cref="IEvidenceScanner"/>.
/// </summary>
public sealed class SniffingEvidenceScanner : IEvidenceScanner
{
    private static readonly Dictionary<string, byte[][]> Signatures = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = new[] { new byte[] { 0x89, 0x50, 0x4E, 0x47 } },
        [".jpg"] = new[] { new byte[] { 0xFF, 0xD8, 0xFF } },
        [".jpeg"] = new[] { new byte[] { 0xFF, 0xD8, 0xFF } },
        [".pdf"] = new[] { "%PDF"u8.ToArray() },
        [".xlsx"] = new[] { new byte[] { 0x50, 0x4B, 0x03, 0x04 } },
        [".docx"] = new[] { new byte[] { 0x50, 0x4B, 0x03, 0x04 } },
    };
    private static readonly HashSet<string> TextTypes = new(StringComparer.OrdinalIgnoreCase) { ".csv", ".json", ".txt" };

    public async Task<ScanResult> ScanAsync(Stream content, string fileName, CancellationToken ct)
    {
        var ext = Path.GetExtension(fileName);
        var head = new byte[8192];
        var start = content.CanSeek ? content.Position : 0;
        var n = await content.ReadAsync(head.AsMemory(0, head.Length), ct);
        if (content.CanSeek) content.Position = start;
        if (n == 0) return new ScanResult(false, "The file is empty.");
        if (Signatures.TryGetValue(ext, out var sigs))
            return sigs.Any(s => n >= s.Length && head.AsSpan(0, s.Length).SequenceEqual(s))
                ? new ScanResult(true, null)
                : new ScanResult(false, $"The content is not a {ext} file.");
        if (TextTypes.Contains(ext))
        {
            if (head.AsSpan(0, n).Contains((byte)0)) return new ScanResult(false, "A text file must not contain binary data.");
            try { new UTF8Encoding(false, true).GetString(head, 0, n - TrailingPartial(head, n)); }
            catch (DecoderFallbackException) { return new ScanResult(false, "A text file must be UTF-8."); }
            return new ScanResult(true, null);
        }
        return new ScanResult(false, $"Files of type {ext} are not accepted.");
    }

    /// <summary>Bytes of a UTF-8 sequence cut by the 8 KB read, so a valid file is not refused.</summary>
    private static int TrailingPartial(byte[] b, int n)
    {
        for (var i = 1; i <= Math.Min(3, n); i++)
        {
            var c = b[n - i];
            if ((c & 0xC0) == 0x80) continue;
            var need = (c & 0xE0) == 0xC0 ? 2 : (c & 0xF0) == 0xE0 ? 3 : (c & 0xF8) == 0xF0 ? 4 : 1;
            return need > i ? i : 0;
        }
        return 0;
    }
}

/// <summary>Record mode: nothing leaves the server; the dispatcher writes RECORDED deliveries.</summary>
public sealed class RecordingSenders : IMailSender, ITeamsActivitySender, ITeamsActivityBulkSender
{
    public Task SendAsync(MailMessageData message, CancellationToken ct) => Task.CompletedTask;
    public Task SendAsync(Guid recipient, string activityType, string previewText, string itemPublicPath, IReadOnlyDictionary<string, string> p, CancellationToken ct) => Task.CompletedTask;
    public Task SendBulkAsync(IReadOnlyList<Guid> recipients, string activityType, string previewText, string itemPublicPath, IReadOnlyDictionary<string, string> p, CancellationToken ct) => Task.CompletedTask;
}
