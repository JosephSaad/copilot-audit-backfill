using CopilotInsights.Charts;
using CopilotInsights.Infrastructure;
using Microsoft.AspNetCore.Html;

namespace CopilotInsights.Pages.Agents;

/// <summary>
/// Who builds, publishes and shares Copilot Studio agents: the authoring records Copilot Studio writes
/// to the audit log (BotCreate, BotUpdateOperation-BotPublish, ...), loaded with the licence events.
/// Agent lifecycles for everyone with data to see; the makers' names for the Agents role.
/// </summary>
public sealed class BuildModel(PageServices s) : InsightsPage(s)
{
    public override string Title => "Build and publish";
    public override string Persona => "Agents";
    public override string Intro => "Copilot Studio agents created, published, shared, changed and deleted, from Copilot Studio's own audit records: each agent's lifecycle, and how much building goes on. The makers' names need the Agents role.";

    sealed class Life
    {
        public string BotSchemaName { get; set; } = "";
        public DateTime? Created { get; set; }
        public DateTime? FirstPublished { get; set; }
        public DateTime? LastPublished { get; set; }
        public int Publishes { get; set; }
        public int Shares { get; set; }
        public int Changes { get; set; }
        public DateTime? Deleted { get; set; }
        public int Makers { get; set; }
        public string? LastMaker { get; set; }
        public string? FriendlyName { get; set; }
        public string? Owner { get; set; }
    }

    protected override async Task BuildAsync()
    {
        if (await Db.ScalarAsync<int>("SELECT CASE WHEN OBJECT_ID(N'bi.FactAgentEvent', N'U') IS NULL THEN 0 ELSE 1 END") == 0)
        { Notice = "Agent build and publish records arrive with 2.3.0's licence-event load. Run Install-CopilotAudit.ps1 from this release."; return; }
        var named = Viewer.Has(Roles.Agents) && !PrivacyMode;
        /* Makers in scope: a manager sees the building their own people do; whole-organisation viewers see all of it. */
        var scope = Ctx.Root is null ? "" : "JOIN dash.fnScopeUsers(@root) AS sc ON sc.UserKey = e.ActorKey";
        var byAction = await Db.QueryAsync<(DateTime Bucket, string Action, int N)>($@"
SELECT {Ctx.BucketSql("d")}, e.Action, COUNT(*)
FROM bi.FactAgentEvent AS e {scope} JOIN bi.DimDate AS d ON d.DateKey = e.DateKey
WHERE e.DateKey BETWEEN @k0 AND @k1
GROUP BY {Ctx.BucketSql("d")}, e.Action;", Ctx.Args());
        int Count(string a) => byAction.Where(x => x.Action == a).Sum(x => x.N);
        var makers = await Db.ScalarAsync<int>($@"SELECT COUNT(DISTINCT e.ActorUpn) FROM bi.FactAgentEvent AS e {scope} WHERE e.DateKey BETWEEN @k0 AND @k1;", Ctx.Args());
        Kpis.Add(new("Agents created", Num(Count("Created"))));
        Kpis.Add(new("Publishes", Num(Count("Published"))));
        Kpis.Add(new("Shares", Num(Count("Shared"))));
        Kpis.Add(new("Makers", Num(makers), "people who built or changed an agent"));
        Kpis.Add(new("Deleted", Num(Count("Deleted"))));

        var buckets = Buckets();
        Series S(string action, int slot) => new(action, buckets.Select(b => (double?)byAction.Where(x => x.Bucket == b.Key && x.Action == action).Sum(x => x.N)).ToList(), slot);
        Panels.Add(new("Authoring over time", Chart.Columns(Labels(), [S("Created", 1), S("Published", 3), S("Shared", 5), S("Component", 2), S("Updated", 4), S("Deleted", 6)], Fmt.Count, "Agent authoring events"),
            TrendNote + " Component: topics, skills and other parts added, changed or removed."));

        var life = await Db.QueryAsync<Life>($@"
WITH e AS (SELECT e.* FROM bi.FactAgentEvent AS e {scope} WHERE e.BotSchemaName IS NOT NULL)
SELECT e.BotSchemaName,
       MIN(CASE WHEN e.Action = 'Created' THEN e.EventUtc END) AS Created,
       MIN(CASE WHEN e.Action = 'Published' THEN e.EventUtc END) AS FirstPublished,
       MAX(CASE WHEN e.Action = 'Published' THEN e.EventUtc END) AS LastPublished,
       COUNT(CASE WHEN e.Action = 'Published' THEN 1 END) AS Publishes,
       COUNT(CASE WHEN e.Action = 'Shared' THEN 1 END) AS Shares,
       COUNT(CASE WHEN e.Action IN ('Updated', 'Component') THEN 1 END) AS Changes,
       MAX(CASE WHEN e.Action = 'Deleted' THEN e.EventUtc END) AS Deleted,
       COUNT(DISTINCT e.ActorUpn) AS Makers,
       (SELECT TOP (1) x.ActorUpn FROM e AS x WHERE x.BotSchemaName = e.BotSchemaName ORDER BY x.EventUtc DESC) AS LastMaker,
       MAX(al.FriendlyName) AS FriendlyName, MAX(al.Owner) AS Owner
FROM e
LEFT JOIN bi.DimAgent AS ag ON ag.AgentId = e.BotSchemaName OR ag.AgentName = e.BotSchemaName
LEFT JOIN dash.AgentAlias AS al ON al.AgentKey = ag.AgentKey
GROUP BY e.BotSchemaName
ORDER BY MAX(e.EventUtc) DESC;", Ctx.Args());
        var neverPublished = life.Count(l => l.Created is not null && l.FirstPublished is null && l.Deleted is null);
        var t = new Table
        {
            Headers = named
                ? ["Agent", "Created", "First published", "Last published", "Publishes", "Shares", "Changes", "Deleted", "Makers", "Last changed by", "Owner"]
                : ["Agent", "Created", "First published", "Last published", "Publishes", "Shares", "Changes", "Deleted", "Makers", "Owner"],
            Numeric = new HashSet<int> { 4, 5, 6, 8 },
            EmptyText = "No authoring records yet.",
        };
        foreach (var l in life)
        {
            var name = l.FriendlyName is null ? l.BotSchemaName : $"{l.FriendlyName} ({l.BotSchemaName})";
            t.Rows.Add(named
                ? [name, l.Created, l.FirstPublished, l.LastPublished, l.Publishes, l.Shares, l.Changes, l.Deleted, l.Makers, l.LastMaker, l.Owner]
                : [name, l.Created, l.FirstPublished, l.LastPublished, l.Publishes, l.Shares, l.Changes, l.Deleted, l.Makers, l.Owner]);
        }
        CsvTable = t;
        Panels.Add(new("Every agent's lifecycle", t.Render(),
            $"{neverPublished} created and never published. All time, not only the period. Name agents and give them owners in Admin > Reference data." +
            (named ? "" : " The makers' names are shown to the Agents role."), "full", CsvUrl()));
    }
}
