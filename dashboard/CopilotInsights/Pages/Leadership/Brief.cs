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
        Kpis.Add(new("Interactions this week", Num(now.Interactions), Change(now.Interactions, prev?.Interactions), Tone(now.Interactions, prev?.Interactions), Href: Open("/leadership", null, true, 7)));
        Kpis.Add(new("Adoption", Pct(Ratio(now.ActiveLicensed, now.LicensedNow)), $"of {now.LicensedNow:#,0} Premium license holders used it with the license this week", Href: Open("/leadership", null, true, 7)));
        Kpis.Add(new("New agents this week", Num(newAgents), newBefore > 0 || full ? $"{newBefore:#,0} the week before" : "first used in the 7 days", newAgents > newBefore ? "good" : null, Href: Open("/agents")));

        var lines = new List<string>();
        if (costTask is not null)
        {
            var v = await costTask;
            if (v.Areas.Count > 0)
            {
                Kpis.Add(new($"AI spend a month ({v.Currency})", StartFigures.Money(v.Spend, v.Currency), $"across {Count(v.Areas.Count, "area")}: licenses held and the other sources' last 30 days", "accent", Href: Open("/leadership/costs")));
                if (v.Spend > 0 && v.Value > 0)
                    Kpis.Add(new("Return on the spend", StartFigures.Times(v.Value / v.Spend), $"{StartFigures.Money(v.Value, v.Currency)} a month of time saved, at the hourly cost set in Admin", v.Value >= v.Spend ? "good" : "warn", Href: Open("/leadership/costs")));
                foreach (var o in v.Opportunities.Where(o => o.Kind == OpportunityKind.Save && o.Saving > 0).OrderByDescending(o => o.Saving).Take(3))
                    lines.Add($"<li><b>{WebUtility.HtmlEncode(StartFigures.Money(o.Saving, v.Currency))} a month</b>: {WebUtility.HtmlEncode(o.Title)}. {WebUtility.HtmlEncode(o.Action)}"
                              + (o.Href is not null && Open(o.Href) is { } oh ? $" <a href=\"{WebUtility.HtmlEncode(oh)}\">Open</a>" : "") + "</li>");
            }
        }
        if (await AiBillAsync() is { } bill) Kpis.Add(bill);
        /* A budget set in Azure that its forecast takes over: first among the things to act on. */
        if ((Viewer.OrgWide || Viewer.Has(Roles.Licensing)) && Open("/leadership/ai-spend") is { } spendUrl && await TableHasRowsAsync("audit.AiBudget"))
            foreach (var b in await Db.QueryAsync<(string Budget, decimal Amount, decimal Forecast, string? Currency)>(
                "SELECT TOP (3) Budget, Amount, ForecastSpend, Currency FROM audit.AiBudget WHERE Amount > 0 AND ForecastSpend > Amount ORDER BY ForecastSpend - Amount DESC;"))
                lines.Insert(0, $"<li><b>Budget {WebUtility.HtmlEncode(b.Budget)}</b> is forecast to reach {WebUtility.HtmlEncode(StartFigures.Money(b.Forecast, b.Currency ?? "USD"))} against "
                              + $"{WebUtility.HtmlEncode(StartFigures.Money(b.Amount, b.Currency ?? "USD"))}: find what is growing before the period ends. <a href=\"{WebUtility.HtmlEncode(spendUrl)}\">AI spend across Azure</a></li>");
        if (ledgerTask is not null && await ledgerTask is { } ledger && ledger.SavedToDate > 0)
            Kpis.Add(new($"Saved to date ({ledger.Currency})", StartFigures.Money(ledger.SavedToDate, ledger.Currency), ledger.SavedSince is { } since ? $"since {since:MMM yyyy}, from savings acted on" : "from savings acted on", "good", Href: Open("/leadership/costs")));
        if (leaversTask is not null)
        {
            var n = (await leaversTask).Count;
            Kpis.Add(new("Licenses on disabled accounts", Num(n), n > 0 ? "still paid for: take them back" : "none", n > 0 ? "crit" : "good", Href: Open("/licensing/leavers")));
        }

        /* What to do this week: the three biggest savings, then the action list's counts. */
        var counts = await countsTask;
        var said = string.Join(" ", lines);
        foreach (var (key, n) in counts.Where(c => c.Value > 0).OrderByDescending(c => c.Value).Take(4))
        {
            /* A list the savings above already name (the unused licenses to revoke) is not said twice. */
            if (!Says.TryGetValue(key, out var what) || key == "revoke" && said.Contains("unused Copilot licenses", StringComparison.OrdinalIgnoreCase)) continue;
            lines.Add($"<li><b>{n:#,0} {(n == 1 ? "person" : "people")}</b> {WebUtility.HtmlEncode(what)}"
                      + (Open("/forecasting/actions", $"kind={key}") is { } au ? $" <a href=\"{WebUtility.HtmlEncode(au)}\">Open the list</a>" : "") + "</li>");
        }
        await SinceYesterdayAsync();
        Panels.Add(new("This week, act on", new HtmlString(lines.Count == 0 ? "<p class=\"muted\">Nothing waiting: no saving or action stands out this week.</p>" : "<ol class=\"tips\">" + string.Join("", lines) + "</ol>"),
            "The largest savings Costs and savings finds, then the people What to act on lists, largest first.", "full", Email: true));

        var links = new List<string>();
        foreach (var (text, path) in new[] { ("AI spend across Azure", "/leadership/ai-spend"), ("Costs and savings", "/leadership/costs"), ("What to act on", "/leadership/act"), ("Agents in use", "/agents"),
                                             ("Foundry overview", "/platforms/foundry"), ("GitHub Copilot overview", "/platforms/github"), ("Board pack", "/leadership/boardpack") })
            if (Open(path) is { } u) links.Add($"<a class=\"chip\" href=\"{WebUtility.HtmlEncode(u)}\">{WebUtility.HtmlEncode(text)}</a>");
        if (links.Count > 0)
            Panels.Add(new("Go deeper", new HtmlString("<div class=\"chips\">" + string.Join("", links) + "</div>"), "The pages behind these figures you may open.", "full"));
    }

    /// <summary>
    /// Since yesterday (3.4.0): the newest whole day against the same weekday a week before (people and interactions,
    /// from the per-person days the refresh keeps), the agents first used that day, the security signals that day for
    /// those who see them, and the AI bill's newest day against the seven before. Each a line; nothing when it is quiet.
    /// </summary>
    async Task SinceYesterdayAsync()
    {
        var day = Ctx.End;
        var k = ReportContext.Key(day); var kWeek = ReportContext.Key(day.AddDays(-7));
        var useTask = Db.QueryAsync<(int People, long N, int PeopleBefore, long NBefore, long Risky)>($@"
SELECT COUNT(CASE WHEN ud.DateKey = @k AND ud.Interactions > 0 THEN 1 END), ISNULL(SUM(CASE WHEN ud.DateKey = @k THEN CAST(ud.Interactions AS BIGINT) END), 0),
       COUNT(CASE WHEN ud.DateKey = @kw AND ud.Interactions > 0 THEN 1 END), ISNULL(SUM(CASE WHEN ud.DateKey = @kw THEN CAST(ud.Interactions AS BIGINT) END), 0),
       ISNULL(SUM(CASE WHEN ud.DateKey = @k THEN ud.Jailbreak + ud.PromptInjection END), 0)
FROM dash.UserDay AS ud {Ctx.Scope("ud.UserKey")} WHERE ud.DateKey IN (@k, @kw);", Ctx.Args(new { k, kw = kWeek }));
        var agentsTask = Db.QueryAsync<string>(@"SELECT TOP (5) AgentName FROM dash.Agent WHERE AgentSource <> N'(no agent)' AND FirstSeenUtc >= @d AND FirstSeenUtc < @n ORDER BY AgentName;",
            new { d = day, n = day.AddDays(1) });
        await Task.WhenAll(useTask, agentsTask);
        var u = (await useTask).Single();
        var lines = new List<string>();
        string Move(double now, double before) => before <= 0 ? "" : $" ({now / before - 1:+0%;-0%;0%} on {day.AddDays(-7):dddd d MMM})";
        lines.Add($"<li><b>{Num(u.People)} people</b> used Copilot on {day:dddd d MMM}{Move(u.People, u.PeopleBefore)}, with {Num(u.N)} interactions{Move(u.N, u.NBefore)}.</li>");
        var agents = await agentsTask;
        if (agents.Count > 0)
            lines.Add($"<li><b>{Count(agents.Count, "agent")} first used</b> that day: {WebUtility.HtmlEncode(string.Join(", ", agents))}." +
                      (Open("/agents") is { } au ? $" <a href=\"{WebUtility.HtmlEncode(au)}\">Agents in use</a>" : "") + "</li>");
        if (Viewer.Has(Roles.Security) && u.Risky > 0)
            lines.Add($"<li><b>{Num(u.Risky)} jailbreak or prompt injection attempts</b> that day." +
                      (Open("/security") is { } su ? $" <a href=\"{WebUtility.HtmlEncode(su)}\">Security</a>" : "") + "</li>");
        if ((Viewer.OrgWide || Viewer.Has(Roles.Licensing)) && Open("/leadership/ai-spend") is { } spend && await TableHasRowsAsync("audit.AiCostDay"))
        {
            var bill = (await Db.QueryAsync<(DateTime? Last, decimal Cost, decimal Before, string? Currency)>(@"
DECLARE @l DATE = (SELECT DATEADD(DAY, -1, MAX([Day])) FROM audit.AiCostDay WHERE CostType = 'Actual');   -- the newest whole day: Azure is still filling in the last
SELECT CAST(@l AS DATETIME2(0)), ISNULL(SUM(CASE WHEN [Day] = @l THEN Cost END), 0), ISNULL(SUM(CASE WHEN [Day] < @l THEN Cost END), 0) / 7, MAX(Currency)
FROM audit.AiCostDay WHERE CostType = 'Actual' AND [Day] BETWEEN DATEADD(DAY, -7, @l) AND @l;")).Single();
            if (bill.Last is { } last && bill.Cost > 0)
                lines.Add($"<li><b>{WebUtility.HtmlEncode(StartFigures.Money(bill.Cost, bill.Currency ?? "USD"))}</b> billed for AI services in Azure on {last:dddd d MMM}" +
                          (bill.Before > 0 ? $" ({(double)(bill.Cost / bill.Before) - 1:+0%;-0%;0%} on the 7 days' average)" : "") + $". <a href=\"{WebUtility.HtmlEncode(spend)}\">AI spend across Azure</a></li>");
        }
        Panels.Add(new("Since yesterday", new HtmlString("<ul class=\"tips\">" + string.Join("", lines) + "</ul>"),
            $"The newest whole day the reports hold, {day:d MMM}, against the same weekday a week before; Azure bills a day 8 to 24 hours after it.", "full", Email: true));
    }

    /// <summary>What each of What to act on's lists asks, as a line after its count.</summary>
    static readonly Dictionary<string, string> Says = new()
    {
        ["upgrade"] = "use the free Copilot Chat most days: ready for a Premium license.",
        ["train"] = "hold a Premium license and barely use it: train them, then review.",
        ["revoke"] = "hold a Premium license and have not used it: take it back.",
    };

    /// <summary>This month's AI bill in Azure, billed so far and forecast, against the budgets set there: null before the wider bill is loaded.</summary>
    async Task<Kpi?> AiBillAsync()
    {
        if (!(Viewer.OrgWide || Viewer.Has(Roles.Licensing)) || Open("/leadership/ai-spend") is not { } href) return null;
        if (!await TableHasRowsAsync("audit.AiCostDay")) return null;
        var today = DateTime.UtcNow.Date;
        var m0 = new DateTime(today.Year, today.Month, 1);
        var r = (await Db.QueryAsync<(decimal? Billed, decimal? Ahead, decimal? Budget, string? Currency)>(@"
SELECT (SELECT SUM(Cost) FROM audit.AiCostDay WHERE CostType = 'Actual' AND [Day] >= @m0),
       CAST(NULL AS DECIMAL(19, 6)), CAST(NULL AS DECIMAL(19, 4)),
       (SELECT TOP (1) Currency FROM audit.AiCostDay WHERE Currency IS NOT NULL GROUP BY Currency ORDER BY COUNT(*) DESC);", new { m0, t = today, m1 = m0.AddMonths(1) })).Single();
        if (await TableHasRowsAsync("audit.AiCostForecastDay"))
            r.Ahead = await Db.ScalarAsync<decimal?>("SELECT SUM(Cost) FROM audit.AiCostForecastDay WHERE [Day] >= @t AND [Day] < @m1;", new { t = today, m1 = m0.AddMonths(1) });
        if (await TableHasRowsAsync("audit.AiBudget"))
            r.Budget = await Db.ScalarAsync<decimal?>("SELECT SUM(Amount) FROM audit.AiBudget WHERE Amount > 0 AND TimeGrain IN ('Monthly', 'BillingMonth');");
        var cur = r.Currency ?? "USD";
        var month = (r.Billed ?? 0) + (r.Ahead ?? 0);
        var over = r.Budget > 0 && month > r.Budget;
        return new($"AI bill this month ({cur})", StartFigures.Money(month, cur),
            (r.Ahead is null ? "billed so far" : $"{StartFigures.Money(r.Billed ?? 0, cur)} billed, the rest forecast") + (r.Budget > 0 ? $"; monthly budgets {StartFigures.Money(r.Budget.Value, cur)}" : ""),
            over ? "crit" : r.Budget > 0 ? "good" : null, Href: href);
    }
}
