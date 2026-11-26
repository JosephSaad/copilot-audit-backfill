using System.Globalization;
using System.Net;
using CopilotInsights.Charts;
using CopilotInsights.Data;
using CopilotInsights.Infrastructure;
using Microsoft.AspNetCore.Html;
using Microsoft.AspNetCore.Mvc;

namespace CopilotInsights.Pages.Forecasting;

/// <summary>
/// Every spending limit set where AI spend happens (Copilot spending policies, the gateway's token limits, GitHub
/// budgets, and limits an administrator enters), each beside its spend: where it stands, where it is heading, when
/// it is reached, and what to set it to next quarter. Read only: the limits are set where they live.
/// </summary>
public sealed class SpendLimitsModel(PageServices s, SpendLimitData data) : InsightsPage(s)
{
    public override string Title => "Spend limits";
    public override string Persona => "Forecasting";
    public override bool UsesTeam => false;
    public override bool CountsWholeOrganization => true;
    public override string? OwnWindow => "each limit's own period";
    public override string Intro => "Each spending limit set on AI, where it is set, beside what has been spent against it: Copilot spending policies, the AI gateway's token limits, GitHub budgets, and limits entered here. " +
        "Which will be reached before their period ends, and what each should be next quarter. The dashboard never changes a limit: an administrator does, where it lives.";

    static string H(string? s) => WebUtility.HtmlEncode(s ?? "");
    static string Amount(decimal? v, string unit) => v is null ? "—" : unit switch
    {
        "USD" => CostRules.Money(Math.Round(v.Value, 0), "USD"),
        "Tokens" => CreditRules.Tokens((long)v.Value) + " tokens",
        _ => v.Value.ToString("#,0", CultureInfo.InvariantCulture) + " credits",
    };

    static string Where(SpendLimitData.LimitRow l) => l.Source switch
    {
        "Graph" => "Microsoft 365 admin center › Copilot › Billing and usage",
        "Gateway" => "The gateway's policy in Azure API Management",
        "GitHub" => "GitHub › Enterprise › Billing and licensing › Budgets",
        _ => "Entered here" + (l.Kind == "CopilotResource" ? "; set in the Power Platform admin center" : ""),
    };

    protected override async Task BuildAsync()
    {
        ShowDone();
        if (!await data.ReadyAsync())
        {
            Notice = await NotSetUpAsync("Spend limits cannot be tracked here yet", "3.5.0", "dash.SpendLimit", "audit.SpendLimitSource");
            return;
        }
        await data.SyncAsync();
        var limits = await data.LimitsAsync(hidden: Viewer.IsAdmin && Request.Query["hidden"] == "1");
        if (limits.Count == 0)
        {
            Notice = "No spending limit is known yet. The loaders read them nightly once set up (tools\\Import-SpendLimits.ps1 for Copilot spending policies and GitHub budgets, tools\\Import-GatewayTokenUsage.ps1 for the gateway's token limits; docs\\OPERATIONS.md, Spend limits), and an administrator can enter one below.";
            if (Viewer.IsAdmin) await AdminAsync([]);
            return;
        }
        var measured = await data.MeasureAsync(limits, Ctx);
        var open = int.TryParse(Request.Query["limit"], out var id) ? measured.FirstOrDefault(m => m.Limit.LimitId == id) : null;

        var live = measured.Where(m => m.Limit.GoneUtc is null).ToList();
        var reached = live.Count(m => m.Position?.Reached == true || m.KeysOver > 0 && m.Limit.Kind is "CopilotUser");
        var heading = live.Count(m => m.Position is { Reached: false, ReachedOn: not null });
        Kpis.Add(new("Limits tracked", Num(live.Count), $"{Count(live.Count(m => m.Limit.Source != "Entered"), "read at its source", "read at their sources")}, {live.Count(m => m.Limit.Source == "Entered"):#,0} entered", "accent"));
        Kpis.Add(new("Projected to be reached", Num(heading), "before their period ends, at the last seven days' pace", heading > 0 ? "warn" : "good",
            Why: await WhyAsync("Limits not yet reached whose spend so far plus the average of the last seven complete days for each day left reaches the limit before the period ends.", "SpendLimitAlertDays")));
        Kpis.Add(new("Reached", Num(reached), "this period: calls over them are refused or billed beyond the limit", reached > 0 ? "crit" : "good"));
        var noUnit = live.Count(m => m.Limit.L2Upn is null);
        Kpis.Add(new("With no business unit", Num(noUnit), "set one below so its alert reaches its executives", noUnit > 0 ? "warn" : null));

        var t = new Table
        {
            Headers = ["Limit", "Kind", "Applies to", "Business unit", "Limit a period", "Period", "Spent so far", "Share", "Projected at period end", "Reached on", "Suggested next quarter"],
            Numeric = new HashSet<int> { 7 }, EmptyText = "No limit.",
        };
        foreach (var m in live.OrderByDescending(m => m.Position?.Reached == true).ThenByDescending(m => m.Warn).ThenByDescending(m => m.Position?.Share ?? -1))
        {
            var l = m.Limit;
            var p = m.Position;
            var name = new Link(l.Name, Ctx.Url(Request.Path, extra: $"limit={l.LimitId}"));
            var applies = (l.Scope ?? (l.ScopeType == "AllUsers" ? "Everyone" : "")) + (l.PerUser ? (l.Kind is "CopilotUser" ? ", each person" : ", each key") : "") +
                          (m.Key is { } k ? $" (largest: {(k.Length == 0 ? "not stamped" : k)}{(m.Keys > 1 && !SpendLimits.IsShort(l.Period) ? $"; {m.KeysOver} of {m.Keys} over" : "")})" : "") +
                          (l.Kind == "CopilotUser" && m.Keys > 0 ? $" ({m.KeysOver:#,0} of {m.Keys:#,0} billed people estimated over)" : "");
            t.Rows.Add([name, SpendLimits.KindLabel(l.Kind), applies, l.L2Upn is null ? "—" : GatewayTokens.UnitName(l.L2Name, l.L2Upn, l.L3Name, l.L3Upn),
                Amount(l.Amount, l.Unit), l.Period,
                m.NotMeasured is { } nm && p is null ? nm : l.Kind == "CopilotUser" ? $"{Amount(p?.Spent, l.Unit)} (the highest person's month)" : Amount(p?.Spent, l.Unit),
                p?.Share is { } sh ? new HtmlString($"<span class=\"{(sh >= 1 ? "status-bad" : m.Warn ? "status-warn" : "")}\">{Pct(sh)}</span>") : (object)"",
                p is null || l.Kind == "CopilotUser" ? "" : p.TooEarly ? "Too early to project" : Amount(p.Projected, l.Unit),
                p?.Reached == true ? new HtmlString($"<span class=\"status-bad\">Reached{(p.ReachedOn is { } r ? $" {r:d MMM}" : "")}</span>") : p?.ReachedOn is { } on ? $"Projected {on:d MMM}" : (object)"",
                m.Suggested is { } sg ? $"{Amount(sg, l.Unit)}{(l.Amount is { } a && a > 0 ? $" ({(sg - a) / a:+0%;-0%;0%})" : "")}" : ""]);
        }
        CsvTable = t;
        Panels.Add(new("Every limit", t.Render(),
            "Spent so far: in the limit's own period, to the newest day its source has (environments: the consumption report; the gateway: its token metric, each counter key taken as the business unit it stamps; GitHub: its bill; a person's credits: Credit use's estimate). " +
            "Projected: at the last seven complete days' pace. Suggested: the highest of the next three months' forecast at the share set (P80 or P95), plus the headroom, from six months of spend or more.", "full", CsvUrl()));

        /* The quarter's planning: what to take to each place a limit is set. */
        var plan = new Table { Headers = ["Business unit", "Limit", "Where it is set", "Now", "Suggested", "Change"], Numeric = new HashSet<int> { 5 }, EmptyText = "No monthly limit has six months of spend yet." };
        foreach (var m in live.Where(m => m.Suggested is not null).OrderBy(m => m.Limit.L2Name ?? "~").ThenBy(m => m.Limit.Name))
            plan.Rows.Add([m.Limit.L2Upn is null ? "(none)" : GatewayTokens.UnitName(m.Limit.L2Name, m.Limit.L2Upn, m.Limit.L3Name, m.Limit.L3Upn), m.Limit.Name, Where(m.Limit),
                Amount(m.Limit.Amount, m.Limit.Unit), Amount(m.Suggested, m.Limit.Unit), m.Limit.Amount is { } a && a > 0 ? $"{(m.Suggested!.Value - a) / a:+0%;-0%;0%}" : ""]);
        Panels.Add(new("Next quarter's limits", plan.Render(),
            "Each monthly limit's suggested value for the next quarter beside its current one, by business unit: the list to take to the admin center, the gateway and GitHub. The suggestion is a forecast, not a decision.", "full"));

        if (open is not null) OneLimit(open);
        if (Viewer.IsAdmin) await AdminAsync(measured);
    }

    void OneLimit(SpendLimitData.Measured m)
    {
        var l = m.Limit;
        var body = $"<p class=\"sub\">Set in: {H(Where(l))}. {H(l.Note)}</p>";
        if (m.AsOf is { } asOf && m.Position is { } p && l.Kind != "CopilotUser")
        {
            var (start, end) = SpendLimits.PeriodOf(l.Period, asOf);
            var days = Enumerable.Range(0, (end - start).Days + 1).Select(i => start.AddDays(i)).ToList();
            decimal run = 0;
            var spent = days.Select(d => d <= asOf ? (double?)(double)(run += m.Daily.GetValueOrDefault(d)) : null).ToList();
            var rate = p.TooEarly || p.Projected is null || p.DaysLeft == 0 ? 0 : (double)((p.Projected.Value - p.Spent) / p.DaysLeft);
            var proj = days.Select(d => d >= asOf ? (double?)((double)p.Spent + rate * (d - asOf).Days) : null).ToList();
            var series = new List<Series> { new("Spent", spent, 1), new("Projected", proj, 3) };
            if (l.Amount is { } a) series.Add(new Series("Limit", days.Select(_ => (double?)(double)a).ToList(), 5));
            body += Chart.Line(days.Select(d => d.ToString("d MMM", CultureInfo.InvariantCulture)).ToList(), series, Fmt.Count, $"{l.Name}: spent against the limit this period").Value;
        }
        if (m.Months.Count > 0)
            body += Chart.Columns(m.Months.TakeLast(12).Select(x => x.Month.ToString("MMM yyyy", CultureInfo.InvariantCulture)).ToList(),
                [new Series("Spent a month", m.Months.TakeLast(12).Select(x => (double?)(double)x.Spent).ToList(), 1)], Fmt.Count, $"{l.Name}: spent a month").Value +
                (l.Amount is { } am && l.Period == SpendLimits.Monthly ? $"<p class=\"sub\">The limit is {H(Amount(am, l.Unit))} a month.</p>" : "");
        if (m.Short is { } sp)
            body += $"<p>Of the last 30 days, {Pct(sp.At80)} reached 80% of it and {Pct(sp.At100)} reached it.</p>";
        if (m.NotMeasured is { } nm) body += $"<p class=\"muted\">{H(nm)}</p>";
        Panels.Add(new(l.Name, new HtmlString(body), "This period's spend day by day against the limit, the projection at the last seven days' pace, and the last twelve months.", "full"));
    }

    async Task AdminAsync(IReadOnlyList<SpendLimitData.Measured> measured)
    {
        var at = new Table { Headers = ["Limit", "Source", "Business unit (L2 and L3 executives) and when to warn", ""], EmptyText = "No limit yet." };
        foreach (var m in measured)
        {
            var l = m.Limit;
            at.Rows.Add([l.Name + (l.GoneUtc is not null ? " (no longer at its source)" : "") + (l.Hidden ? " (hidden)" : ""), l.Source,
                new HtmlString(InlineForm("unit", Hidden("id", l.LimitId) + Input("l2", "L2 executive", l.L2Upn, 22) + Input("l3", "L3 executive", l.L3Upn, 22) +
                    Input("warn", "Warn at %", l.WarnAtPercent?.ToString(CultureInfo.InvariantCulture), 4, "number") + Button("Save", quiet: true))),
                new HtmlString(InlineForm("hide", Hidden("id", l.LimitId) + Hidden("hidden", l.Hidden ? "0" : "1") + Button(l.Hidden ? "Show" : "Hide", quiet: true)))]);
        }
        var kinds = new[] { ("CopilotResource", "Copilot spending policy (environment or agent)"), ("CopilotUser", "Copilot spending policy (per person)"), ("GatewayQuota", "Gateway token quota"), ("GitHubBudget", "GitHub budget") };
        var add = "<h3>Enter a limit</h3><p class=\"sub\">For a limit no loader reads: an environment's Copilot Credit limit set in the Power Platform admin center, say. Applies to: the environment as the consumption report names it, the GitHub organization, or the gateway value.</p>" +
            Form("add", Input("name", "Name", null, 28) + Select("kind", "Kind", kinds, "CopilotResource") +
                Select("scopeType", "Applies to a", new[] { ("Environment", "Environment"), ("Agent", "Agent"), ("AllUsers", "Everyone"), ("Organization", "GitHub organization"), ("Enterprise", "GitHub enterprise") }, "Environment") +
                Input("scope", "Applies to", null, 22) + Input("amount", "Amount", null, 10, "number") + Select("period", "Period", new[] { "Monthly", "Weekly", "Daily", "Yearly" }, "Monthly") +
                Input("note", "Note", null, 24) + Button("Add"));
        Panels.Add(new("Limits and their business units", new HtmlString(at.Render().Value + add +
            $"<p class=\"sub\"><a href=\"{H(Ctx.Url(Request.Path, extra: "hidden=1"))}\">Show hidden limits</a></p>"),
            "The business unit a limit belongs to (its L2 executive, and L3 within it) decides who its alert reaches once executives are switched on; warn at sends it when the spend passes that share. A limit read at its source keeps these when it is read again.", "full"));

        var set = await S.Settings.AllAsync();
        Panels.Add(new("The alert", new HtmlString(Form("alert",
            Input("to", "Send to (semicolons between)", set.GetValueOrDefault("SpendLimitAlertTo"), 40) +
            Select("execs", "Also the unit's L2 and L3 executives", new[] { ("0", "Not yet"), ("1", "Yes") }, set.GetValueOrDefault("SpendLimitAlertToExecutives") ?? "0") +
            Input("days", "Warn when reached this many days early", set.GetValueOrDefault("SpendLimitAlertDays") ?? "5", 4, "number") +
            Input("headroom", "Headroom in the suggestion (%)", set.GetValueOrDefault("SpendLimitHeadroomPercent") ?? "10", 4, "number") +
            Select("from", "Suggest from", new[] { ("P80", "P80"), ("P95", "P95") }, set.GetValueOrDefault("SpendLimitSuggestFrom") ?? "P80") + Button("Save"))),
            "A spend limit will be reached: emailed once a day while any limit is reached or projected to be, first to the contacts named here; once the executives are switched on, each limit's also to its unit's L2 and L3 executives. Empty: no email.", "full"));
    }

    public Task<IActionResult> OnPostUnitAsync(int id, string? l2, string? l3, int? warn) => AdminChangeAsync(async () =>
    {
        l2 = string.IsNullOrWhiteSpace(l2) ? null : l2.Trim();
        l3 = string.IsNullOrWhiteSpace(l3) ? null : l3.Trim();
        if (l2 is null && l3 is not null) throw new ArgumentException("give the L2 executive first: the L3 is within the L2's unit.");
        foreach (var upn in new[] { l2, l3 }.OfType<string>())
            if (await Db.ScalarAsync<int>("SELECT COUNT(*) FROM dash.Person WHERE Upn = @upn;", new { upn }) == 0) throw new ArgumentException($"{upn} is not in the directory.");
        if (warn is < 1 or > 100) throw new ArgumentException("warn at is a share from 1 to 100, or empty.");
        var name = await Db.ScalarAsync<string?>("SELECT Name FROM dash.SpendLimit WHERE LimitId = @id;", new { id }) ?? throw new ArgumentException("that limit is gone.");
        await Db.ExecuteAsync("UPDATE dash.SpendLimit SET L2Upn = @l2, L3Upn = @l3, WarnAtPercent = @warn, UpdatedBy = @by, UpdatedUtc = SYSUTCDATETIME() WHERE LimitId = @id;",
            new { id, l2, l3, warn, by = Viewer.Upn ?? Viewer.WindowsName });
        return $"{name}: business unit {(l2 is null ? "none" : l2 + (l3 is null ? "" : " › " + l3))}, warn at {(warn is null ? "when projected to be reached" : warn + "%")}.";
    });

    public Task<IActionResult> OnPostHideAsync(int id, string? hidden) => AdminChangeAsync(async () =>
    {
        var name = await Db.ScalarAsync<string?>("SELECT Name FROM dash.SpendLimit WHERE LimitId = @id;", new { id }) ?? throw new ArgumentException("that limit is gone.");
        await Db.ExecuteAsync("UPDATE dash.SpendLimit SET Hidden = @h, UpdatedBy = @by, UpdatedUtc = SYSUTCDATETIME() WHERE LimitId = @id;", new { id, h = hidden == "1", by = Viewer.Upn ?? Viewer.WindowsName });
        return hidden == "1" ? $"{name} hidden: it is left out of the page and the alert." : $"{name} shown again.";
    });

    /// <summary>An entered limit, checked. Pure, for the tests.</summary>
    public static (string Name, string Kind, string ScopeType, string Scope, string Unit, decimal Amount, string Period) CheckLimit(string? name, string? kind, string? scopeType, string? scope, string? amount, string? period)
    {
        var n = (name ?? "").Trim();
        if (n.Length is 0 or > 400) throw new ArgumentException("give the limit a name.");
        var k = kind is "CopilotResource" or "CopilotUser" or "GatewayQuota" or "GitHubBudget" ? kind : throw new ArgumentException("choose the kind of limit.");
        var st = scopeType is "Environment" or "Agent" or "AllUsers" or "Organization" or "Enterprise" or "CounterKey" ? scopeType : throw new ArgumentException("choose what it applies to.");
        var sc = (scope ?? "").Trim();
        if (sc.Length == 0 && st != "AllUsers") throw new ArgumentException("name what it applies to: the environment, the organization or the gateway value.");
        if (!decimal.TryParse(amount, NumberStyles.Number, CultureInfo.InvariantCulture, out var a) || a <= 0) throw new ArgumentException("the amount is a number above 0.");
        var p = period is SpendLimits.Monthly or SpendLimits.Weekly or SpendLimits.Daily or SpendLimits.Yearly ? period : throw new ArgumentException("choose the period.");
        var unit = k switch { "GatewayQuota" => "Tokens", "GitHubBudget" => "USD", _ => "Credits" };
        return (n, k, st, sc, unit, a, p);
    }

    public Task<IActionResult> OnPostAddAsync(string? name, string? kind, string? scopeType, string? scope, string? amount, string? period, string? note) => AdminChangeAsync(async () =>
    {
        var c = CheckLimit(name, kind, scopeType, scope, amount, period);
        await Db.ExecuteAsync(@"INSERT INTO dash.SpendLimit (Source, Kind, Name, ScopeType, Scope, Unit, Amount, Period, PerUser, Note, UpdatedBy)
VALUES ('Entered', @Kind, @Name, @ScopeType, @Scope, @Unit, @Amount, @Period, @PerUser, @note, @by);",
            new { c.Kind, c.Name, c.ScopeType, Scope = c.Scope.Length == 0 ? null : c.Scope, c.Unit, c.Amount, c.Period, PerUser = c.Kind == "CopilotUser", note = string.IsNullOrWhiteSpace(note) ? null : note.Trim(), by = Viewer.Upn ?? Viewer.WindowsName });
        return $"Limit added: {c.Name}, {c.Amount:#,0} {c.Unit} {c.Period.ToLowerInvariant()}.";
    });

    public Task<IActionResult> OnPostAlertAsync(string? to, string? execs, int? days, int? headroom, string? from) => AdminChangeAsync(async () =>
    {
        if (days is < 0 or > 365) throw new ArgumentException("warn days are 0 to 365.");
        if (headroom is < 0 or > 200) throw new ArgumentException("the headroom is 0 to 200 percent.");
        var values = new Dictionary<string, string?>
        {
            ["SpendLimitAlertTo"] = string.IsNullOrWhiteSpace(to) ? null : string.Join(";", to.Split([';', ','], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)),
            ["SpendLimitAlertToExecutives"] = execs == "1" ? "1" : "0",
            ["SpendLimitAlertDays"] = (days ?? 5).ToString(CultureInfo.InvariantCulture),
            ["SpendLimitHeadroomPercent"] = (headroom ?? 10).ToString(CultureInfo.InvariantCulture),
            ["SpendLimitSuggestFrom"] = from == "P95" ? "P95" : "P80",
        };
        if (values["SpendLimitAlertTo"] is { } list && list.Split(';').Any(a => !a.Contains('@'))) throw new ArgumentException("each recipient is an email address.");
        foreach (var kv in values)
            await Db.ExecuteAsync(@"UPDATE dash.Setting SET Value = @v, UpdatedBy = @by, UpdatedUtc = SYSUTCDATETIME() WHERE Name = @n;
IF @@ROWCOUNT = 0 INSERT INTO dash.Setting (Name, Value, UpdatedBy) VALUES (@n, @v, @by);", new { n = kv.Key, v = kv.Value, by = Viewer.Upn ?? Viewer.WindowsName });
        S.Settings.Invalidate();
        return $"The alert goes to {values["SpendLimitAlertTo"] ?? "nobody (off)"}{(execs == "1" ? ", and each limit's L2 and L3 executives" : "")}.";
    });
}
