using CopilotInsights.Data;
using Xunit;

namespace CopilotInsights.Tests;

/// <summary>
/// 3.2.1: a report that means nothing until something is chosen on it (Compare two teams' two managers, an agent's or an
/// app's detail) can be subscribed to only once it is chosen; one made before is paused, saying why.
/// </summary>
public class Release321Tests
{
    static string Repo(params string[] path)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine([d.FullName, .. path]))) return File.ReadAllText(Path.Combine([d.FullName, .. path]));
        throw new FileNotFoundException(string.Join('/', path));
    }

    [Fact]
    public void Compare_two_teams_needs_both_managers()
    {
        Assert.Equal("choose the first manager and the second manager on the report first: until then it has nothing to send.",
            Subscriptions.Missing("/leadership/compare", null));
        Assert.Contains("the second manager", Subscriptions.Missing("/leadership/compare", "?a=ann%40contoso.com"));
        Assert.DoesNotContain("the first manager", Subscriptions.Missing("/leadership/compare", "a=ann%40contoso.com"));
        Assert.Contains("the first manager", Subscriptions.Missing("/leadership/compare", "?a=%20&b=bob%40contoso.com"));
        Assert.Null(Subscriptions.Missing("/leadership/compare", "?d=30&a=ann%40contoso.com&b=bob%40contoso.com"));
        Assert.Null(Subscriptions.Missing("/Leadership/Compare/", "a=ann&b=bob"));
    }

    [Fact]
    public void An_agent_or_app_detail_needs_its_agent_or_app()
    {
        Assert.Contains("an agent", Subscriptions.Missing("/agents/detail", "?d=30"));
        Assert.Null(Subscriptions.Missing("/agents/detail", "?agent=12"));
        Assert.Null(Subscriptions.Missing("/agents/detail", "?inv=abc"));
        Assert.Contains("an app", Subscriptions.Missing("/apps/detail", ""));
        Assert.Null(Subscriptions.Missing("/apps/detail", "app=3"));
    }

    [Fact]
    public void A_report_that_needs_nothing_is_never_held_back()
    {
        foreach (var p in new[] { "/leadership", "/leadership/act", "/adoption", "/agents", "/apps", "/agents/children", "/", null })
            Assert.Null(Subscriptions.Missing(p, null));
    }

    [Fact]
    public void Each_report_that_needs_a_choice_is_a_real_page_and_its_choice_is_kept_with_the_subscription()
    {
        foreach (var (path, need) in Subscriptions.Needs)
        {
            Assert.NotNull(Pages.Nav.Groups.SelectMany(g => g.Pages).FirstOrDefault(p => p.Path == path));
            foreach (var key in need.Items.SelectMany(i => i.Keys))
                Assert.Equal($"{key}=x", Subscriptions.KeepQuery([new(key, "x")]));
        }
        /* The three that can be subscribed to today; Person record names a person, so it is not sent. */
        Assert.NotNull(Subscriptions.Report("/leadership/compare"));
        Assert.NotNull(Subscriptions.Report("/agents/detail"));
        Assert.NotNull(Subscriptions.Report("/apps/detail"));
        Assert.Null(Subscriptions.Report("/compliance/person"));
    }

    [Fact]
    public void The_report_the_subscribe_page_the_save_and_the_sender_all_check_it()
    {
        var report = Repo("dashboard", "CopilotInsights", "Pages", "Shared", "_Report.cshtml");
        Assert.Contains("Subscriptions.Missing(path, Context.Request.QueryString.Value)", report);
        Assert.Contains("class=\"subscribe off\"", report);

        var page = Repo("dashboard", "CopilotInsights", "Pages", "Subscriptions.cs");
        Assert.Contains("Subscriptions.Missing(report.Path, Request.QueryString.Value)", page);
        Assert.Contains("if (Subscriptions.Missing(report.Path, kept) is { } missing) throw new ArgumentException(missing);", page);

        var sender = Repo("dashboard", "CopilotInsights", "Infrastructure", "SubscriptionSender.cs");
        Assert.Contains("Subscriptions.Missing(s.Path, s.Query)", sender);
    }

    /* Each page in Needs shows only its picker until the choice is made: if one stops doing that, it no longer belongs. */
    [Theory]
    [InlineData("Leadership/Compare.cs", "if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return;")]
    [InlineData("Agents/Agents.cs", "if (key is null) return;")]
    [InlineData("Apps/Apps.cs", "if (key is null) return;")]
    public void The_pages_listed_still_wait_for_their_choice(string file, string wait) =>
        Assert.Contains(wait, Repo(["dashboard", "CopilotInsights", "Pages", .. file.Split('/')]));
}
