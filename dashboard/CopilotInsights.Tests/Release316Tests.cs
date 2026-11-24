using CopilotInsights.Data;
using CopilotInsights.Infrastructure;
using CopilotInsights.Pages;
using Xunit;

namespace CopilotInsights.Tests;

/// <summary>
/// 3.1.6: beta reports (Admin › Reports shown) open only to full administrators and to a Preview grant that ticks them;
/// success stories move forward only, and are quoted by name only with the person's consent.
/// </summary>
public class Release316Tests
{
    static ViewerService.GrantRow Grant(string role, string? pages = null, string? sections = null) =>
        new("me@contoso.com", "User", role, pages, false, null, sections, false);

    static Viewer V(GrantedAccess a, int? level = 4, int max = 0) => new()
    {
        WindowsName = @"CONTOSO\me", Upn = "me@contoso.com", Level = level, InDirectory = true, Roles = a.Roles, MaxLevel = max,
        Admitted = AccessRules.MayUseDashboard(level, a.Roles, max), SectionKeys = a.Sections, OrgWideSections = a.OrgWideSections,
        OrgWideRoles = a.OrgWideRoles, PreviewPaths = a.Preview,
    };

    static readonly IReadOnlySet<string> None = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    static IReadOnlySet<string> Set(params string[] s) => new HashSet<string>(s, StringComparer.OrdinalIgnoreCase);

    static string Repo(params string[] path)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine([d.FullName, .. path]))) return File.ReadAllText(Path.Combine([d.FullName, .. path]));
        throw new FileNotFoundException(string.Join('/', path));
    }

    [Fact]
    public void A_beta_report_is_gone_for_someone_without_a_preview_and_there_for_one_with()
    {
        var plain = V(AccessRules.Resolve([Grant(Roles.Sections, sections: Sections.Leadership)]));
        var preview = V(AccessRules.Resolve([Grant(Roles.Sections, sections: Sections.Leadership), Grant(Roles.Preview, "/leadership/act")]));
        var beta = Set("/leadership/act");

        Assert.True(HiddenReports.IsOff(HiddenReports.ForViewer(None, beta, plain), "/leadership/act", plain));
        Assert.False(HiddenReports.IsOff(HiddenReports.ForViewer(None, beta, preview), "/leadership/act", preview));
        /* Not in their menu without the preview; in it with one. */
        Assert.DoesNotContain(Nav.For(plain, hidden: HiddenReports.ForViewer(None, beta, plain)).SelectMany(g => g.Pages), p => p.Path == "/leadership/act");
        Assert.Contains(Nav.For(preview, hidden: HiddenReports.ForViewer(None, beta, preview)).SelectMany(g => g.Pages), p => p.Path == "/leadership/act");
        /* Other reports are untouched. */
        Assert.False(HiddenReports.IsOff(HiddenReports.ForViewer(None, beta, plain), "/leadership", plain));
    }

    [Fact]
    public void A_full_administrator_sees_every_beta_report_and_a_report_turned_off_stays_off()
    {
        var admin = V(AccessRules.Resolve([Grant(Roles.Admin), Grant(Roles.Sections, sections: Sections.Leadership)]));
        Assert.True(admin.Previews("/leadership/act"));
        Assert.False(HiddenReports.IsOff(HiddenReports.ForViewer(None, Set("/leadership/act"), admin), "/leadership/act", admin));
        /* Turned off and beta: off for everyone, administrators too. */
        Assert.True(HiddenReports.IsOff(HiddenReports.ForViewer(Set("/leadership/act"), Set("/leadership/act"), admin), "/leadership/act", admin));
    }

    [Fact]
    public void A_preview_opens_no_section_and_lets_no_one_past_the_level_limit()
    {
        var a = AccessRules.Resolve([Grant(Roles.Preview, "/leadership/act"), Grant(Roles.Stories)]);
        Assert.Empty(a.Sections);
        Assert.Contains("/leadership/act", a.Preview);
        Assert.False(AccessRules.MayUseDashboard(9, a.Roles, 2));
        /* Without its section, a preview shows nothing: the section check still refuses the page. */
        var v = V(a);
        Assert.False(Nav.SectionOpen("/leadership/act", v));
    }

    [Fact]
    public void A_preview_grant_keeps_only_reports_that_are_beta_now()
    {
        var stored = HiddenReports.FormatKeys(["/leadership/act", "/leadership/costs", "/nowhere"], Set("/leadership/act"));
        Assert.Equal("/leadership/act", stored);
        Assert.Null(HiddenReports.FormatKeys(["/leadership/costs"], Set("/leadership/act")));
        Assert.Equal(["/a", "/b"], HiddenReports.ParseKeys(" /a/ ,/b,,/a"));
        Assert.Equal("What to act on", HiddenReports.Titles("/leadership/act"));
    }

    [Fact]
    public void A_story_only_moves_forward()
    {
        Assert.Equal(SuccessStories.Approved, SuccessStories.Next(SuccessStories.Entered, "approve"));
        Assert.Equal(SuccessStories.Sent, SuccessStories.Next(SuccessStories.Approved, "send"));
        Assert.Equal(SuccessStories.Sent, SuccessStories.Next(SuccessStories.Sent, "send"));          // sent again, on purpose
        Assert.Equal(SuccessStories.Withdrawn, SuccessStories.Next(SuccessStories.Sent, "withdraw"));
        Assert.Equal(SuccessStories.Declined, SuccessStories.Next(SuccessStories.Approved, "decline"));
        foreach (var (from, action) in new[] { (SuccessStories.Declined, "approve"), (SuccessStories.Withdrawn, "approve"), (SuccessStories.Approved, "approve"),
                                               (SuccessStories.Sent, "decline"), (SuccessStories.Declined, "withdraw"), (SuccessStories.Entered, "send") })
            Assert.Throws<ArgumentException>(() => SuccessStories.Next(from, action));
    }

    [Fact]
    public void Only_a_curator_edits_an_approved_story_and_nothing_is_ticked_before_approval()
    {
        Assert.True(SuccessStories.OwnerMayEdit(SuccessStories.Entered));
        Assert.False(SuccessStories.OwnerMayEdit(SuccessStories.Approved));
        Assert.True(SuccessStories.CuratorMayEdit(SuccessStories.Approved));
        Assert.True(SuccessStories.CuratorMayEdit(SuccessStories.Sent));
        Assert.False(SuccessStories.CuratorMayEdit(SuccessStories.Declined));
        Assert.False(SuccessStories.MayInclude(SuccessStories.Entered));
        Assert.True(SuccessStories.MayInclude(SuccessStories.Approved));
    }

    [Theory]
    [InlineData("Dana Lee", "Finance", true, false, 40, 5, "Dana Lee, Finance")]
    [InlineData("Dana Lee", "Finance", false, false, 40, 5, "A colleague in Finance")]
    [InlineData("Dana Lee", "Finance", true, true, 40, 5, "A colleague in Finance")]   // privacy mode: never a name
    [InlineData("Dana Lee", "Legal", false, false, 3, 5, "A colleague")]               // a small department
    [InlineData("Dana Lee", null, false, false, 0, 5, "A colleague")]
    public void A_quote_names_the_person_only_with_their_consent(string name, string? unit, bool mayName, bool privacy, int size, int min, string expected) =>
        Assert.Equal(expected, SuccessStories.Attribution(name, unit, mayName, privacy, size, min));

    [Fact]
    public void A_story_is_checked_and_its_words_stay_out_of_the_audit_record()
    {
        Assert.Throws<ArgumentException>(() => SuccessStories.CleanQuote("  "));
        Assert.Throws<ArgumentException>(() => SuccessStories.CleanQuote(new string('x', SuccessStories.QuoteMax + 1)));
        Assert.Equal("It saved me an hour.", SuccessStories.CleanQuote(" It saved me an hour. "));
        Assert.Equal("(kept with the story)", AuditChange.Masked("quote", "It saved me an hour."));
        Assert.Throws<ArgumentException>(() => SuccessStories.Minutes(-1));
    }

    [Fact]
    public void The_weekly_report_names_people_as_the_dashboard_does_and_stamps_only_a_real_send()
    {
        var script = Repo("tools", "Send-CopilotWeeklyReport.ps1");
        Assert.Contains("function Format-StoryAttribution", script);
        Assert.Contains("'A colleague in ' + $Unit.Trim()", script);
        /* A preview and a send with -To stamp nothing. */
        Assert.Contains("if ($keepRecord -and $stories.Count) {", script);
        Assert.Contains("Complete-ReportStory -Connection $conn -Stories $stories -Week $week", script);
        /* The schema only grows: the roles' checks are widened, never narrowed. */
        var schema = Repo("sql", "Create-CopilotDashboardSchema.sql");
        Assert.Contains("'Sections', 'Preview', 'Stories')", schema);
        Assert.Contains("CREATE TABLE dash.SuccessStory", schema);
    }
}
