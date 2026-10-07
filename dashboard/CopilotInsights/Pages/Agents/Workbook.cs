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
/// agents and agents in production. The workbook is the list; an agent no one used in the period is flagged, unless it
/// runs on its own (triggers or flows: an autonomous agent, 2.5.8), which no interaction does not make unused.
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
        public int Autonomous { get; set; }
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
        public bool IsAutonomous { get; set; }
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

    /// <summary>What the list says of an agent's use in the period (2.5.8: an autonomous agent with none is not "unused").</summary>
    public static string Use(long n, bool autonomous) => n > 0 ? "Used" : autonomous ? "Autonomous (no chat)" : "Not used";

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
       COALESCE(i.OwnerUpn, al.Owner) AS Owner, i.Environment, i.Channels, i.IsShared, i.InProduction, i.IsAutonomous, i.CreatedAt, i.LastPublishedAt,
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
SELECT COUNT(*) AS Agents, COUNT(CASE WHEN N > 0 THEN 1 END) AS Used, COUNT(CASE WHEN N = 0 AND IsAutonomous = 1 THEN 1 END) AS Autonomous,
       ISNULL(SUM(CAST(IsShared AS INT)), 0) AS Shared, ISNULL(SUM(CAST(InProduction AS INT)), 0) AS Production, COUNT(AgentKey) AS Matched
FROM #x;
SELECT *, COUNT(*) OVER () AS TotalRows
FROM #x
WHERE (@q IS NULL OR AgentName LIKE @q OR Owner LIKE @q OR Environment LIKE @q) AND (@unused = 0 OR (N = 0 AND IsAutonomous = 0))
{pg.OrderAndPage()};", Ctx.Args(new { q = pg.Like, unused = unused ? 1 : 0 }));
        var t0 = totals.Single();
        pg.Total = rows.FirstOrDefault()?.TotalRows ?? 0;
        var notUsed = t0.Agents - t0.Used - t0.Autonomous;

        Kpis.Add(new("Agents in the workbook", Num(t0.Agents), $"{t0.Matched:#,0} of them seen in the audit log"));
        Kpis.Add(new("Used in the period", Num(t0.Used), Pct(Ratio(t0.Used, t0.Agents)) + " of them"));
        Kpis.Add(new("Not used in the period", Num(notUsed), "no interaction in the audit log, and not autonomous", notUsed > 0 ? "warn" : null));
        Kpis.Add(new("Autonomous", Num(t0.Autonomous), "triggers or flows, with no interaction: they can work with nobody chatting to them"));
        Kpis.Add(new("Shared with others", Num(t0.Shared), "with viewers: users or groups"));
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
            Headers = headers, Numeric = numeric, EmptyText = unused ? "Every agent here was used in the period, or runs on its own." : "No agents here.",
            Server = new(pg, Request, keys, new HashSet<int> { headers.IndexOf("Created"), headers.IndexOf("Last published"), headers.IndexOf("Last used"),
                                                             headers.IndexOf("People in the period"), headers.IndexOf("Interactions in the period") }),
        };
        var ownerName = await NamesAsync(rows.Select(r => r.Owner));   // 2.5.8: owners by name
        foreach (var r in rows)
        {
            var cells = new List<object?> { new Link(r.AgentName, Workbook.DetailUrl(Ctx, r.AgentKey, r.InventoryAgentId)), PrivacyMode ? null : ownerName(r.Owner), r.Environment };
            if (SharingColumns) cells.AddRange([r.ViewerUsers, r.ViewerGroups, r.EditorUsers, r.EditorGroups]);
            cells.AddRange([r.IsShared ? "Yes" : "No", r.InProduction ? "Yes" : "No"]);
            if (ChannelColumn) cells.Add(r.Channels);
            cells.AddRange([r.CreatedAt?.Date, r.LastPublishedAt?.Date, r.LastUsed?.Date,
                            r.People == 0 ? 0 : AtLeastMin(r.People), r.N, Use(r.N, r.IsAutonomous)]);
            t.Rows.Add([.. cells]);
        }
        CsvTable = t;
        var tabs = $"<p class=\"sub\"><a href=\"{E(Ctx.Url(Request.Path))}\"{(unused ? "" : " class=\"on\"")}>All</a> · " +
                   $"<a href=\"{E(Ctx.Url(Request.Path, extra: "only=unused"))}\"{(unused ? " class=\"on\"" : "")}>Not used in the period</a></p>";
        Panels.Add(new(listTitle, new HtmlString(tabs + pg.Controls(Request, SortLabels).Value + t.Render().Value),
            listNote + " Names and owners come from the agent workbook; where it leaves one out, from Admin › Reference data, then the audit log. Owners are shown by their name in the directory, or by sign-in where it does not have them. " +
            $"An autonomous agent (one with triggers or flows) can work with nobody chatting to it, so with no interaction it is *Autonomous*, not *Not used*. People are left out where fewer than {Ctx.MinGroupSize} used an agent." +
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

/// <summary>
/// Agents whose owner has gone (2.5.8): the workbook's owner looked up in the directory loaded from AD. An owner who
/// is not in it has most likely left; one whose account is disabled has, or is about to. Their agents still run, and
/// someone has to own them. Also each owner's department, for accountability by part of the business.
/// </summary>
public sealed class OwnersModel(PageServices s) : InsightsPage(s)
{
    public override string Title => "Agents without an owner";
    public override string Persona => "Agents";
    public override string Intro => "Agents from the agent workbook whose owner has left the directory, has a disabled account, or is not set, with their use in the period: the agents that need a new owner first. Below, every agent by its owner's department.";

    sealed class Row
    {
        public string InventoryAgentId { get; set; } = "";
        public int? AgentKey { get; set; }
        public string AgentName { get; set; } = "";
        public string? Owner { get; set; }
        public string OwnerState { get; set; } = "";
        public string? OwnerDepartment { get; set; }
        public string? OwnerManager { get; set; }
        public string? Environment { get; set; }
        public bool IsAutonomous { get; set; }
        public DateTime? LastUsed { get; set; }
        public long N { get; set; }
        public int People { get; set; }
    }

    sealed class Dept
    {
        public string Department { get; set; } = "";
        public int Agents { get; set; }
        public int Used { get; set; }
        public long N { get; set; }
        public int Ownerless { get; set; }
    }

    protected override async Task BuildAsync()
    {
        if (!await Workbook.HasAsync(Db)) { Notice = Workbook.Missing; return; }
        if (await Db.ScalarAsync<int>("SELECT CASE WHEN OBJECT_ID(N'audit.UserDirectory', N'U') IS NULL THEN 0 ELSE 1 END;") != 1)
        {
            Notice = "The directory has not been loaded from Active Directory (tools\\Export-UsersWithHierarchy-ToSql.ps1), so owners cannot be checked.";
            return;
        }
        var (rows, depts) = await Db.QueryTwoAsync<Row, Dept>($@"
SELECT f.AgentKey, COUNT_BIG(*) AS N, COUNT(DISTINCT f.UserKey) AS People
INTO #u
FROM bi.FactInteraction AS f {Ctx.Scope("f.UserKey")}
WHERE f.DateKey BETWEEN @k0 AND @k1
GROUP BY f.AgentKey;
SELECT i.InventoryAgentId, i.AgentKey, COALESCE(NULLIF(i.AgentName, N''), al.FriendlyName, d.AgentName, i.InventoryAgentId) AS AgentName,
       o.Owner, CASE WHEN o.Owner IS NULL THEN N'No owner set' WHEN ud.UserPrincipalName IS NULL THEN N'Not in the directory (left?)'
                     WHEN ud.AccountEnabled = 0 THEN N'Account disabled' ELSE N'Active' END AS OwnerState,
       ud.Department AS OwnerDepartment, ud.ManagerName AS OwnerManager, i.Environment, i.IsAutonomous,
       CAST(ld.[Date] AS DATETIME2(0)) AS LastUsed, ISNULL(u.N, 0) AS N, ISNULL(u.People, 0) AS People
INTO #x
FROM dash.AgentInventory AS i
LEFT JOIN bi.DimAgent AS d ON d.AgentKey = i.AgentKey
LEFT JOIN dash.AgentAlias AS al ON al.AgentKey = i.AgentKey
CROSS APPLY (SELECT NULLIF(LTRIM(RTRIM(COALESCE(i.OwnerUpn, al.Owner))), N'') AS Owner) AS o
LEFT JOIN audit.UserDirectory AS ud ON ud.UserPrincipalName = o.Owner
LEFT JOIN dash.AgentUse AS au ON au.AgentKey = i.AgentKey
LEFT JOIN bi.DimDate AS ld ON ld.DateKey = au.LastDateKey
LEFT JOIN #u AS u ON u.AgentKey = i.AgentKey;
SELECT * FROM #x WHERE OwnerState <> N'Active' ORDER BY N DESC, People DESC, AgentName;
SELECT ISNULL(NULLIF(OwnerDepartment, N''), N'(owner not in the directory)') AS Department, COUNT(*) AS Agents,
       COUNT(CASE WHEN N > 0 THEN 1 END) AS Used, SUM(N) AS N, COUNT(CASE WHEN OwnerState <> N'Active' THEN 1 END) AS Ownerless
FROM #x GROUP BY ISNULL(NULLIF(OwnerDepartment, N''), N'(owner not in the directory)') ORDER BY N DESC, Agents DESC;", Ctx.Args());

        var used = rows.Count(r => r.N > 0);
        Kpis.Add(new("Without an active owner", Num(rows.Count), "the owner left the directory, is disabled, or is not set", rows.Count > 0 ? "warn" : null));
        Kpis.Add(new("Of them used in the period", Num(used), "these need a new owner first", used > 0 ? "warn" : null));
        Kpis.Add(new("Left the directory", Num(rows.Count(r => r.OwnerState.StartsWith("Not in", StringComparison.Ordinal)))));
        Kpis.Add(new("Account disabled", Num(rows.Count(r => r.OwnerState == "Account disabled"))));
        Kpis.Add(new("No owner set", Num(rows.Count(r => r.OwnerState == "No owner set"))));

        var ownerName = await NamesAsync(rows.Select(r => r.Owner));
        var t = new Table
        {
            Headers = ["Agent", "Owner", "Owner's status", "Owner's manager", "Owner's department", "Environment", "Last used", "People in the period", "Interactions in the period", "Use in the period"],
            Numeric = new HashSet<int> { 7, 8 }, EmptyText = "Every agent has an active owner.",
        };
        foreach (var r in rows)
            t.Rows.Add([new Link(r.AgentName, Workbook.DetailUrl(Ctx, r.AgentKey, r.InventoryAgentId)), PrivacyMode ? null : ownerName(r.Owner), r.OwnerState,
                        PrivacyMode ? null : r.OwnerManager, r.OwnerDepartment, r.Environment, r.LastUsed?.Date,
                        r.People == 0 ? 0 : AtLeastMin(r.People), r.N, AgentListPage.Use(r.N, r.IsAutonomous)]);
        CsvTable = t;
        Panels.Add(new("Agents that need a new owner", t.Render(),
            "Most used first. The owner is the workbook's OwnerIdUPN, looked up in the directory loaded from Active Directory each night: not found most likely means they left. The owner's manager is who to ask for a new one." +
            (PrivacyMode ? " Privacy mode is on, so people are not named." : ""), "full", CsvUrl()));

        var dt = new Table { Headers = ["Owner's department", "Agents", "Used in the period", "Interactions in the period", "Without an active owner"], Numeric = new HashSet<int> { 1, 2, 3, 4 } };
        foreach (var d in depts) dt.Rows.Add([d.Department, d.Agents, d.Used, d.N, d.Ownerless]);
        Panels.Add(new("Agents by their owner's department", dt.Render(),
            "Every agent of the workbook, by the department of its owner in the directory: who is accountable for which agents, and their use."));
    }
}

/// <summary>
/// The agent workbook's settings against the agents' use (2.5.8): what the workbook says an agent is, set beside what the
/// audit log shows it doing. Each finding is a breach to act on or a matter to review.
/// </summary>
public sealed class ChecksModel(PageServices s) : InsightsPage(s)
{
    public override string Title => "Agent checks";
    public override string Persona => "Agents";
    public override string Intro => "Where an agent's settings in the agent workbook and its use in the audit log disagree: quarantined but used, web search off but searching the web, a developer environment with many users, production with none, no authentication, or an owner who has gone.";

    /// <summary>People in the period from which a developer-environment agent should move to production.</summary>
    const int DevPeople = 25;

    static readonly (string Key, string Label, string Kind, string Help)[] Checks =
    [
        ("quarantined", "Quarantined, but used", "Breach", "The workbook marks it quarantined, and people used it in the period."),
        ("websearch", "Web search off, but it searched the web", "Breach", "The workbook says web search is off; interactions in the period searched the web."),
        ("noauth", "No authentication, but used", "Review", "Anyone who can reach it can use it, and people did."),
        ("ownergone", "Owner gone, still used", "Review", "Its owner is not in the directory or is disabled, and people still use it."),
        ("dev", $"Developer environment, {DevPeople} people or more", "Review", "Used like a production agent: move it to a production environment."),
        ("produnused", "In production, not used", "Review", "On the AgentsInProd sheet, with no interaction in the period, and not autonomous."),
    ];

    sealed class Finding
    {
        public string Check { get; set; } = "";
        public string InventoryAgentId { get; set; } = "";
        public int? AgentKey { get; set; }
        public string AgentName { get; set; } = "";
        public string? Owner { get; set; }
        public string? Environment { get; set; }
        public long N { get; set; }
        public int People { get; set; }
        public long WebSearches { get; set; }
        public DateTime? LastUsed { get; set; }
    }

    protected override async Task BuildAsync()
    {
        if (!await Workbook.HasAsync(Db)) { Notice = Workbook.Missing; return; }
        if (await Db.ScalarAsync<int>("SELECT CASE WHEN COL_LENGTH(N'dash.AgentInventory', N'IsAutonomous') IS NULL THEN 0 ELSE 1 END;") != 1)
        {
            Notice = "The agent checks need the agent workbook's settings, which arrive with 2.5.8: run Install-CopilotAudit.ps1 -Dashboard, then load the workbook again.";
            return;
        }
        var dir = await Db.ScalarAsync<int>("SELECT CASE WHEN OBJECT_ID(N'audit.UserDirectory', N'U') IS NULL THEN 0 ELSE 1 END;") == 1;
        var rows = await Db.QueryAsync<Finding>($@"
SELECT f.AgentKey, COUNT_BIG(*) AS N, COUNT(DISTINCT f.UserKey) AS People, SUM(CAST(f.UsedWebSearch AS INT)) AS WebSearches
INTO #u
FROM bi.FactInteraction AS f {Ctx.Scope("f.UserKey")}
WHERE f.DateKey BETWEEN @k0 AND @k1
GROUP BY f.AgentKey;
SELECT i.*, COALESCE(NULLIF(i.AgentName, N''), al.FriendlyName, d.AgentName, i.InventoryAgentId) AS Name,
       NULLIF(LTRIM(RTRIM(COALESCE(i.OwnerUpn, al.Owner))), N'') AS Owner1,
       {(dir ? "CASE WHEN ud.UserPrincipalName IS NULL OR ud.AccountEnabled = 0 THEN 1 ELSE 0 END" : "0")} AS OwnerGone,
       ISNULL(u.N, 0) AS N, ISNULL(u.People, 0) AS People, ISNULL(u.WebSearches, 0) AS WebSearches, CAST(ld.[Date] AS DATETIME2(0)) AS LastUsed
INTO #x
FROM dash.AgentInventory AS i
LEFT JOIN bi.DimAgent AS d ON d.AgentKey = i.AgentKey
LEFT JOIN dash.AgentAlias AS al ON al.AgentKey = i.AgentKey
{(dir ? "LEFT JOIN audit.UserDirectory AS ud ON ud.UserPrincipalName = NULLIF(LTRIM(RTRIM(COALESCE(i.OwnerUpn, al.Owner))), N'')" : "")}
LEFT JOIN dash.AgentUse AS au ON au.AgentKey = i.AgentKey
LEFT JOIN bi.DimDate AS ld ON ld.DateKey = au.LastDateKey
LEFT JOIN #u AS u ON u.AgentKey = i.AgentKey;
SELECT c.[Check], x.InventoryAgentId, x.AgentKey, x.Name AS AgentName, x.Owner1 AS Owner, x.Environment, x.N, x.People, x.WebSearches, x.LastUsed
FROM #x AS x
CROSS APPLY (VALUES
    (CASE WHEN x.IsQuarantined = 1 AND x.N > 0 THEN N'quarantined' END),
    (CASE WHEN x.IsWebSearchEnabled = 0 AND x.WebSearches > 0 THEN N'websearch' END),
    (CASE WHEN x.N > 0 AND (x.Authentication LIKE N'%no auth%' OR x.Authentication IN (N'None', N'No authentication')) THEN N'noauth' END),
    (CASE WHEN x.OwnerGone = 1 AND x.Owner1 IS NOT NULL AND x.N > 0 THEN N'ownergone' END),
    (CASE WHEN x.EnvironmentSku IN (N'Developer', N'Trial', N'Sandbox') AND x.InProduction = 0 AND x.People >= @dev THEN N'dev' END),
    (CASE WHEN x.InProduction = 1 AND x.N = 0 AND x.IsAutonomous = 0 THEN N'produnused' END)) AS c([Check])
WHERE c.[Check] IS NOT NULL
ORDER BY x.N DESC, x.Name;", Ctx.Args(new { dev = DevPeople }));

        var chosen = Request.Query["check"].FirstOrDefault() is { } ck && Checks.Any(c => c.Key == ck) ? ck : null;
        foreach (var c in Checks)
        {
            var n = rows.Count(r => r.Check == c.Key);
            Kpis.Add(new(c.Label, Num(n), c.Help, n > 0 ? "warn" : null));
        }
        var tabs = $"<p class=\"sub\"><a href=\"{E(Ctx.Url(Request.Path))}\"{(chosen is null ? " class=\"on\"" : "")}>Every check</a>" +
                   string.Concat(Checks.Select(c => $" · <a href=\"{E(Ctx.Url(Request.Path, extra: "check=" + c.Key))}\"{(c.Key == chosen ? " class=\"on\"" : "")}>{E(c.Label)}</a>")) + "</p>";
        var shown = rows.Where(r => chosen is null || r.Check == chosen).ToList();
        var ownerName = await NamesAsync(shown.Select(r => r.Owner));
        var t = new Table
        {
            Headers = ["Check", "Kind", "Agent", "Owner", "Environment", "People in the period", "Interactions in the period", "Web searches", "Last used"],
            Numeric = new HashSet<int> { 5, 6, 7 }, EmptyText = "Nothing to act on: every agent's settings agree with its use.",
        };
        foreach (var r in shown)
        {
            var c = Checks.First(x => x.Key == r.Check);
            t.Rows.Add([c.Label, c.Kind, new Link(r.AgentName, Workbook.DetailUrl(Ctx, r.AgentKey, r.InventoryAgentId)), PrivacyMode ? null : ownerName(r.Owner), r.Environment,
                        r.People == 0 ? 0 : AtLeastMin(r.People), r.N, r.WebSearches, r.LastUsed?.Date]);
        }
        CsvTable = t;
        Panels.Add(new("Findings", new HtmlString(tabs + t.Render().Value),
            "A breach is an agent doing what its settings say it should not; a review is one to look at. Settings come from the agent workbook, use from the audit log for the period chosen." +
            (dir ? "" : " The directory is not loaded, so owners are not checked."), "full", CsvUrl()));
    }
}
/// <summary>
/// Child and connected agents (2.5.6): each child, the parent agents that call it, and the parents' use. From 2.5.7 a
/// child is what it is, not its name: a child agent lives inside its one parent, a connected agent is an agent of its
/// own that several parents can call, and children that share a name are shown with their parent or environment.
/// </summary>
public sealed class ChildModel(PageServices s) : InsightsPage(s)
{
    public override string Title => "Child agent usage";
    public override string Persona => "Agents";
    public override string Intro => "Agents that other agents call, from the workbook's ParentChildAgent sheet: child agents, which live inside their one parent, and connected agents, agents of their own that several parents can call. How many parent agents call each and which, each parent's use in the period from the audit log, and each child's total across its parents.";

    sealed class Row
    {
        public string ChildKey { get; set; } = "";
        public string ChildAgentName { get; set; } = "";
        public string? ChildKind { get; set; }
        public string? ChildInventoryAgentId { get; set; }
        public string ParentName { get; set; } = "";
        public string? ParentEnvironment { get; set; }
        public string? ParentInventoryAgentId { get; set; }
        public int? ParentAgentKey { get; set; }
        public string? RelationshipType { get; set; }
        public bool? IsValid { get; set; }
        public long N { get; set; }
        public int People { get; set; }
    }

    static string Kind(string? k) => k == "Connected" ? "Connected agent" : "Child agent (inside its parent)";

    protected override async Task BuildAsync()
    {
        if (await Db.ScalarAsync<int>("IF COL_LENGTH(N'dash.AgentChildLink', N'ChildKey') IS NULL SELECT 0 ELSE EXEC (N'SELECT CASE WHEN EXISTS (SELECT 1 FROM dash.AgentChildLink) THEN 1 ELSE 0 END');") != 1)
        {
            Notice = "No parent and child agents are loaded. tools\\Import-AgentWorkbook.ps1 loads them each night from the agent workbook's ParentChildAgent sheet (after Install-CopilotAudit.ps1 -Dashboard from 2.5.7).";
            return;
        }
        var (rows, people) = await Db.QueryTwoAsync<Row, (string ChildKey, int People)>($@"
SELECT f.AgentKey, COUNT_BIG(*) AS N, COUNT(DISTINCT f.UserKey) AS People
INTO #u
FROM bi.FactInteraction AS f {Ctx.Scope("f.UserKey")}
WHERE f.DateKey BETWEEN @k0 AND @k1 AND f.AgentKey IN (SELECT ParentAgentKey FROM dash.AgentChildLink WHERE ParentAgentKey IS NOT NULL)
GROUP BY f.AgentKey;
/* One row for each child and parent: a parent can call its child from several topics. */
SELECT l.ChildKey, MAX(l.ChildAgentName) AS ChildAgentName, MAX(l.ChildKind) AS ChildKind, MAX(l.ChildInventoryAgentId) AS ChildInventoryAgentId,
       COALESCE(MAX(NULLIF(l.ParentAgentName, N'')), MAX(d.AgentName), l.ParentInventoryAgentId, N'(unknown parent)') AS ParentName,
       MAX(l.ParentEnvironment) AS ParentEnvironment, l.ParentInventoryAgentId, MAX(l.ParentAgentKey) AS ParentAgentKey,
       MAX(l.RelationshipType) AS RelationshipType, CAST(MAX(CAST(l.IsValid AS INT)) AS BIT) AS IsValid,
       ISNULL(MAX(u.N), 0) AS N, ISNULL(MAX(u.People), 0) AS People
FROM dash.AgentChildLink AS l
LEFT JOIN bi.DimAgent AS d ON d.AgentKey = l.ParentAgentKey
LEFT JOIN #u AS u ON u.AgentKey = l.ParentAgentKey
GROUP BY l.ChildKey, l.ParentInventoryAgentId;
/* The people behind each child, counted once though they used several of its parents. */
SELECT c.ChildKey, COUNT(DISTINCT f.UserKey) AS People
FROM bi.FactInteraction AS f {Ctx.Scope("f.UserKey")}
JOIN (SELECT DISTINCT ChildKey, ParentAgentKey FROM dash.AgentChildLink WHERE ParentAgentKey IS NOT NULL) AS c ON c.ParentAgentKey = f.AgentKey
WHERE f.DateKey BETWEEN @k0 AND @k1
GROUP BY c.ChildKey;", Ctx.Args());

        var peopleOf = people.ToDictionary(p => p.ChildKey, p => p.People, StringComparer.OrdinalIgnoreCase);
        var children = rows.GroupBy(r => r.ChildKey, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Key: g.Key, Name: g.First().ChildAgentName, Kind: g.First().ChildKind, Inv: g.First().ChildInventoryAgentId,
                          Parents: g.ToList(), N: g.Sum(r => r.N), People: peopleOf.GetValueOrDefault(g.Key)))
            .OrderByDescending(c => c.N).ThenBy(c => c.Name).ToList();
        var chosen = Request.Query["child"].FirstOrDefault() is { Length: > 0 } ch && children.Any(c => string.Equals(c.Key, ch, StringComparison.OrdinalIgnoreCase)) ? ch : null;
        string? chosenName = chosen is null ? null : children.First(c => string.Equals(c.Key, chosen, StringComparison.OrdinalIgnoreCase)).Name;
        string Pick(string key) => Ctx.Url(Request.Path, extra: $"child={Uri.EscapeDataString(key)}");

        Kpis.Add(new("Child and connected agents", Num(children.Count), $"{children.Count(c => c.Kind == "Connected"):#,0} connected, {children.Count(c => c.Kind != "Connected"):#,0} inside their parent"));
        Kpis.Add(new("Parent agents", Num(rows.Select(r => r.ParentInventoryAgentId ?? r.ParentName).Distinct(StringComparer.OrdinalIgnoreCase).Count())));
        Kpis.Add(new("Parents used in the period", Num(rows.Where(r => r.N > 0).Select(r => r.ParentInventoryAgentId ?? r.ParentName).Distinct(StringComparer.OrdinalIgnoreCase).Count())));
        Kpis.Add(new("Interactions through parents", Num(rows.Sum(r => r.N)), "each parent counted once for each child it calls"));

        var options = string.Concat(children.OrderBy(c => c.Name).Select(c =>
            $"<option value=\"{E(Pick(c.Key))}\"{(string.Equals(c.Key, chosen, StringComparison.OrdinalIgnoreCase) ? " selected" : "")}>{E(c.Name)} ({c.Parents.Count})</option>"));
        Panels.Add(new("Choose a child agent", new HtmlString(
            $"<select class=\"pick\" aria-label=\"Child agent\" data-navigate><option value=\"{E(Ctx.Url(Request.Path))}\">Every child agent</option>{options}</select>")));

        var ct = new Table { Headers = ["Child agent", "Kind", "Parent agents", "Which", "People through its parents", "Interactions through its parents"], Numeric = new HashSet<int> { 2, 4, 5 } };
        foreach (var c in children.Where(c => chosen is null || string.Equals(c.Key, chosen, StringComparison.OrdinalIgnoreCase)))
            ct.Rows.Add([new Link(c.Name, Pick(c.Key)), Kind(c.Kind), c.Parents.Count,
                         string.Join("; ", c.Parents.Select(p => p.ParentName).OrderBy(n => n)), c.People == 0 ? 0 : AtLeastMin(c.People), c.N]);
        Panels.Add(new("Each child agent", ct.Render(),
            "A child agent is counted once for its parent: Copilot Studio names every new one \"Agent\", so children of one name are told apart by their parent, and connected agents by their environment. " +
            $"The totals are the parent agents' use in the period, from the audit log: a child's interactions add up its parents', and its people count each person once. People are left out where fewer than {Ctx.MinGroupSize}."));

        var pt = new Table
        {
            Headers = ["Child agent", "Kind", "Parent agent", "Parent's environment", "Relationship", "Valid", "People", "Interactions", "Use in the period"],
            Numeric = new HashSet<int> { 6, 7 }, EmptyText = "No parent agents.",
        };
        foreach (var r in rows.Where(r => chosen is null || string.Equals(r.ChildKey, chosen, StringComparison.OrdinalIgnoreCase))
                              .OrderBy(r => r.ChildAgentName).ThenByDescending(r => r.N).ThenBy(r => r.ParentName))
            pt.Rows.Add([r.ChildInventoryAgentId is null ? r.ChildAgentName : new Link(r.ChildAgentName, Workbook.DetailUrl(Ctx, null, r.ChildInventoryAgentId)),
                         Kind(r.ChildKind),
                         r.ParentInventoryAgentId is null && r.ParentAgentKey is null ? r.ParentName : new Link(r.ParentName, Workbook.DetailUrl(Ctx, r.ParentAgentKey, r.ParentInventoryAgentId ?? "")),
                         r.ParentEnvironment, r.RelationshipType, r.IsValid switch { true => "Yes", false => "No", _ => null },
                         r.People == 0 ? 0 : AtLeastMin(r.People), r.N, r.N > 0 ? "Used" : "Not used"]);
        CsvTable = pt;
        Panels.Add(new(chosenName is null ? "Each parent agent" : $"Parent agents calling {chosenName}", pt.Render(),
            "A parent's use is its own, from the audit log: the audit log does not say which of its interactions reached the child.", "full", CsvUrl()));
    }
}
