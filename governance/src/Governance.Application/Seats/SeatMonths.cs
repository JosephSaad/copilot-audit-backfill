using System.Globalization;
using Governance.Application.Data;

namespace Governance.Application.Seats;

/// <summary>One line of a month: a line of business (FO:id), the seats no tracking group claims (NOLOB).</summary>
public sealed class SeatMonthLine
{
    public string RowKey { get; init; } = "";
    public int? FundingOwnerId { get; init; }
    public Guid? PublicId { get; init; }
    public string Name { get; init; } = "";
    public string? TrackingGroup { get; init; }
    public bool IsCentralCost { get; init; }
    public bool IsNoLob => RowKey == SeatMonths.NoLobKey;
    /// <summary>Charged to the line of business: not central cost and not No LOB.</summary>
    public bool Charged => !IsNoLob && !IsCentralCost;
    public int Reserved { get; init; }
    public int PeakSeats { get; init; }
    public int LowSeats { get; init; }
    public decimal AvgSeats { get; init; }
    public int MonthEndSeats { get; init; }
    public int DaysMeasured { get; init; }
    public int ChargeSeats { get; init; }
    public decimal Charge { get; init; }
    public decimal Overage { get; init; }
    /// <summary>Overage of every month from the tracking start up to this one.</summary>
    public decimal CumulativeOverage { get; set; }
    /// <summary>Corrections to certified months charged in this month.</summary>
    public decimal Adjustments { get; set; }
    /// <summary>What the seats cost at the rate even where nobody is charged: the peak for central cost and No LOB.</summary>
    public decimal Cost(decimal rate) => Charged ? Charge : PeakSeats * rate;
}

/// <summary>A month of seats: certified (frozen) or a draft built from the days measured so far.</summary>
public sealed class SeatMonthView
{
    public DateTime Month { get; init; }
    /// <summary>CERTIFIED, DRAFT, or NOT_TRACKED (before Seats.TrackingStart).</summary>
    public string Status { get; init; } = "DRAFT";
    public bool Certified => Status == "CERTIFIED";
    public int DaysInMonth => DateTime.DaysInMonth(Month.Year, Month.Month);
    public int DaysMeasured { get; init; }
    /// <summary>The days that could have been measured: from the tracking start (or the 1st) to today (or the month's end).</summary>
    public int DaysExpected { get; init; }
    public IReadOnlyList<DateTime> MissingDays { get; init; } = Array.Empty<DateTime>();
    public decimal Rate { get; init; }
    public string Currency { get; init; } = "USD";
    public int? Purchased { get; init; }
    public IReadOnlyList<SeatMonthLine> Lines { get; init; } = Array.Empty<SeatMonthLine>();
    public string? CertifiedByName { get; init; }
    public DateTime? CertifiedUtc { get; init; }
    public string? Reason { get; init; }
    public string? AuditJson { get; init; }
    public string? DuplicatesJson { get; init; }
    public bool Ended { get; init; }

    public decimal ChargedToLobs => Lines.Where(l => l.Charged).Sum(l => l.Charge);
    public decimal CentralCost => Lines.Where(l => l.IsCentralCost && !l.IsNoLob).Sum(l => l.Cost(Rate));
    public decimal NoLobCost => Lines.Where(l => l.IsNoLob).Sum(l => l.Cost(Rate));
    public decimal? Invoice => Purchased is int p ? p * Rate : null;
    /// <summary>The licenses bought but neither charged nor counted above: the unassigned pool (negative: peaks above what was bought).</summary>
    public decimal? Remainder => Invoice is decimal i ? i - ChargedToLobs - CentralCost - NoLobCost : null;
}

/// <summary>
/// Seats day by day and month by month. Every seat read records the day (the highest seats any read saw that day); a
/// month is built from its days: peak, low, average and month-end seats. A line of business pays the larger of its
/// peak and its reservation at the rate; the overage is what the peak adds above the reservation. A certified month is
/// frozen in core.SeatMonth; a later reservation change inside it is charged as an adjustment in the next open month.
/// </summary>
public static class SeatMonths
{
    public const string NoLobKey = "NOLOB";
    public const string TenantKey = "TENANT";
    public static string LobKey(int fundingOwnerId) => "FO:" + fundingOwnerId.ToString(CultureInfo.InvariantCulture);

    public static DateTime MonthOf(DateTime d) => new(d.Year, d.Month, 1);

    /// <summary>Today in the Workbench time zone (Calendar.TimeZone).</summary>
    public static async Task<DateTime> LocalTodayAsync(Uow uow, DateTime utcNow)
        => ToLocal(utcNow, await uow.ScalarAsync<string?>("SELECT SettingValue FROM ref.Setting WHERE SettingKey = 'Calendar.TimeZone'")).Date;

    public static DateTime ToLocal(DateTime utcNow, string? zone)
    {
        try { return TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utcNow, DateTimeKind.Utc), TimeZoneInfo.FindSystemTimeZoneById(string.IsNullOrWhiteSpace(zone) ? "Eastern Standard Time" : zone)); }
        catch (TimeZoneNotFoundException) { return utcNow; }
    }

    public static async Task<DateTime> TrackingStartAsync(Uow uow)
        => DateTime.TryParseExact(await uow.ScalarAsync<string?>("SELECT SettingValue FROM ref.Setting WHERE SettingKey = 'Seats.TrackingStart'"), "yyyy-MM-dd",
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : new DateTime(2026, 12, 1);

    /// <summary>
    /// The day's figures, after a seat read: each line of business, No LOB and the tenant. A second read the same day
    /// keeps the highest seats and members it saw (the day's peak) and the latest active users, reservation and purchase.
    /// </summary>
    public static async Task<int> RecordDayAsync(Uow uow, long runId, DateTime utcNow, int inactiveDays)
    {
        var day = await LocalTodayAsync(uow, utcNow);
        if (day < await TrackingStartAsync(uow)) return 0;
        return await uow.ExecuteAsync(
            """
            WITH lob AS (
                SELECT CASE WHEN p.FundingOwnerId IS NULL THEN 'NOLOB' ELSE CONCAT('FO:', p.FundingOwnerId) END AS RowKey, p.FundingOwnerId,
                       p.TrackingMembers AS Members, p.AssignedSeats AS Seats, p.ReservedSeats AS Reserved,
                       (SELECT COUNT(*) FROM intg.GroupMember gm
                        JOIN core.SeatGroup g ON gm.GroupId IN (g.EntraGroupId, g.AdObjectGuid) AND g.SeatGroupId = p.SeatGroupId
                        JOIN rpt.fn_SeatHolders() h ON h.MemberObjectId = gm.MemberObjectId
                        OUTER APPLY (SELECT TOP (1) sh.UserPrincipalName FROM intg.SeatHolder sh WHERE sh.MemberObjectId = gm.MemberObjectId AND sh.UserPrincipalName IS NOT NULL) x
                        JOIN intg.CopilotActivity a ON a.UserPrincipalName = x.UserPrincipalName
                        WHERE a.LastActivityDate >= DATEADD(DAY, -@inactiveDays, @day)) AS Active,
                       CAST(NULL AS int) AS Purchased
                FROM rpt.fn_SeatPosition(@day) p
                UNION ALL
                SELECT 'TENANT', NULL, 0, (SELECT COUNT(*) FROM rpt.fn_SeatHolders()), NULL, NULL,
                       (SELECT SUM(r.EnabledUnits) FROM intg.SeatSkuReading r JOIN core.SeatSku k ON k.SkuId = r.SkuId AND k.Kind = 'COPILOT_ADDON' AND k.IsActive = 1
                        WHERE r.SyncRunId = (SELECT MAX(SyncRunId) FROM intg.SeatSkuReading))
            )
            MERGE intg.SeatDay AS t
            USING (SELECT RowKey, MAX(FundingOwnerId) AS FundingOwnerId, SUM(Members) AS Members, SUM(Seats) AS Seats, SUM(Active) AS Active,
                          MAX(Reserved) AS Reserved, MAX(Purchased) AS Purchased FROM lob GROUP BY RowKey) AS s
               ON t.Day = @day AND t.RowKey = s.RowKey
            WHEN MATCHED THEN UPDATE SET Members = CASE WHEN s.Members > t.Members THEN s.Members ELSE t.Members END,
                 Seats = CASE WHEN s.Seats > t.Seats THEN s.Seats ELSE t.Seats END, Active = s.Active, Reserved = s.Reserved,
                 Purchased = COALESCE(s.Purchased, t.Purchased), Reads = t.Reads + 1, LastRunId = @runId, UpdatedUtc = SYSUTCDATETIME()
            WHEN NOT MATCHED THEN INSERT (Day, RowKey, FundingOwnerId, Members, Seats, Active, Reserved, Purchased, Reads, LastRunId)
                 VALUES (@day, s.RowKey, s.FundingOwnerId, s.Members, s.Seats, s.Active, s.Reserved, s.Purchased, 1, @runId);
            """, new { day, runId, inactiveDays });
    }

    /// <summary>The months with days or a certification, newest first, from the tracking start to this month.</summary>
    public static async Task<IReadOnlyList<DateTime>> MonthsAsync(Uow uow, DateTime utcNow)
    {
        var start = MonthOf(await TrackingStartAsync(uow));
        var now = MonthOf(await LocalTodayAsync(uow, utcNow));
        var list = new List<DateTime>();
        for (var m = now; m >= start; m = m.AddMonths(-1)) list.Add(m);
        return list;
    }

    sealed record DayRow(string RowKey, int? FundingOwnerId, int Peak, int Low, decimal Avg, int Days, int MonthEnd);
    sealed record LobRow(int FundingOwnerId, Guid PublicId, string Name, string? TrackingGroup, bool IsCentralCost);

    /// <summary>A month as it stands: frozen when certified, else built from its days up to today.</summary>
    public static async Task<SeatMonthView> MonthAsync(Uow uow, DateTime month, DateTime utcNow, SeatSettings settings, bool withCumulative = true)
    {
        month = MonthOf(month);
        var today = await LocalTodayAsync(uow, utcNow);
        var start = await TrackingStartAsync(uow);
        var view = MonthOf(start) > month
            ? new SeatMonthView { Month = month, Status = "NOT_TRACKED", Rate = settings.RatePerSeatMonth, Currency = settings.Currency }
            : await CertifiedAsync(uow, month) ?? await DraftAsync(uow, month, today, start, settings);
        if (view.Status == "NOT_TRACKED") return view;
        var adjustments = (await uow.QueryAsync<(int FundingOwnerId, decimal Amount)>(
            "SELECT FundingOwnerId, SUM(Amount) FROM core.SeatAdjustment WHERE PeriodMonth = @month GROUP BY FundingOwnerId", new { month })).ToDictionary(a => a.FundingOwnerId, a => a.Amount);
        foreach (var l in view.Lines) if (l.FundingOwnerId is int fo && adjustments.TryGetValue(fo, out var a)) l.Adjustments = a;
        if (withCumulative)
        {
            // Overage of every month from the start of the period the month is in (never before the tracking start) to this one.
            var from = CumulativeFrom(month, MonthOf(start), await PeriodsAsync(uow));
            var total = view.Lines.Where(l => l.FundingOwnerId is not null).ToDictionary(l => l.FundingOwnerId!.Value, l => l.Overage);
            for (var m = from; m < month; m = m.AddMonths(1))
            {
                var earlier = await MonthAsync(uow, m, utcNow, settings, withCumulative: false);
                foreach (var l in earlier.Lines.Where(l => l.FundingOwnerId is not null))
                    total[l.FundingOwnerId!.Value] = total.GetValueOrDefault(l.FundingOwnerId!.Value) + l.Overage;
            }
            foreach (var l in view.Lines) if (l.FundingOwnerId is int fo) l.CumulativeOverage = total.GetValueOrDefault(fo);
        }
        return view;
    }

    static async Task<SeatMonthView?> CertifiedAsync(Uow uow, DateTime month)
    {
        var c = await uow.QuerySingleOrDefaultAsync<(DateTime PeriodMonth, string? By, DateTime CertifiedUtc, int DaysMeasured, int DaysInMonth, string? Reason, decimal Rate, string Currency, int? Purchased, string? AuditJson, string? DuplicatesJson)>(
            """
            SELECT c.PeriodMonth, p.DisplayName, c.CertifiedUtc, c.DaysMeasured, c.DaysInMonth, c.Reason, c.Rate, c.Currency, c.Purchased, c.AuditJson, c.DuplicatesJson
            FROM core.SeatMonthCertification c LEFT JOIN sec.Person p ON p.PersonId = c.CertifiedByPersonId WHERE c.PeriodMonth = @month
            """, new { month });
        if (c.PeriodMonth == default) return null;
        var lines = (await uow.QueryAsync<SeatMonthLine>(
            """
            SELECT m.RowKey, m.FundingOwnerId, fo.PublicId, m.Name, m.TrackingGroup, m.IsCentralCost, m.Reserved, m.PeakSeats, m.LowSeats, m.AvgSeats,
                   m.MonthEndSeats, m.DaysMeasured, m.ChargeSeats, m.Charge, m.Overage
            FROM core.SeatMonth m LEFT JOIN core.FundingOwner fo ON fo.FundingOwnerId = m.FundingOwnerId
            WHERE m.PeriodMonth = @month ORDER BY CASE WHEN m.RowKey = 'NOLOB' THEN 1 ELSE 0 END, m.Name
            """, new { month })).ToList();
        return new SeatMonthView
        {
            Month = month, Status = "CERTIFIED", DaysMeasured = c.DaysMeasured, DaysExpected = c.DaysInMonth, Rate = c.Rate, Currency = c.Currency, Purchased = c.Purchased,
            Lines = lines, CertifiedByName = c.By, CertifiedUtc = c.CertifiedUtc, Reason = c.Reason, AuditJson = c.AuditJson, DuplicatesJson = c.DuplicatesJson, Ended = true,
        };
    }

    static async Task<SeatMonthView> DraftAsync(Uow uow, DateTime month, DateTime today, DateTime start, SeatSettings s)
    {
        var from = month < start ? start : month;
        var end = month.AddMonths(1).AddDays(-1);
        var to = end < today ? end : today;
        var rate = s.RatePerSeatMonth;
        var days = (await uow.QueryAsync<DayRow>(
            """
            SELECT d.RowKey, MAX(d.FundingOwnerId) AS FundingOwnerId, MAX(d.Seats) AS Peak, MIN(d.Seats) AS Low, AVG(CAST(d.Seats AS decimal(12,2))) AS Avg,
                   COUNT(*) AS Days, (SELECT TOP (1) e.Seats FROM intg.SeatDay e WHERE e.RowKey = d.RowKey AND e.Day BETWEEN @from AND @to ORDER BY e.Day DESC) AS MonthEnd
            FROM intg.SeatDay d WHERE d.Day BETWEEN @from AND @to GROUP BY d.RowKey
            """, new { from, to })).ToDictionary(d => d.RowKey);
        var measured = (await uow.QueryAsync<DateTime>("SELECT Day FROM intg.SeatDay WHERE RowKey = 'TENANT' AND Day BETWEEN @from AND @to", new { from, to })).Select(d => d.Date).ToHashSet();
        var purchased = await uow.ScalarAsync<int?>("SELECT MAX(Purchased) FROM intg.SeatDay WHERE RowKey = 'TENANT' AND Day BETWEEN @from AND @to", new { from, to });
        var lobs = (await uow.QueryAsync<LobRow>(
            """
            SELECT fo.FundingOwnerId, fo.PublicId, fo.Name,
                   (SELECT TOP (1) g.DisplayName FROM core.SeatGroup g WHERE g.FundingOwnerId = fo.FundingOwnerId AND g.Purpose = 'LOB_TRACKING'
                    AND g.EffectiveFrom <= @to AND (g.EffectiveTo IS NULL OR g.EffectiveTo >= @from) ORDER BY g.EffectiveFrom DESC) AS TrackingGroup,
                   CAST(ISNULL(fo.IsCentralCost, 0) AS bit) AS IsCentralCost
            FROM core.FundingOwner fo WHERE fo.Status IN ('ACTIVE','SUSPENDED')
            """, new { from, to })).ToList();
        // The reservation in effect on the month's last day (today for the month in progress).
        var reserved = (await uow.QueryAsync<(int FundingOwnerId, int Seats)>(
            """
            SELECT r.FundingOwnerId, r.Seats FROM core.SeatReservation r
            WHERE r.EffectiveFrom <= @to AND (r.EffectiveTo IS NULL OR r.EffectiveTo >= @to)
            """, new { to })).GroupBy(r => r.FundingOwnerId).ToDictionary(g => g.Key, g => g.First().Seats);
        var lines = new List<SeatMonthLine>();
        foreach (var lob in lobs)
        {
            var d = days.GetValueOrDefault(LobKey(lob.FundingOwnerId));
            if (d is null && lob.TrackingGroup is null && !reserved.ContainsKey(lob.FundingOwnerId)) continue;
            lines.Add(Line(LobKey(lob.FundingOwnerId), lob.FundingOwnerId, lob.PublicId, lob.Name, lob.TrackingGroup, lob.IsCentralCost, reserved.GetValueOrDefault(lob.FundingOwnerId), d, rate));
        }
        if (days.GetValueOrDefault(NoLobKey) is { } nl) lines.Add(Line(NoLobKey, null, null, "No LOB", null, false, 0, nl, rate));
        var missing = new List<DateTime>();
        for (var d = from; d <= to; d = d.AddDays(1)) if (!measured.Contains(d)) missing.Add(d);
        return new SeatMonthView
        {
            Month = month, Status = "DRAFT", DaysMeasured = measured.Count, DaysExpected = to < from ? 0 : (int)(to - from).TotalDays + 1, MissingDays = missing,
            Rate = rate, Currency = s.Currency, Purchased = purchased, Ended = end < today,
            Lines = lines.OrderBy(l => l.IsNoLob ? 1 : 0).ThenBy(l => l.Name, StringComparer.CurrentCultureIgnoreCase).ToList(),
        };
    }

    /// <summary>One line's month: charged lines pay the larger of the peak and the reservation; the overage is the peak above it. Pure, for the tests.</summary>
    static SeatMonthLine Line(string key, int? fo, Guid? publicId, string name, string? group, bool central, int reserved, DayRow? d, decimal rate)
    {
        var peak = d?.Peak ?? 0;
        var charged = key != NoLobKey && !central;
        var chargeSeats = charged ? Math.Max(peak, reserved) : 0;
        return new SeatMonthLine
        {
            RowKey = key, FundingOwnerId = fo, PublicId = publicId, Name = name, TrackingGroup = group, IsCentralCost = central, Reserved = reserved,
            PeakSeats = peak, LowSeats = d?.Low ?? 0, AvgSeats = Math.Round(d?.Avg ?? 0, 1), MonthEndSeats = d?.MonthEnd ?? 0, DaysMeasured = d?.Days ?? 0,
            ChargeSeats = chargeSeats, Charge = chargeSeats * rate, Overage = charged ? Math.Max(0, peak - reserved) * rate : 0,
        };
    }

    /// <summary>
    /// The first month the cumulative overage of <paramref name="month"/> counts from: the first month of the period it
    /// is in, never before the tracking start; a month in no period counts on its own. Pure, for the tests.
    /// </summary>
    public static DateTime CumulativeFrom(DateTime month, DateTime trackingStartMonth, IReadOnlyList<SeatPeriod> periods)
    {
        month = MonthOf(month);
        if (PeriodFor(periods, month) is not { } p) return month;
        return p.StartMonth > trackingStartMonth ? p.StartMonth : trackingStartMonth;
    }

    public static SeatPeriod? PeriodFor(IReadOnlyList<SeatPeriod> periods, DateTime month)
        => periods.FirstOrDefault(p => p.StartMonth <= MonthOf(month) && MonthOf(month) <= p.EndMonth);

    /// <summary>Whether a period from <paramref name="start"/> to <paramref name="end"/> would overlap another one. Pure, for the tests.</summary>
    public static SeatPeriod? Overlapping(IReadOnlyList<SeatPeriod> periods, int? id, DateTime start, DateTime end)
        => periods.FirstOrDefault(p => p.SeatPeriodId != id && p.StartMonth <= end && start <= p.EndMonth);

    /// <summary>Every period, oldest first, with how many of its months are certified.</summary>
    public static async Task<IReadOnlyList<SeatPeriod>> PeriodsAsync(Uow uow)
        => (await uow.QueryAsync<SeatPeriod>(
            """
            SELECT p.SeatPeriodId, p.Name, p.StartMonth, p.EndMonth,
                   (SELECT COUNT(*) FROM core.SeatMonthCertification c WHERE c.PeriodMonth BETWEEN p.StartMonth AND p.EndMonth) AS CertifiedMonths
            FROM core.SeatPeriod p ORDER BY p.StartMonth
            """)).ToList();

    /// <summary>
    /// A whole period, line by line: the charge, overage and adjustments of its months added up, the highest monthly
    /// peak, and how many months are certified. Months after this one are not counted yet.
    /// </summary>
    public static async Task<SeatPeriodView> PeriodAsync(Uow uow, SeatPeriod period, DateTime utcNow, SeatSettings settings)
    {
        var start = MonthOf(await TrackingStartAsync(uow));
        var now = MonthOf(await LocalTodayAsync(uow, utcNow));
        var months = new List<SeatMonthView>();
        for (var m = period.StartMonth < start ? start : period.StartMonth; m <= period.EndMonth && m <= now; m = m.AddMonths(1))
            months.Add(await MonthAsync(uow, m, utcNow, settings, withCumulative: false));
        var lines = months.SelectMany(v => v.Lines.Select(l => (v, l)))
            .GroupBy(x => x.l.RowKey)
            .Select(g => new SeatPeriodLine
            {
                RowKey = g.Key, FundingOwnerId = g.First().l.FundingOwnerId, PublicId = g.First().l.PublicId, Name = g.Last().l.Name,
                TrackingGroup = g.Last().l.TrackingGroup, IsCentralCost = g.Last().l.IsCentralCost,
                Months = g.Count(), PeakSeats = g.Max(x => x.l.PeakSeats), Charge = g.Sum(x => x.l.Charge), Overage = g.Sum(x => x.l.Overage),
                Adjustments = g.Sum(x => x.l.Adjustments), Cost = g.Sum(x => x.l.Cost(x.v.Rate)),
            })
            .OrderBy(l => l.IsNoLob ? 1 : 0).ThenBy(l => l.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        return new SeatPeriodView { Period = period, Months = months, Lines = lines, Currency = months.LastOrDefault()?.Currency ?? settings.Currency };
    }
    /// <summary>The charge and overage rule on their own; pure, for the tests.</summary>
    public static (int ChargeSeats, decimal Charge, decimal Overage) Price(int peak, int reserved, decimal rate, bool charged)
        => charged ? (Math.Max(peak, reserved), Math.Max(peak, reserved) * rate, Math.Max(0, peak - reserved) * rate) : (0, 0m, 0m);

    /// <summary>
    /// After a reservation change: each certified month of that line of business whose charge would now differ gets an
    /// adjustment in the first open month (this month, or the next one not certified). Repeated changes adjust only the
    /// difference left.
    /// </summary>
    public static async Task<int> AdjustCertifiedAsync(Uow uow, int fundingOwnerId, DateTime utcNow, int? personId, string why)
    {
        var frozen = (await uow.QueryAsync<(DateTime PeriodMonth, int PeakSeats, decimal Charge, decimal Rate)>(
            """
            SELECT m.PeriodMonth, m.PeakSeats, m.Charge, c.Rate FROM core.SeatMonth m
            JOIN core.SeatMonthCertification c ON c.PeriodMonth = m.PeriodMonth
            WHERE m.FundingOwnerId = @fundingOwnerId AND m.IsCentralCost = 0
            """, new { fundingOwnerId })).ToList();
        if (frozen.Count == 0) return 0;
        var open = MonthOf(await LocalTodayAsync(uow, utcNow));
        while (await uow.ScalarAsync<int>("SELECT COUNT(*) FROM core.SeatMonthCertification WHERE PeriodMonth = @open", new { open }) > 0) open = open.AddMonths(1);
        var n = 0;
        foreach (var f in frozen)
        {
            var monthEnd = f.PeriodMonth.AddMonths(1).AddDays(-1);
            var reserved = await uow.ScalarAsync<int?>(
                "SELECT TOP (1) Seats FROM core.SeatReservation WHERE FundingOwnerId = @fundingOwnerId AND EffectiveFrom <= @monthEnd AND (EffectiveTo IS NULL OR EffectiveTo >= @monthEnd) ORDER BY EffectiveFrom DESC",
                new { fundingOwnerId, monthEnd }) ?? 0;
            var now = Price(f.PeakSeats, reserved, f.Rate, true).Charge;
            var already = f.Charge + (await uow.ScalarAsync<decimal?>(
                "SELECT SUM(Amount) FROM core.SeatAdjustment WHERE FundingOwnerId = @fundingOwnerId AND SourceMonth = @PeriodMonth", new { fundingOwnerId, f.PeriodMonth }) ?? 0);
            if (now == already) continue;
            await uow.ExecuteAsync(
                "INSERT core.SeatAdjustment (PeriodMonth, SourceMonth, FundingOwnerId, Amount, Reason, CreatedByPersonId) VALUES (@open, @PeriodMonth, @fundingOwnerId, @amount, @why, @personId)",
                new { open, f.PeriodMonth, fundingOwnerId, amount = now - already, why = $"{why} ({f.PeriodMonth:MMMM yyyy} was certified at {f.Charge:N2})", personId });
            n++;
        }
        return n;
    }
}

/// <summary>A named run of whole months (StartMonth to EndMonth, both first days) that overlaps no other.</summary>
public sealed class SeatPeriod
{
    public int SeatPeriodId { get; init; }
    public string Name { get; init; } = "";
    public DateTime StartMonth { get; init; }
    public DateTime EndMonth { get; init; }
    public int CertifiedMonths { get; init; }
    public int MonthCount => (EndMonth.Year - StartMonth.Year) * 12 + EndMonth.Month - StartMonth.Month + 1;
    public string Label => $"{Name} ({StartMonth:MMM yyyy}{(EndMonth == StartMonth ? "" : " to " + EndMonth.ToString("MMM yyyy"))})";
}

/// <summary>One line of a whole period: its months added up.</summary>
public sealed class SeatPeriodLine
{
    public string RowKey { get; init; } = "";
    public int? FundingOwnerId { get; init; }
    public Guid? PublicId { get; init; }
    public string Name { get; init; } = "";
    public string? TrackingGroup { get; init; }
    public bool IsCentralCost { get; init; }
    public bool IsNoLob => RowKey == SeatMonths.NoLobKey;
    public bool Charged => !IsNoLob && !IsCentralCost;
    public int Months { get; init; }
    /// <summary>The highest monthly peak in the period.</summary>
    public int PeakSeats { get; init; }
    public decimal Charge { get; init; }
    public decimal Overage { get; init; }
    public decimal Adjustments { get; init; }
    /// <summary>What the seats cost at the rate even where nobody is charged.</summary>
    public decimal Cost { get; init; }
}

/// <summary>A whole period: its months so far and its lines added up.</summary>
public sealed class SeatPeriodView
{
    public SeatPeriod Period { get; init; } = new();
    public IReadOnlyList<SeatMonthView> Months { get; init; } = Array.Empty<SeatMonthView>();
    public IReadOnlyList<SeatPeriodLine> Lines { get; init; } = Array.Empty<SeatPeriodLine>();
    public string Currency { get; init; } = "USD";
}
