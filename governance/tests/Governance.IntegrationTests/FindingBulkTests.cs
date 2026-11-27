using System.Diagnostics;
using Dapper;
using Governance.Application;
using Governance.Application.Data;
using Governance.Application.Integrations;
using Governance.Application.Outbox;
using Microsoft.Extensions.Options;

namespace Governance.IntegrationTests;

/// <summary>
/// Findings raised and resolved by the tens of thousands, as a seat read does when most license holders are in no line
/// of business yet: within the default 60-second command timeout, with the same results as one at a time.
/// </summary>
public sealed class FindingBulkTests(DatabaseFixture db) : IClassFixture<DatabaseFixture>
{
    private sealed class FixedClock(DateTime now) : IClock { public DateTime UtcNow { get; } = now; }

    [Fact]
    public async Task Thirty_thousand_findings_are_raised_refreshed_and_resolved_in_seconds()
    {
        var appDb = new Db(Options.Create(new DbOptions { ConnectionString = db.WorkerConnectionString, CommandTimeoutSeconds = 60 }), SystemPrincipal.Instance);
        var writer = new FindingWriter(new OutboxWriter(), new FixedClock(new DateTime(2026, 11, 27, 12, 0, 0, DateTimeKind.Utc)));
        const string type = "SEAT_NO_LOB";
        List<(string, FindingWriter.NewFinding)> Hits(int from, int count) => Enumerable.Range(from, count)
            .Select(i => ($"{type}:bulk-{i:D6}", new FindingWriter.NewFinding(type, "MEDIUM", $"Person {i} holds a Copilot seat but is in no LOB tracking group", "Details", ExternalId: $"bulk-{i}", SyncRunId: 1)))
            .ToList();
        var watch = Stopwatch.StartNew();

        // First read: all new. A key given twice counts once.
        var first = Hits(0, 30_000);
        first.Add(first[0]);
        var raised = await appDb.InTransactionAsync(async u =>
        {
            var n = await writer.RaiseManyAsync(u, first);
            await writer.ResolveMissingAsync(u, type, first.Select(h => h.Item1).ToList(), "gone");
            return n;
        });
        Assert.Equal(30_000, raised);

        // Second read: 1,000 people joined a line of business (no longer hit), 500 are new.
        var second = Hits(1_000, 29_500);
        var (raisedAgain, resolved) = await appDb.InTransactionAsync(async u =>
        {
            var n = await writer.RaiseManyAsync(u, second);
            var r = await writer.ResolveMissingAsync(u, type, second.Select(h => h.Item1).ToList(), "gone");
            return (n, r);
        });
        Assert.Equal(500, raisedAgain);
        Assert.Equal(1_000, resolved);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(60), $"took {watch.Elapsed}");

        using var w = await appDb.OpenSystemAsync();
        Assert.Equal(29_500, await w.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM ops.Finding WHERE FindingType = @type AND Status = 'OPEN' AND DedupeKey LIKE 'SEAT_NO_LOB:bulk-%'", new { type }));
        Assert.Equal(1_000, await w.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM ops.Finding WHERE FindingType = @type AND Status = 'RESOLVED' AND DedupeKey LIKE 'SEAT_NO_LOB:bulk-%'", new { type }));

        // Nothing hit any more: every open one is resolved (an empty list resolves the whole type).
        var all = await appDb.InTransactionAsync(u => writer.ResolveMissingAsync(u, type, Array.Empty<string>(), "gone"));
        Assert.Equal(29_500, all);
    }
}
