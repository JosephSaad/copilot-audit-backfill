using CopilotInsights.Infrastructure;
using Microsoft.AspNetCore.Html;
using SeatHolderRow = Governance.Application.Queries.SeatHolderRow;
using SeatLobRow = Governance.Application.Queries.SeatLobRow;
using WorkbenchSeats = Governance.Web.Infrastructure.WorkbenchSeats;

namespace CopilotInsights.Pages.Licensing;

/// <summary>
/// The page with the Workbench on: the people holding a Copilot seat in one line of business's tracking group, or the
/// seats no line of business claims, from the Workbench's read-back. For the Licensing role, as every list of people,
/// and within the viewer's part of the organization.
/// </summary>
public sealed partial class ReservationHoldersModel
{
    /// <summary>The seats no line of business's tracking group claims.</summary>
    public const string NoLob = "~nolob";

    /// <summary>The most people drawn on the page; the export has them all.</summary>
    const int Shown = 1000;

    async Task LineOfBusinessAsync()
    {
        if (Warming) return;
        var res = Request.Query["res"].FirstOrDefault() ?? "";
        string name;
        string? figure;
        List<SeatHolderRow> people;
        try
        {
            /* Every line of business: the page is for the Licensing role. */
            var q = _seats.Queries(true)!;
            var links = WorkbenchSeats.Links(await _seats.PersonAsync(HttpContext), true);
            if (res == NoLob)
            {
                name = "No line of business";
                people = (await q.UnattributedAsync()).ToList();
                figure = links.NoLob;
            }
            else if (Guid.TryParse(res, out var id) && await q.LobAsync(id) is { } d)
            {
                name = d.LobName;
                people = d.Holders.Where(h => h.HasSeat).ToList();
                figure = links.Lob(d.Position);
            }
            else
            {
                Choose(await q.LobsAsync());
                return;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Notice = Failure(ex, "reading the Workbench's Copilot seats");
            return;
        }

        /* Within the viewer's part of the organization, as every list of people is. */
        if (Ctx.Root is not null)
        {
            var json = System.Text.Json.JsonSerializer.Serialize(people.Select(p => p.UserPrincipalName).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase));
            var inside = (await Db.QueryAsync<string>($"SELECT j.Upn FROM OPENJSON(@json) WITH (Upn NVARCHAR(256) '$') AS j {Ctx.PersonScope("j.Upn")};", Ctx.Args(new { json })))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            people = people.Where(p => p.UserPrincipalName is { } u && inside.Contains(u)).ToList();
            figure = null;   // fewer than the Workbench page counts
        }
        people = people.OrderBy(p => p.DisplayName ?? p.UserPrincipalName, StringComparer.CurrentCultureIgnoreCase).ToList();

        Table Rows(IEnumerable<SeatHolderRow> rows)
        {
            var t = new Table
            {
                Headers = ["Name", "Sign-in", "How assigned", "License state", "Last Copilot use", "Account"],
                EmptyText = "Nobody holds a Copilot seat here today.",
            };
            foreach (var p in rows)
                t.Rows.Add([p.DisplayName ?? "(not in the Workbench's directory)", p.UserPrincipalName, p.Direct ? "Directly" : "License group",
                    p.Error is null ? p.State : $"{p.State} ({p.Error})", p.LastActivityDate is { } a ? a.Date : "None reported", p.AccountEnabled ? "Enabled" : "Disabled"]);
            return t;
        }
        CsvTable = Rows(people);
        var shown = Rows(people.Take(Shown));
        Kpis.Add(new(name, Num(people.Count), "Copilot add-on seats held, from the Workbench's license read-back", Href: figure));
        var back = Open("/leadership/reservations");
        Panels.Add(new("People", new HtmlString(shown.Render().Value +
            (people.Count > Shown ? $"<p class=\"sub\">The first {Shown:#,0} of {people.Count:#,0} by name; the export has them all.</p>" : "") +
            (back is null ? "" : $"<p class=\"sub\">Every line of business's figures are on <a href=\"{E(back)}\">Leadership › Copilot reservations</a>.</p>")),
            res == NoLob
                ? "People holding the Copilot add-on who are in no line of business's tracking group: nobody is charged for them until they are."
                : "The line of business's tracking group members who hold the Copilot add-on: the seats its assigned figure counts. Current operational state, not certified.",
            "full", CsvUrl()));
    }

    /* Opened from the menu: the lines of business to choose from. */
    void Choose(IEnumerable<SeatLobRow> lobs)
    {
        string Pick(string key, string text) => $"<li><a href=\"{E(Ctx.Url(Request.Path, extra: "res=" + Uri.EscapeDataString(key)))}\">{E(text)}</a></li>";
        var list = string.Concat(lobs.Where(l => l.PublicId is not null).Select(l => Pick(l.PublicId!.Value.ToString(), l.Name))) + Pick(NoLob, "No line of business");
        var back = Open("/leadership/reservations");
        var from = back is null ? "" : $"<p class=\"sub\">The lines of business and their figures are on <a href=\"{E(back)}\">Leadership › Copilot reservations</a>.</p>";
        Panels.Add(new("Choose a line of business", new HtmlString($"<ul>{list}</ul>{from}"),
            "Each line of business's people holding the Microsoft 365 Copilot add-on, as its tracking group counts them."));
    }
}
