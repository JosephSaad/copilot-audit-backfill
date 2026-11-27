using Governance.Application;
using Governance.Application.Data;
using Governance.Application.Outbox;
using Governance.Domain;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace Governance.Worker.Jobs;

public sealed class WorkerOptions
{
    /// <summary>Jobs to leave off on this node (comma list of job names).</summary>
    public string DisabledJobs { get; set; } = "";
    public string Node { get; set; } = Environment.MachineName + ":" + Environment.ProcessId;
}

/// <summary>
/// A scheduled job. Singleton jobs take a SQL application lock (sp_getapplock, Session owner) on a dedicated unpooled
/// connection so two worker nodes never run the same job at once (section 6.3). Every run is written to intg.JobRun;
/// two failures in a row raise N19 for administrators.
/// </summary>
public abstract class JobBase(IServiceScopeFactory scopes, IOptions<DbOptions> db, IOptions<WorkerOptions> worker, ILogger log) : BackgroundService
{
    private int _consecutiveFailures;

    protected WorkerOptions Options => worker.Value;

    public abstract string Name { get; }
    public abstract TimeSpan Interval { get; }
    public virtual bool Singleton => true;
    public virtual TimeSpan InitialDelay => TimeSpan.FromSeconds(5);

    protected abstract Task<string?> RunAsync(IServiceProvider services, CancellationToken ct);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (worker.Value.DisabledJobs.Split(',', StringSplitOptions.TrimEntries).Contains(Name, StringComparer.OrdinalIgnoreCase))
        {
            log.LogInformation("Job {Job} is disabled on this node.", Name);
            return;
        }
        try { await Task.Delay(InitialDelay, stoppingToken); } catch (OperationCanceledException) { return; }
        while (!stoppingToken.IsCancellationRequested)
        {
            if (await InScopeAsync(stoppingToken)) await RunGuardedAsync(stoppingToken);
            try { await Task.Delay(Interval, stoppingToken); } catch (OperationCanceledException) { return; }
        }
    }

    private bool _outOfScopeLogged;

    /// <summary>
    /// Whether the job runs on this turn: with Workbench.Scope SEATS only the seat jobs do (<see cref="WorkbenchScope.SeatJobs"/>).
    /// Read at every turn, so changing the setting on the Settings page takes effect without a restart. A database that
    /// cannot be read lets the job run, and fail where it is recorded. A run asked for with --once always runs.
    /// </summary>
    private async Task<bool> InScopeAsync(CancellationToken ct)
    {
        if (WorkbenchScope.SeatJobs.Contains(Name, StringComparer.OrdinalIgnoreCase)) return true;
        string? value;
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            value = await scope.ServiceProvider.GetRequiredService<Db>().InTransactionAsync(u => u.ScalarAsync<string?>(WorkbenchScope.Sql), ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            log.LogDebug(ex, "Job {Job}: the Workbench scope could not be read; running.", Name);
            return true;
        }
        var runs = WorkbenchScope.JobRuns(Name, WorkbenchScope.SeatsOnly(value));
        if (!runs && !_outOfScopeLogged) log.LogInformation("Job {Job} is not needed with the Workbench set to Copilot seats only (Workbench.Scope); it waits.", Name);
        _outOfScopeLogged = !runs;
        return runs;
    }

    /// <summary>Runs the job once if this node gets the lock. Returns false when another node holds it.</summary>
    public async Task<bool> RunGuardedAsync(CancellationToken ct)
    {
        SqlConnection? lockConnection = null;
        try
        {
            if (Singleton)
            {
                lockConnection = await TryLockAsync(ct);
                if (lockConnection is null) return false;
            }
            await using var scope = scopes.CreateAsyncScope();
            var sp = scope.ServiceProvider;
            var started = DateTime.UtcNow;
            long runId = 0;
            if (Singleton)
                runId = await sp.GetRequiredService<Db>().InTransactionAsync(u => u.ScalarAsync<long>(
                    "INSERT intg.JobRun (JobName, Node, StartedUtc, Status) OUTPUT inserted.JobRunId VALUES (@Name, @node, @started, 'RUNNING')",
                    new { Name, node = worker.Value.Node, started }), ct);
            try
            {
                var message = await RunAsync(sp, ct);
                if (runId > 0)
                    await sp.GetRequiredService<Db>().InTransactionAsync(u => u.ExecuteAsync(
                        "UPDATE intg.JobRun SET FinishedUtc = SYSUTCDATETIME(), Status = 'SUCCEEDED', Message = @message WHERE JobRunId = @runId",
                        new { runId, message = Cut(message) }), CancellationToken.None);
                _consecutiveFailures = 0;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _consecutiveFailures++;
                log.LogError(ex, "Job {Job} failed ({Count} in a row).", Name, _consecutiveFailures);
                var note = ex is DomainException or Application.Integrations.IntegrationPermissionException ? ex.Message : $"Failed: {ex.GetType().Name}. See the worker log.";
                var dbx = sp.GetRequiredService<Db>();
                if (runId > 0)
                    await dbx.InTransactionAsync(u => u.ExecuteAsync("UPDATE intg.JobRun SET FinishedUtc = SYSUTCDATETIME(), Status = 'FAILED', Message = @note WHERE JobRunId = @runId",
                        new { runId, note = Cut(note) }), CancellationToken.None);
                if (_consecutiveFailures >= 2)
                {
                    var outbox = sp.GetRequiredService<OutboxWriter>();
                    await dbx.InTransactionAsync(u => outbox.EnqueueAsync(u, new OutboxMessage
                    {
                        EventType = "N19", Queues = { "OPERATIONS" },
                        Roles = { new RoleTarget(Roles.WorkbenchAdmin, ScopeTypes.Tenant, null), new RoleTarget(Roles.PlatformOperator, ScopeTypes.Tenant, null) },
                        Subject = $"Integration failing: {Name}", Text = $"The {Name} job failed {_consecutiveFailures} times in a row. {note}", LinkPath = "/workbench/Admin/Integrations",
                    }, $"N19:job:{Name}:{DateTime.UtcNow:yyyyMMdd}"), CancellationToken.None);
                }
            }
            return true;
        }
        finally
        {
            if (lockConnection is not null) await lockConnection.DisposeAsync();   // closing the session releases the lock
        }
    }

    private async Task<SqlConnection?> TryLockAsync(CancellationToken ct)
    {
        var csb = new SqlConnectionStringBuilder(db.Value.ConnectionString) { Pooling = false };
        var conn = new SqlConnection(csb.ConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DECLARE @r int; EXEC @r = sp_getapplock @Resource = @res, @LockMode = 'Exclusive', @LockOwner = 'Session', @LockTimeout = 0; SELECT @r;";
        cmd.Parameters.AddWithValue("@res", "CopilotGovernance.Job." + Name);
        var result = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
        if (result >= 0) return conn;
        await conn.DisposeAsync();
        log.LogDebug("Job {Job} is running on another node.", Name);
        return null;
    }

    private static string? Cut(string? s) => s is null ? null : s.Length <= 1000 ? s : s[..1000];
}
