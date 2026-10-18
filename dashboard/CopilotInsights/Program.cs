using CopilotInsights.Data;
using CopilotInsights.Infrastructure;
using CopilotInsights.Pages;
using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.AspNetCore.Authorization;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<InsightsOptions>(builder.Configuration.GetSection(InsightsOptions.Section));

/* Windows sign-in. Under IIS the handler defers to IIS Windows authentication;
   under Kestrel (development) it negotiates itself. Every page needs a signed-in user. */
builder.Services.AddAuthentication(NegotiateDefaults.AuthenticationScheme).AddNegotiate();
builder.Services.AddAuthorization(o => o.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

builder.Services.AddMemoryCache();
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<TenantAccessor>();
/* The query cache in a store of its own, of a bounded size (2.4.12): the shared one has no limit. */
builder.Services.AddSingleton(sp =>
{
    var o = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<InsightsOptions>>();
    return new QueryCache(QueryCache.Store(o.Value.QueryCacheEntries), o, sp.GetRequiredService<ILogger<QueryCache>>());
});
/* One set of form keys for every web server, where Insights:DataProtectionKeysPath names a share (2.4.12, KeyRing). */
var keyRing = KeyRing.Configure(builder.Services, builder.Configuration.GetSection(InsightsOptions.Section).Get<InsightsOptions>() ?? new InsightsOptions());
builder.Services.AddSingleton<DataStamp>();
builder.Services.AddSingleton<Db>();
builder.Services.AddSingleton<Settings>();
builder.Services.AddSingleton<Freshness>();
builder.Services.AddScoped<ViewerService>();
builder.Services.AddScoped<ReportContextFactory>();
builder.Services.AddScoped<AuditLog>();
builder.Services.AddScoped<MostUsed>();
builder.Services.AddScoped<Metrics>();
builder.Services.AddScoped<PageServices>();
builder.Services.AddHttpClient();
builder.Services.AddSingleton<SecretStore>();
builder.Services.AddSingleton<Mailer>();
builder.Services.AddScoped<OpsData>();
builder.Services.AddScoped<OpsReports>();
builder.Services.AddScoped<CopilotInsights.Data.CostData>();
/* A background service that fails is logged, and the site goes on (2.4.12): .NET's default stops the whole site,
   which a mail server that did not answer once did, again after every restart. */
builder.Services.Configure<HostOptions>(o => o.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore);
builder.Services.AddSingleton<Notifier>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<Notifier>());
builder.Services.AddSingleton<PageWarmer>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<PageWarmer>());
builder.Services.AddRazorPages(o => o.Conventions.ConfigureFilter(new Microsoft.AspNetCore.Mvc.AutoValidateAntiforgeryTokenAttribute()));

var app = builder.Build();
if (keyRing is not null) app.Logger.LogInformation("Form keys shared by the web servers: {KeyRing}.", keyRing);

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

/* Everything the pages need comes from this site: no CDN, no inline script. */
app.Use(async (context, next) =>
{
    var h = context.Response.Headers;
    h.ContentSecurityPolicy = "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; frame-ancestors 'none'; base-uri 'self'; form-action 'self'";
    h.XContentTypeOptions = "nosniff";
    h["Referrer-Policy"] = "no-referrer";
    h["X-Frame-Options"] = "DENY";
    h.CacheControl = "no-store";
    await next();
});

/* 2.4.10: an address with no page behind it (mistyped, or a bookmark to a page since moved) got an empty response, which
   a browser shows as a blank page. It is asked again as /not-found, which says so and offers the nearest reports; the
   answer is still 404. Only a page someone asked for: not the data API, not a file, not a form, and not a 404 that
   already came with a page of its own (a report turned off). */
app.Use(async (ctx, next) =>
{
    await next();
    var r = ctx.Response;
    if (r.StatusCode != StatusCodes.Status404NotFound || r.HasStarted || r.ContentLength is not null || !string.IsNullOrEmpty(r.ContentType)
        || !HttpMethods.IsGet(ctx.Request.Method) || ctx.Request.Path.StartsWithSegments("/api")
        || Path.HasExtension(ctx.Request.Path.Value) || ctx.Items.ContainsKey(CopilotInsights.Pages.NotFoundModel.AskedKey)) return;
    ctx.Items[CopilotInsights.Pages.NotFoundModel.AskedKey] = ctx.Request.Path.Value;
    ctx.SetEndpoint(null);
    ctx.Features.Get<Microsoft.AspNetCore.Http.Features.IRouteValuesFeature>()?.RouteValues?.Clear();
    ctx.Request.Path = "/not-found";
    ctx.Request.QueryString = QueryString.Empty;
    await next();
});

app.UseHttpsRedirection();
app.UseStaticFiles();
/* A page, a file or an API answer whose audit row could not be written is not served (2.4.12, AuditLog): 503, and why. */
app.Use(async (ctx, next) =>
{
    try { await next(); }
    catch (AuditLogUnavailableException) when (!ctx.Response.HasStarted)
    {
        ctx.Response.Headers.Remove("Content-Disposition");
        ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        ctx.Response.ContentType = "text/plain; charset=utf-8";
        await ctx.Response.WriteAsync(AuditLogUnavailableException.Message503);
    }
});
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.Use((ctx, next) => TenantAccessor.SelectAsync(ctx, () => next()));
/* The dashboard's level limit (2.4.4), behind every endpoint that is not a page: pages refuse with
   their own message (InsightsPage), the data API with its own; anything else added later is covered here. */
app.Use(async (ctx, next) =>
{
    if (ctx.GetEndpoint() is { } ep && ep.Metadata.GetMetadata<Microsoft.AspNetCore.Mvc.RazorPages.PageActionDescriptor>() is null
        && !ctx.Request.Path.StartsWithSegments("/api"))
    {
        var viewer = await ctx.RequestServices.GetRequiredService<ViewerService>().GetAsync();
        if (!viewer.Admitted && !viewer.AnyAdmin)
        {
            await ctx.RequestServices.GetRequiredService<AuditLog>().WriteAsync(viewer, "Denied", ctx.Request.Path, null, "outside the dashboard's levels");
            ctx.Response.StatusCode = 403;
            return;
        }
    }
    await next();
});
app.MapRazorPages();
/* Health moved from /admin, which servers often block, to /admin/health; old links still arrive. */
app.MapGet("/admin", () => Results.Redirect("/admin/health"));
/* My Copilot's pages are gone (2.4.4): links to them in earlier emails land on the start page. */
app.MapGet("/me/{**rest}", () => Results.Redirect("/"));
app.MapDataApi();

app.Run();

/// <summary>For the tests.</summary>
public partial class Program;
