namespace CopilotInsights.Data;

/// <summary>
/// Levels 1 to N of the organization (the top, those who report to the top, and so on: dash.Person.Level) are never
/// on a list to revoke, reclaim, move to pay-as-you-go, train or coach. N is the ProtectedLevels setting
/// (Admin › Access), 5 unless set; 0 protects nobody. Read in SQL, so every page, export, count, digest and API call
/// that names or counts those lists applies it the same way. Lists of accounts nobody can use (disabled, departed,
/// leaving, non-human) are not recommendations and keep everyone.
/// </summary>
public static class ProtectedLevels
{
    public const string Setting = "ProtectedLevels";
    public const int Default = 5;

    /// <summary>A predicate: the person signed in as <paramref name="upn"/> (a SQL expression) is not at a protected level.</summary>
    /// <param name="levels">The setting as <see cref="SqlLevelsAsync"/> reads it, when the caller has it: written into the
    /// statement as a number. Read in the statement instead, the setting was looked up once per row: 1 of the 1.5 seconds
    /// Suite fit took over 210,000 license holders.</param>
    public static string Not(string upn, int? levels = null) =>
        $"NOT EXISTS (SELECT 1 FROM dash.Person AS pl WHERE pl.Upn = {upn} AND pl.Level BETWEEN 1 AND {Bound(levels)})";

    /// <summary>
    /// The same rule as a join, for a query that counts inside an aggregate, where SQL Server allows no subquery:
    /// LEFT JOIN this, and a person is not protected when <paramref name="alias"/>.Upn IS NULL.
    /// </summary>
    public static string Join(string alias, string upn, int? levels = null) =>
        $"LEFT JOIN dash.Person AS {alias} ON {alias}.Upn = {upn} AND {alias}.Level BETWEEN 1 AND {Bound(levels)}";

    static string Bound(int? levels) => levels is int n
        ? n.ToString(System.Globalization.CultureInfo.InvariantCulture)
        : $"ISNULL((SELECT TRY_CAST(ps.Value AS INT) FROM dash.Setting AS ps WHERE ps.Name = N'{Setting}'), {Default})";

    /// <summary>
    /// The setting as the statements read it (TRY_CAST, or <see cref="Default"/>), not clamped as <see cref="LevelsAsync"/>
    /// is for the note: for <see cref="Not"/> and <see cref="Join"/>, so a number written into a statement applies the
    /// very rule the lookup did.
    /// </summary>
    public static async Task<int> SqlLevelsAsync(Infrastructure.Settings settings) =>
        int.TryParse((await settings.StringAsync(Setting))?.Trim(), System.Globalization.NumberStyles.AllowLeadingSign,
            System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : Default;

    /// <summary>What a list says about who it leaves out.</summary>
    public static async Task<string> NoteAsync(Infrastructure.Settings settings)
    {
        var n = await LevelsAsync(settings);
        return n <= 0 ? "" : n == 1 ? " The top of the organization is never listed (Admin › Access)." : $" Levels 1 to {n} of the organization are never listed (Admin › Access).";
    }

    public static async Task<int> LevelsAsync(Infrastructure.Settings settings) =>
        int.TryParse(await settings.StringAsync(Setting), out var n) ? Math.Clamp(n, 0, 15) : Default;
}
