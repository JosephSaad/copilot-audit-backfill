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
/// Apps with Dataverse, GitHub Copilot and Foundry. A month at a time, ranked by what an executive can act on.
/// </summary>
public sealed class CostsModel(PageServices s, CostData costs) : InsightsPage(s)
{
    public override string Title => "Costs and savings";
    public override string Persona => "Leadership";
    public override string Intro => "Every figure is a month: the licenses held now at their price, and what the other sources billed or used over their last 30 days. "
        + "Savings are what would stop being spent if the action beside each were taken. Value is the time the tools save, valued at Admin's hourly cost: "
        + "Copilot's interactions, the flows' runs and Copilot in Power BI's requests. Prices and assumptions are in Admin > Targets and assumptions.";
    public override bool UsesTeam => false;

    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    protected override async Task BuildAsync()
    {
        /* The company's spend is not any one team's: whole-organization viewers, and the Licensing role, see it. */
        if (!Viewer.OrgWide && !Viewer.Has(Roles.Licensing))
        {
            Notice = "This page shows the whole organization's spend on AI and automation, so it is for people who see the whole organization, or who hold the Licensing role. An administrator can grant either in Admin > Access.";
            return;
        }
        var v = await costs.BuildAsync(Ctx);
        var cur = v.Currency;
        string M(decimal x) => CostRules.Money(Math.Round(x, 0), cur);
        if (v.Areas.Count == 0)
        {
            Notice = "No spend to show yet: none of the sources is loaded. Docs\\OPERATIONS.md, Costs and savings, says what each needs.";
            return;
        }

        var saving = v.Saving;
        var grow = v.Opportunities.Where(o => o.Kind != OpportunityKind.Save).ToList();
        Kpis.Add(new($"AI and automation spend a month ({cur})", Num((long)v.Spend), $"{M(v.Spend * 12)} a year, across {Count(v.Areas.Count, "area")}"));
        Kpis.Add(new($"Savings found a month ({cur})", Num((long)saving), $"{(v.Spend > 0 ? saving / v.Spend : 0):P0} of the spend; {M(saving * 12)} a year", saving > 0 ? "good" : null));
        Kpis.Add(new($"Value delivered a month ({cur}, estimate)", Num((long)v.Value), $"hours saved at {CostRules.Money(v.HourlyCost, cur)} an hour"));
        Kpis.Add(new("Return on the spend", v.Spend > 0 ? Times(v.Value / v.Spend) : "—",
            v.Spend > saving && saving > 0 ? $"{Times(v.Value / (v.Spend - saving))} once the savings are taken" : "value against spend"));
        Kpis.Add(new($"Already saved a month ({cur})", Num((long)v.RealizedSaving), v.RealizedLicenses > 0 ? $"{Count(v.RealizedLicenses, "Copilot license")} reclaimed from this dashboard's exports" : "nothing reclaimed from exports yet"));

        Panels.Add(new("Where the money goes, and what it returns", new HtmlString(Ledger(v)),
            "Each area's spend a month, the part of it that can be saved (the hatched end of its bar), and the value its use is estimated to return. Hover a figure for how it was worked out."));

        var save = v.Opportunities.Where(o => o.Kind == OpportunityKind.Save).ToList();
        var csv = new Table { Headers = ["Kind", "Area", "Opportunity", "Evidence", "What to do", $"Saving a month ({cur})", $"Saving a year ({cur})", $"At stake a month ({cur})", "Confidence"],
                              Numeric = new HashSet<int> { 5, 6, 7 } };
        foreach (var o in v.Opportunities) csv.Rows.Add([o.Kind.ToString(), o.Area, o.Title, o.Evidence, o.Action, Math.Round(o.Saving), Math.Round(o.Saving * 12), Math.Round(o.AtStake), o.Confidence]);
        CsvTable = csv;
        Panels.Add(new($"Savings to take: {M(saving)} a month", new HtmlString(Opportunities(save.Take(10).ToList(), cur, true) +
                (save.Count > 10 ? $"<p class=\"sub\">And {Count(save.Count - 10, "smaller one")}, in the CSV.</p>" : "")),
            "Ranked by what each saves a month. High confidence: the data shows it outright. Medium: it follows from a change you make. Low: it rests on a price or an assumption.", "full", CsvUrl()));

        var months = v.Months.Where(m => m.ByArea.Values.Sum() > 0).ToList();
        if (months.Count >= 2)
        {
            var labels = months.Select(m => m.Month.ToString("MMM yyyy", Inv)).ToList();
            var series = CostAreas.All.Where(a => months.Any(m => m.ByArea.ContainsKey(a.Name)))
                .Select(a => new Series(a.Name, months.Select(m => m.ByArea.TryGetValue(a.Name, out var x) ? (double?)(double)x : null).ToList(), a.Slot)).ToList();
            Panels.Add(new("Spend by month", Chart.Columns(labels, series, Fmt.Count, $"Spend a month by area, {cur}, the last six whole months"),
                "The last six whole months. Copilot from the licensed days; the other licenses from the license pool's daily counts; the rest from what each source billed. A source shows from the first month it covers.", "half"));
        }
        Panels.Add(new("Savings by area", Chart.Bars(v.Areas.Where(a => a.Saving > 0).OrderByDescending(a => a.Saving)
                .Select(a => new Bar(a.Name, (double)Math.Round(a.Saving), Note: $"{(a.Spend > 0 ? a.Saving / a.Spend : 0):P0} of {M(a.Spend)}", Slot: CostAreas.Slot(a.Name))).ToList(), Fmt.Count, $"Savings found a month by area, {cur}"),
            "What the opportunities above save in each area, and what share of the area's spend that is.", "half"));

        if (v.CopilotInFabric is { } cif)
        {
            var per = cif.Requests > 0 ? cif.Cost / cif.Requests : 0;
            Panels.Add(new("Copilot in Power BI", new HtmlString(
                "<div class=\"facts\">" +
                Fact("Capacity it uses", $"{cif.Share:P1}", $"of the Fabric capacities' work over the last 30 days, on {Count(cif.Capacities, "capacity", "capacities")}") +
                Fact("What that costs a month", M(cif.Cost), "its share of the capacities' cost") +
                Fact("Requests a month", cif.Requests.ToString("#,0", Inv), $"by up to {cif.Users:#,0} people a day") +
                Fact("Cost a request", CostRules.Money(Math.Round(per, 2), cur), "capacity cost over requests") +
                Fact("Value a month (estimate)", M(cif.Value), "at Admin's minutes a request and hourly cost") +
                Fact("Return", cif.Cost > 0 ? Times(cif.Value / cif.Cost) : "—", "value over its share of the cost") +
                "</div>"),
                "Copilot runs on the Fabric capacity the report's workspace is on, so its cost is a share of that capacity's. Requests come from the Capacity Metrics app's Copilot operations.", "third"));
        }
        if (grow.Count > 0)
            Panels.Add(new("Grow the return", new HtmlString(Opportunities(grow, cur, false)),
                "Spend that is not yet returning what it could: people to train, licenses to hand on, work being lost. The figure is the monthly spend or value each holds back.", v.CopilotInFabric is null ? "half" : "third"));
        Panels.Add(new("What this is built from", new HtmlString(
                "<ul class=\"tips sub\">" + string.Concat(v.Sources.Select(x => $"<li>{E(x)}</li>")) + "</ul>" +
                (v.Missing.Count == 0 ? "" : "<p class=\"sub\"><b>Not loaded yet</b>, so not counted:</p><ul class=\"tips sub\">" + string.Concat(v.Missing.Select(x => $"<li>{E(x)}</li>")) + "</ul>") +
                $"<p class=\"sub\">Prices: the license price list and the assumptions in <a href=\"/admin/assumptions\">Admin › Targets and assumptions</a> (an hour at {E(CostRules.Money(v.HourlyCost, cur))}).</p>"),
            "Every source is optional: an area appears once its source is loaded, and one that is not is listed here rather than shown as zero.", v.CopilotInFabric is null && grow.Count == 0 ? "half" : "third"));
    }

    /// <summary>The areas as a ledger: spend, its bar with the saveable part hatched, savings, value and return; the total last.</summary>
    string Ledger(CostView v)
    {
        var cur = v.Currency;
        var max = v.Areas.Select(a => a.Spend).DefaultIfEmpty(1).Max();
        string M(decimal x) => Math.Round(x, 0).ToString("#,0", Inv);
        var sb = new StringBuilder("<div class=\"ledger\" role=\"table\" aria-label=\"Spend, savings and value a month by area\">");
        sb.Append($"<div class=\"lrow lhead\" role=\"row\"><span role=\"columnheader\">Area</span><span role=\"columnheader\" class=\"n\">Spend ({E(cur)})</span><span role=\"columnheader\" class=\"lbarh\">Spend, and the part that can be saved</span>" +
                  $"<span role=\"columnheader\" class=\"n\">Savings ({E(cur)})</span><span role=\"columnheader\" class=\"n\">Value ({E(cur)})</span><span role=\"columnheader\" class=\"n\">Return</span></div>");
        foreach (var a in v.Areas)
        {
            var slot = CostAreas.Slot(a.Name);
            var width = max > 0 ? (double)(a.Spend / max) * 100 : 0;
            var savePct = a.Spend > 0 ? Math.Min(100, (double)(a.Saving / a.Spend) * 100) : 0;
            var roi = a.Value is { } val && a.Spend > 0 ? Times(val / a.Spend) : "—";
            sb.Append($"<div class=\"lrow\" role=\"row\"><span role=\"cell\" class=\"lname\"><i class=\"dot\" style=\"background:var(--s{slot})\"></i>" +
                      (a.Href is null ? E(a.Name) : $"<a href=\"{E(a.Href)}\">{E(a.Name)}</a>") + ((HtmlString)InfoTip.Icon(a.Basis, $"How {a.Name}'s figures are worked out")).Value + "</span>" +
                      $"<span role=\"cell\" class=\"n\" data-l=\"Spend\">{M(a.Spend)}</span>" +
                      $"<span role=\"cell\" class=\"lbar\" aria-hidden=\"true\"><span class=\"lspend\" style=\"width:{width.ToString("0.#", Inv)}%;--c:var(--s{slot})\"><span class=\"lsave\" style=\"width:{savePct.ToString("0.#", Inv)}%\"></span></span></span>" +
                      $"<span role=\"cell\" class=\"n lsv\" data-l=\"Savings\">{(a.Saving > 0 ? M(a.Saving) : "—")}</span>" +
                      $"<span role=\"cell\" class=\"n\" data-l=\"Value\">{(a.Value is { } x ? M(x) : "—")}</span>" +
                      $"<span role=\"cell\" class=\"n\" data-l=\"Return\">{roi}</span></div>");
        }
        sb.Append($"<div class=\"lrow ltotal\" role=\"row\"><span role=\"cell\" class=\"lname\">All areas</span><span role=\"cell\" class=\"n\" data-l=\"Spend\">{M(v.Spend)}</span><span role=\"cell\" class=\"lbar\" aria-hidden=\"true\"></span>" +
                  $"<span role=\"cell\" class=\"n lsv\" data-l=\"Savings\">{M(v.Saving)}</span><span role=\"cell\" class=\"n\" data-l=\"Value\">{M(v.Value)}</span>" +
                  $"<span role=\"cell\" class=\"n\" data-l=\"Return\">{(v.Spend > 0 ? Times(v.Value / v.Spend) : "—")}</span></div>");
        return sb.Append("</div>").ToString();
    }

    /// <summary>Opportunities as a ranked list: what, why, what to do, and what it is worth.</summary>
    static string Opportunities(IReadOnlyList<Opportunity> list, string cur, bool numbered)
    {
        if (list.Count == 0) return "<p class=\"sub\">Nothing found: every area the sources cover is as lean as these rules can tell.</p>";
        var sb = new StringBuilder($"<{(numbered ? "ol" : "ul")} class=\"opps{(numbered ? "" : " compact")}\">");
        foreach (var o in list)
        {
            var worth = o.Kind == OpportunityKind.Save ? o.Saving : o.AtStake;
            var label = o.Kind switch { OpportunityKind.Save => "a month", OpportunityKind.Grow => "at stake a month", _ => "at risk a month" };
            sb.Append($"<li class=\"opp {o.Kind.ToString().ToLowerInvariant()}\">" +
                      $"<div class=\"otext\"><div class=\"ohead\"><i class=\"dot\" style=\"background:var(--s{CostAreas.Slot(o.Area)})\"></i><span class=\"oarea\">{E(o.Area)}</span>" +
                      $"<span class=\"conf {o.Confidence.ToLowerInvariant()}\">{E(o.Confidence)} confidence</span></div>" +
                      $"<h3>{(o.Href is null ? E(o.Title) : $"<a href=\"{E(o.Href)}\">{E(o.Title)}</a>")}</h3>" +
                      $"<p>{E(o.Evidence)}</p><p class=\"oact\"><b>Do:</b> {E(o.Action)}</p></div>" +
                      $"<div class=\"oworth\">{(worth > 0 ? $"<b>{E(CostRules.Money(Math.Round(worth, 0), cur))}</b><span>{label}</span>" + (o.Kind == OpportunityKind.Save ? $"<span>{E(CostRules.Money(Math.Round(worth * 12, 0), cur))} a year</span>" : "") : "<span>no direct saving</span>")}</div></li>");
        }
        return sb.Append(numbered ? "</ol>" : "</ul>").ToString();
    }

    /// <summary>A return as a multiple: one decimal under ten, none above ("4.2×", "151×").</summary>
    static string Times(decimal r) => r >= 10 ? $"{r.ToString("#,0", Inv)}×" : $"{r.ToString("0.0", Inv)}×";

    static string Fact(string label, string value, string note) =>
        $"<div><span class=\"fl\">{E(label)}</span><b>{E(value)}</b><p>{E(note)}</p></div>";
}
