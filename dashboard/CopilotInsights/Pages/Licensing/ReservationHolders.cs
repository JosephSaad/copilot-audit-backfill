using CopilotInsights.Data;
using CopilotInsights.Infrastructure;

namespace CopilotInsights.Pages.Licensing;

/// <summary>
/// The named people behind a reservation (2.6.8): the Microsoft 365 Copilot add-on holders an organization's figure
/// counts, those under no listed leader, or the Technology-funded pool. For the Licensing role, as every list of people.
/// </summary>
public sealed class ReservationHoldersModel(PageServices s, ReservationData data) : InsightsPage(s)
{
    public override string Title => "Reservation holders";
    public override string Persona => "Licensing";
    public override string? RequiredRole => Roles.Licensing;
    public override string? OwnWindow => "today's licenses";
    public override bool UsesFilters => false;
    public override string Intro => "The people an organization's allocated licenses count: who holds the Microsoft 365 Copilot add-on there today, with their manager, when they were licensed and when they last used Copilot.";

    static readonly Dictionary<string, string> Sorts = new()
    {
        ["name"] = "u.DisplayName, u.UserPrincipalName", ["upn"] = "u.UserPrincipalName", ["dept"] = "u.Department, u.DisplayName",
        ["manager"] = "u.ManagerName, u.DisplayName", ["since"] = "u.LicensedSinceUtc, u.DisplayName", ["recent"] = "x.LastKey DESC, u.DisplayName",
    };
    static readonly Dictionary<string, string> SortLabels = new() { ["name"] = "Name", ["dept"] = "Department", ["manager"] = "Manager", ["since"] = "Licensed longest" };

    sealed class Row
    {
        public string? DisplayName { get; set; }
        public string UserPrincipalName { get; set; } = "";
        public string? Department { get; set; }
        public string? ManagerName { get; set; }
        public DateTime? LicensedSinceUtc { get; set; }
        public DateTime? LastUsed { get; set; }
        public string? Category { get; set; }
        public int TotalRows { get; set; }
    }

    protected override async Task BuildAsync()
    {
        if (!await data.ReadyAsync()) { Notice = "Reservations cannot be recorded here yet. An administrator sets this up by running Install-CopilotAudit.ps1 -Dashboard from 2.6.8 or later."; return; }
        if (await data.LicensedDayAsync() is not { } day) { Notice = "No licenses have been read yet. The license lookup fills them each night."; return; }
        var res = Request.Query["res"].FirstOrDefault() ?? "";
        var name = res == ReservationData.Unmapped ? "Under no listed leader" : res == ReservationData.Technology ? "Technology-funded pool"
            : await Db.ScalarAsync<string?>("SELECT TOP (1) Name FROM dash.Reservation WHERE OrgId = @res ORDER BY EffectiveFrom DESC;", new { res });
        if (name is null)
        {
            /* 2.7.4: opened from the menu, the page said to go and choose on Copilot reservations; it offers the choice itself. */
            var orgs = await Db.QueryAsync<(string OrgId, string Name)>(@"
SELECT OrgId, Name FROM (SELECT OrgId, Name, ROW_NUMBER() OVER (PARTITION BY OrgId ORDER BY EffectiveFrom DESC) AS n FROM dash.Reservation) AS x
WHERE n = 1 ORDER BY Name;");
            string Pick(string key, string text) =>
                $"<li><a href=\"{System.Net.WebUtility.HtmlEncode(Ctx.Url(Request.Path, extra: "res=" + Uri.EscapeDataString(key)))}\">{System.Net.WebUtility.HtmlEncode(text)}</a></li>";
            var list = string.Concat(orgs.Select(o => Pick(o.OrgId, o.Name))) + Pick(ReservationData.Unmapped, "Under no listed leader") + Pick(ReservationData.Technology, "Technology-funded pool");
            var back = Open("/leadership/reservations");
            var from = back is null ? "" : $"<p class=\"sub\">The organizations and their figures are on <a href=\"{System.Net.WebUtility.HtmlEncode(back)}\">Leadership › Copilot reservations</a>.</p>";
            Panels.Add(new("Choose an organization", new Microsoft.AspNetCore.Html.HtmlString($"<ul>{list}</ul>{from}"),
                "Each organization's people holding the Microsoft 365 Copilot add-on, as its allocated count counts them."));
            return;
        }
        var k = int.Parse(day.ToString("yyyyMMdd"));
        /* 2.7.0: within the viewer's part of the organization, as every list of people is (it was the whole bank's for any holder of the Licensing role).
           Who the organization's figure counts: its members holding the add-on, Technology-funded accounts aside; or the
           holders no organization covers; or the Technology pool. As ReservationData counts them. */
        var which = res switch
        {
            ReservationData.Unmapped => "t.Upn IS NULL AND NOT EXISTS (SELECT 1 FROM dash.fnReservationMember(@day) AS m WHERE m.UserKey = u.UserKey)",
            ReservationData.Technology => "t.Upn IS NOT NULL",
            _ => "t.Upn IS NULL AND EXISTS (SELECT 1 FROM dash.fnReservationMember(@day) AS m JOIN dash.Reservation AS r ON r.ReservationId = m.ReservationId WHERE m.UserKey = u.UserKey AND r.OrgId = @res)",
        };
        var pg = new Paging(Request, Sorts, "name");
        var rows = await Db.QueryAsync<Row>($@"
SELECT u.DisplayName, u.UserPrincipalName, u.Department, u.ManagerName, u.LicensedSinceUtc, dl.[Date] AS LastUsed, t.Category, COUNT(*) OVER () AS TotalRows
FROM bi.FactLicensedDay AS ld {Ctx.Scope("ld.UserKey")}
JOIN bi.DimUser AS u ON u.UserKey = ld.UserKey
LEFT JOIN dash.TechnologyAllocation AS t ON t.Upn = u.UserPrincipalName AND t.EffectiveFrom <= @day
OUTER APPLY (SELECT MAX(ud.DateKey) AS LastKey FROM dash.UserDay AS ud WHERE ud.UserKey = u.UserKey AND ud.Interactions > 0) AS x
LEFT JOIN bi.DimDate AS dl ON dl.DateKey = x.LastKey
WHERE ld.DateKey = @k AND NOT EXISTS (SELECT 1 FROM dash.CopilotSuiteOnly AS so WHERE so.Upn = u.UserPrincipalName)
  AND {which}
  AND (@q IS NULL OR u.DisplayName LIKE @q OR u.UserPrincipalName LIKE @q OR u.Department LIKE @q)
{pg.OrderAndPage()};", Ctx.Args(new { k, day, res, q = pg.Like }));
        pg.Total = rows.FirstOrDefault()?.TotalRows ?? 0;

        var t = new Table
        {
            Headers = ["Name", "Sign-in", "Department", "Manager", "Licensed since", "Last used", "Technology-funded"],
            EmptyText = "Nobody holds the add-on here today.",
            Server = new(pg, Request, ["name", "upn", "dept", "manager", "since", "recent", null]),
        };
        foreach (var r in rows)
            t.Rows.Add([r.DisplayName, r.UserPrincipalName, r.Department, r.ManagerName, r.LicensedSinceUtc?.Date, r.LastUsed is null ? "Never" : r.LastUsed.Value.Date, r.Category]);
        CsvTable = t;
        Kpis.Add(new(name, Num(pg.Total), $"Microsoft 365 Copilot add-on holders on {day:d MMM yyyy}", Href: Open("/leadership/reservations")));
        Panels.Add(new("People", new Microsoft.AspNetCore.Html.HtmlString(pg.Controls(Request, SortLabels).Value + t.Render().Value),
            "Current operational state, not certified. Copilot held only inside a suite (E7) is not counted; an organization's people are everyone under its leader in the org chart, the nearer listed leader first.",
            "full", CsvUrl()));
    }
}
