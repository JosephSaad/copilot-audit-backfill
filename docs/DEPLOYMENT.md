# Deploying the Copilot audit pipeline

From an empty SQL Server to a Power BI model refreshed every night, with a Copilot agent that answers questions about it. Microsoft 365 Copilot audit records come from Purview, licences from Microsoft Graph, and the organisation from on-premises Active Directory.

Written for a new install of release 2.0.0. Hands-on time is about a day, plus approvals. [RUNBOOK.md](../RUNBOOK.md) has the reference detail: limits, tuning and the full troubleshooting table.

- [What you're deploying](#what-youre-deploying)
- [Decide these first](#decide-these-first)
- [Phase 1: Prepare](#phase-1-prepare) (steps 1-4)
- [Phase 2: Load the history](#phase-2-load-the-history) (steps 5-7)
- [Phase 3: Build the reporting layers](#phase-3-build-the-reporting-layers) (steps 8-11)
- [Phase 4: Run it every night](#phase-4-run-it-every-night) (steps 12-13)
- [Phase 5: Connect Power BI](#phase-5-connect-power-bi) (steps 14-16)
- [Phase 6: Add the Copilot agent in Power BI](#phase-6-add-the-copilot-agent-in-power-bi) (steps 17-20)
- [Check it's healthy](#check-its-healthy) (step 21)
- [Running it](#running-it)
- [What has run in the lab](#what-has-run-in-the-lab)
- [Not yet proven in a live deployment](#not-yet-proven-in-a-live-deployment)

## What you're deploying

Seven parts, all read-only towards Microsoft 365. Everything lands in two SQL Server databases. A Power BI model imports the reporting views from one of them, and Copilot in Power BI answers questions from that model.

```mermaid
flowchart LR
  P["Purview audit log<br/>(Audit Search API)"] -->|"Copilot interactions"| A[("CopilotAudit")]
  P -->|"licence operations"| L[("CopilotLicenseEvents")]
  G["Microsoft Graph<br/>users and SKUs"] -->|"current licences"| A
  D["On-premises AD<br/>trusted domain"] -->|"users and org chain"| A
  L -->|"licence history"| A
  A -->|"bi views, via gateway"| B["Power BI<br/>semantic model"]
  B --> C["Copilot in Power BI<br/>agent"]
```

Only the Purview and Graph arrows need the app registration.

| Component | What it does | Runs on | Writes |
|---|---|---|---|
| `Invoke-CopilotAuditBackfill.ps1` | Downloads audit records through the Purview Audit Search Graph API and bulk-loads them. With `-Incremental` it keeps them current and refreshes everything derived from them. | Job server, PowerShell 7.2+ | `audit.CopilotAudit`, `audit.CopilotInteraction` |
| The same script, second database | Collects the licence operations (*Change user license*, *Update user*, *Add user*, *Delete user*). They are the only record of licence history. | Job server | `audit.LicenseEvent`, `audit.vLicenseSkuChange` |
| `tools/Export-UsersWithHierarchy-ToSql.ps1` | Reads every user in a trusted AD domain: title, department, division, office, manager, management chain, whether the account is enabled. | A machine that reaches the trusted domain; PowerShell 5.1 or 7 | `audit.UserDirectory` |
| `tools/Update-CopilotAuditEnrichment.ps1` | Who holds a Copilot licence now, and since when. | Job server | `audit.CopilotLicense` |
| SQL procedures | Flatten the records, infer sessions and the Saturday–Friday weekly rollup, and fill the `bi` star schema. | SQL Server 2017+ | `audit.RawLogFlattened`, `audit.CopilotSession`, `audit.CopilotWeeklyRollup`, `bi.*` |
| Power BI | Imports the `bi` views through the on-premises data gateway, refreshing only recent hours of the interaction fact. | Power BI service | — |
| Copilot in Power BI | Answers plain-language questions from the model: in the report's Copilot pane, and in the standalone Copilot (preview). The model carries the instructions and verified answers that ground it. | A paid Fabric (F2+) or Premium (P1+) capacity | — |

## Decide these first

Each is awkward to change after the first load. The examples below use the names in `code`; replace them with yours.

- **SQL Server and the two databases.** `SQLPROD01`, `CopilotAudit` and `CopilotLicenseEvents`. They must be separate databases: the loader refuses to mix operation sets in one.
- **The job server.** One Windows server that runs every scheduled step. It must reach `login.microsoftonline.com` and `graph.microsoft.com` over HTTPS, the SQL Server, and a domain controller of the domain your users are in. The AD export reads a *trusted* domain and refuses the domain the server itself is joined to, so the users must live in a different domain from the job server.
- **The service account.** A group managed service account (gMSA), so there is no password to store or rotate. This guide uses `CORP\gmsa-copilot$`. The AD trust must let it read user objects (by default any authenticated user can).
- **The reporting time zone.** Days and Saturday–Friday weeks are cut in Eastern time (`Eastern Standard Time`, which follows daylight saving). For another zone, change the `@ReportingTimeZone` default in five scripts *before step 6*: `Finalize-CopilotAudit.sql`, `Create-CopilotLoadProcedure.sql`, `Create-CopilotSessionProcedure.sql`, `Create-CopilotBiProcedure.sql` and `Create-LicenseEventProcedure.sql`. Use a name from `sys.time_zone_info`.
- **How much history.** Microsoft keeps Copilot audit records for 180 days. More exists only if a custom retention policy covered Copilot at the time (step 1).
- **Capacity for the Copilot agent.** Copilot in Power BI needs a paid Fabric (F2+) or Premium (P1+) capacity. Outside the US or EU data boundary it also needs a tenant setting that lets data be processed in another region (step 17). Buying capacity and that residency decision both take time, so start them now.
- **Which employee types to load.** The AD export loads `employeeType` codes `EMP` and `NONW` by default, written as EMPLOYEE and CONTRACTOR. Check the codes your directory uses; `-EmployeeTypes @()` loads every type.

## Phase 1: Prepare

Retention, the app registration, the job server and SQL Server. The admin consent and the gMSA usually take the longest to get, so ask for them first.

### Step 1. Check how much history exists

*Exchange Online PowerShell, compliance admin. About 10 minutes.*

```powershell
Get-UnifiedAuditLogRetentionPolicy |
    Format-List Name, RecordTypes, Operations, RetentionDuration, Priority
```

If no policy mentions Copilot, plan for 180 days. Asking for more returns empty windows, not an error.

### Step 2. Register the app

*Entra admin center; Global Administrator or Privileged Role Administrator to consent. About 20 minutes.*

1. **App registrations → New registration.** Single tenant, no redirect URI. Note the *Application (client) ID* and *Directory (tenant) ID*.
2. **API permissions → Add a permission → Microsoft Graph → Application permissions.** Add the three below, then **Grant admin consent**. They must be *Application*, not *Delegated*.
3. **Certificates & secrets → Certificates → Upload** the `.cer` file you create in step 3. The scripts sign in with a certificate only; there is no client-secret option.

| Permission | Used by | For |
|---|---|---|
| `AuditLogsQuery.Read.All` | The loader: backfill and `-Incremental`, both databases | Copilot interaction records and licence operations. Microsoft offers no Copilot-only audit permission, so the all-services one is needed. |
| `User.Read.All` | Licence tool | Each user's assigned licences and plans, and the list of licence holders who never used Copilot. |
| `LicenseAssignment.Read.All` | Licence tool | The tenant's SKUs, to find the Copilot ones. |

The app needs no Purview role: the Audit Reader role applies to a person signing in, not to an app. User attributes come from AD, which needs no Graph permission. To keep the audit app to a single permission, put the two licence permissions on a second app registration instead.

### Step 3. Prepare the job server

*Job server, as a local administrator. About 45 minutes.*

**PowerShell 7 and the release.** Install PowerShell 7.4 (7.2 is the minimum). Then take `copilot-audit-backfill-v2.0.0-offline-win-x64.zip` and its `.sha256` from the [v2.0.0 release](https://github.com/JosephSaad/copilot-audit-backfill/releases/tag/v2.0.0). The offline zip already contains the only external module, `Microsoft.Graph.Authentication`, next to the scripts. That matters here: a module installed for your own account would be invisible to the gMSA.

```powershell
$zip = 'copilot-audit-backfill-v2.0.0-offline-win-x64.zip'
(Get-FileHash ".\$zip" -Algorithm SHA256).Hash   # must equal the first value in "$zip.sha256"
Unblock-File ".\$zip"
Expand-Archive ".\$zip" -DestinationPath 'D:\'
Rename-Item 'D:\copilot-audit-backfill-v2.0.0-offline-win-x64' 'CopilotAudit'
New-Item -ItemType Directory 'D:\CopilotAudit\logs' | Out-Null
```

**The certificate.** In an elevated window. A certificate from your own PKI works as well, as long as its private key is in the machine store.

```powershell
$cert = New-SelfSignedCertificate -Subject 'CN=Copilot audit pipeline' `
    -CertStoreLocation Cert:\LocalMachine\My -KeySpec Signature -KeyLength 2048 `
    -KeyExportPolicy NonExportable -NotAfter (Get-Date).AddYears(2)
Export-Certificate -Cert $cert -FilePath 'D:\CopilotAudit\copilot-audit.cer'
$cert.Thumbprint   # goes in every command below as -CertificateThumbprint
```

Upload `copilot-audit.cer` to the app (step 2). Then let the service account use the private key: **certlm.msc → Personal → Certificates**, right-click the certificate, **All Tasks → Manage Private Keys**, add `CORP\gmsa-copilot$` with *Read*.

**The service account.**

- Install the gMSA on the server (`Install-ADServiceAccount`; `Test-ADServiceAccount` returns True).
- Give it **Log on as a batch job** (Local Security Policy → User Rights Assignment), so it can run a scheduled task.
- Check the server reaches `login.microsoftonline.com` and `graph.microsoft.com` on 443, through the system proxy if there is one.

### Step 4. Prepare SQL Server

*SQL Server, as a sysadmin. About 30 minutes.*

SQL Server 2017 or later, database compatibility level 130 or higher. SIMPLE recovery keeps the parallel bulk loads minimally logged. `CopilotAuditTest` is only for the smoke test in step 5; drop it afterwards.

```sql
CREATE DATABASE CopilotAudit;          ALTER DATABASE CopilotAudit         SET RECOVERY SIMPLE;
CREATE DATABASE CopilotLicenseEvents;  ALTER DATABASE CopilotLicenseEvents SET RECOVERY SIMPLE;
CREATE DATABASE CopilotAuditTest;      ALTER DATABASE CopilotAuditTest     SET RECOVERY SIMPLE;

CREATE LOGIN [CORP\gmsa-copilot$] FROM WINDOWS;
USE CopilotAudit;         CREATE USER [CORP\gmsa-copilot$]; ALTER ROLE db_owner ADD MEMBER [CORP\gmsa-copilot$];
USE CopilotLicenseEvents; CREATE USER [CORP\gmsa-copilot$]; ALTER ROLE db_owner ADD MEMBER [CORP\gmsa-copilot$];
```

Your own account needs the same rights in all three databases for the one-off steps. `db_ddladmin` + `db_datareader` + `db_datawriter` also works instead of `db_owner`. The service account needs its rights in `CopilotLicenseEvents` too, because the session and `bi` builds read the licence history across databases.

**How much space.** Measured in a scale test with 70,000 users, 18,000 of them licensed, and 40 million interactions:

| What | Size at 40M interactions |
|---|---:|
| Backfill staging (about 1.5 KB per record; can be dropped once the finalized tables are checked) | ≈ 60 GB |
| `audit.RawLogFlattened`, plus its index | 15.3 GB + 5.6 GB |
| `audit.CopilotSession` | 2.3 GB |
| `audit.CopilotWeeklyRollup` | 1.9 GB |
| `bi` interaction fact (columnstore) | 1.2 GB |
| tempdb free space for the first full session build (step 11) | ≈ 25 GB |

The finalized `audit.CopilotAudit` is a compressed columnstore that keeps each record's JSON; it was not measured separately. Leave room for it on top of the table.

## Phase 2: Load the history

Run these from a console on the job server, as yourself. Everything signs in as the app, so nothing waits on a browser. Every command here is safe to run again: after Ctrl+C, a crash or a reboot, the same command resumes where it stopped.

### Step 5. Smoke-test one day

*Job server. About 15 minutes.*

```powershell
cd D:\CopilotAudit
$app = @{ TenantId = 'contoso.onmicrosoft.com'; ClientId = '<app-id>'; CertificateThumbprint = '<thumbprint>' }

.\Invoke-CopilotAuditBackfill.ps1 -SqlServer SQLPROD01 -Database CopilotAuditTest @app `
    -StartDate (Get-Date).ToUniversalTime().Date.AddDays(-2) `
    -EndDate   (Get-Date).ToUniversalTime().Date.AddDays(-1) -Finalize
```

Then compare the count with the same UTC day searched in the Purview portal (**Audit → New search**, record type *CopilotInteraction*):

```sql
SELECT COUNT(*) FROM CopilotAuditTest.audit.CopilotAudit;
```

If they match, drop `CopilotAuditTest`. A `403` means the permission or its consent is missing (step 2). A SQL certificate error means the instance presents a self-signed certificate; add `-TrustServerCertificate`.

### Step 6. Backfill the Copilot interactions

*Job server. Roughly 2–3½ hours per 50M records.*

> **Time zone first.** If you are not using Eastern time, edit the five scripts listed under [Decide these first](#decide-these-first) now. Finalizing writes the day boundaries.

```powershell
.\Invoke-CopilotAuditBackfill.ps1 -SqlServer SQLPROD01 -Database CopilotAudit @app `
    -StartDate (Get-Date).ToUniversalTime().Date.AddDays(-180) -Finalize `
    -LogPath D:\CopilotAudit\logs\backfill.log
```

Every 30 seconds it prints coverage, rows per second and an ETA. `-Status` from a second window shows saved progress without disturbing the run. How fast it goes depends on how fast Graph serves pages. At about 2 million records a week, 180 days is roughly 50 million records and about 85 of the tenant's 200 daily searches. The loader keeps itself under 150 searches per 24 hours, so colleagues who also search the audit log still have room.

`-Finalize` refuses to run until the whole range is loaded with no gaps. It then builds the deduplicated `audit.CopilotAudit` and the reporting table `audit.CopilotInteraction`.

### Step 7. Backfill the licence history

*Job server and SQL Server. About 30 minutes.*

Graph only knows the licences users hold today. Who held what, and when, comes from the directory's own audit records, loaded into the second database with a different operation list:

```powershell
$licenceOps = 'Change user license', 'Update user', 'Add user', 'Delete user'

.\Invoke-CopilotAuditBackfill.ps1 -SqlServer SQLPROD01 -Database CopilotLicenseEvents @app `
    -Operations $licenceOps -StartDate (Get-Date).ToUniversalTime().Date.AddDays(-180) -Finalize
```

Then install the licence-event objects in that database and shred what was loaded. Use sqlcmd as below, or open each script in SSMS; some contain `GO`, so they need one of those two.

```powershell
sqlcmd -S SQLPROD01 -d CopilotLicenseEvents -E -b -i sql\Create-LicenseEventSchema.sql
sqlcmd -S SQLPROD01 -d CopilotLicenseEvents -E -b -i sql\Create-LicenseEventProcedure.sql
sqlcmd -S SQLPROD01 -d CopilotLicenseEvents -E -b -Q "EXEC audit.usp_ShredLicenseEvents;"
```

Add `-C` to sqlcmd if the instance's certificate is self-signed. From now on this database must always be loaded with the same `-Operations`; the loader refuses anything else.

## Phase 3: Build the reporting layers

Install the SQL objects, load the two directory sources, then build everything once. After this, the nightly job only rebuilds the days that changed.

### Step 8. Install the reporting objects

*Job server or SSMS. About 5 minutes.*

In `CopilotAudit`, in this order. Each script is safe to run again.

```powershell
$scripts = 'Create-CopilotFlattenedSchema.sql',  'Create-CopilotFlattenProcedure.sql',
           'Create-CopilotPurgeProcedure.sql',   'Create-CopilotEnrichmentSchema.sql',
           'Create-CopilotSessionSchema.sql',    'Create-CopilotSessionProcedure.sql',
           'Create-CopilotBiSchema.sql',         'Create-CopilotBiProcedure.sql'
foreach ($s in $scripts) {
    sqlcmd -S SQLPROD01 -d CopilotAudit -E -b -i "D:\CopilotAudit\sql\$s"
    if ($LASTEXITCODE) { throw "$s failed" }
}
```

The order matters in three places: the enrichment script creates the adoption views only once the finalized tables exist, it makes a column the flattened schema creates nullable, and the `bi` schema needs the flattened and session tables. The purge procedure is optional; it only runs when you call it.

### Step 9. Load the user directory from AD

*Job server; no Graph permission. About 5–15 minutes.*

```powershell
.\tools\Export-UsersWithHierarchy-ToSql.ps1 -TargetDomain corp.example.com `
    -SqlServer SQLPROD01 -Database CopilotAudit
```

One paged search reads every user in the trusted domain, enabled or not, since a user who has left still has interactions to attribute. It replaces `audit.UserDirectory` in one transaction, so readers never see it empty. It binds to AD as whoever runs it, over the trust.

> **Read the match report.** It ends by saying how many users in the audit data it found by UPN, and how many more only by mail. Everything joins on the UPN. Under 90% means AD's UPNs are not the names people sign in to Microsoft 365 with, and their usage will show without a department. Resolve that before going further.

It also reports the accounts it skipped: other employee types, no UPN, a UPN over 256 characters, or a duplicate UPN.

### Step 10. Load current Copilot licences

*Job server. About 5–20 minutes.*

```powershell
.\tools\Update-CopilotAuditEnrichment.ps1 -SqlServer SQLPROD01 -Database CopilotAudit @app -AllLicensedUsers
```

Writes `audit.CopilotLicense`: the Copilot SKU, since when, and whether the user still exists in Entra. `-AllLicensedUsers` adds licence holders who have never used Copilot, which is what the "licensed, never used" report needs. A user who has since been deleted is stored as *not known*, which is a different claim from *unlicensed*.

A good run lists the tenant's Copilot SKUs, how many users it looked up, and how many are licensed. From the lab run:

```text
[INFO ] Copilot SKUs in this tenant: Microsoft_365_Copilot, MICROSOFT_365_COPILOT_FOR_BUSINESS, MICROSOFT_DRAGON_COPILOT_USCAN.
[INFO ] 12 distinct user(s) appear in the audit data.
[INFO ] 14 user(s) after adding licence holders with no interactions.
[INFO ] Licences written: 14.
[INFO ] Enrichment complete: 10 licensed user(s) recorded.
```

People who appear only in the licence history, whose licence was removed and who never used Copilot, aren't looked up. `Users[CopilotLicenceToday]` shows them as *Unknown*. Their licence changes and licensed days are still counted.

### Step 11. Build everything once

*SSMS. About 40 minutes for sessions and `bi` at 40M interactions, plus flattening.*

```sql
USE CopilotAudit;
EXEC audit.usp_FlattenCopilotAudit;                                       -- every record, with the licence flag
EXEC audit.usp_BuildCopilotSessions @LicenceEventDatabase = N'CopilotLicenseEvents';
EXEC bi.usp_RefreshBi               @LicenceEventDatabase = N'CopilotLicenseEvents';
```

Each reports progress as it goes. In the 40-million-interaction scale test the full session build took 25 minutes and the first `bi` build 13½ minutes. Both are one-off costs: the nightly job rebuilds only from the start of the day its load began. A full `bi` build commits a month at a time, so don't refresh a Power BI model while one runs.

## Phase 4: Run it every night

Four steps, in this order, each finishing before the next starts:

1. **User directory from AD**, so tonight's session and `bi` builds pick up today's organisation.
2. **Copilot licences from Graph**: current licence holders and since when.
3. **Licence events**: `-Incremental` into `CopilotLicenseEvents`; it shreds the new events itself.
4. **Copilot interactions**: `-Incremental` into `CopilotAudit`. It loads from where the data ends, minus a 48-hour lookback for records Purview delivers late. Then it refreshes the flattened table, sessions and `bi`, reading the licence history the step before brought up to date.

### Step 12. Write the nightly script

*Job server. About 15 minutes.*

Save as `D:\CopilotAudit\Run-Nightly.ps1`. The script isn't in the release; it's shown here in full. Each step runs in its own PowerShell process and appends to a dated log. The script stops at the first failure and exits 1, so Task Scheduler's history shows it. Tomorrow's run resumes the same load.

```powershell
# Run-Nightly.ps1 - the four nightly steps in order; stops at the first failure.
$ErrorActionPreference = 'Stop'
$root = 'D:\CopilotAudit'
$log  = "$root\logs\nightly-$(Get-Date -Format yyyy-MM-dd).log"

$sql = 'SQLPROD01'
$app = @{ TenantId = 'contoso.onmicrosoft.com'; ClientId = '<app-id>'; CertificateThumbprint = '<thumbprint>' }

function Invoke-Step([string] $Name, [string] $Script, [hashtable] $Arguments) {
    "$(Get-Date -Format s) == $Name" | Tee-Object -FilePath $log -Append
    # A separate process per step, so one step's modules and state cannot affect the next.
    & pwsh -NoProfile -NonInteractive -Command { param($s, $a) & $s @a } -args $Script, $Arguments 2>&1 |
        Tee-Object -FilePath $log -Append
    if ($LASTEXITCODE -ne 0) {
        "$(Get-Date -Format s) $Name failed (exit code $LASTEXITCODE)." | Tee-Object -FilePath $log -Append
        exit 1
    }
}

Invoke-Step 'User directory from AD' "$root\tools\Export-UsersWithHierarchy-ToSql.ps1" @{
    TargetDomain = 'corp.example.com'; SqlServer = $sql; Database = 'CopilotAudit' }

Invoke-Step 'Copilot licences from Graph' "$root\tools\Update-CopilotAuditEnrichment.ps1" (@{
    SqlServer = $sql; Database = 'CopilotAudit'; AllLicensedUsers = $true } + $app)

Invoke-Step 'Licence events' "$root\Invoke-CopilotAuditBackfill.ps1" (@{
    SqlServer = $sql; Database = 'CopilotLicenseEvents'; Incremental = $true
    Operations = 'Change user license', 'Update user', 'Add user', 'Delete user' } + $app)

Invoke-Step 'Copilot interactions' "$root\Invoke-CopilotAuditBackfill.ps1" (@{
    SqlServer = $sql; Database = 'CopilotAudit'; Incremental = $true
    SessionLicenceDatabase = 'CopilotLicenseEvents' } + $app)

"$(Get-Date -Format s) Done." | Tee-Object -FilePath $log -Append
```

Run it once by hand before scheduling it. The script's control flow was tried with stand-in steps; running it by hand is the first time it runs against your tenant and AD.

```powershell
pwsh -NoProfile -File D:\CopilotAudit\Run-Nightly.ps1; $LASTEXITCODE   # 0 when every step succeeded
```

### Step 13. Schedule it

*Job server, elevated. About 10 minutes.*

As the gMSA, so there is no password to store. `-LogonType Password` is how Task Scheduler is told to fetch a gMSA's password from AD.

```powershell
$action    = New-ScheduledTaskAction -Execute 'pwsh.exe' -WorkingDirectory 'D:\CopilotAudit' `
                 -Argument '-NoProfile -NonInteractive -File "D:\CopilotAudit\Run-Nightly.ps1"'
$trigger   = New-ScheduledTaskTrigger -Daily -At 2am
$principal = New-ScheduledTaskPrincipal -UserId 'CORP\gmsa-copilot$' -LogonType Password
Register-ScheduledTask -TaskName 'Copilot audit - nightly' -Action $action -Trigger $trigger -Principal $principal

Start-ScheduledTask -TaskName 'Copilot audit - nightly'
(Get-ScheduledTaskInfo -TaskName 'Copilot audit - nightly').LastTaskResult   # 0 once it has finished
```

A daily run costs 2–4 of the tenant's roughly 200 audit searches per 24 hours. At 40 million interactions, the SQL work after the download (sessions and `bi`) took about 74 seconds, against 31 minutes for rebuilding everything.

## Phase 5: Connect Power BI

The model imports the `bi` views: narrow facts with integer keys, and small dimensions.

### Step 14. Give the gateway read access

*SQL Server. About 5 minutes.*

The on-premises data gateway's login needs the `bi` views and nothing else:

```sql
CREATE LOGIN [CORP\svc-pbigateway] FROM WINDOWS;   -- if it has no login yet
USE CopilotAudit;
CREATE USER [CORP\svc-pbigateway];
GRANT SELECT ON SCHEMA::bi TO [CORP\svc-pbigateway];
```

### Step 15. Build the model

*Power BI Desktop. About 2–3 hours.*

**Get data → SQL Server**, server `SQLPROD01`, database `CopilotAudit`, **Import**. Select these views:

| View | One row per | Relates on |
|---|---|---|
| `bi.Interactions` | Copilot interaction | DateKey, UserKey, AppKey, AgentKey, ModelKey, LicenceStatusKey |
| `bi.Sessions` | inferred session | DateKey, UserKey, AppKey, LicenceStatusKey |
| `bi.LicenceChanges` | SKU added (+1) or removed (−1) | DateKey, UserKey, SkuKey |
| `bi.LicensedDays` | user holding a Copilot licence at the end of a day | DateKey, UserKey |
| `bi.Calendar` | date, with the Saturday–Friday week | DateKey |
| `bi.Users`, `bi.Apps`, `bi.Agents`, `bi.Models`, `bi.Skus`, `bi.LicenceStatuses` | dimension rows | their key |

1. **Relationships:** each fact's key to the dimension with the same key, one-to-many, single direction.
2. **Mark `Calendar` as the date table**, on its `Date` column.
3. **Incremental refresh on `Interactions`:** create the `RangeStart` and `RangeEnd` date/time parameters and filter `ActivityHour` between them (on or after `RangeStart`, before `RangeEnd`). Refresh at least the last 4 days: each night reloads from about 72 hours back, the 48-hour lookback plus the day since the last run. `ActivityHour` is local time cut to the hour, so it costs almost nothing in memory. The other facts are small enough to refresh whole.
4. **Measures**, as a starting point:

```dax
Interactions = COUNTROWS ( Interactions )
Prompts = SUM ( Interactions[PromptCount] )
Active users = DISTINCTCOUNT ( Interactions[UserKey] )
Active licensed users = CALCULATE ( [Active users], LicenceStatuses[LicenceStatus] = "Licensed" )
Licensed users = CALCULATE ( DISTINCTCOUNT ( LicensedDays[UserKey] ),
                     LASTNONBLANK ( Calendar[Date], CALCULATE ( COUNTROWS ( LicensedDays ) ) ) )
Adoption % = DIVIDE ( [Active licensed users], [Licensed users] )
```

**Licence at the time of use** is on the facts: *Licensed*, *Not licensed*, or *Unknown* when there is neither licence history nor a current licence record. `Users[CopilotLicenceToday]` answers a different question. Slice by organisation with `Users[OrgLevel1]` to `[OrgLevel15]`, where level 1 is the top of the chain.

### Step 16. Publish and schedule the refresh

*Power BI service. About 30 minutes.*

1. Publish to the workspace. On the gateway, add a SQL Server data source for `SQLPROD01` / `CopilotAudit` with the account from step 14, and map the semantic model to it.
2. Schedule the refresh after the nightly job has finished, for example 05:00 against a 02:00 job. Check the job's usual duration in its logs and leave a margin.
3. The first refresh loads the whole history; later ones reload only the incremental window.

## Phase 6: Add the Copilot agent in Power BI

Copilot in Power BI answers questions like "how many people used Copilot last week?" by writing DAX against the model. It needs no code from this project. How good its answers are depends almost entirely on how well the model explains itself, so most of this phase is about describing the model to it. Requirements are as Microsoft documents them in September 2026; parts of Copilot in Power BI are still in preview.

### Step 17. Turn Copilot on for the workspace

*Fabric admin portal, Fabric administrator. About 30 minutes, plus purchasing.*

| Requirement | Detail |
|---|---|
| Capacity | The workspace from step 16 on a **paid** Fabric capacity (F2 or larger) or Power BI Premium (P1 or larger). Trial capacities don't work, and neither do Pro or Premium Per User licences alone. A Fabric Copilot capacity can carry the Copilot usage instead. On capacities smaller than F64, everyone who opens the report still needs a Power BI Pro licence. |
| Tenant setting | **Users can use Copilot and other features powered by Azure OpenAI**, enabled at the tenant level and scoped to a security group of the people you've trained. |
| Standalone Copilot | **Users can access a standalone, cross-item Power BI Copilot experience (preview)**, if people should ask from Power BI's left-hand navigation rather than only inside the report. The same setting enables the Power BI agent in Microsoft 365. |
| Region | If the tenant or capacity is outside the US or EU data boundary, Canada for example, Copilot stays off until an admin enables **Data sent to Azure OpenAI can be processed outside your tenant's geographic region, compliance boundary, or national cloud instance**. That's a data-residency decision for the client's compliance team, not only a switch. |
| Network | Copilot isn't supported with Private Link or in closed network environments. |

Copilot's usage is billed against the capacity, alongside the model's refreshes and queries. Watch the Fabric Capacity Metrics app during the first weeks.

### Step 18. Make the model explain itself

*Power BI Desktop. About 1–2 hours.*

The `bi` views already give Copilot a star schema with readable table names. Three more things help it most:

- **Hide every key column** (`DateKey`, `UserKey`, `AppKey` and the rest) and `ActivityHour`. Copilot should reach them only through relationships.
- **Turn on Q&A** for the model. "Prep data for AI" in the next step requires it.
- **Write descriptions.** Copilot reads only the first 200 characters of each, so put the definition first. These matter most:

| Object | Description |
|---|---|
| Interactions (table) | One row per Microsoft 365 Copilot prompt and response, from the Purview audit log. Contains no prompt or response text. |
| Sessions (table) | One row per session: a person's prompts with less than 60 minutes between them, within one Eastern-time day. |
| LicensedDays (table) | One row per person per day on which they held a Copilot licence at the end of the day. Use for licensed-user counts on a date. |
| LicenceStatuses[LicenceStatus] | Whether the person held a Copilot licence when the interaction happened: Licensed, Not licensed, or Unknown (no record either way). |
| Users[CopilotLicenceToday] | Today's Copilot licence. Not for past periods; use LicenceStatus for those. |
| Calendar[WeekEnding] | The Friday that ends the date's Saturday-to-Friday week. Weeks in this model are always Saturday to Friday. |
| [Active users] | People with at least one Copilot interaction in the period. |
| [Adoption %] | Active licensed users divided by people holding a Copilot licence at the end of the period. |

Add synonyms where people's words differ from the model's: "prompts" and "uses" for Interactions, "seats" for licences, "team" and "org" for Department.

### Step 19. Prep data for AI

*Power BI Desktop → Home → Prep data for AI. About 1–2 hours.*

Everything set here is saved on the semantic model, not on the report, so every Copilot surface uses it. That includes a Fabric data agent: its DAX generation ignores the agent's own instructions and reads these instead.

**AI data schema.** On **Simplify data schema**, include the tables Copilot should use and leave out what would confuse it: the key columns, `ActivityHour`, and the `OrgLevel` columns deeper than your organisation goes (`OrgDepth` shows how deep that is). Include the measures from step 15.

**AI instructions.** A starting point, well inside the 10,000-character limit. Change the time zone line if you changed yours, and add your own organisation's vocabulary.

```text
This model describes how people in the organisation use Microsoft 365 Copilot,
from the Microsoft Purview audit log, with each person's Copilot licence and
their place in the organisation.

Terms
- An interaction is one Copilot prompt and its response, as recorded in the audit
  log. Count them with the [Interactions] measure. [Prompts] sums the prompt count
  recorded on each interaction.
- The audit log does not contain prompt or response text. If asked what someone
  asked Copilot or what it answered, say that the text is not available.
- A session is a run of one person's interactions with less than 60 minutes
  between them, within one calendar day. A session's app is the app of its first
  prompt.
- An active user is a person with at least one interaction in the period.
- Weeks run Saturday to Friday and are named by the Friday. Use
  Calendar[WeekEnding] and Calendar[WeekLabel], never ISO weeks. "Last week" means
  the most recent complete Saturday-to-Friday week.
- All dates and hours are Eastern time.

Licences
- For whether someone was licensed when they used Copilot, use
  LicenceStatuses[LicenceStatus]: Licensed, Not licensed or Unknown. Unknown means
  there is no record either way; never count it as Not licensed.
- Users[CopilotLicenceToday] is today's licence only. Use it for "who has a
  licence now", not for past periods.
- Licensed users on a date come from the LicensedDays table. [Adoption %] is
  active licensed users divided by licensed users.
- "Licensed but not using Copilot" means people in LicensedDays for the period
  with no interactions in it.

Organisation
- Department, Division, JobTitle, Office and manager come from Active Directory.
- OrgLevel1 is the top of the management chain, OrgLevel2 the level below, and so
  on. The values are managers' display names. People "in X's organisation" are
  users where any OrgLevel column equals X.
- DirectoryStatus NotFound means the person is in the audit data but not in
  Active Directory, usually because they have left.

Apps and agents
- Apps[App] is where the prompt was made, for example Word, Outlook, Microsoft
  Teams or BizChat. Apps[AppCategory] groups apps into Copilot Chat and In-App
  Copilot.
- Agents[AgentName] is the Copilot Studio or Microsoft agent used. "(no agent)"
  means Copilot itself.

Freshness
- Data is loaded nightly. The latest two days can still grow as late audit
  records arrive.
```

**Verified answers.** Build a visual for each of the questions people will ask most, then **… → Set verified answer** on it with the phrases they'd use. Good first four:

| Visual | Trigger phrases |
|---|---|
| Line: [Active users] by Calendar[WeekLabel] | weekly active users; how many people use Copilot each week |
| Bar: [Adoption %] by Users[Department] | adoption by department; which teams use Copilot most |
| Table: users with CopilotLicenceToday = Licensed and no interactions in the period | who has a licence but isn't using it; unused Copilot licences |
| Bar: [Interactions] by Agents[AgentName], excluding (no agent) | most used agents; which Copilot agents are used |

### Step 20. Test, approve and share

*Power BI Desktop, then the service. About 1–2 hours.*

1. **Test in Desktop.** Open the Copilot pane and, from the skill picker, choose *Answer questions about the data*, which is what the standalone Copilot uses. Close and reopen the pane after each change to the instructions. *How Copilot arrived at this* under each answer shows the fields and filters it used.
2. **Check its numbers against SQL.** Ask the questions users will ask, and compare. For example, active users in the last complete week:

   ```sql
   SELECT c.WeekLabel, COUNT(DISTINCT i.UserKey) AS ActiveUsers, COUNT(*) AS Interactions
   FROM bi.Interactions AS i
   JOIN bi.Calendar AS c ON c.DateKey = i.DateKey
   WHERE c.WeekEnding = (SELECT MAX(WeekEnding) FROM bi.Calendar WHERE WeekEnding < CAST(GETDATE() AS date))
   GROUP BY c.WeekLabel;
   ```

3. **Publish** over the model from step 16, then in the service open the semantic model's **Settings → Approved for Copilot**, tick it and apply. The standalone Copilot then stops attaching its caution notice to answers from this model. It can take up to 24 hours to reach every report.
4. **Tell users what it can't do.** Answers can vary between identical questions. It can't see prompt text. The report is still the quicker way to read the standard figures.

## Check it's healthy

### Step 21. Check the first nightly run

*SSMS. About 10 minutes.*

```sql
USE CopilotAudit;
-- One row per load: its range, and how many records were new or already there
SELECT TOP (5) LoadId, RangeStartUtc, RangeEndUtc, MergedUtc, RowsAdded, RowsAlreadyLoaded
FROM audit.CopilotLoad ORDER BY LoadId DESC;

-- Session builds: RebuiltFrom is NULL for a full build, a date for a nightly one
SELECT TOP (5) RunId, RunStartUtc, RunEndUtc, RebuiltFrom, SessionsWritten
FROM audit.CopilotSessionRun ORDER BY RunId DESC;

-- bi refreshes: SinceUtc is NULL for a full rebuild
SELECT TOP (5) RunId, StartedUtc, EndedUtc, SinceUtc, InteractionRows, SessionRows
FROM bi.RefreshRun ORDER BY RunId DESC;

-- Licensed, never used: usually the number that pays for the rollout
SELECT UserPrincipalName, Department, LicensedSinceUtc
FROM audit.vCopilotAdoption WHERE NeverUsed = 1 ORDER BY LicensedSinceUtc;
```

A healthy morning: a new load with `MergedUtc` set, a session run with a `RebuiltFrom` date, a `bi` run with a `SinceUtc`, and a nightly log ending in *Done.* After that, the semantic model's refresh history shows a success, and Copilot's answer to "how many people used Copilot yesterday?" matches the same query in SQL.

## Running it

| When | Do this |
|---|---|
| A session setting changes (timeout, time zone, licence databases) | Rebuild every session once: `EXEC audit.usp_BuildCopilotSessions @LicenceEventDatabase = N'CopilotLicenseEvents';`. Until then the nightly run stops with *different settings*. |
| Licence history arrives for a period already built, or flattened rows are purged | `EXEC bi.usp_RefreshBi @SinceUtc = NULL, @LicenceEventDatabase = N'CopilotLicenseEvents';`, then a full refresh of the model. |
| A new Copilot app appears | Map it to *Copilot Chat* or *In-App Copilot* in `audit.SessionAppCategory`. Unmapped apps count as In-App. |
| Disk grows past your horizon | `EXEC audit.usp_PurgeCopilotAuditJson @RetainDays = 365;` reports what it would clear; add `@WhatIf = 0` to clear the stored JSON and keep the rows. |
| The certificate nears expiry | Create a new one (step 3), upload it to the app, give the gMSA its private key, and change the thumbprint in `Run-Nightly.ps1`. Remove the old one after the next good run. |

**When the nightly run fails:**

| Log says | Cause and fix |
|---|---|
| `token lacks the application permission` | The permission was added as Delegated, or consent wasn't granted. Add it under **Application** and grant consent. |
| Licence tool: `403` / `Authorization_RequestDenied` | `User.Read.All` or `LicenseAssignment.Read.All` is missing or not consented. |
| `Could not read RootDSE` | The domain name is wrong, the trust doesn't reach it, or the gMSA isn't trusted there. Try a domain controller's name as `-TargetDomain`. |
| `is the domain this script runs in` | The AD export was pointed at the job server's own domain. It only reads a trusted domain. |
| `Merge refused: ... gap(s)` | A load's windows don't cover its range. Look at `audit.CopilotLoadWindow`; the next run resumes the load. |
| `window(s) failed after retries` | Usually throttling or a network drop. The next run retries those windows only. |
| `Submission budget reached` | Not a failure: the tenant's daily search budget is spent. Downloads continue and searches resume as the 24-hour window rolls. |

[RUNBOOK.md](../RUNBOOK.md) has the full list, the tuning parameters and how the loader stays inside Microsoft's limits.

## What has run in the lab

On 25 September 2026, 2.0.0 was applied to the lab database: a small tenant with 12 Copilot users and 1,149 interactions, previously on 1.6.0. It shows what a healthy run looks like, and that the upgrade changed no results. The database was backed up first.

| What ran | Result |
|---|---|
| The five changed scripts (step 8's enrichment, session and `bi` scripts) | No errors; each took under a second. |
| Full session build (step 11) | 241 sessions from 1,149 interactions, 129 weekly rows. The same as on 1.6.0. |
| Full `bi` build (step 11) | 1,149 interactions, 241 sessions, 95 licence changes, 1,005 licensed user-days. |
| Nightly path: session build and `bi` refresh from two days back | Rebuilt only from 23 September: 1 session, 4 interactions. Totals unchanged; the session run recorded its `RebuiltFrom` day and the `bi` run the session run it followed. |
| `bi.Users` | Carries the 2.0.0 columns: `EmployeeType`, `IsManager`, `ManagerName`, `OrgDepth`, `OrgLevel1`–`OrgLevel15`. |
| Licence tool (step 10), signed in interactively | 3 Copilot SKUs found; 14 users looked up (12 from the audit data, 2 licence holders who never used Copilot); 10 licensed. About 25 seconds including sign-in. |
| Flattening and a full `bi` rebuild afterwards | Licence at time of use: 925 interactions Licensed, 224 Not licensed, none Unknown. Today's licence in `bi.Users`: 10 Licensed, 4 Not licensed, 16 Unknown (15 people seen only in licence history, plus the placeholder row for records with no user). |

Not run in the lab: the AD export (the lab machine isn't on a domain, so department and org columns are blank there), the licence tool signed in as the app, `Run-Nightly.ps1` and its scheduled task, and the Power BI model and its Copilot setup.

## Not yet proven in a live deployment

Nothing runs at a client site yet. These parts have not run against the real thing:

- **The AD search itself.** Everything after the search is tested, but the search has not run against a real trusted domain. Run step 9 by hand first and read its match report.
- **The Power BI model and its Copilot setup.** The DAX measures, descriptions, AI instructions and verified answers here are a starting point built from the model's design and Microsoft's documentation. None of them has been tried in a published model yet, so test them as step 20 describes.
- **Export compatibility.** `audit.vCopilotAuditRecord` reproduces the Purview portal export's columns, but its JSON hasn't been compared byte for byte with a portal CSV. Check one record before repointing reports built on the portal export.
- **The `Run-Nightly.ps1` shown above** has been dry-run with stand-in steps only.

Figures under "How much space" and the build times come from a scale test on 8 cores and 31 GB of RAM. Copilot in Power BI requirements follow Microsoft Learn as of 25 September 2026; check the current pages before purchasing capacity.
