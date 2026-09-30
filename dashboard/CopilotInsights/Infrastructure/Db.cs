using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace CopilotInsights.Infrastructure;

/// <summary>A thin wrapper over Dapper: one connection per call, parameters always bound.</summary>
public sealed class Db(TenantAccessor tenants)
{
    /// <summary>The current tenant's database (see TenantAccessor).</summary>
    string _cs => tenants.Current.ConnectionString;

    public SqlConnection Open()
    {
        var c = new SqlConnection(_cs);
        c.Open();
        return c;
    }

    public async Task<List<T>> QueryAsync<T>(string sql, object? args = null, int timeout = 120)
    {
        await using var c = new SqlConnection(_cs);
        return (await c.QueryAsync<T>(new CommandDefinition(sql, args, commandTimeout: timeout))).AsList();
    }

    /// <summary>Two result sets from one batch, for statements that share a temporary table.</summary>
    public async Task<(List<T1> First, List<T2> Second)> QueryTwoAsync<T1, T2>(string sql, object? args = null, int timeout = 120)
    {
        await using var c = new SqlConnection(_cs);
        await using var r = await c.QueryMultipleAsync(new CommandDefinition(sql, args, commandTimeout: timeout));
        var first = (await r.ReadAsync<T1>()).AsList();
        return (first, (await r.ReadAsync<T2>()).AsList());
    }

    public async Task<T?> ScalarAsync<T>(string sql, object? args = null)
    {
        await using var c = new SqlConnection(_cs);
        return await c.ExecuteScalarAsync<T>(sql, args);
    }

    public async Task<int> ExecuteAsync(string sql, object? args = null, int timeout = 120)
    {
        await using var c = new SqlConnection(_cs);
        return await c.ExecuteAsync(new CommandDefinition(sql, args, commandTimeout: timeout));
    }
}
