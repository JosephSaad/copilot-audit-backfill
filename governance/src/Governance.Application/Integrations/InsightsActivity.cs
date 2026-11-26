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

    /// <summary>The users' AD domain set on Insights' Admin &gt; Directory quality (dash.WorkbenchDirectory), or null.</summary>
    Task<string?> DirectoryDomainAsync(CancellationToken ct) => Task.FromResult<string?>(null);

    /// <summary>Each person Insights' directory load matched to an Entra account: object ID, sign-in and SID, for matching AD group members.</summary>
    Task<IReadOnlyList<KnownAccount>> AccountsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<KnownAccount>>([]);
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
            /* An enabled account, and one object ID for the UPN: a UPN given again to a new hire, with the leaver's row still
               there, gives none rather than the wrong one. */
            "IF COL_LENGTH(N'dash.Person', N'ObjectId') IS NOT NULL EXEC (N'SELECT Upn, MIN(ObjectId) AS ObjectId FROM dash.Person WHERE ObjectId IS NOT NULL AND Upn IS NOT NULL AND AccountEnabled = 1 GROUP BY Upn HAVING COUNT(DISTINCT ObjectId) = 1');",
            commandTimeout: 600, cancellationToken: ct))).ToList();
    }

    /// <summary>The users' AD domain, through the view dash.WorkbenchDirectory (the worker reads that one row, not dash.Setting).</summary>
    public const string DomainSql =
        "IF OBJECT_ID(N'dash.WorkbenchDirectory', N'V') IS NOT NULL EXEC (N'SELECT TOP (1) Domain FROM dash.WorkbenchDirectory WHERE Domain IS NOT NULL;');";

    /// <summary>dash.Person's object ID, sign-in and SID: an Insights database from before 2.8.0 has no object IDs to give.</summary>
    public const string AccountsSql =
        "IF COL_LENGTH(N'dash.Person', N'ObjectId') IS NOT NULL AND COL_LENGTH(N'dash.Person', N'Sid') IS NOT NULL EXEC (N'SELECT ObjectId, Upn AS UserPrincipalName, CAST(NULL AS nvarchar(256)) AS Mail, Sid FROM dash.Person WHERE ObjectId IS NOT NULL;');";

    public async Task<string?> DirectoryDomainAsync(CancellationToken ct)
    {
        await using var c = new SqlConnection(connectionString);
        await c.OpenAsync(ct);
        var d = await c.ExecuteScalarAsync<string?>(new CommandDefinition(DomainSql, commandTimeout: 60, cancellationToken: ct));
        return string.IsNullOrWhiteSpace(d) ? null : d.Trim();
    }

    public async Task<IReadOnlyList<KnownAccount>> AccountsAsync(CancellationToken ct)
    {
        await using var c = new SqlConnection(connectionString);
        await c.OpenAsync(ct);
        return (await c.QueryAsync<KnownAccount>(new CommandDefinition(AccountsSql, commandTimeout: 600, cancellationToken: ct))).ToList();
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
