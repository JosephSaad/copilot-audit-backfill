using Dapper;
using Governance.Application.Data;

namespace Governance.Application.Queries;

public sealed class AccessOption
{
    public Guid AccessPackageId { get; init; }
    public string DisplayName { get; init; } = "";
    public string ServiceName { get; init; } = "";
    public int ServiceId { get; init; }
    public string Tier { get; init; } = "";
    public string? EnvelopeStatus { get; init; }
    public decimal? EnvelopeAmount { get; init; }
    public decimal? EnvelopeCommitted { get; init; }
    public string? EnvelopeUnit { get; init; }
    public DateTime? EnvelopeEnd { get; init; }
    public string? MyAssignmentState { get; init; }
    public DateTime? MyAssignmentExpiresUtc { get; init; }
}

public sealed class EntraDecision
{
    public DateTime DecidedAtUtc { get; init; }
    public string Outcome { get; init; } = "";
    public string ScopeText { get; init; } = "";
    public string? SourceRef { get; init; }
    public string Decider { get; init; } = "";
}

/// <summary>J1 routine access: Entra decides through access packages; the Workbench attributes, verifies and records.</summary>
public sealed class AccessQueries(Db db, ICurrentPrincipal me)
{
    public async Task<IReadOnlyList<AccessOption>> OptionsAsync(int fundingOwnerId)
    {
        await using var c = await db.OpenSystemAsync();
        var today = DateTime.UtcNow.Date;
        return (await c.QueryAsync<AccessOption>(
            """
            SELECT m.AccessPackageId, m.DisplayName, s.Name AS ServiceName, s.ServiceId, m.Tier,
                   e.Status AS EnvelopeStatus, e.Amount AS EnvelopeAmount, e.Committed AS EnvelopeCommitted, e.Unit AS EnvelopeUnit, e.PeriodEnd AS EnvelopeEnd,
                   a.State AS MyAssignmentState, a.ExpiresUtc AS MyAssignmentExpiresUtc
            FROM intg.AccessPackageMap m JOIN core.Service s ON s.ServiceId = m.ServiceId
            OUTER APPLY (SELECT TOP (1) x.Status, x.Amount, x.Unit, x.PeriodEnd,
                                COALESCE((SELECT SUM(cm.Amount) FROM core.EnvelopeCommitment cm WHERE cm.EnvelopeId = x.EnvelopeId AND cm.Status = 'ACTIVE'), 0) AS Committed
                         FROM core.FundingEnvelope x WHERE x.FundingOwnerId = m.FundingOwnerId AND x.ServiceId = m.ServiceId AND x.Status = 'ACTIVE'
                           AND x.PeriodStart <= @today AND x.PeriodEnd >= @today ORDER BY x.PeriodEnd) e
            OUTER APPLY (SELECT TOP (1) ea.State, ea.ExpiresUtc FROM intg.EntitlementAssignment ea WHERE ea.AccessPackageId = m.AccessPackageId AND ea.TargetPersonId = @me ORDER BY ea.CreatedUtc DESC) a
            WHERE m.FundingOwnerId = @fundingOwnerId
            ORDER BY s.Name, m.Tier
            """, new { fundingOwnerId, today, me = me.PersonId })).ToList();
    }

    public async Task<IReadOnlyList<EntraDecision>> MyEntraDecisionsAsync()
    {
        await using var c = await db.OpenSystemAsync();
        return (await c.QueryAsync<EntraDecision>(
            """
            SELECT TOP (20) d.DecidedAtUtc, d.Outcome, d.ScopeText, d.SourceRef, p.DisplayName AS Decider
            FROM wf.Decision d JOIN sec.Person p ON p.PersonId = d.DeciderPersonId
            JOIN intg.EntitlementAssignment ea ON ea.DecisionId = d.DecisionId
            WHERE d.Source = 'ENTRA' AND ea.TargetPersonId = @me
            ORDER BY d.DecisionId DESC
            """, new { me = me.PersonId })).ToList();
    }
}
