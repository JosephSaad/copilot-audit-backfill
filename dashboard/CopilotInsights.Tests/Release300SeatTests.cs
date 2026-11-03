using System.Text.RegularExpressions;
using Governance.Application.Queries;
using Governance.Web.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Source = CopilotInsights.Tests.Review2412A.Source;

namespace CopilotInsights.Tests;

/// <summary>3.0.0: one seat model. With the Workbench on, core.SeatReservation is the record and dash.Reservation is kept, unwritten.</summary>
public class Release300SeatTests
{
    static string Root()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(d.FullName, "CHANGELOG.md"))) d = d.Parent!;
        return d.FullName;
    }

    static string Read(params string[] p) => File.ReadAllText(Path.Combine([Root(), .. p]));

    /* ---- The pages switch source on WorkbenchState ---- */

    [Fact]
    public void With_the_Workbench_off_there_are_no_seat_queries_and_the_pages_keep_the_register()
    {
        var one = new CopilotInsights.Infrastructure.TenantAccessor(Microsoft.Extensions.Options.Options.Create(new CopilotInsights.Infrastructure.InsightsOptions { ConnectionString = "x" }));
        var off = new WorkbenchSeats(new ServiceCollection().BuildServiceProvider(), new WorkbenchState(false), one);
        Assert.False(off.On);
        Assert.Null(off.Queries(true));
        Assert.Null(off.Queries(false));
        Assert.True(new WorkbenchSeats(new ServiceCollection().BuildServiceProvider(), new WorkbenchState(true), one).On);
    }

    [Fact]
    public void With_several_tenants_only_the_Workbench_tenant_takes_its_seats()
    {
        var o = new CopilotInsights.Infrastructure.InsightsOptions();
        o.Tenants.Add(new CopilotInsights.Infrastructure.TenantOption { Name = "A", ConnectionString = "a" });
        o.Tenants.Add(new CopilotInsights.Infrastructure.TenantOption { Name = "B", ConnectionString = "b" });
        var tenants = new CopilotInsights.Infrastructure.TenantAccessor(Microsoft.Extensions.Options.Options.Create(o));
        var state = new WorkbenchState(true, "B");
        tenants.Use(tenants.Find("A")!);
        Assert.False(new WorkbenchSeats(new ServiceCollection().BuildServiceProvider(), state, tenants).On);
        tenants.Use(tenants.Find("B")!);
        Assert.True(new WorkbenchSeats(new ServiceCollection().BuildServiceProvider(), state, tenants).On);
        Assert.Equal("A", new WorkbenchState(true).Home(tenants).Name);   // the first tenant unless Workbench:Tenant names one
    }

    [Theory]
    [InlineData("Leadership/Reservations.cs", "if (_seats.On) { await LinesOfBusinessAsync(); return; }")]
    [InlineData("Licensing/ReservationHolders.cs", "if (_seats.On) { await LineOfBusinessAsync(); return; }")]
    public void Each_page_reads_the_Workbench_when_it_is_on_and_the_register_only_when_it_is_off(string file, string turn)
    {
        var page = Source.Read(["Pages", .. file.Split('/')]);
        var at = page.IndexOf(turn, StringComparison.Ordinal);
        Assert.True(at > 0, "the page does not switch on the Workbench");
        /* The switch comes before the first read of the register, so with the Workbench on nothing of it is read. */
        Assert.True(at < page.IndexOf("data.ReadyAsync()", StringComparison.Ordinal));
        Assert.Contains("global::Governance.Web.Infrastructure.WorkbenchSeats seats", page);
    }

    [Theory]
    [InlineData("Leadership/Reservations.Workbench.cs")]
    [InlineData("Licensing/ReservationHolders.Workbench.cs")]
    public void The_line_of_business_views_never_query_the_register(string file)
    {
        var view = Source.Read(["Pages", .. file.Split('/')]);
        Assert.DoesNotMatch(new Regex(@"(FROM|JOIN|INTO|UPDATE)\s+dash\.(Reservation|ReservationRate|ReservationSnapshot|TechnologyAllocation)\b", RegexOptions.IgnoreCase), view);
        Assert.DoesNotContain("ReservationData", view);
        Assert.Contains("Failure(ex, \"reading the Workbench's Copilot seats\")", view);   // a reference on the page, never the error
    }

    [Fact]
    public void A_figure_opens_a_Workbench_page_only_where_the_viewer_sees_the_same_number()
    {
        Assert.Same(SeatLinks.None, WorkbenchSeats.Links(null, true));

        var central = new WebPrincipal { IsAuthenticated = true, IsCentral = true, Permissions = new HashSet<string> { SeatAccess.View }, AppRoles = new HashSet<string> { Governance.Domain.AppRoles.Central } };
        var all = WorkbenchSeats.Links(central, true);
        Assert.Equal("/workbench/Registry/Seats", all.Seats);
        Assert.Equal("/workbench/Registry/Seats?show=nolob#nolob", all.NoLob);
        Assert.Equal("/workbench/Reports/SeatChargeback?month=2026-09", all.Month(new DateTime(2026, 9, 1)));
        var g = Guid.NewGuid();
        Assert.Equal("/workbench/Registry/Seats/" + g, all.Lob(new SeatLobRow { FundingOwnerId = 7, PublicId = g, Name = "Retail" }));

        /* A seat admin for one line of business: the whole organization's figures link nowhere, their own line opens. */
        var admin = new WebPrincipal { IsAuthenticated = true, ScopedPermissions = new Dictionary<int, IReadOnlySet<string>> { [5] = new HashSet<string> { SeatAccess.View } } };
        var mine = WorkbenchSeats.Links(admin, true);
        Assert.Null(mine.Seats);
        Assert.Null(mine.NoLob);
        Assert.Null(mine.Chargeback);
        Assert.NotNull(mine.Lob(new SeatLobRow { FundingOwnerId = 5, PublicId = Guid.NewGuid(), Name = "Mine" }));
        Assert.Null(mine.Lob(new SeatLobRow { FundingOwnerId = 6, PublicId = Guid.NewGuid(), Name = "Theirs" }));
        Assert.Equal("/workbench/Registry/Seats", WorkbenchSeats.Links(admin, false).Seats);   // their own lines, as that page shows them
    }

    [Fact]
    public void The_totals_add_up_as_the_Workbench_seat_list_adds_them()
    {
        var v = new SeatPosition(null, [
            new SeatLobRow { FundingOwnerId = 1, Name = "A", ReservedSeats = 100, AssignedSeats = 80 },
            new SeatLobRow { FundingOwnerId = 2, Name = "B", ReservedSeats = 50, AssignedSeats = 60, OverSeats = 10 },
            new SeatLobRow { FundingOwnerId = null, Name = "No LOB", AssignedSeats = 7 },
        ], "USD", 30m, [
            new SeatMonth(new DateTime(2026, 10, 1), false, 150, 140, 160, 4800m, "USD", []),
            new SeatMonth(new DateTime(2026, 9, 1), true, 150, 130, 155, 4650m, "USD", []),
        ]);
        Assert.Equal(150, v.Reserved);
        Assert.Equal(147, v.Assigned);   // the seats no line of business claims included, as the Workbench's total row
        Assert.Equal(10, v.Over);
        Assert.Equal(20, v.Unused);      // A's unused only: B is over, and the no-LOB row has no reservation
        Assert.Equal(new DateTime(2026, 9, 1), v.LastCertified!.Month);   // a draft month is never the chargeback shown
    }

    /* ---- Nothing writes to dash.Reservation* with the Workbench on ---- */

    [Fact]
    public void Every_change_on_the_reservations_page_is_refused_with_the_Workbench_on()
    {
        var page = Source.Read("Pages", "Leadership", "Reservations.cs");
        var posts = Regex.Matches(page, @"public Task<IActionResult> (OnPost\w+Async)\([^)]*\)\s*=>\s*\w+\(").Select(m => m.Value).ToList();
        Assert.Equal(9, posts.Count);
        Assert.All(posts, p => Assert.EndsWith("RegisterChangeAsync(", p));
        /* AdminChangeAsync is called once, by the guard, which refuses before the change runs. */
        Assert.Single(Regex.Matches(page, @"AdminChangeAsync\("));
        var guard = page[page.IndexOf("Task<IActionResult> RegisterChangeAsync(", StringComparison.Ordinal)..];
        Assert.True(guard.IndexOf("if (_seats.On) throw new ArgumentException(RegisterKept);", StringComparison.Ordinal) < guard.IndexOf("return await change();", StringComparison.Ordinal));
        /* With the Workbench on, the forms are not drawn: the note points to the Workbench's seat pages. */
        var view = Source.Read("Pages", "Leadership", "Reservations.Workbench.cs");
        Assert.Contains("Ownership registry › Copilot seats", view);
        Assert.Contains("Workbench settings › Seat groups", view);
        Assert.DoesNotContain("AdminAsync(", view);
    }

    [Fact]
    public void Only_the_reservations_page_writes_the_register_tables()
    {
        var write = new Regex(@"(INSERT\s+INTO|UPDATE|DELETE\s+FROM|MERGE)\s+dash\.(Reservation|ReservationRate|ReservationSnapshot)\b", RegexOptions.IgnoreCase);
        var dir = Path.Combine(Root(), "dashboard", "CopilotInsights");
        var writers = Directory.EnumerateFiles(dir, "*.cs*", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Where(f => write.IsMatch(File.ReadAllText(f)))
            .Select(f => Path.GetRelativePath(dir, f).Replace('\\', '/')).ToList();
        Assert.Equal(["Pages/Leadership/Reservations.cs"], writers);
    }

    [Fact]
    public void The_nightly_snapshot_adds_nothing_when_the_Workbench_is_the_seat_record()
    {
        var sql = Read("sql", "Create-CopilotOperationsSchema.sql");
        var proc = sql[sql.IndexOf("CREATE OR ALTER PROCEDURE dash.usp_TakeReservationSnapshot", StringComparison.Ordinal)..];
        var stop = proc.IndexOf("IF EXISTS (SELECT 1 FROM dash.Setting WHERE Name = N''SeatModel'' AND Value = N''Governance'') RETURN;", StringComparison.Ordinal);
        Assert.True(stop > 0);
        Assert.True(stop < proc.IndexOf("INSERT INTO dash.ReservationSnapshot", StringComparison.Ordinal));
        /* The setting starts as the leader-based register, so a site without the Workbench keeps its snapshots. */
        Assert.Contains("('SeatModel',              N'Leader',", sql);
        Assert.Equal(("SeatModel", "Governance", "Leader"), (SeatModelSwitch.Name, SeatModelSwitch.Governance, SeatModelSwitch.Leader));
        var setup = Source.Read("Workbench", "WorkbenchSetup.cs");
        Assert.Contains("services.AddHostedService<SeatModelSwitch>();", setup);
        // Governance for the Workbench's own tenant only; every other tenant keeps the register. Set again every quarter hour.
        Assert.Contains("var value = state.On && string.Equals(t.Name, home.Name, StringComparison.OrdinalIgnoreCase) ? Governance : Leader;", Source.Read("Workbench", "WorkbenchSeats.cs"));
        Assert.Contains("TimeSpan.FromMinutes(15)", Source.Read("Workbench", "WorkbenchSeats.cs"));
    }

    [Fact]
    public void No_register_column_or_table_is_dropped_or_changed()
    {
        var sql = Read("sql", "Create-CopilotOperationsSchema.sql");
        Assert.DoesNotMatch(new Regex(@"(DROP\s+TABLE|DROP\s+COLUMN|sp_rename)[^\n]*Reservation", RegexOptions.IgnoreCase), sql);
        Assert.Contains("GRANT INSERT, UPDATE, DELETE ON dash.Reservation TO CopilotInsightsApp;", sql);   // grants stay as they were
    }

    /* ---- "Start from Insights' reservations" ---- */

    static readonly Guid Retail = Guid.NewGuid(), Cards = Guid.NewGuid(), Wealth = Guid.NewGuid();

    static List<ImportOwner> Owners() =>
    [
        new(Retail, "Retail Banking", "RB", false),
        new(Cards, "Cards", "CRD", true),      // already has a seat reservation
        new(Wealth, "Wealth", "WM", false),
    ];

    static List<InsightsReservation> Rows() =>
    [
        new("LOB-01", "Retail Banking", "CC-100", 7403, new DateTime(2026, 1, 1)),
        new("LOB-02", "Cards", null, 900, new DateTime(2026, 1, 1)),
        new("LOB-03", "Wealth East", null, 120, new DateTime(2026, 3, 1)),
        new("LOB-04", "Wealth West", null, 80, new DateTime(2026, 2, 1)),
        new("LOB-05", "Treasury", null, 40, new DateTime(2026, 1, 1)),
    ];

    [Fact]
    public void The_import_skips_a_funding_owner_that_already_has_a_reservation_and_says_so()
    {
        var chosen = new Dictionary<string, Guid> { ["LOB-01"] = Retail, ["LOB-02"] = Cards };
        var (record, skipped) = ReservationImport.Plan(Rows(), chosen, Owners());
        var only = Assert.Single(record);
        Assert.Equal(Retail, only.FundingOwner);
        Assert.Equal(7403, only.Seats);
        Assert.Equal(new DateTime(2026, 1, 1), only.From);
        Assert.Contains("LOB-01", only.Notes);
        Assert.Contains("cost center CC-100", only.Notes);
        var said = Assert.Single(skipped);
        Assert.Contains("Cards already has a seat reservation", said);
    }

    [Fact]
    public void Nothing_is_copied_for_a_row_left_out_and_rows_for_one_owner_are_added_together()
    {
        var (none, noneSkipped) = ReservationImport.Plan(Rows(), new Dictionary<string, Guid>(), Owners());
        Assert.Empty(none);
        Assert.Empty(noneSkipped);

        var (record, skipped) = ReservationImport.Plan(Rows(), new Dictionary<string, Guid> { ["LOB-03"] = Wealth, ["LOB-04"] = Wealth, ["LOB-05"] = Guid.NewGuid() }, Owners());
        var wealth = Assert.Single(record);
        Assert.Equal(200, wealth.Seats);
        Assert.Equal(new DateTime(2026, 2, 1), wealth.From);   // the earliest start
        Assert.Contains("no longer in use", Assert.Single(skipped));   // a funding owner the form named that is gone
    }

    [Fact]
    public void The_import_suggests_a_funding_owner_by_code_then_by_name()
    {
        var owners = Owners();
        Assert.Equal(Retail, ReservationImport.Suggest(new("RB", "Something else", null, 1, DateTime.Today), owners));
        Assert.Equal(Retail, ReservationImport.Suggest(new("LOB-01", "retail banking ", null, 1, DateTime.Today), owners));
        Assert.Null(ReservationImport.Suggest(new("LOB-09", "Treasury", null, 1, DateTime.Today), owners));
    }

    [Fact]
    public void Each_first_reservation_in_a_confirm_has_its_own_key_and_a_second_send_has_the_same()
    {
        var form = Guid.NewGuid();
        Assert.Equal(ReservationImport.Key(form, Retail), ReservationImport.Key(form, Retail));
        Assert.NotEqual(ReservationImport.Key(form, Retail), ReservationImport.Key(form, Wealth));
        Assert.NotEqual(ReservationImport.Key(form, Retail), ReservationImport.Key(Guid.NewGuid(), Retail));
    }

    [Fact]
    public void The_import_goes_through_the_Workbench_command_for_a_first_reservation_and_only_on_confirm()
    {
        var page = Source.Read("Pages", "Workbench", "Admin", "SeatGroups.cshtml.cs");
        var post = page[page.IndexOf("public async Task<IActionResult> OnPostImportAsync(", StringComparison.Ordinal)..];
        Assert.Contains("commands.RecordInitialReservationAsync(ReservationImport.Key(Idem, item.FundingOwner)", post);
        Assert.Contains("ReservationImport.Plan(await import.RowsAsync(), chosen, await import.OwnersAsync())", post);   // read again, not taken from the form
        Assert.Contains("if (!CanImport)", post);
        Assert.DoesNotContain("RecordInitialReservationAsync", page[..page.IndexOf("OnPostImportAsync", StringComparison.Ordinal)]);   // never on a page view
        /* The command refuses a second first reservation, whatever the page shows. */
        Assert.Contains("This LOB already has a seat reservation.", Read("governance", "src", "Governance.Application", "Commands", "SeatCommands.cs"));
        /* The register is only read. */
        Assert.DoesNotMatch(new Regex(@"(INSERT|UPDATE|DELETE|MERGE)[^\n]*dash\.Reservation", RegexOptions.IgnoreCase), Source.Read("Workbench", "ReservationImport.cs"));
        var view = Source.Read("Pages", "Workbench", "Admin", "SeatGroups.cshtml");
        Assert.Contains("asp-page-handler=\"Import\"", view);
        Assert.Contains("data-confirm=", view[view.IndexOf("asp-page-handler=\"Import\"", StringComparison.Ordinal)..]);
    }
}
