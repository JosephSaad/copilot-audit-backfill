using System.Text;
using CopilotInsights.Infrastructure;
using Microsoft.AspNetCore.Html;
using Microsoft.AspNetCore.Mvc;

namespace CopilotInsights.Pages.Admin;

/// <summary>The admin pages: administrators only, no report filters, a message after each change.</summary>
public abstract class AdminPage(PageServices s) : InsightsPage(s)
{
    public override string Persona => "Admin";
    public override bool AdminOnly => true;
    public override bool UsesFilters => false;

}

/* ---- Health ------------------------------------------------------------------------------ */

public sealed class HealthModel(PageServices s) : AdminPage(s)
{
    public override string Title => "Health";
    public override string Intro => "Is the data fresh and complete? Each step of the pipeline, the directory behind the security model, and a manual refresh.";

    sealed record Step(string Name, DateTime? Finished, string Rows, string? Note);

    protected override async Task BuildAsync()
    {
        ShowDone();
        var steps = new List<Step>();
        async Task Try(string name, string sql, string note)
        {
            try
            {
                var r = (await Db.QueryAsync<(DateTime? At, long? Rows, string? Extra)>(sql)).FirstOrDefault();
                steps.Add(new(name, r.At, r.Rows?.ToString("#,0") ?? "—", r.Extra ?? note));
            }
            catch (Microsoft.Data.SqlClient.SqlException) { steps.Add(new(name, null, "—", "Not installed in this database")); }
        }
        await Try("Audit download (backfill)", "SELECT TOP (1) MergedUtc, RowsAdded, CONCAT(N'range to ', FORMAT(RangeEndUtc, 'yyyy-MM-dd HH:mm')) FROM audit.CopilotLoad WHERE MergedUtc IS NOT NULL ORDER BY MergedUtc DESC;", "rows added");
        await Try("CSV import", "SELECT TOP (1) MergedUtc, RowsAdded, FolderPath FROM audit.CopilotCsvImport WHERE MergedUtc IS NOT NULL ORDER BY MergedUtc DESC;", "rows added");
        await Try("Sessions", "SELECT TOP (1) RunEndUtc, CAST(SessionsWritten AS BIGINT), ReportingTimeZone FROM audit.CopilotSessionRun ORDER BY RunId DESC;", "sessions written");
        await Try("bi star schema", "SELECT TOP (1) EndedUtc, InteractionRows, CASE WHEN SinceUtc IS NULL THEN N'full rebuild' ELSE CONCAT(N'since ', FORMAT(SinceUtc, 'yyyy-MM-dd')) END FROM bi.RefreshRun ORDER BY RunId DESC;", "interactions written");
        await Try("Dashboard rollups", "SELECT TOP (1) EndedUtc, CAST(UserDayRows AS BIGINT), CONCAT(FORMAT(People, 'N0'), N' people, ', FORMAT(ClosureRows, 'N0'), N' org pairs') FROM dash.RefreshRun ORDER BY RunId DESC;", "person-days written");
        await Try("User directory", "SELECT MAX(LoadedUtc), COUNT_BIG(*), NULL FROM audit.UserDirectory;", "people");

        var t = new Table { Headers = ["Step", "Last finished (UTC)", "Age", "Rows", "Detail", "Status"], Numeric = new HashSet<int> { 3 } };
        foreach (var st in steps)
        {
            var age = st.Finished is null ? (TimeSpan?)null : DateTime.UtcNow - st.Finished.Value;
            var status = st.Finished is null ? ("status-warn", "No run") : age!.Value.TotalHours <= 36 ? ("status-ok", "Fresh") : age.Value.TotalDays <= 7 ? ("status-warn", "Stale") : ("status-bad", "Old");
            t.Rows.Add([st.Name, st.Finished, age is null ? null : age.Value.TotalHours < 48 ? $"{age.Value.TotalHours:0} h" : $"{age.Value.TotalDays:0} days",
                        st.Rows, st.Note, new HtmlString($"<span class=\"{status.Item1}\">{status.Item2}</span>")]);
        }
        Panels.Add(new("Pipeline", t.Render(), "Fresh: within 36 hours. The dashboard shows data up to the last complete day in its rollups."));

        /* The runs Invoke-CopilotPipeline.ps1 records, with the failing step and its last lines. */
        var runs0 = await HttpContext.RequestServices.GetRequiredService<CopilotInsights.Data.OpsData>().PipelineRunsAsync(10);
        if (runs0.Count > 0)
        {
            var pr = new Table { Headers = ["Run", "Started (UTC)", "Took", "Result", "Stopped at", "On", "Last lines"] };
            foreach (var r in runs0)
                pr.Rows.Add([r.Kind, r.StartedUtc, r.EndedUtc is null ? null : $"{(r.EndedUtc.Value - r.StartedUtc).TotalMinutes:0} min",
                    new HtmlString($"<span class=\"{(r.Status == "Succeeded" ? "status-ok" : r.Status == "Running" ? "status-warn" : "status-bad")}\">{E(r.Status)}</span>"),
                    r.FailedStep, r.Host, r.Detail is null ? null : new HtmlString($"<details><summary>show</summary><pre class=\"log\">{E(r.Detail)}</pre></details>")]);
            Panels.Add(new("Pipeline runs", pr.Render(), "Recorded by Invoke-CopilotPipeline.ps1 on the job server. A failed or stuck run also raises an alert (Admin > Alerts and digests)."));
        }

        var d = (await Db.QueryAsync<(int Found, int NotFound, int NotLooked, int People, int Levelled, int Enabled, int? LastKey)>(@"
SELECT (SELECT COUNT(*) FROM bi.DimUser WHERE DirectoryStatus = N'Found'),
       (SELECT COUNT(*) FROM bi.DimUser WHERE DirectoryStatus = N'NotFound'),
       (SELECT COUNT(*) FROM bi.DimUser WHERE DirectoryStatus = N'Not looked up'),
       (SELECT COUNT(*) FROM dash.Person), (SELECT COUNT(*) FROM dash.Person WHERE Level IS NOT NULL),
       (SELECT COUNT(*) FROM dash.Person WHERE AccountEnabled = 1),
       (SELECT MAX(DateKey) FROM dash.UserDay);")).Single();
        var top = Ctx.OrgTopUpn;
        var topName = top is null ? null : await Db.ScalarAsync<string>("SELECT DisplayName FROM dash.Person WHERE Upn = @top", new { top });
        var sb = new StringBuilder("<div class=\"cards\">");
        void Card(string title, string body, string cls) => sb.Append($"<div class=\"card\"><h3 class=\"{cls}\">{E(title)}</h3><p>{body}</p></div>");
        var topSet = await S.Settings.StringAsync("OrgTopUpn") is not null;
        Card("Top of the organisation", top is null ? "None found: no one person has half the directory under them, so nobody sees the whole organisation by level. Set it on the Access page."
            : topName is null ? $"{E(top)} is not in the directory. Check the UPN on the Access page."
            : $"{E(topName)} ({E(top)}), {(topSet ? "set on the Access page" : "found in the org chart")}.",
            top is not null && topName is not null ? "status-ok" : "status-bad");
        Card("People placed in the org chart", $"{d.Levelled:#,0} of {d.People:#,0} directory people sit under the top. The rest see only themselves and the people under them.",
            d.People > 0 && d.Levelled >= d.People * 0.95 ? "status-ok" : "status-warn");
        var seen = d.Found + d.NotFound + d.NotLooked;
        Card("Users matched to the directory", $"{d.Found:#,0} of {seen:#,0} people in the audit data are in AD. {d.NotFound:#,0} have left; {d.NotLooked:#,0} were never looked up. Unmatched people count only in whole-organisation views.",
            seen > 0 && d.Found >= seen * 0.95 ? "status-ok" : "status-warn");
        Card("Latest day with data", d.LastKey is null ? "No data yet." : $"{d.LastKey / 10000}-{d.LastKey / 100 % 100:00}-{d.LastKey % 100:00}", d.LastKey is null ? "status-bad" : "status-ok");
        Panels.Add(new("Directory and security model", new HtmlString(sb.Append("</div>").ToString())));

        /* The app certificate the pipeline signs in with, and the audit searches waiting at Microsoft. */
        var ops = new StringBuilder("<div class=\"cards\">");
        void OpsCard(string title, string body, string cls) => ops.Append($"<div class=\"card\"><h3 class=\"{cls}\">{E(title)}</h3><p>{body}</p></div>");
        try
        {
            var cert = (await Db.QueryAsync<(string? Thumbprint, DateTime? ExpiresUtc, string? Subject, DateTime StartedUtc)>(
                "SELECT TOP (1) CertificateThumbprint, CertificateExpiresUtc, CertificateSubject, StartedUtc FROM audit.PipelineRun WHERE CertificateExpiresUtc IS NOT NULL ORDER BY RunId DESC;")).FirstOrDefault();
            var warn = await S.Settings.IntAsync("CertificateWarnDays", 30);
            if (cert.ExpiresUtc is null)
                OpsCard("App certificate", "Not recorded yet: the pipeline records it at the start of each run (2.3.0 or later).", "status-warn");
            else
            {
                var left = (cert.ExpiresUtc.Value - DateTime.UtcNow).TotalDays;
                OpsCard("App certificate", $"{E(cert.Subject)} expires {cert.ExpiresUtc:yyyy-MM-dd}: {(left <= 0 ? "expired" : $"{Math.Floor(left):0} days left")}. Seen by the run of {cert.StartedUtc:yyyy-MM-dd HH:mm} UTC. Renew with tools\\Install-CopilotJobServer.ps1 -RegisterApp.",
                    left <= 0 ? "status-bad" : left <= warn ? "status-warn" : "status-ok");
            }
        }
        catch (Microsoft.Data.SqlClient.SqlException) { OpsCard("App certificate", "Not recorded in this database (install 2.3.0's audit schema).", "status-warn"); }
        try
        {
            var q = (await Db.QueryAsync<(int Waiting, int Failed, int Last24h, int Resubmitted, DateTime? Oldest)>(@"
WITH w AS (SELECT Status, SubmittedUtc, Submissions, StartUtc FROM audit.CopilotLoadWindow
           UNION ALL SELECT Status, SubmittedUtc, Submissions, StartUtc FROM audit.CopilotBackfillWindow)
SELECT COUNT(CASE WHEN Status IN ('Pending', 'Submitted', 'Ready', 'Downloading') THEN 1 END),
       COUNT(CASE WHEN Status = 'Failed' THEN 1 END),
       COUNT(CASE WHEN SubmittedUtc >= DATEADD(HOUR, -24, SYSUTCDATETIME()) THEN 1 END),
       COUNT(CASE WHEN Submissions > 1 THEN 1 END),
       MIN(CASE WHEN Status IN ('Pending', 'Submitted', 'Ready', 'Downloading') THEN StartUtc END)
FROM w;")).Single();
            OpsCard("Audit searches", $"{q.Last24h:#,0} submitted to Microsoft Purview in the last 24 hours; {q.Waiting:#,0} waiting or downloading" +
                (q.Oldest is null ? "" : $" (the oldest covers {q.Oldest:yyyy-MM-dd HH:mm} UTC)") + $"; {q.Resubmitted:#,0} had to be resubmitted; {q.Failed:#,0} failed. " +
                "Microsoft limits how many audit searches a tenant runs at once: many waiting, or many resubmitted, means the load is being throttled, and the next run carries on.",
                q.Failed > 0 ? "status-bad" : q.Waiting > 20 || q.Resubmitted > 10 ? "status-warn" : "status-ok");
        }
        catch (Microsoft.Data.SqlClient.SqlException) { OpsCard("Audit searches", "No search windows in this database (the CSV import path, or not installed).", "status-warn"); }
        Panels.Add(new("Certificate and audit searches", new HtmlString(ops.Append("</div>").ToString())));

        var runs = await Db.QueryAsync<(DateTime StartedUtc, DateTime EndedUtc, DateTime? SinceUtc, int People, int ClosureRows, int UserDayRows)>(
            "SELECT TOP (10) StartedUtc, EndedUtc, SinceUtc, People, ClosureRows, UserDayRows FROM dash.RefreshRun ORDER BY RunId DESC;");
        var rt = new Table { Headers = ["Started (UTC)", "Took", "From", "People", "Org pairs", "Person-days"], Numeric = new HashSet<int> { 3, 4, 5 }, EmptyText = "Never refreshed." };
        foreach (var r in runs)
            rt.Rows.Add([r.StartedUtc, $"{(r.EndedUtc - r.StartedUtc).TotalSeconds:0} s", r.SinceUtc is null ? "Every day" : r.SinceUtc.Value, r.People, r.ClosureRows, r.UserDayRows]);
        Panels.Add(new("Refresh the dashboard", new HtmlString(
            "<p class=\"sub\">The scheduled backfill refreshes the dashboard after each run. Refresh by hand after changing the directory, the top of the organisation or app categories.</p>" +
            Form("refresh", "<input type=\"hidden\" name=\"days\" value=\"7\"/>" + Button("Refresh the last 7 days")) +
            Form("refresh", "<input type=\"hidden\" name=\"days\" value=\"0\"/>" + Button("Rebuild every day", "Rebuild every day of the rollups? On a large tenant this takes minutes.", quiet: true)) +
            rt.Render().Value)));
    }

    public Task<IActionResult> OnPostRefreshAsync(int days) => AdminChangeAsync(async () =>
    {
        DateTime? since = days > 0 ? DateTime.UtcNow.Date.AddDays(-days) : null;
        await Db.ExecuteAsync("EXEC dash.usp_RefreshDashboard @SinceUtc = @since;", new { since }, timeout: 3600);
        return since is null ? "Dashboard rebuilt: every day." : $"Dashboard refreshed from {since:yyyy-MM-dd}.";
    });
}

/* ---- Access ------------------------------------------------------------------------------ */

public sealed class AccessModel(PageServices s) : AdminPage(s)
{
    public override string Title => "Access and roles";
    public override string Intro => "Who sees what. Levels 1 and 2 below the top see the whole organisation; everyone else their own part of it. Roles add whole-organisation access or named detail.";

    static readonly string[] SettingNames = ["OrgTopUpn", "MinGroupSize", "DefaultDays", "InactiveDays"];

    sealed class GrantRow
    {
        public int GrantId { get; set; }
        public string Principal { get; set; } = "";
        public string PrincipalType { get; set; } = "";
        public string Role { get; set; } = "";
        public string? Note { get; set; }
        public string GrantedBy { get; set; } = "";
        public DateTime GrantedUtc { get; set; }
        public string? DisplayName { get; set; }
        public int TotalRows { get; set; }
    }

    protected override async Task BuildAsync()
    {
        ShowDone();
        var set = await S.Settings.AllAsync();
        Panels.Add(new("Settings", new HtmlString(Form("settings",
            Input("OrgTopUpn", "Top of the organisation (UPN)", set.GetValueOrDefault("OrgTopUpn"), 34) +
            Input("MinGroupSize", "Minimum group size", set.GetValueOrDefault("MinGroupSize"), 4, "number") +
            Select("DefaultDays", "Default period (days)", ReportContext.DayChoices.Select(d => d.ToString()), set.GetValueOrDefault("DefaultDays")) +
            Input("InactiveDays", "Unused after (days)", set.GetValueOrDefault("InactiveDays"), 4, "number") +
            Input("LicenceMonthlyCost", "Licence cost a month", set.GetValueOrDefault("LicenceMonthlyCost"), 6) +
            Input("LicenceCurrency", "Currency", set.GetValueOrDefault("LicenceCurrency"), 4) +
            Button("Save settings"))),
            "The top is usually the CEO: they and their direct reports see everything. Leave it empty to use the one the org chart shows" +
            (set.GetValueOrDefault("OrgTopUpnDetected") is { Length: > 0 } det ? $" ({det})" : "") +
            ". Groups smaller than the minimum are folded into \"Smaller groups\" in breakdowns. A change takes effect at once."));

        Panels.Add(new("Grant a role", new HtmlString(Form("grant",
            Input("principal", "User (UPN) or group (DOMAIN\\Group)", null, 34) +
            Select("type", "Type", ["User", "Group"], "User") +
            Select("role", "Role", Roles.All, Roles.Organization) +
            Input("note", "Why (optional)", null, 30) + Button("Grant")) +
            "<ul class=\"sub\">" + string.Concat(Roles.All.Select(r => $"<li><b>{E(r)}</b>: {E(Roles.Describe[r])}</li>")) + "</ul>"),
            "A group grant follows AD membership at sign-in, so it is the easiest to run at scale."));

        var roleFilter = Request.Query["role"].FirstOrDefault() is { } rf && Roles.All.Contains(rf) ? rf : null;
        var pg = new Paging(Request, new Dictionary<string, string>
        {
            ["role"] = "g.Role, g.Principal", ["principal"] = "g.Principal, g.Role", ["new"] = "g.GrantedUtc DESC",
        }, "role");
        var grants = await Db.QueryAsync<GrantRow>($@"
SELECT g.GrantId, g.Principal, g.PrincipalType, g.Role, g.Note, g.GrantedBy, g.GrantedUtc, p.DisplayName, COUNT(*) OVER () AS TotalRows
FROM dash.RoleGrant AS g LEFT JOIN dash.Person AS p ON p.Upn = g.Principal
WHERE (@role IS NULL OR g.Role = @role) AND (@q IS NULL OR g.Principal LIKE @q OR p.DisplayName LIKE @q OR g.Note LIKE @q)
{pg.OrderAndPage()};", new { role = roleFilter, q = pg.Like });
        pg.Total = grants.FirstOrDefault()?.TotalRows ?? 0;
        var t = new Table { Headers = ["Principal", "Name", "Type", "Role", "Why", "Granted by", "When (UTC)", ""], EmptyText = "No grants yet." };
        foreach (var g in grants)
            t.Rows.Add([g.Principal, g.DisplayName, g.PrincipalType, g.Role, g.Note, g.GrantedBy, g.GrantedUtc,
                        new HtmlString(Form("revoke", $"<input type=\"hidden\" name=\"id\" value=\"{g.GrantId}\"/>" + Button("Remove", $"Remove {g.Role} from {g.Principal}?", quiet: true)))]);
        var tabs = "<a href=\"" + E(Request.Path) + "\"" + (roleFilter is null ? " class=\"on\"" : "") + ">All roles</a>" +
            string.Concat(Roles.All.Select(r => $" · <a href=\"{E(Request.Path + "?role=" + r)}\"{(r == roleFilter ? " class=\"on\"" : "")}>{r}</a>"));
        Panels.Add(new("Grants", new HtmlString($"<p class=\"sub\">Role: {tabs}</p>" +
            pg.Controls(Request, new Dictionary<string, string> { ["role"] = "Role", ["principal"] = "Principal", ["new"] = "Newest" }).Value + t.Render().Value),
            "Administrators named in the site's configuration (Insights:BootstrapAdmins) are not listed here and cannot be removed from this page."));

        /* Effective access for one person. */
        var who = Request.Query["who"].FirstOrDefault()?.Trim();
        var body = new StringBuilder($"<form class=\"admin\" method=\"get\" action=\"{E(Request.Path)}\">{Input("who", "UPN", who, 34)}{Button("Check")}</form>");
        if (!string.IsNullOrEmpty(who))
        {
            var p = (await Db.QueryAsync<(string Upn, string? DisplayName, byte? Level, int PeopleBelow, string? ManagerUpn)>(
                "SELECT Upn, DisplayName, Level, PeopleBelow, ManagerUpn FROM dash.Person WHERE Upn = @who;", new { who })).FirstOrDefault();
            var direct = await Db.QueryAsync<string>("SELECT Role FROM dash.RoleGrant WHERE PrincipalType = 'User' AND Principal = @who ORDER BY Role;", new { who });
            var roles = direct.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var org = AccessRules.SeesWholeOrganisation(p.Level, roles);
            body.Append(p.Upn is null
                ? $"<p>{E(who)} is not in the directory, so they see nothing unless granted Organization{(roles.Contains(Roles.Organization) ? " (they are)" : "")}.</p>"
                : $"<p><b>{E(p.DisplayName ?? p.Upn)}</b>, level {(p.Level?.ToString() ?? "not under the top")}, reports to {E(p.ManagerUpn ?? "nobody")}.</p>" +
                  $"<p>Sees: <b>{(org ? "the whole organisation" : p.PeopleBelow > 0 ? $"themselves and the {p.PeopleBelow:#,0} people under them" : "their own use")}</b>" +
                  $"{(org && roles.Contains(Roles.Organization) && p.Level is not (1 or 2) ? " (by the Organization role)" : "")}.</p>");
            body.Append($"<p>Roles granted to the account directly: {(roles.Count == 0 ? "none" : E(string.Join(", ", roles)))}. Group grants are resolved when they sign in, so check the groups above too.</p>");
        }
        Panels.Add(new("Check someone's access", new HtmlString(body.ToString()), null, "half"));

        var levels = await Db.QueryAsync<(byte? Level, int N)>("SELECT Level, COUNT(*) FROM dash.Person WHERE AccountEnabled = 1 GROUP BY Level ORDER BY Level;");
        var lt = new Table { Headers = ["Level", "People", "Sees"], Numeric = new HashSet<int> { 1 } };
        foreach (var l in levels)
            lt.Rows.Add([l.Level?.ToString() ?? "Not under the top", l.N, l.Level is 1 or 2 ? "Whole organisation" : "Their own part"]);
        Panels.Add(new("People by level", lt.Render(), null, "half"));
    }

    public Task<IActionResult> OnPostSettingsAsync(string? OrgTopUpn, int MinGroupSize, int DefaultDays, int InactiveDays, string? LicenceMonthlyCost, string? LicenceCurrency) => AdminChangeAsync(async () =>
    {
        OrgTopUpn = string.IsNullOrWhiteSpace(OrgTopUpn) ? null : OrgTopUpn.Trim();
        if (OrgTopUpn is not null && await Db.ScalarAsync<int>("SELECT COUNT(*) FROM dash.Person WHERE Upn = @OrgTopUpn", new { OrgTopUpn }) == 0)
            throw new ArgumentException($"{OrgTopUpn} is not in the directory.");
        if (MinGroupSize is < 1 or > 50) throw new ArgumentException("the minimum group size must be 1 to 50.");
        if (!ReportContext.DayChoices.Contains(DefaultDays)) throw new ArgumentException("the default period must be one of the periods offered.");
        if (InactiveDays is < 7 or > 180) throw new ArgumentException("unused-after must be 7 to 180 days.");
        if (!decimal.TryParse(LicenceMonthlyCost, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var cost) || cost < 0 || cost > 10000)
            throw new ArgumentException("the licence cost is a number, such as 30 or 31.50.");
        LicenceCurrency = (LicenceCurrency ?? "").Trim().ToUpperInvariant();
        if (LicenceCurrency.Length is < 1 or > 8) throw new ArgumentException("the currency is a short code, such as USD or CAD.");
        var values = new Dictionary<string, string?> { ["OrgTopUpn"] = OrgTopUpn, ["MinGroupSize"] = MinGroupSize.ToString(), ["DefaultDays"] = DefaultDays.ToString(), ["InactiveDays"] = InactiveDays.ToString(),
                                                        ["LicenceMonthlyCost"] = cost.ToString(System.Globalization.CultureInfo.InvariantCulture), ["LicenceCurrency"] = LicenceCurrency };
        var before = await S.Settings.AllAsync();
        var changed = values.Where(kv => before.GetValueOrDefault(kv.Key) != kv.Value).ToList();
        foreach (var kv in changed)
            await Db.ExecuteAsync(@"
UPDATE dash.Setting SET Value = @v, UpdatedBy = @by, UpdatedUtc = SYSUTCDATETIME() WHERE Name = @n;
IF @@ROWCOUNT = 0 INSERT INTO dash.Setting (Name, Value, UpdatedBy) VALUES (@n, @v, @by);", new { n = kv.Key, v = kv.Value, by = Viewer.Upn ?? Viewer.WindowsName });
        /* A new top moves everyone's level: rebuild the org chart now (and only today's rollups). */
        if (changed.Any(kv => kv.Key == "OrgTopUpn"))
            await Db.ExecuteAsync("EXEC dash.usp_RefreshDashboard @SinceUtc = @today;", new { today = DateTime.UtcNow.Date }, timeout: 3600);
        S.Settings.Invalidate();
        return changed.Count == 0 ? "Settings unchanged." :
            "Settings saved: " + string.Join("; ", changed.Select(kv => $"{kv.Key} {before.GetValueOrDefault(kv.Key) ?? "(empty)"} → {kv.Value ?? "(empty)"}")) +
            (changed.Any(kv => kv.Key == "OrgTopUpn") ? ". The org chart was rebuilt for the new top." : ".");
    });

    public Task<IActionResult> OnPostGrantAsync(string? principal, string? type, string? role, string? note) => AdminChangeAsync(async () =>
    {
        principal = principal?.Trim();
        if (string.IsNullOrEmpty(principal) || principal.Length > 256) throw new ArgumentException("give a UPN or DOMAIN\\Group.");
        if (type is not ("User" or "Group")) throw new ArgumentException("choose User or Group.");
        if (role is null || !Roles.All.Contains(role)) throw new ArgumentException("choose a role.");
        if (type == "User" && !principal.Contains('@') && !principal.Contains('\\')) throw new ArgumentException("a user is a UPN (name@domain) or DOMAIN\\name.");
        if (type == "Group" && !principal.Contains('\\')) throw new ArgumentException("a group is DOMAIN\\Group.");
        var n = await Db.ExecuteAsync(@"
IF NOT EXISTS (SELECT 1 FROM dash.RoleGrant WHERE Principal = @principal AND Role = @role)
    INSERT INTO dash.RoleGrant (Principal, PrincipalType, Role, Note, GrantedBy) VALUES (@principal, @type, @role, @note, @by);",
            new { principal, type, role, note = string.IsNullOrWhiteSpace(note) ? null : note.Trim()[..Math.Min(400, note.Trim().Length)], by = Viewer.Upn ?? Viewer.WindowsName });
        return n > 0 ? $"Granted {role} to {type.ToLowerInvariant()} {principal}." : $"{principal} already has {role}.";
    });

    public Task<IActionResult> OnPostRevokeAsync(int id) => AdminChangeAsync(async () =>
    {
        var g = (await Db.QueryAsync<(string Principal, string Role)>("DELETE FROM dash.RoleGrant OUTPUT deleted.Principal, deleted.Role WHERE GrantId = @id;", new { id })).FirstOrDefault();
        return g.Principal is null ? "That grant was already gone." : $"Removed {g.Role} from {g.Principal}.";
    });
}

/* ---- Reference data ---------------------------------------------------------------------- */

public sealed class ReferenceModel(PageServices s) : AdminPage(s)
{
    public override string Title => "Reference data";
    public override string Intro => "Names the audit log does not carry: friendly agent names and owners, sensitivity label names and their order, and which apps count as Copilot Chat.";

    sealed class AgentRow
    {
        public int AgentKey { get; set; }
        public string AgentId { get; set; } = "";
        public string? AuditName { get; set; }
        public string? AgentSource { get; set; }
        public string? FriendlyName { get; set; }
        public string? Owner { get; set; }
        public DateTime FirstSeenUtc { get; set; }
        public int TotalRows { get; set; }
    }

    protected override async Task BuildAsync()
    {
        ShowDone();
        var pg = new Paging(Request, new Dictionary<string, string>
        {
            ["new"] = "a.FirstSeenUtc DESC", ["name"] = "a.AgentName", ["unnamed"] = "CASE WHEN al.AgentKey IS NULL THEN 0 ELSE 1 END, a.FirstSeenUtc DESC",
        }, "unnamed");
        var agents = await Db.QueryAsync<AgentRow>($@"
SELECT a.AgentKey, a.AgentId, a.AuditName, a.AgentSource, al.FriendlyName, al.Owner, a.FirstSeenUtc, COUNT(*) OVER () AS TotalRows
FROM dash.Agent AS a LEFT JOIN dash.AgentAlias AS al ON al.AgentKey = a.AgentKey
WHERE a.AgentSource <> N'(no agent)'
  AND (@q IS NULL OR a.AgentName LIKE @q OR a.AuditName LIKE @q OR a.AgentId LIKE @q OR al.Owner LIKE @q)
{pg.OrderAndPage()};", new { q = pg.Like });
        pg.Total = agents.FirstOrDefault()?.TotalRows ?? 0;
        var t = new Table { Headers = ["Name in the audit log", "Source", "First used", "Friendly name and owner"], EmptyText = "No agents seen yet." };
        foreach (var a in agents)
            t.Rows.Add([a.AuditName ?? a.AgentId, a.AgentSource, a.FirstSeenUtc.Date, new HtmlString(Form("alias",
                $"<input type=\"hidden\" name=\"key\" value=\"{a.AgentKey}\"/>" + Input("name", "Friendly name", a.FriendlyName, 22) + Input("owner", "Owner", a.Owner, 22) + Button("Save")))]);
        Panels.Add(new("Agents", new HtmlString(pg.Controls(Request, new Dictionary<string, string> { ["unnamed"] = "Unnamed first", ["new"] = "Newest", ["name"] = "Name" }).Value + t.Render().Value),
            "Copilot Studio agents appear under their schema name. A friendly name shows everywhere instead; clear it to go back."));

        var labels = await Db.QueryAsync<(string LabelId, string? Name, byte? Rank, long N)>(@"
SELECT i.LabelId, sl.Name, sl.Rank, ISNULL(SUM(CAST(rd.Interactions AS BIGINT)), 0)
FROM (SELECT DISTINCT Item AS LabelId FROM dash.ResourceDay WHERE Kind = 'Label' UNION SELECT LabelId FROM dash.SensitivityLabel) AS i
LEFT JOIN dash.SensitivityLabel AS sl ON sl.LabelId = i.LabelId
LEFT JOIN dash.ResourceDay AS rd ON rd.Kind = 'Label' AND rd.Item = i.LabelId
GROUP BY i.LabelId, sl.Name, sl.Rank ORDER BY ISNULL(sl.Rank, 255), i.LabelId;");
        var lt = new Table { Headers = ["Label id", "Interactions", "Name and order"], Numeric = new HashSet<int> { 1 }, EmptyText = "No labelled content seen yet." };
        foreach (var l in labels)
            lt.Rows.Add([l.LabelId, l.N, new HtmlString(Form("label", $"<input type=\"hidden\" name=\"id\" value=\"{E(l.LabelId)}\"/>" +
                Input("name", "Name", l.Name, 22) + Input("rank", "Order (0 least)", l.Rank?.ToString(), 4, "number") + Button("Save")))]);
        Panels.Add(new("Sensitivity labels", lt.Render(),
            "The audit log records label ids only. Name them as in Purview, and order them least to most sensitive. Get-Label in Security & Compliance PowerShell lists them.", "half"));

        var apps = await Db.QueryAsync<(string App, string? Category, bool Mapped)>(@"
SELECT a.App, COALESCE(c.AppCategory, a.AppCategory), CAST(CASE WHEN c.App IS NULL THEN 0 ELSE 1 END AS BIT)
FROM bi.DimApp AS a LEFT JOIN audit.SessionAppCategory AS c ON c.App = a.App ORDER BY a.App;");
        var at = new Table { Headers = ["App", "Category"] };
        foreach (var a in apps)
            at.Rows.Add([a.App + (a.Mapped ? "" : " (default)"), new HtmlString(Form("app", $"<input type=\"hidden\" name=\"app\" value=\"{E(a.App)}\"/>" +
                Select("category", "Category", ["Copilot Chat", "In-App Copilot"], a.Category) + Button("Save")))]);
        Panels.Add(new("App categories", at.Render(),
            "Copilot Chat or Copilot inside an app. A change applies to sessions and the bi model at their next refresh, and to the dashboard after that.", "half"));
    }

    public Task<IActionResult> OnPostAliasAsync(int key, string? name, string? owner) => AdminChangeAsync(async () =>
    {
        var id = await Db.ScalarAsync<string>("SELECT AgentId FROM bi.DimAgent WHERE AgentKey = @key", new { key }) ?? throw new ArgumentException("no such agent.");
        var by = Viewer.Upn ?? Viewer.WindowsName;
        if (string.IsNullOrWhiteSpace(name))
        {
            await Db.ExecuteAsync("DELETE FROM dash.AgentAlias WHERE AgentKey = @key", new { key });
            return $"Friendly name cleared for {id}.";
        }
        name = name.Trim()[..Math.Min(200, name.Trim().Length)];
        owner = string.IsNullOrWhiteSpace(owner) ? null : owner.Trim()[..Math.Min(256, owner.Trim().Length)];
        await Db.ExecuteAsync(@"
UPDATE dash.AgentAlias SET FriendlyName = @name, Owner = @owner, UpdatedBy = @by, UpdatedUtc = SYSUTCDATETIME() WHERE AgentKey = @key;
IF @@ROWCOUNT = 0 INSERT INTO dash.AgentAlias (AgentKey, FriendlyName, Owner, UpdatedBy) VALUES (@key, @name, @owner, @by);", new { key, name, owner, by });
        return $"Agent {id} is now \"{name}\"{(owner is null ? "" : $", owned by {owner}")}.";
    });

    public Task<IActionResult> OnPostLabelAsync(string? id, string? name, int? rank) => AdminChangeAsync(async () =>
    {
        id = id?.Trim();
        if (string.IsNullOrEmpty(id) || id.Length > 64) throw new ArgumentException("no label id.");
        if (rank is < 0 or > 250) throw new ArgumentException("order must be 0 to 250.");
        var by = Viewer.Upn ?? Viewer.WindowsName;
        if (string.IsNullOrWhiteSpace(name))
        {
            await Db.ExecuteAsync("DELETE FROM dash.SensitivityLabel WHERE LabelId = @id", new { id });
            return $"Name cleared for label {id}.";
        }
        name = name.Trim()[..Math.Min(200, name.Trim().Length)];
        await Db.ExecuteAsync(@"
UPDATE dash.SensitivityLabel SET Name = @name, Rank = @rank, UpdatedBy = @by, UpdatedUtc = SYSUTCDATETIME() WHERE LabelId = @id;
IF @@ROWCOUNT = 0 INSERT INTO dash.SensitivityLabel (LabelId, Name, Rank, UpdatedBy) VALUES (@id, @name, @rank, @by);", new { id, name, rank, by });
        return $"Label {id} is now \"{name}\"{(rank is null ? "" : $", order {rank}")}.";
    });

    public Task<IActionResult> OnPostAppAsync(string? app, string? category) => AdminChangeAsync(async () =>
    {
        if (string.IsNullOrWhiteSpace(app) || await Db.ScalarAsync<int>("SELECT COUNT(*) FROM bi.DimApp WHERE App = @app", new { app }) == 0)
            throw new ArgumentException("no such app.");
        if (category is not ("Copilot Chat" or "In-App Copilot")) throw new ArgumentException("choose a category.");
        await Db.ExecuteAsync(@"
UPDATE audit.SessionAppCategory SET AppCategory = @category WHERE App = @app;
IF @@ROWCOUNT = 0 INSERT INTO audit.SessionAppCategory (App, AppCategory) VALUES (@app, @category);", new { app, category });
        return $"{app} is now {category}. It applies from the next session, bi and dashboard refresh.";
    });
}

/* ---- Audit log --------------------------------------------------------------------------- */

public sealed class AuditModel(PageServices s) : AdminPage(s)
{
    public override string Title => "Audit log";
    public override string Intro => "Every page view, refusal, export and admin change, with who, what they looked at and from where.";

    static readonly string[] Kinds = ["View", "Denied", "Export", "Api", "Admin"];

    sealed class Row
    {
        public DateTime AtUtc { get; set; }
        public string Viewer { get; set; } = "";
        public string Kind { get; set; } = "";
        public string? Page { get; set; }
        public string? Scope { get; set; }
        public string? Detail { get; set; }
        public string? ClientIp { get; set; }
        public int TotalRows { get; set; }
    }

    protected override async Task BuildAsync()
    {
        var kind = Request.Query["kind"].FirstOrDefault() is { } k && Kinds.Contains(k) ? k : null;
        var days = int.TryParse(Request.Query["d"], out var d) && ReportContext.DayChoices.Contains(d) ? d : 30;
        var pg = new Paging(Request, new Dictionary<string, string>
        {
            ["new"] = "a.AtUtc DESC, a.LogId DESC", ["old"] = "a.AtUtc, a.LogId", ["who"] = "a.Viewer, a.AtUtc DESC",
        }, "new");
        var rows = await Db.QueryAsync<Row>($@"
SELECT a.AtUtc, a.Viewer, a.Kind, a.Page, a.Scope, a.Detail, a.ClientIp, COUNT(*) OVER () AS TotalRows
FROM dash.AuditLog AS a
WHERE a.AtUtc >= DATEADD(DAY, -@days, SYSUTCDATETIME()) AND (@kind IS NULL OR a.Kind = @kind)
  AND (@q IS NULL OR a.Viewer LIKE @q OR a.Page LIKE @q OR a.Detail LIKE @q OR a.Scope LIKE @q)
{pg.OrderAndPage()};", new { days, kind, q = pg.Like });
        pg.Total = rows.FirstOrDefault()?.TotalRows ?? 0;
        var counts = await Db.QueryAsync<(string Kind, int N, int People)>(
            "SELECT Kind, COUNT(*), COUNT(DISTINCT Viewer) FROM dash.AuditLog WHERE AtUtc >= DATEADD(DAY, -@days, SYSUTCDATETIME()) GROUP BY Kind;", new { days });
        foreach (var kn in Kinds)
        {
            var c = counts.FirstOrDefault(x => x.Kind == kn);
            Kpis.Add(new(kn switch { "View" => "Page views", "Denied" => "Refused", "Export" => "Exports", _ => "Admin changes" }, Num(c.N), $"by {c.People:#,0} people",
                kn == "Denied" && c.N > 0 ? "crit" : null));
        }
        var t = new Table { Headers = ["When (UTC)", "Who", "Kind", "Page", "Scope", "Detail", "From"], EmptyText = "Nothing logged in this period." };
        foreach (var r in rows) t.Rows.Add([r.AtUtc, r.Viewer, r.Kind, r.Page, r.Scope, r.Detail, r.ClientIp]);
        CsvTable = t;
        string Link(string label, int dd, string? kk, bool on) =>
            $"<a href=\"{E(Request.Path + $"?d={dd}" + (kk is null ? "" : "&kind=" + kk))}\"{(on ? " class=\"on\"" : "")}>{label}</a>";
        var periodLinks = string.Join(" · ", ReportContext.DayChoices.Select(x => Link($"{x} days", x, kind, x == days)));
        var kindLinks = Link("All", days, null, kind is null) + string.Concat(Kinds.Select(x => " · " + Link(x, days, x, x == kind)));
        Panels.Add(new("Entries", new HtmlString($"<p class=\"sub\">Last: {periodLinks}</p><p class=\"sub\">Kind: {kindLinks}</p>" +
            pg.Controls(Request, new Dictionary<string, string> { ["new"] = "Newest", ["old"] = "Oldest", ["who"] = "Who" }).Value + t.Render().Value),
            "Viewing-as entries (development only) show both accounts.", "full", CsvUrl()));
    }
}
