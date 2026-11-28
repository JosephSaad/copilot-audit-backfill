# Operations guide

What to do once copilot-audit-backfill and the Copilot Insights dashboard are installed: every task an operator performs, where to do it, and the command for it. [DEPLOYMENT.md](DEPLOYMENT.md) covers installing; [DASHBOARD.md](DASHBOARD.md) explains the dashboard's pages and rules; [RUNBOOK.md](../RUNBOOK.md) is the pipeline's reference, with limits, tuning and every error message.

Two places do everything:

- **The dashboard's Admin pages**, for people holding the Admin role. Every change is checked, needs the page's anti-forgery token, and is written to the audit log.
- **`tools\Invoke-CopilotMaintenance.ps1`** on the job server, for the same tasks without the dashboard, in scripts, or when nobody can sign in. Below, `M` stands for `.\tools\Invoke-CopilotMaintenance.ps1 -SqlServer SQLPROD01` (add `-Database` if it is not `CopilotAudit`, and `-TrustServerCertificate` for a self-signed SQL Server certificate).

Nothing here needs SQL typed by hand, but for looking inside a slow night ([A slow nightly run](#a-slow-nightly-run)), which is the database administrator's. `tests/Test-OperationsGuide.ps1` fails the build when an Admin page or a maintenance task is not in this guide, so the [reference](#every-admin-page-and-maintenance-task) at the end is complete.

## Contents

- [Every day](#every-day)
- [Access and permissions](#access-and-permissions)
- [Access profiles](#access-profiles)
- [Names for codes and ids](#names-for-codes-and-ids)
- [Email, alerts and digests](#email-alerts-and-digests)
- [Targets, value and training](#targets-value-and-training)
- [Costs and savings](#costs-and-savings)
- [How figures are calculated](#how-figures-are-calculated)
- [Copilot reservations](#copilot-reservations)
- [Copilot Credit chargeback and commitment](#copilot-credit-chargeback-and-commitment)
- [Model tokens, spend limits and agent policies](#model-tokens-spend-limits-and-agent-policies)
- [AI apps, Power BI and Power Apps](#ai-apps-power-bi-and-power-apps)
- [Classifications, events and extracts](#classifications-events-and-extracts)
- [Acting on the license recommendations](#acting-on-the-license-recommendations)
- [Triage and investigations](#triage-and-investigations)
- [Privacy, retention and the data API](#privacy-retention-and-the-data-api)
- [The pipeline](#the-pipeline)
- [A slow nightly run](#a-slow-nightly-run)
- [More than one tenant](#more-than-one-tenant)
- [Upgrades and renewals](#upgrades-and-renewals)
- [Governance Workbench](#governance-workbench)
- [Every Admin page and maintenance task](#every-admin-page-and-maintenance-task)

## Every day

Nothing needs doing while it is healthy: the nightly task loads and builds everything, and the dashboard raises an alert when something goes wrong. To look:

| Check | Where | Healthy |
|---|---|---|
| Is the data current, and did the last run work | **Admin › Health**, the three figures at the top (2.4.10): *Reports show data up to*, *Last pipeline run*, *Steps behind* | Yesterday or the day before; *Succeeded*; 0 |
| Last night's run | **Admin › Health**, *Pipeline runs*; or `M -Task Status` | *Succeeded*, finished within the last day |
| Data freshness | Every page shows a yellow banner when the data is older than *FreshnessHours* (36) or the last run failed | No banner |
| Alerts sent, and any that could not be | **Admin › Email**, *Recently sent* | *Sent* |
| Directory coverage | **Admin › Health**, *Directory and security model* | 95% or more of people matched to AD and placed under the top |
| The app certificates and audit searches | **Admin › Health**, *Certificates and audit searches* | Each certificate found on the job server and more than 30 days from expiry (the Purview audit app's, and the SharePoint agent workbook app's where one is set up); no failed searches, few waiting |
| Gaps in AD | **Admin › Directory quality** | Few people without a manager, department, title or email |
| Every report built ahead this morning (2.5.1) | **Admin › Health**, *Report speed*, on each web server; or `precache\precache.log` in the site's folder | *Last pre-cache* today, *built*, the slowest page from memory under a second |

When the last run failed, its step and last log lines are on Admin › Health and in the pipeline alert; the full log is that run's `logs\nightly-<date>-<time>.log` on the job server. The next night's run resumes it; to resume now, run `.\Invoke-CopilotPipeline.ps1` on the job server. [RUNBOOK.md](../RUNBOOK.md) lists every error and its fix.

**Every page says the audit log could not be written (HTTP 503, 2.4.12).** The dashboard serves nothing it cannot record in `dash.AuditLog`, so when the database takes reads but not writes, every page, export and API call is refused with that message, Admin › Health too. Check the SQL Server: a full transaction log or disk, a database set read-only, or the site's account without INSERT on `dash.AuditLog` (the installer's `-GrantDatabaseAccess` grants it again). Pages come back as soon as the write works; nothing needs restarting.

**Several web servers.** Set `Insights:DataProtectionKeysPath` to a share on every web server, or forms posted to a server other than the one that drew them are refused (HTTP 400); change `appsettings.Production.json` on every server. They share one report cache in the database (2.7.6), and one of them builds the pages each morning; the load balancer's probe is `/healthz`. [DASHBOARD.md](DASHBOARD.md#several-web-servers) has the steps.

**The morning pre-cache (2.5.1).** Every day at 07:00 Eastern Time (EST in winter, EDT in summer), the scheduled task *Copilot Insights pre-cache* on each web server runs `Invoke-InsightsPrecache.ps1` from the site's folder. The site builds every report page again, for the whole organization at every period, reading each query afresh, then builds each page once more from memory and times it. Whoever opens a page during the build gets the result kept before, so nobody waits. Each run adds its result to `precache\precache.log` in the site's folder, and the task's last result is its exit code: 0 built, every page from memory in under a second; 2 built, with the pages that took longer listed; 1 not built, with why. The pages for a part of the organization, and a detail page (one app, one agent), are kept from the first time someone opens them; the pre-cache builds the whole organization's. To run it now, after a restart for example, run it by hand in a PowerShell window as an administrator, from the site's folder (`C:\inetpub\CopilotInsights` unless installed elsewhere): it waits, then says how it went.

```powershell
cd C:\inetpub\CopilotInsights
.\Invoke-InsightsPrecache.ps1                                                     # build every page now, and wait
.\Invoke-InsightsPrecache.ps1 -Register -At 06:30 -TimeZone 'Eastern Standard Time'   # another time
.\Invoke-InsightsPrecache.ps1 -Unregister                                         # no pre-cache
```

The dashboard installer schedules it (07:00 Eastern unless given `-PrecacheAt` and `-PrecacheTimeZone`), and a later run keeps the time it has. *Not built: the site did not take the request* means the app pool is stopped, or the site cannot change its `precache` folder: run the installer again, which grants it.

## Access and permissions

**How access works.** Everyone signs in with their Windows account, or, on a site set up for it, through Microsoft Entra ID (2.8.0, [ENTRA-SIGN-IN.md](ENTRA-SIGN-IN.md)). **Every section of the menu needs a grant that opens it (3.1.0):** a section no grant opens for a person is not in their menu or on their start page, and its reports, files, data API calls and digests are refused, whatever their level. Someone with nothing granted sees only a start page that says they have no access yet and names the administrators. Levels decide how much a granted section shows: the top of the organization, the people who report directly to them and anyone granted **Organization** see the whole organization; everyone else sees themselves and the people under them, unless their grant says the whole organization. Lists that name people need *Named people* on the grant. **Admin** runs the Admin pages and sees no usage data by itself. [DASHBOARD.md](DASHBOARD.md#who-sees-what) has the full rules.

**The sections.** Each header in the menu is one section: Leadership, Adoption, Licensing, Security, Compliance, Agents, Enablement, Forecasting, Regions, People, App owners and Governance, and, where the Governance Workbench is on, its Ownership and approvals and Ownership registry. A grant names any of them, for the person's own team or the whole organization, with or without the lists that name people. The persona roles granted before 3.1.0 keep opening their own section with its named lists, as they did (Licensing: Licensing and Forecasting); they are no longer offered, since a section grant with *Named people* gives the same.

| Role | Gives |
|---|---|
| Sections (3.1.0) | The menu sections ticked on the grant, for the person's own team or the whole organization, with or without the lists that name people. Granted in *Grant access* |
| Admin | The Admin pages: all fifteen, or only the ones ticked for that person or group (2.6.4) |
| Organization | Every section the person is granted shows the whole organization, whatever their level. It opens no section by itself |
| Adoption | Named people licensed but not started |
| Licensing | Named unused licenses, Copilot Chat users, licenses on disabled accounts, upgrade/train/revoke lists, the use ranking, rollout waves, license action outcomes, and their export files |
| Security | Named flagged interactions and their triage; named guests |
| Compliance | Named people who worked with labeled content; one person's full record (works in privacy mode) |
| Agents | Named users of an agent |
| Enablement | Named champions and engagement segments |
| Platforms (3.3.0) | The AI platforms section's lists that name people: who holds each GitHub Copilot seat, idle seats, and seats held by people who have left (as the GitHub pages come). A Sections grant ticking *AI platforms* with *Named people* gives it; `M -Task GrantRole -Role Platforms` grants it alone |
| Governance, GovernanceOperator, GovernanceAuditor | Pages of the Governance Workbench (3.0.0), when it is turned on: see [Governance Workbench](#governance-workbench). No usage data |
| Preview (3.1.6; every beta report from 3.2.0) | Every report marked Beta in Admin › Reports shown, inside the sections the person sees already. Ticked in *Grant access* › *Other roles*. A grant made in 3.1.6 that names some reports opens those only, until Preview is ticked for them again. Opens no section and lets no one past the level limit |
| Stories (3.1.6) | Curates the success stories: sees every story, edits, approves or declines it, and ticks the ones the weekly report quotes. No usage data; lets no one past the level limit |

**Grant access (3.1.0).** Admin › Access › *Grant access*, in steps: (1) who, a person (their UPN, or `DOMAIN\user`), an AD group (`DOMAIN\Group`) or an Entra group; (2) a profile to start from, if any; (3) the sections they see, as boxes laid out like the menu, each with an information icon naming its reports and its lists that name people; (4) *Their own team* or *Whole organization*; (5) *Named people*, for the lists that name people in the ticked sections; (6) other roles: Organization, Admin (with its pages) and, where the Workbench is on, its work roles; and why. Granting sections again to a person or group who has them replaces their choice, and the message says what it was and what it is now; *Change* beside a section grant in the grants list opens it in the form. Before 3.1.0 this was *Grant a role*: the roles, and why. Grant to groups where you can: membership is read from each sign-in, so it follows AD with no change here. Power BI's *Organization* role honors the same grants from the next nightly directory load, which records each `DOMAIN\` grant's members (a person's own UPN, from 2.4.12). The grants list has one row per person or group with all their roles, and a small × box beside each role that removes it, after asking. *Remove all*, at the end of the roles, removes every role that person or group holds (2.6.3). To clear several at once, tick their rows (*Select all on this page* ticks every row shown) and choose *Remove every role from the selected* under the table. Both ask first, and the audit log records who was cleared and the roles each held. Someone cleared keeps what their AD groups give them, and no section their level would have opened: since 3.1.0 levels open none. Grant executives the sections they should see, Leadership included.

```powershell
M -Task GrantRole -Principal 'USERS\Copilot-Insights-Executives' -PrincipalType Group -Sections leadership -WholeOrganization -Note 'executives'
M -Task GrantRole -Principal 'USERS\Copilot-Insights-Licensing' -PrincipalType Group -Sections licensing,forecasting -NamedPeople -Note 'licensing team'
M -Task RevokeRole -Principal 'ada.lovelace@contoso.com' -Role Sections
```

**Limit an administrator to some Admin pages (2.6.4).** Admin › Access › *Grant access* › *Other roles*: tick **Admin**, then tick, under *Admin pages*, the pages that person or group should run, any of the fifteen: Health, Access, Access profiles (2.8.0), Directory quality, Email, Alerts and digests, Targets and assumptions, Privacy and retention, Reference data, Value names, Classifications and events, Reports shown, Scheduled extracts, Training records and Audit log. They see those pages in the navigation and no other Admin page, can change what is on them, and are refused the rest, page, form and file. With none ticked, or all fifteen, Admin is the whole role, as every Admin grant was before.

- **To change someone's pages,** grant Admin to them again with the new pages ticked: the pages are replaced, and the message says what the role now covers. Granting it again with none ticked makes them a full administrator.
- **The grants list** shows a limited Admin role with its pages in brackets, and *Check someone's access* says what a person's Admin role covers.
- **Only a full administrator gives, limits or removes Admin.** Someone limited to Admin › Access can grant and remove the other roles and change that page's settings, and cannot make themselves, or anyone, an administrator. Nobody can limit their own Admin role.
- **A person in several grants** gets the pages of all of them (their own and their groups'); one whole Admin grant among them makes them a full administrator.
- **Outside the dashboard's levels,** a limited administrator keeps their Admin pages, as a full one keeps all of them.
- **The data API's `health` call** answers whoever may open Admin › Health.
- **From the job server,** `M -Task GrantRole ... -Role Admin` grants the whole role; limit it from the page. Administrators named in the site's configuration (`Insights:BootstrapAdmins`) are always full administrators.
- **Before the database is upgraded,** a 2.6.4 dashboard treats every Admin grant as whole and offers no pages to tick: run `Install-CopilotAudit.ps1 -SqlServer <server> -Dashboard`, which adds the column the pages are kept in.

**Set the time zone.** Admin › Access › *Settings* › *Time zone* (2.4.10). Every time the dashboard shows (the audit log, pipeline runs, when a license was read, when an alert went out) and the hour digests, alerts and extracts are sent is in this zone, and each column says which ("When (Eastern Time)"); before, they were in UTC. Left on *Default* it is the zone the data's days are cut in, which a new install takes from its server (and an install made before 2.4.10 without `-ReportingTimeZone` has as Eastern time); on a database without one, the web server's own. Choosing a zone here changes what is shown and when things are sent, at once. It does not move the days in the reports: those are cut when the data is loaded and cannot be changed afterwards.

**Check what someone sees.** Admin › Access › *Check someone's access*: their level, their scope, whether the level limit keeps them out, a table of every section with whether they see it, how much of the organization, whether its named lists show, and the grant, profile or group behind it ("No grant opens it" where none does), the roles granted to their account directly, those that come through an access profile, and the roles that reach them through a group, each with the group's name. AD group members are as of the nightly directory load, which records the members of every AD group named in a grant or a profile grant; the dashboard itself asks Windows at sign-in, so a change in AD reaches their access first and this panel the next morning. Entra groups are asked of Microsoft Graph as the panel is drawn, by the person's Entra object ID, which the license tool records (2.8.0).

**The whole organization on a grant (2.8.0; sections from 3.1.0).** *Grant access* › *How much of the organization* › *Whole organization*: the sections ticked then show the whole organization to the holder, where they otherwise show the holder's own part of it. It widens those sections only: Agents for the whole organization leaves Leadership and the other sections at the holder's own part, and the minimum group size and privacy mode apply as before. A persona role granted across the whole organization before 3.1.0 keeps widening its own section. The grants list shows *whole organization* beside such a grant. Organization still gives every section the person has the whole organization.

**Who loses access (3.1.0).** Before 3.1.0, Leadership, Regions, People, App owners and Governance were open to everyone the dashboard let in, and the Workbench's Ownership and approvals and Ownership registry to everyone signed in. Admin › Access › *Who loses access* › *Work out who loses access* counts, section by section, the people who saw one before and whom no grant opens it to now, lists them (*List them*, top of the organization first) and offers *Grant it to a group*, which opens the form with that section ticked. With the Workbench on, it also names the people with Workbench work (a RACI role, seat admin or a delegation) who lack Ownership and approvals: their task emails would open pages they are refused. It reads the grants, the directory and the AD group members the nightly directory load records; grants to Entra groups are not counted there, so a member of one may be listed and still keep the section. The manager digest goes only to managers granted Leadership.

**Grant to an Entra group (2.8.0).** With the app registration in `Insights:Entra` ([ENTRA-SIGN-IN.md](ENTRA-SIGN-IN.md)), *Grant a role* and *Grant a profile* have a box for an Entra group: two letters of its name offer the groups that match. The grant keeps the group's object ID (`entra:<ID>`) and its name as it was. Membership, nested groups included, is checked through Microsoft Graph when the person signs in and kept for ten minutes, whichever way they sign in. AD groups (`DOMAIN\Group`) keep working: with Windows sign-in from the Windows token, with Entra sign-in from the members the nightly directory load records.

## Access profiles

An access profile is a named bundle: sections (whole organization or not, with or without named people, 3.1.0), roles, and for Admin its pages. Grant a profile to a group, and whoever is in the group has the bundle; change the profile, and everyone who holds it has the change from their next page. Grants role by role keep working beside them.

**Start from what people hold today.** Admin › Access profiles › *Offered from today's grants* lists each set of roles that people and groups hold role by role, most held first, then the starters (Executive, Agent program lead, License manager, Security reviewer, Champion, Dashboard operator, and with the Workbench on, Line-of-business owner and its three Governance profiles). *Make it a profile* creates it; tick *and grant it to the N who hold this today* to give it to them at once. Their role-by-role grants are kept: remove them in Admin › Access once the profile is in place.

**Add or change one.** *Add a profile*: a name, what it is for, the sections it opens (the same boxes as *Grant access*, with *Whole organization* and *Named people*), the other roles (and *whole organization* beside a persona role a profile gives from before 3.1.0), Admin pages for Admin, an owner, an approver, how long access lasts, and a request link. *Change* in the profiles list fills the form with a profile's values. A profile that gives Admin is made, changed, granted and removed by a full administrator only. A profile is deleted once nobody holds it.

**Grant one.** *Grant a profile*: a person, an AD group or an Entra group, and the profile. Grant to groups rather than people: access is then group membership, which IT already manages and reviews. *Who holds each profile* lists every grant with a × box that removes it.

**Requests and approvals through Entra ID.** Each profile can carry a request link: create an Entra ID access package for the profile's group (Identity Governance › Access packages, with Entra ID P2, part of Microsoft 365 E5), set its approver and how long access lasts, and paste its My Access link. Anyone refused a page that profile would open is shown *Request <profile>*. For administrators, grant the Dashboard operator profile, or full Admin, to an Entra group managed by PIM for Groups: they activate it for a few hours, with a reason, and Entra ID records each activation.

**What is recorded.** Every change to a profile, and every grant and removal, goes to the audit log in the same transaction, with the profiles and their grants before and after. Power BI's *Organization* role counts Organization given through a profile, as it counts it given role by role.

**Keep leaders off revoke and train lists (2.4.6).** Admin › Access › *Never on revoke or train lists*: levels 1 to N of the organization (5 by default; *None* lists anyone), each choice with how many people it protects. People at those levels are never on Upgrade, train or revoke (train and revoke), the Swap list (take side), Unused licenses, Better on pay-as-you-go, Fading use, Not started, Enablement's one-off and not-started people, or License optimization's counts, nor in their CSVs, removal files, the Licensing digest's counts, the Board pack, Budget and renewal, or the data API. Each of those lists says so at the top. Lists of accounts nobody can use keep everyone: Licenses on disabled accounts, Departures ahead and Copilot on non-human accounts. From the job server: `M -Task SetDashboardSetting -Name ProtectedLevels -Value 5` (0 for none).

**Limit the dashboard to the top levels (2.4.4).** Admin › Access › *Who can open the dashboard*: *Everyone in the directory*, or *Levels 1 to N* below the top of the organization, each choice with how many people it lets in. Anyone granted a section or a role other than Admin is let in whatever their level, so grant sections to an analyst who needs them. Everyone else is refused every page, export and API call. Levels open no section (3.1.0): within the levels, a person sees only the sections granted to them, and nothing until one is. Administrators outside the levels keep the Admin pages, so a wrong choice can be undone here. Managers outside the levels stop getting the manager digest. It applies from the next page, on every web server (2.4.12; the digests within a minute). The same, from the job server:

```powershell
M -Task SetDashboardSetting -Name MaxViewerLevel -Value 7   # 0 opens it to everyone again
```

**The top of the organization** decides who sees everything by level. Admin › Access › *Settings*, or leave it empty to use the one person with no manager who has at least half the directory under them:

```powershell
M -Task SetOrgTop -Upn 'ceo@contoso.com'
M -Task SetOrgTop -Clear
```

A new top takes effect at once: the org chart is rebuilt as it is saved.

**The line under the name.** The header shows *The ledger of AI use, cost and control, kept in your own SQL Server* under *Copilot Insights*. Change it with `Insights:Tagline` in `appsettings.Production.json`, or set it to an empty string to leave it out; restart the site to apply.

**The first administrator, or a way back in.** The site's configuration names bootstrap administrators, who cannot be removed from the Admin pages: run the IIS installer with `-AdminPrincipal`, or edit `Insights:BootstrapAdmins` in `appsettings.Production.json` and recycle the app pool (with several web servers, on every one: each has its own file). From the job server, `M -Task GrantRole -Principal 'USERS\Copilot-Insights-Admins' -PrincipalType Group -Role Admin`.

**The other access settings** (Admin › Access › *Settings*): the minimum group size (no breakdown shows a group smaller than this), the default period, the days after which a license counts as unused, and the license's monthly price and currency.

```powershell
M -Task SetDashboardSetting -Name MinGroupSize -Value 10
M -Task SetDashboardSetting -Name LicenseMonthlyCost -Value 30
```

**An account the directory cannot match.** Sign-ins are matched to people by the account's SID, which the nightly directory load copies from AD. An account with no match (a local or Entra-joined account) is "not in the directory" and sees nothing unless granted Organization. To map one, add it to `Insights:IdentityMap` in `appsettings.Production.json` (`"CONTOSO\\svc-report": "report.owner@contoso.com"`) and recycle the app pool, on every web server. A mapping cannot land an account on a person whose directory record carries a different SID, and nothing in a URL changes who is signed in.

**People with no SID on record (2.4.12).** Active Directory's answer, or a sign-in name that is a UPN, makes an account a person only when the directory records that person's SID: an account in another domain or forest can carry anyone's UPN. Admin › Directory quality › *No SID recorded* lists who this leaves out, managers outside the directory table included (a manager whose employee type the directory load skips is one). For each: load them with their SID (in `EmployeeTypes`, for a manager), or map their account in `Insights:IdentityMap`. Grant a role or bootstrap administration to an account outside the directory as `DOMAIN\name` or through a group, not by UPN.

**Power BI.** The ready-made model in `powerbi\CopilotInsights` has an *Organization* role that follows the same org chart through `bi.OrgSecurity` ([DEPLOYMENT.md](DEPLOYMENT.md), step 15): add the report's viewers to it in the Power BI service. Organization granted to an AD group reaches Power BI after the next nightly directory load, which expands the group into its members (nested groups included) in `audit.GroupMember`; the load's log names any granted group it cannot find in the target domain. The role reaches every table a viewer can read: the sites and agents of their own people only, and the license pool, value names and department figures only for those who see the whole organization (2.4.12; before, it filtered the people alone). In privacy mode `bi.OrgSecurity` is empty (it would pair real sign-ins with the pseudonyms), so share `bi.DepartmentWeek` through the model's *Departments* role rather than the person-level model.

## Names for codes and ids

The audit log records many things as codes. Give them names once and the dashboard's pages and Power BI (`bi.ValueNames`) show the names.

**Any cataloged code** (tenant, app, agent and platform ids, license types, models, Fabric capacities, resource types and statuses, labels): **Admin › Value names**. Pick a column, then name its values one at a time (the list starts with the most seen; *Unnamed first* puts the work at the top). Or work in a spreadsheet: **Download the mapping file** (every value of every column, with its name and note, in one CSV; or one column's from that column's view), fill in or change the *Name* column, and **Upload** it. An empty name removes one. A column with 10,000 or more distinct values is marked *too many to name*.

**License product names (2.7.1).** Every page shows a license by its product name (Microsoft 365 E5), not its part number (SPE_E5). The install brings Microsoft's names for the common licenses. **Admin › Value names › License product names** lists the tenant's licenses with the name each shows under. To name one that has none, or rename one, type its part number and the product name under *Name a license* and **Save**; an empty name removes it, and the license is shown by its part number again. A name set here is kept on every install. License pool shows the part number beside the name. The install also names the app codes Copilot Chat records (BizChat, Office, OfficeCopilotSearchAnswer, Edge) where they have no name yet.

```powershell
M -Task ListValues                                  # the columns, and how many values each has
M -Task ListValues -ColumnId 16 -Path models.csv   # one column's values and names, to fill in
M -Task ImportValueNames -Path models.csv           # read the names back
M -Task RebuildValueCatalog                         # every value again, from every flattened record
```

**Non-human accounts (2.4.5).** A Copilot license on an account that is not a person is a policy breach. Admin › Reference data › *Non-human accounts* sets which accounts count: guests; sign-ins and names that read like a service, shared, test or room account; accounts with no manager outside the org chart; more sign-in patterns of your own (`bot-*`, `*-svc`); employee types that are not people (`SERVICE`, `RESOURCE`); and AD groups of service accounts (`DOMAIN\Group`, whose members the next nightly directory load reads). Each of the three lists holds at most 400 characters (2.4.12): a longer one is refused with that reason, and nothing on the panel is saved. The breaches are on Security, Compliance and Licensing › *Copilot on non-human accounts*, with an Entra bulk remove file and a PowerShell script to remove every one, recorded for License action outcomes. The first time an account is found, the Security and Licensing alert recipients get an alert (and the webhook and the event log, ID 2002); turn it off on the same panel. **An account that is a person after all:** on the breach page, an administrator enters why and clicks **Confirm a person**; it moves to *Confirmed as people* with who confirmed it and when, raises no alert, and **Flag it again** undoes it. Each change is in the audit log.

**Sensitivity labels.** Admin › Reference data › *Sensitivity labels*: a name and an order (0 least sensitive) for each label id. The order drives "most sensitive label" and the label threshold for security alerts. `Get-Label` in Security & Compliance PowerShell lists your labels and their ids. Label names set in Value names show here too, and the other way round.

```powershell
M -Task NameLabel -LabelId 5e1f0c2a-0000-4000-8000-000000000002 -FriendlyName 'Highly Confidential' -Rank 4
```

**Agents.** Admin › Reference data › *Agents*: a friendly name and an owner for each agent. An agent already appears under the friendly name the audit log gives it (2.4.11), the latest when its maker renames it, so a name here is only needed where the log gives none (the agent then appears under a name read from its id, such as a Copilot Studio agent's schema name) or where you want another. *Unnamed first* lists new ones; the new-agent alert and Agents › Agent inventory point here.

```powershell
M -Task NameAgent -AgentId 'Copilot.Studio.Default-<tenant id>-cr123_hrHelper' -FriendlyName 'HR helper' -Owner 'hr-digital@contoso.com'
```

**Which apps count as Copilot Chat.** Admin › Reference data › *App categories*, or `M -Task SetAppCategory -App 'Copilot Notebooks' -Category 'Copilot Chat' -Apply` (`-Apply` rebuilds sessions and `bi` with it now instead of at the next load). An app not set counts as In-App Copilot.

## Email, alerts and digests

**Set up sending.** Admin › Email: Exchange Online (the mailbox's password, or OAuth with an app holding `SMTP.SendAsApp`), or any SMTP server on any port with its user name and password. **Send a test** after any change. Passwords and secrets are stored encrypted and never shown again; leave the field empty to keep the stored one, or tick *Remove the stored password and secret*. A stored password is sent only to the server, port, security and user name it was entered with (2.4.12): after changing any of them, type it again. Each save is in the audit log with the settings' old and new values. [DASHBOARD.md](DASHBOARD.md#email-alerts-and-digests) explains each mode, and when Exchange Online stops taking passwords.

**Who hears about what.** Admin › Alerts and digests › *Who hears about what*: pipeline failures and stale data, security signals, licenses, drops and spikes in use, and new agents, each to its own addresses (a distribution list keeps it short). Alerts also go to the Windows Application log under the source *Copilot Insights*. From 3.4.0, with the whole AI bill loaded, an AI budget set in Azure that Azure forecasts to go over is sent to the licensing addresses (`AlertLicensingTo`) once a week while it lasts, with each budget's forecast and spend so far and a link to AI spend across Azure; Its tick under *When to alert* (`AiBudgetAlert`) turns it off.

**When to alert.** Admin › Alerts and digests › *When to alert*: how old the data may get, the daily thresholds for jailbreaks, prompt injections, blocked resources and sensitive labels, the spike factor, what counts as a drop or a spike in use, and when the license pool is running out. **Run the checks now** runs a round at once in the tenant chosen in the switcher, and with several tenants **Run the checks for every tenant** runs one in each tenant where you are an administrator (2.4.12), each recorded in that tenant's audit log; *Where the checks stand* shows what each last saw.

**Report subscriptions (3.2.0).** People subscribe to reports themselves: **Email me this report**, at the top of a report, opens *My subscriptions* (menu, under *Start*) with the report and its filters ready; they choose the period, how often (daily on weekdays for periods up to 30 days; weekly or monthly for any), the day and the time, and can change, pause, stop, or **Send me one now**. The email holds the report's headline figures and its charts as images, never its tables or lists, and a link to the report; it goes only to the person's own address from the directory (`audit.UserDirectory.Mail`, else the UPN), through the email set on Admin › Email (logged there as *Subscription*). 54 of the 77 report pages can be subscribed to: the lists that name people, the Admin pages and *Group overview* cannot. A report that shows nothing until something is chosen on it (*Compare two teams*, *Agent detail*, *App detail*) can be subscribed to only once it is chosen; a subscription to one without its choice (possible in 3.2.0) pauses at its next send, saying what to choose. **Each email is built when it is sent, as the person would see the report then**: their grants by name and by account, their AD groups as the nightly directory load records them (`audit.GroupMember`), their Entra groups through Microsoft Graph where the app registration is set, their sections, the report turned off or in beta, privacy mode. A report they can no longer open is not sent, and the subscription pauses saying why (they resume it once they can open it again). Someone whose access comes only through an AD group the directory load does not record is refused when subscribing, with that reason. **Settings**, Admin › Alerts and digests › *Report subscriptions*: on or off (`ReportSubscriptions`; off sends nothing and keeps every subscription), the most per person (`SubscriptionsPerPerson`, 20), the earliest time people may choose (`SubscriptionEarliest`, 07:30, after the morning pre-cache; times are every half hour to 22:00 in the dashboard's time zone), and how long an email due before the night's refresh has ended waits for it (`SubscriptionWaitUntil`, 12:00; then it goes saying how old its figures are). Set the dashboard's address on Admin › Email so the emails link to their report. **Watch and stop them:** a full administrator sees everyone's subscriptions on *My subscriptions* (the last week's sent, not sent, paused and missed), and can pause or stop any. With several web servers each email is claimed once (`dash.ReportSubscriptionSend`); one more than 12 hours late (the site was down) is not sent, and the next goes at its time. From 3.4.0 the daily brief's two lists of sentences (*Since yesterday*, *This week, act on*) go in its email too: they name no one, and their links are left out. A send that fails is not tried again: its reason is on the person's page and in Admin › Email's log. The digests below are another thing: set by an administrator for a list of recipients.

**Digests.** Admin › Alerts and digests › *Digests*: add one (report, recipients, daily, weekly or monthly, the hour, the period, and optionally one manager's organization); **Preview** it, **Send now**, **Pause** or **Remove** it. Digests carry aggregates only. One for a manager's organization carries that part alone (2.4.12): the Adoption digest lists the targets inside it, and the Licensing digest leaves out the tenant's license pool, which only the whole organization's digest has. Preview and Send now need the role of the section the digest shows (Adoption, Licensing or Security), and a preview is in the audit log (2.4.12). A digest at an hour the clock skips or repeats goes once (2.4.12).

**Manager digests.** Admin › Alerts and digests › *Manager digests*: *Weekly* or *Monthly* sends every manager with at least the given number of people under them the Leadership digest for their own organization, to their email address in AD, on the day and hour set here (not sent in privacy mode; with the dashboard limited to levels, only to managers within them). A send that fails is tried again an hour later, three times in all; one cut off by the site stopping goes when it starts again. **A manager who asks not to get it:** *Managers who do not get the digest*, enter their UPN and **Stop their digest**; **Send it again** undoes it.

**Success stories in the weekly report (3.1.6).** Anyone the dashboard lets in can share a story (menu: *Success stories*); grant the curators the **Stories** role (Admin › Access › *Grant access* › *Other roles*). On the same page a curator approves or declines each story (declining asks why, and the person sees it), changes its words if needed (each change is in the audit log with the text before it; the status stays), and ticks **Tick for the next report** on the approved ones. The weekly report quotes the ticked stories when it sends, up to `StoriesPerReport` (3; 0 leaves them out), oldest approved first, in *In their words* and the workbook's *Stories* sheet, then marks each sent and clears the tick. A story is quoted by name only where the person ticked that they agree and privacy mode is off; otherwise as "a colleague in" their department, or "a colleague" where the department is smaller than the minimum group size. A story only moves forward (entered, approved, sent; declined or withdrawn end it): to reconsider a declined story, it is entered again. Ticking a sent story sends it again, after a question. **The reminder:** set `StoriesReminderTo` (addresses separated by `;`) with `M -Task SetDashboardSetting` or in `dash.Setting`, and the day before the report, from its send time, the curators get one email a week when stories wait for review or none is ticked. **Remove a story:** a curator deletes it (hidden, never sent); a full administrator restores it, or **Delete permanently** under *Deleted stories*, which also removes its earlier text from the audit log. A report already sent cannot be recalled.

**Teams, ServiceNow or any webhook.** Admin › Alerts and digests › *Teams, ServiceNow or any webhook*: an https address (stored encrypted) and a format. *Teams* posts an adaptive card of plain text (use a Teams Workflows "post to a channel when a webhook request is received" address, or an incoming webhook); *Json* posts `{source, kind, subject, lines, link, atUtc}` for ServiceNow, Splunk or anything else. **Post a test alert** checks it. Every alert then goes to the webhook as well as email.

**The app certificates.** The pipeline signs in with up to three: one for the app that reads the audit log from Microsoft Purview, one for the app that fetches the agent workbook from SharePoint Online, where the settings name a site, and one for the Foundry usage load, where it has a certificate of its own (2.6.2). It records each at the start of every run. Each is watched on its own: an alert goes to the pipeline recipients at *Warn about the app certificate* days before it expires (30 by default), again at 14 and 3 days, and when it has expired; and one when the settings name a certificate the job server does not hold. Admin › Health › *Certificate details* sets them side by side: subject, issuer, thumbprint, valid from, expiry, the app registration and tenant, the store it is in, the job server and when it was last checked.

**Nothing arrives.** Admin › Email › *Recently sent* gives the reason for each message not sent: email off, no recipients, or the server's own answer (a refused password, a certificate that did not validate, a closed connection). **The log to troubleshoot with (2.6.2)** is on the same page: choose **Send a test email**, then read *Troubleshooting: where the logs are*, which shows that test's whole conversation with the mail server: the server, port and security used and who signed in, then each command the dashboard sent (`C:`) and each answer (`S:`). The line that failed is the last one. *Recently sent* keeps the conversation of every test and of every message that was not sent, under **show** in its *Conversation* column; the same text is in `dash.NotificationLog.Detail`. The password and the message itself are never in it. The panel lists what the common answers mean (535, no AUTH offered, 530 STARTTLS first, an untrusted certificate, 550 5.7.60). A sent test that never arrives was accepted by the relay: the trace continues in the relay's own log, from the line `250 ... Queued`. The site's own failures are in the web server's Windows Application log, sources *Copilot Insights*, *.NET Runtime* and *IIS AspNetCore Module V2*. An alert whose email failed, with no webhook to take it, is emailed again an hour later, three tries in all (2.4.12); *Where the checks stand* shows one waiting.

**Restored to another SQL Server instance** (a disaster-recovery restore, an availability-group failover): the stored password, client secret and webhook address cannot be decrypted there. Run `.\Install-CopilotAudit.ps1 -SqlServer <the new instance> -Dashboard` once: it makes the keys again and removes what it cannot read (2.4.12). Then enter them again in Admin › Email and Admin › Alerts and digests. If the database master key there also protects keys of your own, the installer leaves it and says so: open it with its password, or restore the old instance's service master key, and run it again.

## Targets, value and training

**Adoption targets.** Admin › Targets and assumptions › *Adoption targets*: for the whole organization or one manager's, the measure (adoption, share of people active, or people active), the target, the start and the due date. Progress is on Adoption › Targets, the Board pack and in the Adoption digest, each showing the targets for the part of the organization it covers (2.4.12): a target for one manager's part is seen by people who may see that part, and the whole organization's only by those who see all of it. A target for a part smaller than the minimum group size shows no figures, except to that manager.

**Time saved.** Admin › Targets and assumptions › *Time saved*: the default minutes one interaction saves, what an hour is worth, and the minutes per app. Leadership › Value and time saved and the Value digest follow them.

**Training attendance.** Admin › Training records: upload a CSV of UPN, session and date (`yyyy-MM-dd`). **Download the mapping file** for every record held, in the same columns, to add to and upload back, or an empty template. Lines already there are skipped and bad lines listed. Remove a session with its *Remove* button. Enablement › Training effect shows each session four weeks after it.

**Champions.** How many to suggest per department: Admin › Targets and assumptions › *Licensing and champions*. The list is Enablement › Champions, for the Enablement role.

**Contracts.** The Copilot agreements (seats, price per seat, start and renewal dates) are entered by an administrator on **Forecasting › Budget and renewal** itself (*Add a contract*, and *Remove* on each). The page then shows the cost and how many seats the use supports at renewal.

## Costs and savings

**Leadership › Costs and savings (2.6.6)** puts everything the company spends on AI and automation on one page, a month at a time: what each area costs, what of it can be saved, what it returns, the savings ranked by what they are worth, and what is holding the return back. It is for people who see the whole organization, and for the Licensing role. Each area appears once its source is loaded; one that is not is listed under *What this is built from*, never shown as zero.

| Area | Where its figures come from | What the page finds |
|---|---|---|
| Microsoft 365 Copilot | The audit log and the licenses from Graph, as every other page | Unused licenses to revoke, licenses on disabled accounts, light users better on pay-as-you-go, add-ons held beside E7, add-ons bought and assigned to nobody; people to train and heavy free users to license |
| Copilot Studio and AI Builder | `audit.PowerPlatformCreditDay`, and the capacity packs in Graph's license pool | Pay-as-you-go credits that would cost less as packs; packs left unused |
| Fabric and Copilot in Power BI | `audit.FabricCapacityDay` and `audit.FabricWorkloadDay` | Idle capacities, capacities to size down, to reserve or to pause at weekends; throttling; Copilot's share of the capacity, its cost and its value |
| Power BI licenses | Graph's license pool, and `audit.PowerPlatformUserActivity` | Pro and Premium Per User licenses unused in 30 days; Pro held beside E5 or E7, which include it; licenses bought and assigned to nobody |
| Power Automate | Graph's license pool, `audit.PowerAutomateFlow` and the activity | Premium licenses unused (and owning no premium flow that ran); Process licenses on flows that run under 100 times a month; flows failing a third of their runs; the value of the flows' runs |
| Power Apps and Dataverse | Graph's license pool, the activity, and `audit.PowerPlatformCapacityDay` | Premium licenses unused; Dataverse database, file and log capacity over the entitlement |
| GitHub Copilot | `tools\Import-GitHubCopilotUsage.ps1` (2.6.2) | Seats unused in 30 days, or never |
| Foundry and Azure OpenAI | `tools\Import-FoundryUsage.ps1` (2.6.2) | An account that cost money and answered no request: a provisioned (PTU) deployment left running |

**Load a source.** Each new table is filled from its source's own export with `tools\Import-CostCsv.ps1`, on the job server; none needs an API permission. Shape the export so its headings are the table's columns (the key columns are required; others may be left out), then load it:

```powershell
.\tools\Import-CostCsv.ps1 -Table FabricCapacityDay -Path .\fabric-capacity.csv
.\tools\Import-CostCsv.ps1 -Table PowerAutomateFlow -Path .\flows.csv -Replace
```

| Table | Export | Key headings | The other headings |
|---|---|---|---|
| `FabricCapacityDay` | The Fabric Capacity Metrics app (its capacity utilization by day) with the capacity's cost from Azure Cost Management | `CapacityId`, `Day` | `CapacityName`, `Sku`, `CapacityUnits`, `Region`, `Billing` (PayAsYouGo or Reserved), `ActiveHours`, `CuSecondsUsed`, `PeakPercent`, `ThrottledMinutes`, `Cost`, `Currency` |
| `FabricWorkloadDay` | The Capacity Metrics app's items by workload; Copilot's operations as a workload whose name starts with *Copilot* | `CapacityId`, `Day`, `Workload` | `CuSeconds`, `Operations`, `Users` |
| `PowerAutomateFlow` | A flow inventory: the CoE Starter Kit's, or the admin center's export | `EnvironmentName`, `FlowId` | `FlowName`, `OwnerUpn`, `IsPremium`, `LicenseModel` (PerUser, Process or Standard), `State`, `CreatedUtc`, `LastRunUtc`, `Runs30`, `FailedRuns30`, `MinutesSavedPerRun` |
| `PowerPlatformUserActivity` | The admin center's usage reports (Power Apps, Power Automate, Copilot Studio) and Power BI's activity. From 2.6.9 the activity database fills Power BI and Power Apps each night from the audit log (below); a CSV is then needed for Power Automate and Copilot Studio only | `UserPrincipalName`, `Product` (Power BI, Power Apps, Power Automate or Copilot Studio) | `LastActiveUtc`, `ActiveDays30` |
| `PowerPlatformCapacityDay` | The admin center's capacity report | `Day`, `CapacityType` (Database, File or Log) | `EntitledGb`, `UsedGb` |
| `PowerPlatformCreditDay` | The admin center's Copilot Credits and AI Builder consumption | `Day`, `EnvironmentName`, `Product` (Copilot Studio or AI Builder), `Billing` (Prepaid or PayAsYouGo) | `Credits`, `Cost`, `Currency` |

- **A load adds and replaces by the key**: a day or a flow loaded again replaces itself, nothing else changes, so a week or six months can be loaded and loaded again. `-Replace` makes the file the whole table, for the inventories that are a picture of now (the flows, who used what); the delete guard holds a file more than 15% smaller than the table (`CostImportMaxDropPercent`, `-AllowDrop` once).
- **Values** are read as the column's type whatever the machine's language: dates as yyyy-MM-dd, with or without a time; numbers with a point for decimals; True, False, Yes, No, 1 or 0. A blank is NULL. A row whose key cannot be read is left out and counted, and a key given twice keeps its last row. The tool names any heading the table has no column for.
- **Load them on a schedule** that matches the export: the capacity and credit days daily or weekly, the inventories when they are refreshed. The page reads each source's last 30 days, whatever day it ends.

**Prices and assumptions.** Admin › Targets and assumptions › *Costs and savings*: the price of each license and unit (`dash.CostPrice`, seeded with list prices in USD: put in what your agreement pays), the Fabric reservation discount (41% by default), the minutes a flow run and a Copilot in Power BI request save (3 each), and the days after which a seat, a license or an AI account counts as idle (30). A license is priced by its SKU part number, as Graph's license pool names it; one with no price is left out of the spend. The hourly cost the value is counted at is *Time saved*'s, and the Copilot license's price is Admin › Access's.

**What a cut of seats really saves (2.6.9).** Each per-user license's row in the price list also takes:

- **A volume price**: from that many seats every seat costs the volume price (Power Apps Premium is seeded at 12 from 2,000). The spend is counted at it, and a cut that takes the count under it counts every seat going back to the list price; where that would save less than stopping at the volume price, the saving cuts only down to it and says so.
- **The Dataverse capacity a seat brings** (Power Apps Premium and Power Automate Premium: 0.25 GB of database and 2 GB of file; Power Apps per app: 0.05 and 0.4). A cut gives that capacity up: where `PowerPlatformCapacityDay` shows less spare than the cut gives up, the add-on capacity the tenant would then buy (at the Dataverse price a GB) is taken off the saving. Without the capacity loaded, the saving is Low confidence and says the capacity was not checked.
- **Spare kept (%)**: unassigned seats kept on purpose for joiners and moves are not a saving. *Spare seats kept unassigned* (`SpareSeatPercent`, 10% when installed) applies to the Copilot add-on and every license without its own share.
- **Bought for capacity or the volume price**: a license bought for the Dataverse capacity or the volume price it brings (Power Apps Premium bought to 2,000 seats, for example). Its unassigned and idle seats are never counted as money back.

**Suites: E3, E5 and E7 (2.6.6).** A suite is not one product to keep or revoke: Microsoft 365 E5 includes Power BI Pro, E7 includes Microsoft 365 Copilot, and both also buy security, compliance, Teams and more that cannot be split from their price. So:

- **No share of a suite's price is counted as AI spend.** Copilot and Power BI Pro inside a suite cost nothing more, and are listed in the *Suites* card at their list value, not added to the spend. Only what is bought on top (the Copilot add-on, a standalone Power BI Pro, the Power Platform licenses) is spend, at the seats bought: a seat bought and assigned to nobody is paid all the same, and is a saving.
- **A license held twice is a High-confidence saving**: a standalone Power BI Pro on someone whose E5 or E7 includes it, the Copilot add-on on someone on E7, or E5 left beside E7.
- **Copilot inside E7 is never on a list to revoke**: it cannot be taken off alone. Its holders are licensed for Copilot on every page (the license lookup's `-CopilotSuiteSku` names the suites, `Microsoft_365_E7*` and the like by default: check License pool for your E7's part number and pass it if it differs), and moving from the add-on to E7 keeps them licensed through the change.
- **Licensing › Suite fit** lists each holder with the suite that would serve them, from their Copilot and Power BI use in the last 30 days, and what to check first. E7 with no Copilot use: E5 (Medium, as Agent 365 and the Entra Suite are not in the data). E7 with light use: train first. E7 with regular use: E5 and the add-on where cheaper (Low). E5 with no Power BI use: E3; with it, E3 and Power BI Pro where cheaper (both Low, and only with security, compliance and telephony's agreement, since Defender, Purview, Entra ID P2 and Teams Phone are not in the data). A disabled account's suite can be taken back (Medium). Protected levels are never offered a move. These moves are a review, outside the page's AI savings, and the *Suites* card on Costs and savings sums them.
- **Admin › Targets and assumptions › Suites** sets each suite's price (list prices in USD for July 2026 are seeded: put in your agreement's), what it includes (`COPILOT`, `POWER_BI_PRO`) and whether it is counted (`dash.CostSuite`). E5 holders are offered a move only once Power BI activity is loaded (`PowerPlatformUserActivity`, product Power BI), as nobody's use of its Power BI Pro is known before.

**From the headline to the detail (2.6.6).** Every figure on Costs and savings leads to what it is made of:

- **The tiles**: the spend to every area, the savings to the ranked list, the value to Leadership › Value and time saved, what was already saved to Forecasting › License action outcomes.
- **Each area** (its name in the table, its part of the ring, its dot beside a saving) opens its own view: its figures, every saving, risk and opportunity in it, its six months, how it is counted, a table of what it is made of (each Fabric capacity, each license bought and assigned, each flow worth most or failing, each Foundry account, GitHub's seats by plan, Dataverse capacity, the counts behind Copilot's savings), and the reports that name the people.
- **A confidence** (High, Medium, Low) lists its savings alone; **a saving's title** opens the page with the named detail, or its area's view; **a suite move** opens Suite fit on those people (`?move=`, `?suite=`, `?copy=addon` or `suite`).
- Report pages' headline figures lead to their detail too: Leadership's overview to Adoption, License inventory, Retention and Apps and agents; What to act on to each list; the License inventory, Adoption, Agents, Security, Compliance, Enablement and Forecast start pages to the lists behind their counts.

**Credit use: Copilot Credits and tokens by person (2.6.6).** Licensing › Credit use estimates what each person's Copilot use costs in Copilot Credits a month, and the tokens behind it, for a credit policy: how the credits spread across people, who would reach a cap (try 250 to 5,000 a person a month), the agents and departments that bill most, and the unlicensed people whose credits cost more than a license. Microsoft reports neither credits nor tokens per person, so both are worked out from the audit log:

- **What bills**: an agent's answer used by someone without a Microsoft 365 Copilot license at the time, or in a Copilot Studio channel (Teams, a website), where a license does not cover it. Every agent counts: Copilot Studio's, Copilot Studio lite's (the agent builder in Microsoft 365 Copilot), SharePoint's. A licensed person's agents inside Microsoft 365 Copilot, and Copilot Chat without an agent, bill nothing.
- **Credits** are Admin › Targets and assumptions › *Credit estimate*'s: 2 for an answer, 10 more where it read work data (Microsoft Graph grounding), 5 for an action (counted where an agent calls another), Microsoft's list rates; a credit's price is the *Copilot Credit, pay-as-you-go* price under *Costs and savings* (0.01). A classic, scripted answer costs less, so the estimate errs high.
- **Tokens** are a rough figure for sizing, never a measurement: every interaction at the tokens a turn takes (1,500), each file, mail or page read adds (1,000), and an answer gives back (400), all set in the same form.
- What the organization was actually billed is the Power Platform admin center's consumption report (`PowerPlatformCreditDay`, above). Where it is loaded, the page shows it beside the estimate, as a share: set the rates so the two come near each other. Only people not licensed at the time are billed for agents outside a Copilot Studio channel; an unknown license status is not taken for unlicensed.

**How the figures are counted.**

- **Spend** is a month: licenses held now at their price, and for everything billed by use, the last 30 days of what the source charged.
- **Savings** are what would stop being spent if the action beside each were taken: High confidence where the data shows it outright (a seat nobody used in 30 days), Medium where it follows from a change you make (a smaller capacity), Low where it rests on a price or a discount. A capacity on a reservation saves nothing until renewal, so its idle cost is shown as at stake, not saved.
- **Value** is time saved at the hourly cost: Copilot's interactions (as Leadership › Value counts them), the flows' successful runs, and Copilot in Power BI's requests. Areas whose value is not measured show a dash, so the return understates rather than flatters.
- **Already saved** is the Copilot licenses exported from this dashboard to revoke, or from disabled accounts, that have since gone.
- **Saved to date**, under it, adds up the saving ledger (`dash.SavingLedger`): each nightly refresh writes the month in progress as the licenses gone by then at the Copilot license price (`dash.usp_RefreshSavingLedger`), and a month gone by keeps what it was last written with, so a new price changes no past month. A license taken back counts from the month it is seen gone, each month it stays gone. The page also writes the month's savings found, by confidence, each time it works them out for the whole organization. Power BI and other tools can read the table; the start page and the weekly report (`WeeklyReportSavings`) show both figures, linked here.

## How figures are calculated

**Leadership › How figures are calculated (2.6.9)** lists, in one place, every price and assumption a cost, saving, value or estimate on the dashboard rests on: its value now, the figures that use it, who last changed it and when, or *As installed* where nobody has. The inputs stay where they are edited; the page is where they are found. Open to every viewer.

- **Change it.** An administrator who may open the page an input is set on sees *Change it* beside it, which opens that page at the field (`?focus=`), marked for a moment. Others see the value only.
- **How it is worked out.** Every money figure on the pages (Costs and savings' tiles, Chargeback, License optimization, Budget and renewal, Rollout waves, License action outcomes, Fading use, Better with a license, Better on pay-as-you-go, Swap list, Credit use, Agents costing credits, Suite fit, Value and time saved, Copilot reservations) has *How it is worked out* under it: the formula with the values in it, then each assumption it uses with its value and, for an administrator, *Change it*.
- **Prices that disagree** are said first, on this page and at the top of Costs and savings: the Copilot price in Admin › Access against the reservation rate in force (Leadership › Copilot reservations) and against the seat-weighted price of the contracts on Forecasting › Budget and renewal. Settle them before quoting a figure: each page uses the price where it is set.
- **Still as installed** counts the settings and prices nobody has set for this organization: Microsoft's list prices and published estimates until someone does.

| Where an input is set | What |
|---|---|
| Admin › Access | The Copilot license price and currency, the pay-as-you-go cost of an interaction, unused after (days), disabled accounts: reclaim after (days since last use, 90), the minimum group size |
| Admin › Targets and assumptions | Minutes saved (default and by app), an hour's worth, the price list with volume prices, Dataverse capacity a seat brings, spare seats and bought for capacity, the suites, the Fabric discount, flow and Copilot in Power BI minutes, idle after, spare seats kept unassigned, the credit and token estimate |
| Leadership › Copilot reservations | The reservation rate, the purchase gate |
| Leadership › Credit chargeback | The credit routing register, the charge rate a credit (2.7.2) |
| Forecasting › Credit commitment | The credit commitment: amount, term, what one credit draws (2.7.2) |
| Leadership › Model tokens by business unit | The business unit routes of the gateway's values (3.5.0) |
| Forecasting › Spend limits | Entered limits, each limit's business unit and warning, the alert's contacts, headroom and forecast (3.5.0) |
| Governance › Agent policies | The agent rules, and each proposal's decision (3.5.0) |
| Forecasting › Budget and renewal | The contracts' seats and price |

## Copilot reservations

**Leadership › Copilot reservations** (2.6.8) sets each line of business's reserved Microsoft 365 Copilot seats against the add-on licenses its people hold, and shows what that bills. Leaders see the organizations led from inside their part of the organization; the whole-organization view and the Licensing role see every organization, the reconciliation with the license pool and the purchase gate. The people behind each figure are on **Licensing › Reservation holders**, for the Licensing role. Administrators keep everything below on the page itself.

**The register.** One row per persistent chargeback organization: its **Org ID** (kept for good, so a reorganization does not move the billing), name, leader's sign-in, cost center, reserved seats, and the dates it is effective from and to. *Add an organization's reservation* adds one; *Add several at once* takes lines pasted from a spreadsheet, all checked before any is added:

```text
OrgId,Organization,Leader,Reserved,From,CostCenter,To
LOB-01,Retail Banking,leader.one@contoso.com,7403,2027-01-01,CC-100
LOB-EXEC,Executive Leadership,,17,2027-01-01,CC-001
```

A header line is skipped; a field may be quoted to hold a comma (`"Retail Banking, East"`); the leader may be empty for a pool with no one under it; cost center and the end date are optional. Every line is checked before any is added, and they are added together or not at all. To change a reservation (a true-up or an April true-down), add a new row from the date the change applies: the organization's open row is ended the day before (2.7.0), so past months and month-end snapshots keep what they were. *End* ends a row on a day; the cross removes a row entered by mistake. A row that would overlap one starting on or after it is refused. Every change is in the audit log, with what was removed.

**Who counts where.** An organization's people are everyone under its leader in the org chart, the leader included. A person under two listed leaders (one reporting to the other) counts with the nearer one, so nobody is in two organizations. Allocated licenses are the **Microsoft 365 Copilot add-on** held today; Copilot held only inside a suite (E7) and Technology-funded accounts are not counted. Add-on holders under no listed leader are shown as an exception, to map.

**The figures.** Reserved; Entra allocated; unused reserved capacity (MAX(reserved − allocated, 0) per organization); allocation over reservation (MAX(allocated − reserved, 0)); billable licenses (reserved plus allocation over reservation); utilization (allocated over reserved, N/A with nothing reserved); the monthly obligation (billable times the rate) and the remaining obligation (the months left in the year after this one). Each is worked out per organization, then summed. The billable rule is the policy's and the page says it awaits Finance's confirmation.

**The rate.** *Rate and purchase gate* › *Add a rate*: Finance's rate a license a month after any discount (18 for 30 less 40%), from the date it applies. Without one, obligations show *Pending Finance Decision*.

**The purchase gate.** The unassigned add-on licenses counted at the baseline (1,136 in the policy's example) and the share to assign first (95%): the page shows how many are assigned since, and whether a purchase may be considered (at 95% of 1,136, 1,079 assigned, leaving a buffer of 57). Empty: no gate.

**The Technology-funded register.** Approved executive, service, test, shared, migration and exception accounts: sign-in, category, accountable owner, approval reference, from and review dates. Their licenses are counted once in the Technology pool, never in a line of business. Accounts the dashboard reads as non-human that are not in the register are listed as an exception; one past its review date too.

**Snapshots.** Each night after the licenses are read, the pipeline keeps the last month-end's position once (`dash.usp_TakeReservationSnapshot`), and never rewrites it; from 2.7.0 only once the licenses reach two days past the month end (a license change can reach Purview a day late), only when a reservation is in effect that day, and not after a load the delete guard refused: the reserved seats, the allocated licenses on that day and the rate in force, for every organization, those under no leader and the Technology pool. The org chart used is the one on the night it was kept. *Month-end snapshots* lists them; *View* shows one as it was. Every snapshot is **Operational, not certified**: certification, adjustments and the December close come in a later release.

**With the Workbench on** (3.0.0, `ConnectionStrings:Governance` set), the Workbench's lines of business are the seat record:

- **Leadership › Copilot reservations** shows each line of business's reserved seats (`core.SeatReservation`), the seats its tracking group holds, the seats over and unused, and the monthly chargeback the FinOps lead certified (`core.SeatChargeSnapshot`). The Licensing role and anyone whose role covers the whole organization see every line of business; others see the lines their Workbench roles cover. A figure opens the Workbench page with the same number, for those whose Workbench roles let them open it.
- **Licensing › Reservation holders** lists the people holding a seat in one line of business's tracking group, or the seats no line of business claims.
- **The register is kept, not edited.** `dash.Reservation`, `dash.ReservationRate`, `dash.ReservationSnapshot` and `dash.TechnologyAllocation` keep their rows; their forms are gone from the page and a change sent anyway is refused. When the site starts it sets `dash.Setting` *SeatModel* to `Governance`, and the nightly snapshot then adds nothing. With the Workbench off again, the setting goes back to `Leader` and the page is as before.
- **Start from Insights' reservations**, once: **Workbench settings › Seat groups** lists the register's rows in effect today. Choose the funding owner for each row (one with the same code or name is suggested), or leave it out, and confirm. Each becomes that funding owner's first reservation through the Workbench's own command, audited, and needs the FinOps role. Rows given the same funding owner are added together. A funding owner that already has a reservation is skipped, and the page says so: change it with a seat reservation request. The offer goes once every funding owner has a reservation.

## Copilot Credit chargeback and commitment

**Leadership › Credit chargeback** (2.7.2) charges each line of business for the Copilot Credits its Power Platform environments use. It follows a two-path operating model:

| Path | What it is for | Charged to |
|---|---|---|
| LoB Productivity | Team productivity, knowledge and managed productivity agents | The line of business named |
| LoB Business Application | Operational workflows and business applications | The line of business named |
| Enterprise Shared | HR, enterprise help, compliance and critical employee agents | Nobody: funded centrally and shown apart |
| Innovation or development | Pilots, experiments, development and testing | Its sponsor, if the route names one; otherwise funded centrally |

**Set it up.**

1. Install 2.7.2 or later: `.\Install-CopilotAudit.ps1 -SqlServer <server> -Dashboard`.
2. **Load the credits.** In the Power Platform admin center, export the Copilot Credit consumption report: per environment and day, prepaid and pay-as-you-go. Shape its headings as **Costs and savings** lists them for `PowerPlatformCreditDay` (`Day`, `EnvironmentName`, `Product`, `Billing`, `Credits`, `Cost`, `Currency`). Then run `.\tools\Import-CostCsv.ps1 -Table PowerPlatformCreditDay -Path .\credits.csv`. Load each month again once its last day is in; a load replaces the days it holds.
3. **Make sure every line of business is in Copilot reservations.** The chargeback names a line of business by its Org ID there. A line of business that reserves no seats is added with 0 reserved. **With the Governance Workbench on**, the lines of business are its funding owners instead: the Org ID is the funding owner's code (Ownership registry › Funding owners), its name comes from there, a viewer of part of the organization sees the lines their Workbench roles cover, and an agent is misrouted when its environment's line is not the one whose tracking group holds its owner's Copilot seat.
4. **Route every environment.** In *Credit routing register*, enter the environment exactly as the consumption report names it, its path, the Org ID charged, the cost center, the financial owner, the billing policy and the date the route applies from. *Route several at once* takes lines pasted from a spreadsheet, all checked before any is added:

```text
Environment,Path,OrgId,CostCenter,From,FinancialOwner,BillingPolicy,To
Retail-Prod,Productivity,LOB-01,CC-100,2026-11-01,finance.retail@contoso.com,BP-Retail
HR-Agents,EnterpriseShared,,CC-900,2026-11-01,hr.finance@contoso.com,BP-Enterprise
```

5. **Set the charge rate.** Under *Charge rate*, enter Finance's rate for one credit, after any discount. If it is empty, the price list's pay-as-you-go credit (0.01) is used.

**How the register behaves.**

- A path is entered as its value (`Productivity`, `BusinessApplication`, `EnterpriseShared`, `Innovation`) or its label.
- The two LoB paths must name an Org ID. Enterprise Shared must name none.
- A change is a new row from its date: the environment's open route is ended the day before, so past months keep theirs.
- *End* ends a route on a day; the cross removes one entered by mistake.
- A route that overlaps one starting on or after it, or falls inside one running past its end, is refused.
- Every change is in the audit log.

**The figures.**

- Each environment's Copilot Studio credits are charged at one rate, however Microsoft was paid: prepaid packs, a commitment, or pay-as-you-go. AI Builder credits are not Copilot Credits and are left out.
- A route that changed during the month charges each day to the route in effect that day.
- *Billed pay-as-you-go* is what Microsoft billed for the metered credits, shown beside what is charged back.
- Leaders see the lines of business led from inside their part of the organization. The whole-organization view and the Licensing role also see Enterprise Shared, what is not routed, and the exceptions.

**Exceptions.**

- Environments with credits and no route.
- Routes naming an Org ID that isn't in Copilot reservations.
- Agents built by one line of business in another's environment. The agent inventory says where each agent lives, and the reservation register's leaders say where its owner works.

**Snapshots.** Each night the pipeline keeps every complete month not yet kept (`dash.usp_TakeCreditChargeSnapshot`). A month is complete once the consumption report reaches its last day, and it is kept only when a route is in effect at its end. So loading two months at once skips neither. A snapshot keeps each environment's route, credits and the charge rate in force; *View* shows one as it was, and *Not routed* shows the credits it kept without a route. To correct a month after routing its environments, an administrator selects *Take it again*: the month is kept afresh from today's register and rate, and the audit log records it. Every snapshot is **Operational, not certified**.

**Forecasting › Credit commitment** (2.7.2) tracks a prepaid commitment, such as the Copilot Credit Pre-Purchase Plan (P3).

- **Record it.** Under *Record a commitment*, enter its name, amount and currency, what one credit draws from it (0.01 at list price), its term and its Azure scope. Terms may not overlap, or each would claim the same credits: record a top-up by removing the commitment and adding it with the new amount. Until its term starts the page shows only its start date.
- **What the page shows:**
  - what is drawn and what remains;
  - the burn rate over the last 30 days;
  - the date it runs out at that rate, or what it leaves unused at the end;
  - the plan to date, which spreads the commitment evenly over its term;
  - the environments and lines of business drawing most.
- **It is an estimate.** It uses the pay-as-you-go credits in the consumption report × what a credit draws. Prepaid packs draw nothing. Microsoft's applied figure is in Azure Cost Management.
- **Alert.** The page warns, and an alert goes to `AlertLicensingTo` once a week while it lasts, when the commitment runs out (or is projected to) more than *Warn when this many days off* (`CreditCommitmentAlertDays`, 30) before its term ends; or, once a month of its term has passed, when it is projected to leave more than that many days' worth unused at the even plan. That includes a commitment nothing draws on. 0 turns it off.
- **Costs and savings.** While a commitment is in force, Costs and savings no longer suggests buying capacity packs for the metered credits: they draw on what is already paid for.

## Model tokens, spend limits and agent policies

Three pages of 3.5.0, each fed by an optional loader (setup: docs/GATEWAY-LIMITS-AGENTS.md).

**Leadership › Model tokens by business unit** counts the model tokens each business unit used through the AI
gateway. Counts only, never money. A business unit is an L2 and L3 executive pair (`TokenUnitLevels`, `2,3`). A value
the gateway stamps that is a person's sign-in is in that person's unit; any other value is routed by an administrator
on the page: *Values in no business unit* lists each one with a Route form (the L2 executive's sign-in, and the L3's
where the value is one L3's), and *Business unit routes* keeps the dated register (a new route ends the value's open
one the day before; End and remove are on each row). Every change is in the audit log.

**Forecasting › Spend limits** puts every spending limit beside its spend: Copilot spending policies, the gateway's
token quotas and rates, GitHub budgets, and limits entered on the page. The dashboard never changes a limit. Each
night the limits read are brought in (`dash.usp_SyncSpendLimits`): an administrator's settings on a limit are kept,
and a limit gone from its source is marked so, never deleted. On the page an administrator:
- enters a limit no loader reads (*Enter a limit*), such as an environment's Copilot Credit limit;
- sets each limit's business unit (L2 and L3 executives) and the share to warn at, or hides it;
- sets *The alert*: the contacts (`SpendLimitAlertTo`), whether each limit's L2 and L3 executives get it too
  (`SpendLimitAlertToExecutives`, off until the roll-out), how many days early to warn (`SpendLimitAlertDays`, 5),
  and the headroom (`SpendLimitHeadroomPercent`, 10) and forecast (`SpendLimitSuggestFrom`, P80) of next quarter's
  suggestion.
Each period's end is kept once in `dash.SpendLimitSnapshot`. *Next quarter's limits* is the list to take to the
admin center, the gateway and GitHub at the quarter's review.

**Governance › Agent policies** checks four rules over the Copilot Studio agents each night (also with *Check the
rules now*): no owner with an enabled account, unused, costly for few people, shared and not certified. Each match is
a proposal an administrator approves, rejects or withdraws; a rejected one is not proposed again for 30 days. A rule
can approve its own Notify or Quarantine; a new owner is always a person's decision. Notify is emailed by the
dashboard to the agent's owner (to `AlertAdminTo` when it has none). Quarantine, release and a new owner are carried
out by `tools/Invoke-AgentActions.ps1` only when `AgentActionsEnabled` is 1 and its app is set up; until then the page
says Off. Nothing deletes an agent.

## AI apps, Power BI and Power Apps

**What it is (2.6.9).** A third database, the **activity database**, loaded each night by the same backfill as the Copilot and license databases, with Purview **record types** rather than operations:

| Record type | What it is | Where it shows |
|---|---|---|
| `ConnectedAIAppInteraction` | Calls to Azure AI Foundry and Azure OpenAI from the organization's own apps, where Purview collects them (Foundry's *Powered by Microsoft Purview*, or Defender for Cloud's *Data security for AI interactions*), and Entra-registered apps through the Purview SDK. One record per message: a prompt or a response | Governance › AI apps beyond Copilot |
| `PowerBIAudit` | Power BI activity: reports viewed, exported, refreshed, shared | Who used Power BI lately (`audit.PowerPlatformUserActivity`): Costs and savings, Suite fit |
| `PowerAppsApp` | Power Apps launched and managed | Who used Power Apps lately: Costs and savings |
| `TeamCopilotInteraction` (2.7.0) | Teams meeting AI notes: Facilitator's notes and live notes in meetings | Apps, beside Power BI and Power Apps |
| `ComplianceDLPSharePoint`, `ComplianceDLPExchange`, `DLPEndpoint` (2.7.0, not by default) | Data loss prevention rule matches, and those undone: overridden, no longer matching or reported as a false positive. A device's file activity with no rule matched is left out | Compliance › Sensitive content |

It needs nothing new from Microsoft: the app registration's `AuditLogsQuery.Read.All` reads every record type. Record-type searches are submitted to Graph's beta endpoint, whose list of record types has the newer ones; the records are read as every other load's. The Copilot and license databases are not touched, and Copilot interactions are not loaded again.

**Set it up.** On the job server, in order:

1. Add the database to the settings, so the nightly run loads it: `.\tools\Install-CopilotJobServer.ps1 -ActivityDatabase CopilotActivity` (or add `ActivityDatabase = 'CopilotActivity'` to `copilot-audit.settings.psd1`). It must be on the **same SQL Server instance** as the Copilot database: the rollup reads it by its three-part name.
2. Create and install it: `.\Install-CopilotAudit.ps1 -SqlServer <server> -Dashboard -ActivityDatabase CopilotActivity`. It is created like the license database (SIMPLE recovery, the service account as db_owner) and gets the audit tables, the load procedure, `audit.AIAppCall`, `audit.PlatformActivity` and `audit.usp_ShredActivity`. The Copilot database gets `audit.AIAppUserDay`, `audit.AIAppResource` and `audit.usp_RollupActivity`.
   From 2.7.3 a database the installer creates is set to MAXDOP = the server's logical processors less two; for one created by hand, pass `-MaxDop <n>` (or run `ALTER DATABASE SCOPED CONFIGURATION SET MAXDOP = <n>;` in it).
3. Load its history, once: `.\Invoke-CopilotPipeline.ps1 -ActivityHistory`. From 2.7.3 its `-Finalize` builds only `audit.CopilotAudit`, not the Copilot table `audit.CopilotInteraction` (nothing in this database reads it), then shreds the records into the activity tables. It loads the last `HistoryDays` days (180) of the record types in `ActivityRecordTypes` (four by default), finalizes them, shreds them and rolls them up. It resumes where it stopped. At the client's volume (about 245,000 AI app records a day, 44 million over 180 days) it is about 75 searches and several hours: run it on a day with no other backfill, as the tenant's 200 searches a day are shared.
4. From then on the nightly run loads the new records after the Copilot load, then rolls them up. Both steps are optional: a failure is recorded on its step and fails nothing else. Until step 3 is done the nightly's activity step fails each night with "Run a backfill to completion in this database first".

**Space.** Plan by measuring: after the first day of history, `EXEC sp_spaceused 'audit.CopilotAudit'` in the activity database, times 180. The records are kept compressed; the shredded tables are columnstore and much smaller; the Copilot database gets one row per day, resource, app and caller.

**Other record types.** `ActivityRecordTypes` in the settings replaces the list of four (`ConnectedAIAppInteraction`, `PowerBIAudit`, `PowerAppsApp`, `TeamCopilotInteraction`). The DLP types are not in it by default, as their volume is the tenant's every match: add them to load data loss prevention, for example `ActivityRecordTypes = @('ConnectedAIAppInteraction', 'PowerBIAudit', 'PowerAppsApp', 'TeamCopilotInteraction', 'ComplianceDLPSharePoint', 'ComplianceDLPExchange')`. A type added later loads from then on; its history needs a database of its own. A DLP match is kept by its policy, rule, severity, workload and person; no file or message is named.

**Administrators' actions on agents (2.7.0).** Not in the activity database: the license-event load asks for the seven operations of `CopilotAgentManagement` (BlockedAgent, UnblockedAgent, DeployedAgent, RemovedAgent, DeletedAgent, UpdatedAgent, UpdatedTenantSettings) in the same searches as Copilot Studio's authoring records, from the night 2.7.0 is installed, and Agents › Build and publish shows them.

**The rollup by hand.** `.\tools\Update-ActivityRollup.ps1` (three days back, or from three days before the last rollup that succeeded where that is earlier, so nights the optional steps failed are made up), `-Full` (every day) or `-SinceUtc <date>`. After correcting the shred (a field Purview names differently), read every record again in the activity database with `EXEC audit.usp_ShredActivity;`, then `-Full`.

**The resource map.** Governance › AI apps beyond Copilot › *Resource map*, for administrators: each resource's line of business, environment and owner. A new resource is guessed from its name (`retail-prod-aif-0147`: Retail, Production; `aza-dev-aif-0046`: Development); what an administrator saves is kept.

**Privacy.** No prompt or response text exists in these records, and client IP addresses are not copied. The dashboard counts people, never names them, from the minimum group size up. Calls *without a user identity* (`Guest`) are an app calling with its own key or managed identity: Purview cannot say who asked.

**Retention.** Purview keeps these record types 180 days. A custom audit retention policy of one year for `ConnectedAIAppInteraction`, `CopilotInteraction` and the others you load keeps a reload possible; it applies from the day it is made.

## Classifications, events and extracts

**Turn a report off, or back on.** Admin › Reports shown lists every report by section, laid out as the start page is, each with a box. Untick the ones your organization should not have and choose **Save**; a section's own box ticks or clears all of it. A report that is off is gone from the navigation and the start page and is not found at its address (404), for everyone, administrators included; its exports and forms are refused too. Tick it and save to bring it back: nothing is deleted. Roles and levels still decide who sees a report that is on. Admin pages cannot be turned off. Links to a report that is off stop working, so turn a detail page (Agent detail, App detail, Person record) off together with the lists that open it. Scheduled digests and extracts, the board pack and the data API are set on their own pages and are not changed here. Each change is in the audit log. The choice is kept in `dash.Setting`, one row a report, named `Hide:` and its path.

**Put a report in beta (3.1.6).** Admin › Reports shown: tick **Beta** beside a report (it stays ticked to show) and choose **Save**. A beta report is shown only to full administrators and to the people given a preview of it, with a *Beta* badge in the menu and on the page; for everyone else it is gone, as a report turned off is. Then give the preview (3.2.0): Admin › Access › *Grant access*, the person or group, *Other roles*, tick **Preview** and grant. Preview opens every beta report, now and later, but only inside the sections the person sees already, as those sections show it (their own team or the whole organization, named lists, the minimum group size, privacy mode). A Preview grant made in 3.1.6 names its reports and opens those only; ticking Preview for that person again opens every one. The x beside *Preview* in *Grants* removes it. **Take a report out of beta:** untick **Beta** and save: everyone with its section sees it. The morning pre-cache leaves beta reports out, so their first visit each day is not from memory. Kept in `dash.Setting`, one row a beta report, named `Beta:` and its path; a dashboard from before 3.1.6 ignores it and shows the report to everyone.

**Job families.** Admin › Classifications and events › *Job families*: a job-title pattern (`%engineer%` is any title containing engineer), the family, and a priority (lower first). *The most common titles no rule covers* shows what is left. People › Job families and Power BI's `JobFamilies` table follow the rules at once.

**Use cases.** Admin › Classifications and events › *Use cases*: rules from the app (a pattern), whether the interaction searched the web, and whether an agent answered, to a use case. The defaults cover the Microsoft 365 apps; **Put back the default rules** restores them. Leadership › Use cases and Power BI's `UseCases` table follow them.

**Events on the trends.** Admin › Classifications and events › *Events on the trend charts*: a day, a name and a kind (rollout, training, outage, policy, other). Each shows on every trend chart as a dashed line with its name, and in Power BI's `EventMarkers`.

**Scheduled extracts.** Admin › Scheduled extracts: a name, the data (department by week, daily totals, apps by week, the license pool, agents by week: aggregates only), a folder or share, how often and when, and how many days back. The site's app-pool account (for a share, the web server's computer account) needs write access to the folder. With more than one web server, use a share: each round runs on whichever server holds the lock, so an extract to a local folder fails, and says why, once another web server has run the checks (2.4.12); a local folder's *Result* names the server. *Where the checks stand* lists the web servers that run them. **Write now** writes one at once; *Last written* and *Result* show how the last one went.

## Acting on the license recommendations

1. **Forecasting › License optimization** shows the recommendations and what they are worth; **Forecasting › Upgrade, train or revoke** and **Licensing › Licenses on disabled accounts** name the people (Licensing role). *Train* and *revoke* look at Premium use: Copilot in Word, Excel, PowerPoint, Outlook or Teams, or Copilot Chat over work content. Someone who only uses web Copilot Chat, which the free tier gives, is not using their license. **Licensing › Use ranking** ranks everyone by general, free and Premium use. **Licensing › Swap list** pairs the revoke and upgrade lists within a department (or division, or anywhere), so an idle license goes to someone who wants one instead of a new seat being bought; its two files take the licenses back and give them on, in that order.
2. On those pages, choose the **Entra bulk add or remove members file** button, or the **PowerShell script** one. Each covers the part of the organization the page shows, and is recorded for License action outcomes; it is a button, not a link (2.4.12), so nothing but the person on the page can make one. If an export cannot be recorded it is not made: try again in a moment. If Copilot licenses are assigned through a group, set its object id first in Admin › Targets and assumptions › *Licensing and champions*, so both use the group; otherwise the script assigns or removes the SKU directly.
3. A license administrator applies it: the bulk file in Entra (Groups › the license group › Bulk operations), or the script in PowerShell with their own rights (`-WhatIf` first). The dashboard changes nothing in Entra.
4. The next nightly run picks the change up; the lists update the day after.
5. **Forecasting › License action outcomes** shows every export, whether it has been applied, the licenses reclaimed and whether new licenses are being used; *Not applied yet* lists who is still waiting.

**Rolling out in waves.** Forecasting › Rollout waves orders everyone without a license (the heaviest free Copilot Chat users first, then those in departments where licensed people use it most) and splits them into waves; each wave has its own bulk file and script. The default wave size is in Admin › Targets and assumptions.

## Triage and investigations

**Flagged interactions.** Security › Flagged interactions (Security role) lists every jailbreak, prompt-injection and blocked-resource flag with a triage status: *New*, *Investigating*, *Closed* or *False positive*, and a note. Saving one records you as its owner. *Open* (new or investigating) is the default view; the count is at the top.

**Boxes that take a person (2.6.2).** Wherever a page asks for a person (Compliance › Person record, the two managers of Leadership › Compare two teams, a grant or a check in Admin › Access, an agent's owner, a manager's digest), type three letters of their first or their last name: nothing is searched before the third letter, and the names offered are those whose first or last name begins with what was typed (*mor* finds Avery Morgan and Morgan Petrov, not Seymour Hill). Up to 20 are offered; type more to narrow them. Choosing one fills in the sign-in; a sign-in typed in full is taken as it is. Who can be found is who the page can show: people in your part of the organization on a report, managers with a team large enough to show on Compare, anyone in the directory on the Admin pages. Each search is in the audit log, and in privacy mode no report offers names.

**One person's record.** Compliance › Person record (Compliance role; also in privacy mode): a person's directory and license record, and every interaction in the period with its record id, downloadable. Use it for a data-subject request or an investigation; the prompt and response text is in Purview, found by the record ids. Every look and download is in the audit log with whose record it was.

## Privacy, retention and the data API

**Privacy mode.** Admin › Privacy and retention › *Privacy mode*: *Pseudonymize* refuses every page, export, digest and API call that names people, and Power BI's `bi.Users` shows a salted hash instead of names (from its next refresh). *Off* restores both. Units are named after their managers, so in privacy mode they go by the manager's department, and nobody opens a manager's part of the organization by name; a digest set up for one is not sent (its status says why). If the setting cannot be read, the dashboard treats privacy mode as on until it can (2.4.12). It applies from the next page, export or API call on every web server (2.4.12; before, the web servers that had not taken the save went on for up to a minute); digests and extracts read it at most a minute old.

**The minimum group size** (Admin › Access) is also the smallest part of the organization a report can be narrowed to (2.4.12): a manager and everyone under them, fewer than that, are not shown on their own, on a page, in the data API or in a digest.

**Retention.** Admin › Privacy and retention › *Retention*: days to keep the dashboard's audit log (0 keeps it; at least 365 days, 2.4.12) and the notification log, and when to clear the stored JSON of old records (0 keeps it; at least 200 days). A change is in the audit log with its old and new values. Applied each night after a successful run; **Apply retention now** does it at once. To clear JSON from the job server instead, `M -Task PurgeJson -RetainDays 365` reports what would go and `-Apply` clears it. `-DeleteRows` removes those records altogether: the record, its shredded row (`audit.CopilotInteraction`), its flattened row, and the resources and contexts it read (2.4.12; before, all but the record were kept). `bi` and the dashboard keep them until `M -Task RebuildBi`.

**The data API.** Admin › Privacy and retention › *Data API* turns `/api/v1` on or off. Callers need the same access as the pages; [DASHBOARD.md](DASHBOARD.md#the-data-api) lists the endpoints.

**Who did what.** Admin › Audit log: every page view, refusal, export, API call and admin change, with who, when, the scope and the filters, searchable and exportable.

## The pipeline

**The nightly task** (`Copilot audit - nightly`, 02:00 by default) runs `Invoke-CopilotPipeline.ps1`: the directory, the licenses, the license events and the Copilot interactions, then retention. It records each run and step in `audit.PipelineRun`. To run it now, or to resume after a failure, first check Admin › Health that no run is in progress, then run `.\Invoke-CopilotPipeline.ps1` on the job server. To move it, run `tools\Install-CopilotJobServer.ps1 -At 03:30`.

**A directory load now** (3.5.1). Admin › Directory quality › *Load the directory now* asks the job server to load the users from AD and rebuild the org chart without waiting for the night: after a change in AD, or in the domain and groups on that page. The task *Copilot audit - directory on request* (every 15 minutes, as the nightly task's account) runs `Invoke-CopilotPipeline.ps1 -DirectoryOnRequest`, which does nothing and writes no log unless a load was asked for; then it runs the nightly directory step and the dashboard refresh, and the page says when it is done and how many people the directory holds. It waits while the nightly run or an upgrade holds the database. `tools\Install-CopilotJobServer.ps1` registers the task; to add it alone on a job server already set up, as an administrator: `Register-ScheduledTask -TaskName 'Copilot audit - directory on request' -Action (New-ScheduledTaskAction -Execute (Get-Command pwsh).Source -WorkingDirectory <scripts folder> -Argument '-NoProfile -NonInteractive -File "<scripts folder>\Invoke-CopilotPipeline.ps1" -DirectoryOnRequest') -Trigger (New-ScheduledTaskTrigger -Once -At (Get-Date).Date -RepetitionInterval (New-TimeSpan -Minutes 15)) -Settings (New-ScheduledTaskSettingsSet -StartWhenAvailable -MultipleInstances IgnoreNew -ExecutionTimeLimit (New-TimeSpan -Hours 4)) -Principal (New-ScheduledTaskPrincipal -UserId '<the nightly task''s gMSA>' -LogonType Password -RunLevel Limited)`. Where the nightly task runs as an ordinary service account, not a gMSA, Task Scheduler keeps its password: give `-User $c.UserName -Password $c.GetNetworkCredential().Password` in place of `-Principal`, after `$c = Get-Credential <the account>` (type the password in the prompt).

**GitHub Copilot and Foundry usage (optional, 2.6.2).** Two loaders bring in AI use outside Microsoft 365 Copilot, each into tables of its own: `tools\Import-GitHubCopilotUsage.ps1` (seats, daily use, each person's day and cost, for a GitHub enterprise or organization) and `tools\Import-FoundryUsage.ps1` (deployments, daily requests and tokens, and daily cost of Foundry and Azure OpenAI, for the subscriptions named). The nightly run calls each as its last steps when `copilot-audit.settings.psd1` names its source: `GitHubEnterprise` (or `GitHubOrganization`), and `FoundrySubscriptions`. **Where the settings do not, the run skips it and says so in its log** (*GitHub Copilot usage: skipped, not configured ...*); that is not a failure. **A loader that fails does not fail the night either**: its step row has the exit code, the log says it is optional and goes on, and `audit.ExternalUsageRun` has the reason. Admin › Health › *Other AI usage: GitHub Copilot and Foundry* shows each one's last run, the newest day held and, for GitHub, when the token expires. No report reads these tables yet. Setting each up, with the part to hand to the GitHub or Azure administrator: [GITHUB-COPILOT-USAGE.md](GITHUB-COPILOT-USAGE.md) and [FOUNDRY-USAGE.md](FOUNDRY-USAGE.md).

**The delete guard (2.6.5).** Five loads empty a table and fill it again each night. Each counts what it read against what the table holds before it deletes anything, and refuses a load that is more than its percentage smaller: the table keeps the last good load. 10,000 rows last night and 8,000 tonight is 20% fewer, refused at 15. A source that answered with part of its rows (an AD query cut short, a workbook saved half-filled, an API that returned one page) can no longer empty a report.

| Load | What it replaces | Setting in `copilot-audit.settings.psd1` | When it refuses |
|---|---|---|---|
| Agent workbook (`tools\Import-AgentWorkbook.ps1`) | Each sheet's table (`dbo.AgentInventory`, `dbo.AgentsInProd`, `dbo.ParentChildAgent`), and the agent inventory read from them | `AgentWorkbookMaxDropPercent` | The whole workbook is refused, every sheet left as it was. Admin › Health's agent workbook panel shows the run as failed, with why. |
| User directory from AD (`tools\Export-UsersWithHierarchy-ToSql.ps1`) | `audit.UserDirectory` | `UserDirectoryMaxDropPercent` | The directory keeps last night's people, and the rest of the night runs on them. |
| Licenses from Graph (`tools\Update-CopilotAuditEnrichment.ps1`) | `audit.UserLicenseSku`, every license each person looked up holds | `LicenseMaxDropPercent` | Nothing about the people is written: their licenses stay as the last good run left them. The license pool (the seats bought and assigned) is still written. |
| GitHub Copilot seats (`tools\Import-GitHubCopilotUsage.ps1`) | `audit.GitHubCopilotSeat`, for the enterprise or organization | `GitHubSeatMaxDropPercent` | The seats are kept; usage and cost load as usual, and the run ends *Partial*, with why. |
| Foundry deployments (`tools\Import-FoundryUsage.ps1`) | `audit.FoundryDeployment`, per subscription | `FoundryDeploymentMaxDropPercent` | The subscription's deployments are kept; its usage and cost load as usual, and the run ends *Partial*, with why. |

- **The percentage** is 15 by default, for each. Set any of them in the settings file, as a number from 0 to 100; 0 turns that guard off. The nightly run passes each loader its own. A load run by hand takes `-MaxDropPercent` for the one run.
- **A drop that is real** (a reorganization, an agent clean-up, seats taken back): run that loader once by hand with `-AllowDrop`, from the scripts folder on the job server, as the nightly task's account. For example `.\tools\Import-AgentWorkbook.ps1 -AllowDrop` (it reads the rest from the settings file). The next night compares with what that run loaded. For a lasting change, raise the setting instead.
- **What it does not hold back:** a load that grows or stays level, a first load into an empty table, and a sheet missing from the workbook (its table is left alone, as before). The other loads only add or update rows, or rebuild from what is already in the database, so they need no guard. The dashboard's uploads (Admin › Value names, Training records) only add.
- **A refusal does not stop the night.** A refused workbook, directory or license load ends with exit code 3; the nightly run records it on the step, says in its log that the table kept its last good load, and goes on with the license events, the interactions and the rest. The run ends *Succeeded*, and its detail on Admin › Health › *Pipeline runs* names each refused step with the guard's reason. Any other failure still stops the run, as before.
- **The refusal names** the table, both counts, the drop, the limit and the setting, in the nightly run's log, in the run's detail and, for the workbook, GitHub and Foundry, in their own panels on Admin › Health.

**Audit search settings (3.0.2).** Two settings in `copilot-audit.settings.psd1` reach every Purview audit search the nightly run makes: `AuditSearchTimeoutMinutes` (240 by default, 30 to 1440), how long a search may stay queued or running before it is thrown away and submitted again, and `AuditSearchesInFlight` (8 by default, 1 to 40), how many searches run at once. Raise the first when the log says *audit search still 'running' after 240 minutes*; lower the second when Microsoft answers 429 to a submission while fewer than 8 run. A value out of range stops the run at its start, naming the setting. [RUNBOOK.md](../RUNBOOK.md), sections 6 and 7.

**One run at a time** (2.4.12). The pipeline, `Install-CopilotAudit.ps1`, a load (`Invoke-CopilotAuditBackfill.ps1`) or CSV import (`tools\Import-CopilotAuditCsv.ps1`) run by hand, the dashboard installer's `-GrantDatabaseAccess` and the maintenance rebuilds (`Reflatten`, `RebuildSessions`, `RebuildBi`, `RebuildValueCatalog`, and `SetAppCategory` or `PurgeJson` with `-Apply`) each hold the database while they work. One started while another holds it stops at once, says so and changes nothing: run it again when the other has finished. Before, two at once loaded the same windows twice, or rebuilt `bi` under each other and counted interactions twice. Before an upgrade, a rebuild or a `-History` load, disable the nightly task (`Disable-ScheduledTask -TaskName 'Copilot audit - nightly'`), and enable it again when the work is done (`Enable-ScheduledTask`), so that night's load is not refused.

**Refresh or rebuild by hand**, after a change that the nightly run would not pick up:

| After | Do this |
|---|---|
| A change to the directory, the top of the organization or the app categories, wanted on the dashboard now | Admin › Health › *Refresh the last 7 days*, or `M -Task RefreshDashboard` (every day) or `-SinceUtc 2026-10-01` |
| A session setting changes (timeout, time zone, license databases) | `M -Task RebuildSessions` |
| License history arrives for a period already built, or flattened rows are purged | `M -Task RebuildBi`, then a full refresh of the Power BI model (below) |
| An app's category changes (Admin › Reference data, or `M -Task SetAppCategory`) | After the next `bi` refresh, a full refresh of the Power BI model: the category sets Premium use on every past interaction of the app, and the model's incremental refresh reloads only the last 4 days |
| The nightly run catches up two or more missed nights, or makes up an owed refresh from more than 4 days back (below) | A full refresh of the Power BI model: the run rewrote days its scheduled refresh does not reload |
| A release changes the flattening | `M -Task Reflatten` (every record flattened again, then sessions, `bi` and the dashboard) |
| The flatten says a field *now carries information* | Nothing breaks; see `audit.RawLogRecordShape` and [RUNBOOK.md](../RUNBOOK.md) |

`M -Task Status` shows what is loaded and built, and when each step last ran.

**When something looks missing**, run `.\tools\Test-CopilotDataHealth.ps1 -SqlServer SQLPROD01`. It reads both databases without changing anything: gaps between the CSV files and loads, refreshes owed, flattening, sessions, `bi` and the rollups against the data end, the license operations loaded, Copilot Studio's records shredded, and agents without a maker or owner. It ends with what to run, in order. To rebuild everything from the stored records, without searching the audit log again: [RUNBOOK.md](../RUNBOOK.md), section 5g.

**A full refresh of the Power BI model.** The model's refresh policy reloads only the last 4 days: *Refresh now* and the scheduled refresh leave older days as they were. A full refresh loads every partition again: on Premium, Premium Per User or Fabric capacity, a refresh with `applyRefreshPolicy` false (the enhanced refresh REST API, or TMSL over the XMLA endpoint); on Pro, publishing the model again and refreshing it ([DEPLOYMENT.md](DEPLOYMENT.md), step 16, item 4). It is needed whenever SQL rewrites days older than 4: the rebuilds above, and the catch-up runs below.

**A night that failed after its load was merged**, at the session build say, owes a refresh of sessions, `bi` and the dashboard from that load's start. Once the cause is fixed the next run makes it up, even with nothing new to load (2.4.12; before, a failure that lasted past the 48-hour lookback left those days out for good); its log then says *The derived tables were not brought up to date after an earlier load or import: refreshing them from* a date. A CSV import whose refresh failed or was skipped is made up the same way, or by running the import again. When that date is more than 4 days back, or the run caught up two or more missed nights (a load refreshes from 48 hours before where the data ended), refresh the Power BI model in full afterwards. To have sessions and `bi` read several license databases each night, list them as `SessionLicenseDatabases` in `copilot-audit.settings.psd1` ([DEPLOYMENT.md](DEPLOYMENT.md), step 11).

## A slow nightly run

When the nightly task takes longer than it should (2.5.4), find the stage first, then the statement. Three things show it, each one level deeper, and none of them changes any data.

**1. Where the time went: `tools\Measure-CopilotNightly.ps1`.** Run it on the job server, or on any computer that reaches SQL Server; during a run or after it.

```powershell
.\tools\Measure-CopilotNightly.ps1 -SqlServer SQLPROD01 -LogPath D:\CopilotAudit\logs
```

It lists the last seven runs with each step's time, then takes one run apart: inside the license step and the Copilot step, the Purview download, the merge, the flatten, the shred, the license SKU changes, the sessions, the report tables (`bi`) and the dashboard totals, each with its time, its share of the run and what it wrote. The lines add up to the step: time that no table accounts for is a line of its own. It ends with the five longest stages and what to look at for the longest. While a run is under way it shows that run so far, and the statement SQL Server is running for it at that moment.

| Parameter | Use |
|---|---|
| `-RunId 412` | Another run than the last (the ids are in the first list). |
| `-Runs 30` | How many recent runs to list (7). |
| `-LogPath D:\CopilotAudit\logs` | Also reads the run's own lines from its `nightly-<date>-<time>.log`, with the time between them: it splits what the tables cannot, the flatten from the agent names and each shred. Run it on the job server, so the log's local times line up. |
| `-TopStatements 25` | How many statements to list from Query Store (15; 0 leaves them out). |
| `-Database`, `-LicenseDatabase`, `-TrustServerCertificate` | As for the other tools. |

It needs to read both databases; `VIEW SERVER STATE` to show the running statement, and `VIEW DATABASE STATE` for Query Store. Without them those two parts say so and the rest still prints.

**2. Which statement: Query Store.** SQL Server keeps each statement's time, CPU, reads, memory and tempdb use once it has finished, and the script's third part lists a run's longest from it. SQL Server 2022 has it on for a new database; where the script says it is off, turn it on once, in each database, and look again after the next run:

```sql
ALTER DATABASE CopilotAudit SET QUERY_STORE = ON (OPERATION_MODE = READ_WRITE, QUERY_CAPTURE_MODE = AUTO, MAX_STORAGE_SIZE_MB = 1000, INTERVAL_LENGTH_MINUTES = 60);
ALTER DATABASE CopilotLicenseEvents SET QUERY_STORE = ON (OPERATION_MODE = READ_WRITE, QUERY_CAPTURE_MODE = AUTO, MAX_STORAGE_SIZE_MB = 1000, INTERVAL_LENGTH_MINUTES = 60);
```

Each statement is listed with its query id. Its plan, to open in SSMS (or SSMS › the database › Query Store › *Top Resource Consuming Queries*):

```sql
SELECT TOP (1) TRY_CAST(p.query_plan AS XML) AS QueryPlan, p.last_execution_time
FROM sys.query_store_plan AS p WHERE p.query_id = 1234 ORDER BY p.last_execution_time DESC;
```

**3. The statement running now, and a recording of the night: `tools\Trace-CopilotNightly.sql`.** For the database administrator, in SSMS (set `@Do` at the top and run it) or with sqlcmd. It looks only at the task's own connections, which SQL Server knows by their application name: `CopilotAuditBackfill`, `CopilotAuditUserDirectory` and `CopilotAuditEnrichment`.

| `@Do` | What it does | Needs |
|---|---|---|
| `now` | Each statement the task is running at this moment: its procedure, time, CPU, reads, degree of parallelism, memory, what it waits on, who blocks it, and its plan with the rows each operator has produced so far. Then what its sessions have waited on since they connected. | `VIEW SERVER STATE` |
| `start` | Starts an Extended Events session, `CopilotNightly`, that records every statement the task runs for a second or longer (`@MinSeconds`), into at most four 50 MB files in SQL Server's log folder. | `ALTER ANY EVENT SESSION` |
| `read` | The recording so far: time by procedure, then the 50 longest statements with the line they are on. | `VIEW SERVER STATE` |
| `stop` | Stops recording and removes the session. The `CopilotNightly*.xel` files stay until deleted. | `ALTER ANY EVENT SESSION` |

```powershell
sqlcmd -S SQLPROD01 -E -v Do=start -i .\tools\Trace-CopilotNightly.sql      # before the run
sqlcmd -S SQLPROD01 -E -v Do=now -y 200 -i .\tools\Trace-CopilotNightly.sql   # during it
sqlcmd -S SQLPROD01 -E -v Do=read -y 200 -o .\nightly-statements.txt -i .\tools\Trace-CopilotNightly.sql
sqlcmd -S SQLPROD01 -E -v Do=stop -i .\tools\Trace-CopilotNightly.sql       # when done
```

The recording costs little: a statement under the threshold is dropped where it is raised, and no plan is collected. A statement that calls a procedure is listed with the procedure's time in it, so read the lines inside the procedure. The plan as it runs needs SQL Server 2019 or later, or trace flag 7412 on 2016 SP1 and 2017; without it that column is empty and the rest is the same.

**SQL Server Profiler**, where the administrator prefers it, records the same statements. Extended Events above does it with less load on the server, and Microsoft has deprecated Profiler for the database engine, so use it for a look, not for every night:

1. In SQL Server Profiler: *File › New Trace*, connect to the instance, and choose the *Blank* template.
2. On *Events Selection*, tick *Show all events* and *Show all columns*. Under *Stored Procedures* select **SP:StmtCompleted**; under *TSQL* select **SQL:BatchCompleted**. Keep the columns *TextData*, *ApplicationName*, *DatabaseName*, *ObjectName*, *LineNumber*, *Duration*, *CPU*, *Reads*, *Writes*, *RowCounts*, *SPID*, *StartTime* and *EndTime*.
3. *Column Filters*: **ApplicationName** *Like* `CopilotAudit%`, and **Duration** *Greater than or equal* `1000` (milliseconds, as the window shows them). Tick *Exclude rows that do not contain values* on both.
4. On *General*, tick *Save to file* with file rollover, so a night's rows are not kept in the window's memory; then *Run*.
5. Start the nightly task (or leave the trace until morning), then stop the trace and sort by *Duration*. *ObjectName* and *LineNumber* are the procedure and the line of the statement. In the saved file, *Duration* is in microseconds.

Do not add the *Showplan* events to a trace left running: they are costly on every statement. Take one statement's plan from `now` above, or from Query Store.

**Reading the result.**

| The longest stage, or what the statement shows | What it means | What to do |
|---|---|---|
| *Sessions* or *Report tables*, far longer than the night's load explains, in databases on a release before 2.5.4 (the script says so at the end) | Both worked out every license SKU change from the records each night, and the session build looked each session up in them without an index. The time grows with the license history, whatever the night's load. | Upgrade both databases to 2.5.4. |
| *Sessions … : a full build*, or *Report tables … : a full rebuild*, on an ordinary night | Everything was rebuilt, not the night's days: the first run after an install, a session setting that changed, or a full rebuild that was stopped and is being finished. | Nothing, if it happens once. Every night: the run log of that stage says why. |
| *Purview download* | The search runs in Microsoft's service, at its pace, and a tenant shares about 200 searches a day. SQL Server has no part in it. | [RUNBOOK.md](../RUNBOOK.md), sections 6 and 7. |
| *License SKU changes … : every event* on an ordinary night | More than a tenth of the license events were shredded in one night (a catch-up, a CSV import), so every event was worked out again. | Nothing: the next night works out its own events only. |
| *Shredding license events* far longer than usual, and *An earlier shred did not finish* in the run log | The shred before this one was stopped part way (a task ended by its time limit, a restart), and left events without their properties. This one shreds what that one was shredding too. | Nothing, if it happens once. Each night: the task is being stopped before it ends, so look at its time limit in Task Scheduler. |
| The run stays at *Another shred of the license events is running: waiting for it to finish* | One shred runs at a time in a license-event database, and another holds the lock: a CSV import, the installer, or a shred started in a query window and canceled there, which keeps the lock while the window is open. | Let the other finish, or close that window. `now`, in the trace script above, shows the session it waits on. |
| A statement *BLOCKED by session* n, or waiting on `LCK_M_…` | Another session holds what it needs: a report, a backup, an index rebuild at the same hour. | Move one of them to another hour. |
| A long statement waiting on `PAGEIOLATCH_…`, or on `WRITELOG` | It waits for the data disk, or the log disk. | The storage, or a larger `max server memory`: pages read once stay in memory. |
| A long statement waiting on `RESOURCE_SEMAPHORE` | It waits for memory to sort or hash in. | Fewer large queries at that hour, or more memory for SQL Server. |
| A long statement at *DOP 1* that reads many pages | It ran on one core. | Check the instance's `max degree of parallelism` and `cost threshold for parallelism`, and report the statement: some are serial by design. |
| A long statement that reads far more pages than the rows it returns | The plan read a whole table to find a few rows: statistics that are out of date, or sampled too thinly on a large table. | `UPDATE STATISTICS <table> WITH FULLSCAN` once, then compare the next night; report the statement and its plan. |

In the lab, one night's refresh of a database with 120 million interactions and a license history of 4,000,000 events:

| Stage | 2.5.3 | 2.5.4 |
|---|---|---|
| Sessions (60,852 from 329,445 records) | 22 min 36 s | 45 s |
| Report tables (220,480 interactions, 304,308 license changes) | 17 min 7 s | 2 min 24 s |
| Dashboard totals | 2 min 35 s | 2 min 9 s |
| The three together | 42 min 24 s | 5 min 24 s |
| License SKU changes, in the license step (44,405 events) | none: worked out at each read | 2 to 9 seconds |

From 3.1.4 the dashboard refresh (`dash.usp_RefreshDashboard`) also keeps the totals the pages read: each person's sums
per period, per week, per app, agent, use case, license type and model, the Copilot Credit counts, and who held a license
on each day. In the lab, with two years of 160 million interactions on 8 processors, a nightly dashboard refresh took
about 14 minutes (825 s), and the first one after the upgrade, which builds them whole, about 35 minutes. From 3.1.5 it
also keeps the totals of the resources Copilot read (`dash.usp_RefreshResourceTotals`): 934 s in all in the lab, 152 s
of it those totals, and about 4 minutes more the first time. The step names the refresh in the nightly log as before;
Admin › Health shows when it last ended.

To ask for help with a slow night, send the output of the first tool with `-LogPath`, and the `read` result of the third if a recording was made. Neither holds the data: they hold counts, times, the text of the SQL statements and the names of tables and procedures.

## More than one tenant

A group with several Microsoft 365 tenants loads each into its own pair of databases, with its own app registration, and one dashboard spans them.

1. On the job server, as an administrator: `.\tools\Add-CopilotTenant.ps1 -Name Fabrikam -TenantId fabrikam.onmicrosoft.com -TargetDomain fabrikam.local`. It registers the app in that tenant with the job server's certificate, creates and installs the tenant's databases, and adds it to the settings.
2. Load its history once: `.\Invoke-CopilotPipeline.ps1 -History -Tenant Fabrikam`. From then on the nightly task loads every tenant in turn; `-Tenant` runs one.
3. On the web server, run the dashboard installer again with every tenant: `-Tenant 'Contoso=CopilotAudit','Fabrikam=CopilotAudit_Fabrikam' -GrantDatabaseAccess`. The dashboard shows a tenant switcher and **Leadership › Group overview**.

Each tenant's database decides who sees what in it: a person sees a tenant only where its directory has them or they are granted a role there. The Admin pages act on the tenant chosen in the switcher, and a form acts on the tenant its page showed, even after another tab has switched (2.4.12); Admin › Alerts and digests can also run the checks in every tenant where you are an administrator, as the background checks do in all of them.

`M -Database CopilotAudit_Fabrikam` works on that tenant. Its rebuilds read the license history that database was last built with (2.4.12; they read `CopilotLicenseEvents`, the first tenant's, before); name another with `-LicenseEventDatabases CopilotLicenseEvents_Fabrikam`.

## Upgrades and renewals

**A new release.** Disable the nightly task and check Admin › Health that no run is in progress. Back up both databases, replace the scripts on the job server with the new release's (keep `copilot-audit.settings.psd1` and `logs`), run `.\Install-CopilotAudit.ps1 -SqlServer SQLPROD01 -Dashboard` once (adding `-LicenseEventDatabases` with the `SessionLicenseDatabases` list if the settings have one), enable the nightly task again, and read the release notes' upgrade section for any one-off step. The installer does the one-off work itself: for 2.3.0 it catalogs the values of records flattened before, and rebuilds the dashboard's rollups once with Premium use; for 2.5.4 it works out the license SKU changes it keeps from then on, and shreds every license record one last time, to record that the shred finished (`-LicenseEventsShredded` skips that, where the last installer run on the license database ended with `Done.`). After that it shreds only what calls for it: records not shredded yet, and what a shred that was stopped part way was shredding. For the dashboard, run `dashboard\Install-CopilotInsights.ps1 -PackagePath <the new copilot-insights zip> -SqlServer SQLPROD01` again: it keeps the site's account, database, `TrustServerCertificate`, tenants, administrators and bindings (2.4.12; before, a run without every first-run parameter reset them).

With more than one tenant, the installer upgrades only the pair of databases it is given, the first tenant's by default, and nothing else upgrades the others: back up and upgrade each further tenant's pair too. A tenant without its own `LicenseDatabase` or `SessionLicenseDatabases` takes the top-level one, as the nightly run does, and one whose license database is blank has none:

```powershell
$s = Import-PowerShellDataFile .\copilot-audit.settings.psd1
foreach ($t in $s.Tenants) {
    $a = @{ SqlServer = 'SQLPROD01'; Database = $t.Database; Dashboard = $true }   # leave Dashboard out without the dashboard
    $lic = if ($t.Contains('LicenseDatabase')) { $t.LicenseDatabase } else { $s.LicenseDatabase }
    if ($lic) { $a.LicenseDatabase = $lic } else { $a.SkipLicenseDatabase = $true }
    $read = if ($t.Contains('SessionLicenseDatabases')) { $t.SessionLicenseDatabases } else { $s.SessionLicenseDatabases }
    if ($read) { $a.LicenseEventDatabases = @($read) }
    .\Install-CopilotAudit.ps1 @a
}
```

Then run the dashboard installer with every tenant in `-Tenant`, as when one was added.

**A new Power BI model.** When the release notes say the model changed, don't run `powerbi\generate_model.py` over the copy you work in: write the new one beside it with `--out` and bring the changes across in Power BI Desktop ([DEPLOYMENT.md](DEPLOYMENT.md), step 15). Run over a copy with your pages or changes in it, the generator stops and changes nothing. Publish your working copy, not the one in `powerbi\`: that one points at `localhost` and has none of your pages or measures. Desktop loads the whole history before you publish, since `RangeStart` and `RangeEnd` span every date; on a large database, narrow them first. A model published again loads every partition on its next refresh, which is the full refresh it needs.

**Running the job-server setup again** keeps every setting in `copilot-audit.settings.psd1`, those added by hand (`IncludeLeaveDates`) and empty ones (`EmployeeTypes = @()`) too, and the task's time set with `-At` (2.4.12; before, a re-run put `EmployeeTypes` back to EMP and NONW, dropped `IncludeLeaveDates`, and moved the task to 02:00).

**The agent workbook app's certificate.** It is renewed by hand, apart from the audit app's: put a new certificate in the job server's LocalMachine\My, give the task's account read access to its private key, upload its public key to the workbook's app registration, and set `AgentWorkbookCertificateThumbprint` in `copilot-audit.settings.psd1`. Remove the old one from the app after the next good run. Admin › Health shows the new expiry after that run.

**The audit app's certificate.** Within 30 days of expiry, run `.\tools\Install-CopilotJobServer.ps1 -RegisterApp` on the job server: a new certificate, its private key for the gMSA, uploaded next to the old one to the app of every tenant in the settings (you sign in to each), and its thumbprint in the settings. Remove the old certificate from the apps after the next good run. A run without `-RegisterApp` keeps the certificate it has until it expires (2.4.12; before, any run in those 30 days switched to a new certificate no app had, and the nightly sign-in failed).

**Exchange Online.** If the dashboard sends with a mailbox password, move it to OAuth (Admin › Email) before Microsoft turns SMTP AUTH passwords off for your tenant.

## Governance Workbench

The Governance Workbench (3.0.0) is the record of who owns and funds each Copilot workload, who approved each change under which decision right, and what evidence shows the approved setting is in place: requests and approvals routed by a RACI matrix, a registry of funding owners, workloads, envelopes, services and control templates, Copilot seats per line of business with their chargeback, consumption events, drift findings, recertification, exceptions and retirement. It reads from Microsoft 365, Entra and the Power Platform and never writes to them. [GOVERNANCE-USER-GUIDE.md](GOVERNANCE-USER-GUIDE.md) says where each role starts; [GOVERNANCE-RUNBOOK.md](GOVERNANCE-RUNBOOK.md) what to do when something fails; [GOVERNANCE-DECISIONS.md](GOVERNANCE-DECISIONS.md) why it is built the way it is; [GOVERNANCE-TEST-ENVIRONMENT.md](GOVERNANCE-TEST-ENVIRONMENT.md) how to run it on one PC.

It has three parts: its pages, served by this dashboard under `/workbench/...`; its own database, `CopilotGovernance`, beside the Copilot database on the same SQL Server (2022 or later: it uses ledger tables, temporal tables and row-level security); and a worker, a Windows service named `CopilotGovernanceWorker` that runs ten background jobs. The PowerShell nightly run is not part of it and does not change.

### Turning it on

The Workbench is off until the site's settings name its database. Off, every `/workbench` address answers 404 and nothing else in the dashboard changes, so 3.0.0 can be installed without it.

1. Create the database with `governance\src\Governance.Database\Deploy-Database.ps1 -SqlServer <server> -Database CopilotGovernance -WebPrincipal <the site's account> -WorkerPrincipal <the worker's account>` (add `-ReaderPrincipal` for a reporting account, and `-TrustServerCertificate` only for a test server with a self-signed certificate). It runs every script in `governance\src\Governance.Database\Scripts` in name order; each is safe to rerun and the schema only grows. The site's account gets the `app_web` role, the worker's `app_worker`, the reporting account `gov_reader`; none is a database owner. Run it as an account that owns the database.
2. Set `ConnectionStrings:Governance` in the site's `appsettings.Production.json`, on every web server, and recycle the app pool. Under IIS the connection uses the app pool's account, so `Integrated Security=True`.
3. Install the worker on one server and give it `ConnectionStrings:GovernanceWorker` (its own connection to `CopilotGovernance`), the `Tenant` section (tenant, the worker's app registration and certificate subject, the notification mailbox), and `Notifications:WorkbenchBaseUrl`. `governance\deploy\appsettings.Worker.Production.sample.json` lists every setting it reads; `Notifications:Mode` must be `Send` for mail to leave the server (`Record`, the default, only writes what would have been sent). [DEPLOYMENT.md](DEPLOYMENT.md) covers installing the database and the worker service.
   [DEPLOYMENT.md, step 27](DEPLOYMENT.md#step-27-the-governance-workbench-optional) does both with `Install-CopilotInsights.ps1 -GovernanceDatabase`, `-WorkerServer` and `-WorkerAccount`.
4. Grant the Workbench roles (below) in Admin › Access, then open `/workbench/Start`. Workbench settings › Intake switch lists the setup steps left before intake can open.

**`Notifications:WorkbenchBaseUrl` is the dashboard's own address**, with its virtual directory if it has one (`https://insights.contoso.com`, or `https://apps.contoso.com/CopilotDashboard`): every link in a notice is that address followed by `/workbench/...`. The default in the worker's `appsettings.json` is a test address; always set it.

**Copilot activity for the seat read.** The seat read takes each person's last Copilot activity from this dashboard's own records (`dash.UserDay` and `bi.DimUser` in the Copilot database) when it can read them: give the worker `ConnectionStrings:Insights`, a connection to the Copilot database with read access to those two objects. It then covers every day the pipeline has loaded and never sees hidden names. Without it, the worker reads Microsoft's Copilot usage report (`Reports.Read.All`), which hides names unless *Display concealed user, group, and site names in all reports* is turned off in the Microsoft 365 admin center (Settings › Org settings › Reports). *Read seats now* on the dashboard uses the dashboard's own connection to the Copilot database.

**With several web servers** (2.7.6), set `ConnectionStrings:Governance` on every one. Run the worker on one server. If a second copy runs anyway, each job except the outbox dispatcher takes a SQL application lock (`sp_getapplock`), so the second copy waits; the outbox dispatcher claims rows one at a time, so two copies never send the same notice. `Worker:DisabledJobs` (a comma list of job names) turns jobs off on one server.

### Copilot seats only (3.6.0)

The Workbench starts cut down to one job: **tracking Copilot license holders per line of business.** Workbench settings › Settings › *What the Workbench is used for* (`Workbench.Scope` in `ref.Setting`) says which:

- **Copilot seats per line of business only** (`SEATS`, the default, also for an install over 3.5.x). The menu has one section, *Copilot seats by line of business*: Copilot seats, Lines of business, Seat chargeback, Seat groups, Workbench roles, Settings and Sync and freshness, each opened by the same roles as before. Every other Workbench address (requests, approval queues, workloads, envelopes, services, templates, cost centers, commercial, discovery, events, findings, attestations, KPIs, the audit trail page, RACI, workflows, SLAs, notifications, intake, wording, recertification, exceptions, retirement) goes to Copilot seats; a form post to one answers 404. A line of business is active as soon as it is added: no seven-item checklist. One added before, still in Draft, has an *Activate* button on its page. Its page shows its seats link, its roles (LOB seat admin only) and suspend or retire; the edit form asks for code, name, part of and business owner, and keeps the finance fields as they are.
- **The whole governance Workbench** (`FULL`): every page and job as in 3.5.x.

The worker reads the setting before every run: with seats only it runs the outbox dispatcher, the directory sync, the seat read and its own record keeping, and the other jobs (SLA timer, entitlement sync, access review import, licensing read, ITSM poller, recertification campaigns, digests) wait without running, so none of them sends a notice or needs its Graph permission (`AccessReview.Read.All` among them). Nothing is deleted either way: switching back to the whole Workbench brings back every page and job with its data. A change reaches every web server within 30 seconds, and the worker at each job's next turn.

**Reading the seats without a worker** (3.6.1). With seats only, *Read seats now* on Sync and freshness (`/workbench/Operations/Sync`) may be pressed by the Platform Owner and Copilot Insights administrators as well as the platform operator, and the menu shows them the page. Where no worker service is installed, that button is how the seats are read: it does the whole seat read from the site, the draft chargeback included. With the whole Workbench, only the platform operator runs it, as before.

**Reservations with seats only** (3.6.2). Each line of business's seats page (Copilot seats › the line of business) has *Set the reservation*: the seats reserved, the date they apply from and the source, for the Platform Owner alone (FinOps and the line of business's own roles see the figures but cannot change them). A new figure ends the one before on the day before, so the history and the monthly chargeback keep every figure; the same date as the latest corrects it; an earlier date is refused. Each change is in the audit trail. Any figure in the history can be corrected afterwards with its *Edit*: its seats, its start date or its source. The date stays between the start dates of the figures before and after it; the figure before then ends the day before the new date, and the one after shows the corrected seats as what it replaced. Every earlier version is kept (the table is system-versioned) and each correction is in the audit trail; certified monthly chargeback snapshots do not change. A figure can also be deleted with its *Delete*: the figure before it runs on to where the deleted one ended (to now, if it was the latest), and the one after shows the figure before as what it replaced; the deleted row stays in the table's version history and the deletion is in the audit trail. With the whole Workbench, reservations change through seat reservation requests, as before.

**People in the tracking group** (the same page) and **Seats no LOB claims** (Copilot seats › No LOB) list 50 people a page, sorted by sign-in, with *Previous*, *Next* and *Export CSV* under the list; the CSV holds the whole list on show (with a seat, inactive, no seat or everyone), every page. A person the Workbench has not recorded is named from the directory Copilot Insights loads from AD each night, by sign-in, else by directory object ID; one found in neither shows *Not in the directory* with the sign-in or object ID. Every Workbench list's *Previous* and *Next* now move between pages (before, every list stayed on its first page).

**Sync and freshness with seats only.** Only the *Copilot seats* card shows (*Read seats now*); the inventory, licensing and Agent Registry cards are hidden, as those reads are not used. The reads started there are limited to two every five minutes for each person; opening or refreshing the page does not count (before, every visit counted too, for everyone on that web server together, so a press often answered HTTP 429).

**People for a line of business.** The same page has *People for this line of business* near the top: the business owner and the seat admins, changeable at any time after the line of business was added. The Platform Owner or FinOps changes or clears the business owner there (the new person holds the owner role from today, the previous owner no longer); seat admins are added and ended there as before (to replace one, add the new person, then end the old one). Nobody names themselves. Each change is in the audit trail. The business owner can also still be changed on the line of business's Edit form.

**Copilot seat reports.** The *Copilot seats by line of business* menu also has these pages, for auditors, central roles, Copilot Insights administrators and the Platform Owner. Every list pages 50 at a time and exports the whole list as CSV.

| Page | What it shows |
|---|---|
| Reservation summary | Each line of business: reserved licenses, allocated (held) licenses, remaining, over/(under) the reservation, monthly reserved cost, 12-month reserved forecast, monthly actual cost (the reservation, or the seats held when more; red over, green under, yellow even), monthly overage (with a bar) and annual actual cost, at the rate Seats.RatePerSeatMonth, with a total row. Seats no tracking group claims are counted apart. |
| Hierarchy audit | The executive summary of the tracking group and manager hierarchy audit: population and outcome metrics with their definitions (PASS, NEW HIRE, each REVIEW reason, validated population, pass rate, new-hire %, a reconciliation difference that must be zero), the exception hotspots (the group with the most REVIEW records, the group with the highest REVIEW share) and the results by tracking group. |
| Exception report | REVIEW records only, with the expected leader and level, who is actually at that level in the holder's manager chain, the reason and the potential correct group and leader. |
| New hire report | Holders hired within Seats.NewHireDays (default 90), whose check is deferred. |
| Detailed audit | Every holder and tracking group with its result; filter by tracking group, search by name or sign-in; the CSV adds the hire date and the whole manager chain. |
| Multiple groups | People in two or more tracking groups (counted by more than one line of business), and people licensed through more than one group, or through a group and directly. |
| Hierarchy mapping | For each tracking group, the expected leader (by sign-in, with the Active Directory search) and the level that leader sits at (`core.SeatGroup.ExpectedLeaderUpn`, `ExpectedLevel`). The Platform Owner sets it; each change is in the audit trail. |

The audit checks each Copilot seat holder in a tracking group (once per group they are in): their manager chain comes from the directory Copilot Insights loads from AD each night, with levels counted from the top of the organization set in Copilot Insights (level 1 is the top, level 2 reports to the top). PASS when the person at the group's expected level is the expected leader. REVIEW reasons, in the order they are tried: *Unknown tracking group* (the group has no mapping), *Missing data* (the holder or their chain is not in the directory, or does not reach the level), *Possible incorrect tracking group* (another group's mapped leader is in the chain at that group's level; the deepest such group is suggested), *Expected leader at different hierarchy level*, *Hierarchy mismatch*. A holder hired within Seats.NewHireDays (from the hire date the license load records) is NEW HIRE and not checked. The audit is read again at most every five minutes, and at once after a mapping change. It never changes a group or a license. Not measured: duplicate employee IDs (the directory load has no employee ID) and manager or organization changes (no manager history is kept); *Possible duplicate holders* counts rows of people in more than one tracking group instead.

**Tracking a line of business, start to end.** Lines of business › *Add a line of business* (code, name, business owner) › Seat groups: its AD tracking group › Copilot seats › *Read seats now* (or wait for the daily read) › the line of business's row: who holds a seat, when each last used Copilot, inactive holders, and the holders' CSV.

### Who opens which pages

Sign-in is the dashboard's own: Windows, or Microsoft Entra ID where the site is set up for it ([ENTRA-SIGN-IN.md](ENTRA-SIGN-IN.md)). Which Workbench pages a person may open comes from their grants in Admin › Access, given to people, AD groups or Entra groups like any other role, or through an access profile. Three starter profiles are offered in Admin › Access profiles: *Governance team*, *Governance operator* and *Governance auditor*.

| Role | Pages it opens |
|---|---|
| The Ownership and approvals section (3.1.0: granted in *Grant access*) | Start, My work, Requests, Report a consumption problem, Recertification, Exceptions, Retirement, Your Workbench roles. They see their own requests and what their Workbench roles allow. Approvers, seat admins and delegates need it: their task emails open these pages |
| The Ownership registry section (3.1.0: granted in *Grant access*) | Funding owners, Workloads, Envelopes, Copilot seats (to seat roles), Services, Control templates |
| Governance | Both sections above without a grant, plus Approval queues (Intake, Classification, Specialist reviews, Decisions, Fulfillment and verification), the whole registry (also Cost centers, Commercial, Discovery), Consumption events, Drift and findings, Governance KPIs, Audit trail, Seat chargeback |
| GovernanceOperator | Everything Governance opens, plus Sync and freshness (running reads) and Attestations |
| GovernanceAuditor | Governance KPIs, Audit trail and Seat chargeback, read only; the two everyday sections only where granted |
| Admin | Both sections above without a grant, the approval queues, the whole registry, consumption events and findings, and every Workbench settings page. Not the reports or the operator pages: grant those roles as well where an administrator needs them |

- **These roles open no usage data.** They give no Insights report, do not let anyone past the dashboard's level limit (Admin › Access › *Who can open the dashboard*), and never widen to the whole organization.
- **The work people do is given inside the Workbench, not in Admin › Access.** RACI positions (Platform Owner, Steering, intake, operators, architects, FinOps and the others) are assigned on Workbench settings › Workbench roles; roles for one line of business (business owner, delegated approvers, finance partner, LOB seat admin) on that funding owner's page; delegations by the business owner. They decide what a person may approve or record on the pages they can open. **Copilot seats** in the registry shows to whoever holds a seat role: FinOps, the Platform Owner and auditors see every line of business, the line of business's own roles only theirs.
- **The Platform Owner and Steering** also reach five settings pages without Admin: RACI, Intake switch, Workbench roles, Wording and Seat groups.
- **Anyone in the directory** (3.5.2). Every person picker in the Workbench (Platform Owner and central roles, business owner, finance partner, LOB roles, delegates, seat admins, product owners, sponsors, purchase authority) has *Or search everyone in Active Directory* under it, always open (3.6.1; before, behind *Not in the list? Find someone in the directory*): it searches the directory Copilot Insights loads from AD each night, by name or sign-in, and the person chosen is recorded in the Workbench and chosen in the picker, so they can be named before they ever open the Workbench. It is offered to Copilot Insights administrators, central roles and anyone holding a Workbench role; each person recorded is in the audit trail. Before, the pickers listed only people who had opened the Workbench or whom the worker's directory sync had read.
- **The first Platform Owner** (3.5.1). Central roles are given by the Platform Owner, or synced from an Entra group (which needs Microsoft Graph's group permission). While nobody holds the Platform Owner role, a Copilot Insights administrator may name one, themselves included, on Workbench settings › Workbench roles: the form then offers that role alone. Once someone holds it, the Platform Owner gives the central roles.
- **Admin › Access › Check someone's access** shows both: the Workbench pages their grants open, and their Workbench roles and delegations.
- **A refused page** says *This page is not available to you* and names who grants what.

### Workbench settings

Every change on these pages is written to the Workbench's audit trail. Administrators configure; they never approve business decisions or change decision records.

| Page | What it is for | Who may open it |
|---|---|---|
| Workbench settings › RACI | The active 19-activity matrix and the 12-row view generated from it; drafting a new version. A version activates only when every activity has exactly one A or A/R, and only after the Platform Owner and Steering, two different people, approve it | Admin; Platform Owner; Steering |
| Workbench settings › Workflows | Each journey's stages, who acts at each, the service level and what follows each outcome. Read only: a request stays on the version it was submitted under | Admin |
| Workbench settings › SLAs and escalation | How long each stage may take, the escalation ladders, holidays and the business time zone. Reminders go at 50 and 80 percent of the time; at 100 percent a task escalates one level. Calibrate during the first weeks of use | Admin |
| Workbench settings › Notifications | Which notices (N01 to N22) go to whom, by email and Teams, and quiet hours. Email is the notice of record; nobody approves by email or chat | Admin |
| Workbench settings › Workbench roles | Who holds each central role, with its dates; syncing a role from an Entra group. Every assignment is reviewed each year. Roles for one line of business are on its funding owner's page | Admin; Platform Owner; Steering |
| Workbench settings › Seat groups | The Copilot seat groups (the shared license group, one tracking group per line of business with its OIM role, exclusion, Copilot Chat only and other groups), the Copilot SKUs, and the seat settings in use. A change ends the old row and starts a new one, so history is kept | Admin; Platform Owner; Steering |
| Workbench settings › Integrations | The last run of every worker job and integration, the outbox and dead letters, and how fresh each source is. Read only | Admin |
| Workbench settings › Intake switch | Opens intake once the setup steps are done (decision rights, funding owners, catalog, templates, commercial inventory, standards, discovery, envelopes), or closes it. Closing stops new submissions only | Admin; Platform Owner (only the Platform Owner opens or closes it) |
| Workbench settings › Wording | Help text and notice headers. An administrator drafts; the Platform Owner approves; the approved text shows at once | Admin; Platform Owner; Steering |
| Workbench settings › Settings | The Workbench's own settings (3.5.1): the business time zone, the days before exception and renewal notices, how old the inventory may be, when attestations escalate, evidence files (size, types, years kept), how long the worker keeps its records, the address in notices, and the missed directory reads before the banner. Each value is checked and each change audited; the seat settings are on Seat groups and intake on the Intake switch. On top (3.6.0), *What the Workbench is used for*: Copilot seats per line of business only, or the whole Workbench (see *Copilot seats only* above); with seats only, the list shows just the settings the seat pages and the worker still read | Admin; Platform Owner (Steering reads) |

With the Workbench on, Leadership › Copilot reservations and Licensing › Reservation holders read these seats, and Insights' own register is kept but no longer edited: see *With the Workbench on* under Copilot reservations above, which also covers *Start from Insights' reservations*.

**Copilot seats, the first time.**

1. Workbench settings › Seat groups: add the shared license group (purpose License), then one tracking group per line of business with its line of business and its OIM role name (for example "Retail . M365 Copilot License . Role"). Add the other Copilot groups as Exclusion, Copilot Chat only, Managers or Other, unconfirmed until their owners confirm them: an unconfirmed exclusion group raises no findings.
2. Add the Copilot add-on SKU (part number `Microsoft_365_Copilot`) as *Copilot add-on*, and any suite that includes Copilot (E7) as *Includes Copilot*.
3. Check the seat settings with Finance: the EA anniversary, the true-down window, the operational buffer, the one rate per seat and its currency, when increases take effect, the chargeback year and the inactivity days, and Finance's three open choices, each with a default the seat pages name: who pays for seats no line of business claims (`Seats.NoLobChargedTo`: an active funding owner's code, charged to it as seats above its reservation, or empty for a row of their own, charged to no line of business), the rate (`Seats.RatePerSeatMonth`, a placeholder until Finance sets it) and when an increase takes effect (`Seats.IncreaseEffective`: `DECISION_DATE` or `NEXT_MONTH`). The purchase gate is `Seats.PurchaseGatePercent` (95) of the unassigned seats counted at `Seats.GateBaselineUnassigned` (0 until set): the same rule, and the same sentence, on Leadership › Copilot reservations and Ownership registry › Copilot seats. Each is changed on the page (3.5.1): its value box and *Save*, for a Workbench administrator, the Platform Owner or FinOps; a value the pages cannot read is refused, and each change is in the audit trail.
4. FinOps records each line of business's first reservation on its Copilot seats page. Later changes are seat reservation requests.
5. The business owner (or the Platform Owner) names each line of business's first seat admin on the same page; seat admins name the others.

**Seat groups from on-premises AD (3.5.1).** The seat read takes each seat group's members from Microsoft Graph, which needs `GroupMember.Read.All` for the worker's app. Where that is refused, it reads them from on-premises Active Directory instead, over LDAP, as the worker's account (or, for *Read seats now*, the site's app pool account): the groups are synced to Entra, so AD holds the same members. Nothing is set for this by default. `Seats.GroupMembersFrom` in `ref.Setting` says where: `AUTO` (the default) tries Graph and turns to AD for on-premises groups when Graph refuses, `AD` reads AD only, `ENTRA` Graph only.

- **The domain** is the users' AD domain set on Admin › Directory quality, which the nightly directory load uses too. `Seats.AdDomain` overrides it (a DNS name or a domain controller) where the groups are in another domain; empty, and with no domain on Directory quality, it is the domain the server is in. The servers may be in another domain than the groups and users, as with the nightly load: any account of a trusted domain can read groups and members by default.
- **The group's name** on Seat groups is its AD account name (sAMAccountName), common name or display name; a `DOMAIN\` in front is left out. The group's AD objectGUID is recorded (`core.SeatGroup.AdObjectGuid`) and its members are kept under it.
- **Each member is matched to their Entra account** by the SID Copilot Insights' directory load recorded with their Entra object ID (`dash.Person`), then by sign-in name, then by mail. Members that match none are not counted, and the run says how many (Workbench settings › Integrations): the nightly directory and license loads match most of them, so run the seat read after them.
- **The worker reads three things in the Copilot database** for this: the view `dash.WorkbenchDirectory` (the domain), and `Upn`, `Sid` and `ObjectId` of `dash.Person`. The installer grants them with the worker's other reads; on a server upgraded by hand, run in SSMS on the Copilot database: `GRANT SELECT ON dash.WorkbenchDirectory TO [<worker gMSA>]; GRANT SELECT ON dash.Person (Upn, Sid, ObjectId) TO [<worker gMSA>];`
- **A cloud-only group** cannot be read from AD: it keeps its last membership and the run says so.

### Worker jobs

Each job runs on its own timer from when the worker starts (the first runs a few seconds to a few minutes after start, in the order below). Each run is recorded in `intg.JobRun`, and Workbench settings › Integrations shows the last one. Two failures in a row send notice N19 to Workbench administrators and Platform Operations. Errors are written to the worker's log; the page shows only a short note.

| Job (name for `--once`) | Runs | What it reads and does |
|---|---|---|
| Outbox dispatcher (`OutboxDispatcher`) | Every 5 seconds, on every worker | Delivers the notices (email, Teams activity, Teams channel) and ITSM commands each change wrote to the outbox. Gives up after `Notifications:MaxAttempts` (6) and records the message as a dead letter |
| SLA timer (`SlaTimer`) | Every minute | Reminders at 50 and 80 percent of each task's SLA, escalation at 100 percent, and closes returned requests after 10 business days |
| Directory sync (`DirectorySync`) | Every 15 minutes | Entra users by Graph delta (cost center, division, manager, account status), membership of governed groups, central roles synced from Entra groups; leavers and role holders who moved raise vacancy findings (N20) |
| Entitlement sync (`EntitlementSync`) | Every 15 minutes | Entra access package assignments and requests for the governed catalog, recorded as decisions from Entra; assignments without an active envelope raise N15 |
| Access review import (`AccessReviewImport`) | Every 24 hours | Entra access review decisions, as recertification evidence |
| Licensing read (`LicensingRead`) | Every 24 hours | Power Platform licensing per governed environment (allocation, tenant-pool rule, billing policy), then drift findings against the approved posture (N10). Skipped when the worker has no Power Platform reader role (`Tenant:PowerPlatformReader`) |
| Copilot seat read (`SeatSync`) | Every 24 hours, and *Read seats now* on Sync and freshness | Copilot SKUs bought and assigned, the seat groups and their members, each person's license state, and Copilot activity (see above). Then seat findings, reclaim proposals for seats inactive for `Seats.InactiveDays` (and held at least that long: a new seat gets the same time to be used), and, in the first week of a month, the draft chargeback for the month before. A refused or hidden usage report is a note on the run, not a failure; no seat is proposed for reclaim that run |
| ITSM poller (`ItsmPoller`) | Every 5 minutes | The state of each open ITSM ticket. A closed fulfillment ticket moves its request to verification, never straight to completed. A ticket that cannot be read keeps the reason on itself (`wf.ItsmTicket.PollError`) and the poll goes on with the next; a ticket the ITSM no longer has shows as `NOT_FOUND` on its request for an operator to check, and is still polled in case it comes back. The run fails only when no ticket at all can be read |
| Campaign scheduler (`CampaignScheduler`) | Every hour; the work is daily, and each notice is sent once | Recertification campaigns, monthly re-attestation (N22), exception expiry (N13 at 30, 7 and 1 days), commercial renewals (N18 at 90, 60 and 30 days), inventory older than 7 days (N21) |
| Digest builder (`DigestBuilder`) | Every 15 minutes | One email a day per person, from 07:00 in the business time zone (a person can choose another hour), with the items they are informed of (N17) |
| Retention (`Retention`) | Every 24 hours, first 10 minutes after start | The worker's own bookkeeping older than `Retention.WorkerRecordDays` (Workbench settings › Settings; 180 days; never under 30): job runs (each job's latest run and latest success are kept), delivery records of finished notices, and the text of processed outbox items, which keep their row and key so nothing is sent twice. Items set aside as dead letters are kept whole. 5,000 rows at a time. Ledger and audit tables are never touched |

**Run a job again now.** There is no button for it on the dashboard. On the worker's server, as the worker's account, run the worker once with the job's name, then check Workbench settings › Integrations:

```powershell
.\Governance.Worker.exe --once SeatSync              # several: --once DirectorySync,EntitlementSync
.\Governance.Worker.exe --drain                       # deliver every notice due now, then stop
```

The jobs are safe to rerun: a notice is never sent twice (each has a de-duplication key) and a second ITSM ticket is never created (the adapter searches by correlation ID first). A job another worker is running is skipped, and the line says so.

**Read by an operator.** The Power Platform inventory (environments, Copilot Studio agents and agent flows) accepts only a signed-in person's token, so an operator runs it from Governance operations › Sync and freshness, at least weekly and before every verification. It needs the operator's own Microsoft Entra sign-in; on a site that does not request that token, the page says so and nothing is read.

### The ledger digest

Decisions, evidence metadata, attestations, verifications and the audit trail are ledger tables: rows can be added, never changed or deleted, and SQL Server keeps a hash of them. A digest of that hash, kept outside the database on write-once storage, lets an auditor prove that nobody, database administrators included, changed those rows afterwards. SQL Server on premises keeps digests by itself only in Azure immutable storage, so a script saves them to a share:

```powershell
pwsh -File governance\deploy\Save-LedgerDigest.ps1 -SqlServer SQLPROD01 -Database CopilotGovernance -DigestPath \\worm01\CgwLedger$
pwsh -File governance\deploy\Save-LedgerDigest.ps1 -SqlServer SQLPROD01 -Database CopilotGovernance -DigestPath \\worm01\CgwLedger$ -Verify
```

- **Schedule the first every 4 hours** (Task Scheduler or a SQL Server Agent PowerShell step), as an account with `GENERATE LEDGER DIGEST` and `VIEW LEDGER CONTENT` on the database and write access to the share. It needs `sqlcmd` on the server.
- **Run `-Verify` monthly,** after any restore, and when an auditor asks. It checks the database against every digest saved so far, and needs `ALLOW_SNAPSHOT_ISOLATION ON` on the database, which `Deploy-Database.ps1` does not set: a database administrator sets it once.

The installer registers the first as the task *Copilot Governance ledger digest* when given `-LedgerDigestPath` ([DEPLOYMENT.md, step 27](DEPLOYMENT.md#step-27-the-governance-workbench-optional)): daily at `-LedgerDigestAt` (02:00 unless given), as the worker's account. For every 4 hours, add a repetition to that task in Task Scheduler.

### Database and test tools

- **Upgrade** by running `Deploy-Database.ps1` again with the new release's scripts. Every script is safe to rerun and only adds; from 3.0.0 the governance database follows the same rule as the Copilot database: nothing is dropped, renamed or narrowed.
- **Row-level security** shows each line of business only its own rows; central roles see all. Each connection the site opens carries the person and whether they hold a central role, read only, and the database filters by them.
- **Reporting tools** connect as an account in `gov_reader` and read the `rpt` views.
- **Test tools**, not for production: `governance\setup\Setup-TestTenant.ps1` sets up a test tenant, `governance\tools\Governance.Seed` seeds a demo institution, `governance\tools\Governance.MockItsm` is a local ITSM stand-in on port 5470. [GOVERNANCE-TEST-ENVIRONMENT.md](GOVERNANCE-TEST-ENVIRONMENT.md) uses all three.

## Every Admin page and maintenance task

<!-- Kept complete by tests/Test-OperationsGuide.ps1: every Admin page in dashboard/CopilotInsights/Pages/Nav.cs and every -Task of tools/Invoke-CopilotMaintenance.ps1 must appear in this section. -->

| Admin page | What it is for |
|---|---|
| Admin › Health | Report speed (when the report pages were last built ahead on this web server, the last morning pre-cache and its slowest page from memory, what the web servers share in the database (2.7.6), and how the cache works), pipeline runs and each step's freshness, directory coverage, the latest day with data, each certificate the pipeline signs in with (the Purview audit app's, the SharePoint agent workbook app's and the Foundry usage app's where it has its own) and its details, the audit searches waiting and what the last load took, the last run of the optional GitHub Copilot and Foundry usage loads (2.6.2), and a manual refresh |
| Admin › Access | Who can open the dashboard (everyone, or levels 1 to N), the levels never on revoke or train lists, the top of the organization, the access settings and license price, granting sections and roles (3.1.0: *Grant access*, sections laid out as the menu) and removing them (to people, AD groups and Entra groups, across the whole organization or not: 2.8.0), limiting an administrator to some Admin pages (2.6.4), checking someone's access, section by section (3.1.0), and who loses access (3.1.0). Every price and assumption, wherever it is set, is listed on Leadership › How figures are calculated (2.6.9) |
| Admin › Access profiles | Named bundles of roles and Admin pages (2.8.0): the profiles and what each gives, adding and changing them, granting them to people and groups, who holds each, and the bundles held today offered as profiles |
| Admin › Directory quality | People without a manager, department, title, email or SID; managers missing or disabled; people outside the org chart; each listed and downloadable. The users' AD domain and the report's two groups; *Load the directory now* (3.5.1) |
| Admin › Email | How email is sent, a test message, where the logs are with the last test's conversation with the mail server (2.6.2), and everything sent or not, with the conversation of each test and each message not sent |
| Admin › Alerts and digests | Alert recipients and thresholds, digests, manager digests and the managers who do not get them, the webhook, running the checks now |
| Admin › Targets and assumptions | Adoption targets, minutes saved and an hour's worth, the licensing group, champions per department, the rollout wave size, and the prices and assumptions of Leadership › Costs and savings (2.6.6): with each per-user price its volume price, the Dataverse capacity a seat brings, its spare share and whether it is bought for capacity, and the spare seats kept unassigned (2.6.9) |
| Admin › Privacy and retention | Privacy mode, retention of logs and JSON, the data API |
| Admin › Reference data | Agent names and owners, sensitivity label names and order, app categories, which accounts are not a person (non-human accounts) |
| Admin › Value names | The product name for each license part number (2.7.1), and names for every cataloged code, one by one or by CSV, with the mapping file to download |
| Admin › Classifications and events | Job-family rules, use-case rules, and the events marked on trend charts |
| Admin › Reports shown | Which reports the dashboard has: a box to tick for each, by section, and Beta beside it (3.1.6). A report turned off is gone for everyone, administrators too; a beta report for everyone but administrators and the people given a preview of it |
| Admin › Scheduled extracts | Aggregate CSV files written to a folder or share on a schedule |
| Admin › Training records | Training attendance, by CSV, with the mapping file to download |
| Admin › Audit log | Every view, refusal, export, API call and admin change; an admin change written in the same transaction as the change, with what was submitted (secrets masked) and the settings and role grants it changed, before and after (2.7.6, *What changed*) |

| `-Task` of `Invoke-CopilotMaintenance.ps1` | What it does |
|---|---|
| `Status` | What is loaded and built, and when each step last ran |
| `Reflatten` | Flatten every record again, then rebuild sessions, `bi` and the dashboard |
| `RebuildSessions` | Rebuild every session, then bring `bi` and the dashboard up to date |
| `RebuildBi` | Rebuild `bi` in full, then the dashboard |
| `RefreshDashboard` | The dashboard's org chart, and its rollups from `-SinceUtc` (every day when omitted) |
| `SetAppCategory` | Put `-App` in `-Category` (Copilot Chat or In-App Copilot); `-Apply` rebuilds now |
| `GrantRole` | Give `-Principal` (UPN, `DOMAIN\user` or `DOMAIN\Group`) a `-Role`, or the menu sections in `-Sections` (with `-NamedPeople` and `-WholeOrganization`; granting them again replaces the principal's sections) |
| `RevokeRole` | Take it away |
| `SetOrgTop` | Make `-Upn` the top of the organization, or `-Clear` |
| `SetDashboardSetting` | Set MinGroupSize, DefaultDays, InactiveDays, LicenseMonthlyCost, LicenseCurrency, PaygInteractionCost, MaxViewerLevel (who can open the dashboard: 0 everyone, else levels 1 to N) or ProtectedLevels (levels 1 to N never on a revoke or train list: 5 by default, 0 none) to `-Value` |
| `NameAgent` | Give agent `-AgentId` a `-FriendlyName` and `-Owner` |
| `NameLabel` | Give label `-LabelId` a `-FriendlyName` and `-Rank` |
| `PurgeJson` | Clear the stored JSON of records older than `-RetainDays` (`-Apply` to do it), or with `-DeleteRows` remove those records and the rows made from them (`bi` and the dashboard keep theirs until `RebuildBi`) |
| `RebuildValueCatalog` | List every cataloged column's values again, from every flattened record (the installer does it once when upgrading) |
| `ListValues` | The cataloged columns, or one column's values and names (`-ColumnId`, `-Path` for a CSV) |
| `ImportValueNames` | Names for cataloged values from `-Path` |
