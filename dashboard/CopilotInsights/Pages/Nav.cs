using CopilotInsights.Infrastructure;

namespace CopilotInsights.Pages;

public sealed record NavPage(string Title, string Path, string? Role = null, bool Admin = false);
public sealed record NavGroup(string Persona, string Who, IReadOnlyList<NavPage> Pages);

/// <summary>The personas and their pages. A page with a Role lists named people and needs that role.</summary>
public static class Nav
{
    public static readonly IReadOnlyList<NavGroup> Groups =
    [
        new("Leadership", "Executives and department heads", [
            new("Overview", "/leadership"), new("Organisation", "/leadership/org"), new("Apps and agents", "/leadership/mix")]),
        new("Adoption", "Copilot programme lead", [
            new("Funnel and trend", "/adoption"), new("Licence cohorts", "/adoption/cohorts"),
            new("Departments", "/adoption/departments"), new("Not started", "/adoption/not-started", Roles.Adoption)]),
        new("Licensing", "Licensing, IT asset and FinOps", [
            new("Inventory", "/licensing"), new("Unused licences", "/licensing/unused", Roles.Licensing),
            new("Demand", "/licensing/demand")]),
        new("Security", "Security operations", [
            new("Signals", "/security"), new("Flagged interactions", "/security/flags", Roles.Security)]),
        new("Compliance", "Data protection and compliance", [
            new("Sensitive content", "/compliance"), new("Resource access", "/compliance/access", Roles.Compliance)]),
        new("Agents", "Copilot Studio makers and CoE", [
            new("Agents", "/agents"), new("Agent detail", "/agents/detail")]),
        new("Enablement", "Training and champions", [
            new("Engagement", "/enablement"), new("People", "/enablement/people", Roles.Enablement)]),
        new("Governance", "AI governance and architecture", [
            new("Models and grounding", "/governance")]),
        new("Admin", "Dashboard administrators", [
            new("Health", "/admin", Admin: true), new("Access", "/admin/access", Admin: true),
            new("Reference data", "/admin/reference", Admin: true), new("Audit log", "/admin/audit", Admin: true)]),
    ];
}
