using Dapper;
using Governance.Application.Requests;
using Governance.Domain;
using Microsoft.Data.SqlClient;

namespace Governance.Seed;

/// <summary>
/// Requests in every interesting state, each driven through the real engine by the right personas, plus
/// consumption events, findings and an open recertification campaign.
/// </summary>
public sealed class SampleRequests(EngineHost host, SeedResult seed, TextWriter log)
{
    private readonly RequestFlows _f = new(host, seed);
    private DateTime Today => host.Clock.UtcNow.Date;

    public async Task RunAsync()
    {
        if (await ExistingAsync() > 0) { log.WriteLine("Sample requests already exist; skipped."); return; }
        await NewWorkloadCompletedAsync();
        await ProductionAtAcceptanceAsync();
        await ReturnedAtIntakeAsync();
        await ExceptionApprovedAsync();
        await ExceptionOnHoldAsync();
        await LimitIncreaseAsync();
        await RetirementAsync();
        await CommercialRenewalAsync();
        await OperationsAsync();
        log.WriteLine("Sample requests, events, findings and a recertification campaign created.");
    }

    private async Task<int> ExistingAsync()
    {
        await using var c = new SqlConnection(host.ConnectionString);
        return await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM wf.Request");
    }

    public static RequestPayload MakerAgent(string name, string stage = LifecycleStages.Development, decimal estimate = 800) => new()
    {
        Purpose = "Answer branch staff questions about product terms from the internal product library.",
        BusinessOutcome = "Cut average time to answer a product question from 6 minutes to 2.",
        WorkloadName = name, WorkloadType = "AGENT", ServiceCode = "CS_STANDARD", Audience = Audiences.Team, DataClass = DataClasses.Internal,
        ActionScope = ActionScopes.ReadOnly, RequestedLifecycleStage = stage, TriggerType = TriggerTypes.UserInitiated, EnvironmentClassCode = "DEVELOPER",
        EstimateCreditsPerMonth = estimate, DurationMonths = 6, SupportContact = "retail-digital@example.invalid",
    };

    /// <summary>J2: B2, B3, B4 fits, B5 delegated approval, B10, ITSM, D1 pass: Completed, workload in Development.</summary>
    public async Task<long> NewWorkloadCompletedAsync()
    {
        var r = await _f.SubmitAsync("RETAIL.maker1", Journeys.NewWorkload, "Branch product-terms assistant", "RETAIL", "CS_STANDARD", "RETAIL.sponsor", null,
            MakerAgent("Branch product-terms assistant"));
        await _f.CompleteAsync("intake", r.RequestId, Stages.Completeness, StageOutcomes.Complete, "Sponsor and funding owner valid; no duplicate.");
        await _f.ClassifyAsync(r.RequestId, "RB-1100", SolutionClasses.Productivity, LifecycleStages.Development, "LOW", "Internal product library only", "My team");
        await _f.CompleteAsync("architect", r.RequestId, Stages.Classify, StageOutcomes.Classified, "Productivity solution, development, low criticality.");
        await _f.DecideAsync("RETAIL.approver1", r.RequestId, Stages.RoutineApproval, _f.Approve("Copilot Studio standard harness, my team, Developer tier, 800 credits a month for 6 months."));
        await _f.CompleteAsync("intake", r.RequestId, Stages.PrepareFulfillment, StageOutcomes.Ready, "ITSM task prepared from template v1.");
        await _f.CloseTicketAsync(r.RequestId, "operator1");
        await _f.CompleteAsync("operator2", r.RequestId, Stages.Verify, StageOutcomes.Pass, "Read back allocation, tenant pool off, agent limit 2000; deny case tested.");
        return r.RequestId;
    }

    /// <summary>J2 production: specialist review, two B9 decisions, fulfillment, D1, D2 evidence, waiting at D5 with blocking consults.</summary>
    public async Task<long> ProductionAtAcceptanceAsync()
    {
        var p = MakerAgent("Advisor meeting-prep agent", LifecycleStages.Production, 1500);
        p.Purpose = "Prepare advisors for client meetings from CRM notes and research.";
        p.Audience = Audiences.Lob;
        p.EnvironmentClassCode = "PRODUCTION";
        var r = await _f.SubmitAsync("WEALTH.maker1", Journeys.NewWorkload, "Advisor meeting-prep agent", "WEALTH", "CS_STANDARD", "WEALTH.sponsor", null, p);
        await _f.CompleteAsync("intake", r.RequestId, Stages.Completeness, StageOutcomes.Complete);
        await _f.ClassifyAsync(r.RequestId, "WM-2000", SolutionClasses.BusinessApplication, LifecycleStages.Production, "HIGH", "CRM notes and research, internal", "My line of business");
        await _f.CompleteAsync("architect", r.RequestId, Stages.Classify, StageOutcomes.Classified);
        foreach (var t in await _f.OpenTasksAsync(r.RequestId))
        {
            var who = t.Discipline switch { Disciplines.Operations => "operator1", Disciplines.Finance => "finopsAnalyst", Disciplines.SecurityRisk => "risk", _ => "architect" };
            await _f.CompleteAsync(who, r.RequestId, Stages.SpecialistReview, "REVIEWED", "Reviewed; no blocking issue.", discipline: t.Discipline);
        }
        foreach (var t in await _f.OpenTasksAsync(r.RequestId))
        {
            var who = t.AssignedRoleCode == Roles.PlatformOwner ? "platformOwner" : t.AssignedRoleCode == Roles.RiskReviewer ? "risk" : "WEALTH.owner";
            await _f.DecideAsync(who, r.RequestId, Stages.Disposition, _f.Approve($"{DecisionTypes.Label(t.DecisionType!)} for the advisor meeting-prep agent, production, line of business."), t.DecisionType);
        }
        await _f.CompleteAsync("intake", r.RequestId, Stages.PrepareFulfillment, StageOutcomes.Ready);
        await _f.CloseTicketAsync(r.RequestId, "operator1");
        await _f.CompleteAsync("operator2", r.RequestId, Stages.Verify, StageOutcomes.Pass, "Read-back and attestation complete.");
        await _f.CompleteAsync("WEALTH.product", r.RequestId, Stages.BuildAndTest, StageOutcomes.Ready, "Charging, exhaustion, overage, recovery and negative tests uploaded.");
        return r.RequestId;
    }

    /// <summary>J2 returned at B2: waiting at B7 for the requester.</summary>
    public async Task<long> ReturnedAtIntakeAsync()
    {
        var p = MakerAgent("Loan document checker", LifecycleStages.Development, 1200);
        p.BusinessOutcome = null;
        var r = await _f.SubmitAsync("COMMERCIAL.maker1", Journeys.NewWorkload, "Loan document checker", "COMMERCIAL", "CS_STANDARD", "COMMERCIAL.sponsor", null, p);
        await _f.CompleteAsync("intake", r.RequestId, Stages.Completeness, StageOutcomes.Return, "State the measurable business outcome. Confirm the sponsor has agreed.");
        return r.RequestId;
    }

    public static RequestPayload ExceptionPayload(bool customerFacing) => new()
    {
        Purpose = customerFacing ? "Let customers ask about mortgage pre-approval status on the public site." : "Connect an agent to the branch scheduling system.",
        BusinessOutcome = customerFacing ? "Deflect 20 percent of status calls." : "Book branch appointments without a phone call.",
        RequestedDeviation = customerFacing ? "Customer-facing agent on the preview Customer Conversation harness." : "A new connector to the branch scheduling system.",
        ProposedCompensatingControls = "Read-only scope, rate limit, weekly review of transcripts by the product owner.",
        CustomerFacing = customerFacing, NewConnectorOrIntegration = !customerFacing, Audience = customerFacing ? Audiences.Customers : Audiences.Team,
        DataClass = DataClasses.Internal,
    };

    /// <summary>J3 approved with conditions: two reviews, two separate decisions, an exception register row.</summary>
    public async Task<long> ExceptionApprovedAsync()
    {
        var p = ExceptionPayload(false);
        p.RequestedExpiry = Today.AddMonths(6);
        var r = await _f.SubmitAsync("RETAIL.product", Journeys.Exception, "Branch scheduling connector", "RETAIL", "CS_STANDARD", "RETAIL.sponsor", null, p);
        await _f.CompleteAsync("intake", r.RequestId, Stages.Completeness, StageOutcomes.Complete);
        await CompleteReviewsAsync(r.RequestId);
        foreach (var t in await _f.OpenTasksAsync(r.RequestId))
        {
            var who = t.AssignedRoleCode == Roles.RiskReviewer ? "risk" : "platformOwner";
            var input = who == "platformOwner"
                ? _f.Approve("New connector to the branch scheduling system, read-only, Retail Banking makers.", "Read-only scope; weekly transcript review by the product owner.", 3, Today.AddMonths(6))
                : _f.Approve("Risk accepted for the scheduling connector, internal data.", null, 3, Today.AddMonths(6));
            await _f.DecideAsync(who, r.RequestId, Stages.Disposition, input, t.DecisionType);
        }
        return r.RequestId;
    }

    /// <summary>J3 on hold: risk acceptance held; the requester has a task to supply evidence.</summary>
    public async Task<long> ExceptionOnHoldAsync()
    {
        var p = ExceptionPayload(true);
        p.RequestedExpiry = Today.AddMonths(3);
        var r = await _f.SubmitAsync("WEALTH.product", Journeys.Exception, "Mortgage status agent for customers", "WEALTH", "CS_CUSTOMER", "WEALTH.sponsor", null, p);
        await _f.CompleteAsync("intake", r.RequestId, Stages.Completeness, StageOutcomes.Complete);
        await CompleteReviewsAsync(r.RequestId);
        foreach (var t in await _f.OpenTasksAsync(r.RequestId))
        {
            if (t.AssignedRoleCode == Roles.RiskReviewer)
                await _f.DecideAsync("risk", r.RequestId, Stages.Disposition,
                    RequestFlows.Hold("Customer-facing mortgage status agent.", "No privacy impact assessment for customer conversations.", "A privacy impact assessment and the test data plan."), t.DecisionType);
            else
                await _f.DecideAsync("platformOwner", r.RequestId, Stages.Disposition, _f.Approve($"{DecisionTypes.Label(t.DecisionType!)}: public site pilot, 3 months.", null, 3, Today.AddMonths(3)), t.DecisionType);
        }
        return r.RequestId;
    }

    private async Task CompleteReviewsAsync(long requestId)
    {
        foreach (var t in await _f.OpenTasksAsync(requestId))
        {
            var who = t.Discipline switch { Disciplines.SecurityRisk => "risk", Disciplines.Finance => "finopsAnalyst", Disciplines.Operations => "operator1", _ => "architect" };
            await _f.CompleteAsync(who, requestId, Stages.SpecialistReview, "REVIEWED", "Findings recorded; residual risk acceptable with conditions.", discipline: t.Discipline);
        }
    }

    /// <summary>J4 agent limit above the class default: FinOps review, waiting for the overage authorization at B9.</summary>
    public async Task<long> LimitIncreaseAsync()
    {
        var p = new RequestPayload
        {
            Purpose = "The pilot agents in the Retail makers environment hit their limit by mid-month.",
            ChangeKind = "AGENT_LIMIT", EnvironmentId = "9f1c2d4e-0000-4000-8000-00000000a001", CurrentValue = 2000, RequestedValue = 4000,
        };
        var r = await _f.SubmitAsync("RETAIL.product", Journeys.CapacityChange, "Raise agent limit in Retail makers - development", "RETAIL", null, "RETAIL.sponsor",
            seed.Workloads["retailDev"], p);
        await _f.CompleteAsync("intake", r.RequestId, Stages.Completeness, StageOutcomes.Complete);
        await CompleteReviewsAsync(r.RequestId);
        return r.RequestId;
    }

    /// <summary>J7 waiting at the retirement decision (I4), with Finance and Sourcing consulted.</summary>
    public async Task<long> RetirementAsync()
    {
        var p = new RequestPayload
        {
            Purpose = "The advisory test environment is replaced by the meeting-prep agent.",
            RetirementReason = "Replaced", RetirementPlan = "Notify 12 testers, export test results, release the 10,000 credit allocation.", ReplacementWorkload = "Advisor meeting-prep agent",
        };
        var r = await _f.SubmitAsync("WEALTH.product", Journeys.Retirement, "Retire Wealth advisory - test", "WEALTH", null, "WEALTH.sponsor", seed.Workloads["wealthTest"], p);
        await _f.CompleteAsync("intake", r.RequestId, Stages.Completeness, StageOutcomes.Complete);
        return r.RequestId;
    }

    /// <summary>J8 P3 renewal: FinOps recommendation done, waiting for the Commercial Authority (F4).</summary>
    public async Task<long> CommercialRenewalAsync()
    {
        var p = new RequestPayload
        {
            Purpose = "The P3 plan renews in about 75 days.", CommercialKind = "RENEWAL", Quantity = 300000, Unit = Units.P3CommitUnit,
            Rationale = "Twelve months at the current burn (about 24,000 commit units a month) with 15 percent headroom. Forecast only informs; nothing is bought automatically (C21).",
        };
        var r = await _f.SubmitAsync("finopsLead", Journeys.Commercial, "Renew the P3 plan", "ES-IT", null, null, null, p);
        await _f.CompleteAsync("finopsAnalyst", r.RequestId, Stages.FinOpsRecommendation, StageOutcomes.Ready, "Recommendation: renew at 300,000 commit units for 12 months.");
        return r.RequestId;
    }

    /// <summary>Consumption events, findings and an open recertification campaign.</summary>
    public async Task OperationsAsync()
    {
        await using var c = new SqlConnection(host.ConnectionString);
        await c.OpenAsync();
        var now = host.Clock.UtcNow;
        await c.ExecuteAsync(
            """
            INSERT ops.ConsumptionEvent (FundingOwnerId, WorkloadId, EventClass, Severity, Title, Description, ReportedByPersonId, ReportedUtc, AckDueUtc, CriticalServiceAffected, Status)
            VALUES (@retail, @retailDev, 'THRESHOLD', 'NORMAL', N'80 percent of the environment allocation used', N'The Retail makers environment reached its 80 percent notification threshold.', @op, @now, @due1, 0, 'OPEN'),
                   (@esit, @shared, 'EXHAUSTION', 'CRITICAL', N'Shared production environment out of allocation', N'Agents in the shared production environment report capacity denials; tenant-pool draw is on, so this is a tenant-wide shortfall.', @op, @now, @due2, 1, 'OPEN')
            """, new { retail = seed.FundingOwners["RETAIL"], retailDev = seed.Workloads["retailDev"], esit = seed.FundingOwners["ES-IT"], shared = seed.Workloads["sharedProd"],
                op = seed.Person("operator1"), now, due1 = now.AddDays(1), due2 = now.AddHours(1) });
        await c.ExecuteAsync(
            """
            INSERT ops.Finding (FindingType, Severity, FundingOwnerId, WorkloadId, ExternalId, Title, Details, Expected, Actual, Status, DedupeKey, DueUtc)
            VALUES ('UNREGISTERED_AGENT', 'MEDIUM', NULL, NULL, N'agent-7c1e', N'Unregistered Copilot Studio agent "Expense helper"', N'Found by inventory sync in an unmanaged environment; the owner must claim and classify it within 10 business days.', NULL, NULL, 'OPEN', N'UNREGISTERED_AGENT:agent-7c1e', @due),
                   ('ALLOCATION_DRIFT', 'HIGH', @wealth, @wealthTest, N'9f1c2d4e-0000-4000-8000-00000000a002', N'Allocation differs from the approved posture', N'The licensing read found more credits allocated than approved.', N'10,000 credits', N'12,000 credits', 'OPEN', N'ALLOCATION_DRIFT:9f1c2d4e-0000-4000-8000-00000000a002', @due)
            """, new { wealth = seed.FundingOwners["WEALTH"], wealthTest = seed.Workloads["wealthTest"], due = now.AddDays(14) });

        var campaignId = await c.ExecuteScalarAsync<int>(
            "INSERT wf.Campaign (CampaignType, Name, StartsOn, DueOn, Status, CreatedByPersonId) OUTPUT inserted.CampaignId VALUES ('RECERTIFICATION', @name, @start, @due, 'OPEN', @po)",
            new { name = $"Quarterly recertification {Today:yyyy}-Q{(Today.Month - 1) / 3 + 1}", start = Today, due = Today.AddDays(21), po = seed.Person("platformOwner") });
        var items = await c.QueryAsync<(int WorkloadId, int FundingOwnerId, string Name, int? ProductOwnerPersonId, string OwnerType)>(
            "SELECT w.WorkloadId, w.FundingOwnerId, w.Name, w.ProductOwnerPersonId, f.OwnerType FROM core.Workload w JOIN core.FundingOwner f ON f.FundingOwnerId = w.FundingOwnerId WHERE w.State IN ('DEVELOPMENT','TEST','PRODUCTION')");
        foreach (var w in items)
        {
            var taskId = await c.ExecuteScalarAsync<long>(
                """
                INSERT wf.Task (FundingOwnerId, TaskType, Title, AssignedRoleCode, ScopeType, ScopeId, Status, SlaPolicyCode, StartedUtc, DueUtc, RelatedEntityType, RelatedEntityId, RaciActivityCode)
                OUTPUT inserted.TaskId
                VALUES (@fo, 'RECERTIFICATION', @title, @role, 'FUNDING_OWNER', @fo, 'OPEN', 'BD15', @now, @due, 'CAMPAIGN_ITEM', NULL, 'LIFECYCLE_RETIREMENT')
                """, new { fo = w.FundingOwnerId, title = $"Recertify {w.Name}: need, audience, funding and outcome", role = w.OwnerType == "ENTERPRISE_SHARED" ? Roles.SharedServiceOwner : Roles.LobBusinessOwner, now, due = Today.AddDays(21) });
            var itemId = await c.ExecuteScalarAsync<int>(
                "INSERT wf.CampaignItem (CampaignId, FundingOwnerId, WorkloadId, OwnerPersonId, Status, TaskId) OUTPUT inserted.CampaignItemId VALUES (@campaignId, @fo, @wid, @owner, 'OPEN', @taskId)",
                new { campaignId, fo = w.FundingOwnerId, wid = w.WorkloadId, owner = w.ProductOwnerPersonId, taskId });
            await c.ExecuteAsync("UPDATE wf.Task SET RelatedEntityId = @itemId WHERE TaskId = @taskId", new { itemId, taskId });
        }
    }
}
