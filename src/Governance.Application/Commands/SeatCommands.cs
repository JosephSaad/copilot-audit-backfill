using System.Text.Json;
using Governance.Application.Data;
using Governance.Application.Outbox;
using Governance.Application.Queries;
using Governance.Domain;

namespace Governance.Application.Commands;

public sealed record SeatGroupInput(string DisplayName, string Purpose, int? FundingOwnerId, string? OimRoleName, string Source, bool PurposeConfirmed, string? Notes, Guid? EntraGroupId);

/// <summary>
/// Copilot seat actions taken on pages: the first reservation baseline, reclaim answers, chargeback certification,
/// seat group and SKU mapping, and LOB seat admins. Reservation changes after the baseline go through J9.
/// </summary>
public sealed class SeatCommands(CommandRunner run, IClock clock, OutboxWriter outbox)
{
    private ICurrentPrincipal Me => run.Principal;
    private DateTime Today => clock.UtcNow.Date;

    private static readonly string[] OwnerRoles = { Roles.LobBusinessOwner, Roles.SharedServiceOwner };

    private static async Task<int> FundingOwnerIdAsync(Uow uow, Guid publicId)
        => await uow.ScalarAsync<int?>("SELECT FundingOwnerId FROM core.FundingOwner WHERE PublicId = @publicId", new { publicId })
           ?? throw new DomainException("Funding owner not found.");

    /// <summary>Active roles the acting person holds on this funding owner (read from the database, not the session).</summary>
    private Task<IEnumerable<string>> MyRolesAsync(Uow uow, int fundingOwnerId)
        => uow.QueryAsync<string>(
            """
            SELECT RoleCode FROM sec.RoleAssignment
            WHERE PersonId = @me AND EffectiveFrom <= @today AND (EffectiveTo IS NULL OR EffectiveTo >= @today)
              AND ((ScopeType = 'FUNDING_OWNER' AND ScopeId = @fundingOwnerId) OR ScopeType = 'TENANT')
            """, new { me = Me.PersonId, today = Today, fundingOwnerId });

    // ---------------------------------------------------------------- first reservation (baseline)

    /// <summary>The first reservation of an LOB, recorded by Finance once. Every later change is a J9 request (CAL-06).</summary>
    public Task<string?> RecordInitialReservationAsync(Guid idem, Guid fundingOwnerPublicId, int seats, DateTime effectiveFrom, string? notes)
        => run.RunAsync(idem, "seat.reservation.initial", async uow =>
        {
            run.Require(Permissions.RegistryFinance, null, "FinOps records the first seat reservation of an LOB.");
            var fo = await FundingOwnerIdAsync(uow, fundingOwnerPublicId);
            if (seats < 0) throw new DomainException("Seats cannot be negative.");
            if (await uow.ScalarAsync<int>("SELECT COUNT(*) FROM core.SeatReservation WHERE FundingOwnerId = @fo", new { fo }) > 0)
                throw new DomainException("This LOB already has a seat reservation. Change it with a seat reservation request (J9), so the change is approved and recorded.");
            var id = await uow.ScalarAsync<Guid>(
                """
                INSERT core.SeatReservation (FundingOwnerId, Seats, PreviousSeats, ChangeKind, EffectiveFrom, ApprovedByPersonId, Notes, CreatedByPersonId)
                OUTPUT inserted.PublicId VALUES (@fo, @seats, NULL, 'INITIAL', @from, @me, @notes, @me)
                """, new { fo, seats, from = effectiveFrom.Date, me = Me.PersonId, notes = CommandRunner.TrimN(notes, 1000) });
            await run.AuditAsync(uow, "seat.reservation.initial", "SeatReservation", id, $"{seats} seats from {effectiveFrom:yyyy-MM-dd}");
            return id.ToString();
        });

    // ---------------------------------------------------------------- reclaim

    /// <summary>
    /// The LOB business owner (or a delegated approver) keeps or reclaims an inactive seat. Reclaim opens an ITSM task to
    /// remove the person's OIM role; the read-back marks the row REMOVED when the seat is gone.
    /// </summary>
    public Task<string?> DecideReclaimAsync(Guid idem, Guid reclaimPublicId, bool reclaim, string? reason)
        => run.RunAsync(idem, "seat.reclaim.decide", async uow =>
        {
            var r = await uow.QuerySingleOrDefaultAsync<(int Id, int? Fo, int PersonId, string Status, string Name, string? Upn, DateTime? Last)>(
                """
                SELECT r.SeatReclaimId, r.FundingOwnerId, r.PersonId, r.Status, p.DisplayName, p.UserPrincipalName, r.LastActivityDate
                FROM core.SeatReclaim r JOIN sec.Person p ON p.PersonId = r.PersonId WHERE r.PublicId = @reclaimPublicId
                """, new { reclaimPublicId });
            if (r.Id == 0) throw new DomainException("Reclaim item not found.");
            if (r.Status != SeatReclaimStatuses.Proposed) throw new DomainException("This seat was already answered.");
            if (r.Fo is not int fo) throw new DomainException("This seat has no LOB: Platform Operations handles it from the findings.");
            var mine = (await MyRolesAsync(uow, fo)).ToHashSet();
            if (!mine.Overlaps(new[] { Roles.LobBusinessOwner, Roles.SharedServiceOwner, Roles.DelegatedApprover }))
                throw new DomainException("The LOB's business owner or a delegated approver answers reclaim proposals.");
            if (string.IsNullOrWhiteSpace(reason)) throw new DomainException("Give the reason: it is kept with the answer.");
            string? ticketRef = null;
            if (reclaim)
            {
                var lob = await uow.QuerySingleAsync<(string Name, string? Oim)>(
                    """
                    SELECT fo.Name, (SELECT TOP (1) OimRoleName FROM core.SeatGroup g WHERE g.FundingOwnerId = fo.FundingOwnerId AND g.Purpose = 'LOB_TRACKING' AND g.EffectiveTo IS NULL)
                    FROM core.FundingOwner fo WHERE fo.FundingOwnerId = @fo
                    """, new { fo });
                var oimRole = lob.Oim ?? $"{lob.Name} . M365 Copilot License . Role";
                var payload = JsonSerializer.Serialize(new
                {
                    shortDescription = $"Remove Copilot seat: {r.Name} ({lob.Name})",
                    action = "Remove the OIM role so the person leaves the LOB tracking group and the Copilot license group",
                    oimRole, person = r.Name, userPrincipalName = r.Upn, lastCopilotActivity = r.Last?.ToString("yyyy-MM-dd") ?? "none in the report period",
                    reason = CommandRunner.Trim(reason, 1000),
                    preApproved = true,
                    note = "Approved by the LOB in the Copilot Governance Workbench. Do not approve again in ITSM. The Workbench confirms removal from the next license read-back.",
                });
                var ticketId = await uow.ScalarAsync<long>(
                    "INSERT wf.ItsmTicket (FundingOwnerId, Kind, CorrelationId, State, Payload) OUTPUT inserted.ItsmTicketId VALUES (@fo, 'TASK', NEWID(), 'PENDING_CREATE', @payload)",
                    new { fo, payload });
                await outbox.EnqueueAsync(uow, new OutboxMessage { EventType = "ITSM.Create", ItsmTicketId = ticketId, FundingOwnerId = fo }, $"ITSM.Create:{ticketId}");
                ticketRef = ticketId.ToString();
            }
            await uow.ExecuteAsync(
                """
                UPDATE core.SeatReclaim SET Status = @status, Reason = @reason, DecidedByPersonId = @me, DecidedUtc = @now, TicketRef = @ticketRef,
                       ClosedUtc = CASE WHEN @status = 'KEEP' THEN @now ELSE NULL END
                WHERE SeatReclaimId = @Id AND Status = 'PROPOSED'
                """, new { status = reclaim ? SeatReclaimStatuses.Reclaim : SeatReclaimStatuses.Keep, reason = CommandRunner.Trim(reason, 1000), me = Me.PersonId, now = clock.UtcNow, ticketRef, r.Id });
            await run.AuditAsync(uow, reclaim ? "seat.reclaim" : "seat.keep", "SeatReclaim", reclaimPublicId, reason);
            return ticketRef;
        });

    // ---------------------------------------------------------------- chargeback

    /// <summary>The FinOps lead certifies every draft row of a month at once. Certified rows never change (CAL-05).</summary>
    public Task<string?> CertifyMonthAsync(Guid idem, DateTime month)
        => run.RunAsync(idem, "seat.chargeback.certify", async uow =>
        {
            var mine = (await MyRolesAsync(uow, 0)).ToHashSet();
            if (!mine.Contains(Roles.FinOpsLead)) throw new DomainException("The FinOps lead certifies the seat chargeback.");
            var m = new DateTime(month.Year, month.Month, 1);
            if (m >= new DateTime(Today.Year, Today.Month, 1)) throw new DomainException("A month is certified after it ends.");
            var n = await uow.ExecuteAsync(
                "UPDATE core.SeatChargeSnapshot SET Status = 'CERTIFIED', CertifiedByPersonId = @me, CertifiedUtc = @now WHERE PeriodMonth = @m AND Status = 'DRAFT'",
                new { m, me = Me.PersonId, now = clock.UtcNow });
            if (n == 0) throw new DomainException("There is no draft snapshot for this month to certify.");
            await run.AuditAsync(uow, "seat.chargeback.certify", "SeatChargeSnapshot", m.ToString("yyyy-MM"), $"{n} rows");
            return n.ToString();
        });

    // ---------------------------------------------------------------- seat groups and SKUs

    private void RequireConfigure()
    {
        if (!Me.Can(Permissions.AdminConfigure) && !Me.Can(Permissions.RegistryEdit))
            throw new DomainException("The Platform Owner or a Workbench administrator maps seat groups.");
    }

    private static readonly HashSet<string> Purposes = new() { SeatGroupPurposes.License, SeatGroupPurposes.LobTracking, SeatGroupPurposes.Exclusion, SeatGroupPurposes.ChatOnly, SeatGroupPurposes.Manager, SeatGroupPurposes.Other };

    /// <summary>Adds a group, or changes one: the current row is ended and a new row starts today (history kept).</summary>
    public Task<string?> SaveGroupAsync(Guid idem, Guid? publicId, SeatGroupInput i)
        => run.RunAsync(idem, "seat.group.save", async uow =>
        {
            RequireConfigure();
            var name = CommandRunner.Trim(i.DisplayName, 256);
            if (name.Length == 0) throw new DomainException("Enter the group's name as it shows in Entra.");
            if (!Purposes.Contains(i.Purpose)) throw new DomainException("Choose what the group is for.");
            if (i.Purpose == SeatGroupPurposes.LobTracking && i.FundingOwnerId is null) throw new DomainException("A tracking group needs the LOB it tracks.");
            if (i.Source is not ("ON_PREM_AD" or "CLOUD")) throw new DomainException("Choose where the group comes from.");
            var fo = i.Purpose == SeatGroupPurposes.LobTracking ? i.FundingOwnerId : null;
            Guid? entraId = i.EntraGroupId;
            if (publicId is Guid pid)
            {
                var old = await uow.QuerySingleOrDefaultAsync<(int Id, Guid? EntraGroupId, string DisplayName)>(
                    "SELECT SeatGroupId, EntraGroupId, DisplayName FROM core.SeatGroup WHERE PublicId = @pid AND EffectiveTo IS NULL", new { pid });
                if (old.Id == 0) throw new DomainException("This group mapping was changed or ended by someone else. Reload the page.");
                if (entraId is null && string.Equals(old.DisplayName, name, StringComparison.OrdinalIgnoreCase)) entraId = old.EntraGroupId;
                await uow.ExecuteAsync("UPDATE core.SeatGroup SET EffectiveTo = @yesterday WHERE SeatGroupId = @Id", new { yesterday = Today.AddDays(-1), old.Id });
            }
            if (fo is int lob && await uow.ScalarAsync<int>("SELECT COUNT(*) FROM core.SeatGroup WHERE FundingOwnerId = @lob AND Purpose = 'LOB_TRACKING' AND EffectiveTo IS NULL", new { lob }) > 0)
                throw new DomainException("This LOB already has a tracking group. End or change that one first.");
            if (await uow.ScalarAsync<int>("SELECT COUNT(*) FROM core.SeatGroup WHERE DisplayName = @name AND EffectiveTo IS NULL", new { name }) > 0)
                throw new DomainException("A group with this name is already mapped.");
            var id = await uow.ScalarAsync<Guid>(
                """
                INSERT core.SeatGroup (EntraGroupId, DisplayName, Purpose, FundingOwnerId, OimRoleName, Source, PurposeConfirmed, Notes, EffectiveFrom)
                OUTPUT inserted.PublicId VALUES (@entraId, @name, @Purpose, @fo, @oim, @Source, @PurposeConfirmed, @notes, @today)
                """, new { entraId, name, i.Purpose, fo, oim = CommandRunner.TrimN(i.OimRoleName, 300), i.Source, i.PurposeConfirmed, notes = CommandRunner.TrimN(i.Notes, 1000), today = Today });
            await run.AuditAsync(uow, "seat.group.save", "SeatGroup", id, $"{name} {i.Purpose} {fo}");
            return id.ToString();
        });

    public Task EndGroupAsync(Guid idem, Guid publicId)
        => run.RunAsync(idem, "seat.group.end", async uow =>
        {
            RequireConfigure();
            var n = await uow.ExecuteAsync("UPDATE core.SeatGroup SET EffectiveTo = @yesterday WHERE PublicId = @publicId AND EffectiveTo IS NULL", new { yesterday = Today.AddDays(-1), publicId });
            if (n == 0) throw new DomainException("This group mapping was already ended.");
            await run.AuditAsync(uow, "seat.group.end", "SeatGroup", publicId);
        });

    public Task SaveSkuAsync(Guid idem, Guid skuId, string partNumber, string name, string kind, bool isActive, string? notes)
        => run.RunAsync(idem, "seat.sku.save", async uow =>
        {
            RequireConfigure();
            if (kind is not (SeatSkuKinds.CopilotAddOn or SeatSkuKinds.IncludesCopilot)) throw new DomainException("Choose whether the SKU is the Copilot add-on or a suite that includes Copilot.");
            if (skuId == Guid.Empty || string.IsNullOrWhiteSpace(partNumber)) throw new DomainException("Enter the SKU ID and part number from the Microsoft 365 admin center.");
            await uow.ExecuteAsync(
                """
                MERGE core.SeatSku AS t USING (SELECT @skuId AS SkuId) AS s ON t.SkuId = s.SkuId
                WHEN MATCHED THEN UPDATE SET SkuPartNumber = @part, Name = @name, Kind = @kind, IsActive = @isActive, Notes = @notes
                WHEN NOT MATCHED THEN INSERT (SkuId, SkuPartNumber, Name, Kind, IsActive, Notes) VALUES (@skuId, @part, @name, @kind, @isActive, @notes);
                """, new { skuId, part = CommandRunner.Trim(partNumber, 100), name = CommandRunner.Trim(string.IsNullOrWhiteSpace(name) ? partNumber : name, 200), kind, isActive, notes = CommandRunner.TrimN(notes, 1000) });
            await run.AuditAsync(uow, "seat.sku.save", "SeatSku", skuId, $"{partNumber} {kind}");
        });

    // ---------------------------------------------------------------- LOB seat admins

    /// <summary>
    /// Whether the acting person may name or end seat admins for this LOB: a seat admin or business owner of the same
    /// LOB, or the Platform Owner. Never another role, never another LOB.
    /// </summary>
    private async Task<(bool Allowed, bool IsOwner)> AdminRightsAsync(Uow uow, int fo)
    {
        var mine = (await MyRolesAsync(uow, fo)).ToHashSet();
        var owner = mine.Overlaps(OwnerRoles) || mine.Contains(Roles.PlatformOwner);
        return (owner || mine.Contains(SeatAccess.LobSeatAdmin), owner);
    }

    public Task<string?> AddSeatAdminAsync(Guid idem, Guid fundingOwnerPublicId, string personUpnOrId)
        => run.RunAsync(idem, "seat.admin.add", async uow =>
        {
            var fo = await FundingOwnerIdAsync(uow, fundingOwnerPublicId);
            var (allowed, _) = await AdminRightsAsync(uow, fo);
            if (!allowed) throw new DomainException("Only this LOB's seat admins, its business owner or the Platform Owner name seat admins for it.");
            var key = (personUpnOrId ?? "").Trim();
            var personId = int.TryParse(key, out var pid)
                ? await uow.ScalarAsync<int?>("SELECT PersonId FROM sec.Person WHERE PersonId = @pid AND AccountEnabled = 1", new { pid })
                : await uow.ScalarAsync<int?>("SELECT TOP (1) PersonId FROM sec.Person WHERE UserPrincipalName = @key AND AccountEnabled = 1", new { key });
            if (personId is not int target) throw new DomainException("Choose a person from the list: no active person has that sign-in name.");
            if (target == Me.PersonId) throw new DomainException("You cannot name yourself. Another seat admin, the business owner or the Platform Owner does it.");
            if (await uow.ScalarAsync<int>(
                    """
                    SELECT COUNT(*) FROM sec.RoleAssignment WHERE PersonId = @target AND RoleCode = 'LOB_SEAT_ADMIN' AND ScopeType = 'FUNDING_OWNER' AND ScopeId = @fo
                      AND (EffectiveTo IS NULL OR EffectiveTo >= @today)
                    """, new { target, fo, today = Today }) > 0)
                throw new DomainException("This person is already a seat admin for this LOB.");
            var id = await uow.ScalarAsync<Guid>(
                """
                INSERT sec.RoleAssignment (PersonId, RoleCode, ScopeType, ScopeId, EffectiveFrom, Source, AcceptedUtc, ReviewDueOn, GrantedByPersonId)
                OUTPUT inserted.PublicId VALUES (@target, 'LOB_SEAT_ADMIN', 'FUNDING_OWNER', @fo, @today, 'WORKBENCH', @now, @review, @me)
                """, new { target, fo, today = Today, now = clock.UtcNow, review = Today.AddYears(1), me = Me.PersonId });
            await run.AuditAsync(uow, "role.assign", "Person", target, $"LOB_SEAT_ADMIN FUNDING_OWNER {fo}");
            return id.ToString();
        });

    public Task EndSeatAdminAsync(Guid idem, Guid roleAssignmentPublicId)
        => run.RunAsync(idem, "seat.admin.end", async uow =>
        {
            var ra = await uow.QuerySingleOrDefaultAsync<(int Id, string RoleCode, string ScopeType, int? ScopeId, int PersonId)>(
                """
                SELECT RoleAssignmentId, RoleCode, ScopeType, ScopeId, PersonId FROM sec.RoleAssignment
                WHERE PublicId = @roleAssignmentPublicId AND (EffectiveTo IS NULL OR EffectiveTo >= @today)
                """, new { roleAssignmentPublicId, today = Today });
            if (ra.Id == 0) throw new DomainException("This assignment was not found or has already ended.");
            if (ra.RoleCode != SeatAccess.LobSeatAdmin || ra.ScopeType != ScopeTypes.FundingOwner || ra.ScopeId is not int fo)
                throw new DomainException("Seat admins can only end LOB seat admin assignments.");
            var (allowed, owner) = await AdminRightsAsync(uow, fo);
            if (!allowed) throw new DomainException("Only this LOB's seat admins, its business owner or the Platform Owner end seat admins for it.");
            var others = await uow.ScalarAsync<int>(
                """
                SELECT COUNT(*) FROM sec.RoleAssignment WHERE RoleCode = 'LOB_SEAT_ADMIN' AND ScopeType = 'FUNDING_OWNER' AND ScopeId = @fo
                  AND RoleAssignmentId <> @Id AND EffectiveFrom <= @today AND (EffectiveTo IS NULL OR EffectiveTo >= @today)
                """, new { fo, ra.Id, today = Today });
            if (others == 0 && !owner)
                throw new DomainException("This is the LOB's last seat admin. Name another seat admin first, or ask the business owner or the Platform Owner.");
            await uow.ExecuteAsync("UPDATE sec.RoleAssignment SET EffectiveTo = @yesterday WHERE RoleAssignmentId = @Id",
                new { yesterday = Today.AddDays(-1), ra.Id });
            await run.AuditAsync(uow, "role.end", "RoleAssignment", roleAssignmentPublicId, $"LOB_SEAT_ADMIN {fo} person {ra.PersonId}");
        });
}
