using Governance.Domain;

namespace Governance.Application;

/// <summary>Who is acting. The web builds it from the sign-in and SQL role assignments; the worker uses the system principal.</summary>
public interface ICurrentPrincipal
{
    bool IsAuthenticated { get; }
    /// <summary>The person whose permissions apply (the persona when the test-only Act as switch is on).</summary>
    int PersonId { get; }
    /// <summary>The signed-in person, when different from <see cref="PersonId"/> (Act as).</summary>
    int? RealPersonId { get; }
    string DisplayName { get; }
    bool IsCentral { get; }
    IReadOnlyList<RoleGrant> Roles { get; }
    IReadOnlySet<string> AppRoles { get; }
    IReadOnlySet<string> Permissions { get; }
    IReadOnlyDictionary<int, IReadOnlySet<string>> ScopedPermissions { get; }
}

public static class PrincipalExtensions
{
    public static bool HasRole(this ICurrentPrincipal p, string roleCode) => p.Roles.Any(r => r.RoleCode == roleCode);

    public static bool HasRoleIn(this ICurrentPrincipal p, string roleCode, int fundingOwnerId)
        => p.Roles.Any(r => r.RoleCode == roleCode && (r.ScopeType == ScopeTypes.Tenant || r.ScopeId == fundingOwnerId));

    /// <summary>Tenant-wide permission, or the permission held through a role scoped to this funding owner.</summary>
    public static bool Can(this ICurrentPrincipal p, string permission, int? fundingOwnerId = null)
    {
        if (p.Permissions.Contains(permission)) return true;
        return fundingOwnerId is int fo && p.ScopedPermissions.TryGetValue(fo, out var set) && set.Contains(permission);
    }

    public static IEnumerable<int> FundingOwnersWith(this ICurrentPrincipal p, string permission)
        => p.ScopedPermissions.Where(kv => kv.Value.Contains(permission)).Select(kv => kv.Key);
}

/// <summary>The worker's identity: central scope, never a decider.</summary>
public sealed class SystemPrincipal : ICurrentPrincipal
{
    public static readonly SystemPrincipal Instance = new();
    public bool IsAuthenticated => true;
    public int PersonId => 0;
    public int? RealPersonId => null;
    public string DisplayName => "Workbench";
    public bool IsCentral => true;
    public IReadOnlyList<RoleGrant> Roles { get; } = Array.Empty<RoleGrant>();
    public IReadOnlySet<string> AppRoles { get; } = new HashSet<string>();
    public IReadOnlySet<string> Permissions { get; } = new HashSet<string>();
    public IReadOnlyDictionary<int, IReadOnlySet<string>> ScopedPermissions { get; } = new Dictionary<int, IReadOnlySet<string>>();
}

public interface IClock
{
    DateTime UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public DateTime UtcNow => DateTime.UtcNow;
}
