using System.Reflection;
using CopilotInsights.Data;
using CopilotInsights.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CopilotInsights.Tests;

/// <summary>The 2.4.12 review's findings on scope, periods and license exports (group B).</summary>
public class Review2412BTests
{
    static Viewer V(int? level, params string[] roles) =>
        new() { WindowsName = @"CONTOSO\me", Upn = "wei.okafor@contoso.com", Level = level, InDirectory = true, Roles = new HashSet<string>(roles, StringComparer.OrdinalIgnoreCase) };

    static ReportContext Ctx(Viewer viewer, string? root, int days, DateTime end) => new()
    {
        Viewer = viewer, Root = root, Days = days, End = end, Start = end.AddDays(-(days - 1)), MinGroupSize = 5, InactiveDays = 30,
    };

    static string SourceDirectory()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "CopilotInsights", "Pages", "InsightsPage.cs"))) return Path.Combine(d.FullName, "CopilotInsights");
        throw new DirectoryNotFoundException("CopilotInsights, above " + AppContext.BaseDirectory);
    }

    /* The demo data: 182 days, 29 March to 26 September 2026. */
    static readonly DateTime FirstDay = new(2026, 3, 29), LastDay = new(2026, 9, 26);

    /* ---- #14, #15, #40: targets follow the part of the organization the report is on ---- */

    static List<TargetRow> Targets() =>
    [
        new() { TargetId = 1, RootUpn = null, Metric = "Adoption" },
        new() { TargetId = 2, RootUpn = "casey.ivanova@contoso.com", Metric = "ActiveShare", RootSize = 300 },
        new() { TargetId = 3, RootUpn = "lee.park@contoso.com", Metric = "Adoption", RootSize = 20 },
    ];

    [Fact]
    public void The_whole_organization_sees_every_target_and_a_part_only_its_own()
    {
        var all = Targets();
        Assert.Equal([1, 2, 3], OpsReports.Visible(all, null, wholeOrganization: true, new HashSet<string>()).Select(t => t.TargetId));
        /* A level-3 manager (or an executive's board pack drilled into one): not the whole organization's, not another part's. */
        var under = new HashSet<string>(["wei.okafor@contoso.com", "lee.park@contoso.com"], StringComparer.OrdinalIgnoreCase);
        Assert.Equal([3], OpsReports.Visible(all, "wei.okafor@contoso.com", wholeOrganization: false, under).Select(t => t.TargetId));
        Assert.Equal([3], OpsReports.Visible(all, "wei.okafor@contoso.com", wholeOrganization: true, under).Select(t => t.TargetId));
        /* No root is the whole organization, which only a viewer of the whole organization has. */
        Assert.Empty(OpsReports.Visible(all, null, wholeOrganization: false, under));
        /* Someone not in the directory: a scope of nobody, and nothing under it. */
        Assert.Empty(OpsReports.Visible(all, "(nobody)", wholeOrganization: false, new HashSet<string>()));
    }

    [Fact]
    public void A_target_for_a_part_under_the_minimum_shows_no_figures_unless_it_is_the_viewers_own()
    {
        var small = new TargetRow { RootUpn = "lee.park@contoso.com", Metric = "ActivePeople", RootSize = 3 };
        Assert.True(OpsReports.TooSmall(small, 5, "wei.okafor@contoso.com"));
        Assert.False(OpsReports.TooSmall(small, 5, "lee.park@contoso.com"));
        Assert.False(OpsReports.TooSmall(small, 3, "wei.okafor@contoso.com"));
        Assert.False(OpsReports.TooSmall(new TargetRow { RootUpn = null, Metric = "ActivePeople" }, 5, null));
        /* The size is the minimum's business, not the API's: it is not written into /api/v1/targets. */
        Assert.DoesNotContain("RootSize", System.Text.Json.JsonSerializer.Serialize(small));
    }

    /* ---- #16: the license pool and the budget are the tenant's, every term of them ---- */

    [Fact]
    public void The_tenants_figures_cover_the_whole_organization_for_a_viewer_with_the_Licensing_role()
    {
        var page = Ctx(V(3, Roles.Licensing), "wei.okafor@contoso.com", 90, LastDay);
        var t = OpsReports.Tenant(page);
        Assert.False(page.Viewer.OrgWide);
        Assert.Null(t.Root);
        Assert.Equal("", t.Scope("ud.UserKey"));
        Assert.Equal((30, LastDay.AddDays(-29), LastDay), (t.Days, t.Start, t.End));
    }

    /* ---- #42 and #72: an export posts, with the token, at the part of the organization shown ---- */

    [Fact]
    public void An_export_is_a_form_that_posts_at_the_part_shown()
    {
        var c = Ctx(V(1), "casey.ivanova@contoso.com", 30, LastDay);
        var html = LicenseActions.Button(c, "/licensing/leavers", "handler=bulk", "<input type=\"hidden\" name=\"__RequestVerificationToken\" value=\"t\"/>", "&tenant=Fabrikam", "Entra bulk-remove file");
        Assert.StartsWith("<form class=\"inline\" method=\"post\"", html);
        /* With several tenants the post names the tenant the page showed (#13). */
        Assert.Contains("action=\"/licensing/leavers?d=30&amp;org=casey.ivanova%40contoso.com&amp;handler=bulk&amp;tenant=Fabrikam\"", html);
        Assert.Contains("__RequestVerificationToken", html);
        Assert.Contains(">Entra bulk-remove file</button></form>", html);
    }

    [Fact]
    public void No_license_export_is_made_by_a_GET()
    {
        var pages = typeof(Pages.InsightsPage).Assembly.GetTypes().Where(t => typeof(Pages.InsightsPage).IsAssignableFrom(t)).ToList();
        var gets = pages.SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Where(m => m.Name is "OnGetBulkAsync" or "OnGetScriptAsync").Select(m => m.DeclaringType!.Name + "." + m.Name).ToList();
        Assert.Empty(gets);
        Type[] exports = [typeof(Pages.Forecasting.ActionsModel), typeof(Pages.Forecasting.WavesModel), typeof(Pages.Licensing.DeparturesModel),
                          typeof(Pages.Licensing.LicenseForCreditsModel), typeof(Pages.Licensing.NonPersonModel),   // one page for Licensing, Security and Compliance
                          typeof(Pages.Licensing.SwapModel), typeof(Pages.Licensing.LeaversModel)];
        foreach (var t in exports)
        {
            Assert.NotNull(t.GetMethod("OnPostBulkAsync", BindingFlags.Public | BindingFlags.Instance));
            Assert.NotNull(t.GetMethod("OnPostScriptAsync", BindingFlags.Public | BindingFlags.Instance));
        }
        /* A plain CSV of a report's table changes nothing, and stays a GET. */
        Assert.NotNull(typeof(Pages.InsightsPage).GetMethod("OnGetCsvAsync"));
    }

    [Fact]
    public void Every_export_button_is_built_with_the_scope()
    {
        var lines = Directory.GetFiles(Path.Combine(SourceDirectory(), "Pages"), "*.cs", SearchOption.AllDirectories)
            .SelectMany(f => File.ReadLines(f).Select((line, i) => (File: Path.GetFileName(f), Line: i + 1, Text: line)))
            .Where(x => x.Text.Contains("handler=bulk") || x.Text.Contains("handler=script") || x.Text.Contains("&handler={handler}")).ToList();
        Assert.True(lines.Count >= 12, $"found {lines.Count} export handlers named; the pattern has stopped matching them");
        var bare = lines.Where(x => !x.Text.Contains("LicenseActions.Button(")).Select(x => $"{x.File}:{x.Line}").ToList();
        Assert.True(bare.Count == 0, "an export not built by LicenseActions.Button at " + string.Join(", ", bare));
    }

    /* ---- #98: a record that cannot be written is not let go ---- */

    [Fact]
    public async Task An_export_whose_record_fails_fails_too()
    {
        var options = Options.Create(new InsightsOptions { ConnectionString = "Server=tcp:127.0.0.1,1;Database=none;Integrated Security=true;Connect Timeout=3;Encrypt=false;Pooling=false" });
        var db = new Db(new TenantAccessor(options), new QueryCache(new MemoryCache(new MemoryCacheOptions()), options, NullLogger<QueryCache>.Instance));
        /* No server: not the missing table (error 208), which is the only error an export goes on without. */
        var ex = await Assert.ThrowsAsync<Microsoft.Data.SqlClient.SqlException>(() => LicenseActions.RecordAsync(db, "revoke", ["a@contoso.com"], "me@contoso.com"));
        Assert.NotEqual(208, ex.Number);
        /* Nothing to record is nothing to fail. */
        await LicenseActions.RecordAsync(db, "revoke", [], "me@contoso.com");
        await LicenseActions.RecordAsync(db, "not a kind", ["a@contoso.com"], "me@contoso.com");
    }

    /* ---- #43: the period before is compared only where the data covers it ---- */

    [Fact]
    public void The_period_before_is_compared_only_where_the_data_covers_all_of_it()
    {
        var viewer = V(1);
        /* 180 days on 182 days of data: the 180 before start in October 2025, with two days of data in them. */
        var before180 = OpsReports.Window(Ctx(viewer, null, 180, LastDay), LastDay.AddDays(-180), 180);
        Assert.Equal(new DateTime(2025, 10, 2), before180.Start);
        Assert.False(OpsReports.Covers(before180, FirstDay));
        /* 90 days: the 90 before are all inside the data. */
        var before90 = OpsReports.Window(Ctx(viewer, null, 90, LastDay), LastDay.AddDays(-90), 90);
        Assert.True(OpsReports.Covers(before90, FirstDay));
        Assert.False(OpsReports.Covers(before90, null));
        Assert.Equal("no full period before: the data starts on 29 Mar 2026", OpsReports.NoFullPeriodBefore(FirstDay));
    }

    /* ---- #44: the license cost is the licenses held on each day ---- */

    [Fact]
    public void The_license_cost_is_priced_on_the_days_each_license_was_held()
    {
        /* Steady: 1,269 licenses for 30 days is today's count times the days, as before. */
        Assert.Equal(Math.Round(1_269m * 30m * 30m * 12m / 365m, 2), Math.Round(Pages.Licensing.Price.ForLicenseDays(1_269L * 30, 30m), 2));
        /* A rollout: 145,009 license-days in 180 days of a ramp to 1,269 cost 143,023, not 1,269 × 180 days (225,291). */
        Assert.Equal(143_023m, Math.Round(Pages.Licensing.Price.ForLicenseDays(145_009, 30m)));
        Assert.Equal(0m, Pages.Licensing.Price.ForLicenseDays(0, 30m));
    }

    /* ---- #45: a month is scaled from the days the data covers ---- */

    [Fact]
    public void A_month_is_scaled_from_the_days_the_data_covers()
    {
        var viewer = V(1);
        Assert.Equal(182, OpsReports.DaysCovered(Ctx(viewer, null, 365, LastDay), FirstDay));
        Assert.Equal(180, OpsReports.DaysCovered(Ctx(viewer, null, 180, LastDay), FirstDay));
        Assert.Equal(30, OpsReports.DaysCovered(Ctx(viewer, null, 30, LastDay), FirstDay));
        Assert.Equal(365, OpsReports.DaysCovered(Ctx(viewer, null, 365, LastDay), null));
        /* Data that starts after the period: one day, never zero or less. */
        Assert.Equal(1, OpsReports.DaysCovered(Ctx(viewer, null, 7, LastDay), LastDay.AddDays(5)));
    }

    /* ---- #67: the API's index is a call like any other ---- */

    [Fact]
    public void Every_endpoint_of_the_data_API_goes_through_Run()
    {
        var api = File.ReadAllText(Path.Combine(SourceDirectory(), "Infrastructure", "DataApi.cs"));
        var maps = api.Split("api.MapGet(").Skip(1).ToList();
        Assert.True(maps.Count >= 10, $"found {maps.Count} endpoints");
        foreach (var m in maps)
        {
            var head = m[..m.IndexOf("=>", StringComparison.Ordinal)];
            Assert.True(m.IndexOf("=> Run(h,", StringComparison.Ordinal) == head.Length, "not through Run: " + head.Trim());
        }
    }

    /* ---- #90: the digest ranks adoption only over the minimum of license holders ---- */

    static UnitRow U(string name, int people, int licensed, int activeLicensed) =>
        new() { Unit = name, Name = name, People = people, Licensed = licensed, ActiveLicensed = activeLicensed };

    [Fact]
    public void The_digest_ranks_adoption_as_the_Overview_does()
    {
        var units = new[] { U("Owen Tanaka", 77, 35, 30), U("Leila Kowalski", 74, 49, 41), U("A team of twelve", 12, 1, 1), U("No licenses", 20, 0, 0), U("Ravi Wong", 9, 4, 2) };
        var ranked = OpsReports.AdoptionRanking(units, 5);
        Assert.Equal(["Owen Tanaka", "Leila Kowalski", "2 smaller groups, combined"], ranked.Select(u => u.Name));
        /* The one license holder of the team of twelve is not a row of their own, and nothing is left out to subtract. */
        Assert.Equal(5, ranked[^1].Licensed);
        Assert.Equal(units.Sum(u => u.Licensed), ranked.Sum(u => u.Licensed));
        /* With the demo's minimum of 50, every unit is under it: one row, as the Overview shows one bar. */
        Assert.Equal(["4 smaller groups, combined"], OpsReports.AdoptionRanking(units, 50).Select(u => u.Name));
    }

    /* ---- #93: a page past the end ---- */

    [Fact]
    public void A_page_past_the_end_shows_no_rows_and_goes_back_to_the_last()
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Path = "/adoption/by-license";
        ctx.Request.QueryString = new QueryString("?lic=all&p=100000");
        var p = new Paging(ctx.Request, new Dictionary<string, string> { ["name"] = "du.DisplayName" }, "name") { Total = 10_568 };
        var html = p.Nav(ctx.Request).Value!;   // The page links are under the list
        Assert.DoesNotContain("4,999,951", html);
        Assert.Contains("0–0 of 10,568", html);
        Assert.Contains("p=212\">‹ Previous", html);
        Assert.DoesNotContain("Next ›", html);
    }
}
