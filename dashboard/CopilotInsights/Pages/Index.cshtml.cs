using CopilotInsights.Infrastructure;

namespace CopilotInsights.Pages;

public sealed class IndexModel(PageServices s) : InsightsPage(s)
{
    public override string Title => "Copilot Insights";
    public override string Persona => "Start";
    public override bool UsesFilters => false;
    /// <summary>An administrator outside the dashboard's levels still starts here, with the Admin pages listed.</summary>
    public override bool OpenToAdmins => true;
    public string? OrgTop { get; private set; }

    /// <summary>How current the reports are: the last day with any Copilot use, and when the rollups last finished.</summary>
    public string? Freshness { get; private set; }

    /// <summary>
    /// Nothing granted: no section opens for them, whatever their level, and they hold no Admin page. The page says so
    /// and names who can grant access, in place of the list of reports.
    /// </summary>
    public bool NoAccess { get; private set; }

    protected override async Task BuildAsync()
    {
        NoAccess = !Viewer.SeesAny && !Viewer.AnyAdmin && !Viewer.Roles.Any(Roles.IsWorkbench);
        if (NoAccess) Administrators = await AdministratorsAsync();
        OrgTop = Ctx.OrgTopUpn;
        try
        {
            var (day, refreshed) = (await Db.QueryAsync<(DateTime? Day, DateTime? Refreshed)>(@"
SELECT (SELECT d.[Date] FROM bi.DimDate AS d WHERE d.DateKey = (SELECT MAX(ud.DateKey) FROM dash.UserDay AS ud)),
       (SELECT MAX(EndedUtc) FROM dash.RefreshRun);")).Single();
            if (day is not null)
                Freshness = $"The reports hold Copilot use up to {day:d MMM yyyy}" + (refreshed is null ? "." : $", and were last refreshed {When(refreshed)}.");
        }
        catch (Microsoft.Data.SqlClient.SqlException) { /* a database not built yet: the page says nothing about dates */ }
    }

    public string AccessSummary =>
        !Viewer.Admitted ? LimitRefusal(Viewer)
        : !Viewer.SeesAny ? "No section of the dashboard is granted to you yet."
        : Viewer.OrgWide
            ? Viewer.Has(Roles.Organization) ? "You see the whole organization (granted Organization)." : $"You see the whole organization (Level {Viewer.Level}, at or next to the top)."
            : !Viewer.InDirectory
                ? "Your account is not in the organization directory, so there is no data you may see yet. An administrator can grant you access."
                : Viewer.PeopleBelow > 0
                    ? $"You see yourself and the {Viewer.PeopleBelow:#,0} people who report up to you, directly or through their managers."
                    : "You see your own use of Copilot.";
}
