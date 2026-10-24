using Governance.Domain;

namespace Governance.Seed;

/// <summary>A synthetic person: fictional name, @example.invalid mail, roles in scope.</summary>
public sealed record PersonaSpec(string Key, string DisplayName, string JobTitle, string Department, string Division, string CostCenter,
    IReadOnlyList<(string Role, string? Lob)> Roles);

public sealed record LobSpec(string Code, string Name, string OwnerType, string Division, bool CriticalShared, IReadOnlyList<(string Code, string Name)> CostCenters);

/// <summary>The demo institution: four funding owners and one persona per role (two operators, two approvers per LOB).</summary>
public static class Personas
{
    public static readonly IReadOnlyList<LobSpec> Lobs = new[]
    {
        new LobSpec("RETAIL", "Retail Banking", "LOB", "Retail Banking", false, new[] { ("RB-1000", "Retail Banking - Branch Network"), ("RB-1100", "Retail Banking - Digital Channels") }),
        new LobSpec("WEALTH", "Wealth Management", "LOB", "Wealth Management", false, new[] { ("WM-2000", "Wealth Management - Advisory"), ("WM-2100", "Wealth Management - Private Banking") }),
        new LobSpec("COMMERCIAL", "Commercial Banking", "LOB", "Commercial Banking", false, new[] { ("CB-3000", "Commercial Banking - Lending") }),
        new LobSpec("ES-IT", "Enterprise Shared IT", "ENTERPRISE_SHARED", "Enterprise Shared IT", true, new[] { ("IT-9000", "Enterprise IT - Digital Workplace"), ("IT-9100", "Enterprise IT - Platform Services") }),
    };

    private static readonly Dictionary<string, string[]> LobNames = new()
    {
        ["RETAIL"] = new[] { "Amelia Grant", "Marcus Lee", "Priya Natarajan", "Owen Fraser", "Chloe Bennett", "Daniel Okafor", "Sofia Marino", "Ethan Walsh" },
        ["WEALTH"] = new[] { "Helen Carter", "Rajiv Mehta", "Lucy Tremblay", "Victor Alvarez", "Nadia Haddad", "George Liu", "Isla Murray", "Tomas Novak" },
        ["COMMERCIAL"] = new[] { "Robert Klein", "Fatima Sheikh", "Jonah Patel", "Grace Wilson", "Andre Dubois", "Mei Chen", "Liam O'Connor", "Zara Ahmed" },
        ["ES-IT"] = new[] { "Catherine Ross", "Samuel Adeyemi", "Elena Petrova", "Noah Kim", "Hannah Schultz", "Arjun Rao", "Maya Lopez", "Felix Wagner" },
    };

    private static readonly (string Suffix, string Title, string Role)[] LobRoles =
    {
        ("owner", "Business owner", Roles.LobBusinessOwner),
        ("approver1", "Delegated approver", Roles.DelegatedApprover),
        ("approver2", "Delegated approver", Roles.DelegatedApprover),
        ("finance", "Finance partner", Roles.LobFinancePartner),
        ("sponsor", "Workload sponsor", Roles.WorkloadSponsor),
        ("product", "Product owner", Roles.ProductOwner),
        ("maker1", "Maker", ""),
        ("maker2", "Developer", ""),
    };

    public static IReadOnlyList<PersonaSpec> All()
    {
        var list = new List<PersonaSpec>
        {
            Central("platformOwner", "Diane Moreau", "Platform Owner, Digital Workplace", Roles.PlatformOwner),
            Central("steering", "Patrick Hughes", "Program Office lead", Roles.Steering),
            Central("intake", "Aisha Rahman", "Intake and entitlement analyst", Roles.IntakeAnalyst),
            Central("operator1", "Kevin Brooks", "Platform operator", Roles.PlatformOperator),
            Central("operator2", "Yuki Tanaka", "Platform operator", Roles.PlatformOperator),
            Central("architect", "Martin Svensson", "Solution architect", Roles.Architect),
            Central("risk", "Olivia Barnes", "Technology risk reviewer", Roles.RiskReviewer),
            Central("finopsLead", "Carlos Mendes", "FinOps lead", Roles.FinOpsLead),
            Central("finopsAnalyst", "Sarah Lindqvist", "FinOps analyst", Roles.FinOpsAnalyst),
            Central("commercial", "William Hart", "Commercial approver", Roles.CommercialApprover),
            Central("sourcing", "Anika Joshi", "Sourcing agent", Roles.SourcingAgent),
            Central("licensing", "Brian Doyle", "Licensing owner", Roles.LicensingOwner),
            Central("auditor", "Ruth Campbell", "Internal auditor", Roles.Auditor),
            Central("admin", "Leo Fischer", "Workbench administrator", Roles.WorkbenchAdmin),
        };
        foreach (var lob in Lobs)
        {
            var names = LobNames[lob.Code];
            for (var i = 0; i < LobRoles.Length; i++)
            {
                var (suffix, title, role) = LobRoles[i];
                var effective = role == Roles.LobBusinessOwner && lob.OwnerType == "ENTERPRISE_SHARED" ? Roles.SharedServiceOwner : role;
                var roles = string.IsNullOrEmpty(effective) ? new List<(string, string?)>() : new List<(string, string?)> { (effective, lob.Code) };
                var title2 = effective == Roles.SharedServiceOwner ? "Shared-service owner" : title;
                list.Add(new PersonaSpec($"{lob.Code}.{suffix}", names[i], $"{title2}, {lob.Name}", lob.Name, lob.Division, lob.CostCenters[0].Code, roles));
            }
        }
        return list;
    }

    private static PersonaSpec Central(string key, string name, string title, string role)
        => new(key, name, title, "Digital Workplace", "Enterprise Shared IT", "IT-9100", new[] { (role, (string?)null) });

    public static string MailFor(string displayName)
        => new string(displayName.ToLowerInvariant().Replace(' ', '.').Where(c => char.IsLetter(c) || c == '.').ToArray()) + "@example.invalid";
}
