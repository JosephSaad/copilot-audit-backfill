using System.Net;
using CopilotInsights.Data;
using CopilotInsights.Infrastructure;
using Microsoft.AspNetCore.Html;
using Microsoft.AspNetCore.Mvc;

namespace CopilotInsights.Pages
{
    /// <summary>
    /// Copilot licenses on accounts that are not a person: a policy breach (2.4.5; a licensing clean-up in 2.4.3). The
    /// same page in Licensing, Security and Compliance, each for its own role: the accounts the rules in Admin >
    /// Reference data find (Data/NonHuman.cs), why each was flagged, the files to remove their licenses, and the accounts
    /// an administrator confirmed as people, with the reason. Disabled and deleted accounts are Licenses on disabled accounts.
    /// </summary>
    public abstract class NonHumanPage(PageServices s) : InsightsPage(s)
    {
        public override string Title => "Copilot on non-human accounts";
        public override string? OwnWindow => "today's licenses";
        public override string Intro => "Copilot licenses on accounts that are not a person: guests, service, shared, test and room accounts, and the employee types and AD groups your administrators mark as non-human. The license is for people, so each is a policy breach: remove the license, or have an administrator confirm the account is a person.";

        static readonly Dictionary<string, (string Label, string Sql)> Why = new()
        {
            ["all"] = ("Every breach", "x.Breach = 1"),
            ["guest"] = ("Guests", "x.Breach = 1 AND x.Guest = 1"),
            ["name"] = ("Name reads like an account", "x.Breach = 1 AND x.NameLike = 1"),
            ["chart"] = ("Outside the org chart", "x.Breach = 1 AND x.NoChart = 1"),
            ["type"] = ("Employee type", "x.Breach = 1 AND x.TypeMatch = 1"),
            ["group"] = ("Service-account group", "x.Breach = 1 AND x.GroupMatch = 1"),
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
            public bool TypeMatch { get; set; }
            public bool GroupMatch { get; set; }
            public DateTime? LicensedSinceUtc { get; set; }
            public DateTime? LastUsed { get; set; }
            public int TotalRows { get; set; }
        }

        sealed class Confirmation
        {
            public string UserPrincipalName { get; set; } = "";
            public string? DisplayName { get; set; }
            public string Reason { get; set; } = "";
            public string ConfirmedBy { get; set; } = "";
            public DateTime ConfirmedUtc { get; set; }
        }

        protected override async Task BuildAsync()
        {
            ShowDone();
            var rules = await NonHuman.RulesAsync(S.Settings);
            if (!rules.Any) { Notice = "Every rule for finding non-human accounts is off. An administrator sets them in Admin › Reference data."; return; }
            var why = Request.Query["why"].FirstOrDefault() is { } w && Why.ContainsKey(w) ? w : "all";
            var type = Request.Query["etype"].FirstOrDefault() is { Length: > 0 and <= 100 } ty ? ty : null;
            var pg = new Paging(Request, Sorts, "name");
            var args = Ctx.Args(new { q = pg.Like, type });
            var cte = await NonHuman.CteAsync(Db, rules, Ctx.Scope("ld.UserKey"), args);
            /* Every license holder against the rules once, into #x; the list, the counts, the employee types and the
               confirmed accounts are all read from it in the same batch (2.4.11). Each used to work the rules out
               again: four passes over every license holder on every visit, 2.5 seconds at 210,000 people. */
            /* The last day of use is looked up for the rows the list shows; sorted by it, for every row the filter leaves. */
            var filter = $@"{(type is null ? Why[why].Sql : "x.EmployeeType = @type")}
  AND (@q IS NULL OR x.DisplayName LIKE @q OR x.UserPrincipalName LIKE @q OR x.Department LIKE @q)";
            const string columns = "x.DisplayName, x.UserPrincipalName, x.Department, x.EmployeeType, x.Guest, x.NameLike, x.NoChart, x.TypeMatch, x.GroupMatch, x.LicensedSinceUtc";
            var list = pg.SortKey == "idle"
                ? $@"SELECT {columns}, dl.[Date] AS LastUsed, COUNT(*) OVER () AS TotalRows
FROM (SELECT x.*, {NonHuman.LastUsed("x.UserKey")} AS LastKey FROM #x AS x WHERE {filter}) AS x
LEFT JOIN bi.DimDate AS dl ON dl.DateKey = x.LastKey
{pg.OrderAndPage()};"
                : $@"WITH pick AS (SELECT x.*, COUNT(*) OVER () AS TotalRows FROM #x AS x WHERE {filter} {pg.OrderAndPage()})
SELECT {columns}, dl.[Date] AS LastUsed, x.TotalRows
FROM pick AS x LEFT JOIN bi.DimDate AS dl ON dl.DateKey = {NonHuman.LastUsed("x.UserKey")}
ORDER BY {pg.OrderBy};";
            var (rows, c, types, confirmed) = await Db.ReadAsync($@"{cte}
SELECT x.* INTO #x FROM x;
{list}
SELECT COUNT(CASE WHEN Breach = 1 THEN 1 END), COUNT(CASE WHEN Breach = 1 AND Guest = 1 THEN 1 END), COUNT(CASE WHEN Breach = 1 AND NameLike = 1 THEN 1 END),
       COUNT(CASE WHEN Breach = 1 AND NoChart = 1 THEN 1 END), COUNT(CASE WHEN Breach = 1 AND TypeMatch = 1 THEN 1 END),
       COUNT(CASE WHEN Breach = 1 AND GroupMatch = 1 THEN 1 END), COUNT(CASE WHEN Confirmed = 1 THEN 1 END) FROM #x;
SELECT ISNULL(x.EmployeeType, N'(not set)'), COUNT(*) FROM #x AS x GROUP BY x.EmployeeType ORDER BY COUNT(*) DESC;
SELECT x.UserPrincipalName, x.DisplayName, x.Reason, x.ConfirmedBy, x.ConfirmedUtc FROM #x AS x WHERE x.Confirmed = 1 ORDER BY x.ConfirmedUtc DESC;", args, async r =>
                ((await r.ReadAsync<Row>()).ToList(),
                 (await r.ReadAsync<(int Any, int Guest, int Name, int Chart, int Type, int Group, int Confirmed)>()).Single(),
                 (await r.ReadAsync<(string Type, int N)>()).ToList(),
                 (await r.ReadAsync<Confirmation>()).ToList()));
            pg.Total = rows.FirstOrDefault()?.TotalRows ?? 0;

            Kpis.Add(new("Policy breaches", Num(c.Any), "Copilot licenses on accounts that are not a person", c.Any > 0 ? "crit" : null));
            Kpis.Add(new("Guest accounts", Num(c.Guest), "guests holding a Copilot license"));
            Kpis.Add(new("Name reads like an account", Num(c.Name), "service, shared, test, room"));   // 2.7.4: as the filter and the Why column say it
            Kpis.Add(new("Outside the org chart", Num(c.Chart), "no manager, not under the top"));
            if (rules.EmployeeTypes.Count > 0 || rules.Groups.Count > 0) Kpis.Add(new("By employee type or group", Num(c.Type + c.Group), "the employee types and AD groups marked as not a person"));
            Kpis.Add(new("Confirmed as people", Num(c.Confirmed), "by an administrator, with a reason"));

            var admin = Viewer.IsAdmin;
            var t = new Table
            {
                Headers = admin ? ["Name", "Sign-in", "Department", "Employee type", "Why", "Licensed since", "Last used", ""] : ["Name", "Sign-in", "Department", "Employee type", "Why", "Licensed since", "Last used"],
                EmptyText = type is null ? "No Copilot license on a non-human account." : $"No license holder has employee type {type}.",
                Server = new(pg, Request, ["name", "upn", "dept", "type", null, "since", "idle", null]),
            };
            foreach (var r in rows)
            {
                object?[] cells = [r.DisplayName, r.UserPrincipalName, r.Department, r.EmployeeType, NonHuman.Why(r.Guest, r.NameLike, r.NoChart, r.TypeMatch, r.GroupMatch),
                                   r.LicensedSinceUtc?.Date, r.LastUsed is null ? "Never" : r.LastUsed.Value.Date];
                t.Rows.Add(admin && (r.Guest || r.NameLike || r.NoChart || r.TypeMatch || r.GroupMatch)
                    ? [.. cells, new HtmlString(InlineForm("confirm", Hidden("upn", r.UserPrincipalName) +
                        $"<input type=\"text\" name=\"reason\" placeholder=\"Why it is a person\" size=\"18\" maxlength=\"400\" required aria-label=\"Why {E(r.UserPrincipalName)} is a person\"/>" +
                        Button("Confirm a person", $"Confirm {r.UserPrincipalName} is a person? It stops being flagged and alerted.", quiet: true)))]
                    : cells);
            }
            CsvTable = t;
            var tabs = string.Join(" · ", Why.Where(kv => kv.Key is not ("type" or "group") || rules.EmployeeTypes.Count + rules.Groups.Count > 0).Select(kv =>
                $"<a href=\"{WebUtility.HtmlEncode(Ctx.Url(Request.Path, extra: $"why={kv.Key}"))}\"{(type is null && kv.Key == why ? " class=\"on\"" : "")}>{kv.Value.Label}</a>"));
            var exports = c.Any == 0 ? "" :
                $"<div class=\"sub\">Remove every one ({c.Any:#,0}): {LicenseActions.Button(Ctx, Request.Path, "handler=bulk", Token(), TenantQuery, "Entra bulk remove members file")} · " +
                $"{LicenseActions.Button(Ctx, Request.Path, "handler=script", Token(), TenantQuery, "PowerShell script")}, recorded for License action outcomes.</div>";
            Panels.Add(new(type is null ? "Accounts" : $"Employee type: {type}",
                new HtmlString($"<p class=\"sub\">Show: {tabs}</p>" + exports + pg.Controls(Request, SortLabels).Value + t.Render().Value),
                "Enabled accounts still in the directory that hold a Copilot license today and match a rule set in Admin › Reference data. Guest: an #EXT# sign-in, or Entra's user type. " +
                "Name reads like an account: a sign-in such as svc-, service, admin, test-, noreply, shared, room- or support, a display name such as \"shared mailbox\", or a pattern of your own. " +
                "Outside the org chart: no manager and not under the top of the organization. Employee type and group: the types and AD groups your administrators listed. " +
                "An account confirmed as a person is listed below instead.", "full", CsvUrl()));

            if (await Db.ScalarAsync<int>("SELECT CASE WHEN OBJECT_ID(N'dash.NonHumanException', N'U') IS NULL THEN 0 ELSE 1 END") == 1)
            {
                var ct = new Table
                {
                    Zone = Zone, Headers = admin ? ["Name", "Sign-in", "Why it is a person", "Confirmed by", "When (UTC)", ""] : ["Name", "Sign-in", "Why it is a person", "Confirmed by", "When (UTC)"],
                    EmptyText = "No account confirmed as a person.",
                };
                foreach (var x in confirmed)
                {
                    object?[] cells = [x.DisplayName, x.UserPrincipalName, x.Reason, x.ConfirmedBy, x.ConfirmedUtc];
                    ct.Rows.Add(admin ? [.. cells, new HtmlString(InlineForm("unconfirm", Hidden("upn", x.UserPrincipalName) +
                        Button("Flag it again", $"Flag {x.UserPrincipalName} again?", quiet: true)))] : cells);
                }
                Panels.Add(new("Confirmed as people", ct.Render(),
                    /* Full width (2.6.6): a guest's sign-in and the reason do not fit beside another table. */
                    "Accounts a rule matched that an administrator confirmed are a person, with the reason. They are not breaches and raise no alert. Each change is in the audit log.", "full"));
            }

            var tt = new Table { Headers = ["Employee type", "License holders"], Numeric = new HashSet<int> { 1 } };
            foreach (var (ty2, n) in types)
                tt.Rows.Add([ty2 == "(not set)" ? ty2 : new HtmlString($"<a href=\"{WebUtility.HtmlEncode(Ctx.Url(Request.Path, extra: $"etype={Uri.EscapeDataString(ty2)}"))}\">{WebUtility.HtmlEncode(ty2)}</a>"), n]);
            Panels.Add(new("License holders by employee type", tt.Render(),
                "Everyone holding a license today, by Entra's or AD's employee type. Open a type to list its holders; a type that marks service or resource accounts can be added to the rules in Admin › Reference data.", "half"));
        }

        /// <summary>An administrator confirms a flagged account is a person, with the reason: it is no longer a breach and raises no alert.</summary>
        public Task<IActionResult> OnPostConfirmAsync(string? upn, string? reason) => AdminChangeAsync(async () =>
        {
            upn = upn?.Trim();
            reason = reason?.Trim();
            if (string.IsNullOrEmpty(upn) || await Db.ScalarAsync<int>("SELECT COUNT(*) FROM bi.DimUser WHERE UserPrincipalName = @upn", new { upn }) == 0)
                throw new ArgumentException($"{upn} is not in the directory.");
            if (string.IsNullOrEmpty(reason) || reason.Length > 400) throw new ArgumentException("say why the account is a person (up to 400 characters).");
            await Db.ExecuteAsync(@"
UPDATE dash.NonHumanException SET Reason = @reason, ConfirmedBy = @by, ConfirmedUtc = SYSUTCDATETIME() WHERE Upn = @upn;
IF @@ROWCOUNT = 0 INSERT INTO dash.NonHumanException (Upn, Reason, ConfirmedBy) VALUES (@upn, @reason, @by);", new { upn, reason, by = Viewer.Upn ?? Viewer.WindowsName });
            return $"{upn} confirmed as a person: {reason}";
        });

        public Task<IActionResult> OnPostUnconfirmAsync(string? upn) => AdminChangeAsync(async () =>
            await Db.ExecuteAsync("DELETE FROM dash.NonHumanException WHERE Upn = @upn", new { upn }) > 0
                ? $"{upn} is flagged again if a rule matches it." : $"{upn} was not confirmed as a person.");

        /* Posted, with the anti-forgery token (2.4.12, see LicenseActions.Button). */
        public Task<IActionResult> OnPostBulkAsync() => ExportAsync(false);
        public Task<IActionResult> OnPostScriptAsync() => ExportAsync(true);

        async Task<IActionResult> ExportAsync(bool script)
        {
            if (!await PrepareAsync()) return Page();
            var upns = (await NonHuman.AllAsync(Db, S.Settings, Ctx.Scope("ld.UserKey"), Ctx.Args())).Select(b => b.UserPrincipalName).ToList();
            var skuId = (await HttpContext.RequestServices.GetRequiredService<OpsData>().PoolAsync()).FirstOrDefault(p => p.IsCopilot)?.SkuId.ToString();
            var text = script
                ? LicenseExport.Script("revoke", upns, await S.Settings.StringAsync("LicenseGroupId"), skuId, Viewer.Upn ?? Viewer.WindowsName, DateTime.UtcNow)
                : LicenseExport.BulkMembersCsv(upns);
            await LicenseActions.RecordAsync(Db, "nonhuman", upns, Viewer.Upn ?? Viewer.WindowsName);
            await S.Audit.WriteAsync(Viewer, "Export", Request.Path, Ctx.ScopeLabel, $"non-human accounts {(script ? "script" : "bulk file")}: {upns.Count} accounts");
            var name = $"copilot-non-human-{DateTime.UtcNow:yyyyMMdd}";
            return File(CopilotInsights.Data.LicenseExport.FileBytes(text, script), script ? "text/plain" : "text/csv", script ? name + ".ps1" : name + ".csv");
        }
    }
}

namespace CopilotInsights.Pages.Licensing
{
    /// <summary>
    /// The one page, for Licensing, Security and Compliance alike (Nav.NonHuman): it was a copy in each section. It names
    /// people, so it needs one of their roles; the role it asks for is the first the viewer holds.
    /// </summary>
    public sealed class NonPersonModel(PageServices s) : NonHumanPage(s)
    {
        static readonly string[] Holders = [Roles.Licensing, Roles.Security, Roles.Compliance];
        public override string Persona => RequiredRole ?? Roles.Licensing;
        /* The first role the viewer holds whose section has not turned the page off (Admin › Reports shown, Nav.Key). */
        public override string? RequiredRole =>
            Holders.FirstOrDefault(r => Viewer?.Has(r) == true && !CopilotInsights.Pages.HiddenReports.Is(HiddenReports, r == Roles.Licensing ? Nav.NonHuman : "/" + r.ToLowerInvariant() + "/non-human"))
            ?? Holders.FirstOrDefault(r => Viewer?.Has(r) == true) ?? Roles.Licensing;
    }
}
