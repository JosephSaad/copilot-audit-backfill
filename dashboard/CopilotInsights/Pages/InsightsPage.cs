using System.Net;
using System.Text;
using CopilotInsights.Charts;
using CopilotInsights.Infrastructure;
using Microsoft.AspNetCore.Html;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CopilotInsights.Pages;

public sealed record Kpi(string Label, string Value, string? Note = null, string? Tone = null);

/// <summary>A report panel: a chart or a table, with a title and an optional explanation.</summary>
public sealed record Panel(string Title, IHtmlContent Body, string? Sub = null, string Size = "full", string? Csv = null);

public sealed record Link(string Text, string Href);

/// <summary>A table: cells are strings, numbers (formatted), Link, or IHtmlContent.</summary>
public sealed class Table
{
    public required IReadOnlyList<string> Headers { get; init; }
    public IReadOnlySet<int> Numeric { get; init; } = new HashSet<int>();
    public List<object?[]> Rows { get; } = [];
    public string? EmptyText { get; init; }

    public HtmlString Render()
    {
        if (Rows.Count == 0) return new($"<p class=\"muted\">{WebUtility.HtmlEncode(EmptyText ?? "Nobody here.")}</p>");
        var sb = new StringBuilder("<div class=\"table-wrap\"><table><thead><tr>");
        for (var i = 0; i < Headers.Count; i++)
            sb.Append($"<th{(Numeric.Contains(i) ? " class=\"num\"" : "")}>{WebUtility.HtmlEncode(Headers[i])}</th>");
        sb.Append("</tr></thead><tbody>");
        foreach (var r in Rows)
        {
            sb.Append("<tr>");
            for (var i = 0; i < Headers.Count; i++)
            {
                var v = i < r.Length ? r[i] : null;
                var cls = Numeric.Contains(i) ? " class=\"num\"" : "";
                sb.Append($"<td{cls}>{Cell(v)}</td>");
            }
            sb.Append("</tr>");
        }
        return new(sb.Append("</tbody></table></div>").ToString());
    }

    static string Cell(object? v) => v switch
    {
        null => "<span class=\"muted\">—</span>",
        Link l => $"<a href=\"{WebUtility.HtmlEncode(l.Href)}\">{WebUtility.HtmlEncode(l.Text)}</a>",
        IHtmlContent h => h is HtmlString hs ? hs.Value ?? "" : h.ToString() ?? "",
        DateTime d => d.TimeOfDay == TimeSpan.Zero ? d.ToString("yyyy-MM-dd") : d.ToString("yyyy-MM-dd HH:mm"),
        double x => x.ToString("#,0.#"),
        decimal x => x.ToString("#,0.#"),
        int or long => $"{v:#,0}",
        _ => WebUtility.HtmlEncode(v.ToString() ?? ""),
    };

    public string ToCsv()
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", Headers.Select(Q)));
        foreach (var r in Rows)
            sb.AppendLine(string.Join(",", r.Take(Headers.Count).Select(v => Q(v switch
            {
                null => "", Link l => l.Text, IHtmlContent => "", DateTime d => d.TimeOfDay == TimeSpan.Zero ? d.ToString("yyyy-MM-dd") : d.ToString("yyyy-MM-dd HH:mm:ss"),
                _ => Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture) ?? "",
            }))));
        return sb.ToString();
    }

    static string Q(string s)
    {
        /* Neutralise spreadsheet formulas, then quote. */
        if (s.Length > 0 && "=+-@".Contains(s[0])) s = "'" + s;
        return "\"" + s.Replace("\"", "\"\"") + "\"";
    }
}

public sealed class PageServices(Db db, ViewerService viewers, ReportContextFactory contexts, AuditLog audit, Settings settings, Freshness freshness)
{
    public Freshness Freshness { get; } = freshness;
    public Db Db { get; } = db;
    public ViewerService Viewers { get; } = viewers;
    public ReportContextFactory Contexts { get; } = contexts;
    public AuditLog Audit { get; } = audit;
    public Settings Settings { get; } = settings;
}

/// <summary>
/// Every report page: resolves the viewer and their scope, refuses what they
/// may not see, builds its panels, and writes the audit log. A page names the
/// persona role its named detail needs (RequiredRole); aggregate pages need none.
/// </summary>
public abstract class InsightsPage(PageServices s) : PageModel
{
    protected PageServices S { get; } = s;
    protected Db Db => S.Db;

    public Viewer Viewer { get; private set; } = null!;
    public ReportContext Ctx { get; private set; } = null!;
    public abstract string Title { get; }
    public abstract string Persona { get; }

    public const string Product = "Copilot Insights";

    /// <summary>The browser tab's title: the page, then the product, and the product once where the page is named after it.</summary>
    public static string BrowserTitle(string? title) =>
        string.IsNullOrWhiteSpace(title) || title.Trim() == Product ? Product : $"{title.Trim()} · {Product}";
    public virtual string? Intro => null;
    public virtual string? RequiredRole => null;
    public virtual bool AdminOnly => false;
    public virtual bool UsesFilters => true;
    /// <summary>
    /// A page whose figures do not follow the period filter says what window they use instead,
    /// shown in the filter bar where the period's dates usually are.
    /// </summary>
    public virtual string? OwnWindow => null;

    /// <summary>A role page that privacy mode still allows: it is about one person, on request (a data-subject request).</summary>
    public virtual bool AllowedInPrivacyMode => false;

    public List<Kpi> Kpis { get; } = [];
    public List<Panel> Panels { get; } = [];
    public string? Denied { get; private set; }
    public string? Notice { get; protected set; }
    /// <summary>Privacy mode: pages that name people are refused (see Settings.PrivacyModeAsync).</summary>
    public bool PrivacyMode { get; private set; }
    /// <summary>Set when the data is older than the freshness limit: a banner on every page.</summary>
    public string? Stale { get; private set; }

    /// <summary>Set by pages whose main table can be downloaded (named lists).</summary>
    protected Table? CsvTable { get; set; }

    protected abstract Task BuildAsync();

    protected async Task<bool> PrepareAsync()
    {
        Viewer = await S.Viewers.GetAsync();
        Ctx = await S.Contexts.CreateAsync(Viewer, Request);
        PrivacyMode = await S.Settings.PrivacyModeAsync();
        if (AdminOnly && !Viewer.IsAdmin) Denied = "This page is for dashboard administrators.";
        else if (RequiredRole is not null && PrivacyMode && !AllowedInPrivacyMode)
            Denied = "Privacy mode is on, so no page names anyone. An administrator sets it in Admin > Privacy and retention.";
        else if (RequiredRole is not null && !Viewer.Has(RequiredRole))
            Denied = $"This page lists named people, so it needs the {RequiredRole} role. {Roles.Describe[RequiredRole]} An administrator can grant it.";
        Stale = await S.Freshness.BannerAsync();
        if (UsesFilters) await LoadMarkersAsync();
        if (Denied is not null)
        {
            await S.Audit.WriteAsync(Viewer, "Denied", Request.Path, Ctx.ScopeLabel, Denied);
            Response.StatusCode = 403;
            return false;
        }
        return true;
    }

    public virtual async Task<IActionResult> OnGetAsync()
    {
        if (!await PrepareAsync()) return Page();
        await BuildAsync();
        await S.Audit.WriteAsync(Viewer, "View", Request.Path, Ctx.ScopeLabel, $"days={Ctx.Days}{Request.QueryString}");
        return Page();
    }

    public async Task<IActionResult> OnGetCsvAsync()
    {
        if (!await PrepareAsync()) return Page();
        await BuildAsync();
        if (CsvTable is null) return NotFound();
        await S.Audit.WriteAsync(Viewer, "Export", Request.Path, Ctx.ScopeLabel, $"{CsvTable.Rows.Count} rows{Request.QueryString}");
        var name = Title.ToLowerInvariant().Replace(' ', '-') + $"-{Ctx.End:yyyyMMdd}.csv";
        return File(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(CsvTable.ToCsv())).ToArray(), "text/csv", name);
    }

    /// <summary>
    /// An admin change: only for administrators, always audited, then back to the page
    /// with a message. The action returns what it did, for the audit log and the message.
    /// </summary>
    protected async Task<IActionResult> AdminChangeAsync(Func<Task<string>> change)
    {
        if (!await PrepareAsync()) return Page();
        if (!Viewer.IsAdmin)
        {
            await S.Audit.WriteAsync(Viewer, "Denied", Request.Path, null, "admin change refused");
            return StatusCode(403);
        }
        string done;
        try { done = await change(); }
        catch (ArgumentException ex) { done = "Not changed: " + ex.Message; }
        await S.Audit.WriteAsync(Viewer, "Admin", Request.Path, null, done);
        TempData["done"] = done;
        var q = Request.Query.Where(kv => kv.Key != "handler").Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value.ToString())}").ToList();
        return Redirect(Request.Path + (q.Count > 0 ? "?" + string.Join("&", q) : ""));
    }

    /// <summary>
    /// A change a persona role may make (Security triaging a flag, a viewer saving their own view):
    /// the role checked, always audited, then back to the page with a message.
    /// </summary>
    protected async Task<IActionResult> RoleChangeAsync(string? role, Func<Task<string>> change)
    {
        if (!await PrepareAsync()) return Page();
        if (role is not null && !Viewer.Has(role))
        {
            await S.Audit.WriteAsync(Viewer, "Denied", Request.Path, null, $"change refused: needs {role}");
            return StatusCode(403);
        }
        string done;
        try { done = await change(); }
        catch (ArgumentException ex) { done = "Not changed: " + ex.Message; }
        await S.Audit.WriteAsync(Viewer, "Change", Request.Path, null, done);
        TempData["done"] = done;
        var q = Request.Query.Where(kv => kv.Key != "handler").Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value.ToString())}").ToList();
        return Redirect(Request.Path + (q.Count > 0 ? "?" + string.Join("&", q) : ""));
    }

    /// <summary>The message a change left for the page it redirected back to.</summary>
    protected void ShowDone()
    {
        if (TempData["done"] is string d) Notice = d;
    }

    /* ---- forms the pages write themselves (anti-forgery token included) ---------------------- */

    protected string Form(string handler, string body, string? extraQuery = null) =>
        $"<form class=\"admin\" method=\"post\" action=\"{E(Request.Path + "?handler=" + handler + (extraQuery is null ? "" : "&" + extraQuery))}\">{Token()}{body}</form>";

    /// <summary>A form on one line, posting back with the page's own query (its period, scope and filters).</summary>
    protected string InlineForm(string handler, string body)
    {
        var q = Request.Query.Where(kv => kv.Key is not ("handler" or "p")).Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value.ToString())}");
        return $"<form class=\"inline\" method=\"post\" action=\"{E(Request.Path + "?" + string.Join("&", q.Append("handler=" + handler)))}\">{Token()}{body}</form>";
    }

    protected static string Input(string name, string label, string? value, int size = 24, string type = "text") =>
        $"<label>{E(label)}<input type=\"{type}\" name=\"{name}\" value=\"{E(value)}\" size=\"{size}\"/></label>";

    protected static string Select(string name, string label, IEnumerable<string> options, string? selected) =>
        $"<label>{E(label)}<select name=\"{name}\">" +
        string.Concat(options.Select(o => $"<option{(o == selected ? " selected" : "")}>{E(o)}</option>")) + "</select></label>";

    protected static string Button(string text, string? confirm = null, bool quiet = false) =>
        $"<button class=\"btn{(quiet ? " quiet" : "")}\" type=\"submit\"{(confirm is null ? "" : $" data-confirm=\"{E(confirm)}\"")}>{E(text)}</button>";

    protected static string Hidden(string name, object? value) => $"<input type=\"hidden\" name=\"{name}\" value=\"{E(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture))}\"/>";

    /// <summary>A hidden anti-forgery field for forms the page writes itself.</summary>
    protected string Token()
    {
        var af = HttpContext.RequestServices.GetRequiredService<Microsoft.AspNetCore.Antiforgery.IAntiforgery>();
        var t = af.GetAndStoreTokens(HttpContext);
        return $"<input type=\"hidden\" name=\"{t.FormFieldName}\" value=\"{WebUtility.HtmlEncode(t.RequestToken)}\"/>";
    }

    protected static string E(string? s) => WebUtility.HtmlEncode(s ?? "");

    /* ---- helpers for pages ---------------------------------------------------- */

    protected static string Pct(double? v) => Chart.Format(v, Fmt.Percent);
    protected static string Num(double? v) => Chart.Format(v, Fmt.Count);
    protected static double? Ratio(double a, double b) => b > 0 ? a / b : null;
    /// <summary>The trend's points (days or weeks), oldest first, and their labels.</summary>
    protected IReadOnlyList<Bucket> Buckets() => Ctx.Buckets();
    protected IReadOnlyList<string> Labels()
    {
        var buckets = Ctx.Buckets();
        var axis = new Axis(buckets.Select(b => b.Label));
        foreach (var m in _markers)
        {
            var key = Ctx.Daily ? m.Day.Date : ReportContext.WeekEnding(m.Day);
            var i = buckets.ToList().FindIndex(b => b.Key == key);
            if (i >= 0) axis.Markers.Add(new(i, m.Label, m.Kind));
        }
        return axis;
    }

    List<(DateTime Day, string Label, string Kind)> _markers = [];

    /// <summary>The events in the period, for the trend charts (Admin > Classifications and events).</summary>
    async Task LoadMarkersAsync()
    {
        try
        {
            _markers = await Db.QueryAsync<(DateTime, string, string)>(
                "SELECT Day, Label, Kind FROM dash.EventMarker WHERE Day BETWEEN @d0 AND @d1 ORDER BY Day;", new { d0 = Ctx.Start.AddDays(-6), d1 = Ctx.End });
        }
        catch (Microsoft.Data.SqlClient.SqlException) { _markers = []; }
    }
    protected string TrendNote => Ctx.Daily ? "Each point is a day." : "Each point is a Saturday-to-Friday week, labeled by its Friday; the first and last may be partial.";

    /// <summary>The CSV export of this page's list, with its current search and sort.</summary>
    protected string CsvUrl()
    {
        var q = Request.Query.Where(kv => kv.Key is not ("p" or "handler")).Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value.ToString())}");
        return Request.Path + "?" + string.Join("&", q.Append("handler=csv"));
    }

    /// <summary>Folds rows for groups smaller than the minimum into one "Smaller groups" row.</summary>
    protected List<T> FoldSmall<T>(IEnumerable<T> rows, Func<T, int> people, Func<IReadOnlyList<T>, T> combine, out int folded)
    {
        var list = rows.ToList();
        var small = list.Where(r => people(r) < Ctx.MinGroupSize).ToList();
        folded = small.Count;
        var keep = list.Where(r => people(r) >= Ctx.MinGroupSize).ToList();
        if (small.Count > 0) keep.Add(combine(small));
        return keep;
    }
}
