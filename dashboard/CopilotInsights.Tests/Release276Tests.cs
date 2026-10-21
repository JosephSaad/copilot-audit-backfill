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
    public void The_license_event_operation_set_fits_where_it_is_recorded()
    {
        /* The client's nightly stopped at License events: 2.7.0's agent actions made the set 485 characters, into NVARCHAR(400). */
        var pipeline = File.ReadAllText(Path.Combine(Root(), "Invoke-CopilotPipeline.ps1"));
        var lists = pipeline[pipeline.IndexOf("$script:AgentOperations =", StringComparison.Ordinal)..pipeline.IndexOf("$script:ActivityRecordTypes", StringComparison.Ordinal)];
        var code = string.Join("\n", lists.Split('\n').Select(l => l.Split('#')[0]));   // the comments hold apostrophes
        var ops = System.Text.RegularExpressions.Regex.Matches(code, "'([^']+)'").Select(m => m.Groups[1].Value).ToList();
        Assert.Equal(25, ops.Count);   // 4 license operations, 14 of Copilot Studio's, 7 administrators' actions on agents
        Assert.Contains("Change user license", ops);
        Assert.True(string.Join(",", ops).Length <= 1000, "the operation set should stay well inside NVARCHAR(2000)");
        var audit = File.ReadAllText(Path.Combine(Root(), "sql", "Create-CopilotAuditSchema.sql"));
        var csv = File.ReadAllText(Path.Combine(Root(), "sql", "Create-CopilotCsvImport.sql"));
        Assert.DoesNotContain("Operations      NVARCHAR(400)", audit);
        Assert.DoesNotContain("Operations        NVARCHAR(400)", audit);
        Assert.DoesNotContain("Operations          NVARCHAR(400)", csv);
        Assert.Contains("IF COL_LENGTH(N'audit.CopilotBackfillRange', N'Operations') < 4000", audit);
        Assert.Contains("IF COL_LENGTH(N'audit.CopilotLoad', N'Operations') < 4000", audit);
        Assert.Contains("IF COL_LENGTH(N'audit.CopilotCsvImport', N'Operations') < 4000", csv);
    }

    [Fact]
    public void Reservations_with_an_empty_register_say_so_first()
    {
        /* The client's register was empty: 17,886 holders under no listed leader, in red, read as a fault. */
        var page = Page("Leadership", "Reservations.cs");
        Assert.Contains("if (whole && !v.IsSnapshot && v.Orgs.Count == 0)", page);
        Assert.Contains("No organization is in the reservation register yet, so every Microsoft 365 Copilot holder shows under no listed leader.", page);
    }
}
