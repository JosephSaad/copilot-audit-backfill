using System.Globalization;
using CopilotInsights.Infrastructure;

namespace CopilotInsights.Data;

/// <summary>One person's use and its estimate, a month.</summary>
public sealed record CreditPerson(int UserKey, string? Name, string Upn, string? Department, bool Licensed, CreditUse Use, CreditEstimate Month);

/// <summary>One agent's billed use and its estimate, a month.</summary>
public sealed record CreditAgent(int AgentKey, string Name, string? Source, int People, CreditUse Use, CreditEstimate Month);

/// <summary>Everyone's credit use in the window, a month at a time, with the rates it was estimated at and the days it covers.</summary>
public sealed record CreditView(IReadOnlyList<CreditPerson> People, IReadOnlyList<CreditAgent> Agents, CreditRates Rates, int Days, string Currency);

/// <summary>
/// Reads Copilot use from bi.FactInteraction for <see cref="CreditRules"/> (2.6.6): per person and per agent in the
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

    public async Task<CreditView> BuildAsync(ReportContext c)
    {
        var rates = await RatesAsync();
        var cur = await settings.StringAsync("LicenseCurrency") ?? "USD";
        var days = OpsReports.DaysCovered(c, await OpsReports.FirstDayAsync(db));
        /* One pass over the fact, grouped twice (by person, by agent), with nothing copied out: at 120M interactions a
           temporary copy of the period was the cost of the page. Billed: an agent used by someone not licensed at the time
           (status 2; an unknown status is not taken for unlicensed), or in a Copilot Studio channel. */
        var all = await db.QueryAsync<Row>($@"
SELECT CASE WHEN GROUPING(fi.UserKey) = 0 THEN 0 ELSE 1 END AS G, COALESCE(fi.UserKey, fi.AgentKey) AS [Key],
       COUNT_BIG(*) AS I, SUM(x.IsAgent) AS A, SUM(x.Billed) AS B, SUM(x.Billed * x.Work) AS BW, SUM(x.Billed * x.Calls) AS BA,
       SUM(CAST(ISNULL(fi.AccessedResourceCount, 0) AS BIGINT)) AS R, SUM(CAST(ISNULL(fi.MessageCount, 1) AS BIGINT)) AS M,
       MAX(x.Lic) AS Lic, COUNT(DISTINCT CASE WHEN x.Billed = 1 THEN fi.UserKey END) AS People
FROM bi.FactInteraction AS fi {c.Scope("fi.UserKey")}
JOIN bi.DimAgent AS ag ON ag.AgentKey = fi.AgentKey
JOIN bi.DimApp AS ap ON ap.AppKey = fi.AppKey
CROSS APPLY (SELECT CASE WHEN ag.AgentSource = N'(no agent)' THEN 0 ELSE 1 END AS IsAgent,
                    CASE WHEN fi.LicenseStatusKey = 1 THEN 1 ELSE 0 END AS Lic,
                    CASE WHEN ag.AgentSource <> N'(no agent)' AND (fi.LicenseStatusKey = 2 OR ap.App = N'Copilot Studio') THEN 1 ELSE 0 END AS Billed,
                    CASE WHEN fi.AccessedResourceCount > 0 OR fi.UseKind IN (1, 3) THEN 1 ELSE 0 END AS Work,
                    CAST(ISNULL(fi.CalledAnotherAgent, 0) AS INT) AS Calls) AS x
WHERE fi.DateKey BETWEEN @k0 AND @k1
GROUP BY GROUPING SETS ((fi.UserKey), (fi.AgentKey));", c.Args(), timeout: 300);
        var byUser = all.Where(r => r.G == 0).ToList();
        var byAgent = all.Where(r => r.G == 1 && r.A > 0).ToList();
        static CreditUse Use(Row r) => new(r.I, r.A, r.B, r.BW, r.BA, r.R, r.M / 2);   // MessageCount counts the prompt and the answer
        var who = (await db.QueryAsync<(int UserKey, string? DisplayName, string UserPrincipalName, string? Department)>(
                "SELECT UserKey, DisplayName, UserPrincipalName, Department FROM bi.DimUser;")).ToDictionary(u => u.UserKey);
        var agents = (await db.QueryAsync<(int AgentKey, string AgentName, string? AgentSource)>("SELECT AgentKey, AgentName, AgentSource FROM dash.Agent;"))
            .GroupBy(a => a.AgentKey).ToDictionary(g => g.Key, g => g.First());
        var people = byUser.Select(r =>
        {
            var u = who.TryGetValue(r.Key, out var x) ? x : (r.Key, null, $"(user {r.Key})", null);
            var use = Use(r);
            return new CreditPerson(r.Key, u.DisplayName, u.UserPrincipalName, u.Department, r.Lic == 1, use, CreditRules.Monthly(CreditRules.Estimate(use, rates), days));
        }).ToList();
        var byAgentList = byAgent.Where(r => agents.ContainsKey(r.Key)).Select(r =>
        {
            var a = agents[r.Key];
            var use = Use(r);
            return new CreditAgent(r.Key, a.AgentName, a.AgentSource, r.People, use, CreditRules.Monthly(CreditRules.Estimate(use, rates), days));
        }).ToList();
        return new(people, byAgentList, rates, days, cur);
    }
}
