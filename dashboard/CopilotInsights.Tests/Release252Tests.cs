using CopilotInsights.Charts;
using CopilotInsights.Infrastructure;
using CopilotInsights.Tests.Review2412A;
using Xunit;

namespace CopilotInsights.Tests;

/// <summary>
/// What 2.5.2 changed, held where a later change would undo it unseen: a weekly chart draws whole weeks only, and the
/// period's totals still count every day of it.
/// </summary>
public class Release252Tests
{
    static ReportContext Ctx(int days, DateTime end) => new()
    {
        Viewer = new Viewer { WindowsName = "x", Roles = new HashSet<string>() },
        Days = days, End = end, Start = end.AddDays(-(days - 1)), MinGroupSize = 5, InactiveDays = 30,
    };

    /// <summary>
    /// The weeks of the chart that showed the drop, 180 days to Saturday 3 October 2026: from Tuesday 7 April, so
    /// "w/e 10 Apr" held four days and "w/e 9 Oct" one, a Saturday.
    /// </summary>
    static Axis Weeks()
    {
        var x = new Axis(["w/e 10 Apr", "w/e 17 Apr", "w/e 24 Apr", "w/e 9 Oct"]);
        x.Partial.UnionWith([0, 3]);
        return x;
    }

    static readonly IReadOnlyList<double?> Counted = [18_800, 21_000, 21_200, 224];
    static readonly IReadOnlyList<string> WholeWeeks = ["w/e 17 Apr", "w/e 24 Apr"];
    static readonly IReadOnlyList<double?> WholeCounted = [21_000, 21_200];

    [Fact]
    public void A_period_marks_the_weeks_it_covers_only_in_part_and_keeps_them_for_its_totals()
    {
        var b = Ctx(180, new DateTime(2026, 10, 3)).Buckets();
        Assert.Equal(("w/e 10 Apr", true), (b[0].Label, b[0].Partial));       // Tuesday 7 to Friday 10 April
        Assert.Equal(("w/e 9 Oct", true), (b[^1].Label, b[^1].Partial));     // Saturday 3 October alone
        Assert.Equal(25, b.Count(x => !x.Partial));
        Assert.Equal(new DateTime(2026, 4, 17), b.First(x => !x.Partial).Key);
        Assert.Equal(new DateTime(2026, 10, 2), b.Last(x => !x.Partial).Key);
        /* A period ending on a Friday and starting on a Saturday has no partial week. */
        Assert.DoesNotContain(Ctx(182, new DateTime(2026, 10, 2)).Buckets(), x => x.Partial);
        /* The trend's rows are one for every point, the partial ones too: Governance, Compliance, Security, Apps, the
           board pack and the digests add them up for the period's totals. */
        Assert.Contains("return c.Buckets().Select(bk => rows.FirstOrDefault(r => r.Bucket == bk.Key)", Source.Read("Data", "Metrics.cs"));
    }

    [Fact]
    public void Every_trend_page_marks_its_partial_weeks_on_the_axis_and_says_so()
    {
        var page = Source.Read("Pages", "InsightsPage.cs");
        Assert.Contains("if (buckets[i].Partial) axis.Partial.Add(i);", page);
        Assert.Contains("Each point is a whole Saturday-to-Friday week, labeled by its Friday.", page);
        Assert.DoesNotContain("may be partial", page);
    }

    [Fact]
    public void A_line_ends_on_the_last_whole_week()
    {
        var html = Chart.Line(Weeks(), [new Series("Copilot Chat only", Counted, 2)], Fmt.Count, "t").Value!;
        Assert.Equal(Chart.Line(WholeWeeks, [new Series("Copilot Chat only", WholeCounted, 2)], Fmt.Count, "t").Value, html);
        Assert.DoesNotContain("9 Oct", html);
        Assert.DoesNotContain("10 Apr", html);
    }

    [Fact]
    public void Columns_heatmaps_and_sparklines_leave_out_the_partial_weeks_too()
    {
        Assert.Equal(Chart.Columns(WholeWeeks, [new Series("Interactions", WholeCounted, 1)], Fmt.Count, "t").Value,
                     Chart.Columns(Weeks(), [new Series("Interactions", Counted, 1)], Fmt.Count, "t").Value);

        var all = new double?[2, 4] { { 0.30, 0.50, 0.52, 0.01 }, { 0.20, 0.40, 0.41, 0.90 } };
        var whole = new double?[2, 2] { { 0.50, 0.52 }, { 0.40, 0.41 } };
        string[] units = ["Finance", "Retail"];
        Assert.Equal(Chart.Heatmap(units, WholeWeeks, whole, Fmt.Percent, "t").Value, Chart.Heatmap(units, Weeks(), all, Fmt.Percent, "t").Value);

        Assert.Equal(Chart.Sparkline(WholeWeeks, WholeCounted, "App").Value, Chart.Sparkline(Weeks(), Counted, "App").Value);
    }

    [Fact]
    public void An_event_moves_with_its_week_and_one_in_a_partial_week_is_not_drawn()
    {
        var x = Weeks();
        x.Markers.Add(new Marker(2, "Workshop", "Training"));
        x.Markers.Add(new Marker(3, "Wave 3 licenses", "Rollout"));
        var expected = new Axis(WholeWeeks);
        expected.Markers.Add(new Marker(1, "Workshop", "Training"));
        var html = Chart.Line(x, [new Series("People", Counted, 1)], Fmt.Count, "t").Value!;
        Assert.Equal(Chart.Line(expected, [new Series("People", WholeCounted, 1)], Fmt.Count, "t").Value, html);
        Assert.Contains("Workshop", html);
        Assert.DoesNotContain("Wave 3", html);
    }

    [Fact]
    public void A_heatmap_keeps_its_column_labels_inside_the_chart()
    {
        /* The 25 whole weeks of that period under five units' names: the label over the last column, "w/e 2 Oct", ran
           5px past the chart's right edge. A label takes about 6.3 units a character, as Chart.TextW has it. */
        var ci = System.Globalization.CultureInfo.InvariantCulture;
        var weeks = Enumerable.Range(0, 25).Select(i => "w/e " + new DateTime(2026, 4, 17).AddDays(7 * i).ToString("d MMM", ci)).ToList();
        string[] units = ["Casey Ivanova", "Jordan Mensah", "Morgan Petrov", "Riley Rossi", "Taylor Bauer"];
        var html = Chart.Heatmap(units, weeks, new double?[5, 25], Fmt.Percent, "t", 1).Value!;
        var labels = System.Text.RegularExpressions.Regex.Matches(html, "<text class=\"tick\" x=\"([0-9.]+)\" y=\"14\" text-anchor=\"middle\">([^<]+)</text>")
            .Select(m => (Text: m.Groups[2].Value, X: double.Parse(m.Groups[1].Value, ci), Half: m.Groups[2].Value.Length * 6.3 / 2)).ToList();
        Assert.Equal("w/e 17 Apr", labels[0].Text);
        Assert.All(labels, l => Assert.True(l.X - l.Half >= 0 && l.X + l.Half <= 720.01, $"{l.Text} leaves the chart: {l.X - l.Half:0.#} to {l.X + l.Half:0.#} of 720"));
        for (var i = 1; i < labels.Count; i++)
            Assert.True(labels[i].X - labels[i].Half > labels[i - 1].X + labels[i - 1].Half, $"{labels[i - 1].Text} and {labels[i].Text} overlap");
    }

    [Fact]
    public void A_daily_trend_draws_every_day()
    {
        var days = new Axis(["1 Oct", "2 Oct", "3 Oct"]);
        IReadOnlyList<double?> v = [5, 6, 1];
        Assert.Equal(Chart.Line(["1 Oct", "2 Oct", "3 Oct"], [new Series("People", v, 1)], Fmt.Count, "t").Value,
                     Chart.Line(days, [new Series("People", v, 1)], Fmt.Count, "t").Value);
        Assert.Contains("3 Oct", Chart.Line(days, [new Series("People", v, 1)], Fmt.Count, "t").Value);
    }
}
