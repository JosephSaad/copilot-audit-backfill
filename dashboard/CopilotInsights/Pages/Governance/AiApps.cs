using CopilotInsights.Charts;
using CopilotInsights.Data;
using Microsoft.AspNetCore.Html;
using Microsoft.AspNetCore.Mvc;

namespace CopilotInsights.Pages.Governance;

/// <summary>
/// AI apps beyond Copilot: calls to Azure AI Foundry and Azure OpenAI from the organization's own apps, as
/// Purview records them (ConnectedAIAppInteraction), rolled up each night from the activity database into
/// audit.AIAppUserDay. By resource, line of business and environment, by whether the call carried a person's identity,
/// and over time; with what Azure charged for each resource where the Foundry loader is set up. Counts only: a person's
/// calls are counted, never listed, and a department under the minimum group size is folded away. The resources' line of
/// business, environment and owner are guessed from their names and set by administrators here.
/// </summary>
public sealed class AiAppsModel(PageServices s) : InsightsPage(s)
{
    public override string Title => "AI apps beyond Copilot";
    public override string Persona => "Governance";
    public override bool UsesTeam => false;
    public override string Intro => "Calls to Azure AI Foundry and Azure OpenAI from the organization's own apps, as Microsoft Purview records them: which resources, in which line of business and environment, how many carry a person's identity, and how they move over time. The whole organization's, whatever team is chosen.";

    sealed class ResRow
    {
        public string Resource { get; set; } = "";
        public string? LineOfBusiness { get; set; }
        public string? Environment { get; set; }
        public string? Owner { get; set; }
        public string? SetBy { get; set; }
        public long Calls { get; set; }
        public long Prompts { get; set; }
        public long Threads { get; set; }
        public long Failures { get; set; }
        public long Unattributed { get; set; }
        public int People { get; set; }
        public int Apps { get; set; }
        public DateTime? FirstSeen { get; set; }
        public decimal? Cost { get; set; }
    }

    protected override async Task BuildAsync()
    {
        ShowDone();
        if (await Db.ScalarAsync<int>("SELECT CASE WHEN OBJECT_ID(N'audit.AIAppUserDay', N'U') IS NULL THEN 0 WHEN EXISTS (SELECT 1 FROM audit.AIAppUserDay) THEN 2 ELSE 1 END;") is var state && state < 2)
        {
            Notice = state == 0
                ? "AI app calls cannot be shown yet. An administrator installs release 2.6.9 or later (Install-CopilotAudit.ps1), then sets up the activity database (docs/OPERATIONS.md, AI apps, Power BI and Power Apps)."
                : "No AI app calls are loaded. They come from the activity database. An administrator sets ActivityDatabase in the job server's settings, installs it with Install-CopilotAudit.ps1 -ActivityDatabase, and loads its history with Invoke-CopilotPipeline.ps1 -ActivityHistory.";
            if (state == 1 && Viewer.IsAdmin) await ResourceMapAsync([]);
            return;
        }
        var cur = await S.Settings.StringAsync("LicenseCurrency") ?? "USD";
        var hasCost = await Db.ScalarAsync<int>("SELECT CASE WHEN OBJECT_ID(N'audit.FoundryCostDay', N'U') IS NOT NULL AND EXISTS (SELECT 1 FROM audit.FoundryCostDay) THEN 1 ELSE 0 END;") == 1;
        /* The five reads below are independent: started together, awaited where used. */
        var rowsTask = Db.QueryAsync<ResRow>($@"
SELECT a.[Resource], r.LineOfBusiness, r.Environment, r.Owner, r.SetBy, SUM(CAST(a.Calls AS BIGINT)) AS Calls, SUM(CAST(a.Prompts AS BIGINT)) AS Prompts,
       SUM(CAST(a.Threads AS BIGINT)) AS Threads, SUM(CAST(a.Failures AS BIGINT)) AS Failures,
       SUM(CASE WHEN a.CallerKind = 'Unattributed' THEN CAST(a.Calls AS BIGINT) ELSE 0 END) AS Unattributed,
       COUNT(DISTINCT CASE WHEN a.CallerKind = 'Person' THEN a.Caller END) AS People, COUNT(DISTINCT NULLIF(a.ApplicationName, N'')) AS Apps,
       MIN(r.FirstSeen) AS FirstSeen,
       {(hasCost ? "(SELECT SUM(c.Cost) FROM audit.FoundryCostDay AS c WHERE c.Account = a.[Resource] AND c.[Day] BETWEEN @d0 AND @d1)" : "CAST(NULL AS DECIMAL(19, 4))")} AS Cost
FROM audit.AIAppUserDay AS a
LEFT JOIN audit.AIAppResource AS r ON r.[Resource] = a.[Resource]
WHERE a.[Day] BETWEEN @d0 AND @d1
GROUP BY a.[Resource], r.LineOfBusiness, r.Environment, r.Owner, r.SetBy
ORDER BY Calls DESC;", Ctx.Args());
        var peopleTask = Db.ScalarAsync<int>("SELECT COUNT(DISTINCT Caller) FROM audit.AIAppUserDay WHERE CallerKind = 'Person' AND [Day] BETWEEN @d0 AND @d1;", Ctx.Args());
        var trendTask = Db.QueryAsync<(DateTime Bucket, long Person, long None)>($@"
SELECT {Ctx.BucketSql("d")}, SUM(CASE WHEN a.CallerKind = 'Person' THEN CAST(a.Calls AS BIGINT) ELSE 0 END), SUM(CASE WHEN a.CallerKind = 'Unattributed' THEN CAST(a.Calls AS BIGINT) ELSE 0 END)
FROM audit.AIAppUserDay AS a JOIN bi.DimDate AS d ON d.[Date] = a.[Day]
WHERE a.[Day] BETWEEN @d0 AND @d1 GROUP BY {Ctx.BucketSql("d")};", Ctx.Args());
        /* The accounts Azure billed in the period that recorded no call here: idle, or not collected by Purview. */
        var silentTask = hasCost ? Db.QueryAsync<(string Account, decimal Cost)>(@"
SELECT c.Account, SUM(c.Cost) FROM audit.FoundryCostDay AS c
WHERE c.[Day] BETWEEN @d0 AND @d1 AND NOT EXISTS (SELECT 1 FROM audit.AIAppUserDay AS a WHERE a.[Resource] = c.Account AND a.[Day] BETWEEN @d0 AND @d1)
GROUP BY c.Account HAVING SUM(c.Cost) > 0 ORDER BY SUM(c.Cost) DESC;", Ctx.Args()) : Task.FromResult(new List<(string Account, decimal Cost)>());
        var deptsTask = Db.QueryAsync<(string Department, int People, long Calls)>(@"
SELECT ISNULL(NULLIF(u.Department, N''), N'(no department)'), COUNT(DISTINCT CASE WHEN u.AccountEnabled = 1 THEN a.Caller END), SUM(CAST(a.Calls AS BIGINT))   -- people enabled today (3.1.3)
FROM audit.AIAppUserDay AS a JOIN bi.DimUser AS u ON u.UserPrincipalName = a.Caller
WHERE a.CallerKind = 'Person' AND a.[Day] BETWEEN @d0 AND @d1
GROUP BY ISNULL(NULLIF(u.Department, N''), N'(no department)');", Ctx.Args());
        await Task.WhenAll(rowsTask, peopleTask, trendTask, silentTask, deptsTask);
        var rows = await rowsTask;
        var people = await peopleTask;
        long calls = rows.Sum(r => r.Calls), unattributed = rows.Sum(r => r.Unattributed), failures = rows.Sum(r => r.Failures);
        int min = Ctx.MinGroupSize;
        string PeopleText(int n) => n == 0 ? "none" : n >= min ? n.ToString("#,0") : $"fewer than {min}";

        Kpis.Add(new("Calls", Num(calls), "messages Purview recorded, each a prompt or a response"));
        Kpis.Add(new("Conversations", Num(rows.Sum(r => r.Threads)), "conversation threads, a thread counted once on each day it ran"));
        Kpis.Add(new("Resources", Num(rows.Count), $"{rows.Count(r => r.Environment == "Production"):#,0} in production"));
        Kpis.Add(new("Without a user identity", Pct(Ratio(unattributed, calls)), "the app called with its own key or identity: no person is recorded", Ratio(unattributed, calls) > 0.5 ? "warn" : null));
        Kpis.Add(new("People", PeopleText(people), "whose own identity reached a model in the period, disabled accounts included"));
        Kpis.Add(new("Failed", Pct(Ratio(failures, calls)), "of calls the resource reported as failed", Ratio(failures, calls) > 0.05 ? "warn" : null));

        /* Over time: calls with a person's identity, and without. */
        var trend = await trendTask;
        var buckets = Buckets();
        double? At(DateTime key, bool person) { var r = trend.FirstOrDefault(x => x.Bucket == key); return person ? r.Person : r.None; }
        Panels.Add(new("Calls over time", Chart.Columns(Labels(), [
                new Series("With a person's identity", buckets.Select(b => At(b.Key, true)).ToList(), 1),
                new Series("Without a user identity", buckets.Select(b => At(b.Key, false)).ToList(), 4)], Fmt.Count, "AI app calls"),
            $"Each message Purview recorded, by whether the app passed on the person's identity. {TrendNote}"));

        /* By line of business and environment, as the resource map says. */
        Panels.Add(new("By line of business", Chart.Bars(rows.GroupBy(r => r.LineOfBusiness ?? "(not set)").OrderByDescending(g => g.Sum(r => r.Calls))
                .Select(g => new Bar(g.Key, g.Sum(r => r.Calls), Note: $"{Count(g.Count(), "resource")}")).Take(15).ToList(), Fmt.Count, "Calls by line of business"),
            "From each resource's line of business: guessed from its name (retail-prod-aif-0147 is Retail) until an administrator sets it below.", "half"));
        Panels.Add(new("By environment", Chart.Bars(rows.GroupBy(r => r.Environment ?? "Unknown").OrderByDescending(g => g.Sum(r => r.Calls))
                .Select(g => new Bar(g.Key, g.Sum(r => r.Calls), Note: $"{Count(g.Count(), "resource")}, {Pct(Ratio(g.Sum(r => r.Unattributed), g.Sum(r => r.Calls)))} without a user identity")).ToList(), Fmt.Count, "Calls by environment"),
            "Production, development and test, from the resource's name (prod, dev, uat ...) or as an administrator set it.", "half"));

        /* Each resource. */
        var t = new Table
        {
            Headers = ["Resource", "Line of business", "Environment", "Owner", "Calls", "Prompts", "Conversations", "People", "Without a user identity", "Failed", "Apps named", "First seen", .. (hasCost ? new[] { $"Azure cost ({cur})" } : [])],
            Numeric = new HashSet<int> { 4, 5, 6, 8, 9, 10, 12 },
        };
        var silent = await silentTask;
        if (silent.Count > 0)
            Kpis.Add(new("Billed, no calls recorded", Num(silent.Count), $"{CostRules.Money(Math.Round(silent.Sum(s => s.Cost)), cur)} charged in the period: idle, or Purview does not collect them", "warn"));
        foreach (var r in rows)
            t.Rows.Add([r.Resource, r.LineOfBusiness ?? "(not set)", r.Environment ?? "Unknown", r.Owner, r.Calls, r.Prompts, r.Threads, PeopleText(r.People),
                Pct(Ratio(r.Unattributed, r.Calls)), Pct(Ratio(r.Failures, r.Calls)), r.Apps, r.FirstSeen, .. (hasCost ? new object?[] { r.Cost is { } c ? Math.Round(c) : null } : [])]);
        foreach (var s in silent)
            t.Rows.Add([s.Account + " (no calls recorded)", "(not set)", "Unknown", null, 0, 0, 0, "none", "", "", 0, null, Math.Round(s.Cost)]);
        CsvTable = t;
        Panels.Add(new("Each resource", t.Render(),
            "A resource is the Azure AI Foundry or Azure OpenAI account the call went to. People: those whose own identity reached it, never named, and only from " + min + " up. " +
            "Without a user identity: the app called with its own key or managed identity, so Purview's per-user policies and this count cannot see who asked. " +
            (hasCost ? "Azure cost: what Cost Management charged the account of the same name in the period (the Foundry loader)." : "Azure's cost per resource shows here once the Foundry loader is set up (FoundrySubscriptions)."),
            "full", CsvUrl()));

        /* The departments whose people call these apps with their own identity: counts, small ones folded. */
        var depts = await deptsTask;
        var shown = depts.Where(d => d.People >= min).OrderByDescending(d => d.Calls).Take(15).ToList();
        var folded = depts.Count(d => d.People < min);
        Panels.Add(new("Departments calling with their own identity", Chart.Bars(shown.Select(d => new Bar(d.Department, d.Calls, Note: $"{d.People:#,0} people")).ToList(), Fmt.Count, "Calls by department"),
            $"Calls that carried a person's identity, by the department the directory gives them. People here are accounts enabled today; the calls are everyone's. Departments with fewer than {min} such people are left out" +
            (folded > 0 ? $" ({Count(folded, "department")})." : "."), "half"));

        if (Viewer.IsAdmin) await ResourceMapAsync(rows.Select(r => r.Resource).ToList());
    }

    static readonly string[] Environments = ["Production", "Development", "Test", "Unknown"];

    /* The resource map: line of business, environment and owner for each resource, which administrators set. */
    async Task ResourceMapAsync(List<string> seen)
    {
        var map = await Db.QueryAsync<(string Resource, string? Lob, string Env, string? Owner, string? Notes, string SetBy, DateTime? FirstSeen)>(
            "SELECT [Resource], LineOfBusiness, Environment, Owner, Notes, SetBy, FirstSeen FROM audit.AIAppResource ORDER BY [Resource];");
        var t = new Table { Headers = ["Resource", "Line of business, environment, owner and notes"], Sortable = false, EmptyText = "No resources seen yet: the nightly rollup adds each one the first time it is called." };
        foreach (var m in map)
            t.Rows.Add([m.Resource + (m.SetBy != "Name" ? "" : m.Lob is null && m.Env == "Unknown" ? " (nothing to guess from its name: set it)" : m.Lob is null ? " (environment guessed from its name; set its line of business)" : " (guessed from its name)"), new HtmlString(Form("resource", Hidden("resource", m.Resource) +
                Input("lob", "Line of business", m.Lob, 14) + Select("env", "Environment", Environments, m.Env) + Input("owner", "Owner", m.Owner, 22) + Input("notes", "Notes", m.Notes, 22) +
                Button("Save", quiet: true)))]);
        Panels.Add(new("Resource map", t.Render(),
            "Each resource's line of business, environment and owner. A new resource is guessed from its name (retail-prod-aif-0147: Retail, Production); what is saved here is kept, and every change is in the audit log.", "full"));
    }

    public Task<IActionResult> OnPostResourceAsync(string? resource, string? lob, string? env, string? owner, string? notes) => AdminChangeAsync(async () =>
    {
        if (env is null || !Environments.Contains(env)) throw new ArgumentException("choose an environment.");
        static string? Opt(string? s, int n) => string.IsNullOrWhiteSpace(s) ? null : s.Trim()[..Math.Min(n, s.Trim().Length)];
        var n = await Db.ExecuteAsync(@"
UPDATE audit.AIAppResource SET LineOfBusiness = @lob, Environment = @env, Owner = @owner, Notes = @notes, SetBy = 'Admin', UpdatedBy = @by, UpdatedUtc = SYSUTCDATETIME()
WHERE [Resource] = @resource;", new { resource, lob = Opt(lob, 100), env, owner = Opt(owner, 256), notes = Opt(notes, 400), by = Viewer.Upn ?? Viewer.WindowsName });
        return n == 0 ? "No such resource." : $"{resource}: {Opt(lob, 100) ?? "no line of business"}, {env}{(Opt(owner, 256) is { } o ? $", owned by {o}" : "")}.";
    });
}
