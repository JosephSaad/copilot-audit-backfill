using Dapper;
using Microsoft.Data.SqlClient;

namespace Governance.Application.Integrations;

/// <summary>Copilot use per person, as Copilot Insights measured it from the audit log.</summary>
/// <param name="AsOf">The last day Insights has records for; null when it has none.</param>
public sealed record InsightsActivity(DateTime? AsOf, IReadOnlyList<InsightsActivityUser> Users);

public sealed record InsightsActivityUser(string UserPrincipalName, DateTime LastActivityDate);

/// <summary>
/// Copilot activity from the Copilot Insights database, when the Workbench can read it (ConnectionStrings:Insights,
/// or Insights' own connection on the web site). The seat read prefers it to Microsoft's usage report: it covers every
/// day Insights has loaded, and never hides names.
/// </summary>
public interface IInsightsActivitySource
{
    Task<InsightsActivity> ReadAsync(CancellationToken ct);

    /// <summary>Each person's Entra object ID by UPN, as Insights' directory load records it (dash.Person.ObjectId).</summary>
    Task<IReadOnlyList<(string Upn, Guid ObjectId)>> ObjectIdsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<(string, Guid)>>([]);
}

/// <summary>Reads dash.UserDay (each person's interactions per day) and bi.DimUser (their UPN). Read only.</summary>
public sealed class InsightsActivitySource(string connectionString) : IInsightsActivitySource
{
    public const string Sql = """
        SELECT u.UserPrincipalName, CONVERT(date, CONVERT(char(8), MAX(d.DateKey))) AS LastActivityDate
        FROM dash.UserDay AS d JOIN bi.DimUser AS u ON u.UserKey = d.UserKey
        WHERE d.Interactions > 0 AND u.UserPrincipalName IS NOT NULL
        GROUP BY u.UserPrincipalName;
        SELECT CONVERT(date, CONVERT(char(8), MAX(DateKey))) FROM dash.UserDay;
        """;

    public async Task<IReadOnlyList<(string Upn, Guid ObjectId)>> ObjectIdsAsync(CancellationToken ct)
    {
        await using var c = new SqlConnection(connectionString);
        await c.OpenAsync(ct);
        /* dash.Person.ObjectId came with the 2.8.0 license tool; an older Insights database has none to give. */
        return (await c.QueryAsync<(string Upn, Guid ObjectId)>(new CommandDefinition(
            "IF COL_LENGTH(N'dash.Person', N'ObjectId') IS NOT NULL EXEC (N'SELECT Upn, ObjectId FROM dash.Person WHERE ObjectId IS NOT NULL AND Upn IS NOT NULL');",
            commandTimeout: 600, cancellationToken: ct))).ToList();
    }

    public async Task<InsightsActivity> ReadAsync(CancellationToken ct)
    {
        await using var c = new SqlConnection(connectionString);
        await c.OpenAsync(ct);
        using var grid = await c.QueryMultipleAsync(new CommandDefinition(Sql, commandTimeout: 600, cancellationToken: ct));
        var users = (await grid.ReadAsync<InsightsActivityUser>()).ToList();
        var asOf = await grid.ReadSingleOrDefaultAsync<DateTime?>();
        return new InsightsActivity(asOf, users);
    }
}
