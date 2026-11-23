using Xunit;

namespace CopilotInsights.Tests;

/// <summary>2.7.5: headline figures that name a group open it, and figures that two pages count differently say why.</summary>
public class Release275Tests
{
    static string Root()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(d.FullName, "CHANGELOG.md"))) d = d.Parent!;
        return d.FullName;
    }

    static string Page(params string[] p) => File.ReadAllText(Path.Combine([Root(), "dashboard", "CopilotInsights", "Pages", .. p]));

    [Theory]
    [InlineData("Leadership/Leadership.cs", "\"Unit with the highest adoption\"", "Href: Inside(ranked[0])")]
    [InlineData("Leadership/Leadership.cs", "\"Unit with the lowest adoption\"", "Href: Inside(ranked[^1])")]
    [InlineData("Adoption/Adoption.cs", "\"Department with the highest adoption\"", "Href: Only(ranked[0])")]
    [InlineData("Adoption/Adoption.cs", "\"Department with the lowest adoption\"", "Href: Only(ranked[^1])")]
    public void A_figure_naming_a_group_opens_it(string file, string label, string href)
    {
        var line = Page(file.Split('/')).Split('\n').Single(l => l.Contains("Kpis.Add(new(" + label));
        Assert.Contains(href, line);
    }

    [Fact]
    public void Department_names_in_the_chart_and_table_open_the_department()
    {
        var page = Page("Adoption", "Adoption.cs");
        Assert.Contains("new Chart.AdoptionGroup(r.Department, r.ActiveLicensed, r.Licensed, Pick(r))", page);
        Assert.Contains("Pick(r) is { } href ? new Link(r.Department, href) : r.Department", page);
        Assert.Contains("dept is null && r.Department != \"Smaller departments\"", page);
    }

    [Fact]
    public void Agent_inventory_without_a_workbook_opens_each_agent()
    {
        var page = Page("OperationsReports.cs");
        Assert.Contains("SELECT a.AgentKey, a.AgentName, a.AgentSource", page);
        Assert.Contains("new Link(r.AgentName + (r.Named ? \"\" : \" (unnamed)\"), Ctx.Url(\"/agents/detail\", extra: $\"agent={r.AgentKey}\"))", page);
    }

    [Fact]
    public void Agent_credits_suggestion_opens_better_with_a_license()
    {
        var page = Page("Agents", "Credits.cs");
        Assert.Contains("var betterLicensed = Open(\"/licensing/license-for-credits\");", page);
        Assert.Contains("new Link(\"Its users each cost more than a license: see Licensing › Better with a license\", betterLicensed)", page);
    }

    [Theory]
    [InlineData("SPE[_]E5[_]NOPSTNCONF", "Microsoft 365 E5", "Microsoft 365 E5 (SPE_E5_NOPSTNCONF), a user a month")]
    [InlineData("SPE[_]E7%", "Microsoft 365 E7", "Microsoft 365 E7 (SPE_E7*), a user a month")]
    [InlineData("Office[_]365[_]E3[_](no[_]Teams)", "Office 365 E3", "Office 365 E3 (Office_365_E3_(no_Teams)), a user a month")]
    public void A_suite_price_names_its_SKU(string pattern, string suite, string label) =>
        Assert.Equal(label, CopilotInsights.Data.Assumptions.Suite(pattern, suite).Label);

    [Theory]
    [InlineData("Leadership/CreditChargeback.cs", "NotSetUpAsync(\"Credit chargeback cannot be kept here yet\", \"2.7.2\", \"dash.CreditRoute\", \"audit.PowerPlatformCreditDay\")")]
    [InlineData("Forecasting/CreditCommitment.cs", "NotSetUpAsync(\"Commitments cannot be recorded here yet\", \"2.7.2\", \"dash.CreditCommitment\", \"dash.CreditRoute\", \"audit.PowerPlatformCreditDay\")")]
    [InlineData("Leadership/Reservations.cs", "NotSetUpAsync(\"Reservations cannot be recorded here yet\", \"2.6.8\", \"dash.Reservation\", \"dash.fnReservationMember\")")]
    [InlineData("Licensing/ReservationHolders.cs", "NotSetUpAsync(\"Reservations cannot be recorded here yet\", \"2.6.8\", \"dash.Reservation\", \"dash.fnReservationMember\")")]
    public void A_page_not_set_up_names_what_is_missing_and_where(string file, string call)
    {
        /* A client ran Install-CopilotAudit.ps1 -Dashboard on 2.7.4 and Credit chargeback still said to run it. */
        Assert.Contains(call, Page(file.Split('/')));
        var page = Page("InsightsPage.cs");
        Assert.Contains("SELECT DB_NAME(), USER_NAME(), STRING_AGG(n, N'|') FROM (VALUES {names}) AS o(n) WHERE OBJECT_ID(o.n) IS NULL;", page);
        Assert.Contains("add it to the CopilotInsightsApp role in {database}", page);
    }

    [Fact]
    public void The_leaderboard_names_do_not_open_departments()
    {
        /* Inside a team, Departments combined Sales East (163 people) with a department of one, so its name was not there. */
        Assert.DoesNotContain("Open(\"/adoption/departments\"", Page("PeopleReports.cs"));
    }

    [Fact]
    public void Countries_rank_both_ends_among_those_large_enough()
    {
        var page = Page("Regions", "Regions.cs");
        Assert.Contains("c.Name != \"Smaller groups\" && c.Licensed >= Ctx.MinGroupSize", page);
        Assert.DoesNotContain("countries.Where(c => c.Licensed > 0)", page);
    }

    [Fact]
    public void Guests_say_how_many_licensed_guests_were_confirmed_as_people()
    {
        /* Guests counted 2 licensed guests and Copilot on non-human accounts 1: an administrator had confirmed the other. */
        var page = Page("PeopleReports.cs");
        Assert.Contains("COUNT(CASE WHEN ld.UserKey IS NOT NULL AND g.Ok = 1 THEN 1 END) AS Confirmed", page);
        Assert.Contains("confirmed as a person by an administrator", page);
        Assert.Contains("Open(Nav.NonHuman, \"why=guest\")", page);
    }

    [Fact]
    public void Rollout_waves_lead_to_demand_and_say_their_window()
    {
        var page = Page("Forecasting", "Planning.cs");
        Assert.Contains("Href: dept is null ? Open(\"/licensing/demand\") : null", page);
        Assert.Contains("used Copilot Chat in the last 30 days", page);
    }

    [Fact]
    public void Chargeback_names_its_used_licenses()
    {
        var page = Page("Licensing", "Operations.cs");
        Assert.Contains("Kpis.Add(new(\"Licenses used in the period\"", page);
    }
}
