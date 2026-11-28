using Governance.Web.Infrastructure;
using Governance.Web.Pages.Reports;
using Xunit;
using P = Governance.Web.Infrastructure.SeatAuditRules.Person;
using M = Governance.Web.Infrastructure.SeatAuditRules.Mapping;

namespace CopilotInsights.Tests;

/// <summary>
/// 3.6.3: Copilot seat reports (reservation summary, tracking group and manager hierarchy audit, multiple groups),
/// the Platform Owner alone changing reservations, and Workbench lists that page.
/// </summary>
public class Release363Tests
{
    static string Repo(params string[] path)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine([d.FullName, .. path]))) return File.ReadAllText(Path.Combine([d.FullName, .. path]));
        throw new FileNotFoundException(string.Join('/', path));
    }

    static readonly DateTime Today = new(2026, 11, 28);
    static readonly M Finance = new(Guid.NewGuid(), "MCO.Finance Copilot License Tracking", "Finance", "robert@contoso.com", 2);
    static readonly M Cib = new(Guid.NewGuid(), "MCO.CIB Copilot License Tracking", "CIB", "michael@contoso.com", 2);
    static readonly M Regional = new(Guid.NewGuid(), "MCO.Regional President Copilot License Tracking", "Regional", "louis@contoso.com", 3);
    static readonly M Unmapped = new(Guid.NewGuid(), "MCO.New Copilot License Tracking", "New", null, null);
    static readonly IReadOnlyList<M> All = [Finance, Cib, Regional, Unmapped];

    static List<P> Chain(params (string Upn, int? Level)[] up) => up.Select(u => new P(u.Upn, u.Upn.Split('@')[0], u.Level)).ToList();
    static readonly P Ceo = new("ceo@contoso.com", "ceo", 1);

    [Fact]
    public void The_person_at_the_expected_level_decides_pass_or_review()
    {
        // Passes: Robert is at level 2 in the chain.
        var pass = SeatAuditRules.Evaluate(Finance, [.. Chain(("ann@contoso.com", 4), ("boss@contoso.com", 3), ("robert@contoso.com", 2)), Ceo], null, Today, 90, All);
        Assert.Equal(SeatAuditRules.Pass, pass.Status);
        Assert.Equal("robert@contoso.com", pass.AtLevel!.Upn);

        // Another mapped leader at that group's level: possibly the wrong tracking group, with the group to look at.
        var wrong = SeatAuditRules.Evaluate(Finance, [.. Chain(("bob@contoso.com", 4), ("x@contoso.com", 3), ("michael@contoso.com", 2)), Ceo], null, Today, 90, All);
        Assert.Equal(SeatAuditRules.IncorrectGroup, wrong.Status);
        Assert.Equal(Cib, wrong.Potential);
        Assert.Equal("michael@contoso.com", wrong.AtLevel!.Upn);

        // The deepest matching mapping wins: a regional president at level 3 under Michael.
        var regional = SeatAuditRules.Evaluate(Finance, [.. Chain(("cat@contoso.com", 4), ("louis@contoso.com", 3), ("michael@contoso.com", 2)), Ceo], null, Today, 90, All);
        Assert.Equal(Regional, regional.Potential);

        // The expected leader in the chain, but at another level.
        var level = SeatAuditRules.Evaluate(Regional, [.. Chain(("dan@contoso.com", 4), ("hal@contoso.com", 3), ("louis@contoso.com", 2)), Ceo], null, Today, 90, All);
        Assert.Equal(SeatAuditRules.DifferentLevel, level.Status);

        // Nobody mapped fits.
        var mismatch = SeatAuditRules.Evaluate(Finance, [.. Chain(("fay@contoso.com", 3), ("zed@contoso.com", 2)), Ceo], null, Today, 90, All);
        Assert.Equal(SeatAuditRules.Mismatch, mismatch.Status);
    }

    [Fact]
    public void New_hires_unknown_groups_and_missing_data_are_told_apart()
    {
        Assert.Equal(SeatAuditRules.NewHire, SeatAuditRules.Evaluate(Finance, [], Today.AddDays(-30), Today, 90, All).Status);
        Assert.NotEqual(SeatAuditRules.NewHire, SeatAuditRules.Evaluate(Finance, [], Today.AddDays(-91), Today, 90, All).Status);
        Assert.Equal(SeatAuditRules.UnknownGroup, SeatAuditRules.Evaluate(Unmapped, [Ceo], null, Today, 90, All).Status);
        Assert.Equal(SeatAuditRules.MissingData, SeatAuditRules.Evaluate(Finance, [], null, Today, 90, All).Status);
        Assert.Equal(SeatAuditRules.MissingData, SeatAuditRules.Evaluate(Finance, Chain(("lost@contoso.com", null)), null, Today, 90, All).Status);
        Assert.Equal(SeatAuditRules.Pass, SeatAuditRules.Evaluate(Finance, [Ceo], null, Today, 90, All).Status);   // the top of the organization
    }

    [Fact]
    public void The_summary_reconciles_to_zero_and_counts_the_pass_rate_over_the_validated_population()
    {
        SeatAuditRow Row(string status, Guid? who = null) => new() { MemberObjectId = who ?? Guid.NewGuid(), Name = "x", Status = status, GroupName = "G" };
        var twice = Guid.NewGuid();
        var rows = new List<SeatAuditRow> { Row(SeatAuditRules.Pass), Row(SeatAuditRules.Pass), Row(SeatAuditRules.Pass), Row(SeatAuditRules.NewHire), Row(SeatAuditRules.IncorrectGroup, twice), Row(SeatAuditRules.Pass, twice) };
        var m = SeatAuditModel.Summarize(rows, 90).ToDictionary(x => x.Name, x => x.Value);
        Assert.Equal("6", m["Total seat holders audited"]);
        Assert.Equal("1", m["Total REVIEW requiring investigation"]);
        Assert.Equal("5", m["Validated population"]);
        Assert.Equal("80.0%", m["Validated pass rate"]);
        Assert.Equal("0", m["Reconciliation difference"]);
        Assert.Equal("2", m["Possible duplicate holders"]);
        Assert.Equal(["review"], SeatAuditDetailModel.Filter(rows, "review", null, null).Select(_ => "review").Distinct());
        Assert.Single(SeatAuditDetailModel.Filter(rows, "newhire", null, null));
    }

    [Fact]
    public void The_reservation_summary_pays_the_reservation_and_every_seat_above_it()
    {
        var lines = ReservationSummaryModel.Build(
        [
            new() { FundingOwnerId = 1, Name = "Over", ReservedSeats = 750, AssignedSeats = 799 },
            new() { FundingOwnerId = 2, Name = "Under", ReservedSeats = 4361, AssignedSeats = 4025 },
            new() { FundingOwnerId = null, Name = "No LOB", AssignedSeats = 40 },
        ], 18m);
        Assert.Equal(2, lines.Count);   // seats no tracking group claims are not a line of business
        var over = lines.Single(l => l.Name == "Over");
        Assert.Equal((-49, 49, 13_500m, 162_000m, 14_382m, 882m), (over.Remaining, over.OverUnder, over.MonthlyReserved, over.YearReserved, over.MonthlyActual, over.MonthlyOverage));
        var under = lines.Single(l => l.Name == "Under");
        Assert.Equal((78_498m, 0m, "under"), (under.MonthlyActual, under.MonthlyOverage, under.State));
    }

    [Fact]
    public void Reservations_are_the_platform_owners_alone_and_can_be_edited_and_deleted()
    {
        var commands = Repo("governance", "src", "Governance.Application", "Commands", "SeatCommands.cs");
        Assert.Contains("public Task<string?> EditReservationAsync(", commands);
        Assert.Contains("public Task<string?> DeleteReservationAsync(", commands);
        Assert.Contains("throw new DomainException(\"Only the Platform Owner sets or corrects a line of business's reservation.\");", commands);
        Assert.Contains("public bool CanSetReservation => SeatsOnly && Principal.HasRole(Roles.PlatformOwner);", Repo("dashboard", "CopilotInsights", "Pages", "Workbench", "Registry", "Seats.cshtml.cs"));
    }

    [Fact]
    public void Workbench_lists_read_the_page_from_the_query_string_and_the_sync_limit_counts_runs_per_person()
    {
        // Razor Pages keeps "page" for the page's own route value, so a bound PageNo never saw ?page=.
        var web = Repo("dashboard", "CopilotInsights", "Workbench", "Web.cs");
        Assert.DoesNotContain("[BindProperty(SupportsGet = true, Name = \"page\")]", web);
        Assert.Contains("Request.Query[\"page\"]", web);
        var setup = Repo("dashboard", "CopilotInsights", "Workbench", "WorkbenchSetup.cs");
        Assert.Contains("RateLimitPartition.GetNoLimiter(\"view\")", setup);
        Assert.DoesNotContain("o.AddFixedWindowLimiter(\"sync\"", setup);
    }

    [Fact]
    public void The_new_reports_are_open_with_seats_only_and_in_its_menu()
    {
        foreach (var p in new[] { "/workbench/Reports/ReservationSummary", "/workbench/Reports/SeatAudit/Detail", "/workbench/Reports/SeatMultipleGroups", "/workbench/Admin/SeatMapping" })
            Assert.True(Governance.Application.WorkbenchScope.PageOpen(p, true), p);
        var nav = Repo("dashboard", "CopilotInsights", "Workbench", "WorkbenchNav.cs");
        foreach (var t in new[] { "\"Reservation summary\"", "\"Hierarchy audit\"", "\"Exception report\"", "\"New hire report\"", "\"Detailed audit\"", "\"Multiple groups\"", "\"Hierarchy mapping\"" })
            Assert.Contains(t, nav);
    }
}
