using System.Text;
using CopilotInsights.Data;
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

        var t = new Table { Zone = Zone, Headers = ["Step", "Last finished (UTC)", "Age", "Rows", "Detail", "Status"], Numeric = new HashSet<int> { 3 }, Sortable = false };
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
            var pr = new Table { Zone = Zone, Headers = ["Run", "Started (UTC)", "Took", "Result", "Stopped at", "On", "Last lines"] };
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
        Card("Top of the organization", top is null ? "None found: no one person has half the directory under them, so nobody sees the whole organization by level. Set it on the Access page."
            : topName is null ? $"{E(top)} is not in the directory. Check the UPN on the Access page."
            : $"{E(topName)} ({E(top)}), {(topSet ? "set on the Access page" : "found in the org chart")}.",
            top is not null && topName is not null ? "status-ok" : "status-bad");
        Card("People placed in the org chart", $"{d.Levelled:#,0} of {d.People:#,0} directory people sit under the top. The rest see only themselves and the people under them.",
            d.People > 0 && d.Levelled >= d.People * 0.95 ? "status-ok" : "status-warn");
        var seen = d.Found + d.NotFound + d.NotLooked;
        Card("Users matched to the directory", $"{d.Found:#,0} of {seen:#,0} people in the audit data are in AD. {d.NotFound:#,0} have left; {d.NotLooked:#,0} were never looked up. Unmatched people count only in whole-organization views.",
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
                (q.Oldest is null ? "" : $" (the oldest covers {When(q.Oldest)})") + $"; {q.Resubmitted:#,0} had to be resubmitted; {q.Failed:#,0} failed. " +
                "Microsoft limits how many audit searches a tenant runs at once: many waiting, or many resubmitted, means the load is being throttled, and the next run carries on.",
                q.Failed > 0 ? "status-bad" : q.Waiting > 20 || q.Resubmitted > 10 ? "status-warn" : "status-ok");
        }
        catch (Microsoft.Data.SqlClient.SqlException) { OpsCard("Audit searches", "No search windows in this database (the CSV import path, or not installed).", "status-warn"); }
        Panels.Add(new("Certificate and audit searches", new HtmlString(ops.Append("</div>").ToString())));

        var runs = await Db.QueryAsync<(DateTime StartedUtc, DateTime EndedUtc, DateTime? SinceUtc, int People, int ClosureRows, int UserDayRows)>(
            "SELECT TOP (10) StartedUtc, EndedUtc, SinceUtc, People, ClosureRows, UserDayRows FROM dash.RefreshRun ORDER BY RunId DESC;");
        var rt = new Table { Zone = Zone, Headers = ["Started (UTC)", "Took", "From", "People", "Org pairs", "Person-days"], Numeric = new HashSet<int> { 3, 4, 5 }, EmptyText = "Never refreshed." };
        foreach (var r in runs)
            rt.Rows.Add([r.StartedUtc, $"{(r.EndedUtc - r.StartedUtc).TotalSeconds:0} s", r.SinceUtc is null ? "Every day" : r.SinceUtc.Value, r.People, r.ClosureRows, r.UserDayRows]);
        Panels.Add(new("Refresh the dashboard", new HtmlString(
            "<p class=\"sub\">The scheduled backfill refreshes the dashboard after each run. Refresh by hand after changing the directory, the top of the organization or app categories.</p>" +
            Form("refresh", "<input type=\"hidden\" name=\"days\" value=\"7\"/>" + Button("Refresh the last 7 days")) +
            Form("refresh", "<input type=\"hidden\" name=\"days\" value=\"0\"/>" + Button("Rebuild every day", "Rebuild every day of the rollups? On a large tenant this takes minutes.", quiet: true)) +
            rt.Render().Value)));

        /* 2.4.7: the report pages' queries are kept until the data changes, and built ahead for the whole organization. */
        var qc = HttpContext.RequestServices.GetRequiredService<QueryCache>();
        var warmer = HttpContext.RequestServices.GetRequiredService<PageWarmer>();
        var tenant = HttpContext.RequestServices.GetRequiredService<TenantAccessor>().Current.Name;
        var (hits, misses) = qc.Counts;
        var warm = warmer.Last.GetValueOrDefault(tenant);
        var state = !qc.Enabled ? "Off (Insights:QueryCacheMinutes is 0): every page reads the database every time."
            : warm is null ? "On. The pages have not been built ahead yet on this web server; it starts shortly after the site does."
            : warm.EndedUtc is null ? $"On. Building the pages ahead now: {warm.Pages:#,0} page views so far, started {When(warm.StartedUtc)}."
            : $"On. Built ahead {When(warm.EndedUtc)}: {warm.Pages:#,0} page views in {warm.Seconds / 60:0.#} minutes{(warm.Failed > 0 ? $", {warm.Failed:#,0} not built" : "")}.";
        Panels.Add(new("Report speed", new HtmlString($"<p>{E(state)}</p><p class=\"sub\">Since the site started on this web server: {hits:#,0} queries answered from memory, {misses:#,0} read from the database.</p>"),
            "A report page's queries are kept in memory until the data changes: a refresh, a pipeline run, a new license read, or a change to a setting or reference data. " +
            "After each change every report is built once in the background for the whole organization, at every period, so the top two levels and Organization holders get each page at once. " +
            "A manager's own part of the organization is kept from the first time someone opens it. Lists that are edited all day (flag triage, exported license actions, confirmed accounts) always read the database.", "half"));
    }

    public Task<IActionResult> OnPostRefreshAsync(int days) => AdminChangeAsync(async () =>
    {
        DateTime? since = days > 0 ? DateTime.UtcNow.Date.AddDays(-days) : null;
        await Db.ExecuteAsync("EXEC dash.usp_RefreshDashboard @SinceUtc = @since;", new { since }, timeout: 3600);
        return since is null ? "Dashboard rebuilt: every day." : $"Dashboard refreshed from {since:yyyy-MM-dd}.";
    });
}

/* ---- Reports shown ----------------------------------------------------------------------- */

/// <summary>Which reports the dashboard shows (2.4.9): laid out as the start page is, a box to tick for each.</summary>
public sealed class ReportsModel(PageServices s) : AdminPage(s)
{
    public override string Title => "Reports shown";
    public override string Intro => "Turn a report off and it is gone for everyone, administrators too: from the navigation, from the start page, and at its address. Turn it back on here at any time. Nothing is deleted.";

    protected override async Task BuildAsync()
    {
        ShowDone();
        var hidden = HiddenReports;
        var all = CopilotInsights.Pages.HiddenReports.Choices.ToList();
        var total = all.Sum(g => g.Pages.Count);
        var off = all.Sum(g => g.Pages.Count(p => CopilotInsights.Pages.HiddenReports.Is(hidden, p.Path)));
        Kpis.Add(new("Reports shown", Num(total - off), $"of {total:#,0}"));
        Kpis.Add(new("Turned off", Num(off), off == 0 ? "every report is shown" : "gone for everyone", off > 0 ? "warn" : null));

        var cards = string.Concat(all.Select(g =>
            $"<div class=\"card pick\"><h3 class=\"cat\">{E(g.Persona)}</h3><p>{E(g.Who)}</p>" +
            $"<label class=\"all\"><input type=\"checkbox\" data-all{(g.Pages.All(p => !CopilotInsights.Pages.HiddenReports.Is(hidden, p.Path)) ? " checked" : "")}/> All of {E(g.Persona)}</label><ul>" +
            string.Concat(g.Pages.Select(p =>
                $"<li><label><input type=\"checkbox\" name=\"show\" value=\"{E(p.Path)}\"{(CopilotInsights.Pages.HiddenReports.Is(hidden, p.Path) ? "" : " checked")}/> {E(p.Title)}" +
                (p.Role is null ? "" : $" <span class=\"sub\">{E(p.Role)} role</span>") + "</label></li>")) +
            "</ul></div>"));
        Panels.Add(new("Reports", new HtmlString(
            "<p class=\"sub\">A ticked report is shown to the people who may open it; its roles and levels still apply. Untick a report to turn it off for everyone.</p>" +
            Form("save", $"<div class=\"cards\">{cards}</div><p>{Button("Save")}</p>")),
            "A report that is turned off is not found at its address, and its exports and forms are refused. Links to it from other reports stop working, so turn off a detail page (Agent detail, App detail, Person record) together with the lists that open it. " +
            "Scheduled digests, extracts, the board pack and the data API are set on their own pages and are not changed here. Admin pages cannot be turned off.", "full"));
    }

    public Task<IActionResult> OnPostSaveAsync(string[] show) => AdminChangeAsync(async () =>
    {
        var shown = new HashSet<string>(show ?? [], StringComparer.OrdinalIgnoreCase);
        var pages = CopilotInsights.Pages.HiddenReports.Choices.SelectMany(g => g.Pages).ToList();
        var before = await CopilotInsights.Pages.HiddenReports.GetAsync(S.Settings);
        var by = Viewer.Upn ?? Viewer.WindowsName;
        var off = new List<string>(); var on = new List<string>();
        foreach (var p in pages)
        {
            var hide = !shown.Contains(p.Path);
            if (hide == CopilotInsights.Pages.HiddenReports.Is(before, p.Path)) continue;
            (hide ? off : on).Add(p.Title);
            var name = CopilotInsights.Pages.HiddenReports.Prefix + p.Path;
            if (hide)
                await Db.ExecuteAsync(@"
UPDATE dash.Setting SET Value = N'1', UpdatedBy = @by, UpdatedUtc = SYSUTCDATETIME() WHERE Name = @name;
IF @@ROWCOUNT = 0 INSERT INTO dash.Setting (Name, Value, Description, UpdatedBy) VALUES (@name, N'1', N'This report is turned off (Admin > Reports shown).', @by);", new { name, by });
            else
                await Db.ExecuteAsync("DELETE FROM dash.Setting WHERE Name = @name;", new { name });
        }
        S.Settings.Invalidate();
        if (off.Count == 0 && on.Count == 0) return "The reports shown are unchanged.";
        return "Reports shown changed." + (off.Count > 0 ? $" Turned off: {string.Join(", ", off)}." : "") + (on.Count > 0 ? $" Turned on: {string.Join(", ", on)}." : "");
    });
}

/* ---- Access ------------------------------------------------------------------------------ */

public sealed class AccessModel(PageServices s) : AdminPage(s)
{
    public override string Title => "Access";
    public override string Intro => "Who sees what. The dashboard can be limited to the top levels of the organization. Levels 1 and 2 below the top see the whole organization; everyone else their own part of it. Roles add whole-organization access or named detail.";

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
        var reportingZone = await S.Settings.ReportingTimeZoneAsync();
        Panels.Add(new("Settings", new HtmlString(Form("settings",
            Input("OrgTopUpn", "Top of the organization (UPN)", set.GetValueOrDefault("OrgTopUpn"), 34) +
            Input("MinGroupSize", "Minimum group size", set.GetValueOrDefault("MinGroupSize"), 4, "number") +
            Select("DefaultDays", "Default period (days)", ReportContext.DayChoices.Select(d => d.ToString()), set.GetValueOrDefault("DefaultDays")) +
            Input("InactiveDays", "Unused after (days)", set.GetValueOrDefault("InactiveDays"), 4, "number") +
            Input("LicenseMonthlyCost", "License cost a month", set.GetValueOrDefault("LicenseMonthlyCost"), 6) +
            Input("LicenseCurrency", "Currency", set.GetValueOrDefault("LicenseCurrency"), 4) +
            Input("PaygInteractionCost", "Pay-as-you-go cost an interaction", set.GetValueOrDefault("PaygInteractionCost") ?? "0.12", 6) +
            ZonePicker(set.GetValueOrDefault(Zones.Setting), reportingZone) +
            Button("Save settings"))),
            "The top is usually the CEO: they and their direct reports see everything. Leave it empty to use the one the org chart shows" +
            (set.GetValueOrDefault("OrgTopUpnDetected") is { Length: > 0 } det ? $" ({det})" : "") +
            ". Groups smaller than the minimum are folded into \"Smaller groups\" in breakdowns. The pay-as-you-go cost is what one agent or work-grounded chat interaction costs in Copilot Credits " +
            "without a license, for Licensing > Better on pay-as-you-go: 0.12 is 12 credits (a generative answer, 2, grounded in tenant data, 10) at 0.01 each. " +
            $"Time zone: every time the dashboard shows, and the hour digests, alerts and extracts go out, is in it. It is {Zones.Label(Zone)} now ({Zones.Source(set.GetValueOrDefault(Zones.Setting), reportingZone)}). Left on Default, it is the zone the data's days are cut in, which a new install takes from its server. " +
            (reportingZone is null ? "" : $"The days in the reports are cut in {reportingZone}, set when the data was installed; choosing a zone here does not move them. ") +
            "A change takes effect at once."));

        /* Who can open the dashboard (2.4.4): levels 1 to N below the top, with each choice's head count. */
        var levels = await Db.QueryAsync<(byte? Level, int N)>("SELECT Level, COUNT(*) FROM dash.Person WHERE AccountEnabled = 1 GROUP BY Level ORDER BY Level;");
        var max = Math.Clamp(int.TryParse(set.GetValueOrDefault("MaxViewerLevel"), out var mv) ? mv : 0, 0, 15);
        var deepest = Math.Min(15, Math.Max(max, levels.Where(l => l.Level is not null).Select(l => (int)l.Level!.Value).DefaultIfEmpty(0).Max()));
        var everyone = levels.Sum(l => l.N);
        var granted = await Db.QueryAsync<(string PrincipalType, int N)>(
            "SELECT PrincipalType, COUNT(DISTINCT Principal) FROM dash.RoleGrant WHERE Role <> 'Admin' GROUP BY PrincipalType;");
        int Grants(string type) => granted.FirstOrDefault(g => g.PrincipalType == type).N;
        var choices = new List<(string Value, string Label)> { ("0", $"Everyone in the directory ({everyone:#,0} people)") };
        for (var n = 1; n <= deepest; n++)
            choices.Add((n.ToString(), $"Levels 1 to {n} ({levels.Where(l => l.Level is { } v && v <= n).Sum(l => l.N):#,0} people)"));
        Panels.Add(new("Who can open the dashboard", new HtmlString(Form("limit",
            $"<label>Open to<select name=\"MaxViewerLevel\">" +
            string.Concat(choices.Select(c => $"<option value=\"{c.Value}\"{(c.Value == max.ToString() ? " selected" : "")}>{E(c.Label)}</option>")) + "</select></label>" +
            Button("Save", "Change who can open the dashboard? It applies to every page, export, the data API and the manager digests within a minute."))),
            $"Level 1 is the top of the organization, level 2 reports to them, and so on. Whatever their level, anyone granted a role other than Admin (Grant a role, below) is let in too: " +
            $"{Grants("User"):#,0} people and {Grants("Group"):#,0} groups today. Administrators outside the levels keep the Admin pages, so a wrong choice can be put right here. " +
            "Managers below the levels stop getting the manager digest. People with no level (not under the top) are left out by any limit." +
            (max > 0 ? $" Now: levels 1 to {max}." : " Now: everyone.")));

        /* Levels never on a revoke, reclaim or training list (2.4.6), with each choice's head count. */
        var keep = await CopilotInsights.Data.ProtectedLevels.LevelsAsync(S.Settings);
        var keepChoices = new List<(string Value, string Label)> { ("0", "None: anyone can be listed") };
        for (var n = 1; n <= Math.Max(deepest, keep); n++)
            keepChoices.Add((n.ToString(), $"Levels 1 to {n} ({levels.Where(l => l.Level is { } v && v <= n).Sum(l => l.N):#,0} people)"));
        Panels.Add(new("Never on revoke or train lists", new HtmlString(Form("protect",
            $"<label>Leave out<select name=\"ProtectedLevels\">" +
            string.Concat(keepChoices.Select(c => $"<option value=\"{c.Value}\"{(c.Value == keep.ToString() ? " selected" : "")}>{E(c.Label)}</option>")) + "</select></label>" +
            Button("Save", "Change who is never listed to revoke or train? It applies to every such list, its files, the counts, the digests and the data API."))),
            "People at these levels of the organization are never on Upgrade, train or revoke (train and revoke), Swap list, Unused licenses, Better on pay-as-you-go, " +
            "Fading use, Not started, Enablement's one-off and not-started people, or License optimization's counts, nor in their files, digests and the data API. " +
            "Lists of accounts nobody can use keep everyone: disabled and departed accounts, departures ahead and non-human accounts." +
            $" Now: {(keep == 0 ? "none" : $"levels 1 to {keep}")}."));

        Panels.Add(new("Grant a role", new HtmlString(Form("grant",
            Input("principal", "User (UPN) or group (DOMAIN\\Group)", null, 34) +
            Select("type", "Type", ["User", "Group"], "User") +
            "<fieldset class=\"roles\"><legend>Roles</legend>" +
            string.Concat(Roles.All.Select(r => $"<label><input type=\"checkbox\" name=\"role\" value=\"{E(r)}\"/> {E(r)}</label>")) + "</fieldset>" +
            Input("note", "Why (optional)", null, 30) + Button("Grant")) +
            "<ul class=\"sub\">" + string.Concat(Roles.All.Select(r => $"<li><b>{E(r)}</b>: {E(Roles.Describe[r])}</li>")) + "</ul>"),
            "Tick every role the person or group should have; one grant gives them all. A group grant follows AD membership at sign-in, so it is the easiest to run at scale."));

        var roleFilter = Request.Query["role"].FirstOrDefault() is { } rf && Roles.All.Contains(rf) ? rf : null;
        /* One row per person or group, with every role they hold (2.4.2); paged by principal. */
        var pg = new Paging(Request, new Dictionary<string, string>
        {
            ["principal"] = "g.Principal", ["new"] = "MAX(g.GrantedUtc) DESC, g.Principal",
            ["name"] = "MAX(p.DisplayName), g.Principal", ["type"] = "MAX(g.PrincipalType), g.Principal", ["by"] = "MAX(g.GrantedBy), g.Principal",
        }, "principal");
        var principals = await Db.QueryAsync<(string Principal, int TotalRows)>($@"
SELECT g.Principal, COUNT(*) OVER () AS TotalRows
FROM dash.RoleGrant AS g LEFT JOIN dash.Person AS p ON p.Upn = g.Principal
WHERE (@role IS NULL OR EXISTS (SELECT 1 FROM dash.RoleGrant AS r WHERE r.Principal = g.Principal AND r.Role = @role))
  AND (@q IS NULL OR g.Principal LIKE @q OR p.DisplayName LIKE @q OR g.Note LIKE @q)
GROUP BY g.Principal
{pg.OrderAndPage()};", new { role = roleFilter, q = pg.Like });
        pg.Total = principals.FirstOrDefault().TotalRows;
        var list = principals.Select(x => x.Principal).ToList();
        var grants = list.Count == 0 ? [] : await Db.QueryAsync<GrantRow>(@"
SELECT g.GrantId, g.Principal, g.PrincipalType, g.Role, g.Note, g.GrantedBy, g.GrantedUtc, p.DisplayName
FROM dash.RoleGrant AS g LEFT JOIN dash.Person AS p ON p.Upn = g.Principal
WHERE g.Principal IN @list;", new { list });
        var t = new Table
        {
            Zone = Zone, Headers = ["Principal", "Name", "Type", "Roles", "Why", "Granted by", "Last granted (UTC)"], EmptyText = "No grants yet.",
            Server = new(pg, Request, ["principal", "name", "type", null, null, "by", "new"], new HashSet<int> { 6 }),
        };
        foreach (var principal in list)
        {
            var mine = grants.Where(g => g.Principal == principal).OrderBy(g => Array.IndexOf(Roles.All, g.Role)).ToList();
            if (mine.Count == 0) continue;
            var roleCell = string.Concat(mine.Select(g =>
                $"<span class=\"grant\">{E(g.Role)}" +
                Form("revoke", $"<input type=\"hidden\" name=\"id\" value=\"{g.GrantId}\"/>" + Button("Remove", $"Remove {g.Role} from {g.Principal}?", quiet: true)) + "</span>"));
            t.Rows.Add([principal, mine[0].DisplayName, mine[0].PrincipalType, new HtmlString(roleCell),
                        string.Join("; ", mine.Select(g => g.Note).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct()),
                        string.Join(", ", mine.Select(g => g.GrantedBy).Distinct()), mine.Max(g => g.GrantedUtc)]);
        }
        var tabs = "<a href=\"" + E(Request.Path) + "\"" + (roleFilter is null ? " class=\"on\"" : "") + ">All roles</a>" +
            string.Concat(Roles.All.Select(r => $" · <a href=\"{E(Request.Path + "?role=" + r)}\"{(r == roleFilter ? " class=\"on\"" : "")}>{r}</a>"));
        Panels.Add(new("Grants", new HtmlString($"<p class=\"sub\">Role: {tabs}</p>" +
            pg.Controls(Request, new Dictionary<string, string> { ["principal"] = "Principal", ["new"] = "Newest" }).Value + t.Render().Value),
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
            var org = AccessRules.SeesWholeOrganization(p.Level, roles);
            body.Append(p.Upn is null
                ? $"<p>{E(who)} is not in the directory, so they see nothing unless granted Organization{(roles.Contains(Roles.Organization) ? " (they are)" : "")}.</p>"
                : $"<p><b>{E(p.DisplayName ?? p.Upn)}</b>, level {(p.Level?.ToString() ?? "not under the top")}, reports to {E(p.ManagerUpn ?? "nobody")}.</p>" +
                  $"<p>Sees: <b>{(org ? "the whole organization" : p.PeopleBelow > 0 ? $"themselves and the {p.PeopleBelow:#,0} people under them" : "their own use")}</b>" +
                  $"{(org && roles.Contains(Roles.Organization) && p.Level is not (1 or 2) ? " (by the Organization role)" : "")}.</p>");
            if (!AccessRules.MayUseDashboard(p.Level, roles, max))
                body.Append($"<p><b>Cannot open the dashboard</b>: it is open to levels 1 to {max}, and they hold no role directly{(roles.Contains(Roles.Admin) ? " other than Admin, which gives them the Admin pages only" : "")}. A group grant would still let them in.</p>");
            body.Append($"<p>Roles granted to the account directly: {(roles.Count == 0 ? "none" : E(string.Join(", ", roles)))}. Group grants are resolved when they sign in, so check the groups above too.</p>");
        }
        Panels.Add(new("Check someone's access", new HtmlString(body.ToString()), null, "half"));

        var lt = new Table { Headers = ["Level", "People", "Sees"], Numeric = new HashSet<int> { 1 }, Sortable = false };
        foreach (var l in levels)
            lt.Rows.Add([l.Level?.ToString() ?? "Not under the top", l.N,
                         !AccessRules.MayUseDashboard(l.Level, new HashSet<string>(), max) ? "Nothing, unless granted a role" : l.Level is 1 or 2 ? "Whole organization" : "Their own part"]);
        Panels.Add(new("People by level", lt.Render(), null, "half"));
    }

    /// <summary>The time zone box: the default first, then every zone this server knows, by its offset.</summary>
    static string ZonePicker(string? chosen, string? reporting)
    {
        var fallback = Zones.Resolve(null, reporting);
        var options = $"<option value=\"\"{(string.IsNullOrWhiteSpace(chosen) ? " selected" : "")}>Default: {E(fallback.DisplayName)}</option>" +
            string.Concat(TimeZoneInfo.GetSystemTimeZones().Select(z =>
                $"<option value=\"{E(z.Id)}\"{(string.Equals(z.Id, chosen, StringComparison.OrdinalIgnoreCase) ? " selected" : "")}>{E(z.DisplayName)}</option>"));
        return $"<label>Time zone<select name=\"TimeZone\">{options}</select></label>";
    }

    public Task<IActionResult> OnPostSettingsAsync(string? OrgTopUpn, int MinGroupSize, int DefaultDays, int InactiveDays, string? LicenseMonthlyCost, string? LicenseCurrency, string? PaygInteractionCost, string? TimeZone) => AdminChangeAsync(async () =>
    {
        TimeZone = string.IsNullOrWhiteSpace(TimeZone) ? null : TimeZone.Trim();
        if (TimeZone is not null)
            try { TimeZone = TimeZoneInfo.FindSystemTimeZoneById(TimeZone).Id; }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException) { throw new ArgumentException("the time zone is not one this server knows."); }
        OrgTopUpn = string.IsNullOrWhiteSpace(OrgTopUpn) ? null : OrgTopUpn.Trim();
        if (OrgTopUpn is not null && await Db.ScalarAsync<int>("SELECT COUNT(*) FROM dash.Person WHERE Upn = @OrgTopUpn", new { OrgTopUpn }) == 0)
            throw new ArgumentException($"{OrgTopUpn} is not in the directory.");
        if (MinGroupSize is < 1 or > 50) throw new ArgumentException("the minimum group size must be 1 to 50.");
        if (!ReportContext.DayChoices.Contains(DefaultDays)) throw new ArgumentException("the default period must be one of the periods offered.");
        if (InactiveDays is < 7 or > 180) throw new ArgumentException("unused-after must be 7 to 180 days.");
        if (!decimal.TryParse(LicenseMonthlyCost, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var cost) || cost < 0 || cost > 10000)
            throw new ArgumentException("the license cost is a number, such as 30 or 31.50.");
        if (!decimal.TryParse(string.IsNullOrWhiteSpace(PaygInteractionCost) ? "0.12" : PaygInteractionCost, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var payg) || payg < 0 || payg > 100)
            throw new ArgumentException("the pay-as-you-go cost is a number, such as 0.12.");
        LicenseCurrency = (LicenseCurrency ?? "").Trim().ToUpperInvariant();
        if (LicenseCurrency.Length is < 1 or > 8) throw new ArgumentException("the currency is a short code, such as USD or CAD.");
        var values = new Dictionary<string, string?> { ["OrgTopUpn"] = OrgTopUpn, ["MinGroupSize"] = MinGroupSize.ToString(), ["DefaultDays"] = DefaultDays.ToString(), ["InactiveDays"] = InactiveDays.ToString(),
                                                        ["LicenseMonthlyCost"] = cost.ToString(System.Globalization.CultureInfo.InvariantCulture), ["LicenseCurrency"] = LicenseCurrency,
                                                        ["PaygInteractionCost"] = payg.ToString(System.Globalization.CultureInfo.InvariantCulture), [Zones.Setting] = TimeZone };
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

    public Task<IActionResult> OnPostLimitAsync(int MaxViewerLevel) => AdminChangeAsync(async () =>
    {
        if (MaxViewerLevel is < 0 or > 15) throw new ArgumentException("choose everyone, or levels 1 to 15.");
        var before = (await S.Settings.AllAsync()).GetValueOrDefault("MaxViewerLevel") is { } b && int.TryParse(b, out var bi) ? bi : 0;
        if (before == MaxViewerLevel) return "Who can open the dashboard is unchanged.";
        await Db.ExecuteAsync(@"
UPDATE dash.Setting SET Value = @v, UpdatedBy = @by, UpdatedUtc = SYSUTCDATETIME() WHERE Name = N'MaxViewerLevel';
IF @@ROWCOUNT = 0 INSERT INTO dash.Setting (Name, Value, UpdatedBy) VALUES (N'MaxViewerLevel', @v, @by);",
            new { v = MaxViewerLevel.ToString(), by = Viewer.Upn ?? Viewer.WindowsName });
        S.Settings.Invalidate();
        static string Say(int n) => n == 0 ? "everyone in the directory" : $"levels 1 to {n}";
        return $"The dashboard was open to {Say(before)}; it is now open to {Say(MaxViewerLevel)}, and to anyone granted a role.";
    });

    public Task<IActionResult> OnPostProtectAsync(int ProtectedLevels) => AdminChangeAsync(async () =>
    {
        if (ProtectedLevels is < 0 or > 15) throw new ArgumentException("choose none, or levels 1 to 15.");
        var before = await CopilotInsights.Data.ProtectedLevels.LevelsAsync(S.Settings);
        if (before == ProtectedLevels) return "The levels left off revoke and train lists are unchanged.";
        await Db.ExecuteAsync(@"
UPDATE dash.Setting SET Value = @v, UpdatedBy = @by, UpdatedUtc = SYSUTCDATETIME() WHERE Name = N'ProtectedLevels';
IF @@ROWCOUNT = 0 INSERT INTO dash.Setting (Name, Value, UpdatedBy) VALUES (N'ProtectedLevels', @v, @by);",
            new { v = ProtectedLevels.ToString(), by = Viewer.Upn ?? Viewer.WindowsName });
        S.Settings.Invalidate();
        static string Say(int n) => n == 0 ? "nobody" : $"levels 1 to {n}";
        return $"Left off revoke and train lists: {Say(before)} before, {Say(ProtectedLevels)} now.";
    });

    public Task<IActionResult> OnPostGrantAsync(string? principal, string? type, string[]? role, string? note) => AdminChangeAsync(async () =>
    {
        principal = principal?.Trim();
        if (string.IsNullOrEmpty(principal) || principal.Length > 256) throw new ArgumentException("give a UPN or DOMAIN\\Group.");
        if (type is not ("User" or "Group")) throw new ArgumentException("choose User or Group.");
        var roles = (role ?? []).Where(r => Roles.All.Contains(r)).Distinct().ToList();
        if (roles.Count == 0) throw new ArgumentException("tick at least one role.");
        if (type == "User" && !principal.Contains('@') && !principal.Contains('\\')) throw new ArgumentException("a user is a UPN (name@domain) or DOMAIN\\name.");
        if (type == "Group" && !principal.Contains('\\')) throw new ArgumentException("a group is DOMAIN\\Group.");
        var added = new List<string>();
        foreach (var r in roles)
        {
            var n = await Db.ExecuteAsync(@"
IF NOT EXISTS (SELECT 1 FROM dash.RoleGrant WHERE Principal = @principal AND Role = @role)
    INSERT INTO dash.RoleGrant (Principal, PrincipalType, Role, Note, GrantedBy) VALUES (@principal, @type, @role, @note, @by);",
                new { principal, type, role = r, note = string.IsNullOrWhiteSpace(note) ? null : note.Trim()[..Math.Min(400, note.Trim().Length)], by = Viewer.Upn ?? Viewer.WindowsName });
            if (n > 0) added.Add(r);
        }
        var had = roles.Except(added).ToList();
        return (added.Count > 0 ? $"Granted {string.Join(", ", added)} to {type.ToLowerInvariant()} {principal}." : "")
             + (had.Count > 0 ? $"{(added.Count > 0 ? " " : "")}{principal} already had {string.Join(", ", had)}." : "");
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
    public override string Intro => "Names the audit log does not carry: friendly agent names and owners, sensitivity label names and their order, which apps count as Copilot Chat, and which accounts are not a person.";

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
            ["audit"] = "ISNULL(a.AuditName, a.AgentId), a.AgentKey", ["source"] = "a.AgentSource, ISNULL(a.AuditName, a.AgentId), a.AgentKey",
        }, "unnamed");
        var agents = await Db.QueryAsync<AgentRow>($@"
SELECT a.AgentKey, a.AgentId, a.AuditName, a.AgentSource, al.FriendlyName, al.Owner, a.FirstSeenUtc, COUNT(*) OVER () AS TotalRows
FROM dash.Agent AS a LEFT JOIN dash.AgentAlias AS al ON al.AgentKey = a.AgentKey
WHERE a.AgentSource <> N'(no agent)'
  AND (@q IS NULL OR a.AgentName LIKE @q OR a.AuditName LIKE @q OR a.AgentId LIKE @q OR al.Owner LIKE @q)
{pg.OrderAndPage()};", new { q = pg.Like });
        pg.Total = agents.FirstOrDefault()?.TotalRows ?? 0;
        var t = new Table
        {
            Headers = ["Name in the audit log", "Source", "First used", "Friendly name and owner"], EmptyText = "No agents seen yet.",
            Server = new(pg, Request, ["audit", "source", "new", null], new HashSet<int> { 2 }),
        };
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
        var lt = new Table { Headers = ["Label id", "Interactions", "Name and order"], Numeric = new HashSet<int> { 1 }, EmptyText = "No labeled content seen yet.", Sortable = false };
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

        /* Which accounts are not a person (2.4.5): a Copilot license on one is a policy breach. */
        var r = await CopilotInsights.Data.NonHuman.RulesAsync(S.Settings);
        string Box(string name, string label, bool on) => $"<label><input type=\"checkbox\" name=\"{name}\" value=\"On\"{(on ? " checked" : "")}/> {E(label)}</label>";
        var set = await S.Settings.AllAsync();
        Panels.Add(new("Non-human accounts", new HtmlString(Form("nonhuman",
            Box("guests", "Guests (an #EXT# sign-in, or Entra's user type)", r.Guests) +
            Box("names", "Sign-ins and names that read like a service, shared, test or room account (svc-, service, admin, test-, noreply, shared, room-, \"shared mailbox\")", r.Names) +
            Box("nochart", "Accounts with no manager, outside the org chart", r.NoChart) +
            Input("patterns", "More sign-in patterns (comma-separated, * for any text: bot-*, *-svc)", string.Join(", ", r.Patterns), 50) +
            Input("types", "Employee types that are not people (comma-separated: SERVICE, RESOURCE)", string.Join(", ", r.EmployeeTypes), 50) +
            Input("groups", "AD groups of service accounts (DOMAIN\\Group, comma-separated)", string.Join(", ", r.Groups), 50) +
            Select("alert", "Alert Security and Licensing when one is found", ["Yes", "No"], set.GetValueOrDefault("NonHumanAlert") == "0" ? "No" : "Yes") +
            Button("Save", "Change which accounts count as not a person? It changes every breach count, the removal files and the alerts."))),
            "A Copilot license on an account these rules match is a policy breach, on Security, Compliance and Licensing > Copilot on non-human accounts, " +
            "unless an administrator confirms the account is a person there. The groups' members are read by the next nightly directory load. " +
            "The alert goes to the Security and Licensing alert recipients (Admin > Alerts and digests), once for each account."));
    }

    public Task<IActionResult> OnPostNonHumanAsync(string? guests, string? names, string? nochart, string? patterns, string? types, string? groups, string? alert) => AdminChangeAsync(async () =>
    {
        static string? Clean(string? v, int max, Func<string, bool> ok, string what)
        {
            var list = CopilotInsights.Data.NonHuman.Split(v);
            foreach (var x in list) if (x.Length > max || !ok(x)) throw new ArgumentException($"'{x}' is not {what}.");
            var joined = string.Join(", ", list);
            if (joined.Length > 1000) throw new ArgumentException($"the {what} list is longer than 1,000 characters.");
            return joined.Length == 0 ? null : joined;
        }
        var values = new Dictionary<string, string?>
        {
            [CopilotInsights.Data.NonHuman.GuestsSetting] = guests == "On" ? "On" : "Off",
            [CopilotInsights.Data.NonHuman.NamesSetting] = names == "On" ? "On" : "Off",
            [CopilotInsights.Data.NonHuman.NoChartSetting] = nochart == "On" ? "On" : "Off",
            [CopilotInsights.Data.NonHuman.PatternsSetting] = Clean(patterns, 64, x => x.Trim('*').Length > 0, "a sign-in pattern"),
            [CopilotInsights.Data.NonHuman.TypesSetting] = Clean(types, 100, _ => true, "an employee type"),
            [CopilotInsights.Data.NonHuman.GroupsSetting] = Clean(groups, 256, x => x.Contains('\\') && !x.StartsWith('\\') && !x.EndsWith('\\'), "a DOMAIN\\Group name"),
            ["NonHumanAlert"] = alert == "No" ? "0" : "1",
        };
        return SettingWriter.Saved(await SettingWriter.SaveAsync(Db, S.Settings, Viewer.Upn ?? Viewer.WindowsName, values));
    });

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
            ["kind"] = "a.Kind, a.AtUtc DESC, a.LogId DESC", ["page"] = "a.Page, a.AtUtc DESC, a.LogId DESC", ["scope"] = "a.Scope, a.AtUtc DESC, a.LogId DESC",
            ["from"] = "a.ClientIp, a.AtUtc DESC, a.LogId DESC",
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
        var t = new Table
        {
            Zone = Zone, Headers = ["When (UTC)", "Who", "Kind", "Page", "Scope", "Detail", "From"], EmptyText = "Nothing logged in this period.",
            Server = new(pg, Request, ["new", "who", "kind", "page", "scope", null, "from"], new HashSet<int> { 0 }),
        };
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
