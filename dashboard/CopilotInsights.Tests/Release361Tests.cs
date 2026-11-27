using Governance.Domain;
using Governance.Web.Infrastructure;
using Governance.Web.Pages.Operations;
using Xunit;

namespace CopilotInsights.Tests;

/// <summary>3.6.1: the Platform Owner and administrators read the seats themselves; person pickers search all of AD in the open.</summary>
public class Release361Tests
{
    static string Repo(params string[] path)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine([d.FullName, .. path]))) return File.ReadAllText(Path.Combine([d.FullName, .. path]));
        throw new FileNotFoundException(string.Join('/', path));
    }

    static HashSet<string> Set(params string[] s) => new(s, StringComparer.OrdinalIgnoreCase);

    static WebPrincipal Who(bool operatorRole = false, bool admin = false, bool owner = false) => new()
    {
        IsAuthenticated = true,
        IsCentral = operatorRole || owner,
        Permissions = operatorRole ? Set(Permissions.SyncRun) : Set(),
        AppRoles = admin ? Set(AppRoles.Admin, AppRoles.Central) : Set(AppRoles.Central),
        Roles = owner ? [new RoleGrant(Roles.PlatformOwner, ScopeTypes.Tenant, null)] : [],
    };

    [Theory]
    [InlineData(true, false, false, false, true)]    // the platform operator, always
    [InlineData(true, false, false, true, true)]
    [InlineData(false, false, true, true, true)]     // the Platform Owner, with seats only
    [InlineData(false, true, false, true, true)]     // an Insights administrator, with seats only
    [InlineData(false, false, true, false, false)]   // with the whole Workbench, operators only, as before
    [InlineData(false, true, false, false, false)]
    [InlineData(false, false, false, true, false)]   // nobody else
    public void Who_may_read_the_seats(bool op, bool admin, bool owner, bool seatsOnly, bool may)
        => Assert.Equal(may, SyncRunModel.MayReadSeats(Who(op, admin, owner), seatsOnly));

    [Fact]
    public void The_sync_page_opens_to_whoever_may_read_the_seats()
    {
        var operatorPage = new WebPrincipal { IsAuthenticated = true, AppRoles = Set(AppRoles.Central, AppRoles.Operator) };
        Assert.True(SeatReadHandler.Allows(operatorPage, false));                // the GovernanceOperator grant, as before
        Assert.True(SeatReadHandler.Allows(Who(owner: true), true));
        Assert.True(SeatReadHandler.Allows(Who(admin: true), true));
        Assert.False(SeatReadHandler.Allows(Who(owner: true), false));         // the whole Workbench: operators only
        Assert.False(SeatReadHandler.Allows(Who(), true));
        var ops = Repo("dashboard", "CopilotInsights", "Pages", "Workbench", "Operations", "Operations.cshtml.cs");
        Assert.Equal(2, ops.Split("[Authorize(Policy = Policies.SeatRead)]").Length - 1);   // Sync and SyncRun
        Assert.Contains("[Authorize(Policy = Policies.Operator)]\r\npublic class AttestationsModel".Replace("\r\n", ops.Contains("\r\n") ? "\r\n" : "\n"), ops);
        Assert.Equal(3, ops.Split("if (!Principal.Can(Permissions.SyncRun))").Length - 1); // the other reads keep the operator role
    }

    [Fact]
    public void The_platform_owner_finds_the_seat_read_in_the_menu_with_seats_only()
    {
        static IEnumerable<string> Pages(WebPrincipal p, bool seats) => WorkbenchNav.For(p, true, seats).SelectMany(g => g.Pages).Select(x => x.Title);
        Assert.Contains("Sync and freshness", Pages(Who(owner: true), true));
        Assert.Contains("Sync and freshness", Pages(Who(admin: true), true));
        Assert.DoesNotContain("Sync and freshness", Pages(Who(), true));
    }

    [Fact]
    public void Person_pickers_search_all_of_ad_without_a_button_first()
    {
        var partial = Repo("dashboard", "CopilotInsights", "Pages", "Workbench", "_FindPerson.cshtml");
        Assert.Contains("Or search everyone in Active Directory", partial);
        Assert.Contains("<div class=\"find-box\">", partial);          // open, not hidden behind a toggle
        Assert.DoesNotContain("find-open", partial);
        var js = Repo("dashboard", "CopilotInsights", "wwwroot", "workbench.js");
        Assert.Contains("if (open) open.addEventListener", js);
        Assert.DoesNotContain("panel.hidden = true; open.setAttribute", js);
        Assert.True(js.All(c => c < 128), "workbench.js stays ASCII");
    }
}
