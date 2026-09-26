using System.Net;
using Microsoft.AspNetCore.Html;

namespace CopilotInsights.Infrastructure;

/// <summary>
/// Server-side paging for lists of people, which run to tens of thousands of
/// rows: a page (p), a search (q) and a sort (s, one of the list's own keys,
/// never free SQL). A query adds "COUNT(*) OVER () AS TotalRows" and ends with
/// OrderAndPage(); an export asks for everything with ForExport.
/// </summary>
public sealed class Paging
{
    public const int PageSize = 50;
    public int Page { get; }
    public string? Search { get; }
    public string SortKey { get; }
    public bool ForExport { get; init; }
    public int Total { get; set; }
    readonly IReadOnlyDictionary<string, string> _sorts;

    public Paging(HttpRequest request, IReadOnlyDictionary<string, string> sorts, string defaultSort)
    {
        _sorts = sorts;
        Page = int.TryParse(request.Query["p"], out var p) && p > 0 ? p : 1;
        var q = request.Query["q"].FirstOrDefault()?.Trim();
        Search = string.IsNullOrEmpty(q) ? null : q.Length > 100 ? q[..100] : q;
        var s = request.Query["s"].FirstOrDefault();
        SortKey = s is not null && sorts.ContainsKey(s) ? s : defaultSort;
        ForExport = request.Query["handler"] == "csv";
    }

    /// <summary>A LIKE pattern for @q, with the wildcards in the search escaped.</summary>
    public string? Like => Search is null ? null : "%" + Search.Replace("[", "[[]").Replace("%", "[%]").Replace("_", "[_]") + "%";

    public string OrderAndPage() =>
        $"ORDER BY {_sorts[SortKey]} " + (ForExport ? "" : $"OFFSET {(Page - 1) * PageSize} ROWS FETCH NEXT {PageSize} ROWS ONLY");

    public int Pages => Math.Max(1, (int)Math.Ceiling(Total / (double)PageSize));

    /// <summary>Search box, sort choice and page links, keeping the other query values.</summary>
    public HtmlString Controls(HttpRequest request, IReadOnlyDictionary<string, string> sortLabels)
    {
        string Link(int page) => request.Path + QueryString(request, ("p", page.ToString()));
        var hidden = string.Concat(request.Query.Where(kv => kv.Key is not ("p" or "q" or "s" or "handler"))
            .Select(kv => $"<input type=\"hidden\" name=\"{WebUtility.HtmlEncode(kv.Key)}\" value=\"{WebUtility.HtmlEncode(kv.Value)}\"/>"));
        var sorts = string.Concat(sortLabels.Select(kv =>
            $"<option value=\"{kv.Key}\"{(kv.Key == SortKey ? " selected" : "")}>{WebUtility.HtmlEncode(kv.Value)}</option>"));
        var first = Total == 0 ? 0 : (Page - 1) * PageSize + 1;
        var last = Math.Min(Total, Page * PageSize);
        var nav = $"<span class=\"muted\">{first:#,0}–{last:#,0} of {Total:#,0}</span>" +
                  (Page > 1 ? $" <a href=\"{WebUtility.HtmlEncode(Link(Page - 1))}\">‹ Previous</a>" : "") +
                  (Page < Pages ? $" <a href=\"{WebUtility.HtmlEncode(Link(Page + 1))}\">Next ›</a>" : "");
        return new($"<form class=\"pager\" method=\"get\">{hidden}" +
                   $"<input type=\"search\" name=\"q\" value=\"{WebUtility.HtmlEncode(Search ?? "")}\" placeholder=\"Search name, sign-in or department\" aria-label=\"Search\"/>" +
                   $"<select name=\"s\" aria-label=\"Sort\" data-autosubmit>{sorts}</select><button class=\"btn\" type=\"submit\">Search</button>" +
                   $"<span class=\"pages\">{nav}</span></form>");
    }

    static string QueryString(HttpRequest request, (string Key, string Value) set)
    {
        var pairs = request.Query.Where(kv => kv.Key != set.Key && kv.Key != "handler")
            .Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value.ToString())}")
            .Append($"{set.Key}={Uri.EscapeDataString(set.Value)}");
        return "?" + string.Join("&", pairs);
    }
}
