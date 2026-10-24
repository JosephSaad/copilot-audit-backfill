using CopilotInsights.Data;
using CopilotInsights.Infrastructure;
using CopilotInsights.Pages;
using CopilotInsights.Tests.Review2412A;
using Xunit;

namespace CopilotInsights.Tests;

/// <summary>
/// What 2.5.1 changed, held where a later change would undo it unseen: the trend charts show each day or week as it
/// was, without 2.5.0's 28-day moving average, and Adoption rate stays the one rolling figure.
/// </summary>
public class Release251Tests
{
    static string PagesDirectory()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "CopilotInsights", "Pages", "InsightsPage.cs"))) return Path.Combine(d.FullName, "CopilotInsights", "Pages");
        throw new DirectoryNotFoundException("CopilotInsights/Pages, above " + AppContext.BaseDirectory);
    }

    [Fact]
    public void No_report_page_draws_a_moving_average()
    {
        Assert.Null(typeof(Metrics).Assembly.GetType("CopilotInsights.Data.TrendAverage"));
        Assert.Null(typeof(Panel).GetProperty("Averaged"));
        Assert.Null(typeof(ReportContext).GetProperty("TrendStartKey"));
        Assert.DoesNotContain("28-day average", Source.Read("Pages", "Shared", "_Report.cshtml"));
        Assert.DoesNotContain("kt0", Source.Read("Infrastructure", "ReportContext.cs"));
        /* Admin › Operations sets the security-signal alerts, which compare a day with its 28-day average: an
           alert rule, not a chart. Every report page is checked. */
        var offenders = Directory.GetFiles(PagesDirectory(), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "Admin" + Path.DirectorySeparatorChar))
            .Where(f => File.ReadAllText(f) is var text && (text.Contains("28-day average") || text.Contains("moving average") || text.Contains("@kt0")))
            .Select(Path.GetFileName).ToList();
        Assert.True(offenders.Count == 0, "averaging again: " + string.Join(", ", offenders));
    }

    [Fact]
    public void Adoption_rate_stays_a_rolling_28_days()
    {
        Assert.Equal(28, Metrics.AdoptionDays);
        var leadership = Source.Read("Pages", "Leadership", "Leadership.cs");
        Assert.Contains("var adoptedTask = m.AdoptedAsync(Ctx);", leadership);   // Read alongside the others
        Assert.Contains("Ratio(adopted.GetValueOrDefault(w.Bucket), w.Licensed)", leadership);
        Assert.Contains("A rolling {Metrics.AdoptionDays} days, so weekends and holidays do not dip it", leadership);
    }

    [Fact]
    public void A_trend_point_is_its_own_day_or_week_and_a_count_is_written_whole()
    {
        Assert.Contains("protected string TrendNote => Ctx.Daily ? \"Each point is a day.\"", Source.Read("Pages", "InsightsPage.cs"));
        Assert.DoesNotContain("Average", Enum.GetNames<CopilotInsights.Charts.Fmt>());
    }
}
