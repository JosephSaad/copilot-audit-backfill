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
    /// How long a report page's query results are kept in memory, in minutes (2.4.7). They are dropped sooner whenever
    /// the data changes: a refresh, a pipeline run, a setting, a grant. 0 turns the cache, and the warm-up after each
    /// refresh, off: every page then reads the database every time.
    /// </summary>
    public int QueryCacheMinutes { get; set; } = 720;

    /// <summary>
    /// Build every report page for the whole organization in the background whenever the data changes, so its queries
    /// are in the cache before anyone opens it (2.4.7). On by default; off for a test copy of the site.
    /// </summary>
    public bool WarmPages { get; set; } = true;

    /// <summary>Seconds after the site starts before the first warm-up.</summary>
    public int WarmStartDelaySeconds { get; set; } = 20;

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
