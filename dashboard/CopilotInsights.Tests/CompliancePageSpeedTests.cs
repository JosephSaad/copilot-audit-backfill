using Xunit;

namespace CopilotInsights.Tests;

/// <summary>
/// Compliance, Security, Regions and Workforce read the refresh's totals where they answer the same question, so a
/// first visit does not sum every day of the period again.
/// </summary>
public class CompliancePageSpeedTests
{
    static string Root()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(d.FullName, "CHANGELOG.md"))) d = d.Parent!;
        return d.FullName;
    }

    static string Page(params string[] p) => File.ReadAllText(Path.Combine([Root(), "dashboard", "CopilotInsights", "Pages", .. p]));

    [Fact]
    public void Security_reads_the_flagged_interactions_not_the_fact_when_they_are_built()
    {
        var s = Page("Security", "Security.cs");
        Assert.Contains("(\"dash.FlaggedInteraction\", \"DATEPART(HOUR, f.ActivityHour)\") : (\"bi.FactInteraction\", \"f.HourOfDay\")", s);
        Assert.Contains("FROM {Ctx.PerPerson()} AS p {Ctx.Scope(\"p.UserKey\")}", s);
    }

    [Fact]
    public void Access_counts_sites_from_site_person_and_ranks_labels_without_a_join_per_row()
    {
        var c = Page("Compliance", "Compliance.cs");
        Assert.Contains("FROM dash.SitePerson AS sp {Ctx.Scope(\"sp.UserKey\")}", c);
        Assert.Contains("{ReportContext.Reached(\"sp\")}", c);
        Assert.Contains("MAX({rank}) AS TopRank", c);
        /* The fallback before the totals are built still counts the sites from the days. */
        Assert.Contains("COUNT(DISTINCT rd.Item) AS Sites", c);
    }

    [Theory]
    [InlineData("Workforce/Workforce.cs")]
    [InlineData("PeopleReports.cs")]
    public void Per_person_period_sums_come_from_the_totals(string file)
    {
        var s = Page(file.Split('/'));
        Assert.Contains("{Ctx.PerPerson()} AS p", s);
        Assert.DoesNotContain("FROM dash.UserDay AS ud WHERE ud.DateKey BETWEEN @k0 AND @k1 GROUP BY ud.UserKey", s);
    }

    [Fact]
    public void Regions_trend_reads_per_point_and_patterns_reads_side_by_side()
    {
        var r = Page("Regions", "Regions.cs");
        Assert.Contains("FROM {Ctx.PerPoint()} AS p {Ctx.Scope(\"p.UserKey\")}", r);
        Assert.Contains("var (grid, trend, apps) = await Queries(", r);
        Assert.Contains("var (byType, byRole, byLevel, byTitle) = await Queries(", Page("Workforce", "Workforce.cs"));
    }
}
