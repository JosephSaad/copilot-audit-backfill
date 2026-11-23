using CopilotInsights.Charts;
using CopilotInsights.Infrastructure;
using Microsoft.AspNetCore.Html;

namespace CopilotInsights.Pages.Agents;

/// <summary>
/// Who builds, publishes and shares Copilot Studio agents: the authoring records Copilot Studio writes
/// to the audit log (BotCreate, BotUpdateOperation-BotPublish, ...), loaded with the license events.
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
        /// <summary>The name the audit log gave the agent, where it gave one.</summary>
        public string? LogName { get; set; }
        public string? Owner { get; set; }
    }

    protected override async Task BuildAsync()
    {
        if (await Db.ScalarAsync<int>("SELECT CASE WHEN OBJECT_ID(N'bi.FactAgentEvent', N'U') IS NULL THEN 0 ELSE 1 END") == 0)
        { Notice = "Nothing has been recorded yet about agents being built and published. An administrator sets this up by running Install-CopilotAudit.ps1 from the current release; the records then arrive with the nightly load."; return; }
        var named = Viewer.Has(Roles.Agents) && !PrivacyMode;
        /* Makers in scope: a manager sees the building their own people do; whole-organization viewers see all of it. */
        var scope = Ctx.Root is null ? "" : "JOIN dash.fnScopeUsers(@root) AS sc ON sc.UserKey = e.ActorKey";
        var byAction = await Db.QueryAsync<(DateTime Bucket, string Action, int N)>($@"
SELECT {Ctx.BucketSql("d")}, e.Action, COUNT(*)
FROM bi.FactAgentEvent AS e {scope} JOIN bi.DimDate AS d ON d.DateKey = e.DateKey
WHERE e.DateKey BETWEEN @k0 AND @k1
GROUP BY {Ctx.BucketSql("d")}, e.Action;", Ctx.Args());
        int Count(string a) => byAction.Where(x => x.Action == a).Sum(x => x.N);
        /* Makers are people: enabled accounts only (3.1.3). The events themselves are everyone's. */
        var makers = await Db.ScalarAsync<int>($@"SELECT COUNT(DISTINCT e.ActorUpn) FROM bi.FactAgentEvent AS e {scope} {ReportContext.EnabledJoin("e.ActorKey")} WHERE e.DateKey BETWEEN @k0 AND @k1 AND e.Action NOT IN ('Blocked', 'Unblocked', 'Deployed', 'Removed', 'AdminDeleted', 'AdminUpdated', 'Settings');", Ctx.Args());
        Kpis.Add(new("Agents created", Num(Count("Created")), "in Copilot Studio, from its own audit records"));
        Kpis.Add(new("Times published", Num(Count("Published")), "an agent published again counts each time"));
        Kpis.Add(new("Times shared", Num(Count("Shared")), "an agent shared again counts each time"));
        Kpis.Add(new("Makers", Num(makers), "people who built or changed an agent"));
        Kpis.Add(new("Agents deleted", Num(Count("Deleted")), "in Copilot Studio, from its own audit records"));

        var buckets = Buckets();
        Series S(string action, int slot) => new(action, buckets.Select(b => (double?)byAction.Where(x => x.Bucket == b.Key && x.Action == action).Sum(x => x.N)).ToList(), slot);
        Panels.Add(new("Building activity over time", Chart.Columns(Labels(), [S("Created", 1), S("Published", 3), S("Shared", 5), S("Component", 2), S("Updated", 4), S("Deleted", 6)], Fmt.Count, "Agent authoring events"),
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
       MAX(al.FriendlyName) AS FriendlyName, MAX(CASE WHEN ag.AgentName <> e.BotSchemaName THEN ag.AgentName END) AS LogName, MAX(COALESCE(da.Owner, al.Owner)) AS Owner
FROM e
/* A Copilot Studio agent's id ends in its schema name (Copilot.Studio.<environment>-<schema name>). It was found by
   its name, which was the schema name until 2.4.11 named agents as the audit log does; then the alias and owner
   set in Admin › Reference data went missing here (2.4.12). Compared as text, not LIKE: _ is in schema names. */
LEFT JOIN bi.DimAgent AS ag ON ag.AgentId = e.BotSchemaName
    OR (ag.AgentSource = N'Copilot Studio' AND RIGHT(ag.AgentId, LEN(e.BotSchemaName) + 1) = N'-' + e.BotSchemaName)
LEFT JOIN dash.AgentAlias AS al ON al.AgentKey = ag.AgentKey
/* 2.6.6: the owner the agents pages show (dash.Agent: the agent inventory's, else Admin's alias), not the alias's alone. */
LEFT JOIN dash.Agent AS da ON da.AgentKey = ag.AgentKey
GROUP BY e.BotSchemaName
ORDER BY MAX(e.EventUtc) DESC;", Ctx.Args());
        /* What administrators did to agents in the Microsoft 365 admin center (CopilotAgentManagement records):
           blocked, unblocked, deployed, removed, deleted or changed an agent, or changed the tenant's agent settings. */
        var admin = await Db.QueryAsync<(DateTime At, string Action, string? Agent, string? ActorUpn)>(@"
SELECT TOP (200) e.EventUtc, e.Action, COALESCE(e.ComponentName, e.BotSchemaName, NULLIF(e.BotId, N'')), e.ActorUpn
FROM bi.FactAgentEvent AS e
WHERE e.DateKey BETWEEN @k0 AND @k1 AND e.Action IN ('Blocked', 'Unblocked', 'Deployed', 'Removed', 'AdminDeleted', 'AdminUpdated', 'Settings')
ORDER BY e.EventUtc DESC;", Ctx.Args());
        if (admin.Count > 0)
        {
            /* The organization's whatever team is chosen, so who did it is named only with the whole organization in view:
               a manager's scope would otherwise name administrators outside it. */
            var byShown = named && Ctx.Root is null;
            static string Said(string a) => a switch { "AdminDeleted" => "Deleted", "AdminUpdated" => "Changed", "Settings" => "Tenant agent settings changed", _ => a };
            var n = (await Db.QueryAsync<(int All, int Blocked, int Deployed)>(@"
SELECT COUNT(*), COUNT(CASE WHEN e.Action = 'Blocked' THEN 1 END), COUNT(CASE WHEN e.Action = 'Deployed' THEN 1 END)
FROM bi.FactAgentEvent AS e
WHERE e.DateKey BETWEEN @k0 AND @k1 AND e.Action IN ('Blocked', 'Unblocked', 'Deployed', 'Removed', 'AdminDeleted', 'AdminUpdated', 'Settings');", Ctx.Args())).Single();
            Kpis.Add(new("Admin actions on agents", Num(n.All), $"{n.Blocked:#,0} blocked, {n.Deployed:#,0} deployed in the period", n.Blocked > 0 ? "warn" : null));
            var at = new Table { Headers = byShown ? ["When", "Action", "Agent", "By"] : ["When", "Action", "Agent"], EmptyText = "None in the period." };
            var byName = byShown ? await NamesAsync(admin.Select(a => a.ActorUpn)) : null;
            foreach (var a in admin)
                at.Rows.Add(byShown ? [When(a.At), Said(a.Action), a.Agent ?? "(the tenant)", byName!(a.ActorUpn)] : [When(a.At), Said(a.Action), a.Agent ?? "(the tenant)"]);
            Panels.Add(new("What administrators did to agents", at.Render(),
                "From the Microsoft 365 admin center's own audit records (CopilotAgentManagement): agents blocked, unblocked, deployed to people, removed, deleted or changed, and the tenant's agent settings changed. The organization's, whatever team is chosen; the last 200 in the period." +
                (byShown ? "" : " Who did it is shown to the Agents role, with the whole organization in view."), "full"));
        }

        var neverPublished = life.Count(l => l.Created is not null && l.FirstPublished is null && l.Deleted is null);
        var t = new Table
        {
            Headers = named
                ? ["Agent", "Created", "First published", "Last published", "Publishes", "Shares", "Changes", "Deleted", "Makers", "Last changed by", "Owner"]
                : ["Agent", "Created", "First published", "Last published", "Publishes", "Shares", "Changes", "Deleted", "Makers", "Owner"],
            Numeric = new HashSet<int> { 4, 5, 6, 8 },
            EmptyText = "No authoring records yet.",
        };
        var ownerName = await NamesAsync(life.Select(l => l.Owner));   // Owners by name
        foreach (var l in life)
        {
            var name = (l.FriendlyName ?? l.LogName) is { } shown ? $"{shown} ({l.BotSchemaName})" : l.BotSchemaName;
            t.Rows.Add(named
                ? [name, l.Created, l.FirstPublished, l.LastPublished, l.Publishes, l.Shares, l.Changes, l.Deleted, l.Makers, l.LastMaker, ownerName(l.Owner)]
                : [name, l.Created, l.FirstPublished, l.LastPublished, l.Publishes, l.Shares, l.Changes, l.Deleted, l.Makers, ownerName(l.Owner)]);
        }
        CsvTable = t;
        Panels.Add(new("Every agent's lifecycle", t.Render(),
            $"{neverPublished} created and never published. All time, not only the period. Name agents and give them owners in Admin › Reference data." +
            (named ? "" : " The makers' names are shown to the Agents role."), "full", CsvUrl()));
    }
}
