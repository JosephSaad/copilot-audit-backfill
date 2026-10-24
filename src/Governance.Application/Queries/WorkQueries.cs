using Dapper;
using Governance.Application.Data;
using Governance.Domain;

namespace Governance.Application.Queries;

public sealed class JourneyTile
{
    public string JourneyCode { get; init; } = "";
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public string TypicalUser { get; init; } = "";
    public string Turnaround { get; init; } = "";
}

public sealed class SetupStep
{
    public int Step { get; init; }
    public string What { get; init; } = "";
    public string Accountable { get; init; } = "";
    public string Screen { get; init; } = "";
    public string Link { get; init; } = "";
    public bool Done { get; init; }
    public string Status { get; init; } = "";
}

public sealed class TaskListItem : ICounted
{
    public long TaskId { get; init; }
    public Guid TaskPublicId { get; init; }
    public string TaskType { get; init; } = "";
    public string Title { get; init; } = "";
    public string? StageCode { get; init; }
    public string? AssignedRoleCode { get; init; }
    public string? RoleName { get; init; }
    public int? AssignedPersonId { get; init; }
    public string? AssignedName { get; init; }
    public DateTime StartedUtc { get; init; }
    public DateTime? DueUtc { get; init; }
    public int EscalationLevel { get; init; }
    public string? Discipline { get; init; }
    public bool IsBlocking { get; init; }
    public long? RequestId { get; init; }
    public Guid? RequestPublicId { get; init; }
    public string? RequestNumber { get; init; }
    public string? RequestTitle { get; init; }
    public string? JourneyCode { get; init; }
    public string? FundingOwnerName { get; init; }
    public string? RelatedEntityType { get; init; }
    public long? RelatedEntityId { get; init; }
    public int TotalRows { get; init; }
}

public sealed class RequestListItem : ICounted
{
    public long RequestId { get; init; }
    public Guid PublicId { get; init; }
    public string RequestNumber { get; init; } = "";
    public string JourneyCode { get; init; } = "";
    public string JourneyName { get; init; } = "";
    public string Title { get; init; } = "";
    public string State { get; init; } = "";
    public string? CurrentStage { get; init; }
    public string? StageName { get; init; }
    public string FundingOwnerName { get; init; } = "";
    public string RequesterName { get; init; } = "";
    public DateTime CreatedUtc { get; init; }
    public DateTime LastActivityUtc { get; init; }
    public int TotalRows { get; init; }
}

public sealed class RequestDetail
{
    public RequestRow Request { get; init; } = new();
    public string JourneyName { get; init; } = "";
    public string FundingOwnerName { get; init; } = "";
    public Guid FundingOwnerPublicId { get; init; }
    public string? ServiceName { get; init; }
    public string RequesterName { get; init; } = "";
    public string? SponsorName { get; init; }
    public Workload? Workload { get; init; }
    public string? CostCenterCode { get; init; }
    public StageDefinition? Stage { get; init; }
    public IReadOnlyList<StageDefinition> Stages { get; init; } = Array.Empty<StageDefinition>();
    public IReadOnlyList<TimelineItem> Timeline { get; init; } = Array.Empty<TimelineItem>();
    public IReadOnlyList<TaskView> Tasks { get; init; } = Array.Empty<TaskView>();
    public IReadOnlyList<DecisionView> Decisions { get; init; } = Array.Empty<DecisionView>();
    public IReadOnlyList<EvidenceView> Evidence { get; init; } = Array.Empty<EvidenceView>();
    public IReadOnlyList<TicketView> Tickets { get; init; } = Array.Empty<TicketView>();
    public IReadOnlyList<RaciLine> WhoDoesWhat { get; init; } = Array.Empty<RaciLine>();
    public IReadOnlyList<TransitionDefinition> Transitions { get; init; } = Array.Empty<TransitionDefinition>();
    public IReadOnlyList<string> Meters { get; init; } = Array.Empty<string>();
    public IReadOnlyList<VerificationView> Verifications { get; init; } = Array.Empty<VerificationView>();
}

public sealed class TimelineItem
{
    public DateTime Utc { get; init; }
    public string EventType { get; init; } = "";
    public string? FromState { get; init; }
    public string? ToState { get; init; }
    public string? StageCode { get; init; }
    public string? Text { get; init; }
    public string? PersonName { get; init; }
}

public sealed class TaskView
{
    public long TaskId { get; init; }
    public Guid PublicId { get; init; }
    public string TaskType { get; init; } = "";
    public string Title { get; init; } = "";
    public string? StageCode { get; init; }
    public string? AssignedRoleCode { get; init; }
    public string? RoleName { get; init; }
    public int? AssignedPersonId { get; init; }
    public string? AssignedName { get; init; }
    public DateTime? ClaimedUtc { get; init; }
    public string Status { get; init; } = "";
    public bool IsBlocking { get; init; }
    public DateTime StartedUtc { get; init; }
    public DateTime? DueUtc { get; init; }
    public string? Outcome { get; init; }
    public string? CompletedByName { get; init; }
    public DateTime? CompletedUtc { get; init; }
    public string? Notes { get; init; }
    public string? DecisionType { get; init; }
    public string? RaciActivityCode { get; init; }
    public string? Discipline { get; init; }
    public string? Reason { get; init; }
    public string? RelatedEntityType { get; init; }
    public string? ExcludedText { get; set; }
}

public sealed class DecisionView
{
    public long DecisionId { get; init; }
    public Guid PublicId { get; init; }
    public string DecisionType { get; init; } = "";
    public string Outcome { get; init; } = "";
    public string DeciderName { get; init; } = "";
    public string DeciderRoleCode { get; init; } = "";
    public int? DelegationId { get; init; }
    public int RaciVersionNo { get; init; }
    public string RaciActivityCode { get; init; } = "";
    public string? StageCode { get; init; }
    public string ScopeText { get; init; } = "";
    public string? ConditionsText { get; init; }
    public string? ReasonText { get; init; }
    public string? RequiredEvidenceText { get; init; }
    public DateTime? ExpiresOn { get; init; }
    public DateTime? NextReviewOn { get; init; }
    public string Source { get; init; } = "";
    public DateTime DecidedAtUtc { get; init; }
}

public sealed class EvidenceView
{
    public long EvidenceId { get; init; }
    public Guid PublicId { get; init; }
    public string EvidenceType { get; init; } = "";
    public string? FileName { get; init; }
    public long? SizeBytes { get; init; }
    public string? Sha256Hex { get; init; }
    public string? Description { get; init; }
    public string SourceSystem { get; init; } = "";
    public string UploadedByName { get; init; } = "";
    public DateTime UploadedUtc { get; init; }
    public string LinkType { get; init; } = "";
    public bool Purged { get; init; }
}

public sealed class TicketView
{
    public string Kind { get; init; } = "";
    public string? ExternalNumber { get; init; }
    public string? ExternalUrl { get; init; }
    public string State { get; init; } = "";
    public DateTime CreatedUtc { get; init; }
    public DateTime? ClosedUtc { get; init; }
    public string? ClosureNotes { get; init; }
}

public sealed class VerificationView
{
    public Guid PublicId { get; init; }
    public string Method { get; init; } = "";
    public string Result { get; init; } = "";
    public string VerifierName { get; init; } = "";
    public DateTime VerifiedUtc { get; init; }
    public string ChecksJson { get; init; } = "[]";
}

public sealed class RaciLine
{
    public string FunctionCode { get; init; } = "";
    public string FunctionName { get; init; } = "";
    public string RaciValue { get; init; } = "";
    public string? RoleCode { get; set; }
    public string People { get; set; } = "";
}

public sealed class WorkQueries(Db db, ICurrentPrincipal me)
{
    public async Task<IReadOnlyList<JourneyTile>> JourneysAsync()
    {
        await using var c = await db.OpenUserAsync();
        return (await c.QueryAsync<JourneyTile>("SELECT JourneyCode, Name, Description, TypicalUser, Turnaround FROM wf.WorkflowDefinition WHERE IsActive = 1 ORDER BY JourneyCode")).ToList();
    }

    public async Task<bool> IntakeOpenAsync()
    {
        await using var c = await db.OpenUserAsync();
        return string.Equals(await c.ExecuteScalarAsync<string>("SELECT SettingValue FROM ref.Setting WHERE SettingKey = 'Intake.Open'"), "true", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The program start sequence (section 4.1) with live status.</summary>
    public async Task<IReadOnlyList<SetupStep>> SetupAsync()
    {
        await using var c = await db.OpenSystemAsync();
        var today = DateTime.UtcNow.Date;
        var s = await c.QuerySingleAsync<SetupCounts>(
            """
            SELECT
              (SELECT COUNT(*) FROM sec.RoleAssignment WHERE RoleCode = 'PLATFORM_OWNER' AND EffectiveFrom <= @today AND (EffectiveTo IS NULL OR EffectiveTo >= @today)) AS PlatformOwners,
              (SELECT COUNT(*) FROM sec.RaciVersion WHERE Status = 'ACTIVE') AS ActiveRaci,
              (SELECT COUNT(*) FROM core.FundingOwner WHERE Status = 'ACTIVE') AS ActiveOwners,
              (SELECT COUNT(*) FROM core.FundingOwner WHERE Status = 'DRAFT') AS DraftOwners,
              (SELECT COUNT(*) FROM core.Service WHERE InstitutionalStatus IN ('APPROVED','PILOT')) AS ApprovedServices,
              (SELECT COUNT(*) FROM core.Service WHERE InstitutionalStatus IN ('APPROVED','PILOT') AND OperationalOwnerPersonId IS NULL) AS ServicesWithoutOwner,
              (SELECT COUNT(*) FROM core.Service s WHERE s.InstitutionalStatus IN ('APPROVED','PILOT')
                  AND NOT EXISTS (SELECT 1 FROM core.ControlTemplate t WHERE t.ServiceId = s.ServiceId AND t.Status = 'ACTIVE')) AS ServicesWithoutTemplate,
              (SELECT COUNT(*) FROM core.CommercialItem) AS CommercialItems,
              (SELECT COUNT(*) FROM core.CommercialItem WHERE ReconciledToContract = 0) AS UnreconciledItems,
              (SELECT COUNT(*) FROM ref.Setting WHERE SettingKey = 'Setup.StandardsApprovedUtc') AS StandardsApproved,
              (SELECT COUNT(*) FROM intg.SyncRun WHERE Integration = 'X4' AND Status = 'SUCCEEDED') AS InventoryRuns,
              (SELECT COUNT(*) FROM core.Workload w WHERE w.State = 'DISCOVERED'
                  AND NOT EXISTS (SELECT 1 FROM ops.Finding f WHERE f.WorkloadId = w.WorkloadId AND f.Status IN ('OPEN','ACKNOWLEDGED'))) AS UnhandledDiscovered,
              (SELECT COUNT(*) FROM ops.Finding WHERE FindingType LIKE 'UNREGISTERED%' AND Status IN ('OPEN','ACKNOWLEDGED')) AS OpenUnregistered,
              (SELECT COUNT(*) FROM core.FundingOwner fo WHERE fo.Status = 'ACTIVE'
                  AND NOT EXISTS (SELECT 1 FROM core.FundingEnvelope e WHERE e.FundingOwnerId = fo.FundingOwnerId AND e.Status = 'ACTIVE')) AS OwnersWithoutEnvelope,
              (SELECT SettingValue FROM ref.Setting WHERE SettingKey = 'Intake.Open') AS IntakeOpen
            """, new { today });

        return new List<SetupStep>
        {
            new() { Step = 0, What = "Name the Platform Owner and approve RACI version 1", Accountable = "Steering", Screen = "Admin › RACI", Link = "/Admin/Raci",
                    Done = s.PlatformOwners > 0 && s.ActiveRaci > 0,
                    Status = s.ActiveRaci > 0 ? (s.PlatformOwners > 0 ? "RACI active; Platform Owner named." : "RACI active; no Platform Owner named.") : "No RACI version is active yet." },
            new() { Step = 1, What = "Define funding owners (LOBs and Enterprise Shared) and activate them", Accountable = "Finance / FinOps", Screen = "Registry › Funding owners", Link = "/Registry/FundingOwners",
                    Done = s.ActiveOwners > 0 && s.DraftOwners == 0, Status = $"{s.ActiveOwners} active, {s.DraftOwners} still in draft." },
            new() { Step = 2, What = "Publish the approved service catalogue", Accountable = "Platform Owner", Screen = "Registry › Services", Link = "/Registry/Services",
                    Done = s.ApprovedServices > 0 && s.ServicesWithoutOwner == 0, Status = $"{s.ApprovedServices} approved or pilot; {s.ServicesWithoutOwner} without an operational owner." },
            new() { Step = 3, What = "Approve a control template for every approved service", Accountable = "Platform Owner", Screen = "Registry › Control templates", Link = "/Registry/Templates",
                    Done = s.ApprovedServices > 0 && s.ServicesWithoutTemplate == 0, Status = $"{s.ServicesWithoutTemplate} approved services without an active template." },
            new() { Step = 4, What = "Record the commercial inventory and reconcile it to contracts", Accountable = "Finance / FinOps", Screen = "Registry › Commercial", Link = "/Registry/Commercial",
                    Done = s.CommercialItems > 0 && s.UnreconciledItems == 0, Status = $"{s.CommercialItems} items, {s.UnreconciledItems} not reconciled." },
            new() { Step = 5, What = "Approve evidence and continuity standards (SLAs, escalation, recipients)", Accountable = "Platform Owner", Screen = "Admin › SLAs and notifications", Link = "/Admin/Intake",
                    Done = s.StandardsApproved > 0, Status = s.StandardsApproved > 0 ? "Standards approved." : "Not approved yet." },
            new() { Step = 6, What = "Brownfield discovery: sync inventory, register or claim what exists", Accountable = "Platform Owner", Screen = "Registry › Discovery", Link = "/Registry/Discovery",
                    Done = s.InventoryRuns > 0 && s.UnhandledDiscovered == 0, Status = s.InventoryRuns == 0 ? "No inventory sync has succeeded yet." : $"{s.UnhandledDiscovered} discovered items not claimed or under a finding; {s.OpenUnregistered} open unregistered findings." },
            new() { Step = 7, What = "Approve funding envelopes for every active funding owner", Accountable = "LOB business owners", Screen = "Registry › Envelopes", Link = "/Registry/Envelopes",
                    Done = s.ActiveOwners > 0 && s.OwnersWithoutEnvelope == 0, Status = $"{s.OwnersWithoutEnvelope} active funding owners without an active envelope." },
            new() { Step = 8, What = "Open intake", Accountable = "Platform Owner", Screen = "Admin › Intake switch", Link = "/Admin/Intake",
                    Done = string.Equals(s.IntakeOpen, "true", StringComparison.OrdinalIgnoreCase), Status = string.Equals(s.IntakeOpen, "true", StringComparison.OrdinalIgnoreCase) ? "Intake is open." : "Intake is closed." },
        };
    }

    private sealed class SetupCounts
    {
        public int PlatformOwners { get; init; }
        public int ActiveRaci { get; init; }
        public int ActiveOwners { get; init; }
        public int DraftOwners { get; init; }
        public int ApprovedServices { get; init; }
        public int ServicesWithoutOwner { get; init; }
        public int ServicesWithoutTemplate { get; init; }
        public int CommercialItems { get; init; }
        public int UnreconciledItems { get; init; }
        public int StandardsApproved { get; init; }
        public int InventoryRuns { get; init; }
        public int UnhandledDiscovered { get; init; }
        public int OpenUnregistered { get; init; }
        public int OwnersWithoutEnvelope { get; init; }
        public string? IntakeOpen { get; init; }
    }

    private const string TaskSelect =
        """
        SELECT t.TaskId, t.PublicId AS TaskPublicId, t.TaskType, t.Title, t.StageCode, t.AssignedRoleCode, rd.Name AS RoleName,
               t.AssignedPersonId, ap.DisplayName AS AssignedName, t.StartedUtc, t.DueUtc, t.EscalationLevel, t.Discipline, t.IsBlocking,
               t.RequestId, r.PublicId AS RequestPublicId, r.RequestNumber, r.Title AS RequestTitle, r.JourneyCode, fo.Name AS FundingOwnerName,
               t.RelatedEntityType, t.RelatedEntityId, COUNT(*) OVER () AS TotalRows
        FROM wf.Task t
        LEFT JOIN wf.Request r ON r.RequestId = t.RequestId
        LEFT JOIN core.FundingOwner fo ON fo.FundingOwnerId = COALESCE(t.FundingOwnerId, r.FundingOwnerId)
        LEFT JOIN sec.RoleDefinition rd ON rd.RoleCode = t.AssignedRoleCode
        LEFT JOIN sec.Person ap ON ap.PersonId = t.AssignedPersonId
        """;

    private static readonly Dictionary<string, string> TaskSorts = new()
    {
        ["due"] = "COALESCE(t.DueUtc, '9999-12-31')", ["started"] = "t.StartedUtc", ["title"] = "t.Title", ["request"] = "r.RequestNumber", ["owner"] = "fo.Name",
    };

    /// <summary>Open tasks the person can act on: assigned to them, or through a role in scope, or a delegation; SoD exclusions removed.</summary>
    public async Task<Paged<TaskListItem>> MyWorkAsync(ListQuery q)
    {
        await using var c = await db.OpenUserAsync();
        var sql = TaskSelect + $"""

            WHERE t.Status = 'OPEN'
              AND NOT EXISTS (SELECT 1 FROM wf.TaskExclusion x WHERE x.TaskId = t.TaskId AND x.PersonId = @me)
              AND (t.AssignedPersonId = @me
                   OR (t.AssignedPersonId IS NULL AND (
                        EXISTS (SELECT 1 FROM sec.RoleAssignment ra
                                WHERE ra.PersonId = @me AND ra.RoleCode = t.AssignedRoleCode
                                  AND ra.EffectiveFrom <= @today AND (ra.EffectiveTo IS NULL OR ra.EffectiveTo >= @today)
                                  AND (t.ScopeType = 'TENANT' AND ra.ScopeType = 'TENANT' OR ra.ScopeType = 'FUNDING_OWNER' AND ra.ScopeId = t.ScopeId))
                        OR (t.AssignedRoleCode IN ('LOB_BUSINESS_OWNER','SHARED_SERVICE_OWNER') AND t.StageCode IN ('B5') AND EXISTS (
                                SELECT 1 FROM sec.Delegation d WHERE d.DelegatePersonId = @me AND d.FundingOwnerId = t.ScopeId AND d.Status = 'ACTIVE'
                                  AND d.EffectiveFrom <= @today AND (d.EffectiveTo IS NULL OR d.EffectiveTo >= @today))))))
              AND (@like IS NULL OR t.Title LIKE @like OR r.RequestNumber LIKE @like OR fo.Name LIKE @like)
            {q.OrderBy(TaskSorts, "due")}
            OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY
            """;
        return (await c.QueryAsync<TaskListItem>(sql, new { me = me.PersonId, today = DateTime.UtcNow.Date, like = q.Like, q.Offset, q.PageSize })).ToPaged(q);
    }

    /// <summary>A shared queue: open tasks for the given roles and stages (claim and release).</summary>
    public async Task<Paged<TaskListItem>> QueueAsync(ListQuery q, string[] roles, string[]? stages, string? discipline)
    {
        await using var c = await db.OpenUserAsync();
        var sql = TaskSelect + $"""

            WHERE t.Status = 'OPEN' AND t.AssignedRoleCode IN @roles
              AND (@anyStage = 0 OR t.StageCode IN @stages)
              AND (@discipline IS NULL OR t.Discipline = @discipline)
              AND (@like IS NULL OR t.Title LIKE @like OR r.RequestNumber LIKE @like OR fo.Name LIKE @like)
            {q.OrderBy(TaskSorts, "due")}
            OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY
            """;
        return (await c.QueryAsync<TaskListItem>(sql, new { roles, anyStage = stages is { Length: > 0 } ? 1 : 0, stages = stages ?? new[] { "" }, discipline, like = q.Like, q.Offset, q.PageSize })).ToPaged(q);
    }

    private static readonly Dictionary<string, string> RequestSorts = new()
    {
        ["number"] = "r.RequestNumber", ["title"] = "r.Title", ["state"] = "r.State", ["activity"] = "r.LastActivityUtc", ["owner"] = "fo.Name", ["journey"] = "r.JourneyCode",
    };

    /// <summary>Requests visible to the person (row-level security decides); "mine" limits to requester or sponsor.</summary>
    public async Task<Paged<RequestListItem>> RequestsAsync(ListQuery q, bool mine, string? state = null)
    {
        await using var c = await db.OpenUserAsync();
        var sql = $"""
            SELECT r.RequestId, r.PublicId, r.RequestNumber, r.JourneyCode, d.Name AS JourneyName, r.Title, r.State, r.CurrentStage, s.Name AS StageName,
                   fo.Name AS FundingOwnerName, p.DisplayName AS RequesterName, r.CreatedUtc, r.LastActivityUtc, COUNT(*) OVER () AS TotalRows
            FROM wf.Request r
            JOIN wf.WorkflowDefinition d ON d.DefinitionId = r.DefinitionId
            LEFT JOIN wf.StageDefinition s ON s.DefinitionId = r.DefinitionId AND s.StageCode = r.CurrentStage
            JOIN core.FundingOwner fo ON fo.FundingOwnerId = r.FundingOwnerId
            JOIN sec.Person p ON p.PersonId = r.RequesterPersonId
            WHERE (@mine = 0 OR r.RequesterPersonId = @me OR r.SponsorPersonId = @me)
              AND (@state IS NULL OR r.State = @state)
              AND (@like IS NULL OR r.Title LIKE @like OR r.RequestNumber LIKE @like OR fo.Name LIKE @like)
            {q.OrderBy(RequestSorts, "activity", true)}
            OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY
            """;
        return (await c.QueryAsync<RequestListItem>(sql, new { mine = mine ? 1 : 0, me = me.PersonId, state, like = q.Like, q.Offset, q.PageSize })).ToPaged(q);
    }

    public async Task<RequestDetail?> RequestAsync(Guid publicId)
    {
        await using var c = await db.OpenUserAsync();
        var r = await c.QuerySingleOrDefaultAsync<RequestRow>("SELECT * FROM wf.Request WHERE PublicId = @publicId", new { publicId });
        if (r is null) return null;
        var head = await c.QuerySingleAsync<(string JourneyName, string FoName, Guid FoPublic, string? ServiceName, string Requester, string? Sponsor)>(
            """
            SELECT d.Name, fo.Name, fo.PublicId, s.Name, rp.DisplayName, sp.DisplayName
            FROM wf.Request r JOIN wf.WorkflowDefinition d ON d.DefinitionId = r.DefinitionId
            JOIN core.FundingOwner fo ON fo.FundingOwnerId = r.FundingOwnerId
            LEFT JOIN core.Service s ON s.ServiceId = r.ServiceId
            JOIN sec.Person rp ON rp.PersonId = r.RequesterPersonId
            LEFT JOIN sec.Person sp ON sp.PersonId = r.SponsorPersonId
            WHERE r.RequestId = @RequestId
            """, new { r.RequestId });
        var workload = r.WorkloadId is int wid ? await c.QuerySingleOrDefaultAsync<Workload>("SELECT * FROM core.Workload WHERE WorkloadId = @wid", new { wid }) : null;
        var cc = workload?.CostCenterId is int ccid ? await c.ExecuteScalarAsync<string>("SELECT Code FROM core.CostCenter WHERE CostCenterId = @ccid", new { ccid }) : null;
        var stages = (await c.QueryAsync<StageDefinition>("SELECT * FROM wf.StageDefinition WHERE DefinitionId = @DefinitionId ORDER BY SortOrder", new { r.DefinitionId })).ToList();
        var timeline = (await c.QueryAsync<TimelineItem>(
            "SELECT e.Utc, e.EventType, e.FromState, e.ToState, e.StageCode, e.Text, p.DisplayName AS PersonName FROM wf.RequestEvent e LEFT JOIN sec.Person p ON p.PersonId = e.PersonId WHERE e.RequestId = @RequestId ORDER BY e.RequestEventId DESC",
            new { r.RequestId })).ToList();
        var tasks = (await c.QueryAsync<TaskView>(
            """
            SELECT t.TaskId, t.PublicId, t.TaskType, t.Title, t.StageCode, t.AssignedRoleCode, rd.Name AS RoleName, t.AssignedPersonId, ap.DisplayName AS AssignedName, t.ClaimedUtc,
                   t.Status, t.IsBlocking, t.StartedUtc, t.DueUtc, t.Outcome, cp.DisplayName AS CompletedByName, t.CompletedUtc, t.Notes, t.DecisionType, t.RaciActivityCode,
                   t.Discipline, t.Reason, t.RelatedEntityType
            FROM wf.Task t
            LEFT JOIN sec.RoleDefinition rd ON rd.RoleCode = t.AssignedRoleCode
            LEFT JOIN sec.Person ap ON ap.PersonId = t.AssignedPersonId
            LEFT JOIN sec.Person cp ON cp.PersonId = t.CompletedByPersonId
            WHERE t.RequestId = @RequestId ORDER BY CASE t.Status WHEN 'OPEN' THEN 0 ELSE 1 END, t.TaskId DESC
            """, new { r.RequestId })).ToList();
        var exclusions = (await c.QueryAsync<(long TaskId, string Name, string Rule)>(
            "SELECT x.TaskId, p.DisplayName, x.RuleCode FROM wf.TaskExclusion x JOIN sec.Person p ON p.PersonId = x.PersonId JOIN wf.Task t ON t.TaskId = x.TaskId WHERE t.RequestId = @RequestId",
            new { r.RequestId })).ToList();
        foreach (var t in tasks)
        {
            var ex = exclusions.Where(e => e.TaskId == t.TaskId).Select(e => $"{e.Name} ({e.Rule})").ToList();
            if (ex.Count > 0) t.ExcludedText = string.Join(", ", ex);
        }
        var decisions = (await c.QueryAsync<DecisionView>(
            """
            SELECT d.DecisionId, d.PublicId, d.DecisionType, d.Outcome, p.DisplayName AS DeciderName, d.DeciderRoleCode, d.DelegationId, v.VersionNo AS RaciVersionNo,
                   d.RaciActivityCode, d.StageCode, d.ScopeText, d.ConditionsText, d.ReasonText, d.RequiredEvidenceText, d.ExpiresOn, d.NextReviewOn, d.Source, d.DecidedAtUtc
            FROM wf.Decision d JOIN sec.Person p ON p.PersonId = d.DeciderPersonId JOIN sec.RaciVersion v ON v.RaciVersionId = d.RaciVersionId
            WHERE d.RequestId = @RequestId ORDER BY d.DecisionId DESC
            """, new { r.RequestId })).ToList();
        var evidence = (await c.QueryAsync<EvidenceView>(
            """
            SELECT e.EvidenceId, e.PublicId, e.EvidenceType, e.FileName, e.SizeBytes, CONVERT(varchar(64), e.Sha256, 2) AS Sha256Hex, e.Description, e.SourceSystem,
                   p.DisplayName AS UploadedByName, e.UploadedUtc, l.LinkType, CAST(CASE WHEN ec.PurgedUtc IS NULL THEN 0 ELSE 1 END AS bit) AS Purged
            FROM evd.EvidenceLink l JOIN evd.EvidenceItem e ON e.EvidenceId = l.EvidenceId
            JOIN sec.Person p ON p.PersonId = e.UploadedByPersonId
            LEFT JOIN evd.EvidenceContent ec ON ec.ContentId = e.ContentId
            WHERE (l.LinkType = 'REQUEST' AND l.LinkId = @RequestId)
               OR (l.LinkType = 'DECISION' AND l.LinkId IN (SELECT DecisionId FROM wf.Decision WHERE RequestId = @RequestId))
               OR (l.LinkType = 'VERIFICATION' AND l.LinkId IN (SELECT VerificationId FROM evd.Verification WHERE RequestId = @RequestId))
            ORDER BY e.EvidenceId DESC
            """, new { r.RequestId })).ToList();
        var tickets = (await c.QueryAsync<TicketView>("SELECT Kind, ExternalNumber, ExternalUrl, State, CreatedUtc, ClosedUtc, ClosureNotes FROM wf.ItsmTicket WHERE RequestId = @RequestId ORDER BY ItsmTicketId DESC", new { r.RequestId })).ToList();
        var transitions = (await c.QueryAsync<TransitionDefinition>("SELECT * FROM wf.TransitionDefinition WHERE DefinitionId = @DefinitionId", new { r.DefinitionId })).ToList();
        var meters = workload is null ? new List<string>() : (await c.QueryAsync<string>("SELECT MeterCode FROM core.WorkloadMeter WHERE WorkloadId = @WorkloadId", new { workload.WorkloadId })).ToList();
        var verifications = (await c.QueryAsync<VerificationView>(
            "SELECT v.PublicId, v.Method, v.Result, p.DisplayName AS VerifierName, v.VerifiedUtc, v.ChecksJson FROM evd.Verification v JOIN sec.Person p ON p.PersonId = v.VerifierPersonId WHERE v.RequestId = @RequestId ORDER BY v.VerificationId DESC",
            new { r.RequestId })).ToList();
        var stage = stages.FirstOrDefault(s => s.StageCode == r.CurrentStage);
        return new RequestDetail
        {
            Request = r, JourneyName = head.JourneyName, FundingOwnerName = head.FoName, FundingOwnerPublicId = head.FoPublic, ServiceName = head.ServiceName,
            RequesterName = head.Requester, SponsorName = head.Sponsor, Workload = workload, CostCenterCode = cc, Stage = stage, Stages = stages, Timeline = timeline,
            Tasks = tasks, Decisions = decisions, Evidence = evidence, Tickets = tickets, Transitions = transitions, Meters = meters, Verifications = verifications,
            WhoDoesWhat = stage?.RaciActivityCode is null ? Array.Empty<RaciLine>() : await WhoDoesWhatAsync(c, stage.RaciActivityCode, r.FundingOwnerId),
        };
    }

    /// <summary>Resolves the RACI row for an activity to people today, in the request's scope.</summary>
    public static async Task<IReadOnlyList<RaciLine>> WhoDoesWhatAsync(Microsoft.Data.SqlClient.SqlConnection c, string activityCode, int fundingOwnerId)
    {
        var lines = (await c.QueryAsync<(string FunctionCode, string FunctionName, string RaciValue, string? DecisionRoleCode, string? WorkRoleCode, string? ConsultRoleCode)>(
            """
            SELECT f.FunctionCode, f.Name, c.RaciValue, f.DecisionRoleCode, f.WorkRoleCode, f.ConsultRoleCode
            FROM sec.RaciCell c JOIN sec.RaciFunction f ON f.FunctionCode = c.FunctionCode
            JOIN sec.RaciVersion v ON v.RaciVersionId = c.RaciVersionId AND v.Status = 'ACTIVE'
            WHERE c.ActivityCode = @activityCode ORDER BY f.SortOrder
            """, new { activityCode })).ToList();
        var ownerType = await c.ExecuteScalarAsync<string>("SELECT OwnerType FROM core.FundingOwner WHERE FundingOwnerId = @fundingOwnerId", new { fundingOwnerId });
        var result = new List<RaciLine>();
        var today = DateTime.UtcNow.Date;
        foreach (var l in lines)
        {
            var role = l.RaciValue switch
            {
                "A" or "A/R" => l.DecisionRoleCode,
                "R" => l.WorkRoleCode,
                "C" => l.ConsultRoleCode,
                _ => l.DecisionRoleCode,
            };
            if (role == Roles.LobBusinessOwner && ownerType == "ENTERPRISE_SHARED") role = Roles.SharedServiceOwner;
            var people = role is null ? new List<string>() : (await c.QueryAsync<string>(
                """
                SELECT DISTINCT p.DisplayName FROM sec.RoleAssignment ra JOIN sec.Person p ON p.PersonId = ra.PersonId
                JOIN sec.RoleDefinition rd ON rd.RoleCode = ra.RoleCode
                WHERE ra.RoleCode = @role AND ra.EffectiveFrom <= @today AND (ra.EffectiveTo IS NULL OR ra.EffectiveTo >= @today)
                  AND (rd.ScopeType = 'TENANT' AND ra.ScopeType = 'TENANT' OR ra.ScopeType = 'FUNDING_OWNER' AND ra.ScopeId = @fundingOwnerId)
                """, new { role, today, fundingOwnerId })).ToList();
            result.Add(new RaciLine
            {
                FunctionCode = l.FunctionCode, FunctionName = l.FunctionName, RaciValue = l.RaciValue, RoleCode = role,
                People = l.FunctionCode == Functions.Microsoft ? "Supplies evidence and advice; no account." : (people.Count == 0 ? "Nobody holds this role yet." : string.Join(", ", people)),
            });
        }
        return result;
    }
}
