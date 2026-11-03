using Dapper;
using Governance.Application;
using Governance.Application.Data;
using Governance.Application.Integrations;
using Governance.Seed;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Governance.IntegrationTests;

/// <summary>
/// The worker's ITSM poll and its retention step against a throwaway database, as the worker's SQL login: one ticket
/// that cannot be read never stops the poll for the others, and the retention step removes old bookkeeping in batches
/// while keeping what the pages and the de-duplication still need.
/// </summary>
[Collection("sql")]
public sealed class WorkerHousekeepingTests(DatabaseFixture db) : IClassFixture<DatabaseFixture>
{
    private sealed class FakeItsm(Func<string, ItsmTicketState> get) : IItsmClient
    {
        public List<string> Asked { get; } = new();
        public Task<ItsmTicketRef?> FindByCorrelationAsync(Guid correlationId, CancellationToken ct) => throw new NotSupportedException();
        public Task<ItsmTicketRef> CreateAsync(ItsmCreate request, CancellationToken ct) => throw new NotSupportedException();
        public Task<ItsmTicketState> GetAsync(string externalNumber, CancellationToken ct) { Asked.Add(externalNumber); return Task.FromResult(get(externalNumber)); }
    }

    private Db WorkerDb() => new(Options.Create(new DbOptions { ConnectionString = db.WorkerConnectionString }), SystemPrincipal.Instance);

    private ItsmPollService Poller(IItsmClient itsm) => new(WorkerDb(), itsm, new EngineHost(db.AdminConnectionString).Engine, NullLogger<ItsmPollService>.Instance);

    [Fact]
    public async Task A_deleted_or_unreadable_ticket_is_recorded_and_the_poll_goes_on()
    {
        using var c = db.Admin();
        var suffix = Guid.NewGuid().ToString("n")[..6];
        long Ticket(string number) => c.ExecuteScalar<long>(
            "INSERT wf.ItsmTicket (Kind, CorrelationId, ExternalNumber, State, Payload) OUTPUT inserted.ItsmTicketId VALUES ('TASK', NEWID(), @number, 'NEW', N'{}')", new { number });
        var gone = Ticket($"SCTASK-GONE-{suffix}");
        var broken = Ticket($"SCTASK-BROKEN-{suffix}");
        var fine = Ticket($"SCTASK-FINE-{suffix}");

        var itsm = new FakeItsm(n => n.Contains("GONE") ? throw new ItsmTicketNotFoundException(n)
                                   : n.Contains("BROKEN") ? throw new HttpRequestException("Connection refused (itsm-secret.corp:443)")
                                   : new ItsmTicketState(n, "IN_PROGRESS", null, null, null));
        var result = await Poller(itsm).RunAsync(default);

        // The tickets after the missing and the failing one were still read.
        Assert.Contains($"SCTASK-FINE-{suffix}", itsm.Asked);
        Assert.Equal((1, 1), (result.NotFound, result.Failed));
        Assert.Contains("NOT_FOUND", result.Message);
        var rows = (await c.QueryAsync<(long Id, string State, string? PollError)>(
            "SELECT ItsmTicketId, State, PollError FROM wf.ItsmTicket WHERE ItsmTicketId IN (@gone, @broken, @fine)", new { gone, broken, fine })).ToDictionary(r => r.Id);
        Assert.Equal("NOT_FOUND", rows[gone].State);
        Assert.Contains("was not found", rows[gone].PollError);
        Assert.Equal("NEW", rows[broken].State);
        Assert.Matches("^HttpRequestException, reference [0-9A-F]{6}", rows[broken].PollError);
        Assert.DoesNotContain("secret", rows[broken].PollError);
        Assert.Equal("IN_PROGRESS", rows[fine].State);
        Assert.Null(rows[fine].PollError);

        // A ticket that reappears picks its state up again and loses its error.
        var back = new FakeItsm(n => new ItsmTicketState(n, "IN_PROGRESS", null, null, null));
        await Poller(back).RunAsync(default);
        var again = await c.QuerySingleAsync<(string State, string? PollError)>("SELECT State, PollError FROM wf.ItsmTicket WHERE ItsmTicketId = @gone", new { gone });
        Assert.Equal(("IN_PROGRESS", (string?)null), again);

        // When not one ticket can be read, the ITSM itself is failing: the run fails, so the job shows it.
        var down = new FakeItsm(_ => throw new HttpRequestException("down"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Poller(down).RunAsync(default));
    }

    [Fact]
    public async Task Retention_removes_old_bookkeeping_and_keeps_what_is_still_needed()
    {
        Assert.Equal(180, RetentionService.Days(null));
        Assert.Equal(180, RetentionService.Days("six months"));
        Assert.Equal(30, RetentionService.Days("7"));
        Assert.Equal(365, RetentionService.Days(" 365 "));

        using var c = db.Admin();
        var job = "RetentionTest" + Guid.NewGuid().ToString("n")[..6];
        long Run(string status, int daysAgo) => c.ExecuteScalar<long>(
            "INSERT intg.JobRun (JobName, Node, StartedUtc, FinishedUtc, Status) OUTPUT inserted.JobRunId VALUES (@job, N'test', DATEADD(DAY, -@daysAgo, SYSUTCDATETIME()), DATEADD(DAY, -@daysAgo, SYSUTCDATETIME()), @status)",
            new { job, status, daysAgo });
        var oldest = Run("FAILED", 300);
        var lastSuccess = Run("SUCCEEDED", 250);
        var last = Run("FAILED", 200);

        long Outbox(string key, string processed, string payload = "{\"subject\":\"x\"}") => c.ExecuteScalar<long>(
            $"INSERT ntf.Outbox (EventType, Payload, DedupeKey, CreatedUtc, {processed}) OUTPUT inserted.OutboxId VALUES ('N01', @payload, @key, DATEADD(DAY, -200, SYSUTCDATETIME()), DATEADD(DAY, -200, SYSUTCDATETIME()))",
            new { key = key + Guid.NewGuid().ToString("n"), payload });
        var oldDone = Outbox("ret-old-", "ProcessedAtUtc");
        var oldDead = Outbox("ret-dead-", "DeadLetteredUtc");
        var recentDone = c.ExecuteScalar<long>("INSERT ntf.Outbox (EventType, Payload, DedupeKey, ProcessedAtUtc) OUTPUT inserted.OutboxId VALUES ('N01', N'{\"subject\":\"y\"}', CONCAT('ret-new-', NEWID()), SYSUTCDATETIME())");
        var pending = c.ExecuteScalar<long>("INSERT ntf.Outbox (EventType, Payload, DedupeKey, AvailableAtUtc) OUTPUT inserted.OutboxId VALUES ('N01', N'{}', CONCAT('ret-wait-', NEWID()), DATEADD(DAY, 1, SYSUTCDATETIME()))");
        long Delivery(long outboxId, int daysAgo) => c.ExecuteScalar<long>(
            "INSERT ntf.Delivery (OutboxId, Channel, DedupeKey, Status, Utc) OUTPUT inserted.DeliveryId VALUES (@outboxId, 'EMAIL', CONCAT('ret-', NEWID()), 'SENT', DATEADD(DAY, -@daysAgo, SYSUTCDATETIME()))",
            new { outboxId, daysAgo });
        var oldDelivery = Delivery(oldDone, 200);
        var newDelivery = Delivery(recentDone, 1);
        var waitingDelivery = Delivery(pending, 200);   // its outbox item is not finished: kept

        var result = await new RetentionService(WorkerDb()).RunAsync(default);
        Assert.Equal(180, result.Days);

        var runs = c.Query<long>("SELECT JobRunId FROM intg.JobRun WHERE JobName = @job", new { job }).ToHashSet();
        Assert.DoesNotContain(oldest, runs);
        Assert.Contains(lastSuccess, runs);   // the job's latest success, which Admin > Integrations shows
        Assert.Contains(last, runs);          // and its latest run
        var outbox = c.Query<(long Id, string Payload)>("SELECT OutboxId, Payload FROM ntf.Outbox WHERE OutboxId IN (@oldDone, @oldDead, @recentDone)", new { oldDone, oldDead, recentDone }).ToDictionary(r => r.Id, r => r.Payload);
        Assert.Equal("{}", outbox[oldDone]);   // the key stays, so the notice is never sent again
        Assert.NotEqual("{}", outbox[oldDead]);
        Assert.NotEqual("{}", outbox[recentDone]);
        var deliveries = c.Query<long>("SELECT DeliveryId FROM ntf.Delivery WHERE DeliveryId IN (@oldDelivery, @newDelivery, @waitingDelivery)", new { oldDelivery, newDelivery, waitingDelivery }).ToHashSet();
        Assert.Equal(new HashSet<long> { newDelivery, waitingDelivery }, deliveries);

        // The outbox still refuses the old key.
        var key = c.ExecuteScalar<string>("SELECT DedupeKey FROM ntf.Outbox WHERE OutboxId = @oldDone", new { oldDone });
        var again = await WorkerDb().InTransactionAsync(u => new Application.Outbox.OutboxWriter().EnqueueAsync(u, new Application.Outbox.OutboxMessage { EventType = "N01", Subject = "again" }, key!));
        Assert.False(again);
    }
}
