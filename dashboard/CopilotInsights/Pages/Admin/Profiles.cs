using System.Text;
using CopilotInsights.Data;
using CopilotInsights.Infrastructure;
using Microsoft.AspNetCore.Html;
using Microsoft.AspNetCore.Mvc;

namespace CopilotInsights.Pages.Admin;

/* ---- Access profiles --------------------------------------------------------------------------- */

/// <summary>
/// Named bundles of roles (each with or without the whole organization) and Admin pages, granted to people and groups as
/// one: changing a profile changes it for everyone who holds it. The bundles people hold today are offered as profiles.
/// </summary>
public sealed class ProfilesModel(PageServices s) : AdminPage(s)
{
    public override string Title => "Access profiles";
    public override string Intro => "Define access once, as a few named bundles, and grant each to a group: whoever is in the group has the bundle, and a change to the bundle reaches everyone who holds it. " +
        "Requests and approvals can go through an Entra access package for each profile; its link is shown to anyone refused a page the profile would open.";

    sealed class ProfileRow
    {
        public int ProfileId { get; set; }
        public string Name { get; set; } = "";
        public string? Description { get; set; }
        public string? OwnerUpn { get; set; }
        public string? ApproverPrincipal { get; set; }
        public int? MaxDays { get; set; }
        public string? RequestUrl { get; set; }
        public string UpdatedBy { get; set; } = "";
        public DateTime UpdatedUtc { get; set; }
        public int Holders { get; set; }
    }

    sealed class HolderRow
    {
        public int GrantId { get; set; }
        public string Principal { get; set; } = "";
        public string PrincipalType { get; set; } = "";
        public string? PrincipalName { get; set; }
        public string? DisplayName { get; set; }
        public int ProfileId { get; set; }
        public string? Note { get; set; }
        public string GrantedBy { get; set; } = "";
        public DateTime GrantedUtc { get; set; }
    }

    async Task<bool> InstalledAsync() => await Db.ScalarAsync<int>("SELECT CASE WHEN OBJECT_ID(N'dash.AccessProfile', N'U') IS NULL THEN 0 ELSE 1 END;") == 1;

    /// <summary>Whether this database has 3.1.0's section grants on profiles (dash.AccessProfileRole.Sections).</summary>
    async Task<bool> HasSectionsAsync() => await Db.ScalarAsync<int>("SELECT CASE WHEN COL_LENGTH(N'dash.AccessProfileRole', N'Sections') IS NULL THEN 0 ELSE 1 END;") == 1;

    async Task<Dictionary<int, List<ProfileRole>>> RolesAsync() =>
        (await Db.QueryAsync<(int ProfileId, string Role, bool OrgWide, string? Pages, string? Sections, bool NamedPeople)>(await HasSectionsAsync()
            ? "SELECT ProfileId, Role, OrgWide, Pages, Sections, NamedPeople FROM dash.AccessProfileRole;"
            : "SELECT ProfileId, Role, OrgWide, Pages, CAST(NULL AS NVARCHAR(1000)), CAST(0 AS BIT) FROM dash.AccessProfileRole;"))
            .GroupBy(r => r.ProfileId).ToDictionary(g => g.Key, g => AccessProfiles.Normalize(g.Select(r => new ProfileRole(r.Role, r.OrgWide, r.Pages, r.Sections, r.NamedPeople))));

    protected override async Task BuildAsync()
    {
        ShowDone();
        if (!await InstalledAsync())
        {
            Panels.Add(new("Not set up", new HtmlString($"<p>{E(await NotSetUpAsync("Access profiles cannot be kept here yet", "2.8.0", "dash.AccessProfile", "dash.AccessProfileRole", "dash.ProfileGrant"))}</p>")));
            return;
        }
        var profiles = await Db.QueryAsync<ProfileRow>(@"
SELECT p.ProfileId, p.Name, p.Description, p.OwnerUpn, p.ApproverPrincipal, p.MaxDays, p.RequestUrl, p.UpdatedBy, p.UpdatedUtc,
       (SELECT COUNT(*) FROM dash.ProfileGrant AS g WHERE g.ProfileId = p.ProfileId) AS Holders
FROM dash.AccessProfile AS p ORDER BY p.Name;");
        var roles = await RolesAsync();
        List<ProfileRole> Of(int id) => roles.GetValueOrDefault(id) ?? [];

        Kpis.Add(new("Profiles", Num(profiles.Count), profiles.Count == 0 ? "none yet: start from those offered below" : "bundles of roles, granted as one"));
        Kpis.Add(new("Grants of a profile", Num(profiles.Sum(p => p.Holders)), "people and groups holding one"));
        var roleByRole = await Db.ScalarAsync<int>("SELECT COUNT(DISTINCT Principal) FROM dash.RoleGrant WHERE Role <> 'Admin' OR NULLIF(Pages, N'') IS NOT NULL;");
        Kpis.Add(new("Still granted role by role", Num(roleByRole), "people and groups with a role given outside any profile", roleByRole > 0 && profiles.Count > 0 ? "warn" : null));

        /* The profiles, each with what it gives and where to ask for it. */
        var t = new Table { Zone = Zone, Headers = ["Profile", "Gives", "Held by", "Request link", "Owner", "Changed (UTC)", ""], Numeric = new HashSet<int> { 2 }, EmptyText = "No profiles yet." };
        foreach (var p in profiles)
            t.Rows.Add([new HtmlString($"<b>{E(p.Name)}</b>" + (p.Description is null ? "" : $"<span class=\"cellnote\">{E(p.Description)}</span>")),
                AccessProfiles.Describe(Of(p.ProfileId)), p.Holders,
                p.RequestUrl is null ? null : new HtmlString($"<a href=\"{E(p.RequestUrl)}\" rel=\"noopener noreferrer\" target=\"_blank\">My Access</a>" + (p.MaxDays is { } d ? $" <small>({d} days)</small>" : "")),
                p.OwnerUpn, p.UpdatedUtc,
                new HtmlString($"<a class=\"btn quiet\" href=\"{E(Request.Path + "?edit=" + p.ProfileId)}\">Change</a>" +
                    (p.Holders == 0 && Viewer.IsAdmin ? Form("delete", Hidden("id", p.ProfileId) + Button("Delete", $"Delete the profile {p.Name}? Nobody holds it.", quiet: true)) : ""))]);
        Panels.Add(new("Profiles", t.Render(), "A profile can be deleted once nobody holds it. Change one, and everyone who holds it has the change from their next page."));

        /* Add or change one. */
        var editing = int.TryParse(Request.Query["edit"], out var eid) ? profiles.FirstOrDefault(p => p.ProfileId == eid) : null;
        var current = editing is null ? [] : Of(editing.ProfileId);
        /* The Workbench's roles only where the Workbench is set up. */
        var workbenchOn = HttpContext.RequestServices.GetRequiredService<global::Governance.Web.Infrastructure.WorkbenchState>().On;
        /* Sections are ticked in their own step. The roles offered are Organization, Admin and the Workbench's work roles,
           and a persona role the profile gives already (granted before 3.1.0), so it can be kept or taken off. */
        var offered = Roles.All.Where(r => r != Roles.Sections && r != Roles.Preview && (Viewer.IsAdmin || r != Roles.Admin) && (workbenchOn || !Roles.IsWorkbench(r))
                                           && (!AccessRules.IsPersona(r) || r == Roles.Organization || current.Any(c => c.Role == r))).ToList();
        var hasSections = await HasSectionsAsync();
        var sectionRow = current.FirstOrDefault(c => c.Role == Roles.Sections);
        var form = new StringBuilder();
        if (editing is not null) form.Append(Hidden("id", editing.ProfileId));
        form.Append("<fieldset class=\"step\"><legend>1. Name</legend><div class=\"row\">" + Input("name", "Name", editing?.Name, 30) + Input("description", "What it is for", editing?.Description, 40) + "</div></fieldset>");
        if (hasSections)
            form.Append($"<fieldset class=\"step\"><legend>2. Sections it opens{TipFor("section", "Sections it opens")}</legend>" +
                SectionPicker.Boxes(new HashSet<string>(Sections.Parse(sectionRow?.Sections), StringComparer.OrdinalIgnoreCase), workbenchOn) + "</fieldset>" +
                $"<fieldset class=\"step\"><legend>3. How much of the organization{TipFor("orgWide", "How much of the organization")}</legend>" + SectionPicker.Scope(sectionRow?.OrgWide == true) + "</fieldset>" +
                $"<fieldset class=\"step\"><legend>4. Lists that name people{TipFor("named", "Lists that name people")}</legend>" + SectionPicker.Named(sectionRow?.NamedPeople == true) + "</fieldset>");
        form.Append($"<fieldset class=\"step\"><legend>{(hasSections ? 5 : 2)}. Other roles (optional){TipFor("role", "Other roles")}</legend><fieldset class=\"roles\"><legend>Roles</legend>" + string.Concat(offered.Select(r =>
        {
            var have = current.FirstOrDefault(c => c.Role == r);
            return $"<label><input type=\"checkbox\" name=\"role\" value=\"{E(r)}\"{(have is not null ? " checked" : "")}/> {E(r)}</label>" +
                   (AccessRules.IsPersona(r) && r != Roles.Organization ? $"<label class=\"check sub\"><input type=\"checkbox\" name=\"wide\" value=\"{E(r)}\"{(have?.OrgWide == true ? " checked" : "")}/> whole organization</label>" : "");
        })) + "</fieldset>");
        if (Viewer.IsAdmin)
        {
            var pages = AdminPages.Parse(current.FirstOrDefault(c => c.Role == Roles.Admin)?.Pages);
            form.Append("<fieldset class=\"roles\"><legend>Admin pages (none ticked: every page)</legend>" +
                string.Concat(AdminPages.Choices.Select(ap => $"<label><input type=\"checkbox\" name=\"adminPage\" value=\"{E(ap.Path)}\"{(pages.Contains(AdminPages.Normalize(ap.Path)) ? " checked" : "")}/> {E(ap.Title)}</label>")) + "</fieldset>");
        }
        form.Append("</fieldset><fieldset class=\"step\"><legend>Who answers for it, and where to ask for it</legend><div class=\"row\">");
        form.Append(PersonBox("owner", "Owner", editing?.OwnerUpn, 30, FieldTips.Fixed["/admin/profiles|owner"]) + Input("approver", "Approver", editing?.ApproverPrincipal, 30) +
                    Input("maxDays", "Access lasts (days)", editing?.MaxDays?.ToString(), 6, "number") + Input("requestUrl", "Request link (My Access)", editing?.RequestUrl, 50) + "</div></fieldset>");
        form.Append(Button(editing is null ? "Add the profile" : "Save the profile",
            editing is not null && editing.Holders > 0 ? $"Change {editing.Name} for the {editing.Holders} people and groups who hold it?" : null));
        Panels.Add(new(editing is null ? "Add a profile" : $"Change {editing.Name}", new HtmlString(Form("save", form.ToString()).Replace("<form class=\"admin\"", "<form class=\"admin grantform\"") +
            (editing is null ? "" : $"<p class=\"sub\"><a href=\"{E(Request.Path)}\">Add a new one instead</a></p>")),
            (hasSections ? "Tick the sections it opens, as they are in the menu. " + SectionPicker.Help + " " : "") +
            "The other roles: Organization shows every section its holder has for the whole organization; Admin opens the Admin pages ticked (none ticked: every page). " +
            "The request link is where people ask for the profile, such as its Entra access package in My Access; anyone refused a page the profile opens is shown it." +
            (Viewer.IsAdmin ? "" : " A profile that gives Admin is made and changed by a full administrator.")));

        /* Grant one to a person or group. */
        if (profiles.Count > 0)
        {
            var graph = HttpContext.RequestServices.GetRequiredService<EntraGraph>();
            var grant = PersonBox("principal", "User (name or UPN) or group (DOMAIN\\Group)", null, 34) + Select("type", "Type", ["User", "Group"], "Group") +
                (graph.Available ? $"<label>Or an Entra group<input type=\"text\" name=\"entraGroup\" size=\"34\" list=\"entra-groups\" autocomplete=\"off\" " +
                    $"data-lookup=\"{E("/admin/access?handler=groups" + TenantQuery)}\" data-min=\"2\" placeholder=\"2 letters of the group's name\"/>{TipFor("entraGroup", "Or an Entra group")}</label><datalist id=\"entra-groups\"></datalist>" : "") +
                Select("profile", "Profile", profiles.Select(p => (p.ProfileId.ToString(), p.Name)), null) + Input("note", "Why (optional)", null, 30) + Button("Grant the profile");
            Panels.Add(new("Grant a profile", new HtmlString(Form("grant", grant)),
                "Grant a profile to a group rather than to people one by one: then access is group membership, which IT already manages and reviews. " +
                "A group grant follows AD at sign-in (and Entra, for an Entra group), so adding someone to the group is all it takes."));

            var holders = await Db.QueryAsync<HolderRow>(@"
SELECT g.GrantId, g.Principal, g.PrincipalType, g.PrincipalName, p.DisplayName, g.ProfileId, g.Note, g.GrantedBy, g.GrantedUtc
FROM dash.ProfileGrant AS g LEFT JOIN dash.Person AS p ON p.Upn = g.Principal
ORDER BY g.ProfileId, g.Principal;");
            var ht = new Table { Zone = Zone, Headers = ["Profile", "Principal", "Name", "Type", "Why", "Granted by", "Granted (UTC)", ""], EmptyText = "Nobody holds a profile yet." };
            var names = profiles.ToDictionary(p => p.ProfileId, p => p.Name);
            foreach (var h in holders)
            {
                var gives = Of(h.ProfileId);
                var removable = Viewer.IsAdmin || gives.All(r => r.Role != Roles.Admin);
                ht.Rows.Add([names.GetValueOrDefault(h.ProfileId, "?"), h.Principal, h.PrincipalName ?? h.DisplayName, h.PrincipalType, h.Note, h.GrantedBy, h.GrantedUtc,
                    removable ? new HtmlString(Form("revoke", Hidden("id", h.GrantId) + RemoveBox($"Remove {names.GetValueOrDefault(h.ProfileId, "the profile")} from {h.PrincipalName ?? h.Principal}"))) : null]);
            }
            Panels.Add(new("Who holds each profile", ht.Render(), "Removing a profile takes away what it gives, unless the person holds it some other way; it takes effect from their next page."));
        }

        /* Offered: the bundles held today role by role, and the starters. */
        var grants = (await ViewerService.GrantsAsync(Db)).Where(g => g.ProfileId is null).Select(g => (g.Principal, new ProfileRole(g.Role, g.OrgWide, g.Pages, g.Sections, g.NamedPeople)));
        var proposals = AccessProfiles.Proposals(grants, profiles.Select(p => (IEnumerable<ProfileRole>)Of(p.ProfileId)), profiles.Select(p => p.Name),
                HttpContext.RequestServices.GetRequiredService<global::Governance.Web.Infrastructure.WorkbenchState>().On)
            .Where(p => Viewer.IsAdmin || p.Roles.All(r => r.Role != Roles.Admin)).Take(12).ToList();
        var pt = new Table { Headers = ["Offered as", "Gives", "Held today by", ""], Numeric = new HashSet<int> { 2 }, Sortable = false, EmptyText = "Nothing to offer: every bundle held today is a profile already." };
        foreach (var p in proposals)
        {
            var key = AccessProfiles.Key(p.Roles);
            var body = Hidden("key", key) + Hidden("name", p.Name) +
                (p.Holders.Count > 0 ? $"<label class=\"check\"><input type=\"checkbox\" name=\"grantHolders\" value=\"true\"/> and grant it to the {p.Holders.Count} who hold this today</label>" : "") +
                Button("Make it a profile", quiet: true);
            pt.Rows.Add([new HtmlString(E(p.Name) + (p.Starter ? " <small>(a starter)</small>" : "")), AccessProfiles.Describe(p.Roles),
                p.Holders.Count == 0 ? null : new HtmlString($"{p.Holders.Count:#,0}<span class=\"cellnote\">{E(string.Join(", ", p.Holders.Take(5)))}{(p.Holders.Count > 5 ? ", and more" : "")}</span>"),
                new HtmlString(Form("create", body))]);
        }
        Panels.Add(new("Offered from today's grants", pt.Render(),
            "Each set of roles that people or groups hold today role by role, most held first, then the starters. Make one a profile in a click, and rename or change it after. " +
            "Granting it to today's holders keeps their role-by-role grants: remove those in Admin › Access once the profile is in place."));
    }

    /// <summary>The field's information icon, for a field the page writes itself.</summary>
    string TipFor(string name, string label) => FieldTip(name, label);

    List<ProfileRole> Posted(string[]? role, string[]? wide, string[]? adminPage, string[]? section = null, bool orgWide = false, bool named = false)
    {
        var wideSet = (wide ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var pages = AdminPages.Format(adminPage);
        /* The Workbench's roles and sections only where it is set up, whatever the form sent. */
        var workbenchOn = HttpContext.RequestServices.GetRequiredService<global::Governance.Web.Infrastructure.WorkbenchState>().On;
        var sections = Sections.Format((section ?? []).Where(k => workbenchOn || Sections.Find(k)?.Workbench != true));
        var posted = (role ?? []).Where(r => r != Roles.Sections && (workbenchOn || !Roles.IsWorkbench(r))).Select(r => new ProfileRole(r, wideSet.Contains(r), r == Roles.Admin ? pages : null)).ToList();
        if (sections is not null) posted.Add(new ProfileRole(Roles.Sections, orgWide, null, sections, named));
        var list = AccessProfiles.Normalize(posted);
        if (list.Count == 0) throw new ArgumentException("tick at least one section or role.");
        if (list.Any(r => r.Role == Roles.Admin) && !Viewer.IsAdmin) throw new ArgumentException("only a full administrator can make a profile that gives Admin.");
        return list;
    }

    async Task NeedInstalledAsync()
    {
        if (!await InstalledAsync()) throw new ArgumentException("access profiles need the 2.8.0 schema: run Install-CopilotAudit.ps1 -Dashboard.");
    }

    async Task WriteRolesAsync(int id, List<ProfileRole> roles)
    {
        var hasSections = await HasSectionsAsync();
        if (!hasSections && roles.Any(r => r.Role == Roles.Sections)) throw new ArgumentException("a profile's sections need the 3.1.0 schema: run Install-CopilotAudit.ps1 -Dashboard.");
        await Db.ExecuteAsync("DELETE FROM dash.AccessProfileRole WHERE ProfileId = @id;", new { id });
        foreach (var r in roles)
            await Db.ExecuteAsync(hasSections
                    ? "INSERT INTO dash.AccessProfileRole (ProfileId, Role, OrgWide, Pages, Sections, NamedPeople) VALUES (@id, @role, @wide, @pages, @sections, @named);"
                    : "INSERT INTO dash.AccessProfileRole (ProfileId, Role, OrgWide, Pages) VALUES (@id, @role, @wide, @pages);",
                new { id, role = r.Role, wide = r.OrgWide, pages = r.Pages, sections = r.Sections, named = r.NamedPeople });
    }

    async Task<bool> GivesAdminAsync(int id) => await Db.ScalarAsync<int>("SELECT COUNT(*) FROM dash.AccessProfileRole WHERE ProfileId = @id AND Role = 'Admin';", new { id }) > 0;

    public Task<IActionResult> OnPostSaveAsync(int? id, string? name, string? description, string[]? role, string[]? wide, string[]? adminPage,
        string? owner, string? approver, int? maxDays, string? requestUrl, string[]? section = null, bool orgWide = false, bool named = false) => AdminChangeAsync(async () =>
    {
        await NeedInstalledAsync();
        name = name?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > 100) throw new ArgumentException("give the profile a name of up to 100 characters.");
        if (maxDays is < 1 or > 3650) throw new ArgumentException("access lasts 1 to 3,650 days, or leave it empty.");
        var roles = Posted(role, wide, adminPage, section, orgWide, named);
        var url = AccessProfiles.RequestUrl(requestUrl);
        static string? Cut(string? s, int n) => string.IsNullOrWhiteSpace(s) ? null : s.Trim()[..Math.Min(n, s.Trim().Length)];
        var by = Viewer.Upn ?? Viewer.WindowsName;
        var args = new { id, name, description = Cut(description, 400), owner = Cut(owner, 256), approver = Cut(approver, 256), maxDays, url, by };
        if (id is { } pid)
        {
            if (await GivesAdminAsync(pid) && !Viewer.IsAdmin) throw new ArgumentException("only a full administrator can change a profile that gives Admin.");
            if (await Db.ExecuteAsync(@"UPDATE dash.AccessProfile SET Name = @name, Description = @description, OwnerUpn = @owner, ApproverPrincipal = @approver,
       MaxDays = @maxDays, RequestUrl = @url, UpdatedBy = @by, UpdatedUtc = SYSUTCDATETIME() WHERE ProfileId = @id;", args) == 0)
                throw new ArgumentException("that profile is gone.");
            await WriteRolesAsync(pid, roles);
            var held = await Db.ScalarAsync<int>("SELECT COUNT(*) FROM dash.ProfileGrant WHERE ProfileId = @pid;", new { pid });
            return $"Saved {name}: it gives {AccessProfiles.Describe(roles)}, to the {held} people and groups who hold it.";
        }
        if (await Db.ScalarAsync<int>("SELECT COUNT(*) FROM dash.AccessProfile WHERE Name = @name;", new { name }) > 0)
            throw new ArgumentException($"a profile is named {name} already: change that one, or choose another name.");
        var newId = await Db.ScalarAsync<int>(@"INSERT INTO dash.AccessProfile (Name, Description, OwnerUpn, ApproverPrincipal, MaxDays, RequestUrl, UpdatedBy)
OUTPUT inserted.ProfileId VALUES (@name, @description, @owner, @approver, @maxDays, @url, @by);", args);
        await WriteRolesAsync(newId, roles);
        return $"Added the profile {name}: it gives {AccessProfiles.Describe(roles)}.";
    });

    public Task<IActionResult> OnPostCreateAsync(string? key, string? name, bool grantHolders = false) => AdminChangeAsync(async () =>
    {
        await NeedInstalledAsync();
        var roles = AccessProfiles.FromKey(key);
        if (roles.Count == 0) throw new ArgumentException("that bundle gives nothing.");
        if (roles.Any(r => r.Role == Roles.Admin) && !Viewer.IsAdmin) throw new ArgumentException("only a full administrator can make a profile that gives Admin.");
        name = string.IsNullOrWhiteSpace(name) ? AccessProfiles.SuggestName(roles) : name.Trim()[..Math.Min(100, name.Trim().Length)];
        for (var n = 2; await Db.ScalarAsync<int>("SELECT COUNT(*) FROM dash.AccessProfile WHERE Name = @name;", new { name }) > 0; n++)
            name = $"{name.TrimEnd(' ', '0', '1', '2', '3', '4', '5', '6', '7', '8', '9').TrimEnd()} {n}";
        var by = Viewer.Upn ?? Viewer.WindowsName;
        var id = await Db.ScalarAsync<int>("INSERT INTO dash.AccessProfile (Name, UpdatedBy) OUTPUT inserted.ProfileId VALUES (@name, @by);", new { name, by });
        await WriteRolesAsync(id, roles);
        var granted = 0;
        if (grantHolders)
        {
            /* Who holds exactly this bundle today, role by role: each is given the profile, and keeps their grants. */
            var all = (await ViewerService.GrantsAsync(Db)).Where(g => g.ProfileId is null).ToList();
            var want = AccessProfiles.Key(roles);
            var types = (await Db.QueryAsync<(string Principal, string Type, string? Name)>(
                "SELECT Principal, MAX(PrincipalType), MAX(PrincipalName) FROM dash.RoleGrant GROUP BY Principal;")).ToDictionary(x => x.Principal, StringComparer.OrdinalIgnoreCase);
            foreach (var who in all.GroupBy(g => g.Principal, StringComparer.OrdinalIgnoreCase)
                         .Where(g => AccessProfiles.Key(g.Select(x => new ProfileRole(x.Role, x.OrgWide, x.Pages, x.Sections, x.NamedPeople))) == want).Select(g => g.Key))
            {
                var t = types.GetValueOrDefault(who);
                granted += await Db.ExecuteAsync(@"
IF NOT EXISTS (SELECT 1 FROM dash.ProfileGrant WHERE Principal = @who AND ProfileId = @id)
    INSERT INTO dash.ProfileGrant (Principal, PrincipalType, PrincipalObjectId, PrincipalName, ProfileId, Note, GrantedBy)
    VALUES (@who, @type, @oid, @pname, @id, N'Made from their role-by-role grants', @by);",
                    new { who, type = t.Type ?? "User", oid = EntraPrincipal.TryParse(who, out var g) ? g : (Guid?)null, pname = t.Name, id, by }) > 0 ? 1 : 0;
            }
        }
        return $"Added the profile {name}: it gives {AccessProfiles.Describe(roles)}." +
               (grantHolders ? $" Granted it to the {granted} who held this bundle; their role-by-role grants are kept until you remove them in Admin › Access." : "");
    });

    public Task<IActionResult> OnPostDeleteAsync(int id) => AdminChangeAsync(async () =>
    {
        await NeedInstalledAsync();
        if (!Viewer.IsAdmin) throw new ArgumentException("a profile is deleted by a full administrator.");
        if (await Db.ScalarAsync<int>("SELECT COUNT(*) FROM dash.ProfileGrant WHERE ProfileId = @id;", new { id }) > 0)
            throw new ArgumentException("someone holds this profile: remove it from them first.");
        var name = (await Db.QueryAsync<string>("DELETE FROM dash.AccessProfile OUTPUT deleted.Name WHERE ProfileId = @id;", new { id })).FirstOrDefault();
        return name is null ? "That profile was already gone." : $"Deleted the profile {name}.";
    });

    public Task<IActionResult> OnPostGrantAsync(string? principal, string? type, string? entraGroup, int profile, string? note) => AdminChangeAsync(async () =>
    {
        await NeedInstalledAsync();
        if (!string.IsNullOrWhiteSpace(entraGroup)) (principal, type) = (entraGroup, "Group");
        principal = principal?.Trim();
        if (string.IsNullOrEmpty(principal) || principal.Length > 256) throw new ArgumentException("give a UPN, DOMAIN\\Group or an Entra group.");
        if (type is not ("User" or "Group")) throw new ArgumentException("choose User or Group.");
        if (type == "User" && !principal.Contains('@') && !principal.Contains('\\')) throw new ArgumentException("a user is a UPN (name@domain) or DOMAIN\\name.");
        var (objectId, groupName) = type == "Group" && !principal.Contains('\\') ? await EntraGroupAsync(principal) : ((Guid?)null, (string?)null);
        if (objectId is { } oid) principal = EntraPrincipal.Of(oid);
        var name = await Db.ScalarAsync<string?>("SELECT Name FROM dash.AccessProfile WHERE ProfileId = @profile;", new { profile }) ?? throw new ArgumentException("that profile is gone.");
        if (await GivesAdminAsync(profile) && !Viewer.IsAdmin) throw new ArgumentException("only a full administrator can grant a profile that gives Admin.");
        if (await GivesAdminAsync(profile) && IsMe(principal) && !Viewer.IsAdmin) throw new ArgumentException("you cannot widen your own Admin pages; another administrator can.");
        var n = await Db.ExecuteAsync(@"
IF NOT EXISTS (SELECT 1 FROM dash.ProfileGrant WHERE Principal = @principal AND ProfileId = @profile)
    INSERT INTO dash.ProfileGrant (Principal, PrincipalType, PrincipalObjectId, PrincipalName, ProfileId, Note, GrantedBy)
    VALUES (@principal, @type, @objectId, @groupName, @profile, @note, @by);",
            new { principal, type, objectId, groupName, profile, note = string.IsNullOrWhiteSpace(note) ? null : note.Trim()[..Math.Min(400, note.Trim().Length)], by = Viewer.Upn ?? Viewer.WindowsName });
        var who = groupName is null ? principal : $"{groupName} ({principal})";
        return n > 0 ? $"Granted {name} to {type.ToLowerInvariant()} {who}." : $"{who} already holds {name}.";
    });

    public Task<IActionResult> OnPostRevokeAsync(int id) => AdminChangeAsync(async () =>
    {
        await NeedInstalledAsync();
        var g = (await Db.QueryAsync<(string Principal, int ProfileId)>("SELECT Principal, ProfileId FROM dash.ProfileGrant WHERE GrantId = @id;", new { id })).FirstOrDefault();
        if (g.Principal is null) return "That grant was already gone.";
        if (await GivesAdminAsync(g.ProfileId) && !Viewer.IsAdmin) throw new ArgumentException("only a full administrator can remove a profile that gives Admin.");
        var name = await Db.ScalarAsync<string>("SELECT Name FROM dash.AccessProfile WHERE ProfileId = @p;", new { p = g.ProfileId });
        await Db.ExecuteAsync("DELETE FROM dash.ProfileGrant WHERE GrantId = @id;", new { id });
        return $"Removed {name} from {g.Principal}.";
    });
}
