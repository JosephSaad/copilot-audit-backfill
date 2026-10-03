using System.Text.RegularExpressions;
using CopilotInsights.Infrastructure;
using CopilotInsights.Pages;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Html;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CopilotInsights.Tests;

/// <summary>The 2.4.12 review's findings for exports, the audit log, the query cache and web farms.</summary>
public class Review2412CTests
{
    static string Source(params string[] path)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "CopilotInsights", "Pages", "InsightsPage.cs"))) return File.ReadAllText(Path.Combine([d.FullName, .. path]));
        throw new DirectoryNotFoundException("CopilotInsights/Pages, above " + AppContext.BaseDirectory);
    }

    static string[] Lines(string csv) => csv.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

    /* ---- #18: an HTML cell exports its text --------------------------------------------------------------- */

    [Fact]
    public void An_html_cell_exports_what_it_says_and_a_control_exports_nothing()
    {
        var t = new Table { Headers = ["Signals", "Agent", "Days to renewal", "Triage"] };
        t.Rows.Add([new HtmlString("<span class=\"flag\">Jailbreak, Injection</span>"),
                    new HtmlString("<a href=\"/agents/agent?id=1\">Sales &amp; Service</a>"),
                    new HtmlString("<span class=\"status-warn\">70</span>"),
                    new HtmlString("<form method=\"post\"><button>Save</button></form>")]);
        t.Rows.Add([new HtmlString("Name<span class=\"cellnote\">what it is</span>"), null, new HtmlString("<span>=1+1</span>"), null]);
        var lines = Lines(t.ToCsv());
        Assert.Equal("\"Jailbreak, Injection\",\"Sales & Service\",\"70\",\"\"", lines[1]);
        // Text from HTML is text: spaces where tags were, and the formula guard still applies.
        Assert.Equal("\"Name what it is\",\"\",\"'=1+1\",\"\"", lines[2]);
    }

    /* ---- #19: numbers are numbers --------------------------------------------------------------------------- */

    [Fact]
    public void A_negative_number_is_exported_as_a_number_and_text_keeps_the_guard()
    {
        var t = new Table { Headers = ["Against the license (USD)", "Effect (days)", "Change", "Text"] };
        t.Rows.Add([-28.68m, -1.5, -2, "-2"]);
        t.Rows.Add([12.5m, 3.0, 7L, "+1"]);
        var lines = Lines(t.ToCsv());
        Assert.Equal("\"-28.68\",\"-1.5\",\"-2\",\"'-2\"", lines[1]);
        Assert.Equal("\"12.5\",\"3\",\"7\",\"'+1\"", lines[2]);
    }

    [Fact]
    public void A_scheduled_extract_writes_negative_numbers_as_numbers_too()
    {
        var csv = ExtractData.ToCsv([
            new Dictionary<string, object?> { ["Change"] = -5, ["Share"] = -0.25m, ["Name"] = "-x", ["Big"] = -3_000_000_000L },
        ]);
        Assert.Equal("\"-5\",\"-0.25\",\"'-x\",\"-3000000000\"", Lines(csv)[1]);
    }

    /* ---- #53: what is not in the audit log is not served -------------------------------------------------- */

    [Fact]
    public void Every_audit_value_is_cut_to_its_column()
    {
        Assert.Equal(400, AuditLog.Fit(new string('x', 450), 400)!.Length);
        Assert.Equal("short", AuditLog.Fit("short", 400));
        Assert.Null(AuditLog.Fit(null, 400));
    }

    [Fact]
    public async Task An_audit_row_that_cannot_be_written_refuses_the_request()
    {
        var options = Options.Create(new InsightsOptions { ConnectionString = "Server=tcp:127.0.0.1,1;Database=none;Integrated Security=true;Connect Timeout=1;Encrypt=false" });
        var cache = new QueryCache(new MemoryCache(new MemoryCacheOptions()), options, NullLogger<QueryCache>.Instance);
        var audit = new AuditLog(new Db(new TenantAccessor(options), cache), new HttpContextAccessor(), NullLogger<AuditLog>.Instance);
        var before = AuditLog.RetryDelay;
        AuditLog.RetryDelay = TimeSpan.FromMilliseconds(10);
        try
        {
            var viewer = new Viewer { WindowsName = @"CONTOSO\avery", Upn = "avery.morgan@contoso.com", Roles = new HashSet<string>() };
            var ex = await Assert.ThrowsAsync<AuditLogUnavailableException>(() => audit.WriteAsync(viewer, "Api", "/api/v1/targets", new string('z', 500), "days=30"));
            Assert.NotNull(ex.InnerException);
        }
        finally { AuditLog.RetryDelay = before; }
    }

    /* ---- #57: one key ring for every web server ----------------------------------------------------------- */

    static IDataProtector Server(InsightsOptions o)
    {
        var services = new ServiceCollection();
        Assert.NotNull(KeyRing.Configure(services, o, protect: false));
        return services.BuildServiceProvider().GetRequiredService<IDataProtectionProvider>().CreateProtector("Microsoft.AspNetCore.Antiforgery.AntiforgeryToken.v1");
    }

    [Fact]
    public void Two_web_servers_with_the_same_key_folder_read_each_others_tokens()
    {
        var folder = Path.Combine(Path.GetTempPath(), "insights-keys-" + Guid.NewGuid().ToString("N"));
        try
        {
            var o = new InsightsOptions { DataProtectionKeysPath = folder };
            var token = Server(o).Protect("drawn on WEB1");
            Assert.Equal("drawn on WEB1", Server(o).Unprotect(token));
            Assert.NotEmpty(Directory.GetFiles(folder, "*.xml"));
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    [Fact]
    public void One_web_server_without_a_key_folder_is_left_as_it_was() =>
        Assert.Null(KeyRing.Configure(new ServiceCollection(), new InsightsOptions()));

    [Fact]
    public void Keys_that_cannot_be_protected_stop_the_site_with_the_reason()
    {
        var folder = Path.Combine(Path.GetTempPath(), "insights-keys-" + Guid.NewGuid().ToString("N"));
        try
        {
            var ex = Assert.Throws<InvalidOperationException>(() => KeyRing.Configure(new ServiceCollection(),
                new InsightsOptions { DataProtectionKeysPath = folder, DataProtectionCertificate = "0000000000000000000000000000000000000000" }));
            Assert.Contains("Insights:DataProtectionCertificate", ex.Message);
            // For the account the site runs as: works for a domain account, and otherwise says what to do instead.
            if (OperatingSystem.IsWindows())
            {
                try { Assert.Contains("protected for", KeyRing.Configure(new ServiceCollection(), new InsightsOptions { DataProtectionKeysPath = folder })); }
                catch (InvalidOperationException no) { Assert.Contains("Name a certificate in Insights:DataProtectionCertificate", no.Message); }
            }
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    /* ---- #69: no page number overflows the offset --------------------------------------------------------- */

    [Theory]
    [InlineData("?p=42949674")]
    [InlineData("?p=2147483647")]
    public void A_huge_page_number_stays_a_legal_offset(string query)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.QueryString = new QueryString(query);
        var p = new Paging(ctx.Request, new Dictionary<string, string> { ["name"] = "du.DisplayName" }, "name");
        Assert.Equal(Paging.MaxPage, p.Page);
        var offset = long.Parse(Regex.Match(p.OrderAndPage(), @"OFFSET (-?\d+) ROWS").Groups[1].Value);
        Assert.Equal((long)(Paging.MaxPage - 1) * Paging.PageSize, offset);
        p.Total = 10;
        Assert.Contains("of 10", p.Controls(ctx.Request, new Dictionary<string, string> { ["name"] = "Name" }).Value);
    }

    /* ---- #73: a list no longer than the setting that holds it --------------------------------------------- */

    [Fact]
    public void The_non_human_lists_are_held_to_the_width_of_a_setting()
    {
        var schema = Source("..", "sql", "Create-CopilotDashboardSchema.sql");
        var width = int.Parse(Regex.Match(schema, @"CREATE TABLE dash\.Setting\s*\(\s*Name[^\n]*\n\s*Value\s+NVARCHAR\((\d+)\)").Groups[1].Value);
        Assert.Equal(Pages.Admin.ReferenceModel.MaxList, width);
    }

    /* ---- #75: the viewer is read from the database, inside a cached page too ------------------------------ */

    [Fact]
    public async Task Off_reads_the_database_inside_a_cached_page_and_then_the_page_is_cached_again()
    {
        var cache = new QueryCache(new MemoryCache(new MemoryCacheOptions()), Options.Create(new InsightsOptions()), NullLogger<QueryCache>.Instance);
        var runs = 0;
        Task<int> Grants() => cache.GetAsync("T", "q", "SELECT Principal, PrincipalType, Role FROM dash.RoleGrant", null, () => Task.FromResult(++runs));
        using (QueryCache.Use())
        {
            Assert.Equal(1, await Grants());
            Assert.Equal(1, await Grants());                                // a page's own read, from memory
            using (QueryCache.Off())
            {
                Assert.False(QueryCache.On);
                Assert.Equal(2, await Grants());
                Assert.Equal(3, await Grants());                            // the viewer, from the database every time
            }
            Assert.True(QueryCache.On);
        }
    }

    /* ---- #76: bounded, and an old stamp's results go -------------------------------------------------------- */

    static async Task Until(Func<bool> done)
    {
        for (var i = 0; i < 100 && !done(); i++) await Task.Delay(20);
    }

    [Fact]
    public async Task A_new_stamp_or_a_write_removes_what_was_kept_under_the_old_one()
    {
        var store = new MemoryCache(new MemoryCacheOptions());
        var cache = new QueryCache(store, Options.Create(new InsightsOptions()), NullLogger<QueryCache>.Instance);
        using (QueryCache.Use())
        {
            for (var i = 0; i < 5; i++) await cache.GetAsync("T", "s", "SELECT @i", new { i }, () => Task.FromResult(i));
            await cache.GetAsync("Other", "s", "SELECT 1", null, () => Task.FromResult(1));
            Assert.Equal(6, store.Count);
            cache.SetStamp("T", "after the refresh");
            await Until(() => store.Count == 1);
            Assert.Equal(1, store.Count);                                   // the other tenant's stays
            cache.SetStamp("Other", "s1");
            await Until(() => store.Count == 0);
            Assert.Equal(0, store.Count);
            await cache.GetAsync("Other", "s", "SELECT 2", null, () => Task.FromResult(2));
            cache.SetStamp("Other", "s1");                                  // read again, unchanged: nothing ends
            Assert.Equal(2, await cache.GetAsync("Other", "s", "SELECT 2", null, () => Task.FromResult(99)));
            Assert.Equal(1, store.Count);
            cache.Wrote("Other");
            await Until(() => store.Count == 0);
            Assert.Equal(0, store.Count);
        }
    }

    [Fact]
    public async Task The_store_holds_no_more_results_than_its_limit()
    {
        var store = QueryCache.Store(100);
        var cache = new QueryCache(store, Options.Create(new InsightsOptions()), NullLogger<QueryCache>.Instance);
        using (QueryCache.Use())
        {
            for (var p = 1; p <= 500; p++)
                Assert.Equal(p, await cache.GetAsync("T", "q", "SELECT 1 OFFSET @p ROWS", new { p }, () => Task.FromResult(p)));
            Assert.True(((MemoryCache)store).Count <= 100);
            // Still a cache: a result just kept is found again.
            var runs = 0;
            await cache.GetAsync("T", "q", "SELECT 1 OFFSET @p ROWS", new { p = 501 }, () => Task.FromResult(501));   // over the limit: the oldest go
            await Until(() => ((MemoryCache)store).Count < 100);
            await cache.GetAsync("T", "q", "SELECT 2", null, () => Task.FromResult(++runs));
            await cache.GetAsync("T", "q", "SELECT 2", null, () => Task.FromResult(++runs));
            Assert.Equal(1, runs);
        }
    }

    /* ---- #77 and #78: what the stamp follows, and when the pages are built again --------------------------- */

    [Fact]
    public void The_stamp_follows_every_license_table_and_a_run_only_when_it_ends()
    {
        var batch = DataStamp.Batch();
        foreach (var t in new[] { "audit.LicenseSku", "audit.CopilotLicense", "audit.UserLicenseSku", "audit.GroupMember", "audit.UserDirectory", "dash.RefreshRun" })
            Assert.Contains($"(N'{t}'", batch);
        Assert.Contains("FROM audit.PipelineRun WHERE EndedUtc IS NOT NULL", batch);
        Assert.DoesNotContain("CONCAT(MAX(RunId), N'':'', COUNT_BIG(*)) FROM audit.PipelineRun", batch);
        Assert.All(Regex.Matches(batch, @"\(N'[^']+', N'(.*?)'\)(?:,|;)").Select(m => m.Groups[1].Value.Replace("''", "'")),
            q => Assert.True(q.Length <= 400, q));                         // each fits the batch's NVARCHAR(400)
    }

    [Fact]
    public void The_pages_are_built_again_when_the_data_changes_or_the_last_build_has_expired()
    {
        var ended = new DateTime(2026, 10, 2, 1, 45, 0, DateTimeKind.Utc);
        Assert.True(PageWarmer.Due(null, "s1", null, ended, 720));                         // never built
        Assert.True(PageWarmer.Due("s1", "s2", ended, ended.AddMinutes(5), 720));          // the data changed
        Assert.False(PageWarmer.Due("s1", "s1", ended, ended.AddHours(11), 720));          // still kept
        Assert.True(PageWarmer.Due("s1", "s1", ended, ended.AddMinutes(720), 720));        // all of it expired: 13:45 UTC, not midnight
        Assert.False(PageWarmer.Due("s1", "s1", null, ended.AddDays(1), 720));             // a build under way
    }

    /* ---- #79: Admin > Health says what the cache does ------------------------------------------------------- */

    [Fact]
    public void Admin_health_no_longer_says_the_lists_edited_all_day_always_read_the_database()
    {
        var admin = Source("CopilotInsights", "Pages", "Admin", "Admin.cs");
        Assert.DoesNotContain("always read the database", admin);
        Assert.Contains("has a version of its own", admin);
    }

    /* ---- #92: the last complete day is cut where the data's days are --------------------------------------- */

    [Fact]
    public void Today_is_the_day_in_the_zone_the_data_is_cut_in()
    {
        var eastern = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
        var utc = new DateTime(2026, 10, 3, 1, 20, 0, DateTimeKind.Utc);           // 21:20 on 2 October in New York
        Assert.Equal(20261002, ReportContext.TodayKey(utc, eastern));
        Assert.Equal(20261003, ReportContext.TodayKey(utc, TimeZoneInfo.Utc));
        Assert.Equal(new DateTime(2026, 10, 2), ReportContext.Day(20261002));
        var tokyo = TimeZoneInfo.FindSystemTimeZoneById("Tokyo Standard Time");
        Assert.Equal(20261003, ReportContext.TodayKey(new DateTime(2026, 10, 2, 16, 0, 0), tokyo));
    }

    /* ---- #101: settings read for each request ----------------------------------------------------------- */

    [Fact]
    public async Task A_value_is_read_once_for_a_request_and_again_for_the_next()
    {
        var reads = 0;
        Task<bool> Read() => Task.FromResult(++reads > 1);
        var first = new DefaultHttpContext();
        Assert.False(await RequestMemo.GetAsync(first, "privacy-mode", Read));
        Assert.False(await RequestMemo.GetAsync(first, "privacy-mode", Read));
        Assert.Equal(1, reads);
        Assert.True(await RequestMemo.GetAsync(new DefaultHttpContext(), "privacy-mode", Read));   // turned on elsewhere: the next request sees it
        RequestMemo.Forget(first, "privacy-mode");
        Assert.True(await RequestMemo.GetAsync(first, "privacy-mode", Read));
        Assert.Equal(3, reads);
    }

    [Fact]
    public async Task A_read_that_failed_is_not_kept_for_the_request()
    {
        var ctx = new DefaultHttpContext();
        await Assert.ThrowsAsync<InvalidOperationException>(() => RequestMemo.GetAsync<int>(ctx, "k", () => throw new InvalidOperationException()));
        Assert.Equal(7, await RequestMemo.GetAsync(ctx, "k", () => Task.FromResult(7)));
    }

    /* ---- #107: say what a follow-up counts -------------------------------------------------------------------- */

    [Fact]
    public void A_follow_up_is_described_as_what_is_counted()
    {
        Assert.Contains("in any app", Pages.Enablement.FollowUp.Kpi);
        Assert.Contains("less than an hour", Pages.Enablement.FollowUp.Panel);
        foreach (var page in new[] { Source("CopilotInsights", "Pages", "Enablement", "Enablement.cs"), Source("CopilotInsights", "Pages", "Adoption", "Adoption.cs") })
        {
            Assert.DoesNotContain("replied to Copilot at least once", page);
            Assert.DoesNotContain("refine an answer instead", page);
        }
    }
}
