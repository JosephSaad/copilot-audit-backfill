using Governance.Application.Queries;
using Governance.Web.Infrastructure;
using Microsoft.AspNetCore.Mvc;

namespace Governance.Web.Pages.Reports;

/// <summary>The hierarchy audit's figures, as one row each with how it is counted.</summary>
public sealed record AuditMetric(string Name, string Value, string Definition, string? Href = null, bool Strong = false);

/// <summary>One tracking group's results.</summary>
public sealed record AuditGroupLine(string GroupName, Guid GroupPublicId, string? LobName, int Total, int Pass, int NewHire, int Review)
{
    public int Validated => Total - NewHire;
    public double ReviewShare => Total == 0 ? 0 : (double)Review / Total;
}

/// <summary>Who may open the seat reports and the mapping: auditors, central roles, Copilot Insights administrators and the Platform Owner (Policies.SeatReports).</summary>
public static class SeatAuditAccess
{
    public static bool CanOpen(WebPrincipal p) => SeatReportHandler.Allows(p);
}

/// <summary>Executive summary: population and outcome metrics, with exception hotspots and the results by tracking group.</summary>
public class SeatAuditModel(SeatAudit audit) : GovPage
{
    public SeatAudit.Outcome Outcome { get; private set; } = new(Array.Empty<SeatAuditRow>(), DateTime.UtcNow, 90, true, Array.Empty<SeatAuditRules.Mapping>());
    public IReadOnlyList<AuditMetric> Metrics { get; private set; } = Array.Empty<AuditMetric>();
    public IReadOnlyList<AuditGroupLine> Groups { get; private set; } = Array.Empty<AuditGroupLine>();
    public AuditGroupLine? MostReviews { get; private set; }
    public AuditGroupLine? HighestShare { get; private set; }

    /// <summary>The metrics from the audited rows; pure, for the tests.</summary>
    public static IReadOnlyList<AuditMetric> Summarize(IReadOnlyList<SeatAuditRow> rows, int newHireDays)
    {
        static string Pct(double v) => v.ToString("0.0%", System.Globalization.CultureInfo.InvariantCulture);
        const string Detail = "/workbench/Reports/SeatAudit/Detail";
        var total = rows.Count;
        var pass = rows.Count(r => r.Status == SeatAuditRules.Pass);
        var hires = rows.Count(r => r.Status == SeatAuditRules.NewHire);
        var review = rows.Count(r => SeatAuditRules.IsReview(r.Status));
        var validated = total - hires;
        var list = new List<AuditMetric>
        {
            new("Total seat holders audited", total.ToString("N0"), "Copilot license holders in an active tracking group, once for each tracking group they are in.", Detail, true),
            new("PASS", pass.ToString("N0"), SeatAuditRules.Definition(SeatAuditRules.Pass), $"{Detail}?status=pass"),
            new(SeatAuditRules.Label(SeatAuditRules.NewHire), hires.ToString("N0"), $"Hired in the last {newHireDays} days: hierarchy validation deferred; no investigation now.", $"{Detail}?status=newhire"),
            new("Total REVIEW requiring investigation", review.ToString("N0"), "All investigation statuses.", $"{Detail}?status=review", true),
        };
        list.AddRange(SeatAuditRules.Reviews.Select(s => new AuditMetric(SeatAuditRules.Label(s), rows.Count(r => r.Status == s).ToString("N0"), SeatAuditRules.Definition(s), $"{Detail}?status={s}")));
        list.Add(new("Possible duplicate holders", rows.GroupBy(r => r.MemberObjectId).Where(g => g.Count() > 1).Sum(g => g.Count()).ToString("N0"), "Rows of people in more than one tracking group: each group counts them. See Multiple groups.", "/workbench/Reports/SeatMultipleGroups"));
        list.Add(new("Validated population", validated.ToString("N0"), "Total seat holders audited less new hires.", null, true));
        list.Add(new("Validated pass rate", validated == 0 ? "–" : Pct((double)pass / validated), "PASS / validated population.", null, true));
        list.Add(new("New hire %", total == 0 ? "–" : Pct((double)hires / total), "New hires / total seat holders audited.", null, true));
        list.Add(new("Reconciliation difference", (total - pass - hires - review).ToString("N0"), "Must equal zero: total = PASS + NEW HIRE + REVIEW.", null, true));
        return list;
    }

    public static IReadOnlyList<AuditGroupLine> ByGroup(IReadOnlyList<SeatAuditRow> rows) => rows
        .GroupBy(r => (r.GroupPublicId, r.GroupName, r.LobName))
        .Select(g => new AuditGroupLine(g.Key.GroupName, g.Key.GroupPublicId, g.Key.LobName, g.Count(), g.Count(r => r.Status == SeatAuditRules.Pass),
            g.Count(r => r.Status == SeatAuditRules.NewHire), g.Count(r => SeatAuditRules.IsReview(r.Status))))
        .OrderByDescending(g => g.Review).ThenBy(g => g.GroupName, StringComparer.OrdinalIgnoreCase).ToList();

    public async Task<IActionResult> OnGetAsync()
    {
        if (!SeatAuditAccess.CanOpen(Principal)) return Forbid();
        Outcome = await audit.GetAsync();
        Metrics = Summarize(Outcome.Rows, Outcome.NewHireDays);
        Groups = ByGroup(Outcome.Rows);
        MostReviews = Groups.Where(g => g.Review > 0).OrderByDescending(g => g.Review).FirstOrDefault();
        HighestShare = Groups.Where(g => g.Review > 0).OrderByDescending(g => g.ReviewShare).ThenByDescending(g => g.Review).FirstOrDefault();
        return Page();
    }

    public async Task<IActionResult> OnGetCsvAsync()
    {
        if (!SeatAuditAccess.CanOpen(Principal)) return Forbid();
        var o = await audit.GetAsync();
        return Csv(Summarize(o.Rows, o.NewHireDays).Select(m => new { Metric = m.Name, m.Value, Definition = m.Definition }), "hierarchy-audit-summary");
    }
}

/// <summary>
/// Detailed audit, Exception report (status=review) and New hire report (status=newhire): one row per seat holder and
/// tracking group, 50 a page, searchable by name or sign-in, filtered by tracking group, with the whole list as CSV.
/// </summary>
public class SeatAuditDetailModel(SeatAudit audit) : GovPage
{
    public const int Size = 50;
    [BindProperty(SupportsGet = true, Name = "status")] public string? Status { get; set; }
    [BindProperty(SupportsGet = true, Name = "group")] public Guid? Group { get; set; }
    public IReadOnlyList<SeatAuditRow> Rows { get; private set; } = Array.Empty<SeatAuditRow>();
    public int Total { get; private set; }
    public int Pages => Math.Max(1, (Total + Size - 1) / Size);
    public int PageShown { get; private set; } = 1;
    public SeatAudit.Outcome? Outcome { get; private set; }
    public IReadOnlyList<(Guid Id, string Name)> GroupChoices { get; private set; } = Array.Empty<(Guid, string)>();

    public string Title => Status switch
    {
        "review" => "Exception report",
        "newhire" => "New hire report",
        "pass" => "Passed the hierarchy check",
        { } s when s.StartsWith("REVIEW_", StringComparison.Ordinal) => SeatAuditRules.Label(s),
        _ => "Detailed audit",
    };

    public string Lead => Status switch
    {
        "review" => "REVIEW records only: investigation required. PASS and NEW HIRE records are left out. A potential group is a recommendation to investigate, not a change to anyone's group or license.",
        "newhire" => "Seat holders hired recently: their hierarchy check is deferred, as their manager may not be set yet.",
        _ => "Every seat holder in a tracking group with the result of the hierarchy check: the leader expected at the group's level, who is actually at that level in their manager chain, and the status.",
    };

    /// <summary>The rows a filter and search keep; pure, for the tests.</summary>
    public static IEnumerable<SeatAuditRow> Filter(IEnumerable<SeatAuditRow> rows, string? status, Guid? group, string? search)
    {
        var r = status switch
        {
            "review" => rows.Where(x => SeatAuditRules.IsReview(x.Status)),
            "newhire" => rows.Where(x => x.Status == SeatAuditRules.NewHire),
            "pass" => rows.Where(x => x.Status == SeatAuditRules.Pass),
            { } s when s.StartsWith("REVIEW_", StringComparison.Ordinal) => rows.Where(x => x.Status == s),
            _ => rows,
        };
        if (group is Guid g) r = r.Where(x => x.GroupPublicId == g);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var q = search.Trim();
            r = r.Where(x => x.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || (x.Upn?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false));
        }
        return r;
    }

    private async Task<List<SeatAuditRow>> LoadAsync()
    {
        Outcome = await audit.GetAsync();
        GroupChoices = Outcome.Rows.Select(r => (r.GroupPublicId, r.GroupName)).Distinct().OrderBy(g => g.GroupName, StringComparer.OrdinalIgnoreCase).ToList();
        return Filter(Outcome.Rows, Status, Group, Search).ToList();
    }

    public async Task<IActionResult> OnGetAsync()
    {
        if (!SeatAuditAccess.CanOpen(Principal)) return Forbid();
        var all = await LoadAsync();
        Total = all.Count;
        PageShown = Math.Clamp(PageNo, 1, Pages);
        Rows = all.Skip((PageShown - 1) * Size).Take(Size).ToList();
        return Page();
    }

    public async Task<IActionResult> OnGetCsvAsync()
    {
        if (!SeatAuditAccess.CanOpen(Principal)) return Forbid();
        var all = await LoadAsync();
        var name = Status switch { "review" => "exception-report", "newhire" => "new-hire-report", _ => "detailed-audit" };
        return Csv(all.Select(r => new
        {
            EmployeeName = r.Name, SignIn = r.Upn, CurrentTrackingGroup = r.GroupName, LineOfBusiness = r.LobName, ExpectedLeader = r.ExpectedLeaderName,
            ExpectedLevel = r.ExpectedLevel is int l ? $"Level {l}" : null, ActualNameAtExpectedLevel = r.ActualAtLevelName, PrimaryAuditStatus = SeatAuditRules.Label(r.Status),
            PotentialCorrectTrackingGroup = r.PotentialGroup, PotentialCorrectLeader = r.PotentialLeaderName, HireDate = r.HireDate?.ToString("yyyy-MM-dd"), ManagerChain = r.Chain,
        }), name);
    }
}

/// <summary>People who may be licensed or counted through more than one AD group.</summary>
public class SeatMultipleGroupsModel(SeatQueries seats, CopilotInsights.Infrastructure.Db insights) : GovPage
{
    public const int Size = 50;
    [BindProperty(SupportsGet = true, Name = "show")] public string? Show { get; set; }
    public IReadOnlyList<SeatMultiGroupRow> Rows { get; private set; } = Array.Empty<SeatMultiGroupRow>();
    public int Total { get; private set; }
    public int AllCount { get; private set; }
    public int TrackingCount { get; private set; }
    public int LicenseCount { get; private set; }
    public int Pages => Math.Max(1, (Total + Size - 1) / Size);
    public int PageShown { get; private set; } = 1;
    public IReadOnlyDictionary<string, (string Name, string Upn)> Names { get; private set; } = new Dictionary<string, (string, string)>();

    private SeatHolderRow AsHolder(SeatMultiGroupRow r) => new() { MemberObjectId = r.MemberObjectId, DisplayName = r.DisplayName, UserPrincipalName = r.UserPrincipalName };
    public string NameOf(SeatMultiGroupRow r) => Registry.SeatNames.NameOf(AsHolder(r), Names);
    public string UpnOf(SeatMultiGroupRow r) => Registry.SeatNames.UpnOf(AsHolder(r), Names);

    private async Task<List<SeatMultiGroupRow>> LoadAsync()
    {
        var all = await seats.MultipleGroupsAsync();
        AllCount = all.Count;
        TrackingCount = all.Count(r => r.TrackingGroupCount > 1);
        LicenseCount = all.Count(r => r.LicenseSourceCount > 1);
        return (Show switch
        {
            "tracking" => all.Where(r => r.TrackingGroupCount > 1),
            "license" => all.Where(r => r.LicenseSourceCount > 1),
            _ => all,
        }).OrderBy(r => r.UserPrincipalName ?? "￿", StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task<IActionResult> OnGetAsync()
    {
        if (!SeatAuditAccess.CanOpen(Principal)) return Forbid();
        var all = await LoadAsync();
        Total = all.Count;
        PageShown = Math.Clamp(PageNo, 1, Pages);
        Rows = all.Skip((PageShown - 1) * Size).Take(Size).ToList();
        Names = await Registry.SeatNames.ForAsync(insights, Log, Rows.Select(AsHolder));
        return Page();
    }

    public async Task<IActionResult> OnGetCsvAsync()
    {
        if (!SeatAuditAccess.CanOpen(Principal)) return Forbid();
        var all = await LoadAsync();
        Names = await Registry.SeatNames.ForAsync(insights, Log, all.Select(AsHolder));
        return Csv(all.Select(r => new
        {
            Name = NameOf(r), SignIn = UpnOf(r), HoldsSeat = r.HasSeat ? "yes" : "no", r.TrackingGroupCount, r.TrackingGroups, LicensedThrough = r.LicenseSourceCount, r.LicenseSources,
        }), "multiple-groups");
    }
}
