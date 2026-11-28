using Governance.Application.Seats;
using Governance.Web.Infrastructure;
using Xunit;
using P = Governance.Web.Infrastructure.SeatAuditRules.Person;
using M = Governance.Web.Infrastructure.SeatAuditRules.Mapping;

namespace CopilotInsights.Tests;

/// <summary>
/// 3.7.0: seats month by month (daily read, peak charge, cumulative overage with a reset, certification, adjustments,
/// month drop-downs), central-cost lines of business, findings counted where they belong, the business owner one click away.
/// </summary>
public class Release370Tests
{
    static string Repo(params string[] path)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine([d.FullName, .. path]))) return File.ReadAllText(Path.Combine([d.FullName, .. path]));
        throw new FileNotFoundException(string.Join('/', path));
    }

    [Fact]
    public void A_line_pays_the_larger_of_its_peak_and_its_reservation_and_the_overage_is_the_peak_above_it()
    {
        Assert.Equal((60, 1800m, 300m), SeatMonths.Price(60, 50, 30m, charged: true));
        Assert.Equal((50, 1500m, 0m), SeatMonths.Price(45, 50, 30m, charged: true));
        Assert.Equal((0, 0m, 0m), SeatMonths.Price(80, 50, 30m, charged: false));   // central cost or No LOB: charged to nobody
    }

    static SeatPeriod Period(int id, string name, DateTime start, DateTime end) => new() { SeatPeriodId = id, Name = name, StartMonth = start, EndMonth = end };

    [Fact]
    public void The_cumulative_overage_counts_from_the_start_of_the_period_and_periods_never_overlap()
    {
        var start = new DateTime(2026, 12, 1);
        var periods = new List<SeatPeriod> { Period(1, "Period 1", new(2026, 12, 1), new(2026, 12, 1)), Period(2, "FY2027", new(2027, 1, 1), new(2027, 12, 1)) };
        Assert.Equal(new DateTime(2026, 12, 1), SeatMonths.CumulativeFrom(new DateTime(2026, 12, 1), start, periods));   // December alone
        Assert.Equal(new DateTime(2027, 1, 1), SeatMonths.CumulativeFrom(new DateTime(2027, 6, 1), start, periods));     // FY2027 from January
        Assert.Equal(new DateTime(2028, 2, 1), SeatMonths.CumulativeFrom(new DateTime(2028, 2, 1), start, periods));     // in no period: on its own
        Assert.Equal("FY2027", SeatMonths.PeriodFor(periods, new DateTime(2027, 3, 1))!.Name);
        Assert.NotNull(SeatMonths.Overlapping(periods, null, new DateTime(2027, 11, 1), new DateTime(2028, 10, 1)));    // overlaps FY2027
        Assert.Null(SeatMonths.Overlapping(periods, null, new DateTime(2028, 1, 1), new DateTime(2028, 12, 1)));
        Assert.Null(SeatMonths.Overlapping(periods, 2, new DateTime(2027, 1, 1), new DateTime(2027, 11, 1)));            // a period does not overlap itself
        Assert.Equal(12, periods[1].MonthCount);
    }
    [Fact]
    public void The_daily_read_is_due_at_eight_in_the_morning_eastern_and_can_be_turned_off()
    {
        var due = SeatDailyRead.DueUtc(new DateTime(2026, 12, 2, 15, 0, 0, DateTimeKind.Utc), "Eastern Standard Time", "08:00");
        Assert.Equal(new DateTime(2026, 12, 2, 13, 0, 0), due);   // 08:00 EST
        var summer = SeatDailyRead.DueUtc(new DateTime(2027, 7, 2, 15, 0, 0, DateTimeKind.Utc), "Eastern Standard Time", "08:00");
        Assert.Equal(new DateTime(2027, 7, 2, 12, 0, 0), summer);   // 08:00 EDT
        Assert.Null(SeatDailyRead.DueUtc(DateTime.UtcNow, "Eastern Standard Time", ""));
    }

    [Fact]
    public void Blank_manager_and_hierarchy_is_a_new_hire_unless_the_hire_date_says_otherwise_and_the_same_leader_is_no_alternative()
    {
        var today = new DateTime(2027, 1, 15);
        var fin = new M(Guid.NewGuid(), "Finance", "Finance", "robert@contoso.com", 2);
        var fin2 = new M(Guid.NewGuid(), "Finance analytics", "Finance", "robert@contoso.com", 2);
        var cib = new M(Guid.NewGuid(), "CIB", "CIB", "michael@contoso.com", 2);
        var blank = new List<P> { new("new@contoso.com", "new", null) };
        Assert.Equal(SeatAuditRules.NewHire, SeatAuditRules.Evaluate(fin, blank, null, today, 90, [fin, cib]).Status);
        Assert.Equal(SeatAuditRules.MissingData, SeatAuditRules.Evaluate(fin, blank, today.AddYears(-3), today, 90, [fin, cib]).Status);
        // In Finance analytics but under Robert: Finance has the same leader, so it is not suggested.
        var chain = new List<P> { new("a@contoso.com", "a", 4), new("b@contoso.com", "b", 3), new("robert@contoso.com", "robert", 2), new("ceo@contoso.com", "ceo", 1) };
        Assert.Equal(SeatAuditRules.Pass, SeatAuditRules.Evaluate(fin2, chain, null, today, 90, [fin, fin2, cib]).Status);
    }

    [Fact]
    public void The_database_step_adds_the_month_tables_the_settings_and_the_web_sites_deletes()
    {
        var seats = Repo("governance", "src", "Governance.Database", "Scripts", "070-seats.sql");
        foreach (var t in new[] { "CREATE TABLE intg.SeatDay", "CREATE TABLE core.SeatMonth", "CREATE TABLE core.SeatMonthCertification", "CREATE TABLE core.SeatAdjustment",
                     "ALTER TABLE core.FundingOwner ADD IsCentralCost bit NULL", "'Seats.TrackingStart',          N'2026-12-01'", "CREATE TABLE core.SeatPeriod", "VALUES (N'Period 1', '2026-12-01', '2026-12-01')",
                     "'Seats.DailyReadTime',          N'08:00'" })
            Assert.Contains(t, seats);
        var grants = Repo("governance", "src", "Governance.Database", "Scripts", "900-grants.sql");
        Assert.Contains("GRANT DELETE ON core.SeatReservation  TO app_web;", grants);   // 3.6.3's Delete needed it
        Assert.Contains("GRANT DELETE ON core.SeatMonth        TO app_web;", grants);
    }

    [Fact]
    public void Each_seat_read_records_the_day_and_the_site_reads_daily_on_one_server()
    {
        Assert.Contains("await Seats.SeatMonths.RecordDayAsync(uow, runId, clock.UtcNow, settings.InactiveDays);",
            Repo("governance", "src", "Governance.Application", "Integrations", "SeatSyncService.cs"));
        Assert.Contains("services.AddHostedService<SeatDailyRead>();", Repo("dashboard", "CopilotInsights", "Workbench", "WorkbenchSetup.cs"));
        Assert.Contains("sp_getapplock @Resource = N'seat-daily-read'", Repo("dashboard", "CopilotInsights", "Workbench", "SeatDailyRead.cs"));
    }

    [Fact]
    public void No_LOB_counts_its_own_findings_and_the_rest_are_tenant_wide()
    {
        var q = Repo("governance", "src", "Governance.Application", "Queries", "SeatQueries.cs");
        Assert.Contains("f.FundingOwnerId IS NULL AND f.FindingType = 'SEAT_NO_LOB'", q);
        Assert.Contains("FindingType <> 'SEAT_NO_LOB' AND FundingOwnerId IS NULL", q);
        Assert.Contains("AND (ISNULL(fo.IsCentralCost, 0) = 0 OR g.ExpectedLeaderUpn IS NOT NULL)", q);   // central cost audited only when mapped
        Assert.True(Governance.Application.WorkbenchScope.PageOpen("/workbench/Operations/Findings", true));
    }

    [Fact]
    public void The_business_owner_and_the_lines_tracking_group_and_reservation_are_one_click_away()
    {
        var seats = Repo("dashboard", "CopilotInsights", "Pages", "Workbench", "Registry", "Seats.cshtml");
        Assert.Contains("_Th\" model='(\"\", \"Business owner\"", seats);
        Assert.Contains("#admins\">@(r.BusinessOwnerName is null ? \"Name one\" : \"Change\")", seats);
        var lob = Repo("dashboard", "CopilotInsights", "Pages", "Workbench", "Registry", "SeatLob.cshtml");
        Assert.Contains("Edit its tracking group", lob);
        Assert.Contains("href=\"#set-reservation\">Set the reservation", lob);
        Assert.Contains("Make it a central cost", Repo("dashboard", "CopilotInsights", "Pages", "Workbench", "Registry", "FundingOwner.cshtml"));
    }

    [Fact]
    public void Money_and_audit_pages_pick_a_month()
    {
        var p = Repo("dashboard", "CopilotInsights", "Pages", "Workbench", "SeatReports", "ReservationSummary.cshtml");
        Assert.Contains("asp-page-handler=\"Certify\"", p);
        Assert.Contains("asp-page-handler=\"Reopen\"", p);
        Assert.Contains("Reconciliation with the bill", p);
        Assert.Contains("(certified)</option>", Repo("dashboard", "CopilotInsights", "Pages", "Workbench", "SeatReports", "SeatAudit.cshtml"));
        Assert.Contains("(certified)</option>", Repo("dashboard", "CopilotInsights", "Pages", "Workbench", "SeatReports", "SeatMultipleGroups.cshtml"));
    }
}

/// <summary>3.7.0: names that say what is under them, the seat section before Admin with its own color, and the brief.</summary>
public class Release370NamesTests
{
    static string Repo(params string[] path)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine([d.FullName, .. path]))) return File.ReadAllText(Path.Combine([d.FullName, .. path]));
        throw new FileNotFoundException(string.Join('/', path));
    }

    [Fact]
    public void Governance_is_named_for_what_it_holds_and_its_roles_show_as_seat_management()
    {
        Assert.Contains(CopilotInsights.Pages.Nav.Groups, g => g.Persona == "AI apps and controls");
        Assert.DoesNotContain(CopilotInsights.Pages.Nav.Groups, g => g.Persona == "Governance");
        Assert.Equal("Seat management", CopilotInsights.Infrastructure.Roles.Title(CopilotInsights.Infrastructure.Roles.Governance));
        Assert.Equal("Seat management operator", CopilotInsights.Infrastructure.Roles.Title(CopilotInsights.Infrastructure.Roles.GovernanceOperator));
        Assert.Equal("Seat management auditor", CopilotInsights.Infrastructure.Roles.Title(CopilotInsights.Infrastructure.Roles.GovernanceAuditor));
        Assert.Equal("Governance", CopilotInsights.Infrastructure.Roles.Governance);   // the stored code is kept: every grant keeps working
        Assert.Contains("{E(Roles.Title(g.Role))}", Repo("dashboard", "CopilotInsights", "Pages", "Admin", "Admin.cs"));
    }

    [Fact]
    public void Copilot_Seat_Management_comes_before_Admin_with_its_own_color()
    {
        Assert.Contains("\"Copilot Seat Management\"", Repo("dashboard", "CopilotInsights", "Workbench", "WorkbenchNav.cs"));
        var layout = Repo("dashboard", "CopilotInsights", "Pages", "Shared", "_Layout.cshtml");
        Assert.Contains("groups.InsertRange(adminAt < 0 ? groups.Count : adminAt,", layout);
        var css = Repo("dashboard", "CopilotInsights", "wwwroot", "site.css");
        Assert.Contains("body[data-section=\"copilot-seat-management\"], [data-grp=\"Copilot Seat Management\"] { --sec:", css);
        Assert.Contains(":root[data-theme=\"dark\"] body[data-section=\"copilot-seat-management\"]", css);
        Assert.Contains(":root:not([data-theme=\"light\"]) body[data-section=\"copilot-seat-management\"]", css);
    }

    [Fact]
    public void The_daily_brief_shows_the_seats_month_to_those_who_open_the_seat_reports()
    {
        var brief = Repo("dashboard", "CopilotInsights", "Pages", "Leadership", "Brief.cs");
        Assert.Contains("if (await SeatsKpiAsync() is { } seatsKpi) Kpis.Add(seatsKpi);", brief);
        Assert.Contains("Copilot seats charged (", brief);
    }
}
