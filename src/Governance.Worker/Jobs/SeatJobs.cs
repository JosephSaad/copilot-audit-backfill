using Governance.Application.Data;
using Governance.Application.Integrations;
using Microsoft.Extensions.Options;

namespace Governance.Worker.Jobs;

/// <summary>
/// Daily Copilot seat read (X13): subscribed SKUs, seat groups, license states and the usage report from Graph, then the
/// seat findings, reclaim proposals and, in the first week of a month, the draft chargeback snapshot for the month before.
/// Read-only: licenses and groups change only through OIM and ServiceNow.
/// </summary>
public sealed class SeatSyncJob(IServiceScopeFactory scopes, IOptions<DbOptions> db, IOptions<WorkerOptions> worker, ILogger<SeatSyncJob> log)
    : JobBase(scopes, db, worker, log)
{
    public override string Name => "SeatSync";
    public override TimeSpan Interval => TimeSpan.FromHours(24);
    public override TimeSpan InitialDelay => TimeSpan.FromMinutes(4);   // after the first directory sync has brought people
    protected override async Task<string?> RunAsync(IServiceProvider s, CancellationToken ct) => $"Sync run {await s.GetRequiredService<SeatSyncService>().RunAsync(null, ct)}";
}
