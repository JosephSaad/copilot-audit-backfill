using Microsoft.Extensions.Options;

namespace CopilotInsights.Infrastructure;

/// <summary>One Microsoft 365 tenant the dashboard reports on: its name and its Copilot database.</summary>
public sealed class TenantOption
{
    public string Name { get; set; } = "";
    public string ConnectionString { get; set; } = "";
}

/// <summary>
/// Which tenant's database this request (or this round of background checks) uses. Each tenant has
/// its own Copilot database, loaded by its own app registration; the dashboard spans them. With no
/// Insights:Tenants configured there is one, from Insights:ConnectionString, and nothing changes.
/// The tenant is a view choice, not an identity: who the viewer is, and what they may see, is
/// worked out again in each tenant's database from their Windows sign-in.
/// </summary>
public sealed class TenantAccessor
{
    readonly IReadOnlyList<TenantOption> _all;
    readonly AsyncLocal<TenantOption?> _current = new();

    public TenantAccessor(IOptions<InsightsOptions> options)
    {
        var o = options.Value;
        _all = o.Tenants.Count > 0
            ? o.Tenants.Where(t => !string.IsNullOrWhiteSpace(t.Name) && !string.IsNullOrWhiteSpace(t.ConnectionString)).ToList()
            : [new TenantOption { Name = "Default", ConnectionString = o.ConnectionString }];
        if (_all.Count == 0) throw new InvalidOperationException("Insights:Tenants lists no tenant with a name and a connection string.");
    }

    public IReadOnlyList<TenantOption> All => _all;
    public bool Many => _all.Count > 1;
    public TenantOption Current => _current.Value ?? _all[0];

    /// <summary>The tenant by name (case-insensitive), or null.</summary>
    public TenantOption? Find(string? name) =>
        string.IsNullOrWhiteSpace(name) ? null : _all.FirstOrDefault(t => t.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>Use this tenant for the rest of the current async flow.</summary>
    public void Use(TenantOption tenant) => _current.Value = tenant;

    /// <summary>A cache key that cannot collide across tenants.</summary>
    public string Key(string key) => Many ? Current.Name + "|" + key : key;

    public const string Cookie = "insights-tenant";

    /// <summary>
    /// What a form's address carries so it posts to the tenant its page showed (2.4.12): "tenant=Name" with several
    /// tenants, else null.
    /// </summary>
    public string? FormQuery => Many ? "tenant=" + Uri.EscapeDataString(Current.Name) : null;

    /// <summary>
    /// Per request: ?tenant= (remembered in a cookie) or the cookie, else the first tenant. A form names its tenant
    /// (2.4.12): a post goes to the tenant in its address, or nowhere, and never changes the cookie. Before, it went to
    /// whichever tenant the cookie named when it was sent, so with tenant B chosen in another tab, Remove on tenant A's
    /// Admin › Access page removed B's grant with that number.
    /// </summary>
    public static async Task SelectAsync(HttpContext ctx, Func<Task> next)
    {
        var tenants = ctx.RequestServices.GetRequiredService<TenantAccessor>();
        if (tenants.Many)
        {
            var asked = tenants.Find(ctx.Request.Query["tenant"].FirstOrDefault());
            if (!HttpMethods.IsGet(ctx.Request.Method) && !HttpMethods.IsHead(ctx.Request.Method))
            {
                if (asked is null)
                {
                    ctx.Response.StatusCode = StatusCodes.Status409Conflict;
                    ctx.Response.ContentType = "text/plain; charset=utf-8";
                    await ctx.Response.WriteAsync("This form did not say which tenant it was for, so nothing was changed. Open the page again and repeat the change.");
                    return;
                }
                tenants.Use(asked);
                await next();
                return;
            }
            if (asked is not null)
                ctx.Response.Cookies.Append(Cookie, asked.Name, new CookieOptions { HttpOnly = true, SameSite = SameSiteMode.Strict, Secure = ctx.Request.IsHttps, IsEssential = true });
            tenants.Use(asked ?? tenants.Find(ctx.Request.Cookies[Cookie]) ?? tenants.All[0]);
        }
        await next();
    }
}
