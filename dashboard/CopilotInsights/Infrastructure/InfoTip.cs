using System.Net;
using Microsoft.AspNetCore.Html;

namespace CopilotInsights.Infrastructure;

/// <summary>
/// An information icon whose text shows on hover, keyboard focus or a tap (site.js), is read by screen readers
/// from the button's label, and prints inline, so a printed page keeps what the screen hides.
/// </summary>
public static class InfoTip
{
    public static IHtmlContent Icon(string? text, string about)
    {
        if (string.IsNullOrWhiteSpace(text)) return HtmlString.Empty;
        var t = WebUtility.HtmlEncode(text);
        return new HtmlString(Button(t, about) + Note(t));
    }

    /// <summary>
    /// A label with its icon: the icon is tied to the label's last word, so where the label wraps the icon goes
    /// with that word and is never left on a line by itself. With no text there is no icon, and the label is as it was.
    /// </summary>
    public static IHtmlContent After(string label, string? text, string? about = null)
    {
        var l = WebUtility.HtmlEncode(label);
        if (string.IsNullOrWhiteSpace(text)) return new HtmlString(l);
        var t = WebUtility.HtmlEncode(text);
        var cut = l.LastIndexOf(' ') + 1;
        return new HtmlString($"{l[..cut]}<span class=\"nb\">{l[cut..]}{Button(t, about ?? label)}</span>{Note(t)}");
    }

    static string Button(string t, string about) =>
        $"<button type=\"button\" class=\"infob\" data-tip=\"{t}\" aria-label=\"{WebUtility.HtmlEncode(about)}: {t}\">i</button>";

    static string Note(string t) => $"<span class=\"print-only print-note\">{t}</span>";
}
