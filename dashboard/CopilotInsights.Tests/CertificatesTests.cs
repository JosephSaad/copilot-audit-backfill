using CopilotInsights.Infrastructure;
using Xunit;

namespace CopilotInsights.Tests;

/// <summary>
/// The two certificates the pipeline signs in with, the audit app's and the agent workbook app's: what Admin > Health
/// says of each, and that each is watched under a state of its own.
/// </summary>
public class CertificatesTests
{
    static readonly DateTime Now = new(2026, 10, 7, 12, 0, 0, DateTimeKind.Utc);

    static PipelineCertificate Cert(string purpose, double daysLeft, bool found = true) => new()
    {
        Purpose = purpose, Thumbprint = "A1B2C3", Found = found, Subject = "CN=test", Issuer = "CN=issuer", Store = "LocalMachine\\My",
        NotBeforeUtc = Now.AddDays(-100), ExpiresUtc = found ? Now.AddDays(daysLeft) : null, ClientId = "app", TenantId = "contoso.onmicrosoft.com",
        Host = "JOBSRV01", SeenUtc = Now.AddHours(-10),
    };

    [Theory]
    [InlineData(245.5, "status-ok", "245 days left")]
    [InlineData(21.2, "status-warn", "21 days left")]
    [InlineData(30, "status-warn", "30 days left")]      // the warning day itself
    [InlineData(1.9, "status-warn", "1 day left")]
    [InlineData(0, "status-bad", "Expired on 2026-10-07")]
    [InlineData(-12, "status-bad", "Expired on 2026-09-25")]
    public void A_certificate_says_when_it_expires_and_turns_amber_inside_the_warning_then_red(double daysLeft, string css, string says)
    {
        var (text, cls) = Certificates.Status(Cert(Certificates.Purview, daysLeft), 30, Now);
        Assert.Equal(css, cls);
        Assert.Contains(says, text);
    }

    [Fact]
    public void A_certificate_the_settings_name_and_the_job_server_does_not_hold_is_red_and_says_where_it_looked()
    {
        var (text, cls) = Certificates.Status(Cert(Certificates.AgentWorkbook, 0, found: false), 30, Now);
        Assert.Equal("status-bad", cls);
        Assert.StartsWith("Not on the job server", text);
        Assert.Contains("LocalMachine\\My or CurrentUser\\My on JOBSRV01", text);
        /* The thumbprint is forty characters with no break in them: it is in the details table, not on the card. */
        Assert.DoesNotContain("A1B2C3", text);
    }

    [Fact]
    public void Each_certificate_has_its_own_name_purpose_way_to_renew_and_alert_state()
    {
        Assert.NotEqual(Certificates.Name(Certificates.Purview), Certificates.Name(Certificates.AgentWorkbook));
        Assert.Contains("Purview", Certificates.Name(Certificates.Purview));
        Assert.Contains("SharePoint", Certificates.Name(Certificates.AgentWorkbook));
        Assert.Contains("nightly load stops", Certificates.UsedFor(Certificates.Purview));
        Assert.Contains("agent workbook", Certificates.UsedFor(Certificates.AgentWorkbook));
        Assert.Contains("Install-CopilotJobServer.ps1 -RegisterApp", Certificates.Renew(Certificates.Purview));
        Assert.Contains("AgentWorkbookCertificateThumbprint", Certificates.Renew(Certificates.AgentWorkbook));
        /* The audit app keeps the state it had when it was the only certificate, so an upgrade does not send its warning again. */
        Assert.Equal("certificate", Certificates.StateKey(Certificates.Purview));
        Assert.Equal("certificate:AgentWorkbook", Certificates.StateKey(Certificates.AgentWorkbook));
    }

    [Fact]
    public void The_details_carry_every_field_recorded_and_say_not_found_for_the_store_of_a_missing_one()
    {
        var d = Certificates.Details(Cert(Certificates.Purview, 100), t => $"{t:yyyy-MM-dd HH:mm}").ToDictionary(x => x.Field, x => x.Value);
        Assert.Equal("CN=test", d["Subject"]);
        Assert.Equal("CN=issuer", d["Issued by"]);
        Assert.Equal("A1B2C3", d["Thumbprint"]);
        Assert.Equal("2026-06-29", d["Valid from"]);
        Assert.Equal("2027-01-15 12:00 UTC", d["Expires"]);
        Assert.Equal("app", d["App registration (client id)"]);
        Assert.Equal("contoso.onmicrosoft.com", d["Tenant"]);
        Assert.Equal("LocalMachine\\My", d["Certificate store"]);
        Assert.Equal("JOBSRV01", d["Job server"]);
        Assert.Equal("2026-10-07 02:00", d["Last checked"]);
        var missing = Certificates.Details(Cert(Certificates.AgentWorkbook, 0, found: false), t => "").ToDictionary(x => x.Field, x => x.Value);
        Assert.Equal("Not found on the job server", missing["Certificate store"]);
        Assert.Null(missing["Expires"]);
        /* The two certificates' details line up row for row, for the table that sets them side by side. */
        Assert.Equal(d.Keys, missing.Keys);
    }
}
