using Governance.Application;
using Governance.Application.Commands;
using Governance.Domain;
using Governance.Web.Infrastructure;
using Xunit;

namespace CopilotInsights.Tests;

/// <summary>3.6.0: the Workbench cut down to Copilot seats per line of business (Workbench.Scope), the rest behind one switch.</summary>
public class Release360Tests
{
    static string Repo(params string[] path)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine([d.FullName, .. path]))) return File.ReadAllText(Path.Combine([d.FullName, .. path]));
        throw new FileNotFoundException(string.Join('/', path));
    }

    static HashSet<string> Set(params string[] s) => new(s, StringComparer.OrdinalIgnoreCase);

    [Theory]
    [InlineData(null, true)]       // a database without the setting yet: seats only
    [InlineData("", true)]
    [InlineData("SEATS", true)]
    [InlineData(" full ", false)]
    [InlineData("FULL", false)]
    public void Seats_only_unless_the_setting_says_full(string? value, bool seats) => Assert.Equal(seats, WorkbenchScope.SeatsOnly(value));

    [Theory]
    [InlineData("/workbench/Registry/Seats", true)]
    [InlineData("/workbench/Registry/Seats/0f8fad5b-d9cb-469f-a165-70867728950e", true)]
    [InlineData("/Workbench/registry/fundingowners/Edit", true)]
    [InlineData("/workbench/Reports/SeatChargeback", true)]
    [InlineData("/workbench/Admin/SeatGroups", true)]
    [InlineData("/workbench/Admin/Roles", true)]
    [InlineData("/workbench/Admin/Settings", true)]
    [InlineData("/workbench/Operations/Sync", true)]
    [InlineData("/workbench/People", true)]
    [InlineData("/workbench/AccessDenied", true)]
    [InlineData("/workbench", false)]
    [InlineData("/workbench/Start", false)]
    [InlineData("/workbench/Requests/New/J9", false)]
    [InlineData("/workbench/Registry/Workloads", false)]
    [InlineData("/workbench/Registry/Envelopes", false)]
    [InlineData("/workbench/Registry/SeatsExtra", false)]    // a prefix is not enough: whole segments
    [InlineData("/workbench/Reports/Kpis", false)]
    [InlineData("/workbench/Operations/Findings", true)]   // open with seats only: the tenant-wide seat findings
    [InlineData("/workbench/Admin/Intake", false)]
    [InlineData("/workbench/Queues/Intake", false)]
    public void Only_the_seat_pages_open_with_seats_only(string path, bool open)
    {
        Assert.Equal(open, WorkbenchScope.PageOpen(path, seatsOnly: true));
        Assert.True(WorkbenchScope.PageOpen(path, seatsOnly: false));
        Assert.True(WorkbenchScope.PageOpen(WorkbenchScope.Home, seatsOnly: true));
    }

    [Theory]
    [InlineData("OutboxDispatcher", true)]
    [InlineData("DirectorySync", true)]
    [InlineData("SeatSync", true)]
    [InlineData("Retention", true)]
    [InlineData("SlaTimer", false)]
    [InlineData("EntitlementSync", false)]
    [InlineData("AccessReviewImport", false)]     // the job that needs AccessReview.Read.All
    [InlineData("LicensingRead", false)]
    [InlineData("ItsmPoller", false)]
    [InlineData("CampaignScheduler", false)]
    [InlineData("DigestBuilder", false)]
    public void The_worker_runs_only_the_seat_jobs(string job, bool runs)
    {
        Assert.Equal(runs, WorkbenchScope.JobRuns(job, seatsOnly: true));
        Assert.True(WorkbenchScope.JobRuns(job, seatsOnly: false));
    }

    [Fact]
    public void Every_job_the_worker_has_is_named_where_it_is_decided()
    {
        var program = Repo("governance", "src", "Governance.Worker", "Program.cs");
        var jobs = Repo("governance", "src", "Governance.Worker", "Jobs", "Jobs.cs") + Repo("governance", "src", "Governance.Worker", "Jobs", "SeatJobs.cs")
                   + Repo("governance", "src", "Governance.Worker", "Jobs", "SlaTimerJob.cs") + Repo("governance", "src", "Governance.Worker", "Jobs", "CampaignSchedulerJob.cs");
        foreach (var j in WorkbenchScope.SeatJobs) Assert.Contains($"Name => \"{j}\"", jobs);
        Assert.Contains("typeof(SeatSyncJob)", program);
        Assert.Contains("if (await InScopeAsync(stoppingToken)) await RunGuardedAsync(stoppingToken);", Repo("governance", "src", "Governance.Worker", "Jobs", "JobBase.cs"));
    }

    [Theory]
    [InlineData("seats", "SEATS")]
    [InlineData(" Full ", "FULL")]
    public void The_scope_is_kept_in_capitals(string value, string kept) => Assert.Equal(kept, WorkbenchSettings.Check(WorkbenchScope.Key, value));

    [Fact]
    public void The_scope_refuses_anything_else()
        => Assert.Throws<Governance.Application.Data.DomainException>(() => WorkbenchSettings.Check(WorkbenchScope.Key, "SOME"));

    [Fact]
    public void Seats_only_shows_one_section_and_each_page_keeps_its_rule()
    {
        static IReadOnlyList<string> Pages(params string[] appRoles) =>
            WorkbenchNav.For(new WebPrincipal { IsAuthenticated = true, AppRoles = Set(appRoles) }, on: true, seatsOnly: true).SelectMany(g => g.Pages).Select(p => p.Title).ToList();
        /* FinOps-like: central, with the tenant-wide seat view (Copilot seats keeps its own rule). */
        var all = WorkbenchNav.For(new WebPrincipal { IsAuthenticated = true, IsCentral = true, Permissions = Set("seats.view"),
            AppRoles = Set(AppRoles.Central, AppRoles.Operator, AppRoles.Auditor, AppRoles.Admin) }, true, true);
        Assert.Equal(["Copilot Seat Management"], all.Select(g => g.Persona));
        Assert.Equal(["Copilot seats", "Lines of business", "Reservation summary", "Hierarchy audit", "Exception report", "New hire report", "Detailed audit", "Duplicate review", "Hierarchy mapping",
            "Seat groups", "Workbench roles", "Settings", "Seat findings", "Sync and freshness"], all.SelectMany(g => g.Pages).Select(p => p.Title));
        foreach (var p in all.SelectMany(g => g.Pages)) Assert.True(WorkbenchScope.PageOpen(p.Path, true), p.Path);
        Assert.Empty(Pages());
        Assert.Empty(Pages(AppRoles.User));                         // requests and approvals are closed
        Assert.Equal(["Reservation summary", "Hierarchy audit", "Exception report", "New hire report", "Detailed audit", "Duplicate review", "Hierarchy mapping"], Pages(AppRoles.Auditor));
        Assert.DoesNotContain("Settings", Pages(AppRoles.Registry));
        // With the whole Workbench, the menu is as it was.
        Assert.Contains("Ownership and approvals", WorkbenchNav.For(new WebPrincipal { IsAuthenticated = true, AppRoles = Set(AppRoles.User) }, true).Select(g => g.Persona));
    }

    [Fact]
    public void Closed_pages_are_closed_by_address_too_and_the_pages_leave_out_links_to_them()
    {
        var setup = Repo("dashboard", "CopilotInsights", "Workbench", "WorkbenchSetup.cs");
        Assert.Contains("b.UseMiddleware<WorkbenchScopeMiddleware>();", setup);
        Assert.True(setup.IndexOf("UseMiddleware<PrincipalMiddleware>", StringComparison.Ordinal) < setup.IndexOf("UseMiddleware<WorkbenchScopeMiddleware>", StringComparison.Ordinal));
        var mode = Repo("dashboard", "CopilotInsights", "Workbench", "WorkbenchMode.cs");
        Assert.Contains("http.Request.PathBase + WorkbenchScope.Home", mode);          // under a virtual directory too
        Assert.Contains("TimeSpan.FromSeconds(30)", mode);
        static string Page(params string[] p) => Repo(["dashboard", "CopilotInsights", "Pages", "Workbench", .. p]);
        Assert.Contains("!Model.SeatsOnly && r.PublicId is Guid id", Page("Registry", "Seats.cshtml"));
        Assert.Contains("Model.CanAskChange && !Model.SeatsOnly", Page("Registry", "SeatLob.cshtml"));
        Assert.Contains("@if (d.SeatsOnly)", Page("Registry", "FundingOwner.cshtml"));
        Assert.Contains("<input type=\"hidden\" asp-for=\"Input.FinancePartnerPersonId\" />", Page("Registry", "FundingOwnerEdit.cshtml"));  // kept, not wiped
        Assert.Contains("WorkbenchMode.Forget(cache)", Page("Admin", "Admin.cshtml.cs"));
    }

    [Fact]
    public void Existing_installs_get_seats_only_and_new_lines_of_business_are_active_at_once()
    {
        var seed = Repo("governance", "src", "Governance.Database", "Scripts", "200-seed-reference.sql");
        Assert.Contains("('Workbench.Scope',           N'SEATS'", seed);
        Assert.Contains("WHEN NOT MATCHED THEN INSERT", seed);                       // a FULL chosen later is never put back
        var commands = Repo("governance", "src", "Governance.Application", "Commands", "RegistryCommands.cs");
        Assert.Contains("'SEATS') = 'FULL' THEN 'DRAFT' ELSE 'ACTIVE' END", commands);
        Assert.Contains("seatsOnly ? new List<ChecklistItem>()", Repo("governance", "src", "Governance.Application", "Queries", "RegistryQueries.cs"));
    }
}
