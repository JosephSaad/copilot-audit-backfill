using Dapper;
using Governance.Application.Data;
using Governance.Application.Seats;
using Governance.Domain;

namespace Governance.Application.Queries;

/// <summary>Seat permissions and the LOB seat admin role (rows in 210-seed-seats.sql).</summary>
public static class SeatAccess
{
    public const string View = "seats.view";
    public const string AdminAssign = "seats.admin.assign";
    public const string LobSeatAdmin = "LOB_SEAT_ADMIN";

    /// <summary>Tenant-wide seat view: central roles holding seats.view (FinOps, Platform Owner, auditors).</summary>
    public static bool SeesAll(ICurrentPrincipal p) => p.IsCentral && (p.Permissions.Contains(View) || p.Permissions.Contains(Permissions.RegistryFinance) || p.Permissions.Contains(Permissions.AdminConfigure));

    public static bool CanView(ICurrentPrincipal p, int fundingOwnerId) => SeesAll(p) || p.Can(View, fundingOwnerId);

    /// <summary>The LOBs this person may see, or null when they see every LOB.</summary>
    public static int[]? Scope(ICurrentPrincipal p) => SeesAll(p) ? null : p.FundingOwnersWith(View).ToArray();
}

public sealed class SeatSummary
{
    /// <summary>The active funding owner that pays for seats no line of business claims (Seats.NoLobChargedTo), or null.</summary>
    public string? NoLobOwnerName { get; init; }
    public int? Purchased { get; init; }
    public int? Consumed { get; init; }
    public DateTime? SkuReadUtc { get; init; }
    public int ReservedTotal { get; init; }
    public int BufferSeats { get; init; }
    public int PendingIncrease { get; init; }
    public int? Unallocated { get; init; }
    public DateTime NextAnniversary { get; init; }
    public DateTime WindowOpens { get; init; }
    public bool WindowOpen { get; init; }
    public DateTime? LastReadUtc { get; init; }
    public string? LastReadStatus { get; init; }
    public bool Stale { get; init; }
    public SeatSettings Settings { get; init; } = SeatSettings.Default;
}

public sealed class SeatLobRow
{
    public int? FundingOwnerId { get; init; }
    public Guid? PublicId { get; init; }
    public string? Code { get; init; }
    public string Name { get; init; } = "";
    public string? TrackingGroup { get; init; }
    public int ReservedSeats { get; init; }
    public int TrackingMembers { get; init; }
    public int AssignedSeats { get; init; }
    public int OverSeats { get; init; }
    public int Inactive { get; init; }
    public int OpenFindings { get; init; }
    public int PendingSeats { get; init; }
    public int UnusedReserved => Math.Max(0, ReservedSeats - AssignedSeats);
}

public sealed class SeatReservationRow
{
    public Guid PublicId { get; init; }
    public int Seats { get; init; }
    public int? PreviousSeats { get; init; }
    public string ChangeKind { get; init; } = "";
    public DateTime EffectiveFrom { get; init; }
    public DateTime? EffectiveTo { get; init; }
    public string? ApprovedByName { get; init; }
    public string? FlexPoolReview { get; init; }
    public string? Notes { get; init; }
    public string? RequestNumber { get; init; }
    public Guid? RequestPublicId { get; init; }
    public DateTime CreatedUtc { get; init; }
}

public sealed class SeatHolderRow
{
    public Guid MemberObjectId { get; init; }
    public int? PersonId { get; init; }
    public string? DisplayName { get; init; }
    public string? UserPrincipalName { get; init; }
    public bool HasSeat { get; init; }
    public string? State { get; init; }
    public string? Error { get; init; }
    public bool Direct { get; init; }
    public DateTime? LastActivityDate { get; init; }
    public bool AccountEnabled { get; init; } = true;
    public int TotalRows { get; init; }
}

public sealed class SeatReclaimRow
{
    public Guid PublicId { get; init; }
    public int PersonId { get; init; }
    public string DisplayName { get; init; } = "";
    public string? UserPrincipalName { get; init; }
    public DateTime? LastActivityDate { get; init; }
    public int? InactiveDays { get; init; }
    public string Status { get; init; } = "";
    public string? Reason { get; init; }
    public string? DecidedByName { get; init; }
    public DateTime? DecidedUtc { get; init; }
    public string? TicketNumber { get; init; }
    public DateTime CreatedUtc { get; init; }
}

public sealed class SeatAdminRow
{
    public Guid PublicId { get; init; }
    public int PersonId { get; init; }
    public string DisplayName { get; init; } = "";
    public string? UserPrincipalName { get; init; }
    public DateTime EffectiveFrom { get; init; }
    public string? GrantedByName { get; init; }
}

public sealed class SeatTrendPoint
{
    public DateTime PeriodMonth { get; init; }
    public int ReservedSeats { get; init; }
    public int AssignedSeats { get; init; }
    public string Status { get; init; } = "";
}

public sealed class SeatLobDetail
{
    public SeatLobRow Position { get; init; } = new();
    public string LobName { get; init; } = "";
    public string Code { get; init; } = "";
    public Guid PublicId { get; init; }
    public int FundingOwnerId { get; init; }
    public string? OimRoleName { get; init; }
    public IReadOnlyList<SeatReservationRow> Reservations { get; init; } = Array.Empty<SeatReservationRow>();
    public IReadOnlyList<SeatHolderRow> Holders { get; init; } = Array.Empty<SeatHolderRow>();
    public IReadOnlyList<SeatReclaimRow> Reclaims { get; init; } = Array.Empty<SeatReclaimRow>();
    public IReadOnlyList<SeatAdminRow> Admins { get; init; } = Array.Empty<SeatAdminRow>();
    public IReadOnlyList<SeatTrendPoint> Trend { get; init; } = Array.Empty<SeatTrendPoint>();
    public IReadOnlyList<(string FindingType, string Title, string Severity, DateTime RaisedUtc)> Findings { get; init; } = Array.Empty<(string, string, string, DateTime)>();
}

public sealed class SeatSnapshotRow
{
    public long SnapshotId { get; init; }
    public int? FundingOwnerId { get; init; }
    public string LobName { get; init; } = "";
    public string? Code { get; init; }
    public int ReservedSeats { get; init; }
    public int AssignedSeats { get; init; }
    public int OverSeats { get; init; }
    public int ChargeSeats { get; init; }
    public decimal RatePerSeat { get; init; }
    public string Currency { get; init; } = "";
    public decimal Amount { get; init; }
    public string Status { get; init; } = "";
    public DateTime TakenUtc { get; init; }
    public string? CertifiedByName { get; init; }
    public DateTime? CertifiedUtc { get; init; }
}

public sealed class SeatGroupRow
{
    public int SeatGroupId { get; init; }
    public Guid PublicId { get; init; }
    public Guid? EntraGroupId { get; init; }
    public string DisplayName { get; init; } = "";
    public string Purpose { get; init; } = "";
    public int? FundingOwnerId { get; init; }
    public string? LobName { get; init; }
    public string? OimRoleName { get; init; }
    public string Source { get; init; } = "";
    public bool PurposeConfirmed { get; init; }
    public string? Notes { get; init; }
    public DateTime EffectiveFrom { get; init; }
    public bool? Found { get; init; }
    public bool? OnPremisesSynced { get; init; }
    public int? UserMembers { get; init; }
    public int? NestedGroups { get; init; }
    public DateTime? ReadUtc { get; init; }
    public byte[]? RowVer { get; init; }
}

public sealed class SeatSkuRow
{
    public Guid SkuId { get; init; }
    public string SkuPartNumber { get; init; } = "";
    public string Name { get; init; } = "";
    public string Kind { get; init; } = "";
    public bool IsActive { get; init; }
    public string? Notes { get; init; }
    public int? EnabledUnits { get; init; }
    public int? ConsumedUnits { get; init; }
}

public sealed record SettingRow(string SettingKey, string SettingValue, string? Description);

/// <summary>
/// Copilot seat pages. Pages read in user mode (RLS) and also filter by the LOBs the person holds seats.view on:
/// fn_SeatPosition reads read-back tables without RLS, so the LOB filter here is what keeps counts private.
/// </summary>
public sealed class SeatQueries(Db db, ICurrentPrincipal principal, IClock clock)
{
    private DateTime Today => clock.UtcNow.Date;

    public async Task<SeatSettings> SettingsAsync()
        => await db.InTransactionAsync(SeatSettings.LoadAsync);

    public async Task<SeatSummary> SummaryAsync()
    {
        var s = await SettingsAsync();
        await using var c = await db.OpenSystemAsync();
        var sku = await c.QueryFirstOrDefaultAsync<(int? Enabled, int? Consumed, DateTime? ReadUtc)>(
            """
            SELECT SUM(r.EnabledUnits), SUM(r.ConsumedUnits), MAX(r.ReadUtc)
            FROM intg.SeatSkuReading r JOIN core.SeatSku k ON k.SkuId = r.SkuId AND k.Kind = 'COPILOT_ADDON' AND k.IsActive = 1
            WHERE r.SyncRunId = (SELECT MAX(SyncRunId) FROM intg.SeatSkuReading)
            """);
        var reserved = await c.ExecuteScalarAsync<int>(
            "SELECT ISNULL(SUM(Seats), 0) FROM core.SeatReservation WHERE EffectiveFrom <= @today AND (EffectiveTo IS NULL OR EffectiveTo >= @today)", new { today = Today });
        var pending = await PendingIncreaseAsync(c, null);
        var run = await c.QueryFirstOrDefaultAsync<(DateTime? FinishedUtc, string? Status)>(
            "SELECT TOP (1) FinishedUtc, Status FROM intg.SyncRun WHERE Integration = 'X13' ORDER BY StartedUtc DESC");
        var purchased = sku.Enabled;
        /* Who pays for seats no line of business claims: the active funding owner Finance named, or none. */
        var noLobOwner = s.NoLobChargedTo.Length == 0 ? null
            : await c.ExecuteScalarAsync<string?>("SELECT Name FROM core.FundingOwner WHERE Code = @code AND Status IN ('ACTIVE', 'SUSPENDED')", new { code = s.NoLobChargedTo });
        return new SeatSummary
        {
            NoLobOwnerName = noLobOwner,
            Purchased = purchased, Consumed = sku.Consumed, SkuReadUtc = sku.ReadUtc, ReservedTotal = reserved, BufferSeats = s.BufferSeats,
            PendingIncrease = pending.Values.Sum(),
            Unallocated = purchased is int p ? SeatPolicy.Unallocated(p, reserved, s.BufferSeats, pending.Values.Sum()) : null,
            NextAnniversary = SeatPolicy.NextAnniversary(s, Today), WindowOpens = SeatPolicy.TrueDownWindowOpens(s, Today), WindowOpen = SeatPolicy.IsTrueDownWindowOpen(s, Today),
            LastReadUtc = run.FinishedUtc, LastReadStatus = run.Status,
            Stale = run.FinishedUtc is null || run.FinishedUtc < clock.UtcNow.AddHours(-26),
            Settings = s,
        };
    }

    /// <summary>Seats asked for by open J9 requests (new or increase), per LOB.</summary>
    private static async Task<Dictionary<int, int>> PendingIncreaseAsync(Microsoft.Data.SqlClient.SqlConnection c, int? fundingOwnerId)
    {
        var rows = await c.QueryAsync<(int FundingOwnerId, int Seats)>(
            """
            SELECT FundingOwnerId,
                   SUM(CASE WHEN TRY_CAST(JSON_VALUE(PayloadJson, '$.requestedSeats') AS int) > ISNULL(TRY_CAST(JSON_VALUE(PayloadJson, '$.currentSeats') AS int), 0)
                            THEN TRY_CAST(JSON_VALUE(PayloadJson, '$.requestedSeats') AS int) - ISNULL(TRY_CAST(JSON_VALUE(PayloadJson, '$.currentSeats') AS int), 0) ELSE 0 END)
            FROM wf.Request
            WHERE JourneyCode = 'J9' AND State NOT IN ('DRAFT','COMPLETED','CANCELLED','CLOSED','DECLINED')
              AND (@fundingOwnerId IS NULL OR FundingOwnerId = @fundingOwnerId)
            GROUP BY FundingOwnerId
            """, new { fundingOwnerId });
        return rows.ToDictionary(r => r.FundingOwnerId, r => r.Seats);
    }

    public async Task<IReadOnlyList<SeatLobRow>> LobsAsync()
    {
        var s = await SettingsAsync();
        var scope = SeatAccess.Scope(principal);
        if (scope is { Length: 0 }) return Array.Empty<SeatLobRow>();
        await using var c = await db.OpenUserAsync();
        var pending = await PendingIncreaseAsync(c, null);
        var rows = (await c.QueryAsync<SeatLobRow>(
            """
            SELECT p.FundingOwnerId, fo.PublicId, p.Code, p.Name, p.TrackingGroup, p.ReservedSeats, p.TrackingMembers, p.AssignedSeats, p.OverSeats,
                   (SELECT COUNT(*) FROM intg.GroupMember gm
                    JOIN core.SeatGroup g ON gm.GroupId IN (g.EntraGroupId, g.AdObjectGuid) AND g.SeatGroupId = p.SeatGroupId
                    JOIN rpt.fn_SeatHolders() h ON h.MemberObjectId = gm.MemberObjectId
                    LEFT JOIN sec.Person pe ON pe.EntraObjectId = gm.MemberObjectId
                    LEFT JOIN intg.CopilotActivity a ON a.UserPrincipalName = pe.UserPrincipalName
                    WHERE a.LastActivityDate IS NULL OR a.LastActivityDate < DATEADD(DAY, -@inactive, @today)) AS Inactive,
                   (SELECT COUNT(*) FROM ops.Finding f WHERE f.FindingType LIKE 'SEAT[_]%' AND f.Status IN ('OPEN','ACKNOWLEDGED')
                    AND ((p.FundingOwnerId IS NULL AND f.FundingOwnerId IS NULL) OR f.FundingOwnerId = p.FundingOwnerId)) AS OpenFindings
            FROM rpt.fn_SeatPosition(@today) p
            LEFT JOIN core.FundingOwner fo ON fo.FundingOwnerId = p.FundingOwnerId
            ORDER BY CASE WHEN p.FundingOwnerId IS NULL THEN 1 ELSE 0 END, p.Name
            """, new { today = Today, inactive = s.InactiveDays })).ToList();
        return rows
            .Where(r => r.FundingOwnerId is int fo ? scope is null || scope.Contains(fo) : scope is null && r.AssignedSeats > 0)
            .Select(r => r.FundingOwnerId is int fo && pending.TryGetValue(fo, out var n) ? Copy(r, n) : r)
            .ToList();
    }

    private static SeatLobRow Copy(SeatLobRow r, int pending) => new()
    {
        FundingOwnerId = r.FundingOwnerId, PublicId = r.PublicId, Code = r.Code, Name = r.Name, TrackingGroup = r.TrackingGroup, ReservedSeats = r.ReservedSeats,
        TrackingMembers = r.TrackingMembers, AssignedSeats = r.AssignedSeats, OverSeats = r.OverSeats, Inactive = r.Inactive, OpenFindings = r.OpenFindings, PendingSeats = pending,
    };

    /// <summary>One LOB's seats; null when the LOB does not exist or the person may not see it.</summary>
    public async Task<SeatLobDetail?> LobAsync(Guid fundingOwnerPublicId)
    {
        var s = await SettingsAsync();
        await using var c = await db.OpenUserAsync();
        var fo = await c.QuerySingleOrDefaultAsync<(int FundingOwnerId, string Code, string Name)>(
            "SELECT FundingOwnerId, Code, Name FROM core.FundingOwner WHERE PublicId = @fundingOwnerPublicId", new { fundingOwnerPublicId });
        if (fo.FundingOwnerId == 0 || !SeatAccess.CanView(principal, fo.FundingOwnerId)) return null;
        var position = (await LobsAsync()).FirstOrDefault(r => r.FundingOwnerId == fo.FundingOwnerId)
                       ?? new SeatLobRow { FundingOwnerId = fo.FundingOwnerId, PublicId = fundingOwnerPublicId, Code = fo.Code, Name = fo.Name };
        var oim = await c.ExecuteScalarAsync<string?>(
            "SELECT TOP (1) OimRoleName FROM core.SeatGroup WHERE FundingOwnerId = @id AND Purpose = 'LOB_TRACKING' AND EffectiveTo IS NULL", new { id = fo.FundingOwnerId });
        var reservations = (await c.QueryAsync<SeatReservationRow>(
            """
            SELECT r.PublicId, r.Seats, r.PreviousSeats, r.ChangeKind, r.EffectiveFrom, r.EffectiveTo, ap.DisplayName AS ApprovedByName, r.FlexPoolReview, r.Notes,
                   rq.RequestNumber, rq.PublicId AS RequestPublicId, r.CreatedUtc
            FROM core.SeatReservation r
            LEFT JOIN sec.Person ap ON ap.PersonId = r.ApprovedByPersonId
            LEFT JOIN wf.Request rq ON rq.RequestId = r.RequestId
            WHERE r.FundingOwnerId = @id ORDER BY r.EffectiveFrom DESC, r.SeatReservationId DESC
            """, new { id = fo.FundingOwnerId })).ToList();
        var holders = (await c.QueryAsync<SeatHolderRow>(HoldersSql("gm.GroupId IN (SELECT k.GroupKey FROM core.SeatGroup CROSS APPLY (VALUES (EntraGroupId), (AdObjectGuid)) AS k (GroupKey) WHERE FundingOwnerId = @id AND Purpose = 'LOB_TRACKING' AND EffectiveTo IS NULL AND k.GroupKey IS NOT NULL)"),
            new { id = fo.FundingOwnerId })).ToList();
        var reclaims = (await c.QueryAsync<SeatReclaimRow>(
            """
            SELECT r.PublicId, r.PersonId, p.DisplayName, p.UserPrincipalName, r.LastActivityDate, r.InactiveDays, r.Status, r.Reason, d.DisplayName AS DecidedByName, r.DecidedUtc,
                   COALESCE(t.ExternalNumber, r.TicketRef) AS TicketNumber, r.CreatedUtc
            FROM core.SeatReclaim r
            JOIN sec.Person p ON p.PersonId = r.PersonId
            LEFT JOIN sec.Person d ON d.PersonId = r.DecidedByPersonId
            LEFT JOIN wf.ItsmTicket t ON t.ItsmTicketId = TRY_CAST(r.TicketRef AS bigint)
            WHERE r.FundingOwnerId = @id
            ORDER BY CASE r.Status WHEN 'PROPOSED' THEN 0 WHEN 'RECLAIM' THEN 1 ELSE 2 END, r.CreatedUtc DESC
            """, new { id = fo.FundingOwnerId })).ToList();
        var admins = await AdminsAsync(fo.FundingOwnerId);
        var trend = (await c.QueryAsync<SeatTrendPoint>(
            """
            SELECT TOP (12) PeriodMonth, ReservedSeats, AssignedSeats, Status FROM core.SeatChargeSnapshot
            WHERE FundingOwnerId = @id ORDER BY PeriodMonth DESC
            """, new { id = fo.FundingOwnerId })).Reverse().ToList();
        var findings = (await c.QueryAsync<(string, string, string, DateTime)>(
            "SELECT FindingType, Title, Severity, RaisedUtc FROM ops.Finding WHERE FundingOwnerId = @id AND FindingType LIKE 'SEAT[_]%' AND Status IN ('OPEN','ACKNOWLEDGED') ORDER BY RaisedUtc DESC",
            new { id = fo.FundingOwnerId })).ToList();
        return new SeatLobDetail
        {
            Position = position, LobName = fo.Name, Code = fo.Code, PublicId = fundingOwnerPublicId, FundingOwnerId = fo.FundingOwnerId, OimRoleName = oim,
            Reservations = reservations, Holders = holders, Reclaims = reclaims, Admins = admins, Trend = trend, Findings = findings,
        };
    }

    /// <summary>Licensed people no LOB tracking group claims (central only).</summary>
    public async Task<IReadOnlyList<SeatHolderRow>> UnattributedAsync()
    {
        if (!SeatAccess.SeesAll(principal)) return Array.Empty<SeatHolderRow>();
        await using var c = await db.OpenUserAsync();
        return (await c.QueryAsync<SeatHolderRow>(
            """
            SELECT h.MemberObjectId, p.PersonId, p.DisplayName, COALESCE(p.UserPrincipalName, x.UserPrincipalName) AS UserPrincipalName, CAST(1 AS bit) AS HasSeat,
                   x.State, x.Error, CAST(CASE WHEN x.AssignedByGroupId = '00000000-0000-0000-0000-000000000000' THEN 1 ELSE 0 END AS bit) AS Direct,
                   a.LastActivityDate, ISNULL(p.AccountEnabled, 1) AS AccountEnabled, 0 AS TotalRows
            FROM rpt.fn_SeatHolders() h
            OUTER APPLY (SELECT TOP (1) * FROM intg.SeatHolder s WHERE s.MemberObjectId = h.MemberObjectId ORDER BY s.State) x
            LEFT JOIN sec.Person p ON p.EntraObjectId = h.MemberObjectId
            LEFT JOIN intg.CopilotActivity a ON a.UserPrincipalName = COALESCE(p.UserPrincipalName, x.UserPrincipalName)
            WHERE NOT EXISTS (SELECT 1 FROM intg.GroupMember gm JOIN core.SeatGroup g ON gm.GroupId IN (g.EntraGroupId, g.AdObjectGuid) AND g.Purpose = 'LOB_TRACKING' AND g.EffectiveTo IS NULL
                              WHERE gm.MemberObjectId = h.MemberObjectId)
            ORDER BY p.DisplayName
            """)).ToList();
    }

    private static string HoldersSql(string groupFilter) =>
        $"""
        SELECT gm.MemberObjectId, p.PersonId, p.DisplayName, COALESCE(p.UserPrincipalName, x.UserPrincipalName) AS UserPrincipalName,
               CAST(CASE WHEN h.MemberObjectId IS NULL THEN 0 ELSE 1 END AS bit) AS HasSeat, x.State, x.Error,
               CAST(CASE WHEN x.AssignedByGroupId = '00000000-0000-0000-0000-000000000000' THEN 1 ELSE 0 END AS bit) AS Direct,
               a.LastActivityDate, ISNULL(p.AccountEnabled, 1) AS AccountEnabled, 0 AS TotalRows
        FROM intg.GroupMember gm
        LEFT JOIN rpt.fn_SeatHolders() h ON h.MemberObjectId = gm.MemberObjectId
        OUTER APPLY (SELECT TOP (1) s.* FROM intg.SeatHolder s JOIN core.SeatSku k ON k.SkuId = s.SkuId AND k.Kind = 'COPILOT_ADDON'
                     WHERE s.MemberObjectId = gm.MemberObjectId ORDER BY CASE WHEN s.Error IS NULL THEN 1 ELSE 0 END) x
        LEFT JOIN sec.Person p ON p.EntraObjectId = gm.MemberObjectId
        LEFT JOIN intg.CopilotActivity a ON a.UserPrincipalName = COALESCE(p.UserPrincipalName, x.UserPrincipalName)
        WHERE {groupFilter}
        ORDER BY CASE WHEN h.MemberObjectId IS NULL THEN 1 ELSE 0 END, p.DisplayName
        """;

    public async Task<IReadOnlyList<SeatAdminRow>> AdminsAsync(int fundingOwnerId)
    {
        await using var c = await db.OpenSystemAsync();
        return (await c.QueryAsync<SeatAdminRow>(
            """
            SELECT ra.PublicId, ra.PersonId, p.DisplayName, p.UserPrincipalName, ra.EffectiveFrom, g.DisplayName AS GrantedByName
            FROM sec.RoleAssignment ra JOIN sec.Person p ON p.PersonId = ra.PersonId
            LEFT JOIN sec.Person g ON g.PersonId = ra.GrantedByPersonId
            WHERE ra.RoleCode = 'LOB_SEAT_ADMIN' AND ra.ScopeType = 'FUNDING_OWNER' AND ra.ScopeId = @fundingOwnerId
              AND ra.EffectiveFrom <= @today AND (ra.EffectiveTo IS NULL OR ra.EffectiveTo >= @today)
            ORDER BY p.DisplayName
            """, new { fundingOwnerId, today = Today })).ToList();
    }

    /// <summary>People offered when naming a seat admin: enabled people with a sign-in name.</summary>
    public async Task<IReadOnlyList<Option>> PeopleAsync()
    {
        await using var c = await db.OpenSystemAsync();
        return (await c.QueryAsync<Option>(
            "SELECT UserPrincipalName AS Value, DisplayName AS Text FROM sec.Person WHERE AccountEnabled = 1 AND UserPrincipalName IS NOT NULL ORDER BY DisplayName")).ToList();
    }

    // ---------------------------------------------------------------- chargeback

    public async Task<IReadOnlyList<DateTime>> SnapshotMonthsAsync()
    {
        await using var c = await db.OpenUserAsync();
        return (await c.QueryAsync<DateTime>("SELECT DISTINCT PeriodMonth FROM core.SeatChargeSnapshot ORDER BY PeriodMonth DESC")).ToList();
    }

    public async Task<IReadOnlyList<SeatSnapshotRow>> SnapshotAsync(DateTime month)
    {
        var scope = SeatAccess.Scope(principal);
        await using var c = await db.OpenUserAsync();
        var rows = (await c.QueryAsync<SeatSnapshotRow>(
            """
            SELECT s.SnapshotId, s.FundingOwnerId, COALESCE(fo.Name, N'No LOB') AS LobName, fo.Code, s.ReservedSeats, s.AssignedSeats, s.OverSeats, s.ChargeSeats,
                   s.RatePerSeat, s.Currency, s.Amount, s.Status, s.TakenUtc, cp.DisplayName AS CertifiedByName, s.CertifiedUtc
            FROM core.SeatChargeSnapshot s
            LEFT JOIN core.FundingOwner fo ON fo.FundingOwnerId = s.FundingOwnerId
            LEFT JOIN sec.Person cp ON cp.PersonId = s.CertifiedByPersonId
            WHERE s.PeriodMonth = @month
            ORDER BY CASE WHEN s.FundingOwnerId IS NULL THEN 1 ELSE 0 END, fo.Name
            """, new { month = new DateTime(month.Year, month.Month, 1) })).ToList();
        return rows.Where(r => r.FundingOwnerId is int fo ? scope is null || scope.Contains(fo) : scope is null).ToList();
    }

    // ---------------------------------------------------------------- admin

    public async Task<IReadOnlyList<SeatGroupRow>> GroupsAsync()
    {
        await using var c = await db.OpenSystemAsync();
        return (await c.QueryAsync<SeatGroupRow>(
            """
            SELECT g.SeatGroupId, g.PublicId, g.EntraGroupId, g.DisplayName, g.Purpose, g.FundingOwnerId, fo.Name AS LobName, g.OimRoleName, g.Source, g.PurposeConfirmed,
                   g.Notes, g.EffectiveFrom, r.Found, r.OnPremisesSynced, r.UserMembers, r.NestedGroups, r.ReadUtc, g.RowVer
            FROM core.SeatGroup g
            LEFT JOIN core.FundingOwner fo ON fo.FundingOwnerId = g.FundingOwnerId
            OUTER APPLY (SELECT TOP (1) * FROM intg.SeatGroupReading x WHERE x.SeatGroupId = g.SeatGroupId ORDER BY x.SeatGroupReadingId DESC) r
            WHERE g.EffectiveTo IS NULL
            ORDER BY CASE g.Purpose WHEN 'LICENSE' THEN 0 WHEN 'LOB_TRACKING' THEN 1 ELSE 2 END, g.DisplayName
            """)).ToList();
    }

    public async Task<IReadOnlyList<SeatSkuRow>> SkusAsync()
    {
        await using var c = await db.OpenSystemAsync();
        return (await c.QueryAsync<SeatSkuRow>(
            """
            SELECT k.SkuId, k.SkuPartNumber, k.Name, k.Kind, k.IsActive, k.Notes, r.EnabledUnits, r.ConsumedUnits
            FROM core.SeatSku k
            LEFT JOIN intg.SeatSkuReading r ON r.SkuId = k.SkuId AND r.SyncRunId = (SELECT MAX(SyncRunId) FROM intg.SeatSkuReading)
            ORDER BY k.Kind, k.Name
            """)).ToList();
    }

    public async Task<IReadOnlyList<SettingRow>> SeatSettingRowsAsync()
    {
        await using var c = await db.OpenSystemAsync();
        return (await c.QueryAsync<SettingRow>("SELECT SettingKey, SettingValue, Description FROM ref.Setting WHERE SettingKey LIKE 'Seats.%' ORDER BY SettingKey")).ToList();
    }
}
