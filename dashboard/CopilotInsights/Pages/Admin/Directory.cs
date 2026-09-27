using System.Globalization;
using CopilotInsights.Data;
using CopilotInsights.Infrastructure;
using Microsoft.AspNetCore.Html;
using Microsoft.AspNetCore.Mvc;

namespace CopilotInsights.Pages.Admin;

/* ---- Directory quality ------------------------------------------------------------------------ */

/// <summary>What in Active Directory weakens the reports and the security model, and who to fix.</summary>
public sealed class DirectoryModel(PageServices s) : AdminPage(s)
{
    public override string Title => "Directory quality";
    public override string Intro => "The gaps in Active Directory that weaken the reports and who sees what: people with no manager, managers who are not in the directory, missing departments, titles and email addresses, and accounts the sign-in check cannot match. Fix them in AD; the next nightly load picks them up.";

    /// <summary>Each check: its label, why it matters, and the WHERE over dash.Person p / audit.UserDirectory ud.</summary>
    internal static readonly (string Key, string Label, string Why, string Where)[] Checks =
    [
        ("nomanager", "No manager", "They sit outside the org chart: only whole-organisation viewers see them, and no manager's view counts them.",
            "p.AccountEnabled = 1 AND p.ManagerUpn IS NULL AND p.Upn <> ISNULL(@top, N'')"),
        ("lostmanager", "Manager not in the directory", "The manager named in AD is not a person in the directory, so the chain above them is broken.",
            "p.AccountEnabled = 1 AND p.ManagerUpn IS NOT NULL AND NOT EXISTS (SELECT 1 FROM dash.Person AS m WHERE m.Upn = p.ManagerUpn)"),
        ("disabledmanager", "Manager's account disabled", "Their manager has left: the manager's view goes to nobody, and digests to a closed mailbox.",
            "p.AccountEnabled = 1 AND EXISTS (SELECT 1 FROM dash.Person AS m WHERE m.Upn = p.ManagerUpn AND m.AccountEnabled = 0)"),
        ("unplaced", "Not placed under the top", "No chain of managers reaches the top of the organisation, so levels and scopes cannot be worked out for them.",
            "p.AccountEnabled = 1 AND p.Level IS NULL"),
        ("nodept", "No department", "They count under \"(no department)\" in every breakdown, chargeback and leaderboard.",
            "p.AccountEnabled = 1 AND NULLIF(p.Department, N'') IS NULL"),
        ("notitle", "No job title", "Job families cannot place them.",
            "p.AccountEnabled = 1 AND NULLIF(ud.JobTitle, N'') IS NULL"),
        ("nomail", "No email address", "Manager and personal digests go to their sign-in name instead, which may not be a mailbox.",
            "p.AccountEnabled = 1 AND NULLIF(ud.Mail, N'') IS NULL"),
        ("nosid", "No SID recorded", "Sign-in cannot be checked against the account's SID for them (the directory load records it from AD).",
            "p.AccountEnabled = 1 AND p.Sid IS NULL"),
        ("dupsid", "SID shared with another person", "Two directory records carry one SID: the sign-in check picks the enabled one; the other is probably a stale copy.",
            "p.Sid IS NOT NULL AND EXISTS (SELECT 1 FROM dash.Person AS o WHERE o.Sid = p.Sid AND o.Upn <> p.Upn)"),
        ("widespan", "Over 40 direct reports", "Not an error, but often a placeholder manager or a service account used as one.",
            "p.AccountEnabled = 1 AND p.DirectReports > 40"),
    ];

    sealed class Row
    {
        public string Upn { get; set; } = "";
        public string? DisplayName { get; set; }
        public string? Department { get; set; }
        public string? ManagerUpn { get; set; }
        public string? JobTitle { get; set; }
        public int DirectReports { get; set; }
        public int TotalRows { get; set; }
    }

    const string From = "FROM dash.Person AS p LEFT JOIN audit.UserDirectory AS ud ON ud.UserPrincipalName = p.Upn";

    protected override async Task BuildAsync()
    {
        var top = Ctx.OrgTopUpn;
        var counts = new Dictionary<string, int>();
        foreach (var c in Checks) counts[c.Key] = await Db.ScalarAsync<int>($"SELECT COUNT(*) {From} WHERE {c.Where};", new { top });
        var people = await Db.ScalarAsync<int>("SELECT COUNT(*) FROM dash.Person WHERE AccountEnabled = 1;");
        var notInDir = await Db.ScalarAsync<int>(@"SELECT COUNT(*) FROM bi.DimUser AS du WHERE du.DirectoryStatus <> N'Found'
  AND EXISTS (SELECT 1 FROM dash.UserDay AS ud WHERE ud.UserKey = du.UserKey AND ud.Interactions > 0);");
        Kpis.Add(new("People in the directory", Num(people), "enabled accounts"));
        var worst = Checks.Take(4).Sum(c => counts[c.Key]);
        Kpis.Add(new("Outside the org chart", Num(worst), "no manager, a missing or departed manager, or not under the top", worst > 0 ? "warn" : null));
        Kpis.Add(new("Copilot users not in the directory", Num(notInDir), "in the audit records but not found in AD", notInDir > 0 ? "warn" : null));

        var check = Checks.FirstOrDefault(c => c.Key == Request.Query["check"]);
        var t = new Table { Headers = ["Check", "People", "Share", "Why it matters", ""], Numeric = new HashSet<int> { 1, 2 } };
        foreach (var c in Checks)
            t.Rows.Add([c.Label, counts[c.Key], Pct(Ratio(counts[c.Key], people)), c.Why,
                counts[c.Key] == 0 ? null : PrivacyMode ? "privacy mode: counts only" : new Link(c.Key == check.Key ? "Listed below" : "List them", Request.Path + "?check=" + c.Key)]);
        Panels.Add(new("Checks", t.Render(), "From the nightly directory load (audit.UserDirectory) and the org chart built from it (dash.Person)."));
        if (check.Key is null || PrivacyMode) return;

        var pg = new Paging(Request, new Dictionary<string, string> { ["name"] = "p.DisplayName", ["dept"] = "p.Department, p.DisplayName" }, "name");
        var rows = await Db.QueryAsync<Row>($@"
SELECT p.Upn, p.DisplayName, p.Department, p.ManagerUpn, ud.JobTitle, p.DirectReports, COUNT(*) OVER () AS TotalRows
{From} WHERE {check.Where} AND (@q IS NULL OR p.DisplayName LIKE @q OR p.Upn LIKE @q OR p.Department LIKE @q)
{pg.OrderAndPage()};", new { top, q = pg.Like });
        pg.Total = rows.FirstOrDefault()?.TotalRows ?? 0;
        var lt = new Table { Headers = ["Name", "Sign-in", "Department", "Job title", "Manager (UPN in AD)", "Direct reports"], Numeric = new HashSet<int> { 5 } };
        foreach (var r in rows) lt.Rows.Add([r.DisplayName, r.Upn, r.Department, r.JobTitle, r.ManagerUpn, r.DirectReports]);
        CsvTable = lt;
        Panels.Add(new(check.Label, new HtmlString(pg.Controls(Request, new Dictionary<string, string> { ["name"] = "Name", ["dept"] = "Department" }).Value + lt.Render().Value),
            check.Why + " Download the list for the AD team.", "full", CsvUrl()));
    }
}

/* ---- Classifications and events -------------------------------------------------------------------- */

/// <summary>The rules that group job titles into families and interactions into use cases, and the events marked on trend charts.</summary>
public sealed class ClassificationsModel(PageServices s) : AdminPage(s)
{
    public override string Title => "Classifications and events";
    public override string Intro => "Rules that group job titles into job families (People > Job families) and interactions into use cases (Leadership > Use cases), and the events drawn on every trend chart: a rollout wave, a training push, an outage, a policy change. Patterns use SQL LIKE: % matches anything, so %engineer% is any title containing engineer. The first matching rule by priority wins.";

    static readonly string[] Kinds = ["Rollout", "Training", "Outage", "Policy", "Other"];
    static readonly string[] Tri = ["Either", "Yes", "No"];

    protected override async Task BuildAsync()
    {
        ShowDone();
        var families = await Db.QueryAsync<(int RuleId, string Pattern, string Family, int Priority, string UpdatedBy)>("SELECT RuleId, Pattern, Family, Priority, UpdatedBy FROM dash.JobFamilyRule ORDER BY Priority, RuleId;");
        var ft = new Table { Headers = ["Priority", "Job title like", "Family", "Set by", ""], Numeric = new HashSet<int> { 0 }, EmptyText = "No rules: everyone is \"(no family)\"." };
        foreach (var f in families)
            ft.Rows.Add([f.Priority, f.Pattern, f.Family, f.UpdatedBy, new HtmlString(Form("delfamily", Hidden("id", f.RuleId) + Button("Remove", quiet: true)))]);
        var min = await S.Settings.IntAsync("MinGroupSize", 5);
        var titles = await Db.QueryAsync<(string Title, int People)>(@"
SELECT TOP (25) du.JobTitle, COUNT(*) FROM bi.DimUser AS du
WHERE du.AccountEnabled = 1 AND du.DirectoryStatus = N'Found' AND NULLIF(du.JobTitle, N'') IS NOT NULL
  AND NOT EXISTS (SELECT 1 FROM dash.JobFamilyRule AS r WHERE du.JobTitle LIKE r.Pattern)
GROUP BY du.JobTitle HAVING COUNT(*) >= @min ORDER BY COUNT(*) DESC;", new { min });
        var tt = new Table { Headers = ["Title without a family", "People"], Numeric = new HashSet<int> { 1 }, EmptyText = "Every common title has a family." };
        foreach (var x in titles) tt.Rows.Add([x.Title, x.People]);
        Panels.Add(new("Job families", new HtmlString(ft.Render().Value + "<h3>Add a rule</h3>" + Form("family",
            Input("pattern", "Job title like", "%engineer%", 24) + Input("family", "Family", "Engineering", 24) + Input("priority", "Priority (lower first)", "100", 5, "number") + Button("Add")) +
            "<h3>The most common titles no rule covers</h3>" + tt.Render().Value), $"Titles held by fewer than {min} people are not listed."));

        var cases = await Db.QueryAsync<(int RuleId, string AppPattern, bool? WebGrounded, bool? WithAgent, string UseCase, int Priority)>(
            "SELECT RuleId, AppPattern, WebGrounded, WithAgent, UseCase, Priority FROM dash.UseCaseRule ORDER BY Priority, RuleId;");
        static string T(bool? b) => b is null ? "Either" : b.Value ? "Yes" : "No";
        var ct = new Table { Headers = ["Priority", "App like", "Searched the web", "With an agent", "Use case", ""], Numeric = new HashSet<int> { 0 } };
        foreach (var c in cases)
            ct.Rows.Add([c.Priority, c.AppPattern, T(c.WebGrounded), T(c.WithAgent), c.UseCase, new HtmlString(Form("delcase", Hidden("id", c.RuleId) + Button("Remove", quiet: true)))]);
        var apps = await Db.QueryAsync<string>("SELECT App FROM bi.DimApp ORDER BY App;");
        Panels.Add(new("Use cases", new HtmlString(ct.Render().Value + "<h3>Add a rule</h3>" + Form("usecase",
            Input("app", "App like", "%", 16) + Select("web", "Searched the web", Tri, "Either") + Select("agent", "With an agent", Tri, "Either") +
            Input("usecase", "Use case", null, 24) + Input("priority", "Priority (lower first)", "50", 5, "number") + Button("Add")) +
            Form("resetcases", Button("Put back the default rules", "Replace every use-case rule with the defaults?", quiet: true)) +
            $"<p class=\"sub\">Apps in the data: {E(string.Join(", ", apps))}.</p>"),
            "Interactions no rule matches count as Other; the defaults end with a catch-all."));

        var markers = await Db.QueryAsync<(int MarkerId, DateTime Day, string Label, string Kind, string UpdatedBy)>("SELECT MarkerId, Day, Label, Kind, UpdatedBy FROM dash.EventMarker ORDER BY Day DESC;");
        var mt = new Table { Headers = ["Day", "Event", "Kind", "Added by", ""], EmptyText = "No events yet." };
        foreach (var m in markers) mt.Rows.Add([m.Day, m.Label, m.Kind, m.UpdatedBy, new HtmlString(Form("delmarker", Hidden("id", m.MarkerId) + Button("Remove", quiet: true)))]);
        Panels.Add(new("Events on the trend charts", new HtmlString(mt.Render().Value + "<h3>Add an event</h3>" + Form("marker",
            Input("day", "Day", DateTime.Today.ToString("yyyy-MM-dd"), 10, "date") + Input("label", "Event", null, 30) + Select("kind", "Kind", Kinds, "Rollout") + Button("Add"))),
            "Each shows as a dashed rule with its name on every trend in the period it falls in; hover for the detail."));
    }

    static string Required(string? v, string what, int max)
    {
        v = v?.Trim();
        if (string.IsNullOrEmpty(v) || v.Length > max) throw new ArgumentException($"{what} is 1 to {max} characters.");
        return v;
    }

    static bool? TriState(string? v) => v switch { "Yes" => true, "No" => false, _ => null };

    public Task<IActionResult> OnPostFamilyAsync(string? pattern, string? family, string? priority) => AdminChangeAsync(async () =>
    {
        var p = Required(pattern, "The pattern", 200);
        var f = Required(family, "The family", 100);
        var pr = SettingWriter.Int(priority, "The priority", 0, 100000);
        await Db.ExecuteAsync("INSERT INTO dash.JobFamilyRule (Pattern, Family, Priority, UpdatedBy) VALUES (@p, @f, @pr, @by);", new { p, f, pr, by = Viewer.Upn ?? Viewer.WindowsName });
        var n = await Db.ScalarAsync<int>("SELECT COUNT(*) FROM bi.DimUser WHERE AccountEnabled = 1 AND JobTitle LIKE @p;", new { p });
        return $"Titles like {p} are now {f} ({n:#,0} people match, before higher-priority rules).";
    });

    public Task<IActionResult> OnPostDelfamilyAsync(int id) => AdminChangeAsync(async () =>
        await Db.ExecuteAsync("DELETE FROM dash.JobFamilyRule WHERE RuleId = @id", new { id }) > 0 ? "Rule removed." : "That rule was already gone.");

    public Task<IActionResult> OnPostUsecaseAsync(string? app, string? web, string? agent, string? usecase, string? priority) => AdminChangeAsync(async () =>
    {
        var a = Required(app, "The app pattern", 200);
        var u = Required(usecase, "The use case", 100);
        var pr = SettingWriter.Int(priority, "The priority", 0, 100000);
        await Db.ExecuteAsync("INSERT INTO dash.UseCaseRule (AppPattern, WebGrounded, WithAgent, UseCase, Priority, UpdatedBy) VALUES (@a, @w, @g, @u, @pr, @by);",
            new { a, w = TriState(web), g = TriState(agent), u, pr, by = Viewer.Upn ?? Viewer.WindowsName });
        return $"Use-case rule added: {u}.";
    });

    public Task<IActionResult> OnPostDelcaseAsync(int id) => AdminChangeAsync(async () =>
        await Db.ExecuteAsync("DELETE FROM dash.UseCaseRule WHERE RuleId = @id", new { id }) > 0 ? "Rule removed." : "That rule was already gone.");

    public Task<IActionResult> OnPostResetcasesAsync() => AdminChangeAsync(async () =>
    {
        await Db.ExecuteAsync(@"
DELETE FROM dash.UseCaseRule;
INSERT INTO dash.UseCaseRule (AppPattern, WebGrounded, WithAgent, UseCase, Priority, UpdatedBy) VALUES
    (N'%', NULL, 1, N'Working with an agent', 10, N'default'), (N'%Teams%', NULL, NULL, N'Meetings and chats', 20, N'default'),
    (N'Outlook', NULL, NULL, N'Email', 20, N'default'), (N'Word', NULL, NULL, N'Writing documents', 20, N'default'),
    (N'Excel', NULL, NULL, N'Analysing data', 20, N'default'), (N'PowerPoint', NULL, NULL, N'Presentations', 20, N'default'),
    (N'%Loop%', NULL, NULL, N'Collaborative notes', 20, N'default'), (N'%OneNote%', NULL, NULL, N'Collaborative notes', 20, N'default'),
    (N'%', 1, NULL, N'Research on the web', 50, N'default'), (N'%', NULL, NULL, N'Asking questions (Copilot Chat)', 999, N'default');");
        return "The default use-case rules are back.";
    });

    public Task<IActionResult> OnPostMarkerAsync(DateTime? day, string? label, string? kind) => AdminChangeAsync(async () =>
    {
        if (day is null) throw new ArgumentException("give the day.");
        var l = Required(label, "The event", 100);
        if (kind is null || !Kinds.Contains(kind)) throw new ArgumentException("choose the kind.");
        await Db.ExecuteAsync("INSERT INTO dash.EventMarker (Day, Label, Kind, UpdatedBy) VALUES (@day, @l, @kind, @by);", new { day, l, kind, by = Viewer.Upn ?? Viewer.WindowsName });
        return $"Event \"{l}\" on {day:yyyy-MM-dd} added.";
    });

    public Task<IActionResult> OnPostDelmarkerAsync(int id) => AdminChangeAsync(async () =>
        await Db.ExecuteAsync("DELETE FROM dash.EventMarker WHERE MarkerId = @id", new { id }) > 0 ? "Event removed." : "That event was already gone.");
}

/* ---- Scheduled extracts --------------------------------------------------------------------------- */

/// <summary>Aggregate CSV files written to a folder or share on a schedule, for a data lake, a finance system or another BI tool.</summary>
public sealed class ExtractsModel(PageServices s, Notifier notifier, OpsData ops) : AdminPage(s)
{
    public override string Title => "Scheduled extracts";
    public override string Intro => "CSV files of aggregate figures written to a folder or file share on a schedule, for a data lake, a finance system or another BI tool. Only aggregates: no extract names a person. The site's application pool account (or the web server's computer account, for a share) needs write access to the folder.";

    sealed class Row
    {
        public int ExtractId { get; set; }
        public string Name { get; set; } = "";
        public string Dataset { get; set; } = "";
        public string Folder { get; set; } = "";
        public string Frequency { get; set; } = "";
        public byte? DayOfWeek { get; set; }
        public byte? DayOfMonth { get; set; }
        public byte HourLocal { get; set; }
        public int Days { get; set; }
        public bool Enabled { get; set; }
        public DateTime? LastRunUtc { get; set; }
        public string? LastStatus { get; set; }
    }

    static readonly string[] DayNames = ["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"];

    protected override async Task BuildAsync()
    {
        ShowDone();
        var rows = await Db.QueryAsync<Row>("SELECT * FROM dash.Extract ORDER BY Name;");
        var zone = await ops.ZoneAsync();
        var t = new Table { Headers = ["Name", "Data", "Folder", "When", "Covering (days)", "Last written (UTC)", "Result", ""], EmptyText = "No extracts yet." };
        foreach (var x in rows)
            t.Rows.Add([x.Name, x.Dataset, x.Folder,
                (x.Frequency switch { "Weekly" => $"Weekly, {DayNames[x.DayOfWeek ?? 1]}", "Monthly" => $"Monthly, day {x.DayOfMonth ?? 1}", _ => "Daily" }) + $" {x.HourLocal:00}:00" + (x.Enabled ? "" : " (paused)"),
                x.Days, x.LastRunUtc, x.LastStatus,
                new HtmlString(Form("run", Hidden("id", x.ExtractId) + Button("Write now", quiet: true)) +
                               Form("toggle", Hidden("id", x.ExtractId) + Button(x.Enabled ? "Pause" : "Resume", quiet: true)) +
                               Form("remove", Hidden("id", x.ExtractId) + Button("Remove", $"Remove the extract {x.Name}? Files already written stay.", quiet: true)))]);
        Panels.Add(new("Extracts", new HtmlString(t.Render().Value + "<h3>Add an extract</h3>" + Form("add",
            Input("name", "Name (the file is name-yyyyMMdd.csv)", null, 24) + Select("dataset", "Data", ExtractData.Datasets.Keys, "DepartmentWeek") +
            Input("folder", @"Folder (D:\Extracts or \\server\share\copilot)", null, 36) +
            Select("frequency", "How often", Schedule.Frequencies, "Weekly") + Select("dow", "Day (weekly)", DayNames, "Monday") +
            Input("dom", "Day of the month (monthly)", "1", 4, "number") + Input("hour", $"Hour ({zone.StandardName})", "6", 4, "number") +
            Input("days", "Covering the last (days)", "90", 5, "number") + Button("Add")) +
            "<ul class=\"sub\">" + string.Concat(ExtractData.Datasets.Select(d => $"<li><b>{E(d.Key)}</b>: {E(d.Value.Label)}</li>")) + "</ul>"),
            "Written by the dashboard's background checks (every five minutes, on one web server). A file of the same name is replaced."));
    }

    public Task<IActionResult> OnPostAddAsync(string? name, string? dataset, string? folder, string? frequency, string? dow, int? dom, int? hour, int? days) => AdminChangeAsync(async () =>
    {
        name = name?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > 100) throw new ArgumentException("give the extract a name.");
        if (dataset is null || !ExtractData.Datasets.ContainsKey(dataset)) throw new ArgumentException("choose the data.");
        if (ExtractData.FolderProblem(folder) is { } bad) throw new ArgumentException(bad);
        if (frequency is null || !Schedule.Frequencies.Contains(frequency)) throw new ArgumentException("choose how often.");
        if (hour is null or < 0 or > 23) throw new ArgumentException("the hour is 0 to 23.");
        if (dom is null or < 1 or > 28) throw new ArgumentException("the day of the month is 1 to 28.");
        if (days is null or < 1 or > 3650) throw new ArgumentException("cover 1 to 3,650 days.");
        var d = Array.IndexOf(DayNames, dow);
        await Db.ExecuteAsync(@"
INSERT INTO dash.Extract (Name, Dataset, Folder, Frequency, DayOfWeek, DayOfMonth, HourLocal, Days, CreatedBy, LastRunUtc)
VALUES (@name, @dataset, @folder, @frequency, @d, @dom, @hour, @days, @by, SYSUTCDATETIME());",
            new { name, dataset, folder = folder!.Trim(), frequency, d = d < 0 ? 1 : d, dom, hour, days, by = Viewer.Upn ?? Viewer.WindowsName });
        return $"Extract \"{name}\" added. The first file is written at its next scheduled time; Write now writes one today.";
    });

    public Task<IActionResult> OnPostRunAsync(int id) => AdminChangeAsync(async () => "Write now: " + await notifier.WriteExtractNowAsync(id, HttpContext.RequestAborted));

    public Task<IActionResult> OnPostToggleAsync(int id) => AdminChangeAsync(async () =>
        await Db.ScalarAsync<string?>("UPDATE dash.Extract SET Enabled = 1 - Enabled OUTPUT inserted.Name WHERE ExtractId = @id", new { id }) is { } n ? $"Extract \"{n}\" paused or resumed." : "That extract is gone.");

    public Task<IActionResult> OnPostRemoveAsync(int id) => AdminChangeAsync(async () =>
        await Db.ScalarAsync<string?>("DELETE FROM dash.Extract OUTPUT deleted.Name WHERE ExtractId = @id", new { id }) is { } n ? $"Extract \"{n}\" removed." : "That extract was already gone.");
}
