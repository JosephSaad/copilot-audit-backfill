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

    /// <summary>The use sums of a trend point, named as <see cref="CopilotInsights.Data.TrendRow"/> names them, from dash.UserWeek's or dash.UserDay's columns under <paramref name="alias"/>.</summary>
    static string PointSumColumns(string alias) =>
        $@"SUM(CAST({alias}.Interactions AS BIGINT)) AS Interactions, SUM(CAST({alias}.Prompts AS BIGINT)) AS Prompts, SUM(CAST({alias}.Sessions AS BIGINT)) AS Sessions,
             SUM(CAST({alias}.MultiTurnSessions AS BIGINT)) AS MultiTurn, SUM(CAST({alias}.ChatInteractions AS BIGINT)) AS Chat, SUM(CAST({alias}.AgentInteractions AS BIGINT)) AS Agent,
             SUM(CAST({alias}.WebGrounded AS BIGINT)) AS Web, SUM(CAST({alias}.Jailbreak AS BIGINT)) AS Jailbreak, SUM(CAST({alias}.PromptInjection AS BIGINT)) AS Injection,
             SUM(CAST({alias}.BlockedResource AS BIGINT)) AS Blocked, SUM(CAST({alias}.LabeledContent AS BIGINT)) AS Labeled";

    /// <summary>
    /// The use sums at each weekly trend point, in scope, as a derived table: Bucket and the sums of
    /// <see cref="PointSumColumns"/>, a point's rows to be added up by the caller. A week ending between the period's
    /// sixth day and its last has all seven days in it and is dash.UserWeek's, summed a week at a time; the days of every
    /// other week (the one or two the period cuts) are dash.UserDay's. The same days as <see cref="PerPoint"/>, without a
    /// row for each person: for a trend that counts no one. Only once the totals are built and the trend is weekly.
    /// </summary>
    public string PointSums()
    {
        static string Day(string key) => $"DATEFROMPARTS({key} / 10000, {key} / 100 % 100, {key} % 100)";
        var whole = $"BETWEEN DATEADD(DAY, 6, {Day("@k0")}) AND {Day("@k1")}";
        return $@"(SELECT uw.WeekEnding AS Bucket, {PointSumColumns("uw")}
      FROM dash.UserWeek AS uw {Scope("uw.UserKey")}
      WHERE uw.WeekEnding {whole}
      GROUP BY uw.WeekEnding
      UNION ALL
      SELECT d.WeekEnding, {PointSumColumns("ud")}
      FROM dash.UserDay AS ud {Scope("ud.UserKey")} JOIN bi.DimDate AS d ON d.DateKey = ud.DateKey
      WHERE ud.DateKey BETWEEN @k0 AND @k1 AND NOT (d.WeekEnding {whole})
      GROUP BY d.WeekEnding)";
    }
}
