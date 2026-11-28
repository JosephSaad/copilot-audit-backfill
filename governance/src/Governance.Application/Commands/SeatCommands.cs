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

    /// <summary>
    /// With the Workbench set to Copilot seats only (no request journey), the Platform Owner or FinOps sets an LOB's
    /// reservation straight on its page: from the date given, the row in effect ends the day before and the new figure
    /// starts, so the history and the monthly chargeback keep every earlier figure. The same date corrects that row.
    /// </summary>
    public Task<string?> SetReservationAsync(Guid idem, Guid fundingOwnerPublicId, int seats, DateTime effectiveFrom, string? notes)
        => run.RunAsync(idem, "seat.reservation.set", async uow =>
        {
            await RequirePlatformOwnerAsync(uow);
            if (!WorkbenchScope.SeatsOnly(await uow.ScalarAsync<string?>(WorkbenchScope.Sql)))
                throw new DomainException("With the whole Workbench, a reservation changes through a seat reservation request (J9), so the change is approved and recorded.");
            if (seats < 0) throw new DomainException("Seats cannot be negative.");
            var fo = await FundingOwnerIdAsync(uow, fundingOwnerPublicId);
            var from = effectiveFrom.Date;
            if (await uow.ScalarAsync<int>("SELECT COUNT(*) FROM core.SeatReservation WHERE FundingOwnerId = @fo AND EffectiveFrom > @from", new { fo, from }) > 0)
                throw new DomainException("A reservation starts after that date. Choose a date on or after the latest one in the history.");
            var cur = await uow.QuerySingleOrDefaultAsync<(int Id, int Seats, DateTime From)>(
                "SELECT TOP (1) SeatReservationId, Seats, EffectiveFrom FROM core.SeatReservation WHERE FundingOwnerId = @fo AND EffectiveFrom <= @from AND (EffectiveTo IS NULL OR EffectiveTo >= @from) ORDER BY EffectiveFrom DESC",
                new { fo, from });
            Guid id;
            if (cur.Id != 0 && cur.From == from)
            {
                id = await uow.ScalarAsync<Guid>(
                    "UPDATE core.SeatReservation SET Seats = @seats, ChangeKind = CASE WHEN ChangeKind = 'INITIAL' THEN 'INITIAL' ELSE 'CORRECTION' END, Notes = COALESCE(@notes, Notes) OUTPUT inserted.PublicId WHERE SeatReservationId = @Id",
                    new { seats, notes = CommandRunner.TrimN(notes, 1000), cur.Id });
            }
            else
            {
                if (cur.Id != 0) await uow.ExecuteAsync("UPDATE core.SeatReservation SET EffectiveTo = @to WHERE SeatReservationId = @Id", new { to = from.AddDays(-1), cur.Id });
                var kind = cur.Id == 0 ? "INITIAL" : seats > cur.Seats ? "INCREASE" : "CORRECTION";
                id = await uow.ScalarAsync<Guid>(
                    """
                    INSERT core.SeatReservation (FundingOwnerId, Seats, PreviousSeats, ChangeKind, EffectiveFrom, ApprovedByPersonId, Notes, CreatedByPersonId)
                    OUTPUT inserted.PublicId VALUES (@fo, @seats, @prev, @kind, @from, @me, @notes, @me)
                    """, new { fo, seats, prev = cur.Id == 0 ? (int?)null : cur.Seats, kind, from, me = Me.PersonId, notes = CommandRunner.TrimN(notes, 1000) });
            }
            await run.AuditAsync(uow, "seat.reservation.set", "SeatReservation", id, $"{seats} seats from {from:yyyy-MM-dd}{(cur.Id == 0 ? "" : $" (was {cur.Seats})")}");
            return id.ToString();
        });

    /// <summary>
    /// Copilot seats only: corrects one figure in the history: its seats, its start date or its source. The date stays
    /// between the figures before and after it; the one before then ends the day before the new date, and the one after
    /// shows these seats as what it replaced. The table keeps every earlier version (system-versioned), and the change is audited.
    /// </summary>
    public Task<string?> EditReservationAsync(Guid idem, Guid reservationPublicId, int seats, DateTime effectiveFrom, string? notes)
        => run.RunAsync(idem, "seat.reservation.edit", async uow =>
        {
            await RequirePlatformOwnerAsync(uow);
            if (!WorkbenchScope.SeatsOnly(await uow.ScalarAsync<string?>(WorkbenchScope.Sql)))
                throw new DomainException("With the whole Workbench, a reservation changes through a seat reservation request (J9), so the change is approved and recorded.");
            if (seats < 0) throw new DomainException("Seats cannot be negative.");
            var row = await uow.QuerySingleOrDefaultAsync<(int Id, int Fo, int Seats, DateTime From)>(
                "SELECT SeatReservationId, FundingOwnerId, Seats, EffectiveFrom FROM core.SeatReservation WHERE PublicId = @reservationPublicId", new { reservationPublicId });
            if (row.Id == 0) throw new DomainException("This reservation was not found.");
            var from = effectiveFrom.Date;
            var before = await uow.QuerySingleOrDefaultAsync<(int Id, DateTime From)>(
                "SELECT TOP (1) SeatReservationId, EffectiveFrom FROM core.SeatReservation WHERE FundingOwnerId = @Fo AND SeatReservationId <> @Id AND EffectiveFrom <= @From ORDER BY EffectiveFrom DESC, SeatReservationId DESC",
                new { row.Fo, row.Id, row.From });
            var after = await uow.QuerySingleOrDefaultAsync<(int Id, DateTime From)>(
                "SELECT TOP (1) SeatReservationId, EffectiveFrom FROM core.SeatReservation WHERE FundingOwnerId = @Fo AND SeatReservationId <> @Id AND EffectiveFrom >= @From ORDER BY EffectiveFrom, SeatReservationId",
                new { row.Fo, row.Id, row.From });
            if (before.Id != 0 && from <= before.From)
                throw new DomainException($"Choose a date after {before.From:d MMM yyyy}, when the figure before this one starts.");
            if (after.Id != 0 && from >= after.From)
                throw new DomainException($"Choose a date before {after.From:d MMM yyyy}, when the figure after this one starts.");
            await uow.ExecuteAsync(
                """
                UPDATE core.SeatReservation SET Seats = @seats, EffectiveFrom = @from, Notes = COALESCE(@notes, Notes),
                       ChangeKind = CASE WHEN @seats = Seats OR ChangeKind = 'INITIAL' THEN ChangeKind ELSE 'CORRECTION' END
                WHERE SeatReservationId = @Id
                """, new { seats, from, notes = CommandRunner.TrimN(notes, 1000), row.Id });
            if (before.Id != 0) await uow.ExecuteAsync("UPDATE core.SeatReservation SET EffectiveTo = @to WHERE SeatReservationId = @Id", new { to = from.AddDays(-1), before.Id });
            if (after.Id != 0) await uow.ExecuteAsync("UPDATE core.SeatReservation SET PreviousSeats = @seats WHERE SeatReservationId = @Id", new { seats, after.Id });
            await run.AuditAsync(uow, "seat.reservation.edit", "SeatReservation", reservationPublicId,
                $"{seats} seats from {from:yyyy-MM-dd} (was {row.Seats} from {row.From:yyyy-MM-dd})");
            return reservationPublicId.ToString();
        });

    /// <summary>
    /// Copilot seats only: the Platform Owner deletes one figure from the history. The figure before it then runs on to
    /// where the deleted one ended (to now, if it was the latest), and the one after shows the figure before as what it
    /// replaced. The deleted row stays in the table's version history, and the deletion is audited.
    /// </summary>
    public Task<string?> DeleteReservationAsync(Guid idem, Guid reservationPublicId)
        => run.RunAsync(idem, "seat.reservation.delete", async uow =>
        {
            await RequirePlatformOwnerAsync(uow);
            if (!WorkbenchScope.SeatsOnly(await uow.ScalarAsync<string?>(WorkbenchScope.Sql)))
                throw new DomainException("With the whole Workbench, a reservation changes through a seat reservation request (J9), so the change is approved and recorded.");
            var row = await uow.QuerySingleOrDefaultAsync<(int Id, int Fo, int Seats, DateTime From, DateTime? To)>(
                "SELECT SeatReservationId, FundingOwnerId, Seats, EffectiveFrom, EffectiveTo FROM core.SeatReservation WHERE PublicId = @reservationPublicId", new { reservationPublicId });
            if (row.Id == 0) throw new DomainException("This reservation was not found or was already deleted.");
            var before = await uow.QuerySingleOrDefaultAsync<(int Id, int Seats)>(
                "SELECT TOP (1) SeatReservationId, Seats FROM core.SeatReservation WHERE FundingOwnerId = @Fo AND SeatReservationId <> @Id AND EffectiveFrom <= @From ORDER BY EffectiveFrom DESC, SeatReservationId DESC",
                new { row.Fo, row.Id, row.From });
            var after = await uow.QuerySingleOrDefaultAsync<int>(
                "SELECT TOP (1) SeatReservationId FROM core.SeatReservation WHERE FundingOwnerId = @Fo AND SeatReservationId <> @Id AND EffectiveFrom >= @From ORDER BY EffectiveFrom, SeatReservationId",
                new { row.Fo, row.Id, row.From });
            await uow.ExecuteAsync("DELETE core.SeatReservation WHERE SeatReservationId = @Id", new { row.Id });
            // The open figure is unique per line of business: the one before reopens only once the deleted one is gone.
            if (before.Id != 0) await uow.ExecuteAsync("UPDATE core.SeatReservation SET EffectiveTo = @To WHERE SeatReservationId = @Id", new { row.To, before.Id });
            if (after != 0) await uow.ExecuteAsync("UPDATE core.SeatReservation SET PreviousSeats = @prev WHERE SeatReservationId = @after",
                new { prev = before.Id == 0 ? (int?)null : before.Seats, after });
            await run.AuditAsync(uow, "seat.reservation.delete", "SeatReservation", reservationPublicId,
                $"deleted {row.Seats} seats from {row.From:yyyy-MM-dd} to {(row.To is null ? "now" : row.To.Value.ToString("yyyy-MM-dd"))}");
            return reservationPublicId.ToString();
        });

    /// <summary>
    /// The hierarchy audit's mapping for one tracking group: the leader its members should report up to (by sign-in, as
    /// Copilot Insights' directory has it) and at which level under the top of the organization. Empty clears both.
    /// The Platform Owner sets it; each change is audited.
    /// </summary>
    public Task<string?> SetGroupMappingAsync(Guid idem, Guid groupPublicId, string? leaderUpn, int? level)
        => run.RunAsync(idem, "seat.group.mapping", async uow =>
        {
            await RequirePlatformOwnerAsync(uow);
            var upn = string.IsNullOrWhiteSpace(leaderUpn) ? null : leaderUpn.Trim();
            if (upn is not null && (upn.Length > 256 || !upn.Contains('@'))) throw new DomainException("The expected leader is a sign-in name, for example jane.doe@contoso.com.");
            if (upn is not null && level is not (>= 1 and <= 12)) throw new DomainException("Choose the level the expected leader sits at: 1 is the top of the organization, 2 reports to the top, and so on.");
            var n = await uow.ExecuteAsync(
                """
                UPDATE core.SeatGroup SET ExpectedLeaderUpn = @upn, ExpectedLevel = @level
                WHERE PublicId = @groupPublicId AND Purpose = 'LOB_TRACKING'
                """, new { upn, level = upn is null ? null : (byte?)level, groupPublicId });
            if (n == 0) throw new DomainException("Only an active LOB tracking group has a mapping.");
            await run.AuditAsync(uow, "seat.group.mapping", "SeatGroup", groupPublicId, upn is null ? "mapping cleared" : $"{upn} at level {level}");
            return groupPublicId.ToString();
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
            if (name.Length == 0) throw new DomainException("Enter the group's name as it shows in AD or Entra.");
            if (!Purposes.Contains(i.Purpose)) throw new DomainException("Choose what the group is for.");
            if (i.Purpose == SeatGroupPurposes.LobTracking && i.FundingOwnerId is null) throw new DomainException("A tracking group needs the LOB it tracks.");
            /* A line of business chosen with another purpose was dropped without a word: say so instead. */
            if (i.Purpose != SeatGroupPurposes.LobTracking && i.FundingOwnerId is not null)
                throw new DomainException("Only an LOB tracking group belongs to a line of business. Choose LOB tracking group as its purpose, or no line of business.");
            if (i.Source is not ("ON_PREM_AD" or "CLOUD")) throw new DomainException("Choose where the group comes from.");
            var fo = i.Purpose == SeatGroupPurposes.LobTracking ? i.FundingOwnerId : null;
            Guid? entraId = i.EntraGroupId;
            Guid? adId = null;
            if (publicId is Guid pid)
            {
                var old = await uow.QuerySingleOrDefaultAsync<(int Id, Guid? EntraGroupId, string DisplayName, Guid? AdObjectGuid)>(
                    "SELECT SeatGroupId, EntraGroupId, DisplayName, AdObjectGuid FROM core.SeatGroup WHERE PublicId = @pid AND EffectiveTo IS NULL", new { pid });
                if (old.Id == 0) throw new DomainException("This group mapping was changed or ended by someone else. Reload the page.");
                if (entraId is null && string.Equals(old.DisplayName, name, StringComparison.OrdinalIgnoreCase)) entraId = old.EntraGroupId;
                /* The same group under a changed purpose or LOB keeps its AD key, so its members count until the next read. */
                if (string.Equals(old.DisplayName, name, StringComparison.OrdinalIgnoreCase)) adId = old.AdObjectGuid;
                await uow.ExecuteAsync("UPDATE core.SeatGroup SET EffectiveTo = @yesterday WHERE SeatGroupId = @Id", new { yesterday = Today.AddDays(-1), old.Id });
            }
            if (fo is int lob && await uow.ScalarAsync<int>("SELECT COUNT(*) FROM core.SeatGroup WHERE FundingOwnerId = @lob AND Purpose = 'LOB_TRACKING' AND EffectiveTo IS NULL", new { lob }) > 0)
                throw new DomainException("This LOB already has a tracking group. End or change that one first.");
            if (await uow.ScalarAsync<int>("SELECT COUNT(*) FROM core.SeatGroup WHERE DisplayName = @name AND EffectiveTo IS NULL", new { name }) > 0)
                throw new DomainException("A group with this name is already mapped.");
            var id = await uow.ScalarAsync<Guid>(
                """
                INSERT core.SeatGroup (EntraGroupId, DisplayName, Purpose, FundingOwnerId, OimRoleName, Source, PurposeConfirmed, Notes, EffectiveFrom, AdObjectGuid)
                OUTPUT inserted.PublicId VALUES (@entraId, @name, @Purpose, @fo, @oim, @Source, @PurposeConfirmed, @notes, @today, @adId)
                """, new { entraId, adId, name, i.Purpose, fo, oim = CommandRunner.TrimN(i.OimRoleName, 300), i.Source, i.PurposeConfirmed, notes = CommandRunner.TrimN(i.Notes, 1000), today = Today });
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

    /// <summary>
    /// A seat setting changed on the Seat groups page (ref.Setting, Seats.*), checked by <see cref="SeatSettings.Check"/>:
    /// by a Workbench administrator, the Platform Owner or FinOps, on Finance's word for the values Finance owns. Audited
    /// with the old and new value.
    /// </summary>
    public Task SaveSettingAsync(Guid idem, string key, string? value)
        => run.RunAsync(idem, "seat.setting.save", async uow =>
        {
            if (!Me.Can(Permissions.AdminConfigure) && !Me.Can(Permissions.RegistryEdit) && !Me.Can(Permissions.RegistryFinance))
                throw new DomainException("A Workbench administrator, the Platform Owner or FinOps changes the seat settings.");
            var v = Seats.SeatSettings.Check(key, value);
            if (key == "Seats.NoLobChargedTo" && v.Length > 0
                && await uow.ScalarAsync<int>("SELECT COUNT(*) FROM core.FundingOwner WHERE Code = @v AND Status IN ('ACTIVE','SUSPENDED')", new { v }) == 0)
                throw new DomainException($"No active funding owner has the code {v}.");
            var old = await uow.ScalarAsync<string?>("SELECT SettingValue FROM ref.Setting WHERE SettingKey = @key", new { key })
                ?? throw new DomainException("That setting is not a seat setting.");
            await uow.ExecuteAsync("UPDATE ref.Setting SET SettingValue = @v, UpdatedUtc = SYSUTCDATETIME(), UpdatedByPersonId = @me WHERE SettingKey = @key",
                new { v, key, me = Me.PersonId });
            await run.AuditAsync(uow, "seat.setting.save", "Setting", null, $"{key}: {old} -> {v}");
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

    /// <summary>Reservation figures (set and corrected on the seats page) are the Platform Owner's alone, read from the database.</summary>
    private async Task RequirePlatformOwnerAsync(Uow uow)
    {
        if (!(await uow.QueryAsync<string>(
                "SELECT RoleCode FROM sec.RoleAssignment WHERE PersonId = @me AND RoleCode = 'PLATFORM_OWNER' AND ScopeType = 'TENANT' AND EffectiveFrom <= @today AND (EffectiveTo IS NULL OR EffectiveTo >= @today)",
                new { me = Me.PersonId, today = Today })).Any())
            throw new DomainException("Only the Platform Owner sets or corrects a line of business's reservation.");
    }

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

    /// <summary>
    /// Names (or clears) a line of business's business owner after it was added: the owner role moves to the new person
    /// from today and the previous owner's ends. The Platform Owner or FinOps does it; nobody names themselves.
    /// </summary>
    public Task<string?> SetBusinessOwnerAsync(Guid idem, Guid fundingOwnerPublicId, string? personUpnOrId)
        => run.RunAsync(idem, "fundingowner.owner", async uow =>
        {
            run.RequireAny(Permissions.RegistryFinance, Permissions.RegistryEdit);
            var fo = await FundingOwnerIdAsync(uow, fundingOwnerPublicId);
            var key = (personUpnOrId ?? "").Trim();
            int? target = null;
            if (key.Length > 0)
            {
                target = int.TryParse(key, out var pid)
                    ? await uow.ScalarAsync<int?>("SELECT PersonId FROM sec.Person WHERE PersonId = @pid AND AccountEnabled = 1", new { pid })
                    : await uow.ScalarAsync<int?>("SELECT TOP (1) PersonId FROM sec.Person WHERE UserPrincipalName = @key AND AccountEnabled = 1", new { key });
                if (target is null) throw new DomainException("Choose a person from the list: no active person has that sign-in name.");
                if (target == Me.PersonId) throw new DomainException("You cannot name yourself. Another person with this permission names you.");
            }
            var ownerType = await uow.ScalarAsync<string>("SELECT OwnerType FROM core.FundingOwner WHERE FundingOwnerId = @fo", new { fo });
            var role = ownerType == "ENTERPRISE_SHARED" ? Roles.SharedServiceOwner : Roles.LobBusinessOwner;
            await uow.ExecuteAsync("UPDATE core.FundingOwner SET BusinessOwnerPersonId = @target WHERE FundingOwnerId = @fo", new { target, fo });
            await uow.ExecuteAsync(
                """
                UPDATE sec.RoleAssignment SET EffectiveTo = @yesterday
                WHERE ScopeType = 'FUNDING_OWNER' AND ScopeId = @fo AND RoleCode IN ('LOB_BUSINESS_OWNER','SHARED_SERVICE_OWNER')
                  AND (@target IS NULL OR PersonId <> @target) AND (EffectiveTo IS NULL OR EffectiveTo >= @today)
                """, new { yesterday = Today.AddDays(-1), today = Today, fo, target });
            if (target is int bo)
                await uow.ExecuteAsync(
                    """
                    IF NOT EXISTS (SELECT 1 FROM sec.RoleAssignment WHERE PersonId = @bo AND RoleCode = @role AND ScopeType = 'FUNDING_OWNER' AND ScopeId = @fo
                                     AND (EffectiveTo IS NULL OR EffectiveTo >= @today))
                        INSERT sec.RoleAssignment (PersonId, RoleCode, ScopeType, ScopeId, EffectiveFrom, Source, AcceptedUtc, ReviewDueOn, GrantedByPersonId)
                        VALUES (@bo, @role, 'FUNDING_OWNER', @fo, @today, 'WORKBENCH', @now, @review, @me)
                    """, new { bo, role, fo, today = Today, now = clock.UtcNow, review = Today.AddYears(1), me = Me.PersonId });
            await run.AuditAsync(uow, "fundingowner.owner", "FundingOwner", fo, target is null ? "business owner cleared" : $"business owner person {target}");
            return target?.ToString();
        });

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
