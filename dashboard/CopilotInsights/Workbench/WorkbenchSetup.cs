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

        services.AddSingleton(new WorkbenchState(on));
        services.AddSingleton(new TestingOptions(false, actAs && env.IsDevelopment()));
        services.AddScoped<WebPrincipal>();

        /* The page rules, by the role the Workbench page asks for (Policies). Registered either way, so the pages
           compile into one site; with the Workbench off, no request reaches them. */
        services.AddScoped<IAuthorizationHandler, AppRoleHandler>();
        services.AddScoped<IAuthorizationHandler, PlatformGovernanceHandler>();
        services.AddAuthorization(o =>
        {
            o.AddPolicy(Policies.Central, b => b.RequireAuthenticatedUser().AddRequirements(new AppRoleRequirement(AppRoles.Central, AppRoles.Admin)));
            o.AddPolicy(Policies.Operator, b => b.RequireAuthenticatedUser().AddRequirements(new AppRoleRequirement(AppRoles.Operator)));
            o.AddPolicy(Policies.Admin, b => b.RequireAuthenticatedUser().AddRequirements(new AppRoleRequirement(AppRoles.Admin)));
            o.AddPolicy(Policies.PlatformGovernance, b => b.RequireAuthenticatedUser().AddRequirements(new PlatformGovernanceRequirement()));
            o.AddPolicy(Policies.Auditor, b => b.RequireAuthenticatedUser().AddRequirements(new AppRoleRequirement(AppRoles.Auditor, AppRoles.Central)));
        });
        services.Configure<RazorPagesOptions>(o =>
        {
            foreach (var page in new[] { "Admin/Integrations", "Admin/Notifications", "Admin/Sla", "Admin/Workflows" })
                o.Conventions.AuthorizePage($"{PagesFolder}/{page}", Policies.Admin);
            foreach (var page in new[] { "Admin/Raci", "Admin/Intake", "Admin/Roles", "Admin/Text", "Admin/SeatGroups" })
                o.Conventions.AuthorizePage($"{PagesFolder}/{page}", Policies.PlatformGovernance);
            o.Conventions.AuthorizeFolder($"{PagesFolder}/Operations", Policies.Central);
            o.Conventions.AuthorizeFolder($"{PagesFolder}/Queues", Policies.Central);
            o.Conventions.AuthorizeFolder($"{PagesFolder}/Reports", Policies.Auditor);
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
        });
        app.UseRateLimiter();
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

/// <summary>Whether the Workbench is set up on this site.</summary>
public sealed record WorkbenchState(bool On);
