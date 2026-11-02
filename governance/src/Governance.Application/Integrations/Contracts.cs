namespace Governance.Application.Integrations;

// Contracts between the application and the outside world. Implementations live in Governance.Infrastructure
// (real Microsoft and ITSM adapters) and in tests (recorded responses). All calls are outbound (P6).

/// <summary>X4: Power Platform inventory, delegated only: the token belongs to the operator who pressed Run.</summary>
public interface IInventorySync
{
    /// <summary>Runs the inventory query with the operator's token, stores a snapshot, raises findings. Returns the sync run's public ID.</summary>
    Task<Guid> RunAsync(string delegatedAccessToken, int operatorPersonId, CancellationToken ct);
}

/// <summary>X5: licensing read (allocations, tenant-pool rule, billing policy) for governed environments.</summary>
public interface ILicensingRead
{
    /// <summary>App-only when the worker holds the Power Platform reader role; otherwise pass the operator's token.</summary>
    Task<Guid> RunAsync(string? delegatedAccessToken, int? operatorPersonId, CancellationToken ct);
}

/// <summary>X7: Agent Registry export uploaded by an operator.</summary>
public interface IRegistryImport
{
    Task<Guid> ImportAsync(Stream csv, string fileName, int operatorPersonId, CancellationToken ct);
}

/// <summary>X8: ITSM fulfillment tasks and incidents. Search by correlation ID before create, so a retry never duplicates.</summary>
public interface IItsmClient
{
    Task<ItsmTicketRef?> FindByCorrelationAsync(Guid correlationId, CancellationToken ct);
    Task<ItsmTicketRef> CreateAsync(ItsmCreate request, CancellationToken ct);
    Task<ItsmTicketState> GetAsync(string externalNumber, CancellationToken ct);
}

public sealed record ItsmCreate(Guid CorrelationId, string Kind, string ShortDescription, string Description, string AssignmentGroup, string Urgency, string PayloadJson);
public sealed record ItsmTicketRef(string Number, string? Url, string State);
public sealed record ItsmTicketState(string Number, string State, string? ClosedByUpn, string? ClosureNotes, DateTime? ClosedUtc);

/// <summary>X9: email from the one scoped notification mailbox (Graph sendMail), with SMTP fallback.</summary>
public interface IMailSender
{
    Task SendAsync(MailMessageData message, CancellationToken ct);
}

public sealed record MailMessageData(IReadOnlyList<string> To, string Subject, string Html, string Text, string? CorrelationId);

/// <summary>X10: Teams activity feed notification to a person (Teams app installed, resource-specific consent).</summary>
public interface ITeamsActivitySender
{
    Task SendAsync(Guid recipientEntraObjectId, string activityType, string previewText, string itemPublicPath, IReadOnlyDictionary<string, string> templateParameters, CancellationToken ct);
}

/// <summary>X11: Adaptive Card to a team queue's channel through the OAuth-protected Power Automate flow.</summary>
public interface IChannelPoster
{
    bool IsConfigured { get; }
    Task PostAsync(string queue, string title, string text, string linkUrl, CancellationToken ct);
}

/// <summary>Upload scanning by the institution's anti-malware service before evidence is accepted.</summary>
public interface IEvidenceScanner
{
    Task<ScanResult> ScanAsync(Stream content, string fileName, CancellationToken ct);
}

public sealed record ScanResult(bool Clean, string? Reason);

/// <summary>Health of each integration for the job dashboard and the freshness badges (H10).</summary>
public interface IIntegrationStatus
{
    Task<IReadOnlyList<IntegrationHealth>> GetAsync(CancellationToken ct);
}

public sealed record IntegrationHealth(string Code, string Name, string Mode, DateTime? LastSuccessUtc, DateTime? LastRunUtc, string? LastStatus, bool Stale, string? Note);

/// <summary>Power Platform API scope requested incrementally for operator-run reads.</summary>
public static class PowerPlatformScopes
{
    public const string Default = "https://api.powerplatform.com/.default";
    public static readonly string[] Delegated =
    {
        "https://api.powerplatform.com/ResourceQuery.Resources.Read",
        "https://api.powerplatform.com/EnvironmentManagement.Environments.Read",
        "https://api.powerplatform.com/Licensing.Allocations.Read",
        "https://api.powerplatform.com/Licensing.BillingPolicies.Read",
    };
}
