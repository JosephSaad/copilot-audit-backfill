using System.Text.RegularExpressions;
using CopilotInsights.Pages.Leadership;
using CopilotInsights.Tests.Review2412A;
using Xunit;

namespace CopilotInsights.Tests;

/// <summary>2.7.0: the adversarial review of 2.6.7 and 2.6.8 (reservations): scope, the register, the paste, the snapshot.</summary>
public class ReservationReviewTests
{
    static string Root()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(d.FullName, "CHANGELOG.md"))) d = d.Parent!;
        return d.FullName;
    }

    [Fact]
    public void Every_list_of_snapshots_and_holders_is_the_viewers_part_of_the_organization()
    {
        /* A leader saw the whole bank's month ends, and a Licensing holder of part of it every organization's people. */
        Assert.Contains("data.SnapshotsAsync(Ctx)", Source.Read("Pages", "Leadership", "Reservations.cs"));
        Assert.Contains("SnapshotsAsync(ReportContext c)", Source.Read("Data", "ReservationData.cs"));
        Assert.Contains("{Scoped(c, \"s.LeaderUpn\")};\", c.Args());", Source.Read("Data", "ReservationData.cs"));
        Assert.Contains("FROM bi.FactLicensedDay AS ld {Ctx.Scope(\"ld.UserKey\")}", Source.Read("Pages", "Licensing", "ReservationHolders.cs"));
    }

    [Fact]
    public void A_pasted_register_is_checked_whole_before_any_row_is_added()
    {
        /* A quoted field keeps its comma, the header is skipped, and every line is checked first. */
        var rows = ReservationsModel.ParsePaste("OrgId,Organization,Leader,Reserved,From,CostCenter,To\n" +
                                                "LOB-01,\"Retail Banking, East\",Lead@Contoso.com,7403,2027-01-01,CC-100\n" +
                                                "LOB-02,Mortgages,,15,2027-01-01,,2027-12-31");
        Assert.Equal(2, rows.Count);
        Assert.Equal("Retail Banking, East", rows[0].Name);
        Assert.Equal("lead@contoso.com", rows[0].LeaderUpn);
        Assert.Null(rows[1].LeaderUpn);
        Assert.Equal(new DateTime(2027, 12, 31), rows[1].To);
        /* A first line that is a row, though its name has a comma, is not taken for a header. */
        Assert.Single(ReservationsModel.ParsePaste("LOB-01,\"Retail, East\",a@c.com,10,2027-01-01"));
        /* An end before the start on line 3 refuses the whole paste, naming the line. */
        var bad = Assert.Throws<ArgumentException>(() => ReservationsModel.ParsePaste("LOB-01,A,,1,2027-01-01\nLOB-02,B,,1,2027-01-01\nLOB-03,C,,1,2027-06-01,,2027-01-01"));
        Assert.StartsWith("line 3:", bad.Message);
        Assert.Throws<ArgumentException>(() => ReservationsModel.ParsePaste("LOB-01,A,,20000000,2027-01-01"));
        Assert.Throws<ArgumentException>(() => ReservationsModel.Check("~unmapped", "A", null, null, 1, new DateTime(2027, 1, 1), null, null));
    }

    [Fact]
    public void A_change_ends_the_open_row_and_a_paste_is_one_transaction()
    {
        var page = Source.Read("Pages", "Leadership", "Reservations.cs");
        Assert.Contains("UPDATE r SET EffectiveTo = DATEADD(DAY, -1, n.[From])", page);
        Assert.Contains("SET XACT_ABORT ON;", page);
        Assert.Contains("COMMIT TRANSACTION;", page);
        Assert.Contains("overlaps a row that starts on or after it", page);
        Assert.Contains("OnPostEndreserveAsync", page);
        /* A database error during a change is audited and said. */
        Assert.Contains("catch (Microsoft.Data.SqlClient.SqlException ex) { done = \"Failed: \" + ex.Message; }", Source.Read("Pages", "InsightsPage.cs"));
    }

    [Fact]
    public void The_month_end_snapshot_waits_for_late_licenses_needs_a_reservation_and_is_whole()
    {
        var sql = File.ReadAllText(Path.Combine(Root(), "sql", "Create-CopilotOperationsSchema.sql"));
        var proc = sql[sql.IndexOf("CREATE OR ALTER PROCEDURE dash.usp_TakeReservationSnapshot", StringComparison.Ordinal)..];
        proc = proc[..proc.IndexOf("END;');", StringComparison.Ordinal)];
        Assert.Contains("IF @lastDay < DATEADD(DAY, 2, @AsOf) RETURN;", proc);
        Assert.Contains("IF NOT EXISTS (SELECT 1 FROM dash.Reservation WHERE EffectiveFrom <= @AsOf AND (EffectiveTo IS NULL OR EffectiveTo >= @AsOf)) RETURN;", proc);
        Assert.Contains("SET XACT_ABORT ON;", proc);
        Assert.True(proc.IndexOf("BEGIN TRANSACTION;", StringComparison.Ordinal) < proc.IndexOf("INSERT INTO dash.ReservationSnapshot", StringComparison.Ordinal));
        Assert.Contains("COMMIT TRANSACTION;", proc);
    }

    [Fact]
    public void The_reconciliation_counts_the_add_on_and_a_departed_leader_is_an_exception()
    {
        var page = Source.Read("Pages", "Leadership", "Reservations.cs");
        Assert.Contains("!p.SkuPartNumber.Contains(\"STUDIO\", StringComparison.OrdinalIgnoreCase)", page);
        Assert.Contains("!suiteSkus.Contains(p.SkuPartNumber)", page);
        Assert.Contains("MidpointRounding.AwayFromZero", page);
        var data = Source.Read("Data", "ReservationData.cs");
        Assert.Contains("LEFT JOIN dash.Person AS l ON l.Upn = r.LeaderUpn", data);
        Assert.Contains("(l.Upn IS NULL OR l.AccountEnabled = 0)", data);
        /* No subquery inside an aggregate in the reservation SQL either (error 130). */
        foreach (var file in new[] { data, page, File.ReadAllText(Path.Combine(Root(), "sql", "Create-CopilotOperationsSchema.sql")) })
            Assert.DoesNotMatch(new Regex(@"(COUNT|SUM|MAX|MIN)\((CASE WHEN[^)]*(EXISTS|SELECT)|\(SELECT)", RegexOptions.IgnoreCase), file);
    }
}

/// <summary>2.7.0: help on every field, and the Purview additions: agent management, Teams AI notes, DLP, Copilot by product.</summary>
public class UsabilityAndPurviewTests
{
    static string Root()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(d.FullName, "CHANGELOG.md"))) d = d.Parent!;
        return d.FullName;
    }
    static string Read(params string[] p) => File.ReadAllText(Path.Combine([Root(), .. p]));

    [Fact]
    public void Every_field_takes_its_tip_from_its_setting_or_the_fixed_list()
    {
        var tips = CopilotInsights.Infrastructure.FieldTips.For("/admin/assumptions", [("HourlyCost", "2.6.6: What an hour is worth."), ("NoText", null)]);
        Assert.Equal("What an hour is worth.", tips["HourlyCost"]);   // the release it was added in is not shown
        Assert.False(tips.ContainsKey("NoText"));
        Assert.StartsWith("What one unit costs a month", tips["price"]);   // this page's own meaning of price
        Assert.StartsWith("What one seat costs a month under this agreement", CopilotInsights.Infrastructure.FieldTips.For("/forecasting/budget", [])["price"]);
        /* After the field: a label's clicks go to its first control, which an icon before it would take. */
        Assert.Contains("value=\\\"{E(value)}\\\" size=\\\"{size}\\\"/>{Tip(name, label)}</label>", Source.Read("Pages", "InsightsPage.cs"));
        /* Every setting the installer seeds says what it is, so its field has a tip. */
        foreach (var m in Regex.Matches(Read("sql", "Create-CopilotDashboardSchema.sql"), @"\('(\w+)',\s*(?:N'[^']*'|CAST\(NULL[^)]*\)),\s*N'([^']*)'\)").Cast<Match>())
            Assert.False(string.IsNullOrWhiteSpace(m.Groups[2].Value), m.Groups[1].Value);
    }

    [Fact]
    public void Administrators_actions_on_agents_ride_the_license_load_and_stay_out_of_license_events()
    {
        foreach (var op in new[] { "BlockedAgent", "UnblockedAgent", "DeployedAgent", "RemovedAgent", "DeletedAgent", "UpdatedAgent", "UpdatedTenantSettings" })
        {
            Assert.Contains($"'{op}'", Read("Invoke-CopilotPipeline.ps1"));
            Assert.Contains($"N'{op}'", Read("sql", "Create-AgentEventProcedure.sql"));
            Assert.Contains($"N'{op}'", Read("sql", "Create-LicenseEventProcedure.sql"));
        }
        Assert.Contains("a.Operation NOT IN (N'BlockedAgent'", Read("Install-CopilotAudit.ps1"));
        Assert.Contains("What administrators did to agents", Source.Read("Pages", "Agents", "Build.cs"));
    }

    [Fact]
    public void Teams_AI_notes_and_DLP_are_shredded_rolled_up_and_shown()
    {
        var shred = Read("sql", "Create-ActivityProcedure.sql");
        Assert.Contains("WHEN a.RecordType LIKE N'teamCopilot%' THEN N'TeamsAI'", shred);
        Assert.Contains("INSERT INTO audit.DlpMatch", shred);
        var rollup = Read("sql", "Create-CopilotActivityRollup.sql");
        Assert.Contains("INSERT INTO audit.ActivityUserDay", rollup);
        Assert.Contains("INSERT INTO audit.DlpDay", rollup);
        Assert.Contains("AND a.Product IN (''Power BI'', ''Power Apps'')", rollup);
        Assert.Contains("'TeamCopilotInteraction'", Read("Invoke-CopilotPipeline.ps1"));
        Assert.Contains("Teams meeting AI notes, Power BI and Power Apps", Source.Read("Pages", "Apps", "Apps.cs"));
        Assert.Contains("Data loss prevention matches", Source.Read("Pages", "Compliance", "Compliance.cs"));
        Assert.Contains("Copilot by product", Source.Read("Pages", "Governance", "Governance.cs"));
        /* No file or message is named: the DLP tables keep no object. */
        Assert.DoesNotContain("ObjectId", Read("sql", "Create-ActivitySchema.sql")[Read("sql", "Create-ActivitySchema.sql").IndexOf("audit.DlpMatch", StringComparison.Ordinal)..]);
    }

    /* The adversarial review of 2.7.0's own additions. */

    [Fact]
    public void Only_a_policy_match_is_a_DLP_record_and_a_false_positive_is_undone()
    {
        /* An endpoint's DLPEndpoint records also carry its ordinary file activity, audited with no rule matched. */
        var shred = Read("sql", "Create-ActivityProcedure.sql");
        Assert.Contains("THEN CASE WHEN pc.Policies > 0 OR t.MatchPolicyName IS NOT NULL THEN N'DLP' ELSE N'DLPOther' END", shred);
        Assert.Contains("'$.PolicyMatchInfo.PolicyName'", shred);
        var rollup = Read("sql", "Create-CopilotActivityRollup.sql");
        Assert.Contains("COUNT(CASE WHEN m.Operation IN (N''DlpRuleUndo'', N''DlpInfo'') THEN 1 END)", rollup);
        Assert.Contains("ORDER BY SUM(CAST(m.Matches AS BIGINT)) DESC", Source.Read("Pages", "Compliance", "Compliance.cs"));
    }

    [Fact]
    public void A_short_reservation_inside_a_longer_one_is_refused_and_a_rate_keeps_two_decimals()
    {
        var page = Source.Read("Pages", "Leadership", "Reservations.cs");
        Assert.Contains("falls inside a row that runs past its end", page);
        Assert.Contains("(r.EffectiveTo IS NULL OR r.EffectiveTo > n.[To])", page);
        Assert.Contains("decimal.Round(r, 2) != r", page);
        Assert.Contains("Input(\"end\", \"End on\"", page);
    }

    [Fact]
    public void Administrators_are_not_makers_and_are_named_only_with_the_whole_organization_in_view()
    {
        var build = Source.Read("Pages", "Agents", "Build.cs");
        Assert.Contains("COUNT(DISTINCT e.ActorUpn) FROM bi.FactAgentEvent AS e {scope} WHERE e.DateKey BETWEEN @k0 AND @k1 AND e.Action NOT IN ('Blocked'", build);
        Assert.Contains("var byShown = named && Ctx.Root is null;", build);
    }

    [Fact]
    public void A_fields_tip_is_its_pages_where_the_name_is_shared()
    {
        var reference = CopilotInsights.Infrastructure.FieldTips.For("/admin/reference", []);
        Assert.False(reference.ContainsKey("category"));
        Assert.False(CopilotInsights.Infrastructure.FieldTips.For("/forecasting/budget", []).TryGetValue("start", out var s) && s.Contains("progress"));
        Assert.StartsWith("A manager's sign-in (UPN): the digest", CopilotInsights.Infrastructure.FieldTips.For("/admin/notifications", [])["root"]);
        var res = CopilotInsights.Infrastructure.FieldTips.For("/leadership/reservations", []);
        Assert.StartsWith("Why the Technology pool funds it", res["category"]);
        Assert.StartsWith("The last day this reservation is in effect", res["end"]);
        Assert.Contains("progress", CopilotInsights.Infrastructure.FieldTips.For("/admin/assumptions", [])["start"]);
    }
}