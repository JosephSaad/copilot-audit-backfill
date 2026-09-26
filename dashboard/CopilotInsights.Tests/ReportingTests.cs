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
        Assert.Equal("ORDER BY du.Department ", new Paging(Q("?handler=csv&p=9&s=dept"), Sorts, "name").OrderAndPage());
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
