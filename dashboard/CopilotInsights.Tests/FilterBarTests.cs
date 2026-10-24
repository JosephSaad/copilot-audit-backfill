using System.Text.RegularExpressions;
using Xunit;

namespace CopilotInsights.Tests;

/// <summary>
/// The filter bar shows only the controls that change a page: the period box where the period matters, the way
/// down the organization where the team does. A page that says a control does not matter must not use it.
/// </summary>
public class FilterBarTests
{
    static string PagesDirectory()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "CopilotInsights", "Pages", "InsightsPage.cs"))) return Path.Combine(d.FullName, "CopilotInsights", "Pages");
        throw new DirectoryNotFoundException("CopilotInsights/Pages, above " + AppContext.BaseDirectory);
    }

    static readonly Regex ClassStart = new(@"^[ \t]*(public|internal)[ \t]+((sealed|abstract)[ \t]+)?class[ \t]+(?<name>\w+)", RegexOptions.Multiline);

    /// <summary>Each public class in the pages' source: the file, its name and its text up to the next class.</summary>
    static List<(string File, string Class, string Text)> PageClasses()
    {
        var list = new List<(string, string, string)>();
        foreach (var f in Directory.GetFiles(PagesDirectory(), "*.cs", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(f);
            var found = ClassStart.Matches(text);
            for (var i = 0; i < found.Count; i++)
            {
                var end = i + 1 < found.Count ? found[i + 1].Index : text.Length;
                list.Add((Path.GetFileName(f), found[i].Groups["name"].Value, text[found[i].Index..end]));
            }
        }
        return list;
    }

    /// <summary>What a page does when the part of the organization chosen changes its figures.</summary>
    static readonly string[] TeamUse = ["Ctx.Scope(", "Ctx.PersonScope(", "Ctx.Root", "fnScopeUsers", "@root", "c.Scope(", "c.PersonScope("];

    /// <summary>What a page does when the period chosen changes its figures: its start, its length, its trend points.</summary>
    static readonly string[] PeriodUse = ["@k0", "@d0", "Ctx.Start", "Ctx.Days", "Ctx.StartKey", "Buckets()", "BucketSql(", "TrendNote", "Labels()", "TrendAsync(", "UnitSeriesAsync("];

    [Fact]
    public void The_pages_are_found()
    {
        var pages = PageClasses();
        Assert.True(pages.Count > 80, $"found {pages.Count} classes; the pattern has stopped matching them");
        Assert.Contains(pages, p => p.Class == "PoolModel" && p.Text.Contains("override bool UsesTeam => false"));
    }

    /// <summary>
    /// Pages that scope by the team only to decide who may be found, not to change a figure. Compare two teams offers the
    /// managers in the viewer's part, and each of its two teams is a scope of its own, chosen on the page.
    /// </summary>
    static readonly string[] ScopeAsGate = ["PersonModel", "CompareModel"];

    [Fact]
    public void A_page_the_team_does_not_change_does_not_scope_by_it()
    {
        var offenders = PageClasses().Where(p => p.Text.Contains("override bool UsesTeam => false") && !ScopeAsGate.Contains(p.Class))
            .SelectMany(p => TeamUse.Where(p.Text.Contains).Select(t => $"{p.File} {p.Class} uses {t}")).ToList();
        Assert.True(offenders.Count == 0, string.Join("; ", offenders));
    }

    [Fact]
    public void A_page_the_period_does_not_change_does_not_follow_it()
    {
        /* A page with a window of its own, or none, may name the end of the data (@k1, Ctx.End) but not the period's start,
           its length or its trend points: those are what the period box changes. */
        var offenders = PageClasses().Where(p => p.Text.Contains("override bool UsesPeriod => false") || p.Text.Contains("override string? OwnWindow =>"))
            .SelectMany(p => PeriodUse.Where(p.Text.Contains).Select(t => $"{p.File} {p.Class} uses {t}")).ToList();
        Assert.True(offenders.Count == 0, string.Join("; ", offenders));
    }

    [Fact]
    public void The_filter_bar_leaves_out_what_a_page_says_does_not_matter()
    {
        var bar = File.ReadAllText(Path.Combine(PagesDirectory(), "Shared", "_Report.cshtml"));
        Assert.Contains("Model.UsesTeam", bar);
        Assert.Contains("Model.UsesPeriod", bar);
        Assert.Contains("aria-label=\"Look inside a team\"", bar);
        /* The team box sits inside the UsesTeam branch, not outside it. */
        Assert.True(bar.IndexOf("if (Model.UsesTeam)", StringComparison.Ordinal) is var at and > 0
                    && bar.IndexOf("aria-label=\"Look inside a team\"", StringComparison.Ordinal) > at);
    }
}
