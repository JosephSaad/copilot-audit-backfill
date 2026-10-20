using System.Globalization;
using CopilotInsights.Charts;
using CopilotInsights.Data;
using CopilotInsights.Infrastructure;
using Microsoft.AspNetCore.Html;
using Microsoft.AspNetCore.Mvc;

namespace CopilotInsights.Pages.Forecasting;

/// <summary>
/// The Copilot Credit commitment (2.7.2): a prepaid commitment such as the Copilot Credit Pre-Purchase Plan (P3), set
/// against the metered credits the Power Platform admin center's consumption report gives: drawn, remaining, the burn
/// rate, when it runs out at that rate or what it leaves unused, the straight-line plan, and the environments and lines
/// of business drawing most. An estimate until Microsoft's applied figure is loaded from Azure Cost Management.
/// </summary>
public sealed class CreditCommitmentModel(PageServices s, CreditChargeData data) : InsightsPage(s)
{
    public override string Title => "Credit commitment";
    public override string Persona => "Forecasting";
    public override string? OwnWindow => "the commitment's term";
    public override bool UsesFilters => false;
    public override string Intro => "A prepaid Copilot Credit commitment, such as the Pre-Purchase Plan (P3), against the credits drawn from it: what is left, when it runs out at today's rate, and who draws most.";

    protected override async Task BuildAsync()
    {
        ShowDone();
        if (!Viewer.OrgWide && !Viewer.Has(Roles.Licensing))
        {
            Notice = "A commitment is the whole tenant's, so this page needs the whole-organization view or the Licensing role.";
            return;
        }
        if (!await data.CommitmentsReadyAsync() || !await data.ReadyAsync())
        {
            Notice = "Commitments cannot be recorded here yet. An administrator sets this up by running Install-CopilotAudit.ps1 -Dashboard from 2.7.2 or later.";
            return;
        }
        var all = await data.CommitmentsAsync();
        var chosen = int.TryParse(Request.Query["id"].FirstOrDefault(), out var id) ? all.FirstOrDefault(c => c.CommitmentId == id) : null;
        var c = chosen ?? await data.InForceAsync();
        if (c is null)
        {
            Notice = "No commitment is recorded yet." + (Viewer.IsAdmin ? " Add the commitment below: its amount, term and what one credit draws from it." : " An administrator records it on this page.");
            if (Viewer.IsAdmin) Admin(all);
            return;
        }
        var st = await data.StatusAsync(c);
        string M(decimal x) => CostRules.Money(Math.Round(x, MidpointRounding.AwayFromZero), c.Currency);
        var warnDays = await S.Settings.IntAsync("CreditCommitmentAlertDays", 30);
        var warning = CreditChargeRules.Warning(st, warnDays);

        var choose = all.Count < 2 ? "" : "<p class=\"sub\">Commitment: " + string.Join(" · ", all.Select(x =>
            $"<a href=\"{System.Net.WebUtility.HtmlEncode(Ctx.Url(Request.Path, extra: $"id={x.CommitmentId}"))}\"{(x.CommitmentId == c.CommitmentId ? " class=\"on\"" : "")}>{System.Net.WebUtility.HtmlEncode(x.Name)}</a>")) + "</p>";
        var stateText = !st.Started ? $"<b>{H(c.Name)}</b> starts on {c.StartDate:d MMM yyyy}."
            : $"<b>{H(c.Name)}</b>, {c.StartDate:d MMM yyyy} to {c.EndDate:d MMM yyyy}{(c.Scope is null ? "" : $", for {H(c.Scope)}")}: from the consumption report loaded to {st.AsOf:d MMM yyyy}. " +
              "<b>An estimate</b>: the metered credits × what one credit draws, not Microsoft's applied figure." + (warning is null ? "" : $" <span class=\"status-bad\">{H(warning)}</span>");
        Panels.Add(new("State", new HtmlString($"<p class=\"res-state\" role=\"status\">{stateText}</p>{choose}"),
            "Microsoft applies the commitment to eligible Copilot Credit use as it happens; its own figure is in Azure Cost Management. Prepaid capacity packs are paid for apart and draw nothing from it.", "full"));

        Kpis.Add(new($"Commitment ({c.Currency})", M(c.Amount), $"{c.StartDate:d MMM yyyy} to {c.EndDate:d MMM yyyy}"));
        Kpis.Add(new($"Drawn so far ({c.Currency})", M(st.Drawn), $"{Pct((double)(st.Drawn / c.Amount))} drawn; plan to date {M(st.PlanToDate)}",
            st.Variance is > 0.10 ? "warn" : null,
            Why: await WhyAsync($"The pay-as-you-go credits in the consumption report from {c.StartDate:d MMM yyyy} × what one credit draws ({c.DrawRate.ToString("0.######", CultureInfo.InvariantCulture)} {c.Currency}). Plan to date: the commitment spread evenly over its term.")));
        Kpis.Add(new($"Remaining ({c.Currency})", M(st.Remaining), $"{Count(st.DaysLeft, "day")} of the term left", st.Remaining <= 0 ? "crit" : null));
        Kpis.Add(new("Runs out", st.RunsOut is { } r ? r.ToString("d MMM yyyy") : "Not at today's rate",
            st.RunsOut is { } r2 && r2 < c.EndDate ? $"{(c.EndDate - r2).Days:#,0} days before the term ends" : $"{M(st.UnusedAtEnd)} unused at the end",
            warning is null ? null : "warn",
            Why: await WhyAsync($"The remaining {M(st.Remaining)} at the last 30 days' rate of {M(st.BurnPerDay)} a day.", "CreditCommitmentAlertDays")));
        if (st.Beyond > 0) Kpis.Add(new($"Pay-as-you-go beyond it ({c.Currency})", M(st.Beyond), "drawn after the commitment was used up", "crit"));

        /* Drawn against the plan, week by week over the term so far. */
        var weeks = new List<(DateTime End, decimal Drawn, decimal Plan)>();
        decimal run = 0; var term = Math.Max(1, (c.EndDate - c.StartDate).Days + 1);
        for (var w = c.StartDate.AddDays(6); w <= (st.AsOf < c.EndDate ? st.AsOf : c.EndDate).AddDays(6); w = w.AddDays(7))
        {
            var end = w > st.AsOf ? st.AsOf : w;
            run = st.Daily.Where(d => d.Day <= end).Sum(d => d.Drawn);
            weeks.Add((end, Math.Min(run, c.Amount), Math.Round(c.Amount * Math.Min(term, (end - c.StartDate).Days + 1) / term, 2)));
            if (end == st.AsOf) break;
        }
        if (weeks.Count > 1)
            Panels.Add(new("Drawn against the plan", Chart.Line(weeks.Select(x => x.End.ToString("d MMM")).ToList(),
                [new Series("Drawn", weeks.Select(x => (double?)x.Drawn).ToList(), 1), new Series("Plan: even over the term", weeks.Select(x => (double?)x.Plan).ToList(), 8)],
                Fmt.Count, $"Drawn and plan to date ({c.Currency})"),
                "Above the plan, the commitment runs out early and pay-as-you-go follows; below it, part of it may go unused at the end of the term.", "half"));

        /* Who draws most: the environments, and the lines of business they are routed to. */
        var top = await Db.QueryAsync<(string Env, string? Path, string? OrgId, decimal Credits)>(@"
SELECT d.EnvironmentName, r.Path, r.OrgId, SUM(ISNULL(d.Credits, 0))
FROM audit.PowerPlatformCreditDay AS d
OUTER APPLY (SELECT TOP (1) x.Path, x.OrgId FROM dash.CreditRoute AS x
             WHERE x.EnvironmentName = d.EnvironmentName AND x.EffectiveFrom <= d.[Day] AND (x.EffectiveTo IS NULL OR x.EffectiveTo >= d.[Day])
             ORDER BY x.EffectiveFrom DESC, x.RouteId DESC) AS r
WHERE d.Product = 'Copilot Studio' AND d.Billing = 'PayAsYouGo' AND d.[Day] BETWEEN @s AND @e
GROUP BY d.EnvironmentName, r.Path, r.OrgId ORDER BY 4 DESC;", new { s = c.StartDate, e = c.EndDate < st.AsOf ? c.EndDate : st.AsOf });
        var t = new Table { Headers = ["Environment", "Path", "Line of business", "Credits", $"Drawn ({c.Currency})", "Share"], Numeric = new HashSet<int> { 3, 4, 5 }, EmptyText = "Nothing drawn yet." };
        var sum = top.Sum(x => x.Credits);
        foreach (var x in top.Take(25))
            t.Rows.Add([x.Env, x.Path is null ? "Not routed" : CreditPaths.Label(x.Path), x.OrgId, x.Credits.ToString("#,0", CultureInfo.InvariantCulture), M(x.Credits * c.DrawRate), sum > 0 ? Pct((double)(x.Credits / sum)) : ""]);
        CsvTable = t;
        Panels.Add(new("Who draws most", t.Render(), "The environments whose metered credits draw on the commitment, with the route Leadership › Credit chargeback gives them.", "half", CsvUrl()));

        if (Viewer.IsAdmin) Admin(all);
    }

    static string H(string? s) => System.Net.WebUtility.HtmlEncode(s ?? "");

    void Admin(List<CreditCommitment> all)
    {
        var t = new Table { Headers = ["Name", "Amount", "Currency", "One credit draws", "From", "To", "Scope", "Note", ""], Numeric = new HashSet<int> { 1, 3 }, EmptyText = "No commitment yet." };
        foreach (var c in all)
            t.Rows.Add([c.Name, c.Amount, c.Currency, c.DrawRate.ToString("0.######", CultureInfo.InvariantCulture), c.StartDate, c.EndDate, c.Scope, c.Note,
                new HtmlString(Form("delcommit", Hidden("id", c.CommitmentId) + RemoveBox($"Remove the commitment {c.Name}")))]);
        var add = "<h3>Record a commitment</h3>" + Form("commit",
            Input("name", "Name", "Copilot Credit P3", 24) + Input("amount", "Amount", null, 10) + Input("currency", "Currency", null, 5) +
            Input("draw", "One credit draws", "0.01", 8) + Input("start", "From", DateTime.Today.ToString("yyyy-MM-dd"), 10, "date") +
            Input("end", "To", DateTime.Today.AddYears(1).AddDays(-1).ToString("yyyy-MM-dd"), 10, "date") + Input("scope", "Azure scope", null, 20) + Input("note", "Note", null, 20) + Button("Add"));
        Panels.Add(new("Commitments", new HtmlString(t.Render().Value + add),
            "From the purchase: its amount in its currency, the term, and what one Copilot Credit draws from it (0.01 at list price). Every change is in the audit log.", "full"));
    }

    public Task<IActionResult> OnPostCommitAsync(string? name, string? amount, string? currency, string? draw, DateTime? start, DateTime? end, string? scope, string? note) => AdminChangeAsync(async () =>
    {
        var n = (name ?? "").Trim();
        if (n.Length is 0 or > 200) throw new ArgumentException("give the commitment a name.");
        if (!decimal.TryParse(amount, NumberStyles.Number, CultureInfo.InvariantCulture, out var a) || a <= 0 || a > 1_000_000_000) throw new ArgumentException("the amount is a number above 0.");
        if (decimal.Round(a, 2) != a) throw new ArgumentException("the amount has at most two decimals.");
        if (!decimal.TryParse(draw, NumberStyles.Number, CultureInfo.InvariantCulture, out var dr) || dr <= 0 || dr > 100) throw new ArgumentException("what one credit draws is a number above 0, such as 0.01.");
        if (decimal.Round(dr, 6) != dr) throw new ArgumentException("what one credit draws has at most six decimals.");
        if (start is null || end is null || end <= start) throw new ArgumentException("the term ends after it starts.");
        var cur = string.IsNullOrWhiteSpace(currency) ? null : currency.Trim().ToUpperInvariant();
        if (cur is { Length: > 8 }) throw new ArgumentException("the currency is a code such as USD.");
        await Db.ExecuteAsync(@"INSERT INTO dash.CreditCommitment (Name, Amount, Currency, DrawRate, StartDate, EndDate, Scope, Note, UpdatedBy)
VALUES (@n, @a, @cur, @dr, @start, @end, @scope, @note, @by);",
            new { n, a, cur, dr, start, end, scope = string.IsNullOrWhiteSpace(scope) ? null : scope.Trim()[..Math.Min(200, scope.Trim().Length)],
                  note = string.IsNullOrWhiteSpace(note) ? null : note.Trim()[..Math.Min(400, note.Trim().Length)], by = Viewer.Upn ?? Viewer.WindowsName });
        return $"Commitment recorded: {n}, {a:#,0.##} {cur ?? "(the license currency)"} from {start:yyyy-MM-dd} to {end:yyyy-MM-dd}, one credit drawing {dr.ToString(CultureInfo.InvariantCulture)}.";
    });

    public Task<IActionResult> OnPostDelcommitAsync(int id) => AdminChangeAsync(async () =>
    {
        var row = (await Db.QueryAsync<(string Name, decimal Amount, DateTime Start, DateTime End)>("SELECT Name, Amount, StartDate, EndDate FROM dash.CreditCommitment WHERE CommitmentId = @id;", new { id })).FirstOrDefault();
        if (row.Name is null) return "That commitment was already gone.";
        await Db.ExecuteAsync("DELETE FROM dash.CreditCommitment WHERE CommitmentId = @id;", new { id });
        return $"Commitment removed: {row.Name}, {row.Amount:#,0.##} from {row.Start:yyyy-MM-dd} to {row.End:yyyy-MM-dd}.";
    });
}
