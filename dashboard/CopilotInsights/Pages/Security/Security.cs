using CopilotInsights.Charts;
using CopilotInsights.Data;
using CopilotInsights.Infrastructure;
using Microsoft.AspNetCore.Html;

namespace CopilotInsights.Pages.Security;

public sealed class IndexModel(PageServices s, Metrics m) : InsightsPage(s)
{
    public override string Title => "Signals";
    public override string Persona => "Security";
    public override string Intro => "Jailbreak attempts, prompt injection in content Copilot read, and resources Copilot was blocked from: how many, when and where. Aggregates only.";

    protected override async Task BuildAsync()
    {
        var tr = await m.TrendAsync(Ctx);
        long jb = tr.Sum(w => w.Jailbreak), inj = tr.Sum(w => w.Injection), blk = tr.Sum(w => w.Blocked), all = tr.Sum(w => w.Interactions);
        var people = await Db.ScalarAsync<int>($@"
SELECT COUNT(DISTINCT ud.UserKey) FROM dash.UserDay AS ud {Ctx.Scope("ud.UserKey")}
WHERE ud.DateKey BETWEEN @k0 AND @k1 AND (ud.Jailbreak > 0 OR ud.PromptInjection > 0);", Ctx.Args());
        Kpis.Add(new("Jailbreak attempts", Num(jb), all > 0 ? $"{jb * 1000.0 / all:0.00} per 1,000 interactions" : null, jb > 0 ? "crit" : null));
        Kpis.Add(new("Prompt injection detected", Num(inj), all > 0 ? $"{inj * 1000.0 / all:0.00} per 1,000 interactions" : null, inj > 0 ? "crit" : null));
        Kpis.Add(new("Blocked resources", Num(blk), "interactions where access to content was blocked or restricted"));
        Kpis.Add(new("People involved", Num(people), "with a jailbreak or injection flag"));

        Panels.Add(new("Signals over time",
            Chart.Columns(Labels(), [
                new Series("Jailbreak", tr.Select(w => (double?)w.Jailbreak).ToList(), 8),
                new Series("Prompt injection", tr.Select(w => (double?)w.Injection).ToList(), 2),
                new Series("Blocked resource", tr.Select(w => (double?)w.Blocked).ToList(), 7),
            ], Fmt.Count, "Security signals", stacked: false), TrendNote));

        var hours = await Db.QueryAsync<(int Dow, int Hour, int N)>($@"
SELECT d.DayOfWeekNumber, f.HourOfDay, COUNT(*)
FROM bi.FactInteraction AS f {Ctx.Scope("f.UserKey")}
JOIN bi.DimDate AS d ON d.DateKey = f.DateKey
WHERE f.DateKey BETWEEN @k0 AND @k1 AND (f.JailbreakDetected = 1 OR f.PromptInjectionDetected = 1)
GROUP BY d.DayOfWeekNumber, f.HourOfDay;", Ctx.Args());
        var days = new[] { "Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun" };
        var grid = new double?[7, 24];
        foreach (var h in hours) grid[h.Dow - 1, h.Hour] = h.N;
        Panels.Add(new("When jailbreak and injection happen",
            Chart.Heatmap(days, Enumerable.Range(0, 24).Select(h => h.ToString("00")).ToList(), grid, Fmt.Count, "Flags by weekday and hour"),
            "Flagged interactions by weekday and hour, in the reporting time zone. Out-of-hours clusters are worth a look."));

        var apps = await Db.QueryAsync<(string App, int Jb, int Inj, int Blk)>($@"
SELECT a.App, SUM(CAST(f.JailbreakDetected AS INT)), SUM(CAST(f.PromptInjectionDetected AS INT)), SUM(CAST(f.HasBlockedResource AS INT))
FROM bi.FactInteraction AS f {Ctx.Scope("f.UserKey")}
JOIN bi.DimApp AS a ON a.AppKey = f.AppKey
WHERE f.DateKey BETWEEN @k0 AND @k1 AND (f.JailbreakDetected = 1 OR f.PromptInjectionDetected = 1 OR f.HasBlockedResource = 1)
GROUP BY a.App;", Ctx.Args());
        Panels.Add(new("By app",
            Chart.Bars(apps.OrderByDescending(a => a.Jb + a.Inj + a.Blk).Take(12)
                .Select(a => new Bar(a.App, a.Jb + a.Inj + a.Blk, Note: $"jailbreak {a.Jb:#,0}, injection {a.Inj:#,0}, blocked {a.Blk:#,0}", Slot: 7)).ToList(), Fmt.Count, "Signals by app"),
            null, "half"));

        var units = FoldSmall(await m.UnitsAsync(Ctx), u => u.People, r => Metrics.Combine(r, "Smaller groups"), out var folded)
            .Where(u => u.Interactions > 0).OrderByDescending(u => u.Flags * 1000.0 / u.Interactions).Take(15).ToList();
        Panels.Add(new("By unit, per 1,000 interactions",
            Chart.Bars(units.Select(u => new Bar(u.Name, u.Flags * 1000.0 / u.Interactions, u.Drillable ? Ctx.Url(Request.Path, org: u.Unit) : null,
                $"{u.Flags:#,0} signals in {u.Interactions:#,0} interactions", 7)).ToList(), Fmt.Decimal1, "Signal rate by unit"),
            "All three signals together, as a rate so large and small units compare fairly." + (folded > 0 ? $" {folded} small unit(s) combined." : ""), "half"));
    }
}

public sealed class FlagsModel(PageServices s) : InsightsPage(s)
{
    public override string Title => "Flagged interactions";
    public override string Persona => "Security";
    public override string? RequiredRole => Roles.Security;
    public override string Intro => "Each interaction with a jailbreak, prompt-injection or blocked-resource flag, with who and where. The record id finds it in Purview.";

    static readonly Dictionary<string, string> Sorts = new()
    {
        ["new"] = "f.ActivityHour DESC, f.RecordId", ["old"] = "f.ActivityHour, f.RecordId", ["name"] = "du.DisplayName, f.ActivityHour DESC",
        ["dept"] = "du.Department, f.ActivityHour DESC",
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
        public int TotalRows { get; set; }
    }

    protected override async Task BuildAsync()
    {
        var kind = Request.Query["kind"].FirstOrDefault() is { } k && Kinds.ContainsKey(k) ? k : "all";
        var pg = new Paging(Request, Sorts, "new");
        var rows = await Db.QueryAsync<Row>($@"
SELECT f.ActivityHour, du.DisplayName, du.UserPrincipalName, du.Department, a.App,
       CASE WHEN ag.AgentSource = N'(no agent)' THEN NULL ELSE ag.AgentName END AS AgentName,
       f.JailbreakDetected, f.PromptInjectionDetected, f.HasBlockedResource, f.RecordId, COUNT(*) OVER () AS TotalRows
FROM bi.FactInteraction AS f {Ctx.Scope("f.UserKey")}
JOIN bi.DimUser AS du ON du.UserKey = f.UserKey
JOIN bi.DimApp AS a ON a.AppKey = f.AppKey
JOIN dash.Agent AS ag ON ag.AgentKey = f.AgentKey
WHERE f.DateKey BETWEEN @k0 AND @k1 AND {Kinds[kind].Sql}
  AND (@q IS NULL OR du.DisplayName LIKE @q OR du.UserPrincipalName LIKE @q OR du.Department LIKE @q)
{pg.OrderAndPage()};", Ctx.Args(new { q = pg.Like }));
        pg.Total = rows.FirstOrDefault()?.TotalRows ?? 0;
        var t = new Table
        {
            Headers = ["When (hour)", "Name", "Sign-in", "Department", "App", "Agent", "Signals", "Record id"],
            EmptyText = "No flagged interactions in the period.",
        };
        foreach (var r in rows)
        {
            var flags = string.Join(", ", new[] { r.JailbreakDetected ? "Jailbreak" : null, r.PromptInjectionDetected ? "Injection" : null, r.HasBlockedResource ? "Blocked" : null }.Where(x => x is not null));
            t.Rows.Add([r.ActivityHour, r.DisplayName, r.UserPrincipalName, r.Department, r.App, r.AgentName,
                        new HtmlString($"<span class=\"flag\">{System.Net.WebUtility.HtmlEncode(flags)}</span>"), r.RecordId]);
        }
        CsvTable = t;
        Kpis.Add(new(Kinds[kind].Label, Num(pg.Total), "flagged interactions in the period", pg.Total > 0 ? "crit" : null));
        var tabs = string.Join(" · ", Kinds.Select(kv =>
            $"<a href=\"{System.Net.WebUtility.HtmlEncode(Ctx.Url(Request.Path, extra: "kind=" + kv.Key))}\"{(kv.Key == kind ? " class=\"on\"" : "")}>{kv.Value.Label}</a>"));
        Panels.Add(new("Interactions", new HtmlString($"<p class=\"sub\">Show: {tabs}</p>" + pg.Controls(Request, SortLabels).Value + t.Render().Value),
            "Times are the hour of the interaction in the reporting time zone.", "full", CsvUrl()));
    }
}
