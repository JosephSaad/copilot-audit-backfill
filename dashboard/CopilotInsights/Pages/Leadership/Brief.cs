using System.Net;
using CopilotInsights.Data;
using CopilotInsights.Infrastructure;
using Microsoft.AspNetCore.Html;

namespace CopilotInsights.Pages.Leadership;

/// <summary>
/// The daily brief: an executive's first page of the day. The last seven days against the seven before (people using
/// Copilot, interactions, agents), what AI costs a month and returns, the AI bill this month against its forecast and
/// budgets, the money found, what is out of control, and the three biggest savings to act on, each opening the page
/// that shows the same number. It reads what the other pages keep (the cost model, the ledger, the per-person totals),
/// so it opens as fast as they do.
/// </summary>
public sealed class BriefModel(PageServices s, Metrics metrics, OpsReports reports, OpsData ops, CostData costs) : InsightsPage(s)
{
    public override string Title => "Daily brief";
    public override string Persona => "Leadership";
    public override bool UsesPeriod => false;
    public override string Intro => "The week in AI on one page: use against the week before, what AI costs and returns, this month's AI bill against its budgets, money found, what is out of control and the savings to act on first. "
        + "Each figure opens the page behind it.";

    protected override async Task BuildAsync()
    {
        var end = Ctx.End;
        var week = OpsReports.Window(Ctx, end, 7);
        var before = OpsReports.Window(Ctx, end.AddDays(-7), 7);
        var firstTask = OpsReports.FirstDayAsync(Db);
        var nowTask = metrics.TotalsAsync(week);
        var costTask = Viewer.OrgWide || Viewer.Has(Roles.Licensing) ? costs.BuildAsync(Ctx) : null;
        var ledgerTask = Viewer.OrgWide || Viewer.Has(Roles.Licensing) ? SavingLedger.ReadAsync(Db) : null;
        var leaversTask = Open("/licensing/leavers") is not null ? ops.LeaversAsync(Ctx.Root) : null;
        var countsTask = reports.ActionCountsAsync(Ctx);
        var agentsTask = Db.QueryAsync<(int New, int Before)>(@"
SELECT COUNT(CASE WHEN FirstSeenUtc >= @w0 THEN 1 END), COUNT(CASE WHEN FirstSeenUtc >= @b0 AND FirstSeenUtc < @w0 THEN 1 END)
FROM dash.Agent WHERE AgentSource <> N'(no agent)' AND FirstSeenUtc >= @b0 AND FirstSeenUtc < @e;", new { w0 = week.Start, b0 = before.Start, e = end.AddDays(1) });
        var first = await firstTask;
        var full = OpsReports.Covers(before, first);
        var beforeTask = full ? metrics.TotalsAsync(before) : null;
        await Task.WhenAll(new Task?[] { nowTask, beforeTask, costTask, ledgerTask, leaversTask, countsTask, agentsTask }.OfType<Task>());
        var now = await nowTask;
        var prev = beforeTask is null ? null : await beforeTask;
        var (newAgents, newBefore) = (await agentsTask).Single();

        string Change(double a, double? b) => b is not double x ? "no full week before to compare" : x == 0 ? "none the week before" : $"{a / x - 1:+0%;-0%;0%} on the week before";
        string? Tone(double a, double? b) => b is double x && x > 0 ? (a / x - 1 >= 0.02 ? "good" : a / x - 1 <= -0.05 ? "warn" : null) : null;

        Kpis.Add(new("People using Copilot this week", Num(now.Active), $"{Change(now.Active, prev?.Active)}; 7 days to {end:d MMM}", Tone(now.Active, prev?.Active), Href: Open("/adoption")));
        Kpis.Add(new("Interactions this week", Num(now.Interactions), Change(now.Interactions, prev?.Interactions), Tone(now.Interactions, prev?.Interactions), Href: Open("/leadership/mix")));
        Kpis.Add(new("Adoption", Pct(Ratio(now.ActiveLicensed, now.LicensedNow)), $"of {now.LicensedNow:#,0} Premium license holders used it with the license this week", Href: Open("/adoption")));
        Kpis.Add(new("New agents this week", Num(newAgents), newBefore > 0 || full ? $"{newBefore:#,0} the week before" : "first used in the 7 days", newAgents > newBefore ? "good" : null, Href: Open("/agents")));

        var lines = new List<string>();
        if (costTask is not null)
        {
            var v = await costTask;
            if (v.Areas.Count > 0)
            {
                Kpis.Add(new($"AI spend a month ({v.Currency})", StartFigures.Money(v.Spend, v.Currency), $"across {Count(v.Areas.Count, "area")}: licenses held and the other sources' last 30 days", "accent", Href: Open("/leadership/costs")));
                if (v.Spend > 0 && v.Value > 0)
                    Kpis.Add(new("Return on the spend", StartFigures.Times(v.Value / v.Spend), $"{StartFigures.Money(v.Value, v.Currency)} a month of time saved, at the hourly cost set in Admin", v.Value >= v.Spend ? "good" : "warn", Href: Open("/leadership/value")));
                foreach (var o in v.Opportunities.Where(o => o.Kind == OpportunityKind.Save && o.Saving > 0).OrderByDescending(o => o.Saving).Take(3))
                    lines.Add($"<li><b>{WebUtility.HtmlEncode(StartFigures.Money(o.Saving, v.Currency))} a month</b>: {WebUtility.HtmlEncode(o.Title)}. {WebUtility.HtmlEncode(o.Action)}"
                              + (o.Href is not null && Open(o.Href) is { } oh ? $" <a href=\"{WebUtility.HtmlEncode(oh)}\">Open</a>" : "") + "</li>");
            }
        }
        if (await AiBillAsync() is { } bill) Kpis.Add(bill);
        if (ledgerTask is not null && await ledgerTask is { } ledger && ledger.SavedToDate > 0)
            Kpis.Add(new($"Saved to date ({ledger.Currency})", StartFigures.Money(ledger.SavedToDate, ledger.Currency), ledger.SavedSince is { } since ? $"since {since:MMM yyyy}, from savings acted on" : "from savings acted on", "good", Href: Open("/leadership/costs")));
        if (leaversTask is not null)
        {
            var n = (await leaversTask).Count;
            Kpis.Add(new("Licenses on disabled accounts", Num(n), n > 0 ? "still paid for: take them back" : "none", n > 0 ? "crit" : "good", Href: Open("/licensing/leavers")));
        }

        /* What to do this week: the three biggest savings, then the action list's counts. */
        var counts = await countsTask;
        foreach (var (key, n) in counts.Where(c => c.Value > 0).OrderByDescending(c => c.Value).Take(4))
            if (Pages.Forecasting.ActionsModel.Kinds.TryGetValue(key, out var k))
                lines.Add($"<li><b>{n:#,0} people</b>: {WebUtility.HtmlEncode(k.Label.ToLowerInvariant())}. {WebUtility.HtmlEncode(k.Action)}"
                          + (Open("/forecasting/actions", $"kind={key}") is { } au ? $" <a href=\"{WebUtility.HtmlEncode(au)}\">Open the list</a>" : "") + "</li>");
        Panels.Add(new("This week, act on", new HtmlString(lines.Count == 0 ? "<p class=\"muted\">Nothing waiting: no saving or action stands out this week.</p>" : "<ol class=\"tips\">" + string.Join("", lines) + "</ol>"),
            "The largest savings Costs and savings finds, then the people What to act on lists, largest first.", "full"));

        var links = new List<string>();
        foreach (var (text, path) in new[] { ("AI spend across Azure", "/leadership/ai-spend"), ("Costs and savings", "/leadership/costs"), ("What to act on", "/leadership/act"), ("Agents in use", "/agents"),
                                             ("Foundry overview", "/platforms/foundry"), ("GitHub Copilot overview", "/platforms/github"), ("Board pack", "/leadership/boardpack") })
            if (Open(path) is { } u) links.Add($"<a class=\"chip\" href=\"{WebUtility.HtmlEncode(u)}\">{WebUtility.HtmlEncode(text)}</a>");
        if (links.Count > 0)
            Panels.Add(new("Go deeper", new HtmlString("<div class=\"chips\">" + string.Join("", links) + "</div>"), "The pages behind these figures you may open.", "full"));
    }

    /// <summary>This month's AI bill in Azure, billed so far and forecast, against the budgets set there: null before the wider bill is loaded.</summary>
    async Task<Kpi?> AiBillAsync()
    {
        if (!(Viewer.OrgWide || Viewer.Has(Roles.Licensing)) || Open("/leadership/ai-spend") is not { } href) return null;
        if (await Db.ScalarAsync<int>("SELECT CASE WHEN OBJECT_ID(N'audit.AiCostDay', N'U') IS NOT NULL AND EXISTS (SELECT 1 FROM audit.AiCostDay) THEN 1 ELSE 0 END;") == 0) return null;
        var today = DateTime.UtcNow.Date;
        var m0 = new DateTime(today.Year, today.Month, 1);
        var r = (await Db.QueryAsync<(decimal? Billed, decimal? Ahead, decimal? Budget, string? Currency)>(@"
SELECT (SELECT SUM(Cost) FROM audit.AiCostDay WHERE CostType = 'Actual' AND [Day] >= @m0),
       (SELECT SUM(Cost) FROM audit.AiCostForecastDay WHERE [Day] >= @t AND [Day] < @m1),
       (SELECT SUM(Amount) FROM audit.AiBudget WHERE Amount > 0 AND TimeGrain = 'Monthly'),
       (SELECT TOP (1) Currency FROM audit.AiCostDay WHERE Currency IS NOT NULL GROUP BY Currency ORDER BY COUNT(*) DESC);", new { m0, t = today, m1 = m0.AddMonths(1) })).Single();
        var cur = r.Currency ?? "USD";
        var month = (r.Billed ?? 0) + (r.Ahead ?? 0);
        var over = r.Budget > 0 && month > r.Budget;
        return new($"AI bill this month ({cur})", StartFigures.Money(month, cur),
            (r.Ahead is null ? "billed so far" : $"{StartFigures.Money(r.Billed ?? 0, cur)} billed, the rest forecast") + (r.Budget > 0 ? $"; monthly budgets {StartFigures.Money(r.Budget.Value, cur)}" : ""),
            over ? "crit" : r.Budget > 0 ? "good" : null, Href: href);
    }
}
