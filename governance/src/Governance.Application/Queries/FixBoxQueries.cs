using Dapper;
using Governance.Application.Data;

namespace Governance.Application.Queries;

/// <summary>
/// What a requester or sponsor needs to fix a returned request. The intake task that returned it is not visible to them
/// under row-level security, so the notes are read in system mode, for this one request, after the page has checked
/// that the viewer is its requester or sponsor.
/// </summary>
public static class FixBoxQueries
{
    public sealed record ReturnNote(string? ReturnedByName, DateTime? ReturnedUtc, string? Notes, string StageName);

    public static async Task<ReturnNote?> LatestReturnAsync(Db db, Guid requestPublicId, int viewerPersonId)
    {
        await using var conn = await db.OpenSystemAsync();
        return await conn.QueryFirstOrDefaultAsync<ReturnNote>(
            """
            SELECT TOP (1) p.DisplayName AS ReturnedByName, t.CompletedUtc AS ReturnedUtc, t.Notes, s.Name AS StageName
            FROM wf.Request r
            JOIN wf.Task t ON t.RequestId = r.RequestId AND t.Outcome = 'RETURN' AND t.Status = 'DONE'
            JOIN wf.StageDefinition s ON s.DefinitionId = r.DefinitionId AND s.StageCode = t.StageCode
            LEFT JOIN sec.Person p ON p.PersonId = t.CompletedByPersonId
            WHERE r.PublicId = @requestPublicId AND @viewerPersonId IN (r.RequesterPersonId, r.SponsorPersonId)
            ORDER BY t.CompletedUtc DESC
            """, new { requestPublicId, viewerPersonId });
    }
}
