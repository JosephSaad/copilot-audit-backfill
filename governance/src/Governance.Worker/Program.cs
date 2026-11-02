using Governance.Application;
using Governance.Application.Data;
using Governance.Application.Outbox;
using Governance.Infrastructure;
using Governance.Notifications;
using Governance.Worker;
using Governance.Worker.Jobs;
using Microsoft.Extensions.Options;

// Copilot Governance Workbench worker: a Windows Service in production, a console process locally.
//   Governance.Worker                          run every job on its schedule
//   Governance.Worker --once DirectorySync,SlaTimer   run these jobs once and exit (job names below)
//   Governance.Worker --drain                  deliver every due outbox row, then exit
//   --fake-now 2026-11-02T15:00:00Z            (Development only) run with the clock moved, for SLA tests
var cli = CommandLine.Parse(args);
// Content root is the executable's folder (a Windows Service starts in System32), so appsettings.json is always found.
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = cli.HostArgs, ContentRootPath = AppContext.BaseDirectory });
builder.Services.AddWindowsService(o => o.ServiceName = "CopilotGovernanceWorker");

// config\appsettings.Local.json (git-ignored, written by Deploy-Database.ps1) holds the local connection strings.
var local = builder.Configuration["Workbench:LocalSettingsPath"] ?? WorkerConfig.FindUpward("config", "appsettings.Local.json");
if (local is not null) builder.Configuration.AddJsonFile(local, optional: true, reloadOnChange: false);
builder.Configuration.AddEnvironmentVariables("CGW_");

builder.Services.Configure<DbOptions>(o =>
{
    o.ConnectionString = builder.Configuration.GetConnectionString("GovernanceWorker") ?? builder.Configuration.GetConnectionString("Governance") ?? "";
});
builder.Services.Configure<WorkerOptions>(builder.Configuration.GetSection("Worker"));
builder.Services.AddSingleton<ICurrentPrincipal>(SystemPrincipal.Instance);
builder.Services.AddGovernanceInfrastructure(builder.Configuration);
builder.Services.AddGovernanceNotifications(builder.Configuration);
builder.Services.AddScoped<IOutboxHandler, ItsmOutboxHandler>();
if (builder.Configuration.GetValue<bool>("Testing:NoopOutboxHandler")) builder.Services.AddScoped<IOutboxHandler, NoopTestHandler>();

if (cli.FakeNow is DateTime fake)
{
    if (!builder.Environment.IsDevelopment()) throw new InvalidOperationException("--fake-now is allowed only in the Development environment.");
    builder.Services.AddSingleton<IClock>(new OffsetClock(fake - DateTime.UtcNow));
}

var jobs = new[] { typeof(OutboxDispatcherJob), typeof(SlaTimerJob), typeof(DirectorySyncJob), typeof(EntitlementSyncJob), typeof(AccessReviewImportJob),
                   typeof(LicensingReadJob), typeof(SeatSyncJob), typeof(ItsmPollerJob), typeof(CampaignSchedulerJob), typeof(DigestJob) };
foreach (var j in jobs) builder.Services.AddSingleton(j);
if (cli.Once is null && !cli.Drain)
    foreach (var j in jobs) builder.Services.AddSingleton(typeof(IHostedService), sp => sp.GetRequiredService(j));

var host = builder.Build();
if (string.IsNullOrWhiteSpace(host.Services.GetRequiredService<IOptions<DbOptions>>().Value.ConnectionString))
{
    Console.Error.WriteLine("No connection string: set ConnectionStrings:GovernanceWorker (config\\appsettings.Local.json or CGW_ConnectionStrings__GovernanceWorker).");
    return 2;
}

if (cli.Drain)
{
    await using var scope = host.Services.CreateAsyncScope();
    var processor = scope.ServiceProvider.GetRequiredService<OutboxProcessor>();
    var node = host.Services.GetRequiredService<IOptions<WorkerOptions>>().Value.Node;
    int n, total = 0;
    while ((n = await processor.RunOnceAsync(node, CancellationToken.None)) > 0) total += n;
    Console.WriteLine($"DRAINED {total} on {node}");
    return 0;
}
if (cli.Once is not null)
{
    var exit = 0;
    foreach (var name in cli.Once)
    {
        var type = jobs.FirstOrDefault(t => string.Equals(((JobBase)host.Services.GetRequiredService(t)).Name, name, StringComparison.OrdinalIgnoreCase));
        if (type is null) { Console.Error.WriteLine($"Unknown job {name}. Jobs: {string.Join(", ", jobs.Select(t => ((JobBase)host.Services.GetRequiredService(t)).Name))}"); exit = 2; continue; }
        var job = (JobBase)host.Services.GetRequiredService(type);
        var ran = await job.RunGuardedAsync(CancellationToken.None);
        Console.WriteLine($"{job.Name}: {(ran ? "ran" : "skipped (another node holds the lock)")}");
    }
    Console.WriteLine("ONCE DONE");
    return exit;
}
await host.RunAsync();
return 0;

namespace Governance.Worker
{
    internal sealed record CommandLine(string[] HostArgs, string[]? Once, bool Drain, DateTime? FakeNow)
    {
        public static CommandLine Parse(string[] args)
        {
            var rest = new List<string>();
            string[]? once = null;
            var drain = false;
            DateTime? fake = null;
            for (var i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--once" when i + 1 < args.Length: once = args[++i].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries); break;
                    case "--drain": drain = true; break;
                    case "--fake-now" when i + 1 < args.Length:
                        fake = DateTime.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal);
                        break;
                    default: rest.Add(args[i]); break;
                }
            }
            return new CommandLine(rest.ToArray(), once, drain, fake);
        }
    }

    internal static class WorkerConfig
    {
        public static string? FindUpward(string folder, string file)
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, folder, file);
                if (File.Exists(candidate)) return candidate;
            }
            return null;
        }
    }

    /// <summary>The system clock moved by a fixed offset (Development only, for SLA tests).</summary>
    public sealed class OffsetClock(TimeSpan offset) : IClock
    {
        public DateTime UtcNow => DateTime.UtcNow + offset;
    }

    /// <summary>Test-only handler: records which node processed each TEST.Noop row, to prove rows are never processed twice.</summary>
    public sealed class NoopTestHandler(Db db, IOptions<WorkerOptions> worker) : IOutboxHandler
    {
        public bool CanHandle(string eventType) => eventType == "TEST.Noop";
        public Task HandleAsync(OutboxRow row, OutboxMessage message, CancellationToken ct)
            => db.InTransactionAsync(u => u.ExecuteAsync(
                "INSERT ntf.Delivery (OutboxId, Channel, DedupeKey, Status, Detail) VALUES (@OutboxId, 'TEST', @key, 'RECORDED', @node)",
                new { row.OutboxId, key = $"TEST|{row.OutboxId}|{worker.Value.Node}", node = worker.Value.Node }), ct);
    }
}
