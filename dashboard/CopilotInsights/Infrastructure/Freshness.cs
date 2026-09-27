using Microsoft.Extensions.Caching.Memory;

namespace CopilotInsights.Infrastructure;

/// <summary>
/// Whether the data is fresh: the banner every page shows when the rollups are older than
/// FreshnessHours, or the last pipeline run failed. Worked out every five minutes, not per page.
/// </summary>
public sealed class Freshness(Db db, Settings settings, IMemoryCache cache)
{
    public async Task<string?> BannerAsync() =>
        await cache.GetOrCreateAsync("freshness-banner", async e =>
        {
            e.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5);
            try
            {
                var limit = await settings.IntAsync("FreshnessHours", 36);
                var r = (await db.QueryAsync<(DateTime? Refreshed, string? Status, string? Step, DateTime? Started)>(@"
SELECT (SELECT MAX(EndedUtc) FROM dash.RefreshRun),
       (SELECT TOP (1) Status FROM audit.PipelineRun ORDER BY RunId DESC),
       (SELECT TOP (1) FailedStep FROM audit.PipelineRun ORDER BY RunId DESC),
       (SELECT TOP (1) StartedUtc FROM audit.PipelineRun ORDER BY RunId DESC);")).Single();
                return Describe(r.Refreshed, limit, r.Status, r.Step, r.Started, DateTime.UtcNow);
            }
            catch (Microsoft.Data.SqlClient.SqlException)
            {
                /* No pipeline-run table yet (an older database): freshness from the rollups alone. */
                var refreshed = await db.ScalarAsync<DateTime?>("SELECT MAX(EndedUtc) FROM dash.RefreshRun");
                return Describe(refreshed, await settings.IntAsync("FreshnessHours", 36), null, null, null, DateTime.UtcNow);
            }
        });

    /// <summary>The banner's words, or null when all is well. Pure, for the tests.</summary>
    public static string? Describe(DateTime? refreshedUtc, int limitHours, string? lastRunStatus, string? failedStep, DateTime? lastRunStartedUtc, DateTime nowUtc)
    {
        if (lastRunStatus == "Failed")
            return $"The last pipeline run ({lastRunStartedUtc:yyyy-MM-dd HH:mm} UTC) failed at \"{failedStep}\", so the latest days may be missing. Administrators: see Admin > Health.";
        if (refreshedUtc is null) return null;
        var age = nowUtc - refreshedUtc.Value;
        return age.TotalHours > limitHours
            ? $"The data was last refreshed {age.TotalHours:0} hours ago ({refreshedUtc:yyyy-MM-dd HH:mm} UTC), later than the {limitHours} hours expected. The latest days may be missing."
            : null;
    }
}
