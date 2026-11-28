using Dapper;
using Governance.Application;
using Governance.Application.Commands;
using Governance.Application.Data;
using Governance.Application.Outbox;
using Governance.Application.Queries;
using Governance.Domain;
using Microsoft.Extensions.Options;

namespace Governance.IntegrationTests;

/// <summary>Copilot seats: LOB seat admins, what each person sees, and the seeded demo read-back.</summary>
[Collection("sql")]
public sealed class SeatTests(SeededFixture fx) : IClassFixture<SeededFixture>
{
    /// <summary>A signed-in LOB person: not central, permissions loaded from the database like the web does.</summary>
    private sealed class LobPrincipal(int personId, IReadOnlyDictionary<int, IReadOnlySet<string>> scoped, IReadOnlyList<RoleGrant> roles) : ICurrentPrincipal
    {
        public bool IsAuthenticated => true;
        public int PersonId { get; } = personId;
        public int? RealPersonId => null;
        public string DisplayName => "test";
        public bool IsCentral => false;
        public IReadOnlyList<RoleGrant> Roles { get; } = roles;
        public IReadOnlySet<string> AppRoles { get; } = new HashSet<string>();
        public IReadOnlySet<string> Permissions { get; } = new HashSet<string>();
        public IReadOnlyDictionary<int, IReadOnlySet<string>> ScopedPermissions { get; } = scoped;
    }

    private LobPrincipal As(string key)
    {
        using var c = fx.Admin();
        var person = fx.Seed.Person(key);
        var rows = c.Query<(string RoleCode, int ScopeId, string? Permission)>(
            """
            SELECT ra.RoleCode, ra.ScopeId, rp.Permission FROM sec.RoleAssignment ra
            LEFT JOIN sec.RolePermission rp ON rp.RoleCode = ra.RoleCode
            WHERE ra.PersonId = @person AND ra.ScopeType = 'FUNDING_OWNER' AND (ra.EffectiveTo IS NULL OR ra.EffectiveTo >= CAST(SYSUTCDATETIME() AS date))
            """, new { person }).ToList();
        var scoped = rows.GroupBy(r => r.ScopeId).ToDictionary(g => g.Key, g => (IReadOnlySet<string>)g.Where(r => r.Permission is not null).Select(r => r.Permission!).ToHashSet());
        return new LobPrincipal(person, scoped, rows.Select(r => new RoleGrant(r.RoleCode, ScopeTypes.FundingOwner, r.ScopeId)).Distinct().ToList());
    }

    private Db WebDb(ICurrentPrincipal p) => new(Options.Create(new DbOptions { ConnectionString = fx.WebConnectionString }), p);

    private SeatCommands Commands(ICurrentPrincipal p)
    {
        var clock = fx.Host.Clock;
        return new SeatCommands(new CommandRunner(WebDb(p), p, new AuditWriter(clock)), clock, new OutboxWriter());
    }

    private Guid Lob(string code)
    {
        using var c = fx.Admin();
        return c.ExecuteScalar<Guid>("SELECT PublicId FROM core.FundingOwner WHERE Code = @code", new { code });
    }

    private int ActiveAdmins(string code)
    {
        using var c = fx.Admin();
        return c.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM sec.RoleAssignment WHERE RoleCode = 'LOB_SEAT_ADMIN' AND ScopeId = @fo AND (EffectiveTo IS NULL OR EffectiveTo >= CAST(SYSUTCDATETIME() AS date))",
            new { fo = fx.Seed.FundingOwners[code] });
    }

    private Guid AdminAssignment(string personKey, string code)
    {
        using var c = fx.Admin();
        return c.ExecuteScalar<Guid>(
            "SELECT PublicId FROM sec.RoleAssignment WHERE RoleCode = 'LOB_SEAT_ADMIN' AND PersonId = @p AND ScopeId = @fo AND (EffectiveTo IS NULL OR EffectiveTo >= CAST(SYSUTCDATETIME() AS date))",
            new { p = fx.Seed.Person(personKey), fo = fx.Seed.FundingOwners[code] });
    }

    [Fact]
    public void The_demo_seeds_14_lobs_with_tracking_groups_and_a_seat_admin_each()
    {
        using var c = fx.Admin();
        Assert.Equal(14, c.ExecuteScalar<int>("SELECT COUNT(*) FROM core.SeatGroup WHERE Purpose = 'LOB_TRACKING' AND EffectiveTo IS NULL"));
        Assert.Equal("Marketing and Communication . M365 Copilot License . Role",
            c.ExecuteScalar<string>("SELECT OimRoleName FROM core.SeatGroup WHERE DisplayName = 'GSGu_MCO_MarComTracking'"));
        Assert.Equal(1, c.ExecuteScalar<int>("SELECT COUNT(*) FROM core.SeatGroup WHERE Purpose = 'LICENSE' AND DisplayName = 'GSGu_MCO_CopilotM365'"));
        var cib = c.QuerySingle<(int Reserved, int Assigned, int Over)>("SELECT ReservedSeats, AssignedSeats, OverSeats FROM rpt.fn_SeatPosition(CAST(SYSUTCDATETIME() AS date)) WHERE Code = 'CIB'");
        Assert.Equal(150, cib.Reserved);
        Assert.True(cib.Over > 0, "C&IB is seeded above its reservation.");
        Assert.Equal(6, c.ExecuteScalar<int>("SELECT AssignedSeats FROM rpt.fn_SeatPosition(CAST(SYSUTCDATETIME() AS date)) WHERE FundingOwnerId IS NULL"));
        foreach (var code in new[] { "AMG", "CIB", "AUDIT", "RETAIL", "MARCOM", "BSAM" }) Assert.True(ActiveAdmins(code) >= 1, code);
    }

    [Fact]
    public async Task A_seat_admin_can_name_another_seat_admin_for_the_same_lob()
    {
        var before = ActiveAdmins("AMG");
        await Commands(As("AMG.seatadmin")).AddSeatAdminAsync(Guid.NewGuid(), Lob("AMG"), fx.Seed.Person("RETAIL.maker1").ToString());
        Assert.Equal(before + 1, ActiveAdmins("AMG"));
        using var c = fx.Admin();
        Assert.Equal(fx.Seed.Person("AMG.seatadmin"), c.ExecuteScalar<int>(
            "SELECT GrantedByPersonId FROM sec.RoleAssignment WHERE RoleCode = 'LOB_SEAT_ADMIN' AND PersonId = @p AND ScopeId = @fo",
            new { p = fx.Seed.Person("RETAIL.maker1"), fo = fx.Seed.FundingOwners["AMG"] }));
        Assert.True(c.ExecuteScalar<int>("SELECT COUNT(*) FROM aud.AuditLog WHERE Action = 'role.assign' AND PersonId = @p", new { p = fx.Seed.Person("AMG.seatadmin") }) >= 1);
    }

    [Fact]
    public async Task A_seat_admin_cannot_name_seat_admins_for_another_lob()
    {
        var before = ActiveAdmins("CIB");
        var ex = await Assert.ThrowsAsync<DomainException>(() => Commands(As("FININV.seatadmin")).AddSeatAdminAsync(Guid.NewGuid(), Lob("CIB"), fx.Seed.Person("RETAIL.maker2").ToString()));
        Assert.Contains("this LOB", ex.Message);
        Assert.Equal(before, ActiveAdmins("CIB"));
    }

    [Fact]
    public async Task A_seat_admin_cannot_end_another_role()
    {
        using var c = fx.Admin();
        var ownerAssignment = c.ExecuteScalar<Guid>(
            "SELECT PublicId FROM sec.RoleAssignment WHERE RoleCode = 'LOB_BUSINESS_OWNER' AND ScopeId = @fo AND EffectiveTo IS NULL", new { fo = fx.Seed.FundingOwners["LEGAL"] });
        await Assert.ThrowsAsync<DomainException>(() => Commands(As("LEGAL.seatadmin")).EndSeatAdminAsync(Guid.NewGuid(), ownerAssignment));
        Assert.Equal(1, c.ExecuteScalar<int>("SELECT COUNT(*) FROM sec.RoleAssignment WHERE PublicId = @ownerAssignment AND EffectiveTo IS NULL", new { ownerAssignment }));
    }

    [Fact]
    public async Task The_last_seat_admin_cannot_remove_themselves_but_the_business_owner_can()
    {
        Assert.Equal(1, ActiveAdmins("AUDIT"));
        var mine = AdminAssignment("AUDIT.seatadmin", "AUDIT");
        await Assert.ThrowsAsync<DomainException>(() => Commands(As("AUDIT.seatadmin")).EndSeatAdminAsync(Guid.NewGuid(), mine));
        Assert.Equal(1, ActiveAdmins("AUDIT"));

        // With a second admin named, the first one can step down.
        await Commands(As("AUDIT.seatadmin")).AddSeatAdminAsync(Guid.NewGuid(), Lob("AUDIT"), fx.Seed.Person("WEALTH.maker1").ToString());
        await Commands(As("AUDIT.seatadmin")).EndSeatAdminAsync(Guid.NewGuid(), mine);
        Assert.Equal(1, ActiveAdmins("AUDIT"));

        // The business owner may end the last one.
        await Commands(As("AUDIT.owner")).EndSeatAdminAsync(Guid.NewGuid(), AdminAssignment("WEALTH.maker1", "AUDIT"));
        Assert.Equal(0, ActiveAdmins("AUDIT"));
    }

    [Fact]
    public async Task A_seat_admin_cannot_name_themselves()
        => await Assert.ThrowsAsync<DomainException>(() => Commands(As("BSAM.seatadmin")).AddSeatAdminAsync(Guid.NewGuid(), Lob("BSAM"), fx.Seed.Person("BSAM.seatadmin").ToString()));

    [Fact]
    public async Task A_seat_admin_sees_only_their_lob()
    {
        var p = As("MARCOM.seatadmin");
        var fo = fx.Seed.FundingOwners["MARCOM"];
        var q = new SeatQueries(WebDb(p), p, fx.Host.Clock);
        var lobs = await q.LobsAsync();
        Assert.Equal(new int?[] { fo }, lobs.Select(l => l.FundingOwnerId).ToArray());
        Assert.Null(await q.LobAsync(Lob("CIB")));
        Assert.NotNull(await q.LobAsync(Lob("MARCOM")));
        Assert.All(await q.SnapshotAsync(DateTime.UtcNow.AddMonths(-2)), r => Assert.Equal(fo, r.FundingOwnerId));
        Assert.Empty(await q.UnattributedAsync());

        // Row-level security on the tables themselves, as the web login sees them.
        await using var c = await WebDb(p).OpenUserAsync();
        Assert.Equal(new[] { fo }, (await c.QueryAsync<int>("SELECT DISTINCT FundingOwnerId FROM core.SeatReservation")).ToArray());
        Assert.Equal(new[] { fo }, (await c.QueryAsync<int>("SELECT DISTINCT FundingOwnerId FROM core.SeatChargeSnapshot WHERE FundingOwnerId IS NOT NULL")).ToArray());
    }

    [Fact]
    public async Task Reclaim_by_the_business_owner_opens_an_itsm_task_naming_the_oim_role()
    {
        using var c = fx.Admin();
        var reclaim = c.ExecuteScalar<Guid>("SELECT TOP (1) PublicId FROM core.SeatReclaim WHERE FundingOwnerId = @fo AND Status = 'PROPOSED'", new { fo = fx.Seed.FundingOwners["TECHSYS"] });
        await Assert.ThrowsAsync<DomainException>(() => Commands(As("TECHSYS.seatadmin")).DecideReclaimAsync(Guid.NewGuid(), reclaim, true, "Unused"));
        var ticket = await Commands(As("TECHSYS.owner")).DecideReclaimAsync(Guid.NewGuid(), reclaim, true, "No use in the last quarter.");
        Assert.NotNull(ticket);
        Assert.Equal("RECLAIM", c.ExecuteScalar<string>("SELECT Status FROM core.SeatReclaim WHERE PublicId = @reclaim", new { reclaim }));
        var payload = c.ExecuteScalar<string>("SELECT Payload FROM wf.ItsmTicket WHERE ItsmTicketId = @id", new { id = long.Parse(ticket!) });
        Assert.Contains("Technology and Systems . M365 Copilot License . Role", payload);
        Assert.Equal(1, c.ExecuteScalar<int>("SELECT COUNT(*) FROM ntf.Outbox WHERE DedupeKey = @k", new { k = $"ITSM.Create:{ticket}" }));
    }

    [Fact]
    public async Task The_first_reservation_is_recorded_once()
    {
        var finops = fx.Seed.As("finopsLead");
        var runner = new CommandRunner(fx.Host.DbFor(finops), new FinOps(finops.PersonId), new AuditWriter(fx.Host.Clock));
        var cmd = new SeatCommands(runner, fx.Host.Clock, new OutboxWriter());
        // Every seeded LOB already has one: a second "first" reservation is refused.
        await Assert.ThrowsAsync<DomainException>(() => cmd.RecordInitialReservationAsync(Guid.NewGuid(), Lob("HR"), 10, DateTime.UtcNow, null));
        // WEALTH has none yet.
        await cmd.RecordInitialReservationAsync(Guid.NewGuid(), Lob("WEALTH"), 12, DateTime.UtcNow.Date, "test");
        using var c = fx.Admin();
        Assert.Equal(12, c.ExecuteScalar<int>("SELECT Seats FROM core.SeatReservation WHERE FundingOwnerId = @fo AND ChangeKind = 'INITIAL'", new { fo = fx.Seed.FundingOwners["WEALTH"] }));
    }

    [Fact]
    public async Task With_seats_only_the_reservation_is_set_on_the_page_and_its_history_kept()
    {
        // The Platform Owner alone sets reservations; FinOps is refused.
        var po = fx.Seed.As("platformOwner");
        var cmd = new SeatCommands(new CommandRunner(fx.Host.DbFor(po), new FinOps(po.PersonId, Governance.Domain.Permissions.RegistryEdit), new AuditWriter(fx.Host.Clock)), fx.Host.Clock, new OutboxWriter());
        var finops = fx.Seed.As("finopsLead");
        var notPo = new SeatCommands(new CommandRunner(fx.Host.DbFor(finops), new FinOps(finops.PersonId), new AuditWriter(fx.Host.Clock)), fx.Host.Clock, new OutboxWriter());
        var refused = await Assert.ThrowsAsync<DomainException>(() => notPo.SetReservationAsync(Guid.NewGuid(), Lob("HR"), 5, DateTime.UtcNow.Date.AddYears(5), null));
        Assert.Contains("Only the Platform Owner", refused.Message);
        var fo = fx.Seed.FundingOwners["HR"];
        using var c = fx.Admin();
        var scope = c.ExecuteScalar<string?>("SELECT SettingValue FROM ref.Setting WHERE SettingKey = 'Workbench.Scope'");
        try
        {
            c.Execute("UPDATE ref.Setting SET SettingValue = 'SEATS' WHERE SettingKey = 'Workbench.Scope'");
            var before = c.ExecuteScalar<int>("SELECT Seats FROM core.SeatReservation WHERE FundingOwnerId = @fo AND EffectiveTo IS NULL", new { fo });
            var from = fx.Host.Clock.UtcNow.Date.AddDays(10);

            // A new figure from a date: the one before ends the day before, and is kept.
            await cmd.SetReservationAsync(Guid.NewGuid(), Lob("HR"), before + 40, from, "Finance schedule");
            var rows = c.Query<(int Seats, int? Prev, string Kind, DateTime From, DateTime? To)>(
                "SELECT Seats, PreviousSeats, ChangeKind, EffectiveFrom, EffectiveTo FROM core.SeatReservation WHERE FundingOwnerId = @fo ORDER BY EffectiveFrom", new { fo }).ToList();
            Assert.Equal((before + 40, (int?)before, "INCREASE", from, (DateTime?)null), rows[^1]);
            Assert.Equal(from.AddDays(-1), rows[^2].To);

            // The same date corrects that figure instead of adding a row.
            await cmd.SetReservationAsync(Guid.NewGuid(), Lob("HR"), before + 35, from, null);
            Assert.Equal(rows.Count, c.ExecuteScalar<int>("SELECT COUNT(*) FROM core.SeatReservation WHERE FundingOwnerId = @fo", new { fo }));
            Assert.Equal(before + 35, c.ExecuteScalar<int>("SELECT Seats FROM core.SeatReservation WHERE FundingOwnerId = @fo AND EffectiveTo IS NULL", new { fo }));

            // A date before the latest figure would rewrite history: refused. So is a change with the whole Workbench on.
            await Assert.ThrowsAsync<DomainException>(() => cmd.SetReservationAsync(Guid.NewGuid(), Lob("HR"), 5, from.AddDays(-3), null));
            c.Execute("UPDATE ref.Setting SET SettingValue = 'FULL' WHERE SettingKey = 'Workbench.Scope'");
            var full = await Assert.ThrowsAsync<DomainException>(() => cmd.SetReservationAsync(Guid.NewGuid(), Lob("HR"), 5, from.AddDays(20), null));
            Assert.Contains("seat reservation request", full.Message);
        }
        finally
        {
            c.Execute("UPDATE ref.Setting SET SettingValue = @scope WHERE SettingKey = 'Workbench.Scope'", new { scope = scope ?? "SEATS" });
        }
    }

    [Fact]
    public async Task A_line_of_business_on_a_group_that_is_not_a_tracking_group_is_refused_not_dropped()
    {
        var owner = fx.Seed.As("finopsLead");
        var cmd = new SeatCommands(new CommandRunner(fx.Host.DbFor(owner), new FinOps(owner.PersonId, Governance.Domain.Permissions.RegistryEdit), new AuditWriter(fx.Host.Clock)), fx.Host.Clock, new OutboxWriter());
        var e = await Assert.ThrowsAsync<DomainException>(() => cmd.SaveGroupAsync(Guid.NewGuid(), null,
            new SeatGroupInput("GSGu_MCO_TestTracking", "LICENSE", fx.Seed.FundingOwners["HR"], null, "ON_PREM_AD", false, null, null)));
        Assert.Contains("Only an LOB tracking group belongs to a line of business", e.Message);
    }

    [Fact]
    public async Task A_reservation_date_is_corrected_between_its_neighbors_and_the_history_follows()
    {
        var po = fx.Seed.As("platformOwner");
        var cmd = new SeatCommands(new CommandRunner(fx.Host.DbFor(po), new FinOps(po.PersonId, Governance.Domain.Permissions.RegistryEdit), new AuditWriter(fx.Host.Clock)), fx.Host.Clock, new OutboxWriter());
        var fo = fx.Seed.FundingOwners["HR"];
        using var c = fx.Admin();
        var scope = c.ExecuteScalar<string?>("SELECT SettingValue FROM ref.Setting WHERE SettingKey = 'Workbench.Scope'");
        try
        {
            c.Execute("UPDATE ref.Setting SET SettingValue = 'SEATS' WHERE SettingKey = 'Workbench.Scope'");
            var latest = c.QuerySingle<DateTime>("SELECT EffectiveFrom FROM core.SeatReservation WHERE FundingOwnerId = @fo AND EffectiveTo IS NULL", new { fo });
            var a = latest.AddDays(30);
            var b = latest.AddDays(60);
            await cmd.SetReservationAsync(Guid.NewGuid(), Lob("HR"), 100, a, "first");
            await cmd.SetReservationAsync(Guid.NewGuid(), Lob("HR"), 120, b, "second");
            var mid = c.QuerySingle<Guid>("SELECT PublicId FROM core.SeatReservation WHERE FundingOwnerId = @fo AND EffectiveFrom = @a", new { fo, a });

            // Moved later and to 110 seats: the one before ends the day before, the one after shows 110 as what it replaced.
            await cmd.EditReservationAsync(Guid.NewGuid(), mid, 110, a.AddDays(5), null);
            var rows = c.Query<(int Seats, int? Prev, DateTime From, DateTime? To, string? Notes)>(
                "SELECT Seats, PreviousSeats, EffectiveFrom, EffectiveTo, Notes FROM core.SeatReservation WHERE FundingOwnerId = @fo ORDER BY EffectiveFrom", new { fo }).ToList();
            Assert.Equal(a.AddDays(4), rows[^3].To);
            Assert.Equal((110, a.AddDays(5), (DateTime?)b.AddDays(-1), "first"), (rows[^2].Seats, rows[^2].From, rows[^2].To, rows[^2].Notes));
            Assert.Equal(110, rows[^1].Prev);

            // Not past the figures around it.
            await Assert.ThrowsAsync<DomainException>(() => cmd.EditReservationAsync(Guid.NewGuid(), mid, 110, b, null));
            await Assert.ThrowsAsync<DomainException>(() => cmd.EditReservationAsync(Guid.NewGuid(), mid, 110, latest, null));

            // Deleted from the middle: the figure before runs on to where it ended, the one after shows it as what it replaced.
            var count = rows.Count;
            await cmd.DeleteReservationAsync(Guid.NewGuid(), mid);
            rows = c.Query<(int Seats, int? Prev, DateTime From, DateTime? To, string? Notes)>(
                "SELECT Seats, PreviousSeats, EffectiveFrom, EffectiveTo, Notes FROM core.SeatReservation WHERE FundingOwnerId = @fo ORDER BY EffectiveFrom", new { fo }).ToList();
            Assert.Equal(count - 1, rows.Count);
            Assert.Equal(b.AddDays(-1), rows[^2].To);
            Assert.Equal(rows[^2].Seats, rows[^1].Prev);

            // Deleted the latest: the one before is open again, to now.
            var last = c.QuerySingle<Guid>("SELECT PublicId FROM core.SeatReservation WHERE FundingOwnerId = @fo AND EffectiveFrom = @b", new { fo, b });
            await cmd.DeleteReservationAsync(Guid.NewGuid(), last);
            Assert.Equal(latest, c.QuerySingle<DateTime>("SELECT EffectiveFrom FROM core.SeatReservation WHERE FundingOwnerId = @fo AND EffectiveTo IS NULL", new { fo }));
            await Assert.ThrowsAsync<DomainException>(() => cmd.DeleteReservationAsync(Guid.NewGuid(), last));
        }
        finally
        {
            c.Execute("UPDATE ref.Setting SET SettingValue = @scope WHERE SettingKey = 'Workbench.Scope'", new { scope = scope ?? "SEATS" });
        }
    }

    [Fact]
    public async Task The_business_owner_is_changed_or_cleared_after_the_line_of_business_was_added()
    {
        var finops = fx.Seed.As("finopsLead");
        var cmd = new SeatCommands(new CommandRunner(fx.Host.DbFor(finops), new FinOps(finops.PersonId), new AuditWriter(fx.Host.Clock)), fx.Host.Clock, new OutboxWriter());
        var fo = fx.Seed.FundingOwners["HR"];
        var next = fx.Seed.As("RETAIL.maker1").PersonId;
        using var c = fx.Admin();
        var original = c.ExecuteScalar<int?>("SELECT BusinessOwnerPersonId FROM core.FundingOwner WHERE FundingOwnerId = @fo", new { fo });
        const string active = "SELECT PersonId FROM sec.RoleAssignment WHERE ScopeType = 'FUNDING_OWNER' AND ScopeId = @fo AND RoleCode = 'LOB_BUSINESS_OWNER' AND EffectiveFrom <= @today AND (EffectiveTo IS NULL OR EffectiveTo >= @today)";
        var today = fx.Host.Clock.UtcNow.Date;
        try
        {
            // Changed: the new person holds the owner role from today, the previous one no longer.
            await cmd.SetBusinessOwnerAsync(Guid.NewGuid(), Lob("HR"), next.ToString());
            Assert.Equal(next, c.ExecuteScalar<int?>("SELECT BusinessOwnerPersonId FROM core.FundingOwner WHERE FundingOwnerId = @fo", new { fo }));
            Assert.Equal(new[] { next }, c.Query<int>(active, new { fo, today }).ToArray());

            // Nobody names themselves.
            await Assert.ThrowsAsync<DomainException>(() => cmd.SetBusinessOwnerAsync(Guid.NewGuid(), Lob("HR"), finops.PersonId.ToString()));

            // Cleared: no owner, and no one holds the role.
            await cmd.SetBusinessOwnerAsync(Guid.NewGuid(), Lob("HR"), "");
            Assert.Null(c.ExecuteScalar<int?>("SELECT BusinessOwnerPersonId FROM core.FundingOwner WHERE FundingOwnerId = @fo", new { fo }));
            Assert.Empty(c.Query<int>(active, new { fo, today }));
        }
        finally
        {
            if (original is int o && o != finops.PersonId) await cmd.SetBusinessOwnerAsync(Guid.NewGuid(), Lob("HR"), o.ToString());
        }
    }

    /// <summary>FinOps (registry.finance); another permission stands in for the Platform Owner's (registry.edit).</summary>
    private sealed class FinOps(int personId, string permission = Governance.Domain.Permissions.RegistryFinance) : ICurrentPrincipal
    {
        public bool IsAuthenticated => true;
        public int PersonId { get; } = personId;
        public int? RealPersonId => null;
        public string DisplayName => "finops";
        public bool IsCentral => true;
        public IReadOnlyList<RoleGrant> Roles { get; } = new[] { new RoleGrant(Governance.Domain.Roles.FinOpsLead, ScopeTypes.Tenant, null) };
        public IReadOnlySet<string> AppRoles { get; } = new HashSet<string>();
        public IReadOnlySet<string> Permissions { get; } = new HashSet<string> { permission };
        public IReadOnlyDictionary<int, IReadOnlySet<string>> ScopedPermissions { get; } = new Dictionary<int, IReadOnlySet<string>>();
    }
}
