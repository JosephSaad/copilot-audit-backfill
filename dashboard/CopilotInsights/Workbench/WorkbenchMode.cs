using Governance.Application;
using Governance.Application.Data;
using Microsoft.Extensions.Caching.Memory;

namespace Governance.Web.Infrastructure;

/// <summary>
/// What the Workbench is used for on this request (<see cref="WorkbenchScope"/>): read from ref.Setting Workbench.Scope and
/// kept 30 seconds on each web server, so a change on the Settings page reaches every server within half a minute (and
/// this one at once: the page forgets it).
/// </summary>
public sealed class WorkbenchMode(Db db, IMemoryCache cache, ILogger<WorkbenchMode> log)
{
    const string CacheKey = "workbench-scope";

    public static void Forget(IMemoryCache cache) => cache.Remove(CacheKey);

    /// <summary>Copilot seats per line of business only. A database that cannot be read counts as seats only: fewer pages, never more.</summary>
    public async Task<bool> SeatsOnlyAsync()
    {
        if (cache.TryGetValue(CacheKey, out bool known)) return known;
        bool seats;
        try
        {
            await using var c = await db.OpenSystemAsync();
            seats = WorkbenchScope.SeatsOnly(await Dapper.SqlMapper.ExecuteScalarAsync<string?>(c, WorkbenchScope.Sql));
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "The Workbench scope could not be read; the Workbench shows Copilot seats only.");
            return true;   // not kept: the next request reads again
        }
        cache.Set(CacheKey, seats, TimeSpan.FromSeconds(30));
        return seats;
    }
}

public sealed record SeatReadRequirement : Microsoft.AspNetCore.Authorization.IAuthorizationRequirement;

/// <summary>
/// <see cref="Policies.SeatRead"/>: the Operator page role as before; with seats only (decided for the request by
/// <see cref="WorkbenchScopeMiddleware"/>), also Copilot Insights administrators and the Platform Owner.
/// </summary>
public sealed class SeatReadHandler(IHttpContextAccessor http) : Microsoft.AspNetCore.Authorization.AuthorizationHandler<SeatReadRequirement>
{
    public static bool Allows(WebPrincipal p, bool seatsOnly) => p.IsAuthenticated
        && (p.AppRoles.Contains(Governance.Domain.AppRoles.Operator)
            || (seatsOnly && (p.AppRoles.Contains(Governance.Domain.AppRoles.Admin) || p.HasCentral(Governance.Domain.Roles.PlatformOwner))));

    protected override Task HandleRequirementAsync(Microsoft.AspNetCore.Authorization.AuthorizationHandlerContext context, SeatReadRequirement requirement)
    {
        var ctx = http.HttpContext;
        if (ctx?.RequestServices.GetService<WebPrincipal>() is { } p && Allows(p, ctx.Items[WorkbenchScopeMiddleware.ItemKey] is true))
            context.Succeed(requirement);
        return Task.CompletedTask;
    }
}

/// <summary>
/// After the Workbench person is loaded: with seats only, a Workbench address outside <see cref="WorkbenchScope.SeatPages"/>
/// sends a page visit to the seats per line of business, and refuses anything else (a form post, a download) as not found.
/// </summary>
public sealed class WorkbenchScopeMiddleware(RequestDelegate next)
{
    /// <summary>The request's answer, for the pages (<see cref="GovPage.SeatsOnly"/>).</summary>
    public const string ItemKey = "workbench-seats-only";

    public async Task InvokeAsync(HttpContext http, WorkbenchMode mode)
    {
        var seats = await mode.SeatsOnlyAsync();
        http.Items[ItemKey] = seats;
        if (!WorkbenchScope.PageOpen(http.Request.Path.Value, seats))
        {
            if (HttpMethods.IsGet(http.Request.Method) || HttpMethods.IsHead(http.Request.Method))
                http.Response.Redirect(http.Request.PathBase + WorkbenchScope.Home);
            else
                http.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        await next(http);
    }
}

public sealed record SeatReportRequirement : Microsoft.AspNetCore.Authorization.IAuthorizationRequirement;

/// <summary><see cref="Policies.SeatReports"/>: auditors, central roles, Copilot Insights administrators and the Platform Owner.</summary>
public sealed class SeatReportHandler(IHttpContextAccessor http) : Microsoft.AspNetCore.Authorization.AuthorizationHandler<SeatReportRequirement>
{
    public static bool Allows(WebPrincipal p) => p.IsAuthenticated
        && (p.AppRoles.Contains(Governance.Domain.AppRoles.Auditor) || p.AppRoles.Contains(Governance.Domain.AppRoles.Central) || p.AppRoles.Contains(Governance.Domain.AppRoles.Admin)
            || p.HasCentral(Governance.Domain.Roles.PlatformOwner));

    protected override Task HandleRequirementAsync(Microsoft.AspNetCore.Authorization.AuthorizationHandlerContext context, SeatReportRequirement requirement)
    {
        if (http.HttpContext?.RequestServices.GetService<WebPrincipal>() is { } p && Allows(p)) context.Succeed(requirement);
        return Task.CompletedTask;
    }
}
