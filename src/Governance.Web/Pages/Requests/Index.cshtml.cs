using Governance.Application.Queries;
using Governance.Web.Infrastructure;
using Microsoft.AspNetCore.Mvc;

namespace Governance.Web.Pages.Requests;

public class IndexModel(WorkQueries work) : GovPage
{
    [BindProperty(SupportsGet = true)] public bool Mine { get; set; }
    [BindProperty(SupportsGet = true)] public string? State { get; set; }
    public Paged<RequestListItem> Items { get; private set; } = new();

    public async Task OnGetAsync() => Items = await work.RequestsAsync(Query, Mine || !Principal.IsCentral && !Principal.FundingOwners.Any(), State);

    public async Task<IActionResult> OnGetCsvAsync()
        => Csv((await work.RequestsAsync(Query.All(), Mine, State)).Items, "requests");
}
