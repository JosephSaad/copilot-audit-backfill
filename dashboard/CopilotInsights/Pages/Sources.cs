using System.Text;
using CopilotInsights.Infrastructure;
using Microsoft.AspNetCore.Html;

namespace CopilotInsights.Pages;

/// <summary>
/// Data sources: what the ledger is built from, for anyone the dashboard lets in. One card a source: whether it is
/// connected, its last load, the newest day it holds, the reports it opens (those the viewer may open), and for one not
/// connected, what it would add and what it needs. It names no person. Administrators keep Admin › Health for the
/// detail (steps, timings, certificates), which the page links to for them.
/// </summary>
public sealed class SourcesModel(PageServices s) : InsightsPage(s)
{
    public override string Title => "Data sources";
    public override string Persona => "Coverage";
    public override bool UsesFilters => false;
    public override string Intro => "What these reports are built from: each source, whether it is connected and how current it is, and what it opens. A source not connected yet says what it would add and what it needs.";

    protected override async Task BuildAsync()
    {
        var fresh = await S.Settings.IntAsync("FreshnessHours", DataSources.DefaultFreshHours);
        var sources = await DataSources.ReadAsync(Db, fresh, DateTime.UtcNow);
        var connected = sources.Count(DataSources.IsConnected);
        var attention = sources.Where(x => x.State is SourceState.Behind or SourceState.Failed).ToList();
        var audit = sources.First(x => x.Key == "audit");

        Kpis.Add(new("Sources connected", $"{connected} of {sources.Count}", DataSources.Coverage(sources, withCount: false), connected == 0 ? "crit" : null));
        Kpis.Add(new("Copilot data up to", audit.NewestDay?.ToString("d MMM yyyy") ?? "No data yet",
            audit.NewestDay is null ? "nothing has been loaded from the audit log" : "the newest day in the reports", audit.NewestDay is null ? "crit" : null));
        Kpis.Add(new("Sources needing attention", Num(attention.Count),
            attention.Count == 0 ? "every connected source loaded on time" : string.Join(", ", attention.Select(a => $"{a.Name} ({DataSources.Label(a.State).ToLowerInvariant()})")),
            attention.Any(a => a.State == SourceState.Failed) ? "crit" : attention.Count > 0 ? "warn" : null));

        /* The reports each source opens, under their menu titles, among those this viewer may open. */
        var open = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in Nav.For(Viewer, PrivacyMode, false, HiddenReports))
            foreach (var p in g.Pages)
                open.TryAdd(p.Path, $"{g.Persona} › {p.Title}");

        var admin = Viewer.CanAdmin("/admin/health");
        Panels.Add(new("Connected", Cards(sources.Where(DataSources.IsConnected), open),
            $"Late: not loaded in the last {fresh} hours (the agent workbook: 8 days)." + (admin ? " Admin › Health has each step's timings and messages." : "")));
        var off = sources.Where(x => !DataSources.IsConnected(x)).ToList();
        if (off.Count > 0)
            Panels.Add(new("Not connected yet", Cards(off, open),
                "Optional sources: each adds reports or figures when it is set up. Your dashboard administrator can connect them."));
        if (admin)
            Panels.Add(new("For administrators", new HtmlString("<p class=\"sub\">Each step's last run, how long it took, and the certificates the pipeline signs in with: <a href=\"/admin/health\">Admin › Health</a>.</p>")));
    }

    IHtmlContent Cards(IEnumerable<DataSource> list, IReadOnlyDictionary<string, string> open)
    {
        var sb = new StringBuilder("<div class=\"cards sources\">");
        foreach (var x in list)
        {
            sb.Append("<div class=\"card\">");
            sb.Append($"<h3><span>{E(x.Name)}</span> <span class=\"pill {DataSources.Css(x.State)}\">{E(DataSources.Label(x.State))}</span></h3>");
            sb.Append($"<p>{E(x.What)}.</p>");
            if (DataSources.IsConnected(x))
            {
                var facts = new List<string>();
                if (x.LastUtc is not null) facts.Add($"Last loaded {When(x.LastUtc)}");
                if (x.NewestDay is { } d) facts.Add($"newest day {d:d MMM yyyy}");
                if (x.Rows is { } r) facts.Add($"{r:#,0} {x.RowsNoun}");
                sb.Append($"<p>{E(string.Join("; ", facts))}.{(x.Detail is null ? "" : " " + E(x.Detail))}</p>");
            }
            else
                sb.Append($"<p><b>Needs</b> {E(x.Needs)}</p>");
            var links = x.Opens.Where(open.ContainsKey).ToList();
            if (links.Count > 0)
            {
                sb.Append($"<p class=\"cat\">{(DataSources.IsConnected(x) ? "Opens" : "Would add to")}</p><ul>");
                foreach (var l in links) sb.Append($"<li><a href=\"{E(l)}\">{E(open[l])}</a></li>");
                sb.Append("</ul>");
            }
            sb.Append("</div>");
        }
        return new HtmlString(sb.Append("</div>").ToString());
    }
}
