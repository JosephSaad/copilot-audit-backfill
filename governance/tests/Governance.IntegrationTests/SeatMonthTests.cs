using Dapper;
using Governance.Application;
using Governance.Application.Commands;
using Governance.Application.Data;
using Governance.Application.Outbox;
using Governance.Application.Seats;
using Governance.Domain;

namespace Governance.IntegrationTests;

/// <summary>
/// Seats month by month: the days a seat read records, a month's peak, charge, overage and cumulative overage, the
/// certification that freezes it, an adjustment when a reservation changes inside a certified month, and reopening.
/// </summary>
public sealed class SeatMonthTests(SeededFixture fx) : IClassFixture<SeededFixture>
{
    private sealed class FixedClock(DateTime now) : IClock { public DateTime UtcNow { get; } = now; }

    private sealed class Po(int personId) : ICurrentPrincipal
    {
        public bool IsAuthenticated => true;
        public int PersonId { get; } = personId;
        public int? RealPersonId => null;
        public string DisplayName => "platform owner";
        public bool IsCentral => true;
        public IReadOnlyList<RoleGrant> Roles { get; } = [new RoleGrant(Governance.Domain.Roles.PlatformOwner, "TENANT", null)];
        public IReadOnlySet<string> AppRoles { get; } = new HashSet<string>();
        public IReadOnlySet<string> Permissions { get; } = new HashSet<string> { Governance.Domain.Permissions.RegistryEdit };
        public IReadOnlyDictionary<int, IReadOnlySet<string>> ScopedPermissions { get; } = new Dictionary<int, IReadOnlySet<string>>();
    }

    [Fact]
    public async Task A_month_charges_the_peak_is_certified_frozen_adjusted_and_reopened()
    {
        var clock = new FixedClock(new DateTime(2027, 1, 15, 15, 0, 0, DateTimeKind.Utc));
        var po = fx.Seed.As("platformOwner");
        var db = fx.Host.DbFor(po);
        var cmd = new SeatCommands(new CommandRunner(db, new Po(po.PersonId), new AuditWriter(clock)), clock, new OutboxWriter());
        using var c = fx.Admin();
        var keep = c.Query<(string Key, string Value)>("SELECT SettingKey, SettingValue FROM ref.Setting WHERE SettingKey IN ('Seats.TrackingStart','Workbench.Scope','Seats.RatePerSeatMonth')").ToList();
        var fo = c.ExecuteScalar<int>(
            """
            INSERT core.FundingOwner (Code, Name, OwnerType, Status, EffectiveFrom) OUTPUT inserted.FundingOwnerId VALUES ('TSTMONTH', 'Month test line', 'LOB', 'ACTIVE', '2026-01-01');
            """);
        var key = SeatMonths.LobKey(fo);
        try
        {
            c.Execute("""
                UPDATE ref.Setting SET SettingValue = '2026-11-01' WHERE SettingKey = 'Seats.TrackingStart';
                UPDATE ref.Setting SET SettingValue = 'SEATS' WHERE SettingKey = 'Workbench.Scope';
                UPDATE ref.Setting SET SettingValue = '30.00' WHERE SettingKey = 'Seats.RatePerSeatMonth';
                DELETE intg.SeatDay WHERE Day BETWEEN '2026-11-01' AND '2027-01-31';
                """);
            c.Execute("DELETE core.SeatPeriod; INSERT core.SeatPeriod (Name, StartMonth, EndMonth) VALUES (N'Test Nov', '2026-11-01', '2026-11-01'), (N'Test Dec', '2026-12-01', '2026-12-01');");
            c.Execute("INSERT core.SeatReservation (FundingOwnerId, Seats, ChangeKind, EffectiveFrom) VALUES (@fo, 50, 'INITIAL', '2026-10-01')", new { fo });
            // November: 40 seats a day, 60 on the 15th; December: 45 every day. Every day measured.
            for (var d = new DateTime(2026, 11, 1); d <= new DateTime(2026, 12, 31); d = d.AddDays(1))
            {
                var seats = d.Month == 11 ? (d.Day == 15 ? 60 : 40) : 45;
                c.Execute("INSERT intg.SeatDay (Day, RowKey, FundingOwnerId, Members, Seats, Reserved) VALUES (@d, @key, @fo, @seats, @seats, 50)", new { d, key, fo, seats });
                c.Execute("IF NOT EXISTS (SELECT 1 FROM intg.SeatDay WHERE Day = @d AND RowKey = 'TENANT') INSERT intg.SeatDay (Day, RowKey, Members, Seats, Purchased) VALUES (@d, 'TENANT', 0, 1000, 1200)", new { d });
            }
            var settings = await db.InTransactionAsync(SeatSettings.LoadAsync);

            var nov = await db.InTransactionAsync(u => SeatMonths.MonthAsync(u, new DateTime(2026, 11, 1), clock.UtcNow, settings));
            var line = nov.Lines.Single(l => l.FundingOwnerId == fo);
            Assert.Equal((60, 40, 40, 30), (line.PeakSeats, line.LowSeats, line.MonthEndSeats, line.DaysMeasured));
            Assert.Equal((60, 1800m, 300m), (line.ChargeSeats, line.Charge, line.Overage));   // the peak above the reservation of 50
            Assert.Equal(300m, line.CumulativeOverage);
            Assert.Equal((30, 30), (nov.DaysMeasured, nov.DaysExpected));

            var dec = await db.InTransactionAsync(u => SeatMonths.MonthAsync(u, new DateTime(2026, 12, 1), clock.UtcNow, settings));
            var decLine = dec.Lines.Single(l => l.FundingOwnerId == fo);
            Assert.Equal((50, 1500m, 0m, 0m), (decLine.ChargeSeats, decLine.Charge, decLine.Overage, decLine.CumulativeOverage));   // a new period from December
            c.Execute("DELETE core.SeatPeriod; INSERT core.SeatPeriod (Name, StartMonth, EndMonth) VALUES (N'Test Nov-Dec', '2026-11-01', '2026-12-01');");
            dec = await db.InTransactionAsync(u => SeatMonths.MonthAsync(u, new DateTime(2026, 12, 1), clock.UtcNow, settings));
            Assert.Equal(300m, dec.Lines.Single(l => l.FundingOwnerId == fo).CumulativeOverage);   // November and December together

            // Certified: frozen. A reservation change dated inside it becomes an adjustment in the open month (January).
            await cmd.CertifySeatMonthAsync(Guid.NewGuid(), new DateTime(2026, 11, 1), null, "{}", "[]");
            await Assert.ThrowsAsync<DomainException>(() => cmd.CertifySeatMonthAsync(Guid.NewGuid(), new DateTime(2026, 11, 1), null, null, null));
            await Assert.ThrowsAsync<DomainException>(() => cmd.CertifySeatMonthAsync(Guid.NewGuid(), new DateTime(2027, 1, 1), null, null, null));   // not ended
            var frozen = await db.InTransactionAsync(u => SeatMonths.MonthAsync(u, new DateTime(2026, 11, 1), clock.UtcNow, settings));
            Assert.True(frozen.Certified);
            Assert.Equal(1800m, frozen.Lines.Single(l => l.FundingOwnerId == fo).Charge);

            var reservation = c.QuerySingle<Guid>("SELECT PublicId FROM core.SeatReservation WHERE FundingOwnerId = @fo", new { fo });
            await cmd.EditReservationAsync(Guid.NewGuid(), reservation, 70, new DateTime(2026, 10, 1), null);
            var adj = c.Query<(DateTime PeriodMonth, DateTime SourceMonth, decimal Amount)>("SELECT PeriodMonth, SourceMonth, Amount FROM core.SeatAdjustment WHERE FundingOwnerId = @fo", new { fo }).Single();
            Assert.Equal((new DateTime(2027, 1, 1), new DateTime(2026, 11, 1), 300m), adj);   // 70 x 30 = 2,100 against 1,800 certified
            await cmd.EditReservationAsync(Guid.NewGuid(), reservation, 70, new DateTime(2026, 10, 1), "same again");
            Assert.Equal(1, c.ExecuteScalar<int>("SELECT COUNT(*) FROM core.SeatAdjustment WHERE FundingOwnerId = @fo", new { fo }));   // nothing left to adjust
            var jan = await db.InTransactionAsync(u => SeatMonths.MonthAsync(u, new DateTime(2027, 1, 1), clock.UtcNow, settings));
            Assert.Equal(300m, jan.Lines.Single(l => l.FundingOwnerId == fo).Adjustments);

            // Periods never overlap, and one with a certified month keeps it.
            var period = c.ExecuteScalar<int>("SELECT SeatPeriodId FROM core.SeatPeriod WHERE Name = N'Test Nov-Dec'");
            await Assert.ThrowsAsync<DomainException>(() => cmd.SavePeriodAsync(Guid.NewGuid(), null, "Overlap", new DateTime(2026, 12, 1), new DateTime(2027, 3, 1)));
            await Assert.ThrowsAsync<DomainException>(() => cmd.SavePeriodAsync(Guid.NewGuid(), period, "Test Nov-Dec", new DateTime(2026, 12, 1), new DateTime(2026, 12, 1)));   // would leave certified November out
            await Assert.ThrowsAsync<DomainException>(() => cmd.DeletePeriodAsync(Guid.NewGuid(), period));
            await cmd.SavePeriodAsync(Guid.NewGuid(), null, "Test 2027", new DateTime(2027, 1, 1), new DateTime(2027, 12, 1));

            // Reopened by the Platform Owner, with a reason: built from its days again.
            await Assert.ThrowsAsync<DomainException>(() => cmd.ReopenSeatMonthAsync(Guid.NewGuid(), new DateTime(2026, 11, 1), ""));
            await cmd.ReopenSeatMonthAsync(Guid.NewGuid(), new DateTime(2026, 11, 1), "Finance asked for a recount");
            Assert.Equal(0, c.ExecuteScalar<int>("SELECT COUNT(*) FROM core.SeatMonthCertification WHERE PeriodMonth = '2026-11-01'"));
        }
        finally
        {
            c.Execute("DELETE core.SeatAdjustment WHERE FundingOwnerId = @fo; DELETE core.SeatMonth WHERE PeriodMonth BETWEEN '2026-11-01' AND '2027-01-01'; DELETE core.SeatMonthCertification WHERE PeriodMonth BETWEEN '2026-11-01' AND '2027-01-01';", new { fo });
            c.Execute("DELETE intg.SeatDay WHERE Day BETWEEN '2026-11-01' AND '2027-01-31'; DELETE core.SeatReservation WHERE FundingOwnerId = @fo; DELETE core.FundingOwner WHERE FundingOwnerId = @fo;", new { fo });
            foreach (var (k, v) in keep) c.Execute("UPDATE ref.Setting SET SettingValue = @v WHERE SettingKey = @k", new { k, v });
            c.Execute("DELETE core.SeatPeriod WHERE Name LIKE N'Test %'; IF NOT EXISTS (SELECT 1 FROM core.SeatPeriod) INSERT core.SeatPeriod (Name, StartMonth, EndMonth) VALUES (N'Period 1', '2026-12-01', '2026-12-01');");
        }
    }

    [Fact]
    public async Task A_seat_read_records_the_day_once_keeping_its_highest_seats()
    {
        var clock = new FixedClock(new DateTime(2027, 2, 10, 15, 0, 0, DateTimeKind.Utc));
        var db = fx.Host.DbFor(fx.Seed.As("platformOwner"));
        using var c = fx.Admin();
        var start = c.ExecuteScalar<string>("SELECT SettingValue FROM ref.Setting WHERE SettingKey = 'Seats.TrackingStart'");
        try
        {
            c.Execute("UPDATE ref.Setting SET SettingValue = '2027-02-01' WHERE SettingKey = 'Seats.TrackingStart'");
            await db.InTransactionAsync(u => SeatMonths.RecordDayAsync(u, 1, clock.UtcNow, 60));
            await db.InTransactionAsync(u => SeatMonths.RecordDayAsync(u, 2, clock.UtcNow, 60));
            Assert.Equal(2, c.ExecuteScalar<int>("SELECT Reads FROM intg.SeatDay WHERE Day = '2027-02-10' AND RowKey = 'TENANT'"));
            Assert.True(c.ExecuteScalar<int>("SELECT COUNT(*) FROM intg.SeatDay WHERE Day = '2027-02-10' AND RowKey LIKE 'FO:%'") > 0);
            // Before the tracking start nothing is recorded.
            c.Execute("UPDATE ref.Setting SET SettingValue = '2027-03-01' WHERE SettingKey = 'Seats.TrackingStart'");
            Assert.Equal(0, await db.InTransactionAsync(u => SeatMonths.RecordDayAsync(u, 3, clock.UtcNow, 60)));
        }
        finally
        {
            c.Execute("DELETE intg.SeatDay WHERE Day = '2027-02-10'");
            c.Execute("UPDATE ref.Setting SET SettingValue = @start WHERE SettingKey = 'Seats.TrackingStart'", new { start });
        }
    }
}
