using System.Text.Json;

namespace Governance.Application.Integrations;

// Ports the sync services use. Infrastructure implements them over HTTP; contract tests feed them recorded
// responses. Everything is read-only: the Workbench never writes to Microsoft services (D3).

/// <summary>Microsoft Graph directory reads (app-only, User.Read.All and GroupMember.Read.All).</summary>
public interface IDirectoryApi
{
    /// <summary>One page of the users delta query. Pass the saved delta or next link, or null for a full sync.</summary>
    Task<DeltaPage<DirectoryUser>> UsersDeltaAsync(string? link, CancellationToken ct);
    Task<IReadOnlyList<Guid>> GroupMemberIdsAsync(Guid groupId, CancellationToken ct);
}

public sealed record DeltaPage<T>(IReadOnlyList<T> Items, string? NextLink, string? DeltaLink);

public sealed record DirectoryUser(
    Guid Id,
    bool Removed,
    string? DisplayName,
    string? UserPrincipalName,
    string? Mail,
    string? JobTitle,
    string? Department,
    bool? AccountEnabled,
    string? Division,
    string? CostCenter,
    DateTime? LeaveDateTime,
    Guid? ManagerId);

/// <summary>Entra entitlement management reads (catalog reader role on the governed catalog).</summary>
public interface IEntitlementApi
{
    Task<IReadOnlyList<AccessPackageAssignmentData>> AssignmentsAsync(Guid accessPackageId, CancellationToken ct);
    Task<IReadOnlyList<AssignmentRequestData>> AssignmentRequestsAsync(Guid accessPackageId, CancellationToken ct);
}

public sealed record AccessPackageAssignmentData(Guid Id, Guid AccessPackageId, Guid? TargetObjectId, string? TargetPrincipalName, string State, DateTime? ExpiresUtc, DateTime? CreatedUtc);
public sealed record AssignmentRequestData(Guid Id, Guid? AssignmentId, Guid? TargetObjectId, string RequestType, string State, string? Justification, DateTime? CreatedUtc, DateTime? CompletedUtc);

/// <summary>Entra access review reads (AccessReview.Read.All).</summary>
public interface IAccessReviewApi
{
    Task<IReadOnlyList<string>> InstanceIdsAsync(string definitionId, CancellationToken ct);
    Task<IReadOnlyList<AccessReviewDecisionData>> DecisionsAsync(string definitionId, string instanceId, CancellationToken ct);
}

public sealed record AccessReviewDecisionData(string Id, Guid? PrincipalId, string Decision, Guid? ReviewedById, DateTime? ReviewedUtc, string? Justification);

/// <summary>Power Platform API reads. A null token means app-only (Power Platform reader role); otherwise the operator's delegated token.</summary>
public interface IPowerPlatformApi
{
    Task<ResourceQueryPage> QueryResourcesAsync(string? delegatedToken, string body, CancellationToken ct);
    Task<AllocationData?> GetAllocationAsync(string? delegatedToken, string environmentId, CancellationToken ct);
    Task<IReadOnlyDictionary<string, string>> EnvironmentBillingPoliciesAsync(string? delegatedToken, CancellationToken ct);
}

public sealed record ResourceQueryPage(IReadOnlyList<JsonElement> Data, string? SkipToken, long? TotalRecords);

public sealed record AllocationData(string EnvironmentId, decimal? AllocatedCredits, bool? TenantPoolEnabled, decimal? ConsumedCredits, bool? TenantPoolLockedByGroupRule, string RawJson);

/// <summary>Teams activity feed to many recipients in one call (up to 100 each).</summary>
public interface ITeamsActivityBulkSender
{
    Task SendBulkAsync(IReadOnlyList<Guid> recipients, string activityType, string previewText, string itemPublicPath, IReadOnlyDictionary<string, string> templateParameters, CancellationToken ct);
}

/// <summary>A delivery that retrying cannot fix (for example an unknown Teams queue): the outbox row is dead-lettered at once.</summary>
public sealed class PermanentDeliveryException(string message) : Exception(message);

/// <summary>Thrown by adapters for a permission error (401/403), so jobs record the integration as failing with a clear note.</summary>
public sealed class IntegrationPermissionException(string integration, int status) : Exception($"{integration} refused the call (HTTP {status}). Check the permission or role in the permission matrix (section 6.5).")
{
    public string Integration { get; } = integration;
    public int Status { get; } = status;
}
