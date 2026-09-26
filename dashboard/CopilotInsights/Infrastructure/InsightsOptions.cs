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
    /// cannot resolve (a local or Entra-joined account, a test machine).
    /// </summary>
    public Dictionary<string, string> IdentityMap { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Resolve DOMAIN\user to a UPN through Active Directory. On by default; off where there is no domain.</summary>
    public bool ResolveUpnFromDirectory { get; set; } = true;

    /// <summary>
    /// Development only: lets an administrator view the dashboard as someone else
    /// (?as=upn) to check what they would see. Ignored outside Development.
    /// </summary>
    public bool AllowViewAs { get; set; }
}
