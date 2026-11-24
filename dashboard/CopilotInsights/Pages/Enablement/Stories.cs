using CopilotInsights.Data;
using CopilotInsights.Infrastructure;
using Microsoft.AspNetCore.Html;
using Microsoft.AspNetCore.Mvc;

namespace CopilotInsights.Pages.Enablement;

/// <summary>
/// Success stories: what Copilot did for people, in their own words, for the weekly report's "In their words". Anyone
/// signed in enters a story and sees their own; the Stories role sees every story, edits, approves or declines it, and
/// ticks the approved ones the next weekly report quotes (tools\Send-CopilotWeeklyReport.ps1 reads them when it sends,
/// stamps each with the week and clears the tick). A story only moves forward (<see cref="SuccessStories"/>). Deleting
/// hides a story; a full administrator restores it or purges it for good. Every change is in the audit log, an edit with
/// the text before it. Not in a section of the menu: it is open to everyone the dashboard lets in.
/// </summary>
public sealed class StoriesModel(PageServices s) : InsightsPage(s)
{
    public override string Title => "Success stories";
    public override string Persona => "Enablement";
    public override bool UsesFilters => false;
    public override string Intro => "Tell us what Copilot did for you, in your own words. Approved stories may be quoted in the weekly Copilot report: by your name only if you tick that you agree, otherwise as a colleague in your department.";

    bool Curator => Viewer.Has(Roles.Stories);

    sealed class Row
    {
        public int StoryId { get; set; }
        public string Quote { get; set; } = "";
        public string? App { get; set; }
        public string? UseCase { get; set; }
        public int? MinutesSaved { get; set; }
        public string Upn { get; set; } = "";
        public string? DisplayName { get; set; }
        public string? Unit { get; set; }
        public bool MayName { get; set; }
        public string Status { get; set; } = "";
        public string? DeclineReason { get; set; }
        public bool IncludeNext { get; set; }
        public DateTime EnteredUtc { get; set; }
        public string? ApprovedBy { get; set; }
        public bool EditedAfterSent { get; set; }
        public bool Deleted { get; set; }
        public string? DeletedBy { get; set; }
        public DateTime? DeletedUtc { get; set; }
        public string? Sends { get; set; }
    }

    const string Columns = @"s.StoryId, s.Quote, s.App, s.UseCase, s.MinutesSaved, s.Upn, s.DisplayName, s.Unit, s.MayName, s.Status, s.DeclineReason, s.IncludeNext,
       s.EnteredUtc, s.ApprovedBy, s.EditedAfterSent, s.Deleted, s.DeletedBy, s.DeletedUtc,
       (SELECT STRING_AGG(CONVERT(NVARCHAR(10), x.SentWeek, 23), N', ') WITHIN GROUP (ORDER BY x.SentWeek) FROM dash.SuccessStorySend AS x WHERE x.StoryId = s.StoryId) AS Sends";

    async Task<bool> ReadyAsync() => await Db.ScalarAsync<int>("SELECT CASE WHEN OBJECT_ID(N'dash.SuccessStory', N'U') IS NULL THEN 0 ELSE 1 END;") == 1;

    protected override async Task BuildAsync()
    {
        ShowDone();
        if (!await ReadyAsync())
        {
            Panels.Add(new("Not set up", new HtmlString($"<p>{E(await NotSetUpAsync("Success stories cannot be entered yet", "3.1.6", "dash.SuccessStory"))}</p>")));
            return;
        }
        var me = Viewer.Upn;
        var minGroup = await S.Settings.IntAsync("MinGroupSize", 5);

        /* Enter a story. */
        Panels.Add(new("Share a story", new HtmlString(me is null
            ? "<p>Your account is not in the people directory yet, so a story cannot be kept under your name. An administrator can check it in Admin › Access.</p>"
            : Form("add", StoryFields(null, null, null, null, false, true) + Button("Send it for review"))),
            "A curator reads every story before it can be quoted. You can change or withdraw yours until it is approved; after that, ask a curator, or withdraw it."));

        /* Their own stories. */
        if (me is not null)
        {
            var mine = await Db.QueryAsync<Row>($"SELECT {Columns} FROM dash.SuccessStory AS s WHERE s.Upn = @me AND s.Deleted = 0 ORDER BY s.EnteredUtc DESC;", new { me });
            var t = new Table { Headers = ["Entered (UTC)", "Story", "Status", ""], Zone = Zone, Sortable = false, EmptyText = "You have not shared a story yet." };
            foreach (var r in mine)
                t.Rows.Add([r.EnteredUtc, new HtmlString(QuoteHtml(r)), StatusText(r), new HtmlString(
                    (SuccessStories.OwnerMayEdit(r.Status) ? EditBox(r, owner: true) : "") +
                    (SuccessStories.MayWithdraw(r.Status) ? InlineForm("withdraw", Hidden("id", r.StoryId) +
                        Button("Withdraw", r.Status == SuccessStories.Sent ? "Withdraw this story? It is not sent again; the reports it went out in cannot be recalled." : "Withdraw this story? It will not be quoted.", quiet: true)) : ""))]);
            Panels.Add(new("Your stories", t.Render(), "Entered: waiting for a curator. Approved: may be quoted in a weekly report. Sent: quoted, on the date shown. Declined and withdrawn stories are not quoted."));
        }

        if (!Curator)
        {
            if (Viewer.IsAdmin) await DeletedAsync();
            return;
        }

        /* The curators' list. */
        var counts = (await Db.QueryAsync<(string Status, int N, int Ticked)>(
            "SELECT Status, COUNT(*), SUM(CASE WHEN IncludeNext = 1 THEN 1 ELSE 0 END) FROM dash.SuccessStory WHERE Deleted = 0 GROUP BY Status;")).ToList();
        int N(string st) => counts.Where(c => c.Status == st).Sum(c => c.N);
        var ticked = counts.Sum(c => c.Ticked);
        var perReport = Math.Clamp(await S.Settings.IntAsync("StoriesPerReport", 3), 0, 10);
        var oldest = await Db.ScalarAsync<DateTime?>("SELECT MIN(EnteredUtc) FROM dash.SuccessStory WHERE Deleted = 0 AND Status = 'Entered';");
        Kpis.Add(new("Waiting for review", Num(N(SuccessStories.Entered)), oldest is { } o ? $"oldest {(int)(DateTime.UtcNow - o).TotalDays:#,0} days" : "none waiting", N(SuccessStories.Entered) > 0 ? "warn" : null));
        Kpis.Add(new("Ticked for the next report", Num(ticked), $"it quotes up to {perReport}, oldest approved first", ticked == 0 ? "warn" : null));
        Kpis.Add(new("Approved, not sent", Num(N(SuccessStories.Approved)), "may be ticked for a report"));
        Kpis.Add(new("Sent", Num(N(SuccessStories.Sent)), "quoted in a weekly report"));

        var status = Request.Query["status"].FirstOrDefault() is { } q && (SuccessStories.Statuses.Contains(q) || q == "all") ? q : SuccessStories.Entered;
        var rows = await Db.QueryAsync<Row>($@"
SELECT TOP (500) {Columns} FROM dash.SuccessStory AS s
WHERE s.Deleted = 0 AND (@status = 'all' OR s.Status = @status)
ORDER BY s.IncludeNext DESC, s.EnteredUtc;", new { status });
        var units = (await Db.QueryAsync<(string Unit, int N)>(
            "SELECT Department, COUNT(*) FROM dash.Person WHERE AccountEnabled = 1 AND Department IS NOT NULL GROUP BY Department;")).ToDictionary(x => x.Unit, x => x.N, StringComparer.OrdinalIgnoreCase);
        var tabs = string.Join(" · ", SuccessStories.Statuses.Append("all").Select(st =>
            $"<a href=\"{E(Request.Path + "?status=" + st)}\"{(st == status ? " class=\"on\"" : "")}>{E(st == "all" ? "All" : st)} ({(st == "all" ? counts.Sum(c => c.N) : N(st)):#,0})</a>"));
        var ct = new Table { Headers = ["Entered (UTC)", "Story", "Quoted as", "Status", ""], Zone = Zone, Sortable = false, EmptyText = "No stories here." };
        foreach (var r in rows)
        {
            var who = SuccessStories.Attribution(r.DisplayName, r.Unit, r.MayName, PrivacyMode, r.Unit is null ? 0 : units.GetValueOrDefault(r.Unit), minGroup);
            ct.Rows.Add([r.EnteredUtc, new HtmlString(QuoteHtml(r) + (PrivacyMode ? "" : $"<p class=\"sub\">Entered by {E(r.DisplayName ?? r.Upn)}{(r.ApprovedBy is null ? "" : $"; approved by {E(r.ApprovedBy)}")}</p>")),
                who, StatusText(r), new HtmlString(CuratorActions(r))]);
        }
        Panels.Add(new("Stories", new HtmlString($"<p class=\"sub\">Status: {tabs}</p>" + ct.Render().Value),
            "Approve a story to let it be quoted, then tick it for the next report: the weekly report quotes the ticked ones when it sends (up to the number set in StoriesPerReport, oldest approved first) and clears the tick. " +
            "Ticking a sent story sends it again, on purpose. A story never goes back a step: a declined one is entered again as a new story. Your edits keep the story's status; each is in the audit log with the text before it. " +
            "Quoted as is what the report shows: the name only where the person agreed, else their department where it has at least the minimum group size, else a colleague."));
        if (Viewer.IsAdmin) await DeletedAsync();
    }

    /// <summary>Deleted stories, for a full administrator: restore one, or purge it for good.</summary>
    async Task DeletedAsync()
    {
        var gone = await Db.QueryAsync<Row>($"SELECT TOP (200) {Columns} FROM dash.SuccessStory AS s WHERE s.Deleted = 1 ORDER BY s.DeletedUtc DESC;");
        if (gone.Count == 0) return;
        var t = new Table { Headers = ["Deleted (UTC)", "By", "Story", ""], Zone = Zone, Sortable = false };
        foreach (var r in gone)
            t.Rows.Add([r.DeletedUtc, r.DeletedBy, new HtmlString(QuoteHtml(r)), new HtmlString(
                InlineForm("restore", Hidden("id", r.StoryId) + Button("Restore", quiet: true)) +
                InlineForm("purge", Hidden("id", r.StoryId) + Button("Delete permanently", "Delete this story permanently? It cannot be undone: only a line saying it was purged stays in the audit log.", quiet: true)))]);
        Panels.Add(new("Deleted stories", t.Render(), "Deleted stories are not listed or sent. Restore one to put it back as it was; delete it permanently when the person asks for their words to be removed."));
    }

    string StoryFields(string? quote, string? app, string? useCase, int? minutes, bool mayName, bool owner) =>
        $"<label class=\"quote\">Your story<textarea name=\"quote\" rows=\"4\" cols=\"80\" maxlength=\"{SuccessStories.QuoteMax}\" required>{E(quote)}</textarea>{FieldTip("quote", "Your story")}</label>" +
        "<div class=\"row\">" + Input("app", "App or agent", app, 20) + Input("useCase", "Kind of work", useCase, 24) +
        Input("minutesSaved", "Minutes saved a week", minutes?.ToString(System.Globalization.CultureInfo.InvariantCulture), 5, "number") + "</div>" +
        (owner ? $"<label class=\"check\"><input type=\"checkbox\" name=\"mayName\" value=\"true\"{(mayName ? " checked" : "")}/> The weekly report may quote me by name{FieldTip("mayName", "Quote me by name")}</label>" : "");

    string EditBox(Row r, bool owner) =>
        $"<details><summary>Change</summary>{Form("edit", Hidden("id", r.StoryId) + StoryFields(r.Quote, r.App, r.UseCase, r.MinutesSaved, r.MayName, owner) + Button("Save the change"))}</details>";

    string CuratorActions(Row r)
    {
        var html = new System.Text.StringBuilder();
        if (SuccessStories.MayApprove(r.Status)) html.Append(InlineForm("approve", Hidden("id", r.StoryId) + Button("Approve", quiet: true)));
        if (SuccessStories.MayInclude(r.Status))
            html.Append(r.IncludeNext
                ? InlineForm("include", Hidden("id", r.StoryId) + Hidden("on", "false") + Button("Untick", quiet: true))
                : InlineForm("include", Hidden("id", r.StoryId) + Hidden("on", "true") + Button("Tick for the next report",
                    r.Status == SuccessStories.Sent ? $"This story was sent on {r.Sends}. Send it again in the next report?" : null, quiet: true)));
        if (SuccessStories.MayDecline(r.Status))
            html.Append($"<details><summary>Decline</summary>{InlineForm("decline", Hidden("id", r.StoryId) + Select("reason", "Why", SuccessStories.DeclineReasons, null) + Button("Decline", "Decline this story? It is not quoted, and cannot be reopened.", quiet: true))}</details>");
        if (SuccessStories.CuratorMayEdit(r.Status)) html.Append(EditBox(r, owner: false));
        html.Append(InlineForm("delete", Hidden("id", r.StoryId) + Button("Delete",
            r.Status == SuccessStories.Sent ? "Delete this story? It is not listed or sent again; the reports it went out in cannot be recalled." : "Delete this story? It is not listed or sent. A full administrator can restore it.", quiet: true)));
        return html.ToString();
    }

    static string QuoteHtml(Row r) =>
        $"<div class=\"story\"><q>{E(r.Quote)}</q>" +
        (r.App is null && r.UseCase is null && r.MinutesSaved is null ? "" :
            $"<p class=\"sub\">{E(string.Join(" · ", new[] { r.App, r.UseCase, r.MinutesSaved is { } m ? $"{m:#,0} minutes saved a week" : null }.OfType<string>()))}</p>") +
        (r.EditedAfterSent ? "<p class=\"sub\">Edited after it was sent: the reports it went out in kept the text before.</p>" : "") + "</div>";

    static string StatusText(Row r) => r.Status switch
    {
        SuccessStories.Sent => $"Sent {r.Sends}" + (r.IncludeNext ? "; ticked to send again" : ""),
        SuccessStories.Declined => "Declined" + (r.DeclineReason is null ? "" : ": " + r.DeclineReason),
        SuccessStories.Approved => r.IncludeNext ? "Approved; ticked for the next report" : "Approved",
        _ => r.Status,
    };

    /* ---- changes: each audited (RoleChangeAsync); the story's own rules in SuccessStories ------------------------- */

    async Task<Row> StoryAsync(int id) =>
        (await Db.QueryAsync<Row>($"SELECT {Columns} FROM dash.SuccessStory AS s WITH (UPDLOCK) WHERE s.StoryId = @id;", new { id })).FirstOrDefault()
        ?? throw new ArgumentException("that story is gone.");

    bool Mine(Row r) => Viewer.Upn is { } me && string.Equals(r.Upn, me, StringComparison.OrdinalIgnoreCase);
    string By => Viewer.Upn ?? Viewer.WindowsName;

    public Task<IActionResult> OnPostAddAsync(string? quote, string? app, string? useCase, int? minutesSaved, bool mayName = false) => RoleChangeAsync(null, async () =>
    {
        var upn = Viewer.Upn ?? throw new ArgumentException("your account is not in the people directory, so a story cannot be kept under your name.");
        var q = SuccessStories.CleanQuote(quote);
        var person = (await Db.QueryAsync<(string? DisplayName, string? Department)>("SELECT DisplayName, Department FROM dash.Person WHERE Upn = @upn;", new { upn })).FirstOrDefault();
        var id = await Db.ScalarAsync<int>(@"
INSERT INTO dash.SuccessStory (Quote, App, UseCase, MinutesSaved, Upn, DisplayName, Unit, MayName, UpdatedBy, UpdatedUtc)
OUTPUT inserted.StoryId VALUES (@q, @app, @useCase, @minutes, @upn, @name, @unit, @mayName, @upn, SYSUTCDATETIME());",
            new { q, app = SuccessStories.Clip(app, 200), useCase = SuccessStories.Clip(useCase, 200), minutes = SuccessStories.Minutes(minutesSaved), upn,
                  name = person.DisplayName ?? Viewer.DisplayName, unit = person.Department, mayName });
        return $"Thank you: story {id} is sent for review{(mayName ? ", and may be quoted by your name" : "")}.";
    });

    public Task<IActionResult> OnPostEditAsync(int id, string? quote, string? app, string? useCase, int? minutesSaved, bool mayName = false) => RoleChangeAsync(null, async () =>
    {
        var r = await StoryAsync(id);
        var owner = Mine(r) && SuccessStories.OwnerMayEdit(r.Status) && !r.Deleted;
        if (!owner && !(Curator && SuccessStories.CuratorMayEdit(r.Status) && !r.Deleted))
            throw new ArgumentException(Mine(r) ? "an approved story is changed by a curator now; ask one, or withdraw it." : "only a curator (the Stories role) changes someone else's story.");
        var q = SuccessStories.CleanQuote(quote);
        /* The consent to be named is the person's own: a curator's edit keeps it as they gave it. */
        await Db.ExecuteAsync(@"
UPDATE dash.SuccessStory SET Quote = @q, App = @app, UseCase = @useCase, MinutesSaved = @minutes, MayName = CASE WHEN @owner = 1 THEN @mayName ELSE MayName END,
       EditedAfterSent = CASE WHEN Status = 'Sent' THEN 1 ELSE EditedAfterSent END, UpdatedBy = @by, UpdatedUtc = SYSUTCDATETIME()
WHERE StoryId = @id;", new { id, q, app = SuccessStories.Clip(app, 200), useCase = SuccessStories.Clip(useCase, 200), minutes = SuccessStories.Minutes(minutesSaved), owner, mayName, by = By });
        await S.Audit.WriteAsync(Viewer, "Story", Request.Path, null, $"Story {id} before this edit: {r.Quote}");
        return $"Story {id} changed; its status stays {r.Status.ToLowerInvariant()}. The text before is in the audit log.";
    });

    public Task<IActionResult> OnPostWithdrawAsync(int id) => RoleChangeAsync(null, async () =>
    {
        var r = await StoryAsync(id);
        if (!Mine(r)) throw new ArgumentException("only the person who entered a story withdraws it; a curator declines or deletes it.");
        var next = SuccessStories.Next(r.Status, "withdraw");
        await Db.ExecuteAsync("UPDATE dash.SuccessStory SET Status = @next, IncludeNext = 0, UpdatedBy = @by, UpdatedUtc = SYSUTCDATETIME() WHERE StoryId = @id;", new { id, next, by = By });
        return $"Story {id} is withdrawn: it will not be quoted.";
    });

    public Task<IActionResult> OnPostApproveAsync(int id) => RoleChangeAsync(Roles.Stories, async () =>
    {
        var r = await StoryAsync(id);
        var next = SuccessStories.Next(r.Status, "approve");
        await Db.ExecuteAsync("UPDATE dash.SuccessStory SET Status = @next, ApprovedBy = @by, ApprovedUtc = SYSUTCDATETIME(), UpdatedBy = @by, UpdatedUtc = SYSUTCDATETIME() WHERE StoryId = @id;", new { id, next, by = By });
        return $"Story {id} is approved. Tick it for the next report to have it quoted.";
    });

    public Task<IActionResult> OnPostDeclineAsync(int id, string? reason) => RoleChangeAsync(Roles.Stories, async () =>
    {
        var r = await StoryAsync(id);
        var why = SuccessStories.DeclineReasons.FirstOrDefault(x => x == reason) ?? throw new ArgumentException("choose why the story is declined.");
        var next = SuccessStories.Next(r.Status, "decline");
        await Db.ExecuteAsync("UPDATE dash.SuccessStory SET Status = @next, DeclineReason = @why, IncludeNext = 0, UpdatedBy = @by, UpdatedUtc = SYSUTCDATETIME() WHERE StoryId = @id;", new { id, next, why, by = By });
        return $"Story {id} is declined ({why}). The person who entered it sees why.";
    });

    public Task<IActionResult> OnPostIncludeAsync(int id, bool on) => RoleChangeAsync(Roles.Stories, async () =>
    {
        var r = await StoryAsync(id);
        if (on && (!SuccessStories.MayInclude(r.Status) || r.Deleted)) throw new ArgumentException("only an approved or sent story can be ticked for the next report.");
        await Db.ExecuteAsync("UPDATE dash.SuccessStory SET IncludeNext = @on, UpdatedBy = @by, UpdatedUtc = SYSUTCDATETIME() WHERE StoryId = @id;", new { id, on, by = By });
        return on ? $"Story {id} is ticked for the next weekly report{(r.Status == SuccessStories.Sent ? $", to be sent again (sent before: {r.Sends})" : "")}." : $"Story {id} is no longer ticked.";
    });

    public Task<IActionResult> OnPostDeleteAsync(int id) => RoleChangeAsync(Roles.Stories, async () =>
    {
        await StoryAsync(id);
        await Db.ExecuteAsync("UPDATE dash.SuccessStory SET Deleted = 1, IncludeNext = 0, DeletedBy = @by, DeletedUtc = SYSUTCDATETIME() WHERE StoryId = @id;", new { id, by = By });
        return $"Story {id} is deleted: not listed and not sent. A full administrator can restore it.";
    });

    public Task<IActionResult> OnPostRestoreAsync(int id) => RoleChangeAsync(Roles.Admin, async () =>
    {
        await StoryAsync(id);
        await Db.ExecuteAsync("UPDATE dash.SuccessStory SET Deleted = 0, DeletedBy = NULL, DeletedUtc = NULL, UpdatedBy = @by, UpdatedUtc = SYSUTCDATETIME() WHERE StoryId = @id;", new { id, by = By });
        return $"Story {id} is restored, as it was.";
    });

    public Task<IActionResult> OnPostPurgeAsync(int id) => RoleChangeAsync(Roles.Admin, async () =>
    {
        var r = await StoryAsync(id);
        if (!r.Deleted) throw new ArgumentException("delete the story first; only a deleted story is purged.");
        await Db.ExecuteAsync("DELETE FROM dash.SuccessStory WHERE StoryId = @id;", new { id });
        /* Its words go from the audit log too: each edit's earlier text. The lines that say what was done stay. */
        var scrubbed = await Db.ExecuteAsync("UPDATE dash.AuditLog SET Detail = @gone WHERE Kind = 'Story' AND Detail LIKE @like;",
            new { gone = $"Story {id} before this edit: (purged)", like = $"Story {id} before this edit:%" });
        return $"Story {id} is deleted permanently{(scrubbed > 0 ? $", and its earlier text removed from {scrubbed:#,0} audit log line(s)" : "")}.";
    });
}
