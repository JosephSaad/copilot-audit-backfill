using Governance.Application.Data;
using Governance.Application.Queries;
using Governance.Application.Rules;
using Governance.Domain;

namespace Governance.Application.Commands;

/// <summary>Configuration with two-person approval where the operating model needs it (SoD5, SoD6).</summary>
public sealed class AdminCommands(CommandRunner run, DecisionRecorder decisions, AdminQueries admin, WorkQueries work, IClock clock)
{
    private ICurrentPrincipal Me => run.Principal;

    private void RequireAdmin()
    {
        if (!Me.Can(Permissions.AdminConfigure) && !Me.HasRole(Roles.PlatformOwner))
            throw new DomainException("Workbench administrators and the Platform Owner change configuration.");
    }

    // ---------------------------------------------------------------- RACI versions

    public Task<string?> DraftRaciAsync(Guid idem, string? notes)
        => run.RunAsync(idem, "raci.draft", async uow =>
        {
            RequireAdmin();
            if (await uow.ScalarAsync<int>("SELECT COUNT(*) FROM sec.RaciVersion WHERE Status IN ('DRAFT','PENDING')") > 0)
                throw new DomainException("A draft RACI version already exists; edit or activate it first.");
            var source = await uow.ScalarAsync<int?>("SELECT RaciVersionId FROM sec.RaciVersion WHERE Status = 'ACTIVE'")
                         ?? await uow.ScalarAsync<int?>("SELECT MAX(RaciVersionId) FROM sec.RaciVersion");
            var next = (await uow.ScalarAsync<int?>("SELECT MAX(VersionNo) FROM sec.RaciVersion") ?? 0) + 1;
            var id = await uow.ScalarAsync<int>("INSERT sec.RaciVersion (VersionNo, Status, Notes, CreatedByPersonId) OUTPUT inserted.RaciVersionId VALUES (@next, 'DRAFT', @notes, @me)",
                new { next, notes = CommandRunner.TrimN(notes, 2000), me = Me.PersonId });
            if (source is int s)
                await uow.ExecuteAsync("INSERT sec.RaciCell (RaciVersionId, ActivityCode, FunctionCode, RaciValue) SELECT @id, ActivityCode, FunctionCode, RaciValue FROM sec.RaciCell WHERE RaciVersionId = @s", new { id, s });
            await run.AuditAsync(uow, "raci.draft", "RaciVersion", next, notes);
            return next.ToString();
        });

    public Task SetRaciCellAsync(Guid idem, int versionNo, string activityCode, string functionCode, string value)
        => run.RunAsync(idem, "raci.cell", async uow =>
        {
            RequireAdmin();
            if (value is not ("R" or "A" or "A/R" or "C" or "I")) throw new DomainException("A cell is R, A, A/R, C or I.");
            if (functionCode == Functions.Microsoft && value is "R" or "A" or "A/R") throw new DomainException("Microsoft supplies evidence and advice and holds no R or A.");
            var v = await uow.QuerySingleOrDefaultAsync<(int Id, string Status)>("SELECT RaciVersionId, Status FROM sec.RaciVersion WHERE VersionNo = @versionNo", new { versionNo });
            if (v.Id == 0 || v.Status != "DRAFT") throw new DomainException("Only a draft version can be edited.");
            // Moving an A: clear the other A first so the unique index (one accountable per activity) never blocks the edit.
            if (value is "A" or "A/R")
                await uow.ExecuteAsync("UPDATE sec.RaciCell SET RaciValue = CASE WHEN RaciValue = 'A/R' THEN 'R' ELSE 'C' END WHERE RaciVersionId = @Id AND ActivityCode = @activityCode AND FunctionCode <> @functionCode AND RaciValue IN ('A','A/R')",
                    new { v.Id, activityCode, functionCode });
            await uow.ExecuteAsync(
                """
                MERGE sec.RaciCell AS t USING (SELECT @Id AS RaciVersionId, @activityCode AS ActivityCode, @functionCode AS FunctionCode) AS s
                ON t.RaciVersionId = s.RaciVersionId AND t.ActivityCode = s.ActivityCode AND t.FunctionCode = s.FunctionCode
                WHEN MATCHED THEN UPDATE SET RaciValue = @value
                WHEN NOT MATCHED THEN INSERT (RaciVersionId, ActivityCode, FunctionCode, RaciValue) VALUES (@Id, @activityCode, @functionCode, @value);
                """, new { v.Id, activityCode, functionCode, value });
            // Any edit clears approvals already given.
            await uow.ExecuteAsync("UPDATE sec.RaciVersion SET PlatformOwnerApprovalPersonId = NULL, PlatformOwnerApprovalUtc = NULL, SteeringApprovalPersonId = NULL, SteeringApprovalUtc = NULL WHERE RaciVersionId = @Id", new { v.Id });
            await run.AuditAsync(uow, "raci.cell", "RaciVersion", versionNo, $"{activityCode} {functionCode} = {value}");
        });

    /// <summary>Platform Owner and Steering approve (different people, SoD6); activation needs exactly one A or A/R per activity.</summary>
    public Task ApproveRaciAsync(Guid idem, int versionNo, string asRole)
        => run.RunAsync(idem, "raci.approve", async uow =>
        {
            var v = await uow.QuerySingleOrDefaultAsync<(int Id, string Status, int? Po, int? St)>(
                "SELECT RaciVersionId, Status, PlatformOwnerApprovalPersonId, SteeringApprovalPersonId FROM sec.RaciVersion WHERE VersionNo = @versionNo", new { versionNo });
            if (v.Id == 0) throw new DomainException("RACI version not found.");
            if (v.Status is not ("DRAFT" or "PENDING")) throw new DomainException("This version is not waiting for approval.");
            var matrix = await admin.RaciAsync(versionNo) ?? throw new DomainException("RACI version not found.");
            var problems = matrix.Problems();
            if (problems.Count > 0) throw new DomainException("Fix the matrix first: " + string.Join(" ", problems));
            if (asRole == Roles.PlatformOwner)
            {
                if (!Me.HasRole(Roles.PlatformOwner)) throw new DomainException("You don't hold the Platform Owner role.");
                if (v.St == Me.PersonId) throw new DomainException("You approved as Steering; the other approval must come from someone else (SoD6).");
                await uow.ExecuteAsync("UPDATE sec.RaciVersion SET PlatformOwnerApprovalPersonId = @me, PlatformOwnerApprovalUtc = @now, Status = 'PENDING' WHERE RaciVersionId = @Id", new { me = Me.PersonId, now = clock.UtcNow, v.Id });
            }
            else if (asRole == Roles.Steering)
            {
                if (!Me.HasRole(Roles.Steering)) throw new DomainException("You don't hold the Steering role.");
                if (v.Po == Me.PersonId) throw new DomainException("You approved as Platform Owner; the other approval must come from someone else (SoD6).");
                await uow.ExecuteAsync("UPDATE sec.RaciVersion SET SteeringApprovalPersonId = @me, SteeringApprovalUtc = @now, Status = 'PENDING' WHERE RaciVersionId = @Id", new { me = Me.PersonId, now = clock.UtcNow, v.Id });
            }
            else throw new DomainException("Approve as Platform Owner or Steering.");

            var now = await uow.QuerySingleAsync<(int? Po, int? St)>("SELECT PlatformOwnerApprovalPersonId, SteeringApprovalPersonId FROM sec.RaciVersion WHERE RaciVersionId = @Id", new { v.Id });
            if (SegregationOfDuties.TwoApprovers(now.Po, now.St).Passed)
            {
                await uow.ExecuteAsync("UPDATE sec.RaciVersion SET Status = 'RETIRED' WHERE Status = 'ACTIVE'");
                await uow.ExecuteAsync("UPDATE sec.RaciVersion SET Status = 'ACTIVE', ActivatedUtc = @now WHERE RaciVersionId = @Id", new { now = clock.UtcNow, v.Id });
            }
            // The decision row cites the version being approved (it may not be active yet).
            await decisions.RecordAsync(uow, DecisionTypes.RaciActivation, Outcomes.Approved, asRole, "ENV_POLICY_PATTERN", $"RACI matrix version {versionNo}", nextReviewOn: clock.UtcNow.Date.AddYears(1));
            await run.AuditAsync(uow, "raci.approve", "RaciVersion", versionNo, asRole);
        });

    // ---------------------------------------------------------------- intake and standards

    public Task ApproveStandardsAsync(Guid idem)
        => run.RunAsync(idem, "setup.standards", async uow =>
        {
            if (!Me.HasRole(Roles.PlatformOwner)) throw new DomainException("The Platform Owner approves the standards (setup step 5).");
            await uow.ExecuteAsync(
                """
                MERGE ref.Setting AS t USING (SELECT 'Setup.StandardsApprovedUtc' AS SettingKey) AS s ON t.SettingKey = s.SettingKey
                WHEN MATCHED THEN UPDATE SET SettingValue = @now, UpdatedUtc = SYSUTCDATETIME(), UpdatedByPersonId = @me
                WHEN NOT MATCHED THEN INSERT (SettingKey, SettingValue, Description, UpdatedByPersonId) VALUES ('Setup.StandardsApprovedUtc', @now, N'When the Platform Owner approved SLAs, escalation and recipients (setup step 5).', @me);
                """, new { now = clock.UtcNow.ToString("o"), me = Me.PersonId });
            await run.AuditAsync(uow, "setup.standards", "Setting", "Setup.StandardsApprovedUtc", null);
        });

    /// <summary>Intake (B1) opens only after setup steps 0 to 7 (section 4.1).</summary>
    public Task SetIntakeAsync(Guid idem, bool open)
        => run.RunAsync(idem, "intake.switch", async uow =>
        {
            if (!Me.HasRole(Roles.PlatformOwner) || !Me.Can(Permissions.IntakeOpen)) throw new DomainException("The Platform Owner opens and closes intake.");
            if (open)
            {
                var steps = await work.SetupAsync();
                var missing = steps.Where(s => s.Step < 8 && !s.Done).ToList();
                if (missing.Count > 0) throw new DomainException("Intake stays closed until the foundation is built: " + string.Join("; ", missing.Select(s => $"step {s.Step} ({s.What})")) + ".");
            }
            await uow.ExecuteAsync("UPDATE ref.Setting SET SettingValue = @v, UpdatedUtc = SYSUTCDATETIME(), UpdatedByPersonId = @me WHERE SettingKey = 'Intake.Open'", new { v = open ? "true" : "false", me = Me.PersonId });
            await run.AuditAsync(uow, "intake.switch", "Setting", "Intake.Open", open ? "opened" : "closed");
        });

    // ---------------------------------------------------------------- notification rules, SLAs, settings, text

    public Task SetNotificationRuleAsync(Guid idem, string code, bool enabled)
        => run.RunAsync(idem, "notification.rule", async uow =>
        {
            RequireAdmin();
            await uow.ExecuteAsync("UPDATE ntf.NotificationRule SET IsEnabled = @enabled WHERE Code = @code", new { enabled, code });
            await run.AuditAsync(uow, "notification.rule", "NotificationRule", code, enabled ? "enabled" : "disabled");
        });

    public Task SetSlaAsync(Guid idem, string code, int duration)
        => run.RunAsync(idem, "sla.set", async uow =>
        {
            if (!Me.HasRole(Roles.PlatformOwner)) throw new DomainException("The Platform Owner changes SLAs (A5).");
            if (duration < 0 || duration > 90) throw new DomainException("Give a duration between 0 and 90.");
            await uow.ExecuteAsync("UPDATE wf.SlaPolicy SET Duration = @duration WHERE SlaPolicyCode = @code", new { duration, code });
            await run.AuditAsync(uow, "sla.set", "SlaPolicy", code, duration.ToString());
        });

    public Task MapGroupAsync(Guid idem, Guid groupId, string roleCode, string? groupName, bool remove)
        => run.RunAsync(idem, "group.map", async uow =>
        {
            run.Require(Permissions.RoleAssignCentral, null, "The Platform Owner maps Entra groups to central roles.");
            if (remove)
                await uow.ExecuteAsync("DELETE FROM sec.GroupRoleMap WHERE EntraGroupId = @groupId AND RoleCode = @roleCode", new { groupId, roleCode });
            else
            {
                if (await uow.ScalarAsync<string>("SELECT ScopeType FROM sec.RoleDefinition WHERE RoleCode = @roleCode", new { roleCode }) != ScopeTypes.Tenant)
                    throw new DomainException("Only central roles are synced from Entra groups; LOB roles are assigned in the Workbench.");
                await uow.ExecuteAsync("IF NOT EXISTS (SELECT 1 FROM sec.GroupRoleMap WHERE EntraGroupId = @groupId AND RoleCode = @roleCode) INSERT sec.GroupRoleMap (EntraGroupId, RoleCode, GroupName) VALUES (@groupId, @roleCode, @name)",
                    new { groupId, roleCode, name = CommandRunner.TrimN(groupName, 256) });
            }
            await run.AuditAsync(uow, "group.map", "GroupRoleMap", groupId, $"{(remove ? "remove" : "add")} {roleCode}");
        });

    /// <summary>A Workbench setting changed on Workbench settings › Settings, checked by <see cref="WorkbenchSettings.Check"/>, audited with its old and new value.</summary>
    public Task SaveSettingAsync(Guid idem, string key, string? value)
        => run.RunAsync(idem, "setting.save", async uow =>
        {
            RequireAdmin();
            if (!WorkbenchSettings.OnSettingsPage(key)) throw new DomainException("That setting is changed on its own page.");
            var v = WorkbenchSettings.Check(key, value);
            var old = await uow.ScalarAsync<string?>("SELECT SettingValue FROM ref.Setting WHERE SettingKey = @key", new { key })
                ?? throw new DomainException("That setting is not a Workbench setting.");
            await uow.ExecuteAsync("UPDATE ref.Setting SET SettingValue = @v, UpdatedUtc = SYSUTCDATETIME(), UpdatedByPersonId = @me WHERE SettingKey = @key",
                new { v, key, me = Me.PersonId });
            await run.AuditAsync(uow, "setting.save", "Setting", null, $"{key}: {old} -> {v}");
        });

    /// <summary>Business wording: an administrator drafts, the Platform Owner approves (P3: the operating model as data).</summary>
    public Task SaveTextAsync(Guid idem, string key, string body)
        => run.RunAsync(idem, "text.draft", async uow =>
        {
            RequireAdmin();
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(body)) throw new DomainException("Give the key and the text.");
            var next = (await uow.ScalarAsync<int?>("SELECT MAX(VersionNo) FROM ref.TextBlock WHERE TextKey = @key", new { key }) ?? 0) + 1;
            await uow.ExecuteAsync("DELETE FROM ref.TextBlock WHERE TextKey = @key AND Status = 'DRAFT'", new { key });
            await uow.ExecuteAsync("INSERT ref.TextBlock (TextKey, VersionNo, Body, Status) VALUES (@key, @next, @body, 'DRAFT')", new { key = CommandRunner.Trim(key, 120), next, body = CommandRunner.Trim(body, 4000) });
            await run.AuditAsync(uow, "text.draft", "TextBlock", key, null);
        });

    public Task ApproveTextAsync(Guid idem, string key, int versionNo)
        => run.RunAsync(idem, "text.approve", async uow =>
        {
            if (!Me.HasRole(Roles.PlatformOwner)) throw new DomainException("The Platform Owner approves business wording.");
            await uow.ExecuteAsync("UPDATE ref.TextBlock SET Status = 'RETIRED' WHERE TextKey = @key AND Status = 'ACTIVE'", new { key });
            var n = await uow.ExecuteAsync("UPDATE ref.TextBlock SET Status = 'ACTIVE', ApprovedByPersonId = @me, UpdatedUtc = SYSUTCDATETIME() WHERE TextKey = @key AND VersionNo = @versionNo AND Status = 'DRAFT'",
                new { me = Me.PersonId, key, versionNo });
            if (n == 0) throw new DomainException("That draft was not found.");
            await run.AuditAsync(uow, "text.approve", "TextBlock", key, versionNo.ToString());
        });
}
