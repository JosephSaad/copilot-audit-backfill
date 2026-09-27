namespace CopilotInsights.Infrastructure;

/// <summary>Configuration, from appsettings.json and appsettings.Production.json.</summary>
public sealed class InsightsOptions
{
    public const string Section = "Insights";

    /// <summary>The audit database (with the bi and dash schemas). Integrated security by default.</summary>
    public string ConnectionString { get; set; } = "Server=localhost;Database=CopilotAudit;Integrated Security=true;Encrypt=true;TrustServerCertificate=false";

    /// <summary>
    /// Principals who are always administrators, whatever the database says: UPNs,
    /// DOMAIN\user names or DOMAIN\Group names. Set by the installer; the way the
    /// first admin gets in, and the way back in if the grants go wrong.
    /// </summary>
    public string[] BootstrapAdmins { get; set; } = [];

    /// <summary>
    /// Windows sign-in names mapped to UPNs, for accounts the directory lookup
    /// cannot resolve (a local or Entra-joined account, a test machine). Checked like
    /// every other way in: a person whose directory record carries a SID cannot be
    /// mapped to from any other account.
    /// </summary>
    public Dictionary<string, string> IdentityMap { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Resolve DOMAIN\user to a UPN through Active Directory. On by default; off where there is no domain.</summary>
    public bool ResolveUpnFromDirectory { get; set; } = true;


    /// <summary>
    /// The background sender: alerts and scheduled digests (Admin > Alerts and digests). On by
    /// default; with several web servers it runs on one at a time. Off for a test copy of the site.
    /// </summary>
    public bool Notifications { get; set; } = true;

    /// <summary>
    /// Several Microsoft 365 tenants, each with its own Copilot database: a name and a connection
    /// string each. The first is shown by default; viewers switch in the header. Empty for one
    /// tenant, from ConnectionString.
    /// </summary>
    public List<TenantOption> Tenants { get; set; } = [];

    /// <summary>How long after start-up the first round of checks waits, in seconds.</summary>
    public int NotifierStartDelaySeconds { get; set; } = 60;
}
