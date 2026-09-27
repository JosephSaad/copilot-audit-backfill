using CopilotInsights.Infrastructure;
using Microsoft.AspNetCore.Mvc;

namespace CopilotInsights.Pages;

public sealed class IndexModel(PageServices s) : InsightsPage(s)
{
    public override string Title => "Copilot Insights";
    public override string Persona => "Start";
    public override bool UsesFilters => false;
    public string? OrgTop { get; private set; }

    protected override async Task BuildAsync()
    {
        OrgTop = Ctx.OrgTopUpn;
        await Task.CompletedTask;
    }

    /// <summary>The viewer's own start page (My Copilot > Saved views), unless they asked for this one (?home=1).</summary>
    public override async Task<IActionResult> OnGetAsync()
    {
        if (Request.Query["home"] != "1" && Request.Query.Count == 0)
        {
            try
            {
                var viewer = await S.Viewers.GetAsync();
                var start = await Db.ScalarAsync<string?>("SELECT TOP (1) Url FROM dash.SavedView WHERE Owner = @o AND IsStart = 1;", new { o = viewer.Upn ?? viewer.WindowsName });
                if (Personal.ViewsModel.SafeUrl(start)) return LocalRedirect(start!);
            }
            catch (Microsoft.Data.SqlClient.SqlException) { /* no saved views table yet */ }
        }
        return await base.OnGetAsync();
    }

    public string AccessSummary =>
        Viewer.OrgWide
            ? Viewer.Has(Roles.Organization) ? "You see the whole organisation (granted Organization)." : $"You see the whole organisation (Level {Viewer.Level}, at or next to the top)."
            : !Viewer.InDirectory
                ? "Your account is not in the organisation directory, so there is no data you may see yet. An administrator can grant you access."
                : Viewer.PeopleBelow > 0
                    ? $"You see yourself and the {Viewer.PeopleBelow:#,0} people who report up to you."
                    : "You see your own use of Copilot.";
}
