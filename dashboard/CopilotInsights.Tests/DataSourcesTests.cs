using CopilotInsights.Infrastructure;
using CopilotInsights.Pages;
using Xunit;

namespace CopilotInsights.Tests;

/// <summary>
/// The Data sources page (/sources): each source's state from its last load, the coverage line at the top, the reports
/// each source opens, and the page open to everyone the dashboard lets in.
/// </summary>
public class DataSourcesTests
{
    static string Repo(params string[] path)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine([d.FullName, .. path]))) return File.ReadAllText(Path.Combine([d.FullName, .. path]));
        throw new FileNotFoundException(string.Join('/', path));
    }

    static readonly DateTime Now = new(2026, 11, 25, 12, 0, 0, DateTimeKind.Utc);

    static DataSource Source(string key, SourceState state, string? name = null) =>
        new(key, name ?? key, "what", state, state == SourceState.NotConnected ? null : Now.AddHours(-1), null, null, "rows", null, [], "needs", true);

    [Fact]
    public void A_source_is_connected_late_failed_or_not_connected()
    {
        Assert.Equal(SourceState.NotConnected, DataSources.Classify(null, false, Now, 36));
        Assert.Equal(SourceState.Connected, DataSources.Classify(Now.AddHours(-35), false, Now, 36));
        Assert.Equal(SourceState.Behind, DataSources.Classify(Now.AddHours(-37), false, Now, 36));
        Assert.Equal(SourceState.Failed, DataSources.Classify(Now.AddHours(-1), true, Now, 36));
        /* A failure with no successful load before it still counts as connected: someone set it up. */
        Assert.Equal(SourceState.Failed, DataSources.Classify(null, true, Now, 36));
        Assert.True(DataSources.IsConnected(Source("x", SourceState.Failed)));
        Assert.False(DataSources.IsConnected(Source("x", SourceState.NotConnected)));
    }

    [Fact]
    public void The_coverage_line_counts_sources_and_names_what_is_missing()
    {
        var all = new[]
        {
            Source("audit", SourceState.Connected, "Microsoft Purview audit log"),
            Source("powerplatform", SourceState.Behind, "Power Platform usage"),
            Source("github", SourceState.NotConnected, "GitHub Copilot"),
            Source("foundry", SourceState.NotConnected, "Microsoft Foundry"),
        };
        Assert.Equal("2 of 4 sources connected. The AI spend covers Microsoft 365 Copilot, Copilot Studio, Power BI and Power Platform. Not connected: GitHub Copilot and Microsoft Foundry.",
            DataSources.Coverage(all));
        Assert.Equal("0 of 1 sources connected. No AI spend is covered yet. Not connected: Microsoft Purview audit log.",
            DataSources.Coverage([Source("audit", SourceState.NotConnected, "Microsoft Purview audit log")]));
        Assert.DoesNotContain("Not connected", DataSources.Coverage([Source("audit", SourceState.Connected), Source("github", SourceState.Connected), Source("foundry", SourceState.Failed)]));
    }

    [Fact]
    public void Every_report_a_source_opens_is_a_menu_page()
    {
        var pages = Nav.Groups.SelectMany(g => g.Pages).Select(p => p.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, path) in DataSources.OpenPaths)
            Assert.True(pages.Contains(path), $"{key} opens {path}, which no menu section lists");
        Assert.Equal(10, DataSources.OpenPaths.Select(o => o.Key).Distinct().Count());   // 3.4.0: the whole AI bill
    }

    [Fact]
    public void The_page_is_open_to_everyone_let_in_and_never_names_a_person()
    {
        Assert.Contains("@page \"/sources\"", Repo("dashboard", "CopilotInsights", "Pages", "Sources.cshtml"));
        /* In no menu section, so no grant is needed: like Success stories and My subscriptions. */
        Assert.Empty(Nav.SectionsOf("/sources"));
        var layout = Repo("dashboard", "CopilotInsights", "Pages", "Shared", "_Layout.cshtml");
        Assert.Contains("href=\"/sources\">Data sources</a>", layout);
        var code = Repo("dashboard", "CopilotInsights", "Infrastructure", "DataSources.cs");
        foreach (var personal in new[] { "DisplayName", "UserPrincipalName,", "SELECT Upn" })
            Assert.DoesNotContain(personal, code);
        /* The audit log is never counted: at 160 million rows that would make the page slow. */
        Assert.DoesNotContain("FROM audit.CopilotAudit", code);
    }

    [Fact]
    public void The_page_is_in_the_guides()
    {
        Assert.Contains("/sources", Repo("docs", "DASHBOARD.md"));
        Assert.Contains("Data sources", Repo("CHANGELOG.md"));
    }
}
