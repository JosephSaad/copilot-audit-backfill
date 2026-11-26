using System.Text;

namespace Governance.Application.Integrations;

/// <summary>
/// The seat groups read from on-premises Active Directory over LDAP, as the account the Workbench runs as, when Microsoft
/// Graph's group permission (GroupMember.Read.All) is not granted. The groups are synced to Entra, so AD holds the same
/// members. Read only.
/// </summary>
public interface IAdGroupApi
{
    /// <summary>
    /// A group by name in <paramref name="domain"/> (its DNS name or a domain controller; null: the server's own domain):
    /// its account name (sAMAccountName), common name or display name, with any DOMAIN\ in front left out. Null when none.
    /// </summary>
    Task<AdGroupData?> FindGroupAsync(string name, string? domain, CancellationToken ct);

    /// <summary>The group's direct members: users (with their SID, sign-in and mail) and how many are groups.</summary>
    Task<AdGroupMembersData> MembersAsync(AdGroupData group, string? domain, CancellationToken ct);
}

public sealed record AdGroupData(Guid ObjectGuid, string DistinguishedName, string Name, bool DuplicateName = false);

/// <summary>A user member. <paramref name="Sid"/> as S-1-5-..., the way Copilot Insights' directory load keeps it.</summary>
public sealed record AdUserData(string? Sid, string? UserPrincipalName, string? Mail);

/// <summary>Direct members. <paramref name="Unresolved"/>: members that are neither a user nor a group (another forest's, or a contact).</summary>
public sealed record AdGroupMembersData(IReadOnlyList<AdUserData> Users, int NestedGroups, int Unresolved);

/// <summary>A person the Workbench knows, for matching AD members to Entra accounts.</summary>
public sealed record KnownAccount(Guid ObjectId, string? UserPrincipalName, string? Mail, string? Sid);

/// <summary>The pure parts of reading seat groups from AD, for the tests.</summary>
public static class AdGroups
{
    public const string FromEntra = "ENTRA";
    public const string FromAd = "AD";
    public const string Auto = "AUTO";

    /// <summary>Seats.GroupMembersFrom as one of ENTRA, AD or AUTO (anything else: AUTO).</summary>
    public static string Mode(string? value) => value?.Trim().ToUpperInvariant() switch
    {
        FromEntra => FromEntra,
        FromAd => FromAd,
        _ => Auto,
    };

    /// <summary>A group name without its DOMAIN\ in front, trimmed.</summary>
    public static string BareName(string name)
    {
        var n = name.Trim();
        var i = n.LastIndexOf('\\');
        return i >= 0 ? n[(i + 1)..].Trim() : n;
    }

    /// <summary>A value escaped for an LDAP filter (RFC 4515).</summary>
    public static string Escape(string value)
    {
        var sb = new StringBuilder(value.Length + 8);
        foreach (var ch in value)
            sb.Append(ch switch
            {
                '\\' => @"\5c",
                '*' => @"\2a",
                '(' => @"\28",
                ')' => @"\29",
                '\0' => @"\00",
                _ => ch.ToString(),
            });
        return sb.ToString();
    }

    /// <summary>The filter that finds a group by account name, common name or display name.</summary>
    public static string GroupFilter(string name)
    {
        var n = Escape(BareName(name));
        return $"(&(objectCategory=group)(|(sAMAccountName={n})(cn={n})(displayName={n})))";
    }

    /// <summary>The domain a distinguished name lives in, as a DNS name: DC=corp,DC=example,DC=com is corp.example.com.</summary>
    public static string? DnsDomainOf(string distinguishedName)
    {
        var parts = new List<string>();
        foreach (var rdn in SplitDn(distinguishedName))
            if (rdn.StartsWith("DC=", StringComparison.OrdinalIgnoreCase)) parts.Add(rdn[3..]);
        return parts.Count == 0 ? null : string.Join('.', parts);
    }

    /// <summary>The naming context of a distinguished name: its DC= parts.</summary>
    public static string? NamingContextOf(string distinguishedName)
    {
        var parts = SplitDn(distinguishedName).Where(r => r.StartsWith("DC=", StringComparison.OrdinalIgnoreCase)).ToList();
        return parts.Count == 0 ? null : string.Join(',', parts);
    }

    /// <summary>A distinguished name's parts, minding escaped commas (CN=Saad\, Joseph).</summary>
    public static IEnumerable<string> SplitDn(string dn)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < dn.Length; i++)
        {
            if (dn[i] == '\\' && i + 1 < dn.Length) { sb.Append(dn[i]).Append(dn[i + 1]); i++; continue; }
            if (dn[i] == ',') { yield return sb.ToString().Trim(); sb.Clear(); continue; }
            sb.Append(dn[i]);
        }
        if (sb.Length > 0) yield return sb.ToString().Trim();
    }

    /// <summary>
    /// The next range of a ranged attribute read (member;range=0-1499): the low bound to ask for next, or null when the
    /// last range came back (member;range=1500-*), or when the attribute came back whole (member).
    /// </summary>
    public static int? NextRange(string returnedName)
    {
        var i = returnedName.IndexOf(";range=", StringComparison.OrdinalIgnoreCase);
        if (i < 0) return null;
        var range = returnedName[(i + 7)..].Split('-');
        if (range.Length != 2 || range[1] == "*" || !int.TryParse(range[1], out var high)) return null;
        return high + 1;
    }

    /// <summary>
    /// Each AD member's Entra account: by SID first (the account Copilot Insights' directory load matched), then sign-in
    /// name, then mail. AD's sign-in name may not be the one people use for Microsoft 365, so the SID goes first.
    /// Returns the object IDs found, and how many members matched none.
    /// </summary>
    public static (IReadOnlyList<Guid> ObjectIds, int Unmatched) Match(IEnumerable<AdUserData> members, IEnumerable<KnownAccount> known)
    {
        var bySid = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        var byUpn = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        var byMail = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        foreach (var k in known)
        {
            if (k.ObjectId == Guid.Empty) continue;
            if (!string.IsNullOrWhiteSpace(k.Sid)) bySid.TryAdd(k.Sid.Trim(), k.ObjectId);
            if (!string.IsNullOrWhiteSpace(k.UserPrincipalName)) byUpn.TryAdd(k.UserPrincipalName.Trim(), k.ObjectId);
            if (!string.IsNullOrWhiteSpace(k.Mail)) byMail.TryAdd(k.Mail.Trim(), k.ObjectId);
        }
        var ids = new HashSet<Guid>();
        var unmatched = 0;
        foreach (var m in members)
        {
            var id = Find(m.Sid, bySid) ?? Find(m.UserPrincipalName, byUpn) ?? Find(m.Mail, byMail) ?? Find(m.Mail, byUpn);
            if (id is Guid g) ids.Add(g);
            else unmatched++;
        }
        return (ids.ToList(), unmatched);
    }

    static Guid? Find(string? key, Dictionary<string, Guid> map) =>
        !string.IsNullOrWhiteSpace(key) && map.TryGetValue(key.Trim(), out var id) ? id : null;
}
