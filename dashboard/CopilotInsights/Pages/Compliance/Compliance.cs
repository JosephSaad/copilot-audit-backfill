using CopilotInsights.Charts;
using CopilotInsights.Data;
using CopilotInsights.Infrastructure;
using Microsoft.AspNetCore.Html;

namespace CopilotInsights.Pages.Compliance;

static class Sites
{
    /// <summary>A OneDrive site names its owner, so pages without the Compliance role show it as one group.</summary>
    public static string Show(string site, bool named) =>
        !named && site.Contains("/personal/", StringComparison.OrdinalIgnoreCase) ? "OneDrive (personal sites)" : site;

    /// <summary>Label names from Admin › Reference data, with the id where there is none.</summary>
    public const string LabelName = "COALESCE(sl.Name, N'Label ' + LEFT(rd.Item, 8))";
}

public sealed class IndexModel(PageServices s, Metrics m) : InsightsPage(s)
{
    public override string Title => "Sensitive content";
    public override string Persona => "Compliance";
    public override string Intro => "How often Copilot read labeled content (files, emails and messages carrying a Microsoft Purview sensitivity label), which labels, from which sites, and what it was blocked from.";

    protected override async Task BuildAsync()
    {
        /* The reads are independent: started together, awaited where used. */
        var trTask = m.TrendAsync(Ctx);
        /* Counted as Access to labeled content lists them (labeled content, or blocked from content), so the figure
           and the list it opens agree; the daily totals' count of labeled content alone left out those only blocked.
           Anyone with a row of either kind that counts an interaction: the same people as keeping those whose sum of
           either kind is above zero, as a count is never below zero, without summing. From the refresh's per person
           sums of the period (all labels, all blocked sites) where it has them: a distinct count over the period's days
           took a second for two years. */
        var res = Ctx.ResourceTotals;
        var peopleTask = Db.ScalarAsync<int>(res ? $@"
SELECT COUNT(*) FROM (
    SELECT DISTINCT rp.UserKey FROM {Ctx.ResourceSums("'A', 'X'")} AS rp {Ctx.Scope("rp.UserKey")}) AS x
JOIN bi.DimUser AS du ON du.UserKey = x.UserKey;" : $@"
SELECT COUNT(*) FROM (
    SELECT DISTINCT rd.UserKey FROM dash.ResourceDay AS rd {Ctx.Scope("rd.UserKey")}
    WHERE rd.Kind IN ('Label', 'Blocked') AND rd.DateKey BETWEEN @k0 AND @k1 AND rd.Interactions > 0) AS x
JOIN bi.DimUser AS du ON du.UserKey = x.UserKey;", Ctx.Args());
        /* Counted per day and label id first, then by trend point, then named: grouping the daily rows on the name worked
           it out for each, and on the point met each row with its date. */
        var labelsTask = Db.QueryAsync<(DateTime Bucket, string Label, int Rank, int N)>($@"
WITH x AS (SELECT rd.DateKey, rd.Item, SUM(rd.Interactions) AS N
           FROM dash.ResourceDay AS rd {Ctx.Scope("rd.UserKey")}
           WHERE rd.Kind = 'Label' AND rd.DateKey BETWEEN @k0 AND @k1
           GROUP BY rd.DateKey, rd.Item),
g AS (SELECT {Ctx.BucketSql("d")} AS Bucket, x.Item, SUM(x.N) AS N
      FROM x JOIN bi.DimDate AS d ON d.DateKey = x.DateKey
      GROUP BY {Ctx.BucketSql("d")}, x.Item)
SELECT rd.Bucket, {Sites.LabelName}, ISNULL(MAX(sl.Rank), 255), SUM(rd.N)
FROM g AS rd
LEFT JOIN dash.SensitivityLabel AS sl ON sl.LabelId = rd.Item
GROUP BY rd.Bucket, {Sites.LabelName};", Ctx.Args());
        /* Where the refresh keeps each person's sums of the period per site, the interactions and the people are both
           read from them: a person with a site's row in the period reached it. Before, the people at each site came from
           the refresh's list of who reached which site and when (dash.SitePerson), one comparison a person, and the
           interactions were summed from the days. A distinct count of each site's people over every day of the period took
           40 seconds of processor for two years and asked for gigabytes of memory, which a busy server makes it wait
           for; the list 6 seconds, and summing the days by their site names 2 more. Each site is counted by its number or
           key, then named. */
        var sitePeople = $@"p AS (SELECT CASE c.Kind WHEN 'S' THEN 'Site' ELSE 'Blocked' END AS Kind, CAST(LEFT(s.Site, 400) AS NVARCHAR(400)) AS Item, SUM(c.People) AS People
      FROM (SELECT sp.Kind, sp.SiteKey, COUNT(*) AS People
            FROM dash.SitePerson AS sp {Ctx.Scope("sp.UserKey")}
            WHERE sp.Kind IN ('S', 'B') AND {ReportContext.Reached("sp")}
            GROUP BY sp.Kind, sp.SiteKey) AS c
      JOIN bi.DimSite AS s ON s.SiteKey = c.SiteKey
      GROUP BY c.Kind, s.Site)";
        var sitesTask = Db.QueryAsync<(string Kind, string Item, int N, int People)>(res ? $@"
WITH k AS (SELECT rp.Kind, rp.ItemKey, SUM(rp.N) AS N, COUNT(*) AS People
           FROM {Ctx.ResourceSums("'S', 'B'")} AS rp {Ctx.Scope("rp.UserKey")}
           GROUP BY rp.Kind, rp.ItemKey)
SELECT ri.Kind, ri.Item, k.N, k.People FROM k JOIN dash.ResourceItem AS ri ON ri.ItemKey = k.ItemKey;" : Ctx.SitePeople ? $@"
WITH n AS (SELECT rd.Kind, rd.Item, SUM(rd.Interactions) AS N
           FROM dash.ResourceDay AS rd {Ctx.Scope("rd.UserKey")}
           WHERE rd.Kind IN ('Site', 'Blocked') AND rd.DateKey BETWEEN @k0 AND @k1
           GROUP BY rd.Kind, rd.Item),
{sitePeople}
SELECT n.Kind, n.Item, n.N, ISNULL(p.People, 0)
FROM n LEFT JOIN p ON p.Kind = n.Kind AND p.Item = n.Item;" : $@"
SELECT rd.Kind, rd.Item, SUM(rd.Interactions), COUNT(DISTINCT rd.UserKey)
FROM dash.ResourceDay AS rd {Ctx.Scope("rd.UserKey")}
WHERE rd.Kind IN ('Site', 'Blocked') AND rd.DateKey BETWEEN @k0 AND @k1
GROUP BY rd.Kind, rd.Item;", Ctx.Args());
        /* Counted per type key, then named: from each person's resources read in the period where the refresh keeps
           them, else from every resource read (2 to 6 seconds for two years). */
        var typesTask = Db.QueryAsync<(string Type, long N, long Labeled, long Blocked)>($@"
WITH g AS ({(res ? $@"SELECT r.ResourceTypeKey, SUM(r.Reads) AS N, SUM(CASE WHEN r.SensitivityLabelKey <> 1 THEN r.Reads ELSE 0 END) AS L,
                  SUM(CASE WHEN r.IsBlocked = 1 THEN r.Reads ELSE 0 END) AS B
           FROM {Ctx.ReadMix()} AS r {Ctx.Scope("r.UserKey")}
           GROUP BY r.ResourceTypeKey" : $@"SELECT f.ResourceTypeKey, COUNT_BIG(*) AS N, SUM(CASE WHEN f.SensitivityLabelKey <> 1 THEN 1 ELSE 0 END) AS L, SUM(CAST(f.IsBlocked AS INT)) AS B
           FROM bi.FactAccessedResource AS f {Ctx.Scope("f.UserKey")}
           WHERE f.DateKey BETWEEN @k0 AND @k1
           GROUP BY f.ResourceTypeKey")})
SELECT COALESCE(vn.Name, t.ResourceType), SUM(g.N), SUM(g.L), SUM(g.B)
FROM g JOIN bi.DimResourceType AS t ON t.ResourceTypeKey = g.ResourceTypeKey
LEFT JOIN dash.ValueName AS vn ON vn.ColumnId = 31 AND vn.Value = t.ResourceType
GROUP BY t.ResourceType, vn.Name;", Ctx.Args());
        var dlpTask = Db.ScalarAsync<int>("SELECT CASE WHEN OBJECT_ID(N'audit.DlpDay', N'U') IS NOT NULL AND EXISTS (SELECT 1 FROM audit.DlpDay) THEN 1 ELSE 0 END;");
        await Task.WhenAll(trTask, peopleTask, labelsTask, sitesTask, typesTask, dlpTask);
        var tr = await trTask;
        long labeled = tr.Sum(w => w.Labeled), all = tr.Sum(w => w.Interactions), blocked = tr.Sum(w => w.Blocked);
        var people = await peopleTask;
        Kpis.Add(new("Interactions with labeled content", Num(labeled), $"{Pct(Ratio(labeled, all))} of all interactions"));
        /* Each figure opens a page with the same number. Access to labeled content lists these people; the blocked
           count is Security's Blocked resources (the list of people counted people, not blocked interactions). */
        Kpis.Add(new("People", Num(people), "who read labeled content through Copilot, or were blocked from content, in the period; accounts since disabled included", Href: Open("/compliance/access")));
        Kpis.Add(new("Blocked resources", Num(blocked), "interactions where a policy or permissions stopped Copilot reading content", Href: Open("/security")));

        var labels = await labelsTask;
        var order = labels.GroupBy(l => l.Label).OrderBy(g => g.Max(l => l.Rank)).ThenByDescending(g => g.Sum(l => l.N)).Select(g => g.Key).ToList();
        var buckets = Buckets();
        var shown = order.Take(7).ToList();
        var series = shown.Select((l, i) => new Series(l, buckets.Select(b => (double?)labels.Where(x => x.Label == l && x.Bucket == b.Key).Sum(x => x.N)).ToList(), i + 1)).ToList();
        if (order.Count > 7)
            series.Add(new Series("Other labels", buckets.Select(b => (double?)labels.Where(x => !shown.Contains(x.Label) && x.Bucket == b.Key).Sum(x => x.N)).ToList(), 8));
        Panels.Add(new("Labeled content by label", Chart.Columns(Labels(), series, Fmt.Count, "Interactions by sensitivity label"),
            $"An interaction that read content with two labels counts under each. Labels are ordered least to most sensitive when Admin › Reference data ranks them. {TrendNote}"));

        Panels.Add(new("Share of interactions that read labeled content",
            Chart.Line(Labels(), [new Series("Labeled", tr.Select(w => Ratio(w.Labeled, w.Interactions)).ToList(), 1)], Fmt.Percent, "Labeled share"), TrendNote, "half"));
        Panels.Add(new("By label, whole period",
            Chart.Bars(order.Select(l => new Bar(l, labels.Where(x => x.Label == l).Sum(x => x.N))).ToList(), Fmt.Count, "Interactions by label"), "Interactions that read content with each label, over the whole period.", "half"));

        var named = Viewer.Has(Roles.Compliance) && !PrivacyMode;
        var sites = await sitesTask;
        HtmlString SiteBars(string kind, int slot) => Chart.Bars(sites.Where(x => x.Kind == kind)
            .GroupBy(x => Sites.Show(x.Item, named)).Select(g => (Site: g.Key, N: g.Sum(x => x.N), People: g.Sum(x => x.People)))
            .OrderByDescending(x => x.N).Take(12).Select(x => new Bar(x.Site, x.N, Note: $"{x.People:#,0} people", Slot: slot)).ToList(), Fmt.Count, kind + " sites");
        Panels.Add(new("Where labeled content lives", SiteBars("Site", 3),
            "The sites holding the labeled content Copilot read, by interactions; hover for how many people." + (named ? "" : PrivacyMode ? " Personal OneDrive sites are grouped: privacy mode is on." : " Personal OneDrive sites are grouped; the Compliance role shows them."), "half"));
        Panels.Add(new("Sites Copilot was blocked from", SiteBars("Blocked", 7),
            "Sites where a policy or permissions stopped Copilot reading content. Usually a sign the controls are working; a spike is worth a look.", "half"));

        var types = await typesTask;
        Panels.Add(new("What Copilot read, by type",
            Chart.Bars(types.OrderByDescending(t => t.N).Take(12).Select(t => new Bar(t.Type, t.N, Note: $"{t.Labeled:#,0} labeled, {t.Blocked:#,0} blocked")).ToList(), Fmt.Count, "Accessed resources by type"),
            "Every resource an interaction read: files, emails, Teams messages, web results. Counted as resources read, not interactions: one interaction can read several. Hover for how many were labeled or blocked."));

        /* The organization's data loss prevention matches, where the activity database loads the DLP record types:
           by policy and over time, for the part of the organization shown (matches that name no one count for the whole
           organization only). Counts, no file or message named. */
        if (await dlpTask == 1)
        {
            var dlpFrom = Ctx.Root is null ? "audit.DlpDay AS m" : $"audit.DlpDay AS m JOIN bi.DimUser AS u ON u.UserPrincipalName = m.Upn {Ctx.Scope("u.UserKey")}";
            var dlp = await Db.QueryAsync<(DateTime Bucket, string Workload, long Matches, long Undone)>($@"
SELECT {Ctx.BucketSql("d")}, m.Workload, SUM(CAST(m.Matches AS BIGINT)), SUM(CAST(m.Undone AS BIGINT))
FROM {dlpFrom} JOIN bi.DimDate AS d ON d.[Date] = m.[Day]
WHERE m.[Day] BETWEEN @d0 AND @d1 GROUP BY {Ctx.BucketSql("d")}, m.Workload;", Ctx.Args());
            var policies = await Db.QueryAsync<(string Policy, long Matches, int People, long Undone)>($@"
SELECT m.PolicyName, SUM(CAST(m.Matches AS BIGINT)), COUNT(DISTINCT NULLIF(m.Upn, N'')), SUM(CAST(m.Undone AS BIGINT))
FROM {dlpFrom} WHERE m.[Day] BETWEEN @d0 AND @d1 GROUP BY m.PolicyName ORDER BY SUM(CAST(m.Matches AS BIGINT)) DESC;", Ctx.Args());
            long matches = dlp.Sum(x => x.Matches), undone = dlp.Sum(x => x.Undone);
            Kpis.Add(new("DLP rule matches", Num(matches), $"{Count(policies.Count, "policy", "policies")}; {Pct(Ratio(undone, matches))} undone (overridden, no longer matching, or reported as a false positive)", Href: null));
            var workloads = dlp.Select(x => x.Workload).Distinct().OrderBy(x => x).ToList();
            Panels.Add(new("Data loss prevention matches", Chart.Columns(Labels(), workloads.Select((w, i) => new Series(w.Length == 0 ? "(not named)" : w,
                    buckets.Select(b => (double?)dlp.Where(x => x.Bucket == b.Key && x.Workload == w).Sum(x => x.Matches)).ToList(), i + 1)).ToList(), Fmt.Count, "DLP rule matches"),
                $"Each time a data loss prevention (DLP) rule matched content or an action, by workload, from the audit log's DLP records. All DLP matches, not only those involving Copilot. {TrendNote}", "half"));
            Panels.Add(new("Matches by policy", Chart.Bars(policies.Take(12).Select(p => new Bar(p.Policy, p.Matches,
                    Note: $"{(p.People >= Ctx.MinGroupSize ? p.People.ToString("#,0") : $"fewer than {Ctx.MinGroupSize}")} people; {p.Undone:#,0} undone")).ToList(), Fmt.Count, "DLP matches by policy"),
                "The policies that matched most. Undone: the match was overridden, the item stopped matching, or someone reported it as a false positive; a policy with many is worth tuning.", "half"));
        }
    }
}

public sealed class AccessModel(PageServices s) : InsightsPage(s)
{
    public override string Title => "Access to labeled content";
    public override string Persona => "Compliance";
    public override string? RequiredRole => Roles.Compliance;
    public override string Intro => "Who read labeled content through Copilot in the period, how often, and the most sensitive label they reached. Everyone is listed, accounts since disabled included, so nothing is hidden when someone leaves.";

    static readonly Dictionary<string, string> Sorts = new()
    {
        ["most"] = "x.N DESC, du.DisplayName", ["sens"] = "x.TopRank DESC, x.N DESC", ["name"] = "du.DisplayName", ["dept"] = "du.Department, du.DisplayName",
        /* x is #x when the page is picked and #pick when its rows are put back in order: both carry these columns. */
        ["upn"] = "du.UserPrincipalName", ["days"] = "x.Days DESC, du.DisplayName, du.UserPrincipalName",
        ["sites"] = "x.Sites DESC, du.DisplayName, du.UserPrincipalName", ["blocked"] = "x.Blocked DESC, du.DisplayName, du.UserPrincipalName",
    };
    static readonly Dictionary<string, string> SortLabels = new()
    {
        ["most"] = "Most interactions", ["sens"] = "Most sensitive label", ["name"] = "Name", ["dept"] = "Department",
    };

    sealed class Row
    {
        public string? DisplayName { get; set; }
        public string UserPrincipalName { get; set; } = "";
        public string? Department { get; set; }
        public int N { get; set; }
        public int Days { get; set; }
        public string? TopLabel { get; set; }
        public int Sites { get; set; }
        public int Blocked { get; set; }
        public int TotalRows { get; set; }
    }

    protected override async Task BuildAsync()
    {
        /* The labels offered and the list are read side by side, the list for the label asked for. A label the period
           does not hold (an old link) is not one to filter by: the list is read again for all labels, as it was when the
           labels were read first. */
        var asked = Request.Query["label"].FirstOrDefault();
        var pg = new Paging(Request, Sorts, "most");
        /* The label ids first, then named: named on every day's row, each row was matched on its id text (7 seconds of
           processor for two years, against under half a second). */
        /* From the refresh's per person sums of the period where it has them: the labels read in it are those with a row. */
        var res = Ctx.ResourceTotals;
        var labelsTask = Db.QueryAsync<(string Id, string Name)>(res ? $@"
SELECT rd.Item, {Sites.LabelName} FROM dash.ResourceItem AS rd LEFT JOIN dash.SensitivityLabel AS sl ON sl.LabelId = rd.Item
WHERE rd.Kind = 'Label' AND EXISTS (SELECT 1 FROM {Ctx.ResourceSums("'L'")} AS rp WHERE rp.ItemKey = rd.ItemKey);" : $@"
WITH rd AS (SELECT DISTINCT rd.Item FROM dash.ResourceDay AS rd WHERE rd.Kind = 'Label' AND rd.DateKey BETWEEN @k0 AND @k1)
SELECT rd.Item, {Sites.LabelName} FROM rd LEFT JOIN dash.SensitivityLabel AS sl ON sl.LabelId = rd.Item;", Ctx.Args());
        /* The ranks Admin › Reference data gives labels, a few rows: each person's most sensitive label is then a CASE
           on the label id, with the ids and ranks as parameters. Joined to the ranks, every day's label row was matched
           on its id text: 10.5 seconds of processor for two years, against 8 this way. */
        var ranks = await Db.QueryAsync<(string LabelId, int Rank)>("SELECT LabelId, CAST(Rank AS INT) FROM dash.SensitivityLabel WHERE Rank IS NOT NULL;");
        var rankArgs = new Dapper.DynamicParameters();
        for (var i = 0; i < ranks.Count; i++) { rankArgs.Add($"rl{i}", ranks[i].LabelId); rankArgs.Add($"rr{i}", ranks[i].Rank); }
        string Rank(string item) => ranks.Count == 0 ? "0" : $"CASE {item} " + string.Concat(ranks.Select((_, i) => $"WHEN @rl{i} THEN @rr{i} ")) + "ELSE 0 END";
        var rank = Rank("rd.Item");
        var rowsTask = ListAsync(pg, asked);
        var (labels, rows) = await Queries(labelsTask, rowsTask);
        var label = asked is { } l && labels.Any(x => x.Id == l) ? l : null;
        if (asked is not null && label is null) rows = await ListAsync(pg, null);

        /* Each measure into a temporary table of its own, then the page, then the most sensitive label only for the rows
           shown. With the two distinct counts in one statement the optimizer asked for gigabytes of memory and still
           spilled; with the label looked up person by person (dash.ResourceDay is keyed by day, not person) it read the
           whole period per row. Either way 30 to 45 seconds for 180 days at 70,000 people; this way a few seconds. The
           sites each person reached are the refresh's list of who reached which site and when (dash.SitePerson), one
           comparison a row: a distinct count of the sites over every day of the period took 24 seconds of processor for two
           years, against under 2. Where the refresh keeps each person's sums of the period (per label, per site, all labels
           with their days, all blocked sites), the labels, the sites and the times blocked are read from them: summed from
           the days, 1 to 4 seconds for two years, and the list of sites 1.5. */
        Task<List<Row>> ListAsync(Paging pg, string? label) => Db.QueryAsync<Row>($@"
{(res ? $@"SELECT rp.UserKey,
       SUM(CASE WHEN rp.Kind = 'A' THEN {(label is null ? "rp.N" : "0")} WHEN ri.Item = @label THEN rp.N ELSE 0 END) AS N,
       MAX(CASE WHEN rp.Kind = 'A' THEN rp.Days END) AS Days, MAX(CASE WHEN rp.Kind = 'L' THEN {Rank("ri.Item")} END) AS TopRank
INTO #lab
FROM {Ctx.ResourceSums("'A', 'L'")} AS rp {Ctx.Scope("rp.UserKey")}
LEFT JOIN dash.ResourceItem AS ri ON ri.ItemKey = rp.ItemKey AND rp.Kind = 'L'
GROUP BY rp.UserKey;" : $@"SELECT rd.UserKey,
       SUM({(label is null ? "rd.Interactions" : "CASE WHEN rd.Item = @label THEN rd.Interactions ELSE 0 END")}) AS N,
       COUNT(DISTINCT rd.DateKey) AS Days, MAX({rank}) AS TopRank
INTO #lab
FROM dash.ResourceDay AS rd {Ctx.Scope("rd.UserKey")}
WHERE rd.Kind = 'Label' AND rd.DateKey BETWEEN @k0 AND @k1
GROUP BY rd.UserKey;")}
{(res ? $@"SELECT rp.UserKey, COUNT(*) AS Sites
INTO #site
FROM {Ctx.ResourceSums("'S'")} AS rp {Ctx.Scope("rp.UserKey")}
GROUP BY rp.UserKey;" : Ctx.SitePeople ? $@"SELECT sp.UserKey, COUNT(*) AS Sites
INTO #site
FROM dash.SitePerson AS sp {Ctx.Scope("sp.UserKey")}
WHERE sp.Kind = 'S' AND {ReportContext.Reached("sp")}
GROUP BY sp.UserKey;" : $@"SELECT rd.UserKey, COUNT(DISTINCT rd.Item) AS Sites
INTO #site
FROM dash.ResourceDay AS rd {Ctx.Scope("rd.UserKey")}
WHERE rd.Kind = 'Site' AND rd.DateKey BETWEEN @k0 AND @k1
GROUP BY rd.UserKey;")}
{(res ? $@"SELECT rp.UserKey, rp.N AS Blocked
INTO #blk
FROM {Ctx.ResourceSums("'X'")} AS rp {Ctx.Scope("rp.UserKey")};" : $@"SELECT rd.UserKey, SUM(rd.Interactions) AS Blocked
INTO #blk
FROM dash.ResourceDay AS rd {Ctx.Scope("rd.UserKey")}
WHERE rd.Kind = 'Blocked' AND rd.DateKey BETWEEN @k0 AND @k1
GROUP BY rd.UserKey;")}
SELECT k.UserKey, ISNULL(l.N, 0) AS N, ISNULL(l.Days, 0) AS Days, l.TopRank, ISNULL(st.Sites, 0) AS Sites, ISNULL(b.Blocked, 0) AS Blocked
INTO #x
FROM (SELECT UserKey FROM #lab UNION SELECT UserKey FROM #site UNION SELECT UserKey FROM #blk) AS k
LEFT JOIN #lab AS l ON l.UserKey = k.UserKey
LEFT JOIN #site AS st ON st.UserKey = k.UserKey
LEFT JOIN #blk AS b ON b.UserKey = k.UserKey;
SELECT x.UserKey, x.N, x.Days, x.TopRank, x.Sites, x.Blocked, COUNT(*) OVER () AS TotalRows
INTO #pick
FROM #x AS x JOIN bi.DimUser AS du ON du.UserKey = x.UserKey
WHERE (x.N > 0 OR (@label IS NULL AND x.Blocked > 0))
  AND (@q IS NULL OR du.DisplayName LIKE @q OR du.UserPrincipalName LIKE @q OR du.Department LIKE @q)
{pg.OrderAndPage()};
WITH toplabel AS
(
    SELECT rd.UserKey, {Sites.LabelName} AS Name,
           ROW_NUMBER() OVER (PARTITION BY rd.UserKey ORDER BY ISNULL(sl.Rank, 0) DESC) AS n
    FROM dash.ResourceDay AS rd
    JOIN #pick AS pick ON pick.UserKey = rd.UserKey
    LEFT JOIN dash.SensitivityLabel AS sl ON sl.LabelId = rd.Item
    WHERE rd.Kind = 'Label' AND rd.DateKey BETWEEN @k0 AND @k1
)
SELECT du.DisplayName, du.UserPrincipalName, du.Department, x.N, x.Days, toplabel.Name AS TopLabel,
       x.Sites, x.Blocked, x.TotalRows
FROM #pick AS x JOIN bi.DimUser AS du ON du.UserKey = x.UserKey
LEFT JOIN toplabel ON toplabel.UserKey = x.UserKey AND toplabel.n = 1
ORDER BY {pg.OrderBy};", WithRanks(Ctx.Args(new { q = pg.Like, label })));
        Dapper.DynamicParameters WithRanks(Dapper.DynamicParameters d) { d.AddDynamicParams(rankArgs); return d; }
        pg.Total = rows.FirstOrDefault()?.TotalRows ?? 0;
        var t = new Table
        {
            Headers = ["Name", "Sign-in", "Department", "Labeled interactions", "Days with labeled content", "Most sensitive label", "Sites reached", "Times blocked"],
            Numeric = new HashSet<int> { 3, 4, 6, 7 }, EmptyText = "Nobody read labeled content through Copilot in the period.",
            Server = new(pg, Request, ["name", "upn", "dept", "most", "days", "sens", "sites", "blocked"], new HashSet<int> { 3, 4, 5, 6, 7 }),
        };
        foreach (var r in rows) t.Rows.Add([r.DisplayName, r.UserPrincipalName, r.Department, r.N, r.Days, r.TopLabel, r.Sites, r.Blocked]);
        CsvTable = t;
        Kpis.Add(new("People", Num(pg.Total), label is null ? "read labeled content or were blocked from content" : "read content with this label"));
        var choices = "<a href=\"" + System.Net.WebUtility.HtmlEncode(Ctx.Url(Request.Path)) + "\"" + (label is null ? " class=\"on\"" : "") + ">All labels</a>" +
            string.Concat(labels.OrderBy(x => x.Name).Select(x =>
                $" · <a href=\"{System.Net.WebUtility.HtmlEncode(Ctx.Url(Request.Path, extra: "label=" + Uri.EscapeDataString(x.Id)))}\"{(x.Id == label ? " class=\"on\"" : "")}>{System.Net.WebUtility.HtmlEncode(x.Name)}</a>"));
        Panels.Add(new("People", new HtmlString($"<p class=\"sub\">Label: {choices}</p>" + pg.Controls(Request, SortLabels).Value + t.Render().Value),
            "Labeled interactions: interactions that read content with a label (or the label chosen). Sites reached: the sites of that labeled content. Times blocked: interactions where a policy or permissions stopped Copilot reading content.", "full", CsvUrl()));
    }
}
