using CopilotInsights.Infrastructure;

namespace CopilotInsights.Data;

/// <summary>
/// Model tokens through the AI gateway, by business unit: counts only, never money. A business unit is an L2 and L3
/// executive pair (the people at levels 2 and 3 of the org chart, TokenUnitLevels). Each value the gateway stamps is
/// put in a unit by the first of: a route in effect that day (dash.TokenRoute); the org chart, when the value is a
/// person's sign-in (their L2 and L3, themselves included); else it is Unmapped. A value the gateway left empty is Not stamped.
/// </summary>
public sealed class GatewayTokens(Db db, Settings settings)
{
    public const string Route = "Route", OrgChart = "OrgChart", Unmapped = "Unmapped", NotStamped = "NotStamped";

    public static string HowLabel(string how) => how switch
    {
        Route => "Routed", OrgChart => "From the org chart", NotStamped => "Not stamped by the gateway", _ => "Unmapped",
    };

    /// <summary>The two org chart levels a unit is made of: TokenUnitLevels, "2,3" by default.</summary>
    public static (int L2, int L3) ParseLevels(string? s)
    {
        var parts = (s ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => int.TryParse(p, out var n) ? n : 0).Where(n => n is >= 1 and <= 15).Distinct().OrderBy(n => n).ToList();
        return parts.Count >= 2 ? (parts[0], parts[1]) : parts.Count == 1 ? (parts[0], parts[0] + 1) : (2, 3);
    }

    public async Task<bool> ReadyAsync() =>
        await db.ScalarAsync<int>("SELECT CASE WHEN OBJECT_ID(N'audit.GatewayTokenDay', N'U') IS NULL OR OBJECT_ID(N'dash.TokenRoute', N'U') IS NULL THEN 0 ELSE 1 END;") == 1;

    public async Task<bool> LoadedAsync() =>
        await db.ScalarAsync<int>("SELECT CASE WHEN EXISTS (SELECT 1 FROM audit.GatewayTokenDay) THEN 1 ELSE 0 END;") == 1;

    public sealed class Line
    {
        public string Value { get; set; } = "";
        public string How { get; set; } = Unmapped;
        public string? L2Upn { get; set; }
        public string? L2Name { get; set; }
        public string? L3Upn { get; set; }
        public string? L3Name { get; set; }
        public DateTime Month { get; set; }
        public string Model { get; set; } = "";
        public long Prompt { get; set; }
        public long Completion { get; set; }
        public long Total { get; set; }
        public long Calls { get; set; }
    }

    /// <summary>
    /// The tokens from <paramref name="from"/> to <paramref name="to"/> (UTC days), a line per gateway value, unit,
    /// month and model. The values are few (Azure Monitor keeps at most 100 a dimension), so each is put in its unit
    /// once a day it appears, and its person's chain is walked up dash.Person once.
    /// </summary>
    public async Task<List<Line>> LinesAsync(DateTime from, DateTime to)
    {
        var (l2, l3) = ParseLevels(await settings.StringAsync("TokenUnitLevels"));
        return await db.QueryAsync<Line>(@"
WITH t AS (SELECT BusinessUnit AS V, [Day], Model, SUM(ISNULL(PromptTokens, 0)) AS P, SUM(ISNULL(CompletionTokens, 0)) AS C,
                  SUM(ISNULL(TotalTokens, ISNULL(PromptTokens, 0) + ISNULL(CompletionTokens, 0))) AS T, SUM(ISNULL(Calls, 0)) AS N
           FROM audit.GatewayTokenDay WHERE [Day] BETWEEN @from AND @to GROUP BY BusinessUnit, [Day], Model),
vals AS (SELECT DISTINCT V FROM t WHERE V <> N''),
chain AS (SELECT v.V, p.Upn, p.ManagerUpn, p.[Level], 0 AS D FROM vals AS v JOIN dash.Person AS p ON p.Upn = v.V
          UNION ALL
          SELECT c.V, m.Upn, m.ManagerUpn, m.[Level], c.D + 1 FROM chain AS c JOIN dash.Person AS m ON m.Upn = c.ManagerUpn
          WHERE c.D < 20 AND c.[Level] > @l2),
person AS (SELECT V, MAX(CASE WHEN [Level] = @l2 THEN Upn END) AS L2, MAX(CASE WHEN [Level] = @l3 THEN Upn END) AS L3 FROM chain GROUP BY V),
u AS (SELECT t.V, t.[Day], t.Model, t.P, t.C, t.T, t.N,
             CASE WHEN t.V = N'' THEN 'NotStamped' WHEN ro.L2Upn IS NOT NULL THEN 'Route' WHEN pe.L2 IS NOT NULL THEN 'OrgChart' ELSE 'Unmapped' END AS How,
             CASE WHEN ro.L2Upn IS NOT NULL THEN ro.L2Upn ELSE pe.L2 END AS L2,
             CASE WHEN ro.L2Upn IS NOT NULL THEN ro.L3Upn ELSE pe.L3 END AS L3
      FROM t
      OUTER APPLY (SELECT TOP (1) r.L2Upn, r.L3Upn FROM dash.TokenRoute AS r
                   WHERE r.GatewayValue = t.V AND r.EffectiveFrom <= t.[Day] AND (r.EffectiveTo IS NULL OR r.EffectiveTo >= t.[Day])
                   ORDER BY r.EffectiveFrom DESC) AS ro
      LEFT JOIN person AS pe ON pe.V = t.V)
SELECT u.V AS Value, u.How, u.L2 AS L2Upn, p2.DisplayName AS L2Name, u.L3 AS L3Upn, p3.DisplayName AS L3Name,
       DATEFROMPARTS(YEAR(u.[Day]), MONTH(u.[Day]), 1) AS [Month], u.Model,
       SUM(u.P) AS Prompt, SUM(u.C) AS Completion, SUM(u.T) AS Total, SUM(u.N) AS Calls
FROM u
LEFT JOIN dash.Person AS p2 ON p2.Upn = u.L2
LEFT JOIN dash.Person AS p3 ON p3.Upn = u.L3
GROUP BY u.V, u.How, u.L2, p2.DisplayName, u.L3, p3.DisplayName, DATEFROMPARTS(YEAR(u.[Day]), MONTH(u.[Day]), 1), u.Model
OPTION (MAXRECURSION 30);", new { from = from.Date, to = to.Date, l2, l3 });
    }

    /// <summary>The newest day loaded, or null.</summary>
    public Task<DateTime?> LastDayAsync() => db.ScalarAsync<DateTime?>("SELECT MAX([Day]) FROM audit.GatewayTokenDay;");

    /// <summary>Foundry's own token count over the days, to show what did not go through the gateway; null when Foundry is not loaded.</summary>
    public async Task<long?> FoundryTokensAsync(DateTime from, DateTime to) =>
        await db.ScalarAsync<int>("SELECT CASE WHEN OBJECT_ID(N'audit.FoundryUsageDay', N'U') IS NOT NULL AND EXISTS (SELECT 1 FROM audit.FoundryUsageDay) THEN 1 ELSE 0 END;") == 1
            ? await db.ScalarAsync<long?>("SELECT SUM(CAST(ISNULL(TotalTokens, ISNULL(InputTokens, 0) + ISNULL(OutputTokens, 0)) AS BIGINT)) FROM audit.FoundryUsageDay WHERE [Day] BETWEEN @from AND @to;", new { from = from.Date, to = to.Date }) ?? 0
            : null;

    public sealed class RouteRow
    {
        public int RouteId { get; set; }
        public string GatewayValue { get; set; } = "";
        public string L2Upn { get; set; } = "";
        public string? L2Name { get; set; }
        public string? L3Upn { get; set; }
        public string? L3Name { get; set; }
        public DateTime EffectiveFrom { get; set; }
        public DateTime? EffectiveTo { get; set; }
        public string? Note { get; set; }
    }

    public Task<List<RouteRow>> RoutesAsync() => db.QueryAsync<RouteRow>(@"
SELECT r.RouteId, r.GatewayValue, r.L2Upn, p2.DisplayName AS L2Name, r.L3Upn, p3.DisplayName AS L3Name, r.EffectiveFrom, r.EffectiveTo, r.Note
FROM dash.TokenRoute AS r LEFT JOIN dash.Person AS p2 ON p2.Upn = r.L2Upn LEFT JOIN dash.Person AS p3 ON p3.Upn = r.L3Upn
ORDER BY r.GatewayValue, r.EffectiveFrom;");

    /// <summary>A unit's name: "L2 › L3", the L2 alone for the whole of its unit.</summary>
    public static string UnitName(string? l2Name, string? l2Upn, string? l3Name, string? l3Upn) =>
        (l2Name ?? l2Upn ?? "") + (l3Upn is null ? "" : " › " + (l3Name ?? l3Upn));

    /// <summary>One route as typed, checked. Pure, for the tests.</summary>
    public sealed record RouteInput(string Value, string L2Upn, string? L3Upn, DateTime From, DateTime? To, string? Note);

    public static RouteInput Check(string? value, string? l2, string? l3, DateTime? from, DateTime? to, string? note)
    {
        var v = (value ?? "").Trim();
        if (v.Length is 0 or > 256) throw new ArgumentException("give the value the gateway stamps as the business unit, as Unmapped values lists it.");
        var a = (l2 ?? "").Trim();
        if (a.Length == 0 || !a.Contains('@')) throw new ArgumentException("give the L2 executive's sign-in (name@domain).");
        var b = string.IsNullOrWhiteSpace(l3) ? null : l3.Trim();
        if (b is not null && !b.Contains('@')) throw new ArgumentException("the L3 executive is a sign-in (name@domain), or empty for the L2's whole unit.");
        if (from is null) throw new ArgumentException("give the date the route applies from.");
        if (to is not null && to < from) throw new ArgumentException("the end date comes after the start.");
        var n = string.IsNullOrWhiteSpace(note) ? null : note.Trim() is var x && x.Length > 400 ? x[..400] : note.Trim();
        return new(v, a, b, from.Value.Date, to?.Date, n);
    }
}
