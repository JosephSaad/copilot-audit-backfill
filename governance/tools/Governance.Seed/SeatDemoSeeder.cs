using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Governance.Domain;
using Microsoft.Data.SqlClient;

namespace Governance.Seed;

/// <summary>
/// Copilot seat demo: the client's 14 LOBs with their tracking groups and OIM roles, the shared license group,
/// the other MCO groups, reservations, and a synthetic license read-back (people, members, license states, usage)
/// so every seat page and finding has something to show. Synthetic people only (IsSynthetic = 1, @example.invalid,
/// generated object IDs that exist in no tenant). Runs once per database (setting Seed.Seats).
/// </summary>
public static class SeatDemoSeeder
{
    public const string Version = "1";
    public const string LicenseGroup = "GSGu_MCO_CopilotM365";
    public static readonly Guid CopilotSku = Guid.Parse("639dec6b-bb19-468b-871c-c5c441c4b0cb");   // Microsoft_365_Copilot
    public static readonly Guid DemoE7Sku = DeterministicGuid("demo-sku-e7");

    /// <summary>Code, LOB name, OIM role, tracking group, reserved seats, seats assigned today.</summary>
    public sealed record SeatLob(string Code, string Name, string OimRole, string Group, int Reserved, int Assigned);

    public static readonly IReadOnlyList<SeatLob> Lobs = new[]
    {
        new SeatLob("AMG", "Asset Management Group (AMG)", "Asset Management Group . M365 Copilot License . Role", "GSGu_MCO_AMGTracking", 80, 74),
        new SeatLob("CIB", "Corporate & Institutional Banking (C&IB)", "Corporate and Institutional Banking . M365 Copilot License . Role", "GSGu_MCO_CIBTracking", 150, 162),
        new SeatLob("AUDIT", "Audit", "Audit . M365 Copilot License . Role", "GSGu_MCO_AuditTracking", 30, 27),
        new SeatLob("CORPRESP", "Corporate Responsibility Group", "Corporate Responsibility . M365 Copilot License . Role", "GSGu_MCO_CorpRespTracking", 15, 12),
        new SeatLob("REGPRES", "Regional Presidents", "Regional President . M365 Copilot License . Role", "GSGu_MCO_RegPresTracking", 20, 20),
        new SeatLob("TECHSYS", "Technology", "Technology and Systems . M365 Copilot License . Role", "GSGu_MCO_TechSysTracking", 200, 205),
        new SeatLob("IRM", "Independent Risk Management (IRM)", "Independent Risk Management . M365 Copilot License . Role", "GSGu_MCO_IRMTracking", 60, 51),
        new SeatLob("RETAIL", "Retail", "Retail . M365 Copilot License . Role", "GSGu_MCO_RetailTracking", 180, 171),
        new SeatLob("HR", "Human Resources (HR)", "Human Resources . M365 Copilot License . Role", "GSGu_MCO_HRTracking", 40, 33),
        new SeatLob("FININV", "Finance Investments", "Finance Investments . M365 Copilot License . Role", "GSGu_MCO_FinInvTracking", 50, 46),
        new SeatLob("FINANCE", "Finance", "Finance . M365 Copilot License . Role", "GSGu_MCO_FinanceTracking", 90, 88),
        new SeatLob("LEGAL", "Legal", "Legal . M365 Copilot License . Role", "GSGu_MCO_LegalTracking", 35, 30),
        new SeatLob("MARCOM", "Marketing & Communications", "Marketing and Communication . M365 Copilot License . Role", "GSGu_MCO_MarComTracking", 25, 19),
        new SeatLob("BSAM", "BSAM (Balance Sheet Analytics and Modeling)", "Balance Sheet Analytics and Modeling . M365 Copilot License . Role", "GSGu_MCO_BSAMTracking", 15, 14),
    };

    /// <summary>The other MCO groups: purpose guessed from the name only, so left unconfirmed.</summary>
    private static readonly (string Name, string Purpose, bool Confirmed, string Notes)[] OtherGroups =
    {
        (LicenseGroup, SeatGroupPurposes.License, true, "Group-based licensing assigns the Copilot add-on. Confirmed by the client."),
        ("GSGu_MCO_CopilotM365Mgr", SeatGroupPurposes.Manager, false, "May be for managers or delegates; to confirm."),
        ("GSGu_MCO_ExclusionLic", SeatGroupPurposes.Exclusion, false, "Looks like a license exclusion; to confirm."),
        ("GSGu_MCO_FinExclude", SeatGroupPurposes.Exclusion, false, "Finance exclusion, by its name; to confirm."),
        ("GSGu_MCO_EmpRelExclude", SeatGroupPurposes.Exclusion, false, "Employee Relations exclusion, by its name; to confirm."),
        ("GSGu_MCO_ChatOnly", SeatGroupPurposes.ChatOnly, false, "Probably the Copilot Chat population without the paid license; to confirm."),
    };

    private static readonly string[] First = { "Ava", "Ben", "Carla", "Dev", "Elif", "Farid", "Gemma", "Hugo", "Ines", "Jamal", "Keiko", "Lars", "Mira", "Nikhil", "Orla", "Pavel", "Quinn", "Rosa", "Sami", "Tara", "Umar", "Vera", "Wes", "Xin", "Yara", "Zoltan", "Anouk", "Bruno", "Celine", "Diego", "Esme", "Femi" };
    private static readonly string[] Last = { "Abbott", "Barros", "Castillo", "Duarte", "Eklund", "Ferreira", "Gallagher", "Hassan", "Ibarra", "Jansen", "Kowalski", "Lambert", "Moreno", "Nakamura", "Osei", "Pereira", "Quigley", "Rossi", "Sato", "Thorne", "Ueda", "Varga", "Whitfield", "Yilmaz", "Zimmer", "Achterberg", "Beaumont", "Carvalho", "Delacroix", "Esposito", "Fontaine", "Gutierrez", "Holloway" };

    public static Guid DeterministicGuid(string seed) => new(MD5.HashData(Encoding.UTF8.GetBytes(seed)));

    private sealed record Seat(string Key, string Name, string Upn, Guid Oid, string? Lob, string? SecondLob, bool Licensed, bool Direct, string State, string? Error,
        bool Enabled, bool Excluded, bool E7, DateTime? LastActivity);

    public static async Task SeedAsync(SqlConnection conn, SeedResult result, DateTime todayUtc, TextWriter log)
    {
        var today = todayUtc.Date;
        if (await conn.ExecuteScalarAsync<string?>("SELECT SettingValue FROM ref.Setting WHERE SettingKey = 'Seed.Seats'") is not null)
        {
            await LoadAsync(conn, result);
            return;
        }
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        var po = result.People.TryGetValue("platformOwner", out var poId) ? poId : (int?)null;
        var finops = result.People.TryGetValue("finopsLead", out var fl) ? fl : (int?)null;
        var month0 = new DateTime(today.Year, today.Month, 1);
        var start = month0.AddMonths(-6);

        // 1. Funding owners (Retail reuses the existing Retail funding owner), owners and seat admins.
        var n = 0;
        foreach (var l in Lobs)
        {
            n++;
            var existing = await conn.ExecuteScalarAsync<int?>("SELECT FundingOwnerId FROM core.FundingOwner WHERE Code = @Code", new { l.Code }, tx);
            int fo;
            if (existing is int e) fo = e;
            else
            {
                var owner = await PersonAsync(conn, tx, result, $"{l.Code}.owner", Name(n * 7 + 1), $"Business owner, {l.Name}", l.Name);
                fo = await conn.ExecuteScalarAsync<int>(
                    """
                    INSERT core.FundingOwner (Code, Name, OwnerType, BusinessOwnerPersonId, FinanceNode, Status, NotificationRecipientsConfirmed, AttributionTested, EffectiveFrom)
                    OUTPUT inserted.FundingOwnerId VALUES (@Code, @Name, 'LOB', @owner, @node, 'ACTIVE', 1, 1, @from)
                    """, new { l.Code, l.Name, owner, node = "FIN-" + l.Code, from = start }, tx);
                await conn.ExecuteAsync(
                    """
                    INSERT sec.RoleAssignment (PersonId, RoleCode, ScopeType, ScopeId, EffectiveFrom, Source, AcceptedUtc, ReviewDueOn, GrantedByPersonId)
                    VALUES (@owner, 'LOB_BUSINESS_OWNER', 'FUNDING_OWNER', @fo, @from, 'SEED', SYSUTCDATETIME(), @review, @po)
                    """, new { owner, fo, from = today.AddDays(-60), review = today.AddYears(1), po }, tx);
            }
            result.FundingOwners[l.Code] = fo;
            var admin = await PersonAsync(conn, tx, result, $"{l.Code}.seatadmin", Name(n * 7 + 3), $"LOB seat admin, {l.Name}", l.Name);
            await conn.ExecuteAsync(
                """
                INSERT sec.RoleAssignment (PersonId, RoleCode, ScopeType, ScopeId, EffectiveFrom, Source, AcceptedUtc, ReviewDueOn, GrantedByPersonId)
                VALUES (@admin, 'LOB_SEAT_ADMIN', 'FUNDING_OWNER', @fo, @from, 'SEED', SYSUTCDATETIME(), @review, @po)
                """, new { admin, fo, from = today.AddDays(-60), review = today.AddYears(1), po }, tx);
        }

        // 2. SKUs and groups.
        await conn.ExecuteAsync(
            """
            INSERT core.SeatSku (SkuId, SkuPartNumber, Name, Kind, Notes) VALUES
              (@CopilotSku, 'Microsoft_365_Copilot', N'Microsoft 365 Copilot', 'COPILOT_ADDON', N'The paid per-user Copilot seat reserved per LOB.'),
              (@DemoE7Sku, 'DEMO_E7', N'Microsoft 365 E7 (demo)', 'INCLUDES_COPILOT', N'Demo value: replace with the tenant''s E7 SKU ID if E7 is bought.');
            """, new { CopilotSku, DemoE7Sku }, tx);
        var groupIds = new Dictionary<string, (int SeatGroupId, Guid EntraId)>();
        foreach (var (name, purpose, confirmed, notes) in OtherGroups)
            groupIds[name] = await GroupAsync(conn, tx, name, purpose, null, null, confirmed, notes, start);
        foreach (var l in Lobs)
            groupIds[l.Group] = await GroupAsync(conn, tx, l.Group, SeatGroupPurposes.LobTracking, result.FundingOwners[l.Code], l.OimRole, true, "Provisioned by ServiceNow from the OIM role.", start);

        // 3. Reservations: first reservation six months ago; C&IB increased three months ago (130 to 150).
        foreach (var l in Lobs)
        {
            var fo = result.FundingOwners[l.Code];
            if (l.Code == "CIB")
            {
                var inc = month0.AddMonths(-3);
                await conn.ExecuteAsync(
                    """
                    INSERT core.SeatReservation (FundingOwnerId, Seats, ChangeKind, EffectiveFrom, EffectiveTo, ApprovedByPersonId, Notes, CreatedByPersonId)
                    VALUES (@fo, 130, 'INITIAL', @start, @end, @finops, N'Reservation schedule FY start (demo).', @finops);
                    INSERT core.SeatReservation (FundingOwnerId, Seats, PreviousSeats, ChangeKind, EffectiveFrom, ApprovedByPersonId, FlexPoolReview, CreatedByPersonId)
                    VALUES (@fo, 150, 130, 'INCREASE', @inc, @finops, N'180 seats unallocated, 25 buffer, no pending demand: increase of 20 fits (demo).', @finops);
                    """, new { fo, start, end = inc.AddDays(-1), inc, finops }, tx);
            }
            else
                await conn.ExecuteAsync(
                    """
                    INSERT core.SeatReservation (FundingOwnerId, Seats, ChangeKind, EffectiveFrom, ApprovedByPersonId, Notes, CreatedByPersonId)
                    VALUES (@fo, @Reserved, 'INITIAL', @start, @finops, N'Reservation schedule FY start (demo).', @finops)
                    """, new { fo, l.Reserved, start, finops }, tx);
        }

        // 4. Synthetic seat holders and the read-back.
        var seats = new List<Seat>();
        var k = 0;
        Seat Make(string? lob, bool licensed = true, bool direct = false, string state = "Active", string? error = null, bool enabled = true,
            bool excluded = false, bool e7 = false, int? inactiveDays = null, string? second = null)
        {
            k++;
            var name = Name(k * 13 + 5);
            var upn = $"seat.demo.{k:0000}@example.invalid";
            var last = inactiveDays is int d ? (d < 0 ? (DateTime?)null : today.AddDays(-d)) : today.AddDays(-(k % 20) - 3);
            return new Seat($"seat{k}", name, upn, DeterministicGuid("seat-demo-" + k), lob, second, licensed, direct, state, error, enabled, excluded, e7, last);
        }
        foreach (var l in Lobs)
            for (var i = 0; i < l.Assigned; i++)
            {
                // About one holder in eight is inactive (no use in 60+ days, or none reported).
                int? inactive = i % 8 == 7 ? (i % 16 == 15 ? -1 : 75 + i % 40) : null;
                seats.Add(Make(l.Code, inactiveDays: inactive,
                    enabled: !(l.Code == "LEGAL" && i < 2),
                    excluded: l.Code == "HR" && i < 2,
                    e7: l.Code == "FINANCE" && i < 2,
                    second: l.Code == "AMG" && i < 3 ? "CIB" : null));
            }
        foreach (var l in Lobs.Take(5))
            for (var i = 0; i < 2; i++) seats.Add(Make(l.Code, licensed: false, state: "", inactiveDays: -1));
        seats.Add(Make("TECHSYS", state: "Error", error: "CountViolation", inactiveDays: -1));
        seats.Add(Make("TECHSYS", state: "Error", error: "CountViolation", inactiveDays: -1));
        seats.Add(Make("RETAIL", state: "Error", error: "ProhibitedInUsageLocationViolation", inactiveDays: -1));
        seats.Add(Make("IRM", state: "ActiveWithError", error: "MutuallyExclusiveViolation"));
        for (var i = 0; i < 6; i++) seats.Add(Make(null, direct: i < 2));

        var personRows = seats.Select(s => new { s.Name, s.Upn, s.Oid, s.Enabled, Dept = s.Lob is null ? "Unassigned" : Lobs.First(x => x.Code == s.Lob).Name }).ToList();
        await conn.ExecuteAsync(
            """
            INSERT sec.Person (EntraObjectId, UserPrincipalName, DisplayName, Mail, JobTitle, Department, AccountEnabled, LeaveDate, IsSynthetic)
            SELECT j.Oid, j.Upn, j.Name, j.Upn, N'Employee (seat demo)', j.Dept, j.Enabled, CASE WHEN j.Enabled = 0 THEN DATEADD(DAY, -20, @today) END, 1
            FROM OPENJSON(@json) WITH (Name nvarchar(256), Upn nvarchar(256), Oid uniqueidentifier, Enabled bit, Dept nvarchar(200)) j
            """, new { json = JsonSerializer.Serialize(personRows), today }, tx);

        var runId = await conn.ExecuteScalarAsync<long>(
            """
            INSERT intg.SyncRun (Integration, Mode, StartedUtc, FinishedUtc, Status, ItemsRead, FindingsRaised, Message)
            OUTPUT inserted.SyncRunId
            VALUES ('X13', 'TEST_DOUBLE', SYSUTCDATETIME(), SYSUTCDATETIME(), 'SUCCEEDED', @items, 0, N'Demo seed: synthetic license read-back; nothing was read from Microsoft.')
            """, new { items = seats.Count }, tx);

        var members = new List<(Guid G, Guid M)>();
        foreach (var s in seats)
        {
            if (s.Lob is string lob) members.Add((groupIds[Lobs.First(x => x.Code == lob).Group].EntraId, s.Oid));
            if (s.SecondLob is string lob2) members.Add((groupIds[Lobs.First(x => x.Code == lob2).Group].EntraId, s.Oid));
            if (s.Licensed && !s.Direct) members.Add((groupIds[LicenseGroup].EntraId, s.Oid));
            if (s.Excluded) members.Add((groupIds["GSGu_MCO_ExclusionLic"].EntraId, s.Oid));
        }
        await conn.ExecuteAsync(
            """
            INSERT intg.GroupMember (GroupId, MemberObjectId, LastSeenRunId)
            SELECT DISTINCT j.G, j.M, @runId FROM OPENJSON(@json) WITH (G uniqueidentifier, M uniqueidentifier) j
            WHERE NOT EXISTS (SELECT 1 FROM intg.GroupMember x WHERE x.GroupId = j.G AND x.MemberObjectId = j.M)
            """, new { json = JsonSerializer.Serialize(members.Select(x => new { x.G, x.M })), runId }, tx);

        var licenseGroup = groupIds[LicenseGroup].EntraId;
        var holders = seats.Where(s => s.Licensed).Select(s => new
        {
            M = s.Oid, Sku = CopilotSku, G = s.Direct ? Guid.Empty : licenseGroup, s.State, s.Error, s.Upn, Loc = s.Error == "ProhibitedInUsageLocationViolation" ? null : "CA",
        }).Concat(seats.Where(s => s.E7).Select(s => new { M = s.Oid, Sku = DemoE7Sku, G = Guid.Empty, State = "Active", Error = (string?)null, s.Upn, Loc = (string?)"CA" })).ToList();
        await conn.ExecuteAsync(
            """
            INSERT intg.SeatHolder (MemberObjectId, SkuId, AssignedByGroupId, State, Error, UsageLocation, UserPrincipalName, StateUpdatedUtc, LastSeenRunId)
            SELECT j.M, j.Sku, j.G, j.State, j.Error, j.Loc, j.Upn, SYSUTCDATETIME(), @runId
            FROM OPENJSON(@json) WITH (M uniqueidentifier, Sku uniqueidentifier, G uniqueidentifier, State varchar(30), Error varchar(50), Loc varchar(10), Upn nvarchar(256)) j
            """, new { json = JsonSerializer.Serialize(holders), runId }, tx);

        var activity = seats.Where(s => s.Licensed).Select(s => new { s.Upn, Last = s.LastActivity?.ToString("yyyy-MM-dd") }).ToList();
        await conn.ExecuteAsync(
            """
            INSERT intg.CopilotActivity (UserPrincipalName, LastActivityDate, ReportRefreshDate, LastSeenRunId)
            SELECT j.Upn, j.Last, @refresh, @runId FROM OPENJSON(@json) WITH (Upn nvarchar(256), Last date) j
            """, new { json = JsonSerializer.Serialize(activity), refresh = today.AddDays(-2), runId }, tx);

        var active = seats.Count(s => s.Licensed && s.State is "Active" or "ActiveWithError");
        await conn.ExecuteAsync(
            """
            INSERT intg.SeatSkuReading (SyncRunId, SkuId, SkuPartNumber, EnabledUnits, WarningUnits, SuspendedUnits, ConsumedUnits) VALUES
              (@runId, @CopilotSku, 'Microsoft_365_Copilot', 1200, 0, 0, @active),
              (@runId, @DemoE7Sku, 'DEMO_E7', 50, 0, 0, 2);
            """, new { runId, CopilotSku, DemoE7Sku, active }, tx);
        foreach (var (name, (id, entra)) in groupIds)
        {
            var count = members.Where(m => m.G == entra).Select(m => m.M).Distinct().Count();
            await conn.ExecuteAsync(
                """
                INSERT intg.SeatGroupReading (SyncRunId, SeatGroupId, EntraGroupId, Found, OnPremisesSynced, UserMembers, NestedGroups, LicensedSkuIds)
                VALUES (@runId, @id, @entra, 1, 1, @count, @nested, @skus)
                """, new { runId, id, entra, count, nested = name == "GSGu_MCO_ChatOnly" ? 1 : 0, skus = name == LicenseGroup ? CopilotSku.ToString() : null }, tx);
        }

        // 5. Monthly snapshots: five certified months and last month as a draft.
        var rate = 30.00m;
        for (var m = 6; m >= 1; m--)
        {
            var month = month0.AddMonths(-m);
            var certified = m >= 2;
            foreach (var l in Lobs)
            {
                var reserved = l.Code == "CIB" && month < month0.AddMonths(-3) ? 130 : l.Reserved;
                var assigned = (int)Math.Round(l.Assigned * (0.84 + 0.03 * (6 - m)));
                var over = Math.Max(0, assigned - reserved);
                await conn.ExecuteAsync(
                    """
                    INSERT core.SeatChargeSnapshot (PeriodMonth, FundingOwnerId, ReservedSeats, AssignedSeats, OverSeats, ChargeSeats, RatePerSeat, Currency, Amount, Status, SyncRunId, TakenUtc, CertifiedByPersonId, CertifiedUtc)
                    VALUES (@month, @fo, @reserved, @assigned, @over, @charge, @rate, 'USD', @amount, @status, @runId, @taken, @by, @certUtc)
                    """, new
                    {
                        month, fo = result.FundingOwners[l.Code], reserved, assigned, over, charge = reserved + over, rate, amount = (reserved + over) * rate,
                        status = certified ? "CERTIFIED" : "DRAFT", runId, taken = month.AddMonths(1).AddHours(6), by = certified ? finops : null,
                        certUtc = certified ? month.AddMonths(1).AddDays(5) : (DateTime?)null,
                    }, tx);
            }
            await conn.ExecuteAsync(
                """
                INSERT core.SeatChargeSnapshot (PeriodMonth, FundingOwnerId, ReservedSeats, AssignedSeats, OverSeats, ChargeSeats, RatePerSeat, Currency, Amount, Status, SyncRunId, TakenUtc, CertifiedByPersonId, CertifiedUtc)
                VALUES (@month, NULL, 0, @nolob, @nolob, @nolob, @rate, 'USD', 0, @status, @runId, @taken, @by, @certUtc)
                """, new { month, nolob = m % 3 + 4, rate, status = certified ? "CERTIFIED" : "DRAFT", runId, taken = month.AddMonths(1).AddHours(6), by = certified ? finops : null, certUtc = certified ? month.AddMonths(1).AddDays(5) : (DateTime?)null }, tx);
        }

        // 6. Reclaim proposals for some inactive holders, and one finding of each seat type.
        foreach (var code in new[] { "AMG", "RETAIL", "TECHSYS", "CIB" })
            foreach (var s in seats.Where(x => x.Lob == code && x.Licensed && x.State == "Active" && x.Enabled && (x.LastActivity is null || x.LastActivity < today.AddDays(-60))).Take(4))
                await conn.ExecuteAsync(
                    """
                    INSERT core.SeatReclaim (FundingOwnerId, PersonId, LastActivityDate, InactiveDays, Status)
                    SELECT @fo, PersonId, @last, @days, 'PROPOSED' FROM sec.Person WHERE EntraObjectId = @Oid
                    """, new { fo = result.FundingOwners[code], last = s.LastActivity, days = s.LastActivity is DateTime d ? (int)(today - d).TotalDays : (int?)null, s.Oid }, tx);

        var findings = new (string Type, string Severity, string? Lob, string Title, string Details, string? Expected, string? Actual)[]
        {
            (FindingTypes.SeatOverReservation, "MEDIUM", "CIB", "C&IB holds more seats than it reserved", "Seats above the reservation are charged on top of it (FIN-06). Ask for an increase, or reclaim unused seats.", "150 seats", "162 seats"),
            (FindingTypes.SeatOverReservation, "MEDIUM", "TECHSYS", "Technology holds more seats than it reserved", "Seats above the reservation are charged on top of it (FIN-06).", "200 seats", "205 seats"),
            (FindingTypes.SeatNoLob, "MEDIUM", null, "6 people hold a Copilot seat but are in no LOB tracking group", "Nobody is charged for these seats. Request the right LOB's OIM role, or remove the seat.", "0", "6"),
            (FindingTypes.SeatTwoLobs, "MEDIUM", "AMG", "3 people are in two LOB tracking groups (AMG and C&IB)", "Each seat is counted by both LOBs. Remove the person from one tracking group.", "1 LOB per person", "2 LOBs"),
            (FindingTypes.SeatTrackedNoLicense, "LOW", "AMG", "2 people in the AMG tracking group have no seat", "The OIM role may be half provisioned: they are in the tracking group but not in the license group.", null, null),
            (FindingTypes.SeatDirectAssignment, "MEDIUM", null, "2 Copilot seats are assigned directly, not through the license group", "Direct assignments bypass the OIM role and the LOB tracking. Move them to the license group.", "license group", "direct"),
            (FindingTypes.SeatLicenseError, "HIGH", "TECHSYS", "2 Technology seats failed: not enough licenses", "Group-based licensing could not assign the add-on (CountViolation). Buy or free seats.", "Active", "CountViolation"),
            (FindingTypes.SeatLicenseError, "HIGH", "RETAIL", "1 Retail seat failed: usage location not set", "The person has no usage location, so the license cannot be assigned (ProhibitedInUsageLocationViolation).", "Active", "ProhibitedInUsageLocationViolation"),
            (FindingTypes.SeatLeaver, "HIGH", "LEGAL", "2 disabled accounts in Legal still hold a seat", "Leavers keep a paid seat until their OIM role is removed.", null, null),
            (FindingTypes.SeatExcludedHolder, "MEDIUM", "HR", "2 HR seat holders are in GSGu_MCO_ExclusionLic", "Shown for the demo; raised only once the exclusion group's purpose is confirmed.", null, null),
            (FindingTypes.SeatPaidTwice, "MEDIUM", "FINANCE", "2 Finance people have E7 and the Copilot add-on", "E7 already includes Copilot: the add-on seat is paid twice.", null, null),
            (FindingTypes.SeatNestedGroup, "MEDIUM", null, "GSGu_MCO_ChatOnly contains a nested group", "Group-based licensing ignores members of nested groups.", "0 nested groups", "1"),
            (FindingTypes.SeatReservationsOverInventory, "LOW", null, "Reservations and buffer are close to the purchased seats", "990 reserved plus 25 buffer against 1,200 purchased: 185 seats free to reserve.", null, null),
        };
        var fi = 0;
        foreach (var f in findings)
            await conn.ExecuteAsync(
                """
                INSERT ops.Finding (FindingType, Severity, FundingOwnerId, Title, Details, Expected, Actual, Status, DedupeKey, SyncRunId)
                VALUES (@Type, @Severity, @fo, @Title, @Details, @Expected, @Actual, 'OPEN', @key, @runId)
                """, new { f.Type, f.Severity, fo = f.Lob is null ? (int?)null : result.FundingOwners[f.Lob], f.Title, f.Details, f.Expected, f.Actual, key = $"DEMO:SEAT:{++fi}", runId }, tx);

        await conn.ExecuteAsync(
            "INSERT ref.Setting (SettingKey, SettingValue, Description) VALUES ('Seed.Seats', @Version, N'Copilot seat demo applied.')", new { Version }, tx);
        await tx.CommitAsync();
        log.WriteLine($"Copilot seats: {Lobs.Count} LOBs, {seats.Count} synthetic people, 6 monthly snapshots.");
    }

    private static async Task LoadAsync(SqlConnection conn, SeedResult result)
    {
        foreach (var (id, code) in await conn.QueryAsync<(int, string)>("SELECT FundingOwnerId, Code FROM core.FundingOwner")) result.FundingOwners[code] = id;
        foreach (var (id, mail) in await conn.QueryAsync<(int, string)>("SELECT PersonId, Mail FROM sec.Person WHERE IsSynthetic = 1 AND Mail LIKE 'seatlob.%'"))
            result.People[KeyFromMail(mail)] = id;
    }

    private static string MailFor(string key) => "seatlob." + key.ToLowerInvariant() + "@example.invalid";
    private static string KeyFromMail(string mail)
    {
        var k = mail["seatlob.".Length..mail.IndexOf('@')];
        var dot = k.LastIndexOf('.');
        return k[..dot].ToUpperInvariant() + k[dot..];
    }

    private static async Task<int> PersonAsync(SqlConnection conn, SqlTransaction tx, SeedResult result, string key, string name, string title, string dept)
    {
        var mail = MailFor(key);
        var id = await conn.ExecuteScalarAsync<int>(
            """
            INSERT sec.Person (DisplayName, UserPrincipalName, Mail, JobTitle, Department, AccountEnabled, IsSynthetic)
            OUTPUT inserted.PersonId VALUES (@name, @mail, @mail, @title, @dept, 1, 1)
            """, new { name, mail, title, dept }, tx);
        result.People[key] = id;
        return id;
    }

    private static async Task<(int, Guid)> GroupAsync(SqlConnection conn, SqlTransaction tx, string name, string purpose, int? fo, string? oim, bool confirmed, string notes, DateTime from)
    {
        var entra = DeterministicGuid("seat-group-" + name);
        var id = await conn.ExecuteScalarAsync<int>(
            """
            INSERT core.SeatGroup (EntraGroupId, DisplayName, Purpose, FundingOwnerId, OimRoleName, Source, PurposeConfirmed, Notes, EffectiveFrom)
            OUTPUT inserted.SeatGroupId VALUES (@entra, @name, @purpose, @fo, @oim, 'ON_PREM_AD', @confirmed, @notes, @from)
            """, new { entra, name, purpose, fo, oim, confirmed, notes, from }, tx);
        return (id, entra);
    }

    private static string Name(int i) => $"{First[i % First.Length]} {Last[(i / First.Length + i) % Last.Length]}";
}
