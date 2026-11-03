using System.Text;
using Governance.Application;
using Governance.Application.Data;
using Governance.Notifications.Templates;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Logging;

namespace Governance.Notifications;

/// <summary>Options shared with the infrastructure's notification options (same "Notifications" section).</summary>
public sealed class DispatchOptions
{
    public string Mode { get; set; } = "Record";
    public int MaxAttempts { get; set; } = 6;
    public string WorkbenchBaseUrl { get; set; } = "http://localhost:5291";
    public bool IsRecord => !string.Equals(Mode, "Send", StringComparison.OrdinalIgnoreCase);
}

public sealed record RenderedEmail(string Subject, string Html, string Text);

/// <summary>
/// Business wording the Platform Owner edits between releases (ref.TextBlock, ACTIVE version), with code defaults,
/// cached for five minutes. Templates stay compiled; only the words change.
/// </summary>
public sealed class TextBlocks(IClock clock)
{
    private readonly Dictionary<string, (string Value, DateTime Loaded)> _cache = new();
    private readonly object _gate = new();

    public static readonly IReadOnlyDictionary<string, string> Defaults = new Dictionary<string, string>
    {
        ["email.header"] = "Copilot Governance Workbench",
        ["email.footer"] = "You get this notice because you hold a role in the Copilot Governance Workbench. People who act get a notice; people who are informed get the daily digest.",
        ["email.digest.title"] = "Your daily digest",
        ["email.digest.intro"] = "What changed yesterday on items you are informed about. Nothing here needs an action from you.",
        ["email.guidance.N04"] = "Approve only within your delegation and the LOB's envelope; anything above goes to the business owner.",
        ["email.guidance.N08"] = "Verification checks the template's settings, including the explicit deny case. Fix and resubmit through the ITSM task.",
        ["email.guidance.N11"] = "Claim it in Registry > Discovery and classify it, or record why it is out of scope.",
        ["email.guidance.N13"] = "An expired exception suspends the workload when it was its only approved route.",
    };

    public async Task<string> GetAsync(Uow uow, string key)
    {
        lock (_gate)
            if (_cache.TryGetValue(key, out var c) && clock.UtcNow - c.Loaded < TimeSpan.FromMinutes(5)) return c.Value;
        var value = await uow.ScalarAsync<string>("SELECT Body FROM ref.TextBlock WHERE TextKey = @key AND Status = 'ACTIVE'", new { key })
                    ?? Defaults.GetValueOrDefault(key) ?? "";
        lock (_gate) _cache[key] = (value, clock.UtcNow);
        return value;
    }
}

/// <summary>Renders the Razor email components outside the HTTP pipeline (HtmlRenderer), with a plain-text alternative.</summary>
public sealed class EmailRenderer(IServiceProvider services, ILoggerFactory loggers)
{
    public async Task<RenderedEmail> NoticeAsync(string subject, string header, string kindLabel, string title, string text,
        IReadOnlyList<KeyValuePair<string, string>> facts, string? linkUrl, string? guidance, string footer)
    {
        var html = await RenderAsync<NoticeEmail>(new Dictionary<string, object?>
        {
            [nameof(NoticeEmail.Subject)] = subject, [nameof(NoticeEmail.Header)] = header, [nameof(NoticeEmail.KindLabel)] = kindLabel,
            [nameof(NoticeEmail.Title)] = title, [nameof(NoticeEmail.Text)] = text, [nameof(NoticeEmail.Facts)] = facts,
            [nameof(NoticeEmail.LinkUrl)] = linkUrl, [nameof(NoticeEmail.Guidance)] = guidance, [nameof(NoticeEmail.Footer)] = footer,
        });
        var sb = new StringBuilder();
        sb.AppendLine($"{header} - {kindLabel}").AppendLine().AppendLine(title).AppendLine().AppendLine(text).AppendLine();
        foreach (var f in facts) sb.AppendLine($"{f.Key}: {f.Value}");
        if (facts.Count > 0) sb.AppendLine();
        if (linkUrl is not null) sb.AppendLine($"Open in the Workbench: {linkUrl}").AppendLine("Decisions are recorded in the Workbench after you sign in, never by replying to this email.").AppendLine();
        if (!string.IsNullOrEmpty(guidance)) sb.AppendLine(guidance).AppendLine();
        sb.AppendLine(footer);
        return new RenderedEmail(subject, html, sb.ToString());
    }

    public async Task<RenderedEmail> DigestAsync(string header, string title, string intro, IReadOnlyList<DigestEmail.Item> items, string footer)
    {
        var html = await RenderAsync<DigestEmail>(new Dictionary<string, object?>
        {
            [nameof(DigestEmail.Header)] = header, [nameof(DigestEmail.Title)] = title, [nameof(DigestEmail.Intro)] = intro,
            [nameof(DigestEmail.Items)] = items, [nameof(DigestEmail.Footer)] = footer,
        });
        var sb = new StringBuilder().AppendLine(title).AppendLine().AppendLine(intro).AppendLine();
        foreach (var i in items) sb.AppendLine($"- {i.Text}{(i.Url is null ? "" : $" ({i.Url})")}");
        sb.AppendLine().AppendLine(footer);
        return new RenderedEmail(title, html, sb.ToString());
    }

    private async Task<string> RenderAsync<T>(Dictionary<string, object?> parameters) where T : IComponent
    {
        await using var renderer = new HtmlRenderer(services, loggers);
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var output = await renderer.RenderComponentAsync<T>(ParameterView.FromDictionary(parameters));
            return output.ToHtmlString();
        });
    }
}
