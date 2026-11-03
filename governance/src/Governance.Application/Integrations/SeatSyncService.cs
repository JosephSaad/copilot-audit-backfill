using Governance.Application.Data;
using Governance.Application.Outbox;
using Governance.Application.Seats;
using Governance.Domain;
using Microsoft.Extensions.Logging;

namespace Governance.Application.Integrations;

/// <summary>
/// X13 (daily and on demand): reads Copilot seat licenses from Microsoft Graph, app-only and read-only.
/// <list type="number">
/// <item>Subscribed SKUs: purchased and assigned units per SKU; known Copilot add-on SKUs are recorded in core.SeatSku.</item>
/// <item>Seat groups (core.SeatGroup): resolved by name, membership into intg.GroupMember, nesting and sync source recorded.</item>
/// <item>Holders of every active seat SKU with their license states (group or direct, errors) into intg.SeatHolder.</item>
/// <item>Copilot activity: from Copilot Insights' own records when the Workbench can read them, else the Copilot usage report
/// when Reports.Read.All is granted and user names are not concealed.</item>
/// </list>
/// Then it raises seat findings, proposes inactive seats for reclaim and takes the monthly draft chargeback snapshot.
/// The Workbench never changes a license or a group: OIM and ServiceNow do, through the institution's own process.
/// </summary>
public sealed class SeatSyncService(Db db, ISeatLicenseApi api, FindingWriter findings, IClock clock, ILogger<SeatSyncService> log, IInsightsActivitySource? insights = null)
{
    /// <summary>Microsoft 365 Copilot add-on SKUs (Microsoft Learn: product names and service plan identifiers for licensing).</summary>
    public static readonly IReadOnlyDictionary<string, (Guid SkuId, string Name)> KnownAddOns = new Dictionary<string, (Guid, string)>(StringComparer.OrdinalIgnoreCase)
    {
        ["Microsoft_365_Copilot"] = (Guid.Parse("639dec6b-bb19-468b-871c-c5c441c4b0cb"), "Microsoft 365 Copilot"),
        ["M365_Copilot"] = (Guid.Parse("a809996b-059e-42e2-9866-db24b99a9782"), "Microsoft Copilot for Microsoft 365"),
        ["Microsoft_365_Copilot_EDU"] = (Guid.Parse("ad9c22b3-52d7-4e7e-973c-88121ea96436"), "Microsoft 365 Copilot (Education Faculty)"),
    };

    public const string FindingsLink = "/workbench/Operations/Findings";

    private sealed record GroupRow(int SeatGroupId, Guid? EntraGroupId, string DisplayName, string Purpose);

    /// <summary>Each person's last Copilot activity into intg.CopilotActivity; people no longer in the source leave it.</summary>
    private Task<int> WriteActivityAsync(IEnumerable<(string UserPrincipalName, DateTime? LastActivityDate)> users, DateTime? refresh, long runId, CancellationToken ct)
        => db.InTransactionAsync(async uow =>
        {
            foreach (var u in users)
                await uow.ExecuteAsync(
                    """
                    MERGE intg.CopilotActivity AS t USING (SELECT @UserPrincipalName AS UserPrincipalName) AS s ON t.UserPrincipalName = s.UserPrincipalName
                    WHEN MATCHED THEN UPDATE SET LastActivityDate = @LastActivityDate, ReportRefreshDate = @refresh, LastSeenRunId = @runId
                    WHEN NOT MATCHED THEN INSERT (UserPrincipalName, LastActivityDate, ReportRefreshDate, LastSeenRunId) VALUES (@UserPrincipalName, @LastActivityDate, @refresh, @runId);
                    """, new { u.UserPrincipalName, u.LastActivityDate, refresh, runId });
            await uow.ExecuteAsync("DELETE FROM intg.CopilotActivity WHERE LastSeenRunId <> @runId", new { runId });
            return 0;
        }, ct);

    public Task<Guid> RunAsync(int? operatorPersonId, CancellationToken ct)
        => SyncRuns.RunAsync(db, IntegrationCodes.Seats, "APP_ONLY", operatorPersonId, async runId =>
        {
            var notes = new List<string>();
            var today = clock.UtcNow.Date;

            // 1. Subscribed SKUs (LicenseAssignment.Read.All). A refusal fails the run: nothing else is meaningful without it.
            var skus = await api.SubscribedSkusAsync(ct);
            await db.InTransactionAsync(async uow =>
            {
                foreach (var s in skus)
                    await uow.ExecuteAsync(
                        """
                        INSERT intg.SeatSkuReading (SyncRunId, SkuId, SkuPartNumber, EnabledUnits, WarningUnits, SuspendedUnits, ConsumedUnits, ReadUtc)
                        VALUES (@runId, @SkuId, @SkuPartNumber, @Enabled, @Warning, @Suspended, @Consumed, @now)
                        """, new { runId, s.SkuId, s.SkuPartNumber, s.Enabled, s.Warning, s.Suspended, s.Consumed, now = clock.UtcNow });
                // Known add-ons are recorded once; suites that include Copilot are classified by an administrator, never guessed.
                foreach (var s in skus.Where(s => KnownAddOns.ContainsKey(s.SkuPartNumber) || KnownAddOns.Values.Any(k => k.SkuId == s.SkuId)))
                {
                    var name = KnownAddOns.TryGetValue(s.SkuPartNumber, out var k) ? k.Name : s.SkuPartNumber;
                    await uow.ExecuteAsync(
                        """
                        IF NOT EXISTS (SELECT 1 FROM core.SeatSku WHERE SkuId = @SkuId)
                            INSERT core.SeatSku (SkuId, SkuPartNumber, Name, Kind, IsActive, Notes)
                            VALUES (@SkuId, @SkuPartNumber, @name, 'COPILOT_ADDON', 1, N'Recorded by the seat read from the tenant''s subscribed SKUs.')
                        """, new { s.SkuId, s.SkuPartNumber, name });
                }
                return 0;
            }, ct);

            // 2. Seat groups.
            var groupsRead = await ReadGroupsAsync(runId, today, notes, ct);

            // 3. Holders of every active seat SKU, with their license states.
            var activeSkus = (await db.InTransactionAsync(u => u.QueryAsync<Guid>("SELECT SkuId FROM core.SeatSku WHERE IsActive = 1"), ct)).ToList();
            var holders = 0;
            foreach (var sku in activeSkus)
            {
                var users = await api.UsersWithSkuAsync(sku, ct);
                holders += users.Count;
                await db.InTransactionAsync(async uow =>
                {
                    foreach (var row in HolderRows(users, sku))
                        await uow.ExecuteAsync(
                            """
                            MERGE intg.SeatHolder AS t
                            USING (SELECT @MemberObjectId AS MemberObjectId, @SkuId AS SkuId, @AssignedByGroupId AS AssignedByGroupId) AS s
                               ON t.MemberObjectId = s.MemberObjectId AND t.SkuId = s.SkuId AND t.AssignedByGroupId = s.AssignedByGroupId
                            WHEN MATCHED THEN UPDATE SET State = @State, Error = @Error, UsageLocation = @UsageLocation, UserPrincipalName = @UserPrincipalName,
                                 StateUpdatedUtc = @StateUpdatedUtc, LastSeenRunId = @runId
                            WHEN NOT MATCHED THEN INSERT (MemberObjectId, SkuId, AssignedByGroupId, State, Error, UsageLocation, UserPrincipalName, StateUpdatedUtc, LastSeenRunId, FirstSeenUtc)
                                 VALUES (@MemberObjectId, @SkuId, @AssignedByGroupId, @State, @Error, @UsageLocation, @UserPrincipalName, @StateUpdatedUtc, @runId, @now);
                            """,
                            new { row.MemberObjectId, row.SkuId, row.AssignedByGroupId, row.State, row.Error, row.UsageLocation, row.UserPrincipalName, row.StateUpdatedUtc, runId, now = clock.UtcNow });
                    await uow.ExecuteAsync("DELETE FROM intg.SeatHolder WHERE SkuId = @sku AND LastSeenRunId <> @runId", new { sku, runId });
                    return 0;
                }, ct);
            }
            // SKUs no longer active keep no holder rows.
            await db.InTransactionAsync(u => u.ExecuteAsync("DELETE h FROM intg.SeatHolder h WHERE NOT EXISTS (SELECT 1 FROM core.SeatSku k WHERE k.SkuId = h.SkuId AND k.IsActive = 1)"), ct);
            if (activeSkus.Count == 0) notes.Add("No Copilot SKU is recorded yet: none of the tenant's subscribed SKUs is a known Copilot add-on. An administrator can add the SKU in core.SeatSku.");

            // 4. Copilot activity: Copilot Insights' own records when the Workbench can read them (every loaded day, names
            //    never hidden), else the usage report (Reports.Read.All). Optional either way: a refusal is a note, not a failure.
            var settings = await db.InTransactionAsync(SeatSettings.LoadAsync, ct);
            var usageRead = false;
            if (insights is not null)
            {
                try
                {
                    var activity = await insights.ReadAsync(ct);
                    if (activity.AsOf is null)
                        notes.Add("Copilot Insights has no activity loaded yet, so the usage report was read instead.");
                    else if (IsStale(activity.AsOf.Value, today))
                    {
                        // Its nightly load has stopped: every holder would look inactive since then.
                        log.LogWarning("Copilot Insights' activity ends {AsOf:yyyy-MM-dd}, more than {Days} days ago; seat run {RunId} reads the usage report instead.", activity.AsOf, MaxInsightsAgeDays, runId);
                        notes.Add($"Copilot Insights' activity ends {activity.AsOf:d MMM yyyy}, more than {MaxInsightsAgeDays} days ago (its nightly load may have stopped), so the usage report was read instead.");
                    }
                    else
                    {
                        await WriteActivityAsync(activity.Users.Select(u => (u.UserPrincipalName, (DateTime?)u.LastActivityDate)), activity.AsOf, runId, ct);
                        usageRead = true;
                        notes.Add($"Copilot activity from Copilot Insights, up to {activity.AsOf:d MMM yyyy}: {activity.Users.Count} people with any use.");
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    log.LogWarning(ex, "Copilot Insights' activity could not be read in seat run {RunId}.", runId);
                    notes.Add("Copilot Insights' activity could not be read, so the usage report was read instead.");
                }
            }
            if (!usageRead)
            {
                try
                {
                    var usage = await api.CopilotUsageAsync(settings.UsagePeriod, ct);
                    if (usage.NamesConcealed)
                        notes.Add("The Copilot usage report conceals user names, so seats could not be matched to activity. Turn off \"Display concealed user, group, and site names in all reports\" in the Microsoft 365 admin center (Org settings > Reports).");
                    else
                    {
                        await WriteActivityAsync(usage.Users.Select(u => (u.UserPrincipalName, u.LastActivityDate)), usage.ReportRefreshDate, runId, ct);
                        usageRead = true;
                    }
                }
                catch (IntegrationPermissionException)
                {
                    notes.Add("The Copilot usage report was refused (Reports.Read.All not granted): no reclaim proposals this run.");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    log.LogWarning(ex, "The Copilot usage report could not be read in seat run {RunId}.", runId);
                    notes.Add("The Copilot usage report could not be read: no reclaim proposals this run.");
                }
            }

            // 5. Findings, reclaim proposals and the monthly draft snapshot, in one transaction.
            var (raised, proposed, snapshot) = await db.InTransactionAsync(async uow =>
            {
                var r = await new SeatFindingRules(findings, today, runId).EvaluateAsync(uow, settings);
                var p = await ProposeReclaimsAsync(uow, settings, today, usageRead);
                var s = await TakeSnapshotAsync(uow, settings, today, runId, force: false);
                return (r, p, s);
            }, ct);

            var message = $"{holders} seat assignments read across {activeSkus.Count} SKU(s), {groupsRead} seat group(s), {raised} new finding(s), {proposed} seat(s) proposed for reclaim.";
            if (snapshot is not null) message += " " + snapshot;
            if (notes.Count > 0) message += " " + string.Join(" ", notes);
            return (holders, raised, message);
        }, ct);

    /// <summary>Copilot Insights' activity older than this (days before today) is not used: the usage report is read instead.</summary>
    public const int MaxInsightsAgeDays = 3;

    /// <summary>Whether Insights' last loaded day is too old to judge inactivity by. Pure, for the tests.</summary>
    public static bool IsStale(DateTime asOf, DateTime today) => (today.Date - asOf.Date).TotalDays > MaxInsightsAgeDays;

    /// <summary>One row per user, SKU and source. A state for another SKU is ignored; a license with no state is kept as a direct one.</summary>
    public static IEnumerable<(Guid MemberObjectId, Guid SkuId, Guid AssignedByGroupId, string? State, string? Error, string? UsageLocation, string? UserPrincipalName, DateTime? StateUpdatedUtc)>
        HolderRows(IEnumerable<LicensedUserData> users, Guid sku)
    {
        foreach (var u in users)
        {
            var states = u.States.Where(s => s.SkuId == sku).ToList();
            if (states.Count == 0) { yield return (u.Id, sku, Guid.Empty, "Active", null, u.UsageLocation, u.UserPrincipalName, null); continue; }
            foreach (var s in states.GroupBy(s => s.AssignedByGroup ?? Guid.Empty).Select(g => g.First()))
                yield return (u.Id, sku, s.AssignedByGroup ?? Guid.Empty, s.State, s.Error, u.UsageLocation, u.UserPrincipalName, s.LastUpdatedUtc);
        }
    }

    private async Task<int> ReadGroupsAsync(long runId, DateTime today, List<string> notes, CancellationToken ct)
    {
        var groups = (await db.InTransactionAsync(u => u.QueryAsync<GroupRow>(
            "SELECT SeatGroupId, EntraGroupId, DisplayName, Purpose FROM core.SeatGroup WHERE EffectiveFrom <= @today AND (EffectiveTo IS NULL OR EffectiveTo >= @today)",
            new { today }), ct)).ToList();
        var read = 0;
        foreach (var g in groups)
        {
            SeatGroupData? found;
            try { found = g.EntraGroupId is Guid id ? await api.GetGroupAsync(id, ct) : await api.FindGroupAsync(g.DisplayName, ct); }
            catch (IntegrationPermissionException) { throw; }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning(ex, "Seat group {Group} could not be read; keeping its last good membership.", g.DisplayName);
                notes.Add($"Group {g.DisplayName} could not be read; its last membership was kept.");
                continue;
            }
            if (found is null)
            {
                await db.InTransactionAsync(u => u.ExecuteAsync(
                    "INSERT intg.SeatGroupReading (SyncRunId, SeatGroupId, EntraGroupId, Found) VALUES (@runId, @SeatGroupId, @EntraGroupId, 0)",
                    new { runId, g.SeatGroupId, g.EntraGroupId }), ct);
                continue;
            }
            if (found.DuplicateName) notes.Add($"More than one Entra group is named {g.DisplayName}; the first was used. Record its object ID on the seat group.");

            GroupMembersData members;
            try { members = await api.GroupMembersAsync(found.Id, ct); }
            catch (IntegrationPermissionException) { throw; }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning(ex, "Members of seat group {Group} could not be read; keeping the last good membership.", g.DisplayName);
                notes.Add($"Members of {g.DisplayName} could not be read; the last membership was kept.");
                continue;
            }
            await db.InTransactionAsync(async uow =>
            {
                if (g.EntraGroupId is null)
                    await uow.ExecuteAsync("UPDATE core.SeatGroup SET EntraGroupId = @id WHERE SeatGroupId = @SeatGroupId AND EntraGroupId IS NULL", new { id = found.Id, g.SeatGroupId });
                foreach (var m in members.UserIds.Distinct())
                    await uow.ExecuteAsync(
                        "MERGE intg.GroupMember AS t USING (SELECT @groupId AS GroupId, @m AS MemberObjectId) AS s ON t.GroupId = s.GroupId AND t.MemberObjectId = s.MemberObjectId WHEN MATCHED THEN UPDATE SET LastSeenRunId = @runId WHEN NOT MATCHED THEN INSERT (GroupId, MemberObjectId, LastSeenRunId) VALUES (@groupId, @m, @runId);",
                        new { groupId = found.Id, m, runId });
                await uow.ExecuteAsync("DELETE FROM intg.GroupMember WHERE GroupId = @groupId AND LastSeenRunId <> @runId", new { groupId = found.Id, runId });
                await uow.ExecuteAsync(
                    """
                    INSERT intg.SeatGroupReading (SyncRunId, SeatGroupId, EntraGroupId, Found, OnPremisesSynced, UserMembers, NestedGroups, LicensedSkuIds)
                    VALUES (@runId, @SeatGroupId, @id, 1, @OnPremisesSyncEnabled, @users, @NestedGroups, @skus)
                    """,
                    new
                    {
                        runId, g.SeatGroupId, id = found.Id, found.OnPremisesSyncEnabled, users = members.UserIds.Count, members.NestedGroups,
                        skus = found.LicensedSkuIds.Count == 0 ? null : string.Join(",", found.LicensedSkuIds),
                    });
                return 0;
            }, ct);
            read++;
        }
        return read;
    }

    /// <summary>
    /// Seats with no Copilot activity for Seats.InactiveDays, held for at least as long, are proposed for reclaim; proposals whose holder became active
    /// or lost the seat are cancelled; reclaims whose seat is gone are closed as removed. Without a usage report this run,
    /// only the seat-gone checks run (nothing is proposed or cancelled on missing data).
    /// </summary>
    public static async Task<int> ProposeReclaimsAsync(Uow uow, SeatSettings settings, DateTime today, bool usageRead)
    {
        var cutoff = today.AddDays(-settings.InactiveDays);
        await uow.ExecuteAsync(
            """
            UPDATE r SET Status = 'REMOVED', ClosedUtc = SYSUTCDATETIME()
            FROM core.SeatReclaim r JOIN sec.Person p ON p.PersonId = r.PersonId
            WHERE r.Status = 'RECLAIM' AND NOT EXISTS (SELECT 1 FROM rpt.fn_SeatHolders() s WHERE s.MemberObjectId = p.EntraObjectId);

            UPDATE r SET Status = 'CANCELLED', ClosedUtc = SYSUTCDATETIME(), Reason = COALESCE(r.Reason, N'The seat was already gone.')
            FROM core.SeatReclaim r JOIN sec.Person p ON p.PersonId = r.PersonId
            WHERE r.Status = 'PROPOSED' AND NOT EXISTS (SELECT 1 FROM rpt.fn_SeatHolders() s WHERE s.MemberObjectId = p.EntraObjectId);
            """);
        if (!usageRead) return 0;

        await uow.ExecuteAsync(
            """
            UPDATE r SET Status = 'CANCELLED', ClosedUtc = SYSUTCDATETIME(), Reason = N'Copilot activity resumed.'
            FROM core.SeatReclaim r JOIN sec.Person p ON p.PersonId = r.PersonId
            JOIN intg.CopilotActivity a ON a.UserPrincipalName = p.UserPrincipalName
            WHERE r.Status = 'PROPOSED' AND a.LastActivityDate > @cutoff;
            """, new { cutoff });

        return await uow.ExecuteAsync(
            """
            INSERT core.SeatReclaim (FundingOwnerId, PersonId, LastActivityDate, InactiveDays, Status, Reason)
            SELECT lob.FundingOwnerId, p.PersonId, a.LastActivityDate,
                   CASE WHEN a.LastActivityDate IS NULL THEN NULL ELSE DATEDIFF(DAY, a.LastActivityDate, @today) END,
                   'PROPOSED',
                   CASE WHEN a.LastActivityDate IS NULL THEN N'No Copilot activity in the usage report period.'
                        ELSE CONCAT(N'No Copilot activity for ', DATEDIFF(DAY, a.LastActivityDate, @today), N' days.') END
            FROM rpt.fn_SeatHolders() s
            JOIN sec.Person p ON p.EntraObjectId = s.MemberObjectId
            LEFT JOIN intg.CopilotActivity a ON a.UserPrincipalName = p.UserPrincipalName
            OUTER APPLY (SELECT CASE WHEN COUNT(*) = 1 THEN MIN(g.FundingOwnerId) END AS FundingOwnerId
                         FROM intg.GroupMember gm
                         JOIN core.SeatGroup g ON g.EntraGroupId = gm.GroupId AND g.Purpose = 'LOB_TRACKING'
                              AND g.EffectiveFrom <= @today AND (g.EffectiveTo IS NULL OR g.EffectiveTo >= @today)
                         WHERE gm.MemberObjectId = s.MemberObjectId) lob
            -- Since when the person has held a seat: the earliest of the license state's date in Entra and the day the
            -- seat read first saw it. Unknown (rows read before FirstSeenUtc existed) counts as long held.
            OUTER APPLY (SELECT CASE WHEN COUNT(CASE WHEN h.FirstSeenUtc IS NULL AND h.StateUpdatedUtc IS NULL THEN 1 END) > 0 THEN NULL
                                     ELSE MIN(CASE WHEN h.StateUpdatedUtc IS NULL OR h.FirstSeenUtc < h.StateUpdatedUtc THEN h.FirstSeenUtc ELSE h.StateUpdatedUtc END) END AS HeldSince
                         FROM intg.SeatHolder h
                         JOIN core.SeatSku k ON k.SkuId = h.SkuId AND k.Kind = 'COPILOT_ADDON' AND k.IsActive = 1
                         WHERE h.MemberObjectId = s.MemberObjectId AND h.State IN ('Active','ActiveWithError')) seat
            WHERE (a.LastActivityDate IS NULL OR a.LastActivityDate <= @cutoff)
              -- A seat held for less than the inactivity window is not proposed: its holder has not had the time to use it.
              AND (seat.HeldSince IS NULL OR seat.HeldSince <= @cutoff)
              AND NOT EXISTS (SELECT 1 FROM core.SeatReclaim r WHERE r.PersonId = p.PersonId AND r.Status IN ('PROPOSED','RECLAIM'))
              -- A seat the business owner chose to keep is not proposed again within the same inactivity window.
              AND NOT EXISTS (SELECT 1 FROM core.SeatReclaim r WHERE r.PersonId = p.PersonId AND r.Status = 'KEEP' AND r.DecidedUtc > @cutoff);
            """, new { today, cutoff });
    }

    /// <summary>
    /// Draft chargeback rows for the month that just ended: the reservation in effect on its last day, the seats assigned
    /// in the latest read-back, the single rate. Drafts are replaced; a certified month is never touched (CAL-05).
    /// Taken automatically only in the first 7 days after the month ends, so the assignments are close to month end;
    /// <paramref name="force"/> retakes a draft at any time. Returns a note, or null when nothing was done.
    /// </summary>
    public static async Task<string?> TakeSnapshotAsync(Uow uow, SeatSettings settings, DateTime today, long? runId, bool force)
    {
        var month = SeatPolicy.SnapshotMonthFor(today);
        var monthEnd = month.AddMonths(1).AddDays(-1);
        if (await uow.ScalarAsync<int>("SELECT COUNT(*) FROM core.SeatChargeSnapshot WHERE PeriodMonth = @month AND Status = 'CERTIFIED'", new { month }) > 0)
            return null;
        var drafts = await uow.ScalarAsync<int>("SELECT COUNT(*) FROM core.SeatChargeSnapshot WHERE PeriodMonth = @month", new { month });
        if (!force && (drafts > 0 || today > monthEnd.AddDays(7))) return null;

        var position = (await uow.QueryAsync<(int? FundingOwnerId, int ReservedSeats, int AssignedSeats)>(
            "SELECT FundingOwnerId, ReservedSeats, AssignedSeats FROM rpt.fn_SeatPosition(@monthEnd)", new { monthEnd })).ToList();
        /* Seats no line of business claims: to the funding owner Finance named, or a row of their own. */
        var noLobOwner = settings.NoLobChargedTo.Length == 0 ? null
            : await uow.ScalarAsync<int?>("SELECT FundingOwnerId FROM core.FundingOwner WHERE Code = @code", new { code = settings.NoLobChargedTo });
        var rows = SeatPolicy.ChargedRows(position, noLobOwner);
        await uow.ExecuteAsync("DELETE FROM core.SeatChargeSnapshot WHERE PeriodMonth = @month AND Status = 'DRAFT'", new { month });
        foreach (var r in rows)
        {
            if (r.FundingOwnerId is null && r.AssignedSeats == 0) continue;
            var reserved = r.FundingOwnerId is null ? 0 : r.ReservedSeats;
            var (over, charge) = SeatPolicy.Charge(reserved, r.AssignedSeats);
            await uow.ExecuteAsync(
                """
                INSERT core.SeatChargeSnapshot (PeriodMonth, FundingOwnerId, ReservedSeats, AssignedSeats, OverSeats, ChargeSeats, RatePerSeat, Currency, Amount, Status, SyncRunId)
                VALUES (@month, @FundingOwnerId, @reserved, @AssignedSeats, @over, @charge, @rate, @Currency, @amount, 'DRAFT', @runId)
                """,
                new { month, r.FundingOwnerId, reserved, r.AssignedSeats, over, charge, rate = settings.RatePerSeatMonth, settings.Currency, amount = SeatPolicy.Amount(settings, charge), runId });
        }
        return $"Draft seat chargeback for {month:MMMM yyyy} taken ({rows.Count(r => r.FundingOwnerId is not null)} LOBs).";
    }
}

/// <summary>
/// The seat findings, all computed in SQL from the latest read-back. Each type raises one finding per object while the
/// condition holds and resolves it when the condition goes away. LOB and tenant level findings send one notice when new;
/// person level findings show on the Findings page and in the run summary only.
/// </summary>
public sealed class SeatFindingRules(FindingWriter findings, DateTime today, long runId)
{
    private sealed record Reading(int SeatGroupId, string DisplayName, string Purpose, int? FundingOwnerId, bool Found, int? NestedGroups);

    public sealed record Hit(string Key, string Severity, string Title, string? Details, int? FundingOwnerId, string? ExternalId, string? Expected = null, string? Actual = null);

    private const string TrackingGroups =
        "core.SeatGroup g ON g.Purpose = 'LOB_TRACKING' AND g.EntraGroupId IS NOT NULL AND g.EffectiveFrom <= @today AND (g.EffectiveTo IS NULL OR g.EffectiveTo >= @today)";

    private const string HolderName =
        "COALESCE(p.DisplayName, (SELECT TOP (1) x.UserPrincipalName FROM intg.SeatHolder x WHERE x.MemberObjectId = s.MemberObjectId AND x.UserPrincipalName IS NOT NULL), CONVERT(nvarchar(36), s.MemberObjectId))";

    public async Task<int> EvaluateAsync(Uow uow, SeatSettings settings)
    {
        var raised = 0;
        var latestRun = await uow.ScalarAsync<long?>("SELECT MAX(SyncRunId) FROM intg.SeatGroupReading") ?? 0;

        // Over the reservation (FIN-06): seats above it are billed on top.
        raised += await ApplyAsync(uow, FindingTypes.SeatOverReservation, notify: true, (await uow.QueryAsync<(int FundingOwnerId, string Name, int ReservedSeats, int AssignedSeats, int OverSeats)>(
            "SELECT FundingOwnerId, Name, ReservedSeats, AssignedSeats, OverSeats FROM rpt.fn_SeatPosition(@today) WHERE FundingOwnerId IS NOT NULL AND OverSeats > 0", new { today }))
            .Select(r => new Hit($"{FindingTypes.SeatOverReservation}:{r.FundingOwnerId}", "MEDIUM", $"{r.Name} holds more Copilot seats than it reserved",
                $"{r.AssignedSeats} people in the LOB's tracking group hold a seat against a reservation of {r.ReservedSeats}. The {r.OverSeats} seat(s) above it are billed on top of the reservation (FIN-06). Raise the reservation (J9) or remove people through OIM.",
                r.FundingOwnerId, null, $"{r.ReservedSeats} seats", $"{r.AssignedSeats} seats")));

        // Reservations plus the buffer above what was purchased (Section 4.4).
        var purchased = await uow.ScalarAsync<int?>(
            """
            SELECT SUM(r.EnabledUnits) FROM intg.SeatSkuReading r JOIN core.SeatSku k ON k.SkuId = r.SkuId AND k.Kind = 'COPILOT_ADDON' AND k.IsActive = 1
            WHERE r.SyncRunId = (SELECT MAX(SyncRunId) FROM intg.SeatSkuReading)
            """);
        var reserved = await uow.ScalarAsync<int?>("SELECT SUM(ReservedSeats) FROM rpt.fn_SeatPosition(@today) WHERE FundingOwnerId IS NOT NULL", new { today }) ?? 0;
        var inventoryHits = new List<Hit>();
        if (purchased is int bought && reserved + settings.BufferSeats > bought)
            inventoryHits.Add(new Hit($"{FindingTypes.SeatReservationsOverInventory}:TENANT", "HIGH", "Copilot seat reservations exceed the seats purchased",
                $"LOB reservations total {reserved} seats and the operational buffer is {settings.BufferSeats}, but the tenant has {bought} Copilot add-on seats. Buy seats (J8) or hold new reservations until the April true-down.",
                null, null, $"at most {bought} seats (reservations + buffer)", $"{reserved + settings.BufferSeats} seats"));
        raised += await ApplyAsync(uow, FindingTypes.SeatReservationsOverInventory, notify: true, inventoryHits);

        // Licensed with no LOB: nobody is charged for the seat.
        raised += await ApplyAsync(uow, FindingTypes.SeatNoLob, notify: false, (await uow.QueryAsync<(Guid MemberObjectId, string Name)>(
            $"""
            SELECT s.MemberObjectId, {HolderName} AS Name
            FROM rpt.fn_SeatHolders() s LEFT JOIN sec.Person p ON p.EntraObjectId = s.MemberObjectId
            WHERE NOT EXISTS (SELECT 1 FROM intg.GroupMember gm JOIN {TrackingGroups} AND g.EntraGroupId = gm.GroupId WHERE gm.MemberObjectId = s.MemberObjectId)
            """, new { today }))
            .Select(r => new Hit($"{FindingTypes.SeatNoLob}:{r.MemberObjectId}", "MEDIUM", $"{r.Name} holds a Copilot seat but is in no LOB tracking group",
                "No LOB is charged for this seat. Request the person's LOB role in OIM (which adds the tracking group), or remove the license role.", null, r.MemberObjectId.ToString())));

        // In two tracking groups: the seat would be counted, and charged, twice.
        raised += await ApplyAsync(uow, FindingTypes.SeatTwoLobs, notify: false, (await uow.QueryAsync<(Guid MemberObjectId, string Name, string Groups)>(
            $"""
            SELECT gm.MemberObjectId, COALESCE(MAX(p.DisplayName), CONVERT(nvarchar(36), gm.MemberObjectId)) AS Name, STRING_AGG(g.DisplayName, N', ') AS Groups
            FROM intg.GroupMember gm JOIN {TrackingGroups} AND g.EntraGroupId = gm.GroupId
            LEFT JOIN sec.Person p ON p.EntraObjectId = gm.MemberObjectId
            GROUP BY gm.MemberObjectId HAVING COUNT(DISTINCT g.FundingOwnerId) > 1
            """, new { today }))
            .Select(r => new Hit($"{FindingTypes.SeatTwoLobs}:{r.MemberObjectId}", "MEDIUM", $"{r.Name} is in more than one LOB tracking group",
                $"Tracking groups: {r.Groups}. Each LOB counts the seat. Remove the OIM role of the LOB the person does not belong to.", null, r.MemberObjectId.ToString(), "one LOB", r.Groups)));

        // In a tracking group without a seat: the OIM role is half done, or the license failed.
        raised += await ApplyAsync(uow, FindingTypes.SeatTrackedNoLicense, notify: false, (await uow.QueryAsync<(Guid MemberObjectId, string Name, int FundingOwnerId, string GroupName)>(
            $"""
            SELECT gm.MemberObjectId, COALESCE(p.DisplayName, CONVERT(nvarchar(36), gm.MemberObjectId)) AS Name, g.FundingOwnerId, g.DisplayName AS GroupName
            FROM intg.GroupMember gm JOIN {TrackingGroups} AND g.EntraGroupId = gm.GroupId
            LEFT JOIN sec.Person p ON p.EntraObjectId = gm.MemberObjectId
            WHERE NOT EXISTS (SELECT 1 FROM rpt.fn_SeatHolders() s WHERE s.MemberObjectId = gm.MemberObjectId)
            """, new { today }))
            .Select(r => new Hit($"{FindingTypes.SeatTrackedNoLicense}:{r.MemberObjectId}:{r.FundingOwnerId}", "LOW", $"{r.Name} is tracked for an LOB but holds no Copilot seat",
                $"The person is in {r.GroupName} but has no active Copilot license. Check the license group membership and any license error, or end the OIM role.", r.FundingOwnerId, r.MemberObjectId.ToString())));

        // Direct assignment: outside the license group, so outside OIM and the reservation.
        raised += await ApplyAsync(uow, FindingTypes.SeatDirectAssignment, notify: false, (await uow.QueryAsync<(Guid MemberObjectId, string Name)>(
            $"""
            SELECT DISTINCT s.MemberObjectId, {HolderName} AS Name
            FROM intg.SeatHolder s JOIN core.SeatSku k ON k.SkuId = s.SkuId AND k.Kind = 'COPILOT_ADDON' AND k.IsActive = 1
            LEFT JOIN sec.Person p ON p.EntraObjectId = s.MemberObjectId
            WHERE s.AssignedByGroupId = '00000000-0000-0000-0000-000000000000'
            """))
            .Select(r => new Hit($"{FindingTypes.SeatDirectAssignment}:{r.MemberObjectId}", "MEDIUM", $"{r.Name} has a Copilot seat assigned directly",
                "The license was assigned to the person, not through the license group, so it bypasses OIM and the LOB reservation. Remove the direct assignment once the OIM role is in place.", null, r.MemberObjectId.ToString())));

        // License assignment errors: the person is in the group but has no working seat.
        raised += await ApplyAsync(uow, FindingTypes.SeatLicenseError, notify: false, (await uow.QueryAsync<(Guid MemberObjectId, string Name, string Error, string? UsageLocation, string Sku)>(
            $"""
            SELECT s.MemberObjectId, {HolderName} AS Name, s.Error, s.UsageLocation, k.Name AS Sku
            FROM intg.SeatHolder s JOIN core.SeatSku k ON k.SkuId = s.SkuId AND k.IsActive = 1
            LEFT JOIN sec.Person p ON p.EntraObjectId = s.MemberObjectId
            WHERE s.Error IS NOT NULL
            """))
            .Select(r => new Hit($"{FindingTypes.SeatLicenseError}:{r.MemberObjectId}:{r.Error}", r.Error == "CountViolation" ? "HIGH" : "MEDIUM",
                $"{r.Name}: {r.Sku} could not be assigned ({LicenseErrorLabel(r.Error)})", LicenseErrorFix(r.Error, r.UsageLocation), null, r.MemberObjectId.ToString(), "license active", r.Error)));

        // Leavers still holding a seat.
        raised += await ApplyAsync(uow, FindingTypes.SeatLeaver, notify: false, (await uow.QueryAsync<(Guid MemberObjectId, string Name, bool AccountEnabled, DateTime? LeaveDate)>(
            """
            SELECT s.MemberObjectId, p.DisplayName AS Name, p.AccountEnabled, p.LeaveDate
            FROM rpt.fn_SeatHolders() s JOIN sec.Person p ON p.EntraObjectId = s.MemberObjectId
            WHERE p.AccountEnabled = 0 OR p.LeaveDate <= @today
            """, new { today }))
            .Select(r => new Hit($"{FindingTypes.SeatLeaver}:{r.MemberObjectId}", "HIGH", $"{r.Name} has left but still holds a Copilot seat",
                $"{(r.AccountEnabled ? $"Leave date {r.LeaveDate:yyyy-MM-dd}" : "The account is disabled")}. End the OIM license role so the seat returns to the pool.", null, r.MemberObjectId.ToString())));

        // In a confirmed exclusion group and holding a seat.
        raised += await ApplyAsync(uow, FindingTypes.SeatExcludedHolder, notify: false, (await uow.QueryAsync<(Guid MemberObjectId, string Name, string GroupName)>(
            $"""
            SELECT s.MemberObjectId, {HolderName} AS Name, MIN(g.DisplayName) AS GroupName
            FROM rpt.fn_SeatHolders() s
            JOIN intg.GroupMember gm ON gm.MemberObjectId = s.MemberObjectId
            JOIN core.SeatGroup g ON g.EntraGroupId = gm.GroupId AND g.Purpose = 'EXCLUSION' AND g.PurposeConfirmed = 1
                 AND g.EffectiveFrom <= @today AND (g.EffectiveTo IS NULL OR g.EffectiveTo >= @today)
            LEFT JOIN sec.Person p ON p.EntraObjectId = s.MemberObjectId
            GROUP BY s.MemberObjectId, p.DisplayName
            """, new { today }))
            .Select(r => new Hit($"{FindingTypes.SeatExcludedHolder}:{r.MemberObjectId}", "HIGH", $"{r.Name} is excluded from Copilot but holds a seat",
                $"The person is in {r.GroupName}, a confirmed exclusion group. Remove the license role, or remove them from the exclusion group with the owner's approval.", null, r.MemberObjectId.ToString())));

        // Paid twice: the add-on on top of a suite that already includes Copilot.
        raised += await ApplyAsync(uow, FindingTypes.SeatPaidTwice, notify: false, (await uow.QueryAsync<(Guid MemberObjectId, string Name, string Suite)>(
            $"""
            SELECT s.MemberObjectId, {HolderName} AS Name, MIN(k.Name) AS Suite
            FROM rpt.fn_SeatHolders() s
            JOIN intg.SeatHolder h ON h.MemberObjectId = s.MemberObjectId AND h.State IN ('Active','ActiveWithError')
            JOIN core.SeatSku k ON k.SkuId = h.SkuId AND k.Kind = 'INCLUDES_COPILOT' AND k.IsActive = 1
            LEFT JOIN sec.Person p ON p.EntraObjectId = s.MemberObjectId
            GROUP BY s.MemberObjectId, p.DisplayName
            """))
            .Select(r => new Hit($"{FindingTypes.SeatPaidTwice}:{r.MemberObjectId}", "MEDIUM", $"{r.Name} pays for Copilot twice",
                $"The person holds the Copilot add-on and {r.Suite}, which already includes Copilot. Remove the add-on license role; the seat returns to the pool.", null, r.MemberObjectId.ToString())));

        // Seat groups this run: missing in Entra, or nesting groups (group-based licensing ignores nested members).
        var readings = (await uow.QueryAsync<Reading>(
            """
            SELECT g.SeatGroupId, g.DisplayName, g.Purpose, g.FundingOwnerId, r.Found, r.NestedGroups
            FROM intg.SeatGroupReading r JOIN core.SeatGroup g ON g.SeatGroupId = r.SeatGroupId
            WHERE r.SyncRunId = @latestRun
            """, new { latestRun })).ToList();
        raised += await ApplyAsync(uow, FindingTypes.SeatGroupMissing, notify: true, readings.Where(r => !r.Found)
            .Select(r => new Hit($"{FindingTypes.SeatGroupMissing}:{r.SeatGroupId}", r.Purpose == SeatGroupPurposes.License ? "HIGH" : "MEDIUM", $"Seat group {r.DisplayName} was not found in Entra",
                "No Entra group has this name (or the recorded object ID was deleted). Check the name on the seat group and that Entra Connect syncs it.", r.FundingOwnerId, r.DisplayName)));
        raised += await ApplyAsync(uow, FindingTypes.SeatNestedGroup, notify: true, readings.Where(r => r.Found && r.NestedGroups > 0
                && r.Purpose is SeatGroupPurposes.License or SeatGroupPurposes.LobTracking)
            .Select(r => new Hit($"{FindingTypes.SeatNestedGroup}:{r.SeatGroupId}", r.Purpose == SeatGroupPurposes.License ? "HIGH" : "MEDIUM", $"Seat group {r.DisplayName} contains other groups",
                r.Purpose == SeatGroupPurposes.License
                    ? $"{r.NestedGroups} nested group(s): group-based licensing does not license members of nested groups, so they silently get no seat. Add people directly through OIM."
                    : $"{r.NestedGroups} nested group(s): members of nested groups are not counted for the LOB. Add people directly through OIM.",
                r.FundingOwnerId, r.DisplayName, "0 nested groups", $"{r.NestedGroups} nested groups")));

        // Cost center says another LOB: the seat is charged to the wrong LOB. Only when an attribution rule or override matches.
        raised += await ApplyAsync(uow, FindingTypes.SeatCostCenterMismatch, notify: false, (await uow.QueryAsync<(Guid MemberObjectId, string Name, int TrackedFo, string TrackedName, int AttributedFo, string AttributedName, string? CostCenterCode)>(
            $"""
            SELECT s.MemberObjectId, p.DisplayName AS Name, g.FundingOwnerId AS TrackedFo, tfo.Name AS TrackedName, a.FundingOwnerId AS AttributedFo, afo.Name AS AttributedName, p.CostCenterCode
            FROM rpt.fn_SeatHolders() s
            JOIN sec.Person p ON p.EntraObjectId = s.MemberObjectId
            JOIN intg.GroupMember gm ON gm.MemberObjectId = s.MemberObjectId
            JOIN {TrackingGroups} AND g.EntraGroupId = gm.GroupId
            JOIN core.FundingOwner tfo ON tfo.FundingOwnerId = g.FundingOwnerId
            CROSS APPLY (SELECT TOP (1) x.FundingOwnerId FROM (
                    SELECT o.FundingOwnerId, 0 AS Rank FROM core.AttributionOverride o
                    WHERE o.PersonId = p.PersonId AND o.EffectiveFrom <= @today AND (o.EffectiveTo IS NULL OR o.EffectiveTo >= @today)
                    UNION ALL
                    SELECT r.FundingOwnerId, r.Priority + 1 FROM core.AttributionRule r
                    WHERE r.EffectiveFrom <= @today AND (r.EffectiveTo IS NULL OR r.EffectiveTo >= @today)
                      AND (r.MatchDivision IS NULL OR r.MatchDivision = p.Division)
                      AND (r.MatchCostCenter IS NULL OR r.MatchCostCenter = p.CostCenterCode)
                      AND (r.MatchDepartment IS NULL OR r.MatchDepartment = p.Department)) x
                ORDER BY x.Rank) a
            JOIN core.FundingOwner afo ON afo.FundingOwnerId = a.FundingOwnerId
            WHERE a.FundingOwnerId <> g.FundingOwnerId
            """, new { today }))
            .Select(r => new Hit($"{FindingTypes.SeatCostCenterMismatch}:{r.MemberObjectId}:{r.TrackedFo}", "MEDIUM", $"{r.Name} is charged to {r.TrackedName} but belongs to {r.AttributedName}",
                $"The person is in {r.TrackedName}'s tracking group, while their cost center ({r.CostCenterCode ?? "none"}) maps to {r.AttributedName}. Move the OIM role to the right LOB, or record an attribution override.",
                r.TrackedFo, r.MemberObjectId.ToString(), r.AttributedName, r.TrackedName)));

        return raised;
    }

    /// <summary>Raises each hit (once while open), resolves open findings of the type that were not hit, and sends one notice per new LOB or tenant finding.</summary>
    private async Task<int> ApplyAsync(Uow uow, string type, bool notify, IEnumerable<Hit> hits)
    {
        var keys = new List<string>();
        var raised = 0;
        foreach (var h in hits)
        {
            keys.Add(h.Key);
            var id = await findings.RaiseAsync(uow, h.Key, new FindingWriter.NewFinding(type, h.Severity, h.Title, h.Details,
                FundingOwnerId: h.FundingOwnerId, ExternalId: h.ExternalId, Expected: h.Expected, Actual: h.Actual, SyncRunId: runId));
            if (id is not long fid) continue;
            raised++;
            if (!notify) continue;
            var roles = new List<RoleTarget> { new(Roles.PlatformOperator, ScopeTypes.Tenant, null) };
            if (h.FundingOwnerId is int fo) roles.Add(new RoleTarget(Roles.LobBusinessOwner, ScopeTypes.FundingOwner, fo));
            await findings.NotifyAsync(uow, "N10", fid, $"Copilot seats: {h.Title}", h.Title + ".", Array.Empty<int>(), roles, new[] { "OPERATIONS" }, h.FundingOwnerId,
                linkPath: $"{SeatSyncService.FindingsLink}?Type={type}");
        }
        await findings.ResolveMissingAsync(uow, type, keys, "The latest seat read no longer shows this.");
        return raised;
    }

    public static string LicenseErrorLabel(string error) => error switch
    {
        "CountViolation" => "not enough licenses",
        "MutuallyExclusiveViolation" => "conflicting plans",
        "DependencyViolation" => "missing base license",
        "ProhibitedInUsageLocationViolation" => "usage location",
        "UniquenessViolation" => "duplicate product",
        _ => "other license error",
    };

    public static string LicenseErrorFix(string error, string? usageLocation) => error switch
    {
        "CountViolation" => "Not enough licenses: the license group has more members than Copilot seats purchased, so new members get no seat. Buy seats or remove members; Entra retries the assignment by itself.",
        "MutuallyExclusiveViolation" => "Conflicting plans: another license the person holds has a service plan that conflicts with Copilot. Remove that license or disable the conflicting plan.",
        "DependencyViolation" => "Missing base license: the Copilot add-on needs an eligible Microsoft 365 or Office 365 license first. Assign the base license, then Entra retries.",
        "ProhibitedInUsageLocationViolation" => $"Usage location: Copilot is not available in the person's usage location ({usageLocation ?? "none set"}). Set the usage location in AD or Entra.",
        "UniquenessViolation" => "Duplicate product: two licenses for the same product conflict. Remove one of them.",
        _ => "Open the license group in the Entra admin center (Licenses, then the group's issues) for the reason.",
    };
}
