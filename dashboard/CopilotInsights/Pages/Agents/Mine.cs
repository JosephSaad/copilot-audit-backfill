using System.Net;
using CopilotInsights.Charts;
using CopilotInsights.Data;
using CopilotInsights.Infrastructure;
using Microsoft.AspNetCore.Html;

namespace CopilotInsights.Pages.Agents;

/// <summary>
/// My agents: an agent builder's own agents, every day. The agents the signed-in person owns (the agent workbook's
/// owner, else the one set in Admin › Reference data, else the audit log's), how many people used each in the period,
/// whether they came back, and the agents in the agent list nobody used. The use is counted across the organization,
/// whoever the viewer is: an owner needs to see their agent's whole audience. Counts only, each at the minimum group
/// size. A whole-organization viewer may look at another owner's (?owner=).
/// </summary>
public sealed class MineModel(PageServices s, CreditData creditData) : InsightsPage(s)
{
    public override string Title => "My agents";
    public override string Persona => "Agents";
    public override bool UsesTeam => false;
    public override string Intro => "The agents you own, as their builder sees them: how many people used each in the period and across the organization, whether they come back, how use is moving, "
        + "and the agents of yours that nobody used. Counts only, each shown where enough people used it.";

    public string? Owner { get; private set; }

    sealed class Row
    {
        public int AgentKey { get; set; }
        public string AgentName { get; set; } = "";
        public string? AgentSource { get; set; }
        public DateTime FirstSeenUtc { get; set; }
        public long N { get; set; }
        public int People { get; set; }
        public int Returning { get; set; }
    }

    protected override async Task BuildAsync()
    {
        await MineAsync();
        if (Viewer.OrgWide || Viewer.AnyAdmin) await OwnersAsync();
    }

    async Task MineAsync()
    {
        var asked = Request.Query["owner"].ToString().Trim();
        if (asked.Length > 256) asked = "";
        Owner = asked.Length > 0 && (Viewer.OrgWide || Viewer.AnyAdmin) ? asked : Viewer.Upn;
        var others = Viewer.OrgWide || Viewer.AnyAdmin;
        if (string.IsNullOrWhiteSpace(Owner))
        {
            Notice = "Your sign-in name is not known to the dashboard, so it cannot tell which agents are yours" + (others ? ": pick an owner below." : ". Agents in use lists every agent with its owner.");
            return;
        }
        var args = Ctx.Args(new { owner = Owner });
        var mine = await Db.QueryAsync<(int AgentKey, string AgentName, string? AgentSource, DateTime FirstSeenUtc)>(
            "SELECT AgentKey, AgentName, AgentSource, FirstSeenUtc FROM dash.Agent WHERE Owner = @owner AND AgentSource <> N'(no agent)';", args);
        var hasInventory = await Db.ScalarAsync<int>("SELECT CASE WHEN OBJECT_ID(N'dash.AgentInventory', N'U') IS NULL THEN 0 ELSE 1 END;") == 1;
        var listed = hasInventory
            ? await Db.QueryAsync<(string Id, string? Name, int? AgentKey, DateTime? CreatedAt)>("SELECT InventoryAgentId, AgentName, AgentKey, CreatedAt FROM dash.AgentInventory WHERE OwnerUpn = @owner;", args)
            : [];
        if (mine.Count == 0 && listed.Count == 0)
        {
            Notice = $"No agent is owned by {Owner} in the audit log or the agent list. An agent's owner comes from the agent workbook (Admin › Reference data sets one it does not list).";
            return;
        }
        if (others && !PrivacyMode) Kpis.Add(new("Owner", (await NamesAsync([Owner]))(Owner) ?? Owner, Owner == Viewer.Upn ? "you" : "chosen below"));
        var keys = mine.Select(m => m.AgentKey).Concat(listed.Where(l => l.AgentKey is not null).Select(l => l.AgentKey!.Value)).Distinct().ToArray();
        var rowsTask = keys.Length == 0 ? Task.FromResult(new List<Row>()) : Db.QueryAsync<Row>($@"
WITH g AS (SELECT u.Item, SUM(CAST(u.Interactions AS BIGINT)) AS N, COUNT(CASE WHEN {ReportContext.IsEnabled()} THEN 1 END) AS People,
                  COUNT(CASE WHEN u.ActiveDays > 1 AND {ReportContext.IsEnabled()} THEN 1 END) AS Returning
           FROM {Ctx.PerItem('G')} AS u {ReportContext.EnabledJoin("u.UserKey", left: true)}
           WHERE u.Item IN @keys
           GROUP BY u.Item)
SELECT ag.AgentKey, ag.AgentName, ag.AgentSource, ag.FirstSeenUtc, g.N, g.People, g.Returning
FROM g JOIN dash.Agent AS ag ON ag.AgentKey = g.Item;", Ctx.Args(new { keys }));
        var trendTask = keys.Length == 0 ? Task.FromResult(new List<(int, DateTime, int)>()) : Db.QueryAsync<(int AgentKey, DateTime Bucket, int People)>($@"
SELECT u.Item, u.Bucket, COUNT(*) FROM {Ctx.PerItemPoint('G')} AS u {ReportContext.EnabledJoin("u.UserKey")}
WHERE u.Item IN @keys GROUP BY u.Item, u.Bucket;", Ctx.Args(new { keys }));
        /* What each agent's billed use costs in Copilot Credits, as Agents costing credits and Credit use estimate it (kept). */
        var creditsTask = keys.Length == 0 ? null : creditData.TotalsAsync(OpsReports.Window(Ctx, Ctx.End, Ctx.Days, wholeOrganization: true));   // across the organization, as the people are
        await Task.WhenAll(new Task?[] { rowsTask, trendTask, creditsTask }.OfType<Task>());
        var credits = creditsTask is null ? null : await creditsTask;
        var creditOf = credits?.Agents.Where(a => keys.Contains(a.AgentKey)).GroupBy(a => a.AgentKey).ToDictionary(g => g.Key, g => g.First().Month) ?? [];
        var rows = await rowsTask;
        var trendAt = (await trendTask).ToDictionary(t => (t.Item1, t.Item2), t => t.Item3);
        var buckets = Buckets();
        var axis = Labels();
        var used = rows.Where(r => r.N > 0).ToList();
        var usedKeys = used.Select(r => r.AgentKey).ToHashSet();
        var idleListed = listed.Where(l => l.AgentKey is null || !usedKeys.Contains(l.AgentKey.Value)).ToList();
        var people = used.Sum(r => r.People);
        var total = Math.Max(mine.Count, 0) + listed.Count(l => l.AgentKey is null || mine.All(m => m.AgentKey != l.AgentKey));
        var isNew = (Row r) => r.FirstSeenUtc.Date >= Ctx.Start;

        Kpis.Add(new(Owner == Viewer.Upn ? "Your agents" : "Their agents", Num(total), listed.Count > 0 ? $"{listed.Count:#,0} in the agent list" : "seen in the audit log", "accent"));
        Kpis.Add(new("Used in the period", Num(used.Count), $"{used.Count(isNew):#,0} of them new: first used since {Ctx.Start:d MMM}", used.Count > 0 ? "good" : null));
        Kpis.Add(new("Uses by people", AtLeastMin(people) is null ? "fewer than " + Ctx.MinGroupSize : Num(people), "each person counted once an agent: the agents' audiences added up"));
        Kpis.Add(new("Interactions", Num(used.Sum(r => r.N)), $"answered by your agents from {Ctx.Start:d MMM} to {Ctx.End:d MMM yyyy}"));
        if (credits is not null && creditOf.Values.Sum(m => m.Credits) > 0)
            Kpis.Add(new($"Copilot Credits a month (estimate)", Num((long)Math.Round(creditOf.Values.Sum(m => m.Credits))),
                $"{CostRules.Money(Math.Round(creditOf.Values.Sum(m => m.Cost)), credits.Currency)} a month, from their billed use, estimated as Credit use estimates it", "warn", Href: Open("/agents/credits")));
        if (listed.Count > 0)
            Kpis.Add(new("Not used in the period", Num(idleListed.Count), "in the agent list, with no interaction: retire them, or tell people they are there", idleListed.Count > 0 ? "warn" : "good"));

        var t = new Table
        {
            Headers = ["Agent", "Built in", "First used", "People", "Came back", "Interactions", "Interactions per person", "Credits a month (estimate)", "Trend (people)"],
            Numeric = new HashSet<int> { 3, 4, 5, 6, 7 }, EmptyText = "None of these agents was used in the period.",
        };
        double? Point(int key, DateTime b) => trendAt.TryGetValue((key, b), out var n) ? n : 0;
        foreach (var r in used.OrderByDescending(r => r.People))
        {
            var shown = AtLeastMin(r.People);
            t.Rows.Add([new Link(r.AgentName + (isNew(r) ? " (new)" : ""), Ctx.Url("/agents/detail", extra: $"agent={r.AgentKey}")), r.AgentSource, r.FirstSeenUtc.Date,
                        shown, shown is null ? null : Pct(Ratio(r.Returning, r.People)), r.N, shown is null ? null : Math.Round(r.N / (double)Math.Max(1, r.People), 1),
                        creditOf.TryGetValue(r.AgentKey, out var cr) && cr.Credits > 0 ? Math.Round(cr.Credits) : null,
                        shown is null ? null : Chart.Sparkline(axis, buckets.Select(b => Point(r.AgentKey, b.Key)).ToList(), r.AgentName)]);
        }
        CsvTable = t;
        Panels.Add(new("Used in the period", WithTrendNote(t.Render()),
            $"Across the organization. Came back: the share of its people who used it on more than one day. Credits a month: its billed use (people without a license, and Copilot Studio channels) as Agents costing credits estimates it. People are shown where {Ctx.MinGroupSize} or more used it. Select an agent for its detail.", "full", CsvUrl()));
        if (used.Count > 0)
            Panels.Add(new("People by agent", Chart.Bars(used.Where(r => AtLeastMin(r.People) is not null).OrderByDescending(r => r.People).Take(12)
                    .Select((r, i) => new Bar(r.AgentName, r.People, Ctx.Url("/agents/detail", extra: $"agent={r.AgentKey}"), Slot: i % 8 + 1)).ToList(), Fmt.Count, "People using each of your agents"),
                "Your most used agents, by the people who used them in the period.", "half"));
        if (idleListed.Count > 0)
        {
            var it = new Table { Headers = ["Agent", "Created"], EmptyText = "Every agent of yours was used." };
            foreach (var l in idleListed.OrderBy(l => l.Name)) it.Rows.Add([l.Name ?? l.Id, l.CreatedAt?.Date]);
            Panels.Add(new("Not used in the period", it.Render(), "In the agent list, owned by you, with no interaction in the period: each still holds its environment's capacity and is one more to keep secure.", "half"));
        }
    }

    /// <summary>For a whole-organization viewer or an administrator: the owners with the most agents, each a link to their page of this report.</summary>
    async Task OwnersAsync()
    {
        if (PrivacyMode) return;
        var owners = await Db.QueryAsync<(string Owner, int N)>(
            "SELECT TOP (24) Owner, COUNT(*) FROM dash.Agent WHERE Owner IS NOT NULL AND Owner <> N'' AND AgentSource <> N'(no agent)' GROUP BY Owner ORDER BY COUNT(*) DESC, Owner;");
        if (owners.Count == 0) return;
        var name = await NamesAsync(owners.Select(o => (string?)o.Owner));
        var chips = owners.Select(o => $"<a class=\"chip{(string.Equals(o.Owner, Owner, StringComparison.OrdinalIgnoreCase) ? " on" : "")}\" href=\"{WebUtility.HtmlEncode(Ctx.Url(Request.Path, extra: "owner=" + Uri.EscapeDataString(o.Owner)))}\">"
                                       + $"{WebUtility.HtmlEncode(name(o.Owner) ?? o.Owner)} <span class=\"muted\">{o.N:#,0}</span></a>");
        Panels.Add(new("Look at another owner's agents", new HtmlString("<div class=\"chips\">" + string.Join("", chips) + "</div>"),
            "The owners with the most agents, and how many each owns: whole-organization viewers and administrators may open any owner's page.", "full"));
    }
}
