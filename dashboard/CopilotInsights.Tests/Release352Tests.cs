using Governance.Web.Pages;
using Xunit;

namespace CopilotInsights.Tests;

/// <summary>3.5.2: anyone in the directory can be picked in the Workbench, before they ever sign in.</summary>
public class Release352Tests
{
    static string Repo(params string[] path)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine([d.FullName, .. path]))) return File.ReadAllText(Path.Combine([d.FullName, .. path]));
        throw new FileNotFoundException(string.Join('/', path));
    }

    [Theory]
    [InlineData("Pat", "%Pat%", "Pat%")]
    [InlineData("  pat   lee ", "%pat%lee%", "pat   lee%")]
    [InlineData("50%_[x]", "%50[%][_][[]x]%", "50[%][_][[]x]%")]   // wildcards typed are searched for, not used
    public void What_is_typed_is_searched_as_words_and_as_the_start_of_a_sign_in(string q, string like, string prefix)
        => Assert.Equal((like, prefix), PeopleModel.Patterns(q));

    [Theory]
    [InlineData(null)]
    [InlineData("a")]
    [InlineData("  ")]
    public void Nothing_is_searched_for_less_than_two_letters(string? q) => Assert.Null(PeopleModel.Patterns(q));

    [Fact]
    public void Every_person_picker_offers_the_directory()
    {
        foreach (var (file, target) in new[]
        {
            (new[] { "Admin", "Roles.cshtml" }, "\"rp\""),
            (new[] { "Registry", "FundingOwnerEdit.cshtml" }, "\"Input_BusinessOwnerPersonId\""),
            (new[] { "Registry", "FundingOwnerEdit.cshtml" }, "\"Input_FinancePartnerPersonId\""),
            (new[] { "Registry", "FundingOwner.cshtml" }, "\"rp\""),
            (new[] { "Registry", "FundingOwner.cshtml" }, "\"dp\""),
            (new[] { "Registry", "Commercial.cshtml" }, "\"cp\""),
            (new[] { "Registry", "ServiceEdit.cshtml" }, "\"Input_OperationalOwnerPersonId\""),
            (new[] { "Registry", "Workload.cshtml" }, "\"po\""),
            (new[] { "Registry", "Workload.cshtml" }, "\"sp\""),
            (new[] { "Registry", "SeatLob.cshtml" }, "\"ap\""),
        })
            Assert.Contains($"<partial name=\"_FindPerson\" model='({target}, ", Repo([.. new[] { "dashboard", "CopilotInsights", "Pages", "Workbench" }, .. file]));
    }

    [Fact]
    public void The_search_is_for_those_who_name_people_and_the_person_is_read_again_on_the_server()
    {
        Assert.Contains("o.Conventions.AuthorizePage($\"{PagesFolder}/People\", Policies.Registry);", Repo("dashboard", "CopilotInsights", "Workbench", "WorkbenchSetup.cs"));
        var page = Repo("dashboard", "CopilotInsights", "Pages", "Workbench", "People.cshtml.cs");
        Assert.Contains("if (!RegistryCommands.MayAddPeople(Me)) return StatusCode(403);", page);
        Assert.Contains("read again from the directory by sign-in, never taken from the request", page);
        Assert.Contains("await run.AuditAsync(uow, \"person.add\"", Repo("governance", "src", "Governance.Application", "Commands", "RegistryCommands.cs"));
    }

    [Fact]
    public void The_workbench_script_stays_ascii()
        => Assert.All(Repo("dashboard", "CopilotInsights", "wwwroot", "workbench.js"), c => Assert.True(c < 128));
}
