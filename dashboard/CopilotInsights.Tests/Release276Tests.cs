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

    /* Several web servers behind a load balancer. */

    public sealed record Plain(string Name, int People, double? Share, DateTime When, List<int> Days);
    public sealed record WithText(string Name, Microsoft.AspNetCore.Html.HtmlString Html);
    public sealed class WithPrivate { int _hidden; public int Shown { get; set; } public void Set(int v) => _hidden = v; public int Hidden => _hidden; }

    [Fact]
    public void Only_results_JSON_carries_whole_are_shared_between_servers()
    {
        Assert.True(CopilotInsights.Infrastructure.SharedCache.IsShareable(typeof(List<Plain>)));
        Assert.True(CopilotInsights.Infrastructure.SharedCache.IsShareable(typeof(List<(string Name, long N)>)));
        Assert.True(CopilotInsights.Infrastructure.SharedCache.IsShareable(typeof(int?)));
        Assert.False(CopilotInsights.Infrastructure.SharedCache.IsShareable(typeof(object)));
        Assert.False(CopilotInsights.Infrastructure.SharedCache.IsShareable(typeof(List<dynamic>)));
        Assert.False(CopilotInsights.Infrastructure.SharedCache.IsShareable(typeof(WithText)));
        Assert.False(CopilotInsights.Infrastructure.SharedCache.IsShareable(typeof(WithPrivate)));
        /* A dictionary or a set made to ignore case would come back matching case. */
        Assert.False(CopilotInsights.Infrastructure.SharedCache.IsShareable(typeof(Dictionary<string, int>)));
        Assert.False(CopilotInsights.Infrastructure.SharedCache.IsShareable(typeof(HashSet<string>)));
    }

    [Fact]
    public void The_shared_key_changes_with_the_data_and_not_with_the_server()
    {
        static string K(string stamp, string? live) => Convert.ToHexString(CopilotInsights.Infrastructure.SharedCache.Key("t", stamp, "q", typeof(int), "SELECT 1", live));
        Assert.Equal(K("2026-10-22|5", "dash.Setting=3"), K("2026-10-22|5", "dash.Setting=3"));
        Assert.NotEqual(K("2026-10-22|5", null), K("2026-10-23|5", null));
        Assert.NotEqual(K("2026-10-22|5", "dash.Setting=3"), K("2026-10-22|5", "dash.Setting=4"));
        /* An admin change is counted in dash.CacheGeneration, which every server reads in its stamp. */
        Assert.Contains("UPDATE dash.CacheGeneration", File.ReadAllText(Path.Combine(Root(), "dashboard", "CopilotInsights", "Infrastructure", "Db.cs")));
        Assert.Contains("dash.CacheGeneration", File.ReadAllText(Path.Combine(Root(), "dashboard", "CopilotInsights", "Infrastructure", "DataStamp.cs")));
        var ops = File.ReadAllText(Path.Combine(Root(), "sql", "Create-CopilotOperationsSchema.sql"));
        Assert.Contains("CREATE TABLE dash.QueryCacheShared", ops);
        Assert.Contains("CREATE TABLE dash.CacheGeneration", ops);
        /* One server builds the pages each morning; the others wait for it and read what it kept. */
        Assert.Contains("CopilotInsights.WarmPages", File.ReadAllText(Path.Combine(Root(), "dashboard", "CopilotInsights", "Infrastructure", "PageWarmer.cs")));
    }

    [Fact]
    public void Under_a_virtual_directory_every_root_address_is_given_the_base()
    {
        const string html = "<a href=\"/licensing/non-person?days=30\">x</a><form action=\"/admin/settings\"><button formaction=\"/admin/health\"></button></form>"
            + "<script src=\"/js/site.js\"></script><input data-lookup=\"/api/people\"><a href=\"//cdn.example/x\">y</a><a href=\"https://x/\">z</a><a href=\"/CopilotDashboard/agents/inventory\">kept</a>";
        var r = CopilotInsights.Infrastructure.PathBaseLinks.Rewrite(html, "/CopilotDashboard");
        Assert.Contains("href=\"/CopilotDashboard/licensing/non-person?days=30\"", r);
        Assert.Contains("action=\"/CopilotDashboard/admin/settings\"", r);
        Assert.Contains("formaction=\"/CopilotDashboard/admin/health\"", r);
        Assert.Contains("src=\"/CopilotDashboard/js/site.js\"", r);
        Assert.Contains("data-lookup=\"/CopilotDashboard/api/people\"", r);
        Assert.Contains("href=\"//cdn.example/x\"", r);
        Assert.Contains("href=\"https://x/\"", r);
        Assert.Contains("href=\"/CopilotDashboard/agents/inventory\"", r);
        Assert.DoesNotContain("/CopilotDashboard/CopilotDashboard", r);
        Assert.Equal(html, CopilotInsights.Infrastructure.PathBaseLinks.Rewrite(html, ""));
        Assert.Equal("/CopilotDashboard/admin/health", CopilotInsights.Infrastructure.PathBaseLinks.Location("/admin/health", "/CopilotDashboard"));
        Assert.Equal("/CopilotDashboard/x", CopilotInsights.Infrastructure.PathBaseLinks.Location("/CopilotDashboard/x", "/CopilotDashboard"));
        Assert.Equal("https://elsewhere/", CopilotInsights.Infrastructure.PathBaseLinks.Location("https://elsewhere/", "/CopilotDashboard"));
        Assert.Contains("-ApplicationPath", File.ReadAllText(Path.Combine(Root(), "dashboard", "Install-CopilotInsights.ps1")));
    }

    [Fact]
    public void A_load_balancer_probe_answers_without_sign_in_and_names_nothing()
    {
        var program = File.ReadAllText(Path.Combine(Root(), "dashboard", "CopilotInsights", "Program.cs"));
        Assert.Contains("app.MapMethods(HealthProbe.Path, [\"GET\", \"HEAD\"], HealthProbe.AnswerAsync).AllowAnonymous();", program);
        var probe = File.ReadAllText(Path.Combine(Root(), "dashboard", "CopilotInsights", "Infrastructure", "HealthProbe.cs"));
        Assert.Contains("Results.Text(\"ok\"", probe);
        Assert.Contains("Results.Text(\"unavailable\"", probe);
        Assert.DoesNotContain("ex.Message", probe);
        Assert.Contains("\"$location/healthz\"", File.ReadAllText(Path.Combine(Root(), "dashboard", "Install-CopilotInsights.ps1")));
    }

    [Fact]
    public void A_failure_shows_a_reference_and_never_the_error()
    {
        var s = CopilotInsights.Infrastructure.Incident.Sentence("Not changed: it failed", "A1B2C3");
        Assert.Equal("Not changed: it failed. Reference A1B2C3: an administrator finds the details under it in the web server's log.", s);
        Assert.Matches("^[0-9A-F]{6}$", CopilotInsights.Infrastructure.Incident.NewReference());
        /* No page writes an exception's own text where a person reads it: only the dashboard's own refusals (an
           ArgumentException, which says what to correct) are shown as they are. */
        var pages = Directory.GetFiles(Path.Combine(Root(), "dashboard", "CopilotInsights", "Pages"), "*.cs", SearchOption.AllDirectories);
        foreach (var f in pages)
        {
            var lines = File.ReadAllLines(f);
            for (var i = 0; i < lines.Length; i++)
                if (lines[i].Contains("ex.Message", StringComparison.Ordinal)
                    && !lines[Math.Max(0, i - 3)..(i + 1)].Any(l => l.Contains("ArgumentException", StringComparison.Ordinal)))
                    Assert.Fail($"{Path.GetFileName(f)}:{i + 1}: {lines[i].Trim()}");
        }
    }

    [Fact]
    public void An_audit_row_holds_what_was_submitted_and_what_changed_with_secrets_masked()
    {
        var submitted = CopilotInsights.Infrastructure.AuditChange.Submitted([("__RequestVerificationToken", "abc"), ("SmtpPassword", "p@ss"), ("MinGroupSize", "5"), ("TeamsWebhook", "")]);
        Assert.False(submitted.ContainsKey("__RequestVerificationToken"));
        Assert.Equal("(set)", submitted["SmtpPassword"]);
        Assert.Equal("", submitted["TeamsWebhook"]);
        Assert.Equal("5", submitted["MinGroupSize"]);
        var before = new CopilotInsights.Infrastructure.AuditChange.Snapshot(new() { ["MinGroupSize"] = "3", ["SmtpPassword"] = "old" }, ["CONTOSO\\amy Admin"]);
        var after = new CopilotInsights.Infrastructure.AuditChange.Snapshot(new() { ["MinGroupSize"] = "5", ["SmtpPassword"] = "new" }, ["CONTOSO\\bob Admin (/admin/health)"]);
        var json = CopilotInsights.Infrastructure.AuditChange.Json(submitted, before, after)!;
        Assert.Contains("\"MinGroupSize\":{\"before\":\"3\",\"after\":\"5\"}", json);
        Assert.Contains("\"SmtpPassword\":{\"before\":\"(hidden)\",\"after\":\"(hidden)\"}", json);
        Assert.DoesNotContain("p@ss", json);
        Assert.DoesNotContain("\"old\"", json);
        Assert.Contains("\"granted\":[\"CONTOSO\\\\bob Admin (/admin/health)\"]", json);
        Assert.Contains("\"revoked\":[\"CONTOSO\\\\amy Admin\"]", json);
        Assert.Null(CopilotInsights.Infrastructure.AuditChange.Json(new SortedDictionary<string, string>(), null, null));
        /* Shown on Admin › Audit log as lines, not JSON. */
        var text = CopilotInsights.Pages.Admin.AuditModel.AuditChangeText(json);
        Assert.Contains("Settings changed:\n  MinGroupSize: 3 → 5", text);
        Assert.Contains("Roles granted:\n  CONTOSO\\bob Admin (/admin/health)", text);
        Assert.Contains("Roles taken away:\n  CONTOSO\\amy Admin", text);
        Assert.Contains("Submitted on the form:\n  MinGroupSize: 5", text);
        Assert.Contains("TeamsWebhook: (empty)", text);
        Assert.DoesNotContain("{", text);
        /* The change and its audit row commit together. */
        var page = File.ReadAllText(Path.Combine(Root(), "dashboard", "CopilotInsights", "Pages", "InsightsPage.cs"));
        Assert.Contains("new System.Transactions.TransactionScope(", page);
        Assert.Contains("scope.Complete();", page);
        Assert.Contains("IF COL_LENGTH(N'dash.AuditLog', N'Change') IS NULL", File.ReadAllText(Path.Combine(Root(), "sql", "Create-CopilotDashboardSchema.sql")));
    }

    public sealed class WithHiddenProperty { public int Shown { get; set; } int Hidden { get; set; } public int Twice => Hidden * 2; }

    [Fact]
    public void Review_findings_stay_fixed()
    {
        /* A webhook's address carries its signature: kept without it, whatever the field is named. */
        Assert.Equal("https://prod.logic.azure.com/workflows/abc/triggers/manual/paths/invoke?(hidden)",
            CopilotInsights.Infrastructure.AuditChange.Masked("url", "https://prod.logic.azure.com/workflows/abc/triggers/manual/paths/invoke?api-version=2016&sig=SECRET"));
        Assert.Equal("https://insights.contoso.com/", CopilotInsights.Infrastructure.AuditChange.Masked("DashboardUrl", "https://insights.contoso.com/"));
        Assert.Equal("(set)", CopilotInsights.Infrastructure.AuditChange.Masked("ClientSecret", "x"));
        Assert.False(CopilotInsights.Infrastructure.AuditChange.Secret("TokensPerTurn"));
        Assert.False(CopilotInsights.Infrastructure.AuditChange.Secret("key"));
        Assert.True(CopilotInsights.Infrastructure.AuditChange.Secret("ExoAccessToken"));
        /* A member JSON skips is not shared. */
        Assert.False(CopilotInsights.Infrastructure.SharedCache.IsShareable(typeof(WithHiddenProperty)));
        /* A virtual directory named as one of the dashboard's own addresses. */
        Assert.True(CopilotInsights.Infrastructure.PathBaseLinks.Clashes("/agents"));
        Assert.True(CopilotInsights.Infrastructure.PathBaseLinks.Clashes("/Admin"));
        Assert.False(CopilotInsights.Infrastructure.PathBaseLinks.Clashes("/CopilotDashboard"));
        /* Long or outward-reaching admin actions are not held in a transaction. */
        var ops = File.ReadAllText(Path.Combine(Root(), "dashboard", "CopilotInsights", "Pages", "Admin", "Operations.cs"));
        Assert.Contains("OnPostRunAsync() => AdminChangeAsync(false,", ops);
        Assert.Contains("OnPostRunallAsync() => AdminChangeAsync(false,", ops);
        Assert.Contains("OnPostWebhookAsync(string? url, string? AlertWebhookFormat, string? clear) => AdminChangeAsync(async", ops);
        Assert.Contains("OnPostRunAsync(int id) => AdminChangeAsync(false,", File.ReadAllText(Path.Combine(Root(), "dashboard", "CopilotInsights", "Pages", "Admin", "Directory.cs")));
        /* The probe is not sent to HTTPS, and answers HEAD. */
        var program = File.ReadAllText(Path.Combine(Root(), "dashboard", "CopilotInsights", "Program.cs"));
        Assert.Contains("app.UseWhen(ctx => !ctx.Request.Path.StartsWithSegments(HealthProbe.Path), b => b.UseHttpsRedirection());", program);
        Assert.Contains("[\"GET\", \"HEAD\"]", program);
        /* The installer refuses a pool already in use and a folder of something else. */
        var installer = File.ReadAllText(Path.Combine(Root(), "dashboard", "Install-CopilotInsights.ps1"));
        Assert.Contains("already runs $($others -join ', ')", installer);
        Assert.Contains("holds files that are not the dashboard's", installer);
    }

    [Fact]
    public void A_report_turned_off_before_the_merge_stays_off()
    {
        var choices = CopilotInsights.Pages.HiddenReports.Choices.SelectMany(g => g.Pages.Select(p => (g.Persona, p.Path))).ToList();
        /* The non-human page is turned off section by section, by the addresses its copies had. */
        Assert.Contains(("Licensing", "/licensing/non-person"), choices);
        Assert.Contains(("Security", "/security/non-human"), choices);
        Assert.Contains(("Compliance", "/compliance/non-human"), choices);
        /* The pages folded into others are parts that can still be turned off. */
        foreach (var old in new[] { "/leadership/scorecard", "/adoption/leaderboard", "/agents/shared", "/agents/production" })
            Assert.Contains(choices, c => c.Path == old);
        Assert.Equal(choices.Count, choices.Distinct().Count());
        /* Every copy off: off. Only Security's off: still open through Licensing. */
        var all = new HashSet<string>(["/licensing/non-person", "/security/non-human", "/compliance/non-human"]);
        Assert.True(CopilotInsights.Pages.HiddenReports.IsOff(all, "/licensing/non-person", null));
        Assert.False(CopilotInsights.Pages.HiddenReports.IsOff(new HashSet<string>(["/security/non-human"]), "/licensing/non-person", null));
        var section = CopilotInsights.Pages.Nav.Groups.First(g => g.Persona == "Security");
        Assert.Equal("/security/non-human", CopilotInsights.Pages.Nav.Key(section, section.Pages.First(p => p.Path == CopilotInsights.Pages.Nav.NonHuman)));
        /* The folded parts read their old switches. */
        Assert.Contains("HiddenReports.Is(HiddenReports, \"/leadership/scorecard\")", Page("Leadership", "BoardPack.cs"));
        Assert.Contains("HiddenReports.Is(HiddenReports, \"/adoption/leaderboard\")", Page("Adoption", "Adoption.cs"));
        Assert.Contains("HiddenReports.Is(HiddenReports, \"/agents/\" + view)", Page("OperationsReports.cs"));
        Assert.Contains("HiddenReports.Is(HiddenReports, r == Roles.Licensing ? Nav.NonHuman", Page("Licensing", "NonPerson.cs"));
    }

    [Fact]
    public void Merged_pages_keep_their_old_addresses_and_training_stays()
    {
        var program = File.ReadAllText(Path.Combine(Root(), "dashboard", "CopilotInsights", "Program.cs"));
        foreach (var old in new[] { "/security/non-human", "/compliance/non-human", "/leadership/scorecard", "/adoption/leaderboard", "/agents/shared", "/agents/production" })
            Assert.Contains($"app.MapGet(\"{old}\"", program);
        Assert.False(File.Exists(Path.Combine(Root(), "dashboard", "CopilotInsights", "Pages", "Leadership", "Scorecard.cshtml")));
        Assert.False(File.Exists(Path.Combine(Root(), "dashboard", "CopilotInsights", "Pages", "Adoption", "Leaderboard.cshtml")));
        /* The training pages are kept. */
        Assert.True(File.Exists(Path.Combine(Root(), "dashboard", "CopilotInsights", "Pages", "Enablement", "Training.cshtml")));
        Assert.True(File.Exists(Path.Combine(Root(), "dashboard", "CopilotInsights", "Pages", "Admin", "Training.cshtml")));
        /* A person's record charts their use over the window chosen, 7 days to a year. */
        var people = Page("PeopleReports.cs");
        Assert.Contains("Use over time", people);
        Assert.Contains("ReportContext.DayChoices", people);
    }
}
