using CopilotInsights.Infrastructure;
using CopilotInsights.Pages;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace CopilotInsights.Tests;

/// <summary>
/// What 2.5.8 changed in paging: a list paged on the server has its page links under it, numbered as a table paged in
/// the browser is, in the same element and look; the search box and the sort stay above it.
/// </summary>
public class Release258Tests
{
    static (Paging Pager, HttpRequest Request) Pager(string query, int total)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Path = "/agents/inventory";
        ctx.Request.QueryString = new QueryString(query);
        return (new Paging(ctx.Request, new Dictionary<string, string> { ["name"] = "AgentName" }, "name") { Total = total }, ctx.Request);
    }

    [Fact]
    public void The_page_links_are_numbered_around_this_page_with_the_first_and_last()
    {
        var (p, r) = Pager("?p=7", 1_000);   // 20 pages
        var html = p.Nav(r).Value!;
        Assert.StartsWith("<nav class=\"tpager\" aria-label=\"Pages\">", html);
        Assert.Contains("301–350 of 1,000", html);
        Assert.Contains("aria-current=\"page\" href=\"/agents/inventory?p=7\">7</a>", html);
        foreach (var n in new[] { 1, 5, 6, 8, 9, 20 }) Assert.Contains($"?p={n}\">{n}</a>", html);
        Assert.DoesNotContain("?p=4\">4</a>", html);
        Assert.Contains("<span class=\"muted gap\" aria-hidden=\"true\">·</span>", html);   // the pages left out, marked without an ellipsis
        Assert.Contains("p=6\">‹ Previous", html);
        Assert.Contains("p=8\">Next ›", html);
    }

    [Fact]
    public void The_search_and_sort_keep_their_place_and_the_links_go_under_the_table()
    {
        var (p, r) = Pager("?p=2", 120);
        Assert.DoesNotContain("tpager", p.Controls(r, new Dictionary<string, string> { ["name"] = "Name" }).Value!);
        var t = new Table { Headers = ["Name"], Server = new(p, r, ["name"]) };
        t.Rows.Add(["one"]);
        var html = t.Render().Value!;
        Assert.True(html.IndexOf("</table>", StringComparison.Ordinal) < html.IndexOf("class=\"tpager\"", StringComparison.Ordinal));
        Assert.Contains("51–100 of 120", html);
    }

    [Fact]
    public void An_export_has_no_page_links()
    {
        var (p, r) = Pager("?handler=csv", 120);
        var t = new Table { Headers = ["Name"], Server = new(p, r, ["name"]) };
        t.Rows.Add(["one"]);
        Assert.DoesNotContain("tpager", t.Render().Value!);
    }
}
