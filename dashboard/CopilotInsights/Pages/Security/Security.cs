using CopilotInsights.Charts;
using CopilotInsights.Data;
using CopilotInsights.Infrastructure;
using Microsoft.AspNetCore.Html;
using Microsoft.AspNetCore.Mvc;

namespace CopilotInsights.Pages.Security;

public sealed class IndexModel(PageServices s, Metrics m) : InsightsPage(s)
{
    public override string Title => "Security signals";
    public override string Persona => "Security";
    public override string Intro => "Security signals are interactions (one prompt and Copilot's answer) with a jailbreak attempt, a prompt injection in content Copilot read, or a blocked resource. This page shows how many, when, in which apps and in which parts of the organization. Totals only: no one is named.";

    protected override async Task BuildAsync()
    {
        /* The refresh's own list of the flagged interactions holds every interaction with a signal, with its hour and
           app: the hours and the apps are read from it, a few rows in every thousand interactions, where the fact was read
           for the whole period to find them. Before the first refresh that writes it, the fact is read as before. */
        var flagged = await Db.ScalarAsync<int>("SELECT CASE WHEN OBJECT_ID(N'dash.FlaggedInteraction', N'U') IS NOT NULL AND EXISTS (SELECT 1 FROM dash.FlaggedInteraction) THEN 1 ELSE 0 END;") == 1;
        var (signals, hour) = flagged ? ("dash.FlaggedInteraction", "DATEPART(HOUR, f.ActivityHour)") : ("bi.FactInteraction", "f.HourOfDay");
        /* Six independent reads, each on its own connection: started together, awaited where they are used. */
        var trTask = m.TrendAsync(Ctx);
        /* Each person's signals over the period, summed once by the refresh. */
        var peopleTask = Db.ScalarAsync<int>($@"
SELECT COUNT(*) FROM {Ctx.PerPerson()} AS p {Ctx.Scope("p.UserKey")}
WHERE p.Jailbreak > 0 OR p.PromptInjection > 0;", Ctx.Args());
        var hoursTask = Db.QueryAsync<(int Dow, int Hour, int N)>($@"
SELECT d.DayOfWeekNumber, {hour}, COUNT(*)
FROM {signals} AS f {Ctx.Scope("f.UserKey")}
JOIN bi.DimDate AS d ON d.DateKey = f.DateKey
WHERE f.DateKey BETWEEN @k0 AND @k1 AND (f.JailbreakDetected = 1 OR f.PromptInjectionDetected = 1)
GROUP BY d.DayOfWeekNumber, {hour};", Ctx.Args());
        var appsTask = Db.QueryAsync<(string App, int Jb, int Inj, int Blk)>($@"
SELECT a.App, SUM(CAST(f.JailbreakDetected AS INT)), SUM(CAST(f.PromptInjectionDetected AS INT)), SUM(CAST(f.HasBlockedResource AS INT))
FROM {signals} AS f {Ctx.Scope("f.UserKey")}
JOIN bi.DimApp AS a ON a.AppKey = f.AppKey
WHERE f.DateKey BETWEEN @k0 AND @k1 AND (f.JailbreakDetected = 1 OR f.PromptInjectionDetected = 1 OR f.HasBlockedResource = 1)
GROUP BY a.App;", Ctx.Args());
        var unitsTask = m.UnitsAsync(Ctx);
        var seriesTask = m.UnitSeriesAsync(Ctx);
        await Task.WhenAll(trTask, peopleTask, hoursTask, appsTask, unitsTask, seriesTask);
        var tr = await trTask;
        long jb = tr.Sum(w => w.Jailbreak), inj = tr.Sum(w => w.Injection), blk = tr.Sum(w => w.Blocked), all = tr.Sum(w => w.Interactions);
        var people = await peopleTask;
        Kpis.Add(new("Jailbreak attempts", Num(jb), all > 0 ? $"{jb * 1000.0 / all:0.00} per 1,000 interactions" : null, jb > 0 ? "crit" : null, Href: Open("/security/flags", extra: "kind=jailbreak&status=any")));
        Kpis.Add(new("Prompt injections", Num(inj), all > 0 ? $"{inj * 1000.0 / all:0.00} per 1,000 interactions" : null, inj > 0 ? "crit" : null, Href: Open("/security/flags", extra: "kind=injection&status=any")));
        Kpis.Add(new("Blocked resources", Num(blk), "interactions where a policy or permissions stopped Copilot reading content"));
        Kpis.Add(new("People involved", Num(people), "with a jailbreak attempt or prompt injection in the period, accounts since disabled included"));

        Panels.Add(new("Security signals over time",
            Chart.Columns(Labels(), [
                new Series("Jailbreak", tr.Select(w => (double?)w.Jailbreak).ToList(), 8),
                new Series("Prompt injection", tr.Select(w => (double?)w.Injection).ToList(), 2),
                new Series("Blocked resource", tr.Select(w => (double?)w.Blocked).ToList(), 7),
            ], Fmt.Count, "Security signals", stacked: false),
            "Jailbreak: a prompt written to make Copilot ignore its rules. Prompt injection: instructions hidden in something Copilot read (a file, an email, a web page) that try to steer it. " +
            $"Blocked resource: content a policy or permissions stopped Copilot from reading. Microsoft marks each one in the Copilot audit record. {TrendNote}"));

        var hours = await hoursTask;
        var days = new[] { "Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun" };
        var grid = new double?[7, 24];
        foreach (var h in hours) grid[h.Dow - 1, h.Hour] = h.N;
        Panels.Add(new("When jailbreak attempts and prompt injections happen",
            Chart.Heatmap(days, Enumerable.Range(0, 24).Select(h => h.ToString("00")).ToList(), grid, Fmt.Count, "Jailbreak attempts and prompt injections by weekday and hour",
                unit: "flagged interactions", unitOne: "flagged interaction", column: h => $"{h}:00 to {h}:59"),
            $"Interactions with a jailbreak attempt or prompt injection, by weekday and hour, in {DataZone}. Blocked resources are not included. A cluster outside working hours is worth a look."));

        var apps = await appsTask;
        Panels.Add(new("By app",
            Chart.Bars(apps.OrderByDescending(a => a.Jb + a.Inj + a.Blk).Take(12)
                .Select(a => new Bar(a.App, a.Jb + a.Inj + a.Blk, Note: $"jailbreak {a.Jb:#,0}, injection {a.Inj:#,0}, blocked {a.Blk:#,0}", Slot: 7)).ToList(), Fmt.Count, "Security signals by app"),
            "The 12 apps with the most security signals in the period. Hover for each kind.", "half"));

        var units = FoldSmall(await unitsTask, u => u.People, r => Metrics.Combine(r, "Smaller groups"), out var folded)
            .Where(u => u.Interactions > 0).OrderByDescending(u => u.Flags * 1000.0 / u.Interactions).Take(15).ToList();
        Panels.Add(new("By unit, per 1,000 interactions",
            Chart.Bars(units.Select(u => new Bar(u.Name, u.Flags * 1000.0 / u.Interactions, u.Drillable ? Ctx.Url(Request.Path, org: u.Unit) : null,
                $"{u.Flags:#,0} signals in {u.Interactions:#,0} interactions", 7)).ToList(), Fmt.Decimal1, "Security signals per 1,000 interactions, by unit"),
            "All three signals together, per 1,000 interactions, so large and small units compare fairly. An interaction with two signals counts twice." + (folded > 0 ? " " + FoldNote(folded, "unit") : ""), "half"));

        /* The same rate at each point, unit by unit: Security › Security signals by unit was a page of its own for this one chart. */
        var shown = SmallGroups.Shown(await unitsTask, u => u.People, Ctx.MinGroupSize).Where(u => u.Interactions > 0).ToList();
        var series = (await seriesTask).ToDictionary(x => (x.Unit, x.Bucket));
        var buckets = Buckets();
        var rates = new double?[shown.Count, buckets.Count];
        for (var r = 0; r < shown.Count; r++)
            for (var c = 0; c < buckets.Count; c++)
                rates[r, c] = series.TryGetValue((shown[r].Unit, buckets[c].Key), out var p) && p.Interactions > 0 ? p.Flags * 1000.0 / p.Interactions : null;
        Panels.Add(new("By unit over time, per 1,000 interactions",
            Chart.Heatmap(shown.Select(u => u.Name).ToList(), Labels(), rates, Fmt.Decimal1, "Security signals per 1,000 interactions, by unit over time", unit: "signals per 1,000 interactions"),
            $"Jailbreak attempts, prompt injections and blocked resources together, per 1,000 interactions, for each part of the organization one level below. Darker is a higher rate. Units under {Ctx.MinGroupSize} people are left out. {TrendNote}"));
    }
}

public sealed class FlagsModel(PageServices s) : InsightsPage(s)
{
    public override string Title => "Flagged interactions";
    public override string Persona => "Security";
    public override string? RequiredRole => Roles.Security;
    public override string Intro => "Each interaction with a security signal (a jailbreak attempt, a prompt injection or a blocked resource), who had it and in which app, and its triage: New, Investigating, Closed or FalsePositive (not a real threat). Everyone is listed, accounts since disabled included, so an incident is not hidden when someone leaves. The record id finds the interaction in Purview Audit.";

    public static readonly string[] Statuses = ["New", "Investigating", "Closed", "FalsePositive"];
    static readonly Dictionary<string, (string Label, string Sql)> Triage = new()
    {
        ["open"] = ("Open", "ISNULL(fc.Status, 'New') IN ('New', 'Investigating')"),
        ["new"] = ("New", "ISNULL(fc.Status, 'New') = 'New'"),
        ["investigating"] = ("Investigating", "fc.Status = 'Investigating'"),
        ["done"] = ("Closed or false positive", "fc.Status IN ('Closed', 'FalsePositive')"),
        ["any"] = ("Any", "1 = 1"),
    };

    static readonly Dictionary<string, string> Sorts = new()
    {
        ["new"] = "f.ActivityHour DESC, f.RecordId", ["old"] = "f.ActivityHour, f.RecordId", ["name"] = "du.DisplayName, f.ActivityHour DESC",
        ["dept"] = "du.Department, f.ActivityHour DESC",
        ["upn"] = "du.UserPrincipalName, f.ActivityHour DESC, f.RecordId", ["app"] = "a.App, f.ActivityHour DESC, f.RecordId",
        ["agent"] = "CASE WHEN ag.AgentSource = N'(no agent)' THEN 1 ELSE 0 END, ag.AgentName, f.ActivityHour DESC, f.RecordId",
        ["signals"] = "f.JailbreakDetected DESC, f.PromptInjectionDetected DESC, f.HasBlockedResource DESC, f.ActivityHour DESC, f.RecordId",
        ["record"] = "f.RecordId",
        /* The select list's Owner: fc.Owner, or NULL before the triage table exists. */
        ["owner"] = "Owner, f.ActivityHour DESC, f.RecordId",
    };
    static readonly Dictionary<string, string> SortLabels = new() { ["new"] = "Newest", ["old"] = "Oldest", ["name"] = "Name", ["dept"] = "Department" };
    static readonly Dictionary<string, (string Label, string Sql)> Kinds = new()
    {
        ["all"] = ("All signals", "(f.JailbreakDetected = 1 OR f.PromptInjectionDetected = 1 OR f.HasBlockedResource = 1)"),
        ["jailbreak"] = ("Jailbreak", "f.JailbreakDetected = 1"),
        ["injection"] = ("Prompt injection", "f.PromptInjectionDetected = 1"),
        ["blocked"] = ("Blocked resource", "f.HasBlockedResource = 1"),
    };

    sealed class Row
    {
        public DateTime ActivityHour { get; set; }
        public string? DisplayName { get; set; }
        public string UserPrincipalName { get; set; } = "";
        public string? Department { get; set; }
        public string App { get; set; } = "";
        public string? AgentName { get; set; }
        public bool JailbreakDetected { get; set; }
        public bool PromptInjectionDetected { get; set; }
        public bool HasBlockedResource { get; set; }
        public string RecordId { get; set; } = "";
        public string? Status { get; set; }
        public string? Owner { get; set; }
        public string? Note { get; set; }
        public int TotalRows { get; set; }
    }

    protected override async Task BuildAsync()
    {
        ShowDone();
        var kind = Request.Query["kind"].FirstOrDefault() is { } k && Kinds.ContainsKey(k) ? k : "all";
        var cases = await HttpContext.RequestServices.GetRequiredService<CopilotInsights.Data.OpsData>().HasAsync("dash.FlagCase");
        var triage = cases && Request.Query["status"].FirstOrDefault() is { } st && Triage.ContainsKey(st) ? st : cases ? "open" : "any";
        var pg = new Paging(Request, Sorts, "new");
        /* The flagged interactions once into #f, for the list and the open count both, from the refresh's own
           list of them: reading RecordId out of the fact costs a pass over its whole column, 4 seconds for a
           year of 40 million. Before the first refresh with 2.4.7 that list does not exist, and the fact is read. */
        var flagged = await HttpContext.RequestServices.GetRequiredService<CopilotInsights.Data.OpsData>().HasAsync("dash.FlaggedInteraction")
            && await Db.ScalarAsync<int>("SELECT CASE WHEN EXISTS (SELECT 1 FROM dash.FlaggedInteraction) THEN 1 ELSE 0 END") == 1;
        /* Sorted by something the flag itself holds, with nothing searched for (the page as it opens): the page's 50 rows
           are picked first, and only they are joined to the people, apps and agents. Joining every flag of a
           year to them before picking 50 took 1.5 seconds at 120 million interactions. Sorted or searched by a name,
           every flag needs its names first, as before. */
        var late = pg.Like is null && pg.SortKey is "new" or "old" or "signals" or "record";
        var list = late
            ? $@"
WITH pick AS (
    SELECT f.RecordId, f.ActivityHour, f.UserKey, f.AppKey, f.AgentKey, f.JailbreakDetected, f.PromptInjectionDetected, f.HasBlockedResource,
           {(cases ? "fc.Status, fc.Owner, fc.Note" : "CAST(NULL AS VARCHAR(20)) AS Status, CAST(NULL AS NVARCHAR(256)) AS Owner, CAST(NULL AS NVARCHAR(2000)) AS Note")}, COUNT(*) OVER () AS TotalRows
    FROM #f AS f
    {(cases ? "LEFT JOIN dash.FlagCase AS fc ON fc.RecordId = f.RecordId" : "")}
    WHERE {Kinds[kind].Sql} AND {(cases ? Triage[triage].Sql : "1 = 1")}
    {pg.OrderAndPage()})
SELECT f.ActivityHour, du.DisplayName, du.UserPrincipalName, du.Department, a.App,
       CASE WHEN ag.AgentSource = N'(no agent)' THEN NULL ELSE ag.AgentName END AS AgentName,
       f.JailbreakDetected, f.PromptInjectionDetected, f.HasBlockedResource, f.RecordId, f.Status, f.Owner, f.Note, f.TotalRows
FROM pick AS f
JOIN bi.DimUser AS du ON du.UserKey = f.UserKey
JOIN bi.DimApp AS a ON a.AppKey = f.AppKey
JOIN dash.Agent AS ag ON ag.AgentKey = f.AgentKey
ORDER BY {pg.OrderBy};"
            : $@"
SELECT f.ActivityHour, du.DisplayName, du.UserPrincipalName, du.Department, a.App,
       CASE WHEN ag.AgentSource = N'(no agent)' THEN NULL ELSE ag.AgentName END AS AgentName,
       f.JailbreakDetected, f.PromptInjectionDetected, f.HasBlockedResource, f.RecordId,
       {(cases ? "fc.Status, fc.Owner, fc.Note" : "NULL AS Status, NULL AS Owner, NULL AS Note")}, COUNT(*) OVER () AS TotalRows
FROM #f AS f
JOIN bi.DimUser AS du ON du.UserKey = f.UserKey
JOIN bi.DimApp AS a ON a.AppKey = f.AppKey
JOIN dash.Agent AS ag ON ag.AgentKey = f.AgentKey
{(cases ? "LEFT JOIN dash.FlagCase AS fc ON fc.RecordId = f.RecordId" : "")}
WHERE {Kinds[kind].Sql} AND {(cases ? Triage[triage].Sql : "1 = 1")}
  AND (@q IS NULL OR du.DisplayName LIKE @q OR du.UserPrincipalName LIKE @q OR du.Department LIKE @q)
{pg.OrderAndPage()};";
        var (rows, openCount) = await Db.QueryTwoAsync<Row, int>($@"
SELECT f.RecordId, f.ActivityHour, f.UserKey, f.AppKey, f.AgentKey, f.JailbreakDetected, f.PromptInjectionDetected, f.HasBlockedResource
INTO #f
FROM {(flagged ? "dash.FlaggedInteraction" : "bi.FactInteraction")} AS f {Ctx.Scope("f.UserKey")}
WHERE f.DateKey BETWEEN @k0 AND @k1 AND {Kinds["all"].Sql};{list}
{(cases ? $"SELECT COUNT(*) FROM #f AS f LEFT JOIN dash.FlagCase AS fc ON fc.RecordId = f.RecordId WHERE {Triage["open"].Sql};" : "SELECT 0;")}", Ctx.Args(new { q = pg.Like }));
        pg.Total = rows.FirstOrDefault()?.TotalRows ?? 0;
        var t = new Table
        {
            Headers = ["When (hour)", "Name", "Sign-in", "Department", "App", "Agent", "Security signals", "Record id", "Triage", "Owner", "Note"],
            EmptyText = "No flagged interactions in the period.",
            /* Triage and Note are the inline form. */
            Server = new(pg, Request, ["new", "name", "upn", "dept", "app", "agent", "signals", "record", null, "owner", null], new HashSet<int> { 0 }),
        };
        var ownerName = await NamesAsync(rows.Select(r => r.Owner));   // Who triaged, by name
        foreach (var r in rows)
        {
            var flags = string.Join(", ", new[] { r.JailbreakDetected ? "Jailbreak" : null, r.PromptInjectionDetected ? "Injection" : null, r.HasBlockedResource ? "Blocked" : null }.Where(x => x is not null));
            var status = r.Status ?? "New";
            object? triageCell = !cases ? null : pg.ForExport ? status : new HtmlString(InlineForm("triage", Hidden("recordId", r.RecordId) +
                $"<select name=\"status\" aria-label=\"Triage\">{string.Concat(Statuses.Select(x => $"<option{(x == status ? " selected" : "")}>{x}</option>"))}</select>" +
                $"<input type=\"text\" name=\"note\" value=\"{E(r.Note)}\" size=\"14\" maxlength=\"2000\" aria-label=\"Note\"/>" + Button("Save", quiet: true)));
            t.Rows.Add([r.ActivityHour, r.DisplayName, r.UserPrincipalName, r.Department, r.App, r.AgentName,
                        new HtmlString($"<span class=\"flag\">{System.Net.WebUtility.HtmlEncode(flags)}</span>"), r.RecordId, triageCell, ownerName(r.Owner), pg.ForExport ? r.Note : null]);
        }
        CsvTable = t;
        Kpis.Add(new(Kinds[kind].Label, Num(pg.Total), "flagged interactions in the period", pg.Total > 0 ? "crit" : null));
        var tabs = string.Join(" · ", Kinds.Select(kv =>
            $"<a href=\"{System.Net.WebUtility.HtmlEncode(Ctx.Url(Request.Path, extra: "kind=" + kv.Key))}\"{(kv.Key == kind ? " class=\"on\"" : "")}>{kv.Value.Label}</a>"));
        var tri = !cases ? "" : "<p class=\"sub\">Triage: " + string.Join(" · ", Triage.Select(kv =>
            $"<a href=\"{System.Net.WebUtility.HtmlEncode(Ctx.Url(Request.Path, extra: $"kind={kind}&status={kv.Key}"))}\"{(kv.Key == triage ? " class=\"on\"" : "")}>{kv.Value.Label}</a>")) + "</p>";
        if (cases)
        {
            var open = openCount.Single();
            Kpis.Add(new("Open flags", Num(open), "triage New or Investigating, in the period", open > 0 ? "warn" : null));
        }
        Panels.Add(new("Interactions", new HtmlString($"<p class=\"sub\">Show: {tabs}</p>" + tri + pg.Controls(Request, SortLabels).Value + t.Render().Value),
            $"When: the hour of the interaction, in {DataZone}. Open is New and Investigating together. Saving a triage makes you its owner and records the change, with the time, in the audit log.", "full", CsvUrl()));
    }

    /// <summary>Security's triage of one flagged interaction: status and a note, owned by whoever saved it.</summary>
    public Task<IActionResult> OnPostTriageAsync(string? recordId, string? status, string? note) => RoleChangeAsync(Roles.Security, async () =>
    {
        if (status is null || !Statuses.Contains(status)) throw new ArgumentException("choose a status.");
        if (string.IsNullOrEmpty(recordId) || recordId.Length > 64) throw new ArgumentException("no record.");
        if (await Db.ScalarAsync<int>($@"SELECT COUNT(*) FROM bi.FactInteraction AS f {Ctx.Scope("f.UserKey")}
WHERE f.RecordId = @recordId AND (f.JailbreakDetected = 1 OR f.PromptInjectionDetected = 1 OR f.HasBlockedResource = 1);", Ctx.Args(new { recordId })) == 0)
            throw new ArgumentException("that record is not a flagged interaction among the people you can see.");
        note = string.IsNullOrWhiteSpace(note) ? null : note.Trim()[..Math.Min(2000, note.Trim().Length)];
        var by = Viewer.Upn ?? Viewer.WindowsName;
        await Db.ExecuteAsync(@"
UPDATE dash.FlagCase SET Status = @status, Note = @note, Owner = @by, UpdatedBy = @by, UpdatedUtc = SYSUTCDATETIME() WHERE RecordId = @recordId;
IF @@ROWCOUNT = 0 INSERT INTO dash.FlagCase (RecordId, Status, Owner, Note, UpdatedBy) VALUES (@recordId, @status, @by, @note, @by);", new { recordId, status, note, by });
        return $"Record {recordId}: {status}.";
    });
}
