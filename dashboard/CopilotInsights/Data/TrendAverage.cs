namespace CopilotInsights.Data;

/// <summary>
/// The 28-day moving average of a trend (2.5.0): each point is the mean of the points in its window, 28 days or four
/// weeks, so a weekend or a holiday does not dip it. Pure, for the tests.
/// </summary>
public static class TrendAverage
{
    /// <summary>
    /// The trailing mean of <paramref name="raw"/> over <paramref name="window"/> points, for its last
    /// <paramref name="count"/> points (the ones shown; the points before them are the lead-in the first one averages
    /// over). A null, a week not yet over, has no mean of its own and is left out of the mean of every window it falls
    /// in; a window with no value gives null. A window that reaches back before the first point averages the points it has.
    /// </summary>
    public static List<double?> Trailing(IReadOnlyList<double?> raw, int window, int count)
    {
        var result = new List<double?>(count);
        for (var i = Math.Max(0, raw.Count - count); i < raw.Count; i++)
        {
            if (raw[i] is null) { result.Add(null); continue; }
            double sum = 0; var n = 0;
            for (var j = Math.Max(0, i - window + 1); j <= i; j++)
                if (raw[j] is double v) { sum += v; n++; }
            result.Add(n > 0 ? sum / n : null);
        }
        return result;
    }

    /// <summary>
    /// A ratio of two trends over the window, pooled: the numerators added up over the denominators added up, so a day
    /// with little use weighs little, as it does in the period's own figure. Null where the window's denominator is
    /// nothing, and at a point not yet over (either is null). Pairs where either is null are left out of the windows.
    /// </summary>
    public static List<double?> TrailingRatio(IReadOnlyList<double?> numerator, IReadOnlyList<double?> denominator, int window, int count)
    {
        var result = new List<double?>(count);
        for (var i = Math.Max(0, numerator.Count - count); i < numerator.Count; i++)
        {
            if (numerator[i] is null || denominator[i] is null) { result.Add(null); continue; }
            double num = 0, den = 0;
            for (var j = Math.Max(0, i - window + 1); j <= i; j++)
                if (numerator[j] is double a && denominator[j] is double b) { num += a; den += b; }
            result.Add(den > 0 ? num / den : null);
        }
        return result;
    }

    /// <summary>
    /// The 28-day average of the people who used something, shown at the minimum group size or more (2.4.12's rule for a
    /// point, kept for the average). A person counts once in a point, so an average of at least the minimum means at least
    /// that many different people used it in the window. Nobody is 0, and an average in between is a gap.
    /// </summary>
    public static double? PeopleAtMin(double? average, int minimum) =>
        average is not double a ? null : a == 0 ? 0 : a >= minimum ? a : null;
}
