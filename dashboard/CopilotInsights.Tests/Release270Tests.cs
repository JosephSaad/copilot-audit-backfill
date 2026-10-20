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
