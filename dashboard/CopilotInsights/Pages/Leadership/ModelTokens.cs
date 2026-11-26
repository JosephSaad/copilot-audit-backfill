using System.Globalization;
using CopilotInsights.Charts;
using CopilotInsights.Data;
using CopilotInsights.Infrastructure;
using Microsoft.AspNetCore.Html;
using Microsoft.AspNetCore.Mvc;

namespace CopilotInsights.Pages.Leadership;

/// <summary>
/// Model tokens through the AI gateway, by business unit (an L2 and L3 executive pair): counts only, never money
/// (<see cref="GatewayTokens"/>). For the whole organization; administrators route the values the gateway stamps.
/// </summary>
public sealed class ModelTokensModel(PageServices s, GatewayTokens data) : InsightsPage(s)
{
    public override string Title => "Model tokens by business unit";
    public override string Persona => "Leadership";
    public override bool UsesTeam => false;
    public override bool CountsWholeOrganization => true;
    public override string Intro => "The model tokens each business unit used through the organization's AI gateway: its L2 and L3 executives, the prompt and completion tokens, and the models. " +
        "Counts of tokens only, as the gateway measures them; what the models cost is on AI spend across Azure.";

    static string C(long v) => v.ToString("#,0", CultureInfo.InvariantCulture);

    protected override async Task BuildAsync()
    {
        ShowDone();
        if (!await data.ReadyAsync())
        {
            Notice = await NotSetUpAsync("Model tokens by business unit cannot be shown here yet", "3.5.0", "audit.GatewayTokenDay", "dash.TokenRoute");
            return;
        }
        if (!await data.LoadedAsync())
        {
            Notice = "The AI gateway's tokens are not loaded yet. An administrator sets up tools\\Import-GatewayTokenUsage.ps1 (GatewayServices and GatewayWorkspaceId in the settings file; docs\\OPERATIONS.md, Model tokens), " +
                     "after the gateway's llm-emit-token-metric policy stamps a business-unit dimension. Data sources says what it needs.";
            if (Viewer.IsAdmin) await AdminAsync([]);
            return;
        }
        var lines = await data.LinesAsync(Ctx.Start, Ctx.End);
        var total = lines.Sum(l => l.Total);
        var units = lines.Where(l => l.L2Upn is not null).GroupBy(l => (l.L2Upn, l.L3Upn)).ToList();
        var loose = lines.Where(l => l.L2Upn is null).ToList();
        var last = await data.LastDayAsync();

        Kpis.Add(new("Model tokens", Num(total), $"{C(lines.Sum(l => l.Prompt))} prompt, {C(lines.Sum(l => l.Completion))} completion" + (last is { } d ? $"; loaded to {d:d MMM yyyy}" : ""), "accent",
            Why: await WhyAsync("Every token the gateway's token metric counted in the period, through every gateway loaded: prompt and completion tokens (streamed answers' counts are the gateway's estimate).")));
        Kpis.Add(new("Business units using models", Num(units.Count), $"{Num(units.Select(u => u.Key.L2Upn).Distinct().Count())} L2 executives' units",
            Why: await WhyAsync("Each L2 and L3 executive pair with tokens in the period: the gateway's business-unit value routed to it, or the org chart's for a value that is a person's sign-in.", "TokenUnitLevels")));
        var looseTokens = loose.Sum(l => l.Total);
        Kpis.Add(new("In no business unit", Pct(Ratio(looseTokens, total)), $"{C(looseTokens)} tokens: {Count(loose.Select(l => l.Value).Distinct().Count(), "value")} unmapped or not stamped",
            looseTokens > 0 ? "warn" : "good",
            Why: await WhyAsync("Tokens whose business-unit value has no route in effect and is nobody's sign-in, or that the gateway stamped none for. An administrator routes each value below.")));
        if (await data.FoundryTokensAsync(Ctx.Start, Ctx.End) is { } foundry && foundry > 0)
        {
            var bypass = Math.Max(0, foundry - total);
            Kpis.Add(new("Foundry tokens not through the gateway", Pct(Ratio(bypass, foundry)), $"{C(bypass)} of the {C(foundry)} Foundry counted", bypass > 0 ? "warn" : "good",
                Href: Open("/platforms/foundry"),
                Why: await WhyAsync("Azure Monitor's tokens for the Foundry and Azure OpenAI accounts loaded, less the gateway's: calls made to the models directly, which no business unit is counted for.")));
        }

        /* By month, the six largest L2 units and the rest. */
        var months = lines.Select(l => l.Month).Distinct().OrderBy(m => m).ToList();
        if (months.Count > 0)
        {
            var top = lines.Where(l => l.L2Upn is not null).GroupBy(l => l.L2Upn!).OrderByDescending(g => g.Sum(l => l.Total)).Take(6)
                .Select(g => (Upn: g.Key, Name: g.First().L2Name ?? g.Key)).ToList();
            var series = top.Select((t, i) => new Series(t.Name, months.Select(m => (double?)lines.Where(l => l.L2Upn == t.Upn && l.Month == m).Sum(l => l.Total)).ToList(), i + 1)).ToList();
            var rest = months.Select(m => (double?)lines.Where(l => l.Month == m && (l.L2Upn is null || top.All(t => t.Upn != l.L2Upn))).Sum(l => l.Total)).ToList();
            if (rest.Any(v => v > 0)) series.Add(new Series("Others and no unit", rest, top.Count + 1));
            Panels.Add(new("Tokens by L2 unit, by month", Chart.Columns(months.Select(m => m.ToString("MMM yyyy", CultureInfo.InvariantCulture)).ToList(), series, Fmt.Count, "Model tokens by L2 executive's unit, by month"),
                "The months of the period chosen; a month not over, or cut by the period, is part of a month.", "full"));
        }

        var t = new Table
        {
            Headers = ["L2 executive", "L3 executive", "Prompt tokens", "Completion tokens", "Tokens", "Share", "Calls", "Models"],
            Numeric = new HashSet<int> { 2, 3, 4, 5, 6 }, EmptyText = "No tokens reached a business unit in the period.",
        };
        foreach (var u in units.OrderByDescending(g => g.Sum(l => l.Total)))
        {
            var f = u.First();
            var tok = u.Sum(l => l.Total);
            t.Rows.Add([f.L2Name ?? f.L2Upn, f.L3Upn is null ? "(the whole unit)" : f.L3Name ?? f.L3Upn, C(u.Sum(l => l.Prompt)), C(u.Sum(l => l.Completion)), C(tok), Pct(Ratio(tok, total)), C(u.Sum(l => l.Calls)),
                string.Join(", ", u.GroupBy(l => l.Model).OrderByDescending(g => g.Sum(l => l.Total)).Select(g => g.Key.Length == 0 ? "(not stamped)" : g.Key).Take(4))]);
        }
        if (looseTokens > 0) t.Rows.Add(["In no business unit", "", C(loose.Sum(l => l.Prompt)), C(loose.Sum(l => l.Completion)), C(looseTokens), Pct(Ratio(looseTokens, total)), C(loose.Sum(l => l.Calls)), ""]);
        CsvTable = t;
        Panels.Add(new("By business unit", t.Render(),
            "Each L2 and L3 executive pair's tokens: a value routed to an L2 alone counts for the whole of the L2's unit. A value that is a person's sign-in counts for their own L2 and L3; " +
            "the L2 and L3 executives themselves count in their own units. Counts only, as the gateway measures them.", "full", CsvUrl()));

        var mt = new Table { Headers = ["Model", "Tokens", "Share", "Business units"], Numeric = new HashSet<int> { 1, 2, 3 }, EmptyText = "No model is stamped." };
        foreach (var g in lines.GroupBy(l => l.Model).OrderByDescending(g => g.Sum(l => l.Total)))
            mt.Rows.Add([g.Key.Length == 0 ? "(not stamped)" : g.Key, C(g.Sum(l => l.Total)), Pct(Ratio(g.Sum(l => l.Total), total)), g.Where(l => l.L2Upn is not null).Select(l => (l.L2Upn, l.L3Upn)).Distinct().Count()]);
        Panels.Add(new("By model", mt.Render(), "The model the gateway stamped on each call, where its policy names one.", "half"));

        if (Viewer.IsAdmin) await AdminAsync(loose);
    }

    async Task AdminAsync(List<GatewayTokens.Line> loose)
    {
        var from = Ctx.Start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (loose.Count > 0)
        {
            var ut = new Table { Headers = ["Value", "Why", "Tokens", "Route it"], Numeric = new HashSet<int> { 2 } };
            foreach (var g in loose.GroupBy(l => (l.Value, l.How)).OrderByDescending(g => g.Sum(l => l.Total)))
                ut.Rows.Add([g.Key.Value.Length == 0 ? "(empty)" : g.Key.Value, GatewayTokens.HowLabel(g.Key.How), C(g.Sum(l => l.Total)),
                    g.Key.How == GatewayTokens.NotStamped ? "Stamp a business unit in the gateway's policy" :
                    new HtmlString(InlineForm("route", Hidden("value", g.Key.Value) + Input("l2", "L2 executive (sign-in)", null, 22) + Input("l3", "L3 executive (optional)", null, 22) +
                        Hidden("from", from) + Button("Route", quiet: true)))]);
            Panels.Add(new("Values in no business unit", ut.Render(),
                "The gateway's business-unit values with no route in effect that are nobody's sign-in. Route each to its L2 executive, and L3 within it where it is one L3's, from the start of the period shown.", "full"));
        }
        var routes = await data.RoutesAsync();
        var rt = new Table { Headers = ["Value", "L2 executive", "L3 executive", "From", "To", "Note", ""], EmptyText = "No route yet: values that are sign-ins need none." };
        foreach (var r in routes)
            rt.Rows.Add([r.GatewayValue, r.L2Name ?? r.L2Upn, r.L3Upn is null ? "(the whole unit)" : r.L3Name ?? r.L3Upn, r.EffectiveFrom, r.EffectiveTo, r.Note,
                new HtmlString((r.EffectiveTo is null ? InlineForm("endroute", Hidden("id", r.RouteId) + Input("end", "End on", null, 10, "date") + Button("End", quiet: true)) : "") +
                    Form("delroute", Hidden("id", r.RouteId) + RemoveBox($"Remove the route of {r.GatewayValue} from {r.EffectiveFrom:yyyy-MM-dd}")))]);
        var add = "<h3>Route a value</h3>" + Form("route",
            Input("value", "Value the gateway stamps", null, 24) + Input("l2", "L2 executive (sign-in)", null, 24) + Input("l3", "L3 executive (optional)", null, 24) +
            Input("from", "From", new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), 10, "date") +
            Input("to", "To (optional)", null, 10, "date") + Input("note", "Note", null, 20) + Button("Add"));
        Panels.Add(new("Business unit routes", new HtmlString(rt.Render().Value + add),
            "Which unit each value the gateway stamps belongs to, from a date: a change is a new row, and the value's open route is ended the day before, so past months keep theirs. " +
            "A value that is a person's sign-in needs no route: the org chart gives their L2 and L3. Every change is in the audit log.", "full"));
    }

    public Task<IActionResult> OnPostRouteAsync(string? value, string? l2, string? l3, DateTime? from, DateTime? to, string? note) => AdminChangeAsync(async () =>
    {
        var r = GatewayTokens.Check(value, l2, l3, from, to, note);
        foreach (var upn in new[] { r.L2Upn, r.L3Upn }.OfType<string>())
            if (await Db.ScalarAsync<int>("SELECT COUNT(*) FROM dash.Person WHERE Upn = @upn;", new { upn }) == 0)
                throw new ArgumentException($"{upn} is not in the directory.");
        if (await Db.ScalarAsync<int>("SELECT COUNT(*) FROM dash.TokenRoute WHERE GatewayValue = @Value AND EffectiveFrom >= @From AND (@To IS NULL OR EffectiveFrom <= @To);", r) > 0)
            throw new ArgumentException($"{r.Value} has a route that starts on or after {r.From:yyyy-MM-dd}: end or remove it first.");
        var ended = await Db.ScalarAsync<int>(@"
SET XACT_ABORT ON;
BEGIN TRANSACTION;
UPDATE dash.TokenRoute SET EffectiveTo = DATEADD(DAY, -1, @From), UpdatedBy = @by, UpdatedUtc = SYSUTCDATETIME()
WHERE GatewayValue = @Value AND EffectiveFrom < @From AND (EffectiveTo IS NULL OR EffectiveTo >= @From);
DECLARE @ended INT = @@ROWCOUNT;
INSERT INTO dash.TokenRoute (GatewayValue, L2Upn, L3Upn, EffectiveFrom, EffectiveTo, Note, UpdatedBy) VALUES (@Value, @L2Upn, @L3Upn, @From, @To, @Note, @by);
COMMIT TRANSACTION;
SELECT @ended;", new { r.Value, r.L2Upn, r.L3Upn, r.From, r.To, r.Note, by = Viewer.Upn ?? Viewer.WindowsName });
        return $"Route added: {r.Value} to {r.L2Upn}{(r.L3Upn is null ? "" : " › " + r.L3Upn)} from {r.From:yyyy-MM-dd}" + (ended > 0 ? $"; its route before was ended on {r.From.AddDays(-1):yyyy-MM-dd}." : ".");
    });

    public Task<IActionResult> OnPostEndrouteAsync(int id, DateTime? end) => AdminChangeAsync(async () =>
    {
        if (end is null) throw new ArgumentException("give the last day the route is in effect.");
        var row = (await Db.QueryAsync<(string Value, DateTime From)>("SELECT GatewayValue, EffectiveFrom FROM dash.TokenRoute WHERE RouteId = @id;", new { id })).FirstOrDefault();
        if (row.Value is null) throw new ArgumentException("that route is gone.");
        if (end < row.From) throw new ArgumentException("the end comes on or after its start.");
        await Db.ExecuteAsync("UPDATE dash.TokenRoute SET EffectiveTo = @end, UpdatedBy = @by, UpdatedUtc = SYSUTCDATETIME() WHERE RouteId = @id;", new { id, end, by = Viewer.Upn ?? Viewer.WindowsName });
        return $"Route ended: {row.Value} from {row.From:yyyy-MM-dd}, now to {end:yyyy-MM-dd}.";
    });

    public Task<IActionResult> OnPostDelrouteAsync(int id) => AdminChangeAsync(async () =>
    {
        var row = (await Db.QueryAsync<(string Value, DateTime From)>("SELECT GatewayValue, EffectiveFrom FROM dash.TokenRoute WHERE RouteId = @id;", new { id })).FirstOrDefault();
        if (row.Value is null) return "That route was already gone.";
        await Db.ExecuteAsync("DELETE FROM dash.TokenRoute WHERE RouteId = @id;", new { id });
        return $"Route removed: {row.Value} from {row.From:yyyy-MM-dd}.";
    });
}
