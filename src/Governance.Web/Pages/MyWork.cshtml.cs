using Governance.Application.Queries;
using Governance.Web.Infrastructure;
using Microsoft.AspNetCore.Mvc;

namespace Governance.Web.Pages;

public class MyWorkModel(WorkQueries work) : GovPage
{
    public Paged<TaskListItem> Tasks { get; private set; } = new();

    public async Task OnGetAsync() => Tasks = await work.MyWorkAsync(Query);

    public async Task<IActionResult> OnGetCsvAsync()
        => Csv((await work.MyWorkAsync(Query.All())).Items, "my-work", "Title", "TaskType", "StageCode", "RequestNumber", "FundingOwnerName", "RoleName", "AssignedName", "StartedUtc", "DueUtc", "EscalationLevel");
}
