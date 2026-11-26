using CopilotInsights.Infrastructure;

namespace CopilotInsights.Data;

/// <summary>
/// The spending limits Forecasting › Spend limits tracks (dash.SpendLimit, kept in step with the limits read at their
/// sources by dash.usp_SyncSpendLimits) and each one measured against its own source's spend: an environment's
/// Copilot Credits from the consumption report, a gateway's tokens from its token metric, GitHub's bill, and a
/// person's Copilot Credits from Licensing › Credit use's estimate. The rules are <see cref="SpendLimits"/>.
/// </summary>
public sealed class SpendLimitData(Db db, Settings settings, CreditData credits)
{
    public sealed class LimitRow
    {
        public int LimitId { get; set; }
        public string Source { get; set; } = "";
        public string? ExternalId { get; set; }
        public string Kind { get; set; } = "";
        public string Name { get; set; } = "";
        public string? ScopeType { get; set; }
        public string? Scope { get; set; }
        public string Unit { get; set; } = "";
        public decimal? Amount { get; set; }
        public string Period { get; set; } = SpendLimits.Monthly;
        public bool PerUser { get; set; }
        public string? L2Upn { get; set; }
        public string? L2Name { get; set; }
        public string? L3Upn { get; set; }
        public string? L3Name { get; set; }
        public int? WarnAtPercent { get; set; }
        public bool Hidden { get; set; }
        public DateTime? GoneUtc { get; set; }
        public string? Note { get; set; }
    }

    /// <summary>One limit measured. NotMeasured says why there is no position (the source has no spend at that level).</summary>
    public sealed record Measured(LimitRow Limit, SpendLimits.Position? Position, DateTime? AsOf, string? Key, int Keys, int KeysOver,
        IReadOnlyList<(DateTime Month, decimal Spent)> Months, decimal? Suggested, (double At80, double At100)? Short, string? NotMeasured)
    {
        public bool Warn { get; init; }
        public IReadOnlyDictionary<DateTime, decimal> Daily { get; init; } = new Dictionary<DateTime, decimal>();
    }

    public async Task<bool> ReadyAsync() =>
        await db.ScalarAsync<int>("SELECT CASE WHEN OBJECT_ID(N'dash.SpendLimit', N'U') IS NULL THEN 0 ELSE 1 END;") == 1;

    /// <summary>Brings in what the loaders read, when they have read something since the list last changed.</summary>
    public async Task SyncAsync()
    {
        if (await db.ScalarAsync<int>(@"SELECT CASE WHEN OBJECT_ID(N'dash.usp_SyncSpendLimits', N'P') IS NOT NULL AND OBJECT_ID(N'audit.SpendLimitSource', N'U') IS NOT NULL
            AND ISNULL((SELECT MAX(LoadedUtc) FROM audit.SpendLimitSource), '19000101') > ISNULL((SELECT MAX(UpdatedUtc) FROM dash.SpendLimit WHERE UpdatedBy = N'source'), '19000101') THEN 1 ELSE 0 END;") == 1)
            await db.ExecuteAsync("EXEC dash.usp_SyncSpendLimits;");
    }

    public Task<List<LimitRow>> LimitsAsync(bool hidden = false) => db.QueryAsync<LimitRow>($@"
SELECT l.LimitId, l.Source, l.ExternalId, l.Kind, l.Name, l.ScopeType, l.Scope, l.Unit, l.Amount, l.Period, l.PerUser,
       l.L2Upn, p2.DisplayName AS L2Name, l.L3Upn, p3.DisplayName AS L3Name, l.WarnAtPercent, l.Hidden, l.GoneUtc, l.Note
FROM dash.SpendLimit AS l LEFT JOIN dash.Person AS p2 ON p2.Upn = l.L2Upn LEFT JOIN dash.Person AS p3 ON p3.Upn = l.L3Upn
{(hidden ? "" : "WHERE l.Hidden = 0")}
ORDER BY l.Kind, l.Name;");

    async Task<bool> HasRowsAsync(string table) =>
        await db.ScalarAsync<int>($"SELECT CASE WHEN OBJECT_ID(N'{table}', N'U') IS NOT NULL AND EXISTS (SELECT 1 FROM {table}) THEN 1 ELSE 0 END;") == 1;

    /// <summary>Every limit measured. The spend is read once a source, for the last 13 months, and split by scope here.</summary>
    public async Task<List<Measured>> MeasureAsync(IReadOnlyList<LimitRow> limits, ReportContext? c)
    {
        var alertDays = Math.Clamp(await settings.IntAsync("SpendLimitAlertDays", 5), 0, 365);
        var headroom = Math.Clamp(await settings.IntAsync("SpendLimitHeadroomPercent", 10), 0, 200);
        var p95 = string.Equals(await settings.StringAsync("SpendLimitSuggestFrom"), "P95", StringComparison.OrdinalIgnoreCase);
        var from = DateTime.UtcNow.Date.AddMonths(-13);

        /* Each source's spend by scope and day: few rows (environments, gateway values and organizations are few). */
        async Task<(DateTime? AsOf, Dictionary<string, Dictionary<DateTime, decimal>> ByScope)> ReadAsync(bool needed, string table, string sql)
        {
            if (!needed || !await HasRowsAsync(table)) return (null, new(StringComparer.OrdinalIgnoreCase));
            var rows = await db.QueryAsync<(string Scope, DateTime Day, decimal V)>(sql, new { from });
            var map = new Dictionary<string, Dictionary<DateTime, decimal>>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in rows)
            {
                if (!map.TryGetValue(r.Scope ?? "", out var d)) map[r.Scope ?? ""] = d = [];
                d[r.Day.Date] = d.GetValueOrDefault(r.Day.Date) + r.V;
            }
            return (rows.Count == 0 ? null : rows.Max(r => r.Day).Date, map);
        }
        var env = await ReadAsync(limits.Any(l => l.Kind == "CopilotResource"), "audit.PowerPlatformCreditDay",
            "SELECT EnvironmentName, [Day], SUM(ISNULL(Credits, 0)) FROM audit.PowerPlatformCreditDay WHERE Product = 'Copilot Studio' AND [Day] >= @from GROUP BY EnvironmentName, [Day];");
        var gw = await ReadAsync(limits.Any(l => l.Kind is "GatewayQuota" or "GatewayRate"), "audit.GatewayTokenDay",
            "SELECT CONCAT(Gateway, NCHAR(9), BusinessUnit), [Day], SUM(CAST(ISNULL(TotalTokens, 0) AS DECIMAL(19, 0))) FROM audit.GatewayTokenDay WHERE [Day] >= @from GROUP BY Gateway, BusinessUnit, [Day];");
        var gh = await ReadAsync(limits.Any(l => l.Kind == "GitHubBudget"), "audit.GitHubCopilotCost",
            "SELECT ISNULL(Organization, N''), UsageDate, SUM(ISNULL(NetAmount, 0)) FROM audit.GitHubCopilotCost WHERE UsageDate >= @from GROUP BY Organization, UsageDate;");
        CreditView? people = c is not null && limits.Any(l => l.Kind == "CopilotUser") ? await credits.BuildAsync(c) : null;

        Measured Series(LimitRow l, DateTime? asOf, IReadOnlyDictionary<DateTime, decimal> daily, string? key, int keys, int keysOver)
        {
            if (asOf is null) return new(l, null, null, key, keys, keysOver, [], null, null, "No spend is loaded for it yet.");
            var months = daily.GroupBy(kv => new DateTime(kv.Key.Year, kv.Key.Month, 1)).OrderBy(g => g.Key)
                .Select(g => (g.Key, g.Sum(kv => kv.Value))).ToList();
            /* The month the data reaches is not over: its spend is not a month's, so it is left out of the forecast. */
            var whole = months.Where(m => m.Key.AddMonths(1) <= asOf.Value.AddDays(1)).Select(m => m.Item2).ToList();
            if (SpendLimits.IsShort(l.Period))
                return new(l, null, asOf, key, keys, keysOver, months, null, null,
                    "A limit a minute or an hour: the gateway's counts are by day, so whether it was reached is not known here. The gateway refuses calls over it (429).") { Daily = daily };
            var pos = SpendLimits.Where(l.Amount, l.Period, asOf.Value, daily);
            (double, double)? shortP = null;
            if (l.Period == SpendLimits.Daily && l.Amount is { } a)
                shortP = SpendLimits.ShortPeriods(a, Enumerable.Range(0, 30).Select(i => daily.GetValueOrDefault(asOf.Value.AddDays(-i))).ToList());
            var suggested = l.Period == SpendLimits.Monthly ? SpendLimits.Suggest(whole, l.Unit, p95, headroom) : null;
            return new(l, pos, asOf, key, keys, keysOver, months, suggested, shortP, null)
            { Warn = SpendLimits.Warn(pos, asOf.Value, l.Period, alertDays, l.WarnAtPercent), Daily = daily };
        }

        var result = new List<Measured>(limits.Count);
        foreach (var l in limits)
        {
            switch (l.Kind)
            {
                case "CopilotResource" when l.ScopeType == "Environment" && l.Scope is { } e:
                    result.Add(Series(l, env.AsOf, env.ByScope.GetValueOrDefault(e) ?? [], null, 1, 0));
                    break;
                case "CopilotResource":
                    result.Add(new(l, null, null, null, 0, 0, [], null, null, "The consumption report gives credits by environment, not by agent: this limit is not measured here."));
                    break;
                case "GatewayQuota" or "GatewayRate":
                {
                    /* Per counter key: each business-unit value of the gateway its name starts with (every gateway when none
                       matches), on the assumption the key is the business unit the gateway stamps. The worst is shown. */
                    var gateway = l.Name.Split(" / ")[0];
                    var all = gw.ByScope.Where(kv => kv.Key.StartsWith(gateway + "\t", StringComparison.OrdinalIgnoreCase)).ToList();
                    if (all.Count == 0) all = gw.ByScope.ToList();
                    if (!l.PerUser)
                    {
                        var sum = new Dictionary<DateTime, decimal>();
                        foreach (var kv in all) foreach (var d in kv.Value) sum[d.Key] = sum.GetValueOrDefault(d.Key) + d.Value;
                        result.Add(Series(l, gw.AsOf, sum, null, all.Count, 0));
                        break;
                    }
                    var each = all.Select(kv => (Key: kv.Key.Split('\t')[1], Daily: kv.Value, M: gw.AsOf is { } a ? SpendLimits.Where(l.Amount, l.Period, a, kv.Value) : null)).ToList();
                    var worst = each.OrderByDescending(x => x.M?.Projected ?? x.M?.Spent ?? 0).FirstOrDefault();
                    var over = SpendLimits.IsShort(l.Period) ? 0 : each.Count(x => x.M is { } m && (m.Reached || m.ReachedOn is not null));
                    result.Add(Series(l, gw.AsOf, worst.Daily ?? new Dictionary<DateTime, decimal>(), worst.Key, each.Count, over));
                    break;
                }
                case "GitHubBudget" when l.ScopeType is "Enterprise" or "Organization":
                {
                    var daily = new Dictionary<DateTime, decimal>();
                    foreach (var kv in gh.ByScope.Where(kv => l.ScopeType == "Enterprise" || string.Equals(kv.Key, l.Scope, StringComparison.OrdinalIgnoreCase)))
                        foreach (var d in kv.Value) daily[d.Key] = daily.GetValueOrDefault(d.Key) + d.Value;
                    result.Add(Series(l, gh.AsOf, daily, null, 1, 0));
                    break;
                }
                case "GitHubBudget":
                    result.Add(new(l, null, null, null, 0, 0, [], null, null, "GitHub's bill is loaded by organization: a cost center's or a person's budget is not measured here."));
                    break;
                case "CopilotUser" when people is not null:
                {
                    /* Each person's credits a month, as Credit use estimates them: those over their limit. A group's policy
                       is measured over everyone, the group's members not being loaded. */
                    var billed = people.People.Where(p => p.Enabled && p.Month.Credits > 0).ToList();
                    var lim = l.Amount ?? 0;
                    var overN = lim > 0 ? billed.Count(p => p.Month.Credits > lim) : 0;
                    var top = billed.Count == 0 ? 0 : billed.Max(p => p.Month.Credits);
                    var pos = new SpendLimits.Position(l.Amount, Math.Round(top, 0), lim > 0 ? (double)(top / lim) : null, 0, null, null, lim > 0 && top > lim, false);
                    result.Add(new(l, pos, c!.End, null, billed.Count, overN, [], null, null,
                        l.ScopeType == "Group" ? "Measured over everyone billed: the group's members are not loaded." : null)
                    { Warn = overN > 0 && l.WarnAtPercent is not null });
                    break;
                }
                case "CopilotUser":
                    result.Add(new(l, null, null, null, 0, 0, [], null, null, "Measured on Forecasting › Spend limits, from Credit use's estimate of each person's credits."));
                    break;
                default:
                    result.Add(new(l, null, null, null, 0, 0, [], null, null, "Not measured here."));
                    break;
            }
        }
        return result;
    }

    /// <summary>Each limit's position at the end of the last period that is over, kept once (dash.SpendLimitSnapshot).</summary>
    public async Task<int> SnapshotAsync(IReadOnlyList<Measured> measured)
    {
        var n = 0;
        foreach (var m in measured.Where(m => m.AsOf is not null && m.Position is not null && m.Limit.Period is SpendLimits.Monthly or SpendLimits.Weekly or SpendLimits.Yearly))
        {
            var (start, _) = SpendLimits.PeriodOf(m.Limit.Period, m.AsOf!.Value);
            var (ps, pe) = SpendLimits.PeriodOf(m.Limit.Period, start.AddDays(-1));
            if (!m.Daily.Keys.Any(k => k <= ps)) continue;   // the period before is not all loaded
            var spent = m.Daily.Where(kv => kv.Key >= ps && kv.Key <= pe).Sum(kv => kv.Value);
            n += await db.ExecuteAsync(@"
INSERT INTO dash.SpendLimitSnapshot (LimitId, PeriodStart, PeriodEnd, Amount, Spent)
SELECT @id, @ps, @pe, @amount, @spent WHERE NOT EXISTS (SELECT 1 FROM dash.SpendLimitSnapshot WHERE LimitId = @id AND PeriodStart = @ps);",
                new { id = m.Limit.LimitId, ps, pe, amount = m.Limit.Amount, spent });
        }
        return n;
    }
}
