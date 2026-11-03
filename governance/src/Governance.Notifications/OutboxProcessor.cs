using Governance.Application;
using Governance.Application.Data;
using Governance.Application.Integrations;
using Governance.Application.Outbox;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Governance.Notifications;

/// <summary>A claimed outbox row. LockedBy is the node that holds the lease; deliveries re-check it before each send.</summary>
public sealed record OutboxRow(long OutboxId, string EventType, string Payload, string DedupeKey, int Attempts, string? LockedBy = null);

/// <summary>Another node took the row over (this node's lease expired): stop without touching the row.</summary>
public sealed class LeaseLostException(long outboxId) : Exception($"Outbox row {outboxId} is now held by another node.");

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
    /// <summary>Rows claimed at a time: few, so the last of them does not wait long behind slow deliveries.</summary>
    public const int DefaultBatch = 10;

    /// <summary>Claims due rows for this node with a 60-second lease. Attempts are counted when a row fails, not here.</summary>
    public async Task<IReadOnlyList<OutboxRow>> ClaimAsync(string node, int batch, CancellationToken ct)
        => (await db.InTransactionAsync(u => u.QueryAsync<OutboxRow>(
            """
            UPDATE TOP (@batch) o
            SET    LockedBy = @node,
                   LockedUntilUtc = DATEADD(SECOND, 60, SYSUTCDATETIME())
            OUTPUT inserted.OutboxId, inserted.EventType, inserted.Payload, inserted.DedupeKey, inserted.Attempts, inserted.LockedBy
            FROM   ntf.Outbox AS o WITH (ROWLOCK, READPAST, UPDLOCK)
            WHERE  o.ProcessedAtUtc IS NULL
              AND  o.DeadLetteredUtc IS NULL
              AND  o.AvailableAtUtc <= SYSUTCDATETIME()
              AND  (o.LockedUntilUtc IS NULL OR o.LockedUntilUtc < SYSUTCDATETIME());
            """, new { node, batch }), ct)).ToList();

    /// <summary>
    /// Claims and processes one batch. Returns how many rows were claimed. Before each row is handled, this node's lease
    /// on it is renewed and checked: a row whose lease ran out while earlier rows were handled, and that another node
    /// claimed meanwhile, is left to that node.
    /// </summary>
    public async Task<int> RunOnceAsync(string node, CancellationToken ct, int batch = DefaultBatch)
    {
        var rows = await ClaimAsync(node, batch, ct);
        foreach (var row in rows)
        {
            if (ct.IsCancellationRequested) break;
            if (!await RenewLeaseAsync(node, row.OutboxId, ct))
            {
                log.LogInformation("Outbox row {Id} ({Type}) is now held by another node; skipping it here.", row.OutboxId, row.EventType);
                continue;
            }
            await ProcessAsync(node, row, ct);
        }
        return rows.Count;
    }

    /// <summary>Extends this node's lease on the row by 60 seconds; false when the node no longer holds it (or the row is done).</summary>
    public Task<bool> RenewLeaseAsync(string node, long outboxId, CancellationToken ct)
        => db.InTransactionAsync(async u => await u.ExecuteAsync(
            """
            UPDATE ntf.Outbox SET LockedUntilUtc = DATEADD(SECOND, 60, SYSUTCDATETIME())
            WHERE OutboxId = @outboxId AND LockedBy = @node AND ProcessedAtUtc IS NULL AND DeadLetteredUtc IS NULL
            """, new { outboxId, node }) == 1, ct);

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
        catch (LeaseLostException)
        {
            // The lease ran out during a slow delivery and another node claimed the row: it finishes the work.
            log.LogInformation("Outbox row {Id} ({Type}) was taken over by another node; stopping here.", row.OutboxId, row.EventType);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // Attempts count failures, not claims: a row claimed but never handled (lease lost, shutdown) loses none.
            var attempts = row.Attempts + 1;
            var permanent = ex is PermanentDeliveryException;
            var dead = permanent || attempts >= options.Value.MaxAttempts;
            // The row and the notice say what kind of failure it was, with a reference; the error itself, which can name
            // servers and accounts, goes to the worker's log under that reference.
            var note = FailureNote.For(log, ex, $"Outbox row {row.OutboxId} ({row.EventType}) failed on attempt {attempts}");
            await db.InTransactionAsync(async u =>
            {
                if (dead)
                {
                    await u.ExecuteAsync("UPDATE ntf.Outbox SET DeadLetteredUtc = SYSUTCDATETIME(), Attempts = @attempts, LockedBy = NULL, LockedUntilUtc = NULL, LastError = @note WHERE OutboxId = @OutboxId AND LockedBy = @node",
                        new { row.OutboxId, attempts, note = Short(note), node });
                    if (row.EventType != "N19")
                        await outbox.EnqueueAsync(u, new OutboxMessage
                        {
                            EventType = "N19", Queues = { "OPERATIONS" },
                            Roles = { new RoleTarget(Domain.Roles.WorkbenchAdmin, Domain.ScopeTypes.Tenant, null), new RoleTarget(Domain.Roles.PlatformOperator, Domain.ScopeTypes.Tenant, null) },
                            Subject = $"Delivery failed: {row.EventType}", Text = $"An outbox item ({row.EventType}) failed {attempts} times and was set aside. Last error: {Short(note)}",
                            LinkPath = "/workbench/Admin/Integrations",
                        }, $"N19:dead:{row.OutboxId}");
                }
                else
                {
                    var delay = Backoff(attempts);
                    await u.ExecuteAsync("UPDATE ntf.Outbox SET AvailableAtUtc = @next, Attempts = @attempts, LockedBy = NULL, LockedUntilUtc = NULL, LastError = @note WHERE OutboxId = @OutboxId AND LockedBy = @node",
                        new { row.OutboxId, attempts, next = clock.UtcNow + delay, note = Short(note), node });
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

/// <summary>
/// What a failure notice (N19), an outbox row and a delivery record say about an error: our own messages (permission
/// refused, permanent delivery problems, rule refusals) whole; anything else only as its type and a six-character
/// reference. The error itself goes to the worker's log under that reference, since it can name servers, databases
/// and accounts.
/// </summary>
public static class FailureNote
{
    /// <summary>Six letters and digits, unlikely to repeat within a day.</summary>
    public static string NewReference() => Convert.ToHexString(Guid.NewGuid().ToByteArray(), 0, 3);

    /// <summary>Logs the error (under a new reference when it is not one of ours) and returns the note to show.</summary>
    public static string For(ILogger log, Exception ex, string what)
    {
        if (IsOwn(ex))
        {
            log.LogWarning("{What}: {Note}", what, ex.Message);
            return ex.Message;
        }
        var reference = NewReference();
        log.LogWarning(ex, "{What}. Reference {Reference}.", what, reference);
        return Sentence(ex, reference);
    }

    public static bool IsOwn(Exception ex) => ex is PermanentDeliveryException or IntegrationPermissionException or DomainException;

    /// <summary>The note for an unexpected error. Pure, for the tests.</summary>
    public static string Sentence(Exception ex, string reference) => $"{ex.GetType().Name}, reference {reference}: the details are in the worker's log under it.";
}
