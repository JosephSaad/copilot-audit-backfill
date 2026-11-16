using System.Text.Json;
using CopilotInsights.Data;
using CopilotInsights.Infrastructure;
using CopilotInsights.Pages.Admin;
using MailKit.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

/* A namespace of their own: the other review groups' test files sit beside this one. */
namespace CopilotInsights.Tests.Review2412A;

/// <summary>Schedules on the days the clock changes: the skipped hour no longer throws, the repeated hour sends once.</summary>
public class DaylightScheduleTests
{
    static readonly TimeZoneInfo Eastern = Schedule.Zone("Eastern Standard Time");
    static readonly TimeZoneInfo Central = Schedule.Zone("W. Europe Standard Time");
    static DateTime Utc(int y, int m, int d, int h, int min = 0) => new(y, m, d, h, min, 0, DateTimeKind.Utc);

    /// <summary>The background checks every five minutes from <paramref name="from"/> to <paramref name="to"/>: when it was sent.</summary>
    static List<DateTime> Rounds(string frequency, int? dow, int hour, DateTime lastSent, DateTime from, DateTime to, TimeZoneInfo zone)
    {
        var sent = new List<DateTime>();
        DateTime? last = lastSent;
        for (var now = from; now <= to; now = now.AddMinutes(5))
            if (Schedule.IsDue(frequency, dow, null, hour, last, now, zone)) { sent.Add(now); last = now; }
        return sent;
    }

    [Fact]
    public void The_hour_the_clock_skips_is_due_on_the_other_side_of_the_gap()
    {
        // 14 March 2027: Eastern clocks go from 01:59 to 03:00. 02:00 is due at 03:00 daylight time, 07:00 UTC.
        Assert.Equal(Utc(2027, 3, 14, 7), Schedule.LastDueUtc("Daily", null, null, 2, Utc(2027, 3, 14, 12), Eastern));
        // A weekly one on Sunday stays on that day all week, which threw for seven days.
        Assert.Equal(Utc(2027, 3, 14, 7), Schedule.LastDueUtc("Weekly", 0, null, 2, Utc(2027, 3, 18, 15), Eastern));
        Assert.Equal(Utc(2027, 3, 14, 7), Schedule.LastDueUtc("Monthly", null, 14, 2, Utc(2027, 3, 20, 0), Eastern));
        // 28 March 2027 in Central Europe: 02:00 is due at 03:00 summer time, 01:00 UTC.
        Assert.Equal(Utc(2027, 3, 28, 1), Schedule.LastDueUtc("Daily", null, null, 2, Utc(2027, 3, 28, 12), Central));
    }

    [Fact]
    public void A_schedule_in_the_skipped_hour_is_sent_once_that_day()
    {
        var sent = Rounds("Daily", null, 2, Utc(2027, 3, 13, 7), Utc(2027, 3, 13, 12), Utc(2027, 3, 15, 12), Eastern);
        Assert.Equal([Utc(2027, 3, 14, 7), Utc(2027, 3, 15, 6)], sent);
    }

    [Fact]
    public void The_hour_the_clock_repeats_is_due_the_first_time_round_and_sent_once()
    {
        // 1 November 2026: Eastern 01:00 to 01:59 comes twice, at 05:00 and at 06:00 UTC. It was sent 13 times.
        Assert.Equal(Utc(2026, 11, 1, 5), Schedule.LastDueUtc("Daily", null, null, 1, Utc(2026, 11, 1, 6, 30), Eastern));
        Assert.Equal([Utc(2026, 11, 1, 5)], Rounds("Daily", null, 1, Utc(2026, 10, 31, 5), Utc(2026, 11, 1, 4), Utc(2026, 11, 1, 8), Eastern));
        // 25 October 2026: Central European 02:00 comes at 00:00 and at 01:00 UTC.
        Assert.Equal([Utc(2026, 10, 25, 0)], Rounds("Daily", null, 2, Utc(2026, 10, 24, 0), Utc(2026, 10, 24, 23), Utc(2026, 10, 25, 3), Central));
        // An hour away from the change is untouched.
        Assert.Equal([Utc(2026, 11, 1, 12)], Rounds("Daily", null, 7, Utc(2026, 10, 31, 11), Utc(2026, 11, 1, 4), Utc(2026, 11, 1, 13), Eastern));
    }
}

/// <summary>A form posts to the tenant its page showed, not to whichever one the cookie names when it is sent.</summary>
public class TenantFormTests
{
    static TenantAccessor Two() => new(Options.Create(new InsightsOptions
    {
        Tenants = [new() { Name = "Contoso", ConnectionString = "Server=a" }, new() { Name = "Fabrikam", ConnectionString = "Server=b" }],
    }));

    static DefaultHttpContext Request(TenantAccessor tenants, string method, string query, string? cookie)
    {
        var ctx = new DefaultHttpContext { RequestServices = new ServiceCollection().AddSingleton(tenants).BuildServiceProvider() };
        ctx.Request.Method = method;
        ctx.Request.QueryString = new QueryString(query);
        if (cookie is not null) ctx.Request.Headers.Cookie = $"{TenantAccessor.Cookie}={cookie}";
        ctx.Response.Body = new MemoryStream();
        return ctx;
    }

    static async Task<string?> RanIn(TenantAccessor tenants, HttpContext ctx)
    {
        string? ran = null;
        await TenantAccessor.SelectAsync(ctx, () => { ran = tenants.Current.Name; return Task.CompletedTask; });
        return ran;
    }

    [Fact]
    public async Task A_post_goes_to_the_tenant_in_its_address_and_leaves_the_cookie_alone()
    {
        // Admin > Access open on Contoso; another tab has since switched the cookie to Fabrikam.
        var tenants = Two();
        var ctx = Request(tenants, "POST", "?handler=revoke&tenant=Contoso", "Fabrikam");
        Assert.Equal("Contoso", await RanIn(tenants, ctx));
        Assert.False(ctx.Response.Headers.ContainsKey("Set-Cookie"));
    }

    [Fact]
    public async Task A_post_that_names_no_tenant_changes_nothing()
    {
        var tenants = Two();
        var ctx = Request(tenants, "POST", "?handler=revoke", "Fabrikam");
        Assert.Null(await RanIn(tenants, ctx));
        Assert.Equal(StatusCodes.Status409Conflict, ctx.Response.StatusCode);
        Assert.Null(await RanIn(tenants, Request(tenants, "POST", "?handler=revoke&tenant=Nowhere", "Fabrikam")));
    }

    [Fact]
    public async Task Pages_still_follow_the_switcher_and_a_form_names_the_page_s_tenant()
    {
        var tenants = Two();
        var chosen = Request(tenants, "GET", "?tenant=Fabrikam", "Contoso");
        Assert.Equal("Fabrikam", await RanIn(tenants, chosen));
        Assert.Contains("Fabrikam", chosen.Response.Headers.SetCookie.ToString());
        Assert.Equal("Contoso", await RanIn(tenants, Request(tenants, "GET", "", "Contoso")));
        tenants.Use(tenants.Find("Fabrikam")!);
        Assert.Equal("tenant=Fabrikam", tenants.FormQuery);
        // One tenant: nothing to name, and a form posts as before.
        var one = new TenantAccessor(Options.Create(new InsightsOptions()));
        Assert.Null(one.FormQuery);
        Assert.Equal("Default", await RanIn(one, Request(one, "POST", "?handler=revoke", null)));
    }

    [Fact]
    public async Task Run_the_checks_for_every_tenant_runs_only_where_the_viewer_is_an_administrator()
    {
        var tenants = Two();
        tenants.Use(tenants.Find("Fabrikam")!);
        static Viewer V(bool admin) => new() { WindowsName = @"CONTOSO\avery", Upn = "avery.morgan@contoso.com", Roles = new HashSet<string>(admin ? new[] { Roles.Admin } : Array.Empty<string>()) };
        var ran = new List<string>();
        var recorded = new List<string>();
        var done = await NotificationsModel.EveryTenantAsync(tenants,
            () => Task.FromResult(V(tenants.Current.Name == "Fabrikam")),
            () => { ran.Add(tenants.Current.Name); return Task.FromResult(new List<string> { "Alert sent: x" }); },
            (_, _) => { recorded.Add(tenants.Current.Name); return Task.CompletedTask; });
        Assert.Equal(["Fabrikam"], ran);
        Assert.Equal(["[Contoso] Not run: you are not an administrator in this tenant.", "[Fabrikam] Alert sent: x"], done);
        Assert.Empty(recorded);                          // Fabrikam's own entry is the page's
        Assert.Equal("Fabrikam", tenants.Current.Name);  // and the page's tenant again afterwards

        var both = await NotificationsModel.EveryTenantAsync(tenants, () => Task.FromResult(V(true)),
            () => Task.FromResult(new List<string>()), (_, _) => { recorded.Add(tenants.Current.Name); return Task.CompletedTask; });
        Assert.Equal(["Contoso"], recorded);             // a tenant it ran in from elsewhere has it in its own audit log
    }
}

/// <summary>The digest preview and Send now need the section the digest shows granted, and the preview is audited.</summary>
public class DigestRoleTests
{
    [Fact]
    public void Each_digest_needs_its_section()
    {
        Assert.Equal(Sections.Security, NotificationsModel.DigestSection("Security"));
        Assert.Equal(Sections.Licensing, NotificationsModel.DigestSection("Licensing"));
        Assert.Equal(Sections.Adoption, NotificationsModel.DigestSection("Adoption"));
        /* Leadership and Value are Leadership's figures: since 3.1.0 Leadership is a section that needs a grant too. */
        Assert.Equal(Sections.Leadership, NotificationsModel.DigestSection("Leadership"));
        Assert.Equal(Sections.Leadership, NotificationsModel.DigestSection("Value"));
        /* Every digest's section is one the menu has. */
        var sections = CopilotInsights.Pages.Nav.Groups.Select(g => g.Key).ToHashSet();
        Assert.All(OpsReports.DigestKinds.Keys, k => Assert.Contains(NotificationsModel.DigestSection(k), sections));
    }

    [Fact]
    public void The_preview_and_send_now_check_the_role_and_the_preview_writes_the_audit_log()
    {
        var source = Source.Read("Pages", "Admin", "Operations.cs");
        var preview = Source.Method(source, "OnGetPreviewAsync");
        Assert.Contains("DigestRefusal(report)", preview);
        Assert.Contains("LimitRefusal(Viewer)", preview);
        Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(preview, @"S\.Audit\.WriteAsync").Count);   // refused, shown, not shown
        Assert.Contains("DigestRefusal(sub.Report)", Source.Method(source, "OnPostSendnowAsync"));
    }
}

/// <summary>A stored password goes only where it was entered for.</summary>
public class StoredPasswordTests
{
    static IReadOnlyDictionary<string, string?> S(params (string, string?)[] kv) => kv.ToDictionary(x => x.Item1, x => x.Item2);
    static readonly IReadOnlyDictionary<string, string?> Relay = S(("MailMode", "Smtp"), ("MailFrom", "insights@contoso.com"), ("SmtpHost", "relay.contoso.com"),
        ("SmtpPort", "2525"), ("SmtpSecurity", "StartTls"), ("SmtpUser", "relay-user"));

    static Dictionary<string, string?> With(string key, string? value) => new(Relay) { [key] = value };

    [Fact]
    public void The_password_is_used_where_it_was_entered_and_nowhere_else()
    {
        var entered = MailConfig.Load(Relay, null, null).PasswordTarget();
        Assert.Empty(MailConfig.Load(Relay, "pw", null, entered).Problems());
        // The same server written another way is the same place.
        Assert.Empty(MailConfig.Load(With("SmtpHost", "RELAY.contoso.com "), "pw", null, entered).Problems());
        foreach (var (key, value) in new[] { ("SmtpHost", "mail.attacker.example"), ("SmtpPort", "25"), ("SmtpSecurity", "None"), ("SmtpUser", "someone"),
                                             ("SmtpAcceptAnyCertificate", "1"), ("MailMode", "ExchangeOnline") })
        {
            var moved = MailConfig.Load(With(key, value), "pw", null, entered);
            Assert.True(moved.PasswordMoved, key);
            Assert.Contains(moved.Problems(), p => p.Contains("type the password again"));
        }
    }

    [Fact]
    public void A_password_stored_before_is_used_until_the_next_save_ties_it_and_OAuth_does_not_need_it()
    {
        Assert.Empty(MailConfig.Load(Relay, "pw", null, passwordFor: null).Problems());
        var oauth = MailConfig.Load(S(("MailMode", "ExchangeOnline"), ("ExoAuth", "OAuth"), ("MailFrom", "insights@contoso.com"), ("ExoTenantId", "contoso.onmicrosoft.com"),
            ("ExoClientId", "6c1e8b2a-3f4d-4e5f-9a0b-1c2d3e4f5a6b")), "old relay password", "secret", "Smtp|relay.contoso.com|2525|StartTls|relay-user|checked");
        Assert.Empty(oauth.Problems());
        Assert.Equal(("smtp.office365.com", 587, SecureSocketOptions.StartTls), oauth.Endpoint());
    }

    [Fact]
    public void A_token_is_kept_under_the_secret_that_got_it()
    {
        var a = MailConfig.Load(S(("MailMode", "ExchangeOnline"), ("ExoAuth", "OAuth"), ("ExoTenantId", "contoso.onmicrosoft.com"), ("ExoClientId", "6c1e8b2a-3f4d-4e5f-9a0b-1c2d3e4f5a6b")), null, "the real secret");
        Assert.Equal(Mailer.TokenKey(a), Mailer.TokenKey(a with { }));
        Assert.NotEqual(Mailer.TokenKey(a), Mailer.TokenKey(a with { ClientSecret = "anything else" }));
        Assert.DoesNotContain("the real secret", Mailer.TokenKey(a));
    }

    [Fact]
    public void Only_a_send_that_failed_is_worth_another_try()
    {
        Assert.True(Mailer.Settled("Sent"));
        Assert.True(Mailer.Settled("NoRecipients"));
        Assert.True(Mailer.Settled("MailOff"));
        Assert.False(Mailer.Settled("Failed"));
    }
}

/// <summary>Sends are claimed before they go, so a state that cannot be written sends nothing, and nothing twice.</summary>
public class SendStateTests
{
    [Fact]
    public void Every_alert_is_claimed_before_it_is_sent()
    {
        var notifier = Source.Read("Infrastructure", "Notifier.cs");
        var delivery = Source.Read("Infrastructure", "Delivery.cs");
        /* AlertAsync is called from one place, SendOnceAsync, which RaiseAsync reaches only after its claim. */
        var calls = System.Text.RegularExpressions.Regex.Matches(notifier + delivery, @"await AlertAsync\(").Count;
        Assert.Equal(1, calls);
        var raise = Source.Method(notifier, "RaiseAsync");
        Assert.True(raise.IndexOf("ClaimStateAsync", StringComparison.Ordinal) < raise.IndexOf("SendOnceAsync", StringComparison.Ordinal));
        /* No check writes its state after an alert any more: each goes through RaiseAsync or SendOnceAsync. */
        Assert.DoesNotContain("AlertAsync(\"", notifier + delivery);
    }

    [Fact]
    public void A_digest_is_marked_sent_before_it_is_mailed_and_a_stopped_manager_digest_is_given_back()
    {
        var digests = Source.Method(Source.Read("Infrastructure", "Notifier.cs"), "DigestsAsync");
        Assert.True(digests.IndexOf("SET LastSentUtc = SYSUTCDATETIME()", StringComparison.Ordinal) < digests.IndexOf("_mail.SendAsync", StringComparison.Ordinal));
        /* The claim is a compare-and-set on what the round read, and a digest waiting for its retry cannot be claimed early. */
        Assert.Contains("(LastSentUtc = @seen OR (LastSentUtc IS NULL AND @seen IS NULL))", digests);
        Assert.Contains("RetryAfterUtc <= SYSUTCDATETIME()", digests);
        var managers = Source.Method(Source.Read("Infrastructure", "Delivery.cs"), "ManagerDigestsAsync");
        Assert.Contains("ReleaseAsync(m.Upn", managers);
        Assert.True(managers.IndexOf("ct.ThrowIfCancellationRequested()", StringComparison.Ordinal) < managers.IndexOf("ClaimAsync(", StringComparison.Ordinal));
        /* Once a message is on its way it is not cancelled, so a stop leaves it unsent for certain. */
        var mail = Source.Read("Infrastructure", "Mail.cs");
        Assert.Contains("client.SendAsync(msg, CancellationToken.None)", mail);
    }
}

/// <summary>Alert cards, extracts, uploads and retention.</summary>
public class DeliveryReviewTests
{
    [Fact]
    public void A_line_in_a_Teams_card_is_shown_as_text_not_read_as_Markdown()
    {
        const string line = "[Approve](https://evil.example/login) (Copilot Studio), first used 2026-10-01.";
        using var doc = JsonDocument.Parse(Webhook.Payload("Teams", "NewAgent", "1 agent(s) used for the first time", [line], null, DateTime.UtcNow));
        var body = doc.RootElement.GetProperty("attachments")[0].GetProperty("content").GetProperty("body");
        Assert.All(body.EnumerateArray(), b => Assert.Equal("RichTextBlock", b.GetProperty("type").GetString()));
        var run = body[1].GetProperty("inlines")[0];
        Assert.Equal("TextRun", run.GetProperty("type").GetString());
        Assert.Equal(line, run.GetProperty("text").GetString());
        Assert.Equal("Bolder", body[0].GetProperty("inlines")[0].GetProperty("weight").GetString());
    }

    [Fact]
    public void Extracts_that_read_the_facts_between_dates_are_planned_for_their_own_dates()
    {
        foreach (var (name, (_, sql)) in ExtractData.Datasets)
            if (sql.Contains("bi.FactInteraction") || sql.Contains("dash.UserDay") || sql.Contains("bi.DepartmentWeek"))
                Assert.True(sql.TrimEnd().EndsWith("OPTION (RECOMPILE);", StringComparison.Ordinal), name);
    }

    [Theory]
    [InlineData(@"\\server\share\copilot", true)]
    [InlineData(@" \\server\share", true)]
    [InlineData(@"D:\Extracts", false)]
    [InlineData(null, false)]
    public void A_share_is_the_same_folder_on_every_web_server(string? folder, bool share) => Assert.Equal(share, ExtractData.IsShare(folder));

    [Fact]
    public void A_local_folder_is_refused_once_another_web_server_runs_the_checks()
    {
        Assert.Null(ExtractData.SplitProblem(@"D:\Extracts", 0));
        Assert.Null(ExtractData.SplitProblem(@"\\files\copilot", 2));
        Assert.Contains("use a share", ExtractData.SplitProblem(@"D:\Extracts", 1));
        /* Each round records its web server, and an extract checks before it writes. */
        Assert.Contains("await run.HereAsync();", Source.Read("Infrastructure", "Notifier.cs"));
        Assert.Contains("ExtractData.SplitProblem(x.Folder, await OtherServersAsync())", Source.Method(Source.Read("Infrastructure", "Delivery.cs"), "WriteExtractAsync"));
    }

    [Fact]
    public void A_name_Excel_saved_without_quotes_keeps_its_other_separator()
    {
        /* What Excel writes for a mapping file edited in a comma or a semicolon locale. */
        var (rows, errors) = CsvUpload.ValueNames("ColumnId,Value,Name,Note\r\n6,agent-x,Finance; Legal,\r\n6,agent-y,Old; name,\"note, with comma\"\r\n16;gpt-4o;Smith, John;see ticket, then follow up");
        Assert.Empty(errors);
        Assert.Equal(new (string, string, string?)[] { ("agent-x", "Finance; Legal", null), ("agent-y", "Old; name", "note, with comma"), ("gpt-4o", "Smith, John", "see ticket, then follow up") },
            rows.Select(r => (r.Value, r.Name, r.Note)));
        var (training, bad) = CsvUpload.Training("ada@contoso.com;Copilot, part 2;2026-07-01");
        Assert.Empty(bad);
        Assert.Equal("Copilot, part 2", Assert.Single(training).Session);
    }

    [Fact]
    public void A_line_with_more_cells_than_it_should_have_is_refused_not_cut()
    {
        var (rows, errors) = CsvUpload.ValueNames("6,agent-x,Finance,note,extra\n6,agent-y,Legal,,,");
        Assert.Equal("agent-y", Assert.Single(rows).Value);   // empty cells past the fourth are Excel's, and fine
        Assert.Contains("more than four cells", Assert.Single(errors));
        var (training, bad) = CsvUpload.Training("ada@contoso.com,Copilot,2026-07-01,Room 4");
        Assert.Empty(training);
        Assert.Contains("more than three cells", Assert.Single(bad));
    }

    [Theory]
    [InlineData(1, 0, false)]
    [InlineData(364, 0, false)]
    [InlineData(365, 0, true)]
    [InlineData(0, 0, true)]
    [InlineData(730, 199, false)]
    [InlineData(730, 200, true)]
    public void The_audit_log_is_kept_at_least_a_year(int auditDays, int jsonDays, bool ok) =>
        Assert.Equal(ok, PrivacyModel.RetentionProblem(auditDays, jsonDays) is null);

    [Fact]
    public void Agent_ids_on_Value_names_are_not_tied_to_Reference_data_s_agents()
    {
        /* The record's agent id never equals the app identity an agent is known by since 2.4.11: the link named nothing. */
        var values = Source.Read("Pages", "Admin", "Operations.cs");
        var model = values[values.IndexOf("class ValuesModel", StringComparison.Ordinal)..values.IndexOf("class TrainingRecordsModel", StringComparison.Ordinal)];
        Assert.DoesNotContain("AgentAlias", model);
        Assert.DoesNotContain("Agent names are shared", model);
    }
}

/// <summary>Reads the dashboard's own source, for the tests that check how it is put together.</summary>
static class Source
{
    public static string Read(params string[] path)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine([d.FullName, "CopilotInsights", .. path]))) return File.ReadAllText(Path.Combine([d.FullName, "CopilotInsights", .. path]));
        throw new FileNotFoundException(string.Join('/', path));
    }

    /// <summary>A method's text, from its declaration to the brace that closes it at the same indent.</summary>
    public static string Method(string source, string name)
    {
        var m = System.Text.RegularExpressions.Regex.Match(source, @"(?m)^( *)[^\r\n(]*\bTask\b[^\r\n(]* " + name + @"\(");
        Assert.True(m.Success, name + " is not declared in the source");
        var end = source.IndexOf("\n" + m.Groups[1].Value + "}", m.Index, StringComparison.Ordinal);
        return source[m.Index..(end < 0 ? source.Length : end)];
    }
}
