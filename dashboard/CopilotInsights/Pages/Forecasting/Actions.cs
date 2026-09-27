using System.Net;
using CopilotInsights.Infrastructure;
using Microsoft.AspNetCore.Html;

namespace CopilotInsights.Pages.Forecasting;

/// <summary>The recommendations, person by person: who to upgrade, who to train, whose licence to take back.</summary>
public sealed class ActionsModel(PageServices s) : InsightsPage(s)
{
    public override string Title => "Upgrade, train or revoke";
    public override string Persona => "Forecasting";
    public override string? RequiredRole => Roles.Licensing;
    public override string Intro => "Named recommendations from the last 30 days: free Copilot Chat users to upgrade to a Premium licence, "
        + "licence holders who barely use Copilot to train, and licences unused long enough to revoke.";

    public const int UpgradeDays = 6;   // of the last 30, on the free Copilot Chat
    public const int LightMax = 2;      // days of use in 30 with a licence: train
    public const int RevokeAfterDays = 30;

    static readonly Dictionary<string, (string Label, string Where, string Action)> Kinds = new()
    {
        ["upgrade"] = ("Upgrade to Premium", $"lic.UserKey IS NULL AND a.D >= {UpgradeDays}", "Upgrade to Premium"),
        ["train"] = ("Train", $"lic.UserKey IS NOT NULL AND a.D BETWEEN 1 AND {LightMax}", "Train, then review"),
        ["revoke"] = ("Revoke", $"lic.UserKey IS NOT NULL AND ISNULL(a.D, 0) = 0 AND (du.LicensedSinceUtc IS NULL OR du.LicensedSinceUtc <= DATEADD(DAY, -{RevokeAfterDays}, @d1))", "Revoke"),
    };

    static readonly Dictionary<string, string> Sorts = new()
    {
        ["strength"] = "Strength DESC, DisplayName", ["name"] = "DisplayName", ["dept"] = "Department, DisplayName", ["manager"] = "ManagerName, DisplayName",
    };
    static readonly Dictionary<string, string> SortLabels = new() { ["strength"] = "Strongest case first", ["name"] = "Name", ["dept"] = "Department", ["manager"] = "Manager" };

    sealed class Row
    {
        public string? DisplayName { get; set; }
        public string UserPrincipalName { get; set; } = "";
        public string? Department { get; set; }
        public string? ManagerName { get; set; }
        public int Days { get; set; }
        public int Interactions { get; set; }
        public DateTime? LastUsed { get; set; }
        public DateTime? LicensedSinceUtc { get; set; }
        public string? RecordedLicence { get; set; }
        public int Strength { get; set; }
        public int TotalRows { get; set; }
    }

    protected override async Task BuildAsync()
    {
        var kind = Request.Query["kind"].FirstOrDefault() is { } k && Kinds.ContainsKey(k) ? k : "upgrade";
        var price = decimal.TryParse(await S.Settings.StringAsync("LicenceMonthlyCost"), System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var p) ? p : 30m;
        var currency = await S.Settings.StringAsync("LicenceCurrency") ?? "USD";
        var pg = new Paging(Request, Sorts, "strength");
        var k30 = ReportContext.Key(Ctx.End.AddDays(-29));
        var rows = await Db.QueryAsync<Row>($@"
WITH a AS (SELECT ud.UserKey, COUNT(CASE WHEN ud.Interactions > 0 THEN 1 END) AS D, SUM(ud.Interactions) AS I
           FROM dash.UserDay AS ud WHERE ud.DateKey BETWEEN @k30 AND @k1 GROUP BY ud.UserKey),
lic AS (SELECT UserKey FROM bi.FactLicensedDay WHERE DateKey = @k1),
base AS (
    SELECT du.UserKey, du.DisplayName, du.UserPrincipalName, du.Department, du.ManagerName, ISNULL(a.D, 0) AS Days, ISNULL(a.I, 0) AS Interactions,
           du.LicensedSinceUtc,
           CASE WHEN @kind = 'upgrade' THEN ISNULL(a.D, 0) * 100000 + ISNULL(a.I, 0)
                WHEN @kind = 'train' THEN 100000 - ISNULL(a.I, 0)
                ELSE DATEDIFF(DAY, ISNULL(du.LicensedSinceUtc, @d1), @d1) END AS Strength
    FROM bi.DimUser AS du {Ctx.Scope("du.UserKey")}
    LEFT JOIN a ON a.UserKey = du.UserKey
    LEFT JOIN lic ON lic.UserKey = du.UserKey
    WHERE {Kinds[kind].Where}
      AND ISNULL(du.AccountEnabled, 1) = 1
      AND (@q IS NULL OR du.DisplayName LIKE @q OR du.UserPrincipalName LIKE @q OR du.Department LIKE @q)
),
pick AS (SELECT base.*, COUNT(*) OVER () AS TotalRows FROM base {pg.OrderAndPage()})
/* The detail only for the page shown: last use, and the licence the latest record reports. */
SELECT pick.*,
       (SELECT dl.[Date] FROM bi.DimDate AS dl WHERE dl.DateKey = (SELECT MAX(x.DateKey) FROM dash.UserDay AS x WHERE x.UserKey = pick.UserKey AND x.Interactions > 0)) AS LastUsed,
       (SELECT TOP (1) t.LicenseType FROM bi.FactInteraction AS f JOIN bi.DimLicenseType AS t ON t.LicenseTypeKey = f.LicenseTypeKey
        WHERE f.UserKey = pick.UserKey AND f.LicenseTypeKey <> 1 ORDER BY f.DateKey DESC) AS RecordedLicence
FROM pick
ORDER BY {Sorts[pg.SortKey]};", Ctx.Args(new { k30, kind, q = pg.Like }));
        pg.Total = rows.FirstOrDefault()?.TotalRows ?? 0;

        var t = new Table
        {
            Headers = ["Name", "Sign-in", "Department", "Manager", "Recommendation", "Days used (30)", "Interactions (30)", "Last used", "Licensed since", "Licence the records report", "Why"],
            Numeric = new HashSet<int> { 5, 6 },
            EmptyText = "No one fits this recommendation.",
        };
        foreach (var r in rows)
        {
            var why = kind switch
            {
                "upgrade" => $"Used the free Copilot Chat on {r.Days} of the last 30 days",
                "train" => $"Licensed, but used Copilot on {r.Days} day{(r.Days == 1 ? "" : "s")} in 30",
                _ => r.LastUsed is null
                    ? "Licensed" + (r.LicensedSinceUtc is null ? "" : $" {(Ctx.End - r.LicensedSinceUtc.Value.Date).Days} days ago") + " and never used"
                    : $"No use for {(Ctx.End - r.LastUsed.Value.Date).Days} days",
            };
            t.Rows.Add([r.DisplayName, r.UserPrincipalName, r.Department, r.ManagerName, Kinds[kind].Action, r.Days, r.Interactions, r.LastUsed, r.LicensedSinceUtc?.Date, r.RecordedLicence, why]);
        }
        CsvTable = t;

        var effect = kind switch
        {
            "upgrade" => $"a cost of {pg.Total * price:#,0} {currency} a month",
            "revoke" => $"a saving of {pg.Total * price:#,0} {currency} a month",
            _ => "no change in cost; review again in 30 days",
        };
        Kpis.Add(new(Kinds[kind].Label, Num(pg.Total), $"people: {effect}", kind == "revoke" && pg.Total > 0 ? "crit" : null));
        var tabs = string.Join(" · ", Kinds.Select(kv =>
            $"<a href=\"{WebUtility.HtmlEncode(Ctx.Url(Request.Path, extra: "kind=" + kv.Key))}\"{(kv.Key == kind ? " class=\"on\"" : "")}>{kv.Value.Label}</a>"));
        var rule = kind switch
        {
            "upgrade" => $"No Premium licence, and used the free Copilot Chat on {UpgradeDays} or more of the last 30 days: they already rely on it.",
            "train" => $"A Premium licence, used on only 1 to {LightMax} days in the last 30: a short session or a champion usually changes that. Review again after 30 days.",
            _ => $"A Premium licence held for at least {RevokeAfterDays} days with no use at all in the last 30. Offer training first if the role needs Copilot; otherwise revoke and reassign.",
        };
        Panels.Add(new("People", new HtmlString($"<p class=\"sub\">Recommendation: {tabs}</p><p class=\"sub\">{WebUtility.HtmlEncode(rule)}</p>" + pg.Controls(Request, SortLabels).Value + t.Render().Value),
            "The period filter does not apply here: every rule looks at the last 30 days. \"Licence the records report\" is what Copilot wrote into the person's latest audit record.", "full", CsvUrl()));
    }
}
