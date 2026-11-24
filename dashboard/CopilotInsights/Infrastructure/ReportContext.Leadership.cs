namespace CopilotInsights.Infrastructure;

public sealed partial class ReportContext
{
    /// <summary>
    /// Whether <see cref="PerPersonFromWeeks"/> should answer for this context: the refresh keeps no period total ending on
    /// its last day (the period before, on Board pack), it keeps the weeks, and the period is longer than a month, where
    /// summing every person's days took seconds (180 days before 180: 18 million rows).
    /// </summary>
    public bool WeeksAnswer => Rollups && !RolledDays.Contains(Choice) && Days > 30;

    /// <summary>
    /// Each person's sums over the period, as <see cref="PerPerson"/> sums them from the days, from dash.UserWeek for the
    /// whole weeks inside it and dash.UserDay for the rest: the weeks the period cuts, and every week that reaches its last
    /// 7 days, so whether a person was active in those 7 days is still read from their days. Columns: UserKey, ActiveDays,
    /// Last7 (1 when they had an interaction on one of the last 7 days) and <see cref="Sums"/>. Every day of the period is
    /// in one of the two parts, once; a person with no day in it has no row.
    /// </summary>
    public string PerPersonFromWeeks()
    {
        static string Day(string key) => $"DATEFROMPARTS({key} / 10000, {key} / 100 % 100, {key} % 100)";
        var cut = "(SELECT w.WeekEnding FROM bi.DimDate AS w WHERE w.DateKey BETWEEN @k0 AND @k1 GROUP BY w.WeekEnding HAVING COUNT(*) < 7)";
        return $@"(SELECT x.UserKey, SUM(x.ActiveDays) AS ActiveDays, MAX(x.Last7) AS Last7, {Summed("x")}
    FROM (SELECT uw.UserKey, CAST(uw.ActiveDays AS INT) AS ActiveDays, 0 AS Last7, {string.Join(", ", SumColumns.Select(s => "uw." + s))}
          FROM dash.UserWeek AS uw
          WHERE uw.WeekEnding BETWEEN DATEADD(DAY, 6, {Day("@k0")}) AND {Day("@k1")} AND uw.WeekEnding < {Day("@k7")}
            AND uw.WeekEnding NOT IN {cut}
          UNION ALL
          SELECT ud.UserKey, CASE WHEN ud.Interactions > 0 THEN 1 ELSE 0 END, CASE WHEN ud.Interactions > 0 AND ud.DateKey >= @k7 THEN 1 ELSE 0 END, {string.Join(", ", SumColumns.Select(s => "ud." + s))}
          FROM dash.UserDay AS ud JOIN bi.DimDate AS d ON d.DateKey = ud.DateKey
          WHERE ud.DateKey BETWEEN @k0 AND @k1 AND (d.WeekEnding >= {Day("@k7")} OR d.WeekEnding IN {cut})) AS x
    GROUP BY x.UserKey)";
    }
}
