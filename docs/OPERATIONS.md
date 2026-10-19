# Operations guide

What to do once copilot-audit-backfill and the Copilot Insights dashboard are installed: every task an operator performs, where to do it, and the command for it. [DEPLOYMENT.md](DEPLOYMENT.md) covers installing; [DASHBOARD.md](DASHBOARD.md) explains the dashboard's pages and rules; [RUNBOOK.md](../RUNBOOK.md) is the pipeline's reference, with limits, tuning and every error message.

Two places do everything:

- **The dashboard's Admin pages**, for people holding the Admin role. Every change is checked, needs the page's anti-forgery token, and is written to the audit log.
- **`tools\Invoke-CopilotMaintenance.ps1`** on the job server, for the same tasks without the dashboard, in scripts, or when nobody can sign in. Below, `M` stands for `.\tools\Invoke-CopilotMaintenance.ps1 -SqlServer SQLPROD01` (add `-Database` if it is not `CopilotAudit`, and `-TrustServerCertificate` for a self-signed SQL Server certificate).

Nothing here needs SQL typed by hand, but for looking inside a slow night ([A slow nightly run](#a-slow-nightly-run)), which is the database administrator's. `tests/Test-OperationsGuide.ps1` fails the build when an Admin page or a maintenance task is not in this guide, so the [reference](#every-admin-page-and-maintenance-task) at the end is complete.

## Contents

- [Every day](#every-day)
- [Access and permissions](#access-and-permissions)
- [Names for codes and ids](#names-for-codes-and-ids)
- [Email, alerts and digests](#email-alerts-and-digests)
- [Targets, value and training](#targets-value-and-training)
- [Costs and savings](#costs-and-savings)
- [Copilot reservations](#copilot-reservations)
- [Classifications, events and extracts](#classifications-events-and-extracts)
- [Acting on the license recommendations](#acting-on-the-license-recommendations)
- [Triage and investigations](#triage-and-investigations)
- [Privacy, retention and the data API](#privacy-retention-and-the-data-api)
- [The pipeline](#the-pipeline)
- [A slow nightly run](#a-slow-nightly-run)
- [More than one tenant](#more-than-one-tenant)
- [Upgrades and renewals](#upgrades-and-renewals)
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

**Several web servers.** Set `Insights:DataProtectionKeysPath` to a share on every web server, or forms posted to a server other than the one that drew them are refused (HTTP 400); change `appsettings.Production.json` on every server; each keeps its own report cache. [DASHBOARD.md](DASHBOARD.md#several-web-servers) has the steps.

**The morning pre-cache (2.5.1).** Every day at 07:00 Eastern Time (EST in winter, EDT in summer), the scheduled task *Copilot Insights pre-cache* on each web server runs `Invoke-InsightsPrecache.ps1` from the site's folder. The site builds every report page again, for the whole organization at every period, reading each query afresh, then builds each page once more from memory and times it. Whoever opens a page during the build gets the result kept before, so nobody waits. Each run adds its result to `precache\precache.log` in the site's folder, and the task's last result is its exit code: 0 built, every page from memory in under a second; 2 built, with the pages that took longer listed; 1 not built, with why. The pages for a part of the organization, and a detail page (one app, one agent), are kept from the first time someone opens them; the pre-cache builds the whole organization's. To run it now, after a restart for example, run it by hand in a PowerShell window as an administrator, from the site's folder (`C:\inetpub\CopilotInsights` unless installed elsewhere): it waits, then says how it went.

```powershell
cd C:\inetpub\CopilotInsights
.\Invoke-InsightsPrecache.ps1                                                     # build every page now, and wait
.\Invoke-InsightsPrecache.ps1 -Register -At 06:30 -TimeZone 'Eastern Standard Time'   # another time
.\Invoke-InsightsPrecache.ps1 -Unregister                                         # no pre-cache
```

The dashboard installer schedules it (07:00 Eastern unless given `-PrecacheAt` and `-PrecacheTimeZone`), and a later run keeps the time it has. *Not built: the site did not take the request* means the app pool is stopped, or the site cannot change its `precache` folder: run the installer again, which grants it.

## Access and permissions

**How access works.** Everyone signs in with their Windows account. The top of the organization, the people who report directly to them and anyone granted **Organization** see the whole organization; everyone else sees themselves and the people under them. Pages that name people also need their persona's role. **Admin** runs the Admin pages and sees no usage data by itself. [DASHBOARD.md](DASHBOARD.md#who-sees-what) has the full rules.

| Role | Gives |
|---|---|
| Admin | The Admin pages: all fourteen, or only the ones ticked for that person or group (2.6.4) |
| Organization | The whole organization's aggregates, whatever the person's level |
| Adoption | Named people licensed but not started |
| Licensing | Named unused licenses, Copilot Chat users, licenses on disabled accounts, upgrade/train/revoke lists, the use ranking, rollout waves, license action outcomes, and their export files |
| Security | Named flagged interactions and their triage; named guests |
| Compliance | Named people who worked with labeled content; one person's full record (works in privacy mode) |
| Agents | Named users of an agent |
| Enablement | Named champions and engagement segments |

**Grant or remove a role.** Admin › Access › *Grant a role*: a person (their UPN, or `DOMAIN\user`) or an AD group (`DOMAIN\Group`), the roles (tick as many as they need), and why. Grant to groups where you can: membership is read from each sign-in, so it follows AD with no change here. Power BI's *Organization* role honors the same grants from the next nightly directory load, which records each `DOMAIN\` grant's members (a person's own UPN, from 2.4.12). The grants list has one row per person or group with all their roles, and a small × box beside each role that removes it, after asking. *Remove all*, at the end of the roles, removes every role that person or group holds (2.6.3). To clear several at once, tick their rows (*Select all on this page* ticks every row shown) and choose *Remove every role from the selected* under the table. Both ask first, and the audit log records who was cleared and the roles each held. Someone cleared keeps what their level and their AD groups give them. The Adoption, Licensing (with Forecasting), Security, Compliance, Agents and Enablement sections are hidden from anyone without their role, whole-organization viewers included, so grant executives the roles whose sections they should see.

```powershell
M -Task GrantRole -Principal 'USERS\Copilot-Insights-Licensing' -PrincipalType Group -Role Licensing -Note 'licensing team'
M -Task RevokeRole -Principal 'ada.lovelace@contoso.com' -Role Licensing
```

**Limit an administrator to some Admin pages (2.6.4).** Admin › Access › *Grant a role*: tick **Admin**, then tick, under *Admin pages*, the pages that person or group should run, any of the fourteen: Health, Access, Directory quality, Email, Alerts and digests, Targets and assumptions, Privacy and retention, Reference data, Value names, Classifications and events, Reports shown, Scheduled extracts, Training records and Audit log. They see those pages in the navigation and no other Admin page, can change what is on them, and are refused the rest, page, form and file. With none ticked, or all fourteen, Admin is the whole role, as every Admin grant was before.

- **To change someone's pages,** grant Admin to them again with the new pages ticked: the pages are replaced, and the message says what the role now covers. Granting it again with none ticked makes them a full administrator.
- **The grants list** shows a limited Admin role with its pages in brackets, and *Check someone's access* says what a person's Admin role covers.
- **Only a full administrator gives, limits or removes Admin.** Someone limited to Admin › Access can grant and remove the other roles and change that page's settings, and cannot make themselves, or anyone, an administrator. Nobody can limit their own Admin role.
- **A person in several grants** gets the pages of all of them (their own and their groups'); one whole Admin grant among them makes them a full administrator.
- **Outside the dashboard's levels,** a limited administrator keeps their Admin pages, as a full one keeps all of them.
- **The data API's `health` call** answers whoever may open Admin › Health.
- **From the job server,** `M -Task GrantRole ... -Role Admin` grants the whole role; limit it from the page. Administrators named in the site's configuration (`Insights:BootstrapAdmins`) are always full administrators.
- **Before the database is upgraded,** a 2.6.4 dashboard treats every Admin grant as whole and offers no pages to tick: run `Install-CopilotAudit.ps1 -SqlServer <server> -Dashboard`, which adds the column the pages are kept in.

**Set the time zone.** Admin › Access › *Settings* › *Time zone* (2.4.10). Every time the dashboard shows (the audit log, pipeline runs, when a license was read, when an alert went out) and the hour digests, alerts and extracts are sent is in this zone, and each column says which ("When (Eastern Time)"); before, they were in UTC. Left on *Default* it is the zone the data's days are cut in, which a new install takes from its server (and an install made before 2.4.10 without `-ReportingTimeZone` has as Eastern time); on a database without one, the web server's own. Choosing a zone here changes what is shown and when things are sent, at once. It does not move the days in the reports: those are cut when the data is loaded and cannot be changed afterwards.

**Check what someone sees.** Admin › Access › *Check someone's access*: their level, their scope, whether the level limit keeps them out, and the roles granted to their account directly (group grants are resolved when they sign in).

**Keep leaders off revoke and train lists (2.4.6).** Admin › Access › *Never on revoke or train lists*: levels 1 to N of the organization (5 by default; *None* lists anyone), each choice with how many people it protects. People at those levels are never on Upgrade, train or revoke (train and revoke), the Swap list (take side), Unused licenses, Better on pay-as-you-go, Fading use, Not started, Enablement's one-off and not-started people, or License optimization's counts, nor in their CSVs, removal files, the Licensing digest's counts, the Board pack, Budget and renewal, or the data API. Each of those lists says so at the top. Lists of accounts nobody can use keep everyone: Licenses on disabled accounts, Departures ahead and Copilot on non-human accounts. From the job server: `M -Task SetDashboardSetting -Name ProtectedLevels -Value 5` (0 for none).

**Limit the dashboard to the top levels (2.4.4).** Admin › Access › *Who can open the dashboard*: *Everyone in the directory*, or *Levels 1 to N* below the top of the organization, each choice with how many people it lets in. Anyone granted a role other than Admin is let in whatever their level, so grant a role to an analyst who needs it. Everyone else is refused every page, export and API call. Administrators outside the levels keep the Admin pages, so a wrong choice can be undone here. Managers outside the levels stop getting the manager digest. It applies from the next page, on every web server (2.4.12; the digests within a minute). The same, from the job server:

```powershell
M -Task SetDashboardSetting -Name MaxViewerLevel -Value 7   # 0 opens it to everyone again
```

**The top of the organization** decides who sees everything by level. Admin › Access › *Settings*, or leave it empty to use the one person with no manager who has at least half the directory under them:

```powershell
M -Task SetOrgTop -Upn 'ceo@contoso.com'
M -Task SetOrgTop -Clear
```

A new top takes effect at once: the org chart is rebuilt as it is saved.

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

**Who hears about what.** Admin › Alerts and digests › *Who hears about what*: pipeline failures and stale data, security signals, licenses, drops and spikes in use, and new agents, each to its own addresses (a distribution list keeps it short). Alerts also go to the Windows Application log under the source *Copilot Insights*.

**When to alert.** Admin › Alerts and digests › *When to alert*: how old the data may get, the daily thresholds for jailbreaks, prompt injections, blocked resources and sensitive labels, the spike factor, what counts as a drop or a spike in use, and when the license pool is running out. **Run the checks now** runs a round at once in the tenant chosen in the switcher, and with several tenants **Run the checks for every tenant** runs one in each tenant where you are an administrator (2.4.12), each recorded in that tenant's audit log; *Where the checks stand* shows what each last saw.

**Digests.** Admin › Alerts and digests › *Digests*: add one (report, recipients, daily, weekly or monthly, the hour, the period, and optionally one manager's organization); **Preview** it, **Send now**, **Pause** or **Remove** it. Digests carry aggregates only. One for a manager's organization carries that part alone (2.4.12): the Adoption digest lists the targets inside it, and the Licensing digest leaves out the tenant's license pool, which only the whole organization's digest has. Preview and Send now need the role of the section the digest shows (Adoption, Licensing or Security), and a preview is in the audit log (2.4.12). A digest at an hour the clock skips or repeats goes once (2.4.12).

**Manager digests.** Admin › Alerts and digests › *Manager digests*: *Weekly* or *Monthly* sends every manager with at least the given number of people under them the Leadership digest for their own organization, to their email address in AD, on the day and hour set here (not sent in privacy mode; with the dashboard limited to levels, only to managers within them). A send that fails is tried again an hour later, three times in all; one cut off by the site stopping goes when it starts again. **A manager who asks not to get it:** *Managers who do not get the digest*, enter their UPN and **Stop their digest**; **Send it again** undoes it.

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
| `PowerPlatformUserActivity` | The admin center's usage reports (Power Apps, Power Automate, Copilot Studio) and Power BI's activity | `UserPrincipalName`, `Product` (Power BI, Power Apps, Power Automate or Copilot Studio) | `LastActiveUtc`, `ActiveDays30` |
| `PowerPlatformCapacityDay` | The admin center's capacity report | `Day`, `CapacityType` (Database, File or Log) | `EntitledGb`, `UsedGb` |
| `PowerPlatformCreditDay` | The admin center's Copilot Credits and AI Builder consumption | `Day`, `EnvironmentName`, `Product` (Copilot Studio or AI Builder), `Billing` (Prepaid or PayAsYouGo) | `Credits`, `Cost`, `Currency` |

- **A load adds and replaces by the key**: a day or a flow loaded again replaces itself, nothing else changes, so a week or six months can be loaded and loaded again. `-Replace` makes the file the whole table, for the inventories that are a picture of now (the flows, who used what); the delete guard holds a file more than 15% smaller than the table (`CostImportMaxDropPercent`, `-AllowDrop` once).
- **Values** are read as the column's type whatever the machine's language: dates as yyyy-MM-dd, with or without a time; numbers with a point for decimals; True, False, Yes, No, 1 or 0. A blank is NULL. A row whose key cannot be read is left out and counted, and a key given twice keeps its last row. The tool names any heading the table has no column for.
- **Load them on a schedule** that matches the export: the capacity and credit days daily or weekly, the inventories when they are refreshed. The page reads each source's last 30 days, whatever day it ends.

**Prices and assumptions.** Admin › Targets and assumptions › *Costs and savings*: the price of each license and unit (`dash.CostPrice`, seeded with list prices in USD: put in what your agreement pays), the Fabric reservation discount (41% by default), the minutes a flow run and a Copilot in Power BI request save (3 each), and the days after which a seat, a license or an AI account counts as idle (30). A license is priced by its SKU part number, as Graph's license pool names it; one with no price is left out of the spend. The hourly cost the value is counted at is *Time saved*'s, and the Copilot license's price is Admin › Access's.

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

## Copilot reservations

**Leadership › Copilot reservations** (2.6.8) sets each line of business's reserved Microsoft 365 Copilot seats against the add-on licenses its people hold, and shows what that bills. Leaders see the organizations led from inside their part of the organization; the whole-organization view and the Licensing role see every organization, the reconciliation with the license pool and the purchase gate. The people behind each figure are on **Licensing › Reservation holders**, for the Licensing role. Administrators keep everything below on the page itself.

**The register.** One row per persistent chargeback organization: its **Org ID** (kept for good, so a reorganization does not move the billing), name, leader's sign-in, cost center, reserved seats, and the dates it is effective from and to. *Add an organization's reservation* adds one; *Add several at once* takes lines pasted from a spreadsheet, all checked before any is added:

```text
OrgId,Organization,Leader,Reserved,From,CostCenter,To
LOB-01,Retail Banking,leader.one@contoso.com,7403,2027-01-01,CC-100
LOB-EXEC,Executive Leadership,,17,2027-01-01,CC-001
```

A header line is skipped; the leader may be empty for a pool with no one under it; cost center and the end date are optional. To change a reservation (a true-up or an April true-down), give the old row an end date by adding it again with one, or remove it, and add a new row from the date the change applies: past month-end snapshots keep what they were. Every change is in the audit log.

**Who counts where.** An organization's people are everyone under its leader in the org chart, the leader included. A person under two listed leaders (one reporting to the other) counts with the nearer one, so nobody is in two organizations. Allocated licenses are the **Microsoft 365 Copilot add-on** held today; Copilot held only inside a suite (E7) and Technology-funded accounts are not counted. Add-on holders under no listed leader are shown as an exception, to map.

**The figures.** Reserved; Entra allocated; unused reserved capacity (MAX(reserved − allocated, 0) per organization); allocation over reservation (MAX(allocated − reserved, 0)); billable licenses (reserved plus allocation over reservation); utilization (allocated over reserved, N/A with nothing reserved); the monthly obligation (billable times the rate) and the remaining obligation (the months left in the year after this one). Each is worked out per organization, then summed. The billable rule is the policy's and the page says it awaits Finance's confirmation.

**The rate.** *Rate and purchase gate* › *Add a rate*: Finance's rate a license a month after any discount (18 for 30 less 40%), from the date it applies. Without one, obligations show *Pending Finance Decision*.

**The purchase gate.** The unassigned add-on licenses counted at the baseline (1,136 in the policy's example) and the share to assign first (95%): the page shows how many are assigned since, and whether a purchase may be considered (at 95% of 1,136, 1,079 assigned, leaving a buffer of 57). Empty: no gate.

**The Technology-funded register.** Approved executive, service, test, shared, migration and exception accounts: sign-in, category, accountable owner, approval reference, from and review dates. Their licenses are counted once in the Technology pool, never in a line of business. Accounts the dashboard reads as non-human that are not in the register are listed as an exception; one past its review date too.

**Snapshots.** Each night after the licenses are read, the pipeline keeps the last month-end's position once (`dash.usp_TakeReservationSnapshot`), and never rewrites it: the reserved seats, the allocated licenses on that day and the rate in force, for every organization, those under no leader and the Technology pool. The org chart used is the one on the night it was kept. *Month-end snapshots* lists them; *View* shows one as it was. Every snapshot is **Operational, not certified**: certification, adjustments and the December close come in a later release.

## Classifications, events and extracts

**Turn a report off, or back on.** Admin › Reports shown lists every report by section, laid out as the start page is, each with a box. Untick the ones your organization should not have and choose **Save**; a section's own box ticks or clears all of it. A report that is off is gone from the navigation and the start page and is not found at its address (404), for everyone, administrators included; its exports and forms are refused too. Tick it and save to bring it back: nothing is deleted. Roles and levels still decide who sees a report that is on. Admin pages cannot be turned off. Links to a report that is off stop working, so turn a detail page (Agent detail, App detail, Person record) off together with the lists that open it. Scheduled digests and extracts, the board pack and the data API are set on their own pages and are not changed here. Each change is in the audit log. The choice is kept in `dash.Setting`, one row a report, named `Hide:` and its path.

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

## Every Admin page and maintenance task

<!-- Kept complete by tests/Test-OperationsGuide.ps1: every Admin page in dashboard/CopilotInsights/Pages/Nav.cs and every -Task of tools/Invoke-CopilotMaintenance.ps1 must appear in this section. -->

| Admin page | What it is for |
|---|---|
| Admin › Health | Report speed (when the report pages were last built ahead on this web server, the last morning pre-cache and its slowest page from memory, and how the cache works), pipeline runs and each step's freshness, directory coverage, the latest day with data, each certificate the pipeline signs in with (the Purview audit app's, the SharePoint agent workbook app's and the Foundry usage app's where it has its own) and its details, the audit searches waiting and what the last load took, the last run of the optional GitHub Copilot and Foundry usage loads (2.6.2), and a manual refresh |
| Admin › Access | Who can open the dashboard (everyone, or levels 1 to N), the levels never on revoke or train lists, the top of the organization, the access settings and license price, granting and removing roles, limiting an administrator to some Admin pages (2.6.4), checking someone's access |
| Admin › Directory quality | People without a manager, department, title, email or SID; managers missing or disabled; people outside the org chart; each listed and downloadable |
| Admin › Email | How email is sent, a test message, where the logs are with the last test's conversation with the mail server (2.6.2), and everything sent or not, with the conversation of each test and each message not sent |
| Admin › Alerts and digests | Alert recipients and thresholds, digests, manager digests and the managers who do not get them, the webhook, running the checks now |
| Admin › Targets and assumptions | Adoption targets, minutes saved and an hour's worth, the licensing group, champions per department, the rollout wave size, and the prices and assumptions of Leadership › Costs and savings (2.6.6) |
| Admin › Privacy and retention | Privacy mode, retention of logs and JSON, the data API |
| Admin › Reference data | Agent names and owners, sensitivity label names and order, app categories, which accounts are not a person (non-human accounts) |
| Admin › Value names | Names for every cataloged code, one by one or by CSV, with the mapping file to download |
| Admin › Classifications and events | Job-family rules, use-case rules, and the events marked on trend charts |
| Admin › Reports shown | Which reports the dashboard has: a box to tick for each, by section. A report turned off is gone for everyone, administrators too |
| Admin › Scheduled extracts | Aggregate CSV files written to a folder or share on a schedule |
| Admin › Training records | Training attendance, by CSV, with the mapping file to download |
| Admin › Audit log | Every view, refusal, export, API call and admin change |

| `-Task` of `Invoke-CopilotMaintenance.ps1` | What it does |
|---|---|
| `Status` | What is loaded and built, and when each step last ran |
| `Reflatten` | Flatten every record again, then rebuild sessions, `bi` and the dashboard |
| `RebuildSessions` | Rebuild every session, then bring `bi` and the dashboard up to date |
| `RebuildBi` | Rebuild `bi` in full, then the dashboard |
| `RefreshDashboard` | The dashboard's org chart, and its rollups from `-SinceUtc` (every day when omitted) |
| `SetAppCategory` | Put `-App` in `-Category` (Copilot Chat or In-App Copilot); `-Apply` rebuilds now |
| `GrantRole` | Give `-Principal` (UPN, `DOMAIN\user` or `DOMAIN\Group`) a `-Role` |
| `RevokeRole` | Take it away |
| `SetOrgTop` | Make `-Upn` the top of the organization, or `-Clear` |
| `SetDashboardSetting` | Set MinGroupSize, DefaultDays, InactiveDays, LicenseMonthlyCost, LicenseCurrency, PaygInteractionCost, MaxViewerLevel (who can open the dashboard: 0 everyone, else levels 1 to N) or ProtectedLevels (levels 1 to N never on a revoke or train list: 5 by default, 0 none) to `-Value` |
| `NameAgent` | Give agent `-AgentId` a `-FriendlyName` and `-Owner` |
| `NameLabel` | Give label `-LabelId` a `-FriendlyName` and `-Rank` |
| `PurgeJson` | Clear the stored JSON of records older than `-RetainDays` (`-Apply` to do it), or with `-DeleteRows` remove those records and the rows made from them (`bi` and the dashboard keep theirs until `RebuildBi`) |
| `RebuildValueCatalog` | List every cataloged column's values again, from every flattened record (the installer does it once when upgrading) |
| `ListValues` | The cataloged columns, or one column's values and names (`-ColumnId`, `-Path` for a CSV) |
| `ImportValueNames` | Names for cataloged values from `-Path` |
