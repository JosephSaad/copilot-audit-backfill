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
public class ActAsModel(TestingOptions testing, Db db, AuditWriter audit) : GovPage
{
    public IReadOnlyList<(Guid PublicId, string DisplayName, string? Upn, string Roles)> Personas { get; private set; } = Array.Empty<(Guid, string, string?, string)>();

    public async Task<IActionResult> OnGetAsync()
    {
        if (!testing.AllowActAs) return NotFound();
        await using var c = await db.OpenSystemAsync();
        Personas = (await c.QueryAsync<(Guid, string, string?, string)>(
            """
            SELECT p.PublicId, p.DisplayName, p.UserPrincipalName,
                   COALESCE(STUFF((SELECT ', ' + rd.Name + COALESCE(' (' + fo.Name + ')', '') FROM sec.RoleAssignment ra JOIN sec.RoleDefinition rd ON rd.RoleCode = ra.RoleCode
                                   LEFT JOIN core.FundingOwner fo ON fo.FundingOwnerId = ra.ScopeId
                                   WHERE ra.PersonId = p.PersonId AND (ra.EffectiveTo IS NULL OR ra.EffectiveTo >= CAST(SYSUTCDATETIME() AS date)) FOR XML PATH('')), 1, 2, ''), 'No roles')
            FROM sec.Person p WHERE p.IsSynthetic = 1 AND p.AccountEnabled = 1 ORDER BY p.DisplayName
            """)).ToList();
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(Guid id)
    {
        if (!testing.AllowActAs) return NotFound();
        Response.Cookies.Append(PrincipalLoader.ActAsCookie, id.ToString(), new CookieOptions { HttpOnly = true, SameSite = SameSiteMode.Strict, IsEssential = true });
        await db.InTransactionAsync(u => audit.WriteAsync(u, Me, "actas.start", "Person", id, null));
        return Redirect("/Start");
    }

    public async Task<IActionResult> OnPostStopAsync()
    {
        Response.Cookies.Delete(PrincipalLoader.ActAsCookie);
        if (testing.AllowActAs) await db.InTransactionAsync(u => audit.WriteAsync(u, Me, "actas.stop", "Person", Me.PublicId, null));
        return Redirect("/Start");
    }
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
            return Redirect(Request.Headers.Referer.FirstOrDefault() is { Length: > 0 } r && Uri.TryCreate(r, UriKind.Absolute, out var u) && u.Host == Request.Host.Host ? u.PathAndQuery : "/Start");
        }
    }
}

/// <summary>Teams personal tab launcher: frameable only by Teams (CSP frame-ancestors); opens the item in the browser where normal sign-in applies.</summary>
[AllowAnonymous]
public class TeamsLaunchModel : PageModel
{
    public string Target { get; private set; } = "/Start";

    public void OnGet(string? item)
    {
        // Only Workbench paths, never an absolute or protocol-relative address (no open redirect).
        if (!string.IsNullOrEmpty(item) && item.StartsWith('/') && !item.StartsWith("//") && !item.Contains('\\') && !item.Contains(':'))
            Target = item;
        else if (Guid.TryParse(item, out var g))
            Target = $"/Requests/{g}";
    }
}
