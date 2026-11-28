namespace CopilotInsights.Infrastructure;

/// <summary>
/// The menu's sections as an administrator grants them (Admin › Access): each header in the menu is one section, and a
/// section no grant opens for a person is not in their menu, and its pages are refused. Whole organization and the lists
/// that name people come with the grant. The Workbench's two everyday sections are granted the same way; its work roles
/// (Governance, Governance operator, Governance auditor) stay roles.
/// </summary>
public static class Sections
{
    public const string Leadership = "leadership", Adoption = "adoption", Licensing = "licensing", Forecasting = "forecasting",
        Security = "security", Compliance = "compliance", Agents = "agents", Enablement = "enablement", Regions = "regions",
        People = "people", AppOwners = "app-owners", Governance = "governance", Platforms = "platforms",
        WorkbenchOwnership = "wb-ownership", WorkbenchRegistry = "wb-registry";

    /// <param name="Key">What a grant stores (dash.RoleGrant.Sections).</param>
    /// <param name="Name">The header in the menu.</param>
    /// <param name="Who">Who it is for, in a few words.</param>
    /// <param name="NamedRole">The role its lists that name people need, which a grant with named people gives; null when it has none.</param>
    /// <param name="Workbench">One of the Governance Workbench's sections, offered only where the Workbench is set up.</param>
    public sealed record Section(string Key, string Name, string Who, string? NamedRole, bool Workbench = false);

    /// <summary>In the menu's order.</summary>
    public static readonly IReadOnlyList<Section> All =
    [
        new(Leadership, "Leadership", "Executives and department heads", null),
        new(Adoption, "Adoption", "Copilot program leads", Roles.Adoption),
        new(Licensing, "Licensing", "Licensing, IT asset management and FinOps", Roles.Licensing),
        new(Security, "Security", "Security operations", Roles.Security),
        new(Compliance, "Compliance", "Data protection and compliance", Roles.Compliance),
        new(Agents, "Agents", "Copilot Studio makers and the center of excellence", Roles.Agents),
        new(Enablement, "Enablement", "Training leads and champions", Roles.Enablement),
        new(Forecasting, "Forecasting", "Finance, licensing and program leads", Roles.Licensing),
        new(Regions, "Regions", "Country, office and site leads", null),
        new(People, "People", "HR and people analytics", null),
        new(AppOwners, "App owners", "Microsoft 365 workload owners", null),
        new(Governance, "AI apps and controls", "Models, AI apps beyond Copilot and agent policies", null),
        new(Platforms, "AI platforms", "Teams running Foundry and GitHub Copilot", Roles.Platforms),
        new(WorkbenchOwnership, "Ownership and approvals", "Requests, approvals and the workloads people own", null, true),
        new(WorkbenchRegistry, "Ownership registry", "Funding owners, workloads, seats and the service catalog", null, true),
    ];

    public static readonly IReadOnlyList<string> Keys = [.. All.Select(s => s.Key)];

    public static Section? Find(string? key) => All.FirstOrDefault(s => string.Equals(s.Key, key, StringComparison.OrdinalIgnoreCase));

    public static string NameOf(string key) => Find(key)?.Name ?? key;

    /// <summary>
    /// The sections a role granted before 3.1.0 opens, as it opened them: each persona role its own section (Licensing,
    /// Licensing and Forecasting), with its lists that name people. Every other role opens none.
    /// </summary>
    public static IReadOnlyList<string> OfRole(string? role) => role switch
    {
        Roles.Adoption => [Adoption],
        Roles.Licensing => [Licensing, Forecasting],
        Roles.Security => [Security],
        Roles.Compliance => [Compliance],
        Roles.Agents => [Agents],
        Roles.Enablement => [Enablement],
        Roles.Platforms => [Platforms],
        _ => [],
    };

    /// <summary>The known keys in a stored list, in the menu's order; unknown ones are dropped.</summary>
    public static IReadOnlyList<string> Parse(string? stored) =>
        string.IsNullOrWhiteSpace(stored) ? []
        : [.. Keys.Where(k => stored.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Contains(k, StringComparer.OrdinalIgnoreCase))];

    /// <summary>What a grant stores: the known keys ticked, in the menu's order; null when none.</summary>
    public static string? Format(IEnumerable<string?>? ticked)
    {
        var list = Parse(string.Join(",", (ticked ?? []).Where(t => !string.IsNullOrWhiteSpace(t))));
        return list.Count == 0 ? null : string.Join(",", list);
    }

    /// <summary>The names of the stored keys, for messages and lists: "Leadership, Licensing".</summary>
    public static string Names(string? stored) => Names(Parse(stored));

    public static string Names(IEnumerable<string> keys) => string.Join(", ", keys.Select(NameOf));

    /// <summary>The roles a grant of these sections with named people gives, for the pages that name people in them.</summary>
    public static IEnumerable<string> NamedRoles(IEnumerable<string> keys) =>
        keys.Select(k => Find(k)?.NamedRole).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase);
}
