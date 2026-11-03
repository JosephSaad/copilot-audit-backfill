using Governance.Application.Seats;
using Xunit;

namespace Governance.UnitTests;

/// <summary>3.0.1: one purchase gate for the Workbench and Copilot Insights, and Finance's open seat choices as settings.</summary>
public class SeatRuleSetTests
{
    [Fact]
    public void The_purchase_gate_counts_as_the_policy_does()
    {
        var g = SeatPolicy.PurchaseGate(1136, 57, 95);
        Assert.Equal(57, g.Buffer);          // 95% of 1,136 is 1,079 to assign, leaving 57
        Assert.True(g.Reached);
        Assert.Equal(1079, g.Assigned);
        Assert.False(SeatPolicy.PurchaseGate(1136, 58, 95).Reached);
        Assert.False(SeatPolicy.PurchaseGate(0, 0, 95).Reached);   // no baseline, no gate
    }

    [Fact]
    public void The_gate_sentence_says_what_is_left_and_asks_for_a_baseline_when_none_is_set()
    {
        Assert.Contains("No purchase-gate baseline is set", SeatPolicy.PurchaseGateSentence(SeatSettings.Default, 1200, 1000));
        var s = SeatSettings.Default with { GateBaselineUnassigned = 200, PurchaseGatePercent = 90 };
        Assert.Contains("The gate is 90%: 180 more to assign", SeatPolicy.PurchaseGateSentence(s, 1200, 1000));   // 200 unassigned, buffer 20
        Assert.Contains("is reached", SeatPolicy.PurchaseGateSentence(s, 1200, 1185));
    }

    [Fact]
    public void Finances_choices_are_settings_with_the_defaults_named()
    {
        var d = SeatSettings.From(new Dictionary<string, string>());
        Assert.Equal("", d.NoLobChargedTo);
        Assert.Equal(95, d.PurchaseGatePercent);
        Assert.Equal(0, d.GateBaselineUnassigned);
        Assert.Equal("DECISION_DATE", d.IncreaseEffective);
        var set = SeatSettings.From(new Dictionary<string, string> { ["Seats.NoLobChargedTo"] = " ES-IT ", ["Seats.PurchaseGatePercent"] = "101", ["Seats.IncreaseEffective"] = "NEXT_MONTH" });
        Assert.Equal("ES-IT", set.NoLobChargedTo);
        Assert.Equal(95, set.PurchaseGatePercent);     // out of range: the default
        Assert.Equal("NEXT_MONTH", set.IncreaseEffective);
    }

    [Fact]
    public void Seats_no_line_of_business_claims_are_added_on_to_the_owner_Finance_named()
    {
        IReadOnlyList<(int? FundingOwnerId, int ReservedSeats, int AssignedSeats)> rows = [(1, 10, 8), (2, 5, 7), (null, 0, 3)];
        // None named: every row as it is, the no-LOB row its own.
        Assert.Equal(3, SeatPolicy.ChargeRows(rows, null).Count);
        // Named: on top of the owner's reservation, its assigned seats still its tracking group's.
        var charged = SeatPolicy.ChargeRows(rows, 1);
        Assert.Equal(2, charged.Count);
        var one = charged.Single(r => r.FundingOwnerId == 1);
        Assert.Equal((8, 3), (one.AssignedSeats, one.ExtraSeats));
        Assert.Equal((3, 13), SeatPolicy.Charge(one));     // 10 reserved + 0 held over + 3 no-LOB seats
        Assert.Equal((2, 7), SeatPolicy.Charge(charged.Single(r => r.FundingOwnerId == 2)));
        // Named but holding no seats and no reservation: a row of its own is added, so the seats are still charged.
        var added = SeatPolicy.ChargeRows(rows, 9);
        Assert.Equal((0, 0, 3), (added.Single(r => r.FundingOwnerId == 9).ReservedSeats, added.Single(r => r.FundingOwnerId == 9).AssignedSeats, added.Single(r => r.FundingOwnerId == 9).ExtraSeats));
        Assert.Equal(rows.Sum(r => r.AssignedSeats) - 3 + 3, added.Sum(r => r.AssignedSeats + r.ExtraSeats));   // nothing counted twice
    }

    [Fact]
    public void The_no_LOB_sentence_says_what_happens_and_names_the_setting_to_administrators_only()
    {
        var none = SeatSettings.Default;
        Assert.Contains("a row of their own, until Finance names who pays.", SeatPolicy.NoLobSentence(none, null));
        Assert.Contains("(Seats.NoLobChargedTo)", SeatPolicy.NoLobSentence(none, null, forAdmin: true));
        var named = none with { NoLobChargedTo = "ES-IT" };
        Assert.Equal("Seats no line of business claims are charged to Enterprise IT, as seats above its reservation.", SeatPolicy.NoLobSentence(named, "Enterprise IT"));
        Assert.Contains("is not an active one, so they stay a row of their own", SeatPolicy.NoLobSentence(named, null));
        Assert.DoesNotContain("ES-IT", SeatPolicy.NoLobSentence(named, null));
        Assert.DoesNotContain("Seats.", SeatPolicy.PurchaseGateSentence(none, 100, 50));
    }

    [Fact]
    public void A_baseline_is_read_as_an_administrator_writes_it()
    {
        Assert.Equal(1136, SeatSettings.From(new Dictionary<string, string> { ["Seats.GateBaselineUnassigned"] = "1,136" }).GateBaselineUnassigned);
        Assert.Equal(0, SeatSettings.From(new Dictionary<string, string> { ["Seats.GateBaselineUnassigned"] = "-5" }).GateBaselineUnassigned);
    }
}