using System.Globalization;
using CopilotInsights.Data;
using CopilotInsights.Infrastructure;

namespace CopilotInsights.Pages.Licensing;

/// <summary>
/// Agent 365 sizing (3.5.0): how many people must, likely and only by Microsoft's recommendation hold an Agent 365
/// license, from who owns, delegates to and uses each agent, and what each option costs a month and a year
/// (<see cref="AgentLicensing"/>). People who hold Agent 365 inside a suite (Microsoft 365 E7) are counted apart.
/// Counts for everyone the Licensing section is granted to; the owners by name for the Licensing role.
/// </summary>
public sealed class Agent365Model(PageServices s) : InsightsPage(s)
{
    public override string Title => "Agent 365 sizing";
    public override string Persona => "Licensing";
    public override string Intro => "How many Agent 365 licenses the organization needs, from who owns, delegates to and uses each agent, and what each way of buying costs. "
        + "Agent 365 is licensed per user: Microsoft requires it for the owners of agents and for people who let agents act with their access, and recommends it for everyone who uses a managed agent.";

    protected override async Task BuildAsync()
    {
        var price = decimal.TryParse(await S.Settings.StringAsync("Agent365MonthlyPrice"), NumberStyles.Number, CultureInfo.InvariantCulture, out var p) && p >= 0 ? p : AgentLicensing.DefaultPrice;
        var cur = await S.Settings.StringAsync("LicenseCurrency") ?? "USD";
        /* Each person once, in the narrowest tier that holds them: owners of agents (enabled accounts in scope), people
           who used an agent that signs its users in with their own account, and everyone with an interaction through
           an agent in the period. From the totals the refresh keeps per person and per agent. */
        var groups = (await Db.QueryAsync<(string Department, int Tier, bool InSuite, int People)>($@"
WITH rec AS (SELECT p.UserKey FROM {Ctx.PerPerson()} AS p {Ctx.Scope("p.UserKey")} {ReportContext.EnabledJoin("p.UserKey")} WHERE p.AgentInteractions > 0),
del AS (SELECT DISTINCT u.UserKey FROM {Ctx.PerItem('G')} AS u {Ctx.Scope("u.UserKey")} {ReportContext.EnabledJoin("u.UserKey")}
        JOIN dash.AgentInventory AS ai ON ai.AgentKey = u.Item
        WHERE u.Interactions > 0 AND ai.Authentication LIKE N'Authenticate with Microsoft%'),
own AS (SELECT p.Upn FROM dash.Person AS p {Ctx.PersonScope("p.Upn")}
        WHERE p.AccountEnabled = 1 AND p.Upn IN (SELECT a.Owner FROM dash.Agent AS a WHERE a.AgentSource <> N'(no agent)' AND a.Owner IS NOT NULL
                                                 UNION SELECT i.OwnerUpn FROM dash.AgentInventory AS i WHERE i.OwnerUpn IS NOT NULL)),
keyed AS (SELECT du.UserPrincipalName AS Upn, {AgentLicensing.User} AS Tier FROM rec JOIN bi.DimUser AS du ON du.UserKey = rec.UserKey
          UNION ALL SELECT du.UserPrincipalName, {AgentLicensing.Delegating} FROM del JOIN bi.DimUser AS du ON du.UserKey = del.UserKey
          UNION ALL SELECT own.Upn, {AgentLicensing.Owner} FROM own),
people AS (SELECT Upn, MIN(Tier) AS Tier FROM keyed GROUP BY Upn),
suite AS (SELECT DISTINCT Upn FROM dash.CopilotSuiteHolder),
marked AS (SELECT ISNULL(NULLIF(pe.Department, N''), N'(no department)') AS Department, pp.Tier, CAST(CASE WHEN su.Upn IS NULL THEN 0 ELSE 1 END AS BIT) AS InSuite
           FROM people AS pp LEFT JOIN dash.Person AS pe ON pe.Upn = pp.Upn LEFT JOIN suite AS su ON su.Upn = pp.Upn)
SELECT Department, Tier, InSuite, COUNT(*) FROM marked GROUP BY Department, Tier, InSuite;", Ctx.Args()))
            .Select(g => new AgentLicensing.Group(g.Department, g.Tier, g.InSuite, g.People)).ToList();
        if (groups.Count == 0)
        {
            Notice = "Nobody owns or used an agent in the period here, so there is nothing to size. Agent inventory lists the agents the workbook knows.";
            return;
        }
        var options = AgentLicensing.Options(groups, price);
        string M(decimal v) => CostRules.Money(Math.Round(v, 0), cur);
        var owners = options[0]; var likely = options[1]; var everyone = options[2];
        var covered = groups.Where(g => g.InSuite).Sum(g => g.People);

        Kpis.Add(new("Must: owners of agents", Num(owners.People), $"{M(owners.Year)} a year at {M(price)} a user a month", "accent"));
        Kpis.Add(new("Likely: with delegating users", Num(likely.People), $"{M(likely.Year)} a year: owners, and people whose agents act with their access"));
        Kpis.Add(new("Recommended: everyone using agents", Num(everyone.People), $"{M(everyone.Year)} a year: Microsoft's broadest recommendation"));
        Kpis.Add(new($"Saved by licensing who must, and likely ({cur})", M(AgentLicensing.SavingAYear(options, AgentLicensing.Delegating)) + " a year",
            $"against everyone using agents; {M(AgentLicensing.SavingAYear(options, AgentLicensing.Owner))} a year with owners only", "good"));
        if (covered > 0)
            Kpis.Add(new("Already licensed in a suite", Num(covered), "hold Microsoft 365 E7, which includes Agent 365: not priced again"));

        var ot = new Table { Headers = ["Option", "People to license", "Already in a suite", $"A month ({cur})", $"A year ({cur})", $"Saved a year against everyone ({cur})"], Numeric = new HashSet<int> { 1, 2, 3, 4, 5 }, Sortable = false };
        foreach (var o in options)
            ot.Rows.Add([o.Name, o.People, o.Covered, Math.Round(o.Month), Math.Round(o.Year), Math.Round(AgentLicensing.SavingAYear(options, o.Tier))]);
        Panels.Add(new("Three ways to buy", ot.Render(),
            "Owners: the agent workbook's owner, else Admin › Reference data's, else the audit log's; an autonomous agent's owner stands for its sponsor. Delegating: people who used an agent that signs its users in with their own Microsoft account, so it acts with their access (an assumption: Microsoft's account team confirms who must be licensed for your agents). " +
            "Everyone: people with an interaction through an agent in the period. Enabled accounts only; each person once, in the narrowest tier.", "full"));

        var dt = new Table { Headers = ["Department", "Owners", "Delegating", "Everyone else", "All"], Numeric = new HashSet<int> { 1, 2, 3, 4 }, EmptyText = "No department large enough to show." };
        foreach (var d in groups.GroupBy(g => g.Department).Select(g => (Name: g.Key, O: g.Where(x => x.Tier == AgentLicensing.Owner).Sum(x => x.People),
                     D: g.Where(x => x.Tier == AgentLicensing.Delegating).Sum(x => x.People), U: g.Where(x => x.Tier == AgentLicensing.User).Sum(x => x.People)))
                 .Where(d => AtLeastMin(d.O + d.D + d.U) is not null).OrderByDescending(d => d.O + d.D + d.U))
            dt.Rows.Add([d.Name, d.O, d.D, d.U, d.O + d.D + d.U]);
        CsvTable = dt;
        Panels.Add(new("By department", dt.Render(), $"The people of each department in each tier; departments under {Ctx.MinGroupSize} people are left out.", "full", CsvUrl()));

        /* The owners by name: the people who must be licensed, for the Licensing role. */
        if (Viewer.Has(Roles.Licensing) && !PrivacyMode)
        {
            var list = await Db.QueryAsync<(string Upn, string? Name, string? Department, int Agents, bool InSuite)>($@"
WITH o AS (SELECT a.Owner AS Upn, a.AgentKey AS K FROM dash.Agent AS a WHERE a.AgentSource <> N'(no agent)' AND a.Owner IS NOT NULL
           UNION SELECT i.OwnerUpn, ISNULL(i.AgentKey, -1) FROM dash.AgentInventory AS i WHERE i.OwnerUpn IS NOT NULL)
SELECT TOP (500) p.Upn, p.DisplayName, p.Department, COUNT(DISTINCT o.K),
       CAST(CASE WHEN EXISTS (SELECT 1 FROM dash.CopilotSuiteHolder AS csh WHERE csh.Upn = p.Upn) THEN 1 ELSE 0 END AS BIT)
FROM o JOIN dash.Person AS p ON p.Upn = o.Upn {Ctx.PersonScope("p.Upn")}
WHERE p.AccountEnabled = 1
GROUP BY p.Upn, p.DisplayName, p.Department ORDER BY COUNT(DISTINCT o.K) DESC, p.DisplayName;", Ctx.Args());
            var lt = new Table { Headers = ["Owner", "Sign-in", "Department", "Agents", "Agent 365 in a suite"], Numeric = new HashSet<int> { 3 }, EmptyText = "No agent has an owner here." };
            foreach (var o in list) lt.Rows.Add([o.Name ?? o.Upn, o.Upn, o.Department, o.Agents, o.InSuite ? "Yes" : "No"]);
            Panels.Add(new("Owners to license", lt.Render(), "Each owner of an agent and how many they own (the first 500), with whether they already hold Agent 365 in Microsoft 365 E7. My agents shows each one's agents.", "full"));
        }
    }
}
