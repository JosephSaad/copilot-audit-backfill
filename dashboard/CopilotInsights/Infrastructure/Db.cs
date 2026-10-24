using System.Reflection;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace CopilotInsights.Infrastructure;

/// <summary>
/// A thin wrapper over Dapper: one connection per call, parameters always bound. A report page's reads are answered
/// from the query cache when it has them (see <see cref="QueryCache"/>); every write tells the cache. A read
/// between date keys is sent with a plan hint (see <see cref="PlanHints"/>); the cache keys on the text as written.
/// A read of a table people change all day is kept under that table's version (see <see cref="LiveAsync"/>).
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
    /// Several result sets from one batch, read by the caller: for a page whose lists and counts all come from
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

    /* A statement that writes: INSERT ... OUTPUT, an UPDATE or a DELETE that returns a value. */
    static readonly System.Text.RegularExpressions.Regex Writes = new(@"\b(INSERT\s+INTO\s+\[?(dash|audit|bi|dbo)\b|UPDATE\s+[\w\[\].]+\s+SET\b|DELETE\s+FROM\s+\[?(dash|audit|bi|dbo)\b|MERGE\s+(INTO\s+)?\[?(dash|audit|bi|dbo)\b)", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    public async Task<T?> ScalarAsync<T>(string sql, object? args = null)
    {
        var cs = _cs;
        /* A write that returns a value (a new row's id) is never kept, and counts as a write (one adding a credit
           route was not, so the list stayed as it was until the night's load). */
        if (Writes.IsMatch(sql))
        {
            await using var w = new SqlConnection(cs);
            try { return await w.ExecuteScalarAsync<T>(sql, args); }
            finally { await AfterWriteAsync(w, sql); }
        }
        return await cache.GetAsync(Tenant, "s", sql, args, async () =>
        {
            await using var c = new SqlConnection(cs);
            return await c.ExecuteScalarAsync<T>(PlanHints.Apply(sql, args), args);
        }, await LiveAsync(sql));
    }

    /// <summary>
    /// A result worked out from kept reads, itself kept. The credit estimate, the cost model and suite fit build
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
        finally { await AfterWriteAsync(c, sql); }
    }

    /// <summary>After a write, whether it worked or not (part of it may be done): what was kept from the tables it writes is let go.</summary>
    async Task AfterWriteAsync(SqlConnection c, string sql)
    {
        /* Inside an admin change's transaction: counted once it commits, on a connection outside it. Counted before, a page
           read meanwhile on this or another server took the new count with the old rows, and kept them under it. */
        if (DataStamp.Matters(sql) && System.Transactions.Transaction.Current is { } tx)
        {
            var (cs, tenant) = (_cs, Tenant);
            /* At the commit itself, before the page the change redirects to is asked for: a moment, once a change. */
            tx.TransactionCompleted += (_, e) =>
            {
                if (e.Transaction?.TransactionInformation.Status == System.Transactions.TransactionStatus.Committed)
                {
                    using var outside = new System.Transactions.TransactionScope(System.Transactions.TransactionScopeOption.Suppress);
                    try
                    {
                        using var n = new SqlConnection(cs);
                        n.Execute(new CommandDefinition(CountChange, commandTimeout: 15));
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException) { }
                }
                cache.Wrote(tenant);
            };
            foreach (var t in DataStamp.LiveIn(sql)) cache.WroteLive(Tenant, t.Table);
            return;
        }
        if (DataStamp.Matters(sql))
        {
            /* Counted in the database too, for the other web servers (dash.CacheGeneration, in the stamp). A failure here
               must not hide the write's own error, so any is let go: this server forgets its results either way. */
            try { await c.ExecuteAsync(new CommandDefinition(CountChange, commandTimeout: 15)); }
            catch (Exception ex) when (ex is not OperationCanceledException) { }
            cache.Wrote(Tenant);
        }
        foreach (var t in DataStamp.LiveIn(sql)) cache.WroteLive(Tenant, t.Table);
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
