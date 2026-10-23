# Reporting on the Copilot data: the reference

Everything a team needs to build its own reports on the Copilot audit databases, in Grafana or any other tool that reads SQL Server: what the databases hold, how each table is created and filled, what every column means, how the figures are defined, queries to start from, and the full text of the scripts that create the schema.

**[REPORTING.md](REPORTING.md) is the brief version** of this document, about ten pages. Read it first. It names the section here that continues each of its own. Parts B and C are long (about 17,000 lines) and generated, so the repository keeps this guide only: each release attaches the full reference, `REPORTING-REFERENCE.md`, and both guides as Word documents, `REPORTING.docx` and `REPORTING-REFERENCE.docx`.

Written for release 2.6.2. The SQL in Part A was run against a demo database of that release, as a member of the reporting role, with Grafana's macros replaced by fixed dates. It was not run inside Grafana: check the Grafana settings and macros against your version.

## How this document is laid out

| Part | What it is | Kept current by |
|---|---|---|
| **A. The guide** | How the schema is created, filled and used, the reporting views column by column, the figures, the Grafana setup and queries, and the other layers. | Hand, each release |
| **B. Every table and view, column by column** | All 104 tables and 37 views: types, keys, defaults, indexes, and the script that creates each. The same text as [SCHEMA.md](SCHEMA.md). | `tools/dev/schema/Build-ReportingReference.ps1`, from `docs/SCHEMA.md` |
| **C. The scripts** | The full text of the installer, the 19 SQL scripts and the six other PowerShell scripts that create or fill the schema. | The same tool, from the scripts themselves |

Parts B and C are generated when a release is made, from the files of that release, and are in the full reference attached to it; in the repository their headings stand empty. What you read there is what the release runs.

## Contents

**Part A. The guide**

1. [The system in one page](#1-the-system-in-one-page)
2. [The two databases and their schemas](#2-the-two-databases-and-their-schemas)
3. [How the schema is created](#3-how-the-schema-is-created)
4. [How the tables are filled, and when](#4-how-the-tables-are-filled-and-when)
5. [Access for a reporting tool](#5-access-for-a-reporting-tool)
6. [The reporting views, column by column](#6-the-reporting-views-column-by-column)
7. [The figures, as SQL](#7-the-figures-as-sql)
8. [Grafana: setup and queries](#8-grafana-setup-and-queries)
9. [The other layers](#9-the-other-layers)
10. [Checking a report against the dashboard](#10-checking-a-report-against-the-dashboard)
11. [Words used here](#11-words-used-here)

**[Part B. Every table and view, column by column](#part-b-every-table-and-view-column-by-column)**

**[Part C. The scripts](#part-c-the-scripts)**

---

# Part A. The guide

## 1. The system in one page

Microsoft 365 writes an audit record for every Copilot interaction. Microsoft Purview keeps those records for 180 days. This product copies them into SQL Server, keeps them for as long as you choose, and builds reporting tables over them.

```text
 Sources                         Copilot database (CopilotAudit)                      Readers
 -------                         -------------------------------                      -------

 Purview audit log  --Graph-->   audit.CopilotAudit            every record, JSON kept
 (CopilotInteraction)  or CSV          |
                                       v
                                 audit.RawLogFlattened         every field in a column
                                 audit.RawLogAccessedResource  one row per resource read
                                       |
 Active Directory   ---------->  audit.UserDirectory           people, managers, org chain
 Microsoft Graph    ---------->  audit.CopilotLicense          today's licenses
                                       |
                                       v
                                 audit.CopilotSession          inferred sessions
                                       |
 License-event database                v
 (CopilotLicenseEvents)  ----->  bi facts and dimensions       the star schema
  license changes,                     |
  Copilot Studio events                +-->  bi views  ------------------>  Power BI, Grafana,
                                       |                                    any SQL reader
                                       v
 Agent workbook (Excel) ------>  dash.*                        rollups, org chart,   -->  Copilot Insights
                                                               agent inventory            web dashboard
```

Four things come from outside each night: audit records (through Microsoft Graph's audit log query API, or from CSV exports of the Purview portal), the user directory (from on-premises Active Directory), current licenses (from Microsoft Graph), and, optionally, an Excel workbook listing the organization's agents. Everything else is built inside SQL Server by stored procedures.

Three readers exist today. The **Copilot Insights dashboard**, a web site in IIS, reads `bi` and `dash`. The **Power BI model** in `powerbi/` imports the `bi` views. A **reporting team's own tool**, which this document is for, reads the `bi` views too.

**What a record holds.** One audit record is one interaction: a person's prompt and Copilot's response. It holds who, when, the app, the agent, the model, the license the user had, the resources Copilot read to answer (files, mail, Teams messages, sites, web searches, each with its sensitivity label), and the results of the safety checks (jailbreak, prompt injection, blocked resources). **It does not hold the prompt or the response.** No table in either database has them.

## 2. The two databases and their schemas

One Microsoft 365 tenant has one pair of databases. With several tenants, each has its own pair, and nothing joins them.

| Database | Default name | What it holds |
|---|---|---|
| Copilot database | `CopilotAudit` | Copilot interaction records and everything built from them: all of `bi` and `dash`, and most of `audit`. |
| License-event database | `CopilotLicenseEvents` | Entra directory audit records (*Change user license*, *Update user*, *Add user*, *Delete user*) and Copilot Studio's authoring records (*BotCreate*, *BotUpdateOperation-BotPublish* and twelve more). The same loading and final-record tables as the Copilot database, plus the license-event tables. |

The license-event database is a source, not a reporting database. Each night the Copilot database's `bi` refresh reads the license changes and the agent events from it and writes them into `bi.FactLicenseChange`, `bi.FactLicensedDay` and `bi.FactAgentEvent`. A report never needs to connect to it.

The schemas of the Copilot database:

| Schema | Tables | Views | What it is |
|---|--:|--:|---|
| `audit` | 41 | 10 | Loading state and staging; the final records; the flattened records; licenses and the user directory; sessions; the CSV import; and, in the license-event database, the license events. |
| `bi` | 21 | 26 | The star schema: 13 dimension tables, 6 fact tables, 2 tables of refresh state, and the 26 views a reporting tool reads. |
| `dash` | 34 | 1 | The dashboard's own: settings, roles, reference names, its audit log, the org chart, the daily rollups, the agent inventory, and the operations tables (alerts, digests, targets, extracts). |
| `dbo` | 3 | | Only when an agent workbook is loaded: `dbo.AgentInventory`, `dbo.AgentsInProd` and `dbo.ParentChildAgent`, each sheet as it is in the workbook, every column as text. |

The counts are of what Part B documents. Some tables exist only once they are needed: `audit.CopilotAudit` after the first finalize, the `audit.CopilotCsv*` tables after a CSV import, the `dash` tables only where the dashboard is installed.

## 3. How the schema is created

**One command creates and upgrades everything:** `Install-CopilotAudit.ps1`, run on the job server by an administrator. Its full text is in [Part C](#part-c-the-scripts). It is safe to run any number of times. Each run does four things:

1. **Creates the two databases** if they do not exist, with SIMPLE recovery and compatibility level 160, or the server's highest below it. SQL Server 2017 or later is needed. With `-ServiceAccount`, the nightly job's account gets a login and `db_owner` in both.
2. **Runs the SQL scripts**, in the order they depend on each other. Each script is idempotent: it creates what is missing, adds the columns a newer release needs, and never drops data. The scripts are split into batches on `GO`, as `sqlcmd` does.
3. **Stores the reporting time zone** in `audit.PipelineSetting`, in both databases, and **grants the reporting accounts** their role (section 5).
4. **Builds whatever the data calls for**: license events shredded, records flattened, sessions and `bi` built in full the first time, then only what an upgrade needs.

The scripts it runs, in order:

| # | Script | Database | Creates |
|--:|---|---|---|
| 1 | `Create-CopilotAuditSchema.sql` | Both | The loading tables: `CopilotBackfillRange`, `CopilotBackfillWindow`, `CopilotAuditStage`, `CopilotLoad`, `CopilotLoadWindow`, `CopilotLoadStage`. `PipelineSetting`, `PipelineRun`, `PipelineRunStep`, `PipelineCertificate`. The functions `audit.fnShredCopilotInteraction` and `audit.ReportingTimeZone`. |
| 2 | `Create-CopilotLoadProcedure.sql` | Both | `audit.usp_MergeCopilotLoad`. |
| 3 | `Create-CopilotFlattenedSchema.sql` | Copilot | `RawLogFlattened`, `RawLogAccessedResource`, `RawLogContext`, `RawLogRecordShape`, `ValueCatalog`, `ValueCatalogColumn`, `AgentCatalog`; `CopilotLicense` if it is not there yet. |
| 4 | `Create-CopilotFlattenProcedure.sql` | Copilot | `audit.usp_FlattenCopilotAudit`, `audit.usp_CatalogValues`, `audit.usp_CatalogAgents`, `audit.usp_BackfillAgentNames`. |
| 5 | `Create-CopilotPurgeProcedure.sql` | Copilot | `audit.usp_PurgeCopilotAuditJson`. |
| 6 | `Create-CopilotEnrichmentSchema.sql` | Copilot | `CopilotLicense`, `UserDirectory`, `GroupMember`, `LicenseSku`, `LicenseSkuDay`, `UserLicenseSku`; the views `vCopilotAdoption` and `vCopilotUsageByDepartment`. |
| 7 | `Create-CopilotSessionSchema.sql` | Copilot | `SessionAppCategory` (seeded), `CopilotSession`, `CopilotWeeklyRollup`, `CopilotSessionRun`; the views `vCopilotSessionSummary` and `vCopilotWeeklyRollup`. |
| 8 | `Create-CopilotSessionProcedure.sql` | Copilot | `audit.usp_BuildCopilotSessions`. |
| 9 | `Create-CopilotBiSchema.sql` | Copilot | The `bi` schema: every `Dim` and `Fact` table, `RefreshRun`, `RebuildInProgress`, `bi.fnAgentIdentity`, and 19 of the views. |
| 10 | `Create-CopilotBiProcedure.sql` | Copilot | `bi.usp_RefreshBi`, `bi.usp_NameAgents` and `bi.usp_MergeAgents`. |
| 11 | `Create-CopilotExternalUsageSchema.sql` | Copilot | AI usage outside Microsoft 365 Copilot (2.6.2): `GitHubCopilotSeat`, `GitHubCopilotDay`, `GitHubCopilotUserDay`, `GitHubCopilotCost`, `FoundryDeployment`, `FoundryUsageDay`, `FoundryCostDay` and `ExternalUsageRun`. Empty until a loader is set up. |
| 12 | `Create-CopilotDashboardSchema.sql` | Copilot, with `-Dashboard` | The `dash` schema's core: `Setting`, `RoleGrant`, `AgentAlias`, `SensitivityLabel`, `AuditLog`, `Person`, `OrgClosure`, `UserDay`, `ResourceDay`, `FlaggedInteraction`, `AgentUse`, `RefreshRun`, `AgentInventory`, `AgentChildLink`, `AgentInventoryRun`; the view `dash.Agent`; `dash.fnScopeUsers`. |
| 13 | `Create-CopilotDashboardProcedure.sql` | Copilot, with `-Dashboard` | `dash.usp_RefreshDashboard`. |
| 14 | `Create-CopilotOperationsSchema.sql` | Copilot, with `-Dashboard` | The operations tables (`Secret`, `Subscription`, `NotificationLog`, `AlertState`, `Target`, `TimeSaving`, `TrainingAttendance`, `ValueName`, `FlagCase`, `DigestPreference`, `DigestSend`, `LicenseAction`, `LicenseContract`, `SavedView`, `NonHumanException`, `JobFamilyRule`, `UseCaseRule`, `EventMarker`, `Extract`); `audit.GroupMember` if it is not there yet; the role `CopilotInsightsApp` and its grants; privacy mode and retention; and 7 more `bi` views: `OrgSecurity`, `ValueNames`, `LicensePool`, `UseCases`, `JobFamilies`, `EventMarkers`, `DepartmentWeek`. |
| 15 | `Create-LicenseEventSchema.sql` | License-event | `LicenseEvent`, `LicenseEventChunk`, `LicenseEventProperty`, `LicenseSkuChange`, `LicenseSkuChangeRun`; the views `vLicenseChange`, `vLicenseSkuChangeFromRecords`, `vLicenseLifecycle`. |
| 16 | `Create-LicenseEventProcedure.sql` | License-event | `audit.usp_ShredLicenseEvents`, `audit.usp_RefreshLicenseSkuChange`, and the view `vLicenseSkuChange` over the kept changes. |
| 17 | `Create-AgentEventProcedure.sql` | License-event | `audit.AgentEvent`, `audit.usp_ShredAgentEvents`. |

Two more SQL scripts are not run by the installer:

| Script | Run by | Creates |
|---|---|---|
| `Finalize-CopilotAudit.sql` | `Invoke-CopilotAuditBackfill.ps1 -Finalize`, once, after the first load of history | `audit.CopilotAudit` and `audit.CopilotInteraction`, built from staging; the views `vCopilotAuditRecord` and `vCopilotDailyUsage`. |
| `Create-CopilotCsvImport.sql` | `tools/Import-CopilotAuditCsv.ps1`, on each import | `CopilotCsvImport`, `CopilotCsvFile`, `CopilotCsvReject`, `CopilotCsvStage`, `audit.usp_MergeCopilotCsv`. In a database with no backfill, the final tables and views too. |

**What this means for a report.**

- **The seven `bi` views of script 14 exist only where the dashboard's schema is installed** (`-Dashboard`). A tenant without the dashboard has 19 `bi` views. `bi.LicensePool`, `bi.DepartmentWeek`, `bi.UseCases`, `bi.JobFamilies`, `bi.EventMarkers`, `bi.ValueNames` and `bi.OrgSecurity` are the seven.
- **A release may add columns to a view, and adds views.** It does not remove or rename a column of a `bi` view without saying so in [CHANGELOG.md](../CHANGELOG.md). Name your columns in every query; do not use `SELECT *`.
- **Keys are stable.** A user, app, agent, model or SKU keeps its key from the first refresh that saw it, through every later refresh and rebuild of the facts. Only dropping the `bi` tables themselves would renumber them, and no script does.

## 4. How the tables are filled, and when

### The nightly run

A scheduled task, *Copilot audit - nightly*, runs `Invoke-CopilotPipeline.ps1` once a day on the job server. Its full text is in Part C. Each step runs in its own process; the run stops at the first failure, and the next night resumes it. Every run and step is recorded in `audit.PipelineRun` and `audit.PipelineRunStep`.

| Step | Script | Fills |
|---|---|---|
| 1. User directory | `tools/Export-UsersWithHierarchy-ToSql.ps1` | `audit.UserDirectory`, replaced whole in one transaction; `audit.GroupMember`. |
| 2. Licenses | `tools/Update-CopilotAuditEnrichment.ps1` | `audit.CopilotLicense`, `audit.LicenseSku`, `audit.LicenseSkuDay` (one snapshot a night), `audit.UserLicenseSku`. |
| 3. Agent workbook, when configured | `tools/Import-AgentWorkbook.ps1` | The three `dbo` tables, `dash.AgentInventory`, `dash.AgentChildLink`, `dash.AgentInventoryRun`. |
| 4. License events | `Invoke-CopilotAuditBackfill.ps1 -Incremental`, into the license-event database | `audit.CopilotAudit` there, then `audit.LicenseEvent`, `audit.LicenseEventProperty`, `audit.LicenseSkuChange`, `audit.AgentEvent`. |
| 5. Copilot interactions | `Invoke-CopilotAuditBackfill.ps1 -Incremental`, into the Copilot database | `audit.CopilotAudit`, `audit.CopilotInteraction`, and then everything built from them, below. |
| 6. GitHub Copilot usage, when configured (2.6.2) | `tools/Import-GitHubCopilotUsage.ps1` | `audit.GitHubCopilotSeat` (replaced whole), `audit.GitHubCopilotDay` and `audit.GitHubCopilotUserDay` (the last 10 UTC days written again), `audit.GitHubCopilotCost` (this month and the last replaced), and a row of `audit.ExternalUsageRun`. |
| 7. Foundry usage and cost, when configured (2.6.2) | `tools/Import-FoundryUsage.ps1` | `audit.FoundryDeployment` (replaced whole for each subscription), `audit.FoundryUsageDay` and `audit.FoundryCostDay` (the last 10 UTC days written again), and a row of `audit.ExternalUsageRun`. |
| After a good night | `dash.usp_ApplyRetention` | Deletes what the administrators' retention settings say. |

Steps 6 and 7 are optional: each runs only where `copilot-audit.settings.psd1` names its source (`GitHubEnterprise` or `GitHubOrganization`; `FoundrySubscriptions`), and is otherwise skipped with a line in the log. A failure of either is recorded with its step and in `audit.ExternalUsageRun`, and does not fail the night. Neither changes anything in `bi` or `dash`.

Step 5 is where the reporting tables change. After it has merged the night's records, it calls four procedures in order, each for the days from the load's start:

| Order | Procedure | Reads | Writes |
|--:|---|---|---|
| 1 | `audit.usp_FlattenCopilotAudit` | `audit.CopilotAudit` (the JSON) | `audit.RawLogFlattened`, `RawLogAccessedResource`, `RawLogContext`, `ValueCatalog`, `AgentCatalog` |
| 2 | `audit.usp_BuildCopilotSessions` | `RawLogFlattened`, `UserDirectory`, the license history | `audit.CopilotSession`, `audit.CopilotWeeklyRollup` |
| 3 | `bi.usp_RefreshBi` | `RawLogFlattened`, `RawLogAccessedResource`, `CopilotSession`, `UserDirectory`, `CopilotLicense`, `SessionAppCategory`, and the license-event database | Every `bi` table |
| 4 | `dash.usp_RefreshDashboard` | The `bi` facts, `UserDirectory` | `dash.Person`, `dash.OrgClosure`, `dash.UserDay`, `dash.ResourceDay`, `dash.FlaggedInteraction`, `dash.AgentUse` |

### What `bi.usp_RefreshBi` does

In order:

1. Reads every license SKU change from the license-event database and works out, for each person, the periods in which they held a Copilot SKU.
2. Adds a key for any user, app or SKU not seen before.
3. Extends `bi.DimDate` to cover the data. It holds every day of each year the data touches.
4. Deletes the rows of `bi.FactInteraction` and `bi.FactAccessedResource` from the load's start, and writes them again from the flattened records, adding keys for new agents, models, license types, labels, resource types, actions and sites as it goes.
5. Rewrites the dimensions' attributes: each person's directory profile and today's license, each app's category.
6. Copies the sessions the session build replaced into `bi.FactSession`, and rebuilds `bi.FactLicenseChange`, `bi.FactLicensedDay` and `bi.FactAgentEvent` whole.
7. Makes one agent of the identities the audit log gives the same agent (`bi.usp_MergeAgents`, 2.6.3), names the agents (`bi.usp_NameAgents`), and writes a row to `bi.RefreshRun`.

### How much each night rewrites

A nightly load starts 48 hours before the end of the last one, because Purview commits to no delivery time for an audit record and some arrive a day or two late. So each night:

- `bi.Interactions` and `bi.AccessedResources`: about the last two to three days are deleted and written again, in one transaction. Older days are not touched.
- `bi.Sessions`: the sessions of the same days are replaced.
- `bi.LicenseChanges`, `bi.LicensedDays`, `bi.AgentEvents`: rebuilt whole. They are small.
- Every dimension: attributes rewritten; keys kept.

A night that follows missed ones, or a CSV import of older files, refreshes from further back.

### What a report must expect

**The last two days are incomplete.** A record can arrive up to 48 hours after its interaction. Until then, a day's count is low. The dashboard's daily charts stop two days before the last record's day for that reason. On a Grafana panel, either stop the series two days early or note it.

**A partial refresh is atomic; a full rebuild is not.** The nightly replace of its days is one transaction: a query sees the old rows or the new ones. A full rebuild (`@SinceUtc = NULL`: the installer after some upgrades, or an administrator's `-Rebuild`) empties `bi.FactInteraction` and fills it a month at a time, committing each month, because the whole would not fit one transaction. For its duration, hours on a large history, the fact holds only the months written so far. `bi.RebuildInProgress` has a row while one runs, though a reporting account cannot read that table (section 5 has the grant that lets it).

**History is restated in six ways.** A figure for a past period can change from one day to the next when:

| What changed | Effect on past periods |
|---|---|
| A late record arrived | The last two days gain interactions. |
| A person moved department or manager | All their history shows under the new one: `bi.Users` holds today's attributes only. |
| An agent was renamed | The same `AgentKey` carries the new name, for all its history. |
| An administrator changed an app's category | `PremiumUse` is set again on every past interaction of that app. |
| An administrator changed a use-case or job-family rule | `bi.UseCases` and `bi.JobFamilies` are views over the rules, so every period changes at once. |
| License history arrived for a period already built | License status is fixed when a row is written, so those rows keep the old status until a full rebuild. This one does *not* restate by itself. |

**Reporting time zone.** Every day and hour in `bi` is cut in one zone, stored in `audit.PipelineSetting` (`ReportingTimeZone`) and set before the first load. It cannot be changed afterwards without loading into new databases. Daylight saving is applied, so a day in `bi` is a local calendar day: 23 or 25 hours long twice a year.

### The first load

`Invoke-CopilotPipeline.ps1 -History` loads the last 180 days once (the most Purview keeps), finalizes both databases, loads the directory and the licenses, and runs the installer to build everything in full. CSV exports of the Purview portal can extend history further back. A database's range is in `audit.CopilotBackfillRange`.

## 5. Access for a reporting tool

### The role

`CopilotReporting` is a database role in the Copilot database. It holds `SELECT` on each view in `bi`, and nothing else: not the `bi` tables, not `audit`, not `dash`. `Install-CopilotAudit.ps1` creates it the first time it is given `-ReportingAccount`, and grants it every `bi` view on each run; `Create-CopilotOperationsSchema.sql` does the same for the seven views it adds. Both are in Part C.

Why the views and not the tables: in privacy mode `bi.Users` gives pseudonyms where `bi.DimUser` holds real names, and the views leave out the lineage columns (`RecordId`, `CreatedUtc`) that would let a reader go back to one person's record.

### Getting an account into it

For a Windows account, the installer does everything:

```powershell
.\Install-CopilotAudit.ps1 -SqlServer SQLPROD01 -ReportingAccount 'CORP\svc-grafana'
```

For a SQL login, a database administrator runs this in the Copilot database, once the role exists:

```sql
CREATE LOGIN grafana_reader WITH PASSWORD = '<a strong password>';
CREATE USER grafana_reader FOR LOGIN grafana_reader;
ALTER ROLE CopilotReporting ADD MEMBER grafana_reader;
```

If the role does not exist yet (the installer was never given `-ReportingAccount`), the administrator creates it the way the installer does:

```sql
IF DATABASE_PRINCIPAL_ID(N'CopilotReporting') IS NULL CREATE ROLE CopilotReporting AUTHORIZATION dbo;
DECLARE @v sysname, @s nvarchar(400);
DECLARE v CURSOR LOCAL FAST_FORWARD FOR SELECT name FROM sys.views WHERE schema_id = SCHEMA_ID(N'bi');
OPEN v; FETCH NEXT FROM v INTO @v;
WHILE @@FETCH_STATUS = 0
BEGIN
    SET @s = N'GRANT SELECT ON bi.' + QUOTENAME(@v) + N' TO CopilotReporting;';
    EXEC (@s);
    FETCH NEXT FROM v INTO @v;
END;
CLOSE v; DEALLOCATE v;
```

### What a member can and cannot read

| Can read | Cannot read |
|---|---|
| The 26 views in `bi` (19 without the dashboard's schema) | The `bi` tables (`bi.DimUser`, `bi.FactInteraction` ...), `bi.RefreshRun`, `bi.RebuildInProgress` |
| | Everything in `audit` and `dash` |
| | `audit.ReportingTimeZone()` |
| | The license-event database |

So a member cannot find out, from SQL, the reporting time zone, when the last refresh ran, or whether a rebuild is under way. Ask the administrator for the zone. For the other two, an administrator may add these grants by hand; neither table names a person. The installer does not make them, and does not remove them.

```sql
GRANT SELECT ON bi.RefreshRun TO CopilotReporting;
GRANT SELECT ON bi.RebuildInProgress TO CopilotReporting;
```

With them, a report can show when the data was last refreshed and warn during a rebuild (section 8 has the queries).

### No row-level security

Nothing in SQL Server limits which people's rows a member of `CopilotReporting` reads. The two existing readers each limit them in their own way:

- The **dashboard** shows a viewer only themselves and the people under them in the org chart, unless they are in the top two levels or hold the *Organization* role. It refuses any figure for fewer people than the minimum group size (5 by default).
- The **Power BI model** has a role, *Organization*, that filters `Users` by `bi.OrgSecurity` and the viewer's sign-in name.

A Grafana data source has one login, shared by every viewer of every dashboard built on it. Decide who may open those dashboards with that in mind. Section 8 shows how to filter by the viewer with `bi.OrgSecurity`, and what that does not protect against.

### Privacy mode

An administrator turns it on in the dashboard (*Admin › Privacy and retention*), for an organization that must not report on named people. It is one row in `audit.PipelineSetting` (`PrivacyMode` = `Pseudonymize`), which four `bi` views read each time they are queried. With it on:

| View | Change |
|---|---|
| `bi.Users` | `UserPrincipalName`, `DisplayName`, `ManagerUpn`, `ManagerName` and `OrgLevel1` to `OrgLevel15` become `P-` followed by 16 hexadecimal characters: the first 8 bytes of a salted SHA-256 hash of the lower-cased name. The same name gives the same pseudonym in every column and every query, so grouping and counting work. `Department`, `Division`, `JobTitle`, `OfficeLocation`, `City`, `Country`, `CompanyName`, `EmployeeType` are unchanged. |
| `bi.OrgSecurity` | No rows. Its rows would pair a real sign-in name with a `UserKey`. |
| `bi.Sites` | A OneDrive site (`IsPersonal` = 1) is named *OneDrive (personal)*: its address names its owner. |
| `bi.LicenseChanges` | `ChangedBy` is NULL: it names an administrator. |

The change takes effect on the next query, with no refresh. A report that lists people shows pseudonyms from then on, and a report filtered by `bi.OrgSecurity` shows nothing. A job title held by one person still identifies them; in privacy mode, report by department and apply the minimum group size, or use `bi.DepartmentWeek`.

### More than one tenant

Each tenant has its own Copilot database, with its own keys: `UserKey` 5 in one is not `UserKey` 5 in another. Add a Grafana data source for each, and combine figures in Grafana (a mixed data source panel), not in SQL.

## 6. The reporting views, column by column

Conventions for every view:

- **Keys** are integers. Every key in a fact view is filled; a missing value points at a named row of its dimension, listed below.
- **`DateKey`** is the local date as an integer, `yyyymmdd`: 7 October 2026 is `20261007`. "Local" is the reporting time zone.
- **Flags** are `bit`: 1 or 0, never NULL in a fact view. Sum one with `SUM(CAST(Flag AS int))`.
- **Text** from the directory can be NULL or empty. Group on `ISNULL(NULLIF(Department, N''), N'(no department)')`.

Types, keys and indexes are in Part B, under each view's name and the table it reads.

### The fact views

#### bi.Interactions

One row per Copilot interaction: one audit record of type *CopilotInteraction*. Over `bi.FactInteraction`, a clustered columnstore index in date order. The largest table: tens of millions of rows a year for a large organization.

| Column | Meaning |
|---|---|
| `ActivityHour` | The local date and time of the interaction, cut to the hour: `2026-10-07 14:00:00`. Nothing finer is kept. Not UTC. |
| `DateKey` | The local date. Joins to `bi.Calendar`. Filter on this. |
| `HourOfDay` | 0 to 23, local. |
| `UserKey` | Who. Joins to `bi.Users`. Key 1 is *(unknown user)*: a record with no sign-in name. |
| `AppKey` | The app Copilot was used in. Joins to `bi.Apps`. Key 1 is *(unknown)*. |
| `AgentKey` | The agent that answered. Joins to `bi.Agents`. **Key 1 is *(no agent)*: Copilot itself**, which is most interactions. |
| `ModelKey` | The model. Joins to `bi.Models`. Key 1 is *(not reported)*; many records name no model. |
| `LicenseStatusKey` | Whether the person held a Copilot license at that moment, from the license history: 0 *Unknown*, 1 *Licensed*, 2 *Not licensed*. Fixed when the row is written. |
| `LicenseTypeKey` | The license the record itself reports. Joins to `bi.LicenseTypes`: *Premium*, *Starter*, *(not reported)*. Microsoft's own word, beside `LicenseStatusKey`, which is worked out. |
| `SensitivityLabelKey` | The interaction's own sensitivity label. Joins to `bi.SensitivityLabels`. Key 1 is *(none)*. |
| `PromptCount` | The prompts in the record. Usually 1. |
| `MessageCount` | The messages in the record, prompts and responses together. |
| `AccessedResourceCount` | How many resources Copilot read to answer. Each is a row of `bi.AccessedResources`. |
| `UsedWebSearch` | 1 when Copilot grounded the answer in a web search. |
| `JailbreakDetected` | 1 when the service flagged the prompt as a jailbreak attempt. |
| `PromptInjectionDetected` | 1 when a resource Copilot read carried a cross-prompt injection: an instruction hidden in content. |
| `HasBlockedResource` | 1 when Copilot was refused a resource: a failed access, or a Purview policy that blocked it. |
| `HasSensitivityLabel` | 1 when any resource it read carried a sensitivity label. |
| `MemoryUpdated` | 1 when the interaction updated Copilot's memory of the user. |
| `DlpEvaluationDeferred` | 1 when a data loss prevention check was deferred. |
| `CalledAnotherAgent` | 1 when the agent handed off to another agent. |
| `PremiumUse` | 1 when the interaction was made with a license **and** needed it. See [Premium use](#premium-use). |
| `UseCaseKey` | Joins to `bi.UseCases`. It is `AppKey` × 4 + web search × 2 + agent, so it can be read without the join. |

Not in the view, by design: the record id, the UTC timestamp to the second, the conversation thread, the names and addresses of the resources read. They are in `audit.RawLogFlattened` (section 9).

#### bi.Sessions

One row per session. A session is one person's prompts with less than 60 minutes between one and the next, within one local day; it is inferred, not something Microsoft records. A session never crosses midnight. Over `bi.FactSession`.

| Column | Meaning |
|---|---|
| `SessionKey` | The session's number. Stable until an administrator rebuilds every session, which renumbers them. |
| `SessionStart`, `SessionEnd` | The first and last prompt's time, local, to the second. |
| `DateKey` | The local date. A session has one date. |
| `UserKey` | Who. |
| `AppKey` | The app of the session's first prompt. A session can span apps; it is counted under the first. |
| `LicenseStatusKey` | License at the first prompt. For a person with no change in the license history, sessions use today's license throughout, where `bi.Interactions` uses it from the date it began; the two can differ for such a person before that date. |
| `PromptCount` | Prompts in the session. |
| `SpanMinutes` | Minutes from the first prompt to the last. 0 for a session of one prompt. It is not time spent: nothing records when a person stopped reading. |
| `IsMultiTurn` | 1 when the session has more than one prompt. |

#### bi.AccessedResources

One row per resource an interaction read: an element of the record's `AccessedResources` list. Over `bi.FactAccessedResource`, a clustered columnstore index. The web pages a web search cites are listed as resources too.

| Column | Meaning |
|---|---|
| `ActivityHour`, `DateKey` | The interaction's hour and date, local. |
| `UserKey`, `AppKey` | The interaction's person and app. |
| `ResourceTypeKey` | What it was. Joins to `bi.ResourceTypes`: *File*, *EmailMessage*, *TeamsMessage*, *WebSearchQuery* and others, as Microsoft writes them. |
| `ResourceActionKey` | What was done. Joins to `bi.ResourceActions`: *Read*, *Create* and others. |
| `FileTypeKey` | Joins to `bi.FileTypes`: a file's extension (*docx*, *xlsx*, *pptx*), and for anything that is not a file, its type. |
| `SiteKey` | Where it lives. Joins to `bi.Sites`. Key 1 is *(none)*. |
| `SensitivityLabelKey` | The resource's label. Joins to `bi.SensitivityLabels`. Key 1 is *(none)*. |
| `IsBlocked` | 1 when Copilot was refused it. |
| `XPIADetected` | 1 when a cross-prompt injection was found in it. |

There is no key back to the interaction. An interaction and its resources share `UserKey`, `AppKey` and `ActivityHour`, which is not unique. Count resources here and interactions in `bi.Interactions`; do not join the two.

#### bi.LicensedDays

One row per person per local day on which they held a Copilot license at the end of that day. Two columns, `DateKey` and `UserKey`. Over `bi.FactLicensedDay`, rebuilt whole each night, through today.

This is the denominator of every adoption figure: the licensed users on a day are the rows with that `DateKey`.

#### bi.LicenseChanges

One row per Copilot SKU added to or removed from a person. Over `bi.FactLicenseChange`, rebuilt whole each night from the license-event database.

| Column | Meaning |
|---|---|
| `ChangedUtc` | When, **in UTC**. |
| `DateKey` | The local date. |
| `UserKey` | Whose license changed. |
| `SkuKey` | Which SKU. Joins to `bi.Skus`. |
| `Change` | 1 added, -1 removed. `SUM(Change)` over a period is the net change. |
| `ChangedBy` | The administrator or service that made it. NULL in privacy mode. |

A removal is read from the events: a license that disappears from a person's list, or an account that is deleted. A renamed account has no removal under its old name.

#### bi.AgentEvents

One row per Copilot Studio authoring record: something a maker did to an agent. Over `bi.FactAgentEvent`, copied whole each night from the license-event database. Empty unless that database loads Copilot Studio's operations, which the nightly run does from release 2.3.0.

| Column | Meaning |
|---|---|
| `RecordId` | The audit record's id. |
| `EventUtc` | When, **in UTC**. |
| `DateKey` | The local date. |
| `Action` | What it means: `Created`, `Published`, `Shared`, `Updated`, `Component`, `Deleted`. |
| `Operation` | Microsoft's operation name: `BotCreate`, `BotUpdateOperation-BotPublish`, `BotComponentUpdate` ... |
| `ActorKey` | The maker. Joins to `bi.Users` on `UserKey`. NULL when the maker is not a person in `bi.Users`. |
| `BotId`, `BotSchemaName` | The agent. `BotSchemaName` is the last part of the agent's `AgentId` in `bi.Agents` (`crd20_expenseAssistant`). |
| `ComponentName`, `ComponentType` | For a component event, the topic, action or knowledge source changed. |

### The dimension views

#### bi.Calendar

One row per date, for every day of each year the data touches. So it has future days.

| Column | Meaning |
|---|---|
| `DateKey` | `yyyymmdd`. |
| `Date` | The date. |
| `Year`, `Quarter`, `MonthNumber`, `MonthName`, `YearMonth` | `YearMonth` is `2026-10`. |
| `DayOfWeekNumber`, `DayOfWeekName` | 1 is Monday, 7 is Sunday. |
| `IsWeekday` | 1 for Monday to Friday. Public holidays are not known. |
| `WeekEnding` | The Friday that ends the date's week. **Weeks run Saturday to Friday.** |
| `WeekYear`, `WeekNumber`, `WeekLabel` | Week 1 of a year ends on its first Friday. `WeekLabel` is `2026-W41`. |

#### bi.Users

One row per person seen in the audit data, the licenses or the license history: users of Copilot, license holders who never used it, and people whose license changed. Over `bi.DimUser`. **Every attribute is today's**, rewritten each night from `audit.UserDirectory` (on-premises AD) and `audit.CopilotLicense` (Microsoft Graph).

| Column | Meaning |
|---|---|
| `UserKey` | The key. 1 is *(unknown user)*. |
| `UserPrincipalName` | The sign-in name. A pseudonym in privacy mode. |
| `DisplayName` | The person's name. A pseudonym in privacy mode. |
| `ManagerUpn`, `ManagerName` | The direct manager. Pseudonyms in privacy mode. |
| `Division`, `Department`, `JobTitle`, `OfficeLocation`, `City`, `Country`, `CompanyName` | From AD. NULL for a person AD does not have. Which AD attribute fills `Department` is a setting of the directory load. |
| `EmployeeType` | `EMPLOYEE`, `CONTRACTOR`, or what the directory load was told to call each type. |
| `IsManager` | 1 when someone reports to them. |
| `AccountEnabled` | 1 when the AD account is enabled. A leaver is 0, or absent from AD. |
| `OrgDepth` | How many managers are above the person. |
| `OrgLevel1` ... `OrgLevel15` | The management chain, top down, by name: `OrgLevel1` is the top of the organization, `OrgLevel<OrgDepth>` the direct manager. Levels below a person's depth are NULL. Group on `OrgLevel2` for "by member of the leadership team". Pseudonyms in privacy mode. |
| `DirectoryStatus` | `Found` (in AD), `NotFound` (gone from Entra), `Not looked up`. |
| `CopilotLicenseToday` | `Licensed`, `Not licensed`, `Unknown`. Today's, not the period's. |
| `CopilotSku` | The Copilot SKU held today. |
| `LicensedSinceUtc` | When the current assignment began, from Entra. |

A person who was renamed is two rows, one per sign-in name.

#### bi.Apps

| Column | Meaning |
|---|---|
| `AppKey` | The key. 1 is *(unknown)*. |
| `App` | The app as the audit log names it: *Word*, *Excel*, *PowerPoint*, *Outlook*, *Microsoft Teams*, *Copilot Chat*, *BizChat*, *Office*, *Edge*, *Copilot Studio* and others. |
| `AppCategory` | *Copilot Chat* or *In-App Copilot*. An administrator sets it per app; an app with no setting is *In-App Copilot*. *BizChat*, *Copilot Chat*, *Edge*, *Office* and *OfficeCopilotSearchAnswer* are seeded as *Copilot Chat*. |

#### bi.Agents

| Column | Meaning |
|---|---|
| `AgentKey` | The key. 1 is *(no agent)*: Copilot itself. |
| `AgentId` | The agent's identity. For a Copilot Studio agent, `Copilot.Studio.<environment>-<tenant id>-<schema name>`; for one of Microsoft's, `MicrosoftAgent.<name>...`; otherwise the record's own agent id. An agent is its id, never its name. |
| `AgentName` | A label: the name the audit log gave the agent on its latest record that has one, or the agent workbook's, or else one read from the id. It changes when a maker renames the agent; the key does not. |
| `AgentSource` | `Copilot Studio`, `Microsoft`, `Agent ID`, or `(no agent)`. |
| `NamedInAudit` | 1 when `AgentName` is the audit log's own name, 0 when it was read from the id. |

The owner, environment and settings of an agent are not here. They are in `dash.AgentInventory`, from the agent workbook (section 9).

**One agent, one row (2.6.3).** The audit log names some agents two ways: a SharePoint agent with and without `SharePointAgents.Declarative.` before its id, one of Microsoft's agents under two variants of the same name, and an agent made in Microsoft 365 Copilot under an environment id and under a title id. Each was listed as two agents with its use split between them. From 2.6.3 it is one row here, with all its interactions under its `AgentKey`. Two agents of your own that only share a name (two makers' *New Agent*) are still two agents. The other identity of an agent is kept in `bi.DimAgent`, in a row whose `MergedIntoKey` is the agent's key; a report that counts agents should count `bi.Agents`, not `bi.DimAgent`.

#### bi.Models, bi.LicenseStatuses, bi.LicenseTypes, bi.Skus

| View | Columns | Notes |
|---|---|---|
| `bi.Models` | `ModelKey`, `ModelProvider`, `ModelName` | Key 1 is *(not reported)*. |
| `bi.LicenseStatuses` | `LicenseStatusKey`, `LicenseStatus` | 0 *Unknown*, 1 *Licensed*, 2 *Not licensed*. *Unknown* is a person with no license history and no lookup, kept apart from *Not licensed*. |
| `bi.LicenseTypes` | `LicenseTypeKey`, `LicenseType` | Key 1 is *(not reported)*. |
| `bi.Skus` | `SkuKey`, `Sku`, `IsCopilot` | `Sku` is the part number, such as `Microsoft_365_Copilot`. |

#### bi.SensitivityLabels

`SensitivityLabelKey` and `LabelId`: the label's GUID as lower-case text, or *(none)* for key 1. The audit log records labels by id only. Names an administrator gave are in `bi.ValueNames`.

#### bi.ResourceTypes, bi.ResourceActions, bi.FileTypes, bi.Sites

| View | Columns | Notes |
|---|---|---|
| `bi.ResourceTypes` | `ResourceTypeKey`, `ResourceType` | Key 1 is *(not reported)*. |
| `bi.ResourceActions` | `ResourceActionKey`, `ResourceAction` | Key 1 is *(not reported)*. |
| `bi.FileTypes` | `FileTypeKey`, `FileType` | Key 1 is *(not reported)*. |
| `bi.Sites` | `SiteKey`, `Site`, `Host`, `IsPersonal` | `Site` is cut to `https://host/sites/name`, `/teams/name` or `/personal/name`, else the host. Whole addresses are not kept. `IsPersonal` is 1 for a OneDrive site. |

#### bi.UseCases and bi.JobFamilies

Both are views over rules an administrator keeps in the dashboard (*Admin › Classifications and events*), so they change the moment a rule does.

- `bi.UseCases`: `UseCaseKey`, `App`, `WebGrounded`, `WithAgent`, `UseCase`. One row for every combination of app, web search and agent, with the use case the first matching rule gives it, or *Other*. Seeded rules give *Writing documents*, *Analyzing data*, *Presentations*, *Email*, *Meetings and chats*, *Research on the web*, *Working with an agent* and *Asking questions (Copilot Chat)*.
- `bi.JobFamilies`: `UserKey`, `JobFamily`. Each person's family from the first rule their job title matches, or *(no family)*.

### The views that stand alone

#### bi.LicensePool

One row per SKU per night: the tenant's seats, from Microsoft Graph's subscribed SKUs. History starts the night the license tool first ran at release 2.3.0 or later.

| Column | Meaning |
|---|---|
| `SnapshotDate` | The date of the snapshot. |
| `SkuPartNumber`, `IsCopilot` | The SKU, and whether it is a Copilot one. |
| `Bought` | Seats enabled plus those in the grace period. |
| `Assigned` | Seats assigned to someone. |
| `Available` | `Bought` less `Assigned`. |
| `InGracePeriod`, `Suspended` | Seats past their term. |

#### bi.DepartmentWeek

Active people, interactions and sessions by department and week, with small groups hidden. For a report that must not show individuals. It reads the dashboard's daily rollup.

| Column | Meaning |
|---|---|
| `WeekEnding` | The week's Friday. Every week of the data, the one under way included. |
| `Department` | The department, or *(smaller departments)* for all those with fewer enabled people than the minimum group size, combined, or *(no department)*. |
| `ActivePeople`, `Interactions`, `Sessions` | NULL when fewer people than the minimum were active that week. |
| `Hidden` | 1 when the figures are NULL for that reason. |

Every department has a row for every week, used or not, so whether a row exists says nothing about anyone.

#### bi.EventMarkers

`Day`, `DateKey`, `Label`, `Kind`. The events an administrator marked on the dashboard's trend charts. `Kind` is `Rollout`, `Training`, `Outage`, `Policy` or `Other`.

#### bi.ValueNames

One row per distinct value of 23 coded columns of the flattened records, with the name an administrator gave it, if any.

| Column | Meaning |
|---|---|
| `TableName`, `ColumnName`, `ColumnLabel` | Which column the value was seen in, and a readable label for it. |
| `Value` | The code: a label id, a model name, a resource type, an app id. |
| `Name` | The name given in *Admin › Value names*, or, for a sensitivity label, in *Admin › Reference data*. NULL until someone names it. |
| `Records` | How many records carry the value. |
| `FirstSeenUtc`, `LastSeenUtc` | When it was first and last seen. |

#### bi.OrgSecurity

One row per viewer and person they may see: `ViewerUpn`, `UserKey`, `WholeOrganization`. A manager's rows are themselves and everyone under them. The top of the organization, the people who report to them, and anyone granted the *Organization* role have a row for every person, with `WholeOrganization` = 1. Empty in privacy mode. Rebuilt each night with the org chart.

### Premium use

Every interaction made with a license either needed it or did not. The refresh decides once and stores the answer in `PremiumUse`; the dashboard, the Power BI model and your report then agree by reading the same column.

It reads the identity Microsoft writes into each record:

| The record's app identity | Counts as |
|---|---|
| `Copilot.M365Copilot.Apps`: Copilot inside Word, Excel, PowerPoint, Outlook, Teams | Licensed use |
| `Copilot.M365Copilot.Bizchat`: Copilot Chat in work mode | Licensed use, whether or not it read anything |
| `MicrosoftAgent.*`: one of Microsoft's agents | Licensed use |
| `Copilot.M365Copilot.WebChat`: web Copilot Chat | Licensed use only if it read work content |
| `Copilot.Studio.*`: a Copilot Studio agent | Not licensed use: it can be paid for per use, without a license |
| None of these (an older record, or one from a CSV export) | The app's category decides: *In-App Copilot* is licensed use; *Copilot Chat* only if it read work content |

*Work content* is a resource in SharePoint or OneDrive, Outlook or Teams, or a mail, a Teams message, a meeting or a connector item. A web page that a web search cited is listed as a resource too, and is not work content.

`PremiumUse` = 1 when the person held a license at that moment and the row counts as licensed use. An interaction without a license is never Premium. [DASHBOARD.md](DASHBOARD.md), *Premium use and the license rules*, has the reasoning.

## 7. The figures, as SQL

Each query here takes a period as two `DateKey` values, `@from` and `@to`. In Grafana they come from the time range (section 8); the examples fix them.

### Volume

```sql
DECLARE @from int = 20260901, @to int = 20260930;
SELECT COUNT(*)                                   AS Interactions,
       SUM(CAST(i.PromptCount AS bigint))         AS Prompts,
       COUNT(DISTINCT i.UserKey)                  AS [Active users],
       SUM(CAST(i.PremiumUse AS int))             AS [Premium interactions],
       COUNT(*) - SUM(CAST(i.PremiumUse AS int))  AS [Free interactions],
       SUM(CAST(i.UsedWebSearch AS int))          AS [Web-grounded interactions],
       COUNT(DISTINCT CASE WHEN i.PremiumUse = 1 THEN i.UserKey END) AS [Premium users]
FROM bi.Interactions AS i
WHERE i.DateKey BETWEEN @from AND @to;
```

### Adoption

Licensed users are counted on one day, the last day of the period that has data. Active licensed users are people who used Copilot with a license in the period and still hold one on that day, so the rate cannot pass 100% when licenses are removed during the period.

```sql
DECLARE @from int = 20260901, @to int = 20260930;
DECLARE @last int = (SELECT MAX(DateKey) FROM bi.LicensedDays WHERE DateKey BETWEEN @from AND @to);
WITH licensed AS (SELECT UserKey FROM bi.LicensedDays WHERE DateKey = @last),
     active AS (SELECT DISTINCT UserKey FROM bi.Interactions
                WHERE DateKey BETWEEN @from AND @last AND LicenseStatusKey = 1)
SELECT COUNT(*)                         AS [Licensed users],
       COUNT(a.UserKey)                 AS [Active licensed users],
       CAST(100.0 * COUNT(a.UserKey) / NULLIF(COUNT(*), 0) AS decimal(5, 1)) AS [Adoption %],
       COUNT(*) - COUNT(a.UserKey)      AS [Licensed, not using]
FROM licensed AS l
LEFT JOIN active AS a ON a.UserKey = l.UserKey;
```

*Licensed, not using* here leaves out people who used Copilot only before they were licensed. The Power BI measure *Licensed users not using Copilot* counts any interaction in the period, licensed or not; drop `AND LicenseStatusKey = 1` to match it.

### Sessions

```sql
DECLARE @from int = 20260901, @to int = 20260930;
SELECT COUNT(*)                                                         AS Sessions,
       CAST(1.0 * SUM(s.PromptCount) / NULLIF(COUNT(*), 0) AS decimal(6, 2)) AS [Prompts per session],
       SUM(CAST(s.IsMultiTurn AS int))                                  AS [Multi-turn sessions],
       CAST(100.0 * SUM(CAST(s.IsMultiTurn AS int)) / NULLIF(COUNT(*), 0) AS decimal(5, 1)) AS [Multi-turn %],
       CAST(AVG(CASE WHEN s.IsMultiTurn = 1 THEN s.SpanMinutes END) AS decimal(8, 1)) AS [Minutes, multi-turn sessions]
FROM bi.Sessions AS s
WHERE s.DateKey BETWEEN @from AND @to;
```

### Security signals

```sql
DECLARE @from int = 20260901, @to int = 20260930;
SELECT SUM(CAST(i.JailbreakDetected AS int))        AS [Jailbreak attempts],
       SUM(CAST(i.PromptInjectionDetected AS int))  AS [Prompt injections],
       SUM(CAST(i.HasBlockedResource AS int))       AS [Interactions with a blocked resource],
       SUM(CAST(i.HasSensitivityLabel AS int))      AS [Interactions that read labeled content],
       COUNT(DISTINCT CASE WHEN i.JailbreakDetected = 1 OR i.PromptInjectionDetected = 1 OR i.HasBlockedResource = 1
                           THEN i.UserKey END)      AS [People with a signal]
FROM bi.Interactions AS i
WHERE i.DateKey BETWEEN @from AND @to;
```

### Licenses

```sql
DECLARE @from int = 20260901, @to int = 20260930;
SELECT SUM(CASE WHEN lc.Change = 1 THEN 1 ELSE 0 END)   AS [Licenses added],
       SUM(CASE WHEN lc.Change = -1 THEN 1 ELSE 0 END)  AS [Licenses removed],
       SUM(lc.Change)                                   AS [Net change]
FROM bi.LicenseChanges AS lc
JOIN bi.Skus AS k ON k.SkuKey = lc.SkuKey
WHERE k.IsCopilot = 1 AND lc.DateKey BETWEEN @from AND @to;
```

### Agents

```sql
DECLARE @from int = 20260901, @to int = 20260930;
SELECT COUNT(DISTINCT i.AgentKey)  AS [Agents used],
       COUNT(*)                    AS [Interactions with an agent],
       COUNT(DISTINCT i.UserKey)   AS [People who used an agent]
FROM bi.Interactions AS i
WHERE i.AgentKey > 1 AND i.DateKey BETWEEN @from AND @to;
```

## 8. Grafana: setup and queries

### The data source

Add a **Microsoft SQL Server** data source for each Copilot database.

| Setting | Value | Why |
|---|---|---|
| Host | The SQL Server and port | |
| Database | The Copilot database, `CopilotAudit` by default | Not the license-event database. |
| Authentication | SQL Server Authentication, or Windows authentication where your Grafana supports it | Section 5. |
| Encrypt | `true` | The product's own connections are encrypted. Trust the server certificate only if the administrator says it is not from a trusted authority. |
| Min time interval | `1h` | Nothing is finer than an hour. |
| Max open connections | A small number, such as 5 | A dashboard runs one query per panel, at once. |
| Connection max lifetime | Your default | |

### Dashboard settings

| Setting | Value | Why |
|---|---|---|
| Time zone | The reporting time zone | Days then start at midnight where the data's days do. With *Browser*, a viewer elsewhere sees days split. |
| Week start | Saturday | The data's weeks run Saturday to Friday. |
| Auto refresh | Off, or hourly | The data changes once a night. |
| Default time range | `now-30d/d` to `now/d` | Whole days. |

Add a **constant** variable named `tz` with the reporting time zone's Windows name, such as `Eastern Standard Time`, `GMT Standard Time` or `W. Europe Standard Time`. An administrator reads it with `SELECT audit.ReportingTimeZone();`. Every query below uses `'$tz'`.

### Time: the two conversions

Grafana's time range is in UTC. The data's days and hours are local. Two conversions cover every query.

**From the range to `DateKey`.** Put this at the top of each query. `$__timeFrom()` and `$__timeTo()` expand to quoted UTC timestamps.

```sql
DECLARE @from int = CONVERT(int, CONVERT(char(8), CAST(CAST($__timeFrom() AS datetimeoffset) AT TIME ZONE '$tz' AS date), 112)),
        @to   int = CONVERT(int, CONVERT(char(8), CAST(CAST($__timeTo()   AS datetimeoffset) AT TIME ZONE '$tz' AS date), 112));
```

**From a local day or hour to a `time` column.** A time series needs a column named `time` holding a true instant. Local midnight, turned into UTC:

```sql
CAST(CAST(c.[Date] AS datetime2) AT TIME ZONE '$tz' AT TIME ZONE 'UTC' AS datetime) AS time
```

For an hour, the same with `i.ActivityHour` in place of `CAST(c.[Date] AS datetime2)`.

Do not use `$__timeFilter(ActivityHour)` or `$__timeGroup(ActivityHour, ...)`. Both treat the column as UTC. Every interaction would move by the zone's offset, and the days would be cut at the wrong hour.

In every query below, the `DECLARE` is left out after the first to keep them short. Each one needs it.

### Variables

Departments, for a multi-value variable `dept`:

```sql
SELECT DISTINCT ISNULL(NULLIF(Department, N''), N'(no department)') AS __text,
       ISNULL(NULLIF(Department, N''), N'(no department)') AS __value
FROM bi.Users
ORDER BY 1;
```

Apps, for `app`:

```sql
SELECT App AS __text, AppKey AS __value FROM bi.Apps ORDER BY App;
```

The people who report to the top of the organization, for `leader`:

```sql
SELECT DISTINCT OrgLevel2 AS __text, OrgLevel2 AS __value FROM bi.Users WHERE OrgLevel2 IS NOT NULL ORDER BY 1;
```

In a query, a multi-value variable is `AND i.AppKey IN ($app)`. Turn on *Include All option* and give it a custom all value that is a subquery, `SELECT AppKey FROM bi.Apps`, so that *All* does not become a list of every key. For a text variable the custom all value is `SELECT ISNULL(NULLIF(Department, N''), N'(no department)') FROM bi.Users`.

### Annotations

```sql
SELECT CAST(CAST(Day AS datetime2) AT TIME ZONE '$tz' AT TIME ZONE 'UTC' AS datetime) AS time,
       Label AS text, Kind AS tags
FROM bi.EventMarkers
WHERE Day BETWEEN CAST(CAST($__timeFrom() AS datetimeoffset) AT TIME ZONE '$tz' AS date)
              AND CAST(CAST($__timeTo()   AS datetimeoffset) AT TIME ZONE '$tz' AS date);
```

### Usage over time

Interactions and active users a day (time series):

```sql
DECLARE @from int = CONVERT(int, CONVERT(char(8), CAST(CAST($__timeFrom() AS datetimeoffset) AT TIME ZONE '$tz' AS date), 112)),
        @to   int = CONVERT(int, CONVERT(char(8), CAST(CAST($__timeTo()   AS datetimeoffset) AT TIME ZONE '$tz' AS date), 112));
SELECT CAST(CAST(c.[Date] AS datetime2) AT TIME ZONE '$tz' AT TIME ZONE 'UTC' AS datetime) AS time,
       COUNT(*) AS Interactions,
       COUNT(DISTINCT i.UserKey) AS [Active users]
FROM bi.Interactions AS i
JOIN bi.Calendar AS c ON c.DateKey = i.DateKey
WHERE i.DateKey BETWEEN @from AND @to
GROUP BY c.[Date]
ORDER BY c.[Date];
```

The same, held back two days, as the dashboard's daily charts are:

```sql
DECLARE @last date = (SELECT DATEADD(DAY, -2, MAX(c.[Date])) FROM bi.Calendar AS c
                      WHERE c.DateKey = (SELECT MAX(DateKey) FROM bi.Interactions));
SELECT CAST(CAST(c.[Date] AS datetime2) AT TIME ZONE '$tz' AT TIME ZONE 'UTC' AS datetime) AS time,
       COUNT(*) AS Interactions,
       COUNT(DISTINCT i.UserKey) AS [Active users]
FROM bi.Interactions AS i
JOIN bi.Calendar AS c ON c.DateKey = i.DateKey
WHERE i.DateKey BETWEEN @from AND @to AND c.[Date] <= @last
GROUP BY c.[Date]
ORDER BY c.[Date];
```

Interactions an hour (time series), for a range of a few days:

```sql
SELECT CAST(i.ActivityHour AT TIME ZONE '$tz' AT TIME ZONE 'UTC' AS datetime) AS time,
       COUNT(*) AS Interactions
FROM bi.Interactions AS i
WHERE i.DateKey BETWEEN @from AND @to
GROUP BY i.ActivityHour
ORDER BY i.ActivityHour;
```

Active users a week, whole weeks only (time series). A week counts when all seven of its days are inside the range and at or before the last day with data, whether or not anyone used Copilot on each:

```sql
DECLARE @last int = (SELECT MAX(DateKey) FROM bi.Interactions WHERE DateKey <= @to);
WITH wk AS (SELECT WeekEnding FROM bi.Calendar WHERE DateKey BETWEEN @from AND @last
            GROUP BY WeekEnding HAVING COUNT(*) = 7)
SELECT CAST(CAST(wk.WeekEnding AS datetime2) AT TIME ZONE '$tz' AT TIME ZONE 'UTC' AS datetime) AS time,
       COUNT(DISTINCT i.UserKey) AS [Active users],
       COUNT(i.UserKey) AS Interactions
FROM wk
JOIN bi.Calendar AS c ON c.WeekEnding = wk.WeekEnding
LEFT JOIN bi.Interactions AS i ON i.DateKey = c.DateKey
GROUP BY wk.WeekEnding
ORDER BY wk.WeekEnding;
```

Active users a month (bar chart):

```sql
SELECT c.YearMonth AS Month, COUNT(DISTINCT i.UserKey) AS [Active users], COUNT(*) AS Interactions
FROM bi.Interactions AS i
JOIN bi.Calendar AS c ON c.DateKey = i.DateKey
WHERE i.DateKey BETWEEN @from AND @to
GROUP BY c.YearMonth
ORDER BY c.YearMonth;
```

Interactions by app (time series, one line per app):

```sql
SELECT CAST(CAST(c.[Date] AS datetime2) AT TIME ZONE '$tz' AT TIME ZONE 'UTC' AS datetime) AS time,
       a.App AS metric, COUNT(*) AS Interactions
FROM bi.Interactions AS i
JOIN bi.Calendar AS c ON c.DateKey = i.DateKey
JOIN bi.Apps AS a ON a.AppKey = i.AppKey
WHERE i.DateKey BETWEEN @from AND @to
GROUP BY c.[Date], a.App
ORDER BY c.[Date];
```

Premium and free interactions a day (time series, stacked):

```sql
SELECT CAST(CAST(c.[Date] AS datetime2) AT TIME ZONE '$tz' AT TIME ZONE 'UTC' AS datetime) AS time,
       SUM(CAST(i.PremiumUse AS int)) AS Premium,
       COUNT(*) - SUM(CAST(i.PremiumUse AS int)) AS Free
FROM bi.Interactions AS i
JOIN bi.Calendar AS c ON c.DateKey = i.DateKey
WHERE i.DateKey BETWEEN @from AND @to
GROUP BY c.[Date]
ORDER BY c.[Date];
```

When Copilot is used: weekday by hour (heatmap or table):

```sql
SELECT c.DayOfWeekNumber, c.DayOfWeekName AS Weekday, i.HourOfDay AS Hour, COUNT(*) AS Interactions
FROM bi.Interactions AS i
JOIN bi.Calendar AS c ON c.DateKey = i.DateKey
WHERE i.DateKey BETWEEN @from AND @to
GROUP BY c.DayOfWeekNumber, c.DayOfWeekName, i.HourOfDay
ORDER BY c.DayOfWeekNumber, i.HourOfDay;
```

### Breakdowns

By app and category (table or pie):

```sql
SELECT a.AppCategory AS Category, a.App, COUNT(DISTINCT i.UserKey) AS [Active users], COUNT(*) AS Interactions
FROM bi.Interactions AS i
JOIN bi.Apps AS a ON a.AppKey = i.AppKey
WHERE i.DateKey BETWEEN @from AND @to
GROUP BY a.AppCategory, a.App
ORDER BY Interactions DESC;
```

By use case:

```sql
SELECT uc.UseCase AS [Use case], COUNT(DISTINCT i.UserKey) AS [Active users], COUNT(*) AS Interactions
FROM bi.Interactions AS i
JOIN bi.UseCases AS uc ON uc.UseCaseKey = i.UseCaseKey
WHERE i.DateKey BETWEEN @from AND @to
GROUP BY uc.UseCase
ORDER BY Interactions DESC;
```

By model:

```sql
SELECT m.ModelProvider AS Provider, m.ModelName AS Model, COUNT(*) AS Interactions
FROM bi.Interactions AS i
JOIN bi.Models AS m ON m.ModelKey = i.ModelKey
WHERE i.DateKey BETWEEN @from AND @to
GROUP BY m.ModelProvider, m.ModelName
ORDER BY Interactions DESC;
```

By department, with small groups left out (table):

```sql
SELECT ISNULL(NULLIF(u.Department, N''), N'(no department)') AS Department,
       COUNT(DISTINCT i.UserKey) AS [Active users],
       COUNT(*) AS Interactions,
       SUM(CAST(i.PremiumUse AS int)) AS [Premium interactions],
       CAST(1.0 * COUNT(*) / COUNT(DISTINCT i.UserKey) AS decimal(8, 1)) AS [Interactions per active user]
FROM bi.Interactions AS i
JOIN bi.Users AS u ON u.UserKey = i.UserKey
WHERE i.DateKey BETWEEN @from AND @to
GROUP BY ISNULL(NULLIF(u.Department, N''), N'(no department)')
HAVING COUNT(DISTINCT i.UserKey) >= 5
ORDER BY Interactions DESC;
```

By member of the leadership team (the second level of the org chart):

```sql
SELECT ISNULL(u.OrgLevel2, N'(not in the org chart)') AS [Reports to],
       COUNT(DISTINCT i.UserKey) AS [Active users], COUNT(*) AS Interactions
FROM bi.Interactions AS i
JOIN bi.Users AS u ON u.UserKey = i.UserKey
WHERE i.DateKey BETWEEN @from AND @to
GROUP BY ISNULL(u.OrgLevel2, N'(not in the org chart)')
HAVING COUNT(DISTINCT i.UserKey) >= 5
ORDER BY Interactions DESC;
```

By job family:

```sql
SELECT jf.JobFamily AS [Job family], COUNT(DISTINCT i.UserKey) AS [Active users], COUNT(*) AS Interactions
FROM bi.Interactions AS i
JOIN bi.JobFamilies AS jf ON jf.UserKey = i.UserKey
WHERE i.DateKey BETWEEN @from AND @to
GROUP BY jf.JobFamily
HAVING COUNT(DISTINCT i.UserKey) >= 5
ORDER BY Interactions DESC;
```

Filtered by the variables above:

```sql
SELECT CAST(CAST(c.[Date] AS datetime2) AT TIME ZONE '$tz' AT TIME ZONE 'UTC' AS datetime) AS time,
       COUNT(*) AS Interactions, COUNT(DISTINCT i.UserKey) AS [Active users]
FROM bi.Interactions AS i
JOIN bi.Calendar AS c ON c.DateKey = i.DateKey
JOIN bi.Users AS u ON u.UserKey = i.UserKey
WHERE i.DateKey BETWEEN @from AND @to
  AND i.AppKey IN ($app)
  AND ISNULL(NULLIF(u.Department, N''), N'(no department)') IN ($dept)
GROUP BY c.[Date]
ORDER BY c.[Date];
```

Departments by week, safe to show anyone (table, or a time series with `Department` as the metric):

```sql
SELECT CAST(CAST(dw.WeekEnding AS datetime2) AT TIME ZONE '$tz' AT TIME ZONE 'UTC' AS datetime) AS time,
       dw.Department AS metric, dw.ActivePeople AS [Active people]
FROM bi.DepartmentWeek AS dw
WHERE dw.Hidden = 0
  AND dw.WeekEnding BETWEEN CAST(CAST($__timeFrom() AS datetimeoffset) AT TIME ZONE '$tz' AS date)
                        AND CAST(CAST($__timeTo()   AS datetimeoffset) AT TIME ZONE '$tz' AS date)
ORDER BY dw.WeekEnding;
```

### Adoption and licenses

Adoption for the range (stat panels): the query in section 7, with the `DECLARE` above in place of its first line.

Adoption by week (time series): of the people licensed on each Friday, the share who used Copilot with a license in that week.

```sql
DECLARE @last int = (SELECT MAX(DateKey) FROM bi.Interactions WHERE DateKey <= @to);
WITH wk AS (SELECT WeekEnding FROM bi.Calendar WHERE DateKey BETWEEN @from AND @last
            GROUP BY WeekEnding HAVING COUNT(*) = 7),
lic AS (SELECT c.WeekEnding, l.UserKey
        FROM bi.LicensedDays AS l JOIN bi.Calendar AS c ON c.DateKey = l.DateKey
        WHERE c.[Date] = c.WeekEnding AND l.DateKey BETWEEN @from AND @last),
act AS (SELECT DISTINCT c.WeekEnding, i.UserKey
        FROM bi.Interactions AS i JOIN bi.Calendar AS c ON c.DateKey = i.DateKey
        WHERE i.DateKey BETWEEN @from AND @last AND i.LicenseStatusKey = 1)
SELECT CAST(CAST(wk.WeekEnding AS datetime2) AT TIME ZONE '$tz' AT TIME ZONE 'UTC' AS datetime) AS time,
       COUNT(lic.UserKey) AS [Licensed users],
       COUNT(act.UserKey) AS [Active licensed users],
       CAST(100.0 * COUNT(act.UserKey) / NULLIF(COUNT(lic.UserKey), 0) AS decimal(5, 1)) AS [Adoption %]
FROM wk
JOIN lic ON lic.WeekEnding = wk.WeekEnding
LEFT JOIN act ON act.WeekEnding = lic.WeekEnding AND act.UserKey = lic.UserKey
GROUP BY wk.WeekEnding
ORDER BY wk.WeekEnding;
```

This is a week's adoption. The dashboard's *Adoption rate* line is different: at each point, the share of license holders who used Copilot in the 28 days up to it.

Licensed users a day (time series):

```sql
SELECT CAST(CAST(c.[Date] AS datetime2) AT TIME ZONE '$tz' AT TIME ZONE 'UTC' AS datetime) AS time,
       COUNT(*) AS [Licensed users]
FROM bi.LicensedDays AS l
JOIN bi.Calendar AS c ON c.DateKey = l.DateKey
WHERE l.DateKey BETWEEN @from AND @to
GROUP BY c.[Date]
ORDER BY c.[Date];
```

Licenses added and removed a week (bar chart):

```sql
SELECT CAST(CAST(c.WeekEnding AS datetime2) AT TIME ZONE '$tz' AT TIME ZONE 'UTC' AS datetime) AS time,
       SUM(CASE WHEN lc.Change = 1 THEN 1 ELSE 0 END) AS Added,
       SUM(CASE WHEN lc.Change = -1 THEN 1 ELSE 0 END) AS Removed
FROM bi.LicenseChanges AS lc
JOIN bi.Calendar AS c ON c.DateKey = lc.DateKey
JOIN bi.Skus AS k ON k.SkuKey = lc.SkuKey
WHERE k.IsCopilot = 1 AND lc.DateKey BETWEEN @from AND @to
GROUP BY c.WeekEnding
ORDER BY c.WeekEnding;
```

Seats today (stat panels):

```sql
SELECT SkuPartNumber AS SKU, Bought, Assigned, Available
FROM bi.LicensePool
WHERE IsCopilot = 1 AND SnapshotDate = (SELECT MAX(SnapshotDate) FROM bi.LicensePool);
```

Seats over time (time series):

```sql
SELECT CAST(CAST(SnapshotDate AS datetime2) AT TIME ZONE '$tz' AT TIME ZONE 'UTC' AS datetime) AS time,
       SUM(Bought) AS Bought, SUM(Assigned) AS Assigned
FROM bi.LicensePool
WHERE IsCopilot = 1
  AND SnapshotDate BETWEEN CAST(CAST($__timeFrom() AS datetimeoffset) AT TIME ZONE '$tz' AS date)
                       AND CAST(CAST($__timeTo()   AS datetimeoffset) AT TIME ZONE '$tz' AS date)
GROUP BY SnapshotDate
ORDER BY SnapshotDate;
```

License holders who did not use Copilot in the range (table). **This names people.** Show it only where a named list is allowed, and not in privacy mode, where the names are pseudonyms:

```sql
DECLARE @last int = (SELECT MAX(DateKey) FROM bi.LicensedDays WHERE DateKey BETWEEN @from AND @to);
SELECT u.DisplayName AS Name, u.Department, u.ManagerName AS Manager, u.LicensedSinceUtc AS [Licensed since]
FROM bi.LicensedDays AS l
JOIN bi.Users AS u ON u.UserKey = l.UserKey
WHERE l.DateKey = @last
  AND NOT EXISTS (SELECT 1 FROM bi.Interactions AS i WHERE i.UserKey = l.UserKey AND i.DateKey BETWEEN @from AND @last)
ORDER BY u.Department, u.DisplayName;
```

### Sessions

Sessions a day, and prompts in each (time series):

```sql
SELECT CAST(CAST(c.[Date] AS datetime2) AT TIME ZONE '$tz' AT TIME ZONE 'UTC' AS datetime) AS time,
       COUNT(*) AS Sessions,
       CAST(1.0 * SUM(s.PromptCount) / COUNT(*) AS decimal(6, 2)) AS [Prompts per session],
       CAST(100.0 * SUM(CAST(s.IsMultiTurn AS int)) / COUNT(*) AS decimal(5, 1)) AS [Multi-turn %]
FROM bi.Sessions AS s
JOIN bi.Calendar AS c ON c.DateKey = s.DateKey
WHERE s.DateKey BETWEEN @from AND @to
GROUP BY c.[Date]
ORDER BY c.[Date];
```

How long sessions are (bar chart):

```sql
SELECT b.Prompts, COUNT(*) AS Sessions
FROM bi.Sessions AS s
CROSS APPLY (SELECT CASE WHEN s.PromptCount = 1 THEN '1' WHEN s.PromptCount <= 3 THEN '2-3'
                         WHEN s.PromptCount <= 10 THEN '4-10' ELSE 'Over 10' END AS Prompts,
                    CASE WHEN s.PromptCount = 1 THEN 1 WHEN s.PromptCount <= 3 THEN 2
                         WHEN s.PromptCount <= 10 THEN 3 ELSE 4 END AS Ord) AS b
WHERE s.DateKey BETWEEN @from AND @to
GROUP BY b.Prompts, b.Ord
ORDER BY b.Ord;
```

### Agents

The agents most used (table):

```sql
SELECT TOP (20) g.AgentName AS Agent, g.AgentSource AS Source,
       COUNT(DISTINCT i.UserKey) AS People, COUNT(*) AS Interactions
FROM bi.Interactions AS i
JOIN bi.Agents AS g ON g.AgentKey = i.AgentKey
WHERE i.DateKey BETWEEN @from AND @to AND i.AgentKey > 1
GROUP BY g.AgentName, g.AgentSource
ORDER BY Interactions DESC;
```

Agent use by source a week (time series, one line per source):

```sql
SELECT CAST(CAST(c.WeekEnding AS datetime2) AT TIME ZONE '$tz' AT TIME ZONE 'UTC' AS datetime) AS time,
       g.AgentSource AS metric, COUNT(*) AS Interactions
FROM bi.Interactions AS i
JOIN bi.Calendar AS c ON c.DateKey = i.DateKey
JOIN bi.Agents AS g ON g.AgentKey = i.AgentKey
WHERE i.DateKey BETWEEN @from AND @to AND i.AgentKey > 1
GROUP BY c.WeekEnding, g.AgentSource
ORDER BY c.WeekEnding;
```

What makers did (table):

```sql
SELECT e.Action, COUNT(*) AS Events, COUNT(DISTINCT e.BotSchemaName) AS Agents, COUNT(DISTINCT e.ActorKey) AS Makers
FROM bi.AgentEvents AS e
WHERE e.DateKey BETWEEN @from AND @to
GROUP BY e.Action
ORDER BY Events DESC;
```

### Security and content

Signals a day (time series):

```sql
SELECT CAST(CAST(c.[Date] AS datetime2) AT TIME ZONE '$tz' AT TIME ZONE 'UTC' AS datetime) AS time,
       SUM(CAST(i.JailbreakDetected AS int)) AS [Jailbreak attempts],
       SUM(CAST(i.PromptInjectionDetected AS int)) AS [Prompt injections],
       SUM(CAST(i.HasBlockedResource AS int)) AS [Blocked resources]
FROM bi.Interactions AS i
JOIN bi.Calendar AS c ON c.DateKey = i.DateKey
WHERE i.DateKey BETWEEN @from AND @to
GROUP BY c.[Date]
ORDER BY c.[Date];
```

What Copilot read, by kind (table or pie):

```sql
SELECT ft.FileType AS [Kind], COUNT(*) AS [Resources read], SUM(CAST(r.IsBlocked AS int)) AS Blocked
FROM bi.AccessedResources AS r
JOIN bi.FileTypes AS ft ON ft.FileTypeKey = r.FileTypeKey
WHERE r.DateKey BETWEEN @from AND @to
GROUP BY ft.FileType
ORDER BY [Resources read] DESC;
```

The sites Copilot read most (table):

```sql
SELECT TOP (25) s.Site, COUNT(*) AS [Resources read], COUNT(DISTINCT r.UserKey) AS People,
       SUM(CAST(r.IsBlocked AS int)) AS Blocked
FROM bi.AccessedResources AS r
JOIN bi.Sites AS s ON s.SiteKey = r.SiteKey
WHERE r.DateKey BETWEEN @from AND @to AND r.SiteKey > 1 AND s.IsPersonal = 0
GROUP BY s.Site
HAVING COUNT(DISTINCT r.UserKey) >= 5
ORDER BY [Resources read] DESC;
```

Labeled content read, by label (table). The name is the one an administrator gave, or the id:

```sql
SELECT COALESCE(n.Name, sl.LabelId) AS Label, COUNT(*) AS [Resources read], COUNT(DISTINCT r.UserKey) AS People
FROM bi.AccessedResources AS r
JOIN bi.SensitivityLabels AS sl ON sl.SensitivityLabelKey = r.SensitivityLabelKey
LEFT JOIN (SELECT Value, MAX(Name) AS Name FROM bi.ValueNames WHERE Name IS NOT NULL GROUP BY Value) AS n
       ON n.Value = sl.LabelId
WHERE r.DateKey BETWEEN @from AND @to AND r.SensitivityLabelKey > 1
GROUP BY COALESCE(n.Name, sl.LabelId)
ORDER BY [Resources read] DESC;
```

### Showing a viewer only their own people

`bi.OrgSecurity` has a row for each viewer and person they may see. Grafana's `${__user.login}` is the signed-in viewer. Where Grafana's login is the person's sign-in name (as with single sign-on to Entra), a join limits a panel to the viewer's part of the organization:

```sql
SELECT CAST(CAST(c.[Date] AS datetime2) AT TIME ZONE '$tz' AT TIME ZONE 'UTC' AS datetime) AS time,
       COUNT(*) AS Interactions, COUNT(DISTINCT i.UserKey) AS [Active users]
FROM bi.Interactions AS i
JOIN bi.Calendar AS c ON c.DateKey = i.DateKey
JOIN bi.OrgSecurity AS os ON os.UserKey = i.UserKey AND os.ViewerUpn = '${__user.login}'
WHERE i.DateKey BETWEEN @from AND @to
GROUP BY c.[Date]
ORDER BY c.[Date];
```

Know its limits before relying on it:

- It filters a query; it is not security. Anyone who can edit the dashboard, or use *Explore* on the data source, can take the join out. Give viewers the Viewer role only, and keep *Explore* from them.
- A viewer with no row in `bi.OrgSecurity` sees nothing. That is everyone, in privacy mode.
- `bi.OrgSecurity` is large: one row per person for each viewer who sees the whole organization. Always filter it by `ViewerUpn`.

### Freshness, and a rebuild under way

Days since the last day with data (stat panel, and an alert at 2 or more):

```sql
SELECT DATEDIFF(DAY, MAX(c.[Date]), CAST(SYSUTCDATETIME() AT TIME ZONE 'UTC' AT TIME ZONE '$tz' AS date)) AS [Days behind]
FROM bi.Calendar AS c
WHERE c.DateKey = (SELECT MAX(DateKey) FROM bi.Interactions);
```

With the two optional grants of section 5, when `bi` was last refreshed and whether a rebuild is running:

```sql
SELECT (SELECT MAX(EndedUtc) FROM bi.RefreshRun) AS [Last refresh (UTC)],
       (SELECT COUNT(*) FROM bi.RebuildInProgress) AS [Rebuild under way];
```

### Keeping queries fast

- **Always give a `DateKey` range.** The facts are columnstore indexes in date order; a range reads only its own row groups.
- **Aggregate in SQL.** Return a row per point drawn. Never return raw fact rows to Grafana.
- **Name columns.** A columnstore reads only the columns a query names.
- **`COUNT(DISTINCT UserKey)` is the costly part** of most queries. Over a year, by day, for every department at once, it is slow. Keep it to what a panel shows.
- **Join the calendar, not functions.** `JOIN bi.Calendar` for the week or month, in place of date arithmetic on `DateKey`.
- **Cache.** Where your Grafana has query caching, set an hour or more. The data changes once a night.
- **Stay clear of the nightly run** for scheduled reports and alerts. A panel that reads during it waits for the refresh's transaction to commit, unless the database uses read committed snapshot isolation, which is the administrator's choice.

## 9. The other layers

A report needs more than the `bi` views in three cases. Each needs a grant an administrator makes by hand, and each reads real names whatever privacy mode says.

### The agent inventory

`bi.Agents` knows an agent's identity, name and source: what the audit log says. Where an agent workbook is loaded, `dash` has the rest.

| Object | One row per | What it adds |
|---|---|---|
| `dash.AgentInventory` | Agent in the workbook | Owner, environment and its kind, authentication, whether it is quarantined, shared or in production, whether web search is on, whether it runs by itself (`IsAutonomous`), matched to the audit log's agent. |
| `dash.AgentChildLink` | Parent agent and child it calls | Connected and child agents. |
| `dash.Agent` | Agent (a view) | `bi.DimAgent` with the workbook's name and owner first, then the administrator's, then the audit log's; and first use. |
| `dash.AgentUse` | Agent | First and last day used, and interactions in all. |
| `dbo.AgentInventory`, `dbo.AgentsInProd`, `dbo.ParentChildAgent` | Row of each workbook sheet | Every column of the workbook as text, as it was loaded. |

The grant, if the administrator agrees:

```sql
GRANT SELECT ON dash.AgentInventory TO CopilotReporting;
GRANT SELECT ON dash.AgentChildLink TO CopilotReporting;
GRANT SELECT ON dash.Agent TO CopilotReporting;
GRANT SELECT ON dash.AgentUse TO CopilotReporting;
```

`dash.AgentInventory` and `dash.AgentChildLink` are emptied and filled again each night, in one transaction.

### The daily rollups

`dash.UserDay` is one row per person per day with any use: interactions, prompts, apps used, agent interactions, web-grounded, Copilot Chat, licensed use, Premium use, the three security counts, labeled content, sessions and multi-turn sessions. Every page of the dashboard reads it. It is far smaller than `bi.FactInteraction`, and has a columnstore index of its own.

It gives a report nothing the `bi` views do not, only speed for person-per-day questions over a long period. It is rebuilt for the night's days in one transaction, after `bi`. `dash.ResourceDay` is its counterpart for labels and sites. Both are in Part B.

### One record in full

`audit.RawLogFlattened` has one row per audit record with every field the record carries as a typed column: the record id, the UTC timestamp to the second, the conversation thread, the app identity and host, the agent, the model, the client region, and the lists of resources, labels and plugins. `audit.RawLogAccessedResource` has one row per resource with its name and address. `audit.vCopilotAuditRecord` gives the record's whole JSON.

Use them for drill-through: from a figure on a panel to the records behind it. They are wide and large, and they are person-level data in full. Two cautions:

- Timestamps here are UTC (`UtcTimeStamp`). The `bi` layer's days are local. Convert before comparing.
- An administrator can purge old records' JSON and flattened rows (retention). The `bi` facts keep those interactions; the drill-through then finds nothing.

### The layers in full

Every object, by layer. Part B has each one's columns.

**Loading and state** (`audit`, both databases). What the loads write while they run. Not for reports, except to show the pipeline's health.

| Object | What it is |
|---|---|
| `CopilotBackfillRange` | One row: the range this database holds. |
| `CopilotBackfillWindow`, `CopilotAuditStage` | The first load's search windows and downloaded rows. |
| `CopilotLoad` | One row per nightly load: its range, when it was merged, how many records were new, and, from 2.6.0, how many audit searches it took and how many of them had to be resubmitted (`Searches`, `SearchesResubmitted`). |
| `CopilotLoadWindow`, `CopilotLoadStage` | The load in progress. Emptied after each merge. |
| `PipelineSetting` | Settings and marks the procedures read: the reporting time zone, privacy mode, and the marks long jobs leave. |
| `PipelineRun`, `PipelineRunStep` | One row per nightly run and per step: status, the step that failed, the log's last lines. |
| `PipelineCertificate` | One row for each certificate the pipeline signs in with (the audit app's, the agent workbook app's, and the Foundry loader's where it has one of its own): subject, issuer, validity, thumbprint, the app it signs in as, and whether the job server holds it. |

**Final records** (`audit`, both databases).

| Object | What it is |
|---|---|
| `CopilotAudit` | Every audit record once, with its JSON compressed. Clustered columnstore. |
| `CopilotInteraction` | One slim row per interaction: day, user, app, model, thread, counts. |
| `vCopilotAuditRecord` | The records in the Purview portal export's shape, JSON decompressed. |
| `vCopilotDailyUsage` | Interactions, active users and prompts per day per app. |

**Flattened records** (`audit`, Copilot database).

| Object | What it is |
|---|---|
| `RawLogFlattened` | One row per record, every field a column. |
| `RawLogAccessedResource` | One row per resource read. |
| `RawLogContext` | One row per context: where the user was during the interaction. |
| `RawLogRecordShape` | Counts of the fields that never vary, kept in place of storing them. |
| `ValueCatalog`, `ValueCatalogColumn` | Every distinct value of 23 coded columns. |
| `AgentCatalog` | Every agent the log names, with its latest name. |

**Licenses and the directory** (`audit`, Copilot database).

| Object | What it is |
|---|---|
| `UserDirectory` | One row per user in Active Directory: attributes, manager, the chain `Mgr1` to `Mgr15`. Replaced whole each night. |
| `GroupMember` | Members of the AD groups granted *Organization* in the dashboard. |
| `CopilotLicense` | One row per user the license tool looked up: licensed or not, the SKU, since when. |
| `LicenseSku`, `LicenseSkuDay`, `UserLicenseSku` | The tenant's SKUs today, the same per night, and every SKU each person holds. |
| `vCopilotAdoption`, `vCopilotUsageByDepartment` | Licensed users against use; use per department, day and app. |

**Sessions** (`audit`, Copilot database).

| Object | What it is |
|---|---|
| `SessionAppCategory` | App to category. Administrators edit it. |
| `CopilotSession` | One row per inferred session. |
| `CopilotWeeklyRollup` | One row per week, user, app, category and license. |
| `CopilotSessionRun` | One row per session build. |
| `vCopilotSessionSummary`, `vCopilotWeeklyRollup` | The two tables in an older CSV's column names. |

**CSV import** (`audit`). `CopilotCsvImport`, `CopilotCsvFile`, `CopilotCsvReject`, `CopilotCsvStage`: one row per run, per file, per rejected row, and the staged rows.

**AI usage outside Microsoft 365 Copilot** (`audit`, Copilot database; 2.6.2). Filled by two optional loaders, each from its own service. Nothing in `bi` or `dash` reads them yet, and the reporting role does not hold them: to let a reporting account read them, an administrator runs `GRANT SELECT ON audit.GitHubCopilotDay TO CopilotReporting;` and the same for `audit.GitHubCopilotSeat`, `audit.GitHubCopilotUserDay`, `audit.GitHubCopilotCost`, `audit.FoundryDeployment`, `audit.FoundryUsageDay` and `audit.FoundryCostDay`, for the tables wanted. Days are UTC days. A person is a GitHub login (`Login`, `UserId`), which is not a Microsoft 365 sign-in: nothing joins it to `bi.Users` yet. Foundry's usage has no person: Azure Monitor reports by account, deployment and model.

| Object | What it is |
|---|---|
| `GitHubCopilotSeat` | One row per seat as GitHub lists it now: the login, the organization and team it was assigned through, the plan, when it was assigned and last used. |
| `GitHubCopilotDay` | One row per day for the enterprise or organization: daily, weekly and monthly active people, interactions, code generated and accepted, lines of code; the whole record in `Json`. Up to three days behind. |
| `GitHubCopilotUserDay` | One row per person per day they used it: interactions, code, AI credits used, and whether chat, an agent or the command line. |
| `GitHubCopilotCost` | What GitHub billed for Copilot: a row per day, product, SKU, model and organization, with quantity, price and gross, discount and net amounts in US dollars. |
| `FoundryDeployment` | One row per model deployment of each Azure OpenAI and Foundry account in the subscriptions loaded: model, version, SKU, capacity. |
| `FoundryUsageDay` | One row per deployment, model and day: requests, input, output and total tokens. |
| `FoundryCostDay` | One row per account, meter and day: actual cost before tax, in the billing currency. By meter, not by deployment. |
| `ExternalUsageRun` | One row per run of either loader: source, days covered, status (Succeeded, Partial, Failed), rows written, and what was loaded or why not. |

To put cost beside use for Foundry, join `FoundryCostDay` to `FoundryUsageDay` on `SubscriptionId`, `ResourceGroup`, `Account` and `Day`, summing each side first: one account's meters and its deployments do not pair one to one. For GitHub, `GitHubCopilotCost.NetAmount` by `UsageDate` is what was charged; `GitHubCopilotUserDay.AiCreditsUsed` is each person's metered use, at 0.01 US dollars a credit at list price.

**License events** (`audit`, license-event database).

| Object | What it is |
|---|---|
| `LicenseEvent`, `LicenseEventChunk`, `LicenseEventProperty` | One row per directory audit record; the fragments of large ones; one row per property changed. |
| `LicenseSkuChange`, `LicenseSkuChangeRun` | The SKUs each change added and removed, and when they were worked out. |
| `vLicenseChange`, `vLicenseSkuChange`, `vLicenseSkuChangeFromRecords`, `vLicenseLifecycle` | License changes deduplicated; SKUs added and removed per change; provisioning and revocation per user. |
| `AgentEvent` | One row per Copilot Studio authoring record. |

**The star schema** (`bi`). 13 dimensions (`DimDate`, `DimUser`, `DimApp`, `DimAgent`, `DimModel`, `DimSku`, `DimLicenseStatus`, `DimLicenseType`, `DimSensitivityLabel`, `DimFileType`, `DimResourceType`, `DimResourceAction`, `DimSite`), 6 facts (`FactInteraction`, `FactSession`, `FactLicenseChange`, `FactLicensedDay`, `FactAccessedResource`, `FactAgentEvent`), `RefreshRun`, `RebuildInProgress`, and the 26 views of section 6.

**The dashboard** (`dash`).

| Object | What it is |
|---|---|
| `Setting` | The dashboard's settings: the top of the organization, minimum group size, default period, costs, alert thresholds. |
| `RoleGrant` | Roles granted to people and AD groups. |
| `AgentAlias`, `SensitivityLabel`, `ValueName` | Names administrators gave agents, labels and coded values. |
| `AuditLog` | Every page view, refusal, export and admin change in the dashboard. |
| `Person`, `OrgClosure` | The org chart: each person's level and headcount, and every manager and person under them. |
| `UserDay`, `ResourceDay`, `FlaggedInteraction`, `AgentUse` | The rollups. |
| `AgentInventory`, `AgentChildLink`, `AgentInventoryRun`, `Agent` | The agent workbook. |
| `RefreshRun` | One row per dashboard refresh. |
| `Secret`, `Subscription`, `NotificationLog`, `AlertState`, `DigestPreference`, `DigestSend`, `Extract` | Mail, alerts, digests and scheduled extracts. |
| `Target`, `TimeSaving`, `TrainingAttendance`, `LicenseAction`, `LicenseContract` | Targets, the value model, training records, license actions and contracts. |
| `FlagCase`, `NonHumanException` | Security's triage of flagged interactions; accounts confirmed to be people. |
| `JobFamilyRule`, `UseCaseRule`, `EventMarker` | The rules behind `bi.JobFamilies` and `bi.UseCases`, and the events behind `bi.EventMarkers`. |
| `SavedView` | Unused since 2.4.4. |

### Procedures and functions

None is for a reporting account to run. They are listed so that a name in a log or a script is not a mystery.

| Name | What it does |
|---|---|
| `audit.fnShredCopilotInteraction` | Reads an interaction's columns out of a record's JSON. |
| `audit.ReportingTimeZone()` | Returns the reporting time zone. |
| `audit.usp_MergeCopilotLoad`, `audit.usp_MergeCopilotCsv` | Append a nightly load, or a CSV import, to the final records. |
| `audit.usp_FlattenCopilotAudit` | Flattens records into `RawLogFlattened` and its element tables. |
| `audit.usp_CatalogValues`, `audit.usp_CatalogAgents`, `audit.usp_BackfillAgentNames` | Keep the value catalog and the agent catalog; read agent ids and names from stored records once, on upgrade. |
| `audit.usp_PurgeCopilotAuditJson` | Clears the JSON of records past retention. |
| `audit.usp_BuildCopilotSessions` | Builds sessions and the weekly rollup. |
| `audit.usp_ShredLicenseEvents`, `audit.usp_RefreshLicenseSkuChange`, `audit.usp_ShredAgentEvents` | Shred license events and Copilot Studio records, in the license-event database. |
| `bi.usp_RefreshBi`, `bi.usp_NameAgents`, `bi.usp_MergeAgents`, `bi.fnAgentIdentity` | Fill the star schema; name the agents; make one agent of the identities the log gives the same agent (2.6.3); the rule for which agent a record belongs to. |
| `dash.usp_RefreshDashboard`, `dash.fnScopeUsers` | Rebuild the org chart and rollups; the people under a manager. |
| `dash.usp_SetPrivacyMode`, `dash.usp_ApplyRetention`, `dash.usp_SetSecret`, `dash.usp_GetSecret`, `dash.fnValueName` | The dashboard's own. |

## 10. Checking a report against the dashboard

When a Grafana figure and the dashboard's disagree, the cause is nearly always one of these.

| Difference | The dashboard's rule |
|---|---|
| **The period's end** | The last complete day with data: the last day before today, in the reporting time zone, that has any use. Today is left out, since it is still arriving. A Grafana range that ends *now* includes today. |
| **The period's length** | 7, 14, 30, 90, 180 or 365 days, ending on that day. "30 days" is that day and the 29 before it. |
| **Scope** | A viewer sees their own part of the organization unless they are in its top two levels or hold *Organization*. Compare as someone who sees everyone. |
| **Small groups** | Any group under the minimum size is combined or left out. |
| **Daily charts** | Drawn to two days before the last record's day. The KPIs and tables count those two days. |
| **Weekly charts** | Whole Saturday-to-Friday weeks only. |
| **Adoption rate line** | A rolling 28 days. The *Adoption* KPI is the period's, as in section 7. |
| **Agent names and owners** | The dashboard shows the workbook's or an administrator's name first. `bi.Agents.AgentName` is the audit log's unless the workbook renamed it. |

To reproduce a dashboard period of N days exactly:

```sql
DECLARE @days int = 30;
DECLARE @today int = CONVERT(int, CONVERT(char(8), CAST(SYSUTCDATETIME() AT TIME ZONE 'UTC' AT TIME ZONE 'Eastern Standard Time' AS date), 112));
DECLARE @end date = (SELECT MAX(c.[Date]) FROM bi.Calendar AS c
                     WHERE c.DateKey = (SELECT MAX(DateKey) FROM bi.Interactions WHERE DateKey < @today));
DECLARE @to int = CONVERT(int, CONVERT(char(8), @end, 112)),
        @from int = CONVERT(int, CONVERT(char(8), DATEADD(DAY, -(@days - 1), @end), 112));
SELECT @from AS FromKey, @to AS ToKey,
       COUNT(*) AS Interactions, COUNT(DISTINCT UserKey) AS [Active users], SUM(CAST(PremiumUse AS int)) AS [Premium interactions]
FROM bi.Interactions
WHERE DateKey BETWEEN @from AND @to;
```

Replace `Eastern Standard Time` with the reporting time zone. The result should equal the dashboard's *Interactions* and *Active users* at 30 days, seen by someone with the whole organization in scope.

The dashboard also answers JSON at `/api/v1` (usage, units, security, license pool and more), under each caller's own scope and roles. For a figure that must match the dashboard exactly, including its scope and small-group rules, reading that API from Grafana is an alternative to SQL. [DASHBOARD.md](DASHBOARD.md), *The data API*, lists the endpoints.

## 11. Words used here

| Word | Meaning |
|---|---|
| Interaction | One prompt and its response: one audit record. |
| Session | A person's prompts with less than 60 minutes between them, in one local day. Inferred. |
| Active user | A person with at least one interaction in the period. |
| Licensed | Holding a Copilot SKU. *At the time of use* on a fact; *today* on `bi.Users`. |
| Premium use | An interaction made with a license that needed it. |
| Agent | A Copilot Studio or Agent Builder agent, or one of Microsoft's, that answered in place of Copilot itself. |
| Maker | A person who created or changed an agent. |
| Resource | Something Copilot read to answer: a file, a mail, a message, a web search. |
| Reporting time zone | The one zone every day and hour in `bi` is cut in. |
| Minimum group size | The smallest group the dashboard shows a figure for. 5 by default. |
| Privacy mode | The setting under which names are pseudonyms. |
| Flatten | To write every field of a record's JSON into columns. |
| Shred | To read license events or agent events out of their records. |
| Finalize | To build the final record tables from the first load's staging. |

---

# Part B. Every table and view, column by column

Generated from [SCHEMA.md](SCHEMA.md), which is read from the metadata of installed databases and carries the notes the scripts' DDL has. Types are SQL Server's. **no** under Null means NOT NULL. Each object names the script that creates it, whose text is in Part C.

*In the full reference attached to each release.*

<!-- generated:columns:start -->



<!-- generated:columns:end -->

---

# Part C. The scripts

The full text of every script that creates or fills the schema, in the order a new install runs them: the installer, the SQL scripts it runs, the two SQL scripts other tools run, and the three PowerShell scripts that create tables or decide the order in which they are filled. Generated from the files themselves.

Not here, because they load data and create nothing of their own: `Invoke-CopilotAuditBackfill.ps1` (the audit log loads), `tools/Import-CopilotAuditCsv.ps1`, `tools/Export-UsersWithHierarchy-ToSql.ps1` and `tools/Update-CopilotAuditEnrichment.ps1`. Each runs one of the SQL scripts below before it loads, and is in the repository.

*In the full reference attached to each release.*

<!-- generated:scripts:start -->



<!-- generated:scripts:end -->
