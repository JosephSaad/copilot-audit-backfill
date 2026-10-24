using CopilotInsights.Infrastructure;

namespace CopilotInsights.Data;

/// <summary>One chargeback organization's position: its reservation, the add-on licenses its people hold, and what follows.</summary>
public sealed record ReservationOrg(string OrgId, string Name, string? LeaderUpn, string? LeaderName, string? CostCenter, int Reserved, int Allocated,
    int People, bool LeaderMissing, bool SharedLeader)
{
    public int Unused => ReservationRules.Unused(Reserved, Allocated);
    public int Over => ReservationRules.Over(Reserved, Allocated);
    public int Billable => ReservationRules.Billable(Reserved, Allocated);
    public double? Utilization => ReservationRules.Utilization(Reserved, Allocated);
    public bool DataException => LeaderMissing || SharedLeader;
    public ReservationStatus Status => ReservationRules.Status(Reserved, Allocated, DataException);
}

/// <summary>
/// The reservation position on one day: today's, from the licenses read last night (the current operational
/// state, never certified), or a month-end snapshot as it was kept. Unmapped: add-on holders under no listed leader;
/// Technology: holders in the Technology-funded register. Neither is any organization's.
/// </summary>
public sealed record ReservationView(DateTime AsOf, DateTime? SnapshotTaken, IReadOnlyList<ReservationOrg> Orgs, int Unmapped, int Technology,
    decimal? Rate, string Currency)
{
    public bool IsSnapshot => SnapshotTaken is not null;
    public int Reserved => Orgs.Sum(o => o.Reserved);
    public int Allocated => Orgs.Sum(o => o.Allocated);
    public int Unused => Orgs.Sum(o => o.Unused);
    public int Over => Orgs.Sum(o => o.Over);
    public int Billable => Orgs.Sum(o => o.Billable);
    public decimal? Monthly => ReservationRules.Monthly(Billable, Rate);
    public decimal? Remaining => ReservationRules.Remaining(Billable, Rate, AsOf);
}

public sealed class ReservationData(Db db, Settings settings)
{
    public const string Unmapped = "~unmapped", Technology = "~technology";

    public async Task<bool> ReadyAsync() =>
        await db.ScalarAsync<int>("SELECT CASE WHEN OBJECT_ID(N'dash.Reservation', N'U') IS NULL OR OBJECT_ID(N'dash.fnReservationMember', N'IF') IS NULL THEN 0 ELSE 1 END;") == 1;

    /// <summary>The rate in force on a day, and its currency; no rate: null (Pending Finance Decision).</summary>
    public async Task<(decimal? Rate, string Currency)> RateAsync(DateTime asOf)
    {
        var cur = await settings.StringAsync("LicenseCurrency") ?? "USD";
        var r = (await db.QueryAsync<(decimal Rate, string? Currency)>(
            "SELECT TOP (1) MonthlyRate, Currency FROM dash.ReservationRate WHERE EffectiveFrom <= @asOf ORDER BY EffectiveFrom DESC;", new { asOf })).ToList();
        return r.Count == 0 ? (null, cur) : (r[0].Rate, r[0].Currency ?? cur);
    }

    /// <summary>The day of the licenses last read (bi.FactLicensedDay), or null before the first.</summary>
    public async Task<DateTime?> LicensedDayAsync()
    {
        var k = await db.ScalarAsync<int?>("SELECT MAX(DateKey) FROM bi.FactLicensedDay;");
        return k is { } key ? DateTime.ParseExact(key.ToString(), "yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture) : null;
    }

    sealed class OrgRow
    {
        public string OrgId { get; set; } = "";
        public string Name { get; set; } = "";
        public string? LeaderUpn { get; set; }
        public string? LeaderName { get; set; }
        public string? CostCenter { get; set; }
        public int Reserved { get; set; }
        public int Allocated { get; set; }
        public int People { get; set; }
        public bool LeaderMissing { get; set; }
        public int LeaderRows { get; set; }
    }

    /* A viewer who sees part of the organization sees the organizations led from inside it. */
    static string Scoped(ReportContext c, string leader) =>
        c.Root is null ? "" : $"AND {leader} IN (SELECT du.UserPrincipalName FROM bi.DimUser AS du {c.Scope("du.UserKey")})";

    /* The add-on licenses held on a day, each marked Technology-funded or not; Copilot held only inside a suite is not one. */
    static string Held(ReportContext c) => $@"
held AS (SELECT ld.UserKey, CAST(CASE WHEN t.Upn IS NULL THEN 0 ELSE 1 END AS BIT) AS Tech
         FROM bi.FactLicensedDay AS ld {c.Scope("ld.UserKey")}
         JOIN bi.DimUser AS u ON u.UserKey = ld.UserKey
         LEFT JOIN dash.TechnologyAllocation AS t ON t.Upn = u.UserPrincipalName AND t.EffectiveFrom <= @day
         WHERE ld.DateKey = @k AND NOT EXISTS (SELECT 1 FROM dash.CopilotSuiteOnly AS so WHERE so.Upn = u.UserPrincipalName))";

    /// <summary>Today's position, from the licenses read last night.</summary>
    public async Task<ReservationView?> CurrentAsync(ReportContext c)
    {
        if (await LicensedDayAsync() is not { } day) return null;
        var k = int.Parse(day.ToString("yyyyMMdd"));
        var rows = await db.QueryAsync<OrgRow>($@"
WITH {Held(c)},
m AS (SELECT ReservationId, UserKey FROM dash.fnReservationMember(@day)),
r AS (SELECT * FROM dash.Reservation WHERE EffectiveFrom <= @day AND (EffectiveTo IS NULL OR EffectiveTo >= @day)),
lr AS (SELECT LeaderUpn, COUNT(DISTINCT OrgId) AS N FROM r WHERE LeaderUpn IS NOT NULL GROUP BY LeaderUpn)
/* 2.7.0: a leader is missing who is not in today's directory, or whose account is disabled (bi.DimUser keeps everyone ever
   seen, so a leader who had left was never flagged, and their organization read as all reserved, nothing held). The
   People count no page showed is gone: it worked out every member a second time. */
SELECT r.OrgId, MAX(r.Name) AS Name, MAX(r.LeaderUpn) AS LeaderUpn, MAX(l.DisplayName) AS LeaderName, MAX(r.CostCenter) AS CostCenter,
       SUM(r.Reserved) AS Reserved,
       (SELECT COUNT(*) FROM m JOIN held AS h ON h.UserKey = m.UserKey AND h.Tech = 0 JOIN r AS r2 ON r2.ReservationId = m.ReservationId WHERE r2.OrgId = r.OrgId) AS Allocated,
       CAST(MAX(CASE WHEN r.LeaderUpn IS NOT NULL AND (l.Upn IS NULL OR l.AccountEnabled = 0) THEN 1 ELSE 0 END) AS BIT) AS LeaderMissing,
       ISNULL(MAX(lr.N), 0) AS LeaderRows
FROM r LEFT JOIN dash.Person AS l ON l.Upn = r.LeaderUpn
LEFT JOIN lr ON lr.LeaderUpn = r.LeaderUpn
WHERE 1 = 1 {Scoped(c, "r.LeaderUpn")}
GROUP BY r.OrgId
ORDER BY SUM(r.Reserved) DESC, r.OrgId;", c.Args(new { k, day }));
        var (unmapped, tech) = (await db.QueryAsync<(int Unmapped, int Tech)>($@"
WITH {Held(c)}
SELECT COUNT(CASE WHEN h.Tech = 0 AND m.UserKey IS NULL THEN 1 END), COUNT(CASE WHEN h.Tech = 1 THEN 1 END)
FROM held AS h LEFT JOIN dash.fnReservationMember(@day) AS m ON m.UserKey = h.UserKey;   -- one row a person: a join, as a subquery cannot sit inside COUNT", c.Args(new { k, day }))).Single();
        var (rate, cur) = await RateAsync(day);
        return new(day, null, rows.Select(Org).ToList(), unmapped, tech, rate, cur);
    }

    static ReservationOrg Org(OrgRow r) => new(r.OrgId, r.Name, r.LeaderUpn, r.LeaderName, r.CostCenter, r.Reserved, r.Allocated, r.People,
        r.LeaderMissing, r.LeaderUpn is not null && r.LeaderRows > 1);

    /// <summary>A month-end position as it was kept, or null when none was taken that day. The unmapped and Technology
    /// counts are the whole organization's, so a viewer who sees part of it does not get them.</summary>
    public async Task<ReservationView?> SnapshotAsync(ReportContext c, DateTime date)
    {
        var filter = c.Root is null ? "" : $"AND s.OrgId NOT LIKE N'~%' {Scoped(c, "s.LeaderUpn")}";
        var rows = await db.QueryAsync<(string OrgId, string Name, string? LeaderUpn, string? CostCenter, int Reserved, int Allocated, decimal? Rate, DateTime Taken)>($@"
SELECT s.OrgId, s.Name, s.LeaderUpn, s.CostCenter, s.Reserved, s.Allocated, s.MonthlyRate, s.TakenUtc
FROM dash.ReservationSnapshot AS s
WHERE s.SnapshotDate = @date {filter}
ORDER BY s.Reserved DESC, s.OrgId;", c.Args(new { date }));
        if (rows.Count == 0) return null;
        var names = (await db.QueryAsync<(string Upn, string? Name)>(
            "SELECT UserPrincipalName, DisplayName FROM bi.DimUser WHERE UserPrincipalName IN (SELECT LeaderUpn FROM dash.ReservationSnapshot WHERE SnapshotDate = @date);", new { date }))
            .GroupBy(x => x.Upn, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().Name, StringComparer.OrdinalIgnoreCase);
        var orgs = rows.Where(r => !r.OrgId.StartsWith('~')).Select(r => new ReservationOrg(r.OrgId, r.Name, r.LeaderUpn,
            r.LeaderUpn is not null && names.TryGetValue(r.LeaderUpn, out var n) ? n : null, r.CostCenter, r.Reserved, r.Allocated, 0, false, false)).ToList();
        var cur = await settings.StringAsync("LicenseCurrency") ?? "USD";
        return new(date, rows.Max(r => r.Taken), orgs, rows.Where(r => r.OrgId == Unmapped).Sum(r => r.Allocated),
            rows.Where(r => r.OrgId == Technology).Sum(r => r.Allocated), rows.Select(r => r.Rate).FirstOrDefault(x => x is not null), cur);
    }
    /// <summary>
    /// Every snapshot kept, with each one's organizations, newest first: for a viewer who sees part of the organization,
    /// only those led from inside it, as <see cref="SnapshotAsync"/> shows one (every viewer saw the whole bank's).
    /// </summary>
    public async Task<List<(DateTime Date, DateTime Taken, List<ReservationOrg> Orgs, decimal? Rate)>> SnapshotsAsync(ReportContext c)
    {
        var rows = await db.QueryAsync<(DateTime Date, DateTime Taken, string OrgId, int Reserved, int Allocated, decimal? Rate)>($@"
SELECT s.SnapshotDate, s.TakenUtc, s.OrgId, s.Reserved, s.Allocated, s.MonthlyRate FROM dash.ReservationSnapshot AS s
WHERE s.OrgId NOT LIKE N'~%' {Scoped(c, "s.LeaderUpn")};", c.Args());
        return rows.GroupBy(r => r.Date).OrderByDescending(g => g.Key)
            .Select(g => (g.Key, g.Max(r => r.Taken), g.Select(r => new ReservationOrg(r.OrgId, "", null, null, null, r.Reserved, r.Allocated, 0, false, false)).ToList(),
                g.Select(r => r.Rate).FirstOrDefault(x => x is not null))).ToList();
    }
}
