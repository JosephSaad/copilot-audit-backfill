namespace CopilotInsights.Infrastructure;

/// <summary>
/// A certificate the pipeline signs in with, as the job server held it at the start of its last run
/// (audit.PipelineCertificate). There are two: the one for the app that reads the audit log from Microsoft Purview,
/// and, where the agent workbook comes from SharePoint Online, the one for the app that reads that site.
/// </summary>
public sealed class PipelineCertificate
{
    public string Purpose { get; set; } = "";
    public string Thumbprint { get; set; } = "";
    /// <summary>False when the settings file names a thumbprint the job server holds no certificate for.</summary>
    public bool Found { get; set; }
    public string? Subject { get; set; }
    public string? Issuer { get; set; }
    public DateTime? NotBeforeUtc { get; set; }
    public DateTime? ExpiresUtc { get; set; }
    public string? Store { get; set; }
    public string? ClientId { get; set; }
    public string? TenantId { get; set; }
    public string? Host { get; set; }
    public DateTime? SeenUtc { get; set; }
}

/// <summary>What the Health page and the expiry alert say about each certificate. Pure but for <see cref="ReadAsync"/>, for the tests.</summary>
public static class Certificates
{
    public const string Purview = "Purview";
    public const string AgentWorkbook = "AgentWorkbook";
    public const string Foundry = "Foundry";

    /// <summary>The certificate's name on the page and in an alert.</summary>
    public static string Name(string purpose) => purpose switch
    {
        Purview => "Purview audit app certificate",
        AgentWorkbook => "SharePoint agent workbook app certificate",
        Foundry => "Foundry usage app certificate",
        _ => purpose + " certificate",
    };

    /// <summary>What stops when it expires.</summary>
    public static string UsedFor(string purpose) => purpose switch
    {
        Purview => "Signs the job server in to Microsoft Graph, to read Copilot's audit records from Microsoft Purview and the licenses. When it expires, the nightly load stops.",
        AgentWorkbook => "Signs the job server in to SharePoint Online, to fetch the agent workbook. When it expires, the agent inventory stops being updated and the nightly run fails at that step.",
        Foundry => "Signs the job server in to Azure, to read the usage and cost of Foundry and Azure OpenAI deployments. When it expires, those tables stop being updated; the rest of the nightly run goes on.",
        _ => "Used by the pipeline.",
    };

    /// <summary>How to renew it.</summary>
    public static string Renew(string purpose) => purpose switch
    {
        Purview => "Renew it: run tools\\Install-CopilotJobServer.ps1 -RegisterApp on the job server as an administrator (a new certificate, uploaded to the app, its thumbprint in the settings).",
        AgentWorkbook => "Renew it: put a new certificate in the job server's LocalMachine\\My, readable by the task's account, upload its public key to the workbook's app registration, and set AgentWorkbookCertificateThumbprint in copilot-audit.settings.psd1.",
        Foundry => "Renew it: put a new certificate in the job server's LocalMachine\\My, readable by the task's account, upload its public key to the app registration, and set FoundryCertificateThumbprint in copilot-audit.settings.psd1.",
        _ => "Renew it on the job server.",
    };

    /// <summary>The alert state's key: the audit app keeps the key it had when it was the only one.</summary>
    public static string StateKey(string purpose) => purpose == Purview ? "certificate" : "certificate:" + purpose;

    public static double? DaysLeft(PipelineCertificate c, DateTime nowUtc) => c.ExpiresUtc is { } e ? (e - nowUtc).TotalDays : null;

    /// <summary>One line on where the certificate stands, and the card's color.</summary>
    public static (string Text, string Css) Status(PipelineCertificate c, int warnDays, DateTime nowUtc)
    {
        if (!c.Found)
            return ($"Not on the job server: no certificate with the thumbprint the settings name is in LocalMachine\\My or CurrentUser\\My{(c.Host is null ? "" : " on " + c.Host)}. Sign-in with it will fail.", "status-bad");
        if (DaysLeft(c, nowUtc) is not { } left) return ("Its expiry date was not recorded.", "status-warn");
        var when = $"{c.ExpiresUtc:yyyy-MM-dd}";
        return left <= 0 ? ($"Expired on {when}.", "status-bad")
            : ($"Expires {when}: {Math.Floor(left):0} {(Math.Floor(left) == 1 ? "day" : "days")} left.", left <= warnDays ? "status-warn" : "status-ok");
    }

    /// <summary>The certificate's details, field by field, for the Health page's table. A field with nothing recorded is left out by the caller.</summary>
    public static IReadOnlyList<(string Field, string? Value)> Details(PipelineCertificate c, Func<DateTime?, string> when) =>
    [
        ("Used for", UsedFor(c.Purpose)),
        ("Subject", c.Subject),
        ("Issued by", c.Issuer),
        ("Thumbprint", c.Thumbprint),
        ("Valid from", c.NotBeforeUtc is null ? null : $"{c.NotBeforeUtc:yyyy-MM-dd}"),
        ("Expires", c.ExpiresUtc is null ? null : $"{c.ExpiresUtc:yyyy-MM-dd HH:mm} UTC"),
        ("App registration (client id)", c.ClientId),
        ("Tenant", c.TenantId),
        ("Certificate store", c.Found ? c.Store : "Not found on the job server"),
        ("Job server", c.Host),
        ("Last checked", c.SeenUtc is null ? null : when(c.SeenUtc)),
        ("To renew", Renew(c.Purpose)),
    ];

    /// <summary>
    /// The certificates the pipeline last recorded, the audit app's first. From audit.PipelineCertificate; over a database
    /// from before that table, or one no run has written to yet, the one certificate the runs recorded with themselves
    /// (audit.PipelineRun), so the page and the alert go on as they were until the job server is upgraded.
    /// </summary>
    public static async Task<List<PipelineCertificate>> ReadAsync(Db db)
    {
        if (await db.ScalarAsync<int>("SELECT CASE WHEN OBJECT_ID(N'audit.PipelineCertificate', N'U') IS NULL THEN 0 ELSE 1 END") == 1)
        {
            var rows = await db.QueryAsync<PipelineCertificate>(@"
SELECT Purpose, Thumbprint, Found, Subject, Issuer, NotBeforeUtc, ExpiresUtc, Store, ClientId, TenantId, Host, SeenUtc
FROM audit.PipelineCertificate
ORDER BY CASE Purpose WHEN 'Purview' THEN 0 ELSE 1 END, Purpose;");
            if (rows.Count > 0) return rows;
        }
        if (await db.ScalarAsync<int>("SELECT CASE WHEN COL_LENGTH(N'audit.PipelineRun', N'CertificateExpiresUtc') IS NULL THEN 0 ELSE 1 END") == 0) return [];
        return await db.QueryAsync<PipelineCertificate>(@"
SELECT TOP (1) 'Purview' AS Purpose, CertificateThumbprint AS Thumbprint, CAST(1 AS BIT) AS Found, CertificateSubject AS Subject,
       CertificateExpiresUtc AS ExpiresUtc, Host, StartedUtc AS SeenUtc
FROM audit.PipelineRun WHERE CertificateExpiresUtc IS NOT NULL ORDER BY RunId DESC;");
    }
}
