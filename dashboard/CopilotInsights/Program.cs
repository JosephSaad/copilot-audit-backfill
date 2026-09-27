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
builder.Services.AddSingleton<Db>();
builder.Services.AddSingleton<Settings>();
builder.Services.AddScoped<ViewerService>();
builder.Services.AddScoped<ReportContextFactory>();
builder.Services.AddScoped<AuditLog>();
builder.Services.AddScoped<Metrics>();
builder.Services.AddScoped<PageServices>();
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
app.MapRazorPages();

app.Run();

/// <summary>For the tests.</summary>
public partial class Program;
