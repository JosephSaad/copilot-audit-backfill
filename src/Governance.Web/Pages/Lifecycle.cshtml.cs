using Governance.Application.Commands;
using Governance.Application.Queries;
using Microsoft.AspNetCore.Mvc;

namespace Governance.Web.Pages;

public class ExceptionsModel(OperationsQueries ops, OperationsCommands commands) : GovPage
{
    [BindProperty(SupportsGet = true)] public bool All { get; set; }
    public Paged<ExceptionRow> Items { get; private set; } = new();

    public async Task OnGetAsync() => Items = await ops.ExceptionsAsync(Query, !All);
    public async Task<IActionResult> OnGetCsvAsync() => Csv((await ops.ExceptionsAsync(Query.All(), !All)).Items, "exceptions");

    public Task<IActionResult> OnPostCloseAsync(Guid id, string reason) => Act(() => commands.CloseExceptionAsync(Idem, id, reason), "Exception closed.");
}

/// <summary>Retirement (I4, I5): requests in the retirement journey and workloads retiring or retired.</summary>
public class RetirementModel(WorkQueries work, RegistryQueries registry) : GovPage
{
    public Paged<RequestListItem> Requests { get; private set; } = new();
    public Paged<WorkloadListItem> Retiring { get; private set; } = new();

    public async Task OnGetAsync()
    {
        var all = await work.RequestsAsync(new ListQuery(Search, "activity", true, 1, 200), mine: false);
        Requests = new Paged<RequestListItem> { Items = all.Items.Where(r => r.JourneyCode == Journeys.Retirement).ToList(), Total = all.Items.Count(r => r.JourneyCode == Journeys.Retirement), Query = Query };
        Retiring = await registry.WorkloadsAsync(Query, WorkloadStates.Retiring);
    }

    public async Task<IActionResult> OnGetCsvAsync() => Csv((await registry.WorkloadsAsync(Query.All(), WorkloadStates.Retiring)).Items, "retiring");
}
