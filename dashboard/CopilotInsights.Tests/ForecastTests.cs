using CopilotInsights.Data;
using Xunit;

namespace CopilotInsights.Tests;

public class ForecastTests
{
    [Fact]
    public void A_flat_series_forecasts_flat()
    {
        var p = Forecast.Holt(Enumerable.Repeat(500.0, 30).ToList(), 13)!;
        Assert.All(p.Forecast, v => Assert.InRange(v, 499, 501));
    }

    [Fact]
    public void A_steady_climb_continues_but_the_damped_trend_flattens()
    {
        var y = Enumerable.Range(0, 40).Select(i => 100.0 + 10 * i).ToList();   // +10 a week
        var p = Forecast.Holt(y, 52)!;
        Assert.True(p.Forecast[0] > y[^1], "the next week is above the last");
        var firstStep = p.Forecast[1] - p.Forecast[0];
        var lastStep = p.Forecast[^1] - p.Forecast[^2];
        Assert.True(lastStep < firstStep / 10, $"the trend fades ({firstStep:0.0} then {lastStep:0.00})");
        Assert.True(p.Forecast[^1] < y[^1] + 10 * 52, "and never runs away linearly");
    }

    [Fact]
    public void Nothing_goes_below_zero_or_over_a_ceiling()
    {
        var falling = Enumerable.Range(0, 20).Select(i => 200.0 - 12 * i).ToList();
        Assert.All(Forecast.Holt(falling, 26)!.Low, v => Assert.True(v >= 0));
        var rising = Enumerable.Range(0, 20).Select(i => 0.5 + 0.02 * i).ToList();
        Assert.All(Forecast.Holt(rising, 26, ceiling: 1)!.High, v => Assert.True(v <= 1));
    }

    [Fact]
    public void The_interval_widens_with_the_horizon()
    {
        var rnd = new Random(7);
        var y = Enumerable.Range(0, 52).Select(i => 1000 + 5.0 * i + rnd.Next(-40, 40)).ToList();
        var p = Forecast.Holt(y, 26)!;
        Assert.True(p.High[25] - p.Low[25] > p.High[0] - p.Low[0]);
        Assert.All(Enumerable.Range(0, 26), i => Assert.True(p.Low[i] <= p.Forecast[i] && p.Forecast[i] <= p.High[i]));
    }

    [Fact]
    public void Too_little_history_is_no_forecast() => Assert.Null(Forecast.Holt([1, 2, 3], 13));
}
