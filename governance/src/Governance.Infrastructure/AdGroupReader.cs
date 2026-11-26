using System.DirectoryServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using Governance.Application.Integrations;

namespace Governance.Infrastructure;

/// <summary>
/// Seat groups from on-premises Active Directory over LDAP, as the account the worker (or the web site) runs as: the
/// group found by name in the domain the seat settings name, its direct members read in ranges (a group of any size),
/// and each member looked up in the domain its distinguished name is in, 50 at a time. Read only; nothing is written.
/// Any authenticated account in a trusted domain can read these by default.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AdGroupReader : IAdGroupApi
{
    const int Chunk = 50;

    static string Root(string? domain) => string.IsNullOrWhiteSpace(domain) ? "LDAP://" : $"LDAP://{domain.Trim()}/";

    /// <summary>A distinguished name as an ADsPath segment: a slash is the path separator there.</summary>
    static string PathOf(string dn) => dn.Replace("/", "\\/", StringComparison.Ordinal);

    static string NamingContext(string? domain)
    {
        using var rootDse = new DirectoryEntry(Root(domain) + "RootDSE");
        return rootDse.Properties["defaultNamingContext"].Value as string
            ?? throw new InvalidOperationException($"The AD domain {domain ?? "of this server"} gave no naming context.");
    }

    static string? Str(SearchResult r, string name) => r.Properties.Contains(name) && r.Properties[name].Count > 0 ? r.Properties[name][0]?.ToString() : null;

    public Task<AdGroupData?> FindGroupAsync(string name, string? domain, CancellationToken ct) => Task.Run(() =>
    {
        using var root = new DirectoryEntry(Root(domain) + PathOf(NamingContext(domain)));
        using var s = new DirectorySearcher(root, AdGroups.GroupFilter(name), ["objectGUID", "distinguishedName", "sAMAccountName"]) { PageSize = 100, SizeLimit = 0 };
        using var results = s.FindAll();
        var found = results.Cast<SearchResult>().ToList();
        if (found.Count == 0) return (AdGroupData?)null;
        var bare = AdGroups.BareName(name);
        var exact = found.Where(r => string.Equals(Str(r, "samaccountname"), bare, StringComparison.OrdinalIgnoreCase)).ToList();
        var best = exact.Count > 0 ? exact[0] : found[0];
        var duplicate = exact.Count > 1 || (exact.Count == 0 && found.Count > 1);
        return new AdGroupData(new Guid((byte[])best.Properties["objectguid"][0]), Str(best, "distinguishedname") ?? "", Str(best, "samaccountname") ?? bare, duplicate);
    }, ct);

    public Task<AdGroupMembersData> MembersAsync(AdGroupData group, string? domain, CancellationToken ct) => Task.Run(() =>
    {
        var dns = MemberDns(group, domain, ct);
        var users = new List<AdUserData>();
        var groups = 0;
        var other = 0;
        var home = AdGroups.NamingContextOf(group.DistinguishedName) ?? "";
        /* The group's own domain in one paged search (memberOf is the member list seen from the member's side): a group
           of 20,000 is some 20 pages, not 400 lookups. What it does not return (members in other domains) is looked up
           below, 50 at a time. */
        var left = new HashSet<string>(dns, StringComparer.OrdinalIgnoreCase);
        if (home.Length > 0 && left.Count > 0)
        {
            using var root = new DirectoryEntry(Root(domain) + PathOf(home));
            using var s = new DirectorySearcher(root, $"(memberOf={AdGroups.Escape(group.DistinguishedName)})",
                ["distinguishedName", "objectClass", "objectSid", "userPrincipalName", "mail"]) { PageSize = 1000, SizeLimit = 0 };
            using var results = s.FindAll();
            foreach (SearchResult r in results)
            {
                ct.ThrowIfCancellationRequested();
                if (Str(r, "distinguishedname") is not { } dn || !left.Remove(dn)) continue;
                Classify(r, users, ref groups, ref other);
            }
        }
        foreach (var inDomain in left.GroupBy(d => AdGroups.NamingContextOf(d) ?? "", StringComparer.OrdinalIgnoreCase))
        {
            if (inDomain.Key.Length == 0) { other += inDomain.Count(); continue; }
            /* The group's own domain through the server named in the settings; another domain of the forest by its DNS name. */
            var server = string.Equals(inDomain.Key, home, StringComparison.OrdinalIgnoreCase) ? domain : AdGroups.DnsDomainOf(inDomain.First());
            var resolved = 0;
            try
            {
                using var root = new DirectoryEntry(Root(server) + PathOf(inDomain.Key));
                foreach (var chunk in inDomain.Chunk(Chunk))
                {
                    ct.ThrowIfCancellationRequested();
                    var filter = "(|" + string.Concat(chunk.Select(d => $"(distinguishedName={AdGroups.Escape(d)})")) + ")";
                    using var s = new DirectorySearcher(root, filter, ["objectClass", "objectSid", "userPrincipalName", "mail"]) { PageSize = 100, SizeLimit = 0 };
                    using var results = s.FindAll();
                    foreach (SearchResult r in results)
                    {
                        resolved++;
                        Classify(r, users, ref groups, ref other);
                    }
                }
            }
            catch (COMException) when (!string.Equals(inDomain.Key, home, StringComparison.OrdinalIgnoreCase))
            {
                /* A domain this account cannot reach: its members count as not resolved, and the group's own are still read. */
            }
            other += inDomain.Count() - resolved;
        }
        return new AdGroupMembersData(users, groups, other);
    }, ct);

    /// <summary>A member as a user (with SID, sign-in and mail), a nested group, or neither (a contact, a computer).</summary>
    static void Classify(SearchResult r, List<AdUserData> users, ref int groups, ref int other)
    {
        var classes = r.Properties["objectclass"].Cast<object>().Select(o => o.ToString() ?? "").ToList();
        if (classes.Contains("group", StringComparer.OrdinalIgnoreCase)) groups++;
        else if (classes.Contains("user", StringComparer.OrdinalIgnoreCase) && !classes.Contains("computer", StringComparer.OrdinalIgnoreCase))
            users.Add(new AdUserData(Sid(r), Str(r, "userprincipalname"), Str(r, "mail")));
        else other++;
    }

    static string? Sid(SearchResult r) =>
        r.Properties.Contains("objectsid") && r.Properties["objectsid"].Count > 0 && r.Properties["objectsid"][0] is byte[] b ? new SecurityIdentifier(b, 0).Value : null;

    /// <summary>The group's member attribute in ranges, so a group past AD's 1,500 values a read is read whole.</summary>
    static List<string> MemberDns(AdGroupData group, string? domain, CancellationToken ct)
    {
        var dns = new List<string>();
        using var entry = new DirectoryEntry(Root(domain) + PathOf(group.DistinguishedName));
        int? low = 0;
        while (low is int lo)
        {
            ct.ThrowIfCancellationRequested();
            using var s = new DirectorySearcher(entry, "(objectClass=*)", [$"member;range={lo}-*"], SearchScope.Base);
            var r = s.FindOne();
            var name = r?.Properties.PropertyNames.Cast<string>().FirstOrDefault(n => n.StartsWith("member", StringComparison.OrdinalIgnoreCase));
            if (r is null || name is null) break;
            foreach (var v in r.Properties[name]) if (v?.ToString() is { Length: > 0 } dn) dns.Add(dn);
            low = AdGroups.NextRange(name);
        }
        return dns;
    }
}
