using CopilotInsights.Data;
using CopilotInsights.Infrastructure;
using MailKit.Security;
using Xunit;

namespace CopilotInsights.Tests;

/// <summary>When digests are due, in the reporting time zone.</summary>
public class ScheduleTests
{
    static readonly TimeZoneInfo Eastern = Schedule.Zone("Eastern Standard Time");
    static DateTime Utc(int y, int m, int d, int h, int min = 0) => new(y, m, d, h, min, 0, DateTimeKind.Utc);

    [Fact]
    public void Daily_is_due_once_its_hour_has_passed_and_not_again_that_day()
    {
        // 07:00 Eastern on 1 October 2026 is 11:00 UTC (daylight time).
        Assert.False(Schedule.IsDue("Daily", null, null, 7, Utc(2026, 9, 30, 12), Utc(2026, 10, 1, 10, 59), Eastern));
        Assert.True(Schedule.IsDue("Daily", null, null, 7, Utc(2026, 9, 30, 12), Utc(2026, 10, 1, 11, 1), Eastern));
        Assert.False(Schedule.IsDue("Daily", null, null, 7, Utc(2026, 10, 1, 11, 2), Utc(2026, 10, 1, 23), Eastern));
    }

    [Fact]
    public void Weekly_waits_for_its_day()
    {
        // 5 October 2026 is a Monday.
        Assert.Equal(Utc(2026, 10, 5, 11), Schedule.LastDueUtc("Weekly", 1, null, 7, Utc(2026, 10, 7, 9), Eastern));
        Assert.False(Schedule.IsDue("Weekly", 1, null, 7, Utc(2026, 10, 5, 11, 5), Utc(2026, 10, 11, 9), Eastern));
        Assert.True(Schedule.IsDue("Weekly", 1, null, 7, Utc(2026, 10, 5, 11, 5), Utc(2026, 10, 12, 11, 1), Eastern));
    }

    [Fact]
    public void Monthly_uses_its_day_and_the_clock_change()
    {
        // 2 November 2026: standard time again, so 07:00 Eastern is 12:00 UTC.
        Assert.Equal(Utc(2026, 11, 2, 12), Schedule.LastDueUtc("Monthly", null, 2, 7, Utc(2026, 11, 20, 0), Eastern));
        Assert.Equal(Utc(2026, 10, 2, 11), Schedule.LastDueUtc("Monthly", null, 2, 7, Utc(2026, 11, 2, 11, 30), Eastern));
    }

    [Fact]
    public void Never_sent_is_due_and_an_unknown_frequency_never_is()
    {
        Assert.True(Schedule.IsDue("Weekly", 3, null, 7, null, Utc(2026, 10, 12, 0), Eastern));
        Assert.False(Schedule.IsDue("Hourly", null, null, 7, null, Utc(2026, 10, 12, 0), Eastern));
        Assert.Equal(TimeZoneInfo.Utc, Schedule.Zone("No Such Zone"));
    }
}

public class AlertRuleTests
{
    [Fact]
    public void A_signal_alerts_at_its_threshold_or_when_it_spikes()
    {
        var f = SecuritySignals.Findings(
        [
            new("Jailbreak attempts", 2, 0.1, 1),         // over the threshold
            new("Prompt injections", 0, 0.5, 1),           // nothing
            new("Blocked", 40, 10, 0),                     // threshold off, but four times the average
            new("Labeled", 12, 10, 100),                  // neither
            new("Tiny", 2, 0.1, 0),                        // a spike of two is noise
        ], 3);
        Assert.Equal(2, f.Count);
        Assert.StartsWith("Jailbreak attempts: 2", f[0]);
        Assert.Contains("4 times", f[1]);
    }

    static IReadOnlyDictionary<DateTime, long> Days(DateTime from, int n, Func<DateTime, long> value) =>
        Enumerable.Range(0, n).Select(i => from.AddDays(i)).ToDictionary(d => d, value);

    [Fact]
    public void A_drop_and_a_spike_are_judged_against_the_same_weekday()
    {
        var start = new DateTime(2026, 8, 3);   // a Monday
        var normal = Days(start, 42, d => d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday ? 5 : 100);
        var dropped = Days(start, 42, d => d == start.AddDays(38) ? 20 : d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday ? 5 : 100);
        var spiked = Days(start, 42, d => d == start.AddDays(39) ? 400 : d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday ? 5 : 100);
        var found = Anomalies.Find(new Dictionary<string, IReadOnlyDictionary<DateTime, long>> { ["Normal"] = normal, ["Dropped"] = dropped, ["Spiked"] = spiked },
            start.AddDays(28), 50, 200);
        Assert.Equal(2, found.Count);
        Assert.Contains(found, a => a.Unit == "Dropped" && a.Kind == "Drop" && a.Day == start.AddDays(38));
        Assert.Contains(found, a => a.Unit == "Spiked" && a.Kind == "Spike" && Math.Abs(a.Change - 3) < 0.01);
        // Quiet weekends are not a drop: their own weekday baseline is small.
        Assert.DoesNotContain(found, a => a.Unit == "Normal");
    }
}

public class LicenseExportTests
{
    [Fact]
    public void The_bulk_file_is_Entras_template()
    {
        var csv = LicenseExport.BulkMembersCsv(["a@contoso.com", "b,\"x\"@contoso.com"]).Split("\r\n");
        Assert.Equal("version:v1.0", csv[0]);
        Assert.Equal("Member object ID or user principal name [memberObjectIdOrUpn] Required", csv[1]);
        Assert.Equal("a@contoso.com", csv[2]);
        Assert.Equal("bx@contoso.com", csv[3]);
    }

    [Fact]
    public void The_script_uses_the_group_when_there_is_one_and_quotes_names()
    {
        var byGroup = LicenseExport.Script("revoke", ["o'brien@contoso.com"], "6c1e8b2a-3f4d-4e5f-9a0b-1c2d3e4f5a6b", "sku", "admin", DateTime.UtcNow);
        Assert.Contains("'o''brien@contoso.com'", byGroup);
        Assert.Contains("DELETE", byGroup);
        Assert.Contains("SupportsShouldProcess", byGroup);
        var direct = LicenseExport.Script("upgrade", ["a@contoso.com"], null, "639dec6b-bb19-468b-871c-c5c441c4b0cb", "admin", DateTime.UtcNow);
        Assert.Contains("addLicenses = @(@{ skuId = $skuId })", direct);
        Assert.Contains("639dec6b-bb19-468b-871c-c5c441c4b0cb", direct);
    }
}

public class CsvUploadTests
{
    [Fact]
    public void Training_rows_are_checked_line_by_line()
    {
        var (rows, errors) = CsvUpload.Training("﻿UPN,Session,Date\r\nAda@Contoso.com,\"Copilot, advanced\",2026-07-01\r\nnot-a-upn,S,2026-07-01\r\nb@contoso.com,S,01/07/2026\r\n\r\nc@contoso.com;Outlook;2026/07/02");
        Assert.Equal(2, rows.Count);
        Assert.Equal(("ada@contoso.com", "Copilot, advanced", new DateTime(2026, 7, 1)), rows[0]);
        Assert.Equal("Outlook", rows[1].Session);
        Assert.Equal(2, errors.Count);
    }

    [Fact]
    public void Value_names_need_a_column_id()
    {
        var (rows, errors) = CsvUpload.ValueNames("ColumnId,Value,Name,Note\n16,gpt-4o,GPT-4o,old\n16,gpt-5,\nx,y,z");
        Assert.Equal(2, rows.Count);
        Assert.Equal("", rows[1].Name);   // an empty name clears it
        Assert.Single(errors);
    }
}

public class MailConfigTests
{
    static IReadOnlyDictionary<string, string?> S(params (string, string?)[] kv) => kv.ToDictionary(x => x.Item1, x => x.Item2);

    [Fact]
    public void Off_says_so()
    {
        var c = MailConfig.Load(S(), null, null);
        Assert.Equal(MailMode.Off, c.Mode);
        Assert.Equal(["Email is off."], c.Problems());
    }

    [Fact]
    public void An_authenticated_relay_on_a_nonstandard_port()
    {
        var c = MailConfig.Load(S(("MailMode", "Smtp"), ("MailFrom", "insights@contoso.com"), ("SmtpHost", "relay.contoso.com"), ("SmtpPort", "2525"),
                                  ("SmtpSecurity", "starttls"), ("SmtpUser", "relay-user")), "pw", null);
        Assert.Empty(c.Problems());
        Assert.Equal(("relay.contoso.com", 2525, SecureSocketOptions.StartTls), c.Endpoint());
        var implicitTls = c with { Port = 4465, Security = "Implicit" };
        Assert.Equal(SecureSocketOptions.SslOnConnect, implicitTls.Endpoint().Tls);
        Assert.Contains("password", string.Join(" ", (c with { Password = null }).Problems()));
    }

    [Fact]
    public void Exchange_Online_is_always_office365_on_587_and_needs_its_credential()
    {
        var pw = MailConfig.Load(S(("MailMode", "ExchangeOnline"), ("MailFrom", "insights@contoso.com"), ("SmtpHost", "ignored"), ("SmtpPort", "25")), null, null);
        Assert.Equal(("smtp.office365.com", 587, SecureSocketOptions.StartTls), pw.Endpoint());
        Assert.Contains("password", string.Join(" ", pw.Problems()));
        var oauth = MailConfig.Load(S(("MailMode", "ExchangeOnline"), ("ExoAuth", "OAuth"), ("MailFrom", "insights@contoso.com"),
                                      ("ExoTenantId", "contoso.onmicrosoft.com"), ("ExoClientId", "not-a-guid")), null, "secret");
        Assert.Single(oauth.Problems());
    }

    [Fact]
    public void Recipients_are_split_and_checked()
    {
        var r = MailConfig.Recipients("a@contoso.com; b@contoso.com,\nnonsense ; ");
        Assert.Equal(["a@contoso.com", "b@contoso.com"], r.Select(x => x.Address));
    }

    [Fact]
    public void A_refused_login_is_described_as_one() =>
        Assert.StartsWith("The server refused the user name or password", Mailer.Describe(new AuthenticationException("535 bad credentials")));
}

public class FreshnessTests
{
    static readonly DateTime Now = new(2026, 9, 27, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Fresh_data_shows_no_banner() => Assert.Null(Freshness.Describe(Now.AddHours(-10), 36, "Succeeded", null, Now.AddHours(-11), Now));

    [Fact]
    public void Old_data_and_a_failed_run_do()
    {
        Assert.Contains("40 hours ago", Freshness.Describe(Now.AddHours(-40), 36, "Succeeded", null, null, Now));
        Assert.Contains("failed at \"Copilot licenses from Graph\"", Freshness.Describe(Now.AddHours(-2), 36, "Failed", "Copilot licenses from Graph", Now.AddHours(-3), Now));
    }
}

public class SignInTests
{
    const string Mine = "S-1-5-21-100-200-300-1001", Theirs = "S-1-5-21-100-200-300-1002";

    [Fact]
    public void A_person_with_a_SID_on_record_is_only_the_account_with_it()
    {
        Assert.True(AccessRules.SidMatches(Mine, Mine));
        Assert.True(AccessRules.SidMatches(Mine.ToLowerInvariant(), Mine));
        Assert.False(AccessRules.SidMatches(Theirs, Mine));
        Assert.False(AccessRules.SidMatches(Theirs, null));
    }

    [Fact]
    public void A_person_with_no_SID_on_record_is_matched_by_name() =>
        Assert.True(AccessRules.SidMatches(null, Mine) && AccessRules.SidMatches(" ", null));

    [Fact]
    public void Nothing_in_the_options_lets_a_URL_choose_the_viewer() =>
        Assert.Null(typeof(InsightsOptions).GetProperty("AllowViewAs"));
}

public class PrivacyNavigationTests
{
    static Viewer V(params string[] roles) =>
        new() { WindowsName = @"CONTOSO\me", Upn = "me@contoso.com", Level = 3, InDirectory = true, Roles = new HashSet<string>(roles, StringComparer.OrdinalIgnoreCase) };

    [Fact]
    public void Privacy_mode_hides_every_page_that_names_people()
    {
        var paths = Pages.Nav.For(V(Roles.All), privacyMode: true).SelectMany(g => g.Pages).ToList();
        /* The person record stays: a data-subject request is about one named person, on request. */
        Assert.Equal(["/compliance/person"], paths.Where(p => p.Role is not null).Select(p => p.Path));
        Assert.Contains(paths, p => p.Path == "/leadership");
        Assert.Contains(paths, p => p.Path == "/admin/privacy");
    }

    [Fact]
    public void A_report_turned_off_is_gone_for_everyone_and_an_admin_page_cannot_be()
    {
        var hidden = new HashSet<string>(["/adoption/cohorts", "/Leadership/", "/admin/health"], StringComparer.OrdinalIgnoreCase);
        var paths = Pages.Nav.For(V(Roles.All), hidden: hidden).SelectMany(g => g.Pages).Select(p => p.Path).ToList();
        Assert.DoesNotContain("/adoption/cohorts", paths);
        Assert.Contains("/adoption", paths);
        Assert.True(Pages.HiddenReports.Is(hidden, "/adoption/cohorts/"));
        Assert.False(Pages.HiddenReports.Is(hidden, "/adoption"));
        Assert.False(Pages.HiddenReports.Is(null, "/adoption/cohorts"));
        /* Every report can be chosen, and no Admin page: an administrator can always get back to turn one on. */
        var choices = Pages.HiddenReports.Choices.SelectMany(g => g.Pages).ToList();
        Assert.DoesNotContain(choices, p => p.Admin);
        Assert.Equal(Pages.Nav.Groups.SelectMany(g => g.Pages).Count(p => !p.Admin), choices.Count);
        Assert.Contains(Pages.Nav.Groups.SelectMany(g => g.Pages), p => p.Path == "/admin/reports" && p.Admin);
        /* A name for the setting fits its column (64). */
        Assert.All(choices, p => Assert.True((Pages.HiddenReports.Prefix + p.Path).Length <= 64));
    }

    [Fact]
    public void Every_page_says_what_it_answers_and_a_section_keeps_its_headings_together()
    {
        foreach (var g in Pages.Nav.Groups)
        {
            foreach (var p in g.Pages)
            {
                var what = Pages.NavText.What(p);
                Assert.False(string.IsNullOrWhiteSpace(what), $"{p.Path} has no description in NavText");
                Assert.True(what!.Length <= 100 && what.EndsWith('.') || what.EndsWith('?'), $"{p.Path}: one short sentence ({what.Length} characters)");
                if (p.Role is not null) Assert.Contains($"Needs the {p.Role} role.", Pages.NavText.Tip(p));
            }
            /* Under headings, or under none: and each heading once, its pages side by side in the list. */
            var subs = Pages.NavText.Sub(g.Pages).ToList();
            Assert.True(subs.All(x => x.Heading is null) ? subs.Count == 1 : subs.All(x => x.Heading is not null), g.Persona);
            Assert.Equal(subs.Count, subs.Select(x => x.Heading).Distinct().Count());
            Assert.Equal(g.Pages.Count, subs.Sum(x => x.Pages.Count));
        }
    }

    [Fact]
    public void Your_most_used_offers_only_reports_the_viewer_may_open_now()
    {
        var groups = Pages.Nav.For(V(Roles.Adoption), hidden: new HashSet<string>(["/adoption/cohorts"], StringComparer.OrdinalIgnoreCase));
        /* Most opened first: an admin page, the start page, a report turned off, one of a section they do not hold, and one twice. */
        var picked = MostUsed.Pick(["/admin/health", "/adoption/", "/", "/adoption/cohorts", "/licensing/unused", "/leadership", "/adoption", "/regions"], groups);
        Assert.Equal(["/adoption", "/leadership", "/regions"], picked.Select(p => p.Path));
        /* Fewer than three is no section at all; and never more than ten. */
        Assert.Empty(MostUsed.Pick(["/adoption", "/leadership"], groups));
        Assert.Equal(10, MostUsed.Pick(groups.SelectMany(g => g.Pages).Select(p => p.Path).ToList(), groups).Count);
    }

    [Fact]
    public void The_new_named_lists_need_their_roles()
    {
        var paths = Pages.Nav.For(V()).SelectMany(g => g.Pages).Select(p => p.Path).ToList();
        Assert.DoesNotContain("/licensing/leavers", paths);
        Assert.DoesNotContain("/enablement/champions", paths);
        /* 2.4.2: their sections need the role too, aggregates included. */
        Assert.DoesNotContain("/licensing/pool", paths);
        Assert.DoesNotContain("/compliance/sites", paths);
        var withRoles = Pages.Nav.For(V(Roles.Licensing, Roles.Compliance)).SelectMany(g => g.Pages).Select(p => p.Path).ToList();
        Assert.Contains("/licensing/pool", withRoles);
        Assert.Contains("/compliance/sites", withRoles);

    }
}

/// <summary>A mapping file downloaded, edited and uploaded again lands on the values it came from (2.4.5).</summary>
public class MappingFileTests
{
    [Fact]
    public void Value_names_survive_the_round_trip_through_a_download()
    {
        var t = new CopilotInsights.Pages.Table { Headers = ["ColumnId", "Value", "Name", "Note"] };
        t.Rows.Add([6, "-agent:7", "Travel helper", "from the CoE"]);
        t.Rows.Add([11, "@label", "Confidential", null]);
        t.Rows.Add([3, "Word, desktop", "Word", null]);
        var (rows, errors) = CopilotInsights.Data.CsvUpload.ValueNames(t.ToCsv());
        Assert.Empty(errors);
        Assert.Equal([("-agent:7", "Travel helper"), ("@label", "Confidential"), ("Word, desktop", "Word")], rows.Select(r => (r.Value, r.Name)));
        Assert.Equal((short)6, rows[0].ColumnId);
    }

    [Fact]
    public void Training_records_survive_the_round_trip_through_a_download()
    {
        var t = new CopilotInsights.Pages.Table { Headers = ["UPN", "Session", "Date"] };
        t.Rows.Add(["ada@contoso.com", "=Copilot in Excel", "2026-09-30"]);
        var (rows, errors) = CopilotInsights.Data.CsvUpload.Training(t.ToCsv());
        Assert.Empty(errors);
        Assert.Equal(("ada@contoso.com", "=Copilot in Excel", new DateTime(2026, 9, 30)), Assert.Single(rows));
    }

    [Theory]
    [InlineData("'=SUM(A1)", "=SUM(A1)")]
    [InlineData("'-3", "-3")]
    [InlineData("'quoted", "'quoted")]
    [InlineData("'", "'")]
    public void Unguard_removes_only_the_formula_guard(string cell, string expected) => Assert.Equal(expected, CopilotInsights.Data.CsvUpload.Unguard(cell));
}
