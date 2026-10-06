using CopilotInsights.Infrastructure;
using Microsoft.AspNetCore.Html;

namespace CopilotInsights.Pages.Agents;

/// <summary>
/// The agent inventory workbook (2.5.6): tools\Import-AgentWorkbook.ps1 loads it each night into dash.AgentInventory
/// (the current agents, matched to the audit log's), dash.AgentChildLink and, as they come, the dbo tables of its sheets.
/// </summary>
static class Workbook
{
    /// <summary>1 when the inventory holds agents. A database without the table (before 2.5.6) answers 0.</summary>
    public const string HasSql = "IF OBJECT_ID(N'dash.AgentInventory', N'U') IS NULL SELECT 0 ELSE EXEC (N'SELECT CASE WHEN EXISTS (SELECT 1 FROM dash.AgentInventory) THEN 1 ELSE 0 END');";

    public const string Missing = "No agent workbook is loaded. tools\\Import-AgentWorkbook.ps1 loads it each night once the settings name it (AgentWorkbookSiteUrl or AgentWorkbookUrl).";

    public static async Task<bool> HasAsync(Db db) => await db.ScalarAsync<int>(HasSql) == 1;

    /// <summary>An agent's detail page: by its key where the audit log knows it, else by its workbook id.</summary>
    public static string DetailUrl(ReportContext ctx, int? agentKey, string inventoryAgentId) =>
        ctx.Url("/agents/detail", extra: agentKey is { } k ? $"agent={k}" : $"inv={Uri.EscapeDataString(inventoryAgentId)}");
}

/// <summary>
/// A list of the workbook's agents with their use in the period from the audit log (2.5.6): the inventory, shared
/// agents and agents in production. The workbook is the list; an agent no one used in the period is flagged.
/// </summary>
public abstract class AgentListPage(PageServices s) : InsightsPage(s)
{
    public override string Persona => "Agents";

    /// <summary>Which of the workbook's agents the page lists, over dash.AgentInventory AS i.</summary>
    protected abstract string Where { get; }
    protected virtual bool SharingColumns => false;
    protected virtual bool ChannelColumn => false;
    protected virtual string DefaultSort => "most";

    static readonly Dictionary<string, string> Sorts = new()
    {
        ["name"] = "AgentName, InventoryAgentId", ["owner"] = "Owner, AgentName, InventoryAgentId", ["env"] = "Environment, AgentName, InventoryAgentId",
        ["created"] = "CreatedAt DESC, AgentName, InventoryAgentId", ["published"] = "LastPublishedAt DESC, AgentName, InventoryAgentId",
        ["last"] = "LastUsed DESC, AgentName, InventoryAgentId", ["people"] = "People DESC, AgentName, InventoryAgentId",
        ["most"] = "N DESC, People DESC, AgentName, InventoryAgentId", ["viewers"] = "Viewers DESC, AgentName, InventoryAgentId",
    };
    static readonly Dictionary<string, string> SortLabels = new()
    {
        ["most"] = "Most interactions", ["name"] = "Name", ["owner"] = "Owner", ["last"] = "Last used", ["published"] = "Last published",
    };

    sealed class Totals
    {
        public int Agents { get; set; }
        public int Used { get; set; }
        public int Shared { get; set; }
        public int Production { get; set; }
        public int Matched { get; set; }
    }

    sealed class Row
    {
        public string InventoryAgentId { get; set; } = "";
        public int? AgentKey { get; set; }
        public string AgentName { get; set; } = "";
        public string? Owner { get; set; }
        public string? Environment { get; set; }
        public string? Channels { get; set; }
        public bool IsShared { get; set; }
        public bool InProduction { get; set; }
        public DateTime? CreatedAt { get; set; }
        public DateTime? LastPublishedAt { get; set; }
        public int ViewerUsers { get; set; }
        public int ViewerGroups { get; set; }
        public int EditorUsers { get; set; }
        public int EditorGroups { get; set; }
        public DateTime? LastUsed { get; set; }
        public long N { get; set; }
        public int People { get; set; }
        public int TotalRows { get; set; }
    }

    /// <summary>The page's KPIs, its "used or not" filter and its paged list. False when no workbook is loaded.</summary>
    protected async Task<bool> BuildListAsync(string listTitle, string listNote)
    {
        if (!await Workbook.HasAsync(Db)) return false;
        var pg = new Paging(Request, Sorts, DefaultSort);
        var unused = Request.Query["only"].FirstOrDefault() == "unused";
        var (totals, rows) = await Db.QueryTwoAsync<Totals, Row>($@"
SELECT f.AgentKey, COUNT_BIG(*) AS N, COUNT(DISTINCT f.UserKey) AS People
INTO #u
FROM bi.FactInteraction AS f {Ctx.Scope("f.UserKey")}
WHERE f.DateKey BETWEEN @k0 AND @k1
GROUP BY f.AgentKey;
SELECT i.InventoryAgentId, i.AgentKey,
       COALESCE(NULLIF(i.AgentName, N''), al.FriendlyName, d.AgentName, d.AgentId, i.InventoryAgentId) AS AgentName,
       COALESCE(i.OwnerUpn, al.Owner) AS Owner, i.Environment, i.Channels, i.IsShared, i.InProduction, i.CreatedAt, i.LastPublishedAt,
       ISNULL(i.ViewerUsers, 0) AS ViewerUsers, ISNULL(i.ViewerGroups, 0) AS ViewerGroups,
       ISNULL(i.EditorUsers, 0) AS EditorUsers, ISNULL(i.EditorGroups, 0) AS EditorGroups,
       ISNULL(i.ViewerUsers, 0) + ISNULL(i.ViewerGroups, 0) AS Viewers,
       CAST(ld.[Date] AS DATETIME2(0)) AS LastUsed, ISNULL(u.N, 0) AS N, ISNULL(u.People, 0) AS People
INTO #x
FROM dash.AgentInventory AS i
LEFT JOIN bi.DimAgent AS d ON d.AgentKey = i.AgentKey
LEFT JOIN dash.AgentAlias AS al ON al.AgentKey = i.AgentKey
LEFT JOIN dash.AgentUse AS au ON au.AgentKey = i.AgentKey
LEFT JOIN bi.DimDate AS ld ON ld.DateKey = au.LastDateKey
LEFT JOIN #u AS u ON u.AgentKey = i.AgentKey
WHERE {Where};
SELECT COUNT(*) AS Agents, COUNT(CASE WHEN N > 0 THEN 1 END) AS Used, ISNULL(SUM(CAST(IsShared AS INT)), 0) AS Shared,
       ISNULL(SUM(CAST(InProduction AS INT)), 0) AS Production, COUNT(AgentKey) AS Matched
FROM #x;
SELECT *, COUNT(*) OVER () AS TotalRows
FROM #x
WHERE (@q IS NULL OR AgentName LIKE @q OR Owner LIKE @q OR Environment LIKE @q) AND (@unused = 0 OR N = 0)
{pg.OrderAndPage()};", Ctx.Args(new { q = pg.Like, unused = unused ? 1 : 0 }));
        var t0 = totals.Single();
        pg.Total = rows.FirstOrDefault()?.TotalRows ?? 0;

        Kpis.Add(new("Agents", Num(t0.Agents), $"{t0.Matched:#,0} of them seen in the audit log"));
        Kpis.Add(new("Used in the period", Num(t0.Used), Pct(Ratio(t0.Used, t0.Agents)) + " of them"));
        Kpis.Add(new("Not used in the period", Num(t0.Agents - t0.Used), "no interaction in the audit log", t0.Agents > t0.Used ? "warn" : null));
        Kpis.Add(new("Shared", Num(t0.Shared), "with viewers: users or groups"));
        Kpis.Add(new("In production", Num(t0.Production), "on the workbook's AgentsInProd sheet"));

        var headers = new List<string> { "Agent", "Owner", "Environment" };
        var keys = new List<string?> { "name", "owner", "env" };
        if (SharingColumns) { headers.AddRange(["Viewer users", "Viewer groups", "Editor users", "Editor groups"]); keys.AddRange(["viewers", null, null, null]); }
        headers.AddRange(["Shared", "In production"]); keys.AddRange([null, null]);
        if (ChannelColumn) { headers.Add("Channels"); keys.Add(null); }
        headers.AddRange(["Created", "Last published", "Last used", "People in the period", "Interactions in the period", "Use in the period"]);
        keys.AddRange(["created", "published", "last", "people", "most", null]);
        var numeric = new HashSet<int>();
        for (var i = 0; i < headers.Count; i++)
            if (headers[i] is "Viewer users" or "Viewer groups" or "Editor users" or "Editor groups" or "People in the period" or "Interactions in the period") numeric.Add(i);
        var t = new Table
        {
            Headers = headers, Numeric = numeric, EmptyText = unused ? "Every agent here was used in the period." : "No agents here.",
            Server = new(pg, Request, keys, new HashSet<int> { headers.IndexOf("Created"), headers.IndexOf("Last published"), headers.IndexOf("Last used"),
                                                             headers.IndexOf("People in the period"), headers.IndexOf("Interactions in the period") }),
        };
        foreach (var r in rows)
        {
            var cells = new List<object?> { new Link(r.AgentName, Workbook.DetailUrl(Ctx, r.AgentKey, r.InventoryAgentId)), PrivacyMode ? null : r.Owner, r.Environment };
            if (SharingColumns) cells.AddRange([r.ViewerUsers, r.ViewerGroups, r.EditorUsers, r.EditorGroups]);
            cells.AddRange([r.IsShared ? "Yes" : "No", r.InProduction ? "Yes" : "No"]);
            if (ChannelColumn) cells.Add(r.Channels);
            cells.AddRange([r.CreatedAt?.Date, r.LastPublishedAt?.Date, r.LastUsed?.Date,
                            r.People == 0 ? 0 : AtLeastMin(r.People), r.N, r.N > 0 ? "Used" : "Not used"]);
            t.Rows.Add([.. cells]);
        }
        CsvTable = t;
        var tabs = $"<p class=\"sub\"><a href=\"{E(Ctx.Url(Request.Path))}\"{(unused ? "" : " class=\"on\"")}>All</a> · " +
                   $"<a href=\"{E(Ctx.Url(Request.Path, extra: "only=unused"))}\"{(unused ? " class=\"on\"" : "")}>Not used in the period</a></p>";
        Panels.Add(new(listTitle, new HtmlString(tabs + pg.Controls(Request, SortLabels).Value + t.Render().Value),
            listNote + $" Names and owners come from the agent workbook; where it leaves one out, from Admin › Reference data, then the audit log. People are left out where fewer than {Ctx.MinGroupSize} used an agent." +
            (PrivacyMode ? " Privacy mode is on, so owners are not shown." : ""), "full", CsvUrl()));
        return true;
    }
}

public sealed class SharedModel(PageServices s) : AgentListPage(s)
{
    public override string Title => "Shared agents";
    public override string Intro => "Agents from the agent workbook that are shared: with at least one viewer, a user or a group. Their use in the period comes from the audit log, and an agent no one used is flagged.";
    protected override string Where => "i.IsShared = 1";
    protected override bool SharingColumns => true;
    protected override string DefaultSort => "viewers";

    protected override async Task BuildAsync()
    {
        if (!await BuildListAsync("Shared agents", "Shared: the workbook's ViewerUsers or ViewerGroups is not 0."))
            Notice = Workbook.Missing;
    }
}

public sealed class ProductionModel(PageServices s) : AgentListPage(s)
{
    public override string Title => "Agents in production";
    public override string Intro => "The agents the workbook lists on its AgentsInProd sheet, with their use in the period from the audit log. An agent in production no one used is flagged.";
    protected override string Where => "i.InProduction = 1";
    protected override bool ChannelColumn => true;

    protected override async Task BuildAsync()
    {
        if (!await BuildListAsync("Agents in production", "In production: listed on the workbook's AgentsInProd sheet."))
            Notice = Workbook.Missing;
    }
}

/// <summary>Connected agents (2.5.6): each child agent, the parent agents that call it, and the parents' use.</summary>
public sealed class ChildModel(PageServices s) : InsightsPage(s)
{
    public override string Title => "Child agent usage";
    public override string Persona => "Agents";
    public override string Intro => "Agents that other agents call (connected agents, from the workbook's ParentChildAgent sheet): how many parent agents call each one and which, each parent's use in the period from the audit log, and each child's total across its parents.";

    sealed class Row
    {
        public string ChildAgentName { get; set; } = "";
        public string ParentName { get; set; } = "";
        public string? ParentInventoryAgentId { get; set; }
        public int? ParentAgentKey { get; set; }
        public string? RelationshipType { get; set; }
        public bool? IsValid { get; set; }
        public long N { get; set; }
        public int People { get; set; }
    }

    protected override async Task BuildAsync()
    {
        if (await Db.ScalarAsync<int>("IF OBJECT_ID(N'dash.AgentChildLink', N'U') IS NULL SELECT 0 ELSE EXEC (N'SELECT CASE WHEN EXISTS (SELECT 1 FROM dash.AgentChildLink) THEN 1 ELSE 0 END');") != 1)
        {
            Notice = "No parent and child agents are loaded. tools\\Import-AgentWorkbook.ps1 loads them each night from the agent workbook's ParentChildAgent sheet.";
            return;
        }
        var (rows, people) = await Db.QueryTwoAsync<Row, (string Child, int People)>($@"
SELECT f.AgentKey, COUNT_BIG(*) AS N, COUNT(DISTINCT f.UserKey) AS People
INTO #u
FROM bi.FactInteraction AS f {Ctx.Scope("f.UserKey")}
WHERE f.DateKey BETWEEN @k0 AND @k1 AND f.AgentKey IN (SELECT ParentAgentKey FROM dash.AgentChildLink WHERE ParentAgentKey IS NOT NULL)
GROUP BY f.AgentKey;
/* One row for each child and parent: a parent can call its child from several topics. */
SELECT l.ChildAgentName, COALESCE(MAX(NULLIF(l.ParentAgentName, N'')), MAX(d.AgentName), l.ParentInventoryAgentId, N'(unknown parent)') AS ParentName,
       l.ParentInventoryAgentId, MAX(l.ParentAgentKey) AS ParentAgentKey, MAX(l.RelationshipType) AS RelationshipType,
       CAST(MAX(CAST(l.IsValid AS INT)) AS BIT) AS IsValid, ISNULL(MAX(u.N), 0) AS N, ISNULL(MAX(u.People), 0) AS People
FROM dash.AgentChildLink AS l
LEFT JOIN bi.DimAgent AS d ON d.AgentKey = l.ParentAgentKey
LEFT JOIN #u AS u ON u.AgentKey = l.ParentAgentKey
GROUP BY l.ChildAgentName, l.ParentInventoryAgentId;
/* The people behind each child, counted once though they used several of its parents. */
SELECT c.ChildAgentName AS Child, COUNT(DISTINCT f.UserKey) AS People
FROM bi.FactInteraction AS f {Ctx.Scope("f.UserKey")}
JOIN (SELECT DISTINCT ChildAgentName, ParentAgentKey FROM dash.AgentChildLink WHERE ParentAgentKey IS NOT NULL) AS c ON c.ParentAgentKey = f.AgentKey
WHERE f.DateKey BETWEEN @k0 AND @k1
GROUP BY c.ChildAgentName;", Ctx.Args());

        var children = rows.GroupBy(r => r.ChildAgentName, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Child: g.Key, Parents: g.ToList(), N: g.Sum(r => r.N),
                          People: people.Where(p => string.Equals(p.Child, g.Key, StringComparison.OrdinalIgnoreCase)).Sum(p => p.People)))
            .OrderByDescending(c => c.N).ThenBy(c => c.Child).ToList();
        var chosen = Request.Query["child"].FirstOrDefault() is { Length: > 0 } ch ? children.FirstOrDefault(c => string.Equals(c.Child, ch, StringComparison.OrdinalIgnoreCase)).Child : null;

        Kpis.Add(new("Child agents", Num(children.Count), "called by at least one parent agent"));
        Kpis.Add(new("Parent agents", Num(rows.Select(r => r.ParentInventoryAgentId ?? r.ParentName).Distinct(StringComparer.OrdinalIgnoreCase).Count())));
        Kpis.Add(new("Parents used in the period", Num(rows.Where(r => r.N > 0).Select(r => r.ParentInventoryAgentId ?? r.ParentName).Distinct(StringComparer.OrdinalIgnoreCase).Count())));
        Kpis.Add(new("Interactions through parents", Num(rows.Sum(r => r.N)), "each parent counted once for each child it calls"));

        var options = string.Concat(children.Select(c =>
            $"<option value=\"{E(Ctx.Url(Request.Path, extra: $"child={Uri.EscapeDataString(c.Child)}"))}\"{(c.Child == chosen ? " selected" : "")}>{E(c.Child)} ({c.Parents.Count})</option>"));
        Panels.Add(new("Choose a child agent", new HtmlString(
            $"<select class=\"pick\" aria-label=\"Child agent\" data-navigate><option value=\"{E(Ctx.Url(Request.Path))}\">Every child agent</option>{options}</select>")));

        var ct = new Table { Headers = ["Child agent", "Parent agents", "Which", "People through its parents", "Interactions through its parents"], Numeric = new HashSet<int> { 1, 3, 4 } };
        foreach (var c in children.Where(c => chosen is null || c.Child == chosen))
            ct.Rows.Add([new Link(c.Child, Ctx.Url(Request.Path, extra: $"child={Uri.EscapeDataString(c.Child)}")), c.Parents.Count,
                         string.Join("; ", c.Parents.Select(p => p.ParentName).OrderBy(n => n)), c.People == 0 ? 0 : AtLeastMin(c.People), c.N]);
        Panels.Add(new("Each child agent", ct.Render(),
            $"The totals are the parent agents' use in the period, from the audit log: a child's interactions add up its parents', and its people count each person once. People are left out where fewer than {Ctx.MinGroupSize}."));

        var pt = new Table
        {
            Headers = ["Child agent", "Parent agent", "Relationship", "Valid", "People", "Interactions", "Use in the period"],
            Numeric = new HashSet<int> { 4, 5 }, EmptyText = "No parent agents.",
        };
        foreach (var r in rows.Where(r => chosen is null || string.Equals(r.ChildAgentName, chosen, StringComparison.OrdinalIgnoreCase))
                              .OrderBy(r => r.ChildAgentName).ThenByDescending(r => r.N).ThenBy(r => r.ParentName))
            pt.Rows.Add([r.ChildAgentName,
                         r.ParentInventoryAgentId is null && r.ParentAgentKey is null ? r.ParentName : new Link(r.ParentName, Workbook.DetailUrl(Ctx, r.ParentAgentKey, r.ParentInventoryAgentId ?? "")),
                         r.RelationshipType, r.IsValid switch { true => "Yes", false => "No", _ => null },
                         r.People == 0 ? 0 : AtLeastMin(r.People), r.N, r.N > 0 ? "Used" : "Not used"]);
        CsvTable = pt;
        Panels.Add(new(chosen is null ? "Each parent agent" : $"Parent agents calling {chosen}", pt.Render(),
            "A parent's use is its own, from the audit log: the audit log does not say which of its interactions reached the child.", "full", CsvUrl()));
    }
}
