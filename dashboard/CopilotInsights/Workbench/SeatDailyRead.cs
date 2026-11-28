using Dapper;
using Governance.Application.Data;
using Governance.Application.Integrations;

namespace Governance.Web.Infrastructure;

/// <summary>
/// The daily seat read, on the web site (no worker needed): from Seats.DailyReadTime (08:00) in the Workbench time zone
/// (Calendar.TimeZone), when no seat read has succeeded since that time today. One web server reads, under a database
/// lock; the others see the run and wait for tomorrow. A failed read is tried again an hour later. Each read records the
/// day for the monthly peaks (Seats.TrackingStart onwards). Empty Seats.DailyReadTime turns it off.
/// </summary>
public sealed class SeatDailyRead(IServiceScopeFactory scopes, ILogger<SeatDailyRead> log) : BackgroundService
{
    static readonly TimeSpan Tick = TimeSpan.FromMinutes(5);
    DateTime lastTryUtc = DateTime.MinValue;

    /// <summary>When today's read is due, in UTC, or null when it is turned off. Pure, for the tests.</summary>
    public static DateTime? DueUtc(DateTime utcNow, string? zone, string? readTime)
    {
        if (string.IsNullOrWhiteSpace(readTime) || !TimeSpan.TryParseExact(readTime.Trim(), @"hh\:mm", System.Globalization.CultureInfo.InvariantCulture, out var at)) return null;
        TimeZoneInfo tz;
        try { tz = TimeZoneInfo.FindSystemTimeZoneById(string.IsNullOrWhiteSpace(zone) ? "Eastern Standard Time" : zone); }
        catch (TimeZoneNotFoundException) { tz = TimeZoneInfo.Utc; }
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utcNow, DateTimeKind.Utc), tz);
        return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local.Date + at, DateTimeKind.Unspecified), tz);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken); } catch (OperationCanceledException) { return; }
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await TryOnceAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex) { log.LogError(ex, "The daily seat read could not run."); }
            try { await Task.Delay(Tick, stoppingToken); } catch (OperationCanceledException) { return; }
        }
    }

    const string DoneSql = """
        SELECT COUNT(*) FROM intg.SyncRun
        WHERE Integration = 'X13' AND ((Status IN ('SUCCEEDED','PARTIAL') AND StartedUtc >= @dueUtc)
           OR (Status = 'RUNNING' AND StartedUtc >= DATEADD(HOUR, -3, SYSUTCDATETIME())))
        """;

    async Task TryOnceAsync(CancellationToken ct)
    {
        if (DateTime.UtcNow - lastTryUtc < TimeSpan.FromHours(1)) return;
        using var scope = scopes.CreateScope();
        var seats = scope.ServiceProvider.GetService<SeatSyncService>();
        var db = scope.ServiceProvider.GetService<Db>();
        if (seats is null || db is null) return;   // the Workbench or its seat read is not set up on this server

        await using var c = await db.OpenSystemAsync();
        var settings = (await c.QueryAsync<(string Key, string? Value)>(
            "SELECT SettingKey, SettingValue FROM ref.Setting WHERE SettingKey IN ('Calendar.TimeZone','Seats.DailyReadTime')")).ToDictionary(s => s.Key, s => s.Value);
        if (DueUtc(DateTime.UtcNow, settings.GetValueOrDefault("Calendar.TimeZone"), settings.GetValueOrDefault("Seats.DailyReadTime")) is not DateTime due || DateTime.UtcNow < due) return;
        if (await c.ExecuteScalarAsync<int>(DoneSql, new { dueUtc = due }) > 0) return;

        // One server reads: the lock is held by this connection's session until the read ends.
        if (await c.ExecuteScalarAsync<int>("DECLARE @r int; EXEC @r = sp_getapplock @Resource = N'seat-daily-read', @LockMode = 'Exclusive', @LockOwner = 'Session', @LockTimeout = 0; SELECT @r;") < 0) return;
        try
        {
            if (await c.ExecuteScalarAsync<int>(DoneSql, new { dueUtc = due }) > 0) return;
            lastTryUtc = DateTime.UtcNow;
            log.LogInformation("The daily seat read starts on {Server}.", Environment.MachineName);
            await seats.RunAsync(null, ct);
            log.LogInformation("The daily seat read finished on {Server}.", Environment.MachineName);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError(ex, "The daily seat read failed on {Server}; it is tried again in an hour.", Environment.MachineName);
        }
        finally
        {
            try { await c.ExecuteAsync("EXEC sp_releaseapplock @Resource = N'seat-daily-read', @LockOwner = 'Session';"); } catch { /* the lock ends with the connection anyway */ }
        }
    }
}
