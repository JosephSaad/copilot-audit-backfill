using System.Globalization;

namespace CopilotInsights.Infrastructure;

/// <summary>
/// The start page's figures, written as the pages they open write them: money as Costs and savings' tiles do (whole,
/// with the currency), the return as its Return on the spend tile does, counts with thousands separators.
/// </summary>
public static class StartFigures
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    public static string Money(decimal v, string cur) => Math.Round(v, 0).ToString("#,0", Inv) + " " + cur;
    public static string Times(decimal r) => r >= 10 ? $"{r.ToString("#,0", Inv)}×" : $"{r.ToString("0.0", Inv)}×";
    public static string Count(int n) => n.ToString("#,0", Inv);
}
