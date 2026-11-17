using CopilotInsights.Charts;
using CopilotInsights.Data;
using CopilotInsights.Infrastructure;
using CopilotInsights.Tests.Review2412A;
using Xunit;

namespace CopilotInsights.Tests;

/// <summary>
/// What 2.5.3 changed, held where a later change would undo it unseen: a daily trend stops two days before the last
/// record's day and says so under the chart, and the period's totals still count every day of it.
/// </summary>
public class Release253Tests
{
    static ReportContext Ctx(int days, DateTime end, DateTime? lastRecord) => new()
    {
        Viewer = new Viewer { WindowsName = "x", Roles = new HashSet<string>() },
        Days = days, End = end, Start = end.AddDays(-(days - 1)), LastRecord = lastRecord, MinGroupSize = 5, InactiveDays = 30,
    };

    static readonly DateTime Oct3 = new(2026, 10, 3), Oct4 = new(2026, 10, 4);
    const string Note = "Shown to 2 Oct. Records from the last 48 hours may not all be in yet, so the newest days are left out.";

    [Theory]
    [InlineData(7)]
    [InlineData(14)]
    [InlineData(30)]
    public void Records_to_4_October_show_a_daily_trend_to_2_October(int days)
    {
        /* The load has reached today, 4 October: the period still ends on the 3rd, the last complete day. */
        var c = Ctx(days, Oct3, Oct4);
        Assert.Equal(new DateTime(2026, 10, 2), c.DrawnTo);
        var b = c.Buckets();
        Assert.Equal(days, b.Count);                                            // every day is still a row, for the totals
        Assert.Equal(new[] { Oct3 }, b.Where(x => x.Partial).Select(x => x.Key));
        Assert.Equal(new DateTime(2026, 10, 2), b.Last(x => !x.Partial).Key);
    }

    [Fact]
    public void A_load_that_has_not_reached_today_holds_back_two_days_of_the_period()
    {
        /* The last record is the 3rd, as before a day's first load: the 2nd and the 3rd may not all be in. */
        var b = Ctx(30, Oct3, Oct3).Buckets();
        Assert.Equal(new[] { new DateTime(2026, 10, 2), Oct3 }, b.Where(x => x.Partial).Select(x => x.Key));
        /* Not told the last record's day, a report takes it to be its last day. */
        Assert.Equal(new DateTime(2026, 10, 1), Ctx(30, Oct3, null).DrawnTo);
        Assert.Equal(2, ReportContext.HeldBackDays);
    }

    [Theory]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(365)]
    public void A_weekly_trend_keeps_its_whole_weeks(int days)
    {
        var with = Ctx(days, Oct3, Oct4).Buckets();
        var without = Ctx(days, Oct3, null).Buckets();
        Assert.Equal(without, with);
        Assert.Equal(new DateTime(2026, 10, 2), with.Last(x => !x.Partial).Key);   // the week to Friday 2 October is whole, and drawn
    }

    [Fact]
    public void A_report_made_from_another_keeps_its_last_record()
    {
        var c = Ctx(30, Oct3, Oct4);
        Assert.Equal(Oct4, OpsReports.Window(c, Oct3.AddDays(-30), 30).LastRecord);
        Assert.Equal(Oct4, OpsReports.Tenant(c).LastRecord);
        /* So the period before it, which ends a month back, holds nothing back. */
        Assert.DoesNotContain(OpsReports.Window(c, Oct3.AddDays(-30), 30).Buckets(), x => x.Partial);
    }

    static Axis Days(string? note)
    {
        var x = new Axis(["30 Sep", "1 Oct", "2 Oct", "3 Oct"]) { Note = note };
        x.Partial.Add(3);
        return x;
    }

    static readonly IReadOnlyList<double?> Counted = [900, 950, 940, 12];
    static readonly IReadOnlyList<string> ShownDays = ["30 Sep", "1 Oct", "2 Oct"];
    static readonly IReadOnlyList<double?> ShownCounted = [900, 950, 940];

    [Fact]
    public void A_chart_says_under_it_how_far_it_is_shown_and_why()
    {
        var line = Chart.Line(Days(Note), [new Series("People", Counted, 1)], Fmt.Count, "t").Value!;
        Assert.Equal(Chart.Line(ShownDays, [new Series("People", ShownCounted, 1)], Fmt.Count, "t").Value + $"<p class=\"held\">{Note}</p>", line);
        Assert.DoesNotContain("3 Oct", line);

        var columns = Chart.Columns(Days(Note), [new Series("Interactions", Counted, 1)], Fmt.Count, "t").Value!;
        Assert.Equal(Chart.Columns(ShownDays, [new Series("Interactions", ShownCounted, 1)], Fmt.Count, "t").Value + $"<p class=\"held\">{Note}</p>", columns);

        var all = new double?[1, 4] { { 0.5, 0.6, 0.55, 0.01 } };
        var shown = new double?[1, 3] { { 0.5, 0.6, 0.55 } };
        string[] units = ["Finance"];
        Assert.Equal(Chart.Heatmap(units, ShownDays, shown, Fmt.Percent, "t").Value + $"<p class=\"held\">{Note}</p>",
                     Chart.Heatmap(units, Days(Note), all, Fmt.Percent, "t").Value);

        /* The line is in the chart's own markup, after the drawing: a chart drawn again narrower, or copied out, keeps it. */
        var svg = Assert.IsType<ChartSvg>(Chart.Line(Days(Note), [new Series("People", Counted, 1)], Fmt.Count, "t"));
        Assert.EndsWith($"<p class=\"held\">{Note}</p>", svg.Render(440));
    }

    [Fact]
    public void A_chart_with_nothing_held_back_says_nothing()
    {
        Assert.DoesNotContain("class=\"held\"", Chart.Line(Days(null), [new Series("People", Counted, 1)], Fmt.Count, "t").Value);
        Assert.DoesNotContain("class=\"held\"", Chart.Line(ShownDays, [new Series("People", ShownCounted, 1)], Fmt.Count, "t").Value);
        Assert.Equal("", Chart.Held(null));
        Assert.Equal("<p class=\"held\">a &lt; b</p>", Chart.Held("a < b"));
    }

    [Fact]
    public void Every_daily_trend_and_every_table_of_trends_carries_the_line()
    {
        var page = Source.Read("Pages", "InsightsPage.cs");
        Assert.Contains("new Axis(buckets.Select(b => b.Label)) { Note = HeldBack(), ", page);
        Assert.Contains("{shown} to {Ctx.DrawnTo:d MMM}. Records from the last 48 hours may not all be in yet, so the newest days are left out.", page);
        Assert.Contains("The two newest days of records are not drawn", page);
        /* A sparkline has no room to say it: the three tables with a trend in each row say it under the table. */
        Assert.Contains("WithTrendNote(t.Render())", Source.Read("Pages", "Apps", "Apps.cs"));
        Assert.Contains("WithTrendNote(t.Render())", Source.Read("Pages", "Agents", "Agents.cs"));
        Assert.Contains("WithTrendNote(t.Render())", Source.Read("Pages", "Leadership", "Leadership.cs"));
        Assert.Contains(".held {", Source.Read("wwwroot", "site.css"));
        /* The last record's day is read with the period's last day, on every request. */
        Assert.Contains("SELECT (SELECT MAX(DateKey) FROM dash.UserDay WHERE DateKey < @today), (SELECT MAX(DateKey) FROM dash.UserDay);", Source.Read("Infrastructure", "ReportContext.cs"));
    }
}
