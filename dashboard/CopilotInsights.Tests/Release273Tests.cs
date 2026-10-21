using Xunit;

namespace CopilotInsights.Tests;

/// <summary>2.7.3: an activity database no longer builds audit.CopilotInteraction, which nothing there reads.</summary>
public class Release273Tests
{
    static string Root()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(d.FullName, "CHANGELOG.md"))) d = d.Parent!;
        return d.FullName;
    }

    static string Read(params string[] p) => File.ReadAllText(Path.Combine([Root(), .. p]));

    [Fact]
    public void An_activity_database_skips_the_Copilot_shred_in_its_history_and_its_nightly_merge()
    {
        /* A 32-million-record activity history spent half a day parsing every record's JSON into a table nothing read. */
        var finalize = Read("sql", "Finalize-CopilotAudit.sql");
        Assert.Contains("DECLARE @activity BIT = CASE WHEN OBJECT_ID(N'audit.usp_ShredActivity', N'P') IS NULL THEN 0 ELSE 1 END;", finalize);
        Assert.Contains("IF @activity = 0\nINSERT INTO audit.CopilotInteraction WITH (TABLOCK)", finalize.Replace("\r\n", "\n"));
        Assert.Contains("CASE WHEN @activity = 1 THEN (SELECT COUNT(DISTINCT UserPrincipalName) FROM audit.CopilotAudit)", finalize);
        var merge = Read("sql", "Create-CopilotLoadProcedure.sql").Replace("\r\n", "\n");
        Assert.Contains("    IF OBJECT_ID(N'audit.usp_ShredActivity', N'P') IS NULL\n    INSERT INTO audit.CopilotInteraction", merge);
        Assert.Contains("an activity database: its own shred follows", Read("Invoke-CopilotAuditBackfill.ps1"));
    }

    [Fact]
    public void A_database_the_installer_creates_runs_at_the_servers_processors_less_two()
    {
        var install = Read("Install-CopilotAudit.ps1");
        Assert.Contains("[ValidateRange(0, 64)][int] $MaxDop = -1", install);
        Assert.Contains("$targets = @(if ($MaxDop -gt 0) { $dbs } else { $created })", install);   // existing databases keep theirs unless asked
        Assert.Contains("[Math]::Max(1, $cpus - 2)", install);
        Assert.Contains("ALTER DATABASE SCOPED CONFIGURATION SET MAXDOP = $dop;", install);
        Assert.Contains("$created += $db", install);
    }

    [Fact]
    public void Slow_pages_read_their_independent_sources_together()
    {
        var act = Read("dashboard", "CopilotInsights", "Pages", "Leadership", "ActOn.cs");
        Assert.Contains("await Task.WhenAll(new Task?[] { countsTask, nonHumanTask, unitsTask, targetsTask, costsTask, trendTask }.OfType<Task>());", act);
        var lead = Read("dashboard", "CopilotInsights", "Pages", "Leadership", "Leadership.cs");
        Assert.Contains("await Task.WhenAll(totalsTask, trendTask, adoptedTask, unitsTask);", lead);
        Assert.Contains("await Task.WhenAll(unitsTask, trendTask);", lead);
        /* The swap list's figures come from the same read as its pairs, not a second pass over both sides. */
        var swap = Read("dashboard", "CopilotInsights", "Pages", "Licensing", "Swap.cs");
        Assert.Contains("FROM g FULL JOIN t ON t.Unit = g.Unit AND t.N = g.N", swap);
        Assert.DoesNotContain("SELECT (SELECT COUNT(*) FROM g) AS Idle", swap);
    }

    [Fact]
    public void A_page_is_named_by_its_section_with_one_separator()
    {
        /* "Admin > Targets" and "Admin › Value names" both appeared on screen; the guides use ›, and so does the dashboard. */
        var rx = new System.Text.RegularExpressions.Regex(@"\b(Admin|People|Leadership|Licensing|Security|Adoption|Agents|Apps|Forecasting|Compliance|Governance|Enablement|Regions|Workforce|Operations|Start) > [A-Z]");
        var dir = Path.Combine(Root(), "dashboard", "CopilotInsights");
        var found = Directory.EnumerateFiles(dir, "*.*", SearchOption.AllDirectories)
            .Where(f => (f.EndsWith(".cs") || f.EndsWith(".cshtml") || f.EndsWith(".js")) && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .SelectMany(f => File.ReadLines(f).Select((l, i) => (f, i, l)))
            .Where(x => rx.IsMatch(x.l))
            .Select(x => $"{Path.GetFileName(x.f)}:{x.i + 1}").ToList();
        Assert.Empty(found);
    }

    [Fact]
    public void Nothing_in_the_activity_database_reads_the_Copilot_interaction_table()
    {
        foreach (var f in new[] { "Create-ActivityProcedure.sql", "Create-CopilotActivityRollup.sql", "Create-ActivitySchema.sql" })
            Assert.DoesNotContain("FROM audit.CopilotInteraction", Read("sql", f));
    }
}
