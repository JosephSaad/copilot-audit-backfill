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
    public override string Intro => "The programme on one page, for the board or the steering group: use and adoption against the period before, the trend, targets, estimated value, licenses and risk signals. Print it, or save it as a PDF from the print dialog. Aggregates only.";

    protected override async Task BuildAsync()
    {
        var prev = OpsReports.Window(Ctx, Ctx.Start.AddDays(-1), Ctx.Days);
        var (t, p) = (await metrics.TotalsAsync(Ctx), await metrics.TotalsAsync(prev));
        static string Change(double now, double before) => before == 0 ? "none in the period before" : $"{now / before - 1:+0%;-0%;0%} on the period before";
        double? Adopt(Totals x) => x.LicensedNow > 0 ? (double)x.ActiveLicensed / x.LicensedNow : null;
        Kpis.Add(new("People using Copilot", Num(t.Active), Change(t.Active, p.Active)));
        Kpis.Add(new("Adoption", Pct(Adopt(t)), Adopt(p) is double a0 && Adopt(t) is double a1 ? $"{(a1 - a0) * 100:+0.#;-0.#;0} points" : "of licensed people"));
        Kpis.Add(new("Interactions", Num(t.Interactions), Change(t.Interactions, p.Interactions)));
        Kpis.Add(new("Licenses", Num(t.LicensedNow), $"{t.ActiveLicensed:#,0} used in the period"));

        Panels.Add(new("Print", new HtmlString(
            "<button type=\"button\" class=\"btn\" data-print>Print or save as PDF</button>" +
            $"<p class=\"print-only\">Copilot Insights board pack: {E(Ctx.ScopeLabel)}, {E(Ctx.PeriodLabel)}. Printed {DateTime.Now:d MMM yyyy HH:mm}.</p>"),
            "The navigation, filters and buttons are left off the printout.", "half no-print"));

        var tr = await metrics.TrendAsync(Ctx);
        Panels.Add(new("People using Copilot", Chart.Line(Labels(), [
            new Series("Licensed", tr.Select(w => (double?)w.ActiveLicensed).ToList(), 1),
            new Series("Copilot Chat (free)", tr.Select(w => (double?)w.ActiveUnlicensed).ToList(), 2)], Fmt.Count, "People using Copilot"),
            TrendNote + " Dashed rules mark events (Admin > Classifications and events)."));

        var targets = await reports.TargetsAsync(Ctx);
        if (targets.Count > 0)
        {
            var tt = new Table { Headers = ["Target", "For", "Now", "Should be by now", "Due", "Status"] };
            foreach (var r in targets)
                tt.Rows.Add([$"{Adoption.TargetsModel.Label(r.Metric)} {r.TargetValue:0.#}{(r.Metric == "ActivePeople" ? "" : "%")}", r.RootName ?? r.RootUpn ?? "Whole organisation",
                    r.Current is null ? null : Math.Round(r.Current.Value, 1), r.Expected is null ? null : Math.Round(r.Expected.Value, 1), r.DueDate,
                    new HtmlString($"<span class=\"{(r.Status is "Met" or "On track" ? "status-ok" : r.Status == "Behind" ? "status-warn" : "status-bad")}\">{E(r.Status)}</span>")]);
            Panels.Add(new("Targets", tt.Render(), null, "half"));
        }

        var v = await reports.ValueAsync(Ctx);
        var cur = await S.Settings.StringAsync("LicenseCurrency") ?? "USD";
        var price = decimal.TryParse(await S.Settings.StringAsync("LicenseMonthlyCost"), NumberStyles.Number, CultureInfo.InvariantCulture, out var pr) ? pr : 30m;
        var hours = v.ByApp.Sum(x => x.Hours);
        var worth = v.ByApp.Sum(x => x.Value);
        var cost = t.LicensedNow * price * Ctx.Days / 30m;
        Panels.Add(new("Estimated value", new HtmlString(
            $"<div class=\"kpis\"><div class=\"kpi\"><div class=\"klabel\">Hours saved (estimate)</div><div class=\"kvalue\">{hours:#,0}</div></div>" +
            $"<div class=\"kpi\"><div class=\"klabel\">Worth ({E(cur)})</div><div class=\"kvalue\">{worth:#,0}</div></div>" +
            $"<div class=\"kpi\"><div class=\"klabel\">License cost in the period ({E(cur)})</div><div class=\"kvalue\">{cost:#,0}</div></div>" +
            $"<div class=\"kpi\"><div class=\"klabel\">Return</div><div class=\"kvalue\">{(cost > 0 ? $"{worth / cost:0.0}×" : "—")}</div></div></div>"),
            $"At {v.DefaultMinutes:0.#} minutes an interaction unless set per app, and {v.HourlyCost:#,0.##} {cur} an hour: your organisation's own assumptions (Admin > Targets and assumptions).", "half"));

        var pool = (await ops.PoolAsync()).Where(x => x.IsCopilot).ToList();
        var counts = await reports.ActionCountsAsync(Ctx);
        var lt = new Table { Headers = ["Licenses", ""], Numeric = new HashSet<int> { 1 } };
        if (pool.Count > 0) { lt.Rows.Add(["Bought", pool.Sum(x => x.Bought)]); lt.Rows.Add(["Assigned", pool.Sum(x => x.Consumed)]); }
        lt.Rows.Add(["Held in scope", t.LicensedNow]);
        lt.Rows.Add(["Used in the period", t.ActiveLicensed]);
        lt.Rows.Add(["Unused for 30 days, to revoke", counts.GetValueOrDefault("revoke")]);
        lt.Rows.Add(["Free Copilot Chat users ready to upgrade", counts.GetValueOrDefault("upgrade")]);
        Panels.Add(new("Licenses", lt.Render(), $"At {price:#,0.##} {cur} a license a month, the unused licenses cost {counts.GetValueOrDefault("revoke") * price:#,0} {cur} a month.", "half"));

        var rp = await metrics.TrendAsync(prev);
        var rt = new Table { Headers = ["Risk signal", "This period", "Period before"], Numeric = new HashSet<int> { 1, 2 } };
        rt.Rows.Add(["Jailbreak attempts", tr.Sum(x => x.Jailbreak), rp.Sum(x => x.Jailbreak)]);
        rt.Rows.Add(["Prompt injection detected", tr.Sum(x => x.Injection), rp.Sum(x => x.Injection)]);
        rt.Rows.Add(["Blocked resources", tr.Sum(x => x.Blocked), rp.Sum(x => x.Blocked)]);
        rt.Rows.Add(["Interactions with labelled content", tr.Sum(x => x.Labelled), rp.Sum(x => x.Labelled)]);
        Panels.Add(new("Risk", rt.Render(), "From Copilot's own audit records; the detail is on Security and Compliance.", "half"));

        var units = (await metrics.UnitsAsync(Ctx)).Where(u => u.People >= Ctx.MinGroupSize && u.Licensed >= Ctx.MinGroupSize).ToList();
        if (units.Count > 1)
            Panels.Add(new("Adoption by unit", Chart.Bars(units.OrderByDescending(u => (double)u.ActiveLicensed / u.Licensed).Take(12)
                .Select(u => new Bar(u.Name, (double)u.ActiveLicensed / u.Licensed, Note: $"{u.ActiveLicensed:#,0} of {u.Licensed:#,0} licensed")).ToList(), Fmt.Percent, "Adoption by unit", 1),
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
        if (!await ops.HasAsync("dash.UseCaseRule") || await Db.ScalarAsync<int>("SELECT CASE WHEN OBJECT_ID(N'bi.UseCases', N'V') IS NULL THEN 0 ELSE 1 END") == 0) { Notice = "Use-case rules arrive with 2.3.0's operations schema: run Install-CopilotAudit.ps1 -Dashboard from this release."; return; }
        /* Group the interactions first, then classify the groups: the rules run over thousands of rows, not millions. */
        var byDept = await Db.QueryAsync<G>($@"
WITH g AS (SELECT f.UserKey, f.UseCaseKey, COUNT_BIG(*) AS N
           FROM bi.FactInteraction AS f {Ctx.Scope("f.UserKey")}
           WHERE f.DateKey BETWEEN @k0 AND @k1
           GROUP BY f.UserKey, f.UseCaseKey),
k AS (SELECT g.UserKey, g.N, uc.UseCase FROM g {UseCase})
SELECT k.UseCase, ISNULL(NULLIF(du.Department, N''), N'(no department)') AS Department, SUM(k.N) AS N, COUNT(DISTINCT k.UserKey) AS People
FROM k JOIN bi.DimUser AS du ON du.UserKey = k.UserKey
GROUP BY k.UseCase, ISNULL(NULLIF(du.Department, N''), N'(no department)');", Ctx.Args(), timeout: 300);
        var totals = await Db.QueryAsync<G>($@"
WITH g AS (SELECT f.UserKey, f.UseCaseKey, COUNT_BIG(*) AS N
           FROM bi.FactInteraction AS f {Ctx.Scope("f.UserKey")}
           WHERE f.DateKey BETWEEN @k0 AND @k1
           GROUP BY f.UserKey, f.UseCaseKey)
SELECT uc.UseCase, SUM(g.N) AS N, COUNT(DISTINCT g.UserKey) AS People FROM g {UseCase} GROUP BY uc.UseCase;", Ctx.Args(), timeout: 300);
        if (totals.Count == 0) { Notice = $"No Copilot use from {Ctx.PeriodLabel}."; return; }
        var trend = await Db.QueryAsync<G>($@"
WITH g AS (SELECT {Ctx.BucketSql("d")} AS Bucket, f.UseCaseKey, COUNT_BIG(*) AS N
           FROM bi.FactInteraction AS f {Ctx.Scope("f.UserKey")}
           JOIN bi.DimDate AS d ON d.DateKey = f.DateKey
           WHERE f.DateKey BETWEEN @k0 AND @k1
           GROUP BY {Ctx.BucketSql("d")}, f.UseCaseKey)
SELECT uc.UseCase, g.Bucket, SUM(g.N) AS N FROM g {UseCase} GROUP BY uc.UseCase, g.Bucket;", Ctx.Args(), timeout: 300);

        var all = totals.Sum(x => x.N);
        var ranked = totals.OrderByDescending(x => x.N).ToList();
        Kpis.Add(new("Most common", ranked[0].UseCase, $"{Pct(Ratio(ranked[0].N, all))} of interactions"));
        Kpis.Add(new("Use cases seen", Num(ranked.Count)));
        var agents = totals.Where(x => x.UseCase == "Working with an agent").Sum(x => x.N);
        Kpis.Add(new("With an agent", Pct(Ratio(agents, all)), "of interactions"));

        /* Fixed colours per use case, by overall size (the chart shows at most eight). */
        var top = ranked.Take(7).Select(x => x.UseCase).ToList();
        string Key(string u) => top.Contains(u) ? u : "Everything else";
        var names = top.Concat(ranked.Count > 7 ? ["Everything else"] : []).ToList();
        var buckets = Buckets();
        Panels.Add(new("Interactions by use case", Chart.Columns(Labels(),
            names.Select((n, i) => new Series(n, buckets.Select(b => (double?)trend.Where(x => x.Bucket == b.Key && Key(x.UseCase) == n).Sum(x => x.N)).ToList(), i + 1)).ToList(),
            Fmt.Count, "Interactions by use case"), TrendNote));

        var t = new Table { Headers = ["Use case", "Interactions", "Share", "People", "Interactions a person"], Numeric = new HashSet<int> { 1, 2, 3, 4 } };
        foreach (var x in ranked) t.Rows.Add([x.UseCase, x.N, Pct(Ratio(x.N, all)), x.People, x.People > 0 ? Math.Round(x.N / (double)x.People, 1) : null]);
        Panels.Add(new("Use cases", t.Render(), "People: used Copilot for this at least once in the period; one person counts under every use case they had.", "half"));

        /* Each department's mix, for departments big enough to show. */
        var deptPeople = byDept.GroupBy(x => x.Department!).ToDictionary(g => g.Key, g => g.Sum(x => x.N));
        var big = (await Db.QueryAsync<(string Dept, int People)>($@"
SELECT ISNULL(NULLIF(du.Department, N''), N'(no department)'), COUNT(*) FROM bi.DimUser AS du {Ctx.Scope("du.UserKey")}
WHERE du.AccountEnabled = 1 AND du.DirectoryStatus = N'Found' GROUP BY ISNULL(NULLIF(du.Department, N''), N'(no department)');", Ctx.Args()))
            .Where(d => d.People >= Ctx.MinGroupSize).Select(d => d.Dept).ToHashSet();
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
