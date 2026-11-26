using Xunit;

namespace CopilotInsights.Tests;

/// <summary>3.5.1: seat groups from on-premises AD, the first Platform Owner, and a directory load asked for on the dashboard.</summary>
public class Release351Tests
{
    static string Repo(params string[] path)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine([d.FullName, .. path]))) return File.ReadAllText(Path.Combine([d.FullName, .. path]));
        throw new FileNotFoundException(string.Join('/', path));
    }

    [Fact]
    public void Every_join_of_a_seat_group_to_its_members_takes_either_key()
    {
        /* Members read from AD are kept under the group's AD objectGUID: a join on the Entra ID alone would count none of them. */
        foreach (var file in new[]
        {
            Repo("governance", "src", "Governance.Application", "Integrations", "SeatSyncService.cs"),
            Repo("governance", "src", "Governance.Application", "Queries", "SeatQueries.cs"),
            Repo("governance", "src", "Governance.Database", "Scripts", "070-seats.sql"),
            Repo("dashboard", "CopilotInsights", "Workbench", "WorkbenchLines.cs"),
        })
        {
            Assert.DoesNotContain("g.EntraGroupId = gm.GroupId", file);
            Assert.DoesNotContain("gm.GroupId = l.EntraGroupId", file);
        }
        Assert.Contains("gm.GroupId IN (l.EntraGroupId, l.AdObjectGuid)", Repo("governance", "src", "Governance.Database", "Scripts", "070-seats.sql"));
    }

    [Fact]
    public void Schema_changes_only_add()
    {
        var sql = Repo("governance", "src", "Governance.Database", "Scripts", "070-seats.sql");
        Assert.Contains("IF COL_LENGTH('core.SeatGroup', 'AdObjectGuid') IS NULL ALTER TABLE core.SeatGroup ADD AdObjectGuid uniqueidentifier NULL;", sql);
        Assert.Contains("('Seats.GroupMembersFrom',       N'AUTO'", sql);
        Assert.Contains("('Seats.AdDomain',               N''", sql);
        /* The worker reads the users' domain through a view of the one row, never dash.Setting itself. */
        Assert.Contains("CREATE OR ALTER VIEW dash.WorkbenchDirectory", Repo("sql", "Create-CopilotDashboardSchema.sql"));
    }

    [Fact]
    public void An_Insights_administrator_names_the_first_Platform_Owner_and_no_one_after()
    {
        var cmd = Repo("governance", "src", "Governance.Application", "Commands", "RegistryCommands.cs");
        Assert.Contains("if (personId == Me.PersonId && !first)", cmd);
        Assert.Contains("if (!first) run.Require(Permissions.RoleAssignCentral", cmd);
        var page = Repo("dashboard", "CopilotInsights", "Pages", "Workbench", "Admin", "Admin.cshtml.cs");
        Assert.Contains("!await registry.HasPlatformOwnerAsync()", page);
        Assert.Contains("(!FirstOwner || r.RoleCode == Roles.PlatformOwner)", page);
    }

    [Fact]
    public void A_directory_load_is_asked_for_on_the_dashboard_and_run_by_the_job_server()
    {
        var page = Repo("dashboard", "CopilotInsights", "Pages", "Admin", "Directory.cs");
        Assert.Contains("public Task<IActionResult> OnPostLoadAsync() => AdminChangeAsync(", page);
        Assert.Contains("'DirectoryLoadRequested'", page);
        var pipeline = Repo("Invoke-CopilotPipeline.ps1");
        Assert.Contains("[switch] $DirectoryOnRequest,", pipeline);
        Assert.Contains("if ($DirectoryOnRequest) { Invoke-DirectoryMain -Setting $s -LogDir $logDir; return }", pipeline);
        /* The request is cleared when the load starts, so a load runs once; one held by another run waits. */
        Assert.Contains("UPDATE dash.Setting SET Value = N'', UpdatedBy = N'job server'", pipeline);
        Assert.Contains("Enter-PipelineRun $t", pipeline);
        var job = Repo("tools", "Install-CopilotJobServer.ps1");
        Assert.Contains("Register-DirectoryTask -Account $account -Credential $ServiceAccountCredential", job);
        Assert.Contains("-RepetitionInterval (New-TimeSpan -Minutes 15)", job);
        Assert.Contains("-DirectoryOnRequest", job);
    }
}
