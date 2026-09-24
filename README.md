# Copilot audit backfill → SQL Server

Pulls every `CopilotInteraction` audit record for a date range out of Microsoft Purview and into SQL Server, using the Purview Audit Search Graph API. Downloads and SQL loading run in parallel with no CSV files in between, it stays inside Microsoft's published Audit Search API limits, and it resumes after any interruption.

```
Purview audit searches ──► parallel page downloads ──► bounded queue ──► parallel SqlBulkCopy
 (sized to the record cap,   (adaptive concurrency,                    (heap + TABLOCK,
  within the daily budget)    honours Retry-After)                      bulk-update locks)
                        state for every window: audit.CopilotBackfillWindow
```

PowerShell 7 drives it; the pipeline engine is C# compiled in memory at start, so there are no binaries to ship or trust. State lives in SQL, so stopping and restarting is always safe.

**[RUNBOOK.md](RUNBOOK.md) is the full documentation** — limits, tuning, troubleshooting, and what the finalized tables contain. This README is the short version.

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

The audit records say what happened, not who was entitled to it. Counting interactions tells you usage; comparing it against the licensed population tells you adoption, and that needs directory data the records cannot carry.

```powershell
.\tools\Update-CopilotAuditEnrichment.ps1 -SqlServer SQLPROD01 -Database CopilotAudit `
    -TenantId contoso.onmicrosoft.com -AllLicensedUsers
```

It reads Graph and writes only to `audit.CopilotLicense`, `audit.AdministrativeUnit` and `audit.UserProfile`, so it runs on its own schedule and cannot affect the archive. Only the users that appear in `audit.CopilotInteraction` are looked up, not the whole directory; `-AllLicensedUsers` adds licence holders who have never interacted, which is what the shelfware report needs. It signs in app-only with `-ClientId` and `-CertificateThumbprint` for unattended runs.

Needs `User.Read.All`, `Organization.Read.All` and `AdministrativeUnit.Read.All` — a different set from the backfill, so it belongs on its own app registration rather than widening the audit app's single permission.

Afterwards, re-run `audit.usp_FlattenCopilotAudit` to carry `IsCopilotLic` through to the flattened table.

```sql
-- Licensed, never used: usually the number that pays for the rollout
SELECT UserPrincipalName, Department, LicensedSinceUtc
FROM audit.vCopilotAdoption WHERE NeverUsed = 1
ORDER BY LicensedSinceUtc;
```

Two limits are recorded rather than guessed at. Graph gives the date the **current** licence assignment began, so "licensed since March" is answerable but "was this user licensed last March" is not — a licence granted and later removed leaves no trace. And audit records outlive directory objects, so a departed user is stored as `DirectoryStatus = 'NotFound'` with `IsCopilotLic` NULL: *not known*, which is a different claim from *unlicensed*.

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
| `sql/Create-CopilotFlattenedSchema.sql` | The flattened table, plus the optional licence lookup. |
| `sql/Create-CopilotFlattenProcedure.sql` | `audit.usp_FlattenCopilotAudit`. |
| `sql/Create-CopilotPurgeProcedure.sql` | `audit.usp_PurgeCopilotAuditJson`. |
| `sql/Create-CopilotEnrichmentSchema.sql` | Licence, administrative unit and user profile tables, plus the adoption views. |
| `tools/Save-CopilotAuditModules.ps1` | Builds the offline module bundle for a disconnected server. |
| `tools/Update-CopilotAuditEnrichment.ps1` | Fills those tables from Microsoft Graph. |
| `tests/` | Mock Graph service and the three test suites. |

## License

MIT — see [LICENSE](LICENSE).
