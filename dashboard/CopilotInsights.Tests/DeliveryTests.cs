using System.Text.Json;
using CopilotInsights.Charts;
using CopilotInsights.Infrastructure;
using CopilotInsights.Pages;
using CopilotInsights.Pages.Personal;
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

/// <summary>Saved views can only point at report pages on this site.</summary>
public class SavedViewTests
{
    [Theory]
    [InlineData("/adoption?d=90", true)]
    [InlineData("/security/flags?kind=jailbreak&status=open", true)]
    [InlineData("/leadership/", true)]
    [InlineData("//evil.com/adoption", false)]
    [InlineData("https://evil.com/adoption", false)]
    [InlineData("/adoption?handler=csv", false)]
    [InlineData("/\\evil.com", false)]
    [InlineData("/not-a-page", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Safe(string? url, bool ok) => Assert.Equal(ok, ViewsModel.SafeUrl(url));
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
        foreach (var svg in new[] { line, cols })
        {
            Assert.Single(System.Text.RegularExpressions.Regex.Matches(svg, "class=\"marker\""));
            Assert.Contains("Wave 2 licenses +1", svg);
            Assert.Contains("Workshop (training)", svg);
            Assert.DoesNotContain("Out of range", svg);
        }
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
    public void Every_new_page_is_in_the_navigation()
    {
        var paths = Nav.Groups.SelectMany(g => g.Pages).Select(p => p.Path).ToHashSet();
        foreach (var p in new[] { "/me/email", "/me/views", "/leadership/usecases", "/leadership/boardpack", "/adoption/leaderboard", "/security/guests",
                                  "/compliance/person", "/forecasting/budget", "/forecasting/waves", "/forecasting/outcomes", "/workforce/families",
                                  "/admin/directory", "/admin/classifications", "/admin/extracts", "/licensing/ranking", "/licensing/payg", "/licensing/swap", "/licensing/license-for-credits", "/licensing/fading", "/licensing/non-person" })
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
