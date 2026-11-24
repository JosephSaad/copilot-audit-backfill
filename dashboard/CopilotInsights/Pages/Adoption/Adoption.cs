using CopilotInsights.Charts;
using CopilotInsights.Data;
using CopilotInsights.Infrastructure;
using Microsoft.AspNetCore.Html;

namespace CopilotInsights.Pages.Adoption;

/// <summary>
/// Whether a person has ever used Copilot, and their first day: dash.UserActivity's, kept by the nightly refresh, one row
/// a person. Before the refresh has built it, read from each person's days in dash.UserDay, as it always was: the same
/// answer either way. Read from the days, it took 2 to 3 seconds a page at 210,000 people and two years.
/// </summary>
static class FirstUse
{
    /// <summary>SQL: the person with this key has used Copilot at least once, on any day.</summary>
    public static string Ever(ReportContext c, string userKey) => c.Rollups
        ? $"EXISTS (SELECT 1 FROM dash.UserActivity AS ua WHERE ua.UserKey = {userKey} AND ua.FirstActiveKey IS NOT NULL)"
        : $"EXISTS (SELECT 1 FROM dash.UserDay AS ud WHERE ud.UserKey = {userKey} AND ud.Interactions > 0)";

    /// <summary>SQL: the person's first day of use (a date key), or NULL for none.</summary>
    public static string Key(ReportContext c, string userKey) => c.Rollups
        ? $"(SELECT ua.FirstActiveKey FROM dash.UserActivity AS ua WHERE ua.UserKey = {userKey})"
        : $"(SELECT MIN(ud.DateKey) FROM dash.UserDay AS ud WHERE ud.UserKey = {userKey} AND ud.Interactions > 0)";

    /// <summary>SQL: everyone in scope who has used Copilot, with their first day: UserKey, FirstKey.</summary>
    public static string People(ReportContext c) => c.Rollups
        ? $"SELECT ua.UserKey, ua.FirstActiveKey AS FirstKey FROM dash.UserActivity AS ua {c.Scope("ua.UserKey")} WHERE ua.FirstActiveKey IS NOT NULL"
        : $"SELECT ud.UserKey, MIN(ud.DateKey) AS FirstKey FROM dash.UserDay AS ud {c.Scope("ud.UserKey")} WHERE ud.Interactions > 0 GROUP BY ud.UserKey";
}

public sealed class IndexModel(PageServices s, Metrics m) : InsightsPage(s)
{
    public override string Title => "Funnel and trend";
    public override string Persona => "Adoption";
    public override string Intro => "From license to habit: how many Premium license holders (the paid Microsoft 365 Copilot license) have started, used Copilot in the period, and go beyond one-off questions.";

    protected override async Task BuildAsync()
    {
        /* The five reads are independent: started together, each on its own connection, where they ran one after another. */
        var (t, tr, started, notStarted, firsts) = await Queries(
            m.TotalsAsync(Ctx),
            m.TrendAsync(Ctx),
            Db.ScalarAsync<int>($@"
SELECT COUNT(*) FROM bi.FactLicensedDay AS ld {Ctx.Scope("ld.UserKey")} {ReportContext.EnabledJoin("ld.UserKey")}
WHERE ld.DateKey = @k1 AND {FirstUse.Ever(Ctx, "ld.UserKey")};", Ctx.Args()),
            Db.ScalarAsync<int>($"SELECT COUNT(*) {NotStartedModel.Who(Ctx)};", Ctx.Args()),
            Db.QueryAsync<(DateTime Bucket, int N)>($@"
WITH f AS ({FirstUse.People(Ctx)})
SELECT {Ctx.BucketSql("d")}, COUNT(*) FROM f {ReportContext.EnabledJoin("f.UserKey")} JOIN bi.DimDate AS d ON d.DateKey = f.FirstKey
WHERE f.FirstKey BETWEEN @k0 AND @k1 GROUP BY {Ctx.BucketSql("d")};", Ctx.Args()));

        /* The same names as Leadership › Overview: the license is the Premium one, and its use in the period is Adoption. */
        Kpis.Add(new("Premium licenses", Num(t.LicensedNow), "people holding a Microsoft 365 Copilot license; " + LicensesAsOf(t)));
        Kpis.Add(new("Have started", Pct(Ratio(started, t.LicensedNow)), $"{started:#,0} license holders have used Copilot at least once, at any time"));
        /* The list's own count leads to it; the share above is its complement, so it did not appear there. */
        Kpis.Add(new("Licensed, not started", Num(notStarted), "licensed at least 14 days ago, never used Copilot", notStarted > 0 ? "warn" : null, Href: Open("/adoption/not-started")));
        Kpis.Add(new("Adoption", Pct(Ratio(t.ActiveLicensed, t.LicensedNow)), $"{t.ActiveLicensed:#,0} license holders used Copilot with it in the period"));
        Kpis.Add(new("People who followed up", Pct(Ratio(t.MultiTurnUsers, t.Active)), "of people using Copilot in the period had a session of more than one prompt (prompts less than an hour apart on one day, in any app)"));

        var stages = new List<Bar>
        {
            new("People", t.People, Note: "enabled accounts in the directory"),
            new("Premium license holders at the period's end", t.LicensedNow, Slot: 1),
            new("License holders who ever used Copilot", started, Slot: 1),
            new("License holders who used it in the period", t.ActiveLicensed, Slot: 1),
            new("Anyone using Copilot in the period's last 7 days", t.ActiveLast7, Slot: 1),
            new("Anyone with a session of several prompts in the period", t.MultiTurnUsers, Slot: 1),
        };
        Panels.Add(new("Adoption funnel", Chart.Bars(stages, Fmt.Count, "Adoption funnel"),
            "Each bar is a count of people with an enabled account. The first four narrow from everyone to the license holders who used Copilot in the period. The last two count anyone who used Copilot, with a license or on the free Copilot Chat."));

        var buckets = Buckets();
        Panels.Add(new("People using Copilot for the first time",
            Chart.Columns(Labels(), [new Series("First-time users", buckets.Select(b => (double?)firsts.Where(f => f.Bucket == b.Key).Sum(f => f.N)).ToList(), 3)], Fmt.Count, "First-time users"),
            TrendNote, "half"));
        Panels.Add(new("Premium license holders, and how many used Copilot",
            Chart.Line(Labels(), [
                new Series("License holders", tr.Select(w => (double?)w.Licensed).ToList(), 1, ToEnd: true),
                new Series("Used Copilot", tr.Select(w => (double?)w.ActiveLicensed).ToList(), 3),
            ], Fmt.Count, "Licensed and active"),
            $"License holders: holding a Premium Copilot license at the end of the day or week. {ToEndNote} Used Copilot: license holders who used it with the license in the day or week. {TrendNote}", "half"));
    }
}

public sealed class CohortsModel(PageServices s) : InsightsPage(s)
{
    public override string Title => "Retention by license month";
    public override string Persona => "Adoption";
    public override string? OwnWindow => "the licenses held today (cohorts: those that began in the last 12 months)";
    public override string Intro => "Do people keep using Copilot after they get it? Each row is the people licensed in one month; each column a week after their license.";

    protected override async Task BuildAsync()
    {
        const int weeks = 12;
        var rows = await Db.QueryAsync<(DateTime Cohort, int K, int People, int Active, int Size)>($@"
/* Cohort members and their active weeks as temporary tables: as CTEs the optimizer
   re-evaluated the activity once per cohort week (10 seconds at 70,000 people). */
SELECT du.UserKey, CAST(du.LicensedSinceUtc AS DATE) AS Since,
       DATEFROMPARTS(YEAR(du.LicensedSinceUtc), MONTH(du.LicensedSinceUtc), 1) AS Cohort
INTO #c
FROM bi.DimUser AS du {Ctx.Scope("du.UserKey")}
WHERE du.CopilotLicenseToday = N'Licensed' AND du.AccountEnabled = 1 AND du.LicensedSinceUtc >= DATEADD(MONTH, -12, @d1);
SELECT DISTINCT c.UserKey, DATEDIFF(DAY, c.Since, d.[Date]) / 7 AS K
INTO #a
FROM #c AS c JOIN dash.UserDay AS ud ON ud.UserKey = c.UserKey AND ud.Interactions > 0
JOIN bi.DimDate AS d ON d.DateKey = ud.DateKey
WHERE d.[Date] >= c.Since AND d.[Date] < DATEADD(DAY, 7 * {weeks}, c.Since);
WITH k AS (SELECT TOP ({weeks}) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) - 1 AS K FROM sys.all_objects),
n AS (SELECT Cohort, COUNT(*) AS Size FROM #c GROUP BY Cohort)
SELECT c.Cohort, k.K, COUNT(*) AS People, COUNT(a.UserKey) AS Active, MAX(n.Size) AS Size
FROM #c AS c CROSS JOIN k JOIN n ON n.Cohort = c.Cohort
LEFT JOIN #a AS a ON a.UserKey = c.UserKey AND a.K = k.K
WHERE DATEADD(DAY, 7 * k.K + 6, c.Since) <= @d1
GROUP BY c.Cohort, k.K;", Ctx.Args());
        var cohorts = rows.Select(r => r.Cohort).Distinct().OrderBy(d => d).ToList();
        var cohortsShown = cohorts.Where(c => rows.First(r => r.Cohort == c).Size >= Ctx.MinGroupSize).ToList();
        var grid = new double?[cohortsShown.Count, weeks];
        for (var i = 0; i < cohortsShown.Count; i++)
            for (var k = 0; k < weeks; k++)
            {
                var r = rows.FirstOrDefault(x => x.Cohort == cohortsShown[i] && x.K == k);
                grid[i, k] = r.People >= Ctx.MinGroupSize ? r.Active / (double)r.People : null;
            }
        Panels.Add(new("Share of each month's new license holders active, week by week",
            Chart.Heatmap(cohortsShown.Select(c => $"{c:MMM yyyy} ({rows.First(r => r.Cohort == c).Size:#,0})").ToList(),
                Enumerable.Range(1, weeks).Select(k => $"Wk {k}").ToList(), grid, Fmt.Percent, "Cohort retention", 1, unit: "used Copilot that week", column: w => "week " + w[3..] + " after the license"),
            $"People licensed today with an enabled account, by the month their current license began, over the last 12 months. Each row is one month's new license holders; each column a week after their license began. Empty cells are weeks that have not happened yet for enough of them. Months with fewer than {Ctx.MinGroupSize} people are left out."));

        var ttf = await Db.QueryAsync<(string Bucket, int N)>($@"
WITH f AS (SELECT du.UserKey, CAST(du.LicensedSinceUtc AS DATE) AS Since,
                  {FirstUse.Key(Ctx, "du.UserKey")} AS FirstKey
           FROM bi.DimUser AS du {Ctx.Scope("du.UserKey")}
           WHERE du.CopilotLicenseToday = N'Licensed' AND du.AccountEnabled = 1 AND du.LicensedSinceUtc IS NOT NULL),
g AS (SELECT CASE WHEN FirstKey IS NULL THEN 7
                  ELSE CASE WHEN DATEDIFF(DAY, Since, DATEFROMPARTS(FirstKey / 10000, FirstKey / 100 % 100, FirstKey % 100)) <= 0 THEN 1
                            WHEN DATEDIFF(DAY, Since, DATEFROMPARTS(FirstKey / 10000, FirstKey / 100 % 100, FirstKey % 100)) <= 7 THEN 2
                            WHEN DATEDIFF(DAY, Since, DATEFROMPARTS(FirstKey / 10000, FirstKey / 100 % 100, FirstKey % 100)) <= 14 THEN 3
                            WHEN DATEDIFF(DAY, Since, DATEFROMPARTS(FirstKey / 10000, FirstKey / 100 % 100, FirstKey % 100)) <= 30 THEN 4
                            WHEN DATEDIFF(DAY, Since, DATEFROMPARTS(FirstKey / 10000, FirstKey / 100 % 100, FirstKey % 100)) <= 60 THEN 5 ELSE 6 END END AS B
      FROM f)
SELECT CHOOSE(B, N'Same day', N'1-7 days', N'8-14 days', N'15-30 days', N'31-60 days', N'Over 60 days', N'Not yet'), COUNT(*)
FROM g GROUP BY B ORDER BY B;", Ctx.Args());
        Panels.Add(new("Time from license to first use",
            Chart.Bars(ttf.Select(t => new Bar(t.Bucket, t.N, Slot: t.Bucket == "Not yet" ? 2 : 1)).ToList(), Fmt.Count, "Time to first use"),
            "People licensed today with an enabled account, by how long after their license they first used Copilot."));
    }
}

public sealed class DepartmentsModel(PageServices s, Metrics metrics) : InsightsPage(s)
{
    public override string Title => "Departments";
    public override string Persona => "Adoption";
    public override string Intro => "Adoption by department, from Active Directory: the share of Premium license holders who used Copilot with the license in the period. Also every department, division, country or office ranked against the period before, with the most improved. Groups smaller than the minimum group size are combined, or left out of the ranking.";

    static readonly string[] RankBy = ["Department", "Division", "Country", "Office"];

    /// <summary>What the ranking groups by (by=), the department when it is anything else.</summary>
    string RankedBy() => Request.Query["by"].FirstOrDefault() is { } b && RankBy.Contains(b) ? b : "Department";

    /// <summary>
    /// The ranking's groups in the period and, where the data covers it, in the period before: the two read side by
    /// side, and started before the page's own figures where the ranking will be shown, so the page waits for the
    /// slowest of its reads instead of their sum.
    /// </summary>
    async Task<(List<GroupRow> Now, Dictionary<string, GroupRow> Before)> GroupsAsync(string by)
    {
        var prevCtx = OpsReports.Window(Ctx, Ctx.Start.AddDays(-1), Ctx.Days);
        async Task<Dictionary<string, GroupRow>> BeforeAsync() => OpsReports.Covers(prevCtx, await OpsReports.FirstDayAsync(Db))
            ? (await metrics.ByAttributeAsync(prevCtx, by)).ToDictionary(r => r.Name) : new Dictionary<string, GroupRow>();
        return await Queries(metrics.ByAttributeAsync(Ctx, by), BeforeAsync());
    }

    /// <summary>
    /// The groups ranked by adoption against the period before (the Leaderboard was this, on a page of its own: it is
    /// here, and /adoption/leaderboard arrives here). Only groups of at least the minimum group size, with at least that
    /// many licenses, are ranked, then and now. Ranked by department, the change goes into All departments, which is
    /// that ranking already, and no second table repeats it: the panels to add after it are returned, with the changes.
    /// </summary>
    async Task<(string By, Dictionary<string, double> Changes, List<Panel> Panels)> RankingAsync(Task<(List<GroupRow> Now, Dictionary<string, GroupRow> Before)> groups)
    {
        var after = new List<Panel>();
        var by = RankedBy();
        var (all, before) = await groups;
        var now = all.Where(r => r.People >= Ctx.MinGroupSize && r.Licensed >= Ctx.MinGroupSize).ToList();
        double? Adopt(GroupRow r) => r.Licensed > 0 ? (double)r.ActiveLicensed / r.Licensed : null;
        var ranked = now.OrderByDescending(r => Adopt(r) ?? 0).ThenByDescending(r => r.Interactions).ToList();
        var choices = string.Join(" · ", RankBy.Select(x => $"<a href=\"{E(Ctx.Url(Request.Path, extra: "by=" + x))}#ranking\"{(x == by ? " class=\"on\"" : "")}>{x}</a>"));
        if (ranked.Count == 0)
        {
            after.Add(new("Ranking, against the period before", new HtmlString($"<p class=\"sub\" id=\"ranking\">Rank by: {choices}</p><p class=\"muted\">No {by.ToLowerInvariant()} in {Here} has at least {Ctx.MinGroupSize} people and {Ctx.MinGroupSize} licenses.</p>"), null, "full"));
            return (by, [], after);
        }
        var t = new Table
        {
            Headers = ["Rank", by, "People", "License holders", "Adoption", "Change (points)", "Share of its people using Copilot", "Interactions per active person", "Days active per person"],
            Numeric = new HashSet<int> { 0, 2, 3, 4, 5, 6, 7, 8 },
        };
        var changes = new List<(string Name, double Points)>();
        for (var i = 0; i < ranked.Count; i++)
        {
            var r = ranked[i];
            /* Against the period before only where the group met the same minimum then: a group of two license holders a
               period ago made "+50 points" and the most improved of one person's use. */
            double? change = before.TryGetValue(r.Name, out var pr) && pr.People >= Ctx.MinGroupSize && pr.Licensed >= Ctx.MinGroupSize
                             && Adopt(pr) is double a0 && Adopt(r) is double a1 ? (a1 - a0) * 100 : null;
            if (change is double c) changes.Add((r.Name, c));
            t.Rows.Add([i + 1, r.Name, r.People, r.Licensed, Pct(Adopt(r)), change is null ? null : $"{change:+0.#;-0.#;0}", Pct(Ratio(r.Active, r.People)),
                        r.Active > 0 ? Math.Round(r.Interactions / (double)r.Active, 1) : null, r.Active > 0 ? Math.Round(r.ActiveDays / (double)r.Active, 1) : null]);
        }
        if (changes.Count > 0)
        {
            var best = changes.MaxBy(c => c.Points);
            Kpis.Add(new($"Most improved {by.ToLowerInvariant()}", best.Name, $"{best.Points:+0.#;-0.#;0} points on the period before"));
            after.Add(new("Most improved", Chart.Bars(changes.OrderByDescending(c => c.Points).Take(10).Select(c => new Bar(c.Name, c.Points, Note: $"{c.Points:+0.#;-0.#;0} points", Slot: c.Points >= 0 ? 3 : 8)).ToList(),
                Fmt.Decimal1, "Change in adoption, points"), $"Adoption now, less adoption in the period before, in percentage points, by {by.ToLowerInvariant()}.", "half"));
        }
        var changed = changes.GroupBy(c => c.Name).ToDictionary(g => g.Key, g => g.First().Points);
        if (by == "Department")
        {
            after.Add(new("Ranking, against the period before", new HtmlString($"<p class=\"sub\" id=\"ranking\">Rank by: {choices}</p><p class=\"muted\">Departments are ranked in All departments above, with their change on the period before. Choose division, country or office to rank those instead.</p>"), null, "full"));
            return (by, changed, after);
        }
        after.Add(new("Ranking, against the period before", new HtmlString($"<p class=\"sub\" id=\"ranking\">Rank by: {choices}</p>" + t.Render().Value),
            "Adoption: of the people holding a license at the end of the period, the share who used Copilot while licensed. Interactions per active person counts all use, including by people since disabled. Ranked only where the group has at least the minimum group size of people and of licenses.", "full"));
        return (by, changed, after);
    }

    sealed class Row
    {
        public string Department { get; set; } = "";
        public int People { get; set; }
        public int Licensed { get; set; }
        public int Active { get; set; }
        public int ActiveLicensed { get; set; }
        public long ActiveDays { get; set; }
        public long Sessions { get; set; }
        public long MultiTurn { get; set; }
    }

    protected override async Task BuildAsync()
    {
        /* The ranking was the Leaderboard: turned off in Admin › Reports shown by that address (HiddenReports.Parts). Its
           groups are read alongside the page's own figures unless a department is asked for, which leaves it out. */
        var rankingShown = !CopilotInsights.Pages.HiddenReports.Is(HiddenReports, "/adoption/leaderboard");
        var groupsTask = rankingShown && string.IsNullOrEmpty(Request.Query["dept"].FirstOrDefault()) ? GroupsAsync(RankedBy()) : null;
        /* Each person's sums over the period are the nightly totals' (Ctx.PerPerson), one row a person. */
        var rows = await Db.QueryAsync<Row>($@"
WITH act AS (SELECT pp.UserKey, pp.Interactions AS I, pp.LicensedUse AS L, pp.ActiveDays AS D, pp.Sessions AS S, pp.MultiTurnSessions AS MT
             FROM {Ctx.PerPerson()} AS pp)
SELECT ISNULL(NULLIF(du.Department, N''), N'(no department)') AS Department,
       COUNT(*) AS People, COUNT(ld.UserKey) AS Licensed, COUNT(CASE WHEN a.I > 0 THEN 1 END) AS Active,
       COUNT(CASE WHEN a.L > 0 AND ld.UserKey IS NOT NULL THEN 1 END) AS ActiveLicensed, ISNULL(SUM(CAST(a.D AS BIGINT)), 0) AS ActiveDays,
       ISNULL(SUM(CAST(a.S AS BIGINT)), 0) AS Sessions, ISNULL(SUM(CAST(a.MT AS BIGINT)), 0) AS MultiTurn
FROM bi.DimUser AS du {Ctx.Scope("du.UserKey")}
LEFT JOIN act AS a ON a.UserKey = du.UserKey
LEFT JOIN bi.FactLicensedDay AS ld ON ld.UserKey = du.UserKey AND ld.DateKey = @k1
WHERE du.DirectoryStatus = N'Found' AND du.AccountEnabled = 1
GROUP BY ISNULL(NULLIF(du.Department, N''), N'(no department)');", Ctx.Args());
        var shown = FoldSmall(rows, r => r.People, small => new Row
        {
            Department = "Smaller departments", People = small.Sum(r => r.People), Licensed = small.Sum(r => r.Licensed), Active = small.Sum(r => r.Active),
            ActiveLicensed = small.Sum(r => r.ActiveLicensed), ActiveDays = small.Sum(r => r.ActiveDays), Sessions = small.Sum(r => r.Sessions), MultiTurn = small.Sum(r => r.MultiTurn),
        }, out var folded).OrderByDescending(r => r.Licensed >= Ctx.MinGroupSize).ThenByDescending(r => Ratio(r.ActiveLicensed, r.Licensed) ?? -1).ThenByDescending(r => r.Licensed).ToList();

        /* The page had no headline: how many departments, and the two ends of adoption among those large enough to rank. */
        var ranked = shown.Where(r => r.Department != "Smaller departments" && r.Licensed >= Ctx.MinGroupSize).OrderByDescending(r => r.ActiveLicensed / (double)r.Licensed).ToList();
        Kpis.Add(new("Departments", Num(rows.Count), $"in {Here}, from Active Directory"));
        /* A department's name opens this page on it, as choosing it below does. */
        string Only(Row r) => Ctx.Url(Request.Path, extra: "dept=" + Uri.EscapeDataString(r.Department));
        if (ranked.Count > 1)
        {
            Kpis.Add(new("Department with the highest adoption", ranked[0].Department, $"{Pct(Ratio(ranked[0].ActiveLicensed, ranked[0].Licensed))} of its license holders used Copilot in the period", Href: Only(ranked[0])));
            Kpis.Add(new("Department with the lowest adoption", ranked[^1].Department, $"{Pct(Ratio(ranked[^1].ActiveLicensed, ranked[^1].Licensed))} of its license holders used Copilot in the period", Href: Only(ranked[^1])));
        }

        /* One department, or every one (the default). The departments offered are those shown, smaller ones combined. */
        var (dept, picker) = DepartmentFilter(shown.Select(r => r.Department));
        Panels.Add(new("Choose a department", picker));
        if (dept is not null) shown = shown.Where(r => r.Department == dept).ToList();
        /* In the chart and the table, every name but the combined smaller ones, until one is chosen. */
        string? Pick(Row r) => dept is null && r.Department != "Smaller departments" ? Only(r) : null;
        var ranking = dept is null && rankingShown ? await RankingAsync(groupsTask ?? GroupsAsync(RankedBy())) : default;
        var withChange = ranking.By == "Department";

        Panels.Add(new("Adoption by department",
            Chart.Adoption(shown.Select(r => new Chart.AdoptionGroup(r.Department, r.ActiveLicensed, r.Licensed, Pick(r))), Ctx.MinGroupSize, "Adoption by department", 25),
            $"Departments with at least {Ctx.MinGroupSize} license holders, highest first; the rest are combined in the last bar, so no bar rests on one or two people. The table has every department."));
        var t = new Table
        {
            Headers = ["Department", "People", "License holders", "Share with a license", "Used Copilot", "Adoption", .. (withChange ? new[] { "Change (points)" } : []), "Days active per person", "Sessions with a follow-up"],
            Numeric = new HashSet<int>(Enumerable.Range(1, withChange ? 8 : 7)),
        };
        foreach (var r in shown)
        {
            List<object?> cells = [Pick(r) is { } href ? new Link(r.Department, href) : r.Department, r.People, r.Licensed, Pct(Ratio(r.Licensed, r.People)), r.Active, AdoptionAtMin(r.ActiveLicensed, r.Licensed)];
            if (withChange) cells.Add(ranking.Changes.TryGetValue(r.Department, out var c) ? $"{c:+0.#;-0.#;0}" : null);
            cells.AddRange([r.Active > 0 ? Math.Round(r.ActiveDays / (double)r.Active, 1) : null, Pct(Ratio(r.MultiTurn, r.Sessions))]);
            t.Rows.Add([.. cells]);
        }
        CsvTable = t;
        Panels.Add(new("All departments", t.Render(),
            (folded > 0 ? FoldNote(folded, "department") + " " : "") + (withChange ? "Change (points): adoption now less adoption in the period before, where the department had the minimum group size of people and of licenses both times." : ""),
            "full", CsvUrl()));
        if (ranking.Panels is { } more) Panels.AddRange(more);
    }
}

public sealed class NotStartedModel(PageServices s) : InsightsPage(s)
{
    /// <summary>Who the list holds, for its own count and Adoption's: the figure and the list it opens agree.</summary>
    internal static string Who(ReportContext c) => $@"
FROM bi.DimUser AS du {c.Scope("du.UserKey")}
WHERE du.CopilotLicenseToday = N'Licensed' AND du.LicensedSinceUtc <= DATEADD(DAY, -14, @d1)
  AND NOT {FirstUse.Ever(c, "du.UserKey")}
  AND {CopilotInsights.Data.ProtectedLevels.Not("du.UserPrincipalName")}";

    public override string Title => "Not started";
    public override string Persona => "Adoption";
    public override string? OwnWindow => "today's licenses";
    public override string? RequiredRole => Roles.Adoption;
    public override string Intro => "People licensed for at least 14 days who have never used Copilot: who to invite to training or a champion session.";

    static readonly Dictionary<string, string> Sorts = new()
    {
        ["since"] = "du.LicensedSinceUtc, du.DisplayName", ["name"] = "du.DisplayName, du.UserPrincipalName",
        ["dept"] = "du.Department, du.DisplayName", ["manager"] = "du.ManagerName, du.DisplayName",
        ["upn"] = "du.UserPrincipalName", ["title"] = "du.JobTitle, du.DisplayName, du.UserPrincipalName",
    };
    static readonly Dictionary<string, string> SortLabels = new()
    {
        ["since"] = "Licensed longest", ["name"] = "Name", ["dept"] = "Department", ["manager"] = "Manager",
    };

    sealed class Row
    {
        public string? DisplayName { get; set; }
        public string UserPrincipalName { get; set; } = "";
        public string? Department { get; set; }
        public string? JobTitle { get; set; }
        public string? ManagerName { get; set; }
        public DateTime? LicensedSinceUtc { get; set; }
        public int TotalRows { get; set; }
    }

    protected override async Task BuildAsync()
    {
        var pg = new Paging(Request, Sorts, "since");
        var rows = await Db.QueryAsync<Row>($@"
SELECT du.DisplayName, du.UserPrincipalName, du.Department, du.JobTitle, du.ManagerName, du.LicensedSinceUtc, COUNT(*) OVER () AS TotalRows
{Who(Ctx)}
  AND (@q IS NULL OR du.DisplayName LIKE @q OR du.UserPrincipalName LIKE @q OR du.Department LIKE @q)
{pg.OrderAndPage()};", Ctx.Args(new { q = pg.Like }));
        pg.Total = rows.FirstOrDefault()?.TotalRows ?? 0;
        var t = new Table
        {
            Headers = ["Name", "Sign-in", "Department", "Job title", "Manager", "Licensed since", "Days licensed"],
            Numeric = new HashSet<int> { 6 }, EmptyText = "Everyone licensed for 14 days or more has started.",
            /* Days licensed sorts by the same key as Licensed since: longest first. */
            Server = new(pg, Request, ["name", "upn", "dept", "title", "manager", "since", "since"], new HashSet<int> { 6 }),
        };
        foreach (var r in rows)
            t.Rows.Add([r.DisplayName, r.UserPrincipalName, r.Department, r.JobTitle, r.ManagerName, r.LicensedSinceUtc?.Date,
                        r.LicensedSinceUtc is null ? null : (Ctx.End - r.LicensedSinceUtc.Value.Date).Days]);
        CsvTable = t;
        Kpis.Add(new("Licensed, not started", Num(pg.Total), "licensed at least 14 days ago, never used Copilot"));
        Panels.Add(new("People", new HtmlString(await ProtectedNoteAsync() + pg.Controls(Request, SortLabels).Value + t.Render().Value), null, "full", CsvUrl()));
    }
}
