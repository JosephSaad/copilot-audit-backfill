using CopilotInsights.Data;
using CopilotInsights.Pages.Leadership;
using CopilotInsights.Tests.Review2412A;
using Xunit;

namespace CopilotInsights.Tests;

/// <summary>2.7.2: Copilot Credit chargeback by line of business, and the credit commitment (P3).</summary>
public class Release272Tests
{
    static string Root()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(d.FullName, "CHANGELOG.md"))) d = d.Parent!;
        return d.FullName;
    }

    static string Read(params string[] p) => File.ReadAllText(Path.Combine([Root(), .. p]));

    static readonly DateTime Start = new(2026, 8, 1);
    static CreditCommitment P3(decimal amount = 1000m) => new(1, "P3", amount, "USD", 0.01m, Start, Start.AddYears(1).AddDays(-1), null, null);
    static List<(DateTime, decimal)> Daily(int days, decimal each) => Enumerable.Range(0, days).Select(i => (Start.AddDays(i), each)).ToList();

    [Fact]
    public void A_path_charged_to_a_line_of_business_must_name_it_and_Enterprise_Shared_never_does()
    {
        Assert.True(CreditPaths.NeedsOrg(CreditPaths.Productivity));
        Assert.True(CreditPaths.NeedsOrg(CreditPaths.BusinessApplication));
        Assert.False(CreditPaths.NeedsOrg(CreditPaths.EnterpriseShared));
        Assert.False(CreditPaths.NeedsOrg(CreditPaths.Innovation));
        Assert.Contains("give its Org ID", Assert.Throws<ArgumentException>(() => CreditChargebackModel.Check("Env", "Productivity", null, null, null, null, Start, null, null)).Message);
        Assert.Contains("leave the Org ID empty", Assert.Throws<ArgumentException>(() => CreditChargebackModel.Check("Env", "Enterprise Shared", "LOB-1", null, null, null, Start, null, null)).Message);
        Assert.Contains("choose the path", Assert.Throws<ArgumentException>(() => CreditChargebackModel.Check("Env", "Shared", null, null, null, null, Start, null, null)).Message);
        Assert.Contains("after the start", Assert.Throws<ArgumentException>(() => CreditChargebackModel.Check("Env", "Innovation", null, null, null, null, Start, Start.AddDays(-1), null)).Message);
        var ok = CreditChargebackModel.Check("  Retail-Prod ", "LoB Business Application", " LOB-01 ", "", null, null, Start, null, null);
        Assert.Equal(("Retail-Prod", CreditPaths.BusinessApplication, "LOB-01", (string?)null), (ok.Environment, ok.Path, ok.OrgId, ok.CostCenter));
    }

    [Theory]
    [InlineData("Productivity", CreditPaths.Productivity)]
    [InlineData("lob productivity", CreditPaths.Productivity)]
    [InlineData("BusinessApplication", CreditPaths.BusinessApplication)]
    [InlineData("Business Application", CreditPaths.BusinessApplication)]
    [InlineData("ENTERPRISESHARED", CreditPaths.EnterpriseShared)]
    [InlineData("Innovation or development", CreditPaths.Innovation)]
    [InlineData("Unmapped", null)]
    [InlineData("", null)]
    public void A_path_is_read_by_its_value_or_its_label(string typed, string? expected) => Assert.Equal(expected, CreditChargebackModel.ParsePath(typed));

    [Fact]
    public void A_pasted_register_is_checked_whole_with_its_header_and_quoted_fields()
    {
        var rows = CreditChargebackModel.ParsePaste("Environment,Path,Org ID,Cost center,From\n\"Retail, East\",Productivity,LOB-01,CC-1,2026-11-01,owner@c.com,BP-1\nHR,EnterpriseShared,,,2026-11-01");
        Assert.Equal(2, rows.Count);
        Assert.Equal(("Retail, East", "owner@c.com", "BP-1"), (rows[0].Environment, rows[0].Owner, rows[0].Policy));
        Assert.Equal(CreditPaths.EnterpriseShared, rows[1].Path);
        Assert.Equal("line 1: LoB Productivity is charged to a line of business: give its Org ID.", Assert.Throws<ArgumentException>(() => CreditChargebackModel.ParsePaste("A,Productivity,,CC,2026-11-01")).Message);
        Assert.Contains("from is a date", Assert.Throws<ArgumentException>(() => CreditChargebackModel.ParsePaste("A,Innovation,,,11/01/2026")).Message);
    }

    [Fact]
    public void A_charge_is_the_credits_at_one_rate_and_waits_for_Finance_without_one()
    {
        Assert.Equal(1105.44m, CreditChargeRules.Charge(92120m, 0.012m));
        Assert.Null(CreditChargeRules.Charge(92120m, null));
        Assert.Equal(0.01m, CreditChargeRules.Charge(0.5m, 0.01m));   // half a cent rounds away from zero
    }

    [Fact]
    public void A_commitment_drawn_faster_than_planned_is_projected_to_run_out_early()
    {
        var s = CreditChargeRules.Project(P3(1000), Start.AddDays(29), Daily(30, 10));
        Assert.Equal((300m, 700m, 0m, 10m), (s.Drawn, s.Remaining, s.Beyond, s.BurnPerDay));
        Assert.Equal(Start.AddDays(29 + 70), s.RunsOut);
        Assert.Equal(0m, s.UnusedAtEnd);
        Assert.True(s.Variance > 0);   // 300 drawn against a plan of about 82 to date
        Assert.Contains("run out on", CreditChargeRules.Warning(s, 30));
        Assert.Null(CreditChargeRules.Warning(s, 0));   // 0 turns it off
    }

    [Fact]
    public void A_commitment_drawn_slowly_is_projected_to_leave_part_unused()
    {
        var s = CreditChargeRules.Project(P3(10000), Start.AddDays(30), Daily(31, 1));   // a month of the term shows the rate
        Assert.Null(s.RunsOut is { } r && r < s.Commitment.EndDate ? r : null);
        Assert.True(s.UnusedAtEnd > 9000);
        Assert.Contains("unused", CreditChargeRules.Warning(s, 30));
    }

    [Fact]
    public void A_commitment_used_up_counts_the_rest_as_pay_as_you_go_beyond_it()
    {
        var s = CreditChargeRules.Project(P3(100), Start.AddDays(29), Daily(30, 10));
        Assert.Equal((100m, 0m, 200m), (s.Drawn, s.Remaining, s.Beyond));
        Assert.Equal(Start.AddDays(9), s.RunsOut);   // the day the draw reached 100, not the last day loaded
        Assert.Contains("ran out on", CreditChargeRules.Warning(s, 30));
        /* Days outside the term, or after the day asked about, draw nothing. */
        var late = CreditChargeRules.Project(P3(1000), Start.AddDays(5), [(Start.AddDays(-1), 500m), (Start.AddDays(3), 10m), (Start.AddDays(9), 10m)]);
        Assert.Equal(10m, late.Drawn);
        /* Before it starts there is nothing to warn about. */
        Assert.Null(CreditChargeRules.Warning(CreditChargeRules.Project(P3(1000), Start.AddDays(-3), []), 30));
    }

    [Fact]
    public void An_idle_commitment_warns_once_a_month_of_its_term_shows_nothing_drawing_on_it()
    {
        Assert.Null(CreditChargeRules.Warning(CreditChargeRules.Project(P3(100000), Start.AddDays(10), []), 30));   // too soon to tell
        var idle = CreditChargeRules.Project(P3(100000), Start.AddDays(45), []);
        Assert.Equal(0m, idle.BurnPerDay);
        Assert.Contains("nothing has drawn on it", CreditChargeRules.Warning(idle, 30));
    }

    [Fact]
    public void Under_a_commitment_buying_packs_for_metered_credits_is_no_saving()
    {
        Assert.NotNull(CostRules.Credits(0, 0, 60000, 600, 200, "USD"));
        Assert.Null(CostRules.Credits(0, 0, 60000, 600, 200, "USD", committed: true));
        Assert.Contains("committed: committed", Source.Read("Data", "CostData.cs"));
    }

    [Fact]
    public void The_schema_the_snapshot_and_the_alert_are_in_place()
    {
        var ops = Read("sql", "Create-CopilotOperationsSchema.sql");
        foreach (var t in new[] { "CREATE TABLE dash.CreditRoute", "CREATE TABLE dash.CreditChargeSnapshot", "CREATE TABLE dash.CreditCommitment", "CREATE OR ALTER PROCEDURE dash.usp_TakeCreditChargeSnapshot",
                                  "GRANT INSERT, UPDATE, DELETE ON dash.CreditRoute TO CopilotInsightsApp", "GRANT INSERT, UPDATE, DELETE ON dash.CreditCommitment TO CopilotInsightsApp",
                                  "CK_DashCreditRoute_Org CHECK (Path NOT IN ('Productivity', 'BusinessApplication') OR OrgId IS NOT NULL)", "IF @lastDay >= @end",
                                  "('CreditChargeRate'", "('CreditCommitmentAlertDays'", "CK_DashCreditRoute_Shared CHECK (Path <> 'EnterpriseShared' OR OrgId IS NULL)",
                                  "WITH (UPDLOCK, HOLDLOCK)", "WHILE @m <= @stop", "GRANT EXECUTE ON dash.usp_TakeCreditChargeSnapshot TO CopilotInsightsApp", "NULLIF(TRY_CAST(" })
            Assert.Contains(t, ops);
        Assert.Contains("EXEC dash.usp_TakeCreditChargeSnapshot;", Read("Invoke-CopilotPipeline.ps1"));
        var notifier = Source.Read("Infrastructure", "Notifier.cs");
        Assert.Contains("await CommitmentAsync();", notifier);
        Assert.Contains("N'retry:commitment'", notifier);
        Assert.Contains("names.Name(p.SkuPartNumber)", notifier);   // the pool alert says product names too
    }

    [Fact]
    public void The_pages_are_in_the_navigation_with_their_help_and_every_field_has_a_tip()
    {
        var nav = Source.Read("Pages", "Nav.cs");
        Assert.Contains("new(\"Credit chargeback\", \"/leadership/credit-chargeback\")", nav);
        Assert.Contains("new(\"Credit commitment\", \"/forecasting/credit-commitment\")", nav);
        foreach (var f in new[] { "environment", "path", "orgId", "costCenter", "owner", "policy", "from", "to", "end", "rate" })
            Assert.True(CopilotInsights.Infrastructure.FieldTips.For("/leadership/credit-chargeback", []).ContainsKey(f), f);
        foreach (var f in new[] { "name", "amount", "currency", "draw", "start", "end", "scope" })
            Assert.True(CopilotInsights.Infrastructure.FieldTips.For("/forecasting/credit-commitment", []).ContainsKey(f), f);
        Assert.StartsWith("What one Copilot Credit is charged back at", CopilotInsights.Infrastructure.FieldTips.For("/leadership/credit-chargeback", [])["rate"]);
        Assert.Contains("Credit chargeback", Read("docs", "OPERATIONS.md"));
        Assert.Contains("| Credit chargeback |", Read("docs", "DASHBOARD.md"));
        Assert.Contains("| Credit commitment |", Read("docs", "DASHBOARD.md"));
    }

    [Fact]
    public void Every_figure_is_explained_and_leads_to_its_detail()
    {
        var page = Source.Read("Pages", "Leadership", "CreditChargeback.cs");
        Assert.Contains("Href: Open(\"/licensing/credit-use\")", page);
        Assert.Equal(5, System.Text.RegularExpressions.Regex.Matches(page, "Why: await WhyAsync").Count);
        var commit = Source.Read("Pages", "Forecasting", "CreditCommitment.cs");
        Assert.Equal(4, System.Text.RegularExpressions.Regex.Matches(commit, "Why: await WhyAsync").Count);
        Assert.Contains("OnPostAlertdaysAsync", commit);
        Assert.Contains("its term overlaps", commit);
        Assert.NotNull(Assumptions.Find("CreditChargeRate"));
        Assert.NotNull(Assumptions.Find("CreditCommitmentAlertDays"));
        foreach (var (file, text) in new[] { (new[] { "Data", "CostData.cs" }, "\"/leadership/credit-chargeback\""), (new[] { "Pages", "Licensing", "CreditUse.cs" }, "Leadership › Credit chargeback"),
                                              (new[] { "Pages", "Leadership", "Reservations.cs" }, "Leadership › Credit chargeback"), (new[] { "Pages", "Agents", "Credits.cs" }, "Leadership › Credit chargeback") })
            Assert.Contains(text, Source.Read(file));
    }

    [Theory]
    [InlineData("2026-09-21", "2026-10-20", "202609")]                 // the last 30 days: one month, not the two they touch
    [InlineData("2026-07-23", "2026-10-20", "202607,202608,202609")]   // the last 90 days: three
    [InlineData("2026-10-01", "2026-10-31", "202610")]                 // a calendar month: itself
    [InlineData("2026-10-14", "2026-10-20", "")]                       // under a month: none, so no return is shown
    [InlineData("2026-01-01", "2026-12-31", "202601,202602,202603,202604,202605,202606,202607,202608,202609,202610,202611,202612")]
    public void A_period_charges_the_calendar_months_whose_last_day_falls_inside_it(string start, string end, string expected) =>
        Assert.Equal(expected, string.Join(",", CopilotInsights.Pages.Licensing.Price.MonthsCharged(DateTime.Parse(start), DateTime.Parse(end))));

    [Fact]
    public void A_license_month_costs_the_price_whole()
    {
        Assert.Equal(18m * 2_718, CopilotInsights.Pages.Licensing.Price.ForLicenseMonths(2_718, 18m));
        Assert.Contains("ForLicenseMonths(await reports.LicenseMonthsAsync(Ctx), price)", Source.Read("Pages", "Leadership", "BoardPack.cs"));
        Assert.Contains("Licensing.Price.ForLicenseMonths(months, price)", Source.Read("Pages", "OperationsReports.cs"));
        Assert.Contains("if (months.Count == 0) return 0;", Source.Read("Data", "OpsReports.cs"));
    }

    [Fact]
    public void A_rate_is_shown_as_it_is_kept_not_rounded_to_cents()
    {
        var page = Source.Read("Pages", "Leadership", "CreditChargeback.cs");
        Assert.Contains("static string Rate(decimal r, string cur) => $\"{r.ToString(\"0.######\", CultureInfo.InvariantCulture)} {cur}\";", page);
        Assert.DoesNotContain("CostRules.Money(v.Rate.Value, cur)", page);
    }
}
