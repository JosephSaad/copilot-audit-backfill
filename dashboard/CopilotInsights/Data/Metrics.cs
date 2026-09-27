using CopilotInsights.Infrastructure;

namespace CopilotInsights.Data;

/// <summary>One trend point: a day or a week.</summary>
public sealed class TrendRow
{
    public DateTime Bucket { get; set; }
    public int Active { get; set; }
    public int ActiveLicensed { get; set; }
    public int ActiveUnlicensed { get; set; }
    public long Interactions { get; set; }
    public long Prompts { get; set; }
    public long Sessions { get; set; }
    public long MultiTurn { get; set; }
    public long Chat { get; set; }
    public long Agent { get; set; }
    public long Web { get; set; }
    public long Jailbreak { get; set; }
    public long Injection { get; set; }
    public long Blocked { get; set; }
    public long Labelled { get; set; }
    public int Licensed { get; set; }   // holding a licence at the end of the day or week
}

public sealed class Totals
{
    public int People { get; set; }            // enabled accounts in the directory, in scope
    public int LicensedNow { get; set; }       // holding a licence on the last day
    public int Active { get; set; }            // used Copilot in the period
    public int ActiveLicensed { get; set; }    // used it while licensed
    public int EverActive { get; set; }        // used it at any time
    public long Interactions { get; set; }
    public long Prompts { get; set; }
    public long Sessions { get; set; }
    public long MultiTurn { get; set; }
    public long ActiveDays { get; set; }       // person-days of use
    public int ActiveLast7 { get; set; }
    public int MultiTurnUsers { get; set; }
}

public sealed class UnitRow
{
    public string Unit { get; set; } = "";     // the manager's UPN, a division, or ~direct / ~small
    public string Name { get; set; } = "";
    public string? Department { get; set; }
    public int People { get; set; }
    public int Licensed { get; set; }
    public int Active { get; set; }
    public int ActiveLicensed { get; set; }
    public long Interactions { get; set; }
    public long ActiveDays { get; set; }
    public long Sessions { get; set; }
    public long MultiTurn { get; set; }
    public long Flags { get; set; }
    public long Labelled { get; set; }
    public bool Drillable => !Unit.StartsWith('~') && Unit.Contains('@');
}

/// <summary>The measures most pages share, always through the report's scope.</summary>
public sealed class Metrics(Db db)
{
    public async Task<List<TrendRow>> TrendAsync(ReportContext c)
    {
        var rows = await db.QueryAsync<TrendRow>($@"
WITH b AS (SELECT d.DateKey, {c.BucketSql("d")} AS Bucket FROM bi.DimDate AS d WHERE d.DateKey BETWEEN @k0 AND @k1),
ends AS (SELECT Bucket, MAX(DateKey) AS EndKey FROM b GROUP BY Bucket),
ud AS (SELECT b.Bucket, ud.* FROM dash.UserDay AS ud {c.Scope("ud.UserKey")} JOIN b ON b.DateKey = ud.DateKey),
per AS (SELECT Bucket, UserKey, SUM(Interactions) AS I, SUM(LicensedUse) AS L FROM ud GROUP BY Bucket, UserKey),
people AS (SELECT Bucket, COUNT(CASE WHEN I > 0 THEN 1 END) AS Active, COUNT(CASE WHEN L > 0 THEN 1 END) AS ActiveLicensed,
                  COUNT(CASE WHEN I > 0 AND L = 0 THEN 1 END) AS ActiveUnlicensed FROM per GROUP BY Bucket),
sums AS (SELECT Bucket, SUM(CAST(Interactions AS BIGINT)) AS Interactions, SUM(CAST(Prompts AS BIGINT)) AS Prompts,
                SUM(CAST(Sessions AS BIGINT)) AS Sessions, SUM(CAST(MultiTurnSessions AS BIGINT)) AS MultiTurn,
                SUM(CAST(ChatInteractions AS BIGINT)) AS Chat, SUM(CAST(AgentInteractions AS BIGINT)) AS Agent,
                SUM(CAST(WebGrounded AS BIGINT)) AS Web, SUM(CAST(Jailbreak AS BIGINT)) AS Jailbreak,
                SUM(CAST(PromptInjection AS BIGINT)) AS Injection, SUM(CAST(BlockedResource AS BIGINT)) AS Blocked,
                SUM(CAST(LabelledContent AS BIGINT)) AS Labelled
         FROM ud GROUP BY Bucket),
lic AS (SELECT e.Bucket, COUNT(*) AS Licensed FROM ends AS e JOIN bi.FactLicensedDay AS ld ON ld.DateKey = e.EndKey {c.Scope("ld.UserKey")} GROUP BY e.Bucket)
SELECT e.Bucket, ISNULL(p.Active, 0) AS Active, ISNULL(p.ActiveLicensed, 0) AS ActiveLicensed, ISNULL(p.ActiveUnlicensed, 0) AS ActiveUnlicensed,
       ISNULL(s.Interactions, 0) AS Interactions, ISNULL(s.Prompts, 0) AS Prompts, ISNULL(s.Sessions, 0) AS Sessions,
       ISNULL(s.MultiTurn, 0) AS MultiTurn, ISNULL(s.Chat, 0) AS Chat, ISNULL(s.Agent, 0) AS Agent, ISNULL(s.Web, 0) AS Web,
       ISNULL(s.Jailbreak, 0) AS Jailbreak, ISNULL(s.Injection, 0) AS Injection, ISNULL(s.Blocked, 0) AS Blocked,
       ISNULL(s.Labelled, 0) AS Labelled, ISNULL(l.Licensed, 0) AS Licensed
FROM ends AS e
LEFT JOIN people AS p ON p.Bucket = e.Bucket
LEFT JOIN sums AS s ON s.Bucket = e.Bucket
LEFT JOIN lic AS l ON l.Bucket = e.Bucket
ORDER BY e.Bucket;", c.Args());
        /* One row per trend point, in order, even where nothing happened. */
        return c.Buckets().Select(bk => rows.FirstOrDefault(r => r.Bucket == bk.Key) ?? new TrendRow { Bucket = bk.Key }).ToList();
    }

    public async Task<Totals> TotalsAsync(ReportContext c) =>
        (await db.QueryAsync<Totals>($@"
WITH u AS (
    SELECT ud.UserKey, SUM(ud.Interactions) AS I, SUM(ud.LicensedUse) AS L, SUM(CAST(ud.Prompts AS BIGINT)) AS P,
           SUM(ud.Sessions) AS S, SUM(ud.MultiTurnSessions) AS MT, COUNT(CASE WHEN ud.Interactions > 0 THEN 1 END) AS D,
           MAX(CASE WHEN ud.DateKey >= @k7 AND ud.Interactions > 0 THEN 1 ELSE 0 END) AS Last7
    FROM dash.UserDay AS ud {c.Scope("ud.UserKey")}
    WHERE ud.DateKey BETWEEN @k0 AND @k1
    GROUP BY ud.UserKey)
SELECT (SELECT COUNT(*) FROM dash.Person AS p {c.PersonScope("p.Upn")} WHERE p.AccountEnabled = 1) AS People,
       (SELECT COUNT(*) FROM bi.FactLicensedDay AS ld {c.Scope("ld.UserKey")} WHERE ld.DateKey = @k1) AS LicensedNow,
       (SELECT COUNT(*) FROM u WHERE I > 0) AS Active,
       (SELECT COUNT(*) FROM u WHERE L > 0) AS ActiveLicensed,
       (SELECT COUNT(DISTINCT ud.UserKey) FROM dash.UserDay AS ud {c.Scope("ud.UserKey")} WHERE ud.Interactions > 0) AS EverActive,
       (SELECT ISNULL(SUM(CAST(I AS BIGINT)), 0) FROM u) AS Interactions,
       (SELECT ISNULL(SUM(P), 0) FROM u) AS Prompts,
       (SELECT ISNULL(SUM(CAST(S AS BIGINT)), 0) FROM u) AS Sessions,
       (SELECT ISNULL(SUM(CAST(MT AS BIGINT)), 0) FROM u) AS MultiTurn,
       (SELECT ISNULL(SUM(CAST(D AS BIGINT)), 0) FROM u) AS ActiveDays,
       (SELECT COUNT(*) FROM u WHERE Last7 = 1) AS ActiveLast7,
       (SELECT COUNT(*) FROM u WHERE MT > 0) AS MultiTurnUsers;", c.Args())).Single();

    /* The members of each unit one level below the root (or the top): a manager with everyone under them,
       people reporting straight to the root as one "direct team"; divisions when there is no top. */
    static string UnitMembers(ReportContext c, bool haveParent) => haveParent ? @"
kids AS (SELECT p.Upn, CASE WHEN p.PeopleBelow > 0 THEN p.Upn ELSE N'~direct' END AS Unit FROM dash.Person AS p WHERE p.ManagerUpn = @parent
         UNION ALL SELECT @parent, N'~direct'),
m AS (SELECT k.Unit, oc.DescendantUpn AS Upn FROM kids AS k
      JOIN dash.OrgClosure AS oc ON oc.AncestorUpn = k.Upn AND (k.Unit <> N'~direct' OR oc.Depth = 0)),
u AS (SELECT m.Unit, pp.AccountEnabled, du.UserKey FROM m
      JOIN dash.Person AS pp ON pp.Upn = m.Upn
      LEFT JOIN bi.DimUser AS du ON du.UserPrincipalName = m.Upn)" : $@"
u AS (SELECT ISNULL(NULLIF(du.Division, N''), N'(no division)') AS Unit, du.AccountEnabled, du.UserKey
      FROM bi.DimUser AS du {c.Scope("du.UserKey")} WHERE du.DirectoryStatus = N'Found')";

    public async Task<List<UnitRow>> UnitsAsync(ReportContext c)
    {
        var parent = c.Root ?? c.OrgTopUpn;
        var sql = $@"
WITH act AS (SELECT ud.UserKey, SUM(ud.Interactions) AS I, SUM(ud.LicensedUse) AS L, COUNT(CASE WHEN ud.Interactions > 0 THEN 1 END) AS D,
                    SUM(ud.Sessions) AS S, SUM(ud.MultiTurnSessions) AS MT, SUM(ud.Jailbreak + ud.PromptInjection + ud.BlockedResource) AS F,
                    SUM(ud.LabelledContent) AS LB
             FROM dash.UserDay AS ud WHERE ud.DateKey BETWEEN @k0 AND @k1 GROUP BY ud.UserKey),
{UnitMembers(c, parent is not null)}
SELECT u.Unit,
       {(parent is null ? "u.Unit" : "CASE WHEN u.Unit = N'~direct' THEN N'Reporting directly' ELSE MAX(mp.DisplayName) END")} AS Name,
       {(parent is null ? "NULL" : "MAX(mp.Department)")} AS Department,
       COUNT(CASE WHEN u.AccountEnabled = 1 THEN 1 END) AS People,
       COUNT(ld.UserKey) AS Licensed,
       COUNT(CASE WHEN a.I > 0 THEN 1 END) AS Active,
       COUNT(CASE WHEN a.L > 0 THEN 1 END) AS ActiveLicensed,
       ISNULL(SUM(CAST(a.I AS BIGINT)), 0) AS Interactions, ISNULL(SUM(CAST(a.D AS BIGINT)), 0) AS ActiveDays,
       ISNULL(SUM(CAST(a.S AS BIGINT)), 0) AS Sessions, ISNULL(SUM(CAST(a.MT AS BIGINT)), 0) AS MultiTurn,
       ISNULL(SUM(CAST(a.F AS BIGINT)), 0) AS Flags, ISNULL(SUM(CAST(a.LB AS BIGINT)), 0) AS Labelled
FROM u
LEFT JOIN act AS a ON a.UserKey = u.UserKey
LEFT JOIN bi.FactLicensedDay AS ld ON ld.UserKey = u.UserKey AND ld.DateKey = @k1
{(parent is null ? "" : "LEFT JOIN dash.Person AS mp ON mp.Upn = u.Unit")}
GROUP BY u.Unit
ORDER BY {(parent is null ? "u.Unit" : "CASE WHEN u.Unit = N'~direct' THEN 1 ELSE 0 END, Name")};";
        return await db.QueryAsync<UnitRow>(sql, c.Args(new { parent }));
    }

    /// <summary>Active people per unit per trend point, for trend columns and heatmaps.</summary>
    public async Task<List<(string Unit, DateTime Bucket, int Active)>> UnitsTrendAsync(ReportContext c)
    {
        var parent = c.Root ?? c.OrgTopUpn;
        return await db.QueryAsync<(string, DateTime, int)>($@"
WITH {UnitMembers(c, parent is not null)},
b AS (SELECT d.DateKey, {c.BucketSql("d")} AS Bucket FROM bi.DimDate AS d WHERE d.DateKey BETWEEN @k0 AND @k1)
SELECT u.Unit, b.Bucket, COUNT(DISTINCT CASE WHEN ud.Interactions > 0 THEN ud.UserKey END)
FROM u JOIN dash.UserDay AS ud ON ud.UserKey = u.UserKey
JOIN b ON b.DateKey = ud.DateKey
WHERE u.AccountEnabled = 1
GROUP BY u.Unit, b.Bucket;", c.Args(new { parent }));
    }

    /// <summary>Per unit (as UnitsAsync) and trend point: interactions, safety signals, labelled content, web grounding.</summary>
    public async Task<List<UnitPoint>> UnitSeriesAsync(ReportContext c)
    {
        var parent = c.Root ?? c.OrgTopUpn;
        return await db.QueryAsync<UnitPoint>($@"
WITH {UnitMembers(c, parent is not null)},
b AS (SELECT d.DateKey, {c.BucketSql("d")} AS Bucket FROM bi.DimDate AS d WHERE d.DateKey BETWEEN @k0 AND @k1)
SELECT u.Unit, b.Bucket, SUM(CAST(ud.Interactions AS BIGINT)) AS Interactions,
       SUM(CAST(ud.Jailbreak + ud.PromptInjection + ud.BlockedResource AS BIGINT)) AS Flags,
       SUM(CAST(ud.LabelledContent AS BIGINT)) AS Labelled, SUM(CAST(ud.WebGrounded AS BIGINT)) AS Web
FROM u JOIN dash.UserDay AS ud ON ud.UserKey = u.UserKey
JOIN b ON b.DateKey = ud.DateKey
GROUP BY u.Unit, b.Bucket;", c.Args(new { parent }));
    }

    public static UnitRow Combine(IReadOnlyList<UnitRow> rows, string name) => new()
    {
        Unit = "~small", Name = name, People = rows.Sum(r => r.People), Licensed = rows.Sum(r => r.Licensed),
        Active = rows.Sum(r => r.Active), ActiveLicensed = rows.Sum(r => r.ActiveLicensed), Interactions = rows.Sum(r => r.Interactions),
        ActiveDays = rows.Sum(r => r.ActiveDays), Sessions = rows.Sum(r => r.Sessions), MultiTurn = rows.Sum(r => r.MultiTurn),
        Flags = rows.Sum(r => r.Flags), Labelled = rows.Sum(r => r.Labelled),
    };
    /// <summary>
    /// People, licences and use in the period, grouped by one directory attribute of bi.DimUser.
    /// <paramref name="attribute"/> is one of <see cref="Attributes"/>, never text from a request.
    /// </summary>
    public async Task<List<GroupRow>> ByAttributeAsync(ReportContext c, string attribute)
    {
        var expr = Attributes[attribute];
        return await db.QueryAsync<GroupRow>($@"
WITH act AS (SELECT ud.UserKey, SUM(ud.Interactions) AS I, SUM(ud.LicensedUse) AS L, COUNT(CASE WHEN ud.Interactions > 0 THEN 1 END) AS D,
                    SUM(ud.Sessions) AS S, SUM(ud.MultiTurnSessions) AS MT
             FROM dash.UserDay AS ud WHERE ud.DateKey BETWEEN @k0 AND @k1 GROUP BY ud.UserKey)
SELECT {expr} AS Name, COUNT(*) AS People, COUNT(ld.UserKey) AS Licensed,
       COUNT(CASE WHEN a.I > 0 THEN 1 END) AS Active, COUNT(CASE WHEN a.L > 0 THEN 1 END) AS ActiveLicensed,
       ISNULL(SUM(CAST(a.I AS BIGINT)), 0) AS Interactions, ISNULL(SUM(CAST(a.D AS BIGINT)), 0) AS ActiveDays,
       ISNULL(SUM(CAST(a.S AS BIGINT)), 0) AS Sessions, ISNULL(SUM(CAST(a.MT AS BIGINT)), 0) AS MultiTurn
FROM bi.DimUser AS du {c.Scope("du.UserKey")}
LEFT JOIN act AS a ON a.UserKey = du.UserKey
LEFT JOIN bi.FactLicensedDay AS ld ON ld.UserKey = du.UserKey AND ld.DateKey = @k1
WHERE du.DirectoryStatus = N'Found' AND du.AccountEnabled = 1
GROUP BY {expr};", c.Args());
    }

    public static readonly IReadOnlyDictionary<string, string> Attributes = new Dictionary<string, string>
    {
        ["Country"] = "ISNULL(NULLIF(du.Country, N''), N'(no country)')",
        ["City"] = "ISNULL(NULLIF(du.City, N''), N'(no city)')",
        ["Office"] = "ISNULL(NULLIF(du.OfficeLocation, N''), N'(no office)')",
        ["EmployeeType"] = "ISNULL(NULLIF(du.EmployeeType, N''), N'(not set)')",
        ["Role"] = "CASE WHEN du.IsManager = 1 THEN N'Managers' ELSE N'Individual contributors' END",
        ["JobTitle"] = "ISNULL(NULLIF(du.JobTitle, N''), N'(no title)')",
        ["Level"] = "CASE WHEN du.OrgDepth IS NULL THEN N'(not in the chart)' ELSE CONCAT(N'Level ', du.OrgDepth + 1) END",
        ["Division"] = "ISNULL(NULLIF(du.Division, N''), N'(no division)')",
    };

    public static GroupRow CombineGroups(IReadOnlyList<GroupRow> rows, string name) => new()
    {
        Name = name, People = rows.Sum(r => r.People), Licensed = rows.Sum(r => r.Licensed), Active = rows.Sum(r => r.Active),
        ActiveLicensed = rows.Sum(r => r.ActiveLicensed), Interactions = rows.Sum(r => r.Interactions), ActiveDays = rows.Sum(r => r.ActiveDays),
        Sessions = rows.Sum(r => r.Sessions), MultiTurn = rows.Sum(r => r.MultiTurn),
    };
}

public sealed class GroupRow
{
    public string Name { get; set; } = "";
    public int People { get; set; }
    public int Licensed { get; set; }
    public int Active { get; set; }
    public int ActiveLicensed { get; set; }
    public long Interactions { get; set; }
    public long ActiveDays { get; set; }
    public long Sessions { get; set; }
    public long MultiTurn { get; set; }
}

public sealed class UnitPoint
{
    public string Unit { get; set; } = "";
    public DateTime Bucket { get; set; }
    public long Interactions { get; set; }
    public long Flags { get; set; }
    public long Labelled { get; set; }
    public long Web { get; set; }
}
