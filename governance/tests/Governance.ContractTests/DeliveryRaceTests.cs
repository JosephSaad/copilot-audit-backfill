using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Dapper;
using Governance.Application;
using Governance.Application.Data;
using Governance.Application.Integrations;
using Governance.Application.Outbox;
using Governance.Infrastructure;
using Governance.Notifications;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Governance.ContractTests;

/// <summary>
/// A throwaway database (the real Deploy-Database.ps1 with SQL logins), dropped afterwards. Needs a local SQL Server
/// with Windows authentication; set CGW_SKIP_SQL=1 to skip where there is none.
/// </summary>
public sealed class DeliveryDatabase : IAsyncLifetime
{
    public string Server { get; } = Environment.GetEnvironmentVariable("CGW_IT_SERVER") ?? "localhost";
    public string Database { get; } = "CopilotGovCt_" + Guid.NewGuid().ToString("n")[..10];
    public string AdminConnectionString => $"Server={Server};Database={Database};Integrated Security=True;TrustServerCertificate=True;Encrypt=True;Pooling=False";
    public string WorkerConnectionString { get; private set; } = "";
    public bool Skip { get; } = Environment.GetEnvironmentVariable("CGW_SKIP_SQL") == "1";
    private string _settings = "";

    public async Task InitializeAsync()
    {
        if (Skip) return;
        _settings = Path.Combine(Path.GetTempPath(), $"{Database}.json");
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "CopilotGovernance.sln"))) root = root.Parent;
        var psi = new ProcessStartInfo("pwsh") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in new[] { "-NoProfile", "-File", Path.Combine(root!.FullName, "src", "Governance.Database", "Deploy-Database.ps1"), "-SqlServer", Server,
                     "-Database", Database, "-CreateSqlLogins", "-LocalSettingsPath", _settings, "-TrustServerCertificate" })
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(TimeSpan.FromMinutes(5))) { p.Kill(true); throw new TimeoutException("Deploy-Database.ps1 did not finish in 5 minutes."); }
        if (p.ExitCode != 0) throw new InvalidOperationException($"Deploy failed:\n{stdout.Result}\n{stderr.Result}");
        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(_settings));
        WorkerConnectionString = doc.RootElement.GetProperty("ConnectionStrings").GetProperty("GovernanceWorker").GetString()! + ";Pooling=False";
    }

    public async Task DisposeAsync()
    {
        if (Skip) return;
        SqlConnection.ClearAllPools();
        await using var c = new SqlConnection($"Server={Server};Database=master;Integrated Security=True;TrustServerCertificate=True;Encrypt=True;Pooling=False");
        await c.OpenAsync();
        var suffix = Database.ToLowerInvariant();
        await c.ExecuteAsync($"""
            IF DB_ID(N'{Database}') IS NOT NULL BEGIN ALTER DATABASE [{Database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{Database}]; END
            IF SUSER_ID(N'cgw_web_{suffix}') IS NOT NULL DROP LOGIN [cgw_web_{suffix}];
            IF SUSER_ID(N'cgw_worker_{suffix}') IS NOT NULL DROP LOGIN [cgw_worker_{suffix}];
            """);
        if (File.Exists(_settings)) File.Delete(_settings);
    }
}

public sealed class DeliveryRaceTests(DeliveryDatabase fx) : IClassFixture<DeliveryDatabase>
{
    /// <summary>Counts sends per address; the first send blocks until the test lets it go, to stretch a fan-out past its lease.</summary>
    private sealed class SlowMail : IMailSender
    {
        public ConcurrentDictionary<string, int> Sent { get; } = new();
        public TaskCompletionSource FirstSendStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFirstSend { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;

        public async Task SendAsync(MailMessageData message, CancellationToken ct)
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                FirstSendStarted.TrySetResult();
                await ReleaseFirstSend.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
            }
            foreach (var to in message.To) Sent.AddOrUpdate(to, 1, (_, n) => n + 1);
        }
    }

    private ServiceProvider Build(SlowMail mail)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Notifications:Mode"] = "Send",
            ["Notifications:WorkbenchBaseUrl"] = "http://localhost:5450",
            ["Workbench:TenantSettingsPath"] = Path.Combine(Path.GetTempPath(), "no-tenant-settings.json"),
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ICurrentPrincipal>(SystemPrincipal.Instance);
        services.Configure<DbOptions>(o => o.ConnectionString = fx.WorkerConnectionString);
        services.AddGovernanceInfrastructure(config);
        services.AddGovernanceNotifications(config);
        services.AddSingleton<IMailSender>(mail);   // last registration wins
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task Two_processors_on_the_same_row_never_send_twice_even_when_the_lease_runs_out()
    {
        if (fx.Skip) return;
        await using var admin = new SqlConnection(fx.AdminConnectionString);
        await admin.OpenAsync();
        var people = new List<int>();
        for (var i = 1; i <= 4; i++)
            people.Add(await admin.ExecuteScalarAsync<int>(
                "INSERT sec.Person (DisplayName, Mail, AccountEnabled, IsSynthetic) OUTPUT inserted.PersonId VALUES (@n, @m, 1, 1)",
                new { n = $"Recipient {i}", m = $"r{i}@contoso.example" }));
        var message = new OutboxMessage { EventType = "N04", PersonIds = people, Subject = "CCG-2026-000099: Routine approval", Text = "Please decide.", LinkPath = "/Requests/x" };
        var outboxId = await admin.ExecuteScalarAsync<long>(
            "INSERT ntf.Outbox (EventType, Payload, DedupeKey) OUTPUT inserted.OutboxId VALUES ('N04', @p, 'race-test')", new { p = message.ToJson() });

        var mail = new SlowMail();
        await using var sp = Build(mail);

        // Node A claims and starts the fan-out; its first send hangs.
        using var scopeA = sp.CreateScope();
        var a = scopeA.ServiceProvider.GetRequiredService<OutboxProcessor>().RunOnceAsync("node-A", CancellationToken.None);
        await mail.FirstSendStarted.Task.WaitAsync(TimeSpan.FromSeconds(30));

        // A's lease runs out; node B claims the same row and works it while A is still sending.
        await admin.ExecuteAsync("UPDATE ntf.Outbox SET LockedUntilUtc = DATEADD(SECOND, -1, SYSUTCDATETIME()) WHERE OutboxId = @outboxId", new { outboxId });
        using var scopeB = sp.CreateScope();
        Assert.Equal(1, await scopeB.ServiceProvider.GetRequiredService<OutboxProcessor>().RunOnceAsync("node-B", CancellationToken.None));

        // A's first send finishes; A finds it lost the lease and stops.
        mail.ReleaseFirstSend.SetResult();
        await a;

        // B left the row for a retry because A's delivery was still in progress: run it once more now.
        await admin.ExecuteAsync("UPDATE ntf.Outbox SET AvailableAtUtc = SYSUTCDATETIME() WHERE OutboxId = @outboxId AND ProcessedAtUtc IS NULL", new { outboxId });
        using var scopeC = sp.CreateScope();
        await scopeC.ServiceProvider.GetRequiredService<OutboxProcessor>().RunOnceAsync("node-C", CancellationToken.None);

        Assert.Equal(4, mail.Sent.Count);
        Assert.All(mail.Sent, kv => Assert.Equal(1, kv.Value));
        var statuses = (await admin.QueryAsync<string>("SELECT Status FROM ntf.Delivery WHERE OutboxId = @outboxId", new { outboxId })).ToList();
        Assert.Equal(4, statuses.Count);
        Assert.All(statuses, s => Assert.Equal("SENT", s));
        Assert.NotNull(await admin.ExecuteScalarAsync<DateTime?>("SELECT ProcessedAtUtc FROM ntf.Outbox WHERE OutboxId = @outboxId", new { outboxId }));
    }

    [Fact]
    public async Task A_reservation_is_refused_once_another_node_holds_the_lease()
    {
        if (fx.Skip) return;
        await using var admin = new SqlConnection(fx.AdminConnectionString);
        await admin.OpenAsync();
        var outboxId = await admin.ExecuteScalarAsync<long>(
            "INSERT ntf.Outbox (EventType, Payload, DedupeKey, LockedBy, LockedUntilUtc) OUTPUT inserted.OutboxId VALUES ('N04', '{}', 'lease-test', 'node-B', DATEADD(MINUTE, 1, SYSUTCDATETIME()))");
        await using var sp = Build(new SlowMail());
        using var scope = sp.CreateScope();
        var ledger = scope.ServiceProvider.GetRequiredService<DeliveryLedger>();
        var asA = new OutboxRow(outboxId, "N04", "{}", "lease-test", 1, "node-A");
        await Assert.ThrowsAsync<LeaseLostException>(() => ledger.ReserveAsync(asA, 1, "x@contoso.example", "EMAIL", "s", CancellationToken.None));
        var asB = asA with { LockedBy = "node-B" };
        Assert.Equal(Reservation.Reserved, await ledger.ReserveAsync(asB, 1, "x@contoso.example", "EMAIL", "s", CancellationToken.None));
        Assert.Equal(Reservation.PendingElsewhere, await ledger.ReserveAsync(asB, 1, "x@contoso.example", "EMAIL", "s", CancellationToken.None));
        await ledger.RecordAsync(asB, 1, "x@contoso.example", "EMAIL", "SENT", "s", null);
        Assert.Equal(Reservation.Done, await ledger.ReserveAsync(asB, 1, "x@contoso.example", "EMAIL", "s", CancellationToken.None));
    }
}
