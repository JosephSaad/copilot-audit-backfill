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

    /// <summary>Per request: ?tenant= (remembered in a cookie) or the cookie, else the first tenant.</summary>
    public static async Task SelectAsync(HttpContext ctx, Func<Task> next)
    {
        var tenants = ctx.RequestServices.GetRequiredService<TenantAccessor>();
        if (tenants.Many)
        {
            var asked = tenants.Find(ctx.Request.Query["tenant"].FirstOrDefault());
            if (asked is not null)
                ctx.Response.Cookies.Append(Cookie, asked.Name, new CookieOptions { HttpOnly = true, SameSite = SameSiteMode.Strict, Secure = ctx.Request.IsHttps, IsEssential = true });
            tenants.Use(asked ?? tenants.Find(ctx.Request.Cookies[Cookie]) ?? tenants.All[0]);
        }
        await next();
    }
}
