using System.Security.Cryptography;
using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;
using Microsoft.Extensions.Logging.Abstractions;

namespace CopilotInsights.Infrastructure;

/// <summary>
/// Where the site keeps the keys that protect its forms' anti-forgery tokens and the message a form leaves for the page
/// after it (2.4.12). Nothing set this: each web server kept its own keys in its app pool's profile, so with several web
/// servers behind a load balancer, a form drawn by one and posted to another was refused (HTTP 400), and the message
/// after a save was lost when the page after it came from the other. With Insights:DataProtectionKeysPath set, every
/// server keeps one set of keys there, under one application name, protected with Insights:DataProtectionCertificate or,
/// without one, for the account the site runs as. Without it nothing changes, which is right for one web server.
/// </summary>
/// <remarks>
/// The protection is tried when the site starts, and a site that cannot protect its keys does not start, with the
/// reason in its log: keys that cannot be written would refuse every form instead, with nothing to say why.
/// </remarks>
public static class KeyRing
{
    public const string ApplicationName = "CopilotInsights";

    /// <returns>What was set up, for the log at start-up; null when nothing was (one web server).</returns>
    public static string? Configure(IServiceCollection services, InsightsOptions options, bool protect = true)
    {
        if (string.IsNullOrWhiteSpace(options.DataProtectionKeysPath)) return null;
        var folder = new DirectoryInfo(options.DataProtectionKeysPath.Trim());
        var dp = services.AddDataProtection().SetApplicationName(ApplicationName).PersistKeysToFileSystem(folder);
        if (!protect) return $"keys in {folder.FullName}, not protected";
        if (!string.IsNullOrWhiteSpace(options.DataProtectionCertificate))
        {
            var thumbprint = options.DataProtectionCertificate.Trim();
            using var certificate = new CertificateResolver().ResolveCertificate(thumbprint);
            if (certificate is null || !certificate.HasPrivateKey)
                throw new InvalidOperationException($"Insights:DataProtectionCertificate names {thumbprint}, but no valid certificate with that thumbprint and its private key is in " +
                    "Local Computer > Personal (or the account the site runs as cannot read its private key). Install it on every web server, or leave the setting empty.");
            dp.ProtectKeysWithCertificate(thumbprint);
            return $"keys in {folder.FullName}, protected with the certificate {thumbprint}";
        }
        if (!OperatingSystem.IsWindows()) return $"keys in {folder.FullName}, not protected (not Windows)";
        var account = System.Security.Principal.WindowsIdentity.GetCurrent();
        var rule = "SID=" + account.User!.Value;
        try { new DpapiNGXmlEncryptor(rule, DpapiNGProtectionDescriptorFlags.None, NullLoggerFactory.Instance).Encrypt(new XElement("probe")); }
        catch (CryptographicException ex)
        {
            throw new InvalidOperationException($"Insights:DataProtectionKeysPath is set, but the keys there cannot be protected for {account.Name}, the account the site runs as: " +
                "that needs a domain account or group managed service account. Name a certificate in Insights:DataProtectionCertificate instead.", ex);
        }
        dp.ProtectKeysWithDpapiNG(rule, DpapiNGProtectionDescriptorFlags.None);
        return $"keys in {folder.FullName}, protected for {account.Name}";
    }
}
