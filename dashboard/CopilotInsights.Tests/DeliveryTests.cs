using System.Text.Json;
using CopilotInsights.Charts;
using CopilotInsights.Infrastructure;
using CopilotInsights.Pages;
using Xunit;

namespace CopilotInsights.Tests;

/// <summary>Alerts to a webhook: the payloads, and which addresses are refused.</summary>
public class WebhookTests
{
    static readonly DateTime At = new(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Teams_payload_is_an_adaptive_card_with_a_link()
    {
        var json = Webhook.Payload("Teams", "Security", "Signals on Thu", ["Jailbreak attempts: 4", ""], "https://insights/security", At);
        using var doc = JsonDocument.Parse(json);
        var att = doc.RootElement.GetProperty("attachments")[0];
        Assert.Equal("message", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal("application/vnd.microsoft.card.adaptive", att.GetProperty("contentType").GetString());
        var card = att.GetProperty("content");
        Assert.Equal("AdaptiveCard", card.GetProperty("type").GetString());
        Assert.Equal(2, card.GetProperty("body").GetArrayLength());   // the subject and the one non-empty line
        Assert.Equal("https://insights/security", card.GetProperty("actions")[0].GetProperty("url").GetString());
    }

    [Fact]
    public void Json_payload_carries_the_alert_fields()
    {
        using var doc = JsonDocument.Parse(Webhook.Payload("Json", "Pipeline", "Pipeline failed", ["step 3"], null, At));
        var r = doc.RootElement;
        Assert.Equal("Copilot Insights", r.GetProperty("source").GetString());
        Assert.Equal("Pipeline", r.GetProperty("kind").GetString());
        Assert.Equal("step 3", r.GetProperty("lines")[0].GetString());
        Assert.Equal(JsonValueKind.Null, r.GetProperty("link").ValueKind);
        Assert.StartsWith("2026-10-01T08:00:00", r.GetProperty("atUtc").GetString());
    }

    [Theory]
    [InlineData("https://contoso.webhook.office.com/abc", true)]
    [InlineData("http://contoso.example/hook", false)]
    [InlineData("https://localhost/hook", false)]
    [InlineData("https://127.0.0.1/hook", false)]
    [InlineData("https://169.254.169.254/metadata", false)]
    [InlineData("not a url", false)]
    public void Only_https_to_another_host_is_accepted(string url, bool ok) =>
        Assert.Equal(ok, Webhook.Problem(url) is null);

    [Fact]
    public void An_empty_address_is_no_problem() => Assert.Null(Webhook.Problem(" "));
}

/// <summary>The certificate expiry warnings: once at each step, then expired.</summary>
public class CertificateWarningTests
{
    [Theory]
    [InlineData(45, 30, null)]
    [InlineData(29.5, 30, "30")]
    [InlineData(14, 30, "14")]
    [InlineData(2.9, 30, "3")]
    [InlineData(0, 30, "expired")]
    [InlineData(-5, 30, "expired")]
    [InlineData(10, 7, "14")]
    public void Step(double daysLeft, int warn, string? expected) => Assert.Equal(expected, CertificateWarning.Step(daysLeft, warn));
}

/// <summary>Scheduled extracts: where they may write, the file name, and the CSV.</summary>
public class ExtractTests
{
    [Theory]
    [InlineData(@"D:\Extracts", true)]
    [InlineData(@"\\server\share\copilot", true)]
    [InlineData(@"relative\folder", false)]
    [InlineData(@"D:\Extracts\..\Windows", false)]
    [InlineData("", false)]
    [InlineData("C:", false)]
    public void Folder(string folder, bool ok) => Assert.Equal(ok, ExtractData.FolderProblem(folder) is null);

    [Fact]
    public void File_name_is_safe_and_dated() =>
        Assert.Equal("Dept-weekly-report-20261001.csv", ExtractData.FileName("Dept weekly/report", new DateTime(2026, 10, 1)));

    [Fact]
    public void Csv_has_a_header_and_neutralises_formulas()
    {
        var csv = ExtractData.ToCsv([
            new Dictionary<string, object?> { ["Day"] = new DateTime(2026, 10, 1), ["Name"] = "=cmd()", ["N"] = 3, ["On"] = true },
            new Dictionary<string, object?> { ["Day"] = null, ["Name"] = "a \"b\"", ["N"] = 1.5, ["On"] = false },
        ]);
        var lines = csv.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("\"Day\",\"Name\",\"N\",\"On\"", lines[0]);
        Assert.Equal("\"2026-10-01\",\"'=cmd()\",\"3\",\"1\"", lines[1]);
        Assert.Equal("\"\",\"a \"\"b\"\"\",\"1.5\",\"0\"", lines[2]);
    }

    [Fact]
    public void Every_dataset_is_an_aggregate_query() =>
        Assert.All(ExtractData.Datasets.Values, d => Assert.DoesNotContain("UserPrincipalName", d.Sql));
}

/// <summary>Event markers on trend charts.</summary>
public class MarkerTests
{
    [Fact]
    public void Markers_draw_on_line_and_column_charts_at_their_point()
    {
        var x = new Axis(["1 Oct", "2 Oct", "3 Oct"]);
        x.Markers.Add(new Marker(1, "Wave 2 licenses", "Rollout"));
        x.Markers.Add(new Marker(1, "Workshop", "Training"));
        x.Markers.Add(new Marker(7, "Out of range", "Other"));
        var line = Chart.Line(x, [new Series("People", [1, 2, 3], 1)], Fmt.Count, "t").Value!;
        var cols = Chart.Columns(x, [new Series("People", [1, 2, 3], 1)], Fmt.Count, "t").Value!;
        foreach (var html in new[] { line, cols })
        {
            /* One rule and one numbered badge for the two events at the same point, none for the one off the axis. */
            Assert.Single(System.Text.RegularExpressions.Regex.Matches(html, "class=\"marker\""));
            Assert.Single(System.Text.RegularExpressions.Regex.Matches(html, "class=\"mbadge\""));
            Assert.Contains(">1</text></g>", html);
            Assert.Contains("Workshop (training)", html);                 // in the badge's tooltip
            Assert.DoesNotContain("Out of range", html);
            /* Nothing written over the data: the names are in the key under the chart, after the svg. */
            var svg = html[..(html.IndexOf("</svg>") + 6)];
            Assert.DoesNotContain(">Wave 2 licenses<", svg);
            Assert.DoesNotContain("mlabel", svg);
            var key = System.Net.WebUtility.HtmlDecode(html[html.IndexOf("</svg>")..]);
            Assert.Contains("<ol class=\"mkey\"><li><span class=\"k\">1</span>2 Oct Wave 2 licenses · 2 Oct Workshop</li></ol>", key);
        }
    }

    [Fact]
    public void Events_too_close_step_their_badges_apart_and_name_their_day()
    {
        var x = new Axis(Enumerable.Range(1, 60).Select(d => $"d{d}"));
        x.Markers.Add(new Marker(10, "Launch", "Rollout", new DateTime(2026, 9, 14)));
        x.Markers.Add(new Marker(11, "Outage", "Other", new DateTime(2026, 9, 15)));
        var html = Chart.Line(x, [new Series("People", Enumerable.Range(1, 60).Select(i => (double?)i).ToList(), 1)], Fmt.Count, "t").Value!;
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(html, "class=\"mbadge\"").Count);
        Assert.Contains("class=\"mjoin\"", html);                     // the second badge stepped right, joined to its rule
        Assert.Contains("<span class=\"k\">2</span>15 Sep Outage</li>", html);
    }

    [Fact]
    public void A_plain_label_list_draws_no_markers() =>
        Assert.DoesNotContain("class=\"marker\"", Chart.Line(["a", "b"], [new Series("S", [1, 2], 1)], Fmt.Count, "t").Value);
}

/// <summary>The person record is the one role page privacy mode allows.</summary>
public class PrivacyNavTests
{
    static Viewer Compliance() => new() { WindowsName = "C\\x", Upn = "c@contoso.com", InDirectory = true, Roles = new HashSet<string>([Roles.Compliance, Roles.Security]) };

    [Fact]
    public void Privacy_mode_keeps_the_person_record_and_hides_other_named_pages()
    {
        var pages = Nav.For(Compliance(), privacyMode: true).SelectMany(g => g.Pages).Select(p => p.Path).ToList();
        Assert.Contains("/compliance/person", pages);
        Assert.DoesNotContain("/compliance/access", pages);
        Assert.DoesNotContain("/security/flags", pages);
    }

    [Fact]
    public void Every_report_says_what_it_helps_with_and_what_to_do()
    {
        var reports = Nav.Groups.SelectMany(g => g.Pages).Where(p => !p.Admin).Select(p => p.Path).ToList();
        foreach (var path in reports)
        {
            var line = Guidance.For(path);
            Assert.True(line is not null, $"{path} has no guidance");
            Assert.False(string.IsNullOrWhiteSpace(line!.Helps) || string.IsNullOrWhiteSpace(line.Do), path);
            Assert.True(line.Helps.Length <= 110 && line.Do.Length <= 110, $"{path}: keep each line under 110 characters");
        }
        foreach (var key in Guidance.All.Keys) Assert.Contains(key, reports);
    }

    [Fact]
    public void Every_new_page_is_in_the_navigation()
    {
        var paths = Nav.Groups.SelectMany(g => g.Pages).Select(p => p.Path).ToHashSet();
        foreach (var p in new[] { "/leadership/usecases", "/leadership/boardpack", "/adoption/leaderboard", "/security/guests",
                                  "/compliance/person", "/forecasting/budget", "/forecasting/waves", "/forecasting/outcomes", "/workforce/families",
                                  "/admin/directory", "/admin/classifications", "/admin/extracts", "/licensing/ranking", "/licensing/payg", "/licensing/swap", "/licensing/license-for-credits", "/licensing/fading", "/licensing/non-person", "/agents/credits", "/licensing/overlap", "/licensing/departures" })
            Assert.Contains(p, paths);
    }
}

/// <summary>One retry policy: three tries an hour apart, then the next period.</summary>
public class RetryTests
{
    static readonly DateTime Now = new(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void A_success_clears_the_count()
    {
        var r = Retry.After(true, 2, Now);
        Assert.False(r.Retry); Assert.Equal(0, r.FailedTries); Assert.Null(r.RetryAfterUtc);
    }

    [Fact]
    public void Failures_are_retried_an_hour_later_three_tries_in_all()
    {
        var first = Retry.After(false, 0, Now);
        Assert.True(first.Retry); Assert.Equal(1, first.FailedTries); Assert.Equal(Now.AddHours(1), first.RetryAfterUtc);
        var second = Retry.After(false, first.FailedTries, Now);
        Assert.True(second.Retry); Assert.Equal(2, second.FailedTries);
        var third = Retry.After(false, second.FailedTries, Now);
        Assert.False(third.Retry); Assert.Equal(0, third.FailedTries);   // given up: waits for the next period
    }

    [Fact]
    public void Ready_once_the_wait_is_over()
    {
        Assert.True(Retry.Ready(null, Now));
        Assert.False(Retry.Ready(Now.AddMinutes(5), Now));
        Assert.True(Retry.Ready(Now, Now));
    }
}

/// <summary>Adoption ranked across groups rests only on groups with enough license holders (2.4.5).</summary>
public class AdoptionRankingTests
{
    [Fact]
    public void Small_groups_are_combined_last_and_every_bar_shows_its_counts()
    {
        var html = Chart.Adoption([
            new Chart.AdoptionGroup("Marshalls Creek", 2, 2), new Chart.AdoptionGroup("Hamburg Plaza", 1, 1),
            new Chart.AdoptionGroup("Finance", 30, 60), new Chart.AdoptionGroup("Legal", 5, 5), new Chart.AdoptionGroup("Sales", 45, 45),
            new Chart.AdoptionGroup("Unlicensed team", 0, 0)], 5, "t").Value!;
        var order = System.Text.RegularExpressions.Regex.Matches(html, "class=\"blabel\"[^>]*><tspan[^>]*>([^<]+)").Select(m => m.Groups[1].Value).ToList();
        /* 2 + 1 license holders are under 5 together, so the smallest ranked group, Legal's 5, joins them (2.4.12). */
        Assert.Equal(["Sales", "Finance", "3 smaller groups"], order.Take(3).Select(n => n.StartsWith("3 smaller") ? "3 smaller groups" : n));
        Assert.Contains("45 of 45", html);                 // shown beside the rate, not only on hover
        Assert.Contains("8 of 8", html);                   // the combined bar: 2 + 1 + 5 of 2 + 1 + 5
        Assert.DoesNotContain("Marshalls Creek", html);
        Assert.DoesNotContain("Legal", html);
        Assert.DoesNotContain("Unlicensed team", html);
    }
}

/// <summary>Groups too small to show on their own (2.4.12): what is left over reaches the minimum too.</summary>
public class SmallGroupTests
{
    static List<(string Name, int People)> Fold(int min, params (string, int)[] rows) =>
        CopilotInsights.Data.SmallGroups.Fold(rows, r => r.Item2, small => ("Smaller groups", small.Sum(r => r.Item2)), min, out _);

    [Fact]
    public void Small_groups_that_reach_the_minimum_together_are_one_row()
    {
        var rows = Fold(5, ("A", 40), ("B", 3), ("C", 2));
        Assert.Equal([("A", 40), ("Smaller groups", 5)], rows);
    }

    [Fact]
    public void A_lone_small_group_takes_the_smallest_shown_group_with_it()
    {
        /* The head of the organization alone was "Smaller groups, 1 person". */
        var rows = Fold(50, ("Direct", 1), ("Sales", 120), ("Legal", 60), ("Finance", 75));
        Assert.Equal([("Sales", 120), ("Finance", 75), ("Smaller groups", 61)], rows);
    }

    [Fact]
    public void A_list_that_leaves_small_groups_out_leaves_out_enough()
    {
        /* Beside a total of 181, leaving out only the one person gave them back by subtraction. */
        var shown = CopilotInsights.Data.SmallGroups.Shown([("Direct", 1), ("Sales", 120), ("Legal", 60)], r => r.Item2, 50);
        Assert.Equal([("Sales", 120)], shown);
    }

    [Fact]
    public void Everyone_under_the_minimum_is_one_row_the_total()
    {
        var rows = Fold(50, ("A", 10), ("B", 20));
        Assert.Equal([("Smaller groups", 30)], rows);
        Assert.Empty(Fold(5));
    }
}
