using System.Net;
using CopilotInsights.Infrastructure;
using Microsoft.AspNetCore.Html;

namespace CopilotInsights.Pages.Licensing;

/// <summary>
/// People holding the Copilot license together with another Copilot-family SKU (2.4.3): a second Copilot
/// SKU, Copilot Studio (or its trial), Copilot for Sales or Service and the like, from every SKU the
/// license tool now keeps per person (audit.UserLicenseSku). Each pairing is one to review, and to drop
/// where the Microsoft 365 Copilot license already covers the use.
/// </summary>
public sealed class OverlapModel(PageServices s) : InsightsPage(s)
{
    public override string Title => "Overlapping licenses";
    public override string Persona => "Licensing";
    public override string? RequiredRole => Roles.Licensing;
    public override string? OwnWindow => "today's licenses";
    public override string Intro => "People who hold the Copilot license and another Copilot-family license as well: a second Copilot SKU, Copilot Studio, or Copilot for a role such as Sales or Service. Where the first covers what the second is used for, drop the second.";

    /* Part-number patterns of SKUs that overlap Copilot. The Copilot SKUs themselves are audit.LicenseSku.IsCopilot;
       VIRTUAL_AGENT and CCIBOTS are Copilot Studio's names (it was Power Virtual Agents), trial included. */
    internal static readonly string[] Family = ["%COPILOT%", "%VIRTUAL[_]AGENT%", "%CCIBOTS%"];

    sealed class Row
    {
        public string? DisplayName { get; set; }
        public string UserPrincipalName { get; set; } = "";
        public string? Department { get; set; }
        public string? CopilotSkus { get; set; }
        public string? Others { get; set; }
        public DateTime? LastUsed { get; set; }
        public int TotalRows { get; set; }
    }

    static readonly Dictionary<string, string> Sorts = new()
    {
        ["name"] = "x.DisplayName, x.UserPrincipalName", ["dept"] = "x.Department, x.DisplayName", ["idle"] = "x.LastKey, x.DisplayName",
    };
    static readonly Dictionary<string, string> SortLabels = new() { ["name"] = "Name", ["dept"] = "Department", ["idle"] = "Idle longest" };

    protected override async Task BuildAsync()
    {
        if (await Db.ScalarAsync<int>("SELECT CASE WHEN OBJECT_ID(N'audit.UserLicenseSku', N'U') IS NULL OR NOT EXISTS (SELECT 1 FROM audit.UserLicenseSku) THEN 0 ELSE 1 END") == 0)
        {
            Notice = "Every SKU each person holds arrives with the license tool of 2.4.3: install this release's scripts, then run tools\\Update-CopilotAuditEnrichment.ps1 (or wait for tonight's run).";
            return;
        }
        var sku = Guid.TryParse(Request.Query["sku"], out var g) ? g : (Guid?)null;
        var pg = new Paging(Request, Sorts, "name");
        var args = Ctx.Args(new { q = pg.Like, sku });
        for (var i = 0; i < Family.Length; i++) args.Add($"f{i}", Family[i]);
        var family = string.Join(" OR ", Family.Select((_, i) => $"s.SkuPartNumber LIKE @f{i}"));

        /* h: each person in scope holding a Copilot SKU, with their overlapping SKUs; o: the overlapping ones, one row each. */
        var cte = $@"
WITH held AS (SELECT u.UserPrincipalName, s.SkuId, s.SkuPartNumber, s.IsCopilot,
                     CAST(CASE WHEN s.IsCopilot = 0 AND ({family}) THEN 1 ELSE 0 END AS BIT) AS Other
              FROM audit.UserLicenseSku AS u JOIN audit.LicenseSku AS s ON s.SkuId = u.SkuId),
h AS (SELECT du.UserKey, du.DisplayName, du.UserPrincipalName, du.Department
      FROM bi.DimUser AS du {Ctx.Scope("du.UserKey")}
      WHERE EXISTS (SELECT 1 FROM held WHERE held.UserPrincipalName = du.UserPrincipalName AND held.IsCopilot = 1)
        AND ((SELECT COUNT(*) FROM held WHERE held.UserPrincipalName = du.UserPrincipalName AND held.IsCopilot = 1) > 1
             OR EXISTS (SELECT 1 FROM held WHERE held.UserPrincipalName = du.UserPrincipalName AND held.Other = 1))),
o AS (SELECT h.UserKey, held.SkuId, held.SkuPartNumber FROM h JOIN held ON held.UserPrincipalName = h.UserPrincipalName
      WHERE held.Other = 1 OR (held.IsCopilot = 1 AND (SELECT COUNT(*) FROM held AS c WHERE c.UserPrincipalName = h.UserPrincipalName AND c.IsCopilot = 1) > 1))";

        var bySku = await Db.QueryAsync<(Guid SkuId, string Part, int People, int Bought, int Assigned)>($@"{cte}
SELECT o.SkuId, o.SkuPartNumber, COUNT(*), MAX(ls.Enabled + ls.Warning), MAX(ls.Consumed)
FROM o JOIN audit.LicenseSku AS ls ON ls.SkuId = o.SkuId
GROUP BY o.SkuId, o.SkuPartNumber ORDER BY COUNT(*) DESC;", args);
        var rows = await Db.QueryAsync<Row>($@"{cte},
x AS (SELECT h.DisplayName, h.UserPrincipalName, h.Department,
             (SELECT STRING_AGG(held.SkuPartNumber, N', ') FROM held WHERE held.UserPrincipalName = h.UserPrincipalName AND held.IsCopilot = 1) AS CopilotSkus,
             (SELECT STRING_AGG(held.SkuPartNumber, N', ') FROM held WHERE held.UserPrincipalName = h.UserPrincipalName AND held.Other = 1) AS Others,
             (SELECT MAX(ud.DateKey) FROM dash.UserDay AS ud WHERE ud.UserKey = h.UserKey AND ud.Interactions > 0) AS LastKey
      FROM h WHERE @sku IS NULL OR EXISTS (SELECT 1 FROM o WHERE o.UserKey = h.UserKey AND o.SkuId = @sku))
SELECT x.DisplayName, x.UserPrincipalName, x.Department, x.CopilotSkus, x.Others, dl.[Date] AS LastUsed, COUNT(*) OVER () AS TotalRows
FROM x LEFT JOIN bi.DimDate AS dl ON dl.DateKey = x.LastKey
WHERE (@q IS NULL OR x.DisplayName LIKE @q OR x.UserPrincipalName LIKE @q OR x.Department LIKE @q)
{pg.OrderAndPage()};", args);
        pg.Total = rows.FirstOrDefault()?.TotalRows ?? 0;
        var people = sku is null ? pg.Total : await Db.ScalarAsync<int>($"{cte} SELECT COUNT(*) FROM h;", args);

        Kpis.Add(new("People with an overlap", Num(people), "a Copilot license and another Copilot-family one", people > 0 ? "warn" : null));
        Kpis.Add(new("Overlapping SKUs", Num(bySku.Count)));

        var st = new Table { Headers = ["SKU held as well", "People", "Seats bought", "Seats assigned"], Numeric = new HashSet<int> { 1, 2, 3 }, EmptyText = "No overlap." };
        foreach (var b in bySku)
            st.Rows.Add([new HtmlString($"<a href=\"{WebUtility.HtmlEncode(Ctx.Url(Request.Path, extra: $"sku={b.SkuId}"))}\"{(b.SkuId == sku ? " class=\"on\"" : "")}>{WebUtility.HtmlEncode(b.Part)}</a>"),
                         b.People, b.Bought, b.Assigned]);
        Panels.Add(new("By SKU", st.Render(), "Each SKU held alongside the Copilot license, with how many of the tenant's seats of it are bought and assigned. Open one to list its holders.", "half"));

        var t = new Table
        {
            Headers = ["Name", "Sign-in", "Department", "Copilot license", "Also holds", "Last used Copilot"],
            EmptyText = "Nobody holds the Copilot license with another Copilot-family license.",
        };
        foreach (var r in rows)
            t.Rows.Add([r.DisplayName, r.UserPrincipalName, r.Department, r.CopilotSkus, r.Others ?? "a second Copilot SKU", r.LastUsed is null ? "Never" : r.LastUsed.Value.Date]);
        CsvTable = t;
        var all = sku is null ? "" : $"<p class=\"sub\"><a href=\"{WebUtility.HtmlEncode(Ctx.Url(Request.Path))}\">All SKUs</a></p>";
        Panels.Add(new(sku is null ? "People" : $"People holding {bySku.FirstOrDefault(b => b.SkuId == sku).Part}",
            new HtmlString(all + pg.Controls(Request, SortLabels).Value + t.Render().Value),
            "From every SKU the license tool read for each person at its last run. Copilot-family: a part number containing COPILOT, or Copilot Studio's (VIRTUAL_AGENT, CCIBOTS). " +
            "Whether the Copilot license covers the other depends on what the person uses it for: check with them or their manager before removing it.", "full", CsvUrl()));
    }
}
