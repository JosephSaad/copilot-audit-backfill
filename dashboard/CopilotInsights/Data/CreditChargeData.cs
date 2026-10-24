using System.Globalization;
using CopilotInsights.Infrastructure;

namespace CopilotInsights.Data;

/// <summary>
/// The paths a Power Platform environment's Copilot Credits take, as a two-path operating model defines them: a
/// line of business's Productivity or Business Application path, charged to it; Enterprise Shared, funded centrally and
/// never charged to a line of business; and Innovation, a sponsor's where it names one. Unmapped: no route in effect.
/// </summary>
public static class CreditPaths
{
    public const string Productivity = "Productivity", BusinessApplication = "BusinessApplication", EnterpriseShared = "EnterpriseShared",
        Innovation = "Innovation", Unmapped = "Unmapped";

    public static readonly string[] All = [Productivity, BusinessApplication, EnterpriseShared, Innovation];

    public static string Label(string path) => path switch
    {
        Productivity => "LoB Productivity",
        BusinessApplication => "LoB Business Application",
        EnterpriseShared => "Enterprise Shared",
        Innovation => "Innovation or development",
        _ => "Unmapped",
    };

    /// <summary>The paths charged to a line of business, which must name one.</summary>
    public static bool NeedsOrg(string path) => path is Productivity or BusinessApplication;
}

/// <summary>One environment's credits in a month under one route: what it used, how Microsoft billed it, who is charged.</summary>
public sealed record CreditLine(string EnvironmentName, string Path, string? OrgId, string? OrgName, string? CostCenter, string? BillingPolicy,
    string? FinancialOwner, decimal Credits, decimal PaygCredits, decimal? PaygCost, string? Currency)
{
    public decimal PrepaidCredits => Credits - PaygCredits;
}

/// <summary>A month's credit chargeback: the month to date from the credits loaded, or a month-end snapshot as it was kept.</summary>
public sealed record CreditChargeView(DateTime Month, DateTime DataTo, DateTime? SnapshotTaken, IReadOnlyList<CreditLine> Lines, decimal? Rate, string Currency)
{
    public bool IsSnapshot => SnapshotTaken is not null;
    public bool Complete => DataTo >= Month.AddMonths(1).AddDays(-1);
    public decimal Credits => Lines.Sum(l => l.Credits);
    public decimal Charged(Func<CreditLine, bool> which) => Lines.Where(which).Sum(l => l.Credits);
    public decimal? Charge(decimal credits) => CreditChargeRules.Charge(credits, Rate);
}

/// <summary>A prepaid credit commitment (the Copilot Credit Pre-Purchase Plan, P3) and its use to date.</summary>
public sealed record CreditCommitment(int CommitmentId, string Name, decimal Amount, string Currency, decimal DrawRate, DateTime StartDate, DateTime EndDate,
    string? Scope, string? Note);

/// <summary>Where a commitment stands: drawn, remaining, the burn rate, and when it runs out at that rate.</summary>
public sealed record CommitmentStatus(CreditCommitment Commitment, DateTime AsOf, decimal Drawn, decimal Remaining, decimal Beyond, decimal BurnPerDay,
    DateTime? RunsOut, decimal UnusedAtEnd, decimal PlanToDate, double? Variance, IReadOnlyList<(DateTime Day, decimal Drawn)> Daily)
{
    public int DaysLeft => Math.Max(0, (Commitment.EndDate - AsOf).Days);
    public bool Started => AsOf >= Commitment.StartDate;
    public bool Ended => AsOf > Commitment.EndDate;
}

/// <summary>The arithmetic, pure for the tests.</summary>
public static class CreditChargeRules
{
    /// <summary>What credits are charged back at a rate; no rate: null (Pending Finance Decision).</summary>
    public static decimal? Charge(decimal credits, decimal? rate) => rate is { } r ? Math.Round(credits * r, 2, MidpointRounding.AwayFromZero) : null;

    /// <summary>
    /// A commitment on a day, from what each day drew: drawn to date (capped at the amount; the rest is pay-as-you-go
    /// beyond it), the burn rate over the last 30 days with data, when the remainder runs out at that rate, what is left
    /// unused at the end of the term at that rate, the straight-line plan to date and the variance against it.
    /// </summary>
    public static CommitmentStatus Project(CreditCommitment c, DateTime asOf, IReadOnlyList<(DateTime Day, decimal Drawn)> daily)
    {
        var inTerm = daily.Where(d => d.Day >= c.StartDate && d.Day <= c.EndDate && d.Day <= asOf).OrderBy(d => d.Day).ToList();
        var used = inTerm.Sum(d => d.Drawn);
        var drawn = Math.Min(used, c.Amount);
        var beyond = Math.Max(0, used - c.Amount);
        var remaining = c.Amount - drawn;
        var last = inTerm.Count == 0 ? asOf : inTerm[^1].Day;
        var window = inTerm.Where(d => d.Day > last.AddDays(-30)).ToList();
        var span = Math.Max(1, Math.Min(30, (last - c.StartDate).Days + 1));
        var burn = window.Count == 0 ? 0 : window.Sum(d => d.Drawn) / span;
        /* Used up: the day the cumulative draw reached the amount, not the last day loaded. */
        DateTime? runsOut = null;
        if (remaining <= 0)
        {
            decimal run = 0;
            foreach (var d in inTerm) { run += d.Drawn; if (run >= c.Amount) { runsOut = d.Day; break; } }
        }
        else if (burn > 0) runsOut = last.AddDays((double)Math.Ceiling(remaining / burn));
        var daysToEnd = Math.Max(0, (c.EndDate - last).Days);
        var unused = Math.Max(0, remaining - burn * daysToEnd);
        var term = Math.Max(1, (c.EndDate - c.StartDate).Days + 1);
        var elapsed = Math.Clamp(((asOf < c.EndDate ? asOf : c.EndDate) - c.StartDate).Days + 1, 0, term);
        var plan = Math.Round(c.Amount * elapsed / term, 2);
        double? variance = plan > 0 ? (double)((used - plan) / plan) : null;
        return new(c, asOf, drawn, remaining, beyond, Math.Round(burn, 2), runsOut, Math.Round(unused, 2), plan, variance, inTerm);
    }

    /// <summary>
    /// Whether a commitment needs attention: projected to run out more than the warning days before its term ends
    /// (pay-as-you-go after it), or to leave more than that many days' draw unused at the end. Null: nothing to say.
    /// </summary>
    public static string? Warning(CommitmentStatus s, int days)
    {
        if (days <= 0 || !s.Started || s.Ended) return null;
        var c = s.Commitment;
        if (s.RunsOut is { } r && r < c.EndDate.AddDays(-days))
            return r <= s.AsOf
                ? $"{c.Name} ran out on {r:d MMM yyyy}, {(c.EndDate - r).Days:#,0} days before its term ends: what is drawn since is pay-as-you-go."
                : $"{c.Name} is projected to run out on {r:d MMM yyyy}, {(c.EndDate - r).Days:#,0} days before its term ends: pay-as-you-go after that.";
        /* Left unused: more than the warning days' worth at the even plan, once a month of the term shows the rate
           (an idle commitment, drawing nothing, warns too). */
        var term = Math.Max(1, (c.EndDate - c.StartDate).Days + 1);
        if ((s.AsOf - c.StartDate).Days >= 30 && s.Remaining > 0 && s.UnusedAtEnd > c.Amount / term * days)
            return $"{c.Name} is projected to leave {s.UnusedAtEnd:#,0} {c.Currency} unused when its term ends on {c.EndDate:d MMM yyyy}" +
                   (s.BurnPerDay <= 0 ? ": nothing has drawn on it in the last 30 days." : ".");
        return null;
    }
}

/// <summary>2.7.2: the credit register, the month's chargeback, its snapshots, the wrong-path checks and the commitments.</summary>
public sealed class CreditChargeData(Db db, Settings settings)
{
    public async Task<bool> ReadyAsync() =>
        await db.ScalarAsync<int>("SELECT CASE WHEN OBJECT_ID(N'dash.CreditRoute', N'U') IS NULL OR OBJECT_ID(N'audit.PowerPlatformCreditDay', N'U') IS NULL THEN 0 ELSE 1 END;") == 1;

    public async Task<bool> CommitmentsReadyAsync() =>
        await db.ScalarAsync<int>("SELECT CASE WHEN OBJECT_ID(N'dash.CreditCommitment', N'U') IS NULL THEN 0 ELSE 1 END;") == 1;

    /// <summary>The charge rate: CreditChargeRate, else the price list's pay-as-you-go credit; and the currency.</summary>
    public async Task<(decimal? Rate, string Currency)> RateAsync()
    {
        var cur = await settings.StringAsync("LicenseCurrency") ?? "USD";
        if (decimal.TryParse(await settings.StringAsync("CreditChargeRate"), NumberStyles.Number, CultureInfo.InvariantCulture, out var r) && r > 0) return (r, cur);
        var list = await db.ScalarAsync<int>("SELECT CASE WHEN OBJECT_ID(N'dash.CostPrice', N'U') IS NULL THEN 0 ELSE 1 END;") == 1
            ? await db.ScalarAsync<decimal?>("SELECT MonthlyPrice FROM dash.CostPrice WHERE Item = 'COPILOT_STUDIO_PAYG_CREDIT';") : null;
        return (list is > 0 ? list : null, cur);
    }

    /// <summary>The last day the consumption report reaches, for Copilot Studio's credits.</summary>
    public Task<DateTime?> LastDayAsync() =>
        db.ScalarAsync<DateTime?>("SELECT MAX([Day]) FROM audit.PowerPlatformCreditDay WHERE Product = 'Copilot Studio';");

    /// <summary>The months the report covers, newest first.</summary>
    public async Task<List<DateTime>> MonthsAsync() =>
        (await db.QueryAsync<DateTime>("SELECT DISTINCT DATEFROMPARTS(YEAR([Day]), MONTH([Day]), 1) FROM audit.PowerPlatformCreditDay WHERE Product = 'Copilot Studio' ORDER BY 1 DESC;")).ToList();

    /// <summary>The org IDs a viewer of part of the organization sees: those led from inside it. Null: all.</summary>
    async Task<HashSet<string>?> VisibleOrgsAsync(ReportContext c) =>
        c.Root is null ? null : (await db.QueryAsync<string>($@"
SELECT DISTINCT r.OrgId FROM dash.Reservation AS r
WHERE r.LeaderUpn IN (SELECT du.UserPrincipalName FROM bi.DimUser AS du {c.Scope("du.UserKey")});", c.Args())).ToHashSet(StringComparer.OrdinalIgnoreCase);

    async Task<Dictionary<string, string>> OrgNamesAsync() =>
        await db.ScalarAsync<int>("SELECT CASE WHEN OBJECT_ID(N'dash.Reservation', N'U') IS NULL THEN 0 ELSE 1 END;") == 0 ? new(StringComparer.OrdinalIgnoreCase) :
        (await db.QueryAsync<(string OrgId, string Name)>("SELECT OrgId, Name FROM (SELECT OrgId, Name, ROW_NUMBER() OVER (PARTITION BY OrgId ORDER BY EffectiveFrom DESC) AS n FROM dash.Reservation) AS x WHERE n = 1;"))
            .ToDictionary(r => r.OrgId, r => r.Name, StringComparer.OrdinalIgnoreCase);

    class LineRow
    {
        public string EnvironmentName { get; set; } = "";
        public string Path { get; set; } = "";
        public string? OrgId { get; set; }
        public string? OrgName { get; set; }
        public string? CostCenter { get; set; }
        public string? BillingPolicy { get; set; }
        public string? FinancialOwner { get; set; }
        public decimal Credits { get; set; }
        public decimal PaygCredits { get; set; }
        public decimal? PaygCost { get; set; }
        public string? Currency { get; set; }
    }

    sealed class SnapRow : LineRow
    {
        public DateTime TakenUtc { get; set; }
        public decimal? ChargeRate { get; set; }
    }

    static IReadOnlyList<CreditLine> Lines(IEnumerable<LineRow> rows, Dictionary<string, string> names, HashSet<string>? visible) =>
        rows.Where(r => visible is null || (r.OrgId is not null && visible.Contains(r.OrgId)))
            .Select(r => new CreditLine(r.EnvironmentName, r.Path, r.OrgId, r.OrgName ?? (r.OrgId is null ? null : names.GetValueOrDefault(r.OrgId)), r.CostCenter,
                r.BillingPolicy, r.FinancialOwner, r.Credits, r.PaygCredits, r.PaygCost, r.Currency))
            .OrderBy(l => l.OrgName ?? "~").ThenBy(l => l.EnvironmentName, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>A month to date from the credits loaded, each day under the route in effect that day.</summary>
    public async Task<CreditChargeView?> CurrentAsync(ReportContext c, DateTime month)
    {
        var last = await LastDayAsync();
        if (last is null) return null;
        month = new DateTime(month.Year, month.Month, 1);
        var rows = await db.QueryAsync<LineRow>(@"
SELECT d.EnvironmentName, ISNULL(r.Path, 'Unmapped') AS Path, r.OrgId, r.CostCenter, r.BillingPolicy, r.FinancialOwner,
       SUM(ISNULL(d.Credits, 0)) AS Credits,
       SUM(CASE WHEN d.Billing = 'PayAsYouGo' THEN ISNULL(d.Credits, 0) ELSE 0 END) AS PaygCredits,
       SUM(CASE WHEN d.Billing = 'PayAsYouGo' THEN d.Cost END) AS PaygCost, MAX(d.Currency) AS Currency
FROM audit.PowerPlatformCreditDay AS d
OUTER APPLY (SELECT TOP (1) x.Path, x.OrgId, x.CostCenter, x.BillingPolicy, x.FinancialOwner FROM dash.CreditRoute AS x
             WHERE x.EnvironmentName = d.EnvironmentName AND x.EffectiveFrom <= d.[Day] AND (x.EffectiveTo IS NULL OR x.EffectiveTo >= d.[Day])
             ORDER BY x.EffectiveFrom DESC, x.RouteId DESC) AS r
WHERE d.Product = 'Copilot Studio' AND d.[Day] >= @m AND d.[Day] < DATEADD(MONTH, 1, @m)
GROUP BY d.EnvironmentName, ISNULL(r.Path, 'Unmapped'), r.OrgId, r.CostCenter, r.BillingPolicy, r.FinancialOwner;", new { m = month });
        var (rate, cur) = await RateAsync();
        var to = last.Value < month.AddMonths(1).AddDays(-1) ? last.Value : month.AddMonths(1).AddDays(-1);
        return new(month, to, null, Lines(rows, await OrgNamesAsync(), await VisibleOrgsAsync(c)), rate, cur);
    }

    /// <summary>A month-end snapshot as it was kept, or null.</summary>
    public async Task<CreditChargeView?> SnapshotAsync(ReportContext c, DateTime month)
    {
        month = new DateTime(month.Year, month.Month, 1);
        var rows = (await db.QueryAsync<SnapRow>(@"
SELECT EnvironmentName, Path, OrgId, OrgName, CostCenter, CAST(NULL AS NVARCHAR(200)) AS BillingPolicy, CAST(NULL AS NVARCHAR(256)) AS FinancialOwner,
       Credits, PaygCredits, PaygCost, Currency, TakenUtc, ChargeRate
FROM dash.CreditChargeSnapshot WHERE SnapshotMonth = @m;", new { m = month })).ToList();
        if (rows.Count == 0) return null;
        var cur = await settings.StringAsync("LicenseCurrency") ?? "USD";
        return new(month, month.AddMonths(1).AddDays(-1), rows.Max(r => r.TakenUtc), Lines(rows, await OrgNamesAsync(), await VisibleOrgsAsync(c)), rows[0].ChargeRate, cur);
    }

    /// <summary>The snapshots kept, newest first: the month, when it was kept, the credits and the charge, as the viewer sees them.</summary>
    public async Task<List<(DateTime Month, DateTime Taken, decimal Credits, decimal ToLoBs, decimal Unrouted, decimal? Rate)>> SnapshotsAsync(ReportContext c)
    {
        var visible = await VisibleOrgsAsync(c);
        var rows = await db.QueryAsync<(DateTime Month, DateTime Taken, string Path, string? OrgId, decimal Credits, decimal? Rate)>(
            "SELECT SnapshotMonth, TakenUtc, Path, OrgId, Credits, ChargeRate FROM dash.CreditChargeSnapshot;");
        return rows.Where(r => visible is null || (r.OrgId is not null && visible.Contains(r.OrgId)))
            .GroupBy(r => r.Month).OrderByDescending(g => g.Key)
            .Select(g => (g.Key, g.Max(r => r.Taken), g.Sum(r => r.Credits), g.Where(r => CreditPaths.NeedsOrg(r.Path) || (r.Path == CreditPaths.Innovation && r.OrgId is not null)).Sum(r => r.Credits),
                g.Where(r => r.Path == CreditPaths.Unmapped).Sum(r => r.Credits), g.First().Rate))
            .ToList();
    }

    public sealed record RouteRow(int RouteId, string EnvironmentName, string Path, string? OrgId, string? CostCenter, string? FinancialOwner, string? BillingPolicy,
        DateTime EffectiveFrom, DateTime? EffectiveTo, string? Note);

    public async Task<List<RouteRow>> RoutesAsync() =>
        (await db.QueryAsync<RouteRow>("SELECT RouteId, EnvironmentName, Path, OrgId, CostCenter, FinancialOwner, BillingPolicy, EffectiveFrom, EffectiveTo, Note FROM dash.CreditRoute ORDER BY EnvironmentName, EffectiveFrom;")).ToList();

    /// <summary>
    /// Agents in an environment charged to one line of business, built by someone in another: each agent of the agent
    /// inventory whose environment's route today names a line of business other than the one its owner works in (the
    /// reservation register's leaders and the org chart). A credit routed to the wrong path is caught here before Finance
    /// finds it. Empty when the inventory or the register is not there.
    /// </summary>
    /// <summary>Null when the check cannot run: no agent inventory loaded, or no reservation register to place the owners.</summary>
    public async Task<List<(string Agent, string Environment, string? Owner, string EnvOrg, string OwnerOrg)>?> MisroutedAsync(ReportContext c)
    {
        if (await db.ScalarAsync<int>("SELECT CASE WHEN OBJECT_ID(N'dash.AgentInventory', N'U') IS NULL OR OBJECT_ID(N'dash.fnReservationMember', N'IF') IS NULL THEN 0 WHEN NOT EXISTS (SELECT 1 FROM dash.AgentInventory WHERE Environment IS NOT NULL) OR NOT EXISTS (SELECT 1 FROM dash.Reservation WHERE LeaderUpn IS NOT NULL) THEN 0 ELSE 1 END;") == 0) return null;
        var visible = await VisibleOrgsAsync(c);
        var rows = await db.QueryAsync<(string Agent, string Environment, string? Owner, string EnvOrg, string OwnerOrg)>(@"
DECLARE @today DATE = CAST(GETDATE() AS DATE);
SELECT ISNULL(ai.AgentName, ai.InventoryAgentId), ai.Environment, ai.OwnerUpn, rt.OrgId, o.OrgId
FROM dash.AgentInventory AS ai
CROSS APPLY (SELECT TOP (1) x.OrgId, x.Path FROM dash.CreditRoute AS x
             WHERE x.EnvironmentName = ai.Environment AND x.EffectiveFrom <= @today AND (x.EffectiveTo IS NULL OR x.EffectiveTo >= @today)
             ORDER BY x.EffectiveFrom DESC, x.RouteId DESC) AS rt
CROSS APPLY (SELECT TOP (1) r.OrgId FROM dash.fnReservationMember(@today) AS m JOIN dash.Reservation AS r ON r.ReservationId = m.ReservationId
             WHERE m.Upn = ai.OwnerUpn) AS o
WHERE rt.Path IN ('Productivity', 'BusinessApplication') AND rt.OrgId <> o.OrgId
ORDER BY ai.Environment, 1;");
        return rows.Where(r => visible is null || visible.Contains(r.EnvOrg) || visible.Contains(r.OwnerOrg)).ToList();
    }

    /// <summary>Routes naming an org ID the reservation register does not have.</summary>
    public async Task<List<string>> UnknownOrgsAsync() =>
        await db.ScalarAsync<int>("SELECT CASE WHEN OBJECT_ID(N'dash.Reservation', N'U') IS NULL THEN 0 ELSE 1 END;") == 0 ? [] :
        (await db.QueryAsync<string>("SELECT DISTINCT c.OrgId FROM dash.CreditRoute AS c WHERE c.OrgId IS NOT NULL AND NOT EXISTS (SELECT 1 FROM dash.Reservation AS r WHERE r.OrgId = c.OrgId);")).ToList();

    public async Task<List<CreditCommitment>> CommitmentsAsync()
    {
        var cur = await settings.StringAsync("LicenseCurrency") ?? "USD";
        return (await db.QueryAsync<(int Id, string Name, decimal Amount, string? Currency, decimal Draw, DateTime Start, DateTime End, string? Scope, string? Note)>(
                "SELECT CommitmentId, Name, Amount, Currency, DrawRate, StartDate, EndDate, Scope, Note FROM dash.CreditCommitment ORDER BY StartDate DESC;"))
            .Select(r => new CreditCommitment(r.Id, r.Name, r.Amount, r.Currency ?? cur, r.Draw, r.Start, r.End, r.Scope, r.Note)).ToList();
    }

    /// <summary>
    /// A commitment's draw each day of its term: the pay-as-you-go credits the consumption report gives (the metered use a
    /// pre-purchase covers; prepaid packs are paid for apart) × what one credit draws. An estimate: Microsoft's own applied
    /// figure is in Azure Cost Management.
    /// </summary>
    public async Task<CommitmentStatus> StatusAsync(CreditCommitment c, DateTime? asOf = null)
    {
        var last = await LastDayAsync();
        var day = asOf ?? last ?? DateTime.Today;
        var daily = (await db.QueryAsync<(DateTime Day, decimal Credits)>(@"
SELECT [Day], SUM(ISNULL(Credits, 0)) FROM audit.PowerPlatformCreditDay
WHERE Product = 'Copilot Studio' AND Billing = 'PayAsYouGo' AND [Day] BETWEEN @s AND @e GROUP BY [Day];", new { s = c.StartDate, e = c.EndDate }))
            .Select(d => (d.Day, Math.Round(d.Credits * c.DrawRate, 2))).ToList();
        return CreditChargeRules.Project(c, day, daily);
    }

    /// <summary>The commitment in force on the last day of data, or the latest one.</summary>
    public async Task<CreditCommitment?> InForceAsync()
    {
        var all = await CommitmentsAsync();
        var day = await LastDayAsync() ?? DateTime.Today;
        return all.FirstOrDefault(x => x.StartDate <= day && x.EndDate >= day) ?? all.FirstOrDefault();
    }
}
