using System.Collections;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace CopilotInsights.Infrastructure;

/// <summary>
/// Keeps the results of a report page's queries until the data changes. The data behind the reports changes
/// at the nightly refresh and when an administrator changes something; between those, the same query with the same
/// parameters gives the same rows, so it is answered from memory. A result is kept under the tenant, the data's
/// stamp, the query's text and every parameter's name and value: everything that decides who sees what (the scope
/// root, the role-gated SQL) is in those, so a result is only ever given to a request that would have run the very
/// same query. Only report pages opt in (see <see cref="Use"/>); admin pages (but for the labels Admin › Reference data
/// has seen), exports, the data API, forms and the background checks always read the database. A query that reads a
/// table people change all day (triage, exported actions, confirmations) is kept under that table's version as well,
/// read from the database for each page that asks: a change there ends only the results that read it, and is
/// seen by the very next page. Each web server keeps its own (see docs/DASHBOARD.md, Several web servers).
/// </summary>
/// <remarks>
/// Bounded: at most Insights:QueryCacheEntries results (the least recently used go first), and a tenant's
/// results are dropped the moment its stamp changes. Before, nothing bounded it but time: every new search or page
/// number was kept for 12 hours, and every stamp change left the whole previous set in memory until its 12 hours ran out.
/// </remarks>
public sealed class QueryCache(IMemoryCache cache, IOptions<InsightsOptions> options, ILogger<QueryCache> log)
{
    static readonly AsyncLocal<bool> _on = new();
    static readonly AsyncLocal<bool> _refresh = new();
    static readonly AsyncLocal<System.Collections.Concurrent.ConcurrentDictionary<string, string>?> _seen = new();
    static readonly FieldInfo? _templates = typeof(Dapper.DynamicParameters).GetField("templates", BindingFlags.Instance | BindingFlags.NonPublic);
    readonly int _minutes = options.Value.QueryCacheMinutes;
    long _hits, _misses;

    /// <summary>The data's stamp for each tenant: anything cached under an older one is never read again.</summary>
    readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _stamps = new();
    readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> _writes = new();
    readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> _liveWrites = new();

    /// <summary>Each tenant's current results end together: cancelled when its stamp changes, which removes them.</summary>
    readonly System.Collections.Concurrent.ConcurrentDictionary<string, CancellationTokenSource> _ends = new();

    /// <summary>
    /// The store the site gives the query cache: its own, holding at most <paramref name="entries"/> results, so the
    /// rest of the site's small caches (settings, sign-ins) are never what is squeezed out.
    /// </summary>
    public static IMemoryCache Store(int entries) =>
        new MemoryCache(new MemoryCacheOptions { SizeLimit = Math.Max(100, entries), CompactionPercentage = 0.25 });

    public bool Enabled => _minutes > 0;

    /// <summary>The cache the web servers share, behind this one; null keeps every result on this server alone.</summary>
    public SharedCache? Shared { get; init; }

    static readonly AsyncLocal<DateTime?> _refreshSince = new();
    readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _staleStamp = new();

    /// <summary>
    /// The pre-cache on a server that waited for another's build: a result that server kept in the shared cache after
    /// <paramref name="since"/> is used rather than read again (see <see cref="PageWarmer"/>).
    /// </summary>
    public static IDisposable RefreshFrom(DateTime since)
    {
        var before = _refreshSince.Value;
        _refreshSince.Value = since;
        return new SinceScope(before);
    }

    sealed class SinceScope(DateTime? before) : IDisposable
    {
        public void Dispose() => _refreshSince.Value = before;
    }

    /// <summary>
    /// Whether this server wrote since its stamp was last read, which the next page then reads again at once (see
    /// <see cref="DataStamp.EnsureAsync"/>): the change counted in the database is in the stamp the shared cache keys on.
    /// </summary>
    public bool TakeStale(string tenant) => _staleStamp.TryRemove(tenant, out _);

    /// <summary>The data's stamp alone, as the database gave it: what the shared cache keys on.</summary>
    public string DataStampOf(string tenant) => _stamps.GetValueOrDefault(tenant, "0");

    /* The live tables' versions as the database gives them, without this server's own count of its writes. */
    static readonly System.Text.RegularExpressions.Regex LocalWrites = new(@"=\d+/", System.Text.RegularExpressions.RegexOptions.Compiled);
    public (long Hits, long Misses) Counts => (Interlocked.Read(ref _hits), Interlocked.Read(ref _misses));

    /// <summary>Whether the code running now asked for cached reads.</summary>
    public static bool On => _on.Value;

    /// <summary>Turns cached reads on for the code that runs until the result is disposed: a report page building itself.</summary>
    public static IDisposable Use()
    {
        var before = _on.Value;
        var seen = _seen.Value;
        _on.Value = true;
        _seen.Value ??= new();
        return new Scope(before, seen);
    }

    /// <summary>
    /// Turns cached reads off for the code that runs until the result is disposed, inside a page that has them on
    /// who the viewer is and what they may see are read from the database, whatever page asks. Leadership >
    /// Group overview settled each tenant's viewer from a kept copy of the grants, so a revoked Organization grant went
    /// on showing that tenant's whole organization for up to 12 hours.
    /// </summary>
    public static IDisposable Off()
    {
        var before = _on.Value;
        var seen = _seen.Value;
        _on.Value = false;
        return new Scope(before, seen);
    }

    sealed class Scope(bool before, System.Collections.Concurrent.ConcurrentDictionary<string, string>? seen) : IDisposable
    {
        public void Dispose() { _on.Value = before; _seen.Value = seen; }
    }

    /// <summary>
    /// Reads every cached query again, for the code that runs until the result is disposed, and keeps the new result in
    /// place of the old once it is read: the daily pre-cache (see <see cref="PageWarmer"/>). Whoever asks for the
    /// same query meanwhile is answered from the result kept before, so nobody waits for the build, and each result's
    /// time to be kept starts again.
    /// </summary>
    public static IDisposable Refresh()
    {
        var before = _refresh.Value;
        _refresh.Value = true;
        return new RefreshScope(before);
    }

    sealed class RefreshScope(bool before) : IDisposable
    {
        public void Dispose() => _refresh.Value = before;
    }

    /// <summary>
    /// The versions of the tables that change all day, as the page building itself now first read them: one read of
    /// each for a page, however many of its queries name the table. Null outside <see cref="Use"/>.
    /// </summary>
    public static System.Collections.Concurrent.ConcurrentDictionary<string, string>? Seen => _seen.Value;

    /// <summary>This server wrote to a table that changes all day: part of that table's version, with what the database says.</summary>
    public void WroteLive(string tenant, string table) => _liveWrites.AddOrUpdate(tenant + "|" + table.ToLowerInvariant(), 1, (_, n) => n + 1);

    public long LiveWrites(string tenant, string table) => _liveWrites.GetValueOrDefault(tenant + "|" + table.ToLowerInvariant(), 0);

    /// <summary>The stamp of the tenant's data, set by <see cref="DataStamp"/> and bumped by every write the dashboard makes.</summary>
    public string Stamp(string tenant) => _stamps.GetValueOrDefault(tenant, "0") + ":" + _writes.GetValueOrDefault(tenant, 0);

    public void SetStamp(string tenant, string stamp)
    {
        var before = _stamps.GetValueOrDefault(tenant);
        _stamps[tenant] = stamp;
        if (before != stamp) End(tenant);
    }

    /// <summary>The dashboard wrote something (a setting, a reference table): nothing cached before it is read again.</summary>
    public void Wrote(string tenant)
    {
        _writes.AddOrUpdate(tenant, 1, (_, n) => n + 1);
        _staleStamp[tenant] = true;
        End(tenant);
    }

    /* The stamp is changed first and the results ended after it, and a read takes its end before its stamp: a result
       kept under the old stamp always carries an end that is cancelled. */
    void End(string tenant)
    {
        if (_ends.TryRemove(tenant, out var ended)) ended.Cancel();
    }

    /// <param name="live">The versions of the tables that change all day that the query names; without them such a query is not kept.</param>
    public async Task<T> GetAsync<T>(string tenant, string kind, string sql, object? args, Func<Task<T>> read, string? live = null)
    {
        if (!Enabled || !On || (live is null && DataStamp.Volatile(sql)) || Key(sql, args) is not { } k) return await read();
        var end = _ends.GetOrAdd(tenant, _ => new CancellationTokenSource()).Token;
        var key = $"q:{tenant}:{Stamp(tenant)}:{kind}:{typeof(T).FullName}:{k}" + (live is null ? "" : ":" + live);
        /* Behind this server's memory, the cache the web servers share: what another server read is read from there. A
           derived view ("k") is worked out here from shared reads, and stays on this server. */
        if (Shared is { Enabled: true } shared && kind is "q" or "q2" or "s" or "r")
        {
            var dbStamp = DataStampOf(tenant);
            var sharedKey = SharedCache.Key(tenant, dbStamp, kind, typeof(T), k, live is null ? null : LocalWrites.Replace(live, "="));
            var stampHash = SharedCache.StampHash(tenant, dbStamp);
            var own = read;
            var since = _refresh.Value ? _refreshSince.Value ?? DateTime.UtcNow.AddYears(1) : (DateTime?)null;
            read = () => shared.ReadThroughAsync(sharedKey, stampHash, own, since, _minutes);
        }
        if (_refresh.Value)
        {
            /* The pre-cache: read again, and keep the new result once it is read. A read that fails keeps nothing,
               and the result kept before stays. */
            var fresh = new Lazy<Task<T>>(read, LazyThreadSafetyMode.ExecutionAndPublication);
            var value = await fresh.Value;
            using (var e = cache.CreateEntry(key))
            {
                e.Value = fresh;
                e.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(_minutes);
                e.Size = 1;
                e.AddExpirationToken(new Microsoft.Extensions.Primitives.CancellationChangeToken(end));
            }
            Interlocked.Increment(ref _misses);
            return value;
        }
        if (cache.TryGetValue(key, out object? found) && found is Lazy<Task<T>> lazy)
        {
            Interlocked.Increment(ref _hits);
            return await lazy.Value;
        }
        /* One read for the requests that arrive together: the rest wait for it. A failed read is not kept. */
        var mine = new Lazy<Task<T>>(read, LazyThreadSafetyMode.ExecutionAndPublication);
        var entry = cache.GetOrCreate(key, e =>
        {
            e.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(_minutes);
            e.Size = 1;
            e.AddExpirationToken(new Microsoft.Extensions.Primitives.CancellationChangeToken(end));
            return mine;
        })!;
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

    /// <summary>A query's parameters by name, or null when they cannot be read (see <see cref="Key"/>).</summary>
    public static IReadOnlyDictionary<string, object?>? Values(object? args)
    {
        try
        {
            var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            return Collect(args, values) ? values : null;
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
