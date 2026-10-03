using System.Diagnostics;
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
public sealed class PageWarmer(IServiceScopeFactory scopes, TenantAccessor tenants, QueryCache cache, DataStamp stamp,
    IActionDescriptorCollectionProvider actions, IOptions<InsightsOptions> options, ILogger<PageWarmer> log) : BackgroundService
{
    readonly Dictionary<string, string> _warmed = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>What the last warm-up did, for Admin > Health.</summary>
    public sealed record Status(string Tenant, DateTime StartedUtc, DateTime? EndedUtc, int Pages, int Failed, double Seconds);
    public IReadOnlyDictionary<string, Status> Last => _last;
    readonly System.Collections.Concurrent.ConcurrentDictionary<string, Status> _last = new(StringComparer.OrdinalIgnoreCase);

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        if (!cache.Enabled || !options.Value.WarmPages) return;
        try { await Task.Delay(TimeSpan.FromSeconds(Math.Max(0, options.Value.WarmStartDelaySeconds)), stop); } catch (OperationCanceledException) { return; }
        while (!stop.IsCancellationRequested)
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
            try { await Task.Delay(TimeSpan.FromSeconds(45), stop); } catch (OperationCanceledException) { return; }
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

    async Task WarmAsync(TenantOption tenant, CancellationToken stop)
    {
        var started = DateTime.UtcNow;
        var at = stamp.Current;
        var sw = Stopwatch.StartNew();
        int built = 0, failed = 0;
        _last[tenant.Name] = new(tenant.Name, started, null, 0, 0, 0);
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
        foreach (var days in periods)
        {
            foreach (var (page, path) in pages)
            {
                if (stop.IsCancellationRequested) return;
                await stamp.EnsureAsync();
                if (stamp.Current != at) { log.LogInformation("The data changed while warming {Tenant}; starting again.", tenant.Name); return; }
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
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !stop.IsCancellationRequested)
                {
                    failed++;
                    log.LogDebug(ex, "Warming {Path} for {Days} days failed", path, days);
                }
                _last[tenant.Name] = new(tenant.Name, started, null, built, failed, sw.Elapsed.TotalSeconds);
            }
        }
        _warmed[tenant.Name] = at;
        _last[tenant.Name] = new(tenant.Name, started, DateTime.UtcNow, built, failed, sw.Elapsed.TotalSeconds);
        log.LogInformation("Report pages warmed for {Tenant}: {Built} page views in {Seconds:0} s, {Failed} not built.", tenant.Name, built, sw.Elapsed.TotalSeconds, failed);
    }
}
