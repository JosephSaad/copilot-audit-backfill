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
    public long Labeled { get; set; }
    public int Licensed { get; set; }   // holding a license at the end of the day or week
}

public sealed class Totals
{
    public int People { get; set; }            // enabled accounts in the directory, in scope
    /* The people counts below are of accounts enabled today (3.1.3); the use totals are everyone's. */
    public int LicensedNow { get; set; }       // holding a license on the last day, enabled accounts (3.1.3)
    public int LicensedNotEnabled { get; set; } // holding one on the last day with the account disabled or gone: seats to reclaim
    public DateTime? LicensesReadUtc { get; set; } // when the license tool last read who holds a license (3.1.3): the age of LicensedNow
    public int Active { get; set; }            // used Copilot in the period
    public int ActiveLicensed { get; set; }    // used it while licensed
    public int PremiumLicensed { get; set; }   // of those, used Premium features (PremiumUse > 0); the rest used only free features
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
    public int PremiumLicensed { get; set; }   // of ActiveLicensed, used Premium features (PremiumUse > 0)
    public long Interactions { get; set; }
    public long ActiveDays { get; set; }
    public long Sessions { get; set; }
    public long MultiTurn { get; set; }
    public long Flags { get; set; }
    public long Labeled { get; set; }
    /// <summary>Interactions grounded on the web search, in the period.</summary>
    public long Web { get; set; }
    /// <summary>Named after its manager; false in privacy mode, where it is named by department and is not a link.</summary>
    public bool Named { get; set; } = true;
    public bool Drillable => Named && !Unit.StartsWith('~') && Unit.Contains('@');
}

/// <summary>The measures most pages share, always through the report's scope.</summary>
public sealed class Metrics(Db db)
{
    public async Task<List<TrendRow>> TrendAsync(ReportContext c)
    {
        /* Each person's sums at each point come from the totals the refresh keeps (ReportContext.PerPoint): a week is one
           row, not seven days added up, and a person is one row a point, so the people are counted without a distinct. */
        var rows = await db.QueryAsync<TrendRow>($@"
WITH b AS (SELECT d.DateKey, {c.BucketSql("d")} AS Bucket FROM bi.DimDate AS d WHERE d.DateKey BETWEEN @k0 AND @k1),
ends AS (SELECT Bucket, MAX(DateKey) AS EndKey FROM b GROUP BY Bucket),
per AS (SELECT pp.* FROM {c.PerPoint()} AS pp {c.Scope("pp.UserKey")}),
/* Who holds a license at the end of each point: the licenses of those days alone, met on the point and the person,
   so they can be matched to the people in one pass rather than looked up a person at a time. */
held AS (SELECT e.Bucket, ld.UserKey FROM ends AS e JOIN bi.FactLicensedDay AS ld ON ld.DateKey = e.EndKey),
/* Active and licensed: used it with a license in the trend point, and still holds one at its end,
   so it is always a part of Licensed and adoption never passes 100%. */
/* People are accounts enabled today (3.1.3); the sums below still count everyone's use. */
people AS (SELECT per.Bucket, COUNT(CASE WHEN per.Interactions > 0 THEN 1 END) AS Active,
                  COUNT(CASE WHEN per.LicensedUse > 0 AND le.UserKey IS NOT NULL THEN 1 END) AS ActiveLicensed,
                  COUNT(CASE WHEN per.Interactions > 0 AND per.LicensedUse = 0 THEN 1 END) AS ActiveUnlicensed
           FROM per {ReportContext.EnabledJoin("per.UserKey")}
           LEFT JOIN held AS le ON le.Bucket = per.Bucket AND le.UserKey = per.UserKey
           GROUP BY per.Bucket),
sums AS (SELECT Bucket, SUM(CAST(Interactions AS BIGINT)) AS Interactions, SUM(CAST(Prompts AS BIGINT)) AS Prompts,
                SUM(CAST(Sessions AS BIGINT)) AS Sessions, SUM(CAST(MultiTurnSessions AS BIGINT)) AS MultiTurn,
                SUM(CAST(ChatInteractions AS BIGINT)) AS Chat, SUM(CAST(AgentInteractions AS BIGINT)) AS Agent,
                SUM(CAST(WebGrounded AS BIGINT)) AS Web, SUM(CAST(Jailbreak AS BIGINT)) AS Jailbreak,
                SUM(CAST(PromptInjection AS BIGINT)) AS Injection, SUM(CAST(BlockedResource AS BIGINT)) AS Blocked,
                SUM(CAST(LabeledContent AS BIGINT)) AS Labeled
         FROM per GROUP BY Bucket),
lic AS (SELECT e.Bucket, COUNT(*) AS Licensed FROM ends AS e JOIN bi.FactLicensedDay AS ld ON ld.DateKey = e.EndKey {c.Scope("ld.UserKey")} {ReportContext.EnabledJoin("ld.UserKey")} GROUP BY e.Bucket)
SELECT e.Bucket, ISNULL(p.Active, 0) AS Active, ISNULL(p.ActiveLicensed, 0) AS ActiveLicensed, ISNULL(p.ActiveUnlicensed, 0) AS ActiveUnlicensed,
       ISNULL(s.Interactions, 0) AS Interactions, ISNULL(s.Prompts, 0) AS Prompts, ISNULL(s.Sessions, 0) AS Sessions,
       ISNULL(s.MultiTurn, 0) AS MultiTurn, ISNULL(s.Chat, 0) AS Chat, ISNULL(s.Agent, 0) AS Agent, ISNULL(s.Web, 0) AS Web,
       ISNULL(s.Jailbreak, 0) AS Jailbreak, ISNULL(s.Injection, 0) AS Injection, ISNULL(s.Blocked, 0) AS Blocked,
       ISNULL(s.Labeled, 0) AS Labeled, ISNULL(l.Licensed, 0) AS Licensed
FROM ends AS e
LEFT JOIN people AS p ON p.Bucket = e.Bucket
LEFT JOIN sums AS s ON s.Bucket = e.Bucket
LEFT JOIN lic AS l ON l.Bucket = e.Bucket
ORDER BY e.Bucket;", c.Args());
        /* One row per trend point, in order, even where nothing happened. */
        return c.Buckets().Select(bk => rows.FirstOrDefault(r => r.Bucket == bk.Key) ?? new TrendRow { Bucket = bk.Key }).ToList();
    }

    /// <summary>The days adoption looks back over at each trend point.</summary>
    public const int AdoptionDays = 28;

    /// <summary>
    /// Adoption at each trend point: of the people holding a license at its end, how many used Copilot with it in the
    /// 28 days up to then. A rolling four weeks, so a weekend or a holiday does not dip it (the share active on
    /// the day itself is use, not adoption).
    /// </summary>
    public async Task<Dictionary<DateTime, int>> AdoptedAsync(ReportContext c)
    {
        /* The refresh keeps who was adopted on each day (dash.AdoptionDay: licensed use that day or in the 27 before,
           worked out by the same rule as below over every day), so a point is a seek on its last day. */
        if (c.Rollups)
            return (await db.QueryAsync<(DateTime Bucket, int Adopted)>($@"
WITH b AS (SELECT d.DateKey, {c.BucketSql("d")} AS Bucket FROM bi.DimDate AS d WHERE d.DateKey BETWEEN @k0 AND @k1),
ends AS (SELECT b.Bucket, MAX(b.DateKey) AS EndKey FROM b GROUP BY b.Bucket)
SELECT e.Bucket, COUNT(*) AS Adopted
FROM ends AS e
JOIN dash.AdoptionDay AS ad ON ad.DateKey = e.EndKey
{c.Scope("ad.UserKey")}
JOIN bi.FactLicensedDay AS ld ON ld.DateKey = e.EndKey AND ld.UserKey = ad.UserKey
{ReportContext.EnabledJoin("ad.UserKey")}
GROUP BY e.Bucket;", c.Args())).ToDictionary(r => r.Bucket, r => r.Adopted);

        /* Each day of use keeps a person adopted until their next one, or for 28 days: so a person is counted at a
           point by one row, and the points are met by equality, not by a range (4 seconds a year at 70,000 people
           against 20 the plain way). */
        var k = string.Join(",", Enumerable.Range(0, AdoptionDays).Select(i => $"({i})"));
        var rows = await db.QueryAsync<(DateTime Bucket, int Adopted)>($@"
WITH b AS (SELECT d.DateKey, {c.BucketSql("d")} AS Bucket FROM bi.DimDate AS d WHERE d.DateKey BETWEEN @k0 AND @k1),
ends AS (SELECT b.Bucket, MAX(b.DateKey) AS EndKey FROM b GROUP BY b.Bucket),
endn AS (SELECT e.Bucket, e.EndKey, DATEDIFF(DAY, 0, dd.[Date]) AS EndN FROM ends AS e JOIN bi.DimDate AS dd ON dd.DateKey = e.EndKey),
used AS (SELECT ud.UserKey, DATEDIFF(DAY, 0, d.[Date]) AS N
         FROM dash.UserDay AS ud {c.Scope("ud.UserKey")} JOIN bi.DimDate AS d ON d.DateKey = ud.DateKey
         WHERE ud.DateKey BETWEEN @kw AND @k1 AND ud.LicensedUse > 0),
iv AS (SELECT u.UserKey, u.N, ISNULL(LEAD(u.N) OVER (PARTITION BY u.UserKey ORDER BY u.N) - u.N, {AdoptionDays}) AS Gap FROM used AS u),
k AS (SELECT n FROM (VALUES {k}) AS v (n))
SELECT e.Bucket, COUNT(*) AS Adopted
FROM iv JOIN k ON k.n < iv.Gap
JOIN endn AS e ON e.EndN = iv.N + k.n
JOIN bi.FactLicensedDay AS ld ON ld.DateKey = e.EndKey AND ld.UserKey = iv.UserKey
{ReportContext.EnabledJoin("iv.UserKey")}
GROUP BY e.Bucket;", c.Args(new { kw = ReportContext.Key(c.Start.AddDays(1 - AdoptionDays)) }));
        return rows.ToDictionary(r => r.Bucket, r => r.Adopted);
    }

    public async Task<Totals> TotalsAsync(ReportContext c) =>
        (await db.QueryAsync<Totals>($@"
WITH u AS (
    /* Each person's sums over the period, from the totals the refresh keeps (ReportContext.PerPerson). Active in the
       last 7 days: their last day of use is one of them. */
    SELECT p.UserKey, p.Interactions AS I, p.LicensedUse AS L, p.PremiumUse AS PU, CAST(p.Prompts AS BIGINT) AS P,
           p.Sessions AS S, p.MultiTurnSessions AS MT, p.ActiveDays AS D,
           CASE WHEN p.LastActiveKey >= @k7 THEN 1 ELSE 0 END AS Last7
    FROM {c.PerPerson()} AS p {c.Scope("p.UserKey")})
/* One pass over u (2.4.2): as nine subqueries, each re-aggregated dash.UserDay. */
/* People counts are of accounts enabled today (3.1.3, IsEnabled); interactions, prompts and sessions are everyone's. */
SELECT (SELECT COUNT(*) FROM dash.Person AS p {c.PersonScope("p.Upn")} WHERE p.AccountEnabled = 1) AS People,
       (SELECT COUNT(*) FROM bi.FactLicensedDay AS ld {c.Scope("ld.UserKey")} {ReportContext.EnabledJoin("ld.UserKey")} WHERE ld.DateKey = @k1) AS LicensedNow,
       (SELECT COUNT(*) FROM bi.FactLicensedDay AS ld {c.Scope("ld.UserKey")} {ReportContext.EnabledJoin("ld.UserKey", left: true)}
        WHERE ld.DateKey = @k1 AND en.UserKey IS NULL) AS LicensedNotEnabled,
       (SELECT MAX(cl.UpdatedUtc) FROM audit.CopilotLicense AS cl) AS LicensesReadUtc,
       COUNT(CASE WHEN u.I > 0 AND {ReportContext.IsEnabled()} THEN 1 END) AS Active,
       COUNT(CASE WHEN u.L > 0 AND ld.UserKey IS NOT NULL AND {ReportContext.IsEnabled()} THEN 1 END) AS ActiveLicensed,
       COUNT(CASE WHEN u.L > 0 AND u.PU > 0 AND ld.UserKey IS NOT NULL AND {ReportContext.IsEnabled()} THEN 1 END) AS PremiumLicensed,
       {EverActive(c)} AS EverActive,
       ISNULL(SUM(CAST(u.I AS BIGINT)), 0) AS Interactions,
       ISNULL(SUM(u.P), 0) AS Prompts,
       ISNULL(SUM(CAST(u.S AS BIGINT)), 0) AS Sessions,
       ISNULL(SUM(CAST(u.MT AS BIGINT)), 0) AS MultiTurn,
       ISNULL(SUM(CAST(u.D AS BIGINT)), 0) AS ActiveDays,
       COUNT(CASE WHEN u.Last7 = 1 AND {ReportContext.IsEnabled()} THEN 1 END) AS ActiveLast7,
       COUNT(CASE WHEN u.MT > 0 AND {ReportContext.IsEnabled()} THEN 1 END) AS MultiTurnUsers
FROM u LEFT JOIN bi.FactLicensedDay AS ld ON ld.UserKey = u.UserKey AND ld.DateKey = @k1
{ReportContext.EnabledJoin("u.UserKey", left: true)};", c.Args())).Single();

    /* SQL for the enabled people in scope who used Copilot on any day at all. The refresh keeps each person's first day
       of use over every day (dash.UserActivity), so it is a read of one row a person; before it is built, every day of
       dash.UserDay is read for it. */
    static string EverActive(ReportContext c) => c.Rollups
        ? $@"(SELECT COUNT(*) FROM dash.UserActivity AS ev {c.Scope("ev.UserKey")} {ReportContext.EnabledJoin("ev.UserKey")}
        WHERE ev.FirstActiveKey IS NOT NULL)"
        : $@"(SELECT COUNT(*) FROM (SELECT DISTINCT ud.UserKey FROM dash.UserDay AS ud {c.Scope("ud.UserKey")} WHERE ud.Interactions > 0) AS ev
        {ReportContext.EnabledJoin("ev.UserKey")})";

    /* The members of each unit one level below the root (or the top): a manager with everyone under them,
       people reporting straight to the root as one "direct team"; divisions when there is no top. */
    static string UnitMembers(ReportContext c, bool haveParent) => haveParent ? @"
kids AS (SELECT p.Upn, CASE WHEN p.PeopleBelow > 0 THEN p.Upn ELSE N'~direct' END AS Unit FROM dash.Person AS p WHERE p.ManagerUpn = @parent
         UNION ALL SELECT @parent, N'~direct'),
m AS (SELECT k.Unit, oc.DescendantUpn AS Upn, oc.DescendantKey, oc.DescendantEnabled FROM kids AS k
      JOIN dash.OrgClosure AS oc ON oc.AncestorUpn = k.Upn AND (k.Unit <> N'~direct' OR oc.Depth = 0)),
/* Each member's account and bi.DimUser key are on the closure, written by the refresh from dash.Person and bi.DimUser:
   matching 210,000 sign-ins to both by name took seconds. A row written without them (by another tool), or a person
   the closure found no key for, is looked up by sign-in as before; a member who is not in dash.Person is left out, as
   the join to it did. */
u AS (SELECT m.Unit, CASE WHEN m.DescendantEnabled IS NOT NULL THEN m.DescendantEnabled ELSE pe.AccountEnabled END AS AccountEnabled,
             CASE WHEN m.DescendantKey IS NOT NULL THEN m.DescendantKey ELSE dk.UserKey END AS UserKey
      FROM m
      OUTER APPLY (SELECT 1 AS Found, pp.AccountEnabled FROM dash.Person AS pp WHERE m.DescendantEnabled IS NULL AND pp.Upn = m.Upn) AS pe
      OUTER APPLY (SELECT du.UserKey FROM bi.DimUser AS du WHERE m.DescendantKey IS NULL AND du.UserPrincipalName = m.Upn) AS dk
      WHERE m.DescendantEnabled IS NOT NULL OR pe.Found = 1)" : $@"
u AS (SELECT ISNULL(NULLIF(du.Division, N''), N'(no division)') AS Unit, du.AccountEnabled, du.UserKey
      FROM bi.DimUser AS du {c.Scope("du.UserKey")} WHERE du.DirectoryStatus = N'Found')";

    /* The members, materialised as #u before the query that uses them. Joined as a CTE to the
       per-person rollups, the optimizer re-aggregated dash.UserDay once per person (70,000
       times at the top of a 70,000-person organization: 10 to 40 seconds a page); from a
       temporary table it aggregates once. The table lasts only for the one batch. */
    static string UnitMembersInto(ReportContext c, bool haveParent) =>
        $@"WITH {UnitMembers(c, haveParent)}
SELECT u.Unit, u.AccountEnabled, u.UserKey INTO #u FROM u;
";

    public async Task<List<UnitRow>> UnitsAsync(ReportContext c)
    {
        var parent = c.Root ?? c.OrgTopUpn;
        var sql = $@"
{UnitMembersInto(c, parent is not null)}
WITH act AS (SELECT p.UserKey, p.Interactions AS I, p.LicensedUse AS L, p.PremiumUse AS PU, p.ActiveDays AS D,
                    p.Sessions AS S, p.MultiTurnSessions AS MT, p.Jailbreak + p.PromptInjection + p.BlockedResource AS F,
                    p.LabeledContent AS LB, p.WebGrounded AS W
             FROM {c.PerPerson()} AS p)
SELECT u.Unit,
       {(parent is null ? "u.Unit" : "CASE WHEN u.Unit = N'~direct' THEN N'Reporting directly' ELSE MAX(mp.DisplayName) END")} AS Name,
       {(parent is null ? "NULL" : "MAX(mp.Department)")} AS Department,
       COUNT(CASE WHEN u.AccountEnabled = 1 THEN 1 END) AS People,
       COUNT(CASE WHEN ld.UserKey IS NOT NULL AND u.AccountEnabled = 1 THEN 1 END) AS Licensed,
       COUNT(CASE WHEN a.I > 0 AND u.AccountEnabled = 1 THEN 1 END) AS Active,
       COUNT(CASE WHEN a.L > 0 AND ld.UserKey IS NOT NULL AND u.AccountEnabled = 1 THEN 1 END) AS ActiveLicensed,
       COUNT(CASE WHEN a.L > 0 AND a.PU > 0 AND ld.UserKey IS NOT NULL AND u.AccountEnabled = 1 THEN 1 END) AS PremiumLicensed,
       ISNULL(SUM(CAST(a.I AS BIGINT)), 0) AS Interactions, ISNULL(SUM(CAST(a.D AS BIGINT)), 0) AS ActiveDays,
       ISNULL(SUM(CAST(a.S AS BIGINT)), 0) AS Sessions, ISNULL(SUM(CAST(a.MT AS BIGINT)), 0) AS MultiTurn,
       ISNULL(SUM(CAST(a.F AS BIGINT)), 0) AS Flags, ISNULL(SUM(CAST(a.LB AS BIGINT)), 0) AS Labeled, ISNULL(SUM(CAST(a.W AS BIGINT)), 0) AS Web
FROM #u AS u
LEFT JOIN act AS a ON a.UserKey = u.UserKey
LEFT JOIN bi.FactLicensedDay AS ld ON ld.UserKey = u.UserKey AND ld.DateKey = @k1
{(parent is null ? "" : "LEFT JOIN dash.Person AS mp ON mp.Upn = u.Unit")}
GROUP BY u.Unit
ORDER BY {(parent is null ? "u.Unit" : "CASE WHEN u.Unit = N'~direct' THEN 1 ELSE 0 END, Name")};";
        var rows = await db.QueryAsync<UnitRow>(sql, c.Args(new { parent }));
        if (c.PrivacyMode && parent is not null) Unnamed(rows);
        return rows;
    }

    /// <summary>
    /// Privacy mode: a unit is named after its manager, so it goes by the manager's department instead, numbered
    /// where two share one, in the order of their sign-ins so the same unit has the same name on every page. Its sign-in
    /// stays in Unit, for joining a unit's rows within a page, and it is not a link.
    /// </summary>
    static void Unnamed(List<UnitRow> rows)
    {
        foreach (var g in rows.Where(r => !r.Unit.StartsWith('~')).GroupBy(r => string.IsNullOrWhiteSpace(r.Department) ? "A part of the organization" : r.Department!.Trim()))
        {
            var same = g.OrderBy(r => r.Unit, StringComparer.OrdinalIgnoreCase).ToList();
            for (var i = 0; i < same.Count; i++) same[i].Name = same.Count == 1 ? g.Key : $"{g.Key} ({i + 1})";
        }
        foreach (var r in rows) r.Named = false;
        rows.Sort((a, b) => a.Unit.StartsWith('~') != b.Unit.StartsWith('~') ? (a.Unit.StartsWith('~') ? 1 : -1) : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Active people per unit per trend point, for trend columns and heatmaps.</summary>
    public async Task<List<(string Unit, DateTime Bucket, int Active)>> UnitsTrendAsync(ReportContext c)
    {
        var parent = c.Root ?? c.OrgTopUpn;
        return await db.QueryAsync<(string, DateTime, int)>($@"
{UnitMembersInto(c, parent is not null)}
SELECT u.Unit, pp.Bucket, COUNT(DISTINCT CASE WHEN pp.Interactions > 0 THEN pp.UserKey END)
FROM #u AS u JOIN {c.PerPoint()} AS pp ON pp.UserKey = u.UserKey
WHERE u.AccountEnabled = 1
GROUP BY u.Unit, pp.Bucket;", c.Args(new { parent }));
    }

    /// <summary>Per unit (as UnitsAsync) and trend point: interactions, safety signals, labeled content, web grounding.</summary>
    public async Task<List<UnitPoint>> UnitSeriesAsync(ReportContext c)
    {
        var parent = c.Root ?? c.OrgTopUpn;
        return await db.QueryAsync<UnitPoint>($@"
{UnitMembersInto(c, parent is not null)}
SELECT u.Unit, pp.Bucket, SUM(CAST(pp.Interactions AS BIGINT)) AS Interactions,
       SUM(CAST(pp.Jailbreak AS BIGINT) + pp.PromptInjection + pp.BlockedResource) AS Flags,
       SUM(CAST(pp.LabeledContent AS BIGINT)) AS Labeled, SUM(CAST(pp.WebGrounded AS BIGINT)) AS Web
FROM #u AS u JOIN {c.PerPoint()} AS pp ON pp.UserKey = u.UserKey
GROUP BY u.Unit, pp.Bucket;", c.Args(new { parent }));
    }

    public static UnitRow Combine(IReadOnlyList<UnitRow> rows, string name) => new()
    {
        Unit = "~small", Name = name, People = rows.Sum(r => r.People), Licensed = rows.Sum(r => r.Licensed),
        Active = rows.Sum(r => r.Active), ActiveLicensed = rows.Sum(r => r.ActiveLicensed), PremiumLicensed = rows.Sum(r => r.PremiumLicensed),
        Interactions = rows.Sum(r => r.Interactions), ActiveDays = rows.Sum(r => r.ActiveDays), Sessions = rows.Sum(r => r.Sessions), MultiTurn = rows.Sum(r => r.MultiTurn),
        Flags = rows.Sum(r => r.Flags), Labeled = rows.Sum(r => r.Labeled), Web = rows.Sum(r => r.Web),
    };
    /// <summary>
    /// People, licenses and use in the period, grouped by one directory attribute of bi.DimUser.
    /// <paramref name="attribute"/> is one of <see cref="Attributes"/>, never text from a request.
    /// </summary>
    public async Task<List<GroupRow>> ByAttributeAsync(ReportContext c, string attribute)
    {
        var expr = Attributes[attribute];
        return await db.QueryAsync<GroupRow>($@"
WITH act AS (SELECT p.UserKey, p.Interactions AS I, p.LicensedUse AS L, p.ActiveDays AS D, p.Sessions AS S, p.MultiTurnSessions AS MT
             FROM {c.PerPerson()} AS p)
SELECT {expr} AS Name, COUNT(CASE WHEN du.AccountEnabled = 1 THEN 1 END) AS People,
       COUNT(CASE WHEN ld.UserKey IS NOT NULL AND du.AccountEnabled = 1 THEN 1 END) AS Licensed,
       COUNT(CASE WHEN a.I > 0 AND du.AccountEnabled = 1 THEN 1 END) AS Active,
       COUNT(CASE WHEN a.L > 0 AND ld.UserKey IS NOT NULL AND du.AccountEnabled = 1 THEN 1 END) AS ActiveLicensed,
       ISNULL(SUM(CAST(a.I AS BIGINT)), 0) AS Interactions, ISNULL(SUM(CAST(a.D AS BIGINT)), 0) AS ActiveDays,
       ISNULL(SUM(CAST(a.S AS BIGINT)), 0) AS Sessions, ISNULL(SUM(CAST(a.MT AS BIGINT)), 0) AS MultiTurn
FROM bi.DimUser AS du {c.Scope("du.UserKey")}
LEFT JOIN act AS a ON a.UserKey = du.UserKey
LEFT JOIN bi.FactLicensedDay AS ld ON ld.UserKey = du.UserKey AND ld.DateKey = @k1
/* Everyone with use is in a group, so the groups' use adds up to the total (3.1.3): a person disabled or gone
   from the directory since counts in no group's people, and their use stays in the group the directory last gave them. */
WHERE (du.DirectoryStatus = N'Found' AND du.AccountEnabled = 1) OR a.UserKey IS NOT NULL
GROUP BY {expr};", c.Args());
    }

    public static readonly IReadOnlyDictionary<string, string> Attributes = new Dictionary<string, string>
    {
        ["Department"] = "ISNULL(NULLIF(du.Department, N''), N'(no department)')",
        ["Company"] = "ISNULL(NULLIF(du.CompanyName, N''), N'(no company)')",
        ["Country"] = "ISNULL(NULLIF(du.Country, N''), N'(no country)')",
        ["City"] = "ISNULL(NULLIF(du.City, N''), N'(no city)')",
        ["Office"] = "ISNULL(NULLIF(du.OfficeLocation, N''), N'(no office)')",
        ["EmployeeType"] = "ISNULL(NULLIF(du.EmployeeType, N''), N'(not set)')",
        ["Role"] = "CASE WHEN du.IsManager = 1 THEN N'Managers' ELSE N'Individual contributors' END",
        ["JobTitle"] = "ISNULL(NULLIF(du.JobTitle, N''), N'(no title)')",
        ["Level"] = "CASE WHEN du.OrgDepth IS NULL THEN N'(not in the chart)' ELSE CONCAT(N'Level ', du.OrgDepth + 1) END",
        ["Division"] = "ISNULL(NULLIF(du.Division, N''), N'(no division)')",
    };

    /// <summary>
    /// Where a level group sits in a list of levels: Level 2, 3 ... 9, 10, 11, 12 as numbers. Sorted as text,
    /// Level 10 and Level 11 came before Level 2. A name that is no level, (not in the chart), comes after the last level.
    /// </summary>
    public static int LevelOrder(string name) =>
        name.StartsWith("Level ", StringComparison.Ordinal)
        && int.TryParse(name.AsSpan(6), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n)
            ? n : int.MaxValue;

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
    public long Labeled { get; set; }
    public long Web { get; set; }
}
