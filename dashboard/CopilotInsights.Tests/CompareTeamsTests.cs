using CopilotInsights.Pages;
using CopilotInsights.Pages.Leadership;
using Xunit;

namespace CopilotInsights.Tests;

/// <summary>
/// Leadership > Compare two teams: the page is offered where the other Leadership pages are, and a difference between
/// the two teams says which way it goes.
/// </summary>
public class CompareTeamsTests
{
    [Fact]
    public void The_page_is_in_Leadership_beside_Organization_and_needs_no_role()
    {
        var leadership = Nav.Groups.Single(g => g.Persona == "Leadership");
        var page = leadership.Pages.Single(p => p.Path == "/leadership/compare");
        Assert.Equal("Compare two teams", page.Title);
        Assert.Null(page.Role);
        Assert.Null(leadership.Role);
        var titles = leadership.Pages.Select(p => p.Title).ToList();
        Assert.Equal(titles.IndexOf("Organization") + 1, titles.IndexOf("Compare two teams"));
        Assert.Equal("At a glance", NavText.Heading(page));
        Assert.NotNull(Guidance.For("/leadership/compare"));
    }

    [Fact]
    public void What_to_act_on_follows_the_Overview_and_names_no_one()
    {
        var leadership = Nav.Groups.Single(g => g.Persona == "Leadership");
        var titles = leadership.Pages.Select(p => p.Title).ToList();
        Assert.Equal(titles.IndexOf("Overview") + 1, titles.IndexOf("What to act on"));
        var page = leadership.Pages.Single(p => p.Path == "/leadership/act");
        Assert.Null(page.Role);   // counts only: the names stay on the reports it links to
        Assert.NotNull(Guidance.For("/leadership/act"));
        Assert.NotNull(NavText.What(page));
    }

    [Theory]
    [InlineData(12, "#,0", "12 more")]
    [InlineData(-3, "#,0", "3 fewer")]
    [InlineData(0, "#,0", "the same")]
    [InlineData(0.04, "#,0.0", "the same")]        // rounds to nothing
    [InlineData(-1234.5, "#,0.0", "1,234.5 fewer")]
    public void A_difference_says_which_way_in_words_so_a_spreadsheet_reads_no_formula(double value, string format, string expected) =>
        Assert.Equal(expected, CompareModel.Signed(value, format));

    [Theory]
    [InlineData(0.042, "4.2 points higher")]
    [InlineData(-0.1, "10.0 points lower")]
    [InlineData(0.0004, "the same")]
    public void A_difference_between_two_shares_is_in_percentage_points(double value, string expected) =>
        Assert.Equal(expected, CompareModel.Points(value));
}
