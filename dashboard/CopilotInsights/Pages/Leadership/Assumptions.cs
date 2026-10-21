using CopilotInsights.Data;
using Microsoft.AspNetCore.Html;

namespace CopilotInsights.Pages.Leadership;

/// <summary>
/// How figures are calculated (2.6.9): every price and assumption a cost, saving, value or estimate rests on, in one list,
/// with its value now, the figures that use it, who last changed it, and where it is set. The inputs stay where they are
/// edited (Admin › Access, Admin › Targets and assumptions, Leadership › Copilot reservations); an administrator who may
/// change one gets a link that opens its page at the field. Prices that disagree are said first. Open to every viewer:
/// the figures the dashboard shows are worked out from these, so anyone quoting one can see what it rests on.
/// </summary>
public sealed class AssumptionRegisterModel(PageServices s, AssumptionData register) : InsightsPage(s)
{
    public override string Title => "How figures are calculated";
    public override string Persona => "Leadership";
    public override bool UsesFilters => false;
    public override string Intro => "Every price and assumption a cost, saving or value on this dashboard rests on: its value now, the figures that use it, who last changed it, and where an administrator changes it. Then how each kind of figure is worked out.";

    /// <summary>How each kind of figure is worked out, in words, the same as each figure's own "How it is worked out".</summary>
    public static readonly IReadOnlyList<(string Figure, string How, string Where)> Formulas =
    [
        ("Copilot license cost", "Licenses held × the Copilot license price a month.", "Chargeback, Licenses held"),
        ("Unused spend", "(Licenses held − holders who used Copilot in the period) × the license price.", "Chargeback"),
        ("Cost per person using Copilot", "The licenses' monthly cost ÷ the holders who used Copilot in the period.", "Chargeback"),
        ("Licenses to reclaim", "Holders with no use for the unused-after days, or on disabled accounts, × the license price.", "License optimization, Licenses on disabled accounts, Costs and savings"),
        ("Pay-as-you-go saving", "Candidates × the license price, less their metered interactions × the pay-as-you-go cost of an interaction.", "Better on pay-as-you-go, Costs and savings"),
        ("Credits a month", "Billed interactions × credits for an answer, + those grounded in work data × credits for grounding, + those calling another agent × credits for an action; × the price of a credit; brought to 30 days.", "Credit use, Agents costing credits, Better with a license"),
        ("Tokens a month", "Interactions × tokens a turn + resources read × tokens a resource + answers × tokens an answer; brought to 30 days. A rough figure: Microsoft reports none.", "Credit use"),
        ("Hours saved and value", "Interactions in each app × the minutes one saves there (the default unless set for the app) ÷ 60; × what an hour is worth.", "Value and time saved, Costs and savings"),
        ("Return", "Value ÷ cost, over the same window.", "Value and time saved, Costs and savings"),
        ("A license's spend", "Seats bought (or assigned, where more) × its price; from the volume tier's seats, every seat at the volume price.", "Costs and savings"),
        ("A cut of seats", "What the spend falls by: less where the count falls under a volume price and every seat goes back to the list price; less again where the seats' Dataverse capacity was all that kept the tenant within its entitlement (the add-on capacity it would need, at its price a GB).", "Costs and savings"),
        ("Suite moves", "The suite's price less the lower suite's, for each holder a lower one would serve.", "Suite fit, Costs and savings"),
        ("Reservation obligation", "Billable licenses (each organization's reserved, plus its allocation over them) × the rate in force; remaining: × the months left in the year.", "Copilot reservations"),
    ];

    object Link(string text, string path) => Open(path) is { } u ? new Pages.Link(text, u) : text;

    protected override async Task BuildAsync()
    {
        var all = await register.AllAsync();
        var conflicts = await register.ConflictsAsync(all);
        var defaults = all.Count(a => a.IsDefault && a.Def.Kind is AssumptionKind.Setting or AssumptionKind.Price);
        Kpis.Add(new("Assumptions", Num(all.Count), "prices, values and thresholds the figures use"));
        Kpis.Add(new("Still as installed", Num(defaults), "never set for this organization: list prices and published estimates", defaults > 0 ? "warn" : null));
        Kpis.Add(new("Prices that disagree", Num(conflicts.Count), conflicts.Count == 0 ? "none" : "settle them before quoting a figure", conflicts.Count > 0 ? "crit" : null));

        if (conflicts.Count > 0)
            Panels.Add(new("Prices that disagree", new HtmlString(string.Concat(conflicts.Select(c => $"<p class=\"conflict\">{E(c)}</p>"))),
                "Each figure uses the price where it is set; when the same thing has two prices, the pages that use each disagree."));

        foreach (var g in all.GroupBy(a => a.Def.Group))
        {
            var t = new Table { Headers = ["Assumption", "Value now", "Used by", "Last changed", ""], Sortable = false };
            foreach (var a in g)
            {
                /* Who changed it, for administrators only: the page is open to everyone. */
                var changed = a.IsDefault || a.ChangedBy is null ? "As installed" : Viewer.AnyAdmin && !Warming ? $"{When(a.ChangedUtc)} by {a.ChangedBy}" : When(a.ChangedUtc);
                object edit = Viewer.CanAdmin(a.Def.Page) && !Warming ? new Link("Change it", Assumptions.EditHref(a.Def)) : "";
                t.Rows.Add([a.Def.Label, a.Shown, a.Def.UsedBy, changed, edit]);
            }
            Panels.Add(new(g.Key, t.Render(), g.Key switch
            {
                "Prices" => "What each license or unit costs a month, in the currency set in Admin › Access. Microsoft's list prices until set to what your agreement pays.",
                "Suites" => "What each suite costs a user a month, for Suite fit's moves. Its inclusions are set beside the price.",
                "Value" => "Your organization's own estimates of the time saved: from a survey or a pilot, not a measurement.",
                "Credits and tokens" => "Microsoft's list rates for Copilot Credits, and rough token sizes: Microsoft reports neither per person.",
                "Use and thresholds" => "When a license or seat counts as unused, and the smallest group a breakdown shows.",
                _ => null,
            }));
        }

        /* 2.7.0: the Copilot license's cost appears on five pages, each counting something else: which to quote for what. */
        var ct = new Table { Headers = ["Figure", "Page", "What it counts", "Quote it as"], Sortable = false };
        ct.Rows.Add(["Microsoft 365 Copilot, spend a month", Link("Costs and savings", "/leadership/costs"), "The add-on seats bought (or held, where more are held) × the Copilot license price", "What Microsoft bills for the add-on"]);
        ct.Rows.Add(["Licenses held, a month", Link("Chargeback", "/licensing/chargeback"), "The license holders in each group today × the Copilot license price", "What each group holds, for allocating the cost"]);
        ct.Rows.Add(["License cost in the period", Link("Value and time saved", "/leadership/value"), "Each calendar month the period charges (those whose last day falls inside it) × the people who held a license on at least one day of it × the price: a month costs the price whole", "The cost of the period chosen, against its value"]);
        ct.Rows.Add(["Monthly obligation", Link("Copilot reservations", "/leadership/reservations"), "Billable licenses (each organization's reserved seats, plus any held over them) × the reservation rate", "What the lines of business are charged"]);
        ct.Rows.Add(["License cost", Link("Budget and renewal", "/forecasting/budget"), "The contracts' seats × each contract's price", "What is contracted"]);
        Panels.Add(new("Copilot's cost a month, five ways", ct.Render(),
            "They differ on purpose: seats bought, seats held, the days of a period, what is charged back and what is contracted. A reservation can bill more than is bought (an organization over its reservation) or less (reserved seats nobody holds are still charged)."));

        var ft = new Table { Headers = ["Figure", "How it is worked out", "Where"], Sortable = false };
        foreach (var f in Formulas) ft.Rows.Add([f.Figure, f.How, f.Where]);
        Panels.Add(new("How each figure is worked out", ft.Render(),
            "Each figure on a page says the same under \"How it is worked out\", with the values in it. Every figure is a month unless it says otherwise."));
    }
}
