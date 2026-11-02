using Governance.Domain;

namespace Governance.Application.Sla;

/// <summary>
/// SLA arithmetic. Business-day SLAs skip weekends and institutional holidays in the institution's time
/// zone and keep the local time of day; clock SLAs (operational events with after-hours coverage) add hours.
/// All inputs and outputs are UTC.
/// </summary>
public sealed class BusinessCalendar
{
    private readonly TimeZoneInfo _zone;
    private readonly HashSet<DateOnly> _holidays;

    public BusinessCalendar(TimeZoneInfo zone, IEnumerable<DateOnly> holidays)
    {
        _zone = zone;
        _holidays = holidays.ToHashSet();
    }

    public static BusinessCalendar Create(string timeZoneId, IEnumerable<DateOnly> holidays)
    {
        TimeZoneInfo zone;
        try { zone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId); }
        catch (TimeZoneNotFoundException) { zone = TimeZoneInfo.Utc; }
        return new BusinessCalendar(zone, holidays);
    }

    public TimeZoneInfo Zone => _zone;

    public bool IsBusinessDay(DateOnly day)
        => day.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday) && !_holidays.Contains(day);

    public DateTime AddBusinessDays(DateTime utcStart, int days)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utcStart, DateTimeKind.Utc), _zone);
        var day = DateOnly.FromDateTime(local);
        // Work started on a non-business day counts from the next business day.
        while (!IsBusinessDay(day)) { day = day.AddDays(1); local = day.ToDateTime(new TimeOnly(9, 0)); }
        var added = 0;
        while (added < days)
        {
            day = day.AddDays(1);
            if (IsBusinessDay(day)) added++;
        }
        var dueLocal = day.ToDateTime(TimeOnly.FromDateTime(local));
        return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(dueLocal, DateTimeKind.Unspecified), _zone);
    }

    public int BusinessDaysBetween(DateTime utcFrom, DateTime utcTo)
    {
        var a = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utcFrom, DateTimeKind.Utc), _zone));
        var b = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utcTo, DateTimeKind.Utc), _zone));
        var n = 0;
        for (var d = a.AddDays(1); d <= b; d = d.AddDays(1)) if (IsBusinessDay(d)) n++;
        return n;
    }

    /// <summary>Due time and reminder times (50 and 80 percent by default) for a task starting now.</summary>
    public SlaTimes Compute(SlaPolicy? policy, DateTime utcStart)
    {
        if (policy is null || policy.Duration <= 0) return new SlaTimes(null, null, null);
        DateTime due = policy.Unit switch
        {
            "H" => utcStart.AddHours(policy.Duration),
            "D" => utcStart.AddDays(policy.Duration),
            _ when policy.Calendar == "CLOCK" => utcStart.AddDays(policy.Duration),
            _ => AddBusinessDays(utcStart, policy.Duration),
        };
        var span = due - utcStart;
        return new SlaTimes(due,
            utcStart + TimeSpan.FromTicks(span.Ticks * policy.RemindAtPct1 / 100),
            utcStart + TimeSpan.FromTicks(span.Ticks * policy.RemindAtPct2 / 100));
    }

    public string FormatLocal(DateTime utc, string format = "d MMM HH:mm")
        => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), _zone).ToString(format);
}

public sealed record SlaTimes(DateTime? DueUtc, DateTime? Reminder1Utc, DateTime? Reminder2Utc);
