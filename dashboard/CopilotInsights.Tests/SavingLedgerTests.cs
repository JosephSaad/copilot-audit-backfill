using CopilotInsights.Data;
using Xunit;

namespace CopilotInsights.Tests;

/// <summary>
/// The saving ledger: the savings found go in as one row an area, confidence and page, never the ways to grow the
/// return; the start page and the weekly report word saved and found the same way, and each figure opens a page that
/// shows it.
/// </summary>
public class SavingLedgerTests
{
    static string Repo(params string[] path)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine([d.FullName, .. path]))) return File.ReadAllText(Path.Combine([d.FullName, .. path]));
        throw new FileNotFoundException(string.Join('/', path));
    }

    static Opportunity Save(string area, string conf, decimal saving, string? href, string title = "t") =>
        new(area, OpportunityKind.Save, title, "e", "a", saving, 0, conf, href);

    [Fact]
    public void Only_savings_go_in_one_row_a_page_and_confidence()
    {
        var rows = SavingLedger.FoundRows(
        [
            Save(CostAreas.Copilot, "High", 300, "/forecasting/actions?kind=revoke", "Revoke 10"),
            Save(CostAreas.Copilot, "High", 60, "/licensing/leavers", "Take back 2"),
            Save(CostAreas.Copilot, "High", 40.004m, "/licensing/leavers", "Held twice"),
            Save(CostAreas.Copilot, "Medium", 90, "/licensing/payg"),
            Save(CostAreas.Copilot, "Low", 0, "/licensing/nothing"),
            new(CostAreas.Copilot, OpportunityKind.Grow, "Train 5", "e", "a", 0, 150, "Medium", "/forecasting/actions?kind=train"),
            Save(CostAreas.Fabric, "High", 1000, null, "Pause a capacity"),
        ]);
        Assert.Equal(4, rows.Count);
        var leavers = Assert.Single(rows, r => r.Source == "/licensing/leavers");
        Assert.Equal(100.00m, leavers.Amount);
        Assert.Equal("Take back 2; Held twice", leavers.Title);
        Assert.DoesNotContain(rows, r => r.Source.Contains("train") || r.Source == "/licensing/nothing");
        /* A saving with no page of its own keeps its title as its source. */
        Assert.Contains(rows, r => r.Area == CostAreas.Fabric && r.Source == "Pause a capacity" && r.Confidence == "High");
        Assert.Equal(1400m, rows.Where(r => r.Confidence == "High").Sum(r => r.Amount));
    }

    [Fact]
    public void Saved_and_found_are_worded_apart()
    {
        Assert.Equal("4,320 USD saved to date through this dashboard, and 1,250 USD a month more found with high confidence.",
            SavingLedger.Line(new("USD", 4320, new DateTime(2026, 9, 1), 3, 1250, 2000, new DateTime(2026, 11, 1))));
        Assert.Equal("Nothing saved through this dashboard yet, and 90 USD a month more found with high confidence.",
            SavingLedger.Line(new("USD", 0, null, 0, 90, 90, new DateTime(2026, 11, 1))));
        Assert.Equal("540 EUR saved to date through this dashboard.", SavingLedger.Line(new("EUR", 540, new DateTime(2026, 9, 1), 2, 0, 300, null)));
        Assert.False(new LedgerTotals("USD", 0, null, 0, 0, 0, null).Any);
    }

    [Fact]
    public void The_figures_open_costs_and_savings_which_shows_them()
    {
        var home = Repo("dashboard", "CopilotInsights", "Pages", "Index.cshtml");
        Assert.Contains("href=\"/leadership/costs\"", home);
        Assert.Contains("href=\"/leadership/costs?conf=High#savings\"", home);
        var costs = Repo("dashboard", "CopilotInsights", "Pages", "Leadership", "Costs.cs");
        Assert.Contains("saved to date since", costs);
        /* Found is written for the whole organization only, never a team's share. */
        Assert.Contains("if (c.Root is null) await SavingLedger.RecordFoundAsync", Repo("dashboard", "CopilotInsights", "Data", "CostData.cs"));
    }

    [Fact]
    public void The_ledger_is_installed_filled_and_granted()
    {
        var ops = Repo("sql", "Create-CopilotOperationsSchema.sql");
        Assert.Contains("CREATE TABLE dash.SavingLedger", ops);
        Assert.Contains("CREATE OR ALTER PROCEDURE dash.usp_RefreshSavingLedger", ops);
        Assert.Contains("GRANT INSERT, UPDATE, DELETE ON dash.SavingLedger TO CopilotInsightsApp;", ops);
        Assert.Contains("EXEC dash.usp_RefreshSavingLedger;", Repo("sql", "Create-CopilotDashboardProcedure.sql"));
        var weekly = Repo("tools", "Send-CopilotWeeklyReport.ps1");
        Assert.Contains("WeeklyReportSavings", weekly);
        Assert.Contains("saved to date through the Copilot dashboard", weekly);
    }
}
