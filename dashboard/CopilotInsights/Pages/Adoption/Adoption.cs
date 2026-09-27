using CopilotInsights.Charts;
using CopilotInsights.Data;
using CopilotInsights.Infrastructure;
using Microsoft.AspNetCore.Html;

namespace CopilotInsights.Pages.Adoption;

public sealed class IndexModel(PageServices s, Metrics m) : InsightsPage(s)
{
    public override string Title => "Funnel and trend";
    public override string Persona => "Adoption";
    public override string Intro => "From license to habit: how many licensed people have started, are active now, and go beyond one-off questions.";

    protected override async Task BuildAsync()
    {
        var t = await m.TotalsAsync(Ctx);
        var tr = await m.TrendAsync(Ctx);
        var started = await Db.ScalarAsync<int>($@"
SELECT COUNT(*) FROM bi.FactLicensedDay AS ld {Ctx.Scope("ld.UserKey")}
WHERE ld.DateKey = @k1 AND EXISTS (SELECT 1 FROM dash.UserDay AS ud WHERE ud.UserKey = ld.UserKey AND ud.Interactions > 0);", Ctx.Args());

        Kpis.Add(new("Licensed today", Num(t.LicensedNow)));
        Kpis.Add(new("Have started", Pct(Ratio(started, t.LicensedNow)), $"{started:#,0} licensed people have used it at least once"));
        Kpis.Add(new("Active in the period", Pct(Ratio(t.ActiveLicensed, t.LicensedNow)), $"{t.ActiveLicensed:#,0} licensed people"));
        Kpis.Add(new("Multi-turn users", Pct(Ratio(t.MultiTurnUsers, t.Active)), "of active people had a conversation, not only one-off prompts"));

        var stages = new List<Bar>
        {
            new("People in scope", t.People, Note: "enabled accounts in the directory"),
            new("Licensed today", t.LicensedNow, Slot: 1),
            new("Licensed and started", started, Slot: 1),
            new("Licensed, active in the period", t.ActiveLicensed, Slot: 1),
            new("Active in the last 7 days", t.ActiveLast7, Slot: 1),
            new("Had a multi-turn session", t.MultiTurnUsers, Slot: 1),
        };
        Panels.Add(new("Adoption funnel", Chart.Bars(stages, Fmt.Count, "Adoption funnel"),
            "Each stage is a count of people. The last two include anyone active, licensed or on Copilot Chat."));

        var firsts = await Db.QueryAsync<(DateTime Bucket, int N)>($@"
WITH f AS (SELECT ud.UserKey, MIN(ud.DateKey) AS FirstKey FROM dash.UserDay AS ud {Ctx.Scope("ud.UserKey")} WHERE ud.Interactions > 0 GROUP BY ud.UserKey)
SELECT {Ctx.BucketSql("d")}, COUNT(*) FROM f JOIN bi.DimDate AS d ON d.DateKey = f.FirstKey
WHERE f.FirstKey BETWEEN @k0 AND @k1 GROUP BY {Ctx.BucketSql("d")};", Ctx.Args());
        var buckets = Buckets();
        Panels.Add(new("People using Copilot for the first time",
            Chart.Columns(Labels(), [new Series("First-time users", buckets.Select(b => (double?)firsts.Where(f => f.Bucket == b.Key).Sum(f => f.N)).ToList(), 3)], Fmt.Count, "First-time users"),
            TrendNote, "half"));
        Panels.Add(new("Licensed and active",
            Chart.Line(Labels(), [
                new Series("Licensed", tr.Select(w => (double?)w.Licensed).ToList(), 1),
                new Series("Active, licensed", tr.Select(w => (double?)w.ActiveLicensed).ToList(), 3),
            ], Fmt.Count, "Licensed and active"), null, "half"));
    }
}

public sealed class CohortsModel(PageServices s) : InsightsPage(s)
{
    public override string Title => "License cohorts";
    public override string Persona => "Adoption";
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
WHERE du.CopilotLicenseToday = N'Licensed' AND du.LicensedSinceUtc >= DATEADD(MONTH, -12, @d1);
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
        Panels.Add(new("Share of each cohort active, week by week after their license",
            Chart.Heatmap(cohortsShown.Select(c => $"{c:MMM yyyy} ({rows.First(r => r.Cohort == c).Size:#,0})").ToList(),
                Enumerable.Range(1, weeks).Select(k => $"Wk {k}").ToList(), grid, Fmt.Percent, "Cohort retention", 1),
            $"People licensed today, by the month their current license began, over the last 12 months. Empty cells are weeks that have not happened yet for enough of the cohort. Cohorts under {Ctx.MinGroupSize} people are left out. This view ignores the period filter."));

        var ttf = await Db.QueryAsync<(string Bucket, int N)>($@"
WITH f AS (SELECT du.UserKey, CAST(du.LicensedSinceUtc AS DATE) AS Since,
                  (SELECT MIN(ud.DateKey) FROM dash.UserDay AS ud WHERE ud.UserKey = du.UserKey AND ud.Interactions > 0) AS FirstKey
           FROM bi.DimUser AS du {Ctx.Scope("du.UserKey")}
           WHERE du.CopilotLicenseToday = N'Licensed' AND du.LicensedSinceUtc IS NOT NULL),
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
            "People licensed today, by how long after their license they first used Copilot."));
    }
}

public sealed class DepartmentsModel(PageServices s) : InsightsPage(s)
{
    public override string Title => "Departments";
    public override string Persona => "Adoption";
    public override string Intro => "Adoption by department, from Active Directory. Departments smaller than the minimum group size are combined.";

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
        var rows = await Db.QueryAsync<Row>($@"
WITH act AS (SELECT ud.UserKey, SUM(ud.Interactions) AS I, SUM(ud.LicensedUse) AS L, COUNT(CASE WHEN ud.Interactions > 0 THEN 1 END) AS D,
                    SUM(ud.Sessions) AS S, SUM(ud.MultiTurnSessions) AS MT
             FROM dash.UserDay AS ud WHERE ud.DateKey BETWEEN @k0 AND @k1 GROUP BY ud.UserKey)
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
        }, out var folded).OrderByDescending(r => Ratio(r.ActiveLicensed, r.Licensed) ?? -1).ToList();

        Panels.Add(new("Adoption by department",
            Chart.Bars(shown.Take(25).Select(r => new Bar(r.Department, Ratio(r.ActiveLicensed, r.Licensed), Note: $"{r.ActiveLicensed:#,0} of {r.Licensed:#,0} licensed")).ToList(), Fmt.Percent, "Adoption by department", 1),
            shown.Count > 25 ? "The 25 highest; the table has them all." : null));
        var t = new Table
        {
            Headers = ["Department", "People", "Licensed", "Share licensed", "Active", "Adoption", "Days active per person", "Multi-turn sessions"],
            Numeric = new HashSet<int> { 1, 2, 3, 4, 5, 6, 7 },
        };
        foreach (var r in shown)
            t.Rows.Add([r.Department, r.People, r.Licensed, Pct(Ratio(r.Licensed, r.People)), r.Active, Pct(Ratio(r.ActiveLicensed, r.Licensed)),
                        r.Active > 0 ? Math.Round(r.ActiveDays / (double)r.Active, 1) : null, Pct(Ratio(r.MultiTurn, r.Sessions))]);
        Panels.Add(new("All departments", t.Render(), folded > 0 ? $"{folded} department(s) under {Ctx.MinGroupSize} people are combined." : null));
    }
}

public sealed class NotStartedModel(PageServices s) : InsightsPage(s)
{
    public override string Title => "Not started";
    public override string Persona => "Adoption";
    public override string? RequiredRole => Roles.Adoption;
    public override string Intro => "People licensed for at least 14 days who have never used Copilot: who to invite to training or a champion session.";

    static readonly Dictionary<string, string> Sorts = new()
    {
        ["since"] = "du.LicensedSinceUtc, du.DisplayName", ["name"] = "du.DisplayName, du.UserPrincipalName",
        ["dept"] = "du.Department, du.DisplayName", ["manager"] = "du.ManagerName, du.DisplayName",
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
FROM bi.DimUser AS du {Ctx.Scope("du.UserKey")}
WHERE du.CopilotLicenseToday = N'Licensed' AND du.LicensedSinceUtc <= DATEADD(DAY, -14, @d1)
  AND NOT EXISTS (SELECT 1 FROM dash.UserDay AS ud WHERE ud.UserKey = du.UserKey AND ud.Interactions > 0)
  AND (@q IS NULL OR du.DisplayName LIKE @q OR du.UserPrincipalName LIKE @q OR du.Department LIKE @q)
{pg.OrderAndPage()};", Ctx.Args(new { q = pg.Like }));
        pg.Total = rows.FirstOrDefault()?.TotalRows ?? 0;
        var t = new Table
        {
            Headers = ["Name", "Sign-in", "Department", "Job title", "Manager", "Licensed since", "Days licensed"],
            Numeric = new HashSet<int> { 6 }, EmptyText = "Everyone licensed for 14 days or more has started.",
        };
        foreach (var r in rows)
            t.Rows.Add([r.DisplayName, r.UserPrincipalName, r.Department, r.JobTitle, r.ManagerName, r.LicensedSinceUtc?.Date,
                        r.LicensedSinceUtc is null ? null : (Ctx.End - r.LicensedSinceUtc.Value.Date).Days]);
        CsvTable = t;
        Kpis.Add(new("Licensed, not started", Num(pg.Total), "licensed at least 14 days ago, never used Copilot"));
        Panels.Add(new("People", new HtmlString(pg.Controls(Request, SortLabels).Value + t.Render().Value), null, "full", CsvUrl()));
    }
}
