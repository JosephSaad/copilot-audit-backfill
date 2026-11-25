using CopilotInsights.Pages.Governance;
using Xunit;

namespace CopilotInsights.Tests;

/// <summary>AI apps beyond Copilot: the Apps named cell lists the apps by name, most calls first, and notes calls that name none.</summary>
public class AiAppsNamedTests
{
    [Fact]
    public void Apps_are_named_most_calls_first_with_their_calls_on_hover()
    {
        var cell = AiAppsModel.AppsNamed([("invoice-bot", 30), ("contoso-chat", 1200), ("Contoso-Chat", 5)]).Value!;
        Assert.True(cell.IndexOf("contoso-chat", StringComparison.Ordinal) < cell.IndexOf("invoice-bot", StringComparison.Ordinal));
        Assert.Contains("title=\"contoso-chat: 1,205 calls\"", cell);
        Assert.DoesNotContain("Contoso-Chat", cell);
        Assert.DoesNotContain("cellnote", cell);
    }

    [Fact]
    public void Calls_naming_no_app_are_noted_not_listed()
    {
        var cell = AiAppsModel.AppsNamed([("a", 10), ("", 7), ("b", 3), ("c", 2), ("d", 1)]).Value!;
        Assert.Contains("4 apps; 7 calls name no app", cell);
        Assert.Equal("<span class=\"apps-named\" data-v=\"0\">none named</span><span class=\"cellnote\">7 calls, none naming an app</span>",
            AiAppsModel.AppsNamed([("", 7)]).Value);
        Assert.Contains("&lt;b&gt;", AiAppsModel.AppsNamed([("<b>", 1)]).Value!);
    }
}
