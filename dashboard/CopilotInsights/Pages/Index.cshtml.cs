using CopilotInsights.Infrastructure;

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

    public string AccessSummary =>
        Viewer.OrgWide
            ? Viewer.Has(Roles.Organization) ? "You see the whole organisation (granted Organization)." : $"You see the whole organisation (Level {Viewer.Level}, at or next to the top)."
            : !Viewer.InDirectory
                ? "Your account is not in the organisation directory, so there is no data you may see yet. An administrator can grant you access."
                : Viewer.PeopleBelow > 0
                    ? $"You see yourself and the {Viewer.PeopleBelow:#,0} people who report up to you."
                    : "You see your own use of Copilot.";
}
