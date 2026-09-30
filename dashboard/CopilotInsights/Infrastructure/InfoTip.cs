using System.Net;
using Microsoft.AspNetCore.Html;

namespace CopilotInsights.Infrastructure;

/// <summary>
/// An information icon whose text shows on hover, keyboard focus or a tap (site.js), is read by screen readers
/// from the button's label, and prints inline, so a printed page keeps what the screen hides (2.4.3).
/// </summary>
public static class InfoTip
{
    public static IHtmlContent Icon(string? text, string about)
    {
        if (string.IsNullOrWhiteSpace(text)) return HtmlString.Empty;
        var t = WebUtility.HtmlEncode(text);
        return new HtmlString($"<button type=\"button\" class=\"infob\" data-tip=\"{t}\" aria-label=\"{WebUtility.HtmlEncode(about)}: {t}\">i</button>" +
                              $"<span class=\"print-only print-note\">{t}</span>");
    }
}
