namespace CopilotInsights.Data;

/// <summary>A projection: the fitted path, the forecast and its 95% interval, point by point.</summary>
public sealed record Projection(IReadOnlyList<double> Forecast, IReadOnlyList<double> Low, IReadOnlyList<double> High, double Alpha, double Beta, double Error)
{
    public double At(int step) => Forecast[Math.Clamp(step, 1, Forecast.Count) - 1];
}

/// <summary>
/// Damped-trend exponential smoothing (Holt, with the trend damped by phi), fitted to
/// a weekly series by a small grid search on the one-step error. Damping keeps a
/// recent climb from being projected upward for ever: the trend fades, as rollouts do.
/// The interval widens with the horizon, from the fitted error.
/// </summary>
public static class Forecast
{
    const double Phi = 0.9;

    public static Projection? Holt(IReadOnlyList<double> y, int horizon, double? ceiling = null)
    {
        if (y.Count < 6 || horizon < 1) return null;
        double best = double.MaxValue, ba = 0.5, bb = 0.1;
        foreach (var a in new[] { 0.1, 0.2, 0.3, 0.4, 0.5, 0.6, 0.7, 0.8, 0.9 })
            foreach (var b in new[] { 0.05, 0.1, 0.2, 0.3, 0.5 })
            {
                var sse = Fit(y, a, b, out _, out _);
                if (sse < best) { best = sse; ba = a; bb = b; }
            }
        Fit(y, ba, bb, out var level, out var trend);
        var sigma = Math.Sqrt(best / Math.Max(1, y.Count - 3));
        var f = new double[horizon]; var lo = new double[horizon]; var hi = new double[horizon];
        double damp = 0;
        for (var h = 1; h <= horizon; h++)
        {
            damp += Math.Pow(Phi, h);
            var v = level + damp * trend;
            var band = 1.96 * sigma * Math.Sqrt(h);
            f[h - 1] = Clamp(v, ceiling); lo[h - 1] = Clamp(v - band, ceiling); hi[h - 1] = Clamp(v + band, ceiling);
        }
        return new Projection(f, lo, hi, ba, bb, sigma);
    }

    static double Clamp(double v, double? ceiling) => Math.Max(0, ceiling is { } c ? Math.Min(c, v) : v);

    static double Fit(IReadOnlyList<double> y, double a, double b, out double level, out double trend)
    {
        level = y[0];
        trend = y[1] - y[0];
        double sse = 0;
        for (var t = 1; t < y.Count; t++)
        {
            var predicted = level + Phi * trend;
            var err = y[t] - predicted;
            sse += err * err;
            var newLevel = a * y[t] + (1 - a) * predicted;
            trend = b * (newLevel - level) + (1 - b) * Phi * trend;
            level = newLevel;
        }
        return sse;
    }
}
