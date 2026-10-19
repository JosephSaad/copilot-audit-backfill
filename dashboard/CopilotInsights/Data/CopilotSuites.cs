namespace CopilotInsights.Data;

/// <summary>
/// Microsoft 365 Copilot that comes inside a suite (2.6.6: Microsoft 365 E7, marked audit.LicenseSku.CopilotInSuite by
/// the license lookup). Its holders are licensed, but the Copilot cannot be taken off alone and is paid for in the
/// suite's price, so it is never on a list to revoke and never priced as an add-on. Someone who holds the add-on as
/// well holds it twice: the add-on is the one to take back. Read in SQL from the view dash.CopilotSuiteOnly, which the
/// schema scripts keep empty until the license pool exists, as <see cref="ProtectedLevels"/> is read.
/// </summary>
public static class CopilotSuites
{
    /// <summary>A predicate: the person signed in as <paramref name="upn"/> (a SQL expression) holds Copilot only through a suite.</summary>
    public static string OnlyInSuite(string upn) => $"EXISTS (SELECT 1 FROM dash.CopilotSuiteOnly AS cso WHERE cso.Upn = {upn})";

    /// <summary>The opposite: no suite, or an add-on beside it that can be taken back.</summary>
    public static string NotOnlyInSuite(string upn) => $"NOT {OnlyInSuite(upn)}";
}
