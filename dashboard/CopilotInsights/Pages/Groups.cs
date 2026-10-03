using CopilotInsights.Charts;
using CopilotInsights.Data;
using Microsoft.AspNetCore.Html;

namespace CopilotInsights.Pages;

/// <summary>A breakdown by one directory attribute: small groups folded, an adoption chart and a table.</summary>
public static class Groups
{
    public static List<GroupRow> Fold(InsightsPage page, IEnumerable<GroupRow> rows, int minGroup, out int folded) =>
        SmallGroups.Fold(rows, r => r.People, r => Metrics.CombineGroups(r, "Smaller groups"), minGroup, out folded);

    public static HtmlString AdoptionBars(IReadOnlyList<GroupRow> rows, string aria, int min, int take = 20) =>
        Chart.Adoption(rows.Select(r => new Chart.AdoptionGroup(r.Name, r.ActiveLicensed, r.Licensed, Note: $"{r.ActiveLicensed:#,0} of {r.Licensed:#,0} licensed; {r.People:#,0} people")), min, aria, take);

    /// <summary>
    /// The table under a breakdown. Adoption is given only where a group's license holders reach the minimum group
    /// size (2.4.12): a group folded by people can still hold one license holder, whose own use the rate would be.
    /// </summary>
    public static Table Table(IReadOnlyList<GroupRow> rows, string first, int min)
    {
        var t = new Table
        {
            Headers = [first, "People", "License holders", "Share with a license", "Used Copilot", "Adoption", "Days active per person", "Interactions per active person", "Sessions with a follow-up"],
            Numeric = new HashSet<int> { 1, 2, 3, 4, 5, 6, 7, 8 },
        };
        foreach (var r in rows.OrderByDescending(r => r.People))
            t.Rows.Add([r.Name, r.People, r.Licensed, Pct(r.Licensed, r.People), r.Active, r.Licensed >= min ? Pct(r.ActiveLicensed, r.Licensed) : null,
                        r.Active > 0 ? Math.Round(r.ActiveDays / (double)r.Active, 1) : null,
                        r.Active > 0 ? Math.Round(r.Interactions / (double)r.Active, 1) : null, Pct(r.MultiTurn, r.Sessions)]);
        return t;
    }

    static string Pct(double a, double b) => Chart.Format(b > 0 ? a / b : null, Fmt.Percent);
}
