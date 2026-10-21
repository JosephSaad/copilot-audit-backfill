using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Data.SqlClient;

namespace CopilotInsights.Infrastructure;

/// <summary>
/// What the query cache is keyed on (2.4.7): a stamp of everything the reports read that can change, taken from the
/// database at most every 30 seconds for each tenant. It changes when the dashboard is refreshed, when a pipeline run
/// ends, when the licenses or the directory are read, when a setting or a reference table changes (here or on another web
/// server, or from Invoke-CopilotMaintenance.ps1), and at midnight UTC, since some reports count from today.
/// Tables that change all day (triage, exported actions, confirmations) are not in the stamp: a change to one would
/// end everything kept. A query that reads one is kept under that table's own version, read from the database for
/// every page that asks (2.4.11, <see cref="Live"/>).
/// </summary>
public sealed partial class DataStamp(TenantAccessor tenants, QueryCache cache, ILogger<DataStamp> log)
{
    /// <summary>Small tables whose contents shape what reports show: counted and checksummed into the stamp.</summary>
    static readonly string[] Reference =
    [
        "dash.Setting", "dash.Target", "dash.TimeSaving", "dash.TrainingAttendance", "dash.ValueName", "dash.AgentAlias", "dash.SensitivityLabel",
        "dash.JobFamilyRule", "dash.UseCaseRule", "dash.EventMarker", "dash.LicenseContract", "audit.PipelineSetting", "audit.SessionAppCategory",
        "dash.AgentInventoryRun",   // 2.5.6: each load of the agent workbook adds a row, so the agent reports are read again
        /* 2.6.6: what Leadership › Costs and savings reads that tools\Import-CostCsv.ps1 or an administrator can change outside a nightly run. */
        "dash.CostPrice", "dash.CostSuite", "audit.FabricCapacityDay", "audit.FabricWorkloadDay", "audit.PowerAutomateFlow", "audit.PowerPlatformUserActivity",
        "audit.PowerPlatformCapacityDay", "audit.PowerPlatformCreditDay",
        /* 2.6.9: the resource map administrators keep (the AI apps' days are stamped by their rollup's runs, below: too many rows to checksum). */
        "audit.AIAppResource",
        /* 2.6.8: the reservation register, its rate, the Technology pool and the month-end snapshots (Leadership › Copilot reservations). */
        "dash.Reservation", "dash.ReservationRate", "dash.TechnologyAllocation", "dash.ReservationSnapshot",
    ];

    /// <summary>A table people change all day, and the query that gives its version: one short string that differs after any change.</summary>
    public sealed record LiveTable(string Table, string Version);

    /// <summary>
    /// Tables written all day by people working in the dashboard. Each is small, and its version is its row count, its
    /// latest change and a checksum of every row (the exports are only ever added to: the count and the last number).
    /// A table the database does not have yet has the version "none".
    /// </summary>
    public static readonly LiveTable[] Live =
    [
        new("dash.FlagCase", "IF OBJECT_ID(N'dash.FlagCase', N'U') IS NULL SELECT N'none' ELSE SELECT CONCAT(COUNT_BIG(*), N':', CONVERT(NVARCHAR(30), MAX(UpdatedUtc), 126), N':', CHECKSUM_AGG(BINARY_CHECKSUM(*))) FROM dash.FlagCase;"),
        new("dash.LicenseAction", "IF OBJECT_ID(N'dash.LicenseAction', N'U') IS NULL SELECT N'none' ELSE SELECT CONCAT(COUNT_BIG(*), N':', MAX(ActionId)) FROM dash.LicenseAction;"),
        new("dash.NonHumanException", "IF OBJECT_ID(N'dash.NonHumanException', N'U') IS NULL SELECT N'none' ELSE SELECT CONCAT(COUNT_BIG(*), N':', CONVERT(NVARCHAR(30), MAX(ConfirmedUtc), 126), N':', CHECKSUM_AGG(BINARY_CHECKSUM(*))) FROM dash.NonHumanException;"),
    ];

    /// <summary>The names of the tables in <see cref="Live"/>: a query that names one is kept only under that table's version.</summary>
    public static readonly string[] VolatileTables = [.. Live.Select(t => t.Table)];

    /// <summary>Tables a write to which changes nothing a cached report query reads: logs, send records, and the volatile ones.</summary>
    static readonly string[] NoEffect =
    [
        "dash.AuditLog", "dash.NotificationLog", "dash.AlertState", "dash.DigestSend", "dash.DigestPreference", "dash.SavedView", "dash.Subscription",
        "dash.Extract", "dash.Secret", "dash.RoleGrant", .. VolatileTables,
    ];

    readonly ConcurrentDictionary<string, DateTime> _checked = new();
    readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();

    /// <summary>Whether a query reads a table that changes all day: it is kept only under that table's version.</summary>
    public static bool Volatile(string sql) => VolatileTables.Any(t => sql.Contains(t, StringComparison.OrdinalIgnoreCase));

    /// <summary>The tables that change all day that a query names, in a fixed order.</summary>
    public static IReadOnlyList<LiveTable> LiveIn(string sql) => [.. Live.Where(t => sql.Contains(t.Table, StringComparison.OrdinalIgnoreCase))];

    [GeneratedRegex(@"\b(?:INSERT\s+INTO|UPDATE|DELETE\s+FROM|MERGE(?:\s+INTO)?|TRUNCATE\s+TABLE)\s+(\[?\w+\]?\.\[?\w+\]?)", RegexOptions.IgnoreCase)]
    private static partial Regex Targets();

    /// <summary>Whether a write could change what a cached query returns: yes, unless every table it writes is a log or a volatile one.</summary>
    public static bool Matters(string sql)
    {
        var targets = Targets().Matches(sql).Select(m => m.Groups[1].Value.Replace("[", "").Replace("]", "")).ToList();
        return targets.Count == 0 || targets.Any(t => !NoEffect.Contains(t, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>Brings the current tenant's stamp up to date if it was last read more than 30 seconds ago.</summary>
    public async Task EnsureAsync(bool force = false)
    {
        if (!cache.Enabled) return;
        var tenant = tenants.Current;
        if (!force && _checked.TryGetValue(tenant.Name, out var at) && DateTime.UtcNow - at < TimeSpan.FromSeconds(30)) return;
        var gate = _locks.GetOrAdd(tenant.Name, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            if (!force && _checked.TryGetValue(tenant.Name, out at) && DateTime.UtcNow - at < TimeSpan.FromSeconds(30)) return;
            cache.SetStamp(tenant.Name, await ReadAsync(tenant.ConnectionString));
            _checked[tenant.Name] = DateTime.UtcNow;
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException)
        {
            /* No stamp, no cache: a stamp that cannot be read is replaced by one nothing was cached under. */
            log.LogWarning(ex, "The data stamp could not be read; report queries are not cached until it can.");
            cache.SetStamp(tenant.Name, "unread:" + Guid.NewGuid().ToString("N"));
            _checked[tenant.Name] = DateTime.UtcNow;
        }
        finally { gate.Release(); }
    }

    /// <summary>The current tenant's stamp as last read: for the warm-up, which starts again when it changes.</summary>
    public string Current => cache.Stamp(tenants.Current.Name);

    /// <summary>The batch that reads the stamp: one string, the same on every web server.</summary>
    public static string Batch()
    {
        /* One batch: the runs that change the facts, the license read, and each reference table's row count and checksum.
           A table the database does not have yet counts as empty.
           2.4.12: a pipeline run counts when it ends, not when it starts. Its first row ended everything kept, and started
           a build of every page, at the start of the nightly load, before any data had changed. And the license read is
           every table it writes: the stamp saw only the pool, written first, and the warm-up kept the licensed accounts,
           their licenses and the service-account groups from before the read, until midnight UTC, when no refresh
           followed (a run that failed later, or the license tool run on its own). */
        return $@"
SET NOCOUNT ON;
DECLARE @s NVARCHAR(MAX) = N'';
DECLARE @t TABLE (Name SYSNAME, Sql NVARCHAR(400));
INSERT INTO @t VALUES
    (N'dash.RefreshRun', N'SELECT @r = CONCAT(MAX(RunId), N'':'', COUNT_BIG(*)) FROM dash.RefreshRun'),
    (N'audit.PipelineRun', N'SELECT @r = CONCAT(COUNT_BIG(*), N'':'', CONVERT(NVARCHAR(30), MAX(EndedUtc), 126)) FROM audit.PipelineRun WHERE EndedUtc IS NOT NULL'),
    (N'audit.LicenseSku', N'SELECT @r = CONVERT(NVARCHAR(30), MAX(CapturedUtc), 126) FROM audit.LicenseSku'),
    (N'audit.CopilotLicense', N'SELECT @r = CONCAT(COUNT_BIG(*), N'':'', CONVERT(NVARCHAR(30), MAX(UpdatedUtc), 126)) FROM audit.CopilotLicense'),
    (N'audit.UserLicenseSku', N'SELECT @r = CONCAT(COUNT_BIG(*), N'':'', CONVERT(NVARCHAR(30), MAX(CapturedUtc), 126)) FROM audit.UserLicenseSku'),
    (N'audit.GroupMember', N'SELECT @r = CONCAT(COUNT_BIG(*), N'':'', CONVERT(NVARCHAR(30), MAX(LoadedUtc), 126)) FROM audit.GroupMember'),
    (N'audit.UserDirectory', N'SELECT @r = CONCAT(COUNT_BIG(*), N'':'', CONVERT(NVARCHAR(30), MAX(LoadedUtc), 126)) FROM audit.UserDirectory'),
    (N'audit.AIAppUserDay', N'SELECT @r = CONCAT(COUNT_BIG(*), N'':'', CONVERT(NVARCHAR(30), MAX(EndedUtc), 126)) FROM audit.ExternalUsageRun WHERE Source = ''Activity'''),
{string.Join(",\n", Reference.Select(t => $"    (N'{t}', N'SELECT @r = CONCAT(COUNT_BIG(*), N'':'', CHECKSUM_AGG(BINARY_CHECKSUM(*))) FROM {t}')"))};
DECLARE @name SYSNAME, @q NVARCHAR(400), @r NVARCHAR(200);
DECLARE c CURSOR LOCAL FAST_FORWARD FOR SELECT Name, Sql FROM @t;
OPEN c;
FETCH NEXT FROM c INTO @name, @q;
WHILE @@FETCH_STATUS = 0
BEGIN
    SET @r = NULL;
    IF OBJECT_ID(@name) IS NOT NULL
        BEGIN TRY EXEC sp_executesql @q, N'@r NVARCHAR(200) OUTPUT', @r = @r OUTPUT; END TRY BEGIN CATCH SET @r = N'?'; END CATCH;
    SET @s += ISNULL(@r, N'') + N'|';
    FETCH NEXT FROM c INTO @name, @q;
END;
CLOSE c; DEALLOCATE c;
SELECT CONVERT(NVARCHAR(10), CAST(SYSUTCDATETIME() AS DATE), 23) + N'|' + CONVERT(NVARCHAR(64), HASHBYTES('SHA2_256', @s), 2);";
    }

    static async Task<string> ReadAsync(string connectionString)
    {
        await using var conn = new SqlConnection(connectionString);
        return await conn.ExecuteScalarAsync<string>(new CommandDefinition(Batch(), commandTimeout: 30)) ?? "none";
    }
}
