using CopilotInsights.Charts;
using CopilotInsights.Data;
using CopilotInsights.Infrastructure;
using Microsoft.AspNetCore.Html;

namespace CopilotInsights.Pages.Leadership;

/// <summary>
/// Every tenant side by side, and the group's totals: for organizations with several Microsoft 365
/// tenants, each loaded into its own database. A tenant counts only where the viewer sees its whole
/// organization, which is decided in that tenant's own database from their sign-in.
/// </summary>
public sealed class GroupModel(PageServices s, TenantAccessor tenants, Metrics metrics) : InsightsPage(s)
{
    public override string Title => "Group overview";
    public override string Persona => "Leadership";
    /// <summary>Each tenant is read for its whole organization, whatever part is picked.</summary>
    public override bool UsesTeam => false;
    public override string Intro => "Each tenant's headline figures for the period, and the group's totals. A tenant is included where you see its whole organization; the others are listed without figures.";

    sealed record Row(string Tenant, Totals? T, string? Why);

    protected override async Task BuildAsync()
    {
        if (!tenants.Many) { Notice = "This dashboard reports on one tenant, so there is nothing to compare here. An administrator can add tenants in the dashboard's settings (Insights:Tenants)."; return; }
        var here = tenants.Current;
        var rows = new List<Row>();
        try
        {
            foreach (var t in tenants.All)
            {
                tenants.Use(t);
                try
                {
                    /* The viewer and their grants from the database, not the page's cached reads: a revoked
                       Organization grant went on showing a tenant's whole organization here for up to 12 hours. */
                    Viewer v;
                    using (QueryCache.Off()) v = await S.Viewers.ResolveAsync();
                    if (!v.OrgWide) { rows.Add(new(t.Name, null, v.InDirectory ? "you see part of this organization" : "you are not in this tenant's directory")); continue; }
                    var c = await S.Contexts.CreateAsync(v, Ctx.Choice, null);
                    rows.Add(new(t.Name, await metrics.TotalsAsync(c), null));
                }
                catch (Microsoft.Data.SqlClient.SqlException ex) { rows.Add(new(t.Name, null, Failure(ex, "Its database did not answer"))); }
            }
        }
        finally { tenants.Use(here); }

        var shown = rows.Where(r => r.T is not null).Select(r => (r.Tenant, T: r.T!)).ToList();
        int people = shown.Sum(r => r.T.People), licensed = shown.Sum(r => r.T.LicensedNow), active = shown.Sum(r => r.T.Active), activeLic = shown.Sum(r => r.T.ActiveLicensed);
        Kpis.Add(new("Tenants included", $"{shown.Count} of {rows.Count}", "tenants whose whole organization you see"));
        Kpis.Add(new("People using Copilot", Num(active), $"had an interaction in the period, of {people:#,0} people"));
        var read = shown.Select(r => r.T.LicensesReadUtc).Where(d => d is not null).DefaultIfEmpty(null).Min();
        Kpis.Add(new("Premium licenses", Num(licensed), $"across the tenants included; license lists read {When(read, "never")}{(shown.Count > 1 ? " (the oldest)" : "")}"));
        Kpis.Add(new("Adoption", Pct(Ratio(activeLic, licensed)), "of Premium license holders used Copilot with the license in the period"));
        if (shown.Count > 0)
            Panels.Add(new("Adoption by tenant", Chart.Bars(shown.Select(r => new Bar(r.Tenant, Ratio(r.T.ActiveLicensed, r.T.LicensedNow),
                $"?tenant={Uri.EscapeDataString(r.Tenant)}", $"{r.T.ActiveLicensed:#,0} of {r.T.LicensedNow:#,0} licensed")).ToList(), Fmt.Percent, "Adoption by tenant", 1),
                "Select a tenant to switch to it.", "half"));
        var tb = new Table { Headers = ["Tenant", "People", "License holders", "Used Copilot", "Adoption", "Interactions", "Used Copilot in the last 7 days"], Numeric = new HashSet<int> { 1, 2, 3, 4, 5, 6 } };
        foreach (var r in rows)
            tb.Rows.Add(r.T is null
                ? [r.Tenant, null, null, null, null, null, new HtmlString($"<span class=\"muted\">{E(r.Why)}</span>")]
                : [new Link(r.Tenant, $"?tenant={Uri.EscapeDataString(r.Tenant)}"), r.T.People, r.T.LicensedNow, r.T.Active, Pct(Ratio(r.T.ActiveLicensed, r.T.LicensedNow)), r.T.Interactions, r.T.ActiveLast7]);
        if (shown.Count > 1) { tb.Rows.Add(["Group", people, licensed, active, Pct(Ratio(activeLic, licensed)), shown.Sum(r => r.T.Interactions), shown.Sum(r => r.T.ActiveLast7)]); tb.KeepLast = 1; }
        CsvTable = tb;
        Panels.Add(new("Every tenant", tb.Render(), "Each tenant's figures come from its own database, for its own last complete day. People counts are of accounts enabled today; interactions include use by people since disabled or departed. Used Copilot in the last 7 days: the 7 days to the period's end.", "full", CsvUrl()));
    }
}
