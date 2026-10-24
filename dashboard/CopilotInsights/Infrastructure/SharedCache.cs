using System.Collections;
using System.Collections.Concurrent;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace CopilotInsights.Infrastructure;

/// <summary>
/// The report cache the web servers share, behind each one's own (see <see cref="QueryCache"/>): with several servers behind
/// a load balancer, a query one of them read is kept in dash.QueryCacheShared, and the others read it there rather than run
/// it again. The morning build then costs SQL Server once, not once a server, and a manager's part of the organization built
/// on one server is ready on all of them, with no session affinity.
/// </summary>
/// <remarks>
/// A result is kept under what the database says alone: the data's stamp (with the count of the dashboard's changes in it)
/// and the versions of the tables people change all day, never a server's own counts. It is kept as compressed JSON, and
/// only for a type made of plain values (no object, no interface beyond lists and dictionaries, no HTML), and only when it
/// reads back exactly as it was written: anything else stays in the one server's memory, as before. A failure to read or
/// write here is never a failure of the page: the query is read from the database. Without the table (a database not yet
/// upgraded), nothing is shared.
/// </remarks>
public sealed class SharedCache(TenantAccessor tenants, IOptions<InsightsOptions> options, ILogger<SharedCache> log)
{
    static readonly JsonSerializerOptions Json = new() { IncludeFields = true };
    static readonly ConcurrentDictionary<Type, bool> Shareable = new();
    readonly ConcurrentDictionary<string, bool> _ready = new(StringComparer.OrdinalIgnoreCase);
    readonly ConcurrentDictionary<Type, bool> _noRoundTrip = new();
    long _hits, _stored, _skipped;
    readonly ConcurrentDictionary<string, DateTime> _purged = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// This build of the site: a result is shared only between servers running the same one, so a release that reads the
    /// same query into another shape never reads what the one before kept, and a rolling upgrade keeps the two apart.
    /// </summary>
    static readonly string Build = typeof(SharedCache).Assembly.ManifestModule.ModuleVersionId.ToString("N");

    /* A shared read or write waits at most two seconds on a lock (the clean-up, a server writing the same result), then
       goes to the database: the cache never slows a page down for long. */
    const string Wait = "SET LOCK_TIMEOUT 2000; ";

    /// <summary>Results above this size (compressed) are not shared: reading them back would take longer than building them.</summary>
    public const int MaxBytes = 32 * 1024 * 1024;

    public bool Enabled => options.Value.SharedQueryCache && options.Value.QueryCacheMinutes > 0;
    public (long Hits, long Stored, long Skipped) Counts => (Interlocked.Read(ref _hits), Interlocked.Read(ref _stored), Interlocked.Read(ref _skipped));

    /// <summary>The shared key's hash: the tenant, the stamp the database gave, the query's own key and the live tables' versions.</summary>
    public static byte[] Key(string tenant, string stamp, string kind, Type type, string queryKey, string? live) =>
        SHA256.HashData(Encoding.UTF8.GetBytes($"{Build}\u0001{tenant}\u0001{stamp}\u0001{kind}\u0001{type.FullName}\u0001{queryKey}\u0001{live}"));

    public static byte[] StampHash(string tenant, string stamp) => SHA256.HashData(Encoding.UTF8.GetBytes(Build + "\u0001" + tenant + "\u0001" + stamp));

    /// <summary>
    /// A result from the shared cache, or read with <paramref name="read"/> and kept there. With <paramref name="newerThan"/>
    /// (the pre-cache), a kept result is used only if it was kept after that time.
    /// </summary>
    public async Task<T> ReadThroughAsync<T>(byte[] key, byte[] stampHash, Func<Task<T>> read, DateTime? newerThan, int minutes)
    {
        if (!Enabled || !IsShareable(typeof(T)) || _noRoundTrip.ContainsKey(typeof(T))) return await read();
        var cs = tenants.Current.ConnectionString;
        if (!await ReadyAsync(cs)) return await read();
        try
        {
            await using var c = new SqlConnection(cs);
            var row = await c.QueryFirstOrDefaultAsync<(byte[] Payload, DateTime CreatedUtc)?>(new CommandDefinition(
                Wait + "SELECT Payload, CreatedUtc FROM dash.QueryCacheShared WHERE KeyHash = @key AND ExpiresUtc > SYSUTCDATETIME();", new { key }, commandTimeout: 10));
            if (row is { } r && (newerThan is null || r.CreatedUtc >= newerThan.Value))
            {
                var value = JsonSerializer.Deserialize<T>(Unzip(r.Payload), Json);
                if (value is not null) { Interlocked.Increment(ref _hits); return value; }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogDebug(ex, "The shared report cache could not be read; the query is read from the database.");
        }
        var fresh = await read();
        await StoreAsync(cs, key, stampHash, fresh, minutes);
        return fresh;
    }

    async Task StoreAsync<T>(string cs, byte[] key, byte[] stampHash, T value, int minutes)
    {
        try
        {
            /* Kept only if it reads back as written: a value JSON cannot carry whole stays in this server's memory. */
            var json = JsonSerializer.SerializeToUtf8Bytes(value, Json);
            var again = JsonSerializer.SerializeToUtf8Bytes(JsonSerializer.Deserialize<T>(json, Json), Json);
            if (!json.AsSpan().SequenceEqual(again))
            {
                if (_noRoundTrip.TryAdd(typeof(T), true)) log.LogInformation("{Type} does not read back as written; its results are kept on each server only.", typeof(T).FullName);
                Interlocked.Increment(ref _skipped);
                return;
            }
            var payload = Zip(json);
            if (payload.Length > MaxBytes) { Interlocked.Increment(ref _skipped); return; }
            await using var c = new SqlConnection(cs);
            await c.ExecuteAsync(new CommandDefinition(Wait + @"
UPDATE dash.QueryCacheShared SET StampHash = @stamp, Payload = @payload, Bytes = @bytes, CreatedUtc = SYSUTCDATETIME(),
       ExpiresUtc = DATEADD(MINUTE, @minutes, SYSUTCDATETIME()), Server = @server
WHERE KeyHash = @key;
IF @@ROWCOUNT = 0
    INSERT INTO dash.QueryCacheShared (KeyHash, StampHash, Payload, Bytes, ExpiresUtc, Server)
    VALUES (@key, @stamp, @payload, @bytes, DATEADD(MINUTE, @minutes, SYSUTCDATETIME()), @server);",
                new { key, stamp = stampHash, payload, bytes = payload.Length, minutes, server = Environment.MachineName }, commandTimeout: 60));
            Interlocked.Increment(ref _stored);
            /* Results past their time are cleared by whichever server keeps one, every half hour at most, whether or not
               any server builds the pages ahead (Insights:WarmPages false on all of them). */
            if (DateTime.UtcNow - _purged.GetValueOrDefault(cs) > TimeSpan.FromMinutes(30))
            {
                _purged[cs] = DateTime.UtcNow;
                await DeleteAsync(cs, "ExpiresUtc <= SYSUTCDATETIME()", null);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            /* A value JSON cannot write (a NaN), or two servers keeping the same result at once: one of them wins, which is all that is needed. */
            log.LogDebug(ex, "A result could not be kept in the shared report cache.");
        }
    }

    /// <summary>Clears the results of other stamps kept more than ten minutes ago, and those past their time: by the server that builds the pages.</summary>
    public async Task<int> ClearOldAsync(byte[] stampHash)
    {
        if (!Enabled) return 0;
        var cs = tenants.Current.ConnectionString;
        if (!await ReadyAsync(cs)) return 0;
        return await DeleteAsync(cs, "ExpiresUtc <= SYSUTCDATETIME() OR (StampHash <> @stamp AND CreatedUtc < DATEADD(MINUTE, -10, SYSUTCDATETIME()))", stampHash);
    }

    /// <summary>
    /// Clears results 200 at a time, each batch its own short statement, so a clean-up never holds the table: a server
    /// reading or keeping a result meanwhile waits on a few rows at most, and then not past its lock timeout.
    /// </summary>
    async Task<int> DeleteAsync(string cs, string where, byte[]? stampHash)
    {
        var total = 0;
        try
        {
            await using var c = new SqlConnection(cs);
            for (var i = 0; i < 10000; i++)
            {
                var n = await c.ExecuteAsync(new CommandDefinition($"DELETE TOP (200) FROM dash.QueryCacheShared WHERE {where};", new { stamp = stampHash }, commandTimeout: 60));
                total += n;
                if (n < 200) break;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning(ex, "Old results could not be cleared from the shared report cache."); }
        return total;
    }

    /// <summary>What the shared cache holds, for Admin › Health: results and their size, or null without the table.</summary>
    public async Task<(int Results, long Bytes)?> SizeAsync()
    {
        var cs = tenants.Current.ConnectionString;
        if (!await ReadyAsync(cs)) return null;
        try
        {
            await using var c = new SqlConnection(cs);
            return await c.QuerySingleAsync<(int, long)>("SELECT COUNT(*), ISNULL(SUM(CAST(Bytes AS BIGINT)), 0) FROM dash.QueryCacheShared;");
        }
        catch (SqlException) { return null; }
    }

    async Task<bool> ReadyAsync(string cs)
    {
        if (_ready.TryGetValue(cs, out var ready)) return ready;
        try
        {
            await using var c = new SqlConnection(cs);
            ready = await c.ExecuteScalarAsync<int>("SELECT CASE WHEN OBJECT_ID(N'dash.QueryCacheShared', N'U') IS NULL THEN 0 ELSE 1 END;") == 1;
        }
        catch (SqlException) { return false; }
        _ready[cs] = ready;
        return ready;
    }

    static byte[] Zip(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var z = new GZipStream(ms, CompressionLevel.Fastest)) z.Write(data);
        return ms.ToArray();
    }

    static byte[] Unzip(byte[] data)
    {
        using var ms = new MemoryStream(data);
        using var z = new GZipStream(ms, CompressionMode.Decompress);
        using var o = new MemoryStream();
        z.CopyTo(o);
        return o.ToArray();
    }

    /// <summary>
    /// Whether a type can be kept as JSON and read back as the same values: plain values, lists, dictionaries keyed by
    /// text, value tuples and classes or records of those. Not object, not an interface other than a list or a dictionary,
    /// not HTML, not a delegate. Pure, for the tests.
    /// </summary>
    public static bool IsShareable(Type type) => Shareable.GetOrAdd(type, t => Check(t, []));

    static bool Check(Type t, HashSet<Type> seen)
    {
        if (!seen.Add(t)) return true;
        if (Nullable.GetUnderlyingType(t) is { } inner) return Check(inner, seen);
        if (t.IsPrimitive || t.IsEnum || t == typeof(string) || t == typeof(decimal) || t == typeof(DateTime) || t == typeof(DateTimeOffset)
            || t == typeof(TimeSpan) || t == typeof(Guid) || t == typeof(DateOnly) || t == typeof(byte[])) return true;
        if (t == typeof(object) || typeof(Delegate).IsAssignableFrom(t) || typeof(Microsoft.AspNetCore.Html.IHtmlContent).IsAssignableFrom(t)) return false;
        if (t.IsArray) return Check(t.GetElementType()!, seen);
        if (t.IsGenericType)
        {
            var def = t.GetGenericTypeDefinition();
            var args = t.GetGenericArguments();
            if (def == typeof(List<>) || def == typeof(IReadOnlyList<>) || def == typeof(IList<>) || def == typeof(IEnumerable<>) || def == typeof(IReadOnlyCollection<>))
                return Check(args[0], seen);
            /* Not a dictionary or a set: one made to ignore case would come back matching case, and JSON cannot say which it was. */
            if (def == typeof(Dictionary<,>) || def == typeof(IReadOnlyDictionary<,>) || def == typeof(IDictionary<,>) || def == typeof(HashSet<>))
                return false;
            if (t.FullName?.StartsWith("System.ValueTuple`", StringComparison.Ordinal) == true || t.FullName?.StartsWith("System.Tuple`", StringComparison.Ordinal) == true)
                return args.All(a => Check(a, seen));
        }
        if (t.IsInterface || t.IsAbstract || typeof(IEnumerable).IsAssignableFrom(t)) return false;
        if (t.Namespace?.StartsWith("System", StringComparison.Ordinal) == true) return false;
        /* A member JSON skips (a non-public property, one marked JsonIgnore) would come back empty on the other servers. */
        if (t.GetProperties(BindingFlags.Instance | BindingFlags.NonPublic).Any(p => p.Name != "EqualityContract")) return false;   // a record's own
        if (t.GetMembers(BindingFlags.Instance | BindingFlags.Public).Any(m => m.IsDefined(typeof(System.Text.Json.Serialization.JsonIgnoreAttribute), true))) return false;
        /* A private field JSON never sees (other than a public property's own) would come back empty. */
        if (t.GetFields(BindingFlags.Instance | BindingFlags.NonPublic).Any(f => !f.Name.EndsWith(">k__BackingField", StringComparison.Ordinal))) return false;
        return t.GetProperties(BindingFlags.Instance | BindingFlags.Public).Where(p => p.GetIndexParameters().Length == 0).All(p => Check(p.PropertyType, seen))
            && t.GetFields(BindingFlags.Instance | BindingFlags.Public).All(f => Check(f.FieldType, seen));
    }
}
