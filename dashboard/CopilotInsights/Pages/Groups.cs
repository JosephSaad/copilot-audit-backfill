using CopilotInsights.Charts;
using CopilotInsights.Data;
using Microsoft.AspNetCore.Html;

namespace CopilotInsights.Pages;

/// <summary>A breakdown by one directory attribute: small groups folded, an adoption chart and a table.</summary>
public static class Groups
{
    public static List<GroupRow> Fold(InsightsPage page, IEnumerable<GroupRow> rows, int minGroup, out int folded)
    {
        var list = rows.ToList();
        var small = list.Where(r => r.People < minGroup).ToList();
        folded = small.Count;
        var keep = list.Where(r => r.People >= minGroup).ToList();
        if (small.Count > 0) keep.Add(Metrics.CombineGroups(small, "Smaller groups"));
        return keep;
    }

    public static HtmlString AdoptionBars(IReadOnlyList<GroupRow> rows, string aria, int take = 20) =>
        Chart.Bars(rows.Where(r => r.Licensed > 0).OrderByDescending(r => (double)r.ActiveLicensed / r.Licensed).Take(take)
            .Select(r => new Bar(r.Name, r.ActiveLicensed / (double)r.Licensed, Note: $"{r.ActiveLicensed:#,0} of {r.Licensed:#,0} licensed; {r.People:#,0} people")).ToList(),
            Fmt.Percent, aria, 1);

    public static Table Table(IReadOnlyList<GroupRow> rows, string first)
    {
        var t = new Table
        {
            Headers = [first, "People", "Licensed", "Share licensed", "Active", "Adoption", "Days active per person", "Interactions per active person", "Multi-turn sessions"],
            Numeric = new HashSet<int> { 1, 2, 3, 4, 5, 6, 7, 8 },
        };
        foreach (var r in rows.OrderByDescending(r => r.People))
            t.Rows.Add([r.Name, r.People, r.Licensed, Pct(r.Licensed, r.People), r.Active, Pct(r.ActiveLicensed, r.Licensed),
                        r.Active > 0 ? Math.Round(r.ActiveDays / (double)r.Active, 1) : null,
                        r.Active > 0 ? Math.Round(r.Interactions / (double)r.Active, 1) : null, Pct(r.MultiTurn, r.Sessions)]);
        return t;
    }

    static string Pct(double a, double b) => Chart.Format(b > 0 ? a / b : null, Fmt.Percent);
}
