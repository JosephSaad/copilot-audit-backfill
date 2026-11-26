using CopilotInsights.Charts;
using CopilotInsights.Data;
using CopilotInsights.Infrastructure;
using Microsoft.AspNetCore.Html;

namespace CopilotInsights.Pages.Leadership;

/// <summary>
/// AI spend across Azure: what every AI service costs as Cost Management bills it (audit.AiCostDay, read by
/// tools\Import-FoundryUsage.ps1 at each scope of AiCostScopes): Foundry and Azure OpenAI, Copilot Studio and the
/// Power Platform billed to Azure, Microsoft 365 Copilot pay-as-you-go, AI Search and the rest; actual cost beside
/// amortized cost, the month's forecast, and each scope's budgets. Organization-wide: Azure bills by resource.
/// </summary>
public sealed class AiSpendModel(PageServices s) : InsightsPage(s)
{
    public override string Title => "AI spend across Azure";
    public override string Persona => "Leadership";
    public override bool UsesTeam => false;
    public override string Intro => "What every AI service costs in Azure, as Microsoft bills it: Foundry and Azure OpenAI, Copilot Studio, Microsoft 365 Copilot pay-as-you-go, AI Search and the rest. "
        + "Actual cost is what was billed each day; amortized cost spreads a reservation over the days it covers. With this month's forecast and the budgets set in Azure.";

    public sealed class BudgetRow
    {
        public string Scope { get; set; } = "";
        public string Budget { get; set; } = "";
        public decimal? Amount { get; set; }
        public string? TimeGrain { get; set; }
        public decimal? CurrentSpend { get; set; }
        public decimal? ForecastSpend { get; set; }
        public string? Currency { get; set; }
    }

    Task<bool> HasRowsAsync(string table) => TableHasRowsAsync(table);

    static string Money(decimal v, string cur) => CostRules.Money(Math.Round(v, 0), cur);

    protected override async Task BuildAsync()
    {
        if (!await HasRowsAsync("audit.AiCostDay"))
        {
            Notice = "The wider AI bill has not been loaded here yet. An administrator gives the Foundry load's app Cost Management Reader where the AI services are billed "
                   + "(and, for a billing account, Billing account reader) and lists the scopes in AiCostScopes; docs/FOUNDRY-USAGE.md says how. Data sources says what is loaded.";
            return;
        }
        var today = DateTime.UtcNow.Date;
        var monthStart = new DateTime(today.Year, today.Month, 1);
        var cur = await Db.ScalarAsync<string?>("SELECT TOP (1) Currency FROM audit.AiCostDay WHERE Currency IS NOT NULL GROUP BY Currency ORDER BY COUNT(*) DESC;") ?? "USD";
        var byService = await Db.QueryAsync<(string CostType, string Service, decimal Cost)>(
            "SELECT CostType, Service, SUM(Cost) FROM audit.AiCostDay WHERE [Day] BETWEEN @d0 AND @d1 GROUP BY CostType, Service;", Ctx.Args());
        var trend = await Db.QueryAsync<(DateTime Bucket, string Service, decimal Cost)>($@"
SELECT {Ctx.BucketSql("d")}, c.Service, SUM(c.Cost) FROM audit.AiCostDay AS c JOIN bi.DimDate AS d ON d.[Date] = c.[Day]
WHERE c.CostType = 'Amortized' AND c.[Day] BETWEEN @d0 AND @d1 GROUP BY {Ctx.BucketSql("d")}, c.Service;", Ctx.Args());
        var top = await Db.QueryAsync<(string Service, string? Resource, string? Group, decimal Actual, decimal Amortized)>(@"
SELECT TOP (25) Service, ResourceName, ResourceGroup, SUM(CASE WHEN CostType = 'Actual' THEN Cost ELSE 0 END), SUM(CASE WHEN CostType = 'Amortized' THEN Cost ELSE 0 END)
FROM audit.AiCostDay WHERE [Day] BETWEEN @d0 AND @d1 GROUP BY Service, ResourceName, ResourceGroup ORDER BY 5 DESC, 4 DESC;", Ctx.Args());
        var monthToDate = await Db.ScalarAsync<decimal?>("SELECT SUM(Cost) FROM audit.AiCostDay WHERE CostType = 'Actual' AND [Day] >= @m;", new { m = monthStart }) ?? 0;
        var ahead = await HasRowsAsync("audit.AiCostForecastDay")
            ? await Db.ScalarAsync<decimal?>("SELECT SUM(Cost) FROM audit.AiCostForecastDay WHERE [Day] >= @t AND [Day] < @n;", new { t = today, n = monthStart.AddMonths(1) }) : null;
        var budgets = await HasRowsAsync("audit.AiBudget")
            ? await Db.QueryAsync<BudgetRow>("SELECT Scope, Budget, Amount, TimeGrain, CurrentSpend, ForecastSpend, Currency FROM audit.AiBudget ORDER BY Scope, Budget;") : [];

        decimal Sum(string type, Func<string, bool>? which = null) => byService.Where(x => x.CostType == type && (which is null || which(x.Service))).Sum(x => x.Cost);
        var actual = Sum("Actual");
        var amortized = Sum("Amortized");
        var studio = Sum("Amortized", x => x == "Copilot Studio");
        var m365 = Sum("Amortized", x => x.StartsWith("Microsoft 365 Copilot", StringComparison.Ordinal));
        var withBudget = budgets.Where(b => b.Amount > 0).ToList();
        var budgetAmount = withBudget.Sum(b => b.Amount ?? 0);
        var budgetSpent = withBudget.Sum(b => b.CurrentSpend ?? 0);
        var over = withBudget.Count(b => (b.ForecastSpend ?? b.CurrentSpend ?? 0) > b.Amount);

        Kpis.Add(new($"AI spend in the period ({cur})", Money(amortized > 0 ? amortized : actual, cur),
            $"amortized: reservations spread over their days; {Money(actual, cur)} billed as actual cost, {Ctx.Start:d MMM} to {Ctx.End:d MMM yyyy}", "accent"));
        if (ahead is not null)
            Kpis.Add(new($"This month, forecast ({cur})", Money(monthToDate + ahead.Value, cur), $"{Money(monthToDate, cur)} billed so far this month, and Cost Management's forecast of the {(monthStart.AddMonths(1) - today).Days} days left",
                budgetAmount > 0 && monthToDate + ahead.Value > budgetAmount ? "warn" : null));
        else
            Kpis.Add(new($"This month so far ({cur})", Money(monthToDate, cur), "billed since the 1st; no forecast loaded"));
        if (withBudget.Count > 0)
            Kpis.Add(new("Budgets used", Pct((double)(budgetSpent / budgetAmount)), $"{Money(budgetSpent, cur)} of {Money(budgetAmount, cur)} across {Count(withBudget.Count, "budget")}; {(over > 0 ? $"{Count(over, "budget")} forecast to go over" : "none forecast to go over")}",
                over > 0 ? "crit" : budgetSpent / budgetAmount > 0.8m ? "warn" : "good"));
        Kpis.Add(new($"Copilot Studio ({cur})", Money(studio, cur), "messages and Copilot Credits billed to Azure in the period", Href: studio > 0 ? Open("/agents/credits") : null));
        if (m365 > 0)
            Kpis.Add(new($"Microsoft 365 Copilot pay-as-you-go ({cur})", Money(m365, cur), "agents and Copilot Chat billed by use"));
        if (actual > 0 && amortized > 0 && Math.Abs(actual - amortized) >= 1)
            Kpis.Add(new($"Reservations' effect ({cur})", Money(actual - amortized, cur),
                actual > amortized ? "billed up front in the period beyond what it used: the reservation is paid ahead" : "used from reservations bought before the period", null));

        var services = byService.Where(x => x.CostType == "Amortized").OrderByDescending(x => x.Cost).Select(x => x.Service).ToList();
        if (services.Count == 0) services = byService.OrderByDescending(x => x.Cost).Select(x => x.Service).Distinct().ToList();
        var buckets = Buckets();
        var slot = 1;
        Panels.Add(new("AI spend over time, by service", Chart.Columns(Labels(),
            services.Take(8).Select(sv => new Series(sv, buckets.Select(b => (double?)trend.Where(x => x.Bucket == b.Key && x.Service == sv).Sum(x => x.Cost)).ToList(), slot++)).ToList(), Fmt.Count, $"Amortized AI spend by service ({cur})"),
            $"Amortized cost a day or week, by service, stacked. {TrendNote}", "full"));
        Panels.Add(new("By service", Chart.Bars(services.Select((sv, i) => new Bar(sv, (double)Math.Round(byService.Where(x => x.CostType == "Amortized" && x.Service == sv).Sum(x => x.Cost)), Slot: i % 8 + 1)).ToList(),
            Fmt.Count, $"Amortized AI spend by service ({cur})"), "Each service's amortized cost in the period.", "half"));

        if (budgets.Count > 0)
        {
            var bt = new Table { Headers = ["Budget", "Scope", "Period", $"Amount", "Spent", "Used", "Forecast", "Status"], Numeric = new HashSet<int> { 3, 4, 5, 6 } };
            foreach (var b in budgets)
            {
                var used = b.Amount > 0 ? (b.CurrentSpend ?? 0) / b.Amount : null;
                var goesOver = b.Amount > 0 && (b.ForecastSpend ?? b.CurrentSpend ?? 0) > b.Amount;
                bt.Rows.Add([b.Budget, ScopeName(b.Scope), b.TimeGrain, b.Amount is null ? null : Math.Round(b.Amount.Value), b.CurrentSpend is null ? null : Math.Round(b.CurrentSpend.Value),
                             used is null ? null : Pct((double)used.Value), b.ForecastSpend is null ? null : Math.Round(b.ForecastSpend.Value),
                             new HtmlString(goesOver ? "<span class=\"tag crit\">Forecast over</span>" : used > 0.8m ? "<span class=\"tag warn\">Over 80%</span>" : "<span class=\"tag good\">On track</span>")]);
            }
            Panels.Add(new("Budgets", bt.Render(), "Each budget set in Azure at a scope read, its spend so far in its period and Azure's forecast for the period's end. A budget counts what its own filter says, which may be more than AI.", "full"));
        }

        /* What grew most: each resource's amortized cost in the last 7 days loaded against the 7 before, largest rise first. */
        var last = await Db.ScalarAsync<DateTime?>("SELECT CAST(MAX([Day]) AS DATETIME2(0)) FROM audit.AiCostDay WHERE CostType = 'Amortized';");
        if (last is { } l)
        {
            var moves = await Db.QueryAsync<(string Service, string? Resource, decimal Now, decimal Before)>(@"
SELECT TOP (10) Service, ResourceName, SUM(CASE WHEN [Day] > @w0 THEN Cost ELSE 0 END), SUM(CASE WHEN [Day] <= @w0 THEN Cost ELSE 0 END)
FROM audit.AiCostDay WHERE CostType = 'Amortized' AND [Day] > @b0 AND [Day] <= @l
GROUP BY Service, ResourceName
HAVING SUM(CASE WHEN [Day] > @w0 THEN Cost ELSE 0 END) - SUM(CASE WHEN [Day] <= @w0 THEN Cost ELSE 0 END) >= 1
ORDER BY SUM(CASE WHEN [Day] > @w0 THEN Cost ELSE 0 END) - SUM(CASE WHEN [Day] <= @w0 THEN Cost ELSE 0 END) DESC;", new { l, w0 = l.AddDays(-7), b0 = l.AddDays(-14) });
            if (moves.Count > 0)
            {
                var gt = new Table { Headers = ["Service", "Resource", $"Last 7 days ({cur})", $"7 days before ({cur})", "Change"], Numeric = new HashSet<int> { 2, 3 } };
                foreach (var m in moves)
                    gt.Rows.Add([m.Service, m.Resource ?? "(no resource)", Math.Round(m.Now), Math.Round(m.Before),
                                 new HtmlString(m.Before > 0 ? $"<span class=\"tag {(m.Now / m.Before >= 1.5m ? "crit" : "warn")}\">{(double)(m.Now / m.Before - 1):+0%}</span>" : "<span class=\"tag info\">New</span>")]);
                Panels.Add(new("Growing fastest", gt.Render(), $"Amortized cost in the 7 days to {l:d MMM} against the 7 before, largest rise first: the ones to ask their owners about. Up half again or more is marked critical.", "full"));
            }
        }

        var t = new Table { Headers = ["Service", "Resource", "Resource group", $"Actual ({cur})", $"Amortized ({cur})"], Numeric = new HashSet<int> { 3, 4 } };
        foreach (var r in top)
            t.Rows.Add([r.Service, r.Resource is null ? "(no resource)" : (object)(r.Service == "Foundry and Azure OpenAI" ? new Link(r.Resource, Ctx.Url("/governance/ai-apps/resource", extra: $"r={Uri.EscapeDataString(r.Resource)}")) : r.Resource),
                        r.Group, Math.Round(r.Actual), Math.Round(r.Amortized)]);
        CsvTable = t;
        Panels.Add(new("The 25 costliest AI resources", t.Render(), "By amortized cost in the period; a Foundry account opens its detail.", "full", CsvUrl()));
    }

    /// <summary>A scope as people say it: the subscription or billing account's id after its kind.</summary>
    public static string ScopeName(string scope)
    {
        var parts = scope.Trim('/').Split('/');
        return parts.Length >= 2 ? $"{Humanize(parts[^2])} {parts[^1]}" : scope;
        static string Humanize(string kind) => kind.ToLowerInvariant() switch
        {
            "subscriptions" => "Subscription",
            "billingaccounts" => "Billing account",
            "enrollmentaccounts" => "Enrollment account",
            "departments" => "Department",
            "billingprofiles" => "Billing profile",
            "invoicesections" => "Invoice section",
            "resourcegroups" => "Resource group",
            _ => kind,
        };
    }
}
