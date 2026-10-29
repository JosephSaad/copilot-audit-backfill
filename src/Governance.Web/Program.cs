using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Governance.Application;
using Governance.Application.Commands;
using Governance.Application.Data;
using Governance.Application.Queries;
using Governance.Infrastructure;
using Governance.Web.Infrastructure;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Identity.Web;
using Microsoft.Identity.Web.UI;

var builder = WebApplication.CreateBuilder(args);
var cfg = builder.Configuration;
var env = builder.Environment;

// Local settings: the tenant IDs written by setup\Setup-TestTenant.ps1 and the connection strings written by
// Deploy-Database.ps1. Both are git-ignored; neither holds a secret the tenant script made (certificates stay in the store).
var repoRoot = FindRepoRoot(env.ContentRootPath);
cfg.AddJsonFile(Path.Combine(repoRoot, "config", "appsettings.Local.json"), optional: true, reloadOnChange: false);
var tenantPath = cfg["Workbench:TenantSettingsPath"] is { Length: > 0 } p ? p : Path.Combine(repoRoot, "config", "tenant.test.json");
cfg.AddInMemoryCollection(TenantSettings(tenantPath));
cfg.AddEnvironmentVariables();

var testAuth = cfg.GetValue<bool>("Testing:TestAuthentication");
var actAs = cfg.GetValue<bool>("Testing:AllowActAs");
if ((testAuth || actAs) && !env.IsDevelopment())
    throw new InvalidOperationException("Testing:TestAuthentication and Testing:AllowActAs work only in Development. Turn them off.");

builder.Services.Configure<DbOptions>(o =>
{
    o.ConnectionString = cfg.GetConnectionString("Governance") ?? "";
    o.CommandTimeoutSeconds = cfg.GetValue("Database:CommandTimeoutSeconds", 60);
});
builder.Services.AddHttpContextAccessor();
builder.Services.AddGovernanceInfrastructure(cfg);
builder.Services.AddScoped<WebPrincipal>();
builder.Services.AddScoped<ICurrentPrincipal>(sp => sp.GetRequiredService<WebPrincipal>());
builder.Services.AddScoped<PrincipalLoader>();
builder.Services.AddScoped<CommandRunner>();
builder.Services.AddScoped<DecisionRecorder>();
builder.Services.AddScoped<EvidenceStore>();
builder.Services.AddScoped<EvidenceQueries>();
builder.Services.AddScoped<RequestCommands>();
builder.Services.AddScoped<RegistryCommands>();
builder.Services.AddScoped<AdminCommands>();
builder.Services.AddScoped<OperationsCommands>();
builder.Services.AddScoped<SeatCommands>();
builder.Services.AddScoped<WorkQueries>();
builder.Services.AddScoped<RegistryQueries>();
builder.Services.AddScoped<OperationsQueries>();
builder.Services.AddScoped<ReportQueries>();
builder.Services.AddScoped<AdminQueries>();
builder.Services.AddScoped<AccessQueries>();
builder.Services.AddScoped<SeatQueries>();
builder.Services.AddSingleton<HelpText>();
builder.Services.AddSingleton<TestingOptions>(new TestingOptions(testAuth && env.IsDevelopment(), actAs && env.IsDevelopment()));

// ---------------------------------------------------------------------------------------------------------------
// Authentication: Entra ID (OIDC, certificate credential) or, in Development only, a loopback test scheme.
// ---------------------------------------------------------------------------------------------------------------
if (testAuth)
{
    builder.Services.AddAuthentication(TestAuthHandler.SchemeName)
        .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });
}
else
{
    if (string.IsNullOrEmpty(cfg["AzureAd:ClientId"]))
        throw new InvalidOperationException($"Entra settings are missing. Run setup\\Setup-TestTenant.ps1 or set Workbench:TenantSettingsPath (looked in {tenantPath}).");
    builder.Services.AddMicrosoftIdentityWebAppAuthentication(cfg, "AzureAd")
        .EnableTokenAcquisitionToCallDownstreamApi(Governance.Application.Integrations.PowerPlatformScopes.Delegated)
        .AddDistributedTokenCaches();
    builder.Services.AddDistributedSqlServerCache(o =>
    {
        o.ConnectionString = cfg.GetConnectionString("Governance");
        o.SchemaName = "sec";
        o.TableName = "TokenCache";
    });
    builder.Services.Configure<CookieAuthenticationOptions>(CookieAuthenticationDefaults.AuthenticationScheme, o =>
    {
        o.Cookie.Name = "cgw.auth";
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Lax;
        o.Cookie.SecurePolicy = env.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
        o.AccessDeniedPath = "/AccessDenied";
    });
}

builder.Services.AddScoped<IAuthorizationHandler, AppRoleHandler>();
builder.Services.AddScoped<IAuthorizationHandler, PlatformGovernanceHandler>();
builder.Services.AddAuthorization(o =>
{
    o.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
    o.AddPolicy(Policies.Central, b => b.RequireAuthenticatedUser().AddRequirements(new AppRoleRequirement(Governance.Domain.AppRoles.Central, Governance.Domain.AppRoles.Admin)));
    o.AddPolicy(Policies.Operator, b => b.RequireAuthenticatedUser().AddRequirements(new AppRoleRequirement(Governance.Domain.AppRoles.Operator)));
    // Admin pages: Workbench administrators only. The Platform Owner's RACI approval and the intake switch are pages of
    // their own (PlatformGovernance) with their own permission checks.
    o.AddPolicy(Policies.Admin, b => b.RequireAuthenticatedUser().AddRequirements(new AppRoleRequirement(Governance.Domain.AppRoles.Admin)));
    o.AddPolicy(Policies.PlatformGovernance, b => b.RequireAuthenticatedUser().AddRequirements(new PlatformGovernanceRequirement()));
    o.AddPolicy(Policies.Auditor, b => b.RequireAuthenticatedUser().AddRequirements(new AppRoleRequirement(Governance.Domain.AppRoles.Auditor, Governance.Domain.AppRoles.Central)));
});

var mvc = builder.Services.AddRazorPages(o =>
{
    // Configuration pages: administrators only. Governance pages the Platform Owner and Steering use: their own policy.
    foreach (var page in new[] { "/Admin/Integrations", "/Admin/Notifications", "/Admin/Sla", "/Admin/Workflows" })
        o.Conventions.AuthorizePage(page, Policies.Admin);
    foreach (var page in new[] { "/Admin/Raci", "/Admin/Intake", "/Admin/Roles", "/Admin/Text", "/Admin/SeatGroups" })
        o.Conventions.AuthorizePage(page, Policies.PlatformGovernance);
    o.Conventions.AuthorizeFolder("/Operations", Policies.Central);
    o.Conventions.AuthorizeFolder("/Queues", Policies.Central);
    o.Conventions.AuthorizeFolder("/Reports", Policies.Auditor);
    o.Conventions.AllowAnonymousToPage("/Teams/Launch");
    o.Conventions.AllowAnonymousToPage("/Error");
});
if (!testAuth) mvc.AddMicrosoftIdentityUI();
builder.Services.AddAntiforgery(o =>
{
    o.Cookie.Name = "cgw.af";
    o.Cookie.SecurePolicy = env.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
    o.HeaderName = "X-CSRF";
});
builder.Services.Configure<Microsoft.AspNetCore.Mvc.CookieTempDataProviderOptions>(o => o.Cookie.Name = "cgw.td");
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddFixedWindowLimiter("sync", w => { w.PermitLimit = 2; w.Window = TimeSpan.FromMinutes(5); w.QueueLimit = 0; });
    o.AddFixedWindowLimiter("upload", w => { w.PermitLimit = 30; w.Window = TimeSpan.FromMinutes(5); w.QueueLimit = 0; });
});
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(o => o.MultipartBodyLengthLimit = 25 * 1024 * 1024);
builder.Services.AddHealthChecks().AddCheck<SqlHealthCheck>("sql", tags: new[] { "ready" });
if (env.IsDevelopment()) builder.WebHost.UseUrls(cfg["Urls"] ?? "http://localhost:5450");

var app = builder.Build();

app.UseExceptionHandler("/Error");
if (!env.IsDevelopment())
{
    app.UseHsts();
    app.UseHttpsRedirection();
}
app.UseMiddleware<SecurityHeadersMiddleware>();
app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseMiddleware<PrincipalMiddleware>();
app.UseAuthorization();
app.UseRateLimiter();

app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false }).AllowAnonymous();
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = c => c.Tags.Contains("ready") }).AllowAnonymous();
app.MapGet("/", () => Results.Redirect("/Start"));
app.MapRazorPages();
if (!testAuth) app.MapControllers();
app.Run();

static string FindRepoRoot(string start)
{
    var dir = new DirectoryInfo(start);
    while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CopilotGovernance.sln"))) dir = dir.Parent;
    return dir?.FullName ?? start;
}

static Dictionary<string, string?> TenantSettings(string path)
{
    var d = new Dictionary<string, string?>();
    if (!File.Exists(path)) return d;
    using var doc = JsonDocument.Parse(File.ReadAllText(path));
    string? Get(string name) => doc.RootElement.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    d["AzureAd:Instance"] = "https://login.microsoftonline.com/";
    d["AzureAd:TenantId"] = Get("tenantId");
    d["AzureAd:Domain"] = Get("tenantDomain");
    d["AzureAd:ClientId"] = Get("webClientId");
    d["AzureAd:CallbackPath"] = "/signin-oidc";
    d["AzureAd:SignedOutCallbackPath"] = "/signout-oidc";
    d["AzureAd:ClientCertificates:0:SourceType"] = "StoreWithDistinguishedName";
    d["AzureAd:ClientCertificates:0:CertificateStorePath"] = "CurrentUser/My";
    d["AzureAd:ClientCertificates:0:CertificateDistinguishedName"] = Get("webCertificateSubject");
    d["Workbench:CatalogId"] = Get("catalogId");
    d["Workbench:TenantDomain"] = Get("tenantDomain");
    d["Workbench:TeamsAppId"] = Get("teamsAppId");
    return d;
}

public sealed class SqlHealthCheck(Microsoft.Extensions.Options.IOptions<DbOptions> options) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        try
        {
            await using var c = new SqlConnection(options.Value.ConnectionString);
            await c.OpenAsync(ct);
            await using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT 1";
            await cmd.ExecuteScalarAsync(ct);
            return HealthCheckResult.Healthy();
        }
        catch
        {
            // Never name the server or the error on an anonymous endpoint.
            return HealthCheckResult.Unhealthy();
        }
    }
}
