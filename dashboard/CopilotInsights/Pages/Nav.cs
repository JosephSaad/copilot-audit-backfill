using CopilotInsights.Infrastructure;

namespace CopilotInsights.Pages;

public sealed record NavPage(string Title, string Path, string? Role = null, bool Admin = false);
public sealed record NavGroup(string Persona, string Who, IReadOnlyList<NavPage> Pages);

/// <summary>The personas and their pages. A page with a Role lists named people and needs that role.</summary>
public static class Nav
{
    public static readonly IReadOnlyList<NavGroup> Groups =
    [
        new("My Copilot", "Everyone: your own use", [
            new("My use", "/me")]),
        new("Leadership", "Executives and department heads", [
            new("Overview", "/leadership"), new("Scorecard", "/leadership/scorecard"), new("Organisation", "/leadership/org"), new("Apps and agents", "/leadership/mix")]),
        new("Adoption", "Copilot programme lead", [
            new("Funnel and trend", "/adoption"), new("Retention and habit", "/adoption/retention"), new("Licence cohorts", "/adoption/cohorts"),
            new("Departments", "/adoption/departments"), new("Not started", "/adoption/not-started", Roles.Adoption)]),
        new("Licensing", "Licensing, IT asset and FinOps", [
            new("Inventory", "/licensing"), new("Unused licences", "/licensing/unused", Roles.Licensing),
            new("Demand", "/licensing/demand"), new("Licence changes", "/licensing/changes")]),
        new("Security", "Security operations", [
            new("Signals", "/security"), new("Signals by unit", "/security/units"), new("Flagged interactions", "/security/flags", Roles.Security)]),
        new("Compliance", "Data protection and compliance", [
            new("Sensitive content", "/compliance"), new("Labelled content by unit", "/compliance/units"), new("Resource access", "/compliance/access", Roles.Compliance)]),
        new("Agents", "Copilot Studio makers and CoE", [
            new("Agents", "/agents"), new("Agent detail", "/agents/detail"), new("Agent hand-offs", "/agents/handoffs")]),
        new("Enablement", "Training and champions", [
            new("Engagement", "/enablement"), new("Conversation depth", "/enablement/depth"), new("People", "/enablement/people", Roles.Enablement)]),
        new("Forecasting", "Finance, licensing and programme leads", [
            new("Forecast", "/forecasting"), new("Licence optimisation", "/forecasting/optimise"),
            new("Upgrade, train or revoke", "/forecasting/actions", Roles.Licensing)]),
        new("Regions", "Country, office and site leads", [
            new("Countries and offices", "/regions"), new("Working patterns", "/regions/patterns")]),
        new("People", "HR and people analytics", [
            new("Workforce", "/workforce"), new("Managers and their teams", "/workforce/managers")]),
        new("App owners", "Microsoft 365 workload owners", [
            new("Apps", "/apps"), new("App detail", "/apps/detail")]),
        new("Governance", "AI governance and architecture", [
            new("Models and grounding", "/governance"), new("Controls", "/governance/controls")]),
        new("Admin", "Dashboard administrators", [
            new("Health", "/admin", Admin: true), new("Access", "/admin/access", Admin: true),
            new("Reference data", "/admin/reference", Admin: true), new("Audit log", "/admin/audit", Admin: true)]),
    ];

    /// <summary>
    /// Whether a page is open to the viewer: admin pages to administrators, role pages to holders
    /// of the role, and report pages to anyone with data to see (in the directory, or granted
    /// Organization). The navigation and the start page show only these; the rest are not listed.
    /// </summary>
    public static bool Allowed(NavPage page, Viewer viewer) =>
        page.Admin ? viewer.IsAdmin
                   : (viewer.OrgWide || viewer.InDirectory) && (page.Role is null || viewer.Has(page.Role));

    /// <summary>The groups and pages this viewer may open; a group with none is left out.</summary>
    public static IReadOnlyList<NavGroup> For(Viewer? viewer) =>
        viewer is null ? [] :
        Groups.Select(g => g with { Pages = g.Pages.Where(p => Allowed(p, viewer)).ToList() })
              .Where(g => g.Pages.Count > 0).ToList();
}
