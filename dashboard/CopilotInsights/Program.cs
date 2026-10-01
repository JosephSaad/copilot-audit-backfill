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
builder.Services.AddSingleton<QueryCache>();
builder.Services.AddSingleton<DataStamp>();
builder.Services.AddSingleton<Db>();
builder.Services.AddSingleton<Settings>();
builder.Services.AddSingleton<Freshness>();
builder.Services.AddScoped<ViewerService>();
builder.Services.AddScoped<ReportContextFactory>();
builder.Services.AddScoped<AuditLog>();
builder.Services.AddScoped<Metrics>();
builder.Services.AddScoped<PageServices>();
builder.Services.AddHttpClient();
builder.Services.AddSingleton<SecretStore>();
builder.Services.AddSingleton<Mailer>();
builder.Services.AddScoped<OpsData>();
builder.Services.AddScoped<OpsReports>();
builder.Services.AddSingleton<Notifier>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<Notifier>());
builder.Services.AddSingleton<PageWarmer>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<PageWarmer>());
builder.Services.AddRazorPages(o => o.Conventions.ConfigureFilter(new Microsoft.AspNetCore.Mvc.AutoValidateAntiforgeryTokenAttribute()));

var app = builder.Build();

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

app.UseHttpsRedirection();
app.UseStaticFiles();
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
        if (!viewer.Admitted && !viewer.IsAdmin)
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
