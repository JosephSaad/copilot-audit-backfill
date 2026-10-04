using CopilotInsights.Charts;
using CopilotInsights.Infrastructure;
using CopilotInsights.Tests.Review2412A;
using Xunit;

/* A namespace of their own: the other review groups' test files sit beside this one. */
namespace CopilotInsights.Tests.Review2412R1;

/// <summary>The minimum group size, held everywhere a group is drawn or counted (2.4.12, review round 2).</summary>
public class MinimumGroupSizeTests
{
    [Fact]
    public void A_part_of_the_organization_is_sized_by_its_enabled_people()
    {
        var sql = ReportContextFactory.EnabledInPartSql("@root");
        Assert.Contains("dash.OrgClosure", sql);
        Assert.Contains("ec.AncestorUpn = @root", sql);
        Assert.Contains("ep.AccountEnabled = 1", sql);
        /* Both the size check on a requested part and the list of parts to look inside use it; PeopleBelow counts leavers. */
        var context = Source.Read("Infrastructure", "ReportContext.cs");
        Assert.DoesNotContain("SELECT PeopleBelow + 1", context);
        Assert.DoesNotContain("PeopleBelow + 1 >= @min", context);
        Assert.Contains("EnabledInPartSql(\"@root\")", context);
        Assert.Contains("EnabledInPartSql(\"p.Upn\")", context);
    }

    [Fact]
    public void An_adoption_bar_for_smaller_groups_under_the_minimum_together_is_not_drawn()
    {
        var alone = Chart.Adoption([new Chart.AdoptionGroup("Legal", 1, 2), new Chart.AdoptionGroup("Audit", 1, 1)], 5, "t").Value!;
        Assert.DoesNotContain("smaller group", alone);
        var together = Chart.Adoption([new Chart.AdoptionGroup("Legal", 1, 2), new Chart.AdoptionGroup("Audit", 1, 3)], 5, "t").Value!;
        Assert.Contains("2 smaller groups, combined", together);
    }

    [Fact]
    public void Adoption_in_tables_and_the_level_chart_needs_the_minimum_of_license_holders()
    {
        Assert.Contains("AdoptionAtMin(r.ActiveLicensed, r.Licensed)", Source.Read("Pages", "Adoption", "Adoption.cs"));
        Assert.Contains("AdoptionAtMin(u.ActiveLicensed, u.Licensed)", Source.Read("Pages", "Leadership", "Leadership.cs"));
        var workforce = Source.Read("Pages", "Workforce", "Workforce.cs");
        Assert.Contains("SmallGroups.Split(levels.Where(l => l.Licensed > 0), l => l.Licensed, Ctx.MinGroupSize)", workforce);
        Assert.Contains("AdoptionAtMin(mgr.ActiveLicensed, mgr.Licensed)", workforce);
        /* The digest's unit table blanks a combined row that is under the minimum. */
        Assert.Contains("u.Licensed >= c.MinGroupSize ? PctText(", Source.Read("Data", "OpsReports.cs"));
    }

    [Fact]
    public void Agent_people_are_masked_at_every_point_and_a_small_combined_department_is_left_out()
    {
        var agents = Source.Read("Pages", "Agents", "Agents.cs");
        var index = agents[agents.IndexOf("class IndexModel", StringComparison.Ordinal)..agents.IndexOf("class DetailModel", StringComparison.Ordinal)];
        var detail = agents[agents.IndexOf("class DetailModel", StringComparison.Ordinal)..];
        Assert.Contains("PointAtMin(", index);
        Assert.Contains("rows.Where(r => AtLeastMin(r.People) is not null)", index);
        Assert.Contains("AtLeastMin(r.People) is null ? null : Chart.Sparkline(", index);
        Assert.DoesNotContain("(double?)trend.Where", index);
        Assert.Contains("PointAtMin(tr.Where(", detail);
        Assert.Contains(".Where(d => d.People >= Ctx.MinGroupSize)", detail);
    }

    [Fact]
    public void The_leaderboard_compares_only_with_a_group_that_met_the_minimum_before_and_a_folded_no_family_is_not_counted()
    {
        var people = Source.Read("Pages", "PeopleReports.cs");
        Assert.Contains("pr.People >= Ctx.MinGroupSize && pr.Licensed >= Ctx.MinGroupSize", people);
        Assert.Contains("kept.Any(r => r.Name == \"(no family)\")", people);
    }
}

/// <summary>Roles and digests (2.4.12, review round 2).</summary>
public class RolesAndDigestsTests
{
    [Fact]
    public void The_board_pack_shows_security_signals_and_labeled_content_only_with_their_roles()
    {
        var board = Source.Read("Pages", "Leadership", "BoardPack.cs");
        Assert.True(board.IndexOf("Viewer.Has(Roles.Security)", StringComparison.Ordinal) < board.IndexOf("\"Jailbreak attempts\"", StringComparison.Ordinal));
        Assert.True(board.IndexOf("Viewer.Has(Roles.Compliance)", StringComparison.Ordinal) < board.IndexOf("\"Interactions with labeled content\"", StringComparison.Ordinal));
    }

    [Fact]
    public void A_digest_an_administrator_may_not_see_cannot_be_added_or_resumed()
    {
        var source = Source.Read("Pages", "Admin", "Operations.cs");
        Assert.Contains("DigestRefusal(report)", Source.Method(source, "OnPostAddAsync"));
        var toggle = Source.Method(source, "OnPostToggleAsync");
        Assert.Contains("!sub.Enabled && DigestRefusal(sub.Report)", toggle);
        Assert.True(toggle.IndexOf("DigestRefusal", StringComparison.Ordinal) < toggle.IndexOf("UPDATE dash.Subscription", StringComparison.Ordinal));
    }

    [Fact]
    public void Manager_digests_leave_out_small_parts_and_settle_a_refused_one()
    {
        var managers = Source.Method(Source.Read("Infrastructure", "Delivery.cs"), "ManagerDigestsAsync");
        Assert.Contains("ReportContextFactory.EnabledInPartSql(\"p.Upn\") + @\" >= @minGroup", managers);
        Assert.Contains("catch (DigestRefused ex)", managers);
        /* Refused is caught before the generic failure, which would retry it. */
        Assert.True(managers.IndexOf("catch (DigestRefused", StringComparison.Ordinal) < managers.IndexOf("catch (Exception ex)", StringComparison.Ordinal));
        Assert.Contains("status is \"Sent\" or \"Skipped\"", managers);
    }

    [Fact]
    public void A_digest_for_someone_who_has_left_the_directory_is_refused()
    {
        var ops = Source.Read("Data", "OpsReports.cs");
        var start = ops.IndexOf(" DigestAsync(string report", StringComparison.Ordinal);
        var digest = ops[start..ops.IndexOf("switch (report)", start, StringComparison.Ordinal)];
        Assert.Contains("c.OrgWhy == OrgRefusal.NotFound", digest);
        Assert.Contains("throw new DigestRefused(\"the person whose part of the organization", digest);
    }
}
