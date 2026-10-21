using System.Text.Json;
using CopilotInsights.Infrastructure;

namespace CopilotInsights.Data;

/// <summary>Which signals count an account as not a person (Admin › Reference data), from dash.Setting.</summary>
public sealed record NonHumanRules(bool Guests, bool Names, bool NoChart, IReadOnlyList<string> Patterns, IReadOnlyList<string> EmployeeTypes, IReadOnlyList<string> Groups)
{
    public bool Any => Guests || Names || NoChart || Patterns.Count > 0 || EmployeeTypes.Count > 0 || Groups.Count > 0;
}

/// <summary>
/// A Copilot license on an account that is not a person is a policy breach (2.4.5): the license is for people. The
/// accounts are found by the rules an administrator sets: guests, sign-ins and names that read like a service,
/// shared, test or room account, accounts outside the org chart with no manager, the employee types and the AD
/// groups that mark service accounts, and sign-in patterns of the organization's own. An account an administrator
/// confirmed as a person (dash.NonHumanException, with a reason) is not one. One query, for the pages (in the
/// viewer's scope), the alert and the board pack (the whole organization).
/// </summary>
public static class NonHuman
{
    /* LIKE patterns on the lowercased local part of a sign-in, and on the lowercased display name, that usually mean an
       account is not a person. Each needs a separator or the whole name, so a surname such as Testa or Botha is not caught. */
    public static readonly string[] UpnPatterns = ["svc%", "srv-%", "srv[_]%", "srv.%", "service%", "sa-%", "sa[_]%", "adm-%", "adm[_]%", "admin%",
                                                  "test", "test[-_.0-9]%", "tst-%", "noreply%", "no-reply%", "donotreply%", "shared%", "room", "room[-_.0-9]%",
                                                  "conf-%", "mtg-%", "app-%", "app[_]%", "sync-%", "sync[_]%", "helpdesk%", "support", "info", "mailbox%"];
    public static readonly string[] NamePatterns = ["%service account%", "%shared mailbox%", "%conference room%", "%meeting room%", "%test account%",
                                                   "%test user%", "%mailbox%", "%do not reply%", "%no reply%"];

    /// <summary>The settings behind each rule, with their defaults: the three built-in signals on, the lists empty.</summary>
    public const string GuestsSetting = "NonHumanGuests", NamesSetting = "NonHumanNames", NoChartSetting = "NonHumanNoChart",
        PatternsSetting = "NonHumanPatterns", TypesSetting = "NonHumanEmployeeTypes", GroupsSetting = "NonHumanGroups";

    public static async Task<NonHumanRules> RulesAsync(Settings settings)
    {
        async Task<bool> On(string name) => await settings.StringAsync(name) != "Off";
        async Task<IReadOnlyList<string>> List(string name) => Split(await settings.StringAsync(name));
        return new(await On(GuestsSetting), await On(NamesSetting), await On(NoChartSetting), await List(PatternsSetting), await List(TypesSetting), await List(GroupsSetting));
    }

    /// <summary>A comma, semicolon or line separated list, trimmed, without blanks or repeats.</summary>
    public static IReadOnlyList<string> Split(string? value) =>
        (value ?? "").Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>An administrator's sign-in pattern ("bot-*", "*-svc") as a LIKE pattern on the lowercased local part: * is any text, the rest literal.</summary>
    public static string Like(string pattern) =>
        pattern.Trim().ToLowerInvariant().Split('@')[0].Replace("[", "[[]").Replace("%", "[%]").Replace("_", "[_]").Replace('*', '%');

    /// <summary>
    /// The CTE "x": every enabled account in the directory holding a Copilot license on @k1, in <paramref name="scopeJoin"/>,
    /// with a flag for each signal, Breach (any signal and not confirmed a person) and the confirmation if there is one.
    /// The last day of use is not here (2.4.11): working it out for every license holder was half this query's time, and
    /// only the page's list shows it, for the rows it shows (<see cref="LastUsed"/>). Fills <paramref name="args"/> with
    /// the patterns it needs.
    /// </summary>
    public static async Task<string> CteAsync(Db db, NonHumanRules rules, string scopeJoin, Dapper.DynamicParameters args)
    {
        var userType = await db.ScalarAsync<int>("SELECT CASE WHEN COL_LENGTH(N'audit.CopilotLicense', N'UserType') IS NULL THEN 0 ELSE 1 END") == 1;
        var exceptions = await db.ScalarAsync<int>("SELECT CASE WHEN OBJECT_ID(N'dash.NonHumanException', N'U') IS NULL THEN 0 ELSE 1 END") == 1;
        var groups = await db.ScalarAsync<int>("SELECT CASE WHEN OBJECT_ID(N'audit.GroupMember', N'U') IS NULL THEN 0 ELSE 1 END") == 1;
        /* The sign-in's local part and the display name, lowercased once per account (v.L and v.N, below) and compared as
           plain text (2.4.11): the patterns are lowercase too, and 38 of them against every license holder in the
           database's own collation was most of this query's time. */
        const string local = "v.L", bin = "COLLATE Latin1_General_100_BIN2";
        for (var i = 0; i < UpnPatterns.Length; i++) args.Add($"nhp{i}", UpnPatterns[i]);
        for (var i = 0; i < NamePatterns.Length; i++) args.Add($"nhn{i}", NamePatterns[i]);
        args.Add("nhx", JsonSerializer.Serialize(rules.Patterns.Select(Like).Where(p => p.Length > 0)));
        args.Add("nht", JsonSerializer.Serialize(rules.EmployeeTypes));
        args.Add("nhg", JsonSerializer.Serialize(rules.Groups));
        var guest = !rules.Guests ? "0" : "du.UserPrincipalName LIKE N'%#EXT#%'" + (userType ? " OR cl.UserType = N'Guest'" : "");
        var name = !rules.Names ? "0" : string.Join(" OR ", UpnPatterns.Select((_, i) => $"{local} LIKE @nhp{i}").Concat(NamePatterns.Select((_, i) => $"v.N LIKE @nhn{i}")));
        var pattern = rules.Patterns.Count == 0 ? "0" : $"EXISTS (SELECT 1 FROM OPENJSON(@nhx) AS p WHERE {local} LIKE p.[value] {bin})";
        var chart = !rules.NoChart ? "0" : "du.ManagerUpn IS NULL AND du.OrgDepth IS NULL";
        var type = rules.EmployeeTypes.Count == 0 ? "0" : "du.EmployeeType IN (SELECT t.[value] FROM OPENJSON(@nht) AS t)";
        var group = rules.Groups.Count == 0 || !groups ? "0"
            : "EXISTS (SELECT 1 FROM audit.GroupMember AS gm WHERE gm.UserPrincipalName = du.UserPrincipalName AND gm.GroupName IN (SELECT g.[value] FROM OPENJSON(@nhg) AS g))";
        static string Flag(string sql) => sql == "0" ? "CAST(0 AS BIT)" : $"CAST(CASE WHEN {sql} THEN 1 ELSE 0 END AS BIT)";
        return $@"
WITH s AS (SELECT du.UserKey, du.DisplayName, du.UserPrincipalName, du.Department, NULLIF(du.EmployeeType, N'') AS EmployeeType, du.LicensedSinceUtc,
                  {Flag(guest)} AS Guest, {Flag(name == "0" ? pattern : pattern == "0" ? name : $"{name} OR {pattern}")} AS NameLike,
                  {Flag(chart)} AS NoChart, {Flag(type)} AS TypeMatch, {Flag(group)} AS GroupMatch,
                  {(exceptions ? "ex.Reason, ex.ConfirmedBy, ex.ConfirmedUtc" : "CAST(NULL AS NVARCHAR(400)) AS Reason, CAST(NULL AS NVARCHAR(256)) AS ConfirmedBy, CAST(NULL AS DATETIME2(0)) AS ConfirmedUtc")}
           FROM bi.FactLicensedDay AS ld {scopeJoin}
           JOIN bi.DimUser AS du ON du.UserKey = ld.UserKey
           CROSS APPLY (SELECT LOWER(LEFT(du.UserPrincipalName, CHARINDEX(N'@', du.UserPrincipalName + N'@') - 1)) {bin} AS L,
                               LOWER(du.DisplayName) {bin} AS N) AS v
           LEFT JOIN audit.CopilotLicense AS cl ON cl.UserPrincipalName = du.UserPrincipalName
           {(exceptions ? "LEFT JOIN dash.NonHumanException AS ex ON ex.Upn = du.UserPrincipalName" : "")}
           WHERE ld.DateKey = @k1 AND ISNULL(du.AccountEnabled, 1) = 1 AND du.DirectoryStatus = N'Found'
             AND (cl.UserPrincipalName IS NULL OR ISNULL(cl.DirectoryStatus, N'Found') = N'Found')),
x AS (SELECT s.*, CAST(CASE WHEN (s.Guest = 1 OR s.NameLike = 1 OR s.NoChart = 1 OR s.TypeMatch = 1 OR s.GroupMatch = 1) AND s.Reason IS NULL THEN 1 ELSE 0 END AS BIT) AS Breach,
             CAST(CASE WHEN s.Reason IS NOT NULL THEN 1 ELSE 0 END AS BIT) AS Confirmed
      FROM s)";
    }

    /// <summary>The last day an account used Copilot, as a subquery on <paramref name="userKey"/>.</summary>
    public static string LastUsed(string userKey) =>
        $"(SELECT MAX(ud.DateKey) FROM dash.UserDay AS ud WHERE ud.UserKey = {userKey} AND ud.Interactions > 0)";

    /// <summary>The reasons an account was flagged, in words.</summary>
    public static string Why(bool guest, bool nameLike, bool noChart, bool type, bool group) =>
        string.Join(", ", new[] { guest ? "Guest" : null, nameLike ? "Name reads like an account" : null, noChart ? "Outside the org chart" : null,
                                  type ? "Employee type" : null, group ? "In a service-account group" : null }.Where(x => x is not null));

    public sealed class Breach
    {
        public string UserPrincipalName { get; set; } = "";
        public string? DisplayName { get; set; }
        public bool Guest { get; set; }
        public bool NameLike { get; set; }
        public bool NoChart { get; set; }
        public bool TypeMatch { get; set; }
        public bool GroupMatch { get; set; }
    }

    /// <summary>Every breach in the whole organization, on the latest licensed day: for the alert, the board pack and the removal files.</summary>
    public static async Task<List<Breach>> AllAsync(Db db, Settings settings, string scopeJoin = "", Dapper.DynamicParameters? scopeArgs = null)
    {
        var rules = await RulesAsync(settings);
        if (!rules.Any) return [];
        var args = scopeArgs ?? new Dapper.DynamicParameters(new { k1 = await db.ScalarAsync<int?>("SELECT MAX(DateKey) FROM bi.FactLicensedDay;") ?? 0 });
        var cte = await CteAsync(db, rules, scopeJoin, args);
        return await db.QueryAsync<Breach>($"{cte} SELECT UserPrincipalName, DisplayName, Guest, NameLike, NoChart, TypeMatch, GroupMatch FROM x WHERE Breach = 1 ORDER BY UserPrincipalName;", args);
    }
}
