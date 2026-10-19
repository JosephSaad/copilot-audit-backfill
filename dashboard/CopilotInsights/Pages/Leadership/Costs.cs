using System.Globalization;
using System.Text;
using CopilotInsights.Charts;
using CopilotInsights.Data;
using CopilotInsights.Infrastructure;
using Microsoft.AspNetCore.Html;

namespace CopilotInsights.Pages.Leadership;

/// <summary>
/// What the company spends on AI and automation, what of it can be saved, and what it returns, on one page (2.6.6):
/// Microsoft 365 Copilot, Copilot Studio's credits, Fabric with Copilot in Power BI, Power BI, Power Automate, Power
/// Apps with Dataverse, GitHub Copilot and Foundry. A month at a time, laid out as a dashboard, the most important first:
/// the headline in a sentence and five tiles, the savings ranked beside how sure they are and where the money goes, each
/// area, what holds the return back, then the trend and the sources.
/// </summary>
public sealed class CostsModel(PageServices s, CostData costs) : InsightsPage(s)
{
    public override string Title => "Costs and savings";
    public override string Persona => "Leadership";
    public override string Intro => "Every figure is a month: the licenses held now at their price, and what the other sources billed or used over their last 30 days. "
        + "Savings are what would stop being spent if the action beside each were taken. Value is the time the tools save, valued at Admin's hourly cost: "
        + "Copilot's interactions, the flows' runs and Copilot in Power BI's requests. Prices and assumptions are in Admin > Targets and assumptions.";
    public override bool UsesTeam => false;

    /// <summary>The dashboard below the heading, built here and written by Costs.cshtml after the heading and filters.</summary>
    public HtmlString? Board { get; private set; }

    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    /// <summary>The working hours in a month of one full-time person, to put the hours saved in people.</summary>
    const decimal HoursAMonth = 160;

    protected override async Task BuildAsync()
    {
        /* The company's spend is not any one team's: whole-organization viewers, and the Licensing role, see it. */
        if (!Viewer.OrgWide && !Viewer.Has(Roles.Licensing))
        {
            Notice = "This page shows the whole organization's spend on AI and automation, so it is for people who see the whole organization, or who hold the Licensing role. An administrator can grant either in Admin > Access.";
            return;
        }
        var v = await costs.BuildAsync(Ctx);
        if (v.Areas.Count == 0)
        {
            Notice = "No spend to show yet: none of the sources is loaded. Docs\\OPERATIONS.md, Costs and savings, says what each needs.";
            return;
        }
        var cur = v.Currency;
        var csv = new Table { Headers = ["Kind", "Area", "Opportunity", "Evidence", "What to do", $"Saving a month ({cur})", $"Saving a year ({cur})", $"At stake a month ({cur})", "Confidence"],
                              Numeric = new HashSet<int> { 5, 6, 7 } };
        foreach (var o in v.Opportunities) csv.Rows.Add([o.Kind.ToString(), o.Area, o.Title, o.Evidence, o.Action, Math.Round(o.Saving), Math.Round(o.Saving * 12), Math.Round(o.AtStake), o.Confidence]);
        CsvTable = csv;
        /* An area's own view (?area=<its color slot>), or the whole page, its savings narrowed to one confidence (?conf=). */
        Area = name => Ctx.Url(Request.Path, keepOrg: false, extra: $"area={CostAreas.Slot(name)}");
        var pick = int.TryParse(Request.Query["area"], out var slot) ? v.Areas.FirstOrDefault(a => CostAreas.Slot(a.Name) == slot) : null;
        var only = Request.Query["conf"].FirstOrDefault() is "High" or "Medium" or "Low" ? Request.Query["conf"].ToString() : null;
        Board = new HtmlString(pick is null ? Render(v, only) : RenderArea(v, pick));
    }

    bool Narrow => !Warming && Request.Cookies["narrow"] == "1";

    /// <summary>The address of an area's own view, the period and team kept.</summary>
    Func<string, string> Area = _ => "";

    /// <summary>A link to the page with the named detail where there is one, else to the area's own view.</summary>
    string Go(string text, string? href, string area)
    {
        /* The page with the named detail where this viewer may open it; else the area's own view; inside that view, plain text. */
        var to = href is null ? null : Whole(href);
        return to is null && area == OpenArea ? E(text) : $"<a href=\"{E(to ?? Area(area))}\">{E(text)}</a>";
    }

    /// <summary>Admin › Targets and assumptions, as a link for an administrator who may open it, as its name for anyone else.</summary>
    string AdminLink() => Whole("/admin/assumptions") is { } u ? $"<a href=\"{E(u)}\">Admin › Targets and assumptions</a>" : "Admin › Targets and assumptions";

    /// <summary>A report this viewer may open, for the whole organization: the page counts it whole, whatever the team chosen.</summary>
    string? Whole(string path, string? extra = null) => Open(path, extra, keepOrg: false);

    /// <summary>The area whose own view is shown, if any: nothing in it links to itself.</summary>
    string? OpenArea;

    string Render(CostView v, string? only = null)
    {
        var cur = v.Currency;
        var allSave = v.Opportunities.Where(o => o.Kind == OpportunityKind.Save).ToList();
        var save = only is null ? allSave : allSave.Where(o => o.Confidence == only).ToList();
        var grow = v.Opportunities.Where(o => o.Kind != OpportunityKind.Save).ToList();
        var spend = v.Spend; var saving = v.Saving; var value = v.Value;
        var after = spend - saving;
        var sb = new StringBuilder("<div class=\"cboard\">");

        /* The headline in a sentence: what is spent, what comes back, and what taking the savings would do. */
        sb.Append("<p class=\"cb-say\">").Append($"AI and automation cost <b>{M(spend, cur)}</b> a month");
        if (spend > 0 && value > 0) sb.Append($" and return an estimated <b>{Times(value / spend)}</b> that in time saved");
        sb.Append('.');
        if (saving > 0) sb.Append($" Taking the {Count(allSave.Count, "saving")} found would cut the bill by <b>{saving / spend:P0}</b> ({M(saving * 12, cur)} a year)" +
                                  (after > 0 && value > 0 ? $" and lift the return to <b>{Times(value / after)}</b>." : "."));
        if (v.Suites is { Saving: > 0 } ss) sb.Append($" A further {M(ss.Saving, cur)} a month in suite moves is worth reviewing.");
        sb.Append("</p>");

        /* Five tiles: the figure, a line on it, and a picture of it. */
        var months = v.Months.Where(m => m.ByArea.Values.Sum() > 0).ToList();
        var totals = months.Select(m => (double)m.ByArea.Values.Sum()).ToList();
        var trend = "";
        if (totals.Count >= 2 && totals[^2] > 0)
        {
            var ch = totals[^1] / totals[^2] - 1;
            trend = $"<span class=\"cb-delta\">{(ch > 0.005 ? "▲" : ch < -0.005 ? "▼" : "●")} {Math.Abs(ch):P0} in {months[^1].Month.ToString("MMM", Inv)}</span>";
        }
        sb.Append("<div class=\"cb-kpis\">");
        sb.Append(Tile($"Spend a month ({cur})", "Every area's spend a month: the licenses held now at their price, and what the other sources billed over their last 30 days. The line is the last six whole months.",
            N(spend), "spend", $"{M(spend * 12, cur)} a year across {Count(v.Areas.Count, "area")}",
            (totals.Count >= 2 ? Spark(totals, "var(--accent)", "Spend over the last six whole months") : "") + trend, ("Every area", "#areas")));
        sb.Append(Tile($"Savings found a month ({cur})", "What would stop being spent if every saving below were taken, a month. The bar is its share of the spend.",
            N(saving), "save", $"{(spend > 0 ? saving / spend : 0):P0} of the spend; {M(saving * 12, cur)} a year",
            Meter(spend > 0 ? (double)(saving / spend) : 0, "var(--good)", "Savings as a share of the spend"), ("The savings", "#savings")));
        var hours = v.HourlyCost > 0 ? value / v.HourlyCost : 0;
        sb.Append(Tile($"Value a month ({cur}, estimate)", $"The time Copilot, the flows and Copilot in Power BI save, at Admin's hourly cost of {CostRules.Money(v.HourlyCost, cur)}. People: the hours over {HoursAMonth:0} working hours a month.",
            N(value), "value", $"{hours:#,0} hours saved at {CostRules.Money(v.HourlyCost, cur)} an hour",
            hours >= HoursAMonth ? $"<span class=\"cb-people\"><b>{hours / HoursAMonth:#,0}</b> people's working time</span>" : "", (Whole("/leadership/value") is { } vu ? ("Value and time saved", vu) : null)));
        sb.Append(Tile("Return on the spend", spend > 0 && value > 0 ? $"Value a month over spend a month: every {CostRules.Money(1, cur)} spent returns {CostRules.Money(Math.Round(value / spend, 2), cur)}. The marker on the dial is the return once the savings are taken: the same value for less spend." : "Value a month over spend a month.",
            spend > 0 ? Times(value / spend) : "—", "roi", after > 0 && saving > 0 ? $"{Times(value / after)} once the savings are taken" : "value over spend",
            spend > 0 ? Gauge((double)(value / spend), after > 0 && saving > 0 ? (double)(value / after) : null) : ""));
        sb.Append(Tile($"Already saved a month ({cur})", "Copilot licenses taken back after this dashboard's exports named them, at their price a month.",
            N(v.RealizedSaving), "done", v.RealizedLicenses > 0 ? $"{Count(v.RealizedLicenses, "Copilot license")} reclaimed through this dashboard" : "nothing reclaimed through this dashboard yet", "", (Whole("/forecasting/outcomes") is { } ou ? ("License action outcomes", ou) : null)));
        sb.Append("</div>");

        /* The cards, most important first, two to a row so the page reads in a few screens: what to take (with how sure it
           is, and where the money goes), each area, what holds the return back, then the trend and the sources. */
        sb.Append("<div class=\"cb-grid\">");

        /* The savings, ranked: the three largest with their evidence and what to do, the next seven as rows, the rest on request. */
        sb.Append(Card(only is null ? $"Savings to take: {M(saving, cur)} a month" : $"{only}-confidence savings: {M(save.Sum(o => o.Saving), cur)} a month",
            "Ranked by what each saves a month. Each says what the data shows and what to do; its title opens the page with the named detail, or the area's own view. The CSV has every one, with the opportunities to grow the return.", "c8 cb-anchor",
            (only is null ? "" : $"<p class=\"cb-filter\">Showing the {E(only.ToLowerInvariant())}-confidence savings only. <a href=\"{E(Ctx.Url(Request.Path, keepOrg: false))}#savings\">Show every saving</a></p>") +
            (save.Count == 0 ? "<p class=\"sub\">Nothing found: every area the sources cover is as lean as these rules can tell.</p>" :
            "<ol class=\"cb-top\">" + string.Concat(save.Take(3).Select((o, i) => TopRow(o, i + 1, cur))) + "</ol>" + Rest(save, cur)), CsvUrl(), "savings"));

        /* Beside them: how sure each saving is, and where the money goes, the areas' shares in a ring. */
        var conf = new[] { ("High", "c-high", "The data shows it outright: a seat or a license nobody has used in 30 days."),
                           ("Medium", "c-med", "It follows from a change you make: a smaller capacity, a different license."),
                           ("Low", "c-low", "It rests on a price or a discount that your agreement may not match.") }
            .Select(c => (c.Item1, c.Item2, c.Item3, Sum: allSave.Where(o => o.Confidence == c.Item1).Sum(o => o.Saving), N: allSave.Count(o => o.Confidence == c.Item1))).ToList();
        string ConfUrl(string c) => E(Ctx.Url(Request.Path, keepOrg: false, extra: $"conf={c}")) + "#savings";
        sb.Append("<div class=\"c4 rail\">");
        sb.Append(Card("Savings by confidence", "How sure each saving is. Take the high-confidence ones first: they need no more than a decision. Select one to list its savings alone.", "",
            "<div class=\"cb-stack\" role=\"img\" aria-label=\"Savings by confidence\">" +
            string.Concat(conf.Where(c => c.Sum > 0).Select(c => $"<a class=\"{c.Item2}\" style=\"width:{Pct(c.Sum, saving)}%\" href=\"{ConfUrl(c.Item1)}\" title=\"{c.Item1}: {E(M(c.Sum, cur))}\" aria-label=\"{c.Item1}-confidence savings: {E(M(c.Sum, cur))}\"></a>")) + "</div>" +
            "<ul class=\"cb-conf\">" + string.Concat(conf.Select(c =>
                $"<li{(only == c.Item1 ? " class=\"on\"" : "")}><i class=\"sw {c.Item2}\"></i><span class=\"cf-n\"><a href=\"{ConfUrl(c.Item1)}\">{E(c.Item1)}</a>{((HtmlString)InfoTip.Icon(c.Item3, c.Item1)).Value}<small>{Count(c.N, "saving")}</small></span><b>{N(c.Sum)}</b></li>")) + "</ul>" +
            (conf[0].Sum > 0 ? $"<p class=\"cb-foot\"><b>{(saving > 0 ? conf[0].Sum / saving : 0):P0}</b> of the savings can be taken on the data alone.</p>" : "")));
        var byShare = v.Areas.Where(a => a.Spend > 0).OrderByDescending(a => a.Spend).ToList();
        sb.Append(Card("Where the money goes", "Each area's spend a month, and its share of the whole. Select an area, or its part of the ring, for its own view: what it is made of, and what can be done.", "",
            $"<div class=\"cb-donut\">{Donut(byShare, spend, cur, Area)}<ul class=\"cb-legend\">" +
            string.Concat(byShare.Select(a => $"<li><i class=\"dot\" style=\"background:var(--s{CostAreas.Slot(a.Name)})\"></i><span class=\"lg-n\">{Go(a.Name, null, a.Name)}</span>" +
                                              $"<span class=\"lg-v\">{N(a.Spend)}</span><span class=\"lg-p\">{(spend > 0 ? a.Spend / spend : 0):P0}</span></li>")) +
            "</ul></div>"));
        sb.Append("</div>");

        /* Each area side by side, across the whole row so every one reads on a line. */
        sb.Append(Card("Every area, side by side", "Each area's spend a month, the part that can be saved (the hatched end of its bar), its last six whole months, and the value its use returns. The icon beside each says how its figures are worked out.", "c12",
            Ledger(v, months), null, "areas"));

        /* Then the wide cards (what holds the return back, the trend) each beside a narrow one, in order of weight: the
           suites, Copilot in Power BI, the sources. What is left over takes a row of its own. */
        var cif = v.CopilotInFabric;
        var wide = new List<Func<string, string>>();
        var narrow = new List<Func<string, string>>();
        if (grow.Count > 0)
            wide.Add(cls => Card("Grow the return", "Spend that is not yet returning what it could: people to train, licenses to hand on, work being lost. The figure is the monthly value or spend each holds back.", cls,
                "<div class=\"cb-grow\">" + string.Concat(grow.Select(o => GrowCard(o, cur))) + "</div>"));
        if (months.Count >= 2)
        {
            var labels = months.Select(m => m.Month.ToString("MMM yyyy", Inv)).ToList();
            var series = CostAreas.All.Where(a => months.Any(m => m.ByArea.ContainsKey(a.Name)))
                .Select(a => new Series(a.Name, months.Select(m => m.ByArea.TryGetValue(a.Name, out var x) ? (double?)(double)x : null).ToList(), a.Slot)).ToList();
            var chart = Chart.Columns(labels, series, Fmt.Count, $"Spend a month by area, {cur}, the last six whole months");
            wide.Add(cls => Card($"Spend by month ({cur})", "The last six whole months by area. Copilot from the add-on's licensed days; the other licenses from the license pool's daily counts; the rest from what each source billed. A source shows from the first month it covers.",
                cls, chart is ChartSvg c ? c.Render(Narrow ? 320 : cls == "c12" ? 900 : 720) : chart.Value ?? ""));
        }
        if (v.Suites is { } su) narrow.Add(cls => SuitesCard(su, cur, cls, Open));
        if (cif is not null)
        {
            var per = cif.Requests > 0 ? cif.Cost / cif.Requests : 0;
            narrow.Add(cls => Card("Copilot in Power BI", "Copilot runs on the Fabric capacity the report's workspace is on, so its cost is its share of that capacity's. Requests come from the Capacity Metrics app's Copilot operations; value at Admin's minutes a request and hourly cost.", cls,
                "<div class=\"cif\">" +
                $"<div class=\"cif-big\"><b>{(cif.Cost > 0 ? Times(cif.Value / cif.Cost) : "—")}</b><span>return on its share of the capacity: {M(cif.Value, cur)} of value a month</span></div>" +
                "<div class=\"cif-grid\">" +
                Stat("Capacity it uses", $"{cif.Share:P1}", $"on {Count(cif.Capacities, "capacity", "capacities")}", Meter(cif.Share, "var(--s3)", "Copilot's share of the capacity")) +
                Stat("Cost a month", M(cif.Cost, cur), "its share of the capacity's cost") +
                Stat("Requests a month", cif.Requests.ToString("#,0", Inv), $"by up to {cif.Users:#,0} people a day") +
                Stat("Cost a request", CostRules.Money(Math.Round(per, 2), cur), "capacity cost over requests") +
                "</div></div>" + $"<p class=\"cb-foot\"><a href=\"{E(Area(CostAreas.Fabric))}\">Each capacity, and what it could save ›</a></p>"));
        }
        narrow.Add(cls => Card("What this is built from", "Every source is optional: an area appears once its source is loaded, and one that is not is listed here rather than shown as zero.", cls,
            "<ul class=\"cb-src\">" + string.Concat(v.Sources.Select(x => $"<li class=\"ok\">{E(x)}</li>")) +
            string.Concat(v.Missing.Select(x => $"<li class=\"no\">{E(x)} <small>not loaded yet, so not counted</small></li>")) + "</ul>" +
            $"<p class=\"cb-foot\">Prices and assumptions: {AdminLink()} (an hour at {E(CostRules.Money(v.HourlyCost, cur))}).</p>"));
        for (var i = 0; i < Math.Max(wide.Count, narrow.Count); i++)
        {
            var pair = i < wide.Count && i < narrow.Count;
            if (i < wide.Count) sb.Append(wide[i](pair ? "c8" : "c12"));
            if (i < narrow.Count) sb.Append(narrow[i](pair ? "c4" : "c12"));
        }

        return sb.Append("</div></div>").ToString();
    }

    /// <summary>The reports that name the people, licenses or agents behind an area's figures.</summary>
    static readonly Dictionary<string, (string Text, string Href)[]> Reports = new()
    {
        [CostAreas.Copilot] = [("License inventory", "/licensing"), ("Upgrade, train or revoke", "/forecasting/actions"), ("Better on pay-as-you-go", "/licensing/payg"),
                               ("Licenses on disabled accounts", "/licensing/leavers"), ("Suite fit", "/licensing/suites"), ("Value and time saved", "/leadership/value"), ("Budget and renewal", "/forecasting/budget")],
        [CostAreas.Studio] = [("Credit use", "/licensing/credit-use"), ("Agents costing credits", "/agents/credits"), ("Better with a license", "/licensing/license-for-credits"), ("Agents in use", "/agents")],
        [CostAreas.PowerBi] = [("License pool", "/licensing/pool"), ("Suite fit", "/licensing/suites")],
        [CostAreas.Automate] = [("License pool", "/licensing/pool")],
        [CostAreas.Apps] = [("License pool", "/licensing/pool")],
    };

    /// <summary>
    /// One area's own view (2.6.6): its figures, every saving, risk and opportunity in it, its six months, how it is
    /// counted, the table of what it is made of, and the reports with the named detail. Reached from the area's name,
    /// its part of the ring, or a saving that has no page of its own.
    /// </summary>
    string RenderArea(CostView v, CostArea a)
    {
        var cur = v.Currency;
        var slot = CostAreas.Slot(a.Name);
        OpenArea = a.Name;
        var mine = v.Opportunities.Where(o => o.Area == a.Name).ToList();
        var save = mine.Where(o => o.Kind == OpportunityKind.Save).ToList();
        var others = mine.Where(o => o.Kind != OpportunityKind.Save).ToList();
        var sb = new StringBuilder($"<div class=\"cboard\" style=\"--c:var(--s{slot})\">");
        sb.Append($"<nav class=\"cb-crumb\" aria-label=\"Areas\"><a href=\"{E(Ctx.Url(Request.Path, keepOrg: false))}\">‹ Costs and savings</a><span class=\"cb-here\"><i class=\"dot\" style=\"background:var(--s{slot})\"></i>{E(a.Name)}</span>" +
                  "<span class=\"cb-areas\">" + string.Concat(v.Areas.Where(x => x.Name != a.Name).Select(x => $"<a href=\"{E(Area(x.Name))}\"><i class=\"dot\" style=\"background:var(--s{CostAreas.Slot(x.Name)})\"></i>{E(x.Name)}</a>")) + "</span></nav>");

        sb.Append("<div class=\"cb-kpis\">");
        sb.Append(Tile($"Spend a month ({cur})", a.Basis, N(a.Spend), "spend", $"{(v.Spend > 0 ? a.Spend / v.Spend : 0):P0} of all AI and automation spend", Meter(v.Spend > 0 ? (double)(a.Spend / v.Spend) : 0, $"var(--s{slot})", "Share of all spend")));
        sb.Append(Tile($"Savings found a month ({cur})", "What would stop being spent if every saving below were taken.", N(a.Saving), "save",
            $"{(a.Spend > 0 ? a.Saving / a.Spend : 0):P0} of the area; {M(a.Saving * 12, cur)} a year", Meter(a.Spend > 0 ? (double)(a.Saving / a.Spend) : 0, "var(--good)", "Savings as a share of the area")));
        var val = a.Value ?? 0;
        sb.Append(Tile($"Value a month ({cur}, estimate)", "The time its use saves, at Admin's hourly cost; a dash where its value is not measured.", a.Value is null ? "—" : N(val), "value",
            a.Value is null ? "not measured for this area" : $"{val / Math.Max(1, v.HourlyCost):#,0} hours at {CostRules.Money(v.HourlyCost, cur)}", ""));
        sb.Append(Tile("Return on the spend", "Value over spend, for this area.", a.Value is { } v2 && a.Spend > 0 ? Times(v2 / a.Spend) : "—", "roi",
            a.Value is { } v3 && a.Spend > a.Saving && a.Saving > 0 ? $"{Times(v3 / (a.Spend - a.Saving))} once its savings are taken" : "value over spend", ""));
        sb.Append(Tile("To act on", "Savings, risks and opportunities in this area.", (save.Count + others.Count).ToString("#,0", Inv), "done",
            $"{Count(save.Count, "saving")}, {Count(others.Count, "opportunity or risk", "opportunities and risks")}", ""));
        sb.Append("</div><div class=\"cb-grid\">");

        /* Everything to do in it, ranked, the savings first. */
        sb.Append(Card(save.Count > 0 ? $"What can be done: {M(save.Sum(o => o.Saving), cur)} a month" : "What can be done",
            "Every saving in the area, ranked by what it saves a month, then what holds its return back. A title opens the page with the named detail where there is one.", "c8",
            (save.Count == 0 ? "<p class=\"sub\">No saving found in this area.</p>" : "<ol class=\"cb-top\">" + string.Concat(save.Select((o, i) => TopRow(o, i + 1, cur))) + "</ol>") +
            (others.Count == 0 ? "" : "<p class=\"cb-sub\">To grow the return, and risks</p><div class=\"cb-grow\">" + string.Concat(others.Select(o => GrowCard(o, cur))) + "</div>")));

        /* How it is counted, and where its detail is. */
        /* Only the reports this viewer may open. */
        var reports = (Reports.TryGetValue(a.Name, out var rs) ? rs : []).Select(r => (r.Text, Href: Whole(r.Href))).Where(r => r.Href is not null).ToArray();
        sb.Append(Card("How it is counted", "What the area's figures are worked out from, and the reports that name what is behind them.", "c4",
            $"<p class=\"cb-basis\">{E(a.Basis)}.</p>" +
            (reports.Length == 0 ? "<p class=\"cb-foot\">No report lists this area's detail beyond the tables on this page.</p>"
                : "<p class=\"cb-sub\">The detail, in the reports</p><ul class=\"cb-links\">" + string.Concat(reports.Select(r => $"<li><a href=\"{E(r.Href!)}\">{E(r.Text)} ›</a></li>")) + "</ul>") +
            $"<p class=\"cb-foot\">Prices and assumptions: {AdminLink()}.</p>"));

        /* What it is made of. */
        foreach (var d in (v.Details ?? []).Where(d => d.Area == a.Name))
        {
            var t = new Table { Headers = d.Headers, Numeric = d.Numeric };
            /* A row's link to a list this viewer cannot open is shown as its name only. */
            foreach (var r in d.Rows) t.Rows.Add([.. r.Select(cell => cell is Link l ? (Whole(l.Href) is { } to ? new Link(l.Text, to) : l.Text) : cell)]);
            sb.Append(Card(d.Title, d.Note ?? d.Title, "c12", t.Render().Value ?? ""));
        }

        /* Its six months. */
        var months = v.Months.Where(m => m.ByArea.TryGetValue(a.Name, out var x) && x > 0).ToList();
        if (months.Count >= 2)
        {
            var chart = Chart.Columns(months.Select(m => m.Month.ToString("MMM yyyy", Inv)).ToList(),
                [new Series(a.Name, months.Select(m => (double?)(double)m.ByArea[a.Name]).ToList(), slot)], Fmt.Count, $"{a.Name}: spend a month, {cur}, the last six whole months");
            sb.Append(Card($"Spend by month ({cur})", "The last six whole months the area's source covers.", a.Name == CostAreas.Fabric && v.CopilotInFabric is not null ? "c8" : "c12",
                chart is ChartSvg c ? c.Render(Narrow ? 320 : 720) : chart.Value ?? ""));
        }
        if (a.Name == CostAreas.Fabric && v.CopilotInFabric is { } cif)
            sb.Append(Card("Copilot in Power BI", "Copilot's share of the Fabric capacity, its cost and its return.", "c4",
                $"<div class=\"cif\"><div class=\"cif-big\"><b>{(cif.Cost > 0 ? Times(cif.Value / cif.Cost) : "—")}</b><span>return on {M(cif.Cost, cur)} a month of capacity: {M(cif.Value, cur)} of value</span></div>" +
                $"<div class=\"cif-grid\">{Stat("Capacity it uses", $"{cif.Share:P1}", $"on {Count(cif.Capacities, "capacity", "capacities")}")}{Stat("Requests a month", cif.Requests.ToString("#,0", Inv), $"by up to {cif.Users:#,0} people a day")}</div></div>"));
        if (a.Name is CostAreas.Copilot or CostAreas.PowerBi && v.Suites is { } su)
            sb.Append(SuitesCard(su, cur, "c12", Open));
        return sb.Append("</div></div>").ToString();
    }

    /// <summary>The savings after the first three: seven as rows, and any more behind a button, so the list is never long.</summary>
    string Rest(IReadOnlyList<Opportunity> save, string cur)
    {
        const int first = 3, rows = 7;
        if (save.Count <= first) return "";
        string R(Opportunity o, int rank) =>
            $"<div class=\"rr\" role=\"row\"><span role=\"cell\" class=\"rk\">{rank}</span>" +
            $"<span role=\"cell\" class=\"rt\">{Go(o.Title, o.Href, o.Area)}{((HtmlString)InfoTip.Icon($"{o.Evidence} Do: {o.Action}", o.Title)).Value}</span>" +
            $"<span role=\"cell\" class=\"ra\"><i class=\"dot\" style=\"background:var(--s{CostAreas.Slot(o.Area)})\"></i>{Go(o.Area, null, o.Area)}</span>" +
            $"<span role=\"cell\"><span class=\"conf {o.Confidence.ToLowerInvariant()}\">{E(o.Confidence)}</span></span>" +
            $"<span role=\"cell\" class=\"n rs\" data-l=\"A month\">{N(o.Saving)}</span></div>";
        var shown = save.Skip(first).Take(rows).ToList();
        var more = save.Skip(first + rows).ToList();
        return "<div class=\"cb-rest\" role=\"table\" aria-label=\"The other savings\"><div class=\"rr rh\" role=\"row\"><span role=\"columnheader\">#</span><span role=\"columnheader\">Saving</span>" +
               $"<span role=\"columnheader\">Area</span><span role=\"columnheader\">Confidence</span><span role=\"columnheader\" class=\"n\">A month ({E(cur)})</span></div>" +
               string.Concat(shown.Select((o, i) => R(o, first + 1 + i))) +
               (more.Count == 0 ? "" : $"<details class=\"more\"><summary>{more.Count:#,0} smaller {(more.Count == 1 ? "saving" : "savings")}, {E(M(more.Sum(o => o.Saving), cur))} a month</summary>" +
                                       string.Concat(more.Select((o, i) => R(o, first + rows + 1 + i))) + "</details>") +
               "</div>";
    }

    /// <summary>The areas as a ledger: spend, its bar with the saveable part hatched, six months, savings, value and return; the total last.</summary>
    string Ledger(CostView v, IReadOnlyList<CostMonth> months)
    {
        var cur = v.Currency;
        var max = v.Areas.Select(a => a.Spend).DefaultIfEmpty(1).Max();
        var sb = new StringBuilder("<div class=\"ledger\" role=\"table\" aria-label=\"Spend, savings and value a month by area\">");
        sb.Append($"<div class=\"lrow lhead\" role=\"row\"><span role=\"columnheader\">Area</span><span role=\"columnheader\" class=\"n\">Spend ({E(cur)})</span><span role=\"columnheader\" class=\"lbarh\">Spend, and the part that can be saved</span>" +
                  $"<span role=\"columnheader\" class=\"lsp\">Six months</span><span role=\"columnheader\" class=\"n\">Savings ({E(cur)})</span><span role=\"columnheader\" class=\"n\">Value ({E(cur)})</span><span role=\"columnheader\" class=\"n\">Return</span></div>");
        foreach (var a in v.Areas)
        {
            var slot = CostAreas.Slot(a.Name);
            var savePct = a.Spend > 0 ? Math.Min(100, (double)(a.Saving / a.Spend) * 100) : 0;
            var roi = a.Value is { } val && a.Spend > 0 ? Times(val / a.Spend) : "—";
            var line = months.Select(m => m.ByArea.TryGetValue(a.Name, out var x) ? (double)x : 0).ToList();
            sb.Append($"<div class=\"lrow\" role=\"row\"><span role=\"cell\" class=\"lname\"><i class=\"dot\" style=\"background:var(--s{slot})\"></i>" +
                      Go(a.Name, null, a.Name) + ((HtmlString)InfoTip.Icon(a.Basis, $"How {a.Name}'s figures are worked out")).Value + "</span>" +
                      $"<span role=\"cell\" class=\"n\" data-l=\"Spend\">{N(a.Spend)}</span>" +
                      $"<span role=\"cell\" class=\"lbar\" aria-hidden=\"true\"><span class=\"lspend\" style=\"width:{Pct(a.Spend, max)}%;--c:var(--s{slot})\"><span class=\"lsave\" style=\"width:{savePct.ToString("0.#", Inv)}%\"></span></span></span>" +
                      $"<span role=\"cell\" class=\"lsp\" data-l=\"Six months\">{(line.Count(x => x > 0) >= 2 ? Spark(line, $"var(--s{slot})", $"Spend of {a.Name} over the last six whole months") : "<span class=\"muted\">—</span>")}</span>" +
                      $"<span role=\"cell\" class=\"n lsv\" data-l=\"Savings\">{(a.Saving > 0 ? N(a.Saving) : "—")}</span>" +
                      $"<span role=\"cell\" class=\"n\" data-l=\"Value\">{(a.Value is { } x ? N(x) : "—")}</span>" +
                      $"<span role=\"cell\" class=\"n\" data-l=\"Return\">{roi}</span></div>");
        }
        sb.Append($"<div class=\"lrow ltotal\" role=\"row\"><span role=\"cell\" class=\"lname\">All areas</span><span role=\"cell\" class=\"n\" data-l=\"Spend\">{N(v.Spend)}</span><span role=\"cell\" class=\"lbar\" aria-hidden=\"true\"></span><span role=\"cell\" class=\"lsp\"></span>" +
                  $"<span role=\"cell\" class=\"n lsv\" data-l=\"Savings\">{N(v.Saving)}</span><span role=\"cell\" class=\"n\" data-l=\"Value\">{N(v.Value)}</span>" +
                  $"<span role=\"cell\" class=\"n\" data-l=\"Return\">{(v.Spend > 0 ? Times(v.Value / v.Spend) : "—")}</span></div>");
        return sb.Append("</div>").ToString();
    }

    /// <summary>
    /// The suites (2.6.6): what each held costs, what it includes that is not counted again as AI spend, and the moves
    /// Licensing › Suite fit finds (E7 to E5, E5 to E3, a second suite, training first), outside the AI totals: a
    /// suite's price buys much that cannot be split from it, so its savings are a review, not a sum to add.
    /// </summary>
    static string SuitesCard(SuiteSummary s, string cur, string cls, Func<string, string?, string?> open)
    {
        var sb = new StringBuilder("<ul class=\"cb-suite\">");
        foreach (var h in s.Held)
            sb.Append($"<li><span class=\"sn\">{(open("/licensing/suites", $"suite={Uri.EscapeDataString(h.Suite)}") is { } su ? $"<a href=\"{E(su)}\">{E(h.Suite)}</a>" : E(h.Suite))}</span><span class=\"sh\">{h.Holders:#,0} × {E(CostRules.Money(h.Price, cur))}</span>" +
                      (h.Includes.Length > 0 ? $"<small>{E(h.Includes)} inside: {E(M(h.IncludedValue, cur))} at list, not AI spend</small>" : "") + "</li>");
        sb.Append("</ul>");
        if (s.Moves.Count > 0)
        {
            sb.Append($"<p class=\"cb-sub\">Moves to review ({E(cur)} a month)</p><ul class=\"cb-moves\">");
            foreach (var m in s.Moves)
            {
                var worth = m.Move == SuiteMove.Train ? m.AtStake : m.Saving;
                var what = m.Move switch
                {
                    SuiteMove.Train => $"Train {m.People:#,0} on {Short(m.From)} first",
                    SuiteMove.Reclaim => $"Take back {m.People:#,0} {Short(m.From)} from disabled accounts",
                    SuiteMove.DropDuplicate => $"Drop the second suite beside {Short(m.From)} for {m.People:#,0}",
                    SuiteMove.CutSeats => $"Cut {m.People:#,0} unassigned {Short(m.From)} seats",
                    _ => $"{m.People:#,0} from {Short(m.From)} to {Short(m.To ?? "")}",
                };
                var link = m.Move == SuiteMove.CutSeats ? open("/licensing/pool", null)
                    : open("/licensing/suites", (m.Move == SuiteMove.DropDuplicate ? "copy=suite" : $"move={m.Move.ToString().ToLowerInvariant()}") + $"&suite={Uri.EscapeDataString(m.From)}");
                sb.Append($"<li><span class=\"mw\">{(link is null ? E(what) : $"<a href=\"{E(link)}\">{E(what)}</a>")}</span><b title=\"{(m.Move == SuiteMove.Train ? "riding on it" : "saved")} a month\">{E(N(worth))}</b><span class=\"conf {m.Confidence.ToLowerInvariant()}\">{E(m.Confidence)}</span></li>");
            }            sb.Append("</ul>");
        }
        else sb.Append("<p class=\"cb-foot\">Every holder's suite fits what the data shows of them.</p>");
        sb.Append($"<p class=\"cb-foot\">{(s.PowerBiKnown ? "" : "Power BI activity is not loaded, so no E5 holder is offered E3. ")}Outside the savings above: each move names what to check first in {(open("/licensing/suites", null) is { } sf ? $"<a href=\"{E(sf)}\">Licensing › Suite fit</a>" : "Licensing › Suite fit, for the Licensing role")}.</p>");
        return Card(s.Saving > 0 ? $"Suites: {M(s.Saving, cur)} a month to review" : "Suites",
            "E3, E5 and E7 as Admin › Targets and assumptions prices them. A suite's price also buys security, compliance and Teams, which cannot be split from it, so none of it is counted as AI spend, and Copilot or Power BI Pro inside it is not priced again. " +
            "A standalone one on a suite's holder is paid twice, and counted with its area above. A move down rests on Copilot and Power BI use, the only parts of a suite the data sees, so these are a review, not savings to add.", cls, sb.ToString());
    }

    /// <summary>"Microsoft 365 E5" as "E5", and "Microsoft 365 Copilot" as the add-on, for a narrow card; Office 365's as they are.</summary>
    static string Short(string suite) => suite.Replace("Microsoft 365 E", "E", StringComparison.Ordinal).Replace("Microsoft 365 Copilot", "the Copilot add-on", StringComparison.Ordinal);

    /// <summary>One of the three largest savings: its rank, area and confidence, the evidence and what to do, and what it is worth.</summary>
    string TopRow(Opportunity o, int rank, string cur) =>
        $"<li class=\"tc\" style=\"--c:var(--s{CostAreas.Slot(o.Area)})\"><span class=\"tc-r\">{rank}</span><div class=\"tc-b\">" +
        $"<div class=\"tc-h\"><span class=\"tc-a\"><i class=\"dot\" style=\"background:var(--c)\"></i>{Go(o.Area, null, o.Area)}</span><span class=\"conf {o.Confidence.ToLowerInvariant()}\">{E(o.Confidence)} confidence</span></div>" +
        $"<h3>{Go(o.Title, o.Href, o.Area)}</h3><p>{E(o.Evidence)}</p><p class=\"tc-do\"><b>Do</b> {E(o.Action)}</p></div>" +
        $"<div class=\"tc-w\"><b>{E(M(o.Saving, cur))}</b><span>a month</span><span>{E(M(o.Saving * 12, cur))} a year</span></div></li>";

    /// <summary>An opportunity to grow the return, or a risk to it: what it holds back, and what to do.</summary>
    string GrowCard(Opportunity o, string cur) =>
        $"<article class=\"gc {o.Kind.ToString().ToLowerInvariant()}\"><div class=\"gc-h\"><span class=\"gk\">{(o.Kind == OpportunityKind.Grow ? "Grow" : "Risk")}</span>" +
        $"<span class=\"tc-a\"><i class=\"dot\" style=\"background:var(--s{CostAreas.Slot(o.Area)})\"></i>{Go(o.Area, null, o.Area)}</span></div>" +
        $"<div class=\"gc-w\">{(o.AtStake > 0 ? $"<b>{E(M(o.AtStake, cur))}</b><span>{(o.Kind == OpportunityKind.Grow ? "at stake" : "at risk")} a month</span>" : "<span>no figure on it</span>")}</div>" +
        $"<h3>{Go(o.Title, o.Href, o.Area)}{((HtmlString)InfoTip.Icon(o.Evidence, o.Title)).Value}</h3><p class=\"tc-do\"><b>Do</b> {E(o.Action)}</p></article>";

    /// <summary>A headline figure: its label, value, a line on it, a small picture, and where to see what it is made of.</summary>
    static string Tile(string label, string tip, string value, string kind, string note, string visual, (string Text, string Href)? more = null) =>
        $"<div class=\"cb-tile t-{kind}\"><div class=\"cb-label\">{T(label, tip)}</div><div class=\"cb-value\">{E(value)}</div><div class=\"cb-note\">{E(note)}</div>" +
        (visual.Length > 0 ? $"<div class=\"cb-vis\">{visual}</div>" : "") +
        (more is { } m ? $"<a class=\"cb-more\" href=\"{E(m.Href)}\">{E(m.Text)} ›</a>" : "") + "</div>";

    static string Card(string title, string tip, string cls, string body, string? csv = null, string? id = null) =>
        $"<section class=\"panel {cls}\"{(id is null ? "" : $" id=\"{id}\"")}><div class=\"phead\"><h2>{T(title, tip)}</h2>{(csv is null ? "" : $"<a class=\"csv\" href=\"{E(csv)}\">Download CSV</a>")}</div>{body}</section>";

    static string Stat(string label, string value, string note, string visual = "") =>
        $"<div class=\"st\"><span class=\"st-l\">{E(label)}</span><b>{E(value)}</b>{visual}<span class=\"st-n\">{E(note)}</span></div>";

    /* ---- Small pictures, drawn here: a line, a bar, a ring and a dial, in the page's colors. ---- */

    /// <summary>A line of the values, scaled to its box; the stroke keeps its width however the box is stretched.</summary>
    static string Spark(IReadOnlyList<double> ys, string color, string label)
    {
        const double w = 120, h = 30;
        var max = ys.DefaultIfEmpty(0).Max(); var min = Math.Min(0, ys.DefaultIfEmpty(0).Min());
        var span = max - min <= 0 ? 1 : max - min;
        var pts = ys.Select((y, i) => (X: ys.Count == 1 ? w / 2 : i * w / (ys.Count - 1), Y: h - 2 - (y - min) / span * (h - 4))).ToList();
        var line = string.Join(" ", pts.Select(p => $"{F(p.X)},{F(p.Y)}"));
        return $"<svg class=\"spark\" viewBox=\"0 0 {F(w)} {F(h)}\" preserveAspectRatio=\"none\" role=\"img\" aria-label=\"{E(label)}\"><title>{E(label)}</title>" +
               $"<polygon points=\"0,{F(h)} {line} {F(w)},{F(h)}\" style=\"fill:{color};opacity:.14\"/>" +
               $"<polyline points=\"{line}\" style=\"stroke:{color}\" vector-effect=\"non-scaling-stroke\"/></svg>";
    }

    static string Meter(double share, string color, string label) =>
        $"<span class=\"meter\" role=\"img\" aria-label=\"{E(label)}: {share:P0}\"><span style=\"width:{F(Math.Clamp(share, 0, 1) * 100)}%;background:{color}\"></span></span>";

    /// <summary>A ring of the areas' shares, the total in its middle; each part names its area on hover.</summary>
    static string Donut(IReadOnlyList<CostArea> areas, decimal total, string cur, Func<string, string>? url = null)
    {
        const double r = 62, c = 2 * Math.PI * r;
        var sb = new StringBuilder($"<svg class=\"donut\" viewBox=\"0 0 160 160\" role=\"img\" aria-label=\"Spend a month by area\"><circle cx=\"80\" cy=\"80\" r=\"{F(r)}\" class=\"d-track\"/>");
        double at = 0;
        foreach (var a in areas)
        {
            var len = total > 0 ? (double)(a.Spend / total) * c : 0;
            var gap = areas.Count > 1 && len > 3 ? 1.5 : 0;
            sb.Append(url is null ? "" : $"<a href=\"{E(url(a.Name))}\">");
            sb.Append($"<circle cx=\"80\" cy=\"80\" r=\"{F(r)}\" class=\"d-seg\" style=\"stroke:var(--s{CostAreas.Slot(a.Name)})\" stroke-dasharray=\"{F(Math.Max(0, len - gap))} {F(c)}\" stroke-dashoffset=\"{F(-at)}\" transform=\"rotate(-90 80 80)\">" +
                      $"<title>{E(a.Name)}: {E(M(a.Spend, cur))} a month, {(total > 0 ? a.Spend / total : 0):P0}</title></circle>");
            sb.Append(url is null ? "" : "</a>");
            at += len;
        }
        return sb.Append($"<text x=\"80\" y=\"78\" class=\"d-big\">{E(Short(total))}</text><text x=\"80\" y=\"97\" class=\"d-sub\">{E(cur)} a month</text></svg>").ToString();
    }

    /// <summary>A half dial of the return: the arc to the return now, a marker where it would be once the savings are taken.</summary>
    static string Gauge(double now, double? then)
    {
        var top = Math.Max(now, then ?? 0);
        var max = top <= 5 ? 5 : top <= 10 ? 10 : Math.Ceiling(top / 10) * 10;
        static (double X, double Y) At(double t) { var a = Math.PI * (1 - Math.Clamp(t, 0, 1)); return (60 + 48 * Math.Cos(a), 58 - 48 * Math.Sin(a)); }
        var sb = new StringBuilder($"<svg class=\"gauge\" viewBox=\"0 0 120 78\" role=\"img\" aria-label=\"Return {F(now)} times, out of a dial to {F(max)}\">" +
            "<path d=\"M12 58 A48 48 0 0 1 108 58\" class=\"g-track\" pathLength=\"100\"/>" +
            $"<path d=\"M12 58 A48 48 0 0 1 108 58\" class=\"g-val\" pathLength=\"100\" stroke-dasharray=\"{F(Math.Clamp(now / max, 0, 1) * 100)} 100\"/>");
        if (then is { } t2)
        {
            var (x, y) = At(t2 / max); var (xi, yi) = (60 + (x - 60) * 0.72, 58 + (y - 58) * 0.72);
            sb.Append($"<line x1=\"{F(xi)}\" y1=\"{F(yi)}\" x2=\"{F(60 + (x - 60) * 1.18)}\" y2=\"{F(58 + (y - 58) * 1.18)}\" class=\"g-then\"><title>{F(t2)} times once the savings are taken</title></line>");
        }
        return sb.Append($"<text x=\"12\" y=\"75\" class=\"g-tick\">0×</text><text x=\"108\" y=\"75\" class=\"g-tick\">{F(max)}×</text></svg>").ToString();
    }

    /* ---- Formatting ---- */

    static string N(decimal x) => Math.Round(x, 0).ToString("#,0", Inv);
    static string M(decimal x, string cur) => CostRules.Money(Math.Round(x, 0), cur);
    static string F(double x) => x.ToString("0.##", Inv);
    static string Pct(decimal part, decimal whole) => whole > 0 ? Math.Min(100, (double)(part / whole) * 100).ToString("0.#", Inv) : "0";
    /// <summary>A large figure in the ring's middle, short enough to fit it: 194,930 as "195K", 2,339,160 as "2.34M".</summary>
    static string Short(decimal x) => x >= 1_000_000 ? (x / 1_000_000).ToString("0.##", Inv) + "M" : x >= 10_000 ? (x / 1000).ToString("#,0", Inv) + "K" : N(x);
    static string T(string label, string tip) => ((HtmlString)InfoTip.After(label, tip)).Value ?? "";
    static string Link(string text, string? href) => href is null ? E(text) : $"<a href=\"{E(href)}\">{E(text)}</a>";

    /// <summary>A return as a multiple: one decimal under ten, none above ("4.2×", "151×").</summary>
    static string Times(decimal r) => r >= 10 ? $"{r.ToString("#,0", Inv)}×" : $"{r.ToString("0.0", Inv)}×";
}
