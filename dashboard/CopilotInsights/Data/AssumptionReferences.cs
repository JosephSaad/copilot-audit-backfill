namespace CopilotInsights.Data;

/// <summary>Where a default comes from: a published figure, one a published figure informs, or this product's own estimate.</summary>
public enum ReferenceBasis { Published, Informed, Estimate }

/// <summary>
/// Why an assumption's default is what it is (3.1.3): the reasoning in a sentence or two, and the public page it rests
/// on, so a reader can check it and an administrator can decide whether it fits their organization. A default with no
/// published figure behind it says so (Estimate), with the nearest thing that informs it.
/// </summary>
public sealed record AssumptionReference(string Why, ReferenceBasis Basis, string? SourceTitle = null, string? SourceUrl = null);

public static class AssumptionReferences
{
    const string WorkLab = "https://www.microsoft.com/en-us/worklab/how-we-measure-the-value-of-ai-at-work";
    const string CopilotDashboard = "https://learn.microsoft.com/en-us/viva/insights/org-team-insights/copilot-dashboard";
    const string CopilotStudioRates = "https://learn.microsoft.com/en-us/microsoft-copilot-studio/requirements-messages-management";
    const string CopilotStudioLicensing = "https://www.microsoft.com/licensing/guidance/microsoft-copilot-studio";

    /// <summary>By the assumption's key in <see cref="Assumptions.Settings"/>.</summary>
    public static readonly IReadOnlyDictionary<string, AssumptionReference> ByKey = new Dictionary<string, AssumptionReference>
    {
        ["LicenseMonthlyCost"] = new("Microsoft's list price for the Microsoft 365 Copilot add-on, 30 USD a user a month on an annual commitment. Set it to what your agreement pays.",
            ReferenceBasis.Published, "Microsoft 365 Copilot pricing", "https://www.microsoft.com/en-us/microsoft-365-copilot/pricing"),
        ["LicenseCurrency"] = new("Microsoft's list prices are published in US dollars.", ReferenceBasis.Published),
        ["PaygInteractionCost"] = new("What an interaction billed through Copilot Credits costs: a generative answer is 2 credits and grounding in work data 10, at 0.01 USD a credit, so 0.02 to 0.12. The default takes the grounded case.",
            ReferenceBasis.Informed, "Copilot Studio billing rates", CopilotStudioRates),
        ["InactiveDays"] = new("Microsoft's Copilot dashboard counts a licensed person with no Copilot activity in the last 28 days as inactive, and measures active users over 28 days. The same window here keeps \"unused\" and adoption on one clock.",
            ReferenceBasis.Published, "Microsoft Copilot dashboard (Viva Insights)", CopilotDashboard),
        ["SpareSeatPercent"] = new("Seats kept free for joiners and moves. No published figure: 10% is this product's estimate; set it to your joiner rate over a buying cycle.",
            ReferenceBasis.Estimate),
        ["CostIdleDays"] = new("GitHub's guidance on inactive Copilot seats works from each seat's last activity and uses 30 days in its example. Microsoft publishes no idle rule for Power Platform licenses.",
            ReferenceBasis.Informed, "GitHub: remind inactive Copilot users", "https://docs.github.com/en/copilot/tutorials/roll-out-at-scale/assign-licenses/remind-inactive-users"),
        ["MinGroupSize"] = new("Microsoft's Copilot dashboard shows a group's figures only when it has at least 10 people by default, and lets an administrator lower that to 5 at the least, so no figure points to one person.",
            ReferenceBasis.Published, "Copilot dashboard settings (Viva Insights)", "https://learn.microsoft.com/en-us/viva/insights/advanced/admin/manage-settings-copilot-dashboard"),
        ["MinutesSavedDefault"] = new("Microsoft counts 6 minutes of Copilot-assisted time for a search for information and 6 for creating content (a summarized hour-long meeting counts as an hour). Early users reported saving 14 minutes a day. An estimate of time assisted, not a measurement of time saved: replace it with your own survey or pilot.",
            ReferenceBasis.Published, "Microsoft WorkLab: how we measure the value of AI at work", WorkLab),
        ["AppMinutes"] = new("The minutes one interaction saves in each app, where your organization has measured it. Without one, the default above applies.",
            ReferenceBasis.Informed, "Microsoft WorkLab: how we measure the value of AI at work", WorkLab),
        ["HourlyCost"] = new("Microsoft's Copilot impact report values an hour at 72 USD by default, from US Bureau of Labor Statistics estimates. BLS puts the total compensation of all private-industry workers at 46.89 USD an hour (June 2026). Use your own loaded cost.",
            ReferenceBasis.Published, "Microsoft 365 Copilot impact report (Viva Insights)", "https://learn.microsoft.com/en-us/viva/insights/advanced/analyst/templates/microsoft-365-copilot-impact"),
        ["FlowMinutesPerRun"] = new("No published figure per flow run: 3 minutes is this product's estimate of a short manual task a run replaces. Set a flow's own figure where it is known.",
            ReferenceBasis.Estimate, "Forrester: Total Economic Impact of Power Automate", "https://tei.forrester.com/go/microsoft/powerautomatetei"),
        ["PowerBiCopilotMinutes"] = new("No published figure for Copilot in Power BI: 3 minutes is this product's estimate, half the 6 minutes Microsoft counts for a Copilot search or create action.",
            ReferenceBasis.Estimate, "Microsoft WorkLab: how we measure the value of AI at work", WorkLab),
        ["FabricReservedDiscount"] = new("Microsoft states a one-year Fabric capacity reservation saves about 41% (40.5%) against pay-as-you-go.",
            ReferenceBasis.Published, "Microsoft Fabric pricing", "https://azure.microsoft.com/en-us/pricing/details/microsoft-fabric/"),
        ["CreditsPerAnswer"] = new("Microsoft's rate for a generative answer is 2 Copilot Credits. Agents used in Microsoft 365 apps by someone with a Microsoft 365 Copilot license are not billed.",
            ReferenceBasis.Published, "Copilot Studio billing rates", CopilotStudioRates),
        ["CreditsPerGrounding"] = new("Microsoft's rate for grounding an answer in the tenant's Microsoft Graph data is 10 Copilot Credits.",
            ReferenceBasis.Published, "Copilot Studio billing rates", CopilotStudioRates),
        ["CreditsPerAction"] = new("Microsoft's rate for an agent action is 5 Copilot Credits.",
            ReferenceBasis.Published, "Copilot Studio billing rates", CopilotStudioRates),
        ["TokensPerTurn"] = new("Microsoft reports no tokens for Microsoft 365 Copilot. 1,500 is this product's estimate of instructions, prompt and history in one turn; a token is about 4 characters or three quarters of a word of English.",
            ReferenceBasis.Estimate, "OpenAI: what are tokens", "https://help.openai.com/en/articles/4936856-what-are-tokens-and-how-to-count-them"),
        ["TokensPerResource"] = new("No published figure: 1,000 tokens (about 750 words) is this product's estimate of what one file, mail or page read for grounding adds.",
            ReferenceBasis.Estimate, "OpenAI: what are tokens", "https://help.openai.com/en/articles/4936856-what-are-tokens-and-how-to-count-them"),
        ["TokensPerAnswer"] = new("No published figure: 400 tokens (about 300 words) is this product's estimate of one answer.",
            ReferenceBasis.Estimate, "OpenAI: what are tokens", "https://help.openai.com/en/articles/4936856-what-are-tokens-and-how-to-count-them"),
        ["ReservationGatePercent"] = new("Your organization's purchase policy: no more licenses are bought until this share of the baseline is assigned. Not a Microsoft figure.",
            ReferenceBasis.Estimate),
        ["ReservationGateBaseline"] = new("Your organization's purchase policy: the unassigned licenses the gate counts from. Not a Microsoft figure.", ReferenceBasis.Estimate),
        ["ReservationRate"] = new("The rate your organization charges lines of business for a reserved license, set from your agreement.", ReferenceBasis.Estimate),
    };

    /// <summary>The price list's rows, by the area they are in (dash.CostPrice.Area): Microsoft's list price pages.</summary>
    public static readonly IReadOnlyDictionary<string, AssumptionReference> ByPriceArea = new Dictionary<string, AssumptionReference>(StringComparer.OrdinalIgnoreCase)
    {
        ["Power Automate"] = new("Microsoft's list prices: Power Automate Premium 15 USD a user a month, Process 150 USD a bot a month.",
            ReferenceBasis.Published, "Power Automate pricing", "https://www.microsoft.com/en-us/power-platform/products/power-automate/pricing"),
        ["Power Apps"] = new("Microsoft's list prices: Power Apps Premium 20 USD a user a month; per app 10 USD an active user an app a month, pay-as-you-go (the 5 USD per-app plan is no longer sold).",
            ReferenceBasis.Published, "Power Apps pricing", "https://www.microsoft.com/en-us/power-platform/products/power-apps/pricing"),
        ["Power BI"] = new("Microsoft's list prices: Power BI Pro 14 USD and Premium Per User 24 USD a user a month.",
            ReferenceBasis.Published, "Power BI pricing", "https://www.microsoft.com/en-us/power-platform/products/power-bi/pricing"),
        ["Copilot Studio"] = new("Microsoft's list prices: 0.01 USD a Copilot Credit pay-as-you-go, or 200 USD a month for a pack of 25,000 credits.",
            ReferenceBasis.Published, "Copilot Studio licensing", CopilotStudioLicensing),
        ["Dataverse"] = new("Microsoft's list prices for capacity over the entitlement: database 40 USD a GB a month; file 2 and log 10 USD a GB a month, from the Power Platform licensing guide.",
            ReferenceBasis.Published, "Power Apps pricing (Dataverse capacity)", "https://www.microsoft.com/en-us/power-platform/products/power-apps/pricing"),
        ["GitHub Copilot"] = new("GitHub's list prices: Copilot Business 19 USD and Enterprise 39 USD a user a month (Enterprise needs GitHub Enterprise Cloud).",
            ReferenceBasis.Published, "GitHub Copilot plans", "https://github.com/features/copilot/plans"),
    };

    /// <summary>The suites' prices: Microsoft 365 enterprise plans.</summary>
    public static readonly AssumptionReference Suites = new("Microsoft's list prices for the Microsoft 365 enterprise plans, a user a month on an annual commitment. Set them to what your agreement pays.",
        ReferenceBasis.Published, "Microsoft 365 enterprise plans", "https://www.microsoft.com/en-us/microsoft-365/enterprise/microsoft365-plans-and-pricing");

    /// <summary>The reference for an assumption, or null when there is none.</summary>
    public static AssumptionReference? For(AssumptionDef d) => d.Kind switch
    {
        AssumptionKind.Price => d.UsedBy is { } u && u.IndexOf('(') is var i and >= 0 && ByPriceArea.TryGetValue(u[(i + 1)..].TrimEnd(')'), out var p) ? p : null,
        AssumptionKind.Suite => Suites,
        _ => ByKey.GetValueOrDefault(d.Key),
    };

    public static string BasisLabel(ReferenceBasis b) => b switch
    {
        ReferenceBasis.Published => "Published figure",
        ReferenceBasis.Informed => "Informed by a published figure",
        _ => "Estimate: no published figure",
    };
}
