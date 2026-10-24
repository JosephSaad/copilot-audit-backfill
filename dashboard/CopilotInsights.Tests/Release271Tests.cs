using CopilotInsights.Data;
using CopilotInsights.Pages.Forecasting;
using CopilotInsights.Tests.Review2412A;
using Xunit;

namespace CopilotInsights.Tests;

/// <summary>2.7.1: one renewal figure, product names for licenses and apps, and the bridge from bought to held.</summary>
public class Release271Tests
{
    static string Root()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(d.FullName, "CHANGELOG.md"))) d = d.Parent!;
        return d.FullName;
    }

    static string Read(params string[] p) => File.ReadAllText(Path.Combine([Root(), .. p]));

    [Theory]
    [InlineData(100, 120, 5, 10, 137)]   // the forecast is larger: 120 × 1.10 = 132, plus 5 waiting
    [InlineData(100, 80, 0, 10, 110)]    // today's use is larger: 100 × 1.10
    [InlineData(100, 80, 0, 0, 100)]     // nothing kept spare
    [InlineData(10, 10, -3, 150, 20)]    // the share is at most 100%, and no demand is less than none
    public void The_renewal_figure_is_the_forecast_or_todays_use_plus_the_spare_share_and_the_demand(int active, int projected, int demand, int spare, int expected) =>
        Assert.Equal(expected, Renewal.Target(active, projected, demand, spare));

    [Fact]
    public void Budget_and_renewal_and_License_optimization_work_it_out_the_same_way()
    {
        /* They disagreed by hundreds of seats: today's seats less revocations and leavers, against a forecast with 10%. */
        var optimize = Source.Read("Pages", "Forecasting", "Forecasting.cs");
        var budget = Source.Read("Pages", "Forecasting", "Planning.cs");
        Assert.Contains("var size = await Renewal.SizeAsync(Db, S.Settings, Ctx, rows);", optimize);
        Assert.DoesNotContain("* 1.10)", optimize);
        Assert.Contains("var size = await Renewal.SizeAsync(Db, S.Settings, whole);", budget);
        Assert.Contains("var keep = Math.Max(0, size.Target - suiteActive);", budget);
        Assert.DoesNotContain("Seats the use supports", budget);
        Assert.Contains("await settings.StringAsync(\"SpareSeatPercent\")", optimize);
    }

    [Fact]
    public void A_license_is_shown_by_its_product_name()
    {
        var names = new SkuNames(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["SPE_E5"] = "Microsoft 365 E5", ["Microsoft_365_Copilot"] = "Microsoft 365 Copilot" });
        Assert.Equal("Microsoft 365 E5", names.Name("spe_e5"));
        Assert.Equal("CUSTOM_SKU", names.Name("CUSTOM_SKU"));
        Assert.Equal("Microsoft 365 Copilot, CUSTOM_SKU", names.Name("Microsoft_365_Copilot, CUSTOM_SKU"));
        Assert.Equal("<span title=\"SPE_E5\">Microsoft 365 E5</span>", names.Cell("SPE_E5").Value);
        Assert.Equal("", SkuNames.None.Name(""));

        var schema = Read("sql", "Create-CopilotDashboardSchema.sql");
        Assert.Contains("CREATE TABLE dash.SkuName", schema);
        Assert.Contains("(N'SPE_E5',                         N'Microsoft 365 E5'),", schema);
        Assert.Contains("WHEN NOT MATCHED THEN INSERT (SkuPartNumber, ProductName, UpdatedBy)", schema);   // an administrator's name is kept
        Assert.DoesNotContain("WHEN MATCHED", schema[schema.IndexOf("MERGE dash.SkuName", StringComparison.Ordinal)..schema.IndexOf("/* Roles granted", StringComparison.Ordinal)]);
        Assert.Contains("GRANT INSERT, UPDATE, DELETE ON dash.SkuName TO CopilotInsightsApp", Read("sql", "Create-CopilotOperationsSchema.sql"));

        foreach (var page in new[] { new[] { "Pages", "Licensing", "Licensing.cs" }, ["Pages", "Licensing", "PayAsYouGo.cs"], ["Pages", "Licensing", "Overlap.cs"], ["Pages", "Licensing", "Operations.cs"], ["Data", "OpsReports.cs"] })
            Assert.Contains("SkuNames.LoadAsync(", Source.Read(page));
        var admin = Source.Read("Pages", "Admin", "Operations.cs");
        Assert.Contains("OnPostSkunameAsync", admin);
        Assert.Contains("License product names", admin);
        Assert.Contains("License product names", Read("docs", "OPERATIONS.md"));
        Assert.StartsWith("The license's part number", CopilotInsights.Infrastructure.FieldTips.For("/admin/values", [])["part"]);
    }

    [Fact]
    public void An_app_is_shown_by_its_name_where_it_was_shown_by_its_code()
    {
        Assert.Contains("(CAST(2 AS SMALLINT), N'BizChat',                   N'Copilot Chat'),", Read("sql", "Create-CopilotOperationsSchema.sql"));
        foreach (var page in new[] { new[] { "Pages", "Leadership", "Leadership.cs" }, ["Pages", "Regions", "Regions.cs"], ["Pages", "Leadership", "Compare.cs"], ["Pages", "PeopleReports.cs"] })
            Assert.Contains("LEFT JOIN dash.ValueName AS vn ON vn.ColumnId = 2 AND vn.Value = a.App", Source.Read(page));
        Assert.DoesNotContain("BizChat are Copilot Chat", Source.Read("Pages", "Leadership", "Leadership.cs"));
        Assert.Contains("\"Employee type\"", Source.Read("Pages", "Licensing", "Operations.cs"));
        Assert.Contains("$\"{suite} ({SkuShown(pattern)}), a user a month\"", Source.Read("Data", "Assumptions.cs"));   // With its SKU
    }

    [Fact]
    public void License_inventory_bridges_the_licenses_bought_to_the_licenses_held()
    {
        var page = Source.Read("Pages", "Licensing", "Licensing.cs");
        Assert.Contains("From licenses bought to licenses held", page);
        Assert.Contains("new(\"Plus: Copilot through a suite\", suiteOnly", page);
        Assert.Contains("var rest = held - assigned - suiteOnly;", page);
        Assert.Contains("if (Ctx.Root is not null)", page);   // the tenant's licenses, shown with the whole organization in view
    }

    [Fact]
    public void The_check_for_a_table_finds_a_view_too()
    {
        /* dash.CopilotSuiteOnly is a view: OBJECT_ID(@t, N'U') never found it, so the leavers counted the people a suite
           covers, and the bridge and the renewal figure found no one holding Copilot through a suite. */
        var ops = Source.Read("Data", "OpsData.cs");
        Assert.Contains("OBJECT_ID(@t) IS NULL", ops);
        Assert.DoesNotContain("OBJECT_ID(@t, N'U')", ops);
    }
}
