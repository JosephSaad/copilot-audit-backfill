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
    /// the data changes (2.4.12: dropped, not only never read again): a refresh, a finished pipeline run, a license or
    /// directory read, a setting or reference data, midnight UTC. A grant changes no result: who may see what is settled
    /// from the database for each request. 0 turns the cache, and the warm-up after each refresh, off: every page then
    /// reads the database every time.
    /// </summary>
    public int QueryCacheMinutes { get; set; } = 720;

    /// <summary>
    /// The most query results kept in memory on this web server (2.4.12); past it the least recently used go first. A
    /// full build of every report for one tenant keeps about 5,000.
    /// </summary>
    public int QueryCacheEntries { get; set; } = 20000;

    /// <summary>
    /// A folder every web server can read and write, for the keys that protect the forms' anti-forgery tokens and the
    /// message a form leaves for the page after it (2.4.12). Empty, as on one web server: each server keeps its own keys
    /// in its app pool's profile. With several web servers behind a load balancer, set it to a share on all of them, or
    /// a form posted to a server other than the one that drew it is refused. See docs/DASHBOARD.md, Several web servers.
    /// </summary>
    public string? DataProtectionKeysPath { get; set; }

    /// <summary>
    /// The thumbprint of a certificate, in the Local Machine's Personal store on every web server, that protects the keys
    /// in <see cref="DataProtectionKeysPath"/>. Empty: they are protected for the account the site runs as (DPAPI-NG),
    /// which must then be the same domain account or group managed service account on every server.
    /// </summary>
    public string? DataProtectionCertificate { get; set; }

    /// <summary>
    /// Build every report page for the whole organization in the background whenever the data changes, so its queries
    /// are in the cache before anyone opens it (2.4.7), and again when they expire (2.4.12). On by default; off for a
    /// test copy of the site. Each web server builds its own, as each keeps its own cache.
    /// </summary>
    public bool WarmPages { get; set; } = true;

    /// <summary>Seconds after the site starts before the first warm-up.</summary>
    public int WarmStartDelaySeconds { get; set; } = 20;

    /// <summary>
    /// The folder where Invoke-InsightsPrecache.ps1 leaves a request to build every report page again, and the site
    /// says how the build went (2.5.1, see <see cref="PageWarmer"/>). Empty: the site's own precache folder, which
    /// Install-CopilotInsights.ps1 lets the app pool write.
    /// </summary>
    public string? PrecacheFolder { get; set; }

    /// <summary>
    /// The background sender: alerts and scheduled digests (Admin › Alerts and digests). On by
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
