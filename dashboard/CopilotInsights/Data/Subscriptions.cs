using System.Globalization;

namespace CopilotInsights.Data;

/// <summary>
/// The rules of a report subscription (dash.ReportSubscription), pure, for the page, the sender and the tests. A person
/// subscribes to a report for themselves: its headline figures and charts by email, never its lists, at a time they choose.
/// A period of up to 30 days may be sent daily (on weekdays), weekly or monthly; a longer one, or all the data, weekly or
/// monthly: a daily email of a year's figures says the same thing every day.
/// </summary>
public static class Subscriptions
{
    public const string Daily = "Daily", Weekly = "Weekly", Monthly = "Monthly";
    public static readonly string[] Frequencies = [Daily, Weekly, Monthly];

    /// <summary>
    /// The report a subscription may be to: a report in the menu, not an Admin page, not a list that names people (a page
    /// that needs a role), and not the group overview of several tenants. Null for any other address.
    /// </summary>
    public static Pages.NavPage? Report(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        var bare = path.Length > 1 ? path.TrimEnd('/') : path;
        return Pages.Nav.Groups.Where(g => g.Persona != "Admin").SelectMany(g => g.Pages)
            .FirstOrDefault(p => string.Equals(p.Path, bare, StringComparison.OrdinalIgnoreCase) && !p.Admin && p.Role is null && !p.ManyTenants);
    }

    /// <summary>
    /// What a report needs chosen on it before it means anything, so before it may be subscribed to: in short (for the
    /// report's top), then each thing in words with the filters that give it (any one of them). A report that shows only its
    /// picker until then would send an empty email. Person record names a person, so it is not sent; it is listed should
    /// that change.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, (string Short, (string What, string[] Keys)[] Items)> Needs =
        new Dictionary<string, (string Short, (string What, string[] Keys)[] Items)>(StringComparer.OrdinalIgnoreCase)
        {
            ["/leadership/compare"] = ("two managers", [("the first manager", ["a"]), ("the second manager", ["b"])]),
            ["/agents/detail"] = ("an agent", [("an agent", ["agent", "inv"])]),
            ["/apps/detail"] = ("an app", [("an app", ["app"])]),
            ["/compliance/person"] = ("a person", [("a person", ["upn"])]),
        };

    /// <summary>
    /// What is still to be chosen on the report before it may be subscribed to, in words ("choose the first manager and the
    /// second manager on the report first: ..."); null when nothing is missing. <paramref name="query"/> is the report's own
    /// query string or a subscription's kept filters, with or without the leading "?".
    /// </summary>
    public static string? Missing(string? path, string? query)
    {
        if (NeedsOf(path) is not { } needs) return null;
        var given = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(query ?? "");
        var missing = needs.Items.Where(n => !n.Keys.Any(k => given.TryGetValue(k, out var v) && !string.IsNullOrWhiteSpace(v.ToString())))
            .Select(n => n.What).ToList();
        return missing.Count == 0 ? null
            : $"choose {(missing.Count == 1 ? missing[0] : string.Join(", ", missing[..^1]) + " and " + missing[^1])} on the report first: until then it has nothing to send.";
    }

    /// <summary>What the report needs chosen on it, or null when it needs nothing.</summary>
    public static (string Short, (string What, string[] Keys)[] Items)? NeedsOf(string? path) =>
        path is { Length: > 0 } && Needs.TryGetValue(path.Length > 1 ? path.TrimEnd('/') : path, out var n) ? n : null;

    /// <summary>The longest period that may be sent daily.</summary>
    public const int DailyUpTo = 30;

    /// <summary>The latest time a subscription may be sent.</summary>
    public static readonly TimeSpan Latest = new(22, 0, 0);

    /// <summary>A send this much past its time (the site was down) is not sent late: the next one is waited for.</summary>
    public static readonly TimeSpan MissedAfter = TimeSpan.FromHours(12);

    /// <summary>How often a period may be sent: daily only up to 30 days; all the data (0) is a long period.</summary>
    public static IReadOnlyList<string> Allowed(int days) =>
        days is > 0 and <= DailyUpTo ? Frequencies : [Weekly, Monthly];

    public static bool IsAllowed(int days, string? frequency) => frequency is not null && Allowed(days).Contains(frequency);

    /// <summary>The send times offered: every half hour from the earliest to 22:00.</summary>
    public static IReadOnlyList<TimeSpan> Slots(TimeSpan earliest)
    {
        var start = Round(earliest);
        var list = new List<TimeSpan>();
        for (var t = start; t <= Latest; t += TimeSpan.FromMinutes(30)) list.Add(t);
        return list.Count == 0 ? [Latest] : list;
    }

    /// <summary>Up to the next half hour: 07:10 is 07:30.</summary>
    static TimeSpan Round(TimeSpan t)
    {
        var m = (int)Math.Ceiling(t.TotalMinutes / 30.0) * 30;
        return TimeSpan.FromMinutes(Math.Clamp(m, 0, (int)Latest.TotalMinutes));
    }

    /// <summary>A time setting such as "07:30"; the fallback when it is not one.</summary>
    public static TimeSpan ParseTime(string? value, TimeSpan fallback) =>
        TimeSpan.TryParseExact(value?.Trim(), [@"hh\:mm", @"h\:mm"], CultureInfo.InvariantCulture, out var t) && t >= TimeSpan.Zero && t < TimeSpan.FromDays(1) ? t : fallback;

    public static string TimeText(TimeSpan t) => t.ToString(@"hh\:mm", CultureInfo.InvariantCulture);

    /// <summary>
    /// What is wrong with a subscription as asked for, in words; null when it may be kept. The period is one a report
    /// offers, the frequency one the period allows, the day one the frequency needs, the time one of the slots.
    /// </summary>
    public static string? Problem(int days, string? frequency, int? dayOfWeek, int? dayOfMonth, TimeSpan? time, TimeSpan earliest)
    {
        if (!Infrastructure.ReportContext.DayChoices.Contains(days)) return "choose a period the report offers.";
        if (frequency is null || !Frequencies.Contains(frequency)) return "choose how often it is sent.";
        if (!IsAllowed(days, frequency)) return $"a period longer than {DailyUpTo} days is sent weekly or monthly, not daily.";
        if (frequency == Weekly && dayOfWeek is not (>= 0 and <= 6)) return "choose the day of the week it is sent.";
        if (frequency == Monthly && dayOfMonth is not (>= 1 and <= 28)) return "choose the day of the month it is sent (1 to 28).";
        if (time is null || !Slots(earliest).Contains(time.Value)) return $"choose a time from {TimeText(Slots(earliest)[0])} to {TimeText(Latest)}, on the hour or half hour.";
        return null;
    }

    /// <summary>
    /// The next send after <paramref name="afterUtc"/>, in UTC: daily on the next weekday at the time, weekly on the day,
    /// monthly on the day of the month; all at the time in <paramref name="zone"/>. A time that does not exist that day
    /// (the clocks going forward) is the hour after.
    /// </summary>
    public static DateTime NextDue(string frequency, int? dayOfWeek, int? dayOfMonth, TimeSpan time, DateTime afterUtc, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(afterUtc, DateTimeKind.Utc), zone);
        for (var day = local.Date; day <= local.Date.AddDays(62); day = day.AddDays(1))
        {
            var fits = frequency switch
            {
                Daily => day.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday),
                Weekly => (int)day.DayOfWeek == (dayOfWeek ?? 1),
                _ => day.Day == Math.Clamp(dayOfMonth ?? 1, 1, 28),
            };
            if (!fits) continue;
            var at = DateTime.SpecifyKind(day + time, DateTimeKind.Unspecified);
            if (zone.IsInvalidTime(at)) at = at.AddHours(1);
            var utc = TimeZoneInfo.ConvertTimeToUtc(at, zone);
            if (utc > afterUtc) return utc;
        }
        throw new InvalidOperationException("no send day found in two months.");
    }

    /// <summary>When it goes, in words for a sentence: "weekdays at 08:00", "Mondays at 07:30", "on the 1st of each month at 09:00".</summary>
    public static string When(string frequency, int? dayOfWeek, int? dayOfMonth, TimeSpan time) => frequency switch
    {
        Daily => $"weekdays at {TimeText(time)}",
        Weekly => $"{(DayOfWeek)(dayOfWeek ?? 1)}s at {TimeText(time)}",
        _ => $"on the {Ordinal(dayOfMonth ?? 1)} of each month at {TimeText(time)}",
    };

    public static string Ordinal(int n) => n + (n % 100 is 11 or 12 or 13 ? "th" : (n % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" });

    /// <summary>
    /// A report's own filters kept with a subscription: its query without the period (kept apart), the page of a list, a
    /// handler, the tenant, or a sort; each key and value as asked, at most 1,000 characters. The page checks them again
    /// at every send as it does for anyone, so nothing here widens what the person sees.
    /// </summary>
    public static string? KeepQuery(IEnumerable<KeyValuePair<string, string>> query)
    {
        var kept = query.Where(kv => kv.Key is { Length: > 0 and <= 40 } k && k.All(c => char.IsLetterOrDigit(c) || c is '_' or '-' or '.')
                                     && !Skipped.Contains(kv.Key) && kv.Value.Length <= 400)
            .Select(kv => Uri.EscapeDataString(kv.Key) + "=" + Uri.EscapeDataString(kv.Value)).ToList();
        var q = string.Join("&", kept);
        return q.Length == 0 ? null : q.Length > 1000 ? q[..q.LastIndexOf('&', 1000)] : q;
    }

    static readonly HashSet<string> Skipped = new(["d", "handler", "p", "tenant", "sort", "dir", "add", "id", "q", "home"], StringComparer.OrdinalIgnoreCase);

    /// <summary>The report's address as sent: its path, the period and the kept filters.</summary>
    public static string Address(string path, int days, string? query) =>
        path + "?d=" + days.ToString(CultureInfo.InvariantCulture) + (string.IsNullOrEmpty(query) ? "" : "&" + query);

    /// <summary>The period in words: "the last 30 days", or "all the data".</summary>
    public static string Period(int days) => days == Infrastructure.ReportContext.AllData ? "all the data" : $"the last {days:#,0} days";

    /// <summary>Whether a send that was due at <paramref name="dueUtc"/> is too late to go now.</summary>
    public static bool Missed(DateTime dueUtc, DateTime nowUtc) => nowUtc - dueUtc > MissedAfter;

    /// <summary>
    /// Whether to send now or wait for the night's refresh: it goes once the refresh has ended since the start of today
    /// (in the zone), or, if it has not, once it is past the wait-until time, with a note. Pure.
    /// </summary>
    public static (bool Go, bool Late) Ready(DateTime? refreshedUtc, DateTime nowUtc, TimeSpan waitUntil, TimeZoneInfo zone)
    {
        var now = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, zone);
        var refreshed = refreshedUtc is { } r ? TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(r, DateTimeKind.Utc), zone) : (DateTime?)null;
        if (refreshed is { } done && done.Date >= now.Date) return (true, false);
        return now.TimeOfDay >= waitUntil ? (true, true) : (false, false);
    }
}
