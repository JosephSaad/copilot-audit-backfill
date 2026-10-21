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
    public void The_views_built_over_everyone_are_kept_once_worked_out()
    {
        /* At 120 million interactions these took 0.5 to 0.7 s on every visit with no query run: they built an object for
           each of 210,000 people from rows already kept. Each is now kept under the stamp, the context and the live tables. */
        var db = File.ReadAllText(Path.Combine(Root(), "dashboard", "CopilotInsights", "Infrastructure", "Db.cs"));
        Assert.Contains("public async Task<T> KeptAsync<T>(string name, ReportContext c, Func<Task<T>> build) =>", db);
        Assert.Contains("await LiveAsync(string.Join(\" \", DataStamp.VolatileTables))", db);
        var data = Path.Combine(Root(), "dashboard", "CopilotInsights", "Data");
        Assert.Contains("db.KeptAsync(\"CostData.Build\", c, () => BuildFreshAsync(c))", File.ReadAllText(Path.Combine(data, "CostData.cs")));
        Assert.Contains("db.KeptAsync(\"CreditData.Build\", c, () => BuildFreshAsync(c))", File.ReadAllText(Path.Combine(data, "CreditData.cs")));
        Assert.Contains("db.KeptAsync($\"SuiteData.Fit:{scoped}\", c, () => FitFreshAsync(c, scoped))", File.ReadAllText(Path.Combine(data, "SuiteData.cs")));
        /* Credit use's default list is the kept one: never reversed in place, as it is shared. */
        var page = Page("Licensing", "CreditUse.cs");
        Assert.Contains("await Db.KeptAsync(\"CreditUse.Prep\", Ctx,", page);
        Assert.Contains("(pg.Reversed ? Enumerable.Reverse(billed).ToList() : billed)", page);
        Assert.Contains("if (!plain && pg.Reversed) ordered.Reverse();", page);
        var suites = Page("Licensing", "Suites.cs");
        Assert.Contains("await Db.KeptAsync(\"Suites.Prep\", Ctx,", suites);
        Assert.Contains("(pg.Reversed ? Enumerable.Reverse(moves).ToList() : moves)", suites);
    }

    [Fact]
    public void Organization_and_departments_open_with_figures()
    {
        Assert.Contains("Kpis.Add(new(\"Unit with the highest adoption\"", Page("Leadership", "Leadership.cs"));
        Assert.Contains("Kpis.Add(new(\"Department with the lowest adoption\"", Page("Adoption", "Adoption.cs"));
    }
}
