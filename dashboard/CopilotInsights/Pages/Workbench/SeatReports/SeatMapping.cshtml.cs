using Governance.Application;
using Governance.Application.Commands;
using Governance.Application.Queries;
using Governance.Domain;
using Governance.Web.Infrastructure;
using Microsoft.AspNetCore.Mvc;

namespace Governance.Web.Pages.Admin;

/// <summary>
/// The hierarchy audit's mapping: for each tracking group, the leader its members report up to and that leader's level
/// under the top of the organization. Seen by those who see every line of business's seats; set by the Platform Owner.
/// </summary>
public class SeatMappingModel(SeatQueries seats, SeatCommands commands, CopilotInsights.Infrastructure.Db insights, Microsoft.Extensions.Caching.Memory.IMemoryCache cache) : GovPage
{
    public IReadOnlyList<SeatMappingRow> Rows { get; private set; } = Array.Empty<SeatMappingRow>();
    /// <summary>Leaders' names and levels from Copilot Insights' directory, by sign-in.</summary>
    public IReadOnlyDictionary<string, (string? Name, int? Level)> Leaders { get; private set; } = new Dictionary<string, (string?, int?)>();
    public bool CanEdit => Principal.HasRole(Roles.PlatformOwner);

    public async Task<IActionResult> OnGetAsync()
    {
        if (!Reports.SeatAuditAccess.CanOpen(Principal)) return Forbid();
        Rows = await seats.MappingAsync();
        var upns = Rows.Select(r => r.ExpectedLeaderUpn).Where(u => !string.IsNullOrWhiteSpace(u)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (upns.Count > 0)
        {
            try
            {
                var found = await insights.QueryAsync<(string Upn, string? DisplayName, int? Level)>(
                    "SELECT p.Upn, p.DisplayName, CAST(p.Level AS int) AS Level FROM dash.Person AS p WHERE p.Upn IN (SELECT j.value FROM OPENJSON(@upns) AS j)",
                    new { upns = System.Text.Json.JsonSerializer.Serialize(upns) });
                Leaders = found.GroupBy(f => f.Upn, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => (g.First().DisplayName, g.First().Level), StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex) { Incident.Note(Log, ex, "reading leaders from Copilot Insights' directory"); }
        }
        return Page();
    }

    public Task<IActionResult> OnPostSaveAsync(Guid group, string? leader, int? level)
        => Act(async () =>
        {
            await commands.SetGroupMappingAsync(Idem, group, leader, level);
            SeatAudit.Forget(cache);
        }, string.IsNullOrWhiteSpace(leader) ? "Mapping cleared: the group's holders show as Unknown tracking group." : "Mapping saved. The hierarchy audit uses it from now on.");
}
