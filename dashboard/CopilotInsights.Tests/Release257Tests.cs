using CopilotInsights.Infrastructure;
using CopilotInsights.Pages;
using Xunit;

namespace CopilotInsights.Tests;

/// <summary>
/// What 2.5.7 changed: a table sent whole with more than a page of rows is shown a page at a time in the browser,
/// 50 rows as the server's lists are; a short table, and a list the server pages, are left as they were.
/// </summary>
public class Release257Tests
{
    static Table Rows(int n, int keepLast = 0)
    {
        var t = new Table { Headers = ["Name", "Count"], Numeric = new HashSet<int> { 1 } };
        for (var i = 0; i < n; i++) t.Rows.Add([$"row {i}", i]);
        t.KeepLast = keepLast;
        return t;
    }

    [Fact]
    public void A_table_of_more_than_a_page_is_paged_in_the_browser_at_the_server_s_page_size()
    {
        Assert.Equal(50, Paging.PageSize);
        var html = Rows(Paging.PageSize + 1).Render().Value!;
        Assert.Contains($"data-page-size=\"{Paging.PageSize}\"", html);
        Assert.Contains("row 50", html);   // every row is sent: the browser shows a page, a print and a sort see them all
    }

    [Fact]
    public void A_page_or_less_is_not_paged_and_a_total_row_does_not_count()
    {
        Assert.DoesNotContain("data-page-size", Rows(Paging.PageSize).Render().Value!);
        Assert.DoesNotContain("data-page-size", Rows(Paging.PageSize + 1, keepLast: 1).Render().Value!);
        Assert.DoesNotContain("data-page-size", Rows(0).Render().Value!);
    }
}
