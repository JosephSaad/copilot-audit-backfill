# Copilot Insights dashboard

A web dashboard over the Copilot audit data. Everyone signs in with their Windows account and sees the part of the organisation they are entitled to. Executives and anyone granted it see everything; a manager sees their own people; everyone else sees their own use. Pages are grouped by persona. A web-based admin module handles access, reference data, health and the audit log.

It is an ASP.NET Core 10 (Razor Pages) site hosted in IIS on Windows Server 2022 or later. It reads the Copilot database the pipeline already fills: the `bi` star schema, plus its own `dash` schema of settings, roles, the org chart and daily rollups. It has no other data store and calls no outside services.

**Status: not yet released.** Every page, the security model, the admin changes and the rollups were tested against a synthetic 2,317-person demo database and by the SQL integration suite. It has not yet run on a Windows Server under IIS, or against the lab's data. See [Not yet proven](#not-yet-proven).

## Contents

- [What each persona gets](#what-each-persona-gets)
- [Who sees what](#who-sees-what)
- [Periods](#periods)
- [Long lists and exports](#long-lists-and-exports)
- [Installing it on IIS](#installing-it-on-iis)
- [Enrolling the first administrator](#enrolling-the-first-administrator)
- [Running it](#running-it)
- [Trying it on a demo database](#trying-it-on-a-demo-database)
- [Not yet proven](#not-yet-proven)

## What each persona gets

Pages without a role show aggregates only: counts, rates and trends for the viewer's scope, broken down one level at a time. Pages marked with a role list named people. They need that role, and even then they show only people inside the viewer's own scope. The navigation and the start page list only the pages the viewer can open: someone without a role never sees its pages, an administrator's Admin pages are invisible to everyone else, and a persona with nothing open to the viewer is left out.

| Persona | Page | What it answers | Grain | Named people |
|---|---|---|---|---|
| Leadership | Overview | People using Copilot, licensed, adoption, active in the last 7 days; trends; adoption by the next level down | Scope, then one level down | No |
| | Organisation | Each unit one level down side by side: people, licensed, active, adoption, days active, interactions, multi-turn share, a trend line; heatmap of share active per week | One level down | No |
| | Apps and agents | Interactions by app over time, Copilot Chat and agent share, most-used agents | Scope | No |
| Adoption | Funnel and trend | Licensed → started → active → active this week → multi-turn; first-time users over time | Scope | No |
| | Licence cohorts | Of the people licensed each month, the share active in weeks 1 to 12 after; time from licence to first use | Monthly cohorts | No |
| | Departments | Licensed, active and adoption per AD department; small departments combined | Department | No |
| | Not started | People licensed 14 days or more who have never used Copilot | Person | **Adoption** |
| Licensing | Inventory | Licences held over time, assigned and removed, by SKU, idle licences by unit | Scope, SKU, unit | No |
| | Unused licences | People holding a licence with no use for 7, 14, 30, 60 or 90 days, and when they last used it | Person | **Licensing** |
| | Demand | Unlicensed Copilot Chat users over time and by department; the licence type each record reports; the list of them | Department; person | List: **Licensing** |
| Security | Signals | Jailbreak, prompt injection and blocked resources over time, by weekday and hour, by app, per 1,000 interactions by unit | Scope, unit | No |
| | Flagged interactions | Each flagged interaction: when, who, app, agent, the signal and the Purview record id | Interaction | **Security** |
| Compliance | Sensitive content | Interactions with labelled content by label and over time; the sites that content lives on; sites Copilot was blocked from; everything it read, by type. Personal OneDrive sites are grouped | Scope, label, site, type | No |
| | Resource access | Who worked with labelled content, how often, their most sensitive label, how many sites, how often blocked; filter by label | Person | **Compliance** |
| Agents | Agents | Every agent used: people, how many came back, interactions, first use, a trend line; the top six over time | Agent | No |
| | Agent detail | One agent: people and new people, return rate, trend, by department and app; its users | Agent; person | List: **Agents** |
| Enablement | Engagement | Segments (power, regular, occasional, one-off, not started) overall and by department; multi-turn share and prompts per session; apps per person | Scope, department | No |
| | People | Named people in a segment: champions to recruit, one-off users to train | Person | **Enablement** |
| Governance | Models and grounding | Model mix over time, web grounding, Copilot Chat against in-app, memory updates, calls to another agent, deferred DLP checks, distinct agents in use, agents known by source | Scope; organisation-wide for agents known | No |
| Admin | Health, Access, Reference data, Audit log | See [Running it](#running-it) | | **Admin** |

An L2 manager, who reports directly to the CEO, sees the whole organisation. The Leadership pages are theirs, and every other aggregate page is open to them too. Whole-organisation access doesn't include named lists: those still need the role.

## Who sees what

**Scope.** Every query is restricted to one part of the organisation before anything is counted:

- **The whole organisation** for the person set as its top (`OrgTopUpn`, usually the CEO), everyone who reports directly to them, and anyone granted the **Organization** role, wherever they sit.
- **Themselves and everyone under them, at any depth,** for everyone else, from the manager chain in Active Directory (`audit.UserDirectory`). Someone with nobody under them sees their own use.
- **Nothing** for someone who is not in the directory, unless granted Organization.

A viewer can look inside any part of their own scope, one level at a time. The breadcrumb and the "Look inside" list offer only what they may open. Each request's `org` value is checked against the org chart on the server, so editing the URL can't widen a scope.

**Roles** are granted in Admin › Access to a person (UPN or `DOMAIN\user`) or an AD group (`DOMAIN\Group`). Group membership is read from the Windows sign-in, so a group grant follows AD with no database change.

| Role | Gives |
|---|---|
| Admin | The admin module. **No usage data by itself.** |
| Organization | The whole organisation's aggregates, at any level |
| Adoption | Named people who were licensed but have not started |
| Licensing | Named unused licences and named unlicensed Copilot Chat users |
| Security | Named flagged interactions |
| Compliance | Named people who worked with labelled content; personal OneDrive site names |
| Agents | Named users of an agent |
| Enablement | Named people by engagement segment |

**Small groups.** A breakdown never shows a group smaller than the minimum group size (default 5): those groups are folded into "Smaller groups" or left out, so no total describes one person.

**The audit log.** Every page view, refusal, CSV export and admin change is written to `dash.AuditLog` with who, which page, what scope and which filters, and the client address.

**How a sign-in becomes a person.** IIS authenticates with Negotiate (Kerberos, falling back to NTLM). The site then resolves `DOMAIN\user` to a UPN through Active Directory, or through `Insights:IdentityMap` in the settings for accounts AD can't resolve, and looks the UPN up in `dash.Person`.

## Periods

Every report has the same choice: the last **7, 14, 30, 90, 180 or 365 days**, ending on the last complete day with data. The default is 30, which admins can change. Periods of 30 days or less trend by day. Longer ones trend by Saturday-to-Friday week, labelled by the Friday, and the first and last week may be partial.

A few views set their own window, and each one says so on the page:

- **Licence cohorts:** the last 12 months of licence starts.
- **Unused licences:** today's licences against an idle time chosen on the page.
- **Agents known:** every agent the log has ever seen.

## Long lists and exports

Every list of people is paged on the server: 50 rows per page, a search box (name, sign-in, department), and a fixed set of sorts. Only one page of rows ever leaves the database, so a list of 40,000 people costs the same to show as a list of 50. **Download CSV** exports every row matching the current search and sort, UTF-8 with a BOM for Excel. Cells that would start a spreadsheet formula are prefixed with `'`, and each export is audit-logged with its row count.

The pages read two rollups rather than the raw facts:

- **`dash.UserDay`:** one row per person per day with any use.
- **`dash.ResourceDay`:** labels and sites per person per day.

Both are rebuilt incrementally with the rest of the pipeline. On the demo (2,317 people, 180,000 interactions), pages take 50–300 ms and the cohort page 0.6 s.

## Installing it on IIS

### Prerequisites

- Windows Server 2022 or later, joined to the domain whose users will sign in.
- The ASP.NET Core 10 **Hosting Bundle**, installed beforehand or passed to the installer with `-HostingBundlePath`.
- The Copilot database with the `bi` schema filled (deployment steps 8–11). Install the dashboard's two scripts there, in this order:
  ```
  sql/Create-CopilotDashboardSchema.sql
  sql/Create-CopilotDashboardProcedure.sql
  ```
  Then run the procedure once, and set the top of the organisation:
  ```sql
  EXEC dash.usp_RefreshDashboard;
  UPDATE dash.Setting SET Value = N'ceo@contoso.com' WHERE Name = 'OrgTopUpn';
  EXEC dash.usp_RefreshDashboard;   -- levels follow the new top
  ```
- An account for the app pool. A **group managed service account** is best: it needs no password and can have its own SPN.
- A certificate for the site's host name, in LocalMachine\My.

### Run the installer

In an elevated **Windows PowerShell 5.1** (`powershell.exe`, not PowerShell 7: the IIS provider it uses exists only there):

```powershell
.\dashboard\Install-CopilotInsights.ps1 -PackagePath .\CopilotInsights `
    -SqlServer SQL01 -Database CopilotAudit `
    -AdminPrincipal 'CONTOSO\Copilot-Insights-Admins' `
    -AppPoolIdentity 'CONTOSO\gmsa-insights$' `
    -HostName insights.contoso.com -CertificateThumbprint 3F2A... `
    -GrantDatabaseAccess
```

`-PackagePath` is the published app, as a folder or a zip. It can also be the `dashboard\CopilotInsights` source folder, which the installer publishes itself when the .NET 10 SDK is installed. The installer is idempotent. It:

1. Installs the IIS role services: Windows authentication, static content, logging, request filtering and the management console.
2. Checks for the Hosting Bundle, and installs it from `-HostingBundlePath` when given.
3. Copies the site to `C:\inetpub\CopilotInsights`, keeping `appsettings.Production.json`. It takes the site offline with `app_offline.htm` while the files change.
4. Writes `appsettings.Production.json`: the connection string, and the bootstrap administrators under `Insights:BootstrapAdmins`.
5. Creates the app pool (no managed code, always running) and the site, with an HTTPS binding.
6. Turns Windows authentication on and anonymous authentication off.
7. Writes the database grants for the app-pool account to `Grant-CopilotInsightsAccess.sql`, and runs them with `-GrantDatabaseAccess`, which needs rights to create logins:
   - SELECT on the `bi` and `dash` schemas;
   - changes to the `dash` settings, roles and reference tables;
   - INSERT on the audit log;
   - EXECUTE on the refresh procedure;
   - SELECT on `audit.UserDirectory` and the pipeline's run logs;
   - changes to `audit.SessionAppCategory`.

For Kerberos on a host name, register the SPN on the app-pool account: `setspn -S HTTP/insights.contoso.com CONTOSO\gmsa-insights$`. Add the host name to the browsers' Local intranet zone so they sign in silently.

Host it as a site of its own, not as an application under another site: its links are rooted at `/`.

## Enrolling the first administrator

The site's configuration names its first administrators. The database's grants don't, so there is always a way in:

1. Run the installer with `-AdminPrincipal`, naming an AD group (recommended) or a person as `DOMAIN\user` or a UPN. It writes them to `Insights:BootstrapAdmins` in `appsettings.Production.json`.
2. A member signs in and opens **Admin › Access**. They set the top of the organisation, then grant roles, including Admin for the people who will run it day to day.
3. Bootstrap administrators aren't listed in Admin › Access and can't be removed there. To change them, edit the settings file, or run the installer again with a new `-AdminPrincipal`, then recycle the app pool.

If the settings file is lost and nobody holds Admin, a DBA can grant it directly:

```sql
INSERT INTO dash.RoleGrant (Principal, PrincipalType, Role, Note, GrantedBy)
VALUES (N'CONTOSO\Copilot-Insights-Admins', 'Group', 'Admin', N'recovery', SUSER_SNAME());
```

Admin shows no usage data by itself. An administrator who should also see the reports needs their level or the Organization role, like anyone else.

## Running it

**Refresh.** Nothing extra to schedule. `Invoke-CopilotAuditBackfill.ps1 -Incremental` and the CSV importer run `dash.usp_RefreshDashboard` last, after `bi.usp_RefreshBi`, from the same day. Each refresh rebuilds the org chart from `audit.UserDirectory` in full, so the scopes follow AD from the night after a move. **Admin › Health** can refresh the last 7 days, or rebuild everything, by hand.

**Admin › Health.** Each pipeline step's last run and its age, marked fresh, stale or old. It also shows:

- whether the top of the organisation is set and found;
- how many people sit under it;
- how many people in the data are matched to AD;
- the latest day with data;
- the last ten dashboard refreshes.

**Admin › Access.** The settings (top of the organisation, minimum group size, default period, unused-after days), granting and removing roles, and checking one person's effective access.

**Admin › Reference data.** Friendly names and owners for agents, which the audit log names by schema name. Names and sensitivity order for labels, which it records by id only (`Get-Label` lists them). Which apps count as Copilot Chat.

**Admin › Audit log.** The log, filtered by period, kind and search, and exportable.

Every admin change needs the Admin role and a valid anti-forgery token, and is written to the audit log.

## Trying it on a demo database

`dashboard/demo/New-InsightsDemo.ps1` builds a synthetic company: 2,317 people in six divisions, with six months of Copilot use, licences, agents, labels and a few security signals. It builds it with the repository's own scripts, exactly as an install would. It also grants one director each persona role.

```powershell
.\dashboard\demo\New-InsightsDemo.ps1 -SqlServer localhost -Database CopilotInsightsDemo -TrustServerCertificate -Force -Admins 'CONTOSO\me'
cd dashboard\CopilotInsights
dotnet run --launch-profile https
```

In Development, an administrator can add `?as=someone@contoso.com` to any page to see the dashboard as that person, and `?as=` to stop. The script prints who to try:

- the CEO;
- the heads of division, who are L2;
- a manager, and someone with nobody under them;
- the persona-role holders.

View-as is ignored outside Development.

## Not yet proven

- **The IIS installer** has only been parsed under Windows PowerShell 5.1. It has not been run on a Windows Server.
- **The lab's own data**, and anything at the real organisation's size (18,000 people now, 40,000 expected). The demo has 2,317. The rollups are sized per active person per day, and every list is paged in SQL, but the page timings above are the demo's.
- **Kerberos on a host name, with a gMSA,** in a real domain.
