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
        new("My Copilot", "Everyone: your own use", [
            new("My use", "/me"), new("Email", "/me/email"), new("Saved views", "/me/views")]),
        new("Leadership", "Executives and department heads", [
            new("Overview", "/leadership"), new("Scorecard", "/leadership/scorecard"), new("Organization", "/leadership/org"), new("Apps and agents", "/leadership/mix"),
            new("Value and time saved", "/leadership/value"), new("Use cases", "/leadership/usecases"), new("Board pack", "/leadership/boardpack"),
            new("Group overview", "/group", ManyTenants: true)]),
        new("Adoption", "Copilot program lead", [
            new("Funnel and trend", "/adoption"), new("Retention and habit", "/adoption/retention"), new("License cohorts", "/adoption/cohorts"),
            new("Departments", "/adoption/departments"), new("Leaderboard", "/adoption/leaderboard"), new("Targets", "/adoption/targets"), new("New joiners", "/adoption/onboarding"),
            new("Drops and spikes", "/adoption/anomalies"), new("Not started", "/adoption/not-started", Roles.Adoption)], Roles.Adoption),
        new("Licensing", "Licensing, IT asset and FinOps", [
            new("Inventory", "/licensing"), new("Unused licenses", "/licensing/unused", Roles.Licensing), new("Use ranking", "/licensing/ranking", Roles.Licensing),
            new("Better on pay-as-you-go", "/licensing/payg", Roles.Licensing), new("Swap list", "/licensing/swap", Roles.Licensing),
            new("Better with a license", "/licensing/license-for-credits", Roles.Licensing),
            new("Demand", "/licensing/demand"), new("License changes", "/licensing/changes"), new("License pool", "/licensing/pool"),
            new("Chargeback", "/licensing/chargeback"), new("Licenses on disabled accounts", "/licensing/leavers", Roles.Licensing)], Roles.Licensing),
        new("Security", "Security operations", [
            new("Signals", "/security"), new("Signals by unit", "/security/units"), new("Guests and external users", "/security/guests"),
            new("Flagged interactions", "/security/flags", Roles.Security)], Roles.Security),
        new("Compliance", "Data protection and compliance", [
            new("Sensitive content", "/compliance"), new("Labeled content by unit", "/compliance/units"), new("Oversharing hotspots", "/compliance/sites"),
            new("Resource access", "/compliance/access", Roles.Compliance), new("Person record", "/compliance/person", Roles.Compliance, InPrivacy: true)], Roles.Compliance),
        new("Agents", "Copilot Studio makers and CoE", [
            new("Agents", "/agents"), new("Inventory", "/agents/inventory"), new("Build and publish", "/agents/build"), new("Agent detail", "/agents/detail"), new("Agent hand-offs", "/agents/handoffs")], Roles.Agents),
        new("Enablement", "Training and champions", [
            new("Engagement", "/enablement"), new("Conversation depth", "/enablement/depth"), new("Training effect", "/enablement/training"),
            new("Champions", "/enablement/champions", Roles.Enablement), new("People", "/enablement/people", Roles.Enablement)], Roles.Enablement),
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
            new("Models and grounding", "/governance"), new("Controls", "/governance/controls")]),
        new("Admin", "Dashboard administrators", [
            new("Health", "/admin", Admin: true), new("Access", "/admin/access", Admin: true), new("Directory quality", "/admin/directory", Admin: true),
            new("Email", "/admin/email", Admin: true), new("Alerts and digests", "/admin/notifications", Admin: true),
            new("Targets and assumptions", "/admin/assumptions", Admin: true), new("Privacy and retention", "/admin/privacy", Admin: true),
            new("Reference data", "/admin/reference", Admin: true), new("Value names", "/admin/values", Admin: true),
            new("Classifications and events", "/admin/classifications", Admin: true), new("Scheduled extracts", "/admin/extracts", Admin: true),
            new("Training records", "/admin/training", Admin: true), new("Audit log", "/admin/audit", Admin: true)]),
    ];

    /// <summary>
    /// Whether a page is open to the viewer: admin pages to administrators, role pages to holders
    /// of the role, and report pages to anyone with data to see (in the directory, or granted
    /// Organization). The navigation and the start page show only these; the rest are not listed.
    /// </summary>
    public static bool Allowed(NavPage page, Viewer viewer, bool privacyMode = false) =>
        page.ManyTenants ? (viewer.OrgWide || viewer.InDirectory)
        : page.Admin ? viewer.IsAdmin
                   : (viewer.OrgWide || viewer.InDirectory) && (page.Role is null || ((!privacyMode || page.InPrivacy) && viewer.Has(page.Role)));

    /// <summary>The groups and pages this viewer may open; a group with none is left out.</summary>
    public static IReadOnlyList<NavGroup> For(Viewer? viewer, bool privacyMode = false, bool manyTenants = false) =>
        viewer is null ? [] :
        Groups.Where(g => g.Role is null || viewer.Has(g.Role))
              .Select(g => g with { Pages = g.Pages.Where(p => (manyTenants || !p.ManyTenants) && Allowed(p, viewer, privacyMode)).ToList() })
              .Where(g => g.Pages.Count > 0).ToList();

    /// <summary>The role a page's section needs, if any (2.4.2): the section is hidden, and its pages refused, without it.</summary>
    public static NavGroup? SectionOf(string? path) =>
        path is null ? null : Groups.FirstOrDefault(g => g.Pages.Any(p => string.Equals(p.Path, path.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)));
}
