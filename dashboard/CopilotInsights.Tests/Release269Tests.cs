using System.Text.RegularExpressions;
using CopilotInsights.Data;
using CopilotInsights.Pages;
using CopilotInsights.Tests.Review2412A;
using Xunit;

namespace CopilotInsights.Tests;

/// <summary>2.6.9: what a cut of seats really saves: the volume price, the Dataverse capacity the seats bring, and the spare kept on purpose.</summary>
public class SeatCutTests
{
    static readonly CostRules.SeatPrice PowerApps = new(20m, 2000, 12m, 0.25m, 2m);

    [Fact]
    public void A_cut_under_the_volume_price_counts_every_seat_going_back_to_the_list_price()
    {
        /* 2,000 at 12 is 24,000; 776 at 20 is 15,520: cutting 1,224 saves 8,480, not 1,224 x 20 = 24,480. */
        var cut = CostRules.CutSeats(PowerApps with { DatabaseGbPerSeat = 0, FileGbPerSeat = 0 }, 2000, 1224, null, "USD");
        Assert.Equal(1224, cut.Seats);
        Assert.Equal(8480m, cut.Saving);
        Assert.Contains("goes back to 20 USD", cut.Note);
    }

    [Fact]
    public void A_cut_stops_at_the_volume_price_where_crossing_it_would_save_less()
    {
        /* 2,100 at 12: cutting 100 (to 2,000) saves 1,200; cutting 200 would put 1,900 at 20 and cost more. */
        var cut = CostRules.CutSeats(PowerApps with { DatabaseGbPerSeat = 0, FileGbPerSeat = 0 }, 2100, 200, null, "USD");
        Assert.Equal(100, cut.Seats);
        Assert.Equal(1200m, cut.Saving);
        Assert.Contains("down to the volume price", cut.Note);
    }

    [Fact]
    public void Giving_up_more_Dataverse_capacity_than_is_spare_costs_add_on_capacity()
    {
        /* 100 seats give up 25 GB of database with 10 GB spare: 15 GB over at 40 a GB, 600 off the 2,000 saved. */
        var p = new CostRules.SeatPrice(20m, null, null, 0.25m, 0m);
        var room = new CostRules.DataverseRoom(10m, 1000m, 40m, 2m);
        var cut = CostRules.CutSeats(p, 500, 100, room, "USD");
        Assert.Equal(1400m, cut.Saving);
        Assert.Contains("over its entitlement", cut.Note);
        /* The room is taken: a second cut finds none left and pays for all of its capacity. */
        Assert.Equal(1000m, room.Take(p, 100));
    }

    [Fact]
    public void Without_the_capacity_loaded_the_cut_says_it_was_not_checked()
    {
        var cut = CostRules.CutSeats(new CostRules.SeatPrice(20m, null, null, 0.25m, 2m), 500, 100, null, "USD");
        Assert.Equal("Low", cut.Confidence);
        Assert.Contains("not checked", cut.Note);
        Assert.Equal(2000m, cut.Saving);
    }

    [Fact]
    public void With_only_one_side_of_the_capacity_loaded_the_cut_is_not_checked()
    {
        /* An unknown file side would otherwise count as room to spare (review, 2.6.9). */
        var cut = CostRules.CutSeats(new CostRules.SeatPrice(20m, null, null, 0.25m, 2m), 500, 100, new CostRules.DataverseRoom(1000m, null, 40m, 2m), "USD");
        Assert.Equal("Low", cut.Confidence);
    }

    [Fact]
    public void Seats_kept_spare_on_purpose_are_not_a_saving()
    {
        /* The client's Copilot pool: 18,640 bought, 17,886 assigned. 754 unassigned is within the 10% kept spare (1,864). */
        Assert.Equal(1864, CostRules.Spare(18640, 10m));
        Assert.Null(CostRules.Unassigned(CostAreas.Copilot, "Microsoft 365 Copilot", 18640, 17886, 18m, "USD", spare: CostRules.Spare(18640, 10m)));
        /* Beyond the spare, only what is beyond it. */
        var o = CostRules.Unassigned(CostAreas.Copilot, "Microsoft 365 Copilot", 1000, 800, 18m, "USD", spare: CostRules.Spare(1000, 10m));
        Assert.NotNull(o);
        Assert.Equal(1800m, o!.Saving);
        Assert.Contains("100 of them kept spare on purpose, 100 beyond that", o.Evidence);
        /* A license bought for its capacity or volume price: never money back. */
        Assert.Null(CostRules.Unassigned(CostAreas.Apps, "Power Apps Premium", 2000, 776, 12m, "USD", forCapacity: true));
        Assert.Equal(0, CostRules.Spare(100, 0m));
    }

    [Fact]
    public void The_volume_price_and_capacity_are_given_together_or_not_at_all()
    {
        Assert.Equal((2000, 12m, 0.25m, 2m), Pages.Admin.AssumptionsModel.PriceExtras("2000", "12", "0.25", "2"));
        Assert.Equal((null, null, null, null), Pages.Admin.AssumptionsModel.PriceExtras("", "", "", ""));
        Assert.Throws<ArgumentException>(() => Pages.Admin.AssumptionsModel.PriceExtras("2000", "", null, null));
    }
}

/// <summary>2.6.9: the assumptions register: one list of every input, where it is set, and what disagrees.</summary>
public class AssumptionRegisterTests
{
    static string Root()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(d.FullName, "CHANGELOG.md"))) d = d.Parent!;
        return d.FullName;
    }

    [Fact]
    public void Each_assumption_opens_a_page_with_its_field()
    {
        var sources = new Dictionary<string, string>
        {
            [Assumptions.Access] = Source.Read("Pages", "Admin", "Admin.cs"),
            [Assumptions.Targets] = Source.Read("Pages", "Admin", "Operations.cs"),
            [Assumptions.Reservations] = Source.Read("Pages", "Leadership", "Reservations.cs"),
            [Assumptions.CreditChargeback] = Source.Read("Pages", "Leadership", "CreditChargeback.cs"),   // 2.7.2
            [Assumptions.CreditCommitment] = Source.Read("Pages", "Forecasting", "CreditCommitment.cs"),
        };
        foreach (var d in Assumptions.Settings)
        {
            var field = d.Field.Split('|')[0];
            Assert.True(sources[d.Page].Contains($"Input(\"{field}\"") || sources[d.Page].Contains($"Select(\"{field}\""), $"{d.Key}: no field {field} on {d.Page}");
            Assert.StartsWith(d.Page + "?focus=", Assumptions.EditHref(d));
        }
        Assert.Equal(Assumptions.Settings.Count, Assumptions.Settings.Select(d => d.Key).Distinct().Count());
        /* A price row is found by its item, which its form carries hidden. */
        Assert.Equal("/admin/assumptions?focus=price%7CPOWERAPPS_PER_USER", Assumptions.EditHref(Assumptions.Price("POWERAPPS_PER_USER", "Power Apps Premium", "Power Apps", "user")));
        Assert.Contains("hidden.form.querySelector", File.ReadAllText(Path.Combine(Root(), "dashboard", "CopilotInsights", "wwwroot", "site.js")));
    }

    [Fact]
    public void The_defaults_the_register_names_are_the_ones_installed()
    {
        var sql = string.Concat(new[] { "Create-CopilotDashboardSchema.sql", "Create-CopilotOperationsSchema.sql" }.Select(f => File.ReadAllText(Path.Combine(Root(), "sql", f))));
        foreach (var d in Assumptions.Settings.Where(d => d.Kind == AssumptionKind.Setting && d.Default is not null))
            Assert.Matches(new Regex($@"'{d.Key}',\s*N'{Regex.Escape(d.Default!)}'"), sql);
    }

    [Fact]
    public void Prices_that_disagree_are_said_with_both_values_and_where_each_is_set()
    {
        var lines = Assumptions.Conflicts(30m, 18m, new DateTime(2026, 1, 1), 30m, "USD");
        var line = Assert.Single(lines);
        Assert.Contains("30 USD a month in Admin › Access", line);
        Assert.Contains("18 USD on Leadership › Copilot reservations", line);
        Assert.Empty(Assumptions.Conflicts(18m, 18m, null, 18m, "USD"));
        Assert.Equal(2, Assumptions.Conflicts(30m, 18m, null, 25m, "USD").Count);
        Assert.Empty(Assumptions.Conflicts(30m, null, null, null, "USD"));
    }

    [Fact]
    public void Every_money_figure_says_how_it_is_worked_out()
    {
        foreach (var page in new[] { new[] { "Licensing", "Operations.cs" }, ["Forecasting", "Forecasting.cs"], ["Forecasting", "Planning.cs"], ["Licensing", "Fading.cs"],
                                     ["Licensing", "LicenseForCredits.cs"], ["Licensing", "PayAsYouGo.cs"], ["Licensing", "Swap.cs"], ["Licensing", "CreditUse.cs"],
                                     ["Agents", "Credits.cs"], ["Licensing", "Suites.cs"], ["OperationsReports.cs"], ["Leadership", "Reservations.cs"], ["Leadership", "Costs.cs"] })
            Assert.Contains("await WhyAsync(", Source.Read(["Pages", .. page]));
        /* The credit estimate's tooltip is its formula, with the rates set. */
        var f = CreditRules.Formula(new CreditRates(), "USD");
        Assert.Contains("× 2", f); Assert.Contains("× 10", f); Assert.Contains("× 5", f); Assert.Contains("0.01 USD", f);
        /* "Change it" only for whoever may open the page it is set on. */
        Assert.Contains("Viewer.CanAdmin(a.Def.Page) ? CopilotInsights.Data.Assumptions.EditHref(a.Def) : null", Source.Read("Pages", "InsightsPage.cs"));
    }

    [Fact]
    public void The_new_pages_are_offered_and_described()
    {
        Assert.Contains(Nav.Groups.First(g => g.Persona == "Leadership").Pages, p => p.Path == "/leadership/assumptions" && p.Role is null);
        Assert.Contains(Nav.Groups.First(g => g.Persona == "Governance").Pages, p => p.Path == "/governance/ai-apps" && p.Role is null);
        Assert.NotNull(Guidance.For("/leadership/assumptions"));
        Assert.NotNull(Guidance.For("/governance/ai-apps"));
    }

    [Fact]
    public void No_subquery_sits_inside_an_aggregate()
    {
        foreach (var file in new[] { Source.Read("Data", "Assumptions.cs"), Source.Read("Pages", "Governance", "AiApps.cs"), Source.Read("Pages", "Leadership", "Assumptions.cs"), Source.Read("Data", "CostData.cs") })
            Assert.DoesNotMatch(new Regex(@"(COUNT|SUM|MAX|MIN)\((CASE WHEN[^)]*(EXISTS|SELECT)|\(SELECT)", RegexOptions.IgnoreCase), file);
    }
}

/// <summary>2.6.9: AI apps beyond Copilot, Power BI and Power Apps, from the activity database.</summary>
public class ActivitySourceTests
{
    static string Root()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(d.FullName, "CHANGELOG.md"))) d = d.Parent!;
        return d.FullName;
    }
    static string Read(params string[] p) => File.ReadAllText(Path.Combine([Root(), .. p]));

    [Fact]
    public void The_activity_database_is_installed_and_rolled_up_into_the_Copilot_database()
    {
        var installer = Read("Install-CopilotAudit.ps1");
        Assert.Contains("'Create-CopilotActivityRollup.sql'", installer);
        Assert.Contains("'Create-ActivitySchema.sql', 'Create-ActivityProcedure.sql'", installer);
        Assert.Contains("EXEC audit.usp_RollupActivity @ActivityDatabase = @a;", installer);
        var rollup = Read("sql", "Create-CopilotActivityRollup.sql");
        /* Read by its quoted name, never pasted in. */
        Assert.Contains("QUOTENAME(@ActivityDatabase)", rollup);
        /* Days in the reporting time zone, as Copilot's. */
        Assert.Contains("audit.ReportingTimeZone()", rollup);
        /* A product's CSV rows are replaced only by records of it. */
        Assert.Contains("WHERE u.Product IN (SELECT DISTINCT Product FROM #pp)", rollup);
        /* An administrator's resource map is kept. */
        Assert.Contains("WHERE NOT EXISTS (SELECT 1 FROM audit.AIAppResource AS r WHERE r.[Resource] = d.[Resource])", rollup);
        Assert.Contains("GRANT SELECT, INSERT, UPDATE ON audit.AIAppResource TO CopilotInsightsApp;", rollup);
    }

    [Fact]
    public void Record_type_searches_go_to_beta_and_name_no_operation()
    {
        var engine = Read("CopilotAuditEngine.cs");
        Assert.Contains("_o.RecordTypes != null && _o.RecordTypes.Length > 0 ? \"/beta\" : \"/v1.0\"", engine);
        Assert.Contains("if (_o.Operations != null && _o.Operations.Length > 0) body[\"operationFilters\"] = _o.Operations;", engine);
        var backfill = Read("Invoke-CopilotAuditBackfill.ps1");
        Assert.Contains("if ($RecordTypes -and -not $PSBoundParameters.ContainsKey('Operations')) { $Operations = @() }", backfill);
        Assert.Contains("if (Test-Proc 'audit.usp_ShredActivity')", backfill);
    }

    [Fact]
    public void A_history_is_shredded_when_final_and_owed_days_are_rolled_up()
    {
        /* Review, 2.6.9: the history was finalized and never shredded, and a rollup after failed nights skipped days. */
        var backfill = Read("Invoke-CopilotAuditBackfill.ps1");
        var finalize = backfill[backfill.IndexOf("function Invoke-CopilotAuditFinalize", StringComparison.Ordinal)..];
        Assert.Contains("EXEC audit.usp_ShredActivity;", finalize[..finalize.IndexOf("\n}", StringComparison.Ordinal)]);
        Assert.Contains("OR NOT EXISTS (SELECT 1 FROM audit.PipelineSetting WHERE Name = N'ActivityShredCompleteUtc')", Read("sql", "Create-ActivityProcedure.sql"));
        Assert.Contains("DECLARE @last DATE = (SELECT MAX(ToDay) FROM audit.ExternalUsageRun WHERE Source = 'Activity' AND Status = 'Succeeded');", Read("sql", "Create-CopilotActivityRollup.sql"));
        /* The dashboard's role can read and keep them on a new install, whose role is made after their scripts. */
        var ops = Read("sql", "Create-CopilotOperationsSchema.sql");
        Assert.Contains("(N'AIAppUserDay'), (N'AIAppResource')", ops);
        Assert.Contains("GRANT INSERT, UPDATE ON audit.AIAppResource TO CopilotInsightsApp;", ops);
        /* The stamp reads the rollup's runs, not the whole table. */
        Assert.DoesNotContain("\"audit.AIAppUserDay\",", Source.Read("Infrastructure", "DataStamp.cs"));
        /* -ActivityHistory for a tenant with no activity database records no run. */
        var pipeline = Read("Invoke-CopilotPipeline.ps1");
        Assert.True(pipeline.IndexOf("there is no activity history to load", StringComparison.Ordinal) < pipeline.IndexOf("INSERT INTO audit.PipelineRun (Kind, Host, RunAs)", StringComparison.Ordinal));
    }

    [Fact]
    public void Who_changed_an_assumption_is_shown_to_administrators_only()
    {
        Assert.Contains("Viewer.AnyAdmin && !Warming ? $\"{When(a.ChangedUtc)} by {a.ChangedBy}\"", Source.Read("Pages", "Leadership", "Assumptions.cs"));
    }

    [Fact]
    public void The_dashboard_reads_only_the_Copilot_database()
    {
        var page = Source.Read("Pages", "Governance", "AiApps.cs");
        Assert.DoesNotContain("AIAppCall", page);
        Assert.DoesNotContain("PlatformActivity ", page);
        /* People are counted, never listed: the page selects no caller. */
        Assert.DoesNotMatch(new Regex(@"SELECT[^;]*\ba\.Caller\b\s*,", RegexOptions.IgnoreCase), page.Replace("COUNT(DISTINCT a.Caller)", "").Replace("COUNT(DISTINCT CASE WHEN a.CallerKind = 'Person' THEN a.Caller END)", ""));
    }
}
