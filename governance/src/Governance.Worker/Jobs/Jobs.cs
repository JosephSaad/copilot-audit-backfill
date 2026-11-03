using System.Text.Json;
using Governance.Application;
using Governance.Application.Data;
using Governance.Application.Integrations;
using Governance.Application.Outbox;
using Governance.Application.Workflow;
using Governance.Notifications;
using Microsoft.Extensions.Options;

namespace Governance.Worker.Jobs;

/// <summary>Continuous, every node (row claiming shares the work): delivers notices and integration commands.</summary>
public sealed class OutboxDispatcherJob(IServiceScopeFactory scopes, IOptions<DbOptions> db, IOptions<WorkerOptions> worker, ILogger<OutboxDispatcherJob> log)
    : JobBase(scopes, db, worker, log)
{
    public override string Name => "OutboxDispatcher";
    public override TimeSpan Interval => TimeSpan.FromSeconds(5);
    public override bool Singleton => false;
    public override TimeSpan InitialDelay => TimeSpan.FromSeconds(1);

    protected override async Task<string?> RunAsync(IServiceProvider services, CancellationToken ct)
    {
        var processor = services.GetRequiredService<OutboxProcessor>();
        var total = 0;
        int n;
        // Drain what is due, then wait for the next poll.
        while ((n = await processor.RunOnceAsync(Options.Node, ct)) > 0 && !ct.IsCancellationRequested) total += n;
        return $"{total} outbox rows";
    }
}

public sealed class DirectorySyncJob(IServiceScopeFactory scopes, IOptions<DbOptions> db, IOptions<WorkerOptions> worker, ILogger<DirectorySyncJob> log)
    : JobBase(scopes, db, worker, log)
{
    public override string Name => "DirectorySync";
    public override TimeSpan Interval => TimeSpan.FromMinutes(15);
    protected override async Task<string?> RunAsync(IServiceProvider s, CancellationToken ct) => $"Sync run {await s.GetRequiredService<DirectorySyncService>().RunAsync(ct)}";
}

public sealed class EntitlementSyncJob(IServiceScopeFactory scopes, IOptions<DbOptions> db, IOptions<WorkerOptions> worker, ILogger<EntitlementSyncJob> log)
    : JobBase(scopes, db, worker, log)
{
    public override string Name => "EntitlementSync";
    public override TimeSpan Interval => TimeSpan.FromMinutes(15);
    public override TimeSpan InitialDelay => TimeSpan.FromSeconds(40);   // after the first directory sync
    protected override async Task<string?> RunAsync(IServiceProvider s, CancellationToken ct) => $"Sync run {await s.GetRequiredService<EntitlementSyncService>().RunAsync(ct)}";
}

public sealed class AccessReviewImportJob(IServiceScopeFactory scopes, IOptions<DbOptions> db, IOptions<WorkerOptions> worker, ILogger<AccessReviewImportJob> log)
    : JobBase(scopes, db, worker, log)
{
    public override string Name => "AccessReviewImport";
    public override TimeSpan Interval => TimeSpan.FromHours(24);
    public override TimeSpan InitialDelay => TimeSpan.FromMinutes(2);
    protected override async Task<string?> RunAsync(IServiceProvider s, CancellationToken ct) => $"Sync run {await s.GetRequiredService<AccessReviewImportService>().RunAsync(ct)}";
}

/// <summary>Daily licensing read and drift evaluation. App-only only when the Worker holds the Power Platform reader role.</summary>
public sealed class LicensingReadJob(IServiceScopeFactory scopes, IOptions<DbOptions> db, IOptions<WorkerOptions> worker, ILogger<LicensingReadJob> log)
    : JobBase(scopes, db, worker, log)
{
    public override string Name => "LicensingRead";
    public override TimeSpan Interval => TimeSpan.FromHours(24);
    public override TimeSpan InitialDelay => TimeSpan.FromMinutes(3);
    protected override async Task<string?> RunAsync(IServiceProvider s, CancellationToken ct)
    {
        if (!s.GetRequiredService<TenantSettings>().PowerPlatformReader)
            return "Skipped: the Worker has no Power Platform reader role; operators run the licensing read from Operations > Sync.";
        return $"Sync run {await s.GetRequiredService<ILicensingRead>().RunAsync(null, null, ct)}";
    }
}

public sealed class DigestJob(IServiceScopeFactory scopes, IOptions<DbOptions> db, IOptions<WorkerOptions> worker, ILogger<DigestJob> log)
    : JobBase(scopes, db, worker, log)
{
    public override string Name => "DigestBuilder";
    public override TimeSpan Interval => TimeSpan.FromMinutes(15);
    protected override async Task<string?> RunAsync(IServiceProvider s, CancellationToken ct) => $"{await s.GetRequiredService<DigestBuilder>().RunAsync(ct)} digests";
}

/// <summary>
/// Every 5 minutes: reads the state of open ITSM tickets (<see cref="ItsmPollService"/>). A closed fulfillment ticket
/// moves its request to verification (never to Completed), with the configurer mapped from the ticket's "closed by"
/// (SoD3). A ticket that cannot be read is recorded on itself and the poll goes on with the next.
/// </summary>
public sealed class ItsmPollerJob(IServiceScopeFactory scopes, IOptions<DbOptions> db, IOptions<WorkerOptions> worker, ILogger<ItsmPollerJob> log)
    : JobBase(scopes, db, worker, log)
{
    public override string Name => "ItsmPoller";
    public override TimeSpan Interval => TimeSpan.FromMinutes(5);

    protected override async Task<string?> RunAsync(IServiceProvider s, CancellationToken ct)
        => (await s.GetRequiredService<ItsmPollService>().RunAsync(ct)).Message;
}

/// <summary>
/// Daily: removes the worker's own bookkeeping older than Retention.WorkerRecordDays (180 unless set) in small batches
/// (<see cref="RetentionService"/>). Ledger and audit tables are never touched.
/// </summary>
public sealed class RetentionJob(IServiceScopeFactory scopes, IOptions<DbOptions> db, IOptions<WorkerOptions> worker, ILogger<RetentionJob> log)
    : JobBase(scopes, db, worker, log)
{
    public override string Name => "Retention";
    public override TimeSpan Interval => TimeSpan.FromHours(24);
    public override TimeSpan InitialDelay => TimeSpan.FromMinutes(10);

    protected override async Task<string?> RunAsync(IServiceProvider s, CancellationToken ct)
        => (await s.GetRequiredService<RetentionService>().RunAsync(ct)).Message;
}

/// <summary>Outbox handler for ITSM.Create: searches by correlation ID first, so a retry never creates a second ticket (X8).</summary>
public sealed class ItsmOutboxHandler(Db db, IItsmClient itsm) : IOutboxHandler
{
    public bool CanHandle(string eventType) => eventType == "ITSM.Create";

    public async Task HandleAsync(OutboxRow row, OutboxMessage m, CancellationToken ct)
    {
        if (m.ItsmTicketId is not long id) throw new PermanentDeliveryException("ITSM.Create without a ticket ID.");
        var t = await db.InTransactionAsync(u => u.QuerySingleAsync<(Guid CorrelationId, string Kind, string? ExternalNumber, string Payload)>(
            "SELECT CorrelationId, Kind, ExternalNumber, Payload FROM wf.ItsmTicket WHERE ItsmTicketId = @id", new { id }), ct);
        if (t.ExternalNumber is not null) return;
        var found = await itsm.FindByCorrelationAsync(t.CorrelationId, ct);
        if (found is null)
        {
            string shortDescription = "Copilot Governance Workbench task";
            try
            {
                using var doc = JsonDocument.Parse(t.Payload);
                if (doc.RootElement.TryGetProperty("shortDescription", out var sd) && sd.GetString() is string s) shortDescription = s;
            }
            catch (JsonException) { }
            var pretty = JsonSerializer.Serialize(JsonDocument.Parse(t.Payload).RootElement, new JsonSerializerOptions { WriteIndented = true });
            found = await itsm.CreateAsync(new ItsmCreate(t.CorrelationId, t.Kind, shortDescription.Length > 160 ? shortDescription[..160] : shortDescription,
                pretty, "", t.Kind == "INCIDENT" ? "1" : "3", t.Payload), ct);
        }
        await db.InTransactionAsync(u => u.ExecuteAsync(
            "UPDATE wf.ItsmTicket SET ExternalNumber = @Number, ExternalUrl = @Url, State = CASE WHEN @State IN ('UNKNOWN','') THEN 'NEW' ELSE @State END WHERE ItsmTicketId = @id",
            new { found.Number, found.Url, found.State, id }), ct);
    }
}
