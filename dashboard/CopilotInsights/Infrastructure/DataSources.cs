namespace CopilotInsights.Infrastructure;

/// <summary>Where a data source stands: loaded and current, loaded but late, its last load failed, or never loaded here.</summary>
public enum SourceState { Connected, Behind, Failed, NotConnected }

/// <summary>A data source as the Data sources page shows it: its last load, the newest day it holds, and what it opens.</summary>
public sealed record DataSource(
    string Key,
    string Name,
    string What,
    SourceState State,
    DateTime? LastUtc,
    DateTime? NewestDay,
    long? Rows,
    string RowsNoun,
    string? Detail,
    IReadOnlyList<string> Opens,
    string Needs,
    bool Optional);

/// <summary>
/// The data sources the ledger is built from, read from the tables each load writes: one entry a source, in the order a
/// newcomer reads them. Pure but for <see cref="ReadAsync"/>, for the tests. A source's table missing from the database
/// (an older install, or a site account not granted it) counts as never loaded, so the page never fails on one.
/// </summary>
public static class DataSources
{
    /// <summary>How long after a load a source counts as late, in hours, when no setting says otherwise.</summary>
    public const int DefaultFreshHours = 36;
    /// <summary>The agent workbook is exported by hand, often weekly: it is late after eight days.</summary>
    public const int AgentWorkbookFreshHours = 8 * 24;

    /// <summary>The state of a source from its last load. <paramref name="failed"/>: the last run recorded a failure.</summary>
    public static SourceState Classify(DateTime? lastUtc, bool failed, DateTime nowUtc, int freshHours) =>
        failed ? SourceState.Failed
        : lastUtc is null ? SourceState.NotConnected
        : nowUtc - lastUtc.Value > TimeSpan.FromHours(freshHours) ? SourceState.Behind
        : SourceState.Connected;

    public static string Label(SourceState s) => s switch
    {
        SourceState.Connected => "Connected",
        SourceState.Behind => "Late",
        SourceState.Failed => "Last load failed",
        _ => "Not connected",
    };

    public static string Css(SourceState s) => s switch
    {
        SourceState.Connected => "status-ok",
        SourceState.Behind => "status-warn",
        SourceState.Failed => "status-bad",
        _ => "muted",
    };

    /// <summary>Connected here: loaded at least once, whether current, late or failed since.</summary>
    public static bool IsConnected(DataSource s) => s.State != SourceState.NotConnected;

    /// <summary>
    /// The line at the top: how many sources are connected, which AI the costs cover, and what is not connected.
    /// </summary>
    public static string Coverage(IReadOnlyList<DataSource> sources, bool withCount = true)
    {
        var on = sources.Count(IsConnected);
        var off = sources.Where(s => !IsConnected(s)).Select(s => s.Name).ToList();
        var bill = new List<string>();
        if (sources.Any(s => s.Key == "audit" && IsConnected(s))) bill.AddRange(["Microsoft 365 Copilot", "Copilot Studio"]);
        if (sources.Any(s => s.Key == "powerplatform" && IsConnected(s))) bill.AddRange(["Power BI", "Power Platform"]);
        if (sources.Any(s => s.Key == "github" && IsConnected(s))) bill.Add("GitHub Copilot");
        if (sources.Any(s => s.Key == "foundry" && IsConnected(s))) bill.Add("Microsoft Foundry");
        var head = $"{on} of {sources.Count} sources connected. ";
        return withCount ? head + CoverageDetail(bill, off) : CoverageDetail(bill, off);
    }

    static string CoverageDetail(List<string> bill, List<string> off) =>
        (bill.Count == 0 ? "No AI spend is covered yet." : $"The AI spend covers {Join(bill)}.") + (off.Count == 0 ? "" : $" Not connected: {Join(off)}.");

    static string Join(IReadOnlyList<string> items) =>
        items.Count <= 1 ? string.Concat(items) : string.Join(", ", items.Take(items.Count - 1)) + " and " + items[^1];

    /* The sources, with what each opens (report paths, so the page lists only those the viewer may open, under their
       menu titles) and what one not connected needs. */
    sealed record Def(string Key, string Name, string What, string RowsNoun, string[] Opens, string Needs, bool Optional, int? FreshHours = null);

    static readonly Def[] Defs =
    [
        new("audit", "Microsoft Purview audit log", "Every Copilot and Copilot Studio interaction, who had it, in which app, and what it touched", "records added by the last load",
            ["/leadership", "/adoption", "/leadership/mix", "/security", "/compliance", "/agents", "/enablement", "/apps"],
            "AuditLogsQuery.Read.All for the app that reads the audit log (README). Every other source builds on it.", false),
        new("directory", "People directory", "People, their managers, departments, offices and job titles, from Active Directory", "people",
            ["/leadership/org", "/adoption/departments", "/workforce", "/regions"],
            "The nightly run's directory step, which reads Active Directory.", false),
        new("licenses", "Licenses (Microsoft Graph)", "Which license each person holds, and when each was assigned and removed", "license assignments",
            ["/licensing", "/licensing/unused", "/forecasting", "/leadership/costs"],
            "User.Read.All and LicenseAssignment.Read.All for the audit app, and the license step of the nightly run (tools\\Update-CopilotAuditEnrichment.ps1).", false),
        new("sessions", "Copilot sessions", "Interactions grouped into sessions, for how long and how deep people work with Copilot", "sessions written by the last run",
            ["/enablement/depth", "/enablement"],
            "Nothing new: the nightly run builds them from the audit log.", false),
        new("activity", "AI apps and other Microsoft 365 activity", "Calls to Azure AI Foundry and Azure OpenAI from the organization's own apps, and Power BI, Power Apps and Teams AI notes use, from the same audit log", "person-days",
            ["/apps", "/governance/ai-apps"],
            "An activity database named in the nightly run's settings (ActivityDatabase; docs/OPERATIONS.md).", true),
        new("powerplatform", "Power Platform usage", "Who uses Power Apps, Power Automate and Copilot Studio, and the tenant's Dataverse capacity", "people and products",
            ["/leadership/costs", "/licensing/suites"],
            "The Power Platform admin center's usage exports, or the activity database for Power BI and Power Apps (docs/OPERATIONS.md). No new permission.", true),
        new("agents", "Copilot Studio agent inventory", "Every agent made in the tenant, its owner and where it is published, from the Power Platform admin center's export", "agents",
            ["/agents/inventory", "/agents/owners", "/agents/checks"],
            "The agent workbook exported from the Power Platform admin center and loaded with tools\\Import-AgentWorkbook.ps1.", true, AgentWorkbookFreshHours),
        new("github", "GitHub Copilot", "GitHub Copilot seats, use and cost, by organization", "rows loaded by the last run",
            ["/platforms/github", "/platforms/github/seats", "/leadership/costs"],
            "A GitHub enterprise owner's read-only token for Copilot metrics and billing (docs/GITHUB-COPILOT-USAGE.md).", true),
        new("foundry", "Microsoft Foundry", "Foundry and Azure OpenAI model deployments, the tokens they used and their cost, by subscription", "rows loaded by the last run",
            ["/platforms/foundry", "/platforms/foundry/deployments", "/platforms/foundry/posture", "/leadership/costs"],
            "Monitoring Reader and Cost Management Reader on the subscriptions, for the audit app registration (docs/FOUNDRY-USAGE.md).", true),
    ];

    /// <summary>Each source's key and the report paths it opens, for the tests: every path must be a menu page.</summary>
    public static IEnumerable<(string Key, string Path)> OpenPaths => Defs.SelectMany(d => d.Opens.Select(p => (d.Key, p)));

    /// <summary>Every source, read from this database. <paramref name="freshHours"/>: the FreshnessHours setting.</summary>
    public static async Task<List<DataSource>> ReadAsync(Db db, int freshHours, DateTime nowUtc)
    {
        var list = new List<DataSource>();
        foreach (var d in Defs)
        {
            var (last, newest, rows, failed, detail) = await ReadOneAsync(db, d.Key);
            if (d.Key == "foundry" && await OutsideAsync(db) is { } outside) detail = detail is null ? outside : detail + " " + outside;
            list.Add(new(d.Key, d.Name, d.What, Classify(last, failed, nowUtc, d.FreshHours ?? freshHours), last, newest, rows, d.RowsNoun, detail, d.Opens, d.Needs, d.Optional));
        }
        return list;
    }

    /// <summary>
    /// The AI accounts the Foundry load saw in subscriptions it does not read (audit.FoundryAccountSeen, 3.3.0), in
    /// words; null when there are none, or before the load has looked.
    /// </summary>
    static async Task<string?> OutsideAsync(Db db)
    {
        try
        {
            var r = (await db.QueryAsync<(int Accounts, int Subscriptions)>(@"
IF OBJECT_ID(N'audit.FoundryAccountSeen', N'U') IS NULL BEGIN SELECT 0, 0; RETURN; END;
SELECT COUNT(*), COUNT(DISTINCT SubscriptionId) FROM audit.FoundryAccountSeen WHERE Loaded = 0;")).Single();
            return OutsideText(r.Accounts, r.Subscriptions);
        }
        catch (Microsoft.Data.SqlClient.SqlException) { return null; }
    }

    /// <summary>The line about AI accounts outside the subscriptions loaded: their usage and cost are not counted. Pure, for the tests.</summary>
    public static string? OutsideText(int accounts, int subscriptions) => accounts <= 0 ? null
        : $"{accounts} more AI account{(accounts == 1 ? "" : "s")} in {subscriptions} subscription{(subscriptions == 1 ? "" : "s")} not loaded: their usage and cost are not counted (add them to FoundrySubscriptions).";

    sealed class One
    {
        public DateTime? LastUtc { get; set; }
        public DateTime? NewestDay { get; set; }
        public long? Rows { get; set; }
        public bool Failed { get; set; }
        public string? Detail { get; set; }
    }

    /* Each source's last load from the table it writes. A table that is missing, or one the site cannot read, is a source
       never loaded here. Counts read small tables only: the audit log's own size is never counted (160 million rows). */
    static async Task<(DateTime? Last, DateTime? Newest, long? Rows, bool Failed, string? Detail)> ReadOneAsync(Db db, string key)
    {
        var sql = key switch
        {
            "audit" => @"
SELECT COALESCE((SELECT MAX(MergedUtc) FROM audit.CopilotLoad), (SELECT MAX(EndedUtc) FROM bi.RefreshRun)) AS LastUtc,
       (SELECT CAST(CONVERT(CHAR(8), MAX(DateKey)) AS DATE) FROM dash.UserDay) AS NewestDay,
       (SELECT TOP (1) CAST(RowsAdded AS BIGINT) FROM audit.CopilotLoad WHERE MergedUtc IS NOT NULL ORDER BY MergedUtc DESC) AS Rows,
       CAST(0 AS BIT) AS Failed, CAST(NULL AS NVARCHAR(400)) AS Detail;",
            "directory" => "SELECT MAX(LoadedUtc) AS LastUtc, CAST(NULL AS DATE) AS NewestDay, COUNT_BIG(*) AS Rows, CAST(0 AS BIT) AS Failed, CAST(NULL AS NVARCHAR(400)) AS Detail FROM audit.UserDirectory;",
            "licenses" => "SELECT MAX(CapturedUtc) AS LastUtc, CAST(NULL AS DATE) AS NewestDay, COUNT_BIG(*) AS Rows, CAST(0 AS BIT) AS Failed, CAST(NULL AS NVARCHAR(400)) AS Detail FROM audit.UserLicenseSku;",
            "sessions" => "SELECT TOP (1) RunEndUtc AS LastUtc, CAST(NULL AS DATE) AS NewestDay, CAST(SessionsWritten AS BIGINT) AS Rows, CAST(0 AS BIT) AS Failed, CAST(NULL AS NVARCHAR(400)) AS Detail FROM audit.CopilotSessionRun ORDER BY RunId DESC;",
            "activity" => "SELECT MAX(LoadedUtc) AS LastUtc, MAX([Day]) AS NewestDay, COUNT_BIG(*) AS Rows, CAST(0 AS BIT) AS Failed, CAST(NULL AS NVARCHAR(400)) AS Detail FROM audit.ActivityUserDay;",
            "powerplatform" => "SELECT MAX(LoadedUtc) AS LastUtc, CAST(NULL AS DATE) AS NewestDay, COUNT_BIG(*) AS Rows, CAST(0 AS BIT) AS Failed, CAST(NULL AS NVARCHAR(400)) AS Detail FROM audit.PowerPlatformUserActivity;",
            "agents" => @"
SELECT s.EndedUtc AS LastUtc, CAST(NULL AS DATE) AS NewestDay, CAST(s.InventoryRows AS BIGINT) AS Rows,
       CAST(CASE WHEN f.RunId IS NOT NULL THEN 1 ELSE 0 END AS BIT) AS Failed,
       CAST(CASE WHEN f.RunId IS NOT NULL THEN N'The last load failed; the agents shown are from the load before.' END AS NVARCHAR(400)) AS Detail
FROM (SELECT TOP (1) RunId, EndedUtc, InventoryRows FROM dash.AgentInventoryRun WHERE Status = N'Succeeded' ORDER BY RunId DESC) AS s
OUTER APPLY (SELECT TOP (1) x.RunId FROM dash.AgentInventoryRun AS x WHERE x.RunId > s.RunId AND x.Status = N'Failed' ORDER BY x.RunId DESC) AS f;",
            "github" or "foundry" => $@"
SELECT TOP (1) COALESCE(r.EndedUtc, r.StartedUtc) AS LastUtc,
       (SELECT MAX([Day]) FROM {(key == "github" ? "audit.GitHubCopilotDay" : "audit.FoundryUsageDay")}) AS NewestDay,
       CAST(r.RowsWritten AS BIGINT) AS Rows,
       CAST(CASE WHEN r.Status IN (N'Succeeded', N'Partial', N'Running') THEN 0 ELSE 1 END AS BIT) AS Failed,
       CAST(CASE r.Status WHEN N'Partial' THEN N'The last load got only a part of the data.' WHEN N'Running' THEN N'A load is running.' END AS NVARCHAR(400)) AS Detail
FROM audit.ExternalUsageRun AS r WHERE r.Source = N'{(key == "github" ? ExternalUsage.GitHub : ExternalUsage.Foundry)}' ORDER BY r.RunId DESC;",
            _ => throw new ArgumentOutOfRangeException(nameof(key)),
        };
        try
        {
            var r = (await db.QueryAsync<One>(sql)).FirstOrDefault();
            if (r is null || (r.LastUtc is null && !r.Failed)) return (null, null, null, false, null);
            return (r.LastUtc, r.NewestDay, r.Rows, r.Failed, r.Detail);
        }
        catch (Microsoft.Data.SqlClient.SqlException) { return (null, null, null, false, null); }
    }
}
