using CopilotInsights.Infrastructure;
using Xunit;

namespace CopilotInsights.Tests;

/// <summary>The start page's three questions and the header's tagline.</summary>
public class StartQuestionsTests
{
    static string Repo(params string[] path)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine([d.FullName, .. path]))) return File.ReadAllText(Path.Combine([d.FullName, .. path]));
        throw new FileNotFoundException(string.Join('/', path));
    }

    [Fact]
    public void Figures_are_written_as_the_pages_they_open_write_them()
    {
        Assert.Equal("48,213 USD", StartFigures.Money(48212.6m, "USD"));
        Assert.Equal("3.4×", StartFigures.Times(3.4166m));
        Assert.Equal("12×", StartFigures.Times(12.2m));
        Assert.Equal("1,204", StartFigures.Count(1204));
        /* Costs and savings writes its return the same way. */
        Assert.Contains("static string Times(decimal r) => r >= 10 ? $\"{r.ToString(\"#,0\", Inv)}×\" : $\"{r.ToString(\"0.0\", Inv)}×\";", Repo("dashboard", "CopilotInsights", "Pages", "Leadership", "Costs.cs"));
    }

    [Fact]
    public void Each_question_shows_only_where_its_pages_open()
    {
        var code = Repo("dashboard", "CopilotInsights", "Pages", "Index.cshtml.cs");
        Assert.Contains("Open(\"/leadership/costs\", null, keepOrg: false) is { } costsUrl", code);
        Assert.Contains("var lu = Open(\"/licensing/leavers\");", code);
        Assert.Contains("(await ops.LeaversAsync(Ctx.Root)).Count", code);
    }

    [Fact]
    public void The_tagline_is_a_setting_with_a_default()
    {
        Assert.Equal("The ledger of AI use, cost and control, kept in your own SQL Server", new InsightsOptions().Tagline);
        Assert.Contains("class=\"tagline\"", Repo("dashboard", "CopilotInsights", "Pages", "Shared", "_Layout.cshtml"));
    }
}
