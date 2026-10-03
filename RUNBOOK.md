# Copilot audit backfill → SQL Server — runbook

Pulls every `CopilotInteraction` record for a date range out of Microsoft Purview and into SQL Server. Downloads and SQL loading run in parallel, with no CSV files in between. It stays inside Microsoft's published Audit Search API limits, and it resumes after any interruption.

```
Purview audit searches ──► parallel page downloads ──► bounded queue ──► parallel SqlBulkCopy
 (sized to the record cap,   (adaptive concurrency,                    (heap + TABLOCK,
  within the daily budget)    honors Retry-After)                      bulk-update locks)
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

`-Finalize` (or `-FinalizeOnly` later) refuses to run unless the whole range is loaded with no gaps. It also refuses (2.4.12) once the staging table no longer holds every row the windows loaded: it is dropped after finalize (the advice finalize ends with) or purged by `-Task PurgeJson`, and rebuilding from it would empty `audit.CopilotAudit`. A backfill run again over a completed range leaves tables already finalized alone (`audit.PipelineSetting` `FinalizedUtc`), so `-History` carries on past it; `-FinalizeOnly` still builds them again from a whole staging table. It then builds:

| Object | What it is |
|---|---|
| `audit.CopilotAudit` | Every record once: final attempt per window, boundary duplicates removed. Clustered columnstore. Full JSON kept compressed in `AuditDataGz`. |
| `audit.vCopilotAuditRecord` | The same records in the **Purview export's shape**: `RecordId, CreationDate, RecordType, Operation, UserId, AuditData, AssociatedAdminUnits`, with `AuditData` decompressed. SQL written against the portal CSV import can point here. |
| `audit.CopilotInteraction` | Slim reporting table: `ActivityDate`, `UserPrincipalName`, `AppHost`, `AppIdentity`, `ModelName`, `ModelProvider`, `ThreadId`, `PromptCount`, `AccessedResourceCount`, `UsedWebSearch`. |
| `audit.vCopilotDailyUsage` | Interactions, active users, prompts and web-grounded interactions per day per app. Ready for Power BI. |

`ActivityDate` uses the **reporting time zone** for day boundaries, with daylight saving: the zone stored in the database, which `SELECT audit.ReportingTimeZone();` shows. `Install-CopilotAudit.ps1 -ReportingTimeZone` sets it; left out, a new database gets the installing server's own zone (2.4.10), and one the installer never set falls back to Eastern time. `@ReportingTimeZone` at the top of `sql/Finalize-CopilotAudit.sql` is only that fallback: the stored zone replaces it, so editing it changes nothing. Every other timestamp stays UTC.

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

## 5b. Keeping it current

After the backfill is finalized, schedule `-Incremental`. Each run:

1. Picks up an unfinished load if the previous run stopped, otherwise starts a new one from where the data ends (the last load's end, or the backfill's) **minus `-LookbackHours`** (48 by default) up to the start of the current hour. Microsoft commits to no delivery time for audit records - core services typically take 60-90 minutes, others longer - so the lookback is searched again.
2. Downloads it with the same engine into `audit.CopilotLoadWindow` / `audit.CopilotLoadStage`.
3. Runs `audit.usp_MergeCopilotLoad`: checks the load covers its range with no gap, appends the records `audit.CopilotAudit` does not already hold (tagged with `LoadId`) and their shredded rows to `audit.CopilotInteraction` in one transaction, and empties the load tables. One merge runs at a time, a CSV import's included, so two at once never append the same record twice (2.4.12).
4. Refreshes the derived tables that are installed: `usp_FlattenCopilotAudit` and `usp_ShredLicenseEvents` from the load's start, then `usp_BuildCopilotSessions` from local midnight of the load's start's day, then `bi.usp_RefreshBi`. `-SkipPostProcessing` stops after the merge. A refresh that is skipped or stops part way is owed: `audit.CopilotLoad.DerivedRefreshedUtc` stays empty, and the next run makes it up from the earliest load or CSV import that owes one, even with nothing new to load (2.4.12; before, a failure that lasted past the 48-hour lookback left those days out of sessions, `bi` and the dashboard for good). A run that refreshes from more than 4 days back, the first good night after two or more missed ones or one that makes up an owed refresh from further back (its log says *The derived tables were not brought up to date after an earlier load or import: refreshing them from* that date), rewrites days the model's scheduled refresh does not reload: refresh the Power BI model in full afterwards (section 5c).

**Sessions are rebuilt incrementally.** A session never crosses midnight, so only the days from the load's start can change: those sessions are replaced, numbering carries on from the ones kept, and the weeks containing those days are recomputed. The result equals a full rebuild over the same rows, except that sessions already built keep the department and license flag they were built with. It matters at scale: rebuilding every session took 32 minutes and 25 GB of tempdb at 40M interactions, and grows with every day of history kept. Prompts are put in order, and the 60-minute gap measured, in UTC, and days are cut in the reporting time zone, so a daylight-saving change neither joins, splits nor reorders sessions (2.4.12). That changes only the sessions in the hours around a change; `-Task RebuildSessions` applies it to those built before.

Rebuild every session (`.\tools\Invoke-CopilotMaintenance.ps1 -SqlServer <server> -Task RebuildSessions`) after changing a session setting - timeout, context fallback, time zone, metric version or license databases. An incremental build refuses to mix settings, so the nightly load fails with `different settings` until you do. The same license databases in another order or spacing are the same setting (2.4.12). For the nightly run to read several, list them as `SessionLicenseDatabases` in `copilot-audit.settings.psd1` ([DEPLOYMENT.md](docs/DEPLOYMENT.md), step 11). `tools\Export-CopilotSessionCsv.ps1 -Build` rebuilds these same sessions: it uses the last build's settings, and refuses others without `-Force` (2.4.12). After upgrading to any new release, run `.\Install-CopilotAudit.ps1 -SqlServer <server>` once, with the nightly task disabled: it upgrades every object in the databases it is given (`-Database`, `-LicenseDatabase`; the first tenant's by default) and does whatever one-off rebuild the release needs. With several tenants, run it again for each further tenant's pair, `-Database CopilotAudit_Fabrikam -LicenseDatabase CopilotLicenseEvents_Fabrikam` (or `-SkipLicenseDatabase` for a tenant without one; [OPERATIONS.md](docs/OPERATIONS.md), *Upgrades and renewals*, loops over the settings as the nightly run reads them): nothing else upgrades them, and the nightly run keeps calling the procedures each database already has. The pipeline, the installer and the maintenance rebuilds hold the database while they work, so one started while another runs stops at once, saying so (2.4.12).

`audit.CopilotLoad` keeps one row per load with its range and how many records were new or already there; `-Status` lists the latest. Once a load has been merged, `-Finalize` refuses to run: it rebuilds from the backfill's staging table and would drop the loaded days.

**Unattended sign-in.** A scheduled task has nobody to sign in, so use an app registration:

1. Entra admin center → App registrations → New registration. Single tenant; no redirect URI.
2. API permissions → Add a permission → Microsoft Graph → **Application** permissions → the permissions below → **Grant admin consent** (Global Administrator or Privileged Role Administrator).
3. Certificates & secrets → upload the public key of a certificate held in the task account's certificate store. The scripts sign in with a certificate only (`-CertificateThumbprint`); there is no client-secret option.
4. Run with `-TenantId <tenant> -ClientId <app id> -CertificateThumbprint <thumbprint>`.

**Every permission, for one app registration that runs everything.** Three Microsoft Graph **application** permissions, all read-only:

| Permission | Used by | Graph calls | What for |
|---|---|---|---|
| `AuditLogsQuery.Read.All` | `Invoke-CopilotAuditBackfill.ps1`: the backfill, `-Incremental`, and license-event databases | `POST` and `GET /security/auditLog/queries`, `GET .../records` | Copilot interaction records and the Entra license events. The per-workload permissions (`AuditLogsQuery-Entra.Read.All`, `-Exchange`, `-SharePoint`, `-OneDrive`, `-CRM`, `-Endpoint`) include none for Copilot, so the all-services one is needed. |
| `User.Read.All` | `tools/Update-CopilotAuditEnrichment.ps1` | `GET /subscribedSkus` (the Copilot SKUs and their service plans), `GET /users/{upn}?$select=userPrincipalName,assignedLicenses,assignedPlans`, `GET /users?$filter=assignedLicenses/any(...)` | Each user's Copilot license and since when, and license holders who never used Copilot (`-AllLicensedUsers`). Microsoft's license-only alternative, `GET /users/{id}/licenseDetails`, does not support application permissions. |
| `LicenseAssignment.Read.All` | `tools/Update-CopilotAuditEnrichment.ps1` | `GET /subscribedSkus` | Which of the tenant's SKUs are Copilot. The least-privileged permission Microsoft lists for this call. |

- The backfill and loads need only `AuditLogsQuery.Read.All`; the other two are for the license tool. To keep the audit app to a single permission, put those two on a second app registration instead.
- User attributes - title, department, division, office, manager and the org chain - come from on-premises AD (section 5d), which uses the task account's domain identity and **no Graph permission**. Before 2.0.0 they came from Graph, and needed `Organization.Read.All` and `AdministrativeUnit.Read.All` too; remove those two from an existing app registration.
- For app-only access, Microsoft documents only the Graph permission. The **Audit Reader** / **Audit Manager** role in section 1 applies to a person signing in, not to the app.
- Not needed on the app: SQL Server access (the task account's Windows or SQL login, section 1), the Power BI gateway and semantic model (they connect to SQL with their own credentials), and the dashboard.
- Signing in interactively (no `-CertificateThumbprint`) uses *delegated* permissions instead: the same names under **Delegated**, on *Microsoft Graph Command Line Tools* by default or on your own app with `-ClientId`, plus the Purview audit role for the person.

**The scheduled task.** `tools\Install-CopilotJobServer.ps1` registers it: *Copilot audit - nightly*, daily, as the service account, running `Invoke-CopilotPipeline.ps1`. That script runs the directory load, the license tool and both `-Incremental` loads, in order, from `copilot-audit.settings.psd1`. The account needs `db_owner` (or the rights in section 1) and read access to the certificate's private key, and the installer grants both. A group managed service account avoids storing a password at all; for any other account, pass `-ServiceAccountCredential`. Schedule the Power BI refresh after the task. A failed run exits non-zero, so the task history shows it; the next run resumes the same load, and makes up any refresh of the derived tables the failed run left owed.

## 5c. The Power BI star schema

The `bi` schema is what a Power BI semantic model imports: narrow facts with integer keys (`bi.Interactions`, `bi.AccessedResources`, `bi.Sessions`, `bi.LicenseChanges`, `bi.LicensedDays`) and their dimensions. The README's [Power BI section](README.md#power-bi-the-bi-schema) describes the model.

**Install and build it once:** `.\Install-CopilotAudit.ps1 -SqlServer <server> -LicenseEventDatabases <license database(s)>` installs it with everything else and builds it the first time there is data. It reports what it wrote; `bi.RefreshRun` keeps one row per refresh.

**Keeping it current.** Once the procedure is installed, every `-Incremental` run calls it last, with the load's start as `@SinceUtc` and `-SessionLicenseDatabase` as `@LicenseEventDatabase`. A partial refresh replaces its window in one transaction, so a model refreshing at the same time never sees it half-written. Sessions follow the session builds: only the days an incremental build rebuilt are copied, and after a full session build (which renumbers every session) all of them. Schedule the model's refresh after the task.

**Run a full rebuild** (`@SinceUtc = NULL`, then a full refresh of the model) when:

- the reporting time zone changes. A partial refresh in a different zone is refused, because rows already written carry the old zone's dates.
- license history arrives for a period already built. A row's license status is fixed when it is written.
- flattened rows are purged (`usp_PurgeCopilotAuditJson @PurgeFlattened = 1`, or `-Task PurgeJson -DeleteRows`). The fact keeps them until rebuilt.
- the flattened records were re-flattened in full, as the upgrade to 2.2.0 asks. The facts carry what the flattening wrote when they were built.

The dashboard's next rollup after a full rebuild covers every day, whatever window it is asked for (2.4.12), so the rollups follow the rebuilt facts; before, only the window did, and older days kept the figures of the facts before it. Two refreshes never run at once (2.4.12): one that starts while another runs waits for it to finish. A full rebuild holds nothing on the fact between its months, so before, a refresh that started meanwhile emptied the fact under it and both filled it, counting those months twice.

**Refresh the model fully, without a rebuild,** when an app's category changes (Admin › Reference data, or `-Task SetAppCategory`). The next `bi` refresh sets `PremiumUse` again on every past interaction of that app and tells the dashboard to roll up from its first day, but the model's incremental refresh reloads only the last 4 days, so its older partitions keep the old flags until a full refresh. The same goes after an `-Incremental` run that refreshed from more than 4 days back (section 5b, item 4).

**What a full refresh of the model is.** The model's refresh policy (`Interactions` and `AccessedResources`: 2 years kept, the last 4 days refreshed) applies to *Refresh now* and to the scheduled refresh, which reload those 4 days and leave older partitions alone. A full refresh loads every partition again. On Premium, Premium Per User or Fabric capacity it is a refresh with `applyRefreshPolicy` false, through the enhanced refresh REST API or TMSL over the XMLA endpoint; on Pro, publish the model again from Desktop and refresh it, since a republished model's next refresh loads the whole history ([DEPLOYMENT.md](docs/DEPLOYMENT.md), step 16).

**Flattening (2.2.0).** `audit.usp_FlattenCopilotAudit` writes every informative field of each record into `audit.RawLogFlattened`, and a row per accessed resource and context into `audit.RawLogAccessedResource` and `audit.RawLogContext`, replacing a record's element rows each time it is flattened. The fields that never vary are counted in `audit.RawLogRecordShape` instead of stored; if the flatten says one of them *now carries information*, look there, and add a column if the difference matters. A full re-flatten (`-Task Reflatten`) is safe after a JSON purge: records with no JSON left are skipped.

**Agents and their names (2.4.11).** A record names its agent in `AppIdentity` (`Copilot.Studio.<...>` for an agent built in Copilot Studio or Agent Builder, `MicrosoftAgent.<...>` for one of Microsoft's), and carries the agent's id and friendly name at the top of the record (`AgentId`, `AgentName`), on some records and not on others. The flatten reads both into `audit.RawLogFlattened` and keeps `audit.AgentCatalog`: every agent the log names, with the name its latest record gave it. `bi.usp_NameAgents`, at the end of every `bi` refresh, names `bi.DimAgent` from the catalog; an agent the log never names gets a name read from its id (a Copilot Studio agent's schema name, Microsoft's own name for one of its agents), and `NamedInAudit` says which.

- **An agent is its id, never its name.** `bi.DimAgent` keys on the `AppIdentity` that names the agent, and on `AgentId` only where `AppIdentity` names none (`bi.fnAgentIdentity`). The name is a label on that key. When a maker renames an agent, the night its first record under the new name arrives the same agent is relabeled, in every period; nothing is split and nothing is rebuilt.
- **A name an administrator gives** (Admin › Reference data › *Agents*, or `-Task NameAgent`) shows on the dashboard instead of the log's, and stays when the maker renames the agent.
- **Records flattened before 2.4.11** have no id or name: the flatten looked for them inside `CopilotEventData`, where no record has them. `Install-CopilotAudit.ps1` reads them once on the upgrade (`audit.usp_BackfillAgentNames`: the records of agents only, from their stored JSON, in minutes), and leaves a mark so it is not done twice. A record whose JSON was purged keeps what it has; its agent is still named, from any other record of the same agent.
- **Another app's records that carry an agent's id** (Copilot Chat running a declarative agent, `AppIdentity` `Copilot.MicrosoftCopilot.BizChat` with an `AgentId`) are keyed on that id. 2.4.11 read it from its upgrade night on, so such an agent's history started that night. 2.4.12's installer reads them once (`audit.usp_BackfillAgentNames @Others = 1`), from the apps that carry one: those whose records flattened since show it, and those among the newest 50,000 records still without one whose JSON has it. When any record gains an id it rebuilds `bi` and every day of the dashboard's rollups once, since those interactions change agent, and says to refresh the Power BI model in full. An app none of whose recent records carries an agent's id is not read; `-Task Reflatten` reads everything.

A full rebuild commits a month at a time to keep the log small, so don't refresh the model while one runs.

**The gateway's access.** The Power BI gateway's SQL login needs to read the views and nothing else: `.\Install-CopilotAudit.ps1 -SqlServer <server> -ReportingAccount 'DOMAIN\svc-pbigateway'` grants it, through the role `CopilotReporting` (2.4.12; before, it read the whole `bi` schema, tables included, so give it once more).

## 5d. The user directory, from on-premises AD

`tools/Export-UsersWithHierarchy-ToSql.ps1` loads `audit.UserDirectory`: every user in a trusted domain, with title, department, division, office, city, country, company, employee type, manager, the management chain and whether the account is enabled. Sessions, the `bi` schema's user dimension (as `OrgLevel1`..`OrgLevel15`) and the adoption views read it. Licenses stay with the Graph license tool.

**Where it runs.** On a machine that can reach a domain controller in the trusted domain, as an account the trust lets read user objects (by default any authenticated user can). Windows PowerShell 5.1 or PowerShell 7; nothing to install. It needs no Graph permission and no app registration.

```powershell
.\tools\Export-UsersWithHierarchy-ToSql.ps1 -TargetDomain corp.example.com -SqlServer SQLPROD01 -Database CopilotAudit
```

- Every account is loaded, enabled or not, with `AccountEnabled`. The audit data covers months, and a user who has since left still has interactions to attribute.
- `-EmployeeTypes` (default `EMP`, `NONW`) chooses which `employeeType` codes to load and `-EmployeeTypeMap` what to call them (default `EMPLOYEE`, `CONTRACTOR`). Pass `-EmployeeTypes @()` for every type. Managers are resolved for the chain whatever their type. Check your directory's codes before the first load (`Get-ADUser -Filter * -Properties employeeType | Group-Object employeeType`): a directory with other codes, or none, loads few users under the default. The nightly run takes `EmployeeTypes` from the settings file, so set it there too.
- `-MaxLevels` (1-15) keeps that many levels of the chain from the top. `OrgDepth` is always the full depth.
- The table is replaced in one transaction, so reports never see it empty.

**Check the match on the first run.** Everything joins on the UPN. The load ends by reporting how many users in the audit data it found by UPN, and how many more only by mail. Anything under 90% means AD's UPNs are not the names users sign in to Microsoft 365 with (common when the AD forest is not the one synced to Entra), and their usage will show without a department.

**Schedule it before the incremental load**, daily, so the load's session and `bi` rebuilds pick up today's organization: first this, then the license tool, then `-Incremental`.

## 5e. Importing Purview CSV exports

`tools/Import-CopilotAuditCsv.ps1` loads audit log exports from the Purview portal into `audit.CopilotAudit` and `audit.CopilotInteraction`, in the shape the Graph backfill stores: the object ID in `UserId`, the sign-in name in `UserPrincipalName`, the workload as `Service`, the user type by name, and the `AuditData` JSON compressed exactly as the engine compresses it. The flattened table, sessions and the `bi` schema therefore read them like any other record.

```powershell
.\tools\Import-CopilotAuditCsv.ps1 -Path D:\PurviewExports -SqlServer SQLPROD01 -Database CopilotAudit `
    -SessionLicenseDatabase CopilotLicenseEvents
```

**Adding operations to a load (2.3.0).** A database records the operations its backfill asked for, and every `-Incremental` load must ask for the same ones or more. 2.3.0 adds Copilot Studio's authoring operations (`BotCreate`, `BotUpdateOperation-BotPublish`, `BotUpdateOperation-BotShare`, `BotDelete`, `BotComponent*` ...) to the license-event load: the first 2.3.0 run records the wider set and logs *Loading ... too from now on*. Earlier records of the new operations are not fetched into that database. A load asking for fewer operations, or different ones, is still refused (*holds a backfill of ..., not ...*).

**Exporting.** In the Purview portal, search the audit log for the record type the database holds (*CopilotInteraction*, or the license operations for a license-event database) and export all results. Note each search's exact start and end: the importer cannot tell a quiet day from a day that was not exported. Leave the files as they came: a file opened and saved in Excel has its `AuditData` cut at 32,767 characters, and those rows are rejected rather than loaded broken. Both the current export's columns (`RecordId`, `CreationDate`, `RecordType`, `Operation`, `UserId`, `AuditData`, ...) and the older one's (`UserIds`, `Operations`) are read.

**What a run does.**

1. Runs `sql/Create-CopilotAuditSchema.sql` and `sql/Create-CopilotCsvImport.sql`. In a database with no backfill, the second creates the final tables and views that finalize would.
2. Lists the files (`-Filter`, default `*.csv`; `-Recurse` for subfolders) and skips any whose SHA-256 an earlier import already loaded, and any repeating another file in the folder.
3. Reads each file here, streaming it in batches into `audit.CopilotCsvStage`. SQL Server needs no access to the folder. Rows of another operation are counted and left out, matched the way the Graph search's operation filter matches (`Change user license.` counts as `Change user license`). Rows that cannot be loaded go to `audit.CopilotCsvReject` with their line number and reason.
4. `audit.usp_MergeCopilotCsv` keeps one row per `RecordId` across the files, drops those `audit.CopilotAudit` already holds, appends the rest (tagged with `CsvImportId`) and their shredded rows, and records the covered range if given - in one transaction, one merge at a time with the nightly load's.
5. Refreshes the derived tables that are installed from the earliest record added: flattening, license events, sessions, `bi`. Sessions and `bi` read the license databases this database's last build read unless `-SessionLicenseDatabase` names others (2.4.12; left out, it read none, which a database built with license history refused). `-SkipPostProcessing` stops after the merge. A refresh skipped that way, or one that fails, is owed (`audit.CopilotCsvImport.DerivedRefreshedUtc` stays empty): the next run of the importer without the switch, even with no new files, or the next `-Incremental` run makes it up from the import's earliest record (2.4.12). If a Power BI model refreshes incrementally and the import added records older than its refresh window, refresh the model fully once (section 5c, *What a full refresh of the model is*).

`audit.CopilotCsvImport` keeps one row per run with its counts, and `audit.CopilotCsvFile` one row per file. A run that stops part-way is started again from scratch the next time; nothing is merged until every file is read.

**A database that starts from CSV files.** Create it (section 1), then run the importer with `-CoveredThroughUtc`, the end of the latest export's search range, and optionally `-CoveredFromUtc`. The import records that range in `audit.CopilotBackfillRange` with `Source = 'CSV'`, and `-Incremental` continues from its end, less the lookback, like after a backfill. A later import can widen a CSV-recorded range; a range the Graph backfill recorded is never changed. Without `-CoveredThroughUtc` the rows load, but `-Incremental` has nowhere to continue from until an import records a range. The Graph API keeps 180 days: if the exports end earlier than that, the importer warns, and the days between cannot be filled.

**Alongside the Graph backfill.** Import at any time once the backfill is finalized, before or after `-Incremental` loads begin - older exports can extend history back past the 180 days the API holds. The importer refuses while a backfill has started but is not finalized: finalize rebuilds `audit.CopilotAudit` from its own staging table. For the same reason finalize refuses once CSV rows are merged.

**Different from the Graph path.** `ClientIp` is filled from `AuditData.ClientIP`, which the Graph path leaves empty; the export's `AssociatedAdminUnitsNames` is not kept. Everything the reports read is the same.

## 5f. The Copilot Insights dashboard

A web dashboard in IIS (`dashboard/CopilotInsights`), reading `bi` and its own `dash` schema. [docs/DASHBOARD.md](docs/DASHBOARD.md) has the pages, the security model and the install; [docs/DEPLOYMENT.md](docs/DEPLOYMENT.md) steps 22-24 the order.

- **Refresh:** `dash.usp_RefreshDashboard @SinceUtc` runs last in every `-Incremental` run and CSV import, after `bi.usp_RefreshBi`, from the same day. It rebuilds the org chart (`dash.Person`, `dash.OrgClosure`) from `audit.UserDirectory` in full each time, and the daily rollups (`dash.UserDay`, `dash.ResourceDay`) from that day, or every day when a full `bi` refresh has run since the last rollup (2.4.12). While a full `bi` rebuild has not finished (`bi.RebuildInProgress` has a row) it leaves the rollups as they are and says so: the facts are half written. `.\tools\Invoke-CopilotMaintenance.ps1 -Task RefreshDashboard` rebuilds everything; so does Admin › Health › Rebuild.
- **Who sees what:** the person in `dash.Setting` `OrgTopUpn` and their direct reports see the whole organization, as does anyone granted Organization; everyone else sees themselves and the people under them. Named lists need a persona role. Admin alone shows no data.
- **Locked out:** the installer's `-AdminPrincipal` (`Insights:BootstrapAdmins` in `appsettings.Production.json`) are always administrators. Failing that, run the installer again with `-AdminPrincipal`, or `.\tools\Invoke-CopilotMaintenance.ps1 -SqlServer <server> -Task GrantRole -Principal 'CORP\Copilot-Insights-Admins' -Role Admin`.
- **Who looked at what:** `dash.AuditLog`, or Admin › Audit log.

| Symptom | Cause and fix |
|---|---|
| Everyone sees only their own use, even executives | No top of the organization: none is set and no one person has half the directory under them, or the one set is not in the directory (Admin › Health says which). Set it in Admin › Access, or `-Task SetOrgTop -Upn ceo@...`; it applies at once. |
| Someone sees nothing: "not in the directory" | Their sign-in did not resolve to a UPN in `audit.UserDirectory`: another domain, a new starter before tonight's AD export, or an account AD cannot resolve (map it under `Insights:IdentityMap`). |
| The browser keeps asking for a password | The host name is not in the Local intranet zone, or its SPN is missing or on the wrong account (`setspn -Q HTTP/<host>`). |
| HTTP 500.19 or 500.30 from IIS | The ASP.NET Core 10 Hosting Bundle is missing, or was installed before IIS: repair it and restart IIS. |
| Every page: *Something went wrong. The page could not be built* | The error is in the web server's Windows Application log (source *.NET Runtime* or *IIS AspNetCore Module V2*). Most often the SQL connection: *certificate chain … not trusted* needs `-TrustServerCertificate` (or `TrustServerCertificate=true` in `appsettings.Production.json`, then restart the app pool); *Invalid object name 'dash.…'* needs `Install-CopilotAudit.ps1 -Dashboard`; *Login failed* or *permission was denied* needs the grants file run. |
| Installer: `The ASP.NET Core 10 Hosting Bundle is not installed` | Needed even with the pre-built zip. Download the *Hosting Bundle* for .NET 10 and pass it with `-HostingBundlePath`. |
| Installer: `dotnet.exe : The command could not be loaded` | `-PackagePath` is the source folder and the server has no .NET 10 SDK. Pass the release's `copilot-insights-v<version>.zip` instead. |
| Installer: `No certificate <thumbprint> in LocalMachine\My` | The certificate isn't on the web server. From 2.4.12 the installer drops the invisible characters the certificate dialog copies, and checks the certificate before it takes the site offline. |
| Every page: *Copilot Insights is being updated* long after an install | An installer before 2.4.12 stopped part way and left `app_offline.htm` in the site's folder. Delete it, or run the installer again. From 2.4.12 a failed run always brings the site back. |
| Installer: `Incorrect function. (Exception from HRESULT: 0x80070001)` at `AddSslCertificate` | Most likely, before 2.4.1: a host-name HTTPS binding made on the first run lacked SNI while another certificate held port 443. Remove the binding (`Remove-WebBinding -Name CopilotInsights -Protocol https -Port 443 -HostHeader <name>`) and run the installer again. |
| Browser: `ERR_CONNECTION_REFUSED` | Nothing listens for that address and port. Check the name, then `Get-WebBinding -Name CopilotInsights`. Before 2.4.1, running the installer again without a certificate added no HTTP binding to a site first made with HTTPS. |
| Admin › Health: *404.0 - Not Found* from *IIS Web Core*, while the other Admin pages work | Before 2.4.4, Health was at the bare `/admin`, which a server-wide URL Rewrite or Request Filtering rule often blocks (`Select-String "$env:windir\System32\inetsrv\config\applicationHost.config" -Pattern 'admin'` finds it). From 2.4.4 it is at `/admin/health`. On an older release, ask the server team to exclude the site from the rule. |
| Browser reaches another site | A binding on the server's IP address outranks the dashboard's. See [Another site on port 443](docs/DASHBOARD.md#another-site-on-port-443). |
| A page is slow at a long period | Check `dash.RefreshRun` ran after the last `bi` refresh, and that the `dash` indexes exist (re-run the schema script). |

## 6. How it stays inside Microsoft's limits

| Published limit (Graph throttling docs) | What the engine does |
|---|---|
| ~1,000,000 records per search, after which it stops silently and still reports `succeeded` | Sizes each search to ~600K from observed density. Reads the service's `isRecordCountLimitExceeded` and splits any over-cap search **before downloading it**. If that flag is unavailable, it detects a capped result by row count. |
| ~200 search submissions per rolling 24 h, **tenant-wide** | Caps itself at 150/24 h (`-MaxSubmissionsPer24h`), counted across restarts, which leaves headroom for colleagues. Honors `Retry-After` on any submission 429. |
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
| `A connection was successfully established with the server, but then an error occurred during the login process` | The words in brackets after it name the cause. *certificate chain … not trusted*: add `-TrustServerCertificate`. *target principal name is incorrect*: use the server's name as its certificate carries it. *forcibly closed by the remote host*: a TLS mismatch, usually Windows PowerShell 5.1 against a SQL Server that requires TLS 1.2; run the script in PowerShell 7. See [DEPLOYMENT.md](docs/DEPLOYMENT.md#connecting-to-sql-server). |
| `The login already has an account under a different user name` | Before 2.4.1: the service account is already in the database under another name, usually `dbo` because it created and so owns the database. From 2.4.1 the installers use that user. On an older release, run `Install-CopilotAudit.ps1` without `-ServiceAccount` (an owner has every right already), and the job-server setup with `-SkipDatabase`. |
| `#Requires` … `7.2` / `The term 'pwsh' is not recognized` | The script needs PowerShell 7, or it isn't installed. Install it next to Windows PowerShell 5.1; see [Which PowerShell runs what](docs/DEPLOYMENT.md#which-powershell-runs-what). |
| `Submission budget reached` | Normal. Downloads continue; submissions resume on their own when the 24 h window rolls. |
| Old windows load 0 rows | Retention boundary (section 1). The summary reports the oldest record found. |
| `window(s) failed after retries` | Run the same command again. Failed windows are retried; nothing else is redone. The run fails, so `-History` stops at that step instead of carrying on (2.4.12). |
| `Another run is loading [audit].[...Window] in this database now` | A second download into the same table, beside one still running (2.4.12): it stopped before touching the table. Let the first finish, or stop it, then run again. |
| `Another run is using <database> now` | The nightly pipeline, the installer or a maintenance rebuild holds the database (2.4.12); this run changed nothing. Run it again when the other has finished. Admin › Health shows the pipeline's runs. |
| Finalize refused | It names the reason: unfinished windows, gaps, compatibility level, or incremental loads already merged. |
| `-Incremental`: `Run a backfill to completion in this database first` | Loads continue from a finalized backfill. Run one (even a single day) and `-FinalizeOnly`. |
| `-Incremental`: `Merge refused: ... gap(s)` | A load's windows do not cover its range. Before 2.4.12 a split that stopped part way, or two runs at once, could leave such a hole, and a load already marked downloaded is only merged again, never downloaded, so the next run does not mend it. Inspect `audit.CopilotLoadWindow`, then start the load again with `DELETE FROM audit.CopilotLoad WHERE MergedUtc IS NULL;`: the next run plans a new one from where the merged data ends. |
| `-Incremental`: `The last session build used different settings` | A session setting changed, or a session export's `-Build -Force` used another one. Rebuild every session once (section 5b); the next load continues incrementally, and makes up the refreshes the failed nights owed. |
| `bi.usp_RefreshBi`: `needs a full rebuild` | The reporting time zone differs from the last refresh. Run `.\tools\Invoke-CopilotMaintenance.ps1 -SqlServer <server> -Task RebuildBi` once in the new zone. |
| `-Incremental` app-only: `token lacks the application permission` | The app has the delegated permission, or consent was not granted. Add `AuditLogsQuery.Read.All` under **Application** permissions and grant consent. |
| License tool app-only: `403` / `Authorization_RequestDenied` | `User.Read.All` or `LicenseAssignment.Read.All` is missing or not consented. See section 5b. |
| User directory: `Could not read RootDSE` | The domain name is wrong, the trust does not reach it, or the account running the script is not trusted there. Try `-TargetDomain` with a domain controller's name. |
| User directory: `Cannot process argument transformation on parameter 'Group'` | Before 2.4.1, whenever the number of AD groups granted Organization isn't exactly one: none (always so without the dashboard), or two or more. The users were already loaded; the group step failed, and with it the nightly run. Fixed in 2.4.1. |
| User directory: far fewer users than AD has | The `employeeType` filter: only `EMP` and `NONW` by default. Count your codes and pass `-EmployeeTypes` (see section 5d). |
| User directory: `TargetDomain … is the domain this script runs in` | The load reads a trusted domain, not the server's own. Run it from a machine in another domain. |
| User directory: under 90% of audit users matched | AD's UPNs are not the names users sign in to Microsoft 365 with. The load reports how many match by mail instead. See section 5d. |
| CSV import: `A Graph backfill has started in this database but is not finalized` | Finish the backfill and run `-FinalizeOnly`, then import. See section 5e. |
| CSV import: `row(s) were not loaded` | See `audit.CopilotCsvReject` for each row's file, line and reason. Usually a file saved from Excel: export it again and leave it unopened. |
| CSV import: `No AuditData column` | A file in the folder is not a Purview audit log export. Move it out, or narrow `-Filter`. |
| Finalize: `CSV imports have been merged` | The tables are already final, and a rebuild would drop the imported rows. Keep them current with `-Incremental` and further imports. |

## Files

| File | Purpose |
|---|---|
| `Invoke-CopilotAuditBackfill.ps1` | The script you run. |
| `CopilotAuditEngine.cs` | The pipeline engine, compiled in memory at start (no binaries shipped). |
| `Import-CopilotAuditEngine.ps1` | Compiles the engine. |
| `sql/Create-CopilotAuditSchema.sql` | DDL, run automatically. Provided for DBA review. |
| `sql/Finalize-CopilotAudit.sql` | Dedupe, columnstore, reporting tables and the export-shaped view. |
| `sql/Create-CopilotLoadProcedure.sql` | `audit.usp_MergeCopilotLoad`, which appends each `-Incremental` load. Installed automatically. |
| `sql/Create-CopilotBiSchema.sql` / `sql/Create-CopilotBiProcedure.sql` | The `bi` star schema and `bi.usp_RefreshBi` (section 5c). |
| `tools/Export-UsersWithHierarchy-ToSql.ps1` | The user directory from on-premises AD (section 5d). |
| `sql/Create-CopilotDashboardSchema.sql` / `sql/Create-CopilotDashboardProcedure.sql` | The dashboard's `dash` schema and `dash.usp_RefreshDashboard` (section 5f). |
| `dashboard/` | The Copilot Insights web dashboard, its IIS installer, demo and tests (section 5f). |
| `tools/Update-CopilotAuditEnrichment.ps1` | Copilot licenses, from Microsoft Graph. |
| `tools/Import-CopilotAuditCsv.ps1` / `sql/Create-CopilotCsvImport.sql` | Purview CSV exports into the same tables (section 5e). |
| `tests/` | Mock Graph service plus test suites (`pwsh tests/Test-Engine.ps1`, `pwsh tests/Test-Orchestrator.ps1`; needs `python3`). |
