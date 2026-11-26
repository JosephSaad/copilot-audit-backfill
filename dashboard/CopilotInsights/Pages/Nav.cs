using CopilotInsights.Infrastructure;

namespace CopilotInsights.Pages;

public sealed record NavPage(string Title, string Path, string? Role = null, bool Admin = false, bool ManyTenants = false, bool InPrivacy = false);
/// <summary>A section of the menu. Its Key is what a grant opens (Admin › Access); without a grant that opens it, the whole section is hidden and refused.</summary>
public sealed record NavGroup(string Persona, string Who, IReadOnlyList<NavPage> Pages, string? Role = null, string? Key = null)
{
    /// <summary>
    /// Whether this section is open to the viewer: an Insights section (with a <see cref="Key"/>) when a grant opens it
    /// (<see cref="Viewer.Sees"/>), whatever their level; Admin and the Workbench's sections by their own rules.
    /// </summary>
    public bool OpenTo(Viewer? viewer) => viewer is not null && (Key is not null ? viewer.Sees(Key) : Role is null || viewer.Has(Role));
}

/// <summary>The personas and their pages. A page with a Role lists named people and needs that role.</summary>
public static class Nav
{
    /// <summary>
    /// Copilot on non-human accounts: one page, listed in Licensing, Security and Compliance, open to a holder of any of
    /// their roles. It was three copies, one a section; /security/non-human and /compliance/non-human arrive here.
    /// </summary>
    public const string NonHuman = "/licensing/non-person";

    /// <summary>The sections listing a page: one, or several for a page shared between them.</summary>
    public static IReadOnlyList<NavGroup> SectionsOf(string? path) =>
        path is null ? [] : [.. Groups.Where(g => g.Pages.Any(p => string.Equals(p.Path, path.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)))];

    /// <summary>
    /// What turns a page off in a section (Admin › Reports shown): its path, but for the non-human page in Security and
    /// Compliance, which keep the addresses of their own copies before 2.7.6, so a copy turned off then stays off there,
    /// and each section's listing is turned off on its own.
    /// </summary>
    public static string Key(NavGroup g, NavPage p) =>
        p.Path == NonHuman && g.Persona is "Security" or "Compliance" ? "/" + g.Persona.ToLowerInvariant() + "/non-human" : p.Path;

    static string KeyIn(NavGroup g, string path) =>
        g.Pages.FirstOrDefault(p => string.Equals(p.Path, path.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)) is { } p ? Key(g, p) : path;

    /// <summary>
    /// Whether the viewer may open a page as far as its sections go: one of them needs no role, or one the viewer holds,
    /// and has not turned it off there (<paramref name="hidden"/>).
    /// </summary>
    public static bool SectionOpen(string? path, Viewer viewer, IReadOnlySet<string>? hidden = null)
    {
        var sections = SectionsOf(path);
        return sections.Count == 0 || sections.Any(g => g.OpenTo(viewer) && !HiddenReports.Is(hidden, KeyIn(g, path!)));
    }

    /// <summary>The section a viewer reaches a page through: the first listing it that they may open, and that is not turned off.</summary>
    public static NavGroup? SectionFor(string? path, Viewer? viewer, IReadOnlySet<string>? hidden = null) =>
        SectionsOf(path).FirstOrDefault(g => g.OpenTo(viewer) && !HiddenReports.Is(hidden, KeyIn(g, path!)))
        ?? SectionsOf(path).FirstOrDefault(g => g.OpenTo(viewer)) ?? SectionOf(path);

    public static readonly IReadOnlyList<NavGroup> Groups =
    [
        new("Leadership", "Executives and department heads", [
            new("Daily brief", "/leadership/brief"), new("Overview", "/leadership"), new("What to act on", "/leadership/act"), new("Organization", "/leadership/org"), new("Compare two teams", "/leadership/compare"), new("Group overview", "/group", ManyTenants: true),
            new("Apps and agents", "/leadership/mix"), new("Use cases", "/leadership/usecases"), new("Value and time saved", "/leadership/value"), new("Costs and savings", "/leadership/costs"), new("AI spend across Azure", "/leadership/ai-spend"), new("Copilot reservations", "/leadership/reservations"), new("Credit chargeback", "/leadership/credit-chargeback"),
            new("Board pack", "/leadership/boardpack"), new("How figures are calculated", "/leadership/assumptions")], Key: Sections.Leadership),
        new("Adoption", "Copilot program leads", [
            new("Funnel and trend", "/adoption"), new("Usage by license", "/adoption/by-license", Roles.Adoption), new("Departments", "/adoption/departments"),
            new("Retention and habit", "/adoption/retention"), new("Retention by license month", "/adoption/cohorts"), new("New joiners", "/adoption/onboarding"), new("Not started", "/adoption/not-started", Roles.Adoption),
            new("Targets", "/adoption/targets"), new("Drops and spikes", "/adoption/anomalies")], Roles.Adoption, Sections.Adoption),
        new("Licensing", "Licensing, IT asset management and FinOps", [
            new("License inventory", "/licensing"), new("License pool", "/licensing/pool"), new("License changes", "/licensing/changes"),
            new("Chargeback", "/licensing/chargeback"), new("Demand for licenses", "/licensing/demand"), new("Agent 365 sizing", "/licensing/agent-365"),
            new("Unused licenses", "/licensing/unused", Roles.Licensing), new("Use ranking", "/licensing/ranking", Roles.Licensing),
            new("Better on pay-as-you-go", "/licensing/payg", Roles.Licensing), new("Swap list", "/licensing/swap", Roles.Licensing),
            new("Better with a license", "/licensing/license-for-credits", Roles.Licensing), new("Fading use", "/licensing/fading", Roles.Licensing),
            new("Overlapping licenses", "/licensing/overlap", Roles.Licensing), new("Suite fit", "/licensing/suites", Roles.Licensing), new("Reservation holders", "/licensing/reservation-holders", Roles.Licensing), new("Credit use", "/licensing/credit-use", Roles.Licensing),
            new("Licenses on disabled accounts", "/licensing/leavers", Roles.Licensing), new("Copilot on non-human accounts", NonHuman, Roles.Licensing),
            new("Departures ahead", "/licensing/departures", Roles.Licensing)], Roles.Licensing, Sections.Licensing),
        new("Security", "Security operations", [
            new("Security signals", "/security"), new("Guests and external users", "/security/guests"),
            new("Flagged interactions", "/security/flags", Roles.Security), new("Copilot on non-human accounts", NonHuman, Roles.Security)], Roles.Security, Sections.Security),
        new("Compliance", "Data protection and compliance", [
            new("Sensitive content", "/compliance"), new("Labeled content by unit", "/compliance/units"), new("Oversharing hotspots", "/compliance/sites"),
            new("Access to labeled content", "/compliance/access", Roles.Compliance), new("Person record", "/compliance/person", Roles.Compliance, InPrivacy: true),
            new("Copilot on non-human accounts", NonHuman, Roles.Compliance)], Roles.Compliance, Sections.Compliance),
        new("Agents", "Copilot Studio makers and the center of excellence", [
            new("My agents", "/agents/mine"), new("Agents in use", "/agents"), new("Agent inventory", "/agents/inventory"), new("Agents without an owner", "/agents/owners"), new("Agent checks", "/agents/checks"),
            new("Build and publish", "/agents/build"), new("Agent detail", "/agents/detail"), new("Agent hand-offs", "/agents/handoffs"), new("Child agent usage", "/agents/children"),
            new("Agents costing credits", "/agents/credits")], Roles.Agents, Sections.Agents),
        new("Enablement", "Training leads and champions", [
            new("Engagement", "/enablement"), new("Conversation depth", "/enablement/depth"), new("Training effect", "/enablement/training"),
            new("Champions", "/enablement/champions", Roles.Enablement), new("People by engagement", "/enablement/people", Roles.Enablement)], Roles.Enablement, Sections.Enablement),
        new("Forecasting", "Finance, licensing and program leads", [
            new("Forecast", "/forecasting"), new("License optimization", "/forecasting/optimize"), new("Budget and renewal", "/forecasting/budget"), new("Credit commitment", "/forecasting/credit-commitment"),
            new("Upgrade, train or revoke", "/forecasting/actions", Roles.Licensing), new("Rollout waves", "/forecasting/waves", Roles.Licensing),
            new("License action outcomes", "/forecasting/outcomes", Roles.Licensing)], Roles.Licensing, Sections.Forecasting),
        new("Regions", "Country, office and site leads", [
            new("Countries and offices", "/regions"), new("Working patterns", "/regions/patterns")], Key: Sections.Regions),
        new("People", "HR and people analytics", [
            new("Workforce", "/workforce"), new("Managers and their teams", "/workforce/managers"), new("Job families", "/workforce/families")], Key: Sections.People),
        new("App owners", "Microsoft 365 workload owners", [
            new("Apps", "/apps"), new("App detail", "/apps/detail")], Key: Sections.AppOwners),
        new("Governance", "AI governance and architecture", [
            new("Models and controls", "/governance"), new("AI apps beyond Copilot", "/governance/ai-apps"), new("AI resource detail", "/governance/ai-apps/resource")], Key: Sections.Governance),
        /* AI platforms: the Foundry and GitHub Copilot platform teams' section. Its own grant gives them exactly these pages;
           AI apps beyond Copilot and a resource's detail are listed here too, as in Governance. */
        new("AI platforms", "Teams running Foundry and GitHub Copilot", [
            new("Foundry overview", "/platforms/foundry"), new("Foundry deployments", "/platforms/foundry/deployments"), new("Foundry posture", "/platforms/foundry/posture"),
            new("GitHub Copilot overview", "/platforms/github"), new("GitHub Copilot seats", "/platforms/github/seats", Roles.Platforms), new("GitHub Copilot posture", "/platforms/github/posture"),
            new("AI apps beyond Copilot", "/governance/ai-apps"), new("AI resource detail", "/governance/ai-apps/resource")], Roles.Platforms, Sections.Platforms),
        new("Admin", "Dashboard administrators", [
            new("Health", "/admin/health", Admin: true), new("Access", "/admin/access", Admin: true), new("Access profiles", "/admin/profiles", Admin: true), new("Directory quality", "/admin/directory", Admin: true),
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
    /// Outside the dashboard's levels, only an administrator's Admin pages are.
    /// </summary>
    public static bool Allowed(NavPage page, Viewer viewer, bool privacyMode = false) =>
        !viewer.Admitted ? page.Admin && viewer.CanAdmin(page.Path)
        : page.ManyTenants ? (viewer.OrgWide || viewer.InDirectory)
        : page.Admin ? viewer.CanAdmin(page.Path)
                   : (viewer.OrgWide || viewer.InDirectory) && (page.Role is null || ((!privacyMode || page.InPrivacy) && viewer.Has(page.Role)));

    /// <summary>The groups and pages this viewer may open; a group with none is left out.</summary>
    public static IReadOnlyList<NavGroup> For(Viewer? viewer, bool privacyMode = false, bool manyTenants = false, IReadOnlySet<string>? hidden = null) =>
        viewer is null ? [] :
        Groups.Where(g => g.OpenTo(viewer))
              .Select(g => g with { Pages = g.Pages.Where(p => (manyTenants || !p.ManyTenants) && Allowed(p, viewer.ForSection(g.Key), privacyMode) && !HiddenReports.Is(hidden, Key(g, p))).ToList() })
              .Where(g => g.Pages.Count > 0).ToList();

    /// <summary>The role a page's section needs, if any: the section is hidden, and its pages refused, without it.</summary>
    /// <summary>
    /// The page before <paramref name="path"/> in its section and the one after, among the pages offered:
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
    /// The pages nearest to an address that has no page, among the pages offered: those in the section the
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
/// The reports an administrator has turned off (Admin › Reports shown): gone from the navigation and the start
/// page, and not found at their address, for everyone, administrators too. Kept in dash.Setting, one row a report,
/// named Hide: and its path. Admin pages cannot be turned off.
/// </summary>
public static class HiddenReports
{
    public const string Prefix = "Hide:";

    /// <summary>
    /// The parts of a page that were pages of their own before 2.7.6, turned off as they were: by their old address. A
    /// report turned off then stays off as part of the page that took it, and can be turned on again.
    /// </summary>
    public static readonly IReadOnlyList<(string Section, NavPage Part)> Parts =
    [
        ("Leadership", new("Board pack: each unit against the period before (was the Scorecard)", "/leadership/scorecard")),
        ("Adoption", new("Departments: the ranking against the period before (was the Leaderboard)", "/adoption/leaderboard")),
        ("Agents", new("Agent inventory: Shared agents", "/agents/shared")),
        ("Agents", new("Agent inventory: In production", "/agents/production")),
    ];

    /// <summary>
    /// Every report that can be turned off, by section: all but the Admin pages, the non-human page in each section
    /// that lists it (<see cref="Nav.Key"/>), and the parts that were pages of their own (<see cref="Parts"/>).
    /// </summary>
    public static IEnumerable<NavGroup> Choices =>
        Nav.Groups.Select(g => g with { Pages = [.. g.Pages.Where(p => !p.Admin).Select(p => p with { Path = Nav.Key(g, p) }), .. Parts.Where(x => x.Section == g.Persona).Select(x => x.Part)] })
                  .Where(g => g.Pages.Count > 0);

    /// <summary>
    /// Whether a page is turned off for this viewer: its path, or, for a page listed in several sections, every section
    /// the viewer reaches it through (every section, for one who reaches it through none).
    /// </summary>
    public static bool IsOff(IReadOnlySet<string>? hidden, string? path, Viewer? viewer)
    {
        if (hidden is not { Count: > 0 } || path is null) return false;
        var sections = Nav.SectionsOf(path);
        if (sections.Count <= 1) return Is(hidden, path);
        var page = path.TrimEnd('/');
        string Key(NavGroup g) => Nav.Key(g, g.Pages.First(p => string.Equals(p.Path, page, StringComparison.OrdinalIgnoreCase)));
        var mine = sections.Where(g => g.OpenTo(viewer)).ToList();
        return (mine.Count > 0 ? mine : sections).All(g => Is(hidden, Key(g)));
    }

    public static async Task<IReadOnlySet<string>> GetAsync(Settings settings) =>
        (await settings.AllAsync()).Where(kv => kv.Key.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) && kv.Value == "1")
            .Select(kv => kv.Key[Prefix.Length..]).ToHashSet(StringComparer.OrdinalIgnoreCase);

    public static bool Is(IReadOnlySet<string>? hidden, string? path) =>
        hidden is { Count: > 0 } && path is not null && hidden.Contains(path.Length > 1 ? path.TrimEnd('/') : path);

    /// <summary>
    /// A beta report (Admin › Reports shown): a setting of its own beside the one that turns a report off, so a dashboard
    /// from before reads a beta report as shown.
    /// </summary>
    public const string BetaPrefix = "Beta:";

    public static async Task<IReadOnlySet<string>> BetaAsync(Settings settings) =>
        (await settings.AllAsync()).Where(kv => kv.Key.StartsWith(BetaPrefix, StringComparison.OrdinalIgnoreCase) && kv.Value == "1")
            .Select(kv => kv.Key[BetaPrefix.Length..]).ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The reports a viewer cannot open: those turned off, and the beta ones not open to them (<see cref="Viewer.Previews"/>).
    /// Everything that reads the reports turned off (the menu, the start page, a page's own check) reads this, so a beta
    /// report is gone for anyone not given a preview of it, as one turned off is. Pure, for the tests.
    /// </summary>
    public static IReadOnlySet<string> ForViewer(IReadOnlySet<string> hidden, IReadOnlySet<string> beta, Viewer? viewer)
    {
        if (beta.Count == 0) return hidden;
        var off = new HashSet<string>(hidden, StringComparer.OrdinalIgnoreCase);
        off.UnionWith(beta.Where(b => viewer?.Previews(b) != true));
        return off;
    }

    /// <summary>The beta report keys a Preview grant stores (dash.RoleGrant.Pages), comma-separated; blanks dropped.</summary>
    public static IReadOnlyList<string> ParseKeys(string? stored) =>
        string.IsNullOrWhiteSpace(stored) ? []
        : [.. stored.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(k => k.Length > 1 ? k.TrimEnd('/') : k).Distinct(StringComparer.OrdinalIgnoreCase)];

    /// <summary>What a Preview grant stores: the reports ticked that are beta now, in the menu's order; null when none.</summary>
    public static string? FormatKeys(IEnumerable<string?>? ticked, IReadOnlySet<string> beta)
    {
        var want = new HashSet<string>((ticked ?? []).OfType<string>().Select(t => t.Trim()), StringComparer.OrdinalIgnoreCase);
        var keys = Choices.SelectMany(g => g.Pages).Select(p => p.Path).Where(p => want.Contains(p) && beta.Contains(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return keys.Count == 0 ? null : string.Join(",", keys);
    }

    /// <summary>The titles of stored report keys, for messages and lists.</summary>
    public static string Titles(string? stored)
    {
        var all = Choices.SelectMany(g => g.Pages).GroupBy(p => p.Path, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().Title, StringComparer.OrdinalIgnoreCase);
        return string.Join(", ", ParseKeys(stored).Select(k => all.GetValueOrDefault(k, k)));
    }
}
