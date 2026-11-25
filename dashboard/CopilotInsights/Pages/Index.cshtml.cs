using CopilotInsights.Infrastructure;

namespace CopilotInsights.Pages;

/// <summary>One of the start page's three questions: one figure, a line on it, and the pages that answer it.</summary>
public sealed record StartQuestion(string Ask, string? Figure, string Line, string? Href, IReadOnlyList<(string Text, string Href)> Links);

public sealed class IndexModel(PageServices s, CopilotInsights.Data.CostData costs, CopilotInsights.Data.OpsData ops) : InsightsPage(s)
{
    /// <summary>
    /// What are we spending on AI, is it worth it, are we in control: each only where the viewer may open the page behind
    /// its figure, and each figure the same number that page shows (Costs and savings' spend and return tiles, the
    /// Licenses on disabled accounts count).
    /// </summary>
    public List<StartQuestion> Questions { get; } = [];
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

    /// <summary>The saving ledger's totals, for the viewers Costs and savings is for (whole organization, or Licensing); else null.</summary>
    public CopilotInsights.Data.LedgerTotals? Ledger { get; private set; }

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
        /* The questions first: working out Costs and savings' model writes the month's savings found, which the ledger line reads. */
        if (Viewer.Admitted && !NoAccess) await QuestionsAsync();
        if (Viewer.Admitted && (Viewer.OrgWide || Viewer.Has(Roles.Licensing)))
            Ledger = await CopilotInsights.Data.SavingLedger.ReadAsync(Db);
    }

    async Task QuestionsAsync()
    {
        try
        {
            /* Spend and return: Costs and savings' own model (kept, built ahead each morning), for those it is for. */
            if ((Viewer.OrgWide || Viewer.Has(Roles.Licensing)) && Open("/leadership/costs", null, keepOrg: false) is { } costsUrl)
            {
                var v = await costs.BuildAsync(Ctx);
                if (v.Areas.Count > 0)
                {
                    Questions.Add(new("What are we spending on AI?", StartFigures.Money(v.Spend, v.Currency) + " a month",
                        $"Across {Count(v.Areas.Count, "area")}: the licenses held, and what the other sources billed over their last 30 days.", costsUrl,
                        [.. new[] { ("Costs and savings", costsUrl) }]));
                    var links = new List<(string, string)> { ("Costs and savings", costsUrl) };
                    if (Open("/leadership/value") is { } vu) links.Add(("Value and time saved", vu));
                    if (Open("/adoption") is { } au) links.Add(("Adoption", au));
                    Questions.Add(new("Is it worth it?", v.Spend > 0 && v.Value > 0 ? StartFigures.Times(v.Value / v.Spend) + " return" : null,
                        v.Value > 0 ? $"{StartFigures.Money(v.Value, v.Currency)} a month of time saved, at the hourly cost set in Admin." : "The value of the time saved shows once use is loaded.",
                        costsUrl, links));
                }
            }
            /* Control: the licenses on disabled or departed accounts (the Licensing role's list), and the pages that hold the rest. */
            var control = new List<(string, string)>();
            if (Open("/security/flags") is { } fu) control.Add(("Flagged interactions", fu));
            if (Open("/agents/owners") is { } ou) control.Add(("Agents without an owner", ou));
            if (Open("/governance") is { } gu) control.Add(("Models and controls", gu));
            var lu = Open("/licensing/leavers");
            int? leavers = lu is null ? null : (await ops.LeaversAsync(Ctx.Root)).Count;
            if (lu is not null) control.Insert(0, ("Licenses on disabled accounts", lu));
            if (control.Count > 0)
                Questions.Add(new("Are we in control?", leavers is { } n ? StartFigures.Count(n) + (n == 1 ? " license" : " licenses") : null,
                    leavers is null ? "Flagged interactions, agents without an owner, and the models and controls in use." : "on disabled or departed accounts, still paid for. Flags to triage and agents without an owner are a click away.",
                    lu, control));
        }
        catch (Microsoft.Data.SqlClient.SqlException ex) { Failure(ex, "The start page's questions"); /* left out; the start page still opens */ }
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
