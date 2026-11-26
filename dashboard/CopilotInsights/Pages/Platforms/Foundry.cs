using System.Globalization;
using System.Net;
using CopilotInsights.Charts;
using CopilotInsights.Data;
using CopilotInsights.Infrastructure;
using Microsoft.AspNetCore.Html;

namespace CopilotInsights.Pages.Platforms;

/// <summary>
/// The AI platforms section's pages: Foundry and Azure OpenAI (tools\Import-FoundryUsage.ps1) and GitHub Copilot
/// (tools\Import-GitHubCopilotUsage.ps1). Each reads the loader's tables only, for the whole organization; a page whose
/// loader has never run says how to set it up.
/// </summary>
public abstract class PlatformPage(PageServices s) : InsightsPage(s)
{
    public override string Persona => "AI platforms";
    public override bool UsesTeam => false;

    protected Task<bool> HasRowsAsync(string table) => TableHasRowsAsync(table);

    protected async Task<bool> HasTableAsync(string table) =>
        await Db.ScalarAsync<int>($"SELECT CASE WHEN OBJECT_ID(N'{table}', N'U') IS NULL THEN 0 ELSE 1 END;") == 1;

    protected static string Money(decimal v, string cur) => CostRules.Money(Math.Round(v, 0), cur);

    protected string NotSetUp(string what, string guide) =>
        $"{what} has not been loaded here yet. An administrator sets it up with {guide}; Data sources says what it needs.";
}

/// <summary>The Foundry pages' common reads: the newest day loaded, the currency, and each deployment with its use.</summary>
public abstract class FoundryPage(PageServices s) : PlatformPage(s)
{
    public sealed class DeploymentRow
    {
        public string SubscriptionId { get; set; } = "";
        public string Account { get; set; } = "";
        public string Deployment { get; set; } = "";
        public string? Location { get; set; }
        public string? ModelName { get; set; }
        public string? ModelVersion { get; set; }
        public string? SkuName { get; set; }
        public int? Capacity { get; set; }
        public string? RaiPolicy { get; set; }
        public bool Relaxed { get; set; }
        public long Requests { get; set; }
        public long Tokens { get; set; }
        public DateTime? LastUsed { get; set; }
        public double? PtuAvg { get; set; }
        public double? PtuMax { get; set; }
        public DateTime? Retires { get; set; }
        public string? Owner { get; set; }
    }

    /// <summary>Every deployment as it stands, its requests and tokens in the period, when it was last used, its provisioned use, when its model retires, its filter and its owner.</summary>
    protected async Task<List<DeploymentRow>> DeploymentsAsync()
    {
        var hasCatalog = await HasTableAsync("audit.FoundryModelCatalog");
        var hasAccount = await HasTableAsync("audit.FoundryAccount");
        var hasFilter = await HasTableAsync("audit.FoundryContentFilter");
        var hasRai = await Db.ScalarAsync<int>("SELECT CASE WHEN COL_LENGTH(N'audit.FoundryDeployment', N'RaiPolicy') IS NULL THEN 0 ELSE 1 END;") == 1;
        var hasPtu = await Db.ScalarAsync<int>("SELECT CASE WHEN COL_LENGTH(N'audit.FoundryUsageDay', N'PtuUtilizationAvg') IS NULL THEN 0 ELSE 1 END;") == 1;
        return await Db.QueryAsync<DeploymentRow>($@"
SELECT d.SubscriptionId, d.Account, d.Deployment, d.Location, d.ModelName, d.ModelVersion, d.SkuName, d.Capacity,
       {(hasRai ? "d.RaiPolicy" : "CAST(NULL AS NVARCHAR(200))")} AS RaiPolicy,
       CAST({(hasRai && hasFilter ? "CASE WHEN EXISTS (SELECT 1 FROM audit.FoundryContentFilter AS f WHERE f.SubscriptionId = d.SubscriptionId AND f.Account = d.Account AND f.Policy = d.RaiPolicy AND f.Relaxed = 1) THEN 1 ELSE 0 END" : "0")} AS BIT) AS Relaxed,
       ISNULL(u.Requests, 0) AS Requests, ISNULL(u.Tokens, 0) AS Tokens, l.LastUsed, u.PtuAvg, u.PtuMax,
       {(hasCatalog ? "(SELECT MIN(c.RetiresDate) FROM audit.FoundryModelCatalog AS c WHERE c.Location = d.Location AND c.ModelName = d.ModelName AND c.ModelVersion = d.ModelVersion AND (c.SkuName = N'' OR c.SkuName = d.SkuName))" : "CAST(NULL AS DATE)")} AS Retires,
       {(hasAccount ? "(SELECT a.Owner FROM audit.FoundryAccount AS a WHERE a.SubscriptionId = d.SubscriptionId AND a.ResourceGroup = d.ResourceGroup AND a.Account = d.Account)" : "CAST(NULL AS NVARCHAR(256))")} AS Owner
FROM audit.FoundryDeployment AS d
OUTER APPLY (SELECT SUM(x.Requests) AS Requests, SUM(x.TotalTokens) AS Tokens,
                    {(hasPtu ? "AVG(CAST(x.PtuUtilizationAvg AS FLOAT)), MAX(CAST(x.PtuUtilizationMax AS FLOAT))" : "CAST(NULL AS FLOAT), CAST(NULL AS FLOAT)")}
             FROM audit.FoundryUsageDay AS x WHERE x.SubscriptionId = d.SubscriptionId AND x.Account = d.Account AND x.Deployment = d.Deployment
               AND x.[Day] BETWEEN @d0 AND @d1) AS u (Requests, Tokens, PtuAvg, PtuMax)
OUTER APPLY (SELECT CAST(MAX(x.[Day]) AS DATETIME2(0)) AS LastUsed FROM audit.FoundryUsageDay AS x
             WHERE x.SubscriptionId = d.SubscriptionId AND x.Account = d.Account AND x.Deployment = d.Deployment AND x.Requests > 0) AS l
ORDER BY d.Account, d.Deployment;", Ctx.Args());
    }

    /// <summary>The newest day the usage holds: the day "idle" is counted back from.</summary>
    protected async Task<DateTime> NewestUsageDayAsync() =>
        await Db.ScalarAsync<DateTime?>("SELECT CAST(MAX([Day]) AS DATETIME2(0)) FROM audit.FoundryUsageDay;") ?? DateTime.UtcNow.Date;

    protected async Task<string> CurrencyAsync() =>
        await Db.ScalarAsync<string?>("SELECT TOP (1) Currency FROM audit.FoundryCostDay WHERE Currency IS NOT NULL GROUP BY Currency ORDER BY COUNT(*) DESC;") ?? "USD";

    protected async Task<(int IdleDays, int WarnDays, int LowPercent)> RulesAsync() =>
        ((int)Math.Clamp(await S.Settings.IntAsync("CostIdleDays", 30), 7, 180), Math.Clamp(await S.Settings.IntAsync("ModelRetirementWarnDays", 90), 7, 365),
         Math.Clamp(await S.Settings.IntAsync("PtuLowUtilizationPercent", 50), 1, 100));
}

/// <summary>Foundry overview: what Foundry and Azure OpenAI cost, what is idle or retiring, and what each model costs a million tokens.</summary>
public sealed class FoundryOverviewModel(PageServices s) : FoundryPage(s)
{
    public override string Title => "Foundry overview";
    public override string Intro => "What Foundry and Azure OpenAI cost the organization, where it goes, and what is paid for but not used: idle deployments, reserved capacity standing empty, and models that retire soon.";

    protected override async Task BuildAsync()
    {
        if (!await HasRowsAsync("audit.FoundryDeployment") && !await HasRowsAsync("audit.FoundryCostDay"))
        {
            Notice = NotSetUp("Foundry and Azure OpenAI", "tools\\Import-FoundryUsage.ps1 (docs/FOUNDRY-USAGE.md)");
            return;
        }
        var cur = await CurrencyAsync();
        var (idleDays, warnDays, lowPercent) = await RulesAsync();
        var newest = await NewestUsageDayAsync();
        var deps = await DeploymentsAsync();
        var byAccount = await Db.QueryAsync<(string Account, decimal Cost)>("SELECT Account, SUM(Cost) FROM audit.FoundryCostDay WHERE [Day] BETWEEN @d0 AND @d1 GROUP BY Account ORDER BY SUM(Cost) DESC;", Ctx.Args());
        var meters = await Db.QueryAsync<(string Meter, decimal Cost)>("SELECT Meter, SUM(Cost) FROM audit.FoundryCostDay WHERE [Day] BETWEEN @d0 AND @d1 GROUP BY Meter;", Ctx.Args());
        var trend = await Db.QueryAsync<(DateTime Bucket, decimal Cost)>($@"
SELECT {Ctx.BucketSql("d")}, SUM(c.Cost) FROM audit.FoundryCostDay AS c JOIN bi.DimDate AS d ON d.[Date] = c.[Day]
WHERE c.[Day] BETWEEN @d0 AND @d1 GROUP BY {Ctx.BucketSql("d")};", Ctx.Args());
        var tokens = await Db.QueryAsync<(string Model, long Tokens)>("SELECT ModelName, SUM(TotalTokens) FROM audit.FoundryUsageDay WHERE [Day] BETWEEN @d0 AND @d1 AND ModelName <> N'' GROUP BY ModelName;", Ctx.Args());
        var outside = await HasTableAsync("audit.FoundryAccountSeen") ? await Db.ScalarAsync<int>("SELECT COUNT(*) FROM audit.FoundryAccountSeen WHERE Loaded = 0;") : 0;

        var spend = byAccount.Sum(a => a.Cost);
        var accountsAnswering = deps.Where(d => d.Requests > 0).Select(d => d.Account).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var silentSpend = byAccount.Where(a => !accountsAnswering.Contains(a.Account)).Sum(a => a.Cost);
        var idle = deps.Count(d => PlatformRules.IsIdle(d.LastUsed, newest, idleDays));
        var retiring = deps.Count(d => PlatformRules.IsRetiring(d.Retires, DateTime.UtcNow, warnDays));
        var ptu = deps.Where(d => PlatformRules.IsProvisioned(d.SkuName)).ToList();
        var ptuLow = ptu.Count(d => PlatformRules.IsPtuLow(d.SkuName, d.PtuAvg, lowPercent));
        string? List(string show) => Open("/platforms/foundry/deployments", $"show={show}");

        Kpis.Add(new($"Spend in the period ({cur})", Money(spend, cur), $"what Azure charged {Count(byAccount.Count, "account")} from {Ctx.Start:d MMM} to {Ctx.End:d MMM yyyy}"));
        Kpis.Add(new($"Spend on accounts that answered nothing ({cur})", Money(silentSpend, cur), spend > 0 ? $"{silentSpend / spend:P0} of the spend: billed, with no request in the period" : "no spend in the period",
            silentSpend > 0 ? "warn" : null));
        Kpis.Add(new("Idle deployments", Num(idle), $"no request in the {idleDays} days to {newest:d MMM}", idle > 0 ? "warn" : null, Href: idle > 0 ? List(PlatformRules.Idle) : null));
        Kpis.Add(new("Retiring soon", Num(retiring), $"deployments whose model retires within {warnDays} days", retiring > 0 ? "warn" : null, Href: retiring > 0 ? List(PlatformRules.Retiring) : null));
        if (ptu.Count > 0)
            Kpis.Add(new("Reserved capacity under-used", Num(ptuLow), $"of {Count(ptu.Count, "provisioned deployment")}, using under {lowPercent}% of it on average", ptuLow > 0 ? "warn" : null, Href: ptuLow > 0 ? List(PlatformRules.PtuLow) : null));
        if (outside > 0)
            Kpis.Add(new("AI accounts not loaded", Num(outside), "in subscriptions the load does not read: their cost is not counted here", "warn", Href: Open("/sources")));

        var buckets = Buckets();
        Panels.Add(new("Spend over time", Chart.Columns(Labels(), [new Series("Azure cost", buckets.Select(b => (double?)trend.Where(x => x.Bucket == b.Key).Sum(x => x.Cost)).ToList(), 2)], Fmt.Count, $"Foundry spend ({cur})"),
            $"What Cost Management charged the accounts loaded. {TrendNote}", "half"));
        Panels.Add(new("Spend by account", Chart.Bars(byAccount.Take(12).Select(a => new Bar(a.Account, (double)Math.Round(a.Cost), Open("/governance/ai-apps/resource", $"r={Uri.EscapeDataString(a.Account)}"))).ToList(), Fmt.Count, $"Spend by account ({cur})"),
            "Each account's charges in the period; select one for its apps, models and meters.", "half"));

        /* What a million tokens costs, model by model: each meter goes to the model it names (the longest that fits). */
        var models = tokens.Select(t => t.Model).ToList();
        var costOf = meters.GroupBy(m => PlatformRules.ModelOfMeter(m.Meter, models)).Where(g => g.Key is not null).ToDictionary(g => g.Key!, g => g.Sum(m => m.Cost), StringComparer.OrdinalIgnoreCase);
        var mt = new Table { Headers = ["Model", "Tokens", $"Cost ({cur})", $"A million tokens ({cur})"], Numeric = new HashSet<int> { 1, 2, 3 } };
        foreach (var t in tokens.OrderByDescending(t => t.Tokens))
            mt.Rows.Add([t.Model, t.Tokens, costOf.TryGetValue(t.Model, out var c) ? Math.Round(c) : null,
                         costOf.TryGetValue(t.Model, out var c2) && t.Tokens > 0 ? Math.Round(c2 / t.Tokens * 1_000_000m, 2) : null]);
        CsvTable = mt;
        Panels.Add(new("Tokens and cost by model", mt.Render(),
            "Tokens from Azure Monitor, cost from the meters that name the model (provisioned capacity is billed by the hour, not by token, so it has no price a token).", "full", CsvUrl()));
    }
}

/// <summary>Foundry deployments: every deployment, with what it was used for, when it was last used, and when it retires.</summary>
public sealed class FoundryDeploymentsModel(PageServices s) : FoundryPage(s)
{
    public override string Title => "Foundry deployments";
    public override string Intro => "Every model deployment: its model and SKU, how much it was used, when it was last used, how much of reserved capacity it uses, when its model retires, and the content filter it runs with.";

    protected override async Task BuildAsync()
    {
        if (!await HasRowsAsync("audit.FoundryDeployment"))
        {
            Notice = NotSetUp("Foundry and Azure OpenAI", "tools\\Import-FoundryUsage.ps1 (docs/FOUNDRY-USAGE.md)");
            return;
        }
        var (idleDays, warnDays, lowPercent) = await RulesAsync();
        var newest = await NewestUsageDayAsync();
        var all = await DeploymentsAsync();
        var show = Request.Query["show"].FirstOrDefault() is PlatformRules.Idle or PlatformRules.Retiring or PlatformRules.PtuLow ? Request.Query["show"].ToString() : null;
        var rows = show switch
        {
            PlatformRules.Idle => all.Where(d => PlatformRules.IsIdle(d.LastUsed, newest, idleDays)).ToList(),
            PlatformRules.Retiring => all.Where(d => PlatformRules.IsRetiring(d.Retires, DateTime.UtcNow, warnDays)).ToList(),
            PlatformRules.PtuLow => all.Where(d => PlatformRules.IsPtuLow(d.SkuName, d.PtuAvg, lowPercent)).ToList(),
            _ => all,
        };
        var label = show switch { PlatformRules.Idle => "Idle deployments", PlatformRules.Retiring => "Retiring soon", PlatformRules.PtuLow => "Reserved capacity under-used", _ => "Deployments" };
        Kpis.Add(new(label, Num(rows.Count), show switch
        {
            PlatformRules.Idle => $"no request in the {idleDays} days to {newest:d MMM}",
            PlatformRules.Retiring => $"deployments whose model retires within {warnDays} days",
            PlatformRules.PtuLow => $"provisioned, using under {lowPercent}% of their capacity on average",
            _ => $"in {Count(all.Select(d => d.Account).Distinct().Count(), "account")}",
        }));

        string Filter(string? key, string text) => key == show ? $"<b>{WebUtility.HtmlEncode(text)}</b>"
            : $"<a href=\"{WebUtility.HtmlEncode(Ctx.Url(Request.Path, keepOrg: false, extra: key is null ? null : $"show={key}"))}\">{WebUtility.HtmlEncode(text)}</a>";
        var filters = $"<p class=\"sub\">Show: {Filter(null, "every deployment")} · {Filter(PlatformRules.Idle, "idle")} · {Filter(PlatformRules.Retiring, "retiring soon")} · {Filter(PlatformRules.PtuLow, "reserved capacity under-used")}</p>";

        var t = new Table
        {
            Headers = ["Account", "Deployment", "Model", "SKU", "Capacity", "Requests", "Tokens", "Last used", "Reserved capacity used", "Retires", "Content filter", "Owner"],
            Numeric = new HashSet<int> { 4, 5, 6 }, EmptyText = "No deployment matches.",
        };
        foreach (var d in rows)
            t.Rows.Add([new Link(d.Account, Ctx.Url("/governance/ai-apps/resource", extra: $"r={Uri.EscapeDataString(d.Account)}")), d.Deployment,
                string.Join(" ", new[] { d.ModelName, d.ModelVersion }.Where(x => !string.IsNullOrEmpty(x))), d.SkuName, d.Capacity, d.Requests, d.Tokens,
                d.LastUsed is { } lu ? lu.Date : "never in what is loaded",
                PlatformRules.IsProvisioned(d.SkuName) ? (d.PtuAvg is { } a ? $"{a:0}% on average, {d.PtuMax:0}% at most" : "not read") : null,
                d.Retires is { } r ? r.Date : null,
                d.RaiPolicy is null ? null : d.Relaxed ? new HtmlString($"{WebUtility.HtmlEncode(d.RaiPolicy)} <span class=\"status-warn\">looser than default</span>") : d.RaiPolicy,
                d.Owner ?? "(no owner tag)"]);
        CsvTable = t;
        Panels.Add(new("Each deployment", new HtmlString(filters + t.Render().Value),
            $"Requests and tokens in the period, from Azure Monitor. Last used: the newest day with a request. Idle: none in the {idleDays} days to the newest day loaded (CostIdleDays). " +
            $"Retires: the day Azure stops serving the model, or its SKU, where it is deployed; flagged within {warnDays} days (ModelRetirementWarnDays). Reserved capacity: a provisioned deployment's daily use, under {lowPercent}% flagged (PtuLowUtilizationPercent).",
            "full", CsvUrl()));
    }
}

/// <summary>Foundry posture: each account against the checks that matter for its security and its cost.</summary>
public sealed class FoundryPostureModel(PageServices s) : FoundryPage(s)
{
    public override string Title => "Foundry posture";
    public override bool UsesPeriod => false;
    public override string Intro => "Each Foundry and Azure OpenAI account against five checks: who can reach it, whether keys are allowed, whether it logs, whether its content filters are looser than Microsoft's default, and whether it has an owner.";

    sealed class Account
    {
        public string SubscriptionId { get; set; } = "";
        public string ResourceGroup { get; set; } = "";
        public string Name { get; set; } = "";
        public string? Kind { get; set; }
        public string? PublicNetworkAccess { get; set; }
        public string? NetworkDefaultAction { get; set; }
        public int? PrivateEndpoints { get; set; }
        public bool? DisableLocalAuth { get; set; }
        public int? DiagnosticSettings { get; set; }
        public string? LogCategories { get; set; }
        public string? Owner { get; set; }
        public string? CostCenter { get; set; }
        public int Relaxed { get; set; }
    }

    protected override async Task BuildAsync()
    {
        if (!await HasRowsAsync("audit.FoundryAccount"))
        {
            Notice = NotSetUp("The Foundry accounts' posture", "tools\\Import-FoundryUsage.ps1 from 3.3.0 (docs/FOUNDRY-USAGE.md)");
            return;
        }
        var hasFilter = await HasTableAsync("audit.FoundryContentFilter");
        var accounts = await Db.QueryAsync<Account>($@"
SELECT a.SubscriptionId, a.ResourceGroup, a.Account AS Name, a.Kind, a.PublicNetworkAccess, a.NetworkDefaultAction, a.PrivateEndpoints, a.DisableLocalAuth,
       a.DiagnosticSettings, a.LogCategories, a.Owner, a.CostCenter,
       {(hasFilter ? "(SELECT COUNT(*) FROM audit.FoundryContentFilter AS f WHERE f.SubscriptionId = a.SubscriptionId AND f.Account = a.Account AND f.Relaxed = 1)" : "0")} AS Relaxed
FROM audit.FoundryAccount AS a ORDER BY a.Account;");
        var checks = accounts.Select(a => (A: a, C: PlatformRules.Posture(a.PublicNetworkAccess, a.NetworkDefaultAction, a.PrivateEndpoints, a.DisableLocalAuth, a.DiagnosticSettings, a.Relaxed, a.Owner))).ToList();
        int Failing(string name) => checks.Count(x => x.C.First(c => c.Name == name).Passes == false);
        Kpis.Add(new("Accounts", Num(accounts.Count), "Azure OpenAI and Foundry accounts loaded"));
        Kpis.Add(new("Reachable from any network", Num(Failing("Network")), "public network access on, no network rule", Failing("Network") > 0 ? "warn" : null));
        Kpis.Add(new("Keys allowed", Num(Failing("Sign-in")), "a call with a key carries no identity", Failing("Sign-in") > 0 ? "warn" : null));
        Kpis.Add(new("Not logging", Num(Failing("Logging")), "no diagnostic setting sends their logs anywhere", Failing("Logging") > 0 ? "warn" : null));
        Kpis.Add(new("Filters looser than default", Num(Failing("Content filters")), "accounts with a content filter looser than Microsoft's default", Failing("Content filters") > 0 ? "warn" : null));
        Kpis.Add(new("No owner tag", Num(Failing("Owner")), "nobody named to answer for the account", Failing("Owner") > 0 ? "warn" : null));

        static object Cell(PlatformRules.Check c) => new HtmlString(
            $"<span class=\"{(c.Passes is null ? "muted" : c.Passes.Value ? "status-ok" : "status-warn")}\">{(c.Passes is null ? "Unknown" : c.Passes.Value ? "OK" : "Check")}</span><span class=\"cellnote\">{WebUtility.HtmlEncode(c.Text)}</span>");
        var t = new Table { Headers = ["Account", "Kind", "Network", "Sign-in", "Logging", "Content filters", "Owner", "Cost center"], Sortable = true };
        foreach (var (a, c) in checks)
            t.Rows.Add([new Link(a.Name, Ctx.Url("/governance/ai-apps/resource", extra: $"r={Uri.EscapeDataString(a.Name)}")), a.Kind, Cell(c[0]), Cell(c[1]), Cell(c[2]), Cell(c[3]), Cell(c[4]), a.CostCenter]);
        CsvTable = t;
        Panels.Add(new("Each account", t.Render(),
            "As the Foundry load last read each account from Azure Resource Manager. Network: public network access and the rule for networks not listed. Sign-in: whether keys are refused (Entra only). " +
            "Logging: the account's diagnostic settings. Content filters: any filter turned off, not blocking, or blocking only high severity. Owner and cost center: from the account's tags.", "full", CsvUrl()));
    }
}
