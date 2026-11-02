using Governance.Application;
using Governance.Application.Data;
using Governance.Application.Queries;
using Governance.Domain;
using Governance.Web.Infrastructure;
using Governance.Application.Commands;
using Governance.Application.Integrations;
using Governance.Application.Queries;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Identity.Web;

namespace Governance.Web.Pages.Operations;

public class EventsModel(OperationsQueries ops) : GovPage
{
    [BindProperty(SupportsGet = true)] public bool Open { get; set; } = true;
    public Paged<EventRow> Items { get; private set; } = new();

    public async Task OnGetAsync() => Items = await ops.EventsAsync(Query, Open);
    public async Task<IActionResult> OnGetCsvAsync() => Csv((await ops.EventsAsync(Query.All(), Open)).Items, "consumption-events");
}

public class EventModel(OperationsQueries ops, OperationsCommands commands) : GovPage
{
    [BindProperty(SupportsGet = true)] public Guid Id { get; set; }
    public EventRow E { get; private set; } = new();
    public bool CanRespond => Principal.Can(Permissions.EventRespond);

    public async Task<IActionResult> OnGetAsync()
    {
        var e = await ops.EventAsync(Id);
        if (e is null) return NotFound();
        E = e;
        return Page();
    }

    public Task<IActionResult> OnPostStepAsync(string step, string? text) => Act(() => commands.AdvanceEventAsync(Idem, Id, step, text), "Recorded.");
}

public class FindingsModel(RegistryQueries registry, OperationsCommands commands) : GovPage
{
    [BindProperty(SupportsGet = true)] public bool Open { get; set; } = true;
    [BindProperty(SupportsGet = true)] public string? Type { get; set; }
    public Paged<FindingRow> Items { get; private set; } = new();
    public bool CanAct => Principal.Can(Permissions.Verify) || Principal.Can(Permissions.RegistryEdit);

    public async Task OnGetAsync() => Items = await registry.FindingsAsync(Query, string.IsNullOrEmpty(Type) ? null : Type, Open);
    public async Task<IActionResult> OnGetCsvAsync() => Csv((await registry.FindingsAsync(Query.All(), string.IsNullOrEmpty(Type) ? null : Type, Open)).Items, "findings");

    public Task<IActionResult> OnPostActAsync(Guid finding, string action, string? text)
        => Act(() => commands.UpdateFindingAsync(Idem, finding, action, text), action switch { "accept" => "Accepted with the reason recorded.", "resolve" => "Resolved.", _ => "Acknowledged." });
}

[Authorize(Policy = Policies.Operator)]
public class SyncModel(OperationsQueries ops, IServiceProvider services) : GovPage
{
    public IReadOnlyList<(string Integration, DateTime? LastSuccessUtc, DateTime? LastRunUtc, string? LastStatus)> Freshness { get; private set; } = Array.Empty<(string, DateTime?, DateTime?, string?)>();
    public IReadOnlyList<IntegrationHealth> Health { get; private set; } = Array.Empty<IntegrationHealth>();
    public Paged<SyncRunRow> Runs { get; private set; } = new();
    public bool HasInventory => services.GetService<IInventorySync>() is not null;
    public bool HasLicensing => services.GetService<ILicensingRead>() is not null;
    public bool HasRegistry => services.GetService<IRegistryImport>() is not null;
    public bool HasSeats => services.GetService<SeatSyncService>() is not null;

    public static readonly IReadOnlyDictionary<string, (string Name, int MaxAgeHours)> Integrations = new Dictionary<string, (string, int)>
    {
        ["X1"] = ("Entra directory (people, cost center, groups)", 1), ["X2"] = ("Entra access packages", 1), ["X3"] = ("Entra access reviews", 48),
        ["X4"] = ("Power Platform inventory (operator-run)", 24 * 7), ["X5"] = ("Power Platform licensing read", 26), ["X6"] = ("Microsoft 365 settings attestation", 24 * 31),
        ["X7"] = ("Agent Registry export", 24 * 31), ["X8"] = ("ITSM", 1), ["X9"] = ("Email", 24), ["X10"] = ("Teams activity feed", 24), ["X11"] = ("Teams channels", 24),
        ["X12"] = ("Copilot Agent Kit (optional)", 48),
        ["X13"] = ("Copilot seat licenses (Graph)", 26),
    };

    public async Task OnGetAsync()
    {
        Freshness = await ops.FreshnessAsync();
        Runs = await ops.SyncRunsAsync(Query, null);
        if (services.GetService<IIntegrationStatus>() is { } status)
        {
            try { Health = await status.GetAsync(HttpContext.RequestAborted); }
            catch (Exception ex) { ShowErrorNow(Incident.Note(Log, ex, "reading integration health")); }
        }
    }

    public async Task<IActionResult> OnGetCsvAsync() => Csv((await ops.SyncRunsAsync(Query.All(), null)).Items, "sync-runs");
}

/// <summary>
/// Operator-run reads (X4, X5, X7). The inventory API accepts only delegated tokens, so the token belongs to the
/// operator who pressed the button; the worker never receives it. Rate limited: two runs per five minutes.
/// </summary>
[Authorize(Policy = Policies.Operator)]
[EnableRateLimiting("sync")]
[AuthorizeForScopes(Scopes = new[]
{
    "https://api.powerplatform.com/ResourceQuery.Resources.Read", "https://api.powerplatform.com/EnvironmentManagement.Environments.Read",
    "https://api.powerplatform.com/Licensing.Allocations.Read", "https://api.powerplatform.com/Licensing.BillingPolicies.Read",
})]
public class SyncRunModel(IServiceProvider services) : GovPage
{
    public IActionResult OnGet() => Redirect("/workbench/Operations/Sync");

    private async Task<string?> DelegatedTokenAsync()
    {
        var tokens = services.GetService<ITokenAcquisition>();
        if (tokens is null) return null;   // loopback test sign-in: no Entra session
        return await tokens.GetAccessTokenForUserAsync(PowerPlatformScopes.Delegated);
    }

    public async Task<IActionResult> OnPostInventoryAsync()
    {
        if (!Principal.Can(Permissions.SyncRun)) { FlashError = "Running the inventory sync needs the platform operator role."; return Redirect("/workbench/Operations/Sync"); }
        var sync = services.GetService<IInventorySync>();
        if (sync is null) { FlashError = "The inventory integration is not installed on this server."; return Redirect("/workbench/Operations/Sync"); }
        try
        {
            var token = await DelegatedTokenAsync();
            if (token is null) { FlashError = "The inventory API accepts only a signed-in operator's token. Sign in with Microsoft Entra to run it."; return Redirect("/workbench/Operations/Sync"); }
            var run = await sync.RunAsync(token, Principal.PersonId, HttpContext.RequestAborted);
            Flash = "Inventory sync finished. New and changed items raised findings; see Discovery.";
        }
        catch (MicrosoftIdentityWebChallengeUserException) { throw; }   // incremental consent: AuthorizeForScopes sends the operator to sign in
        catch (DomainException ex) { FlashError = ex.Message; }
        catch (Exception ex) { FlashError = Incident.Note(Log, ex, "running the inventory sync"); }
        return Redirect("/workbench/Operations/Sync");
    }

    /// <summary>X13 runs app-only with the Worker's Graph permissions; no operator token is used.</summary>
    public async Task<IActionResult> OnPostSeatsAsync()
    {
        if (!Principal.Can(Permissions.SyncRun)) { FlashError = "Running the seat read needs the platform operator role."; return Redirect("/workbench/Operations/Sync"); }
        var seats = services.GetService<SeatSyncService>();
        if (seats is null) { FlashError = "The seat integration is not installed on this server."; return Redirect("/workbench/Operations/Sync"); }
        try
        {
            await seats.RunAsync(Principal.PersonId, HttpContext.RequestAborted);
            Flash = "Seat read finished. Findings, reclaim proposals and the draft chargeback were updated; the run row says what was read.";
        }
        catch (DomainException ex) { FlashError = ex.Message; }
        catch (IntegrationPermissionException ex) { FlashError = ex.Message; }
        catch (Exception ex) { FlashError = Incident.Note(Log, ex, "reading Copilot seats"); }
        return Redirect("/workbench/Operations/Sync");
    }

    public async Task<IActionResult> OnPostLicensingAsync()
    {
        if (!Principal.Can(Permissions.SyncRun)) { FlashError = "Running the licensing read needs the platform operator role."; return Redirect("/workbench/Operations/Sync"); }
        var read = services.GetService<ILicensingRead>();
        if (read is null) { FlashError = "The licensing integration is not installed on this server."; return Redirect("/workbench/Operations/Sync"); }
        try
        {
            string? token = null;
            try { token = await DelegatedTokenAsync(); } catch (MicrosoftIdentityWebChallengeUserException) { throw; } catch { token = null; }
            await read.RunAsync(token, Principal.PersonId, HttpContext.RequestAborted);
            Flash = "Licensing read finished. The drift evaluator compared it with the approved posture.";
        }
        catch (MicrosoftIdentityWebChallengeUserException) { throw; }
        catch (DomainException ex) { FlashError = ex.Message; }
        catch (Exception ex) { FlashError = Incident.Note(Log, ex, "reading licensing"); }
        return Redirect("/workbench/Operations/Sync");
    }

    public async Task<IActionResult> OnPostRegistryAsync(IFormFile? file)
    {
        if (!Principal.Can(Permissions.SyncRun)) { FlashError = "Importing the registry export needs the platform operator role."; return Redirect("/workbench/Operations/Sync"); }
        var import = services.GetService<IRegistryImport>();
        if (import is null) { FlashError = "The registry import is not installed on this server."; return Redirect("/workbench/Operations/Sync"); }
        if (file is null || file.Length == 0 || file.Length > 10 * 1024 * 1024 || !file.FileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
        { FlashError = "Choose the Agent Registry CSV export (up to 10 MB)."; return Redirect("/workbench/Operations/Sync"); }
        try
        {
            await using var s = file.OpenReadStream();
            await import.ImportAsync(s, file.FileName, Principal.PersonId, HttpContext.RequestAborted);
            Flash = "Registry export imported. Unmatched agents raised findings.";
        }
        catch (DomainException ex) { FlashError = ex.Message; }
        catch (Exception ex) { FlashError = Incident.Note(Log, ex, "importing the registry export"); }
        return Redirect("/workbench/Operations/Sync");
    }
}

[Authorize(Policy = Policies.Operator)]
public class AttestationsModel(OperationsQueries ops, RegistryQueries registry, OperationsCommands commands) : GovPage
{
    [BindProperty(SupportsGet = true, Name = "template")] public int? TemplateId { get; set; }
    [BindProperty(SupportsGet = true, Name = "binding")] public int? BindingId { get; set; }
    public IReadOnlyList<AttestationDue> Due { get; private set; } = Array.Empty<AttestationDue>();
    public Paged<AttestationRow> Recent { get; private set; } = new();
    public AttestationDue? Selected { get; private set; }
    public Dictionary<string, string> Expected { get; private set; } = new();

    public async Task OnGetAsync()
    {
        Due = await ops.AttestationsDueAsync();
        Recent = await ops.AttestationsAsync(Query);
        if (TemplateId is int t)
        {
            Selected = Due.FirstOrDefault(d => d.TemplateId == t && d.BindingId == BindingId);
            var tpl = (await registry.TemplatesAsync(new ListQuery(PageSize: 1000))).Items.FirstOrDefault(x => x.TemplateId == t);
            Expected = RequestCommands.Settings(tpl?.SettingsJson);
        }
    }

    public async Task<IActionResult> OnGetCsvAsync() => Csv((await ops.AttestationsAsync(Query.All())).Items, "attestations");

    public Task<IActionResult> OnPostAsync(string subject, string reason, IFormFile? file)
    {
        var values = Request.Form.Keys.Where(k => k.StartsWith("v_", StringComparison.Ordinal))
            .ToDictionary(k => k[2..], k => Request.Form[k].ToString());
        return Act(() => commands.AttestAsync(Idem, TemplateId ?? 0, BindingId, subject, values, reason, Upload(file)), r => "Attestation recorded. " + r,
            _ => Redirect("/workbench/Operations/Attestations"));
    }
}
