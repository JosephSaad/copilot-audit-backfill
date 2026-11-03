using System.Globalization;
using System.Net;
using CopilotInsights.Data;
using CopilotInsights.Infrastructure;
using Microsoft.AspNetCore.Html;
using Microsoft.AspNetCore.Mvc;

namespace CopilotInsights.Pages.Leadership;

/// <summary>
/// Copilot Credit chargeback by line of business: each Power Platform environment's Copilot Credits, from the
/// admin center's consumption report, charged along the route its environment is registered to: a line of business's
/// Productivity or Business Application path, Enterprise Shared (funded centrally, never charged to a line of business),
/// or Innovation. The month to date is never certified; month-end snapshots are kept as they were, so Finance can
/// reproduce any month. Leaders see the lines of business led from inside their part; the whole organization and the
/// Licensing role see all, with Enterprise Shared, what is not routed, and the checks. Administrators keep the register.
/// </summary>
public sealed class CreditChargebackModel(PageServices s, CreditChargeData data) : InsightsPage(s)
{
    public override string Title => "Credit chargeback";
    public override string Persona => "Leadership";
    public override string? OwnWindow => "a calendar month";
    public override bool UsesFilters => false;
    public override string Intro => "Each line of business's Copilot Credits a month, by the path its environments are routed to, and what that charges it. Enterprise Shared, and credits from environments not yet routed, are shown apart.";

    static string H(string? s) => WebUtility.HtmlEncode(s ?? "");
    /* A rate as it is kept, to six decimals: money rounds to cents, and 0.012 a credit read as 0.01. */
    static string Rate(decimal r, string cur) => $"{r.ToString("0.######", CultureInfo.InvariantCulture)} {cur}";

    protected override async Task BuildAsync()
    {
        /* With the Governance Workbench on, the lines of business are its funding owners: a Workbench database that does
           not answer gives a reference, not a failed page. */
        try { await BuildChargebackAsync(); }
        catch (Exception ex) when (data.ByFundingOwner && ex is Microsoft.Data.SqlClient.SqlException or InvalidOperationException or TimeoutException)
        {
            Panels.Clear();
            Kpis.Clear();
            Notice = Failure(ex, "reading the Workbench's lines of business for Credit chargeback");
        }
    }

    async Task BuildChargebackAsync()
    {
        ShowDone();
        if (!await data.ReadyAsync())
        {
            Notice = await NotSetUpAsync("Credit chargeback cannot be kept here yet", "2.7.2", "dash.CreditRoute", "audit.PowerPlatformCreditDay");
            return;
        }
        var whole = Ctx.Root is null && (Viewer.OrgWide || Viewer.Has(Roles.Licensing));
        var months = await data.MonthsAsync();
        if (months.Count == 0)
        {
            Notice = "No Copilot Credits are loaded yet. An administrator loads the Power Platform admin center's consumption report with tools\\Import-CostCsv.ps1 -Table PowerPlatformCreditDay (docs\\OPERATIONS.md, Credit chargeback).";
            if (Viewer.IsAdmin) await AdminAsync();
            return;
        }
        var month = DateTime.TryParseExact(Request.Query["month"].FirstOrDefault(), "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out var m) && months.Contains(m) ? m : months[0];
        var snap = Request.Query["snap"].FirstOrDefault() == "1" ? await data.SnapshotAsync(Ctx, month) : null;
        var v = snap ?? await data.CurrentAsync(Ctx, month);
        if (v is null) { Notice = "No Copilot Credits are loaded yet."; return; }
        var cur = v.Currency;
        string M(decimal? x) => x is { } d ? CostRules.Money(Math.Round(d, MidpointRounding.AwayFromZero), cur) : "Pending Finance Decision";
        string C(decimal x) => x.ToString("#,0", CultureInfo.InvariantCulture);

        /* Which month, and in what state, before anything else. */
        var choose = string.Join(" · ", months.Take(12).Select(x =>
            $"<a href=\"{H(Ctx.Url(Request.Path, extra: $"month={x:yyyy-MM}"))}\"{(x == month && snap is null ? " class=\"on\"" : "")}>{x:MMM yyyy}</a>"));
        var state = v.IsSnapshot
            ? $"<b>Snapshot of {v.Month:MMMM yyyy}</b>, kept on {v.SnapshotTaken:d MMM yyyy}: <b>Operational, not certified</b>. <a href=\"{H(Ctx.Url(Request.Path, extra: $"month={month:yyyy-MM}"))}\">The month as loaded now</a>."
            : $"<b>{v.Month:MMMM yyyy}{(v.Complete ? "" : " to date")}</b>, from the consumption report loaded to {v.DataTo:d MMM yyyy}: <b>not certified</b>." +
              (v.Rate is null ? " No charge rate is set: charges show <b>Pending Finance Decision</b>." : $" Each credit is charged at {Rate(v.Rate.Value, cur)}.");
        Panels.Add(new("State", new HtmlString($"<p class=\"res-state\" role=\"status\">{state}</p><p class=\"sub\">Month: {choose}</p>"),
            "A month's figures change until its last day is loaded; the nightly run then keeps it as a snapshot, never rewritten.", "full"));

        var lob = v.Lines.Where(l => l.OrgId is not null && l.Path != CreditPaths.EnterpriseShared).ToList();
        var shared = v.Lines.Where(l => l.Path == CreditPaths.EnterpriseShared).Sum(l => l.Credits);
        var unrouted = v.Lines.Where(l => l.Path == CreditPaths.Unmapped).ToList();
        var lobCredits = lob.Sum(l => l.Credits);
        Kpis.Add(new("Copilot Credits", Num((long)Math.Round(v.Credits)), whole ? $"every environment, {v.Month:MMMM yyyy}" : "the lines of business led from inside your part",
            Href: Open("/licensing/credit-use"),
            Why: await WhyAsync($"Copilot Studio's credits in the Power Platform admin center's consumption report for {v.Month:MMMM yyyy}, loaded to {v.DataTo:d MMM yyyy}, prepaid and pay-as-you-go. AI Builder's credits are not Copilot Credits and are left out. Licensing › Credit use estimates who used them.")));
        Kpis.Add(new($"Charged to lines of business ({cur})", M(v.Charge(lobCredits)), $"{C(lobCredits)} credits" + (v.Rate is { } r ? $" at {Rate(r, cur)}" : ""),
            Why: await WhyAsync($"The credits of every environment routed to a line of business (its Productivity, Business Application or sponsored Innovation path: {C(lobCredits)}) × the charge rate" +
                (v.Rate is { } r2 ? $" ({Rate(r2, cur)} a credit)." : ", none set yet."), "CreditChargeRate")));
        if (whole)
        {
            Kpis.Add(new("Enterprise Shared", Num((long)Math.Round(shared)), "credits funded centrally, never charged to a line of business",
                Why: await WhyAsync("The credits of every environment routed to Enterprise Shared in the register below: HR, enterprise help, compliance-required and critical employee agents, funded centrally.")));
            var un = unrouted.Sum(l => l.Credits);
            Kpis.Add(new("Not routed", Num((long)Math.Round(un)), $"{Count(unrouted.Count, "environment")} with no route", un > 0 ? "crit" : null,
                Why: await WhyAsync("The credits of environments the register has no route for on the day they were used. Nobody is charged for them: route each environment below, from the day it began.")));
            var paid = v.Lines.Sum(l => l.PaygCost ?? 0);
            var paidCur = v.Lines.Select(l => l.Currency).FirstOrDefault(c => !string.IsNullOrEmpty(c)) ?? cur;
            Kpis.Add(new($"Billed pay-as-you-go ({paidCur})", CostRules.Money(Math.Round(paid), paidCur), $"{C(v.Lines.Sum(l => l.PaygCredits))} metered credits",
                Why: await WhyAsync("What Microsoft billed for the metered credits, as the consumption report gives it: what is paid, beside what is charged back.")));
        }

        /* One row for each line of business, its paths side by side; then what is no line of business's. */
        var t = new Table
        {
            Headers = ["Line of business", "Org ID", "Cost center", "LoB Productivity", "LoB Business Application", "Innovation", "Credits", $"Charge ({cur})"],
            Numeric = new HashSet<int> { 3, 4, 5, 6, 7 },
            EmptyText = whole ? "No environment is routed to a line of business yet: an administrator registers them below." : "No line of business led from inside this part of the organization has credits this month.",
        };
        decimal P(IEnumerable<CreditLine> ls, string path) => ls.Where(l => l.Path == path).Sum(l => l.Credits);
        foreach (var g in lob.GroupBy(l => l.OrgId!, StringComparer.OrdinalIgnoreCase).OrderBy(g => g.First().OrgName ?? g.Key))
        {
            var total = g.Sum(l => l.Credits);
            var orgLink = Open("/leadership/reservations");
            t.Rows.Add([g.First().OrgName is { } on && orgLink is not null ? new Link(on, orgLink) : (object)(g.First().OrgName ?? (data.ByFundingOwner ? "(not a Workbench funding owner)" : "(not in the reservation register)")), g.Key, string.Join(", ", g.Select(l => l.CostCenter).Where(c => !string.IsNullOrEmpty(c)).Distinct()),
                C(P(g, CreditPaths.Productivity)), C(P(g, CreditPaths.BusinessApplication)), C(P(g, CreditPaths.Innovation)), C(total), M(v.Charge(total))]);
        }
        t.Rows.Add([new HtmlString("<b>Lines of business</b>"), "", "", C(P(lob, CreditPaths.Productivity)), C(P(lob, CreditPaths.BusinessApplication)), C(P(lob, CreditPaths.Innovation)), C(lobCredits), M(v.Charge(lobCredits))]);
        if (whole)
        {
            t.Rows.Add(["Enterprise Shared", "", "", "", "", "", C(shared), "Funded centrally"]);
            var lone = v.Lines.Where(l => l.Path == CreditPaths.Innovation && l.OrgId is null).Sum(l => l.Credits);
            if (lone > 0) t.Rows.Add(["Innovation with no sponsor", "", "", "", "", C(lone), C(lone), "Funded centrally"]);
            var un = unrouted.Sum(l => l.Credits);
            t.Rows.Add([new HtmlString(un > 0 ? "<span class=\"status-bad\">Not routed</span>" : "Not routed"), "", "", "", "", "", C(un), un > 0 ? "To register" : "None"]);
        }
        if (whole)
            t.Rows.Add([new HtmlString("<b>All credits</b>"), "", "", C(P(v.Lines, CreditPaths.Productivity)), C(P(v.Lines, CreditPaths.BusinessApplication)), C(P(v.Lines, CreditPaths.Innovation)),
                C(v.Lines.Sum(l => l.Credits)), ""]);
        CsvTable = t;
        Panels.Add(new(v.IsSnapshot ? $"Lines of business, {v.Month:MMMM yyyy}, as kept" : "Lines of business", t.Render(),
            "Each environment's Copilot Credits (Copilot Studio's; AI Builder's are not Copilot Credits), charged to the line of business its route names. " +
            "A route that changed during the month charges each day to the route in effect that day. Enterprise Shared is never charged to a line of business. " +
            "The charge is the credits × one rate, however Microsoft was paid: prepaid capacity, a commitment, or pay-as-you-go. The lines of business and their Org IDs are Copilot reservations'; a commitment's draw is on Forecasting › Credit commitment.", "full", CsvUrl()));

        EnvironmentsPanel(v, M, C);
        if (whole && !v.IsSnapshot) await ExceptionsAsync(unrouted);
        SnapshotsPanel(await data.SnapshotsAsync(Ctx), cur, v.IsSnapshot ? v.Month : null, months, month);
        if (Viewer.IsAdmin) await AdminAsync();
    }

    void EnvironmentsPanel(CreditChargeView v, Func<decimal?, string> M, Func<decimal, string> C)
    {
        var t = new Table
        {
            Headers = ["Environment", "Path", "Line of business", "Cost center", "Billing policy", "Financial owner", "Credits", "Prepaid", "Pay-as-you-go", "Charge"],
            Numeric = new HashSet<int> { 6, 7, 8, 9 }, EmptyText = "No credits this month.",
        };
        foreach (var l in v.Lines.OrderByDescending(l => l.Credits))
            t.Rows.Add([l.EnvironmentName, new HtmlString(l.Path == CreditPaths.Unmapped ? "<span class=\"status-bad\">Not routed</span>" : H(CreditPaths.Label(l.Path))),
                l.OrgName ?? l.OrgId, l.CostCenter, l.BillingPolicy, PrivacyMode ? null : l.FinancialOwner, C(l.Credits), C(l.PrepaidCredits), C(l.PaygCredits),
                CreditPaths.NeedsOrg(l.Path) || (l.Path == CreditPaths.Innovation && l.OrgId is not null) ? M(v.Charge(l.Credits)) : ""]);
        Panels.Add(new("Environments", t.Render(),
            "Each environment as the consumption report names it, matched to the register by that name. Prepaid: credits from capacity packs; pay-as-you-go: metered, billed through the billing policy's Azure subscription." +
            (v.IsSnapshot ? " A snapshot keeps the route; the billing policy and owner are today's register." : ""), "full"));
    }

    /* What needs someone's attention before the figures can be relied on. */
    async Task ExceptionsAsync(List<CreditLine> unrouted)
    {
        var mis = await data.MisroutedAsync(Ctx);
        var t = new Table { Headers = ["Exception", "How many", "What to do"], Numeric = new HashSet<int> { 1 }, Sortable = false,
            EmptyText = data.ByFundingOwner
                ? "No exceptions: every environment with credits is routed, every route names a Workbench funding owner, and no agent is built in one line of business's environment by another's people."
                : "No exceptions: every environment with credits is routed, every route names a line of business in the register, and no agent is built in one line of business's environment by another's people." };
        void Add(string what, int n, string todo) { if (n > 0) t.Rows.Add([what, n, todo]); }
        Add("Environments with credits and no route", unrouted.Count, "Register each one's path and line of business: " + string.Join(", ", unrouted.Select(l => l.EnvironmentName).Take(12)) + (unrouted.Count > 12 ? ", ..." : ""));
        var unknown = await data.UnknownOrgsAsync();
        if (data.ByFundingOwner)
            Add("Routes naming an Org ID that is not a Workbench funding owner", unknown.Count, "Correct the route to the funding owner's code (Ownership registry › Funding owners), or add the funding owner there: " + string.Join(", ", unknown));
        else
            Add("Routes naming an Org ID not in the reservation register", unknown.Count, "Add the line of business to Copilot reservations (reserved 0 if it reserves no seats), or correct the Org ID: " + string.Join(", ", unknown));
        if (mis is null)
            t.Rows.Add(["Agents in another line of business's environment: not checked", "", data.ByFundingOwner
                ? "The check needs the agent inventory (tools\\Import-AgentWorkbook.ps1)."
                : "The check needs the agent inventory (tools\\Import-AgentWorkbook.ps1) and reservation leaders who cover the agents' owners."]);
        else Add("Agents built by one line of business in another's environment", mis.Count, "Move the agent to its own line of business's environment, or confirm the route is right: listed below.");
        Panels.Add(new("Exceptions", t.Render(), data.ByFundingOwner ? "Raised from this month's credits, the Workbench's funding owners, the agent inventory and the seat tracking groups." : "Raised from this month's credits, the register, the agent inventory and the org chart.", "half"));
        if (mis is { Count: > 0 })
        {
            var names = await data.LineNamesAsync();
            string Line(string code) => names.TryGetValue(code, out var n) ? n : code;
            var named = Viewer.Has(Roles.Agents) && !PrivacyMode;
            var mt = new Table { Headers = named ? ["Agent", "Environment", "Charged to", "Owner", "Owner's line of business"] : ["Agent", "Environment", "Charged to", "Owner's line of business"] };
            foreach (var x in mis)
                mt.Rows.Add(named ? [x.Agent, x.Environment, Line(x.EnvOrg), x.Owner, Line(x.OwnerOrg)] : [x.Agent, x.Environment, Line(x.EnvOrg), Line(x.OwnerOrg)]);
            Panels.Add(new("Agents in another line of business's environment", mt.Render(),
                (data.ByFundingOwner
                    ? "From the agent inventory: the environment each agent lives in, and the line of business whose seat tracking group holds its owner's Copilot seat. "
                    : "From the agent inventory: the environment each agent lives in, and the line of business its owner works in under the reservation register's leaders. ") +
                "Its credits are charged to the environment's line of business." +
                (named ? "" : " Owners are named for the Agents role."), "half"));
        }
    }

    void SnapshotsPanel(List<(DateTime Month, DateTime Taken, decimal Credits, decimal ToLoBs, decimal Unrouted, decimal? Rate)> snaps, string cur, DateTime? shown, List<DateTime> months, DateTime month)
    {
        var t = new Table { Headers = ["Month", "Kept on", "Credits", "Credits charged to lines of business", "Not routed", $"Charge ({cur})", ""], Numeric = new HashSet<int> { 2, 3, 4, 5 }, Sortable = false,
            EmptyText = "No month-end snapshot yet: the nightly run keeps each month once its last day's credits are loaded and the register has a route in effect." };
        foreach (var s in snaps)
            t.Rows.Add([s.Month.ToString("MMMM yyyy", CultureInfo.InvariantCulture), s.Taken.Date, s.Credits.ToString("#,0", CultureInfo.InvariantCulture), s.ToLoBs.ToString("#,0", CultureInfo.InvariantCulture),
                new HtmlString(s.Unrouted > 0 ? $"<span class=\"status-bad\">{s.Unrouted:#,0}</span>" : "0"),
                CreditChargeRules.Charge(s.ToLoBs, s.Rate) is { } c ? Math.Round(c) : "Pending Finance Decision",
                new HtmlString((shown == s.Month ? "Shown" : $"<a href=\"{H(Ctx.Url(Request.Path, extra: $"month={s.Month:yyyy-MM}&snap=1"))}\">View</a>") +
                    (Viewer.IsAdmin ? " " + InlineForm("retake", Hidden("month", s.Month.ToString("yyyy-MM")) + Button("Take it again", $"Take the {s.Month:MMMM yyyy} snapshot again from today's register and rate? The month as kept now is replaced.", quiet: true)) : ""))]);
        Panels.Add(new("Month-end snapshots", t.Render(), "Each month's chargeback, kept once at the rate in force: Operational, not certified. Not routed: credits kept without a route; route those environments, then an administrator takes the month again, and the audit log records it.", "half"));
    }

    /* ---- The register and the charge rate: administrators --------------------------------------------------- */

    async Task AdminAsync()
    {
        var routes = await data.RoutesAsync();
        var rt = new Table { Headers = ["Environment", "Path", "Org ID", "Cost center", "Financial owner", "Billing policy", "From", "To", "Note", ""], EmptyText = "The register is empty." };
        foreach (var r in routes)
            rt.Rows.Add([r.EnvironmentName, CreditPaths.Label(r.Path), r.OrgId, r.CostCenter, r.FinancialOwner, r.BillingPolicy, r.EffectiveFrom, r.EffectiveTo, r.Note,
                new HtmlString((r.EffectiveTo is null ? InlineForm("endroute", Hidden("id", r.RouteId) + Input("end", "End on", null, 10, "date") + Button("End", quiet: true)) : "") +
                    Form("delroute", Hidden("id", r.RouteId) + RemoveBox($"Remove the route of {r.EnvironmentName} from {r.EffectiveFrom:yyyy-MM-dd}")))]);
        var paths = CreditPaths.All.Select(p => (p, CreditPaths.Label(p)));
        var add = "<h3>Route an environment</h3>" + Form("route",
            Input("environment", "Environment", null, 26) + Select("path", "Path", paths, CreditPaths.Productivity) + Input("orgId", "Org ID", null, 12) +
            Input("costCenter", "Cost center", null, 10) + Input("owner", "Financial owner", null, 22) + Input("policy", "Billing policy", null, 18) +
            Input("from", "From", new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1).ToString("yyyy-MM-dd"), 10, "date") + Input("to", "To (optional)", null, 10, "date") +
            Input("note", "Note", null, 20) + Button("Add"));
        var paste = "<h3>Route several at once</h3><p class=\"sub\">One environment a line: environment, path, Org ID, cost center, from (yyyy-mm-dd), and optionally financial owner, billing policy and to. " +
                    "Path is Productivity, BusinessApplication, EnterpriseShared or Innovation (or its label). A header line is skipped.</p>" +
            Form("pasteroutes", "<label>Rows<textarea name=\"rows\" rows=\"5\" cols=\"90\" placeholder=\"Retail-Prod,Productivity,LOB-01,CC-100,2026-11-01,finance.retail@contoso.com,BP-Retail\"></textarea></label>" + Button("Add these"));
        Panels.Add(new("Credit routing register", new HtmlString(rt.Render().Value + add + paste),
            $"{routes.Count:#,0} routes over {routes.Select(r => r.EnvironmentName).Distinct(StringComparer.OrdinalIgnoreCase).Count():#,0} environments. The environment is named as the consumption report names it. " +
            (data.ByFundingOwner
                ? "LoB Productivity and LoB Business Application name the line of business charged: its funding owner's code in the Governance Workbench (Ownership registry › Funding owners); Enterprise Shared names none; Innovation may name its sponsor. "
                : "LoB Productivity and LoB Business Application name the line of business charged (its Org ID in Copilot reservations); Enterprise Shared names none; Innovation may name its sponsor. ") +
            "A change is a new row from its date: the environment's open row is ended the day before, so past months keep their route. Every change is in the audit log.", "full"));

        var rate = await S.Settings.StringAsync("CreditChargeRate");
        var (shown, cur) = await data.RateAsync();
        Panels.Add(new("Charge rate", new HtmlString(Form("chargerate",
                Input("rate", $"Charge a credit ({cur})", rate, 10) + Button("Save")) +
            $"<p class=\"sub\">{(string.IsNullOrWhiteSpace(rate) ? (shown is { } s ? $"Not set: the price list's pay-as-you-go credit, {Rate(s, cur)}, is used." : "Not set, and the price list has no pay-as-you-go credit: charges are Pending Finance Decision.") : "Set by an administrator.")}</p>"),
            "Finance's rate for one Copilot Credit, after any discount: the same for every line of business, however Microsoft was paid.", "half"));
    }

    static string Clip(string? s, int n) => (s ?? "").Trim() is var t && t.Length > n ? t[..n] : (s ?? "").Trim();
    static string? Opt(string? s, int n) => string.IsNullOrWhiteSpace(s) ? null : Clip(s, n);

    /// <summary>One route, checked. Pure, for the tests.</summary>
    public sealed record RouteInput(string Environment, string Path, string? OrgId, string? CostCenter, string? Owner, string? Policy, DateTime From, DateTime? To, string? Note);

    /// <summary>A path as typed: its value or its label, whatever the case. Null: not a path.</summary>
    public static string? ParsePath(string? s)
    {
        var x = (s ?? "").Trim();
        return CreditPaths.All.FirstOrDefault(p => string.Equals(p, x, StringComparison.OrdinalIgnoreCase) || string.Equals(CreditPaths.Label(p), x, StringComparison.OrdinalIgnoreCase)
            || string.Equals(p, x.Replace(" ", ""), StringComparison.OrdinalIgnoreCase) || string.Equals("LoB" + p, x.Replace(" ", ""), StringComparison.OrdinalIgnoreCase));
    }

    public static RouteInput Check(string? environment, string? path, string? orgId, string? costCenter, string? owner, string? policy, DateTime? from, DateTime? to, string? note)
    {
        var env = Clip(environment, 200);
        if (env.Length == 0) throw new ArgumentException("name the environment, as the consumption report names it.");
        var p = ParsePath(path) ?? throw new ArgumentException("choose the path: LoB Productivity, LoB Business Application, Enterprise Shared or Innovation.");
        var org = Opt(orgId, 64);
        if (CreditPaths.NeedsOrg(p) && org is null) throw new ArgumentException($"{CreditPaths.Label(p)} is charged to a line of business: give its Org ID.");
        if (p == CreditPaths.EnterpriseShared && org is not null) throw new ArgumentException("Enterprise Shared is funded centrally and names no line of business: leave the Org ID empty.");
        if (from is null) throw new ArgumentException("give the date the route applies from.");
        if (to is not null && to < from) throw new ArgumentException("the end date comes after the start.");
        return new(env, p, org, Opt(costCenter, 100), Opt(owner, 256), Opt(policy, 200), from.Value.Date, to?.Date, Opt(note, 400));
    }

    /// <summary>
    /// Adds the routes in one transaction, all or none: an environment's route open on a new row's start is ended the day
    /// before; a new row that would overlap one starting on or after it, or fall inside one running past its end, is refused.
    /// </summary>
    async Task<int> AddAsync(IReadOnlyList<RouteInput> rows)
    {
        if (rows.GroupBy(r => r.Environment, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1))
            throw new ArgumentException("each environment once a paste: add its later route in a second paste.");
        var json = System.Text.Json.JsonSerializer.Serialize(rows.Select(r => new { r.Environment, r.Path, r.OrgId, r.CostCenter, r.Owner, r.Policy, From = r.From.ToString("yyyy-MM-dd"), To = r.To?.ToString("yyyy-MM-dd"), r.Note }));
        var clash = await Db.ScalarAsync<string?>(@"
SELECT TOP (1) CONCAT(n.Environment, N' from ', CONVERT(CHAR(10), n.[From], 120))
FROM OPENJSON(@json) WITH (Environment NVARCHAR(200), [From] DATE, [To] DATE) AS n
JOIN dash.CreditRoute AS r ON r.EnvironmentName = n.Environment AND r.EffectiveFrom >= n.[From] AND (n.[To] IS NULL OR r.EffectiveFrom <= n.[To])
ORDER BY n.Environment;", new { json });
        if (clash is not null) throw new ArgumentException($"{clash} overlaps a route that starts on or after it: end or remove that route first.");
        var inside = await Db.ScalarAsync<string?>(@"
SELECT TOP (1) CONCAT(n.Environment, N' from ', CONVERT(CHAR(10), n.[From], 120), N' to ', CONVERT(CHAR(10), n.[To], 120))
FROM OPENJSON(@json) WITH (Environment NVARCHAR(200), [From] DATE, [To] DATE) AS n
JOIN dash.CreditRoute AS r ON r.EnvironmentName = n.Environment AND r.EffectiveFrom < n.[From] AND (r.EffectiveTo IS NULL OR r.EffectiveTo > n.[To])
WHERE n.[To] IS NOT NULL ORDER BY n.Environment;", new { json });
        if (inside is not null) throw new ArgumentException($"{inside} falls inside a route that runs past its end: end that route on the day before, add this one, then add the route that follows it.");
        return await Db.ScalarAsync<int>(@"
SET XACT_ABORT ON;
BEGIN TRANSACTION;
DECLARE @n TABLE (Environment NVARCHAR(200), Path VARCHAR(24), OrgId NVARCHAR(64), CostCenter NVARCHAR(100), Owner NVARCHAR(256), Policy NVARCHAR(200), [From] DATE, [To] DATE, Note NVARCHAR(400));
INSERT INTO @n SELECT * FROM OPENJSON(@json) WITH (Environment NVARCHAR(200), Path VARCHAR(24), OrgId NVARCHAR(64), CostCenter NVARCHAR(100), Owner NVARCHAR(256), Policy NVARCHAR(200), [From] DATE, [To] DATE, Note NVARCHAR(400));
UPDATE r SET EffectiveTo = DATEADD(DAY, -1, n.[From]), UpdatedBy = @by, UpdatedUtc = SYSUTCDATETIME()
FROM dash.CreditRoute AS r JOIN @n AS n ON n.Environment = r.EnvironmentName
WHERE r.EffectiveFrom < n.[From] AND (r.EffectiveTo IS NULL OR r.EffectiveTo >= n.[From]);
DECLARE @ended INT = @@ROWCOUNT;
INSERT INTO dash.CreditRoute (EnvironmentName, Path, OrgId, CostCenter, FinancialOwner, BillingPolicy, EffectiveFrom, EffectiveTo, Note, UpdatedBy)
SELECT Environment, Path, OrgId, CostCenter, Owner, Policy, [From], [To], Note, @by FROM @n;
COMMIT TRANSACTION;
SELECT @ended;", new { json, by = Viewer.Upn ?? Viewer.WindowsName });
    }

    static string Said(RouteInput r) => $"{r.Environment}: {CreditPaths.Label(r.Path)}{(r.OrgId is null ? "" : $", {r.OrgId}")}, from {r.From:yyyy-MM-dd}";

    public Task<IActionResult> OnPostRouteAsync(string? environment, string? path, string? orgId, string? costCenter, string? owner, string? policy, DateTime? from, DateTime? to, string? note) =>
        AdminChangeAsync(async () =>
        {
            var row = Check(environment, path, orgId, costCenter, owner, policy, from, to, note);
            var ended = await AddAsync([row]);
            return "Route added: " + Said(row) + (ended > 0 ? $"; its route before was ended on {row.From.AddDays(-1):yyyy-MM-dd}." : ".");
        });

    public Task<IActionResult> OnPostEndrouteAsync(int id, DateTime? end) => AdminChangeAsync(async () =>
    {
        if (end is null) throw new ArgumentException("give the last day the route is in effect.");
        var row = (await Db.QueryAsync<(string Env, string Path, DateTime From)>("SELECT EnvironmentName, Path, EffectiveFrom FROM dash.CreditRoute WHERE RouteId = @id;", new { id })).FirstOrDefault();
        if (row.Env is null) throw new ArgumentException("that route is gone.");
        if (end < row.From) throw new ArgumentException("the end comes on or after its start.");
        await Db.ExecuteAsync("UPDATE dash.CreditRoute SET EffectiveTo = @end, UpdatedBy = @by, UpdatedUtc = SYSUTCDATETIME() WHERE RouteId = @id;", new { id, end, by = Viewer.Upn ?? Viewer.WindowsName });
        return $"Route ended: {row.Env}, {CreditPaths.Label(row.Path)} from {row.From:yyyy-MM-dd}, now to {end:yyyy-MM-dd}.";
    });

    public Task<IActionResult> OnPostDelrouteAsync(int id) => AdminChangeAsync(async () =>
    {
        var row = (await Db.QueryAsync<(string Env, string Path, string? OrgId, DateTime From)>("SELECT EnvironmentName, Path, OrgId, EffectiveFrom FROM dash.CreditRoute WHERE RouteId = @id;", new { id })).FirstOrDefault();
        if (row.Env is null) return "That route was already gone.";
        await Db.ExecuteAsync("DELETE FROM dash.CreditRoute WHERE RouteId = @id;", new { id });
        return $"Route removed: {row.Env}, {CreditPaths.Label(row.Path)}{(row.OrgId is null ? "" : $", {row.OrgId}")}, from {row.From:yyyy-MM-dd}; month-end snapshots keep what they were.";
    });

    public Task<IActionResult> OnPostPasteroutesAsync(string? rows) => AdminChangeAsync(async () =>
    {
        var parsed = ParsePaste(rows);
        var ended = await AddAsync(parsed);
        return $"{parsed.Count:#,0} routes added" + (ended > 0 ? $"; {ended:#,0} open route{(ended == 1 ? "" : "s")} ended the day before." : ".");
    });

    /// <summary>A kept month cleared and taken again from today's register and rate: the routes put right after it was kept.</summary>
    public Task<IActionResult> OnPostRetakeAsync(string? month) => AdminChangeAsync(false, async () =>
    {
        if (!DateTime.TryParseExact(month, "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out var m)) throw new ArgumentException("choose the month to take again.");
        var before = await Db.QueryAsync<(decimal Credits, decimal Unrouted)>("SELECT ISNULL(SUM(Credits), 0), ISNULL(SUM(CASE WHEN Path = 'Unmapped' THEN Credits END), 0) FROM dash.CreditChargeSnapshot WHERE SnapshotMonth = @m;", new { m });
        await Db.ExecuteAsync("SET XACT_ABORT ON; BEGIN TRANSACTION; DELETE FROM dash.CreditChargeSnapshot WHERE SnapshotMonth = @m; EXEC dash.usp_TakeCreditChargeSnapshot @Month = @m; COMMIT TRANSACTION;", new { m });
        var after = await Db.QueryAsync<(int Rows, decimal Unrouted)>("SELECT COUNT(*), ISNULL(SUM(CASE WHEN Path = 'Unmapped' THEN Credits END), 0) FROM dash.CreditChargeSnapshot WHERE SnapshotMonth = @m;", new { m });
        var (b, a) = (before.Single(), after.Single());
        return a.Rows == 0
            ? $"The {m:MMMM yyyy} snapshot was cleared and could not be taken again: its credits do not reach the month's last day, or no route was in effect at its end."
            : $"The {m:MMMM yyyy} snapshot was taken again: {a.Rows:#,0} environment rows; not routed {b.Unrouted:#,0} credits before, {a.Unrouted:#,0} now.";
    });

    public Task<IActionResult> OnPostChargerateAsync(string? rate) => AdminChangeAsync(async () =>
    {
        string? value = null;
        if (!string.IsNullOrWhiteSpace(rate))
        {
            if (!decimal.TryParse(rate, NumberStyles.Number, CultureInfo.InvariantCulture, out var r) || r <= 0 || r > 100) throw new ArgumentException("the charge rate is a number above 0, such as 0.01.");
            if (decimal.Round(r, 6) != r) throw new ArgumentException("the charge rate has at most six decimals, as it is kept.");
            value = r.ToString(CultureInfo.InvariantCulture);
        }
        var was = await S.Settings.StringAsync("CreditChargeRate");
        await Db.ExecuteAsync("UPDATE dash.Setting SET Value = @value WHERE Name = N'CreditChargeRate';", new { value });
        S.Settings.Invalidate();
        return (value is null ? "Charge rate cleared: the price list's pay-as-you-go credit is used." : $"Charge rate: {value} a credit.") + (string.IsNullOrWhiteSpace(was) ? "" : $" (was {was})");
    });

    /// <summary>The pasted lines as routes: quoted fields may hold commas; a first line that is not a route is the header. Pure, for the tests.</summary>
    public static List<RouteInput> ParsePaste(string? text)
    {
        var lines = (text ?? "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        if (lines.Count == 0) throw new ArgumentException("paste one environment a line.");
        if (lines.Count > 500) throw new ArgumentException("at most 500 lines at once.");
        static bool IsDate(string s) => DateTime.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
        var parsed = new List<RouteInput>();
        foreach (var (line, i) in lines.Select((l, i) => (l, i + 1)))
        {
            var f = Fields(line);
            if (i == 1 && f.Count > 4 && ParsePath(f[1]) is null && !IsDate(f[4])) continue;   // the header
            if (f.Count < 5) throw new ArgumentException($"line {i} has {f.Count} fields; it needs environment, path, Org ID, cost center and from.");
            if (!DateTime.TryParseExact(f[4], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) throw new ArgumentException($"line {i}: from is a date, yyyy-mm-dd.");
            DateTime? to = f.Count > 7 && f[7].Length > 0 ? DateTime.TryParseExact(f[7], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var e) ? e : throw new ArgumentException($"line {i}: to is a date, yyyy-mm-dd.") : null;
            try { parsed.Add(Check(f[0], f[1], f[2], f[3], f.Count > 5 ? f[5] : null, f.Count > 6 ? f[6] : null, d, to, null)); }
            catch (ArgumentException ex) { throw new ArgumentException($"line {i}: {ex.Message}"); }
        }
        if (parsed.Count == 0) throw new ArgumentException("no rows below the header.");
        return parsed;
    }

    static List<string> Fields(string line)
    {
        var fields = new List<string>(); var cur = new System.Text.StringBuilder(); var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (quoted) { if (ch == '"' && i + 1 < line.Length && line[i + 1] == '"') { cur.Append('"'); i++; } else if (ch == '"') quoted = false; else cur.Append(ch); }
            else if (ch == '"') quoted = true;
            else if (ch == ',') { fields.Add(cur.ToString().Trim()); cur.Clear(); }
            else cur.Append(ch);
        }
        fields.Add(cur.ToString().Trim());
        return fields;
    }
}
