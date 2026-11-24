using CopilotInsights.Charts;
using CopilotInsights.Data;
using CopilotInsights.Infrastructure;
using CopilotInsights.Pages;
using Xunit;

namespace CopilotInsights.Tests;

/// <summary>
/// 3.2.0: report subscriptions (a report's figures and charts by email, on the person's own schedule, never its lists)
/// and the Preview role, which opens every beta report.
/// </summary>
public class Release320Tests
{
    static ViewerService.GrantRow Grant(string role, string? pages = null, string? sections = null) =>
        new("me@contoso.com", "User", role, pages, false, null, sections, false);

    static Viewer V(GrantedAccess a, int? level = 4) => new()
    {
        WindowsName = @"CONTOSO\me", Upn = "me@contoso.com", Level = level, InDirectory = true, Roles = a.Roles,
        Admitted = AccessRules.MayUseDashboard(level, a.Roles, 0), SectionKeys = a.Sections, OrgWideSections = a.OrgWideSections,
        OrgWideRoles = a.OrgWideRoles, PreviewPaths = a.Preview, PreviewsAll = a.PreviewAll,
    };

    static readonly IReadOnlySet<string> None = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    static IReadOnlySet<string> Set(params string[] s) => new HashSet<string>(s, StringComparer.OrdinalIgnoreCase);

    static string Repo(params string[] path)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine([d.FullName, .. path]))) return File.ReadAllText(Path.Combine([d.FullName, .. path]));
        throw new FileNotFoundException(string.Join('/', path));
    }

    static readonly TimeZoneInfo Eastern = TimeZoneInfo.FindSystemTimeZoneById(OperatingSystem.IsWindows() ? "Eastern Standard Time" : "America/New_York");

    /* ---- Preview: every beta report ---------------------------------------------------------------------------- */

    [Fact]
    public void The_Preview_role_opens_every_beta_report_in_the_sections_held()
    {
        var holder = V(AccessRules.Resolve([Grant(Roles.Sections, sections: Sections.Leadership), Grant(Roles.Preview)]));
        Assert.True(holder.PreviewsAll);
        var beta = Set("/leadership/act", "/leadership/mix");
        foreach (var p in beta)
            Assert.False(HiddenReports.IsOff(HiddenReports.ForViewer(None, beta, holder), p, holder));
        /* Still not past its sections: Adoption's beta report stays refused without Adoption. */
        Assert.False(Nav.SectionOpen(Nav.Groups.First(g => g.Key == Sections.Adoption).Pages[0].Path, holder));
        /* Nor past the level limit. */
        Assert.False(AccessRules.MayUseDashboard(9, AccessRules.Resolve([Grant(Roles.Preview)]).Roles, 2));
    }

    [Fact]
    public void A_Preview_grant_from_before_names_its_reports_only()
    {
        var older = V(AccessRules.Resolve([Grant(Roles.Sections, sections: Sections.Leadership), Grant(Roles.Preview, "/leadership/act")]));
        Assert.False(older.PreviewsAll);
        Assert.True(older.Previews("/leadership/act"));
        Assert.False(older.Previews("/leadership/mix"));
    }

    [Fact]
    public void Preview_is_ticked_with_the_other_roles_and_the_per_report_step_is_gone()
    {
        var admin = Repo("dashboard", "CopilotInsights", "Pages", "Admin", "Admin.cs");
        Assert.Contains("r == Roles.Organization || r == Roles.Stories || r == Roles.Preview ||", admin);
        Assert.DoesNotContain("Step(\"Beta reports they preview (optional)\"", admin);
        /* Ticking Preview for someone whose older grant names reports opens every one. */
        Assert.Contains("Role = 'Preview' AND Pages IS NOT NULL", admin);
    }

    /* ---- Subscription rules ------------------------------------------------------------------------------------ */

    [Theory]
    [InlineData(7, true)]
    [InlineData(14, true)]
    [InlineData(30, true)]
    [InlineData(90, false)]
    [InlineData(365, false)]
    [InlineData(0, false)]
    public void Daily_is_offered_up_to_30_days_and_weekly_and_monthly_always(int days, bool daily)
    {
        Assert.Equal(daily, Subscriptions.IsAllowed(days, Subscriptions.Daily));
        Assert.True(Subscriptions.IsAllowed(days, Subscriptions.Weekly));
        Assert.True(Subscriptions.IsAllowed(days, Subscriptions.Monthly));
    }

    [Fact]
    public void A_subscription_is_checked_before_it_is_kept()
    {
        var at = new TimeSpan(8, 0, 0);
        var earliest = new TimeSpan(7, 30, 0);
        Assert.Null(Subscriptions.Problem(30, "Daily", null, null, at, earliest));
        Assert.Contains("weekly or monthly", Subscriptions.Problem(90, "Daily", null, null, at, earliest));
        Assert.NotNull(Subscriptions.Problem(45, "Weekly", 1, null, at, earliest));        // not a period a report offers
        Assert.NotNull(Subscriptions.Problem(30, "Weekly", null, null, at, earliest));     // no day
        Assert.NotNull(Subscriptions.Problem(30, "Monthly", null, 29, at, earliest));      // not every month has a 29th
        Assert.NotNull(Subscriptions.Problem(30, "Daily", null, null, new TimeSpan(7, 0, 0), earliest));   // before the earliest
        Assert.NotNull(Subscriptions.Problem(30, "Daily", null, null, new TimeSpan(8, 15, 0), earliest));  // not a half hour
        Assert.NotNull(Subscriptions.Problem(30, "Hourly", null, null, at, earliest));
    }

    [Fact]
    public void The_times_offered_are_every_half_hour_from_the_earliest_to_ten_at_night()
    {
        var slots = Subscriptions.Slots(new TimeSpan(7, 30, 0));
        Assert.Equal(new TimeSpan(7, 30, 0), slots[0]);
        Assert.Equal(new TimeSpan(22, 0, 0), slots[^1]);
        Assert.Equal(30, slots.Count);
        Assert.Equal(new TimeSpan(7, 30, 0), Subscriptions.Slots(new TimeSpan(7, 10, 0))[0]);   // up to the next half hour
    }

    [Fact]
    public void Daily_skips_the_weekend()
    {
        /* Friday 2026-11-27 09:00 Eastern, after an 08:00 send: the next is Monday 08:00. */
        var after = TimeZoneInfo.ConvertTimeToUtc(new DateTime(2026, 11, 27, 9, 0, 0), Eastern);
        var next = TimeZoneInfo.ConvertTimeFromUtc(Subscriptions.NextDue("Daily", null, null, new TimeSpan(8, 0, 0), after, Eastern), Eastern);
        Assert.Equal(new DateTime(2026, 11, 30, 8, 0, 0), next);
        /* Before the time on a weekday: the same day. */
        var early = TimeZoneInfo.ConvertTimeToUtc(new DateTime(2026, 11, 25, 7, 0, 0), Eastern);
        Assert.Equal(new DateTime(2026, 11, 25, 8, 0, 0), TimeZoneInfo.ConvertTimeFromUtc(Subscriptions.NextDue("Daily", null, null, new TimeSpan(8, 0, 0), early, Eastern), Eastern));
    }

    [Fact]
    public void Weekly_and_monthly_go_on_their_day_at_their_time_in_the_dashboards_zone()
    {
        var after = TimeZoneInfo.ConvertTimeToUtc(new DateTime(2026, 11, 24, 12, 0, 0), Eastern);   // a Tuesday
        Assert.Equal(new DateTime(2026, 11, 30, 7, 30, 0), TimeZoneInfo.ConvertTimeFromUtc(Subscriptions.NextDue("Weekly", 1, null, new TimeSpan(7, 30, 0), after, Eastern), Eastern));
        Assert.Equal(new DateTime(2026, 12, 1, 9, 0, 0), TimeZoneInfo.ConvertTimeFromUtc(Subscriptions.NextDue("Monthly", null, 1, new TimeSpan(9, 0, 0), after, Eastern), Eastern));
        /* The clocks going forward (8 March 2026, 02:00 to 03:00): a time that does not exist that day is the hour after. */
        var dst = TimeZoneInfo.ConvertTimeToUtc(new DateTime(2026, 3, 7, 12, 0, 0), Eastern);
        var sunday = Subscriptions.NextDue("Weekly", 0, null, new TimeSpan(2, 30, 0), dst, Eastern);
        Assert.Equal(new DateTime(2026, 3, 8, 3, 30, 0), TimeZoneInfo.ConvertTimeFromUtc(sunday, Eastern));
    }

    [Fact]
    public void It_waits_for_the_nights_refresh_until_the_wait_time_then_goes_late()
    {
        var today8 = TimeZoneInfo.ConvertTimeToUtc(new DateTime(2026, 11, 24, 8, 0, 0), Eastern);
        var refreshedToday = TimeZoneInfo.ConvertTimeToUtc(new DateTime(2026, 11, 24, 4, 0, 0), Eastern);
        var refreshedYesterday = TimeZoneInfo.ConvertTimeToUtc(new DateTime(2026, 11, 23, 4, 0, 0), Eastern);
        Assert.Equal((true, false), Subscriptions.Ready(refreshedToday, today8, new TimeSpan(12, 0, 0), Eastern));
        Assert.Equal((false, false), Subscriptions.Ready(refreshedYesterday, today8, new TimeSpan(12, 0, 0), Eastern));
        Assert.Equal((true, true), Subscriptions.Ready(refreshedYesterday, today8.AddHours(4.5), new TimeSpan(12, 0, 0), Eastern));
        /* More than 12 hours late (the site was down) waits for the next one. */
        Assert.True(Subscriptions.Missed(today8, today8.AddHours(13)));
        Assert.False(Subscriptions.Missed(today8, today8.AddHours(2)));
    }

    [Fact]
    public void Only_reports_that_are_not_lists_of_people_or_admin_pages_can_be_subscribed_to()
    {
        Assert.NotNull(Subscriptions.Report("/leadership"));
        Assert.NotNull(Subscriptions.Report("/leadership/act/"));
        Assert.Null(Subscriptions.Report("/admin/health"));
        Assert.Null(Subscriptions.Report("/group"));                 // several tenants
        Assert.Null(Subscriptions.Report("/stories"));               // not a report
        Assert.Null(Subscriptions.Report("/nowhere"));
        var listPage = Nav.Groups.SelectMany(g => g.Pages).First(p => p.Role is not null);
        Assert.Null(Subscriptions.Report(listPage.Path));            // a list that names people
    }

    [Fact]
    public void A_subscription_keeps_the_reports_filters_but_not_the_period_paging_or_the_form()
    {
        var q = Subscriptions.KeepQuery([new("org", "ada@contoso.com"), new("d", "90"), new("handler", "csv"), new("p", "3"), new("dept", "R&D"), new("bad key!", "x")]);
        Assert.Equal("org=ada%40contoso.com&dept=R%26D", q);
        Assert.Equal("/leadership?d=90&org=ada%40contoso.com", Subscriptions.Address("/leadership", 90, "org=ada%40contoso.com"));
        Assert.Null(Subscriptions.KeepQuery([new("d", "30")]));
    }

    /* ---- The email: figures and charts, never the lists ---------------------------------------------------------- */

    [Fact]
    public void A_panels_charts_are_found_with_their_legend_and_notes_and_its_tables_are_not()
    {
        var x = new Axis(["w/e 1 Nov", "w/e 8 Nov", "w/e 15 Nov"]) { Note = "The two newest days are not drawn." };
        var line = Chart.Line(x, [new Series("Active", [1, 2, 3], 1), new Series("Licensed", [4, 5, 6], 2)], Fmt.Count, "trend").Value!;
        var html = "<table><tr><td>Ada Lovelace</td></tr></table>" + line;
        var found = ReportEmail.Charts(html);
        Assert.Single(found);
        Assert.Equal(["Active", "Licensed"], found[0].Legend.Select(l => l.Name));
        Assert.Equal("s2", found[0].Legend[1].Token);
        Assert.Contains("The two newest days are not drawn.", found[0].Notes);
        Assert.DoesNotContain("Ada", found[0].Svg);
        Assert.Empty(ReportEmail.Charts("<table><tr><td>only a list</td></tr></table>"));
    }

    [Fact]
    public void A_charts_path_is_read_with_its_curves()
    {
        var segs = ChartImage.Segments("M10,20 H30 V40 Q50,40 50,60 Z");
        Assert.Equal(['M', 'L', 'L', 'C', 'Z'], segs.Select(s => s.Op));
        Assert.Equal([30f, 20f, 30f, 40f], segs[2].Pts);
        Assert.Equal(50f, segs[3].Pts[6]);
        Assert.Equal(60f, segs[3].Pts[7]);
    }

    [Fact]
    public void A_chart_is_drawn_as_a_png_on_windows()
    {
        if (!OperatingSystem.IsWindows()) return;   // GDI+: Windows only; elsewhere the email keeps its figures and link
        var x = new Axis(["1 Nov", "2 Nov", "3 Nov", "4 Nov"]);
        var svgs = new[]
        {
            Chart.Line(x, [new Series("Active", [1, 4, 2, 5], 1)], Fmt.Count, "trend").Value!,
            Chart.Bars([new Bar("Finance", 0.4, Shown: "4 of 10"), new Bar("Sales", 0.7)], Fmt.Percent, "rank").Value!,
            Chart.Columns(x, [new Series("Chat", [1, 2, 3, 4], 1), new Series("Agents", [2, 2, 2, 2], 3)], Fmt.Count, "mix").Value!,
            Chart.Heatmap(["Mon", "Tue"], x, new double?[,] { { 1, 2, 3, null }, { 4, 5, 6, 7 } }, Fmt.Count, "heat").Value!,
        };
        foreach (var html in svgs)
        {
            var svg = ReportEmail.Charts(html).Single().Svg;
            var png = ChartImage.Png(svg);
            Assert.NotNull(png);
            Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, png![..4]);
            /* Drawn at twice the view box: its width is in the PNG header (bytes 16 to 19, big-endian). */
            var width = (png[16] << 24) | (png[17] << 16) | (png[18] << 8) | png[19];
            Assert.Equal(ChartImage.Size(svg)!.Value.Width * 2, width);
        }
    }

    [Fact]
    public void The_email_is_built_from_the_page_its_lists_left_out_and_sent_to_the_person_only()
    {
        var sender = Repo("dashboard", "CopilotInsights", "Infrastructure", "SubscriptionSender.cs");
        /* Built as the person, at the send, through the page's own checks. */
        Assert.Contains("WithoutSignInAsync(s.Upn, s.SignInName, s.SignedInWith, ct)", sender);
        Assert.Contains("BuildForEmailAsync()", sender);
        /* Claimed once across web servers. */
        Assert.Contains("INSERT INTO dash.ReportSubscriptionSend (SubscriptionId, DueUtc, Server)", sender);
        Assert.Contains("ex.Number is 2627 or 2601", sender);
        /* To the person's own address only. */
        Assert.Contains("MailConfig.Recipients(address)", sender);
        var page = Repo("dashboard", "CopilotInsights", "Pages", "InsightsPage.cs");
        Assert.Contains("await S.Audit.WriteAsync(Viewer, \"Email\", Request.Path, Ctx.ScopeLabel", page);
        /* A send now is kept apart from the request that asked for it. */
        Assert.Contains("ExecutionContext.SuppressFlow()", sender);
    }

    [Fact]
    public void The_schema_adds_the_two_tables_and_settings_only()
    {
        var sql = Repo("sql", "Create-CopilotDashboardSchema.sql");
        Assert.Contains("CREATE TABLE dash.ReportSubscription", sql);
        Assert.Contains("CONSTRAINT UQ_DashReportSubscriptionSend_Due UNIQUE (SubscriptionId, DueUtc)", sql);
        foreach (var s in new[] { "ReportSubscriptions", "SubscriptionsPerPerson", "SubscriptionEarliest", "SubscriptionWaitUntil" })
            Assert.Contains($"WHERE Name = N'{s}')", sql);
        Assert.Contains(DataStamp.Live, t => t.Table == "dash.ReportSubscription");
    }
}

public class Release320Dump
{
    [Fact]
    public void Dump()
    {
        var dir = Environment.GetEnvironmentVariable("CHART_DUMP");
        if (dir is null) return;
        var x = new Axis(["w/e 1 Nov", "w/e 8 Nov", "w/e 15 Nov", "w/e 22 Nov"]) { Note = "Held back." };
        x.Markers.Add(new Marker(1, "Rollout wave 2", "Rollout"));
        var list = new (string, string)[]
        {
            ("line", Chart.Line(x, [new Series("Active people", [120, 410, 380, 520], 1), new Series("Licensed", [600, 640, 660, 700], 2, true)], Fmt.Count, "t").Value!),
            ("bars", Chart.Bars([new Bar("Finance and accounting", 0.42, Shown: "42 of 100"), new Bar("Sales", 0.71, Shown: "71 of 100"), new Bar("Human resources", 0.18)], Fmt.Percent, "r").Value!),
            ("cols", Chart.Columns(x, [new Series("Chat", [10, 20, 30, 40], 1), new Series("Agents", [5, 6, 7, 8], 3)], Fmt.Count, "m").Value!),
            ("heat", Chart.Heatmap(["Mon", "Tue", "Wed"], x, new double?[,] { { 1, 2, 3, null }, { 4, 5, 6, 7 }, { 2, 9, 1, 3 } }, Fmt.Count, "h").Value!),
        };
        foreach (var (n, html) in list)
            File.WriteAllBytes(Path.Combine(dir, n + ".png"), CopilotInsights.Charts.ChartImage.Png(ReportEmail.Charts(html).Single().Svg)!);
    }
}

public class Release320Count
{
    [Fact]
    public void Count()
    {
        var dir = Environment.GetEnvironmentVariable("CHART_DUMP");
        if (dir is null) return;
        var reports = Nav.Groups.Where(g => g.Persona != "Admin").SelectMany(g => g.Pages).Where(p => !p.Admin && !p.ManyTenants).Select(p => p.Path).Distinct().ToList();
        var ok = reports.Where(p => Subscriptions.Report(p) is not null).ToList();
        File.WriteAllText(Path.Combine(dir, "count.txt"), $"{reports.Count} {ok.Count}");
    }
}
