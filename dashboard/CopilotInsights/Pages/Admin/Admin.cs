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
    /* An administrator names a person to grant, check or exempt: anyone in the directory can be found. */
    public override CopilotInsights.Data.LookupKind Lookup => CopilotInsights.Data.LookupKind.Directory;

    /// <summary>Whether a grant's principal is the signed-in account itself.</summary>
    protected bool IsMe(string principal) =>
        string.Equals(principal, Viewer.Upn, StringComparison.OrdinalIgnoreCase) || string.Equals(principal, Viewer.WindowsName, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// An Entra group named in the grant form, by its object ID or its exact name, looked up in Microsoft Graph so the
    /// grant holds the ID (a name can change; the ID cannot) and the name as it is today.
    /// </summary>
    protected async Task<(Guid? Id, string? Name)> EntraGroupAsync(string given)
    {
        var graph = HttpContext.RequestServices.GetRequiredService<EntraGraph>();
        if (!graph.Available)
            throw new ArgumentException("a group is DOMAIN\\Group. Entra groups need the app registration in Insights:Entra (docs/ENTRA-SIGN-IN.md).");
        if (EntraPrincipal.TryParse(given, out var parsed) || Guid.TryParse(given, out parsed))
            return (parsed, await graph.GroupNameAsync(parsed) ?? throw new ArgumentException($"no Entra group has the object ID {parsed}."));
        var found = (await graph.FindGroupsAsync(given)).Where(g => string.Equals(g.Name, given, StringComparison.OrdinalIgnoreCase)).ToList();
        return found.Count switch
        {
            0 => throw new ArgumentException($"no Entra group is named {given}. Give its exact name or its object ID."),
            1 => (found[0].Id, found[0].Name),
            _ => throw new ArgumentException($"{found.Count} Entra groups are named {given}. Give the one you mean by its object ID: {string.Join(", ", found.Select(f => f.Id))}."),
        };
    }

}

/* ---- Health ------------------------------------------------------------------------------ */

public sealed class HealthModel(PageServices s) : AdminPage(s)
{
    public override string Title => "Health";
    public override string Intro => "Is the data current, and did the last run work? The three figures answer that; below them, each step of the pipeline, the directory behind who sees what, and a manual refresh.";

    /* A step by a name that says what it is, with a line on what it does ("bi star schema" and "Dashboard rollups"
       told an administrator nothing). Optional: the way records are fetched from the audit log, which a site loaded from files may never use. */
    sealed record Step(string Name, string What, DateTime? Finished, string Rows, string? Note, bool Optional = false);
    const string NotInstalled = "Not installed in this database";

    protected override async Task BuildAsync()
    {
        ShowDone();
        var steps = new List<Step>();
        async Task Try(string name, string what, string sql, string note, bool optional = false)
        {
            try
            {
                var r = (await Db.QueryAsync<(DateTime? At, long? Rows, string? Extra)>(sql)).FirstOrDefault();
                steps.Add(new(name, what, r.At, r.Rows?.ToString("#,0") ?? "—", r.Extra ?? note, optional));
            }
            catch (Microsoft.Data.SqlClient.SqlException) { steps.Add(new(name, what, null, "—", NotInstalled, optional)); }
        }
        await Try("Audit download", "Copilot audit records fetched from Microsoft Purview", "SELECT TOP (1) MergedUtc, RowsAdded, CONCAT(N'range to ', FORMAT(RangeEndUtc, 'yyyy-MM-dd HH:mm')) FROM audit.CopilotLoad WHERE MergedUtc IS NOT NULL ORDER BY MergedUtc DESC;", "rows added", optional: true);
        await Try("Sessions", "Records grouped into sessions, for session counts and lengths", "SELECT TOP (1) RunEndUtc, CAST(SessionsWritten AS BIGINT), ReportingTimeZone FROM audit.CopilotSessionRun ORDER BY RunId DESC;", "sessions written");
        await Try("Report tables", "The tables every report and Power BI read (bi.usp_RefreshBi)", "SELECT TOP (1) EndedUtc, InteractionRows, CASE WHEN SinceUtc IS NULL THEN N'full rebuild' ELSE CONCAT(N'since ', FORMAT(SinceUtc, 'yyyy-MM-dd')) END FROM bi.RefreshRun ORDER BY RunId DESC;", "interactions written");
        await Try("Dashboard totals", "Daily totals for each person and the org chart, which these pages read (dash.usp_RefreshDashboard)", "SELECT TOP (1) EndedUtc, CAST(UserDayRows AS BIGINT), CONCAT(FORMAT(People, 'N0'), N' people, ', FORMAT(ClosureRows, 'N0'), N' org pairs') FROM dash.RefreshRun ORDER BY RunId DESC;", "person-days written");
        await Try("People directory", "People, managers and departments from Active Directory", "SELECT MAX(LoadedUtc), COUNT_BIG(*), NULL FROM audit.UserDirectory;", "people");
        /* The agent workbook, from the last load that reached the tables; a later load that failed says so. */
        await Try("Agent inventory (Excel)", "The agent workbook loaded into the agent tables (tools\\Import-AgentWorkbook.ps1)", @"
SELECT TOP (1) s.EndedUtc, CAST(s.InventoryRows AS BIGINT),
       CONCAT(N'agents; ', FORMAT(s.Matched, 'N0'), N' in the audit log; file ', ISNULL(s.Source, N'?'),
              CASE WHEN s.FileCreatedUtc IS NOT NULL THEN CONCAT(N', created ', FORMAT(s.FileCreatedUtc, 'yyyy-MM-dd HH:mm'), N' UTC') END,
              CASE WHEN f.RunId IS NOT NULL THEN CONCAT(N'. The last load failed: ', f.Message) END)
FROM dash.AgentInventoryRun AS s
OUTER APPLY (SELECT TOP (1) x.RunId, x.Message FROM dash.AgentInventoryRun AS x
             WHERE x.RunId > s.RunId AND x.Status = N'Failed' ORDER BY x.RunId DESC) AS f
WHERE s.Status = N'Succeeded' ORDER BY s.RunId DESC;", "agents loaded", optional: true);

        var t = new Table { Zone = Zone, Headers = ["Step", "Last finished (UTC)", "Age", "Rows", "Detail", "Status"], Numeric = new HashSet<int> { 3 }, Sortable = false };
        var fresh = await S.Settings.IntAsync("FreshnessHours", 36);
        var behind = new List<string>();
        var old = false;
        foreach (var st in steps)
        {
            var age = st.Finished is null ? (TimeSpan?)null : DateTime.UtcNow - st.Finished.Value;
            var status = st.Finished is null ? (st.Optional ? ("muted", "Not used") : ("status-warn", "Never ran"))
                : age!.Value.TotalHours <= fresh ? ("status-ok", "Fresh") : age.Value.TotalDays <= 7 ? ("status-warn", "Stale") : ("status-bad", "Old");
            if (status.Item2 is "Never ran" or "Stale" or "Old") behind.Add(st.Name);
            old |= status.Item2 == "Old";
            t.Rows.Add([new HtmlString($"{E(st.Name)}<span class=\"cellnote\">{E(st.What)}</span>"), st.Finished,
                        age is null ? null : age.Value.TotalHours < 48 ? $"{age.Value.TotalHours:0} h" : $"{age.Value.TotalDays:0} days",
                        st.Rows, st.Finished is null && st.Note != NotInstalled ? null : st.Note, new HtmlString($"<span class=\"{status.Item1}\">{status.Item2}</span>")]);
        }
        Panels.Add(new("Pipeline", t.Render(),
            $"Fresh: finished in the last {fresh} hours. Stale: in the last 7 days. Old: longer ago. Not used: this way of loading records has never run here."));

        /* The runs Invoke-CopilotPipeline.ps1 records, with the failing step and its last lines. */
        var runs0 = await HttpContext.RequestServices.GetRequiredService<CopilotInsights.Data.OpsData>().PipelineRunsAsync(10);
        if (runs0.Count > 0)
        {
            var pr = new Table { Zone = Zone, Headers = ["Run", "Started (UTC)", "Took", "Result", "Stopped at", "Server", "Last lines"] };
            foreach (var r in runs0)
                pr.Rows.Add([r.Kind, r.StartedUtc, r.EndedUtc is null ? null : $"{(r.EndedUtc.Value - r.StartedUtc).TotalMinutes:0} min",
                    new HtmlString($"<span class=\"{(r.Status == "Succeeded" ? "status-ok" : r.Status == "Running" ? "status-warn" : "status-bad")}\">{E(r.Status)}</span>"),
                    r.FailedStep, r.Host, r.Detail is null ? null : new HtmlString($"<details><summary>show</summary><pre class=\"log\">{E(r.Detail)}</pre></details>")]);
            Panels.Add(new("Pipeline runs", pr.Render(), "Recorded by Invoke-CopilotPipeline.ps1 on the job server. A failed or stuck run also raises an alert (Admin › Alerts and digests)."));
        }

        var d = (await Db.QueryAsync<(int Found, int NotFound, int NotLooked, int People, int Levelled, int Enabled, int? LastKey)>(@"
SELECT (SELECT COUNT(*) FROM bi.DimUser WHERE DirectoryStatus = N'Found'),
       (SELECT COUNT(*) FROM bi.DimUser WHERE DirectoryStatus = N'NotFound'),
       (SELECT COUNT(*) FROM bi.DimUser WHERE DirectoryStatus = N'Not looked up'),
       (SELECT COUNT(*) FROM dash.Person), (SELECT COUNT(*) FROM dash.Person WHERE Level IS NOT NULL),
       (SELECT COUNT(*) FROM dash.Person WHERE AccountEnabled = 1),
       (SELECT MAX(DateKey) FROM dash.UserDay);")).Single();
        /* The answer first. How current the reports are, whether the last run worked, and how many steps are behind;
           the tables below are the detail. */
        var today = CopilotInsights.Data.Zones.FromUtc(DateTime.UtcNow, Zone).Date;
        var lastDay = d.LastKey is int k ? new DateTime(k / 10000, k / 100 % 100, k % 100) : (DateTime?)null;
        var daysBack = lastDay is null ? 0 : (int)(today - lastDay.Value).TotalDays;
        Kpis.Add(new("Reports show data up to", lastDay?.ToString("d MMM yyyy") ?? "No data yet",
            lastDay is null ? "nothing has been loaded" : daysBack <= 1 ? "the last complete day" : daysBack == 2 ? "2 days ago" : $"{daysBack} days ago: newer records have not been loaded",
            lastDay is null ? "crit" : daysBack > 2 ? "warn" : null));
        var last = runs0.FirstOrDefault();
        if (last is null)
            Kpis.Add(new("Last pipeline run", "None recorded", "Invoke-CopilotPipeline.ps1 records each run; a load started another way is not listed"));
        else
        {
            var quiet = last.Status != "Running" && last.StartedUtc < DateTime.UtcNow.AddHours(-fresh);
            Kpis.Add(new("Last pipeline run", last.Status,
                $"{last.Kind}, started {When(last.StartedUtc)}" + (last.FailedStep is null ? "" : $"; stopped at {last.FailedStep}") + (quiet ? "; none since" : ""),
                last.Status is "Succeeded" or "Running" ? (quiet ? "warn" : null) : "crit"));
        }
        Kpis.Add(new("Steps behind", Num(behind.Count), behind.Count == 0 ? $"every step in use finished in the last {fresh} hours" : string.Join(", ", behind),
            behind.Count == 0 ? null : old ? "crit" : "warn"));

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
        Card("Users matched to the directory", $"{d.Found:#,0} of {seen:#,0} people in the audit data are in AD. {d.NotFound:#,0} {(d.NotFound == 1 ? "has" : "have")} left; {d.NotLooked:#,0} {(d.NotLooked == 1 ? "was" : "were")} never looked up. Unmatched people count only in whole-organization views.",
            seen > 0 && d.Found >= seen * 0.95 ? "status-ok" : "status-warn");
        Card("Latest day with data", d.LastKey is null ? "No data yet." : $"{d.LastKey / 10000}-{d.LastKey / 100 % 100:00}-{d.LastKey % 100:00}", d.LastKey is null ? "status-bad" : "status-ok");
        Panels.Add(new("Directory and security model", new HtmlString(sb.Append("</div>").ToString())));

        /* The certificates the pipeline signs in with (the audit app's and, where the agent workbook comes from SharePoint
           Online, that app's), and the audit searches waiting at Microsoft. */
        var ops = new StringBuilder("<div class=\"cards\">");
        void OpsCard(string title, string body, string cls) => ops.Append($"<div class=\"card\"><h3 class=\"{cls}\">{E(title)}</h3><p>{body}</p></div>");
        List<PipelineCertificate> certs = [];
        try
        {
            certs = await Certificates.ReadAsync(Db);
            var warn = await S.Settings.IntAsync("CertificateWarnDays", 30);
            if (certs.Count == 0)
                OpsCard("App certificates", "Not recorded yet: the pipeline records each certificate it signs in with at the start of every run.", "status-warn");
            /* The next certificate to expire is a figure at the top, beside the steps behind: it sat mid-page. */
            if (certs.Where(c => c.ExpiresUtc is not null).OrderBy(c => c.ExpiresUtc).FirstOrDefault() is { } soonest)
            {
                var left = (int)Math.Floor(Certificates.DaysLeft(soonest, DateTime.UtcNow) ?? 0);
                Kpis.Add(new("Next certificate to expire", left < 0 ? "Expired" : Count(left, "day"), $"{Certificates.Name(soonest.Purpose)}, on {When(soonest.ExpiresUtc)}",
                    left < 0 ? "crit" : left <= warn ? "warn" : null));
            }
            foreach (var c in certs)
            {
                var (text, cls) = Certificates.Status(c, warn, DateTime.UtcNow);
                OpsCard(Certificates.Name(c.Purpose), $"{E(text)}{(string.IsNullOrWhiteSpace(c.Subject) ? "" : $" Subject: {E(c.Subject)}.")}{(c.SeenUtc is null ? "" : $" Checked by the run of {E(When(c.SeenUtc))}.")} Its details are in the table below.", cls);
            }
        }
        catch (Microsoft.Data.SqlClient.SqlException) { OpsCard("App certificates", "Not recorded in this database (run Install-CopilotAudit.ps1 from the current release).", "status-warn"); }
        try
        {
            /* The searches of a load are rows of audit.CopilotLoadWindow only while it runs: the merge empties it. So the
               card says what is in the window tables now, then what the last merged load took (audit.CopilotLoad), which
               the merge keeps with it; a database from before that has the load's records and not its searches. */
            var q = (await Db.QueryAsync<(int Submitted, int Waiting, int Failed, int Resubmitted, DateTime? Oldest)>(@"
WITH w AS (SELECT 1 AS OfLoad, Status, Submissions, StartUtc FROM audit.CopilotLoadWindow
           UNION ALL SELECT 0, Status, Submissions, StartUtc FROM audit.CopilotBackfillWindow)
SELECT COUNT(CASE WHEN OfLoad = 1 AND Submissions > 0 THEN 1 END),
       COUNT(CASE WHEN Status IN ('Pending', 'Submitted', 'Ready', 'Downloading') THEN 1 END),
       COUNT(CASE WHEN Status = 'Failed' THEN 1 END),
       COUNT(CASE WHEN Submissions > 1 THEN 1 END),
       MIN(CASE WHEN Status IN ('Pending', 'Submitted', 'Ready', 'Downloading') THEN StartUtc END)
FROM w;")).Single();
            var kept = await Db.ScalarAsync<int>("SELECT CASE WHEN COL_LENGTH(N'audit.CopilotLoad', N'Searches') IS NULL THEN 0 ELSE 1 END") == 1;
            var l = (await Db.QueryAsync<(DateTime? MergedUtc, long? Staged, long? Added, int? Searches, int? Resubmitted)>(
                $"SELECT TOP (1) MergedUtc, RowsStaged, RowsAdded, {(kept ? "Searches, SearchesResubmitted" : "CAST(NULL AS INT), CAST(NULL AS INT)")} FROM audit.CopilotLoad WHERE MergedUtc IS NOT NULL ORDER BY MergedUtc DESC;")).FirstOrDefault();
            var (text, cls) = AuditSearches.Describe(
                new(q.Submitted, q.Waiting, q.Failed, q.Resubmitted, q.Oldest is null ? null : When(q.Oldest)),
                l.MergedUtc is null ? null : new(When(l.MergedUtc), l.Staged ?? 0, l.Added ?? 0, l.Searches, l.Resubmitted));
            OpsCard("Audit searches", E(text), cls);
        }
        catch (Microsoft.Data.SqlClient.SqlException) { OpsCard("Audit searches", "No search windows in this database (records were not fetched from the audit log here, or this is not installed).", "status-warn"); }
        Panels.Add(new("Certificates and audit searches", new HtmlString(ops.Append("</div>").ToString()),
            "The pipeline signs in with one certificate to read the audit log from Microsoft Purview, and with a second to fetch the agent workbook from SharePoint Online, where one is set up; the Foundry usage load can have one of its own. Each expires on its own date, and an alert goes out before any does (Admin › Alerts and digests)."));
        if (certs.Count > 0)
        {
            /* Every detail the job server recorded, a certificate to a column, so two read side by side. */
            var fields = certs.Select(c => Certificates.Details(c, d => When(d))).ToList();
            var ct = new Table { Headers = ["Detail", .. certs.Select(c => Certificates.Name(c.Purpose))], Sortable = false };
            for (var i = 0; i < fields[0].Count; i++)
                if (fields.Any(f => !string.IsNullOrWhiteSpace(f[i].Value)))
                    ct.Rows.Add([fields[0][i].Field, .. fields.Select(f => (object?)f[i].Value)]);
            Panels.Add(new("Certificate details", ct.Render(),
                "As the job server held each certificate at the start of its last run. A certificate the settings name that the job server does not hold is shown as not found."));
        }

        /* AI usage outside Microsoft 365 Copilot: the last run of each optional loader. */
        try
        {
            var loaded = await ExternalUsage.ReadAsync(Db);
            var ext = new StringBuilder("<div class=\"cards\">");
            foreach (var source in new[] { ExternalUsage.GitHub, ExternalUsage.Foundry })
            {
                var (text, cls) = ExternalUsage.Status(source, loaded.FirstOrDefault(r => r.Source == source), DateTime.UtcNow, d => When(d));
                ext.Append($"<div class=\"card\"><h3 class=\"{cls}\">{E(ExternalUsage.Name(source))}</h3><p>{E(text)}</p></div>");
            }
            Panels.Add(new("Other AI usage: GitHub Copilot and Foundry", new HtmlString(ext.Append("</div>").ToString()),
                "Optional loads of usage and cost from GitHub Copilot and from Foundry and Azure OpenAI, each run recorded with what it loaded. Leadership › Costs and savings and Governance › AI apps beyond Copilot read them."));
        }
        catch (Microsoft.Data.SqlClient.SqlException) { /* A site account not yet granted the table (the installer grants it): the panel is left out. */ }

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

        /* The report pages' queries are kept until the data changes, and built ahead for the whole organization. */
        var qc = HttpContext.RequestServices.GetRequiredService<QueryCache>();
        var warmer = HttpContext.RequestServices.GetRequiredService<PageWarmer>();
        var tenant = HttpContext.RequestServices.GetRequiredService<TenantAccessor>().Current.Name;
        var (hits, misses) = qc.Counts;
        var warm = warmer.Last.GetValueOrDefault(tenant);
        var state = !qc.Enabled ? "Off (Insights:QueryCacheMinutes is 0): every page reads the database every time."
            : warm is null ? "On. The pages have not been built ahead yet on this web server; it starts shortly after the site does."
            : warm.EndedUtc is null ? $"On. Building the pages ahead now: {warm.Pages:#,0} page views so far, started {When(warm.StartedUtc)}."
            : $"On. Built ahead {When(warm.EndedUtc)}: {warm.Pages:#,0} page views in {warm.Seconds / 60:0.#} minutes{(warm.Failed > 0 ? $", {warm.Failed:#,0} not built" : "")}.";
        /* The daily pre-cache, which Invoke-InsightsPrecache.ps1 asks for. */
        var pre = warmer.LastPrecache;
        var slowest = pre?.Tenants.Select(x => x.SlowestFromMemorySeconds).DefaultIfEmpty(0).Max() ?? 0;
        var over = pre?.Tenants.Sum(x => x.OverOneSecond) ?? 0;
        var precache = pre is null ? "No pre-cache on this web server since the site started (Invoke-InsightsPrecache.ps1; the installer schedules it daily)."
            : pre.EndedUtc is null ? $"Pre-cache asked for {When(pre.RequestedUtc ?? pre.StartedUtc)}: building every page again now."
            : $"Last pre-cache {When(pre.EndedUtc)}: {pre.Outcome}, {pre.Tenants.Sum(x => x.Pages):#,0} page views built again; from memory the slowest took {slowest:0.00} s{(over > 0 ? $", {over:#,0} over a second" : "")}.";
        /* The results the web servers share, in the database. */
        var shared = HttpContext.RequestServices.GetRequiredService<SharedCache>();
        var (sHits, sStored, _) = shared.Counts;
        var size = shared.Enabled ? await shared.SizeAsync() : null;
        var sharing = !shared.Enabled ? "Shared with the other web servers: off on this one (Insights:SharedQueryCache)."
            : size is not { } sz ? "Shared with the other web servers: not yet; Install-CopilotAudit.ps1 -Dashboard adds the table it needs (dash.QueryCacheShared)."
            : $"Shared with the other web servers, in the database: {sz.Results:#,0} results, {sz.Bytes / 1048576.0:#,0.#} MB. Since the site started, this one read {sHits:#,0} from there and kept {sStored:#,0}.";
        Panels.Add(new("Report speed", new HtmlString($"<p>{E(state)}</p><p>{E(precache)}</p><p>{E(sharing)}</p><p class=\"sub\">Since the site started on this web server: {hits:#,0} queries answered from memory, {misses:#,0} read from the database.</p>"),
            "A report page's queries are kept in memory until the data changes: a refresh, a finished pipeline run, a license or directory read, or a change to a setting or reference data. " +
            "After each change, and again when the results expire, every report is built once in the background for the whole organization, at every period, so the top two levels and Organization holders get each page at once. " +
            "A manager's own part of the organization is kept from the first time someone opens it. A list edited all day (flag triage, exported license actions, confirmed accounts) has a version of its own, " +
            "read from the database for every page: a page that reads one is kept until that list changes, on this web server or another. With several web servers, each keeps results in its own memory and shares them " +
            "through the database, and the morning build runs on one at a time: the others read what it kept.", "half"));
    }

    public Task<IActionResult> OnPostRefreshAsync(int days) => AdminChangeAsync(false, async () =>
    {
        DateTime? since = days > 0 ? DateTime.UtcNow.Date.AddDays(-days) : null;
        await Db.ExecuteAsync("EXEC dash.usp_RefreshDashboard @SinceUtc = @since;", new { since }, timeout: 3600);
        return since is null ? "Dashboard rebuilt: every day." : $"Dashboard refreshed from {since:yyyy-MM-dd}.";
    });
}

/* ---- Reports shown ----------------------------------------------------------------------- */

/// <summary>Which reports the dashboard shows: laid out as the start page is, a box to tick for each.</summary>
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
IF @@ROWCOUNT = 0 INSERT INTO dash.Setting (Name, Value, Description, UpdatedBy) VALUES (@name, N'1', N'This report is turned off (Admin › Reports shown).', @by);", new { name, by });
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
        public string? Pages { get; set; }
        public bool OrgWide { get; set; }
        public string? PrincipalName { get; set; }
        public int TotalRows { get; set; }
    }

    /// <summary>Whether dash.RoleGrant has the Pages column that limits an Admin grant to some pages (added by the schema script).</summary>
    async Task<bool> HasPagesAsync() => await Db.ScalarAsync<int>("SELECT CASE WHEN COL_LENGTH(N'dash.RoleGrant', N'Pages') IS NULL THEN 0 ELSE 1 END;") == 1;


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
            Input("LicenseMonthlyCost", "Copilot license cost a month, after discount", set.GetValueOrDefault("LicenseMonthlyCost"), 6) +
            Input("LicenseCurrency", "Currency", set.GetValueOrDefault("LicenseCurrency"), 4) +
            Input("PaygInteractionCost", $"Pay-as-you-go cost an interaction ({set.GetValueOrDefault("LicenseCurrency") ?? "USD"})", set.GetValueOrDefault("PaygInteractionCost") ?? "0.12", 6) +
            ZonePicker(set.GetValueOrDefault(Zones.Setting), reportingZone) +
            Button("Save settings")) +
            /* What each setting does, one line a setting (it was one paragraph for all of them). */
            "<ul class=\"tips sub\">" +
            $"<li><b>Top of the organization.</b> Usually the CEO: they and their direct reports see everything. Leave it empty to use the one the org chart shows{(set.GetValueOrDefault("OrgTopUpnDetected") is { Length: > 0 } det ? $" ({E(det)})" : "")}.</li>" +
            "<li><b>Minimum group size.</b> A group smaller than this is folded into \"Smaller groups\" wherever reports break figures down, and what is folded reaches it too, so no figure describes one or two people. It is also the smallest part of the organization a report can be narrowed to.</li>" +
            "<li><b>Default period.</b> The period every report opens with.</li>" +
            "<li><b>Unused after.</b> The days without any use after which a license counts as unused.</li>" +
            "<li><b>License cost a month, and currency.</b> Used wherever a cost or a saving is shown.</li>" +
            "<li><b>Pay-as-you-go cost an interaction.</b> What one agent or work-grounded chat interaction costs in Copilot Credits without a license, for Licensing &gt; Better on pay-as-you-go: 0.12 is 12 credits (a generative answer, 2, and tenant data, 10) at 0.01 each.</li>" +
            $"<li><b>Time zone.</b> Every time the dashboard shows, and the hour digests, alerts and extracts go out, is in it. It is {E(Zones.Label(Zone))} now ({E(Zones.Source(set.GetValueOrDefault(Zones.Setting), reportingZone))}). " +
            "Left on Default, it is the zone the data's days are cut in, which a new install takes from its server. " +
            (reportingZone is null ? "" : $"The days in the reports are cut in {E(reportingZone)}, set when the data was installed; choosing a zone here does not move them.") + "</li>" +
            "</ul>"),
            "A change takes effect at once, and is recorded in the audit log."));

        /* Who can open the dashboard: levels 1 to N below the top, with each choice's head count. */
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
            Button("Save", "Change who can open the dashboard? It applies from the next page, export and data API call, on every web server, and to the manager digests within a minute."))),
            $"Level 1 is the top of the organization, level 2 reports to them, and so on. Whatever their level, anyone granted a role other than Admin (Grant a role, below) is let in too: " +
            $"{Grants("User"):#,0} people and {Grants("Group"):#,0} groups today. Administrators outside the levels keep the Admin pages (one limited to some pages, those), so a wrong choice can be put right here. " +
            "Managers below the levels stop getting the manager digest. People with no level (not under the top) are left out by any limit." +
            (max > 0 ? $" Now: levels 1 to {max}." : " Now: everyone.")));

        /* Levels never on a revoke, reclaim or training list, with each choice's head count. */
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

        /* Admin can be limited to some of the Admin pages. Only a full administrator gives, limits or removes
           Admin, so someone limited to this page cannot make themselves, or anyone, more than they are. */
        var hasPages = await HasPagesAsync();
        /* The Workbench's roles only where the Workbench is set up. */
        var workbenchOn = HttpContext.RequestServices.GetRequiredService<global::Governance.Web.Infrastructure.WorkbenchState>().On;
        var offered = Roles.All.Where(r => (Viewer.IsAdmin || r != Roles.Admin) && (workbenchOn || !Roles.IsWorkbench(r))).ToList();
        var pagePicker = !Viewer.IsAdmin || !hasPages ? "" :
            "<fieldset class=\"roles\"><legend>Admin pages (none ticked: every page)</legend>" +
            string.Concat(AdminPages.Choices.Select(ap => $"<label><input type=\"checkbox\" name=\"adminPage\" value=\"{E(ap.Path)}\"/> {E(ap.Title)}</label>")) + "</fieldset>";
        var has28 = await Has28Async();
        var entraGroups = HttpContext.RequestServices.GetRequiredService<EntraGraph>().Available && has28;
        Panels.Add(new("Grant a role", new HtmlString(Form("grant",
            PersonBox("principal", "User (name or UPN) or group (DOMAIN\\Group)", null, 34) +
            Select("type", "Type", ["User", "Group"], "User") +
            (entraGroups ? EntraGroupBox() : "") +
            "<fieldset class=\"roles\"><legend>Roles</legend>" +
            string.Concat(offered.Select(r => $"<label><input type=\"checkbox\" name=\"role\" value=\"{E(r)}\"/> {E(r)}</label>")) + "</fieldset>" +
            (has28 ? $"<label class=\"check\"><input type=\"checkbox\" name=\"orgWide\" value=\"true\"/> Across the whole organization{FieldTip("orgWide", "Across the whole organization")}</label>" : "") +
            pagePicker +
            Input("note", "Why (optional)", null, 30) + Button("Grant")) +
            "<ul class=\"sub\">" + string.Concat(offered.Select(r => $"<li><b>{E(r)}</b>: {E(Roles.Describe[r])}</li>")) + "</ul>"),
            "Tick every role the person or group should have; one grant gives them all. A group grant follows AD membership at sign-in, so it is the easiest to run at scale. " +
            "To give the same bundle to many people, make it an access profile (Admin › Access profiles) and grant that to a group." +
            (has28 ? " Across the whole organization: the persona roles ticked (Adoption, Licensing, Security, Compliance, Agents, Enablement) show their own pages for the whole organization, " +
                     "where otherwise a holder sees their own part of it. It widens nothing else; the minimum group size and privacy mode still apply. Granting a role held already sets this box as ticked now." : "") +
            (entraGroups ? " An Entra group is checked through Microsoft Graph at sign-in, nested groups included, and is kept by its object ID, so renaming it changes nothing." : "") +
            (pagePicker != "" ? " With Admin, tick the Admin pages to limit it to those: the person sees and changes only them. With none ticked, Admin is every page. " +
                                "Granting Admin again to someone who holds it replaces their pages." : "") +
            (Viewer.IsAdmin && !hasPages ? " To limit Admin to some pages, run Create-CopilotDashboardSchema.sql again: this database is from before 2.6.4." : "") +
            (Viewer.IsAdmin ? "" : " Admin is given, limited and removed by a full administrator.")));

        var roleFilter = Request.Query["role"].FirstOrDefault() is { } rf && Roles.All.Contains(rf) ? rf : null;
        /* One row per person or group, with every role they hold; paged by principal. */
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
SELECT g.GrantId, g.Principal, g.PrincipalType, g.Role, g.Note, g.GrantedBy, g.GrantedUtc, COALESCE(p.DisplayName" + (has28 ? ", g.PrincipalName" : "") + @") AS DisplayName" + (hasPages ? ", g.Pages" : "") + (has28 ? ", g.OrgWide, g.PrincipalName" : "") + @"
FROM dash.RoleGrant AS g LEFT JOIN dash.Person AS p ON p.Upn = g.Principal
WHERE g.Principal IN @list;", new { list });
        var t = new Table
        {
            Zone = Zone, Headers = ["Select", "Principal", "Name", "Type", "Roles", "Why", "Granted by", "Last granted (UTC)"], EmptyText = "No grants yet.",
            Server = new(pg, Request, [null, "principal", "name", "type", null, null, "by", "new"], new HashSet<int> { 7 }),
        };
        /* Every role of one person or group at once (Remove all, on their row), and of everyone ticked (below
           the table). The tick boxes belong to that one form by its id: a row's own forms cannot hold them. */
        const string many = "revoke-many";
        foreach (var principal in list)
        {
            var mine = grants.Where(g => g.Principal == principal).OrderBy(g => Array.IndexOf(Roles.All, g.Role)).ToList();
            if (mine.Count == 0) continue;
            var roleCell = string.Concat(mine.Select(g =>
                $"<span class=\"grant\">{E(g.Role)}" + (g.OrgWide ? " <small>(whole organization)</small>" : "") +
                (g.Role == Roles.Admin && AdminPages.Limits(g.Pages) ? $" <small>({E(AdminPages.Names(g.Pages))})</small>" : "") +
                (g.Role == Roles.Admin && !Viewer.IsAdmin ? "" :
                    Form("revoke", $"<input type=\"hidden\" name=\"id\" value=\"{g.GrantId}\"/>" + RemoveBox($"Remove {g.Role} from {g.Principal}"))) + "</span>")) +
                "<span class=\"grant all\">" + Form("revokeall", Hidden("principal", principal) +
                    $"<button class=\"btn quiet\" type=\"submit\" title=\"{E($"Remove every role from {principal}")}\" data-confirm=\"{E($"Remove every role from {principal}? They keep only what their level and their groups give them.")}\">Remove all</button>") + "</span>";
            var tick = $"<input type=\"checkbox\" name=\"principal\" value=\"{E(principal)}\" form=\"{many}\" aria-label=\"{E($"Select {principal}")}\" title=\"{E($"Select {principal}, to remove every role from several at once")}\"/>";
            t.Rows.Add([new HtmlString(tick), principal, mine[0].DisplayName, mine[0].PrincipalType, new HtmlString(roleCell),
                        string.Join("; ", mine.Select(g => g.Note).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct()),
                        string.Join(", ", mine.Select(g => g.GrantedBy).Distinct()), mine.Max(g => g.GrantedUtc)]);
        }
        var tabs = "<a href=\"" + E(Request.Path) + "\"" + (roleFilter is null ? " class=\"on\"" : "") + ">All roles</a>" +
            string.Concat(Roles.All.Select(r => $" · <a href=\"{E(Request.Path + "?role=" + r)}\"{(r == roleFilter ? " class=\"on\"" : "")}>{r}</a>"));
        Panels.Add(new("Grants", new HtmlString($"<p class=\"sub\">Role: {tabs}</p>" +
            pg.Controls(Request, new Dictionary<string, string> { ["principal"] = "Principal", ["new"] = "Newest" }).Value + t.Render().Value +
            (list.Count == 0 ? "" :
                $"<form id=\"{many}\" class=\"admin\" method=\"post\" action=\"{E(Request.Path + "?handler=revokemany" + TenantQuery)}\">{Token()}" +
                $"<label class=\"check\" title=\"Tick or clear every person and group on this page\"><input type=\"checkbox\" data-check-all=\"{many}\"/> Select all on this page</label>" +
                Button("Remove every role from the selected", "Remove every role from each person and group ticked? They keep only what their level and their groups give them.") + "</form>")),
            "The x beside a role removes that role. Remove all removes every role the person or group holds. To clear several at once, tick them and use the button under the table. " +
            "Each removal is recorded in the audit log and takes effect from the person's next page. " +
            "An Admin role limited to some pages lists them in brackets; a full administrator changes them by granting Admin again. " +
            "Administrators named in the site's configuration (Insights:BootstrapAdmins) are not listed here and cannot be removed from this page."));

        /* Effective access for one person. */
        var who = Request.Query["who"].FirstOrDefault()?.Trim();
        var body = new StringBuilder($"<form class=\"admin\" method=\"get\" action=\"{E(Request.Path)}\">{PersonBox("who", "Person (name or UPN)", who, 34)}{Button("Check")}</form>");
        if (!string.IsNullOrEmpty(who))
        {
            var p = (await Db.QueryAsync<(string Upn, string? DisplayName, byte? Level, int PeopleBelow, string? ManagerUpn)>(
                "SELECT Upn, DisplayName, Level, PeopleBelow, ManagerUpn FROM dash.Person WHERE Upn = @who;", new { who })).FirstOrDefault();
            var upn = p.Upn ?? who;
            /* Every grant as sign-in reads it: role by role, and each access profile's roles. */
            var all = await ViewerService.GrantsAsync(Db);
            var profileNames = (await Db.QueryAsync<(int Id, string Name)>(
                "IF OBJECT_ID(N'dash.AccessProfile', N'U') IS NOT NULL SELECT ProfileId, Name FROM dash.AccessProfile;")).ToDictionary(x => x.Id, x => x.Name);
            var groupNames = !has28 ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) : (await Db.QueryAsync<(string Principal, string Name)>(@"
SELECT Principal, MAX(PrincipalName) FROM (SELECT Principal, PrincipalName FROM dash.RoleGrant
    UNION ALL SELECT Principal, PrincipalName FROM dash.ProfileGrant) AS x WHERE PrincipalName IS NOT NULL GROUP BY Principal;"))
                .ToDictionary(x => x.Principal, x => x.Name, StringComparer.OrdinalIgnoreCase);
            string Named(string principal) => groupNames.TryGetValue(principal, out var n) ? $"{n} (Entra group)" : principal;

            /* AD groups: the members the nightly directory load records for every group named in a grant. The dashboard
               itself asks Windows at sign-in, so this is as of that load. */
            var membersLoaded = await Db.ScalarAsync<int>("SELECT CASE WHEN OBJECT_ID(N'audit.GroupMember', N'U') IS NULL THEN 0 ELSE 1 END") == 1;
            var adGroups = !membersLoaded ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                : (await Db.QueryAsync<string>("SELECT GroupName FROM audit.GroupMember WHERE UserPrincipalName = @upn;", new { upn })).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var loadedUtc = !membersLoaded ? null : await Db.ScalarAsync<DateTime?>("SELECT MAX(LoadedUtc) FROM audit.GroupMember;");
            var unexpanded = !membersLoaded ? [] : all.Where(g => g.PrincipalType == "Group" && g.Principal.Contains('\\')).Select(g => g.Principal)
                .Distinct(StringComparer.OrdinalIgnoreCase).Except(await Db.QueryAsync<string>("SELECT DISTINCT GroupName FROM audit.GroupMember;"), StringComparer.OrdinalIgnoreCase).Order().ToList();

            /* Entra groups: asked of Microsoft Graph now, by the person's Entra object ID, as sign-in does. */
            var graph = HttpContext.RequestServices.GetRequiredService<EntraGraph>();
            var entraIds = all.Select(g => EntraPrincipal.TryParse(g.Principal, out var id) ? id : Guid.Empty).Where(id => id != Guid.Empty).Distinct().ToList();
            var personOid = entraIds.Count == 0 || !has28 ? null : await Db.ScalarAsync<Guid?>("SELECT TOP (1) ObjectId FROM dash.Person WHERE Upn = @upn;", new { upn });
            var inEntra = personOid is { } poid && graph.Available ? await graph.MemberOfAsync(poid, entraIds) : new HashSet<Guid>();

            /* Each grant that reaches them, and how: "" by their own name, else the group it comes through. */
            string? Through(ViewerService.GrantRow g) =>
                string.Equals(g.Principal, who, StringComparison.OrdinalIgnoreCase) || string.Equals(g.Principal, upn, StringComparison.OrdinalIgnoreCase) ? ""
                : adGroups.Contains(g.Principal) ? g.Principal
                : EntraPrincipal.TryParse(g.Principal, out var gid) && inEntra.Contains(gid) ? g.Principal : null;
            var held = all.Select(g => (Grant: g, Via: Through(g))).Where(x => x.Via is not null).ToList();
            string RoleText(ViewerService.GrantRow g) =>
                (g.Role == Roles.Admin && AdminPages.Limits(g.Pages) ? $"Admin ({AdminPages.Names(g.Pages)})" : g.Role) + (g.OrgWide && AccessRules.IsPersona(g.Role) ? " across the organization" : "");

            var roles = held.Select(x => x.Grant.Role).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var wideRoles = held.Where(x => x.Grant.OrgWide && AccessRules.IsPersona(x.Grant.Role)).Select(x => x.Grant.Role).Distinct(StringComparer.OrdinalIgnoreCase).Order().ToList();
            var org = AccessRules.SeesWholeOrganization(p.Level, roles);
            body.Append(p.Upn is null
                ? $"<p>{E(who)} is not in the directory, so they see nothing unless granted Organization{(roles.Contains(Roles.Organization) ? " (they are)" : "")}.</p>"
                : $"<p><b>{E(p.DisplayName ?? p.Upn)}</b>, level {(p.Level?.ToString() ?? "not under the top")}, reports to {E(p.ManagerUpn ?? "nobody")}.</p>" +
                  $"<p>Sees: <b>{(org ? "the whole organization" : p.PeopleBelow > 0 ? $"themselves and the {p.PeopleBelow:#,0} people under them" : "their own use")}</b>" +
                  $"{(org && roles.Contains(Roles.Organization) && p.Level is not (1 or 2) ? " (by the Organization role)" : "")}" +
                  (!org && wideRoles.Count > 0 ? $"; on the {E(string.Join(", ", wideRoles))} pages, the whole organization" : "") + ".</p>");
            if (!AccessRules.MayUseDashboard(p.Level, roles, max))
                body.Append($"<p><b>Cannot open the dashboard</b>: it is open to levels 1 to {max}, and they hold no role{(roles.Contains(Roles.Admin) ? " other than Admin, which gives them the Admin pages only" : "")}, directly, through a profile or through a group listed below.</p>");

            var direct = held.Where(x => x.Via == "" && x.Grant.ProfileId is null).Select(x => RoleText(x.Grant)).Distinct().ToList();
            body.Append($"<p>Roles granted to the account directly: {(direct.Count == 0 ? "none" : E(string.Join(", ", direct)))}.</p>");
            var viaProfiles = held.Where(x => x.Grant.ProfileId is not null).GroupBy(x => (x.Grant.ProfileId!.Value, x.Via!)).ToList();
            if (viaProfiles.Count > 0)
                body.Append("<p>Through access profiles:</p><ul>" + string.Concat(viaProfiles.Select(g =>
                    $"<li><b>{E(profileNames.GetValueOrDefault(g.Key.Item1, $"profile {g.Key.Item1}"))}</b>: {E(string.Join(", ", g.Select(x => RoleText(x.Grant))))}" +
                    (g.Key.Item2 == "" ? ", granted to them" : $", through {E(Named(g.Key.Item2))}") + "</li>")) + "</ul>");
            var viaGroups = held.Where(x => x.Via != "" && x.Grant.ProfileId is null).GroupBy(x => x.Via!, StringComparer.OrdinalIgnoreCase).ToList();
            body.Append("<p>Roles from groups" + (membersLoaded && loadedUtc is { } at ? $" (AD groups as of the directory load, {at:yyyy-MM-dd HH:mm} UTC)" : "") + ": " +
                (viaGroups.Count == 0 ? "none. " : "</p><ul>" + string.Concat(viaGroups.Select(g =>
                    $"<li>{E(string.Join(", ", g.Select(x => RoleText(x.Grant))))} through <b>{E(Named(g.Key))}</b></li>")) + "</ul><p>") +
                (membersLoaded ? "A change to a group in Active Directory reaches the dashboard at their next sign-in, and this list after the next directory load." : "") + "</p>");
            if (!membersLoaded && all.Any(g => g.Principal.Contains('\\')))
                body.Append("<p>AD groups: not known here. Group members are recorded by the nightly directory load (tools\\Export-UsersWithHierarchy-ToSql.ps1), which has not run against this database; Windows still applies group grants when they sign in.</p>");
            if (unexpanded.Count > 0)
                body.Append($"<p>No members recorded yet for {E(string.Join(", ", unexpanded))}: granted since the last directory load, or not found in Active Directory (its log names any it could not find).</p>");
            if (entraIds.Count > 0 && !graph.Available)
                body.Append("<p>Entra groups: not checked. The app registration in Insights:Entra is not set up to call Microsoft Graph (docs/ENTRA-SIGN-IN.md).</p>");
            else if (entraIds.Count > 0 && personOid is null)
                body.Append("<p>Entra groups: not checked for them. No Entra object ID is recorded for this person yet; the license tool records it at its next run.</p>");
            var adminGrants = held.Where(x => x.Grant.Role == Roles.Admin).Select(x => x.Grant.Pages).ToList();
            if (hasPages && adminGrants.Count > 0)
                body.Append($"<p>Their Admin role covers: {E(adminGrants.Any(pg => !AdminPages.Limits(pg)) ? AdminPages.Names(null) : AdminPages.Names(string.Join(",", adminGrants.SelectMany(AdminPages.Parse))))}.</p>");
            /* The Governance Workbench, when it is set up: the pages these roles open there, and its own roles. */
            if (HttpContext.RequestServices.GetRequiredService<global::Governance.Web.Infrastructure.WorkbenchState>().On)
                body.Append(await global::Governance.Web.Infrastructure.WorkbenchAccess.DescribeAsync(HttpContext.RequestServices, upn, roles, HttpContext.RequestAborted));
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

    public Task<IActionResult> OnPostGrantAsync(string? principal, string? type, string[]? role, string[]? adminPage, string? note, bool orgWide = false, string? entraGroup = null) => AdminChangeAsync(async () =>
    {
        /* An Entra group chosen in its own box is the grant's group, whatever else was typed. */
        if (!string.IsNullOrWhiteSpace(entraGroup)) (principal, type) = (entraGroup, "Group");
        principal = principal?.Trim();
        if (string.IsNullOrEmpty(principal) || principal.Length > 256) throw new ArgumentException("give a UPN, DOMAIN\\Group or an Entra group.");
        if (type is not ("User" or "Group")) throw new ArgumentException("choose User or Group.");
        var workbenchOn = HttpContext.RequestServices.GetRequiredService<global::Governance.Web.Infrastructure.WorkbenchState>().On;
        var roles = (role ?? []).Where(r => Roles.All.Contains(r) && (workbenchOn || !Roles.IsWorkbench(r))).Distinct().ToList();
        if (roles.Count == 0) throw new ArgumentException("tick at least one role.");
        if (type == "User" && !principal.Contains('@') && !principal.Contains('\\')) throw new ArgumentException("a user is a UPN (name@domain) or DOMAIN\\name.");
        var (objectId, groupName) = type == "Group" && !principal.Contains('\\') ? await EntraGroupAsync(principal) : ((Guid?)null, (string?)null);
        if (objectId is { } oid) principal = EntraPrincipal.Of(oid);
        var has28 = await Has28Async();
        if (orgWide && !has28) throw new ArgumentException("the whole organization on a role needs the 2.8.0 schema: run Install-CopilotAudit.ps1 -Dashboard.");
        if (objectId is not null && !has28) throw new ArgumentException("grants to Entra groups need the 2.8.0 schema: run Install-CopilotAudit.ps1 -Dashboard.");
        /* Admin limited to the pages ticked; none, or all of them, is the whole role. */
        string? pages = null;
        var hasPages = false;
        if (roles.Contains(Roles.Admin))
        {
            if (!Viewer.IsAdmin) throw new ArgumentException("only a full administrator can grant Admin.");
            pages = AdminPages.Format(adminPage);
            hasPages = await HasPagesAsync();
            if (pages is not null && !hasPages) throw new ArgumentException("limiting Admin to some pages needs the 2.6.4 schema: run Create-CopilotDashboardSchema.sql again.");
            if (pages is not null && IsMe(principal)) throw new ArgumentException("you cannot limit your own Admin role; another administrator can.");
        }
        var by = Viewer.Upn ?? Viewer.WindowsName;
        var added = new List<string>();
        var changed = "";
        var widened = new List<string>();
        foreach (var r in roles)
        {
            var wide = orgWide && AccessRules.IsPersona(r);
            var n = await Db.ExecuteAsync(has28 ? @"
IF NOT EXISTS (SELECT 1 FROM dash.RoleGrant WHERE Principal = @principal AND Role = @role)
    INSERT INTO dash.RoleGrant (Principal, PrincipalType, Role, Note, GrantedBy, PrincipalObjectId, PrincipalName, OrgWide)
    VALUES (@principal, @type, @role, @note, @by, @objectId, @groupName, @wide);" : @"
IF NOT EXISTS (SELECT 1 FROM dash.RoleGrant WHERE Principal = @principal AND Role = @role)
    INSERT INTO dash.RoleGrant (Principal, PrincipalType, Role, Note, GrantedBy) VALUES (@principal, @type, @role, @note, @by);",
                new { principal, type, role = r, note = string.IsNullOrWhiteSpace(note) ? null : note.Trim()[..Math.Min(400, note.Trim().Length)], by, objectId, groupName, wide });
            if (n > 0) added.Add(r);
            /* Granting a role held already sets its whole-organization box as ticked now, and says so. */
            else if (has28 && AccessRules.IsPersona(r) && await Db.ExecuteAsync(
                "UPDATE dash.RoleGrant SET OrgWide = @wide, GrantedBy = @by, GrantedUtc = SYSUTCDATETIME() WHERE Principal = @principal AND Role = @role AND OrgWide <> @wide;",
                new { principal, role = r, wide, by }) > 0) widened.Add(r + (wide ? " now across the whole organization" : " now for their own part only"));
            if (r != Roles.Admin || !hasPages) continue;
            /* A new Admin grant takes its pages; one held already has them replaced, and says so. */
            var moved = await Db.ExecuteAsync(
                "UPDATE dash.RoleGrant SET Pages = @pages, GrantedBy = @by, GrantedUtc = SYSUTCDATETIME() WHERE Principal = @principal AND Role = 'Admin' AND ISNULL(Pages, N'') <> ISNULL(@pages, N'');",
                new { principal, pages, by });
            if (moved > 0 && n <= 0) changed = $" {principal}'s Admin role now covers: {AdminPages.Names(pages)}.";
        }
        var had = roles.Except(added).Where(r => r != Roles.Admin || changed == "").Where(r => !widened.Any(w => w.StartsWith(r + " ", StringComparison.Ordinal))).ToList();
        var who = groupName is null ? principal : $"{groupName} ({principal})";
        var wideAdded = added.Where(r => orgWide && AccessRules.IsPersona(r)).ToList();
        return ((added.Count > 0 ? $"Granted {string.Join(", ", added)} to {type.ToLowerInvariant()} {who}" +
                    (wideAdded.Count > 0 ? $", {string.Join(", ", wideAdded)} across the whole organization" : "") +
                    (added.Contains(Roles.Admin) && pages is not null ? $", with Admin limited to: {AdminPages.Names(pages)}." : ".") : "")
             + (widened.Count > 0 ? $" {who}: {string.Join("; ", widened)}." : "")
             + (had.Count > 0 ? $" {who} already had {string.Join(", ", had)}." : "") + changed).Trim();
    });

    /// <summary>Whether this database has 2.8.0's grant columns (the whole organization, Entra groups).</summary>
    async Task<bool> Has28Async() => await Db.ScalarAsync<int>("SELECT CASE WHEN COL_LENGTH(N'dash.RoleGrant', N'OrgWide') IS NULL THEN 0 ELSE 1 END;") == 1;

    /// <summary>Entra groups whose name starts with what was typed, for the grant form's list.</summary>
    public async Task<IActionResult> OnGetGroupsAsync(string? q)
    {
        if (!await PrepareAsync() || !Viewer.CanAdmin(Request.Path)) return StatusCode(403);
        var groups = await HttpContext.RequestServices.GetRequiredService<EntraGraph>().FindGroupsAsync(q ?? "");
        return new JsonResult(groups.Select(g => new { value = EntraPrincipal.Of(g.Id), label = g.Name }));
    }

    /// <summary>The grant form's box for an Entra group: its name, searched in Graph from the second letter.</summary>
    string EntraGroupBox() =>
        $"<label>Or an Entra group<input type=\"text\" name=\"entraGroup\" size=\"34\" list=\"entra-groups\" autocomplete=\"off\" " +
        $"data-lookup=\"{E(Request.Path + "?handler=groups" + TenantQuery)}\" data-min=\"2\" placeholder=\"2 letters of the group's name\"/>{FieldTip("entraGroup", "Or an Entra group")}</label>" +
        "<datalist id=\"entra-groups\"></datalist>";

    public Task<IActionResult> OnPostRevokeAsync(int id) => AdminChangeAsync(async () =>
    {
        /* Admin is removed by a full administrator only. */
        if (!Viewer.IsAdmin && await Db.ScalarAsync<int>("SELECT COUNT(*) FROM dash.RoleGrant WHERE GrantId = @id AND Role = 'Admin';", new { id }) > 0)
            throw new ArgumentException("only a full administrator can remove Admin.");
        var g = (await Db.QueryAsync<(string Principal, string Role)>("DELETE FROM dash.RoleGrant OUTPUT deleted.Principal, deleted.Role WHERE GrantId = @id;", new { id })).FirstOrDefault();
        return g.Principal is null ? "That grant was already gone." : $"Removed {g.Role} from {g.Principal}.";
    });

    /// <summary>Every role of one person or group.</summary>
    public Task<IActionResult> OnPostRevokeAllAsync(string? principal) => AdminChangeAsync(() => RemoveEveryRoleAsync([principal]));

    /// <summary>Every role of each person and group ticked.</summary>
    public Task<IActionResult> OnPostRevokeManyAsync(string[]? principal) => AdminChangeAsync(() => RemoveEveryRoleAsync(principal));

    async Task<string> RemoveEveryRoleAsync(IEnumerable<string?>? asked)
    {
        var list = RoleGrants.Clean(asked);
        if (list.Count == 0) throw new ArgumentException("tick at least one person or group.");
        if (list.Count > RoleGrants.MaxAtOnce) throw new ArgumentException($"at most {RoleGrants.MaxAtOnce} at once.");
        if (!Viewer.IsAdmin && await Db.ScalarAsync<int>("SELECT COUNT(*) FROM dash.RoleGrant WHERE Principal IN @list AND Role = 'Admin';", new { list }) > 0)
            throw new ArgumentException("only a full administrator can remove Admin, and someone selected holds it.");
        var gone = await Db.QueryAsync<(string Principal, string Role)>(
            "DELETE FROM dash.RoleGrant OUTPUT deleted.Principal, deleted.Role WHERE Principal IN @list;", new { list });
        return RoleGrants.Removed(gone, list, Viewer.Upn);
    }
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
                $"<input type=\"hidden\" name=\"key\" value=\"{a.AgentKey}\"/>" + Input("name", "Friendly name", a.FriendlyName, 22) + PersonBox("owner", "Owner", a.Owner, 22) + Button("Save")))]);
        Panels.Add(new("Agents", new HtmlString(pg.Controls(Request, new Dictionary<string, string> { ["unnamed"] = "Unnamed first", ["new"] = "Newest", ["name"] = "Name" }).Value + t.Render().Value),
            "An agent appears under the friendly name the audit log gives it, the latest when its maker renames it; where the log gives none, under a name read from its id (a Copilot Studio agent's schema name). A name given here shows everywhere instead; clear it to go back."));

        /* The labels seen and how often: one pass over the rollup instead of two, and kept until the data
           changes, as a report's queries are. It reads every day there is: 1.4 seconds at 120 million interactions on
           each visit to this page otherwise. The names and their order are read as they are now. */
        List<(string LabelId, long N)> seen;
        await S.Stamp.EnsureAsync();
        using (QueryCache.Use())
            seen = await Db.QueryAsync<(string LabelId, long N)>(
                "SELECT rd.Item, SUM(CAST(rd.Interactions AS BIGINT)) FROM dash.ResourceDay AS rd WHERE rd.Kind = 'Label' GROUP BY rd.Item;");
        var named = await Db.QueryAsync<(string LabelId, string? Name, byte? Rank)>("SELECT LabelId, Name, Rank FROM dash.SensitivityLabel;");
        var labels = seen.Select(x => x.LabelId).Union(named.Select(x => x.LabelId), StringComparer.OrdinalIgnoreCase)
            .Select(id => (LabelId: id, Named: named.FirstOrDefault(x => string.Equals(x.LabelId, id, StringComparison.OrdinalIgnoreCase)),
                           N: seen.Where(x => string.Equals(x.LabelId, id, StringComparison.OrdinalIgnoreCase)).Sum(x => x.N)))
            .Select(x => (x.LabelId, x.Named.Name, x.Named.Rank, x.N))
            .OrderBy(x => x.Rank ?? 255).ThenBy(x => x.LabelId, StringComparer.OrdinalIgnoreCase).ToList();
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
            "Copilot Chat or Copilot inside an app. A change applies the next time the pipeline builds sessions and the report tables, and shows on the dashboard after that.", "half"));

        /* Which accounts are not a person: a Copilot license on one is a policy breach. */
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
            "A Copilot license on an account these rules match is a policy breach, on Security, Compliance and Licensing › Copilot on non-human accounts, " +
            "unless an administrator confirms the account is a person there. The groups' members are read by the next nightly directory load. " +
            "The alert goes to the Security and Licensing alert recipients (Admin › Alerts and digests), once for each account."));
    }

    /// <summary>The longest list of patterns, types or groups: dash.Setting.Value is NVARCHAR(400).</summary>
    public const int MaxList = 400;

    public Task<IActionResult> OnPostNonHumanAsync(string? guests, string? names, string? nochart, string? patterns, string? types, string? groups, string? alert) => AdminChangeAsync(async () =>
    {
        static string? Clean(string? v, int max, Func<string, bool> ok, string what)
        {
            var list = CopilotInsights.Data.NonHuman.Split(v);
            foreach (var x in list) if (x.Length > max || !ok(x)) throw new ArgumentException($"'{x}' is not {what}.");
            var joined = string.Join(", ", list);
            /* A setting holds 400 characters. This allowed 1,000: a longer list failed in SQL Server after the
               boxes before it were saved, with no audit row and no word of why. Now nothing is saved, and it says so. */
            if (joined.Length > MaxList) throw new ArgumentException($"the {what} list is longer than {MaxList} characters, the most a setting holds.");
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
    public override string Intro => "Every page view, refusal, export, data API call and admin change, with who, what they looked at and from where.";

    static readonly string[] Kinds = ["View", "Denied", "Export", "Api", "Admin"];

    /// <summary>
    /// A kind in words, one entry or many. The log stores View, Denied, Export, Api and Admin; the page showed
    /// those, and counted the data API's calls under a second "Admin changes". The download keeps the stored words.
    /// </summary>
    public static string KindName(string kind, bool many = false) => kind switch
    {
        "View" => many ? "Page views" : "Page view",
        "Denied" => "Refused",
        "Export" => many ? "Exports" : "Export",
        "Api" => many ? "Data API calls" : "Data API call",
        "Admin" => many ? "Admin changes" : "Admin change",
        _ => kind,
    };

    sealed class Row
    {
        public DateTime AtUtc { get; set; }
        public string Viewer { get; set; } = "";
        public string Kind { get; set; } = "";
        public string? Page { get; set; }
        public string? Scope { get; set; }
        public string? Detail { get; set; }
        public string? ClientIp { get; set; }
        public string? Change { get; set; }
        public int TotalRows { get; set; }
    }

    /// <summary>A change's record, laid out to read: the JSON indented.</summary>
    /// <summary>The audit row's record of a change, as lines a person reads: settings and grants first, then the form. The CSV keeps the JSON.</summary>
    public static string AuditChangeText(string json)
    {
        try
        {
            var root = System.Text.Json.JsonDocument.Parse(json).RootElement;
            var lines = new List<string>();
            static string V(System.Text.Json.JsonElement e) => e.ValueKind switch
            {
                System.Text.Json.JsonValueKind.Null => "(none)",
                System.Text.Json.JsonValueKind.String => e.GetString() is { Length: > 0 } s ? s : "(empty)",
                _ => e.ToString(),
            };
            if (root.TryGetProperty("settings", out var settings))
            {
                lines.Add("Settings changed:");
                foreach (var p in settings.EnumerateObject())
                    lines.Add($"  {p.Name}: {V(p.Value.GetProperty("before"))} → {V(p.Value.GetProperty("after"))}");
            }
            foreach (var (key, label) in new[] { ("granted", "Roles granted:"), ("revoked", "Roles taken away:") })
                if (root.TryGetProperty(key, out var list))
                {
                    lines.Add(label);
                    lines.AddRange(list.EnumerateArray().Select(x => "  " + V(x)));
                }
            if (root.TryGetProperty("submitted", out var submitted) && submitted.EnumerateObject().Any())
            {
                lines.Add("Submitted on the form:");
                lines.AddRange(submitted.EnumerateObject().Select(p => $"  {p.Name}: {V(p.Value)}"));
            }
            return lines.Count > 0 ? string.Join("\n", lines) : json;
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException or KeyNotFoundException) { return json; }
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
        /* What a change submitted and altered (dash.AuditLog.Change), where the database has the column. */
        var hasChange = await Db.ScalarAsync<int>("SELECT CASE WHEN COL_LENGTH(N'dash.AuditLog', N'Change') IS NULL THEN 0 ELSE 1 END") == 1;
        var rows = await Db.QueryAsync<Row>($@"
SELECT a.AtUtc, a.Viewer, a.Kind, a.Page, a.Scope, a.Detail, a.ClientIp, {(hasChange ? "a.Change" : "CAST(NULL AS NVARCHAR(MAX)) AS Change")}, COUNT(*) OVER () AS TotalRows
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
            Kpis.Add(new(KindName(kn, many: true), Num(c.N), c.N == 0 ? "none in this period" : $"by {Count(c.People, "person", "people")}",
                kn == "Denied" && c.N > 0 ? "crit" : null));
        }
        var t = new Table
        {
            Zone = Zone, Headers = ["When (UTC)", "Who", "Kind", "Page", "Scope", "Detail", "From"], EmptyText = "Nothing logged in this period.",
            Server = new(pg, Request, ["new", "who", "kind", "page", "scope", null, "from"], new HashSet<int> { 0 }),
        };
        /* The page says each kind in words; the download keeps the words the log stores, which a tool reading it may match on. */
        var csv = new Table { Zone = Zone, Headers = [.. t.Headers, "Change"] };
        foreach (var r in rows)
        {
            object? detail = r.Change is null ? r.Detail : new HtmlString(E(r.Detail ?? "") + $"<details class=\"kwhy\"><summary>What changed</summary><pre class=\"change\">{E(AuditChangeText(r.Change))}</pre></details>");
            t.Rows.Add([r.AtUtc, r.Viewer, KindName(r.Kind), r.Page, r.Scope, detail, r.ClientIp]);
            csv.Rows.Add([r.AtUtc, r.Viewer, r.Kind, r.Page, r.Scope, r.Detail, r.ClientIp, r.Change]);
        }
        CsvTable = csv;
        string Link(string label, int dd, string? kk, bool on) =>
            $"<a href=\"{E(Request.Path + $"?d={dd}" + (kk is null ? "" : "&kind=" + kk))}\"{(on ? " class=\"on\"" : "")}>{label}</a>";
        var periodLinks = string.Join(" · ", ReportContext.DayChoices.Select(x => Link($"{x} days", x, kind, x == days)));
        var kindLinks = Link("All", days, null, kind is null) + string.Concat(Kinds.Select(x => " · " + Link(KindName(x, many: true), days, x, x == kind)));
        Panels.Add(new("Entries", new HtmlString($"<p class=\"sub\">Last: {periodLinks}</p><p class=\"sub\">Show: {kindLinks}</p>" +
            pg.Controls(Request, new Dictionary<string, string> { ["new"] = "Newest", ["old"] = "Oldest", ["who"] = "Who" }).Value + t.Render().Value),
            "Viewing-as entries (development only) show both accounts.", "full", CsvUrl()));
    }
}
