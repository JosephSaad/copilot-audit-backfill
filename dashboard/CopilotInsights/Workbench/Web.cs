using Governance.Application;
using Governance.Application.Data;
using Governance.Application.Queries;
using Governance.Domain;
using Governance.Web.Infrastructure;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Dapper;
using Governance.Application;
using Governance.Application.Commands;
using Governance.Application.Data;
using Governance.Application.Outbox;
using Governance.Application.Queries;
using Microsoft.AspNetCore.Html;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Razor.TagHelpers;

namespace Governance.Web.Infrastructure;

/// <summary>An error the user sees as a short reference; the details go to the log only.</summary>
public static class Incident
{
    public static string Note(ILogger log, Exception ex, string what)
    {
        var reference = Convert.ToHexString(RandomNumberGenerator.GetBytes(3));
        log.LogError(ex, "Incident {Reference} while {What}", reference, what);
        return $"Something went wrong while {what}. Nothing was changed if the action did not finish. Quote reference {reference} to the Workbench team.";
    }
}

/// <summary>
/// Help text behind the (i) icon. Defaults ship in the pages; the Platform Owner can override any key through an
/// active ref.TextBlock row (Admin › Text). Cached for five minutes.
/// </summary>
public sealed class HelpText(IServiceScopeFactory scopes, ILogger<HelpText> log)
{
    private Dictionary<string, string> _overrides = new();
    private DateTime _loaded = DateTime.MinValue;

    int _reloading;

    /// <summary>The text for a key now: a reload when due runs in the background, so a page never waits for the database.</summary>
    public string Resolve(string? key, string fallback)
    {
        if (DateTime.UtcNow - _loaded > TimeSpan.FromMinutes(5) && Interlocked.Exchange(ref _reloading, 1) == 0)
            _ = Task.Run(async () => { try { await ReloadAsync(); } finally { Interlocked.Exchange(ref _reloading, 0); } });
        return key is not null && _overrides.TryGetValue(key, out var v) ? v : fallback;
    }

    public void Reload() => ReloadAsync().GetAwaiter().GetResult();

    /// <summary>Loads the approved overrides; a failure keeps the defaults until the next try, five minutes on.</summary>
    public async Task ReloadAsync()
    {
        _loaded = DateTime.UtcNow;
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<Db>();
            await using var c = await db.OpenAsync(0, true, CancellationToken.None);
            _overrides = (await c.QueryAsync<(string Key, string Body)>("SELECT TextKey, Body FROM ref.TextBlock WHERE Status = 'ACTIVE'")).ToDictionary(x => x.Key, x => x.Body);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Help text overrides could not be loaded; defaults are used.");
        }
    }
}

/// <summary>&lt;help k="key"&gt;Default text&lt;/help&gt; renders an (i) button with a tooltip on hover, focus and tap.</summary>
[HtmlTargetElement("help")]
public sealed class HelpTagHelper(HelpText texts) : TagHelper
{
    private static int _counter;
    public string? K { get; set; }
    public string? Label { get; set; }

    public override async Task ProcessAsync(TagHelperContext context, TagHelperOutput output)
    {
        var inner = (await output.GetChildContentAsync()).GetContent().Trim();
        var text = texts.Resolve(K, System.Net.WebUtility.HtmlDecode(inner));
        var id = "h" + Interlocked.Increment(ref _counter).ToString(CultureInfo.InvariantCulture);
        output.TagName = "span";
        output.Attributes.SetAttribute("class", "help");
        output.Content.SetHtmlContent(
            $"<button type=\"button\" class=\"help-btn\" aria-label=\"{Enc(Label ?? "More information")}\" aria-describedby=\"{id}\" aria-expanded=\"false\">i</button>" +
            $"<span role=\"tooltip\" id=\"{id}\" class=\"help-tip\">{Enc(text)}</span>");
    }

    private static string Enc(string s) => System.Net.WebUtility.HtmlEncode(s);
}

/// <summary>&lt;idem /&gt; adds the idempotency key every form post carries (wf.CommandLog).</summary>
[HtmlTargetElement("idem", TagStructure = TagStructure.WithoutEndTag)]
public sealed class IdemTagHelper : TagHelper
{
    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        output.TagName = "input";
        output.TagMode = TagMode.SelfClosing;
        output.Attributes.SetAttribute("type", "hidden");
        output.Attributes.SetAttribute("name", "__idem");
        output.Attributes.SetAttribute("value", Guid.NewGuid().ToString());
    }
}

/// <summary>Base page: the acting person, flash messages, the post-redirect-get pattern, safe errors and CSV export.</summary>
public abstract class GovPage : PageModel
{
    protected WebPrincipal Me => HttpContext.RequestServices.GetRequiredService<WebPrincipal>();
    public WebPrincipal Principal => Me;

    /// <summary>The Workbench is set to Copilot seats per line of business only (Workbench.Scope): links to closed pages are left out.</summary>
    public bool SeatsOnly => HttpContext.Items[WorkbenchScopeMiddleware.ItemKey] is true;

    [BindProperty(Name = "__idem")] public Guid Idem { get; set; }
    [TempData] public string? Flash { get; set; }
    [TempData] public string? FlashError { get; set; }

    [BindProperty(SupportsGet = true, Name = "q")] public string? Search { get; set; }
    [BindProperty(SupportsGet = true, Name = "sort")] public string? Sort { get; set; }
    [BindProperty(SupportsGet = true, Name = "desc")] public bool Desc { get; set; }
    /// <summary>
    /// The list page asked for (?page=), read from the query string: Razor Pages keeps the name "page" for the page's
    /// own route value, so model binding never saw the number and every list stayed on its first page.
    /// </summary>
    public int PageNo
    {
        get => pageNo ??= int.TryParse(PageContext?.HttpContext?.Request.Query["page"].ToString(), out var p) && p > 0 ? p : 1;
        set => pageNo = value;
    }
    private int? pageNo;

    public ListQuery Query => new(Search, Sort, Desc, Math.Max(1, PageNo));

    protected ILogger Log => HttpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(GetType());

    /// <summary>Runs a command, then redirects (post-redirect-get). Business refusals show as they are; anything else as an incident reference.</summary>
    protected async Task<IActionResult> Act(Func<Task> action, string success, Func<IActionResult>? next = null, string what = "saving your change")
    {
        try
        {
            await action();
            Flash = success;
            return next?.Invoke() ?? RedirectToPage();
        }
        catch (DomainException ex) { FlashError = ex.Message; }
        catch (ConcurrencyException ex) { FlashError = ex.Message; }
        catch (Exception ex) { FlashError = Incident.Note(Log, ex, what); }
        return RedirectToPage();
    }

    protected async Task<IActionResult> Act<T>(Func<Task<T>> action, Func<T, string> success, Func<T, IActionResult>? next = null, string what = "saving your change")
    {
        try
        {
            var r = await action();
            Flash = success(r);
            return next?.Invoke(r) ?? RedirectToPage();
        }
        catch (DomainException ex) { FlashError = ex.Message; }
        catch (ConcurrencyException ex) { FlashError = ex.Message; }
        catch (Exception ex) { FlashError = Incident.Note(Log, ex, what); }
        return RedirectToPage();
    }

    /// <summary>
    /// An error shown on the page rendered by this same request (no redirect). The [TempData] property is written only
    /// after the handler finishes, so the layout would not see it; the dictionary entry is visible at once.
    /// </summary>
    protected void ShowErrorNow(string message)
    {
        FlashError = null;
        TempData["FlashError"] = message;
    }

    protected IActionResult Csv<T>(IEnumerable<T> rows, string name, params string[] columns)
        => new FileContentResult(CsvWriter.Write(rows, columns), "text/csv; charset=utf-8") { FileDownloadName = $"{name}-{DateTime.UtcNow:yyyyMMdd-HHmm}.csv" };

    protected async Task AuditViewAsync(string action, string entityType, object? id)
    {
        var db = HttpContext.RequestServices.GetRequiredService<Db>();
        var audit = HttpContext.RequestServices.GetRequiredService<AuditWriter>();
        await db.InTransactionAsync(u => audit.WriteAsync(u, Me, action, entityType, id, null));
    }

    protected static UploadedFile? Upload(IFormFile? f) => f is null || f.Length == 0 ? null : new UploadedFile(f.FileName, f.ContentType, f.Length, f.OpenReadStream);

    public string Local(DateTime? utc, string format = "d MMM yyyy HH:mm") => Ui.Local(utc, format);
}

public static class CsvWriter
{
    public static byte[] Write<T>(IEnumerable<T> rows, params string[] columns)
    {
        var props = typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.Name != "TotalRows" && p.Name != "RowVer" && (columns.Length == 0 || columns.Contains(p.Name)))
            .Where(p => p.PropertyType == typeof(string) || p.PropertyType.IsValueType || Nullable.GetUnderlyingType(p.PropertyType) is not null)
            .ToList();
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", props.Select(p => Esc(p.Name))));
        foreach (var r in rows)
            sb.AppendLine(string.Join(",", props.Select(p => Esc(Format(p.GetValue(r))))));
        return Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
    }

    private static string Format(object? v) => v switch
    {
        null => "",
        DateTime d => d.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        decimal m => m.ToString(CultureInfo.InvariantCulture),
        bool b => b ? "Yes" : "No",
        _ => Convert.ToString(v, CultureInfo.InvariantCulture) ?? "",
    };

    private static string Esc(string s)
    {
        // Neutralize spreadsheet formulas (CSV injection) and quote when needed.
        if (s.Length > 0 && "=+-@\t\r".Contains(s[0])) s = "'" + s;
        return s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }
}

/// <summary>Small display helpers shared by the pages.</summary>
public static class Ui
{
    private static readonly TimeZoneInfo Zone = FindZone();

    private static TimeZoneInfo FindZone()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time"); } catch { return TimeZoneInfo.Utc; }
    }

    public static string Local(DateTime? utc, string format = "d MMM yyyy HH:mm")
        => utc is null ? "" : TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc.Value, DateTimeKind.Utc), Zone).ToString(format, CultureInfo.GetCultureInfo("en-US"));

    public static string Date(DateTime? d) => d?.ToString("d MMM yyyy", CultureInfo.GetCultureInfo("en-US")) ?? "";

    public static string Ago(DateTime? utc)
    {
        if (utc is null) return "never";
        var span = DateTime.UtcNow - utc.Value;
        if (span.TotalMinutes < 1) return "just now";
        if (span.TotalHours < 1) return $"{(int)span.TotalMinutes} min ago";
        if (span.TotalDays < 1) return $"{(int)span.TotalHours} h ago";
        return $"{(int)span.TotalDays} days ago";
    }

    public static string Num(decimal? d) => d?.ToString("N0", CultureInfo.GetCultureInfo("en-US")) ?? "";

    /// <summary>Plain words for a code (see <see cref="Labels"/>): people never see raw codes.</summary>
    public static string Words(string? code) => Labels.For(code);

    /// <summary>A status pill: color is never the only signal (WCAG): the text says the status.</summary>
    public static IHtmlContent Pill(string? text, string? tone = null)
    {
        var t = tone ?? Tone(text);
        return new HtmlString($"<span class=\"pill pill-{t}\">{System.Net.WebUtility.HtmlEncode(Words(text))}</span>");
    }

    public static string Tone(string? status) => status switch
    {
        "ACTIVE" or "APPROVED" or "COMPLETED" or "DONE" or "PASS" or "RESOLVED" or "SUCCEEDED" or "PRODUCTION" or "CLOSED" or "EXTEND" or "ACCEPTED" or "GA" => "ok",
        "DECLINED" or "FAIL" or "FAILED" or "CRITICAL" or "EXPIRED" or "RETIRED" or "CANCELLED" or "NOT_APPROVED" or "SUSPENDED" => "bad",
        "OPEN" or "HIGH" or "ON_HOLD" or "RETURNED" or "PENDING" or "DRAFT" or "PREVIEW" or "PILOT" or "APPROVED_WITH_CONDITIONS" or "CONSTRAINED_PILOT" or "PARTIAL" or "ACKNOWLEDGED" => "warn",
        _ => "info",
    };

    /// <summary>"a" or "an" for the word that follows (spoken sound approximated by the first letter; "LOB" reads as a word).</summary>
    public static string Article(string? word)
        => !string.IsNullOrEmpty(word) && "aeiouAEIOU".Contains(word[0]) ? "an" : "a";

    /// <summary>Plain status sentence for a task: "Waiting for a delegated approver for LOB Retail since 14 Oct, due 16 Oct".</summary>
    public static string TaskSentence(string? roleName, string? fundingOwner, DateTime started, DateTime? due, string? assignedName, bool orDelegate = false)
    {
        var role = (roleName ?? "role holder").ToLowerInvariant().Replace("lob ", "LOB ");
        var who = assignedName is not null ? assignedName
                  : role.StartsWith("the ", StringComparison.Ordinal) ? role
                  : $"{Article(role)} {role}" + (orDelegate ? " or a delegated approver" : "");
        var forWhom = string.IsNullOrEmpty(fundingOwner) ? "" : $" for {fundingOwner}";
        var dueText = due is null ? "" : $", due {Local(due, "d MMM")}";
        var late = due is not null && due < DateTime.UtcNow ? " (overdue)" : "";
        return $"Waiting for {who}{forWhom} since {Local(started, "d MMM")}{dueText}{late}";
    }

    public static string SortHref(HttpRequest req, string key)
    {
        var q = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(req.QueryString.Value);
        var current = q.TryGetValue("sort", out var s) ? s.ToString() : null;
        var desc = q.TryGetValue("desc", out var d) && d == "true";
        var dict = q.ToDictionary(k => k.Key, v => (string?)v.Value.ToString());
        dict["sort"] = key;
        dict["desc"] = current == key && !desc ? "true" : "false";
        dict["page"] = "1";
        return Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString(req.Path, dict!);
    }

    public static string PageHref(HttpRequest req, int page, string? handler = null)
    {
        var q = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(req.QueryString.Value);
        var dict = q.ToDictionary(k => k.Key, v => (string?)v.Value.ToString());
        dict["page"] = page.ToString(CultureInfo.InvariantCulture);
        if (handler is not null) dict["handler"] = handler; else dict.Remove("handler");
        return Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString(req.Path, dict!);
    }

    public static string CsvHref(HttpRequest req) => PageHref(req, 1, "Csv");

    public static string SortMark(HttpRequest req, string key)
    {
        var q = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(req.QueryString.Value);
        if (!q.TryGetValue("sort", out var s) || s != key) return "";
        return q.TryGetValue("desc", out var d) && d == "true" ? " ▼" : " ▲";
    }
}
