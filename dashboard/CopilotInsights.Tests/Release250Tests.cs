using CopilotInsights.Data;
using CopilotInsights.Tests.Review2412A;
using Xunit;

namespace CopilotInsights.Tests;

/// <summary>What 2.5.0 changed on the pages, held where a later change would undo it unseen.</summary>
public class Release250Tests
{
    [Fact]
    public void Admin_Health_has_no_CSV_import_step_and_keeps_the_audit_download()
    {
        /* Loading from exports is mostly a first-time job: its row was "Not used" on nearly every site. */
        var admin = Source.Read("Pages", "Admin", "Admin.cs");
        Assert.DoesNotContain("\"CSV import\"", admin);
        Assert.Contains("Try(\"Audit download\"", admin);
    }

    [Fact]
    public void Value_names_open_on_the_values_seen_most_and_unnamed_first_is_one_choice_away()
    {
        var values = Source.Read("Pages", "Admin", "Operations.cs");
        var paging = values.Split('\n').Single(l => l.Contains("[\"unnamed\"] = $\"CASE WHEN {NameExpr} IS NULL"));
        Assert.EndsWith("}, \"most\");", paging.TrimEnd());
        var choices = values.Split('\n').Single(l => l.Contains("[\"most\"] = \"Most seen\""));
        Assert.True(choices.IndexOf("\"Most seen\"", StringComparison.Ordinal) < choices.IndexOf("\"Unnamed first\"", StringComparison.Ordinal), "Most seen comes first in the list");
    }

    [Fact]
    public void Labeled_content_by_unit_names_a_resource_type_from_Admin_Value_names_as_the_other_pages_do()
    {
        var more = Source.Read("Pages", "MoreReports.cs");
        var compliance = more[more.IndexOf("namespace CopilotInsights.Pages.Compliance", StringComparison.Ordinal)..more.IndexOf("namespace CopilotInsights.Pages.Agents", StringComparison.Ordinal)];
        Assert.Contains("LEFT JOIN dash.ValueName AS vn ON vn.ColumnId = 31 AND vn.Value = t.ResourceType", compliance);
        Assert.Contains("COALESCE(vn.Name, t.ResourceType)", compliance);
        Assert.Contains("Type names are set in Admin › Value names", compliance);
    }

    [Fact]
    public void The_levels_are_listed_in_numeric_order_and_the_combined_bar_stays_last()
    {
        Assert.Contains("OrderBy(l => Metrics.LevelOrder(l.Name))", Source.Read("Pages", "Workforce", "Workforce.cs"));
        string[] names = ["Level 10", "Level 2", "Level 11", "Level 9", "(not in the chart)", "Level 3"];
        Assert.Equal(["Level 2", "Level 3", "Level 9", "Level 10", "Level 11", "(not in the chart)"], names.OrderBy(Metrics.LevelOrder).ThenBy(n => n, StringComparer.Ordinal));
    }
}
