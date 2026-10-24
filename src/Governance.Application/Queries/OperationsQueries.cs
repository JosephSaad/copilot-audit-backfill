using Dapper;
using Governance.Application.Data;
using Governance.Domain;

namespace Governance.Application.Queries;

public sealed class EventRow : ICounted
{
    public int EventId { get; init; }
    public Guid PublicId { get; init; }
    public int FundingOwnerId { get; init; }
    public string FundingOwnerName { get; init; } = "";
    public int? WorkloadId { get; init; }
    public string? WorkloadName { get; init; }
    public string EventClass { get; init; } = "";
    public string Severity { get; init; } = "";
    public string Title { get; init; } = "";
    public string Description { get; init; } = "";
    public string ReportedByName { get; init; } = "";
    public DateTime ReportedUtc { get; init; }
    public DateTime AckDueUtc { get; init; }
    public DateTime? AckUtc { get; init; }
    public string? AckByName { get; init; }
    public string? ResponseAction { get; init; }
    public DateTime? RestoredUtc { get; init; }
    public string? ReviewNotes { get; init; }
    public bool CriticalServiceAffected { get; init; }
    public string Status { get; init; } = "";
    public string? IncidentNumber { get; init; }
    public string? IncidentState { get; init; }
    public byte[]? RowVer { get; init; }
    public int TotalRows { get; init; }
}

public sealed class SyncRunRow : ICounted
{
    public long SyncRunId { get; init; }
    public Guid PublicId { get; init; }
    public string Integration { get; init; } = "";
    public string Mode { get; init; } = "";
    public string? TriggeredByName { get; init; }
    public DateTime StartedUtc { get; init; }
    public DateTime? FinishedUtc { get; init; }
    public string Status { get; init; } = "";
    public int ItemsRead { get; init; }
    public int FindingsRaised { get; init; }
    public string? Message { get; init; }
    public int TotalRows { get; init; }
}

public sealed class AttestationDue
{
    public int TemplateId { get; init; }
    public string ServiceName { get; init; } = "";
    public int VersionNo { get; init; }
    public int? WorkloadId { get; init; }
    public string? WorkloadName { get; init; }
    public int? BindingId { get; init; }
    public string? ObjectType { get; init; }
    public string? ExternalId { get; init; }
    public string? BindingName { get; init; }
    public DateTime? LastAttestedUtc { get; init; }
    public string? LastAttestedBy { get; init; }
    public bool? LastMatched { get; init; }
    public bool Overdue { get; init; }
}

public sealed class AttestationRow : ICounted
{
    public long AttestationId { get; init; }
    public Guid PublicId { get; init; }
    public string Subject { get; init; } = "";
    public bool MatchesTemplate { get; init; }
    public string? Differences { get; init; }
    public string AttestedByName { get; init; } = "";
    public DateTime AttestedUtc { get; init; }
    public string Reason { get; init; } = "";
    public string AttestedJson { get; init; } = "{}";
    public int TotalRows { get; init; }
}

public sealed class CampaignRow
{
    public int CampaignId { get; init; }
    public Guid PublicId { get; init; }
    public string CampaignType { get; init; } = "";
    public string Name { get; init; } = "";
    public DateTime StartsOn { get; init; }
    public DateTime DueOn { get; init; }
    public string Status { get; init; } = "";
    public int Items { get; init; }
    public int Done { get; init; }
    public int Overdue { get; init; }
}

public sealed class CampaignItemRow : ICounted
{
    public int CampaignItemId { get; init; }
    public int CampaignId { get; init; }
    public string CampaignName { get; init; } = "";
    public DateTime DueOn { get; init; }
    public string FundingOwnerName { get; init; } = "";
    public int FundingOwnerId { get; init; }
    public int? WorkloadId { get; init; }
    public Guid? WorkloadPublicId { get; init; }
    public string? WorkloadName { get; init; }
    public string? Audience { get; init; }
    public string? LifecycleStage { get; init; }
    public string? OwnerName { get; init; }
    public int? OwnerPersonId { get; init; }
    public string Status { get; init; } = "";
    public string? Outcome { get; init; }
    public string? OutcomeEvidence { get; init; }
    public DateTime? CompletedUtc { get; init; }
    public int TotalRows { get; init; }
}

public sealed class ExceptionRow : ICounted
{
    public int ExceptionId { get; init; }
    public Guid PublicId { get; init; }
    public string FundingOwnerName { get; init; } = "";
    public int FundingOwnerId { get; init; }
    public string? WorkloadName { get; init; }
    public Guid? RequestPublicId { get; init; }
    public string? RequestNumber { get; init; }
    public string Title { get; init; } = "";
    public string Description { get; init; } = "";
    public string CompensatingControls { get; init; } = "";
    public string OwnerName { get; init; } = "";
    public DateTime ExpiresOn { get; init; }
    public DateTime? NextReviewOn { get; init; }
    public string Status { get; init; } = "";
    public int DaysLeft { get; init; }
    public byte[]? RowVer { get; init; }
    public int TotalRows { get; init; }
}

public sealed class OperationsQueries(Db db)
{
    private const string EventSelect =
        """
        SELECT e.*, fo.Name AS FundingOwnerName, w.Name AS WorkloadName, rp.DisplayName AS ReportedByName, ap.DisplayName AS AckByName,
               t.ExternalNumber AS IncidentNumber, t.State AS IncidentState, COUNT(*) OVER () AS TotalRows
        FROM ops.ConsumptionEvent e JOIN core.FundingOwner fo ON fo.FundingOwnerId = e.FundingOwnerId
        LEFT JOIN core.Workload w ON w.WorkloadId = e.WorkloadId
        JOIN sec.Person rp ON rp.PersonId = e.ReportedByPersonId LEFT JOIN sec.Person ap ON ap.PersonId = e.AckByPersonId
        OUTER APPLY (SELECT TOP (1) ExternalNumber, State FROM wf.ItsmTicket x WHERE x.EventId = e.EventId ORDER BY x.ItsmTicketId DESC) t
        """;

    public async Task<Paged<EventRow>> EventsAsync(ListQuery q, bool openOnly)
    {
        await using var c = await db.OpenUserAsync();
        var sorts = new Dictionary<string, string> { ["reported"] = "e.ReportedUtc", ["severity"] = "CASE e.Severity WHEN 'CRITICAL' THEN 0 WHEN 'HIGH' THEN 1 ELSE 2 END", ["due"] = "e.AckDueUtc", ["status"] = "e.Status" };
        return (await c.QueryAsync<EventRow>(EventSelect + $"""

            WHERE (@openOnly = 0 OR e.Status <> 'CLOSED') AND (@like IS NULL OR e.Title LIKE @like OR fo.Name LIKE @like OR w.Name LIKE @like)
            {q.OrderBy(sorts, "severity")}, e.ReportedUtc DESC
            OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY
            """, new { openOnly = openOnly ? 1 : 0, like = q.Like, q.Offset, q.PageSize })).ToPaged(q);
    }

    public async Task<EventRow?> EventAsync(Guid publicId)
    {
        await using var c = await db.OpenUserAsync();
        return await c.QuerySingleOrDefaultAsync<EventRow>(EventSelect + " WHERE e.PublicId = @publicId", new { publicId });
    }

    public async Task<Paged<SyncRunRow>> SyncRunsAsync(ListQuery q, string? integration)
    {
        await using var c = await db.OpenSystemAsync();
        return (await c.QueryAsync<SyncRunRow>($"""
            SELECT r.*, p.DisplayName AS TriggeredByName, COUNT(*) OVER () AS TotalRows
            FROM intg.SyncRun r LEFT JOIN sec.Person p ON p.PersonId = r.TriggeredByPersonId
            WHERE (@integration IS NULL OR r.Integration = @integration)
            ORDER BY r.StartedUtc DESC
            OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY
            """, new { integration, q.Offset, q.PageSize })).ToPaged(q);
    }

    /// <summary>Last successful run per integration: freshness for every source (H10).</summary>
    public async Task<IReadOnlyList<(string Integration, DateTime? LastSuccessUtc, DateTime? LastRunUtc, string? LastStatus)>> FreshnessAsync()
    {
        await using var c = await db.OpenSystemAsync();
        return (await c.QueryAsync<(string, DateTime?, DateTime?, string?)>(
            """
            SELECT i.Integration,
                   (SELECT MAX(FinishedUtc) FROM intg.SyncRun s WHERE s.Integration = i.Integration AND s.Status IN ('SUCCEEDED','PARTIAL')),
                   (SELECT MAX(StartedUtc) FROM intg.SyncRun s WHERE s.Integration = i.Integration),
                   (SELECT TOP (1) Status FROM intg.SyncRun s WHERE s.Integration = i.Integration ORDER BY StartedUtc DESC)
            FROM (VALUES ('X1'),('X2'),('X3'),('X4'),('X5'),('X6'),('X7'),('X8'),('X9'),('X10'),('X11'),('X12')) i(Integration)
            """)).ToList();
    }

    /// <summary>Governed Microsoft 365 policy bindings and when each was last attested (X6, monthly re-attestation, C16).</summary>
    public async Task<IReadOnlyList<AttestationDue>> AttestationsDueAsync()
    {
        await using var c = await db.OpenSystemAsync();
        return (await c.QueryAsync<AttestationDue>(
            """
            SELECT t.TemplateId, s.Name AS ServiceName, t.VersionNo, w.WorkloadId, w.Name AS WorkloadName, b.BindingId, b.ObjectType, b.ExternalId, b.DisplayName AS BindingName,
                   a.AttestedUtc AS LastAttestedUtc, ap.DisplayName AS LastAttestedBy, a.MatchesTemplate AS LastMatched,
                   CAST(CASE WHEN a.AttestedUtc IS NULL OR a.AttestedUtc < DATEADD(MONTH, -1, SYSUTCDATETIME()) THEN 1 ELSE 0 END AS bit) AS Overdue
            FROM core.ControlTemplate t JOIN core.Service s ON s.ServiceId = t.ServiceId AND s.RouteType IN ('M365_POLICY','ENTRA_PACKAGE')
            LEFT JOIN core.Workload w ON w.ServiceId = s.ServiceId AND w.State NOT IN ('RETIRED','DISCOVERED')
            LEFT JOIN core.WorkloadBinding b ON b.WorkloadId = w.WorkloadId AND b.EffectiveTo IS NULL AND b.ObjectType IN ('CHAT_CREDIT_POLICY','SPENDING_POLICY','ENTRA_GROUP','ACCESS_PACKAGE')
            OUTER APPLY (SELECT TOP (1) x.AttestedUtc, x.AttestedByPersonId, x.MatchesTemplate FROM evd.Attestation x
                         WHERE x.TemplateId = t.TemplateId AND (x.BindingId = b.BindingId OR (b.BindingId IS NULL AND x.BindingId IS NULL)) ORDER BY x.AttestationId DESC) a
            LEFT JOIN sec.Person ap ON ap.PersonId = a.AttestedByPersonId
            WHERE t.Status = 'ACTIVE'
            ORDER BY Overdue DESC, s.Name, w.Name
            """)).ToList();
    }

    public async Task<Paged<AttestationRow>> AttestationsAsync(ListQuery q)
    {
        await using var c = await db.OpenSystemAsync();
        return (await c.QueryAsync<AttestationRow>(
            """
            SELECT a.AttestationId, a.PublicId, a.Subject, a.MatchesTemplate, a.Differences, p.DisplayName AS AttestedByName, a.AttestedUtc, a.Reason, a.AttestedJson, COUNT(*) OVER () AS TotalRows
            FROM evd.Attestation a JOIN sec.Person p ON p.PersonId = a.AttestedByPersonId
            WHERE (@like IS NULL OR a.Subject LIKE @like)
            ORDER BY a.AttestationId DESC OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY
            """, new { like = q.Like, q.Offset, q.PageSize })).ToPaged(q);
    }

    public async Task<IReadOnlyList<CampaignRow>> CampaignsAsync()
    {
        await using var c = await db.OpenSystemAsync();
        return (await c.QueryAsync<CampaignRow>(
            """
            SELECT c.CampaignId, c.PublicId, c.CampaignType, c.Name, c.StartsOn, c.DueOn, c.Status,
                   (SELECT COUNT(*) FROM wf.CampaignItem i WHERE i.CampaignId = c.CampaignId) AS Items,
                   (SELECT COUNT(*) FROM wf.CampaignItem i WHERE i.CampaignId = c.CampaignId AND i.Status = 'DONE') AS Done,
                   (SELECT COUNT(*) FROM wf.CampaignItem i WHERE i.CampaignId = c.CampaignId AND i.Status <> 'DONE' AND c.DueOn < CAST(SYSUTCDATETIME() AS date)) AS Overdue
            FROM wf.Campaign c ORDER BY c.Status, c.DueOn DESC
            """)).ToList();
    }

    private const string ItemSelect =
        """
        SELECT i.CampaignItemId, i.CampaignId, c.Name AS CampaignName, c.DueOn, fo.Name AS FundingOwnerName, i.FundingOwnerId, i.WorkloadId, w.PublicId AS WorkloadPublicId,
               w.Name AS WorkloadName, w.Audience, w.LifecycleStage, p.DisplayName AS OwnerName, i.OwnerPersonId, i.Status, i.Outcome, i.OutcomeEvidence, i.CompletedUtc,
               COUNT(*) OVER () AS TotalRows
        FROM wf.CampaignItem i JOIN wf.Campaign c ON c.CampaignId = i.CampaignId JOIN core.FundingOwner fo ON fo.FundingOwnerId = i.FundingOwnerId
        LEFT JOIN core.Workload w ON w.WorkloadId = i.WorkloadId LEFT JOIN sec.Person p ON p.PersonId = i.OwnerPersonId
        """;

    public async Task<Paged<CampaignItemRow>> CampaignItemsAsync(ListQuery q, int? campaignId, int? ownerPersonId)
    {
        await using var c = await db.OpenUserAsync();
        return (await c.QueryAsync<CampaignItemRow>(ItemSelect + """

            WHERE (@campaignId IS NULL OR i.CampaignId = @campaignId) AND (@owner IS NULL OR i.OwnerPersonId = @owner)
              AND (@like IS NULL OR w.Name LIKE @like OR fo.Name LIKE @like)
            ORDER BY CASE i.Status WHEN 'DONE' THEN 1 ELSE 0 END, c.DueOn, w.Name
            OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY
            """, new { campaignId, owner = ownerPersonId, like = q.Like, q.Offset, q.PageSize })).ToPaged(q);
    }

    public async Task<Paged<ExceptionRow>> ExceptionsAsync(ListQuery q, bool activeOnly)
    {
        await using var c = await db.OpenUserAsync();
        var sorts = new Dictionary<string, string> { ["expires"] = "x.ExpiresOn", ["owner"] = "fo.Name", ["title"] = "x.Title", ["status"] = "x.Status" };
        return (await c.QueryAsync<ExceptionRow>($"""
            SELECT x.ExceptionId, x.PublicId, fo.Name AS FundingOwnerName, x.FundingOwnerId, w.Name AS WorkloadName, r.PublicId AS RequestPublicId, r.RequestNumber, x.Title, x.Description,
                   x.CompensatingControls, p.DisplayName AS OwnerName, x.ExpiresOn, x.NextReviewOn, x.Status,
                   DATEDIFF(DAY, CAST(SYSUTCDATETIME() AS date), x.ExpiresOn) AS DaysLeft, x.RowVer, COUNT(*) OVER () AS TotalRows
            FROM wf.ExceptionItem x JOIN core.FundingOwner fo ON fo.FundingOwnerId = x.FundingOwnerId JOIN sec.Person p ON p.PersonId = x.OwnerPersonId
            LEFT JOIN core.Workload w ON w.WorkloadId = x.WorkloadId LEFT JOIN wf.Request r ON r.RequestId = x.RequestId
            WHERE (@activeOnly = 0 OR x.Status = 'ACTIVE') AND (@like IS NULL OR x.Title LIKE @like OR fo.Name LIKE @like OR w.Name LIKE @like)
            {q.OrderBy(sorts, "expires")}
            OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY
            """, new { activeOnly = activeOnly ? 1 : 0, like = q.Like, q.Offset, q.PageSize })).ToPaged(q);
    }
}

public sealed class KpiRow
{
    public string KpiCode { get; init; } = "";
    public string Name { get; init; } = "";
    public decimal? Numerator { get; init; }
    public decimal? Denominator { get; init; }
    public int Phase { get; init; }
}

public sealed class AuditRow : ICounted
{
    public long AuditId { get; init; }
    public DateTime Utc { get; init; }
    public string? PersonName { get; init; }
    public string? ActingAsName { get; init; }
    public string Action { get; init; } = "";
    public string? EntityType { get; init; }
    public string? EntityId { get; init; }
    public string? Detail { get; init; }
    public string Outcome { get; init; } = "";
    public int TotalRows { get; init; }
}

public sealed class DecisionReportRow : ICounted
{
    public Guid PublicId { get; init; }
    public string DecisionType { get; init; } = "";
    public string Outcome { get; init; } = "";
    public DateTime DecidedAtUtc { get; init; }
    public string Source { get; init; } = "";
    public string RaciActivityCode { get; init; } = "";
    public int RaciVersionId { get; init; }
    public string Decider { get; init; } = "";
    public string DeciderRoleCode { get; init; } = "";
    public int? DelegationId { get; init; }
    public string ScopeText { get; init; } = "";
    public string? ConditionsText { get; init; }
    public DateTime? ExpiresOn { get; init; }
    public DateTime? NextReviewOn { get; init; }
    public string? RequestNumber { get; init; }
    public string? FundingOwner { get; init; }
    public int TotalRows { get; init; }
}

public sealed class ReportQueries(Db db)
{
    public async Task<(IReadOnlyList<KpiRow> Kpis, DateTime AsOfUtc)> KpisAsync()
    {
        await using var c = await db.OpenSystemAsync();
        var rows = (await c.QueryAsync<KpiRow>("SELECT KpiCode, Name, Numerator, Denominator, Phase FROM rpt.vKpi")).ToList();
        return (rows, DateTime.UtcNow);
    }

    public async Task<Paged<AuditRow>> AuditAsync(ListQuery q, DateTime? fromUtc, DateTime? toUtc, string? action)
    {
        await using var c = await db.OpenSystemAsync();
        return (await c.QueryAsync<AuditRow>(
            """
            SELECT a.AuditId, a.Utc, p.DisplayName AS PersonName, ap.DisplayName AS ActingAsName, a.Action, a.EntityType, a.EntityId, a.Detail, a.Outcome, COUNT(*) OVER () AS TotalRows
            FROM aud.AuditLog a LEFT JOIN sec.Person p ON p.PersonId = a.PersonId LEFT JOIN sec.Person ap ON ap.PersonId = a.ActingAsPersonId
            WHERE (@fromUtc IS NULL OR a.Utc >= @fromUtc) AND (@toUtc IS NULL OR a.Utc < @toUtc) AND (@action IS NULL OR a.Action LIKE @action + '%')
              AND (@like IS NULL OR a.Detail LIKE @like OR a.EntityId LIKE @like OR p.DisplayName LIKE @like)
            ORDER BY a.AuditId DESC OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY
            """, new { fromUtc, toUtc, action, like = q.Like, q.Offset, q.PageSize })).ToPaged(q);
    }

    public async Task<Paged<DecisionReportRow>> DecisionsAsync(ListQuery q, DateTime? fromUtc, DateTime? toUtc)
    {
        await using var c = await db.OpenSystemAsync();
        return (await c.QueryAsync<DecisionReportRow>(
            """
            SELECT d.PublicId, d.DecisionType, d.Outcome, d.DecidedAtUtc, d.Source, d.RaciActivityCode, d.RaciVersionId, p.DisplayName AS Decider, d.DeciderRoleCode,
                   d.DelegationId, d.ScopeText, d.ConditionsText, d.ExpiresOn, d.NextReviewOn, r.RequestNumber, fo.Name AS FundingOwner, COUNT(*) OVER () AS TotalRows
            FROM wf.Decision d JOIN sec.Person p ON p.PersonId = d.DeciderPersonId
            LEFT JOIN wf.Request r ON r.RequestId = d.RequestId LEFT JOIN core.FundingOwner fo ON fo.FundingOwnerId = d.FundingOwnerId
            WHERE (@fromUtc IS NULL OR d.DecidedAtUtc >= @fromUtc) AND (@toUtc IS NULL OR d.DecidedAtUtc < @toUtc)
              AND (@like IS NULL OR r.RequestNumber LIKE @like OR fo.Name LIKE @like OR p.DisplayName LIKE @like OR d.DecisionType LIKE @like)
            ORDER BY d.DecisionId DESC OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY
            """, new { fromUtc, toUtc, like = q.Like, q.Offset, q.PageSize })).ToPaged(q);
    }
}

public sealed class RaciVersionRow
{
    public int RaciVersionId { get; init; }
    public int VersionNo { get; init; }
    public string Status { get; init; } = "";
    public string? Notes { get; init; }
    public string? CreatedBy { get; init; }
    public DateTime CreatedUtc { get; init; }
    public string? PlatformOwnerApproval { get; init; }
    public int? PlatformOwnerApprovalPersonId { get; init; }
    public DateTime? PlatformOwnerApprovalUtc { get; init; }
    public string? SteeringApproval { get; init; }
    public int? SteeringApprovalPersonId { get; init; }
    public DateTime? SteeringApprovalUtc { get; init; }
    public DateTime? ActivatedUtc { get; init; }
}

public sealed class RaciMatrix
{
    public RaciVersionRow Version { get; init; } = new();
    public IReadOnlyList<RaciFunction> Functions { get; init; } = Array.Empty<RaciFunction>();
    public IReadOnlyList<(string Code, string Name, string Stages, string Simplified)> Activities { get; init; } = Array.Empty<(string, string, string, string)>();
    public IReadOnlyDictionary<(string Activity, string Function), string> Cells { get; init; } = new Dictionary<(string, string), string>();

    /// <summary>
    /// The simplified view: activities merged by their simplified row; where merged activities differ, A or A/R and R
    /// keep their letter, and between C and I the cell shows C (the operating model's merge rule).
    /// </summary>
    public IReadOnlyList<(string Row, IReadOnlyDictionary<string, string> Cells)> Simplified()
    {
        static int Rank(string v) => v switch { "A/R" => 5, "A" => 4, "R" => 3, "C" => 2, "I" => 1, _ => 0 };
        var rows = new List<(string, IReadOnlyDictionary<string, string>)>();
        foreach (var g in Activities.GroupBy(a => a.Simplified))
        {
            var merged = new Dictionary<string, string>();
            foreach (var f in Functions)
            {
                var values = g.Select(a => Cells.TryGetValue((a.Code, f.FunctionCode), out var v) ? v : "I").ToList();
                merged[f.FunctionCode] = values.OrderByDescending(Rank).First();
            }
            rows.Add((g.Key, merged));
        }
        return rows;
    }

    public IReadOnlyList<string> Problems()
    {
        var p = new List<string>();
        foreach (var a in Activities)
        {
            var count = Functions.Count(f => Cells.TryGetValue((a.Code, f.FunctionCode), out var v) && v is "A" or "A/R");
            if (count != 1) p.Add($"{a.Name}: {count} accountable cells (needs exactly one A or A/R).");
            if (Cells.TryGetValue((a.Code, Domain.Functions.Microsoft), out var ms) && ms is "A" or "A/R" or "R")
                p.Add($"{a.Name}: Microsoft cannot hold R or A.");
        }
        return p;
    }
}

public sealed class AdminQueries(Db db)
{
    public async Task<IReadOnlyList<RaciVersionRow>> RaciVersionsAsync()
    {
        await using var c = await db.OpenSystemAsync();
        return (await c.QueryAsync<RaciVersionRow>(
            """
            SELECT v.RaciVersionId, v.VersionNo, v.Status, v.Notes, cp.DisplayName AS CreatedBy, v.CreatedUtc,
                   po.DisplayName AS PlatformOwnerApproval, v.PlatformOwnerApprovalPersonId, v.PlatformOwnerApprovalUtc,
                   st.DisplayName AS SteeringApproval, v.SteeringApprovalPersonId, v.SteeringApprovalUtc, v.ActivatedUtc
            FROM sec.RaciVersion v LEFT JOIN sec.Person cp ON cp.PersonId = v.CreatedByPersonId
            LEFT JOIN sec.Person po ON po.PersonId = v.PlatformOwnerApprovalPersonId LEFT JOIN sec.Person st ON st.PersonId = v.SteeringApprovalPersonId
            ORDER BY v.VersionNo DESC
            """)).ToList();
    }

    public async Task<RaciMatrix?> RaciAsync(int? versionNo)
    {
        await using var c = await db.OpenSystemAsync();
        var versions = await RaciVersionsAsync();
        var v = versionNo is int n ? versions.FirstOrDefault(x => x.VersionNo == n) : versions.FirstOrDefault(x => x.Status == "ACTIVE") ?? versions.FirstOrDefault();
        if (v is null) return null;
        var functions = (await c.QueryAsync<RaciFunction>("SELECT * FROM sec.RaciFunction ORDER BY SortOrder")).ToList();
        var acts = (await c.QueryAsync<(string, string, string, string)>("SELECT ActivityCode, Name, GovernsStages, SimplifiedRow FROM sec.RaciActivity ORDER BY SortOrder")).ToList();
        var cells = (await c.QueryAsync<RaciCell>("SELECT * FROM sec.RaciCell WHERE RaciVersionId = @RaciVersionId", new { v.RaciVersionId }))
            .ToDictionary(x => (x.ActivityCode, x.FunctionCode), x => x.RaciValue);
        return new RaciMatrix { Version = v, Functions = functions, Activities = acts, Cells = cells };
    }

    public async Task<IReadOnlyList<(string JourneyCode, string Name, int VersionNo, IReadOnlyList<StageDefinition> Stages, IReadOnlyList<TransitionDefinition> Transitions)>> WorkflowsAsync()
    {
        await using var c = await db.OpenSystemAsync();
        var defs = (await c.QueryAsync<(int DefinitionId, string JourneyCode, string Name, int VersionNo)>("SELECT DefinitionId, JourneyCode, Name, VersionNo FROM wf.WorkflowDefinition WHERE IsActive = 1 ORDER BY JourneyCode")).ToList();
        var stages = (await c.QueryAsync<StageDefinition>("SELECT * FROM wf.StageDefinition ORDER BY SortOrder")).ToList();
        var trans = (await c.QueryAsync<TransitionDefinition>("SELECT * FROM wf.TransitionDefinition")).ToList();
        return defs.Select(d => (d.JourneyCode, d.Name, d.VersionNo,
            (IReadOnlyList<StageDefinition>)stages.Where(s => s.DefinitionId == d.DefinitionId).ToList(),
            (IReadOnlyList<TransitionDefinition>)trans.Where(t => t.DefinitionId == d.DefinitionId).ToList())).ToList();
    }

    public async Task<IReadOnlyList<SlaPolicy>> SlaPoliciesAsync()
    {
        await using var c = await db.OpenSystemAsync();
        return (await c.QueryAsync<SlaPolicy>("SELECT * FROM wf.SlaPolicy ORDER BY Calendar, Unit, Duration")).ToList();
    }

    public async Task<IReadOnlyList<(string LadderCode, int Level, string RoleCode, string ScopeKind)>> LaddersAsync()
    {
        await using var c = await db.OpenSystemAsync();
        return (await c.QueryAsync<(string, int, string, string)>("SELECT LadderCode, Level, RoleCode, ScopeKind FROM wf.EscalationStep ORDER BY LadderCode, Level")).ToList();
    }

    public sealed record NotificationRuleRow(string Code, string EventName, string Recipients, string Channels, string Timing, bool IsAction, bool IsEnabled);

    public async Task<IReadOnlyList<NotificationRuleRow>> NotificationRulesAsync()
    {
        await using var c = await db.OpenSystemAsync();
        return (await c.QueryAsync<NotificationRuleRow>("SELECT Code, EventName, Recipients, Channels, Timing, IsAction, IsEnabled FROM ntf.NotificationRule ORDER BY Code")).ToList();
    }

    public sealed record GroupMapRow(Guid EntraGroupId, string RoleCode, string RoleName, string? GroupName);

    public async Task<IReadOnlyList<GroupMapRow>> GroupMapAsync()
    {
        await using var c = await db.OpenSystemAsync();
        return (await c.QueryAsync<GroupMapRow>("SELECT m.EntraGroupId, m.RoleCode, r.Name AS RoleName, m.GroupName FROM sec.GroupRoleMap m JOIN sec.RoleDefinition r ON r.RoleCode = m.RoleCode ORDER BY r.SortOrder")).ToList();
    }

    public sealed record RoleOption(string RoleCode, string Name, string Description, string ScopeType, bool IsCentral, bool IsLob, bool CanDecide);

    public async Task<IReadOnlyList<RoleOption>> RolesAsync()
    {
        await using var c = await db.OpenSystemAsync();
        return (await c.QueryAsync<RoleOption>("SELECT RoleCode, Name, Description, ScopeType, IsCentral, IsLob, CanDecide FROM sec.RoleDefinition ORDER BY SortOrder")).ToList();
    }

    public sealed record TextBlockRow(string TextKey, int VersionNo, string Body, string Status, DateTime UpdatedUtc);

    public async Task<IReadOnlyList<TextBlockRow>> TextBlocksAsync()
    {
        await using var c = await db.OpenSystemAsync();
        return (await c.QueryAsync<TextBlockRow>("SELECT TextKey, VersionNo, Body, Status, UpdatedUtc FROM ref.TextBlock ORDER BY TextKey, VersionNo DESC")).ToList();
    }

    public async Task<IReadOnlyList<(string Key, string Value, string? Description)>> SettingsAsync()
    {
        await using var c = await db.OpenSystemAsync();
        return (await c.QueryAsync<(string, string, string?)>("SELECT SettingKey, SettingValue, Description FROM ref.Setting ORDER BY SettingKey")).ToList();
    }

    public async Task<IReadOnlyList<(string JobName, DateTime? LastSuccessUtc, DateTime? LastRunUtc, string? LastStatus, string? Message)>> JobsAsync()
    {
        await using var c = await db.OpenSystemAsync();
        return (await c.QueryAsync<(string, DateTime?, DateTime?, string?, string?)>(
            """
            SELECT j.JobName, MAX(CASE WHEN j.Status = 'SUCCEEDED' THEN j.FinishedUtc END), MAX(j.StartedUtc),
                   (SELECT TOP (1) Status FROM intg.JobRun x WHERE x.JobName = j.JobName ORDER BY x.StartedUtc DESC),
                   (SELECT TOP (1) Message FROM intg.JobRun x WHERE x.JobName = j.JobName ORDER BY x.StartedUtc DESC)
            FROM intg.JobRun j GROUP BY j.JobName ORDER BY j.JobName
            """)).ToList();
    }

    public async Task<(int Pending, int DeadLettered, DateTime? OldestPendingUtc)> OutboxAsync()
    {
        await using var c = await db.OpenSystemAsync();
        return await c.QuerySingleAsync<(int, int, DateTime?)>(
            "SELECT COALESCE(SUM(CASE WHEN ProcessedAtUtc IS NULL AND DeadLetteredUtc IS NULL THEN 1 ELSE 0 END), 0), COALESCE(SUM(CASE WHEN DeadLetteredUtc IS NOT NULL THEN 1 ELSE 0 END), 0), MIN(CASE WHEN ProcessedAtUtc IS NULL AND DeadLetteredUtc IS NULL THEN CreatedUtc END) FROM ntf.Outbox");
    }
}
