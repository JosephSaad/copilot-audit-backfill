using System.Net;
using CopilotInsights.Infrastructure;
using Microsoft.AspNetCore.Html;

namespace CopilotInsights.Pages.Licensing;

/// <summary>
/// License holders whose use is falling away but has not stopped (2.4.3): the last 30 days against the 60
/// before. Caught now, a word from their manager or a session keeps the license earning; a month later
/// they are on Unused licenses.
/// </summary>
public sealed class FadingModel(PageServices s) : InsightsPage(s)
{
    public override string Title => "Fading use";
    public override string Persona => "Licensing";
    public override string? RequiredRole => Roles.Licensing;
    public override string? OwnWindow => "the last 90 days";
    public override string Intro => "License holders who used Copilot regularly and now use it much less, though not yet never: the last 30 days against the 60 before. Coach them, or plan to reclaim the license, before it goes idle.";

    const int MinDaysBefore = 4;   // days of use a 30 days before, at least: a habit to lose
    static readonly int[] Drops = [50, 75];

    static readonly Dictionary<string, string> Sorts = new()
    {
        ["drop"] = "x.RD * 1.0 / x.PD, x.DisplayName", ["name"] = "x.DisplayName, x.UserPrincipalName",
        ["dept"] = "x.Department, x.DisplayName", ["manager"] = "x.ManagerName, x.DisplayName",
        ["upn"] = "x.UserPrincipalName", ["before"] = "x.PD DESC, x.DisplayName, x.UserPrincipalName", ["days"] = "x.RD DESC, x.DisplayName, x.UserPrincipalName",
        ["ibefore"] = "x.PI DESC, x.DisplayName, x.UserPrincipalName", ["use"] = "x.RI DESC, x.DisplayName, x.UserPrincipalName",
        ["premium"] = "x.RP DESC, x.DisplayName, x.UserPrincipalName", ["recent"] = "x.LastUsed DESC, x.DisplayName, x.UserPrincipalName",
    };
    static readonly Dictionary<string, string> SortLabels = new() { ["drop"] = "Largest fall", ["name"] = "Name", ["dept"] = "Department", ["manager"] = "Manager" };

    sealed class Row
    {
        public string? DisplayName { get; set; }
        public string UserPrincipalName { get; set; } = "";
        public string? Department { get; set; }
        public string? ManagerName { get; set; }
        public int PD { get; set; }
        public int RD { get; set; }
        public long PI { get; set; }
        public long RI { get; set; }
        public int RP { get; set; }
        public DateTime? LastUsed { get; set; }
        public int TotalRows { get; set; }
    }

    protected override async Task BuildAsync()
    {
        var drop = int.TryParse(Request.Query["drop"], out var d) && Drops.Contains(d) ? d : 50;
        var k90 = ReportContext.Key(Ctx.End.AddDays(-89));
        var k30 = ReportContext.Key(Ctx.End.AddDays(-29));
        var pg = new Paging(Request, Sorts, "drop");
        /* PD, PI: days and interactions in the 60 days before the last 30 (so halved for a 30-day rate); RD, RI, RP: the
           last 30, with RP the days of Premium use. Fading: at least MinDaysBefore days a 30 before, some use still, and
           the last 30 days' days of use down by the chosen share or more. */
        var rows = await Db.QueryAsync<Row>($@"
WITH a AS (SELECT ud.UserKey,
                  COUNT(CASE WHEN ud.DateKey < @k30 AND ud.Interactions > 0 THEN 1 END) AS PD,
                  COUNT(CASE WHEN ud.DateKey >= @k30 AND ud.Interactions > 0 THEN 1 END) AS RD,
                  SUM(CASE WHEN ud.DateKey < @k30 THEN CAST(ud.Interactions AS BIGINT) ELSE 0 END) AS PI,
                  SUM(CASE WHEN ud.DateKey >= @k30 THEN CAST(ud.Interactions AS BIGINT) ELSE 0 END) AS RI,
                  COUNT(CASE WHEN ud.DateKey >= @k30 AND ud.PremiumUse > 0 THEN 1 END) AS RP,
                  MAX(CASE WHEN ud.Interactions > 0 THEN ud.DateKey END) AS LastKey
           FROM dash.UserDay AS ud {Ctx.Scope("ud.UserKey")}
           WHERE ud.DateKey BETWEEN @k90 AND @k1
           GROUP BY ud.UserKey),
x AS (SELECT du.DisplayName, du.UserPrincipalName, du.Department, du.ManagerName, a.PD, a.RD, a.PI, a.RI, a.RP, dl.[Date] AS LastUsed
      FROM a
      JOIN bi.FactLicensedDay AS ld ON ld.UserKey = a.UserKey AND ld.DateKey = @k1
      JOIN bi.DimUser AS du ON du.UserKey = a.UserKey
      LEFT JOIN bi.DimDate AS dl ON dl.DateKey = a.LastKey
      WHERE (du.LicensedSinceUtc IS NULL OR du.LicensedSinceUtc < DATEADD(DAY, -89, @d1))
        AND a.PD >= 2 * @minDays AND a.RD >= 1 AND a.RD * 200 <= a.PD * (100 - @drop)
        AND {CopilotInsights.Data.ProtectedLevels.Not("du.UserPrincipalName")})
SELECT x.*, COUNT(*) OVER () AS TotalRows FROM x
WHERE (@q IS NULL OR x.DisplayName LIKE @q OR x.UserPrincipalName LIKE @q OR x.Department LIKE @q)
{pg.OrderAndPage()};", Ctx.Args(new { q = pg.Like, k90, k30, drop, minDays = MinDaysBefore }));
        pg.Total = rows.FirstOrDefault()?.TotalRows ?? 0;

        Kpis.Add(new("People with fading use", Num(pg.Total), $"days of use down {drop}% or more", pg.Total > 0 ? "warn" : null));
        var price = decimal.TryParse(await S.Settings.StringAsync("LicenseMonthlyCost"), System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var p) ? p : 30m;
        var cur = await S.Settings.StringAsync("LicenseCurrency") ?? "USD";
        Kpis.Add(new($"Their licenses a month ({cur})", Num((long)(pg.Total * price)), "what goes idle if nothing changes"));

        var t = new Table
        {
            Headers = ["Name", "Sign-in", "Department", "Manager", "Days of use a month before", "Days of use, last 30", "Change",
                       "Interactions a month before", "Interactions, last 30", "Days with Premium use, last 30", "Last used"],
            Numeric = new HashSet<int> { 4, 5, 6, 7, 8, 9 },
            EmptyText = "Nobody's use is falling away.",
            Server = new(pg, Request, ["name", "upn", "dept", "manager", "before", "days", "drop", "ibefore", "use", "premium", "recent"], new HashSet<int> { 4, 5, 7, 8, 9, 10 }),
        };
        foreach (var r in rows)
            t.Rows.Add([r.DisplayName, r.UserPrincipalName, r.Department, r.ManagerName, Math.Round(r.PD / 2.0, 1), r.RD,
                        Pct(r.RD / (r.PD / 2.0) - 1), Math.Round(r.PI / 2.0), r.RI, r.RP, r.LastUsed?.Date]);
        CsvTable = t;
        var choices = string.Join(" · ", Drops.Select(x =>
            $"<a href=\"{WebUtility.HtmlEncode(Ctx.Url(Request.Path, extra: $"drop={x}"))}\"{(x == drop ? " class=\"on\"" : "")}>Down {x}% or more</a>"));
        Panels.Add(new("People", new HtmlString($"<p class=\"sub\">Show: {choices}</p>" + await ProtectedNoteAsync() + pg.Controls(Request, SortLabels).Value + t.Render().Value),
            $"License holders for at least 90 days who used Copilot on at least {MinDaysBefore} days a month over the 60 days before the last 30, and on at least one of the last 30. " +
            "\"A month before\" is those 60 days halved. People who stopped altogether are on Unused licenses.", "full", CsvUrl()));
    }
}
