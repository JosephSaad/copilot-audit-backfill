using Governance.Application.Data;

namespace Governance.Application.Integrations;

/// <summary>
/// Keeps the worker's own bookkeeping from growing without end (the daily Retention job). Older than
/// Retention.WorkerRecordDays (180 unless set):
/// <list type="bullet">
/// <item>intg.JobRun rows are deleted, except each job's latest run and latest successful run (Admin > Integrations lists them).</item>
/// <item>ntf.Delivery rows of finished outbox items are deleted.</item>
/// <item>Processed ntf.Outbox items keep their row and key, so a notice is never sent a second time, but lose their text
/// (Payload becomes {}). Items set aside (dead-lettered) are kept whole for an administrator.</item>
/// </list>
/// Deletes go 5,000 rows at a time, each batch its own transaction, so SQL Server never escalates to a table lock.
/// Ledger and audit tables (append-only) are never touched.
/// </summary>
public sealed class RetentionService(Db db)
{
    public const string SettingKey = "Retention.WorkerRecordDays";
    public const int DefaultDays = 180;
    public const int MinimumDays = 30;
    public const int Batch = 5000;

    public sealed record Result(int Days, int JobRuns, int Deliveries, int OutboxCompacted)
    {
        public string Message => $"Older than {Days} days: {JobRuns} job runs and {Deliveries} delivery records removed, {OutboxCompacted} processed outbox items reduced to their key.";
    }

    public async Task<Result> RunAsync(CancellationToken ct)
    {
        var raw = await db.InTransactionAsync(u => u.ScalarAsync<string?>("SELECT SettingValue FROM ref.Setting WHERE SettingKey = @SettingKey", new { SettingKey }), ct);
        var days = Days(raw);
        var jobRuns = await RepeatAsync(
            """
            DELETE TOP (@Batch) j FROM intg.JobRun AS j
            WHERE j.StartedUtc < DATEADD(DAY, -@days, SYSUTCDATETIME())
              AND j.JobRunId NOT IN (SELECT MAX(JobRunId) FROM intg.JobRun GROUP BY JobName)
              AND j.JobRunId NOT IN (SELECT MAX(JobRunId) FROM intg.JobRun WHERE Status = 'SUCCEEDED' GROUP BY JobName);
            """, days, ct);
        var deliveries = await RepeatAsync(
            """
            DELETE TOP (@Batch) d FROM ntf.Delivery AS d
            WHERE d.Utc < DATEADD(DAY, -@days, SYSUTCDATETIME()) AND d.Status <> 'PENDING'
              AND NOT EXISTS (SELECT 1 FROM ntf.Outbox AS o WHERE o.OutboxId = d.OutboxId AND o.ProcessedAtUtc IS NULL AND o.DeadLetteredUtc IS NULL);
            """, days, ct);
        var outbox = await RepeatAsync(
            """
            UPDATE TOP (@Batch) o SET Payload = N'{}', LastError = NULL
            FROM ntf.Outbox AS o
            WHERE o.ProcessedAtUtc < DATEADD(DAY, -@days, SYSUTCDATETIME()) AND o.Payload <> N'{}';
            """, days, ct);
        return new Result(days, jobRuns, deliveries, outbox);
    }

    /// <summary>The setting as a number of days: the default when missing or not a number, never under 30. Pure, for the tests.</summary>
    public static int Days(string? setting)
        => int.TryParse(setting?.Trim(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var d) ? Math.Max(MinimumDays, d) : DefaultDays;

    private async Task<int> RepeatAsync(string sql, int days, CancellationToken ct)
    {
        var total = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var n = await db.InTransactionAsync(u => u.ExecuteAsync(sql, new { Batch, days }), ct);
            total += n;
            if (n < Batch) return total;
        }
    }
}
