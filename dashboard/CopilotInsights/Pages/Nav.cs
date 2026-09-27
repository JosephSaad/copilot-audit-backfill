using CopilotInsights.Infrastructure;

namespace CopilotInsights.Pages;

public sealed record NavPage(string Title, string Path, string? Role = null, bool Admin = false, bool ManyTenants = false, bool InPrivacy = false);
public sealed record NavGroup(string Persona, string Who, IReadOnlyList<NavPage> Pages);

/// <summary>The personas and their pages. A page with a Role lists named people and needs that role.</summary>
public static class Nav
{
    public static readonly IReadOnlyList<NavGroup> Groups =
    [
        new("My Copilot", "Everyone: your own use", [
            new("My use", "/me"), new("Email", "/me/email"), new("Saved views", "/me/views")]),
        new("Leadership", "Executives and department heads", [
            new("Overview", "/leadership"), new("Scorecard", "/leadership/scorecard"), new("Organisation", "/leadership/org"), new("Apps and agents", "/leadership/mix"),
            new("Value and time saved", "/leadership/value"), new("Use cases", "/leadership/usecases"), new("Board pack", "/leadership/boardpack"),
            new("Group overview", "/group", ManyTenants: true)]),
        new("Adoption", "Copilot programme lead", [
            new("Funnel and trend", "/adoption"), new("Retention and habit", "/adoption/retention"), new("Licence cohorts", "/adoption/cohorts"),
            new("Departments", "/adoption/departments"), new("Leaderboard", "/adoption/leaderboard"), new("Targets", "/adoption/targets"), new("New joiners", "/adoption/onboarding"),
            new("Drops and spikes", "/adoption/anomalies"), new("Not started", "/adoption/not-started", Roles.Adoption)]),
        new("Licensing", "Licensing, IT asset and FinOps", [
            new("Inventory", "/licensing"), new("Unused licences", "/licensing/unused", Roles.Licensing), new("Use ranking", "/licensing/ranking", Roles.Licensing),
            new("Demand", "/licensing/demand"), new("Licence changes", "/licensing/changes"), new("Licence pool", "/licensing/pool"),
            new("Chargeback", "/licensing/chargeback"), new("Licences on disabled accounts", "/licensing/leavers", Roles.Licensing)]),
        new("Security", "Security operations", [
            new("Signals", "/security"), new("Signals by unit", "/security/units"), new("Guests and external users", "/security/guests"),
            new("Flagged interactions", "/security/flags", Roles.Security)]),
        new("Compliance", "Data protection and compliance", [
            new("Sensitive content", "/compliance"), new("Labelled content by unit", "/compliance/units"), new("Oversharing hotspots", "/compliance/sites"),
            new("Resource access", "/compliance/access", Roles.Compliance), new("Person record", "/compliance/person", Roles.Compliance, InPrivacy: true)]),
        new("Agents", "Copilot Studio makers and CoE", [
            new("Agents", "/agents"), new("Inventory", "/agents/inventory"), new("Build and publish", "/agents/build"), new("Agent detail", "/agents/detail"), new("Agent hand-offs", "/agents/handoffs")]),
        new("Enablement", "Training and champions", [
            new("Engagement", "/enablement"), new("Conversation depth", "/enablement/depth"), new("Training effect", "/enablement/training"),
            new("Champions", "/enablement/champions", Roles.Enablement), new("People", "/enablement/people", Roles.Enablement)]),
        new("Forecasting", "Finance, licensing and programme leads", [
            new("Forecast", "/forecasting"), new("Licence optimisation", "/forecasting/optimise"), new("Budget and renewal", "/forecasting/budget"),
            new("Upgrade, train or revoke", "/forecasting/actions", Roles.Licensing), new("Rollout waves", "/forecasting/waves", Roles.Licensing),
            new("Licence action outcomes", "/forecasting/outcomes", Roles.Licensing)]),
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
        Groups.Select(g => g with { Pages = g.Pages.Where(p => (manyTenants || !p.ManyTenants) && Allowed(p, viewer, privacyMode)).ToList() })
              .Where(g => g.Pages.Count > 0).ToList();
}
