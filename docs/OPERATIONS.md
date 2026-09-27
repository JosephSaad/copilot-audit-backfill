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
- [Acting on the licence recommendations](#acting-on-the-licence-recommendations)
- [Privacy, retention and the data API](#privacy-retention-and-the-data-api)
- [The pipeline](#the-pipeline)
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

When the last run failed, its step and last log lines are on Admin › Health and in the pipeline alert; the full log is `logs\nightly-<date>.log` on the job server. The next night's run resumes it; to resume now, run `.\Invoke-CopilotPipeline.ps1` on the job server. [RUNBOOK.md](../RUNBOOK.md) lists every error and its fix.

## Access and permissions

**How access works.** Everyone signs in with their Windows account. The top of the organisation, the people who report directly to them and anyone granted **Organization** see the whole organisation; everyone else sees themselves and the people under them. Pages that name people also need their persona's role. **Admin** runs the Admin pages and sees no usage data by itself. [DASHBOARD.md](DASHBOARD.md#who-sees-what) has the full rules.

| Role | Gives |
|---|---|
| Admin | The Admin pages |
| Organization | The whole organisation's aggregates, whatever the person's level |
| Adoption | Named people licensed but not started |
| Licensing | Named unused licences, Copilot Chat users, licences on disabled accounts, upgrade/train/revoke lists and their export files |
| Security | Named flagged interactions |
| Compliance | Named people who worked with labelled content |
| Agents | Named users of an agent |
| Enablement | Named champions and engagement segments |

**Grant or remove a role.** Admin › Access › *Grant a role*: a person (their UPN, or `DOMAIN\user`) or an AD group (`DOMAIN\Group`), the role, and why. Grant to groups where you can: membership is read from each sign-in, so it follows AD with no change here. The grants list has a *Remove* button on each.

```powershell
M -Task GrantRole -Principal 'USERS\Copilot-Insights-Licensing' -PrincipalType Group -Role Licensing -Note 'licensing team'
M -Task RevokeRole -Principal 'ada.lovelace@contoso.com' -Role Licensing
```

**Check what someone sees.** Admin › Access › *Check someone's access*: their level, their scope, and the roles granted to their account directly (group grants are resolved when they sign in).

**The top of the organisation** decides who sees everything by level. Admin › Access › *Settings*, or leave it empty to use the one person with no manager who has at least half the directory under them:

```powershell
M -Task SetOrgTop -Upn 'ceo@contoso.com'
M -Task SetOrgTop -Clear
```

A new top takes effect at once: the org chart is rebuilt as it is saved.

**The first administrator, or a way back in.** The site's configuration names bootstrap administrators, who cannot be removed from the Admin pages: run the IIS installer with `-AdminPrincipal`, or edit `Insights:BootstrapAdmins` in `appsettings.Production.json` and recycle the app pool. From the job server, `M -Task GrantRole -Principal 'USERS\Copilot-Insights-Admins' -PrincipalType Group -Role Admin`.

**The other access settings** (Admin › Access › *Settings*): the minimum group size (no breakdown shows a group smaller than this), the default period, the days after which a licence counts as unused, and the licence's monthly price and currency.

```powershell
M -Task SetDashboardSetting -Name MinGroupSize -Value 10
M -Task SetDashboardSetting -Name LicenceMonthlyCost -Value 30
```

**An account the directory cannot match.** Sign-ins are matched to people by the account's SID, which the nightly directory load copies from AD. An account with no match (a local or Entra-joined account) is "not in the directory" and sees nothing unless granted Organization. To map one, add it to `Insights:IdentityMap` in `appsettings.Production.json` (`"CONTOSO\\svc-report": "report.owner@contoso.com"`) and recycle the app pool. A mapping cannot land an account on a person whose directory record carries a different SID, and nothing in a URL changes who is signed in.

**Power BI.** With the dashboard installed, the model's row-level security follows the same org chart through `bi.OrgSecurity` ([DEPLOYMENT.md](DEPLOYMENT.md), step 15). Grant Organization to people directly, not through a group, for it to reach Power BI.

## Names for codes and ids

The audit log records many things as codes. Give them names once and the dashboard's pages and Power BI (`bi.ValueNames`) show the names.

**Any catalogued code** (tenant, app, agent and platform ids, licence types, models, Fabric capacities, resource types and statuses, labels): **Admin › Value names**. Pick a column, then name its values one at a time (*Unnamed first* puts the work at the top), or **Download** the column as a CSV, fill in the *Name* column, and **Upload** it. An empty name removes one. A column with 10,000 or more distinct values is marked *too many to name*.

```powershell
M -Task ListValues                                  # the columns, and how many values each has
M -Task ListValues -ColumnId 16 -Path models.csv   # one column's values and names, to fill in
M -Task ImportValueNames -Path models.csv           # read the names back
M -Task RebuildValueCatalog                         # once after upgrading to 2.3.0, for data flattened before it
```

**Sensitivity labels.** Admin › Reference data › *Sensitivity labels*: a name and an order (0 least sensitive) for each label id. The order drives "most sensitive label" and the label threshold for security alerts. `Get-Label` in Security & Compliance PowerShell lists your labels and their ids. Label names set in Value names show here too, and the other way round.

```powershell
M -Task NameLabel -LabelId 5e1f0c2a-0000-4000-8000-000000000002 -FriendlyName 'Highly Confidential' -Rank 4
```

**Agents.** Admin › Reference data › *Agents*: a friendly name and an owner for each agent (Copilot Studio agents appear under their schema name). *Unnamed first* lists new ones; the new-agent alert and Agents › Inventory point here.

```powershell
M -Task NameAgent -AgentId 'cr123_hrHelper' -FriendlyName 'HR helper' -Owner 'hr-digital@contoso.com'
```

**Which apps count as Copilot Chat.** Admin › Reference data › *App categories*, or `M -Task SetAppCategory -App 'Copilot Notebooks' -Category 'Copilot Chat' -Apply` (`-Apply` rebuilds sessions and `bi` with it now instead of at the next load). An app not set counts as In-App Copilot.

## Email, alerts and digests

**Set up sending.** Admin › Email: Exchange Online (the mailbox's password, or OAuth with an app holding `SMTP.SendAsApp`), or any SMTP server on any port with its user name and password. **Send a test** after any change. Passwords and secrets are stored encrypted and never shown again; leave the field empty to keep the stored one, or tick *Remove the stored password and secret*. [DASHBOARD.md](DASHBOARD.md#email-alerts-and-digests) explains each mode, and when Exchange Online stops taking passwords.

**Who hears about what.** Admin › Alerts and digests › *Who hears about what*: pipeline failures and stale data, security signals, licences, drops and spikes in use, and new agents, each to its own addresses (a distribution list keeps it short). Alerts also go to the Windows Application log under the source *Copilot Insights*.

**When to alert.** Admin › Alerts and digests › *When to alert*: how old the data may get, the daily thresholds for jailbreaks, prompt injections, blocked resources and sensitive labels, the spike factor, what counts as a drop or a spike in use, and when the licence pool is running out. **Run the checks now** runs a round at once; *Where the checks stand* shows what each last saw.

**Digests.** Admin › Alerts and digests › *Digests*: add one (report, recipients, daily, weekly or monthly, the hour, the period, and optionally one manager's organisation); **Preview** it, **Send now**, **Pause** or **Remove** it. Digests carry aggregates only.

**Nothing arrives.** Admin › Email › *Recently sent* gives the reason for each message not sent: email off, no recipients, or the server's own answer (a refused password, a certificate that did not validate, a closed connection).

## Targets, value and training

**Adoption targets.** Admin › Targets and assumptions › *Adoption targets*: for the whole organisation or one manager's, the measure (adoption, share of people active, or people active), the target, the start and the due date. Progress is on Adoption › Targets and in the Adoption digest.

**Time saved.** Admin › Targets and assumptions › *Time saved*: the default minutes one interaction saves, what an hour is worth, and the minutes per app. Leadership › Value and time saved and the Value digest follow them.

**Training attendance.** Admin › Training records: upload a CSV of UPN, session and date (`yyyy-MM-dd`; a template is on the page). Lines already there are skipped and bad lines listed. Remove a session with its *Remove* button. Enablement › Training effect shows each session four weeks after it.

**Champions.** How many to suggest per department: Admin › Targets and assumptions › *Licensing and champions*. The list is Enablement › Champions, for the Enablement role.

## Acting on the licence recommendations

1. **Forecasting › Licence optimisation** shows the recommendations and what they are worth; **Forecasting › Upgrade, train or revoke** and **Licensing › Licences on disabled accounts** name the people (Licensing role).
2. On those pages, download the **Entra bulk add or remove members file**, or the **PowerShell script**. If Copilot licences are assigned through a group, set its object id first in Admin › Targets and assumptions › *Licensing and champions*, so both use the group; otherwise the script assigns or removes the SKU directly.
3. A licence administrator applies it: the bulk file in Entra (Groups › the licence group › Bulk operations), or the script in PowerShell with their own rights (`-WhatIf` first). The dashboard changes nothing in Entra.
4. The next nightly run picks the change up; the lists update the day after.

## Privacy, retention and the data API

**Privacy mode.** Admin › Privacy and retention › *Privacy mode*: *Pseudonymise* refuses every page, export, digest and API call that names people, and Power BI's `bi.Users` shows a salted hash instead of names (from its next refresh). *Off* restores both.

**Retention.** Admin › Privacy and retention › *Retention*: days to keep the dashboard's audit log and the notification log, and when to clear the stored JSON of old records (0 keeps it; at least 200 days). Applied each night after a successful run; **Apply retention now** does it at once. To clear JSON from the job server instead, `M -Task PurgeJson -RetainDays 365` reports what would go and `-Apply` clears it (`-DeleteRows` removes those records altogether).

**The data API.** Admin › Privacy and retention › *Data API* turns `/api/v1` on or off. Callers need the same access as the pages; [DASHBOARD.md](DASHBOARD.md#the-data-api) lists the endpoints.

**Who did what.** Admin › Audit log: every page view, refusal, export, API call and admin change, with who, when, the scope and the filters, searchable and exportable.

## The pipeline

**The nightly task** (`Copilot audit - nightly`, 02:00 by default) runs `Invoke-CopilotPipeline.ps1`: the directory, the licences, the licence events and the Copilot interactions, then retention. It records each run and step in `audit.PipelineRun`. To run it now, or to resume after a failure, run `.\Invoke-CopilotPipeline.ps1` on the job server. To move it, run `tools\Install-CopilotJobServer.ps1 -At 03:30`.

**Refresh or rebuild by hand**, after a change that the nightly run would not pick up:

| After | Do this |
|---|---|
| A change to the directory, the top of the organisation or the app categories, wanted on the dashboard now | Admin › Health › *Refresh the last 7 days*, or `M -Task RefreshDashboard` (every day) or `-SinceUtc 2026-10-01` |
| A session setting changes (timeout, time zone, licence databases) | `M -Task RebuildSessions` |
| Licence history arrives for a period already built, or flattened rows are purged | `M -Task RebuildBi`, then a full refresh of the Power BI model |
| A release changes the flattening | `M -Task Reflatten` (every record flattened again, then sessions, `bi` and the dashboard) |
| The flatten says a field *now carries information* | Nothing breaks; see `audit.RawLogRecordShape` and [RUNBOOK.md](../RUNBOOK.md) |

`M -Task Status` shows what is loaded and built, and when each step last ran.

## Upgrades and renewals

**A new release.** Back up both databases, replace the scripts on the job server with the new release's (keep `copilot-audit.settings.psd1` and `logs`), run `.\Install-CopilotAudit.ps1 -SqlServer SQLPROD01` once, and read the release notes' upgrade section for any one-off step. For the dashboard, run `dashboard\Install-CopilotInsights.ps1` again with the new `copilot-insights` zip. From 2.2.0 to 2.3.0, also run `M -Task RebuildValueCatalog` once.

**The app's certificate.** Within 30 days of expiry, run `.\tools\Install-CopilotJobServer.ps1 -RegisterApp` on the job server: a new certificate, its private key for the gMSA, uploaded to the app next to the old one, and its thumbprint in the settings. Remove the old certificate from the app after the next good run.

**Exchange Online.** If the dashboard sends with a mailbox password, move it to OAuth (Admin › Email) before Microsoft turns SMTP AUTH passwords off for your tenant.

## Every Admin page and maintenance task

<!-- Kept complete by tests/Test-OperationsGuide.ps1: every Admin page in dashboard/CopilotInsights/Pages/Nav.cs and every -Task of tools/Invoke-CopilotMaintenance.ps1 must appear in this section. -->

| Admin page | What it is for |
|---|---|
| Admin › Health | Pipeline runs and each step's freshness, directory coverage, the latest day with data, and a manual refresh |
| Admin › Access | The top of the organisation, the access settings and licence price, granting and removing roles, checking someone's access |
| Admin › Email | How email is sent, a test message, and everything sent or not |
| Admin › Alerts and digests | Alert recipients and thresholds, digests, running the checks now |
| Admin › Targets and assumptions | Adoption targets, minutes saved and an hour's worth, the licensing group, champions per department |
| Admin › Privacy and retention | Privacy mode, retention of logs and JSON, the data API |
| Admin › Reference data | Agent names and owners, sensitivity label names and order, app categories |
| Admin › Value names | Names for every catalogued code, one by one or by CSV |
| Admin › Training records | Training attendance, by CSV |
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
| `SetOrgTop` | Make `-Upn` the top of the organisation, or `-Clear` |
| `SetDashboardSetting` | Set MinGroupSize, DefaultDays, InactiveDays, LicenceMonthlyCost or LicenceCurrency to `-Value` |
| `NameAgent` | Give agent `-AgentId` a `-FriendlyName` and `-Owner` |
| `NameLabel` | Give label `-LabelId` a `-FriendlyName` and `-Rank` |
| `PurgeJson` | Clear the stored JSON of records older than `-RetainDays` (`-Apply` to do it) |
| `RebuildValueCatalog` | List every catalogued column's values again, from every flattened record |
| `ListValues` | The catalogued columns, or one column's values and names (`-ColumnId`, `-Path` for a CSV) |
| `ImportValueNames` | Names for catalogued values from `-Path` |
