using CopilotInsights.Infrastructure;
using Microsoft.AspNetCore.Mvc;

namespace CopilotInsights.Pages;

/// <summary>
/// What an address with no page behind it is answered with (2.4.10): a mistyped address, or a bookmark to a page since
/// moved, got an empty response, which a browser shows as a blank page. Program.cs sends such a request here with the
/// address that was asked for; the page says so, and offers the reports nearest to it among those this person can open.
/// </summary>
public sealed class NotFoundModel(PageServices s) : InsightsPage(s)
{
    /// <summary>Where Program.cs leaves the address that was asked for (its path only: a query string can hold a name).</summary>
    public const string AskedKey = "Insights.NotFound.Asked";

    public override string Title => "Page not found";
    public override string Persona => "Copilot Insights";
    public override bool UsesFilters => false;
    public override bool OpenToAdmins => true;

    /// <summary>The path that had no page, or null when this page was opened by its own address.</summary>
    public string? Asked { get; private set; }

    protected override Task BuildAsync() => Task.CompletedTask;

    public override async Task<IActionResult> OnGetAsync()
    {
        Asked = HttpContext.Items[AskedKey] as string;
        /* Someone the dashboard is not open to is told that, as on every page (403); everyone else that nothing is here. */
        if (await PrepareAsync()) Response.StatusCode = 404;
        return Page();
    }
}
