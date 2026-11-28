using Governance.Web.Infrastructure;
using Xunit;

namespace CopilotInsights.Tests;

/// <summary>3.7.0 (first planned as 3.6.4): inactive seats counted by the sign-in the license read has, column lines and sorting on the seat tables, Duplicate review.</summary>
public class Release364Tests
{
    static string Repo(params string[] path)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine([d.FullName, .. path]))) return File.ReadAllText(Path.Combine([d.FullName, .. path]));
        throw new FileNotFoundException(string.Join('/', path));
    }

    static string Page(params string[] p) => Repo(["dashboard", "CopilotInsights", "Pages", "Workbench", .. p]);

    [Fact]
    public void Inactive_seats_match_activity_by_the_sign_in_the_license_read_has()
    {
        // Matching on sec.Person alone left most holders without activity, so nearly all showed as inactive.
        var q = Repo("governance", "src", "Governance.Application", "Queries", "SeatQueries.cs");
        Assert.Contains("LEFT JOIN intg.CopilotActivity a ON a.UserPrincipalName = COALESCE(pe.UserPrincipalName, x.UserPrincipalName)", q);
        Assert.DoesNotContain("LEFT JOIN intg.CopilotActivity a ON a.UserPrincipalName = pe.UserPrincipalName", q);
    }

    [Fact]
    public void Lists_sort_the_whole_list_by_a_heading_with_empty_values_last()
    {
        var rows = new[] { ("b", (int?)2), ("a", null), ("C", 1) };
        var keys = new Dictionary<string, Func<(string Name, int? N), IComparable?>> { ["name"] = r => r.Name, ["n"] = r => r.N };
        Assert.Equal(["a", "b", "C"], ListSort.By(rows, "name", false, keys).Select(r => r.Item1));
        Assert.Equal(["C", "b", "a"], ListSort.By(rows, "name", true, keys).Select(r => r.Item1));
        Assert.Equal(["C", "b", "a"], ListSort.By(rows, "n", false, keys).Select(r => r.Item1));   // empty last
        Assert.Equal(["b", "C", "a"], ListSort.By(rows, "n", true, keys).Select(r => r.Item1));    // empty last either way
        Assert.Equal(["b", "a", "C"], ListSort.By(rows, "unknown", false, keys).Select(r => r.Item1));
    }

    [Fact]
    public void Seat_tables_have_column_lines_and_sort_by_their_headings()
    {
        var css = Repo("dashboard", "CopilotInsights", "wwwroot", "workbench.css");
        Assert.Contains(".wb th + th, .wb td + td", css);
        var js = Repo("dashboard", "CopilotInsights", "wwwroot", "workbench.js");
        Assert.Contains("table.classList.contains('sortable')", js);
        Assert.DoesNotContain(js, c => c > 127);   // the client's servers re-encode files: keep the script ASCII
        Assert.Contains("<table class=\"stack sortable\">", Page("Registry", "Seats.cshtml"));
        Assert.Contains("<table class=\"stack resv sortable\">", Page("SeatReports", "ReservationSummary.cshtml"));
        Assert.Contains("model='(\"person\", \"Person\"", Page("Registry", "SeatLob.cshtml"));
        Assert.Contains("model='(\"status\", \"Primary audit status\"", Page("SeatReports", "SeatAuditDetail.cshtml"));
        // A heading's first value is its sort key, never a style: "num" made a sort link that sorted nothing.
        foreach (var f in new[] { "ReservationSummary.cshtml", "SeatAudit.cshtml", "SeatMapping.cshtml", "SeatMultipleGroups.cshtml" })
            Assert.DoesNotContain("model='(\"num\"", Page("SeatReports", f));
    }

    [Fact]
    public void Multiple_groups_is_called_Duplicate_review()
    {
        Assert.Contains("ViewData[\"Title\"] = \"Duplicate review\";", Page("SeatReports", "SeatMultipleGroups.cshtml"));
        Assert.Contains("new(\"Duplicate review\", $\"{P}/Reports/SeatMultipleGroups\")", Repo("dashboard", "CopilotInsights", "Workbench", "WorkbenchNav.cs"));
    }
}
