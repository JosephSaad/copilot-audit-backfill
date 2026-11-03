using System.Security.Cryptography;
using Dapper;
using Governance.Application.Data;

namespace Governance.Web.Infrastructure;

/// <summary>A row of Insights' reservation register in effect today, its rows for one Org ID added up.</summary>
public sealed record InsightsReservation(string OrgId, string Name, string? CostCenter, int Reserved, DateTime From);

/// <summary>A funding owner the import can give a first reservation, and whether it already has one.</summary>
public sealed record ImportOwner(Guid PublicId, string Name, string Code, bool Reserved);

/// <summary>One first reservation to record: the register's rows an administrator gave this funding owner.</summary>
public sealed record ImportItem(Guid FundingOwner, string Name, int Seats, DateTime From, string Notes, IReadOnlyList<InsightsReservation> Rows);

/// <summary>
/// Workbench settings › Seat groups › "Start from Insights' reservations": the reservation register Copilot Insights kept
/// (dash.Reservation) offered once as each line of business's first reservation. Nothing is copied until an
/// administrator chooses a funding owner for a row and confirms; a funding owner that already has a reservation is
/// skipped, as the Workbench allows a first reservation once. The register itself is only read.
/// </summary>
public sealed class ReservationImport(CopilotInsights.Infrastructure.Db insights, Db governance)
{
    /// <summary>The register's rows in effect today; none when the register is not installed.</summary>
    public async Task<IReadOnlyList<InsightsReservation>> RowsAsync()
    {
        if (await insights.ScalarAsync<int>("SELECT CASE WHEN OBJECT_ID(N'dash.Reservation', N'U') IS NULL THEN 0 ELSE 1 END;") == 0) return [];
        return await insights.QueryAsync<InsightsReservation>(@"
SELECT r.OrgId, MAX(r.Name) AS Name, MAX(r.CostCenter) AS CostCenter, SUM(r.Reserved) AS Reserved, MIN(r.EffectiveFrom) AS [From]
FROM dash.Reservation AS r
WHERE r.EffectiveFrom <= CAST(GETDATE() AS DATE) AND (r.EffectiveTo IS NULL OR r.EffectiveTo >= CAST(GETDATE() AS DATE))
GROUP BY r.OrgId
ORDER BY MAX(r.Name);");
    }

    /// <summary>The funding owners in use, and which already have a seat reservation.</summary>
    public async Task<IReadOnlyList<ImportOwner>> OwnersAsync()
    {
        await using var c = await governance.OpenSystemAsync();
        return (await c.QueryAsync<ImportOwner>(
            """
            SELECT fo.PublicId, fo.Name, fo.Code,
                   CAST(CASE WHEN EXISTS (SELECT 1 FROM core.SeatReservation r WHERE r.FundingOwnerId = fo.FundingOwnerId) THEN 1 ELSE 0 END AS bit) AS Reserved
            FROM core.FundingOwner fo WHERE fo.Status <> 'RETIRED' ORDER BY fo.Name
            """)).ToList();
    }

    /// <summary>The funding owner a row most likely belongs to: the same code as its Org ID, else the same name.</summary>
    public static Guid? Suggest(InsightsReservation row, IEnumerable<ImportOwner> owners)
    {
        var list = owners.ToList();
        return (list.FirstOrDefault(o => string.Equals(o.Code, row.OrgId.Trim(), StringComparison.OrdinalIgnoreCase))
                ?? list.FirstOrDefault(o => string.Equals(o.Name, row.Name.Trim(), StringComparison.OrdinalIgnoreCase)))?.PublicId;
    }

    /// <summary>
    /// What a confirm records, and what it leaves out and why. Rows given the same funding owner are added together,
    /// from the earliest start. A funding owner that already has a reservation is skipped and said. Pure, for the tests.
    /// </summary>
    public static (List<ImportItem> Record, List<string> Skipped) Plan(IEnumerable<InsightsReservation> rows, IReadOnlyDictionary<string, Guid> chosen, IEnumerable<ImportOwner> owners)
    {
        var byId = owners.ToDictionary(o => o.PublicId);
        var record = new List<ImportItem>();
        var skipped = new List<string>();
        var groups = new Dictionary<Guid, List<InsightsReservation>>();
        foreach (var r in rows)
        {
            if (!chosen.TryGetValue(r.OrgId, out var g)) continue;   // left out: no funding owner chosen
            if (!byId.TryGetValue(g, out var o)) { skipped.Add($"{r.Name} ({r.OrgId}): the funding owner chosen is no longer in use."); continue; }
            if (o.Reserved) { skipped.Add($"{r.Name} ({r.OrgId}): {o.Name} already has a seat reservation, so it is left as it is. Change it with a seat reservation request."); continue; }
            if (!groups.TryGetValue(g, out var list)) groups[g] = list = [];
            list.Add(r);
        }
        foreach (var (g, list) in groups)
        {
            var o = byId[g];
            var notes = "Started from Copilot Insights' reservation register: " +
                        string.Join("; ", list.Select(r => $"{r.Name} ({r.OrgId}{(r.CostCenter is null ? "" : ", cost center " + r.CostCenter)}), {r.Reserved:#,0} from {r.From:yyyy-MM-dd}"));
            record.Add(new ImportItem(g, o.Name, list.Sum(r => r.Reserved), list.Min(r => r.From), notes.Length > 1000 ? notes[..1000] : notes, list));
        }
        return (record, skipped);
    }

    /// <summary>
    /// The idempotency key of one funding owner's first reservation in one confirm: a second send of the same form
    /// records nothing twice, and each funding owner in it keeps its own key.
    /// </summary>
    public static Guid Key(Guid form, Guid fundingOwner)
    {
        Span<byte> both = stackalloc byte[32];
        form.TryWriteBytes(both[..16]);
        fundingOwner.TryWriteBytes(both[16..]);
        return new Guid(SHA256.HashData(both)[..16]);
    }
}
