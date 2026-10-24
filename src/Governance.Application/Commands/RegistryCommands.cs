using System.Text.Json;
using Governance.Application.Data;
using Governance.Application.Queries;
using Governance.Application.Rules;
using Governance.Domain;

namespace Governance.Application.Commands;

/// <summary>Decisions taken outside a request (envelopes, templates, RACI, recertification): same ledger, same fields.</summary>
public sealed class DecisionRecorder(IClock clock, ICurrentPrincipal principal)
{
    public async Task<long> RecordAsync(Uow uow, string decisionType, string outcome, string roleCode, string activityCode, string scopeText,
        int? fundingOwnerId = null, int? workloadId = null, string? conditions = null, string? reason = null, DateTime? expiresOn = null, DateTime? nextReviewOn = null,
        int? delegationId = null, string source = DecisionSources.Workbench, string? sourceRef = null)
    {
        var version = await uow.ScalarAsync<int?>("SELECT RaciVersionId FROM sec.RaciVersion WHERE Status = 'ACTIVE'")
                      ?? await uow.ScalarAsync<int?>("SELECT MAX(RaciVersionId) FROM sec.RaciVersion")
                      ?? throw new DomainException("No RACI version exists.");
        return await uow.ScalarAsync<long>(
            """
            INSERT wf.Decision (PublicId, WorkloadId, FundingOwnerId, DecisionType, Outcome, DeciderPersonId, DeciderRoleCode, DelegationId, RaciVersionId, RaciActivityCode,
                                ScopeText, ConditionsText, ReasonText, ExpiresOn, NextReviewOn, Source, SourceRef, DecidedAtUtc)
            OUTPUT inserted.DecisionId
            VALUES (NEWID(), @workloadId, @fundingOwnerId, @decisionType, @outcome, @person, @roleCode, @delegationId, @version, @activityCode,
                    @scope, @conditions, @reason, @expiresOn, @nextReviewOn, @source, @sourceRef, @now)
            """,
            new
            {
                workloadId, fundingOwnerId, decisionType, outcome, person = principal.PersonId, roleCode, delegationId, version, activityCode,
                scope = CommandRunner.Trim(scopeText, 1000), conditions = CommandRunner.TrimN(conditions, 2000), reason = CommandRunner.TrimN(reason, 2000),
                expiresOn = expiresOn?.Date, nextReviewOn = nextReviewOn?.Date, source, sourceRef, now = clock.UtcNow,
            });
    }
}

public sealed record FundingOwnerInput(string Code, string Name, string OwnerType, int? ParentFundingOwnerId, int? BusinessOwnerPersonId, int? FinancePartnerPersonId,
    string? FinanceNode, bool IsCriticalShared, DateTime EffectiveFrom);

public sealed record ServiceInput(string Code, string Name, string? Harness, string RouteType, string TenantAvailability, string InstitutionalStatus, bool IsCustomerFacing,
    string Audiences, string DataClasses, string AccessTiers, int? OperationalOwnerPersonId, DateTime? ReviewOn, string? Description);

public sealed record CommercialInput(string ItemType, string Name, decimal? Quantity, string? Unit, DateTime? StartOn, DateTime? EndOn, DateTime? RenewalOn, bool AutoRenew,
    string? BillingScope, string? ContractRef, int? PurchaseAuthorityPersonId, bool ReconciledToContract, string? Notes);

public sealed record CheckDefinitionInput(string CheckCode, string Description, string Method, string? SettingKey, bool IsDenyCase, string? CaveatCode);

public sealed class RegistryCommands(CommandRunner run, DecisionRecorder decisions, IClock clock)
{
    private ICurrentPrincipal Me => run.Principal;
    private DateTime Today => clock.UtcNow.Date;

    // ---------------------------------------------------------------- funding owners

    public Task<string?> SaveFundingOwnerAsync(Guid idem, Guid? publicId, FundingOwnerInput i, byte[]? rowVer)
        => run.RunAsync(idem, "fundingowner.save", async uow =>
        {
            run.RequireAny(Permissions.RegistryFinance, Permissions.RegistryEdit);
            if (string.IsNullOrWhiteSpace(i.Code) || string.IsNullOrWhiteSpace(i.Name)) throw new DomainException("Code and name are required.");
            if (i.OwnerType is not ("LOB" or "ENTERPRISE_SHARED")) throw new DomainException("Choose LOB or Enterprise Shared.");
            var code = CommandRunner.Trim(i.Code, 32).ToUpperInvariant();
            int id;
            if (publicId is null)
            {
                id = await uow.ScalarAsync<int>(
                    """
                    INSERT core.FundingOwner (Code, Name, OwnerType, ParentFundingOwnerId, BusinessOwnerPersonId, FinancePartnerPersonId, FinanceNode, Status, IsCriticalShared, EffectiveFrom)
                    OUTPUT inserted.FundingOwnerId
                    VALUES (@code, @Name, @OwnerType, @ParentFundingOwnerId, @BusinessOwnerPersonId, @FinancePartnerPersonId, @FinanceNode, 'DRAFT', @IsCriticalShared, @EffectiveFrom)
                    """, new { code, Name = CommandRunner.Trim(i.Name, 200), i.OwnerType, i.ParentFundingOwnerId, i.BusinessOwnerPersonId, i.FinancePartnerPersonId, FinanceNode = CommandRunner.TrimN(i.FinanceNode, 100), i.IsCriticalShared, EffectiveFrom = i.EffectiveFrom.Date });
            }
            else
            {
                id = await uow.ScalarAsync<int?>("SELECT FundingOwnerId FROM core.FundingOwner WHERE PublicId = @publicId", new { publicId }) ?? throw new DomainException("Funding owner not found.");
                var n = await uow.ExecuteAsync(
                    """
                    UPDATE core.FundingOwner SET Code = @code, Name = @Name, OwnerType = @OwnerType, ParentFundingOwnerId = @ParentFundingOwnerId, BusinessOwnerPersonId = @BusinessOwnerPersonId,
                           FinancePartnerPersonId = @FinancePartnerPersonId, FinanceNode = @FinanceNode, IsCriticalShared = @IsCriticalShared
                    WHERE FundingOwnerId = @id AND (@rowVer IS NULL OR RowVer = @rowVer)
                    """, new { code, Name = CommandRunner.Trim(i.Name, 200), i.OwnerType, i.ParentFundingOwnerId, i.BusinessOwnerPersonId, i.FinancePartnerPersonId, FinanceNode = CommandRunner.TrimN(i.FinanceNode, 100), i.IsCriticalShared, id, rowVer });
                if (n == 0) throw new ConcurrencyException();
            }
            if (i.ParentFundingOwnerId == id) throw new DomainException("A funding owner cannot be its own parent.");
            // The business owner holds the owner role in scope and must accept it (activation checklist item 1).
            if (i.BusinessOwnerPersonId is int bo)
            {
                var role = i.OwnerType == "ENTERPRISE_SHARED" ? Roles.SharedServiceOwner : Roles.LobBusinessOwner;
                await uow.ExecuteAsync("UPDATE sec.RoleAssignment SET EffectiveTo = @yesterday WHERE ScopeType = 'FUNDING_OWNER' AND ScopeId = @id AND RoleCode IN ('LOB_BUSINESS_OWNER','SHARED_SERVICE_OWNER') AND PersonId <> @bo AND EffectiveTo IS NULL",
                    new { yesterday = Today.AddDays(-1), id, bo });
                await uow.ExecuteAsync(
                    """
                    IF NOT EXISTS (SELECT 1 FROM sec.RoleAssignment WHERE PersonId = @bo AND RoleCode = @role AND ScopeType = 'FUNDING_OWNER' AND ScopeId = @id AND EffectiveTo IS NULL)
                        INSERT sec.RoleAssignment (PersonId, RoleCode, ScopeType, ScopeId, EffectiveFrom, Source, ReviewDueOn, GrantedByPersonId)
                        VALUES (@bo, @role, 'FUNDING_OWNER', @id, @today, 'WORKBENCH', @review, @me)
                    """, new { bo, role, id, today = Today, review = Today.AddYears(1), me = Me.PersonId });
            }
            if (i.FinancePartnerPersonId is int fp)
                await uow.ExecuteAsync(
                    """
                    IF NOT EXISTS (SELECT 1 FROM sec.RoleAssignment WHERE PersonId = @fp AND RoleCode = 'LOB_FINANCE_PARTNER' AND ScopeType = 'FUNDING_OWNER' AND ScopeId = @id AND EffectiveTo IS NULL)
                        INSERT sec.RoleAssignment (PersonId, RoleCode, ScopeType, ScopeId, EffectiveFrom, Source, ReviewDueOn, GrantedByPersonId)
                        VALUES (@fp, 'LOB_FINANCE_PARTNER', 'FUNDING_OWNER', @id, @today, 'WORKBENCH', @review, @me)
                    """, new { fp, id, today = Today, review = Today.AddYears(1), me = Me.PersonId });
            await run.AuditAsync(uow, "fundingowner.save", "FundingOwner", code, i.Name);
            return await uow.ScalarAsync<string>("SELECT CAST(PublicId AS varchar(40)) FROM core.FundingOwner WHERE FundingOwnerId = @id", new { id });
        });

    public Task SetFundingOwnerFlagAsync(Guid idem, Guid publicId, string flag, bool value)
        => run.RunAsync(idem, "fundingowner.flag", async uow =>
        {
            var id = await FundingOwnerIdAsync(uow, publicId);
            var column = flag switch
            {
                "recipients" => "NotificationRecipientsConfirmed",
                "attribution" => "AttributionTested",
                _ => throw new DomainException("Unknown setting."),
            };
            if (flag == "recipients") { if (!Me.Can(Permissions.EnvelopeApprove, id) && !Me.Can(Permissions.RegistryEdit)) throw new DomainException("The business owner or the Platform Owner confirms recipients."); }
            else run.RequireAny(Permissions.RegistryFinance, Permissions.RegistryEdit);
            await uow.ExecuteAsync($"UPDATE core.FundingOwner SET {column} = @value WHERE FundingOwnerId = @id", new { value, id });
            await run.AuditAsync(uow, "fundingowner." + flag, "FundingOwner", publicId, value.ToString());
        });

    /// <summary>Draft to Active only when the seven checklist items of section 5.2 hold.</summary>
    public Task ActivateFundingOwnerAsync(Guid idem, Guid publicId, IReadOnlyList<ChecklistItem> checklist)
        => run.RunAsync(idem, "fundingowner.activate", async uow =>
        {
            run.RequireAny(Permissions.RegistryFinance, Permissions.RegistryEdit);
            var open = checklist.Where(c => !c.Done).ToList();
            if (open.Count > 0) throw new DomainException("Activation needs every checklist item: " + string.Join(" ", open.Select(c => $"{c.No}. {c.Text}")));
            var id = await FundingOwnerIdAsync(uow, publicId);
            await uow.ExecuteAsync("UPDATE core.FundingOwner SET Status = 'ACTIVE' WHERE FundingOwnerId = @id", new { id });
            await run.AuditAsync(uow, "fundingowner.activate", "FundingOwner", publicId, null);
        });

    public Task SetFundingOwnerStatusAsync(Guid idem, Guid publicId, string status, string reason)
        => run.RunAsync(idem, "fundingowner.status", async uow =>
        {
            run.RequireAny(Permissions.RegistryFinance, Permissions.RegistryEdit);
            if (status is not ("SUSPENDED" or "RETIRED" or "DRAFT")) throw new DomainException("Choose suspend, retire or back to draft.");
            if (string.IsNullOrWhiteSpace(reason)) throw new DomainException("Give the reason.");
            var id = await FundingOwnerIdAsync(uow, publicId);
            await uow.ExecuteAsync("UPDATE core.FundingOwner SET Status = @status, EffectiveTo = CASE WHEN @status = 'RETIRED' THEN @today ELSE EffectiveTo END WHERE FundingOwnerId = @id", new { status, today = Today, id });
            await run.AuditAsync(uow, "fundingowner.status", "FundingOwner", publicId, $"{status}: {reason}");
        });

    public Task AcceptRoleAsync(Guid idem, Guid roleAssignmentPublicId)
        => run.RunAsync(idem, "role.accept", async uow =>
        {
            var n = await uow.ExecuteAsync("UPDATE sec.RoleAssignment SET AcceptedUtc = @now WHERE PublicId = @roleAssignmentPublicId AND PersonId = @me AND AcceptedUtc IS NULL",
                new { now = clock.UtcNow, roleAssignmentPublicId, me = Me.PersonId });
            if (n == 0) throw new DomainException("There is no role waiting for your acceptance.");
            await run.AuditAsync(uow, "role.accept", "RoleAssignment", roleAssignmentPublicId, null);
        });

    public Task AddAttributionRuleAsync(Guid idem, Guid fundingOwnerPublicId, string? division, string? costCenter, string? department, int priority)
        => run.RunAsync(idem, "attribution.add", async uow =>
        {
            run.RequireAny(Permissions.RegistryFinance, Permissions.RegistryEdit);
            if (string.IsNullOrWhiteSpace(division) && string.IsNullOrWhiteSpace(costCenter) && string.IsNullOrWhiteSpace(department))
                throw new DomainException("Give at least one Entra value to match: division, cost center or department.");
            var id = await FundingOwnerIdAsync(uow, fundingOwnerPublicId);
            await uow.ExecuteAsync("INSERT core.AttributionRule (FundingOwnerId, MatchDivision, MatchCostCenter, MatchDepartment, Priority, EffectiveFrom) VALUES (@id, @d, @c, @p, @priority, @today)",
                new { id, d = CommandRunner.TrimN(division, 200), c = CommandRunner.TrimN(costCenter, 100), p = CommandRunner.TrimN(department, 200), priority, today = Today });
            await uow.ExecuteAsync("UPDATE core.FundingOwner SET AttributionTested = 0 WHERE FundingOwnerId = @id", new { id });
            await run.AuditAsync(uow, "attribution.add", "FundingOwner", fundingOwnerPublicId, $"{division}|{costCenter}|{department}");
        });

    public Task EndAttributionRuleAsync(Guid idem, int ruleId)
        => run.RunAsync(idem, "attribution.end", async uow =>
        {
            run.RequireAny(Permissions.RegistryFinance, Permissions.RegistryEdit);
            await uow.ExecuteAsync("UPDATE core.AttributionRule SET EffectiveTo = @today WHERE AttributionRuleId = @ruleId AND EffectiveTo IS NULL", new { today = Today, ruleId });
            await run.AuditAsync(uow, "attribution.end", "AttributionRule", ruleId, null);
        });

    // ---------------------------------------------------------------- roles and delegations

    public Task AssignRoleAsync(Guid idem, int personId, string roleCode, int? fundingOwnerId, DateTime? effectiveTo)
        => run.RunAsync(idem, "role.assign", async uow =>
        {
            var scope = await uow.ScalarAsync<string>("SELECT ScopeType FROM sec.RoleDefinition WHERE RoleCode = @roleCode", new { roleCode }) ?? throw new DomainException("Unknown role.");
            if (scope == ScopeTypes.FundingOwner)
            {
                if (fundingOwnerId is null) throw new DomainException("Choose the funding owner this role is for.");
                if (!Me.Can(Permissions.RoleAssignLob, fundingOwnerId)) throw new DomainException("The business owner or the Platform Owner assigns LOB roles.");
            }
            else
            {
                run.Require(Permissions.RoleAssignCentral, null, "The Platform Owner assigns central roles.");
                fundingOwnerId = null;
            }
            if (roleCode is Roles.LobBusinessOwner or Roles.SharedServiceOwner) throw new DomainException("Name the business owner on the funding owner's page.");
            var exists = await uow.ScalarAsync<int>("SELECT COUNT(*) FROM sec.RoleAssignment WHERE PersonId = @personId AND RoleCode = @roleCode AND ScopeType = @scope AND (ScopeId = @fundingOwnerId OR (@fundingOwnerId IS NULL AND ScopeId IS NULL)) AND EffectiveTo IS NULL",
                new { personId, roleCode, scope, fundingOwnerId });
            if (exists > 0) throw new DomainException("This person already holds the role in this scope.");
            await uow.ExecuteAsync(
                "INSERT sec.RoleAssignment (PersonId, RoleCode, ScopeType, ScopeId, EffectiveFrom, EffectiveTo, Source, ReviewDueOn, GrantedByPersonId) VALUES (@personId, @roleCode, @scope, @fundingOwnerId, @today, @effectiveTo, 'WORKBENCH', @review, @me)",
                new { personId, roleCode, scope, fundingOwnerId, today = Today, effectiveTo = effectiveTo?.Date, review = Today.AddYears(1), me = Me.PersonId });
            await run.AuditAsync(uow, "role.assign", "Person", personId, $"{roleCode} {scope} {fundingOwnerId}");
        });

    public Task EndRoleAsync(Guid idem, Guid roleAssignmentPublicId)
        => run.RunAsync(idem, "role.end", async uow =>
        {
            var ra = await uow.QuerySingleOrDefaultAsync<(int Id, string ScopeType, int? ScopeId, string RoleCode)>(
                "SELECT RoleAssignmentId, ScopeType, ScopeId, RoleCode FROM sec.RoleAssignment WHERE PublicId = @roleAssignmentPublicId", new { roleAssignmentPublicId });
            if (ra.Id == 0) throw new DomainException("Role assignment not found.");
            if (ra.ScopeType == ScopeTypes.FundingOwner) { if (!Me.Can(Permissions.RoleAssignLob, ra.ScopeId)) throw new DomainException("You cannot end this role."); }
            else run.Require(Permissions.RoleAssignCentral);
            await uow.ExecuteAsync("UPDATE sec.RoleAssignment SET EffectiveTo = @yesterday WHERE RoleAssignmentId = @Id AND (EffectiveTo IS NULL OR EffectiveTo >= @today)", new { yesterday = Today.AddDays(-1), today = Today, ra.Id });
            await run.AuditAsync(uow, "role.end", "RoleAssignment", roleAssignmentPublicId, ra.RoleCode);
        });

    public Task AddDelegationAsync(Guid idem, Guid fundingOwnerPublicId, int delegatePersonId, decimal? maxCredits, DateTime from, DateTime? to, IReadOnlyList<int> envelopeIds, string? notes)
        => run.RunAsync(idem, "delegation.add", async uow =>
        {
            var id = await FundingOwnerIdAsync(uow, fundingOwnerPublicId);
            if (!Me.Can(Permissions.DelegationManage, id) || !(Me.HasRoleIn(Roles.LobBusinessOwner, id) || Me.HasRoleIn(Roles.SharedServiceOwner, id)))
                throw new DomainException("Only the funding owner's business owner delegates routine approval.");
            if (delegatePersonId == Me.PersonId) throw new DomainException("Delegate to someone else.");
            if (envelopeIds.Count == 0) throw new DomainException("Choose the envelopes the delegation covers (SoD7).");
            var valid = await uow.ScalarAsync<int>("SELECT COUNT(*) FROM core.FundingEnvelope WHERE EnvelopeId IN @envelopeIds AND FundingOwnerId = @id", new { envelopeIds, id });
            if (valid != envelopeIds.Count) throw new DomainException("Every envelope must belong to this funding owner.");
            var did = await uow.ScalarAsync<int>(
                "INSERT sec.Delegation (FundingOwnerId, DelegatePersonId, GrantedByPersonId, MaxCreditsPerRequest, EffectiveFrom, EffectiveTo, Status, Notes) OUTPUT inserted.DelegationId VALUES (@id, @delegatePersonId, @me, @maxCredits, @from, @to, 'ACTIVE', @notes)",
                new { id, delegatePersonId, me = Me.PersonId, maxCredits, from = from.Date, to = to?.Date, notes = CommandRunner.TrimN(notes, 1000) });
            foreach (var e in envelopeIds.Distinct())
                await uow.ExecuteAsync("INSERT sec.DelegationEnvelope (DelegationId, EnvelopeId) VALUES (@did, @e)", new { did, e });
            await run.AuditAsync(uow, "delegation.add", "Delegation", did, $"to {delegatePersonId}, max {maxCredits}");
        });

    public Task RevokeDelegationAsync(Guid idem, Guid delegationPublicId)
        => run.RunAsync(idem, "delegation.revoke", async uow =>
        {
            var d = await uow.QuerySingleOrDefaultAsync<(int Id, int Fo)>("SELECT DelegationId, FundingOwnerId FROM sec.Delegation WHERE PublicId = @delegationPublicId", new { delegationPublicId });
            if (d.Id == 0) throw new DomainException("Delegation not found.");
            if (!Me.Can(Permissions.DelegationManage, d.Fo) && !Me.Can(Permissions.RegistryEdit)) throw new DomainException("You cannot revoke this delegation.");
            await uow.ExecuteAsync("UPDATE sec.Delegation SET Status = 'REVOKED', EffectiveTo = @today WHERE DelegationId = @Id", new { today = Today, d.Id });
            await run.AuditAsync(uow, "delegation.revoke", "Delegation", d.Id, null);
        });

    // ---------------------------------------------------------------- cost centers

    public Task SaveCostCenterAsync(Guid idem, Guid? publicId, string code, string name, string? financeNode, int fundingOwnerId, DateTime effectiveFrom)
        => run.RunAsync(idem, "costcenter.save", async uow =>
        {
            run.RequireAny(Permissions.RegistryFinance, Permissions.RegistryEdit);
            if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(name)) throw new DomainException("Code and name are required.");
            if (publicId is null)
            {
                if (await uow.ScalarAsync<int>("SELECT COUNT(*) FROM core.CostCenter WHERE Code = @code", new { code = code.Trim() }) > 0) throw new DomainException("That cost center code exists.");
                await uow.ExecuteAsync("INSERT core.CostCenter (Code, Name, FinanceNode, FundingOwnerId, EffectiveFrom) VALUES (@code, @name, @financeNode, @fundingOwnerId, @from)",
                    new { code = CommandRunner.Trim(code, 100), name = CommandRunner.Trim(name, 200), financeNode = CommandRunner.TrimN(financeNode, 100), fundingOwnerId, from = effectiveFrom.Date });
            }
            else
            {
                // Moving a cost center to another funding owner is versioned (business dates and system time): history is not rewritten (H3).
                await uow.ExecuteAsync("UPDATE core.CostCenter SET Name = @name, FinanceNode = @financeNode, FundingOwnerId = @fundingOwnerId, ValidatedByFinance = CASE WHEN FundingOwnerId = @fundingOwnerId THEN ValidatedByFinance ELSE 0 END WHERE PublicId = @publicId",
                    new { name = CommandRunner.Trim(name, 200), financeNode = CommandRunner.TrimN(financeNode, 100), fundingOwnerId, publicId });
            }
            await run.AuditAsync(uow, "costcenter.save", "CostCenter", code, name);
        });

    public Task ValidateCostCenterAsync(Guid idem, Guid publicId, bool valid)
        => run.RunAsync(idem, "costcenter.validate", async uow =>
        {
            run.Require(Permissions.RegistryFinance, null, "Finance validates cost centers.");
            await uow.ExecuteAsync("UPDATE core.CostCenter SET ValidatedByFinance = @valid, ValidatedByPersonId = @me WHERE PublicId = @publicId", new { valid, me = Me.PersonId, publicId });
            await run.AuditAsync(uow, "costcenter.validate", "CostCenter", publicId, valid.ToString());
        });

    /// <summary>Finance hierarchy import (OD2): CSV with Code, Name, FinanceNode, FundingOwnerCode. Upserts; returns a summary.</summary>
    public Task<string?> ImportCostCentersAsync(Guid idem, string csv)
        => run.RunAsync(idem, "costcenter.import", async uow =>
        {
            run.Require(Permissions.RegistryFinance, null, "Finance imports cost centers.");
            var lines = csv.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries);
            int added = 0, updated = 0, skipped = 0;
            var problems = new List<string>();
            foreach (var (line, idx) in lines.Select((l, i) => (l, i)))
            {
                var f = CsvSplit(line);
                if (idx == 0 && f.Count > 0 && f[0].Equals("Code", StringComparison.OrdinalIgnoreCase)) continue;
                if (f.Count < 4) { skipped++; problems.Add($"Line {idx + 1}: needs 4 columns."); continue; }
                var fo = await uow.ScalarAsync<int?>("SELECT FundingOwnerId FROM core.FundingOwner WHERE Code = @c", new { c = f[3].Trim() });
                if (fo is null) { skipped++; problems.Add($"Line {idx + 1}: unknown funding owner {f[3]}."); continue; }
                var exists = await uow.ScalarAsync<int>("SELECT COUNT(*) FROM core.CostCenter WHERE Code = @c", new { c = f[0].Trim() });
                if (exists > 0)
                {
                    await uow.ExecuteAsync("UPDATE core.CostCenter SET Name = @n, FinanceNode = @node, FundingOwnerId = @fo, ValidatedByFinance = 1, ValidatedByPersonId = @me WHERE Code = @c",
                        new { n = CommandRunner.Trim(f[1], 200), node = CommandRunner.TrimN(f[2], 100), fo, me = Me.PersonId, c = f[0].Trim() });
                    updated++;
                }
                else
                {
                    await uow.ExecuteAsync("INSERT core.CostCenter (Code, Name, FinanceNode, FundingOwnerId, ValidatedByFinance, ValidatedByPersonId, EffectiveFrom) VALUES (@c, @n, @node, @fo, 1, @me, @today)",
                        new { c = CommandRunner.Trim(f[0], 100), n = CommandRunner.Trim(f[1], 200), node = CommandRunner.TrimN(f[2], 100), fo, me = Me.PersonId, today = Today });
                    added++;
                }
            }
            var summary = $"{added} added, {updated} updated, {skipped} skipped." + (problems.Count > 0 ? " " + string.Join(" ", problems.Take(10)) : "");
            await run.AuditAsync(uow, "costcenter.import", "CostCenter", null, summary);
            return summary;
        });

    public static List<string> CsvSplit(string line)
    {
        var result = new List<string>();
        var sb = new System.Text.StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (quoted)
            {
                if (ch == '"' && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                else if (ch == '"') quoted = false;
                else sb.Append(ch);
            }
            else if (ch == '"') quoted = true;
            else if (ch == ',') { result.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(ch);
        }
        result.Add(sb.ToString());
        return result;
    }

    // ---------------------------------------------------------------- services and templates

    public Task<string?> SaveServiceAsync(Guid idem, Guid? publicId, ServiceInput i)
        => run.RunAsync(idem, "service.save", async uow =>
        {
            run.Require(Permissions.RegistryEdit, null, "The Platform Owner publishes the service catalogue.");
            if (string.IsNullOrWhiteSpace(i.Code) || string.IsNullOrWhiteSpace(i.Name)) throw new DomainException("Code and name are required.");
            var args = new
            {
                Code = CommandRunner.Trim(i.Code, 40).ToUpperInvariant(), Name = CommandRunner.Trim(i.Name, 200), Harness = CommandRunner.TrimN(i.Harness, 100), i.RouteType, i.TenantAvailability,
                i.InstitutionalStatus, i.IsCustomerFacing, Audiences = CommandRunner.Trim(i.Audiences, 400), DataClasses = CommandRunner.Trim(i.DataClasses, 400),
                AccessTiers = CommandRunner.Trim(i.AccessTiers, 200), i.OperationalOwnerPersonId, ReviewOn = i.ReviewOn?.Date, Description = CommandRunner.TrimN(i.Description, 2000), publicId,
            };
            if (publicId is null)
                await uow.ExecuteAsync(
                    """
                    INSERT core.Service (Code, Name, Harness, RouteType, TenantAvailability, InstitutionalStatus, IsCustomerFacing, Audiences, DataClasses, AccessTiers, OperationalOwnerPersonId, ReviewOn, Description)
                    VALUES (@Code, @Name, @Harness, @RouteType, @TenantAvailability, @InstitutionalStatus, @IsCustomerFacing, @Audiences, @DataClasses, @AccessTiers, @OperationalOwnerPersonId, @ReviewOn, @Description)
                    """, args);
            else
                await uow.ExecuteAsync(
                    """
                    UPDATE core.Service SET Code = @Code, Name = @Name, Harness = @Harness, RouteType = @RouteType, TenantAvailability = @TenantAvailability, InstitutionalStatus = @InstitutionalStatus,
                           IsCustomerFacing = @IsCustomerFacing, Audiences = @Audiences, DataClasses = @DataClasses, AccessTiers = @AccessTiers, OperationalOwnerPersonId = @OperationalOwnerPersonId,
                           ReviewOn = @ReviewOn, Description = @Description
                    WHERE PublicId = @publicId
                    """, args);
            await run.AuditAsync(uow, "service.save", "Service", args.Code, $"{i.InstitutionalStatus}/{i.TenantAvailability}");
            return await uow.ScalarAsync<string>("SELECT CAST(PublicId AS varchar(40)) FROM core.Service WHERE Code = @Code", new { args.Code });
        });

    /// <summary>A new draft template version, copied from the active one when there is one.</summary>
    public Task<string?> DraftTemplateAsync(Guid idem, int serviceId)
        => run.RunAsync(idem, "template.draft", async uow =>
        {
            run.RequireAny(Permissions.TemplateDraft, Permissions.RegistryEdit);
            if (await uow.ScalarAsync<int>("SELECT COUNT(*) FROM core.ControlTemplate WHERE ServiceId = @serviceId AND Status IN ('DRAFT','PENDING')", new { serviceId }) > 0)
                throw new DomainException("A draft already exists for this service; edit it.");
            var source = await uow.QueryFirstOrDefaultAsync<(int TemplateId, string SettingsJson, string? PermittedExceptions, string OperatorRoles)>(
                "SELECT TOP (1) TemplateId, SettingsJson, PermittedExceptions, OperatorRoles FROM core.ControlTemplate WHERE ServiceId = @serviceId ORDER BY CASE Status WHEN 'ACTIVE' THEN 0 ELSE 1 END, VersionNo DESC", new { serviceId });
            var next = (await uow.ScalarAsync<int?>("SELECT MAX(VersionNo) FROM core.ControlTemplate WHERE ServiceId = @serviceId", new { serviceId }) ?? 0) + 1;
            var newId = await uow.ScalarAsync<int>(
                "INSERT core.ControlTemplate (ServiceId, VersionNo, Status, SettingsJson, PermittedExceptions, OperatorRoles, CreatedByPersonId) OUTPUT inserted.TemplateId VALUES (@serviceId, @next, 'DRAFT', @json, @pe, @roles, @me)",
                new { serviceId, next, json = source.SettingsJson ?? "{}", pe = source.PermittedExceptions, roles = source.OperatorRoles ?? "PLATFORM_OPERATOR", me = Me.PersonId });
            if (source.TemplateId != 0)
                await uow.ExecuteAsync(
                    "INSERT core.ControlCheck (TemplateId, CheckCode, Description, Method, SettingKey, IsDenyCase, CaveatCode, SortOrder) SELECT @newId, CheckCode, Description, Method, SettingKey, IsDenyCase, CaveatCode, SortOrder FROM core.ControlCheck WHERE TemplateId = @src",
                    new { newId, src = source.TemplateId });
            await run.AuditAsync(uow, "template.draft", "ControlTemplate", newId, $"service {serviceId} v{next}");
            return await uow.ScalarAsync<string>("SELECT CAST(PublicId AS varchar(40)) FROM core.ControlTemplate WHERE TemplateId = @newId", new { newId });
        });

    public Task SaveTemplateAsync(Guid idem, Guid publicId, string settingsJson, string? permittedExceptions, string operatorRoles, string? notes, IReadOnlyList<CheckDefinitionInput> checks, byte[]? rowVer)
        => run.RunAsync(idem, "template.save", async uow =>
        {
            run.RequireAny(Permissions.TemplateDraft, Permissions.RegistryEdit);
            var t = await uow.QuerySingleOrDefaultAsync<(int TemplateId, string Status)>("SELECT TemplateId, Status FROM core.ControlTemplate WHERE PublicId = @publicId", new { publicId });
            if (t.TemplateId == 0) throw new DomainException("Template not found.");
            if (t.Status != "DRAFT") throw new DomainException("Only a draft can be edited; draft a new version.");
            try { using var _ = JsonDocument.Parse(settingsJson); }
            catch (JsonException) { throw new DomainException("The settings must be valid JSON (an object of setting names and expected values)."); }
            if (checks.Any(c => c.Method is not ("READBACK" or "ATTESTATION" or "TEST_EVIDENCE"))) throw new DomainException("Each check's method is read-back, attestation or test evidence.");
            if (checks.Select(c => c.CheckCode.Trim().ToUpperInvariant()).Distinct().Count() != checks.Count) throw new DomainException("Check codes must be unique.");
            var n = await uow.ExecuteAsync("UPDATE core.ControlTemplate SET SettingsJson = @settingsJson, PermittedExceptions = @pe, OperatorRoles = @operatorRoles, Notes = @notes WHERE TemplateId = @TemplateId AND (@rowVer IS NULL OR RowVer = @rowVer)",
                new { settingsJson, pe = CommandRunner.TrimN(permittedExceptions, 1000), operatorRoles = CommandRunner.Trim(operatorRoles, 400), notes = CommandRunner.TrimN(notes, 2000), t.TemplateId, rowVer });
            if (n == 0) throw new ConcurrencyException();
            await uow.ExecuteAsync("DELETE FROM core.ControlCheck WHERE TemplateId = @TemplateId", new { t.TemplateId });
            var order = 0;
            foreach (var c in checks)
                await uow.ExecuteAsync("INSERT core.ControlCheck (TemplateId, CheckCode, Description, Method, SettingKey, IsDenyCase, CaveatCode, SortOrder) VALUES (@TemplateId, @code, @d, @Method, @key, @IsDenyCase, @cav, @order)",
                    new { t.TemplateId, code = CommandRunner.Trim(c.CheckCode, 40).ToUpperInvariant(), d = CommandRunner.Trim(c.Description, 1000), c.Method, key = CommandRunner.TrimN(c.SettingKey, 100), c.IsDenyCase, cav = CommandRunner.TrimN(c.CaveatCode, 10), order = ++order * 10 });
            await run.AuditAsync(uow, "template.save", "ControlTemplate", publicId, $"{checks.Count} checks");
        });

    /// <summary>SoD6: the Platform Owner and Steering approve, as different people; the second approval activates.</summary>
    public Task ApproveTemplateAsync(Guid idem, Guid publicId, string asRole)
        => run.RunAsync(idem, "template.approve", async uow =>
        {
            var t = await uow.QuerySingleOrDefaultAsync<(int TemplateId, int ServiceId, int VersionNo, string Status, int? Po, int? St)>(
                "SELECT TemplateId, ServiceId, VersionNo, Status, PlatformOwnerApprovalPersonId, SteeringApprovalPersonId FROM core.ControlTemplate WHERE PublicId = @publicId", new { publicId });
            if (t.TemplateId == 0) throw new DomainException("Template not found.");
            if (t.Status is not ("DRAFT" or "PENDING")) throw new DomainException("This version is not waiting for approval.");
            if (await uow.ScalarAsync<int>("SELECT COUNT(*) FROM core.ControlCheck WHERE TemplateId = @TemplateId AND IsDenyCase = 1", new { t.TemplateId }) == 0)
                throw new DomainException("A template needs at least one explicit deny-case check before approval.");
            if (asRole == Roles.PlatformOwner)
            {
                if (!Me.HasRole(Roles.PlatformOwner)) throw new DomainException("You don't hold the Platform Owner role.");
                if (t.St == Me.PersonId) throw new DomainException("You approved as Steering; the Platform Owner approval must come from someone else (SoD6).");
                await uow.ExecuteAsync("UPDATE core.ControlTemplate SET PlatformOwnerApprovalPersonId = @me, Status = 'PENDING' WHERE TemplateId = @TemplateId", new { me = Me.PersonId, t.TemplateId });
            }
            else if (asRole == Roles.Steering)
            {
                if (!Me.HasRole(Roles.Steering)) throw new DomainException("You don't hold the Steering role.");
                if (t.Po == Me.PersonId) throw new DomainException("You approved as Platform Owner; Steering approval must come from someone else (SoD6).");
                await uow.ExecuteAsync("UPDATE core.ControlTemplate SET SteeringApprovalPersonId = @me, Status = 'PENDING' WHERE TemplateId = @TemplateId", new { me = Me.PersonId, t.TemplateId });
            }
            else throw new DomainException("Approve as Platform Owner or Steering.");
            await decisions.RecordAsync(uow, DecisionTypes.TemplateActivation, Outcomes.Approved, asRole, "ENV_POLICY_PATTERN", $"Control template v{t.VersionNo} for service {t.ServiceId}", nextReviewOn: Today.AddYears(1));

            var now = await uow.QuerySingleAsync<(int? Po, int? St)>("SELECT PlatformOwnerApprovalPersonId, SteeringApprovalPersonId FROM core.ControlTemplate WHERE TemplateId = @TemplateId", new { t.TemplateId });
            if (SegregationOfDuties.TwoApprovers(now.Po, now.St).Passed)
            {
                await uow.ExecuteAsync("UPDATE core.ControlTemplate SET Status = 'RETIRED' WHERE ServiceId = @ServiceId AND Status = 'ACTIVE'", new { t.ServiceId });
                await uow.ExecuteAsync("UPDATE core.ControlTemplate SET Status = 'ACTIVE', ActivatedUtc = @now WHERE TemplateId = @TemplateId", new { now = clock.UtcNow, t.TemplateId });
            }
            await run.AuditAsync(uow, "template.approve", "ControlTemplate", publicId, asRole);
        });

    // ---------------------------------------------------------------- workloads, bindings, discovery

    public Task SaveWorkloadBasicsAsync(Guid idem, Guid publicId, string name, int? productOwnerPersonId, int? sponsorPersonId, string? supportContact, string? complianceCaseRef)
        => run.RunAsync(idem, "workload.save", async uow =>
        {
            var w = await uow.QuerySingleOrDefaultAsync<Workload>("SELECT * FROM core.Workload WHERE PublicId = @publicId", new { publicId }) ?? throw new DomainException("Workload not found.");
            if (!(Me.Can(Permissions.WorkloadEdit, w.FundingOwnerId) || w.ProductOwnerPersonId == Me.PersonId || Me.Can(Permissions.RegistryEdit)))
                throw new DomainException("The product owner or the Platform Owner edits the workload's details.");
            if (string.IsNullOrWhiteSpace(name)) throw new DomainException("Give the workload a name.");
            await uow.ExecuteAsync("UPDATE core.Workload SET Name = @name, ProductOwnerPersonId = @productOwnerPersonId, SponsorPersonId = @sponsorPersonId, SupportContact = @support, ComplianceCaseRef = @caseRef WHERE WorkloadId = @WorkloadId",
                new { name = CommandRunner.Trim(name, 200), productOwnerPersonId, sponsorPersonId, support = CommandRunner.TrimN(supportContact, 200), caseRef = CommandRunner.TrimN(complianceCaseRef, 200), w.WorkloadId });
            await run.AuditAsync(uow, "workload.save", "Workload", publicId, name);
        });

    public Task AddBindingAsync(Guid idem, Guid workloadPublicId, string objectType, string externalId, string? displayName)
        => run.RunAsync(idem, "binding.add", async uow =>
        {
            run.RequireAny(Permissions.Verify, Permissions.RegistryEdit);
            var w = await uow.QuerySingleOrDefaultAsync<Workload>("SELECT * FROM core.Workload WHERE PublicId = @workloadPublicId", new { workloadPublicId }) ?? throw new DomainException("Workload not found.");
            if (string.IsNullOrWhiteSpace(externalId)) throw new DomainException("Give the Microsoft object's ID.");
            var other = await uow.ScalarAsync<string>("SELECT TOP (1) w.Name FROM core.WorkloadBinding b JOIN core.Workload w ON w.WorkloadId = b.WorkloadId WHERE b.ObjectType = @objectType AND b.ExternalId = @externalId AND b.EffectiveTo IS NULL AND b.WorkloadId <> @WorkloadId",
                new { objectType, externalId = externalId.Trim(), w.WorkloadId });
            if (other is not null) throw new DomainException($"That object is already bound to {other}.");
            await uow.ExecuteAsync("INSERT core.WorkloadBinding (WorkloadId, FundingOwnerId, ObjectType, ExternalId, DisplayName, EffectiveFrom) VALUES (@WorkloadId, @FundingOwnerId, @objectType, @ext, @displayName, @today)",
                new { w.WorkloadId, w.FundingOwnerId, objectType, ext = CommandRunner.Trim(externalId, 200), displayName = CommandRunner.TrimN(displayName, 400), today = Today });
            await run.AuditAsync(uow, "binding.add", "Workload", workloadPublicId, $"{objectType} {externalId}");
        });

    public Task EndBindingAsync(Guid idem, int bindingId)
        => run.RunAsync(idem, "binding.end", async uow =>
        {
            run.RequireAny(Permissions.Verify, Permissions.RegistryEdit);
            await uow.ExecuteAsync("UPDATE core.WorkloadBinding SET EffectiveTo = @today WHERE BindingId = @bindingId AND EffectiveTo IS NULL", new { today = Today, bindingId });
            await run.AuditAsync(uow, "binding.end", "WorkloadBinding", bindingId, null);
        });

    public Task SavePostureAsync(Guid idem, Guid workloadPublicId, string environmentId, decimal allocation, bool tenantPool, bool payg, string? billingPolicyId, decimal? agentLimit, bool groupRuleLock)
        => run.RunAsync(idem, "posture.save", async uow =>
        {
            run.RequireAny(Permissions.RegistryEdit);
            var w = await uow.QuerySingleOrDefaultAsync<Workload>("SELECT * FROM core.Workload WHERE PublicId = @workloadPublicId", new { workloadPublicId }) ?? throw new DomainException("Workload not found.");
            if (tenantPool && w.EnvironmentClassCode is not null && !await uow.ScalarAsync<bool>("SELECT TenantPoolAllowed FROM core.EnvironmentClass WHERE EnvironmentClassCode = @EnvironmentClassCode", new { w.EnvironmentClassCode }))
                throw new DomainException("This environment class does not allow drawing from tenant capacity; use the capacity change journey (J4) for an exception.");
            await uow.ExecuteAsync("UPDATE core.ApprovedPosture SET EffectiveTo = @yesterday WHERE WorkloadId = @WorkloadId AND EffectiveTo IS NULL", new { yesterday = Today.AddDays(-1), w.WorkloadId });
            await uow.ExecuteAsync(
                """
                INSERT core.ApprovedPosture (WorkloadId, FundingOwnerId, EnvironmentId, AllocationCredits, TenantPoolEnabled, PaygLinked, BillingPolicyId, AgentLimitCredits, GroupRuleLocksTenantPool, EffectiveFrom)
                VALUES (@WorkloadId, @FundingOwnerId, @env, @allocation, @tenantPool, @payg, @billing, @agentLimit, @groupRuleLock, @today)
                """, new { w.WorkloadId, w.FundingOwnerId, env = CommandRunner.Trim(environmentId, 100), allocation, tenantPool, payg, billing = CommandRunner.TrimN(billingPolicyId, 100), agentLimit, groupRuleLock, today = Today });
            await run.AuditAsync(uow, "posture.save", "Workload", workloadPublicId,
                $"alloc {allocation}, tenant pool {tenantPool}, PAYG {payg}, cap {(ProhibitedAssumptions.AllocationIsCap(tenantPool, payg) ? "yes" : "no")}");
        });

    /// <summary>Discovery (setup step 6): a discovered workload, or an unregistered object from a finding, is claimed by a funding owner.</summary>
    public Task<string?> ClaimAsync(Guid idem, Guid? workloadPublicId, Guid? findingPublicId, int fundingOwnerId, int? productOwnerPersonId, string? name)
        => run.RunAsync(idem, "discovery.claim", async uow =>
        {
            run.RequireAny(Permissions.RegistryClaim, Permissions.RegistryEdit);
            int workloadId;
            if (workloadPublicId is Guid wp)
            {
                workloadId = await uow.ScalarAsync<int?>("SELECT WorkloadId FROM core.Workload WHERE PublicId = @wp AND State = 'DISCOVERED'", new { wp }) ?? throw new DomainException("That workload is not waiting to be claimed.");
                await uow.ExecuteAsync("UPDATE core.Workload SET FundingOwnerId = @fundingOwnerId, ProductOwnerPersonId = COALESCE(@productOwnerPersonId, ProductOwnerPersonId), State = 'REGISTERED' WHERE WorkloadId = @workloadId",
                    new { fundingOwnerId, productOwnerPersonId, workloadId });
                await uow.ExecuteAsync("UPDATE core.WorkloadBinding SET FundingOwnerId = @fundingOwnerId WHERE WorkloadId = @workloadId", new { fundingOwnerId, workloadId });
            }
            else if (findingPublicId is Guid fp)
            {
                var f = await uow.QuerySingleOrDefaultAsync<(long FindingId, string FindingType, string? ExternalId, string Title)>(
                    "SELECT FindingId, FindingType, ExternalId, Title FROM ops.Finding WHERE PublicId = @fp AND Status IN ('OPEN','ACKNOWLEDGED')", new { fp });
                if (f.FindingId == 0 || f.ExternalId is null) throw new DomainException("That finding is not open or names no object.");
                var (type, objectType) = f.FindingType switch
                {
                    FindingTypes.UnregisteredEnvironment => ("ENVIRONMENT", "PP_ENVIRONMENT"),
                    FindingTypes.UnregisteredFlow => ("AGENT_FLOW", "AGENT_FLOW"),
                    FindingTypes.UnmatchedRegistryAgent => ("AGENT", "REGISTRY_AGENT"),
                    _ => ("AGENT", "CS_AGENT"),
                };
                workloadId = await uow.ScalarAsync<int>(
                    "INSERT core.Workload (Name, WorkloadType, FundingOwnerId, ProductOwnerPersonId, State) OUTPUT inserted.WorkloadId VALUES (@n, @type, @fundingOwnerId, @productOwnerPersonId, 'REGISTERED')",
                    new { n = CommandRunner.Trim(string.IsNullOrWhiteSpace(name) ? f.Title : name, 200), type, fundingOwnerId, productOwnerPersonId });
                await uow.ExecuteAsync("INSERT core.WorkloadBinding (WorkloadId, FundingOwnerId, ObjectType, ExternalId, DisplayName, EffectiveFrom) VALUES (@workloadId, @fundingOwnerId, @objectType, @ext, @n, @today)",
                    new { workloadId, fundingOwnerId, objectType, ext = f.ExternalId, n = name, today = Today });
                await uow.ExecuteAsync("UPDATE ops.Finding SET Status = 'RESOLVED', ResolvedUtc = @now, ResolvedByPersonId = @me, Resolution = 'Claimed and registered.', WorkloadId = @workloadId, FundingOwnerId = @fundingOwnerId WHERE FindingId = @FindingId",
                    new { now = clock.UtcNow, me = Me.PersonId, workloadId, fundingOwnerId, f.FindingId });
            }
            else throw new DomainException("Choose what to claim.");
            await uow.ExecuteAsync("UPDATE ops.Finding SET Status = 'RESOLVED', ResolvedUtc = @now, ResolvedByPersonId = @me, Resolution = 'Claimed and registered.' WHERE WorkloadId = @workloadId AND FindingType LIKE 'UNREGISTERED%' AND Status IN ('OPEN','ACKNOWLEDGED')",
                new { now = clock.UtcNow, me = Me.PersonId, workloadId });
            await run.AuditAsync(uow, "discovery.claim", "Workload", workloadId, $"funding owner {fundingOwnerId}");
            return await uow.ScalarAsync<string>("SELECT CAST(PublicId AS varchar(40)) FROM core.Workload WHERE WorkloadId = @workloadId", new { workloadId });
        });

    // ---------------------------------------------------------------- envelopes and commercial

    public Task CreateEnvelopeAsync(Guid idem, int fundingOwnerId, int serviceId, DateTime start, DateTime end, decimal amount, string unit)
        => run.RunAsync(idem, "envelope.create", async uow =>
        {
            run.Require(Permissions.EnvelopeManage, null, "FinOps prepares envelopes.");
            if (end < start) throw new DomainException("The period ends before it starts.");
            if (amount <= 0) throw new DomainException("Give a positive amount.");
            if (unit is not (Units.CopilotCredit or Units.P3CommitUnit or "USD" or "CAD" or "EUR" or "GBP")) throw new DomainException("Choose the unit.");
            var overlap = await uow.ScalarAsync<int>("SELECT COUNT(*) FROM core.FundingEnvelope WHERE FundingOwnerId = @fundingOwnerId AND ServiceId = @serviceId AND Status IN ('DRAFT','ACTIVE') AND PeriodStart <= @end AND PeriodEnd >= @start",
                new { fundingOwnerId, serviceId, start, end });
            if (overlap > 0) throw new DomainException("An envelope for this funding owner and service already covers part of that period.");
            await uow.ExecuteAsync("INSERT core.FundingEnvelope (FundingOwnerId, ServiceId, PeriodStart, PeriodEnd, Amount, Unit, Status) VALUES (@fundingOwnerId, @serviceId, @start, @end, @amount, @unit, 'DRAFT')",
                new { fundingOwnerId, serviceId, start = start.Date, end = end.Date, amount, unit });
            await run.AuditAsync(uow, "envelope.create", "FundingOwner", fundingOwnerId, $"{amount} {unit} service {serviceId}");
        });

    /// <summary>The business owner approves the envelope: a decision under Funding and annual budget.</summary>
    public Task ApproveEnvelopeAsync(Guid idem, Guid envelopePublicId, bool approve, string? reason)
        => run.RunAsync(idem, "envelope.approve", async uow =>
        {
            var e = await uow.QuerySingleOrDefaultAsync<(int EnvelopeId, int FundingOwnerId, string Status, decimal Amount, string Unit, DateTime PeriodStart, DateTime PeriodEnd, string Service)>(
                "SELECT e.EnvelopeId, e.FundingOwnerId, e.Status, e.Amount, e.Unit, e.PeriodStart, e.PeriodEnd, s.Name FROM core.FundingEnvelope e JOIN core.Service s ON s.ServiceId = e.ServiceId WHERE e.PublicId = @envelopePublicId",
                new { envelopePublicId });
            if (e.EnvelopeId == 0) throw new DomainException("Envelope not found.");
            if (e.Status != "DRAFT") throw new DomainException("This envelope is not waiting for approval.");
            var role = Me.HasRoleIn(Roles.LobBusinessOwner, e.FundingOwnerId) ? Roles.LobBusinessOwner : Me.HasRoleIn(Roles.SharedServiceOwner, e.FundingOwnerId) ? Roles.SharedServiceOwner : null;
            if (role is null || !Me.Can(Permissions.EnvelopeApprove, e.FundingOwnerId)) throw new DomainException("The funding owner's business owner approves its envelopes.");
            if (!approve && string.IsNullOrWhiteSpace(reason)) throw new DomainException("Give the reason for declining.");
            var id = await decisions.RecordAsync(uow, DecisionTypes.EnvelopeApproval, approve ? Outcomes.Approved : Outcomes.Declined, role, "FUNDING_BUDGET",
                $"{e.Service}: {e.Amount:N0} {Units.Label(e.Unit)} from {e.PeriodStart:d MMM yyyy} to {e.PeriodEnd:d MMM yyyy}", e.FundingOwnerId, reason: reason,
                expiresOn: approve ? e.PeriodEnd : null);
            await uow.ExecuteAsync("UPDATE core.FundingEnvelope SET Status = @status, ApprovedDecisionId = @id WHERE EnvelopeId = @EnvelopeId",
                new { status = approve ? "ACTIVE" : "CLOSED", id, e.EnvelopeId });
            await run.AuditAsync(uow, "envelope.approve", "Envelope", envelopePublicId, approve ? "approved" : "declined");
        });

    public Task CloseEnvelopeAsync(Guid idem, Guid envelopePublicId)
        => run.RunAsync(idem, "envelope.close", async uow =>
        {
            run.Require(Permissions.EnvelopeManage);
            await uow.ExecuteAsync("UPDATE core.FundingEnvelope SET Status = 'CLOSED' WHERE PublicId = @envelopePublicId", new { envelopePublicId });
            await run.AuditAsync(uow, "envelope.close", "Envelope", envelopePublicId, null);
        });

    public Task SaveCommercialAsync(Guid idem, Guid? publicId, CommercialInput i, byte[]? rowVer)
        => run.RunAsync(idem, "commercial.save", async uow =>
        {
            run.Require(Permissions.RegistryFinance, null, "FinOps records the commercial inventory.");
            if (string.IsNullOrWhiteSpace(i.Name)) throw new DomainException("Give the item a name.");
            if (i.ItemType is not ("LICENCE" or "PACK" or "P3" or "PAYG_SCOPE")) throw new DomainException("Choose the item type.");
            var args = new
            {
                i.ItemType, Name = CommandRunner.Trim(i.Name, 200), i.Quantity, i.Unit, StartOn = i.StartOn?.Date, EndOn = i.EndOn?.Date, RenewalOn = i.RenewalOn?.Date, i.AutoRenew,
                BillingScope = CommandRunner.TrimN(i.BillingScope, 400), ContractRef = CommandRunner.TrimN(i.ContractRef, 200), i.PurchaseAuthorityPersonId, i.ReconciledToContract,
                Notes = CommandRunner.TrimN(i.Notes, 2000), publicId, rowVer,
            };
            if (publicId is null)
                await uow.ExecuteAsync(
                    """
                    INSERT core.CommercialItem (ItemType, Name, Quantity, Unit, StartOn, EndOn, RenewalOn, AutoRenew, BillingScope, ContractRef, PurchaseAuthorityPersonId, ReconciledToContract, Notes)
                    VALUES (@ItemType, @Name, @Quantity, @Unit, @StartOn, @EndOn, @RenewalOn, @AutoRenew, @BillingScope, @ContractRef, @PurchaseAuthorityPersonId, @ReconciledToContract, @Notes)
                    """, args);
            else if (await uow.ExecuteAsync(
                         """
                         UPDATE core.CommercialItem SET ItemType = @ItemType, Name = @Name, Quantity = @Quantity, Unit = @Unit, StartOn = @StartOn, EndOn = @EndOn, RenewalOn = @RenewalOn, AutoRenew = @AutoRenew,
                                BillingScope = @BillingScope, ContractRef = @ContractRef, PurchaseAuthorityPersonId = @PurchaseAuthorityPersonId, ReconciledToContract = @ReconciledToContract, Notes = @Notes
                         WHERE PublicId = @publicId AND (@rowVer IS NULL OR RowVer = @rowVer)
                         """, args) == 0) throw new ConcurrencyException();
            await run.AuditAsync(uow, "commercial.save", "CommercialItem", i.Name, i.ItemType);
        });

    private static async Task<int> FundingOwnerIdAsync(Uow uow, Guid publicId)
        => await uow.ScalarAsync<int?>("SELECT FundingOwnerId FROM core.FundingOwner WHERE PublicId = @publicId", new { publicId }) ?? throw new DomainException("Funding owner not found.");
}
