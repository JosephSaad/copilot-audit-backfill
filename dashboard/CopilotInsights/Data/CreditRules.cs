namespace CopilotInsights.Data;

/// <summary>
/// The rates an estimate of Copilot Credits is made with (2.6.6), from Admin › Targets and assumptions: credits for a
/// generative answer, for grounding in the organization's data (Microsoft Graph), and for an agent action (here, one
/// agent calling another); the price of a credit; and, for the rough token figure, the tokens a turn takes before any
/// grounding, each resource read adds, and an answer gives back.
/// </summary>
public sealed record CreditRates(decimal Generative = 2, decimal Graph = 10, decimal Action = 5, decimal CreditPrice = 0.01m,
    int TokensPerTurn = 1500, int TokensPerResource = 1000, int TokensPerAnswer = 400);

/// <summary>
/// One person's (or one agent's) Copilot use in a window: interactions in all; with an agent; billed in credits (an
/// agent used without a Microsoft 365 Copilot license, or in a Copilot Studio channel, where a license does not
/// zero-rate it); of those, grounded in work data, and calling another agent; and the resources read and answers given.
/// </summary>
public sealed record CreditUse(long Interactions, long WithAgent, long Billed, long BilledWork, long BilledActions, long Resources, long Answers);

/// <summary>An estimate: credits and their cost, and tokens, for the window the use covers.</summary>
public sealed record CreditEstimate(decimal Credits, decimal Cost, long Tokens);

/// <summary>
/// Estimates Copilot Credits and tokens from the audit log (2.6.6). Microsoft reports neither per person: credits are
/// billed per response by kind, and tokens are not shown for Microsoft 365 Copilot or its agents at all. So:
/// <list type="bullet">
/// <item>Credits: each billed interaction is a generative answer, plus Graph grounding where it read work data, plus an
///   agent action where it called another agent. Unbilled: a licensed person's agent use inside Microsoft 365 Copilot
///   (zero-rated), and Copilot Chat without an agent (free, web-grounded).</item>
/// <item>Tokens: every interaction, billed or not, at a turn's tokens, a resource's and an answer's: a rough figure for
///   sizing, never a measurement.</item>
/// </list>
/// It errs high on credits (a classic, scripted answer costs less) and is only as good as the rates set.
/// </summary>
public static class CreditRules
{
    public static CreditEstimate Estimate(CreditUse u, CreditRates r)
    {
        var credits = u.Billed * r.Generative + u.BilledWork * r.Graph + u.BilledActions * r.Action;
        var tokens = u.Interactions * (long)r.TokensPerTurn + u.Resources * r.TokensPerResource + Math.Max(u.Answers, u.Interactions) * r.TokensPerAnswer;
        return new(credits, Math.Round(credits * r.CreditPrice, 2), tokens);
    }

    /// <summary>The credit estimate in words, with the rates in it (2.6.9), for a figure's "how it is worked out".</summary>
    public static string Formula(CreditRates r, string cur) =>
        $"Credits = billed interactions × {r.Generative:0.##} (a generative answer) + those grounded in work data × {r.Graph:0.##} + those calling another agent × {r.Action:0.##}; " +
        $"cost = credits × {r.CreditPrice:0.####} {cur}; the period brought to 30 days. Billed: an agent used without a Microsoft 365 Copilot license, or in a Copilot Studio channel.";

    /// <summary>The token estimate in words, with the sizes in it (2.6.9).</summary>
    public static string TokenFormula(CreditRates r) =>
        $"Tokens = every interaction × {r.TokensPerTurn:#,0} + each resource read × {r.TokensPerResource:#,0} + each answer × {r.TokensPerAnswer:#,0}; the period brought to 30 days. A rough figure for sizing: Microsoft reports no tokens for Microsoft 365 Copilot.";

    /// <summary>A window's estimate brought to 30 days.</summary>
    public static CreditEstimate Monthly(CreditEstimate e, int days) =>
        days <= 0 ? new(0, 0, 0) : new(Math.Round(e.Credits * 30m / days, 1), Math.Round(e.Cost * 30m / days, 2), (long)Math.Round(e.Tokens * 30.0 / days));

    /// <summary>
    /// A cap a person a month, for a credit policy: who would reach it, and the credits above it, which the cap would
    /// refuse (or a pack or a license would have to cover).
    /// </summary>
    public static (int People, decimal Above) Cap(IEnumerable<decimal> monthlyCredits, decimal cap)
    {
        if (cap <= 0) return (0, 0);
        var over = monthlyCredits.Where(c => c > cap).ToList();
        return (over.Count, over.Sum(c => c - cap));
    }

    /// <summary>A short form of a token count: 1.2M, 340K.</summary>
    public static string Tokens(long t) => t >= 1_000_000_000 ? $"{t / 1e9:0.#}B" : t >= 1_000_000 ? $"{t / 1e6:0.#}M" : t >= 10_000 ? $"{t / 1e3:0}K" : t.ToString("#,0");
}
