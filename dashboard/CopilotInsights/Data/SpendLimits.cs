namespace CopilotInsights.Data;

/// <summary>
/// The rules of Forecasting › Spend limits: where a spending limit stands in its period, where it is heading, when it
/// is reached, and what to set it to next quarter. Pure, for the page, the alert and the tests. Days are whole days:
/// a period's spend is measured to <c>asOf</c> (the newest complete day of the source), the run rate from the last
/// seven complete days, as Credit commitment does.
/// </summary>
public static class SpendLimits
{
    public const string Minute = "Minute", Hourly = "Hourly", Daily = "Daily", Weekly = "Weekly", Monthly = "Monthly", Yearly = "Yearly";

    /// <summary>The days of run rate a projection needs.</summary>
    public const int RateDays = 7;

    /// <summary>The months of history a suggestion needs (the forecast's own minimum).</summary>
    public const int SuggestMonths = 6;

    /// <summary>A period shorter than a day: a rate or an hourly quota, measured from daily counts only roughly.</summary>
    public static bool IsShort(string? period) => period is Minute or Hourly;

    /// <summary>The period holding <paramref name="day"/>: its first and last day. Weeks start on Monday.</summary>
    public static (DateTime Start, DateTime End) PeriodOf(string? period, DateTime day)
    {
        var d = day.Date;
        switch (period)
        {
            case Daily or Minute or Hourly: return (d, d);
            case Weekly:
                var s = d.AddDays(-(((int)d.DayOfWeek + 6) % 7));
                return (s, s.AddDays(6));
            case Yearly: return (new DateTime(d.Year, 1, 1), new DateTime(d.Year, 12, 31));
            default:
                var m = new DateTime(d.Year, d.Month, 1);
                return (m, m.AddMonths(1).AddDays(-1));
        }
    }

    /// <summary>Where a limit stands, and where it is heading by the end of its period.</summary>
    public sealed record Position(decimal? Limit, decimal Spent, double? Share, int DaysLeft, decimal? Projected, DateTime? ReachedOn, bool Reached, bool TooEarly);

    /// <summary>
    /// The position of a limit on <paramref name="asOf"/>: <paramref name="daily"/> is the spend of each day (any days;
    /// those outside the period are used for the run rate only). The projection is the spend so far plus the average of
    /// the last <see cref="RateDays"/> complete days for each day left; with fewer days of history it is too early.
    /// ReachedOn is the day the spend reached the limit, or the day the projection does, within the period.
    /// </summary>
    public static Position Where(decimal? limit, string? period, DateTime asOf, IReadOnlyDictionary<DateTime, decimal> daily)
    {
        var (start, end) = PeriodOf(period, asOf);
        var day = asOf.Date;
        decimal spent = 0;
        DateTime? reachedOn = null;
        for (var d = start; d <= day; d = d.AddDays(1))
        {
            spent += daily.GetValueOrDefault(d);
            if (reachedOn is null && limit is { } l && l > 0 && spent >= l) reachedOn = d;
        }
        var left = Math.Max(0, (end - day).Days);
        var history = Enumerable.Range(0, RateDays).Select(i => day.AddDays(-i)).ToList();
        var tooEarly = !daily.Keys.Any(k => k <= day.AddDays(-(RateDays - 1)));
        decimal? projected = null;
        if (!tooEarly)
        {
            var rate = history.Sum(d => daily.GetValueOrDefault(d)) / RateDays;
            projected = spent + rate * left;
            if (reachedOn is null && limit is { } l && l > 0 && rate > 0 && projected >= l)
                reachedOn = day.AddDays((int)Math.Ceiling((double)((l - spent) / rate)));
        }
        double? share = limit is { } lim && lim > 0 ? (double)(spent / lim) : null;
        return new(limit, spent, share, left, projected, reachedOn is { } r && r <= end ? r : null, limit is { } x && x > 0 && spent >= x, tooEarly);
    }

    /// <summary>Whether to warn: projected to be reached <paramref name="alertDays"/> or more days before its period ends, already reached, or past the share an administrator set.</summary>
    public static bool Warn(Position p, DateTime asOf, string? period, int alertDays, int? warnAtPercent)
    {
        if (p.Limit is null or <= 0) return false;
        if (p.Reached) return true;
        if (warnAtPercent is { } w && p.Share >= w / 100.0) return true;
        var (_, end) = PeriodOf(period, asOf);
        return p.ReachedOn is { } r && (end - r).Days >= alertDays;
    }

    /// <summary>For a daily quota measured each day: the share of the last days that reached 80% and 100% of it.</summary>
    public static (double At80, double At100) ShortPeriods(decimal limit, IReadOnlyList<decimal> perPeriod) =>
        limit <= 0 || perPeriod.Count == 0 ? (0, 0)
            : (perPeriod.Count(v => v >= limit * 0.8m) / (double)perPeriod.Count, perPeriod.Count(v => v >= limit) / (double)perPeriod.Count);

    /// <summary>
    /// Next quarter's suggested monthly limit: the forecast of each of the next three months from the monthly spend
    /// (<see cref="SuggestMonths"/> months at least), at P80 or P95, the highest of the three, plus the headroom,
    /// rounded up to a sensible step for the unit. Null under <see cref="SuggestMonths"/> months.
    /// </summary>
    public static decimal? Suggest(IReadOnlyList<decimal> monthly, string unit, bool p95, int headroomPercent)
    {
        if (monthly.Count < SuggestMonths) return null;
        var f = Forecast.Holt(monthly.Select(v => (double)v).ToList(), 3, null);
        if (f is null) return null;
        var z = p95 ? 1.645 : 0.8416;
        var top = Enumerable.Range(1, 3).Max(h => f.At(h) + z * f.Error * Math.Sqrt(h));
        var v = Math.Max(0, top) * (1 + headroomPercent / 100.0);
        return RoundUp((decimal)v, unit);
    }

    /// <summary>Up to a step a person would set: 100 credits, a million tokens, 10 US dollars.</summary>
    public static decimal RoundUp(decimal v, string unit)
    {
        var step = unit switch { "Tokens" => 1_000_000m, "USD" => 10m, _ => 100m };
        return Math.Ceiling(v / step) * step;
    }

    /// <summary>What a kind is called on the page.</summary>
    public static string KindLabel(string kind) => kind switch
    {
        "CopilotUser" => "Copilot spending policy (per person)",
        "CopilotResource" => "Copilot spending policy (environment or agent)",
        "GatewayQuota" => "Gateway token quota",
        "GatewayRate" => "Gateway token rate",
        "GitHubBudget" => "GitHub budget",
        _ => kind,
    };
}
