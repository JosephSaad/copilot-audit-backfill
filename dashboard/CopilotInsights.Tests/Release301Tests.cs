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
        Assert.Contains("SeatPolicy.PurchaseGateSentence(rules, bought, held)", Read("dashboard", "CopilotInsights", "Pages", "Leadership", "Reservations.Workbench.cs"));
        Assert.Contains("SeatPolicy.PurchaseGateSentence(s.Settings, bought, held)", Read("dashboard", "CopilotInsights", "Pages", "Workbench", "Registry", "Seats.cshtml"));
        Assert.Contains("Seats.NoLobChargedTo", Read("dashboard", "CopilotInsights", "Pages", "Workbench", "Registry", "Seats.cshtml"));
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
}
