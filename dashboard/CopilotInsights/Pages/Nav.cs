using CopilotInsights.Infrastructure;

namespace CopilotInsights.Pages;

public sealed record NavPage(string Title, string Path, string? Role = null, bool Admin = false, bool ManyTenants = false, bool InPrivacy = false);
/// <summary>A persona section. With a Role, the whole section (aggregates too) is for holders of that role only.</summary>
public sealed record NavGroup(string Persona, string Who, IReadOnlyList<NavPage> Pages, string? Role = null);

/// <summary>The personas and their pages. A page with a Role lists named people and needs that role.</summary>
public static class Nav
{
    public static readonly IReadOnlyList<NavGroup> Groups =
    [
        new("Leadership", "Executives and department heads", [
            new("Overview", "/leadership"), new("What to act on", "/leadership/act"), new("Scorecard", "/leadership/scorecard"), new("Organization", "/leadership/org"), new("Compare two teams", "/leadership/compare"), new("Group overview", "/group", ManyTenants: true),
            new("Apps and agents", "/leadership/mix"), new("Use cases", "/leadership/usecases"), new("Value and time saved", "/leadership/value"), new("Costs and savings", "/leadership/costs"),
            new("Board pack", "/leadership/boardpack")]),
        new("Adoption", "Copilot program leads", [
            new("Funnel and trend", "/adoption"), new("Usage by license", "/adoption/by-license", Roles.Adoption), new("Departments", "/adoption/departments"), new("Leaderboard", "/adoption/leaderboard"),
            new("Retention and habit", "/adoption/retention"), new("Retention by license month", "/adoption/cohorts"), new("New joiners", "/adoption/onboarding"), new("Not started", "/adoption/not-started", Roles.Adoption),
            new("Targets", "/adoption/targets"), new("Drops and spikes", "/adoption/anomalies")], Roles.Adoption),
        new("Licensing", "Licensing, IT asset management and FinOps", [
            new("License inventory", "/licensing"), new("License pool", "/licensing/pool"), new("License changes", "/licensing/changes"),
            new("Chargeback", "/licensing/chargeback"), new("Demand for licenses", "/licensing/demand"),
            new("Unused licenses", "/licensing/unused", Roles.Licensing), new("Use ranking", "/licensing/ranking", Roles.Licensing),
            new("Better on pay-as-you-go", "/licensing/payg", Roles.Licensing), new("Swap list", "/licensing/swap", Roles.Licensing),
            new("Better with a license", "/licensing/license-for-credits", Roles.Licensing), new("Fading use", "/licensing/fading", Roles.Licensing),
            new("Overlapping licenses", "/licensing/overlap", Roles.Licensing),
            new("Licenses on disabled accounts", "/licensing/leavers", Roles.Licensing), new("Copilot on non-human accounts", "/licensing/non-person", Roles.Licensing),
            new("Departures ahead", "/licensing/departures", Roles.Licensing)], Roles.Licensing),
        new("Security", "Security operations", [
            new("Security signals", "/security"), new("Guests and external users", "/security/guests"),
            new("Flagged interactions", "/security/flags", Roles.Security), new("Copilot on non-human accounts", "/security/non-human", Roles.Security)], Roles.Security),
        new("Compliance", "Data protection and compliance", [
            new("Sensitive content", "/compliance"), new("Labeled content by unit", "/compliance/units"), new("Oversharing hotspots", "/compliance/sites"),
            new("Access to labeled content", "/compliance/access", Roles.Compliance), new("Person record", "/compliance/person", Roles.Compliance, InPrivacy: true),
            new("Copilot on non-human accounts", "/compliance/non-human", Roles.Compliance)], Roles.Compliance),
        new("Agents", "Copilot Studio makers and the center of excellence", [
            new("Agents in use", "/agents"), new("Agent inventory", "/agents/inventory"), new("Shared agents", "/agents/shared"), new("Agents in production", "/agents/production"), new("Agents without an owner", "/agents/owners"), new("Agent checks", "/agents/checks"),
            new("Build and publish", "/agents/build"), new("Agent detail", "/agents/detail"), new("Agent hand-offs", "/agents/handoffs"), new("Child agent usage", "/agents/children"),
            new("Agents costing credits", "/agents/credits")], Roles.Agents),
        new("Enablement", "Training leads and champions", [
            new("Engagement", "/enablement"), new("Conversation depth", "/enablement/depth"), new("Training effect", "/enablement/training"),
            new("Champions", "/enablement/champions", Roles.Enablement), new("People by engagement", "/enablement/people", Roles.Enablement)], Roles.Enablement),
        new("Forecasting", "Finance, licensing and program leads", [
            new("Forecast", "/forecasting"), new("License optimization", "/forecasting/optimize"), new("Budget and renewal", "/forecasting/budget"),
            new("Upgrade, train or revoke", "/forecasting/actions", Roles.Licensing), new("Rollout waves", "/forecasting/waves", Roles.Licensing),
            new("License action outcomes", "/forecasting/outcomes", Roles.Licensing)], Roles.Licensing),
        new("Regions", "Country, office and site leads", [
            new("Countries and offices", "/regions"), new("Working patterns", "/regions/patterns")]),
        new("People", "HR and people analytics", [
            new("Workforce", "/workforce"), new("Managers and their teams", "/workforce/managers"), new("Job families", "/workforce/families")]),
        new("App owners", "Microsoft 365 workload owners", [
            new("Apps", "/apps"), new("App detail", "/apps/detail")]),
        new("Governance", "AI governance and architecture", [
            new("Models and controls", "/governance")]),
        new("Admin", "Dashboard administrators", [
            new("Health", "/admin/health", Admin: true), new("Access", "/admin/access", Admin: true), new("Directory quality", "/admin/directory", Admin: true),
            new("Email", "/admin/email", Admin: true), new("Alerts and digests", "/admin/notifications", Admin: true),
            new("Targets and assumptions", "/admin/assumptions", Admin: true), new("Privacy and retention", "/admin/privacy", Admin: true),
            new("Reference data", "/admin/reference", Admin: true), new("Value names", "/admin/values", Admin: true),
            new("Classifications and events", "/admin/classifications", Admin: true), new("Reports shown", "/admin/reports", Admin: true), new("Scheduled extracts", "/admin/extracts", Admin: true),
            new("Training records", "/admin/training", Admin: true), new("Audit log", "/admin/audit", Admin: true)]),
    ];

    /// <summary>
    /// Whether a page is open to the viewer: admin pages to administrators (to one limited to some pages, those: 2.6.4), role pages to holders
    /// of the role, and report pages to anyone with data to see (in the directory, or granted
    /// Organization). The navigation and the start page show only these; the rest are not listed.
    /// Outside the dashboard's levels (2.4.4), only an administrator's Admin pages are.
    /// </summary>
    public static bool Allowed(NavPage page, Viewer viewer, bool privacyMode = false) =>
        !viewer.Admitted ? page.Admin && viewer.CanAdmin(page.Path)
        : page.ManyTenants ? (viewer.OrgWide || viewer.InDirectory)
        : page.Admin ? viewer.CanAdmin(page.Path)
                   : (viewer.OrgWide || viewer.InDirectory) && (page.Role is null || ((!privacyMode || page.InPrivacy) && viewer.Has(page.Role)));

    /// <summary>The groups and pages this viewer may open; a group with none is left out.</summary>
    public static IReadOnlyList<NavGroup> For(Viewer? viewer, bool privacyMode = false, bool manyTenants = false, IReadOnlySet<string>? hidden = null) =>
        viewer is null ? [] :
        Groups.Where(g => g.Role is null || viewer.Has(g.Role))
              .Select(g => g with { Pages = g.Pages.Where(p => (manyTenants || !p.ManyTenants) && Allowed(p, viewer, privacyMode) && !HiddenReports.Is(hidden, p.Path)).ToList() })
              .Where(g => g.Pages.Count > 0).ToList();

    /// <summary>The role a page's section needs, if any (2.4.2): the section is hidden, and its pages refused, without it.</summary>
    /// <summary>
    /// The page before <paramref name="path"/> in its section and the one after (2.4.10), among the pages offered:
    /// <paramref name="groups"/> is what <see cref="For"/> returned for this person, so neither is one they cannot open.
    /// </summary>
    public static (NavPage? Before, NavPage? After, NavGroup? Section) Beside(IReadOnlyList<NavGroup> groups, string? path)
    {
        foreach (var g in groups)
            for (var i = 0; i < g.Pages.Count; i++)
                if (string.Equals(g.Pages[i].Path, path, StringComparison.OrdinalIgnoreCase))
                    return (i > 0 ? g.Pages[i - 1] : null, i < g.Pages.Count - 1 ? g.Pages[i + 1] : null, g);
        return (null, null, null);
    }

    /// <summary>
    /// The pages nearest to an address that has no page (2.4.10), among the pages offered: those in the section the
    /// address begins with, and those whose name, address or section holds a word of it. Best first, the list's order
    /// among equals.
    /// </summary>
    public static IReadOnlyList<NavPage> Near(IReadOnlyList<NavGroup> groups, string? path, int top = 6)
    {
        var words = (path ?? "").Split(['/', '-', '_', '.', ' ', '+'], StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length >= 3).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (words.Count == 0) return [];
        var first = "/" + (path ?? "").Trim('/').Split('/')[0];
        return [.. groups.SelectMany(g => g.Pages.Select(p => (Page: p, Score:
                (p.Path.Equals(first, StringComparison.OrdinalIgnoreCase) || p.Path.StartsWith(first + "/", StringComparison.OrdinalIgnoreCase) ? 2 : 0) +
                words.Count(w => p.Path.Contains(w, StringComparison.OrdinalIgnoreCase) || p.Title.Contains(w, StringComparison.OrdinalIgnoreCase)
                    || g.Persona.Contains(w, StringComparison.OrdinalIgnoreCase)))))
            .Where(x => x.Score > 0).OrderByDescending(x => x.Score).Select(x => x.Page).DistinctBy(p => p.Path).Take(top)];
    }

    public static NavGroup? SectionOf(string? path) =>
        path is null ? null : Groups.FirstOrDefault(g => g.Pages.Any(p => string.Equals(p.Path, path.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)));
}

/// <summary>
/// The reports an administrator has turned off (2.4.9, Admin > Reports shown): gone from the navigation and the start
/// page, and not found at their address, for everyone, administrators too. Kept in dash.Setting, one row a report,
/// named Hide: and its path. Admin pages cannot be turned off.
/// </summary>
public static class HiddenReports
{
    public const string Prefix = "Hide:";

    /// <summary>Every report that can be turned off, by section: all but the Admin pages.</summary>
    public static IEnumerable<NavGroup> Choices =>
        Nav.Groups.Select(g => g with { Pages = g.Pages.Where(p => !p.Admin).ToList() }).Where(g => g.Pages.Count > 0);

    public static async Task<IReadOnlySet<string>> GetAsync(Settings settings) =>
        (await settings.AllAsync()).Where(kv => kv.Key.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) && kv.Value == "1")
            .Select(kv => kv.Key[Prefix.Length..]).ToHashSet(StringComparer.OrdinalIgnoreCase);

    public static bool Is(IReadOnlySet<string>? hidden, string? path) =>
        hidden is { Count: > 0 } && path is not null && hidden.Contains(path.Length > 1 ? path.TrimEnd('/') : path);
}
