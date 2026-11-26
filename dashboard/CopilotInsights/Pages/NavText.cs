namespace CopilotInsights.Pages;

/// <summary>
/// One sentence on what each page answers, and, in the larger sections, the heading it sits under.
/// Written once and shown wherever a page is offered: the hover text of its link in the navigation, the section's
/// panel, the start page, the search, and Admin › Reports shown. A page with no entry here fails a test.
/// </summary>
public static class NavText
{
    public static string? What(NavPage page) => Text.TryGetValue(page.Path, out var t) ? t.What : global::Governance.Web.Infrastructure.WorkbenchNav.What(page.Path);
    public static string? Heading(NavPage page) => Text.TryGetValue(page.Path, out var t) ? t.Heading : null;

    /// <summary>The hover text of a link: what the page answers, and the role it needs.</summary>
    public static string? Tip(NavPage page) =>
        What(page) is { } w ? w + (page.Role is null ? "" : $" Needs the {page.Role} role.") : null;

    /// <summary>A section's pages under their headings, in the section's own order; one group with no heading where it has none.</summary>
    public static IEnumerable<(string? Heading, List<NavPage> Pages)> Sub(IEnumerable<NavPage> pages)
    {
        var groups = new List<(string? Heading, List<NavPage> Pages)>();
        var list = pages.ToList();
        var unheaded = list.Any(x => Heading(x) is null);
        foreach (var p in list)
        {
            /* A page shared by several sections keeps its heading only in a section whose pages all have one. */
            var h = unheaded && Nav.SectionsOf(p.Path).Count > 1 ? null : Heading(p);
            if (groups.Count > 0 && groups[^1].Heading == h) groups[^1].Pages.Add(p);
            else groups.Add((h, [p]));
        }
        return groups;
    }

    static readonly Dictionary<string, (string What, string? Heading)> Text = new(StringComparer.OrdinalIgnoreCase)
    {
        ["/leadership"] = ("Is Copilot being used, by how many of the people who have it, and where.", "At a glance"),
        ["/leadership/act"] = ("What the other reports say to do: each finding, how many, and where the detail is.", "At a glance"),
        ["/leadership/org"] = ("Each part of the organization one level below, side by side.", "At a glance"),
        ["/leadership/compare"] = ("Two managers' teams side by side: adoption, use, depth and apps, over the same period.", "At a glance"),
        ["/group"] = ("Each tenant's headline figures for the period, and the group's totals.", "At a glance"),
        ["/leadership/mix"] = ("Where people use Copilot: which apps, chat or in-app, and how much through agents.", "What it is used for"),
        ["/leadership/usecases"] = ("What Copilot is used for: email, meetings, writing, analysis, research and agents.", "What it is used for"),
        ["/leadership/value"] = ("An estimate of the hours Copilot saved and what they are worth.", "What it is used for"),
        ["/leadership/brief"] = ("The week in AI: use against the week before, cost, return, the AI bill and what to act on.", "At a glance"),
        ["/leadership/ai-spend"] = ("Every AI service's cost in Azure, actual and amortized, against forecast and budgets.", "What it is used for"),
        ["/leadership/costs"] = ("What AI and automation cost, what can be saved, and what they return, all areas on one page.", "What it is used for"),
        ["/leadership/reservations"] = ("Each organization's reserved Copilot seats against its licenses, and what it bills.", "Cost savings"),
        ["/leadership/credit-chargeback"] = ("Each line of business's Copilot Credits by path, and what they charge it.", "Cost savings"),
        ["/leadership/model-tokens"] = ("The model tokens each business unit used through the AI gateway, by its L2 and L3 executives.", "Cost savings"),
        ["/leadership/boardpack"] = ("The program on one page: change on the period before, by unit, targets, value and risk.", "Reporting"),
        ["/leadership/assumptions"] = ("Every price and assumption a cost, saving or value rests on, and where it is set.", "Reporting"),

        ["/adoption"] = ("From license to habit: how many licensed people started, are active, and keep going.", "Overview"),
        ["/adoption/by-license"] = ("How the people of one license type use Copilot: who, which apps, how long.", "Overview"),
        ["/adoption/departments"] = ("Adoption in each department, side by side.", "Overview"),
        ["/adoption/retention"] = ("Do people keep using Copilot once they start? Week by week.", "Who is and is not using it"),
        ["/adoption/cohorts"] = ("Do people keep using Copilot after they get a license? By the month licensed.", "Who is and is not using it"),
        ["/adoption/onboarding"] = ("People newly licensed or hired: how long until they first used Copilot.", "Who is and is not using it"),
        ["/adoption/not-started"] = ("People licensed for 14 days or more who have never used Copilot.", "Who is and is not using it"),
        ["/adoption/targets"] = ("Each adoption target: where it started, where it stands, and whether it is on track.", "Targets and trends"),
        ["/adoption/anomalies"] = ("Days when a department's Copilot use was far from usual for that weekday.", "Targets and trends"),

        ["/licensing"] = ("How many Copilot licenses are assigned, how that changed, and how many are in use.", "Inventory and pool"),
        ["/licensing/pool"] = ("Copilot seats bought, assigned and used, from the tenant's subscriptions.", "Inventory and pool"),
        ["/licensing/changes"] = ("Every Copilot license assigned and removed in the period.", "Inventory and pool"),
        ["/licensing/chargeback"] = ("The monthly license cost each group holds, and how much of it is used.", "Inventory and pool"),
        ["/licensing/demand"] = ("People without a license who use the free Copilot Chat: where demand is.", "Inventory and pool"),
        ["/licensing/agent-365"] = ("Who must, likely and could hold an Agent 365 license, and what each way of buying costs.", "Inventory and pool"),
        ["/licensing/unused"] = ("License holders who have not used Copilot recently: reclaim or follow up.", "Cost savings"),
        ["/licensing/ranking"] = ("Everyone ranked by all use, free use, and use that needs the license.", "Cost savings"),
        ["/licensing/payg"] = ("License holders whose use would cost less on pay-as-you-go credits.", "Cost savings"),
        ["/licensing/swap"] = ("Idle licenses paired with people who want one, so no new seat is bought.", "Cost savings"),
        ["/licensing/license-for-credits"] = ("People without a license whose credits cost more than a license would.", "Cost savings"),
        ["/licensing/fading"] = ("License holders who used Copilot regularly and now use it much less.", "Cost savings"),
        ["/licensing/overlap"] = ("People holding the Copilot license and another Copilot-family license too.", "Cost savings"),
        ["/licensing/credit-use"] = ("Copilot Credits and tokens by person and agent, estimated, with a cap to try.", "Cost savings"),
        ["/licensing/suites"] = ("Which of E3, E5 and E7 fits each holder: copies to drop, people to train, moves down.", "Cost savings"),
        ["/licensing/reservation-holders"] = ("The people behind each organization's allocated Copilot licenses.", "Cost savings"),
        ["/licensing/leavers"] = ("Licenses on accounts that are disabled or have left: nobody can use them.", "Policy breaches"),
        ["/licensing/non-person"] = ("Copilot licenses on service, shared, test and other accounts that are not a person.", "Policy breaches"),
        ["/licensing/departures"] = ("License holders due to leave: reclaim each license on the last day.", "Changes ahead"),

        ["/security"] = ("Jailbreak attempts, prompt injection and blocked resources: how many, when, and in which units.", null),
        ["/security/guests"] = ("Copilot use by guest accounts, and the organizations they come from.", null),
        ["/security/flags"] = ("Each flagged interaction, who and where, with its triage.", null),

        ["/compliance"] = ("How often Copilot works with labeled content, which labels, and what was blocked.", null),
        ["/compliance/units"] = ("Which parts of the organization have Copilot working with labeled content.", null),
        ["/compliance/sites"] = ("The sites Copilot read from most: where to check permissions first.", null),
        ["/compliance/access"] = ("Who worked with labeled content through Copilot, and the most sensitive label.", null),
        ["/compliance/person"] = ("Everything recorded about one person's Copilot use, for an access request.", null),

        ["/agents/mine"] = ("The agents you own: who uses them, whether they come back, and the ones nobody used.", null),
        ["/agents"] = ("Every agent people used: how many people, how often, and which are new.", null),
        ["/agents/inventory"] = ("Every current agent from the agent workbook, its owner and use; unused ones flagged.", null),
        ["/agents/owners"] = ("Agents whose owner left, is disabled or is not set, and agents by owner's department.", null),
        ["/agents/checks"] = ("Agents whose settings disagree with their use: quarantined, web search, production.", null),
        ["/agents/children"] = ("Child agents: the parent agents that call each one, and their use.", null),
        ["/agents/build"] = ("Copilot Studio agents created, published, shared, changed and deleted.", null),
        ["/agents/detail"] = ("One agent: who uses it, where, and how that is changing.", null),
        ["/agents/handoffs"] = ("When one agent calls another: how often, and to which agents.", null),
        ["/agents/credits"] = ("What each agent's use by people without a license costs in credits a month.", null),

        ["/enablement"] = ("How deeply people use Copilot: habit, conversation and breadth across apps.", null),
        ["/enablement/depth"] = ("How far people take a conversation: prompts per session and session length.", null),
        ["/enablement/training"] = ("Each training session: attendees' use before and after, against their colleagues.", null),
        ["/enablement/champions"] = ("The most regular and broad users in each department, as champion candidates.", null),
        ["/enablement/people"] = ("Named people by engagement: power users, and those to invite to training.", null),

        ["/forecasting"] = ("Where use and licenses are heading, from the last year of weekly history.", "Forecast and budget"),
        ["/forecasting/optimize"] = ("What to do with licenses: reclaim, reassign, coach or buy, and what each is worth.", "Forecast and budget"),
        ["/forecasting/budget"] = ("The Copilot contracts, their cost, renewal dates, and the seats to renew.", "Forecast and budget"),
        ["/forecasting/spend-limits"] = ("Every spending limit on AI beside its spend: which will be reached, and next quarter's.", "Forecast and budget"),
        ["/forecasting/credit-commitment"] = ("A prepaid Copilot Credit commitment: drawn, left, and when it runs out.", "Forecast and budget"),
        ["/forecasting/actions"] = ("Named people to upgrade to a license, to train, or to take a license back from.", "Actions"),
        ["/forecasting/waves"] = ("Everyone without a license, in the order to license them, split into waves.", "Actions"),
        ["/forecasting/outcomes"] = ("Each license export from the dashboard, and whether the change happened.", "Actions"),

        ["/regions"] = ("Adoption by country, city and office: where Copilot has landed.", null),
        ["/regions/patterns"] = ("When people use Copilot, by hour and weekday, and how much is out of hours.", null),

        ["/workforce"] = ("Adoption across employees and contractors, managers, job titles and levels.", null),
        ["/workforce/managers"] = ("Do teams follow their manager? Adoption where the manager uses Copilot or not.", null),
        ["/workforce/families"] = ("Copilot use by job family, for comparing like with like.", null),

        ["/apps"] = ("Copilot in each Microsoft 365 app: how many people, how deeply, and whether they return.", null),
        ["/apps/detail"] = ("One app: who uses Copilot in it, what they read, and which agents run there.", null),

        ["/governance"] = ("Which models answer, web search, memory updates and deferred data-loss checks.", null),
        ["/governance/ai-apps"] = ("Calls to Azure AI Foundry and Azure OpenAI from the organization's own apps.", null),
        ["/governance/agent-policies"] = ("Nightly rules over the agents, and the actions they propose: notify, quarantine, a new owner.", null),
        ["/platforms/foundry"] = ("What Foundry and Azure OpenAI cost, what is idle, and what retires soon.", null),
        ["/platforms/foundry/deployments"] = ("Every model deployment: use, last used, reserved capacity, retirement and filter.", null),
        ["/platforms/foundry/posture"] = ("Each Foundry account's network, sign-in, logging, filters and owner.", null),
        ["/platforms/github"] = ("GitHub Copilot's seats, their use, what they cost, and who holds both Copilots.", null),
        ["/platforms/github/seats"] = ("Every GitHub Copilot seat and its person: idle, never used, or held by a leaver.", null),
        ["/platforms/github/posture"] = ("Each organization's GitHub Copilot settings beside the recommended ones.", null),
        ["/governance/ai-apps/resource"] = ("One AI resource: the apps that call it, its failures, models, tokens and cost.", null),

        ["/admin/health"] = ("Is the data fresh and complete? Pipeline runs, the directory, and a manual refresh.", null),
        ["/admin/access"] = ("Who sees what: the top of the organization, levels, and roles.", null),
        ["/admin/profiles"] = ("Named bundles of roles, granted to groups as one, and where to ask for each.", null),
        ["/admin/directory"] = ("The gaps in Active Directory that weaken the reports and who sees what.", null),
        ["/admin/email"] = ("How the dashboard sends alerts and digests.", null),
        ["/admin/notifications"] = ("Who hears about what, when to raise an alert, and the scheduled digests.", null),
        ["/admin/assumptions"] = ("Adoption targets, minutes saved per interaction, costs, and the licensing group.", null),
        ["/admin/privacy"] = ("Privacy mode, how long logs and stored records are kept, and the data API.", null),
        ["/admin/reference"] = ("Agent names and owners, label names, chat apps, and accounts that are not a person.", null),
        ["/admin/values"] = ("Readable names for the codes the audit log records.", null),
        ["/admin/classifications"] = ("Job-family rules, use-case rules, and the events drawn on trend charts.", null),
        ["/admin/reports"] = ("Turn any report off for everyone, or back on.", null),
        ["/admin/extracts"] = ("CSV files of aggregate figures written to a folder on a schedule.", null),
        ["/admin/training"] = ("Who attended which Copilot training, loaded from a CSV.", null),
        ["/admin/audit"] = ("Every page view, refusal, export and admin change.", null),
    };
}
