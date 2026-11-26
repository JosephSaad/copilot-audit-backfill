using System.Net;
using CopilotInsights.Charts;
using CopilotInsights.Data;
using Microsoft.AspNetCore.Html;

namespace CopilotInsights.Pages.Governance;

/// <summary>
/// One AI resource (an Azure AI Foundry or Azure OpenAI account): the apps that call it, with or without a person's
/// identity, its failures over time, the departments calling it with their own identity (counts only), and, where the
/// Foundry loader runs, its deployments, models, tokens and what Azure charged, by meter. The cost per app is an estimate:
/// the resource's cost shared by each app's calls. Reached from AI apps beyond Copilot's Each resource; names no person.
/// </summary>
public sealed class AiResourceModel(PageServices s) : InsightsPage(s)
{
    public override string Title => Resource ?? "AI resource detail";
    public override string Persona => "Governance";
    public override bool UsesTeam => false;
    public override string Intro => "One Azure AI Foundry or Azure OpenAI resource: which apps call it, how often they fail, whether a person's identity comes with the call, and, where Azure's usage is loaded, its models, tokens and cost. The whole organization's, whatever team is chosen.";

    string? Resource { get; set; }

    sealed class AppRow
    {
        public string App { get; set; } = "";
        public long Calls { get; set; }
        public long Prompts { get; set; }
        public long Threads { get; set; }
        public long Failures { get; set; }
        public long Unattributed { get; set; }
        public int People { get; set; }
        public DateTime FirstDay { get; set; }
        public DateTime LastDay { get; set; }
    }

    sealed class DeploymentRow
    {
        public string Deployment { get; set; } = "";
        public string? Model { get; set; }
        public string? Version { get; set; }
        public string? Sku { get; set; }
        public int? Capacity { get; set; }
        public long? Requests { get; set; }
        public long? InputTokens { get; set; }
        public long? OutputTokens { get; set; }
        public DateTime? LastUsed { get; set; }
    }

    /// <summary>
    /// An app's estimated share of the resource's cost: the cost shared by calls. Null when either is unknown. Calls are
    /// not equal (a long answer costs more than a short one), so the page marks it as an estimate.
    /// </summary>
    public static decimal? CostShare(decimal? cost, long appCalls, long allCalls) =>
        cost is { } c && allCalls > 0 ? Math.Round(c * appCalls / allCalls, 2) : null;

    protected override async Task BuildAsync()
    {
        ShowDone();
        if (await Db.ScalarAsync<int>("SELECT CASE WHEN OBJECT_ID(N'audit.AIAppUserDay', N'U') IS NULL THEN 0 ELSE 1 END;") == 0)
        {
            Notice = "AI app calls cannot be shown yet. An administrator installs release 2.6.9 or later and sets up the activity database (docs/OPERATIONS.md, AI apps, Power BI and Power Apps).";
            return;
        }
        var hasCost = await TableHasRowsAsync("audit.FoundryCostDay");
        var hasUsage = await TableHasRowsAsync("audit.FoundryDeployment");

        /* The resources to choose from: every one a call has reached, and every account Azure billed. */
        var names = await Db.QueryAsync<string>($@"
SELECT [Resource] FROM audit.AIAppUserDay GROUP BY [Resource]
{(hasCost ? "UNION SELECT Account FROM audit.FoundryCostDay GROUP BY Account" : "")}
ORDER BY 1;");
        var asked = Request.Query["r"].FirstOrDefault();
        Resource = names.FirstOrDefault(n => string.Equals(n, asked, StringComparison.OrdinalIgnoreCase));
        var options = string.Concat(names.Select(n =>
            $"<option value=\"{WebUtility.HtmlEncode(Ctx.Url(Request.Path, extra: $"r={Uri.EscapeDataString(n)}"))}\"{(n == Resource ? " selected" : "")}>{WebUtility.HtmlEncode(n)}</option>"));
        Panels.Add(new(Resource is null ? "Choose a resource" : "Resource", new HtmlString(
            $"<select class=\"pick\" aria-label=\"Resource\" data-navigate><option value=\"\">Choose a resource</option>{options}</select>" +
            (Resource is null ? $"<p class=\"sub\">Or select a resource's name on <a href=\"{WebUtility.HtmlEncode(Ctx.Url("/governance/ai-apps"))}\">AI apps beyond Copilot</a>.</p>" : ""))));
        if (Resource is null) return;

        var cur = await S.Settings.StringAsync("LicenseCurrency") ?? "USD";
        /* Each query its own parameters: they run side by side, and Dapper's parameter object is not shared safely. */
        object Args() => Ctx.Args(new { res = Resource });
        int min = Ctx.MinGroupSize;
        string PeopleText(int n) => n == 0 ? "none" : n >= min ? n.ToString("#,0") : $"fewer than {min}";

        var mapTask = Db.QueryAsync<(string? Lob, string? Env, string? Owner, string? SetBy, DateTime? FirstSeen, string? Subs)>(@"
SELECT r.LineOfBusiness, r.Environment, r.Owner, r.SetBy, CAST(r.FirstSeen AS DATETIME2(0)),
       (SELECT STRING_AGG(CAST(x.SubscriptionId AS NVARCHAR(64)), N', ') FROM (SELECT DISTINCT SubscriptionId FROM audit.AIAppUserDay WHERE [Resource] = @res AND SubscriptionId <> '') AS x)
FROM (SELECT 1 AS One) AS o LEFT JOIN audit.AIAppResource AS r ON r.[Resource] = @res;", Args());
        var appsTask = Db.QueryAsync<AppRow>(@"
SELECT ApplicationName AS App, SUM(CAST(Calls AS BIGINT)) AS Calls, SUM(CAST(Prompts AS BIGINT)) AS Prompts, SUM(CAST(Threads AS BIGINT)) AS Threads,
       SUM(CAST(Failures AS BIGINT)) AS Failures, SUM(CASE WHEN CallerKind = 'Unattributed' THEN CAST(Calls AS BIGINT) ELSE 0 END) AS Unattributed,
       COUNT(DISTINCT CASE WHEN CallerKind = 'Person' THEN Caller END) AS People,
       CAST(MIN([Day]) AS DATETIME2(0)) AS FirstDay, CAST(MAX([Day]) AS DATETIME2(0)) AS LastDay
FROM audit.AIAppUserDay WHERE [Resource] = @res AND [Day] BETWEEN @d0 AND @d1
GROUP BY ApplicationName ORDER BY Calls DESC;", Args());
        var peopleTask = Db.ScalarAsync<int>("SELECT COUNT(DISTINCT Caller) FROM audit.AIAppUserDay WHERE [Resource] = @res AND CallerKind = 'Person' AND [Day] BETWEEN @d0 AND @d1;", Args());
        var trendTask = Db.QueryAsync<(DateTime Bucket, long Person, long None, long Failures)>($@"
SELECT {Ctx.BucketSql("d")}, SUM(CASE WHEN a.CallerKind = 'Person' THEN CAST(a.Calls AS BIGINT) ELSE 0 END),
       SUM(CASE WHEN a.CallerKind = 'Unattributed' THEN CAST(a.Calls AS BIGINT) ELSE 0 END), SUM(CAST(a.Failures AS BIGINT))
FROM audit.AIAppUserDay AS a JOIN bi.DimDate AS d ON d.[Date] = a.[Day]
WHERE a.[Resource] = @res AND a.[Day] BETWEEN @d0 AND @d1 GROUP BY {Ctx.BucketSql("d")};", Args());
        var deptsTask = Db.QueryAsync<(string Department, int People, long Calls)>(@"
SELECT ISNULL(NULLIF(u.Department, N''), N'(no department)'), COUNT(DISTINCT CASE WHEN u.AccountEnabled = 1 THEN a.Caller END), SUM(CAST(a.Calls AS BIGINT))
FROM audit.AIAppUserDay AS a JOIN bi.DimUser AS u ON u.UserPrincipalName = a.Caller
WHERE a.[Resource] = @res AND a.CallerKind = 'Person' AND a.[Day] BETWEEN @d0 AND @d1
GROUP BY ISNULL(NULLIF(u.Department, N''), N'(no department)');", Args());
        var costTask = hasCost ? Db.QueryAsync<(string Meter, decimal Cost)>(@"
SELECT ISNULL(NULLIF(Meter, N''), N'(no meter named)'), SUM(Cost) FROM audit.FoundryCostDay
WHERE Account = @res AND [Day] BETWEEN @d0 AND @d1 GROUP BY ISNULL(NULLIF(Meter, N''), N'(no meter named)') ORDER BY SUM(Cost) DESC;", Args())
            : Task.FromResult(new List<(string Meter, decimal Cost)>());
        var costTrendTask = hasCost ? Db.QueryAsync<(DateTime Bucket, decimal Cost)>($@"
SELECT {Ctx.BucketSql("d")}, SUM(c.Cost) FROM audit.FoundryCostDay AS c JOIN bi.DimDate AS d ON d.[Date] = c.[Day]
WHERE c.Account = @res AND c.[Day] BETWEEN @d0 AND @d1 GROUP BY {Ctx.BucketSql("d")};", Args())
            : Task.FromResult(new List<(DateTime Bucket, decimal Cost)>());
        var deployTask = hasUsage ? Db.QueryAsync<DeploymentRow>(@"
SELECT dp.Deployment, dp.ModelName AS Model, dp.ModelVersion AS Version, dp.SkuName AS Sku, dp.Capacity,
       u.Requests, u.InputTokens, u.OutputTokens, u.LastUsed
FROM audit.FoundryDeployment AS dp
OUTER APPLY (SELECT SUM(x.Requests) AS Requests, SUM(x.InputTokens) AS InputTokens, SUM(x.OutputTokens) AS OutputTokens,
                    CAST(MAX(CASE WHEN x.Requests > 0 THEN x.[Day] END) AS DATETIME2(0)) AS LastUsed
             FROM audit.FoundryUsageDay AS x
             WHERE x.Account = dp.Account AND x.Deployment = dp.Deployment AND x.[Day] BETWEEN @d0 AND @d1) AS u
WHERE dp.Account = @res ORDER BY u.Requests DESC, dp.Deployment;", Args())
            : Task.FromResult(new List<DeploymentRow>());
        await Task.WhenAll(mapTask, appsTask, peopleTask, trendTask, deptsTask, costTask, costTrendTask, deployTask);

        var map = (await mapTask).FirstOrDefault();
        var apps = await appsTask;
        var meters = await costTask;
        decimal? cost = hasCost ? meters.Sum(m => m.Cost) : null;
        long calls = apps.Sum(a => a.Calls), failures = apps.Sum(a => a.Failures), unattributed = apps.Sum(a => a.Unattributed);

        /* What the resource is: as the resource map has it, and where Azure has it. */
        var facts = new List<string>
        {
            $"Line of business: {map.Lob ?? "(not set)"}", $"Environment: {map.Env ?? "Unknown"}", $"Owner: {map.Owner ?? "(not set)"}",
        };
        if (map.FirstSeen is { } fs) facts.Add($"First seen {fs:d MMM yyyy}");
        if (!string.IsNullOrEmpty(map.Subs)) facts.Add($"Subscription {map.Subs}");
        Notice = string.Join(" · ", facts) + (map.SetBy == "Admin" ? "" : ". Line of business and environment are guessed from the name until an administrator sets them on AI apps beyond Copilot.");

        Kpis.Add(new("Calls", Num(calls), "messages Purview recorded, each a prompt or a response"));
        Kpis.Add(new("Apps", Num(apps.Count(a => a.App.Length > 0)), apps.Any(a => a.App.Length == 0) ? "named by the calls; some calls name no app" : "named by the calls"));
        Kpis.Add(new("People", PeopleText(await peopleTask), "whose own identity reached it in the period"));
        Kpis.Add(new("Without a user identity", Pct(Ratio(unattributed, calls)), "the app called with its own key or identity", Ratio(unattributed, calls) > 0.5 ? "warn" : null));
        Kpis.Add(new("Failed", Pct(Ratio(failures, calls)), "of calls the resource reported as failed", Ratio(failures, calls) > 0.05 ? "crit" : null));
        if (cost is { } c)
        {
            Kpis.Add(new($"Azure cost ({cur})", Num((long)Math.Round(c)), "what Cost Management charged this account in the period"));
            if (calls > 0) Kpis.Add(new($"Cost per 1,000 calls ({cur})", (c * 1000 / calls).ToString("#,0.00", System.Globalization.CultureInfo.InvariantCulture), "the period's cost over its calls"));
        }

        /* Over time: calls with and without a person, and the share that failed. */
        var trend = await trendTask;
        var buckets = Buckets();
        (long Person, long None, long Failures) At(DateTime key) { var r = trend.FirstOrDefault(x => x.Bucket == key); return (r.Person, r.None, r.Failures); }
        Panels.Add(new("Calls over time", Chart.Columns(Labels(), [
                new Series("With a person's identity", buckets.Select(b => (double?)At(b.Key).Person).ToList(), 1),
                new Series("Without a user identity", buckets.Select(b => (double?)At(b.Key).None).ToList(), 4)], Fmt.Count, "Calls to this resource"),
            $"Each message Purview recorded, by whether the app passed on the person's identity. {TrendNote}", "half"));
        Panels.Add(new("Failures over time", Chart.Line(Labels(), [
                new Series("Failed", buckets.Select(b => { var a = At(b.Key); return Ratio(a.Failures, a.Person + a.None); }).ToList(), 5)], Fmt.Percent, "Share of calls that failed", 1),
            $"The share of calls the resource reported as failed. A jump usually means a key, a quota or a deployment changed. {TrendNote}", "half"));

        /* Each app: its calls, identity, failures, and its estimated share of the cost. */
        var t = new Table
        {
            Headers = ["App", "Calls", "Prompts", "Conversations", "People", "Without a user identity", "Failed", "First day", "Last day", .. (cost is null ? Array.Empty<string>() : [$"Estimated cost ({cur})"])],
            Numeric = new HashSet<int> { 1, 2, 3, 5, 6, 9 },
        };
        foreach (var a in apps)
            t.Rows.Add([a.App.Length == 0 ? "(no app named)" : a.App, a.Calls, a.Prompts, a.Threads, PeopleText(a.People), Pct(Ratio(a.Unattributed, a.Calls)), Pct(Ratio(a.Failures, a.Calls)),
                a.FirstDay, a.LastDay, .. (cost is null ? Array.Empty<object?>() : [CostShare(cost, a.Calls, calls) is { } share ? Math.Round(share) : null])]);
        CsvTable = t;
        Panels.Add(new("Each app", t.Render(),
            "The apps the calls name (ApplicationName), most calls first. People: those whose own identity came with the call, never named, and only from " + min + " up. " +
            (cost is null ? "The estimated cost per app shows here once the Foundry loader is set up (FoundrySubscriptions)."
                          : "Estimated cost: the resource's cost shared by each app's calls. Calls do not all cost the same, so it is a guide; a deployment or a tag per app makes it exact."),
            "full", CsvUrl()));

        /* Departments calling it with their own identity: counts, small ones folded. */
        var depts = await deptsTask;
        var shown = depts.Where(d => d.People >= min).OrderByDescending(d => d.Calls).Take(15).ToList();
        var folded = depts.Count(d => d.People < min);
        Panels.Add(new("Departments calling with their own identity", Chart.Bars(shown.Select(d => new Bar(d.Department, d.Calls, Note: $"{d.People:#,0} people")).ToList(), Fmt.Count, "Calls by department"),
            $"Calls that carried a person's identity, by the department the directory gives them. Departments with fewer than {min} such people are left out" + (folded > 0 ? $" ({Count(folded, "department")})." : "."), "half"));

        /* The Azure side: deployments and their tokens, and the cost by meter and over time. */
        var deployments = await deployTask;
        if (deployments.Count > 0)
        {
            var dt = new Table { Headers = ["Deployment", "Model", "Version", "SKU", "Capacity", "Requests", "Input tokens", "Output tokens", "Last used"], Numeric = new HashSet<int> { 4, 5, 6, 7 } };
            foreach (var d in deployments)
                dt.Rows.Add([d.Deployment, d.Model, d.Version, d.Sku, d.Capacity, d.Requests, d.InputTokens, d.OutputTokens, d.LastUsed is { } lu ? lu : "not in the period"]);
            Panels.Add(new("Deployments", dt.Render(),
                "Each model deployment on this account, from Azure: its model, SKU and capacity, and the requests and tokens Azure Monitor counted in the period. A deployment with no requests still costs money when its capacity is reserved (provisioned)."));
        }
        if (cost is not null)
        {
            var ct = await costTrendTask;
            Panels.Add(new("Cost over time", Chart.Columns(Labels(), [new Series("Azure cost", buckets.Select(b => (double?)ct.Where(x => x.Bucket == b.Key).Sum(x => x.Cost)).ToList(), 2)], Fmt.Count, $"Azure cost ({cur})"),
                $"What Cost Management charged this account, as the Foundry loader loaded it. {TrendNote}", "half"));
            Panels.Add(new("Cost by meter", Chart.Bars(meters.Take(12).Select(m => new Bar(m.Meter, (double)Math.Round(m.Cost))).ToList(), Fmt.Count, $"Azure cost by meter ({cur})"),
                "Azure's meters for this account in the period, such as a model's input and output tokens: the twelve largest.", "half"));
        }
        else if (deployments.Count == 0)
            Panels.Add(new("Models, tokens and cost", new HtmlString("<p class=\"sub\">Azure's side of this resource (its deployments, tokens and what it cost) shows here once the Foundry loader is set up: the subscriptions in FoundrySubscriptions, with Monitoring Reader and Cost Management Reader for the app. See Data sources.</p>")));
    }
}
