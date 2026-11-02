using System.Text.Json;
using System.Text.Json.Serialization;

namespace Governance.Application.Requests;

/// <summary>
/// The answers a requester gives in the journey wizard, stored as JSON on wf.Request. One shape for every
/// journey; each journey uses its own fields. Classification (B3) is stored on the workload, not here.
/// </summary>
public sealed class RequestPayload
{
    // Common
    public string? Purpose { get; set; }
    public string? BusinessOutcome { get; set; }
    public Guid? ExistingWorkloadPublicId { get; set; }
    public bool MaterialChange { get; set; }

    // J2 new workload (and the requester's view of what it is)
    public string? WorkloadName { get; set; }
    public string? WorkloadType { get; set; }          // AGENT, ENVIRONMENT, AGENT_FLOW, SERVICE_ENTITLEMENT, OTHER
    public string? ServiceCode { get; set; }
    public string? Audience { get; set; }              // EMPLOYEES_TEAM, EMPLOYEES_LOB, EMPLOYEES_ALL, CUSTOMERS
    public string? DataClass { get; set; }
    public string? ActionScope { get; set; }           // READ_ONLY, INTERNAL_ACTIONS, EXTERNAL_ACTIONS
    public string? RequestedLifecycleStage { get; set; }
    public string? TriggerType { get; set; }
    public string? Tier { get; set; }
    public string? EnvironmentClassCode { get; set; }
    public decimal? EstimateCreditsPerMonth { get; set; }
    public int? DurationMonths { get; set; }
    public bool CustomerFacing { get; set; }
    public bool NewConnectorOrIntegration { get; set; }
    public bool NewBillingScope { get; set; }
    public bool UsesGitHubHarness { get; set; }
    public List<string> Meters { get; set; } = new();
    public string? SupportContact { get; set; }

    // J3 exception
    public string? RequestedDeviation { get; set; }
    public string? ProposedCompensatingControls { get; set; }
    public DateTime? RequestedExpiry { get; set; }

    // J4 capacity or limit change
    public string? ChangeKind { get; set; }            // ALLOCATION, AGENT_LIMIT, ENVELOPE
    public string? EnvironmentId { get; set; }
    public decimal? CurrentValue { get; set; }
    public decimal? RequestedValue { get; set; }
    public bool RequestTenantPool { get; set; }

    // J7 retirement
    public string? RetirementReason { get; set; }
    public string? RetirementPlan { get; set; }
    public string? ReplacementWorkload { get; set; }

    // J8 commercial decision
    public string? CommercialKind { get; set; }        // P3_PURCHASE, PACK_PURCHASE, RENEWAL, CRITICAL_SHARED_FUNDING
    public decimal? Quantity { get; set; }
    public string? Unit { get; set; }
    public Guid? CommercialItemPublicId { get; set; }
    public string? Rationale { get; set; }

    // J9 Copilot seat reservation (seats, never credits)
    public string? SeatChangeKind { get; set; }        // NEW, INCREASE, TRUE_DOWN (SeatChangeKinds); set by the engine
    public int? CurrentSeats { get; set; }             // the reservation in effect, filled by the engine (not the form)
    public int? RequestedSeats { get; set; }           // the LOB's new total reservation
    public string? SeatDemand { get; set; }            // who needs the seats and why (or why seats can go)
    public string? FlexPoolReview { get; set; }        // recorded by the Finance approver at S4 (CAL-06)

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static RequestPayload FromJson(string? json)
        => string.IsNullOrWhiteSpace(json) ? new RequestPayload() : JsonSerializer.Deserialize<RequestPayload>(json, Json) ?? new RequestPayload();
}

public static class Audiences
{
    public const string Team = "EMPLOYEES_TEAM";
    public const string Lob = "EMPLOYEES_LOB";
    public const string AllEmployees = "EMPLOYEES_ALL";
    public const string Customers = "CUSTOMERS";

    public static readonly IReadOnlyDictionary<string, string> Labels = new Dictionary<string, string>
    {
        [Team] = "My team",
        [Lob] = "My line of business",
        [AllEmployees] = "All employees",
        [Customers] = "Customers (external)",
    };
}

public static class ActionScopes
{
    public const string ReadOnly = "READ_ONLY";
    public const string Internal = "INTERNAL_ACTIONS";
    public const string External = "EXTERNAL_ACTIONS";

    public static readonly IReadOnlyDictionary<string, string> Labels = new Dictionary<string, string>
    {
        [ReadOnly] = "Reads and answers only",
        [Internal] = "Takes actions in internal systems",
        [External] = "Takes actions outside the institution",
    };
}
