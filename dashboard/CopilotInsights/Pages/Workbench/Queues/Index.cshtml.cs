using Governance.Application;
using Governance.Application.Data;
using Governance.Application.Queries;
using Governance.Domain;
using Governance.Web.Infrastructure;
using Governance.Application.Commands;
using Governance.Application.Queries;
using Governance.Application.Workflow;
using Microsoft.AspNetCore.Mvc;

namespace Governance.Web.Pages.Queues;

/// <summary>Shared queues for central teams. Any eligible holder claims a task; it can be released back.</summary>
public class IndexModel(WorkQueries work, RequestCommands commands) : GovPage
{
    [BindProperty(SupportsGet = true)] public string Queue { get; set; } = "Intake";
    [BindProperty(SupportsGet = true)] public string? Discipline { get; set; }
    public Paged<TaskListItem> Tasks { get; private set; } = new();
    public (string Title, string Lead) Text { get; private set; }

    private (string[] Roles, string[]? Stages, string Title, string Lead) Definition => Queue.ToLowerInvariant() switch
    {
        "intake" => (new[] { Roles.IntakeAnalyst }, new[] { "B2", "B10" }, "Intake",
            "Completeness and sponsorship checks (B2, 2 business days) and fulfillment preparation (B10, 1 business day). Return a request with exactly what is missing."),
        "classification" => (new[] { Roles.Architect }, new[] { "B3" }, "Classification",
            "Classify each workload's four independent fields, trigger type and meters (B3, 3 business days)."),
        "reviews" => (new[] { Roles.Architect, Roles.RiskReviewer, Roles.FinOpsAnalyst, Roles.FinOpsLead, Roles.PlatformOperator }, new[] { "B8" },
            "Specialist reviews" + (Discipline is null ? "" : ": " + WorkflowEngine.DisciplineLabel(Discipline)),
            "Only the disciplines a request triggers review it (B8, 5 business days). The accountable decision follows at B9."),
        "decisions" => (new[] { Roles.PlatformOwner, Roles.RiskReviewer, Roles.CommercialApprover, Roles.FinOpsLead }, new[] { "B9", "F4", "D5" }, "Central decisions",
            "Exception dispositions, risk acceptance, platform feature and preview adoption decisions, and commercial approvals. One accountable decision per type (SoD4)."),
        "fulfillment" => (new[] { Roles.PlatformOperator, Roles.FinOpsAnalyst, Roles.SourcingAgent }, new[] { "D1", "I5", "F1", "F5" }, "Fulfillment and verification",
            "Verify changes made through ITSM (D1, 3 business days) with a second operator, close residual commitments (I5), and commercial execution."),
        _ => (Array.Empty<string>(), null, "Unknown queue", ""),
    };

    public async Task<IActionResult> OnGetAsync()
    {
        var d = Definition;
        if (d.Roles.Length == 0) return NotFound();
        Text = (d.Title, d.Lead);
        Tasks = await work.QueueAsync(Query, d.Roles, d.Stages, Discipline);
        return Page();
    }

    public async Task<IActionResult> OnGetCsvAsync()
    {
        var d = Definition;
        return Csv((await work.QueueAsync(Query.All(), d.Roles, d.Stages, Discipline)).Items, "queue-" + Queue.ToLowerInvariant());
    }

    public Task<IActionResult> OnPostClaimAsync(Guid task, Guid request)
        => Act(() => commands.ClaimAsync(Idem, task), "Claimed. It is yours now.", () => Redirect($"/workbench/Requests/{request}"));
}
