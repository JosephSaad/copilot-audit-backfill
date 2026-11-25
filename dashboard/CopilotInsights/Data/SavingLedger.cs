using System.Text.Json;
using CopilotInsights.Infrastructure;

namespace CopilotInsights.Data;

/// <summary>What the saving ledger holds: saved to date, and the savings found in its latest month.</summary>
public sealed record LedgerTotals(string Currency, decimal SavedToDate, DateTime? SavedSince, int Months, decimal FoundHigh, decimal FoundAll, DateTime? FoundMonth)
{
    public bool Any => SavedToDate > 0 || FoundAll > 0;
}

/// <summary>
/// The saving ledger (dash.SavingLedger): money saved and found, a month at a time. The nightly refresh writes Saved
/// (dash.usp_RefreshSavingLedger); <see cref="RecordFoundAsync"/> writes Found from the savings Costs and savings works out
/// for the whole organization. Both are a month's amounts; saved to date is the sum of the Saved months.
/// </summary>
public static class SavingLedger
{
    /// <summary>One row of the month's savings found: a saving the page shows, by its area, confidence and page.</summary>
    public sealed record FoundRow(string Area, string Confidence, string Source, decimal Amount, string Title);

    /// <summary>
    /// The savings found as ledger rows: only savings (not ways to grow the return), only those above nothing, one row for
    /// each area, confidence and page (two savings opening the same page are summed), the page's address as the source.
    /// </summary>
    public static List<FoundRow> FoundRows(IEnumerable<Opportunity> opportunities) =>
        [.. opportunities.Where(o => o.Kind == OpportunityKind.Save && o.Saving > 0)
            .GroupBy(o => (o.Area, o.Confidence, Source: o.Href ?? o.Title))
            .Select(g => new FoundRow(g.Key.Area, g.Key.Confidence, Cut(g.Key.Source, 400), Math.Round(g.Sum(o => o.Saving), 2), Cut(string.Join("; ", g.Select(o => o.Title)), 400)))
            .OrderBy(r => r.Area, StringComparer.Ordinal).ThenBy(r => r.Source, StringComparer.Ordinal)];

    static string Cut(string s, int n) => s.Length <= n ? s : s[..n];

    /// <summary>
    /// Replaces the month's Found rows with these, in one transaction; earlier months are kept. Does nothing over a
    /// database without the table, or a site account not granted it (the installer grants it): the page never fails on it.
    /// </summary>
    public static async Task RecordFoundAsync(Db db, DateTime month, string currency, IReadOnlyList<FoundRow> rows)
    {
        var first = new DateTime(month.Year, month.Month, 1);
        try
        {
            await db.ExecuteAsync(@"
IF OBJECT_ID(N'dash.SavingLedger', N'U') IS NULL RETURN;
SET XACT_ABORT ON;
BEGIN TRANSACTION;
DELETE FROM dash.SavingLedger WHERE [Month] = @first AND Kind = 'Found';
INSERT INTO dash.SavingLedger ([Month], Area, Kind, Confidence, Source, Amount, Currency, Title)
SELECT @first, j.Area, 'Found', j.Confidence, j.Source, j.Amount, @currency, j.Title
FROM OPENJSON(@json) WITH (Area NVARCHAR(100), Confidence VARCHAR(8), Source NVARCHAR(400), Amount DECIMAL(19, 2), Title NVARCHAR(400)) AS j;
COMMIT;", new { first, currency, json = JsonSerializer.Serialize(rows) });
        }
        catch (Microsoft.Data.SqlClient.SqlException) { /* not installed or not granted yet: the ledger waits for the installer */ }
    }

    /// <summary>Saved to date, and the latest month's savings found; null over a database without the ledger.</summary>
    public static async Task<LedgerTotals?> ReadAsync(Db db)
    {
        try
        {
            return (await db.QueryAsync<LedgerTotals>(@"
IF OBJECT_ID(N'dash.SavingLedger', N'U') IS NULL BEGIN SELECT TOP (0) N'' AS Currency, CAST(0 AS DECIMAL(19, 2)) AS SavedToDate, CAST(NULL AS DATETIME2(0)) AS SavedSince, 0 AS Months, CAST(0 AS DECIMAL(19, 2)) AS FoundHigh, CAST(0 AS DECIMAL(19, 2)) AS FoundAll, CAST(NULL AS DATETIME2(0)) AS FoundMonth; RETURN; END;
DECLARE @fm DATE = (SELECT MAX([Month]) FROM dash.SavingLedger WHERE Kind = 'Found');
SELECT ISNULL((SELECT TOP (1) Currency FROM dash.SavingLedger ORDER BY [Month] DESC, WrittenUtc DESC), N'USD') AS Currency,
       ISNULL((SELECT SUM(Amount) FROM dash.SavingLedger WHERE Kind = 'Saved'), 0) AS SavedToDate,
       CAST((SELECT MIN([Month]) FROM dash.SavingLedger WHERE Kind = 'Saved' AND Amount > 0) AS DATETIME2(0)) AS SavedSince,
       (SELECT COUNT(*) FROM dash.SavingLedger WHERE Kind = 'Saved' AND Amount > 0) AS Months,
       ISNULL((SELECT SUM(Amount) FROM dash.SavingLedger WHERE Kind = 'Found' AND [Month] = @fm AND Confidence = 'High'), 0) AS FoundHigh,
       ISNULL((SELECT SUM(Amount) FROM dash.SavingLedger WHERE Kind = 'Found' AND [Month] = @fm), 0) AS FoundAll,
       CAST(@fm AS DATETIME2(0)) AS FoundMonth;")).FirstOrDefault();
        }
        catch (Microsoft.Data.SqlClient.SqlException) { return null; }
    }

    /// <summary>The start page's line: saved to date and the high-confidence savings found a month, in words.</summary>
    public static string Line(LedgerTotals t) =>
        (t.SavedToDate > 0 ? $"{CostRules.Money(t.SavedToDate, t.Currency)} saved to date through this dashboard" : "Nothing saved through this dashboard yet")
        + (t.FoundHigh > 0 ? $", and {CostRules.Money(t.FoundHigh, t.Currency)} a month more found with high confidence." : ".");
}
