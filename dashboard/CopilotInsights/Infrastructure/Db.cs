using System.Reflection;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace CopilotInsights.Infrastructure;

/// <summary>
/// A thin wrapper over Dapper: one connection per call, parameters always bound. A report page's reads are answered
/// from the query cache when it has them (2.4.7, see <see cref="QueryCache"/>); every write tells the cache. A read
/// between date keys is sent with a plan hint (2.4.11, see <see cref="PlanHints"/>); the cache keys on the text as written.
/// A read of a table people change all day is kept under that table's version (2.4.11, see <see cref="LiveAsync"/>).
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

    /// <summary>
    /// The versions, read from the database now, of the tables people change all day that a query names: what its result
    /// is kept under, so a triage, an export or a confirmation is seen by the next page that reads it, from this server
    /// or another. Each table is read once for a page. Null when the query names none, when the code running did not ask
    /// for cached reads, or when a version cannot be read: then the query reads the database, as it always did.
    /// </summary>
    async Task<string?> LiveAsync(string sql)
    {
        if (!cache.Enabled || !QueryCache.On) return null;
        var tables = DataStamp.LiveIn(sql);
        if (tables.Count == 0) return null;
        var tenant = Tenant;
        var seen = QueryCache.Seen;
        var all = new System.Text.StringBuilder();
        foreach (var t in tables)
        {
            var key = tenant + "|" + t.Table;
            if (seen is null || !seen.TryGetValue(key, out var version))
            {
                var wrote = cache.LiveWrites(tenant, t.Table);
                try
                {
                    await using var c = new SqlConnection(_cs);
                    version = wrote + "/" + (await c.ExecuteScalarAsync<string>(new CommandDefinition(t.Version, commandTimeout: 15)) ?? "none");
                }
                catch (SqlException) { return null; }
                version = seen?.GetOrAdd(key, version) ?? version;
            }
            all.Append(t.Table).Append('=').Append(version).Append(';');
        }
        return all.ToString();
    }

    public async Task<List<T>> QueryAsync<T>(string sql, object? args = null, int timeout = 120)
    {
        var cs = _cs;
        return Copy(await cache.GetAsync(Tenant, "q", sql, args, async () =>
        {
            await using var c = new SqlConnection(cs);
            return (await c.QueryAsync<T>(new CommandDefinition(PlanHints.Apply(sql, args), args, commandTimeout: timeout))).AsList();
        }, await LiveAsync(sql)));
    }

    /// <summary>Two result sets from one batch, for statements that share a temporary table.</summary>
    public async Task<(List<T1> First, List<T2> Second)> QueryTwoAsync<T1, T2>(string sql, object? args = null, int timeout = 120)
    {
        var cs = _cs;
        var (first, second) = await cache.GetAsync(Tenant, "q2", sql, args, async () =>
        {
            await using var c = new SqlConnection(cs);
            await using var r = await c.QueryMultipleAsync(new CommandDefinition(PlanHints.Apply(sql, args), args, commandTimeout: timeout));
            var a = (await r.ReadAsync<T1>()).AsList();
            return (a, (await r.ReadAsync<T2>()).AsList());
        }, await LiveAsync(sql));
        return (Copy(first), Copy(second));
    }

    /// <summary>
    /// Several result sets from one batch, read by the caller (2.4.11): for a page whose lists and counts all come from
    /// one temporary table, so the work behind them is done once. Kept like any other read of a report page; what
    /// <paramref name="read"/> returns is then shared between viewers, so the caller reads it and never changes it.
    /// </summary>
    public async Task<T> ReadAsync<T>(string sql, object? args, Func<SqlMapper.GridReader, Task<T>> read, int timeout = 120)
    {
        var cs = _cs;
        return await cache.GetAsync(Tenant, "r", sql, args, async () =>
        {
            await using var c = new SqlConnection(cs);
            await using var r = await c.QueryMultipleAsync(new CommandDefinition(PlanHints.Apply(sql, args), args, commandTimeout: timeout));
            return await read(r);
        }, await LiveAsync(sql));
    }

    public async Task<T?> ScalarAsync<T>(string sql, object? args = null)
    {
        var cs = _cs;
        return await cache.GetAsync(Tenant, "s", sql, args, async () =>
        {
            await using var c = new SqlConnection(cs);
            return await c.ExecuteScalarAsync<T>(PlanHints.Apply(sql, args), args);
        }, await LiveAsync(sql));
    }

    /// <summary>
    /// A result worked out from kept reads, itself kept (2.7.4). The credit estimate, the cost model and suite fit build
    /// an object for each of up to hundreds of thousands of people from rows already kept, and did it again on every
    /// visit: 0.6 s a page at 120 million interactions, with no query run. Kept under the data stamp, the period and part
    /// of the organization (the context's arguments), and the versions of the tables that change all day. What it returns
    /// is shared between viewers: callers read it and never change it.
    /// </summary>
    public async Task<T> KeptAsync<T>(string name, ReportContext c, Func<Task<T>> build) =>
        await cache.GetAsync(Tenant, "k", $"kept:{name}", c.Args(), build, await LiveAsync(string.Join(" ", DataStamp.VolatileTables)));

    const string CountChange = "IF OBJECT_ID(N'dash.CacheGeneration', N'U') IS NOT NULL UPDATE dash.CacheGeneration SET N = N + 1, ChangedUtc = SYSUTCDATETIME() WHERE Id = 1;";

    public async Task<int> ExecuteAsync(string sql, object? args = null, int timeout = 120)
    {
        await using var c = new SqlConnection(_cs);
        try { return await c.ExecuteAsync(new CommandDefinition(sql, args, commandTimeout: timeout)); }
        finally
        {
            if (DataStamp.Matters(sql))
            {
                /* Counted in the database too, for the other web servers (dash.CacheGeneration, in the stamp). */
                try { await c.ExecuteAsync(new CommandDefinition(CountChange, commandTimeout: 15)); } catch (SqlException) { }
                cache.Wrote(Tenant);
            }
            foreach (var t in DataStamp.LiveIn(sql)) cache.WroteLive(Tenant, t.Table);
        }
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
