using System.Net;
using System.Text;
using System.Text.Json;
using Governance.Application.Integrations;
using Governance.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Governance.ContractTests;

public class GraphContractTests
{
    [Fact]
    public async Task Users_delta_follows_next_link_then_returns_the_delta_link()
    {
        var h = new ReplayHandler()
            .On(r => r.RequestUri!.Query.Contains("skiptoken=page2token"), "graph-users-delta-page2.json")
            .On(r => r.RequestUri!.AbsolutePath.EndsWith("/users/delta"), "graph-users-delta-page1.json");
        var graph = Clients.Graph(h);

        var first = await graph.UsersDeltaAsync(null, default);
        Assert.Equal(2, first.Items.Count);
        Assert.NotNull(first.NextLink);
        Assert.Null(first.DeltaLink);
        var avery = first.Items[0];
        Assert.Equal("Retail", avery.Division);
        Assert.Equal("CC-RET-100", avery.CostCenter);
        Assert.Equal(Guid.Parse("22222222-2222-2222-2222-222222222222"), avery.ManagerId);
        Assert.Contains("employeeOrgData", h.Requests[0].Url);

        var second = await graph.UsersDeltaAsync(first.NextLink, default);
        Assert.Null(second.NextLink);
        Assert.Contains("deltatoken=final-delta-token", second.DeltaLink);
        Assert.True(second.Items[0].Removed);
        Assert.False(second.Items[1].AccountEnabled);
        Assert.Equal(new DateTime(2026, 10, 1), second.Items[1].LeaveDateTime!.Value.Date);
    }

    [Fact]
    public async Task Users_delta_drops_the_leave_date_when_the_permission_is_missing()
    {
        var h = new ReplayHandler()
            .OnStatus(r => r.RequestUri!.Query.Contains("employeeLeaveDateTime"), HttpStatusCode.Forbidden, "{\"error\":{\"code\":\"Authorization_RequestDenied\"}}")
            .On(r => r.RequestUri!.AbsolutePath.EndsWith("/users/delta"), "graph-users-delta-page1.json");
        var page = await Clients.Graph(h).UsersDeltaAsync(null, default);
        Assert.Equal(2, page.Items.Count);
        Assert.Equal(2, h.Requests.Count);
        Assert.DoesNotContain("employeeLeaveDateTime", h.Requests[1].Url);
    }

    [Fact]
    public async Task Group_members_and_permission_errors()
    {
        var group = Guid.NewGuid();
        var h = new ReplayHandler().On(r => r.RequestUri!.AbsolutePath.Contains($"/groups/{group}/members"), "graph-group-members.json");
        Assert.Equal(2, (await Clients.Graph(h).GroupMemberIdsAsync(group, default)).Count);

        var refused = new ReplayHandler().OnStatus(_ => true, HttpStatusCode.Forbidden);
        var ex = await Assert.ThrowsAsync<IntegrationPermissionException>(() => Clients.Graph(refused).GroupMemberIdsAsync(group, default));
        Assert.Equal(403, ex.Status);
    }

    [Fact]
    public async Task Access_package_assignments_parse_target_state_and_expiry()
    {
        var pkg = Guid.NewGuid();
        var h = new ReplayHandler().On(r => r.RequestUri!.AbsolutePath.EndsWith("/entitlementManagement/assignments"), "graph-access-package-assignments.json");
        var list = await Clients.Graph(h).AssignmentsAsync(pkg, default);
        Assert.Equal(2, list.Count);
        Assert.Equal("delivered", list[0].State);
        Assert.Equal(Guid.Parse("11111111-1111-1111-1111-111111111111"), list[0].TargetObjectId);
        Assert.Equal(new DateTime(2027, 10, 20), list[0].ExpiresUtc!.Value.Date);
        Assert.Equal(pkg, list[0].AccessPackageId);
        Assert.Contains($"accessPackage/id eq '{pkg}'", Uri.UnescapeDataString(h.Requests[0].Url));
    }

    [Fact]
    public async Task Access_review_decisions_parse()
    {
        var h = new ReplayHandler().On(r => r.RequestUri!.AbsolutePath.EndsWith("/decisions"), "graph-access-review-decisions.json");
        var list = await Clients.Graph(h).DecisionsAsync("def-1", "inst-1", default);
        Assert.Equal("Approve", list[0].Decision);
        Assert.Equal(Guid.Parse("11111111-1111-1111-1111-111111111111"), list[0].PrincipalId);
        Assert.Null(list[1].ReviewedUtc);
    }

    [Fact]
    public void Mail_is_mime_with_html_and_plain_text_alternatives()
    {
        var mime = GraphClient.BuildMime("workbench@contoso.example", new MailMessageData(new[] { "a@contoso.example" }, "Décision: approuvée", "<p>Hi</p>", "Hi", "corr-1"));
        Assert.Contains("multipart/alternative", mime);
        Assert.Contains("Content-Type: text/plain; charset=utf-8", mime);
        Assert.Contains("Content-Type: text/html; charset=utf-8", mime);
        Assert.Contains("Subject: =?utf-8?B?", mime);
        Assert.Contains("From: workbench@contoso.example", mime);
    }

    [Fact]
    public async Task Mail_goes_from_the_scoped_mailbox_as_base64_mime()
    {
        var h = new ReplayHandler().OnStatus(r => r.Method == HttpMethod.Post, HttpStatusCode.Accepted, "");
        await Clients.Graph(h).SendAsync(new MailMessageData(new[] { "a@contoso.example" }, "S", "<p>H</p>", "T", null), default);
        Assert.EndsWith("/users/workbench%40contoso.example/sendMail", h.Requests[0].Url);
        var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(h.Requests[0].Body!));
        Assert.StartsWith("From: workbench@contoso.example", decoded);
    }

    [Fact]
    public void Activity_topic_is_a_Teams_deep_link_and_bulk_carries_recipients()
    {
        var graph = Clients.Graph(new ReplayHandler());
        var body = graph.ActivityBody("taskAssigned", "Approve CCG-2026-000001", "/Requests/abc", new Dictionary<string, string> { ["actor"] = "Workbench", ["taskTitle"] = "x" },
            new[] { Guid.NewGuid() });
        var json = JsonSerializer.Serialize(body);
        using var doc = JsonDocument.Parse(json);
        Assert.StartsWith("https://teams.microsoft.com/l/entity/19a68a93-1f6f-4034-9791-97b71816bfdc/launch?context=", doc.RootElement.GetProperty("topic").GetProperty("webUrl").GetString());
        Assert.Equal("text", doc.RootElement.GetProperty("topic").GetProperty("source").GetString());
        Assert.Equal("microsoft.graph.aadUserNotificationRecipient", doc.RootElement.GetProperty("recipients")[0].GetProperty("@odata.type").GetString());
    }
}

public class PowerPlatformContractTests
{
    [Fact]
    public async Task Resource_query_pages_with_skip_tokens_and_uses_the_delegated_token()
    {
        var h = new ReplayHandler()
            .On(r => r.Content!.ReadAsStringAsync().Result.Contains("skip-page-2"), "pp-resources-page2.json")
            .On(r => r.RequestUri!.AbsolutePath.EndsWith("/resourcequery/resources/query"), "pp-resources-page1.json");
        var pp = Clients.PowerPlatform(h);
        var first = await pp.QueryResourcesAsync("operator-token", InventorySyncService.QueryBody("microsoft.copilotstudio/agents", null), default);
        Assert.Equal(2, first.Data.Count);
        Assert.Equal("skip-page-2", first.SkipToken);
        var second = await pp.QueryResourcesAsync("operator-token", InventorySyncService.QueryBody("microsoft.copilotstudio/agents", first.SkipToken), default);
        Assert.Single(second.Data);
        Assert.Null(second.SkipToken);
        Assert.All(h.Requests, r => Assert.Equal("Bearer operator-token", r.Authorization));
        Assert.Contains("api-version=2024-10-01", h.Requests[0].Url);
        using var body = JsonDocument.Parse(h.Requests[0].Body!);
        Assert.Equal("PowerPlatformResources", body.RootElement.GetProperty("TableName").GetString());
        Assert.Equal("where", body.RootElement.GetProperty("Clauses")[0].GetProperty("$type").GetString());
    }

    [Fact]
    public void Inventory_rows_yield_harness_owner_and_environment()
    {
        using var doc = JsonDocument.Parse(Recorded.Read("pp-resources-page1.json"));
        var rows = doc.RootElement.GetProperty("data").EnumerateArray().ToList();
        var a = InventorySyncService.Parse("microsoft.copilotstudio/agents", rows[0]);
        Assert.Equal("agent-001", a.ResourceId);
        Assert.Equal("GitHubCopilot", a.Harness);
        Assert.Equal("Default-0000-env-1", a.EnvironmentId);
        Assert.Equal("11111111-1111-1111-1111-111111111111", a.OwnerObjectId);
        var b = InventorySyncService.Parse("microsoft.copilotstudio/agents", rows[1]);
        Assert.Equal("22222222-2222-2222-2222-222222222222", b.OwnerObjectId);
    }

    [Fact]
    public async Task Allocation_reads_credits_and_the_TenantPool_rule()
    {
        var h = new ReplayHandler().On(r => r.RequestUri!.AbsolutePath.EndsWith("/licensing/allocationsV2"), "pp-allocations.json");
        var a = await Clients.PowerPlatform(h).GetAllocationAsync(null, "env-2", default);
        Assert.NotNull(a);
        Assert.Equal(5000m, a!.AllocatedCredits);
        Assert.False(a.TenantPoolEnabled);
        Assert.Equal(1250.5m, a.ConsumedCredits);
        Assert.Null(h.Requests[0].Authorization);   // app-only: the token handler adds it, not the client
        Assert.Equal(HttpMethod.Get, h.Requests[0].Method);   // read only, never PUT
    }
}

public class ItsmContractTests
{
    [Fact]
    public async Task Create_then_get_closed_with_closed_by_and_notes()
    {
        var h = new ReplayHandler()
            .On(r => r.Method == HttpMethod.Post, "itsm-create.json", HttpStatusCode.Created)
            .On(r => r.RequestUri!.Query.Contains("number=SCTASK0010001"), "itsm-get-closed.json");
        var itsm = Clients.Itsm(h);
        var created = await itsm.CreateAsync(new ItsmCreate(Guid.NewGuid(), "TASK", "Configure", "settings", "", "3", "{}"), default);
        Assert.Equal("SCTASK0010001", created.Number);
        Assert.Equal("NEW", created.State);
        Assert.StartsWith("Basic ", h.Requests[0].Authorization);
        using (var body = JsonDocument.Parse(h.Requests[0].Body!))
            Assert.False(string.IsNullOrEmpty(body.RootElement.GetProperty("correlation_id").GetString()));

        var state = await itsm.GetAsync("SCTASK0010001", default);
        Assert.Equal("CLOSED", state.State);
        Assert.Equal("dana@contoso.example", state.ClosedByUpn);
        Assert.Contains("tenant pool off", state.ClosureNotes);
    }

    [Fact]
    public async Task Search_by_correlation_checks_both_tables()
    {
        var h = new ReplayHandler().OnStatus(_ => true, HttpStatusCode.OK, "{\"result\":[]}");
        Assert.Null(await Clients.Itsm(h).FindByCorrelationAsync(Guid.NewGuid(), default));
        Assert.Equal(2, h.Requests.Count);
        Assert.Contains("/table/sc_task", h.Requests[0].Url);
        Assert.Contains("/table/incident", h.Requests[1].Url);
    }

    [Theory]
    [InlineData("1", "NEW")]
    [InlineData("2", "IN_PROGRESS")]
    [InlineData("6", "RESOLVED")]
    [InlineData("3", "CLOSED")]
    [InlineData("4", "CANCELLED")]
    [InlineData("7", "CANCELLED")]
    public void States_map(string raw, string expected) => Assert.Equal(expected, ServiceNowItsmClient.MapState(raw));
}

/// <summary>The contract agreed with the Power Automate flow (section 6.6, X11).</summary>
public class ChannelFlowContractTests
{
    private static (FlowChannelPoster Poster, ReplayHandler Handler, FakeTokens Tokens) Make(HttpStatusCode status, string? url = "https://flow.example/trigger?sig=SECRET")
    {
        var h = new ReplayHandler().OnStatus(_ => true, status, "");
        var tokens = new FakeTokens();
        var poster = new FlowChannelPoster(new HttpClient(h), Options.Create(new NotificationOptions { ChannelFlowUrl = url }), tokens, NullLogger<FlowChannelPoster>.Instance);
        return (poster, h, tokens);
    }

    [Fact]
    public async Task Posts_queue_card_and_correlation_with_the_worker_token()
    {
        var (poster, h, tokens) = Make(HttpStatusCode.Accepted);
        await poster.PostAsync("OPERATIONS", "CCG-2026-000001: fulfillment task created", "Configure, then close the ticket.", "http://localhost:5450/Requests/x", default);

        Assert.Equal("https://service.flow.microsoft.com//.default", tokens.Scopes.Single());
        var req = h.Requests.Single();
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.Equal("Bearer app-token", req.Authorization);
        using var doc = JsonDocument.Parse(req.Body!);
        var root = doc.RootElement;
        Assert.Equal("OPERATIONS", root.GetProperty("queue").GetString());
        Assert.True(Guid.TryParse(root.GetProperty("correlationId").GetString(), out _));
        var card = root.GetProperty("card");
        Assert.Equal("AdaptiveCard", card.GetProperty("type").GetString());
        Assert.Equal("1.4", card.GetProperty("version").GetString());
        var actions = card.GetProperty("actions").EnumerateArray().ToList();
        Assert.All(actions, a => Assert.Equal("Action.OpenUrl", a.GetProperty("type").GetString()));   // never approve buttons
        Assert.Equal("http://localhost:5450/Requests/x", actions[0].GetProperty("url").GetString());
    }

    [Fact]
    public async Task Unknown_queue_is_permanent_and_refusal_is_a_permission_error()
    {
        await Assert.ThrowsAsync<PermanentDeliveryException>(() => Make(HttpStatusCode.BadRequest).Poster.PostAsync("NOPE", "t", "x", "http://l", default));
        var refused = await Assert.ThrowsAsync<IntegrationPermissionException>(() => Make(HttpStatusCode.Forbidden).Poster.PostAsync("RISK", "t", "x", "http://l", default));
        Assert.DoesNotContain("SECRET", refused.Message);
    }

    [Fact]
    public void Not_configured_without_a_url() => Assert.False(Make(HttpStatusCode.Accepted, url: null).Poster.IsConfigured);
}

public class EvidenceAndImportTests
{
    [Fact]
    public async Task Scanner_checks_content_against_the_extension()
    {
        var scanner = new SniffingEvidenceScanner();
        Assert.True((await scanner.ScanAsync(new MemoryStream(new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 2 }), "shot.png", default)).Clean);
        Assert.False((await scanner.ScanAsync(new MemoryStream("%PDF-1.7"u8.ToArray()), "shot.png", default)).Clean);
        Assert.False((await scanner.ScanAsync(new MemoryStream(new byte[] { 0x4D, 0x5A }), "tool.exe", default)).Clean);
        Assert.True((await scanner.ScanAsync(new MemoryStream("a,b\n1,2"u8.ToArray()), "export.csv", default)).Clean);
        Assert.False((await scanner.ScanAsync(new MemoryStream(new byte[] { 0x61, 0x00, 0x62 }), "export.csv", default)).Clean);
    }

    [Fact]
    public void Registry_csv_handles_quotes_commas_and_line_breaks()
    {
        var csv = "Agent ID,Name,Owner\r\nab-1,\"Sales, helper\",x@contoso.example\r\nab-2,\"Line\nbreak \"\"quoted\"\"\",y@contoso.example\r\n";
        var rows = RegistryImportService.ParseCsv(new MemoryStream(Encoding.UTF8.GetBytes(csv)));
        Assert.Equal(3, rows.Count);
        Assert.Equal("Sales, helper", rows[1][1]);
        Assert.Equal("Line\nbreak \"quoted\"", rows[2][1]);
    }
}
