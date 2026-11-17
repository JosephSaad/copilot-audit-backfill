using CopilotInsights.Charts;
using Xunit;

namespace CopilotInsights.Tests;

/// <summary>
/// A license line runs to the period's last day, so it ends on the headline's number: the week under way is drawn for it
/// alone, under the period's last day. The other lines still stop at the last whole week.
/// </summary>
public sealed class ToEndLineTests
{
    /// <summary>A 90-day period ending Monday 16 Nov: the week to Friday 20 Nov is under way.</summary>
    static Axis Weeks(string? note = null)
    {
        var a = new Axis(["w/e 30 Oct", "w/e 6 Nov", "w/e 13 Nov", "w/e 20 Nov"]) { LastDay = "16 Nov", Note = note };
        a.Partial.Add(3);
        return a;
    }

    static readonly double?[] Licensed = [18000, 18020, 18036, 18056];
    static readonly double?[] Used = [15000, 15100, 15114, 4000];

    [Fact]
    public void The_license_line_ends_on_the_last_day_with_the_headline_number()
    {
        var html = Chart.Line(Weeks(), [new Series("Premium license holders", Licensed, 1, ToEnd: true), new Series("Used", Used, 3)], Fmt.Count, "t").Value!;
        Assert.Contains("Premium license holders 18,056", html);
        Assert.DoesNotContain("18,036<", html);
        Assert.Contains(">16 Nov<", html);
        Assert.DoesNotContain("20 Nov", html);
    }

    [Fact]
    public void The_other_lines_still_stop_at_the_last_whole_week()
    {
        var html = Chart.Line(Weeks(), [new Series("Premium license holders", Licensed, 1, ToEnd: true), new Series("Used", Used, 3)], Fmt.Count, "t").Value!;
        Assert.Contains("Used 15,114", html);
        Assert.DoesNotContain("4,000", html);
        /* The last point's tooltip names the license line alone. */
        Assert.Contains("data-tip=\"16 Nov: Premium license holders 18,056\"", html);
    }

    [Fact]
    public void Without_a_line_to_the_end_the_week_under_way_is_left_out_as_before()
    {
        var html = Chart.Line(Weeks(), [new Series("Used", Used, 3)], Fmt.Count, "t").Value!;
        var whole = Chart.Line(new Axis(["w/e 30 Oct", "w/e 6 Nov", "w/e 13 Nov"]), [new Series("Used", Used[..3], 3)], Fmt.Count, "t").Value!;
        Assert.Equal(whole, html);
    }

    [Fact]
    public void A_daily_line_runs_through_the_days_held_back_and_says_so()
    {
        var days = new Axis(["14 Nov", "15 Nov", "16 Nov"]) { Note = "Shown to 14 Nov. Records from the last 48 hours may not all be in yet, so the newest days are left out." };
        days.Partial.Add(1); days.Partial.Add(2);
        var html = Chart.Line(days, [new Series("Licensed", [10, 11, 12], 1, ToEnd: true)], Fmt.Count, "t").Value!;
        Assert.Contains("Licensed 12", html);
        Assert.Contains("Licensed runs to 16 Nov: a count held on each day, it does not wait for late records.", html);
    }

    [Fact]
    public void A_period_ending_on_a_Friday_draws_nothing_extra()
    {
        var a = new Axis(["w/e 6 Nov", "w/e 13 Nov"]) { LastDay = "13 Nov" };
        var html = Chart.Line(a, [new Series("Licensed", [1, 2], 1, ToEnd: true)], Fmt.Count, "t").Value!;
        Assert.Contains(">w/e 13 Nov<", html);
    }

    [Theory]
    [InlineData("Pages/Leadership/Leadership.cs", "\"Premium license holders\", tr.Select(w => (double?)w.Licensed).ToList(), 1, ToEnd: true")]
    [InlineData("Pages/Adoption/Adoption.cs", "\"License holders\", tr.Select(w => (double?)w.Licensed).ToList(), 1, ToEnd: true")]
    [InlineData("Pages/Licensing/Licensing.cs", "\"Licensed\", tr.Select(w => (double?)w.Licensed).ToList(), 1, ToEnd: true")]
    [InlineData("Pages/InsightsPage.cs", "LastDay = Ctx.Daily ? null : Ctx.End.ToString(\"d MMM\")")]
    public void The_license_lines_run_to_the_end(string file, string text) =>
        Assert.Contains(text, File.ReadAllText(Path.Combine(Project(), file)));

    static string Project()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "CopilotInsights", "CopilotInsights.csproj"))) d = d.Parent;
        return Path.Combine(d!.FullName, "CopilotInsights");
    }
}
