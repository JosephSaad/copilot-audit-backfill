using Dapper;
using Governance.Application;
using Governance.Application.Data;
using Microsoft.Extensions.Options;

namespace Governance.Web.Infrastructure;

/// <summary>
/// The lines of business as the Governance Workbench keeps them (core.FundingOwner, by code), for Copilot Insights'
/// pages that charge to a line of business. With the Workbench on, a credit route's Org ID is a funding owner's code:
/// its name, whether it is known, who sees it and whose agents are in it come from the Workbench. Off, or in another
/// tenant: <see cref="On"/> is false and the pages keep the reservation register.
/// </summary>
public sealed class WorkbenchLines(IServiceProvider services, WorkbenchState state, CopilotInsights.Infrastructure.TenantAccessor tenants, IHttpContextAccessor http)
{
    public bool On => state.OnFor(tenants);

    /// <summary>Reads every line of business: the Workbench's own system reader, never a decider.</summary>
    Db Reader() => new(services.GetRequiredService<IOptions<DbOptions>>(), SystemPrincipal.Instance);

    Dictionary<string, string>? _names;
    HashSet<string>? _visible;

    /// <summary>Each line of business by its code: active and suspended funding owners. Read once a request.</summary>
    public async Task<Dictionary<string, string>> NamesAsync()
    {
        if (_names is not null) return _names;
        await using var c = await Reader().OpenAsync(0, true, CancellationToken.None);
        return _names = (await c.QueryAsync<(string Code, string Name)>("SELECT Code, Name FROM core.FundingOwner WHERE Status IN ('ACTIVE', 'SUSPENDED');"))
            .ToDictionary(x => x.Code, x => x.Name, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The lines of business a viewer of part of the organization sees: as the Workbench's seat pages scope them
    /// (SeatAccess.Scope: every line for a central seat viewer, else the lines their seat roles cover; a delegation to
    /// approve is not one). Null: every line. Someone the Workbench does not know sees none. Read once a request.
    /// </summary>
    public async Task<HashSet<string>?> VisibleCodesAsync()
    {
        if (_visible is not null) return _visible;
        var ctx = http.HttpContext;
        var me = ctx is null ? null : await services.GetRequiredService<WorkbenchSeats>().PersonAsync(ctx);
        if (me is null) return _visible = new(StringComparer.OrdinalIgnoreCase);
        if (Governance.Application.Queries.SeatAccess.Scope(me) is not { } ids) return null;
        if (ids.Length == 0) return _visible = new(StringComparer.OrdinalIgnoreCase);
        await using var c = await Reader().OpenAsync(0, true, CancellationToken.None);
        return _visible = (await c.QueryAsync<string>("SELECT Code FROM core.FundingOwner WHERE FundingOwnerId IN @ids;", new { ids })).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Each person's line of business by the tracking group that holds their Copilot seat today, by UPN. Kept ten
    /// minutes: the groups are read once a day.
    /// </summary>
    public async Task<Dictionary<string, string>> LineByUpnAsync()
    {
        var cache = services.GetRequiredService<Microsoft.Extensions.Caching.Memory.IMemoryCache>();
        var key = "workbench-line-by-upn:" + tenants.Current.Name;
        if (Microsoft.Extensions.Caching.Memory.CacheExtensions.TryGetValue(cache, key, out Dictionary<string, string>? known) && known is not null) return known;
        var found = await ReadLineByUpnAsync();
        Microsoft.Extensions.Caching.Memory.CacheExtensions.Set(cache, key, found, TimeSpan.FromMinutes(10));
        return found;
    }

    async Task<Dictionary<string, string>> ReadLineByUpnAsync()
    {
        await using var c = await Reader().OpenAsync(0, true, CancellationToken.None);
        var rows = await c.QueryAsync<(string Upn, string Code)>(
            """
            DECLARE @today date = CAST(SYSUTCDATETIME() AS date);
            SELECT p.UserPrincipalName, fo.Code
            FROM intg.GroupMember AS gm
            JOIN core.SeatGroup AS g ON g.EntraGroupId = gm.GroupId AND g.Purpose = 'LOB_TRACKING'
                 AND g.EffectiveFrom <= @today AND (g.EffectiveTo IS NULL OR g.EffectiveTo >= @today)
            JOIN core.FundingOwner AS fo ON fo.FundingOwnerId = g.FundingOwnerId
            JOIN sec.Person AS p ON p.EntraObjectId = gm.MemberObjectId
            WHERE p.UserPrincipalName IS NOT NULL;
            """);
        /* Someone in two lines of business's groups is a seat finding of its own: placed in neither here. */
        return rows.GroupBy(r => r.Upn, StringComparer.OrdinalIgnoreCase).Where(g => g.Select(x => x.Code).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1)
            .ToDictionary(g => g.Key, g => g.First().Code, StringComparer.OrdinalIgnoreCase);
    }
}
