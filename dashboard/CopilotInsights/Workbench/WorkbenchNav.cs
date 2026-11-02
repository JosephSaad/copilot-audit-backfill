using CopilotInsights.Pages;
using Governance.Domain;
using Microsoft.Extensions.Caching.Memory;

namespace Governance.Web.Infrastructure;

/// <summary>
/// The Workbench's sections in the site's menu, after the Insights ones: what this person may open, by their Workbench
/// page roles (from their Insights grants) and their Workbench roles. Each page says what it is for in <see cref="What"/>.
/// </summary>
public static class WorkbenchNav
{
    const string P = WorkbenchSetup.PathPrefix;

    static readonly Dictionary<string, string> Text = new(StringComparer.OrdinalIgnoreCase)
    {
        [$"{P}/Start"] = "Where to begin: the journeys you can start, and the setup steps left before intake opens.",
        [$"{P}/MyWork"] = "The tasks waiting for you or your queues, oldest first, with what each needs.",
        [$"{P}/Requests"] = "Every request you started or can see, with its stage and who acts next.",
        [$"{P}/Events/Report"] = "Report a consumption problem: an agent or workload using more than it should.",
        [$"{P}/Recertification"] = "Confirm or change each workload you own: need, audience, funding and outcome.",
        [$"{P}/Exceptions"] = "Approved exceptions to a control, each with its expiry.",
        [$"{P}/Retirement"] = "Workloads being retired and what is left to do for each.",
        [$"{P}/Me"] = "Your Workbench roles, the lines of business they cover, and your delegations.",
        [$"{P}/Queues/Intake"] = "New requests for the intake team to check for completeness.",
        [$"{P}/Queues/Classification"] = "Complete requests waiting to be classified by risk and route.",
        [$"{P}/Queues/Reviews"] = "Specialist reviews (architecture, risk, licensing) waiting for an answer.",
        [$"{P}/Queues/Decisions"] = "Requests ready for the accountable role's decision.",
        [$"{P}/Queues/Fulfillment"] = "Approved changes being carried out through ITSM, and their verification.",
        [$"{P}/Registry/FundingOwners"] = "The lines of business and Enterprise Shared that own and fund Copilot workloads.",
        [$"{P}/Registry/Workloads"] = "Each approved Copilot workload, its owner, funding and approved setting.",
        [$"{P}/Registry/Envelopes"] = "Each funding owner's approved consumption envelope and what it has committed.",
        [$"{P}/Registry/Seats"] = "Paid Microsoft 365 Copilot seats per line of business: reserved, assigned, over and unused.",
        [$"{P}/Registry/Services"] = "The approved service catalog workloads are built from.",
        [$"{P}/Registry/Templates"] = "The control template each approved service must meet.",
        [$"{P}/Registry/CostCenters"] = "Cost centers chargeback lands on, by funding owner.",
        [$"{P}/Registry/Commercial"] = "Licenses, packs, P3 plans and pay-as-you-go scopes, with renewal dates.",
        [$"{P}/Registry/Discovery"] = "What exists in the tenant but is not registered yet: register it or claim it.",
        [$"{P}/Operations/Events"] = "Consumption events: spikes, budget alerts and reported problems, with their acknowledgement.",
        [$"{P}/Operations/Findings"] = "Drift from the approved setting, unregistered agents and seat findings to follow up.",
        [$"{P}/Operations/Sync"] = "When each read from Microsoft and ITSM last ran, and running one now.",
        [$"{P}/Operations/Attestations"] = "Settings no API can read back, attested by a second operator.",
        [$"{P}/Reports/Kpis"] = "The Workbench's own measures: cycle times, SLAs met, findings closed.",
        [$"{P}/Reports/Audit"] = "Who did what in the Workbench, from the tamper-evident ledger, with export.",
        [$"{P}/Reports/SeatChargeback"] = "Monthly Copilot seat chargeback per line of business, drafted by the worker and certified by FinOps.",
        [$"{P}/Admin/Raci"] = "Who is responsible, accountable, consulted and informed for each activity.",
        [$"{P}/Admin/Workflows"] = "The stages of each journey and who acts at each.",
        [$"{P}/Admin/Sla"] = "How long each stage may take, the reminders and the escalation ladder.",
        [$"{P}/Admin/Notifications"] = "Which notices go to whom, by email and Teams.",
        [$"{P}/Admin/Roles"] = "Who holds each Workbench role (RACI positions, delegations, LOB seat admins).",
        [$"{P}/Admin/SeatGroups"] = "The Copilot license group, each line of business's tracking group, and the seat settings.",
        [$"{P}/Admin/Integrations"] = "The Microsoft, ITSM, mail and Teams connections and their health.",
        [$"{P}/Admin/Intake"] = "Open or close intake, with the setup steps it needs first.",
        [$"{P}/Admin/Text"] = "Change the wording of help and notices.",
    };

    public static string? What(string path) => Text.TryGetValue(path, out var t) ? t : null;

    /// <summary>
    /// The sections for the menu of any page. On an Insights page the Workbench person is not loaded for the request, so
    /// it is loaded here and the sections kept two minutes per person; a Workbench database that does not answer leaves
    /// them out rather than fail the page.
    /// </summary>
    public static async Task<IReadOnlyList<NavGroup>> ForAsync(HttpContext ctx, WebPrincipal me, bool on)
    {
        if (!on || ctx.User.Identity?.IsAuthenticated != true) return [];
        if (me.IsAuthenticated) return For(me, on);
        var cache = ctx.RequestServices.GetRequiredService<Microsoft.Extensions.Caching.Memory.IMemoryCache>();
        var key = "workbench-nav:" + (ctx.User.Identity?.Name ?? CopilotInsights.Infrastructure.EntraClaims.SignInName(ctx.User));
        if (cache.TryGetValue(key, out IReadOnlyList<NavGroup>? known) && known is not null) return known;
        IReadOnlyList<NavGroup> groups;
        try
        {
            await ctx.RequestServices.GetRequiredService<PrincipalLoader>().LoadAsync(ctx, me);
            groups = For(me, on);
        }
        catch (Exception ex)
        {
            ctx.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(WorkbenchNav)).LogWarning(ex, "The Workbench menu could not be loaded; the page is shown without it.");
            groups = [];
        }
        cache.Set(key, groups, TimeSpan.FromMinutes(2));
        return groups;
    }

    /// <summary>The Workbench sections this person may open: none when the Workbench is off or they could not be loaded.</summary>
    public static IReadOnlyList<NavGroup> For(WebPrincipal? me, bool on)
    {
        if (!on || me is not { IsAuthenticated: true }) return [];
        var central = me.AppRoles.Contains(AppRoles.Central) || me.AppRoles.Contains(AppRoles.Admin);
        var oper = me.AppRoles.Contains(AppRoles.Operator);
        var isAdmin = me.AppRoles.Contains(AppRoles.Admin);
        var settings = isAdmin || me.HasCentral(Roles.PlatformOwner, Roles.Steering);
        var auditor = me.AppRoles.Contains(AppRoles.Auditor) || me.AppRoles.Contains(AppRoles.Central);

        var groups = new List<NavGroup>
        {
            new("Ownership and approvals", "Everyone: requests, approvals and the workloads you own", [
                new("Start", $"{P}/Start"), new("My work", $"{P}/MyWork"), new("Requests", $"{P}/Requests"),
                new("Report a consumption problem", $"{P}/Events/Report"), new("Recertification", $"{P}/Recertification"),
                new("Exceptions", $"{P}/Exceptions"), new("Retirement", $"{P}/Retirement"), new("Your Workbench roles", $"{P}/Me")]),
        };
        if (central)
            groups.Add(new("Approval queues", "Intake, specialists and decision makers", [
                new("Intake", $"{P}/Queues/Intake"), new("Classification", $"{P}/Queues/Classification"), new("Specialist reviews", $"{P}/Queues/Reviews"),
                new("Decisions", $"{P}/Queues/Decisions"), new("Fulfillment and verification", $"{P}/Queues/Fulfillment")]));

        var registry = new List<NavPage> { new("Funding owners", $"{P}/Registry/FundingOwners"), new("Workloads", $"{P}/Registry/Workloads"), new("Envelopes", $"{P}/Registry/Envelopes") };
        if (Governance.Web.Pages.Registry.SeatsModel.CanOpen(me)) registry.Add(new("Copilot seats", $"{P}/Registry/Seats"));
        registry.Add(new("Services", $"{P}/Registry/Services"));
        registry.Add(new("Control templates", $"{P}/Registry/Templates"));
        if (central)
        {
            registry.Add(new("Cost centers", $"{P}/Registry/CostCenters"));
            registry.Add(new("Commercial", $"{P}/Registry/Commercial"));
            registry.Add(new("Discovery", $"{P}/Registry/Discovery"));
        }
        groups.Add(new("Ownership registry", "Funding owners, workloads, seats and the service catalog", registry));

        var ops = new List<NavPage>();
        if (central)
        {
            ops.Add(new("Consumption events", $"{P}/Operations/Events"));
            ops.Add(new("Drift and findings", $"{P}/Operations/Findings"));
            if (oper)
            {
                ops.Add(new("Sync and freshness", $"{P}/Operations/Sync"));
                ops.Add(new("Attestations", $"{P}/Operations/Attestations"));
            }
        }
        if (auditor)
        {
            ops.Add(new("Governance KPIs", $"{P}/Reports/Kpis"));
            ops.Add(new("Audit trail", $"{P}/Reports/Audit"));
            ops.Add(new("Seat chargeback", $"{P}/Reports/SeatChargeback"));
        }
        if (ops.Count > 0) groups.Add(new("Governance operations", "The platform team, FinOps and auditors", ops));

        if (settings)
        {
            var s = new List<NavPage> { new("RACI", $"{P}/Admin/Raci") };
            if (isAdmin)
            {
                s.Add(new("Workflows", $"{P}/Admin/Workflows"));
                s.Add(new("SLAs and escalation", $"{P}/Admin/Sla"));
                s.Add(new("Notifications", $"{P}/Admin/Notifications"));
            }
            s.Add(new("Workbench roles", $"{P}/Admin/Roles"));
            s.Add(new("Seat groups", $"{P}/Admin/SeatGroups"));
            if (isAdmin) s.Add(new("Integrations", $"{P}/Admin/Integrations"));
            s.Add(new("Intake switch", $"{P}/Admin/Intake"));
            s.Add(new("Wording", $"{P}/Admin/Text"));
            groups.Add(new("Workbench settings", "Administrators, the Platform Owner and Steering", s));
        }
        return groups;
    }
}
