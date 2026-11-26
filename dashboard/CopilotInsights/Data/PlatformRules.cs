namespace CopilotInsights.Data;

/// <summary>
/// The rules the AI platforms pages share (Foundry and GitHub Copilot): which deployment is idle, retiring or under-used,
/// which posture check an account fails, which GitHub seat is idle, and the GitHub settings Microsoft and GitHub
/// recommend. Pure, for the tests; the pages read the tables the loaders fill.
/// </summary>
public static class PlatformRules
{
    /// <summary>The filters of the Foundry deployments list, as the overview's figures open it.</summary>
    public const string Idle = "idle", Retiring = "retiring", PtuLow = "ptu-low";

    /// <summary>A deployment no request reached in the last <paramref name="idleDays"/> days before the newest day loaded.</summary>
    public static bool IsIdle(DateTime? lastUsed, DateTime newestDay, int idleDays) =>
        lastUsed is null || lastUsed.Value.Date <= newestDay.Date.AddDays(-idleDays);

    /// <summary>A deployment whose model (or its SKU) retires within <paramref name="warnDays"/> days of today, or already has.</summary>
    public static bool IsRetiring(DateTime? retires, DateTime today, int warnDays) =>
        retires is { } r && r.Date <= today.Date.AddDays(warnDays);

    /// <summary>A provisioned deployment using less of its capacity than <paramref name="lowPercent"/> on average.</summary>
    public static bool IsPtuLow(string? sku, double? averagePercent, int lowPercent) =>
        IsProvisioned(sku) && (averagePercent ?? 0) < lowPercent;

    public static bool IsProvisioned(string? sku) => sku is not null && sku.StartsWith("Provisioned", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The model a cost meter belongs to ("gpt-4o-mini Input Tokens" is gpt-4o-mini, not gpt-4o): the longest model name
    /// the meter starts with, followed by a space or the end; null for a meter of no model (storage, networking).
    /// </summary>
    public static string? ModelOfMeter(string meter, IEnumerable<string> models) =>
        models.Where(m => m.Length > 0 && meter.StartsWith(m, StringComparison.OrdinalIgnoreCase) && (meter.Length == m.Length || meter[m.Length] == ' '))
              .OrderByDescending(m => m.Length).FirstOrDefault();

    /// <summary>One posture check of a Foundry account: its name, whether it passes, and what the page says of it.</summary>
    public sealed record Check(string Name, bool? Passes, string Text);

    /// <summary>
    /// The posture checks of a Foundry account, in the page's order: reachable from the internet, keys allowed, logging,
    /// content filters looser than the default, an owner tag. Passes is null where the load could not read it.
    /// </summary>
    public static IReadOnlyList<Check> Posture(string? publicNetwork, string? defaultAction, int? privateEndpoints, bool? keysRefused,
        int? diagnosticSettings, int relaxedFilters, string? owner)
    {
        var open = string.Equals(publicNetwork, "Enabled", StringComparison.OrdinalIgnoreCase) && !string.Equals(defaultAction, "Deny", StringComparison.OrdinalIgnoreCase);
        return
        [
            new("Network", publicNetwork is null ? null : !open,
                open ? "Reachable from any network: limit it to known networks or a private endpoint (Networking, in the account)."
                     : (privateEndpoints ?? 0) > 0 ? $"Closed to the internet; {privateEndpoints} private endpoint{(privateEndpoints == 1 ? "" : "s")}." : "Closed to networks not listed."),
            new("Sign-in", keysRefused,
                keysRefused is null ? "Not known." : keysRefused.Value ? "Keys refused: Entra sign-in only, so each call has an identity." : "Keys allowed: a call with a key carries no identity. Turn keys off (disableLocalAuth) once apps use Entra."),
            new("Logging", diagnosticSettings is null ? null : diagnosticSettings > 0,
                diagnosticSettings is null ? "Not read: the app needs Monitoring Reader." : diagnosticSettings > 0 ? "Logs sent somewhere (diagnostic settings)." : "No diagnostic setting: requests are not logged. Add one sending the audit category to Log Analytics."),
            new("Content filters", relaxedFilters == 0,
                relaxedFilters == 0 ? "At Microsoft's default or stricter." : $"{relaxedFilters} filter{(relaxedFilters == 1 ? " is" : "s are")} looser than Microsoft's default: check it was approved."),
            new("Owner", !string.IsNullOrWhiteSpace(owner),
                string.IsNullOrWhiteSpace(owner) ? "No owner tag: tag the account with its owner, so its cost and its calls have someone to answer for them." : $"Owner: {owner}."),
        ];
    }

    /// <summary>A GitHub Copilot seat with no activity in the last <paramref name="idleDays"/> days, or never used since it was given that long ago: as Costs and savings counts it.</summary>
    public static bool IsIdleSeat(DateTime? lastActivityUtc, DateTime? createdUtc, DateTime nowUtc, int idleDays) =>
        lastActivityUtc is null ? createdUtc is null || createdUtc < nowUtc.AddDays(-idleDays) : lastActivityUtc < nowUtc.AddDays(-idleDays);

    /// <summary>The setting GitHub recommends for an enterprise, where there is one; null for a setting that is a choice.</summary>
    public static string? Recommended(string policy) => policy switch
    {
        "public_code_suggestions" => "block",
        "seat_management_setting" => "assign_selected",
        _ => null,
    };

    /// <summary>The setting in words, for the posture page.</summary>
    public static string PolicyName(string policy) => policy switch
    {
        "public_code_suggestions" => "Suggestions matching public code",
        "ide_chat" => "Copilot Chat in the editor",
        "platform_chat" => "Copilot Chat on github.com",
        "cli" => "Copilot in the command line",
        "seat_management_setting" => "How seats are given",
        "plan_type" => "Plan",
        _ => policy.Replace('_', ' '),
    };
}
