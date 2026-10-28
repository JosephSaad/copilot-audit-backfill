using CopilotInsights.Charts;
using Xunit;

namespace CopilotInsights.Tests;

/// <summary>2.7.7: chart names shown whole, roles from groups in Check someone's access, and the report rebuild's index.</summary>
public class Release277Tests
{
    static readonly System.Globalization.CultureInfo Ci = System.Globalization.CultureInfo.InvariantCulture;
    static readonly string[] Weeks = ["w/e 4 Sep", "w/e 11 Sep", "w/e 18 Sep", "w/e 25 Sep", "w/e 2 Oct", "w/e 9 Oct"];

    static string Root()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(d.FullName, "CHANGELOG.md"))) d = d.Parent!;
        return d.FullName;
    }

    static string Heat(string[] rows, int width) =>
        Assert.IsType<ChartSvg>(Chart.Heatmap(rows, Weeks, new double?[rows.Length, Weeks.Length], Fmt.Percent, "t", 1)).Render(width);

    /// <summary>Each line of each row name: where it starts (right-aligned, at about 6.9 units a character) and its text.</summary>
    static List<(double Start, string Text)> RowLines(string svg) =>
        System.Text.RegularExpressions.Regex.Matches(svg, "<text class=\"blabel\" x=\"([0-9.]+)\"[^>]*>(.*?)</text>").SelectMany(m =>
        {
            var x = double.Parse(m.Groups[1].Value, Ci);
            var spans = System.Text.RegularExpressions.Regex.Matches(m.Groups[2].Value, "<tspan[^>]*>([^<]*)</tspan>").Select(s => s.Groups[1].Value).ToList();
            return (spans.Count > 0 ? spans : [m.Groups[2].Value]).Select(t => (x - t.Length * 6.9, t));
        }).ToList();

    [Theory]
    [InlineData(320)]
    [InlineData(390)]
    [InlineData(720)]
    public void A_heatmap_row_name_wraps_whole_inside_the_chart(int width)
    {
        string[] rows = ["Learning", "Employee Experience", "Information Technology Infrastructure and Operations, Global Shared Services"];
        var svg = Heat(rows, width);
        var lines = RowLines(svg);
        Assert.DoesNotContain("…", svg);
        Assert.All(lines, l => Assert.True(l.Start >= 0, $"\"{l.Text}\" starts at {l.Start:0.#} at {width}"));
        Assert.Equal(string.Join(" ", rows), string.Join(" ", lines.Select(l => l.Text)));   // every word of every name, in order
    }

    [Fact]
    public void Short_heatmap_row_names_draw_as_before()
    {
        var svg = Heat(["Learning", "Total Rewards"], 720);
        Assert.Contains("<text class=\"blabel\" x=\"102\" y=\"35\" text-anchor=\"end\">Learning</text>", svg);
        Assert.Contains("height=\"22\"", svg);
    }

    [Fact]
    public void A_long_trend_line_name_goes_to_the_legend_whole()
    {
        var name = "Microsoft 365 Copilot Chat in Teams, Outlook and the Microsoft 365 app, all regions";
        var chart = Assert.IsType<ChartSvg>(Chart.Line(Weeks, [new Series(name, [1, 2, 3, 4, 5, 6], 0)], Fmt.Count, "t"));
        var svg = chart.Render(720);
        Assert.DoesNotContain("…", svg);
        Assert.Contains($"</i>{name}</span>", svg);   // the legend
        Assert.Contains("<text class=\"dlabel\" x=\"", svg);
        Assert.DoesNotContain($"{name} 6</text>", svg);
    }

    [Fact]
    public void A_short_trend_line_name_stays_beside_its_line()
    {
        var chart = Assert.IsType<ChartSvg>(Chart.Line(Weeks, [new Series("Teams", [1, 2, 3, 4, 5, 6], 0)], Fmt.Count, "t"));
        var svg = chart.Render(720);
        Assert.Contains(">Teams 6</text>", svg);
        Assert.DoesNotContain("class=\"legend\"", svg);
    }

    [Fact]
    public void No_chart_cuts_a_name()
    {
        var src = File.ReadAllText(Path.Combine(Root(), "dashboard", "CopilotInsights", "Charts", "Chart.cs"));
        Assert.DoesNotContain("static string Trunc(", src);
        Assert.DoesNotContain("Cut(", src);
    }

    [Fact]
    public void Check_someones_access_lists_roles_from_groups()
    {
        var admin = File.ReadAllText(Path.Combine(Root(), "dashboard", "CopilotInsights", "Pages", "Admin", "Admin.cs"));
        Assert.Contains("JOIN audit.GroupMember AS gm ON gm.GroupName = g.Principal", admin);
        Assert.Contains("Roles from groups", admin);
        Assert.DoesNotContain("Group grants are resolved when they sign in, so check the groups above too", admin);
    }

    [Fact]
    public void The_directory_load_expands_every_granted_group()
    {
        var export = File.ReadAllText(Path.Combine(Root(), "tools", "Export-UsersWithHierarchy-ToSql.ps1"));
        var fn = export[export.IndexOf("function Get-OrganizationGroup", StringComparison.Ordinal)..export.IndexOf("function Write-GroupMember", StringComparison.Ordinal)];
        Assert.DoesNotContain("Role = ''Organization''", fn);
        Assert.Contains("PrincipalType = ''Group'' OR Principal LIKE N''%\\%''", fn);
        /* Power BI's row-level security still counts only Organization grants. */
        var ops = File.ReadAllText(Path.Combine(Root(), "sql", "Create-CopilotOperationsSchema.sql"));
        Assert.Contains("JOIN dash.RoleGrant AS g ON g.Principal = gm.GroupName AND g.Role = ''Organization''", ops);
    }

    [Fact]
    public void A_report_rebuild_indexes_the_months_interactions()
    {
        var bi = File.ReadAllText(Path.Combine(Root(), "sql", "Create-CopilotBiProcedure.sql"));
        Assert.Contains("CREATE CLUSTERED INDEX IX_src_RecordId ON #src (RecordId);", bi);
        Assert.True(bi.IndexOf("IX_src_RecordId ON #src", StringComparison.Ordinal) < bi.IndexOf("INSERT INTO #rsrc", StringComparison.Ordinal));
        Assert.Contains("UPDATE STATISTICS bi.DimResourceAction;", bi);
        Assert.DoesNotContain("2.6.6:", bi);
    }
}
