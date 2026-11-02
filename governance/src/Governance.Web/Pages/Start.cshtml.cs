using Dapper;
using Governance.Application;
using Governance.Application.Commands;
using Governance.Application.Data;
using Governance.Application.Queries;
using Governance.Domain;
using Governance.Web.Infrastructure;
using Microsoft.AspNetCore.Mvc;

namespace Governance.Web.Pages;

public class StartModel(WorkQueries work, RegistryCommands registry, Db db) : GovPage
{
    public IReadOnlyList<JourneyTile> Tiles { get; private set; } = Array.Empty<JourneyTile>();
    public bool IntakeOpen { get; private set; }
    public IReadOnlyList<SetupStep> Setup { get; private set; } = Array.Empty<SetupStep>();
    public bool ShowSetup { get; private set; }
    public int OpenTasks { get; private set; }
    public int MyRequests { get; private set; }
    public IReadOnlyList<(Guid PublicId, string RoleName, string? Scope)> PendingRoles { get; private set; } = Array.Empty<(Guid, string, string?)>();

    /// <summary>Tiles a person sees: everyone gets the routine journeys; commercial and recertification follow roles.</summary>
    public bool Visible(string journey) => journey switch
    {
        Journeys.Commercial => Me.Can(Permissions.RegistryFinance) || Me.Can(Permissions.CommercialView),
        Journeys.Recertification => Me.Roles.Any(r => r.RoleCode is Roles.LobBusinessOwner or Roles.SharedServiceOwner or Roles.ProductOwner) || Me.HasCentral(Roles.PlatformOwner),
        Journeys.SeatReservation => Me.Roles.Any(r => r.ScopeType == ScopeTypes.FundingOwner && Application.Seats.SeatRequests.RequesterRoles.Contains(r.RoleCode))
                                    || Me.HasCentral(Roles.FinOpsLead) || Me.HasCentral(Roles.PlatformOwner),
        _ => true,
    };

    public string Link(string journey) => journey switch
    {
        Journeys.RoutineAccess => "/Requests/Access",
        Journeys.ConsumptionEvent => "/Events/Report",
        Journeys.Recertification => "/Recertification",
        _ => $"/Requests/New/{journey}",
    };

    public async Task OnGetAsync()
    {
        Tiles = await work.JourneysAsync();
        IntakeOpen = await work.IntakeOpenAsync();
        ShowSetup = Me.HasCentral(Roles.PlatformOwner, Roles.WorkbenchAdmin, Roles.Steering) || !IntakeOpen;
        if (ShowSetup) Setup = await work.SetupAsync();
        OpenTasks = (await work.MyWorkAsync(new ListQuery(PageSize: 1))).Total;
        MyRequests = (await work.RequestsAsync(new ListQuery(PageSize: 1), mine: true)).Total;
        await using var c = await db.OpenSystemAsync();
        PendingRoles = (await c.QueryAsync<(Guid, string, string?)>(
            """
            SELECT ra.PublicId, rd.Name, fo.Name FROM sec.RoleAssignment ra JOIN sec.RoleDefinition rd ON rd.RoleCode = ra.RoleCode
            LEFT JOIN core.FundingOwner fo ON fo.FundingOwnerId = ra.ScopeId
            WHERE ra.PersonId = @p AND ra.AcceptedUtc IS NULL AND ra.RoleCode IN ('LOB_BUSINESS_OWNER','SHARED_SERVICE_OWNER') AND (ra.EffectiveTo IS NULL OR ra.EffectiveTo >= CAST(SYSUTCDATETIME() AS date))
            """, new { p = Me.PersonId })).ToList();
    }

    public Task<IActionResult> OnPostAcceptAsync(Guid id)
        => Act(() => registry.AcceptRoleAsync(Idem, id), "You accepted the role. The funding owner's activation checklist shows it.");
}
