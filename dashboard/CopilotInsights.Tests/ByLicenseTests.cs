using CopilotInsights.Infrastructure;
using CopilotInsights.Pages;
using CopilotInsights.Pages.Adoption;
using Xunit;

namespace CopilotInsights.Tests;

/// <summary>Usage by license: the license choice, the active-days bands, and who may open the page.</summary>
public class ByLicenseTests
{
    [Theory]
    [InlineData(null, "licensed")]
    [InlineData("", "licensed")]
    [InlineData("licensed", "licensed")]
    [InlineData("unlicensed", "unlicensed")]
    [InlineData("all", "all")]
    [InlineData("ALL", "licensed")]
    [InlineData("1; DROP TABLE x", "licensed")]
    public void The_license_choice_is_one_of_three_and_licensed_otherwise(string? asked, string expected) =>
        Assert.Equal(expected, ByLicenseModel.License(asked));

    [Fact]
    public void Licensed_means_a_license_on_the_last_day_and_all_adds_no_condition()
    {
        Assert.Contains("EXISTS (SELECT 1 FROM bi.FactLicensedDay AS ld WHERE ld.DateKey = @k1", ByLicenseModel.LicenseSql("licensed"));
        Assert.DoesNotContain("NOT EXISTS", ByLicenseModel.LicenseSql("licensed"));
        Assert.Contains("NOT EXISTS (SELECT 1 FROM bi.FactLicensedDay", ByLicenseModel.LicenseSql("unlicensed"));
        Assert.Equal("", ByLicenseModel.LicenseSql("all"));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(4, 2)]
    [InlineData(5, 3)]
    [InlineData(9, 3)]
    [InlineData(10, 4)]
    [InlineData(14, 4)]
    [InlineData(15, 5)]
    [InlineData(365, 5)]
    public void Active_days_fall_in_bands(int days, int band) => Assert.Equal(band, ByLicenseModel.Band(days));

    [Fact]
    public void Every_band_has_a_name() => Assert.Equal(6, ByLicenseModel.BandNames.Length);

    [Fact]
    public void The_page_is_in_the_adoption_section_and_needs_the_adoption_role()
    {
        var group = Nav.Groups.Single(g => g.Persona == "Adoption");
        var page = Assert.Single(group.Pages, p => p.Path == "/adoption/by-license");
        Assert.Equal(Roles.Adoption, page.Role);
        Assert.NotNull(Guidance.For("/adoption/by-license"));
    }
}
