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
    public override string Intro => "The program on one page, for the board or the steering group: use and adoption against the period before, the trend, targets, estimated value, licenses and risk signals. Print it, or save it as a PDF from the print dialog. Totals only: no one is named.";

    protected override async Task BuildAsync()
    {
        var prev = OpsReports.Window(Ctx, Ctx.Start.AddDays(-1), Ctx.Days);
        /* Set against the period before only where the data covers all of it (2.4.12, see OpsReports.Covers): the board read
           "+3997% on the period before" at 180 days, against the two days of data before them. */
        var first = await OpsReports.FirstDayAsync(Db);
        var full = OpsReports.Covers(prev, first);
        var (t, p) = (await metrics.TotalsAsync(Ctx), full ? await metrics.TotalsAsync(prev) : null);
        string Change(double now, double? before) => before is not double b ? OpsReports.NoFullPeriodBefore(first)
            : b == 0 ? "none in the period before" : $"{now / b - 1:+0%;-0%;0%} on the period before";
        double? Adopt(Totals? x) => x is { LicensedNow: > 0 } ? (double)x.ActiveLicensed / x.LicensedNow : null;
        Kpis.Add(new("People using Copilot", Num(t.Active), Change(t.Active, p?.Active)));
        Kpis.Add(new("Adoption", Pct(Adopt(t)), Adopt(p) is double a0 && Adopt(t) is double a1 ? $"{(a1 - a0) * 100:+0.#;-0.#;0} points" : "of licensed people"));
        Kpis.Add(new("Interactions", Num(t.Interactions), Change(t.Interactions, p?.Interactions)));
        Kpis.Add(new("Licenses", Num(t.LicensedNow), $"{t.ActiveLicensed:#,0} used in the period"));

        Panels.Add(new("Print", new HtmlString(
            "<button type=\"button\" class=\"btn\" data-print>Print or save as PDF</button>" +
            $"<p class=\"print-only\">Copilot Insights board pack: {E(Ctx.ScopeLabel)}, {E(Ctx.PeriodLabel)}. Printed {DateTime.Now:d MMM yyyy HH:mm}.</p>"),
            "The navigation, filters and buttons are left off the printout.", "half no-print"));

        var tr = await metrics.TrendSeriesAsync(Ctx);
        Panels.Add(new("People using Copilot", Chart.Line(Labels(), [
            new Series("With a Premium license", tr.Average(w => w.ActiveLicensed), 1),
            new Series("Free Copilot Chat only", tr.Average(w => w.ActiveUnlicensed), 2)], Fmt.Average, "People using Copilot, 28-day average"),
            TrendNote + " Dashed rules mark events (Admin > Classifications and events)."));

        /* The targets for the part of the organization printed, not every target (2.4.12, see OpsReports.Visible). */
        var targets = await reports.TargetsAsync(Ctx);
        if (targets.Count > 0)
        {
            var tt = new Table { Headers = ["Target", "For", "Now", "Should be by now", "Due", "Status"] };
            foreach (var r in targets)
                tt.Rows.Add([$"{Adoption.TargetsModel.Label(r.Metric)} {r.TargetValue:0.#}{(r.Metric == "ActivePeople" ? "" : "%")}", r.RootName ?? r.RootUpn ?? "Whole organization",
                    r.Current is null ? null : Math.Round(r.Current.Value, 1), r.Expected is null ? null : Math.Round(r.Expected.Value, 1), r.DueDate,
                    new HtmlString($"<span class=\"{(r.Status is "Met" or "On track" ? "status-ok" : r.Status == "Behind" ? "status-warn" : "status-bad")}\">{E(r.Status)}</span>")]);
            Panels.Add(new("Targets", tt.Render(), null, "half"));
        }

        var v = await reports.ValueAsync(Ctx);
        var cur = await S.Settings.StringAsync("LicenseCurrency") ?? "USD";
        var price = decimal.TryParse(await S.Settings.StringAsync("LicenseMonthlyCost"), NumberStyles.Number, CultureInfo.InvariantCulture, out var pr) ? pr : 30m;
        var hours = v.ByApp.Sum(x => x.Hours);
        var worth = v.ByApp.Sum(x => x.Value);
        var cost = Licensing.Price.ForLicenseDays(await reports.LicenseDaysAsync(Ctx), price);
        Panels.Add(new("Estimated value", new HtmlString(
            $"<div class=\"kpis\"><div class=\"kpi\"><div class=\"klabel\">Hours saved (estimate)</div><div class=\"kvalue\">{hours:#,0}</div></div>" +
            $"<div class=\"kpi\"><div class=\"klabel\">Estimated value ({E(cur)})</div><div class=\"kvalue\">{worth:#,0}</div></div>" +
            $"<div class=\"kpi\"><div class=\"klabel\">License cost in the period ({E(cur)})</div><div class=\"kvalue\">{cost:#,0}</div></div>" +
            $"<div class=\"kpi\"><div class=\"klabel\">Return on license cost</div><div class=\"kvalue\">{(cost > 0 ? $"{worth / cost:0.0}×" : "—")}</div></div></div>"),
            $"At {v.DefaultMinutes:0.#} minutes an interaction unless set per app, and {v.HourlyCost:#,0.##} {cur} an hour: your organization's own assumptions (Admin > Targets and assumptions).", "half"));

        /* The pool is the whole tenant's: for the viewers License pool shows it to, the whole-organization view or the
           Licensing role (2.4.12). Every viewer of the board pack had it, whatever their part of the organization. */
        var pool = Viewer.OrgWide || Viewer.Has(Roles.Licensing) ? (await ops.PoolAsync()).Where(x => x.IsCopilot).ToList() : [];
        var counts = await reports.ActionCountsAsync(Ctx);
        var lt = new Table { Headers = ["Licenses", ""], Numeric = new HashSet<int> { 1 }, Sortable = false };
        var tenant = Ctx.Root is null ? "" : " (the whole tenant)";
        if (pool.Count > 0) { lt.Rows.Add(["Bought" + tenant, pool.Sum(x => x.Bought)]); lt.Rows.Add(["Assigned" + tenant, pool.Sum(x => x.Consumed)]); }
        lt.Rows.Add(["Premium licenses held", t.LicensedNow]);
        lt.Rows.Add(["Used in the period", t.ActiveLicensed]);
        lt.Rows.Add(["Unused for 30 days, to revoke", counts.GetValueOrDefault("revoke")]);
        lt.Rows.Add(["Free Copilot Chat users ready to upgrade", counts.GetValueOrDefault("upgrade")]);
        Panels.Add(new("Licenses", lt.Render(), $"At {price:#,0.##} {cur} a license a month, the unused licenses cost {counts.GetValueOrDefault("revoke") * price:#,0} {cur} a month.", "half"));

        /* The period's own sums, not the chart's averages (2.5.0). */
        var sg = await metrics.SumsAsync(Ctx);
        var rp = full ? await metrics.SumsAsync(prev) : null;
        var rt = new Table { Headers = ["Risk signal", "This period", "Period before"], Numeric = new HashSet<int> { 1, 2 }, Sortable = false };
        /* The security signals are the Security section's and labeled content the Compliance section's (2.4.12), as on the
           data API: every viewer of the board pack had them, whatever their roles. */
        if (Viewer.Has(Roles.Security))
        {
            rt.Rows.Add(["Jailbreak attempts", sg.Jailbreak, rp?.Jailbreak]);
            rt.Rows.Add(["Prompt injection detected", sg.Injection, rp?.Injection]);
            rt.Rows.Add(["Blocked resources", sg.Blocked, rp?.Blocked]);
        }
        if (Viewer.Has(Roles.Compliance))
            rt.Rows.Add(["Interactions with labeled content", sg.Labeled, rp?.Labeled]);
        /* A policy breach, counted today (2.4.5): there is no earlier figure to set beside it. */
        rt.Rows.Add(["Copilot licenses on non-human accounts (today)", (await CopilotInsights.Data.NonHuman.AllAsync(Db, S.Settings, Ctx.Scope("ld.UserKey"), Ctx.Args())).Count, null]);
        Panels.Add(new("Risk", rt.Render(), "From Copilot's own audit records; the detail is on Security and Compliance." +
            (Viewer.Has(Roles.Security) ? "" : " The security signals are shown to people with the Security role.") +
            (Viewer.Has(Roles.Compliance) ? "" : " Labeled content is shown to people with the Compliance role.") +
            (full ? "" : $" The period before is left blank: {OpsReports.NotCovered(first)}."), "half"));

        var units = SmallGroups.Shown(await metrics.UnitsAsync(Ctx), u => u.People, Ctx.MinGroupSize).Where(u => u.Licensed >= Ctx.MinGroupSize).ToList();
        if (units.Count > 1)
            Panels.Add(new("Adoption by unit", Chart.Adoption(units.Select(u => new Chart.AdoptionGroup(u.Name, u.ActiveLicensed, u.Licensed)), Ctx.MinGroupSize, "Adoption by unit", 12),
                "Units with at least the minimum group size of people and licenses.", "half"));
    }
}

/// <summary>What people use Copilot for, from the app, whether it searched the web, and whether an agent answered: rules an administrator can change.</summary>
public sealed class UseCasesModel(PageServices s, OpsData ops) : InsightsPage(s)
{
    public override string Title => "Use cases";
    public override string Persona => "Leadership";
    public override string Intro => "What Copilot is used for, worked out from where each interaction happened (Outlook, Teams, Word, Excel...), whether it searched the web, and whether an agent answered. The rules are in Admin > Classifications and events. The audit log holds no prompt text, so this is where and how, not the words.";

    sealed class G
    {
        public string UseCase { get; set; } = "";
        public string? Department { get; set; }
        public DateTime? Bucket { get; set; }
        public long N { get; set; }
        public int People { get; set; }
    }

    /* bi.UseCases applies the rules (its one definition); the fact carries the key into it. */
    const string UseCase = "JOIN bi.UseCases AS uc ON uc.UseCaseKey = g.UseCaseKey";

    protected override async Task BuildAsync()
    {
        if (!await ops.HasAsync("dash.UseCaseRule") || await Db.ScalarAsync<int>("SELECT CASE WHEN OBJECT_ID(N'bi.UseCases', N'V') IS NULL THEN 0 ELSE 1 END") == 0) { Notice = "The board pack needs use cases, which are not set up here yet. An administrator sets this up by running Install-CopilotAudit.ps1 -Dashboard from the current release."; return; }
        /* Group the interactions first, then classify the groups: the rules run over thousands of rows, not millions. */
        /* In steps (2.4.2): the rules read once into #uc, one row a person and use case in #k, then a plain count.
           As one statement the optimizer ran the rules per group and counted distinct people: 22 seconds for a year. */
        var byDept = await Db.QueryAsync<G>($@"
SELECT UseCaseKey, UseCase INTO #uc FROM bi.UseCases;
WITH g AS (SELECT f.UserKey, f.UseCaseKey, COUNT_BIG(*) AS N
           FROM bi.FactInteraction AS f {Ctx.Scope("f.UserKey")}
           WHERE f.DateKey BETWEEN @k0 AND @k1
           GROUP BY f.UserKey, f.UseCaseKey)
SELECT g.UserKey, uc.UseCase, SUM(g.N) AS N INTO #k FROM g JOIN #uc AS uc ON uc.UseCaseKey = g.UseCaseKey GROUP BY g.UserKey, uc.UseCase;
SELECT k.UseCase, ISNULL(NULLIF(du.Department, N''), N'(no department)') AS Department, SUM(k.N) AS N, COUNT(*) AS People
FROM #k AS k JOIN bi.DimUser AS du ON du.UserKey = k.UserKey
GROUP BY k.UseCase, ISNULL(NULLIF(du.Department, N''), N'(no department)');", Ctx.Args(), timeout: 300);
        /* The totals add up the departments: each person has one department, so their people add up too.
           A second pass over the fact for the same numbers doubled the page's time (2.4.2). */
        var totals = byDept.GroupBy(x => x.UseCase).Select(g => new G { UseCase = g.Key, N = g.Sum(x => x.N), People = g.Sum(x => x.People) }).ToList();
        if (totals.Count == 0) { Notice = $"No Copilot use from {Ctx.PeriodLabel}."; return; }
        /* From the lead-in a 28-day average needs (2.5.0). */
        var trend = await Db.QueryAsync<G>($@"
WITH g AS (SELECT {Ctx.BucketSql("d")} AS Bucket, f.UseCaseKey, COUNT_BIG(*) AS N
           FROM bi.FactInteraction AS f {Ctx.Scope("f.UserKey")}
           JOIN bi.DimDate AS d ON d.DateKey = f.DateKey
           WHERE f.DateKey BETWEEN @kt0 AND @k1
           GROUP BY {Ctx.BucketSql("d")}, f.UseCaseKey)
SELECT uc.UseCase, g.Bucket, SUM(g.N) AS N FROM g {UseCase} GROUP BY uc.UseCase, g.Bucket;", Ctx.Args(), timeout: 300);

        var all = totals.Sum(x => x.N);
        var ranked = totals.OrderByDescending(x => x.N).ToList();
        Kpis.Add(new("Most common", ranked[0].UseCase, $"{Pct(Ratio(ranked[0].N, all))} of interactions"));
        Kpis.Add(new("Use cases seen", Num(ranked.Count)));
        var agents = totals.Where(x => x.UseCase == "Working with an agent").Sum(x => x.N);
        Kpis.Add(new("Interactions through an agent", Pct(Ratio(agents, all)), "of all interactions in the period"));

        /* Fixed colors per use case, by overall size (the chart shows at most eight). */
        var top = ranked.Take(7).Select(x => x.UseCase).ToList();
        string Key(string u) => top.Contains(u) ? u : "Everything else";
        /* "Everything else" where the trend has a use case outside the top seven, which includes one seen only before the period (the average reads those days). */
        var names = top.Concat(ranked.Count > 7 || trend.Any(x => !top.Contains(x.UseCase)) ? ["Everything else"] : []).ToList();
        var useAt = trend.ToLookup(x => (Key(x.UseCase), x.Bucket!.Value), x => x.N);
        Panels.Add(new("Interactions by use case", Chart.Columns(Labels(),
            names.Select((n, i) => new Series(n, Average(b => useAt[(n, b.Key)].Sum()), i + 1)).ToList(),
            Fmt.Average, "Interactions by use case, 28-day average"), TrendNote));

        var t = new Table { Headers = ["Use case", "Interactions", "Share of interactions", "People", "Interactions per person"], Numeric = new HashSet<int> { 1, 2, 3, 4 } };
        foreach (var x in ranked) t.Rows.Add([x.UseCase, x.N, Pct(Ratio(x.N, all)), x.People, x.People > 0 ? Math.Round(x.N / (double)x.People, 1) : null]);
        Panels.Add(new("Use cases", t.Render(), "People: used Copilot for this at least once in the period; one person counts under every use case they had.", "half"));

        /* Each department's mix, for departments big enough to show. */
        var deptPeople = byDept.GroupBy(x => x.Department!).ToDictionary(g => g.Key, g => g.Sum(x => x.N));
        var big = SmallGroups.Shown(await Db.QueryAsync<(string Dept, int People)>($@"
SELECT ISNULL(NULLIF(du.Department, N''), N'(no department)'), COUNT(*) FROM bi.DimUser AS du {Ctx.Scope("du.UserKey")}
WHERE du.AccountEnabled = 1 AND du.DirectoryStatus = N'Found' GROUP BY ISNULL(NULLIF(du.Department, N''), N'(no department)');", Ctx.Args()), d => d.People, Ctx.MinGroupSize)
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
