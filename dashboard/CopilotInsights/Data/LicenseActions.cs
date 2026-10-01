using System.Text.Json;
using CopilotInsights.Infrastructure;

namespace CopilotInsights.Data;

/// <summary>
/// The license changes the dashboard exported for an administrator to apply (upgrade, revoke,
/// leaver, rollout wave), kept so Forecasting > License action outcomes can show whether they
/// were applied and what followed.
/// </summary>
public static class LicenseActions
{
    public static readonly string[] Kinds = ["upgrade", "revoke", "leaver", "wave", "nonhuman"];

    public static async Task RecordAsync(Db db, string kind, IReadOnlyCollection<string> upns, string by)
    {
        if (upns.Count == 0 || !Kinds.Contains(kind)) return;
        try
        {
            foreach (var chunk in upns.Chunk(1000))
                await db.ExecuteAsync(@"
INSERT INTO dash.LicenseAction (Kind, Upn, ExportedBy)
SELECT @kind, j.Upn, @by FROM OPENJSON(@json) WITH (Upn NVARCHAR(256) '$') AS j;", new { kind, by, json = JsonSerializer.Serialize(chunk) });
        }
        catch (Microsoft.Data.SqlClient.SqlException) { /* dash.LicenseAction arrives with 2.3.0's operations schema; the export still works without it. */ }
    }
}
