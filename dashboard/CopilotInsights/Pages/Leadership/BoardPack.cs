using System.Globalization;
using CopilotInsights.Charts;
using CopilotInsights.Data;
using CopilotInsights.Infrastructure;
using Microsoft.AspNetCore.Html;

namespace CopilotInsights.Pages.Leadership;

/// <summary>One page to print for the board or the steering group: the headline figures, the trend, targets, value, licenses and risk.</summary>
public sealed class BoardPackModel(PageServices s, Metrics metrics, OpsReports reports, OpsData ops) : InsightsPage(s)
{
    public override string Title => "Board pack";
    public override string Persona => "Leadership";
    public override string Intro => "The program on one page, for the board or the steering group: use and adoption against the period before, by unit, the trend, targets, estimated value, licenses and risk signals. "
        + "Adoption is the share of Premium license holders (the paid Microsoft 365 Copilot license) who used Copilot with it in the period. It splits in two: Premium, holders who used what the license pays for (Copilot in Word, Excel, PowerPoint, Outlook or Teams, or chat over work content); and free only, holders who used only what is free without it (web Copilot Chat, agents). "
        + "Print it, or save it as a PDF from the print dialog. Totals only: no one is named.";

    protected override async Task BuildAsync()
    {
        /* The Scorecard was this page's figures against the period before, by unit, on a page of its own: it is folded in
           here, and /leadership/scorecard arrives here. */
        var prev = OpsReports.Window(Ctx, Ctx.Start.AddDays(-1), Ctx.Days);
        /* Every read the page makes is started here, side by side, each on its own connection, and awaited where it is
           used: one after another, the trend, the targets, the value, the license months, the pool, the action counts and
           the non-human accounts added their times to the headline's. Those against the period before start as soon as
           the first day with data says there is a full one. */
        var splitTask = Forecasting.ActionsModel.PremiumReadyAsync(Db, Ctx.StartKey, Ctx.EndKey);
        var firstTask = OpsReports.FirstDayAsync(Db);
        var nowTask = metrics.TotalsAsync(Ctx);
        var u1Task = metrics.UnitsAsync(Ctx);
        var trTask = metrics.TrendAsync(Ctx);
        var targetsTask = reports.TargetsAsync(Ctx);
        var valueTask = reports.ValueAsync(Ctx);
        var monthsTask = reports.LicenseMonthsAsync(Ctx);
        /* The pool is the whole tenant's: for the viewers License pool shows it to, the whole-organization view or the
           Licensing role. Every viewer of the board pack had it, whatever their part of the organization. */
        var poolTask = Viewer.OrgWide || Viewer.Has(Roles.Licensing) ? ops.PoolAsync() : null;
        var countsTask = reports.ActionCountsAsync(Ctx);
        var nonHumanTask = CopilotInsights.Data.NonHuman.AllAsync(Db, S.Settings, Ctx.Scope("ld.UserKey"), Ctx.Args());
        /* Set against the period before only where the data covers all of it (see OpsReports.Covers): the board read
           "+3997% on the period before" at 180 days, against the two days of data before them. */
        var first = await firstTask;
        var full = OpsReports.Covers(prev, first);
        var beforeTask = full ? metrics.TotalsAsync(prev) : null;
        var u0Task = full ? metrics.UnitsAsync(prev) : null;
        /* The period before is read only for its risk sums: its people and license holders are not shown. */
        var rpTask = full ? metrics.SumsTrendAsync(prev) : null;
        await Task.WhenAll(new Task?[] { splitTask, nowTask, u1Task, beforeTask, u0Task }.OfType<Task>());
        var split = await splitTask;
        /* The Scorecard's parts (the Premium split and each unit against the period before), turned off in Admin › Reports
           shown by its address (HiddenReports.Parts). */
        var scorecard = !CopilotInsights.Pages.HiddenReports.Is(HiddenReports, "/leadership/scorecard");
        if (!split)
            Notice = "Adoption is not yet split into Premium and free use here. An administrator separates them by rebuilding the dashboard once (Admin › Health, Rebuild every day).";
        var t = await nowTask;
        var p = beforeTask is null ? null : await beforeTask;
        string Change(double now, double? before) => before is not double b ? OpsReports.NoFullPeriodBefore(first)
            : b == 0 ? "none in the period before" : $"{now / b - 1:+0%;-0%;0%} on the {Ctx.Days} days before";
        string Points(double? now, double? before) => !full ? OpsReports.NoFullPeriodBefore(first)
            : now is double a1 && before is double a0 ? $"{(a1 - a0) * 100:+0.0;-0.0;0} points on the {Ctx.Days} days before" : "no earlier figure";
        double? Adopt(Totals? x) => x is { LicensedNow: > 0 } ? (double)x.ActiveLicensed / x.LicensedNow : null;
        /* Premium + free only = Adoption: every active license holder either used a Premium feature or did not. */
        double? Premium(Totals? x) => x is { LicensedNow: > 0 } ? x.PremiumLicensed / (double)x.LicensedNow : null;
        double? FreeOnly(Totals? x) => x is { LicensedNow: > 0 } ? (x.ActiveLicensed - x.PremiumLicensed) / (double)x.LicensedNow : null;
        Kpis.Add(new("People using Copilot", Num(t.Active), Change(t.Active, p?.Active), Href: Open("/licensing/ranking")));
        Kpis.Add(new("Adoption", Pct(Adopt(t)), Points(Adopt(t), Adopt(p)), Href: Open("/adoption")));
        if (split && scorecard)
        {
            Kpis.Add(new("Premium adoption", Pct(Premium(t)), Points(Premium(t), Premium(p))));
            Kpis.Add(new("Free-only adoption", Pct(FreeOnly(t)), Points(FreeOnly(t), FreeOnly(p))));
        }
        Kpis.Add(new("Interactions", Num(t.Interactions), Change(t.Interactions, p?.Interactions)));
        Kpis.Add(new("Sessions with a follow-up", Pct(Ratio(t.MultiTurn, t.Sessions)), Points(Ratio(t.MultiTurn, t.Sessions), p is null ? null : Ratio(p.MultiTurn, p.Sessions))));
        Kpis.Add(new("Premium licenses", Num(t.LicensedNow), $"held on {Ctx.End:d MMM}; {t.ActiveLicensed:#,0} holders used Copilot with it in the period", Href: Open("/licensing")));

        Panels.Add(new("Print", new HtmlString(
            "<button type=\"button\" class=\"btn\" data-print>Print or save as PDF</button>" +
            $"<p class=\"print-only\">Copilot Insights board pack: {E(Ctx.ScopeLabel)}, {E(Ctx.PeriodLabel)}. Printed {DateTime.Now:d MMM yyyy HH:mm}.</p>"),
            "The navigation, filters and buttons are left off the printout.", "half no-print"));

        var tr = await trTask;
        Panels.Add(new("People using Copilot", Chart.Line(Labels(), [
            new Series("With a Premium license", tr.Select(w => (double?)w.ActiveLicensed).ToList(), 1),
            new Series("Free Copilot Chat only", tr.Select(w => (double?)w.ActiveUnlicensed).ToList(), 2)], Fmt.Count, "People using Copilot"),
            TrendNote + " Dashed rules mark events (Admin › Classifications and events)."));

        /* The targets for the part of the organization printed, not every target (see OpsReports.Visible). */
        var targets = await targetsTask;
        if (targets.Count > 0)
        {
            var tt = new Table { Headers = ["Target", "For", "Now", "Should be by now", "Due", "Status"] };
            foreach (var r in targets)
                tt.Rows.Add([$"{Adoption.TargetsModel.Label(r.Metric)} {r.TargetValue:0.#}{(r.Metric == "ActivePeople" ? "" : "%")}", r.RootName ?? r.RootUpn ?? "Whole organization",
                    r.Current is null ? null : Math.Round(r.Current.Value, 1), r.Expected is null ? null : Math.Round(r.Expected.Value, 1), r.DueDate,
                    new HtmlString($"<span class=\"{(r.Status is "Met" or "On track" ? "status-ok" : r.Status == "Behind" ? "status-warn" : "status-bad")}\">{E(r.Status)}</span>")]);
            Panels.Add(new("Targets", tt.Render(), null, "half"));
        }

        var v = await valueTask;
        var cur = await S.Settings.StringAsync("LicenseCurrency") ?? "USD";
        var price = decimal.TryParse(await S.Settings.StringAsync("LicenseMonthlyCost"), NumberStyles.Number, CultureInfo.InvariantCulture, out var pr) ? pr : 30m;
        var hours = v.ByApp.Sum(x => x.Hours);
        var worth = v.ByApp.Sum(x => x.Value);
        var cost = Licensing.Price.ForLicenseMonths(await monthsTask, price);   // Whole calendar months held
        Panels.Add(new("Estimated value", new HtmlString(
            $"<div class=\"kpis\"><div class=\"kpi\"><div class=\"klabel\">Hours saved (estimate)</div><div class=\"kvalue\">{hours:#,0}</div></div>" +
            $"<div class=\"kpi\"><div class=\"klabel\">Estimated value ({E(cur)})</div><div class=\"kvalue\">{worth:#,0}</div></div>" +
            $"<div class=\"kpi\"><div class=\"klabel\">License cost in the period ({E(cur)})</div><div class=\"kvalue\">{(Licensing.Price.MonthsCharged(Ctx.Start, Ctx.End).Count > 0 ? cost.ToString("#,0") : "—")}</div></div>" +
            $"<div class=\"kpi\"><div class=\"klabel\">Return on license cost</div><div class=\"kvalue\">{(cost > 0 ? $"{worth / cost:0.0}×" : "—")}</div></div></div>" +
            (Open("/leadership/value") is { } vu ? $"<p class=\"sub no-print\"><a href=\"{E(vu)}\">Value and time saved</a> has it by app and by unit.</p>" : "")),
            $"At {v.DefaultMinutes:0.#} minutes an interaction unless set per app, and {v.HourlyCost:#,0.##} {cur} an hour: your organization's own assumptions (Admin › Targets and assumptions). " +
            "License cost: each calendar month whose last day falls in the period, at the license price for everyone who held one that month; a period under a month charges none.", "half"));

        var pool = poolTask is null ? [] : (await poolTask).Where(x => x.IsCopilot).ToList();
        var counts = await countsTask;
        var lt = new Table { Headers = ["Licenses", ""], Numeric = new HashSet<int> { 1 }, Sortable = false };
        var tenant = Ctx.Root is null ? "" : " (the whole tenant)";
        if (pool.Count > 0) { lt.Rows.Add(["Bought" + tenant, pool.Sum(x => x.Bought)]); lt.Rows.Add(["Assigned" + tenant, pool.Sum(x => x.Consumed)]); }
        lt.Rows.Add(["Premium licenses held", t.LicensedNow]);
        lt.Rows.Add(["Holders who used Copilot with it in the period", t.ActiveLicensed]);
        lt.Rows.Add(["Unused for 30 days, to revoke", counts.GetValueOrDefault("revoke")]);
        lt.Rows.Add(["Free Copilot Chat users ready to upgrade", counts.GetValueOrDefault("upgrade")]);
        Panels.Add(new("Licenses", lt.Render(), $"At {price:#,0.##} {cur} a license a month, the unused licenses cost {counts.GetValueOrDefault("revoke") * price:#,0} {cur} a month.", "half"));

        var rp = rpTask is null ? null : await rpTask;
        var rt = new Table { Headers = ["Risk signal", "This period", "Period before"], Numeric = new HashSet<int> { 1, 2 }, Sortable = false };
        /* The security signals are the Security section's and labeled content the Compliance section's, as on the
           data API: every viewer of the board pack had them, whatever their roles. */
        if (Viewer.Has(Roles.Security))
        {
            rt.Rows.Add(["Jailbreak attempts", tr.Sum(x => x.Jailbreak), rp?.Sum(x => x.Jailbreak)]);
            rt.Rows.Add(["Prompt injection detected", tr.Sum(x => x.Injection), rp?.Sum(x => x.Injection)]);
            rt.Rows.Add(["Blocked resources", tr.Sum(x => x.Blocked), rp?.Sum(x => x.Blocked)]);
        }
        if (Viewer.Has(Roles.Compliance))
            rt.Rows.Add(["Interactions with labeled content", tr.Sum(x => x.Labeled), rp?.Sum(x => x.Labeled)]);
        /* A policy breach, counted today: there is no earlier figure to set beside it. */
        rt.Rows.Add(["Copilot licenses on non-human accounts (today)", (await nonHumanTask).Count, null]);
        Panels.Add(new("Risk", rt.Render(), "From Copilot's own audit records; the detail is on Security and Compliance." +
            (Viewer.Has(Roles.Security) ? "" : " The security signals are shown to people with the Security role.") +
            (Viewer.Has(Roles.Compliance) ? "" : " Labeled content is shown to people with the Compliance role.") +
            (full ? "" : $" The period before is left blank: {OpsReports.NotCovered(first)}."), "half"));
        await AiBillByMonthAsync();

        var u1All = await u1Task;
        var units = SmallGroups.Shown(u1All, u => u.People, Ctx.MinGroupSize).Where(u => u.Licensed >= Ctx.MinGroupSize).ToList();
        if (units.Count > 1)
            Panels.Add(new("Adoption by unit", Chart.Adoption(units.Select(u => new Chart.AdoptionGroup(u.Name, u.ActiveLicensed, u.Licensed)), Ctx.MinGroupSize, "Adoption by unit", 12),
                $"The share of Premium license holders who used Copilot with the license in the period, for units with at least {Ctx.MinGroupSize} people and {Ctx.MinGroupSize} license holders.", "half"));

        /* Each unit against the period before (the Scorecard's table). */
        if (!scorecard) return;
        var u1 = u1All.ToDictionary(u => u.Unit);
        var u0 = u0Task is not null ? (await u0Task).ToDictionary(u => u.Unit) : new Dictionary<string, UnitRow>();
        var ut = new Table
        {
            Headers = split
                ? ["Unit", "People", "Adoption", "Premium", "Premium change", "Free only", "Free-only change", "Active people", "Active change", "Interactions per active person", "Interactions change"]
                : ["Unit", "People", "Adoption", "Adoption change", "Active people", "Active change", "Interactions per active person", "Interactions change"],
            Numeric = Enumerable.Range(1, split ? 10 : 7).ToHashSet(),
        };
        HtmlString Arrow(double? d, bool pct) => d is null ? new("<span class=\"muted\">—</span>")
            : new($"<span class=\"{(d > 0 ? "status-ok" : d < 0 ? "status-bad" : "muted")}\">{(d > 0 ? "▲" : d < 0 ? "▼" : "")} {(pct ? $"{Math.Abs(d.Value) * 100:0.0} pts" : $"{Math.Abs(d.Value):0%}")}</span>");
        foreach (var n in SmallGroups.Shown(u1.Values, u => u.People, Ctx.MinGroupSize).OrderByDescending(u => u.People))
        {
            var b = u0.GetValueOrDefault(n.Unit);
            /* A share of license holders only where there are at least the minimum group size of them, then and now. */
            bool Few(UnitRow? x) => x is null || x.Licensed < Ctx.MinGroupSize;
            double? Share(UnitRow? x, Func<UnitRow, double> of) => Few(x) ? null : of(x!) / x!.Licensed;
            double? a1 = Share(n, x => x.ActiveLicensed), a0 = Share(b, x => x.ActiveLicensed);
            double? i1 = n.Active > 0 ? n.Interactions / (double)n.Active : null, i0 = b is { Active: > 0 } ? b.Interactions / (double)b.Active : null;
            var name = n.Drillable ? (object)new Link(n.Name, Ctx.Url(Request.Path, org: n.Unit)) : n.Name;
            var rest = new object?[] { n.Active, Arrow(b is { Active: > 0 } ? (n.Active - b.Active) / (double)b.Active : null, false),
                                       i1 is null ? null : Math.Round(i1.Value, 1), Arrow(i0 is > 0 ? (i1 - i0) / i0 : null, false) };
            if (split)
            {
                double? p1 = Share(n, x => x.PremiumLicensed), p0 = Share(b, x => x.PremiumLicensed);
                double? f1 = Share(n, x => x.ActiveLicensed - x.PremiumLicensed), f0 = Share(b, x => x.ActiveLicensed - x.PremiumLicensed);
                ut.Rows.Add([name, n.People, Pct(a1), Pct(p1), Arrow(p1 - p0, true), Pct(f1), Arrow(f1 - f0, true), .. rest]);
            }
            else
                ut.Rows.Add([name, n.People, Pct(a1), Arrow(a1 - a0, true), .. rest]);
        }
        CsvTable = ut;
        Panels.Add(new("By unit, against the period before", ut.Render(),
            $"Each part of the organization one level below. Units under {Ctx.MinGroupSize} people are left out, and the next smallest too until those left out reach {Ctx.MinGroupSize} together, so no small unit can be worked out. "
            + (split ? "Adoption is Premium plus free only: license holders who used a Premium feature, and those who used only free Copilot Chat or agents. " : "")
            + $"Adoption is shown only for a unit of at least {Ctx.MinGroupSize} license holders. Active people: people who used Copilot in the period. Interactions count all use, including by people since disabled. Change in adoption is in percentage points; the others are relative."
            + (full ? "" : $" The changes are left blank: {OpsReports.NotCovered(first)}."), "full", CsvUrl()));
    }

    /// <summary>
    /// The AI bill in Azure a month, by service (3.4.0): the last six whole months and this one so far, amortized, from
    /// the whole AI bill the Foundry load reads. For those who see the money (the whole organization, or Licensing),
    /// once it is loaded; a table, so it prints.
    /// </summary>
    async Task AiBillByMonthAsync()
    {
        if (!(Viewer.OrgWide || Viewer.Has(Roles.Licensing)) || !await TableHasRowsAsync("audit.AiCostDay")) return;
        var m0 = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1).AddMonths(-6);
        var rows = await Db.QueryAsync<(DateTime Month, string Service, decimal Cost, string? Currency)>(@"
SELECT DATEFROMPARTS(YEAR([Day]), MONTH([Day]), 1), Service, SUM(Cost), MAX(Currency)
FROM audit.AiCostDay WHERE CostType = 'Amortized' AND [Day] >= @m0
GROUP BY DATEFROMPARTS(YEAR([Day]), MONTH([Day]), 1), Service;", new { m0 });
        if (rows.Count == 0) return;
        var cur = rows.Select(r => r.Currency).FirstOrDefault(c => c is not null) ?? "USD";
        var services = rows.GroupBy(r => r.Service).OrderByDescending(g => g.Sum(r => r.Cost)).Select(g => g.Key).ToList();
        var shown = services.Take(4).ToList();
        var other = services.Count > shown.Count;
        var t = new Table
        {
            Headers = ["Month", .. shown, .. (other ? new[] { "Other AI services" } : []), $"Total ({cur})"],
            Numeric = Enumerable.Range(1, shown.Count + (other ? 1 : 0) + 1).ToHashSet(), Sortable = false,
        };
        foreach (var month in rows.Select(r => r.Month).Distinct().OrderBy(m => m))
        {
            var inMonth = rows.Where(r => r.Month == month).ToList();
            var cells = new List<object?> { month.ToString("MMM yyyy") + (month.Month == DateTime.UtcNow.Month && month.Year == DateTime.UtcNow.Year ? " (so far)" : "") };
            cells.AddRange(shown.Select(sv => (object?)Math.Round(inMonth.Where(r => r.Service == sv).Sum(r => r.Cost))));
            if (other) cells.Add(Math.Round(inMonth.Where(r => !shown.Contains(r.Service)).Sum(r => r.Cost)));
            cells.Add(Math.Round(inMonth.Sum(r => r.Cost)));
            t.Rows.Add([.. cells]);
        }
        Panels.Add(new("AI spend in Azure, by month", t.Render(),
            "Every AI service's amortized cost as Azure bills it: Foundry and Azure OpenAI, Copilot Studio, Microsoft 365 Copilot pay-as-you-go, AI Search and the rest. Licenses are not in it: they are under Licenses. AI spend across Azure has the detail.", "full"));
    }
}

/// <summary>What people use Copilot for, from the app, whether it searched the web, and whether an agent answered: rules an administrator can change.</summary>
public sealed class UseCasesModel(PageServices s, OpsData ops) : InsightsPage(s)
{
    public override string Title => "Use cases";
    public override string Persona => "Leadership";
    public override string Intro => "What Copilot is used for, worked out from where each interaction happened (Outlook, Teams, Word, Excel...), whether it searched the web, and whether an agent answered. The rules are in Admin › Classifications and events. The audit log holds no prompt text, so this is where and how, not the words.";

    sealed class G
    {
        public string UseCase { get; set; } = "";
        public string? Department { get; set; }
        public DateTime? Bucket { get; set; }
        public long N { get; set; }
        public int People { get; set; }
    }

    /* bi.UseCases applies the rules (its one definition); the fact carries the key into it, and the nightly totals keep
       each person's interactions per key (Ctx.PerItem('U')), so a rule changed today still applies to every day. */
    const string UseCase = "JOIN bi.UseCases AS uc ON uc.UseCaseKey = g.UseCaseKey";

    protected override async Task BuildAsync()
    {
        if (!await ops.HasAsync("dash.UseCaseRule") || await Db.ScalarAsync<int>("SELECT CASE WHEN OBJECT_ID(N'bi.UseCases', N'V') IS NULL THEN 0 ELSE 1 END") == 0) { Notice = "Use cases are not set up here yet. An administrator sets them up by running Install-CopilotAudit.ps1 -Dashboard from the current release."; return; }
        /* Group the interactions first, then classify the groups: the rules run over thousands of rows, not millions. */
        /* In steps: the rules read once into #uc, one row a person and use case in #k, then a plain count.
           As one statement the optimizer ran the rules per group and counted distinct people: 22 seconds for a year. */
        /* The three reads are independent: started together. Each person's interactions per use-case key are the nightly
           totals' (Ctx.PerItem, Ctx.PerItemPoint), one row a person and key, where the 160 million interactions were
           grouped on every first visit: 9 to 28 seconds at two years. */
        var byDeptTask = Db.QueryAsync<G>($@"
SELECT UseCaseKey, UseCase INTO #uc FROM bi.UseCases;
WITH g AS (SELECT it.UserKey, it.Item AS UseCaseKey, CAST(it.Interactions AS BIGINT) AS N
           FROM {Ctx.PerItem('U')} AS it {Ctx.Scope("it.UserKey")})
SELECT g.UserKey, uc.UseCase, SUM(g.N) AS N INTO #k FROM g JOIN #uc AS uc ON uc.UseCaseKey = g.UseCaseKey GROUP BY g.UserKey, uc.UseCase;
/* People are accounts enabled today (3.1.3); interactions are everyone's. */
SELECT k.UseCase, ISNULL(NULLIF(du.Department, N''), N'(no department)') AS Department, SUM(k.N) AS N, COUNT(CASE WHEN du.AccountEnabled = 1 THEN 1 END) AS People
FROM #k AS k JOIN bi.DimUser AS du ON du.UserKey = k.UserKey
GROUP BY k.UseCase, ISNULL(NULLIF(du.Department, N''), N'(no department)');", Ctx.Args(), timeout: 300);
        var trendTask = Db.QueryAsync<G>($@"
WITH g AS (SELECT pt.Bucket, pt.Item AS UseCaseKey, SUM(CAST(pt.Interactions AS BIGINT)) AS N
           FROM {Ctx.PerItemPoint('U')} AS pt {Ctx.Scope("pt.UserKey")}
           GROUP BY pt.Bucket, pt.Item)
SELECT uc.UseCase, g.Bucket, SUM(g.N) AS N FROM g {UseCase} GROUP BY uc.UseCase, g.Bucket;", Ctx.Args(), timeout: 300);
        var deptsTask = Db.QueryAsync<(string Dept, int People)>($@"
SELECT ISNULL(NULLIF(du.Department, N''), N'(no department)'), COUNT(*) FROM bi.DimUser AS du {Ctx.Scope("du.UserKey")}
WHERE du.AccountEnabled = 1 AND du.DirectoryStatus = N'Found' GROUP BY ISNULL(NULLIF(du.Department, N''), N'(no department)');", Ctx.Args());
        await Task.WhenAll(byDeptTask, trendTask, deptsTask);
        var byDept = await byDeptTask;
        /* The totals add up the departments: each person has one department, so their people add up too.
           A second pass over the fact for the same numbers doubled the page's time. */
        var totals = byDept.GroupBy(x => x.UseCase).Select(g => new G { UseCase = g.Key, N = g.Sum(x => x.N), People = g.Sum(x => x.People) }).ToList();
        if (totals.Count == 0) { Notice = $"No Copilot use from {Ctx.PeriodLabel}."; return; }
        var trend = await trendTask;

        var all = totals.Sum(x => x.N);
        var ranked = totals.OrderByDescending(x => x.N).ToList();
        Kpis.Add(new("Most common", ranked[0].UseCase, $"{Pct(Ratio(ranked[0].N, all))} of interactions"));
        Kpis.Add(new("Use cases seen", Num(ranked.Count), "by the rules in Admin › Classifications and events"));
        var agents = totals.Where(x => x.UseCase == "Working with an agent").Sum(x => x.N);
        Kpis.Add(new("Interactions through an agent", Pct(Ratio(agents, all)), "of all interactions in the period"));

        /* Fixed colors per use case, by overall size (the chart shows at most eight). */
        var top = ranked.Take(7).Select(x => x.UseCase).ToList();
        string Key(string u) => top.Contains(u) ? u : "Everything else";
        var names = top.Concat(ranked.Count > 7 ? ["Everything else"] : []).ToList();
        var buckets = Buckets();
        Panels.Add(new("Interactions by use case", Chart.Columns(Labels(),
            names.Select((n, i) => new Series(n, buckets.Select(b => (double?)trend.Where(x => x.Bucket == b.Key && Key(x.UseCase) == n).Sum(x => x.N)).ToList(), i + 1)).ToList(),
            Fmt.Count, "Interactions by use case"), TrendNote));

        var t = new Table { Headers = ["Use case", "Interactions", "Share of interactions", "People", "Interactions per person"], Numeric = new HashSet<int> { 1, 2, 3, 4 } };
        foreach (var x in ranked) t.Rows.Add([x.UseCase, x.N, Pct(Ratio(x.N, all)), x.People, x.People > 0 ? Math.Round(x.N / (double)x.People, 1) : null]);
        Panels.Add(new("Use cases", t.Render(), "People: enabled accounts that used Copilot for this at least once in the period; one person counts under every use case they had. Interactions count all use, including by people since disabled.", "half"));

        /* Each department's mix, for departments big enough to show. */
        var deptPeople = byDept.GroupBy(x => x.Department!).ToDictionary(g => g.Key, g => g.Sum(x => x.N));
        var big = SmallGroups.Shown(await deptsTask, d => d.People, Ctx.MinGroupSize)
            .Select(d => d.Dept).ToHashSet();
        var mix = new Table { Headers = ["Department", .. names, "Interactions"], Numeric = new HashSet<int>(Enumerable.Range(1, names.Count + 1)) };
        foreach (var d in deptPeople.Where(d => big.Contains(d.Key)).OrderByDescending(d => d.Value).Take(25))
        {
            var row = new List<object?> { d.Key };
            row.AddRange(names.Select(n => (object?)Pct(Ratio(byDept.Where(x => x.Department == d.Key && Key(x.UseCase) == n).Sum(x => x.N), d.Value))));
            row.Add(d.Value);
            mix.Rows.Add([.. row]);
        }
        CsvTable = mix;
        Panels.Add(new("Each department's mix", mix.Render(), "The share of each department's interactions per use case; the 25 busiest departments of at least the minimum group size.", "full", CsvUrl()));
    }
}
