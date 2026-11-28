using Governance.Application.Queries;
using Microsoft.Extensions.Caching.Memory;

namespace Governance.Web.Infrastructure;

/// <summary>
/// The tracking group and manager hierarchy audit: is each Copilot seat holder in the tracking group their manager chain
/// says? Each tracking group names the leader its members report up to and that leader's level under the top of the
/// organization (Hierarchy mapping). A holder passes when the person at that level in their chain is the expected leader.
/// The audit only recommends: it never changes a group or a license.
/// </summary>
public static class SeatAuditRules
{
    public const string Pass = "PASS";
    public const string NewHire = "NEW_HIRE";
    public const string IncorrectGroup = "REVIEW_INCORRECT_GROUP";
    public const string DifferentLevel = "REVIEW_DIFFERENT_LEVEL";
    public const string Mismatch = "REVIEW_HIERARCHY_MISMATCH";
    public const string UnknownGroup = "REVIEW_UNKNOWN_GROUP";
    public const string MissingData = "REVIEW_MISSING_DATA";

    public static readonly IReadOnlyList<string> Reviews = [IncorrectGroup, DifferentLevel, Mismatch, UnknownGroup, MissingData];

    public static string Label(string status) => status switch
    {
        Pass => "PASS",
        NewHire => "NEW HIRE - No Investigation Required",
        IncorrectGroup => "REVIEW - Possible Incorrect Tracking Group",
        DifferentLevel => "REVIEW - Expected Leader at Different Hierarchy Level",
        Mismatch => "REVIEW - Hierarchy Mismatch",
        UnknownGroup => "REVIEW - Unknown Tracking Group",
        MissingData => "REVIEW - Missing Data",
        _ => status,
    };

    public static string Definition(string status) => status switch
    {
        Pass => "The person at the group's expected level in the holder's manager chain is the expected leader.",
        NewHire => "Hired within the new-hire days (Seats.NewHireDays), or manager and hierarchy both blank with no hire date saying otherwise: hierarchy validation deferred, no investigation now.",
        IncorrectGroup => "Another group's designated leader (not the current one) appears in the holder's chain at the level that group requires; the deepest such group is suggested.",
        DifferentLevel => "The expected leader is in the holder's chain, but not at the required level.",
        Mismatch => "The expected leader is not in the holder's chain, and no other mapped leader fits.",
        UnknownGroup => "The holder's tracking group has no expected leader on Hierarchy mapping.",
        MissingData => "The holder or their manager chain is not in the directory, or does not reach the required level (the top of the organization excepted).",
        _ => "",
    };

    public static bool IsReview(string status) => status.StartsWith("REVIEW", StringComparison.Ordinal);

    /// <summary>A person in the directory: their level under the top (1 = the top), null when not under it.</summary>
    public sealed record Person(string Upn, string? Name, int? Level);

    /// <summary>A tracking group's mapping.</summary>
    public sealed record Mapping(Guid Group, string GroupName, string? LobName, string? LeaderUpn, int? Level);

    /// <summary>One holder's result.</summary>
    public sealed record Result(string Status, Person? AtLevel, Mapping? Potential);

    /// <summary>
    /// One holder in one group. <paramref name="chain"/> is the holder then each manager up to the top (empty when the
    /// holder is not in the directory). Pure, for the tests.
    /// </summary>
    public static Result Evaluate(Mapping group, IReadOnlyList<Person> chain, DateTime? hired, DateTime today, int newHireDays, IReadOnlyList<Mapping> all)
    {
        if (hired is DateTime h && h.Date > today.AddDays(-newHireDays) && h.Date <= today) return new(NewHire, null, null);
        // The workbook's new-hire test: manager and hierarchy both blank (in the directory, with no manager above them and
        // no level), unless the hire date shows the person joined long ago: then their record is missing data, not new.
        if (chain.Count == 1 && chain[0].Level is null && hired is null) return new(NewHire, null, null);
        if (string.IsNullOrWhiteSpace(group.LeaderUpn) || group.Level is null) return new(UnknownGroup, null, null);
        if (chain.Count == 0 || chain[0].Level is null) return new(MissingData, null, null);
        if (chain[0].Level == 1) return new(Pass, chain[0], null);               // the top of the organization
        var level = group.Level.Value;
        var at = chain.FirstOrDefault(p => p.Level == level);
        if (at is null) return new(chain[0].Level < level ? Mismatch : MissingData, null, null);
        if (Same(at.Upn, group.LeaderUpn)) return new(Pass, at, null);
        var other = all.Where(m => m.Group != group.Group && !Same(m.LeaderUpn, group.LeaderUpn) && m.Level is int l && !string.IsNullOrWhiteSpace(m.LeaderUpn)
                                   && chain.Any(p => p.Level == l && Same(p.Upn, m.LeaderUpn)))
            .OrderByDescending(m => m.Level).ThenBy(m => m.GroupName, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
        if (other is not null) return new(IncorrectGroup, at, other);
        if (chain.Any(p => Same(p.Upn, group.LeaderUpn))) return new(DifferentLevel, at, null);
        return new(Mismatch, at, null);
    }

    static bool Same(string? a, string? b) => string.Equals(a?.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);
}

/// <summary>One seat holder in one tracking group, audited.</summary>
public sealed class SeatAuditRow
{
    public Guid MemberObjectId { get; init; }
    public string? Upn { get; init; }
    public string Name { get; init; } = "";
    public string GroupName { get; init; } = "";
    public Guid GroupPublicId { get; init; }
    public string? LobName { get; init; }
    public string? ExpectedLeaderUpn { get; init; }
    public string? ExpectedLeaderName { get; init; }
    public int? ExpectedLevel { get; init; }
    public string? ActualAtLevelName { get; init; }
    public string? ActualAtLevelUpn { get; init; }
    public string Status { get; init; } = "";
    public string? PotentialGroup { get; init; }
    public string? PotentialLeaderName { get; init; }
    public DateTime? HireDate { get; init; }
    /// <summary>The manager chain from the top down, as names.</summary>
    public string? Chain { get; init; }
}

/// <summary>The audit of every seat holder, read when asked and kept five minutes on each web server.</summary>
public sealed class SeatAudit(SeatQueries seats, CopilotInsights.Infrastructure.Db insights, IMemoryCache cache, ILogger<SeatAudit> log)
{
    const string CacheKey = "seat-hierarchy-audit";
    public static void Forget(IMemoryCache cache) => cache.Remove(CacheKey);

    public sealed record Outcome(IReadOnlyList<SeatAuditRow> Rows, DateTime TakenUtc, int NewHireDays, bool DirectoryRead, IReadOnlyList<SeatAuditRules.Mapping> Mappings);

    public async Task<Outcome> GetAsync()
    {
        if (cache.TryGetValue(CacheKey, out Outcome? known) && known is not null) return known;
        var settings = await seats.SeatSettingRowsAsync();
        var newHireDays = int.TryParse(settings.FirstOrDefault(s => s.SettingKey == "Seats.NewHireDays")?.SettingValue, out var d) ? d : 90;
        var members = await seats.AuditPopulationAsync();
        var mapping = await seats.MappingAsync();
        var maps = mapping.Select(m => new SeatAuditRules.Mapping(m.PublicId, m.GroupName, m.LobName, m.ExpectedLeaderUpn, m.ExpectedLevel)).ToList();

        var upns = members.Select(m => m.UserPrincipalName).Concat(maps.Select(m => m.LeaderUpn))
            .Where(u => !string.IsNullOrWhiteSpace(u)).Select(u => u!.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var chains = new Dictionary<string, List<SeatAuditRules.Person>>(StringComparer.OrdinalIgnoreCase);
        var hired = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        var read = true;
        try
        {
            var json = System.Text.Json.JsonSerializer.Serialize(upns);
            foreach (var r in await insights.QueryAsync<(string Root, string Upn, string? DisplayName, int? Level, int Depth)>(ChainSqlChains, new { upns = json }, 600))
            {
                if (!chains.TryGetValue(r.Root, out var list)) chains[r.Root] = list = [];
                list.Add(new(r.Upn, r.DisplayName, r.Level));
            }
            foreach (var r in await insights.QueryAsync<(string Upn, DateTime? Hired)>(HireSql, new { upns = json }, 600))
                if (r.Hired is DateTime h) hired[r.Upn] = h;
        }
        catch (Exception ex)
        {
            read = false;
            Incident.Note(log, ex, "reading manager chains from Copilot Insights' directory for the hierarchy audit");
        }

        var today = DateTime.UtcNow.Date;
        string? NameOf(string? upn) => upn is not null && chains.TryGetValue(upn, out var c) && c.Count > 0 ? c[0].Name : null;
        var rows = new List<SeatAuditRow>(members.Count);
        foreach (var m in members)
        {
            var group = maps.FirstOrDefault(x => x.Group == m.GroupPublicId) ?? new(m.GroupPublicId, m.GroupName, m.LobName, m.ExpectedLeaderUpn, m.ExpectedLevel);
            var chain = m.UserPrincipalName is { } u && chains.TryGetValue(u, out var c) ? c : [];
            DateTime? hire = m.UserPrincipalName is { } hu && hired.TryGetValue(hu, out var hd) ? hd : null;
            var r = SeatAuditRules.Evaluate(group, chain, hire, today, newHireDays, maps);
            rows.Add(new SeatAuditRow
            {
                MemberObjectId = m.MemberObjectId, Upn = m.UserPrincipalName,
                Name = m.DisplayName ?? (chain.Count > 0 ? chain[0].Name : null) ?? m.UserPrincipalName ?? m.MemberObjectId.ToString("D"),
                GroupName = m.GroupName, GroupPublicId = m.GroupPublicId, LobName = m.LobName,
                ExpectedLeaderUpn = group.LeaderUpn, ExpectedLeaderName = NameOf(group.LeaderUpn) ?? group.LeaderUpn, ExpectedLevel = group.Level,
                ActualAtLevelName = r.AtLevel?.Name ?? r.AtLevel?.Upn, ActualAtLevelUpn = r.AtLevel?.Upn,
                Status = r.Status, PotentialGroup = r.Potential?.GroupName, PotentialLeaderName = r.Potential is null ? null : NameOf(r.Potential.LeaderUpn) ?? r.Potential.LeaderUpn,
                HireDate = hire,
                Chain = chain.Count == 0 ? null : string.Join(" > ", chain.AsEnumerable().Reverse().Select(p => p.Name ?? p.Upn)),
            });
        }
        var outcome = new Outcome(rows.OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToList(), DateTime.UtcNow, newHireDays, read, maps);
        cache.Set(CacheKey, outcome, TimeSpan.FromMinutes(5));
        return outcome;
    }

    const string ChainSqlChains = """
        DECLARE @t TABLE (Upn nvarchar(256) NOT NULL PRIMARY KEY);
        INSERT @t (Upn) SELECT DISTINCT LEFT(j.value, 256) FROM OPENJSON(@upns) AS j WHERE j.value IS NOT NULL AND j.value <> N'';
        WITH c AS (
            SELECT t.Upn AS Root, p.Upn, p.DisplayName, p.ManagerUpn, p.Level, 0 AS Depth
            FROM @t AS t JOIN dash.Person AS p ON p.Upn = t.Upn
            UNION ALL
            SELECT c.Root, m.Upn, m.DisplayName, m.ManagerUpn, m.Level, c.Depth + 1
            FROM c JOIN dash.Person AS m ON m.Upn = c.ManagerUpn
            WHERE c.Depth < 20 AND c.ManagerUpn <> c.Upn)
        SELECT Root, Upn, DisplayName, CAST(Level AS int) AS Level, Depth FROM c ORDER BY Root, Depth OPTION (MAXRECURSION 25);
        """;

    /// <summary>Hire dates from the license load, where it recorded them.</summary>
    const string HireSql = """
        IF COL_LENGTH(N'audit.CopilotLicense', N'EmployeeHireDate') IS NULL
            SELECT CAST(NULL AS nvarchar(256)) AS Upn, CAST(NULL AS date) AS Hired WHERE 1 = 0;
        ELSE EXEC sp_executesql N'
            SELECT l.UserPrincipalName AS Upn, CAST(l.EmployeeHireDate AS datetime2) AS Hired
            FROM audit.CopilotLicense AS l
            WHERE l.EmployeeHireDate IS NOT NULL AND l.UserPrincipalName IN (SELECT j.value FROM OPENJSON(@upns) AS j)',
            N'@upns nvarchar(max)', @upns = @upns;
        """;
}

/// <summary>
/// Sorting a list held in memory by a heading's key (?sort=key&amp;desc=true), before it is paged, so the order is the
/// whole list's. Text compares without case; empty values go last either way.
/// </summary>
public static class ListSort
{
    public static IEnumerable<T> By<T>(IEnumerable<T> rows, string? sort, bool desc, IReadOnlyDictionary<string, Func<T, IComparable?>> keys)
    {
        if (string.IsNullOrEmpty(sort) || !keys.TryGetValue(sort, out var key)) return rows;
        var withValue = rows.Select(r => (Row: r, Key: key(r))).ToList();
        var known = withValue.Where(x => x.Key is not null);
        var ordered = desc ? known.OrderByDescending(x => x.Key, Comparer) : known.OrderBy(x => x.Key, Comparer);
        return ordered.Concat(withValue.Where(x => x.Key is null)).Select(x => x.Row);
    }

    static readonly IComparer<IComparable?> Comparer = Comparer<IComparable?>.Create((a, b) =>
        a is string x && b is string y ? string.Compare(x, y, StringComparison.OrdinalIgnoreCase) : Comparer<IComparable?>.Default.Compare(a, b));
}
