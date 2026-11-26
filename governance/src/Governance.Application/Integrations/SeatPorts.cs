namespace Governance.Application.Integrations;

/// <summary>
/// Microsoft Graph reads for Copilot seat licenses (X13), app-only and read-only:
/// LicenseAssignment.Read.All (subscribed SKUs, users' license states), GroupMember.Read.All (seat groups, unless they are
/// read from on-premises AD: <see cref="IAdGroupApi"/>) and Reports.Read.All (Copilot usage report).
/// </summary>
public interface ISeatLicenseApi
{
    /// <summary>The tenant's subscribed SKUs: purchased (enabled) and assigned (consumed) units.</summary>
    Task<IReadOnlyList<SubscribedSkuData>> SubscribedSkusAsync(CancellationToken ct);

    /// <summary>A group by exact display name (null when none). Two groups with the same name return the first and say so in <see cref="SeatGroupData.DuplicateName"/>.</summary>
    Task<SeatGroupData?> FindGroupAsync(string displayName, CancellationToken ct);

    /// <summary>The group by ID (null when deleted).</summary>
    Task<SeatGroupData?> GetGroupAsync(Guid groupId, CancellationToken ct);

    /// <summary>Direct members split into users and nested groups (group-based licensing ignores nested members).</summary>
    Task<GroupMembersData> GroupMembersAsync(Guid groupId, CancellationToken ct);

    /// <summary>Every user with <paramref name="skuId"/> assigned, with their license assignment states.</summary>
    Task<IReadOnlyList<LicensedUserData>> UsersWithSkuAsync(Guid skuId, CancellationToken ct);

    /// <summary>The Microsoft 365 Copilot usage report (latest activity per user) for a period such as D90.</summary>
    Task<CopilotUsageData> CopilotUsageAsync(string period, CancellationToken ct);
}

public sealed record SubscribedSkuData(Guid SkuId, string SkuPartNumber, int Enabled, int Warning, int Suspended, int Consumed);

public sealed record SeatGroupData(Guid Id, string DisplayName, bool? OnPremisesSyncEnabled, IReadOnlyList<Guid> LicensedSkuIds, bool DuplicateName = false);

public sealed record GroupMembersData(IReadOnlyList<Guid> UserIds, int NestedGroups);

public sealed record LicensedUserData(Guid Id, string? UserPrincipalName, string? UsageLocation, IReadOnlyList<LicenseStateData> States);

/// <summary>One licenseAssignmentState. AssignedByGroup null means a direct assignment.</summary>
public sealed record LicenseStateData(Guid SkuId, Guid? AssignedByGroup, string? State, string? Error, DateTime? LastUpdatedUtc);

/// <summary>
/// The usage report. When the tenant conceals user names in reports, the user principal names are hashed:
/// <see cref="NamesConcealed"/> is true and <see cref="Users"/> is empty (hashes cannot be matched to people).
/// </summary>
public sealed record CopilotUsageData(IReadOnlyList<CopilotUsageRow> Users, DateTime? ReportRefreshDate, bool NamesConcealed);

public sealed record CopilotUsageRow(string UserPrincipalName, DateTime? LastActivityDate);
