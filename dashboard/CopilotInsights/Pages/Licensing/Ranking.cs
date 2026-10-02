using System.Net;
using CopilotInsights.Charts;
using CopilotInsights.Infrastructure;
using Microsoft.AspNetCore.Html;

namespace CopilotInsights.Pages.Licensing;

/// <summary>
/// Everyone ranked three ways: by all their Copilot use, by use the free tier covers (web Copilot Chat,
/// agents), and by use that needs the Premium license (Copilot in Word, Excel, PowerPoint, Outlook and
/// Teams, or chat over work content). License decisions follow the Premium ranking.
/// </summary>
public sealed class RankingModel(PageServices s) : InsightsPage(s)
{
    public override string Title => "Use ranking";
    public override string Persona => "Licensing";
    public override string? RequiredRole => Roles.Licensing;
    public override string Intro => "Everyone you can see, ranked three ways in the period: all their Copilot use, use the free tier covers (web Copilot Chat and agents), and use that needs the Premium license (Copilot in Word, Excel, PowerPoint, Outlook and Teams, or Copilot Chat over work files, mail and sites). A license holder low on the Premium ranking is paying for what they do not use; someone without a license high on the free ranking is ready for one.";

    internal static readonly Dictionary<string, (string Label, string Order)> By = new()
    {
        ["general"] = ("General use", "r.I DESC, r.P DESC"),
        ["free"] = ("Free use", "r.F DESC, r.I DESC"),
        ["premium"] = ("Premium use", "r.P DESC, r.I DESC"),
    };

    /* The list's keys beside "rank", the order of the ranking chosen. */
    static readonly Dictionary<string, string> Sorts = new()
    {
        ["name"] = "x.DisplayName", ["general"] = "x.RankGeneral, x.DisplayName, x.UserPrincipalName", ["free"] = "x.RankFree, x.DisplayName, x.UserPrincipalName",
        ["premium"] = "x.RankPremium, x.DisplayName, x.UserPrincipalName", ["upn"] = "x.UserPrincipalName", ["dept"] = "x.Department, x.DisplayName",
        ["licensed"] = "x.Licensed DESC, x.DisplayName, x.UserPrincipalName", ["use"] = "x.I DESC, x.DisplayName, x.UserPrincipalName",
        ["freeuse"] = "x.F DESC, x.DisplayName, x.UserPrincipalName", ["premiumuse"] = "x.P DESC, x.DisplayName, x.UserPrincipalName",
        ["share"] = "x.P * 1.0 / x.I DESC, x.DisplayName, x.UserPrincipalName", ["days"] = "x.Days DESC, x.DisplayName, x.UserPrincipalName",
        ["pdays"] = "x.PremiumDays DESC, x.DisplayName, x.UserPrincipalName",
    };

    sealed class Row
    {
        public long RankGeneral { get; set; }
        public long RankFree { get; set; }
        public long RankPremium { get; set; }
        public string? DisplayName { get; set; }
        public string UserPrincipalName { get; set; } = "";
        public string? Department { get; set; }
        public bool Licensed { get; set; }
        public long I { get; set; }
        public long F { get; set; }
        public long P { get; set; }
        public int Days { get; set; }
        public int PremiumDays { get; set; }
        public int TotalRows { get; set; }
    }

    protected override async Task BuildAsync()
    {
        var by = Request.Query["by"].FirstOrDefault() is { } b && By.ContainsKey(b) ? b : "premium";
        var license = Request.Query["license"].FirstOrDefault() is "licensed" or "unlicensed" ? Request.Query["license"].ToString() : "all";
        var ready = await Db.ScalarAsync<int>("SELECT CASE WHEN COL_LENGTH(N'dash.UserDay', N'PremiumUse') IS NULL THEN 0 ELSE 1 END") == 1
                    && await Db.ScalarAsync<int>("SELECT CASE WHEN EXISTS (SELECT 1 FROM dash.UserDay WHERE DateKey BETWEEN @k0 AND @k1 AND PremiumUse > 0) THEN 1 ELSE 0 END", Ctx.Args()) == 1;
        if (!ready)
            Notice = "Free and Premium use are not told apart here yet, so those columns are empty. An administrator separates them by rebuilding the dashboard once (Admin > Health, Rebuild every day).";
        var pg = new Paging(Request, new Dictionary<string, string>(Sorts) { ["rank"] = By[by].Order.Replace("r.", "x.") }, "rank");
        /* Ranked over everyone in scope with any use, before the search and the license filter, so a person's rank does not change with them. */
        var rows = await Db.QueryAsync<Row>($@"
WITH u AS (SELECT ud.UserKey, SUM(CAST(ud.Interactions AS BIGINT)) AS I, SUM(CAST(ud.PremiumUse AS BIGINT)) AS P,
                  COUNT(CASE WHEN ud.Interactions > 0 THEN 1 END) AS Days, COUNT(CASE WHEN ud.PremiumUse > 0 THEN 1 END) AS PremiumDays
           FROM dash.UserDay AS ud {Ctx.Scope("ud.UserKey")}
           WHERE ud.DateKey BETWEEN @k0 AND @k1 GROUP BY ud.UserKey HAVING SUM(ud.Interactions) > 0),
r AS (SELECT u.*, u.I - u.P AS F FROM u),
x AS (SELECT r.*, du.DisplayName, du.UserPrincipalName, du.Department,
             CAST(CASE WHEN ld.UserKey IS NULL THEN 0 ELSE 1 END AS BIT) AS Licensed,
             RANK() OVER (ORDER BY r.I DESC) AS RankGeneral, RANK() OVER (ORDER BY r.F DESC) AS RankFree, RANK() OVER (ORDER BY r.P DESC) AS RankPremium
      FROM r JOIN bi.DimUser AS du ON du.UserKey = r.UserKey
      LEFT JOIN bi.FactLicensedDay AS ld ON ld.UserKey = r.UserKey AND ld.DateKey = @k1)
SELECT x.*, COUNT(*) OVER () AS TotalRows FROM x
WHERE (@q IS NULL OR x.DisplayName LIKE @q OR x.UserPrincipalName LIKE @q OR x.Department LIKE @q)
  AND (@license = 'all' OR (@license = 'licensed' AND x.Licensed = 1) OR (@license = 'unlicensed' AND x.Licensed = 0))
{pg.OrderAndPage()};", Ctx.Args(new { q = pg.Like, license }), timeout: 300);
        pg.Total = rows.FirstOrDefault()?.TotalRows ?? 0;

        var totals = (await Db.QueryAsync<(long I, long P, int People)>($@"
SELECT ISNULL(SUM(CAST(ud.Interactions AS BIGINT)), 0), ISNULL(SUM(CAST(ud.PremiumUse AS BIGINT)), 0), COUNT(DISTINCT CASE WHEN ud.Interactions > 0 THEN ud.UserKey END)
FROM dash.UserDay AS ud {Ctx.Scope("ud.UserKey")} WHERE ud.DateKey BETWEEN @k0 AND @k1;", Ctx.Args())).Single();
        Kpis.Add(new("People using Copilot", Num(totals.People)));
        Kpis.Add(new("Premium use", Pct(Ratio(totals.P, totals.I)), $"{totals.P:#,0} of {totals.I:#,0} interactions needed the license"));
        Kpis.Add(new("Free use", Pct(Ratio(totals.I - totals.P, totals.I)), "web Copilot Chat, agents, and use without a license"));

        var tabs = string.Join(" · ", By.Select(kv =>
            $"<a href=\"{WebUtility.HtmlEncode(Ctx.Url(Request.Path, extra: $"by={kv.Key}&license={license}"))}\"{(kv.Key == by ? " class=\"on\"" : "")}>{kv.Value.Label}</a>"));
        var lic = string.Join(" · ", new[] { ("all", "Everyone"), ("licensed", "Licensed"), ("unlicensed", "Not licensed") }.Select(l =>
            $"<a href=\"{WebUtility.HtmlEncode(Ctx.Url(Request.Path, extra: $"by={by}&license={l.Item1}"))}\"{(l.Item1 == license ? " class=\"on\"" : "")}>{l.Item2}</a>"));
        var t = new Table
        {
            Headers = ["Rank: general", "Rank: free", "Rank: premium", "Name", "Sign-in", "Department", "Has a license", "Interactions", "Free interactions", "Premium interactions", "Premium share", "Days used", "Days with Premium use"],
            Numeric = new HashSet<int> { 0, 1, 2, 7, 8, 9, 10, 11, 12 },
            EmptyText = "Nobody here used Copilot in the period.",
            /* The chosen ranking's column sorts by "rank", the other two by their own rank; Licensed: Yes first. */
            Server = new(pg, Request, [by == "general" ? "rank" : "general", by == "free" ? "rank" : "free", by == "premium" ? "rank" : "premium",
                                       "name", "upn", "dept", "licensed", "use", "freeuse", "premiumuse", "share", "days", "pdays"],
                         new HashSet<int> { 6, 7, 8, 9, 10, 11, 12 }),
        };
        foreach (var r in rows)
            t.Rows.Add([r.RankGeneral, r.RankFree, r.RankPremium, r.DisplayName, r.UserPrincipalName, r.Department, r.Licensed ? "Yes" : "No",
                        r.I, r.F, r.P, Pct(Ratio(r.P, r.I)), r.Days, r.PremiumDays]);
        CsvTable = t;
        Panels.Add(new("Ranking", new HtmlString($"<p class=\"sub\">Rank by: {tabs}</p><p class=\"sub\">Show: {lic}</p>" +
            pg.Controls(Request, new Dictionary<string, string> { ["rank"] = "Rank", ["name"] = "Name" }).Value + t.Render().Value),
            "Ranks are among everyone you can see with any use in the period (1 is the most); ties share a rank. Premium: a license holder's interactions inside Word, Excel, PowerPoint, Outlook or Teams, in Copilot Chat in work mode, or that read work content. Free: the rest. The Upgrade, train or revoke page uses the same split.",
            "full", CsvUrl()));
    }
}
