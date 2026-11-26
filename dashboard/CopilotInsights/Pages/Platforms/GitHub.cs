using System.Net;
using CopilotInsights.Charts;
using CopilotInsights.Data;
using CopilotInsights.Infrastructure;
using Microsoft.AspNetCore.Html;

namespace CopilotInsights.Pages.Platforms;

/// <summary>The GitHub pages' common reads: the seat prices and the idle window, as Costs and savings counts them.</summary>
public abstract class GitHubPage(PageServices s) : PlatformPage(s)
{
    protected async Task<(decimal Business, decimal Enterprise, int IdleDays)> PricesAsync()
    {
        var prices = await HasTableAsync("dash.CostPrice")
            ? (await Db.QueryAsync<(string Item, decimal Price)>("SELECT Item, MonthlyPrice FROM dash.CostPrice WHERE Item IN ('GITHUB_COPILOT_BUSINESS', 'GITHUB_COPILOT_ENTERPRISE');")).ToDictionary(x => x.Item, x => x.Price)
            : [];
        return (prices.GetValueOrDefault("GITHUB_COPILOT_BUSINESS", 19), prices.GetValueOrDefault("GITHUB_COPILOT_ENTERPRISE", 39),
                (int)Math.Clamp(await S.Settings.IntAsync("CostIdleDays", 30), 7, 180));
    }

    /// <summary>The SQL that says whether a seat is idle, as Costs and savings counts it (see <see cref="PlatformRules.IsIdleSeat"/>).</summary>
    protected const string IdleSql = "((s.LastActivityUtc IS NULL AND (s.CreatedUtc IS NULL OR s.CreatedUtc < DATEADD(DAY, -@idleDays, SYSUTCDATETIME()))) OR s.LastActivityUtc < DATEADD(DAY, -@idleDays, SYSUTCDATETIME()))";

    /// <summary>Each seat's person: the administrator's list first, then the SCIM identity.</summary>
    protected async Task<string> PersonJoinAsync()
    {
        var ids = await HasTableAsync("audit.GitHubIdentity");
        var map = await HasTableAsync("dash.GitHubIdentityMap");
        return $@"OUTER APPLY (SELECT COALESCE({(map ? "(SELECT m.Upn FROM dash.GitHubIdentityMap AS m WHERE m.Login = s.Login)" : "NULL")},
                              {(ids ? "(SELECT TOP (1) i.Upn FROM audit.GitHubIdentity AS i WHERE i.Scope = s.Scope AND i.Login = s.Login)" : "NULL")}) AS Upn) AS who
LEFT JOIN audit.UserDirectory AS ud ON ud.UserPrincipalName = who.Upn";
    }
}

/// <summary>GitHub Copilot overview: the seats, who uses them, what they cost, and the people who hold both Copilots.</summary>
public sealed class GitHubOverviewModel(PageServices s) : GitHubPage(s)
{
    public override string Title => "GitHub Copilot overview";
    public override string Intro => "GitHub Copilot across the enterprise: the seats held and used, the seats paid for and idle, what GitHub billed, how much of Copilot's code is accepted, and who also holds Microsoft 365 Copilot.";

    protected override async Task BuildAsync()
    {
        if (!await HasRowsAsync("audit.GitHubCopilotSeat"))
        {
            Notice = NotSetUp("GitHub Copilot", "tools\\Import-GitHubCopilotUsage.ps1 (docs/GITHUB-COPILOT-USAGE.md)");
            return;
        }
        var (business, enterprise, idleDays) = await PricesAsync();
        var args = Ctx.Args(new { idleDays, business, enterprise });
        var seats = (await Db.QueryAsync<(int Seats, int Business, int Enterprise, int Active, int Idle, int Never, decimal IdleSpend)>($@"
SELECT COUNT(*), SUM(CASE WHEN s.PlanType = 'enterprise' THEN 0 ELSE 1 END), SUM(CASE WHEN s.PlanType = 'enterprise' THEN 1 ELSE 0 END),
       SUM(CASE WHEN {IdleSql} THEN 0 ELSE 1 END), SUM(CASE WHEN {IdleSql} THEN 1 ELSE 0 END), SUM(CASE WHEN s.LastActivityUtc IS NULL THEN 1 ELSE 0 END),
       ISNULL(SUM(CASE WHEN {IdleSql} THEN CASE WHEN s.PlanType = 'enterprise' THEN @enterprise ELSE @business END END), 0)
FROM audit.GitHubCopilotSeat AS s;", args)).Single();
        var use = (await Db.QueryAsync<(long Generations, long Acceptances)>("SELECT ISNULL(SUM(CodeGenerations), 0), ISNULL(SUM(CodeAcceptances), 0) FROM audit.GitHubCopilotDay WHERE [Day] BETWEEN @d0 AND @d1;", Ctx.Args())).Single();
        var billed = await Db.ScalarAsync<decimal?>("SELECT SUM(NetAmount) FROM audit.GitHubCopilotCost WHERE UsageDate BETWEEN @d0 AND @d1;", Ctx.Args()) ?? 0;
        var cur = "USD";

        string? Seats(string show) => Open("/platforms/github/seats", $"show={show}");
        Kpis.Add(new("Seats", Num(seats.Seats), $"{seats.Business:#,0} Business, {seats.Enterprise:#,0} Enterprise"));
        Kpis.Add(new("Active", Num(seats.Active), $"used Copilot in the last {idleDays} days"));
        Kpis.Add(new("Idle seats", Num(seats.Idle), $"{Money(seats.IdleSpend, cur)} a month at list price: no use in {idleDays} days, {seats.Never:#,0} never used", seats.Idle > 0 ? "warn" : null, Href: seats.Idle > 0 ? Seats("idle") : null));
        Kpis.Add(new("Code accepted", Pct(Ratio(use.Acceptances, use.Generations)), "of the code Copilot suggested in the period, as GitHub's usage report counts it"));
        Kpis.Add(new($"Billed in the period ({cur})", Money(billed, cur), "GitHub's billing usage for Copilot: seats and AI credits"));

        /* Who holds both: a seat's person (by the identities or the administrator's list) holding Microsoft 365 Copilot on the newest day. */
        if (await HasTableAsync("audit.GitHubIdentity"))
        {
            var both = (await Db.QueryAsync<(int Both, int BothUsing)>($@"
WITH people AS (SELECT DISTINCT who.Upn, s.LastActivityUtc FROM audit.GitHubCopilotSeat AS s {await PersonJoinAsync()} WHERE who.Upn IS NOT NULL),
lic AS (SELECT du.UserPrincipalName, du.UserKey FROM bi.FactLicensedDay AS ld JOIN bi.DimUser AS du ON du.UserKey = ld.UserKey
        WHERE ld.DateKey = (SELECT MAX(DateKey) FROM bi.FactLicensedDay)),
flagged AS (SELECT CASE WHEN p.LastActivityUtc >= DATEADD(DAY, -30, SYSUTCDATETIME())
                         AND EXISTS (SELECT 1 FROM dash.UserDay AS u WHERE u.UserKey = lic.UserKey AND u.DateKey >= @k30 AND u.Interactions > 0) THEN 1 ELSE 0 END AS UsingBoth
            FROM people AS p JOIN lic ON lic.UserPrincipalName = p.Upn)
SELECT COUNT(*), ISNULL(SUM(UsingBoth), 0) FROM flagged;", Ctx.Args(new { k30 = ReportContext.Key(Ctx.End.AddDays(-29)) }))).Single();
            Kpis.Add(new("Hold both Copilots", Num(both.Both), $"a GitHub Copilot seat and a Microsoft 365 Copilot license; {both.BothUsing:#,0} used both in the last 30 days"));
        }

        var trend = await Db.QueryAsync<(DateTime Bucket, double Active)>($@"
SELECT {Ctx.BucketSql("d")}, AVG(CAST(g.DailyActiveUsers AS FLOAT)) FROM audit.GitHubCopilotDay AS g JOIN bi.DimDate AS d ON d.[Date] = g.[Day]
WHERE g.[Day] BETWEEN @d0 AND @d1 GROUP BY {Ctx.BucketSql("d")};", Ctx.Args());
        var buckets = Buckets();
        Panels.Add(new("Daily active users", Chart.Line(Labels(), [new Series("Daily active users", buckets.Select(b => (double?)trend.FirstOrDefault(x => x.Bucket == b.Key).Active).ToList(), 1)], Fmt.Count, "GitHub Copilot daily active users"),
            $"From GitHub's usage report: the average a day at each point. {TrendNote}", "half"));
        var bySku = await Db.QueryAsync<(string Sku, decimal Net)>("SELECT ISNULL(Sku, N'(none)'), SUM(NetAmount) FROM audit.GitHubCopilotCost WHERE UsageDate BETWEEN @d0 AND @d1 GROUP BY Sku ORDER BY SUM(NetAmount) DESC;", Ctx.Args());
        Panels.Add(new("Billed by product", Chart.Bars(bySku.Select(x => new Bar(x.Sku, (double)Math.Round(x.Net))).ToList(), Fmt.Count, $"Billed by product ({cur})"),
            "GitHub's billing usage in the period: seats by plan, and AI credits beyond those included.", "half"));
        var byOrg = await Db.QueryAsync<(string Org, int Seats, int Idle)>($@"
SELECT ISNULL(s.Organization, N'(none)'), COUNT(*), SUM(CASE WHEN {IdleSql} THEN 1 ELSE 0 END) FROM audit.GitHubCopilotSeat AS s GROUP BY s.Organization ORDER BY COUNT(*) DESC;", args);
        Panels.Add(new("Seats by organization", Chart.Bars(byOrg.Select(x => new Bar(x.Org, x.Seats, Note: $"{x.Idle:#,0} idle")).ToList(), Fmt.Count, "Seats by organization"),
            "The organization each seat was given through.", "half"));
        var models = await Db.QueryAsync<(string Model, decimal Quantity)>("SELECT Model, SUM(Quantity) FROM audit.GitHubCopilotCost WHERE UsageDate BETWEEN @d0 AND @d1 AND Model IS NOT NULL AND Model <> N'' GROUP BY Model ORDER BY SUM(Quantity) DESC;", Ctx.Args());
        if (models.Count > 0)
            Panels.Add(new("Premium requests by model", Chart.Bars(models.Take(12).Select(x => new Bar(x.Model, (double)x.Quantity)).ToList(), Fmt.Count, "Premium requests by model"),
                "The premium requests GitHub billed in the period, by the model that answered.", "half"));
    }
}

/// <summary>GitHub Copilot seats: every seat with the person behind it, for those who hold the Platforms role.</summary>
public sealed class GitHubSeatsModel(PageServices s) : GitHubPage(s)
{
    public override string Title => "GitHub Copilot seats";
    public override bool CountsWholeOrganization => false;   // the viewer's scope, unlike the section's other pages
    public override string? RequiredRole => Roles.Platforms;
    public override bool UsesPeriod => false;
    public override string Intro => "Every GitHub Copilot seat and the person behind it: when it was last used, in which editor, and the seats to take back, idle, never used, held by people who have left, or with no person matched.";

    static readonly Dictionary<string, string> Sorts = new()
    {
        ["login"] = "s.Login", ["name"] = "ud.FullName, s.Login", ["dept"] = "ud.Department, s.Login", ["org"] = "s.Organization, s.Login",
        ["plan"] = "s.PlanType, s.Login", ["last"] = "s.LastActivityUtc, s.Login",
    };

    /// <summary>The filters of the list, as the overview's figures open it.</summary>
    public static readonly IReadOnlyDictionary<string, (string Label, string Sql)> Shows = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase)
    {
        ["idle"] = ("Idle seats", IdleSql),
        ["never"] = ("Never used", "s.LastActivityUtc IS NULL"),
        ["left"] = ("Held by people who have left", "(who.Upn IS NOT NULL AND (ud.UserPrincipalName IS NULL OR ud.AccountEnabled = 0))"),
        ["unmatched"] = ("No person matched", "who.Upn IS NULL"),
    };

    sealed class Row
    {
        public string Login { get; set; } = "";
        public string? Upn { get; set; }
        public string? FullName { get; set; }
        public string? Department { get; set; }
        public bool? AccountEnabled { get; set; }
        public string? Organization { get; set; }
        public string? AssigningTeam { get; set; }
        public string? PlanType { get; set; }
        public DateTime? LastActivityUtc { get; set; }
        public string? LastActivityEditor { get; set; }
        public bool Idle { get; set; }
        public int TotalRows { get; set; }
    }

    protected override async Task BuildAsync()
    {
        if (!await HasRowsAsync("audit.GitHubCopilotSeat"))
        {
            Notice = NotSetUp("GitHub Copilot", "tools\\Import-GitHubCopilotUsage.ps1 (docs/GITHUB-COPILOT-USAGE.md)");
            return;
        }
        var (_, _, idleDays) = await PricesAsync();
        var show = Request.Query["show"].FirstOrDefault() is { } sh && Shows.ContainsKey(sh) ? sh.ToLowerInvariant() : null;
        var pg = new Paging(Request, Sorts, "login");
        var join = await PersonJoinAsync();
        /* A person outside the viewer's part of the organization is not listed to them; seats with no person are, to the whole organization's viewers only. */
        var scope = Ctx.Root is null ? "" : "AND who.Upn IN (SELECT du.UserPrincipalName FROM bi.DimUser AS du JOIN dash.fnScopeUsers(@root) AS sc ON sc.UserKey = du.UserKey)";
        var rows = await Db.QueryAsync<Row>($@"
SELECT s.Login, who.Upn, ud.FullName, ud.Department, ud.AccountEnabled, s.Organization, s.AssigningTeam, s.PlanType, s.LastActivityUtc, s.LastActivityEditor,
       CAST(CASE WHEN {IdleSql} THEN 1 ELSE 0 END AS BIT) AS Idle, COUNT(*) OVER () AS TotalRows
FROM audit.GitHubCopilotSeat AS s
{join}
WHERE 1 = 1 {scope} {(show is null ? "" : "AND " + Shows[show].Sql)}
  AND (@like IS NULL OR s.Login LIKE @like OR ud.FullName LIKE @like OR ud.Department LIKE @like OR s.Organization LIKE @like)
{pg.OrderAndPage()};", Ctx.Args(new { idleDays, like = pg.Like, root = Ctx.Root }));
        pg.Total = rows.FirstOrDefault()?.TotalRows ?? 0;
        Kpis.Add(new(show is null ? "Seats" : Shows[show].Label, Num(pg.Total), show switch
        {
            "idle" => $"no use in {idleDays} days, or never since given that long ago",
            "never" => "no Copilot activity since the seat was given",
            "left" => "the person is disabled or gone from the directory",
            "unmatched" => "no SCIM identity and not in the administrator's list",
            _ => "in the enterprise",
        }, show is null ? null : pg.Total > 0 ? "warn" : null));

        string Filter(string? key, string text) => key == show ? $"<b>{WebUtility.HtmlEncode(text)}</b>"
            : $"<a href=\"{WebUtility.HtmlEncode(Ctx.Url(Request.Path, keepOrg: false, extra: key is null ? null : $"show={key}"))}\">{WebUtility.HtmlEncode(text)}</a>";
        var filters = $"<p class=\"sub\">Show: {Filter(null, "every seat")} · {Filter("idle", "idle")} · {Filter("never", "never used")} · {Filter("left", "held by people who have left")} · {Filter("unmatched", "no person matched")}</p>";

        var t = new Table
        {
            Headers = ["Login", "Person", "Department", "Organization", "Team", "Plan", "Last used", "Editor", "Status"], EmptyText = "No seat matches.",
            Server = new(pg, Request, ["login", "name", "dept", "org", null, "plan", "last", null, null], new HashSet<int> { 6 }),
        };
        foreach (var r in rows)
        {
            var status = r.Upn is null ? ("No person matched", "muted") : r.AccountEnabled != true ? ("Left", "status-bad")
                : r.LastActivityUtc is null ? ("Never used", "status-warn") : r.Idle ? ("Idle", "status-warn") : ("Active", "status-ok");
            t.Rows.Add([r.Login, r.FullName ?? r.Upn, r.Department, r.Organization, r.AssigningTeam, r.PlanType, r.LastActivityUtc?.Date ?? (object)"never", r.LastActivityEditor,
                        new HtmlString($"<span class=\"{status.Item2}\">{status.Item1}</span>")]);
        }
        CsvTable = t;
        Panels.Add(new("Seats", new HtmlString(filters + pg.Controls(Request, new Dictionary<string, string> { ["login"] = "Login", ["name"] = "Person", ["last"] = "Least recently used" }).Value + t.Render().Value),
            "Person: from the enterprise's SCIM identities (managed users), or an administrator's list of logins and people. Left: the person is disabled or gone from the directory. " +
            $"Idle: no use in {idleDays} days (CostIdleDays), as Costs and savings counts it.", "full", CsvUrl()));
    }
}

/// <summary>GitHub Copilot posture: each organization's Copilot settings beside the recommended ones, and where they differ.</summary>
public sealed class GitHubPostureModel(PageServices s) : GitHubPage(s)
{
    public override string Title => "GitHub Copilot posture";
    public override bool UsesPeriod => false;
    public override string Intro => "Each organization's GitHub Copilot settings, side by side, against the settings GitHub recommends for an enterprise, and the ones that differ from one organization to the next.";

    protected override async Task BuildAsync()
    {
        if (!await HasRowsAsync("audit.GitHubCopilotPolicy"))
        {
            Notice = NotSetUp("GitHub Copilot's settings", "tools\\Import-GitHubCopilotUsage.ps1 from 3.3.0 (docs/GITHUB-COPILOT-USAGE.md)");
            return;
        }
        var rows = await Db.QueryAsync<(string Organization, string Policy, string? State)>("SELECT Organization, Policy, [State] FROM audit.GitHubCopilotPolicy ORDER BY Organization, Policy;");
        var orgs = rows.Select(r => r.Organization).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(o => o, StringComparer.OrdinalIgnoreCase).ToList();
        var policies = rows.Select(r => r.Policy).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(p => PlatformRules.Recommended(p) is null ? 1 : 0).ThenBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
        string? State(string org, string policy) => rows.FirstOrDefault(r => string.Equals(r.Organization, org, StringComparison.OrdinalIgnoreCase) && string.Equals(r.Policy, policy, StringComparison.OrdinalIgnoreCase)).State;
        var differ = policies.Count(p => orgs.Select(o => State(o, p) ?? "").Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1);
        var off = orgs.Count(o => policies.Any(p => PlatformRules.Recommended(p) is { } rec && !string.Equals(State(o, p), rec, StringComparison.OrdinalIgnoreCase)));
        Kpis.Add(new("Organizations", Num(orgs.Count), "with Copilot settings loaded"));
        Kpis.Add(new("Settings that differ", Num(differ), "set one way in one organization and another way in another", differ > 0 ? "warn" : null));
        Kpis.Add(new("Off the recommended settings", Num(off), "organizations with a setting other than GitHub's recommendation", off > 0 ? "warn" : null));

        var t = new Table { Headers = ["Setting", "Recommended", .. orgs], Sortable = false };
        foreach (var p in policies)
        {
            var rec = PlatformRules.Recommended(p);
            var cells = orgs.Select(o =>
            {
                var st = State(o, p);
                var bad = rec is not null && !string.Equals(st, rec, StringComparison.OrdinalIgnoreCase);
                return (object?)new HtmlString($"<span class=\"{(bad ? "status-warn" : "")}\">{WebUtility.HtmlEncode(st ?? "not set")}</span>");
            });
            t.Rows.Add([PlatformRules.PolicyName(p), rec ?? "your choice", .. cells]);
        }
        CsvTable = t;
        Panels.Add(new("Settings by organization", t.Render(),
            "From each organization's Copilot settings, as the GitHub load last read them. Recommended: block suggestions matching public code (they may carry a license obligation), and give seats to chosen people or teams rather than everyone. The rest are choices; a setting that differs between organizations is worth a decision.",
            "full", CsvUrl()));
    }
}
