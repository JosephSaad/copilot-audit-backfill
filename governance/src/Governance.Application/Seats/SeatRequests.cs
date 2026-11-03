using Governance.Application.Data;
using Governance.Application.Requests;
using Governance.Domain;

namespace Governance.Application.Seats;

/// <summary>
/// Journey J9 (change a Copilot seat reservation): who may ask, what the decider sees, what the decision must
/// record, and what an approval writes to core.SeatReservation. Seats only, never credits or money.
/// </summary>
public static class SeatRequests
{
    /// <summary>LOB roles that may ask to change their LOB's reservation.</summary>
    public static readonly IReadOnlyList<string> RequesterRoles = new[]
    {
        Roles.LobBusinessOwner, Roles.SharedServiceOwner, Roles.DelegatedApprover, Roles.LobFinancePartner, Roles.ProductOwner,
        "LOB_SEAT_ADMIN",   // LOB seat admins ask for changes for their LOB (decided 2026-10-29)
    };

    /// <summary>
    /// The figures the Finance decider weighs (Section 4.4) and the rules the decision must meet.
    /// Purchased and Unallocated are null until the seat read-back has run once.
    /// </summary>
    public sealed record Facts(
        int FundingOwnerId,
        int CurrentSeats,
        int RequestedSeats,
        string Kind,
        int AssignedSeats,
        int? Purchased,
        int ReservedTotal,
        int BufferSeats,
        int PendingIncrease,
        int? Unallocated,
        bool WindowOpen,
        DateTime WindowOpens,
        DateTime Anniversary,
        DateTime EffectiveOn,
        DateTime? FutureChangeOn)
    {
        public int Delta => RequestedSeats - CurrentSeats;
        public bool IsDecrease => Kind == SeatChangeKinds.TrueDown;
        /// <summary>Seats the increase needs beyond what is unallocated (0 when it fits or when unknown).</summary>
        public int Shortfall => !IsDecrease && Unallocated is int u && Delta > u ? Delta - Math.Max(0, u) : 0;
        /// <summary>A true-down below the seats already assigned leaves people licensed above the reservation (billed on top, FIN-06).</summary>
        public bool BelowAssigned => IsDecrease && RequestedSeats < AssignedSeats;
    }

    /// <summary>
    /// Checks the requester and fills what the form must not set: the reservation in effect and the kind of change.
    /// Called when a J9 draft is created or edited, and again at submission.
    /// </summary>
    public static async Task PrepareAsync(Uow uow, int personId, int fundingOwnerId, RequestPayload p, DateTime today)
    {
        var holds = await uow.ScalarAsync<int>(
            """
            SELECT COUNT(*) FROM sec.RoleAssignment
            WHERE PersonId = @personId AND RoleCode IN @RequesterRoles AND ScopeType = 'FUNDING_OWNER' AND ScopeId = @fundingOwnerId
              AND EffectiveFrom <= @today AND (EffectiveTo IS NULL OR EffectiveTo >= @today)
            """, new { personId, RequesterRoles, fundingOwnerId, today });
        if (holds == 0)
            throw new DomainException("Only the LOB's business owner, seat admin, delegated approver, finance partner or a product owner in that LOB can ask to change its Copilot seat reservation.");
        if (p.RequestedSeats is not int requested || requested < 0) throw new DomainException("Give the LOB's new total seat reservation (0 or more).");
        var current = await CurrentSeatsAsync(uow, fundingOwnerId, today);
        if (requested == (current ?? 0)) throw new DomainException($"The LOB already reserves {current ?? 0} seats: ask for a different total.");
        p.CurrentSeats = current ?? 0;
        p.SeatChangeKind = SeatPolicy.KindOf(current, requested);
        if (string.IsNullOrWhiteSpace(p.Purpose)) p.Purpose = p.SeatDemand;
    }

    public static Task<int?> CurrentSeatsAsync(Uow uow, int fundingOwnerId, DateTime day)
        => uow.ScalarAsync<int?>(
            """
            SELECT TOP (1) Seats FROM core.SeatReservation
            WHERE FundingOwnerId = @fundingOwnerId AND EffectiveFrom <= @day AND (EffectiveTo IS NULL OR EffectiveTo >= @day)
            ORDER BY EffectiveFrom DESC, SeatReservationId DESC
            """, new { fundingOwnerId, day = day.Date });

    /// <summary>The decision facts for a J9 request on <paramref name="today"/>.</summary>
    public static async Task<Facts> FactsAsync(Uow uow, RequestRow r, DateTime today)
    {
        var s = await SeatSettings.LoadAsync(uow);
        var p = RequestPayload.FromJson(r.PayloadJson);
        var day = today.Date;
        var current = await CurrentSeatsAsync(uow, r.FundingOwnerId, day) ?? 0;
        var requested = p.RequestedSeats ?? current;
        var kind = SeatPolicy.KindOf(current, requested);
        var assigned = await uow.ScalarAsync<int?>("SELECT AssignedSeats FROM rpt.fn_SeatPosition(@day) WHERE FundingOwnerId = @FundingOwnerId",
            new { day, r.FundingOwnerId }) ?? 0;
        var purchased = await uow.ScalarAsync<int?>(
            """
            SELECT SUM(x.EnabledUnits) FROM intg.SeatSkuReading x
            JOIN core.SeatSku k ON k.SkuId = x.SkuId AND k.Kind = 'COPILOT_ADDON' AND k.IsActive = 1
            WHERE x.SyncRunId = (SELECT MAX(SyncRunId) FROM intg.SeatSkuReading)
            """);
        var reservedTotal = await uow.ScalarAsync<int?>(
            """
            SELECT SUM(x.Seats) FROM core.SeatReservation x
            WHERE x.EffectiveFrom <= @day AND (x.EffectiveTo IS NULL OR x.EffectiveTo >= @day)
            """, new { day }) ?? 0;
        // Approved demand not yet in effect: future-dated increases, plus other open J9 requests asking for more.
        var pending = await uow.ScalarAsync<int?>(
            """
            SELECT SUM(d) FROM (
                SELECT CASE WHEN f.Seats > ISNULL(c.Seats, 0) THEN f.Seats - ISNULL(c.Seats, 0) ELSE 0 END AS d
                FROM core.SeatReservation f
                OUTER APPLY (SELECT TOP (1) Seats FROM core.SeatReservation c
                             WHERE c.FundingOwnerId = f.FundingOwnerId AND c.EffectiveFrom <= @day AND (c.EffectiveTo IS NULL OR c.EffectiveTo >= @day)
                             ORDER BY c.EffectiveFrom DESC, c.SeatReservationId DESC) c
                WHERE f.EffectiveFrom > @day
                UNION ALL
                SELECT CASE WHEN TRY_CAST(JSON_VALUE(q.PayloadJson, '$.requestedSeats') AS int) > ISNULL(TRY_CAST(JSON_VALUE(q.PayloadJson, '$.currentSeats') AS int), 0)
                            THEN TRY_CAST(JSON_VALUE(q.PayloadJson, '$.requestedSeats') AS int) - ISNULL(TRY_CAST(JSON_VALUE(q.PayloadJson, '$.currentSeats') AS int), 0) ELSE 0 END
                FROM wf.Request q
                WHERE q.JourneyCode = 'J9' AND q.RequestId <> @RequestId
                  AND q.State NOT IN ('DRAFT','COMPLETED','CANCELLED','CLOSED','DECLINED')
            ) x
            """, new { day, r.RequestId }) ?? 0;
        var future = await uow.ScalarAsync<DateTime?>(
            "SELECT MIN(EffectiveFrom) FROM core.SeatReservation WHERE FundingOwnerId = @FundingOwnerId AND EffectiveFrom > @day", new { r.FundingOwnerId, day });
        return new Facts(r.FundingOwnerId, current, requested, kind, assigned, purchased, reservedTotal, s.BufferSeats, pending,
            purchased is int pu ? SeatPolicy.Unallocated(pu, reservedTotal, s.BufferSeats, pending) : null,
            SeatPolicy.IsTrueDownWindowOpen(s, day), SeatPolicy.TrueDownWindowOpens(s, day), SeatPolicy.NextAnniversary(s, day),
            SeatPolicy.EffectiveDate(s, kind, day), future);
    }

    /// <summary>
    /// The S4 rules for a positive decision (CAL-04, CAL-06, Section 4.4). Returns the conditions text to record,
    /// with the commercial follow-up added when the increase goes beyond the unallocated seats.
    /// </summary>
    public static string? CheckApproval(Facts f, string? flexPoolReview, string? conditionsText)
    {
        if (f.Delta == 0) throw new DomainException("The reservation already equals the requested seats: decline or let the requester withdraw.");
        if (f.IsDecrease && !f.WindowOpen)
            throw new DomainException($"A decrease can be approved only from {f.WindowOpens:d MMMM yyyy} up to the EA anniversary on {f.Anniversary:d MMMM yyyy}. "
                                      + "Hold it until then (the reservation does not change while it is held), or decline it.");
        if (f.FutureChangeOn is DateTime later && later > f.EffectiveOn.Date)
            throw new DomainException($"An approved change for this LOB already takes effect on {later:d MMMM yyyy}. Hold this request and decide it after that date.");
        if (string.IsNullOrWhiteSpace(flexPoolReview))
            throw new DomainException("Record the flex-pool review: unallocated seats, approved pending demand and the operational buffer you checked (CAL-06).");
        if (f.Shortfall > 0)
        {
            if (string.IsNullOrWhiteSpace(conditionsText))
                throw new DomainException($"This increase needs {f.Shortfall:N0} more seats than are unallocated. State the purchase need in the conditions to approve it anyway.");
            return conditionsText.Trim() + $" Above the unallocated seats by {f.Shortfall:N0}: buying them needs a commercial decision (J8).";
        }
        return conditionsText;
    }

    /// <summary>
    /// An approved change ends the LOB's open reservation the day before it takes effect and starts the new one
    /// (CAL-05, CAL-06). A true-down takes effect at the EA anniversary, so the current row keeps running until then.
    /// Idempotent: a request writes one reservation row.
    /// </summary>
    public static async Task ApplyAsync(Uow uow, RequestRow r, DateTime today)
    {
        if (await uow.ScalarAsync<int>("SELECT COUNT(*) FROM core.SeatReservation WHERE RequestId = @RequestId", new { r.RequestId }) > 0) return;
        var p = RequestPayload.FromJson(r.PayloadJson);
        if (p.RequestedSeats is not int requested) throw new DomainException("The request has no requested seats.");
        var s = await SeatSettings.LoadAsync(uow);
        var decision = await uow.QuerySingleAsync<(long DecisionId, int DeciderPersonId)>(
            "SELECT TOP (1) DecisionId, DeciderPersonId FROM wf.Decision WHERE RequestId = @RequestId AND StageCode = 'S4' ORDER BY DecisionId DESC", new { r.RequestId });
        var open = await uow.QueryFirstOrDefaultAsync<(int SeatReservationId, int Seats, DateTime EffectiveFrom)>(
            "SELECT SeatReservationId, Seats, EffectiveFrom FROM core.SeatReservation WITH (UPDLOCK, HOLDLOCK) WHERE FundingOwnerId = @FundingOwnerId AND EffectiveTo IS NULL",
            new { r.FundingOwnerId });
        var kind = SeatPolicy.KindOf(open.SeatReservationId == 0 ? null : open.Seats, requested);
        var effective = SeatPolicy.EffectiveDate(s, kind, today);
        var flex = p.FlexPoolReview is null ? null : p.FlexPoolReview.Length > 1000 ? p.FlexPoolReview[..1000] : p.FlexPoolReview;
        var notes = $"{r.RequestNumber}: {r.Title}";
        if (notes.Length > 1000) notes = notes[..1000];
        if (open.SeatReservationId != 0)
        {
            if (open.EffectiveFrom.Date > effective)
                throw new DomainException($"An approved change for this LOB already takes effect on {open.EffectiveFrom:d MMMM yyyy}. Hold this request and decide it after that date.");
            if (open.EffectiveFrom.Date == effective)
            {
                // A second change taking effect the same day replaces that day's row, so two rows never overlap;
                // the replaced version stays in the temporal history (core.SeatReservationHistory).
                await uow.ExecuteAsync(
                    """
                    UPDATE core.SeatReservation SET Seats = @requested, PreviousSeats = Seats, ChangeKind = @kind, RequestId = @RequestId, DecisionId = @DecisionId,
                           ApprovedByPersonId = @DeciderPersonId, FlexPoolReview = @flex, Notes = @notes, CreatedByPersonId = @DeciderPersonId
                    WHERE SeatReservationId = @SeatReservationId
                    """, new { requested, kind, r.RequestId, decision.DecisionId, decision.DeciderPersonId, flex, notes, open.SeatReservationId });
                return;
            }
            await uow.ExecuteAsync("UPDATE core.SeatReservation SET EffectiveTo = @end WHERE SeatReservationId = @SeatReservationId",
                new { end = effective.AddDays(-1), open.SeatReservationId });
        }
        await uow.ExecuteAsync(
            """
            INSERT core.SeatReservation (FundingOwnerId, Seats, PreviousSeats, ChangeKind, EffectiveFrom, RequestId, DecisionId, ApprovedByPersonId, FlexPoolReview, Notes, CreatedByPersonId)
            VALUES (@FundingOwnerId, @requested, @previous, @kind, @effective, @RequestId, @DecisionId, @DeciderPersonId, @flex, @notes, @DeciderPersonId)
            """,
            new
            {
                r.FundingOwnerId, requested, previous = open.SeatReservationId == 0 ? (int?)null : open.Seats, kind, effective, r.RequestId,
                decision.DecisionId, decision.DeciderPersonId, flex, notes,
            });
    }
}
