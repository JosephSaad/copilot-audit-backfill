using CopilotInsights.Data;
using CopilotInsights.Infrastructure;
using Xunit;

namespace CopilotInsights.Tests;

/// <summary>The search behind every box that takes a person: three letters, then the start of a first or a last name.</summary>
public class PersonLookupTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("m")]
    [InlineData("mo")]
    [InlineData(" mo ")]
    [InlineData("m o")]       // two letters, however they are spaced
    [InlineData("o'")]
    [InlineData("--.")]
    public void Nothing_is_searched_under_three_letters(string? typed) => Assert.Null(PersonLookup.Term(typed));

    [Theory]
    [InlineData("mor", "mor")]
    [InlineData("  Mor ", "Mor")]
    [InlineData("avery   mor", "avery mor")]
    [InlineData("o'br", "o'br")]
    [InlineData("a1b", "a1b")]
    public void Three_letters_or_more_are_searched_as_typed_trimmed(string typed, string term) => Assert.Equal(term, PersonLookup.Term(typed));

    [Fact]
    public void A_very_long_entry_is_cut() => Assert.Equal(100, PersonLookup.Term(new string('a', 500))!.Length);

    [Theory]
    [InlineData("Avery Morgan", "mor", true)]        // the start of the last name
    [InlineData("Morgan Petrov", "mor", true)]       // the start of the first name
    [InlineData("Morgan, Avery", "ave", true)]       // "Last, First"
    [InlineData("Seymour Hill", "mor", false)]       // the middle of a name is not a match
    [InlineData("Avery Morgan", "MOR", true)]
    [InlineData("Avery Morgan", "avery mor", true)]  // a first name and the start of a last
    [InlineData("Avery Morgan", "gan", false)]
    [InlineData("Mary Ann Lee", "ann", true)]        // any word of the name
    [InlineData(null, "mor", false)]
    [InlineData("", "mor", false)]
    public void A_name_matches_where_a_first_or_a_last_name_begins_with_what_was_typed(string? name, string term, bool matches) =>
        Assert.Equal(matches, PersonLookup.Matches(name, term));

    [Fact]
    public void What_was_typed_is_matched_as_text_never_as_a_pattern() =>
        Assert.Equal(@"50\%\_a\[b\\", PersonLookup.Escape(@"50%_a[b\"));

    [Fact]
    public void A_match_is_offered_by_name_with_the_department_and_for_a_manager_the_team()
    {
        var p = new PersonMatch("avery.morgan@contoso.com", "Avery Morgan", "Finance", 1240);
        Assert.Equal("Avery Morgan, Finance", PersonLookup.Label(p, team: false));
        Assert.Equal("Avery Morgan, Finance (1,240 people)", PersonLookup.Label(p, team: true));
        Assert.Equal("x@contoso.com (1 person)", PersonLookup.Label(new PersonMatch("x@contoso.com", null, " ", 1), team: true));
    }
}

/// <summary>The conversation with the mail server kept for troubleshooting: everything but the secrets and the message.</summary>
public class SmtpTranscriptTests
{
    static readonly MailConfig Relay = new()
    {
        Mode = MailMode.Smtp, Host = "relay.contoso.local", Port = 25, Security = "StartTls", User = "svc-copilot", Password = "S3cret!value", From = "copilot@contoso.com",
    };

    const string Log = "Connected to smtp://relay.contoso.local:25/?starttls=always\r\n" +
        "S: 220 relay.contoso.local ESMTP\r\nC: EHLO web01\r\nS: 250-relay.contoso.local\r\nS: 250-STARTTLS\r\nS: 250 AUTH LOGIN PLAIN\r\n" +
        "C: AUTH PLAIN AHN2Yy1jb3BpbG90AFMzY3JldCF2YWx1ZQ==\r\nS: 235 2.7.0 Authentication successful\r\n" +
        "C: MAIL FROM:<copilot@contoso.com>\r\nS: 250 OK\r\nC: RCPT TO:<admin@contoso.com>\r\nS: 250 OK\r\nC: DATA\r\nS: 354 Start mail input\r\n" +
        "C: Subject: Weekly digest\r\nC: \r\nC: Confidential figures for the board\r\nC: .\r\nS: 250 Queued\r\nC: QUIT\r\nS: 221 Bye\r\n";

    [Fact]
    public void The_header_says_where_how_and_as_whom_and_never_the_password()
    {
        var h = SmtpTranscript.Header(Relay);
        Assert.Contains("Server relay.contoso.local, port 25, STARTTLS, required", h);
        Assert.Contains("signing in as svc-copilot (password stored)", h);
        Assert.Contains("from copilot@contoso.com", h);
        Assert.DoesNotContain("S3cret", h);
        Assert.Contains("not signing in", SmtpTranscript.Header(Relay with { User = null, Password = null }));
        Assert.Contains("no password stored", SmtpTranscript.Header(Relay with { Password = null }));
        Assert.Contains("certificate is not checked", SmtpTranscript.Header(Relay with { AcceptAnyCertificate = true }));
    }

    [Fact]
    public void The_conversation_keeps_each_command_and_answer()
    {
        var c = SmtpTranscript.Clean(Log);
        Assert.Contains("S: 250 AUTH LOGIN PLAIN", c);
        Assert.Contains("C: MAIL FROM:<copilot@contoso.com>", c);
        Assert.Contains("S: 235 2.7.0 Authentication successful", c);
        Assert.Contains("S: 250 Queued", c);
        Assert.EndsWith("S: 221 Bye", c);
    }

    [Fact]
    public void What_follows_AUTH_is_masked()
    {
        var c = SmtpTranscript.Clean(Log);
        Assert.Contains("C: AUTH PLAIN ********", c);
        Assert.DoesNotContain("AHN2Yy1jb3BpbG90", c);
    }

    [Fact]
    public void Each_part_of_a_sign_in_asked_for_line_by_line_is_masked_too()
    {
        var c = SmtpTranscript.Clean("C: AUTH LOGIN\nS: 334 VXNlcm5hbWU6\nC: c3ZjLWNvcGlsb3Q=\nS: 334 UGFzc3dvcmQ6\nC: UzNjcmV0IXZhbHVl\nS: 535 5.7.3 Authentication unsuccessful\nC: QUIT\n");
        Assert.DoesNotContain("c3ZjLWNvcGlsb3Q=", c);
        Assert.DoesNotContain("UzNjcmV0IXZhbHVl", c);
        Assert.Equal(2, c.Split('\n').Count(l => l == "C: ********"));
        Assert.Contains("S: 535 5.7.3 Authentication unsuccessful", c);
        Assert.EndsWith("C: QUIT", c);       // after the sign-in ends, commands are kept again
    }

    [Fact]
    public void The_message_itself_is_not_kept()
    {
        var c = SmtpTranscript.Clean(Log);
        Assert.DoesNotContain("Confidential", c);
        Assert.DoesNotContain("Weekly digest", c);
        Assert.Contains("S: 354 Start mail input\nC: (the message itself is not kept)\nS: 250 Queued", c);
    }

    [Fact]
    public void A_long_conversation_is_cut_and_says_so()
    {
        var c = SmtpTranscript.Clean(string.Concat(Enumerable.Repeat("S: 250 a line of the server's answer\n", 2000)));
        Assert.True(c.Length < SmtpTranscript.MaxLength + 100);
        Assert.EndsWith("(cut: the conversation was longer than is kept)", c);
    }

    [Fact]
    public void A_failed_send_ends_with_every_cause()
    {
        var ex = new InvalidOperationException("535: 5.7.3 Authentication unsuccessful", new IOException("the connection was closed"));
        var t = SmtpTranscript.Compose(Relay, "S: 220 hello\nC: EHLO web01\n", ex);
        Assert.StartsWith("Server relay.contoso.local", t);
        Assert.Contains("C: EHLO web01", t);
        Assert.EndsWith("Not sent. InvalidOperationException: 535: 5.7.3 Authentication unsuccessful <- IOException: the connection was closed", t);
        Assert.DoesNotContain("S3cret", t);
    }

    [Fact]
    public void A_server_never_reached_has_a_header_and_an_ending_and_no_conversation()
    {
        var t = SmtpTranscript.Compose(Relay, "", new TimeoutException("no answer"));
        Assert.Equal(2, t.Split('\n').Length);
        Assert.EndsWith("Sent: the server accepted the message.", SmtpTranscript.Compose(Relay, Log, null));
    }
}

/// <summary>The Health cards of the two optional loaders, GitHub Copilot and Foundry, and Foundry's own certificate.</summary>
public class ExternalUsageTests
{
    static readonly DateTime Now = new(2026, 10, 8, 12, 0, 0, DateTimeKind.Utc);
    static string When(DateTime? d) => $"{d:yyyy-MM-dd HH:mm}";

    static ExternalUsageRun Run(string status, double hoursAgo = 5, DateTime? tokenExpires = null, string? detail = null) => new()
    {
        Source = ExternalUsage.GitHub, Scope = "enterprise:contoso", StartedUtc = Now.AddHours(-hoursAgo), EndedUtc = status == "Running" ? null : Now.AddHours(-hoursAgo).AddMinutes(2),
        Status = status, FromDay = new DateTime(2026, 9, 28), ToDay = new DateTime(2026, 10, 7), RowsWritten = 4321, Detail = detail, CredentialExpiresUtc = tokenExpires,
        LastDay = new DateTime(2026, 10, 5),
    };

    [Fact]
    public void A_source_never_loaded_is_optional_not_a_fault()
    {
        var (text, css) = ExternalUsage.Status(ExternalUsage.Foundry, null, Now, When);
        Assert.Equal("muted", css);
        Assert.Contains("Not loaded here. It is optional", text);
        Assert.Contains("Import-FoundryUsage.ps1", text);
        Assert.Contains("FoundrySubscriptions", text);
        Assert.Contains("GitHubEnterprise", ExternalUsage.Status(ExternalUsage.GitHub, null, Now, When).Text);
    }

    [Fact]
    public void A_good_run_says_when_what_and_the_newest_day_held()
    {
        var (text, css) = ExternalUsage.Status(ExternalUsage.GitHub, Run("Succeeded", tokenExpires: Now.AddDays(90.5)), Now, When);
        Assert.Equal("status-ok", css);
        Assert.Contains("for 2026-09-28 to 2026-10-07: 4,321 rows.", text);
        Assert.Contains("Newest day held: 2026-10-05.", text);
        Assert.Contains("Its token expires 2027-01-07 (90 days).", text);
    }

    [Theory]
    [InlineData(14, "status-warn")]
    [InlineData(15, "status-ok")]
    public void A_token_about_to_expire_turns_the_card_amber(int days, string css) =>
        Assert.Equal(css, ExternalUsage.Status(ExternalUsage.GitHub, Run("Succeeded", tokenExpires: Now.AddDays(days)), Now, When).Css);

    [Fact]
    public void A_token_that_has_expired_says_so() =>
        Assert.Contains("Its token expired on 2026-10-01.", ExternalUsage.Status(ExternalUsage.GitHub, Run("Failed", tokenExpires: new DateTime(2026, 10, 1)), Now, When).Text);

    [Fact]
    public void A_good_run_too_long_ago_means_the_nightly_step_is_not_running()
    {
        var (text, css) = ExternalUsage.Status(ExternalUsage.GitHub, Run("Succeeded", hoursAgo: 80), Now, When);
        Assert.Equal("status-warn", css);
        Assert.Contains("Nothing has run since", text);
    }

    [Fact]
    public void A_partial_run_is_amber_and_names_what_was_not_loaded()
    {
        var (text, css) = ExternalUsage.Status(ExternalUsage.GitHub, Run("Partial", detail: "Loaded 3 seat(s). Not loaded: cost (GitHub answered 403)."), Now, When);
        Assert.Equal("status-warn", css);
        Assert.Contains("loaded a part", text);
        Assert.Contains("Not loaded: cost (GitHub answered 403).", text);
    }

    [Fact]
    public void A_failed_run_is_red_with_the_service_s_own_words()
    {
        var (text, css) = ExternalUsage.Status(ExternalUsage.GitHub, Run("Failed", detail: "Not loaded: seats (GitHub answered 401: Bad credentials)."), Now, When);
        Assert.Equal("status-bad", css);
        Assert.Contains("failed. Not loaded: seats (GitHub answered 401: Bad credentials).", text);
    }

    [Theory]
    [InlineData(1, "status-ok", "Running since")]
    [InlineData(9, "status-warn", "never recorded how it ended")]
    public void A_run_in_progress_is_fine_until_it_is_plainly_one_that_was_stopped(double hoursAgo, string css, string says)
    {
        var (text, cls) = ExternalUsage.Status(ExternalUsage.GitHub, Run("Running", hoursAgo), Now, When);
        Assert.Equal(css, cls);
        Assert.Contains(says, text);
    }

    [Fact]
    public void Foundry_s_own_certificate_is_named_and_watched_under_a_state_of_its_own()
    {
        Assert.Equal("Foundry usage app certificate", Certificates.Name(Certificates.Foundry));
        Assert.Equal("certificate:Foundry", Certificates.StateKey(Certificates.Foundry));
        Assert.Contains("the rest of the nightly run goes on", Certificates.UsedFor(Certificates.Foundry));
        Assert.Contains("FoundryCertificateThumbprint", Certificates.Renew(Certificates.Foundry));
    }
}
