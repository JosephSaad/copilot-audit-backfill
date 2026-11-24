namespace CopilotInsights.Infrastructure;

/* ---- The totals of resources read ----------------------------------------------------------------------------
   dash.usp_RefreshResourceTotals keeps each person's resources read per combination of type, label, file type and
   blocked over each period (dash.ReadMixPeriod), dash.ResourceDay summed per person over each period
   (dash.ResourcePeriod), each person's last two days at each site (dash.SiteReader) and the conversations as a
   columnstore (dash.SessionSpan). Each ends on the last day with data or the day before, which the setting
   DashResourceTotalsEnds records once they are built: until then, or for a period ending on another day, a page reads
   bi.FactAccessedResource, dash.ResourceDay and bi.FactSession as before. The answer is the same either way. */
public sealed partial class ReportContext
{
    /// <summary>Whether the totals of resources read answer for this context: the refresh built them for a period ending on its last day.</summary>
    public bool ResourceTotals { get; init; }

    /// <summary>Whether the setting DashResourceTotalsEnds (the two days, comma-separated) names this end day.</summary>
    public static bool EndsHold(string? ends, int endKey) =>
        (ends ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Contains(endKey.ToString(System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>
    /// The suffix of dash.ReadMixPeriod's and dash.ResourcePeriod's column for the period chosen: Reads30, N30 and D30 for
    /// 30 days, ReadsAll for All data. A fixed list, never text from a request.
    /// </summary>
    public string PeriodColumn => Choice switch
    {
        7 => "7", 14 => "14", 30 => "30", 90 => "90", 180 => "180", 365 => "365", 730 => "730", AllData => "All",
        _ => throw new InvalidOperationException($"No period total is kept for {Choice} days."),
    };

    /// <summary>
    /// Each person's resources read over the period, per combination, as a derived table: UserKey, ResourceTypeKey,
    /// SensitivityLabelKey (1: no label), FileTypeKey, IsBlocked and Reads, the rows the period has. Only where
    /// <see cref="ResourceTotals"/>: else a page counts bi.FactAccessedResource as before. Not scoped: join the scope to it.
    /// </summary>
    public string ReadMix() =>
        $@"(SELECT rm.UserKey, m.ResourceTypeKey, m.SensitivityLabelKey, m.FileTypeKey, m.IsBlocked, CAST(rm.Reads{PeriodColumn} AS BIGINT) AS Reads
    FROM dash.ReadMixPeriod AS rm JOIN dash.ReadMix AS m ON m.MixKey = rm.MixKey
    WHERE rm.EndKey = @k1 AND rm.Reads{PeriodColumn} > 0)";

    /// <summary>
    /// dash.ResourceDay summed per person over the period, as a derived table: Kind (L a label, S a labeled site, B a
    /// blocked site, A all labels, X all blocked sites), ItemKey (dash.ResourceItem; 0 for A and X), UserKey, N (the
    /// interactions) and Days (A only: the days with labeled content). Only where <see cref="ResourceTotals"/>.
    /// </summary>
    public string ResourceSums(string kinds) =>
        $@"(SELECT rp.Kind, rp.ItemKey, rp.UserKey, rp.N{PeriodColumn} AS N, CAST(rp.D{PeriodColumn} AS INT) AS Days
    FROM dash.ResourcePeriod AS rp WHERE rp.EndKey = @k1 AND rp.Kind IN ({kinds}) AND rp.N{PeriodColumn} > 0)";

    /// <summary>
    /// Each person's conversations in each app over the period, as a derived table: UserKey, AppKey, M (their minutes),
    /// N (those with minutes: more than one prompt) and S (all of them), for the people and apps with one. Only where
    /// <see cref="ResourceTotals"/>. Not scoped: join the scope to it.
    /// </summary>
    public string SessionSums() =>
        $@"(SELECT sp.UserKey, sp.AppKey, sp.Minutes{PeriodColumn} AS M, sp.Spanned{PeriodColumn} AS N, sp.Sessions{PeriodColumn} AS S
    FROM dash.SessionPeriod AS sp WHERE sp.EndKey = @k1 AND sp.Sessions{PeriodColumn} > 0)";

    /// <summary>
    /// SQL: the dash.SiteReader row under <paramref name="alias"/> read its site between @k0 and @k1. Its last day, where
    /// that is not after the period's, else the day before it: the period ends on the data's last day or the one before.
    /// </summary>
    public static string SiteRead(string alias) => $"CASE WHEN {alias}.LastKey <= @k1 THEN {alias}.LastKey ELSE {alias}.PrevKey END >= @k0";
}
