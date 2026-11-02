using Governance.Application;
using Governance.Application.Data;
using Governance.Application.Queries;
using Governance.Domain;
using Governance.Web.Infrastructure;
using System.Diagnostics;
using Dapper;
using Governance.Application.Commands;
using Governance.Application.Data;
using Governance.Application.Outbox;
using Governance.Domain;
using Governance.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Governance.Web.Pages;

[AllowAnonymous]
[IgnoreAntiforgeryToken]
public class ErrorModel(ILogger<ErrorModel> log) : PageModel
{
    public string Reference { get; private set; } = "";

    public void OnGet()
    {
        var feature = HttpContext.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerPathFeature>();
        Reference = feature?.Error is Exception ex ? Incident.Note(log, ex, "showing this page").Split("reference ")[1].Split(' ')[0] : Activity.Current?.Id ?? HttpContext.TraceIdentifier;
    }
}

public class AccessDeniedModel : GovPage
{
    public void OnGet() { }
}

/// <summary>Who I am in the Workbench: roles, scopes, delegations.</summary>
public class MeModel : GovPage
{
    public void OnGet() { }
}

/// <summary>Development-only persona switch (Testing:AllowActAs). Refused otherwise.</summary>
public class ActAsModel(TestingOptions testing, Db db) : GovPage
{
    public IReadOnlyList<(Guid PublicId, string DisplayName, string? Upn, string Roles)> Personas { get; private set; } = Array.Empty<(Guid, string, string?, string)>();

    public async Task<IActionResult> OnGetAsync()
    {
        if (!testing.AllowActAs) return NotFound();
        if (!Me.CanActAs) return Forbid();
        await using var c = await db.OpenSystemAsync();
        Personas = (await c.QueryAsync<(Guid, string, string?, string)>(
            """
            SELECT p.PublicId, p.DisplayName, p.UserPrincipalName,
                   COALESCE(STUFF((SELECT ', ' + rd.Name + COALESCE(' (' + fo.Name + ')', '') FROM sec.RoleAssignment ra JOIN sec.RoleDefinition rd ON rd.RoleCode = ra.RoleCode
                                   LEFT JOIN core.FundingOwner fo ON fo.FundingOwnerId = ra.ScopeId
                                   WHERE ra.PersonId = p.PersonId AND (ra.EffectiveTo IS NULL OR ra.EffectiveTo >= CAST(SYSUTCDATETIME() AS date)) FOR XML PATH('')), 1, 2, ''), 'No roles')
            FROM sec.Person p WHERE p.IsSynthetic = 1 AND p.AccountEnabled = 1
              -- Synthetic seat holders of the Copilot seat demo (they carry an object ID and no role) are not personas.
              AND (p.EntraObjectId IS NULL OR EXISTS (SELECT 1 FROM sec.RoleAssignment r WHERE r.PersonId = p.PersonId))
            ORDER BY p.DisplayName
            """)).ToList();
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(Guid id)
    {
        if (!testing.AllowActAs) return NotFound();
        if (!Me.CanActAs) return Forbid();
        string? name;
        await using (var c = await db.OpenSystemAsync())
            name = await c.QuerySingleOrDefaultAsync<string>("SELECT DisplayName FROM sec.Person WHERE PublicId = @id AND IsSynthetic = 1 AND AccountEnabled = 1", new { id });
        if (name is null) return NotFound();
        Response.Cookies.Append(PrincipalLoader.ActAsCookie, id.ToString(), new CookieOptions { HttpOnly = true, SameSite = SameSiteMode.Strict, IsEssential = true });
        await WriteAsync("actas.start", id, $"Started acting as {name} (test persona).");
        return Redirect("/workbench/Start");
    }

    public async Task<IActionResult> OnPostStopAsync()
    {
        Response.Cookies.Delete(PrincipalLoader.ActAsCookie);
        if (testing.AllowActAs && Me.ActingAs) await WriteAsync("actas.stop", Me.PublicId, $"Stopped acting as {Me.DisplayName}.");
        return Redirect("/workbench/Start");
    }

    /// <summary>The switch is recorded against the signed-in person alone, not against the persona being left or entered.</summary>
    private Task WriteAsync(string action, Guid personaId, string detail)
        => db.InTransactionAsync(u => u.ExecuteAsync(
            "INSERT aud.AuditLog (Utc, PersonId, ActingAsPersonId, Action, EntityType, EntityId, Detail, Outcome) VALUES (SYSUTCDATETIME(), @p, NULL, @action, 'Person', @id, @detail, 'OK')",
            new { p = Me.RealPersonId ?? Me.PersonId, action, id = personaId.ToString(), detail }));
}

/// <summary>Evidence download: visible only through something the person can see; every download is audited.</summary>
public class EvidenceModel(EvidenceQueries evidence) : GovPage
{
    public async Task<IActionResult> OnGetAsync(Guid id)
    {
        try
        {
            var file = await evidence.DownloadAsync(id);
            if (file is null) return NotFound();
            await AuditViewAsync("evidence.download", "Evidence", id);
            Response.Headers["X-Content-SHA256"] = file.Value.Sha256;
            return File(file.Value.Bytes, file.Value.ContentType, file.Value.FileName);
        }
        catch (DomainException ex)
        {
            FlashError = ex.Message;
            return Redirect(Request.Headers.Referer.FirstOrDefault() is { Length: > 0 } r && Uri.TryCreate(r, UriKind.Absolute, out var u) && u.Host == Request.Host.Host ? u.PathAndQuery : "/workbench/Start");
        }
    }
}

/// <summary>Teams personal tab launcher: frameable only by Teams (CSP frame-ancestors); opens the item in the browser where normal sign-in applies.</summary>
[AllowAnonymous]
public class TeamsLaunchModel : PageModel
{
    public string Target { get; private set; } = "/workbench/Start";

    // Only these Workbench addresses (no open redirect): an item page by its GUID, or a fixed list page.
    private static readonly System.Text.RegularExpressions.Regex ItemPath = new(
        @"^/(Requests|Registry/Workloads|Registry/FundingOwners|Operations/Events)/[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    private static readonly HashSet<string> Fixed = new(StringComparer.OrdinalIgnoreCase)
    {
        "/workbench/Start", "/workbench/MyWork", "/workbench/Recertification", "/workbench/Exceptions", "/workbench/Operations/Findings", "/workbench/Operations/Events", "/workbench/Operations/Attestations", "/workbench/Operations/Sync",
    };

    public void OnGet(string? item)
    {
        Target = Resolve(item);
    }

    public static string Resolve(string? item)
    {
        if (string.IsNullOrEmpty(item) || item.Length > 200 || item.Any(char.IsControl) || item.Any(char.IsWhiteSpace)) return "/workbench/Start";
        if (Guid.TryParse(item, out var g)) return $"/workbench/Requests/{g}";
        if (Fixed.Contains(item)) return item;
        return ItemPath.IsMatch(item) ? item : "/workbench/Start";
    }
}
