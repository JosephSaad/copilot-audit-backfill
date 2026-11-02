using Governance.Application.Queries;
using Microsoft.AspNetCore.Mvc;

namespace Governance.Web.Pages.Reports;

public class KpisModel(ReportQueries reports) : GovPage
{
    public IReadOnlyList<KpiRow> Kpis { get; private set; } = Array.Empty<KpiRow>();
    public DateTime AsOf { get; private set; }

    /// <summary>How each figure is worked out, its source and its target phase (strategy slide 5, Appendix C).</summary>
    public static readonly IReadOnlyDictionary<string, (string Formula, string Source)> Definitions = new Dictionary<string, (string, string)>
    {
        ["OWNERSHIP_COVERAGE"] = ("Workloads in scope with an active funding owner and a cost center, divided by all workloads in scope (not discovered, not retired).", "Workbench register"),
        ["APPROVED_ROUTE"] = ("Workloads bound to a Microsoft object and covered by an unexpired approval or an active exception, divided by all workloads in scope.", "Workbench register and decision ledger"),
        ["THRESHOLD_RESPONSE"] = ("Consumption events in the last 90 days acknowledged by their target time, divided by all events in that period.", "Operations events"),
        ["SHARED_EXHAUSTION_RISK"] = ("Exhaustion or no-permitted-source events on a critical shared service in the last 90 days, out of all events.", "Operations events"),
        ["RECERT_COMPLETION"] = ("Recertification items done, divided by items in campaigns due up to 30 days from now.", "Recertification campaigns"),
        ["OUTCOME_EVIDENCE"] = ("Recertified items with outcome evidence, divided by recertified items.", "Recertification campaigns"),
        ["UNAPPROVED_FINDINGS"] = ("Open findings for unregistered objects, unmatched registry agents and assignments without funding.", "Discovery, drift and entitlement findings"),
    };

    public async Task OnGetAsync() => (Kpis, AsOf) = await reports.KpisAsync();
    public async Task<IActionResult> OnGetCsvAsync() => Csv((await reports.KpisAsync()).Kpis, "governance-kpis");
}

public class AuditModel(ReportQueries reports) : GovPage
{
    [BindProperty(SupportsGet = true)] public DateTime? From { get; set; }
    [BindProperty(SupportsGet = true)] public DateTime? To { get; set; }
    [BindProperty(SupportsGet = true)] public string? Action { get; set; }
    [BindProperty(SupportsGet = true)] public string View { get; set; } = "audit";
    public Paged<AuditRow> Audit { get; private set; } = new();
    public Paged<DecisionReportRow> Decisions { get; private set; } = new();

    private DateTime? ToUtc => To?.Date.AddDays(1);

    public async Task OnGetAsync()
    {
        if (View == "decisions") Decisions = await reports.DecisionsAsync(Query, From, ToUtc);
        else Audit = await reports.AuditAsync(Query, From, ToUtc, string.IsNullOrWhiteSpace(Action) ? null : Action);
        await AuditViewAsync("audit.view", View == "decisions" ? "Decision" : "AuditLog", null);
    }

    public async Task<IActionResult> OnGetCsvAsync()
    {
        if (!Principal.Can(Permissions.AuditExport) && !Principal.IsCentral) return Forbid();
        await AuditViewAsync("audit.export", View == "decisions" ? "Decision" : "AuditLog", $"{From:yyyy-MM-dd}..{To:yyyy-MM-dd}");
        return View == "decisions"
            ? Csv((await reports.DecisionsAsync(Query.All(), From, ToUtc)).Items, "decisions")
            : Csv((await reports.AuditAsync(Query.All(), From, ToUtc, string.IsNullOrWhiteSpace(Action) ? null : Action)).Items, "audit-log");
    }
}
