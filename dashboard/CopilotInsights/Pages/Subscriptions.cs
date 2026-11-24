using System.Globalization;
using CopilotInsights.Data;
using CopilotInsights.Infrastructure;
using Microsoft.AspNetCore.Html;
using Microsoft.AspNetCore.Mvc;

namespace CopilotInsights.Pages;

/// <summary>
/// My subscriptions: the reports a person has sent to them by email, on their own schedule (dash.ReportSubscription).
/// They subscribe from a report's "Email me this report", which opens this page with the report and its filters ready;
/// here they set the period, how often, the day and the time, change, pause, send one now or stop one. Only to
/// themselves: the email is built with their access, at each send (<see cref="SubscriptionSender"/>). A full
/// administrator also sees everyone's, and can pause or stop any. Every change is in the audit log.
/// </summary>
public sealed class SubscriptionsModel(PageServices s) : InsightsPage(s)
{
    public override string Title => "My subscriptions";
    public override string Persona => "Email";
    public override bool UsesFilters => false;
    public override string Intro => "Reports sent to you by email, when you choose: their headline figures and charts, never their lists, with a link to the report. Each is built at the time it is sent, with your access as it is then.";

    sealed class Row
    {
        public int SubscriptionId { get; set; }
        public string Upn { get; set; } = "";
        public string Path { get; set; } = "";
        public string Title { get; set; } = "";
        public string? Query { get; set; }
        public int Days { get; set; }
        public string Frequency { get; set; } = "";
        public byte? DayOfWeek { get; set; }
        public byte? DayOfMonth { get; set; }
        public TimeSpan SendTime { get; set; }
        public bool Paused { get; set; }
        public string? PausedReason { get; set; }
        public DateTime? NextDueUtc { get; set; }
        public DateTime? LastSentUtc { get; set; }
        public string? LastStatus { get; set; }
        public string? DisplayName { get; set; }
    }

    const string Columns = "s.SubscriptionId, s.Upn, s.Path, s.Title, s.Query, s.Days, s.Frequency, s.DayOfWeek, s.DayOfMonth, s.SendTime, s.Paused, s.PausedReason, s.NextDueUtc, s.LastSentUtc, s.LastStatus";

    async Task<bool> ReadyAsync() => await Db.ScalarAsync<int>("SELECT CASE WHEN OBJECT_ID(N'dash.ReportSubscription', N'U') IS NULL THEN 0 ELSE 1 END;") == 1;
    async Task<bool> OnAsync() => await S.Settings.StringAsync("ReportSubscriptions") != "0";
    async Task<TimeSpan> EarliestAsync() => Subscriptions.ParseTime(await S.Settings.StringAsync("SubscriptionEarliest"), new TimeSpan(7, 30, 0));
    async Task<int> CapAsync() => Math.Clamp(await S.Settings.IntAsync("SubscriptionsPerPerson", 20), 1, 100);

    protected override async Task BuildAsync()
    {
        ShowDone();
        if (!await ReadyAsync())
        {
            Panels.Add(new("Not set up", new HtmlString($"<p>{E(await NotSetUpAsync("Reports cannot be sent by email yet", "3.2.0", "dash.ReportSubscription", "dash.ReportSubscriptionSend"))}</p>")));
            return;
        }
        var on = await OnAsync();
        if (!on) Notice = "An administrator has turned report subscriptions off: none is sent until they are turned on again (Admin › Alerts and digests). Yours are kept.";
        var me = Viewer.Upn;
        if (me is null)
        {
            Panels.Add(new("Not available", new HtmlString("<p>Your account is not in the people directory yet, so there is no address to send a report to. An administrator can check it in Admin › Access.</p>")));
            return;
        }
        var zone = await S.Settings.ZoneAsync();
        var earliest = await EarliestAsync();
        var cap = await CapAsync();
        var mine = await Db.QueryAsync<Row>($"SELECT {Columns} FROM dash.ReportSubscription AS s WHERE s.Upn = @me AND s.Deleted = 0 ORDER BY s.Title, s.SubscriptionId;", new { me });

        /* The form: a report to subscribe to (from its "Email me this report"), or one of theirs to change. */
        var editId = int.TryParse(Request.Query["edit"], out var eid) ? eid : (int?)null;
        var editing = editId is { } x ? mine.FirstOrDefault(r => r.SubscriptionId == x) : null;
        var addPath = Request.Query["add"].FirstOrDefault();
        if (editing is not null)
            Panels.Add(new($"Change: {editing.Title}", new HtmlString(FormFor(editing.SubscriptionId, editing.Path, editing.Title, editing.Query, editing.Days, editing.Frequency, editing.DayOfWeek, editing.DayOfMonth, editing.SendTime, earliest, zone)),
                "The report keeps the filters it was subscribed with. To change those, open the report with the filters you want and subscribe again."));
        else if (addPath is not null)
        {
            if (Subscriptions.Report(addPath) is not { } report)
                Panels.Add(new("Not a report that can be emailed", new HtmlString("<p>That report cannot be sent by email: lists that name people, Admin pages and pages that are not reports are not sent. Open a report and choose <b>Email me this report</b>.</p>")));
            else if (mine.Count >= cap)
                Panels.Add(new("Too many subscriptions", new HtmlString($"<p>You have {mine.Count:#,0} subscriptions, the most one person may have ({cap:#,0}). Stop one below to add this one.</p>")));
            else
            {
                var days = int.TryParse(Request.Query["d"], out var d) && ReportContext.DayChoices.Contains(d) ? d : 30;
                var query = Subscriptions.KeepQuery(Request.Query.Where(kv => kv.Key is not ("add" or "d")).Select(kv => new KeyValuePair<string, string>(kv.Key, kv.Value.ToString())));
                var filters = query is null ? "" : $" It keeps the filters it was open with ({E(Uri.UnescapeDataString(query.Replace("&", ", ").Replace("=", " ")))}).";
                Panels.Add(new($"Subscribe: {report.Title}", new HtmlString(FormFor(null, report.Path, report.Title, query, days, Subscriptions.Weekly, 1, 1, earliest, earliest, zone)),
                    $"Choose the period the email covers, how often it comes and when.{filters} Up to {Subscriptions.DailyUpTo} days can come every weekday; longer periods weekly or monthly."));
            }
        }

        /* Theirs. */
        Kpis.Add(new("Your subscriptions", $"{mine.Count:#,0} of {cap:#,0}", "the most one person may have"));
        Kpis.Add(new("Paused", Num(mine.Count(r => r.Paused)), "not sent until you resume them", mine.Any(r => r.Paused) ? "warn" : null));
        var next = mine.Where(r => !r.Paused && r.NextDueUtc is not null).MinBy(r => r.NextDueUtc);
        Kpis.Add(new("Next email", next is null ? "None" : TimeZoneInfo.ConvertTimeFromUtc(next.NextDueUtc!.Value, zone).ToString("ddd d MMM, HH:mm", CultureInfo.InvariantCulture), next?.Title));
        var t = new Table { Headers = ["Report", "Period", "When", "Next", "Last", ""], Zone = zone, Sortable = false, EmptyText = "You have no subscriptions. Open a report and choose Email me this report, at the top of the page." };
        foreach (var r in mine)
            t.Rows.Add([new HtmlString($"<a href=\"{E(Subscriptions.Address(r.Path, r.Days, r.Query))}\">{E(r.Title)}</a>" + (r.Query is null ? "" : $"<p class=\"sub\">{E(Uri.UnescapeDataString(r.Query.Replace("&", ", ").Replace("=", " ")))}</p>")),
                Subscriptions.Period(r.Days), Cap(Subscriptions.When(r.Frequency, r.DayOfWeek, r.DayOfMonth, r.SendTime)),
                r.Paused ? new HtmlString($"<b>Paused</b>{(r.PausedReason is null ? "" : $"<p class=\"sub\">{E(r.PausedReason)}</p>")}") : (object?)When(r.NextDueUtc),
                new HtmlString(E(r.LastSentUtc is null ? "Not sent yet" : "Sent " + When(r.LastSentUtc)) + (r.LastStatus is null ? "" : $"<p class=\"sub\">{E(r.LastStatus)}</p>")),
                new HtmlString(Actions(r))]);
        Panels.Add(new("Your subscriptions", t.Render(),
            $"Times are in {E(zone.StandardName)}. Daily means every weekday. If the night's refresh is late, an email waits for it until midday at most, then goes saying how old its figures are. Send me one now sends it at once to check how it looks; its schedule does not change."));

        if (Viewer.IsAdmin) await EveryoneAsync(zone);
    }

    static string Cap(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    string Actions(Row r) => "<div class=\"acts\">" +
        $"<a class=\"btn quiet\" href=\"{E(Request.Path + "?edit=" + r.SubscriptionId.ToString(CultureInfo.InvariantCulture))}\">Change</a>" +
        InlineForm("pause", Hidden("id", r.SubscriptionId) + Hidden("on", r.Paused ? "false" : "true") + Button(r.Paused ? "Resume" : "Pause", quiet: true)) +
        (r.Paused ? "" : InlineForm("send", Hidden("id", r.SubscriptionId) + Button("Send me one now", quiet: true))) +
        InlineForm("delete", Hidden("id", r.SubscriptionId) + Button("Stop", "Stop this subscription? No more emails of this report are sent.", quiet: true)) + "</div>";

    /// <summary>A full administrator's view of every subscription: who, what, when, and pause or stop.</summary>
    async Task EveryoneAsync(TimeZoneInfo zone)
    {
        var all = await Db.QueryAsync<Row>($@"
SELECT TOP (500) {Columns}, p.DisplayName FROM dash.ReportSubscription AS s LEFT JOIN dash.Person AS p ON p.Upn = s.Upn
WHERE s.Deleted = 0 ORDER BY s.Paused DESC, s.Upn, s.Title;");
        var sent = await Db.QueryAsync<(string Status, int N)>(@"
SELECT Status, COUNT(*) FROM dash.ReportSubscriptionSend WHERE ClaimedUtc >= DATEADD(DAY, -7, SYSUTCDATETIME()) GROUP BY Status;");
        int N(string st) => sent.Where(x => x.Status == st).Sum(x => x.N);
        var t = new Table { Headers = ["Person", "Report", "When", "Status", ""], Zone = zone, Sortable = false, EmptyText = "Nobody has subscribed to a report yet." };
        foreach (var r in all)
            t.Rows.Add([PrivacyMode ? "A person" : r.DisplayName ?? r.Upn, r.Title + " · " + Subscriptions.Period(r.Days), Cap(Subscriptions.When(r.Frequency, r.DayOfWeek, r.DayOfMonth, r.SendTime)),
                r.Paused ? "Paused" + (r.PausedReason is null ? "" : ": " + r.PausedReason) : r.LastStatus ?? "Not sent yet",
                new HtmlString("<div class=\"acts\">" + InlineForm("adminpause", Hidden("id", r.SubscriptionId) + Hidden("on", r.Paused ? "false" : "true") + Button(r.Paused ? "Resume" : "Pause", quiet: true)) +
                    InlineForm("admindelete", Hidden("id", r.SubscriptionId) + Button("Stop", "Stop this person's subscription? They see it gone from My subscriptions.", quiet: true)) + "</div>")]);
        Panels.Add(new("Everyone's subscriptions", new HtmlString(
            $"<p class=\"sub\">The last 7 days: {N("Sent"):#,0} sent, {N("Failed"):#,0} not sent, {N("Paused"):#,0} paused for lost access, {N("Missed"):#,0} missed while the site was down. Each send is in Admin › Email's log as Subscription.</p>" + t.Render().Value),
            "For full administrators. Pause or stop a subscription that should not go out; the person sees it on their own page. Settings are on Admin › Alerts and digests."));
    }

    /// <summary>The form that sets a subscription: the period, how often, the day and the time.</summary>
    string FormFor(int? id, string path, string title, string? query, int days, string frequency, int? dayOfWeek, int? dayOfMonth, TimeSpan time, TimeSpan earliest, TimeZoneInfo zone)
    {
        var slots = Subscriptions.Slots(earliest);
        if (!slots.Contains(time)) time = slots[0];
        var periods = ReportContext.DayChoices.Select(d => (d.ToString(CultureInfo.InvariantCulture), ReportContext.ChoiceLabel(d)));
        var weekdays = new[] { 1, 2, 3, 4, 5, 6, 0 }.Select(d => (d.ToString(CultureInfo.InvariantCulture), ((DayOfWeek)d).ToString()));
        var monthDays = Enumerable.Range(1, 28).Select(d => (d.ToString(CultureInfo.InvariantCulture), Subscriptions.Ordinal(d)));
        var body = Hidden("id", id) + Hidden("path", path) + Hidden("query", query) +
            "<div class=\"row\">" +
            Select("days", "Period", periods, days.ToString(CultureInfo.InvariantCulture)) +
            Select("frequency", "How often", Subscriptions.Frequencies.Select(f => (f, f == Subscriptions.Daily ? "Daily (weekdays)" : f)), frequency) +
            Select("dayOfWeek", "Day of the week", weekdays, (dayOfWeek ?? 1).ToString(CultureInfo.InvariantCulture)) +
            Select("dayOfMonth", "Day of the month", monthDays, (dayOfMonth ?? 1).ToString(CultureInfo.InvariantCulture)) +
            Select("time", $"Time ({zone.StandardName})", slots.Select(s => (Subscriptions.TimeText(s), Subscriptions.TimeText(s))), Subscriptions.TimeText(time)) +
            "</div>" + Button(id is null ? "Subscribe" : "Save the change");
        return Form("save", body).Replace("<form class=\"admin\"", "<form class=\"admin\" data-subscribe")
            + $"<p class=\"sub\">Sent to your own address from the directory. <a href=\"{E(Subscriptions.Address(path, days, query))}\">Back to {E(title)}</a></p>";
    }

    /* ---- changes: each audited (RoleChangeAsync); only a person's own, but for a full administrator's pause and stop ---- */

    string By => Viewer.Upn ?? Viewer.WindowsName;

    async Task<Row> MineAsync(int id) =>
        (await Db.QueryAsync<Row>($"SELECT {Columns} FROM dash.ReportSubscription AS s WITH (UPDLOCK) WHERE s.SubscriptionId = @id AND s.Upn = @me AND s.Deleted = 0;", new { id, me = Viewer.Upn }))
        .FirstOrDefault() ?? throw new ArgumentException("that subscription is not yours, or it was stopped.");

    public Task<IActionResult> OnPostSaveAsync(int? id, string? path, string? query, int days, string? frequency, int? dayOfWeek, int? dayOfMonth, string? time) => RoleChangeAsync(null, async () =>
    {
        var me = Viewer.Upn ?? throw new ArgumentException("your account is not in the people directory, so there is no address to send to.");
        if (!await ReadyAsync()) throw new ArgumentException("report subscriptions are not set up: an administrator runs Install-CopilotAudit.ps1 -Dashboard from 3.2.0.");
        if (!await OnAsync()) throw new ArgumentException("an administrator has turned report subscriptions off.");
        var report = Subscriptions.Report(path) ?? throw new ArgumentException("that report cannot be sent by email.");
        var earliest = await EarliestAsync();
        var at = Subscriptions.ParseTime(time, TimeSpan.MinValue);
        int? dow = frequency == Subscriptions.Weekly ? dayOfWeek : null;
        int? dom = frequency == Subscriptions.Monthly ? dayOfMonth : null;
        if (Subscriptions.Problem(days, frequency, dow, dom, at == TimeSpan.MinValue ? null : at, earliest) is { } problem) throw new ArgumentException(problem);
        var kept = Subscriptions.KeepQuery(Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(query ?? "").Select(kv => new KeyValuePair<string, string>(kv.Key, kv.Value.ToString())));

        /* Whether it can be sent: the access the sender will find without their sign-in must open the report. */
        var later = await S.Viewers.WithoutSignInAsync(me, Viewer.WindowsName, Viewer.SignedInWith);
        var hidden = Pages.HiddenReports.ForViewer(await Pages.HiddenReports.GetAsync(S.Settings), await Pages.HiddenReports.BetaAsync(S.Settings), later);
        if (!Nav.SectionOpen(report.Path, later, hidden) || Pages.HiddenReports.IsOff(hidden, report.Path, later) || !later.Admitted)
            throw new ArgumentException(Nav.SectionOpen(report.Path, Viewer, HiddenReports)
                ? "your access to this report comes through a group the dashboard checks only while you are signed in, so it cannot be sent while you are away. An administrator can grant it to you by name, or to a group the nightly directory load records."
                : "this report is not open to you.");

        var zone = await S.Settings.ZoneAsync();
        var next = Subscriptions.NextDue(frequency!, dow, dom, at, DateTime.UtcNow, zone);
        var args = new { me, signIn = Viewer.WindowsName, with = Viewer.SignedInWith, path = report.Path, title = report.Title, query = kept, days, frequency, dow, dom, at, next, by = By };
        if (id is { } existing)
        {
            await MineAsync(existing);
            await Db.ExecuteAsync(@"
UPDATE dash.ReportSubscription SET Days = @days, Frequency = @frequency, DayOfWeek = @dow, DayOfMonth = @dom, SendTime = @at, NextDueUtc = @next,
       SignInName = @signIn, SignedInWith = @with, Paused = 0, PausedReason = NULL, UpdatedUtc = SYSUTCDATETIME(), UpdatedBy = @by
WHERE SubscriptionId = @existing;", new { args.days, args.frequency, args.dow, args.dom, args.at, args.next, args.signIn, args.with, args.by, existing });
            return $"{report.Title} is now sent {Subscriptions.When(frequency!, dow, dom, at)}, for {Subscriptions.Period(days)}. The next one goes {When(next)}.";
        }
        var count = await Db.ScalarAsync<int>("SELECT COUNT(*) FROM dash.ReportSubscription WITH (UPDLOCK, HOLDLOCK) WHERE Upn = @me AND Deleted = 0;", new { me });
        var cap = await CapAsync();
        if (count >= cap) throw new ArgumentException($"you have {count:#,0} subscriptions, the most one person may have ({cap:#,0}); stop one first.");
        await Db.ExecuteAsync(@"
INSERT INTO dash.ReportSubscription (Upn, SignInName, SignedInWith, Path, Title, Query, Days, Frequency, DayOfWeek, DayOfMonth, SendTime, NextDueUtc, UpdatedBy)
VALUES (@me, @signIn, @with, @path, @title, @query, @days, @frequency, @dow, @dom, @at, @next, @by);", args);
        return $"Subscribed: {report.Title}, for {Subscriptions.Period(days)}, {Subscriptions.When(frequency!, dow, dom, at)}. The first one goes {When(next)}.";
    });

    public Task<IActionResult> OnPostPauseAsync(int id, bool on) => RoleChangeAsync(null, async () =>
    {
        var r = await MineAsync(id);
        return await PauseAsync(r, on, "you");
    });

    async Task<string> PauseAsync(Row r, bool on, string who)
    {
        if (on)
        {
            await Db.ExecuteAsync("UPDATE dash.ReportSubscription SET Paused = 1, PausedReason = @why, UpdatedUtc = SYSUTCDATETIME(), UpdatedBy = @by WHERE SubscriptionId = @id;",
                new { id = r.SubscriptionId, why = $"Paused by {who}.", by = By });
            return $"{r.Title} is paused: nothing is sent until it is resumed.";
        }
        var next = Subscriptions.NextDue(r.Frequency, r.DayOfWeek, r.DayOfMonth, r.SendTime, DateTime.UtcNow, await S.Settings.ZoneAsync());
        await Db.ExecuteAsync("UPDATE dash.ReportSubscription SET Paused = 0, PausedReason = NULL, NextDueUtc = @next, UpdatedUtc = SYSUTCDATETIME(), UpdatedBy = @by WHERE SubscriptionId = @id;",
            new { id = r.SubscriptionId, next, by = By });
        return $"{r.Title} is resumed. The next one goes {When(next)}; access is checked again then.";
    }

    public Task<IActionResult> OnPostDeleteAsync(int id) => RoleChangeAsync(null, async () =>
    {
        var r = await MineAsync(id);
        await Db.ExecuteAsync("UPDATE dash.ReportSubscription SET Deleted = 1, UpdatedUtc = SYSUTCDATETIME(), UpdatedBy = @by WHERE SubscriptionId = @id;", new { id, by = By });
        return $"Stopped: {r.Title} is no longer sent to you.";
    });

    /// <summary>One now, to their own address: built and sent outside the change's transaction, then audited.</summary>
    public async Task<IActionResult> OnPostSendAsync(int id)
    {
        if (!await PrepareAsync()) return Page();
        string done;
        try
        {
            var r = await MineAsync(id);
            if (r.Paused) throw new ArgumentException("resume it first.");
            done = $"{r.Title}: " + await HttpContext.RequestServices.GetRequiredService<SubscriptionSender>().SendNowAsync(id, HttpContext.RequestAborted);
        }
        catch (ArgumentException ex) { done = "Not sent: " + ex.Message; }
        await S.Audit.WriteAsync(Viewer, "Change", Request.Path, null, done);
        TempData["done"] = done;
        return Redirect(Request.Path);
    }

    async Task<Row> AnyAsync(int id) =>
        (await Db.QueryAsync<Row>($"SELECT {Columns} FROM dash.ReportSubscription AS s WITH (UPDLOCK) WHERE s.SubscriptionId = @id AND s.Deleted = 0;", new { id })).FirstOrDefault()
        ?? throw new ArgumentException("that subscription was stopped.");

    public Task<IActionResult> OnPostAdminPauseAsync(int id, bool on) => RoleChangeAsync(Roles.Admin, async () =>
    {
        var r = await AnyAsync(id);
        return $"{r.Upn}: " + await PauseAsync(r, on, "an administrator");
    });

    public Task<IActionResult> OnPostAdminDeleteAsync(int id) => RoleChangeAsync(Roles.Admin, async () =>
    {
        var r = await AnyAsync(id);
        await Db.ExecuteAsync("UPDATE dash.ReportSubscription SET Deleted = 1, UpdatedUtc = SYSUTCDATETIME(), UpdatedBy = @by WHERE SubscriptionId = @id;", new { id, by = By });
        return $"Stopped {r.Upn}'s subscription to {r.Title}.";
    });
}
