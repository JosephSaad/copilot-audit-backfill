using CopilotInsights.Charts;
using Microsoft.AspNetCore.Html;

namespace CopilotInsights.Pages.Personal;

/// <summary>
/// The employee's own view: their use only, whoever they are, against the organisation's
/// median. Nothing here is anyone else's, so it needs no role and no place in the org chart.
/// </summary>
public sealed class IndexModel(PageServices s) : InsightsPage(s)
{
    public override string Title => "My Copilot";
    public override string Persona => "My Copilot";
    public override string Intro => "Your own use of Copilot: how often, in which apps, with which agents, and when. Only you, and administrators checking access, see this page as you.";

    sealed class Totals
    {
        public int ActiveDays { get; set; }
        public long Interactions { get; set; }
        public long Prompts { get; set; }
        public long Sessions { get; set; }
        public long MultiTurn { get; set; }
        public long AgentInteractions { get; set; }
        public long WebGrounded { get; set; }
        public long Labelled { get; set; }
    }

    protected override async Task BuildAsync()
    {
        var me = Ctx.Viewer.Upn;
        if (me is null) { Notice = "Your sign-in isn't matched to a person in the directory, so there's nothing to show yet."; return; }
        var args = Ctx.Args(new { me });
        const string Mine = "JOIN bi.DimUser AS mu ON mu.UserKey = {0} AND mu.UserPrincipalName = @me";

        var t = (await Db.QueryAsync<Totals>($@"
SELECT COUNT(CASE WHEN ud.Interactions > 0 THEN 1 END) AS ActiveDays, ISNULL(SUM(CAST(ud.Interactions AS BIGINT)), 0) AS Interactions,
       ISNULL(SUM(CAST(ud.Prompts AS BIGINT)), 0) AS Prompts, ISNULL(SUM(CAST(ud.Sessions AS BIGINT)), 0) AS Sessions,
       ISNULL(SUM(CAST(ud.MultiTurnSessions AS BIGINT)), 0) AS MultiTurn, ISNULL(SUM(CAST(ud.AgentInteractions AS BIGINT)), 0) AS AgentInteractions,
       ISNULL(SUM(CAST(ud.WebGrounded AS BIGINT)), 0) AS WebGrounded, ISNULL(SUM(CAST(ud.LabelledContent AS BIGINT)), 0) AS Labelled
FROM dash.UserDay AS ud {string.Format(Mine, "ud.UserKey")}
WHERE ud.DateKey BETWEEN @k0 AND @k1;", args)).FirstOrDefault() ?? new Totals();

        /* The organisation's median, over everyone who used it in the period: an aggregate, shown only for a group big enough to hide anyone in it. */
        var peers = (await Db.QueryAsync<(int People, double? MedianDays, double? MedianInteractions)>(@"
WITH p AS (SELECT UserKey, COUNT(*) AS Days, SUM(CAST(Interactions AS BIGINT)) AS N
           FROM dash.UserDay WHERE DateKey BETWEEN @k0 AND @k1 AND Interactions > 0 GROUP BY UserKey)
SELECT TOP (1) COUNT(*) OVER () AS People,
       PERCENTILE_CONT(0.5) WITHIN GROUP (ORDER BY Days) OVER () AS MedianDays,
       PERCENTILE_CONT(0.5) WITHIN GROUP (ORDER BY N) OVER () AS MedianInteractions
FROM p;", args)).FirstOrDefault();
        var showPeers = peers.People >= Ctx.MinGroupSize;

        var licensed = await Db.ScalarAsync<int>($@"
SELECT COUNT(*) FROM bi.FactLicensedDay AS l {string.Format(Mine, "l.UserKey")} WHERE l.DateKey = @k1;", args) > 0;

        Kpis.Add(new("Days you used Copilot", Num(t.ActiveDays), showPeers ? $"the organisation's median is {Num(peers.MedianDays)}" : $"of {Ctx.Days}"));
        Kpis.Add(new("Interactions", Num(t.Interactions), showPeers ? $"median {Num(peers.MedianInteractions)}" : null));
        Kpis.Add(new("Conversations", Num(t.Sessions), t.Sessions > 0 ? $"{Pct(Ratio(t.MultiTurn, t.Sessions))} went back and forth" : null));
        Kpis.Add(new("License", licensed ? "Microsoft 365 Copilot" : "Copilot Chat (free)", licensed ? null : "ask your manager if you use it most days"));
        if (t.Interactions == 0)
        {
            Notice = $"No Copilot use recorded for you from {Ctx.PeriodLabel}. Records arrive a day or two after use.";
            return;
        }

        var trend = await Db.QueryAsync<(DateTime Bucket, long N)>($@"
SELECT {Ctx.BucketSql("d")}, SUM(CAST(ud.Interactions AS BIGINT))
FROM dash.UserDay AS ud {string.Format(Mine, "ud.UserKey")}
JOIN bi.DimDate AS d ON d.DateKey = ud.DateKey
WHERE ud.DateKey BETWEEN @k0 AND @k1
GROUP BY {Ctx.BucketSql("d")};", args);
        var buckets = Buckets();
        Panels.Add(new("Your interactions over time",
            Chart.Columns(Labels(), [new Series("Interactions", buckets.Select(b => (double?)trend.Where(x => x.Bucket == b.Key).Sum(x => x.N)).ToList(), 1)], Fmt.Count, "Your interactions"),
            TrendNote));

        var apps = await Db.QueryAsync<(string App, long N)>($@"
SELECT a.App, COUNT_BIG(*)
FROM bi.FactInteraction AS f {string.Format(Mine, "f.UserKey")}
JOIN bi.DimApp AS a ON a.AppKey = f.AppKey
WHERE f.DateKey BETWEEN @k0 AND @k1
GROUP BY a.App ORDER BY 2 DESC;", args);
        Panels.Add(new("Where you use it", Chart.Bars(apps.Select(a => new Bar(a.App, a.N)).ToList(), Fmt.Count, "Your interactions by app"),
            apps.Count == 1 ? "All in one app. Copilot also works in Word, Excel, PowerPoint, Outlook and Teams." : null, "half"));

        var agents = await Db.QueryAsync<(string Agent, long N, DateTime Last)>($@"
SELECT a.AgentName, COUNT_BIG(*), MAX(d.[Date])
FROM bi.FactInteraction AS f {string.Format(Mine, "f.UserKey")}
JOIN dash.Agent AS a ON a.AgentKey = f.AgentKey AND a.AgentSource <> N'(no agent)'
JOIN bi.DimDate AS d ON d.DateKey = f.DateKey
WHERE f.DateKey BETWEEN @k0 AND @k1
GROUP BY a.AgentKey, a.AgentName ORDER BY 2 DESC;", args);
        var at = new Table { Headers = ["Agent", "Interactions", "Last used"], Numeric = new HashSet<int> { 1 }, EmptyText = "You haven't used an agent in this period." };
        foreach (var a in agents) at.Rows.Add([a.Agent, a.N, a.Last]);
        Panels.Add(new("Agents you use", at.Render(), $"{Num(t.AgentInteractions)} of your interactions were with an agent.", "half"));

        var grid = await Db.QueryAsync<(int Dow, int Hour, long N)>($@"
SELECT d.DayOfWeekNumber, f.HourOfDay, COUNT_BIG(*)
FROM bi.FactInteraction AS f {string.Format(Mine, "f.UserKey")}
JOIN bi.DimDate AS d ON d.DateKey = f.DateKey
WHERE f.DateKey BETWEEN @k0 AND @k1
GROUP BY d.DayOfWeekNumber, f.HourOfDay;", args);
        string[] days = ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"];
        var cells = new double?[7, 24];
        foreach (var g in grid) cells[g.Dow - 1, g.Hour] = g.N;
        Panels.Add(new("When you use it", Chart.Heatmap(days, Enumerable.Range(0, 24).Select(h => h.ToString("00")).ToList(), cells, Fmt.Count, "Your interactions by weekday and hour"),
            "Darker is busier, in the reporting time zone."));

        var tips = new List<string>();
        if (t.Sessions > 0 && Ratio(t.MultiTurn, t.Sessions) < 0.3) tips.Add("Most of your conversations are a single prompt. Following up (\"shorter\", \"as a table\", \"what did I miss?\") usually gets a better answer than starting again.");
        if (apps.Count <= 2) tips.Add("You use Copilot in one or two apps. Try it where you spend the most time: summarising a Teams meeting, drafting in Word, or analysing a sheet in Excel.");
        if (t.AgentInteractions == 0) tips.Add("You haven't used an agent. Your organisation's agents answer questions from its own content; look for them in Copilot Chat.");
        if (t.WebGrounded == 0) tips.Add("None of your prompts used the web. For current events or public information, Copilot can search the web when that is enabled.");
        if (tips.Count > 0)
            Panels.Add(new("Ideas to get more from it",
                new HtmlString("<ul class=\"tips\">" + string.Concat(tips.Select(x => $"<li>{E(x)}</li>")) + "</ul>"), "From your own patterns, not anyone else's."));
    }
}
