using Governance.Application;
using Governance.Application.Data;
using Governance.Application.Integrations;
using Governance.Application.Outbox;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Governance.Notifications;

public sealed record OutboxRow(long OutboxId, string EventType, string Payload, string DedupeKey, int Attempts);

/// <summary>Handles one kind of outbox row (notices, ITSM commands...). The first handler that accepts the event type wins.</summary>
public interface IOutboxHandler
{
    bool CanHandle(string eventType);
    Task HandleAsync(OutboxRow row, OutboxMessage message, CancellationToken ct);
}

/// <summary>
/// The outbox dispatcher's core (section 8.1). Claims rows with READPAST so several worker nodes share the work
/// without double-processing, runs the handler outside any database transaction, then marks the row processed or
/// schedules a retry with exponential backoff and jitter. After the configured attempts the row is dead-lettered
/// and administrators get N19.
/// </summary>
public sealed class OutboxProcessor(Db db, IEnumerable<IOutboxHandler> handlers, IClock clock, IOptions<DispatchOptions> options, OutboxWriter outbox, ILogger<OutboxProcessor> log)
{
    public async Task<IReadOnlyList<OutboxRow>> ClaimAsync(string node, int batch, CancellationToken ct)
        => (await db.InTransactionAsync(u => u.QueryAsync<OutboxRow>(
            """
            UPDATE TOP (@batch) o
            SET    LockedBy = @node,
                   LockedUntilUtc = DATEADD(SECOND, 60, SYSUTCDATETIME()),
                   Attempts = Attempts + 1
            OUTPUT inserted.OutboxId, inserted.EventType, inserted.Payload, inserted.DedupeKey, inserted.Attempts
            FROM   ntf.Outbox AS o WITH (ROWLOCK, READPAST, UPDLOCK)
            WHERE  o.ProcessedAtUtc IS NULL
              AND  o.DeadLetteredUtc IS NULL
              AND  o.AvailableAtUtc <= SYSUTCDATETIME()
              AND  (o.LockedUntilUtc IS NULL OR o.LockedUntilUtc < SYSUTCDATETIME());
            """, new { node, batch }), ct)).ToList();

    /// <summary>Claims and processes one batch. Returns how many rows were claimed.</summary>
    public async Task<int> RunOnceAsync(string node, CancellationToken ct, int batch = 50)
    {
        var rows = await ClaimAsync(node, batch, ct);
        foreach (var row in rows)
        {
            if (ct.IsCancellationRequested) break;
            await ProcessAsync(node, row, ct);
        }
        return rows.Count;
    }

    private async Task ProcessAsync(string node, OutboxRow row, CancellationToken ct)
    {
        var handler = handlers.FirstOrDefault(h => h.CanHandle(row.EventType));
        try
        {
            if (handler is null) throw new PermanentDeliveryException($"No handler for {row.EventType}.");
            await handler.HandleAsync(row, OutboxMessage.FromJson(row.Payload), ct);
            await db.InTransactionAsync(u => u.ExecuteAsync(
                "UPDATE ntf.Outbox SET ProcessedAtUtc = SYSUTCDATETIME(), LockedBy = NULL, LockedUntilUtc = NULL, LastError = NULL WHERE OutboxId = @OutboxId AND LockedBy = @node",
                new { row.OutboxId, node }), CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            var permanent = ex is PermanentDeliveryException;
            var dead = permanent || row.Attempts >= options.Value.MaxAttempts;
            var note = ex is PermanentDeliveryException or IntegrationPermissionException or DomainException ? ex.Message : $"{ex.GetType().Name}: {Short(ex.Message)}";
            log.LogWarning("Outbox row {Id} ({Type}) failed on attempt {Attempt}: {Note}", row.OutboxId, row.EventType, row.Attempts, note);
            await db.InTransactionAsync(async u =>
            {
                if (dead)
                {
                    await u.ExecuteAsync("UPDATE ntf.Outbox SET DeadLetteredUtc = SYSUTCDATETIME(), LockedBy = NULL, LockedUntilUtc = NULL, LastError = @note WHERE OutboxId = @OutboxId",
                        new { row.OutboxId, note = Short(note) });
                    if (row.EventType != "N19")
                        await outbox.EnqueueAsync(u, new OutboxMessage
                        {
                            EventType = "N19", Queues = { "OPERATIONS" },
                            Roles = { new RoleTarget(Domain.Roles.WorkbenchAdmin, Domain.ScopeTypes.Tenant, null), new RoleTarget(Domain.Roles.PlatformOperator, Domain.ScopeTypes.Tenant, null) },
                            Subject = $"Delivery failed: {row.EventType}", Text = $"An outbox item ({row.EventType}) failed {row.Attempts} times and was set aside. Last error: {Short(note)}",
                            LinkPath = "/Admin/Integrations",
                        }, $"N19:dead:{row.OutboxId}");
                }
                else
                {
                    var delay = Backoff(row.Attempts);
                    await u.ExecuteAsync("UPDATE ntf.Outbox SET AvailableAtUtc = @next, LockedBy = NULL, LockedUntilUtc = NULL, LastError = @note WHERE OutboxId = @OutboxId",
                        new { row.OutboxId, next = clock.UtcNow + delay, note = Short(note) });
                }
            }, CancellationToken.None);
        }
    }

    /// <summary>Exponential backoff with jitter: 30 s, 1 min, 2 min ... capped at one hour, each within plus or minus 20 percent.</summary>
    public static TimeSpan Backoff(int attempts)
    {
        var seconds = Math.Min(3600, 30 * Math.Pow(2, Math.Max(0, attempts - 1)));
        return TimeSpan.FromSeconds(seconds * (0.8 + Random.Shared.NextDouble() * 0.4));
    }

    private static string Short(string s) => s.Length <= 900 ? s : s[..900];
}
