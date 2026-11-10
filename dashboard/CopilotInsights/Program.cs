using CopilotInsights.Data;
using CopilotInsights.Infrastructure;
using CopilotInsights.Pages;
using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.AspNetCore.Authorization;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<InsightsOptions>(builder.Configuration.GetSection(InsightsOptions.Section));

var insights = builder.Configuration.GetSection(InsightsOptions.Section).Get<InsightsOptions>() ?? new InsightsOptions();
var entraSignIn = string.Equals(insights.Authentication?.Trim(), SignIn.Entra, StringComparison.OrdinalIgnoreCase);
/* Windows sign-in, the default. Under IIS the handler defers to IIS Windows authentication; under Kestrel (development)
   it negotiates itself. Or Entra ID, with Insights:Authentication = Entra (EntraSignIn). Every page needs a signed-in user. */
if (entraSignIn) EntraSignIn.Configure(builder.Services, insights, Governance.Web.Infrastructure.WorkbenchSetup.Enabled(builder.Configuration));
else builder.Services.AddAuthentication(NegotiateDefaults.AuthenticationScheme).AddNegotiate();
builder.Services.AddAuthorization(o => o.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
/* Grants to Entra groups are checked through Microsoft Graph, whichever way people sign in, once the app registration is set. */
builder.Services.AddSingleton<EntraGraph>();
/* Requests, failures and the site's warnings to Application Insights as well as the log, when a connection string is set. */
if (!string.IsNullOrWhiteSpace(insights.ApplicationInsightsConnectionString))
    builder.Services.AddApplicationInsightsTelemetry(o => o.ConnectionString = insights.ApplicationInsightsConnectionString.Trim());

builder.Services.AddMemoryCache();
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<TenantAccessor>();
/* The query cache in a store of its own, of a bounded size: the shared one has no limit. */
builder.Services.AddSingleton<SharedCache>();
builder.Services.AddSingleton(sp =>
{
    var o = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<InsightsOptions>>();
    /* Each server's own cache, with the one the web servers share behind it (SharedCache). */
    return new QueryCache(QueryCache.Store(o.Value.QueryCacheEntries), o, sp.GetRequiredService<ILogger<QueryCache>>()) { Shared = sp.GetRequiredService<SharedCache>() };
});
/* One set of form keys for every web server, where Insights:DataProtectionKeysPath names a share (KeyRing). */
var keyRing = KeyRing.Configure(builder.Services, insights);
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
builder.Services.AddScoped<CopilotInsights.Data.SuiteData>();
builder.Services.AddScoped<CopilotInsights.Data.CreditData>();
builder.Services.AddScoped<CopilotInsights.Data.ReservationData>();
builder.Services.AddScoped<CopilotInsights.Data.CreditChargeData>();   // Credit chargeback and the commitment
builder.Services.AddScoped<CopilotInsights.Data.AssumptionData>();   // The assumptions register
/* A background service that fails is logged, and the site goes on: .NET's default stops the whole site,
   which a mail server that did not answer once did, again after every restart. */
builder.Services.Configure<HostOptions>(o => o.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore);
builder.Services.AddSingleton<Notifier>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<Notifier>());
builder.Services.AddSingleton<PageWarmer>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<PageWarmer>());
builder.Services.AddRazorPages(o => o.Conventions.ConfigureFilter(new Microsoft.AspNetCore.Mvc.AutoValidateAntiforgeryTokenAttribute()));
/* The Governance Workbench, under /workbench, when ConnectionStrings:Governance names its database (WorkbenchSetup). */
Governance.Web.Infrastructure.WorkbenchSetup.AddWorkbench(builder);

var app = builder.Build();
if (keyRing is not null) app.Logger.LogInformation("Form keys shared by the web servers: {KeyRing}.", keyRing);

/* Under a virtual directory: IIS gives the application's path itself; behind another proxy, Insights:PathBase does. */
if (app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<InsightsOptions>>().Value.PathBase is { Length: > 1 } pathBase)
    app.UsePathBase(pathBase);
app.UsePathBaseLinks();
/* Under IIS the base is only known per request: said once, at the first, when it is named as one of the site's own addresses. */
var clashSaid = 0;
app.Use(async (ctx, next) =>
{
    if (ctx.Request.PathBase.HasValue && Interlocked.Exchange(ref clashSaid, 1) == 0 && PathBaseLinks.Clashes(ctx.Request.PathBase.Value!))
        app.Logger.LogWarning("The site's virtual directory {PathBase} has the name of one of its own addresses: some links will not reach their page. Install it under another name.", ctx.Request.PathBase.Value);
    await next();
});

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

/* Everything the pages need comes from this site: no CDN, no inline script. */
app.Use(async (context, next) =>
{
    var h = context.Response.Headers;
    /* The Workbench's Teams tab is the one page another site may frame, and only Teams. */
    var teams = context.Request.Path.StartsWithSegments("/workbench/Teams/Launch", StringComparison.OrdinalIgnoreCase);
    var frame = teams ? "frame-ancestors https://teams.microsoft.com https://*.teams.microsoft.com https://*.cloud.microsoft https://*.skype.com https://*.office.com" : "frame-ancestors 'none'";
    h.ContentSecurityPolicy = $"default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; {frame}; base-uri 'self'; form-action 'self'";
    h.XContentTypeOptions = "nosniff";
    h["Referrer-Policy"] = "no-referrer";
    if (!teams) h["X-Frame-Options"] = "DENY";
    h.CacheControl = "no-store";
    await next();
});

/* An address with no page behind it (mistyped, or a bookmark to a page since moved) got an empty response, which
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

/* A Workbench page refused shows why (WorkbenchStatusPages), as a missing address does above. */
Governance.Web.Infrastructure.WorkbenchStatusPages.UseWorkbenchStatusPages(app);

/* Not the load balancer's probe: one that asks over HTTP, with TLS ended at the balancer, would be sent to HTTPS and
   count every server down. */
app.UseWhen(ctx => !ctx.Request.Path.StartsWithSegments(HealthProbe.Path), b => b.UseHttpsRedirection());
app.UseStaticFiles();
/* A page, a file or an API answer whose audit row could not be written is not served (AuditLog): 503, and why. */
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
Governance.Web.Infrastructure.WorkbenchSetup.UseWorkbench(app);
app.UseAuthorization();
/* A Workbench address is always in the Workbench's own tenant (WorkbenchSetup.SelectTenantAsync). */
app.Use((ctx, next) => Governance.Web.Infrastructure.WorkbenchSetup.SelectTenantAsync(ctx, () => next()));
/* The dashboard's level limit, behind every endpoint that is not a page: pages refuse with
   their own message (InsightsPage), the data API with its own; anything else added later is covered here. */
app.Use(async (ctx, next) =>
{
    if (ctx.GetEndpoint() is { } ep && ep.Metadata.GetMetadata<Microsoft.AspNetCore.Mvc.RazorPages.PageActionDescriptor>() is null
        && !ctx.Request.Path.StartsWithSegments("/api") && !ctx.Request.Path.StartsWithSegments(HealthProbe.Path))
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
/* The Workbench's home is its Start page; /workbench on its own was a page not found. */
app.MapGet("/workbench", () => Results.Redirect("/workbench/Start"));
/* My Copilot's pages are gone: links to them in earlier emails land on the start page. */
app.MapGet("/me/{**rest}", () => Results.Redirect("/"));
/* Pages merged in 2.7.6: the old addresses (bookmarks, emails, alerts) land on the page that took them, their query kept. */
static IResult Moved(HttpRequest r, string to, string? add = null)
{
    var q = string.Join("&", new[] { r.QueryString.HasValue ? r.QueryString.Value![1..] : null, add }.Where(x => !string.IsNullOrEmpty(x)));
    return Results.Redirect(q.Length == 0 ? to : to + "?" + q);
}
app.MapGet("/security/non-human", (HttpRequest r) => Moved(r, Nav.NonHuman));
app.MapGet("/compliance/non-human", (HttpRequest r) => Moved(r, Nav.NonHuman));
app.MapGet("/leadership/scorecard", (HttpRequest r) => Moved(r, "/leadership/boardpack"));
app.MapGet("/adoption/leaderboard", (HttpRequest r) => Moved(r, "/adoption/departments"));
app.MapGet("/agents/shared", (HttpRequest r) => Moved(r, "/agents/inventory", "show=shared"));
app.MapGet("/agents/production", (HttpRequest r) => Moved(r, "/agents/inventory", "show=production"));
app.MapDataApi();
/* Signed in through Entra ID, a way out: of the dashboard and of Entra ID, back to the start page. */
if (entraSignIn) app.MapGet(EntraSignIn.SignOutPath, EntraSignIn.SignOut);
/* The load balancer's probe: no sign-in, nothing named, only whether this server answers and reaches its database. */
app.MapMethods(HealthProbe.Path, ["GET", "HEAD"], HealthProbe.AnswerAsync).AllowAnonymous();

app.Run();

/// <summary>For the tests.</summary>
public partial class Program;
