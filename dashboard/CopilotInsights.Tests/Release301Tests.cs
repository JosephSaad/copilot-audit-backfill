using CopilotInsights.Data;
using Xunit;

namespace CopilotInsights.Tests;

/// <summary>3.0.1: the rest of the 3.0.0 plan: one seat rule set, credits by the Workbench's lines of business, the checks.</summary>
public class Release301Tests
{
    static string Root()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(d.FullName, "CHANGELOG.md"))) d = d.Parent!;
        return d.FullName;
    }

    static string Read(params string[] p) => File.ReadAllText(Path.Combine([Root(), .. p]));

    [Theory]
    [InlineData(1136, 57, 95)]
    [InlineData(1136, 58, 95)]
    [InlineData(200, 25, 90)]
    [InlineData(0, 0, 95)]
    public void One_purchase_gate_for_both_seat_records(int baseline, int now, int percent) =>
        Assert.Equal(Governance.Application.Seats.SeatPolicy.PurchaseGate(baseline, now, percent), ReservationRules.Gate(baseline, now, percent));

    [Fact]
    public void Both_seat_pages_say_the_same_seat_rules()
    {
        var insights = Read("dashboard", "CopilotInsights", "Pages", "Leadership", "Reservations.Workbench.cs");
        var workbench = Read("dashboard", "CopilotInsights", "Pages", "Workbench", "Registry", "Seats.cshtml");
        Assert.Contains("SeatPolicy.PurchaseGateSentence(rules, bought, held, admin)", insights);
        Assert.Contains("SeatPolicy.NoLobSentence(rules, s.NoLobOwnerName, admin)", insights);
        Assert.Contains("SeatPolicy.PurchaseGateSentence(s.Settings, bought, held, Model.Principal.AppRoles.Contains(AppRoles.Admin))", workbench);
        Assert.Contains("SeatPolicy.NoLobSentence(s.Settings, s.NoLobOwnerName, Model.Principal.AppRoles.Contains(AppRoles.Admin))", workbench);
        // Review: the tenant-wide gate only for those who see every line of business, as the tiles beside it.
        var card = workbench[workbench.IndexOf("Finance's choices", StringComparison.Ordinal)..];
        Assert.True(card.IndexOf("@if (Model.SeesAll)", StringComparison.Ordinal) < card.IndexOf("PurchaseGateSentence", StringComparison.Ordinal));
    }

    [Fact]
    public void Credits_are_charged_to_the_Workbench_lines_of_business_when_it_is_on()
    {
        var data = Read("dashboard", "CopilotInsights", "Data", "CreditChargeData.cs");
        Assert.Contains("Workbench ? await lines!.NamesAsync() :", data);                // names
        Assert.Contains("c.Root is null ? null : Workbench ? await lines!.VisibleCodesAsync()", data);   // who sees which
        Assert.Contains("if (Workbench) return await MisroutedByFundingOwnerAsync(c);", data);            // misrouted agents
        Assert.Contains("var known = await lines!.NamesAsync();", data);                                  // unknown codes
        Assert.Contains("WorkbenchLines? lines = null", data);   // the notifier, with no request, keeps the register
        Assert.Contains("(not a Workbench funding owner)", Read("dashboard", "CopilotInsights", "Pages", "Leadership", "CreditChargeback.cs"));
        var lines = Read("dashboard", "CopilotInsights", "Workbench", "WorkbenchLines.cs");
        Assert.Contains("public bool On => state.OnFor(tenants);", lines);
        Assert.Contains("Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1", lines);   // someone in two lines' groups is placed in neither
    }

    [Fact]
    public void The_seat_figures_that_open_Workbench_pages_are_checked()
    {
        Assert.Contains("[string] $ActAs", Read("tools", "dev", "ui", "Test-FigureLinks.ps1"));
        Assert.Contains("-ActAs <persona PublicId>", Read("tools", "dev", "README.md"));
    }

    [Fact]
    public void A_reporting_view_gives_todays_seats_by_line_of_business()
    {
        var sql = Read("governance", "src", "Governance.Database", "Scripts", "070-seats.sql");
        Assert.Contains("CREATE OR ALTER VIEW rpt.vSeatReservationCurrent", sql);
        Assert.Contains("('Seats.NoLobChargedTo',", sql);
        Assert.Contains("('Seats.PurchaseGatePercent',", sql);
    }

    [Fact]
    public void Review_credit_chargeback_speaks_of_the_Workbench_and_survives_its_database()
    {
        var page = Read("dashboard", "CopilotInsights", "Pages", "Leadership", "CreditChargeback.cs");
        Assert.Contains("Routes naming an Org ID that is not a Workbench funding owner", page);
        Assert.Contains("Line(x.EnvOrg)", page);                             // names, not codes
        Assert.Contains("catch (Exception ex) when (data.ByFundingOwner", page);
        var lines = Read("dashboard", "CopilotInsights", "Workbench", "WorkbenchLines.cs");
        Assert.Contains("SeatAccess.Scope(me)", lines);                     // the seat scope, not every role and delegation
        Assert.Contains("TimeSpan.FromMinutes(10)", lines);                 // owners' lines kept, not read on each view
    }

    [Fact]
    public void Review_a_months_credit_snapshot_keeps_the_names_the_Workbench_had()
    {
        var sql = Read("sql", "Create-CopilotOperationsSchema.sql");
        Assert.Contains("CREATE TABLE dash.LineOfBusinessName", sql);
        Assert.Contains("CASE WHEN @model = N''Governance'' THEN (SELECT l.Name FROM dash.LineOfBusinessName AS l WHERE l.OrgId = c.OrgId)", sql);
        Assert.Contains("GRANT INSERT, UPDATE, DELETE ON dash.LineOfBusinessName TO CopilotInsightsApp;", sql);
        Assert.Contains("if (value == Governance) await CopyLineNamesAsync(stoppingToken);", Read("dashboard", "CopilotInsights", "Workbench", "WorkbenchSeats.cs"));
    }

    [Fact]
    public void Review_the_seat_view_sums_to_one_month_and_the_person_link_takes_no_reused_UPN()
    {
        var view = Read("governance", "src", "Governance.Database", "Scripts", "070-seats.sql");
        Assert.Contains("WITH certified AS (SELECT MAX(PeriodMonth) AS PeriodMonth FROM core.SeatChargeSnapshot WHERE Status = 'CERTIFIED')", view);
        var src = Read("governance", "src", "Governance.Application", "Integrations", "InsightsActivity.cs");
        Assert.Contains("AccountEnabled = 1 GROUP BY Upn HAVING COUNT(DISTINCT ObjectId) = 1", src);
    }
}