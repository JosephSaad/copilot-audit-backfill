# Operations guide

What to do once copilot-audit-backfill and the Copilot Insights dashboard are installed: every task an operator performs, where to do it, and the command for it. [DEPLOYMENT.md](DEPLOYMENT.md) covers installing; [DASHBOARD.md](DASHBOARD.md) explains the dashboard's pages and rules; [RUNBOOK.md](../RUNBOOK.md) is the pipeline's reference, with limits, tuning and every error message.

Two places do everything:

- **The dashboard's Admin pages**, for people holding the Admin role. Every change is checked, needs the page's anti-forgery token, and is written to the audit log.
- **`tools\Invoke-CopilotMaintenance.ps1`** on the job server, for the same tasks without the dashboard, in scripts, or when nobody can sign in. Below, `M` stands for `.\tools\Invoke-CopilotMaintenance.ps1 -SqlServer SQLPROD01` (add `-Database` if it is not `CopilotAudit`, and `-TrustServerCertificate` for a self-signed SQL Server certificate).

Nothing here needs SQL typed by hand. `tests/Test-OperationsGuide.ps1` fails the build when an Admin page or a maintenance task is not in this guide, so the [reference](#every-admin-page-and-maintenance-task) at the end is complete.

## Contents

- [Every day](#every-day)
- [Access and permissions](#access-and-permissions)
- [Names for codes and ids](#names-for-codes-and-ids)
- [Email, alerts and digests](#email-alerts-and-digests)
- [Targets, value and training](#targets-value-and-training)
- [Classifications, events and extracts](#classifications-events-and-extracts)
- [Acting on the license recommendations](#acting-on-the-license-recommendations)
- [Triage and investigations](#triage-and-investigations)
- [Privacy, retention and the data API](#privacy-retention-and-the-data-api)
- [The pipeline](#the-pipeline)
- [More than one tenant](#more-than-one-tenant)
- [Upgrades and renewals](#upgrades-and-renewals)
- [Every Admin page and maintenance task](#every-admin-page-and-maintenance-task)

## Every day

Nothing needs doing while it is healthy: the nightly task loads and builds everything, and the dashboard raises an alert when something goes wrong. To look:

| Check | Where | Healthy |
|---|---|---|
| Last night's run | **Admin › Health**, *Pipeline runs*; or `M -Task Status` | *Succeeded*, finished within the last day |
| Data freshness | Every page shows a yellow banner when the data is older than *FreshnessHours* (36) or the last run failed | No banner |
| Alerts sent, and any that could not be | **Admin › Email**, *Recently sent* | *Sent* |
| Directory coverage | **Admin › Health**, *Directory and security model* | 95% or more of people matched to AD and placed under the top |
| The app certificate and audit searches | **Admin › Health**, *Certificate and audit searches* | Certificate more than 30 days from expiry; no failed searches, few waiting |
| Gaps in AD | **Admin › Directory quality** | Few people without a manager, department, title or email |

When the last run failed, its step and last log lines are on Admin › Health and in the pipeline alert; the full log is `logs\nightly-<date>.log` on the job server. The next night's run resumes it; to resume now, run `.\Invoke-CopilotPipeline.ps1` on the job server. [RUNBOOK.md](../RUNBOOK.md) lists every error and its fix.

## Access and permissions

**How access works.** Everyone signs in with their Windows account. The top of the organization, the people who report directly to them and anyone granted **Organization** see the whole organization; everyone else sees themselves and the people under them. Pages that name people also need their persona's role. **Admin** runs the Admin pages and sees no usage data by itself. [DASHBOARD.md](DASHBOARD.md#who-sees-what) has the full rules.

| Role | Gives |
|---|---|
| Admin | The Admin pages |
| Organization | The whole organization's aggregates, whatever the person's level |
| Adoption | Named people licensed but not started |
| Licensing | Named unused licenses, Copilot Chat users, licenses on disabled accounts, upgrade/train/revoke lists, the use ranking, rollout waves, license action outcomes, and their export files |
| Security | Named flagged interactions and their triage; named guests |
| Compliance | Named people who worked with labeled content; one person's full record (works in privacy mode) |
| Agents | Named users of an agent |
| Enablement | Named champions and engagement segments |

**Grant or remove a role.** Admin › Access › *Grant a role*: a person (their UPN, or `DOMAIN\user`) or an AD group (`DOMAIN\Group`), the roles (tick as many as they need), and why. Grant to groups where you can: membership is read from each sign-in, so it follows AD with no change here. The grants list has one row per person or group with all their roles, and a *Remove* button beside each role. The Adoption, Licensing (with Forecasting), Security, Compliance, Agents and Enablement sections are hidden from anyone without their role, whole-organization viewers included, so grant executives the roles whose sections they should see.

```powershell
M -Task GrantRole -Principal 'USERS\Copilot-Insights-Licensing' -PrincipalType Group -Role Licensing -Note 'licensing team'
M -Task RevokeRole -Principal 'ada.lovelace@contoso.com' -Role Licensing
```

**Check what someone sees.** Admin › Access › *Check someone's access*: their level, their scope, whether the level limit keeps them out, and the roles granted to their account directly (group grants are resolved when they sign in).

**Keep leaders off revoke and train lists (2.4.6).** Admin › Access › *Never on revoke or train lists*: levels 1 to N of the organization (5 by default; *None* lists anyone), each choice with how many people it protects. People at those levels are never on Upgrade, train or revoke (train and revoke), the Swap list (take side), Unused licenses, Better on pay-as-you-go, Fading use, Not started, Enablement's one-off and not-started people, or License optimization's counts, nor in their CSVs, removal files, the Licensing digest's counts, the Board pack, Budget and renewal, or the data API. Each of those lists says so at the top. Lists of accounts nobody can use keep everyone: Licenses on disabled accounts, Departures ahead and Copilot on non-human accounts. From the job server: `M -Task SetDashboardSetting -Name ProtectedLevels -Value 5` (0 for none).

**Limit the dashboard to the top levels (2.4.4).** Admin › Access › *Who can open the dashboard*: *Everyone in the directory*, or *Levels 1 to N* below the top of the organization, each choice with how many people it lets in. Anyone granted a role other than Admin is let in whatever their level, so grant a role to an analyst who needs it. Everyone else is refused every page, export and API call. Administrators outside the levels keep the Admin pages, so a wrong choice can be undone here. Managers outside the levels stop getting the manager digest. It applies within a minute. The same, from the job server:

```powershell
M -Task SetDashboardSetting -Name MaxViewerLevel -Value 7   # 0 opens it to everyone again
```

**The top of the organization** decides who sees everything by level. Admin › Access › *Settings*, or leave it empty to use the one person with no manager who has at least half the directory under them:

```powershell
M -Task SetOrgTop -Upn 'ceo@contoso.com'
M -Task SetOrgTop -Clear
```

A new top takes effect at once: the org chart is rebuilt as it is saved.

**The first administrator, or a way back in.** The site's configuration names bootstrap administrators, who cannot be removed from the Admin pages: run the IIS installer with `-AdminPrincipal`, or edit `Insights:BootstrapAdmins` in `appsettings.Production.json` and recycle the app pool. From the job server, `M -Task GrantRole -Principal 'USERS\Copilot-Insights-Admins' -PrincipalType Group -Role Admin`.

**The other access settings** (Admin › Access › *Settings*): the minimum group size (no breakdown shows a group smaller than this), the default period, the days after which a license counts as unused, and the license's monthly price and currency.

```powershell
M -Task SetDashboardSetting -Name MinGroupSize -Value 10
M -Task SetDashboardSetting -Name LicenseMonthlyCost -Value 30
```

**An account the directory cannot match.** Sign-ins are matched to people by the account's SID, which the nightly directory load copies from AD. An account with no match (a local or Entra-joined account) is "not in the directory" and sees nothing unless granted Organization. To map one, add it to `Insights:IdentityMap` in `appsettings.Production.json` (`"CONTOSO\\svc-report": "report.owner@contoso.com"`) and recycle the app pool. A mapping cannot land an account on a person whose directory record carries a different SID, and nothing in a URL changes who is signed in.

**Power BI.** The ready-made model in `powerbi\CopilotInsights` has an *Organization* role that follows the same org chart through `bi.OrgSecurity` ([DEPLOYMENT.md](DEPLOYMENT.md), step 15): add the report's viewers to it in the Power BI service. Organization granted to an AD group reaches Power BI after the next nightly directory load, which expands the group into its members (nested groups included) in `audit.GroupMember`; the load's log names any granted group it cannot find in the target domain. In privacy mode `bi.OrgSecurity` is empty (it would pair real sign-ins with the pseudonyms), so share `bi.DepartmentWeek` rather than the person-level model.

## Names for codes and ids

The audit log records many things as codes. Give them names once and the dashboard's pages and Power BI (`bi.ValueNames`) show the names.

**Any cataloged code** (tenant, app, agent and platform ids, license types, models, Fabric capacities, resource types and statuses, labels): **Admin › Value names**. Pick a column, then name its values one at a time (*Unnamed first* puts the work at the top). Or work in a spreadsheet: **Download the mapping file** (every value of every column, with its name and note, in one CSV; or one column's from that column's view), fill in or change the *Name* column, and **Upload** it. An empty name removes one. A column with 10,000 or more distinct values is marked *too many to name*.

```powershell
M -Task ListValues                                  # the columns, and how many values each has
M -Task ListValues -ColumnId 16 -Path models.csv   # one column's values and names, to fill in
M -Task ImportValueNames -Path models.csv           # read the names back
M -Task RebuildValueCatalog                         # every value again, from every flattened record
```

**Non-human accounts (2.4.5).** A Copilot license on an account that is not a person is a policy breach. Admin › Reference data › *Non-human accounts* sets which accounts count: guests; sign-ins and names that read like a service, shared, test or room account; accounts with no manager outside the org chart; more sign-in patterns of your own (`bot-*`, `*-svc`); employee types that are not people (`SERVICE`, `RESOURCE`); and AD groups of service accounts (`DOMAIN\Group`, whose members the next nightly directory load reads). The breaches are on Security, Compliance and Licensing › *Copilot on non-human accounts*, with an Entra bulk remove file and a PowerShell script to remove every one, recorded for License action outcomes. The first time an account is found, the Security and Licensing alert recipients get an alert (and the webhook and the event log, ID 2002); turn it off on the same panel. **An account that is a person after all:** on the breach page, an administrator enters why and clicks **Confirm a person**; it moves to *Confirmed as people* with who confirmed it and when, raises no alert, and **Flag it again** undoes it. Each change is in the audit log.

**Sensitivity labels.** Admin › Reference data › *Sensitivity labels*: a name and an order (0 least sensitive) for each label id. The order drives "most sensitive label" and the label threshold for security alerts. `Get-Label` in Security & Compliance PowerShell lists your labels and their ids. Label names set in Value names show here too, and the other way round.

```powershell
M -Task NameLabel -LabelId 5e1f0c2a-0000-4000-8000-000000000002 -FriendlyName 'Highly Confidential' -Rank 4
```

**Agents.** Admin › Reference data › *Agents*: a friendly name and an owner for each agent (Copilot Studio agents appear under their schema name). *Unnamed first* lists new ones; the new-agent alert and Agents › Agent inventory point here.

```powershell
M -Task NameAgent -AgentId 'cr123_hrHelper' -FriendlyName 'HR helper' -Owner 'hr-digital@contoso.com'
```

**Which apps count as Copilot Chat.** Admin › Reference data › *App categories*, or `M -Task SetAppCategory -App 'Copilot Notebooks' -Category 'Copilot Chat' -Apply` (`-Apply` rebuilds sessions and `bi` with it now instead of at the next load). An app not set counts as In-App Copilot.

## Email, alerts and digests

**Set up sending.** Admin › Email: Exchange Online (the mailbox's password, or OAuth with an app holding `SMTP.SendAsApp`), or any SMTP server on any port with its user name and password. **Send a test** after any change. Passwords and secrets are stored encrypted and never shown again; leave the field empty to keep the stored one, or tick *Remove the stored password and secret*. [DASHBOARD.md](DASHBOARD.md#email-alerts-and-digests) explains each mode, and when Exchange Online stops taking passwords.

**Who hears about what.** Admin › Alerts and digests › *Who hears about what*: pipeline failures and stale data, security signals, licenses, drops and spikes in use, and new agents, each to its own addresses (a distribution list keeps it short). Alerts also go to the Windows Application log under the source *Copilot Insights*.

**When to alert.** Admin › Alerts and digests › *When to alert*: how old the data may get, the daily thresholds for jailbreaks, prompt injections, blocked resources and sensitive labels, the spike factor, what counts as a drop or a spike in use, and when the license pool is running out. **Run the checks now** runs a round at once in the tenant chosen in the switcher, and with several tenants **Run the checks for every tenant** runs one in each; *Where the checks stand* shows what each last saw.

**Digests.** Admin › Alerts and digests › *Digests*: add one (report, recipients, daily, weekly or monthly, the hour, the period, and optionally one manager's organization); **Preview** it, **Send now**, **Pause** or **Remove** it. Digests carry aggregates only.

**Manager digests.** Admin › Alerts and digests › *Manager digests*: *Weekly* or *Monthly* sends every manager with at least the given number of people under them the Leadership digest for their own organization, to their email address in AD, on the day and hour set here (not sent in privacy mode; with the dashboard limited to levels, only to managers within them). A send that fails is tried again an hour later, three times in all. **A manager who asks not to get it:** *Managers who do not get the digest*, enter their UPN and **Stop their digest**; **Send it again** undoes it.

**Teams, ServiceNow or any webhook.** Admin › Alerts and digests › *Teams, ServiceNow or any webhook*: an https address (stored encrypted) and a format. *Teams* posts an adaptive card (use a Teams Workflows "post to a channel when a webhook request is received" address, or an incoming webhook); *Json* posts `{source, kind, subject, lines, link, atUtc}` for ServiceNow, Splunk or anything else. **Post a test alert** checks it. Every alert then goes to the webhook as well as email.

**The app certificate.** The pipeline records its certificate at the start of each run; an alert goes to the pipeline recipients at *Warn about the app certificate* days before it expires (30 by default), again at 14 and 3 days, and when it has expired.

**Nothing arrives.** Admin › Email › *Recently sent* gives the reason for each message not sent: email off, no recipients, or the server's own answer (a refused password, a certificate that did not validate, a closed connection).

## Targets, value and training

**Adoption targets.** Admin › Targets and assumptions › *Adoption targets*: for the whole organization or one manager's, the measure (adoption, share of people active, or people active), the target, the start and the due date. Progress is on Adoption › Targets and in the Adoption digest.

**Time saved.** Admin › Targets and assumptions › *Time saved*: the default minutes one interaction saves, what an hour is worth, and the minutes per app. Leadership › Value and time saved and the Value digest follow them.

**Training attendance.** Admin › Training records: upload a CSV of UPN, session and date (`yyyy-MM-dd`). **Download the mapping file** for every record held, in the same columns, to add to and upload back, or an empty template. Lines already there are skipped and bad lines listed. Remove a session with its *Remove* button. Enablement › Training effect shows each session four weeks after it.

**Champions.** How many to suggest per department: Admin › Targets and assumptions › *Licensing and champions*. The list is Enablement › Champions, for the Enablement role.

**Contracts.** The Copilot agreements (seats, price per seat, start and renewal dates) are entered by an administrator on **Forecasting › Budget and renewal** itself (*Add a contract*, and *Remove* on each). The page then shows the cost and how many seats the use supports at renewal.

## Classifications, events and extracts

**Turn a report off, or back on.** Admin › Reports shown lists every report by section, laid out as the start page is, each with a box. Untick the ones your organization should not have and choose **Save**; a section's own box ticks or clears all of it. A report that is off is gone from the navigation and the start page and is not found at its address (404), for everyone, administrators included; its exports and forms are refused too. Tick it and save to bring it back: nothing is deleted. Roles and levels still decide who sees a report that is on. Admin pages cannot be turned off. Links to a report that is off stop working, so turn a detail page (Agent detail, App detail, Person record) off together with the lists that open it. Scheduled digests and extracts, the board pack and the data API are set on their own pages and are not changed here. Each change is in the audit log. The choice is kept in `dash.Setting`, one row a report, named `Hide:` and its path.

**Job families.** Admin › Classifications and events › *Job families*: a job-title pattern (`%engineer%` is any title containing engineer), the family, and a priority (lower first). *The most common titles no rule covers* shows what is left. People › Job families and Power BI's `JobFamilies` table follow the rules at once.

**Use cases.** Admin › Classifications and events › *Use cases*: rules from the app (a pattern), whether the interaction searched the web, and whether an agent answered, to a use case. The defaults cover the Microsoft 365 apps; **Put back the default rules** restores them. Leadership › Use cases and Power BI's `UseCases` table follow them.

**Events on the trends.** Admin › Classifications and events › *Events on the trend charts*: a day, a name and a kind (rollout, training, outage, policy, other). Each shows on every trend chart as a dashed line with its name, and in Power BI's `EventMarkers`.

**Scheduled extracts.** Admin › Scheduled extracts: a name, the data (department by week, daily totals, apps by week, the license pool, agents by week: aggregates only), a folder or share, how often and when, and how many days back. The site's app-pool account (for a share, the web server's computer account) needs write access to the folder. **Write now** writes one at once; *Last written* and *Result* show how the last one went.

## Acting on the license recommendations

1. **Forecasting › License optimization** shows the recommendations and what they are worth; **Forecasting › Upgrade, train or revoke** and **Licensing › Licenses on disabled accounts** name the people (Licensing role). *Train* and *revoke* look at Premium use: Copilot in Word, Excel, PowerPoint, Outlook or Teams, or Copilot Chat over work content. Someone who only uses web Copilot Chat, which the free tier gives, is not using their license. **Licensing › Use ranking** ranks everyone by general, free and Premium use. **Licensing › Swap list** pairs the revoke and upgrade lists within a department (or division, or anywhere), so an idle license goes to someone who wants one instead of a new seat being bought; its two files take the licenses back and give them on, in that order.
2. On those pages, download the **Entra bulk add or remove members file**, or the **PowerShell script**. If Copilot licenses are assigned through a group, set its object id first in Admin › Targets and assumptions › *Licensing and champions*, so both use the group; otherwise the script assigns or removes the SKU directly.
3. A license administrator applies it: the bulk file in Entra (Groups › the license group › Bulk operations), or the script in PowerShell with their own rights (`-WhatIf` first). The dashboard changes nothing in Entra.
4. The next nightly run picks the change up; the lists update the day after.
5. **Forecasting › License action outcomes** shows every export, whether it has been applied, the licenses reclaimed and whether new licenses are being used; *Not applied yet* lists who is still waiting.

**Rolling out in waves.** Forecasting › Rollout waves orders everyone without a license (the heaviest free Copilot Chat users first, then those in departments where licensed people use it most) and splits them into waves; each wave has its own bulk file and script. The default wave size is in Admin › Targets and assumptions.

## Triage and investigations

**Flagged interactions.** Security › Flagged interactions (Security role) lists every jailbreak, prompt-injection and blocked-resource flag with a triage status: *New*, *Investigating*, *Closed* or *False positive*, and a note. Saving one records you as its owner. *Open* (new or investigating) is the default view; the count is at the top.

**One person's record.** Compliance › Person record (Compliance role; also in privacy mode): a person's directory and license record, and every interaction in the period with its record id, downloadable. Use it for a data-subject request or an investigation; the prompt and response text is in Purview, found by the record ids. Every look and download is in the audit log with whose record it was.

## Privacy, retention and the data API

**Privacy mode.** Admin › Privacy and retention › *Privacy mode*: *Pseudonymize* refuses every page, export, digest and API call that names people, and Power BI's `bi.Users` shows a salted hash instead of names (from its next refresh). *Off* restores both.

**Retention.** Admin › Privacy and retention › *Retention*: days to keep the dashboard's audit log and the notification log, and when to clear the stored JSON of old records (0 keeps it; at least 200 days). Applied each night after a successful run; **Apply retention now** does it at once. To clear JSON from the job server instead, `M -Task PurgeJson -RetainDays 365` reports what would go and `-Apply` clears it (`-DeleteRows` removes those records altogether).

**The data API.** Admin › Privacy and retention › *Data API* turns `/api/v1` on or off. Callers need the same access as the pages; [DASHBOARD.md](DASHBOARD.md#the-data-api) lists the endpoints.

**Who did what.** Admin › Audit log: every page view, refusal, export, API call and admin change, with who, when, the scope and the filters, searchable and exportable.

## The pipeline

**The nightly task** (`Copilot audit - nightly`, 02:00 by default) runs `Invoke-CopilotPipeline.ps1`: the directory, the licenses, the license events and the Copilot interactions, then retention. It records each run and step in `audit.PipelineRun`. To run it now, or to resume after a failure, run `.\Invoke-CopilotPipeline.ps1` on the job server. To move it, run `tools\Install-CopilotJobServer.ps1 -At 03:30`.

**Refresh or rebuild by hand**, after a change that the nightly run would not pick up:

| After | Do this |
|---|---|
| A change to the directory, the top of the organization or the app categories, wanted on the dashboard now | Admin › Health › *Refresh the last 7 days*, or `M -Task RefreshDashboard` (every day) or `-SinceUtc 2026-10-01` |
| A session setting changes (timeout, time zone, license databases) | `M -Task RebuildSessions` |
| License history arrives for a period already built, or flattened rows are purged | `M -Task RebuildBi`, then a full refresh of the Power BI model |
| A release changes the flattening | `M -Task Reflatten` (every record flattened again, then sessions, `bi` and the dashboard) |
| The flatten says a field *now carries information* | Nothing breaks; see `audit.RawLogRecordShape` and [RUNBOOK.md](../RUNBOOK.md) |

`M -Task Status` shows what is loaded and built, and when each step last ran.

## More than one tenant

A group with several Microsoft 365 tenants loads each into its own pair of databases, with its own app registration, and one dashboard spans them.

1. On the job server, as an administrator: `.\tools\Add-CopilotTenant.ps1 -Name Fabrikam -TenantId fabrikam.onmicrosoft.com -TargetDomain fabrikam.local`. It registers the app in that tenant with the job server's certificate, creates and installs the tenant's databases, and adds it to the settings.
2. Load its history once: `.\Invoke-CopilotPipeline.ps1 -History -Tenant Fabrikam`. From then on the nightly task loads every tenant in turn; `-Tenant` runs one.
3. On the web server, run the dashboard installer again with every tenant: `-Tenant 'Contoso=CopilotAudit','Fabrikam=CopilotAudit_Fabrikam' -GrantDatabaseAccess`. The dashboard shows a tenant switcher and **Leadership › Group overview**.

Each tenant's database decides who sees what in it: a person sees a tenant only where its directory has them or they are granted a role there. The Admin pages act on the tenant chosen in the switcher; Admin › Alerts and digests can also run the checks in every tenant at once, as the background checks do.

## Upgrades and renewals

**A new release.** Back up both databases, replace the scripts on the job server with the new release's (keep `copilot-audit.settings.psd1` and `logs`), run `.\Install-CopilotAudit.ps1 -SqlServer SQLPROD01 -Dashboard` once, and read the release notes' upgrade section for any one-off step. The installer does the one-off work itself: for 2.3.0 it catalogs the values of records flattened before, and rebuilds the dashboard's rollups once with Premium use. For the dashboard, run `dashboard\Install-CopilotInsights.ps1` again with the new `copilot-insights` zip.

**The app's certificate.** Within 30 days of expiry, run `.\tools\Install-CopilotJobServer.ps1 -RegisterApp` on the job server: a new certificate, its private key for the gMSA, uploaded to the app next to the old one, and its thumbprint in the settings. Remove the old certificate from the app after the next good run.

**Exchange Online.** If the dashboard sends with a mailbox password, move it to OAuth (Admin › Email) before Microsoft turns SMTP AUTH passwords off for your tenant.

## Every Admin page and maintenance task

<!-- Kept complete by tests/Test-OperationsGuide.ps1: every Admin page in dashboard/CopilotInsights/Pages/Nav.cs and every -Task of tools/Invoke-CopilotMaintenance.ps1 must appear in this section. -->

| Admin page | What it is for |
|---|---|
| Admin › Health | Report speed (when the report pages were last built ahead), pipeline runs and each step's freshness, directory coverage, the latest day with data, the app certificate, audit searches waiting, and a manual refresh |
| Admin › Access | Who can open the dashboard (everyone, or levels 1 to N), the levels never on revoke or train lists, the top of the organization, the access settings and license price, granting and removing roles, checking someone's access |
| Admin › Directory quality | People without a manager, department, title, email or SID; managers missing or disabled; people outside the org chart; each listed and downloadable |
| Admin › Email | How email is sent, a test message, and everything sent or not |
| Admin › Alerts and digests | Alert recipients and thresholds, digests, manager digests and the managers who do not get them, the webhook, running the checks now |
| Admin › Targets and assumptions | Adoption targets, minutes saved and an hour's worth, the licensing group, champions per department, the rollout wave size |
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
| `PurgeJson` | Clear the stored JSON of records older than `-RetainDays` (`-Apply` to do it) |
| `RebuildValueCatalog` | List every cataloged column's values again, from every flattened record (the installer does it once when upgrading) |
| `ListValues` | The cataloged columns, or one column's values and names (`-ColumnId`, `-Path` for a CSV) |
| `ImportValueNames` | Names for cataloged values from `-Path` |
