using Dapper;
using Governance.Application;
using Governance.Application.Data;
using Governance.Application.Integrations;
using Governance.Application.Outbox;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Governance.IntegrationTests;

/// <summary>3.0.1: one person list with Copilot Insights, joined by Entra object ID.</summary>
public sealed class PersonLinkTests(DatabaseFixture db) : IClassFixture<DatabaseFixture>
{
    private sealed class Clock : IClock { public DateTime UtcNow => DateTime.UtcNow; }

    private sealed class FakeInsights(IReadOnlyList<(string, Guid)>? ids) : IInsightsActivitySource
    {
        public Task<InsightsActivity> ReadAsync(CancellationToken ct) => Task.FromResult(new InsightsActivity(null, []));
        public Task<IReadOnlyList<(string Upn, Guid ObjectId)>> ObjectIdsAsync(CancellationToken ct) =>
            ids is null ? throw new InvalidOperationException("Insights database unavailable (test)") : Task.FromResult(ids);
    }

    private DirectorySyncService Build(IInsightsActivitySource? insights)
    {
        var appDb = new Db(Options.Create(new DbOptions { ConnectionString = db.WorkerConnectionString }), SystemPrincipal.Instance);
        return new DirectorySyncService(appDb, null!, new FindingWriter(new OutboxWriter(), new Clock()), new Clock(), new TenantSettings(), NullLogger<DirectorySyncService>.Instance, insights);
    }

    [Fact]
    public async Task A_person_without_an_object_ID_takes_the_one_Insights_records_unless_someone_has_it()
    {
        using var c = db.Admin();
        var taken = Guid.NewGuid();
        var free = Guid.NewGuid();
        var upn = $"link-{Guid.NewGuid():N}@contoso.example";
        var clash = $"clash-{Guid.NewGuid():N}@contoso.example";
        await c.ExecuteAsync("INSERT sec.Person (UserPrincipalName, DisplayName, AccountEnabled) VALUES (@upn, N'Link test', 1), (@clash, N'Clash test', 1)", new { upn, clash });
        await c.ExecuteAsync("INSERT sec.Person (EntraObjectId, UserPrincipalName, DisplayName, AccountEnabled) VALUES (@taken, N'other@contoso.example', N'Holder', 1)", new { taken });

        var linked = await Build(new FakeInsights([(upn, free), (clash, taken)])).LinkInsightsPeopleAsync(default);

        Assert.Equal(1, linked);
        Assert.Equal(free, await c.ExecuteScalarAsync<Guid?>("SELECT EntraObjectId FROM sec.Person WHERE UserPrincipalName = @upn", new { upn }));
        Assert.Null(await c.ExecuteScalarAsync<Guid?>("SELECT EntraObjectId FROM sec.Person WHERE UserPrincipalName = @clash", new { clash }));   // someone already has that ID
        Assert.Equal(0, await Build(null).LinkInsightsPeopleAsync(default));                                    // no Insights database: nothing
        Assert.Equal(0, await Build(new FakeInsights(null)).LinkInsightsPeopleAsync(default));                  // Insights does not answer: nothing, no failure
    }
}
