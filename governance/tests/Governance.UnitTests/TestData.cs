using Governance.Application.Requests;
using Governance.Application.Rules;
using Governance.Domain;

namespace Governance.UnitTests;

internal static class TestData
{
    public static Service Studio(int id = 1) => new()
    {
        ServiceId = id, Code = "CS_STANDARD", Name = "Copilot Studio (standard harness)", Harness = "Standard", RouteType = "PPAC_ENVIRONMENT",
        TenantAvailability = "GA", InstitutionalStatus = "APPROVED", Audiences = "EMPLOYEES_TEAM,EMPLOYEES_LOB", DataClasses = "INTERNAL", AccessTiers = "Developer",
    };

    public static ApprovedPattern MakerPattern(int serviceId = 1) => new()
    {
        PatternId = 7, Code = "STUDIO-MAKER", Name = "Studio maker", ServiceId = serviceId, Audiences = "EMPLOYEES_TEAM,EMPLOYEES_LOB",
        MaxDataClass = DataClasses.Internal, ActionScopes = "READ_ONLY,INTERNAL_ACTIONS", LifecycleStages = "LOCAL,DEVELOPMENT,TEST",
        TriggerTypes = "USER_INITIATED", Tiers = "*", MaxCreditsPerMonth = 2000, IsActive = true,
    };

    public static RequestPayload MakerPayload() => new()
    {
        Purpose = "Answer product questions.", BusinessOutcome = "Faster answers.", WorkloadName = "Assistant", WorkloadType = "AGENT", ServiceCode = "CS_STANDARD",
        Audience = Audiences.Team, DataClass = DataClasses.Internal, ActionScope = ActionScopes.ReadOnly, RequestedLifecycleStage = LifecycleStages.Development,
        TriggerType = TriggerTypes.UserInitiated, EstimateCreditsPerMonth = 800,
    };

    public static Envelope ActiveEnvelope(decimal amount = 10000, decimal committed = 0, string unit = Units.CopilotCredit, int id = 3) => new()
    {
        EnvelopeId = id, FundingOwnerId = 1, ServiceId = 1, Amount = amount, Committed = committed, Unit = unit, Status = "ACTIVE",
        PeriodStart = new DateTime(2026, 1, 1), PeriodEnd = new DateTime(2026, 12, 31),
    };

    public static EnvelopeHeadroom.Result Headroom(decimal amount = 10000, decimal committed = 0, decimal estimate = 800)
        => EnvelopeHeadroom.Evaluate(ActiveEnvelope(amount, committed), estimate);

    public static PatternFit.Result Fit(RequestPayload p, Service? service = null, EnvelopeHeadroom.Result? envelope = null, string journey = Journeys.NewWorkload,
        Workload? workload = null, PatternFit.EnvironmentLimits? limits = null)
        => PatternFit.Evaluate(new PatternFit.Facts(journey, p, service ?? Studio(), workload, new[] { MakerPattern() }, envelope ?? Headroom(), limits));
}
