namespace CopilotInsights.Data;

/// <summary>
/// Levels 1 to N of the organization (the top, those who report to the top, and so on: dash.Person.Level) are never
/// on a list to revoke, reclaim, move to pay-as-you-go, train or coach (2.4.6). N is the ProtectedLevels setting
/// (Admin > Access), 5 unless set; 0 protects nobody. Read in SQL, so every page, export, count, digest and API call
/// that names or counts those lists applies it the same way. Lists of accounts nobody can use (disabled, departed,
/// leaving, non-human) are not recommendations and keep everyone.
/// </summary>
public static class ProtectedLevels
{
    public const string Setting = "ProtectedLevels";
    public const int Default = 5;

    /// <summary>A predicate: the person signed in as <paramref name="upn"/> (a SQL expression) is not at a protected level.</summary>
    public static string Not(string upn) =>
        $"NOT EXISTS (SELECT 1 FROM dash.Person AS pl WHERE pl.Upn = {upn} AND pl.Level BETWEEN 1 AND " +
        $"ISNULL((SELECT TRY_CAST(ps.Value AS INT) FROM dash.Setting AS ps WHERE ps.Name = N'{Setting}'), {Default}))";

    /// <summary>
    /// The same rule as a join, for a query that counts inside an aggregate, where SQL Server allows no subquery (2.4.7):
    /// LEFT JOIN this, and a person is not protected when <paramref name="alias"/>.Upn IS NULL.
    /// </summary>
    public static string Join(string alias, string upn) =>
        $"LEFT JOIN dash.Person AS {alias} ON {alias}.Upn = {upn} AND {alias}.Level BETWEEN 1 AND " +
        $"ISNULL((SELECT TRY_CAST(ps.Value AS INT) FROM dash.Setting AS ps WHERE ps.Name = N'{Setting}'), {Default})";

    /// <summary>What a list says about who it leaves out.</summary>
    public static async Task<string> NoteAsync(Infrastructure.Settings settings)
    {
        var n = await LevelsAsync(settings);
        return n <= 0 ? "" : n == 1 ? " The top of the organization is never listed (Admin > Access)." : $" Levels 1 to {n} of the organization are never listed (Admin > Access).";
    }

    public static async Task<int> LevelsAsync(Infrastructure.Settings settings) =>
        int.TryParse(await settings.StringAsync(Setting), out var n) ? Math.Clamp(n, 0, 15) : Default;
}
