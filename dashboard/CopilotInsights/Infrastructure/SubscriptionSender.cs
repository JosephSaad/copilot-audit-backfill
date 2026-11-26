using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using CopilotInsights.Charts;
using CopilotInsights.Data;
using CopilotInsights.Pages;
using Dapper;
using Microsoft.AspNetCore.Html;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.RazorPages.Infrastructure;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;

namespace CopilotInsights.Infrastructure;

/// <summary>
/// Sends the report subscriptions people set up for themselves (dash.ReportSubscription; My subscriptions): every two
/// minutes, those whose time has come. Each is built as its report page is built for the person, under their access as
/// it is at that moment (<see cref="ViewerService.WithoutSignInAsync"/>, <see cref="InsightsPage.BuildForEmailAsync"/>):
/// a report they can no longer open is not sent, and the subscription pauses saying why. The email carries the page's
/// headline figures and its charts as images (<see cref="ChartImage"/>), never its lists, and a link to the page. A send
/// is claimed first (a row of dash.ReportSubscriptionSend), so with several web servers it goes once. It waits for the
/// night's refresh until SubscriptionWaitUntil, then goes with a note; one more than 12 hours late is not sent.
/// </summary>
public sealed partial class SubscriptionSender(IServiceScopeFactory scopes, IActionDescriptorCollectionProvider actions, TenantAccessor tenantAccessor, IOptions<InsightsOptions> options,
    ILogger<SubscriptionSender> log) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(2);

    /// <summary>The most sent in one round for one tenant: the rest wait the two minutes.</summary>
    public const int PerRound = 25;

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        if (!options.Value.Notifications) return;
        try { await Task.Delay(TimeSpan.FromSeconds(Math.Max(0, options.Value.NotifierStartDelaySeconds) + 30), stop); } catch (OperationCanceledException) { return; }
        while (!stop.IsCancellationRequested)
        {
            try { await RunAllAsync(stop); }
            catch (Exception ex) when (ex is not OperationCanceledException || !stop.IsCancellationRequested) { log.LogError(ex, "The report subscriptions round failed"); }
            try { await Task.Delay(Interval, stop); } catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>One round in every tenant's database.</summary>
    public async Task RunAllAsync(CancellationToken ct = default)
    {
        using var root = scopes.CreateScope();
        var tenants = root.ServiceProvider.GetRequiredService<TenantAccessor>();
        var before = tenants.Current;
        try
        {
            foreach (var t in tenants.All)
            {
                tenants.Use(t);
                try { await RunOnceAsync(ct); }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested) { log.LogError(ex, "The report subscriptions round failed for {Tenant}", t.Name); }
            }
        }
        finally { tenants.Use(before); }
    }

    sealed class Sub
    {
        public int SubscriptionId { get; set; }
        public string Upn { get; set; } = "";
        public string SignInName { get; set; } = "";
        public string SignedInWith { get; set; } = SignIn.Windows;
        public string Path { get; set; } = "";
        public string Title { get; set; } = "";
        public string? Query { get; set; }
        public int Days { get; set; }
        public string Frequency { get; set; } = "";
        public byte? DayOfWeek { get; set; }
        public byte? DayOfMonth { get; set; }
        public TimeSpan SendTime { get; set; }
        public DateTime? NextDueUtc { get; set; }
    }

    const string Columns = "SubscriptionId, Upn, SignInName, SignedInWith, Path, Title, Query, Days, Frequency, DayOfWeek, DayOfMonth, SendTime, NextDueUtc";

    /// <summary>The subscriptions due in the current tenant: claimed, built and sent, one by one.</summary>
    async Task RunOnceAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<Db>();
        var settings = sp.GetRequiredService<Settings>();
        if (await db.ScalarAsync<int>("SELECT CASE WHEN OBJECT_ID(N'dash.ReportSubscription', N'U') IS NULL THEN 0 ELSE 1 END;") == 0) return;
        if (await settings.StringAsync("ReportSubscriptions") == "0") return;
        var zone = await settings.ZoneAsync();
        var due = await db.QueryAsync<Sub>($@"
SELECT TOP (@take) {Columns} FROM dash.ReportSubscription
WHERE Deleted = 0 AND Paused = 0 AND NextDueUtc <= SYSUTCDATETIME() ORDER BY NextDueUtc;", new { take = PerRound });
        if (due.Count == 0) return;
        var refreshed = await db.ScalarAsync<DateTime?>("SELECT MAX(EndedUtc) FROM dash.RefreshRun;");
        var waitUntil = Subscriptions.ParseTime(await settings.StringAsync("SubscriptionWaitUntil"), new TimeSpan(12, 0, 0));
        var now = DateTime.UtcNow;
        var (go, late) = Subscriptions.Ready(refreshed, now, waitUntil, zone);
        foreach (var s in due)
        {
            ct.ThrowIfCancellationRequested();
            var dueUtc = s.NextDueUtc!.Value;
            var missed = Subscriptions.Missed(dueUtc, now);
            if (!missed && !go) continue;   // waiting for the night's refresh
            if (!await ClaimAsync(db, s.SubscriptionId, dueUtc)) continue;
            var next = Subscriptions.NextDue(s.Frequency, s.DayOfWeek, s.DayOfMonth, s.SendTime, now, zone);
            await db.ExecuteAsync("UPDATE dash.ReportSubscription SET NextDueUtc = @next WHERE SubscriptionId = @id AND NextDueUtc = @dueUtc;", new { next, id = s.SubscriptionId, dueUtc });
            if (missed)
            {
                await EndAsync(db, s.SubscriptionId, dueUtc, new("Missed", $"Not sent: the site was not running at {Subscriptions.TimeText(s.SendTime)}, and a send more than 12 hours late waits for the next one.", 0, 0), recordLast: true);
                continue;
            }
            var result = await SendOneAsync(s, late ? refreshed : null, waitUntil, ct);
            await EndAsync(db, s.SubscriptionId, dueUtc, result, recordLast: true);
        }
    }

    /// <summary>
    /// One subscription, now, for its owner (My subscriptions' "Send me one now"): built and sent as a scheduled one is,
    /// recorded as a send of its own; its schedule does not move. Returns what happened, in words.
    /// </summary>
    public async Task<string> SendNowAsync(int id, CancellationToken ct = default)
    {
        /* Apart from the request that asked: the build sets a request of its own for the person (IHttpContextAccessor),
           which must not replace the page's own. Only the tenant is carried over. */
        var tenant = tenantAccessor.Current;
        Task<string> work;
        using (ExecutionContext.SuppressFlow())
            work = Task.Run(() => { tenantAccessor.Use(tenant); return SendNowCoreAsync(id, ct); }, CancellationToken.None);
        return await work;
    }

    async Task<string> SendNowCoreAsync(int id, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Db>();
        var s = (await db.QueryAsync<Sub>($"SELECT {Columns} FROM dash.ReportSubscription WHERE SubscriptionId = @id AND Deleted = 0;", new { id })).FirstOrDefault()
                ?? throw new ArgumentException("that subscription is gone.");
        var at = DateTime.UtcNow;
        at = new DateTime(at.Ticks - at.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);
        if (!await ClaimAsync(db, id, at)) throw new ArgumentException("it is being sent already; try again in a minute.");
        var result = await SendOneAsync(s, null, TimeSpan.Zero, ct);
        await EndAsync(db, id, at, result, recordLast: false);
        return result.Detail;
    }

    /// <summary>What a send came to: Sent, Failed, Paused (the person can no longer have it), Skipped or Missed.</summary>
    public sealed record Outcome(string Status, string Detail, int Figures, int Charts);

    static async Task<bool> ClaimAsync(Db db, int id, DateTime dueUtc)
    {
        try
        {
            await db.ExecuteAsync("INSERT INTO dash.ReportSubscriptionSend (SubscriptionId, DueUtc, Server) VALUES (@id, @dueUtc, @server);",
                new { id, dueUtc, server = Environment.MachineName });
            return true;
        }
        catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number is 2627 or 2601) { return false; }   // another web server took it
    }

    static Task EndAsync(Db db, int id, DateTime dueUtc, Outcome o, bool recordLast) => db.ExecuteAsync(@"
UPDATE dash.ReportSubscriptionSend SET Status = @status, Detail = LEFT(@detail, 1000), Figures = @figures, Charts = @charts, EndedUtc = SYSUTCDATETIME()
WHERE SubscriptionId = @id AND DueUtc = @dueUtc;
UPDATE dash.ReportSubscription SET
    LastSentUtc = CASE WHEN @status = 'Sent' THEN SYSUTCDATETIME() ELSE LastSentUtc END,
    LastStatus = LEFT(@detail, 400),
    Paused = CASE WHEN @status = 'Paused' THEN 1 ELSE Paused END,
    PausedReason = CASE WHEN @status = 'Paused' THEN LEFT(@detail, 400) ELSE PausedReason END
WHERE SubscriptionId = @id AND (@recordLast = 1 OR @status IN ('Sent', 'Paused'));",
        new { id, dueUtc, status = o.Status, detail = o.Detail, figures = o.Figures, charts = o.Charts, recordLast });

    /// <summary>Builds the report for the person, as they may see it now, and emails it to them.</summary>
    async Task<Outcome> SendOneAsync(Sub s, DateTime? lateDataUtc, TimeSpan waitUntil, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<Db>();
        var settings = sp.GetRequiredService<Settings>();
        try
        {
            if (Subscriptions.Report(s.Path) is not { } report)
                return new("Paused", "Paused: this report can no longer be sent by email.", 0, 0);
            /* One made before a report had to have its choice made first (two managers, an agent, an app) would be empty. */
            if (Subscriptions.Missing(s.Path, s.Query) is { } missing)
                return new("Paused", "Paused: " + char.ToUpperInvariant(missing[0]) + missing[1..] + " Stop this one, then subscribe again from the report once it is chosen.", 0, 0);
            var person = (await db.QueryAsync<(string? DisplayName, bool? Enabled)>("SELECT DisplayName, AccountEnabled FROM dash.Person WHERE Upn = @upn;", new { upn = s.Upn })).FirstOrDefault();
            if (person == default || person.Enabled == false)
                return new("Paused", "Paused: the account is disabled or no longer in the people directory.", 0, 0);
            var page = await PageAsync(sp, s.Path) ?? throw new InvalidOperationException($"No page answers {s.Path}.");

            /* A request made for the person: their filters and period, their access as it is now. */
            var http = new DefaultHttpContext { RequestServices = sp };
            http.Request.Scheme = "https";
            http.Request.Host = new HostString("localhost");
            http.Request.Path = s.Path;
            http.Request.QueryString = new QueryString("?" + Subscriptions.Address("", s.Days, s.Query).TrimStart('?'));
            sp.GetRequiredService<IHttpContextAccessor>().HttpContext = http;
            var viewers = sp.GetRequiredService<ViewerService>();
            var viewer = await viewers.WithoutSignInAsync(s.Upn, s.SignInName, s.SignedInWith, ct);
            viewers.Use(viewer);
            var model = (InsightsPage)ActivatorUtilities.CreateInstance(sp, page.ModelTypeInfo!.AsType());
            model.PageContext = new PageContext(new ActionContext(http, new RouteData(), page));
            if (await model.BuildForEmailAsync() is { } denied)
                return new("Paused", "Paused: " + Plain(denied) + " Resume it on My subscriptions once you can open the report again; your access is checked at each send.", 0, 0);

            var url = (await settings.StringAsync("DashboardUrl"))?.TrimEnd('/');
            var mail = ReportEmail.Compose(model, report.Title, s.Days, s.Frequency, s.DayOfWeek, s.DayOfMonth, s.SendTime,
                url is null ? null : url + Subscriptions.Address(s.Path, s.Days, s.Query), url is null ? null : url + "/subscriptions",
                lateDataUtc is null ? null : $"The night's refresh had not ended by {Subscriptions.TimeText(waitUntil)}, so these figures are from the data as it was, to {model.Ctx.End:d MMM yyyy}.");
            var address = await AddressAsync(db, s.Upn);
            if (address is null) return new("Failed", "Not sent: the directory has no email address for you.", mail.Figures, mail.Charts.Count);
            var (status, error) = await sp.GetRequiredService<Mailer>().DeliverAsync("Subscription", MailConfig.Recipients(address), mail.Subject, mail.Html, mail.Text, mail.Charts, ct);
            var what = $"{mail.Figures:#,0} figure{(mail.Figures == 1 ? "" : "s")} and {mail.Charts.Count:#,0} chart{(mail.Charts.Count == 1 ? "" : "s")}" +
                       (mail.ChartsNotDrawn > 0 ? $" ({mail.ChartsNotDrawn:#,0} chart{(mail.ChartsNotDrawn == 1 ? "" : "s")} could not be drawn as an image on this server)" : "");
            return status == "Sent"
                ? new("Sent", $"Sent to {address}: {what}.", mail.Figures, mail.Charts.Count)
                : new("Failed", $"Not sent ({error}). The next one goes at its time.", mail.Figures, mail.Charts.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return new("Failed", Incident.Note(log, ex, "Not sent: it failed") + " The next one goes at its time.", 0, 0);
        }
    }

    static string Plain(string s) => s.TrimEnd() is var t && t.EndsWith('.') ? t : s.TrimEnd() + ".";

    /// <summary>The person's email address: the directory's mail, else the UPN where it is an address.</summary>
    static async Task<string?> AddressAsync(Db db, string upn)
    {
        string? mail = null;
        try { mail = await db.ScalarAsync<string?>("IF OBJECT_ID(N'audit.UserDirectory', N'U') IS NOT NULL SELECT TOP (1) NULLIF(Mail, N'') FROM audit.UserDirectory WHERE UserPrincipalName = @upn;", new { upn }); }
        catch (Microsoft.Data.SqlClient.SqlException) { }
        var a = mail ?? upn;
        return MailConfig.Recipients(a).Count == 1 ? a : null;
    }

    readonly Dictionary<string, CompiledPageActionDescriptor> _pages = new(StringComparer.OrdinalIgnoreCase);
    readonly SemaphoreSlim _pagesGate = new(1, 1);

    /// <summary>The compiled report page at an address, as the warm-up finds them.</summary>
    async Task<CompiledPageActionDescriptor?> PageAsync(IServiceProvider sp, string path)
    {
        await _pagesGate.WaitAsync();
        try
        {
            if (_pages.Count == 0)
            {
                var loader = sp.GetRequiredService<PageLoader>();
                foreach (var d in actions.ActionDescriptors.Items.OfType<PageActionDescriptor>())
                {
                    var compiled = await loader.LoadAsync(d, EndpointMetadataCollection.Empty);
                    var model = compiled.ModelTypeInfo?.AsType();
                    if (model is null || !typeof(InsightsPage).IsAssignableFrom(model) || model.IsAbstract || typeof(Pages.Admin.AdminPage).IsAssignableFrom(model)) continue;
                    var p = "/" + (d.AttributeRouteInfo?.Template ?? "").Trim('/');
                    if (!p.Contains('{')) _pages[p] = compiled;
                }
            }
            return _pages.GetValueOrDefault(path.Length > 1 ? path.TrimEnd('/') : path);
        }
        finally { _pagesGate.Release(); }
    }
}

/// <summary>
/// A report subscription's email: the page's headline figures, then each panel's charts as images with their legend,
/// key and notes, never a table or a list; a link to the page and to My subscriptions. Inline styles only: mail apps
/// drop style sheets.
/// </summary>
public static partial class ReportEmail
{
    public sealed record Message(string Subject, string Html, string Text, int Figures, IReadOnlyList<(string Id, byte[] Png)> Charts, int ChartsNotDrawn);

    const int Width = 680;
    const string Ink = "#17212a", Muted = "#55626d", Faint = "#7b8791", Rule = "#dde3e1", Accent = "#0a6b6b";

    static string E(string? s) => WebUtility.HtmlEncode(s ?? "");

    /// <summary>The HTML of a panel's body.</summary>
    public static string HtmlOf(IHtmlContent body)
    {
        using var w = new StringWriter();
        body.WriteTo(w, System.Text.Encodings.Web.HtmlEncoder.Default);
        return w.ToString();
    }

    /// <summary>A chart found in a panel: its SVG, and the legend, color scale, event key and notes around it, as text.</summary>
    public sealed record Found(string Svg, IReadOnlyList<(string Name, string Token)> Legend, string? Scale, IReadOnlyList<string> Notes);

    [GeneratedRegex("<svg class=\"chart[^\"]*\"[\\s\\S]*?</svg>")]
    private static partial Regex SvgChart();
    [GeneratedRegex("<span><i class=\"sw\" style=\"background:var\\(--([a-z0-9]+)\\)\"></i>([\\s\\S]*?)</span>")]
    private static partial Regex LegendItem();
    [GeneratedRegex("<div class=\"scale\"><span>([\\s\\S]*?)</span>[\\s\\S]*?<span>([\\s\\S]*?)</span></div>")]
    private static partial Regex ScaleRange();
    [GeneratedRegex("<p class=\"held\">([\\s\\S]*?)</p>")]
    private static partial Regex Held();
    [GeneratedRegex("<ol class=\"mkey\">([\\s\\S]*?)</ol>")]
    private static partial Regex Key();
    [GeneratedRegex("<li><span class=\"k\">([0-9]+)</span>([\\s\\S]*?)</li>")]
    private static partial Regex KeyItem();
    [GeneratedRegex("<[^>]+>")]
    private static partial Regex Tags();

    static string Text(string html) => WebUtility.HtmlDecode(Tags().Replace(html, "")).Trim();

    /// <summary>The charts in a panel's HTML, each with what is said around it; tables, lists and sparklines are left out.</summary>
    public static List<Found> Charts(string html)
    {
        var list = new List<Found>();
        var svgs = SvgChart().Matches(html);
        for (var i = 0; i < svgs.Count; i++)
        {
            var from = i == 0 ? 0 : svgs[i - 1].Index + svgs[i - 1].Length;
            var to = i + 1 < svgs.Count ? svgs[i + 1].Index : html.Length;
            var before = html[from..svgs[i].Index];
            var after = html[(svgs[i].Index + svgs[i].Length)..to];
            var legend = LegendItem().Matches(before).Select(m => (Text(m.Groups[2].Value), m.Groups[1].Value)).ToList();
            var scale = ScaleRange().Match(after) is { Success: true } sm ? $"Shading from {Text(sm.Groups[1].Value)} (lightest) to {Text(sm.Groups[2].Value)} (darkest)." : null;
            var notes = new List<string>();
            if (Key().Match(after) is { Success: true } km)
                notes.AddRange(KeyItem().Matches(km.Groups[1].Value).Select(m => $"{m.Groups[1].Value}: {Text(m.Groups[2].Value)}"));
            notes.AddRange(Held().Matches(after).Select(m => Text(m.Groups[1].Value)));
            list.Add(new(svgs[i].Value, legend, scale, notes));
        }
        return list;
    }

    /// <summary>The email for a report built for the person (<see cref="InsightsPage.BuildForEmailAsync"/>).</summary>
    public static Message Compose(InsightsPage page, string title, int days, string frequency, int? dayOfWeek, int? dayOfMonth, TimeSpan time,
        string? reportUrl, string? manageUrl, string? lateNote, Func<string, byte[]?>? draw = null)
    {
        draw ??= svg => ChartImage.Png(svg);
        var c = page.Ctx;
        var subject = $"{title}: {Subscriptions.Period(days)}";
        var h = new StringBuilder();
        var t = new StringBuilder();
        h.Append($"<!DOCTYPE html><html><head><meta charset=\"utf-8\"/><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\"/></head><body style=\"margin:0;padding:0;background:#f4f6f5\"><table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" style=\"background:#f4f6f5\"><tr><td align=\"center\" style=\"padding:18px 8px\">");
        h.Append($"<table role=\"presentation\" width=\"{Width}\" cellpadding=\"0\" cellspacing=\"0\" style=\"width:100%;max-width:{Width}px;background:#ffffff;border:1px solid {Rule};border-radius:8px;font-family:Segoe UI,Arial,sans-serif;color:{Ink}\"><tr><td style=\"padding:20px 22px\">");
        h.Append($"<div style=\"font-size:12px;color:{Faint};text-transform:uppercase;letter-spacing:.04em\">Copilot Insights · {E(page.Persona)}</div>");
        h.Append($"<h1 style=\"font-size:21px;margin:4px 0 2px;color:{Ink}\">{E(title)}</h1>");
        h.Append($"<div style=\"font-size:13px;color:{Muted}\">{E(c.ScopeLabel)} · {E(c.PeriodLabel)}</div>");
        t.AppendLine(title).AppendLine($"{c.ScopeLabel} · {c.PeriodLabel}").AppendLine();
        foreach (var note in new[] { lateNote, page.Stale, c.OrgNote }.OfType<string>())
        {
            h.Append($"<p style=\"font-size:13px;background:#fbf0de;color:{Ink};padding:8px 10px;border-radius:6px;margin:12px 0 0\">{E(note)}</p>");
            t.AppendLine(note).AppendLine();
        }

        /* The headline figures, three to a row. */
        var kpis = page.Kpis.ToList();
        if (kpis.Count > 0)
        {
            h.Append("<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" style=\"margin-top:14px;border-collapse:separate;border-spacing:0 8px\">");
            foreach (var row in kpis.Chunk(3))
            {
                h.Append("<tr>");
                for (var i = 0; i < 3; i++)
                {
                    var k = i < row.Length ? row[i] : null;
                    var pad = i < 2 ? "padding-right:8px;" : "";
                    if (k is null) { h.Append($"<td width=\"33%\" style=\"{pad}\"></td>"); continue; }
                    var tone = k.Tone switch { "crit" => "#d03b3b", "warn" => "#8f5500", "good" => "#0ca30c", _ => Ink };
                    h.Append($"<td width=\"33%\" valign=\"top\" style=\"{pad}\"><div style=\"border:1px solid {Rule};border-radius:8px;padding:10px 12px\">" +
                             $"<div style=\"font-size:12px;color:{Muted}\">{E(k.Label)}</div><div style=\"font-size:22px;font-weight:600;color:{tone};margin-top:2px\">{E(k.Value)}</div>" +
                             (k.Note is null ? "" : $"<div style=\"font-size:12px;color:{Muted};margin-top:2px\">{E(k.Note)}</div>") + "</div></td>");
                    t.AppendLine($"{k.Label}: {k.Value}" + (k.Note is null ? "" : $" ({k.Note})"));
                }
                h.Append("</tr>");
            }
            h.Append("</table>");
        }

        /* Each panel's charts, as images; its tables and lists are not sent. */
        var images = new List<(string Id, byte[] Png)>();
        var notDrawn = 0;
        var listsLeft = false;
        foreach (var p in page.Panels)
        {
            string html;
            var whole = p.Body is CopilotInsights.Charts.ChartSvg cs ? cs.Render(Width - 44) : null;
            html = whole ?? HtmlOf(p.Body);
            var found = Charts(html);
            /* A panel of sentences the page marks as naming no one: sent as text, without its links (they lead nowhere from a
               mailbox; the email's own link opens the report). */
            if (p.Email && found.Count == 0 && !html.Contains("<table", StringComparison.OrdinalIgnoreCase))
            {
                var words = System.Text.RegularExpressions.Regex.Replace(html, "\\s*<a\\b[^>]*>.*?</a>", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline);
                h.Append($"<h2 style=\"font-size:16px;margin:22px 0 4px;color:{Ink}\">{E(p.Title)}</h2>");
                h.Append($"<div style=\"font-size:14px;line-height:1.5;color:{Ink}\">{words}</div>");
                t.AppendLine().AppendLine(p.Title);
                foreach (System.Text.RegularExpressions.Match li in System.Text.RegularExpressions.Regex.Matches(words, "<li>(.*?)</li>", System.Text.RegularExpressions.RegexOptions.Singleline))
                    t.AppendLine("  - " + Text(li.Groups[1].Value));
                continue;
            }
            if (found.Count == 0) { listsLeft |= html.Contains("<table", StringComparison.OrdinalIgnoreCase) || html.Contains("<ul", StringComparison.OrdinalIgnoreCase); continue; }
            if (html.Contains("<table", StringComparison.OrdinalIgnoreCase)) listsLeft = true;
            h.Append($"<h2 style=\"font-size:16px;margin:22px 0 4px;color:{Ink}\">{E(p.Title)}</h2>");
            if (p.Sub is not null) h.Append($"<p style=\"font-size:12px;color:{Muted};margin:0 0 6px\">{E(p.Sub)}</p>");
            t.AppendLine().AppendLine(p.Title);
            foreach (var f in found)
            {
                if (f.Legend.Count > 0)
                    h.Append($"<div style=\"font-size:12px;color:{Muted};margin:4px 0\">" + string.Join("&nbsp;&nbsp;&nbsp;", f.Legend.Select(l =>
                        $"<span style=\"white-space:nowrap\"><span style=\"display:inline-block;width:10px;height:10px;border-radius:2px;background:{ChartImage.Hex(l.Token)};margin-right:5px\"></span>{E(l.Name)}</span>")) + "</div>");
                var png = draw(f.Svg);
                if (png is null)
                {
                    notDrawn++;
                    h.Append($"<p style=\"font-size:12px;color:{Muted}\">This chart could not be drawn as an image here; it is on the report.</p>");
                    t.AppendLine("  (a chart: see the report)");
                }
                else
                {
                    var id = $"chart{images.Count + 1}@insights";
                    images.Add((id, png));
                    var size = ChartImage.Size(f.Svg) ?? (720, 250);
                    var shown = Math.Min(Width - 44, size.Width);
                    var height = (int)Math.Round(size.Height * shown / (double)size.Width);
                    h.Append($"<img src=\"cid:{E(id)}\" width=\"{shown}\" height=\"{height}\" alt=\"{E(p.Title)}\" style=\"display:block;width:100%;max-width:{shown}px;height:auto;border:0\"/>");
                    t.AppendLine("  (a chart: in the HTML version of this email)");
                }
                if (f.Scale is not null) h.Append($"<p style=\"font-size:12px;color:{Muted};margin:4px 0 0\">{E(f.Scale)}</p>");
                foreach (var n in f.Notes) { h.Append($"<p style=\"font-size:12px;color:{Muted};margin:4px 0 0\">{E(n)}</p>"); t.AppendLine("  " + n); }
            }
        }
        if (kpis.Count == 0 && images.Count == 0 && notDrawn == 0)
        {
            h.Append($"<p style=\"font-size:14px;margin-top:14px\">This report has no headline figures or charts to send: its content is lists, which are not emailed.</p>");
            t.AppendLine("This report has no headline figures or charts to send: its content is lists, which are not emailed.");
        }
        else if (listsLeft)
            h.Append($"<p style=\"font-size:12px;color:{Muted};margin-top:14px\">The report's tables and lists are not emailed; they are on the report.</p>");

        if (reportUrl is not null)
        {
            h.Append($"<p style=\"margin:20px 0 0\"><a href=\"{E(reportUrl)}\" style=\"display:inline-block;background:{Accent};color:#ffffff;text-decoration:none;padding:9px 16px;border-radius:6px;font-weight:600;font-size:14px\">Open the report</a></p>");
            t.AppendLine().AppendLine($"Open the report: {reportUrl}");
        }
        var when = Subscriptions.When(frequency, dayOfWeek, dayOfMonth, time);
        var foot = $"You subscribed to this report for {Subscriptions.Period(days)}, sent {when}. " +
                   "It is built at each send with your access as it is then. Figures are aggregates; groups under the minimum size are not shown.";
        h.Append($"<p style=\"font-size:11px;color:{Faint};margin:22px 0 0\">{E(foot)}" +
                 (manageUrl is null ? "" : $" <a href=\"{E(manageUrl)}\" style=\"color:{Accent}\">Change or stop it in My subscriptions</a>.") + "</p>");
        t.AppendLine().AppendLine(foot + (manageUrl is null ? "" : $" Change or stop it: {manageUrl}"));
        h.Append("</td></tr></table></td></tr></table></body></html>");
        return new(subject, h.ToString(), t.ToString(), kpis.Count, images, notDrawn);
    }
}
