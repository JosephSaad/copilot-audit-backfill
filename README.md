# Copilot audit backfill → SQL Server

Pulls every `CopilotInteraction` audit record for a date range out of Microsoft Purview and into SQL Server, using the Purview Audit Search Graph API. Downloads and SQL loading run in parallel with no CSV files in between, it stays inside Microsoft's published Audit Search API limits, and it resumes after any interruption.

```
Purview audit searches ──► parallel page downloads ──► bounded queue ──► parallel SqlBulkCopy
 (sized to the record cap,   (adaptive concurrency,                    (heap + TABLOCK,
  within the daily budget)    honors Retry-After)                      bulk-update locks)
                        state for every window: audit.CopilotBackfillWindow
```

PowerShell 7 drives it; the pipeline engine is C# compiled in memory at start, so there are no binaries to ship or trust. State lives in SQL, so stopping and restarting is always safe.

**[RUNBOOK.md](RUNBOOK.md) is the full documentation** — limits, tuning, troubleshooting, and what the finalized tables contain. **[docs/DEPLOYMENT.md](docs/DEPLOYMENT.md)** walks through a new install step by step, from the app registration to the nightly job, the Power BI model and its Copilot agent. **[docs/SCHEMA.md](docs/SCHEMA.md)** lists every table and view, column by column, and the script that creates it. **[docs/DASHBOARD.md](docs/DASHBOARD.md)** covers the Copilot Insights web dashboard. This README is the short version.

## Prerequisites

**Retention.** Microsoft keeps Copilot audit records for **180 days**. Audit (Premium)'s one-year default covers only Entra, Exchange, OneDrive and SharePoint. Ask for 180 days unless a custom audit retention policy already covered Copilot — those policies are not retroactive. Check with `Get-UnifiedAuditLogRetentionPolicy` before planning a longer run.

**The sign-in account needs both of these** — one without the other returns 403:

| Requirement | Where |
|---|---|
| `AuditLogsQuery.Read.All` delegated permission, admin-consented for *Microsoft Graph Command Line Tools* (or your own app, passed with `-ClientId`) | Entra admin center → Enterprise applications |
| **Audit Reader** or **Audit Manager** role | Purview portal → Roles and scopes |

**The machine that runs it:**

- PowerShell **7.2+** (7.4 recommended). Windows PowerShell 5.1 will not run the loader; the one script that needs 5.1 is the dashboard's IIS installer. Both install side by side: [DEPLOYMENT.md](docs/DEPLOYMENT.md#which-powershell-runs-what) says which runs what.
- `Install-Module Microsoft.Graph.Authentication -Scope CurrentUser` — the only module needed. On a server with no gallery access, see [Servers without gallery access](#servers-without-gallery-access).
- Outbound HTTPS to `login.microsoftonline.com` and `graph.microsoft.com`.
- An interactive desktop session for the browser sign-in, or `-AuthMode DeviceCode` to sign in from another device.

**SQL Server:**

- SQL Server **2017+** (2016+ for loading alone). Database compatibility level **130+**.
- `db_owner`, or `db_ddladmin` + `db_datareader` + `db_datawriter`.
- **Plan ~1.5 KB per record in staging, about 75 GB per 50M records.** Each record's JSON is gzip-compressed before it reaches SQL.
- Put the database in **SIMPLE** or **BULK_LOGGED** recovery for the load so the parallel bulk inserts are minimally logged.

## Quick start

**A production job server** takes two commands. Run the first in an elevated PowerShell 7 window, as a SQL Server sysadmin; you sign in once as an admin who can grant consent:

```powershell
.\tools\Install-CopilotJobServer.ps1 -SqlServer SQLPROD01 -ServiceAccount 'CORP\gmsa-copilot$' `
    -TargetDomain users.contoso.com -TenantId contoso.onmicrosoft.com -RegisterApp
.\Invoke-CopilotPipeline.ps1 -History
```

The first command does the setup:

- the certificate, the gMSA's access to its private key, and its *Log on as a batch job* right;
- both databases and every SQL object;
- the app registration, with its three permissions, their admin consent and the certificate;
- a settings file, and the nightly task.

The second loads 180 days of history and builds everything. From then on, the task runs `Invoke-CopilotPipeline.ps1` every night. [docs/DEPLOYMENT.md](docs/DEPLOYMENT.md) walks through it step by step, with the alternatives for when a DBA or an identity team does their part separately.

**Trying it interactively,** signed in as yourself: smoke-test one day into a scratch database first, and compare the row count against the same day searched in the Purview portal:

```powershell
.\Invoke-CopilotAuditBackfill.ps1 -SqlServer SQLDEV01 -Database CopilotAuditTest `
    -TenantId contoso.onmicrosoft.com `
    -StartDate (Get-Date).ToUniversalTime().Date.AddDays(-2) `
    -EndDate   (Get-Date).ToUniversalTime().Date.AddDays(-1) -Finalize
```

Then the full run:

```powershell
.\Invoke-CopilotAuditBackfill.ps1 -SqlServer SQLPROD01 -Database CopilotAudit `
    -TenantId contoso.onmicrosoft.com `
    -StartDate (Get-Date).ToUniversalTime().Date.AddDays(-180) -Finalize
```

A browser opens for sign-in. Every 30 seconds it prints a progress line:

```
 34% covered | windows 12 loaded, 8 searching, 5 downloading | 7 streams | 6,210 rows/s | 7,412,000 rows in SQL | 429s 2 | ETA 1h48m
```

**Ctrl+C, a crash or a reboot:** run the same command again. The dates are remembered in SQL, so a resume does not need them, and searches that already finished server-side are reused rather than resubmitted. Add `-Status` from another window to check progress.

## Keeping it current

The backfill covers one fixed range. Once it is finalized, run `-Incremental` on a schedule to keep the tables current:

```powershell
.\Invoke-CopilotAuditBackfill.ps1 -SqlServer SQLPROD01 -Database CopilotAudit -Incremental `
    -TenantId contoso.onmicrosoft.com -ClientId <app-id> -CertificateThumbprint <thumbprint>
```

Each run loads from where the data ends up to the start of the current hour, and appends the new records to `audit.CopilotAudit` and `audit.CopilotInteraction` without rebuilding them. It also re-searches the 48 hours before that (`-LookbackHours`), because Purview can deliver a record hours after the event; records already loaded are counted and skipped, never duplicated. It then refreshes whichever derived tables are installed: the flattened table, license events, sessions and the `bi` schema, each from the load's start rather than from scratch (`-SessionLicenseDatabase` passes the license history through). A daily run costs 2–4 of the tenant's ~200 searches per 24 hours.

`-CertificateThumbprint` signs in as an app, so Task Scheduler can run it with nobody present. Without it, the run signs in interactively as before. A run that stops part-way resumes its load next time, and a run that fails exits non-zero. See [RUNBOOK.md](RUNBOOK.md#5b-keeping-it-current) for the app registration and the scheduled task.

**App registration permissions.** One app can run everything. Three Microsoft Graph **application** permissions, admin-consented, all read-only:

| Permission | Needed for |
|---|---|
| `AuditLogsQuery.Read.All` | The backfill and `-Incremental` loads: Copilot records and license events. Enough on its own if you don't run the license tool. |
| `User.Read.All` | License tool: each user's assigned licenses and plans, and `-AllLicensedUsers`. |
| `LicenseAssignment.Read.All` | License tool: the tenant's SKUs, to find the Copilot ones. |

Plus a certificate credential. User attributes - title, department, division, office, manager, the org chain - come from on-premises Active Directory instead (see [Adoption reporting](#adoption-reporting)), which needs no Graph permission at all. An app needs no Purview audit role; that applies to people signing in. RUNBOOK section 5b lists the Graph calls behind each permission.

## Importing Purview CSV exports

Audit log exports from the Purview portal can go into the same tables, instead of the Graph backfill or alongside it. Point the importer at a folder:

```powershell
.\tools\Import-CopilotAuditCsv.ps1 -Path D:\PurviewExports -SqlServer SQLPROD01 -Database CopilotAudit `
    -SessionLicenseDatabase CopilotLicenseEvents
```

- **Overlaps are expected.** Exports may cover overlapping ranges and repeat each other; one row per `RecordId` is kept, and a record already loaded - by the backfill, `-Incremental` or an earlier import - wins.
- **Run it again as the folder grows.** A file already imported (same SHA-256) is skipped.
- **Nothing is loaded broken.** Rows whose `AuditData` is not JSON (a file opened and saved in Excel cuts it at 32,767 characters), or that have no record ID or time, are listed in `audit.CopilotCsvReject` with their line number. Rows of another operation are counted and left out.
- **Everything downstream is unchanged.** Records land as the Graph path stores them, and the importer then refreshes the flattened table, sessions and `bi` from the earliest record it added.

**Starting from CSV files alone.** Pass `-CoveredThroughUtc`, the end of the search range the exports were made for. The import records it as the database's range, so `-Incremental` continues from there. The Graph API keeps 180 days only: if the exports end earlier than that, the days in between cannot be filled. The files are read by the importer, not by SQL Server, and it runs on PowerShell 7.2+. RUNBOOK section 5e has the details.

## Servers without gallery access

`Microsoft.Graph.Authentication` is the only external module — everything else the tool uses, including `System.Data.SqlClient`, ships inside PowerShell 7. On a locked-down server that cannot reach the PowerShell Gallery, put the module next to the script instead of installing it.

On any machine that **can** reach the gallery (Windows x64 is the closest match to the usual target):

```powershell
.\tools\Save-CopilotAuditModules.ps1
```

That pins a known-good version, saves it into a `Modules` folder beside the script, checks the saved copy resolves with `PSModulePath` pointing at it alone, and writes `Modules\MODULES-MANIFEST.csv` with a SHA-256 for every file. Copy the whole tool folder — `Modules` included — to the server and run it as normal.

`Invoke-CopilotAuditBackfill.ps1` puts that folder at the front of `PSModulePath` at startup, so nothing is installed on the server and nothing is written outside the folder. When the folder is absent, module resolution is unchanged.

Each release also carries a prebuilt `...-offline-win-x64.zip` with the module already in place, built on a Windows runner. It bundles Microsoft-owned binaries under Microsoft's own license terms — see `NOTICE-BUNDLED-MODULES.txt` inside it — so rebuild it yourself with the script above if you would rather not rely on the prebuilt copy.

## What you get

| Object | What it is |
|---|---|
| `audit.CopilotAudit` | Every record once: final attempt per window, boundary duplicates removed. Clustered columnstore, full JSON kept compressed in `AuditDataGz`. |
| `audit.vCopilotAuditRecord` | The same records in the **Purview export's shape**, with `AuditData` decompressed. SQL written against the portal CSV import can point here. |
| `audit.CopilotInteraction` | Slim reporting table: activity date, user, app host, model, prompt and resource counts, web grounding. |
| `audit.vCopilotDailyUsage` | Interactions, active users, prompts and web-grounded interactions per day per app. Ready for Power BI. |
| `audit.RawLogFlattened` | One row per record with `CopilotEventData` shredded into columns — accessed resources and their locations, sensitivity labels, plugins, jailbreak and prompt-injection flags — and every other field of the record that carries information in its own typed column (2.2.0). |
| `audit.RawLogAccessedResource`, `audit.RawLogContext` | One row per element of the record's `AccessedResources` and `Contexts` arrays: type, site, label, policy outcome, blocked, prompt injection (2.2.0). |
| `audit.vCopilotAdoption` | Licensed users against actual usage, with `NeverUsed` and `DaysToFirstUse`. Needs the enrichment step below. |
| `audit.vCopilotUsageByDepartment` | Usage per department, day and app. Needs the enrichment step below. |
| `bi.*` | A star schema for a Power BI semantic model: interaction, session and license facts with integer keys, and their dimensions. See [Power BI: the `bi` schema](#power-bi-the-bi-schema). |
| `audit.CopilotSession` / `audit.CopilotWeeklyRollup` | Inferred sessions (60 minutes idle or a new day ends one) and their Saturday-Friday weekly rollup. `EXEC audit.usp_BuildCopilotSessions` rebuilds them all; `@SinceUtc` only the days from then on, which is what `-Incremental` does. `tools/Export-CopilotSessionCsv.ps1` writes them as the old `copilot_session_summary.csv` and `weekly_rollup.csv`. |

`-Finalize` (or `-FinalizeOnly` later) refuses to run unless the whole range is loaded with no gaps.

> **Before repointing existing views at `vCopilotAuditRecord`**, take one record from the smoke test and diff its `AuditData` against the same record in a portal CSV export. This pipeline reads the Graph API's `auditData`; it has not been verified against a live tenant that the JSON is byte-for-byte identical to the portal export's.

### Flattening and retention

The nightly `-Incremental` run flattens each load, and `Install-CopilotAudit.ps1` flattens everything the first time (and again after an upgrade that adds columns). By hand, and for retention:

```powershell
$m = '.\tools\Invoke-CopilotMaintenance.ps1'
& $m -SqlServer SQL01 -Task Reflatten                                  # every record again, then sessions, bi and the dashboard
& $m -SqlServer SQL01 -Task PurgeJson -RetainDays 180                  # reports what it would clear
& $m -SqlServer SQL01 -Task PurgeJson -RetainDays 180 -Apply           # clear the JSON, keep the rows
& $m -SqlServer SQL01 -Task PurgeJson -RetainDays 365 -DeleteRows -Apply   # remove the records
```

The JSON is what costs the disk, so the default mode clears `AuditDataGz` past the horizon and leaves the reporting and flattened tables intact. A re-flatten skips records whose JSON is gone, so it never blanks what was shredded while the JSON existed.

**What the flattening keeps (2.2.0).** Every field of the Copilot audit record that carries information, as a typed column named after its JSON path (`CopilotEventData.LicenseType` is `CopilotEventData_LicenseType`); identifiers that are always GUIDs are stored as `uniqueidentifier`. The small arrays are JSON text, NULL when empty; `AccessedResources` and `Contexts` get a row per element. Seventeen fields that a 19-million-record production profile shows never vary, or are always empty (`Operation`, `RecordType`, `Workload`, `CopilotLogVersion` and the like), are not stored per row: `audit.RawLogRecordShape` counts their combinations, and the flatten reports if one of them ever takes a second value. `OrganizationId` stays on every row, so one database can hold more than one tenant. A resource counts as blocked when its `Status` failed or a Purview policy outcome other than *None* applied; before 2.2.0 the policy outcome, which records carry as JSON text inside the JSON, was not read.

`audit.vCopilotAuditRecord` exposes `AssociatedAdminUnitsNames` to match the portal export's column list, but it is **always NULL**: the Graph `auditLogRecord` resource carries a single `administrativeUnits` collection of IDs with no display names. Resolve them from Entra if you need them.

Note that audit records do not contain prompt or response text. That needs the Copilot interaction export API or eDiscovery.

## Adoption reporting

The audit records say what happened, not who was entitled to it or where they sit in the organization. Two tools fill that in, each from the one place that knows it:

**Who holds a Copilot license - from Microsoft Graph.**

```powershell
.\tools\Update-CopilotAuditEnrichment.ps1 -SqlServer SQLPROD01 -Database CopilotAudit `
    -TenantId contoso.onmicrosoft.com -AllLicensedUsers
```

Writes only `audit.CopilotLicense`: the Copilot SKU, since when, and whether the user still exists in Entra. Only the users in the audit data are looked up; `-AllLicensedUsers` adds license holders who have never interacted, which is what the shelfware report needs. Needs `User.Read.All` and `LicenseAssignment.Read.All`, and signs in app-only with `-ClientId` and `-CertificateThumbprint` for unattended runs. Afterwards, re-run `audit.usp_FlattenCopilotAudit` to carry `IsCopilotLic` through to the flattened table.

**Where they sit - from on-premises Active Directory.**

```powershell
.\tools\Export-UsersWithHierarchy-ToSql.ps1 -TargetDomain corp.example.com `
    -SqlServer SQLPROD01 -Database CopilotAudit
```

Reads every user in a trusted domain in one paged search and replaces `audit.UserDirectory` in one transaction: display name, mail, employee type, title, department, division, office, city, country, company, manager, whether they manage anyone, whether the account is enabled, and the management chain (`Mgr1` the top down to `Mgr<OrgDepth>`, the direct manager). Every account is loaded, enabled or not - a user who has left still has interactions to attribute. Sessions, the `bi` schema and the adoption views all read it.

It binds to AD as the identity running it, over the trust, and needs **no Graph permission**. It runs on Windows PowerShell 5.1 or PowerShell 7, with nothing to install. The audit data is joined on the UPN, so after loading it reports how many audit users it matched - by UPN, and by mail for the rest. A low match means AD's UPNs are not the names users sign in to Microsoft 365 with.

```sql
-- Licensed, never used: usually the number that pays for the rollout
SELECT UserPrincipalName, Department, LicensedSinceUtc
FROM audit.vCopilotAdoption WHERE NeverUsed = 1
ORDER BY LicensedSinceUtc;
```

Two limits are recorded rather than guessed at. Graph gives the date the **current** license assignment began, so "licensed since March" is answerable but "was this user licensed last March" is not — a license granted and later removed leaves no trace. And audit records outlive directory objects, so a departed user is stored as `DirectoryStatus = 'NotFound'` with `IsCopilotLic` NULL: *not known*, which is a different claim from *unlicensed*.

## Power BI: the `bi` schema

A star schema for a Power BI semantic model. The audit tables are shaped for loading and auditing; a model over tens of millions of interactions wants narrow facts with integer keys and small dimensions. `Install-CopilotAudit.ps1` installs it and builds it the first time there is data:

```powershell
.\Install-CopilotAudit.ps1 -SqlServer SQL01 -LicenseEventDatabases CopilotLicenseEvents,CopilotLicenseLive
```

From then on each `-Incremental` run refreshes it after the other derived tables (`-SessionLicenseDatabase` is passed through as `@LicenseEventDatabase`). The model imports the views, which leave out the lineage columns:

| View | One row per | Keys |
|---|---|---|
| `bi.Interactions` | Copilot interaction | Date, User, App, Agent, Model, LicenseStatus, LicenseType, SensitivityLabel |
| `bi.AccessedResources` | resource an interaction read | Date, User, App, ResourceType, ResourceAction, Site, SensitivityLabel |
| `bi.Sessions` | inferred session | Date, User, App (the first prompt's), LicenseStatus |
| `bi.LicenseChanges` | SKU added (+1) or removed (−1) | Date, User, Sku |
| `bi.LicensedDays` | user holding a Copilot license at the end of a day | Date, User |
| `bi.Calendar` | date, with the rollup's Saturday–Friday week | `DateKey` |
| `bi.AgentEvents` | Copilot Studio authoring event (2.3.0) | Date, User (the maker) |
| `bi.Users`, `bi.Apps`, `bi.Agents`, `bi.Models`, `bi.Skus`, `bi.LicenseStatuses`, `bi.LicenseTypes`, `bi.SensitivityLabels`, `bi.ResourceTypes`, `bi.ResourceActions`, `bi.Sites` | dimension rows | `UserKey`, `AppKey`, and the rest |
| With the dashboard: `bi.OrgSecurity`, `bi.UseCases`, `bi.JobFamilies`, `bi.EventMarkers`, `bi.LicensePool`, `bi.ValueNames`, `bi.DepartmentWeek` | who may see whom, use cases, job families, events, the license pool, value names, department weeks with small groups hidden (2.3.0) | `UserKey`, `UseCaseKey`, `DateKey` |

- **Relationships:** each fact's `DateKey`, `UserKey`, `AppKey` and so on to the dimension of the same key, one-to-many, single direction. Mark `bi.Calendar` as the date table.
- **Incremental refresh:** on `bi.Interactions` and `bi.AccessedResources`, filter `ActivityHour` between `RangeStart` and `RangeEnd`. It is local time truncated to the hour, so it has ~8,760 values a year and costs almost nothing in memory. The other facts are small enough to refresh whole.
- **License at the time of use** is on the facts (`LicenseStatusKey`): *Licensed*, *Not licensed*, or *Unknown* when there is neither license history nor enrichment for the user. `bi.Users.CopilotLicenseToday` is today's license, which is a different question. `LicenseTypeKey` is what the record itself reports (`CopilotEventData.LicenseType`, such as Premium or Starter), from 2.2.0.
- **Left out on purpose:** RecordId, ThreadId, ConversationId, app context, whole resource URLs and the JSON. Each is close to one value per row and would cost more memory than the rest of the model together. Resources are in the model by type and site (`bi.AccessedResources`, `bi.Sites`); the rest stays in `audit.RawLogFlattened` and `audit.RawLogAccessedResource` for a DirectQuery drill-through table if you need one.
- **Keys are stable** across refreshes and rebuilds, so partitions refreshed on different days stay consistent.

**A ready-made model** ships in `powerbi/CopilotInsights` (2.3.0): a Power BI project over every view here and the dashboard's, with the relationships, measures (adoption, Premium use, rankings by general, free and Premium use, sessions, licenses, agents), descriptions and an *Organization* row-level-security role from the org chart. It has been opened and refreshed in Power BI Desktop against the demo database, where its figures matched the dashboard's. [DEPLOYMENT.md, step 15](docs/DEPLOYMENT.md#step-15-open-the-ready-made-model) sets it up. Some of its measures:

```dax
Interactions = COUNTROWS ( Interactions )
Prompts = SUM ( Interactions[PromptCount] )
Active users = DISTINCTCOUNT ( Interactions[UserKey] )
// The period's last day the data covers, the same for every row and the total.
Period end = CALCULATE ( MAX ( Calendar[Date] ), KEEPFILTERS ( Calendar[IsLoaded] = TRUE () ) )
Licensed users =
    VAR EndDate = [Period end]
    RETURN CALCULATE ( DISTINCTCOUNT ( LicensedDays[UserKey] ), Calendar[Date] = EndDate )
// Used it with a license in the period, and still hold one at its end: always part of
// [Licensed users], so adoption can't pass 100% when licenses are removed mid-period.
Active licensed users =
    VAR EndDate = [Period end]
    VAR HeldAtEnd = CALCULATETABLE ( VALUES ( LicensedDays[UserKey] ), Calendar[Date] = EndDate )
    RETURN CALCULATE ( [Active users], LicenseStatuses[LicenseStatus] = "Licensed",
                       KEEPFILTERS ( TREATAS ( HeldAtEnd, Interactions[UserKey] ) ) )
Adoption % = DIVIDE ( [Active licensed users], [Licensed users] )
Premium share = DIVIDE ( CALCULATE ( [Interactions], Interactions[PremiumUse] = TRUE () ), [Interactions] )
```

`@SinceUtc` (the load's start, from `-Incremental`) rebuilds only the interactions from then on; `NULL` rebuilds them all. Run a full rebuild, and a full refresh of the model, after changing the reporting time zone (a partial refresh in a different zone is refused), after license history arrives for a period already built, or after purging flattened rows. Refresh the model fully, too, after an app's category changes: the `bi` refresh sets Premium use again on all of that app's past interactions, older than the model's incremental window.

## Copilot Insights dashboard

A web dashboard over the same database, for people who will never open Power BI: an ASP.NET Core 10 site in IIS on Windows Server 2022+, with Windows sign-in. It works with the web servers in one domain and the users in another.

- **Who sees what.** The top of the organization and their direct reports see everything, as does anyone granted *Organization*; every other manager sees the people under them, at any depth; everyone else sees their own use. The scope comes from the AD manager chain and is enforced on every query.
- **Pages by persona:** Leadership (including use cases and a printable board pack), Adoption (including a department leaderboard), Licensing (including the license pool, chargeback and a ranking by general, free and Premium use), Security (with triage of flagged interactions, and guests), Compliance (with one person's record for data-subject requests), Agents (with Copilot Studio build and publish activity), Enablement, Governance, Forecasting (with budget and renewal, rollout waves and license action outcomes), Regions, People (with job families) and App owners. Forecasting turns forecasts into priced recommendations: upgrade proven Copilot Chat users, train light users and revoke licenses whose Premium features go unused. Lists of named people need that persona's role, and stay inside the viewer's scope. The dashboard can be limited to the top levels of the organization.
- **Periods** of 7, 14, 30, 90, 180 or 365 days; every list paged in SQL (50 rows, search, sort) and exportable to CSV; groups under five people folded together; every chart label shown whole.
- **Read-only towards Microsoft 365:** it changes nothing there; what it writes (its own tables, email, a webhook, extract files, downloads) is listed in [DASHBOARD.md](docs/DASHBOARD.md).
- **Operations:** email through Exchange Online or any SMTP server; alerts (pipeline, stale data, the app certificate, security signals, drops and spikes, the license pool, new agents) by email, to Teams or ServiceNow by webhook, and to the Windows event log; scheduled and manager digests; scheduled CSV extracts; a read-only data API; privacy mode and retention.
- **Admin module:** access and roles, directory quality, email, alerts and digests, targets and assumptions, reference data and value names, classifications and events, extracts, training records, pipeline health, and the audit log of every view, refusal, export and change.
- **Several tenants:** one dashboard over each tenant's own databases, with a tenant switcher and a group overview.

```powershell
.\dashboard\Install-CopilotInsights.ps1 -PackagePath .\CopilotInsights -SqlServer SQL01 -Database CopilotAudit `
    -AdminPrincipal 'CONTOSO\Copilot-Insights-Admins' -HostName insights.contoso.com -CertificateThumbprint <thumbprint> -GrantDatabaseAccess
```

`dash.usp_RefreshDashboard` keeps its org chart and daily rollups current: every `-Incremental` run and CSV import calls it after `bi`. **[docs/DASHBOARD.md](docs/DASHBOARD.md)** has every page, the security model, the install and how the first administrator gets in; `dashboard/demo/New-InsightsDemo.ps1` builds a 2,317-person demo company to try it on.

## Tests

```bash
pwsh -NoProfile -File tests/Test-Engine.ps1        # engine against a mock Graph service
pwsh -NoProfile -File tests/Test-Orchestrator.ps1  # the PowerShell orchestrator's own logic
pwsh -NoProfile -File tests/Test-Provisioning.ps1  # app registration, the pipeline's plan, the job-server settings
```

Both need `python3` on PATH and take a couple of minutes. They run the whole pipeline — adaptive windowing, cap detection and splits, page throttling, token rotation, transient SQL failures, a hard stop and a resume — against `tests/mock_graph.py`, and prove every record lands exactly once.

Those two use in-memory stand-ins for SQL. The third runs the real `SqlBulkCopy` path and every SQL script against a real SQL Server:

```bash
docker run -d --name sqlit -e ACCEPT_EULA=Y -e MSSQL_SA_PASSWORD='<pw>' \
    -p 1433:1433 mcr.microsoft.com/mssql/server:2022-latest
SQLIT_PASSWORD='<pw>' pwsh -NoProfile -File tests/Test-SqlIntegration.ps1
```

It creates a database, runs the DDL, backfills 8 days through the real engine, stops and resumes it, finalizes, flattens and purges, builds sessions, `bi` and the dashboard's rollups, and checks the results. It exits 2 and explains itself if `SQLIT_PASSWORD` is not set.

The dashboard's own tests (the security rules, paging, periods, exports and charts) need the .NET 10 SDK:

```bash
dotnet test dashboard/CopilotInsights.Tests
```

CI runs them all on every push, with PSScriptAnalyzer (`PSScriptAnalyzerSettings.psd1`), which fails the build on any warning.

Two more are run by hand before a release, since CI has no dashboard or installed database to point them at: the layout audit of every dashboard page and the generator for `docs/SCHEMA.md`. They are in `tools/dev`, with the release checklist ([tools/dev/README.md](tools/dev/README.md)).

## Files

| File | Purpose |
|---|---|
| `Invoke-CopilotAuditBackfill.ps1` | The loader: a backfill or an `-Incremental` run. |
| `Install-CopilotAudit.ps1` | Creates the databases, installs every SQL object and builds what the data calls for. Safe to run again. |
| `Invoke-CopilotPipeline.ps1` | The nightly run, or the first load with `-History`, from the job server's settings. |
| `tools/Install-CopilotJobServer.ps1` | Sets up the job server: certificate, service account, databases, app registration, settings and the nightly task. |
| `tools/New-CopilotAuditApp.ps1` | Registers the Entra app: the three permissions, admin consent and the certificate. |
| `tools/Invoke-CopilotMaintenance.ps1` | Day-to-day tasks without SQL: status, rebuilds, roles, settings, names, retention. |
| `CopilotAuditEngine.cs` | The pipeline engine, compiled in memory at start. |
| `Import-CopilotAuditEngine.ps1` | Compiles the engine. |
| `sql/Create-CopilotAuditSchema.sql` | DDL, run automatically. Provided for DBA review. |
| `sql/Finalize-CopilotAudit.sql` | Dedupe, columnstore, reporting tables and the export-shaped view. |
| `sql/Create-CopilotLoadProcedure.sql` | `audit.usp_MergeCopilotLoad`, which appends each `-Incremental` load. Installed automatically. |
| `sql/Create-CopilotFlattenedSchema.sql` | The flattened table, plus the optional license lookup. |
| `sql/Create-CopilotFlattenProcedure.sql` | `audit.usp_FlattenCopilotAudit`. |
| `sql/Create-CopilotPurgeProcedure.sql` | `audit.usp_PurgeCopilotAuditJson`. |
| `sql/Create-CopilotEnrichmentSchema.sql` | The license and user directory tables, plus the adoption views. |
| `tools/Save-CopilotAuditModules.ps1` | Builds the offline module bundle for a disconnected server. |
| `tools/Update-CopilotAuditEnrichment.ps1` | Copilot licenses, from Microsoft Graph. |
| `tools/Export-UsersWithHierarchy-ToSql.ps1` | The user directory and management chain, from on-premises AD, and the members of AD groups granted Organization, for Power BI. |
| `sql/Create-CopilotSessionSchema.sql` | Session, weekly rollup, app-category and run-log tables, and the CSV-shaped views. |
| `sql/Create-CopilotSessionProcedure.sql` | `audit.usp_BuildCopilotSessions`. |
| `tools/Export-CopilotSessionCsv.ps1` | Writes the session and weekly CSVs. |
| `sql/Create-CopilotBiSchema.sql` | The `bi` star schema's tables and the views a Power BI model imports. |
| `sql/Create-CopilotBiProcedure.sql` | `bi.usp_RefreshBi`, which fills it. |
| `tools/Import-CopilotAuditCsv.ps1` | Imports a folder of Purview audit log exports (CSV) into the same tables. |
| `sql/Create-CopilotCsvImport.sql` | The CSV import's tables and `audit.usp_MergeCopilotCsv`. Run by the importer. |
| `sql/Create-CopilotDashboardSchema.sql` | The dashboard's `dash` schema: settings, roles, reference names, audit log, org chart and daily rollups. |
| `sql/Create-CopilotDashboardProcedure.sql` | `dash.usp_RefreshDashboard`, which rebuilds the org chart and rollups after `bi`. |
| `dashboard/CopilotInsights/` | The Copilot Insights web dashboard (ASP.NET Core 10, Razor Pages). |
| `dashboard/Install-CopilotInsights.ps1` | Installs or updates it on IIS, with Windows authentication and its database grants. |
| `dashboard/demo/` | A synthetic 2,317-person company to try the dashboard on. |
| `dashboard/CopilotInsights.Tests/` | The dashboard's unit tests. |
| `docs/SCHEMA.md` | Every table and view, column by column, and the script that creates each. |
| `docs/DASHBOARD.md` | The dashboard: pages by persona, who sees what, install on IIS, the first administrator. |
| `docs/DEPLOYMENT.md` | Step-by-step deployment: app registration, SQL Server, history load, directory and licenses, the nightly job, Power BI and Copilot. |
| `tests/` | Mock Graph service and the test suites. |
| `tools/dev/` | For building a release: the dashboard's layout audit, the `docs/SCHEMA.md` generator and the release checklist. Not deployed. |
| `PSScriptAnalyzerSettings.psd1` | The analyzer rules CI applies, and why each excluded rule is excluded. |

## License

MIT — see [LICENSE](LICENSE).
