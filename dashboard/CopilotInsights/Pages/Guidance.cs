namespace CopilotInsights.Pages;

/// <summary>
/// What each report is for and what to do with it: two short lines under the title, written for the
/// section's audience (NavGroup.Who). The longer explanations sit behind information icons. A test checks
/// every report in the navigation has both lines.
/// </summary>
public static class Guidance
{
    public sealed record Line(string Helps, string Do);

    public static Line? For(string? path) => path is not null && All.TryGetValue(path.TrimEnd('/') is { Length: > 0 } p ? p : "/", out var g) ? g : null;

    public static readonly IReadOnlyDictionary<string, Line> All = new Dictionary<string, Line>(StringComparer.OrdinalIgnoreCase)
    {
        // Leadership
        ["/leadership"] = new("Answer \"is Copilot working here?\" in one screen.", "Look at the units with low use, and ask their leads what is in the way."),
        ["/leadership/act"] = new("See what needs doing without opening every report.", "Take the largest count first, and open the report beside it for the detail."),
        ["/leadership/org"] = new("Compare the parts of the organization side by side.", "Open the weakest unit to find the team behind it."),
        ["/leadership/compare"] = new("Set two managers' teams side by side, whatever their size.", "Ask the lead of the team ahead what they did, and share it with the other."),
        ["/leadership/mix"] = new("See where Copilot earns its place: which apps, and how much through agents.", "Back the apps and agents that carry use; question the ones that do not."),
        ["/leadership/value"] = new("Put an estimated value on Copilot, with your own assumptions.", "Check the minutes and the hourly cost in Admin before you quote the figure."),
        ["/leadership/costs"] = new("See what AI and automation cost, what can be saved, and what they return, in one view.", "Take the top three savings this month; send the growth items to their owners."),
        ["/leadership/credit-chargeback"] = new("See each line of business's Copilot Credits by path, and what they charge it.", "Route every environment first; then send each line of business its month."),
        ["/leadership/reservations"] = new("See reserved Copilot seats against licenses held, and what each part of the organization is billed.", "Clear the exceptions first; use unused reservations before any purchase."),
        ["/leadership/usecases"] = new("Know what people use Copilot for, not just how much.", "Share the common use cases as examples; fund training for the missing ones."),
        ["/leadership/boardpack"] = new("Report the program to the board on one page.", "Print it or save it as a PDF for the steering group."),
        ["/leadership/assumptions"] = new("Know what every cost and saving rests on, and where to change it.", "Settle any prices that disagree before you quote a figure."),
        ["/group"] = new("See every tenant in the group together.", "Compare the tenants, and share what works in the strongest."),

        // Adoption
        ["/adoption"] = new("Track the path from license to habit.", "Work on the step where most people drop out."),
        ["/adoption/by-license"] = new("See who of each license type uses Copilot, in which apps, for how long and on how many days.", "Follow up with the license holders at the bottom; learn from the apps and habits of the ones at the top."),
        ["/adoption/retention"] = new("Tell a habit from a trial.", "Follow up with the groups whose use falls away after the first weeks."),
        ["/adoption/cohorts"] = new("See whether each month's new licenses turn into lasting use.", "Compare a weak month with a strong one, and repeat what the strong one had."),
        ["/adoption/departments"] = new("Find the departments ahead and behind.", "Pair a department behind with one ahead, through their champions."),
        ["/adoption/targets"] = new("Know whether the program is on track for its goals.", "For a target behind its line, decide the push now, not at the deadline."),
        ["/adoption/onboarding"] = new("Make sure new license holders and new hires start quickly.", "Welcome each department's new joiners in their first week."),
        ["/adoption/anomalies"] = new("Catch outages, policy changes and launches from their effect on use.", "Check a sudden drop with IT; learn from a spike."),
        ["/adoption/not-started"] = new("Find the people who were given a license and never used it.", "Invite them to a session, or hand the license to someone who will use it."),

        // Licensing
        ["/licensing"] = new("Know how many licenses you have, and how many are in use.", "Compare the unused count with next quarter's demand before buying more."),
        ["/licensing/unused"] = new("Find licenses nobody is using.", "Follow up or reclaim; export the list for your license team."),
        ["/licensing/ranking"] = new("See who gets the most from Premium, and who uses only what is free.", "Move licenses from the bottom of the Premium ranking to the top of the free one."),
        ["/licensing/payg"] = new("Spot license holders that Copilot Credits would serve for less.", "Check their Premium use, then move them to pay-as-you-go."),
        ["/licensing/swap"] = new("Reassign idle licenses instead of buying new ones.", "Apply the take-back file, then the give-out file."),
        ["/licensing/license-for-credits"] = new("Spot people whose Copilot Credits cost more than a license.", "License them: the cost falls and they gain Copilot in the apps."),
        ["/licensing/fading"] = new("Catch license holders drifting away before the license goes idle.", "Ask their managers to check in, or offer a refresher session."),
        ["/licensing/demand"] = new("See who already relies on the free Copilot Chat.", "License these people first when seats come free."),
        ["/licensing/changes"] = new("Audit license assignments and removals.", "Look into licenses removed while still in use, and ones that came back."),
        ["/licensing/pool"] = new("Know the seats you pay for, assigned or not.", "Assign or give up seats bought but not assigned before the renewal."),
        ["/licensing/chargeback"] = new("Allocate license cost to the budgets that hold the licenses.", "Send each group its cost and its share in use."),
        ["/licensing/leavers"] = new("Find licenses nobody can use: disabled or deleted accounts.", "Reclaim them all: export the removal file."),
        ["/licensing/non-person"] = new("Find Copilot licenses on accounts that are not a person: a policy breach, and money spent on no one.", "Remove them with the file here, or have an administrator confirm the account is a person."),
        ["/licensing/overlap"] = new("Find people paying for two licenses that overlap.", "Drop the second license where the first covers the use."),
        ["/licensing/credit-use"] = new("Estimate each person's Copilot Credits and tokens, and what a cap would touch.", "Set the credit policy's cap from the spread; license the people above a license's worth."),
        ["/licensing/suites"] = new("See which of E3, E5 and E7 fits each holder, and what a move down saves.", "Drop the copies now; agree each move down with the manager, and with security for E5."),
        ["/licensing/reservation-holders"] = new("See who holds the reserved licenses of an organization or line of business.", "Map anyone under no leader or no line of business; reclaim what is unused before reserving more."),
        ["/licensing/departures"] = new("Reclaim licenses on the last day, not weeks after.", "Plan the leavers' removals; export the ones past their date now."),

        // Security
        ["/security"] = new("Watch jailbreak attempts, prompt injection and blocked resources.", "Look into a rise, and adjust policy where it clusters."),
        ["/security/guests"] = new("Watch how guest accounts use Copilot.", "Check the home organizations with signals, and review their access."),
        ["/security/flags"] = new("Triage each flagged interaction.", "Mark each one investigating, closed or a false positive."),

        // Compliance
        ["/compliance"] = new("See how much Copilot works with sensitive content, and what it is blocked from.", "Review the labels and sites behind the most use."),
        ["/compliance/units"] = new("See which parts of the organization reach labeled content through Copilot.", "Review access in the units with the most labeled content."),
        ["/compliance/sites"] = new("Find the sites where oversharing reaches the most people.", "Tighten permissions or add labels on the widest-reaching sites first."),
        ["/compliance/access"] = new("See who works with labeled content through Copilot.", "Check that each person's access to the most sensitive labels is expected."),
        ["/compliance/person"] = new("Answer a data-subject request or support an investigation.", "Download the record; every look is audited."),

        // Agents
        ["/agents"] = new("See which agents people use and come back to.", "Promote the agents with returning users; review the ones nobody returns to."),
        ["/agents/inventory"] = new("Keep a register of every current agent, and whether anyone uses it.", "Retire or promote the agents nobody used in the period."),
        ["/agents/owners"] = new("Find the agents whose owner has left or is disabled.", "Give the used ones a new owner first; ask the old owner's manager."),
        ["/agents/checks"] = new("See where an agent's settings and its use disagree.", "Act on each breach; review the rest with the agent's owner."),
        ["/agents/children"] = new("See which parent agents call each child agent, and their use.", "Keep the child agents that busy parents rely on owned and supported."),
        ["/agents/build"] = new("See how much building goes on, and by whom.", "Support the active makers; clean up agents nobody publishes."),
        ["/agents/detail"] = new("Understand one agent's reach and trend.", "Share the agent more widely, or retire it."),
        ["/agents/handoffs"] = new("See how agents chain together.", "Check that the agents called are owned and supported."),
        ["/agents/credits"] = new("See what each agent costs in Copilot Credits.", "Restrict or retire costly agents with few users, or license their heavy users."),

        // Enablement
        ["/enablement"] = new("Aim training where use stays shallow.", "Plan sessions for the groups with the most one-off use."),
        ["/enablement/depth"] = new("See whether people hold a conversation with Copilot or ask one-off questions.", "Teach follow-up prompting where sessions stay short."),
        ["/enablement/training"] = new("Measure whether training changed use.", "Repeat the sessions that worked; rework the ones that did not."),
        ["/enablement/champions"] = new("Find the people best placed to be champions.", "Ask the top candidates in each department to join the network."),
        ["/enablement/people"] = new("Know who to recruit and who to invite.", "Recruit power users as champions; invite one-off users to training."),

        // Forecasting
        ["/forecasting"] = new("See where use and licenses are heading.", "Plan purchases and renewals on the projection, with its range."),
        ["/forecasting/optimize"] = new("Weigh every license action and what it is worth.", "Start with the action worth the most."),
        ["/forecasting/credit-commitment"] = new("Know how much of the credit commitment is left, and when it runs out.", "Act before it runs out early or is left unused: move work onto it, or plan the renewal smaller."),
        ["/forecasting/budget"] = new("Prepare the renewal with the seats to renew.", "Renew at the seats to renew, License optimization's right size, not the current count."),
        ["/forecasting/actions"] = new("Act on named upgrade, train and revoke recommendations.", "Export each list and hand it to the license team."),
        ["/forecasting/waves"] = new("Roll out new licenses in the order most likely to pay off.", "Apply the next wave's file when seats come free."),
        ["/forecasting/outcomes"] = new("Check that license actions happened, and that they paid off.", "Chase the changes not made, and the new licenses not used."),

        // Regions
        ["/regions"] = new("See where Copilot has landed, by country and office.", "Send support to the offices with low adoption."),
        ["/regions/patterns"] = new("See when people use Copilot, and how much after hours.", "Plan support hours; watch after-hours use for workload."),

        // People
        ["/workforce"] = new("Compare adoption across employee groups and levels.", "Close gaps between comparable groups."),
        ["/workforce/managers"] = new("See whether teams follow their manager's lead.", "Enlist managers who do not use Copilot yet."),
        ["/workforce/families"] = new("Compare like with like, by job family.", "Share what works in a family's strongest department with the rest."),

        // App owners
        ["/apps"] = new("See how Copilot does in each Microsoft 365 app.", "Focus on apps with many users but shallow or fading use."),
        ["/apps/detail"] = new("Understand one app's Copilot use in depth.", "Share the app's strongest examples with its weaker groups."),

        // Governance
        ["/governance"] = new("See which models answer and how much Copilot relies on the web.", "Check the share of web search against your policy."),
        ["/platforms/foundry"] = new("See what Foundry costs, and what is paid for but idle, under-used or retiring.", "Remove idle deployments, resize reserved capacity, and move off models before they retire."),
        ["/platforms/foundry/deployments"] = new("See every deployment with its use, its last day used, and when its model retires.", "Ask each owner about idle deployments and plan the move off retiring models."),
        ["/platforms/foundry/posture"] = new("See which accounts are open to any network, allow keys, do not log, or have no owner.", "Close the network, turn keys off once apps use Entra, add logging, and tag an owner."),
        ["/platforms/github"] = new("See how many GitHub Copilot seats are used, idle or never used, and what they cost.", "Take back idle seats, and look at who uses both Copilots before renewing either."),
        ["/platforms/github/seats"] = new("See every seat and its person, and the seats held by leavers or nobody matched.", "Remove seats from leavers first, then idle ones; match the logins nobody claims."),
        ["/platforms/github/posture"] = new("See each organization's Copilot settings beside GitHub's recommendation.", "Block suggestions matching public code, and settle settings that differ between organizations."),
        ["/governance/ai-apps/resource"] = new("See which apps call one AI resource, how often they fail, and what it costs.", "Ask the owner of an app that fails often, or calls with no person's identity, what changed."),
        ["/governance/ai-apps"] = new("See which of the organization's own AI apps are used, by whom, and in which environment.", "Ask the owners of production resources with no user identity how their callers are governed."),
    };
}
