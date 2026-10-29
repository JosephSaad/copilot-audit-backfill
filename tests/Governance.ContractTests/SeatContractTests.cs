using System.Net;
using Governance.Application.Integrations;

namespace Governance.ContractTests;

/// <summary>X13 Graph reads against recorded responses: subscribed SKUs, seat groups, license states and the Copilot usage report.</summary>
public class SeatContractTests
{
    private static readonly Guid CopilotSku = Guid.Parse("639dec6b-bb19-468b-871c-c5c441c4b0cb");
    private static readonly Guid LicenseGroup = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");

    [Fact]
    public async Task Subscribed_skus_parse_purchased_and_assigned_units()
    {
        var h = new ReplayHandler().On(r => r.RequestUri!.AbsolutePath.EndsWith("/subscribedSkus"), "graph-subscribed-skus.json");
        var skus = await Clients.Graph(h).SubscribedSkusAsync(default);
        var copilot = Assert.Single(skus, s => s.SkuId == CopilotSku);
        Assert.Equal(("Microsoft_365_Copilot", 1000, 50, 0, 912), (copilot.SkuPartNumber, copilot.Enabled, copilot.Warning, copilot.Suspended, copilot.Consumed));
        Assert.Equal(2, skus.Count);
    }

    [Fact]
    public async Task Group_found_by_exact_name_with_its_licenses_and_sync_source()
    {
        var h = new ReplayHandler().On(r => r.RequestUri!.AbsolutePath.EndsWith("/groups"), "graph-seat-group.json");
        var g = await Clients.Graph(h).FindGroupAsync("GSGu_MCO_CopilotM365", default);
        Assert.NotNull(g);
        Assert.Equal(LicenseGroup, g!.Id);
        Assert.True(g.OnPremisesSyncEnabled);
        Assert.Equal(new[] { CopilotSku }, g.LicensedSkuIds);
        Assert.False(g.DuplicateName);
        Assert.Contains("displayName eq 'GSGu_MCO_CopilotM365'", Uri.UnescapeDataString(h.Requests[0].Url));

        var none = new ReplayHandler().OnStatus(_ => true, HttpStatusCode.OK, "{\"value\":[]}");
        Assert.Null(await Clients.Graph(none).FindGroupAsync("Missing", default));

        var gone = new ReplayHandler().OnStatus(_ => true, HttpStatusCode.NotFound, "{\"error\":{\"code\":\"Request_ResourceNotFound\"}}");
        Assert.Null(await Clients.Graph(gone).GetGroupAsync(LicenseGroup, default));
    }

    [Fact]
    public async Task Group_members_count_nested_groups_and_ignore_devices()
    {
        var h = new ReplayHandler().On(r => r.RequestUri!.AbsolutePath.Contains($"/groups/{LicenseGroup}/members"), "graph-seat-group-members.json");
        var m = await Clients.Graph(h).GroupMembersAsync(LicenseGroup, default);
        Assert.Equal(2, m.UserIds.Count);
        Assert.Equal(1, m.NestedGroups);
    }

    [Fact]
    public async Task Users_with_the_sku_page_with_eventual_consistency_and_parse_states()
    {
        var h = new ReplayHandler()
            .On(r => r.RequestUri!.Query.Contains("skiptoken=page2") && r.Headers.Contains("ConsistencyLevel"), "graph-users-with-sku-page2.json")
            .On(r => r.RequestUri!.AbsolutePath.EndsWith("/users") && r.Headers.Contains("ConsistencyLevel"), "graph-users-with-sku-page1.json");
        var users = await Clients.Graph(h).UsersWithSkuAsync(CopilotSku, default);
        Assert.Equal(3, users.Count);
        Assert.Contains($"assignedLicenses/any(x:x/skuId eq {CopilotSku})", Uri.UnescapeDataString(h.Requests[0].Url));

        var avery = users[0];
        Assert.Equal("CA", avery.UsageLocation);
        var seat = Assert.Single(avery.States, s => s.SkuId == CopilotSku);
        Assert.Equal(LicenseGroup, seat.AssignedByGroup);
        Assert.Null(seat.Error);   // "None" means no error

        var devon = users[1];
        Assert.Equal(("Error", "CountViolation"), (devon.States[0].State, devon.States[0].Error));
        Assert.Null(devon.UsageLocation);

        Assert.Null(users[2].States[0].AssignedByGroup);   // direct assignment

        // Three users, one with an error: holder rows keep the group and the direct source apart.
        var rows = SeatSyncService.HolderRows(users, CopilotSku).ToList();
        Assert.Equal(3, rows.Count);
        Assert.Single(rows, r => r.AssignedByGroupId == Guid.Empty);
    }

    [Fact]
    public async Task Usage_report_reads_the_copilot_path_first()
    {
        var h = new ReplayHandler().On(r => r.RequestUri!.AbsolutePath.Contains("/copilot/reports/getMicrosoft365CopilotUsageUserDetail"), "graph-copilot-usage.json");
        var usage = await Clients.Graph(h).CopilotUsageAsync("D90", default);
        Assert.False(usage.NamesConcealed);
        Assert.Equal(new DateTime(2026, 10, 27), usage.ReportRefreshDate);
        Assert.Equal(2, usage.Users.Count);
        Assert.Equal(new DateTime(2026, 10, 26), usage.Users[0].LastActivityDate);
        Assert.Null(usage.Users[1].LastActivityDate);
        Assert.Contains("period='D90'", Uri.UnescapeDataString(h.Requests[0].Url));
    }

    [Fact]
    public async Task Usage_report_falls_back_to_beta_when_the_copilot_path_is_missing()
    {
        var h = new ReplayHandler()
            .OnStatus(r => r.RequestUri!.AbsolutePath.Contains("/copilot/reports/"), HttpStatusCode.NotFound, "{\"error\":{\"code\":\"BadRequest\"}}")
            .On(r => r.RequestUri!.AbsolutePath.StartsWith("/beta/reports/"), "graph-copilot-usage.json");
        var usage = await Clients.Graph(h).CopilotUsageAsync("D90", default);
        Assert.Equal(2, usage.Users.Count);
        Assert.Contains("$format=application/json", Uri.UnescapeDataString(h.Requests[^1].Url));
    }

    [Fact]
    public async Task Concealed_names_are_reported_and_not_kept()
    {
        var h = new ReplayHandler().On(_ => true, "graph-copilot-usage-concealed.json");
        var usage = await Clients.Graph(h).CopilotUsageAsync("D90", default);
        Assert.True(usage.NamesConcealed);
        Assert.Empty(usage.Users);
    }

    [Fact]
    public async Task Refused_reports_raise_a_permission_error_the_service_turns_into_a_note()
    {
        var refused = new ReplayHandler().OnStatus(_ => true, HttpStatusCode.Forbidden);
        var ex = await Assert.ThrowsAsync<IntegrationPermissionException>(() => Clients.Graph(refused).CopilotUsageAsync("D90", default));
        Assert.Equal(403, ex.Status);
        await Assert.ThrowsAsync<IntegrationPermissionException>(() => Clients.Graph(refused).SubscribedSkusAsync(default));
    }
}
