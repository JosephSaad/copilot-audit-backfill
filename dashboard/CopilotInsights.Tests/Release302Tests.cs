using System.Text.RegularExpressions;
using Xunit;

namespace CopilotInsights.Tests;

/// <summary>Since 3.0.1: scripts that survive a change of encoding, the Workbench's home address.</summary>
public class Release302Tests
{
    static string Root()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(d.FullName, "CHANGELOG.md"))) d = d.Parent!;
        return d.FullName;
    }

    static string Read(params string[] p) => File.ReadAllText(Path.Combine([Root(), .. p]));

    /// <summary>
    /// A copy of the source that reached a build server re-encoded turned a character in a regular expression of
    /// site.js into one the browser refused, and the whole script stopped: no menu folding, on every page. Outside
    /// comments and quoted text, the scripts are plain ASCII (write À, not the character).
    /// </summary>
    [Theory]
    [InlineData("site.js")]
    [InlineData("theme.js")]
    [InlineData("workbench.js")]
    public void Script_code_is_plain_ascii(string file)
    {
        var lines = Read("dashboard", "CopilotInsights", "wwwroot", file).Split('\n');
        var bad = new List<string>();
        for (var i = 0; i < lines.Length; i++)
        {
            var code = Regex.Replace(lines[i], @"/\*.*?\*/", "");
            var t = code.TrimStart();
            if (t.StartsWith("//") || t.StartsWith('*') || t.StartsWith("/*")) continue;
            code = Regex.Replace(code, @"'(?:[^'\\]|\\.)*'|""(?:[^""\\]|\\.)*""", "''");
            var at = code.IndexOf("//", StringComparison.Ordinal);
            if (at >= 0 && !code[..at].Contains('/')) code = code[..at];
            else if (at >= 0 && Regex.IsMatch(code[at..], @"^//\s")) code = code[..at];
            if (code.Any(c => c > 127)) bad.Add($"{file}:{i + 1}: {lines[i].Trim()}");
        }
        Assert.True(bad.Count == 0, "Non-ASCII outside comments and quoted text:\n" + string.Join("\n", bad));
    }

    [Fact]
    public void Workbench_on_its_own_opens_its_Start_page()
    {
        var program = Read("dashboard", "CopilotInsights", "Program.cs");
        Assert.Contains("app.MapGet(\"/workbench\", () => Results.Redirect(\"/workbench/Start\"));", program);
    }
}
