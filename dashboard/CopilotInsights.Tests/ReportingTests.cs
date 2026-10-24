using CopilotInsights.Charts;
using CopilotInsights.Infrastructure;
using CopilotInsights.Pages;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace CopilotInsights.Tests;

public class PagingTests
{
    static readonly Dictionary<string, string> Sorts = new() { ["name"] = "du.DisplayName", ["dept"] = "du.Department" };

    static HttpRequest Q(string query)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.QueryString = new QueryString(query);
        return ctx.Request;
    }

    [Fact]
    public void Pages_fifty_rows_at_a_time()
    {
        var p = new Paging(Q("?p=3"), Sorts, "name");
        Assert.Equal("ORDER BY du.DisplayName OFFSET 100 ROWS FETCH NEXT 50 ROWS ONLY", p.OrderAndPage());
        p.Total = 40_001;
        Assert.Equal(801, p.Pages);
    }

    [Theory]
    [InlineData("?s=du.DisplayName;DROP%20TABLE%20x")]
    [InlineData("?s=")]
    [InlineData("?p=-4&s=nope")]
    public void Only_listed_sorts_and_sane_pages_reach_the_sql(string query)
    {
        var p = new Paging(Q(query), Sorts, "dept");
        Assert.Equal("dept", p.SortKey);
        Assert.Equal(1, p.Page);
        Assert.DoesNotContain("DROP", p.OrderAndPage());
    }

    [Fact]
    public void Search_wildcards_are_literal() =>
        Assert.Equal("%50[%] [_]off[[]1]%", new Paging(Q("?q=50%25%20_off[1]"), Sorts, "name").Like);

    [Fact]
    public void An_export_gets_every_row() =>
        Assert.Equal("ORDER BY du.Department OFFSET 0 ROWS", new Paging(Q("?handler=csv&p=9&s=dept"), Sorts, "name").OrderAndPage());
}

public class ExportTests
{
    [Fact]
    public void Csv_neutralises_spreadsheet_formulas_and_quotes()
    {
        var t = new Table { Headers = ["Name", "Note", "When"] };
        t.Rows.Add(["=HYPERLINK(\"http://x\")", "say \"hi\"", new DateTime(2026, 9, 1)]);
        t.Rows.Add(["+1", "-2", new DateTime(2026, 9, 1, 13, 5, 0)]);
        var lines = t.ToCsv().Split(Environment.NewLine);
        Assert.Equal("\"'=HYPERLINK(\"\"http://x\"\")\",\"say \"\"hi\"\"\",\"2026-09-01\"", lines[1]);
        Assert.Equal("\"'+1\",\"'-2\",\"2026-09-01 13:05:00\"", lines[2]);
    }

    [Fact]
    public void Html_is_encoded_in_tables()
    {
        var t = new Table { Headers = ["Name"] };
        t.Rows.Add(["<script>alert(1)</script>"]);
        Assert.DoesNotContain("<script>", t.Render().Value);
    }
}

public class PeriodTests
{
    static ReportContext Ctx(int days, DateTime end) => new()
    {
        Viewer = new Viewer { WindowsName = "x", Roles = new HashSet<string>() },
        Days = days, End = end, Start = end.AddDays(-(days - 1)), MinGroupSize = 5, InactiveDays = 30,
    };

    [Theory]
    [InlineData(7)]
    [InlineData(14)]
    [InlineData(30)]
    public void Short_periods_trend_by_day(int days)
    {
        var c = Ctx(days, new DateTime(2026, 9, 24));
        Assert.True(c.Daily);
        Assert.Equal(days, c.Buckets().Count);
        Assert.Equal(new DateTime(2026, 9, 24), c.Buckets()[^1].Key);
    }

    [Theory]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(365)]
    public void Long_periods_trend_by_Saturday_to_Friday_week(int days)
    {
        var c = Ctx(days, new DateTime(2026, 9, 24));   // a Thursday
        var b = c.Buckets();
        Assert.False(c.Daily);
        Assert.All(b, x => Assert.Equal(DayOfWeek.Friday, x.Key.DayOfWeek));
        Assert.True(b[^1].Partial);                      // the week to Friday the 25th is not over
        Assert.Equal("w/e 25 Sep", b[^1].Label);
        Assert.True(b[0].Key.AddDays(-6) <= c.Start && b[0].Key >= c.Start);
    }

    [Fact]
    public void Date_keys_are_yyyymmdd() => Assert.Equal(20260105, ReportContext.Key(new DateTime(2026, 1, 5)));
}

public class ChartTests
{
    [Fact]
    public void Gaps_and_empty_series_draw_without_NaN()
    {
        var svg = Chart.Line(["a", "b", "c"], [new Series("s", [null, 1, null], 1), new Series("t", [null, null, null], 2)], Fmt.Count, "x").Value!;
        Assert.DoesNotContain("NaN", svg);
        var bars = Chart.Bars([new Bar("zero", 0), new Bar("none", null)], Fmt.Percent, "x").Value!;
        Assert.DoesNotContain("NaN", bars);
    }

    [Fact]
    public void Labels_are_encoded() =>
        Assert.DoesNotContain("<b>", Chart.Bars([new Bar("<b>x</b>", 1)], Fmt.Count, "x").Value!);
}

public class BrowserTitleTests
{
    [Theory]
    [InlineData("Adoption", "Adoption · Copilot Insights")]
    [InlineData("Copilot Insights", "Copilot Insights")]   // the home page: the name once
    [InlineData(" Copilot Insights ", "Copilot Insights")]
    [InlineData(null, "Copilot Insights")]                  // a page that is not a report
    [InlineData("", "Copilot Insights")]
    public void The_product_name_appears_once(string? title, string expected) =>
        Assert.Equal(expected, InsightsPage.BrowserTitle(title));
}

/// <summary>Column headers sort a table: on the server for a paged list, in the page for the rest.</summary>
public class ColumnSortTests
{
    static readonly Dictionary<string, string> Sorts = new() { ["name"] = "du.DisplayName", ["idle"] = "x.LastKey, x.DisplayName", ["most"] = "SUM(CASE WHEN a = 1 THEN 1 ELSE 0 END) DESC, du.DisplayName ASC" };

    static HttpRequest Q(string query)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Path = "/licensing/unused";
        ctx.Request.QueryString = new QueryString(query);
        return ctx.Request;
    }

    [Theory]
    [InlineData("du.DisplayName", "du.DisplayName DESC")]
    [InlineData("x.LastKey, x.DisplayName", "x.LastKey DESC, x.DisplayName DESC")]
    [InlineData("v.Records DESC", "v.Records")]
    [InlineData("SUM(CASE WHEN a = 1 THEN 1 ELSE 0 END) DESC, du.DisplayName ASC", "SUM(CASE WHEN a = 1 THEN 1 ELSE 0 END), du.DisplayName DESC")]
    [InlineData("COALESCE(a, b), c desc", "COALESCE(a, b) DESC, c")]
    public void Reverse_turns_every_term(string order, string reversed) => Assert.Equal(reversed, Paging.Reverse(order));

    [Fact]
    public void A_leading_minus_reverses_a_listed_key_only()
    {
        Assert.Equal("ORDER BY x.LastKey DESC, x.DisplayName DESC OFFSET 0 ROWS FETCH NEXT 50 ROWS ONLY", new Paging(Q("?s=-idle"), Sorts, "name").OrderAndPage());
        var bad = new Paging(Q("?s=-du.DisplayName;DROP"), Sorts, "name");
        Assert.Equal("name", bad.SortKey);
        Assert.False(bad.Reversed);
        Assert.False(new Paging(Q("?s=-"), Sorts, "name").Reversed);
    }

    [Fact]
    public void A_header_link_sorts_then_reverses_and_starts_at_page_one()
    {
        var first = new Paging(Q("?d=90&p=4"), Sorts, "name");
        Assert.Equal("/licensing/unused?d=90&s=idle", first.SortLink(Q("?d=90&p=4"), "idle"));
        Assert.Equal("/licensing/unused?d=90&s=-name", first.SortLink(Q("?d=90&p=4"), "name"));
        var reversed = new Paging(Q("?s=-name"), Sorts, "idle");
        Assert.Equal("/licensing/unused?s=name", reversed.SortLink(Q("?s=-name"), "name"));
        Assert.Equal("descending", reversed.AriaSort("name", keyDescends: false));
        Assert.Equal("ascending", reversed.AriaSort("name", keyDescends: true));
        Assert.Null(reversed.AriaSort("idle", false));
    }

    [Fact]
    public void The_sort_choice_says_when_a_column_or_a_reversal_is_in_force()
    {
        var html = new Paging(Q("?s=-name"), Sorts, "idle").Controls(Q("?s=-name"), new Dictionary<string, string> { ["name"] = "Name", ["idle"] = "Idle longest" }).Value!;
        Assert.Contains("<option value=\"-name\" selected>Name, reversed</option>", html);
        var column = new Paging(Q("?s=most"), Sorts, "idle").Controls(Q("?s=most"), new Dictionary<string, string> { ["idle"] = "Idle longest" }).Value!;
        Assert.Contains("<option value=\"most\" selected>By the column chosen</option>", column);
    }

    [Fact]
    public void A_full_table_sorts_in_the_page_by_value_and_keeps_its_last_rows()
    {
        var t = new Table { Headers = ["Unit", "People", "Last used", ""] , KeepLast = 1 };
        t.Rows.Add(["Finance", 1200, new DateTime(2026, 9, 3), new Microsoft.AspNetCore.Html.HtmlString("<form><button>Go</button></form>")]);
        t.Rows.Add(["Legal", 35.5, new DateTime(2026, 9, 3, 14, 5, 0), null]);
        t.Rows.Add(["Smaller groups", 7, null, null]);
        var html = t.Render().Value!;
        Assert.Contains("<table class=\"sortable\">", html);
        Assert.Contains("<th scope=\"col\"><button type=\"button\" class=\"sort\">Unit</button></th>", html);
        Assert.Contains("<th scope=\"col\"><button type=\"button\" class=\"sort\">Last used</button></th>", html);
        Assert.Contains("<th scope=\"col\"></th>", html);                                   // the column of buttons does not sort
        Assert.Contains("data-v=\"1200\"", html);
        Assert.Contains("data-v=\"35.5\"", html);
        Assert.Contains("data-v=\"202609031405\"", html);
        Assert.Contains("<tr data-keep=\"1\"><td>Smaller groups</td>", html);
    }

    [Fact]
    public void A_table_whose_order_is_its_meaning_or_too_short_does_not_sort()
    {
        var steps = new Table { Headers = ["Step", "People"], Sortable = false };
        steps.Rows.Add(["Licensed", 10]); steps.Rows.Add(["Started", 8]); steps.Rows.Add(["Habit", 3]);
        Assert.DoesNotContain("sort", steps.Render().Value!);
        var one = new Table { Headers = ["Unit", "People"] };
        one.Rows.Add(["Finance", 10]);
        Assert.DoesNotContain("sort", one.Render().Value!);
    }

    [Fact]
    public void A_paged_list_sorts_through_its_keys()
    {
        var req = Q("?s=idle");
        var pg = new Paging(req, Sorts, "name");
        var t = new Table { Headers = ["Name", "Department", "Last used"], Server = new(pg, req, ["name", null, "idle"]) };
        t.Rows.Add(["A", "Finance", new DateTime(2026, 9, 1)]); t.Rows.Add(["B", "Legal", null]); t.Rows.Add(["C", "HR", null]);
        var html = t.Render().Value!;
        Assert.DoesNotContain("class=\"sortable\"", html);
        Assert.DoesNotContain("data-v", html);
        Assert.Contains("<th scope=\"col\"><a class=\"sort\" href=\"/licensing/unused?s=name\">Name</a></th>", html);
        Assert.Contains("<th scope=\"col\">Department</th>", html);
        Assert.Contains("<th scope=\"col\" aria-sort=\"ascending\"><a class=\"sort\" href=\"/licensing/unused?s=-idle\">Last used</a></th>", html);
    }
}

/// <summary>Workforce › By level in the organization lists the levels as numbers.</summary>
public class LevelOrderTests
{
    [Fact]
    public void Levels_sort_as_numbers_so_level_10_and_above_come_after_level_9()
    {
        string[] names = ["Level 10", "Level 11", "Level 2", "(not in the chart)", "Level 3", "Level 9", "Level 12", "Level 1"];
        /* As text, Level 10 and Level 11 came before Level 2, which is the order the chart had. */
        Assert.Equal(["(not in the chart)", "Level 1", "Level 10", "Level 11", "Level 12", "Level 2", "Level 3", "Level 9"], names.OrderBy(n => n, StringComparer.Ordinal).ToArray());
        Assert.Equal(["Level 1", "Level 2", "Level 3", "Level 9", "Level 10", "Level 11", "Level 12", "(not in the chart)"],
            names.OrderBy(CopilotInsights.Data.Metrics.LevelOrder).ThenBy(n => n, StringComparer.Ordinal).ToArray());
    }

    [Theory]
    [InlineData("Level 7", 7)]
    [InlineData("Level 15", 15)]
    [InlineData("(not in the chart)", int.MaxValue)]
    [InlineData("Level x", int.MaxValue)]
    [InlineData("Level -2", int.MaxValue)]
    [InlineData("Level 99999999999", int.MaxValue)]
    [InlineData("", int.MaxValue)]
    public void A_level_names_its_number_and_anything_else_goes_last(string name, int order) => Assert.Equal(order, CopilotInsights.Data.Metrics.LevelOrder(name));

    [Fact]
    public void The_workforce_chart_orders_its_levels_with_it()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            var file = Path.Combine(d.FullName, "CopilotInsights", "Pages", "Workforce", "Workforce.cs");
            if (!File.Exists(file)) continue;
            var source = File.ReadAllText(file);
            Assert.Contains("OrderBy(l => Metrics.LevelOrder(l.Name))", source);
            Assert.DoesNotContain("lv.OrderBy(l => l.Name)", source);
            return;
        }
        throw new DirectoryNotFoundException("CopilotInsights/Pages/Workforce, above " + AppContext.BaseDirectory);
    }
}
