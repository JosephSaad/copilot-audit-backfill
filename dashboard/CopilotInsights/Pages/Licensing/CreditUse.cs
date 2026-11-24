using System.Globalization;
using System.Net;
using CopilotInsights.Charts;
using CopilotInsights.Data;
using CopilotInsights.Infrastructure;
using Microsoft.AspNetCore.Html;

namespace CopilotInsights.Pages.Licensing;

/// <summary>
/// Copilot Credits, and tokens, by person: an estimate, since Microsoft reports neither per person. Every agent
/// is counted (Copilot Studio's, Copilot Studio lite's in Microsoft 365 Copilot, SharePoint's), and every use that bills
/// credits: an agent without a license, or in a Copilot Studio channel. For a credit policy: how the credits spread across
/// people, who would reach a cap a month, and the agents and departments behind them. The rules are <see cref="CreditRules"/>.
/// </summary>
public sealed class CreditUseModel(PageServices s, CreditData credits) : InsightsPage(s)
{
    public override string Title => "Credit use";
    public override string Persona => "Licensing";
    public override string? RequiredRole => Roles.Licensing;
    public override string Intro => "An estimate of the Copilot Credits each person's use costs a month, and of the tokens behind it. Microsoft reports neither per person, so both are worked out from the audit log, and \"a month\" is the period's use brought to 30 days. "
        + "Each agent answer billed to the organization (an agent used without a Microsoft 365 Copilot license, or in a Copilot Studio channel) is counted at the credits set in Admin › Targets and assumptions, more where it read work data or called another agent. "
        + "A licensed person's agents inside Microsoft 365 Copilot, and Copilot Chat without an agent, cost no credits. Tokens are a rough figure for sizing, not a measurement.";

    static readonly int[] Caps = [0, 250, 500, 1000, 2500, 5000];
    static readonly Dictionary<string, string> Sorts = new() { ["credits"] = "credits", ["name"] = "name", ["dept"] = "dept", ["tokens"] = "tokens", ["inter"] = "inter", ["upn"] = "upn" };
    static readonly Dictionary<string, string> SortLabels = new() { ["credits"] = "Most credits", ["tokens"] = "Most tokens", ["name"] = "Name", ["dept"] = "Department" };

    /// <summary>
    /// What the page works out over everyone before the viewer's choices: the billed people, most credits first,
    /// the totals and the spread. Kept once worked out (see <see cref="Db.KeptAsync"/>): over the 210,000 people of 120
    /// million interactions, scanning and sorting them again took half a second each visit. Shared: read, never changed.
    /// </summary>
    /* People and Billed: accounts enabled today only (3.1.3), so the counts, the spread, the cap and the list are of
       them; Total, the use by kind and each department's credits are everyone's, a disabled or departed person's use included.
       EnabledCredits: the enabled people's credits, the base of the top tenth's share and of a department's credits a person. */
    sealed record Prep(CreditView V, List<CreditPerson> People, List<CreditPerson> Billed, CreditEstimate Total, decimal EnabledCredits, List<decimal> Sorted, decimal Top,
                       long All, long Agent, long Bill, long Work, long Calls,
                       List<int> Bands, Dictionary<int, (int Over, decimal Above)> Caps, decimal LicenseCredits, int OverLicense,
                       List<(string Dept, int N, decimal Credits, decimal Cost, long Tokens, decimal EnabledCredits)> Depts);

    static readonly (decimal From, decimal To, string Label)[] BandsOf =
        [(0.0001m, 100, "Under 100"), (100, 500, "100 to 500"), (500, 1000, "500 to 1,000"), (1000, 2500, "1,000 to 2,500"), (2500, 5000, "2,500 to 5,000"), (5000, decimal.MaxValue, "5,000 or more")];

    async Task<Prep> PrepAsync() => await Db.KeptAsync("CreditUse.Prep", Ctx, async () =>
    {
        var v = await credits.BuildAsync(Ctx);
        /* The estimate's people are the scope's already, each marked enabled as bi.DimUser has it: reading the scope's
           enabled accounts again, after the estimate and on its own, gave the same set and only added a read. */
        var enabled = v.People.Where(p => p.Enabled).Select(p => p.UserKey).ToHashSet();
        var people = v.People.Where(p => enabled.Contains(p.UserKey)).ToList();
        var allBilled = v.People.Where(p => p.Month.Credits > 0).ToList();
        var billed = people.Where(p => p.Month.Credits > 0).OrderByDescending(p => p.Month.Credits).ThenBy(p => p.Name ?? p.Upn).ToList();
        var total = v.People.Aggregate(new CreditEstimate(0, 0, 0), (a, p) => new(a.Credits + p.Month.Credits, a.Cost + p.Month.Cost, a.Tokens + p.Month.Tokens));
        var enabledCredits = billed.Sum(p => p.Month.Credits);
        var sorted = billed.Select(p => p.Month.Credits).Reverse().ToList();
        var top = billed.Take(Math.Max(1, billed.Count / 10)).Sum(p => p.Month.Credits);
        long all = 0, agent = 0, bill = 0, work = 0, calls = 0;
        foreach (var p in v.People) { all += p.Use.Interactions; agent += p.Use.WithAgent; bill += p.Use.Billed; work += p.Use.BilledWork; calls += p.Use.BilledActions; }
        var bands = BandsOf.Select(b => billed.Count(p => p.Month.Credits >= b.From && p.Month.Credits < b.To)).ToList();
        var caps = Caps.Where(x => x > 0).ToDictionary(x => x, x => CreditRules.Cap(billed.Select(p => p.Month.Credits), x));
        var licensePrice = decimal.TryParse(await S.Settings.StringAsync("LicenseMonthlyCost"), NumberStyles.Number, CultureInfo.InvariantCulture, out var lp) ? lp : 30m;
        var licenseCredits = v.Rates.CreditPrice > 0 ? licensePrice / v.Rates.CreditPrice : 0;
        var overLicense = billed.Count(p => !p.Licensed && p.Month.Credits >= licenseCredits && licenseCredits > 0);
        var depts = allBilled.GroupBy(p => p.Department ?? "(none)")
            .Select(g => (Dept: g.Key, N: g.Count(p => enabled.Contains(p.UserKey)), Credits: g.Sum(p => p.Month.Credits), Cost: g.Sum(p => p.Month.Cost), Tokens: g.Sum(p => p.Month.Tokens),
                          EnabledCredits: g.Where(p => enabled.Contains(p.UserKey)).Sum(p => p.Month.Credits)))
            .OrderByDescending(g => g.Credits).Take(15).ToList();
        return new Prep(v, people, billed, total, enabledCredits, sorted, top, all, agent, bill, work, calls, bands, caps, licenseCredits, overLicense, depts);
    });

    protected override async Task BuildAsync()
    {
        var prep = await PrepAsync();
        var v = prep.V;
        var cur = v.Currency;
        var r = v.Rates;
        if (v.People.Count == 0) { Notice = "Nobody here used Copilot in the period."; return; }
        var billed = prep.Billed;
        var total = prep.Total;
        var sorted = prep.Sorted;
        decimal Pct(double q) => sorted.Count == 0 ? 0 : sorted[Math.Clamp((int)Math.Ceiling(q * sorted.Count) - 1, 0, sorted.Count - 1)];
        var top = prep.Top;
        string N(decimal x) => x.ToString("#,0", CultureInfo.InvariantCulture);
        string M(decimal x) => CostRules.Money(Math.Round(x, x >= 100 ? 0 : 2), cur);

        Kpis.Add(new("Credits a month (estimate)", N(total.Credits), $"{M(total.Cost)} at {r.CreditPrice.ToString("0.###", CultureInfo.InvariantCulture)} {cur} a credit",
            Why: await WhyAsync(CreditRules.Formula(r, cur), "CreditsPerAnswer", "CreditsPerGrounding", "CreditsPerAction", "Price:COPILOT_STUDIO_PAYG_CREDIT")));
        Kpis.Add(new("People using credits", Num(billed.Count), $"whose use billed credits, of the {prep.People.Count:#,0} enabled accounts that used Copilot in the period"));
        Kpis.Add(new("Credits a person a month", billed.Count == 0 ? "—" : N(Pct(0.5)), billed.Count == 0 ? "nobody billed" : $"the median of the people billed; the top tenth use {N(Pct(0.9))} or more"));
        Kpis.Add(new("Tokens a month (rough)", CreditRules.Tokens(total.Tokens), "every interaction, at the token sizes set in Admin",
            Why: await WhyAsync(CreditRules.TokenFormula(r), "TokensPerTurn", "TokensPerResource", "TokensPerAnswer")));
        if (prep.EnabledCredits > 0) Kpis.Add(new("Top tenth's share", $"{top / prep.EnabledCredits:P0}", "of enabled accounts' credits, used by the tenth of billed people who use most"));
        /* The estimate beside what was billed (the admin center's consumption export), where it is loaded: the rates in
           Admin are right when the two come near each other. */
        if (Ctx.Root is null && await Db.ScalarAsync<int>("SELECT CASE WHEN OBJECT_ID(N'audit.PowerPlatformCreditDay', N'U') IS NULL THEN 0 ELSE 1 END;") == 1)
        {
            var billedNow = (await Db.QueryAsync<(decimal Credits, int Days)>(@"
WITH last AS (SELECT MAX([Day]) AS D FROM audit.PowerPlatformCreditDay WHERE Product = 'Copilot Studio')
SELECT ISNULL(SUM(Credits), 0), COUNT(DISTINCT [Day]) FROM audit.PowerPlatformCreditDay
WHERE Product = 'Copilot Studio' AND [Day] > DATEADD(DAY, -30, (SELECT D FROM last));")).Single();
            if (billedNow.Days > 0)
            {
                var billedMonth = CostRules.Monthly(billedNow.Credits, billedNow.Days);
                /* What was billed first, and the estimate as the share of it traced to people. */
                Kpis.Insert(0, new("Billed by the admin center", N(billedMonth), billedMonth > 0 ? $"credits a month, from the last 30 days loaded; the estimate below traces {total.Credits / billedMonth:P0} of it to people" : "credits a month, from the last 30 days loaded",
                    Href: Open("/leadership/costs", extra: $"area={CostAreas.Slot(CostAreas.Studio)}"),
                    Why: await WhyAsync("Copilot Studio's credits in the Power Platform admin center's consumption report, over the last 30 days loaded, as a month. Leadership › Costs and savings shows what they cost.")));
            }
        }

        /* The use by kind: what bills and what does not, and why. */
        var (all, agent, bill, work, calls) = (prep.All, prep.Agent, prep.Bill, prep.Work, prep.Calls);
        var scale = 30m / Math.Max(1, v.Days);
        var kt = new Table { Headers = ["Use", "Interactions a month", "Credits each", "Credits a month"], Numeric = new HashSet<int> { 1, 2, 3 }, Sortable = false };
        kt.Rows.Add(["Copilot without an agent (Copilot Chat, or Copilot in the apps)", Math.Round((all - agent) * scale), 0, 0]);
        kt.Rows.Add(["Agents a licensed person used inside Microsoft 365 Copilot", Math.Round((agent - bill) * scale), 0, 0]);
        kt.Rows.Add(["Agent answers billed, without work data", Math.Round((bill - work) * scale), r.Generative, Math.Round((bill - work) * r.Generative * scale)]);
        kt.Rows.Add(["Agent answers billed, grounded in work data", Math.Round(work * scale), r.Generative + r.Graph, Math.Round(work * (r.Generative + r.Graph) * scale)]);
        kt.Rows.Add(["Of those, an agent calling another (an action)", Math.Round(calls * scale), r.Action, Math.Round(calls * r.Action * scale)]);
        Panels.Add(new("What bills credits", kt.Render(),
            "Billed: an agent used by someone without a Microsoft 365 Copilot license at the time, or in a Copilot Studio channel (Teams, a website), where a license does not cover it. Grounded: the answer read files, mail or pages, or Copilot marked it work mode. " +
            "Credits each are the rates set in Admin; the list rates are 2 for a generative answer, 10 more for Graph grounding, 5 for an action. A classic, scripted answer costs less, so this errs high. " +
            "What each line of business is charged for its environments' credits is on Leadership › Credit chargeback; a prepaid commitment's draw on Forecasting › Credit commitment.", "half"));

        /* How the credits spread: people by band. */
        var bars = BandsOf.Select((b, i) => new Bar(b.Label, prep.Bands[i])).ToList();
        Panels.Add(new("People by credits a month", Chart.Bars(bars, Fmt.Count, "People by estimated Copilot Credits a month"),
            "People with an enabled account whose use billed credits in the period, by their estimated credits a month. A credit policy usually caps the long tail on the right, or licenses it.", "half"));

        /* A cap a person a month, for the policy. */
        /* Unless one is chosen, the smallest cap offered at or above the 90th percentile: only the long tail reaches it. */
        var p90 = Pct(0.9);
        var cap = int.TryParse(Request.Query["cap"], out var cp) && Caps.Contains(cp) ? cp : Caps.Where(x => x > 0 && x >= p90).DefaultIfEmpty(Caps[^1]).First();
        var (over, above) = prep.Caps.TryGetValue(cap, out var kc) ? kc : CreditRules.Cap(billed.Select(p => p.Month.Credits), cap);
        var licenseCredits = prep.LicenseCredits;
        var overLicense = prep.OverLicense;
        var capLinks = string.Join(" ", Caps.Where(x => x > 0).Select(x => $"<a href=\"{WebUtility.HtmlEncode(Ctx.Url(Request.Path, extra: $"cap={x}"))}\"{(x == cap ? " class=\"on\"" : "")}>{x:#,0}</a>"));
        Panels.Add(new($"A cap of {cap:#,0} credits a person a month", new HtmlString(
            $"<p class=\"sub\">Try a cap: {capLinks}</p>" +
            $"<div class=\"facts\"><div><span class=\"fl\">People who would reach it</span><b>{over:#,0}</b><p>of the {billed.Count:#,0} people billed</p></div>" +
            $"<div><span class=\"fl\">Credits above it a month</span><b>{N(above)}</b><p>{M(above * r.CreditPrice)}: refused by the cap, or paid by a pack or a license</p></div>" +
            $"<div><span class=\"fl\">Unlicensed people costing more than a license</span><b>{overLicense:#,0}</b><p>{N(licenseCredits)} credits or more a month: cheaper licensed, see {(Open("/licensing/license-for-credits") is { } lfc ? $"<a href=\"{WebUtility.HtmlEncode(lfc)}\">Better with a license</a>" : "Better with a license")}</p></div></div>"),
            "Copilot Credits can be capped per person in the Power Platform admin center (or limited by environment and agent). This shows what a cap would touch, from the estimate.", "half"));

        /* By department. */
        var dt = new Table { Headers = ["Department", "People billed", "Credits a month", $"Cost a month ({cur})", "Credits a person", "Tokens a month (rough)"], Numeric = new HashSet<int> { 1, 2, 3, 4 } };
        foreach (var g in prep.Depts)
            dt.Rows.Add([new Link(g.Dept, Ctx.Url(Request.Path, extra: $"dept={Uri.EscapeDataString(g.Dept)}")), AtLeastMin(g.N), Math.Round(g.Credits), Math.Round(g.Cost), g.N >= Ctx.MinGroupSize ? Math.Round(g.EnabledCredits / g.N) : null,
                CreditRules.Tokens(g.Tokens)]);
        Panels.Add(new("By department", dt.Render(), "The fifteen departments billed most. People billed and credits a person count enabled accounts; the credits, cost and tokens are everyone's, disabled and departed people's use included. A credit a person is shown only where the minimum group size or more were billed.", "half"));

        /* By agent. */
        var at = new Table { Headers = ["Agent", "Made with", "People billed", "Billed answers a month", "Credits a month", $"Cost a month ({cur})"], Numeric = new HashSet<int> { 2, 3, 4, 5 } };
        foreach (var a in v.Agents.Where(a => a.Month.Credits > 0).OrderByDescending(a => a.Month.Credits).Take(15))
            at.Rows.Add([new HtmlString($"<a href=\"{WebUtility.HtmlEncode(Ctx.Url("/agents/detail", extra: $"agent={a.AgentKey}"))}\">{WebUtility.HtmlEncode(a.Name)}</a>"), a.Source,
                AtLeastMin(a.People), Math.Round(a.Use.Billed * scale), Math.Round(a.Month.Credits), Math.Round(a.Month.Cost)]);
        Panels.Add(new("By agent", at.Rows.Count == 0 ? new HtmlString("<p class=\"muted\">No agent billed credits in the period.</p>") : at.Render(),
            "The fifteen agents that billed most: the ones a policy or an environment limit acts on first.", "half"));

        /* The people. */
        var pg = new Paging(Request, Sorts, "credits");
        /* A department chosen (?dept=, from its row above): its people only. */
        var dept = Request.Query["dept"].FirstOrDefault() is { Length: > 0 } dn ? dn : null;
        IEnumerable<CreditPerson> shown = prep.People.Where(p => p.Month.Credits > 0 || pg.SortKey == "tokens");
        if (dept is not null) shown = shown.Where(p => (p.Department ?? "(none)") == dept);
        if (pg.Search is { } q)
            shown = shown.Where(p => (p.Name ?? "").Contains(q, StringComparison.OrdinalIgnoreCase) || p.Upn.Contains(q, StringComparison.OrdinalIgnoreCase) || (p.Department ?? "").Contains(q, StringComparison.OrdinalIgnoreCase));
        /* The default view (most credits, no department, no search) is the kept list itself; anything else is ordered here. */
        var plain = pg.SortKey == "credits" && dept is null && pg.Search is null;
        var ordered = plain ? (pg.Reversed ? Enumerable.Reverse(billed).ToList() : billed) : (pg.SortKey switch
        {
            "name" => shown.OrderBy(p => p.Name ?? p.Upn, StringComparer.CurrentCulture),
            "dept" => shown.OrderBy(p => p.Department ?? "", StringComparer.CurrentCulture).ThenBy(p => p.Name ?? p.Upn),
            "tokens" => shown.OrderByDescending(p => p.Month.Tokens),
            "inter" => shown.OrderByDescending(p => p.Use.Interactions),
            "upn" => shown.OrderBy(p => p.Upn, StringComparer.OrdinalIgnoreCase),
            _ => shown.OrderByDescending(p => p.Month.Credits).ThenBy(p => p.Name ?? p.Upn),
        }).ToList();
        if (!plain && pg.Reversed) ordered.Reverse();
        pg.Total = ordered.Count;
        var page = pg.ForExport ? ordered : ordered.Skip((pg.Page - 1) * Paging.PageSize).Take(Paging.PageSize).ToList();
        var t = new Table
        {
            Headers = ["Name", "Sign-in", "Department", "Licensed", "Interactions a month", "Billed agent answers a month", "Credits a month", $"Cost a month ({cur})", "Tokens a month (rough)", $"Over a cap of {cap:#,0}"],
            Numeric = new HashSet<int> { 4, 5, 6, 7 },
            EmptyText = "Nobody's use billed credits in the period.",
            Server = new(pg, Request, ["name", "upn", "dept", null, "inter", null, "credits", "credits", "tokens", null], new HashSet<int> { 4, 6, 7, 8 }),
        };
        foreach (var p in page)
            t.Rows.Add([p.Name, p.Upn, p.Department, p.Licensed ? "Yes" : "No", Math.Round(p.Use.Interactions * scale), Math.Round(p.Use.Billed * scale), Math.Round(p.Month.Credits),
                Math.Round(p.Month.Cost, 2), CreditRules.Tokens(p.Month.Tokens), p.Month.Credits > cap ? "Yes" : ""]);
        CsvTable = t;
        Panels.Add(new(dept is null ? "People" : $"People in {dept}", new HtmlString((dept is null ? "" : $"<p class=\"sub\"><a href=\"{WebUtility.HtmlEncode(Ctx.Url(Request.Path))}\">Every department</a></p>") + pg.Controls(Request, SortLabels).Value + t.Render().Value),
            $"Each person with an enabled account whose use billed credits, most first, brought to a month from the {v.Days} days the data covers. Tokens: every interaction at {r.TokensPerTurn:#,0} a turn, {r.TokensPerResource:#,0} a resource read and {r.TokensPerAnswer:#,0} an answer (Admin), a rough figure for sizing. " +
            "Sort by tokens to see everyone, billed or not. Disabled and departed people are not listed; their credits stay in the totals above.", "full", CsvUrl()));
    }
}
