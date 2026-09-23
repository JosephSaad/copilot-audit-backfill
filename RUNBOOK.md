# Copilot audit backfill → SQL Server — runbook

Pulls every `CopilotInteraction` record for a date range out of Microsoft Purview and into SQL Server. Downloads and SQL loading run in parallel, with no CSV files in between. It stays inside Microsoft's published Audit Search API limits, and it resumes after any interruption.

```
Purview audit searches ──► parallel page downloads ──► bounded queue ──► parallel SqlBulkCopy
 (sized to the record cap,   (adaptive concurrency,                    (heap + TABLOCK,
  within the daily budget)    honours Retry-After)                      bulk-update locks)
                        state for every window: audit.CopilotBackfillWindow
```

## 1. Before the first run

**Check how much history actually exists.** Microsoft keeps Copilot audit records for **180 days**. The one-year default retention in Audit (Premium) covers only Entra, Exchange, OneDrive and SharePoint records. More than 180 days exists only if a *custom* audit retention policy covered Copilot at the time, and those policies are not retroactive. In Exchange Online PowerShell:

```powershell
Get-UnifiedAuditLogRetentionPolicy | Format-List Name, RecordTypes, Operations, RetentionDuration, Priority
```

If no policy mentions Copilot, ask for 180 days, not 365. Older windows would just come back empty.

**The sign-in account needs both of these (one without the other returns 403):**

| Requirement | Where |
|---|---|
| `AuditLogsQuery.Read.All` delegated permission, admin-consented for *Microsoft Graph Command Line Tools* (or your own app, passed with `-ClientId`) | Entra admin center → Enterprise applications |
| **Audit Reader** or **Audit Manager** role | Purview portal → Roles and scopes |

**The server:**

- PowerShell **7.2+** (7.4 recommended). Windows PowerShell 5.1 won't work.
- `Install-Module Microsoft.Graph.Authentication -Scope CurrentUser`. That's the only module needed; everything else ships with PowerShell 7.
- Outbound HTTPS to `login.microsoftonline.com` and `graph.microsoft.com`. The system proxy is used, authenticated with your Windows identity.
- An interactive desktop session (RDP) for the browser sign-in, or use `-AuthMode DeviceCode` and sign in from any other device.

**SQL Server:**

- SQL Server **2017+** for the finalize step (2016+ for loading alone). Database compatibility level **130+**.
- Permissions: `db_owner`, or `db_ddladmin` + `db_datareader` + `db_datawriter`.
- **Disk: plan ~1.5 KB per record in staging, about 75 GB per 50M records.** Each record's JSON is gzip-compressed before it reaches SQL. Microsoft's documented sample records measure 1.1–1.3 KB compressed, versus 2.5–9 KB raw. The run summary prints your actual ratio. Drop staging once the final tables are verified.
- Put the database in **SIMPLE** or **BULK_LOGGED** recovery for the load, so the parallel bulk inserts are minimally logged. Set it back afterwards if your standard requires it.

## 2. Smoke test: one day, into a scratch database

```powershell
.\Invoke-CopilotAuditBackfill.ps1 -SqlServer SQLDEV01 -Database CopilotAuditTest `
    -TenantId contoso.onmicrosoft.com `
    -StartDate (Get-Date).ToUniversalTime().Date.AddDays(-2) `
    -EndDate   (Get-Date).ToUniversalTime().Date.AddDays(-1) -Finalize
```

Compare the row count with the same day searched in the Purview portal before starting the full run.

## 3. Full run

```powershell
.\Invoke-CopilotAuditBackfill.ps1 -SqlServer SQLPROD01 -Database CopilotAudit `
    -TenantId contoso.onmicrosoft.com -StartDate (Get-Date).ToUniversalTime().Date.AddDays(-180) -Finalize
```

A browser window opens: sign in with username, password and MFA. The Graph SDK then refreshes the token silently. If a Conditional Access sign-in-frequency policy forces a fresh sign-in mid-run, the browser opens again. If nobody answers within 15 minutes, the run stops cleanly and resumes on the next start.

Every 30 seconds it prints a progress line:

```
 34% covered | windows 12 loaded, 8 searching, 5 downloading | 7 streams | 6,210 rows/s | 7,412,000 rows in SQL | 429s 2 | ETA 1h48m
```

- *searching*: audit searches running server-side.
- *streams*: current page-download concurrency; it adapts on its own.
- *429s*: throttled requests. A few are normal; the engine backs off and waits them out.

**Throughput** depends on how quickly Graph serves record pages, which Microsoft doesn't publish. The progress line gives a real figure within minutes. As a rough model: 8 streams × ~1,000 records per page, at 1–2 s per page, is 4,000–8,000 records/s. At ~2M records a week, 180 days is ~50M records: roughly 2–3.5 hours if Graph doesn't throttle paging, and ~85 searches, well inside the daily budget.

## 4. Stopping, resuming, checking

- **Ctrl+C, a crash, a reboot:** run the same command again. Dates are remembered in SQL, so a resume doesn't need them. Searches that already finished server-side are reused rather than resubmitted.
- **Check progress from another window:** add `-Status`.
- **Extend further back later:** pass an earlier `-StartDate`. A later `-EndDate` needs a separate database; the ongoing feed is the right tool for new data anyway.

## 5. Finalize, and what you get

`-Finalize` (or `-FinalizeOnly` later) refuses to run unless the whole range is loaded with no gaps. It then builds:

| Object | What it is |
|---|---|
| `audit.CopilotAudit` | Every record once: final attempt per window, boundary duplicates removed. Clustered columnstore. Full JSON kept compressed in `AuditDataGz`. |
| `audit.vCopilotAuditRecord` | The same records in the **Purview export's shape**: `RecordId, CreationDate, RecordType, Operation, UserId, AuditData, AssociatedAdminUnits`, with `AuditData` decompressed. SQL written against the portal CSV import can point here. |
| `audit.CopilotInteraction` | Slim reporting table: `ActivityDate`, `UserPrincipalName`, `AppHost`, `AppIdentity`, `ModelName`, `ModelProvider`, `ThreadId`, `PromptCount`, `AccessedResourceCount`, `UsedWebSearch`. |
| `audit.vCopilotDailyUsage` | Interactions, active users, prompts and web-grounded interactions per day per app. Ready for Power BI. |

`ActivityDate` uses **Eastern time** for day boundaries (`@ReportingTimeZone` at the top of `sql/Finalize-CopilotAudit.sql`; it handles daylight saving). Every other timestamp stays UTC.

**Before repointing existing views at `vCopilotAuditRecord`**, take one record from the smoke test and diff its `AuditData` against the same record in a portal CSV export. This pipeline reads the Graph API's `auditData`, and I could not verify against a live tenant that its JSON is byte-for-byte identical to the portal export's.

```sql
-- Monthly active users by app
SELECT DATEFROMPARTS(YEAR(ActivityDate), MONTH(ActivityDate), 1) AS Month, AppHost,
       COUNT(DISTINCT UserPrincipalName) AS ActiveUsers, COUNT(*) AS Interactions
FROM audit.CopilotInteraction
GROUP BY DATEFROMPARTS(YEAR(ActivityDate), MONTH(ActivityDate), 1), AppHost
ORDER BY Month, ActiveUsers DESC;
```

Note that the audit record doesn't contain prompt or response text. That needs the Copilot interaction export API or eDiscovery.

## 6. How it stays inside Microsoft's limits

| Published limit (Graph throttling docs) | What the engine does |
|---|---|
| ~1,000,000 records per search, after which it stops silently and still reports `succeeded` | Sizes each search to ~600K from observed density. Reads the service's `isRecordCountLimitExceeded` and splits any over-cap search **before downloading it**. If that flag is unavailable, it detects a capped result by row count. |
| ~200 search submissions per rolling 24 h, **tenant-wide** | Caps itself at 150/24 h (`-MaxSubmissionsPer24h`), counted across restarts, which leaves headroom for colleagues. Honours `Retry-After` on any submission 429. |
| ~50 queued/running searches tenant-wide; 10 per user in the portal | 8 in flight (`-MaxQueriesInFlight`). |
| Page throttling, rate not published | Starts at 4 streams, adds one after each run of clean pages, halves on a 429, and pauses every stream for `Retry-After`. |

## 7. Tuning

| Parameter | Default | Change when |
|---|---|---|
| `-MaxDownloadStreams` | 8 | Raise to 12–16 if the progress line shows no 429s and SQL keeps up. |
| `-InitialWindowHours` | 24 | Only matters until density is known. Lower it if a single day exceeds ~1M records. |
| `-TargetRecordsPerWindow` | 600,000 | Lower it if searches keep splitting. |
| `-MaxSubmissionsPer24h` | 150 | Lower it if other teams run audit searches through the API. |
| `-SqlWriters` / `-BatchRows` | 4 / 2,000 | Raise writers if rows/s written lags rows/s downloaded. Memory is roughly writers × batch × 8 KB. |

## 8. Troubleshooting

| Symptom | Cause and fix |
|---|---|
| `403` at the first submission | Missing Purview audit role or Graph consent. See section 1. |
| `Signed in ... but the token lacks AuditLogsQuery.Read.All` | Admin consent not granted for the app. |
| SQL certificate error | Self-signed certificate on the instance: add `-TrustServerCertificate`. |
| `Submission budget reached` | Normal. Downloads continue; submissions resume on their own when the 24 h window rolls. |
| Old windows load 0 rows | Retention boundary (section 1). The summary reports the oldest record found. |
| `window(s) failed after retries` | Run the same command again. Failed windows are retried; nothing else is redone. |
| Finalize refused | It names the reason: unfinished windows, gaps, or compatibility level. |

## Files

| File | Purpose |
|---|---|
| `Invoke-CopilotAuditBackfill.ps1` | The script you run. |
| `CopilotAuditEngine.cs` | The pipeline engine, compiled in memory at start (no binaries shipped). |
| `Import-CopilotAuditEngine.ps1` | Compiles the engine. |
| `sql/Create-CopilotAuditSchema.sql` | DDL, run automatically. Provided for DBA review. |
| `sql/Finalize-CopilotAudit.sql` | Dedupe, columnstore, reporting tables and the export-shaped view. |
| `tests/` | Mock Graph service plus test suites (`pwsh tests/Test-Engine.ps1`, `pwsh tests/Test-Orchestrator.ps1`; needs `python3`). |
