using System.Net;
using CopilotInsights.Infrastructure;
using Microsoft.AspNetCore.Html;

namespace CopilotInsights.Data;

/// <summary>
/// A license's product name for its part number (Microsoft 365 E5 for SPE_E5): dash.SkuName, which Admin › Value
/// names edits, then the price list's name (dash.CostPrice), then the part number as it is. Pages showed part numbers
/// (Microsoft_365_Copilot, POWERAUTOMATE_ATTENDED_RPA) where people look for product names.
/// </summary>
public sealed class SkuNames
{
    readonly IReadOnlyDictionary<string, string> _names;

    public SkuNames(IReadOnlyDictionary<string, string> names) => _names = names;

    /// <summary>No names: every part number shown as it is.</summary>
    public static readonly SkuNames None = new(new Dictionary<string, string>());

    public static async Task<SkuNames> LoadAsync(Db db)
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (await db.ScalarAsync<int>("SELECT CASE WHEN OBJECT_ID(N'dash.CostPrice', N'U') IS NULL THEN 0 ELSE 1 END") == 1)
            foreach (var (item, name) in await db.QueryAsync<(string, string)>("SELECT Item, Name FROM dash.CostPrice;"))
                names[item] = name;
        if (await db.ScalarAsync<int>("SELECT CASE WHEN OBJECT_ID(N'dash.SkuName', N'U') IS NULL THEN 0 ELSE 1 END") == 1)
            foreach (var (part, name) in await db.QueryAsync<(string, string)>("SELECT SkuPartNumber, ProductName FROM dash.SkuName;"))
                names[part] = name;   // the administrator's name wins over the price list's
        return new(names);
    }

    /// <summary>The product name, or the part number when none is known; a list (A, B) named item by item.</summary>
    public string Name(string? part)
    {
        if (string.IsNullOrWhiteSpace(part)) return part ?? "";
        if (part.Contains(','))
            return string.Join(", ", part.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(Name));
        return _names.TryGetValue(part.Trim(), out var n) ? n : part;
    }

    public bool Knows(string? part) => part is not null && _names.ContainsKey(part.Trim());

    /// <summary>For a table cell: the product name, with the part number on hover.</summary>
    public HtmlString Cell(string? part) =>
        string.IsNullOrWhiteSpace(part) ? new HtmlString("") :
        Name(part) == part ? new HtmlString(WebUtility.HtmlEncode(part)) :
        new HtmlString($"<span title=\"{WebUtility.HtmlEncode(part)}\">{WebUtility.HtmlEncode(Name(part))}</span>");
}
