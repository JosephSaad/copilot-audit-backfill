namespace CopilotInsights.Infrastructure;

/// <summary>
/// The last run of a loader of AI usage outside Microsoft 365 Copilot (audit.ExternalUsageRun, 2.6.2): GitHub Copilot
/// (tools\Import-GitHubCopilotUsage.ps1) or Foundry and Azure OpenAI (tools\Import-FoundryUsage.ps1).
/// </summary>
public sealed class ExternalUsageRun
{
    public string Source { get; set; } = "";
    public string? Scope { get; set; }
    public DateTime StartedUtc { get; set; }
    public DateTime? EndedUtc { get; set; }
    public string Status { get; set; } = "";
    public DateTime? FromDay { get; set; }
    public DateTime? ToDay { get; set; }
    public int? RowsWritten { get; set; }
    public string? Detail { get; set; }
    public DateTime? CredentialExpiresUtc { get; set; }
    public string? Host { get; set; }
    /// <summary>The newest day the source's daily table holds; null when it holds none.</summary>
    public DateTime? LastDay { get; set; }
}

/// <summary>What the Health page says about each source. Pure but for <see cref="ReadAsync"/>, for the tests.</summary>
public static class ExternalUsage
{
    public const string GitHub = "GitHub";
    public const string Foundry = "Foundry";
    /// <summary>A run still "Running" after this long was stopped before it could record how it ended.</summary>
    public static readonly TimeSpan Stale = TimeSpan.FromHours(6);
    /// <summary>A last successful run older than this means the nightly step is not running.</summary>
    public static readonly TimeSpan Late = TimeSpan.FromHours(50);

    public static string Name(string source) => source switch
    {
        GitHub => "GitHub Copilot usage",
        Foundry => "Foundry and Azure OpenAI usage",
        _ => source + " usage",
    };

    /// <summary>The tool that loads the source, and the setting that makes the nightly run call it.</summary>
    public static string Tool(string source) => source switch
    {
        GitHub => "tools\\Import-GitHubCopilotUsage.ps1 (GitHubEnterprise in copilot-audit.settings.psd1)",
        Foundry => "tools\\Import-FoundryUsage.ps1 (FoundrySubscriptions in copilot-audit.settings.psd1)",
        _ => "its loader",
    };

    /// <summary>One line on where the source stands, and the card's color. A null run: never loaded here.</summary>
    public static (string Text, string Css) Status(string source, ExternalUsageRun? r, DateTime nowUtc, Func<DateTime?, string> when)
    {
        if (r is null) return ($"Not loaded here. It is optional: {Tool(source)} loads it.", "muted");
        var days = r.FromDay is { } f && r.ToDay is { } t ? $" for {f:yyyy-MM-dd} to {t:yyyy-MM-dd}" : "";
        var newest = r.LastDay is { } d ? $" Newest day held: {d:yyyy-MM-dd}." : "";
        var token = r.CredentialExpiresUtc is { } e
            ? e <= nowUtc ? $" Its token expired on {e:yyyy-MM-dd}." : $" Its token expires {e:yyyy-MM-dd} ({Math.Floor((e - nowUtc).TotalDays):0} days)."
            : "";
        var tokenBad = r.CredentialExpiresUtc is { } x && (x - nowUtc).TotalDays <= 14;
        var detail = string.IsNullOrWhiteSpace(r.Detail) ? "" : " " + r.Detail.Trim();
        switch (r.Status)
        {
            case "Succeeded":
                var late = nowUtc - (r.EndedUtc ?? r.StartedUtc) > Late;
                return ($"Last loaded {when(r.EndedUtc ?? r.StartedUtc)}{days}: {r.RowsWritten ?? 0:N0} rows.{newest}{token}{(late ? " Nothing has run since: check the nightly run's settings." : "")}",
                    late || tokenBad ? "status-warn" : "status-ok");
            case "Partial":
                return ($"Last run {when(r.EndedUtc ?? r.StartedUtc)}{days} loaded a part: {r.RowsWritten ?? 0:N0} rows.{detail}{newest}{token}", "status-warn");
            case "Running":
                return nowUtc - r.StartedUtc > Stale
                    ? ($"A run that began {when(r.StartedUtc)} never recorded how it ended: it was stopped. Run {Tool(source)} again.{newest}", "status-warn")
                    : ($"Running since {when(r.StartedUtc)}.{newest}", "status-ok");
            default:
                return ($"The last run, {when(r.EndedUtc ?? r.StartedUtc)}, failed.{detail}{newest}{token}", "status-bad");
        }
    }

    /// <summary>The last run of each source that has ever run here, GitHub first; empty over a database without the table.</summary>
    public static async Task<List<ExternalUsageRun>> ReadAsync(Db db)
    {
        if (await db.ScalarAsync<int>("SELECT CASE WHEN OBJECT_ID(N'audit.ExternalUsageRun', N'U') IS NULL THEN 0 ELSE 1 END") == 0) return [];
        return await db.QueryAsync<ExternalUsageRun>(@"
SELECT r.Source, r.Scope, r.StartedUtc, r.EndedUtc, r.Status, CAST(r.FromDay AS DATETIME2(0)) AS FromDay, CAST(r.ToDay AS DATETIME2(0)) AS ToDay,
       r.RowsWritten, r.Detail, r.CredentialExpiresUtc, r.Host,
       CAST(CASE r.Source WHEN 'GitHub' THEN (SELECT MAX([Day]) FROM audit.GitHubCopilotDay)
                          ELSE (SELECT MAX([Day]) FROM audit.FoundryUsageDay) END AS DATETIME2(0)) AS LastDay
FROM audit.ExternalUsageRun AS r
WHERE r.RunId IN (SELECT MAX(RunId) FROM audit.ExternalUsageRun GROUP BY Source)
ORDER BY CASE r.Source WHEN 'GitHub' THEN 0 ELSE 1 END, r.Source;");
    }
}
