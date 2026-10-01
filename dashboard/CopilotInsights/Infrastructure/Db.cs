using System.Reflection;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace CopilotInsights.Infrastructure;

/// <summary>
/// A thin wrapper over Dapper: one connection per call, parameters always bound. A report page's reads are answered
/// from the query cache when it has them (2.4.7, see <see cref="QueryCache"/>); every write tells the cache.
/// </summary>
public sealed class Db(TenantAccessor tenants, QueryCache cache)
{
    /// <summary>The current tenant's database (see TenantAccessor).</summary>
    string _cs => tenants.Current.ConnectionString;
    string Tenant => tenants.Current.Name;

    public SqlConnection Open()
    {
        var c = new SqlConnection(_cs);
        c.Open();
        return c;
    }

    public async Task<List<T>> QueryAsync<T>(string sql, object? args = null, int timeout = 120)
    {
        var cs = _cs;
        return Copy(await cache.GetAsync(Tenant, "q", sql, args, async () =>
        {
            await using var c = new SqlConnection(cs);
            return (await c.QueryAsync<T>(new CommandDefinition(sql, args, commandTimeout: timeout))).AsList();
        }));
    }

    /// <summary>Two result sets from one batch, for statements that share a temporary table.</summary>
    public async Task<(List<T1> First, List<T2> Second)> QueryTwoAsync<T1, T2>(string sql, object? args = null, int timeout = 120)
    {
        var cs = _cs;
        var (first, second) = await cache.GetAsync(Tenant, "q2", sql, args, async () =>
        {
            await using var c = new SqlConnection(cs);
            await using var r = await c.QueryMultipleAsync(new CommandDefinition(sql, args, commandTimeout: timeout));
            var a = (await r.ReadAsync<T1>()).AsList();
            return (a, (await r.ReadAsync<T2>()).AsList());
        });
        return (Copy(first), Copy(second));
    }

    public async Task<T?> ScalarAsync<T>(string sql, object? args = null)
    {
        var cs = _cs;
        return await cache.GetAsync(Tenant, "s", sql, args, async () =>
        {
            await using var c = new SqlConnection(cs);
            return await c.ExecuteScalarAsync<T>(sql, args);
        });
    }

    public async Task<int> ExecuteAsync(string sql, object? args = null, int timeout = 120)
    {
        await using var c = new SqlConnection(_cs);
        try { return await c.ExecuteAsync(new CommandDefinition(sql, args, commandTimeout: timeout)); }
        finally { if (DataStamp.Matters(sql)) cache.Wrote(Tenant); }
    }

    /* ---- A caller's own copy of cached rows ---------------------------------------------------
       A page may sort its list or fill in a row after reading it; with a cached result that would
       change what the next viewer gets. So each caller gets its own list, and its own copy of each
       row that is an object (tuples and numbers are copied by value anyway). */

    static readonly MethodInfo Clone = typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic)!;

    static List<T> Copy<T>(List<T> rows)
    {
        if (!QueryCache.On) return rows;
        if (typeof(T).IsValueType || typeof(T) == typeof(string)) return new List<T>(rows);
        var copy = new List<T>(rows.Count);
        foreach (var r in rows) copy.Add(r is null ? r : (T)Clone.Invoke(r, null)!);
        return copy;
    }
}
