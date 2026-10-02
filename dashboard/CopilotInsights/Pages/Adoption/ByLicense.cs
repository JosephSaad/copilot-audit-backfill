using System.Net;
using System.Text;
using CopilotInsights.Charts;
using CopilotInsights.Infrastructure;
using Microsoft.AspNetCore.Html;

namespace CopilotInsights.Pages.Adoption;

/// <summary>
/// Copilot use by license type (2.4.6): the people of one type (licensed, unlicensed or all) in the part of the
/// organization chosen, how many are active, in which apps, what Copilot read for them, how long their
/// conversations ran, and on how many days. Named lists, so it needs the Adoption role.
/// </summary>
public sealed class ByLicenseModel(PageServices s) : InsightsPage(s)
{
    public override string Title => "Usage by license";
    public override string Persona => "Adoption";
    public override string? RequiredRole => Roles.Adoption;
    public override string Intro => "How the people of one license type use Copilot: who is active, in which apps, for how long and on how many days. " +
        "Choose the license type on the page and the part of the organization in the filter bar. People are enabled accounts in the directory; licensed means holding a Copilot license on the last day of the period.";

    /// <summary>The most sessions a download holds: a year of a large organization runs to millions.</summary>
    const int ExportMax = 100_000;
    const int Top = 15;

    public static readonly IReadOnlyList<(string Key, string Name)> Licenses = [("licensed", "Licensed"), ("unlicensed", "Unlicensed"), ("all", "All")];

    /// <summary>The license type asked for (lic=licensed|unlicensed|all), licensed when it is anything else.</summary>
    public static string License(string? lic) => Licenses.Any(l => l.Key == lic) ? lic! : "licensed";

    /// <summary>The condition on a bi.DimUser alias du for the license type: a Copilot license on the period's last day (@k1), or none.</summary>
    public static string LicenseSql(string lic) => lic switch
    {
        "licensed" => " AND EXISTS (SELECT 1 FROM bi.FactLicensedDay AS ld WHERE ld.DateKey = @k1 AND ld.UserKey = du.UserKey)",
        "unlicensed" => " AND NOT EXISTS (SELECT 1 FROM bi.FactLicensedDay AS ld WHERE ld.DateKey = @k1 AND ld.UserKey = du.UserKey)",
        _ => "",
    };

    public static readonly string[] BandNames = ["Not active", "1 day", "2 to 4 days", "5 to 9 days", "10 to 14 days", "15 days or more"];

    /// <summary>The active-days band a person falls in: 0 for none, then 1, 2-4, 5-9, 10-14 and 15 or more.</summary>
    public static int Band(int days) => days <= 0 ? 0 : days == 1 ? 1 : days <= 4 ? 2 : days <= 9 ? 3 : days <= 14 ? 4 : 5;

    /// <summary>The app groups with an average-minutes card, by their names in the audit data (Office, BizChat and Edge are Copilot Chat).</summary>
    public static readonly (string Name, string[] Apps)[] CardApps =
    [
        ("Outlook", ["Outlook"]),
        ("Teams", ["Teams", "Microsoft Teams"]),
        ("Copilot Chat", ["Copilot Chat", "BizChat", "Office", "OfficeCopilotSearchAnswer", "Edge"]),
    ];

    static readonly Dictionary<string, string> Sorts = new()
    {
        ["spent"] = "x.SpanMinutes DESC, x.SessionStart DESC, x.SessionKey",
        ["prompts"] = "x.PromptCount DESC, x.SpanMinutes DESC, x.SessionKey",
        ["app"] = "x.SortText, x.SpanMinutes DESC, x.SessionKey",
        ["name"] = "x.SortText, x.SessionStart DESC, x.SessionKey",
        ["upn"] = "x.SortText, x.SessionStart DESC, x.SessionKey",
        ["started"] = "x.SessionStart DESC, x.SessionKey",
    };
    static readonly Dictionary<string, string> SortLabels = new() { ["spent"] = "Longest first", ["started"] = "Most recent", ["name"] = "Name" };

    sealed class Person
    {
        public int Low { get; set; }
        public string? DisplayName { get; set; }
        public string UserPrincipalName { get; set; } = "";
        public string? Department { get; set; }
        public long I { get; set; }
        public int D { get; set; }
    }

    sealed class Minutes
    {
        public string Kind { get; set; } = "";
        public int Key { get; set; }
        public string? Name { get; set; }
        public string? Upn { get; set; }
        public int People { get; set; }
        public decimal Total { get; set; }
        public long Sessions { get; set; }
    }

    sealed class Session
    {
        public decimal SpanMinutes { get; set; }
        public int PromptCount { get; set; }
        public DateTime SessionStart { get; set; }
        public string App { get; set; } = "";
        public string? DisplayName { get; set; }
        public string UserPrincipalName { get; set; } = "";
    }

    protected override async Task BuildAsync()
    {
        var lic = License(Request.Query["lic"].FirstOrDefault());
        var licName = Licenses.First(l => l.Key == lic).Name;
        var licSql = LicenseSql(lic);
        /* The people of the license type in the viewer's scope: every query below starts from them, so the scope holds throughout. */
        var pop = $@"p AS (SELECT du.UserKey FROM bi.DimUser AS du {Ctx.Scope("du.UserKey")}
           WHERE du.DirectoryStatus = N'Found' AND du.AccountEnabled = 1{licSql})";
        var pg = new Paging(Request, Sorts, "spent");
        var sortText = pg.SortKey switch
        {
            "name" => "ps.DisplayName",
            "upn" => "ps.UserPrincipalName",
            "app" => "(SELECT COALESCE(vn.Name, a.App) FROM bi.DimApp AS a LEFT JOIN dash.ValueName AS vn ON vn.ColumnId = 2 AND vn.Value = a.App WHERE a.AppKey = fs.AppKey)",
            _ => "CAST(NULL AS NVARCHAR(1))",
        };
        var page = pg.ForExport ? $"ORDER BY {pg.OrderBy} OFFSET 0 ROWS FETCH NEXT {ExportMax} ROWS ONLY" : pg.OrderAndPage();
        var cards = string.Join("\n", CardApps.Select((c, i) => $"WHEN a.App IN ({string.Join(", ", c.Apps.Select(x => $"N'{x}'"))}) THEN {i + 1}"));

        /* Independent statements, each on its own connection, run side by side: the page waits for the slowest, not the sum. */
        var peopleTask = Db.QueryTwoAsync<(int Days, int People), Person>($@"
WITH {pop}
SELECT p.UserKey, ISNULL(SUM(CAST(ud.Interactions AS BIGINT)), 0) AS I, COUNT(CASE WHEN ud.Interactions > 0 THEN 1 END) AS D
INTO #u
FROM p LEFT JOIN dash.UserDay AS ud ON ud.UserKey = p.UserKey AND ud.DateKey BETWEEN @k0 AND @k1
GROUP BY p.UserKey;
SELECT D, COUNT(*) FROM #u GROUP BY D;
SELECT x.Low, du.DisplayName, du.UserPrincipalName, du.Department, x.I, x.D
FROM (SELECT TOP ({Top}) 0 AS Low, UserKey, I, D FROM #u WHERE I > 0 ORDER BY I DESC, UserKey) AS x JOIN bi.DimUser AS du ON du.UserKey = x.UserKey
UNION ALL
SELECT x.Low, du.DisplayName, du.UserPrincipalName, du.Department, x.I, x.D
FROM (SELECT TOP ({Top}) 1 AS Low, u.UserKey, u.I, u.D FROM #u AS u JOIN bi.DimUser AS n ON n.UserKey = u.UserKey ORDER BY u.I, n.DisplayName, u.UserKey) AS x
JOIN bi.DimUser AS du ON du.UserKey = x.UserKey;", Ctx.Args());

        var appsTask = Db.QueryAsync<(DateTime Bucket, string App, long N)>($@"
WITH {pop},
x AS (SELECT f.DateKey, f.AppKey, COUNT_BIG(*) AS N FROM bi.FactInteraction AS f JOIN p ON p.UserKey = f.UserKey
      WHERE f.DateKey BETWEEN @k0 AND @k1 GROUP BY f.DateKey, f.AppKey)
SELECT {Ctx.BucketSql("d")}, COALESCE(vn.Name, a.App), SUM(x.N)
FROM x JOIN bi.DimDate AS d ON d.DateKey = x.DateKey JOIN bi.DimApp AS a ON a.AppKey = x.AppKey
LEFT JOIN dash.ValueName AS vn ON vn.ColumnId = 2 AND vn.Value = a.App
GROUP BY {Ctx.BucketSql("d")}, COALESCE(vn.Name, a.App);", Ctx.Args());

        /* By file type (2.4.6: docx, xlsx, pptx ... and, for what is not a file, its kind), or by kind before that column exists. */
        var fileTypes = await Db.ScalarAsync<int>("SELECT CASE WHEN COL_LENGTH(N'bi.FactAccessedResource', N'FileTypeKey') IS NULL THEN 0 ELSE 1 END") == 1;
        var sourcesTask = Db.QueryAsync<(string Type, long N)>(fileTypes ? $@"
WITH {pop},
x AS (SELECT f.FileTypeKey, COUNT_BIG(*) AS N FROM bi.FactAccessedResource AS f JOIN p ON p.UserKey = f.UserKey
      WHERE f.DateKey BETWEEN @k0 AND @k1 GROUP BY f.FileTypeKey)
SELECT t.FileType, x.N FROM x JOIN bi.DimFileType AS t ON t.FileTypeKey = x.FileTypeKey;" : $@"
WITH {pop},
x AS (SELECT f.ResourceTypeKey, COUNT_BIG(*) AS N FROM bi.FactAccessedResource AS f JOIN p ON p.UserKey = f.UserKey
      WHERE f.DateKey BETWEEN @k0 AND @k1 GROUP BY f.ResourceTypeKey)
SELECT COALESCE(vn.Name, t.ResourceType), SUM(x.N) FROM x JOIN bi.DimResourceType AS t ON t.ResourceTypeKey = x.ResourceTypeKey
LEFT JOIN dash.ValueName AS vn ON vn.ColumnId = 31 AND vn.Value = t.ResourceType
GROUP BY COALESCE(vn.Name, t.ResourceType);", Ctx.Args());

        /* Minutes per person and app once, then by app, by card and by person from that. Sessions counts those with any
           minutes, the list's length when it has no search. */
        var minutesTask = Db.QueryAsync<Minutes>($@"
WITH {pop}
SELECT fs.AppKey, fs.UserKey, SUM(fs.SpanMinutes) AS M, COUNT_BIG(CASE WHEN fs.SpanMinutes > 0 THEN 1 END) AS N
INTO #s
FROM bi.FactSession AS fs JOIN p ON p.UserKey = fs.UserKey
WHERE fs.DateKey BETWEEN @k0 AND @k1
GROUP BY fs.AppKey, fs.UserKey;
SELECT N'a' AS Kind, a.AppKey AS [Key], COALESCE(vn.Name, a.App) AS Name, CAST(NULL AS NVARCHAR(512)) AS Upn, COUNT(*) AS People, SUM(s.M) AS Total, SUM(s.N) AS Sessions
FROM #s AS s JOIN bi.DimApp AS a ON a.AppKey = s.AppKey LEFT JOIN dash.ValueName AS vn ON vn.ColumnId = 2 AND vn.Value = a.App
GROUP BY a.AppKey, COALESCE(vn.Name, a.App)
UNION ALL
SELECT N'g', g.G, NULL, NULL, COUNT(*), SUM(g.M), 0
FROM (SELECT CASE {cards} END AS G, s.UserKey, SUM(s.M) AS M
      FROM #s AS s JOIN bi.DimApp AS a ON a.AppKey = s.AppKey
      GROUP BY CASE {cards} END, s.UserKey) AS g
WHERE g.G IS NOT NULL GROUP BY g.G
UNION ALL
SELECT N'u', 0, du.DisplayName, du.UserPrincipalName, 1, x.M, 0
FROM (SELECT TOP ({Top}) UserKey, SUM(M) AS M FROM #s GROUP BY UserKey HAVING SUM(M) > 0 ORDER BY SUM(M) DESC, UserKey) AS x
JOIN bi.DimUser AS du ON du.UserKey = x.UserKey;", Ctx.Args());

        /* The conversations, sorted on their own narrow columns first and named afterwards: sorting millions of rows with
           their names took four seconds for a year, this one. */
        var search = $@"ps AS (SELECT du.UserKey, du.DisplayName, du.UserPrincipalName FROM bi.DimUser AS du {Ctx.Scope("du.UserKey")}
           WHERE du.DirectoryStatus = N'Found' AND du.AccountEnabled = 1{licSql}
             AND (@q IS NULL OR du.DisplayName LIKE @q OR du.UserPrincipalName LIKE @q OR du.Department LIKE @q))";
        var sessionsTask = Db.QueryAsync<Session>($@"
WITH {search},
s AS (SELECT fs.SessionKey, fs.SpanMinutes, fs.PromptCount, fs.SessionStart, fs.AppKey, fs.UserKey, {sortText} AS SortText
      FROM bi.FactSession AS fs JOIN ps ON ps.UserKey = fs.UserKey
      WHERE fs.DateKey BETWEEN @k0 AND @k1 AND fs.SpanMinutes > 0),
pageRows AS (SELECT x.* FROM s AS x {page})
SELECT x.SpanMinutes, x.PromptCount, x.SessionStart, COALESCE(vn.Name, a.App) AS App, du.DisplayName, du.UserPrincipalName
FROM pageRows AS x JOIN bi.DimUser AS du ON du.UserKey = x.UserKey
JOIN bi.DimApp AS a ON a.AppKey = x.AppKey LEFT JOIN dash.ValueName AS vn ON vn.ColumnId = 2 AND vn.Value = a.App
ORDER BY {pg.OrderBy};", Ctx.Args(new { q = pg.Like }));
        var countTask = pg.Search is null ? Task.FromResult<long?>(null) : Db.ScalarAsync<long?>($@"
WITH {search}
SELECT COUNT_BIG(*) FROM bi.FactSession AS fs JOIN ps ON ps.UserKey = fs.UserKey
WHERE fs.DateKey BETWEEN @k0 AND @k1 AND fs.SpanMinutes > 0;", Ctx.Args(new { q = pg.Like }));

        var factsTask = Db.QueryAsync<(DateTime? Refreshed, DateTime? Latest)>(@"
SELECT (SELECT MAX(EndedUtc) FROM dash.RefreshRun),
       ISNULL((SELECT MAX(f.CreatedUtc) FROM bi.FactInteraction AS f WHERE f.DateKey >= @k1), (SELECT MAX(f.CreatedUtc) FROM bi.FactInteraction AS f));", Ctx.Args());

        await Task.WhenAll(peopleTask, appsTask, sourcesTask, minutesTask, sessionsTask, countTask, factsTask);
        var (days, people) = peopleTask.Result;
        var apps = appsTask.Result;
        var sources = sourcesTask.Result;
        var minutes = minutesTask.Result;
        var sessions = sessionsTask.Result;
        var facts = factsTask.Result.FirstOrDefault();

        /* The license type, and how fresh the data is. */
        var choices = string.Join(" · ", Licenses.Select(l =>
            $"<a href=\"{WebUtility.HtmlEncode(Ctx.Url(Request.Path, extra: $"lic={l.Key}"))}\"{(l.Key == lic ? " class=\"on\"" : "")}>{l.Name}</a>"));
        Panels.Add(new("License type", new HtmlString(
            $"<p class=\"sub\">License: {choices}</p>" +
            $"<p class=\"sub\">Last database update: {E(When(facts.Refreshed))} · Latest audit record: {E(When(facts.Latest))}</p>"),
            "Licensed: holding a Copilot license on the last day of the period. Unlicensed: not holding one that day. " +
            "Last database update: when the dashboard's rollups last finished. Latest audit record: the newest Copilot interaction loaded."));

        /* KPIs: the people of the type, and how many used Copilot in the period. */
        var total = days.Sum(d => d.People);
        var active = days.Where(d => d.Days > 0).Sum(d => d.People);
        Kpis.Add(new(lic == "all" ? "People" : $"{licName} people", Num(total), $"enabled accounts in {Here}"));
        Kpis.Add(new("Active", Pct(Ratio(active, total)), $"{active:#,0} used Copilot in the period"));
        Kpis.Add(new("Inactive", Pct(Ratio(total - active, total)), $"{total - active:#,0} did not use it"));
        Kpis.Add(new("Active people", Num(active), "at least one interaction in the period"));
        Kpis.Add(new("Inactive people", Num(total - active), "no interaction in the period", total - active > 0 && lic == "licensed" ? "warn" : null));

        /* Apps: each one's share of the interactions. */
        var byApp = apps.GroupBy(a => a.App).Select(g => (App: g.Key, N: g.Sum(x => x.N))).OrderByDescending(a => a.N).ToList();
        var interactions = byApp.Sum(a => a.N);
        Panels.Add(new("Copilot use by app",
            Chart.Bars(byApp.Select(a => new Bar(a.App, Ratio(a.N, interactions), Note: $"{a.N:#,0} interactions", Shown: Num(a.N))).ToList(), Fmt.Percent, "Share of interactions by app"),
            $"Each app's share of the {interactions:#,0} interactions of {licName.ToLowerInvariant()} people in the period, with the count beside it. " +
            "Office, BizChat and Edge are Copilot Chat.", "half"));

        /* What Copilot read for them, by file type: the twelve most read, the rest together. */
        var read = sources.Sum(x => x.N);
        var ranked = sources.OrderByDescending(x => x.N).ToList();
        var shownSources = ranked.Take(12).Select(x => new Bar(x.Type, x.N, Note: Pct(Ratio(x.N, read)) + " of what Copilot read", Shown: Pct(Ratio(x.N, read)))).ToList();
        if (ranked.Count > 12) { var rest = ranked.Skip(12).Sum(x => x.N); shownSources.Add(new Bar($"{ranked.Count - 12:#,0} other types", rest, Note: Pct(Ratio(rest, read)) + " of what Copilot read", Slot: 2, Shown: Pct(Ratio(rest, read)))); }
        Panels.Add(new("Copilot use by source", Chart.Bars(shownSources, Fmt.Count, "Resources Copilot read, by file type"),
            fileTypes
                ? "The resources Copilot read to answer them: a file by its extension (docx, xlsx, pptx, csv ...), and anything else by its kind (a mail, a Teams message, a Stream video, a whiteboard, a web search). " +
                  "Resources read before 2.4.6 show as (not reported) until bi is refreshed in full once, which the 2.4.6 installer does."
                : "The resources Copilot read to answer them, by the kind the audit record gives. Install 2.4.6's scripts to split files by type.", "half"));

        /* The most and least active people of the type. */
        static string Who(Person p) => p.DisplayName ?? p.UserPrincipalName;
        static string Days(int d) => d == 1 ? "1 day" : $"{d:#,0} days";
        Panels.Add(new($"Highest active users: {licName}",
            Chart.Bars(people.Where(p => p.Low == 0).Select(p => new Bar(Who(p), p.I, Note: $"{p.UserPrincipalName} · {p.Department}", Shown: Days(p.D))).ToList(), Fmt.Count, "Most interactions by person"),
            $"The {Top} people with the most interactions in the period, and the days they used Copilot.", "half"));
        Panels.Add(new($"Lowest users: {licName}",
            Chart.Bars(people.Where(p => p.Low == 1).Select(p => new Bar(Who(p), p.I, Note: $"{p.UserPrincipalName} · {p.Department}", Shown: Days(p.D))).ToList(), Fmt.Count, "Fewest interactions by person"),
            $"The {Top} people with the fewest interactions in the period, those with none first, by name. Inactive people lists everyone at zero.", "half"));

        /* Minutes: the span of each conversation, first prompt to last. */
        const string minutesNote = "A conversation's minutes are its span from the first prompt to the last; a one-prompt conversation counts 0. " +
            "So the minutes are a floor, not the time spent in the app.";
        static string Hours(decimal m) => $"{m / 60m:#,0.0} h";
        var topMinutes = minutes.Where(m => m.Kind == "u").OrderByDescending(m => m.Total).ToList();
        Panels.Add(new("Highest minutes used",
            Chart.Bars(topMinutes.Select(m => new Bar(m.Name ?? m.Upn ?? "", (double)Math.Round(m.Total), Note: m.Upn, Shown: Hours(m.Total))).ToList(), Fmt.Count, "Most minutes by person"),
            $"The {Top} people whose conversations ran longest in the period, in minutes, with hours beside them. {minutesNote}", "half"));

        var sb = new StringBuilder("<div class=\"kpis\">");
        for (var i = 0; i < CardApps.Length; i++)
        {
            var g = minutes.FirstOrDefault(m => m.Kind == "g" && m.Key == i + 1);
            var avg = g is null || g.People == 0 ? (decimal?)null : g.Total / g.People;
            sb.Append($"<div class=\"kpi\"><div class=\"klabel\">Average minutes used: {E(CardApps[i].Name)}</div>" +
                      $"<div class=\"kvalue\">{(avg is null ? "—" : E(Hours(avg.Value)))}</div>" +
                      $"<div class=\"knote\">{(avg is null ? "nobody in the period" : $"{avg.Value:#,0} min a person · {g!.People:#,0} people")}</div></div>");
        }
        sb.Append("</div>");
        var byAppMinutes = minutes.Where(m => m.Kind == "a" && m.People > 0).OrderByDescending(m => m.Total / m.People).ToList();
        var appBars = Chart.Bars(byAppMinutes.Select(m => new Bar(m.Name ?? "", (double)Math.Round(m.Total / m.People), Note: $"{m.People:#,0} people · {Math.Round(m.Total):#,0} minutes in all",
            Shown: Hours(m.Total / m.People))).ToList(), Fmt.Count, "Average minutes a person by app");
        /* Drawn at a half panel's width, as _Report draws a chart that is a half panel's whole body. */
        sb.Append(appBars is ChartSvg cs ? cs.Render(440) : appBars.Value);
        Panels.Add(new("Total average minutes used (by app)", new HtmlString(sb.ToString()),
            "For each app, the minutes of the conversations that started in it, divided by the people who had one there, in the period. " +
            $"Copilot Chat includes Office, BizChat and Edge. {minutesNote}", "half"));

        pg.Total = (int)Math.Min(int.MaxValue, countTask.Result ?? minutes.Where(m => m.Kind == "a").Sum(m => m.Sessions));
        var t = new Table
        {
            Headers = ["Time spent (min)", "Prompts", "App", "Name", "Sign-in", "Started"],
            Numeric = new HashSet<int> { 0, 1 },
            EmptyText = "No conversation of more than one prompt in the period.",
            Server = new(pg, Request, ["spent", "prompts", "app", "name", "upn", "started"], new HashSet<int> { 0, 1, 5 }),
        };
        foreach (var r in sessions)
            t.Rows.Add([Math.Round(r.SpanMinutes, 1), r.PromptCount, r.App, r.DisplayName, r.UserPrincipalName, r.SessionStart]);
        CsvTable = t;
        Panels.Add(new("Minutes used (by user)", new HtmlString(pg.Controls(Request, SortLabels).Value + t.Render().Value),
            $"Each conversation of more than one prompt in the period, with the app of its first prompt; times are in {DataZone}. {minutesNote} " +
            $"The download holds up to {ExportMax:#,0} conversations, in the order chosen.", "full", CsvUrl()));

        /* The trend: interactions by app over the period, with each app's lowest, highest and mean point. */
        var buckets = Buckets();
        var shown = byApp.Take(byApp.Count > 8 ? 7 : 8).Select(a => a.App).ToList();
        double?[] Points(Func<string, bool> pick) =>
            buckets.Select(b => (double?)apps.Where(x => x.Bucket == b.Key && pick(x.App)).Sum(x => x.N)).ToArray();
        var series = shown.Select((app, i) => new Series(app, Points(a => a == app), i + 1)).ToList();
        if (byApp.Count > shown.Count) series.Add(new Series("Other apps", Points(a => !shown.Contains(a)), 8));
        var legend = new Table { Headers = ["App", "Lowest", "Highest", "Mean"], Numeric = new HashSet<int> { 1, 2, 3 } };
        foreach (var sr in series)
        {
            var v = sr.Values.Select(x => x ?? 0).ToList();
            legend.Rows.Add([sr.Name, (long)v.DefaultIfEmpty(0).Min(), (long)v.DefaultIfEmpty(0).Max(), Math.Round(v.DefaultIfEmpty(0).Average(), 1)]);
        }
        Panels.Add(new("Interaction trends by app",
            new HtmlString(Chart.Line(Labels(), series, Fmt.Count, "Interactions by app over the period").Value + legend.Render().Value),
            $"Interactions of {licName.ToLowerInvariant()} people in each app; the busiest apps are drawn and the rest combined. " +
            $"The table gives each line's lowest, highest and mean point. {TrendNote}"));

        /* Active days: how many people used Copilot on how many days of the period. */
        var bands = BandNames.Select((name, i) => (Name: name, People: days.Where(d => Band(d.Days) == i).Sum(d => d.People))).ToList();
        Panels.Add(new("Active days",
            Chart.Bars(bands.Select((b, i) => new Bar(b.Name, b.People, Slot: i == 0 ? 8 : 1, Shown: Pct(Ratio(b.People, total)))).ToList(), Fmt.Count, "People by days active"),
            $"The {licName.ToLowerInvariant()} people by the number of days in the period on which they used Copilot, with their share of all {total:#,0}.", "half"));
    }
}
