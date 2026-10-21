using Xunit;

namespace CopilotInsights.Tests;

/// <summary>2.7.4: what the full walkthrough of every page found.</summary>
public class Release274Tests
{
    static string Root()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(d.FullName, "CHANGELOG.md"))) d = d.Parent!;
        return d.FullName;
    }

    static string Page(params string[] p) => File.ReadAllText(Path.Combine([Root(), "dashboard", "CopilotInsights", "Pages", .. p]));

    [Theory]
    [InlineData("MoreReports.cs", "CsvTable = t;")]                 // Scorecard
    [InlineData("MoreReports.cs", "CsvTable = t2;")]                // Labeled content by unit
    [InlineData("Leadership/Leadership.cs", "CsvTable = t;")]      // Organization
    [InlineData("Adoption/Adoption.cs", "CsvTable = t;")]          // Departments
    [InlineData("Agents/Agents.cs", "CsvTable = t;")]              // Agents in use
    [InlineData("Enablement/Enablement.cs", "CsvTable = tbl;")]    // Engagement
    [InlineData("Apps/Apps.cs", "CsvTable = t;")]                  // Apps
    public void A_page_main_table_downloads(string file, string set) => Assert.Contains(set, Page(file.Split('/')));

    [Fact]
    public void Targets_onboarding_and_drops_download_their_tables()
    {
        var ops = Page("OperationsReports.cs");
        Assert.Contains("Panels.Add(new(\"Every target\", tb.Render(),", ops);
        Assert.True(System.Text.RegularExpressions.Regex.Matches(ops, @"CsvTable = tb;").Count >= 6);
    }

    [Fact]
    public void Reservation_holders_offers_the_choice_it_needs()
    {
        var page = Page("Licensing", "ReservationHolders.cs");
        Assert.DoesNotContain("Notice = \"Choose an organization on Leadership › Copilot reservations to see its people.\";", page);
        Assert.Contains("Panels.Add(new(\"Choose an organization\"", page);
    }

    [Fact]
    public void Organization_and_departments_open_with_figures()
    {
        Assert.Contains("Kpis.Add(new(\"Unit with the highest adoption\"", Page("Leadership", "Leadership.cs"));
        Assert.Contains("Kpis.Add(new(\"Department with the lowest adoption\"", Page("Adoption", "Adoption.cs"));
    }
}
