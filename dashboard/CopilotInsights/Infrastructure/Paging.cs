using System.Net;
using Microsoft.AspNetCore.Html;

namespace CopilotInsights.Infrastructure;

/// <summary>
/// Server-side paging for lists of people, which run to tens of thousands of
/// rows: a page (p), a search (q) and a sort (s, one of the list's own keys,
/// never free SQL, with a leading "-" for the reverse order, 2.4.5). A query adds
/// "COUNT(*) OVER () AS TotalRows" and ends with OrderAndPage(); an export asks
/// for everything with ForExport. A table's column headers sort it (SortLink).
/// </summary>
public sealed class Paging
{
    public const int PageSize = 50;
    /// <summary>The highest page asked for that is taken as asked: its offset, and its last row, fit an int.</summary>
    public const int MaxPage = int.MaxValue / PageSize;
    public int Page { get; }
    public string? Search { get; }
    public string SortKey { get; }
    /// <summary>The key's order reversed (s=-key): a second click on a column header.</summary>
    public bool Reversed { get; }
    public bool ForExport { get; init; }
    public int Total { get; set; }
    readonly IReadOnlyDictionary<string, string> _sorts;

    public Paging(HttpRequest request, IReadOnlyDictionary<string, string> sorts, string defaultSort)
    {
        _sorts = sorts;
        /* No further than the last page an offset can name (2.4.12): from p=42949674 on, (p - 1) * 50 overflowed into a
           negative OFFSET written into the SQL, which SQL Server refused, and every list answered 500 unaudited. */
        Page = int.TryParse(request.Query["p"], out var p) && p > 0 ? Math.Min(p, MaxPage) : 1;
        var q = request.Query["q"].FirstOrDefault()?.Trim();
        Search = string.IsNullOrEmpty(q) ? null : q.Length > 100 ? q[..100] : q;
        var s = request.Query["s"].FirstOrDefault();
        var reversed = s is { Length: > 1 } && s[0] == '-';
        if (reversed) s = s![1..];
        SortKey = s is not null && sorts.ContainsKey(s) ? s : defaultSort;
        Reversed = reversed && SortKey == s;
        ForExport = request.Query["handler"] == "csv";
    }

    /// <summary>A LIKE pattern for @q, with the wildcards in the search escaped.</summary>
    public string? Like => Search is null ? null : "%" + Search.Replace("[", "[[]").Replace("%", "[%]").Replace("_", "[_]") + "%";

    /// <summary>The ORDER BY list in force, reversed when asked: for an outer query that puts a page's rows back in order.</summary>
    public string OrderBy => Reversed ? Reverse(_sorts[SortKey]) : _sorts[SortKey];

    public string OrderAndPage() =>
        /* An export takes every row; OFFSET 0 keeps the ORDER BY legal inside a CTE or derived table too. */
        $"ORDER BY {OrderBy} " + (ForExport ? "OFFSET 0 ROWS" : $"OFFSET {(Page - 1) * PageSize} ROWS FETCH NEXT {PageSize} ROWS ONLY");

    /// <summary>
    /// An ORDER BY list turned the other way: each term's ASC becomes DESC and the other way
    /// round, splitting on the commas outside parentheses. The keys are the page's own, never input.
    /// </summary>
    public static string Reverse(string orderBy)
    {
        var terms = new List<string>();
        int depth = 0, start = 0;
        for (var i = 0; i < orderBy.Length; i++)
        {
            if (orderBy[i] == '(') depth++;
            else if (orderBy[i] == ')') depth--;
            else if (orderBy[i] == ',' && depth == 0) { terms.Add(orderBy[start..i]); start = i + 1; }
        }
        terms.Add(orderBy[start..]);
        return string.Join(", ", terms.Select(t =>
        {
            t = t.Trim();
            if (t.EndsWith(" DESC", StringComparison.OrdinalIgnoreCase)) return t[..^5].TrimEnd();
            if (t.EndsWith(" ASC", StringComparison.OrdinalIgnoreCase)) return t[..^4].TrimEnd() + " DESC";
            return t + " DESC";
        }));
    }

    public bool Has(string key) => _sorts.ContainsKey(key);

    /// <summary>The address that sorts by this key: its own order first, reversed when it is already the sort.</summary>
    public string SortLink(HttpRequest request, string key) =>
        request.Path + QueryString(request, ("s", key == SortKey && !Reversed ? "-" + key : key), drop: "p");

    /// <summary>"ascending" or "descending" for the column sorted by this key, as aria-sort reads it; null for the others.</summary>
    public string? AriaSort(string key, bool keyDescends) =>
        key != SortKey ? null : keyDescends ^ Reversed ? "descending" : "ascending";

    public int Pages => Math.Max(1, (int)Math.Ceiling(Total / (double)PageSize));

    /// <summary>Search box, sort choice and page links, keeping the other query values.</summary>
    public HtmlString Controls(HttpRequest request, IReadOnlyDictionary<string, string> sortLabels)
    {
        string Link(int page) => request.Path + QueryString(request, ("p", page.ToString()));
        var hidden = string.Concat(request.Query.Where(kv => kv.Key is not ("p" or "q" or "s" or "handler"))
            .Select(kv => $"<input type=\"hidden\" name=\"{WebUtility.HtmlEncode(kv.Key)}\" value=\"{WebUtility.HtmlEncode(kv.Value)}\"/>"));
        var sorts = string.Concat(sortLabels.Select(kv =>
            $"<option value=\"{kv.Key}\"{(kv.Key == SortKey && !Reversed ? " selected" : "")}>{WebUtility.HtmlEncode(kv.Value)}</option>"));
        /* Sorted by a column header the list offers no choice for, or reversed: say so, and keep it on a new search. */
        if (Reversed || !sortLabels.ContainsKey(SortKey))
            sorts = $"<option value=\"{(Reversed ? "-" : "")}{SortKey}\" selected>{WebUtility.HtmlEncode((sortLabels.GetValueOrDefault(SortKey) ?? "By the column chosen") + (Reversed ? ", reversed" : ""))}</option>" + sorts;
        /* A page past the end (a stale link, a list that shrank) shows none of the rows, and Previous goes to the last page
           (2.4.12): it read "4,999,951–10,568 of 10,568", and Previous went one page back, still past the end. */
        var past = Page > Pages;
        var first = Total == 0 || past ? 0 : (Page - 1) * PageSize + 1;
        var last = past ? 0 : Math.Min(Total, Page * PageSize);
        var nav = $"<span class=\"muted\">{first:#,0}–{last:#,0} of {Total:#,0}</span>" +
                  (Page > 1 ? $" <a href=\"{WebUtility.HtmlEncode(Link(Math.Min(Page - 1, Pages)))}\">‹ Previous</a>" : "") +
                  (Page < Pages ? $" <a href=\"{WebUtility.HtmlEncode(Link(Page + 1))}\">Next ›</a>" : "");
        return new($"<form class=\"pager\" method=\"get\">{hidden}" +
                   $"<input type=\"search\" name=\"q\" value=\"{WebUtility.HtmlEncode(Search ?? "")}\" placeholder=\"Search name, sign-in or department\" aria-label=\"Search\"/>" +
                   $"<select name=\"s\" aria-label=\"Sort\" data-autosubmit>{sorts}</select><button class=\"btn\" type=\"submit\">Search</button>" +
                   $"<span class=\"pages\">{nav}</span></form>");
    }

    static string QueryString(HttpRequest request, (string Key, string Value) set, string? drop = null)
    {
        var pairs = request.Query.Where(kv => kv.Key != set.Key && kv.Key != "handler" && kv.Key != drop)
            .Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value.ToString())}")
            .Append($"{set.Key}={Uri.EscapeDataString(set.Value)}");
        return "?" + string.Join("&", pairs);
    }
}
