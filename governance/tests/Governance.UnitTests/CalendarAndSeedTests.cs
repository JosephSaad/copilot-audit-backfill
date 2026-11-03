using Governance.Application.Sla;
using Governance.Domain;
using Governance.Seed;

namespace Governance.UnitTests;

public class BusinessCalendarTests
{
    private static readonly TimeZoneInfo Eastern = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");

    private static BusinessCalendar Cal(params DateOnly[] holidays) => new(Eastern, holidays);
    private static DateTime Utc(int y, int m, int d, int h, int min = 0) => TimeZoneInfo.ConvertTimeToUtc(new DateTime(y, m, d, h, min, 0, DateTimeKind.Unspecified), Eastern);
    private static DateTime Local(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(utc, Eastern);

    [Fact]
    public void Friday_plus_one_business_day_is_Monday_same_time()
    {
        var due = Cal().AddBusinessDays(Utc(2026, 10, 23, 10), 1);   // Friday 10:00
        Assert.Equal(new DateTime(2026, 10, 26, 10, 0, 0), Local(due));
    }

    [Fact]
    public void Holidays_are_skipped()
    {
        var due = Cal(new DateOnly(2026, 10, 30)).AddBusinessDays(Utc(2026, 10, 29, 9), 1);   // Thursday, Friday is a holiday
        Assert.Equal(new DateTime(2026, 11, 2, 9, 0, 0), Local(due));
    }

    [Fact]
    public void Weekend_start_counts_from_Monday_morning()
    {
        var due = Cal().AddBusinessDays(Utc(2026, 10, 24, 15), 1);   // Saturday
        Assert.Equal(new DateTime(2026, 10, 27, 9, 0, 0), Local(due));
    }

    [Fact]
    public void Daylight_saving_change_keeps_local_time()
    {
        // DST ends Sunday 1 November 2026: 10:00 EDT is 14:00 UTC, 10:00 EST is 15:00 UTC.
        var start = Utc(2026, 10, 30, 10);
        var due = Cal().AddBusinessDays(start, 1);
        Assert.Equal(14, start.Hour);
        Assert.Equal(new DateTime(2026, 11, 2, 15, 0, 0), due);
    }

    [Fact]
    public void Clock_sla_adds_hours_with_reminders_at_50_and_80_percent()
    {
        var start = new DateTime(2026, 10, 24, 22, 0, 0, DateTimeKind.Utc);
        var t = Cal().Compute(new SlaPolicy { Duration = 4, Unit = "H", Calendar = "CLOCK", RemindAtPct1 = 50, RemindAtPct2 = 80 }, start);
        Assert.Equal(start.AddHours(4), t.DueUtc);
        Assert.Equal(start.AddHours(2), t.Reminder1Utc);
        Assert.Equal(start.AddMinutes(192), t.Reminder2Utc);
    }

    [Fact]
    public void Business_sla_reminders_are_fractions_of_the_span()
    {
        var start = Utc(2026, 10, 26, 9);
        var t = Cal().Compute(new SlaPolicy { Duration = 2, Unit = "BD", Calendar = "BUSINESS", RemindAtPct1 = 50, RemindAtPct2 = 80 }, start);
        Assert.Equal(new DateTime(2026, 10, 28, 9, 0, 0), Local(t.DueUtc!.Value));
        Assert.Equal(start + (t.DueUtc.Value - start) / 2, t.Reminder1Utc);
    }

    [Fact]
    public void No_sla_has_no_due_time()
    {
        Assert.Null(Cal().Compute(null, DateTime.UtcNow).DueUtc);
        Assert.Null(Cal().Compute(new SlaPolicy { Duration = 0, Unit = "BD", Calendar = "BUSINESS" }, DateTime.UtcNow).DueUtc);
    }

    [Fact]
    public void Business_days_between_skips_weekends_and_holidays()
        => Assert.Equal(4, Cal(new DateOnly(2026, 10, 28)).BusinessDaysBetween(Utc(2026, 10, 23, 12), Utc(2026, 10, 30, 12)));

    [Fact]
    public void Unknown_time_zone_falls_back_to_utc() => Assert.Equal(TimeZoneInfo.Utc, BusinessCalendar.Create("Nowhere/Nothing", Array.Empty<DateOnly>()).Zone);
}

public class SeedDataTests
{
    [Theory]
    [InlineData(2026, 4, 3, "Good Friday")]
    [InlineData(2026, 5, 18, "Victoria Day")]
    [InlineData(2026, 7, 1, "Canada Day")]
    [InlineData(2026, 9, 7, "Labour Day")]
    [InlineData(2026, 10, 12, "Thanksgiving Day")]
    [InlineData(2026, 12, 25, "Christmas Day")]
    [InlineData(2026, 12, 28, "Boxing Day")]
    [InlineData(2027, 12, 27, "Christmas Day")]
    [InlineData(2027, 12, 28, "Boxing Day")]
    [InlineData(2027, 3, 26, "Good Friday")]
    public void Canadian_federal_holidays_with_observed_days(int y, int m, int d, string name)
        => Assert.Contains(DemoSeeder.Holidays(y), h => h.Date == new DateTime(y, m, d) && h.Name == name);

    [Fact]
    public void Holidays_never_fall_on_a_weekend()
    {
        foreach (var y in new[] { 2026, 2027, 2028 })
            Assert.All(DemoSeeder.Holidays(y), h => Assert.True(h.Date.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday), $"{h.Name} {h.Date:d}"));
    }

    [Fact]
    public void Personas_cover_every_role_with_unique_synthetic_mail()
    {
        var all = Personas.All();
        Assert.Equal(46, all.Count);
        Assert.Equal(all.Count, all.Select(p => Personas.MailFor(p.DisplayName)).Distinct().Count());
        Assert.All(all, p => Assert.EndsWith("@example.invalid", Personas.MailFor(p.DisplayName)));
        var roles = all.SelectMany(p => p.Roles.Select(r => r.Role)).ToHashSet();
        foreach (var r in new[] { Roles.PlatformOwner, Roles.Steering, Roles.IntakeAnalyst, Roles.PlatformOperator, Roles.Architect, Roles.RiskReviewer, Roles.FinOpsLead,
                     Roles.FinOpsAnalyst, Roles.CommercialApprover, Roles.SourcingAgent, Roles.LicensingOwner, Roles.Auditor, Roles.WorkbenchAdmin, Roles.LobBusinessOwner,
                     Roles.SharedServiceOwner, Roles.DelegatedApprover, Roles.LobFinancePartner, Roles.WorkloadSponsor, Roles.ProductOwner })
            Assert.Contains(r, roles);
    }

    [Fact]
    public void Two_delegated_approvers_per_funding_owner()
    {
        foreach (var lob in Personas.Lobs)
            Assert.Equal(2, Personas.All().Count(p => p.Roles.Any(r => r.Role == Roles.DelegatedApprover && r.Lob == lob.Code)));
    }
}
