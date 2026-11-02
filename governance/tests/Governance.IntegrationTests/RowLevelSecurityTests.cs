using Dapper;
using Governance.Application.Requests;
using Governance.Domain;
using Microsoft.Data.SqlClient;

namespace Governance.IntegrationTests;

/// <summary>Row-level security as the web's SQL login sees it (session context set read-only on open).</summary>
[Collection("sql")]
public sealed class RowLevelSecurityTests(SeededFixture fx) : IClassFixture<SeededFixture>
{
    private async Task<SqlConnection> WebAs(int personId, bool central)
    {
        var c = new SqlConnection(fx.WebConnectionString);
        await c.OpenAsync();
        await c.ExecuteAsync("EXEC sp_set_session_context @key = N'PersonId', @value = @personId, @read_only = 1; EXEC sp_set_session_context @key = N'IsCentral', @value = @central, @read_only = 1;",
            new { personId, central });
        return c;
    }

    [Fact]
    public async Task An_lob_owner_sees_only_their_lobs_workloads()
    {
        await using var c = await WebAs(fx.Seed.Person("RETAIL.owner"), false);
        var fos = (await c.QueryAsync<int>("SELECT DISTINCT FundingOwnerId FROM core.Workload")).ToList();
        Assert.Equal(new[] { fx.Seed.FundingOwners["RETAIL"] }, fos);
        Assert.Equal(0, await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM core.FundingEnvelope WHERE FundingOwnerId <> @fo", new { fo = fx.Seed.FundingOwners["RETAIL"] }));
    }

    [Fact]
    public async Task A_delegated_approver_sees_the_funding_owner_they_approve_for()
    {
        await using var c = await WebAs(fx.Seed.Person("WEALTH.approver2"), false);
        Assert.Equal(new[] { fx.Seed.FundingOwners["WEALTH"] }, (await c.QueryAsync<int>("SELECT DISTINCT FundingOwnerId FROM core.Workload")).ToList());
    }

    [Fact]
    public async Task Central_scope_sees_every_workload()
    {
        await using var admin = fx.Admin();
        var all = await admin.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM core.Workload");
        await using var c = await WebAs(fx.Seed.Person("architect"), true);
        Assert.Equal(all, await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM core.Workload"));
        Assert.True(all >= 3);
    }

    [Fact]
    public async Task Without_session_context_the_web_login_sees_nothing()
    {
        await using var c = new SqlConnection(fx.WebConnectionString);
        await c.OpenAsync();
        Assert.Equal(0, await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM core.Workload"));
    }

    [Fact]
    public async Task A_requester_sees_their_own_request_in_another_lob_and_nothing_else()
    {
        var maker = "WEALTH.maker2";   // no role in COMMERCIAL
        var mine = await fx.Flows.DraftAsync(maker, Journeys.NewWorkload, "RLS own request", "COMMERCIAL", "CS_STANDARD", "COMMERCIAL.sponsor", null,
            new RequestPayload { Purpose = "RLS test" });
        var others = await fx.Flows.DraftAsync("COMMERCIAL.maker1", Journeys.NewWorkload, "RLS someone else's request", "COMMERCIAL", "CS_STANDARD", "COMMERCIAL.sponsor", null,
            new RequestPayload { Purpose = "RLS test" });
        await using var c = await WebAs(fx.Seed.Person(maker), false);
        var visible = (await c.QueryAsync<long>("SELECT RequestId FROM wf.Request")).ToList();
        Assert.Contains(mine.RequestId, visible);
        Assert.DoesNotContain(others.RequestId, visible);
        Assert.Equal(0, await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM core.Workload"));
    }

    [Fact]
    public async Task A_sponsor_sees_the_request_they_sponsor()
    {
        var r = await fx.Flows.DraftAsync("RETAIL.maker2", Journeys.NewWorkload, "RLS sponsored", "RETAIL", "CS_STANDARD", "COMMERCIAL.sponsor", null, new RequestPayload { Purpose = "x" });
        await using var c = await WebAs(fx.Seed.Person("COMMERCIAL.sponsor"), false);
        Assert.Contains(r.RequestId, await c.QueryAsync<long>("SELECT RequestId FROM wf.Request"));
    }

    [Fact]
    public async Task Session_context_cannot_be_changed_once_set()
    {
        await using var c = await WebAs(fx.Seed.Person("RETAIL.maker1"), false);
        var ex = await Assert.ThrowsAsync<SqlException>(() => c.ExecuteAsync("EXEC sp_set_session_context @key = N'IsCentral', @value = 1"));
        Assert.Contains("read_only", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM core.Workload"));
    }

    [Fact]
    public async Task An_lob_owner_never_sees_the_enterprise_shared_rows()
    {
        await using var c = await WebAs(fx.Seed.Person("RETAIL.owner"), false);
        Assert.Equal(1, await c.ExecuteScalarAsync<int>("SELECT IS_MEMBER('app_web')"));
        Assert.Equal(0, await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM core.Workload WHERE FundingOwnerId = @fo", new { fo = fx.Seed.FundingOwners["ES-IT"] }));
    }
}
