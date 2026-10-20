using System.Net;
using CopilotInsights.Data;
using CopilotInsights.Infrastructure;
using Microsoft.AspNetCore.Html;

namespace CopilotInsights.Pages.Licensing;

/// <summary>
/// Which suite fits each holder (2.6.6). With E3, E5 and E7, saving on a license is seldom revoking it: the Copilot in
/// E7 cannot be taken off alone, and E5's Power BI Pro is paid for whether used or not. The choice is the suite: E7 to
/// E5 for someone who does not use Copilot, E5 to E3 (with Power BI Pro, or without) for someone E5's extras do not
/// serve, a copy held beside the suite to drop, or training before any of it. The rules are <see cref="SuiteRules"/>;
/// each move names what the data cannot see and must be checked first.
/// </summary>
public sealed class SuitesModel(PageServices s, SuiteData suites) : InsightsPage(s)
{
    public override string Title => "Suite fit";
    public override string Persona => "Licensing";
    public override string? RequiredRole => Roles.Licensing;
    public override string? OwnWindow => "the last 30 days, and today's licenses";
    public override string Intro => "Who holds Microsoft 365 or Office 365 E3, E5 or E7, and which suite fits them better, from their Copilot and Power BI use in the last 30 days. "
        + "A move down saves the difference in price, but each suite brings more than the data can see (security, compliance, telephony, Agent 365), so every move says what to check first. "
        + "Suites, their prices and what they include are set in Admin > Targets and assumptions.";

    static readonly Dictionary<string, string> Sorts = new()
    {
        ["saving"] = "saving", ["name"] = "name", ["dept"] = "dept", ["suite"] = "suite", ["copilot"] = "copilot", ["move"] = "move", ["upn"] = "upn",
    };
    static readonly Dictionary<string, string> SortLabels = new() { ["saving"] = "Largest saving first", ["name"] = "Name", ["dept"] = "Department", ["suite"] = "Suite" };

    protected override async Task BuildAsync()
    {
        var v = await suites.FitAsync(Ctx, scoped: true);
        if (v.Suites.Count == 0)
        {
            Notice = "No suites are set: run the 2.6.6 dashboard schema (sql\\Create-CopilotDashboardSchema.sql), which adds E3, E5 and E7, or turn them on in Admin > Targets and assumptions.";
            return;
        }
        if (v.Rows.Count == 0)
        {
            Notice = "Nobody here holds a suite the list knows, or the licenses each person holds have not been loaded yet. They fill on the next license lookup (tools\\Update-CopilotAuditEnrichment.ps1).";
            return;
        }
        var cur = v.Currency;
        string M(decimal x) => CostRules.Money(Math.Round(x, 0), cur);
        var moves = v.Rows.Where(r => r.Fit.Move is not SuiteMove.Keep).ToList();
        /* Summed as the Suites card on Costs and savings sums them (SuiteData.Summarize), so the two pages agree: a copy of
           the Copilot add-on or of Power BI Pro is an AI saving, counted with its area there, and shown apart here. */
        var summary = SuiteData.Summarize(v, 0, decimal.TryParse(await S.Settings.StringAsync("SpareSeatPercent"), System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var sparePct) ? sparePct : 10m);   // 2.6.9: seats kept spare on purpose
        /* The add-on a suite covers, as Costs and savings counts it (SuiteData.AddOnCopy), so the two pages agree. */
        var today = Ctx.End.Date;
        decimal CopyWorth(SuiteRow r) { var (cp, pb) = SuiteData.AddOnCopy(r, today); return (cp ? v.CopilotPrice : 0) + (pb ? v.PowerBiPrice : 0); }
        var copies = v.Rows.Where(r => SuiteData.AddOnCopy(r, today) is (true, _) or (_, true)).ToList();

        Kpis.Add(new("Suite holders", Num(v.Rows.Count), $"{Count(v.Rows.Select(r => r.Holder.Suite.Name).Distinct().Count(), "suite")} held"));
        Kpis.Add(new($"Suite moves to review a month ({cur})", Num((long)Math.Round(summary.Saving)), $"{M(summary.Saving * 12)} a year; outside Costs and savings' AI savings", summary.Saving > 0 ? "good" : null,
            Why: await WhyAsync("For each holder a lower suite would serve: the suite's price less the lower one's, a month, as set in Suites: E3, E5 and E7. Moves to train save nothing until decided.",
                [.. v.Suites.Select(su => "Suite:" + su.Pattern).Distinct()])));
        Kpis.Add(new($"Licenses held twice ({cur})", Num((long)Math.Round(copies.Sum(CopyWorth))), $"{Count(copies.Count, "person", "people")} with an add-on the suite covers: AI savings on Costs and savings",
            Href: Open(Request.Path, extra: "copy=addon"),
            Why: await WhyAsync($"For each holder whose suite already includes an add-on they hold: the Copilot license price ({v.CopilotPrice:#,0.##} {cur}) where it is Copilot, plus Power BI Pro's ({v.PowerBiPrice:#,0.##} {cur}) where it is Power BI.",
                "LicenseMonthlyCost", "Price:POWER_BI_PRO")));
        Kpis.Add(new("To train before deciding", Num(moves.Count(r => r.Fit.Move == SuiteMove.Train)), "E7 holders who barely use Copilot", Href: Open(Request.Path, extra: "move=train")));
        /* The suites held, what each costs and includes. */
        var st = new Table { Headers = ["Suite", "Holders", $"Price a month ({cur})", "Includes, so not counted as AI spend", "Also brings"], Numeric = new HashSet<int> { 1, 2 } };
        foreach (var g in v.Rows.GroupBy(r => r.Holder.Suite.Name).OrderByDescending(g => g.First().Holder.Suite.Tier).ThenBy(g => g.Key))
        {
            var s = g.First().Holder.Suite;
            st.Rows.Add([new Link(s.Name, Ctx.Url(Request.Path, extra: $"suite={Uri.EscapeDataString(s.Name)}")), g.Count(), s.Price, Includes(s), s.Covers]);
        }
        Panels.Add(new("Suites held", st.Render(), "Every suite turned on in Admin, with its holders here and its price. What it includes (Copilot, Power BI Pro) is not counted again as AI spend on Costs and savings; the rest of what it brings cannot be split from its price.", "half"));

        /* The moves, summed as the Suites card sums them, each leading to its people on that suite. */
        var mt = new Table { Headers = ["Move", "From", "To", "People or seats", $"A month ({cur})", "Confidence"], Numeric = new HashSet<int> { 3, 4 } };
        string MoveUrl(SuiteMove mv, string from) => Ctx.Url(Request.Path, extra: (mv == SuiteMove.DropDuplicate ? "copy=suite" : $"move={mv.ToString().ToLowerInvariant()}") + $"&suite={Uri.EscapeDataString(from)}");
        foreach (var ml in summary.Moves)
            mt.Rows.Add([ml.Move == SuiteMove.CutSeats ? SuiteRules.Label(ml.Move) : new Link(ml.Move == SuiteMove.DropDuplicate ? "Remove the second suite" : SuiteRules.Label(ml.Move), MoveUrl(ml.Move, ml.From)),
                ml.From, ml.To, ml.People, Math.Round(ml.Move == SuiteMove.Train ? ml.AtStake : ml.Saving), ml.Confidence]);
        if (copies.Count > 0)
            mt.Rows.Add([new Link("Remove the add-on the suite covers", Ctx.Url(Request.Path, extra: "copy=addon")), "E5 or E7", "(an AI saving)", copies.Count, Math.Round(copies.Sum(CopyWorth)), "High"]);
        Panels.Add(new("What could change", mt.Rows.Count == 0 ? new HtmlString("<p class=\"muted\">Every holder's suite fits what the data shows of them.</p>") : mt.Render(),
            "Each move with its people (or, to cut, its unassigned seats) and what it saves a month; to train, what rides on it. High: a license held twice. Medium: a whole part of the suite unused, or seats nobody holds. Low: rests on what the data cannot see. " +
            "The add-on a suite covers is an AI saving, counted on Costs and savings; the rest is the Suites card's review. Select one to list its people.", "half"));
        if (!v.PowerBiKnown && v.Rows.Any(r => r.Holder.Suite.HasPowerBi && !r.Holder.Suite.HasCopilot))
            Notice = "Power BI activity is not loaded, so whether E5's Power BI Pro is used is not known, and no E5 holder is offered a move. Load it with tools\\Import-CostCsv.ps1 -Table PowerPlatformUserActivity.";

        string CopyWhy(SuiteRow r) => SuiteData.AddOnCopy(r, today) switch
        {
            (true, true) => $"{r.Holder.Suite.Name} includes Microsoft 365 Copilot and Power BI Pro, and both are held as add-ons as well.",
            (true, _) => $"{r.Holder.Suite.Name} includes Microsoft 365 Copilot, and the add-on is held as well.",
            _ => $"{r.Holder.Suite.Name} includes Power BI Pro, and it is held as an add-on as well.",
        };

        /* The people. */
        var move = Enum.TryParse<SuiteMove>(Request.Query["move"].FirstOrDefault(), true, out var mv) ? mv : (SuiteMove?)null;
        var pg = new Paging(Request, Sorts, "saving");
        /* A suite chosen (?suite=, from its row above or from Costs and savings): every holder of it, whatever fits them. */
        var suite = Request.Query["suite"].FirstOrDefault() is { Length: > 0 } sn && v.Rows.Any(r => r.Holder.Suite.Name == sn) ? sn : null;
        /* A copy chosen (?copy=addon or suite, from the figures above or Costs and savings): the people holding one. */
        var copy = Request.Query["copy"].FirstOrDefault() is "addon" or "suite" ? Request.Query["copy"].ToString() : null;
        IEnumerable<SuiteRow> shown = copy == "addon" ? copies
            : copy == "suite" ? v.Rows.Where(r => r.Fit.Move == SuiteMove.DropDuplicate && r.Holder.Others.Any(o => o.Tier <= r.Holder.Suite.Tier))
            : move is { } only ? v.Rows.Where(r => r.Fit.Move == only) : suite is null ? moves : v.Rows;
        if (suite is not null) shown = shown.Where(r => r.Holder.Suite.Name == suite);
        if (pg.Search is { } q)
            shown = shown.Where(r => (r.Name ?? "").Contains(q, StringComparison.OrdinalIgnoreCase) || r.Upn.Contains(q, StringComparison.OrdinalIgnoreCase) || (r.Department ?? "").Contains(q, StringComparison.OrdinalIgnoreCase));
        var ordered = (pg.SortKey switch
        {
            "name" => shown.OrderBy(r => r.Name ?? r.Upn, StringComparer.CurrentCulture),
            "dept" => shown.OrderBy(r => r.Department ?? "", StringComparer.CurrentCulture).ThenBy(r => r.Name ?? r.Upn),
            "suite" => shown.OrderByDescending(r => r.Holder.Suite.Tier).ThenBy(r => r.Name ?? r.Upn),
            "copilot" => shown.OrderByDescending(r => r.Holder.CopilotDays).ThenBy(r => r.Name ?? r.Upn),
            "move" => shown.OrderBy(r => r.Fit.Move).ThenBy(r => r.Name ?? r.Upn),
            "upn" => shown.OrderBy(r => r.Upn, StringComparer.OrdinalIgnoreCase),
            _ => copy == "addon" ? shown.OrderByDescending(CopyWorth).ThenBy(r => r.Name ?? r.Upn) : shown.OrderByDescending(r => r.Fit.Saving + r.Fit.AtStake).ThenBy(r => r.Name ?? r.Upn),
        }).ToList();
        if (pg.Reversed) ordered.Reverse();
        pg.Total = ordered.Count;
        var page = pg.ForExport ? ordered : ordered.Skip((pg.Page - 1) * Paging.PageSize).Take(Paging.PageSize).ToList();

        var t = new Table
        {
            Headers = ["Name", "Sign-in", "Department", "Suite", "Copilot days (30)", "Power BI", "Move", "To", $"A month ({cur})", "Confidence", "Why, and what to check"],
            Numeric = new HashSet<int> { 4, 8 },
            EmptyText = move is null ? "Every holder's suite fits what the data shows of them." : "Nobody for this move.",
            Server = new(pg, Request, ["name", "upn", "dept", "suite", "copilot", null, "move", null, "saving", null, null]),
        };
        foreach (var r in page)
            t.Rows.Add([r.Name, r.Upn, r.Department, r.Holder.Suite.Name, r.Holder.CopilotDays,
                r.Holder.UsesPowerBi is null ? "Not loaded" : r.Holder.UsesPowerBi.Value ? "Used" : "Not used",
                /* Listing the add-on copies: what dropping the copy saves, which is what the figure above sums. */
                copy == "addon" ? "Remove the add-on" : SuiteRules.Label(r.Fit.Move), copy == "addon" ? "" : r.Fit.To,
                Math.Round(copy == "addon" ? CopyWorth(r) : r.Fit.Move == SuiteMove.Train ? r.Fit.AtStake : r.Fit.Saving), copy == "addon" ? "High" : r.Fit.Confidence,
                new HtmlString(copy == "addon" ? WebUtility.HtmlEncode(CopyWhy(r))
                    : WebUtility.HtmlEncode(r.Fit.Why) + (r.Fit.Check.Length > 0 ? $"<span class=\"cellnote\">Check: {WebUtility.HtmlEncode(r.Fit.Check)}</span>" : ""))]);
        CsvTable = t;
        var all = move is null && suite is null && copy is null ? "" : $"<p class=\"sub\"><a href=\"{WebUtility.HtmlEncode(Ctx.Url(Request.Path))}\">Every move, every suite</a></p>";
        Panels.Add(new(copy == "addon" ? "People with an add-on their suite covers" : copy == "suite" ? $"People with a second suite{(suite is null ? "" : $" beside {suite}")}" : move is { } m ? $"People to {SuiteRules.Label(m).ToLowerInvariant()}{(suite is null ? "" : $" on {suite}")}" : suite is not null ? $"Every holder of {suite}" : "People whose suite could change",
            new HtmlString(all + pg.Controls(Request, SortLabels).Value + t.Render().Value),
            "Copilot days: days in the last 30 with use that needs the Copilot license, as Upgrade, train or revoke counts them. Power BI: any use in the window, from the Power Platform activity export. " +
            "Protected levels (Admin > Access) are never offered a move, nor a copy to drop; a disabled account's licenses are reclaimed whatever its level. Agree each move with the person's manager, and with security and compliance for any move off E5.", "full", CsvUrl()));
    }

    static string Includes(Suite s) => string.Join(", ", new[] { s.HasCopilot ? "Microsoft 365 Copilot" : null, s.HasPowerBi ? "Power BI Pro" : null }.OfType<string>()) is { Length: > 0 } x ? x : "Neither Copilot nor Power BI Pro";
}
