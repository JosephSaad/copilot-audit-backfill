namespace CopilotInsights.Data;

/// <summary>
/// Agent 365 license sizing (3.5.0): who must, who likely and who is only recommended to hold an Agent 365 license,
/// and what each option costs. Agent 365 is licensed per user, not per agent. Microsoft requires a license for the
/// owners, sponsors or managers of agents with their own access and for users who delegate their access to agents,
/// and recommends one for everyone who interacts with a managed agent. Pure, for the page and the tests.
/// </summary>
public static class AgentLicensing
{
    /// <summary>The list price a user a month when none is set (Agent365MonthlyPrice).</summary>
    public const decimal DefaultPrice = 15m;

    /// <summary>The tiers, narrowest first. A person is counted in the narrowest that holds them.</summary>
    public const int Owner = 1, Delegating = 2, User = 3;

    public static string TierName(int tier) => tier switch
    {
        Owner => "Must: owners of agents",
        Delegating => "Likely: people whose agents act with their access",
        _ => "Recommended: everyone else using agents",
    };

    /// <summary>One department's people in one tier, and how many of them already hold Agent 365 inside a suite (E7).</summary>
    public sealed record Group(string Department, int Tier, bool InSuite, int People);

    /// <summary>An option: license every tier up to and including <paramref name="Tier"/>.</summary>
    public sealed record Option(int Tier, string Name, int People, int Covered, decimal Month, decimal Year);

    /// <summary>
    /// The three options: owners only, owners and delegating users, everyone using agents. People already holding
    /// Agent 365 inside a suite are counted apart and not priced again.
    /// </summary>
    public static IReadOnlyList<Option> Options(IEnumerable<Group> groups, decimal price)
    {
        var all = groups.ToList();
        return [.. new[] { Owner, Delegating, User }.Select(tier =>
        {
            var upTo = all.Where(g => g.Tier <= tier).ToList();
            var pay = upTo.Where(g => !g.InSuite).Sum(g => g.People);
            return new Option(tier, tier switch { Owner => "Owners only", Delegating => "Owners and delegating users", _ => "Everyone using agents" },
                pay, upTo.Where(g => g.InSuite).Sum(g => g.People), pay * price, pay * price * 12);
        })];
    }

    /// <summary>What an option saves a year against licensing everyone using agents.</summary>
    public static decimal SavingAYear(IReadOnlyList<Option> options, int tier) =>
        options.Single(o => o.Tier == User).Year - options.Single(o => o.Tier == tier).Year;
}
