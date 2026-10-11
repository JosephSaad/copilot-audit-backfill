using CopilotInsights.Charts;
using CopilotInsights.Data;
using CopilotInsights.Infrastructure;
using Microsoft.AspNetCore.Html;

namespace CopilotInsights.Pages.Agents;

public sealed class IndexModel(PageServices s) : InsightsPage(s)
{
    public override string Title => "Agents in use";
    public override string Persona => "Agents";
    public override string Intro => "Every agent people used in the period: how many people, how often, whether they come back, and which agents are new.";

    sealed class Row
    {
        public int AgentKey { get; set; }
        public string AgentName { get; set; } = "";
        public string? AgentSource { get; set; }
        public string? Owner { get; set; }
        public DateTime FirstSeenUtc { get; set; }
        public long N { get; set; }
        public int People { get; set; }
        public int Returning { get; set; }
        public bool IsShared { get; set; }
        public string? InventoryAgentId { get; set; }
    }

    protected override async Task BuildAsync()
    {
        var book = await Workbook.HasAsync(Db);   // 2.5.6: names, owners and sharing from the agent workbook
        var rows = await Db.QueryAsync<Row>($@"
WITH u AS (SELECT f.AgentKey, f.UserKey, COUNT_BIG(*) AS N, COUNT(DISTINCT f.DateKey) AS Days
           FROM bi.FactInteraction AS f {Ctx.Scope("f.UserKey")}
           WHERE f.DateKey BETWEEN @k0 AND @k1 GROUP BY f.AgentKey, f.UserKey)
SELECT ag.AgentKey, ag.AgentName, ag.AgentSource, ag.Owner, ag.FirstSeenUtc, SUM(u.N) AS N, COUNT(*) AS People,
       COUNT(CASE WHEN u.Days > 1 THEN 1 END) AS Returning, ag.IsShared, ag.InventoryAgentId
FROM u JOIN dash.Agent AS ag ON ag.AgentKey = u.AgentKey
WHERE ag.AgentSource <> N'(no agent)'
GROUP BY ag.AgentKey, ag.AgentName, ag.AgentSource, ag.Owner, ag.FirstSeenUtc, ag.IsShared, ag.InventoryAgentId;", Ctx.Args());
        var trend = await Db.QueryAsync<(int AgentKey, DateTime Bucket, int People)>($@"
SELECT f.AgentKey, {Ctx.BucketSql("d")}, COUNT(DISTINCT f.UserKey)
FROM bi.FactInteraction AS f {Ctx.Scope("f.UserKey")}
JOIN bi.DimDate AS d ON d.DateKey = f.DateKey
JOIN bi.DimAgent AS a ON a.AgentKey = f.AgentKey AND a.AgentSource <> N'(no agent)'
WHERE f.DateKey BETWEEN @k0 AND @k1
GROUP BY f.AgentKey, {Ctx.BucketSql("d")};", Ctx.Args());
        var buckets = Buckets();
        var axis = Labels();
        var isNew = (Row r) => r.FirstSeenUtc.Date >= Ctx.Start;

        Kpis.Add(new("Agents used", Num(rows.Count), $"{rows.Count(r => r.AgentSource == "Copilot Studio"):#,0} built in Copilot Studio"));
        Kpis.Add(new("New in the period", Num(rows.Count(isNew)), "first used in the period"));
        Kpis.Add(new("People using agents", Num(await Db.ScalarAsync<int>($@"
SELECT COUNT(DISTINCT ud.UserKey) FROM dash.UserDay AS ud {Ctx.Scope("ud.UserKey")}
WHERE ud.DateKey BETWEEN @k0 AND @k1 AND ud.AgentInteractions > 0;", Ctx.Args()))));
        Kpis.Add(new("Interactions through an agent", Num(rows.Sum(r => r.N))));
        if (book)
        {
            /* The same counts as Agent inventory's (2.5.7): workbook agents, used when the audit agent they match was used
               in the period. Several workbook agents can match one audit agent, so they are counted from the workbook. */
            /* 2.5.8: an autonomous agent (triggers or flows) with no interaction is not counted as unused, as on Agent inventory. */
            var auto = await Db.ScalarAsync<int>("SELECT CASE WHEN COL_LENGTH(N'dash.AgentInventory', N'IsAutonomous') IS NULL THEN 0 ELSE 1 END;") == 1;
            var inv = await Db.QueryAsync<(string Id, int? AgentKey, bool Autonomous)>(
                $"SELECT InventoryAgentId, AgentKey, {(auto ? "IsAutonomous" : "CAST(0 AS BIT)")} FROM dash.AgentInventory;");
            var used = rows.Select(r => r.AgentKey).ToHashSet();
            var listedUsed = inv.Count(i => i.AgentKey is { } k && used.Contains(k));
            var autonomous = inv.Count(i => i.Autonomous && !(i.AgentKey is { } k && used.Contains(k)));
            var listedKeys = inv.Where(i => i.AgentKey is not null).Select(i => i.AgentKey!.Value).ToHashSet();
            var inBook = rows.Count(r => listedKeys.Contains(r.AgentKey));
            var at = Kpis.FindIndex(k => k.Label == "Agents used");
            if (at >= 0) Kpis[at] = Kpis[at] with { Note = $"{inBook:#,0} in the agent workbook, {rows.Count - inBook:#,0} not in it (deleted, unmatched or Microsoft's)" };
            var notUsed = inv.Count - listedUsed - autonomous;
            Kpis.Add(new("Listed agents not used", Num(notUsed), $"of {inv.Count:#,0} in the agent workbook; {listedUsed:#,0} used in the period, {autonomous:#,0} autonomous", notUsed > 0 ? "warn" : null));
        }

        /* People per agent are a group at every point too (2.4.12): agents with fewer than the minimum in the period are
           left out, and a point under it is a gap, as the table's counts are. */
        double? Point(int agentKey, DateTime bucket) => PointAtMin(trend.Where(t => t.AgentKey == agentKey && t.Bucket == bucket).Sum(t => t.People));
        var top = rows.Where(r => AtLeastMin(r.People) is not null).OrderByDescending(r => r.People).Take(6).ToList();
        Panels.Add(new("People using the top agents",
            Chart.Line(axis, top.Select((r, i) => new Series(r.AgentName, buckets.Select(b => Point(r.AgentKey, b.Key)).ToList(), i + 1)).ToList(), Fmt.Count, "People per agent"),
            $"The six agents with the most people in the period. A point where fewer than {Ctx.MinGroupSize} people used an agent is left out. {TrendNote}"));

        var t = new Table
        {
            Headers = book
                ? ["Agent", "Built in", "Owner", "Shared", "In the agent list", "First used", "People", "Came back", "Interactions", "Interactions per person", "Trend (people)"]
                : ["Agent", "Built in", "Owner", "First used", "People", "Came back", "Interactions", "Interactions per person", "Trend (people)"],
            Numeric = book ? new HashSet<int> { 6, 7, 8, 9 } : new HashSet<int> { 4, 5, 6, 7 }, EmptyText = "No agents were used in the period.",
        };
        var ownerName = await NamesAsync(rows.Select(r => r.Owner));   // 2.5.8: owners by name
        foreach (var r in rows.OrderByDescending(r => r.People))
        {
            var cells = new List<object?> { new Link(r.AgentName + (isNew(r) ? " (new)" : ""), Ctx.Url("/agents/detail", extra: $"agent={r.AgentKey}")), r.AgentSource, PrivacyMode ? null : ownerName(r.Owner) };
            /* The workbook lists Copilot Studio agents: one of those it does not list was deleted, or is not in it yet. */
            if (book) cells.AddRange([r.IsShared ? "Yes" : "No",
                                      r.InventoryAgentId is not null ? "Yes" : r.AgentSource == "Copilot Studio" ? "No: deleted, or not listed" : "No"]);
            cells.AddRange([r.FirstSeenUtc.Date,
                AtLeastMin(r.People), AtLeastMin(r.People) is null ? null : Pct(Ratio(r.Returning, r.People)), r.N,
                AtLeastMin(r.People) is null ? null : Math.Round(r.N / (double)Math.Max(1, r.People), 1),
                AtLeastMin(r.People) is null ? null : Chart.Sparkline(axis, buckets.Select(b => Point(r.AgentKey, b.Key)).ToList(), r.AgentName)]);
            t.Rows.Add([.. cells]);
        }
        Panels.Add(new("All agents", WithTrendNote(t.Render()),
            $"Came back: the share of its people who used it on more than one day. People are shown only where {Ctx.MinGroupSize} or more used an agent. Names and owners come from the agent workbook; an agent it does not list (one since deleted) has the one set in Admin › Reference data, else the audit log's. Select an agent for its detail." +
            (PrivacyMode ? " Privacy mode is on, so owners are not shown." : "")));
    }
}

public sealed class DetailModel(PageServices s) : InsightsPage(s)
{
    public override string Title => AgentName ?? "Agent detail";
    public override string Persona => "Agents";
    public override string Intro => "One agent: who uses it, where, in which apps and how that is changing.";
    string? AgentName { get; set; }

    static readonly Dictionary<string, string> Sorts = new()
    {
        ["most"] = "x.N DESC", ["recent"] = "x.LastKey DESC", ["name"] = "du.DisplayName", ["dept"] = "du.Department, du.DisplayName",
        ["upn"] = "du.UserPrincipalName", ["days"] = "x.Days DESC, du.DisplayName, du.UserPrincipalName",
    };
    static readonly Dictionary<string, string> SortLabels = new() { ["most"] = "Most interactions", ["recent"] = "Most recent", ["name"] = "Name", ["dept"] = "Department" };

    sealed class Row
    {
        public string? DisplayName { get; set; }
        public string UserPrincipalName { get; set; } = "";
        public string? Department { get; set; }
        public long N { get; set; }
        public int Days { get; set; }
        public DateTime Last { get; set; }
        public int TotalRows { get; set; }
    }

    protected override async Task BuildAsync()
    {
        var agents = await Db.QueryAsync<(int AgentKey, string AgentName, string? AgentSource)>(
            "SELECT AgentKey, AgentName, AgentSource FROM dash.Agent WHERE AgentSource <> N'(no agent)' ORDER BY AgentName;");
        var key = int.TryParse(Request.Query["agent"], out var k) && agents.Any(a => a.AgentKey == k) ? k : (int?)null;
        /* 2.5.6: an agent from the workbook, by its workbook id: the audit log's agent where it knows it, else the
           workbook's row alone, flagged as unused. */
        string? invId = Request.Query["inv"].FirstOrDefault() is { Length: > 0 and <= 100 } iv ? iv : null;
        if (key is null && invId is not null && await Workbook.HasAsync(Db))
        {
            var inv = (await Db.QueryAsync<(int? AgentKey, string? AgentName)>(
                "SELECT AgentKey, AgentName FROM dash.AgentInventory WHERE InventoryAgentId = @invId;", new { invId })).FirstOrDefault();
            if (inv.AgentKey is { } ik && agents.Any(a => a.AgentKey == ik)) key = ik;
            else if (inv.AgentName is not null || await Db.ScalarAsync<int>("SELECT COUNT(*) FROM dash.AgentInventory WHERE InventoryAgentId = @invId;", new { invId }) > 0)
            {
                AgentName = inv.AgentName ?? invId;
                var autonomous = await Db.ScalarAsync<int>(@"IF COL_LENGTH(N'dash.AgentInventory', N'IsAutonomous') IS NULL SELECT 0
ELSE EXEC sp_executesql N'SELECT ISNULL(MAX(CAST(IsAutonomous AS INT)), 0) FROM dash.AgentInventory WHERE InventoryAgentId = @i', N'@i NVARCHAR(100)', @i = @invId;", new { invId }) == 1;
                Kpis.Add(autonomous
                    ? new("Use", "Autonomous", "no interaction in the audit log, but it has triggers or flows: it can work with nobody chatting to it")
                    : new("Use", "Not used", "the audit log has no interaction with this agent", "warn"));
                await AddWorkbookMetadataAsync(invId);
                return;
            }
        }
        var options = string.Concat(agents.Select(a =>
            $"<option value=\"{System.Net.WebUtility.HtmlEncode(Ctx.Url(Request.Path, extra: $"agent={a.AgentKey}"))}\"{(a.AgentKey == key ? " selected" : "")}>{System.Net.WebUtility.HtmlEncode(a.AgentName)} ({System.Net.WebUtility.HtmlEncode(a.AgentSource)})</option>"));
        /* With none chosen the box is the page: it says what to do, and where else an agent can be picked (2.4.10: the
           hint sat above it and said "above"). */
        Panels.Add(new(key is null ? "Choose an agent" : "Agent", new HtmlString(
            $"<select class=\"pick\" aria-label=\"Agent\" data-navigate><option value=\"\">Choose an agent</option>{options}</select>" +
            (key is null ? $"<p class=\"sub\">Or select an agent's name on <a href=\"{System.Net.WebUtility.HtmlEncode(Ctx.Url("/agents"))}\">Agents in use</a>.</p>" : ""))));
        if (key is null) return;
        AgentName = agents.First(a => a.AgentKey == key).AgentName;
        var args = Ctx.Args(new { agent = key });

        var tr = await Db.QueryAsync<(DateTime Bucket, int People, long N)>($@"
SELECT {Ctx.BucketSql("d")}, COUNT(DISTINCT f.UserKey), COUNT_BIG(*)
FROM bi.FactInteraction AS f {Ctx.Scope("f.UserKey")}
JOIN bi.DimDate AS d ON d.DateKey = f.DateKey
WHERE f.AgentKey = @agent AND f.DateKey BETWEEN @k0 AND @k1
GROUP BY {Ctx.BucketSql("d")};", args);
        var tot = (await Db.QueryAsync<(int People, long N, int Returning, int NewPeople)>($@"
WITH u AS (SELECT f.UserKey, COUNT_BIG(*) AS N, COUNT(DISTINCT f.DateKey) AS Days, MIN(f.DateKey) AS FirstKey
           FROM bi.FactInteraction AS f {Ctx.Scope("f.UserKey")} WHERE f.AgentKey = @agent AND f.DateKey <= @k1 GROUP BY f.UserKey),
p AS (SELECT f.UserKey, COUNT_BIG(*) AS N, COUNT(DISTINCT f.DateKey) AS Days
      FROM bi.FactInteraction AS f {Ctx.Scope("f.UserKey")} WHERE f.AgentKey = @agent AND f.DateKey BETWEEN @k0 AND @k1 GROUP BY f.UserKey)
SELECT (SELECT COUNT(*) FROM p), (SELECT ISNULL(SUM(N), 0) FROM p), (SELECT COUNT(*) FROM p WHERE Days > 1),
       (SELECT COUNT(*) FROM u WHERE FirstKey >= @k0);", args)).Single();
        /* Its people are a group: counted only at the minimum group size or more (2.4.12). */
        var few = tot.People > 0 && AtLeastMin(tot.People) is null;
        Kpis.Add(new("People who used it", few ? "—" : Num(tot.People), few ? $"fewer than {Ctx.MinGroupSize} used it in the period" : "used it in the period"));
        Kpis.Add(new("New people", few ? "—" : Num(tot.NewPeople), "first used it in the period"));
        Kpis.Add(new("Came back", few ? "—" : Pct(Ratio(tot.Returning, tot.People)), "used it on more than one day"));
        Kpis.Add(new("Interactions", Num(tot.N), tot.People > 0 && !few ? $"{tot.N / (double)tot.People:0.0} per person" : null));

        var buckets = Buckets();
        /* Each point at the minimum group size or more (2.4.12), as the KPI above is. */
        Panels.Add(new("People using it", Chart.Line(Labels(), [new Series("People", buckets.Select(b => PointAtMin(tr.Where(x => x.Bucket == b.Key).Sum(x => x.People))).ToList(), 1)], Fmt.Count, "People using the agent"),
            $"A point where fewer than {Ctx.MinGroupSize} people used it is left out. {TrendNote}", "half"));
        Panels.Add(new("Interactions", Chart.Columns(Labels(), [new Series("Interactions", buckets.Select(b => (double?)tr.Where(x => x.Bucket == b.Key).Sum(x => x.N)).ToList(), 1)], Fmt.Count, "Agent interactions"),
            null, "half"));

        var depts = await Db.QueryAsync<(string Dept, int People, long N)>($@"
SELECT ISNULL(NULLIF(du.Department, N''), N'(no department)'), COUNT(DISTINCT f.UserKey), COUNT_BIG(*)
FROM bi.FactInteraction AS f {Ctx.Scope("f.UserKey")}
JOIN bi.DimUser AS du ON du.UserKey = f.UserKey
WHERE f.AgentKey = @agent AND f.DateKey BETWEEN @k0 AND @k1
GROUP BY ISNULL(NULLIF(du.Department, N''), N'(no department)');", args);
        /* The combined row is left out when even it is under the minimum (2.4.12): with every department small, it was
           everyone who used the agent, one or two people. */
        var shownDepts = FoldSmall(depts, d => d.People, small => ("Smaller departments", small.Sum(x => x.People), small.Sum(x => x.N)), out _)
            .Where(d => d.People >= Ctx.MinGroupSize).ToList();
        Panels.Add(new("By department", Chart.Bars(shownDepts.OrderByDescending(d => d.People).Take(15).Select(d => new Bar(d.Dept, d.People, Note: $"{d.N:#,0} interactions")).ToList(), Fmt.Count, "People by department"),
            $"People who used it. Departments where fewer than {Ctx.MinGroupSize} people used it are combined.", "half"));
        var apps = await Db.QueryAsync<(string App, long N)>($@"
SELECT COALESCE(vn.Name, a.App), COUNT_BIG(*) FROM bi.FactInteraction AS f {Ctx.Scope("f.UserKey")} JOIN bi.DimApp AS a ON a.AppKey = f.AppKey
LEFT JOIN dash.ValueName AS vn ON vn.ColumnId = 2 AND vn.Value = a.App
WHERE f.AgentKey = @agent AND f.DateKey BETWEEN @k0 AND @k1 GROUP BY a.App, vn.Name;", args);
        Panels.Add(new("Where it is used", Chart.Bars(apps.OrderByDescending(a => a.N).Select(a => new Bar(a.App, a.N)).ToList(), Fmt.Count, "Agent interactions by app"), null, "half"));

        if (await Workbook.HasAsync(Db)
            && await Db.ScalarAsync<string?>("SELECT InventoryAgentId FROM dash.Agent WHERE AgentKey = @key;", new { key }) is { } bookId)
            await AddWorkbookMetadataAsync(bookId);

        if (!Viewer.Has(Roles.Agents) || PrivacyMode) return;   // the named users are for the Agents role only, and never in privacy mode
        var pg = new Paging(Request, Sorts, "most");
        var rows = await Db.QueryAsync<Row>($@"
WITH x AS (SELECT f.UserKey, COUNT_BIG(*) AS N, COUNT(DISTINCT f.DateKey) AS Days, MAX(f.DateKey) AS LastKey
           FROM bi.FactInteraction AS f {Ctx.Scope("f.UserKey")} WHERE f.AgentKey = @agent AND f.DateKey BETWEEN @k0 AND @k1 GROUP BY f.UserKey)
SELECT du.DisplayName, du.UserPrincipalName, du.Department, x.N, x.Days, dl.[Date] AS Last, COUNT(*) OVER () AS TotalRows
FROM x JOIN bi.DimUser AS du ON du.UserKey = x.UserKey JOIN bi.DimDate AS dl ON dl.DateKey = x.LastKey
WHERE (@q IS NULL OR du.DisplayName LIKE @q OR du.UserPrincipalName LIKE @q OR du.Department LIKE @q)
{pg.OrderAndPage()};", Ctx.Args(new { agent = key, q = pg.Like }));
        pg.Total = rows.FirstOrDefault()?.TotalRows ?? 0;
        var t = new Table { Headers = ["Name", "Sign-in", "Department", "Interactions", "Days", "Last used"], Numeric = new HashSet<int> { 3, 4 },
            Server = new(pg, Request, ["name", "upn", "dept", "most", "days", "recent"], new HashSet<int> { 3, 4, 5 }) };
        foreach (var r in rows) t.Rows.Add([r.DisplayName, r.UserPrincipalName, r.Department, r.N, r.Days, r.Last]);
        CsvTable = t;
        Panels.Add(new("Users", new HtmlString(pg.Controls(Request, SortLabels).Value + t.Render().Value), null, "full", CsvUrl()));
    }

    /// <summary>
    /// The agent's row of the workbook's first sheet, every column as it comes (2.5.6): a column the workbook gains
    /// shows without a change here. Read from dbo.AgentInventory, which the import grants the site.
    /// </summary>
    async Task AddWorkbookMetadataAsync(string inventoryAgentId)
    {
        IDictionary<string, object?>? row;
        try
        {
            row = (await Db.QueryAsync<dynamic>(
                "IF OBJECT_ID(N'dbo.AgentInventory', N'U') IS NOT NULL SELECT TOP (1) * FROM dbo.AgentInventory WHERE CAST(AgentId AS NVARCHAR(100)) = @id;",
                new { id = inventoryAgentId })).Cast<IDictionary<string, object?>>().FirstOrDefault();
        }
        catch (Microsoft.Data.SqlClient.SqlException) { row = null; }
        if (row is null) return;
        var named = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "CreatedByUPN", "OwnerIdUPN", "CreatedBy", "OwnerId" };
        var t = new Table { Headers = ["Field", "Value"], Sortable = false };
        foreach (var (field, value) in row)
            t.Rows.Add([field, PrivacyMode && named.Contains(field) ? null : value?.ToString()]);
        Panels.Add(new("From the agent workbook", t.Render(),
            "Every column of this agent's row on the workbook's first sheet, as loaded last." + (PrivacyMode ? " Privacy mode is on, so the people behind it are not shown." : ""), "full"));
    }
}
