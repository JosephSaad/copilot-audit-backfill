namespace Governance.Application.Queries;

/// <summary>Search, sort and page for any list. Sort keys are checked against a whitelist per list.</summary>
public sealed record ListQuery(string? Search = null, string? Sort = null, bool Desc = false, int Page = 1, int PageSize = 50)
{
    public int Offset => Math.Max(0, (Math.Max(1, Page) - 1) * PageSize);
    public string? Like => string.IsNullOrWhiteSpace(Search) ? null : "%" + Search.Trim().Replace("[", "[[]").Replace("%", "[%]").Replace("_", "[_]") + "%";

    /// <summary>Returns a safe ORDER BY clause: only whitelisted columns, never user text.</summary>
    public string OrderBy(IReadOnlyDictionary<string, string> columns, string defaultKey, bool defaultDesc = false)
    {
        var key = Sort is not null && columns.ContainsKey(Sort) ? Sort : defaultKey;
        var desc = Sort is not null && columns.ContainsKey(Sort) ? Desc : defaultDesc;
        return $"ORDER BY {columns[key]} {(desc ? "DESC" : "ASC")}";
    }

    public ListQuery All() => this with { Page = 1, PageSize = 100_000 };
}

public sealed class Paged<T>
{
    public IReadOnlyList<T> Items { get; init; } = Array.Empty<T>();
    public int Total { get; init; }
    public ListQuery Query { get; init; } = new();
    public int Pages => Math.Max(1, (int)Math.Ceiling(Total / (double)Math.Max(1, Query.PageSize)));
}

/// <summary>Rows that carry the window count as TotalRows (COUNT(*) OVER ()).</summary>
public interface ICounted
{
    int TotalRows { get; }
}

public static class PagedExtensions
{
    public static Paged<T> ToPaged<T>(this IEnumerable<T> rows, ListQuery q) where T : ICounted
    {
        var list = rows.ToList();
        return new Paged<T> { Items = list, Total = list.Count == 0 ? 0 : list[0].TotalRows, Query = q };
    }
}
