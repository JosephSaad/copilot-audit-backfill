using Dapper;
using Governance.Application.Data;
using Governance.Domain;

namespace Governance.Application.Queries;

public sealed record Option(string Value, string Text);

/// <summary>A person offered in a picker for one funding owner (the page filters by the funding owner chosen).</summary>
public sealed class ScopedPersonOption
{
    public int PersonId { get; init; }
    public int FundingOwnerId { get; init; }
    public string Text { get; init; } = "";
}

public sealed class FundingOwnerListItem : ICounted
{
    public int FundingOwnerId { get; init; }
    public Guid PublicId { get; init; }
    public string Code { get; init; } = "";
    public string Name { get; init; } = "";
    public string OwnerType { get; init; } = "";
    public string Status { get; init; } = "";
    public string? BusinessOwnerName { get; init; }
    public int CostCenters { get; init; }
    public int Workloads { get; init; }
    public int ActiveEnvelopes { get; init; }
    public int TotalRows { get; init; }
}

public sealed class ChecklistItem
{
    public int No { get; init; }
    public string Text { get; init; } = "";
    public bool Done { get; init; }
    public string Detail { get; init; } = "";
}

public sealed class FundingOwnerDetail
{
    public FundingOwner Owner { get; init; } = new();
    public string? BusinessOwnerName { get; init; }
    public string? FinancePartnerName { get; init; }
    public string? ParentName { get; init; }
    public IReadOnlyList<CostCenterRow> CostCenters { get; init; } = Array.Empty<CostCenterRow>();
    public IReadOnlyList<RoleAssignmentRow> Roles { get; init; } = Array.Empty<RoleAssignmentRow>();
    public IReadOnlyList<DelegationRow> Delegations { get; init; } = Array.Empty<DelegationRow>();
    public IReadOnlyList<EnvelopeRow> Envelopes { get; init; } = Array.Empty<EnvelopeRow>();
    public IReadOnlyList<AttributionRuleRow> Attribution { get; init; } = Array.Empty<AttributionRuleRow>();
    public IReadOnlyList<AccessPackageRow> AccessPackages { get; init; } = Array.Empty<AccessPackageRow>();
    public IReadOnlyList<ChecklistItem> Checklist { get; init; } = Array.Empty<ChecklistItem>();
    public int AttributedPeople { get; init; }
}

public sealed class CostCenterRow : ICounted
{
    public int CostCenterId { get; init; }
    public Guid PublicId { get; init; }
    public string Code { get; init; } = "";
    public string Name { get; init; } = "";
    public string? FinanceNode { get; init; }
    public int FundingOwnerId { get; init; }
    public string? FundingOwnerName { get; init; }
    public bool ValidatedByFinance { get; init; }
    public DateTime EffectiveFrom { get; init; }
    public DateTime? EffectiveTo { get; init; }
    public int TotalRows { get; init; }
}

public sealed class RoleAssignmentRow : ICounted
{
    public int RoleAssignmentId { get; init; }
    public Guid PublicId { get; init; }
    public int PersonId { get; init; }
    public string PersonName { get; init; } = "";
    public string? Upn { get; init; }
    public string RoleCode { get; init; } = "";
    public string RoleName { get; init; } = "";
    public string ScopeType { get; init; } = "";
    public int? ScopeId { get; init; }
    public string? ScopeName { get; init; }
    public DateTime EffectiveFrom { get; init; }
    public DateTime? EffectiveTo { get; init; }
    public string Source { get; init; } = "";
    public DateTime? AcceptedUtc { get; init; }
    public DateTime? ReviewDueOn { get; init; }
    public int TotalRows { get; init; }
}

public sealed class DelegationRow
{
    public int DelegationId { get; init; }
    public Guid PublicId { get; init; }
    public string DelegateName { get; init; } = "";
    public string GrantedByName { get; init; } = "";
    public decimal? MaxCreditsPerRequest { get; init; }
    public DateTime EffectiveFrom { get; init; }
    public DateTime? EffectiveTo { get; init; }
    public string Status { get; init; } = "";
    public string? Envelopes { get; init; }
}

public sealed class EnvelopeRow : ICounted
{
    public int EnvelopeId { get; init; }
    public Guid PublicId { get; init; }
    public int FundingOwnerId { get; init; }
    public string FundingOwnerName { get; init; } = "";
    public string ServiceName { get; init; } = "";
    public DateTime PeriodStart { get; init; }
    public DateTime PeriodEnd { get; init; }
    public decimal Amount { get; init; }
    public string Unit { get; init; } = "";
    public string Status { get; init; } = "";
    public decimal Committed { get; init; }
    public long? ApprovedDecisionId { get; init; }
    public int TotalRows { get; init; }
}

public sealed class AttributionRuleRow
{
    public int AttributionRuleId { get; init; }
    public string? MatchDivision { get; init; }
    public string? MatchCostCenter { get; init; }
    public string? MatchDepartment { get; init; }
    public int Priority { get; init; }
    public DateTime EffectiveFrom { get; init; }
    public DateTime? EffectiveTo { get; init; }
}

public sealed class AccessPackageRow
{
    public Guid AccessPackageId { get; init; }
    public string DisplayName { get; init; } = "";
    public string ServiceName { get; init; } = "";
    public string Tier { get; init; } = "";
    public Guid? PolicyGroupId { get; init; }
    public Guid? CatalogId { get; init; }
}

public sealed class ServiceRow : ICounted
{
    public int ServiceId { get; init; }
    public Guid PublicId { get; init; }
    public string Code { get; init; } = "";
    public string Name { get; init; } = "";
    public string? Harness { get; init; }
    public string RouteType { get; init; } = "";
    public string TenantAvailability { get; init; } = "";
    public string InstitutionalStatus { get; init; } = "";
    public bool IsCustomerFacing { get; init; }
    public string Audiences { get; init; } = "";
    public string DataClasses { get; init; } = "";
    public string AccessTiers { get; init; } = "";
    public int? OperationalOwnerPersonId { get; init; }
    public string? OperationalOwnerName { get; init; }
    public DateTime? ReviewOn { get; init; }
    public string? Description { get; init; }
    public int? ActiveTemplateVersion { get; init; }
    public int TotalRows { get; init; }
}

public sealed class TemplateRow : ICounted
{
    public int TemplateId { get; init; }
    public Guid PublicId { get; init; }
    public int ServiceId { get; init; }
    public string ServiceName { get; init; } = "";
    public int VersionNo { get; init; }
    public string Status { get; init; } = "";
    public string SettingsJson { get; init; } = "{}";
    public string? PermittedExceptions { get; init; }
    public string OperatorRoles { get; init; } = "";
    public string? Notes { get; init; }
    public string? PlatformOwnerApprovalName { get; init; }
    public string? SteeringApprovalName { get; init; }
    public int? PlatformOwnerApprovalPersonId { get; init; }
    public int? SteeringApprovalPersonId { get; init; }
    public DateTime? ActivatedUtc { get; init; }
    public int Checks { get; init; }
    public byte[]? RowVer { get; init; }
    public int TotalRows { get; init; }
}

public sealed class ControlCheckRow
{
    public int ControlCheckId { get; init; }
    public int TemplateId { get; init; }
    public string CheckCode { get; init; } = "";
    public string Description { get; init; } = "";
    public string Method { get; init; } = "";
    public string? SettingKey { get; init; }
    public bool IsDenyCase { get; init; }
    public string? CaveatCode { get; init; }
    public int SortOrder { get; init; }
}

public sealed class WorkloadListItem : ICounted
{
    public int WorkloadId { get; init; }
    public Guid PublicId { get; init; }
    public string Name { get; init; } = "";
    public string WorkloadType { get; init; } = "";
    public string FundingOwnerName { get; init; } = "";
    public string? ServiceName { get; init; }
    public string? LifecycleStage { get; init; }
    public string? SolutionClass { get; init; }
    public string? Criticality { get; init; }
    public string State { get; init; } = "";
    public DateTime? NextReviewOn { get; init; }
    public string? CostCenterCode { get; init; }
    public int TotalRows { get; init; }
}

public sealed class WorkloadDetail
{
    public Workload Workload { get; init; } = new();
    public string FundingOwnerName { get; init; } = "";
    public string? CostCenterCode { get; init; }
    public string? ServiceName { get; init; }
    public string? ProductOwnerName { get; init; }
    public string? SponsorName { get; init; }
    public IReadOnlyList<BindingRow> Bindings { get; init; } = Array.Empty<BindingRow>();
    public IReadOnlyList<string> Meters { get; init; } = Array.Empty<string>();
    public IReadOnlyList<PostureRow> Posture { get; init; } = Array.Empty<PostureRow>();
    public IReadOnlyList<RequestListItem> Requests { get; init; } = Array.Empty<RequestListItem>();
    public IReadOnlyList<DecisionView> Decisions { get; init; } = Array.Empty<DecisionView>();
    public IReadOnlyList<FindingRow> Findings { get; init; } = Array.Empty<FindingRow>();
    public DateTime? AsOf { get; init; }
}

public sealed class BindingRow
{
    public int BindingId { get; init; }
    public string ObjectType { get; init; } = "";
    public string ExternalId { get; init; } = "";
    public string? DisplayName { get; init; }
    public DateTime EffectiveFrom { get; init; }
    public DateTime? EffectiveTo { get; init; }
}

public sealed class PostureRow
{
    public int PostureId { get; init; }
    public string EnvironmentId { get; init; } = "";
    public decimal AllocationCredits { get; init; }
    public bool TenantPoolEnabled { get; init; }
    public bool PaygLinked { get; init; }
    public string? BillingPolicyId { get; init; }
    public decimal? AgentLimitCredits { get; init; }
    public bool GroupRuleLocksTenantPool { get; init; }
    public DateTime EffectiveFrom { get; init; }
    public DateTime? EffectiveTo { get; init; }
    public decimal? ReadAllocation { get; init; }
    public bool? ReadTenantPool { get; init; }
    public DateTime? ReadAsOf { get; init; }
}

public sealed class CommercialRow : ICounted
{
    public int CommercialItemId { get; init; }
    public Guid PublicId { get; init; }
    public string ItemType { get; init; } = "";
    public string Name { get; init; } = "";
    public decimal? Quantity { get; init; }
    public string? Unit { get; init; }
    public DateTime? StartOn { get; init; }
    public DateTime? EndOn { get; init; }
    public DateTime? RenewalOn { get; init; }
    public bool AutoRenew { get; init; }
    public string? BillingScope { get; init; }
    public string? ContractRef { get; init; }
    public int? PurchaseAuthorityPersonId { get; init; }
    public string? PurchaseAuthorityName { get; init; }
    public bool ReconciledToContract { get; init; }
    public string? Notes { get; init; }
    public byte[]? RowVer { get; init; }
    public int TotalRows { get; init; }
}

public sealed class FindingRow : ICounted
{
    public long FindingId { get; init; }
    public Guid PublicId { get; init; }
    public string FindingType { get; init; } = "";
    public string Severity { get; init; } = "";
    public string? FundingOwnerName { get; init; }
    public int? WorkloadId { get; init; }
    public Guid? WorkloadPublicId { get; init; }
    public string? WorkloadName { get; init; }
    public string? ExternalId { get; init; }
    public string Title { get; init; } = "";
    public string? Details { get; init; }
    public string? Expected { get; init; }
    public string? Actual { get; init; }
    public string Status { get; init; } = "";
    public DateTime RaisedUtc { get; init; }
    public DateTime LastSeenUtc { get; init; }
    public DateTime? DueUtc { get; init; }
    public string? Resolution { get; init; }
    public int TotalRows { get; init; }
}

public sealed class RegistryQueries(Db db)
{
    // ---------------------------------------------------------------- lookups

    public async Task<IReadOnlyList<Option>> FundingOwnerOptionsAsync(bool activeOnly = false)
    {
        await using var c = await db.OpenSystemAsync();
        return (await c.QueryAsync<Option>($"SELECT CAST(FundingOwnerId AS varchar(12)) AS Value, Name + ' (' + Code + ')' AS Text FROM core.FundingOwner {(activeOnly ? "WHERE Status = 'ACTIVE'" : "WHERE Status <> 'RETIRED'")} ORDER BY Name")).ToList();
    }

    public async Task<IReadOnlyList<Option>> ServiceOptionsAsync(bool approvedOnly = false)
    {
        await using var c = await db.OpenSystemAsync();
        return (await c.QueryAsync<Option>($"SELECT CAST(ServiceId AS varchar(12)) AS Value, Name AS Text FROM core.Service {(approvedOnly ? "WHERE InstitutionalStatus IN ('APPROVED','PILOT')" : "")} ORDER BY Name")).ToList();
    }

    public async Task<IReadOnlyList<Option>> PeopleOptionsAsync()
    {
        await using var c = await db.OpenSystemAsync();
        return (await c.QueryAsync<Option>("SELECT CAST(PersonId AS varchar(12)) AS Value, DisplayName + COALESCE(' — ' + UserPrincipalName, '') AS Text FROM sec.Person WHERE AccountEnabled = 1 ORDER BY DisplayName")).ToList();
    }

    /// <summary>Roles that can sponsor a request in a funding owner.</summary>
    public static readonly string[] SponsorRoles = { Roles.WorkloadSponsor, Roles.LobBusinessOwner, Roles.SharedServiceOwner, Roles.ProductOwner };

    /// <summary>
    /// Sponsors: people holding a sponsor role in a funding owner today, one row per funding owner (the page shows only
    /// the chosen funding owner's), never the requester.
    /// </summary>
    public async Task<IReadOnlyList<ScopedPersonOption>> SponsorOptionsAsync(int requesterPersonId)
    {
        await using var c = await db.OpenSystemAsync();
        return (await c.QueryAsync<ScopedPersonOption>(
            """
            SELECT p.PersonId, ra.ScopeId AS FundingOwnerId, p.DisplayName + ' — ' + MIN(rd.Name) AS Text
            FROM sec.RoleAssignment ra JOIN sec.Person p ON p.PersonId = ra.PersonId AND p.AccountEnabled = 1 JOIN sec.RoleDefinition rd ON rd.RoleCode = ra.RoleCode
            WHERE ra.RoleCode IN @roles AND ra.ScopeType = 'FUNDING_OWNER' AND ra.EffectiveFrom <= @today AND (ra.EffectiveTo IS NULL OR ra.EffectiveTo >= @today)
              AND p.PersonId <> @requesterPersonId
            GROUP BY p.PersonId, ra.ScopeId, p.DisplayName
            ORDER BY p.DisplayName
            """, new { roles = SponsorRoles, today = DateTime.UtcNow.Date, requesterPersonId })).ToList();
    }

    /// <summary>People who can own a decision's condition: anyone with a role or delegation in this funding owner, and central role holders.</summary>
    public async Task<IReadOnlyList<Option>> PeopleInScopeAsync(int fundingOwnerId)
    {
        await using var c = await db.OpenSystemAsync();
        return (await c.QueryAsync<Option>(
            """
            SELECT CAST(p.PersonId AS varchar(12)) AS Value, p.DisplayName + ' — ' + MIN(x.What) AS Text
            FROM (
                SELECT ra.PersonId, rd.Name AS What FROM sec.RoleAssignment ra JOIN sec.RoleDefinition rd ON rd.RoleCode = ra.RoleCode
                WHERE ra.EffectiveFrom <= @today AND (ra.EffectiveTo IS NULL OR ra.EffectiveTo >= @today)
                  AND ((ra.ScopeType = 'FUNDING_OWNER' AND ra.ScopeId = @fundingOwnerId) OR (ra.ScopeType = 'TENANT' AND rd.IsCentral = 1 AND rd.RoleCode NOT IN ('WORKBENCH_ADMIN','AUDITOR')))
                UNION ALL
                SELECT d.DelegatePersonId, N'Delegated approver' FROM sec.Delegation d
                WHERE d.FundingOwnerId = @fundingOwnerId AND d.Status = 'ACTIVE' AND d.EffectiveFrom <= @today AND (d.EffectiveTo IS NULL OR d.EffectiveTo >= @today)
            ) x JOIN sec.Person p ON p.PersonId = x.PersonId AND p.AccountEnabled = 1
            GROUP BY p.PersonId, p.DisplayName
            ORDER BY p.DisplayName
            """, new { fundingOwnerId, today = DateTime.UtcNow.Date })).ToList();
    }

    public async Task<IReadOnlyList<Option>> CostCenterOptionsAsync(int? fundingOwnerId = null)
    {
        await using var c = await db.OpenSystemAsync();
        return (await c.QueryAsync<Option>("SELECT CAST(CostCenterId AS varchar(12)) AS Value, Code + ' — ' + Name AS Text FROM core.CostCenter WHERE EffectiveTo IS NULL AND (@fundingOwnerId IS NULL OR FundingOwnerId = @fundingOwnerId) ORDER BY Code", new { fundingOwnerId })).ToList();
    }

    public async Task<IReadOnlyList<Option>> EnvironmentClassOptionsAsync()
    {
        await using var c = await db.OpenSystemAsync();
        return (await c.QueryAsync<Option>("SELECT EnvironmentClassCode AS Value, Name + ' (default agent limit ' + FORMAT(DefaultAgentLimitCredits, 'N0') + ' credits)' AS Text FROM core.EnvironmentClass ORDER BY DefaultAgentLimitCredits")).ToList();
    }

    public async Task<IReadOnlyList<Option>> WorkloadOptionsAsync(bool liveOnly = true)
    {
        await using var c = await db.OpenUserAsync();
        return (await c.QueryAsync<Option>($"SELECT CAST(PublicId AS varchar(40)) AS Value, Name + ' (' + State + ')' AS Text FROM core.Workload {(liveOnly ? "WHERE State NOT IN ('RETIRED','DISCOVERED')" : "")} ORDER BY Name")).ToList();
    }

    public async Task<IReadOnlyDictionary<string, (string Title, string Guidance)>> CaveatsAsync()
    {
        await using var c = await db.OpenSystemAsync();
        return (await c.QueryAsync<(string Code, string Title, string? Guidance)>("SELECT CaveatCode, Title, Guidance FROM ref.Caveat"))
            .ToDictionary(x => x.Code, x => (x.Title, x.Guidance ?? ""));
    }

    /// <summary>Funding owner a person is attributed to: dated manual override first, then Entra division / cost center / department rules (OD2).</summary>
    public async Task<(int? FundingOwnerId, string Basis)> AttributeAsync(int personId)
    {
        await using var c = await db.OpenSystemAsync();
        var today = DateTime.UtcNow.Date;
        var o = await c.QueryFirstOrDefaultAsync<int?>(
            "SELECT TOP (1) FundingOwnerId FROM core.AttributionOverride WHERE PersonId = @personId AND EffectiveFrom <= @today AND (EffectiveTo IS NULL OR EffectiveTo >= @today) ORDER BY EffectiveFrom DESC",
            new { personId, today });
        if (o is int fo) return (fo, "a dated override");
        var p = await c.QuerySingleOrDefaultAsync<Person>("SELECT * FROM sec.Person WHERE PersonId = @personId", new { personId });
        if (p is null) return (null, "");
        var rule = await c.QueryFirstOrDefaultAsync<(int FundingOwnerId, string Basis)>(
            """
            SELECT TOP (1) r.FundingOwnerId,
                   CASE WHEN r.MatchCostCenter IS NOT NULL THEN 'your cost center ' + r.MatchCostCenter
                        WHEN r.MatchDivision IS NOT NULL THEN 'your division ' + r.MatchDivision ELSE 'your department ' + r.MatchDepartment END
            FROM core.AttributionRule r JOIN core.FundingOwner fo ON fo.FundingOwnerId = r.FundingOwnerId AND fo.Status <> 'RETIRED'
            WHERE r.EffectiveFrom <= @today AND (r.EffectiveTo IS NULL OR r.EffectiveTo >= @today)
              AND (r.MatchCostCenter IS NULL OR r.MatchCostCenter = @CostCenterCode)
              AND (r.MatchDivision IS NULL OR r.MatchDivision = @Division)
              AND (r.MatchDepartment IS NULL OR r.MatchDepartment = @Department)
            ORDER BY r.Priority, CASE WHEN r.MatchCostCenter IS NOT NULL THEN 0 ELSE 1 END
            """, new { today, p.CostCenterCode, p.Division, p.Department });
        if (rule.FundingOwnerId != 0) return (rule.FundingOwnerId, rule.Basis);
        // A person holding a LOB role is attributed there when no rule matches.
        var role = await c.QueryFirstOrDefaultAsync<int?>("SELECT TOP (1) ScopeId FROM sec.RoleAssignment WHERE PersonId = @personId AND ScopeType = 'FUNDING_OWNER' AND (EffectiveTo IS NULL OR EffectiveTo >= @today)", new { personId, today });
        return role is int r ? (r, "your role in that funding owner") : (null, "");
    }

    // ---------------------------------------------------------------- funding owners

    private static readonly Dictionary<string, string> FoSorts = new() { ["name"] = "fo.Name", ["code"] = "fo.Code", ["type"] = "fo.OwnerType", ["status"] = "fo.Status" };

    public async Task<Paged<FundingOwnerListItem>> FundingOwnersAsync(ListQuery q)
    {
        await using var c = await db.OpenSystemAsync();   // the register of owners is not confidential; amounts below the LOB level are
        return (await c.QueryAsync<FundingOwnerListItem>($"""
            SELECT fo.FundingOwnerId, fo.PublicId, fo.Code, fo.Name, fo.OwnerType, fo.Status, p.DisplayName AS BusinessOwnerName,
                   (SELECT COUNT(*) FROM core.CostCenter cc WHERE cc.FundingOwnerId = fo.FundingOwnerId AND cc.EffectiveTo IS NULL) AS CostCenters,
                   (SELECT COUNT(*) FROM core.Workload w WHERE w.FundingOwnerId = fo.FundingOwnerId AND w.State <> 'RETIRED') AS Workloads,
                   (SELECT COUNT(*) FROM core.FundingEnvelope e WHERE e.FundingOwnerId = fo.FundingOwnerId AND e.Status = 'ACTIVE') AS ActiveEnvelopes,
                   COUNT(*) OVER () AS TotalRows
            FROM core.FundingOwner fo LEFT JOIN sec.Person p ON p.PersonId = fo.BusinessOwnerPersonId
            WHERE (@like IS NULL OR fo.Name LIKE @like OR fo.Code LIKE @like)
            {q.OrderBy(FoSorts, "name")}
            OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY
            """, new { like = q.Like, q.Offset, q.PageSize })).ToPaged(q);
    }

    public async Task<FundingOwnerDetail?> FundingOwnerAsync(Guid publicId)
    {
        await using var c = await db.OpenSystemAsync();
        var fo = await c.QuerySingleOrDefaultAsync<FundingOwner>("SELECT * FROM core.FundingOwner WHERE PublicId = @publicId", new { publicId });
        if (fo is null) return null;
        var id = fo.FundingOwnerId;
        var today = DateTime.UtcNow.Date;
        var names = await c.QuerySingleAsync<(string? Bo, string? Fp, string? Parent)>(
            "SELECT (SELECT DisplayName FROM sec.Person WHERE PersonId = @b), (SELECT DisplayName FROM sec.Person WHERE PersonId = @f), (SELECT Name FROM core.FundingOwner WHERE FundingOwnerId = @p)",
            new { b = fo.BusinessOwnerPersonId, f = fo.FinancePartnerPersonId, p = fo.ParentFundingOwnerId });
        var ccs = (await c.QueryAsync<CostCenterRow>("SELECT cc.*, 0 AS TotalRows FROM core.CostCenter cc WHERE cc.FundingOwnerId = @id ORDER BY cc.Code", new { id })).ToList();
        var roles = (await RoleAssignmentsAsync(c, ScopeTypes.FundingOwner, id)).ToList();
        var dels = (await c.QueryAsync<DelegationRow>(
            """
            SELECT d.DelegationId, d.PublicId, dp.DisplayName AS DelegateName, gp.DisplayName AS GrantedByName, d.MaxCreditsPerRequest, d.EffectiveFrom, d.EffectiveTo, d.Status,
                   STUFF((SELECT ', ' + s.Name + ' ' + FORMAT(e.PeriodStart, 'MMM yyyy') FROM sec.DelegationEnvelope de JOIN core.FundingEnvelope e ON e.EnvelopeId = de.EnvelopeId JOIN core.Service s ON s.ServiceId = e.ServiceId WHERE de.DelegationId = d.DelegationId FOR XML PATH('')), 1, 2, '') AS Envelopes
            FROM sec.Delegation d JOIN sec.Person dp ON dp.PersonId = d.DelegatePersonId JOIN sec.Person gp ON gp.PersonId = d.GrantedByPersonId
            WHERE d.FundingOwnerId = @id ORDER BY d.Status, dp.DisplayName
            """, new { id })).ToList();
        var envs = (await EnvelopesInternalAsync(c, id)).ToList();
        var attr = (await c.QueryAsync<AttributionRuleRow>("SELECT * FROM core.AttributionRule WHERE FundingOwnerId = @id ORDER BY Priority", new { id })).ToList();
        var packages = (await c.QueryAsync<AccessPackageRow>("SELECT m.AccessPackageId, m.DisplayName, s.Name AS ServiceName, m.Tier, m.PolicyGroupId, m.CatalogId FROM intg.AccessPackageMap m JOIN core.Service s ON s.ServiceId = m.ServiceId WHERE m.FundingOwnerId = @id", new { id })).ToList();
        var attributed = await c.ExecuteScalarAsync<int>(
            """
            SELECT COUNT(DISTINCT p.PersonId) FROM sec.Person p JOIN core.AttributionRule r ON r.FundingOwnerId = @id AND (r.EffectiveTo IS NULL OR r.EffectiveTo >= @today)
             AND (r.MatchCostCenter IS NULL OR r.MatchCostCenter = p.CostCenterCode) AND (r.MatchDivision IS NULL OR r.MatchDivision = p.Division)
             AND (r.MatchDepartment IS NULL OR r.MatchDepartment = p.Department)
            WHERE p.AccountEnabled = 1
            """, new { id, today });
        var activeApprovers = dels.Count(d => d.Status == "ACTIVE" && (d.EffectiveTo is null || d.EffectiveTo >= today))
                              + roles.Count(r => r.RoleCode == Roles.DelegatedApprover && (r.EffectiveTo is null || r.EffectiveTo >= today));
        var ownerRole = fo.OwnerType == "ENTERPRISE_SHARED" ? Roles.SharedServiceOwner : Roles.LobBusinessOwner;
        var ownerAccepted = roles.Any(r => r.RoleCode == ownerRole && r.PersonId == fo.BusinessOwnerPersonId && r.AcceptedUtc is not null);
        var services = (await c.QueryAsync<int>("SELECT DISTINCT ServiceId FROM core.Workload WHERE FundingOwnerId = @id AND ServiceId IS NOT NULL AND State <> 'RETIRED'", new { id })).ToList();
        var activeEnvServices = envs.Where(e => e.Status == "ACTIVE").Select(e => e.ServiceName).ToHashSet();
        var envServiceIds = (await c.QueryAsync<int>("SELECT DISTINCT ServiceId FROM core.FundingEnvelope WHERE FundingOwnerId = @id AND Status = 'ACTIVE'", new { id })).ToHashSet();
        var missingEnv = services.Count(s => !envServiceIds.Contains(s));
        var checklist = new List<ChecklistItem>
        {
            new() { No = 1, Text = "The business owner is named and has accepted the role in the Workbench.", Done = fo.BusinessOwnerPersonId is not null && ownerAccepted,
                    Detail = fo.BusinessOwnerPersonId is null ? "No business owner named." : ownerAccepted ? $"{names.Bo} accepted." : $"{names.Bo} has not accepted the role yet." },
            new() { No = 2, Text = "At least two delegated approvers are named.", Done = activeApprovers >= 2, Detail = $"{activeApprovers} active." },
            new() { No = 3, Text = "Finance has validated the cost centers.", Done = ccs.Count > 0 && ccs.All(x => x.ValidatedByFinance || x.EffectiveTo is not null),
                    Detail = $"{ccs.Count(x => x.ValidatedByFinance)} of {ccs.Count(x => x.EffectiveTo is null)} validated." },
            new() { No = 4, Text = "An envelope is approved for each service the funding owner will use.", Done = envServiceIds.Count > 0 && missingEnv == 0,
                    Detail = envServiceIds.Count == 0 ? "No active envelope." : missingEnv == 0 ? $"Active envelopes for {string.Join(", ", activeEnvServices)}." : $"{missingEnv} services in use without an envelope." },
            new() { No = 5, Text = "Attribution rules are defined and tested against the Entra directory.", Done = attr.Count > 0 && fo.AttributionTested,
                    Detail = attr.Count == 0 ? "No rules." : $"{attr.Count} rules match {attributed} people; {(fo.AttributionTested ? "tested" : "not marked tested")}." },
            new() { No = 6, Text = "Access packages and policy cohorts are mapped for routine services.", Done = packages.Count > 0, Detail = $"{packages.Count} access packages mapped." },
            new() { No = 7, Text = "Notification recipients are confirmed.", Done = fo.NotificationRecipientsConfirmed, Detail = fo.NotificationRecipientsConfirmed ? "Confirmed." : "Not confirmed." },
        };
        return new FundingOwnerDetail
        {
            Owner = fo, BusinessOwnerName = names.Bo, FinancePartnerName = names.Fp, ParentName = names.Parent, CostCenters = ccs, Roles = roles,
            Delegations = dels, Envelopes = envs, Attribution = attr, AccessPackages = packages, Checklist = checklist, AttributedPeople = attributed,
        };
    }

    private static async Task<IEnumerable<RoleAssignmentRow>> RoleAssignmentsAsync(Microsoft.Data.SqlClient.SqlConnection c, string scopeType, int? scopeId)
        => await c.QueryAsync<RoleAssignmentRow>(
            """
            SELECT ra.RoleAssignmentId, ra.PublicId, ra.PersonId, p.DisplayName AS PersonName, p.UserPrincipalName AS Upn, ra.RoleCode, rd.Name AS RoleName,
                   ra.ScopeType, ra.ScopeId, fo.Name AS ScopeName, ra.EffectiveFrom, ra.EffectiveTo, ra.Source, ra.AcceptedUtc, ra.ReviewDueOn, 0 AS TotalRows
            FROM sec.RoleAssignment ra JOIN sec.Person p ON p.PersonId = ra.PersonId JOIN sec.RoleDefinition rd ON rd.RoleCode = ra.RoleCode
            LEFT JOIN core.FundingOwner fo ON fo.FundingOwnerId = ra.ScopeId
            WHERE ra.ScopeType = @scopeType AND (@scopeId IS NULL OR ra.ScopeId = @scopeId)
            ORDER BY CASE WHEN ra.EffectiveTo IS NULL THEN 0 ELSE 1 END, rd.SortOrder, p.DisplayName
            """, new { scopeType, scopeId });

    public async Task<Paged<RoleAssignmentRow>> RoleAssignmentsAsync(ListQuery q, bool centralOnly)
    {
        await using var c = await db.OpenSystemAsync();
        var sorts = new Dictionary<string, string> { ["person"] = "p.DisplayName", ["role"] = "rd.SortOrder", ["scope"] = "fo.Name", ["from"] = "ra.EffectiveFrom" };
        return (await c.QueryAsync<RoleAssignmentRow>($"""
            SELECT ra.RoleAssignmentId, ra.PublicId, ra.PersonId, p.DisplayName AS PersonName, p.UserPrincipalName AS Upn, ra.RoleCode, rd.Name AS RoleName,
                   ra.ScopeType, ra.ScopeId, fo.Name AS ScopeName, ra.EffectiveFrom, ra.EffectiveTo, ra.Source, ra.AcceptedUtc, ra.ReviewDueOn, COUNT(*) OVER () AS TotalRows
            FROM sec.RoleAssignment ra JOIN sec.Person p ON p.PersonId = ra.PersonId JOIN sec.RoleDefinition rd ON rd.RoleCode = ra.RoleCode
            LEFT JOIN core.FundingOwner fo ON fo.FundingOwnerId = ra.ScopeId
            WHERE (@centralOnly = 0 OR rd.IsCentral = 1) AND (@like IS NULL OR p.DisplayName LIKE @like OR rd.Name LIKE @like OR p.UserPrincipalName LIKE @like)
            {q.OrderBy(sorts, "role")}
            OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY
            """, new { centralOnly = centralOnly ? 1 : 0, like = q.Like, q.Offset, q.PageSize })).ToPaged(q);
    }

    // ---------------------------------------------------------------- cost centers

    public async Task<Paged<CostCenterRow>> CostCentersAsync(ListQuery q)
    {
        await using var c = await db.OpenSystemAsync();
        var sorts = new Dictionary<string, string> { ["code"] = "cc.Code", ["name"] = "cc.Name", ["owner"] = "fo.Name", ["validated"] = "cc.ValidatedByFinance" };
        return (await c.QueryAsync<CostCenterRow>($"""
            SELECT cc.*, fo.Name AS FundingOwnerName, COUNT(*) OVER () AS TotalRows
            FROM core.CostCenter cc JOIN core.FundingOwner fo ON fo.FundingOwnerId = cc.FundingOwnerId
            WHERE (@like IS NULL OR cc.Code LIKE @like OR cc.Name LIKE @like OR fo.Name LIKE @like)
            {q.OrderBy(sorts, "code")}
            OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY
            """, new { like = q.Like, q.Offset, q.PageSize })).ToPaged(q);
    }

    // ---------------------------------------------------------------- services and templates

    public async Task<Paged<ServiceRow>> ServicesAsync(ListQuery q)
    {
        await using var c = await db.OpenSystemAsync();
        var sorts = new Dictionary<string, string> { ["name"] = "s.Name", ["status"] = "s.InstitutionalStatus", ["availability"] = "s.TenantAvailability", ["route"] = "s.RouteType" };
        return (await c.QueryAsync<ServiceRow>($"""
            SELECT s.*, p.DisplayName AS OperationalOwnerName,
                   (SELECT VersionNo FROM core.ControlTemplate t WHERE t.ServiceId = s.ServiceId AND t.Status = 'ACTIVE') AS ActiveTemplateVersion,
                   COUNT(*) OVER () AS TotalRows
            FROM core.Service s LEFT JOIN sec.Person p ON p.PersonId = s.OperationalOwnerPersonId
            WHERE (@like IS NULL OR s.Name LIKE @like OR s.Code LIKE @like OR s.Harness LIKE @like)
            {q.OrderBy(sorts, "name")}
            OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY
            """, new { like = q.Like, q.Offset, q.PageSize })).ToPaged(q);
    }

    public async Task<ServiceRow?> ServiceAsync(Guid publicId)
    {
        await using var c = await db.OpenSystemAsync();
        return await c.QuerySingleOrDefaultAsync<ServiceRow>("SELECT s.*, p.DisplayName AS OperationalOwnerName, NULL AS ActiveTemplateVersion, 0 AS TotalRows FROM core.Service s LEFT JOIN sec.Person p ON p.PersonId = s.OperationalOwnerPersonId WHERE s.PublicId = @publicId", new { publicId });
    }

    private const string TemplateSelect =
        """
        SELECT t.*, s.Name AS ServiceName, po.DisplayName AS PlatformOwnerApprovalName, st.DisplayName AS SteeringApprovalName,
               (SELECT COUNT(*) FROM core.ControlCheck cc WHERE cc.TemplateId = t.TemplateId) AS Checks, COUNT(*) OVER () AS TotalRows
        FROM core.ControlTemplate t JOIN core.Service s ON s.ServiceId = t.ServiceId
        LEFT JOIN sec.Person po ON po.PersonId = t.PlatformOwnerApprovalPersonId
        LEFT JOIN sec.Person st ON st.PersonId = t.SteeringApprovalPersonId
        """;

    public async Task<Paged<TemplateRow>> TemplatesAsync(ListQuery q)
    {
        await using var c = await db.OpenSystemAsync();
        var sorts = new Dictionary<string, string> { ["service"] = "s.Name", ["version"] = "t.VersionNo", ["status"] = "t.Status" };
        return (await c.QueryAsync<TemplateRow>(TemplateSelect + $"""

            WHERE (@like IS NULL OR s.Name LIKE @like)
            {q.OrderBy(sorts, "service")}, t.VersionNo DESC
            OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY
            """, new { like = q.Like, q.Offset, q.PageSize })).ToPaged(q);
    }

    public async Task<(TemplateRow? Template, IReadOnlyList<ControlCheckRow> Checks)> TemplateAsync(Guid publicId)
    {
        await using var c = await db.OpenSystemAsync();
        var t = await c.QuerySingleOrDefaultAsync<TemplateRow>(TemplateSelect + " WHERE t.PublicId = @publicId", new { publicId });
        if (t is null) return (null, Array.Empty<ControlCheckRow>());
        var checks = (await c.QueryAsync<ControlCheckRow>("SELECT * FROM core.ControlCheck WHERE TemplateId = @TemplateId ORDER BY SortOrder, CheckCode", new { t.TemplateId })).ToList();
        return (t, checks);
    }

    public async Task<(TemplateRow? Template, IReadOnlyList<ControlCheckRow> Checks)> ActiveTemplateForServiceAsync(int serviceId)
    {
        await using var c = await db.OpenSystemAsync();
        var t = await c.QuerySingleOrDefaultAsync<TemplateRow>(TemplateSelect + " WHERE t.ServiceId = @serviceId AND t.Status = 'ACTIVE'", new { serviceId });
        if (t is null) return (null, Array.Empty<ControlCheckRow>());
        return (t, (await c.QueryAsync<ControlCheckRow>("SELECT * FROM core.ControlCheck WHERE TemplateId = @TemplateId ORDER BY SortOrder", new { t.TemplateId })).ToList());
    }

    // ---------------------------------------------------------------- workloads

    public async Task<Paged<WorkloadListItem>> WorkloadsAsync(ListQuery q, string? state = null)
    {
        await using var c = await db.OpenUserAsync();
        var sorts = new Dictionary<string, string> { ["name"] = "w.Name", ["owner"] = "fo.Name", ["state"] = "w.State", ["stage"] = "w.LifecycleStage", ["review"] = "w.NextReviewOn", ["service"] = "s.Name" };
        return (await c.QueryAsync<WorkloadListItem>($"""
            SELECT w.WorkloadId, w.PublicId, w.Name, w.WorkloadType, fo.Name AS FundingOwnerName, s.Name AS ServiceName, w.LifecycleStage, w.SolutionClass,
                   w.Criticality, w.State, w.NextReviewOn, cc.Code AS CostCenterCode, COUNT(*) OVER () AS TotalRows
            FROM core.Workload w JOIN core.FundingOwner fo ON fo.FundingOwnerId = w.FundingOwnerId
            LEFT JOIN core.Service s ON s.ServiceId = w.ServiceId LEFT JOIN core.CostCenter cc ON cc.CostCenterId = w.CostCenterId
            WHERE (@state IS NULL OR w.State = @state) AND (@like IS NULL OR w.Name LIKE @like OR fo.Name LIKE @like OR s.Name LIKE @like)
            {q.OrderBy(sorts, "name")}
            OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY
            """, new { state, like = q.Like, q.Offset, q.PageSize })).ToPaged(q);
    }

    /// <summary>
    /// A workload as the register said at a point in time (system time, FOR SYSTEM_TIME AS OF) when <paramref name="asOfUtc"/>
    /// is given; reorganizations never rewrite history (H3).
    /// </summary>
    public async Task<WorkloadDetail?> WorkloadAsync(Guid publicId, DateTime? asOfUtc)
    {
        await using var c = await db.OpenUserAsync();
        var asOf = asOfUtc is null ? "" : " FOR SYSTEM_TIME AS OF @asOfUtc";
        var w = await c.QuerySingleOrDefaultAsync<Workload>($"SELECT * FROM core.Workload{asOf} WHERE PublicId = @publicId", new { publicId, asOfUtc });
        if (w is null) return null;
        var names = await c.QuerySingleAsync<(string Fo, string? Cc, string? Svc, string? Po, string? Sp)>(
            $"SELECT (SELECT Name FROM core.FundingOwner{asOf} WHERE FundingOwnerId = @FundingOwnerId), (SELECT Code FROM core.CostCenter{asOf} WHERE CostCenterId = @CostCenterId), (SELECT Name FROM core.Service WHERE ServiceId = @ServiceId), (SELECT DisplayName FROM sec.Person WHERE PersonId = @ProductOwnerPersonId), (SELECT DisplayName FROM sec.Person WHERE PersonId = @SponsorPersonId)",
            new { w.FundingOwnerId, w.CostCenterId, w.ServiceId, w.ProductOwnerPersonId, w.SponsorPersonId, asOfUtc });
        var bindings = (await c.QueryAsync<BindingRow>($"SELECT BindingId, ObjectType, ExternalId, DisplayName, EffectiveFrom, EffectiveTo FROM core.WorkloadBinding{asOf} WHERE WorkloadId = @WorkloadId ORDER BY ObjectType", new { w.WorkloadId, asOfUtc })).ToList();
        var meters = (await c.QueryAsync<string>("SELECT MeterCode FROM core.WorkloadMeter WHERE WorkloadId = @WorkloadId", new { w.WorkloadId })).ToList();
        var posture = (await c.QueryAsync<PostureRow>(
            $"""
            SELECT p.PostureId, p.EnvironmentId, p.AllocationCredits, p.TenantPoolEnabled, p.PaygLinked, p.BillingPolicyId, p.AgentLimitCredits, p.GroupRuleLocksTenantPool, p.EffectiveFrom, p.EffectiveTo,
                   ar.AllocatedCredits AS ReadAllocation, ar.TenantPoolEnabled AS ReadTenantPool, ar.FinishedUtc AS ReadAsOf
            FROM core.ApprovedPosture{asOf} p
            OUTER APPLY (SELECT TOP (1) a.AllocatedCredits, a.TenantPoolEnabled, r.FinishedUtc FROM intg.AllocationReading a JOIN intg.SyncRun r ON r.SyncRunId = a.SyncRunId
                         WHERE a.EnvironmentId = p.EnvironmentId AND r.Status IN ('SUCCEEDED','PARTIAL') ORDER BY a.AllocationReadingId DESC) ar
            WHERE p.WorkloadId = @WorkloadId ORDER BY p.EffectiveFrom DESC
            """, new { w.WorkloadId, asOfUtc })).ToList();
        var requests = (await c.QueryAsync<RequestListItem>(
            """
            SELECT r.RequestId, r.PublicId, r.RequestNumber, r.JourneyCode, d.Name AS JourneyName, r.Title, r.State, r.CurrentStage, NULL AS StageName, '' AS FundingOwnerName,
                   p.DisplayName AS RequesterName, r.CreatedUtc, r.LastActivityUtc, 0 AS TotalRows
            FROM wf.Request r JOIN wf.WorkflowDefinition d ON d.DefinitionId = r.DefinitionId JOIN sec.Person p ON p.PersonId = r.RequesterPersonId
            WHERE r.WorkloadId = @WorkloadId ORDER BY r.RequestId DESC
            """, new { w.WorkloadId })).ToList();
        var decisions = (await c.QueryAsync<DecisionView>(
            """
            SELECT d.DecisionId, d.PublicId, d.DecisionType, d.Outcome, p.DisplayName AS DeciderName, d.DeciderRoleCode, d.DelegationId, v.VersionNo AS RaciVersionNo,
                   d.RaciActivityCode, d.StageCode, d.ScopeText, d.ConditionsText, d.ReasonText, d.RequiredEvidenceText, d.ExpiresOn, d.NextReviewOn, d.Source, d.DecidedAtUtc
            FROM wf.Decision d JOIN sec.Person p ON p.PersonId = d.DeciderPersonId JOIN sec.RaciVersion v ON v.RaciVersionId = d.RaciVersionId
            WHERE d.WorkloadId = @WorkloadId ORDER BY d.DecisionId DESC
            """, new { w.WorkloadId })).ToList();
        var findings = (await c.QueryAsync<FindingRow>(FindingSelect + " WHERE f.WorkloadId = @WorkloadId ORDER BY f.RaisedUtc DESC", new { w.WorkloadId })).ToList();
        return new WorkloadDetail
        {
            Workload = w, FundingOwnerName = names.Fo, CostCenterCode = names.Cc, ServiceName = names.Svc, ProductOwnerName = names.Po, SponsorName = names.Sp,
            Bindings = bindings, Meters = meters, Posture = posture, Requests = requests, Decisions = decisions, Findings = findings, AsOf = asOfUtc,
        };
    }

    // ---------------------------------------------------------------- envelopes and commercial

    private static Task<IEnumerable<EnvelopeRow>> EnvelopesInternalAsync(Microsoft.Data.SqlClient.SqlConnection c, int? fundingOwnerId)
        => c.QueryAsync<EnvelopeRow>(
            """
            SELECT e.EnvelopeId, e.PublicId, e.FundingOwnerId, fo.Name AS FundingOwnerName, s.Name AS ServiceName, e.PeriodStart, e.PeriodEnd, e.Amount, e.Unit, e.Status,
                   COALESCE((SELECT SUM(x.Amount) FROM core.EnvelopeCommitment x WHERE x.EnvelopeId = e.EnvelopeId AND x.Status = 'ACTIVE'), 0) AS Committed,
                   e.ApprovedDecisionId, 0 AS TotalRows
            FROM core.FundingEnvelope e JOIN core.FundingOwner fo ON fo.FundingOwnerId = e.FundingOwnerId JOIN core.Service s ON s.ServiceId = e.ServiceId
            WHERE @fundingOwnerId IS NULL OR e.FundingOwnerId = @fundingOwnerId
            ORDER BY e.Status, s.Name, e.PeriodStart DESC
            """, new { fundingOwnerId });

    public async Task<Paged<EnvelopeRow>> EnvelopesAsync(ListQuery q)
    {
        await using var c = await db.OpenUserAsync();
        var sorts = new Dictionary<string, string> { ["owner"] = "fo.Name", ["service"] = "s.Name", ["start"] = "e.PeriodStart", ["amount"] = "e.Amount", ["status"] = "e.Status" };
        return (await c.QueryAsync<EnvelopeRow>($"""
            SELECT e.EnvelopeId, e.PublicId, e.FundingOwnerId, fo.Name AS FundingOwnerName, s.Name AS ServiceName, e.PeriodStart, e.PeriodEnd, e.Amount, e.Unit, e.Status,
                   COALESCE((SELECT SUM(x.Amount) FROM core.EnvelopeCommitment x WHERE x.EnvelopeId = e.EnvelopeId AND x.Status = 'ACTIVE'), 0) AS Committed,
                   e.ApprovedDecisionId, COUNT(*) OVER () AS TotalRows
            FROM core.FundingEnvelope e JOIN core.FundingOwner fo ON fo.FundingOwnerId = e.FundingOwnerId JOIN core.Service s ON s.ServiceId = e.ServiceId
            WHERE (@like IS NULL OR fo.Name LIKE @like OR s.Name LIKE @like)
            {q.OrderBy(sorts, "owner")}
            OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY
            """, new { like = q.Like, q.Offset, q.PageSize })).ToPaged(q);
    }

    public async Task<Paged<CommercialRow>> CommercialAsync(ListQuery q)
    {
        await using var c = await db.OpenSystemAsync();
        var sorts = new Dictionary<string, string> { ["name"] = "ci.Name", ["type"] = "ci.ItemType", ["renewal"] = "COALESCE(ci.RenewalOn, ci.EndOn)", ["end"] = "ci.EndOn" };
        return (await c.QueryAsync<CommercialRow>($"""
            SELECT ci.*, p.DisplayName AS PurchaseAuthorityName, COUNT(*) OVER () AS TotalRows
            FROM core.CommercialItem ci LEFT JOIN sec.Person p ON p.PersonId = ci.PurchaseAuthorityPersonId
            WHERE (@like IS NULL OR ci.Name LIKE @like OR ci.ContractRef LIKE @like OR ci.BillingScope LIKE @like)
            {q.OrderBy(sorts, "renewal")}
            OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY
            """, new { like = q.Like, q.Offset, q.PageSize })).ToPaged(q);
    }

    // ---------------------------------------------------------------- findings (discovery, drift)

    public const string FindingSelect =
        """
        SELECT f.FindingId, f.PublicId, f.FindingType, f.Severity, fo.Name AS FundingOwnerName, f.WorkloadId, w.PublicId AS WorkloadPublicId, w.Name AS WorkloadName,
               f.ExternalId, f.Title, f.Details, f.Expected, f.Actual, f.Status, f.RaisedUtc, f.LastSeenUtc, f.DueUtc, f.Resolution, COUNT(*) OVER () AS TotalRows
        FROM ops.Finding f LEFT JOIN core.FundingOwner fo ON fo.FundingOwnerId = f.FundingOwnerId LEFT JOIN core.Workload w ON w.WorkloadId = f.WorkloadId
        """;

    public async Task<Paged<FindingRow>> FindingsAsync(ListQuery q, string? typePrefix, bool openOnly)
    {
        await using var c = await db.OpenSystemAsync();
        var sorts = new Dictionary<string, string> { ["raised"] = "f.RaisedUtc", ["severity"] = "CASE f.Severity WHEN 'CRITICAL' THEN 0 WHEN 'HIGH' THEN 1 WHEN 'MEDIUM' THEN 2 ELSE 3 END", ["type"] = "f.FindingType", ["status"] = "f.Status" };
        return (await c.QueryAsync<FindingRow>(FindingSelect + $"""

            WHERE (@prefix IS NULL OR f.FindingType LIKE @prefix + '%') AND (@openOnly = 0 OR f.Status IN ('OPEN','ACKNOWLEDGED'))
              AND (@like IS NULL OR f.Title LIKE @like OR f.ExternalId LIKE @like OR w.Name LIKE @like)
            {q.OrderBy(sorts, "severity")}, f.RaisedUtc DESC
            OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY
            """, new { prefix = typePrefix, openOnly = openOnly ? 1 : 0, like = q.Like, q.Offset, q.PageSize })).ToPaged(q);
    }
}
