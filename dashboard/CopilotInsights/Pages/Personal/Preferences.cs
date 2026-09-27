using CopilotInsights.Data;
using CopilotInsights.Infrastructure;
using Microsoft.AspNetCore.Html;
using Microsoft.AspNetCore.Mvc;

namespace CopilotInsights.Pages.Personal;

/// <summary>Each person's own email choices: a weekly email of their own use, and (for managers) whether to get their team's digest.</summary>
public sealed class EmailModel(PageServices s, OpsData ops) : InsightsPage(s)
{
    public override string Title => "Email";
    public override string Persona => "My Copilot";
    public override bool UsesFilters => false;
    public override string Intro => "What Copilot Insights emails you. Your own weekly email is only ever about you and only ever sent to you.";

    protected override async Task BuildAsync()
    {
        ShowDone();
        if (Viewer.Upn is null) { Notice = "Your sign-in isn't matched to a person in the directory, so there is nothing to send you."; return; }
        if (!await ops.HasAsync("dash.DigestPreference")) { Notice = "Email choices arrive with 2.3.0's operations schema."; return; }
        var pref = (await Db.QueryAsync<(bool ManagerDigestOptOut, bool PersonalDigest)>(
            "SELECT ManagerDigestOptOut, PersonalDigest FROM dash.DigestPreference WHERE Upn = @upn;", new { upn = Viewer.Upn })).FirstOrDefault();
        var personalOn = await S.Settings.StringAsync("PersonalDigest") != "Off";
        var managerMode = await S.Settings.StringAsync("ManagerDigest") ?? "Off";
        var min = await S.Settings.IntAsync("ManagerDigestMinPeople", 10);
        var mail = await Db.ScalarAsync<string?>("SELECT NULLIF(Mail, N'') FROM audit.UserDirectory WHERE UserPrincipalName = @upn;", new { upn = Viewer.Upn }) ?? Viewer.Upn;
        var body = "";
        if (personalOn)
            body += $"<label><input type=\"checkbox\" name=\"personal\" value=\"1\"{(pref.PersonalDigest ? " checked" : "")}/> Send me a weekly email about my own use of Copilot</label>";
        if (managerMode != "Off" && Viewer.PeopleBelow >= min)
            body += $"<label><input type=\"checkbox\" name=\"manager\" value=\"1\"{(pref.ManagerDigestOptOut ? "" : " checked")}/> Send me the {managerMode.ToLowerInvariant()} digest for my organization ({Viewer.PeopleBelow:#,0} people)</label>";
        if (body == "") { Notice = "There is nothing to choose: the administrators have not turned on the personal or manager emails."; return; }
        Panels.Add(new("Your email", new HtmlString(Form("save", body + Button("Save"))),
            $"Sent to {mail}. The weekly emails go out on the day and hour set by the administrators.", "half"));
    }

    public Task<IActionResult> OnPostSaveAsync(string? personal, string? manager) => RoleChangeAsync(null, async () =>
    {
        if (Viewer.Upn is null) throw new ArgumentException("your sign-in isn't matched to a person in the directory.");
        var optOut = manager != "1" && Viewer.PeopleBelow > 0;
        await Db.ExecuteAsync(@"
UPDATE dash.DigestPreference SET PersonalDigest = @p, ManagerDigestOptOut = @o, UpdatedUtc = SYSUTCDATETIME() WHERE Upn = @upn;
IF @@ROWCOUNT = 0 INSERT INTO dash.DigestPreference (Upn, PersonalDigest, ManagerDigestOptOut) VALUES (@upn, @p, @o);",
            new { upn = Viewer.Upn, p = personal == "1", o = optOut });
        return "Saved: " + (personal == "1" ? "you will get your weekly email" : "no weekly email about your own use") +
               (Viewer.PeopleBelow > 0 ? optOut ? "; no manager digest." : "; the manager digest when it is on." : ".");
    });
}

/// <summary>Each person's saved views: a page with its period, scope and filters, one of which can be their start page.</summary>
public sealed class ViewsModel(PageServices s, OpsData ops) : InsightsPage(s)
{
    public override string Title => "Saved views";
    public override string Persona => "My Copilot";
    public override bool UsesFilters => false;
    public override string Intro => "The pages you saved with Save view, with their period, part of the organization and filters. Make one your start page to open it when you come to the dashboard.";

    string Owner => Viewer.Upn ?? Viewer.WindowsName;

    /// <summary>A saved view is a path on this site that the navigation lists, with its query: never another site.</summary>
    public static bool SafeUrl(string? url)
    {
        if (string.IsNullOrEmpty(url) || url.Length > 600 || !url.StartsWith('/') || url.StartsWith("//") || url.Contains('\\') || url.Contains("handler=", StringComparison.OrdinalIgnoreCase)) return false;
        var path = url.Split('?')[0].TrimEnd('/');
        return Nav.Groups.SelectMany(g => g.Pages).Any(p => p.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
    }

    protected override async Task BuildAsync()
    {
        ShowDone();
        if (!await ops.HasAsync("dash.SavedView")) { Notice = "Saved views arrive with 2.3.0's operations schema."; return; }
        var views = await Db.QueryAsync<(int ViewId, string Name, string Url, bool IsStart, DateTime CreatedUtc)>(
            "SELECT ViewId, Name, Url, IsStart, CreatedUtc FROM dash.SavedView WHERE Owner = @o ORDER BY IsStart DESC, Name;", new { o = Owner });
        var t = new Table { Headers = ["View", "Page", "Saved", "Start page", ""], EmptyText = "No saved views yet. Open a report, set its period and part of the organization, and use Save view at the top." };
        foreach (var v in views)
        {
            var page = Nav.Groups.SelectMany(g => g.Pages).FirstOrDefault(p => p.Path.Equals(v.Url.Split('?')[0].TrimEnd('/'), StringComparison.OrdinalIgnoreCase));
            t.Rows.Add([new Link(v.Name, v.Url), page?.Title, v.CreatedUtc,
                v.IsStart ? "Yes" : new HtmlString(Form("start", Hidden("id", v.ViewId) + Button("Make it my start page", quiet: true))),
                new HtmlString((v.IsStart ? Form("nostart", Button("Start on the usual page", quiet: true)) : "") + Form("remove", Hidden("id", v.ViewId) + Button("Remove", $"Remove the view {v.Name}?", quiet: true)))]);
        }
        Panels.Add(new("Your views", t.Render(), "What a view shows follows your access at the time you open it, not when you saved it."));
    }

    public Task<IActionResult> OnPostSaveAsync(string? name, string? url) => RoleChangeAsync(null, async () =>
    {
        name = name?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > 100) throw new ArgumentException("give the view a name (up to 100 characters).");
        if (!SafeUrl(url)) throw new ArgumentException("only a report page on this site can be saved.");
        if (await Db.ScalarAsync<int>("SELECT COUNT(*) FROM dash.SavedView WHERE Owner = @o", new { o = Owner }) >= 50) throw new ArgumentException("you have 50 saved views; remove one first.");
        await Db.ExecuteAsync("INSERT INTO dash.SavedView (Owner, Name, Url) VALUES (@o, @name, @url);", new { o = Owner, name, url });
        return $"Saved \"{name}\". It is in My Copilot > Saved views.";
    });

    public Task<IActionResult> OnPostStartAsync(int id) => RoleChangeAsync(null, async () =>
        await Db.ExecuteAsync("UPDATE dash.SavedView SET IsStart = CASE WHEN ViewId = @id THEN 1 ELSE 0 END WHERE Owner = @o;", new { id, o = Owner }) > 0
            ? "Your start page is set." : "That view is gone.");

    public Task<IActionResult> OnPostNostartAsync() => RoleChangeAsync(null, async () =>
    {
        await Db.ExecuteAsync("UPDATE dash.SavedView SET IsStart = 0 WHERE Owner = @o;", new { o = Owner });
        return "You start on the usual page again.";
    });

    public Task<IActionResult> OnPostRemoveAsync(int id) => RoleChangeAsync(null, async () =>
        await Db.ExecuteAsync("DELETE FROM dash.SavedView WHERE ViewId = @id AND Owner = @o;", new { id, o = Owner }) > 0 ? "View removed." : "That view was already gone.");
}
