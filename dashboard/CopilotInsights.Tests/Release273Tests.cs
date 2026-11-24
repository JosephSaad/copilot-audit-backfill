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
        Assert.Contains("await Task.WhenAll(new Task?[] { countsTask, nonHumanTask, unitsTask, targetsTask, costsTask, signalsTask }.OfType<Task>());", act);
        var lead = Read("dashboard", "CopilotInsights", "Pages", "Leadership", "Leadership.cs");
        Assert.Contains("await Task.WhenAll(new Task?[] { totalsTask, trendTask, adoptedTask, unitsTask }.OfType<Task>());", lead);
        Assert.Contains("await Task.WhenAll(unitsTask, trendTask);", lead);
        /* The swap list's figures come from the same read as its pairs, not a second pass over both sides. */
        var swap = Read("dashboard", "CopilotInsights", "Pages", "Licensing", "Swap.cs");
        Assert.Contains("FROM g FULL JOIN t ON t.Unit = g.Unit AND t.N = g.N", swap);
        Assert.DoesNotContain("SELECT (SELECT COUNT(*) FROM g) AS Idle", swap);
    }

    [Fact]
    public void A_linked_figure_is_counted_as_the_page_it_opens_counts_it()
    {
        /* Adoption's link went from a share that started to the list of those who had not; it is now the list's own count. */
        var adoption = Read("dashboard", "CopilotInsights", "Pages", "Adoption", "Adoption.cs");
        Assert.Contains("Db.ScalarAsync<int>($\"SELECT COUNT(*) {NotStartedModel.Who(Ctx)};\", Ctx.Args())", adoption);
        Assert.Contains("{Who(Ctx)}", adoption);
        /* Sensitive content's people are Access to labeled content's: labeled content, or blocked from it. */
        var compliance = Read("dashboard", "CopilotInsights", "Pages", "Compliance", "Compliance.cs");
        Assert.Contains("WHERE rd.Kind IN ('Label', 'Blocked') AND rd.DateKey BETWEEN @k0 AND @k1", compliance);
        Assert.Contains("Href: Open(\"/compliance/access\")", compliance);
        /* Overview's Interactions opens Apps and agents, which now leads with the same figure. */
        var lead = Read("dashboard", "CopilotInsights", "Pages", "Leadership", "Leadership.cs");
        Assert.Contains("Kpis.Add(new(\"Interactions\", Num(t.Interactions), t.Active > 0 ? $\"{t.Interactions / (double)t.Active:#,0.0} per person using Copilot; includes use by accounts since disabled\" : null));", lead);
        /* Security's two counts open the flags of their own kind, whose figure is that count, not all three signals'. */
        var security = Read("dashboard", "CopilotInsights", "Pages", "Security", "Security.cs");
        Assert.Contains("Href: Open(\"/security/flags\", extra: \"kind=jailbreak&status=any\")", security);
        Assert.Contains("Href: Open(\"/security/flags\", extra: \"kind=injection&status=any\")", security);
        /* Agents costing credits totals from people, as Credit use does: by agent it was a few credits apart at 90 days. */
        Assert.Contains("var month = v.People.Aggregate(", Read("dashboard", "CopilotInsights", "Pages", "Agents", "Credits.cs"));
        /* The check that found these runs against a live dashboard. */
        Assert.True(File.Exists(Path.Combine(Root(), "tools", "dev", "ui", "Test-FigureLinks.ps1")));
    }

    [Fact]
    public void Chargeback_shows_the_licenses_it_charges_to_no_group()
    {
        /* Its groups are enabled accounts found in the directory: a license on a disabled or missing one was paid and lost. */
        var page = Read("dashboard", "CopilotInsights", "Pages", "Licensing", "Operations.cs");
        Assert.Contains("var unallocated = Math.Max(0, await heldTask - licensed);", page);
        Assert.Contains("Kpis.Add(new(\"Charged to no group\", Num(unallocated),", page);
        Assert.Contains("Href: Open(\"/licensing/leavers\")", page);
    }

    [Fact]
    public void Every_headline_figure_says_what_it_counts()
    {
        /* 41 figures carried a label and a value and nothing else: no note on the card or behind its information icon. */
        var dir = Path.Combine(Root(), "dashboard", "CopilotInsights", "Pages");
        var bare = new List<string>();
        foreach (var f in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
        {
            var lines = File.ReadAllLines(f);
            for (var i = 0; i < lines.Length; i++)
            {
                var at = lines[i].IndexOf("Kpis.Add(new(\"", StringComparison.Ordinal);
                if (at < 0 || !lines[i].TrimEnd().EndsWith("));", StringComparison.Ordinal)) continue;
                /* Count the arguments at the top level of new(...): a label and a value alone is two. */
                var s = lines[i][(at + "Kpis.Add(new(".Length)..];
                int depth = 0, args = 1; var inString = false;
                for (var k = 0; k < s.Length; k++)
                {
                    var c = s[k];
                    if (c == '"' && (k == 0 || s[k - 1] != '\\')) { inString = !inString; continue; }
                    if (inString) continue;
                    if (c is '(' or '[' or '{') depth++;
                    else if (c is ')' or ']' or '}') { if (depth == 0) break; depth--; }
                    else if (c == ',' && depth == 0) args++;
                }
                if (args < 3) bare.Add($"{Path.GetFileName(f)}:{i + 1}");
            }
        }
        Assert.Empty(bare);
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
