using CopilotInsights.Data;
using CopilotInsights.Infrastructure;
using Xunit;

namespace CopilotInsights.Tests;

/// <summary>The 28-day moving average of a trend (2.5.0): a weekend or a holiday does not dip a line.</summary>
public class TrendAverageTests
{
    [Fact]
    public void A_weekly_pattern_of_busy_weekdays_and_quiet_weekends_averages_to_a_flat_line()
    {
        /* Five weekdays of 100 and two weekend days of 10, for eight weeks: the raw days dip every weekend. */
        var raw = Enumerable.Range(0, 56).Select(i => (double?)(i % 7 >= 5 ? 10 : 100)).ToList();
        var shown = TrendAverage.Trailing(raw, 28, 28);
        Assert.Equal(28, shown.Count);
        Assert.All(shown, v => Assert.Equal((5 * 100 + 2 * 10) / 7.0, v!.Value, 6));
    }

    [Fact]
    public void A_window_that_reaches_back_before_the_first_point_averages_the_points_it_has()
    {
        Assert.Equal<double?>([10, 15, 20], TrendAverage.Trailing([10, 20, 30], 28, 3));
    }

    [Fact]
    public void Only_the_last_points_are_returned_and_each_averages_over_its_own_window()
    {
        /* Window 3 over 1..6, the last 3 points shown: 4 is the mean of 2, 3, 4; 5 of 3, 4, 5; 6 of 4, 5, 6. */
        Assert.Equal<double?>([3, 4, 5], TrendAverage.Trailing([1, 2, 3, 4, 5, 6], 3, 3));
    }

    [Fact]
    public void A_week_not_yet_over_is_null_and_left_out_of_the_windows_it_falls_in()
    {
        /* Its own point is a gap, not the mean of the others, and the point after it averages without it. */
        Assert.Equal<double?>([10, 15, null], TrendAverage.Trailing([10, 20, null], 3, 3));
        Assert.Equal<double?>([10, null, 20], TrendAverage.Trailing([10, null, 30], 3, 3));
        Assert.Equal<double?>([null, null], TrendAverage.Trailing([null, null], 2, 2));
        Assert.Empty(TrendAverage.Trailing([], 28, 0));
    }

    [Fact]
    public void A_rate_is_the_numerators_over_the_denominators_of_the_window_not_the_mean_of_rates()
    {
        /* Day two had no use, and day three a lot of it: the pooled rate follows the use, not the days. */
        var rate = TrendAverage.TrailingRatio([1, 0, 3], [10, 10, 10], 2, 3);
        Assert.Equal(0.1, rate[0]!.Value, 9);        // 1 / 10
        Assert.Equal(0.05, rate[1]!.Value, 9);       // (1 + 0) / (10 + 10)
        Assert.Equal(0.15, rate[2]!.Value, 9);       // (0 + 3) / (10 + 10)
    }

    [Fact]
    public void A_rate_leaves_out_a_pair_with_a_null_and_is_null_where_nothing_was_available()
    {
        var rate = TrendAverage.TrailingRatio([1, null, 3], [10, 5, 10], 3, 3);
        Assert.Equal(0.2, rate[2]!.Value, 9);   // (1 + 3) / (10 + 10)
        Assert.Null(rate[1]);                   // its own pair is a gap
        Assert.Equal<double?>([null, null], TrendAverage.TrailingRatio([0, 0], [0, 0], 2, 2));
        Assert.Equal<double?>([null], TrendAverage.TrailingRatio([null], [null], 4, 1));
    }

    [Fact]
    public void The_average_of_a_group_is_shown_at_the_minimum_or_more_nobody_is_0_and_in_between_is_a_gap()
    {
        Assert.Null(TrendAverage.PeopleAtMin(null, 5));
        Assert.Equal(0, TrendAverage.PeopleAtMin(0, 5));
        Assert.Null(TrendAverage.PeopleAtMin(0.04, 5));
        Assert.Null(TrendAverage.PeopleAtMin(4.9, 5));
        Assert.Equal(5, TrendAverage.PeopleAtMin(5, 5));
        Assert.Equal(12.5, TrendAverage.PeopleAtMin(12.5, 5));
    }

    [Fact]
    public void An_average_shown_at_the_minimum_always_stands_for_that_many_different_people()
    {
        /* The privacy rule for a group at a point (2.4.12), carried to the average: a person counts once in a point, so a
           window whose points average the minimum or more cannot have been used by fewer different people. Tried over
           many windows of 28 days, with few people or many, and rarely or nearly every day. */
        var rnd = new Random(2412);
        var shown = 0;
        for (var trial = 0; trial < 2000; trial++)
        {
            var pool = rnd.Next(1, 40);
            var chance = rnd.NextDouble();
            var days = Enumerable.Range(0, 28).Select(_ => Enumerable.Range(0, pool).Where(p => rnd.NextDouble() < chance).ToHashSet()).ToList();
            var average = TrendAverage.Trailing(days.Select(d => (double?)d.Count).ToList(), 28, 1)[0];
            if (TrendAverage.PeopleAtMin(average, 5) is not > 0) continue;
            shown++;
            var different = days.SelectMany(d => d).Distinct().Count();
            Assert.True(different >= 5, $"an average of {average} for {different} different people");
        }
        Assert.True(shown > 100, "the trials never reached the minimum, so they proved nothing");
    }
}

/// <summary>Where the pages' trend charts read from, and what they chart (2.5.0).</summary>
public class TrendPagesTests
{
    static string PagesDirectory()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "CopilotInsights", "Pages", "InsightsPage.cs"))) return Path.Combine(d.FullName, "CopilotInsights", "Pages");
        throw new DirectoryNotFoundException("CopilotInsights/Pages, above " + AppContext.BaseDirectory);
    }

    static readonly System.Text.RegularExpressions.Regex DbCall = new(@"Db\.(Query|QueryTwo|Scalar)Async");

    [Fact]
    public void A_page_query_that_groups_by_the_trend_point_reads_from_the_lookback()
    {
        /* A query's text runs from its Db call to the next one. A trend query reads from @kt0, so its first point has its
           whole 28 days; one that read from @k0 would average the first month of a chart over fewer days than the rest. */
        var offenders = new List<string>();
        var queries = 0;
        foreach (var file in Directory.GetFiles(PagesDirectory(), "*.cs", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            var calls = DbCall.Matches(text);
            for (var i = 0; i < calls.Count; i++)
            {
                var query = text[calls[i].Index..(i + 1 < calls.Count ? calls[i + 1].Index : text.Length)];
                if (!query.Contains("BucketSql(")) continue;
                queries++;
                if (query.Contains("@k0") || !query.Contains("@kt0"))
                    offenders.Add($"{Path.GetFileName(file)}: {query.Split('\n')[0].Trim()}");
            }
        }
        Assert.True(queries > 15, $"found {queries} trend queries; the pattern has stopped matching them");
        Assert.True(offenders.Count == 0, string.Join("; ", offenders));
    }

    static readonly System.Text.RegularExpressions.Regex ClassStart = new(@"^[ \t]*(public|internal)[ \t]+((sealed|abstract)[ \t]+)?class[ \t]+(?<name>\w+)", System.Text.RegularExpressions.RegexOptions.Multiline);
    static readonly System.Text.RegularExpressions.Regex Averaging = new(@"\bAverage(Ratio)?\(");

    [Fact]
    public void A_page_that_averages_a_trend_says_so_where_the_reader_sees_it()
    {
        /* The note under a chart says what a point is: TrendNote for an average of counts, RatioNote for a rate or a share, or
           the words themselves where a table's trend column is the average. The pages are found by their classes. */
        var offenders = new List<string>();
        var averaging = 0;
        foreach (var file in Directory.GetFiles(PagesDirectory(), "*.cs", SearchOption.AllDirectories).Where(f => Path.GetFileName(f) != "InsightsPage.cs"))
        {
            var text = File.ReadAllText(file);
            var found = ClassStart.Matches(text);
            for (var i = 0; i < found.Count; i++)
            {
                var page = text[found[i].Index..(i + 1 < found.Count ? found[i + 1].Index : text.Length)];
                if (!Averaging.IsMatch(page)) continue;
                averaging++;
                if (!(page.Contains("TrendNote") || page.Contains("RatioNote") || page.Contains("28-day") || page.Contains("28 days")))
                    offenders.Add($"{Path.GetFileName(file)} {found[i].Groups["name"].Value}");
            }
        }
        Assert.True(averaging > 15, $"found {averaging} pages that average; the pattern has stopped matching them");
        Assert.True(offenders.Count == 0, "average a trend without saying so: " + string.Join(", ", offenders));
    }

    [Fact]
    public void A_panel_added_after_its_note_said_it_is_averaged_is_marked_and_the_next_is_not()
    {
        var averaged = false;
        var panels = new CopilotInsights.Pages.PanelList(() => { var a = averaged; averaged = false; return a; });
        panels.Add(new("Plain", Microsoft.AspNetCore.Html.HtmlString.Empty));
        averaged = true;
        panels.Add(new("Averaged", Microsoft.AspNetCore.Html.HtmlString.Empty, "A note"));
        panels.Add(new("After it", Microsoft.AspNetCore.Html.HtmlString.Empty));
        Assert.Equal([false, true, false], panels.Select(p => p.Averaged));
        Assert.Equal("A note", panels[1].Sub);
    }

    [Fact]
    public void The_notes_that_say_a_chart_is_averaged_mark_the_panel_and_its_heading_says_so()
    {
        var page = File.ReadAllText(Path.Combine(PagesDirectory(), "InsightsPage.cs"));
        foreach (var note in new[] { "TrendNote", "RatioNote" })
        {
            var start = page.IndexOf($"protected string {note}", StringComparison.Ordinal);
            Assert.True(start >= 0, note + " is not declared");
            var end = page.IndexOf("/// <summary>", start, StringComparison.Ordinal);
            Assert.Contains("_averaged = true", page[start..end]);
        }
        var report = File.ReadAllText(Path.Combine(PagesDirectory(), "Shared", "_Report.cshtml"));
        Assert.Contains("p.Averaged", report);
        Assert.Contains("28-day average", report);
    }

    [Fact]
    public void The_notes_that_say_a_chart_is_averaged_are_read_while_its_panel_is_made()
    {
        /* Reading one marks the next panel added, so each belongs inside the Panels.Add that makes the panel: a note kept in a
           variable would mark whichever panel came next. */
        var offenders = new List<string>();
        var notes = 0;
        foreach (var file in Directory.GetFiles(PagesDirectory(), "*.cs", SearchOption.AllDirectories).Where(f => Path.GetFileName(f) != "InsightsPage.cs"))
        {
            var text = File.ReadAllText(file);
            foreach (System.Text.RegularExpressions.Match n in System.Text.RegularExpressions.Regex.Matches(text, @"\b(TrendNote|RatioNote)\b"))
            {
                notes++;
                var add = text.LastIndexOf("Panels.Add(", n.Index, StringComparison.Ordinal);
                var between = add < 0 ? "" : text[add..n.Index];
                if (add < 0 || System.Text.RegularExpressions.Regex.IsMatch(between, @"(?m)^\s*(var |if \(|foreach |for \(|return |Kpis\.Add|Notice )"))
                    offenders.Add($"{Path.GetFileName(file)}, after: {text[Math.Max(0, n.Index - 60)..n.Index].Replace('\n', ' ').Trim()}");
            }
        }
        Assert.True(notes > 30, $"found {notes} notes; the pattern has stopped matching them");
        Assert.True(offenders.Count == 0, string.Join("; ", offenders));
    }

    [Fact]
    public void A_week_not_yet_over_is_left_out_of_a_legends_lowest_and_mean_not_counted_as_zero()
    {
        /* Its point is blank (null): read as 0 it made every app's lowest 0 and its mean too low, on a period that ends mid-week. */
        var page = File.ReadAllText(Path.Combine(PagesDirectory(), "Adoption", "ByLicense.cs"));
        Assert.DoesNotContain("sr.Values.Select(x => x ?? 0)", page);
        Assert.Contains("sr.Values.Where(x => x.HasValue)", page);
    }

    [Fact]
    public void An_other_series_is_decided_from_the_trend_which_reads_the_days_before_the_period()
    {
        /* A category used only in the lead-in is in the average of the first points, so it belongs to "Other" though the period has
           no more categories than are drawn. */
        var pages = Path.Combine(PagesDirectory());
        foreach (var (file, wrong) in new[]
                 {
                     ("Leadership/Leadership.cs", "period.Count > top.Count"), ("Adoption/ByLicense.cs", "byApp.Count > shown.Count"),
                     ("Compliance/Compliance.cs", "order.Count > 7)\n            series.Add"), ("Leadership/BoardPack.cs", "names = top.Concat(ranked.Count > 7 ?"),
                 })
            Assert.DoesNotContain(wrong, File.ReadAllText(Path.Combine(pages, file.Replace('/', Path.DirectorySeparatorChar))).Replace("\r\n", "\n"));
    }

    [Fact]
    public void A_trend_column_in_a_table_names_the_average_in_its_heading()
    {
        foreach (var (file, page) in new[] { ("Leadership/Leadership.cs", "Trend (people using Copilot, 28-day average)"), ("Agents/Agents.cs", "Trend (people, 28-day average)"), ("Apps/Apps.cs", "Trend (people, 28-day average)") })
            Assert.Contains(page, File.ReadAllText(Path.Combine(PagesDirectory(), file.Replace('/', Path.DirectorySeparatorChar))));
    }

    [Fact]
    public void A_page_charts_the_averaged_trend_and_takes_its_period_figures_from_the_period()
    {
        /* TrendAsync is the trend as measured, by day or week of the period: for the period's own sums, the digests and the
           data API. A page's chart reads TrendSeriesAsync, whose points are 28-day averages, and a page's KPI is a sum over
           the period, which no sum of those points is. */
        foreach (var file in Directory.GetFiles(PagesDirectory(), "*.cs", SearchOption.AllDirectories))
            Assert.DoesNotContain(".TrendAsync(", File.ReadAllText(file));
    }
}

/// <summary>The lead-in a trend reads so that its first point has its whole 28 days (2.5.0).</summary>
public class TrendWindowTests
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
    public void A_daily_trend_reads_27_days_before_its_first_point(int days)
    {
        var c = Ctx(days, new DateTime(2026, 9, 24));
        Assert.Equal(28, c.TrendWindow);
        var all = c.TrendBuckets();
        Assert.Equal(days + 27, all.Count);
        Assert.Equal(c.Start.AddDays(-27), all[0].Key);
        Assert.Equal(c.Buckets().Select(b => b.Key), all.Skip(27).Select(b => b.Key));
        Assert.Equal(ReportContext.Key(c.Start.AddDays(-27)), c.TrendStartKey);
    }

    [Theory]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(365)]
    public void A_weekly_trend_reads_three_weeks_before_its_first_week_and_starts_on_a_Saturday(int days)
    {
        var c = Ctx(days, new DateTime(2026, 9, 24));
        Assert.Equal(4, c.TrendWindow);
        var shown = c.Buckets();
        var all = c.TrendBuckets();
        Assert.Equal(shown.Count + 3, all.Count);
        Assert.Equal(shown[0].Key.AddDays(-21), all[0].Key);
        Assert.All(all, b => Assert.Equal(DayOfWeek.Friday, b.Key.DayOfWeek));
        Assert.Equal(DayOfWeek.Saturday, ReportContext.Day(c.TrendStartKey).DayOfWeek);
        Assert.Equal(ReportContext.Key(all[0].Key.AddDays(-6)), c.TrendStartKey);
        /* The first week shown is whole, though the period starts in the middle of it. */
        Assert.True(c.TrendStartKey < c.StartKey);
    }

    [Fact]
    public void A_trend_query_gets_where_to_start_as_a_parameter()
    {
        /* Dapper keeps the parameters of an object it is given until the query runs, so the names are read from the source. */
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            var file = Path.Combine(d.FullName, "CopilotInsights", "Infrastructure", "ReportContext.cs");
            if (!File.Exists(file)) continue;
            var source = File.ReadAllText(file);
            Assert.Contains("k0 = StartKey, k1 = EndKey, kt0 = TrendStartKey", source);
            return;
        }
        throw new DirectoryNotFoundException("CopilotInsights/Infrastructure, above " + AppContext.BaseDirectory);
    }

    [Fact]
    public void Averaged_counts_are_written_with_the_decimals_a_small_figure_needs()
    {
        Assert.Equal("1,234", CopilotInsights.Charts.Chart.Format(1234.4, CopilotInsights.Charts.Fmt.Average));
        Assert.Equal("12.3", CopilotInsights.Charts.Chart.Format(12.34, CopilotInsights.Charts.Fmt.Average));
        Assert.Equal("0.04", CopilotInsights.Charts.Chart.Format(0.036, CopilotInsights.Charts.Fmt.Average));
        Assert.Equal("0", CopilotInsights.Charts.Chart.Format(0, CopilotInsights.Charts.Fmt.Average));
        Assert.Equal("0.5", CopilotInsights.Charts.Chart.Format(0.5, CopilotInsights.Charts.Fmt.Average));
        Assert.Equal("—", CopilotInsights.Charts.Chart.Format(null, CopilotInsights.Charts.Fmt.Average));
    }
}
