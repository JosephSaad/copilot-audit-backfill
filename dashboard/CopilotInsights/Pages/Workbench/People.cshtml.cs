using Governance.Application.Commands;
using Governance.Application.Data;
using Governance.Web.Infrastructure;
using Microsoft.AspNetCore.Mvc;

namespace Governance.Web.Pages;

/// <summary>
/// The person pickers' "Find someone in the directory": anyone in the directory Copilot Insights loads from AD each
/// night, found by name or sign-in, and recorded in the Workbench when chosen, so they can be named to a role before
/// they ever open the Workbench. The search and the record are only for people who name others to roles
/// (<see cref="RegistryCommands.MayAddPeople"/>); each person recorded is in the audit trail.
/// </summary>
public class PeopleModel(RegistryCommands commands, CopilotInsights.Infrastructure.Db insights) : GovPage
{
    /// <summary>A directory entry as the picker shows it.</summary>
    public sealed record Found(string Upn, string? DisplayName, string? Department, string? JobTitle, string? Mail, Guid? ObjectId);

    /// <summary>
    /// The enabled people whose name has the words typed, or whose sign-in starts with them; 15 at most, by name. The
    /// object ID only where the license load recorded it (3.0.1 and later). Mail and job title from the directory table.
    /// </summary>
    public const string FindSql = """
        IF COL_LENGTH(N'dash.Person', N'ObjectId') IS NULL
            SELECT TOP (15) p.Upn, p.DisplayName, p.Department, d.JobTitle, d.Mail, CAST(NULL AS uniqueidentifier) AS ObjectId
            FROM dash.Person AS p OUTER APPLY (SELECT TOP (1) u.JobTitle, u.Mail FROM audit.UserDirectory AS u WHERE u.UserPrincipalName = p.Upn) AS d
            WHERE ISNULL(p.AccountEnabled, 1) = 1 AND (p.DisplayName LIKE @like OR p.Upn LIKE @prefix) ORDER BY p.DisplayName, p.Upn;
        ELSE EXEC sp_executesql N'
            SELECT TOP (15) p.Upn, p.DisplayName, p.Department, d.JobTitle, d.Mail, p.ObjectId
            FROM dash.Person AS p OUTER APPLY (SELECT TOP (1) u.JobTitle, u.Mail FROM audit.UserDirectory AS u WHERE u.UserPrincipalName = p.Upn) AS d
            WHERE ISNULL(p.AccountEnabled, 1) = 1 AND (p.DisplayName LIKE @like OR p.Upn LIKE @prefix) ORDER BY p.DisplayName, p.Upn;',
            N'@like nvarchar(300), @prefix nvarchar(300)', @like = @like, @prefix = @prefix;
        """;

    /// <summary>The words typed as LIKE patterns: every word in the name, in any order; the sign-in from its start. Pure, for the tests.</summary>
    public static (string Like, string Prefix)? Patterns(string? q)
    {
        var t = (q ?? "").Trim();
        if (t.Length < 2 || t.Length > 100) return null;
        static string Esc(string s) => s.Replace("[", "[[]", StringComparison.Ordinal).Replace("%", "[%]", StringComparison.Ordinal).Replace("_", "[_]", StringComparison.Ordinal);
        var words = t.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(Esc);
        return ("%" + string.Join("%", words) + "%", Esc(t) + "%");
    }

    public IActionResult OnGet() => NotFound();

    public async Task<IActionResult> OnGetFindAsync(string? q)
    {
        if (!RegistryCommands.MayAddPeople(Me)) return StatusCode(403);
        if (Patterns(q) is not var (like, prefix)) return new JsonResult(Array.Empty<object>());
        try
        {
            var rows = await insights.QueryAsync<Found>(FindSql, new { like, prefix });
            return new JsonResult(rows.Select(r => new
            {
                upn = r.Upn,
                name = string.IsNullOrWhiteSpace(r.DisplayName) ? r.Upn : r.DisplayName,
                detail = string.Join(" · ", new[] { r.Upn, r.JobTitle, r.Department }.Where(x => !string.IsNullOrWhiteSpace(x))),
            }));
        }
        catch (Exception ex)
        {
            Incident.Note(Log, ex, "searching Copilot Insights' directory");
            return StatusCode(503);
        }
    }

    /// <summary>Records the person chosen: read again from the directory by sign-in, never taken from the request.</summary>
    public async Task<IActionResult> OnPostAddAsync(string? upn)
    {
        if (!RegistryCommands.MayAddPeople(Me)) return StatusCode(403);
        if (string.IsNullOrWhiteSpace(upn) || Patterns(upn) is not var (_, prefix)) return BadRequest();
        try
        {
            var found = (await insights.QueryAsync<Found>(FindSql, new { like = "\u0001", prefix = prefix.TrimEnd('%') }))
                .FirstOrDefault(r => string.Equals(r.Upn, upn.Trim(), StringComparison.OrdinalIgnoreCase));
            if (found is null) return new JsonResult(new { error = "That person is not in the directory, or their account is disabled." }) { StatusCode = 400 };
            var id = await commands.AddPersonAsync(Guid.NewGuid(), new RegistryCommands.DirectoryPerson(found.ObjectId, found.Upn,
                string.IsNullOrWhiteSpace(found.DisplayName) ? found.Upn : found.DisplayName, found.Mail, found.JobTitle, found.Department));
            return new JsonResult(new { value = id.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), upn = found.Upn, text = $"{id.Name} — {found.Upn}", added = id.Added });
        }
        catch (DomainException ex) { return new JsonResult(new { error = ex.Message }) { StatusCode = 400 }; }
        catch (Exception ex)
        {
            Incident.Note(Log, ex, "recording a person from the directory");
            return StatusCode(503);
        }
    }
}
