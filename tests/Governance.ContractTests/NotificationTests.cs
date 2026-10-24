using Governance.Notifications;
using Governance.Notifications.Templates;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Governance.ContractTests;

public class NotificationTests
{
    private static EmailRenderer Renderer()
    {
        var sp = new ServiceCollection().AddLogging().BuildServiceProvider();
        return new EmailRenderer(sp, sp.GetRequiredService<ILoggerFactory>());
    }

    [Fact]
    public async Task Notice_renders_html_and_plain_text_and_encodes_request_text()
    {
        var email = await Renderer().NoticeAsync("[Workbench] CCG-2026-000001: Routine approval", "Copilot Governance Workbench", "Action needed",
            "CCG-2026-000001: Routine approval", "Purpose: <script>alert(1)</script> & more",
            new List<KeyValuePair<string, string>> { new("Due", "Tue 27 Oct 2026, 14:00") }, "http://localhost:5450/Requests/abc", "Guidance text", "Footer text");

        Assert.Contains("<!DOCTYPE html>", email.Html);
        Assert.DoesNotContain("<script>", email.Html);
        Assert.Contains("&lt;script&gt;", email.Html);
        Assert.Contains("href=\"http://localhost:5450/Requests/abc\"", email.Html);
        Assert.Contains("never by replying", email.Html);
        Assert.Contains("Due: Tue 27 Oct 2026, 14:00", email.Text);
        Assert.Contains("Open in the Workbench: http://localhost:5450/Requests/abc", email.Text);
    }

    [Fact]
    public async Task Digest_lists_items_with_links()
    {
        var email = await Renderer().DigestAsync("Copilot Governance Workbench", "Your daily digest: 2 items", "Intro",
            new List<DigestEmail.Item> { new("CCG-1 approved", "http://l/1"), new("Drift on env", null) }, "Footer");
        Assert.Contains("CCG-1 approved", email.Html);
        Assert.Contains("- Drift on env", email.Text);
    }

    [Fact]
    public void Backoff_grows_and_is_capped()
    {
        Assert.InRange(OutboxProcessor.Backoff(1).TotalSeconds, 24, 36);
        Assert.InRange(OutboxProcessor.Backoff(3).TotalSeconds, 96, 144);
        Assert.InRange(OutboxProcessor.Backoff(30).TotalSeconds, 2880, 4320);
    }

    [Fact]
    public void Kind_labels_are_words_not_colors()
    {
        Assert.Equal("Decision recorded", NotificationDelivery.KindLabel("N06"));
        Assert.Equal("Action needed", NotificationDelivery.KindLabel("N04"));
    }
}
