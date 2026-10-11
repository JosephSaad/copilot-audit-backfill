using System.Globalization;
using System.Net;
using CopilotInsights.Infrastructure;
using Microsoft.AspNetCore.Html;

namespace CopilotInsights.Pages.Agents;

/// <summary>
/// What each agent costs in Copilot Credits (2.4.3): its use by people who held no Copilot license at the
/// time, which pay-as-you-go charges for, at the price in Admin > Access. An agent with few users and a
/// high bill is one to restrict or retire; one whose users each spend more than a license is cheaper licensed.
/// </summary>
public sealed class CreditsModel(PageServices s) : InsightsPage(s)
{
    public override string Title => "Agents costing credits";
    public override string Persona => "Agents";
    public override string Intro => "What each agent's use by people without a Copilot license costs in Copilot Credits a month. Restrict or retire an agent few people use that costs much; where its users each spend more than a license costs, licensing them is cheaper.";

    static readonly Dictionary<string, string> Sorts = new()
    {
        ["credits"] = "x.U DESC, x.AgentName", ["users"] = "x.UPeople DESC, x.AgentName", ["name"] = "x.AgentName",
        ["source"] = "x.AgentSource, x.AgentName, x.AgentKey", ["owner"] = "x.Owner, x.AgentName, x.AgentKey", ["inter"] = "x.I DESC, x.AgentName, x.AgentKey",
        ["people"] = "x.People DESC, x.AgentName, x.AgentKey", ["each"] = "CASE WHEN x.UPeople > 0 THEN x.U * 1.0 / x.UPeople ELSE 0 END DESC, x.AgentName, x.AgentKey",
    };
    static readonly Dictionary<string, string> SortLabels = new() { ["credits"] = "Most credits", ["users"] = "Most unlicensed users", ["name"] = "Name" };

    sealed class Row
    {
        public int AgentKey { get; set; }
        public string AgentName { get; set; } = "";
        public string? AgentSource { get; set; }
        public string? Owner { get; set; }
        public long I { get; set; }
        public long U { get; set; }
        public int People { get; set; }
        public int UPeople { get; set; }
        public int TotalRows { get; set; }
    }

    protected override async Task BuildAsync()
    {
        static decimal Dec(string? v, decimal fallback) => decimal.TryParse(v, NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? d : fallback;
        var price = Dec(await S.Settings.StringAsync("LicenseMonthlyCost"), 30m);
        var rate = Dec(await S.Settings.StringAsync("PaygInteractionCost"), 0.12m);
        var cur = await S.Settings.StringAsync("LicenseCurrency") ?? "USD";
        /* A month is scaled from the days the data covers, not the period's (2.4.12, see OpsReports.DaysCovered). */
        var days = CopilotInsights.Data.OpsReports.DaysCovered(Ctx, await CopilotInsights.Data.OpsReports.FirstDayAsync(Db));
        decimal Monthly(long n) => n * rate * 30m / days;

        var pg = new Paging(Request, Sorts, "credits");
        /* One row per agent, person and whether they were licensed at the time, then counted: the distinct people come
           from a few hundred thousand rows rather than the fact's millions. The figures above the list are every agent's,
           read on their own (2.4.12): taken from the page's first row, they were 0 when a search matched no agent. */
        var (rows, all) = await Db.QueryTwoAsync<Row, (int Charging, long AllU)>($@"
WITH g AS (SELECT f.AgentKey, f.UserKey, CAST(CASE WHEN f.LicenseStatusKey = 1 THEN 1 ELSE 0 END AS BIT) AS Lic, COUNT_BIG(*) AS N
           FROM bi.FactInteraction AS f {Ctx.Scope("f.UserKey")}
           WHERE f.DateKey BETWEEN @k0 AND @k1
           GROUP BY f.AgentKey, f.UserKey, CAST(CASE WHEN f.LicenseStatusKey = 1 THEN 1 ELSE 0 END AS BIT)),
a AS (SELECT g.AgentKey, SUM(g.N) AS I, SUM(CASE WHEN g.Lic = 0 THEN g.N ELSE 0 END) AS U,
             COUNT(DISTINCT g.UserKey) AS People, COUNT(DISTINCT CASE WHEN g.Lic = 0 THEN g.UserKey END) AS UPeople
      FROM g GROUP BY g.AgentKey),
x AS (SELECT ag.AgentKey, ag.AgentName, ag.AgentSource, ag.Owner, a.I, a.U, a.People, a.UPeople
      FROM a JOIN dash.Agent AS ag ON ag.AgentKey = a.AgentKey
      WHERE ag.AgentSource <> N'(no agent)')
SELECT x.* INTO #x FROM x;
SELECT x.*, COUNT(*) OVER () AS TotalRows FROM #x AS x
WHERE (@q IS NULL OR x.AgentName LIKE @q OR x.Owner LIKE @q)
{pg.OrderAndPage()};
SELECT COUNT(CASE WHEN x.U > 0 THEN 1 END), ISNULL(SUM(x.U), 0) FROM #x AS x;", Ctx.Args(new { q = pg.Like }), timeout: 300);
        pg.Total = rows.FirstOrDefault()?.TotalRows ?? 0;
        var (charging, allU) = all.Single();

        Kpis.Add(new("Agents charging credits", Num(charging), "used by people without a license"));
        Kpis.Add(new($"Credits a month ({cur})", Num((long)Monthly(allU)), $"at {rate:#,0.00##} {cur} an interaction"));

        var t = new Table
        {
            Headers = ["Agent", "Built in", "Owner", "Interactions", "Interactions without a license", "People", "People without a license", $"Credits a month ({cur})", $"Credits a person a month ({cur})", "Suggestion"],
            Numeric = new HashSet<int> { 3, 4, 5, 6, 7, 8 },
            EmptyText = "No agent was used in the period.",
            /* Without a license (interactions) and Credits a month are the same count, so both sort by credits. */
            Server = new(pg, Request, ["name", "source", "owner", "inter", "credits", "people", "users", "credits", "each", null], new HashSet<int> { 3, 4, 5, 6, 7, 8 }),
        };
        var ownerName = await NamesAsync(rows.Select(r => r.Owner));   // 2.5.8: owners by name
        foreach (var r in rows)
        {
            var credits = Monthly(r.U);
            var each = r.UPeople > 0 ? credits / r.UPeople : 0m;
            /* An agent's people are a group: shown at the minimum group size or more (2.4.12), with what they would reveal. */
            var few = r.UPeople > 0 && AtLeastMin(r.UPeople) is null;
            var suggestion = r.U == 0 ? "No credits: its users hold licenses"
                : each >= price ? "Its users each cost more than a license: see Licensing › Better with a license"
                : r.UPeople < Ctx.MinGroupSize && credits >= price ? "Few users, a license's worth of credits or more: restrict or retire"
                : "";
            var name = new HtmlString($"<a href=\"{WebUtility.HtmlEncode(Ctx.Url("/agents/detail", extra: $"agent={r.AgentKey}"))}\">{WebUtility.HtmlEncode(r.AgentName)}</a>");
            t.Rows.Add([name, r.AgentSource, ownerName(r.Owner), r.I, r.U, r.People == 0 ? 0 : AtLeastMin(r.People), r.UPeople == 0 ? 0 : AtLeastMin(r.UPeople), Math.Round(credits, 2), few ? null : Math.Round(each, 2), suggestion]);
        }
        CsvTable = t;
        Panels.Add(new("Agents", new HtmlString(pg.Controls(Request, SortLabels).Value + t.Render().Value),
            $"Without a license: interactions by people who held no Copilot license at the time; pay-as-you-go charges for these in Copilot Credits. Credits a month: those interactions, " +
            $"scaled to 30 days, at {rate:#,0.00##} {cur} each (set in Admin > Access). An agent grounded only in public data is not charged, so this errs high. " +
            $"A person a month: the credits over the people without a license who used it, against the license's {price:#,0.##} {cur}. People are shown only where {Ctx.MinGroupSize} or more used an agent.", "full", CsvUrl()));
    }
}
