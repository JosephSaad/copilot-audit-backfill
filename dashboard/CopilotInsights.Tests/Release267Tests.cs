using CopilotInsights.Tests.Review2412A;
using Xunit;

namespace CopilotInsights.Tests;

/// <summary>2.6.7: a headline figure opens a page that shows the same number, or has no link.</summary>
public class FigureLinkTests
{
    static string Page(params string[] path) => Source.Read(["Pages", .. path]);

    [Fact]
    public void Need_a_nudge_opens_the_people_it_counts()
    {
        var page = Page("Enablement", "Enablement.cs");
        /* The figure opens its own choice on People by engagement, not the page's default (power users). */
        Assert.Contains("Open(\"/enablement/people\", extra: \"seg=\" + Uri.EscapeDataString(Segments.Nudge))", page);
        Assert.DoesNotContain("Href: Open(\"/enablement/people\")));", page);
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
        var licensing = Page("Licensing", "Licensing.cs");
        var optimize = Page("Forecasting", "Forecasting.cs");
        Assert.Contains(rule, licensing);
        Assert.Contains(rule, optimize);
        Assert.Contains("idle > 0 ? \"crit\" : null, Href: Open(\"/licensing/unused\")));", optimize);
        Assert.Contains("A(\"Unused licenses\", \"/licensing/unused\", Roles.Licensing)", optimize);
        /* The share in use counts every idle license, new or protected, and links nowhere: it is a percentage. */
        Assert.Contains("Pct(Ratio(t.LicensedNow - idle, t.LicensedNow)), $\"used in the last {Ctx.InactiveDays} days\"));", licensing);
    }

    [Fact]
    public void Licenses_and_adoption_open_the_page_that_shows_them()
    {
        /* Licenses held today are on Licensing (License pool counts the add-on's seats, which is fewer). */
        Assert.Contains("used in the period\", Href: Open(\"/licensing\")));", Page("Leadership", "BoardPack.cs"));
        Assert.Contains("Delta(now.LicensedNow, before.LicensedNow), Href: Open(\"/licensing\")));", Page("MoreReports.cs"));
        Assert.Contains("used in the last 30 days\", Href: Open(\"/licensing\")));", Page("Forecasting", "Forecasting.cs"));
        /* Adoption is on Adoption; Usage by license counts enabled accounts only (80.6% against 80.4% in the demo). */
        Assert.DoesNotContain("Open(\"/adoption/by-license\")", Page("Leadership", "Leadership.cs"));
        Assert.Contains("\"of licensed people\", Href: Open(\"/adoption\")));", Page("Leadership", "BoardPack.cs"));
        Assert.Contains("pct: true), Href: Open(\"/adoption\")));", Page("MoreReports.cs"));
        /* People using Copilot is on Use ranking only: no fallback to a page without the number. */
        foreach (var page in new[] { Page("Leadership", "Leadership.cs"), Page("Leadership", "BoardPack.cs"), Page("MoreReports.cs") })
        {
            Assert.Contains("Href: Open(\"/licensing/ranking\")));", page);
            Assert.DoesNotContain("Open(\"/licensing/ranking\") ?? Open(\"/adoption\")", page);
        }
    }

    [Fact]
    public void Figures_counted_another_way_have_no_link()
    {
        /* Chargeback's unused spend is the period's, by another rule than Unused licenses; its licenses held are Usage by license's. */
        var chargeback = Page("Licensing", "Operations.cs");
        Assert.Contains("\"a month, on licenses not used in the period\", licensed - used > 0 ? \"warn\" : null));", chargeback);
        Assert.Contains("Href: Open(\"/adoption/by-license\")));", chargeback);
        /* The agent lists count within the page's own list and filters, so Shared and In production do not open the full lists. */
        var workbook = Page("Agents", "Workbook.cs");
        Assert.DoesNotContain("Open(\"/agents/shared\")", workbook);
        Assert.DoesNotContain("Open(\"/agents/production\")", workbook);
        Assert.Contains("Open(Request.Path, extra: \"only=unused\")", workbook);
    }
}
