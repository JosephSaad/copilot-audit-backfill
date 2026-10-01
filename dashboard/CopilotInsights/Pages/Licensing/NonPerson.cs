using System.Net;
using CopilotInsights.Infrastructure;
using Microsoft.AspNetCore.Html;

namespace CopilotInsights.Pages.Licensing;

/// <summary>
/// Copilot licenses on accounts that are enabled but may not be a person, or not one of the organization's
/// (2.4.3): guests, names that read like a service, shared or test account, and accounts outside the org
/// chart with no manager. Plus every license holder by employee type, for contractors and the like.
/// Disabled and deleted accounts are Licenses on disabled accounts.
/// </summary>
public sealed class NonPersonModel(PageServices s) : InsightsPage(s)
{
    public override string Title => "Licenses on non-person accounts";
    public override string Persona => "Licensing";
    public override string? RequiredRole => Roles.Licensing;
    public override string? OwnWindow => "today's licenses";
    public override string Intro => "Copilot licenses held by guests, by accounts whose name reads like a service, shared, test or room account, and by accounts outside the org chart with no manager. Each is worth a look: remove the license, or confirm it is meant.";

    /* LIKE patterns on the lowercased local part of a sign-in, and on the lowercased display name, that usually mean an
       account is not a person. Each needs a separator or the whole name, so a surname such as Testa or Botha is not caught;
       a false match costs a look, a missed one a license. */
    internal static readonly string[] UpnPatterns = ["svc%", "srv-%", "srv[_]%", "srv.%", "service%", "sa-%", "sa[_]%", "adm-%", "adm[_]%", "admin%",
                                                      "test", "test[-_.0-9]%", "tst-%", "noreply%", "no-reply%", "donotreply%", "shared%", "room", "room[-_.0-9]%",
                                                      "conf-%", "mtg-%", "app-%", "app[_]%", "sync-%", "sync[_]%", "helpdesk%", "support", "info", "mailbox%"];
    internal static readonly string[] NamePatterns = ["%service account%", "%shared mailbox%", "%conference room%", "%meeting room%", "%test account%",
                                                       "%test user%", "%mailbox%", "%do not reply%", "%no reply%"];

    static readonly Dictionary<string, (string Label, string Sql)> Why = new()
    {
        ["all"] = ("Any reason", "(x.Guest = 1 OR x.NameLike = 1 OR x.NoChart = 1)"),
        ["guest"] = ("Guests", "x.Guest = 1"),
        ["name"] = ("Name reads like an account", "x.NameLike = 1"),
        ["chart"] = ("Outside the org chart", "x.NoChart = 1"),
    };

    static readonly Dictionary<string, string> Sorts = new()
    {
        ["name"] = "x.DisplayName, x.UserPrincipalName", ["since"] = "x.LicensedSinceUtc, x.DisplayName", ["idle"] = "x.LastKey, x.DisplayName",
        ["upn"] = "x.UserPrincipalName", ["dept"] = "x.Department, x.DisplayName", ["type"] = "x.EmployeeType, x.DisplayName",
    };
    static readonly Dictionary<string, string> SortLabels = new() { ["name"] = "Name", ["since"] = "Licensed longest", ["idle"] = "Idle longest" };

    sealed class Row
    {
        public string? DisplayName { get; set; }
        public string UserPrincipalName { get; set; } = "";
        public string? Department { get; set; }
        public string? EmployeeType { get; set; }
        public bool Guest { get; set; }
        public bool NameLike { get; set; }
        public bool NoChart { get; set; }
        public DateTime? LicensedSinceUtc { get; set; }
        public DateTime? LastUsed { get; set; }
        public int TotalRows { get; set; }
    }

    protected override async Task BuildAsync()
    {
        var why = Request.Query["why"].FirstOrDefault() is { } w && Why.ContainsKey(w) ? w : "all";
        var type = Request.Query["type"].FirstOrDefault() is { Length: > 0 and <= 100 } ty ? ty : null;
        var userType = await Db.ScalarAsync<int>("SELECT CASE WHEN COL_LENGTH(N'audit.CopilotLicense', N'UserType') IS NULL THEN 0 ELSE 1 END") == 1;
        var guestSql = "du.UserPrincipalName LIKE N'%#EXT#%'" + (userType ? " OR cl.UserType = N'Guest'" : "");
        var local = "LOWER(LEFT(du.UserPrincipalName, CHARINDEX(N'@', du.UserPrincipalName + N'@') - 1))";
        var nameSql = string.Join(" OR ", UpnPatterns.Select((_, i) => $"{local} LIKE @p{i}").Concat(NamePatterns.Select((_, i) => $"LOWER(du.DisplayName) LIKE @n{i}")));
        var pg = new Paging(Request, Sorts, "name");
        var args = Ctx.Args(new { q = pg.Like, type });
        for (var i = 0; i < UpnPatterns.Length; i++) args.Add($"p{i}", UpnPatterns[i]);
        for (var i = 0; i < NamePatterns.Length; i++) args.Add($"n{i}", NamePatterns[i]);

        /* Enabled accounts still in the directory, with a license today: the disabled and the gone are on their own page. */
        var cte = $@"
WITH x AS (SELECT du.DisplayName, du.UserPrincipalName, du.Department, NULLIF(du.EmployeeType, N'') AS EmployeeType, du.LicensedSinceUtc,
                  CAST(CASE WHEN {guestSql} THEN 1 ELSE 0 END AS BIT) AS Guest,
                  CAST(CASE WHEN {nameSql} THEN 1 ELSE 0 END AS BIT) AS NameLike,
                  CAST(CASE WHEN du.ManagerUpn IS NULL AND du.OrgDepth IS NULL THEN 1 ELSE 0 END AS BIT) AS NoChart,
                  (SELECT MAX(ud.DateKey) FROM dash.UserDay AS ud WHERE ud.UserKey = du.UserKey AND ud.Interactions > 0) AS LastKey
           FROM bi.FactLicensedDay AS ld {Ctx.Scope("ld.UserKey")}
           JOIN bi.DimUser AS du ON du.UserKey = ld.UserKey
           LEFT JOIN audit.CopilotLicense AS cl ON cl.UserPrincipalName = du.UserPrincipalName
           WHERE ld.DateKey = @k1 AND ISNULL(du.AccountEnabled, 1) = 1 AND du.DirectoryStatus = N'Found'
             AND (cl.UserPrincipalName IS NULL OR ISNULL(cl.DirectoryStatus, N'Found') = N'Found'))";
        var rows = await Db.QueryAsync<Row>($@"{cte}
SELECT x.DisplayName, x.UserPrincipalName, x.Department, x.EmployeeType, x.Guest, x.NameLike, x.NoChart, x.LicensedSinceUtc, dl.[Date] AS LastUsed,
       COUNT(*) OVER () AS TotalRows
FROM x LEFT JOIN bi.DimDate AS dl ON dl.DateKey = x.LastKey
WHERE {(type is null ? Why[why].Sql : "x.EmployeeType = @type")}
  AND (@q IS NULL OR x.DisplayName LIKE @q OR x.UserPrincipalName LIKE @q OR x.Department LIKE @q)
{pg.OrderAndPage()};", args);
        pg.Total = rows.FirstOrDefault()?.TotalRows ?? 0;
        var counts = (await Db.QueryAsync<(int Guest, int NameLike, int NoChart, int Any)>($@"{cte}
SELECT COUNT(CASE WHEN Guest = 1 THEN 1 END), COUNT(CASE WHEN NameLike = 1 THEN 1 END), COUNT(CASE WHEN NoChart = 1 THEN 1 END),
       COUNT(CASE WHEN Guest = 1 OR NameLike = 1 OR NoChart = 1 THEN 1 END) FROM x;", args)).Single();
        var types = await Db.QueryAsync<(string Type, int N)>($@"{cte}
SELECT ISNULL(x.EmployeeType, N'(not set)'), COUNT(*) FROM x GROUP BY x.EmployeeType ORDER BY COUNT(*) DESC;", args);

        Kpis.Add(new("To check", Num(counts.Any), "licenses on accounts that may not be a person", counts.Any > 0 ? "warn" : null));
        Kpis.Add(new("Guests", Num(counts.Guest)));
        Kpis.Add(new("Names like an account", Num(counts.NameLike), "service, shared, test, room"));
        Kpis.Add(new("Outside the org chart", Num(counts.NoChart), "no manager, not under the top"));

        var t = new Table
        {
            Headers = ["Name", "Sign-in", "Department", "Employee type", "Why", "Licensed since", "Last used"],
            EmptyText = type is null ? "No license on an account like these." : $"No license holder has employee type {type}.",
            /* Last used: Idle longest, the oldest first. Why is a list of reasons: no sort. */
            Server = new(pg, Request, ["name", "upn", "dept", "type", null, "since", "idle"]),
        };
        foreach (var r in rows)
            t.Rows.Add([r.DisplayName, r.UserPrincipalName, r.Department, r.EmployeeType,
                        string.Join(", ", new[] { r.Guest ? "Guest" : null, r.NameLike ? "Name reads like an account" : null, r.NoChart ? "Outside the org chart" : null }.Where(x => x is not null)),
                        r.LicensedSinceUtc?.Date, r.LastUsed is null ? "Never" : r.LastUsed.Value.Date]);
        CsvTable = t;
        var tabs = string.Join(" · ", Why.Select(kv =>
            $"<a href=\"{WebUtility.HtmlEncode(Ctx.Url(Request.Path, extra: $"why={kv.Key}"))}\"{(type is null && kv.Key == why ? " class=\"on\"" : "")}>{kv.Value.Label}</a>"));
        Panels.Add(new(type is null ? "Accounts" : $"Employee type: {type}", new HtmlString($"<p class=\"sub\">Show: {tabs}</p>" + pg.Controls(Request, SortLabels).Value + t.Render().Value),
            "Enabled accounts still in the directory that hold a Copilot license today. Guest: an #EXT# sign-in, or Entra's user type. " +
            "Name reads like an account: a sign-in such as svc-, service, admin, test-, noreply, shared, room- or support, or a display name such as \"shared mailbox\". " +
            "Outside the org chart: no manager and not under the top of the organization. Disabled and deleted accounts are on Licenses on disabled accounts.", "full", CsvUrl()));

        var tt = new Table { Headers = ["Employee type", "License holders"], Numeric = new HashSet<int> { 1 } };
        foreach (var (ty2, n) in types)
            tt.Rows.Add([ty2 == "(not set)" ? ty2 : new HtmlString($"<a href=\"{WebUtility.HtmlEncode(Ctx.Url(Request.Path, extra: $"type={Uri.EscapeDataString(ty2)}"))}\">{WebUtility.HtmlEncode(ty2)}</a>"), n]);
        Panels.Add(new("License holders by employee type", tt.Render(),
            "Everyone holding a license today, by Entra's or AD's employee type. Open a type, contractors or interns for example, to list its holders.", "half"));
    }
}
