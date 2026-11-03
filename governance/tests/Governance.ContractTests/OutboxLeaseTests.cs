using System.Collections.Concurrent;
using Dapper;
using Governance.Application;
using Governance.Application.Data;
using Governance.Application.Outbox;
using Governance.Infrastructure;
using Governance.Notifications;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Governance.ContractTests;

/// <summary>
/// The outbox processor on several nodes: the lease is renewed and checked before every row, attempts count failures
/// (not claims), and a failure notice names the error's type and a reference, never the error's text. Its own
/// throwaway database (the rows of other tests would be claimed too).
/// </summary>
public sealed class OutboxLeaseTests(DeliveryDatabase fx) : IClassFixture<DeliveryDatabase>
{
    /// <summary>Records which node handled which row; "TEST.Gate" on node-A waits for the test, "TEST.Fail" throws.</summary>
    private sealed class GateHandler : IOutboxHandler
    {
        public ConcurrentBag<(string Node, long Id)> Handled { get; } = new();
        public TaskCompletionSource FirstStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _gated;

        public bool CanHandle(string eventType) => eventType.StartsWith("TEST.", StringComparison.Ordinal);

        public async Task HandleAsync(OutboxRow row, OutboxMessage message, CancellationToken ct)
        {
            if (row.EventType == "TEST.Fail") throw new InvalidOperationException("Login failed for user 'CORP\\svc-secret' on Server=secret-sql01.");
            Handled.Add((row.LockedBy!, row.OutboxId));
            if (row.LockedBy == "node-A" && Interlocked.Increment(ref _gated) == 1)
            {
                FirstStarted.TrySetResult();
                await Release.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
            }
        }
    }

    private ServiceProvider Build(GateHandler handler, int maxAttempts = 6)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Notifications:Mode"] = "Record",
            ["Notifications:MaxAttempts"] = maxAttempts.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Notifications:WorkbenchBaseUrl"] = "http://localhost:5450",
            ["Workbench:TenantSettingsPath"] = Path.Combine(Path.GetTempPath(), "no-tenant-settings.json"),
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ICurrentPrincipal>(SystemPrincipal.Instance);
        services.Configure<DbOptions>(o => o.ConnectionString = fx.WorkerConnectionString);
        services.AddGovernanceInfrastructure(config);
        services.AddGovernanceNotifications(config);
        services.AddSingleton<IOutboxHandler>(handler);
        return services.BuildServiceProvider();
    }

    private async Task<SqlConnection> AdminAsync()
    {
        var c = new SqlConnection(fx.AdminConnectionString);
        await c.OpenAsync();
        // Nothing left over from another test of this class is due.
        await c.ExecuteAsync("UPDATE ntf.Outbox SET ProcessedAtUtc = SYSUTCDATETIME() WHERE ProcessedAtUtc IS NULL AND DeadLetteredUtc IS NULL");
        return c;
    }

    [Fact]
    public async Task A_row_whose_lease_ran_out_behind_a_slow_one_is_left_to_the_node_that_claimed_it()
    {
        if (fx.Skip) return;
        await using var admin = await AdminAsync();
        var first = await admin.ExecuteScalarAsync<long>("INSERT ntf.Outbox (EventType, Payload, DedupeKey) OUTPUT inserted.OutboxId VALUES ('TEST.Gate', '{}', CONCAT('gate-1-', NEWID()))");
        var second = await admin.ExecuteScalarAsync<long>("INSERT ntf.Outbox (EventType, Payload, DedupeKey) OUTPUT inserted.OutboxId VALUES ('TEST.Gate', '{}', CONCAT('gate-2-', NEWID()))");
        var handler = new GateHandler();
        await using var sp = Build(handler);

        // Node A claims both rows and is slow on the first.
        using var scopeA = sp.CreateScope();
        var a = scopeA.ServiceProvider.GetRequiredService<OutboxProcessor>().RunOnceAsync("node-A", CancellationToken.None);
        await handler.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(2, await admin.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM ntf.Outbox WHERE OutboxId IN (@first, @second) AND LockedBy = 'node-A'", new { first, second }));

        // A's lease on the second row runs out while it waits; node B claims and handles it.
        await admin.ExecuteAsync("UPDATE ntf.Outbox SET LockedUntilUtc = DATEADD(SECOND, -1, SYSUTCDATETIME()) WHERE OutboxId = @second", new { second });
        using var scopeB = sp.CreateScope();
        Assert.Equal(1, await scopeB.ServiceProvider.GetRequiredService<OutboxProcessor>().RunOnceAsync("node-B", CancellationToken.None));

        // A finishes the first row, then finds it no longer holds the second and leaves it alone.
        handler.Release.SetResult();
        Assert.Equal(2, await a);
        Assert.Equal(1, handler.Handled.Count(h => h.Id == second));
        Assert.Contains(("node-B", second), handler.Handled);
        Assert.Contains(("node-A", first), handler.Handled);
        var rows = (await admin.QueryAsync<(long Id, int Attempts, DateTime? Processed)>(
            "SELECT OutboxId, Attempts, ProcessedAtUtc FROM ntf.Outbox WHERE OutboxId IN (@first, @second)", new { first, second })).ToList();
        Assert.All(rows, r => Assert.NotNull(r.Processed));
        Assert.All(rows, r => Assert.Equal(0, r.Attempts));   // claims are not attempts
    }

    [Fact]
    public async Task Attempts_count_failures_and_the_failure_notice_carries_a_reference_not_the_error()
    {
        if (fx.Skip) return;
        await using var admin = await AdminAsync();
        var failing = await admin.ExecuteScalarAsync<long>("INSERT ntf.Outbox (EventType, Payload, DedupeKey) OUTPUT inserted.OutboxId VALUES ('TEST.Fail', '{}', CONCAT('fail-', NEWID()))");
        var handler = new GateHandler();

        // A claim alone (the node stops before handling the row) costs no attempt.
        await using (var sp = Build(handler))
        using (var scope = sp.CreateScope())
            Assert.Single(await scope.ServiceProvider.GetRequiredService<OutboxProcessor>().ClaimAsync("node-Z", 10, CancellationToken.None));
        Assert.Equal(0, await admin.ExecuteScalarAsync<int>("SELECT Attempts FROM ntf.Outbox WHERE OutboxId = @failing", new { failing }));
        await admin.ExecuteAsync("UPDATE ntf.Outbox SET LockedUntilUtc = DATEADD(SECOND, -1, SYSUTCDATETIME()) WHERE OutboxId = @failing", new { failing });

        // One failure is one attempt; with one attempt allowed the row is set aside and administrators get N19.
        await using (var sp = Build(handler, maxAttempts: 1))
        using (var scope = sp.CreateScope())
            await scope.ServiceProvider.GetRequiredService<OutboxProcessor>().RunOnceAsync("node-A", CancellationToken.None);
        var row = await admin.QuerySingleAsync<(int Attempts, DateTime? Dead, string LastError)>(
            "SELECT Attempts, DeadLetteredUtc, LastError FROM ntf.Outbox WHERE OutboxId = @failing", new { failing });
        Assert.Equal(1, row.Attempts);
        Assert.NotNull(row.Dead);
        Assert.Matches("^InvalidOperationException, reference [0-9A-F]{6}: ", row.LastError);
        Assert.DoesNotContain("secret", row.LastError);
        var notice = await admin.ExecuteScalarAsync<string>("SELECT Payload FROM ntf.Outbox WHERE DedupeKey = @key", new { key = $"N19:dead:{failing}" });
        Assert.Contains("reference", notice);
        Assert.DoesNotContain("secret", notice);
    }

    private sealed class Env(string name) : Microsoft.Extensions.Hosting.IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "test";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    /// <summary>A production site or worker never looks above its folder for config\tenant.test.json (or appsettings.Local.json).</summary>
    [Fact]
    public void Local_test_files_are_looked_for_only_in_Development()
    {
        Assert.True(Governance.Infrastructure.ServiceCollectionExtensions.SearchForLocalFiles(new Env("Development")));
        Assert.True(Governance.Infrastructure.ServiceCollectionExtensions.SearchForLocalFiles(null));   // tests and tools, with no host
        Assert.False(Governance.Infrastructure.ServiceCollectionExtensions.SearchForLocalFiles(new Env("Production")));
        Assert.False(Governance.Infrastructure.ServiceCollectionExtensions.SearchForLocalFiles(new Env("Staging")));

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Tenant:TenantId"] = "from-settings" }).Build();
        Assert.Equal("from-settings", Governance.Infrastructure.ServiceCollectionExtensions.LoadTenantSettings(config, searchUpward: false).TenantId);

        // The host's environment decides: a production host's TenantSettings come from its settings alone.
        var services = new ServiceCollection();
        services.AddSingleton<Microsoft.Extensions.Hosting.IHostEnvironment>(new Env("Production"));
        services.AddSingleton<ICurrentPrincipal>(SystemPrincipal.Instance);
        services.AddGovernanceInfrastructure(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Notifications:Mode"] = "Record" }).Build());
        using var sp = services.BuildServiceProvider();
        Assert.Null(sp.GetRequiredService<Application.Integrations.TenantSettings>().TenantId);
    }

    [Fact]
    public void A_failure_note_keeps_our_own_messages_and_hides_others()
    {
        var own = FailureNote.For(NullLogger.Instance, new Application.Integrations.PermanentDeliveryException("The channel flow does not know queue X."), "test");
        Assert.Equal("The channel flow does not know queue X.", own);
        var other = FailureNote.For(NullLogger.Instance, new HttpRequestException("No such host is known. (secret-host.corp:443)"), "test");
        Assert.Matches("^HttpRequestException, reference [0-9A-F]{6}: ", other);
        Assert.DoesNotContain("secret", other);
    }
}
