using System.Data;
using System.Text.Json;
using Dapper;
using Governance.Domain;
using Microsoft.Data.SqlClient;

namespace Governance.Seed;

/// <summary>IDs the seed created, for the sample requests and the tests.</summary>
public sealed class SeedResult
{
    public Dictionary<string, int> People { get; } = new();
    public Dictionary<string, int> FundingOwners { get; } = new();
    public Dictionary<string, int> Services { get; } = new();
    public Dictionary<(string Lob, string Service), int> Envelopes { get; } = new();
    public Dictionary<string, int> Workloads { get; } = new();
    public Dictionary<string, int> CostCenters { get; } = new();
    public int RaciVersionId { get; set; }
    public int? AdminPersonId { get; set; }
    public bool AlreadySeeded { get; set; }

    public int Person(string key) => People.TryGetValue(key, out var id) ? id : throw new KeyNotFoundException($"Persona '{key}' was not seeded.");
    public PersonaPrincipal As(string key) => new(Person(key), key);
}

/// <summary>Tenant settings written by setup\Setup-TestTenant.ps1 (IDs only).</summary>
public sealed class TenantSettings
{
    public string? AdminUpn { get; set; }
    public string? AdminObjectId { get; set; }
    public string? CatalogId { get; set; }
    public Dictionary<string, string>? CentralGroups { get; set; }
    public List<PolicyGroup>? PolicyGroups { get; set; }

    public sealed class PolicyGroup
    {
        public string? Lob { get; set; }
        public string? Service { get; set; }
        public string? Tier { get; set; }
        public string? GroupId { get; set; }
        public string? Name { get; set; }
        public string? AccessPackageId { get; set; }
    }

    public static TenantSettings? Load(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        return JsonSerializer.Deserialize<TenantSettings>(File.ReadAllText(path), new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }
}

/// <summary>
/// Seeds the demo institution's foundation (setup steps 0 to 8 of section 4.1) as the deployment principal:
/// personas, funding owners passing the activation checklist, cost centers, catalogue, approved templates,
/// patterns, envelopes, delegations, commercial inventory, holidays, RACI v1 activated, then opens intake.
/// </summary>
public sealed class DemoSeeder(string connectionString, DateTime todayUtc)
{
    public const string Version = "1";
    private readonly DateTime _today = todayUtc.Date;

    public async Task<SeedResult> SeedAsync(TenantSettings? tenant, bool force = false, TextWriter? log = null)
    {
        log ??= TextWriter.Null;
        var result = new SeedResult();
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync();

        var marker = await conn.ExecuteScalarAsync<string?>("SELECT SettingValue FROM ref.Setting WHERE SettingKey = 'Seed.Version'");
        if (marker is not null && !force)
        {
            log.WriteLine($"Already seeded (Seed.Version {marker}); loading IDs only.");
            await LoadAsync(conn, result);
            result.AlreadySeeded = true;
            await SeatDemoSeeder.SeedAsync(conn, result, _today, log);
            return result;
        }
        if (marker is not null && await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM wf.Request") > 0)
            throw new InvalidOperationException("--force only works on a database without requests; deploy a fresh database instead.");

        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        var now = DateTime.UtcNow;

        // Step 0: people, then the RACI version approved by two different people and activated.
        foreach (var p in Personas.All())
        {
            var id = await conn.ExecuteScalarAsync<int>(
                """
                INSERT sec.Person (DisplayName, UserPrincipalName, Mail, JobTitle, Department, Division, CostCenterCode, AccountEnabled, IsSynthetic)
                OUTPUT inserted.PersonId VALUES (@DisplayName, @Mail, @Mail, @JobTitle, @Department, @Division, @CostCenter, 1, 1)
                """, new { p.DisplayName, Mail = Personas.MailFor(p.DisplayName), p.JobTitle, p.Department, p.Division, p.CostCenter }, tx);
            result.People[p.Key] = id;
        }
        log.WriteLine($"People: {result.People.Count} synthetic personas.");

        if (tenant?.AdminObjectId is string adminOid && Guid.TryParse(adminOid, out var adminGuid))
        {
            var upn = tenant.AdminUpn ?? "admin";
            var display = string.Join(' ', upn.Split('@')[0].Split('.').Select(s => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..]));
            result.AdminPersonId = await conn.ExecuteScalarAsync<int>(
                """
                IF NOT EXISTS (SELECT 1 FROM sec.Person WHERE EntraObjectId = @adminGuid)
                    INSERT sec.Person (EntraObjectId, UserPrincipalName, DisplayName, Mail, JobTitle, Department, AccountEnabled, IsSynthetic)
                    VALUES (@adminGuid, @upn, @display, @upn, N'Workbench administrator (test tenant)', N'Digital Workplace', 1, 0);
                SELECT PersonId FROM sec.Person WHERE EntraObjectId = @adminGuid;
                """, new { adminGuid, upn, display }, tx);
            result.People["realAdmin"] = result.AdminPersonId.Value;
        }

        var raciId = await conn.ExecuteScalarAsync<int>("SELECT RaciVersionId FROM sec.RaciVersion WHERE VersionNo = 1", transaction: tx);
        await conn.ExecuteAsync(
            """
            UPDATE sec.RaciVersion SET Status = 'ACTIVE', PlatformOwnerApprovalPersonId = @po, PlatformOwnerApprovalUtc = @now,
                   SteeringApprovalPersonId = @st, SteeringApprovalUtc = @now, ActivatedUtc = @now, CreatedByPersonId = COALESCE(CreatedByPersonId, @po)
            WHERE RaciVersionId = @raciId AND Status IN ('DRAFT','PENDING')
            """, new { po = result.Person("platformOwner"), st = result.Person("steering"), now, raciId }, tx);
        result.RaciVersionId = raciId;

        // Step 1: funding owners (Draft until the checklist passes), cost centers, attribution rules, roles.
        foreach (var lob in Personas.Lobs)
        {
            var foId = await conn.ExecuteScalarAsync<int>(
                """
                INSERT core.FundingOwner (Code, Name, OwnerType, BusinessOwnerPersonId, FinancePartnerPersonId, FinanceNode, Status, IsCriticalShared, EffectiveFrom)
                OUTPUT inserted.FundingOwnerId VALUES (@Code, @Name, @OwnerType, @owner, @finance, @node, 'DRAFT', @CriticalShared, @from)
                """, new { lob.Code, lob.Name, lob.OwnerType, owner = result.Person($"{lob.Code}.owner"), finance = result.Person($"{lob.Code}.finance"),
                    node = "FIN-" + lob.Code, lob.CriticalShared, from = new DateTime(_today.Year, 1, 1) }, tx);
            result.FundingOwners[lob.Code] = foId;
            foreach (var (code, name) in lob.CostCenters)
                result.CostCenters[code] = await conn.ExecuteScalarAsync<int>(
                    """
                    INSERT core.CostCenter (Code, Name, FinanceNode, FundingOwnerId, ValidatedByFinance, ValidatedByPersonId, EffectiveFrom)
                    OUTPUT inserted.CostCenterId VALUES (@code, @name, @node, @foId, 1, @fin, @from)
                    """, new { code, name, node = "FIN-" + lob.Code, foId, fin = result.Person("finopsLead"), from = new DateTime(_today.Year, 1, 1) }, tx);
            await conn.ExecuteAsync("INSERT core.AttributionRule (FundingOwnerId, MatchDivision, Priority, EffectiveFrom) VALUES (@foId, @Division, 100, @from)",
                new { foId, lob.Division, from = new DateTime(_today.Year, 1, 1) }, tx);
            foreach (var (code, _) in lob.CostCenters)
                await conn.ExecuteAsync("INSERT core.AttributionRule (FundingOwnerId, MatchCostCenter, Priority, EffectiveFrom) VALUES (@foId, @code, 50, @from)",
                    new { foId, code, from = new DateTime(_today.Year, 1, 1) }, tx);
        }

        foreach (var p in Personas.All())
            foreach (var (role, lob) in p.Roles)
                await conn.ExecuteAsync(
                    """
                    INSERT sec.RoleAssignment (PersonId, RoleCode, ScopeType, ScopeId, EffectiveFrom, Source, AcceptedUtc, ReviewDueOn, GrantedByPersonId)
                    VALUES (@person, @role, @scopeType, @scopeId, @from, 'SEED', @now, @review, @po)
                    """,
                    new
                    {
                        person = result.Person(p.Key), role, scopeType = lob is null ? ScopeTypes.Tenant : ScopeTypes.FundingOwner,
                        scopeId = lob is null ? (int?)null : result.FundingOwners[lob], from = _today.AddDays(-60), now, review = _today.AddYears(1),
                        po = result.Person("platformOwner"),
                    }, tx);
        if (result.AdminPersonId is int admin)
            foreach (var role in new[] { Roles.WorkbenchAdmin, Roles.Auditor })
                await conn.ExecuteAsync(
                    "INSERT sec.RoleAssignment (PersonId, RoleCode, ScopeType, EffectiveFrom, Source, AcceptedUtc, ReviewDueOn) VALUES (@admin, @role, 'TENANT', @from, 'SEED', @now, @review)",
                    new { admin, role, from = _today.AddDays(-1), now, review = _today.AddYears(1) }, tx);
        log.WriteLine($"Funding owners: {string.Join(", ", result.FundingOwners.Keys)}.");

        // Step 2: the approved service catalogue.
        var ops = result.Person("platformOwner");
        foreach (var s in Catalogue)
            result.Services[s.Code] = await conn.ExecuteScalarAsync<int>(
                """
                INSERT core.Service (Code, Name, Harness, RouteType, TenantAvailability, InstitutionalStatus, IsCustomerFacing, Audiences, DataClasses, AccessTiers,
                                     OperationalOwnerPersonId, ReviewOn, Description)
                OUTPUT inserted.ServiceId
                VALUES (@Code, @Name, @Harness, @Route, @Availability, @Status, @Customer, @Audiences, @DataClasses, @Tiers, @owner, @review, @Description)
                """, new { s.Code, s.Name, s.Harness, s.Route, s.Availability, s.Status, s.Customer, s.Audiences, s.DataClasses, s.Tiers,
                    owner = s.Code.StartsWith("CS_") || s.Code == "SHARED_STUDIO" ? result.Person("operator1") : ops, review = _today.AddMonths(6), s.Description }, tx);

        // Step 3: control templates approved by the Platform Owner and Steering (SoD6), with verification checks.
        foreach (var s in Catalogue)
        {
            var templateId = await conn.ExecuteScalarAsync<int>(
                """
                INSERT core.ControlTemplate (ServiceId, VersionNo, Status, SettingsJson, PermittedExceptions, OperatorRoles, Notes, CreatedByPersonId,
                                             PlatformOwnerApprovalPersonId, SteeringApprovalPersonId, ActivatedUtc)
                OUTPUT inserted.TemplateId
                VALUES (@sid, 1, 'DRAFT', @json, @exceptions, @roles, @notes, @architect, @po, @st, NULL)
                """, new
                {
                    sid = result.Services[s.Code], json = JsonSerializer.Serialize(s.Settings), exceptions = s.PermittedExceptions,
                    roles = s.Route == "PPAC_ENVIRONMENT" ? "Power Platform administrator (through ITSM)" : "Microsoft 365 billing administrator (through ITSM)",
                    notes = "Seeded template version 1.", architect = result.Person("architect"), po = result.Person("platformOwner"), st = result.Person("steering"), now,
                }, tx);
            var order = 0;
            foreach (var c in s.Checks)
                await conn.ExecuteAsync(
                    "INSERT core.ControlCheck (TemplateId, CheckCode, Description, Method, SettingKey, IsDenyCase, CaveatCode, SortOrder) VALUES (@templateId, @Code, @Description, @Method, @Key, @Deny, @Caveat, @order)",
                    new { templateId, c.Code, c.Description, c.Method, c.Key, c.Deny, c.Caveat, order = ++order }, tx);
            // Activate after the checks are in: an active template is frozen (060-guards.sql).
            await conn.ExecuteAsync("UPDATE core.ControlTemplate SET Status = 'ACTIVE', ActivatedUtc = @now WHERE TemplateId = @templateId", new { templateId, now }, tx);
        }

        foreach (var p in Patterns)
            await conn.ExecuteAsync(
                """
                INSERT core.ApprovedPattern (Code, Name, ServiceId, Audiences, MaxDataClass, ActionScopes, LifecycleStages, TriggerTypes, Tiers, MaxCreditsPerMonth, Description)
                VALUES (@Code, @Name, @sid, @Audiences, @MaxData, @Actions, @Stages, @Triggers, @Tiers, @Cap, @Description)
                """, new { p.Code, p.Name, sid = result.Services[p.Service], p.Audiences, p.MaxData, p.Actions, p.Stages, p.Triggers, p.Tiers, p.Cap, p.Description }, tx);

        // Step 4: commercial inventory, with purchase and renewal authority.
        var commercial = result.Person("commercial");
        var items = new List<CommercialSpec>
        {
            new("LICENCE", "Microsoft 365 Copilot licenses", 1200m, "SEAT", _today.AddMonths(-10), _today.AddMonths(26), _today.AddMonths(25), false, "Tenant", "EA-2025-0412"),
            new("PACK", "Copilot Studio monthly capacity pack (25,000 credits)", 25000m, "COPILOT_CREDIT", new DateTime(_today.Year, _today.Month, 1),
                new DateTime(_today.Year, _today.Month, 1).AddMonths(1).AddDays(-1), new DateTime(_today.Year, _today.Month, 1).AddMonths(1), true, "Tenant", "EA-2025-0412-P"),
            new("P3", "Copilot Credit pre-purchase plan (P3)", 300000m, "P3_COMMIT_UNIT", _today.AddDays(75).AddYears(-1), _today.AddDays(75), _today.AddDays(75), false,
                "Subscription sub-cc-p3-001", "P3-2025-118"),
            new("PAYG_SCOPE", "Pay-as-you-go billing plan for Enterprise Shared IT", null, null, _today.AddMonths(-6), null, null, false,
                "Subscription sub-cc-payg-002 / billing policy bp-es-it", "AZ-SUB-002"),
        };
        foreach (var c in items)
            await conn.ExecuteAsync(
                """
                INSERT core.CommercialItem (ItemType, Name, Quantity, Unit, StartOn, EndOn, RenewalOn, AutoRenew, BillingScope, ContractRef, PurchaseAuthorityPersonId, ReconciledToContract)
                VALUES (@ItemType, @Name, @Quantity, @Unit, @StartOn, @EndOn, @RenewalOn, @AutoRenew, @BillingScope, @ContractRef, @commercial, 1)
                """, new { c.ItemType, c.Name, c.Quantity, c.Unit, c.StartOn, c.EndOn, c.RenewalOn, c.AutoRenew, c.BillingScope, c.ContractRef, commercial }, tx);

        // Step 5: business calendar (Canadian federal statutory holidays: the demo institution is in Toronto, Eastern time).
        foreach (var (date, name) in Holidays(_today.Year).Concat(Holidays(_today.Year + 1)))
            await conn.ExecuteAsync("IF NOT EXISTS (SELECT 1 FROM ref.Holiday WHERE HolidayDate = @date) INSERT ref.Holiday (HolidayDate, Name) VALUES (@date, @name)", new { date, name }, tx);

        // Step 6: brownfield environments registered with an approved posture (read-back compares against these).
        foreach (var e in Environments)
        {
            var foId = result.FundingOwners[e.Lob];
            var wid = await conn.ExecuteScalarAsync<int>(
                """
                INSERT core.Workload (Name, WorkloadType, FundingOwnerId, CostCenterId, SolutionClass, LifecycleStage, ServiceId, Criticality, TriggerType, DataBoundary,
                                      DataClass, Audience, EnvironmentClassCode, ProductOwnerPersonId, SponsorPersonId, SupportContact, State, NextReviewOn)
                OUTPUT inserted.WorkloadId
                VALUES (@Name, 'ENVIRONMENT', @foId, @cc, @Class, @Stage, @sid, @Criticality, 'USER_INITIATED', @Boundary, 'INTERNAL', @Audience, @EnvClass,
                        @product, @sponsor, @support, @State, @review)
                """, new
                {
                    e.Name, foId, cc = result.CostCenters[e.CostCenter], e.Class, e.Stage, sid = result.Services[e.Service], e.Criticality,
                    Boundary = "Institution tenant; internal data only", Audience = "My line of business", e.EnvClass,
                    product = result.Person($"{e.Lob}.product"), sponsor = result.Person($"{e.Lob}.sponsor"), support = "platform-ops@example.invalid", e.State,
                    review = _today.AddMonths(3),
                }, tx);
            result.Workloads[e.Key] = wid;
            await conn.ExecuteAsync(
                "INSERT core.WorkloadBinding (WorkloadId, FundingOwnerId, ObjectType, ExternalId, DisplayName, EffectiveFrom) VALUES (@wid, @foId, 'PP_ENVIRONMENT', @EnvironmentId, @Name, @from)",
                new { wid, foId, e.EnvironmentId, e.Name, from = _today.AddDays(-30) }, tx);
            await conn.ExecuteAsync(
                """
                INSERT core.ApprovedPosture (WorkloadId, FundingOwnerId, EnvironmentId, AllocationCredits, TenantPoolEnabled, PaygLinked, BillingPolicyId, AgentLimitCredits, GroupRuleLocksTenantPool, EffectiveFrom)
                VALUES (@wid, @foId, @EnvironmentId, @Allocation, @TenantPool, @Payg, @Billing, @Limit, @Lock, @from)
                """, new { wid, foId, e.EnvironmentId, e.Allocation, e.TenantPool, e.Payg, e.Billing, e.Limit, e.Lock, from = _today.AddDays(-30) }, tx);
        }

        // Step 7: envelopes per funding owner and service, each approved by its business owner (a decision record).
        var periodStart = new DateTime(_today.Year, 1, 1);
        var periodEnd = new DateTime(_today.Year, 12, 31);
        foreach (var (lob, service, amount) in EnvelopeAmounts)
        {
            var foId = result.FundingOwners[lob];
            var envId = await conn.ExecuteScalarAsync<int>(
                """
                INSERT core.FundingEnvelope (FundingOwnerId, ServiceId, PeriodStart, PeriodEnd, Amount, Unit, Status)
                OUTPUT inserted.EnvelopeId VALUES (@foId, @sid, @periodStart, @periodEnd, @amount, 'COPILOT_CREDIT', 'ACTIVE')
                """, new { foId, sid = result.Services[service], periodStart, periodEnd, amount }, tx);
            result.Envelopes[(lob, service)] = envId;
            var ownerRole = Personas.Lobs.First(l => l.Code == lob).OwnerType == "ENTERPRISE_SHARED" ? Roles.SharedServiceOwner : Roles.LobBusinessOwner;
            var decisionId = await conn.ExecuteScalarAsync<long>(
                """
                INSERT wf.Decision (PublicId, FundingOwnerId, DecisionType, Outcome, DeciderPersonId, DeciderRoleCode, RaciVersionId, RaciActivityCode,
                                    ScopeText, ExpiresOn, NextReviewOn, Source, DecidedAtUtc)
                OUTPUT inserted.DecisionId
                VALUES (NEWID(), @foId, 'ENVELOPE_APPROVAL', 'APPROVED', @owner, @ownerRole, @raciId, 'FUNDING_BUDGET', @scope, @periodEnd, @review, 'WORKBENCH', @now)
                """, new { foId, owner = result.Person($"{lob}.owner"), ownerRole, raciId, scope = $"{amount:N0} Copilot Credits for {service} in {periodStart:yyyy}.", periodEnd, review = _today.AddMonths(3), now }, tx);
            await conn.ExecuteAsync("UPDATE core.FundingEnvelope SET ApprovedDecisionId = @decisionId WHERE EnvelopeId = @envId", new { decisionId, envId }, tx);
        }

        // Delegations: two approvers per funding owner, within that funding owner's envelopes.
        foreach (var lob in Personas.Lobs)
        {
            var foId = result.FundingOwners[lob.Code];
            foreach (var (key, max) in new[] { ($"{lob.Code}.approver1", 2500m), ($"{lob.Code}.approver2", 1000m) })
            {
                var did = await conn.ExecuteScalarAsync<int>(
                    """
                    INSERT sec.Delegation (FundingOwnerId, DelegatePersonId, GrantedByPersonId, MaxCreditsPerRequest, EffectiveFrom, EffectiveTo, Status, Notes)
                    OUTPUT inserted.DelegationId VALUES (@foId, @delegate, @owner, @max, @from, @to, 'ACTIVE', N'Routine approval (B5) within the envelopes listed.')
                    """, new { foId, @delegate = result.Person(key), owner = result.Person($"{lob.Code}.owner"), max, from = _today.AddDays(-60), to = periodEnd }, tx);
                foreach (var env in result.Envelopes.Where(e => e.Key.Lob == lob.Code))
                    await conn.ExecuteAsync("INSERT sec.DelegationEnvelope (DelegationId, EnvelopeId) VALUES (@did, @env)", new { did, env = env.Value }, tx);
            }
        }

        // Access packages and central groups from the test tenant, when the settings file is there.
        if (tenant?.PolicyGroups is { } groups)
            foreach (var g in groups.Where(g => Guid.TryParse(g.AccessPackageId, out _) && g.Lob is not null && result.FundingOwners.ContainsKey(g.Lob)))
            {
                var serviceCode = g.Service?.Contains("Cowork", StringComparison.OrdinalIgnoreCase) == true ? "COWORK" : "COPILOT_CHAT";
                await conn.ExecuteAsync(
                    """
                    IF NOT EXISTS (SELECT 1 FROM intg.AccessPackageMap WHERE AccessPackageId = @pkg)
                        INSERT intg.AccessPackageMap (AccessPackageId, DisplayName, FundingOwnerId, ServiceId, Tier, PolicyGroupId, CatalogId)
                        VALUES (@pkg, @name, @fo, @sid, @tier, @grp, @cat)
                    """, new
                    {
                        pkg = Guid.Parse(g.AccessPackageId!), name = $"{g.Service} - {g.Lob} - {g.Tier}", fo = result.FundingOwners[g.Lob!], sid = result.Services[serviceCode],
                        tier = g.Tier ?? "Standard", grp = Guid.TryParse(g.GroupId, out var gid) ? gid : (Guid?)null, cat = Guid.TryParse(tenant.CatalogId, out var cid) ? cid : (Guid?)null,
                    }, tx);
            }
        if (tenant?.CentralGroups is { } central)
            foreach (var (key, groupId) in central)
                if (GroupRoles.TryGetValue(key, out var role) && Guid.TryParse(groupId, out var gid))
                    await conn.ExecuteAsync("IF NOT EXISTS (SELECT 1 FROM sec.GroupRoleMap WHERE EntraGroupId = @gid AND RoleCode = @role) INSERT sec.GroupRoleMap (EntraGroupId, RoleCode, GroupName) VALUES (@gid, @role, @name)",
                        new { gid, role, name = $"CGW Test - {key}" }, tx);

        // Activation checklist (section 5.2), then Active.
        await conn.ExecuteAsync("UPDATE core.FundingOwner SET NotificationRecipientsConfirmed = 1, AttributionTested = 1", transaction: tx);
        foreach (var (code, foId) in result.FundingOwners)
        {
            var gaps = await ActivationChecklist.EvaluateAsync(conn, tx, foId, requireAccessPackages: tenant?.PolicyGroups is not null);
            if (gaps.Count > 0) throw new InvalidOperationException($"{code} fails the activation checklist: {string.Join(" ", gaps)}");
            await conn.ExecuteAsync("UPDATE core.FundingOwner SET Status = 'ACTIVE' WHERE FundingOwnerId = @foId", new { foId }, tx);
        }

        // Step 5: the Platform Owner approved the SLAs, escalation ladders and recipients (seeded values; recipients confirmed above).
        await conn.ExecuteAsync(
            """
            MERGE ref.Setting AS t USING (SELECT 'Setup.StandardsApprovedUtc' AS SettingKey) AS s ON t.SettingKey = s.SettingKey
            WHEN MATCHED THEN UPDATE SET SettingValue = @now, UpdatedUtc = SYSUTCDATETIME(), UpdatedByPersonId = @po
            WHEN NOT MATCHED THEN INSERT (SettingKey, SettingValue, Description, UpdatedByPersonId)
                 VALUES ('Setup.StandardsApprovedUtc', @now, N'When the Platform Owner approved SLAs, escalation and recipients (setup step 5).', @po);
            """, new { now = DateTime.UtcNow.ToString("o"), po = result.Person("platformOwner") }, tx);

        // Step 6: brownfield discovery. The demo estate is registered above, so the seeded discovery run finds nothing
        // unclaimed. It is recorded as a test double, never as a real inventory read.
        await conn.ExecuteAsync(
            """
            IF NOT EXISTS (SELECT 1 FROM intg.SyncRun WHERE Integration = 'X4' AND Status = 'SUCCEEDED')
                INSERT intg.SyncRun (Integration, Mode, TriggeredByPersonId, StartedUtc, FinishedUtc, Status, ItemsRead, FindingsRaised, Message)
                VALUES ('X4', 'TEST_DOUBLE', @op, SYSUTCDATETIME(), SYSUTCDATETIME(), 'SUCCEEDED', 0, 0, N'Demo seed: the seeded estate is registered; no inventory was read from Microsoft.');
            """, new { op = result.Person("operator1") }, tx);

        // Step 8: open intake.
        await conn.ExecuteAsync("UPDATE ref.Setting SET SettingValue = 'true', UpdatedUtc = SYSUTCDATETIME() WHERE SettingKey = 'Intake.Open'", transaction: tx);
        await conn.ExecuteAsync(
            "MERGE ref.Setting t USING (SELECT 'Seed.Version' k) s ON t.SettingKey = s.k WHEN MATCHED THEN UPDATE SET SettingValue = @v WHEN NOT MATCHED THEN INSERT (SettingKey, SettingValue, Description) VALUES ('Seed.Version', @v, N'Demo seed applied; rerunning the seed loads IDs only.');",
            new { v = Version }, tx);
        await tx.CommitAsync();
        log.WriteLine("Foundation seeded; intake is open.");
        await SeatDemoSeeder.SeedAsync(conn, result, _today, log);
        return result;
    }

    /// <summary>Reads back the IDs of an already seeded database (personas by mail, funding owners, services...).</summary>
    public static async Task LoadAsync(SqlConnection conn, SeedResult result)
    {
        var byMail = (await conn.QueryAsync<(int PersonId, string Mail)>("SELECT PersonId, Mail FROM sec.Person WHERE IsSynthetic = 1")).ToDictionary(x => x.Mail, x => x.PersonId);
        foreach (var p in Personas.All())
            if (byMail.TryGetValue(Personas.MailFor(p.DisplayName), out var id)) result.People[p.Key] = id;
        foreach (var (id, code) in await conn.QueryAsync<(int, string)>("SELECT FundingOwnerId, Code FROM core.FundingOwner")) result.FundingOwners[code] = id;
        foreach (var (id, code) in await conn.QueryAsync<(int, string)>("SELECT ServiceId, Code FROM core.Service")) result.Services[code] = id;
        foreach (var (id, code) in await conn.QueryAsync<(int, string)>("SELECT CostCenterId, Code FROM core.CostCenter")) result.CostCenters[code] = id;
        foreach (var (envId, fo, svc) in await conn.QueryAsync<(int, string, string)>(
                     "SELECT e.EnvelopeId, f.Code, s.Code FROM core.FundingEnvelope e JOIN core.FundingOwner f ON f.FundingOwnerId = e.FundingOwnerId JOIN core.Service s ON s.ServiceId = e.ServiceId"))
            result.Envelopes[(fo, svc)] = envId;
        foreach (var e in Environments)
        {
            var wid = await conn.ExecuteScalarAsync<int?>("SELECT TOP (1) WorkloadId FROM core.Workload WHERE Name = @Name", new { e.Name });
            if (wid is int w) result.Workloads[e.Key] = w;
        }
        result.RaciVersionId = await conn.ExecuteScalarAsync<int>("SELECT RaciVersionId FROM sec.RaciVersion WHERE VersionNo = 1");
        result.AdminPersonId = await conn.ExecuteScalarAsync<int?>("SELECT TOP (1) PersonId FROM sec.Person WHERE IsSynthetic = 0 AND EntraObjectId IS NOT NULL ORDER BY PersonId");
        if (result.AdminPersonId is int a) result.People["realAdmin"] = a;
    }

    // ------------------------------------------------------------------------------------------
    // Data
    // ------------------------------------------------------------------------------------------

    public static readonly IReadOnlyDictionary<string, string> GroupRoles = new Dictionary<string, string>
    {
        ["PLATFORM_OWNER"] = Roles.PlatformOwner, ["INTAKE"] = Roles.IntakeAnalyst, ["PLATFORM_OPS"] = Roles.PlatformOperator,
        ["ARCHITECT"] = Roles.Architect, ["RISK"] = Roles.RiskReviewer, ["FINOPS"] = Roles.FinOpsAnalyst, ["COMMERCIAL"] = Roles.CommercialApprover,
        ["SOURCING"] = Roles.SourcingAgent, ["AUDITOR"] = Roles.Auditor, ["ADMIN"] = Roles.WorkbenchAdmin,
    };

    private static readonly (string Lob, string Service, decimal Amount)[] EnvelopeAmounts =
    {
        ("RETAIL", "CS_STANDARD", 60000), ("RETAIL", "COPILOT_CHAT", 40000),
        ("WEALTH", "CS_STANDARD", 45000), ("WEALTH", "COPILOT_CHAT", 30000), ("WEALTH", "COWORK", 20000),
        ("COMMERCIAL", "CS_STANDARD", 30000), ("COMMERCIAL", "COPILOT_CHAT", 25000), ("COMMERCIAL", "CS_GITHUB", 15000),
        ("ES-IT", "CS_STANDARD", 80000), ("ES-IT", "SHARED_STUDIO", 150000), ("ES-IT", "COPILOT_CHAT", 50000), ("ES-IT", "SP_AGENTS", 20000),
    };

    private sealed record EnvSpec(string Key, string Lob, string Name, string EnvironmentId, string Service, string CostCenter, string Class, string Stage,
        string Criticality, string EnvClass, string State, decimal Allocation, bool TenantPool, bool Payg, string? Billing, decimal? Limit, bool Lock);

    private static readonly EnvSpec[] Environments =
    {
        new("retailDev", "RETAIL", "Retail makers - development", "9f1c2d4e-0000-4000-8000-00000000a001", "CS_STANDARD", "RB-1100",
            SolutionClasses.Productivity, LifecycleStages.Development, "LOW", "DEVELOPER", WorkloadStates.Development, 5000, false, false, null, 2000, false),
        new("wealthTest", "WEALTH", "Wealth advisory - test", "9f1c2d4e-0000-4000-8000-00000000a002", "CS_STANDARD", "WM-2000",
            SolutionClasses.BusinessApplication, LifecycleStages.Test, "MEDIUM", "TEST", WorkloadStates.Test, 10000, false, false, null, 5000, false),
        new("sharedProd", "ES-IT", "Enterprise shared Studio - production", "9f1c2d4e-0000-4000-8000-00000000a003", "SHARED_STUDIO", "IT-9100",
            SolutionClasses.BusinessApplication, LifecycleStages.Production, "CRITICAL", "SHARED", WorkloadStates.Production, 100000, true, true, "bp-es-it", 50000, true),
    };

    private sealed record CheckSpec(string Code, string Description, string Method, string? Key, bool Deny, string? Caveat);
    private sealed record ServiceSpec(string Code, string Name, string? Harness, string Route, string Availability, string Status, bool Customer,
        string Audiences, string DataClasses, string Tiers, string Description, object Settings, string? PermittedExceptions, IReadOnlyList<CheckSpec> Checks);

    private static IReadOnlyList<CheckSpec> PpacChecks(bool tenantPoolAllowed) => new CheckSpec[]
    {
        new("ALLOCATION", "Copilot Credits allocated to the environment match the approved posture.", "READBACK", "allocationCredits", false, null),
        new("TENANT_POOL", tenantPoolAllowed ? "Draw from tenant capacity matches the approved posture (allowed for this class)." : "Draw from the available capacity in my tenant is off, so the allocation is a cap.", "READBACK", "tenantPool", false, "C15"),
        new("PAYG_LINK", "The PAYG billing plan link matches the approved posture.", "READBACK", "paygBillingPlan", false, "C08"),
        new("AGENT_LIMIT", "Each agent has the monthly limit of its environment class (no tenant-wide default exists).", "READBACK", "agentLimitCredits", false, "C11"),
        new("FLOW_CHECK", "Agent flows the agents call are listed and checked separately: an agent limit does not stop them.", "ATTESTATION", null, false, "C11"),
        new("DENY_OVER_LIMIT", "Explicit deny case: an agent over its limit is stopped (allow up to 15 minutes for credit availability to refresh).", "TEST_EVIDENCE", null, true, "C08"),
        new("EXHAUSTION_TEST", "Exhaustion and overage behavior tested and recorded, including enforcement lag.", "TEST_EVIDENCE", null, false, "C08"),
    };

    private static IReadOnlyList<CheckSpec> M365Checks(string policyKind) => new CheckSpec[]
    {
        new("SCOPE_GROUPS", $"The {policyKind} is scoped to the approved policy groups only.", "ATTESTATION", "scopeGroups", false, "C14"),
        new("AUTO_APPLY", "Auto-apply new services is set as approved (on by default; new services can join the policy).", "ATTESTATION", "autoApplyNewServices", false, "C16"),
        new("LIMITS", "Policy limit and per-user limit match the approved values.", "ATTESTATION", "limits", false, null),
        new("ALERTS", "Alert recipients are the approved FinOps and operations mailboxes.", "ATTESTATION", "alertRecipients", false, "C08"),
        new("BILLING", "Billing method and model profile match the approved values.", "ATTESTATION", "billingMethod", false, null),
        new("DENY_OUT_OF_SCOPE", "Explicit deny case: a user outside the scope groups gets no allowance from this policy.", "TEST_EVIDENCE", null, true, "C10"),
    };

    private static readonly ServiceSpec[] Catalogue =
    {
        new("CS_STANDARD", "Copilot Studio (standard harness)", "Standard", "PPAC_ENVIRONMENT", "GA", "APPROVED", false,
            "EMPLOYEES_TEAM,EMPLOYEES_LOB,EMPLOYEES_ALL", "PUBLIC,INTERNAL,CONFIDENTIAL", "Developer,Test,Production",
            "Agents built in Copilot Studio with the standard harness, in governed Power Platform environments.",
            new { route = "PPAC environment", allocationCredits = "per environment class", tenantPool = false, paygBillingPlan = "none", agentLimitCredits = "class default",
                  notificationThresholdPct = 80, stopBehavior = "stop at limit", environmentGroupRule = "TenantPool not locked" },
            "Higher agent limit through J4 with FinOps review.", PpacChecks(false)),
        new("CS_GITHUB", "Copilot Studio (GitHub Copilot harness)", "GitHub Copilot", "PPAC_ENVIRONMENT", "GA", "PILOT", false,
            "EMPLOYEES_TEAM", "PUBLIC,INTERNAL", "Developer",
            "Agents on the GitHub Copilot harness. Build and test usage is billable (C12).",
            new { route = "PPAC environment", allocationCredits = 2000, tenantPool = false, paygBillingPlan = "none", agentLimitCredits = 1000,
                  notificationThresholdPct = 70, stopBehavior = "stop at limit", finOpsReview = "forecast before each release" },
            "None during the pilot.", PpacChecks(false)),
        new("CS_CUSTOMER", "Copilot Studio (Customer Conversation harness)", "Customer Conversation", "PPAC_ENVIRONMENT", "PREVIEW", "PILOT", true,
            "CUSTOMERS", "PUBLIC", "Production",
            "Customer-facing agents. Preview: needs an adoption decision with audience, test data, expiry and exit criteria (C02, C8).",
            new { route = "PPAC environment", allocationCredits = "per adoption decision", tenantPool = false, paygBillingPlan = "dedicated", agentLimitCredits = "per adoption decision",
                  notificationThresholdPct = 60, stopBehavior = "stop at limit", customerForecast = "required (C13)" },
            "Only through a preview adoption decision.", PpacChecks(false)),
        new("COPILOT_CHAT", "Microsoft 365 Copilot Chat", null, "M365_POLICY", "GA", "APPROVED", false,
            "EMPLOYEES_LOB,EMPLOYEES_ALL", "PUBLIC,INTERNAL,CONFIDENTIAL", "Standard,Extended",
            "Copilot Chat with pay-as-you-go agents, governed by credit policies scoped to LOB tier groups (at most 10 per tenant, C14).",
            new { route = "Microsoft 365 admin center, Billing and usage", policyKind = "Copilot Chat credit policy", scopeGroups = "one group per LOB tier",
                  autoApplyNewServices = "n/a", limits = new { policy = "envelope / 12", perUser = 50 }, alertRecipients = "finops@example.invalid", billingMethod = "PAYG overage off", modelProfile = "standard" },
            "Extended tier per LOB on approval.", M365Checks("credit policy")),
        new("COWORK", "Microsoft 365 Copilot Cowork", null, "M365_POLICY", "GA", "APPROVED", false,
            "EMPLOYEES_LOB", "PUBLIC,INTERNAL", "Standard",
            "Cowork through spending policies; a person can be in several policies and only one applies (C10).",
            new { route = "Microsoft 365 admin center, Cost Management", policyKind = "spending policy", selectedServices = new[] { "Cowork" }, scopeGroups = "one group per LOB tier",
                  autoApplyNewServices = false, limits = new { policy = "envelope / 12", perUser = 30 }, alertRecipients = "finops@example.invalid", billingMethod = "PAYG", modelProfile = "standard" },
            "None.", M365Checks("spending policy")),
        new("SP_AGENTS", "SharePoint agents", null, "M365_POLICY", "GA", "APPROVED", false,
            "EMPLOYEES_TEAM,EMPLOYEES_LOB", "PUBLIC,INTERNAL", "Standard",
            "SharePoint agents billed through Copilot Chat credit policies.",
            new { route = "Microsoft 365 admin center, Billing and usage", policyKind = "Copilot Chat credit policy", scopeGroups = "site owners group",
                  autoApplyNewServices = "n/a", limits = new { policy = 2000, perUser = 20 }, alertRecipients = "platform-ops@example.invalid", billingMethod = "PAYG overage off", modelProfile = "standard" },
            "None.", M365Checks("credit policy")),
        new("SHARED_STUDIO", "Shared Studio environment (enterprise)", "Standard", "PPAC_ENVIRONMENT", "GA", "APPROVED", false,
            "EMPLOYEES_ALL", "PUBLIC,INTERNAL,CONFIDENTIAL", "Production",
            "The enterprise shared production environment for critical shared agents; continuity funding (F9).",
            new { route = "PPAC environment (shared)", allocationCredits = 100000, tenantPool = true, paygBillingPlan = "bp-es-it", agentLimitCredits = 50000,
                  notificationThresholdPct = 70, stopBehavior = "notify, continuity owner decides", environmentGroupRule = "TenantPool locked by group rule" },
            "Continuity exceptions by the shared-service owner.", PpacChecks(true)),
    };

    private sealed record CommercialSpec(string ItemType, string Name, decimal? Quantity, string? Unit, DateTime StartOn, DateTime? EndOn, DateTime? RenewalOn,
        bool AutoRenew, string BillingScope, string ContractRef);

    private sealed record PatternSpec(string Code, string Name, string Service, string Audiences, string MaxData, string Actions, string Stages, string Triggers,
        string Tiers, decimal? Cap, string Description);

    private static readonly PatternSpec[] Patterns =
    {
        new("STUDIO-MAKER", "Studio maker productivity agent", "CS_STANDARD", "EMPLOYEES_TEAM,EMPLOYEES_LOB", DataClasses.Internal, "READ_ONLY,INTERNAL_ACTIONS",
            "LOCAL,DEVELOPMENT,TEST", "USER_INITIATED", "*", 2000, "Employee-facing productivity agents up to internal data, user-initiated, before production."),
        new("CHAT-STANDARD", "Copilot Chat standard tier", "COPILOT_CHAT", "EMPLOYEES_LOB,EMPLOYEES_ALL", DataClasses.Internal, "READ_ONLY,INTERNAL_ACTIONS",
            "PRODUCTION", "USER_INITIATED", "Standard", 500, "Routine Copilot Chat access for an LOB tier."),
        new("COWORK-STANDARD", "Cowork standard tier", "COWORK", "EMPLOYEES_LOB", DataClasses.Internal, "READ_ONLY,INTERNAL_ACTIONS",
            "PRODUCTION", "USER_INITIATED", "Standard", 300, "Routine Cowork access for an LOB tier."),
    };

    /// <summary>Canadian federal statutory holidays, with observed days when they fall on a weekend.</summary>
    public static IEnumerable<(DateTime Date, string Name)> Holidays(int year)
    {
        static DateTime Observed(DateTime d) => d.DayOfWeek switch { DayOfWeek.Saturday => d.AddDays(2), DayOfWeek.Sunday => d.AddDays(1), _ => d };
        static DateTime Nth(int y, int m, DayOfWeek dow, int n) { var d = new DateTime(y, m, 1); while (d.DayOfWeek != dow) d = d.AddDays(1); return d.AddDays(7 * (n - 1)); }
        static DateTime VictoriaDay(int y) { var d = new DateTime(y, 5, 24); while (d.DayOfWeek != DayOfWeek.Monday) d = d.AddDays(-1); return d; }
        static DateTime Easter(int y)
        {
            int a = y % 19, b = y / 100, c = y % 100, d = b / 4, e = b % 4, f = (b + 8) / 25, g = (b - f + 1) / 3, h = (19 * a + b - d - g + 15) % 30,
                i = c / 4, k = c % 4, l = (32 + 2 * e + 2 * i - h - k) % 7, m = (a + 11 * h + 22 * l) / 451, month = (h + l - 7 * m + 114) / 31, day = (h + l - 7 * m + 114) % 31 + 1;
            return new DateTime(y, month, day);
        }
        var christmas = new DateTime(year, 12, 25);
        var boxing = new DateTime(year, 12, 26);
        var christmasObs = Observed(christmas);
        var boxingObs = boxing.DayOfWeek switch
        {
            DayOfWeek.Saturday => boxing.AddDays(2),
            DayOfWeek.Sunday => boxing.AddDays(2),
            DayOfWeek.Monday when christmas.DayOfWeek == DayOfWeek.Sunday => boxing.AddDays(1),
            _ => boxing,
        };
        if (boxingObs == christmasObs) boxingObs = boxingObs.AddDays(1);
        return new[]
        {
            (Observed(new DateTime(year, 1, 1)), "New Year's Day"),
            (Easter(year).AddDays(-2), "Good Friday"),
            (VictoriaDay(year), "Victoria Day"),
            (Observed(new DateTime(year, 7, 1)), "Canada Day"),
            (Nth(year, 9, DayOfWeek.Monday, 1), "Labour Day"),
            (Observed(new DateTime(year, 9, 30)), "National Day for Truth and Reconciliation"),
            (Nth(year, 10, DayOfWeek.Monday, 2), "Thanksgiving Day"),
            (Observed(new DateTime(year, 11, 11)), "Remembrance Day"),
            (christmasObs, "Christmas Day"),
            (boxingObs, "Boxing Day"),
        };
    }
}

/// <summary>The seven activation conditions of section 5.2.</summary>
public static class ActivationChecklist
{
    public static async Task<List<string>> EvaluateAsync(SqlConnection conn, SqlTransaction? tx, int fundingOwnerId, bool requireAccessPackages)
    {
        var gaps = new List<string>();
        var today = DateTime.UtcNow.Date;
        var fo = await conn.QuerySingleAsync<FundingOwner>("SELECT * FROM core.FundingOwner WHERE FundingOwnerId = @fundingOwnerId", new { fundingOwnerId }, tx);
        var ownerRole = fo.OwnerType == "ENTERPRISE_SHARED" ? Roles.SharedServiceOwner : Roles.LobBusinessOwner;
        var accepted = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM sec.RoleAssignment WHERE PersonId = @p AND RoleCode = @ownerRole AND ScopeType = 'FUNDING_OWNER' AND ScopeId = @fundingOwnerId AND AcceptedUtc IS NOT NULL",
            new { p = fo.BusinessOwnerPersonId, ownerRole, fundingOwnerId }, tx);
        if (fo.BusinessOwnerPersonId is null || accepted == 0) gaps.Add("1. The business owner is named and has accepted the role.");
        var approvers = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(DISTINCT DelegatePersonId) FROM sec.Delegation WHERE FundingOwnerId = @fundingOwnerId AND Status = 'ACTIVE' AND (EffectiveTo IS NULL OR EffectiveTo >= @today)",
            new { fundingOwnerId, today }, tx);
        if (approvers < 2) gaps.Add("2. At least two delegated approvers are named.");
        var unvalidated = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM core.CostCenter WHERE FundingOwnerId = @fundingOwnerId AND ValidatedByFinance = 0", new { fundingOwnerId }, tx);
        var centers = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM core.CostCenter WHERE FundingOwnerId = @fundingOwnerId", new { fundingOwnerId }, tx);
        if (centers == 0 || unvalidated > 0) gaps.Add("3. Finance has validated the cost centers.");
        var envelopes = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM core.FundingEnvelope WHERE FundingOwnerId = @fundingOwnerId AND Status = 'ACTIVE' AND ApprovedDecisionId IS NOT NULL", new { fundingOwnerId }, tx);
        if (envelopes == 0) gaps.Add("4. An envelope is approved for each service the funding owner uses.");
        var rules = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM core.AttributionRule WHERE FundingOwnerId = @fundingOwnerId AND EffectiveTo IS NULL", new { fundingOwnerId }, tx);
        if (rules == 0 || !fo.AttributionTested) gaps.Add("5. Attribution rules are defined and tested against the directory.");
        if (requireAccessPackages)
        {
            var routine = await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM core.FundingEnvelope e JOIN core.Service s ON s.ServiceId = e.ServiceId WHERE e.FundingOwnerId = @fundingOwnerId AND s.RouteType = 'M365_POLICY' AND s.Code IN ('COPILOT_CHAT','COWORK')",
                new { fundingOwnerId }, tx);
            var mapped = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM intg.AccessPackageMap WHERE FundingOwnerId = @fundingOwnerId", new { fundingOwnerId }, tx);
            if (routine > 0 && mapped == 0) gaps.Add("6. Access packages and policy cohorts are mapped for routine services.");
        }
        if (!fo.NotificationRecipientsConfirmed) gaps.Add("7. Notification recipients are confirmed.");
        return gaps;
    }
}
