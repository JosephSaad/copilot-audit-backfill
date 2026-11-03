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
    public void Seats_no_line_of_business_claims_go_to_the_owner_Finance_named_or_stay_their_own_row()
    {
        IReadOnlyList<(int? FundingOwnerId, int ReservedSeats, int AssignedSeats)> rows = [(1, 10, 8), (2, 5, 7), (null, 0, 3)];
        Assert.Same(rows, SeatPolicy.ChargedRows(rows, null));
        var charged = SeatPolicy.ChargedRows(rows, 2);
        Assert.Equal([(1, 10, 8), (2, 5, 10)], charged.Select(r => (r.FundingOwnerId!.Value, r.ReservedSeats, r.AssignedSeats)));
        Assert.Same(rows, SeatPolicy.ChargedRows(rows, 99));   // a code that names no line of business with seats: unchanged
    }
}
