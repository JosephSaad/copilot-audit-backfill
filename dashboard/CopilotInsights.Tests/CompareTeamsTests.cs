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

    [Theory]
    [InlineData(12, "#,0", "+12")]
    [InlineData(-3, "#,0", "-3")]
    [InlineData(0, "#,0", "0")]
    [InlineData(0.04, "#,0.0", "0")]        // rounds to nothing: no sign on a zero
    [InlineData(-1234.5, "#,0.0", "-1,234.5")]
    public void A_difference_carries_its_sign_and_a_zero_has_none(double value, string format, string expected) =>
        Assert.Equal(expected, CompareModel.Signed(value, format));

    [Theory]
    [InlineData(0.042, "+4.2 pts")]
    [InlineData(-0.1, "-10.0 pts")]
    [InlineData(0.0004, "0 pts")]
    public void A_difference_between_two_shares_is_in_percentage_points(double value, string expected) =>
        Assert.Equal(expected, CompareModel.Points(value));
}
