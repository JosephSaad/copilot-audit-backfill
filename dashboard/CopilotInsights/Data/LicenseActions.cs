using System.Net;
using System.Text.Json;
using CopilotInsights.Infrastructure;

namespace CopilotInsights.Data;

/// <summary>
/// The license changes the dashboard exported for an administrator to apply (upgrade, revoke,
/// leaver, rollout wave), kept so Forecasting › License action outcomes can show whether they
/// were applied and what followed.
/// </summary>
public static class LicenseActions
{
    public static readonly string[] Kinds = ["upgrade", "revoke", "leaver", "wave", "nonhuman"];

    /// <summary>
    /// Records an export's people: all of them, or none and the export fails. They were inserted a thousand at a
    /// time, each on its own, and any database error was let go: a timeout on the third thousand gave out the whole file
    /// with two thousand recorded, and a failed insert gave it out recorded nowhere, with nothing to say so. Now one
    /// statement, and only a database from before 2.3.0's operations schema (no table) goes on without the record. A
    /// person already recorded for the same change that day is not recorded again: each repeat of an export added the
    /// whole list (three downloads, 899 rows for 303 people), which License action outcomes counts once a day anyway.
    /// </summary>
    public static async Task RecordAsync(Db db, string kind, IReadOnlyCollection<string> upns, string by)
    {
        if (upns.Count == 0 || !Kinds.Contains(kind)) return;
        try
        {
            await db.ExecuteAsync(@"
INSERT INTO dash.LicenseAction (Kind, Upn, ExportedBy)
SELECT DISTINCT @kind, j.Upn, @by FROM OPENJSON(@json) WITH (Upn NVARCHAR(256) '$') AS j
WHERE NOT EXISTS (SELECT 1 FROM dash.LicenseAction AS la
                  WHERE la.Kind = @kind AND la.Upn = j.Upn AND la.ExportedUtc >= CAST(CAST(SYSUTCDATETIME() AS DATE) AS DATETIME2(0)));",
                new { kind, by, json = JsonSerializer.Serialize(upns) }, timeout: 300);
        }
        catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 208) { /* no dash.LicenseAction: the export works without it */ }
    }

    /// <summary>
    /// The button that makes an export: a form that posts, with the anti-forgery token, to the page's handler at the
    /// part of the organization shown. An export records who it names, so it was never meant to be a link: any page or email
    /// a Licensing role holder opened could fetch one and record an export in their name, with an audit entry saying they
    /// made it. <paramref name="extra"/> names the handler and the export's own choices ("kind=revoke&amp;handler=bulk"), and
    /// <paramref name="tenantQuery"/> is the page's TenantQuery: with several tenants a post runs only in the tenant its address names.
    /// </summary>
    public static string Button(ReportContext c, string path, string extra, string token, string tenantQuery, string text) =>
        $"<form class=\"inline\" method=\"post\" action=\"{WebUtility.HtmlEncode(c.Url(path, extra: extra) + tenantQuery)}\">{token}" +
        $"<button class=\"btn\" type=\"submit\">{WebUtility.HtmlEncode(text)}</button></form>";
}
