using CopilotInsights.Data;
using System.Globalization;
using System.Net;
using CopilotInsights.Infrastructure;
using Microsoft.AspNetCore.Html;

namespace CopilotInsights.Pages.Licensing;

/// <summary>
/// License holders whose use the free tier and pay-as-you-go credits would cover: Copilot Chat on the
/// web and agents, with little or no Copilot in Word, Excel, PowerPoint, Outlook or Teams. Each with an
/// estimate of what pay-as-you-go would cost them against the license (2.4.3).
/// </summary>
public sealed class PayAsYouGoModel(PageServices s) : InsightsPage(s)
{
    public override string Title => "Better on pay-as-you-go";
    public override string Persona => "Licensing";
    public override string? RequiredRole => Roles.Licensing;
    public override string Intro => "People holding a Copilot license who use Copilot, but little of what only the license gives: Copilot inside Word, Excel, PowerPoint, Outlook and Teams, or chat over work files, mail and sites. Web Copilot Chat is free, and agents and work-grounded chat can be paid for per use with Copilot Credits, so for them credits may cost less than the license.";

    static readonly int[] Shares = [0, 5, 10, 25];

    static readonly Dictionary<string, string> Sorts = new()
    {
        ["saving"] = "x.Metered, x.DisplayName", ["share"] = "x.Share, x.DisplayName",
        ["name"] = "x.DisplayName, x.UserPrincipalName", ["dept"] = "x.Department, x.DisplayName",
        ["upn"] = "x.UserPrincipalName", ["manager"] = "x.ManagerName, x.DisplayName", ["sku"] = "x.CopilotSku, x.DisplayName",
        ["since"] = "x.LicensedSinceUtc, x.DisplayName", ["use"] = "x.I DESC, x.DisplayName, x.UserPrincipalName", ["premium"] = "x.P DESC, x.DisplayName, x.UserPrincipalName",
        ["agent"] = "x.A DESC, x.DisplayName, x.UserPrincipalName", ["days"] = "x.Days DESC, x.DisplayName, x.UserPrincipalName",
        ["pdays"] = "x.PremiumDays DESC, x.DisplayName, x.UserPrincipalName", ["recent"] = "x.LastUsed DESC, x.DisplayName, x.UserPrincipalName",
        ["credits"] = "x.Metered DESC, x.DisplayName, x.UserPrincipalName",
    };
    static readonly Dictionary<string, string> SortLabels = new() { ["saving"] = "Largest saving", ["share"] = "Lowest Premium share", ["name"] = "Name", ["dept"] = "Department" };

    sealed class Row
    {
        public string? DisplayName { get; set; }
        public string UserPrincipalName { get; set; } = "";
        public string? Department { get; set; }
        public string? ManagerName { get; set; }
        public string? CopilotSku { get; set; }
        public DateTime? LicensedSinceUtc { get; set; }
        public long I { get; set; }
        public long P { get; set; }
        public long A { get; set; }
        public long Metered { get; set; }
        public int Days { get; set; }
        public int PremiumDays { get; set; }
        public DateTime? LastUsed { get; set; }
        public int TotalRows { get; set; }
    }

    protected override async Task BuildAsync()
    {
        var max = int.TryParse(Request.Query["max"], out var m) && Shares.Contains(m) ? m : 10;
        static decimal Dec(string? v, decimal fallback) => decimal.TryParse(v, NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? d : fallback;
        var price = Dec(await S.Settings.StringAsync("LicenseMonthlyCost"), 30m);
        var rate = Dec(await S.Settings.StringAsync("PaygInteractionCost"), 0.12m);
        var cur = await S.Settings.StringAsync("LicenseCurrency") ?? "USD";
        /* A month is scaled from the days the data covers, not the period's (2.4.12, see OpsReports.DaysCovered). */
        var days = CopilotInsights.Data.OpsReports.DaysCovered(Ctx, await CopilotInsights.Data.OpsReports.FirstDayAsync(Db));
        decimal Monthly(long metered) => metered * rate * 30m / days;

        var pg = new Paging(Request, Sorts, "saving");
        /* Metered: the interactions pay-as-you-go would charge for, Premium use (work-grounded chat; in-app use has no
           pay-as-you-go form) and agent use. An agent that read work content is in both, and one grounded only in the
           web is charged for nothing: the estimate errs high on cost, so low on the saving. The candidates and their
           totals are read on their own, before the search, so the figures above the table do not change with it: taken
           from the page's first row, they were 0 when a search matched nobody or the page was past the end (2.4.12). */
        var (rows, all) = await Db.QueryTwoAsync<Row, (int Candidates, long AllMetered)>($@"
WITH u AS (SELECT ud.UserKey, SUM(CAST(ud.Interactions AS BIGINT)) AS I, SUM(CAST(ud.PremiumUse AS BIGINT)) AS P,
                  SUM(CAST(ud.AgentInteractions AS BIGINT)) AS A,
                  COUNT(CASE WHEN ud.Interactions > 0 THEN 1 END) AS Days, COUNT(CASE WHEN ud.PremiumUse > 0 THEN 1 END) AS PremiumDays,
                  MAX(CASE WHEN ud.Interactions > 0 THEN ud.DateKey END) AS LastKey
           FROM dash.UserDay AS ud {Ctx.Scope("ud.UserKey")}
           WHERE ud.DateKey BETWEEN @k0 AND @k1
           GROUP BY ud.UserKey HAVING SUM(ud.Interactions) > 0),
x AS (SELECT du.DisplayName, du.UserPrincipalName, du.Department, du.ManagerName, du.CopilotSku, du.LicensedSinceUtc,
             u.I, u.P, u.A, u.P + u.A AS Metered, u.P * 1.0 / u.I AS Share, u.Days, u.PremiumDays, dl.[Date] AS LastUsed
      FROM u
      JOIN bi.FactLicensedDay AS ld ON ld.UserKey = u.UserKey AND ld.DateKey = @k1
      JOIN bi.DimUser AS du ON du.UserKey = u.UserKey
      LEFT JOIN bi.DimDate AS dl ON dl.DateKey = u.LastKey
      WHERE (du.LicensedSinceUtc IS NULL OR du.LicensedSinceUtc < @d0) AND u.P * 100 <= @max * u.I
        AND {CopilotInsights.Data.ProtectedLevels.Not("du.UserPrincipalName")})
SELECT x.* INTO #x FROM x;
SELECT x.*, COUNT(*) OVER () AS TotalRows FROM #x AS x
WHERE (@q IS NULL OR x.DisplayName LIKE @q OR x.UserPrincipalName LIKE @q OR x.Department LIKE @q)
{pg.OrderAndPage()};
SELECT COUNT(*), ISNULL(SUM(x.Metered), 0) FROM #x AS x;", Ctx.Args(new { q = pg.Like, max }));
        pg.Total = rows.FirstOrDefault()?.TotalRows ?? 0;

        var (candidates, allMetered) = all.Single();
        var saving = candidates * price - Monthly(allMetered);
        Kpis.Add(new("Pay-as-you-go candidates", Num(candidates), max == 0 ? "no Premium use in the period" : $"Premium use {max}% or less of their use"));
        Kpis.Add(new($"Saving a month ({cur})", Num((long)Math.Max(0, saving)),
            $"{candidates:#,0} licenses at {price:#,0.##} less the credits their use would cost",
            Why: await WhyAsync($"Candidates × the Copilot license price ({candidates:#,0} × {price:#,0.##} {cur}), less their metered interactions × the pay-as-you-go cost of an interaction.", "LicenseMonthlyCost", "PaygInteractionCost")));
        Kpis.Add(new("Credits assumed", $"{rate:#,0.00##} {cur}", "for each metered interaction (set in Admin > Access)"));

        var names = await SkuNames.LoadAsync(Db);   // 2.7.1: product names, not part numbers
        var t = new Table
        {
            Headers = ["Name", "Sign-in", "Department", "Manager", "License", "Licensed since", "Interactions", "Premium", "Premium share", "Agent",
                       "Days used", "Days with Premium use", "Last used", $"Credits a month ({cur})", $"Saving a month ({cur})"],
            Numeric = new HashSet<int> { 6, 7, 8, 9, 10, 11, 13, 14 },
            EmptyText = "No license holder here uses Copilot this little outside the free tier.",
            /* Largest saving is the fewest metered interactions, so its own order puts the saving column descending. */
            Server = new(pg, Request, ["name", "upn", "dept", "manager", "sku", "since", "use", "premium", "share", "agent", "days", "pdays", "recent", "credits", "saving"],
                         new HashSet<int> { 6, 7, 9, 10, 11, 12, 13, 14 }),
        };
        foreach (var r in rows)
        {
            var credits = Monthly(r.Metered);
            t.Rows.Add([r.DisplayName, r.UserPrincipalName, r.Department, r.ManagerName, names.Name(r.CopilotSku), r.LicensedSinceUtc?.Date,
                        r.I, r.P, Pct(Ratio(r.P, r.I)), r.A, r.Days, r.PremiumDays, r.LastUsed?.Date,
                        Math.Round(credits, 2), Math.Round(price - credits, 2)]);
        }
        CsvTable = t;
        var choices = string.Join(" · ", Shares.Select(sh =>
            $"<a href=\"{WebUtility.HtmlEncode(Ctx.Url(Request.Path, extra: $"max={sh}"))}\"{(sh == max ? " class=\"on\"" : "")}>{(sh == 0 ? "None" : $"{sh}% or less")}</a>"));
        Panels.Add(new("People", new HtmlString($"<p class=\"sub\">Premium use: {choices}</p>" + await ProtectedNoteAsync() + pg.Controls(Request, SortLabels).Value + t.Render().Value),
            $"License holders since before the period with any use in it. Credits a month: their Premium and agent interactions, scaled to 30 days, at {rate:#,0.00##} {cur} each; " +
            "an agent grounded only in the web costs nothing, so this errs high. Anyone here with Premium use would lose Copilot in the apps: check the Premium column before moving them. " +
            "People with no use at all are on Unused licenses.", "full", CsvUrl()));
    }
}
