using System.Collections;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace CopilotInsights.Infrastructure;

/// <summary>
/// Keeps the results of a report page's queries until the data changes (2.4.7). The data behind the reports changes
/// at the nightly refresh and when an administrator changes something; between those, the same query with the same
/// parameters gives the same rows, so it is answered from memory. A result is kept under the tenant, the data's
/// stamp, the query's text and every parameter's name and value: everything that decides who sees what (the scope
/// root, the role-gated SQL) is in those, so a result is only ever given to a request that would have run the very
/// same query. Only report pages opt in (see <see cref="Use"/>); admin pages, exports, the data API, forms and the
/// background checks always read the database.
/// </summary>
public sealed class QueryCache(IMemoryCache cache, IOptions<InsightsOptions> options, ILogger<QueryCache> log)
{
    static readonly AsyncLocal<bool> _on = new();
    static readonly FieldInfo? _templates = typeof(Dapper.DynamicParameters).GetField("templates", BindingFlags.Instance | BindingFlags.NonPublic);
    readonly int _minutes = options.Value.QueryCacheMinutes;
    long _hits, _misses;

    /// <summary>The data's stamp for each tenant: anything cached under an older one is never read again.</summary>
    readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _stamps = new();
    readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> _writes = new();

    public bool Enabled => _minutes > 0;
    public (long Hits, long Misses) Counts => (Interlocked.Read(ref _hits), Interlocked.Read(ref _misses));

    /// <summary>Whether the code running now asked for cached reads.</summary>
    public static bool On => _on.Value;

    /// <summary>Turns cached reads on for the code that runs until the result is disposed: a report page building itself.</summary>
    public static IDisposable Use()
    {
        var before = _on.Value;
        _on.Value = true;
        return new Scope(before);
    }

    sealed class Scope(bool before) : IDisposable
    {
        public void Dispose() => _on.Value = before;
    }

    /// <summary>The stamp of the tenant's data, set by <see cref="DataStamp"/> and bumped by every write the dashboard makes.</summary>
    public string Stamp(string tenant) => _stamps.GetValueOrDefault(tenant, "0") + ":" + _writes.GetValueOrDefault(tenant, 0);

    public void SetStamp(string tenant, string stamp) => _stamps[tenant] = stamp;

    /// <summary>The dashboard wrote something (a setting, a grant, a triage): nothing cached before it is read again.</summary>
    public void Wrote(string tenant) => _writes.AddOrUpdate(tenant, 1, (_, n) => n + 1);

    public async Task<T> GetAsync<T>(string tenant, string kind, string sql, object? args, Func<Task<T>> read)
    {
        if (!Enabled || !On || DataStamp.Volatile(sql) || Key(sql, args) is not { } k) return await read();
        var key = $"q:{tenant}:{Stamp(tenant)}:{kind}:{typeof(T).FullName}:{k}";
        if (cache.TryGetValue(key, out object? found) && found is Lazy<Task<T>> lazy)
        {
            Interlocked.Increment(ref _hits);
            return await lazy.Value;
        }
        /* One read for the requests that arrive together: the rest wait for it. A failed read is not kept. */
        var mine = new Lazy<Task<T>>(read, LazyThreadSafetyMode.ExecutionAndPublication);
        var entry = cache.GetOrCreate(key, e => { e.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(_minutes); return mine; })!;
        if (ReferenceEquals(entry, mine)) Interlocked.Increment(ref _misses); else Interlocked.Increment(ref _hits);
        try { return await entry.Value; }
        catch { cache.Remove(key); throw; }
    }

    /// <summary>
    /// The query's text and its parameters, hashed: null when a parameter cannot be read (then the query is not cached,
    /// never cached wrongly). Dapper's DynamicParameters keeps what it was built from in a private list, read here.
    /// </summary>
    public static string? Key(string sql, object? args)
    {
        try
        {
            var values = new SortedDictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            if (!Collect(args, values)) return null;
            var sb = new StringBuilder(sql).Append('\u0001');
            foreach (var (name, value) in values) sb.Append(name).Append('=').Append(Text(value)).Append('\u0002');
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
        }
        catch (Exception) { return null; }
    }

    static bool Collect(object? args, IDictionary<string, object?> into)
    {
        switch (args)
        {
            case null: return true;
            case Dapper.DynamicParameters dp:
                if (_templates is null) return false;
                if (_templates.GetValue(dp) is IEnumerable templates)
                    foreach (var t in templates) if (t is not null && !Collect(t, into)) return false;
                foreach (var name in dp.ParameterNames) into[name] = dp.Get<object?>(name);
                return true;
            case IEnumerable<KeyValuePair<string, object?>> pairs:
                foreach (var (name, value) in pairs) into[name] = value;
                return true;
            default:
                foreach (var p in args.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public))
                    if (p.GetIndexParameters().Length == 0) into[p.Name] = p.GetValue(args);
                return true;
        }
    }

    static string Text(object? value) => value switch
    {
        null => "\u0000null",
        string s => "s:" + s,
        DateTime d => "d:" + d.Ticks,
        IEnumerable list => "l:" + JsonSerializer.Serialize(list.Cast<object?>().Select(Text)),
        IFormattable f => value.GetType().Name + ":" + f.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
        _ => value.GetType().Name + ":" + value,
    };

    public void LogCounts() => log.LogInformation("Query cache: {Hits} answered from memory, {Misses} read from the database.", Counts.Hits, Counts.Misses);
}
