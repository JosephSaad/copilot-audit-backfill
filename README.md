# Copilot audit backfill → SQL Server

Pulls every `CopilotInteraction` audit record for a date range out of Microsoft Purview and into SQL Server, using the Purview Audit Search Graph API. Downloads and SQL loading run in parallel with no CSV files in between, it stays inside Microsoft's published Audit Search API limits, and it resumes after any interruption.

```
Purview audit searches ──► parallel page downloads ──► bounded queue ──► parallel SqlBulkCopy
 (sized to the record cap,   (adaptive concurrency,                    (heap + TABLOCK,
  within the daily budget)    honours Retry-After)                      bulk-update locks)
                        state for every window: audit.CopilotBackfillWindow
```

PowerShell 7 drives it; the pipeline engine is C# compiled in memory at start, so there are no binaries to ship or trust. State lives in SQL, so stopping and restarting is always safe.

**[RUNBOOK.md](RUNBOOK.md) is the full documentation** — limits, tuning, troubleshooting, and what the finalized tables contain. **[docs/DEPLOYMENT.md](docs/DEPLOYMENT.md)** walks through a new install step by step, from the app registration to the nightly job, the Power BI model and its Copilot agent. This README is the short version.

## Prerequisites

**Retention.** Microsoft keeps Copilot audit records for **180 days**. Audit (Premium)'s one-year default covers only Entra, Exchange, OneDrive and SharePoint. Ask for 180 days unless a custom audit retention policy already covered Copilot — those policies are not retroactive. Check with `Get-UnifiedAuditLogRetentionPolicy` before planning a longer run.

**The sign-in account needs both of these** — one without the other returns 403:

| Requirement | Where |
|---|---|
| `AuditLogsQuery.Read.All` delegated permission, admin-consented for *Microsoft Graph Command Line Tools* (or your own app, passed with `-ClientId`) | Entra admin center → Enterprise applications |
| **Audit Reader** or **Audit Manager** role | Purview portal → Roles and scopes |

**The machine that runs it:**

- PowerShell **7.2+** (7.4 recommended). Windows PowerShell 5.1 will not work.
- `Install-Module Microsoft.Graph.Authentication -Scope CurrentUser` — the only module needed. On a server with no gallery access, see [Servers without gallery access](#servers-without-gallery-access).
- Outbound HTTPS to `login.microsoftonline.com` and `graph.microsoft.com`.
- An interactive desktop session for the browser sign-in, or `-AuthMode DeviceCode` to sign in from another device.

**SQL Server:**

- SQL Server **2017+** (2016+ for loading alone). Database compatibility level **130+**.
- `db_owner`, or `db_ddladmin` + `db_datareader` + `db_datawriter`.
- **Plan ~1.5 KB per record in staging, about 75 GB per 50M records.** Each record's JSON is gzip-compressed before it reaches SQL.
- Put the database in **SIMPLE** or **BULK_LOGGED** recovery for the load so the parallel bulk inserts are minimally logged.

## Quick start

Smoke test one day into a scratch database first, and compare the row count against the same day searched in the Purview portal:

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

Each run loads from where the data ends up to the start of the current hour, and appends the new records to `audit.CopilotAudit` and `audit.CopilotInteraction` without rebuilding them. It also re-searches the 48 hours before that (`-LookbackHours`), because Purview can deliver a record hours after the event; records already loaded are counted and skipped, never duplicated. It then refreshes whichever derived tables are installed: the flattened table, licence events, sessions and the `bi` schema, each from the load's start rather than from scratch (`-SessionLicenceDatabase` passes the licence history through). A daily run costs 2–4 of the tenant's ~200 searches per 24 hours.

`-CertificateThumbprint` signs in as an app, so Task Scheduler can run it with nobody present. Without it, the run signs in interactively as before. A run that stops part-way resumes its load next time, and a run that fails exits non-zero. See [RUNBOOK.md](RUNBOOK.md#5b-keeping-it-current) for the app registration and the scheduled task.

**App registration permissions.** One app can run everything. Three Microsoft Graph **application** permissions, admin-consented, all read-only:

| Permission | Needed for |
|---|---|
| `AuditLogsQuery.Read.All` | The backfill and `-Incremental` loads: Copilot records and licence events. Enough on its own if you don't run the licence tool. |
| `User.Read.All` | Licence tool: each user's assigned licences and plans, and `-AllLicensedUsers`. |
| `LicenseAssignment.Read.All` | Licence tool: the tenant's SKUs, to find the Copilot ones. |

Plus a certificate credential. User attributes - title, department, division, office, manager, the org chain - come from on-premises Active Directory instead (see [Adoption reporting](#adoption-reporting)), which needs no Graph permission at all. An app needs no Purview audit role; that applies to people signing in. RUNBOOK section 5b lists the Graph calls behind each permission.

## Importing Purview CSV exports

Audit log exports from the Purview portal can go into the same tables, instead of the Graph backfill or alongside it. Point the importer at a folder:

```powershell
.\tools\Import-CopilotAuditCsv.ps1 -Path D:\PurviewExports -SqlServer SQLPROD01 -Database CopilotAudit `
    -SessionLicenceDatabase CopilotLicenseEvents
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

Each release also carries a prebuilt `...-offline-win-x64.zip` with the module already in place, built on a Windows runner. It bundles Microsoft-owned binaries under Microsoft's own licence terms — see `NOTICE-BUNDLED-MODULES.txt` inside it — so rebuild it yourself with the script above if you would rather not rely on the prebuilt copy.

## What you get

| Object | What it is |
|---|---|
| `audit.CopilotAudit` | Every record once: final attempt per window, boundary duplicates removed. Clustered columnstore, full JSON kept compressed in `AuditDataGz`. |
| `audit.vCopilotAuditRecord` | The same records in the **Purview export's shape**, with `AuditData` decompressed. SQL written against the portal CSV import can point here. |
| `audit.CopilotInteraction` | Slim reporting table: activity date, user, app host, model, prompt and resource counts, web grounding. |
| `audit.vCopilotDailyUsage` | Interactions, active users, prompts and web-grounded interactions per day per app. Ready for Power BI. |
| `audit.RawLogFlattened` | One row per record with `CopilotEventData` shredded into columns — accessed resources and their locations, sensitivity labels, plugins, jailbreak and prompt-injection flags. |
| `audit.vCopilotAdoption` | Licensed users against actual usage, with `NeverUsed` and `DaysToFirstUse`. Needs the enrichment step below. |
| `audit.vCopilotUsageByDepartment` | Usage per department, day and app. Needs the enrichment step below. |
| `bi.*` | A star schema for a Power BI semantic model: interaction, session and licence facts with integer keys, and their dimensions. See [Power BI: the `bi` schema](#power-bi-the-bi-schema). |
| `audit.CopilotSession` / `audit.CopilotWeeklyRollup` | Inferred sessions (60 minutes idle or a new day ends one) and their Saturday-Friday weekly rollup. `EXEC audit.usp_BuildCopilotSessions` rebuilds them all; `@SinceUtc` only the days from then on, which is what `-Incremental` does. `tools/Export-CopilotSessionCsv.ps1` writes them as the old `copilot_session_summary.csv` and `weekly_rollup.csv`. |

`-Finalize` (or `-FinalizeOnly` later) refuses to run unless the whole range is loaded with no gaps.

> **Before repointing existing views at `vCopilotAuditRecord`**, take one record from the smoke test and diff its `AuditData` against the same record in a portal CSV export. This pipeline reads the Graph API's `auditData`; it has not been verified against a live tenant that the JSON is byte-for-byte identical to the portal export's.

### Flattening and retention

Two stored procedures, both re-runnable, are built by the scripts in `sql/`:

```sql
-- Shred the JSON into audit.RawLogFlattened (MERGEs, so it is safe on a schedule)
EXEC audit.usp_FlattenCopilotAudit;
EXEC audit.usp_FlattenCopilotAudit @SinceUtc = '2026-09-01', @RebuildAll = 0;

-- Retention. @WhatIf defaults to 1 and only reports; pass 0 to actually purge.
EXEC audit.usp_PurgeCopilotAuditJson @RetainDays = 180;
EXEC audit.usp_PurgeCopilotAuditJson @RetainDays = 180, @WhatIf = 0;              -- clear the JSON, keep the rows
EXEC audit.usp_PurgeCopilotAuditJson @RetainDays = 365, @DeleteRows = 1, @WhatIf = 0;  -- remove the records
```

The JSON is what costs the disk, so the default mode clears `AuditDataGz` past the horizon and leaves the reporting and flattened tables intact.

`audit.vCopilotAuditRecord` exposes `AssociatedAdminUnitsNames` to match the portal export's column list, but it is **always NULL**: the Graph `auditLogRecord` resource carries a single `administrativeUnits` collection of IDs with no display names. Resolve them from Entra if you need them.

Note that audit records do not contain prompt or response text. That needs the Copilot interaction export API or eDiscovery.

## Adoption reporting

The audit records say what happened, not who was entitled to it or where they sit in the organisation. Two tools fill that in, each from the one place that knows it:

**Who holds a Copilot licence - from Microsoft Graph.**

```powershell
.\tools\Update-CopilotAuditEnrichment.ps1 -SqlServer SQLPROD01 -Database CopilotAudit `
    -TenantId contoso.onmicrosoft.com -AllLicensedUsers
```

Writes only `audit.CopilotLicense`: the Copilot SKU, since when, and whether the user still exists in Entra. Only the users in the audit data are looked up; `-AllLicensedUsers` adds licence holders who have never interacted, which is what the shelfware report needs. Needs `User.Read.All` and `LicenseAssignment.Read.All`, and signs in app-only with `-ClientId` and `-CertificateThumbprint` for unattended runs. Afterwards, re-run `audit.usp_FlattenCopilotAudit` to carry `IsCopilotLic` through to the flattened table.

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

Two limits are recorded rather than guessed at. Graph gives the date the **current** licence assignment began, so "licensed since March" is answerable but "was this user licensed last March" is not — a licence granted and later removed leaves no trace. And audit records outlive directory objects, so a departed user is stored as `DirectoryStatus = 'NotFound'` with `IsCopilotLic` NULL: *not known*, which is a different claim from *unlicensed*.

## Power BI: the `bi` schema

A star schema for a Power BI semantic model. The audit tables are shaped for loading and auditing; a model over tens of millions of interactions wants narrow facts with integer keys and small dimensions. Install it once, after the flattened and session scripts (the session tables may stay empty), then build it:

```sql
-- sql/Create-CopilotBiSchema.sql, then sql/Create-CopilotBiProcedure.sql, then:
EXEC bi.usp_RefreshBi @LicenceEventDatabase = N'CopilotLicenseEvents,CopilotLicenseLive';
```

From then on each `-Incremental` run refreshes it after the other derived tables (`-SessionLicenceDatabase` is passed through as `@LicenceEventDatabase`). The model imports the views, which leave out the lineage columns:

| View | One row per | Keys |
|---|---|---|
| `bi.Interactions` | Copilot interaction | Date, User, App, Agent, Model, LicenceStatus |
| `bi.Sessions` | inferred session | Date, User, App (the first prompt's), LicenceStatus |
| `bi.LicenceChanges` | SKU added (+1) or removed (−1) | Date, User, Sku |
| `bi.LicensedDays` | user holding a Copilot licence at the end of a day | Date, User |
| `bi.Calendar` | date, with the rollup's Saturday–Friday week | `DateKey` |
| `bi.Users`, `bi.Apps`, `bi.Agents`, `bi.Models`, `bi.Skus`, `bi.LicenceStatuses` | dimension rows | `UserKey`, `AppKey` … |

- **Relationships:** each fact's `DateKey`, `UserKey`, `AppKey` and so on to the dimension of the same key, one-to-many, single direction. Mark `bi.Calendar` as the date table.
- **Incremental refresh:** on `bi.Interactions`, filter `ActivityHour` between `RangeStart` and `RangeEnd`. It is local time truncated to the hour, so it has ~8,760 values a year and costs almost nothing in memory. The other facts are small enough to refresh whole.
- **Licence at the time of use** is on the facts (`LicenceStatusKey`): *Licensed*, *Not licensed*, or *Unknown* when there is neither licence history nor enrichment for the user. `bi.Users.CopilotLicenceToday` is today's licence, which is a different question.
- **Left out on purpose:** RecordId, ThreadId, app context, accessed resources and their URLs, sensitivity label IDs and the JSON. Each is close to one value per row and would cost more memory than the rest of the model together. They stay in `audit.RawLogFlattened` for a DirectQuery drill-through table if you need one.
- **Keys are stable** across refreshes and rebuilds, so partitions refreshed on different days stay consistent.

Starting-point measures (not yet tried in a model):

```dax
Interactions = COUNTROWS ( Interactions )
Prompts = SUM ( Interactions[PromptCount] )
Active users = DISTINCTCOUNT ( Interactions[UserKey] )
Active licensed users = CALCULATE ( [Active users], LicenceStatuses[LicenceStatus] = "Licensed" )
Licensed users = CALCULATE ( DISTINCTCOUNT ( LicensedDays[UserKey] ),
                     LASTNONBLANK ( Calendar[Date], CALCULATE ( COUNTROWS ( LicensedDays ) ) ) )
Adoption % = DIVIDE ( [Active licensed users], [Licensed users] )
```

`@SinceUtc` (the load's start, from `-Incremental`) rebuilds only the interactions from then on; `NULL` rebuilds them all. Run a full rebuild, and a full refresh of the model, after changing the reporting time zone (a partial refresh in a different zone is refused), after licence history arrives for a period already built, or after purging flattened rows.

## Tests

```bash
pwsh -NoProfile -File tests/Test-Engine.ps1        # engine against a mock Graph service
pwsh -NoProfile -File tests/Test-Orchestrator.ps1  # the PowerShell orchestrator's own logic
```

Both need `python3` on PATH and take a couple of minutes. They run the whole pipeline — adaptive windowing, cap detection and splits, page throttling, token rotation, transient SQL failures, a hard stop and a resume — against `tests/mock_graph.py`, and prove every record lands exactly once.

Those two use in-memory stand-ins for SQL. The third runs the real `SqlBulkCopy` path and every SQL script against a real SQL Server:

```bash
docker run -d --name sqlit -e ACCEPT_EULA=Y -e MSSQL_SA_PASSWORD='<pw>' \
    -p 1433:1433 mcr.microsoft.com/mssql/server:2022-latest
SQLIT_PASSWORD='<pw>' pwsh -NoProfile -File tests/Test-SqlIntegration.ps1
```

It creates a database, runs the DDL, backfills 8 days through the real engine, stops and resumes it, finalizes, flattens and purges, and checks the results. It exits 2 and explains itself if `SQLIT_PASSWORD` is not set. CI runs all three on every push.

## Files

| File | Purpose |
|---|---|
| `Invoke-CopilotAuditBackfill.ps1` | The script you run. |
| `CopilotAuditEngine.cs` | The pipeline engine, compiled in memory at start. |
| `Import-CopilotAuditEngine.ps1` | Compiles the engine. |
| `sql/Create-CopilotAuditSchema.sql` | DDL, run automatically. Provided for DBA review. |
| `sql/Finalize-CopilotAudit.sql` | Dedupe, columnstore, reporting tables and the export-shaped view. |
| `sql/Create-CopilotLoadProcedure.sql` | `audit.usp_MergeCopilotLoad`, which appends each `-Incremental` load. Installed automatically. |
| `sql/Create-CopilotFlattenedSchema.sql` | The flattened table, plus the optional licence lookup. |
| `sql/Create-CopilotFlattenProcedure.sql` | `audit.usp_FlattenCopilotAudit`. |
| `sql/Create-CopilotPurgeProcedure.sql` | `audit.usp_PurgeCopilotAuditJson`. |
| `sql/Create-CopilotEnrichmentSchema.sql` | The licence and user directory tables, plus the adoption views. |
| `tools/Save-CopilotAuditModules.ps1` | Builds the offline module bundle for a disconnected server. |
| `tools/Update-CopilotAuditEnrichment.ps1` | Copilot licences, from Microsoft Graph. |
| `tools/Export-UsersWithHierarchy-ToSql.ps1` | The user directory and management chain, from on-premises AD. |
| `sql/Create-CopilotSessionSchema.sql` | Session, weekly rollup, app-category and run-log tables, and the CSV-shaped views. |
| `sql/Create-CopilotSessionProcedure.sql` | `audit.usp_BuildCopilotSessions`. |
| `tools/Export-CopilotSessionCsv.ps1` | Writes the session and weekly CSVs. |
| `sql/Create-CopilotBiSchema.sql` | The `bi` star schema's tables and the views a Power BI model imports. |
| `sql/Create-CopilotBiProcedure.sql` | `bi.usp_RefreshBi`, which fills it. |
| `tools/Import-CopilotAuditCsv.ps1` | Imports a folder of Purview audit log exports (CSV) into the same tables. |
| `sql/Create-CopilotCsvImport.sql` | The CSV import's tables and `audit.usp_MergeCopilotCsv`. Run by the importer. |
| `docs/DEPLOYMENT.md` | Step-by-step deployment: app registration, SQL Server, history load, directory and licences, the nightly job, Power BI and Copilot. |
| `tests/` | Mock Graph service and the test suites. |

## License

MIT — see [LICENSE](LICENSE).
