using Governance.Application.Data;
using Governance.Application.Requests;
using Governance.Application.Rules;
using Governance.Application.Seats;
using Governance.Domain;

namespace Governance.UnitTests;

/// <summary>J9: the S4 decision rules (CAL-04, CAL-06, Section 4.4) and B2 completeness for a seat change.</summary>
public class SeatRequestTests
{
    private static readonly DateTime Today = new(2026, 10, 29);

    private static SeatRequests.Facts Facts(int current, int requested, int? unallocated = 100, bool windowOpen = false, int assigned = 0, DateTime? future = null)
    {
        var kind = SeatPolicy.KindOf(current, requested);
        return new SeatRequests.Facts(1, current, requested, kind, assigned, unallocated is null ? null : 1000, 800, 25, 0, unallocated,
            windowOpen, new DateTime(2027, 2, 15), new DateTime(2027, 4, 1), kind == SeatChangeKinds.TrueDown ? new DateTime(2027, 4, 1) : Today, future);
    }

    [Fact]
    public void Increase_within_unallocated_needs_only_the_flex_pool_review()
    {
        Assert.Null(SeatRequests.CheckApproval(Facts(100, 150), "Checked 100 unallocated, buffer 25.", null));
        var ex = Assert.Throws<DomainException>(() => SeatRequests.CheckApproval(Facts(100, 150), " ", null));
        Assert.Contains("flex-pool review", ex.Message);
    }

    [Fact]
    public void Increase_above_unallocated_needs_conditions_and_adds_the_commercial_follow_up()
    {
        var f = Facts(100, 180, unallocated: 50);
        Assert.Equal(30, f.Shortfall);
        var ex = Assert.Throws<DomainException>(() => SeatRequests.CheckApproval(f, "Reviewed.", null));
        Assert.Contains("30 more seats", ex.Message);
        var text = SeatRequests.CheckApproval(f, "Reviewed.", "Buy 30 seats at the next order.");
        Assert.Contains("J8", text);
        Assert.StartsWith("Buy 30 seats", text);
    }

    [Fact]
    public void Unknown_inventory_does_not_block_an_increase()
        => Assert.Equal(0, Facts(100, 500, unallocated: null).Shortfall);

    [Fact]
    public void True_down_outside_the_window_is_refused_with_the_opening_date()
    {
        var ex = Assert.Throws<DomainException>(() => SeatRequests.CheckApproval(Facts(100, 60), "Reviewed.", null));
        Assert.Contains("15 February 2027", ex.Message);
        Assert.Contains("Hold", ex.Message);
    }

    [Fact]
    public void True_down_inside_the_window_is_allowed_and_warns_below_assigned()
    {
        var f = Facts(100, 60, windowOpen: true, assigned: 75);
        Assert.Null(SeatRequests.CheckApproval(f, "Reviewed with Digital Workplace.", null));
        Assert.True(f.BelowAssigned);
        Assert.Equal(0, f.Shortfall);
    }

    [Fact]
    public void A_change_already_dated_later_blocks_an_earlier_one()
    {
        var ex = Assert.Throws<DomainException>(() => SeatRequests.CheckApproval(Facts(100, 120, future: new DateTime(2027, 4, 1)), "Reviewed.", null));
        Assert.Contains("1 April 2027", ex.Message);
    }

    [Fact]
    public void Completeness_needs_requested_seats_and_demand()
    {
        var p = new RequestPayload { Purpose = "More seats", CurrentSeats = 100, RequestedSeats = 100 };
        var r = CompletenessCheck.Evaluate(new CompletenessCheck.Facts(Journeys.SeatReservation, p, 1, null, false, "ACTIVE", null, null, Array.Empty<string>()));
        Assert.False(r.Passed);
        Assert.Contains(r.Reasons, x => x.Contains("equal the current"));
        Assert.Contains(r.Reasons, x => x.Contains("who needs the seats"));
        p.RequestedSeats = 140; p.SeatDemand = "Claims team, 40 people from December.";
        Assert.True(CompletenessCheck.Evaluate(new CompletenessCheck.Facts(Journeys.SeatReservation, p, 1, null, false, "ACTIVE", null, null, Array.Empty<string>())).Passed);
    }
}
