using Xunit;

namespace CopilotInsights.Tests;

/// <summary>2.7.6: what the client's first look at the release pages raised.</summary>
public class Release276Tests
{
    static string Root()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(d.FullName, "CHANGELOG.md"))) d = d.Parent!;
        return d.FullName;
    }

    static string Page(params string[] p) => File.ReadAllText(Path.Combine([Root(), "dashboard", "CopilotInsights", "Pages", .. p]));

    [Fact]
    public void Reservations_with_an_empty_register_say_so_first()
    {
        /* The client's register was empty: 17,886 holders under no listed leader, in red, read as a fault. */
        var page = Page("Leadership", "Reservations.cs");
        Assert.Contains("if (whole && !v.IsSnapshot && v.Orgs.Count == 0)", page);
        Assert.Contains("No organization is in the reservation register yet, so every Microsoft 365 Copilot holder shows under no listed leader.", page);
    }
}
