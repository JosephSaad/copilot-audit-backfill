using System.Globalization;
using CopilotInsights.Infrastructure;

namespace CopilotInsights.Data;

/// <summary>One person's use and its estimate, a month.</summary>
public sealed record CreditPerson(int UserKey, string? Name, string Upn, string? Department, bool Licensed, CreditUse Use, CreditEstimate Month)
{
    /// <summary>
    /// The account is enabled today (3.1.3). Counts of people and rankings of named people take only these; the credits and
    /// their cost are everyone's, so a total adds up every person here.
    /// </summary>
    public bool Enabled { get; init; }
}

/// <summary>One agent's billed use and its estimate, a month.</summary>
public sealed record CreditAgent(int AgentKey, string Name, string? Source, int People, CreditUse Use, CreditEstimate Month);

/// <summary>Everyone's credit use in the window, a month at a time, with the rates it was estimated at and the days it covers.</summary>
public sealed record CreditView(IReadOnlyList<CreditPerson> People, IReadOnlyList<CreditAgent> Agents, CreditRates Rates, int Days, string Currency);

/// <summary>The credit estimate's totals over everyone (each person estimated, then added up, as Credit use adds them), the people billed, and each agent's estimate.</summary>
public sealed record CreditTotals(decimal Credits, decimal Cost, int PeopleBilled, IReadOnlyList<CreditAgent> Agents, CreditRates Rates, int Days, string Currency);

/// <summary>
/// Reads Copilot use from bi.FactInteraction for <see cref="CreditRules"/>: per person and per agent in the
/// period and team shown, each interaction marked as with an agent or not, billed or not (an agent without a license at
/// the time, or in a Copilot Studio channel), grounded in work data (resources read, or Copilot's own work-mode mark),
/// and calling another agent. One pass over the fact, grouped twice.
/// </summary>
public sealed class CreditData(Db db, Settings settings)
{
    static decimal Dec(string? v, decimal fallback) => decimal.TryParse(v, NumberStyles.Number, CultureInfo.InvariantCulture, out var d) && d >= 0 ? d : fallback;

    public async Task<CreditRates> RatesAsync()
    {
        var price = 0.01m;
        if (await db.ScalarAsync<int>("SELECT CASE WHEN OBJECT_ID(N'dash.CostPrice', N'U') IS NULL THEN 0 ELSE 1 END;") == 1)
            price = await db.ScalarAsync<decimal?>("SELECT MonthlyPrice FROM dash.CostPrice WHERE Item = N'COPILOT_STUDIO_PAYG_CREDIT';") ?? price;
        return new(Dec(await settings.StringAsync("CreditsPerAnswer"), 2), Dec(await settings.StringAsync("CreditsPerGrounding"), 10),
            Dec(await settings.StringAsync("CreditsPerAction"), 5), price,
            (int)Dec(await settings.StringAsync("TokensPerTurn"), 1500), (int)Dec(await settings.StringAsync("TokensPerResource"), 1000),
            (int)Dec(await settings.StringAsync("TokensPerAnswer"), 400));
    }

    sealed class Row
    {
        public int G { get; set; } public int Key { get; set; } public long I { get; set; } public long A { get; set; } public long B { get; set; } public long BW { get; set; }
        public long BA { get; set; } public long R { get; set; } public long M { get; set; } public int Lic { get; set; } public int People { get; set; }
    }

    /// <summary>The credit estimate, kept once worked out (see <see cref="Db.KeptAsync"/>).</summary>
    public Task<CreditView> BuildAsync(ReportContext c) => db.KeptAsync("CreditData.Build", c, () => BuildFreshAsync(c));

    /// <summary>
    /// The estimate's totals and its agents, without anyone's name: for Agents › Agents costing credits, which shows
    /// agents and the people's totals only. The same rows and the same estimate as <see cref="BuildAsync"/>, so the pages
    /// give one figure, without reading 210,000 people's names and departments it does not show.
    /// </summary>
    public Task<CreditTotals> TotalsAsync(ReportContext c) => db.KeptAsync("CreditData.Totals", c, async () =>
    {
        var (byUser, agents, rates, days, cur) = await ReadAsync(c);
        decimal credits = 0, cost = 0;
        var billed = 0;
        foreach (var r in byUser)
        {
            var use = Use(r);
            var month = CreditRules.Monthly(CreditRules.Estimate(use, rates), days);
            credits += month.Credits; cost += month.Cost;
            if (use.Billed > 0 && use.WithAgent > 0) billed++;
        }
        return new CreditTotals(credits, cost, billed, agents, rates, days, cur);
    });

    static CreditUse Use(Row r) => new(r.I, r.A, r.B, r.BW, r.BA, r.R, r.M / 2);   // MessageCount counts the prompt and the answer

    async Task<CreditView> BuildFreshAsync(ReportContext c)
    {
        /* The names do not depend on the use: they are read beside it. */
        var whoTask = db.QueryAsync<(int UserKey, string? DisplayName, string UserPrincipalName, string? Department, bool Enabled)>(
                "SELECT UserKey, DisplayName, UserPrincipalName, Department, CAST(CASE WHEN AccountEnabled = 1 THEN 1 ELSE 0 END AS BIT) FROM bi.DimUser;");
        var (byUser, byAgentList, rates, days, cur) = await ReadAsync(c);
        var who = (await whoTask).ToDictionary(u => u.UserKey);
        var people = byUser.Select(r =>
        {
            var u = who.TryGetValue(r.Key, out var x) ? x : (r.Key, null, $"(user {r.Key})", null, false);
            var use = Use(r);
            return new CreditPerson(r.Key, u.DisplayName, u.UserPrincipalName, u.Department, r.Lic == 1, use, CreditRules.Monthly(CreditRules.Estimate(use, rates), days)) { Enabled = u.Enabled };
        }).ToList();
        return new(people, byAgentList, rates, days, cur);
    }

    /// <summary>Each person's use (rows grouped by person) and each agent's, estimated; with the rates, the days and the currency.</summary>
    async Task<(List<Row> ByUser, List<CreditAgent> Agents, CreditRates Rates, int Days, string Currency)> ReadAsync(ReportContext c)
    {
        /* The agents and the rates do not depend on the use: they are read beside it. */
        var agentsTask = db.QueryAsync<(int AgentKey, string AgentName, string? AgentSource)>("SELECT AgentKey, AgentName, AgentSource FROM dash.Agent;");
        var ratesTask = RatesAsync();
        var firstTask = OpsReports.FirstDayAsync(db);
        var cur = await settings.StringAsync("LicenseCurrency") ?? "USD";
        /* One row a person and agent, then grouped twice (by person, by agent). The rows are the credit counts the
           refresh keeps per person and agent (ReportContext.PerCredit, by the rules it states: billed is an agent used by
           someone not licensed at the time, or in a Copilot Studio channel), or the same worked out from the fact before
           it has built them. A person and agent being one row, its billed rows count the people. Where the refresh keeps
           the period's rows they are grouped straight from its columnstore, twice: copying two million of them into a
           temporary table first cost more than grouping them. Summed from the days or the fact, they are worked out once. */
        var rolled = c.RolledCreditDays.Contains(c.Choice);
        var all = await db.QueryAsync<Row>(rolled ? $@"
SELECT 0 AS G, p.UserKey AS [Key], SUM(p.I) AS I, SUM(p.A) AS A, SUM(p.B) AS B, SUM(p.BW) AS BW, SUM(p.BA) AS BA, SUM(p.R) AS R, SUM(p.M) AS M,
       MAX(p.Lic) AS Lic, MAX(p.AnyBilled) AS People
FROM {c.PerCredit()} AS p {c.Scope("p.UserKey")} GROUP BY p.UserKey
UNION ALL
SELECT 1, p.AgentKey, SUM(p.I), SUM(p.A), SUM(p.B), SUM(p.BW), SUM(p.BA), SUM(p.R), SUM(p.M), MAX(p.Lic),
       COUNT(CASE WHEN p.AnyBilled = 1 AND {ReportContext.IsEnabled()} THEN 1 END)   -- billed people enabled today (3.1.3); the use is everyone's
FROM {c.PerCredit()} AS p {c.Scope("p.UserKey")} {ReportContext.EnabledJoin("p.UserKey", left: true)} GROUP BY p.AgentKey;" : $@"
SELECT p.UserKey, p.AgentKey, p.I, p.A, p.B, p.BW, p.BA, p.R, p.M, p.Lic, p.AnyBilled
INTO #ua
FROM {c.PerCredit()} AS p {c.Scope("p.UserKey")};
SELECT 0 AS G, UserKey AS [Key], SUM(I) AS I, SUM(A) AS A, SUM(B) AS B, SUM(BW) AS BW, SUM(BA) AS BA, SUM(R) AS R, SUM(M) AS M,
       MAX(Lic) AS Lic, MAX(AnyBilled) AS People
FROM #ua GROUP BY UserKey
UNION ALL
SELECT 1, ua.AgentKey, SUM(ua.I), SUM(ua.A), SUM(ua.B), SUM(ua.BW), SUM(ua.BA), SUM(ua.R), SUM(ua.M), MAX(ua.Lic),
       COUNT(CASE WHEN ua.AnyBilled = 1 AND {ReportContext.IsEnabled()} THEN 1 END)   -- billed people enabled today (3.1.3); the use is everyone's
FROM #ua AS ua {ReportContext.EnabledJoin("ua.UserKey", left: true)} GROUP BY ua.AgentKey;", c.Args(), timeout: 300);
        var byUser = all.Where(r => r.G == 0).ToList();
        var byAgent = all.Where(r => r.G == 1 && r.A > 0).ToList();
        var rates = await ratesTask;
        var days = OpsReports.DaysCovered(c, await firstTask);
        var agents = (await agentsTask).GroupBy(a => a.AgentKey).ToDictionary(g => g.Key, g => g.First());
        var byAgentList = byAgent.Where(r => agents.ContainsKey(r.Key)).Select(r =>
        {
            var a = agents[r.Key];
            var use = Use(r);
            return new CreditAgent(r.Key, a.AgentName, a.AgentSource, r.People, use, CreditRules.Monthly(CreditRules.Estimate(use, rates), days));
        }).ToList();
        return (byUser, byAgentList, rates, days, cur);
    }
}
