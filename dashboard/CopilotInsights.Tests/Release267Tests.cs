using CopilotInsights.Tests.Review2412A;
using Xunit;

namespace CopilotInsights.Tests;

/// <summary>2.6.7: a headline figure opens a page that shows the same number, or the list it was counted from.</summary>
public class FigureLinkTests
{
    [Fact]
    public void Need_a_nudge_opens_the_people_it_counts()
    {
        var page = Source.Read("Pages", "Enablement", "Enablement.cs");
        /* The figure opens its own choice on People by engagement, not the page's default (power users). */
        Assert.Contains("Open(\"/enablement/people\", extra: \"seg=\" + Uri.EscapeDataString(Segments.Nudge))", page);
        Assert.Contains("segParam == Segments.Nudge", page);
        /* The list takes one-off and not started together, with the protected levels kept out, as the figure counts them. */
        Assert.Contains("@seg = @nudge AND p.Segment IN (N'One-off', N'Not started')", page);
        Assert.Contains("WHERE p.Segment IN (N'One-off', N'Not started') AND", page);
        Assert.Contains("@nudged = 0 OR", page);
    }

    [Fact]
    public void Unused_licenses_are_counted_one_way()
    {
        /* Licensing, License optimization and Unused licenses: a license newer than the inactive days is not idle yet. */
        var rule = "du.LicensedSinceUtc IS NULL OR du.LicensedSinceUtc <= DATEADD(DAY, -@idle, @d1)";
        Assert.Contains(rule, Source.Read("Pages", "Licensing", "Licensing.cs"));
        Assert.Contains(rule, Source.Read("Pages", "Forecasting", "Forecasting.cs"));
        Assert.Contains("Href: Open(\"/licensing/unused\")", Source.Read("Pages", "Forecasting", "Forecasting.cs"));
    }

    [Fact]
    public void Headline_figures_open_a_page_with_the_same_number()
    {
        /* Licenses held today are on Licensing; License pool counts the add-on's seats, which is fewer. */
        foreach (var (dir, file) in new[] { ("Leadership", "BoardPack.cs"), ("Forecasting", "Forecasting.cs") })
            Assert.DoesNotContain("used in the last 30 days\", Href: Open(\"/licensing/pool\")", Source.Read("Pages", dir, file));
        Assert.Contains("used in the period\", Href: Open(\"/licensing\")", Source.Read("Pages", "Leadership", "BoardPack.cs"));
        /* Adoption is on Adoption; Usage by license counts enabled accounts only. */
        foreach (var (dir, file) in new[] { ("Leadership", "Leadership.cs"), ("Leadership", "BoardPack.cs"), ("", "MoreReports.cs") })
            Assert.DoesNotContain("Open(\"/adoption/by-license\")", dir == "" ? Source.Read("Pages", file) : Source.Read("Pages", dir, file));
    }
}
