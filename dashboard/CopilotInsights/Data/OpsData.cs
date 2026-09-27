using CopilotInsights.Infrastructure;

namespace CopilotInsights.Data;

public sealed class PipelineRunRow
{
    public int RunId { get; set; }
    public string Kind { get; set; } = "";
    public DateTime StartedUtc { get; set; }
    public DateTime? EndedUtc { get; set; }
    public string Status { get; set; } = "";
    public string? FailedStep { get; set; }
    public string? Detail { get; set; }
    public string? Host { get; set; }
}

public sealed class PoolRow
{
    public string SkuPartNumber { get; set; } = "";
    public Guid SkuId { get; set; }
    public bool IsCopilot { get; set; }
    public int Enabled { get; set; }
    public int Warning { get; set; }
    public int Suspended { get; set; }
    public int Consumed { get; set; }
    public DateTime CapturedUtc { get; set; }
    public int Bought => Enabled + Warning;
    public int Available => Bought - Consumed;
}

public sealed class LeaverRow
{
    public string UserPrincipalName { get; set; } = "";
    public string? DisplayName { get; set; }
    public string? Department { get; set; }
    public string? ManagerName { get; set; }
    public string Reason { get; set; } = "";
    public DateTime? LicensedSinceUtc { get; set; }
    public DateTime? LastUsed { get; set; }
    public int TotalRows { get; set; }
}

/// <summary>The queries behind the operations pages, the alerts and the digests.</summary>
public sealed class OpsData(Db db, Settings settings)
{
    public async Task<bool> HasAsync(string table) =>
        await db.ScalarAsync<int>("SELECT CASE WHEN OBJECT_ID(@t, N'U') IS NULL THEN 0 ELSE 1 END", new { t = table }) == 1;

    public async Task<List<PipelineRunRow>> PipelineRunsAsync(int top = 10) =>
        await HasAsync("audit.PipelineRun")
            ? await db.QueryAsync<PipelineRunRow>("SELECT TOP (@top) RunId, Kind, StartedUtc, EndedUtc, Status, FailedStep, Detail, Host FROM audit.PipelineRun ORDER BY RunId DESC;", new { top })
            : [];

    /// <summary>When the dashboard's rollups last finished, and how old that is in hours.</summary>
    public async Task<(DateTime? LastUtc, double? AgeHours, int Limit)> FreshnessAsync()
    {
        var last = await db.ScalarAsync<DateTime?>("SELECT MAX(EndedUtc) FROM dash.RefreshRun;");
        var limit = await settings.IntAsync("FreshnessHours", 36);
        return (last, last is null ? null : (DateTime.UtcNow - last.Value).TotalHours, limit);
    }

    /// <summary>The last complete day in the rollups, and the security signals on it against their 28-day averages.</summary>
    public async Task<(DateTime? Day, List<SignalDay> Signals)> SecurityDayAsync()
    {
        var end = await db.ScalarAsync<int?>(@"
SELECT COALESCE((SELECT MAX(DateKey) FROM dash.UserDay WHERE DateKey < YEAR(SYSDATETIME()) * 10000 + MONTH(SYSDATETIME()) * 100 + DAY(SYSDATETIME())),
                (SELECT MAX(DateKey) FROM dash.UserDay));");
        if (end is not int k) return (null, []);
        var day = new DateTime(k / 10000, k / 100 % 100, k % 100);
        var k0 = ReportContext.Key(day.AddDays(-28));
        var rows = (await db.QueryAsync<(long J, long I, long B, double Ja, double Ia, double Ba)>(@"
SELECT SUM(CASE WHEN DateKey = @k THEN CAST(Jailbreak AS BIGINT) ELSE 0 END), SUM(CASE WHEN DateKey = @k THEN CAST(PromptInjection AS BIGINT) ELSE 0 END),
       SUM(CASE WHEN DateKey = @k THEN CAST(BlockedResource AS BIGINT) ELSE 0 END),
       SUM(CASE WHEN DateKey < @k THEN CAST(Jailbreak AS BIGINT) ELSE 0 END) / 28.0, SUM(CASE WHEN DateKey < @k THEN CAST(PromptInjection AS BIGINT) ELSE 0 END) / 28.0,
       SUM(CASE WHEN DateKey < @k THEN CAST(BlockedResource AS BIGINT) ELSE 0 END) / 28.0
FROM dash.UserDay WHERE DateKey BETWEEN @k0 AND @k;", new { k, k0 })).Single();
        var list = new List<SignalDay>
        {
            new("Jailbreak attempts", rows.J, rows.Ja, await settings.IntAsync("AlertJailbreakPerDay", 0)),
            new("Prompt injections", rows.I, rows.Ia, await settings.IntAsync("AlertInjectionPerDay", 0)),
            new("Interactions with a blocked resource", rows.B, rows.Ba, await settings.IntAsync("AlertBlockedPerDay", 0)),
        };
        if (int.TryParse(await settings.StringAsync("AlertLabelRank"), out var rank))
        {
            var lab = (await db.QueryAsync<(long N, double Avg)>(@"
SELECT ISNULL(SUM(CASE WHEN rd.DateKey = @k THEN CAST(rd.Interactions AS BIGINT) END), 0), ISNULL(SUM(CASE WHEN rd.DateKey < @k THEN CAST(rd.Interactions AS BIGINT) END), 0) / 28.0
FROM dash.ResourceDay AS rd JOIN dash.SensitivityLabel AS sl ON sl.LabelId = rd.Item AND sl.Rank >= @rank
WHERE rd.Kind = 'Label' AND rd.DateKey BETWEEN @k0 AND @k;", new { k, k0, rank })).Single();
            list.Add(new($"Interactions touching content labeled at order {rank} or above", lab.N, lab.Avg, await settings.IntAsync("AlertLabelPerDay", 1)));
        }
        return (day, list);
    }

    /// <summary>Interactions per department per day, zeros filled in, for the drop and spike checks.</summary>
    public async Task<Dictionary<string, IReadOnlyDictionary<DateTime, long>>> DepartmentDaysAsync(DateTime from, DateTime to, int minPeople, string? root = null)
    {
        var scope = root is null ? "" : "JOIN dash.fnScopeUsers(@root) AS sc ON sc.UserKey = u.UserKey";
        var rows = await db.QueryAsync<(string Dept, DateTime Day, long N)>($@"
WITH dept AS (SELECT ISNULL(NULLIF(p.Department, N''), N'(no department)') AS Dept, u.UserKey
              FROM dash.Person AS p JOIN bi.DimUser AS u ON u.UserPrincipalName = p.Upn {scope} WHERE p.AccountEnabled = 1),
big AS (SELECT Dept FROM dept GROUP BY Dept HAVING COUNT(*) >= @minPeople)
SELECT d.Dept, dd.[Date], SUM(CAST(ud.Interactions AS BIGINT))
FROM dash.UserDay AS ud JOIN dept AS d ON d.UserKey = ud.UserKey JOIN big ON big.Dept = d.Dept
JOIN bi.DimDate AS dd ON dd.DateKey = ud.DateKey
WHERE ud.DateKey BETWEEN @k0 AND @k1
GROUP BY d.Dept, dd.[Date];", new { k0 = ReportContext.Key(from), k1 = ReportContext.Key(to), minPeople, root });
        var result = new Dictionary<string, IReadOnlyDictionary<DateTime, long>>();
        foreach (var g in rows.GroupBy(r => r.Dept))
        {
            var map = new Dictionary<DateTime, long>();
            for (var d = from.Date; d <= to.Date; d = d.AddDays(1)) map[d] = 0;
            foreach (var r in g) map[r.Day.Date] = r.N;
            result[g.Key] = map;
        }
        return result;
    }

    /// <summary>The license pool now: every SKU, Copilot ones first.</summary>
    public async Task<List<PoolRow>> PoolAsync() =>
        await HasAsync("audit.LicenseSku")
            ? await db.QueryAsync<PoolRow>("SELECT SkuPartNumber, SkuId, IsCopilot, Enabled, Warning, Suspended, Consumed, CapturedUtc FROM audit.LicenseSku ORDER BY IsCopilot DESC, SkuPartNumber;")
            : [];

    /// <summary>Copilot licenses held today by accounts that are disabled (in AD or Entra) or gone from the directory.</summary>
    public async Task<List<LeaverRow>> LeaversAsync(string? root = null, string? q = null, string orderAndPage = "ORDER BY u.DisplayName OFFSET 0 ROWS")
    {
        var hasEntra = await db.ScalarAsync<int>("SELECT CASE WHEN COL_LENGTH(N'audit.CopilotLicense', N'EntraAccountEnabled') IS NULL THEN 0 ELSE 1 END") == 1;
        var entra = hasEntra ? "cl.EntraAccountEnabled" : "CAST(NULL AS BIT)";
        var scope = root is null ? "" : "JOIN dash.fnScopeUsers(@root) AS sc ON sc.UserKey = u.UserKey";
        return await db.QueryAsync<LeaverRow>($@"
WITH today AS (SELECT MAX(DateKey) AS K FROM bi.FactLicensedDay)
SELECT u.UserPrincipalName, u.DisplayName, u.Department, u.ManagerName,
       CASE WHEN u.DirectoryStatus = N'NotFound' OR cl.DirectoryStatus = N'NotFound' THEN N'Gone from the directory'
            WHEN {entra} = 0 AND u.AccountEnabled = 0 THEN N'Disabled in AD and Entra'
            WHEN {entra} = 0 THEN N'Disabled in Entra'
            ELSE N'Disabled in AD' END AS Reason,
       u.LicensedSinceUtc,
       (SELECT dl.[Date] FROM bi.DimDate AS dl WHERE dl.DateKey = (SELECT MAX(x.DateKey) FROM dash.UserDay AS x WHERE x.UserKey = u.UserKey AND x.Interactions > 0)) AS LastUsed,
       COUNT(*) OVER () AS TotalRows
FROM bi.FactLicensedDay AS ld
JOIN today ON ld.DateKey = today.K
JOIN bi.DimUser AS u ON u.UserKey = ld.UserKey {scope}
LEFT JOIN audit.CopilotLicense AS cl ON cl.UserPrincipalName = u.UserPrincipalName
WHERE (u.AccountEnabled = 0 OR u.DirectoryStatus = N'NotFound' OR cl.DirectoryStatus = N'NotFound' OR {entra} = 0)
  AND (@q IS NULL OR u.DisplayName LIKE @q OR u.UserPrincipalName LIKE @q OR u.Department LIKE @q)
{orderAndPage};", new { root, q });
    }

    /// <summary>Agents first used after the given moment.</summary>
    public async Task<List<(string Name, string Source, DateTime FirstSeenUtc)>> AgentsSinceAsync(DateTime sinceUtc) =>
        await db.QueryAsync<(string, string, DateTime)>(
            "SELECT AgentName, AgentSource, FirstSeenUtc FROM dash.Agent WHERE AgentSource <> N'(no agent)' AND FirstSeenUtc > @sinceUtc ORDER BY FirstSeenUtc;", new { sinceUtc });

    /// <summary>The reporting time zone, as the pipeline cuts days in.</summary>
    public async Task<TimeZoneInfo> ZoneAsync()
    {
        string? name = null;
        try { name = await db.ScalarAsync<string?>("SELECT audit.ReportingTimeZone();"); } catch (Microsoft.Data.SqlClient.SqlException) { }
        return Schedule.Zone(name ?? "Eastern Standard Time");
    }
}
