using Governance.Application;
using Governance.Application.Data;
using Governance.Application.Queries;
using Governance.Domain;
using Governance.Web.Infrastructure;
using Governance.Application.Commands;
using Governance.Application.Integrations;
using Governance.Application.Queries;
using Microsoft.AspNetCore.Mvc;

namespace Governance.Web.Pages.Admin;

public class RaciModel(AdminQueries admin, AdminCommands commands) : GovPage
{
    [BindProperty(SupportsGet = true, Name = "v")] public int? VersionNo { get; set; }
    [BindProperty(SupportsGet = true)] public bool Simplified { get; set; }
    public RaciMatrix? M { get; private set; }
    public IReadOnlyList<RaciVersionRow> Versions { get; private set; } = Array.Empty<RaciVersionRow>();
    public bool CanEdit => M?.Version.Status == "DRAFT" && (Principal.Can(Permissions.AdminConfigure) || Principal.HasCentral(Roles.PlatformOwner));

    public async Task OnGetAsync()
    {
        Versions = await admin.RaciVersionsAsync();
        M = await admin.RaciAsync(VersionNo);
    }

    public async Task<IActionResult> OnGetCsvAsync()
    {
        var m = await admin.RaciAsync(VersionNo);
        if (m is null) return NotFound();
        var rows = m.Activities.Select(a => new RaciCsvRow(a.Name, a.Stages,
            m.Functions.Select(f => $"{f.Name}={(m.Cells.TryGetValue((a.Code, f.FunctionCode), out var v) ? v : "I")}").Aggregate((x, y) => x + "; " + y)));
        return Csv(rows, $"raci-v{m.Version.VersionNo}");
    }

    public sealed record RaciCsvRow(string Activity, string Stages, string Cells);

    public Task<IActionResult> OnPostDraftAsync(string? notes)
        => Act(() => commands.DraftRaciAsync(Idem, notes), v => $"Draft version {v} created from the active one.", v => Redirect($"/workbench/Admin/Raci?v={v}"));

    public Task<IActionResult> OnPostCellAsync(string activity, string function, string value)
        => Act(() => commands.SetRaciCellAsync(Idem, VersionNo ?? 0, activity, function, value), "Cell saved. Approvals given so far were cleared.", () => Redirect($"/workbench/Admin/Raci?v={VersionNo}"));

    public Task<IActionResult> OnPostApproveAsync(string asRole)
        => Act(() => commands.ApproveRaciAsync(Idem, VersionNo ?? 0, asRole), "Approval recorded. The version activates after both approvals.", () => Redirect($"/workbench/Admin/Raci?v={VersionNo}"));
}

public class WorkflowsModel(AdminQueries admin) : GovPage
{
    public IReadOnlyList<(string JourneyCode, string Name, int VersionNo, IReadOnlyList<StageDefinition> Stages, IReadOnlyList<TransitionDefinition> Transitions)> Journeys { get; private set; }
        = Array.Empty<(string, string, int, IReadOnlyList<StageDefinition>, IReadOnlyList<TransitionDefinition>)>();
    public async Task OnGetAsync() => Journeys = await admin.WorkflowsAsync();
}

public class SlaModel(AdminQueries admin, AdminCommands commands) : GovPage
{
    public IReadOnlyList<SlaPolicy> Policies { get; private set; } = Array.Empty<SlaPolicy>();
    public IReadOnlyList<(string LadderCode, int Level, string RoleCode, string ScopeKind)> Ladders { get; private set; } = Array.Empty<(string, int, string, string)>();
    public async Task OnGetAsync()
    {
        Policies = await admin.SlaPoliciesAsync();
        Ladders = await admin.LaddersAsync();
    }
    public Task<IActionResult> OnPostAsync(string code, int duration) => Act(() => commands.SetSlaAsync(Idem, code, duration), "Service level changed. New tasks use it; open tasks keep their due time.");
}

public class NotificationsModel(AdminQueries admin, AdminCommands commands) : GovPage
{
    public IReadOnlyList<AdminQueries.NotificationRuleRow> Rules { get; private set; } = Array.Empty<AdminQueries.NotificationRuleRow>();
    public (int Pending, int DeadLettered, DateTime? OldestPendingUtc) Outbox { get; private set; }
    public async Task OnGetAsync()
    {
        Rules = await admin.NotificationRulesAsync();
        Outbox = await admin.OutboxAsync();
    }
    public Task<IActionResult> OnPostAsync(string code, bool enabled) => Act(() => commands.SetNotificationRuleAsync(Idem, code, enabled), enabled ? "Rule enabled." : "Rule disabled.");
}

public class RolesModel(RegistryQueries registry, AdminQueries admin, RegistryCommands registryCommands, AdminCommands commands) : GovPage
{
    public Paged<RoleAssignmentRow> Items { get; private set; } = new();
    public IReadOnlyList<Option> People { get; private set; } = Array.Empty<Option>();
    public IReadOnlyList<AdminQueries.RoleOption> CentralRoles { get; private set; } = Array.Empty<AdminQueries.RoleOption>();
    public IReadOnlyList<AdminQueries.GroupMapRow> Groups { get; private set; } = Array.Empty<AdminQueries.GroupMapRow>();
    public bool CanAssign => Principal.Can(Permissions.RoleAssignCentral);

    public async Task OnGetAsync()
    {
        Items = await registry.RoleAssignmentsAsync(Query, centralOnly: true);
        People = await registry.PeopleOptionsAsync();
        CentralRoles = (await admin.RolesAsync()).Where(r => r.IsCentral).ToList();
        Groups = await admin.GroupMapAsync();
    }

    public async Task<IActionResult> OnGetCsvAsync() => Csv((await registry.RoleAssignmentsAsync(Query.All(), centralOnly: true)).Items, "central-roles");
    public Task<IActionResult> OnPostAssignAsync(int personId, string roleCode, DateTime? effectiveTo) => Act(() => registryCommands.AssignRoleAsync(Idem, personId, roleCode, null, effectiveTo), "Role assigned.");
    public Task<IActionResult> OnPostEndAsync(Guid role) => Act(() => registryCommands.EndRoleAsync(Idem, role), "Role ended.");
    public Task<IActionResult> OnPostMapAsync(Guid groupId, string roleCode, string? groupName, bool remove)
        => Act(() => commands.MapGroupAsync(Idem, groupId, roleCode, groupName, remove), remove ? "Mapping removed." : "Mapped. The directory sync assigns the role to the group's members.");
}

public class IntegrationsModel(AdminQueries admin, OperationsQueries ops, IServiceProvider services) : GovPage
{
    public IReadOnlyList<(string JobName, DateTime? LastSuccessUtc, DateTime? LastRunUtc, string? LastStatus, string? Message)> Jobs { get; private set; } = Array.Empty<(string, DateTime?, DateTime?, string?, string?)>();
    public IReadOnlyList<IntegrationHealth> Health { get; private set; } = Array.Empty<IntegrationHealth>();
    public IReadOnlyList<(string Integration, DateTime? LastSuccessUtc, DateTime? LastRunUtc, string? LastStatus)> Freshness { get; private set; } = Array.Empty<(string, DateTime?, DateTime?, string?)>();
    public (int Pending, int DeadLettered, DateTime? OldestPendingUtc) Outbox { get; private set; }
    public bool InfrastructureInstalled { get; private set; }

    public async Task OnGetAsync()
    {
        Jobs = await admin.JobsAsync();
        Freshness = await ops.FreshnessAsync();
        Outbox = await admin.OutboxAsync();
        var status = services.GetService<IIntegrationStatus>();
        InfrastructureInstalled = status is not null;
        if (status is not null)
        {
            try { Health = await status.GetAsync(HttpContext.RequestAborted); }
            catch (Exception ex) { ShowErrorNow(Incident.Note(Log, ex, "reading integration health")); }
        }
    }
}

public class IntakeModel(WorkQueries work, AdminCommands commands) : GovPage
{
    public IReadOnlyList<SetupStep> Steps { get; private set; } = Array.Empty<SetupStep>();
    public bool Open { get; private set; }
    public async Task OnGetAsync()
    {
        Steps = await work.SetupAsync();
        Open = await work.IntakeOpenAsync();
    }
    public Task<IActionResult> OnPostSwitchAsync(bool open) => Act(() => commands.SetIntakeAsync(Idem, open), open ? "Intake is open. The Start page accepts new requests." : "Intake is closed.");
    public Task<IActionResult> OnPostStandardsAsync() => Act(() => commands.ApproveStandardsAsync(Idem), "Standards approved (setup step 5).");
}

public class TextModel(AdminQueries admin, AdminCommands commands, HelpText help) : GovPage
{
    public IReadOnlyList<AdminQueries.TextBlockRow> Blocks { get; private set; } = Array.Empty<AdminQueries.TextBlockRow>();
    public async Task OnGetAsync() => Blocks = await admin.TextBlocksAsync();
    public Task<IActionResult> OnPostSaveAsync(string key, string body) => Act(() => commands.SaveTextAsync(Idem, key, body), "Draft saved. The Platform Owner approves it to show it.");
    public Task<IActionResult> OnPostApproveAsync(string key, int version)
        => Act(async () => { await commands.ApproveTextAsync(Idem, key, version); await help.ReloadAsync(); }, "Approved. The new wording shows now.");
}
