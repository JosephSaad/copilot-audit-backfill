using Governance.Application.Commands;
using Governance.Application.Data;
using Governance.Application.Integrations;
using Governance.Application.Seats;
using Governance.Domain;

namespace Governance.UnitTests;

/// <summary>Seat groups read from on-premises AD when Graph's group permission is refused, and the first Platform Owner.</summary>
public class AdGroupTests
{
    [Theory]
    [InlineData(null, "AUTO")]
    [InlineData("", "AUTO")]
    [InlineData("ad", "AD")]
    [InlineData(" AD ", "AD")]
    [InlineData("Entra", "ENTRA")]
    [InlineData("LDAP", "AUTO")]
    public void Mode_is_one_of_three(string? value, string expected) => Assert.Equal(expected, AdGroups.Mode(value));

    [Theory]
    [InlineData("USERS\\GSG_Copilot_Retail", "GSG_Copilot_Retail")]
    [InlineData(" GSG_Copilot_Retail ", "GSG_Copilot_Retail")]
    [InlineData("a\\b\\Group", "Group")]
    public void Bare_name_leaves_out_the_domain(string name, string expected) => Assert.Equal(expected, AdGroups.BareName(name));

    [Fact]
    public void Filter_escapes_and_matches_three_names()
    {
        Assert.Equal(@"a\28b\29\2a\5c", AdGroups.Escape(@"a(b)*\"));
        Assert.Equal("(&(objectCategory=group)(|(sAMAccountName=G\\2a)(cn=G\\2a)(displayName=G\\2a)))", AdGroups.GroupFilter(@"DOM\G*"));
    }

    [Fact]
    public void Distinguished_names_give_their_domain()
    {
        const string dn = @"CN=Lee\, Pat,OU=Users,DC=users,DC=contoso,DC=com";
        Assert.Equal(["CN=Lee\\, Pat", "OU=Users", "DC=users", "DC=contoso", "DC=com"], AdGroups.SplitDn(dn).ToArray());
        Assert.Equal("users.contoso.com", AdGroups.DnsDomainOf(dn));
        Assert.Equal("DC=users,DC=contoso,DC=com", AdGroups.NamingContextOf(dn));
        Assert.Null(AdGroups.DnsDomainOf("CN=x"));
    }

    [Theory]
    [InlineData("member;range=0-1499", 1500)]
    [InlineData("member;range=1500-2999", 3000)]
    [InlineData("member;range=1500-*", null)]
    [InlineData("member", null)]
    public void Ranged_reads_go_on_until_the_last_range(string returned, int? next) => Assert.Equal(next, AdGroups.NextRange(returned));

    [Fact]
    public void Members_match_by_sid_then_sign_in_then_mail()
    {
        Guid a = Guid.NewGuid(), b = Guid.NewGuid(), c = Guid.NewGuid();
        var known = new[]
        {
            new KnownAccount(a, "alex@contoso.com", null, "S-1-5-21-1-2-3-1001"),
            new KnownAccount(b, "pat@contoso.com", "pat.lee@contoso.com", null),
            new KnownAccount(c, "sam@contoso.com", null, null),
        };
        var members = new[]
        {
            new AdUserData("S-1-5-21-1-2-3-1001", "alex@users.contoso.com", null),   // the SID wins: AD's sign-in is not Microsoft 365's
            new AdUserData(null, "PAT@CONTOSO.COM", null),                            // sign-in, any case
            new AdUserData(null, "sam@users.contoso.com", "sam@contoso.com"),         // AD's mail is the Entra sign-in
            new AdUserData("S-1-5-21-9", "nobody@users.contoso.com", "nobody@users.contoso.com"),
            new AdUserData("S-1-5-21-1-2-3-1001", null, null),                        // the same person twice counts once
        };
        var (ids, unmatched) = AdGroups.Match(members, known);
        Assert.Equal(new[] { a, b, c }.OrderBy(x => x), ids.OrderBy(x => x));
        Assert.Equal(1, unmatched);
    }

    [Fact]
    public void Settings_read_where_members_come_from()
    {
        var s = SeatSettings.From(new Dictionary<string, string> { ["Seats.GroupMembersFrom"] = "ad", ["Seats.AdDomain"] = " users.contoso.com " });
        Assert.Equal("AD", s.GroupMembersFrom);
        Assert.Equal("users.contoso.com", s.AdDomain);
        Assert.Equal("AUTO", SeatSettings.From(new Dictionary<string, string>()).GroupMembersFrom);
    }

    [Theory]
    [InlineData("Seats.EaAnniversary", "4-1", "04-01")]
    [InlineData("Seats.RatePerSeatMonth", "31.5", "31.50")]
    [InlineData("Seats.Currency", "cad", "CAD")]
    [InlineData("Seats.InactiveDays", "90", "90")]
    [InlineData("Seats.IncreaseEffective", "next_month", "NEXT_MONTH")]
    [InlineData("Seats.GroupMembersFrom", "ad", "AD")]
    [InlineData("Seats.AdDomain", " Users.Contoso.com ", "users.contoso.com")]
    [InlineData("Seats.AdDomain", "", "")]
    [InlineData("Seats.NoLobChargedTo", "", "")]
    public void Seat_settings_are_kept_in_one_form(string key, string value, string kept) => Assert.Equal(kept, SeatSettings.Check(key, value));

    [Theory]
    [InlineData("Seats.EaAnniversary", "02-30")]
    [InlineData("Seats.RatePerSeatMonth", "thirty")]
    [InlineData("Seats.InactiveDays", "0")]
    [InlineData("Seats.UsagePeriod", "D60")]
    [InlineData("Seats.GroupMembersFrom", "LDAP")]
    [InlineData("Seats.AdDomain", "users contoso")]
    [InlineData("Intake.Open", "true")]                  // not a seat setting: not changed here
    public void Seat_settings_refuse_what_the_pages_cannot_read(string key, string value)
        => Assert.Throws<DomainException>(() => SeatSettings.Check(key, value));

    [Theory]
    [InlineData(Roles.PlatformOwner, true, 0, true)]
    [InlineData(Roles.PlatformOwner, true, 1, false)]    // once someone holds it, the Platform Owner gives central roles
    [InlineData(Roles.PlatformOwner, false, 0, false)]   // only a Copilot Insights administrator
    [InlineData(Roles.WorkbenchAdmin, true, 0, false)]   // only the Platform Owner role itself
    public void The_first_platform_owner(string role, bool insightsAdmin, int holders, bool allowed)
        => Assert.Equal(allowed, RegistryCommands.FirstPlatformOwner(role, insightsAdmin, holders));

    [Theory]
    [InlineData("Exception.NoticeDays", "1, 30,7,30", "30,7,1")]
    [InlineData("Evidence.AllowedTypes", ".PDF, .png", ".pdf,.png")]
    [InlineData("Retention.WorkerRecordDays", "365", "365")]
    [InlineData("Workbench.BaseUrl", "https://insights.contoso.com/CopilotDashboard/", "https://insights.contoso.com/CopilotDashboard")]
    [InlineData("Calendar.TimeZone", "UTC", "UTC")]
    public void Workbench_settings_are_kept_in_one_form(string key, string value, string kept) => Assert.Equal(kept, WorkbenchSettings.Check(key, value));

    [Theory]
    [InlineData("Retention.WorkerRecordDays", "10")]          // never under 30
    [InlineData("Evidence.MaxUploadMb", "100")]               // the site takes 25 MB at most
    [InlineData("Exception.NoticeDays", "30,x")]
    [InlineData("Workbench.BaseUrl", "insights.contoso.com")]
    [InlineData("Calendar.TimeZone", "Mars Standard Time")]
    [InlineData("Seats.InactiveDays", "60")]                   // on Seat groups, not here
    public void Workbench_settings_refuse_what_cannot_be_read(string key, string value)
        => Assert.Throws<DomainException>(() => WorkbenchSettings.Check(key, value));

    private sealed class Who(bool admin, bool central, int roles) : Governance.Application.ICurrentPrincipal
    {
        public bool IsAuthenticated => true;
        public int PersonId => 1;
        public int? RealPersonId => null;
        public string DisplayName => "x";
        public bool IsCentral => central;
        public IReadOnlyList<RoleGrant> Roles { get; } = Enumerable.Range(0, roles).Select(_ => new RoleGrant("LOB_SEAT_ADMIN", ScopeTypes.FundingOwner, 1)).ToList();
        public IReadOnlySet<string> AppRoles { get; } = admin ? new HashSet<string> { Governance.Domain.AppRoles.Admin } : new HashSet<string>();
        public IReadOnlySet<string> Permissions { get; } = new HashSet<string>();
        public IReadOnlyDictionary<int, IReadOnlySet<string>> ScopedPermissions { get; } = new Dictionary<int, IReadOnlySet<string>>();
    }

    [Theory]
    [InlineData(true, false, 0, true)]    // a Copilot Insights administrator
    [InlineData(false, true, 0, true)]    // a central role
    [InlineData(false, false, 1, true)]   // any Workbench role (a seat admin names other seat admins)
    [InlineData(false, false, 0, false)]  // someone with no role does not search the directory
    public void Who_may_add_people_from_the_directory(bool admin, bool central, int roles, bool may)
        => Assert.Equal(may, RegistryCommands.MayAddPeople(new Who(admin, central, roles)));

    [Fact]
    public void The_settings_page_leaves_seats_intake_and_setup_to_their_own_pages()
    {
        Assert.True(WorkbenchSettings.OnSettingsPage("Calendar.TimeZone"));
        Assert.False(WorkbenchSettings.OnSettingsPage("Seats.RatePerSeatMonth"));
        Assert.False(WorkbenchSettings.OnSettingsPage("Intake.Open"));
        Assert.False(WorkbenchSettings.OnSettingsPage("Setup.StandardsApprovedUtc"));
    }
}
