using Dapper;
using Governance.Application;
using Governance.Application.Data;
using Governance.Application.Integrations;
using Governance.Application.Outbox;
using Governance.Application.Seats;
using Governance.Domain;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Governance.IntegrationTests;

/// <summary>
/// The X13 seat read end to end against a throwaway database, as the worker's SQL login: groups resolved by name,
/// holders and usage written, seat position per LOB, every finding type, reclaim proposals, and the monthly draft
/// chargeback (a certified month never changes).
/// </summary>
[Collection("sql")]
public sealed class SeatSyncTests(DatabaseFixture db) : IClassFixture<DatabaseFixture>
{
    private static readonly Guid AddOn = Guid.Parse("639dec6b-bb19-468b-871c-c5c441c4b0cb");
    private static readonly Guid Suite = Guid.Parse("18a4bd3f-0b5b-4887-b04f-61dd0ee15f5e");   // stands in for a suite that includes Copilot
    private static readonly Guid Lic = G(0xA1), TrackA = G(0xA2), TrackB = G(0xA3), Excl = G(0xA4);
    private static Guid G(int n) => Guid.Parse($"00000000-0000-0000-0000-{n:x12}");

    // People: 1-3 in A, 4 in A and B, 5 no LOB (direct), 6 in A with CountViolation, 7 leaver in A,
    // 8 in B without a seat, 9 in B with the suite too, 10 in B and the exclusion group.
    private static readonly Guid[] P = Enumerable.Range(1, 10).Select(G).ToArray();

    private sealed class FixedClock(DateTime now) : IClock { public DateTime UtcNow { get; } = now; }

    private sealed class FakeSeats : ISeatLicenseApi
    {
        public bool ReportsRefused { get; set; }
        public int UsageCalls { get; private set; }
        public Task<IReadOnlyList<SubscribedSkuData>> SubscribedSkusAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<SubscribedSkuData>>(new[] { new SubscribedSkuData(AddOn, "Microsoft_365_Copilot", 10, 0, 0, 9), new SubscribedSkuData(Suite, "SUITE_WITH_COPILOT", 50, 0, 0, 1) });

        public Task<SeatGroupData?> FindGroupAsync(string name, CancellationToken ct) => Task.FromResult(name switch
        {
            "GSGu_MCO_CopilotM365" => new SeatGroupData(Lic, name, true, new[] { AddOn }),
            "GSGu_MCO_ATracking" => new SeatGroupData(TrackA, name, true, Array.Empty<Guid>()),
            "GSGu_MCO_BTracking" => new SeatGroupData(TrackB, name, true, Array.Empty<Guid>()),
            "GSGu_MCO_ExclusionLic" => new SeatGroupData(Excl, name, true, Array.Empty<Guid>()),
            _ => (SeatGroupData?)null,
        });

        public Task<SeatGroupData?> GetGroupAsync(Guid id, CancellationToken ct)
            => FindGroupAsync(id == Lic ? "GSGu_MCO_CopilotM365" : id == TrackA ? "GSGu_MCO_ATracking" : id == TrackB ? "GSGu_MCO_BTracking" : id == Excl ? "GSGu_MCO_ExclusionLic" : "", ct);

        public Task<GroupMembersData> GroupMembersAsync(Guid id, CancellationToken ct) => Task.FromResult(
            id == Lic ? new GroupMembersData(new[] { P[0], P[1], P[2], P[3], P[5], P[6], P[8], P[9] }, 1)
          : id == TrackA ? new GroupMembersData(new[] { P[0], P[1], P[2], P[3], P[5], P[6] }, 0)
          : id == TrackB ? new GroupMembersData(new[] { P[3], P[7], P[8], P[9] }, 0)
          : id == Excl ? new GroupMembersData(new[] { P[9] }, 0)
          : new GroupMembersData(Array.Empty<Guid>(), 0));

        /// <summary>The date of each person's license state in Entra; everyone else has held the seat since January.</summary>
        public Dictionary<int, DateTime> HeldSince { get; } = new();

        public Task<IReadOnlyList<LicensedUserData>> UsersWithSkuAsync(Guid sku, CancellationToken ct)
        {
            LicensedUserData U(int i, Guid? group, string? error = null) => new(P[i], $"p{i + 1}@contoso.example", "CA",
                new[] { new LicenseStateData(sku, group, error is null ? "Active" : "Error", error, HeldSince.TryGetValue(i, out var d) ? d : new DateTime(2026, 1, 1)) });
            IReadOnlyList<LicensedUserData> list = sku == AddOn
                ? new[] { U(0, Lic), U(1, Lic), U(2, Lic), U(3, Lic), U(4, null), U(5, Lic, "CountViolation"), U(6, Lic), U(8, Lic), U(9, Lic) }
                : new[] { U(8, null) };
            return Task.FromResult(list);
        }

        public Task<CopilotUsageData> CopilotUsageAsync(string period, CancellationToken ct)
        {
            UsageCalls++;
            if (ReportsRefused) throw new IntegrationPermissionException("Microsoft Graph", 403);
            return Task.FromResult(new CopilotUsageData(new[]
            {
                new CopilotUsageRow("p1@contoso.example", new DateTime(2026, 10, 30)),   // active
                new CopilotUsageRow("p2@contoso.example", new DateTime(2026, 7, 1)),     // inactive 125 days
            }, new DateTime(2026, 11, 2), false));
        }
    }

    private sealed class FakeInsights(InsightsActivity? activity) : IInsightsActivitySource
    {
        public Task<InsightsActivity> ReadAsync(CancellationToken ct) => activity is null ? throw new InvalidOperationException("Insights database unavailable (test)") : Task.FromResult(activity);
    }

    private (SeatSyncService Service, Db Db, FakeSeats Api) Build(DateTime now, IInsightsActivitySource? insights = null)
    {
        var clock = new FixedClock(now);
        var appDb = new Db(Options.Create(new DbOptions { ConnectionString = db.WorkerConnectionString }), SystemPrincipal.Instance);
        var api = new FakeSeats();
        return (new SeatSyncService(appDb, api, new FindingWriter(new OutboxWriter(), clock), clock, NullLogger<SeatSyncService>.Instance, insights), appDb, api);
    }

    [Fact]
    public async Task Activity_comes_from_Copilot_Insights_when_it_can_be_read_and_from_the_usage_report_when_not()
    {
        using var c = db.Admin();
        await ArrangeAsync(c);
        var insights = new InsightsActivity(new DateTime(2026, 11, 1),
            [new InsightsActivityUser("p2@contoso.example", new DateTime(2026, 10, 31)), new InsightsActivityUser("p3@contoso.example", new DateTime(2026, 6, 1))]);
        var (service, appDb, api) = Build(new DateTime(2026, 11, 3, 12, 0, 0, DateTimeKind.Utc), new FakeInsights(insights));
        await service.RunAsync(null, default);
        using var w = await appDb.OpenSystemAsync();
        var rows = (await w.QueryAsync<(string Upn, DateTime? Last, DateTime? Refresh)>("SELECT UserPrincipalName, LastActivityDate, ReportRefreshDate FROM intg.CopilotActivity ORDER BY UserPrincipalName")).ToList();
        Assert.Equal([("p2@contoso.example", (DateTime?)new DateTime(2026, 10, 31), (DateTime?)new DateTime(2026, 11, 1)), ("p3@contoso.example", new DateTime(2026, 6, 1), new DateTime(2026, 11, 1))], rows);
        Assert.Equal(0, api.UsageCalls);
        Assert.Contains("from Copilot Insights", await w.ExecuteScalarAsync<string>("SELECT TOP 1 Message FROM intg.SyncRun WHERE Integration = 'X13' ORDER BY SyncRunId DESC"));

        // Insights unreadable: the usage report is read instead, and the run still succeeds.
        var (fallback, _, api2) = Build(new DateTime(2026, 11, 3, 12, 0, 0, DateTimeKind.Utc), new FakeInsights(null));
        await fallback.RunAsync(null, default);
        Assert.Equal(1, api2.UsageCalls);
        Assert.Equal("SUCCEEDED", await w.ExecuteScalarAsync<string>("SELECT TOP 1 Status FROM intg.SyncRun WHERE Integration = 'X13' ORDER BY SyncRunId DESC"));
        Assert.Equal(new DateTime(2026, 7, 1), await w.ExecuteScalarAsync<DateTime>("SELECT LastActivityDate FROM intg.CopilotActivity WHERE UserPrincipalName = 'p2@contoso.example'"));
    }

    [Fact]
    public async Task Stale_Copilot_Insights_activity_is_not_used_and_the_usage_report_is_read_instead()
    {
        Assert.False(SeatSyncService.IsStale(new DateTime(2026, 10, 31), new DateTime(2026, 11, 3)));
        Assert.True(SeatSyncService.IsStale(new DateTime(2026, 10, 30), new DateTime(2026, 11, 3)));

        using var c = db.Admin();
        await ArrangeAsync(c);
        // Insights' nightly load stopped on 25 October: read as is, every holder would look inactive since then.
        var stale = new InsightsActivity(new DateTime(2026, 10, 25), [new InsightsActivityUser("p1@contoso.example", new DateTime(2026, 10, 25))]);
        var (service, appDb, api) = Build(new DateTime(2026, 11, 3, 12, 0, 0, DateTimeKind.Utc), new FakeInsights(stale));
        await service.RunAsync(null, default);
        Assert.Equal(1, api.UsageCalls);
        using var w = await appDb.OpenSystemAsync();
        var run = await w.QuerySingleAsync<(string Status, string Message)>("SELECT TOP 1 Status, Message FROM intg.SyncRun WHERE Integration = 'X13' ORDER BY SyncRunId DESC");
        Assert.Equal("SUCCEEDED", run.Status);
        Assert.Contains("ends 25 Oct 2026, more than 3 days ago", run.Message);
        // The activity is the usage report's (person 2 last active in July), not Insights'.
        Assert.Equal(new DateTime(2026, 11, 2), await w.ExecuteScalarAsync<DateTime>("SELECT ReportRefreshDate FROM intg.CopilotActivity WHERE UserPrincipalName = 'p2@contoso.example'"));
    }

    [Fact]
    public async Task A_seat_held_for_less_than_the_inactivity_window_is_not_proposed_for_reclaim()
    {
        using var c = db.Admin();
        await ArrangeAsync(c);
        // Person 4 has no Copilot activity at all. Start them over as a new seat holder, assigned on 30 October.
        var p4 = await c.ExecuteScalarAsync<int>("SELECT PersonId FROM sec.Person WHERE EntraObjectId = @id", new { id = P[3] });
        await c.ExecuteAsync("DELETE FROM intg.SeatHolder WHERE MemberObjectId = @id", new { id = P[3] });
        await c.ExecuteAsync("UPDATE core.SeatReclaim SET Status = 'CANCELLED', ClosedUtc = SYSUTCDATETIME() WHERE PersonId = @p4 AND Status IN ('PROPOSED','RECLAIM')", new { p4 });
        const string proposedSql = "SELECT COUNT(*) FROM core.SeatReclaim WHERE PersonId = @p4 AND Status = 'PROPOSED'";

        var (service, appDb, api) = Build(new DateTime(2026, 11, 3, 12, 0, 0, DateTimeKind.Utc));
        api.HeldSince[3] = new DateTime(2026, 10, 30);
        await service.RunAsync(null, default);
        using var w = await appDb.OpenSystemAsync();
        Assert.Equal(new DateTime(2026, 11, 3, 12, 0, 0), await w.ExecuteScalarAsync<DateTime>("SELECT MIN(FirstSeenUtc) FROM intg.SeatHolder WHERE MemberObjectId = @id", new { id = P[3] }));
        Assert.Equal(0, await w.ExecuteScalarAsync<int>(proposedSql, new { p4 }));

        // 60 days on, the seat has been held for the whole window and is still unused: now it is proposed.
        var (later, _, laterApi) = Build(new DateTime(2027, 1, 10, 12, 0, 0, DateTimeKind.Utc));
        laterApi.HeldSince[3] = new DateTime(2026, 10, 30);
        await later.RunAsync(null, default);
        Assert.Equal(1, await w.ExecuteScalarAsync<int>(proposedSql, new { p4 }));
        // The first sighting is kept across reads.
        Assert.Equal(new DateTime(2026, 11, 3, 12, 0, 0), await w.ExecuteScalarAsync<DateTime>("SELECT MIN(FirstSeenUtc) FROM intg.SeatHolder WHERE MemberObjectId = @id", new { id = P[3] }));
    }

    /// <summary>Every test shares the database: the data is arranged once, whichever test runs first.</summary>
    private async Task<(int A, int B)> ArrangeAsync(SqlConnection c)
    {
        var existing = await c.QueryAsync<int>("SELECT FundingOwnerId FROM core.FundingOwner WHERE Code IN ('SEAT_A','SEAT_B') ORDER BY Code");
        if (existing.ToList() is [var ea, var eb]) return (ea, eb);
        var a = await c.ExecuteScalarAsync<int>("INSERT core.FundingOwner (Code, Name, OwnerType, Status, EffectiveFrom) OUTPUT inserted.FundingOwnerId VALUES ('SEAT_A', N'Seat LOB A', 'LOB', 'ACTIVE', '2026-01-01')");
        var b = await c.ExecuteScalarAsync<int>("INSERT core.FundingOwner (Code, Name, OwnerType, Status, EffectiveFrom) OUTPUT inserted.FundingOwnerId VALUES ('SEAT_B', N'Seat LOB B', 'LOB', 'ACTIVE', '2026-01-01')");
        for (var i = 0; i < P.Length; i++)
            await c.ExecuteAsync(
                "INSERT sec.Person (EntraObjectId, UserPrincipalName, DisplayName, AccountEnabled, LeaveDate, CostCenterCode) VALUES (@id, @upn, @name, @enabled, NULL, @cc)",
                new { id = P[i], upn = $"p{i + 1}@contoso.example", name = $"Person {i + 1}", enabled = i != 6, cc = i == 2 ? "CC-B" : null });
        // Person 3 is tracked in A but their cost center maps to B.
        await c.ExecuteAsync("INSERT core.AttributionRule (FundingOwnerId, MatchCostCenter, EffectiveFrom) VALUES (@b, N'CC-B', '2026-01-01')", new { b });
        await c.ExecuteAsync(
            """
            INSERT core.SeatGroup (DisplayName, Purpose, FundingOwnerId, PurposeConfirmed, EffectiveFrom) VALUES
              (N'GSGu_MCO_CopilotM365', 'LICENSE', NULL, 1, '2026-01-01'),
              (N'GSGu_MCO_ATracking', 'LOB_TRACKING', @a, 1, '2026-01-01'),
              (N'GSGu_MCO_BTracking', 'LOB_TRACKING', @b, 1, '2026-01-01'),
              (N'GSGu_MCO_ExclusionLic', 'EXCLUSION', NULL, 1, '2026-01-01'),
              (N'GSGu_MCO_Gone', 'OTHER', NULL, 0, '2026-01-01');
            INSERT core.SeatReservation (FundingOwnerId, Seats, ChangeKind, EffectiveFrom) VALUES (@a, 2, 'INITIAL', '2026-01-01'), (@b, 5, 'INITIAL', '2026-01-01');
            INSERT core.SeatSku (SkuId, SkuPartNumber, Name, Kind) VALUES (@Suite, 'SUITE_WITH_COPILOT', N'Suite with Copilot', 'INCLUDES_COPILOT');
            """, new { a, b, Suite });
        return (a, b);
    }

    [Fact]
    public async Task Seat_read_writes_position_findings_reclaims_and_a_draft_snapshot()
    {
        using var c = db.Admin();
        var (a, b) = await ArrangeAsync(c);
        var (service, appDb, _) = Build(new DateTime(2026, 11, 3, 12, 0, 0, DateTimeKind.Utc));

        await service.RunAsync(null, default);
        // Read as the worker (central scope): row-level security hides tenant-level rows from the admin login.
        using var w = await appDb.OpenSystemAsync();

        var run = await w.QuerySingleAsync<(string Status, string Message)>("SELECT TOP 1 Status, Message FROM intg.SyncRun WHERE Integration = 'X13' ORDER BY SyncRunId DESC");
        Assert.Equal("SUCCEEDED", run.Status);
        Assert.Equal(1, await w.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM core.SeatSku WHERE SkuId = @AddOn AND Kind = 'COPILOT_ADDON'", new { AddOn }));
        Assert.Equal(4, await w.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM core.SeatGroup WHERE EntraGroupId IS NOT NULL"));

        // A: people 1-4 and 7 hold active seats (6 is in error) against 2 reserved; B: 4, 9, 10 against 5.
        var pos = (await w.QueryAsync<(int? FundingOwnerId, int ReservedSeats, int TrackingMembers, int AssignedSeats, int OverSeats)>(
            "SELECT FundingOwnerId, ReservedSeats, TrackingMembers, AssignedSeats, OverSeats FROM rpt.fn_SeatPosition('2026-11-03')")).ToList();
        Assert.Contains((a, 2, 6, 5, 3), pos.Select(p => (p.FundingOwnerId ?? 0, p.ReservedSeats, p.TrackingMembers, p.AssignedSeats, p.OverSeats)));
        Assert.Contains((b, 5, 4, 3, 0), pos.Select(p => (p.FundingOwnerId ?? 0, p.ReservedSeats, p.TrackingMembers, p.AssignedSeats, p.OverSeats)));
        Assert.Equal(1, pos.Single(p => p.FundingOwnerId is null).AssignedSeats);   // person 5

        var open = (await w.QueryAsync<(string FindingType, string DedupeKey)>("SELECT FindingType, DedupeKey FROM ops.Finding WHERE Status = 'OPEN'")).ToList();
        int Count(string type) => open.Count(f => f.FindingType == type);
        Assert.Equal(1, Count(FindingTypes.SeatOverReservation));             // A
        Assert.Equal(1, Count(FindingTypes.SeatReservationsOverInventory));   // 7 reserved + 25 buffer > 10 bought
        Assert.Equal(1, Count(FindingTypes.SeatNoLob));                       // 5
        Assert.Equal(1, Count(FindingTypes.SeatTwoLobs));                     // 4
        Assert.Equal(2, Count(FindingTypes.SeatTrackedNoLicense));            // 6 (error) and 8
        Assert.Equal(1, Count(FindingTypes.SeatDirectAssignment));            // 5; person 9's direct row is the suite, not the add-on
        Assert.Equal(1, Count(FindingTypes.SeatLicenseError));                // 6
        Assert.Equal(1, Count(FindingTypes.SeatLeaver));                      // 7
        Assert.Equal(1, Count(FindingTypes.SeatExcludedHolder));              // 10
        Assert.Equal(1, Count(FindingTypes.SeatPaidTwice));                   // 9
        Assert.Equal(1, Count(FindingTypes.SeatNestedGroup));                 // license group
        Assert.Equal(1, Count(FindingTypes.SeatGroupMissing));                // GSGu_MCO_Gone
        Assert.Equal(1, Count(FindingTypes.SeatCostCenterMismatch));          // 3
        Assert.True(await w.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM ntf.Outbox WHERE EventType = 'N10'") >= 4);

        // Reclaim: person 2 inactive 125 days; holders missing from the report are never active; person 1 is active.
        var proposed = (await w.QueryAsync<string>(
            "SELECT p.UserPrincipalName FROM core.SeatReclaim r JOIN sec.Person p ON p.PersonId = r.PersonId WHERE r.Status = 'PROPOSED'")).ToHashSet();
        Assert.Contains("p2@contoso.example", proposed);
        Assert.DoesNotContain("p1@contoso.example", proposed);
        Assert.Contains("p4@contoso.example", proposed);
        Assert.Null(await w.ExecuteScalarAsync<int?>("SELECT r.FundingOwnerId FROM core.SeatReclaim r JOIN sec.Person p ON p.PersonId = r.PersonId WHERE p.UserPrincipalName = 'p4@contoso.example'"));   // two LOBs

        // Draft chargeback for October: A pays 2 + 3 over, B pays its 5 reserved, plus the no-LOB row.
        var snap = (await w.QueryAsync<(int? FundingOwnerId, int ChargeSeats, decimal Amount, string Status)>(
            "SELECT FundingOwnerId, ChargeSeats, Amount, Status FROM core.SeatChargeSnapshot WHERE PeriodMonth = '2026-10-01'")).ToList();
        Assert.Equal(3, snap.Count);
        Assert.Contains((a, 5, 150.00m, "DRAFT"), snap.Select(s => (s.FundingOwnerId ?? 0, s.ChargeSeats, s.Amount, s.Status)));
        Assert.Contains((b, 5, 150.00m, "DRAFT"), snap.Select(s => (s.FundingOwnerId ?? 0, s.ChargeSeats, s.Amount, s.Status)));

        // A second run raises nothing new and keeps the existing draft.
        await service.RunAsync(null, default);
        Assert.Equal(open.Count, await w.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM ops.Finding WHERE Status = 'OPEN' AND FindingType LIKE 'SEAT%'"));
        Assert.Equal(3, await w.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM core.SeatChargeSnapshot WHERE PeriodMonth = '2026-10-01'"));
    }

    [Fact]
    public async Task Certified_snapshot_never_changes_and_refused_reports_do_not_fail_the_run()
    {
        using var c = db.Admin();
        await ArrangeAsync(c);
        var (service, appDb, api) = Build(new DateTime(2026, 12, 2, 12, 0, 0, DateTimeKind.Utc));
        api.ReportsRefused = true;
        await service.RunAsync(null, default);
        var run = await c.QuerySingleAsync<(string Status, string Message)>("SELECT TOP 1 Status, Message FROM intg.SyncRun WHERE Integration = 'X13' ORDER BY SyncRunId DESC");
        Assert.Equal("SUCCEEDED", run.Status);
        Assert.Contains("Reports.Read.All", run.Message);

        Assert.True(await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM core.SeatChargeSnapshot WHERE PeriodMonth = '2026-11-01'") > 0);
        await c.ExecuteAsync("UPDATE core.SeatChargeSnapshot SET Status = 'CERTIFIED', CertifiedUtc = SYSUTCDATETIME() WHERE PeriodMonth = '2026-11-01'");
        var ex = await Assert.ThrowsAsync<SqlException>(() => c.ExecuteAsync("UPDATE core.SeatChargeSnapshot SET Amount = 0 WHERE PeriodMonth = '2026-11-01'"));
        Assert.Equal(51070, ex.Number);
        await Assert.ThrowsAsync<SqlException>(() => c.ExecuteAsync("DELETE FROM core.SeatChargeSnapshot WHERE PeriodMonth = '2026-11-01'"));

        // A forced retake skips a certified month.
        var note = await appDb.InTransactionAsync(u => SeatSyncService.TakeSnapshotAsync(u, SeatSettings.Default, new DateTime(2026, 12, 2), null, force: true));
        Assert.Null(note);
    }
    [Fact]
    public async Task The_reporting_view_gives_todays_seats_by_line_of_business()
    {
        using var c = db.Admin();
        var (a, _) = await ArrangeAsync(c);
        var (service, appDb, _) = Build(new DateTime(2026, 11, 3, 12, 0, 0, DateTimeKind.Utc));
        await service.RunAsync(null, default);
        using var w = await appDb.OpenSystemAsync();
        var today = DateTime.UtcNow.Date;
        var view = (await w.QueryAsync<(int? FundingOwnerId, int ReservedSeats, int AssignedSeats, int OverSeats, int UnusedReservedSeats)>(
            "SELECT FundingOwnerId, ReservedSeats, AssignedSeats, OverSeats, UnusedReservedSeats FROM rpt.vSeatReservationCurrent")).ToList();
        var fn = (await w.QueryAsync<(int? FundingOwnerId, int ReservedSeats, int AssignedSeats, int OverSeats)>(
            "SELECT FundingOwnerId, ReservedSeats, AssignedSeats, OverSeats FROM rpt.fn_SeatPosition(@today)", new { today })).ToList();
        Assert.Equal(fn.Count, view.Count);
        Assert.Equal(fn.Sum(x => x.ReservedSeats), view.Sum(x => x.ReservedSeats));
        Assert.Equal(fn.Sum(x => x.AssignedSeats), view.Sum(x => x.AssignedSeats));
        var rowA = view.Single(x => x.FundingOwnerId == a);
        Assert.Equal(Math.Max(0, rowA.ReservedSeats - rowA.AssignedSeats), rowA.UnusedReservedSeats);
    }
}