using System.Net;
using CopilotInsights.Charts;
using CopilotInsights.Data;
using CopilotInsights.Infrastructure;
using Microsoft.AspNetCore.Html;
using Microsoft.AspNetCore.Mvc;

namespace CopilotInsights.Pages.Security
{
    /// <summary>Guests and external users in Copilot's records: who from outside is using it, and on what.</summary>
    public sealed class GuestsModel(PageServices s) : InsightsPage(s)
    {
        public override string Title => "Guests and external users";
        public override string Persona => "Security";
        public override string Intro => "Copilot use by guest accounts (#EXT# sign-ins, or a user type of Guest from the license tool): how much, from which home organizations, and with which signals. Home organizations with fewer guests than the minimum group size are combined; the named list needs the Security role.";

        sealed class Row
        {
            public string Domain { get; set; } = "";
            public int People { get; set; }
            public int Active { get; set; }
            public long Interactions { get; set; }
            public long Flags { get; set; }
            public long Labeled { get; set; }
            public int Licensed { get; set; }
            public int Confirmed { get; set; }
        }

        /// <summary>A guest's home domain: user_fabrikam.com#EXT#@contoso.onmicrosoft.com is fabrikam.com.</summary>
        internal const string Domain = @"CASE WHEN du.UserPrincipalName NOT LIKE N'%#EXT#%'
          THEN SUBSTRING(du.UserPrincipalName, CHARINDEX(N'@', du.UserPrincipalName) + 1, 256)
     WHEN CHARINDEX(N'_', LEFT(du.UserPrincipalName, CHARINDEX(N'#EXT#', du.UserPrincipalName) - 1)) = 0
          THEN LEFT(du.UserPrincipalName, CHARINDEX(N'#EXT#', du.UserPrincipalName) - 1)
     ELSE RIGHT(LEFT(du.UserPrincipalName, CHARINDEX(N'#EXT#', du.UserPrincipalName) - 1),
                CHARINDEX(N'_', REVERSE(LEFT(du.UserPrincipalName, CHARINDEX(N'#EXT#', du.UserPrincipalName) - 1))) - 1) END";

        protected override async Task BuildAsync()
        {
            var userType = await Db.ScalarAsync<int>("SELECT CASE WHEN COL_LENGTH(N'audit.CopilotLicense', N'UserType') IS NULL THEN 0 ELSE 1 END") == 1;
            var guest = userType
                ? "(du.UserPrincipalName LIKE N'%#EXT#%' OR EXISTS (SELECT 1 FROM audit.CopilotLicense AS cl WHERE cl.UserPrincipalName = du.UserPrincipalName AND cl.UserType = N'Guest'))"
                : "du.UserPrincipalName LIKE N'%#EXT#%'";
            /* 2.7.5: a licensed guest an administrator confirmed as a person is no breach on Copilot on non-human accounts;
               the count says how many, so the two pages agree. */
            var exceptions = await Db.ScalarAsync<int>("SELECT CASE WHEN OBJECT_ID(N'dash.NonHumanException', N'U') IS NULL THEN 0 ELSE 1 END") == 1;
            var ok = exceptions ? "CASE WHEN EXISTS (SELECT 1 FROM dash.NonHumanException AS ex WHERE ex.Upn = du.UserPrincipalName) THEN 1 ELSE 0 END" : "0";
            var rows = await Db.QueryAsync<Row>($@"
WITH g AS (SELECT du.UserKey, {Domain} AS Domain, {ok} AS Ok FROM bi.DimUser AS du {Ctx.Scope("du.UserKey")} WHERE {guest}),
a AS (SELECT ud.UserKey, SUM(ud.Interactions) AS I, SUM(ud.Jailbreak + ud.PromptInjection + ud.BlockedResource) AS F, SUM(ud.LabeledContent) AS L
      FROM dash.UserDay AS ud JOIN g ON g.UserKey = ud.UserKey WHERE ud.DateKey BETWEEN @k0 AND @k1 GROUP BY ud.UserKey)
SELECT g.Domain, COUNT(*) AS People, COUNT(CASE WHEN a.I > 0 THEN 1 END) AS Active, ISNULL(SUM(CAST(a.I AS BIGINT)), 0) AS Interactions,
       ISNULL(SUM(CAST(a.F AS BIGINT)), 0) AS Flags, ISNULL(SUM(CAST(a.L AS BIGINT)), 0) AS Labeled, COUNT(ld.UserKey) AS Licensed,
       COUNT(CASE WHEN ld.UserKey IS NOT NULL AND g.Ok = 1 THEN 1 END) AS Confirmed
FROM g LEFT JOIN a ON a.UserKey = g.UserKey
LEFT JOIN bi.FactLicensedDay AS ld ON ld.UserKey = g.UserKey AND ld.DateKey = @k1
GROUP BY g.Domain;", Ctx.Args());
            Kpis.Add(new("Guests in Copilot's records", Num(rows.Sum(r => r.People)), "guest and external accounts that used Copilot"));
            Kpis.Add(new("Used Copilot in the period", Num(rows.Sum(r => r.Active)), $"{rows.Sum(r => r.Interactions):#,0} interactions"));
            var licensed = rows.Sum(r => r.Licensed);
            var confirmed = rows.Sum(r => r.Confirmed);
            Kpis.Add(new("Holding a Copilot license", Num(licensed),
                confirmed == 0 ? "licenses in this tenant assigned to guests" : $"licenses in this tenant assigned to guests; {confirmed:#,0} confirmed as a person by an administrator",
                licensed > confirmed ? "warn" : null, Href: licensed > 0 ? Open(Nav.NonHuman, "why=guest") : null));
            Kpis.Add(new("Security signals", Num(rows.Sum(r => r.Flags)), "jailbreak, injection or blocked resource", rows.Sum(r => r.Flags) > 0 ? "crit" : null));
            if (rows.Count == 0) { Notice = $"No guest or external accounts used Copilot in {Here} in the period."; return; }

            var named = Viewer.Has(Roles.Security) && !PrivacyMode;
            var shown = named ? rows : SmallGroups.Fold(rows, r => r.People,
                small => new Row { Domain = $"{small.Count} smaller home organizations", People = small.Sum(r => r.People), Active = small.Sum(r => r.Active), Interactions = small.Sum(r => r.Interactions), Flags = small.Sum(r => r.Flags), Labeled = small.Sum(r => r.Labeled), Licensed = small.Sum(r => r.Licensed) },
                Ctx.MinGroupSize, out _);
            var t = new Table { Headers = ["Home organization", "Guests", "Used Copilot", "Interactions", "Licensed here", "Security signals", "Labeled content"], Numeric = new HashSet<int> { 1, 2, 3, 4, 5, 6 } };
            foreach (var r in shown.OrderByDescending(r => r.Interactions)) t.Rows.Add([r.Domain, r.People, r.Active, r.Interactions, r.Licensed, r.Flags, r.Labeled]);
            Panels.Add(new("By home organization", t.Render(), "From the guest's sign-in name. Labeled content: interactions that touched content with a sensitivity label."));

            if (!named) return;
            var pg = new Paging(Request, new Dictionary<string, string>
            {
                ["most"] = "I DESC", ["name"] = "du.DisplayName", ["upn"] = "du.UserPrincipalName", ["org"] = Domain + ", du.DisplayName, du.UserPrincipalName",
                ["signals"] = "a.F DESC, a.I DESC, du.UserPrincipalName", ["recent"] = "a.LastKey DESC, du.UserPrincipalName",
            }, "most");
            var people = await Db.QueryAsync<(string Upn, string? Name, string Domain, long I, long F, DateTime? Last, int Total)>($@"
WITH a AS (SELECT ud.UserKey, SUM(CAST(ud.Interactions AS BIGINT)) AS I, SUM(CAST(ud.Jailbreak + ud.PromptInjection + ud.BlockedResource AS BIGINT)) AS F, MAX(CASE WHEN ud.Interactions > 0 THEN ud.DateKey END) AS LastKey
           FROM dash.UserDay AS ud WHERE ud.DateKey BETWEEN @k0 AND @k1 GROUP BY ud.UserKey)
SELECT du.UserPrincipalName, du.DisplayName, {Domain}, a.I, a.F, dl.[Date], COUNT(*) OVER ()
FROM bi.DimUser AS du {Ctx.Scope("du.UserKey")} JOIN a ON a.UserKey = du.UserKey AND a.I > 0
LEFT JOIN bi.DimDate AS dl ON dl.DateKey = a.LastKey
WHERE {guest} AND (@q IS NULL OR du.DisplayName LIKE @q OR du.UserPrincipalName LIKE @q)
{pg.OrderAndPage()};", Ctx.Args(new { q = pg.Like }));
            pg.Total = people.FirstOrDefault().Total;
            var pt = new Table
            {
                Headers = ["Name", "Sign-in", "Home organization", "Interactions", "Security signals", "Last used"], Numeric = new HashSet<int> { 3, 4 },
                Server = new(pg, Request, ["name", "upn", "org", "most", "signals", "recent"], new HashSet<int> { 3, 4, 5 }),
            };
            foreach (var r in people) pt.Rows.Add([r.Name, r.Upn, r.Domain, r.I, r.F, r.Last]);
            CsvTable = pt;
            Panels.Add(new("Guests using Copilot", new HtmlString(pg.Controls(Request, new Dictionary<string, string> { ["most"] = "Most use", ["name"] = "Name" }).Value + pt.Render().Value),
                "Shown to the Security role.", "full", CsvUrl()));
        }
    }
}

namespace CopilotInsights.Pages.Compliance
{
    /// <summary>
    /// Everything recorded about one person's Copilot use, for a data-subject access request or an
    /// investigation: their directory and license record, and each interaction, downloadable. Needs
    /// the Compliance role; works in privacy mode (it is a request about one named person); every
    /// look and download is in the audit log with the person it was about.
    /// </summary>
    public sealed class PersonModel(PageServices s) : InsightsPage(s)
    {
        public override string Title => "Person record";
        public override string Persona => "Compliance";
        public override string? RequiredRole => Roles.Compliance;
        public override bool AllowedInPrivacyMode => true;
        /* The box offers people with Copilot records, in the part of the organization the viewer may see. */
        public override CopilotInsights.Data.LookupKind Lookup => CopilotInsights.Data.LookupKind.People;
        /// <summary>The scope here only decides who can be looked up (2.5.0); the team box would drop the person chosen.</summary>
        public override bool UsesTeam => false;
        public override string Intro => "Everything recorded about one person's Copilot use, for a data-subject access request or an investigation. Each look and download is in the audit log, with whose record it was. Copilot's audit records hold no prompt or response text: for the content itself, use Purview eDiscovery with the record ids below.";

        sealed class Who
        {
            public int UserKey { get; set; }
            public string UserPrincipalName { get; set; } = "";
            public string? DisplayName { get; set; }
            public string? Department { get; set; }
            public string? JobTitle { get; set; }
            public string? ManagerName { get; set; }
            public string? CompanyName { get; set; }
            public string? Country { get; set; }
            public string? DirectoryStatus { get; set; }
            public bool? AccountEnabled { get; set; }
            public DateTime? LicensedSinceUtc { get; set; }
            public DateTime FirstSeenUtc { get; set; }
        }

        sealed class Rec
        {
            public DateTime CreatedUtc { get; set; }
            public string App { get; set; } = "";
            public string? Agent { get; set; }
            public bool UsedWebSearch { get; set; }
            public bool JailbreakDetected { get; set; }
            public bool PromptInjectionDetected { get; set; }
            public bool HasBlockedResource { get; set; }
            public bool HasSensitivityLabel { get; set; }
            public int AccessedResourceCount { get; set; }
            public string RecordId { get; set; } = "";
            public int TotalRows { get; set; }
        }

        protected override async Task BuildAsync()
        {
            var upn = Request.Query["upn"].FirstOrDefault()?.Trim();
            var form = $"<form class=\"admin\" method=\"get\" action=\"{E(Request.Path)}\"><input type=\"hidden\" name=\"d\" value=\"{Ctx.Days}\"/>" +
                       PersonBox("upn", "Person", upn, 36, "Type three letters of the person's first or last name and choose them from the names offered, or type their sign-in (name@domain). Only people with Copilot records, in the part of the organization you may see, are offered.") +
                       "<button class=\"btn\" type=\"submit\">Look up</button></form>";
            Panels.Add(new("Whose record", new HtmlString(form), "Names are offered from three letters on, by first or last name. The period above sets how far back the interactions go; choose 365 days for everything the dashboard holds.", "half"));
            if (string.IsNullOrEmpty(upn)) return;

            var who = (await Db.QueryAsync<Who>($@"
SELECT du.UserKey, du.UserPrincipalName, du.DisplayName, du.Department, du.JobTitle, du.ManagerName, du.CompanyName, du.Country, du.DirectoryStatus,
       du.AccountEnabled, du.LicensedSinceUtc, du.FirstSeenUtc
FROM bi.DimUser AS du {Ctx.Scope("du.UserKey")} WHERE du.UserPrincipalName = @upn;", Ctx.Args(new { upn }))).FirstOrDefault();
            if (who is null) { Notice = $"No Copilot records for {upn} among the people you can see. Check the sign-in; someone outside your part of the organization is not shown."; return; }

            var d = new Table { Headers = ["Field", "Value"], Sortable = false };
            d.Rows.Add(["Name", who.DisplayName]); d.Rows.Add(["Sign-in", who.UserPrincipalName]); d.Rows.Add(["Department", who.Department]);
            d.Rows.Add(["Job title", who.JobTitle]); d.Rows.Add(["Manager", who.ManagerName]); d.Rows.Add(["Company", who.CompanyName]); d.Rows.Add(["Country", who.Country]);
            d.Rows.Add(["In the directory", who.DirectoryStatus]); d.Rows.Add(["Account enabled", who.AccountEnabled is null ? null : who.AccountEnabled.Value ? "Yes" : "No"]);
            d.Rows.Add(["Licensed since", who.LicensedSinceUtc]); d.Rows.Add(["First seen in Copilot's records", who.FirstSeenUtc]);
            var lic = (await Db.QueryAsync<(string? Sku, string? Status, DateTime? Updated)>(
                "SELECT SkuPartNumber, CapabilityStatus, UpdatedUtc FROM audit.CopilotLicense WHERE UserPrincipalName = @upn;", new { upn })).FirstOrDefault();
            if (lic.Updated is not null) { d.Rows.Add(["License (license tool)", $"{lic.Sku} {lic.Status}".Trim()]); d.Rows.Add(["License read", lic.Updated]); }
            Panels.Add(new("Directory and license", d.Render(), null, "half"));

            /* The client asked to see one person's use over time, over 7, 14, 30, 90 days and more: by day up to 30 days,
               by week beyond, as every trend. The links are the period above, kept with the person. */
            var trend = await Db.QueryAsync<(DateTime Bucket, long Interactions, long Sessions, int Days)>($@"
SELECT {Ctx.BucketSql("dd")}, SUM(CAST(ud.Interactions AS BIGINT)), SUM(CAST(ud.Sessions AS BIGINT)), COUNT(CASE WHEN ud.Interactions > 0 THEN 1 END)
FROM dash.UserDay AS ud JOIN bi.DimDate AS dd ON dd.DateKey = ud.DateKey
WHERE ud.UserKey = @key AND ud.DateKey BETWEEN @k0 AND @k1
GROUP BY {Ctx.BucketSql("dd")};", Ctx.Args(new { key = who.UserKey }));
            var buckets = Buckets();
            long At(DateTime b, Func<(DateTime Bucket, long Interactions, long Sessions, int Days), long> f) => trend.Where(x => x.Bucket == b).Sum(f);
            var windows = string.Join(" · ", ReportContext.DayChoices.Select(n =>
                $"<a href=\"{E(Ctx.Url(Request.Path, days: n, extra: "upn=" + Uri.EscapeDataString(who.UserPrincipalName)))}\"{(n == Ctx.Days ? " class=\"on\"" : "")}>{n} days</a>"));
            var activeDays = trend.Sum(x => x.Days);
            Kpis.Add(new("Days active", Num(activeDays), $"of {Ctx.Days} in the period"));
            Kpis.Add(new("Interactions a day active", activeDays > 0 ? $"{trend.Sum(x => x.Interactions) / (double)activeDays:#,0.0}" : "—", "on the days they used Copilot"));
            Panels.Add(new("Use over time",
                new HtmlString($"<p class=\"sub\">Show: {windows}</p>" + Chart.Columns(Labels(), [
                    new Series("Interactions", buckets.Select(b => (double?)At(b.Key, x => x.Interactions)).ToList(), 1),
                    new Series("Sessions", buckets.Select(b => (double?)At(b.Key, x => x.Sessions)).ToList(), 3),
                ], Fmt.Count, $"{who.DisplayName ?? who.UserPrincipalName}: interactions and sessions", stacked: false).Value),
                $"Interactions (a prompt and its answer) and sessions (a conversation) for this person, in every app and agent. {TrendNote}", "full"));

            var pg = new Paging(Request, new Dictionary<string, string>
            {
                ["new"] = "f.CreatedUtc DESC, f.RecordId", ["old"] = "f.CreatedUtc, f.RecordId", ["app"] = "a.App, f.CreatedUtc DESC, f.RecordId",
                ["agent"] = "CASE WHEN ag.AgentSource = N'(no agent)' THEN NULL ELSE ag.AgentName END, f.CreatedUtc DESC, f.RecordId",
                ["web"] = "f.UsedWebSearch DESC, f.CreatedUtc DESC, f.RecordId",
                ["signals"] = "CAST(f.JailbreakDetected AS INT) + CAST(f.PromptInjectionDetected AS INT) + CAST(f.HasBlockedResource AS INT) DESC, f.CreatedUtc DESC, f.RecordId",
                ["labeled"] = "f.HasSensitivityLabel DESC, f.CreatedUtc DESC, f.RecordId", ["resources"] = "f.AccessedResourceCount DESC, f.CreatedUtc DESC, f.RecordId",
                ["record"] = "f.RecordId",
            }, "new");
            var recs = await Db.QueryAsync<Rec>($@"
SELECT f.CreatedUtc, COALESCE(vn.Name, a.App) AS App, CASE WHEN ag.AgentSource = N'(no agent)' THEN NULL ELSE ag.AgentName END AS Agent, f.UsedWebSearch, f.JailbreakDetected, f.PromptInjectionDetected,
       f.HasBlockedResource, f.HasSensitivityLabel, f.AccessedResourceCount, f.RecordId, COUNT(*) OVER () AS TotalRows
FROM bi.FactInteraction AS f
JOIN bi.DimApp AS a ON a.AppKey = f.AppKey
LEFT JOIN dash.ValueName AS vn ON vn.ColumnId = 2 AND vn.Value = a.App   -- 2.7.1: the app's name
JOIN dash.Agent AS ag ON ag.AgentKey = f.AgentKey
WHERE f.UserKey = @key AND f.DateKey BETWEEN @k0 AND @k1
{pg.OrderAndPage()};", Ctx.Args(new { key = who.UserKey }), timeout: 300);
            pg.Total = recs.FirstOrDefault()?.TotalRows ?? 0;
            Kpis.Add(new("Interactions in the period", Num(pg.Total), "this person's prompts to Copilot and its answers"));
            Kpis.Add(new("With a security signal", Num(recs.Count(r => r.JailbreakDetected || r.PromptInjectionDetected || r.HasBlockedResource)), pg.ForExport || pg.Total <= Paging.PageSize ? null : "on this page"));
            var t = new Table
            {
                Zone = Zone, Headers = ["When (UTC)", "App", "Agent", "Searched the web", "Security signals", "Touched labeled content", "Resources read", "Record id"],
                Numeric = new HashSet<int> { 6 }, EmptyText = "No interactions in the period.",
                Server = new(pg, Request, ["new", "app", "agent", "web", "signals", "labeled", "resources", "record"], new HashSet<int> { 0, 3, 4, 5, 6 }),
            };
            foreach (var r in recs)
            {
                var flags = string.Join(", ", new[] { r.JailbreakDetected ? "Jailbreak" : null, r.PromptInjectionDetected ? "Injection" : null, r.HasBlockedResource ? "Blocked" : null }.Where(x => x is not null));
                t.Rows.Add([r.CreatedUtc, r.App, r.Agent, r.UsedWebSearch ? "Yes" : "", flags, r.HasSensitivityLabel ? "Yes" : "", r.AccessedResourceCount, r.RecordId]);
            }
            CsvTable = t;
            Panels.Add(new("Interactions", new HtmlString(pg.Controls(Request, new Dictionary<string, string> { ["new"] = "Newest", ["old"] = "Oldest" }).Value + t.Render().Value),
                "The download holds every interaction in the period, not only this page. Record ids find each one in Purview Audit and eDiscovery.", "full", CsvUrl()));
        }
    }
}

namespace CopilotInsights.Pages.Workforce
{
    /// <summary>Adoption by job family: job titles grouped by rules an administrator sets.</summary>
    public sealed class FamiliesModel(PageServices s, OpsData ops) : InsightsPage(s)
    {
        public override string Title => "Job families";
        public override string Persona => "People";
        public override string Intro => "Copilot use by job family, for comparing like with like: job titles grouped by the rules in Admin › Classifications and events (engineers, analysts, relationship managers...). Families smaller than the minimum group size are combined.";

        protected override async Task BuildAsync()
        {
            if (!await ops.HasAsync("dash.JobFamilyRule")) { Notice = "Job families are not set up here yet. An administrator sets this up by running Install-CopilotAudit.ps1 -Dashboard from the current release."; return; }
            var rules = await Db.ScalarAsync<int>("SELECT COUNT(*) FROM dash.JobFamilyRule;");
            var rows = await Db.QueryAsync<GroupRow>($@"
WITH act AS (SELECT ud.UserKey, SUM(ud.Interactions) AS I, SUM(ud.LicensedUse) AS L, COUNT(CASE WHEN ud.Interactions > 0 THEN 1 END) AS D,
                    SUM(ud.Sessions) AS S, SUM(ud.MultiTurnSessions) AS MT
             FROM dash.UserDay AS ud WHERE ud.DateKey BETWEEN @k0 AND @k1 GROUP BY ud.UserKey),
fam AS (SELECT du.UserKey, ISNULL(r.Family, N'(no family)') AS Family
        FROM bi.DimUser AS du {Ctx.Scope("du.UserKey")}
        OUTER APPLY (SELECT TOP (1) jr.Family FROM dash.JobFamilyRule AS jr WHERE du.JobTitle LIKE jr.Pattern ORDER BY jr.Priority, jr.RuleId) AS r
        WHERE du.DirectoryStatus = N'Found' AND du.AccountEnabled = 1)
SELECT fam.Family AS Name, COUNT(*) AS People, COUNT(ld.UserKey) AS Licensed,
       COUNT(CASE WHEN a.I > 0 THEN 1 END) AS Active, COUNT(CASE WHEN a.L > 0 AND ld.UserKey IS NOT NULL THEN 1 END) AS ActiveLicensed,
       ISNULL(SUM(CAST(a.I AS BIGINT)), 0) AS Interactions, ISNULL(SUM(CAST(a.D AS BIGINT)), 0) AS ActiveDays,
       ISNULL(SUM(CAST(a.S AS BIGINT)), 0) AS Sessions, ISNULL(SUM(CAST(a.MT AS BIGINT)), 0) AS MultiTurn
FROM fam LEFT JOIN act AS a ON a.UserKey = fam.UserKey
LEFT JOIN bi.FactLicensedDay AS ld ON ld.UserKey = fam.UserKey AND ld.DateKey = @k1
GROUP BY fam.Family;", Ctx.Args(), timeout: 300);
            var kept = Groups.Fold(this, rows, Ctx.MinGroupSize, out var folded);
            var unmatched = rows.FirstOrDefault(r => r.Name == "(no family)")?.People ?? 0;
            Kpis.Add(new("Job families", Num(rows.Count(r => r.Name != "(no family)")), rules == 0 ? "no rules yet" : $"from {Count(rules, "rule")}"));
            /* Counted only when "(no family)" is a row of its own (2.4.12): folded into "Smaller groups", its count here
               was the one, two or three people the fold kept from view. */
            if (unmatched == 0 || kept.Any(r => r.Name == "(no family)"))
                Kpis.Add(new("People without a family", Num(unmatched), rows.Sum(r => r.People) > 0 ? $"{Pct(Ratio(unmatched, rows.Sum(r => r.People)))} of people in {Here}" : null, unmatched > 0 && rules > 0 ? "warn" : null));
            if (rules == 0) Notice = "No job family rules yet: everyone is \"(no family)\". An administrator adds them in Admin › Classifications and events, with the titles that need one listed there.";
            Panels.Add(new("Adoption by job family", Groups.AdoptionBars(kept, "Adoption by job family", Ctx.MinGroupSize), "Licensed people who used Copilot, over people licensed.", "half"));
            var t = Groups.Table(kept, "Job family", Ctx.MinGroupSize);
            CsvTable = t;
            Panels.Add(new("Job families", t.Render(), folded > 0 ? FoldNote(folded, "job family", "job families") : null, "full", CsvUrl()));
        }
    }
}
