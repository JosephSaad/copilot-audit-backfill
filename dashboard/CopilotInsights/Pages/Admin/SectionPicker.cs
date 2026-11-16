using CopilotInsights.Infrastructure;
using Microsoft.AspNetCore.Html;

namespace CopilotInsights.Pages.Admin;

/// <summary>
/// The menu's sections as boxes to tick, laid out as the menu is (Copilot Insights, then the Governance Workbench), each
/// with who it is for, how many reports it holds and an information icon naming them; then the whole organization or the
/// holder's own part, and the lists that name people. Admin › Access and Admin › Access profiles both write it.
/// </summary>
public static class SectionPicker
{
    static string E(string? s) => System.Net.WebUtility.HtmlEncode(s ?? "");

    static string Icon(string text, string about) => ((HtmlString)InfoTip.Icon(text, about)).Value ?? "";

    /// <summary>The reports a section holds, by their names in the menu.</summary>
    public static IReadOnlyList<string> Reports(string key) => key switch
    {
        Sections.WorkbenchOwnership => ["Start", "My work", "Requests", "Report a consumption problem", "Recertification", "Exceptions", "Retirement", "Your Workbench roles"],
        Sections.WorkbenchRegistry => ["Funding owners", "Workloads", "Envelopes", "Copilot seats", "Services", "Control templates"],
        _ => [.. Nav.Groups.FirstOrDefault(g => g.Key == key)?.Pages.Where(p => !p.ManyTenants).Select(p => p.Title) ?? []],
    };

    /// <summary>What a section's information icon says: who it is for, its reports, and its lists that name people.</summary>
    public static string Tip(Sections.Section s)
    {
        var named = s.NamedRole is null ? []
            : Nav.Groups.FirstOrDefault(g => g.Key == s.Key)?.Pages.Where(p => p.Role is not null).Select(p => p.Title).ToList() ?? [];
        return $"{s.Name}, for {s.Who.ToLowerInvariant()}: {string.Join(", ", Reports(s.Key))}." +
               (named.Count > 0 ? $" Lists that name people, shown with Named people ticked: {string.Join(", ", named)}." : s.Workbench ? "" : " It has no list that names people.") +
               (s.Workbench ? " The Governance and Governance operator roles, and Admin, open it without a tick." : "");
    }

    /// <summary>The boxes for every section, ticked where <paramref name="ticked"/> holds it.</summary>
    public static string Boxes(IReadOnlySet<string> ticked, bool workbenchOn)
    {
        string Box(Sections.Section s)
        {
            var n = Reports(s.Key).Count;
            return $"<label class=\"sec\"><input type=\"checkbox\" name=\"section\" value=\"{E(s.Key)}\"{(ticked.Contains(s.Key) ? " checked" : "")}/>" +
                   $"<span class=\"sn\"><b>{E(s.Name)}{Icon(Tip(s), s.Name)}</b><small>{E(s.Who)} · {n} report{(n == 1 ? "" : "s")}</small></span></label>";
        }
        var html = "<p class=\"grouphead\">Copilot Insights</p><div class=\"secgrid\">" + string.Concat(Sections.All.Where(s => !s.Workbench).Select(Box)) + "</div>";
        if (workbenchOn)
            html += "<p class=\"grouphead\">Governance Workbench</p><div class=\"secgrid\">" + string.Concat(Sections.All.Where(s => s.Workbench).Select(Box)) + "</div>";
        return html;
    }

    /// <summary>How much of the organization the ticked sections show, as two choices side by side.</summary>
    public static string Scope(bool orgWide) =>
        "<div class=\"seg\" role=\"radiogroup\" aria-label=\"How much of the organization\">" +
        $"<label><input type=\"radio\" name=\"orgWide\" value=\"false\"{(orgWide ? "" : " checked")}/> Their own team</label>" +
        $"<label><input type=\"radio\" name=\"orgWide\" value=\"true\"{(orgWide ? " checked" : "")}/> Whole organization</label></div>";

    /// <summary>The lists that name people, one box for every ticked section that has them.</summary>
    public static string Named(bool named) =>
        $"<label class=\"check\"><input type=\"checkbox\" name=\"named\" value=\"true\"{(named ? " checked" : "")}/> Show the lists that name people in the ticked sections</label>";

    /// <summary>The help under the steps: what ticking does, and what levels still decide.</summary>
    public const string Help =
        "Each box is a header in the menu. Ticked, the header and its reports appear; not ticked, the header is gone and its reports are refused, whatever the person's level. " +
        "Their own team is themselves and everyone under them in the directory; people at levels 1 and 2 always see the whole organization in the sections they have. " +
        "Named people adds the lists that name people (who has not started, unused licenses, flagged interactions) in the ticked sections that have them; privacy mode and the minimum group size still apply.";
}
