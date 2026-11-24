using System.Globalization;
using System.Net;
using CopilotInsights.Data;
using CopilotInsights.Infrastructure;
using Microsoft.AspNetCore.Html;

namespace CopilotInsights.Pages.Agents;

/// <summary>
/// What each agent costs in Copilot Credits (2.6.6 on the credit estimate): its billed use, an agent used by
/// someone without a Copilot license at the time or in a Copilot Studio channel, estimated answer by answer as Licensing
/// › Credit use estimates it (<see cref="CreditData"/>), so the two pages give one figure. An agent with few users and a
/// high bill is one to restrict or retire; one whose users each spend more than a license is cheaper licensed.
/// </summary>
public sealed class CreditsModel(PageServices s, CreditData creditData) : InsightsPage(s)
{
    public override string Title => "Agents costing credits";
    public override string Persona => "Agents";
    public override string Intro => "What each agent's billed use costs in Copilot Credits a month, estimated as Licensing › Credit use estimates it. Restrict or retire an agent few people use that costs much; where its users each spend more than a license costs, licensing them is cheaper.";

    /* The keys the list sorts by; it is sorted in memory, as the estimate is worked out per agent after the read. */
    static readonly Dictionary<string, string> Sorts = new()
    {
        ["credits"] = "", ["users"] = "", ["name"] = "", ["source"] = "", ["owner"] = "", ["inter"] = "", ["billed"] = "", ["people"] = "", ["each"] = "",
    };
    static readonly Dictionary<string, string> SortLabels = new() { ["credits"] = "Most credits", ["users"] = "Most people billed", ["name"] = "Name" };

    sealed class Row
    {
        public int AgentKey { get; set; }
        public string? Owner { get; set; }
        public int People { get; set; }
    }

    protected override async Task BuildAsync()
    {
        var price = decimal.TryParse(await S.Settings.StringAsync("LicenseMonthlyCost"), NumberStyles.Number, CultureInfo.InvariantCulture, out var lp) ? lp : 30m;
        /* Each agent's owner and everyone who used it, billed or not: the estimate counts only the people billed. From
           each person's use of each agent the refresh keeps; the agents are those dash.Agent does not mark "(no agent)".
           Independent of the estimate: read side by side with it. */
        var (v, everyone) = await Queries(creditData.BuildAsync(Ctx), Db.QueryAsync<Row>($@"
SELECT ag.AgentKey, ag.Owner, COUNT(CASE WHEN {ReportContext.IsEnabled()} THEN 1 END) AS People   -- enabled accounts
FROM {Ctx.PerItem('G')} AS u {Ctx.Scope("u.UserKey")}
JOIN dash.Agent AS ag ON ag.AgentKey = u.Item
{ReportContext.EnabledJoin("u.UserKey", left: true)}
WHERE ag.AgentSource <> N'(no agent)'
GROUP BY ag.AgentKey, ag.Owner;", Ctx.Args(), timeout: 300));
        var cur = v.Currency;
        var more = everyone.GroupBy(r => r.AgentKey).ToDictionary(g => g.Key, g => g.First());

        var pg = new Paging(Request, Sorts, "credits");
        var charging = v.Agents.Where(a => a.Month.Credits > 0).ToList();
        Kpis.Add(new("Agents charging credits", Num(charging.Count), $"of {Num(v.Agents.Count)} used: billed use by people without a license, or in a Copilot Studio channel"));
        /* Totalled from people, as Credit use totals it, which the figure opens: summed by agent it came out a few
           credits apart, an agent's billed use being split by person, and each part estimated, the other way round. */
        var month = v.People.Aggregate((Credits: 0m, Cost: 0m), (s, p) => (s.Credits + p.Month.Credits, s.Cost + p.Month.Cost));
        Kpis.Add(new("Credits a month (estimate)", Num((long)Math.Round(month.Credits)),
            $"{CopilotInsights.Data.CostRules.Money(Math.Round(month.Cost), cur)} at {v.Rates.CreditPrice:#,0.00##} {cur} a credit", Href: Open("/licensing/credit-use"),
            Why: await WhyAsync(CopilotInsights.Data.CreditRules.Formula(v.Rates, cur), "CreditsPerAnswer", "CreditsPerGrounding", "CreditsPerAction", "Price:COPILOT_STUDIO_PAYG_CREDIT")));
        Kpis.Add(new("People billed", Num(v.People.Count(p => p.Use.Billed > 0 && p.Use.WithAgent > 0)), "used an agent that bills"));

        var ownerName = await NamesAsync(v.Agents.Select(a => more.TryGetValue(a.AgentKey, out var m) ? m.Owner : null));
        string? Owner(CreditAgent a) => more.TryGetValue(a.AgentKey, out var m) ? m.Owner : null;
        int All(CreditAgent a) => more.TryGetValue(a.AgentKey, out var m) ? m.People : a.People;
        decimal Each(CreditAgent a) => a.People > 0 ? a.Month.Cost / a.People : 0;

        IEnumerable<CreditAgent> shown = v.Agents;
        if (pg.Search is { } q)
            shown = shown.Where(a => a.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || (Owner(a) ?? "").Contains(q, StringComparison.OrdinalIgnoreCase));
        var ordered = (pg.SortKey switch
        {
            "name" => shown.OrderBy(a => a.Name, StringComparer.CurrentCulture),
            "source" => shown.OrderBy(a => a.Source ?? "").ThenBy(a => a.Name),
            "owner" => shown.OrderBy(a => Owner(a) ?? "").ThenBy(a => a.Name),
            "inter" => shown.OrderByDescending(a => a.Use.WithAgent).ThenBy(a => a.Name),
            "billed" => shown.OrderByDescending(a => a.Use.Billed).ThenBy(a => a.Name),
            "people" => shown.OrderByDescending(All).ThenBy(a => a.Name),
            "users" => shown.OrderByDescending(a => a.People).ThenBy(a => a.Name),
            "each" => shown.OrderByDescending(Each).ThenBy(a => a.Name),
            _ => shown.OrderByDescending(a => a.Month.Credits).ThenBy(a => a.Name),
        }).ToList();
        if (pg.Reversed) ordered.Reverse();
        pg.Total = ordered.Count;
        var page = pg.ForExport ? ordered : ordered.Skip((pg.Page - 1) * Paging.PageSize).Take(Paging.PageSize).ToList();

        var t = new Table
        {
            Headers = ["Agent", "Built in", "Owner", "Interactions", "Billed", "People", "People billed", "Credits a month", $"Cost a month ({cur})", $"A person billed a month ({cur})", "Suggestion"],
            Numeric = new HashSet<int> { 3, 4, 5, 6, 7, 8, 9 },
            EmptyText = "No agent was used in the period.",
            Server = new(pg, Request, ["name", "source", "owner", "inter", "billed", "people", "users", "credits", "credits", "each", null], new HashSet<int> { 3, 4, 5, 6, 7, 8, 9 }),
        };
        var betterLicensed = Open("/licensing/license-for-credits");
        foreach (var a in page)
        {
            var each = Each(a);
            /* An agent's people are a group: shown at the minimum group size or more, with what they would reveal. */
            var few = a.People > 0 && AtLeastMin(a.People) is null;
            object suggestion = a.Month.Credits == 0 ? "No credits: its users hold licenses"
                : each >= price ? (betterLicensed is null ? "Its users each cost more than a license: see Licensing › Better with a license"
                                   : new Link("Its users each cost more than a license: see Licensing › Better with a license", betterLicensed))   // A link, where the viewer may open it
                : a.People < Ctx.MinGroupSize && a.Month.Cost >= price ? "Few users, a license's worth of credits or more: restrict or retire"
                : "";
            var name = new HtmlString($"<a href=\"{WebUtility.HtmlEncode(Ctx.Url("/agents/detail", extra: $"agent={a.AgentKey}"))}\">{WebUtility.HtmlEncode(a.Name)}</a>");
            var all = All(a);
            t.Rows.Add([name, a.Source, ownerName(Owner(a)), a.Use.WithAgent, a.Use.Billed, all == 0 ? 0 : AtLeastMin(all), a.People == 0 ? 0 : AtLeastMin(a.People),
                Math.Round(a.Month.Credits), Math.Round(a.Month.Cost, 2), few ? null : Math.Round(each, 2), suggestion]);
        }
        CsvTable = t;
        Panels.Add(new("Agents", new HtmlString(pg.Controls(Request, SortLabels).Value + t.Render().Value),
            $"Billed: interactions with the agent by people who held no Copilot license at the time, or in a Copilot Studio channel; an unknown license is not taken for none. " +
            $"Credits a month: each billed answer at {v.Rates.Generative:#,0.##} credits, {v.Rates.Graph:#,0.##} more where it read work data and {v.Rates.Action:#,0.##} where it called another agent, " +
            $"scaled to 30 days, at {v.Rates.CreditPrice:#,0.00##} {cur} a credit (Admin › Targets and assumptions): the same estimate as Licensing › Credit use. It errs high. " +
            $"A person billed a month: the cost over the people billed, against the license's {price:#,0.##} {cur}. People are shown only where {Ctx.MinGroupSize} or more used an agent. " +
            "Which line of business each agent's environment is charged to is on Leadership › Credit chargeback.", "full", CsvUrl()));
    }
}