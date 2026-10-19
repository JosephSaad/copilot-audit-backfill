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
    public override string Intro => "How often Copilot works with sensitivity-labeled content, which labels, from which sites, and what it was blocked from.";

    protected override async Task BuildAsync()
    {
        var tr = await m.TrendAsync(Ctx);
        long labeled = tr.Sum(w => w.Labeled), all = tr.Sum(w => w.Interactions), blocked = tr.Sum(w => w.Blocked);
        var people = await Db.ScalarAsync<int>($@"
SELECT COUNT(DISTINCT ud.UserKey) FROM dash.UserDay AS ud {Ctx.Scope("ud.UserKey")}
WHERE ud.DateKey BETWEEN @k0 AND @k1 AND ud.LabeledContent > 0;", Ctx.Args());
        Kpis.Add(new("Interactions with labeled content", Num(labeled), $"{Pct(Ratio(labeled, all))} of all interactions"));
        Kpis.Add(new("People", Num(people), "whose Copilot use touched labeled content"));
        Kpis.Add(new("Blocked from content", Num(blocked), "interactions where access was blocked or restricted", Href: Open("/compliance/access")));

        var labels = await Db.QueryAsync<(DateTime Bucket, string Label, int Rank, int N)>($@"
SELECT {Ctx.BucketSql("d")}, {Sites.LabelName}, ISNULL(MAX(sl.Rank), 255), SUM(rd.Interactions)
FROM dash.ResourceDay AS rd {Ctx.Scope("rd.UserKey")}
JOIN bi.DimDate AS d ON d.DateKey = rd.DateKey
LEFT JOIN dash.SensitivityLabel AS sl ON sl.LabelId = rd.Item
WHERE rd.Kind = 'Label' AND rd.DateKey BETWEEN @k0 AND @k1
GROUP BY {Ctx.BucketSql("d")}, {Sites.LabelName};", Ctx.Args());
        var order = labels.GroupBy(l => l.Label).OrderBy(g => g.Max(l => l.Rank)).ThenByDescending(g => g.Sum(l => l.N)).Select(g => g.Key).ToList();
        var buckets = Buckets();
        var shown = order.Take(7).ToList();
        var series = shown.Select((l, i) => new Series(l, buckets.Select(b => (double?)labels.Where(x => x.Label == l && x.Bucket == b.Key).Sum(x => x.N)).ToList(), i + 1)).ToList();
        if (order.Count > 7)
            series.Add(new Series("Other labels", buckets.Select(b => (double?)labels.Where(x => !shown.Contains(x.Label) && x.Bucket == b.Key).Sum(x => x.N)).ToList(), 8));
        Panels.Add(new("Labeled content by label", Chart.Columns(Labels(), series, Fmt.Count, "Interactions by sensitivity label"),
            $"An interaction that read content with two labels counts under each. Labels are ordered least to most sensitive when Admin › Reference data ranks them. {TrendNote}"));

        Panels.Add(new("Share of interactions touching labeled content",
            Chart.Line(Labels(), [new Series("Labeled", tr.Select(w => Ratio(w.Labeled, w.Interactions)).ToList(), 1)], Fmt.Percent, "Labeled share"), null, "half"));
        Panels.Add(new("By label, whole period",
            Chart.Bars(order.Select(l => new Bar(l, labels.Where(x => x.Label == l).Sum(x => x.N))).ToList(), Fmt.Count, "Interactions by label"), null, "half"));

        var named = Viewer.Has(Roles.Compliance) && !PrivacyMode;
        var sites = await Db.QueryAsync<(string Kind, string Item, int N, int People)>($@"
SELECT rd.Kind, rd.Item, SUM(rd.Interactions), COUNT(DISTINCT rd.UserKey)
FROM dash.ResourceDay AS rd {Ctx.Scope("rd.UserKey")}
WHERE rd.Kind IN ('Site', 'Blocked') AND rd.DateKey BETWEEN @k0 AND @k1
GROUP BY rd.Kind, rd.Item;", Ctx.Args());
        HtmlString SiteBars(string kind, int slot) => Chart.Bars(sites.Where(x => x.Kind == kind)
            .GroupBy(x => Sites.Show(x.Item, named)).Select(g => (Site: g.Key, N: g.Sum(x => x.N), People: g.Sum(x => x.People)))
            .OrderByDescending(x => x.N).Take(12).Select(x => new Bar(x.Site, x.N, Note: $"{x.People:#,0} people", Slot: slot)).ToList(), Fmt.Count, kind + " sites");
        Panels.Add(new("Where labeled content lives", SiteBars("Site", 3),
            "Sites of the labeled content Copilot read." + (named ? "" : PrivacyMode ? " Personal OneDrive sites are grouped: privacy mode is on." : " Personal OneDrive sites are grouped; the Compliance role shows them."), "half"));
        Panels.Add(new("Sites Copilot was blocked from", SiteBars("Blocked", 7),
            "Content whose policy or permissions stopped Copilot. Usually a sign the controls are working; a spike is worth a look.", "half"));

        /* Counted per type key, then named (2.4.2). */
        var types = await Db.QueryAsync<(string Type, long N, long Labeled, long Blocked)>($@"
WITH g AS (SELECT f.ResourceTypeKey, COUNT_BIG(*) AS N, SUM(CASE WHEN f.SensitivityLabelKey <> 1 THEN 1 ELSE 0 END) AS L, SUM(CAST(f.IsBlocked AS INT)) AS B
           FROM bi.FactAccessedResource AS f {Ctx.Scope("f.UserKey")}
           WHERE f.DateKey BETWEEN @k0 AND @k1
           GROUP BY f.ResourceTypeKey)
SELECT COALESCE(vn.Name, t.ResourceType), SUM(g.N), SUM(g.L), SUM(g.B)
FROM g JOIN bi.DimResourceType AS t ON t.ResourceTypeKey = g.ResourceTypeKey
LEFT JOIN dash.ValueName AS vn ON vn.ColumnId = 31 AND vn.Value = t.ResourceType
GROUP BY t.ResourceType, vn.Name;", Ctx.Args());
        Panels.Add(new("What Copilot read, by type",
            Chart.Bars(types.OrderByDescending(t => t.N).Take(12).Select(t => new Bar(t.Type, t.N, Note: $"{t.Labeled:#,0} labeled, {t.Blocked:#,0} blocked")).ToList(), Fmt.Count, "Accessed resources by type"),
            "Every resource an interaction read: files, mail, Teams messages, web results. Hover for how many were labeled or blocked."));
    }
}

public sealed class AccessModel(PageServices s) : InsightsPage(s)
{
    public override string Title => "Access to labeled content";
    public override string Persona => "Compliance";
    public override string? RequiredRole => Roles.Compliance;
    public override string Intro => "Who worked with labeled content through Copilot in the period, how often, and the most sensitive label they touched.";

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
        var labels = await Db.QueryAsync<(string Id, string Name)>($@"
SELECT DISTINCT rd.Item, {Sites.LabelName} FROM dash.ResourceDay AS rd LEFT JOIN dash.SensitivityLabel AS sl ON sl.LabelId = rd.Item
WHERE rd.Kind = 'Label' AND rd.DateKey BETWEEN @k0 AND @k1;", Ctx.Args());
        var label = Request.Query["label"].FirstOrDefault() is { } l && labels.Any(x => x.Id == l) ? l : null;
        var pg = new Paging(Request, Sorts, "most");
        var rows = await Db.QueryAsync<Row>($@"
/* Each measure into a temporary table of its own, then the page, then the most sensitive
   label only for the rows shown. With the two distinct counts in one statement the optimizer
   asked for gigabytes of memory and still spilled; with the label looked up person by person
   (dash.ResourceDay is keyed by day, not person) it read the whole period per row. Either way
   30 to 45 seconds for 180 days at 70,000 people; this way a few seconds. */
SELECT rd.UserKey,
       SUM(CASE WHEN @label IS NULL OR rd.Item = @label THEN rd.Interactions ELSE 0 END) AS N,
       COUNT(DISTINCT rd.DateKey) AS Days, MAX(ISNULL(sl.Rank, 0)) AS TopRank
INTO #lab
FROM dash.ResourceDay AS rd {Ctx.Scope("rd.UserKey")}
LEFT JOIN dash.SensitivityLabel AS sl ON sl.LabelId = rd.Item
WHERE rd.Kind = 'Label' AND rd.DateKey BETWEEN @k0 AND @k1
GROUP BY rd.UserKey;
SELECT rd.UserKey, COUNT(DISTINCT rd.Item) AS Sites
INTO #site
FROM dash.ResourceDay AS rd {Ctx.Scope("rd.UserKey")}
WHERE rd.Kind = 'Site' AND rd.DateKey BETWEEN @k0 AND @k1
GROUP BY rd.UserKey;
SELECT rd.UserKey, SUM(rd.Interactions) AS Blocked
INTO #blk
FROM dash.ResourceDay AS rd {Ctx.Scope("rd.UserKey")}
WHERE rd.Kind = 'Blocked' AND rd.DateKey BETWEEN @k0 AND @k1
GROUP BY rd.UserKey;
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
ORDER BY {pg.OrderBy};", Ctx.Args(new { q = pg.Like, label }));
        pg.Total = rows.FirstOrDefault()?.TotalRows ?? 0;
        var t = new Table
        {
            Headers = ["Name", "Sign-in", "Department", "Labeled interactions", "Days with one", "Most sensitive label", "Sites reached", "Times blocked"],
            Numeric = new HashSet<int> { 3, 4, 6, 7 }, EmptyText = "Nobody touched labeled content in the period.",
            Server = new(pg, Request, ["name", "upn", "dept", "most", "days", "sens", "sites", "blocked"], new HashSet<int> { 3, 4, 5, 6, 7 }),
        };
        foreach (var r in rows) t.Rows.Add([r.DisplayName, r.UserPrincipalName, r.Department, r.N, r.Days, r.TopLabel, r.Sites, r.Blocked]);
        CsvTable = t;
        Kpis.Add(new("People", Num(pg.Total), label is null ? "touched labeled content or were blocked" : "touched this label"));
        var choices = "<a href=\"" + System.Net.WebUtility.HtmlEncode(Ctx.Url(Request.Path)) + "\"" + (label is null ? " class=\"on\"" : "") + ">All labels</a>" +
            string.Concat(labels.OrderBy(x => x.Name).Select(x =>
                $" · <a href=\"{System.Net.WebUtility.HtmlEncode(Ctx.Url(Request.Path, extra: "label=" + Uri.EscapeDataString(x.Id)))}\"{(x.Id == label ? " class=\"on\"" : "")}>{System.Net.WebUtility.HtmlEncode(x.Name)}</a>"));
        Panels.Add(new("People", new HtmlString($"<p class=\"sub\">Label: {choices}</p>" + pg.Controls(Request, SortLabels).Value + t.Render().Value), null, "full", CsvUrl()));
    }
}
