using Governance.Application.Integrations;
using Governance.Application.Seats;
using Governance.Domain;

namespace Governance.UnitTests;

public class SeatPolicyTests
{
    private static readonly SeatSettings S = SeatSettings.Default;   // anniversary 1 April, 45-day window, NEXT decision date

    [Theory]
    [InlineData("2026-10-29", "2027-04-01")]
    [InlineData("2027-04-01", "2027-04-01")]   // the anniversary itself
    [InlineData("2027-04-02", "2028-04-01")]
    [InlineData("2026-12-31", "2027-04-01")]   // across the year boundary
    public void Next_anniversary(string day, string expected)
        => Assert.Equal(DateTime.Parse(expected), SeatPolicy.NextAnniversary(S, DateTime.Parse(day)));

    [Theory]
    [InlineData("2027-02-14", false)]   // 46 days before
    [InlineData("2027-02-15", true)]    // 45 days before
    [InlineData("2027-04-01", true)]
    [InlineData("2027-04-02", false)]   // the day after: the window is a year away
    [InlineData("2026-10-29", false)]
    public void True_down_window(string day, bool open)
        => Assert.Equal(open, SeatPolicy.IsTrueDownWindowOpen(S, DateTime.Parse(day)));

    [Fact]
    public void Window_of_an_anniversary_early_in_january_reaches_back_into_december()
    {
        var s = S with { AnniversaryMonth = 1, AnniversaryDay = 15, TrueDownWindowDays = 30 };
        Assert.True(SeatPolicy.IsTrueDownWindowOpen(s, new DateTime(2026, 12, 20)));
        Assert.Equal(new DateTime(2027, 1, 15), SeatPolicy.EffectiveDate(s, SeatChangeKinds.TrueDown, new DateTime(2026, 12, 20)));
        Assert.False(SeatPolicy.IsTrueDownWindowOpen(s, new DateTime(2026, 12, 15)));
    }

    [Fact]
    public void Effective_dates()
    {
        var day = new DateTime(2026, 10, 29, 15, 30, 0);
        Assert.Equal(new DateTime(2026, 10, 29), SeatPolicy.EffectiveDate(S, SeatChangeKinds.Increase, day));
        Assert.Equal(new DateTime(2026, 11, 1), SeatPolicy.EffectiveDate(S with { IncreaseEffective = "NEXT_MONTH" }, SeatChangeKinds.New, day));
        Assert.Equal(new DateTime(2027, 1, 1), SeatPolicy.EffectiveDate(S with { IncreaseEffective = "NEXT_MONTH" }, SeatChangeKinds.Increase, new DateTime(2026, 12, 31)));
        Assert.Equal(new DateTime(2027, 4, 1), SeatPolicy.EffectiveDate(S, SeatChangeKinds.TrueDown, day));
    }

    [Theory]
    [InlineData(null, 10, SeatChangeKinds.New)]
    [InlineData(0, 10, SeatChangeKinds.New)]
    [InlineData(10, 12, SeatChangeKinds.Increase)]
    [InlineData(10, 8, SeatChangeKinds.TrueDown)]
    [InlineData(10, 10, SeatChangeKinds.Correction)]
    public void Kind_of_a_change(int? current, int requested, string kind) => Assert.Equal(kind, SeatPolicy.KindOf(current, requested));

    [Fact]
    public void Charge_is_the_reservation_plus_seats_above_it()
    {
        Assert.Equal((0, 100), SeatPolicy.Charge(100, 80));    // FIN-05: the full reservation even when unused
        Assert.Equal((15, 115), SeatPolicy.Charge(100, 115));  // FIN-06: seats above it on top
        Assert.Equal((7, 7), SeatPolicy.Charge(0, 7));
        Assert.Equal(3450.00m, SeatPolicy.Amount(S, 115));
    }

    [Fact]
    public void Unallocated_subtracts_reserved_buffer_and_pending()
    {
        Assert.Equal(75, SeatPolicy.Unallocated(1000, 850, 25, 50));
        Assert.Equal(-10, SeatPolicy.Unallocated(900, 850, 25, 35));
    }

    [Fact]
    public void Snapshot_month_is_the_month_that_ended()
    {
        Assert.Equal(new DateTime(2026, 12, 1), SeatPolicy.SnapshotMonthFor(new DateTime(2027, 1, 3)));
        Assert.Equal(new DateTime(2026, 9, 1), SeatPolicy.SnapshotMonthFor(new DateTime(2026, 10, 29)));
    }

    [Fact]
    public void Settings_parse_and_fall_back()
    {
        var s = SeatSettings.From(new Dictionary<string, string>
        {
            ["Seats.EaAnniversary"] = "07-15", ["Seats.TrueDownWindowDays"] = "30", ["Seats.OperationalBufferSeats"] = "10",
            ["Seats.RatePerSeatMonth"] = "31.50", ["Seats.Currency"] = "cad", ["Seats.IncreaseEffective"] = "NEXT_MONTH",
            ["Seats.InactiveDays"] = "90", ["Seats.UsagePeriod"] = "D30",
        });
        Assert.Equal((7, 15, 30, 10, 31.50m, "CAD", "NEXT_MONTH", 90, "D30"),
            (s.AnniversaryMonth, s.AnniversaryDay, s.TrueDownWindowDays, s.BufferSeats, s.RatePerSeatMonth, s.Currency, s.IncreaseEffective, s.InactiveDays, s.UsagePeriod));

        var bad = SeatSettings.From(new Dictionary<string, string>
        {
            ["Seats.EaAnniversary"] = "13-40", ["Seats.TrueDownWindowDays"] = "-3", ["Seats.RatePerSeatMonth"] = "abc",
            ["Seats.Currency"] = "dollars", ["Seats.IncreaseEffective"] = "SOMETIME", ["Seats.UsagePeriod"] = "D365",
        });
        Assert.Equal(SeatSettings.Default, bad);
    }
}

public class SeatReadBackTests
{
    private static readonly Guid Sku = Guid.Parse("639dec6b-bb19-468b-871c-c5c441c4b0cb");
    private static readonly Guid Other = Guid.NewGuid();
    private static readonly Guid Group = Guid.NewGuid();

    [Fact]
    public void Holder_rows_one_per_source_for_the_sku_read()
    {
        var u = Guid.NewGuid();
        var users = new[]
        {
            new LicensedUserData(u, "a@x", "CA", new[]
            {
                new LicenseStateData(Sku, Group, "Active", null, null),
                new LicenseStateData(Sku, null, "Active", null, null),                 // also direct
                new LicenseStateData(Other, Group, "Active", null, null),              // another SKU: ignored
            }),
            new LicensedUserData(Guid.NewGuid(), "b@x", null, Array.Empty<LicenseStateData>()),   // no state: kept as direct
        };
        var rows = SeatSyncService.HolderRows(users, Sku).ToList();
        Assert.Equal(3, rows.Count);
        Assert.Contains(rows, r => r.MemberObjectId == u && r.AssignedByGroupId == Group);
        Assert.Contains(rows, r => r.MemberObjectId == u && r.AssignedByGroupId == Guid.Empty);
        Assert.All(rows, r => Assert.Equal(Sku, r.SkuId));
    }

    [Theory]
    [InlineData("CountViolation", "not enough licenses", "Buy seats")]
    [InlineData("DependencyViolation", "missing base license", "eligible Microsoft 365")]
    [InlineData("MutuallyExclusiveViolation", "conflicting plans", "conflicts")]
    [InlineData("ProhibitedInUsageLocationViolation", "usage location", "none set")]
    [InlineData("Other", "other license error", "Entra admin center")]
    public void License_errors_have_plain_fixes(string error, string label, string fixContains)
    {
        Assert.Equal(label, SeatFindingRules.LicenseErrorLabel(error));
        Assert.Contains(fixContains, SeatFindingRules.LicenseErrorFix(error, null));
    }

    [Fact]
    public void Known_add_on_skus_match_microsoft_learn()
    {
        Assert.Equal(Sku, SeatSyncService.KnownAddOns["Microsoft_365_Copilot"].SkuId);
        Assert.True(SeatSyncService.KnownAddOns.ContainsKey("m365_copilot"));
    }
}
