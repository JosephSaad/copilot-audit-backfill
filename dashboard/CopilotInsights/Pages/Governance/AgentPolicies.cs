using System.Globalization;
using CopilotInsights.Data;
using CopilotInsights.Infrastructure;
using Microsoft.AspNetCore.Html;
using Microsoft.AspNetCore.Mvc;

namespace CopilotInsights.Pages.Governance;

/// <summary>
/// Agent policies: the rules checked each night over the Copilot Studio agents, what they propose, and what was
/// decided and done (<see cref="AgentPolicies"/>). Administrators set the rules and decide; nothing deletes an agent.
/// </summary>
public sealed class AgentPoliciesModel(PageServices s, AgentPolicies data, OpsReports reports) : InsightsPage(s)
{
    public override string Title => "Agent policies";
    public override string Persona => "Governance";
    public override bool UsesTeam => false;
    public override bool CountsWholeOrganization => true;
    public override string? OwnWindow => "today";
    public override string Intro => "Rules over the organization's Copilot Studio agents, checked every night: no owner, unused, costly for few people, shared and not certified. " +
        "Each match is a proposal an administrator approves or rejects: tell the owner, quarantine the agent, or give an orphaned agent to its former owner's manager. Nothing here deletes an agent.";

    protected override async Task BuildAsync()
    {
        ShowDone();
        if (!await data.ReadyAsync())
        {
            Notice = await NotSetUpAsync("Agent policies cannot be kept here yet", "3.5.0", "dash.AgentPolicyRule", "dash.AgentAction");
            return;
        }
        var rules = await data.RulesAsync();
        var open = await data.ActionsAsync(open: true);
        var history = await data.ActionsAsync(open: false, take: 200);
        var on = await S.Settings.StringAsync("AgentActionsEnabled") == "1";
        var names = Viewer.Has(Roles.Agents) && !PrivacyMode;

        Kpis.Add(new("Rules turned on", Num(rules.Count(r => r.Enabled)), $"of {rules.Count}", rules.Any(r => r.Enabled) ? "accent" : null));
        Kpis.Add(new("Waiting for a decision", Num(open.Count(a => a.Status == "Proposed")), "proposals an administrator approves or rejects", open.Any(a => a.Status == "Proposed") ? "warn" : null));
        Kpis.Add(new("Approved, waiting to run", Num(open.Count(a => a.Status == "Approved")),
            on ? "carried out tonight by tools\\Invoke-AgentActions.ps1" : "actions in Microsoft 365 are off: only Notify is sent"));
        var month = DateTime.UtcNow.AddDays(-30);
        Kpis.Add(new("Done in the last 30 days", Num(history.Count(a => a.Status == "Done" && a.DoneUtc >= month)),
            $"{history.Count(a => a.Status == "Failed" && a.DoneUtc >= month):#,0} failed", history.Any(a => a.Status == "Failed" && a.DoneUtc >= month) ? "warn" : "good"));

        Panels.Add(new("Actions in Microsoft 365", new HtmlString(on
            ? "<p class=\"res-state\" role=\"status\"><b>On.</b> Approved quarantines, releases and new owners are carried out each night through the Power Platform API, with the agent actions app's own sign-in, and checked the night after.</p>"
            : "<p class=\"res-state\" role=\"status\"><b>Off.</b> Proposals are made and decided here, and owners are emailed, but nothing is changed in Microsoft 365. An administrator turns it on with the setting AgentActionsEnabled and the agent actions app (docs\\OPERATIONS.md, Agent policies).</p>"),
            "The first part of the product that changes Microsoft 365, so it is off until it is set up and turned on. Quarantine is undone by a release; a new owner is a person's decision, never automatic.", "full"));

        var ot = new Table { Headers = ["Agent", "Environment", "Owner", "Rule", "Proposed", "Why", "Status", ""], EmptyText = "Nothing is proposed." };
        foreach (var a in open)
            ot.Rows.Add([a.AgentName ?? a.InventoryAgentId, a.Environment, names ? a.Owner ?? "(none)" : "", RuleName(rules, a.RuleId),
                AgentPolicies.ActionText(a.ActionKind) + (a.NewOwnerUpn is { } n && names ? $": {n}" : ""), a.Reason,
                a.Status == "Approved" ? $"Approved by {a.DecidedBy}" : "Proposed " + a.ProposedUtc.ToString("d MMM", CultureInfo.InvariantCulture),
                Viewer.IsAdmin ? new HtmlString(a.Status == "Proposed"
                    ? InlineForm("decide", Hidden("id", a.ActionId) + Hidden("decision", "Approved") + Button("Approve", $"Approve: {AgentPolicies.ActionText(a.ActionKind)} for {a.AgentName}?", quiet: true)) +
                      InlineForm("decide", Hidden("id", a.ActionId) + Hidden("decision", "Rejected") + Button("Reject", quiet: true))
                    : InlineForm("decide", Hidden("id", a.ActionId) + Hidden("decision", "Dismissed") + Button("Withdraw", quiet: true))) : (object)""]);
        Panels.Add(new("Proposals", ot.Render(),
            "What the rules found last night, and what an administrator approved that has not run yet. A rejected proposal is not made again for 30 days.", "full"));

        var ht = new Table { Headers = ["Agent", "Action", "Status", "Decided by", "Done", "Result", "Checked"], EmptyText = "Nothing has been decided yet." };
        foreach (var a in history)
            ht.Rows.Add([a.AgentName ?? a.InventoryAgentId, AgentPolicies.ActionText(a.ActionKind), a.Status, a.DecidedBy, a.DoneUtc, a.Result, a.CheckedUtc]);
        CsvTable = ht;
        Panels.Add(new("What was decided and done", ht.Render(), "The last 200, newest first: every decision and every call is also in the audit log.", "full", CsvUrl()));

        var rt = new Table { Headers = ["Rule", "Proposes", "On", "Approves itself", ""] };
        foreach (var r in rules)
            rt.Rows.Add([AgentPolicies.RuleText(r), AgentPolicies.ActionText(r.ProposedAction), r.Enabled ? "Yes" : "No", r.AutoApprove ? "Yes" : "No",
                Viewer.IsAdmin ? new HtmlString(InlineForm("rule", Hidden("id", r.RuleId) +
                    (r.RuleKind is AgentPolicies.CostlyFewUsers
                        ? Input("credits", "Credits a month over", r.Credits?.ToString(CultureInfo.InvariantCulture), 6, "number") + Input("people", "Fewer people than", r.People?.ToString(CultureInfo.InvariantCulture), 4, "number")
                        : Input("days", "Days", r.Days?.ToString(CultureInfo.InvariantCulture), 4, "number")) +
                    Select("action", "Proposes", (r.RuleKind == AgentPolicies.NoOwner ? new[] { AgentPolicies.Notify, AgentPolicies.Quarantine, AgentPolicies.Reassign } : [AgentPolicies.Notify, AgentPolicies.Quarantine])
                        .Select(x => (x, AgentPolicies.ActionText(x))), r.ProposedAction) +
                    Select("enabled", "On", new[] { ("1", "On"), ("0", "Off") }, r.Enabled ? "1" : "0") +
                    Select("auto", "Approves itself", new[] { ("0", "No"), ("1", "Yes (Notify and Quarantine only)") }, r.AutoApprove ? "1" : "0") + Button("Save", quiet: true))) : (object)""]);
        Panels.Add(new("Rules", new HtmlString(rt.Render().Value + (Viewer.IsAdmin ? Form("check", Button("Check the rules now", "Check every rule turned on now and propose what they find?")) : "")),
            "Checked each night after the refresh. No owner: the agent workbook's owner is disabled, gone or not set. Unused: no interaction in the days set (autonomous agents are left out, they work unseen). " +
            "Costly for few people: Credit use's estimate of the agent's credits a month. Shared and not certified: shared with people or groups and not on the workbook's production list.", "full"));
    }

    static string RuleName(IReadOnlyList<AgentPolicies.Rule> rules, int? id) =>
        id is null ? "Proposed by a person" : rules.FirstOrDefault(r => r.RuleId == id) is { } r ? AgentPolicies.RuleText(r) : "A rule since removed";

    public Task<IActionResult> OnPostDecideAsync(int id, string? decision) => AdminChangeAsync(async () =>
    {
        if (decision is not ("Approved" or "Rejected" or "Dismissed")) throw new ArgumentException("approve, reject or withdraw.");
        var from = decision == "Dismissed" ? "Approved" : "Proposed";
        var n = await Db.ExecuteAsync("UPDATE dash.AgentAction SET Status = @decision, DecidedBy = @by, DecidedUtc = SYSUTCDATETIME() WHERE ActionId = @id AND Status = @from;",
            new { id, decision, from, by = Viewer.Upn ?? Viewer.WindowsName });
        if (n == 0) throw new ArgumentException("that proposal was decided already, or has run.");
        var name = await Db.ScalarAsync<string?>("SELECT CONCAT(ISNULL(AgentName, InventoryAgentId), N': ', ActionKind) FROM dash.AgentAction WHERE ActionId = @id;", new { id });
        return $"{name}: {decision.ToLowerInvariant()}.";
    });

    public Task<IActionResult> OnPostRuleAsync(int id, int? days, int? credits, int? people, string? action, string? enabled, string? auto) => AdminChangeAsync(async () =>
    {
        var kind = await Db.ScalarAsync<string?>("SELECT RuleKind FROM dash.AgentPolicyRule WHERE RuleId = @id;", new { id }) ?? throw new ArgumentException("that rule is gone.");
        if (days is < 1 or > 730) throw new ArgumentException("days are 1 to 730.");
        if (credits is < 1 || people is < 1) throw new ArgumentException("credits and people are above 0.");
        if (action is not (AgentPolicies.Notify or AgentPolicies.Quarantine or AgentPolicies.Reassign) || action == AgentPolicies.Reassign && kind != AgentPolicies.NoOwner)
            throw new ArgumentException("a new owner is proposed only for an agent with no owner.");
        var self = auto == "1" && AgentPolicies.MayAutoApprove(action);
        if (auto == "1" && !self) throw new ArgumentException("a new owner is always a person's decision: it cannot approve itself.");
        await Db.ExecuteAsync(@"UPDATE dash.AgentPolicyRule SET Days = ISNULL(@days, Days), Credits = ISNULL(@credits, Credits), People = ISNULL(@people, People),
    ProposedAction = @action, Enabled = @on, AutoApprove = @self, UpdatedBy = @by, UpdatedUtc = SYSUTCDATETIME() WHERE RuleId = @id;",
            new { id, days, credits, people, action, on = enabled == "1", self, by = Viewer.Upn ?? Viewer.WindowsName });
        return $"Rule saved: {kind}, {(enabled == "1" ? "on" : "off")}, proposes {AgentPolicies.ActionText(action).ToLowerInvariant()}{(self ? ", approving itself" : "")}.";
    });

    public Task<IActionResult> OnPostCheckAsync() => AdminChangeAsync(false, async () =>
    {
        var n = await data.ProposeAsync(await reports.ContextAsync(30, null));
        return n == 0 ? "The rules were checked: nothing new to propose." : $"The rules were checked: {n:#,0} new proposal{(n == 1 ? "" : "s")}.";
    });
}
