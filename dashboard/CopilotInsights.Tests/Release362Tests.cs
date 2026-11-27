using Xunit;

namespace CopilotInsights.Tests;

/// <summary>3.6.2: the seat read's findings and activity written in bulk, so tens of thousands of license holders fit in the command timeout.</summary>
public class Release362Tests
{
    static string Repo(params string[] path)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine([d.FullName, .. path]))) return File.ReadAllText(Path.Combine([d.FullName, .. path]));
        throw new FileNotFoundException(string.Join('/', path));
    }

    static string Integrations(string file) => Repo("governance", "src", "Governance.Application", "Integrations", file);

    [Fact]
    public void Findings_are_resolved_against_an_indexed_table_not_a_NOT_IN_over_OPENJSON()
    {
        var writer = Integrations("IntegrationSupport.cs");
        Assert.DoesNotContain("NOT IN (SELECT value FROM OPENJSON", writer);
        Assert.Contains("CREATE TABLE #keep (K nvarchar(300) COLLATE DATABASE_DEFAULT NOT NULL PRIMARY KEY);", writer);
        Assert.Contains("public Task<int> RaiseManyAsync(", writer);
    }

    [Fact]
    public void The_seat_read_raises_findings_without_notices_in_bulk_and_writes_activity_in_one_statement()
    {
        var seats = Integrations("SeatSyncService.cs");
        Assert.Contains("raised = await findings.RaiseManyAsync(uow,", seats);
        Assert.Contains("FROM OPENJSON(@rows) WITH (U nvarchar(256), D date)", seats);
        Assert.DoesNotContain("USING (SELECT @UserPrincipalName AS UserPrincipalName)", seats);   // the old one-person-at-a-time MERGE
    }

    [Fact]
    public void With_seats_only_the_reservation_is_set_on_the_lines_page_and_new_groups_start_as_tracking_groups()
    {
        static string Page(params string[] p) => Repo(["dashboard", "CopilotInsights", "Pages", "Workbench", .. p]);
        Assert.Contains("asp-page-handler=\"SetReservation\"", Page("Registry", "SeatLob.cshtml"));
        Assert.Contains("public bool CanSetReservation => SeatsOnly && (Principal.Can(Permissions.RegistryFinance) || Principal.Can(Permissions.RegistryEdit));", Page("Registry", "Seats.cshtml.cs"));
        Assert.Contains("selected=\"@((e?.Purpose ?? \"LOB_TRACKING\") == kv.Key)\"", Page("Admin", "SeatGroups.cshtml"));
    }

    [Fact]
    public void A_failed_run_says_where_its_error_is_whoever_ran_it()
        => Assert.Contains("a web server's Windows Application log", Integrations("IntegrationSupport.cs"));
}
