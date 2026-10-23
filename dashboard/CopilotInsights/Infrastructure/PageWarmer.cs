using System.Diagnostics;
using System.Text.Json;
using CopilotInsights.Pages;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.RazorPages.Infrastructure;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;

namespace CopilotInsights.Infrastructure;

/// <summary>
/// Builds every report page for the whole organization, at every period, whenever the data changes (2.4.7): after the
/// nightly refresh, after an administrator changes a setting or reference data, when the site starts, and when the last build's
/// results expire (2.4.12). Each web server builds its own, for its own cache: nothing is shared. It runs the
/// pages' own code with no response written, so their queries' results are in the query cache before anyone asks:
/// the top of the organization, their direct reports and holders of Organization then get each page from memory.
/// Nothing here changes who may see what: a result is only ever read by a request whose own checks produced the
/// very same query (see <see cref="QueryCache"/>). Off with Insights:QueryCacheMinutes = 0.
/// </summary>
/// <remarks>
/// The daily pre-cache (2.5.1): Invoke-InsightsPrecache.ps1, scheduled on each web server, leaves a request in the
/// precache folder (<see cref="InsightsOptions.PrecacheFolder"/>). The site takes it within 15 seconds, builds every
/// page again reading each query afresh (<see cref="QueryCache.Refresh"/>), so nobody waits during the build and every
/// result is kept for its full time from then, then builds each page once more from memory, timed, and writes how it
/// went to precache.json beside the request, which the script waits for. Only an account that can write that folder,
/// an administrator of the server, can ask; the request starts a build and carries nothing else.
/// </remarks>
public sealed class PageWarmer(IServiceScopeFactory scopes, TenantAccessor tenants, QueryCache cache, DataStamp stamp, SharedCache shared,
    IActionDescriptorCollectionProvider actions, IOptions<InsightsOptions> options, IWebHostEnvironment env, ILogger<PageWarmer> log) : BackgroundService
{
    readonly Dictionary<string, string> _warmed = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>What the last warm-up did, for Admin › Health.</summary>
    public sealed record Status(string Tenant, DateTime StartedUtc, DateTime? EndedUtc, int Pages, int Failed, double Seconds);
    public IReadOnlyDictionary<string, Status> Last => _last;
    readonly System.Collections.Concurrent.ConcurrentDictionary<string, Status> _last = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The last pre-cache on this web server since the site started, for Admin › Health; null before the first.</summary>
    public PrecacheStatus? LastPrecache { get; private set; }

    /// <summary>The file the script leaves, and the one the site answers with, in <see cref="Folder"/>.</summary>
    public const string RequestFile = "precache.request", StatusFile = "precache.json";

    /// <summary>A page built from memory more slowly than this is counted in the pre-cache's answer.</summary>
    public const double SlowSeconds = 1.0;

    /// <summary>Where requests are left: Insights:PrecacheFolder, else the site's own precache folder.</summary>
    public string Folder => string.IsNullOrWhiteSpace(options.Value.PrecacheFolder) ? Path.Combine(env.ContentRootPath, "precache") : options.Value.PrecacheFolder;

    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    string? _takenId;

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(Math.Max(0, options.Value.WarmStartDelaySeconds)), stop); } catch (OperationCanceledException) { return; }
        while (!stop.IsCancellationRequested)
        {
            /* A pre-cache asked for comes first (2.5.1): it builds every page whatever the last build was. */
            if (TakeRequest() is { } request)
            {
                try { await PrecacheAsync(request, stop); }
                catch (Exception ex) when (ex is not OperationCanceledException || !stop.IsCancellationRequested) { log.LogWarning(ex, "The pre-cache {Id} failed", request.Id); }
            }
            if (cache.Enabled && options.Value.WarmPages)
            {
                foreach (var t in tenants.All)
                {
                    tenants.Use(t);
                    try
                    {
                        await stamp.EnsureAsync();
                        if (Due(_warmed.GetValueOrDefault(t.Name), stamp.Current, _last.GetValueOrDefault(t.Name)?.EndedUtc, DateTime.UtcNow, options.Value.QueryCacheMinutes))
                            await WarmAsync(t, stop);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException || !stop.IsCancellationRequested) { log.LogWarning(ex, "Warming the report pages failed for {Tenant}", t.Name); }
                }
            }
            /* A request that made the build above give way is taken at once. */
            if (RequestWaiting()) continue;
            try { await Task.Delay(TimeSpan.FromSeconds(15), stop); } catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>
    /// Whether to build the pages now: the data changed since the last build, or that build's results have expired
    /// (2.4.12). Only a change started one, and every result is kept for at most Insights:QueryCacheMinutes, so 12 hours
    /// after the nightly build nothing was kept and nothing was built again until midnight UTC: for the afternoon, the
    /// top of the organization waited for every page. Pure, for the tests.
    /// </summary>
    public static bool Due(string? warmedStamp, string currentStamp, DateTime? lastEndedUtc, DateTime nowUtc, int cacheMinutes) =>
        warmedStamp != currentStamp || (lastEndedUtc is { } ended && nowUtc >= ended.AddMinutes(cacheMinutes));

    /// <summary>The report pages: every page whose model is an InsightsPage that is not an admin page.</summary>
    async Task<List<(CompiledPageActionDescriptor Page, string Path)>> PagesAsync(IServiceProvider sp)
    {
        var loader = sp.GetRequiredService<PageLoader>();
        var list = new List<(CompiledPageActionDescriptor, string)>();
        foreach (var d in actions.ActionDescriptors.Items.OfType<PageActionDescriptor>())
        {
            var compiled = await loader.LoadAsync(d, EndpointMetadataCollection.Empty);
            var model = compiled.ModelTypeInfo?.AsType();
            if (model is null || !typeof(InsightsPage).IsAssignableFrom(model) || model.IsAbstract || typeof(Pages.Admin.AdminPage).IsAssignableFrom(model)) continue;
            var path = "/" + (d.AttributeRouteInfo?.Template ?? "").Trim('/');
            if (path.Contains('{')) continue;
            list.Add((compiled, path));
        }
        return list;
    }

    /// <summary>A build of every page: how many were built, how many failed, and whether it ran to the end.</summary>
    sealed record Build(int Pages, int Failed, double Seconds, bool Complete);

    /// <summary>The automatic warm-up: every page, kept where it is already kept, recorded for Admin › Health.</summary>
    Task<Build> WarmAsync(TenantOption tenant, CancellationToken stop) => BuildAllAsync(tenant, stop, refresh: false, times: null, record: true);

    /// <summary>
    /// Builds every report page at every period for the whole organization. Refresh: each query is read again and its
    /// result replaces the kept one (the pre-cache). Times: each page's time is added, for the pre-cache's check from
    /// memory. Record: the build is what Admin › Health shows and what <see cref="Due"/> counts from.
    /// </summary>
    async Task<Build> BuildAllAsync(TenantOption tenant, CancellationToken stop, bool refresh, List<PageTime>? times, bool record, Action<int, int>? progress = null)
    {
        var started = DateTime.UtcNow;
        var at = stamp.Current;
        var sw = Stopwatch.StartNew();
        int built = 0, failed = 0;
        if (record) _last[tenant.Name] = new(tenant.Name, started, null, 0, 0, 0);
        /* Someone who sees the whole organization and holds every report role: the queries they run are the ones the top
           of the organization, their direct reports and Organization holders run. No account, so nothing of a person's own. */
        var viewer = new Viewer
        {
            WindowsName = "(warm-up)", Upn = null, DisplayName = "Warm-up", Level = 1, InDirectory = true, PeopleBelow = 0,
            Roles = new HashSet<string>(Roles.All.Where(r => r != Roles.Admin), StringComparer.OrdinalIgnoreCase),
        };
        List<(CompiledPageActionDescriptor Page, string Path)> pages;
        using (var s0 = scopes.CreateScope()) pages = await PagesAsync(s0.ServiceProvider);
        var settings = scopes.CreateScope().ServiceProvider.GetRequiredService<Settings>();
        var defaultDays = await settings.IntAsync("DefaultDays", 30);
        /* The period a page opens with first, then the longest (the slowest to build), then the rest. */
        var periods = ReportContext.DayChoices.OrderBy(d => d == defaultDays ? 0 : d == 365 ? 1 : 2).ThenByDescending(d => d).ToList();
        /* With several web servers sharing the cache, one builds at a time: the first reads the database, and the next finds
           its results in the shared cache rather than reading it again. A pre-cache on a server that waited uses what the
           other kept since it began. */
        var (held, waited) = shared.Enabled ? await WarmLockAsync(stop) : (null, false);
        await using var heldLock = held;
        using var fresh = refresh ? QueryCache.Refresh() : null;
        using var since = refresh && waited ? QueryCache.RefreshFrom(started.AddMinutes(-60)) : null;
        if (waited) log.LogInformation("Another web server built the pages for {Tenant}; this one reads what it kept.", tenant.Name);
        foreach (var days in periods)
        {
            foreach (var (page, path) in pages)
            {
                if (stop.IsCancellationRequested) return new(built, failed, sw.Elapsed.TotalSeconds, false);
                /* A pre-cache asked for while the pages are being built as usual: this build gives way, as the pre-cache
                   builds every page anyway, and the script waiting for it is answered sooner. */
                if (!refresh && record && RequestWaiting()) { log.LogInformation("A pre-cache was asked for while warming {Tenant}; it goes first.", tenant.Name); return new(built, failed, sw.Elapsed.TotalSeconds, false); }
                await stamp.EnsureAsync();
                if (stamp.Current != at) { log.LogInformation("The data changed while warming {Tenant}; starting again.", tenant.Name); return new(built, failed, sw.Elapsed.TotalSeconds, false); }
                var one = Stopwatch.StartNew();
                try
                {
                    using var scope = scopes.CreateScope();
                    var http = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
                    http.Request.Scheme = "https";
                    http.Request.Host = new HostString("localhost");
                    http.Request.Path = path;
                    http.Request.QueryString = new QueryString($"?d={days}");
                    var model = (InsightsPage)ActivatorUtilities.CreateInstance(scope.ServiceProvider, page.ModelTypeInfo!.AsType());
                    model.PageContext = new PageContext(new ActionContext(http, new RouteData(), page));
                    await model.WarmAsync(viewer, days);
                    built++;
                    times?.Add(new(path, days, Math.Round(one.Elapsed.TotalSeconds, 3)));
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !stop.IsCancellationRequested)
                {
                    failed++;
                    log.LogDebug(ex, "Warming {Path} for {Days} days failed", path, days);
                }
                if (record) _last[tenant.Name] = new(tenant.Name, started, null, built, failed, sw.Elapsed.TotalSeconds);
                progress?.Invoke(built, pages.Count * periods.Count);
            }
        }
        /* What older data kept in the shared cache goes, once a build for this data is done. */
        if (shared.Enabled && !waited) await shared.ClearOldAsync(SharedCache.StampHash(tenant.Name, cache.DataStampOf(tenant.Name)));
        if (record)
        {
            _warmed[tenant.Name] = at;
            _last[tenant.Name] = new(tenant.Name, started, DateTime.UtcNow, built, failed, sw.Elapsed.TotalSeconds);
            log.LogInformation("Report pages warmed for {Tenant}: {Built} page views in {Seconds:0} s, {Failed} not built.", tenant.Name, built, sw.Elapsed.TotalSeconds, failed);
        }
        return new(built, failed, sw.Elapsed.TotalSeconds, true);
    }

    /// <summary>
    /// The build's turn, among the web servers: a lock in SQL Server held for the build on a connection of its own, and
    /// whether this server had to wait for another's build. Waits at most three hours; then it builds without it.
    /// </summary>
    async Task<(Microsoft.Data.SqlClient.SqlConnection? Lock, bool Waited)> WarmLockAsync(CancellationToken stop)
    {
        var waited = false;
        var until = DateTime.UtcNow.AddHours(3);
        while (!stop.IsCancellationRequested && DateTime.UtcNow < until)
        {
            var c = new Microsoft.Data.SqlClient.SqlConnection(tenants.Current.ConnectionString);
            try
            {
                await c.OpenAsync(stop);
                var got = await Dapper.SqlMapper.ExecuteScalarAsync<int>(c, @"
DECLARE @r INT;
EXEC @r = sp_getapplock @Resource = N'CopilotInsights.WarmPages', @LockMode = N'Exclusive', @LockOwner = N'Session', @LockTimeout = 0;
SELECT @r;");
                if (got >= 0) return (c, waited);
            }
            catch (Microsoft.Data.SqlClient.SqlException ex) { log.LogWarning(ex, "The build's turn could not be taken; building without it."); await c.DisposeAsync(); return (null, waited); }
            await c.DisposeAsync();
            waited = true;
            try { await Task.Delay(TimeSpan.FromSeconds(15), stop); } catch (OperationCanceledException) { break; }
        }
        return (null, waited);
    }

    /// <summary>Whether a request is waiting that has not been taken: one the site could not remove is taken once.</summary>
    bool RequestWaiting()
    {
        var file = Path.Combine(Folder, RequestFile);
        return File.Exists(file) && File.GetLastWriteTimeUtc(file).Ticks != _takenWritten;
    }

    long _takenWritten;

    /// <summary>The request the script left, taken (its file removed), or null when there is none.</summary>
    PrecacheRequest? TakeRequest()
    {
        var file = Path.Combine(Folder, RequestFile);
        if (!File.Exists(file)) return null;
        /* A request with no id of its own is known by when it was written, so one left behind is not taken twice. */
        var written = File.GetLastWriteTimeUtc(file).Ticks;
        if (written == _takenWritten) return null;
        _takenWritten = written;
        PrecacheRequest? request = null;
        try { request = JsonSerializer.Deserialize<PrecacheRequest>(File.ReadAllText(file), Json); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { log.LogWarning(ex, "The pre-cache request {File} could not be read", file); }
        try { File.Delete(file); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            /* A request the site cannot remove would be taken again every 15 seconds: it is taken once. The installer lets
               the app pool change the folder. */
            log.LogWarning(ex, "The pre-cache request {File} could not be removed: let the app pool's account change {Folder}", file, Folder);
        }
        request ??= new PrecacheRequest();
        if (string.IsNullOrWhiteSpace(request.Id)) request.Id = "written-" + written;
        if (request.Id == _takenId) return null;
        _takenId = request.Id;
        return request;
    }

    async Task PrecacheAsync(PrecacheRequest request, CancellationToken stop)
    {
        var status = new PrecacheStatus { Id = request.Id, RequestedUtc = request.RequestedUtc, RequestedBy = request.RequestedBy, StartedUtc = DateTime.UtcNow };
        log.LogInformation("Pre-cache {Id} asked for by {By}: building every report page again.", request.Id, request.RequestedBy ?? "(not given)");
        Write(status);
        if (!cache.Enabled)
        {
            status.Outcome = "off";
            status.Message = "The query cache is off on this web server (Insights:QueryCacheMinutes is 0): every page reads the database every time, and nothing is kept to build ahead.";
            status.EndedUtc = DateTime.UtcNow;
            Write(status);
            return;
        }
        foreach (var t in tenants.All)
        {
            tenants.Use(t);
            var one = new PrecacheTenant { Tenant = t.Name };
            status.Tenants.Add(one);
            /* How far it has got, written every 30 seconds for the script waiting on it. */
            var written = DateTime.UtcNow;
            void Progress(int built, int planned)
            {
                (one.Pages, one.Planned) = (built, planned);
                if (DateTime.UtcNow - written < TimeSpan.FromSeconds(30)) return;
                written = DateTime.UtcNow;
                Write(status);
            }
            try
            {
                /* The data can change while it builds (a refresh ending): it builds again for the new data, up to three times. */
                for (var attempt = 1; ; attempt++)
                {
                    await stamp.EnsureAsync(force: true);
                    var built = new List<PageTime>();
                    var b = await BuildAllAsync(t, stop, refresh: true, times: built, record: true, progress: Progress);
                    one.Pages = b.Pages; one.Failed = b.Failed; one.BuildSeconds = Math.Round(one.BuildSeconds + b.Seconds, 1);
                    one.SlowestToBuild = [.. built.OrderByDescending(x => x.Seconds).Take(10)];
                    if (b.Complete || stop.IsCancellationRequested) break;
                    one.BuiltAgain++;
                    if (attempt == 3) { one.Error = "The data kept changing while the pages were built; the site builds them again on its own once it settles."; break; }
                }
                Write(status);
                if (one.Error is null && !stop.IsCancellationRequested)
                {
                    /* The check: every page again, from memory now, timed. */
                    var times = new List<PageTime>();
                    var check = await BuildAllAsync(t, stop, refresh: false, times, record: false);
                    one.CheckedPages = times.Count;
                    one.CheckSeconds = Math.Round(check.Seconds, 1);
                    one.SlowestFromMemorySeconds = times.Count == 0 ? 0 : times.Max(x => x.Seconds);
                    one.OverOneSecond = times.Count(x => x.Seconds > SlowSeconds);
                    one.Slowest = [.. times.OrderByDescending(x => x.Seconds).Take(10)];
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !stop.IsCancellationRequested)
            {
                one.Error = Incident.Note(log, ex, "The pre-cache failed");
                log.LogWarning(ex, "The pre-cache failed for {Tenant}", t.Name);
            }
            Write(status);
        }
        status.EndedUtc = DateTime.UtcNow;
        status.Outcome = stop.IsCancellationRequested ? "stopped" : status.Tenants.Any(x => x.Error is not null || x.Failed > 0) ? "failed" : "built";
        Write(status);
        log.LogInformation("Pre-cache {Id}: {Outcome}, {Pages} page views built in {Seconds:0} s; from memory the slowest took {Slowest:0.00} s.", status.Id, status.Outcome,
            status.Tenants.Sum(x => x.Pages), status.Tenants.Sum(x => x.BuildSeconds), status.Tenants.Select(x => x.SlowestFromMemorySeconds).DefaultIfEmpty(0).Max());
    }

    /// <summary>Writes the answer whole, through a file beside it, so the script never reads half of one.</summary>
    void Write(PrecacheStatus status)
    {
        var json = JsonSerializer.Serialize(status, Json);
        /* A copy for Admin › Health, which may read it while the build goes on. */
        LastPrecache = JsonSerializer.Deserialize<PrecacheStatus>(json, Json);
        try
        {
            var file = Path.Combine(Folder, StatusFile);
            var part = file + ".part";
            File.WriteAllText(part, json);
            File.Move(part, file, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { log.LogWarning(ex, "The pre-cache's answer could not be written to {Folder}", Folder); }
    }
}

/// <summary>What Invoke-InsightsPrecache.ps1 leaves in the precache folder (2.5.1).</summary>
public sealed class PrecacheRequest
{
    public string Id { get; set; } = "";
    public DateTime? RequestedUtc { get; set; }
    public string? RequestedBy { get; set; }
}

/// <summary>How a pre-cache went, written to precache.json as it goes (2.5.1).</summary>
public sealed class PrecacheStatus
{
    public string Id { get; set; } = "";
    public string Server { get; set; } = Environment.MachineName;
    public DateTime? RequestedUtc { get; set; }
    public string? RequestedBy { get; set; }
    public DateTime StartedUtc { get; set; }
    public DateTime? EndedUtc { get; set; }
    /// <summary>building, built, failed (a page or a tenant did not build), off (no cache), or stopped (the site stopped).</summary>
    public string Outcome { get; set; } = "building";
    public string? Message { get; set; }
    public List<PrecacheTenant> Tenants { get; set; } = [];
}

/// <summary>One tenant's part of a pre-cache.</summary>
public sealed class PrecacheTenant
{
    public string Tenant { get; set; } = "";
    /// <summary>Page views built, each page at each period, reading every query afresh: so far, while it builds.</summary>
    public int Pages { get; set; }
    /// <summary>The page views it builds: every report page at every period.</summary>
    public int Planned { get; set; }
    public int Failed { get; set; }
    public double BuildSeconds { get; set; }
    /// <summary>How many times the data changed during the build, which started it again.</summary>
    public int BuiltAgain { get; set; }
    /// <summary>The ten page views that took longest to build from the database.</summary>
    public List<PageTime> SlowestToBuild { get; set; } = [];
    /// <summary>Page views built again from memory, timed.</summary>
    public int CheckedPages { get; set; }
    public double CheckSeconds { get; set; }
    public double SlowestFromMemorySeconds { get; set; }
    /// <summary>Page views that took more than a second from memory.</summary>
    public int OverOneSecond { get; set; }
    public List<PageTime> Slowest { get; set; } = [];
    public string? Error { get; set; }
}

/// <summary>A page at a period, and how long it took to build.</summary>
public sealed record PageTime(string Path, int Days, double Seconds);
