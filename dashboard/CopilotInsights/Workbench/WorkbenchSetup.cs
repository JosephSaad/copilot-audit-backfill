using System.Threading.RateLimiting;
using Governance.Application;
using Governance.Application.Commands;
using Governance.Application.Data;
using Governance.Application.Queries;
using Governance.Domain;
using Governance.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;

namespace Governance.Web.Infrastructure;

/// <summary>
/// The Governance Workbench inside Copilot Insights: its pages under /workbench, its services, and the page rules.
/// It is on when ConnectionStrings:Governance names its database; without it, every /workbench address is not found
/// and nothing else in the site changes.
/// </summary>
public static class WorkbenchSetup
{
    public const string PathPrefix = "/workbench";
    public const string PagesFolder = "/Workbench";

    public static bool Enabled(IConfiguration cfg) => !string.IsNullOrWhiteSpace(cfg.GetConnectionString("Governance"));

    public static bool IsWorkbenchPath(PathString path) => path.StartsWithSegments(PathPrefix, StringComparison.OrdinalIgnoreCase);

    public static void AddWorkbench(this WebApplicationBuilder builder)
    {
        var cfg = builder.Configuration;
        var env = builder.Environment;
        var services = builder.Services;
        var on = Enabled(cfg);
        var actAs = cfg.GetValue<bool>("Testing:AllowActAs");
        if (actAs && !env.IsDevelopment())
            throw new InvalidOperationException("Testing:AllowActAs works only in Development. Turn it off.");

        services.AddSingleton(new WorkbenchState(on, cfg["Workbench:Tenant"]));
        services.AddSingleton(new TestingOptions(false, actAs && env.IsDevelopment()));
        services.AddScoped<WebPrincipal>();
        /* Copilot seats by line of business for Insights' reservation pages; with the Workbench off they keep the leader-based register. */
        services.AddScoped<WorkbenchSeats>();
        services.AddScoped<WorkbenchLines>();
        services.AddHostedService<SeatModelSwitch>();

        /* The page rules, by the role the Workbench page asks for (Policies). Registered either way, so the pages
           compile into one site; with the Workbench off, no request reaches them. */
        services.AddScoped<IAuthorizationHandler, AppRoleHandler>();
        services.AddScoped<IAuthorizationHandler, PlatformGovernanceHandler>();
        services.AddScoped<IAuthorizationHandler, SeatReadHandler>();
        services.AddAuthorization(o =>
        {
            o.AddPolicy(Policies.SeatRead, b => b.RequireAuthenticatedUser().AddRequirements(new SeatReadRequirement()));
            o.AddPolicy(Policies.Central, b => b.RequireAuthenticatedUser().AddRequirements(new AppRoleRequirement(AppRoles.Central, AppRoles.Admin)));
            o.AddPolicy(Policies.Operator, b => b.RequireAuthenticatedUser().AddRequirements(new AppRoleRequirement(AppRoles.Operator)));
            o.AddPolicy(Policies.Admin, b => b.RequireAuthenticatedUser().AddRequirements(new AppRoleRequirement(AppRoles.Admin)));
            o.AddPolicy(Policies.PlatformGovernance, b => b.RequireAuthenticatedUser().AddRequirements(new PlatformGovernanceRequirement()));
            o.AddPolicy(Policies.Auditor, b => b.RequireAuthenticatedUser().AddRequirements(new AppRoleRequirement(AppRoles.Auditor, AppRoles.Central)));
            o.AddPolicy(Policies.Ownership, b => b.RequireAuthenticatedUser().AddRequirements(new AppRoleRequirement(AppRoles.User, AppRoles.Central, AppRoles.Admin)));
            o.AddPolicy(Policies.Registry, b => b.RequireAuthenticatedUser().AddRequirements(new AppRoleRequirement(AppRoles.Registry, AppRoles.Central, AppRoles.Admin)));
        });
        services.Configure<RazorPagesOptions>(o =>
        {
            foreach (var page in new[] { "Admin/Integrations", "Admin/Notifications", "Admin/Sla", "Admin/Workflows" })
                o.Conventions.AuthorizePage($"{PagesFolder}/{page}", Policies.Admin);
            foreach (var page in new[] { "Admin/Raci", "Admin/Intake", "Admin/Roles", "Admin/Text", "Admin/SeatGroups", "Admin/Settings" })
                o.Conventions.AuthorizePage($"{PagesFolder}/{page}", Policies.PlatformGovernance);
            o.Conventions.AuthorizeFolder($"{PagesFolder}/Operations", Policies.Central);
            o.Conventions.AuthorizeFolder($"{PagesFolder}/Queues", Policies.Central);
            o.Conventions.AuthorizeFolder($"{PagesFolder}/Reports", Policies.Auditor);
            /* The two everyday sections, each granted in Admin › Access like every other section. */
            foreach (var page in new[] { "Start", "MyWork", "Me", "Recertification", "Exceptions", "Retirement", "Evidence" })
                o.Conventions.AuthorizePage($"{PagesFolder}/{page}", Policies.Ownership);
            o.Conventions.AuthorizeFolder($"{PagesFolder}/Requests", Policies.Ownership);
            o.Conventions.AuthorizeFolder($"{PagesFolder}/Events", Policies.Ownership);
            o.Conventions.AuthorizeFolder($"{PagesFolder}/Registry", Policies.Registry);
            /* The person pickers' directory search: registry editors, central roles and administrators (Workbench settings, Workbench roles). */
            o.Conventions.AuthorizePage($"{PagesFolder}/People", Policies.Registry);
        });
        services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.AddFixedWindowLimiter("sync", w => { w.PermitLimit = 2; w.Window = TimeSpan.FromMinutes(5); w.QueueLimit = 0; });
            o.AddFixedWindowLimiter("upload", w => { w.PermitLimit = 30; w.Window = TimeSpan.FromMinutes(5); w.QueueLimit = 0; });
        });
        services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(o => o.MultipartBodyLengthLimit = 25 * 1024 * 1024);
        if (!on) return;

        services.Configure<DbOptions>(o =>
        {
            o.ConnectionString = cfg.GetConnectionString("Governance") ?? "";
            o.CommandTimeoutSeconds = cfg.GetValue("Database:CommandTimeoutSeconds", 60);
        });
        services.AddGovernanceInfrastructure(cfg);
        /* The seat read run from this site takes Copilot activity from the Insights database it already reads. */
        if (string.IsNullOrWhiteSpace(cfg.GetConnectionString("Insights")) && cfg["Insights:ConnectionString"] is { Length: > 0 } insights)
            services.AddSingleton<Governance.Application.Integrations.IInsightsActivitySource>(new Governance.Application.Integrations.InsightsActivitySource(insights));
        services.AddScoped<ICurrentPrincipal>(sp => sp.GetRequiredService<WebPrincipal>());
        services.AddScoped<PrincipalLoader>();
        services.AddScoped<CommandRunner>();
        services.AddScoped<DecisionRecorder>();
        services.AddScoped<EvidenceStore>();
        services.AddScoped<EvidenceQueries>();
        services.AddScoped<RequestCommands>();
        services.AddScoped<RegistryCommands>();
        services.AddScoped<AdminCommands>();
        services.AddScoped<OperationsCommands>();
        services.AddScoped<SeatCommands>();
        services.AddScoped<WorkQueries>();
        services.AddScoped<RegistryQueries>();
        services.AddScoped<OperationsQueries>();
        services.AddScoped<ReportQueries>();
        services.AddScoped<AdminQueries>();
        services.AddScoped<AccessQueries>();
        services.AddScoped<SeatQueries>();
        services.AddScoped<ReservationImport>();
        services.AddScoped<WorkbenchMode>();
        services.AddSingleton<HelpText>();
    }

    /// <summary>
    /// Between authentication and authorization: with the Workbench off, its addresses are not found; with it on, the
    /// person behind a Workbench request is loaded before the page rules are checked.
    /// </summary>
    public static void UseWorkbench(this WebApplication app)
    {
        var on = app.Services.GetRequiredService<WorkbenchState>().On;
        app.UseWhen(ctx => IsWorkbenchPath(ctx.Request.Path), b =>
        {
            if (!on)
            {
                b.Run(ctx => { ctx.Response.StatusCode = StatusCodes.Status404NotFound; return Task.CompletedTask; });
                return;
            }
            b.UseMiddleware<PrincipalMiddleware>();
            /* Copilot seats only (Workbench.Scope): the other pages are closed. */
            b.UseMiddleware<WorkbenchScopeMiddleware>();
        });
        app.UseRateLimiter();
    }

    /// <summary>
    /// In place of the site's tenant choice for a Workbench address: always the Workbench's own tenant, for a form post
    /// too (its forms name no tenant, and need none).
    /// </summary>
    public static Task SelectTenantAsync(HttpContext ctx, Func<Task> next)
    {
        if (!IsWorkbenchPath(ctx.Request.Path)) return CopilotInsights.Infrastructure.TenantAccessor.SelectAsync(ctx, next);
        var tenants = ctx.RequestServices.GetRequiredService<CopilotInsights.Infrastructure.TenantAccessor>();
        tenants.Use(ctx.RequestServices.GetRequiredService<WorkbenchState>().Home(tenants));
        return next();
    }
}

public static class WorkbenchStatusPages
{
    const string DeniedKey = "workbench-denied";

    /// <summary>
    /// Before routing: a Workbench page refused (403) is asked again as /workbench/AccessDenied, which says why and who
    /// can grant it; the answer stays 403. Pages only: not a form post, not a file.
    /// </summary>
    public static void UseWorkbenchStatusPages(this WebApplication app) => app.Use(async (ctx, next) =>
    {
        await next();
        var r = ctx.Response;
        if (r.StatusCode != StatusCodes.Status403Forbidden || r.HasStarted || r.ContentLength is > 0 || !HttpMethods.IsGet(ctx.Request.Method)
            || !WorkbenchSetup.IsWorkbenchPath(ctx.Request.Path) || ctx.Items.ContainsKey(DeniedKey)) return;
        ctx.Items[DeniedKey] = ctx.Request.Path.Value;
        ctx.SetEndpoint(null);
        ctx.Features.Get<Microsoft.AspNetCore.Http.Features.IRouteValuesFeature>()?.RouteValues?.Clear();
        ctx.Request.Path = WorkbenchSetup.PathPrefix + "/AccessDenied";
        ctx.Request.QueryString = QueryString.Empty;
        r.StatusCode = StatusCodes.Status403Forbidden;   // kept: the page sets no status of its own
        await next();
    });
}

/// <summary>
/// Whether the Workbench is set up on this site, and the one tenant it belongs to (Workbench:Tenant, else the first):
/// it has one database, so its pages, its roles and the seats on Insights' pages are that tenant's alone.
/// </summary>
public sealed record WorkbenchState(bool On, string? Tenant = null)
{
    public CopilotInsights.Infrastructure.TenantOption Home(CopilotInsights.Infrastructure.TenantAccessor tenants) =>
        tenants.Find(Tenant) ?? tenants.All[0];

    /// <summary>On, and the request is in the Workbench's tenant.</summary>
    public bool OnFor(CopilotInsights.Infrastructure.TenantAccessor tenants) =>
        On && string.Equals(tenants.Current.Name, Home(tenants).Name, StringComparison.OrdinalIgnoreCase);
}
