using CopilotInsights.Data;
using CopilotInsights.Infrastructure;
using Microsoft.AspNetCore.Html;

namespace CopilotInsights.Pages.Leadership;

/// <summary>
/// What the other reports say to do, on one page: each finding with how many it covers, why it matters, and the report
/// that has the detail. Counts only; the names behind a count stay on the report it links to, with that report's role.
/// A finding of none is left out, so an empty page is good news.
/// </summary>
public sealed class ActOnModel(PageServices s, Metrics metrics, OpsReports reports, CostData costs) : InsightsPage(s)
{
    public override string Title => "What to act on";
    public override string Persona => "Leadership";
    public override string Intro => "What the other reports say to do, in one list: each finding with how many it covers, why it matters and where the detail is. Counts only: no one is named.";

    protected override async Task BuildAsync()
    {
        var offered = Nav.For(Viewer, PrivacyMode, hidden: HiddenReports).SelectMany(g => g.Pages.Select(p => (Group: g, Page: p))).ToList();
        /* Where the detail is: a link when this viewer may open the report, else its name and the role it needs. */
        object Where(string path)
        {
            if (offered.FirstOrDefault(x => x.Page.Path == path) is { Page: not null } hit)
                return new Link($"{hit.Group.Persona} › {hit.Page.Title}", Ctx.Url(path));
            var g = Nav.Groups.FirstOrDefault(x => x.Pages.Any(p => p.Path == path));
            var page = g?.Pages.First(p => p.Path == path);
            var role = page?.Role ?? g?.Role;
            return g is null ? "" : $"{g.Persona} › {page!.Title}" + (role is null ? "" : $" (needs the {role} role)");
        }

        var t = new Table { Headers = ["Finding", "How many", "Why it matters", "Where the detail is"], Numeric = new HashSet<int> { 1 }, Sortable = false,
            EmptyText = "Nothing to act on in this part of the organization: no idle licenses, nobody waiting for one, no target behind." };
        void Add(string finding, int count, string why, string path) { if (count > 0) t.Rows.Add([finding, count, why, Where(path)]); }

        var counts = await reports.ActionCountsAsync(Ctx);
        int revoke = counts.GetValueOrDefault("revoke"), train = counts.GetValueOrDefault("train"), upgrade = counts.GetValueOrDefault("upgrade");
        Kpis.Add(new("Licenses to take back", Num(revoke), "license holders with no licensed use in the last 30 days", Href: Open("/forecasting/actions", extra: "kind=revoke")));
        Kpis.Add(new("License holders to train", Num(train), "license holders who use Copilot only a little", Href: Open("/forecasting/actions", extra: "kind=train")));
        Kpis.Add(new("People ready for a license", Num(upgrade), "regular users of the free Copilot Chat who hold no license", Href: Open("/forecasting/actions", extra: "kind=upgrade")));

        Add("License holders with no licensed use in the last 30 days", revoke,
            "Each is a license paid for and not used: take it back, or find out what is in the way.", "/forecasting/actions");
        Add("License holders who use Copilot only a little", train,
            "A short session or a champion usually turns light use into a habit.", "/forecasting/actions");
        Add("Regular users of the free Copilot Chat without a license", upgrade,
            "They have shown they will use it: the first people to give a freed license to.", "/forecasting/actions");

        var nonHuman = (await NonHuman.AllAsync(Db, S.Settings, Ctx.Scope("ld.UserKey"), Ctx.Args())).Count;
        Add("Copilot licenses on accounts that are not a person", nonHuman,
            "A policy breach, and a license nobody can use: service, shared, test and guest accounts.", "/licensing/non-person");

        /* The units one level below with the lowest adoption, among those large enough to show. */
        var units = SmallGroups.Shown(await metrics.UnitsAsync(Ctx), u => u.People, Ctx.MinGroupSize).Where(u => u.Licensed >= Ctx.MinGroupSize).ToList();
        if (units.Count > 1)
        {
            var overall = units.Sum(u => u.Licensed) is var l and > 0 ? units.Sum(u => u.ActiveLicensed) / (double)l : 0;
            var behind = units.Where(u => u.ActiveLicensed / (double)u.Licensed < overall - 0.10).OrderBy(u => u.ActiveLicensed / (double)u.Licensed).ToList();
            if (behind.Count > 0)
                t.Rows.Add([$"Parts of the organization more than 10 points under the adoption of {Here} ({Pct(overall)})", behind.Count,
                    "Lowest first: " + string.Join("; ", behind.Take(3).Select(u => $"{u.Name} {Pct(u.ActiveLicensed / (double)u.Licensed)}")) + (behind.Count > 3 ? "; and others." : "."),
                    Where("/leadership/org")]);
        }

        var targets = await reports.TargetsAsync(Ctx);
        Add("Adoption targets behind their line or missed", targets.Count(x => x.Status is "Behind" or "Missed"),
            "A target behind its line needs its push now, not at the deadline.", "/adoption/targets");

        /* What could be saved (2.6.6): Costs and savings is the organization's, so its findings show to those who see the
           whole of it, or hold the Licensing role, and only when the page shows the whole organization. */
        if (Ctx.Root is null && (Viewer.OrgWide || Viewer.Has(Roles.Licensing)))
        {
            var v = await costs.BuildAsync(Ctx);
            var save = v.Opportunities.Where(o => o.Kind == OpportunityKind.Save).ToList();
            if (save.Count > 0)
                t.Rows.Add([$"Savings found in AI and automation: {CostRules.Money(Math.Round(v.Saving), v.Currency)} a month", save.Count,
                    $"{v.Saving / Math.Max(1, v.Spend):P0} of the spend. The largest: {save[0].Title} ({CostRules.Money(Math.Round(save[0].Saving), v.Currency)} a month).", Where("/leadership/costs")]);
            if (v.Suites is { Saving: > 0 } su)
                t.Rows.Add([$"Suite moves to review: {CostRules.Money(Math.Round(su.Saving), v.Currency)} a month", su.Moves.Sum(m => m.People),
                    "E7 to E5, E5 to E3, second suites, unassigned seats: each with what to check before moving.", Where("/licensing/suites")]);
        }

        /* The security signals are the Security section's: counted here for its holders only, as on the board pack. */
        if (Viewer.Has(Roles.Security))
        {
            var tr = await metrics.TrendAsync(Ctx);
            Add("Jailbreak attempts and prompt injections in the period", (int)Math.Min(int.MaxValue, tr.Sum(x => x.Jailbreak) + tr.Sum(x => x.Injection)),
                "Each is flagged by Microsoft in the audit record, and waits for triage.", "/security/flags");
        }

        CsvTable = t;
        Panels.Add(new("Findings", t.Render(),
            $"For {Here}. The three license findings look at the last 30 days whatever the period chosen, as Forecasting › Upgrade, train or revoke does; the rest follow the period, {Ctx.PeriodLabel}. " +
            "A finding with nothing to report is left out. A report you cannot open is named with the role it needs.", "full", CsvUrl()));
    }
}
