using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Governance.Application;
using Governance.Application.Integrations;
using Governance.Application.Outbox;
using Governance.Infrastructure;
using Governance.Notifications;
using Microsoft.Extensions.Logging.Abstractions;

namespace Governance.ContractTests;

/// <summary>The review fixes: activity feed parameters, delta resync, the approver of an Entra assignment.</summary>
public class WorkerFixTests
{
    // ------------------------------------------------------------------------------------------
    // Teams activity feed: {actor} is reserved (Teams fills it with the app's name)
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void Activity_body_never_sends_the_reserved_actor_parameter()
    {
        var graph = Clients.Graph(new ReplayHandler());
        var body = graph.ActivityBody("taskAssigned", "preview", "/workbench/Requests/abc",
            new Dictionary<string, string> { ["Actor"] = "The Workbench", ["actor"] = "x", ["taskTitle"] = "CCG-1: Routine approval" }, null);
        var json = JsonSerializer.Serialize(body);
        using var doc = JsonDocument.Parse(json);
        var names = doc.RootElement.GetProperty("templateParameters").EnumerateArray().Select(p => p.GetProperty("name").GetString()).ToList();
        Assert.Equal(new[] { "taskTitle" }, names);
    }

    [Fact]
    public void Template_parameters_match_each_activity_types_template_text_except_actor()
    {
        var manifest = FindManifest();
        using var doc = JsonDocument.Parse(File.ReadAllText(manifest));
        foreach (var type in doc.RootElement.GetProperty("activities").GetProperty("activityTypes").EnumerateArray())
        {
            var name = type.GetProperty("type").GetString()!;
            var expected = Regex.Matches(type.GetProperty("templateText").GetString()!, @"\{(\w+)\}").Select(m => m.Groups[1].Value)
                                .Where(t => !string.Equals(t, "actor", StringComparison.OrdinalIgnoreCase)).OrderBy(t => t).ToList();
            var actual = NotificationDelivery.TemplateParameters(new OutboxMessage { ActivityType = name, Subject = "CCG-2026-000001: Approved" })
                                             .Keys.OrderBy(k => k).ToList();
            Assert.Equal(expected, actual);
        }
    }

    private static string FindManifest()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var p = Path.Combine(dir.FullName, "setup", "teams-app", "manifest.json");
            if (File.Exists(p)) return p;
        }
        throw new FileNotFoundException("setup/teams-app/manifest.json not found above the test output.");
    }

    // ------------------------------------------------------------------------------------------
    // Directory delta: an expired link asks for a full resync
    // ------------------------------------------------------------------------------------------

    [Fact]
    public async Task An_expired_delta_link_raises_resync_required()
    {
        var h = new ReplayHandler().OnStatus(r => r.RequestUri!.ToString().Contains("deltatoken"), HttpStatusCode.Gone,
            "{\"error\":{\"code\":\"syncStateNotFound\",\"message\":\"Sync state generation is not found.\"}}");
        await Assert.ThrowsAsync<DeltaResyncRequiredException>(() => Clients.Graph(h).UsersDeltaAsync("https://graph.microsoft.com/v1.0/users/delta?$deltatoken=old", CancellationToken.None));
    }

    [Theory]
    [InlineData(HttpStatusCode.Gone, "{}", true)]
    [InlineData(HttpStatusCode.BadRequest, "{\"error\":{\"code\":\"resyncRequired\"}}", true)]
    [InlineData(HttpStatusCode.BadRequest, "{\"error\":{\"code\":\"Request_BadRequest\",\"message\":\"Invalid delta token.\"}}", true)]
    [InlineData(HttpStatusCode.BadRequest, "{\"error\":{\"code\":\"Request_BadRequest\",\"message\":\"Invalid filter.\"}}", false)]
    [InlineData(HttpStatusCode.InternalServerError, "{}", false)]
    public async Task Resync_is_recognized_only_for_expired_or_invalid_tokens(HttpStatusCode status, string body, bool expected)
    {
        using var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        Assert.Equal(expected, await GraphClient.IsResyncRequiredAsync(response, CancellationToken.None));
    }

    // ------------------------------------------------------------------------------------------
    // Entitlement sync: the approver of a delivered assignment
    // ------------------------------------------------------------------------------------------

    [Fact]
    public async Task Approval_stages_name_the_reviewer_and_ignore_the_empty_guid()
    {
        var h = new ReplayHandler().On(r => r.RequestUri!.AbsolutePath.EndsWith("/stages"), "em-approval-stages.json");
        var stages = await Clients.Graph(h).ApprovalStagesAsync(Guid.Parse("abd306ef-f7b2-4a10-9fd1-493454322489"), CancellationToken.None);
        Assert.NotNull(stages);
        Assert.Equal(Guid.Parse("04ab5bfe-2b9e-4c5b-8e53-9c6b7f6f1a11"), stages![0].ReviewedById);
        Assert.Equal("Rita Approver", stages[0].ReviewedByName);
        Assert.Null(stages[1].ReviewedById);
        Assert.Contains("/identityGovernance/entitlementManagement/accessPackageAssignmentApprovals/abd306ef-f7b2-4a10-9fd1-493454322489/stages", h.Requests[0].Url);
    }

    [Fact]
    public async Task Unreadable_approval_stages_return_null_not_an_error()
    {
        var h = new ReplayHandler().OnStatus(_ => true, HttpStatusCode.Forbidden);
        Assert.Null(await Clients.Graph(h).ApprovalStagesAsync(Guid.NewGuid(), CancellationToken.None));
    }

    [Fact]
    public async Task The_approver_is_the_reviewer_the_admin_or_unknown_with_a_reason()
    {
        var reviewer = Guid.NewGuid();
        var admin = Guid.NewGuid();
        var api = new FakeEntitlements
        {
            Stages = new List<ApprovalStageData> { new("s1", "Approve", reviewer, "Rita", new DateTime(2026, 10, 20, 14, 0, 0, DateTimeKind.Utc), "ok", "Completed") },
        };
        var svc = new EntitlementSyncService(null!, api, null!, null!, null!, new SystemClock(), new TenantSettings(), NullLogger<EntitlementSyncService>.Instance);

        var byReview = await svc.ReadApproverAsync(new AssignmentRequestData(Guid.NewGuid(), null, Guid.NewGuid(), "userAdd", "Delivered", null, null, null), CancellationToken.None);
        Assert.Equal(reviewer, byReview.ObjectId);
        Assert.Null(byReview.Unknown);

        var byAdmin = await svc.ReadApproverAsync(new AssignmentRequestData(Guid.NewGuid(), null, admin, "adminAdd", "Delivered", null, null, null), CancellationToken.None);
        Assert.Equal(admin, byAdmin.ObjectId);

        api.Stages = null;
        var unreadable = await svc.ReadApproverAsync(new AssignmentRequestData(Guid.NewGuid(), null, Guid.NewGuid(), "userAdd", "Delivered", null, null, null), CancellationToken.None);
        Assert.Null(unreadable.ObjectId);
        Assert.Contains("catalog reader", unreadable.Unknown);

        api.Stages = new List<ApprovalStageData>();
        var auto = await svc.ReadApproverAsync(new AssignmentRequestData(Guid.NewGuid(), null, Guid.NewGuid(), "userAdd", "Delivered", null, null, null), CancellationToken.None);
        Assert.Equal("the policy needed no approval", auto.Unknown);

        var none = await svc.ReadApproverAsync(null, CancellationToken.None);
        Assert.Null(none.ObjectId);
    }

    private sealed class FakeEntitlements : IEntitlementApi
    {
        public IReadOnlyList<ApprovalStageData>? Stages { get; set; }
        public Task<IReadOnlyList<AccessPackageAssignmentData>> AssignmentsAsync(Guid accessPackageId, CancellationToken ct) => Task.FromResult<IReadOnlyList<AccessPackageAssignmentData>>(Array.Empty<AccessPackageAssignmentData>());
        public Task<IReadOnlyList<AssignmentRequestData>> AssignmentRequestsAsync(Guid accessPackageId, CancellationToken ct) => Task.FromResult<IReadOnlyList<AssignmentRequestData>>(Array.Empty<AssignmentRequestData>());
        public Task<IReadOnlyList<ApprovalStageData>?> ApprovalStagesAsync(Guid assignmentRequestId, CancellationToken ct) => Task.FromResult(Stages);
    }
}
