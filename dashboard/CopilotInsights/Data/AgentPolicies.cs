using CopilotInsights.Infrastructure;

namespace CopilotInsights.Data;

/// <summary>
/// Agent policies: rules over what the dashboard knows of each Copilot Studio agent (dash.AgentPolicyRule), checked
/// each night, each match a proposed action in dash.AgentAction (notify its owner, quarantine it, give an orphaned
/// agent to its former owner's manager). A person approves or rejects each one, unless the rule is set to approve its
/// own; Notify is emailed by the dashboard, the rest are carried out by tools\Invoke-AgentActions.ps1, only when
/// AgentActionsEnabled is 1. Nothing here deletes an agent.
/// </summary>
public sealed class AgentPolicies(Db db, CreditData credits)
{
    public const string NoOwner = "NoOwner", Unused = "Unused", CostlyFewUsers = "CostlyFewUsers", SharedNotCertified = "SharedNotCertified";
    public const string Notify = "Notify", Quarantine = "Quarantine", Release = "Release", Reassign = "Reassign";

    /// <summary>A rejected or dismissed proposal is not made again for this many days.</summary>
    public const int QuietDays = 30;

    public sealed class Rule
    {
        public int RuleId { get; set; }
        public string RuleKind { get; set; } = "";
        public int? Days { get; set; }
        public int? Credits { get; set; }
        public int? People { get; set; }
        public string ProposedAction { get; set; } = Notify;
        public bool Enabled { get; set; }
        public bool AutoApprove { get; set; }
    }

    public static string RuleText(Rule r) => r.RuleKind switch
    {
        NoOwner => $"No owner with an enabled account, published or created over {r.Days ?? 30} days ago",
        Unused => $"Not used for {r.Days ?? 90} days, and not autonomous (no triggers or flows)",
        CostlyFewUsers => $"Over {r.Credits ?? 10000:#,0} Copilot Credits a month, used by fewer than {r.People ?? 5} people",
        SharedNotCertified => $"Shared with others, not listed as in production, created over {r.Days ?? 30} days ago",
        _ => r.RuleKind,
    };

    public static string ActionText(string kind) => kind switch
    {
        Notify => "Notify its owner", Quarantine => "Quarantine it", Release => "Release it from quarantine", Reassign => "Give it to its former owner's manager", _ => kind,
    };

    /// <summary>Only Notify and Quarantine may approve themselves: a person undoes either. A new owner is always a person's decision.</summary>
    public static bool MayAutoApprove(string action) => action is Notify or Quarantine;

    public async Task<bool> ReadyAsync() =>
        await db.ScalarAsync<int>("SELECT CASE WHEN OBJECT_ID(N'dash.AgentPolicyRule', N'U') IS NULL OR OBJECT_ID(N'dash.AgentAction', N'U') IS NULL THEN 0 ELSE 1 END;") == 1;

    public Task<List<Rule>> RulesAsync() => db.QueryAsync<Rule>("SELECT RuleId, RuleKind, Days, Credits, People, ProposedAction, Enabled, AutoApprove FROM dash.AgentPolicyRule ORDER BY RuleId;");

    sealed record Hit(string InventoryAgentId, string? AgentName, string? Environment, string Reason, string? NewOwnerUpn);

    /// <summary>
    /// Checks every rule turned on and proposes what each matches, once: not while the agent has an open proposal of the
    /// same action, and not within <see cref="QuietDays"/> of one that was rejected or dismissed. Returns how many were proposed.
    /// </summary>
    public async Task<int> ProposeAsync(ReportContext c)
    {
        var rules = (await RulesAsync()).Where(r => r.Enabled).ToList();
        if (rules.Count == 0 || await db.ScalarAsync<int>("SELECT CASE WHEN EXISTS (SELECT 1 FROM dash.AgentInventory) THEN 1 ELSE 0 END;") == 0) return 0;
        var made = 0;
        foreach (var r in rules)
        {
            var hits = r.RuleKind switch
            {
                NoOwner => await db.QueryAsync<Hit>(@"
SELECT i.InventoryAgentId, i.AgentName, i.Environment,
       CASE WHEN i.OwnerUpn IS NULL THEN N'It has no owner.' ELSE CONCAT(N'Its owner, ', i.OwnerUpn, N', is disabled or has left.') END AS Reason,
       m.Upn AS NewOwnerUpn
FROM dash.AgentInventory AS i
LEFT JOIN dash.Person AS o ON o.Upn = i.OwnerUpn
LEFT JOIN dash.Person AS m ON m.Upn = o.ManagerUpn AND m.AccountEnabled = 1
WHERE ISNULL(i.IsQuarantined, 0) = 0 AND (o.Upn IS NULL OR o.AccountEnabled = 0)
  AND ISNULL(i.LastPublishedAt, i.CreatedAt) <= DATEADD(DAY, -@days, SYSUTCDATETIME());", new { days = r.Days ?? 30 }),
                Unused => await db.QueryAsync<Hit>(@"
SELECT i.InventoryAgentId, i.AgentName, i.Environment,
       CASE WHEN i.AgentKey IS NULL THEN N'The audit log has no use of it.' WHEN u.LastDateKey IS NULL THEN N'Nobody has used it.' ELSE CONCAT(N'Last used on ', CONVERT(CHAR(10), d.[Date], 120), N'.') END AS Reason, CAST(NULL AS NVARCHAR(256)) AS NewOwnerUpn
FROM dash.AgentInventory AS i
LEFT JOIN dash.AgentUse AS u ON u.AgentKey = i.AgentKey
LEFT JOIN bi.DimDate AS d ON d.DateKey = u.LastDateKey
WHERE ISNULL(i.IsQuarantined, 0) = 0 AND i.IsAutonomous = 0
  AND ISNULL(i.CreatedAt, '19000101') <= DATEADD(DAY, -@days, SYSUTCDATETIME())
  AND (u.LastDateKey IS NULL OR d.[Date] <= DATEADD(DAY, -@days, CAST(SYSUTCDATETIME() AS DATE)));", new { days = r.Days ?? 90 }),
                SharedNotCertified => await db.QueryAsync<Hit>(@"
SELECT i.InventoryAgentId, i.AgentName, i.Environment, CONCAT(N'Shared with ', ISNULL(i.ViewerUsers, 0), N' people and ', ISNULL(i.ViewerGroups, 0), N' groups, and not in production.') AS Reason, CAST(NULL AS NVARCHAR(256)) AS NewOwnerUpn
FROM dash.AgentInventory AS i
WHERE ISNULL(i.IsQuarantined, 0) = 0 AND i.IsShared = 1 AND i.InProduction = 0
  AND ISNULL(i.CreatedAt, '19000101') <= DATEADD(DAY, -@days, SYSUTCDATETIME());", new { days = r.Days ?? 30 }),
                CostlyFewUsers => await CostlyAsync(r, c),
                _ => [],
            };
            foreach (var h in hits)
            {
                /* A new owner needs someone to give it to: without the former owner's manager, the owner's team is told instead. */
                var action = r.ProposedAction == Reassign && h.NewOwnerUpn is null ? Notify : r.ProposedAction;
                var auto = r.AutoApprove && MayAutoApprove(action);
                made += await db.ExecuteAsync(@"
INSERT INTO dash.AgentAction (InventoryAgentId, AgentName, Environment, RuleId, ActionKind, Reason, NewOwnerUpn, Status, ProposedBy, DecidedBy, DecidedUtc)
SELECT @id, @name, @env, @rule, @action, @reason, @owner, CASE WHEN @auto = 1 THEN 'Approved' ELSE 'Proposed' END, N'rule',
       CASE WHEN @auto = 1 THEN N'rule' END, CASE WHEN @auto = 1 THEN SYSUTCDATETIME() END
WHERE NOT EXISTS (SELECT 1 FROM dash.AgentAction AS a WHERE a.InventoryAgentId = @id AND a.ActionKind = @action
                  AND (a.Status IN ('Proposed', 'Approved') OR a.Status IN ('Rejected', 'Dismissed') AND a.DecidedUtc > DATEADD(DAY, -@quiet, SYSUTCDATETIME())));",
                    new { id = h.InventoryAgentId, name = h.AgentName, env = h.Environment, rule = r.RuleId, action, reason = h.Reason, owner = action == Reassign ? h.NewOwnerUpn : null, auto = auto ? 1 : 0, quiet = QuietDays });
            }
        }
        return made;
    }

    /* Credit use's estimate of each agent's credits a month, and its people, matched to the agent workbook. */
    async Task<List<Hit>> CostlyAsync(Rule r, ReportContext c)
    {
        var view = await credits.BuildAsync(c);
        var costly = view.Agents.Where(a => a.Month.Credits > (r.Credits ?? 10000) && a.People < (r.People ?? 5)).ToList();
        if (costly.Count == 0) return [];
        var keys = costly.Select(a => a.AgentKey).ToList();
        var inv = await db.QueryAsync<(int AgentKey, string Id, string? Name, string? Env)>(
            "SELECT AgentKey, InventoryAgentId, AgentName, Environment FROM dash.AgentInventory WHERE AgentKey IN @keys AND ISNULL(IsQuarantined, 0) = 0;", new { keys });
        return [.. inv.Select(i => { var a = costly.First(x => x.AgentKey == i.AgentKey);
            return new Hit(i.Id, i.Name, i.Env, $"About {a.Month.Credits:#,0} Copilot Credits a month, used by {a.People} {(a.People == 1 ? "person" : "people")}.", null); })];
    }

    public sealed class ActionRow
    {
        public int ActionId { get; set; }
        public string InventoryAgentId { get; set; } = "";
        public string? AgentName { get; set; }
        public string? Environment { get; set; }
        public string? Owner { get; set; }
        public int? RuleId { get; set; }
        public string ActionKind { get; set; } = "";
        public string? Reason { get; set; }
        public string? NewOwnerUpn { get; set; }
        public string Status { get; set; } = "";
        public DateTime ProposedUtc { get; set; }
        public string? ProposedBy { get; set; }
        public string? DecidedBy { get; set; }
        public DateTime? DecidedUtc { get; set; }
        public DateTime? DoneUtc { get; set; }
        public string? Result { get; set; }
        public DateTime? CheckedUtc { get; set; }
    }

    public Task<List<ActionRow>> ActionsAsync(bool open, int take = 500) => db.QueryAsync<ActionRow>($@"
SELECT TOP (@take) a.ActionId, a.InventoryAgentId, a.AgentName, a.Environment, i.OwnerUpn AS Owner, a.RuleId, a.ActionKind, a.Reason, a.NewOwnerUpn, a.Status,
       a.ProposedUtc, a.ProposedBy, a.DecidedBy, a.DecidedUtc, a.DoneUtc, a.Result, a.CheckedUtc
FROM dash.AgentAction AS a LEFT JOIN dash.AgentInventory AS i ON i.InventoryAgentId = a.InventoryAgentId
WHERE {(open ? "a.Status IN ('Proposed', 'Approved')" : "a.Status NOT IN ('Proposed', 'Approved')")}
ORDER BY {(open ? "CASE a.Status WHEN 'Proposed' THEN 0 ELSE 1 END, a.ProposedUtc" : "ISNULL(a.DoneUtc, ISNULL(a.DecidedUtc, a.ProposedUtc)) DESC")};", new { take });
}
