using System.Net;
using System.Text;
using CopilotInsights.Charts;
using CopilotInsights.Infrastructure;
using Microsoft.AspNetCore.Html;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CopilotInsights.Pages;

/// <summary>A headline figure; Href (2.6.6) is the report with the detail behind it, which the figure links to.</summary>
public sealed record Kpi(string Label, string Value, string? Note = null, string? Tone = null, string? Href = null, KpiWhy? Why = null);

/// <summary>
/// How a figure is worked out (2.6.9): its formula with the values in it, and the assumptions it rests on, each with its
/// value now and, for an administrator who may change it, a link to the field it is set in. Shown under the figure.
/// </summary>
public sealed record KpiWhy(string Formula, IReadOnlyList<WhyInput> Inputs);

/// <summary>One assumption behind a figure: its name, its value now, and where to change it (null for anyone who may not).</summary>
public sealed record WhyInput(string Label, string Value, string? Href);

/// <summary>A report panel: a chart or a table, with a title and an optional explanation.</summary>
public sealed record Panel(string Title, IHtmlContent Body, string? Sub = null, string Size = "full", string? Csv = null);

public sealed record Link(string Text, string Href);

/// <summary>
/// A table: cells are strings, numbers (formatted), Link, or IHtmlContent. Its column headers sort it
/// (2.4.5): a list paged on the server through its Paging keys (ServerSort), any other table in the
/// browser (site.js), by the cells' values rather than their text. Columns of buttons don't sort, nor does
/// a table whose order is its meaning (Sortable = false), and the last KeepLast rows (a total, "Smaller
/// groups") stay at the bottom.
/// </summary>
public sealed class Table
{
    public required IReadOnlyList<string> Headers { get; init; }
    public IReadOnlySet<int> Numeric { get; init; } = new HashSet<int>();
    public List<object?[]> Rows { get; } = [];
    public string? EmptyText { get; init; }
    /// <summary>False where the rows' order is the point: steps, a time series, levels, a single row.</summary>
    public bool Sortable { get; init; } = true;
    /// <summary>The number of rows at the end that stay there whatever the sort: a total, or "Smaller groups".</summary>
    public int KeepLast { get; set; }
    /// <summary>For a list paged on the server: each column's Paging key (null: not sortable), and whether the key's own order descends.</summary>
    public ServerSort? Server { get; init; }
    /// <summary>
    /// The zone to show times in (2.4.10): a column whose header ends "(UTC)" holds UTC times, and with a zone set they are
    /// shown in it, the header saying which ("When (Eastern Time)"). Without one they stay UTC, as the header says.
    /// </summary>
    public TimeZoneInfo? Zone { get; init; }

    bool InZone(int i) => Zone is not null && i < Headers.Count && Headers[i].EndsWith("(UTC)", StringComparison.Ordinal);
    string Header(int i) => InZone(i) ? Headers[i][..^"(UTC)".Length] + $"({CopilotInsights.Data.Zones.Label(Zone!)})" : Headers[i];
    object? Shown(int i, object? v) => v is DateTime d && InZone(i) ? CopilotInsights.Data.Zones.FromUtc(d, Zone!) : v;

    public sealed record ServerSort(Paging Pager, HttpRequest Request, IReadOnlyList<string?> Keys, IReadOnlySet<int>? Descending = null);

    /// <summary>A column sorts when it has a header and no cell in it is a control (a form or a button) or a chart (a sparkline).</summary>
    bool ColumnSorts(int i) =>
        !string.IsNullOrWhiteSpace(Headers[i])
        && !Rows.Any(r => i < r.Length && r[i] is IHtmlContent h && Html(h) is var x
                          && (x.Contains("<form", StringComparison.OrdinalIgnoreCase) || x.Contains("<button", StringComparison.OrdinalIgnoreCase) || x.Contains("<svg", StringComparison.OrdinalIgnoreCase)));

    static string Html(IHtmlContent h) => h is HtmlString hs ? hs.Value ?? "" : h.ToString() ?? "";

    public HtmlString Render()
    {
        if (Rows.Count == 0) return new($"<p class=\"muted\">{WebUtility.HtmlEncode(EmptyText ?? "Nobody here.")}</p>");
        var client = Server is null && Sortable && Rows.Count - KeepLast > 1;
        /* 2.5.7: a table sent whole (not paged on the server) with more than a page of rows shows a page at a time in the
           browser (site.js), as the server's lists do; a sort orders every row and starts again from the first page. */
        var paged = Server is null && Rows.Count - KeepLast > Paging.PageSize;
        var sb = new StringBuilder($"<div class=\"table-wrap\"><table{(client ? " class=\"sortable\"" : "")}{(paged ? $" data-page-size=\"{Paging.PageSize}\"" : "")}><thead><tr>");
        for (var i = 0; i < Headers.Count; i++)
        {
            /* scope: a screen reader reads each cell with its column's heading (2.4.10). */
            var cls = " scope=\"col\"" + (Numeric.Contains(i) ? " class=\"num\"" : "");
            var text = WebUtility.HtmlEncode(Header(i));
            if (Server is { } sv && i < sv.Keys.Count && sv.Keys[i] is { } key && sv.Pager.Has(key))
            {
                var aria = sv.Pager.AriaSort(key, sv.Descending?.Contains(i) ?? false);
                sb.Append($"<th{cls}{(aria is null ? "" : $" aria-sort=\"{aria}\"")}><a class=\"sort\" href=\"{WebUtility.HtmlEncode(sv.Pager.SortLink(sv.Request, key))}\">{text}</a></th>");
            }
            else if (client && ColumnSorts(i))
                sb.Append($"<th{cls}><button type=\"button\" class=\"sort\">{text}</button></th>");
            else
                sb.Append($"<th{cls}>{text}</th>");
        }
        sb.Append("</tr></thead><tbody>");
        for (var n = 0; n < Rows.Count; n++)
        {
            var r = Rows[n];
            sb.Append(n >= Rows.Count - KeepLast ? "<tr data-keep=\"1\">" : "<tr>");
            for (var i = 0; i < Headers.Count; i++)
            {
                var v = Shown(i, i < r.Length ? r[i] : null);
                var cls = Numeric.Contains(i) ? " class=\"num\"" : "";
                var sortValue = client ? SortValue(v) : null;
                sb.Append($"<td{cls}{(sortValue is null ? "" : $" data-v=\"{sortValue}\"")}>{Cell(v)}</td>");
            }
            sb.Append("</tr>");
        }
        sb.Append("</tbody></table></div>");
        /* 2.5.8: a list paged on the server has its page links under it, as a table paged in the browser does. */
        if (Server is { } onServer && !onServer.Pager.ForExport) sb.Append(onServer.Pager.Nav(onServer.Request).Value);
        return new(sb.ToString());
    }

    /// <summary>What a cell sorts by, where its text would mislead: a number's value, a date as yyyyMMddHHmm.</summary>
    static string? SortValue(object? v) => v switch
    {
        int or long or short or byte => Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture),
        double d => double.IsFinite(d) ? d.ToString("R", System.Globalization.CultureInfo.InvariantCulture) : null,
        decimal m => m.ToString(System.Globalization.CultureInfo.InvariantCulture),
        float f => float.IsFinite(f) ? f.ToString("R", System.Globalization.CultureInfo.InvariantCulture) : null,
        DateTime d => d.ToString("yyyyMMddHHmm", System.Globalization.CultureInfo.InvariantCulture),
        _ => null,
    };

    static string Cell(object? v) => v switch
    {
        null => "<span class=\"muted\">—</span>",
        Link l => $"<a href=\"{WebUtility.HtmlEncode(l.Href)}\">{WebUtility.HtmlEncode(l.Text)}</a>",
        IHtmlContent h => Html(h),
        DateTime d => d.TimeOfDay == TimeSpan.Zero ? d.ToString("yyyy-MM-dd") : d.ToString("yyyy-MM-dd HH:mm"),
        double x => x.ToString("#,0.#"),
        decimal x => x.ToString("#,0.#"),
        int or long => $"{v:#,0}",
        _ => WebUtility.HtmlEncode(v.ToString() ?? ""),
    };

    public string ToCsv()
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", Headers.Select((_, i) => Q(Header(i)))));
        foreach (var r in Rows)
            sb.AppendLine(string.Join(",", r.Take(Headers.Count).Select((x, i) => Shown(i, x)).Select(Field)));
        return sb.ToString();
    }

    /// <summary>
    /// One cell as a CSV field (2.4.12). A cell the page builds as HTML gives its text: the CSV left it empty, so the
    /// flagged interactions came without their signals, the agents costing credits without their names and the
    /// contracts without their days to renewal. A control or a chart in a cell still gives nothing. A number is
    /// written as a number: the formula guard is for text, and it made every negative figure the text '-28.68,
    /// which a spreadsheet does not add up.
    /// </summary>
    static string Field(object? v) => v switch
    {
        null => Q(""),
        Link l => Q(l.Text),
        IHtmlContent h => Q(Text(h)),
        DateTime d => Q(d.TimeOfDay == TimeSpan.Zero ? d.ToString("yyyy-MM-dd") : d.ToString("yyyy-MM-dd HH:mm:ss")),
        double x when !double.IsFinite(x) => Q(x.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        float x when !float.IsFinite(x) => Q(x.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal =>
            Quote(Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture) ?? ""),
        _ => Q(Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture) ?? ""),
    };

    /// <summary>A cell that holds a form, a button or a chart, as <see cref="ColumnSorts"/> tells them: nothing to export.</summary>
    static bool IsControl(string html) =>
        html.Contains("<form", StringComparison.OrdinalIgnoreCase) || html.Contains("<button", StringComparison.OrdinalIgnoreCase) || html.Contains("<svg", StringComparison.OrdinalIgnoreCase);

    /// <summary>What an HTML cell says, as a reader sees it: its tags dropped, its entities decoded, its spaces collapsed.</summary>
    static string Text(IHtmlContent h)
    {
        var html = Html(h);
        if (IsControl(html)) return "";
        var text = WebUtility.HtmlDecode(System.Text.RegularExpressions.Regex.Replace(html, "<[^>]*>", " "));
        return System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();
    }

    static string Q(string s)
    {
        /* Neutralise spreadsheet formulas, then quote. */
        if (s.Length > 0 && "=+-@".Contains(s[0])) s = "'" + s;
        return Quote(s);
    }

    static string Quote(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";
}

public sealed class PageServices(Db db, ViewerService viewers, ReportContextFactory contexts, AuditLog audit, Settings settings, Freshness freshness, DataStamp stamp)
{
    public DataStamp Stamp { get; } = stamp;
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
    /// <summary>A page that shows no usage data and that an administrator outside the dashboard's levels may still open (the start page).</summary>
    public virtual bool OpenToAdmins => false;
    public virtual bool UsesFilters => true;
    /// <summary>
    /// A page whose figures do not follow the period filter says what window they use instead,
    /// shown in the filter bar where the period's dates usually are.
    /// </summary>
    public virtual string? OwnWindow => null;
    /// <summary>
    /// Whether the period chosen changes this page's figures (2.5.0). False for a page whose figures do not follow it
    /// and that has no window worth naming (a list as it stands today): the period box and its dates are left out. A
    /// page with a window of its own says it in <see cref="OwnWindow"/> instead.
    /// </summary>
    public virtual bool UsesPeriod => true;
    /// <summary>
    /// Whether the part of the organization chosen (Look inside a team) changes this page's figures (2.5.0). False for a
    /// page that is the whole tenant's whatever is picked, such as the license pool: the team box, and the links down
    /// the organization beside it, are left out.
    /// </summary>
    public virtual bool UsesTeam => true;

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

    /// <summary>
    /// Builds a report page with its queries answered from memory where the data has not changed since they last ran
    /// (2.4.7). Who the viewer is and what they may see were settled before this, from the database; admin pages, the
    /// CSV and file exports and every form always read the database. A page that reads a list edited through the day
    /// is kept under that list's version, read for every page (2.4.11, see <see cref="Db"/>).
    /// </summary>
    /// <summary>
    /// Builds the page for the warm-up (PageWarmer): the same queries a viewer of the whole organization runs, with
    /// nothing rendered, logged or sent. Pages that name people are left out in privacy mode, as they are for everyone.
    /// </summary>
    internal async Task WarmAsync(Viewer viewer, int days)
    {
        Warming = true;
        Viewer = viewer;
        Ctx = await S.Contexts.CreateAsync(viewer, days, null);
        PrivacyMode = await S.Settings.PrivacyModeAsync();
        Zone = await S.Settings.ZoneAsync();
        if (RequiredRole is not null && PrivacyMode && !AllowedInPrivacyMode) return;
        if (Pages.HiddenReports.Is(await Pages.HiddenReports.GetAsync(S.Settings), Request.Path)) return;
        await S.Stamp.EnsureAsync();
        using (QueryCache.Use()) await BuildAsync();
    }

    /// <summary>True while the warm-up builds the page: there is no request behind it, so no message from a redirect.</summary>
    protected bool Warming { get; private set; }

    async Task BuildCachedAsync()
    {
        if (AdminOnly) { await BuildAsync(); return; }
        await S.Stamp.EnsureAsync();
        using (QueryCache.Use()) await BuildAsync();
    }

    /// <summary>The zone times are shown in (2.4.10): Admin > Access, else the data's own, else the server's.</summary>
    public TimeZoneInfo Zone { get; private set; } = TimeZoneInfo.Utc;

    /// <summary>
    /// The zone the data's days and hours were cut in when it was loaded, by name ("Eastern Time"): for the reports that
    /// show an hour of the day or a weekday. The pages said "the reporting time zone", which named nothing.
    /// </summary>
    public string DataZone { get; private set; } = "the server's time zone";

    /// <summary>
    /// A chart a page puts inside its own HTML, drawn for where it will sit (2.4.10): on a phone (the narrow cookie
    /// theme.js sets) at the phone's width, else at <paramref name="width"/>, or as drawn when that is null. A chart a
    /// panel holds on its own is sized by the page layout; this is for the ones it cannot see.
    /// </summary>
    protected string Sized(Microsoft.AspNetCore.Html.HtmlString chart, int? width = null)
    {
        var narrow = !Warming && Request.Cookies["narrow"] == "1";
        return chart is CopilotInsights.Charts.ChartSvg c && (narrow || width is not null) ? c.Render(narrow ? 320 : width!.Value) : chart.Value ?? "";
    }

    /// <summary>A UTC time in that zone, with its name: "2026-10-02 08:45 Eastern Time".</summary>
    protected string When(DateTime? utc, string none = "not yet") => utc is null ? none : CopilotInsights.Data.Zones.Stamp(utc, Zone);

    /// <summary>
    /// Who to ask, on a page that was refused (2.4.10): up to three people granted the Admin role by name, so "an
    /// administrator can grant it" names someone. Nobody is named in privacy mode, and a group grant names no one.
    /// </summary>
    public IReadOnlyList<string> Administrators { get; private set; } = [];

    async Task<IReadOnlyList<string>> AdministratorsAsync()
    {
        if (PrivacyMode) return [];
        try
        {
            /* Full administrators only (2.6.4): one limited to some Admin pages may not have Access among them. */
            return await Db.QueryAsync<string>(@"
DECLARE @full NVARCHAR(100) = CASE WHEN COL_LENGTH(N'dash.RoleGrant', N'Pages') IS NULL THEN N'' ELSE N' AND NULLIF(LTRIM(RTRIM(g.Pages)), N'''') IS NULL' END;
DECLARE @sql NVARCHAR(MAX) = N'
SELECT TOP (3) COALESCE(p.DisplayName, g.Principal)
FROM dash.RoleGrant AS g LEFT JOIN dash.Person AS p ON p.Upn = g.Principal
WHERE g.Role = N''Admin'' AND g.PrincipalType = N''User''' + @full + N'
ORDER BY CASE WHEN p.AccountEnabled = 1 THEN 0 ELSE 1 END, g.GrantedUtc;';
EXEC sys.sp_executesql @sql;");
        }
        catch (Microsoft.Data.SqlClient.SqlException) { return []; }
    }

    /// <summary>The reports this viewer opens most (2.4.9), by path, most opened first; see <see cref="MostUsed"/>.</summary>
    public IReadOnlyList<string> MostUsedPaths { get; private set; } = [];

    /// <summary>The reports turned off in Admin > Reports shown (2.4.9), by path.</summary>
    public IReadOnlySet<string> HiddenReports { get; private set; } = new HashSet<string>();

    protected async Task<bool> PrepareAsync()
    {
        Viewer = await S.Viewers.GetAsync();
        Ctx = await S.Contexts.CreateAsync(Viewer, Request, forPage: true);
        PrivacyMode = await S.Settings.PrivacyModeAsync();
        Zone = await S.Settings.ZoneAsync();
        DataZone = CopilotInsights.Data.Zones.Label(CopilotInsights.Data.Zones.Resolve(null, await S.Settings.ReportingTimeZoneAsync()));
        HiddenReports = await Pages.HiddenReports.GetAsync(S.Settings);
        if (HttpContext.RequestServices.GetService<MostUsed>() is { } most) MostUsedPaths = await most.PathsAsync(Viewer);
        /* A report an administrator turned off is not there, for anyone: its page, its files and its forms. */
        var turnedOff = !AdminOnly && Pages.HiddenReports.Is(HiddenReports, Request.Path);
        if (turnedOff) Denied = "This report is turned off. An administrator can turn it back on in Admin > Reports shown.";
        else if (!Viewer.Admitted && !(AdminOnly ? Viewer.CanAdmin(Request.Path) : OpenToAdmins && Viewer.AnyAdmin)) Denied = LimitRefusal(Viewer);
        else if (AdminOnly && !Viewer.CanAdmin(Request.Path))
            Denied = Viewer.AnyAdmin ? "This Admin page is not one of those your Admin role covers. A full administrator can add it in Admin > Access." : "This page is for dashboard administrators.";
        else if (Nav.SectionOf(Request.Path) is { Role: { } section } g && !Viewer.Has(section))
            Denied = $"{g.Persona} pages are for holders of the {section} role. An administrator can grant it in Admin > Access.";
        else if (RequiredRole is not null && PrivacyMode && !AllowedInPrivacyMode)
            Denied = "Privacy mode is on, so no page names anyone. An administrator sets it in Admin > Privacy and retention.";
        else if (RequiredRole is not null && !Viewer.Has(RequiredRole))
            Denied = $"This page lists named people, so it needs the {RequiredRole} role. {Roles.Describe[RequiredRole]} An administrator can grant it.";
        Stale = await S.Freshness.BannerAsync();
        if (UsesFilters) await LoadMarkersAsync();
        if (Denied is not null)
        {
            Administrators = await AdministratorsAsync();
            await S.Audit.WriteAsync(Viewer, "Denied", Request.Path, Ctx.ScopeLabel, Denied);
            Response.StatusCode = turnedOff ? 404 : 403;
            return false;
        }
        return true;
    }

    /// <summary>What a viewer outside the dashboard's levels is told (2.4.4), on every page, export and form.</summary>
    public static string LimitRefusal(Viewer viewer) =>
        $"This dashboard is for leaders at levels 1 to {viewer.MaxLevel} of the organization" +
        (viewer.Level is { } l ? $"; you are at level {l}." : ", and your account is not placed in the org chart.") +
        (viewer.IsAdmin ? " As an administrator, you can open the Admin pages." : viewer.AnyAdmin ? " You can open the Admin pages your Admin role covers." : " An administrator can grant access in Admin > Access.");

    public virtual async Task<IActionResult> OnGetAsync()
    {
        if (!await PrepareAsync()) return Page();
        await BuildCachedAsync();
        await S.Audit.WriteAsync(Viewer, "View", Request.Path, Ctx.ScopeLabel, $"days={Ctx.Days}{Request.QueryString}");
        return Page();
    }

    /// <summary>Whose names this page's name boxes may offer (see <see cref="CopilotInsights.Data.PersonLookup"/>); none unless the page says.</summary>
    public virtual CopilotInsights.Data.LookupKind Lookup => CopilotInsights.Data.LookupKind.None;

    /// <summary>
    /// The names a name box offers for what has been typed, as JSON: nothing under three letters, at most twenty, a
    /// first or a last name beginning with them. The page's own checks come first (its role, its section, privacy
    /// mode), and its scope decides who can be found. Each search is in the audit log, as a page view is.
    /// </summary>
    public async Task<IActionResult> OnGetPeopleAsync(string? q)
    {
        if (!await PrepareAsync()) return new JsonResult(Array.Empty<object>()) { StatusCode = Response.StatusCode };
        if (Lookup == CopilotInsights.Data.LookupKind.None) return NotFound();
        /* In privacy mode nobody is offered by name, but on a page privacy mode leaves open (a person's own record, on request). */
        var none = PrivacyMode && !AllowedInPrivacyMode;
        var found = none ? [] : await CopilotInsights.Data.PersonLookup.FindAsync(Db, Ctx, Lookup, q);
        if (CopilotInsights.Data.PersonLookup.Term(q) is { } term)
            await S.Audit.WriteAsync(Viewer, "Lookup", Request.Path, AdminOnly ? null : Ctx.ScopeLabel, $"\"{term}\": {found.Count} offered");
        Response.Headers.CacheControl = "no-store";
        return new JsonResult(found.Select(p => new { value = p.Upn, label = CopilotInsights.Data.PersonLookup.Label(p, Lookup == CopilotInsights.Data.LookupKind.Managers) }));
    }

    /// <summary>
    /// A box that takes a person: as three letters of a first or a last name are typed, the names that match are offered
    /// (site.js asks <see cref="OnGetPeopleAsync"/>), and choosing one fills in the sign-in. A sign-in typed in full is
    /// taken as it is, with or without script. Boxes on one page can share one list (<paramref name="list"/>).
    /// </summary>
    protected string PersonBox(string name, string label, string? value, int size = 34, string? tip = null, string list = "people-found")
    {
        var lookup = E(Request.Path + "?handler=people" + TenantQuery);
        var text = tip is null ? E(label) : ((HtmlString)InfoTip.After(label, tip)).Value;
        return $"<label>{text}<input type=\"text\" name=\"{name}\" value=\"{E(value)}\" size=\"{size}\" list=\"{list}\" autocomplete=\"off\" " +
               $"data-lookup=\"{lookup}\" data-min=\"{CopilotInsights.Data.PersonLookup.MinLetters}\" placeholder=\"3 letters of a first or last name\"/></label>" +
               (_lists.Add(list) ? $"<datalist id=\"{list}\"></datalist>" : "");
    }

    readonly HashSet<string> _lists = [];

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
        /* On an Admin page, whoever may open it (2.6.4: a limited administrator, their own pages); elsewhere, full administrators. */
        if (!Viewer.CanAdmin(Request.Path))
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

    /// <summary>The line a revoke, reclaim or training list opens with, saying which levels it never lists (2.4.6); empty when none are.</summary>
    protected async Task<string> ProtectedNoteAsync()
    {
        var note = await CopilotInsights.Data.ProtectedLevels.NoteAsync(S.Settings);
        return note.Length == 0 ? "" : $"<p class=\"sub protected\">{E(note.Trim())}</p>";
    }

    IReadOnlyList<CopilotInsights.Data.AssumptionValue>? _assumptions;

    /// <summary>
    /// How a figure is worked out (2.6.9): the formula, with the values put in, and the assumptions it rests on by their
    /// keys in the register (<see cref="CopilotInsights.Data.Assumptions"/>), each with its value now; for an administrator
    /// who may open the page it is set on, a link that opens that page at the field.
    /// </summary>
    protected async Task<KpiWhy> WhyAsync(string formula, params string[] keys)
    {
        _assumptions ??= await HttpContext.RequestServices.GetRequiredService<CopilotInsights.Data.AssumptionData>().AllAsync();
        var inputs = keys.Select(k => _assumptions.FirstOrDefault(a => a.Def.Key == k)).OfType<CopilotInsights.Data.AssumptionValue>()
            .Select(a => new WhyInput(a.Def.Label, a.Shown, !Warming && Viewer.CanAdmin(a.Def.Page) ? CopilotInsights.Data.Assumptions.EditHref(a.Def) : null)).ToList();
        return new(formula, inputs);
    }

    /// <summary>A figure's "how it is worked out" as HTML (2.6.9), for a page that draws its own tiles; _Report draws a Kpi's.</summary>
    protected static string WhyHtml(KpiWhy? w) => w is null ? "" :
        $"<details class=\"kwhy\"><summary>How it is worked out</summary><p>{E(w.Formula)}</p>" +
        (w.Inputs.Count == 0 ? "" : "<ul>" + string.Concat(w.Inputs.Select(i => $"<li>{E(i.Label)}: <b>{E(i.Value)}</b>" +
            (i.Href is null ? "" : $" · <a href=\"{E(i.Href)}\">Change it</a>") + "</li>")) + "</ul>") + "</details>";

    /// <summary>The message a change left for the page it redirected back to.</summary>
    protected void ShowDone()
    {
        if (Warming) return;
        if (TempData["done"] is string d) Notice = d;
    }

    /* ---- forms the pages write themselves (anti-forgery token included) ---------------------- */

    /// <summary>
    /// The address of a report, the period and team kept, if this viewer may open it; null if not (2.6.6). A figure or
    /// row that leads to its detail links only where the detail would open: a leader without the Licensing role sees
    /// the count of licenses to take back, but no link to the list that names the people. The test is the menu's
    /// (<see cref="Nav.Allowed"/>), section role included; a page the menu does not list is open.
    /// </summary>
    protected string? Open(string path, string? extra = null) => Open(path, extra, keepOrg: true);

    /// <summary>
    /// The same, choosing whether the team shown is kept: a page that shows the whole organization whatever the team
    /// (Costs and savings) passes false, so its figures lead to lists of the whole organization, as they count it.
    /// </summary>
    protected string? Open(string path, string? extra, bool keepOrg)
    {
        /* A path that carries its own query ("/forecasting/actions?kind=revoke") keeps it after the period and team. */
        var parts = path.Split('?', 2);
        var bare = parts[0];
        if (parts.Length > 1 && parts[1].Length > 0) extra = extra is null ? parts[1] : parts[1] + "&" + extra;
        foreach (var g in Nav.Groups)
            foreach (var page in g.Pages)
                if (string.Equals(page.Path, bare, StringComparison.OrdinalIgnoreCase))
                    return (g.Role is null || Viewer.Has(g.Role)) && Nav.Allowed(page, Viewer, PrivacyMode) ? Ctx.Url(bare, keepOrg: keepOrg, extra: extra) : null;
        return Ctx.Url(bare, keepOrg: keepOrg, extra: extra);
    }

    protected string Form(string handler, string body, string? extraQuery = null) =>
        $"<form class=\"admin\" method=\"post\" action=\"{E(Request.Path + "?handler=" + handler + (extraQuery is null ? "" : "&" + extraQuery) + TenantQuery)}\">{Token()}{body}</form>";

    /// <summary>A form on one line, posting back with the page's own query (its period, scope and filters).</summary>
    protected string InlineForm(string handler, string body)
    {
        var q = Request.Query.Where(kv => kv.Key is not ("handler" or "p" or "tenant")).Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value.ToString())}");
        return $"<form class=\"inline\" method=\"post\" action=\"{E(Request.Path + "?" + string.Join("&", q.Append("handler=" + handler)) + TenantQuery)}\">{Token()}{body}</form>";
    }

    /// <summary>"&amp;tenant=Name" with several tenants (2.4.12): a form posts to the tenant its page showed (see <see cref="TenantAccessor.SelectAsync"/>).</summary>
    protected string TenantQuery => HttpContext.RequestServices.GetService<TenantAccessor>()?.FormQuery is { } t ? "&" + t : "";

    protected static string Input(string name, string label, string? value, int size = 24, string type = "text") =>
        $"<label>{E(label)}<input type=\"{type}\" name=\"{name}\" value=\"{E(value)}\" size=\"{size}\"/></label>";

    protected static string Select(string name, string label, IEnumerable<string> options, string? selected) =>
        $"<label>{E(label)}<select name=\"{name}\">" +
        string.Concat(options.Select(o => $"<option{(o == selected ? " selected" : "")}>{E(o)}</option>")) + "</select></label>";

    /// <summary>A choice whose stored value is not what the reader should see: "ActiveShare" is offered as "Share of people active".</summary>
    protected static string Select(string name, string label, IEnumerable<(string Value, string Label)> options, string? selected) =>
        $"<label>{E(label)}<select name=\"{name}\">" +
        string.Concat(options.Select(o => $"<option value=\"{E(o.Value)}\"{(o.Value == selected ? " selected" : "")}>{E(o.Label)}</option>")) + "</select></label>";

    protected static string Button(string text, string? confirm = null, bool quiet = false) =>
        $"<button class=\"btn{(quiet ? " quiet" : "")}\" type=\"submit\"{(confirm is null ? "" : $" data-confirm=\"{E(confirm)}\"")}>{E(text)}</button>";

    /// <summary>A small x box that removes the item beside it (2.4.12), named for screen readers and asked to confirm.</summary>
    protected static string RemoveBox(string what) =>
        $"<button class=\"btn quiet x\" type=\"submit\" aria-label=\"{E(what)}\" title=\"{E(what)}\" data-confirm=\"{E(what)}?\">&#215;</button>";

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

    /// <summary>"1 unit", "3 units": a count with its noun (2.4.10; the pages wrote "3 unit(s)").</summary>
    protected static string Count(int n, string noun, string? plural = null) => $"{n:#,0} {(n == 1 ? noun : plural ?? noun + "s")}";

    /// <summary>"is" for one, "are" for more: "1 unit is combined", "3 units are combined".</summary>
    protected static string Are(int n) => n == 1 ? "is" : "are";

    /// <summary>Where the figures are from, in a sentence: "the organization", or "this part of the organization".</summary>
    protected string Here => Ctx.Root is null && Viewer.OrgWide ? "the organization" : "this part of the organization";

    protected static string Pct(double? v) => Chart.Format(v, Fmt.Percent);
    protected static string Num(double? v) => Chart.Format(v, Fmt.Count);
    protected static double? Ratio(double a, double b) => b > 0 ? a / b : null;
    /// <summary>The trend's points (days or weeks), oldest first, and their labels.</summary>
    protected IReadOnlyList<Bucket> Buckets() => Ctx.Buckets();
    protected IReadOnlyList<string> Labels()
    {
        var buckets = Ctx.Buckets();
        var axis = new Axis(buckets.Select(b => b.Label)) { Note = HeldBack() };
        /* A week the period covers only in part is not drawn (2.5.2, see Axis.Partial), nor a day held back (2.5.3). It
           stays in the trend's rows, which several pages add up for the period's totals. */
        for (var i = 0; i < buckets.Count; i++)
            if (buckets[i].Partial) axis.Partial.Add(i);
        foreach (var m in _markers)
        {
            var key = Ctx.Daily ? m.Day.Date : ReportContext.WeekEnding(m.Day);
            var i = buckets.ToList().FindIndex(b => b.Key == key);
            if (i >= 0) axis.Markers.Add(new(i, m.Label, m.Kind, m.Day));
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
    protected string TrendNote => Ctx.Daily ? "Each point is a day." + (HeldBack() is null ? "" : " The two newest days of records are not drawn: Purview can deliver a record up to 48 hours late, so they may not all be in yet. The page's other figures count them.")
        : "Each point is a whole Saturday-to-Friday week, labeled by its Friday. A week the period covers only in part, at its start or not yet over, is not drawn; the page's other figures count its days.";

    /// <summary>
    /// Said in a line under a daily trend (2.5.3): the day it is shown to, and why it stops short of the period. Each
    /// nightly load reads the last 48 hours again, because Purview can deliver a record that late; until it has, the two
    /// newest days of records may not all be in, so a daily trend leaves them out (see <see cref="ReportContext.DrawnTo"/>).
    /// Null on a weekly trend.
    /// </summary>
    protected string? HeldBack(string shown = "Shown") => Ctx.Daily && Ctx.DrawnTo < Ctx.End
        ? $"{shown} to {Ctx.DrawnTo:d MMM}. Records from the last 48 hours may not all be in yet, so the newest days are left out."
        : null;

    /// <summary>A table with a trend in each row, and that line under it: a sparkline has no room to say it.</summary>
    protected HtmlString WithTrendNote(HtmlString table) => new(table.Value + Chart.Held(HeldBack("Trends shown")));

    /// <summary>The CSV export of this page's list, with its current search and sort.</summary>
    protected string CsvUrl()
    {
        var q = Request.Query.Where(kv => kv.Key is not ("p" or "handler")).Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value.ToString())}");
        return Request.Path + "?" + string.Join("&", q.Append("handler=csv"));
    }

    /// <summary>Folds rows for groups smaller than the minimum into one "Smaller groups" row that itself reaches it (see <see cref="CopilotInsights.Data.SmallGroups"/>).</summary>
    protected List<T> FoldSmall<T>(IEnumerable<T> rows, Func<T, int> people, Func<IReadOnlyList<T>, T> combine, out int folded) =>
        CopilotInsights.Data.SmallGroups.Fold(rows, people, combine, Ctx.MinGroupSize, out folded);

    /// <summary>
    /// A count of people that is shown only at the minimum group size or more (2.4.12): the people who used one agent,
    /// for one, are a group, and an agent used by one person is that person's use. Null (a dash) below it.
    /// </summary>
    protected int? AtLeastMin(int people) => people >= Ctx.MinGroupSize ? people : null;

    /// <summary>
    /// The department chosen (?dept=), one of those offered, or null for every department (2.5.8), and a box to choose it
    /// that keeps the period and the part of the organization.
    /// </summary>
    protected (string? Chosen, HtmlString Picker) DepartmentFilter(IEnumerable<string> departments)
    {
        var list = departments.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(d => d, StringComparer.OrdinalIgnoreCase).ToList();
        var asked = Request.Query["dept"].FirstOrDefault();
        var chosen = list.FirstOrDefault(d => string.Equals(d, asked, StringComparison.OrdinalIgnoreCase));
        var options = string.Concat(list.Select(d =>
            $"<option value=\"{E(Ctx.Url(Request.Path, extra: "dept=" + Uri.EscapeDataString(d)))}\"{(d == chosen ? " selected" : "")}>{E(d)}</option>"));
        return (chosen, new HtmlString(
            $"<p class=\"sub\"><label>Department <select class=\"pick\" aria-label=\"Department\" data-navigate>" +
            $"<option value=\"{E(Ctx.Url(Request.Path))}\"{(chosen is null ? " selected" : "")}>All departments</option>{options}</select></label></p>"));
    }

    /// <summary>
    /// People's display names for their sign-ins (2.5.8): every Owner column shows a name. From the directory loaded from
    /// AD (audit.UserDirectory, everyone, Copilot users or not), else the report tables' (bi.DimUser). A sign-in neither
    /// knows (someone outside the directory, or a DOMAIN\name) is shown as it is.
    /// </summary>
    protected async Task<Func<string?, string?>> NamesAsync(IEnumerable<string?> signIns)
    {
        var keys = signIns.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var chunk in keys.Chunk(1000))   // under SQL Server's 2,100 parameters
            foreach (var (upn, name) in await Db.QueryAsync<(string Upn, string Name)>(
                "SELECT UserPrincipalName, DisplayName FROM bi.DimUser WHERE UserPrincipalName IN @upns AND NULLIF(LTRIM(RTRIM(DisplayName)), N'') IS NOT NULL;", new { upns = chunk }))
                names[upn] = name;
        /* The directory has the owners who never used Copilot, whom the report tables do not. */
        var missing = keys.Where(k => !names.ContainsKey(k)).ToList();
        if (missing.Count > 0 && await Db.ScalarAsync<int>("SELECT CASE WHEN OBJECT_ID(N'audit.UserDirectory', N'U') IS NULL THEN 0 ELSE 1 END;") == 1)
            foreach (var chunk in missing.Chunk(1000))
                foreach (var (upn, name) in await Db.QueryAsync<(string Upn, string Name)>(
                    "SELECT UserPrincipalName, FullName FROM audit.UserDirectory WHERE UserPrincipalName IN @upns AND NULLIF(LTRIM(RTRIM(FullName)), N'') IS NOT NULL;", new { upns = chunk }))
                    names[upn] = name;
        return s => s is null ? null : names.TryGetValue(s.Trim(), out var n) ? n : s;
    }

    /// <summary>
    /// The same for a point on a trend (2.4.12): nobody is shown as 0, a count under the minimum as a gap. A day when two
    /// people used an agent is those two people's use, as much as the period's total is.
    /// </summary>
    protected double? PointAtMin(int people) => people == 0 ? 0 : AtLeastMin(people);

    /// <summary>
    /// A group's adoption, shown only where its license holders reach the minimum group size (2.4.12), as the charts and
    /// the digests rank it: a team of twelve with one license holder is that one person's use. Blank below it.
    /// </summary>
    protected string AdoptionAtMin(int activeLicensed, int licensed) =>
        Pct(licensed >= Ctx.MinGroupSize ? Ratio(activeLicensed, licensed) : null);

    /// <summary>The same, in words: "12 people", or "fewer than 5 people".</summary>
    protected string AgentPeople(int people) => people >= Ctx.MinGroupSize ? $"{people:#,0} people" : $"fewer than {Ctx.MinGroupSize} people";

    /// <summary>What a breakdown says under it when groups were folded: how many, and why (2.4.12).</summary>
    protected string FoldNote(int folded, string noun, string? plural = null) =>
        folded == 0 ? "" : $"{Count(folded, "smaller " + noun, "smaller " + (plural ?? noun + "s"))} {Are(folded)} combined, so no row describes fewer than {Ctx.MinGroupSize} people.";
}
