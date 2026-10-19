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
        var saves = moves.Where(r => r.Fit.Saving > 0).ToList();
        var sure = saves.Where(r => r.Fit.Confidence == "High").Sum(r => r.Fit.Saving);

        Kpis.Add(new("Suite holders", Num(v.Rows.Count), $"{Count(v.Rows.Select(r => r.Holder.Suite.Name).Distinct().Count(), "suite")} held"));
        Kpis.Add(new($"Savings to review a month ({cur})", Num((long)Math.Round(saves.Sum(r => r.Fit.Saving))), $"{Count(saves.Count, "person", "people")}; {M(saves.Sum(r => r.Fit.Saving) * 12)} a year", saves.Count > 0 ? "good" : null));
        Kpis.Add(new($"Certain: licenses held twice ({cur})", Num((long)Math.Round(sure)), $"{Count(saves.Count(r => r.Fit.Confidence == "High"), "person", "people")} with a copy the suite covers"));
        Kpis.Add(new("To train before deciding", Num(moves.Count(r => r.Fit.Move == SuiteMove.Train)), "E7 holders who barely use Copilot"));

        /* The suites held, what each costs and includes. */
        var st = new Table { Headers = ["Suite", "Holders", $"Price a month ({cur})", "Includes, so not counted as AI spend", "Also brings"], Numeric = new HashSet<int> { 1, 2 } };
        foreach (var g in v.Rows.GroupBy(r => r.Holder.Suite.Name).OrderByDescending(g => g.First().Holder.Suite.Tier).ThenBy(g => g.Key))
        {
            var s = g.First().Holder.Suite;
            st.Rows.Add([s.Name, g.Count(), s.Price, Includes(s), s.Covers]);
        }
        Panels.Add(new("Suites held", st.Render(), "Every suite turned on in Admin, with its holders here and its price. What it includes (Copilot, Power BI Pro) is not counted again as AI spend on Costs and savings; the rest of what it brings cannot be split from its price.", "half"));

        /* The moves, summed. */
        var mt = new Table { Headers = ["Move", "From", "To", "People", $"A month ({cur})", "Confidence"], Numeric = new HashSet<int> { 3, 4 } };
        foreach (var g in moves.GroupBy(r => (r.Fit.Move, From: r.Holder.Suite.Name, r.Fit.To)).OrderByDescending(g => g.Sum(r => r.Fit.Saving + r.Fit.AtStake)))
        {
            var key = g.Key.Move.ToString().ToLowerInvariant();
            mt.Rows.Add([new HtmlString($"<a href=\"{WebUtility.HtmlEncode(Ctx.Url(Request.Path, extra: $"move={key}"))}\">{WebUtility.HtmlEncode(SuiteRules.Label(g.Key.Move))}</a>"),
                g.Key.From, g.Key.To, g.Count(), Math.Round(g.Sum(r => r.Fit.Move == SuiteMove.Train ? r.Fit.AtStake : r.Fit.Saving)), g.First().Fit.Confidence]);
        }
        Panels.Add(new("What could change", mt.Rows.Count == 0 ? new HtmlString("<p class=\"muted\">Every holder's suite fits what the data shows of them.</p>") : mt.Render(),
            "Each move with its people and what it saves a month (to train: what rides on it). High: a license held twice. Medium: a whole part of the suite unused. Low: rests on what the data cannot see. Select one to list its people.", "half"));

        if (!v.PowerBiKnown && v.Rows.Any(r => r.Holder.Suite.HasPowerBi && !r.Holder.Suite.HasCopilot))
            Notice = "Power BI activity is not loaded, so whether E5's Power BI Pro is used is not known, and no E5 holder is offered a move. Load it with tools\\Import-CostCsv.ps1 -Table PowerPlatformUserActivity.";

        /* The people. */
        var move = Enum.TryParse<SuiteMove>(Request.Query["move"].FirstOrDefault(), true, out var mv) ? mv : (SuiteMove?)null;
        var pg = new Paging(Request, Sorts, "saving");
        IEnumerable<SuiteRow> shown = move is { } only ? v.Rows.Where(r => r.Fit.Move == only) : moves;
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
            _ => shown.OrderByDescending(r => r.Fit.Saving + r.Fit.AtStake).ThenBy(r => r.Name ?? r.Upn),
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
                SuiteRules.Label(r.Fit.Move), r.Fit.To, Math.Round(r.Fit.Move == SuiteMove.Train ? r.Fit.AtStake : r.Fit.Saving), r.Fit.Confidence,
                new HtmlString(WebUtility.HtmlEncode(r.Fit.Why) + (r.Fit.Check.Length > 0 ? $"<span class=\"cellnote\">Check: {WebUtility.HtmlEncode(r.Fit.Check)}</span>" : ""))]);
        CsvTable = t;
        var all = move is null ? "" : $"<p class=\"sub\"><a href=\"{WebUtility.HtmlEncode(Ctx.Url(Request.Path))}\">Every move</a></p>";
        Panels.Add(new(move is { } m ? $"People to {SuiteRules.Label(m).ToLowerInvariant()}" : "People whose suite could change",
            new HtmlString(all + pg.Controls(Request, SortLabels).Value + t.Render().Value),
            "Copilot days: days in the last 30 with use that needs the Copilot license, as Upgrade, train or revoke counts them. Power BI: any use in the window, from the Power Platform activity export. " +
            "Protected levels (Admin > Access) are never offered a move; a copy held twice is shown for everyone. Agree each move with the person's manager, and with security and compliance for any move off E5.", "full", CsvUrl()));
    }

    static string Includes(Suite s) => string.Join(", ", new[] { s.HasCopilot ? "Microsoft 365 Copilot" : null, s.HasPowerBi ? "Power BI Pro" : null }.OfType<string>()) is { Length: > 0 } x ? x : "Neither Copilot nor Power BI Pro";
}
