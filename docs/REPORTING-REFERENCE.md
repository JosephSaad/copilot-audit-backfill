# Reporting on the Copilot data: the reference

Everything a team needs to build its own reports on the Copilot audit databases, in Grafana or any other tool that reads SQL Server: what the databases hold, how each table is created and filled, what every column means, how the figures are defined, queries to start from, and the full text of the scripts that create the schema.

**[REPORTING.md](REPORTING.md) is the brief version** of this document, about ten pages. Read it first. It names the section here that continues each of its own.

Written for release 2.5.9, and current for 2.6.0. The SQL in Part A was run against a demo database of that release, as a member of the reporting role, with Grafana's macros replaced by fixed dates. It was not run inside Grafana: check the Grafana settings and macros against your version.

## How this document is laid out

| Part | What it is | Kept current by |
|---|---|---|
| **A. The guide** | How the schema is created, filled and used, the reporting views column by column, the figures, the Grafana setup and queries, and the other layers. | Hand, each release |
| **B. Every table and view, column by column** | All 95 tables and 37 views: types, keys, defaults, indexes, and the script that creates each. The same text as [SCHEMA.md](SCHEMA.md). | `tools/dev/schema/Build-ReportingReference.ps1`, from `docs/SCHEMA.md` |
| **C. The scripts** | The full text of the installer, the 18 SQL scripts and the three other PowerShell scripts that create or fill the schema. | The same tool, from the scripts themselves |

Parts B and C are generated. A test in CI (`tests/Test-ReportingReference.ps1`) fails when either is behind its source, so what you read here is what the release runs.

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
| `audit` | 40 | 10 | Loading state and staging; the final records; the flattened records; licenses and the user directory; sessions; the CSV import; and, in the license-event database, the license events. |
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
| 1 | `Create-CopilotAuditSchema.sql` | Both | The loading tables: `CopilotBackfillRange`, `CopilotBackfillWindow`, `CopilotAuditStage`, `CopilotLoad`, `CopilotLoadWindow`, `CopilotLoadStage`. `PipelineSetting`, `PipelineRun`, `PipelineRunStep`. The functions `audit.fnShredCopilotInteraction` and `audit.ReportingTimeZone`. |
| 2 | `Create-CopilotLoadProcedure.sql` | Both | `audit.usp_MergeCopilotLoad`. |
| 3 | `Create-CopilotFlattenedSchema.sql` | Copilot | `RawLogFlattened`, `RawLogAccessedResource`, `RawLogContext`, `RawLogRecordShape`, `ValueCatalog`, `ValueCatalogColumn`, `AgentCatalog`; `CopilotLicense` if it is not there yet. |
| 4 | `Create-CopilotFlattenProcedure.sql` | Copilot | `audit.usp_FlattenCopilotAudit`, `audit.usp_CatalogValues`, `audit.usp_CatalogAgents`, `audit.usp_BackfillAgentNames`. |
| 5 | `Create-CopilotPurgeProcedure.sql` | Copilot | `audit.usp_PurgeCopilotAuditJson`. |
| 6 | `Create-CopilotEnrichmentSchema.sql` | Copilot | `CopilotLicense`, `UserDirectory`, `GroupMember`, `LicenseSku`, `LicenseSkuDay`, `UserLicenseSku`; the views `vCopilotAdoption` and `vCopilotUsageByDepartment`. |
| 7 | `Create-CopilotSessionSchema.sql` | Copilot | `SessionAppCategory` (seeded), `CopilotSession`, `CopilotWeeklyRollup`, `CopilotSessionRun`; the views `vCopilotSessionSummary` and `vCopilotWeeklyRollup`. |
| 8 | `Create-CopilotSessionProcedure.sql` | Copilot | `audit.usp_BuildCopilotSessions`. |
| 9 | `Create-CopilotBiSchema.sql` | Copilot | The `bi` schema: every `Dim` and `Fact` table, `RefreshRun`, `RebuildInProgress`, `bi.fnAgentIdentity`, and 19 of the views. |
| 10 | `Create-CopilotBiProcedure.sql` | Copilot | `bi.usp_RefreshBi` and `bi.usp_NameAgents`. |
| 11 | `Create-CopilotDashboardSchema.sql` | Copilot, with `-Dashboard` | The `dash` schema's core: `Setting`, `RoleGrant`, `AgentAlias`, `SensitivityLabel`, `AuditLog`, `Person`, `OrgClosure`, `UserDay`, `ResourceDay`, `FlaggedInteraction`, `AgentUse`, `RefreshRun`, `AgentInventory`, `AgentChildLink`, `AgentInventoryRun`; the view `dash.Agent`; `dash.fnScopeUsers`. |
| 12 | `Create-CopilotDashboardProcedure.sql` | Copilot, with `-Dashboard` | `dash.usp_RefreshDashboard`. |
| 13 | `Create-CopilotOperationsSchema.sql` | Copilot, with `-Dashboard` | The operations tables (`Secret`, `Subscription`, `NotificationLog`, `AlertState`, `Target`, `TimeSaving`, `TrainingAttendance`, `ValueName`, `FlagCase`, `DigestPreference`, `DigestSend`, `LicenseAction`, `LicenseContract`, `SavedView`, `NonHumanException`, `JobFamilyRule`, `UseCaseRule`, `EventMarker`, `Extract`); `audit.GroupMember` if it is not there yet; the role `CopilotInsightsApp` and its grants; privacy mode and retention; and 7 more `bi` views: `OrgSecurity`, `ValueNames`, `LicensePool`, `UseCases`, `JobFamilies`, `EventMarkers`, `DepartmentWeek`. |
| 14 | `Create-LicenseEventSchema.sql` | License-event | `LicenseEvent`, `LicenseEventChunk`, `LicenseEventProperty`, `LicenseSkuChange`, `LicenseSkuChangeRun`; the views `vLicenseChange`, `vLicenseSkuChangeFromRecords`, `vLicenseLifecycle`. |
| 15 | `Create-LicenseEventProcedure.sql` | License-event | `audit.usp_ShredLicenseEvents`, `audit.usp_RefreshLicenseSkuChange`, and the view `vLicenseSkuChange` over the kept changes. |
| 16 | `Create-AgentEventProcedure.sql` | License-event | `audit.AgentEvent`, `audit.usp_ShredAgentEvents`. |

Two more SQL scripts are not run by the installer:

| Script | Run by | Creates |
|---|---|---|
| `Finalize-CopilotAudit.sql` | `Invoke-CopilotAuditBackfill.ps1 -Finalize`, once, after the first load of history | `audit.CopilotAudit` and `audit.CopilotInteraction`, built from staging; the views `vCopilotAuditRecord` and `vCopilotDailyUsage`. |
| `Create-CopilotCsvImport.sql` | `tools/Import-CopilotAuditCsv.ps1`, on each import | `CopilotCsvImport`, `CopilotCsvFile`, `CopilotCsvReject`, `CopilotCsvStage`, `audit.usp_MergeCopilotCsv`. In a database with no backfill, the final tables and views too. |

**What this means for a report.**

- **The seven `bi` views of script 13 exist only where the dashboard's schema is installed** (`-Dashboard`). A tenant without the dashboard has 19 `bi` views. `bi.LicensePool`, `bi.DepartmentWeek`, `bi.UseCases`, `bi.JobFamilies`, `bi.EventMarkers`, `bi.ValueNames` and `bi.OrgSecurity` are the seven.
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
| After a good night | `dash.usp_ApplyRetention` | Deletes what the administrators' retention settings say. |

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
7. Names the agents (`bi.usp_NameAgents`), and writes a row to `bi.RefreshRun`.

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
| `CopilotLoad` | One row per nightly load: its range, when it was merged, how many records were new. |
| `CopilotLoadWindow`, `CopilotLoadStage` | The load in progress. Emptied after each merge. |
| `PipelineSetting` | Settings and marks the procedures read: the reporting time zone, privacy mode, and the marks long jobs leave. |
| `PipelineRun`, `PipelineRunStep` | One row per nightly run and per step: status, the step that failed, the log's last lines. |

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
| `bi.usp_RefreshBi`, `bi.usp_NameAgents`, `bi.fnAgentIdentity` | Fill the star schema; name the agents; the rule for which agent a record belongs to. |
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

<!-- generated:columns:start -->

### Loading and state

What the backfill and the nightly loads write while they run. The engine resumes from these after any interruption.

#### audit.CopilotBackfillRange

*Table.* One row (Id = 1): the range and operation set this database holds, and when the backfill completed. -Incremental continues from its end. A CSV import that starts a database writes it with Source = 'CSV'.

**Script** `Create-CopilotAuditSchema.sql` · **In** Copilot and license-event databases · **Columns added by** `Create-CopilotCsvImport.sql`

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `Id` | tinyint | **no** | **PK** |  |
| 2 | `RangeStartUtc` | datetime2(0) | **no** |  |  |
| 3 | `RangeEndUtc` | datetime2(0) | **no** |  |  |
| 4 | `Operations` | nvarchar(400) | **no** |  |  |
| 5 | `StartedUtc` | datetime2(0) | **no** | `sysutcdatetime` |  |
| 6 | `CompletedUtc` | datetime2(0) | yes |  |  |
| 7 | `Source` | varchar(8) | yes |  | 'CSV' when a CSV import wrote the row |

Indexes and constraints:

- `PK_CopilotBackfillRange`: primary key, clustered (Id)
- `CK_CopilotBackfillRange_Single`: check `([Id]=(1))`

#### audit.CopilotBackfillWindow

*Table.* One row per search window of the backfill: its time range, status (Pending, Submitted, Ready, Downloading, Loaded, Split, Failed), the Graph query, attempts and rows loaded. The resume state; finalize checks it covers the range with no gap.

**Script** `Create-CopilotAuditSchema.sql` · **In** Copilot and license-event databases

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `WindowId` | bigint | **no** | **PK** identity |  |
| 2 | `ParentWindowId` | bigint | yes |  | set when a window was split |
| 3 | `StartUtc` | datetime2(0) | **no** |  |  |
| 4 | `EndUtc` | datetime2(0) | **no** |  |  |
| 5 | `Status` | varchar(16) | **no** |  | Pending\|Submitted\|Ready\|Downloading\|Loaded\|Split\|Failed |
| 6 | `QueryId` | varchar(64) | yes |  | Graph auditLogQuery id |
| 7 | `Attempts` | int | **no** | `0` |  |
| 8 | `Submissions` | int | **no** | `0` |  |
| 9 | `ApproxRecords` | bigint | yes |  | service's approximateReturnedRecordCount |
| 10 | `RowsLoaded` | bigint | yes |  |  |
| 11 | `SubmittedUtc` | datetime2(0) | yes |  |  |
| 12 | `CompletedUtc` | datetime2(0) | yes |  |  |
| 13 | `LastError` | nvarchar(2000) | yes |  |  |
| 14 | `CreatedUtc` | datetime2(0) | **no** | `sysutcdatetime` |  |
| 15 | `UpdatedUtc` | datetime2(0) | **no** | `sysutcdatetime` |  |

Indexes and constraints:

- `PK_CopilotBackfillWindow`: primary key, clustered (WindowId)
- `CK_CopilotBackfillWindow_Range`: check `([EndUtc]>[StartUtc])`

#### audit.CopilotAuditStage

*Table.* Every row the backfill downloaded, per window attempt, with the record JSON gzipped in AuditDataGz. Finalize keeps each window's final attempt. A heap; drop it once the final tables are checked.

**Script** `Create-CopilotAuditSchema.sql` · **In** Copilot and license-event databases

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `WindowId` | bigint | **no** |  |  |
| 2 | `Attempt` | int | **no** |  |  |
| 3 | `RecordId` | varchar(64) | **no** |  |  |
| 4 | `CreatedDateTime` | datetime2(7) | **no** |  |  |
| 5 | `RecordType` | varchar(128) | yes |  |  |
| 6 | `Operation` | varchar(128) | yes |  |  |
| 7 | `OrganizationId` | varchar(64) | yes |  |  |
| 8 | `UserType` | varchar(64) | yes |  |  |
| 9 | `UserId` | nvarchar(512) | yes |  |  |
| 10 | `UserPrincipalName` | nvarchar(512) | yes |  |  |
| 11 | `Service` | varchar(128) | yes |  |  |
| 12 | `ObjectId` | nvarchar(4000) | yes |  |  |
| 13 | `ClientIp` | varchar(128) | yes |  |  |
| 14 | `AdministrativeUnits` | nvarchar(4000) | yes |  |  |
| 15 | `AuditDataGz` | varbinary(max) | yes |  | gzip of the full audit record JSON (UTF-16): |
| 16 | `LoadedUtc` | datetime2(0) | **no** | `sysutcdatetime` |  |

#### audit.CopilotLoad

*Table.* One row per -Incremental load: its range, operations, when it finished downloading and was merged, how many records were new or already there, and how many audit searches it took.

**Script** `Create-CopilotAuditSchema.sql` · **In** Copilot and license-event databases · **Columns added by** `Create-CopilotLoadProcedure.sql`

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `LoadId` | int | **no** | **PK** identity |  |
| 2 | `RangeStartUtc` | datetime2(0) | **no** |  |  |
| 3 | `RangeEndUtc` | datetime2(0) | **no** |  |  |
| 4 | `Operations` | nvarchar(400) | **no** |  |  |
| 5 | `StartedUtc` | datetime2(0) | **no** | `sysutcdatetime` |  |
| 6 | `DownloadedUtc` | datetime2(0) | yes |  | every window Loaded; ready to merge |
| 7 | `MergedUtc` | datetime2(0) | yes |  | appended to audit.CopilotAudit |
| 8 | `RowsStaged` | bigint | yes |  | final-attempt rows in the load |
| 9 | `RowsAdded` | bigint | yes |  | new to audit.CopilotAudit |
| 10 | `RowsAlreadyLoaded` | bigint | yes |  | in the lookback, already present |
| 11 | `DerivedRefreshedUtc` | datetime2(0) | yes |  | the derived tables brought up to date with it (2.4.12); empty on a merged load, a refresh the next -Incremental run makes up |
| 12 | `Searches` | int | yes |  | the audit searches the load took, one per window, counted at the merge before its windows are emptied (after 2.5.4); empty on a load merged before |
| 13 | `SearchesResubmitted` | int | yes |  | those Microsoft Purview had to be asked for more than once |

Indexes and constraints:

- `PK_CopilotLoad`: primary key, clustered (LoadId)
- `CK_CopilotLoad_Range`: check `([RangeEndUtc]>[RangeStartUtc])`

#### audit.CopilotLoadWindow

*Table.* The search windows of the load in progress, as CopilotBackfillWindow. Emptied after each merge.

**Script** `Create-CopilotAuditSchema.sql` · **In** Copilot and license-event databases

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `WindowId` | bigint | **no** | **PK** identity |  |
| 2 | `ParentWindowId` | bigint | yes |  |  |
| 3 | `StartUtc` | datetime2(0) | **no** |  |  |
| 4 | `EndUtc` | datetime2(0) | **no** |  |  |
| 5 | `Status` | varchar(16) | **no** |  |  |
| 6 | `QueryId` | varchar(64) | yes |  |  |
| 7 | `Attempts` | int | **no** | `0` |  |
| 8 | `Submissions` | int | **no** | `0` |  |
| 9 | `ApproxRecords` | bigint | yes |  |  |
| 10 | `RowsLoaded` | bigint | yes |  |  |
| 11 | `SubmittedUtc` | datetime2(0) | yes |  |  |
| 12 | `CompletedUtc` | datetime2(0) | yes |  |  |
| 13 | `LastError` | nvarchar(2000) | yes |  |  |
| 14 | `CreatedUtc` | datetime2(0) | **no** | `sysutcdatetime` |  |
| 15 | `UpdatedUtc` | datetime2(0) | **no** | `sysutcdatetime` |  |

Indexes and constraints:

- `PK_CopilotLoadWindow`: primary key, clustered (WindowId)
- `CK_CopilotLoadWindow_Range`: check `([EndUtc]>[StartUtc])`

#### audit.CopilotLoadStage

*Table.* The rows of the load in progress, as CopilotAuditStage. Emptied after each merge.

**Script** `Create-CopilotAuditSchema.sql` · **In** Copilot and license-event databases

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `WindowId` | bigint | **no** |  |  |
| 2 | `Attempt` | int | **no** |  |  |
| 3 | `RecordId` | varchar(64) | **no** |  |  |
| 4 | `CreatedDateTime` | datetime2(7) | **no** |  |  |
| 5 | `RecordType` | varchar(128) | yes |  |  |
| 6 | `Operation` | varchar(128) | yes |  |  |
| 7 | `OrganizationId` | varchar(64) | yes |  |  |
| 8 | `UserType` | varchar(64) | yes |  |  |
| 9 | `UserId` | nvarchar(512) | yes |  |  |
| 10 | `UserPrincipalName` | nvarchar(512) | yes |  |  |
| 11 | `Service` | varchar(128) | yes |  |  |
| 12 | `ObjectId` | nvarchar(4000) | yes |  |  |
| 13 | `ClientIp` | varchar(128) | yes |  |  |
| 14 | `AdministrativeUnits` | nvarchar(4000) | yes |  |  |
| 15 | `AuditDataGz` | varbinary(max) | yes |  |  |
| 16 | `LoadedUtc` | datetime2(0) | **no** | `sysutcdatetime` |  |

#### audit.PipelineSetting

*Table.* Settings the procedures read, one row each: ReportingTimeZone, the Windows time zone name every procedure cuts days and hours in (read through audit.ReportingTimeZone(); Eastern Standard Time when unset; written by Install-CopilotAudit.ps1 -ReportingTimeZone, which refuses a change once records are loaded), and from 2.3.0 PrivacyMode and PseudonymSalt (written by dash.usp_SetPrivacyMode from Admin &gt; Privacy and retention). It also holds the marks a long job leaves: FullFlattenUtc when a flatten of every record has finished (2.4.12) and, in the license-event database, LicenseShredInProgress while a license shred runs (the time it shreds from; still there after a shred that was stopped, with the time that one began) and LicenseShredCompleteUtc when a shred of every record last finished, or the installer was told that one had (2.5.4).

**Script** `Create-CopilotAuditSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `Name` | sysname | **no** | **PK** |  |
| 2 | `Value` | nvarchar(400) | yes |  |  |
| 3 | `UpdatedUtc` | datetime2(0) | **no** | `sysutcdatetime` |  |
| 4 | `UpdatedBy` | nvarchar(256) | **no** | `suser_sname` |  |

Indexes and constraints:

- `PK_PipelineSetting`: primary key, clustered (Name)

#### audit.PipelineRun

*Table.* One row per run of Invoke-CopilotPipeline.ps1 (2.3.0): kind (Nightly or History), host and account, status, the step that failed and the log's last lines, and the app certificate it signed in with and its expiry, for the dashboard's alerts.

**Script** `Create-CopilotAuditSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `RunId` | int | **no** | **PK** identity |  |
| 2 | `Kind` | varchar(16) | **no** |  | Nightly \| History |
| 3 | `StartedUtc` | datetime2(0) | **no** | `sysutcdatetime` |  |
| 4 | `EndedUtc` | datetime2(0) | yes |  |  |
| 5 | `Status` | varchar(16) | **no** | `'Running'` | Running \| Succeeded \| Failed |
| 6 | `FailedStep` | nvarchar(200) | yes |  |  |
| 7 | `Detail` | nvarchar(4000) | yes |  | the failed step's last lines |
| 8 | `Host` | nvarchar(128) | yes |  |  |
| 9 | `RunAs` | nvarchar(256) | yes |  |  |
| 10 | `CertificateThumbprint` | varchar(64) | yes |  |  |
| 11 | `CertificateExpiresUtc` | datetime2(0) | yes |  |  |
| 12 | `CertificateSubject` | nvarchar(400) | yes |  |  |

Indexes and constraints:

- `PK_PipelineRun`: primary key, clustered (RunId)

#### audit.PipelineRunStep

*Table.* One row per step of a pipeline run (2.3.0): its name, start, end and exit code.

**Script** `Create-CopilotAuditSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `RunId` | int | **no** | **PK** |  |
| 2 | `StepNo` | tinyint | **no** | **PK** |  |
| 3 | `Name` | nvarchar(200) | **no** |  |  |
| 4 | `StartedUtc` | datetime2(0) | **no** | `sysutcdatetime` |  |
| 5 | `EndedUtc` | datetime2(0) | yes |  |  |
| 6 | `ExitCode` | int | yes |  |  |

Indexes and constraints:

- `PK_PipelineRunStep`: primary key, clustered (RunId, StepNo)

### Final records

Every audit record once, and the slim reporting table. Built by finalize, then appended to by each load and CSV import.

#### audit.CopilotAudit

*Table.* One row per audit record, deduplicated by RecordId. Clustered columnstore; the full JSON is in AuditDataGz (read it through vCopilotAuditRecord). Lineage: WindowId and LoadId for Graph loads, CsvImportId for CSV imports.

**Script** `Finalize-CopilotAudit.sql` · **In** Copilot and license-event databases · **Also** Create-CopilotCsvImport.sql, for a database that starts from CSV · **Columns added by** `Create-CopilotCsvImport.sql`, `Create-CopilotLoadProcedure.sql`

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `RecordId` | varchar(64) | **no** |  |  |
| 2 | `CreatedDateTime` | datetime2(7) | **no** |  |  |
| 3 | `RecordType` | varchar(128) | yes |  |  |
| 4 | `Operation` | varchar(128) | yes |  |  |
| 5 | `OrganizationId` | varchar(64) | yes |  |  |
| 6 | `UserType` | varchar(64) | yes |  |  |
| 7 | `UserId` | nvarchar(512) | yes |  |  |
| 8 | `UserPrincipalName` | nvarchar(512) | yes |  |  |
| 9 | `Service` | varchar(128) | yes |  |  |
| 10 | `ObjectId` | nvarchar(4000) | yes |  |  |
| 11 | `ClientIp` | varchar(128) | yes |  |  |
| 12 | `AdministrativeUnits` | nvarchar(4000) | yes |  |  |
| 13 | `AuditDataGz` | varbinary(max) | yes |  | CAST(DECOMPRESS(AuditDataGz) AS NVARCHAR(MAX)); or use audit.vCopilotAuditRecord |
| 14 | `WindowId` | bigint | **no** |  | lineage back to the control table |
| 15 | `LoadId` | int | yes |  | NULL: the backfill or a CSV import. Otherwise the audit.CopilotLoad that appended the row, and WindowId is then a row of audit.CopilotLoadWindow. |
| 16 | `CsvImportId` | int | yes |  | Set when the row came from a CSV import (audit.CopilotCsvImport); WindowId is then its audit.CopilotCsvFile. |

Indexes and constraints:

- `CCI_CopilotAudit`: clustered columnstore include (RecordId, CreatedDateTime, RecordType, Operation, OrganizationId, UserType, UserId, UserPrincipalName, Service, ObjectId, ClientIp, AdministrativeUnits, AuditDataGz, WindowId, LoadId, CsvImportId)

#### audit.CopilotInteraction

*Table.* One row per Copilot interaction, shredded from the JSON by fnShredCopilotInteraction: day, user, app, agent identity, model, thread, prompt and resource counts, web grounding. Clustered columnstore.

**Script** `Finalize-CopilotAudit.sql` · **In** Copilot and license-event databases · **Also** Create-CopilotCsvImport.sql, for a database that starts from CSV

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `RecordId` | varchar(64) | **no** |  |  |
| 2 | `CreatedDateTime` | datetime2(0) | **no** |  | UTC |
| 3 | `ActivityDate` | date | **no** |  | calendar day in @ReportingTimeZone |
| 4 | `UserPrincipalName` | nvarchar(512) | yes |  |  |
| 5 | `AppHost` | nvarchar(100) | yes |  | Word, Teams, BizChat, Outlook ... |
| 6 | `AppIdentity` | nvarchar(200) | yes |  | e.g. Copilot.MicrosoftCopilot.BizChat, Copilot.Studio.&lt;agent&gt; |
| 7 | `ClientRegion` | nvarchar(20) | yes |  |  |
| 8 | `ModelName` | nvarchar(200) | yes |  |  |
| 9 | `ModelProvider` | nvarchar(200) | yes |  |  |
| 10 | `ThreadId` | nvarchar(400) | yes |  |  |
| 11 | `PromptCount` | int | yes |  |  |
| 12 | `AccessedResourceCount` | int | yes |  |  |
| 13 | `UsedWebSearch` | bit | yes |  |  |

Indexes and constraints:

- `CCI_CopilotInteraction`: clustered columnstore include (RecordId, CreatedDateTime, ActivityDate, UserPrincipalName, AppHost, AppIdentity, ClientRegion, ModelName, ModelProvider, ThreadId, PromptCount, AccessedResourceCount, UsedWebSearch)

#### audit.vCopilotAuditRecord

*View.* CopilotAudit in the Purview portal export's shape (RecordId, CreationDate, RecordType, Operation, UserId, AuditData, ...), with AuditData decompressed. SQL written against the portal CSV can point here. AssociatedAdminUnitsNames is always NULL.

**Script** `Finalize-CopilotAudit.sql` · **In** Copilot and license-event databases · **Also** Create-CopilotCsvImport.sql, for a database that starts from CSV

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `RecordId` | varchar(64) | **no** |  |  |
| 2 | `CreationDate` | datetime2(7) | **no** |  |  |
| 3 | `RecordType` | varchar(128) | yes |  |  |
| 4 | `Operation` | varchar(128) | yes |  |  |
| 5 | `UserId` | nvarchar(512) | yes |  |  |
| 6 | `AuditData` | nvarchar(max) | yes |  |  |
| 7 | `AssociatedAdminUnits` | nvarchar(4000) | yes |  |  |
| 8 | `AssociatedAdminUnitsNames` | nvarchar(max) | yes |  |  |

#### audit.vCopilotDailyUsage

*View.* Interactions, active users, prompts and web-grounded interactions per day per app.

**Script** `Finalize-CopilotAudit.sql` · **In** Copilot and license-event databases · **Also** Create-CopilotCsvImport.sql, for a database that starts from CSV

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `ActivityDate` | date | **no** |  |  |
| 2 | `AppHost` | nvarchar(100) | yes |  |  |
| 3 | `Interactions` | int | yes |  |  |
| 4 | `ActiveUsers` | int | yes |  |  |
| 5 | `Prompts` | int | yes |  |  |
| 6 | `WebGroundedInteractions` | int | yes |  |  |

### Flattened records

One row per record with the Copilot event data shredded into columns, for reports written against the old RawLogFlattened table.

#### audit.RawLogFlattened

*Table.* One row per audit record. The first fifteen columns match dbo.RawLogFlattened, so existing reports can point here; multi-valued parts (accessed resources, sensitivity labels, plugins) are '; '-joined lists there. Since 2.2.0 every other field of the record that carries information has its own typed column, named after its JSON path; the small arrays are JSON text, NULL when empty. Filled by usp_FlattenCopilotAudit, which MERGEs.

**Script** `Create-CopilotFlattenedSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `RecordId` | nvarchar(450) | **no** | **PK** | $.Id - the audit record id, one row per record. |
| 2 | `TimeStamp` | nvarchar(4000) | yes |  | $.CreationTime, kept as text in the source's shape. UtcTimeStamp below is the same instant typed, for anyone who wants to filter. |
| 3 | `User` | nvarchar(4000) | yes |  | $.UserId |
| 4 | `App` | nvarchar(4000) | yes |  | $.CopilotEventData.AppHost - Word, Teams, BizChat, Outlook, Bing ... |
| 5 | `Location` | nvarchar(max) | yes |  | $.ClientRegion - the user's region when they performed the operation |
| 6 | `App context` | nvarchar(4000) | yes |  |  |
| 7 | `Accessed Resources` | nvarchar(max) | yes |  |  |
| 8 | `Accessed Resource Locations` | nvarchar(max) | yes |  |  |
| 9 | `Action` | nvarchar(max) | yes |  | $.CopilotEventData.AccessedResources[ ].Action - Read, Create, ... |
| 10 | `AIPlugin` | nvarchar(max) | yes |  | $.CopilotEventData.AISystemPlugin[ ].Id - BingWebSearch means the interaction was grounded on the public web. |
| 11 | `JailbreakDetected` | nvarchar(5) | yes |  | $.CopilotEventData.Messages[ ].JailbreakDetected - 'true'/'false' kept as text to match the source column. |
| 12 | `PromptInjectionDetected` | nvarchar(5) | yes |  | $.CopilotEventData.AccessedResources[ ].XPIADetected - cross prompt injection attack detected in an accessed resource. |
| 13 | `BlockedResourceUrls` | nvarchar(max) | yes |  | SiteUrl of any accessed resource carrying PolicyDetails or a failed Status - Copilot's access to it was blocked or restricted. |
| 14 | `SensitivityLabelIds` | nvarchar(max) | yes |  | $.CopilotEventData.AccessedResources[ ].SensitivityLabelId |
| 15 | `IsCopilotLic` | bit | yes |  | Whether the user held a Copilot license. Not in the audit record: filled from audit.CopilotLicense when that table exists (see the procedure), NULL otherwise. |
| 16 | `UtcTimeStamp` | datetime2(0) | yes |  | Beyond the source table's columns, cheap to carry and hard to reconstruct later. Drop them if the shape has to match exactly. $.CreationTime, typed |
| 17 | `AppIdentity` | nvarchar(200) | yes |  | $.AppIdentity |
| 18 | `ThreadId` | nvarchar(400) | yes |  | $.CopilotEventData.ThreadId |
| 19 | `AgentId` | nvarchar(400) | yes |  | $.AgentId (2.4.11), else $.CopilotEventData.AgentId |
| 20 | `AgentName` | nvarchar(400) | yes |  | $.AgentName, the agent's friendly name (2.4.11), else $.CopilotEventData.AgentName |
| 21 | `ModelName` | nvarchar(200) | yes |  | ModelTransparencyDetails[0].ModelName |
| 22 | `ModelProvider` | nvarchar(200) | yes |  | ModelTransparencyDetails[0].ModelProviderName |
| 23 | `AccessedResourceCount` | int | yes |  |  |
| 24 | `PromptCount` | int | yes |  |  |
| 25 | `FlattenedUtc` | datetime2(0) | **no** | `sysutcdatetime` |  |
| 26 | `OrganizationId` | uniqueidentifier | yes |  | $.OrganizationId: the tenant. One value in a single-tenant database; kept on every row so a database can hold more than one. |
| 27 | `UserKey` | uniqueidentifier | yes |  | $.UserKey |
| 28 | `ClientIP` | varchar(64) | yes |  | $.ClientIP |
| 29 | `ApplicationName` | nvarchar(200) | yes |  | $.ApplicationName |
| 30 | `PlatformAgentId` | varchar(256) | yes |  | $.PlatformAgentId |
| 31 | `ResultStatus` | varchar(64) | yes |  | $.ResultStatus |
| 32 | `AppAccessContext_APIId` | varchar(64) | yes |  | $.AppAccessContext.APIId |
| 33 | `CopilotEventData_LicenseType` | varchar(32) | yes |  | $.CopilotEventData.LicenseType: the license the record reports (Premium, Starter ...) |
| 34 | `CopilotEventData_ConversationId` | varchar(256) | yes |  | $.CopilotEventData.ConversationId |
| 35 | `CopilotEventData_CorrelationId` | uniqueidentifier | yes |  | $.CopilotEventData.CorrelationId |
| 36 | `CopilotEventData_SensitivityLabelId` | uniqueidentifier | yes |  | $.CopilotEventData.SensitivityLabelId: the interaction's own label |
| 37 | `CopilotEventData_TargetAgentName` | nvarchar(200) | yes |  | $.CopilotEventData.TargetAgentName: an agent this one handed work to |
| 38 | `CopilotEventData_TargetPlatformAgentId` | varchar(256) | yes |  | $.CopilotEventData.TargetPlatformAgentId |
| 39 | `CopilotEventData_DLPEvaluationDeferred` | tinyint | yes |  | $.CopilotEventData.DLPEvaluationDeferred |
| 40 | `CopilotEventData_MemoryUpdated` | bit | yes |  | $.CopilotEventData.MemoryUpdated |
| 41 | `CopilotEventData_MemoryCorrelationId` | varchar(64) | yes |  | $.CopilotEventData.MemoryCorrelationId |
| 42 | `CopilotEventData_FabricCapacityId` | uniqueidentifier | yes |  | $.CopilotEventData.FabricCapacityId |
| 43 | `CopilotEventData_PlatformAgentId` | varchar(256) | yes |  | $.CopilotEventData.PlatformAgentId |
| 44 | `CopilotEventData_TaskId` | uniqueidentifier | yes |  | $.CopilotEventData.TaskId |
| 45 | `ModelVersion` | varchar(32) | yes |  | ModelTransparencyDetails[0].ModelVersion |
| 46 | `MessageCount` | int | yes |  | Messages[*]: prompts and responses |
| 47 | `CopilotEventData_AISystemPlugin` | nvarchar(max) | yes |  | $.CopilotEventData.AISystemPlugin as JSON text; NULL when empty |
| 48 | `CopilotEventData_ModelTransparencyDetails` | nvarchar(max) | yes |  | $.CopilotEventData.ModelTransparencyDetails as JSON text; NULL when empty |
| 49 | `CopilotEventData_ParticipatingAgents` | nvarchar(max) | yes |  | $.CopilotEventData.ParticipatingAgents as JSON text; NULL when empty |
| 50 | `CopilotEventData_SourceThreadIds` | nvarchar(max) | yes |  | $.CopilotEventData.SourceThreadIds as JSON text; NULL when empty |

Indexes and constraints:

- `PK_RawLogFlattened_RecordId`: primary key, clustered (RecordId)
- `IX_RawLogFlattened_UtcTimeStamp`: nonclustered (UtcTimeStamp) include (User, App)

#### audit.RawLogAccessedResource

*Table.* One row per element of CopilotEventData.AccessedResources (2.2.0): what Copilot read, its type, site, label and policy, and whether it was blocked (a failed Status, or a Purview policy outcome other than None). Page-compressed.

**Script** `Create-CopilotFlattenedSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `RecordId` | nvarchar(440) | **no** | **PK** | audit.RawLogFlattened (440, not 450, so the key stays under 900 bytes) |
| 2 | `Ordinal` | smallint | **no** | **PK** | position in the array, from 0 |
| 3 | `Action` | nvarchar(450) | yes |  | Read, Create ... |
| 4 | `Type` | nvarchar(200) | yes |  | File, EmailMessage, TeamsMessage, a URL scheme ... |
| 5 | `Id` | nvarchar(1000) | yes |  |  |
| 6 | `Name` | nvarchar(4000) | yes |  |  |
| 7 | `SiteUrl` | nvarchar(max) | yes |  |  |
| 8 | `Site` | nvarchar(400) | yes |  | SiteUrl cut to https://host/sites/name (or /personal/name, /teams/name) |
| 9 | `ListItemUniqueId` | uniqueidentifier | yes |  |  |
| 10 | `SensitivityLabelId` | uniqueidentifier | yes |  |  |
| 11 | `XPIADetected` | bit | yes |  | cross-prompt injection found in this resource |
| 12 | `Status` | varchar(64) | yes |  |  |
| 13 | `PolicyDetails` | nvarchar(max) | yes |  |  |
| 14 | `PolicyOutcomes` | nvarchar(400) | yes |  | the outcomes other than None, '; '-joined |
| 15 | `IsBlocked` | bit | **no** |  |  |

Indexes and constraints:

- `PK_RawLogAccessedResource`: primary key, clustered (RecordId, Ordinal)

#### audit.RawLogContext

*Table.* One row per element of CopilotEventData.Contexts (2.2.0): where the user was during the interaction.

**Script** `Create-CopilotFlattenedSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `RecordId` | nvarchar(440) | **no** | **PK** |  |
| 2 | `Ordinal` | smallint | **no** | **PK** |  |
| 3 | `Id` | nvarchar(4000) | yes |  |  |
| 4 | `Type` | varchar(64) | yes |  |  |
| 5 | `ContainerId` | varchar(64) | yes |  |  |

Indexes and constraints:

- `PK_RawLogContext`: primary key, clustered (RecordId, Ordinal)

#### audit.ValueCatalog

*Table.* Every distinct value of 23 low-cardinality columns (2.3.0): label ids, tenant, app, agent and platform ids, models, capacities, license types, resource types. How often and when first and last seen; a column stops collecting at 10,000 values. Filled by each flatten batch; names are in dash.ValueName.

**Script** `Create-CopilotFlattenedSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `ColumnId` | smallint | **no** | **PK** |  |
| 2 | `Value` | nvarchar(400) | **no** | **PK** |  |
| 3 | `Records` | bigint | **no** |  |  |
| 4 | `FirstSeenUtc` | datetime2(0) | **no** | `sysutcdatetime` |  |
| 5 | `LastSeenUtc` | datetime2(0) | **no** | `sysutcdatetime` |  |

Indexes and constraints:

- `PK_ValueCatalog`: primary key, clustered (ColumnId, Value)

#### audit.ValueCatalogColumn

*Table.* The cataloged columns (2.3.0): which table and column, a label, how many distinct values, and whether the column passed 10,000 and was capped.

**Script** `Create-CopilotFlattenedSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `ColumnId` | smallint | **no** | **PK** |  |
| 2 | `TableName` | sysname | **no** |  | in the audit schema |
| 3 | `ColumnName` | sysname | **no** |  |  |
| 4 | `Label` | nvarchar(200) | **no** |  | what an administrator sees |
| 5 | `DistinctValues` | int | **no** | `0` |  |
| 6 | `Capped` | bit | **no** | `0` |  |
| 7 | `UpdatedUtc` | datetime2(0) | yes |  |  |

Indexes and constraints:

- `PK_ValueCatalogColumn`: primary key, clustered (ColumnId)
- `UQ_ValueCatalogColumn`: unique, nonclustered (TableName, ColumnName)

#### audit.AgentCatalog

*Table.* Every agent the audit log names (2.4.11), one row per pair of AppIdentity and AgentId as records carry them, with the friendly name ($.AgentName) of the latest record that has one and when the agent was first and last seen. Kept by the flatten for each batch, so an agent's name is known from its whole history; bi.usp_NameAgents names bi.DimAgent from it.

**Script** `Create-CopilotFlattenedSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `AgentCatalogId` | int | **no** | **PK** identity |  |
| 2 | `AppIdentity` | nvarchar(200) | **no** |  | $.AppIdentity; N'' when the record has none |
| 3 | `AgentId` | nvarchar(400) | **no** |  | $.AgentId; N'' when the record has none |
| 4 | `AgentName` | nvarchar(400) | yes |  | $.AgentName on the latest record that carried one |
| 5 | `NameSeenUtc` | datetime2(0) | yes |  | that record's time |
| 6 | `FirstSeenUtc` | datetime2(0) | yes |  | the earliest record of this agent |
| 7 | `LastSeenUtc` | datetime2(0) | yes |  | the latest |

Indexes and constraints:

- `PK_AgentCatalog`: primary key, clustered (AgentCatalogId)
- `UQ_AgentCatalog`: unique, nonclustered (AppIdentity, AgentId)

#### audit.RawLogRecordShape

*Table.* The seventeen fields that carry no information in a 19-million-record production profile (one value where present, or always empty), counted by combination instead of stored per row (2.2.0). The flattening reports if one of them ever takes a second value.

**Script** `Create-CopilotFlattenedSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `ShapeHash` | binary(32) | **no** | **PK** |  |
| 2 | `RecordType` | smallint | yes |  |  |
| 3 | `Operation` | varchar(64) | yes |  |  |
| 4 | `Workload` | varchar(32) | yes |  |  |
| 5 | `UserType` | tinyint | yes |  |  |
| 6 | `Version` | tinyint | yes |  |  |
| 7 | `CopilotLogVersion` | varchar(32) | yes |  |  |
| 8 | `SubjectType` | tinyint | yes |  |  |
| 9 | `AgentVersion` | varchar(32) | yes |  |  |
| 10 | `AgentPlatform` | varchar(64) | yes |  |  |
| 11 | `AgentBlueprintId` | uniqueidentifier | yes |  |  |
| 12 | `AppAccessContext_ClientAppId` | uniqueidentifier | yes |  |  |
| 13 | `CopilotEventData_PlatformAgentType` | varchar(64) | yes |  |  |
| 14 | `CopilotEventData_IsSpawnTask` | bit | yes |  |  |
| 15 | `HasAdminUnits` | bit | **no** |  | AssociatedAdminUnits or its names had a value |
| 16 | `HasMessageIds` | bit | **no** |  | CopilotEventData.MessageIds not empty |
| 17 | `HasGuardrailDetails` | bit | **no** |  |  |
| 18 | `HasTargetTaskIds` | bit | **no** |  |  |
| 19 | `Records` | bigint | **no** |  | flattened with this shape since the last full rebuild; a re-flatten counts again |
| 20 | `FirstSeenUtc` | datetime2(0) | **no** | `sysutcdatetime` |  |
| 21 | `LastSeenUtc` | datetime2(0) | **no** | `sysutcdatetime` |  |

Indexes and constraints:

- `PK_RawLogRecordShape`: primary key, clustered (ShapeHash)

### Licenses and the user directory

Who holds a license (Microsoft Graph) and where they sit in the organization (on-premises AD).

#### audit.CopilotLicense

*Table.* One row per user looked up by the license tool: whether they hold a Copilot license, the SKU, since when, and whether they still exist in Entra. IsCopilotLic NULL means not known, which is not the same as unlicensed.

**Script** `Create-CopilotEnrichmentSchema.sql` · **In** Copilot database · **Also** Create-CopilotFlattenedSchema.sql, if run first

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `UserPrincipalName` | nvarchar(512) | **no** | **PK** |  |
| 2 | `IsCopilotLic` | bit | yes |  | Nullable on purpose: NULL means "not known", which is the honest answer for a user who appears in the audit data but has since left the directory. 0 would claim they were unlicensed. |
| 3 | `UpdatedUtc` | datetime2(0) | **no** | `sysutcdatetime` |  |
| 4 | `SkuPartNumber` | nvarchar(100) | yes |  |  |
| 5 | `LicensedSinceUtc` | datetime2(0) | yes |  |  |
| 6 | `EntraAccountEnabled` | bit | yes |  |  |
| 7 | `EmployeeHireDate` | date | yes |  |  |
| 8 | `UserType` | nvarchar(20) | yes |  |  |
| 9 | `CapabilityStatus` | nvarchar(50) | yes |  |  |
| 10 | `DirectoryStatus` | nvarchar(20) | yes |  |  |
| 11 | `EmployeeLeaveUtc` | datetime2(0) | yes |  | Entra's employeeLeaveDateTime, read with -IncludeLeaveDates (2.4.3) |

Indexes and constraints:

- `PK_CopilotLicense`: primary key, clustered (UserPrincipalName)

#### audit.UserDirectory

*Table.* One row per user in the trusted AD domain, enabled or not: identity, job and location attributes, manager, and the management chain Mgr1 (the top) to Mgr&lt;OrgDepth&gt; (the direct manager). Replaced whole in one transaction by the AD export.

**Script** `Create-CopilotEnrichmentSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `UserPrincipalName` | nvarchar(256) | **no** | **PK** |  |
| 2 | `Mail` | nvarchar(256) | yes |  |  |
| 3 | `FullName` | nvarchar(256) | yes |  | AD attribute: displayName |
| 4 | `EmployeeType` | nvarchar(32) | yes |  | after -EmployeeTypeMap, e.g. EMPLOYEE / CONTRACTOR |
| 5 | `IsManager` | bit | **no** |  | someone reports to them |
| 6 | `AccountEnabled` | bit | **no** |  |  |
| 7 | `JobTitle` | nvarchar(128) | yes |  | AD attribute: title |
| 8 | `Department` | nvarchar(200) | yes |  | department, or the first of -DepartmentAttribute that has a value (2.5.0) |
| 9 | `Division` | nvarchar(256) | yes |  | division: the line of business |
| 10 | `Office` | nvarchar(128) | yes |  | AD attribute: physicalDeliveryOfficeName |
| 11 | `City` | nvarchar(128) | yes |  | AD attribute: l |
| 12 | `Country` | nvarchar(128) | yes |  | co, else the English name of c (2.5.0) |
| 13 | `Company` | nvarchar(64) | yes |  | AD attribute: company |
| 14 | `ManagerUPN` | nvarchar(256) | yes |  |  |
| 15 | `ManagerName` | nvarchar(256) | yes |  |  |
| 16 | `OrgDepth` | tinyint | **no** |  | managers above this user; 0 = the top |
| 17 | `Mgr1` | nvarchar(256) | yes |  |  |
| 18 | `Mgr2` | nvarchar(256) | yes |  |  |
| 19 | `Mgr3` | nvarchar(256) | yes |  |  |
| 20 | `Mgr4` | nvarchar(256) | yes |  |  |
| 21 | `Mgr5` | nvarchar(256) | yes |  |  |
| 22 | `Mgr6` | nvarchar(256) | yes |  |  |
| 23 | `Mgr7` | nvarchar(256) | yes |  |  |
| 24 | `Mgr8` | nvarchar(256) | yes |  |  |
| 25 | `Mgr9` | nvarchar(256) | yes |  |  |
| 26 | `Mgr10` | nvarchar(256) | yes |  |  |
| 27 | `Mgr11` | nvarchar(256) | yes |  |  |
| 28 | `Mgr12` | nvarchar(256) | yes |  |  |
| 29 | `Mgr13` | nvarchar(256) | yes |  |  |
| 30 | `Mgr14` | nvarchar(256) | yes |  |  |
| 31 | `Mgr15` | nvarchar(256) | yes |  |  |
| 32 | `LoadedUtc` | datetime2(0) | **no** | `sysutcdatetime` |  |
| 33 | `Sid` | varchar(184) | yes |  |  |
| 34 | `AccountExpiresUtc` | datetime2(0) | yes |  | AD's accountExpires; NULL when the account never expires (2.4.3) |

Indexes and constraints:

- `PK_UserDirectory`: primary key, clustered (UserPrincipalName)
- `IX_UserDirectory_Mail`: nonclustered (Mail)

#### audit.GroupMember

*Table.* The members of each AD group granted Organization in the dashboard, nested groups included (2.4.0): the group as granted (DOMAIN\\Group) and the member's UPN. Rewritten by each AD export, so bi.OrgSecurity gives them the whole organization in Power BI as the dashboard does at sign-in.

**Script** `Create-CopilotEnrichmentSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `GroupName` | nvarchar(256) | **no** | **PK** | as granted: DOMAIN\\Group |
| 2 | `UserPrincipalName` | nvarchar(256) | **no** | **PK** |  |
| 3 | `LoadedUtc` | datetime2(0) | **no** | `sysutcdatetime` |  |

Indexes and constraints:

- `PK_GroupMember`: primary key, clustered (GroupName, UserPrincipalName)

#### audit.LicenseSku

*Table.* The tenant's subscriptions as of the license tool's last run (2.3.0): per SKU, seats enabled, in the grace period, suspended and assigned, and whether it is a Copilot SKU.

**Script** `Create-CopilotEnrichmentSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `SkuId` | uniqueidentifier | **no** | **PK** |  |
| 2 | `SkuPartNumber` | nvarchar(100) | **no** |  |  |
| 3 | `IsCopilot` | bit | **no** |  |  |
| 4 | `Enabled` | int | **no** |  | prepaidUnits.enabled: seats bought and active |
| 5 | `Warning` | int | **no** |  | prepaidUnits.warning: in the grace period after expiry |
| 6 | `Suspended` | int | **no** |  |  |
| 7 | `LockedOut` | int | **no** |  |  |
| 8 | `Consumed` | int | **no** |  | consumedUnits: seats assigned |
| 9 | `CapabilityStatus` | nvarchar(32) | yes |  |  |
| 10 | `CapturedUtc` | datetime2(0) | **no** | `sysutcdatetime` |  |

Indexes and constraints:

- `PK_LicenseSku`: primary key, clustered (SkuId)

#### audit.UserLicenseSku

*Table.* Every SKU each person the license tool looked up holds (2.4.3), from assignedLicenses; replaced whole by each run. Licensing > Overlapping licenses reads it, with names from audit.LicenseSku.

**Script** `Create-CopilotEnrichmentSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `UserPrincipalName` | nvarchar(512) | **no** | **PK** |  |
| 2 | `SkuId` | uniqueidentifier | **no** | **PK** |  |
| 3 | `CapturedUtc` | datetime2(0) | **no** | `sysutcdatetime` |  |

Indexes and constraints:

- `PK_UserLicenseSku`: primary key, clustered (UserPrincipalName, SkuId)

#### audit.LicenseSkuDay

*Table.* The same per night (2.3.0), for the license pool over time.

**Script** `Create-CopilotEnrichmentSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `SnapshotDate` | date | **no** | **PK** |  |
| 2 | `SkuId` | uniqueidentifier | **no** | **PK** |  |
| 3 | `SkuPartNumber` | nvarchar(100) | **no** |  |  |
| 4 | `IsCopilot` | bit | **no** |  |  |
| 5 | `Enabled` | int | **no** |  |  |
| 6 | `Warning` | int | **no** |  |  |
| 7 | `Suspended` | int | **no** |  |  |
| 8 | `LockedOut` | int | **no** |  |  |
| 9 | `Consumed` | int | **no** |  |  |

Indexes and constraints:

- `PK_LicenseSkuDay`: primary key, clustered (SnapshotDate, SkuId)

#### audit.vCopilotAdoption

*View.* Licensed users against actual use: first and last interaction, interactions, NeverUsed, DaysToFirstUse, with directory attributes.

**Script** `Create-CopilotEnrichmentSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `UserPrincipalName` | nvarchar(512) | **no** |  |  |
| 2 | `IsCopilotLic` | bit | yes |  |  |
| 3 | `SkuPartNumber` | nvarchar(100) | yes |  |  |
| 4 | `LicensedSinceUtc` | datetime2(0) | yes |  |  |
| 5 | `Division` | nvarchar(256) | yes |  |  |
| 6 | `Department` | nvarchar(200) | yes |  |  |
| 7 | `Office` | nvarchar(128) | yes |  |  |
| 8 | `Country` | nvarchar(128) | yes |  |  |
| 9 | `ManagerUPN` | nvarchar(256) | yes |  |  |
| 10 | `EmployeeType` | nvarchar(32) | yes |  |  |
| 11 | `AccountEnabled` | bit | yes |  |  |
| 12 | `Interactions` | bigint | yes |  |  |
| 13 | `FirstInteractionUtc` | datetime2(0) | yes |  |  |
| 14 | `LastInteractionUtc` | datetime2(0) | yes |  |  |
| 15 | `NeverUsed` | int | **no** |  |  |
| 16 | `DaysToFirstUse` | int | yes |  |  |

#### audit.vCopilotUsageByDepartment

*View.* Interactions and active users per department, day and app.

**Script** `Create-CopilotEnrichmentSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `Department` | nvarchar(200) | **no** |  |  |
| 2 | `ActivityDate` | date | **no** |  |  |
| 3 | `AppHost` | nvarchar(100) | yes |  |  |
| 4 | `Interactions` | bigint | yes |  |  |
| 5 | `ActiveUsers` | int | yes |  |  |

### Sessions and the weekly rollup

The SQL port of the session script: sessions inferred from the flattened records, rolled up by Saturday-to-Friday week.

#### audit.SessionAppCategory

*Table.* App name to category (Copilot Chat or In-App Copilot). Seeded once; your edits survive re-running the script. Unmapped apps count as In-App Copilot.

**Script** `Create-CopilotSessionSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `App` | nvarchar(200) | **no** | **PK** |  |
| 2 | `AppCategory` | nvarchar(50) | **no** |  |  |

Indexes and constraints:

- `PK_SessionAppCategory`: primary key, clustered (App)

#### audit.CopilotSession

*Table.* One row per inferred session: a person's prompts with less than 60 minutes between them, within one reporting-time-zone day. Primary app and license flag come from its first prompt.

**Script** `Create-CopilotSessionSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `SessionKey` | bigint | **no** | **PK** identity |  |
| 2 | `AppContext` | nvarchar(4000) | yes |  |  |
| 3 | `UserUPN` | nvarchar(512) | **no** |  |  |
| 4 | `SessionOrdinal_Derived` | int | **no** |  |  |
| 5 | `SessionStart_PromptSubmittedDateTime` | datetime2(0) | **no** |  | reporting time zone |
| 6 | `SessionEnd_PromptSubmittedDateTime` | datetime2(0) | **no** |  |  |
| 7 | `InferredSessionSpanMinutes` | decimal(12,2) | **no** |  |  |
| 8 | `InferredSessionSpanHours` | decimal(12,2) | **no** |  |  |
| 9 | `InferredSessionSpanDays` | decimal(12,6) | **no** |  |  |
| 10 | `SessionTimeoutMinutes` | int | **no** |  |  |
| 11 | `CalendarDaysTouched_InSessionSpan` | int | **no** |  |  |
| 12 | `PromptCount` | int | **no** |  |  |
| 13 | `SessionType` | nvarchar(20) | **no** |  | Single-Turn / Multi-Turn |
| 14 | `PrimaryApp` | nvarchar(200) | yes |  |  |
| 15 | `AppCategory` | nvarchar(50) | **no** |  |  |
| 16 | `IsM365CopilotLicensed` | bit | yes |  |  |
| 17 | `Division` | nvarchar(400) | yes |  |  |
| 18 | `Department` | nvarchar(400) | yes |  |  |
| 19 | `JobTitle` | nvarchar(400) | yes |  |  |
| 20 | `ManagerUPN` | nvarchar(1024) | yes |  |  |
| 21 | `AvgMinutesPerPrompt_InferredSessionSpan` | decimal(12,2) | **no** |  |  |
| 22 | `AvgMinutesPerPromptPerCalendarDay_InferredSpan` | decimal(12,2) | **no** |  |  |
| 23 | `AvgMinutesPerPrompt_NormalizedByCalendarDays` | decimal(12,2) | **no** |  |  |
| 24 | `MetricVersion` | nvarchar(200) | **no** |  |  |

Indexes and constraints:

- `PK_CopilotSession`: primary key, clustered (SessionKey)
- `IX_CopilotSession_Start`: nonclustered (SessionStart_PromptSubmittedDateTime)
- `IX_CopilotSession_User`: nonclustered (UserUPN, SessionStart_PromptSubmittedDateTime) include (AppContext, SessionOrdinal_Derived)

#### audit.CopilotWeeklyRollup

*Table.* One row per week x user x primary app x category x license, by the week a session's last prompt falls in.

**Script** `Create-CopilotSessionSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `WeekEnding` | date | **no** |  | a Friday; the week is Saturday-Friday |
| 2 | `WeekYear` | int | **no** |  |  |
| 3 | `WeekNumber` | int | **no** |  | 1 = the week ending on the year's first Friday |
| 4 | `WeekLabel` | nvarchar(10) | **no** |  | 2026-W38 |
| 5 | `UserUPN` | nvarchar(512) | **no** |  |  |
| 6 | `PrimaryApp` | nvarchar(200) | yes |  |  |
| 7 | `AppCategory` | nvarchar(50) | **no** |  |  |
| 8 | `IsM365CopilotLicensed` | bit | yes |  |  |
| 9 | `WeeklySessionCount` | int | **no** |  |  |
| 10 | `WeeklyPromptCount` | int | **no** |  |  |
| 11 | `WeeklyInferredSessionSpanMinutes` | decimal(14,2) | **no** |  |  |
| 12 | `WeeklyInferredSessionSpanHours` | decimal(14,2) | **no** |  |  |
| 13 | `WeeklyInferredSessionSpanDays` | decimal(14,6) | **no** |  |  |
| 14 | `ActiveDaysWithUsageInWeek` | int | **no** |  |  |
| 15 | `DistinctAppContextsInWeek` | int | **no** |  |  |
| 16 | `FirstPromptSubmittedDateTime_InWeek` | datetime2(0) | **no** |  |  |
| 17 | `LastPromptSubmittedDateTime_InWeek` | datetime2(0) | **no** |  |  |
| 18 | `Division` | nvarchar(400) | yes |  |  |
| 19 | `Department` | nvarchar(400) | yes |  |  |
| 20 | `JobTitle` | nvarchar(400) | yes |  |  |
| 21 | `ManagerUPN` | nvarchar(1024) | yes |  |  |
| 22 | `AvgMinutesPerPrompt_InferredSpan_Weekly` | decimal(12,2) | **no** |  |  |
| 23 | `MetricVersion` | nvarchar(200) | **no** |  |  |

Indexes and constraints:

- `IX_CopilotWeeklyRollup_Week`: nonclustered (WeekEnding, UserUPN)

#### audit.CopilotSessionRun

*Table.* One row per session build: the settings used, rows read and skipped, sessions written, and RebuiltFrom (NULL for a full build, the day an incremental build started from).

**Script** `Create-CopilotSessionSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `RunId` | int | **no** | **PK** identity |  |
| 2 | `RunStartUtc` | datetime2(0) | **no** |  |  |
| 3 | `RunEndUtc` | datetime2(0) | **no** |  |  |
| 4 | `RowsRead` | int | **no** |  |  |
| 5 | `RowsWithOriginalAppContext` | int | **no** |  |  |
| 6 | `RowsRecoveredFromLocation` | int | **no** |  |  |
| 7 | `RowsSkippedMissingContext` | int | **no** |  |  |
| 8 | `RowsSkippedMissingUser` | int | **no** |  |  |
| 9 | `RowsSkippedBadTimestamp` | int | **no** |  |  |
| 10 | `SessionsWritten` | int | **no** |  |  |
| 11 | `WeeklyGroups` | int | **no** |  |  |
| 12 | `SessionTimeoutMinutes` | int | **no** |  |  |
| 13 | `ContextFallback` | nvarchar(20) | **no** |  |  |
| 14 | `ReportingTimeZone` | nvarchar(128) | **no** |  |  |
| 15 | `LicenseEventDatabase` | nvarchar(1000) | yes |  | NULL: license flag is today's, from the enrichment |
| 16 | `MetricVersion` | nvarchar(200) | **no** |  |  |
| 17 | `RebuiltFrom` | datetime2(0) | yes |  |  |

Indexes and constraints:

- `PK_CopilotSessionRun`: primary key, clustered (RunId)

#### audit.vCopilotSessionSummary

*View.* CopilotSession with the old copilot_session_summary.csv column names and text formats (yyyy-MM-dd HH:mm:ss, TRUE/FALSE).

**Script** `Create-CopilotSessionSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `AppContext` | nvarchar(4000) | yes |  |  |
| 2 | `UserUPN` | nvarchar(512) | **no** |  |  |
| 3 | `SessionOrdinal_Derived` | int | **no** |  |  |
| 4 | `SessionStart_PromptSubmittedDateTime` | varchar(19) | yes |  |  |
| 5 | `SessionEnd_PromptSubmittedDateTime` | varchar(19) | yes |  |  |
| 6 | `InferredSessionSpanMinutes` | decimal(12,2) | **no** |  |  |
| 7 | `InferredSessionSpanHours` | decimal(12,2) | **no** |  |  |
| 8 | `InferredSessionSpanDays` | decimal(12,6) | **no** |  |  |
| 9 | `SessionTimeoutMinutes` | int | **no** |  |  |
| 10 | `CalendarDaysTouched_InSessionSpan` | int | **no** |  |  |
| 11 | `PromptCount` | int | **no** |  |  |
| 12 | `SessionType` | nvarchar(20) | **no** |  |  |
| 13 | `PrimaryApp` | nvarchar(200) | yes |  |  |
| 14 | `AppCategory` | nvarchar(50) | **no** |  |  |
| 15 | `IsM365CopilotLicensed` | varchar(5) | **no** |  |  |
| 16 | `Division` | nvarchar(400) | yes |  |  |
| 17 | `Department` | nvarchar(400) | yes |  |  |
| 18 | `JobTitle` | nvarchar(400) | yes |  |  |
| 19 | `ManagerUPN` | nvarchar(1024) | yes |  |  |
| 20 | `AvgMinutesPerPrompt_InferredSessionSpan` | decimal(12,2) | **no** |  |  |
| 21 | `AvgMinutesPerPromptPerCalendarDay_InferredSpan` | decimal(12,2) | **no** |  |  |
| 22 | `AvgMinutesPerPrompt_NormalizedByCalendarDays` | decimal(12,2) | **no** |  |  |
| 23 | `MetricVersion` | nvarchar(200) | **no** |  |  |

#### audit.vCopilotWeeklyRollup

*View.* CopilotWeeklyRollup with the old weekly_rollup.csv column names and formats.

**Script** `Create-CopilotSessionSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `WeekEnding` | varchar(10) | yes |  |  |
| 2 | `WeekYear` | int | **no** |  |  |
| 3 | `WeekNumber` | int | **no** |  |  |
| 4 | `WeekLabel` | nvarchar(10) | **no** |  |  |
| 5 | `UserUPN` | nvarchar(512) | **no** |  |  |
| 6 | `PrimaryApp` | nvarchar(200) | yes |  |  |
| 7 | `AppCategory` | nvarchar(50) | **no** |  |  |
| 8 | `IsM365CopilotLicensed` | varchar(5) | **no** |  |  |
| 9 | `WeeklySessionCount` | int | **no** |  |  |
| 10 | `WeeklyPromptCount` | int | **no** |  |  |
| 11 | `WeeklyInferredSessionSpanMinutes` | decimal(14,2) | **no** |  |  |
| 12 | `WeeklyInferredSessionSpanHours` | decimal(14,2) | **no** |  |  |
| 13 | `WeeklyInferredSessionSpanDays` | decimal(14,6) | **no** |  |  |
| 14 | `ActiveDaysWithUsageInWeek` | int | **no** |  |  |
| 15 | `DistinctAppContextsInWeek` | int | **no** |  |  |
| 16 | `FirstPromptSubmittedDateTime_InWeek` | varchar(19) | yes |  |  |
| 17 | `LastPromptSubmittedDateTime_InWeek` | varchar(19) | yes |  |  |
| 18 | `Division` | nvarchar(400) | yes |  |  |
| 19 | `Department` | nvarchar(400) | yes |  |  |
| 20 | `JobTitle` | nvarchar(400) | yes |  |  |
| 21 | `ManagerUPN` | nvarchar(1024) | yes |  |  |
| 22 | `AvgMinutesPerPrompt_InferredSpan_Weekly` | decimal(12,2) | **no** |  |  |
| 23 | `MetricVersion` | nvarchar(200) | **no** |  |  |

### CSV import

Created by tools/Import-CopilotAuditCsv.ps1 (2.1.0).

#### audit.CopilotCsvImport

*Table.* One row per run of the importer: folder, operations, files and rows read, rejected, duplicates, already loaded, added, and the covered range if given.

**Script** `Create-CopilotCsvImport.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `ImportId` | int | **no** | **PK** identity |  |
| 2 | `FolderPath` | nvarchar(1024) | **no** |  |  |
| 3 | `Operations` | nvarchar(400) | **no** |  | the database's operation set, as the backfill records it |
| 4 | `StartedUtc` | datetime2(0) | **no** | `sysutcdatetime` |  |
| 5 | `MergedUtc` | datetime2(0) | yes |  |  |
| 6 | `FilesLoaded` | int | yes |  |  |
| 7 | `RowsRead` | bigint | yes |  | data rows in the files |
| 8 | `RowsRejected` | bigint | yes |  | see audit.CopilotCsvReject |
| 9 | `RowsOtherOperation` | bigint | yes |  | valid, but not an operation this database holds |
| 10 | `RowsStaged` | bigint | yes |  |  |
| 11 | `RowsDuplicate` | bigint | yes |  | the same RecordId more than once across the files |
| 12 | `RowsAlreadyLoaded` | bigint | yes |  | already in audit.CopilotAudit |
| 13 | `RowsAdded` | bigint | yes |  |  |
| 14 | `EarliestAddedUtc` | datetime2(0) | yes |  |  |
| 15 | `LatestAddedUtc` | datetime2(0) | yes |  |  |
| 16 | `CoveredFromUtc` | datetime2(0) | yes |  | the search range the exports were made for, if given |
| 17 | `CoveredToUtc` | datetime2(0) | yes |  |  |
| 18 | `DerivedRefreshedUtc` | datetime2(0) | yes |  | the derived tables brought up to date with it (2.4.12); empty on a merged import that added rows, a refresh the next import or -Incremental run makes up |

Indexes and constraints:

- `PK_CopilotCsvImport`: primary key, clustered (ImportId)

#### audit.CopilotCsvFile

*Table.* One row per file loaded, with its SHA-256, so a file already imported is skipped next time. Its FileId is the WindowId of the rows it added.

**Script** `Create-CopilotCsvImport.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `FileId` | int | **no** | **PK** identity |  |
| 2 | `ImportId` | int | **no** |  |  |
| 3 | `FileName` | nvarchar(260) | **no** |  |  |
| 4 | `FullPath` | nvarchar(1024) | **no** |  |  |
| 5 | `Sha256` | char(64) | **no** |  |  |
| 6 | `SizeBytes` | bigint | **no** |  |  |
| 7 | `LastWriteUtc` | datetime2(0) | yes |  |  |
| 8 | `RowsRead` | bigint | yes |  |  |
| 9 | `RowsRejected` | bigint | yes |  |  |
| 10 | `RowsOtherOperation` | bigint | yes |  |  |
| 11 | `RowsStaged` | bigint | yes |  |  |
| 12 | `EarliestRecordUtc` | datetime2(0) | yes |  |  |
| 13 | `LatestRecordUtc` | datetime2(0) | yes |  |  |
| 14 | `LoadedUtc` | datetime2(0) | **no** | `sysutcdatetime` |  |

Indexes and constraints:

- `PK_CopilotCsvFile`: primary key, clustered (FileId)
- `IX_CopilotCsvFile_Sha256`: nonclustered (Sha256)

#### audit.CopilotCsvReject

*Table.* Rows that could not be loaded: file, the line the row starts on, the record ID if known, and why.

**Script** `Create-CopilotCsvImport.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `FileId` | int | **no** |  |  |
| 2 | `LineNumber` | bigint | **no** |  | where the row starts in the file |
| 3 | `RecordId` | varchar(64) | yes |  |  |
| 4 | `Reason` | nvarchar(400) | **no** |  |  |

Indexes and constraints:

- `IX_CopilotCsvReject_File`: clustered (FileId, LineNumber)

#### audit.CopilotCsvStage

*Table.* The staged rows of the import being merged, in the shape of CopilotAuditStage with the file in place of the window. A heap, emptied after each merge.

**Script** `Create-CopilotCsvImport.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `FileId` | int | **no** |  |  |
| 2 | `LineNumber` | bigint | **no** |  |  |
| 3 | `RecordId` | varchar(64) | **no** |  |  |
| 4 | `CreatedDateTime` | datetime2(7) | **no** |  |  |
| 5 | `RecordType` | varchar(128) | yes |  |  |
| 6 | `Operation` | varchar(128) | yes |  |  |
| 7 | `OrganizationId` | varchar(64) | yes |  |  |
| 8 | `UserType` | varchar(64) | yes |  |  |
| 9 | `UserId` | nvarchar(512) | yes |  |  |
| 10 | `UserPrincipalName` | nvarchar(512) | yes |  |  |
| 11 | `Service` | varchar(128) | yes |  |  |
| 12 | `ObjectId` | nvarchar(4000) | yes |  |  |
| 13 | `ClientIp` | varchar(128) | yes |  |  |
| 14 | `AdministrativeUnits` | nvarchar(4000) | yes |  |  |
| 15 | `AuditDataGz` | varbinary(max) | yes |  |  |

### License events (license-event database)

In the separate database the license-operations backfill fills. It also holds the loading, state and final-record tables above; these are added on top.

#### audit.LicenseEvent

*Table.* One row per license-related directory audit record (Change user license, Update user, Add user, Delete user): who changed whom, when, and whether the detail came from chunks.

**Script** `Create-LicenseEventSchema.sql` · **In** License-event database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `RecordId` | varchar(64) | **no** | **PK** |  |
| 2 | `CreatedDateTime` | datetime2(0) | **no** |  | UTC |
| 3 | `ActivityDate` | date | **no** |  |  |
| 4 | `Operation` | nvarchar(200) | yes |  | 'Change user license', 'Update user', ... |
| 5 | `ResultStatus` | nvarchar(50) | yes |  |  |
| 6 | `ActorUpn` | nvarchar(512) | yes |  | who made the change |
| 7 | `TargetUpn` | nvarchar(512) | yes |  | whose license changed |
| 8 | `TargetObjectId` | nvarchar(200) | yes |  |  |
| 9 | `CorrelationId` | nvarchar(200) | yes |  | Records raised for the same underlying change share this, which is what pairs a detail-free 'Change user license' with the 'Update user' that carries the license lists. |
| 10 | `ShreddedUtc` | datetime2(0) | **no** | `sysutcdatetime` |  |
| 11 | `ChunkGroupId` | nvarchar(100) | yes |  |  |
| 12 | `ChunkSeq` | int | yes |  |  |

Indexes and constraints:

- `PK_LicenseEvent`: primary key, clustered (RecordId)
- `IX_LicenseEvent_Chunk`: nonclustered (ChunkGroupId, ChunkSeq)
- `IX_LicenseEvent_Correlation`: nonclustered (CorrelationId)
- `IX_LicenseEvent_Target`: nonclustered (TargetUpn, CreatedDateTime) include (Operation, CorrelationId)
- `IX_LicenseEvent_Shredded`: nonclustered (ShreddedUtc)

#### audit.LicenseEventChunk

*Table.* Fragments of changes too large for one audit record, kept until every fragment of a group has arrived and can be reassembled.

**Script** `Create-LicenseEventSchema.sql` · **In** License-event database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `ChunkGroupId` | nvarchar(100) | **no** | **PK** |  |
| 2 | `Seq` | int | **no** | **PK** |  |
| 3 | `RecordId` | varchar(64) | **no** |  |  |
| 4 | `Body` | nvarchar(max) | yes |  |  |

Indexes and constraints:

- `PK_LicenseEventChunk`: primary key, clustered (ChunkGroupId, Seq)

#### audit.LicenseEventProperty

*Table.* One row per modified property of an event: Name, OldValue, NewValue. Whatever Microsoft records is queryable, whatever the property names.

**Script** `Create-LicenseEventSchema.sql` · **In** License-event database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `RecordId` | varchar(64) | **no** | **PK** |  |
| 2 | `Ordinal` | int | **no** | **PK** |  |
| 3 | `Name` | nvarchar(400) | yes |  |  |
| 4 | `OldValue` | nvarchar(max) | yes |  |  |
| 5 | `NewValue` | nvarchar(max) | yes |  |  |
| 6 | `Source` | varchar(10) | **no** | `'record'` |  |

Indexes and constraints:

- `PK_LicenseEventProperty`: primary key, clustered (RecordId, Ordinal)

#### audit.vLicenseChange

*View.* One row per license change, deduplicated: a chunked change counts once, and the Update user raised alongside every license change is dropped in favor of the Change user license.

**Script** `Create-LicenseEventSchema.sql` · **In** License-event database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `RecordId` | varchar(64) | **no** |  |  |
| 2 | `CreatedDateTime` | datetime2(0) | **no** |  |  |
| 3 | `ActivityDate` | date | **no** |  |  |
| 4 | `Operation` | nvarchar(200) | yes |  |  |
| 5 | `ActorUpn` | nvarchar(512) | yes |  |  |
| 6 | `TargetUpn` | nvarchar(512) | yes |  |  |
| 7 | `CorrelationId` | nvarchar(200) | yes |  |  |
| 8 | `PropertyName` | nvarchar(400) | yes |  |  |
| 9 | `OldValue` | nvarchar(max) | yes |  |  |
| 10 | `NewValue` | nvarchar(max) | yes |  |  |
| 11 | `FromPairedRecord` | int | **no** |  |  |
| 12 | `FromChunks` | int | **no** |  |  |

#### audit.LicenseSkuChange

*Table.* The SKUs each license change added and removed, kept (2.5.4): one row per record, SKU and direction. Worked out by usp_RefreshLicenseSkuChange at the end of every shred, for the events shredded since it last ran and the records raised with them. Sessions and bi read every row each night, through vLicenseSkuChange.

**Script** `Create-LicenseEventSchema.sql` · **In** License-event database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `RecordId` | varchar(64) | **no** | **PK** |  |
| 2 | `Sku` | nvarchar(200) | **no** | **PK** |  |
| 3 | `Change` | nvarchar(7) | **no** | **PK** | 'Added' \| 'Removed' |
| 4 | `CreatedDateTime` | datetime2(0) | **no** |  | UTC |
| 5 | `ActivityDate` | date | **no** |  |  |
| 6 | `ActorUpn` | nvarchar(512) | yes |  |  |
| 7 | `TargetUpn` | nvarchar(512) | yes |  | the record's own, or the one a record raised with it carries |
| 8 | `FromChunks` | int | **no** |  | 1: read from a reassembled chunk group |

Indexes and constraints:

- `PK_LicenseSkuChange`: primary key, clustered (RecordId, Sku, Change)

#### audit.LicenseSkuChangeRun

*Table.* One row per run of usp_RefreshLicenseSkuChange (2.5.4): when, the events worked out and the changes kept for them. SinceUtc is empty when every event was worked out. No row: the changes have never been worked out in this database.

**Script** `Create-LicenseEventSchema.sql` · **In** License-event database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `RunId` | int | **no** | **PK** identity |  |
| 2 | `StartedUtc` | datetime2(0) | **no** |  |  |
| 3 | `EndedUtc` | datetime2(0) | **no** |  |  |
| 4 | `SinceUtc` | datetime2(0) | yes |  | events shredded from then on, and the records raised with them; NULL: every event |
| 5 | `Events` | int | **no** |  | events worked out again |
| 6 | `Changes` | int | **no** |  | SKU changes kept for them |

Indexes and constraints:

- `PK_LicenseSkuChangeRun`: primary key, clustered (RunId)

#### audit.vLicenseSkuChangeFromRecords

*View.* The rule the kept rows follow, read from the events and their properties each time: SKUs added and removed per change, from the old and new AssignedLicense lists. It was vLicenseSkuChange before 2.5.4, and is what the tests check the table against. Slow on a large history: 11 minutes a read over 4,000,000 events in the lab.

**Script** `Create-LicenseEventSchema.sql` · **In** License-event database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `RecordId` | varchar(64) | **no** |  |  |
| 2 | `CreatedDateTime` | datetime2(0) | **no** |  |  |
| 3 | `ActivityDate` | date | **no** |  |  |
| 4 | `ActorUpn` | nvarchar(512) | yes |  |  |
| 5 | `TargetUpn` | nvarchar(512) | yes |  |  |
| 6 | `FromChunks` | int | **no** |  |  |
| 7 | `Sku` | nvarchar(max) | yes |  |  |
| 8 | `Change` | nvarchar(7) | **no** |  |  |

#### audit.vLicenseSkuChange

*View.* SKUs added and removed per change: "when was this user given Copilot, and when was it taken away". Sessions and bi read it for license at the time of use. From 2.5.4 it reads audit.LicenseSkuChange.

**Script** `Create-LicenseEventSchema.sql` · **In** License-event database · **Also** Create-LicenseEventProcedure.sql, which points it at the table once the changes of events shredded before 2.5.4 are worked out

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `RecordId` | varchar(64) | **no** |  |  |
| 2 | `CreatedDateTime` | datetime2(0) | **no** |  |  |
| 3 | `ActivityDate` | date | **no** |  |  |
| 4 | `ActorUpn` | nvarchar(512) | yes |  |  |
| 5 | `TargetUpn` | nvarchar(512) | yes |  |  |
| 6 | `FromChunks` | int | **no** |  |  |
| 7 | `Sku` | nvarchar(200) | **no** |  |  |
| 8 | `Change` | nvarchar(7) | **no** |  |  |

#### audit.AgentEvent

*Table.* One row per Copilot Studio authoring record (2.3.0): the operation (BotCreate, BotUpdateOperation-BotPublish, BotUpdateOperation-BotShare, BotDelete, BotComponent...), the action it means (Created, Published, Shared, Updated, Component, Deleted), the maker, the agent and the component, and the update details. Shredded by usp_ShredAgentEvents after each license-event load, which asks for these operations too.

**Script** `Create-AgentEventProcedure.sql` · **In** License-event database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `RecordId` | varchar(64) | **no** | **PK** |  |
| 2 | `CreatedUtc` | datetime2(0) | **no** |  |  |
| 3 | `Operation` | nvarchar(100) | **no** |  |  |
| 4 | `Action` | varchar(16) | **no** |  | Created \| Published \| Shared \| Updated \| Deleted \| Component |
| 5 | `ActorUpn` | nvarchar(512) | yes |  |  |
| 6 | `BotId` | nvarchar(100) | yes |  |  |
| 7 | `BotSchemaName` | nvarchar(200) | yes |  |  |
| 8 | `ComponentName` | nvarchar(200) | yes |  | BotComponentSchemaName |
| 9 | `ComponentType` | nvarchar(100) | yes |  |  |
| 10 | `ResultStatus` | nvarchar(50) | yes |  |  |
| 11 | `UpdateDetails` | nvarchar(4000) | yes |  | BotUpdateDetails / BotComponentUpdateDetails, as JSON |
| 12 | `ShreddedUtc` | datetime2(0) | **no** | `sysutcdatetime` |  |

Indexes and constraints:

- `PK_AgentEvent`: primary key, clustered (RecordId)
- `IX_AgentEvent_Bot`: nonclustered (BotSchemaName, CreatedUtc)
- `IX_AgentEvent_Created`: nonclustered (CreatedUtc)

#### audit.vLicenseLifecycle

*View.* Provisioning and revocation per user. "Revoked" is inferred from a license disappearing or the account being deleted: a reading of the events, not a field the service provides.

**Script** `Create-LicenseEventSchema.sql` · **In** License-event database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `TargetUpn` | nvarchar(512) | yes |  |  |
| 2 | `AccountCreatedUtc` | datetime2(0) | yes |  |  |
| 3 | `AccountDeletedUtc` | datetime2(0) | yes |  |  |
| 4 | `FirstLicenseChangeUtc` | datetime2(0) | yes |  |  |
| 5 | `LastLicenseChangeUtc` | datetime2(0) | yes |  |  |
| 6 | `LicenseChangeCount` | int | yes |  |  |
| 7 | `EventCount` | int | yes |  |  |

### bi: dimensions

The star schema Power BI imports (through the bi views below). Integer surrogate keys, stable across refreshes and rebuilds.

#### bi.DimDate

*Table.* One row per date, with the Saturday-to-Friday week the rollup uses. Key: DateKey (yyyymmdd).

**Script** `Create-CopilotBiSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `DateKey` | int | **no** | **PK** | yyyymmdd |
| 2 | `Date` | date | **no** |  |  |
| 3 | `Year` | smallint | **no** |  |  |
| 4 | `Quarter` | tinyint | **no** |  |  |
| 5 | `MonthNumber` | tinyint | **no** |  |  |
| 6 | `MonthName` | nvarchar(10) | **no** |  |  |
| 7 | `YearMonth` | char(7) | **no** |  | 2026-09 |
| 8 | `DayOfWeekNumber` | tinyint | **no** |  | 1 = Monday ... 7 = Sunday |
| 9 | `DayOfWeekName` | nvarchar(10) | **no** |  |  |
| 10 | `IsWeekday` | bit | **no** |  |  |
| 11 | `WeekEnding` | date | **no** |  | The weekly rollup's week: Saturday to Friday, labeled by the Friday; week 1 of a year ends on its first Friday. |
| 12 | `WeekYear` | smallint | **no** |  |  |
| 13 | `WeekNumber` | tinyint | **no** |  |  |
| 14 | `WeekLabel` | char(8) | **no** |  | 2026-W38 |

Indexes and constraints:

- `PK_DimDate`: primary key, clustered (DateKey)

#### bi.DimUser

*Table.* One row per person seen in the audit data, licenses or license history: directory attributes from AD, the org chain as OrgLevel1..15, and today's license. Restated from the sources on every refresh.

**Script** `Create-CopilotBiSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `UserKey` | int | **no** | **PK** identity |  |
| 2 | `UserPrincipalName` | nvarchar(512) | **no** |  |  |
| 3 | `DisplayName` | nvarchar(400) | yes |  |  |
| 4 | `Division` | nvarchar(200) | yes |  |  |
| 5 | `Department` | nvarchar(200) | yes |  |  |
| 6 | `JobTitle` | nvarchar(200) | yes |  |  |
| 7 | `OfficeLocation` | nvarchar(200) | yes |  |  |
| 8 | `City` | nvarchar(200) | yes |  |  |
| 9 | `Country` | nvarchar(200) | yes |  |  |
| 10 | `CompanyName` | nvarchar(200) | yes |  |  |
| 11 | `EmployeeType` | nvarchar(32) | yes |  | EMPLOYEE \| CONTRACTOR ... |
| 12 | `IsManager` | bit | yes |  |  |
| 13 | `AccountEnabled` | bit | yes |  |  |
| 14 | `ManagerUpn` | nvarchar(512) | yes |  |  |
| 15 | `ManagerName` | nvarchar(256) | yes |  |  |
| 16 | `OrgDepth` | tinyint | yes |  | The management chain from AD: OrgLevel1 is the top (the CEO), OrgLevel&lt;OrgDepth&gt; the direct manager. A ragged hierarchy, top down. |
| 17 | `OrgLevel1` | nvarchar(256) | yes |  |  |
| 18 | `OrgLevel2` | nvarchar(256) | yes |  |  |
| 19 | `OrgLevel3` | nvarchar(256) | yes |  |  |
| 20 | `OrgLevel4` | nvarchar(256) | yes |  |  |
| 21 | `OrgLevel5` | nvarchar(256) | yes |  |  |
| 22 | `OrgLevel6` | nvarchar(256) | yes |  |  |
| 23 | `OrgLevel7` | nvarchar(256) | yes |  |  |
| 24 | `OrgLevel8` | nvarchar(256) | yes |  |  |
| 25 | `OrgLevel9` | nvarchar(256) | yes |  |  |
| 26 | `OrgLevel10` | nvarchar(256) | yes |  |  |
| 27 | `OrgLevel11` | nvarchar(256) | yes |  |  |
| 28 | `OrgLevel12` | nvarchar(256) | yes |  |  |
| 29 | `OrgLevel13` | nvarchar(256) | yes |  |  |
| 30 | `OrgLevel14` | nvarchar(256) | yes |  |  |
| 31 | `OrgLevel15` | nvarchar(256) | yes |  |  |
| 32 | `DirectoryStatus` | nvarchar(20) | **no** | `N'Not looked up'` | Found (in AD) \| NotFound (left Entra) \| Not looked up (neither source) |
| 33 | `CopilotLicenseToday` | nvarchar(12) | **no** | `N'Unknown'` | Today's license, from the enrichment. Licensed \| Not licensed \| Unknown. License at the time of use is on the facts, not here. |
| 34 | `CopilotSku` | nvarchar(100) | yes |  |  |
| 35 | `LicensedSinceUtc` | datetime2(0) | yes |  |  |
| 36 | `FirstSeenUtc` | datetime2(0) | **no** | `sysutcdatetime` |  |

Indexes and constraints:

- `PK_DimUser`: primary key, clustered (UserKey)
- `UQ_DimUser_Upn`: unique, nonclustered (UserPrincipalName)

#### bi.DimApp

*Table.* One row per app, with its category from SessionAppCategory.

**Script** `Create-CopilotBiSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `AppKey` | int | **no** | **PK** identity |  |
| 2 | `App` | nvarchar(200) | **no** |  | Word, Teams, Office (Copilot Chat) ... |
| 3 | `AppCategory` | nvarchar(50) | **no** | `N'In-App Copilot'` |  |
| 4 | `FirstSeenUtc` | datetime2(0) | **no** | `sysutcdatetime` |  |

Indexes and constraints:

- `PK_DimApp`: primary key, clustered (AppKey)
- `UQ_DimApp_App`: unique, nonclustered (App)

#### bi.DimAgent

*Table.* One row per agent: the one the record's AppIdentity names (a Copilot Studio or Agent Builder agent, one of Microsoft's), else its AgentId (2.4.11; bi.fnAgentIdentity is the rule). AgentName is a label on the key: the friendly name the audit log gave the agent last, else one read from its id, so a renamed agent keeps its key and its history. (no agent) is Copilot itself.

**Script** `Create-CopilotBiSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `AgentKey` | int | **no** | **PK** identity |  |
| 2 | `AgentId` | nvarchar(400) | **no** |  | the AppIdentity that names the agent, else AgentId |
| 3 | `AgentName` | nvarchar(400) | yes |  | the latest friendly name the audit log gave it, else one read from the id |
| 5 | `FirstSeenUtc` | datetime2(0) | **no** | `sysutcdatetime` |  |
| 6 | `NamedInAudit` | bit | **no** | `0` | 1 when AgentName is the audit log's own name for the agent, 0 when it was read from the id (2.4.11) |
| 7 | `AgentSource` | nvarchar(30) | yes |  | Copilot Studio \| Microsoft \| Agent ID \| (no agent) |

Indexes and constraints:

- `PK_DimAgent`: primary key, clustered (AgentKey)
- `UQ_DimAgent_Id`: unique, nonclustered (AgentId)

#### bi.DimModel

*Table.* One row per model provider and name.

**Script** `Create-CopilotBiSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `ModelKey` | int | **no** | **PK** identity |  |
| 2 | `ModelProvider` | nvarchar(200) | **no** |  |  |
| 3 | `ModelName` | nvarchar(200) | **no** |  |  |
| 4 | `FirstSeenUtc` | datetime2(0) | **no** | `sysutcdatetime` |  |

Indexes and constraints:

- `PK_DimModel`: primary key, clustered (ModelKey)
- `UQ_DimModel`: unique, nonclustered (ModelProvider, ModelName)

#### bi.DimSku

*Table.* One row per license SKU seen in the license history, flagged when it is a Copilot SKU.

**Script** `Create-CopilotBiSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `SkuKey` | int | **no** | **PK** identity |  |
| 2 | `Sku` | nvarchar(200) | **no** |  | SkuPartNumber, e.g. Microsoft_365_Copilot |
| 3 | `IsCopilot` | bit | **no** |  |  |
| 4 | `FirstSeenUtc` | datetime2(0) | **no** | `sysutcdatetime` |  |

Indexes and constraints:

- `PK_DimSku`: primary key, clustered (SkuKey)
- `UQ_DimSku_Sku`: unique, nonclustered (Sku)

#### bi.DimLicenseStatus

*Table.* Three rows: 0 Unknown, 1 Licensed, 2 Not licensed.

**Script** `Create-CopilotBiSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `LicenseStatusKey` | tinyint | **no** | **PK** |  |
| 2 | `LicenseStatus` | nvarchar(20) | **no** |  |  |

Indexes and constraints:

- `PK_DimLicenseStatus`: primary key, clustered (LicenseStatusKey)

#### bi.DimLicenseType

*Table.* The license type the audit record itself reports (CopilotEventData.LicenseType), 2.2.0. Key 1 is (not reported).

**Script** `Create-CopilotBiSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `LicenseTypeKey` | int | **no** | **PK** identity |  |
| 2 | `LicenseType` | nvarchar(32) | **no** |  |  |

Indexes and constraints:

- `PK_DimLicenseType`: primary key, clustered (LicenseTypeKey)
- `UQ_DimLicenseType`: unique, nonclustered (LicenseType)

#### bi.DimSensitivityLabel

*Table.* One row per sensitivity label id seen on an interaction or a resource it read (2.2.0). Key 1 is (none). Names are in dash.SensitivityLabel.

**Script** `Create-CopilotBiSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `SensitivityLabelKey` | int | **no** | **PK** identity |  |
| 2 | `LabelId` | uniqueidentifier | yes |  | NULL: the '(none)' row |
| 3 | `FirstSeenUtc` | datetime2(0) | **no** | `sysutcdatetime` |  |

Indexes and constraints:

- `PK_DimSensitivityLabel`: primary key, clustered (SensitivityLabelKey)
- `UQ_DimSensitivityLabel`: unique, nonclustered (LabelId)

#### bi.DimFileType

*Table.* What a resource Copilot read was (2.4.6): a file's extension (docx, xlsx, pptx, csv ...) from AccessedResources[].Name, and for anything else its Type (EmailMessage, TeamsMessage, StreamVideo ...).

**Script** `Create-CopilotBiSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `FileTypeKey` | int | **no** | **PK** identity | 1 is (not reported) |
| 2 | `FileType` | nvarchar(64) | **no** |  |  |

Indexes and constraints:

- `PK_DimFileType`: primary key, clustered (FileTypeKey)
- `UQ_DimFileType`: unique, nonclustered (FileType)

#### bi.DimResourceType

*Table.* Accessed-resource types: File, EmailMessage, TeamsMessage, WebSearchQuery ... (2.2.0).

**Script** `Create-CopilotBiSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `ResourceTypeKey` | int | **no** | **PK** identity |  |
| 2 | `ResourceType` | nvarchar(200) | **no** |  |  |

Indexes and constraints:

- `PK_DimResourceType`: primary key, clustered (ResourceTypeKey)
- `UQ_DimResourceType`: unique, nonclustered (ResourceType)

#### bi.DimResourceAction

*Table.* Accessed-resource actions: Read, Create ... (2.2.0).

**Script** `Create-CopilotBiSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `ResourceActionKey` | int | **no** | **PK** identity |  |
| 2 | `ResourceAction` | nvarchar(450) | **no** |  | as wide as audit.RawLogAccessedResource.[Action] |

Indexes and constraints:

- `PK_DimResourceAction`: primary key, clustered (ResourceActionKey)
- `UQ_DimResourceAction`: unique, nonclustered (ResourceAction)

#### bi.DimSite

*Table.* The site a resource lives on: https://host/sites/name (or /teams/, /personal/), otherwise the host; IsPersonal marks OneDrive (2.2.0).

**Script** `Create-CopilotBiSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `SiteKey` | int | **no** | **PK** identity |  |
| 2 | `Site` | nvarchar(400) | **no** |  |  |
| 3 | `Host` | nvarchar(400) | **no** |  |  |
| 4 | `IsPersonal` | bit | **no** |  | a OneDrive (/personal/) site: it names its owner |
| 5 | `FirstSeenUtc` | datetime2(0) | **no** | `sysutcdatetime` |  |

Indexes and constraints:

- `PK_DimSite`: primary key, clustered (SiteKey)
- `UQ_DimSite`: unique, nonclustered (Site)

### bi: facts and refresh log

Narrow facts with integer keys. FactInteraction is a clustered columnstore and the only one refreshed incrementally in Power BI.

#### bi.FactInteraction

*Table.* One row per Copilot interaction: date, hour, user, app, agent, model, license status at the time of use, counts and safety flags. RecordId and CreatedUtc are kept for lineage and left out of the view the model imports.

**Script** `Create-CopilotBiSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `RecordId` | nvarchar(450) | **no** |  | lineage; not imported |
| 2 | `CreatedUtc` | datetime2(0) | **no** |  | lineage and the refresh window; not imported |
| 3 | `ActivityHour` | datetime2(0) | **no** |  | Local time in the reporting time zone, truncated to the hour: the column for the model's incremental-refresh RangeStart/RangeEnd filter. An hour, not a second, so it has ~8,760 values a year rather than millions, and costs almost nothing in the model. |
| 4 | `DateKey` | int | **no** |  |  |
| 5 | `HourOfDay` | tinyint | **no** |  |  |
| 6 | `UserKey` | int | **no** |  |  |
| 7 | `AppKey` | int | **no** |  |  |
| 8 | `AgentKey` | int | **no** |  |  |
| 9 | `ModelKey` | int | **no** |  |  |
| 10 | `LicenseStatusKey` | tinyint | **no** |  |  |
| 11 | `PromptCount` | int | **no** |  |  |
| 12 | `AccessedResourceCount` | int | **no** |  |  |
| 13 | `UsedWebSearch` | bit | **no** |  |  |
| 14 | `JailbreakDetected` | bit | **no** |  |  |
| 15 | `PromptInjectionDetected` | bit | **no** |  |  |
| 16 | `HasBlockedResource` | bit | **no** |  |  |
| 17 | `HasSensitivityLabel` | bit | **no** |  |  |
| 18 | `PremiumUse` | bit | **no** | `0` |  |
| 19 | `UseCaseKey` | int | **no** | `0` |  |
| 20 | `LicenseTypeKey` | int | **no** | `1` | bi.DimLicenseType: the license the record reports (2.2.0) |
| 21 | `SensitivityLabelKey` | int | **no** | `1` | bi.DimSensitivityLabel: the interaction's own label (2.2.0) |
| 22 | `MessageCount` | int | **no** | `0` | prompts and responses (2.2.0) |
| 23 | `MemoryUpdated` | bit | **no** | `0` | 2.2.0 |
| 24 | `DlpEvaluationDeferred` | bit | **no** | `0` | 2.2.0 |
| 25 | `CalledAnotherAgent` | bit | **no** | `0` | TargetAgentName set (2.2.0) |
| 26 | `TargetAgentName` | nvarchar(400) | yes |  |  |
| 27 | `UseKind` | tinyint | yes |  | what the record says about licensed use (2.4.8): 0 web Copilot Chat or a Copilot Studio agent, 1 inside an app, Copilot Chat in work mode, a Microsoft agent, or work content read, 2 no identity and no work content, 3 no identity, work content read; NULL before 2.4.8 |

Indexes and constraints:

- `CCI_FactInteraction`: clustered columnstore include (RecordId, CreatedUtc, ActivityHour, DateKey, HourOfDay, UserKey, AppKey, AgentKey, ModelKey, LicenseStatusKey, PromptCount, AccessedResourceCount, UsedWebSearch, JailbreakDetected, PromptInjectionDetected, HasBlockedResource, HasSensitivityLabel, PremiumUse, UseCaseKey, LicenseTypeKey, SensitivityLabelKey, MessageCount, MemoryUpdated, DlpEvaluationDeferred, CalledAnotherAgent, TargetAgentName, UseKind)

#### bi.FactSession

*Table.* One row per inferred session, following the session builds.

**Script** `Create-CopilotBiSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `SessionKey` | bigint | **no** | **PK** |  |
| 2 | `SessionStart` | datetime2(0) | **no** |  | reporting time zone |
| 3 | `SessionEnd` | datetime2(0) | **no** |  |  |
| 4 | `DateKey` | int | **no** |  |  |
| 5 | `UserKey` | int | **no** |  |  |
| 6 | `AppKey` | int | **no** |  | the first prompt's app |
| 7 | `LicenseStatusKey` | tinyint | **no** |  |  |
| 8 | `PromptCount` | int | **no** |  |  |
| 9 | `SpanMinutes` | decimal(12,2) | **no** |  |  |
| 10 | `IsMultiTurn` | bit | **no** |  |  |

Indexes and constraints:

- `PK_FactSession`: primary key, clustered (SessionKey)

#### bi.FactLicenseChange

*Table.* One row per Copilot SKU added (+1) or removed (-1) from a person.

**Script** `Create-CopilotBiSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `RecordId` | varchar(64) | **no** | **PK** | lineage; not imported |
| 2 | `ChangedUtc` | datetime2(0) | **no** |  |  |
| 3 | `DateKey` | int | **no** |  |  |
| 4 | `UserKey` | int | **no** |  | whose license changed |
| 5 | `SkuKey` | int | **no** | **PK** |  |
| 6 | `Change` | smallint | **no** | **PK** | +1 added, -1 removed |
| 7 | `ChangedBy` | nvarchar(512) | yes |  | the admin who made it |

Indexes and constraints:

- `PK_FactLicenseChange`: primary key, clustered (RecordId, SkuKey, Change)

#### bi.FactLicensedDay

*Table.* One row per person per day on which they held a Copilot license at the end of the day.

**Script** `Create-CopilotBiSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `DateKey` | int | **no** | **PK** |  |
| 2 | `UserKey` | int | **no** | **PK** |  |

Indexes and constraints:

- `PK_FactLicensedDay`: primary key, clustered (DateKey, UserKey)

#### bi.FactAccessedResource

*Table.* One row per resource an interaction read (2.2.0), from audit.RawLogAccessedResource: type, action, site, label, blocked and prompt-injection flags. Clustered columnstore, refreshed with the interactions.

**Script** `Create-CopilotBiSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `RecordId` | nvarchar(450) | **no** |  | lineage; not imported |
| 2 | `Ordinal` | smallint | **no** |  | lineage; not imported |
| 3 | `CreatedUtc` | datetime2(0) | **no** |  | lineage and the refresh window; not imported |
| 4 | `ActivityHour` | datetime2(0) | **no** |  |  |
| 5 | `DateKey` | int | **no** |  |  |
| 6 | `UserKey` | int | **no** |  |  |
| 7 | `AppKey` | int | **no** |  |  |
| 8 | `ResourceTypeKey` | int | **no** |  |  |
| 9 | `ResourceActionKey` | int | **no** |  |  |
| 10 | `SiteKey` | int | **no** |  |  |
| 11 | `SensitivityLabelKey` | int | **no** |  |  |
| 12 | `IsBlocked` | bit | **no** |  |  |
| 13 | `XPIADetected` | bit | **no** |  |  |
| 14 | `FileTypeKey` | int | **no** | `1` | the file type (2.4.6); 1 for rows loaded before it, until bi is refreshed in full |

Indexes and constraints:

- `CCI_FactAccessedResource`: clustered columnstore include (RecordId, Ordinal, CreatedUtc, ActivityHour, DateKey, UserKey, AppKey, ResourceTypeKey, ResourceActionKey, SiteKey, SensitivityLabelKey, IsBlocked, XPIADetected, FileTypeKey)

#### bi.FactAgentEvent

*Table.* One row per Copilot Studio authoring event (2.3.0), copied from audit.AgentEvent in every license-event database by each refresh, with the day in the reporting time zone and the maker's user key.

**Script** `Create-CopilotBiSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `RecordId` | varchar(64) | **no** | **PK** |  |
| 2 | `EventUtc` | datetime2(0) | **no** |  |  |
| 3 | `DateKey` | int | **no** |  |  |
| 4 | `Action` | varchar(16) | **no** |  |  |
| 5 | `Operation` | nvarchar(100) | **no** |  |  |
| 6 | `ActorUpn` | nvarchar(512) | yes |  |  |
| 7 | `ActorKey` | int | yes |  | bi.DimUser, where the maker is in it |
| 8 | `BotId` | nvarchar(100) | yes |  |  |
| 9 | `BotSchemaName` | nvarchar(200) | yes |  |  |
| 10 | `ComponentName` | nvarchar(200) | yes |  |  |
| 11 | `ComponentType` | nvarchar(100) | yes |  |  |

Indexes and constraints:

- `PK_FactAgentEvent`: primary key, clustered (RecordId)
- `IX_FactAgentEvent_Date`: nonclustered (DateKey)

#### bi.RebuildInProgress

*Table.* A row while a full rebuild of bi.FactInteraction runs (2.4.9): bi.usp_RefreshBi puts it there before it empties the fact and takes it out when it has finished. While a row is here every refresh is a full one, so a rebuild stopped part way is finished by the next run.

**Script** `Create-CopilotBiSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `StartedUtc` | datetime2(0) | **no** |  |  |

#### bi.RefreshRun

*Table.* One row per bi.usp_RefreshBi run: window (SinceUtc NULL is a full rebuild), time zone, license databases, rows written, the session build it followed, and from 2.4.0 the first day with use of an app whose category it changed (RecategorizedFromDateKey), from which the dashboard rolls up again.

**Script** `Create-CopilotBiSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `RunId` | int | **no** | **PK** identity |  |
| 2 | `StartedUtc` | datetime2(0) | **no** |  |  |
| 3 | `EndedUtc` | datetime2(0) | **no** |  |  |
| 4 | `SinceUtc` | datetime2(0) | yes |  | NULL = a full rebuild |
| 5 | `ReportingTimeZone` | sysname | **no** |  |  |
| 6 | `LicenseEventDatabase` | nvarchar(1000) | yes |  |  |
| 7 | `InteractionRows` | bigint | **no** |  | written by this run |
| 8 | `InteractionsSkipped` | bigint | **no** |  | no timestamp |
| 9 | `SessionRows` | int | **no** |  | written by this run |
| 10 | `LicenseChangeRows` | int | **no** |  |  |
| 11 | `LicensedDayRows` | int | **no** |  |  |
| 12 | `SessionRunId` | int | yes |  | the latest audit.CopilotSessionRun it reflects |
| 13 | `AccessedResourceRows` | bigint | yes |  |  |
| 14 | `RecategorizedFromDateKey` | int | yes |  |  |

Indexes and constraints:

- `PK_BiRefreshRun`: primary key, clustered (RunId)

### bi: views Power BI imports

One view per table above, leaving out the lineage columns. The model imports these, not the tables.

#### bi.Interactions

*View.* bi.FactInteraction for the model.

**Script** `Create-CopilotBiSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `ActivityHour` | datetime2(0) | **no** |  |  |
| 2 | `DateKey` | int | **no** |  |  |
| 3 | `HourOfDay` | tinyint | **no** |  |  |
| 4 | `UserKey` | int | **no** |  |  |
| 5 | `AppKey` | int | **no** |  |  |
| 6 | `AgentKey` | int | **no** |  |  |
| 7 | `ModelKey` | int | **no** |  |  |
| 8 | `LicenseStatusKey` | tinyint | **no** |  |  |
| 9 | `LicenseTypeKey` | int | **no** |  |  |
| 10 | `SensitivityLabelKey` | int | **no** |  |  |
| 11 | `PromptCount` | int | **no** |  |  |
| 12 | `MessageCount` | int | **no** |  |  |
| 13 | `AccessedResourceCount` | int | **no** |  |  |
| 14 | `UsedWebSearch` | bit | **no** |  |  |
| 15 | `JailbreakDetected` | bit | **no** |  |  |
| 16 | `PromptInjectionDetected` | bit | **no** |  |  |
| 17 | `HasBlockedResource` | bit | **no** |  |  |
| 18 | `HasSensitivityLabel` | bit | **no** |  |  |
| 19 | `MemoryUpdated` | bit | **no** |  |  |
| 20 | `DlpEvaluationDeferred` | bit | **no** |  |  |
| 21 | `CalledAnotherAgent` | bit | **no** |  |  |
| 22 | `PremiumUse` | bit | **no** |  |  |
| 23 | `UseCaseKey` | int | **no** |  |  |

#### bi.Sessions

*View.* bi.FactSession for the model.

**Script** `Create-CopilotBiSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `SessionKey` | bigint | **no** |  |  |
| 2 | `SessionStart` | datetime2(0) | **no** |  |  |
| 3 | `SessionEnd` | datetime2(0) | **no** |  |  |
| 4 | `DateKey` | int | **no** |  |  |
| 5 | `UserKey` | int | **no** |  |  |
| 6 | `AppKey` | int | **no** |  |  |
| 7 | `LicenseStatusKey` | tinyint | **no** |  |  |
| 8 | `PromptCount` | int | **no** |  |  |
| 9 | `SpanMinutes` | decimal(12,2) | **no** |  |  |
| 10 | `IsMultiTurn` | bit | **no** |  |  |

#### bi.LicenseChanges

*View.* bi.FactLicenseChange for the model.

**Script** `Create-CopilotBiSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `ChangedUtc` | datetime2(0) | **no** |  |  |
| 2 | `DateKey` | int | **no** |  |  |
| 3 | `UserKey` | int | **no** |  |  |
| 4 | `SkuKey` | int | **no** |  |  |
| 5 | `Change` | smallint | **no** |  |  |
| 6 | `ChangedBy` | nvarchar(512) | yes |  |  |

#### bi.LicensedDays

*View.* bi.FactLicensedDay for the model.

**Script** `Create-CopilotBiSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `DateKey` | int | **no** |  |  |
| 2 | `UserKey` | int | **no** |  |  |

#### bi.Calendar

*View.* bi.DimDate for the model; mark it as the date table.

**Script** `Create-CopilotBiSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `DateKey` | int | **no** |  |  |
| 2 | `Date` | date | **no** |  |  |
| 3 | `Year` | smallint | **no** |  |  |
| 4 | `Quarter` | tinyint | **no** |  |  |
| 5 | `MonthNumber` | tinyint | **no** |  |  |
| 6 | `MonthName` | nvarchar(10) | **no** |  |  |
| 7 | `YearMonth` | char(7) | **no** |  |  |
| 8 | `DayOfWeekNumber` | tinyint | **no** |  |  |
| 9 | `DayOfWeekName` | nvarchar(10) | **no** |  |  |
| 10 | `IsWeekday` | bit | **no** |  |  |
| 11 | `WeekEnding` | date | **no** |  |  |
| 12 | `WeekYear` | smallint | **no** |  |  |
| 13 | `WeekNumber` | tinyint | **no** |  |  |
| 14 | `WeekLabel` | char(8) | **no** |  |  |

#### bi.Users

*View.* bi.DimUser for the model.

**Script** `Create-CopilotBiSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `UserKey` | int | **no** | identity |  |
| 2 | `UserPrincipalName` | nvarchar(512) | yes |  |  |
| 3 | `DisplayName` | nvarchar(400) | yes |  |  |
| 4 | `ManagerUpn` | nvarchar(512) | yes |  |  |
| 5 | `ManagerName` | nvarchar(256) | yes |  |  |
| 6 | `Division` | nvarchar(200) | yes |  |  |
| 7 | `Department` | nvarchar(200) | yes |  |  |
| 8 | `JobTitle` | nvarchar(200) | yes |  |  |
| 9 | `OfficeLocation` | nvarchar(200) | yes |  |  |
| 10 | `City` | nvarchar(200) | yes |  |  |
| 11 | `Country` | nvarchar(200) | yes |  |  |
| 12 | `CompanyName` | nvarchar(200) | yes |  |  |
| 13 | `EmployeeType` | nvarchar(32) | yes |  |  |
| 14 | `IsManager` | bit | yes |  |  |
| 15 | `AccountEnabled` | bit | yes |  |  |
| 16 | `OrgDepth` | tinyint | yes |  |  |
| 17 | `OrgLevel1` | nvarchar(256) | yes |  |  |
| 18 | `OrgLevel2` | nvarchar(256) | yes |  |  |
| 19 | `OrgLevel3` | nvarchar(256) | yes |  |  |
| 20 | `OrgLevel4` | nvarchar(256) | yes |  |  |
| 21 | `OrgLevel5` | nvarchar(256) | yes |  |  |
| 22 | `OrgLevel6` | nvarchar(256) | yes |  |  |
| 23 | `OrgLevel7` | nvarchar(256) | yes |  |  |
| 24 | `OrgLevel8` | nvarchar(256) | yes |  |  |
| 25 | `OrgLevel9` | nvarchar(256) | yes |  |  |
| 26 | `OrgLevel10` | nvarchar(256) | yes |  |  |
| 27 | `OrgLevel11` | nvarchar(256) | yes |  |  |
| 28 | `OrgLevel12` | nvarchar(256) | yes |  |  |
| 29 | `OrgLevel13` | nvarchar(256) | yes |  |  |
| 30 | `OrgLevel14` | nvarchar(256) | yes |  |  |
| 31 | `OrgLevel15` | nvarchar(256) | yes |  |  |
| 32 | `DirectoryStatus` | nvarchar(20) | **no** |  |  |
| 33 | `CopilotLicenseToday` | nvarchar(12) | **no** |  |  |
| 34 | `CopilotSku` | nvarchar(100) | yes |  |  |
| 35 | `LicensedSinceUtc` | datetime2(0) | yes |  |  |

#### bi.Apps

*View.* bi.DimApp for the model.

**Script** `Create-CopilotBiSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `AppKey` | int | **no** | identity |  |
| 2 | `App` | nvarchar(200) | **no** |  |  |
| 3 | `AppCategory` | nvarchar(50) | **no** |  |  |

#### bi.Agents

*View.* bi.DimAgent for the model.

**Script** `Create-CopilotBiSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `AgentKey` | int | **no** | identity |  |
| 2 | `AgentId` | nvarchar(400) | **no** |  |  |
| 3 | `AgentName` | nvarchar(400) | yes |  |  |
| 4 | `AgentSource` | nvarchar(30) | yes |  |  |
| 5 | `NamedInAudit` | bit | **no** |  |  |

#### bi.Models

*View.* bi.DimModel for the model.

**Script** `Create-CopilotBiSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `ModelKey` | int | **no** | identity |  |
| 2 | `ModelProvider` | nvarchar(200) | **no** |  |  |
| 3 | `ModelName` | nvarchar(200) | **no** |  |  |

#### bi.Skus

*View.* bi.DimSku for the model.

**Script** `Create-CopilotBiSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `SkuKey` | int | **no** | identity |  |
| 2 | `Sku` | nvarchar(200) | **no** |  |  |
| 3 | `IsCopilot` | bit | **no** |  |  |

#### bi.LicenseStatuses

*View.* bi.DimLicenseStatus for the model.

**Script** `Create-CopilotBiSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `LicenseStatusKey` | tinyint | **no** |  |  |
| 2 | `LicenseStatus` | nvarchar(20) | **no** |  |  |

#### bi.AccessedResources

*View.* bi.FactAccessedResource for the model.

**Script** `Create-CopilotBiSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `ActivityHour` | datetime2(0) | **no** |  |  |
| 2 | `DateKey` | int | **no** |  |  |
| 3 | `UserKey` | int | **no** |  |  |
| 4 | `AppKey` | int | **no** |  |  |
| 5 | `ResourceTypeKey` | int | **no** |  |  |
| 6 | `ResourceActionKey` | int | **no** |  |  |
| 7 | `SiteKey` | int | **no** |  |  |
| 8 | `SensitivityLabelKey` | int | **no** |  |  |
| 9 | `IsBlocked` | bit | **no** |  |  |
| 10 | `XPIADetected` | bit | **no** |  |  |
| 11 | `FileTypeKey` | int | **no** |  |  |

#### bi.LicenseTypes

*View.* bi.DimLicenseType for the model.

**Script** `Create-CopilotBiSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `LicenseTypeKey` | int | **no** | identity |  |
| 2 | `LicenseType` | nvarchar(32) | **no** |  |  |

#### bi.SensitivityLabels

*View.* bi.DimSensitivityLabel for the model, with the id as lower-case text.

**Script** `Create-CopilotBiSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `SensitivityLabelKey` | int | **no** | identity |  |
| 2 | `LabelId` | nvarchar(36) | yes |  |  |

#### bi.ResourceTypes

*View.* bi.DimResourceType for the model.

**Script** `Create-CopilotBiSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `ResourceTypeKey` | int | **no** | identity |  |
| 2 | `ResourceType` | nvarchar(200) | **no** |  |  |

#### bi.FileTypes

*View.* bi.DimFileType for the model (2.4.6).

**Script** `Create-CopilotBiSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `FileTypeKey` | int | **no** | identity |  |
| 2 | `FileType` | nvarchar(64) | **no** |  |  |

#### bi.ResourceActions

*View.* bi.DimResourceAction for the model.

**Script** `Create-CopilotBiSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `ResourceActionKey` | int | **no** | identity |  |
| 2 | `ResourceAction` | nvarchar(450) | **no** |  |  |

#### bi.Sites

*View.* bi.DimSite for the model; in privacy mode a OneDrive site is named OneDrive (personal).

**Script** `Create-CopilotBiSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `SiteKey` | int | **no** | identity |  |
| 2 | `Site` | nvarchar(400) | **no** |  |  |
| 3 | `Host` | nvarchar(400) | **no** |  |  |
| 4 | `IsPersonal` | bit | **no** |  |  |

#### bi.AgentEvents

*View.* bi.FactAgentEvent for the model (2.3.0).

**Script** `Create-CopilotBiSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `RecordId` | varchar(64) | **no** |  |  |
| 2 | `EventUtc` | datetime2(0) | **no** |  |  |
| 3 | `DateKey` | int | **no** |  |  |
| 4 | `Action` | varchar(16) | **no** |  |  |
| 5 | `Operation` | nvarchar(100) | **no** |  |  |
| 6 | `ActorKey` | int | yes |  |  |
| 7 | `BotId` | nvarchar(100) | yes |  |  |
| 8 | `BotSchemaName` | nvarchar(200) | yes |  |  |
| 9 | `ComponentName` | nvarchar(200) | yes |  |  |
| 10 | `ComponentType` | nvarchar(100) | yes |  |  |

#### bi.OrgSecurity

*View.* Who may see whom (2.3.0): each viewer's sign-in with the user key of everyone at or under them, and everyone for the top of the organization, the people reporting to them and people granted Organization. The Power BI model's Organization role filters Users by it, and shows each viewer only their own rows. Empty in privacy mode.

**Script** `Create-CopilotOperationsSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `ViewerUpn` | nvarchar(256) | **no** |  |  |
| 2 | `UserKey` | int | **no** |  |  |
| 3 | `WholeOrganization` | bit | yes |  | 2.4.12: true when the viewer sees the whole organization. The model's Organization role shows LicensePool, ValueNames and DepartmentWeek to them alone. |

#### bi.ValueNames

*View.* Each cataloged value with the name an administrator gave it (2.3.0), from audit.ValueCatalog and dash.ValueName.

**Script** `Create-CopilotOperationsSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `TableName` | sysname | **no** |  |  |
| 2 | `ColumnName` | sysname | **no** |  |  |
| 3 | `ColumnLabel` | nvarchar(200) | **no** |  |  |
| 4 | `Value` | nvarchar(400) | **no** |  |  |
| 5 | `Name` | nvarchar(200) | yes |  |  |
| 6 | `Records` | bigint | **no** |  |  |
| 7 | `FirstSeenUtc` | datetime2(0) | **no** |  |  |
| 8 | `LastSeenUtc` | datetime2(0) | **no** |  |  |

#### bi.LicensePool

*View.* Seats bought, assigned and available per SKU per day (2.3.0), from audit.LicenseSkuDay.

**Script** `Create-CopilotOperationsSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `SnapshotDate` | date | **no** |  |  |
| 2 | `SkuPartNumber` | nvarchar(100) | **no** |  |  |
| 3 | `IsCopilot` | bit | **no** |  |  |
| 4 | `Bought` | int | yes |  |  |
| 5 | `Assigned` | int | **no** |  |  |
| 6 | `Available` | int | yes |  |  |
| 7 | `InGracePeriod` | int | **no** |  |  |
| 8 | `Suspended` | int | **no** |  |  |

#### bi.DepartmentWeek

*View.* Active people, interactions and sessions by department and week (2.3.0), for a model that must not show individuals. From 2.4.12 every department has a row for every week of the data, used or not; departments with fewer enabled people than the minimum group size are combined as (smaller departments), and a week with fewer active people than the minimum is blank.

**Script** `Create-CopilotOperationsSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `WeekEnding` | date | **no** |  |  |
| 2 | `Department` | nvarchar(200) | **no** |  |  |
| 3 | `ActivePeople` | int | yes |  |  |
| 4 | `Interactions` | bigint | yes |  |  |
| 5 | `Sessions` | bigint | yes |  |  |
| 6 | `Hidden` | bit | yes |  |  |

#### bi.UseCases

*View.* Every combination of app, web search and agent (UseCaseKey = AppKey x 4 + web x 2 + agent) with the use case the rules in dash.UseCaseRule give it (2.3.0). bi.Interactions.UseCaseKey points here.

**Script** `Create-CopilotOperationsSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `UseCaseKey` | int | yes |  |  |
| 2 | `App` | nvarchar(200) | **no** |  |  |
| 3 | `WebGrounded` | bit | yes |  |  |
| 4 | `WithAgent` | bit | yes |  |  |
| 5 | `UseCase` | nvarchar(100) | **no** |  |  |

#### bi.JobFamilies

*View.* Each person's job family from the rules in dash.JobFamilyRule (2.3.0); (no family) when none matches.

**Script** `Create-CopilotOperationsSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `UserKey` | int | **no** |  |  |
| 2 | `JobFamily` | nvarchar(100) | **no** |  |  |

#### bi.EventMarkers

*View.* The events marked on the dashboard's trends (2.3.0), with their day key.

**Script** `Create-CopilotOperationsSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `Day` | date | **no** |  |  |
| 2 | `DateKey` | int | yes |  |  |
| 3 | `Label` | nvarchar(100) | **no** |  |  |
| 4 | `Kind` | varchar(16) | **no** |  |  |

### dash: the Copilot Insights dashboard

What the dashboard (dashboard/CopilotInsights, see DASHBOARD.md) keeps besides the bi schema it reads: its settings, roles, reference names and audit log, which the admin module edits, and the org chart and daily rollups dash.usp_RefreshDashboard rebuilds after each bi refresh.

#### dash.Setting

*Table.* Settings the admin module edits: OrgTopUpn (the top of the organization), MinGroupSize, DefaultDays, InactiveDays. Seeded; edits survive re-running the script.

**Script** `Create-CopilotDashboardSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `Name` | varchar(64) | **no** | **PK** |  |
| 2 | `Value` | nvarchar(400) | yes |  |  |
| 3 | `Description` | nvarchar(400) | yes |  |  |
| 4 | `UpdatedBy` | nvarchar(256) | yes |  |  |
| 5 | `UpdatedUtc` | datetime2(0) | **no** | `sysutcdatetime` |  |

Indexes and constraints:

- `PK_DashSetting`: primary key, clustered (Name)

#### dash.RoleGrant

*Table.* Roles granted to people (UPN or DOMAIN\\user) or AD groups (DOMAIN\\Group). Admin runs the admin module; Organization sees the whole organization; the persona roles unlock named detail.

**Script** `Create-CopilotDashboardSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `GrantId` | int | **no** | **PK** identity |  |
| 2 | `Principal` | nvarchar(256) | **no** |  | a UPN, or DOMAIN\\Group |
| 3 | `PrincipalType` | varchar(8) | **no** |  |  |
| 4 | `Role` | varchar(32) | **no** |  |  |
| 5 | `Note` | nvarchar(400) | yes |  |  |
| 6 | `GrantedBy` | nvarchar(256) | **no** |  |  |
| 7 | `GrantedUtc` | datetime2(0) | **no** | `sysutcdatetime` |  |

Indexes and constraints:

- `PK_DashRoleGrant`: primary key, clustered (GrantId)
- `UQ_DashRoleGrant`: unique, nonclustered (Principal, Role)
- `CK_DashRoleGrant_Type`: check `([PrincipalType]='Group' OR [PrincipalType]='User')`
- `CK_DashRoleGrant_Role`: check `([Role]='Enablement' OR [Role]='Agents' OR [Role]='Compliance' OR [Role]='Security' OR [Role]='Licensing' OR [Role]='Adoption' OR [Role]='Organization' OR [Role]='Admin')`

#### dash.AgentAlias

*Table.* Friendly names and owners for agents, which the audit log names by schema name. Edited in Admin &gt; Reference data.

**Script** `Create-CopilotDashboardSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `AgentKey` | int | **no** | **PK** | bi.DimAgent, whose keys are stable |
| 2 | `FriendlyName` | nvarchar(200) | **no** |  |  |
| 3 | `Owner` | nvarchar(256) | yes |  |  |
| 4 | `Notes` | nvarchar(400) | yes |  |  |
| 5 | `UpdatedBy` | nvarchar(256) | **no** |  |  |
| 6 | `UpdatedUtc` | datetime2(0) | **no** | `sysutcdatetime` |  |

Indexes and constraints:

- `PK_DashAgentAlias`: primary key, clustered (AgentKey)

#### dash.SensitivityLabel

*Table.* Names and order for sensitivity labels, which the audit log records by id only. Edited in Admin &gt; Reference data.

**Script** `Create-CopilotDashboardSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `LabelId` | nvarchar(64) | **no** | **PK** | the label GUID |
| 2 | `Name` | nvarchar(200) | **no** |  |  |
| 3 | `Rank` | tinyint | yes |  | ordering, least to most sensitive |
| 4 | `UpdatedBy` | nvarchar(256) | **no** |  |  |
| 5 | `UpdatedUtc` | datetime2(0) | **no** | `sysutcdatetime` |  |

Indexes and constraints:

- `PK_DashSensitivityLabel`: primary key, clustered (LabelId)

#### dash.AuditLog

*Table.* Every page view, refusal, export and admin change: who, what page, what scope, what filters or change, and from where.

**Script** `Create-CopilotDashboardSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `LogId` | bigint | **no** | **PK** identity |  |
| 2 | `AtUtc` | datetime2(0) | **no** | `sysutcdatetime` |  |
| 3 | `Viewer` | nvarchar(256) | **no** |  | the resolved UPN, or the Windows name if none |
| 4 | `Kind` | varchar(12) | **no** |  | View \| Denied \| Export \| Admin |
| 5 | `Page` | nvarchar(200) | yes |  |  |
| 6 | `Scope` | nvarchar(400) | yes |  | Organization, or the subtree root shown |
| 7 | `Detail` | nvarchar(2000) | yes |  | filters, or what an admin change did |
| 8 | `ClientIp` | varchar(64) | yes |  |  |

Indexes and constraints:

- `PK_DashAuditLog`: primary key, clustered (LogId)
- `IX_DashAuditLog_At`: nonclustered (AtUtc)
- `IX_DashAuditLog_Viewer`: nonclustered (Viewer, AtUtc)

#### dash.Person

*Table.* One row per person in the user directory (and each manager missing from it), with their level below the top and how many people are under them. Rebuilt whole by each refresh.

**Script** `Create-CopilotDashboardSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `Upn` | nvarchar(256) | **no** | **PK** |  |
| 2 | `DisplayName` | nvarchar(256) | yes |  |  |
| 3 | `ManagerUpn` | nvarchar(256) | yes |  |  |
| 4 | `Department` | nvarchar(200) | yes |  |  |
| 5 | `AccountEnabled` | bit | yes |  |  |
| 6 | `Level` | tinyint | yes |  | 1 = OrgTopUpn, 2 = reports to them, ...; NULL when not under the top (or no top set) |
| 7 | `DirectReports` | int | **no** |  |  |
| 8 | `PeopleBelow` | int | **no** |  | everyone under them, at any depth |
| 9 | `Sid` | varchar(184) | yes |  |  |

Indexes and constraints:

- `PK_DashPerson`: primary key, clustered (Upn)
- `IX_DashPerson_Manager`: nonclustered (ManagerUpn)
- `IX_DashPerson_Sid`: nonclustered (Sid)

#### dash.OrgClosure

*Table.* Every (manager, person under them) pair at any depth, and each person with themselves at depth 0: the security model's scope. Rebuilt whole by each refresh.

**Script** `Create-CopilotDashboardSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `AncestorUpn` | nvarchar(256) | **no** | **PK** |  |
| 2 | `DescendantUpn` | nvarchar(256) | **no** | **PK** |  |
| 3 | `Depth` | tinyint | **no** |  | 0 = the person themselves |

Indexes and constraints:

- `PK_DashOrgClosure`: primary key, nonclustered (AncestorUpn, DescendantUpn)

#### dash.UserDay

*Table.* One row per person per day with any use or session: the daily rollup behind every page, so the 7- to 365-day periods are exact.

**Script** `Create-CopilotDashboardSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `DateKey` | int | **no** | **PK** | bi.DimDate, in the reporting time zone |
| 2 | `UserKey` | int | **no** | **PK** | bi.DimUser |
| 3 | `Interactions` | int | **no** |  |  |
| 4 | `Prompts` | int | **no** |  |  |
| 5 | `AppsUsed` | tinyint | **no** |  |  |
| 6 | `AgentInteractions` | int | **no** |  | interactions with an agent, not Copilot itself |
| 7 | `WebGrounded` | int | **no** |  |  |
| 8 | `ChatInteractions` | int | **no** |  | in Copilot Chat apps (bi.DimApp.AppCategory) |
| 9 | `LicensedUse` | int | **no** |  | interactions made while licensed |
| 10 | `Jailbreak` | int | **no** |  |  |
| 11 | `PromptInjection` | int | **no** |  |  |
| 12 | `BlockedResource` | int | **no** |  |  |
| 13 | `LabeledContent` | int | **no** |  | interactions that touched sensitivity-labeled content |
| 14 | `Sessions` | int | **no** |  | sessions that started that day |
| 15 | `MultiTurnSessions` | int | **no** |  |  |
| 16 | `PremiumUse` | int | **no** | `0` | licensed interactions that needed the license (2.3.0) |

Indexes and constraints:

- `PK_DashUserDay`: primary key, clustered (DateKey, UserKey)
- `IX_DashUserDay_User`: nonclustered (UserKey, DateKey)
- `NCCI_DashUserDay`: nonclustered columnstore include (DateKey, UserKey, Interactions, Prompts, AppsUsed, AgentInteractions, WebGrounded, ChatInteractions, LicensedUse, Jailbreak, PromptInjection, BlockedResource, LabeledContent, Sessions, MultiTurnSessions, PremiumUse)

#### dash.FlaggedInteraction

*Table.* The interactions with a security signal (2.4.7), a few in every thousand, each with the record id its triage is kept under: Security &gt; Flagged interactions lists them from here. Kept by dash.usp_RefreshDashboard from bi.FactInteraction.

**Script** `Create-CopilotDashboardSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `DateKey` | int | **no** |  |  |
| 2 | `UserKey` | int | **no** |  |  |
| 3 | `RecordId` | nvarchar(450) | **no** |  | the audit record, and dash.FlagCase's key |
| 4 | `ActivityHour` | datetime2(0) | **no** |  |  |
| 5 | `AppKey` | int | **no** |  |  |
| 6 | `AgentKey` | int | **no** |  |  |
| 7 | `JailbreakDetected` | bit | **no** |  |  |
| 8 | `PromptInjectionDetected` | bit | **no** |  |  |
| 9 | `HasBlockedResource` | bit | **no** |  |  |

Indexes and constraints:

- `CX_DashFlaggedInteraction`: clustered (DateKey, UserKey)

#### dash.ResourceDay

*Table.* Labels on content Copilot read, the sites of labeled content, and the sites it was blocked from, per person per day, from bi.FactAccessedResource.

**Script** `Create-CopilotDashboardSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `DateKey` | int | **no** | **PK** |  |
| 2 | `UserKey` | int | **no** | **PK** |  |
| 3 | `Kind` | varchar(8) | **no** | **PK** |  |
| 4 | `Item` | nvarchar(400) | **no** | **PK** |  |
| 5 | `Interactions` | int | **no** |  |  |

Indexes and constraints:

- `PK_DashResourceDay`: primary key, clustered (DateKey, Kind, UserKey, Item)
- `CK_DashResourceDay_Kind`: check `([Kind]='Blocked' OR [Kind]='Site' OR [Kind]='Label')`
- `NCCI_DashResourceDay`: nonclustered columnstore include (DateKey, UserKey, Kind, Item, Interactions)

#### dash.AgentUse

*Table.* When each agent was first and last used, from the facts. Rebuilt whole by each refresh.

**Script** `Create-CopilotDashboardSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `AgentKey` | int | **no** | **PK** |  |
| 2 | `FirstDateKey` | int | **no** |  |  |
| 3 | `LastDateKey` | int | **no** |  |  |
| 4 | `Interactions` | bigint | **no** |  |  |

Indexes and constraints:

- `PK_DashAgentUse`: primary key, clustered (AgentKey)

#### dash.RefreshRun

*Table.* One row per dashboard refresh: when, from which day, how many people, org pairs and person-days it wrote, and from 2.4.0 the latest bi refresh it reflects (BiRunId).

**Script** `Create-CopilotDashboardSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `RunId` | int | **no** | **PK** identity |  |
| 2 | `StartedUtc` | datetime2(0) | **no** |  |  |
| 3 | `EndedUtc` | datetime2(0) | **no** |  |  |
| 4 | `SinceUtc` | datetime2(0) | yes |  | NULL: every day rebuilt |
| 5 | `FromDateKey` | int | yes |  |  |
| 6 | `People` | int | **no** |  |  |
| 7 | `ClosureRows` | int | **no** |  |  |
| 8 | `UserDayRows` | int | **no** |  |  |
| 9 | `BiRunId` | int | yes |  |  |

Indexes and constraints:

- `PK_DashRefreshRun`: primary key, clustered (RunId)

#### dash.AgentInventory

*Table.* The agent workbook's agents (2.5.6): the current agents, one row each, as tools\Import-AgentWorkbook.ps1 loads them each night from the workbook's AgentInventory sheet, matched to the agent the audit log knows. Emptied and filled again on each load.

**Script** `Create-CopilotDashboardSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `InventoryAgentId` | nvarchar(100) | **no** | **PK** | the workbook's AgentId |
| 2 | `AgentKey` | int | yes |  | bi.DimAgent: matched on the schema name the audit identity ends in, then the bot, title or Entra agent id it holds; NULL when the audit log has not seen the agent |
| 3 | `AgentName` | nvarchar(400) | yes |  | AgentName |
| 4 | `OwnerUpn` | nvarchar(256) | yes |  | OwnerIdUPN |
| 5 | `Environment` | nvarchar(400) | yes |  | EnvironmentDisplayName |
| 6 | `SchemaName` | nvarchar(400) | yes |  |  |
| 7 | `CreatedAt` | datetime2(0) | yes |  |  |
| 8 | `LastPublishedAt` | datetime2(0) | yes |  |  |
| 9 | `Channels` | nvarchar(400) | yes |  |  |
| 10 | `ViewerUsers` | int | yes |  |  |
| 11 | `ViewerGroups` | int | yes |  |  |
| 12 | `EditorUsers` | int | yes |  |  |
| 13 | `EditorGroups` | int | yes |  |  |
| 14 | `IsShared` | bit | yes |  | computed: ViewerUsers or ViewerGroups above 0 |
| 15 | `InProduction` | bit | **no** | `0` | listed on the AgentsInProd sheet |
| 16 | `LoadedUtc` | datetime2(0) | **no** | `sysutcdatetime` |  |
| 17 | `EnvironmentSku` | nvarchar(100) | yes |  | EnvironmentSku (2.5.8): Developer, Production ... |
| 18 | `Authentication` | nvarchar(200) | yes |  | Authentication (2.5.8) |
| 19 | `IsQuarantined` | bit | yes |  | IsQuarantined (2.5.8) |
| 20 | `IsWebSearchEnabled` | bit | yes |  | IsWebSearchEnabled (2.5.8) |
| 21 | `IsAutonomous` | bit | **no** | `0` | Triggers or Flows set (2.5.8): no interaction does not make it unused |

Indexes and constraints:

- `PK_DashAgentInventory`: primary key, clustered (InventoryAgentId)
- `IX_DashAgentInventory_AgentKey`: nonclustered (AgentKey)

#### dash.AgentChildLink

*Table.* Connected agents (2.5.6): which parent agent calls which child, from the workbook's ParentChildAgent sheet. Emptied and filled again on each load.

**Script** `Create-CopilotDashboardSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `LinkId` | int | **no** | **PK** identity |  |
| 2 | `ChildAgentName` | nvarchar(400) | **no** |  | ChildAgentName, else ComponentName |
| 3 | `ParentInventoryAgentId` | nvarchar(100) | yes |  | ParentAgentId |
| 4 | `ParentAgentName` | nvarchar(400) | yes |  |  |
| 5 | `ParentAgentKey` | int | yes |  | bi.DimAgent, through the parent's dash.AgentInventory row |
| 6 | `RelationshipType` | nvarchar(200) | yes |  |  |
| 7 | `IsValid` | bit | yes |  | IsValidParentChildAgent |
| 8 | `ChildKey` | nvarchar(450) | yes |  | which child it is (2.5.7): `child:<ComponentId>` for a child agent inside its parent; `agent:<inventory AgentId>` or `connected:<EnvironmentId>/<schema name>` for a connected agent |
| 9 | `ChildKind` | nvarchar(20) | yes |  | Child or Connected (2.5.7), from RelationshipType |
| 10 | `ChildInventoryAgentId` | nvarchar(100) | yes |  | a connected agent's own dash.AgentInventory row (2.5.7) |
| 11 | `ParentEnvironment` | nvarchar(400) | yes |  | the parent's environment (2.5.7) |

`ChildAgentName` is the child's name told apart where children share one (2.5.7): by its parent for a child agent, by its environment for a connected agent.

Indexes and constraints:

- `PK_DashAgentChildLink`: primary key, clustered (LinkId)

#### dash.AgentInventoryRun

*Table.* Each load of the agent workbook (2.5.6), from its start, so a failure shows too. Admin &gt; Health reads the last that succeeded, and says when a later one failed.

**Script** `Create-CopilotDashboardSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `RunId` | int | **no** | **PK** identity |  |
| 2 | `StartedUtc` | datetime2(0) | **no** | `sysutcdatetime` |  |
| 3 | `EndedUtc` | datetime2(0) | yes |  |  |
| 4 | `Source` | nvarchar(800) | yes |  | the file, and where it came from |
| 5 | `FileCreatedUtc` | datetime2(0) | yes |  |  |
| 6 | `InventoryRows` | int | yes |  | rows of the AgentInventory sheet |
| 7 | `ProductionRows` | int | yes |  | rows of the AgentsInProd sheet |
| 8 | `ChildLinkRows` | int | yes |  | rows of the ParentChildAgent sheet |
| 9 | `Matched` | int | yes |  | inventory agents the audit log knows |
| 10 | `Status` | nvarchar(20) | **no** | `'Running'` | Running, Succeeded or Failed |
| 11 | `Message` | nvarchar(2000) | yes |  | why a load failed |

Indexes and constraints:

- `PK_DashAgentInventoryRun`: primary key, clustered (RunId)

The workbook's sheets themselves are loaded as they come into `dbo.AgentInventory`, `dbo.AgentsInProd` and `dbo.ParentChildAgent`: one text column for each heading, emptied and loaded again each night. Agent detail reads `dbo.AgentInventory`, which the import grants the site's role.

#### dash.Agent

*View.* Agents with their friendly name, owner and first use. From 2.5.6 the name and owner are the agent workbook's first, then Admin › Reference data's, then the audit log's.

**Script** `Create-CopilotDashboardSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `AgentKey` | int | **no** |  |  |
| 2 | `AgentId` | nvarchar(400) | **no** |  |  |
| 3 | `AgentSource` | nvarchar(30) | yes |  |  |
| 4 | `AgentName` | nvarchar(400) | yes |  |  |
| 5 | `AuditName` | nvarchar(400) | yes |  |  |
| 6 | `Owner` | nvarchar(256) | yes |  |  |
| 7 | `FirstSeenUtc` | datetime2(0) | yes |  |  |
| 8 | `InventoryAgentId` | nvarchar(100) | yes |  | the agent's dash.AgentInventory row (2.5.6) |
| 9 | `IsShared` | bit | yes |  | (2.5.6) |
| 10 | `InProduction` | bit | yes |  | (2.5.6) |

### dash: operations (2.3.0)

What the dashboard's operations features keep, from Create-CopilotOperationsSchema.sql: every table is edited through the Admin pages or the pages named, never by hand.

#### dash.Secret

*Table.* Passwords, client secrets and the webhook address, encrypted by SQL Server (a symmetric key protected by a certificate and the database master key). The site's role reads them only through dash.usp_GetSecret and cannot select the table.

**Script** `Create-CopilotOperationsSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `Name` | varchar(64) | **no** | **PK** |  |
| 2 | `Cipher` | varbinary(8000) | **no** |  |  |
| 3 | `UpdatedBy` | nvarchar(256) | **no** |  |  |
| 4 | `UpdatedUtc` | datetime2(0) | **no** | `sysutcdatetime` |  |

Indexes and constraints:

- `PK_DashSecret`: primary key, clustered (Name)

#### dash.Subscription

*Table.* Scheduled digests (Admin &gt; Alerts and digests): report, recipients, frequency, day and hour in the reporting time zone, period, optionally one manager's organization, and the last send and its result.

**Script** `Create-CopilotOperationsSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `SubscriptionId` | int | **no** | **PK** identity |  |
| 2 | `Name` | nvarchar(200) | **no** |  |  |
| 3 | `Report` | varchar(32) | **no** |  | Leadership \| Adoption \| Licensing \| Security \| Value |
| 4 | `Recipients` | nvarchar(2000) | **no** |  | addresses separated by ; |
| 5 | `Frequency` | varchar(8) | **no** |  |  |
| 6 | `DayOfWeek` | tinyint | yes |  | Weekly: 0 Sunday to 6 Saturday |
| 7 | `DayOfMonth` | tinyint | yes |  | Monthly: 1 to 28 |
| 8 | `HourLocal` | tinyint | **no** | `7` | in the reporting time zone |
| 9 | `RootUpn` | nvarchar(256) | yes |  | a manager's organization; NULL for the whole organization |
| 10 | `Days` | int | **no** | `30` |  |
| 11 | `Enabled` | bit | **no** | `1` |  |
| 12 | `LastSentUtc` | datetime2(0) | yes |  |  |
| 13 | `LastStatus` | nvarchar(400) | yes |  |  |
| 14 | `CreatedBy` | nvarchar(256) | **no** |  |  |
| 15 | `CreatedUtc` | datetime2(0) | **no** | `sysutcdatetime` |  |
| 16 | `FailedTries` | tinyint | **no** | `0` |  |
| 17 | `RetryAfterUtc` | datetime2(0) | yes |  |  |

Indexes and constraints:

- `PK_DashSubscription`: primary key, clustered (SubscriptionId)
- `CK_DashSubscription_Frequency`: check `([Frequency]='Monthly' OR [Frequency]='Weekly' OR [Frequency]='Daily')`

#### dash.NotificationLog

*Table.* Every email and webhook post, sent or not, with the reason when not.

**Script** `Create-CopilotOperationsSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `LogId` | bigint | **no** | **PK** identity |  |
| 2 | `AtUtc` | datetime2(0) | **no** | `sysutcdatetime` |  |
| 3 | `Kind` | varchar(32) | **no** |  | Pipeline \| Freshness \| Security \| Anomaly \| Pool \| Leavers \| NewAgent \| Digest \| Test |
| 4 | `Subject` | nvarchar(400) | **no** |  |  |
| 5 | `Recipients` | nvarchar(2000) | yes |  |  |
| 6 | `Status` | varchar(16) | **no** |  | Sent \| Failed \| NoRecipients \| MailOff |
| 7 | `Error` | nvarchar(2000) | yes |  |  |

Indexes and constraints:

- `PK_DashNotificationLog`: primary key, clustered (LogId)
- `IX_DashNotificationLog_At`: nonclustered (AtUtc)

#### dash.AlertState

*Table.* What each alert check last saw, so an alert fires once per occurrence.

**Script** `Create-CopilotOperationsSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `AlertKey` | nvarchar(200) | **no** | **PK** |  |
| 2 | `LastValue` | nvarchar(400) | yes |  |  |
| 3 | `LastFiredUtc` | datetime2(0) | **no** | `sysutcdatetime` |  |

Indexes and constraints:

- `PK_DashAlertState`: primary key, clustered (AlertKey)

#### dash.Target

*Table.* Adoption targets (Admin &gt; Targets and assumptions): measure, value, start and due dates, for the whole organization or one manager's.

**Script** `Create-CopilotOperationsSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `TargetId` | int | **no** | **PK** identity |  |
| 2 | `RootUpn` | nvarchar(256) | yes |  | a manager's organization; NULL for the whole organization |
| 3 | `Metric` | varchar(16) | **no** |  |  |
| 4 | `TargetValue` | decimal(12,2) | **no** |  | a percentage for Adoption and ActiveShare, a count for ActivePeople |
| 5 | `StartDate` | date | **no** |  |  |
| 6 | `DueDate` | date | **no** |  |  |
| 7 | `Note` | nvarchar(400) | yes |  |  |
| 8 | `CreatedBy` | nvarchar(256) | **no** |  |  |
| 9 | `CreatedUtc` | datetime2(0) | **no** | `sysutcdatetime` |  |

Indexes and constraints:

- `PK_DashTarget`: primary key, clustered (TargetId)
- `CK_DashTarget_Metric`: check `([Metric]='ActiveShare' OR [Metric]='ActivePeople' OR [Metric]='Adoption')`
- `CK_DashTarget_Dates`: check `([DueDate]>[StartDate])`

#### dash.TimeSaving

*Table.* Minutes one interaction saves in each app, where it differs from the default.

**Script** `Create-CopilotOperationsSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `App` | nvarchar(200) | **no** | **PK** | bi.DimApp.App |
| 2 | `Minutes` | decimal(6,2) | **no** |  |  |
| 3 | `UpdatedBy` | nvarchar(256) | **no** |  |  |
| 4 | `UpdatedUtc` | datetime2(0) | **no** | `sysutcdatetime` |  |

Indexes and constraints:

- `PK_DashTimeSaving`: primary key, clustered (App)
- `CK_DashTimeSaving_Minutes`: check `([Minutes]>=(0) AND [Minutes]<=(240))`

#### dash.TrainingAttendance

*Table.* Who attended which training session, from a CSV (Admin &gt; Training records).

**Script** `Create-CopilotOperationsSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `Upn` | nvarchar(256) | **no** | **PK** |  |
| 2 | `Session` | nvarchar(190) | **no** | **PK** | short enough for the key (900 bytes) |
| 3 | `AttendedOn` | date | **no** | **PK** |  |
| 4 | `ImportedBy` | nvarchar(256) | **no** |  |  |
| 5 | `ImportedUtc` | datetime2(0) | **no** | `sysutcdatetime` |  |

Indexes and constraints:

- `PK_DashTrainingAttendance`: primary key, clustered (Upn, Session, AttendedOn)
- `IX_DashTraining_Session`: nonclustered (Session, AttendedOn)

#### dash.ValueName

*Table.* Names and notes administrators gave cataloged values (Admin &gt; Value names).

**Script** `Create-CopilotOperationsSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `ColumnId` | smallint | **no** | **PK** | audit.ValueCatalogColumn |
| 2 | `Value` | nvarchar(400) | **no** | **PK** |  |
| 3 | `Name` | nvarchar(200) | **no** |  |  |
| 4 | `Note` | nvarchar(400) | yes |  |  |
| 5 | `UpdatedBy` | nvarchar(256) | **no** |  |  |
| 6 | `UpdatedUtc` | datetime2(0) | **no** | `sysutcdatetime` |  |

Indexes and constraints:

- `PK_DashValueName`: primary key, clustered (ColumnId, Value)

#### dash.FlagCase

*Table.* Security's triage of a flagged interaction: status (New, Investigating, Closed, FalsePositive), owner and note, by record id.

**Script** `Create-CopilotOperationsSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `RecordId` | varchar(64) | **no** | **PK** |  |
| 2 | `Status` | varchar(16) | **no** |  |  |
| 3 | `Owner` | nvarchar(256) | yes |  |  |
| 4 | `Note` | nvarchar(2000) | yes |  |  |
| 5 | `UpdatedBy` | nvarchar(256) | **no** |  |  |
| 6 | `UpdatedUtc` | datetime2(0) | **no** | `sysutcdatetime` |  |

Indexes and constraints:

- `PK_DashFlagCase`: primary key, clustered (RecordId)
- `CK_DashFlagCase_Status`: check `([Status]='FalsePositive' OR [Status]='Closed' OR [Status]='Investigating' OR [Status]='New')`

#### dash.DigestPreference

*Table.* Managers who do not get their team digest, kept by administrators (Admin &gt; Alerts and digests). PersonalDigest is unused since 2.4.4.

**Script** `Create-CopilotOperationsSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `Upn` | nvarchar(256) | **no** | **PK** |  |
| 2 | `ManagerDigestOptOut` | bit | **no** | `0` |  |
| 3 | `PersonalDigest` | bit | **no** | `0` |  |
| 4 | `UpdatedUtc` | datetime2(0) | **no** | `sysutcdatetime` |  |

Indexes and constraints:

- `PK_DashDigestPreference`: primary key, clustered (Upn)

#### dash.DigestSend

*Table.* One row per person, kind (Manager or Personal) and period: sent, or the try that failed (Fail1 to Fail3), so a digest is never sent twice and a failure is retried.

**Script** `Create-CopilotOperationsSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `Upn` | nvarchar(256) | **no** | **PK** |  |
| 2 | `Kind` | varchar(16) | **no** | **PK** | Manager \| Personal |
| 3 | `PeriodKey` | varchar(16) | **no** | **PK** | 2026-W40, 2026-10 |
| 4 | `SentUtc` | datetime2(0) | **no** | `sysutcdatetime` |  |
| 5 | `Status` | varchar(16) | **no** |  |  |

Indexes and constraints:

- `PK_DashDigestSend`: primary key, clustered (Upn, Kind, PeriodKey)

#### dash.LicenseAction

*Table.* Each person in each license export (upgrade, revoke, leaver, wave), with who exported it and when, for Forecasting &gt; License action outcomes.

**Script** `Create-CopilotOperationsSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `ActionId` | bigint | **no** | **PK** identity |  |
| 2 | `Kind` | varchar(16) | **no** |  | upgrade \| revoke \| leaver \| wave |
| 3 | `Upn` | nvarchar(256) | **no** |  |  |
| 4 | `ExportedUtc` | datetime2(0) | **no** | `sysutcdatetime` |  |
| 5 | `ExportedBy` | nvarchar(256) | **no** |  |  |

Indexes and constraints:

- `PK_DashLicenseAction`: primary key, clustered (ActionId)
- `IX_DashLicenseAction_Upn`: nonclustered (Upn, ExportedUtc)

#### dash.LicenseContract

*Table.* License contracts (Forecasting &gt; Budget and renewal): seats, price per seat a month, start and renewal dates.

**Script** `Create-CopilotOperationsSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `ContractId` | int | **no** | **PK** identity |  |
| 2 | `Name` | nvarchar(200) | **no** |  |  |
| 3 | `Seats` | int | **no** |  |  |
| 4 | `MonthlyPrice` | decimal(12,2) | **no** |  |  |
| 5 | `StartDate` | date | **no** |  |  |
| 6 | `RenewalDate` | date | **no** |  |  |
| 7 | `Note` | nvarchar(400) | yes |  |  |
| 8 | `UpdatedBy` | nvarchar(256) | **no** |  |  |
| 9 | `UpdatedUtc` | datetime2(0) | **no** | `sysutcdatetime` |  |

Indexes and constraints:

- `PK_DashLicenseContract`: primary key, clustered (ContractId)
- `CK_DashLicenseContract_Seats`: check `([Seats]>=(0))`

#### dash.NonHumanException

*Table.* Accounts a non-human rule matches that an administrator confirmed are a person, with the reason (2.4.5): a Copilot license on a non-human account is a policy breach, and these are not. Removing a row flags the account again.

**Script** `Create-CopilotOperationsSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `Upn` | nvarchar(256) | **no** | **PK** | the sign-in confirmed as a person |
| 2 | `Reason` | nvarchar(400) | **no** |  | why it is a person |
| 3 | `ConfirmedBy` | nvarchar(256) | **no** |  | the administrator |
| 4 | `ConfirmedUtc` | datetime2(0) | **no** | `sysutcdatetime` |  |

Indexes and constraints:

- `PK_DashNonHumanException`: primary key, clustered (Upn)

#### dash.SavedView

*Table.* Unused since 2.4.4, which removed saved views; kept, with its rows. Each was a page and its query, one of which could be the start page.

**Script** `Create-CopilotOperationsSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `ViewId` | int | **no** | **PK** identity |  |
| 2 | `Owner` | nvarchar(256) | **no** |  | the viewer's UPN or Windows name |
| 3 | `Name` | nvarchar(100) | **no** |  |  |
| 4 | `Url` | nvarchar(600) | **no** |  | a path and query on this site |
| 5 | `IsStart` | bit | **no** | `0` |  |
| 6 | `CreatedUtc` | datetime2(0) | **no** | `sysutcdatetime` |  |

Indexes and constraints:

- `PK_DashSavedView`: primary key, clustered (ViewId)
- `IX_DashSavedView_Owner`: nonclustered (Owner)

#### dash.JobFamilyRule

*Table.* Job-title patterns (SQL LIKE) to job families, first matching by priority (Admin &gt; Classifications and events).

**Script** `Create-CopilotOperationsSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `RuleId` | int | **no** | **PK** identity |  |
| 2 | `Pattern` | nvarchar(200) | **no** |  | a LIKE pattern on bi.DimUser.JobTitle, such as %engineer% |
| 3 | `Family` | nvarchar(100) | **no** |  |  |
| 4 | `Priority` | int | **no** | `100` |  |
| 5 | `UpdatedBy` | nvarchar(256) | **no** |  |  |
| 6 | `UpdatedUtc` | datetime2(0) | **no** | `sysutcdatetime` |  |

Indexes and constraints:

- `PK_DashJobFamilyRule`: primary key, clustered (RuleId)

#### dash.UseCaseRule

*Table.* App patterns, web search and agent to use cases, first matching by priority; seeded with defaults.

**Script** `Create-CopilotOperationsSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `RuleId` | int | **no** | **PK** identity |  |
| 2 | `AppPattern` | nvarchar(200) | **no** |  | a LIKE pattern on bi.DimApp.App |
| 3 | `WebGrounded` | bit | yes |  | NULL: either |
| 4 | `WithAgent` | bit | yes |  | NULL: either |
| 5 | `UseCase` | nvarchar(100) | **no** |  |  |
| 6 | `Priority` | int | **no** | `100` |  |
| 7 | `UpdatedBy` | nvarchar(256) | **no** |  |  |
| 8 | `UpdatedUtc` | datetime2(0) | **no** | `sysutcdatetime` |  |

Indexes and constraints:

- `PK_DashUseCaseRule`: primary key, clustered (RuleId)

#### dash.EventMarker

*Table.* Events drawn on the trend charts: day, name and kind.

**Script** `Create-CopilotOperationsSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `MarkerId` | int | **no** | **PK** identity |  |
| 2 | `Day` | date | **no** |  |  |
| 3 | `Label` | nvarchar(100) | **no** |  |  |
| 4 | `Kind` | varchar(16) | **no** |  |  |
| 5 | `UpdatedBy` | nvarchar(256) | **no** |  |  |

Indexes and constraints:

- `PK_DashEventMarker`: primary key, clustered (MarkerId)
- `IX_DashEventMarker_Day`: nonclustered (Day)
- `CK_DashEventMarker_Kind`: check `([Kind]='Other' OR [Kind]='Policy' OR [Kind]='Outage' OR [Kind]='Training' OR [Kind]='Rollout')`

#### dash.Extract

*Table.* Scheduled CSV extracts (Admin &gt; Scheduled extracts): dataset, folder, schedule, days covered, and the last run and its result.

**Script** `Create-CopilotOperationsSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `ExtractId` | int | **no** | **PK** identity |  |
| 2 | `Name` | nvarchar(100) | **no** |  |  |
| 3 | `Dataset` | varchar(32) | **no** |  |  |
| 4 | `Folder` | nvarchar(400) | **no** |  | a local path or \\\\server\\share\\folder the site's account may write |
| 5 | `Frequency` | varchar(8) | **no** |  |  |
| 6 | `DayOfWeek` | tinyint | yes |  |  |
| 7 | `DayOfMonth` | tinyint | yes |  |  |
| 8 | `HourLocal` | tinyint | **no** | `6` |  |
| 9 | `Days` | int | **no** | `30` |  |
| 10 | `Enabled` | bit | **no** | `1` |  |
| 11 | `LastRunUtc` | datetime2(0) | yes |  |  |
| 12 | `LastStatus` | nvarchar(400) | yes |  |  |
| 13 | `CreatedBy` | nvarchar(256) | **no** |  |  |
| 14 | `FailedTries` | tinyint | **no** | `0` |  |
| 15 | `RetryAfterUtc` | datetime2(0) | yes |  |  |

Indexes and constraints:

- `PK_DashExtract`: primary key, clustered (ExtractId)
- `CK_DashExtract_Frequency`: check `([Frequency]='Monthly' OR [Frequency]='Weekly' OR [Frequency]='Daily')`

### Left over from 1.x

Present in a database upgraded from 1.x, as the lab is. No current script creates them.

#### audit.UserProfile

*Table.* Entra profiles written by the 1.x license tool. No 2.x script creates, fills or reads it; only the optional ASP.NET dashboard still reads it for names. Safe to drop once that no longer matters.

**Script** 1.x (no current script) · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `UserPrincipalName` | nvarchar(512) | **no** | **PK** |  |
| 2 | `DisplayName` | nvarchar(400) | yes |  |  |
| 3 | `Department` | nvarchar(200) | yes |  |  |
| 4 | `JobTitle` | nvarchar(200) | yes |  |  |
| 5 | `OfficeLocation` | nvarchar(200) | yes |  |  |
| 6 | `City` | nvarchar(200) | yes |  |  |
| 7 | `Country` | nvarchar(200) | yes |  |  |
| 8 | `CompanyName` | nvarchar(200) | yes |  |  |
| 9 | `UsageLocation` | nvarchar(10) | yes |  |  |
| 10 | `UserType` | nvarchar(20) | yes |  |  |
| 11 | `AccountEnabled` | bit | yes |  |  |
| 12 | `ManagerUpn` | nvarchar(512) | yes |  |  |
| 13 | `CreatedDateTimeUtc` | datetime2(0) | yes |  |  |
| 14 | `DirectoryStatus` | nvarchar(20) | yes |  |  |
| 15 | `UpdatedUtc` | datetime2(0) | **no** | `sysutcdatetime` |  |
| 16 | `Division` | nvarchar(200) | yes |  |  |

Indexes and constraints:

- `PK_UserProfile`: primary key, clustered (UserPrincipalName)
- `IX_UserProfile_Department`: nonclustered (Department) include (OfficeLocation, Country)

#### audit.AdministrativeUnit

*Table.* Administrative-unit names written by the 1.x license tool. Not used since 2.0.0; safe to drop.

**Script** 1.x (no current script) · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `Id` | nvarchar(100) | **no** | **PK** |  |
| 2 | `DisplayName` | nvarchar(400) | yes |  |  |
| 3 | `Description` | nvarchar(1000) | yes |  |  |
| 4 | `UpdatedUtc` | datetime2(0) | **no** | `sysutcdatetime` |  |

Indexes and constraints:

- `PK_AdministrativeUnit`: primary key, clustered (Id)

<!-- generated:columns:end -->

---

# Part C. The scripts

The full text of every script that creates or fills the schema, in the order a new install runs them: the installer, the SQL scripts it runs, the two SQL scripts other tools run, and the three PowerShell scripts that create tables or decide the order in which they are filled. Generated from the files themselves.

Not here, because they load data and create nothing of their own: `Invoke-CopilotAuditBackfill.ps1` (the audit log loads), `tools/Import-CopilotAuditCsv.ps1`, `tools/Export-UsersWithHierarchy-ToSql.ps1` and `tools/Update-CopilotAuditEnrichment.ps1`. Each runs one of the SQL scripts below before it loads, and is in the repository.

<!-- generated:scripts:start -->

### C.1 `Install-CopilotAudit.ps1`

The installer. Creates both databases, runs the SQL scripts below in order, sets the reporting time zone, grants the reporting accounts their role, and builds everything from the records already loaded. 511 lines.

```powershell
<#
.SYNOPSIS
    Sets up, upgrades and builds the Copilot audit databases in one command.

.DESCRIPTION
    Run it once before the first backfill, and again after it: it does what the
    state of the databases calls for, and is safe to run any number of times.

      1. Creates the Copilot and license-event databases if they do not exist
         (compatibility level 160 or the server's highest below it, SIMPLE
         recovery so the bulk loads are minimally logged), and with
         -ServiceAccount gives the nightly job's account a login and db_owner
         in both.
      2. Installs or upgrades every table, view and procedure, in the order
         they depend on each other. Nothing is dropped; data is kept.
      3. Builds whatever needs building from the data already loaded:
           * the license events shredded: the records not shredded yet, and
             what a shred that was stopped part way was shredding (2.5.4: not
             every record on every run; once more over a database an earlier
             release shredded, unless -LicenseEventsShredded);
           * every record flattened, the first time there are records to
             flatten - and again once after an upgrade that adds columns (2.2.0),
             so history gets them too;
           * sessions and the bi schema, in full, the first time or after that
             re-flatten; otherwise nothing: the nightly -Incremental run keeps
             them current;
           * the dashboard's org chart and rollups, when -Dashboard is given.
      4. Reports what it did and what is left to do.

    Nothing here is typed into SQL by hand: settings are parameters, and
    day-to-day tasks are tools\Invoke-CopilotMaintenance.ps1.

.PARAMETER Dashboard
    Also install the Copilot Insights dashboard's schema and build its rollups.
    The site itself is installed on the web server with
    dashboard\Install-CopilotInsights.ps1.

.PARAMETER OrgTopUpn
    The top of the organization for the dashboard (usually the CEO). Leave it
    out and the dashboard uses the one person the org chart has most people
    under, or keeps what an administrator already set.

.PARAMETER ReportingTimeZone
    The zone days and Saturday-to-Friday weeks are cut in: a name from
    sys.time_zone_info, such as 'GMT Standard Time'. Default: this server's
    own zone, for a database with no records and no zone yet (2.4.10); a
    database no installer set reads Eastern Standard Time. Stored in both
    databases, where every procedure reads it; set it
    before the first load, as changing it later would leave history cut in
    the old zone (it is refused then).

.PARAMETER Rebuild
    Rebuild the flattened table, sessions, bi and the dashboard in full even
    if nothing calls for it, and shred every license record again.

.PARAMETER LicenseEventsShredded
    For the first run of 2.5.4 or later over a license database an earlier
    release shredded. Such a database carries no mark that its last shred
    finished, so this run shreds every license record once more and leaves
    the mark; later runs then shred only what is new. With this switch you
    say that the last shred did finish (the installer run that made it
    ended with Done.): the mark is written and nothing is shredded again.
    Leave it out when unsure: a shred that was stopped part way left events
    without their properties, and only shredding them all repairs that.

.EXAMPLE
    .\Install-CopilotAudit.ps1 -SqlServer SQLPROD01
.EXAMPLE
    .\Install-CopilotAudit.ps1 -SqlServer SQLPROD01 -Dashboard -LicenseEventDatabases CopilotLicenseEvents,CopilotLicenseLive
#>
#Requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $SqlServer,
    [string] $Database = 'CopilotAudit',
    [string] $LicenseDatabase = 'CopilotLicenseEvents',
    # Databases holding license events, for license at the time of use (the backfill's -SessionLicenseDatabase). Defaults to -LicenseDatabase.
    [string[]] $LicenseEventDatabases,
    [pscredential] $SqlCredential,
    [switch] $TrustServerCertificate,
    [switch] $Dashboard,
    [string] $OrgTopUpn,
    [switch] $Rebuild,
    # The license records were all shredded by an earlier release, in a run that finished: do not shred them once more (2.5.4).
    [switch] $LicenseEventsShredded,
    [string] $ReportingTimeZone,
    [switch] $SkipLicenseDatabase,
    # The account the nightly job runs as (DOMAIN\gmsa$ or DOMAIN\user): given a login and db_owner in both databases.
    [string] $ServiceAccount,
    # Accounts that read the bi views and nothing else, such as the Power BI gateway's (DOMAIN\name).
    [string[]] $ReportingAccount = @()
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$sqlDir = Join-Path $PSScriptRoot 'sql'
if (-not $LicenseEventDatabases) { $LicenseEventDatabases = if ($SkipLicenseDatabase) { @() } else { @($LicenseDatabase) } }
$licList = if ($LicenseEventDatabases) { @($LicenseEventDatabases) -join ',' } else { $null }

function Write-Step([string] $Text) { Write-Host "==> $Text" -ForegroundColor Cyan }
function Write-Done([string] $Text) { Write-Host "    $Text" }

function New-ConnString([string] $Catalog) {
    $b = New-Object System.Data.SqlClient.SqlConnectionStringBuilder
    $b['Data Source'] = $SqlServer
    $b['Initial Catalog'] = $Catalog
    if ($SqlCredential) { $b['User ID'] = $SqlCredential.UserName; $b['Password'] = $SqlCredential.GetNetworkCredential().Password }
    else { $b['Integrated Security'] = $true }
    $b['Encrypt'] = $true
    $b['TrustServerCertificate'] = [bool] $TrustServerCertificate
    $b['Application Name'] = 'Install-CopilotAudit'
    return $b.ConnectionString
}

function Invoke-Sql {
    param([string] $Catalog, [string] $Sql, [hashtable] $Parameters = @{}, [switch] $Scalar, [int] $Timeout = 0)
    $conn = New-Object System.Data.SqlClient.SqlConnection (New-ConnString $Catalog)
    $conn.add_InfoMessage({ param($s, $e) foreach ($m in $e.Errors) { if ($m.Class -le 10 -and $m.Message -notlike 'Warning: Null value*' -and $m.Message -notlike 'Warning! The maximum key length*') { Write-Host "      $($m.Message)" -ForegroundColor DarkGray } } })
    $conn.Open()
    try {
        $cmd = $conn.CreateCommand(); $cmd.CommandText = $Sql; $cmd.CommandTimeout = $Timeout
        foreach ($k in $Parameters.Keys) { [void] $cmd.Parameters.AddWithValue($k, $(if ($null -eq $Parameters[$k]) { [DBNull]::Value } else { $Parameters[$k] })) }
        if ($Scalar) { return $cmd.ExecuteScalar() }
        [void] $cmd.ExecuteNonQuery()
    }
    finally { $conn.Dispose() }
}

function Invoke-SqlFile([string] $Catalog, [string] $Name) {
    $text = Get-Content -LiteralPath (Join-Path $sqlDir $Name) -Raw
    # Batches split on GO alone on a line, as sqlcmd does.
    foreach ($batch in [regex]::Split($text, '(?im)^[ \t]*GO[ \t]*(?:--[^\r\n]*)?\r?$')) {
        if ($batch.Trim()) { Invoke-Sql -Catalog $Catalog -Sql $batch }
    }
    Write-Done $Name
}

function Test-Object([string] $Catalog, [string] $Name, [string] $Type = 'U') {
    [bool] (Invoke-Sql -Catalog $Catalog -Scalar -Sql "SELECT CASE WHEN OBJECT_ID(@n, @t) IS NULL THEN 0 ELSE 1 END" -Parameters @{ '@n' = $Name; '@t' = $Type })
}
function Get-Count([string] $Catalog, [string] $Table) {
    if (-not (Test-Object $Catalog $Table)) { return [long] 0 }
    [long] (Invoke-Sql -Catalog $Catalog -Scalar -Sql "SELECT SUM(row_count) FROM sys.dm_db_partition_stats WHERE object_id = OBJECT_ID(@t) AND index_id IN (0, 1)" -Parameters @{ '@t' = $Table })
}

function Enter-RunLock([string] $Catalog) {
    <#
        Holds the database for this run alone (2.4.12), as the nightly pipeline and the maintenance rebuilds do: an
        exclusive application lock, on a connection of its own kept open to the end of the build. Nothing kept them
        apart, and a nightly that started during an upgrade's full bi rebuild rebuilt the fact under it, counting
        interactions twice. As the pipeline's history build step, the pipeline holds it already (CopilotAuditRunLock).
    #>
    if ($env:CopilotAuditRunLock -eq "$SqlServer/$Catalog") { return $null }
    $b = New-Object System.Data.SqlClient.SqlConnectionStringBuilder (New-ConnString $Catalog)
    $b['Pooling'] = $false   # closing a pooled connection keeps its session, and the session's lock with it
    $c = New-Object System.Data.SqlClient.SqlConnection $b.ConnectionString
    try {
        $c.Open()
        $cmd = $c.CreateCommand()
        $cmd.CommandText = "DECLARE @r INT; EXEC @r = sp_getapplock @Resource = N'CopilotAudit.Run', @LockMode = 'Exclusive', @LockOwner = 'Session', @LockTimeout = 0; SELECT @r;"
        $granted = [int] $cmd.ExecuteScalar() -ge 0
    }
    catch { $c.Dispose(); throw }
    if (-not $granted) {
        $c.Dispose()
        throw "Another run is using $Catalog now: the nightly pipeline, a maintenance rebuild, or this installer in another window. Nothing was changed. Let it finish (Admin > Health shows the pipeline's runs), disable the nightly task while you upgrade, then run this again."
    }
    $c
}

# ---- 1. Databases -------------------------------------------------------------------------------
Write-Step "Databases on $SqlServer"
$targetLevel = [int] (Invoke-Sql -Catalog master -Scalar -Sql "SELECT CASE WHEN CAST(SERVERPROPERTY('ProductMajorVersion') AS INT) >= 16 THEN 160 ELSE CAST(SERVERPROPERTY('ProductMajorVersion') AS INT) * 10 END")
if ($targetLevel -lt 140) { throw 'SQL Server 2017 or later is needed.' }
$dbs = @($Database) + $(if ($SkipLicenseDatabase) { @() } else { @($LicenseDatabase) })
foreach ($db in $dbs) {
    $exists = [bool] (Invoke-Sql -Catalog master -Scalar -Sql 'SELECT CASE WHEN DB_ID(@d) IS NULL THEN 0 ELSE 1 END' -Parameters @{ '@d' = $db })
    if (-not $exists) {
        Invoke-Sql -Catalog master -Sql "CREATE DATABASE [$($db.Replace(']', ']]'))]; ALTER DATABASE [$($db.Replace(']', ']]'))] SET RECOVERY SIMPLE;"
        Write-Done "created $db (SIMPLE recovery)"
    }
    $level = [int] (Invoke-Sql -Catalog master -Scalar -Sql 'SELECT compatibility_level FROM sys.databases WHERE name = @d' -Parameters @{ '@d' = $db })
    if ($level -lt 130) {
        Invoke-Sql -Catalog master -Sql "ALTER DATABASE [$($db.Replace(']', ']]'))] SET COMPATIBILITY_LEVEL = $targetLevel;"
        Write-Done "$db compatibility level $level -> $targetLevel"
    }
    elseif ($exists) { Write-Done "$db exists (level $level)" }
}

if ($ServiceAccount) {
    $sa = $ServiceAccount.Replace(']', ']]')
    Invoke-Sql -Catalog master -Sql "IF SUSER_ID(@a) IS NULL EXEC (N'CREATE LOGIN [$sa] FROM WINDOWS');" -Parameters @{ '@a' = $ServiceAccount }
    foreach ($db in $dbs) {
        # The login may already be in the database under another name: as dbo when it owns the
        # database (it created it), or as a user a DBA added. That user is the one to use.
        Invoke-Sql -Catalog $db -Parameters @{ '@a' = $ServiceAccount } -Sql @"
DECLARE @u sysname = (SELECT name FROM sys.database_principals WHERE sid = SUSER_SID(@a));
IF @u IS NULL BEGIN EXEC (N'CREATE USER [$sa] FOR LOGIN [$sa]'); SET @u = @a; END;
IF @u <> N'dbo' AND IS_ROLEMEMBER(N'db_owner', @u) = 0
BEGIN
    DECLARE @s nvarchar(400) = N'ALTER ROLE db_owner ADD MEMBER ' + QUOTENAME(@u) + N';';
    EXEC (@s);
END;
"@
    }
    Write-Done "$ServiceAccount has a login and db_owner in $($dbs -join ' and ')"
}

# One run at a time (2.4.12): held from here to the end of step 3, and let go in the finally there.
$runLock = Enter-RunLock $Database
try {

# ---- 2. Objects ---------------------------------------------------------------------------------
Write-Step "Tables, views and procedures in $Database"
$copilotScripts = 'Create-CopilotAuditSchema.sql', 'Create-CopilotLoadProcedure.sql',
                  'Create-CopilotFlattenedSchema.sql', 'Create-CopilotFlattenProcedure.sql', 'Create-CopilotPurgeProcedure.sql',
                  'Create-CopilotEnrichmentSchema.sql', 'Create-CopilotSessionSchema.sql', 'Create-CopilotSessionProcedure.sql',
                  'Create-CopilotBiSchema.sql', 'Create-CopilotBiProcedure.sql'
if ($Dashboard) { $copilotScripts += 'Create-CopilotDashboardSchema.sql', 'Create-CopilotDashboardProcedure.sql', 'Create-CopilotOperationsSchema.sql' }
foreach ($s in $copilotScripts) { Invoke-SqlFile $Database $s }
if (-not $SkipLicenseDatabase) {
    Write-Step "Tables, views and procedures in $LicenseDatabase"
    foreach ($s in 'Create-CopilotAuditSchema.sql', 'Create-CopilotLoadProcedure.sql', 'Create-LicenseEventSchema.sql', 'Create-LicenseEventProcedure.sql', 'Create-AgentEventProcedure.sql') { Invoke-SqlFile $LicenseDatabase $s }
}

# The reporting time zone, stored where every procedure reads it (audit.ReportingTimeZone()).
$zoneDbs = @($Database) + $(if ($SkipLicenseDatabase) { @() } else { @($LicenseDatabase) })
if ($ReportingTimeZone) {
    $known = [int] (Invoke-Sql -Catalog master -Scalar -Sql 'SELECT COUNT(*) FROM sys.time_zone_info WHERE name = @z' -Parameters @{ '@z' = $ReportingTimeZone })
    if (-not $known) { throw "'$ReportingTimeZone' is not a time zone SQL Server knows. SELECT name FROM sys.time_zone_info lists them." }
    foreach ($db in $zoneDbs) {
        $current = [string] (Invoke-Sql -Catalog $db -Scalar -Sql 'SELECT audit.ReportingTimeZone();')
        if ($current -eq $ReportingTimeZone) { continue }
        if ((Get-Count $db 'audit.CopilotAudit') -gt 0 -or (Get-Count $db 'audit.CopilotInteraction') -gt 0) {
            throw "$db already holds records cut in $current. The reporting time zone is set before the first load; to change it, load into new databases."
        }
        Invoke-Sql -Catalog $db -Parameters @{ '@z' = $ReportingTimeZone } -Sql @"
MERGE audit.PipelineSetting AS t USING (SELECT N'ReportingTimeZone' AS Name, @z AS Value) AS s ON t.Name = s.Name
WHEN MATCHED THEN UPDATE SET Value = s.Value, UpdatedUtc = SYSUTCDATETIME(), UpdatedBy = SUSER_SNAME()
WHEN NOT MATCHED THEN INSERT (Name, Value) VALUES (s.Name, s.Value);
"@
    }
}
else {
    # 2.4.10: with no zone named, a database that holds no records yet takes this server's own time zone (it was
    # Eastern whatever the server). One that holds records, or that has a zone set, keeps it: its days are already cut.
    $serverZone = [TimeZoneInfo]::Local.Id
    # .NET 6 and later only (PowerShell 7): Windows PowerShell 5.1's zone ids are Windows ones already, and reading the
    # property there stopped the script under strict mode, before any rebuild ran (2.4.12).
    if ($PSVersionTable.PSEdition -eq 'Core' -and [TimeZoneInfo]::Local.HasIanaId) {
        $windowsId = $null
        $serverZone = if ([TimeZoneInfo]::TryConvertIanaIdToWindowsId($serverZone, [ref] $windowsId)) { $windowsId } else { $null }
    }
    if ($serverZone -and [int] (Invoke-Sql -Catalog master -Scalar -Sql 'SELECT COUNT(*) FROM sys.time_zone_info WHERE name = @z' -Parameters @{ '@z' = $serverZone })) {
        foreach ($db in $zoneDbs) {
            if ([int] (Invoke-Sql -Catalog $db -Scalar -Sql "SELECT COUNT(*) FROM audit.PipelineSetting WHERE Name = N'ReportingTimeZone';") -gt 0) { continue }
            if ((Get-Count $db 'audit.CopilotAudit') -gt 0 -or (Get-Count $db 'audit.CopilotInteraction') -gt 0) { continue }
            Invoke-Sql -Catalog $db -Parameters @{ '@z' = $serverZone } -Sql "INSERT INTO audit.PipelineSetting (Name, Value) VALUES (N'ReportingTimeZone', @z);"
        }
    }
}
Write-Done ("reporting time zone: {0}" -f (Invoke-Sql -Catalog $Database -Scalar -Sql 'SELECT audit.ReportingTimeZone();'))

# Reporting accounts read the bi views and nothing else (2.4.12). They were given SELECT on the whole bi schema, which
# holds the tables under the views too: the Power BI account read bi.DimUser's real names beside privacy mode's
# pseudonyms, and every column the views leave out. They are members of CopilotReporting now, which each run grants
# every bi view to, so a view a release adds is readable too; the schema-wide grant is taken back.
$reporting = @($ReportingAccount | Where-Object { $_ })
if ($reporting.Count -gt 0 -or [bool] (Invoke-Sql -Catalog $Database -Scalar -Sql "SELECT CASE WHEN DATABASE_PRINCIPAL_ID(N'CopilotReporting') IS NULL THEN 0 ELSE 1 END")) {
    Invoke-Sql -Catalog $Database -Sql @"
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
"@
}
foreach ($acct in $reporting) {
    $ra = $acct.Replace(']', ']]')
    # A database user of that name already there (this installer's from before, or a DBA's) is used as it is.
    if (-not [bool] (Invoke-Sql -Catalog $Database -Scalar -Sql 'SELECT CASE WHEN USER_ID(@a) IS NULL THEN 0 ELSE 1 END' -Parameters @{ '@a' = $acct })) {
        Invoke-Sql -Catalog master -Sql "IF SUSER_ID(@a) IS NULL EXEC (N'CREATE LOGIN [$ra] FROM WINDOWS');" -Parameters @{ '@a' = $acct }
        Invoke-Sql -Catalog $Database -Sql "CREATE USER [$ra] FOR LOGIN [$ra];"
    }
    Invoke-Sql -Catalog $Database -Sql "REVOKE SELECT ON SCHEMA::bi FROM [$ra]; ALTER ROLE CopilotReporting ADD MEMBER [$ra];"
    Write-Done "$acct can read the bi views in $Database, and nothing else"
}
# Others that read every table of bi (an account given -ReportingAccount before 2.4.12, and not again): said, not changed.
$wide = Invoke-Sql -Catalog $Database -Scalar -Sql @"
SELECT STRING_AGG(CONVERT(NVARCHAR(MAX), pr.name), N', ')
FROM sys.database_permissions AS p
JOIN sys.database_principals AS pr ON pr.principal_id = p.grantee_principal_id
WHERE p.class = 3 AND p.major_id = SCHEMA_ID(N'bi') AND p.permission_name = N'SELECT' AND p.state IN ('G', 'W')
  AND pr.name <> N'CopilotInsightsApp' AND ISNULL(IS_ROLEMEMBER(N'CopilotInsightsApp', pr.name), 0) = 0
  AND NOT EXISTS (SELECT 1 FROM sys.database_permissions AS d WHERE d.grantee_principal_id = pr.principal_id AND d.class = 3 AND d.major_id = SCHEMA_ID(N'dash'));
"@
if ($wide -isnot [DBNull] -and $wide) {
    Write-Warning "These accounts can read every table of the bi schema in $Database, not only its views, so privacy mode does not hide names from them: $wide. Give a reporting account to -ReportingAccount again to narrow it to the views."
}

if ($Dashboard -and $OrgTopUpn) {
    Invoke-Sql -Catalog $Database -Parameters @{ '@v' = $OrgTopUpn.Trim(); '@by' = "$env:USERDOMAIN\$env:USERNAME" } -Sql @"
UPDATE dash.Setting SET Value = @v, UpdatedBy = @by, UpdatedUtc = SYSUTCDATETIME() WHERE Name = 'OrgTopUpn';
"@
    Write-Done "top of the organization: $OrgTopUpn"
}

# ---- 3. Build what needs building ---------------------------------------------------------------
Write-Step 'Building from the data loaded so far'
$records = Get-Count $Database 'audit.CopilotAudit'
$flattened = Get-Count $Database 'audit.RawLogFlattened'
$todo = @()

if (-not $SkipLicenseDatabase) {
    $licRecords = Get-Count $LicenseDatabase 'audit.CopilotAudit'
    if ($licRecords -gt 0) {
        # 2.5.4: only when something calls for it. Every record was read and parsed again on every run, an upgrade's
        # longest step on a large license history, to write the rows already there. Every record is shredded now for
        # -Rebuild and, once, for a database with no mark that a shred of every record finished
        # (LicenseShredCompleteUtc), which is every database an earlier release shredded - unless
        # -LicenseEventsShredded says that one did. After that: records with no event of their own (counted: two
        # columns of the columnstore against a key) are shredded from the earliest of them on; and a shred that was
        # stopped part way, which leaves its mark (LicenseShredInProgress) and events without their properties, has
        # what it was shredding shredded again, and no more.
        $unshredded = [long] (Invoke-Sql -Catalog $LicenseDatabase -Scalar -Sql @"
SELECT COUNT_BIG(*) FROM audit.CopilotAudit AS a
WHERE a.Operation NOT LIKE N'Bot%' AND NOT EXISTS (SELECT 1 FROM audit.LicenseEvent AS e WHERE e.RecordId = a.RecordId);
"@)
        $unshreddedAgents = [long] (Invoke-Sql -Catalog $LicenseDatabase -Scalar -Sql @"
SELECT COUNT_BIG(*) FROM audit.CopilotAudit AS a
WHERE a.Operation LIKE N'Bot%' AND NOT EXISTS (SELECT 1 FROM audit.AgentEvent AS e WHERE e.RecordId = a.RecordId);
"@)
        $marks = [string] (Invoke-Sql -Catalog $LicenseDatabase -Scalar -Sql "SELECT ISNULL(STRING_AGG(CONVERT(NVARCHAR(MAX), Name), N','), N'') FROM audit.PipelineSetting WHERE Name IN (N'LicenseShredInProgress', N'LicenseShredCompleteUtc');")
        $stopped = $marks -like '*LicenseShredInProgress*'
        $complete = $marks -like '*LicenseShredCompleteUtc*'
        # The one damage that shows in the tables themselves: a chunk group with every fragment, and none of the
        # properties it reassembles into on its first record. A shred stopped before its last step leaves them so.
        # Only a group whose document does hold properties counts: one that holds none has none to show.
        $unassembled = [long] 0
        if (-not $complete -and $LicenseEventsShredded) {
            $unassembled = [long] (Invoke-Sql -Catalog $LicenseDatabase -Scalar -Sql @"
SELECT COUNT_BIG(*)
FROM (SELECT c.ChunkGroupId, MAX(CASE WHEN c.Seq = 1 THEN c.RecordId END) AS Carrier
      FROM audit.LicenseEventChunk AS c GROUP BY c.ChunkGroupId HAVING MIN(c.Seq) = 1 AND MAX(c.Seq) = COUNT(*)) AS g
WHERE NOT EXISTS (SELECT 1 FROM audit.LicenseEventProperty AS p WHERE p.RecordId = g.Carrier AND p.Source = 'chunks')
  AND EXISTS (SELECT 1
              FROM (SELECT STRING_AGG(CAST(b.Body AS NVARCHAR(MAX)), N'') WITHIN GROUP (ORDER BY b.Seq) AS Payload
                    FROM audit.LicenseEventChunk AS b WHERE b.ChunkGroupId = g.ChunkGroupId) AS w
              CROSS APPLY OPENJSON(CASE WHEN ISJSON(w.Payload) = 1 THEN w.Payload ELSE N'{}' END)
                   WITH (Tup NVARCHAR(MAX) '`$.targetUpdatedProperties') AS d
              CROSS APPLY OPENJSON(CASE WHEN ISJSON(d.Tup) = 1 THEN d.Tup ELSE N'[]' END) AS item);
"@)
        }
        $why = if ($Rebuild) { '-Rebuild' }
               elseif ($unassembled -gt 0) { '{0:N0} chunk group(s) were never reassembled, so a shred did stop part way: -LicenseEventsShredded is not taken' -f $unassembled }
               elseif (-not $complete -and -not $LicenseEventsShredded) {
                   if ($unshredded -gt 0) { '{0:N0} not shredded yet' -f $unshredded }
                   else { 'once more, to record that every one is shredded: later runs skip this, and -LicenseEventsShredded skips it now' }
               }
               else { $null }
        if ($why) {
            Write-Done ("license events: shredding {0:N0} record(s) ({1})..." -f $licRecords, $why)
            Invoke-Sql -Catalog $LicenseDatabase -Sql 'EXEC audit.usp_ShredLicenseEvents;'
        }
        else {
            if ($unshredded -gt 0) {
                # Every record is shredded but these, which arrived since: from the earliest of them on (a second
                # before it, as the shred counts in whole seconds). Where a stopped shred's mark says to go further
                # back, the procedure does.
                $from = [string] (Invoke-Sql -Catalog $LicenseDatabase -Scalar -Sql @"
SELECT CONVERT(NVARCHAR(19), DATEADD(SECOND, -1, MIN(a.CreatedDateTime)), 126) FROM audit.CopilotAudit AS a
WHERE a.Operation NOT LIKE N'Bot%' AND NOT EXISTS (SELECT 1 FROM audit.LicenseEvent AS e WHERE e.RecordId = a.RecordId);
"@)
                Write-Done ("license events: shredding the records from {0}Z on ({1:N0} not shredded yet)..." -f $from.Replace('T', ' '), $unshredded)
                Invoke-Sql -Catalog $LicenseDatabase -Sql 'EXEC audit.usp_ShredLicenseEvents @SinceUtc = @s;' -Parameters @{ '@s' = $from }
            }
            elseif ($stopped) {
                # From no date of its own: the procedure reads the mark, and shreds from where the stopped one began.
                Write-Done 'license events: an earlier shred did not finish, so what it was shredding is shredded again...'
                Invoke-Sql -Catalog $LicenseDatabase -Sql "EXEC audit.usp_ShredLicenseEvents @SinceUtc = '9999-12-31';"
            }
            else { Write-Done ("license events: every one of {0:N0} record(s) is shredded already, so none is read again (-Rebuild reads them all)." -f $licRecords) }
            # Not when the shred just above was one of every record: that leaves the mark itself.
            if (-not $complete -and [int] (Invoke-Sql -Catalog $LicenseDatabase -Scalar -Sql "SELECT COUNT(*) FROM audit.PipelineSetting WHERE Name = N'LicenseShredCompleteUtc';") -eq 0) {
                Invoke-Sql -Catalog $LicenseDatabase -Sql "INSERT INTO audit.PipelineSetting (Name, Value) VALUES (N'LicenseShredCompleteUtc', CONVERT(NVARCHAR(30), SYSUTCDATETIME(), 126));"
                Write-Done 'license events: recorded as all shredded by an earlier run that finished (-LicenseEventsShredded).'
            }
        }
        if ($Rebuild -or $unshreddedAgents -gt 0) { Invoke-Sql -Catalog $LicenseDatabase -Sql 'EXEC audit.usp_ShredAgentEvents;' }
    }
    else { $todo += "Backfill the license history into $LicenseDatabase (deployment step 7), then run this again." }
    # 2.5.4: the SKU changes sessions and bi read are worked out at the end of each shred. Events written since some
    # other way (by hand, or by a shred of a build before the marks above) are worked out here.
    if ([int] (Invoke-Sql -Catalog $LicenseDatabase -Scalar -Sql 'SELECT CASE WHEN EXISTS (SELECT 1 FROM audit.LicenseEvent WHERE ShreddedUtc > (SELECT MAX(StartedUtc) FROM audit.LicenseSkuChangeRun)) THEN 1 ELSE 0 END;') -eq 1) {
        Write-Done 'license SKU changes: working out those of the events shredded since they were last worked out...'
        Invoke-Sql -Catalog $LicenseDatabase -Sql 'EXEC audit.usp_RefreshLicenseSkuChange;'
    }
}

# Flattened by a version before 2.2.0: rows, but no record shapes - only the 2.2.0
# flatten counts them. Upgraded scripts alone do not fill history; this does, once.
$upgrade = $flattened -gt 0 -and (Get-Count $Database 'audit.RawLogRecordShape') -eq 0
# 2.4.12: built is a flatten of every record that finished, which leaves a mark (FullFlattenUtc). Any flattened row
# counted as built, so a first flatten stopped part way, or a nightly that flattened only its own few days, left the
# rest of history unflattened for good. A database flattened before 2.4.12 has no mark: it is given one here when every
# record is in the flattened table (counted once, minutes at 40 million), and flattened again when some are not.
$unflattened = [long] 0
if ($records -gt 0 -and $flattened -gt 0 -and -not $Rebuild -and -not $upgrade -and
    [long] (Invoke-Sql -Catalog $Database -Scalar -Sql "SELECT COUNT(*) FROM audit.PipelineSetting WHERE Name = N'FullFlattenUtc';") -eq 0) {
    Write-Done 'checking that every record has been flattened (once)...'
    $unflattened = [long] (Invoke-Sql -Catalog $Database -Scalar -Sql @"
SELECT COUNT_BIG(*) FROM audit.CopilotAudit AS a
WHERE NOT EXISTS (SELECT 1 FROM audit.RawLogFlattened AS f WHERE f.RecordId = a.RecordId);
"@)
    if ($unflattened -eq 0) {
        Invoke-Sql -Catalog $Database -Sql "INSERT INTO audit.PipelineSetting (Name, Value) VALUES (N'FullFlattenUtc', CONVERT(NVARCHAR(30), SYSUTCDATETIME(), 126));"
    }
}
$fullBuild = $Rebuild -or ($records -gt 0 -and $flattened -eq 0) -or $upgrade -or $unflattened -gt 0
if ($records -eq 0) {
    $todo += "Load Copilot records into $Database (the backfill, deployment step 6, or a CSV import), then run this again."
}
elseif ($fullBuild) {
    $why = if ($upgrade) { 'the flattened table gained columns (2.2.0), so every record is flattened again' } elseif ($Rebuild) { '-Rebuild' }
           elseif ($unflattened -gt 0) { '{0:N0} were never flattened: an earlier flatten stopped part way, or only the nightly''s days were' -f $unflattened } else { 'first build' }
    Write-Done ("flattening {0:N0} record(s) ({1})..." -f $records, $why)
    Invoke-Sql -Catalog $Database -Sql 'EXEC audit.usp_FlattenCopilotAudit;'
    Write-Done 'sessions, in full...'
    Invoke-Sql -Catalog $Database -Sql 'EXEC audit.usp_BuildCopilotSessions @LicenseEventDatabase = @l;' -Parameters @{ '@l' = $licList }
    Write-Done 'bi, in full...'
    Invoke-Sql -Catalog $Database -Sql 'EXEC bi.usp_RefreshBi @LicenseEventDatabase = @l;' -Parameters @{ '@l' = $licList }
    if ($upgrade) { $todo += 'Refresh the Power BI model in full once. The ready-made model in powerbi\CopilotInsights has every bi view (docs/DEPLOYMENT.md, step 15).' }
}
else { Write-Done 'sessions and bi are built; the nightly -Incremental run keeps them current.' }

# 2.4.11: the flatten reads an agent's id and friendly name where the audit log puts them, at the top of the record.
# Records flattened before have neither. They are read here once, from the records of agents only (minutes, where a
# re-flatten takes hours), decided from the mark the read leaves, so an install stopped part way reads them the next time.
if ($flattened -gt 0 -and -not $fullBuild -and (Test-Object $Database 'audit.usp_BackfillAgentNames' 'P') -and
    [long] (Invoke-Sql -Catalog $Database -Scalar -Sql "SELECT COUNT(*) FROM audit.PipelineSetting WHERE Name = N'AgentNamesReadUtc';") -eq 0) {
    Write-Done 'agent names: reading the id and friendly name from the records of agents, once (2.4.11)...'
    Invoke-Sql -Catalog $Database -Sql 'EXEC audit.usp_BackfillAgentNames;'
    if (Test-Object $Database 'bi.usp_NameAgents' 'P') { Invoke-Sql -Catalog $Database -Sql 'EXEC bi.usp_NameAgents;' }
    $todo += 'Refresh the Power BI model in full once (docs/DEPLOYMENT.md, step 16): agents carry the friendly names the audit log gives them, and bi.Agents gained NamedInAudit.'
}

# 2.4.12: the records of other apps that carry an agent's id (Copilot Chat running a declarative agent), which bi keys
# on that id. 2.4.11 read it from its upgrade night on, so the same agent's history started then. Read once here, from
# the apps whose records since show one; a record that gains an id changes agent, so bi is rebuilt once (below).
$agentIds = $false
if ($flattened -gt 0 -and -not $fullBuild -and (Test-Object $Database 'audit.usp_BackfillAgentNames' 'P') -and
    [long] (Invoke-Sql -Catalog $Database -Scalar -Sql "SELECT COUNT(*) FROM audit.PipelineSetting WHERE Name = N'AgentIdsReadUtc';") -eq 0) {
    Write-Done 'agent ids: reading them from the records of other apps that carry them, once (2.4.12)...'
    $gainedIds = [long] (Invoke-Sql -Catalog $Database -Scalar -Sql 'EXEC audit.usp_BackfillAgentNames @Others = 1;')
    if ($gainedIds -gt 0) {
        $agentIds = $true
        if (Test-Object $Database 'bi.usp_NameAgents' 'P') { Invoke-Sql -Catalog $Database -Sql 'EXEC bi.usp_NameAgents;' }
    }
}

# 2.4.6: each resource Copilot read carries its file type. Resources loaded before have none, so bi is refreshed in full
# once - decided from the data, not from this run, so an install stopped part way finishes it the next time.
# 2.4.8: each interaction carries what its record says about licensed use (UseKind), which Premium use is now decided
# from. Interactions loaded before have none: the same one full refresh, and every day of the rollups after it.
$noFileType = $flattened -gt 0 -and -not $fullBuild -and (Get-Count $Database 'bi.FactAccessedResource') -gt 0 -and
    [long] (Invoke-Sql -Catalog $Database -Scalar -Sql 'SELECT CASE WHEN EXISTS (SELECT 1 FROM bi.FactAccessedResource WHERE FileTypeKey > 1) THEN 1 ELSE 0 END;') -eq 0
$noUseKind = $flattened -gt 0 -and -not $fullBuild -and
    [long] (Invoke-Sql -Catalog $Database -Scalar -Sql 'SELECT CASE WHEN EXISTS (SELECT 1 FROM bi.FactInteraction WHERE UseKind IS NULL) THEN 1 ELSE 0 END;') -eq 1
# 2.4.9: a full refresh that was stopped part way (bi.usp_RefreshBi leaves a mark while one runs), or, for one stopped
# before 2.4.9, a fact with fewer than 99 in 100 of the flattened records: finished here, with every day of the rollups.
$cutShort = $flattened -gt 0 -and -not $fullBuild -and (Test-Object $Database 'bi.FactInteraction') -and
    ((Get-Count $Database 'bi.RebuildInProgress') -gt 0 -or (Get-Count $Database 'bi.FactInteraction') * 100 -lt $flattened * 99)
if ($noFileType -or $noUseKind -or $cutShort -or $agentIds) {
    $gain = @($(if ($noFileType) { 'the resources Copilot read gain their file type (2.4.6)' }), $(if ($noUseKind) { 'Premium use is decided from each record''s own identity (2.4.8)' }),
        $(if ($cutShort) { ('an earlier full refresh did not finish ({0:N0} of {1:N0} interactions are there)' -f (Get-Count $Database 'bi.FactInteraction'), $flattened) }),
        $(if ($agentIds) { ('{0:N0} interactions gain the agent the audit log names by id (2.4.12)' -f $gainedIds) })) | Where-Object { $_ }
    Write-Done "bi, in full once: $($gain -join '; '). This takes as long as a full bi refresh..."
    Invoke-Sql -Catalog $Database -Sql 'EXEC bi.usp_RefreshBi @LicenseEventDatabase = @l;' -Parameters @{ '@l' = $licList }
    if ($noFileType) { $todo += 'Refresh the Power BI model in full once (docs/DEPLOYMENT.md, step 16): bi.AccessedResources gained FileTypeKey, with names in the new bi.FileTypes.' }
    if ($agentIds) { $todo += 'Refresh the Power BI model in full once (docs/DEPLOYMENT.md, step 16): interactions of every day moved to the agents the audit log names by id, and a scheduled refresh redoes only the last 4 days.' }
}

# 2.3.0: the value catalog fills as records are flattened; records flattened before it existed are cataloged once here.
if ($flattened -gt 0 -and -not $fullBuild -and (Test-Object $Database 'audit.ValueCatalog') -and (Get-Count $Database 'audit.ValueCatalog') -eq 0) {
    Write-Done ("value catalog: reading {0:N0} flattened record(s) once..." -f $flattened)
    Invoke-Sql -Catalog $Database -Sql 'EXEC audit.usp_CatalogValues @Rebuild = 1;'
}

if ($Dashboard -and (Test-Object $Database 'bi.FactInteraction')) {
    # 2.3.0 added Premium-feature use to the rollups: rebuilt in full once, when they have days but none of it.
    $premiumEmpty = (Get-Count $Database 'dash.UserDay') -gt 0 -and
        [long] (Invoke-Sql -Catalog $Database -Scalar -Sql 'SELECT CASE WHEN EXISTS (SELECT 1 FROM dash.UserDay WHERE PremiumUse > 0) OR NOT EXISTS (SELECT 1 FROM dash.UserDay WHERE LicensedUse > 0) THEN 1 ELSE 0 END;') -eq 0
    $since = if ($fullBuild -or $premiumEmpty -or $noUseKind -or $cutShort -or $agentIds -or (Get-Count $Database 'dash.UserDay') -eq 0) { $null } else { [datetime]::UtcNow.Date }
    Write-Done $(if ($since) { 'dashboard: org chart, and today''s rollups...' } else { 'dashboard: org chart and every day''s rollups...' })
    Invoke-Sql -Catalog $Database -Sql 'EXEC dash.usp_RefreshDashboard @SinceUtc = @s;' -Parameters @{ '@s' = $since }
    if ((Get-Count $Database 'audit.UserDirectory') -eq 0) { $todo += 'Load the user directory from AD (deployment step 9): the dashboard''s scopes come from it.' }
}
if ((Get-Count $Database 'audit.CopilotLicense') -eq 0 -and $records -gt 0) { $todo += 'Load current licenses (deployment step 10, tools\Update-CopilotAuditEnrichment.ps1).' }

}
finally { if ($runLock) { $runLock.Dispose() } }

# ---- 4. Summary -------------------------------------------------------------------------------
Write-Host ''
Write-Host 'Done.' -ForegroundColor Green
if ($todo) { Write-Host 'Still to do:'; $todo | ForEach-Object { Write-Host "  - $_" } }
```

### C.2 `sql/Create-CopilotAuditSchema.sql`

Both databases. Loading state and staging, the pipeline's settings and run log, and the shared function that reads an interaction out of the record JSON. 240 lines.

```sql
/*
    Copilot audit backfill - schema
    ------------------------------------------------------------------------
    Idempotent. Invoke-CopilotAuditBackfill.ps1 runs this automatically on
    every start; it is provided separately so a DBA can review or pre-create
    the objects. Single batch (no GO) so it runs the same from SSMS or code.

    Requires SQL Server 2016+. The finalize step (columnstore over
    NVARCHAR(MAX)) requires 2017+.

    Space: AuditData arrives gzip-compressed (AuditDataGz). Microsoft's
    documented sample records compress to ~1.1-1.3 KB each (2.5-9 KB raw), so
    plan ~1.5 KB per record in staging - about 75 GB for 50M records, versus
    125-450 GB uncompressed. The run summary prints the actual ratio. Drop the
    staging table once the finalized tables are verified.
*/
SET NOCOUNT ON;

IF SCHEMA_ID(N'audit') IS NULL
    EXEC (N'CREATE SCHEMA audit AUTHORIZATION dbo;');

/* The requested range. Finalize refuses to run until it is fully covered. */
IF OBJECT_ID(N'audit.CopilotBackfillRange', N'U') IS NULL
    CREATE TABLE audit.CopilotBackfillRange
    (
        Id              TINYINT         NOT NULL CONSTRAINT PK_CopilotBackfillRange PRIMARY KEY
                                                 CONSTRAINT CK_CopilotBackfillRange_Single CHECK (Id = 1),
        RangeStartUtc   DATETIME2(0)    NOT NULL,
        RangeEndUtc     DATETIME2(0)    NOT NULL,
        Operations      NVARCHAR(400)   NOT NULL,
        StartedUtc      DATETIME2(0)    NOT NULL CONSTRAINT DF_CopilotBackfillRange_Started DEFAULT (SYSUTCDATETIME()),
        CompletedUtc    DATETIME2(0)    NULL
    );

/* One row per time window: the backfill's durable state. Resume reads this. */
IF OBJECT_ID(N'audit.CopilotBackfillWindow', N'U') IS NULL
    CREATE TABLE audit.CopilotBackfillWindow
    (
        WindowId        BIGINT IDENTITY(1,1) NOT NULL
                            CONSTRAINT PK_CopilotBackfillWindow PRIMARY KEY CLUSTERED,
        ParentWindowId  BIGINT          NULL,       -- set when a window was split
        StartUtc        DATETIME2(0)    NOT NULL,
        EndUtc          DATETIME2(0)    NOT NULL,
        Status          VARCHAR(16)     NOT NULL,   -- Pending|Submitted|Ready|Downloading|Loaded|Split|Failed
        QueryId         VARCHAR(64)     NULL,       -- Graph auditLogQuery id
        Attempts        INT             NOT NULL CONSTRAINT DF_CopilotBackfillWindow_Attempts    DEFAULT (0),
        Submissions     INT             NOT NULL CONSTRAINT DF_CopilotBackfillWindow_Submissions DEFAULT (0),
        ApproxRecords   BIGINT          NULL,       -- service's approximateReturnedRecordCount
        RowsLoaded      BIGINT          NULL,
        SubmittedUtc    DATETIME2(0)    NULL,
        CompletedUtc    DATETIME2(0)    NULL,
        LastError       NVARCHAR(2000)  NULL,
        CreatedUtc      DATETIME2(0)    NOT NULL CONSTRAINT DF_CopilotBackfillWindow_Created DEFAULT (SYSUTCDATETIME()),
        UpdatedUtc      DATETIME2(0)    NOT NULL CONSTRAINT DF_CopilotBackfillWindow_Updated DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT CK_CopilotBackfillWindow_Range CHECK (EndUtc > StartUtc)
    );

/*
    Landing table. A HEAP on purpose: SqlBulkCopy with TABLOCK takes
    bulk-update (BU) locks on a heap, so several writers load it concurrently
    with minimal logging. Any index here would serialize them.

    Every row carries (WindowId, Attempt). A window interrupted mid-download
    is re-downloaded under a new Attempt number; finalize keeps only each
    Loaded window's final attempt, so partial loads never double-count.

    Column lengths must match RowSchema.MaxLength in CopilotAuditEngine.cs.
*/
IF OBJECT_ID(N'audit.CopilotAuditStage', N'U') IS NULL
    CREATE TABLE audit.CopilotAuditStage
    (
        WindowId            BIGINT          NOT NULL,
        Attempt             INT             NOT NULL,
        RecordId            VARCHAR(64)     NOT NULL,
        CreatedDateTime     DATETIME2(7)    NOT NULL,
        RecordType          VARCHAR(128)    NULL,
        Operation           VARCHAR(128)    NULL,
        OrganizationId      VARCHAR(64)     NULL,
        UserType            VARCHAR(64)     NULL,
        UserId              NVARCHAR(512)   NULL,
        UserPrincipalName   NVARCHAR(512)   NULL,
        Service             VARCHAR(128)    NULL,
        ObjectId            NVARCHAR(4000)  NULL,
        ClientIp            VARCHAR(128)    NULL,
        AdministrativeUnits NVARCHAR(4000)  NULL,
        AuditDataGz         VARBINARY(MAX)  NULL,   -- gzip of the full audit record JSON (UTF-16):
                                                    --   CAST(DECOMPRESS(AuditDataGz) AS NVARCHAR(MAX))
        LoadedUtc           DATETIME2(0)    NOT NULL CONSTRAINT DF_CopilotAuditStage_Loaded DEFAULT (SYSUTCDATETIME())
    );

/* ---- Incremental loads (-Incremental) ------------------------------------ */
/*
    The backfill covers one fixed range. Keeping the tables current afterwards
    is a series of small loads, each from the previous load's end - less a
    lookback, because Purview can deliver records hours after the event - up
    to the current hour. A load runs the same engine against its own window and
    staging tables, which are emptied once audit.usp_MergeCopilotLoad has
    appended the load's new records to audit.CopilotAudit. One row per load.
*/
IF OBJECT_ID(N'audit.CopilotLoad', N'U') IS NULL
    CREATE TABLE audit.CopilotLoad
    (
        LoadId            INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_CopilotLoad PRIMARY KEY,
        RangeStartUtc     DATETIME2(0)    NOT NULL,
        RangeEndUtc       DATETIME2(0)    NOT NULL,
        Operations        NVARCHAR(400)   NOT NULL,
        StartedUtc        DATETIME2(0)    NOT NULL CONSTRAINT DF_CopilotLoad_Started DEFAULT (SYSUTCDATETIME()),
        DownloadedUtc     DATETIME2(0)    NULL,     -- every window Loaded; ready to merge
        MergedUtc         DATETIME2(0)    NULL,     -- appended to audit.CopilotAudit
        RowsStaged        BIGINT          NULL,     -- final-attempt rows in the load
        RowsAdded         BIGINT          NULL,     -- new to audit.CopilotAudit
        RowsAlreadyLoaded BIGINT          NULL,     -- in the lookback, already present
        CONSTRAINT CK_CopilotLoad_Range CHECK (RangeEndUtc > RangeStartUtc)
    );

/* Same shape as audit.CopilotBackfillWindow: the engine's state for the load in progress. */
IF OBJECT_ID(N'audit.CopilotLoadWindow', N'U') IS NULL
    CREATE TABLE audit.CopilotLoadWindow
    (
        WindowId        BIGINT IDENTITY(1,1) NOT NULL
                            CONSTRAINT PK_CopilotLoadWindow PRIMARY KEY CLUSTERED,
        ParentWindowId  BIGINT          NULL,
        StartUtc        DATETIME2(0)    NOT NULL,
        EndUtc          DATETIME2(0)    NOT NULL,
        Status          VARCHAR(16)     NOT NULL,
        QueryId         VARCHAR(64)     NULL,
        Attempts        INT             NOT NULL CONSTRAINT DF_CopilotLoadWindow_Attempts    DEFAULT (0),
        Submissions     INT             NOT NULL CONSTRAINT DF_CopilotLoadWindow_Submissions DEFAULT (0),
        ApproxRecords   BIGINT          NULL,
        RowsLoaded      BIGINT          NULL,
        SubmittedUtc    DATETIME2(0)    NULL,
        CompletedUtc    DATETIME2(0)    NULL,
        LastError       NVARCHAR(2000)  NULL,
        CreatedUtc      DATETIME2(0)    NOT NULL CONSTRAINT DF_CopilotLoadWindow_Created DEFAULT (SYSUTCDATETIME()),
        UpdatedUtc      DATETIME2(0)    NOT NULL CONSTRAINT DF_CopilotLoadWindow_Updated DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT CK_CopilotLoadWindow_Range CHECK (EndUtc > StartUtc)
    );

/* Same shape as audit.CopilotAuditStage, and a heap for the same reason. */
IF OBJECT_ID(N'audit.CopilotLoadStage', N'U') IS NULL
    CREATE TABLE audit.CopilotLoadStage
    (
        WindowId            BIGINT          NOT NULL,
        Attempt             INT             NOT NULL,
        RecordId            VARCHAR(64)     NOT NULL,
        CreatedDateTime     DATETIME2(7)    NOT NULL,
        RecordType          VARCHAR(128)    NULL,
        Operation           VARCHAR(128)    NULL,
        OrganizationId      VARCHAR(64)     NULL,
        UserType            VARCHAR(64)     NULL,
        UserId              NVARCHAR(512)   NULL,
        UserPrincipalName   NVARCHAR(512)   NULL,
        Service             VARCHAR(128)    NULL,
        ObjectId            NVARCHAR(4000)  NULL,
        ClientIp            VARCHAR(128)    NULL,
        AdministrativeUnits NVARCHAR(4000)  NULL,
        AuditDataGz         VARBINARY(MAX)  NULL,
        LoadedUtc           DATETIME2(0)    NOT NULL CONSTRAINT DF_CopilotLoadStage_Loaded DEFAULT (SYSUTCDATETIME())
    );

/*
    audit.fnShredCopilotInteraction - one audit record's JSON to the columns
    of audit.CopilotInteraction. Finalize-CopilotAudit.sql, usp_MergeCopilotLoad
    and usp_MergeCopilotCsv all shred through it, so a record reads the same
    whichever path loaded it. Inline, so it costs what the expressions would
    written out in place. JsonUserId is the UPN inside the record, for rows
    that arrived without one.
*/
EXEC (N'
CREATE OR ALTER FUNCTION audit.fnShredCopilotInteraction (@AuditDataGz VARBINARY(MAX))
RETURNS TABLE
AS
RETURN
SELECT
    LEFT(JSON_VALUE(j.Doc, ''$.UserId''), 512)                                                   AS JsonUserId,
    LEFT(JSON_VALUE(j.Doc, ''$.CopilotEventData.AppHost''), 100)                                  AS AppHost,
    LEFT(JSON_VALUE(j.Doc, ''$.AppIdentity''), 200)                                               AS AppIdentity,
    LEFT(JSON_VALUE(j.Doc, ''$.ClientRegion''), 20)                                               AS ClientRegion,
    LEFT(JSON_VALUE(j.Doc, ''$.CopilotEventData.ModelTransparencyDetails[0].ModelName''), 200)    AS ModelName,
    LEFT(JSON_VALUE(j.Doc, ''$.CopilotEventData.ModelTransparencyDetails[0].ModelProviderName''), 200) AS ModelProvider,
    LEFT(JSON_VALUE(j.Doc, ''$.CopilotEventData.ThreadId''), 400)                                 AS ThreadId,
    (SELECT COUNT(*) FROM OPENJSON(j.Doc, ''$.CopilotEventData.Messages'') WITH (isPrompt BIT ''$.isPrompt'') AS m
      WHERE m.isPrompt = 1)                                                                        AS PromptCount,
    (SELECT COUNT(*) FROM OPENJSON(j.Doc, ''$.CopilotEventData.AccessedResources''))              AS AccessedResourceCount,
    CAST(CASE WHEN EXISTS (SELECT 1 FROM OPENJSON(j.Doc, ''$.CopilotEventData.AISystemPlugin'') WITH (Id NVARCHAR(200) ''$.Id'') AS p
                           WHERE p.Id = N''BingWebSearch'') THEN 1 ELSE 0 END AS BIT)            AS UsedWebSearch
FROM (SELECT CAST(DECOMPRESS(@AuditDataGz) AS NVARCHAR(MAX)) AS Raw) AS r
CROSS APPLY (SELECT CASE WHEN ISJSON(r.Raw) = 1 THEN r.Raw END AS Doc) AS j;');

/*  Pipeline settings: the reporting time zone, set once by
    Install-CopilotAudit.ps1 -ReportingTimeZone before the first load. Every
    procedure that cuts days reads it through audit.ReportingTimeZone() when
    no zone is passed; with no row it is Eastern Standard Time. */
IF OBJECT_ID(N'audit.PipelineSetting', N'U') IS NULL
    CREATE TABLE audit.PipelineSetting
    (
        Name       SYSNAME         NOT NULL CONSTRAINT PK_PipelineSetting PRIMARY KEY,
        Value      NVARCHAR(400)   NULL,
        UpdatedUtc DATETIME2(0)    NOT NULL CONSTRAINT DF_PipelineSetting_Updated DEFAULT (SYSUTCDATETIME()),
        UpdatedBy  NVARCHAR(256)   NOT NULL CONSTRAINT DF_PipelineSetting_By DEFAULT (SUSER_SNAME())
    );
EXEC (N'CREATE OR ALTER FUNCTION audit.ReportingTimeZone() RETURNS SYSNAME
AS
BEGIN
    RETURN COALESCE((SELECT Value FROM audit.PipelineSetting WHERE Name = N''ReportingTimeZone''), N''Eastern Standard Time'');
END;');

/*  Pipeline runs: one row per run of Invoke-CopilotPipeline.ps1 and one per
    step, written by the runner as it goes. Admin > Health shows them, and the
    dashboard sends a pipeline alert when a run fails or none finishes. A row
    left Running means the run died without saying so (a reboot). */
IF OBJECT_ID(N'audit.PipelineRun', N'U') IS NULL
    CREATE TABLE audit.PipelineRun
    (
        RunId      INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_PipelineRun PRIMARY KEY,
        Kind       VARCHAR(16)     NOT NULL,   -- Nightly | History
        StartedUtc DATETIME2(0)    NOT NULL CONSTRAINT DF_PipelineRun_Started DEFAULT (SYSUTCDATETIME()),
        EndedUtc   DATETIME2(0)    NULL,
        Status     VARCHAR(16)     NOT NULL CONSTRAINT DF_PipelineRun_Status DEFAULT ('Running'),   -- Running | Succeeded | Failed
        FailedStep NVARCHAR(200)   NULL,
        Detail     NVARCHAR(4000)  NULL,       -- the failed step's last lines
        Host       NVARCHAR(128)   NULL,
        RunAs      NVARCHAR(256)   NULL
    );
IF OBJECT_ID(N'audit.PipelineRunStep', N'U') IS NULL
    CREATE TABLE audit.PipelineRunStep
    (
        RunId      INT             NOT NULL,
        StepNo     TINYINT         NOT NULL,
        Name       NVARCHAR(200)   NOT NULL,
        StartedUtc DATETIME2(0)    NOT NULL CONSTRAINT DF_PipelineRunStep_Started DEFAULT (SYSUTCDATETIME()),
        EndedUtc   DATETIME2(0)    NULL,
        ExitCode   INT             NULL,
        CONSTRAINT PK_PipelineRunStep PRIMARY KEY (RunId, StepNo)
    );

/* The app certificate each run signed in with, and when it expires: the dashboard warns 30, 14
   and 3 days before (2.3.0). */
IF COL_LENGTH(N'audit.PipelineRun', N'CertificateThumbprint') IS NULL
    ALTER TABLE audit.PipelineRun ADD CertificateThumbprint VARCHAR(64) NULL, CertificateExpiresUtc DATETIME2(0) NULL, CertificateSubject NVARCHAR(400) NULL;
```

### C.3 `sql/Create-CopilotLoadProcedure.sql`

Both databases. `audit.usp_MergeCopilotLoad`, which appends each nightly load to the final records. 242 lines.

```sql
/*
    audit.usp_MergeCopilotLoad
    ------------------------------------------------------------------------
    Appends an incremental load (Invoke-CopilotAuditBackfill.ps1 -Incremental)
    to the finalized tables, instead of rebuilding them.

    Finalize-CopilotAudit.sql builds audit.CopilotAudit and
    audit.CopilotInteraction from scratch, which is right once, for the
    backfill, and wrong every day after: it rebuilds everything and knows only
    the backfill's range. This procedure takes the one load that has finished
    downloading and:

      1. Refuses unless every window of the load is Loaded and together they
         cover its range with no gap - the same checks finalize makes.
      2. Keeps each window's final attempt only, one row per RecordId.
      3. Drops the records audit.CopilotAudit already holds. Every load
         re-searches a lookback before its start, because Purview can deliver a
         record hours after the event; the records that were already there are
         counted, not added again.
      4. Appends the rest to audit.CopilotAudit (with LoadId for lineage) and
         their shredded rows to audit.CopilotInteraction, and marks the load
         merged, with how many audit searches it took - all in one transaction.
      5. Empties the load's window and staging tables for the next load.

    Re-runnable: with nothing waiting it reports so and returns. It never
    touches rows from the backfill or from earlier loads.

    One merge at a time: steps 1 to 4 hold an exclusive lock that
    audit.usp_MergeCopilotCsv takes too, so two merges never both find a
    record missing and both append it.

    Step 4 shreds through audit.fnShredCopilotInteraction, as finalize and CSV
    imports do, so a record reads the same whichever path loaded it.

    Requires SQL Server 2017+. Run sql/Create-CopilotAuditSchema.sql first (the
    orchestrator does), and finalize the backfill before the first load.
*/
SET NOCOUNT ON;

/* When the derived tables were brought up to date with a merged load (2.4.12).
   A load is marked merged before -Incremental refreshes the flattened table,
   sessions, bi and the dashboard from its start, and nothing recorded whether
   that refresh finished: the next load refreshed from its own start only, so
   after a few nights of a refresh failing, or one long catch-up load failing,
   the days between were never refreshed. NULL on a merged load is a refresh
   still owed, and -Incremental makes it up from the earliest one. Loads merged
   before the column existed are taken as refreshed. */
IF OBJECT_ID(N'audit.CopilotLoad', N'U') IS NOT NULL AND COL_LENGTH(N'audit.CopilotLoad', N'DerivedRefreshedUtc') IS NULL
BEGIN
    ALTER TABLE audit.CopilotLoad ADD DerivedRefreshedUtc DATETIME2(0) NULL;
    EXEC (N'UPDATE audit.CopilotLoad SET DerivedRefreshedUtc = MergedUtc WHERE MergedUtc IS NOT NULL;');
END;

/* How many audit searches a load took, and how many of them Microsoft Purview had to be asked for more than once
   (after 2.5.4). A load's windows are emptied once it is merged, so nothing was left to say what a night that
   finished had asked of the service: Admin > Health read "0 submitted" after every run that succeeded. NULL on a
   load merged before the columns existed. */
IF OBJECT_ID(N'audit.CopilotLoad', N'U') IS NOT NULL AND COL_LENGTH(N'audit.CopilotLoad', N'Searches') IS NULL
    ALTER TABLE audit.CopilotLoad ADD Searches INT NULL, SearchesResubmitted INT NULL;

IF OBJECT_ID(N'audit.usp_MergeCopilotLoad', N'P') IS NULL
    EXEC (N'CREATE PROCEDURE audit.usp_MergeCopilotLoad AS BEGIN SET NOCOUNT ON; END;');
GO

ALTER PROCEDURE audit.usp_MergeCopilotLoad
    @ReportingTimeZone SYSNAME = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    SET @ReportingTimeZone = COALESCE(@ReportingTimeZone, audit.ReportingTimeZone());   -- the zone Install-CopilotAudit.ps1 -ReportingTimeZone stored

    DECLARE @msg NVARCHAR(600), @loadId INT, @start DATETIME2(0), @end DATETIME2(0), @downloaded DATETIME2(0), @lock INT;

    /* One merge into audit.CopilotAudit at a time, this one or a CSV import's
       (2.4.12). Each looked for the records already there and then appended
       the rest, with no lock between: two at once, a console run beside the
       nightly or an import beside it, each missed what the other was
       appending and stored it a second time, and every flatten over those
       days then failed on the duplicate. The lock belongs to the transaction
       and is taken before the load is chosen, so a second merge waits, then
       finds the load merged or sees the first one's records. */
    BEGIN TRANSACTION;
    EXEC @lock = sys.sp_getapplock @Resource = N'audit.CopilotAudit:append', @LockMode = N'Exclusive',
                                   @LockOwner = N'Transaction', @LockTimeout = -1;
    IF @lock < 0
    BEGIN
        ROLLBACK TRANSACTION;
        RAISERROR('Merge refused: the merge lock on audit.CopilotAudit was not granted (%d). Run the same -Incremental command again.', 16, 1, @lock);
        RETURN;
    END;

    SELECT TOP (1) @loadId = LoadId, @start = RangeStartUtc, @end = RangeEndUtc, @downloaded = DownloadedUtc
    FROM audit.CopilotLoad
    WHERE MergedUtc IS NULL
    ORDER BY LoadId;

    IF @loadId IS NULL
    BEGIN
        ROLLBACK TRANSACTION;
        RAISERROR('Nothing to merge: no incremental load is waiting.', 0, 1) WITH NOWAIT;
        RETURN;
    END;
    IF @downloaded IS NULL
    BEGIN
        ROLLBACK TRANSACTION;
        RAISERROR('Merge refused: load %d has not finished downloading. Re-run the same -Incremental command to finish it.', 16, 1, @loadId);
        RETURN;
    END;
    IF OBJECT_ID(N'audit.CopilotAudit', N'U') IS NULL OR OBJECT_ID(N'audit.CopilotInteraction', N'U') IS NULL
    BEGIN
        ROLLBACK TRANSACTION;
        RAISERROR('Merge refused: audit.CopilotAudit does not exist yet. Finalize the backfill (-FinalizeOnly) before the first incremental load.', 16, 1);
        RETURN;
    END;
    IF OBJECT_ID(N'audit.fnShredCopilotInteraction', N'IF') IS NULL
    BEGIN
        ROLLBACK TRANSACTION;
        RAISERROR('Merge refused: audit.fnShredCopilotInteraction is missing. Run sql/Create-CopilotAuditSchema.sql first (Invoke-CopilotAuditBackfill.ps1 does).', 16, 1);
        RETURN;
    END;

    /* ---- 1. Coverage: the same checks finalize makes ---------------------- */

    DECLARE @unfinished INT = (SELECT COUNT(*) FROM audit.CopilotLoadWindow WHERE Status NOT IN ('Loaded', 'Split'));
    IF @unfinished > 0
    BEGIN
        ROLLBACK TRANSACTION;
        RAISERROR('Merge refused: %d window(s) of load %d are not Loaded.', 16, 1, @unfinished, @loadId);
        RETURN;
    END;

    DECLARE @gaps INT;
    WITH w AS
    (
        SELECT StartUtc, LAG(EndUtc) OVER (ORDER BY StartUtc) AS PrevEnd
        FROM audit.CopilotLoadWindow
        WHERE Status = 'Loaded'
    )
    SELECT @gaps = COUNT(*) FROM w WHERE PrevEnd IS NOT NULL AND PrevEnd <> StartUtc;

    IF ISNULL((SELECT MIN(StartUtc) FROM audit.CopilotLoadWindow WHERE Status = 'Loaded'), '19000101') <> @start
       OR ISNULL((SELECT MAX(EndUtc) FROM audit.CopilotLoadWindow WHERE Status = 'Loaded'), '19000101') <> @end
        SET @gaps += 1;

    IF @gaps > 0
    BEGIN
        ROLLBACK TRANSACTION;
        RAISERROR('Merge refused: the Loaded windows of load %d have %d gap(s)/overlap(s). Inspect audit.CopilotLoadWindow.', 16, 1, @loadId, @gaps);
        RETURN;
    END;

    /* Lineage for rows appended by a load. Finalize creates the column; a
       database finalized by an earlier version gets it here. */
    IF COL_LENGTH(N'audit.CopilotAudit', N'LoadId') IS NULL
        EXEC (N'ALTER TABLE audit.CopilotAudit ADD LoadId INT NULL;');

    /* ---- 2. The load's records: final attempt, one per RecordId ----------- */

    DROP TABLE IF EXISTS #cand;
    SELECT s.RecordId, s.CreatedDateTime, s.RecordType, s.Operation, s.OrganizationId, s.UserType, s.UserId,
           s.UserPrincipalName, s.Service, s.ObjectId, s.ClientIp, s.AdministrativeUnits, s.AuditDataGz, s.WindowId,
           ROW_NUMBER() OVER (PARTITION BY s.RecordId ORDER BY s.WindowId DESC) AS rn
    INTO #cand
    FROM audit.CopilotLoadStage AS s
    JOIN audit.CopilotLoadWindow AS w
      ON  w.WindowId = s.WindowId
      AND w.Status   = 'Loaded'
      AND w.Attempts = s.Attempt;

    DELETE FROM #cand WHERE rn > 1;             -- a record on a window boundary comes back from both
    DECLARE @staged BIGINT = (SELECT COUNT_BIG(*) FROM #cand);

    /* ---- 3. Skip what is already there ------------------------------------ */
    /*
        A search returns records by creation time, so a record in this load can
        only already be in audit.CopilotAudit with a CreatedDateTime inside the
        load's range. Bounding the probe by it (with a day's margin) lets the
        columnstore skip every row group outside it.
    */
    CREATE CLUSTERED INDEX CIX_cand ON #cand (RecordId);

    DELETE c
    FROM #cand AS c
    WHERE EXISTS (SELECT 1 FROM audit.CopilotAudit AS a
                  WHERE a.RecordId = c.RecordId
                    AND a.CreatedDateTime >= DATEADD(DAY, -1, @start)
                    AND a.CreatedDateTime <  DATEADD(DAY,  1, @end));

    DECLARE @added BIGINT = (SELECT COUNT_BIG(*) FROM #cand);

    /* ---- 4. Append, in the same transaction -------------------------------- */

    INSERT INTO audit.CopilotAudit
        (RecordId, CreatedDateTime, RecordType, Operation, OrganizationId, UserType, UserId,
         UserPrincipalName, Service, ObjectId, ClientIp, AdministrativeUnits, AuditDataGz, WindowId, LoadId)
    SELECT c.RecordId, c.CreatedDateTime, c.RecordType, c.Operation, c.OrganizationId, c.UserType, c.UserId,
           c.UserPrincipalName, c.Service, c.ObjectId, c.ClientIp, c.AdministrativeUnits, c.AuditDataGz, c.WindowId, @loadId
    FROM #cand AS c;

    INSERT INTO audit.CopilotInteraction
        (RecordId, CreatedDateTime, ActivityDate, UserPrincipalName, AppHost, AppIdentity, ClientRegion,
         ModelName, ModelProvider, ThreadId, PromptCount, AccessedResourceCount, UsedWebSearch)
    SELECT
        c.RecordId,
        CAST(c.CreatedDateTime AS DATETIME2(0)),
        CAST((c.CreatedDateTime AT TIME ZONE 'UTC') AT TIME ZONE @ReportingTimeZone AS DATE),
        COALESCE(c.UserPrincipalName, s.JsonUserId),
        s.AppHost, s.AppIdentity, s.ClientRegion, s.ModelName, s.ModelProvider, s.ThreadId,
        s.PromptCount, s.AccessedResourceCount, s.UsedWebSearch
    FROM #cand AS c
    CROSS APPLY audit.fnShredCopilotInteraction(c.AuditDataGz) AS s;

    /* The searches the load took, kept with it: its windows are emptied below. One search per window, a window that
       was split included; resubmitted, where the service had to be asked for a window more than once. */
    DECLARE @searches INT, @resubmitted INT;
    SELECT @searches = COUNT(CASE WHEN Submissions > 0 THEN 1 END), @resubmitted = COUNT(CASE WHEN Submissions > 1 THEN 1 END)
    FROM audit.CopilotLoadWindow;

    UPDATE audit.CopilotLoad
    SET MergedUtc = SYSUTCDATETIME(), RowsStaged = @staged, RowsAdded = @added, RowsAlreadyLoaded = @staged - @added,
        Searches = @searches, SearchesResubmitted = @resubmitted
    WHERE LoadId = @loadId AND MergedUtc IS NULL;
    IF @@ROWCOUNT = 0   -- cannot happen under the lock; if it ever does, nothing is appended twice
        THROW 50120, N'Merge refused: the load was merged by another run meanwhile.', 1;

    COMMIT TRANSACTION;

    /* ---- 5. Ready for the next load ---------------------------------------- */

    TRUNCATE TABLE audit.CopilotLoadStage;
    TRUNCATE TABLE audit.CopilotLoadWindow;

    SET @msg = CONCAT(N'Load ', @loadId, N' merged: ', FORMAT(@staged, 'N0'), N' record(s) in ',
                      CONVERT(NVARCHAR(16), @start, 120), N'Z -> ', CONVERT(NVARCHAR(16), @end, 120), N'Z, ',
                      FORMAT(@added, 'N0'), N' new, ', FORMAT(@staged - @added, 'N0'), N' already loaded.');
    RAISERROR(@msg, 0, 1) WITH NOWAIT;

    SELECT @loadId AS LoadId, @start AS RangeStartUtc, @end AS RangeEndUtc,
           @staged AS RowsStaged, @added AS RowsAdded, @staged - @added AS RowsAlreadyLoaded;
END;
GO
```

### C.4 `sql/Create-CopilotFlattenedSchema.sql`

Copilot database. The flattened record tables and the value and agent catalogs. 360 lines.

```sql
/*
    Copilot audit backfill - flattened reporting table
    ------------------------------------------------------------------------
    One row per audit record, with the CopilotEventData JSON shredded into
    columns. Column names and types match dbo.RawLogFlattened so existing
    reports can point here unchanged.

    Populated by audit.usp_FlattenCopilotAudit (sql/Create-CopilotFlattenProcedure.sql),
    which reads audit.vCopilotAuditRecord - the Purview-export-shaped view. It
    is keyed on RecordId, so the multi-valued parts of the record (a Copilot
    interaction can touch many resources) are aggregated into '; '-delimited
    lists rather than exploded into extra rows. Every JSON path below is from
    Microsoft's published Copilot audit schema:
      https://learn.microsoft.com/purview/audit-copilot
      https://learn.microsoft.com/office/office-365-management-api/copilot-schema

    Idempotent. Single batch (no GO) so it runs the same from SSMS or code.
    Requires SQL Server 2017+ (STRING_AGG).
*/
SET NOCOUNT ON;

IF SCHEMA_ID(N'audit') IS NULL
    EXEC (N'CREATE SCHEMA audit AUTHORIZATION dbo;');

IF OBJECT_ID(N'audit.RawLogFlattened', N'U') IS NULL
    CREATE TABLE audit.RawLogFlattened
    (
        /* $.Id - the audit record id, one row per record.                      */
        RecordId                        NVARCHAR(450)   NOT NULL
                                            CONSTRAINT PK_RawLogFlattened_RecordId PRIMARY KEY,

        /* $.CreationTime, kept as text in the source's shape. UtcTimeStamp
           below is the same instant typed, for anyone who wants to filter.     */
        [TimeStamp]                     NVARCHAR(4000)  NULL,

        /* $.UserId                                                             */
        [User]                          NVARCHAR(4000)  NULL,

        /* $.CopilotEventData.AppHost - Word, Teams, BizChat, Outlook, Bing ... */
        App                             NVARCHAR(4000)  NULL,

        /* $.ClientRegion - the user's region when they performed the operation */
        Location                        NVARCHAR(MAX)   NULL,

        /* $.CopilotEventData.Contexts[*].Id - where the user was during the
           interaction (the open document, meeting, channel or chat).           */
        [App context]                   NVARCHAR(4000)  NULL,

        /* $.CopilotEventData.AccessedResources[*].Name                         */
        [Accessed Resources]            NVARCHAR(MAX)   NULL,

        /* $.CopilotEventData.AccessedResources[*].SiteUrl                      */
        [Accessed Resource Locations]   NVARCHAR(MAX)   NULL,

        /* $.CopilotEventData.AccessedResources[*].Action - Read, Create, ...   */
        [Action]                        NVARCHAR(MAX)   NULL,

        /* $.CopilotEventData.AISystemPlugin[*].Id - BingWebSearch means the
           interaction was grounded on the public web.                          */
        AIPlugin                        NVARCHAR(MAX)   NULL,

        /* $.CopilotEventData.Messages[*].JailbreakDetected - 'true'/'false'
           kept as text to match the source column.                             */
        JailbreakDetected               NVARCHAR(5)     NULL,

        /* $.CopilotEventData.AccessedResources[*].XPIADetected - cross prompt
           injection attack detected in an accessed resource.                   */
        PromptInjectionDetected         NVARCHAR(5)     NULL,

        /* SiteUrl of any accessed resource carrying PolicyDetails or a failed
           Status - Copilot's access to it was blocked or restricted.           */
        BlockedResourceUrls             NVARCHAR(MAX)   NULL,

        /* $.CopilotEventData.AccessedResources[*].SensitivityLabelId           */
        SensitivityLabelIds             NVARCHAR(MAX)   NULL,

        /* Whether the user held a Copilot license. Not in the audit record:
           filled from audit.CopilotLicense when that table exists (see the
           procedure), NULL otherwise.                                          */
        IsCopilotLic                    BIT             NULL,

        /* ---- Beyond the source table's columns, cheap to carry and hard to
           reconstruct later. Drop them if the shape has to match exactly. ---- */
        UtcTimeStamp                    DATETIME2(0)    NULL,   -- $.CreationTime, typed
        AppIdentity                     NVARCHAR(200)   NULL,   -- $.AppIdentity
        ThreadId                        NVARCHAR(400)   NULL,   -- $.CopilotEventData.ThreadId
        AgentId                         NVARCHAR(400)   NULL,   -- $.AgentId (2.4.11), else $.CopilotEventData.AgentId
        AgentName                       NVARCHAR(400)   NULL,   -- $.AgentName, the agent's friendly name (2.4.11), else $.CopilotEventData.AgentName
        ModelName                       NVARCHAR(200)   NULL,   -- ModelTransparencyDetails[0].ModelName
        ModelProvider                   NVARCHAR(200)   NULL,   -- ModelTransparencyDetails[0].ModelProviderName
        AccessedResourceCount           INT             NULL,
        PromptCount                     INT             NULL,
        FlattenedUtc                    DATETIME2(0)    NOT NULL
                                            CONSTRAINT DF_RawLogFlattened_Flattened DEFAULT (SYSUTCDATETIME())
    );

/*
    ---- Every other field of the record (2.2.0) --------------------------------

    Each scalar the Copilot audit record carries gets a typed column, named
    after its JSON path (CopilotEventData.X as CopilotEventData_X), except
    seventeen that carry no information (audit.RawLogRecordShape, below). Types come
    from a profile of 19 million production records: identifiers that were
    GUID text in every row are UNIQUEIDENTIFIER (16 bytes rather than 72), and
    a value that does not parse as the type is left NULL rather than failing
    the batch. The small arrays are kept as JSON text (NULL when empty); the
    two large ones, AccessedResources and Contexts, get a row per element in
    audit.RawLogAccessedResource and audit.RawLogContext below.

    Added in place, so a 2.1.0 table keeps its rows; the new columns stay NULL
    until those records are flattened again (EXEC audit.usp_FlattenCopilotAudit
    once, with no @SinceUtc).
*/
DECLARE @cols TABLE (Name SYSNAME, Def NVARCHAR(200));
INSERT INTO @cols (Name, Def) VALUES
    /* top-level scalars ($.X) */
    (N'OrganizationId',                          N'UNIQUEIDENTIFIER NULL'),
    (N'UserKey',                                 N'UNIQUEIDENTIFIER NULL'),
    (N'ClientIP',                                N'VARCHAR(64) NULL'),
    (N'ApplicationName',                         N'NVARCHAR(200) NULL'),
    (N'PlatformAgentId',                         N'VARCHAR(256) NULL'),
    (N'ResultStatus',                            N'VARCHAR(64) NULL'),
    (N'AppAccessContext_APIId',                  N'VARCHAR(64) NULL'),
    /* $.CopilotEventData.X */
    (N'CopilotEventData_LicenseType',            N'VARCHAR(32) NULL'),
    (N'CopilotEventData_ConversationId',         N'VARCHAR(256) NULL'),
    (N'CopilotEventData_CorrelationId',          N'UNIQUEIDENTIFIER NULL'),
    (N'CopilotEventData_SensitivityLabelId',     N'UNIQUEIDENTIFIER NULL'),
    (N'CopilotEventData_TargetAgentName',        N'NVARCHAR(200) NULL'),
    (N'CopilotEventData_TargetPlatformAgentId',  N'VARCHAR(256) NULL'),
    (N'CopilotEventData_DLPEvaluationDeferred',  N'TINYINT NULL'),
    (N'CopilotEventData_MemoryUpdated',          N'BIT NULL'),
    (N'CopilotEventData_MemoryCorrelationId',    N'VARCHAR(64) NULL'),
    (N'CopilotEventData_FabricCapacityId',       N'UNIQUEIDENTIFIER NULL'),
    (N'CopilotEventData_PlatformAgentId',        N'VARCHAR(256) NULL'),
    (N'CopilotEventData_TaskId',                 N'UNIQUEIDENTIFIER NULL'),
    (N'ModelVersion',                            N'VARCHAR(32) NULL'),        -- ModelTransparencyDetails[0].ModelVersion
    (N'MessageCount',                            N'INT NULL'),                -- Messages[*], prompts and responses
    /* small arrays, as JSON text; NULL when empty */
    (N'CopilotEventData_AISystemPlugin',         N'NVARCHAR(MAX) NULL'),
    (N'CopilotEventData_ModelTransparencyDetails', N'NVARCHAR(MAX) NULL'),
    (N'CopilotEventData_ParticipatingAgents',    N'NVARCHAR(MAX) NULL'),
    (N'CopilotEventData_SourceThreadIds',        N'NVARCHAR(MAX) NULL');

DECLARE @n SYSNAME, @d NVARCHAR(200), @ddl NVARCHAR(600);
DECLARE c CURSOR LOCAL FAST_FORWARD FOR SELECT Name, Def FROM @cols;
OPEN c; FETCH NEXT FROM c INTO @n, @d;
WHILE @@FETCH_STATUS = 0
BEGIN
    IF COL_LENGTH(N'audit.RawLogFlattened', @n) IS NULL
    BEGIN
        SET @ddl = N'ALTER TABLE audit.RawLogFlattened ADD ' + QUOTENAME(@n) + N' ' + @d + N';';
        EXEC (@ddl);
    END;
    FETCH NEXT FROM c INTO @n, @d;
END;
CLOSE c; DEALLOCATE c;

/*
    Seventeen fields carry no information in a 19-million-record production
    profile: each has one value on every record where it appears, or is always
    empty (Operation, RecordType, Workload, UserType, Version,
    CopilotLogVersion, SubjectType, AgentVersion, AgentPlatform,
    AgentBlueprintId, AppAccessContext.ClientAppId, CopilotEventData.
    PlatformAgentType and .IsSpawnTask; AssociatedAdminUnits and its names,
    and CopilotEventData.MessageIds, .GuardrailDetails and .TargetTaskIds).
    They are not stored per row. The flattening counts each combination it
    sees here instead - the empty ones as whether they had anything - and
    says so if any of them ever takes a second value, so the day one starts
    to vary it is noticed rather than lost. OrganizationId has one value too,
    but stays on every row: it is what keeps a database holding more than one
    tenant correct.
*/
IF OBJECT_ID(N'audit.RawLogRecordShape', N'U') IS NULL
    CREATE TABLE audit.RawLogRecordShape
    (
        ShapeHash                           BINARY(32)       NOT NULL CONSTRAINT PK_RawLogRecordShape PRIMARY KEY,
        RecordType                          SMALLINT         NULL,
        Operation                           VARCHAR(64)      NULL,
        Workload                            VARCHAR(32)      NULL,
        UserType                            TINYINT          NULL,
        [Version]                           TINYINT          NULL,
        CopilotLogVersion                   VARCHAR(32)      NULL,
        SubjectType                         TINYINT          NULL,
        AgentVersion                        VARCHAR(32)      NULL,
        AgentPlatform                       VARCHAR(64)      NULL,
        AgentBlueprintId                    UNIQUEIDENTIFIER NULL,
        AppAccessContext_ClientAppId        UNIQUEIDENTIFIER NULL,
        CopilotEventData_PlatformAgentType  VARCHAR(64)      NULL,
        CopilotEventData_IsSpawnTask        BIT              NULL,
        HasAdminUnits                       BIT              NOT NULL,   -- AssociatedAdminUnits or its names had a value
        HasMessageIds                       BIT              NOT NULL,   -- CopilotEventData.MessageIds not empty
        HasGuardrailDetails                 BIT              NOT NULL,
        HasTargetTaskIds                    BIT              NOT NULL,
        Records                             BIGINT           NOT NULL,   -- flattened with this shape since the last full rebuild; a re-flatten counts again
        FirstSeenUtc                        DATETIME2(0)     NOT NULL CONSTRAINT DF_RawLogRecordShape_First DEFAULT (SYSUTCDATETIME()),
        LastSeenUtc                         DATETIME2(0)     NOT NULL CONSTRAINT DF_RawLogRecordShape_Last DEFAULT (SYSUTCDATETIME())
    );

/*
    One row per element of CopilotEventData.AccessedResources: what Copilot
    read, where, under which label and policy. 1.8 elements per record in the
    production profile, so about twice the rows of the flattened table; page
    compression keeps the repetitive URLs and types small.

    Blocked: the element's Status is present and not Success, or its Purview
    policy outcome is anything other than None. PolicyDetails arrives as
    JSON text inside the JSON; PolicyOutcomes lists the outcomes that were
    not None.
*/
IF OBJECT_ID(N'audit.RawLogAccessedResource', N'U') IS NULL
    CREATE TABLE audit.RawLogAccessedResource
    (
        RecordId            NVARCHAR(440)    NOT NULL,   -- audit.RawLogFlattened (440, not 450, so the key stays under 900 bytes)
        Ordinal             SMALLINT         NOT NULL,   -- position in the array, from 0
        [Action]            NVARCHAR(450)    NULL,       -- Read, Create ...
        [Type]              NVARCHAR(200)    NULL,       -- File, EmailMessage, TeamsMessage, a URL scheme ...
        Id                  NVARCHAR(1000)   NULL,
        Name                NVARCHAR(4000)   NULL,
        SiteUrl             NVARCHAR(MAX)    NULL,
        Site                NVARCHAR(400)    NULL,       -- SiteUrl cut to https://host/sites/name (or /personal/name, /teams/name)
        ListItemUniqueId    UNIQUEIDENTIFIER NULL,
        SensitivityLabelId  UNIQUEIDENTIFIER NULL,
        XPIADetected        BIT              NULL,       -- cross-prompt injection found in this resource
        [Status]            VARCHAR(64)      NULL,
        PolicyDetails       NVARCHAR(MAX)    NULL,
        PolicyOutcomes      NVARCHAR(400)    NULL,       -- the outcomes other than None, '; '-joined
        IsBlocked           BIT              NOT NULL,
        CONSTRAINT PK_RawLogAccessedResource PRIMARY KEY (RecordId, Ordinal) WITH (DATA_COMPRESSION = PAGE)
    );

/* One row per element of CopilotEventData.Contexts: where the user was (the open document, meeting, chat). */
IF OBJECT_ID(N'audit.RawLogContext', N'U') IS NULL
    CREATE TABLE audit.RawLogContext
    (
        RecordId     NVARCHAR(440)   NOT NULL,
        Ordinal      SMALLINT        NOT NULL,
        Id           NVARCHAR(4000)  NULL,
        [Type]       VARCHAR(64)     NULL,
        ContainerId  VARCHAR(64)     NULL,
        CONSTRAINT PK_RawLogContext PRIMARY KEY (RecordId, Ordinal) WITH (DATA_COMPRESSION = PAGE)
    );

/* Reporting filters hit the timestamp and the user far more than anything
   else. A rowstore table (not columnstore): the procedure MERGEs into it. */
IF OBJECT_ID(N'audit.RawLogFlattened', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes
                   WHERE object_id = OBJECT_ID(N'audit.RawLogFlattened') AND name = N'IX_RawLogFlattened_UtcTimeStamp')
    EXEC (N'CREATE INDEX IX_RawLogFlattened_UtcTimeStamp
            ON audit.RawLogFlattened (UtcTimeStamp) INCLUDE ([User], App);');

/*
    Optional license lookup. Create and populate it (from Entra/Graph, or a
    license report) and the flatten procedure fills IsCopilotLic; leave it out
    and IsCopilotLic stays NULL. Matched on UPN, case-insensitively.
*/
IF OBJECT_ID(N'audit.CopilotLicense', N'U') IS NULL
    CREATE TABLE audit.CopilotLicense
    (
        UserPrincipalName   NVARCHAR(512)   NOT NULL
                                CONSTRAINT PK_CopilotLicense PRIMARY KEY,
        /* Nullable on purpose: NULL means "not known", which is the honest
           answer for a user who appears in the audit data but has since left
           the directory. 0 would claim they were unlicensed. */
        IsCopilotLic        BIT             NULL,
        UpdatedUtc          DATETIME2(0)    NOT NULL CONSTRAINT DF_CopilotLicense_Updated DEFAULT (SYSUTCDATETIME())
    );

/*
    The value catalog: every distinct value of the columns that carry an
    opaque code or a short list - label ids, agent and app ids, license types,
    model names, regions - so an administrator can give each one a readable
    name (dash.ValueName, Admin > Value names). The flatten procedure adds each
    batch's values (audit.usp_CatalogValues); a column that passes 10,000
    distinct values is marked Capped and stops collecting, since a list that
    long is not one to name by hand.

    Records counts every time a record is flattened, so a record re-flattened
    by the nightly lookback counts again: good for ranking, not for reporting.
*/
IF OBJECT_ID(N'audit.ValueCatalogColumn', N'U') IS NULL
    CREATE TABLE audit.ValueCatalogColumn
    (
        ColumnId     SMALLINT        NOT NULL CONSTRAINT PK_ValueCatalogColumn PRIMARY KEY,
        TableName    SYSNAME         NOT NULL,   -- in the audit schema
        ColumnName   SYSNAME         NOT NULL,
        Label        NVARCHAR(200)   NOT NULL,   -- what an administrator sees
        DistinctValues INT           NOT NULL CONSTRAINT DF_ValueCatalogColumn_Distinct DEFAULT (0),
        Capped       BIT             NOT NULL CONSTRAINT DF_ValueCatalogColumn_Capped DEFAULT (0),
        UpdatedUtc   DATETIME2(0)    NULL,
        CONSTRAINT UQ_ValueCatalogColumn UNIQUE (TableName, ColumnName)
    );
IF OBJECT_ID(N'audit.ValueCatalog', N'U') IS NULL
    CREATE TABLE audit.ValueCatalog
    (
        ColumnId     SMALLINT        NOT NULL,
        Value        NVARCHAR(400)   NOT NULL,
        Records      BIGINT          NOT NULL,
        FirstSeenUtc DATETIME2(0)    NOT NULL CONSTRAINT DF_ValueCatalog_First DEFAULT (SYSUTCDATETIME()),
        LastSeenUtc  DATETIME2(0)    NOT NULL CONSTRAINT DF_ValueCatalog_Last DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT PK_ValueCatalog PRIMARY KEY (ColumnId, Value)
    );
/*
    The agent catalog (2.4.11): every agent the audit log names, with the
    friendly name the log gave it last. A record names its agent in AppIdentity
    (Copilot.Studio.<...>, MicrosoftAgent.<...>), in AgentId, or in both, and
    carries the agent's readable name in AgentName - on some records, not all,
    and makers rename agents. One row per pair of AppIdentity and AgentId as
    they appear together (N'' where a record has none), kept by the flatten
    procedure for each batch (audit.usp_CatalogAgents), so the name is known
    from the whole history however short the window a later step reads.
    bi.usp_NameAgents names bi.DimAgent from it.

    Nothing here counts records: a record re-flattened by the nightly lookback
    would count again. FirstSeenUtc and LastSeenUtc are the records' own times.
*/
IF OBJECT_ID(N'audit.AgentCatalog', N'U') IS NULL
    CREATE TABLE audit.AgentCatalog
    (
        AgentCatalogId INT IDENTITY(1, 1) NOT NULL CONSTRAINT PK_AgentCatalog PRIMARY KEY,
        AppIdentity    NVARCHAR(200)   NOT NULL,   -- $.AppIdentity; N'' when the record has none
        AgentId        NVARCHAR(400)   NOT NULL,   -- $.AgentId; N'' when the record has none
        AgentName      NVARCHAR(400)   NULL,       -- $.AgentName on the latest record that carried one
        NameSeenUtc    DATETIME2(0)    NULL,       -- that record's time
        FirstSeenUtc   DATETIME2(0)    NULL,       -- the earliest record of this agent
        LastSeenUtc    DATETIME2(0)    NULL,       -- the latest
        CONSTRAINT UQ_AgentCatalog UNIQUE (AppIdentity, AgentId)
    );

/* The ids are stable: names given in dash.ValueName refer to them. */
MERGE audit.ValueCatalogColumn AS t
USING (VALUES
    (1,  N'RawLogFlattened',        N'OrganizationId',                          N'Tenant (OrganizationId)'),
    (2,  N'RawLogFlattened',        N'App',                                     N'App (AppHost)'),
    (3,  N'RawLogFlattened',        N'Location',                                N'Region (ClientRegion)'),
    (4,  N'RawLogFlattened',        N'AppIdentity',                             N'App identity'),
    (5,  N'RawLogFlattened',        N'ApplicationName',                         N'Application name'),
    (6,  N'RawLogFlattened',        N'AgentId',                                 N'Agent id'),
    (7,  N'RawLogFlattened',        N'PlatformAgentId',                         N'Platform agent id'),
    (8,  N'RawLogFlattened',        N'ResultStatus',                            N'Result status'),
    (9,  N'RawLogFlattened',        N'AppAccessContext_APIId',                  N'API id (AppAccessContext.APIId)'),
    (10, N'RawLogFlattened',        N'CopilotEventData_LicenseType',            N'License type'),
    (11, N'RawLogFlattened',        N'CopilotEventData_SensitivityLabelId',     N'Sensitivity label of the interaction'),
    (12, N'RawLogFlattened',        N'CopilotEventData_TargetAgentName',        N'Agent called (TargetAgentName)'),
    (13, N'RawLogFlattened',        N'CopilotEventData_TargetPlatformAgentId',  N'Agent called, platform id'),
    (14, N'RawLogFlattened',        N'CopilotEventData_FabricCapacityId',       N'Fabric capacity'),
    (15, N'RawLogFlattened',        N'CopilotEventData_PlatformAgentId',        N'Platform agent id (CopilotEventData)'),
    (16, N'RawLogFlattened',        N'ModelName',                               N'Model'),
    (17, N'RawLogFlattened',        N'ModelProvider',                           N'Model provider'),
    (18, N'RawLogFlattened',        N'ModelVersion',                            N'Model version'),
    (30, N'RawLogAccessedResource', N'SensitivityLabelId',                      N'Sensitivity label of a resource'),
    (31, N'RawLogAccessedResource', N'Type',                                    N'Resource type'),
    (32, N'RawLogAccessedResource', N'Action',                                  N'Resource action'),
    (33, N'RawLogAccessedResource', N'Status',                                  N'Resource status'),
    (40, N'RawLogContext',          N'Type',                                    N'Context type')
) AS s (ColumnId, TableName, ColumnName, Label)
ON t.ColumnId = s.ColumnId
WHEN MATCHED AND (t.Label <> s.Label OR t.TableName <> s.TableName OR t.ColumnName <> s.ColumnName)
    THEN UPDATE SET Label = s.Label, TableName = s.TableName, ColumnName = s.ColumnName
WHEN NOT MATCHED THEN INSERT (ColumnId, TableName, ColumnName, Label) VALUES (s.ColumnId, s.TableName, s.ColumnName, s.Label);
```

### C.5 `sql/Create-CopilotFlattenProcedure.sql`

Copilot database. `audit.usp_FlattenCopilotAudit`, which writes every field of a record into columns. 1070 lines.

```sql
/*
    Copilot audit backfill - audit.usp_FlattenCopilotAudit
    ------------------------------------------------------------------------
    Shreds the CopilotEventData JSON into audit.RawLogFlattened, one row per
    audit record, with every informative scalar field of the record in its own
    column (2.2.0; the seventeen that carry no information are counted in
    audit.RawLogRecordShape instead), and the AccessedResources and Contexts arrays into
    audit.RawLogAccessedResource and audit.RawLogContext, one row per element.

    Reads audit.vCopilotAuditRecord, which has the Purview export's column
    shape (RecordId, CreationDate, RecordType, Operation, UserId, AuditData,
    AssociatedAdminUnits, AssociatedAdminUnitsNames). Any table with those
    columns works, so to flatten a portal CSV import instead, change the one
    FROM in the #batch load below to your own raw-log table.

    Parameters:
      @SinceUtc     only records at or after this UTC time. NULL = everything.
      @RebuildAll   1 truncates and rebuilds; 0 (default) MERGEs, so it is
                    safe to run incrementally on a schedule. 1 is refused
                    once the purge has cleared any record's JSON (2.4.12):
                    run with 0, which flattens every record again and keeps
                    what the purged ones were flattened to.
      @BatchSize    records per batch. Keeps the log, tempdb and lock
                    footprint flat on tens of millions of rows. Default
                    200,000.

    The agent (2.4.11): a record carries its agent's id and friendly name at
    the top ($.AgentId, $.AgentName), not inside CopilotEventData, where this
    procedure looked before and found nothing on any record. Both places are
    read now, and each batch's agents go into audit.AgentCatalog with the
    latest name the log gave each (audit.usp_CatalogAgents, below). Records
    flattened before 2.4.11 get theirs from audit.usp_BackfillAgentNames, once.

    Re-runnable and idempotent: a record already flattened is updated in place,
    keyed on RecordId, and its element rows replaced. A record whose JSON the
    purge procedure has cleared is skipped once it has been flattened, so a
    full re-flatten never blanks what was shredded while the JSON existed.

    ---- How it parses, and why it is shaped this way --------------------------

    Every JSON_VALUE or OPENJSON call against a document parses that whole
    document. Reading twenty-odd fields with twenty-odd calls therefore parses
    each record twenty-odd times, and the documents here are the expensive part
    of the row - they arrive gzipped and are decompressed by the view before
    any of this runs.

    So each batch parses in stages, widest first, and keeps the results:

      #batch  one OPENJSON over the whole document for the top-level scalars,
              and a second over the CopilotEventData fragment it returns for
              that object's scalars. Both pull the nested arrays out as JSON
              fragments in the same pass, so nothing below ever touches the
              full document again.
      #res    the AccessedResources fragment exploded once into rows.

    These are temp tables rather than CTEs on purpose: SQL Server does not
    materialise a CTE, so referencing one several times re-runs the OPENJSON
    underneath it, which is the cost this is trying to avoid. Everything after
    #res - including the DISTINCT lists, which STRING_AGG cannot express
    directly - is ordinary relational aggregation over rows that are already
    parsed.

    Requires SQL Server 2017+ (STRING_AGG, OPENJSON). Run
    sql/Create-CopilotFlattenedSchema.sql first.
*/
SET NOCOUNT ON;

/*
    audit.usp_CatalogValues: adds the distinct values of the cataloged columns
    (audit.ValueCatalogColumn) to audit.ValueCatalog. The flatten calls it for
    each batch while #batch exists, so only that batch's records are read.
    Called on its own it reads every flattened record; @Rebuild = 1 starts the
    catalog again (Invoke-CopilotMaintenance.ps1 -Task RebuildValueCatalog).
    A column stops collecting at 10,000 distinct values and is marked Capped.
*/
IF OBJECT_ID(N'audit.usp_CatalogValues', N'P') IS NULL
    EXEC (N'CREATE PROCEDURE audit.usp_CatalogValues AS RETURN 0;');
GO

ALTER PROCEDURE audit.usp_CatalogValues
    @Rebuild BIT = 0
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    IF OBJECT_ID(N'audit.ValueCatalogColumn', N'U') IS NULL RETURN;

    DECLARE @cap INT = 10000;
    DECLARE @inBatch BIT = CASE WHEN OBJECT_ID(N'tempdb..#batch') IS NULL THEN 0 ELSE 1 END;
    IF @Rebuild = 1 AND @inBatch = 0
    BEGIN
        TRUNCATE TABLE audit.ValueCatalog;
        UPDATE audit.ValueCatalogColumn SET DistinctValues = 0, Capped = 0, UpdatedUtc = NULL;
    END;

    /* In the database's collation (2.4.12): in tempdb's, a database whose collation differs from the server's
       failed every flatten at the first comparison with the catalog. So for #agents below. */
    CREATE TABLE #v (ColumnId SMALLINT NOT NULL, Value NVARCHAR(400) COLLATE DATABASE_DEFAULT NOT NULL, N BIGINT NOT NULL, PRIMARY KEY (ColumnId, Value));

    DECLARE @t SYSNAME, @vals NVARCHAR(MAX), @sql NVARCHAR(MAX);
    DECLARE tc CURSOR LOCAL FAST_FORWARD FOR
        SELECT DISTINCT TableName FROM audit.ValueCatalogColumn
        WHERE Capped = 0 AND OBJECT_ID(N'audit.' + QUOTENAME(TableName), N'U') IS NOT NULL;
    OPEN tc;
    FETCH NEXT FROM tc INTO @t;
    WHILE @@FETCH_STATUS = 0
    BEGIN
        /* One pass over the table for all its columns: each row unpivoted to (column, value). */
        SELECT @vals = STRING_AGG(CAST(CONCAT(N'(', c.ColumnId, N', ',
                   CASE WHEN ty.name = N'uniqueidentifier' THEN N'LOWER(CAST(t.' + QUOTENAME(c.ColumnName) + N' AS NVARCHAR(36)))'
                        ELSE N'LEFT(LTRIM(RTRIM(CAST(t.' + QUOTENAME(c.ColumnName) + N' AS NVARCHAR(400)))), 400)' END,
                   N')') AS NVARCHAR(MAX)), N', ')
        FROM audit.ValueCatalogColumn AS c
        JOIN sys.columns AS sc ON sc.object_id = OBJECT_ID(N'audit.' + QUOTENAME(c.TableName)) AND sc.name = c.ColumnName
        JOIN sys.types AS ty ON ty.user_type_id = sc.user_type_id
        WHERE c.TableName = @t AND c.Capped = 0;

        IF @vals IS NOT NULL
        BEGIN
            SET @sql = N'INSERT INTO #v (ColumnId, Value, N)
SELECT v.ColumnId, v.Value, COUNT_BIG(*)
FROM audit.' + QUOTENAME(@t) + N' AS t '
                + CASE WHEN @inBatch = 1 THEN N'JOIN #batch AS b ON b.RecordId = t.RecordId ' ELSE N'' END + N'
CROSS APPLY (VALUES ' + @vals + N') AS v (ColumnId, Value)
WHERE v.Value IS NOT NULL AND v.Value <> N''''
GROUP BY v.ColumnId, v.Value;';
            EXEC sys.sp_executesql @sql;
        END;
        FETCH NEXT FROM tc INTO @t;
    END;
    CLOSE tc;
    DEALLOCATE tc;

    BEGIN TRANSACTION;
    UPDATE t SET Records = t.Records + v.N, LastSeenUtc = SYSUTCDATETIME()
    FROM audit.ValueCatalog AS t
    JOIN #v AS v ON v.ColumnId = t.ColumnId AND v.Value = t.Value;

    /* New values, up to the cap: the most frequent first. */
    WITH n AS
    (
        SELECT v.ColumnId, v.Value, v.N, ROW_NUMBER() OVER (PARTITION BY v.ColumnId ORDER BY v.N DESC, v.Value) AS rn
        FROM #v AS v
        WHERE NOT EXISTS (SELECT 1 FROM audit.ValueCatalog AS t WHERE t.ColumnId = v.ColumnId AND t.Value = v.Value)
    )
    INSERT INTO audit.ValueCatalog (ColumnId, Value, Records)
    SELECT n.ColumnId, n.Value, n.N
    FROM n JOIN audit.ValueCatalogColumn AS c ON c.ColumnId = n.ColumnId
    WHERE n.rn <= @cap - c.DistinctValues;

    UPDATE c
    SET DistinctValues = ISNULL(k.N, 0),
        Capped = CASE WHEN ISNULL(k.N, 0) >= @cap THEN 1 ELSE 0 END,
        UpdatedUtc = SYSUTCDATETIME()
    FROM audit.ValueCatalogColumn AS c
    LEFT JOIN (SELECT ColumnId, COUNT(*) AS N FROM audit.ValueCatalog GROUP BY ColumnId) AS k ON k.ColumnId = c.ColumnId
    WHERE c.ColumnId IN (SELECT ColumnId FROM #v) OR @Rebuild = 1;
    COMMIT;
END;
GO

/*
    audit.usp_CatalogAgents: the agents named by a set of flattened records into
    audit.AgentCatalog, each with the latest friendly name the records give it.
    The flatten calls it for each batch while #batch exists, so only that
    batch's records are read; called on its own it reads every flattened record
    (one pass over audit.RawLogFlattened). Idempotent: the same records again
    change nothing. A name is replaced only by one from a record at least as
    recent, so a batch of older records never undoes a rename.
*/
IF OBJECT_ID(N'audit.usp_CatalogAgents', N'P') IS NULL
    EXEC (N'CREATE PROCEDURE audit.usp_CatalogAgents AS RETURN 0;');
GO

ALTER PROCEDURE audit.usp_CatalogAgents
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    IF OBJECT_ID(N'audit.AgentCatalog', N'U') IS NULL RETURN;

    DECLARE @from NVARCHAR(100) = CASE WHEN OBJECT_ID(N'tempdb..#batch') IS NULL THEN N'audit.RawLogFlattened' ELSE N'#batch' END;
    CREATE TABLE #agents (AppIdentity NVARCHAR(200) COLLATE DATABASE_DEFAULT NOT NULL, AgentId NVARCHAR(400) COLLATE DATABASE_DEFAULT NOT NULL,
                          AgentName NVARCHAR(400) COLLATE DATABASE_DEFAULT NULL,
                          NameSeenUtc DATETIME2(0) NULL, FirstSeenUtc DATETIME2(0) NULL, LastSeenUtc DATETIME2(0) NULL);

    /* One row per agent: its first and last record, and the name on the latest record that has one. */
    DECLARE @sql NVARCHAR(MAX) = N'
INSERT INTO #agents (AppIdentity, AgentId, AgentName, NameSeenUtc, FirstSeenUtc, LastSeenUtc)
SELECT x.AppIdentity, x.AgentId, MAX(CASE WHEN x.rn = 1 THEN x.AgentName END), MAX(CASE WHEN x.rn = 1 THEN x.Utc END), MIN(x.Utc), MAX(x.Utc)
FROM (SELECT v.AppIdentity, v.AgentId, v.AgentName, b.UtcTimeStamp AS Utc,
             CASE WHEN v.AgentName IS NULL THEN NULL
                  ELSE ROW_NUMBER() OVER (PARTITION BY v.AppIdentity, v.AgentId, CASE WHEN v.AgentName IS NULL THEN 0 ELSE 1 END
                                          ORDER BY b.UtcTimeStamp DESC, b.RecordId DESC) END AS rn
      FROM ' + @from + N' AS b
      CROSS APPLY (SELECT ISNULL(LTRIM(RTRIM(b.AppIdentity)), N'''') AS AppIdentity, ISNULL(LTRIM(RTRIM(b.AgentId)), N'''') AS AgentId,
                          NULLIF(LTRIM(RTRIM(b.AgentName)), N'''') AS AgentName) AS v
      WHERE v.AgentId <> N'''' OR v.AgentName IS NOT NULL OR v.AppIdentity LIKE N''Copilot.Studio.%'' OR v.AppIdentity LIKE N''MicrosoftAgent.%'') AS x
GROUP BY x.AppIdentity, x.AgentId;';
    EXEC sys.sp_executesql @sql;

    MERGE audit.AgentCatalog AS t
    USING #agents AS s ON t.AppIdentity = s.AppIdentity AND t.AgentId = s.AgentId
    WHEN MATCHED THEN UPDATE SET
        AgentName    = CASE WHEN s.AgentName IS NOT NULL AND (t.NameSeenUtc IS NULL OR s.NameSeenUtc >= t.NameSeenUtc) THEN s.AgentName ELSE t.AgentName END,
        NameSeenUtc  = CASE WHEN s.AgentName IS NOT NULL AND (t.NameSeenUtc IS NULL OR s.NameSeenUtc >= t.NameSeenUtc) THEN s.NameSeenUtc ELSE t.NameSeenUtc END,
        FirstSeenUtc = CASE WHEN t.FirstSeenUtc IS NULL OR s.FirstSeenUtc < t.FirstSeenUtc THEN s.FirstSeenUtc ELSE t.FirstSeenUtc END,
        LastSeenUtc  = CASE WHEN t.LastSeenUtc IS NULL OR s.LastSeenUtc > t.LastSeenUtc THEN s.LastSeenUtc ELSE t.LastSeenUtc END
    WHEN NOT MATCHED THEN INSERT (AppIdentity, AgentId, AgentName, NameSeenUtc, FirstSeenUtc, LastSeenUtc)
        VALUES (s.AppIdentity, s.AgentId, s.AgentName, s.NameSeenUtc, s.FirstSeenUtc, s.LastSeenUtc);
END;
GO

/*
    audit.usp_BackfillAgentNames: the agent's id and friendly name for records
    flattened before 2.4.11, which looked for them in the wrong place. Reads
    only the records of agents (AppIdentity names one) that still have no id or
    no name, takes $.AgentId and $.AgentName from each one's stored JSON, writes
    them to audit.RawLogFlattened, and catalogs the agents. Nothing else of the
    record is flattened again, so it takes minutes where a full re-flatten
    takes hours. A record whose JSON the purge cleared keeps what it has.

    Install-CopilotAudit.ps1 runs it once on an upgrade: it leaves a mark in
    audit.PipelineSetting (AgentNamesReadUtc), as does a flatten of every
    record. Safe to run again: it reads what is still empty, and changes
    nothing that is filled. Each batch's records and their agents commit
    together, so a run stopped part way reads its last batch again (2.4.12).

    @Others = 1 (2.4.12): the records of any other app that carry an agent's id,
    such as Copilot Chat running a declarative agent. bi keys those on the
    AgentId, which 2.4.11 read from its upgrade night on: before it they had
    no agent, so the same agent's history started that night. Read are the
    apps that carry one: those whose records flattened since show it, and
    those among the newest records still without one whose JSON has it; an
    app that carries none has nothing to read. Its mark is AgentIdsReadUtc,
    and it returns how many
    records gained an id: each of those changes agent, so bi is rebuilt once.
    Records flattened after AgentNamesReadUtc (by 2.4.11 or later, which read
    the id where it is) are not read again.

    Resumable (2.4.12): each batch saves where it got to and what it gained
    (<mark>Cursor, <mark>Gained in audit.PipelineSetting), and a run carries on
    from there; the mark clears them.
*/
IF OBJECT_ID(N'audit.usp_BackfillAgentNames', N'P') IS NULL
    EXEC (N'CREATE PROCEDURE audit.usp_BackfillAgentNames AS RETURN 0;');
GO

ALTER PROCEDURE audit.usp_BackfillAgentNames
    @BatchSize INT = 50000,
    @Others    BIT = 0
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    IF @BatchSize IS NULL OR @BatchSize < 1000 SET @BatchSize = 1000;

    DECLARE @msg NVARCHAR(400), @read BIGINT = 0, @named BIGINT = 0, @rows INT;
    DECLARE @cursor NVARCHAR(450) = N'', @maxId NVARCHAR(450);
    DECLARE @mark SYSNAME = CASE WHEN @Others = 1 THEN N'AgentIdsReadUtc' ELSE N'AgentNamesReadUtc' END;
    /* 2.4.12: where a run stopped part way got to, saved with each batch, and what it had gained. The read started
       from the first record again, and with @Others that is most of Copilot Chat's history: an install stopped
       after hours began again from zero, and a resumed run that gained nothing left bi unbuilt. */
    DECLARE @cursorName SYSNAME = @mark + N'Cursor', @gainedName SYSNAME = @mark + N'Gained', @before BIGINT = 0;
    /* With @Others: records flattened once the 2.4.11 flatten was in already read the agent's id where it is, so
       they are left out. AgentNamesReadUtc marks that (it is written after that release installed its flatten). */
    DECLARE @since DATETIME2(0);
    IF OBJECT_ID(N'audit.PipelineSetting', N'U') IS NOT NULL
    BEGIN
        SELECT @cursor = ISNULL(Value, N'') FROM audit.PipelineSetting WHERE Name = @cursorName;
        SELECT @before = ISNULL(TRY_CAST(Value AS BIGINT), 0) FROM audit.PipelineSetting WHERE Name = @gainedName;
        IF @Others = 1
            SELECT @since = TRY_CONVERT(DATETIME2(0), Value, 126) FROM audit.PipelineSetting WHERE Name = N'AgentNamesReadUtc';
    END;
    IF @cursor <> N''
    BEGIN
        SET @msg = CONCAT(N'  Carrying on where a run stopped part way (', FORMAT(@before, 'N0'), N' record(s) gained by then).');
        RAISERROR(@msg, 0, 1) WITH NOWAIT;
    END;

    /* The records to read, keyed once: an agent's, with its id or its name still empty; or, with @Others, another
       app's with no id, where that app's records flattened since 2.4.11 show one. */
    DROP TABLE IF EXISTS #todo;
    CREATE TABLE #todo (RecordId NVARCHAR(450) COLLATE DATABASE_DEFAULT NOT NULL);
    IF @Others = 0
        INSERT INTO #todo (RecordId)
        SELECT f.RecordId
        FROM audit.RawLogFlattened AS f
        WHERE (f.AppIdentity LIKE N'Copilot.Studio.%' OR f.AppIdentity LIKE N'MicrosoftAgent.%')
          AND (f.AgentId IS NULL OR f.AgentName IS NULL)
          AND f.RecordId > @cursor;
    ELSE
    BEGIN
        /* The apps that carry an agent's id: those whose records flattened since 2.4.11 show one, and those among the
           newest records still without one whose JSON has one (an upgrade from before 2.4.11 has flattened none since). */
        DROP TABLE IF EXISTS #apps;
        CREATE TABLE #apps (AppIdentity NVARCHAR(200) COLLATE DATABASE_DEFAULT NOT NULL PRIMARY KEY);
        INSERT INTO #apps (AppIdentity)
        SELECT DISTINCT g.AppIdentity
        FROM audit.RawLogFlattened AS g
        WHERE g.AgentId IS NOT NULL AND g.AppIdentity IS NOT NULL
          AND g.AppIdentity NOT LIKE N'Copilot.Studio.%' AND g.AppIdentity NOT LIKE N'MicrosoftAgent.%';
        INSERT INTO #apps (AppIdentity)
        SELECT DISTINCT s.AppIdentity
        FROM (SELECT TOP (50000) f.RecordId, f.AppIdentity
              FROM audit.RawLogFlattened AS f
              WHERE f.AgentId IS NULL AND f.AppIdentity IS NOT NULL
                AND f.AppIdentity NOT LIKE N'Copilot.Studio.%' AND f.AppIdentity NOT LIKE N'MicrosoftAgent.%'
                AND (@since IS NULL OR f.FlattenedUtc < @since)
              ORDER BY f.UtcTimeStamp DESC) AS s
        JOIN audit.vCopilotAuditRecord AS r ON r.RecordId = s.RecordId
        CROSS APPLY OPENJSON(CASE WHEN ISJSON(r.AuditData) = 1 THEN r.AuditData ELSE N'{}' END)
            WITH (TopId NVARCHAR(400) '$.AgentId', InId NVARCHAR(400) '$.CopilotEventData.AgentId') AS j
        WHERE COALESCE(NULLIF(j.InId, N''), NULLIF(j.TopId, N'')) IS NOT NULL
          AND NOT EXISTS (SELECT 1 FROM #apps AS a WHERE a.AppIdentity = s.AppIdentity);
        INSERT INTO #todo (RecordId)
        SELECT f.RecordId
        FROM audit.RawLogFlattened AS f
        JOIN #apps AS a ON a.AppIdentity = f.AppIdentity
        WHERE f.AgentId IS NULL
          AND (@since IS NULL OR f.FlattenedUtc < @since)
          AND f.RecordId > @cursor;
        SET @msg = CONCAT(N'  Agent ids: ', (SELECT COUNT(*) FROM #apps), N' other app(s) carry them; ', FORMAT((SELECT COUNT_BIG(*) FROM #todo), 'N0'), N' of their record(s) have none yet.');
        RAISERROR(@msg, 0, 1) WITH NOWAIT;
    END;
    CREATE CLUSTERED INDEX CIX_todo ON #todo (RecordId);

    WHILE 1 = 1
    BEGIN
        SET @maxId = NULL;
        ;WITH nextBatch AS (SELECT TOP (@BatchSize) k.RecordId FROM #todo AS k WHERE k.RecordId > @cursor ORDER BY k.RecordId)
        SELECT @maxId = MAX(RecordId) FROM nextBatch;
        IF @maxId IS NULL BREAK;

        /* #batch: the name audit.usp_CatalogAgents reads. What the record already has is kept; the JSON fills what is empty. */
        DROP TABLE IF EXISTS #batch;
        SELECT f.RecordId, f.AppIdentity, f.UtcTimeStamp,
               COALESCE(NULLIF(f.AgentId, N''),   LEFT(COALESCE(NULLIF(j.InId, N''),   NULLIF(j.TopId, N'')), 400))   AS AgentId,
               COALESCE(NULLIF(f.AgentName, N''), LEFT(COALESCE(NULLIF(j.InName, N''), NULLIF(j.TopName, N'')), 400)) AS AgentName,
               CAST(CASE WHEN (NULLIF(f.AgentId, N'') IS NULL AND COALESCE(NULLIF(j.InId, N''), NULLIF(j.TopId, N'')) IS NOT NULL)
                           OR (NULLIF(f.AgentName, N'') IS NULL AND COALESCE(NULLIF(j.InName, N''), NULLIF(j.TopName, N'')) IS NOT NULL)
                         THEN 1 ELSE 0 END AS BIT) AS Gained
        INTO #batch
        FROM #todo AS k
        JOIN audit.RawLogFlattened AS f ON f.RecordId = k.RecordId
        LEFT JOIN audit.vCopilotAuditRecord AS r ON r.RecordId = k.RecordId
        OUTER APPLY OPENJSON(CASE WHEN ISJSON(r.AuditData) = 1 THEN r.AuditData ELSE N'{}' END)
            WITH (TopId NVARCHAR(400) '$.AgentId', TopName NVARCHAR(400) '$.AgentName',
                  InId  NVARCHAR(400) '$.CopilotEventData.AgentId', InName NVARCHAR(400) '$.CopilotEventData.AgentName') AS j
        WHERE k.RecordId > @cursor AND k.RecordId <= @maxId
        OPTION (MAXDOP 0);
        SET @rows = @@ROWCOUNT;

        /* 2.4.12: the batch's records and its catalog step in one transaction. A run stopped between them kept the
           records' new ids and names but not their agents: the next run reads only what is still empty, so those
           names never reached the catalog, and bi kept the name read from the id. Now a stopped batch is read again. */
        BEGIN TRANSACTION;
        UPDATE f
        SET AgentId = b.AgentId, AgentName = b.AgentName
        FROM audit.RawLogFlattened AS f
        JOIN #batch AS b ON b.RecordId = f.RecordId
        WHERE b.Gained = 1;
        SET @named += @@ROWCOUNT;

        EXEC audit.usp_CatalogAgents;

        /* Where this run got to, with the batch it ends (2.4.12): a run stopped after it carries on from the next. */
        IF OBJECT_ID(N'audit.PipelineSetting', N'U') IS NOT NULL
        BEGIN
            UPDATE audit.PipelineSetting SET Value = @maxId, UpdatedUtc = SYSUTCDATETIME() WHERE Name = @cursorName;
            IF @@ROWCOUNT = 0 INSERT INTO audit.PipelineSetting (Name, Value) VALUES (@cursorName, @maxId);
            UPDATE audit.PipelineSetting SET Value = CONVERT(NVARCHAR(30), @before + @named), UpdatedUtc = SYSUTCDATETIME() WHERE Name = @gainedName;
            IF @@ROWCOUNT = 0 INSERT INTO audit.PipelineSetting (Name, Value) VALUES (@gainedName, CONVERT(NVARCHAR(30), @before + @named));
        END;
        COMMIT TRANSACTION;

        SET @read += @rows;
        SET @cursor = @maxId;
        DROP TABLE IF EXISTS #batch;
        SET @msg = CONCAT(N'  ', FORMAT(@read, 'N0'), N' agent record(s) read, ', FORMAT(@named, 'N0'), N' gained an id or a name.');
        RAISERROR(@msg, 0, 1) WITH NOWAIT;
    END;

    IF OBJECT_ID(N'audit.PipelineSetting', N'U') IS NOT NULL
    BEGIN
        BEGIN TRANSACTION;
        UPDATE audit.PipelineSetting SET Value = CONVERT(NVARCHAR(30), SYSUTCDATETIME(), 126), UpdatedUtc = SYSUTCDATETIME() WHERE Name = @mark;
        IF @@ROWCOUNT = 0 INSERT INTO audit.PipelineSetting (Name, Value) VALUES (@mark, CONVERT(NVARCHAR(30), SYSUTCDATETIME(), 126));
        DELETE FROM audit.PipelineSetting WHERE Name IN (@cursorName, @gainedName);
        COMMIT TRANSACTION;
    END;
    /* What this read gained, a run stopped before it included. */
    SET @named += @before;

    SET @msg = CONCAT(N'Agent names: ', FORMAT(@read, 'N0'), N' record(s) ', CASE WHEN @Others = 1 THEN N'of other apps' ELSE N'of agents' END, N' read, ',
                      FORMAT(@named, 'N0'), N' gained an id or a name; ',
                      (SELECT COUNT(*) FROM audit.AgentCatalog WHERE AgentName IS NOT NULL), N' of ', (SELECT COUNT(*) FROM audit.AgentCatalog), N' agent(s) in the catalog have a name.');
    RAISERROR(@msg, 0, 1) WITH NOWAIT;
    /* For Install-CopilotAudit.ps1: records of other apps that gained an id change agent, so bi is rebuilt once. */
    IF @Others = 1 SELECT @named AS Gained;
END;
GO

IF OBJECT_ID(N'audit.usp_FlattenCopilotAudit', N'P') IS NULL
    EXEC (N'CREATE PROCEDURE audit.usp_FlattenCopilotAudit AS BEGIN SET NOCOUNT ON; END;');
GO

ALTER PROCEDURE audit.usp_FlattenCopilotAudit
    @SinceUtc   DATETIME2(0) = NULL,
    @RebuildAll BIT          = 0,
    @BatchSize  INT          = 200000
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF @BatchSize IS NULL OR @BatchSize < 1000 SET @BatchSize = 1000;

    DECLARE @msg NVARCHAR(400), @rows BIGINT = 0, @done BIGINT = 0, @batch INT = 1;
    DECLARE @hasLicense BIT = CASE WHEN OBJECT_ID(N'audit.CopilotLicense', N'U') IS NULL THEN 0 ELSE 1 END;

    /* 2.4.12: a record whose JSON the purge cleared is kept as it was flattened (below), found by its flattened row.
       Emptying the table first left nothing to find it by, so every such record came back blank: every column the
       JSON gave NULL, no resources and no contexts, and nothing left to flatten it from again. */
    IF @RebuildAll = 1 AND OBJECT_ID(N'audit.CopilotAudit', N'U') IS NOT NULL
       AND EXISTS (SELECT 1 FROM audit.CopilotAudit WHERE AuditDataGz IS NULL)
    BEGIN
        RAISERROR('Rebuild refused: the purge has cleared the JSON of some records, and a rebuild would blank what they were flattened to. Run without @RebuildAll: every record is flattened again, and those keep what they have.', 16, 1);
        RETURN;
    END;

    IF @RebuildAll = 1
    BEGIN
        RAISERROR('Rebuilding audit.RawLogFlattened from scratch.', 0, 1) WITH NOWAIT;
        TRUNCATE TABLE audit.RawLogFlattened;
        IF OBJECT_ID(N'audit.RawLogAccessedResource', N'U') IS NOT NULL TRUNCATE TABLE audit.RawLogAccessedResource;
        IF OBJECT_ID(N'audit.RawLogContext', N'U') IS NOT NULL TRUNCATE TABLE audit.RawLogContext;
        IF OBJECT_ID(N'audit.RawLogRecordShape', N'U') IS NOT NULL TRUNCATE TABLE audit.RawLogRecordShape;
        IF OBJECT_ID(N'audit.usp_CatalogValues', N'P') IS NOT NULL EXEC audit.usp_CatalogValues @Rebuild = 1;
        /* Emptied: not flattened in full until this run finishes (2.4.12). */
        IF OBJECT_ID(N'audit.PipelineSetting', N'U') IS NOT NULL DELETE FROM audit.PipelineSetting WHERE Name = N'FullFlattenUtc';
        SET @SinceUtc = NULL;
    END;

    /*
        Work through the records in RecordId order, @BatchSize at a time. The
        cursor is the last RecordId done, so a batch that fails can be resumed
        by re-running: already-flattened records simply MERGE to themselves.
    */
    DECLARE @cursor NVARCHAR(450) = N'';
    DECLARE @maxId  NVARCHAR(450);

    /*
        The records to do, keyed once. Finding each batch's upper bound in the
        columnstore itself scans and sorts every remaining RecordId: about 95
        seconds a batch at 40M records, half the flatten. Keyed here it is a
        seek. Records that arrive while this runs are the next run's.
    */
    DROP TABLE IF EXISTS #todo;
    SELECT r.RecordId
    INTO #todo
    FROM audit.vCopilotAuditRecord AS r
    WHERE @SinceUtc IS NULL OR r.CreationDate >= @SinceUtc;
    CREATE CLUSTERED INDEX CIX_todo ON #todo (RecordId);

    WHILE 1 = 1
    BEGIN
        SET @maxId = NULL;

        ;WITH nextBatch AS
        (
            SELECT TOP (@BatchSize) k.RecordId
            FROM #todo AS k
            WHERE k.RecordId > @cursor
            ORDER BY k.RecordId
        )
        SELECT @maxId = MAX(RecordId) FROM nextBatch;

        IF @maxId IS NULL BREAK;   -- nothing left

        DROP TABLE IF EXISTS #batch;
        DROP TABLE IF EXISTS #raw;
        DROP TABLE IF EXISTS #res;
        DROP TABLE IF EXISTS #resagg;
        DROP TABLE IF EXISTS #actagg;
        DROP TABLE IF EXISTS #labagg;
        DROP TABLE IF EXISTS #ctxagg;
        DROP TABLE IF EXISTS #plugagg;
        DROP TABLE IF EXISTS #msgagg;

        /* ---- 0. Each record's JSON, decompressed once ------------------- */
        /*
            The view decompresses AuditData, and SQL Server may evaluate a
            view's expression once per reference. Materialising the batch here
            decompresses each record exactly once (in parallel), and lets the
            purge rule below drop records without touching their JSON again.
        */
        DROP TABLE IF EXISTS #raw;
        SELECT r.RecordId, r.CreationDate, r.UserId, r.AuditData, r.AssociatedAdminUnits, r.AssociatedAdminUnitsNames
        INTO #raw
        FROM audit.vCopilotAuditRecord AS r
        WHERE r.RecordId > @cursor AND r.RecordId <= @maxId
          AND (@SinceUtc IS NULL OR r.CreationDate >= @SinceUtc);

        /* JSON cleared by the purge: keep what was shredded while it existed. */
        DELETE w FROM #raw AS w
        WHERE w.AuditData IS NULL
          AND EXISTS (SELECT 1 FROM audit.RawLogFlattened AS f WHERE f.RecordId = w.RecordId);

        /* ---- 1. The only two passes over the document ------------------- */

        SELECT
            r.RecordId,

            /* scalars */
            LEFT(COALESCE(t.CreationTime, CONVERT(NVARCHAR(30), r.CreationDate, 126)), 4000)  AS [TimeStamp],
            /* Style 127, not 126: CreationTime carries a trailing Z, which only
               127 accepts. It also parses the unzoned fallback. */
            TRY_CONVERT(DATETIME2(0),
                COALESCE(t.CreationTime, CONVERT(NVARCHAR(30), r.CreationDate, 126)), 127)    AS UtcTimeStamp,
            LEFT(COALESCE(t.JsonUserId, r.UserId), 4000)                                      AS [User],
            LEFT(e.AppHost, 4000)                                                             AS App,
            t.ClientRegion                                                                    AS Location,
            LEFT(t.AppIdentity, 200)                                                          AS AppIdentity,
            LEFT(e.ThreadId, 400)                                                             AS ThreadId,
            /* The agent's id and friendly name sit at the top of the record (2.4.11);
               inside CopilotEventData, where the schema once put them, they win if present. */
            LEFT(COALESCE(NULLIF(e.AgentId, N''), NULLIF(t.AgentId, N'')), 400)                AS AgentId,
            LEFT(COALESCE(NULLIF(e.AgentName, N''), NULLIF(t.AgentName, N'')), 400)            AS AgentName,
            /* ModelTransparencyDetails is a short fragment by the time we read
               it, not the whole record. */
            LEFT(JSON_VALUE(e.ModelTransparencyDetails, '$[0].ModelName'), 200)                AS ModelName,
            LEFT(JSON_VALUE(e.ModelTransparencyDetails, '$[0].ModelProviderName'), 200)        AS ModelProvider,
            LEFT(JSON_VALUE(e.ModelTransparencyDetails, '$[0].ModelVersion'), 32)              AS ModelVersion,

            /* every other scalar (2.2.0): typed, NULL where a value does not parse */
            TRY_CONVERT(SMALLINT, t.RecordType)                                               AS RecordType,
            LEFT(t.Operation, 64)                                                             AS Operation,
            TRY_CONVERT(UNIQUEIDENTIFIER, t.OrganizationId)                                   AS OrganizationId,
            TRY_CONVERT(UNIQUEIDENTIFIER, t.UserKey)                                          AS UserKey,
            TRY_CONVERT(TINYINT, t.UserType)                                                  AS UserType,
            TRY_CONVERT(TINYINT, t.[Version])                                                 AS [Version],
            LEFT(t.Workload, 32)                                                              AS Workload,
            LEFT(t.ClientIP, 64)                                                              AS ClientIP,
            LEFT(t.CopilotLogVersion, 32)                                                     AS CopilotLogVersion,
            LEFT(t.AgentVersion, 32)                                                          AS AgentVersion,
            LEFT(t.ApplicationName, 200)                                                      AS ApplicationName,
            LEFT(t.AgentPlatform, 64)                                                         AS AgentPlatform,
            LEFT(t.PlatformAgentId, 256)                                                      AS PlatformAgentId,
            TRY_CONVERT(UNIQUEIDENTIFIER, t.AgentBlueprintId)                                 AS AgentBlueprintId,
            TRY_CONVERT(TINYINT, t.SubjectType)                                               AS SubjectType,
            LEFT(t.ResultStatus, 64)                                                          AS ResultStatus,
            LEFT(t.AppAccessContext_APIId, 64)                                                AS AppAccessContext_APIId,
            TRY_CONVERT(UNIQUEIDENTIFIER, t.AppAccessContext_ClientAppId)                     AS AppAccessContext_ClientAppId,
            LEFT(r.AssociatedAdminUnits, 4000)                                                AS AssociatedAdminUnits,
            LEFT(r.AssociatedAdminUnitsNames, 4000)                                           AS AssociatedAdminUnitsNames,
            LEFT(e.LicenseType, 32)                                                           AS CopilotEventData_LicenseType,
            LEFT(e.ConversationId, 256)                                                       AS CopilotEventData_ConversationId,
            TRY_CONVERT(UNIQUEIDENTIFIER, e.CorrelationId)                                    AS CopilotEventData_CorrelationId,
            TRY_CONVERT(UNIQUEIDENTIFIER, e.SensitivityLabelId)                               AS CopilotEventData_SensitivityLabelId,
            LEFT(e.TargetAgentName, 200)                                                      AS CopilotEventData_TargetAgentName,
            LEFT(e.TargetPlatformAgentId, 256)                                                AS CopilotEventData_TargetPlatformAgentId,
            TRY_CONVERT(TINYINT, e.DLPEvaluationDeferred)                                     AS CopilotEventData_DLPEvaluationDeferred,
            e.MemoryUpdated                                                                   AS CopilotEventData_MemoryUpdated,
            LEFT(e.MemoryCorrelationId, 64)                                                   AS CopilotEventData_MemoryCorrelationId,
            TRY_CONVERT(UNIQUEIDENTIFIER, e.FabricCapacityId)                                 AS CopilotEventData_FabricCapacityId,
            LEFT(e.PlatformAgentId, 256)                                                      AS CopilotEventData_PlatformAgentId,
            LEFT(e.PlatformAgentType, 64)                                                     AS CopilotEventData_PlatformAgentType,
            e.IsSpawnTask                                                                     AS CopilotEventData_IsSpawnTask,
            TRY_CONVERT(UNIQUEIDENTIFIER, e.TaskId)                                           AS CopilotEventData_TaskId,
            /* the small arrays as JSON text, NULL when empty */
            NULLIF(e.AISystemPlugin, N'[]')                                                   AS CopilotEventData_AISystemPlugin,
            NULLIF(e.ModelTransparencyDetails, N'[]')                                         AS CopilotEventData_ModelTransparencyDetails,
            NULLIF(e.MessageIds, N'[]')                                                       AS CopilotEventData_MessageIds,
            NULLIF(e.GuardrailDetails, N'[]')                                                 AS CopilotEventData_GuardrailDetails,
            NULLIF(e.ParticipatingAgents, N'[]')                                              AS CopilotEventData_ParticipatingAgents,
            NULLIF(e.TargetTaskIds, N'[]')                                                    AS CopilotEventData_TargetTaskIds,
            NULLIF(e.SourceThreadIds, N'[]')                                                  AS CopilotEventData_SourceThreadIds,

            /* nested arrays, carried forward as fragments */
            e.AccessedResources,
            e.AISystemPlugin,
            e.Contexts,
            e.Messages
        INTO #batch
        FROM #raw AS r
        /* A record whose AuditData is absent or not JSON still yields a row,
           with the JSON-derived columns NULL. */
        OUTER APPLY OPENJSON(CASE WHEN ISJSON(r.AuditData) = 1 THEN r.AuditData ELSE N'{}' END)
            WITH (
                CreationTime     NVARCHAR(64)   '$.CreationTime',
                JsonUserId       NVARCHAR(4000) '$.UserId',
                AppIdentity      NVARCHAR(200)  '$.AppIdentity',
                AgentId          NVARCHAR(400)  '$.AgentId',
                AgentName        NVARCHAR(400)  '$.AgentName',
                ClientRegion     NVARCHAR(100)  '$.ClientRegion',
                RecordType       NVARCHAR(20)   '$.RecordType',
                Operation        NVARCHAR(100)  '$.Operation',
                OrganizationId   NVARCHAR(100)  '$.OrganizationId',
                UserKey          NVARCHAR(100)  '$.UserKey',
                UserType         NVARCHAR(20)   '$.UserType',
                [Version]        NVARCHAR(20)   '$.Version',
                Workload         NVARCHAR(100)  '$.Workload',
                ClientIP         NVARCHAR(100)  '$.ClientIP',
                CopilotLogVersion NVARCHAR(100) '$.CopilotLogVersion',
                AgentVersion     NVARCHAR(100)  '$.AgentVersion',
                ApplicationName  NVARCHAR(400)  '$.ApplicationName',
                AgentPlatform    NVARCHAR(100)  '$.AgentPlatform',
                PlatformAgentId  NVARCHAR(400)  '$.PlatformAgentId',
                AgentBlueprintId NVARCHAR(100)  '$.AgentBlueprintId',
                SubjectType      NVARCHAR(20)   '$.SubjectType',
                ResultStatus     NVARCHAR(100)  '$.ResultStatus',
                AppAccessContext_APIId       NVARCHAR(100) '$.AppAccessContext.APIId',
                AppAccessContext_ClientAppId NVARCHAR(100) '$.AppAccessContext.ClientAppId',
                CopilotEventData NVARCHAR(MAX)  '$.CopilotEventData' AS JSON
            ) AS t
        OUTER APPLY OPENJSON(COALESCE(t.CopilotEventData, N'{}'))
            WITH (
                AppHost                  NVARCHAR(4000) '$.AppHost',
                ThreadId                 NVARCHAR(400)  '$.ThreadId',
                AgentId                  NVARCHAR(400)  '$.AgentId',
                AgentName                NVARCHAR(400)  '$.AgentName',
                LicenseType              NVARCHAR(100)  '$.LicenseType',
                ConversationId           NVARCHAR(400)  '$.ConversationId',
                CorrelationId            NVARCHAR(100)  '$.CorrelationId',
                SensitivityLabelId       NVARCHAR(100)  '$.SensitivityLabelId',
                TargetAgentName          NVARCHAR(400)  '$.TargetAgentName',
                TargetPlatformAgentId    NVARCHAR(400)  '$.TargetPlatformAgentId',
                DLPEvaluationDeferred    NVARCHAR(20)   '$.DLPEvaluationDeferred',
                MemoryUpdated            BIT            '$.MemoryUpdated',
                MemoryCorrelationId      NVARCHAR(100)  '$.MemoryCorrelationId',
                FabricCapacityId         NVARCHAR(100)  '$.FabricCapacityId',
                PlatformAgentId          NVARCHAR(400)  '$.PlatformAgentId',
                PlatformAgentType        NVARCHAR(100)  '$.PlatformAgentType',
                IsSpawnTask              BIT            '$.IsSpawnTask',
                TaskId                   NVARCHAR(100)  '$.TaskId',
                ModelTransparencyDetails NVARCHAR(MAX)  '$.ModelTransparencyDetails' AS JSON,
                AccessedResources        NVARCHAR(MAX)  '$.AccessedResources'        AS JSON,
                AISystemPlugin           NVARCHAR(MAX)  '$.AISystemPlugin'           AS JSON,
                Contexts                 NVARCHAR(MAX)  '$.Contexts'                 AS JSON,
                Messages                 NVARCHAR(MAX)  '$.Messages'                 AS JSON,
                MessageIds               NVARCHAR(MAX)  '$.MessageIds'               AS JSON,
                GuardrailDetails         NVARCHAR(MAX)  '$.GuardrailDetails'         AS JSON,
                ParticipatingAgents      NVARCHAR(MAX)  '$.ParticipatingAgents'      AS JSON,
                TargetTaskIds            NVARCHAR(MAX)  '$.TargetTaskIds'            AS JSON,
                SourceThreadIds          NVARCHAR(MAX)  '$.SourceThreadIds'          AS JSON
            ) AS e
        OPTION (MAXDOP 0);

        CREATE CLUSTERED INDEX CIX_batch ON #batch (RecordId);

        /* The fields that never vary: counted by combination, not stored per row. */
        MERGE audit.RawLogRecordShape AS t
        USING (
            SELECT h.ShapeHash, MAX(b.RecordType) AS RecordType, MAX(b.Operation) AS Operation, MAX(b.Workload) AS Workload,
                   MAX(b.UserType) AS UserType, MAX(b.[Version]) AS [Version], MAX(b.CopilotLogVersion) AS CopilotLogVersion,
                   MAX(b.SubjectType) AS SubjectType, MAX(b.AgentVersion) AS AgentVersion, MAX(b.AgentPlatform) AS AgentPlatform,
                   MAX(b.AgentBlueprintId) AS AgentBlueprintId, MAX(b.AppAccessContext_ClientAppId) AS ClientAppId,
                   MAX(b.CopilotEventData_PlatformAgentType) AS PlatformAgentType,
                   CAST(MAX(CAST(b.CopilotEventData_IsSpawnTask AS TINYINT)) AS BIT) AS IsSpawnTask,
                   CAST(MAX(CAST(f.HasAdminUnits AS TINYINT)) AS BIT) AS HasAdminUnits, CAST(MAX(CAST(f.HasMessageIds AS TINYINT)) AS BIT) AS HasMessageIds,
                   CAST(MAX(CAST(f.HasGuardrailDetails AS TINYINT)) AS BIT) AS HasGuardrailDetails, CAST(MAX(CAST(f.HasTargetTaskIds AS TINYINT)) AS BIT) AS HasTargetTaskIds, COUNT_BIG(*) AS Records
            FROM #batch AS b
            CROSS APPLY (SELECT CAST(CASE WHEN NULLIF(b.AssociatedAdminUnits, N'') IS NOT NULL OR NULLIF(b.AssociatedAdminUnitsNames, N'') IS NOT NULL THEN 1 ELSE 0 END AS BIT) AS HasAdminUnits,
                                CAST(CASE WHEN b.CopilotEventData_MessageIds IS NOT NULL THEN 1 ELSE 0 END AS BIT) AS HasMessageIds,
                                CAST(CASE WHEN b.CopilotEventData_GuardrailDetails IS NOT NULL THEN 1 ELSE 0 END AS BIT) AS HasGuardrailDetails,
                                CAST(CASE WHEN b.CopilotEventData_TargetTaskIds IS NOT NULL THEN 1 ELSE 0 END AS BIT) AS HasTargetTaskIds) AS f
            CROSS APPLY (SELECT CAST(HASHBYTES('SHA2_256', CONCAT_WS(N'|', ISNULL(CAST(b.RecordType AS NVARCHAR(10)), N'~'),
                    ISNULL(b.Operation, N'~'), ISNULL(b.Workload, N'~'), ISNULL(CAST(b.UserType AS NVARCHAR(5)), N'~'),
                    ISNULL(CAST(b.[Version] AS NVARCHAR(5)), N'~'), ISNULL(b.CopilotLogVersion, N'~'),
                    ISNULL(CAST(b.SubjectType AS NVARCHAR(5)), N'~'), ISNULL(b.AgentVersion, N'~'), ISNULL(b.AgentPlatform, N'~'),
                    ISNULL(CAST(b.AgentBlueprintId AS NVARCHAR(36)), N'~'), ISNULL(CAST(b.AppAccessContext_ClientAppId AS NVARCHAR(36)), N'~'),
                    ISNULL(b.CopilotEventData_PlatformAgentType, N'~'), ISNULL(CAST(b.CopilotEventData_IsSpawnTask AS NVARCHAR(1)), N'~'),
                    f.HasAdminUnits, f.HasMessageIds, f.HasGuardrailDetails, f.HasTargetTaskIds)) AS BINARY(32)) AS ShapeHash) AS h
            GROUP BY h.ShapeHash
        ) AS s
        ON t.ShapeHash = s.ShapeHash
        WHEN MATCHED THEN UPDATE SET t.Records += s.Records, t.LastSeenUtc = SYSUTCDATETIME()
        WHEN NOT MATCHED THEN INSERT
            (ShapeHash, RecordType, Operation, Workload, UserType, [Version], CopilotLogVersion, SubjectType, AgentVersion,
             AgentPlatform, AgentBlueprintId, AppAccessContext_ClientAppId, CopilotEventData_PlatformAgentType, CopilotEventData_IsSpawnTask,
             HasAdminUnits, HasMessageIds, HasGuardrailDetails, HasTargetTaskIds, Records)
        VALUES (s.ShapeHash, s.RecordType, s.Operation, s.Workload, s.UserType, s.[Version], s.CopilotLogVersion, s.SubjectType,
                s.AgentVersion, s.AgentPlatform, s.AgentBlueprintId, s.ClientAppId, s.PlatformAgentType, s.IsSpawnTask,
                s.HasAdminUnits, s.HasMessageIds, s.HasGuardrailDetails, s.HasTargetTaskIds, s.Records);

        /* ---- 2. AccessedResources, exploded once ------------------------ */

        SELECT b.RecordId,
               CAST(j.[key] AS SMALLINT)                                          AS Ordinal,
               /*
                   A precomputed plain column, because SQL Server only
                   reconciles several ordered aggregates in one scope when the
                   ORDER BY is a bare column reference. Any expression there -
                   LEFT(SiteUrl, 400) included - makes each aggregate's
                   ordering "incompatible" with the others even when the
                   expressions are identical. Materialising the key keeps the
                   sort cheap and the aggregates legal. CONCAT never returns
                   NULL, so the ordering stays deterministic.
               */
               CAST(LEFT(CONCAT(a.SiteUrl, N'|', a.Name), 400) AS NVARCHAR(400)) AS SortKey,
               a.Name,
               a.SiteUrl,
               a.[Action],
               a.SensitivityLabelId,
               a.XPIADetected,
               a.[Status],
               a.[Type],
               a.Id,
               a.ListItemUniqueId,
               /* Real records carry PolicyDetails as JSON text inside the JSON; older
                  shapes as an array. Either way it is kept as text. */
               COALESCE(NULLIF(a.PolicyDetailsText, N''), a.PolicyDetailsJson)   AS PolicyDetails,
               CAST(NULL AS NVARCHAR(400))                                        AS PolicyOutcomes
        INTO #res
        FROM #batch AS b
        CROSS APPLY OPENJSON(COALESCE(b.AccessedResources, N'[]')) AS j
        CROSS APPLY OPENJSON(j.[value])
            WITH (
                Name               NVARCHAR(MAX)  '$.Name',
                SiteUrl            NVARCHAR(MAX)  '$.SiteUrl',
                [Action]           NVARCHAR(4000) '$.Action',
                SensitivityLabelId NVARCHAR(4000) '$.SensitivityLabelId',
                XPIADetected       BIT            '$.XPIADetected',
                [Status]           NVARCHAR(100)  '$.Status',
                [Type]             NVARCHAR(400)  '$.Type',
                Id                 NVARCHAR(2000) '$.Id',
                ListItemUniqueId   NVARCHAR(100)  '$.listItemUniqueId',
                PolicyDetailsText  NVARCHAR(MAX)  '$.PolicyDetails',
                PolicyDetailsJson  NVARCHAR(MAX)  '$.PolicyDetails' AS JSON
            ) AS a
        WHERE j.[type] = 5;   -- objects only

        CREATE CLUSTERED INDEX CIX_res ON #res (RecordId, Ordinal);

        /* Policy outcomes other than None. Nearly every element says None, so
           only the rare ones that say something else are parsed. */
        UPDATE r SET PolicyOutcomes = o.Outcomes
        FROM #res AS r
        CROSS APPLY (
            SELECT LEFT(STRING_AGG(CAST(v.[value] AS NVARCHAR(MAX)), N'; '), 400) AS Outcomes
            FROM OPENJSON(r.PolicyDetails) WITH (PolicyOutcomes NVARCHAR(MAX) '$.PolicyOutcomes' AS JSON) AS pd
            CROSS APPLY OPENJSON(pd.PolicyOutcomes) AS v
            WHERE v.[value] NOT IN (N'None', N'')
        ) AS o
        WHERE r.PolicyDetails LIKE N'%PolicyOutcomes%'
          AND REPLACE(r.PolicyDetails, N'"PolicyOutcomes":["None"]', N'') LIKE N'%PolicyOutcomes%'
          AND ISJSON(r.PolicyDetails) = 1;

        /* ---- 3. Aggregate, all of it relational -------------------------

            Each aggregate is its own statement writing its own temp table,
            not a CTE. Two reasons, and both matter:

              * SQL Server rejects ordered aggregates with differing ORDER BY
                clauses "in the same scope", and CTEs feeding one statement
                count as that scope once they are flattened. Separate
                statements are unambiguously separate scopes, so each list can
                keep the ordering that suits it.
              * A CTE is not materialised, so referencing one repeatedly
                re-runs the OPENJSON underneath it - the cost this rewrite
                exists to remove.

            Everything below reads #batch or #res, which are already parsed.
        */

        SELECT RecordId,
               STRING_AGG(CASE WHEN Name IS NOT NULL THEN CAST(Name AS NVARCHAR(MAX)) END, N'; ')
                   WITHIN GROUP (ORDER BY SortKey)                       AS ResourceNames,
               STRING_AGG(CASE WHEN SiteUrl IS NOT NULL THEN CAST(SiteUrl AS NVARCHAR(MAX)) END, N'; ')
                   WITHIN GROUP (ORDER BY SortKey)                       AS ResourceUrls,
               STRING_AGG(CASE WHEN SiteUrl IS NOT NULL
                                AND (PolicyOutcomes IS NOT NULL
                                     OR ([Status] IS NOT NULL AND [Status] <> N'Success'))
                               THEN CAST(SiteUrl AS NVARCHAR(MAX)) END, N'; ')
                   WITHIN GROUP (ORDER BY SortKey)                       AS BlockedUrls,
               MAX(CASE WHEN XPIADetected = 1 THEN 1 ELSE 0 END)                 AS AnyXpia,
               COUNT(*)                                                          AS ResourceCount
        INTO #resagg
        FROM #res
        GROUP BY RecordId;

        /* STRING_AGG has no DISTINCT, so dedupe first. No JSON is touched. */
        SELECT RecordId,
               STRING_AGG(CAST([Action] AS NVARCHAR(MAX)), N'; ')
                   WITHIN GROUP (ORDER BY [Action])                             AS Actions
        INTO #actagg
        FROM (SELECT DISTINCT RecordId, [Action] FROM #res WHERE [Action] IS NOT NULL) AS d
        GROUP BY RecordId;

        SELECT RecordId,
               STRING_AGG(CAST(SensitivityLabelId AS NVARCHAR(MAX)), N'; ')
                   WITHIN GROUP (ORDER BY SensitivityLabelId)                   AS Labels
        INTO #labagg
        FROM (SELECT DISTINCT RecordId, SensitivityLabelId FROM #res WHERE SensitivityLabelId IS NOT NULL) AS d
        GROUP BY RecordId;

        /* The three short fragments, each exploded once and aggregated set-based
           rather than per row in a correlated subquery. */
        SELECT b.RecordId,
               LEFT(STRING_AGG(CAST(c.Id AS NVARCHAR(MAX)), N'; ')
                   WITHIN GROUP (ORDER BY c.Id)          , 4000)                AS Contexts
        INTO #ctxagg
        FROM #batch AS b
        CROSS APPLY OPENJSON(COALESCE(b.Contexts, N'[]')) WITH (Id NVARCHAR(MAX) '$.Id') AS c
        WHERE c.Id IS NOT NULL
        GROUP BY b.RecordId;

        SELECT RecordId,
               STRING_AGG(CAST(Id AS NVARCHAR(MAX)), N'; ')
                   WITHIN GROUP (ORDER BY Id)                                   AS Plugins
        INTO #plugagg
        FROM (
            SELECT DISTINCT b.RecordId, p.Id
            FROM #batch AS b
            CROSS APPLY OPENJSON(COALESCE(b.AISystemPlugin, N'[]')) WITH (Id NVARCHAR(4000) '$.Id') AS p
            WHERE p.Id IS NOT NULL
        ) AS d
        GROUP BY RecordId;

        /* Messages answers two questions, so it is read once and pivoted. */
        SELECT b.RecordId,
               CASE WHEN MAX(CASE WHEN x.JailbreakDetected = 1 THEN 1 ELSE 0 END) = 1
                    THEN N'true' ELSE N'false' END                               AS JailbreakDetected,
               COUNT(CASE WHEN x.isPrompt = 1 THEN 1 END)                        AS PromptCount,
               COUNT(x.isPrompt)                                                 AS MessageCount
        INTO #msgagg
        FROM #batch AS b
        CROSS APPLY OPENJSON(COALESCE(b.Messages, N'[]'))
             WITH (isPrompt BIT '$.isPrompt', JailbreakDetected BIT '$.JailbreakDetected') AS x
        GROUP BY b.RecordId;

        /* ---- 4. Merge. No aggregates left in this statement. ------------ */

        MERGE audit.RawLogFlattened WITH (HOLDLOCK) AS t
        USING (
            SELECT
                b.RecordId,
                b.[TimeStamp],
                b.UtcTimeStamp,
                b.[User],
                b.App,
                b.Location,
                b.AppIdentity,
                b.ThreadId,
                b.AgentId,
                b.AgentName,
                b.ModelName,
                b.ModelProvider,
                b.ModelVersion,
                b.OrganizationId,
                b.UserKey,
                b.ClientIP,
                b.ApplicationName,
                b.PlatformAgentId,
                b.AppAccessContext_APIId,
                b.ResultStatus,
                b.CopilotEventData_LicenseType,
                b.CopilotEventData_ConversationId,
                b.CopilotEventData_CorrelationId,
                b.CopilotEventData_SensitivityLabelId,
                b.CopilotEventData_TargetAgentName,
                b.CopilotEventData_TargetPlatformAgentId,
                b.CopilotEventData_DLPEvaluationDeferred,
                b.CopilotEventData_MemoryUpdated,
                b.CopilotEventData_MemoryCorrelationId,
                b.CopilotEventData_FabricCapacityId,
                b.CopilotEventData_PlatformAgentId,
                b.CopilotEventData_TaskId,
                b.CopilotEventData_AISystemPlugin,
                b.CopilotEventData_ModelTransparencyDetails,
                b.CopilotEventData_ParticipatingAgents,
                b.CopilotEventData_SourceThreadIds,
                ISNULL(mg.MessageCount, 0) AS MessageCount,
                cx.Contexts                                                      AS [App context],
                ra.ResourceNames                                                 AS [Accessed Resources],
                ra.ResourceUrls                                                  AS [Accessed Resource Locations],
                ra.BlockedUrls                                                   AS BlockedResourceUrls,
                aa.Actions                                                       AS [Action],
                la.Labels                                                        AS SensitivityLabelIds,
                pl.Plugins                                                       AS AIPlugin,
                ISNULL(mg.JailbreakDetected, N'false')                           AS JailbreakDetected,
                ISNULL(mg.PromptCount, 0)                                        AS PromptCount,
                CASE WHEN ISNULL(ra.AnyXpia, 0) = 1 THEN N'true' ELSE N'false' END AS PromptInjectionDetected,
                ISNULL(ra.ResourceCount, 0)                                      AS AccessedResourceCount,
                CASE WHEN @hasLicense = 1 THEN lic.IsCopilotLic END              AS IsCopilotLic
            FROM #batch AS b
            LEFT JOIN #resagg  AS ra ON ra.RecordId = b.RecordId
            LEFT JOIN #actagg  AS aa ON aa.RecordId = b.RecordId
            LEFT JOIN #labagg  AS la ON la.RecordId = b.RecordId
            LEFT JOIN #ctxagg  AS cx ON cx.RecordId = b.RecordId
            LEFT JOIN #plugagg AS pl ON pl.RecordId = b.RecordId
            LEFT JOIN #msgagg  AS mg ON mg.RecordId = b.RecordId
            OUTER APPLY (
                SELECT TOP (1) l.IsCopilotLic
                FROM audit.CopilotLicense AS l
                WHERE @hasLicense = 1 AND l.UserPrincipalName = b.[User]
            ) AS lic
        ) AS s
        ON t.RecordId = s.RecordId
        WHEN MATCHED THEN UPDATE SET
            t.[TimeStamp]                   = s.[TimeStamp],
            t.UtcTimeStamp                  = s.UtcTimeStamp,
            t.[User]                        = s.[User],
            t.App                           = s.App,
            t.Location                      = s.Location,
            t.[App context]                 = s.[App context],
            t.[Accessed Resources]          = s.[Accessed Resources],
            t.[Accessed Resource Locations] = s.[Accessed Resource Locations],
            t.[Action]                      = s.[Action],
            t.AIPlugin                      = s.AIPlugin,
            t.JailbreakDetected             = s.JailbreakDetected,
            t.PromptInjectionDetected       = s.PromptInjectionDetected,
            t.BlockedResourceUrls           = s.BlockedResourceUrls,
            t.SensitivityLabelIds           = s.SensitivityLabelIds,
            t.IsCopilotLic                  = s.IsCopilotLic,
            t.AppIdentity                   = s.AppIdentity,
            t.ThreadId                      = s.ThreadId,
            t.AgentId                       = s.AgentId,
            t.AgentName                     = s.AgentName,
            t.ModelName                     = s.ModelName,
            t.ModelProvider                 = s.ModelProvider,
            t.AccessedResourceCount         = s.AccessedResourceCount,
            t.PromptCount                   = s.PromptCount,
            t.ModelVersion = s.ModelVersion,
            t.OrganizationId = s.OrganizationId,
            t.UserKey = s.UserKey,
            t.ClientIP = s.ClientIP,
            t.ApplicationName = s.ApplicationName,
            t.PlatformAgentId = s.PlatformAgentId,
            t.AppAccessContext_APIId = s.AppAccessContext_APIId,
            t.ResultStatus = s.ResultStatus,
            t.CopilotEventData_LicenseType = s.CopilotEventData_LicenseType,
            t.CopilotEventData_ConversationId = s.CopilotEventData_ConversationId,
            t.CopilotEventData_CorrelationId = s.CopilotEventData_CorrelationId,
            t.CopilotEventData_SensitivityLabelId = s.CopilotEventData_SensitivityLabelId,
            t.CopilotEventData_TargetAgentName = s.CopilotEventData_TargetAgentName,
            t.CopilotEventData_TargetPlatformAgentId = s.CopilotEventData_TargetPlatformAgentId,
            t.CopilotEventData_DLPEvaluationDeferred = s.CopilotEventData_DLPEvaluationDeferred,
            t.CopilotEventData_MemoryUpdated = s.CopilotEventData_MemoryUpdated,
            t.CopilotEventData_MemoryCorrelationId = s.CopilotEventData_MemoryCorrelationId,
            t.CopilotEventData_FabricCapacityId = s.CopilotEventData_FabricCapacityId,
            t.CopilotEventData_PlatformAgentId = s.CopilotEventData_PlatformAgentId,
            t.CopilotEventData_TaskId = s.CopilotEventData_TaskId,
            t.CopilotEventData_AISystemPlugin = s.CopilotEventData_AISystemPlugin,
            t.CopilotEventData_ModelTransparencyDetails = s.CopilotEventData_ModelTransparencyDetails,
            t.CopilotEventData_ParticipatingAgents = s.CopilotEventData_ParticipatingAgents,
            t.CopilotEventData_SourceThreadIds = s.CopilotEventData_SourceThreadIds,
            t.MessageCount = s.MessageCount,
            t.FlattenedUtc                  = SYSUTCDATETIME()
        WHEN NOT MATCHED BY TARGET THEN INSERT
        (
            RecordId, [TimeStamp], UtcTimeStamp, [User], App, Location, [App context],
            [Accessed Resources], [Accessed Resource Locations], [Action], AIPlugin,
            JailbreakDetected, PromptInjectionDetected, BlockedResourceUrls, SensitivityLabelIds,
            IsCopilotLic, AppIdentity, ThreadId, AgentId, AgentName, ModelName, ModelProvider,
            AccessedResourceCount, PromptCount,
            ModelVersion, OrganizationId, UserKey, ClientIP, ApplicationName, PlatformAgentId, AppAccessContext_APIId, ResultStatus, CopilotEventData_LicenseType, CopilotEventData_ConversationId, CopilotEventData_CorrelationId, CopilotEventData_SensitivityLabelId, CopilotEventData_TargetAgentName, CopilotEventData_TargetPlatformAgentId, CopilotEventData_DLPEvaluationDeferred, CopilotEventData_MemoryUpdated, CopilotEventData_MemoryCorrelationId, CopilotEventData_FabricCapacityId, CopilotEventData_PlatformAgentId, CopilotEventData_TaskId, CopilotEventData_AISystemPlugin, CopilotEventData_ModelTransparencyDetails, CopilotEventData_ParticipatingAgents, CopilotEventData_SourceThreadIds, MessageCount
        )
        VALUES
        (
            s.RecordId, s.[TimeStamp], s.UtcTimeStamp, s.[User], s.App, s.Location, s.[App context],
            s.[Accessed Resources], s.[Accessed Resource Locations], s.[Action], s.AIPlugin,
            s.JailbreakDetected, s.PromptInjectionDetected, s.BlockedResourceUrls, s.SensitivityLabelIds,
            s.IsCopilotLic, s.AppIdentity, s.ThreadId, s.AgentId, s.AgentName, s.ModelName, s.ModelProvider,
            s.AccessedResourceCount, s.PromptCount,
            s.ModelVersion, s.OrganizationId, s.UserKey, s.ClientIP, s.ApplicationName, s.PlatformAgentId, s.AppAccessContext_APIId, s.ResultStatus, s.CopilotEventData_LicenseType, s.CopilotEventData_ConversationId, s.CopilotEventData_CorrelationId, s.CopilotEventData_SensitivityLabelId, s.CopilotEventData_TargetAgentName, s.CopilotEventData_TargetPlatformAgentId, s.CopilotEventData_DLPEvaluationDeferred, s.CopilotEventData_MemoryUpdated, s.CopilotEventData_MemoryCorrelationId, s.CopilotEventData_FabricCapacityId, s.CopilotEventData_PlatformAgentId, s.CopilotEventData_TaskId, s.CopilotEventData_AISystemPlugin, s.CopilotEventData_ModelTransparencyDetails, s.CopilotEventData_ParticipatingAgents, s.CopilotEventData_SourceThreadIds, s.MessageCount
        );

        SET @rows = @@ROWCOUNT;

        /* ---- 5. The element tables: this batch's rows replaced ---------- */

        DELETE ar FROM audit.RawLogAccessedResource AS ar JOIN #batch AS b ON b.RecordId = ar.RecordId;
        INSERT INTO audit.RawLogAccessedResource
            (RecordId, Ordinal, [Action], [Type], Id, Name, SiteUrl, Site, ListItemUniqueId, SensitivityLabelId,
             XPIADetected, [Status], PolicyDetails, PolicyOutcomes, IsBlocked)
        SELECT r.RecordId, r.Ordinal, LEFT(r.[Action], 450), LEFT(r.[Type], 200), LEFT(r.Id, 1000), LEFT(r.Name, 4000),
               NULLIF(r.SiteUrl, N''), LEFT(NULLIF(st.Site, N''), 400),
               TRY_CONVERT(UNIQUEIDENTIFIER, r.ListItemUniqueId), TRY_CONVERT(UNIQUEIDENTIFIER, r.SensitivityLabelId),
               r.XPIADetected, LEFT(r.[Status], 64), NULLIF(r.PolicyDetails, N''), r.PolicyOutcomes,
               CASE WHEN r.PolicyOutcomes IS NOT NULL OR (r.[Status] IS NOT NULL AND r.[Status] <> N'Success') THEN 1 ELSE 0 END
        FROM #res AS r
        /* The site: https://host/sites/name (or /personal/, /teams/), otherwise https://host. */
        CROSS APPLY (SELECT CHARINDEX(N'/', r.SiteUrl + N'/', 9) AS p) AS p1
        CROSS APPLY (SELECT CASE WHEN p1.p = 0 THEN 0 ELSE CHARINDEX(N'/', r.SiteUrl + N'/', p1.p + 1) END AS p) AS p2
        CROSS APPLY (SELECT CASE WHEN p2.p = 0 THEN 0 ELSE CHARINDEX(N'/', r.SiteUrl + N'/', p2.p + 1) END AS p) AS p3
        CROSS APPLY (SELECT CASE WHEN r.SiteUrl IS NULL THEN NULL
                                 WHEN SUBSTRING(r.SiteUrl, p1.p, 10) LIKE N'/sites/%' OR SUBSTRING(r.SiteUrl, p1.p, 10) LIKE N'/teams/%' OR SUBSTRING(r.SiteUrl, p1.p, 10) = N'/personal/'
                                      THEN CASE WHEN p3.p = 0 THEN r.SiteUrl ELSE LEFT(r.SiteUrl, p3.p - 1) END
                                 ELSE CASE WHEN p1.p = 0 THEN r.SiteUrl ELSE LEFT(r.SiteUrl, p1.p - 1) END END AS Site) AS st;

        DELETE rc FROM audit.RawLogContext AS rc JOIN #batch AS b ON b.RecordId = rc.RecordId;
        INSERT INTO audit.RawLogContext (RecordId, Ordinal, Id, [Type], ContainerId)
        SELECT b.RecordId, CAST(j.[key] AS SMALLINT), LEFT(c.Id, 4000), LEFT(c.[Type], 64), LEFT(c.ContainerId, 64)
        FROM #batch AS b
        CROSS APPLY OPENJSON(COALESCE(b.Contexts, N'[]')) AS j
        CROSS APPLY OPENJSON(j.[value]) WITH (Id NVARCHAR(MAX) '$.Id', [Type] NVARCHAR(200) '$.Type', ContainerId NVARCHAR(200) '$.ContainerId') AS c
        WHERE j.[type] = 5;

        /* This batch's values into the catalog, for Admin > Value names. */
        IF OBJECT_ID(N'audit.usp_CatalogValues', N'P') IS NOT NULL EXEC audit.usp_CatalogValues;

        /* And its agents, with the latest friendly name the log gave each (2.4.11). */
        EXEC audit.usp_CatalogAgents;

        SET @done += @rows;
        SET @cursor = @maxId;

        DROP TABLE IF EXISTS #batch;
        DROP TABLE IF EXISTS #raw;
        DROP TABLE IF EXISTS #res;
        DROP TABLE IF EXISTS #resagg;
        DROP TABLE IF EXISTS #actagg;
        DROP TABLE IF EXISTS #labagg;
        DROP TABLE IF EXISTS #ctxagg;
        DROP TABLE IF EXISTS #plugagg;
        DROP TABLE IF EXISTS #msgagg;

        SET @msg = CONCAT(N'  batch ', @batch, N': ', FORMAT(@rows, 'N0'), N' record(s), ',
                          FORMAT(@done, 'N0'), N' so far.');
        RAISERROR(@msg, 0, 1) WITH NOWAIT;
        SET @batch += 1;
    END;

    IF @hasLicense = 0
        RAISERROR('audit.CopilotLicense does not exist - IsCopilotLic left NULL.', 0, 1) WITH NOWAIT;

    /* Present or absent is expected (several appear only with an agent); a second value is news. */
    DECLARE @varying NVARCHAR(400);
    SELECT @varying = CONCAT_WS(N', ',
        CASE WHEN COUNT(DISTINCT RecordType) > 1 THEN N'RecordType' END, CASE WHEN COUNT(DISTINCT Operation) > 1 THEN N'Operation' END,
        CASE WHEN COUNT(DISTINCT Workload) > 1 THEN N'Workload' END, CASE WHEN COUNT(DISTINCT UserType) > 1 THEN N'UserType' END,
        CASE WHEN COUNT(DISTINCT [Version]) > 1 THEN N'Version' END, CASE WHEN COUNT(DISTINCT CopilotLogVersion) > 1 THEN N'CopilotLogVersion' END,
        CASE WHEN COUNT(DISTINCT SubjectType) > 1 THEN N'SubjectType' END, CASE WHEN COUNT(DISTINCT AgentVersion) > 1 THEN N'AgentVersion' END,
        CASE WHEN COUNT(DISTINCT AgentPlatform) > 1 THEN N'AgentPlatform' END, CASE WHEN COUNT(DISTINCT AgentBlueprintId) > 1 THEN N'AgentBlueprintId' END,
        CASE WHEN COUNT(DISTINCT AppAccessContext_ClientAppId) > 1 THEN N'AppAccessContext.ClientAppId' END,
        CASE WHEN COUNT(DISTINCT CopilotEventData_PlatformAgentType) > 1 THEN N'CopilotEventData.PlatformAgentType' END,
        CASE WHEN COUNT(DISTINCT CAST(CopilotEventData_IsSpawnTask AS TINYINT)) > 1 THEN N'CopilotEventData.IsSpawnTask' END,
        CASE WHEN MAX(CAST(HasAdminUnits AS TINYINT)) = 1 THEN N'AssociatedAdminUnits' END,
        CASE WHEN MAX(CAST(HasMessageIds AS TINYINT)) = 1 THEN N'CopilotEventData.MessageIds' END,
        CASE WHEN MAX(CAST(HasGuardrailDetails AS TINYINT)) = 1 THEN N'CopilotEventData.GuardrailDetails' END,
        CASE WHEN MAX(CAST(HasTargetTaskIds AS TINYINT)) = 1 THEN N'CopilotEventData.TargetTaskIds' END)
    FROM audit.RawLogRecordShape;
    IF @varying <> N''
    BEGIN
        SET @msg = CONCAT(N'Note: ', @varying, N' now carry information - a second value, or something where there was ',
                          N'always nothing (audit.RawLogRecordShape). They are not stored per row; if it matters, it needs a column.');
        RAISERROR(@msg, 0, 1) WITH NOWAIT;
    END;

    /* Every record was flattened by this version: none is left for audit.usp_BackfillAgentNames to read, either way.
       And the mark that every record has been flattened (2.4.12): Install-CopilotAudit.ps1 reads it, where it read
       "the flattened table has rows", which a first flatten stopped part way, or a nightly's few days, also gave. */
    IF @SinceUtc IS NULL AND OBJECT_ID(N'audit.PipelineSetting', N'U') IS NOT NULL
    BEGIN
        IF NOT EXISTS (SELECT 1 FROM audit.PipelineSetting WHERE Name = N'AgentNamesReadUtc')
            INSERT INTO audit.PipelineSetting (Name, Value) VALUES (N'AgentNamesReadUtc', CONVERT(NVARCHAR(30), SYSUTCDATETIME(), 126));
        IF NOT EXISTS (SELECT 1 FROM audit.PipelineSetting WHERE Name = N'AgentIdsReadUtc')
            INSERT INTO audit.PipelineSetting (Name, Value) VALUES (N'AgentIdsReadUtc', CONVERT(NVARCHAR(30), SYSUTCDATETIME(), 126));
        /* Nor is anything left of a read that stopped part way (2.4.12). */
        DELETE FROM audit.PipelineSetting WHERE Name IN (N'AgentNamesReadUtcCursor', N'AgentNamesReadUtcGained', N'AgentIdsReadUtcCursor', N'AgentIdsReadUtcGained');
        UPDATE audit.PipelineSetting SET Value = CONVERT(NVARCHAR(30), SYSUTCDATETIME(), 126), UpdatedUtc = SYSUTCDATETIME() WHERE Name = N'FullFlattenUtc';
        IF @@ROWCOUNT = 0 INSERT INTO audit.PipelineSetting (Name, Value) VALUES (N'FullFlattenUtc', CONVERT(NVARCHAR(30), SYSUTCDATETIME(), 126));
    END;

    SET @msg = CONCAT(N'Flatten complete: ', FORMAT(@done, 'N0'), N' record(s) in audit.RawLogFlattened.');
    RAISERROR(@msg, 0, 1) WITH NOWAIT;
END;
GO
```

### C.6 `sql/Create-CopilotPurgeProcedure.sql`

Copilot database. `audit.usp_PurgeCopilotAuditJson`, which clears the stored JSON of old records. 225 lines.

```sql
/*
    Copilot audit backfill - audit.usp_PurgeCopilotAuditJson
    ------------------------------------------------------------------------
    Retention for the bulky part of the archive: the compressed AuditData JSON.

    The JSON is what costs the disk (~1.5 KB per record; ~75 GB per 50M). The
    flattened and reporting tables are a fraction of that, so the usual policy
    is to drop the JSON past the retention horizon and keep the reporting rows
    indefinitely. @DeleteRows = 1 removes the records outright instead.

    Parameters:
      @RetainDays   required. Records with CreatedDateTime older than
                    SYSUTCDATETIME() - @RetainDays are in scope. Must be >= 1.
      @DeleteRows   0 (default) NULLs AuditDataGz and leaves the row, so
                    counts, the flattened table and reporting still work.
                    1 deletes the records: their rows in audit.CopilotAudit,
                    and their shredded rows in audit.CopilotInteraction (each
                    with the user, app, thread and time), which 2.4.12 added:
                    before, every one of those was kept.
      @PurgeStaging 1 (default) also clears matching rows from
                    audit.CopilotAuditStage, which holds a second copy of the
                    same JSON until it is dropped.
      @PurgeFlattened 0 (default). 1 also deletes the matching
                    audit.RawLogFlattened rows, and the resources and contexts
                    each one read (audit.RawLogAccessedResource and
                    audit.RawLogContext: file names, sites and links, which
                    2.4.12 added) - only with @DeleteRows = 1.
                    tools/Invoke-CopilotMaintenance.ps1 -DeleteRows passes it.
      @BatchSize    rows per transaction. Default 100,000.
      @WhatIf       1 (default) reports what would go and changes nothing.
                    Pass 0 to actually purge.

    What stays: the bi facts and the dashboard's rollups, built from the
    flattened rows, keep the records until bi is rebuilt in full and the
    rollups after it (Invoke-CopilotMaintenance.ps1 -Task RebuildBi).

    @WhatIf defaults to 1 on purpose: this procedure destroys data that cannot
    be re-fetched once past Purview's own 180-day retention.

    Re-runnable. Requires SQL Server 2017+.
*/
SET NOCOUNT ON;

IF OBJECT_ID(N'audit.usp_PurgeCopilotAuditJson', N'P') IS NULL
    EXEC (N'CREATE PROCEDURE audit.usp_PurgeCopilotAuditJson AS BEGIN SET NOCOUNT ON; END;');
GO

ALTER PROCEDURE audit.usp_PurgeCopilotAuditJson
    @RetainDays     INT,
    @DeleteRows     BIT = 0,
    @PurgeStaging   BIT = 1,
    @PurgeFlattened BIT = 0,
    @BatchSize      INT = 100000,
    @WhatIf         BIT = 1
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF @RetainDays IS NULL OR @RetainDays < 1
    BEGIN
        RAISERROR('Purge refused: @RetainDays is required and must be at least 1.', 16, 1);
        RETURN;
    END;
    IF @BatchSize IS NULL OR @BatchSize < 1000 SET @BatchSize = 1000;
    IF @PurgeFlattened = 1 AND @DeleteRows = 0
    BEGIN
        RAISERROR('Purge refused: @PurgeFlattened = 1 needs @DeleteRows = 1, otherwise the flattened rows would be rebuilt from records that still exist.', 16, 1);
        RETURN;
    END;

    DECLARE @cutoff DATETIME2(7) = DATEADD(DAY, -@RetainDays, SYSUTCDATETIME());
    DECLARE @msg NVARCHAR(400), @n BIGINT, @total BIGINT = 0;

    SET @msg = CONCAT(N'Cutoff: records created before ', CONVERT(NVARCHAR(30), @cutoff, 126), N' UTC (',
                      @RetainDays, N' day retention).');
    RAISERROR(@msg, 0, 1) WITH NOWAIT;

    /* ---- What is in scope --------------------------------------------- */

    DECLARE @scope BIGINT =
        (SELECT COUNT_BIG(*) FROM audit.CopilotAudit
         WHERE CreatedDateTime < @cutoff AND (@DeleteRows = 1 OR AuditDataGz IS NOT NULL));
    DECLARE @keep BIGINT = (SELECT COUNT_BIG(*) FROM audit.CopilotAudit WHERE CreatedDateTime >= @cutoff);

    SET @msg = CONCAT(N'audit.CopilotAudit: ', FORMAT(@scope, 'N0'), N' record(s) in scope, ',
                      FORMAT(@keep, 'N0'), N' newer record(s) untouched.');
    RAISERROR(@msg, 0, 1) WITH NOWAIT;

    IF @WhatIf = 1
    BEGIN
        SELECT @cutoff                                                             AS CutoffUtc,
               @scope                                                              AS RecordsInScope,
               @keep                                                               AS RecordsRetained,
               CASE WHEN @DeleteRows = 1 THEN 'delete rows' ELSE 'NULL AuditDataGz' END AS Action,
               (SELECT MIN(CreatedDateTime) FROM audit.CopilotAudit)               AS OldestRecordUtc,
               (SELECT COUNT_BIG(*) FROM audit.CopilotAuditStage AS s
                WHERE s.CreatedDateTime < @cutoff)                                 AS StagingRowsInScope;
        RAISERROR('@WhatIf = 1: nothing was changed. Pass @WhatIf = 0 to purge.', 0, 1) WITH NOWAIT;
        RETURN;
    END;

    /* ---- audit.CopilotAudit -------------------------------------------- */

    IF @DeleteRows = 1
    BEGIN
        RAISERROR('Deleting records from audit.CopilotAudit...', 0, 1) WITH NOWAIT;
        WHILE 1 = 1
        BEGIN
            DELETE TOP (@BatchSize) FROM audit.CopilotAudit WHERE CreatedDateTime < @cutoff;
            SET @n = @@ROWCOUNT;
            SET @total += @n;
            IF @n = 0 BREAK;
            SET @msg = CONCAT(N'  ', FORMAT(@total, 'N0'), N' deleted.');
            RAISERROR(@msg, 0, 1) WITH NOWAIT;
        END;

        /* 2.4.12: the shredded copy of each record goes with it. It names the user, the app, the thread and the time,
           and was kept for every record deleted, so the records were not removed altogether as this promises. */
        IF OBJECT_ID(N'audit.CopilotInteraction', N'U') IS NOT NULL
        BEGIN
            DECLARE @shredded BIGINT = 0;
            WHILE 1 = 1
            BEGIN
                DELETE TOP (@BatchSize) FROM audit.CopilotInteraction WHERE CreatedDateTime < @cutoff;
                SET @n = @@ROWCOUNT;
                SET @shredded += @n;
                IF @n = 0 BREAK;
            END;
            SET @msg = CONCAT(N'  ', FORMAT(@shredded, 'N0'), N' shredded row(s) deleted from audit.CopilotInteraction.');
            RAISERROR(@msg, 0, 1) WITH NOWAIT;
        END;
    END
    ELSE
    BEGIN
        RAISERROR('Clearing AuditDataGz on aged records (rows are kept)...', 0, 1) WITH NOWAIT;
        WHILE 1 = 1
        BEGIN
            UPDATE TOP (@BatchSize) audit.CopilotAudit
               SET AuditDataGz = NULL
             WHERE CreatedDateTime < @cutoff AND AuditDataGz IS NOT NULL;
            SET @n = @@ROWCOUNT;
            SET @total += @n;
            IF @n = 0 BREAK;
            SET @msg = CONCAT(N'  ', FORMAT(@total, 'N0'), N' cleared.');
            RAISERROR(@msg, 0, 1) WITH NOWAIT;
        END;
    END;

    /*
        Both paths churn the clustered columnstore - a delete tombstones rows
        and an update is a delete plus an insert into the delta store. Squeeze
        the row groups back down so the space is actually returned.
    */
    IF @total > 0 AND OBJECT_ID(N'audit.CopilotAudit', N'U') IS NOT NULL
    BEGIN
        RAISERROR('Reorganizing the columnstore to reclaim space...', 0, 1) WITH NOWAIT;
        ALTER INDEX CCI_CopilotAudit ON audit.CopilotAudit REORGANIZE WITH (COMPRESS_ALL_ROW_GROUPS = ON);
    END;

    /* ---- staging: the second copy of the same JSON ---------------------- */

    IF @PurgeStaging = 1 AND OBJECT_ID(N'audit.CopilotAuditStage', N'U') IS NOT NULL
    BEGIN
        DECLARE @staged BIGINT = 0;
        RAISERROR('Deleting aged rows from audit.CopilotAuditStage...', 0, 1) WITH NOWAIT;
        WHILE 1 = 1
        BEGIN
            DELETE TOP (@BatchSize) FROM audit.CopilotAuditStage WHERE CreatedDateTime < @cutoff;
            SET @n = @@ROWCOUNT;
            SET @staged += @n;
            IF @n = 0 BREAK;
        END;
        SET @msg = CONCAT(N'  ', FORMAT(@staged, 'N0'), N' staging row(s) deleted.');
        RAISERROR(@msg, 0, 1) WITH NOWAIT;
    END;

    /* ---- flattened ------------------------------------------------------ */

    IF @PurgeFlattened = 1 AND OBJECT_ID(N'audit.RawLogFlattened', N'U') IS NOT NULL
    BEGIN
        DECLARE @flat BIGINT = 0, @elements BIGINT = 0;
        RAISERROR('Deleting aged rows from audit.RawLogFlattened, with the resources and contexts each read...', 0, 1) WITH NOWAIT;
        /* 2.4.12: each batch's element rows first, then the batch itself. The element tables keep each record's file
           names, sites and links, and nothing removes them later: a record that is gone is never flattened again,
           which is the only thing that replaces them. Before, they were all kept. */
        DROP TABLE IF EXISTS #gone;
        CREATE TABLE #gone (RecordId NVARCHAR(450) COLLATE DATABASE_DEFAULT NOT NULL PRIMARY KEY);
        WHILE 1 = 1
        BEGIN
            TRUNCATE TABLE #gone;
            INSERT INTO #gone (RecordId)
            SELECT TOP (@BatchSize) RecordId FROM audit.RawLogFlattened WHERE UtcTimeStamp < @cutoff;
            IF @@ROWCOUNT = 0 BREAK;
            BEGIN TRANSACTION;
            IF OBJECT_ID(N'audit.RawLogAccessedResource', N'U') IS NOT NULL
            BEGIN
                DELETE r FROM audit.RawLogAccessedResource AS r JOIN #gone AS g ON g.RecordId = r.RecordId;
                SET @elements += @@ROWCOUNT;
            END;
            IF OBJECT_ID(N'audit.RawLogContext', N'U') IS NOT NULL
            BEGIN
                DELETE c FROM audit.RawLogContext AS c JOIN #gone AS g ON g.RecordId = c.RecordId;
                SET @elements += @@ROWCOUNT;
            END;
            DELETE f FROM audit.RawLogFlattened AS f JOIN #gone AS g ON g.RecordId = f.RecordId;
            SET @flat += @@ROWCOUNT;
            COMMIT TRANSACTION;
        END;
        SET @msg = CONCAT(N'  ', FORMAT(@flat, 'N0'), N' flattened row(s) deleted, with ', FORMAT(@elements, 'N0'), N' resource and context row(s).');
        RAISERROR(@msg, 0, 1) WITH NOWAIT;
    END;

    SET @msg = CONCAT(N'Purge complete: ',
                      FORMAT(@total, 'N0'),
                      CASE WHEN @DeleteRows = 1 THEN N' record(s) deleted.' ELSE N' record(s) had their JSON cleared.' END);
    RAISERROR(@msg, 0, 1) WITH NOWAIT;

    SELECT @cutoff                                               AS CutoffUtc,
           @total                                                AS RecordsPurged,
           (SELECT COUNT_BIG(*) FROM audit.CopilotAudit)         AS RecordsRemaining,
           (SELECT COUNT_BIG(*) FROM audit.CopilotAudit
             WHERE AuditDataGz IS NOT NULL)                      AS RecordsStillHoldingJson;
END;
GO
```

### C.7 `sql/Create-CopilotEnrichmentSchema.sql`

Copilot database. Current licenses, the user directory and the adoption views. 269 lines.

```sql
/*
    Copilot audit backfill - enrichment lookup tables
    ------------------------------------------------------------------------
    Facts about users that the audit records cannot carry, kept in their own
    tables so nothing here touches the backfill path.

      audit.CopilotLicense   who holds a Copilot license, and since when -
                             from Microsoft Graph, by
                             tools/Update-CopilotAuditEnrichment.ps1
      audit.UserDirectory    title, department, division, office, manager and
                             the management chain - from on-premises Active
                             Directory, by tools/Export-UsersWithHierarchy-ToSql.ps1

    Before 2.0.0 the Graph tool also filled audit.UserProfile and
    audit.AdministrativeUnit. Nothing writes or reads them any more; on a
    database that has them they are left in place, and can be dropped.

    audit.CopilotLicense is also created by Create-CopilotFlattenedSchema.sql,
    which shipped before this file existed and made only three columns. The
    ALTERs below add the rest, so a database created by either script ends up
    the same shape.

    Idempotent. Single batch (no GO). Requires SQL Server 2016+.
*/
SET NOCOUNT ON;

IF SCHEMA_ID(N'audit') IS NULL
    EXEC (N'CREATE SCHEMA audit AUTHORIZATION dbo;');

/* ---- Licenses ---------------------------------------------------------- */

IF OBJECT_ID(N'audit.CopilotLicense', N'U') IS NULL
    CREATE TABLE audit.CopilotLicense
    (
        UserPrincipalName   NVARCHAR(512)   NOT NULL
                                CONSTRAINT PK_CopilotLicense PRIMARY KEY,
        IsCopilotLic        BIT             NULL,   -- NULL = not known, see above
        UpdatedUtc          DATETIME2(0)    NOT NULL CONSTRAINT DF_CopilotLicense_Updated DEFAULT (SYSUTCDATETIME())
    );

/*
    IsCopilotLic must allow NULL, which shipped as NOT NULL in
    Create-CopilotFlattenedSchema.sql before there was anything to record but a
    yes or a no. There is now a third answer: a user who appears in the audit
    data but no longer exists in the directory is unknown, not unlicensed, and
    conflating the two would understate licensed usage.
*/
IF EXISTS (SELECT 1 FROM sys.columns
           WHERE object_id = OBJECT_ID(N'audit.CopilotLicense')
             AND name = N'IsCopilotLic' AND is_nullable = 0)
    ALTER TABLE audit.CopilotLicense ALTER COLUMN IsCopilotLic BIT NULL;

/* The Copilot SKU the license came from, e.g. Microsoft_365_Copilot. */
IF COL_LENGTH(N'audit.CopilotLicense', N'SkuPartNumber') IS NULL
    ALTER TABLE audit.CopilotLicense ADD SkuPartNumber NVARCHAR(100) NULL;

/*
    assignedPlans[].assignedDateTime for the Copilot service plan. This is the
    only history Graph offers: it says when the CURRENT assignment began, so
    "licensed since March" is answerable but "was this user licensed last
    March" is not - a license granted and later removed leaves no trace.
*/
IF COL_LENGTH(N'audit.CopilotLicense', N'LicensedSinceUtc') IS NULL
    ALTER TABLE audit.CopilotLicense ADD LicensedSinceUtc DATETIME2(0) NULL;

/* The Entra account's state and hire date, from the same user lookup (User.Read.All):
   a license on a disabled account is wasted, and the hire date starts the
   new-joiner report's clock. AD's own AccountEnabled is in audit.UserDirectory. */
IF COL_LENGTH(N'audit.CopilotLicense', N'EntraAccountEnabled') IS NULL
    ALTER TABLE audit.CopilotLicense ADD EntraAccountEnabled BIT NULL;
IF COL_LENGTH(N'audit.CopilotLicense', N'EmployeeHireDate') IS NULL
    ALTER TABLE audit.CopilotLicense ADD EmployeeHireDate DATE NULL;

/* 2.4.3: Entra's employeeLeaveDateTime, when the license tool runs with -IncludeLeaveDates (it needs
   User-LifeCycleInfo.Read.All), for Licensing > Departures ahead. */
IF COL_LENGTH(N'audit.CopilotLicense', N'EmployeeLeaveUtc') IS NULL
    ALTER TABLE audit.CopilotLicense ADD EmployeeLeaveUtc DATETIME2(0) NULL;
/* Member or Guest, from the same lookup: guests using Copilot are a report of their own. */
IF COL_LENGTH(N'audit.CopilotLicense', N'UserType') IS NULL
    ALTER TABLE audit.CopilotLicense ADD UserType NVARCHAR(20) NULL;

/*
    2.4.0: members of the AD groups granted Organization in the dashboard (dash.RoleGrant), expanded
    by the directory load, nested groups included. bi.OrgSecurity gives them the whole organization,
    so Power BI honors a group grant the way the dashboard does at sign-in. Replaced whole each load.
*/
IF OBJECT_ID(N'audit.GroupMember', N'U') IS NULL
    CREATE TABLE audit.GroupMember
    (
        GroupName          NVARCHAR(256)  NOT NULL,   -- as granted: DOMAIN\Group
        UserPrincipalName  NVARCHAR(256)  NOT NULL,
        LoadedUtc          DATETIME2(0)   NOT NULL CONSTRAINT DF_GroupMember_Loaded DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT PK_GroupMember PRIMARY KEY (GroupName, UserPrincipalName)
    );

/*
    The tenant's license pool, from /subscribedSkus (LicenseAssignment.Read.All),
    which the license tool already reads to find the Copilot SKUs: seats bought
    (prepaidUnits) and assigned (consumedUnits) per SKU. LicenseSku is the
    latest; LicenseSkuDay keeps one row per SKU per day for the trend.
*/
IF OBJECT_ID(N'audit.LicenseSku', N'U') IS NULL
    CREATE TABLE audit.LicenseSku
    (
        SkuId          UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_LicenseSku PRIMARY KEY,
        SkuPartNumber  NVARCHAR(100)    NOT NULL,
        IsCopilot      BIT              NOT NULL,
        Enabled        INT              NOT NULL,   -- prepaidUnits.enabled: seats bought and active
        Warning        INT              NOT NULL,   -- prepaidUnits.warning: in the grace period after expiry
        Suspended      INT              NOT NULL,
        LockedOut      INT              NOT NULL,
        Consumed       INT              NOT NULL,   -- consumedUnits: seats assigned
        CapabilityStatus NVARCHAR(32)   NULL,
        CapturedUtc    DATETIME2(0)     NOT NULL CONSTRAINT DF_LicenseSku_Captured DEFAULT (SYSUTCDATETIME())
    );
IF OBJECT_ID(N'audit.LicenseSkuDay', N'U') IS NULL
    CREATE TABLE audit.LicenseSkuDay
    (
        SnapshotDate   DATE             NOT NULL,
        SkuId          UNIQUEIDENTIFIER NOT NULL,
        SkuPartNumber  NVARCHAR(100)    NOT NULL,
        IsCopilot      BIT              NOT NULL,
        Enabled        INT              NOT NULL,
        Warning        INT              NOT NULL,
        Suspended      INT              NOT NULL,
        LockedOut      INT              NOT NULL,
        Consumed       INT              NOT NULL,
        CONSTRAINT PK_LicenseSkuDay PRIMARY KEY (SnapshotDate, SkuId)
    );

/* 2.4.3: every SKU each person looked up holds (assignedLicenses), not only Copilot, so the dashboard can find
   licenses that overlap. Replaced whole by each run; names come from audit.LicenseSku. */
IF OBJECT_ID(N'audit.UserLicenseSku', N'U') IS NULL
    CREATE TABLE audit.UserLicenseSku
    (
        UserPrincipalName   NVARCHAR(512)    NOT NULL,
        SkuId               UNIQUEIDENTIFIER NOT NULL,
        CapturedUtc         DATETIME2(0)     NOT NULL CONSTRAINT DF_UserLicenseSku_Captured DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT PK_UserLicenseSku PRIMARY KEY (UserPrincipalName, SkuId)
    );

/* Enabled | Suspended | Deleted | Warning, from assignedPlans[]. */
IF COL_LENGTH(N'audit.CopilotLicense', N'CapabilityStatus') IS NULL
    ALTER TABLE audit.CopilotLicense ADD CapabilityStatus NVARCHAR(50) NULL;

/*
    Audit records outlive directory objects. A user who has left resolves to
    nothing in Graph, and must not be reported as unlicensed - that would be a
    different claim from "we do not know". DirectoryStatus records which it is.
*/
IF COL_LENGTH(N'audit.CopilotLicense', N'DirectoryStatus') IS NULL
    ALTER TABLE audit.CopilotLicense ADD DirectoryStatus NVARCHAR(20) NULL;   -- Found | NotFound

/* ---- The user directory (on-premises AD) ------------------------------ */
/*
    One row per user, replaced whole on every load. Joined to the audit data
    on UserPrincipalName, so AD's UPN must be the one users sign in to
    Microsoft 365 with; the load reports how many audit users it matched, by
    UPN and by Mail, so a mismatch shows at once.

    Mgr1 is the top of the chain (the CEO) and Mgr<OrgDepth> the direct
    manager; the columns after it are empty. That is the layout a Power BI
    hierarchy wants. Chains deeper than 15 keep their top 15 levels; OrgDepth
    is always the full depth.
*/
IF OBJECT_ID(N'audit.UserDirectory', N'U') IS NULL
    CREATE TABLE audit.UserDirectory
    (
        UserPrincipalName  NVARCHAR(256)  NOT NULL CONSTRAINT PK_UserDirectory PRIMARY KEY,
        Mail               NVARCHAR(256)  NULL,
        FullName           NVARCHAR(256)  NULL,       -- displayName
        EmployeeType       NVARCHAR(32)   NULL,       -- after -EmployeeTypeMap, e.g. EMPLOYEE / CONTRACTOR
        IsManager          BIT            NOT NULL,   -- someone reports to them
        AccountEnabled     BIT            NOT NULL,
        JobTitle           NVARCHAR(128)  NULL,       -- title
        Department         NVARCHAR(200)  NULL,       -- department, or the first of -DepartmentAttribute that has a value (2.5.0)
        Division           NVARCHAR(256)  NULL,       -- division: the line of business
        Office             NVARCHAR(128)  NULL,       -- physicalDeliveryOfficeName
        City               NVARCHAR(128)  NULL,       -- l
        Country            NVARCHAR(128)  NULL,       -- co, else the English name of c (2.5.0)
        Company            NVARCHAR(64)   NULL,       -- company
        ManagerUPN         NVARCHAR(256)  NULL,
        ManagerName        NVARCHAR(256)  NULL,
        OrgDepth           TINYINT        NOT NULL,   -- managers above this user; 0 = the top
        Mgr1  NVARCHAR(256) NULL, Mgr2  NVARCHAR(256) NULL, Mgr3  NVARCHAR(256) NULL,
        Mgr4  NVARCHAR(256) NULL, Mgr5  NVARCHAR(256) NULL, Mgr6  NVARCHAR(256) NULL,
        Mgr7  NVARCHAR(256) NULL, Mgr8  NVARCHAR(256) NULL, Mgr9  NVARCHAR(256) NULL,
        Mgr10 NVARCHAR(256) NULL, Mgr11 NVARCHAR(256) NULL, Mgr12 NVARCHAR(256) NULL,
        Mgr13 NVARCHAR(256) NULL, Mgr14 NVARCHAR(256) NULL, Mgr15 NVARCHAR(256) NULL,
        LoadedUtc          DATETIME2(0)   NOT NULL CONSTRAINT DF_UserDirectory_Loaded DEFAULT (SYSUTCDATETIME())
    );

IF OBJECT_ID(N'audit.UserDirectory', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes
                   WHERE object_id = OBJECT_ID(N'audit.UserDirectory') AND name = N'IX_UserDirectory_Mail')
    EXEC (N'CREATE INDEX IX_UserDirectory_Mail ON audit.UserDirectory (Mail);');

/* The account's SID (objectSid, S-1-5-21-...): the dashboard recognizes a
   Windows sign-in by it, with no directory lookup at sign-in - so it works
   when the web servers are in another domain from the users. */
IF OBJECT_ID(N'audit.UserDirectory', N'U') IS NOT NULL AND COL_LENGTH(N'audit.UserDirectory', N'Sid') IS NULL
    ALTER TABLE audit.UserDirectory ADD Sid VARCHAR(184) NULL;

/* 2.4.3: AD's accountExpires, when the account is set to expire (NULL for never), for Licensing > Departures ahead. */
IF OBJECT_ID(N'audit.UserDirectory', N'U') IS NOT NULL AND COL_LENGTH(N'audit.UserDirectory', N'AccountExpiresUtc') IS NULL
    ALTER TABLE audit.UserDirectory ADD AccountExpiresUtc DATETIME2(0) NULL;

/* 2.5.0: Department holds 200 characters, as bi.DimUser and dash.Person do. It held 64, AD's limit for department,
   and a custom attribute named by -DepartmentAttribute can be longer. COL_LENGTH is in bytes: 400 is nvarchar(200). */
IF OBJECT_ID(N'audit.UserDirectory', N'U') IS NOT NULL AND COL_LENGTH(N'audit.UserDirectory', N'Department') < 400
    EXEC (N'ALTER TABLE audit.UserDirectory ALTER COLUMN Department NVARCHAR(200) NULL;');

/* ---- Reporting views ---------------------------------------------------- */

/*
    Both views read audit.CopilotInteraction, which finalize creates; before
    then they are skipped, so the tables can be loaded first.

    Adoption: licensed users against users who actually interacted. The LEFT
    JOIN direction matters - a licensed user with no interactions is the
    shelfware this is meant to surface.
*/
IF OBJECT_ID(N'audit.CopilotInteraction', N'U') IS NOT NULL
EXEC (N'
CREATE OR ALTER VIEW audit.vCopilotAdoption
AS
SELECT l.UserPrincipalName,
       l.IsCopilotLic,
       l.SkuPartNumber,
       l.LicensedSinceUtc,
       d.Division,
       d.Department,
       d.Office,
       d.Country,
       d.ManagerUPN,
       d.EmployeeType,
       d.AccountEnabled,
       u.Interactions,
       u.FirstInteractionUtc,
       u.LastInteractionUtc,
       CASE WHEN u.Interactions IS NULL THEN 1 ELSE 0 END AS NeverUsed,
       DATEDIFF(DAY, l.LicensedSinceUtc, u.FirstInteractionUtc) AS DaysToFirstUse
FROM audit.CopilotLicense AS l
LEFT JOIN audit.UserDirectory AS d
       ON d.UserPrincipalName = l.UserPrincipalName
LEFT JOIN (
    SELECT UserPrincipalName,
           COUNT_BIG(*)         AS Interactions,
           MIN(CreatedDateTime) AS FirstInteractionUtc,
           MAX(CreatedDateTime) AS LastInteractionUtc
    FROM audit.CopilotInteraction
    GROUP BY UserPrincipalName
) AS u ON u.UserPrincipalName = l.UserPrincipalName
WHERE l.IsCopilotLic = 1;');

/* Interactions by department, for the cut every rollout report asks for. */
IF OBJECT_ID(N'audit.CopilotInteraction', N'U') IS NOT NULL
EXEC (N'
CREATE OR ALTER VIEW audit.vCopilotUsageByDepartment
AS
SELECT ISNULL(d.Department, N''(unknown)'') AS Department,
       i.ActivityDate,
       i.AppHost,
       COUNT_BIG(*)                          AS Interactions,
       COUNT(DISTINCT i.UserPrincipalName)   AS ActiveUsers
FROM audit.CopilotInteraction AS i
LEFT JOIN audit.UserDirectory AS d
       ON d.UserPrincipalName = i.UserPrincipalName
GROUP BY ISNULL(d.Department, N''(unknown)''), i.ActivityDate, i.AppHost;');
```

### C.8 `sql/Create-CopilotSessionSchema.sql`

Copilot database. App categories, inferred sessions and the weekly rollup. 214 lines.

```sql
/*
    Inferred sessions and the Saturday-Friday weekly rollup
    ------------------------------------------------------------------------
    A SQL port of the session script that used to read the Purview CSV export
    (v5.7.5, metric v5.5-prod-60m-12h-satfri-customweek-...). It reads
    audit.RawLogFlattened, whose columns are that export's columns, and writes
    the two outputs the script wrote as CSV:

      audit.CopilotSession        copilot_session_summary.csv, one row per session
      audit.CopilotWeeklyRollup   weekly_rollup.csv, one row per
                                  week x user x primary app x category x license

    The views audit.vCopilotSessionSummary and audit.vCopilotWeeklyRollup carry
    the CSVs' exact column names and text formats (yyyy-MM-dd HH:mm:ss, TRUE /
    FALSE), so anything that read the files can read the views instead.

    audit.usp_BuildCopilotSessions (Create-CopilotSessionProcedure.sql) fills
    them. Its rules are the script's; where this data differs from the CSV the
    script was written for, the difference is a setting, and documented:

      * audit.SessionAppCategory maps App to 'Copilot Chat' / 'In-App Copilot'.
        The script tested App = 'Copilot Chat', the portal export's label. The
        Graph data carries host names instead ('Office' is Copilot Chat), so
        the literal test would call every session In-App. Edit the table to
        change the split; unmapped apps are 'In-App Copilot'.
      * The script keyed a session on user + app context, falling back to
        Location, and skipped a row with neither. On a live tenant 98% of rows
        had no app context and the script's rule skipped every Copilot Chat,
        Outlook and Teams row, so the procedure keys on the user by default.
        @ContextFallback = 'Location' reproduces the script exactly, and every
        build records its rule and its skips in audit.CopilotSessionRun.

    Division, Department, JobTitle and ManagerUPN come from audit.UserDirectory
    (on-premises AD, loaded by tools/Export-UsersWithHierarchy-ToSql.ps1);
    Division is AD's division attribute, the line of business.

    Idempotent. Single batch (no GO). Requires SQL Server 2016+.
*/
SET NOCOUNT ON;

IF SCHEMA_ID(N'audit') IS NULL
    EXEC (N'CREATE SCHEMA audit AUTHORIZATION dbo;');

/* App -> category. Seeded once; edits survive re-running this script. */
IF OBJECT_ID(N'audit.SessionAppCategory', N'U') IS NULL
BEGIN
    CREATE TABLE audit.SessionAppCategory
    (
        App          NVARCHAR(200) NOT NULL CONSTRAINT PK_SessionAppCategory PRIMARY KEY,
        AppCategory  NVARCHAR(50)  NOT NULL
    );
    INSERT INTO audit.SessionAppCategory (App, AppCategory) VALUES
        (N'Copilot Chat',              N'Copilot Chat'),   -- the portal export's label
        (N'Office',                    N'Copilot Chat'),   -- Microsoft 365 Copilot Chat in the Graph data
        (N'OfficeCopilotSearchAnswer', N'Copilot Chat'),
        (N'BizChat',                   N'Copilot Chat'),
        (N'Edge',                      N'Copilot Chat');
END;

IF OBJECT_ID(N'audit.CopilotSession', N'U') IS NULL
    CREATE TABLE audit.CopilotSession
    (
        SessionKey                                    BIGINT IDENTITY(1, 1) NOT NULL CONSTRAINT PK_CopilotSession PRIMARY KEY,
        AppContext                                    NVARCHAR(4000) NULL,
        UserUPN                                       NVARCHAR(512)  NOT NULL,
        SessionOrdinal_Derived                        INT            NOT NULL,
        SessionStart_PromptSubmittedDateTime          DATETIME2(0)   NOT NULL,   -- reporting time zone
        SessionEnd_PromptSubmittedDateTime            DATETIME2(0)   NOT NULL,
        InferredSessionSpanMinutes                    DECIMAL(12, 2) NOT NULL,
        InferredSessionSpanHours                      DECIMAL(12, 2) NOT NULL,
        InferredSessionSpanDays                       DECIMAL(12, 6) NOT NULL,
        SessionTimeoutMinutes                         INT            NOT NULL,
        CalendarDaysTouched_InSessionSpan             INT            NOT NULL,
        PromptCount                                   INT            NOT NULL,
        SessionType                                   NVARCHAR(20)   NOT NULL,   -- Single-Turn / Multi-Turn
        PrimaryApp                                    NVARCHAR(200)  NULL,
        AppCategory                                   NVARCHAR(50)   NOT NULL,
        IsM365CopilotLicensed                         BIT            NULL,
        Division                                      NVARCHAR(400)  NULL,
        Department                                    NVARCHAR(400)  NULL,
        JobTitle                                      NVARCHAR(400)  NULL,
        ManagerUPN                                    NVARCHAR(1024) NULL,
        AvgMinutesPerPrompt_InferredSessionSpan       DECIMAL(12, 2) NOT NULL,
        AvgMinutesPerPromptPerCalendarDay_InferredSpan DECIMAL(12, 2) NOT NULL,
        AvgMinutesPerPrompt_NormalizedByCalendarDays  DECIMAL(12, 2) NOT NULL,
        MetricVersion                                 NVARCHAR(200)  NOT NULL
    );

/* Per user, with what an incremental build needs to carry numbering on
   (AppContext, the ordinal). Rebuilt with them if it predates that. */
IF OBJECT_ID(N'audit.CopilotSession', N'U') IS NOT NULL
   AND EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'audit.CopilotSession') AND name = N'IX_CopilotSession_User')
   AND NOT EXISTS (SELECT 1 FROM sys.indexes AS i JOIN sys.index_columns AS ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
                   WHERE i.object_id = OBJECT_ID(N'audit.CopilotSession') AND i.name = N'IX_CopilotSession_User'
                     AND ic.is_included_column = 1)
    EXEC (N'DROP INDEX IX_CopilotSession_User ON audit.CopilotSession;');
IF OBJECT_ID(N'audit.CopilotSession', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'audit.CopilotSession') AND name = N'IX_CopilotSession_User')
    EXEC (N'CREATE INDEX IX_CopilotSession_User ON audit.CopilotSession (UserUPN, SessionStart_PromptSubmittedDateTime)
            INCLUDE (AppContext, SessionOrdinal_Derived);');

/* By start, for an incremental build's delete and the weeks it recomputes. */
IF OBJECT_ID(N'audit.CopilotSession', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'audit.CopilotSession') AND name = N'IX_CopilotSession_Start')
    EXEC (N'CREATE INDEX IX_CopilotSession_Start ON audit.CopilotSession (SessionStart_PromptSubmittedDateTime);');

IF OBJECT_ID(N'audit.CopilotWeeklyRollup', N'U') IS NULL
    CREATE TABLE audit.CopilotWeeklyRollup
    (
        WeekEnding                                DATE           NOT NULL,   -- a Friday; the week is Saturday-Friday
        WeekYear                                  INT            NOT NULL,
        WeekNumber                                INT            NOT NULL,   -- 1 = the week ending on the year's first Friday
        WeekLabel                                 NVARCHAR(10)   NOT NULL,   -- 2026-W38
        UserUPN                                   NVARCHAR(512)  NOT NULL,
        PrimaryApp                                NVARCHAR(200)  NULL,
        AppCategory                               NVARCHAR(50)   NOT NULL,
        IsM365CopilotLicensed                     BIT            NULL,
        WeeklySessionCount                        INT            NOT NULL,
        WeeklyPromptCount                         INT            NOT NULL,
        WeeklyInferredSessionSpanMinutes          DECIMAL(14, 2) NOT NULL,
        WeeklyInferredSessionSpanHours            DECIMAL(14, 2) NOT NULL,
        WeeklyInferredSessionSpanDays             DECIMAL(14, 6) NOT NULL,
        ActiveDaysWithUsageInWeek                 INT            NOT NULL,
        DistinctAppContextsInWeek                 INT            NOT NULL,
        FirstPromptSubmittedDateTime_InWeek       DATETIME2(0)   NOT NULL,
        LastPromptSubmittedDateTime_InWeek        DATETIME2(0)   NOT NULL,
        Division                                  NVARCHAR(400)  NULL,
        Department                                NVARCHAR(400)  NULL,
        JobTitle                                  NVARCHAR(400)  NULL,
        ManagerUPN                                NVARCHAR(1024) NULL,
        AvgMinutesPerPrompt_InferredSpan_Weekly   DECIMAL(12, 2) NOT NULL,
        MetricVersion                             NVARCHAR(200)  NOT NULL
    );

IF OBJECT_ID(N'audit.CopilotWeeklyRollup', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'audit.CopilotWeeklyRollup') AND name = N'IX_CopilotWeeklyRollup_Week')
    EXEC (N'CREATE INDEX IX_CopilotWeeklyRollup_Week ON audit.CopilotWeeklyRollup (WeekEnding, UserUPN);');

/* One row per build: the script's run_log.csv, with the settings it ran under. */
IF OBJECT_ID(N'audit.CopilotSessionRun', N'U') IS NULL
    CREATE TABLE audit.CopilotSessionRun
    (
        RunId                        INT IDENTITY(1, 1) NOT NULL CONSTRAINT PK_CopilotSessionRun PRIMARY KEY,
        RunStartUtc                  DATETIME2(0)  NOT NULL,
        RunEndUtc                    DATETIME2(0)  NOT NULL,
        RowsRead                     INT           NOT NULL,
        RowsWithOriginalAppContext   INT           NOT NULL,
        RowsRecoveredFromLocation    INT           NOT NULL,
        RowsSkippedMissingContext    INT           NOT NULL,
        RowsSkippedMissingUser       INT           NOT NULL,
        RowsSkippedBadTimestamp      INT           NOT NULL,
        SessionsWritten              INT           NOT NULL,
        WeeklyGroups                 INT           NOT NULL,
        SessionTimeoutMinutes        INT           NOT NULL,
        ContextFallback              NVARCHAR(20)  NOT NULL,
        ReportingTimeZone            NVARCHAR(128) NOT NULL,
        LicenseEventDatabase         NVARCHAR(1000) NULL,  -- NULL: license flag is today's, from the enrichment
        MetricVersion                NVARCHAR(200) NOT NULL
    );

IF COL_LENGTH(N'audit.CopilotSessionRun', N'LicenseEventDatabase') IS NULL
    ALTER TABLE audit.CopilotSessionRun ADD LicenseEventDatabase NVARCHAR(1000) NULL;

/* Local midnight an incremental build rebuilt from; NULL = everything. */
IF COL_LENGTH(N'audit.CopilotSessionRun', N'RebuiltFrom') IS NULL
    ALTER TABLE audit.CopilotSessionRun ADD RebuiltFrom DATETIME2(0) NULL;

/* ---- CSV-shaped views ---------------------------------------------------- */

EXEC (N'
CREATE OR ALTER VIEW audit.vCopilotSessionSummary
AS
SELECT AppContext,
       UserUPN,
       SessionOrdinal_Derived,
       CONVERT(varchar(19), SessionStart_PromptSubmittedDateTime, 120) AS SessionStart_PromptSubmittedDateTime,
       CONVERT(varchar(19), SessionEnd_PromptSubmittedDateTime, 120)   AS SessionEnd_PromptSubmittedDateTime,
       InferredSessionSpanMinutes,
       InferredSessionSpanHours,
       InferredSessionSpanDays,
       SessionTimeoutMinutes,
       CalendarDaysTouched_InSessionSpan,
       PromptCount,
       SessionType,
       PrimaryApp,
       AppCategory,
       CASE IsM365CopilotLicensed WHEN 1 THEN ''TRUE'' WHEN 0 THEN ''FALSE'' ELSE '''' END AS IsM365CopilotLicensed,
       Division, Department, JobTitle, ManagerUPN,
       AvgMinutesPerPrompt_InferredSessionSpan,
       AvgMinutesPerPromptPerCalendarDay_InferredSpan,
       AvgMinutesPerPrompt_NormalizedByCalendarDays,
       MetricVersion
FROM audit.CopilotSession;');

EXEC (N'
CREATE OR ALTER VIEW audit.vCopilotWeeklyRollup
AS
SELECT CONVERT(varchar(10), WeekEnding, 23) AS WeekEnding,
       WeekYear, WeekNumber, WeekLabel,
       UserUPN, PrimaryApp, AppCategory,
       CASE IsM365CopilotLicensed WHEN 1 THEN ''TRUE'' WHEN 0 THEN ''FALSE'' ELSE '''' END AS IsM365CopilotLicensed,
       WeeklySessionCount,
       WeeklyPromptCount,
       WeeklyInferredSessionSpanMinutes,
       WeeklyInferredSessionSpanHours,
       WeeklyInferredSessionSpanDays,
       ActiveDaysWithUsageInWeek,
       DistinctAppContextsInWeek,
       CONVERT(varchar(19), FirstPromptSubmittedDateTime_InWeek, 120) AS FirstPromptSubmittedDateTime_InWeek,
       CONVERT(varchar(19), LastPromptSubmittedDateTime_InWeek, 120)  AS LastPromptSubmittedDateTime_InWeek,
       Division, Department, JobTitle, ManagerUPN,
       AvgMinutesPerPrompt_InferredSpan_Weekly,
       MetricVersion
FROM audit.CopilotWeeklyRollup;');
```

### C.9 `sql/Create-CopilotSessionProcedure.sql`

Copilot database. `audit.usp_BuildCopilotSessions`. 474 lines.

```sql
/*
    audit.usp_BuildCopilotSessions
    ------------------------------------------------------------------------
    Rebuilds audit.CopilotSession and audit.CopilotWeeklyRollup from
    audit.RawLogFlattened, with the session script's rules:

      * A session belongs to one user - and, under the script's own rule, to
        one app context. Which applies is @ContextFallback (see below); every
        row it skips is counted.
      * The next prompt continues the session unless it comes 60 minutes or
        more after the previous one, or on a different calendar day in the
        reporting time zone. Either starts a new session.
      * A session's PrimaryApp and license flag are those of its first prompt;
        with @LicenseEventDatabase the flag is the license held at that moment.
        PromptCount is its number of rows. Single-Turn is one prompt.
      * Sessions roll up by the week their LAST prompt falls in. Weeks run
        Saturday to Friday and are labeled by the Friday; week 1 of a year
        ends on its first Friday.

    Where the port differs from the script, deliberately:

      * Rows are taken in time order. The script read them in file order and
        also started a new session on a timestamp earlier than the last one;
        in time order that cannot happen, so the rule has nothing to act on.
      * The script's metric version mentions 12h, but no 12-hour rule appears
        in its session logic, so none is applied here.
      * It is set-based over the table, in one transaction, rather than a
        restart-safe pass over files carrying open-session state. Readers
        never see a half-built table.

    @SinceUtc - incremental builds
      NULL (default) rebuilds every session and week. A time rebuilds only
      from local midnight of the day it falls on (in @ReportingTimeZone):
      a session never crosses midnight, so nothing before that day can
      change. The sessions from that midnight on are replaced, numbering
      continues from each stream's last kept session, and every week that
      contains a rebuilt day is recomputed from audit.CopilotSession. The
      result is the same as a full rebuild over the same rows - except that
      a kept session keeps the Division, Department, JobTitle, ManagerUPN and
      license flag it was built with, rather than being restated with today's.
      -Incremental passes the load's start. At 40M interactions a full build
      took 28 minutes and 24 GB of tempdb, growing with the history kept;
      an incremental one costs what the window costs.

      An incremental build is refused when the last build used different
      settings (timeout, fallback, time zone, metric version or license
      databases): the kept sessions would follow other rules. Build fully
      once after changing one. With no earlier build it builds everything.

    @ContextFallback
      'User'      (default) one session stream per user across all contexts;
                  AppContext is recorded as '(user)' and no row is skipped for
                  want of one
      'Location'  the script's rule: App context, else Location, else skip
      'None'      App context only; a row without one is skipped

    The default is not the script's rule, on evidence. The Graph data leaves
    App context empty on almost every row, and Location is only an
    environment tag ('prd') on agent traffic. On a live tenant the script's
    rule skipped every Copilot Chat, Outlook and Teams row - 507 of 1,145 - and
    kept almost nothing but agents. 'Location' reproduces the script exactly,
    for comparing against its CSVs; audit.CopilotSessionRun records which
    rule each build used.

    Requires SQL Server 2017+. Run sql/Create-CopilotSessionSchema.sql first,
    and audit.usp_FlattenCopilotAudit to fill audit.RawLogFlattened.
*/
SET NOCOUNT ON;

IF OBJECT_ID(N'audit.usp_BuildCopilotSessions', N'P') IS NULL
    EXEC (N'CREATE PROCEDURE audit.usp_BuildCopilotSessions AS BEGIN SET NOCOUNT ON; END;');
GO

ALTER PROCEDURE audit.usp_BuildCopilotSessions
    @SessionTimeoutMinutes INT           = 60,
    @ReportingTimeZone     SYSNAME       = NULL,
    @ContextFallback       NVARCHAR(20)  = N'User',
    @MetricVersion         NVARCHAR(200) = N'sql-1.0-60m-daysplit-satfri-customweek',
    @LicenseEventDatabase  NVARCHAR(1000) = NULL,  -- one database, or several comma-separated
    @SinceUtc              DATETIME2(0)   = NULL   -- NULL: everything; else from local midnight of that day
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    SET @ReportingTimeZone = COALESCE(@ReportingTimeZone, audit.ReportingTimeZone());   -- the zone Install-CopilotAudit.ps1 -ReportingTimeZone stored

    IF @ContextFallback NOT IN (N'Location', N'None', N'User')
        THROW 50001, N'@ContextFallback must be Location, None or User.', 1;
    IF @SessionTimeoutMinutes IS NULL OR @SessionTimeoutMinutes < 1
        THROW 50002, N'@SessionTimeoutMinutes must be at least 1.', 1;
    IF NOT EXISTS (SELECT 1 FROM sys.time_zone_info WHERE name = @ReportingTimeZone)
        THROW 50003, N'@ReportingTimeZone is not a name in sys.time_zone_info.', 1;
    DECLARE @licDbs TABLE (Name SYSNAME NOT NULL PRIMARY KEY);
    IF @LicenseEventDatabase IS NOT NULL
        INSERT INTO @licDbs (Name)
        SELECT DISTINCT LTRIM(RTRIM(value)) FROM STRING_SPLIT(@LicenseEventDatabase, N',')
        WHERE LTRIM(RTRIM(value)) <> N'';
    IF EXISTS (SELECT 1 FROM @licDbs WHERE OBJECT_ID(QUOTENAME(Name) + N'.audit.vLicenseSkuChange', N'V') IS NULL)
        THROW 50004, N'A database in @LicenseEventDatabase has no audit.vLicenseSkuChange - run sql/Create-LicenseEventSchema.sql there.', 1;
    /* The list as the run log keeps it and the check below compares it (2.4.12):
       each database once, trimmed, in order. The raw text was compared, so
       'A,B' after 'B,A' or 'A, B' - the same databases - counted as different
       settings and every incremental build after was refused. */
    SET @LicenseEventDatabase = (SELECT STRING_AGG(Name, N',') WITHIN GROUP (ORDER BY Name) FROM @licDbs);

    DECLARE @started DATETIME2(0) = SYSUTCDATETIME(), @msg NVARCHAR(600);

    IF COL_LENGTH(N'audit.CopilotSessionRun', N'RebuiltFrom') IS NULL
        THROW 50005, N'audit.CopilotSessionRun predates incremental builds - run sql/Create-CopilotSessionSchema.sql again.', 1;

    /* Incremental: from local midnight of @SinceUtc's day, if the last build
       followed the same rules. @rebuiltFrom is local, @fromUtc the same
       instant in UTC for the index on audit.RawLogFlattened. */
    DECLARE @rebuiltFrom DATETIME2(0) = NULL, @fromUtc DATETIME2(0) = NULL;
    IF @SinceUtc IS NOT NULL
    BEGIN
        DECLARE @lastRun INT, @lastTimeout INT, @lastFallback NVARCHAR(20), @lastZone NVARCHAR(128),
                @lastMetric NVARCHAR(200), @lastLic NVARCHAR(1000);
        SELECT TOP (1) @lastRun = RunId, @lastTimeout = SessionTimeoutMinutes, @lastFallback = ContextFallback,
                       @lastZone = ReportingTimeZone, @lastMetric = MetricVersion, @lastLic = LicenseEventDatabase
        FROM audit.CopilotSessionRun ORDER BY RunId DESC;
        /* A build before 2.4.12 logged the list as it was given. */
        SET @lastLic = (SELECT STRING_AGG(x.Name, N',') WITHIN GROUP (ORDER BY x.Name)
                        FROM (SELECT DISTINCT CAST(LTRIM(RTRIM(value)) AS SYSNAME) AS Name FROM STRING_SPLIT(@lastLic, N',')
                              WHERE LTRIM(RTRIM(value)) <> N'') AS x);

        IF @lastRun IS NULL
        BEGIN
            RAISERROR('No earlier session build: building every session, not only the days asked for.', 0, 1) WITH NOWAIT;
            SET @SinceUtc = NULL;
        END
        ELSE IF @lastTimeout <> @SessionTimeoutMinutes OR @lastFallback <> @ContextFallback
             OR @lastZone <> @ReportingTimeZone OR @lastMetric <> @MetricVersion
             OR ISNULL(@lastLic, N'') <> ISNULL(@LicenseEventDatabase, N'')
            THROW 50006, N'The last session build used different settings, and an incremental build would keep sessions built under them. Run once with @SinceUtc = NULL to rebuild everything.', 1;
        ELSE
        BEGIN
            SET @rebuiltFrom = CAST(CAST(CAST(CAST(@SinceUtc AS DATETIME2(0)) AT TIME ZONE 'UTC'
                                              AT TIME ZONE @ReportingTimeZone AS DATETIME2(0)) AS DATE) AS DATETIME2(0));
            SET @fromUtc = CAST(@rebuiltFrom AT TIME ZONE @ReportingTimeZone AT TIME ZONE 'UTC' AS DATETIME2(0));
        END;
    END;

    /* ---- 1. The rows, with the session context resolved ------------------ */

    DROP TABLE IF EXISTS #src;
    SELECT v.RecordId, v.UserUPN, v.TsUtc, v.Ts, v.App, v.Ctx, v.Loc, v.IsCopilotLic, k.KeyCtx,
           CAST(CASE WHEN @ContextFallback = N'Location' AND v.Ctx IS NULL AND v.Loc IS NOT NULL THEN 1 ELSE 0 END AS BIT) AS Recovered,
           /* The script's order of checks: context, then user, then timestamp. */
           CAST(CASE WHEN k.KeyCtx IS NULL  THEN 1
                     WHEN v.UserUPN IS NULL THEN 2
                     WHEN v.Ts IS NULL      THEN 3
                     ELSE 0 END AS TINYINT) AS Skip
    INTO #src
    FROM audit.RawLogFlattened AS f
    /* RecordId is NVARCHAR(900) in the flattened table, and a window ORDER BY
       key is limited to 900 bytes; audit record ids are GUIDs. */
    CROSS APPLY (SELECT CAST(f.RecordId AS VARCHAR(64))                                     AS RecordId,
                        NULLIF(LTRIM(RTRIM(f.[User])), N'')                                AS UserUPN,
                        CAST(f.UtcTimeStamp AS DATETIME2(0))                               AS TsUtc,
                        CAST(CAST(f.UtcTimeStamp AS DATETIME2(0)) AT TIME ZONE 'UTC'
                             AT TIME ZONE @ReportingTimeZone AS DATETIME2(0))               AS Ts,
                        NULLIF(LTRIM(RTRIM(f.App)), N'')                                   AS App,
                        NULLIF(LTRIM(RTRIM(CAST(f.[App context] AS NVARCHAR(4000)))), N'') AS Ctx,
                        NULLIF(LTRIM(RTRIM(CAST(f.Location AS NVARCHAR(4000)))), N'')      AS Loc,
                        f.IsCopilotLic) AS v
    CROSS APPLY (SELECT CASE @ContextFallback
                            WHEN N'Location' THEN COALESCE(v.Ctx, v.Loc)
                            WHEN N'None'     THEN v.Ctx
                            ELSE N'(user)' END AS KeyCtx) AS k
    WHERE @fromUtc IS NULL OR f.UtcTimeStamp >= @fromUtc
    OPTION (RECOMPILE);   -- planned for the window actually asked for, so a small one seeks

    DECLARE @read INT, @orig INT, @recovered INT, @noCtx INT, @noUser INT, @badTs INT;
    SELECT @read      = COUNT(*),
           @orig      = SUM(CASE WHEN Skip = 0 AND Ctx IS NOT NULL THEN 1 ELSE 0 END),
           @recovered = SUM(CASE WHEN Skip = 0 AND Recovered = 1 THEN 1 ELSE 0 END),
           @noCtx     = SUM(CASE WHEN Skip = 1 THEN 1 ELSE 0 END),
           @noUser    = SUM(CASE WHEN Skip = 2 THEN 1 ELSE 0 END),
           @badTs     = SUM(CASE WHEN Skip = 3 THEN 1 ELSE 0 END)
    FROM #src;

    /* ---- 2. Session boundaries ------------------------------------------- */
    /*
        Order and the gap are taken in UTC, the calendar day in the reporting
        zone (2.4.12). Both were local times, which a daylight-saving change
        moves: in the repeated autumn hour two prompts 110 minutes apart read
        as 50 and a later prompt sorted before an earlier one (taking the
        session's start and primary app), and in spring a 10-minute gap read
        as 70. Only sessions around a change come out differently; the local
        day still never goes backwards in UTC order.
    */

    DROP TABLE IF EXISTS #seq;
    SELECT l.RecordId, l.UserUPN, l.KeyCtx, l.TsUtc, l.Ts, l.App, l.IsCopilotLic,
           CASE WHEN l.PrevUtc IS NULL                                                     THEN 1
                WHEN DATEDIFF_BIG(SECOND, l.PrevUtc, l.TsUtc) >= @SessionTimeoutMinutes * 60 THEN 1
                WHEN CAST(l.PrevTs AS DATE) <> CAST(l.Ts AS DATE)                        THEN 1
                ELSE 0 END AS IsNew
    INTO #seq
    FROM (SELECT s.RecordId, s.UserUPN, s.KeyCtx, s.TsUtc, s.Ts, s.App, s.IsCopilotLic,
                 LAG(s.TsUtc) OVER (PARTITION BY s.UserUPN, s.KeyCtx ORDER BY s.TsUtc, s.RecordId) AS PrevUtc,
                 LAG(s.Ts)    OVER (PARTITION BY s.UserUPN, s.KeyCtx ORDER BY s.TsUtc, s.RecordId) AS PrevTs
          FROM #src AS s
          WHERE s.Skip = 0) AS l;

    DROP TABLE IF EXISTS #num;
    SELECT q.*,
           SUM(q.IsNew) OVER (PARTITION BY q.UserUPN, q.KeyCtx ORDER BY q.TsUtc, q.RecordId
                              ROWS UNBOUNDED PRECEDING) AS Ordinal
    INTO #num
    FROM #seq AS q;

    DROP TABLE IF EXISTS #first;
    SELECT DISTINCT n.UserUPN, n.KeyCtx, n.Ordinal,
           FIRST_VALUE(n.App) OVER (PARTITION BY n.UserUPN, n.KeyCtx, n.Ordinal ORDER BY n.TsUtc, n.RecordId
                                    ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW)          AS PrimaryApp,
           FIRST_VALUE(n.IsCopilotLic) OVER (PARTITION BY n.UserUPN, n.KeyCtx, n.Ordinal ORDER BY n.TsUtc, n.RecordId
                                    ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW)          AS IsLic
    INTO #first
    FROM #num AS n;

    /* ---- 3. Directory attributes (optional table) ------------------------ */
    /* From on-premises AD (audit.UserDirectory, loaded by
       tools/Export-UsersWithHierarchy-ToSql.ps1). Without it, NULL. */

    DROP TABLE IF EXISTS #profile;
    CREATE TABLE #profile (UserPrincipalName NVARCHAR(450) COLLATE DATABASE_DEFAULT NOT NULL PRIMARY KEY,
                           Division NVARCHAR(400) NULL, Department NVARCHAR(400) NULL, JobTitle NVARCHAR(400) NULL, ManagerUpn NVARCHAR(1024) NULL);
    IF OBJECT_ID(N'audit.UserDirectory', N'U') IS NOT NULL
        EXEC (N'INSERT INTO #profile (UserPrincipalName, Division, Department, JobTitle, ManagerUpn)
                SELECT UserPrincipalName, Division, Department, JobTitle, ManagerUPN FROM audit.UserDirectory;');

    /* ---- 3b. Copilot license history (optional) --------------------------- */
    /*
        With @LicenseEventDatabase, a session's license flag is whether the
        user held a Copilot SKU when it started, not today's enrichment value.
        Per SKU: the latest change at or before the start decides; with none,
        the first change after it does in reverse (a removal means it was held
        before). A user with no Copilot change in the history keeps today's
        flag - nothing changed while the history was being kept - so the
        history must reach up to the enrichment run, or a change after its end
        is missed and today's flag is used for everything before it. Limit: a
        second Copilot SKU held throughout, unchanged, while another changed,
        is not seen.
    */
    /* COLLATE DATABASE_DEFAULT as #profile and #base have it (2.4.12): without
       it the columns took tempdb's collation, and on a database whose own
       differs, the comparison with the sessions below failed every build
       (Msg 468), even with no license database. TsUtc is the change's UTC
       time, compared with the session's UTC start (2.4.12; see section 2). */
    DROP TABLE IF EXISTS #lic;
    CREATE TABLE #lic (UserUPN NVARCHAR(512) COLLATE DATABASE_DEFAULT NOT NULL, Sku NVARCHAR(200) COLLATE DATABASE_DEFAULT NOT NULL,
                       TsUtc DATETIME2(0) NOT NULL, Added BIT NOT NULL);
    /* Several databases are read together: each license backfill covers a
       fixed range, and a change after one's end - the most recent, often -
       is only in a later one. Overlap is harmless: each lookup below takes a
       single latest or earliest change. */
    DECLARE @licDb SYSNAME, @licSql NVARCHAR(MAX);
    DECLARE licDbs CURSOR LOCAL FAST_FORWARD FOR SELECT Name FROM @licDbs;
    OPEN licDbs;
    FETCH NEXT FROM licDbs INTO @licDb;
    WHILE @@FETCH_STATUS = 0
    BEGIN
        SET @licSql = N'INSERT INTO #lic (UserUPN, Sku, TsUtc, Added)
            SELECT c.TargetUpn, c.Sku, CAST(c.CreatedDateTime AS DATETIME2(0)),
                   CASE WHEN c.Change = N''Added'' THEN 1 ELSE 0 END
            FROM ' + QUOTENAME(@licDb) + N'.audit.vLicenseSkuChange AS c
            WHERE c.TargetUpn IS NOT NULL AND c.Sku LIKE N''%COPILOT%'';';
        EXEC sys.sp_executesql @licSql;
        FETCH NEXT FROM licDbs INTO @licDb;
    END;
    CLOSE licDbs;
    DEALLOCATE licDbs;

    /* 2.5.4: sought by the lookups below, which ask it once per session. With no index each session read the whole
       history: over 154,530 Copilot changes, a night's 60,852 sessions took more than ten minutes in the lab, and
       now take seconds. Nonclustered: a UPN can be 1,024 bytes, over a clustered key's 900. */
    CREATE INDEX IX_lic ON #lic (UserUPN, Sku, TsUtc, Added);

    /* ---- 4. Sessions ----------------------------------------------------- */

    /* Start and end are the first and last prompt in UTC, shown in the
       reporting zone; the span is measured in UTC (2.4.12). The end is never
       shown before the start (2.4.12): in the hour clocks go back, a session
       begun before the change and ended after it ended, in local time,
       before it began. */
    DROP TABLE IF EXISTS #sess;
    SELECT g.UserUPN, g.KeyCtx, g.Ordinal, g.StartUtc, g.EndUtc,
           z.StartTs,
           CASE WHEN z.EndTs < z.StartTs THEN z.StartTs ELSE z.EndTs END AS EndTs,
           g.Prompts
    INTO #sess
    FROM (SELECT n.UserUPN, n.KeyCtx, n.Ordinal,
                 MIN(n.TsUtc) AS StartUtc,
                 MAX(n.TsUtc) AS EndUtc,
                 COUNT(*)     AS Prompts
          FROM #num AS n
          GROUP BY n.UserUPN, n.KeyCtx, n.Ordinal) AS g
    CROSS APPLY (SELECT CAST(g.StartUtc AT TIME ZONE 'UTC' AT TIME ZONE @ReportingTimeZone AS DATETIME2(0)) AS StartTs,
                        CAST(g.EndUtc   AT TIME ZONE 'UTC' AT TIME ZONE @ReportingTimeZone AS DATETIME2(0)) AS EndTs) AS z;

    /* A SKU added and removed in the same second: the addition is the later of the two, as bi.usp_RefreshBi reads
       it (2.5.4; which one a session took was left to the plan). */
    DROP TABLE IF EXISTS #histlic;
    SELECT s.UserUPN, s.KeyCtx, s.Ordinal,
           CASE WHEN NOT EXISTS (SELECT 1 FROM #lic AS l WHERE l.UserUPN = s.UserUPN) THEN NULL
                WHEN EXISTS (
                    SELECT 1
                    FROM (SELECT DISTINCT l.Sku FROM #lic AS l WHERE l.UserUPN = s.UserUPN) AS k
                    CROSS APPLY (SELECT COALESCE(
                        (SELECT TOP (1) CAST(b.Added AS INT) FROM #lic AS b
                         WHERE b.UserUPN = s.UserUPN AND b.Sku = k.Sku AND b.TsUtc <= s.StartUtc ORDER BY b.TsUtc DESC, b.Added DESC),
                        (SELECT TOP (1) 1 - CAST(a.Added AS INT) FROM #lic AS a
                         WHERE a.UserUPN = s.UserUPN AND a.Sku = k.Sku AND a.TsUtc > s.StartUtc ORDER BY a.TsUtc, a.Added)) AS Held) AS h
                    WHERE h.Held = 1) THEN CAST(1 AS BIT)
                ELSE CAST(0 AS BIT) END AS HistLic
    INTO #histlic
    FROM #sess AS s;

    /* Incremental: each stream's numbering carries on from its last session
       before the rebuilt days. */
    DROP TABLE IF EXISTS #base;
    CREATE TABLE #base (UserUPN NVARCHAR(4000) COLLATE DATABASE_DEFAULT NOT NULL,
                        KeyCtx  NVARCHAR(4000) COLLATE DATABASE_DEFAULT NOT NULL,
                        MaxOrdinal INT NOT NULL);
    IF @rebuiltFrom IS NOT NULL
        INSERT INTO #base (UserUPN, KeyCtx, MaxOrdinal)
        SELECT k.UserUPN, k.KeyCtx, m.MaxOrdinal
        FROM (SELECT DISTINCT UserUPN, KeyCtx FROM #sess) AS k
        CROSS APPLY (SELECT MAX(c.SessionOrdinal_Derived) AS MaxOrdinal
                     FROM audit.CopilotSession AS c
                     WHERE c.UserUPN = k.UserUPN AND c.AppContext = k.KeyCtx
                       AND c.SessionStart_PromptSubmittedDateTime < @rebuiltFrom) AS m
        WHERE m.MaxOrdinal IS NOT NULL;

    DROP TABLE IF EXISTS #out;
    SELECT s.KeyCtx AS AppContext, s.UserUPN, CAST(s.Ordinal + ISNULL(b.MaxOrdinal, 0) AS INT) AS Ordinal, s.StartTs, s.EndTs, s.Prompts,
           m.SpanMinutes, d.CalendarDays,
           f.PrimaryApp,
           COALESCE(c.AppCategory, N'In-App Copilot') AS AppCategory,
           COALESCE(h.HistLic, f.IsLic) AS IsLic,
           p.Division, p.Department, p.JobTitle, p.ManagerUpn
    INTO #out
    FROM #sess AS s
    JOIN #first AS f ON f.UserUPN = s.UserUPN AND f.KeyCtx = s.KeyCtx AND f.Ordinal = s.Ordinal
    JOIN #histlic AS h ON h.UserUPN = s.UserUPN AND h.KeyCtx = s.KeyCtx AND h.Ordinal = s.Ordinal
    LEFT JOIN audit.SessionAppCategory AS c ON c.App = f.PrimaryApp
    LEFT JOIN #profile AS p ON p.UserPrincipalName = s.UserUPN
    LEFT JOIN #base AS b ON b.UserUPN = s.UserUPN AND b.KeyCtx = s.KeyCtx
    CROSS APPLY (SELECT CAST(DATEDIFF_BIG(SECOND, s.StartUtc, s.EndUtc) / 60.0 AS DECIMAL(14, 4)) AS SpanMinutes) AS m
    CROSS APPLY (SELECT DATEDIFF(DAY, s.StartTs, s.EndTs) + 1 AS CalendarDays) AS d;

    /* ---- 5. Replace the sessions, then the weeks they fall in ------------ */
    /*
        Full: both tables emptied and rebuilt. Incremental: the sessions from
        @rebuiltFrom on are replaced, and every week containing a rebuilt day
        is recomputed. Either way the weeks are computed from
        audit.CopilotSession, so a week that straddles @rebuiltFrom counts
        its kept sessions too, and both paths give the same numbers.

        Weeks run Saturday-Friday and are labeled by the Friday; a session
        belongs to the week its last prompt falls in, which - a session never
        crossing midnight - is the week of its day. 1900-01-06 was a
        Saturday, so this is right whatever DATEFIRST says.
    */
    DECLARE @sessions INT, @weeks INT, @wk0 DATE = NULL, @weekFrom DATETIME2(0) = NULL;
    IF @rebuiltFrom IS NOT NULL
    BEGIN
        SET @wk0 = DATEADD(DAY, 6 - DATEDIFF(DAY, '19000106', CAST(@rebuiltFrom AS DATE)) % 7, CAST(@rebuiltFrom AS DATE));
        SET @weekFrom = CAST(DATEADD(DAY, -6, @wk0) AS DATETIME2(0));   -- the Saturday that week starts
    END;

    BEGIN TRANSACTION;

    IF @rebuiltFrom IS NULL
        TRUNCATE TABLE audit.CopilotSession;
    ELSE
        DELETE FROM audit.CopilotSession WHERE SessionStart_PromptSubmittedDateTime >= @rebuiltFrom;

    INSERT INTO audit.CopilotSession
        (AppContext, UserUPN, SessionOrdinal_Derived,
         SessionStart_PromptSubmittedDateTime, SessionEnd_PromptSubmittedDateTime,
         InferredSessionSpanMinutes, InferredSessionSpanHours, InferredSessionSpanDays,
         SessionTimeoutMinutes, CalendarDaysTouched_InSessionSpan, PromptCount, SessionType,
         PrimaryApp, AppCategory, IsM365CopilotLicensed, Division, Department, JobTitle, ManagerUPN,
         AvgMinutesPerPrompt_InferredSessionSpan, AvgMinutesPerPromptPerCalendarDay_InferredSpan,
         AvgMinutesPerPrompt_NormalizedByCalendarDays, MetricVersion)
    SELECT o.AppContext, o.UserUPN, o.Ordinal, o.StartTs, o.EndTs,
           ROUND(o.SpanMinutes, 2), ROUND(o.SpanMinutes / 60, 2), ROUND(o.SpanMinutes / 1440, 6),
           @SessionTimeoutMinutes, o.CalendarDays, o.Prompts,
           CASE WHEN o.Prompts = 1 THEN N'Single-Turn' ELSE N'Multi-Turn' END,
           o.PrimaryApp, o.AppCategory, o.IsLic, o.Division, o.Department, o.JobTitle, o.ManagerUpn,
           ROUND(o.SpanMinutes / o.Prompts, 2),
           ROUND(o.SpanMinutes / o.Prompts / o.CalendarDays, 2),
           ROUND((o.SpanMinutes / o.CalendarDays) / o.Prompts, 2),
           @MetricVersion
    FROM #out AS o;
    SET @sessions = @@ROWCOUNT;

    IF @rebuiltFrom IS NULL
        TRUNCATE TABLE audit.CopilotWeeklyRollup;
    ELSE
        DELETE FROM audit.CopilotWeeklyRollup WHERE WeekEnding >= @wk0;

    INSERT INTO audit.CopilotWeeklyRollup
        (WeekEnding, WeekYear, WeekNumber, WeekLabel, UserUPN, PrimaryApp, AppCategory, IsM365CopilotLicensed,
         WeeklySessionCount, WeeklyPromptCount,
         WeeklyInferredSessionSpanMinutes, WeeklyInferredSessionSpanHours, WeeklyInferredSessionSpanDays,
         ActiveDaysWithUsageInWeek, DistinctAppContextsInWeek,
         FirstPromptSubmittedDateTime_InWeek, LastPromptSubmittedDateTime_InWeek,
         Division, Department, JobTitle, ManagerUPN,
         AvgMinutesPerPrompt_InferredSpan_Weekly, MetricVersion)
    SELECT k.WeekEnding, k.WeekYear, k.WeekNumber,
           CONCAT(k.WeekYear, N'-W', RIGHT(CONCAT(N'0', k.WeekNumber), 2)),
           k.UserUPN, k.PrimaryApp, k.AppCategory, k.IsLic,
           COUNT(*), SUM(k.Prompts),
           ROUND(SUM(k.SpanMinutes), 2), ROUND(SUM(k.SpanMinutes) / 60, 2), ROUND(SUM(k.SpanMinutes) / 1440, 6),
           COUNT(DISTINCT CAST(k.StartTs AS DATE)), COUNT(DISTINCT k.AppContext),
           MIN(k.StartTs), MAX(k.EndTs),
           MAX(k.Division), MAX(k.Department), MAX(k.JobTitle), MAX(k.ManagerUpn),
           ROUND(SUM(k.SpanMinutes) / SUM(k.Prompts), 2),
           @MetricVersion
    FROM (
        SELECT c.AppContext, c.UserUPN,
               c.SessionStart_PromptSubmittedDateTime AS StartTs, c.SessionEnd_PromptSubmittedDateTime AS EndTs,
               c.PromptCount AS Prompts, c.PrimaryApp, c.AppCategory, c.IsM365CopilotLicensed AS IsLic,
               c.Division, c.Department, c.JobTitle, c.ManagerUPN AS ManagerUpn,
               /* the exact span, not the rounded column: weeks sum it before rounding. A span
                  is whole seconds and the column keeps hundredths of a minute, within 0.3 s,
                  so the seconds come back from it exactly (2.4.12: they were taken from the
                  stored start and end, local times an hour out across a daylight-saving change). */
               CAST(CAST(ROUND(c.InferredSessionSpanMinutes * 60, 0) AS BIGINT) / 60.0 AS DECIMAL(14, 4)) AS SpanMinutes,
               w.WeekEnding, YEAR(w.WeekEnding) AS WeekYear,
               DATEDIFF(DAY, ff.FirstFriday, w.WeekEnding) / 7 + 1 AS WeekNumber
        FROM audit.CopilotSession AS c
        CROSS APPLY (SELECT CAST(c.SessionEnd_PromptSubmittedDateTime AS DATE) AS EndDay) AS e
        CROSS APPLY (SELECT DATEADD(DAY, 6 - DATEDIFF(DAY, '19000106', e.EndDay) % 7, e.EndDay) AS WeekEnding) AS w
        CROSS APPLY (SELECT DATEFROMPARTS(YEAR(w.WeekEnding), 1, 1) AS Jan1) AS j
        CROSS APPLY (SELECT DATEADD(DAY, 6 - DATEDIFF(DAY, '19000106', j.Jan1) % 7, j.Jan1) AS FirstFriday) AS ff
        WHERE @weekFrom IS NULL OR c.SessionStart_PromptSubmittedDateTime >= @weekFrom
    ) AS k
    GROUP BY k.WeekEnding, k.WeekYear, k.WeekNumber, k.UserUPN, k.PrimaryApp, k.AppCategory, k.IsLic
    OPTION (RECOMPILE);
    SET @weeks = @@ROWCOUNT;

    INSERT INTO audit.CopilotSessionRun
        (RunStartUtc, RunEndUtc, RowsRead, RowsWithOriginalAppContext, RowsRecoveredFromLocation,
         RowsSkippedMissingContext, RowsSkippedMissingUser, RowsSkippedBadTimestamp,
         SessionsWritten, WeeklyGroups, SessionTimeoutMinutes, ContextFallback, ReportingTimeZone, LicenseEventDatabase,
         MetricVersion, RebuiltFrom)
    VALUES (@started, SYSUTCDATETIME(), @read, ISNULL(@orig, 0), ISNULL(@recovered, 0),
            ISNULL(@noCtx, 0), ISNULL(@noUser, 0), ISNULL(@badTs, 0),
            @sessions, @weeks, @SessionTimeoutMinutes, @ContextFallback, @ReportingTimeZone, @LicenseEventDatabase,
            @MetricVersion, @rebuiltFrom);

    COMMIT TRANSACTION;

    SET @msg = CONCAT(N'Sessions built', CASE WHEN @rebuiltFrom IS NULL THEN N'' ELSE
                          CONCAT(N' from ', CONVERT(NVARCHAR(10), @rebuiltFrom, 23), N' (incremental)') END, N': ',
                      FORMAT(@read, 'N0'), N' row(s) read, ',
                      FORMAT(@sessions, 'N0'), N' session(s), ', FORMAT(@weeks, 'N0'), N' weekly group(s)',
                      CASE WHEN @rebuiltFrom IS NULL THEN N'' ELSE N' written' END, N'.');
    RAISERROR(@msg, 0, 1) WITH NOWAIT;
    IF ISNULL(@noCtx, 0) + ISNULL(@noUser, 0) + ISNULL(@badTs, 0) > 0
    BEGIN
        SET @msg = CONCAT(N'  Skipped: ', FORMAT(ISNULL(@noCtx, 0), 'N0'), N' with no context (',
                          @ContextFallback, N' fallback), ', FORMAT(ISNULL(@noUser, 0), 'N0'), N' with no user, ',
                          FORMAT(ISNULL(@badTs, 0), 'N0'), N' with no timestamp. ',
                          FORMAT(ISNULL(@recovered, 0), 'N0'), N' took their context from Location.');
        RAISERROR(@msg, 0, 1) WITH NOWAIT;
    END;
END;
GO
```

### C.10 `sql/Create-CopilotBiSchema.sql`

Copilot database. The `bi` star schema: dimensions, facts and the views a reporting tool reads. 684 lines.

```sql
/*
    The bi schema: a star schema for a Power BI semantic model
    ------------------------------------------------------------------------
    The audit tables are shaped for loading and auditing, not for a model: the
    flattened table has a row-sized text column for every list, users are UPN
    strings, and license history lives in other databases. A semantic model
    over 20-40M interactions wants narrow facts with integer keys, small
    dimensions, and nothing high-cardinality it does not need. This is that
    layer. bi.usp_RefreshBi (Create-CopilotBiProcedure.sql) fills it, and each
    -Incremental run calls it after the other derived tables.

      Facts                          one row per
        bi.FactInteraction           Copilot interaction (the big one)
        bi.FactSession               inferred session (audit.CopilotSession)
        bi.FactLicenseChange         SKU added to or removed from a user
        bi.FactLicensedDay           user holding a Copilot license, per day

      Dimensions
        bi.DimDate                   with the Saturday-Friday week of the rollup
        bi.DimUser                   AD attributes, the org chain, today's license
        bi.DimApp                    app and its Copilot Chat / In-App category
        bi.DimAgent, bi.DimModel, bi.DimSku, bi.DimLicenseStatus

    The views with plain names (bi.Interactions, bi.Users, bi.Calendar ...)
    are what the model imports: the same columns less lineage (RecordId,
    CreatedUtc, FirstSeenUtc). Import them, not the tables.

    Left out on purpose, and kept in audit.*: RecordId, ThreadId, app context,
    accessed resource names and URLs, sensitivity label IDs and the JSON.
    Each is close to one value per row, so in an import model they would cost
    more memory than everything else together. A DirectQuery table over
    audit.RawLogFlattened serves drill-through to a record if it is needed.

    Keys are stable: a user, app, agent, model or SKU keeps its key from the
    first refresh that saw it, so a model refreshing only recent partitions
    stays consistent with the partitions it did not refresh. Every fact key is
    NOT NULL; a missing user, app, agent or model maps to a named row
    ('(unknown user)', '(unknown)', '(no agent)', '(not reported)').

    Idempotent. Single batch (no GO). References no audit object, so it can be
    run before or after the rest. Requires SQL Server 2016+ (2017+ for the
    procedure).
*/
SET NOCOUNT ON;

IF SCHEMA_ID(N'bi') IS NULL
    EXEC (N'CREATE SCHEMA bi AUTHORIZATION dbo;');

/* ---- Dimensions ---------------------------------------------------------- */

IF OBJECT_ID(N'bi.DimDate', N'U') IS NULL
    CREATE TABLE bi.DimDate
    (
        DateKey          INT           NOT NULL CONSTRAINT PK_DimDate PRIMARY KEY,   -- yyyymmdd
        [Date]           DATE          NOT NULL,
        [Year]           SMALLINT      NOT NULL,
        [Quarter]        TINYINT       NOT NULL,
        MonthNumber      TINYINT       NOT NULL,
        [MonthName]      NVARCHAR(10)  NOT NULL,
        YearMonth        CHAR(7)       NOT NULL,   -- 2026-09
        DayOfWeekNumber  TINYINT       NOT NULL,   -- 1 = Monday ... 7 = Sunday
        DayOfWeekName    NVARCHAR(10)  NOT NULL,
        IsWeekday        BIT           NOT NULL,
        /* The weekly rollup's week: Saturday to Friday, labeled by the
           Friday; week 1 of a year ends on its first Friday. */
        WeekEnding       DATE          NOT NULL,
        WeekYear         SMALLINT      NOT NULL,
        WeekNumber       TINYINT       NOT NULL,
        WeekLabel        CHAR(8)       NOT NULL    -- 2026-W38
    );

IF OBJECT_ID(N'bi.DimUser', N'U') IS NULL
BEGIN
    CREATE TABLE bi.DimUser
    (
        UserKey              INT IDENTITY(1, 1) NOT NULL CONSTRAINT PK_DimUser PRIMARY KEY,
        UserPrincipalName    NVARCHAR(512)  NOT NULL CONSTRAINT UQ_DimUser_Upn UNIQUE,
        DisplayName          NVARCHAR(400)  NULL,
        Division             NVARCHAR(200)  NULL,
        Department           NVARCHAR(200)  NULL,
        JobTitle             NVARCHAR(200)  NULL,
        OfficeLocation       NVARCHAR(200)  NULL,
        City                 NVARCHAR(200)  NULL,
        Country              NVARCHAR(200)  NULL,
        CompanyName          NVARCHAR(200)  NULL,
        EmployeeType         NVARCHAR(32)   NULL,   -- EMPLOYEE | CONTRACTOR ...
        IsManager            BIT            NULL,
        AccountEnabled       BIT            NULL,
        ManagerUpn           NVARCHAR(512)  NULL,
        ManagerName          NVARCHAR(256)  NULL,
        /* The management chain from AD: OrgLevel1 is the top (the CEO),
           OrgLevel<OrgDepth> the direct manager. A ragged hierarchy, top down. */
        OrgDepth             TINYINT        NULL,
        OrgLevel1            NVARCHAR(256)  NULL,
        OrgLevel2            NVARCHAR(256)  NULL,
        OrgLevel3            NVARCHAR(256)  NULL,
        OrgLevel4            NVARCHAR(256)  NULL,
        OrgLevel5            NVARCHAR(256)  NULL,
        OrgLevel6            NVARCHAR(256)  NULL,
        OrgLevel7            NVARCHAR(256)  NULL,
        OrgLevel8            NVARCHAR(256)  NULL,
        OrgLevel9            NVARCHAR(256)  NULL,
        OrgLevel10           NVARCHAR(256)  NULL,
        OrgLevel11           NVARCHAR(256)  NULL,
        OrgLevel12           NVARCHAR(256)  NULL,
        OrgLevel13           NVARCHAR(256)  NULL,
        OrgLevel14           NVARCHAR(256)  NULL,
        OrgLevel15           NVARCHAR(256)  NULL,
        /* Found (in AD) | NotFound (left Entra) | Not looked up (neither source) */
        DirectoryStatus      NVARCHAR(20)   NOT NULL CONSTRAINT DF_DimUser_Directory DEFAULT (N'Not looked up'),
        /* Today's license, from the enrichment. Licensed | Not licensed | Unknown.
           License at the time of use is on the facts, not here. */
        CopilotLicenseToday  NVARCHAR(12)   NOT NULL CONSTRAINT DF_DimUser_License DEFAULT (N'Unknown'),
        CopilotSku           NVARCHAR(100)  NULL,
        LicensedSinceUtc     DATETIME2(0)   NULL,
        FirstSeenUtc         DATETIME2(0)   NOT NULL CONSTRAINT DF_DimUser_FirstSeen DEFAULT (SYSUTCDATETIME())
    );
    INSERT INTO bi.DimUser (UserPrincipalName) VALUES (N'(unknown user)');
END;

/* 2.0.0: user attributes come from on-premises AD (audit.UserDirectory), which
   adds the employee type, manager name and org chain; Entra's Member/Guest
   UserType goes. Existing tables are altered in place. */
IF OBJECT_ID(N'bi.DimUser', N'U') IS NOT NULL
BEGIN
    IF COL_LENGTH(N'bi.DimUser', N'EmployeeType') IS NULL ALTER TABLE bi.DimUser ADD EmployeeType NVARCHAR(32) NULL;
    IF COL_LENGTH(N'bi.DimUser', N'IsManager')    IS NULL ALTER TABLE bi.DimUser ADD IsManager BIT NULL;
    IF COL_LENGTH(N'bi.DimUser', N'ManagerName')  IS NULL ALTER TABLE bi.DimUser ADD ManagerName NVARCHAR(256) NULL;
    IF COL_LENGTH(N'bi.DimUser', N'OrgDepth')     IS NULL ALTER TABLE bi.DimUser ADD OrgDepth TINYINT NULL;
    IF COL_LENGTH(N'bi.DimUser', N'OrgLevel1') IS NULL ALTER TABLE bi.DimUser ADD OrgLevel1 NVARCHAR(256) NULL;
    IF COL_LENGTH(N'bi.DimUser', N'OrgLevel2') IS NULL ALTER TABLE bi.DimUser ADD OrgLevel2 NVARCHAR(256) NULL;
    IF COL_LENGTH(N'bi.DimUser', N'OrgLevel3') IS NULL ALTER TABLE bi.DimUser ADD OrgLevel3 NVARCHAR(256) NULL;
    IF COL_LENGTH(N'bi.DimUser', N'OrgLevel4') IS NULL ALTER TABLE bi.DimUser ADD OrgLevel4 NVARCHAR(256) NULL;
    IF COL_LENGTH(N'bi.DimUser', N'OrgLevel5') IS NULL ALTER TABLE bi.DimUser ADD OrgLevel5 NVARCHAR(256) NULL;
    IF COL_LENGTH(N'bi.DimUser', N'OrgLevel6') IS NULL ALTER TABLE bi.DimUser ADD OrgLevel6 NVARCHAR(256) NULL;
    IF COL_LENGTH(N'bi.DimUser', N'OrgLevel7') IS NULL ALTER TABLE bi.DimUser ADD OrgLevel7 NVARCHAR(256) NULL;
    IF COL_LENGTH(N'bi.DimUser', N'OrgLevel8') IS NULL ALTER TABLE bi.DimUser ADD OrgLevel8 NVARCHAR(256) NULL;
    IF COL_LENGTH(N'bi.DimUser', N'OrgLevel9') IS NULL ALTER TABLE bi.DimUser ADD OrgLevel9 NVARCHAR(256) NULL;
    IF COL_LENGTH(N'bi.DimUser', N'OrgLevel10') IS NULL ALTER TABLE bi.DimUser ADD OrgLevel10 NVARCHAR(256) NULL;
    IF COL_LENGTH(N'bi.DimUser', N'OrgLevel11') IS NULL ALTER TABLE bi.DimUser ADD OrgLevel11 NVARCHAR(256) NULL;
    IF COL_LENGTH(N'bi.DimUser', N'OrgLevel12') IS NULL ALTER TABLE bi.DimUser ADD OrgLevel12 NVARCHAR(256) NULL;
    IF COL_LENGTH(N'bi.DimUser', N'OrgLevel13') IS NULL ALTER TABLE bi.DimUser ADD OrgLevel13 NVARCHAR(256) NULL;
    IF COL_LENGTH(N'bi.DimUser', N'OrgLevel14') IS NULL ALTER TABLE bi.DimUser ADD OrgLevel14 NVARCHAR(256) NULL;
    IF COL_LENGTH(N'bi.DimUser', N'OrgLevel15') IS NULL ALTER TABLE bi.DimUser ADD OrgLevel15 NVARCHAR(256) NULL;
    IF COL_LENGTH(N'bi.DimUser', N'UserType') IS NOT NULL ALTER TABLE bi.DimUser DROP COLUMN UserType;
END;

IF OBJECT_ID(N'bi.DimApp', N'U') IS NULL
BEGIN
    CREATE TABLE bi.DimApp
    (
        AppKey        INT IDENTITY(1, 1) NOT NULL CONSTRAINT PK_DimApp PRIMARY KEY,
        App           NVARCHAR(200) NOT NULL CONSTRAINT UQ_DimApp_App UNIQUE,   -- Word, Teams, Office (Copilot Chat) ...
        AppCategory   NVARCHAR(50)  NOT NULL CONSTRAINT DF_DimApp_Category DEFAULT (N'In-App Copilot'),
        FirstSeenUtc  DATETIME2(0)  NOT NULL CONSTRAINT DF_DimApp_FirstSeen DEFAULT (SYSUTCDATETIME())
    );
    INSERT INTO bi.DimApp (App) VALUES (N'(unknown)');
END;

/*
    The agent an interaction went to. A record names its agent in AppIdentity:
    Copilot.Studio.<environment>-<tenant id>-<schema name> or
    Copilot.Studio.Declarative.T_<id> for a Copilot Studio agent,
    MicrosoftAgent.<name>.<...> for one of Microsoft's. Some records carry an
    AgentId as well and some of the same agent's do not (seen on two live
    tenants), so the agent is the one AppIdentity names, and AgentId only where
    AppIdentity names none (2.4.11; before, AgentId came first, which no real
    record had). AgentSource says which: bi.fnAgentIdentity, below, is the rule.

    AgentName is a label, not the identity: the friendly name the audit log
    gave the agent on its latest record that carries one ($.AgentName, kept for
    every agent in audit.AgentCatalog), else a name read from the id. A maker
    renaming an agent changes the label of the same AgentKey, so its history
    stays in one piece. bi.usp_NameAgents sets it, at the end of every refresh.
*/
IF OBJECT_ID(N'bi.DimAgent', N'U') IS NULL
BEGIN
    CREATE TABLE bi.DimAgent
    (
        AgentKey      INT IDENTITY(1, 1) NOT NULL CONSTRAINT PK_DimAgent PRIMARY KEY,
        AgentId       NVARCHAR(400) NOT NULL CONSTRAINT UQ_DimAgent_Id UNIQUE,   -- the AppIdentity that names the agent, else AgentId
        AgentName     NVARCHAR(400) NULL,   -- the latest friendly name the audit log gave it, else one read from the id
        AgentSource   NVARCHAR(30)  NULL,   -- Copilot Studio | Microsoft | Agent ID | (no agent)
        FirstSeenUtc  DATETIME2(0)  NOT NULL CONSTRAINT DF_DimAgent_FirstSeen DEFAULT (SYSUTCDATETIME())
    );
    /* Dynamic: this script is one batch, compiled whole before it runs, and
       on a database with the older DimAgent a static reference to
       AgentSource would fail to compile before the ALTER below could add it. */
    EXEC (N'INSERT INTO bi.DimAgent (AgentId, AgentName, AgentSource) VALUES (N''(none)'', N''(no agent)'', N''(no agent)'');');
END;

/* AgentSource arrived after the first bi schema; add it in place. */
IF COL_LENGTH(N'bi.DimAgent', N'AgentSource') IS NULL
BEGIN
    ALTER TABLE bi.DimAgent ADD AgentSource NVARCHAR(30) NULL;
    EXEC (N'UPDATE bi.DimAgent SET AgentSource = N''(no agent)'' WHERE AgentId = N''(none)'';');
END;

/* 2.4.11: whether AgentName is the audit log's own name for the agent (1), or one read from its id (0). */
IF COL_LENGTH(N'bi.DimAgent', N'NamedInAudit') IS NULL
    ALTER TABLE bi.DimAgent ADD NamedInAudit BIT NOT NULL CONSTRAINT DF_DimAgent_NamedInAudit DEFAULT (0);

/*
    bi.fnAgentIdentity: who the agent of a record is, from its AppIdentity and
    AgentId. One row: the id bi.DimAgent keys on, where it came from, and the
    name to show when the audit log gives none.

      AppIdentity Copilot.Studio.<...>   that AppIdentity, 'Copilot Studio'. The name is the schema name after the
                                         tenant id (Copilot.Studio.Default-<tenant id>-crd20_expenseClaims gives
                                         crd20_expenseClaims); where the identity ends in an id instead
                                         (Copilot.Studio.Declarative.T_<id>), everything after Copilot.Studio.
      AppIdentity MicrosoftAgent.<...>   that AppIdentity, 'Microsoft'. The name is Microsoft's own, the part after
                                         MicrosoftAgent. (MicrosoftAgent.OutlookDraftsChatSteering.BuiltIn_<...>
                                         gives OutlookDraftsChatSteering).
      any other AppIdentity, an AgentId  the AgentId, 'Agent ID', no name of its own.
      neither                            '(none)', '(no agent)': Copilot itself.
*/
EXEC (N'CREATE OR ALTER FUNCTION bi.fnAgentIdentity (@AppIdentity NVARCHAR(400), @AgentId NVARCHAR(400))
RETURNS TABLE
AS
RETURN
SELECT COALESCE(CASE WHEN s.Src IS NOT NULL THEN a.Ident END, a.Aid, N''(none)'') AS AgentId,
       COALESCE(s.Src, CASE WHEN a.Aid IS NOT NULL THEN N''Agent ID'' END, N''(no agent)'') AS AgentSource,
       CAST(NULLIF(CASE s.Src
            WHEN N''Copilot Studio'' THEN
                CASE WHEN CHARINDEX(N''-'', r.Rest) > 0 AND r.Tail NOT LIKE REPLICATE(N''[0-9a-fA-F]'', 12) THEN r.Tail ELSE r.Rest END
            WHEN N''Microsoft'' THEN
                CASE WHEN CHARINDEX(N''.'', r.Rest) > 1 THEN LEFT(r.Rest, CHARINDEX(N''.'', r.Rest) - 1) ELSE r.Rest END
            END, N'''') AS NVARCHAR(400)) AS DerivedName
FROM (SELECT NULLIF(LTRIM(RTRIM(@AgentId)), N'''') AS Aid, NULLIF(LTRIM(RTRIM(@AppIdentity)), N'''') AS Ident) AS a
CROSS APPLY (SELECT CASE WHEN a.Ident LIKE N''Copilot.Studio.%''  THEN N''Copilot Studio''
                         WHEN a.Ident LIKE N''MicrosoftAgent.%''  THEN N''Microsoft'' END AS Src) AS s
/* Both prefixes are 15 characters. Rest: what follows the prefix. Tail: what follows the last hyphen. */
CROSS APPLY (SELECT STUFF(a.Ident, 1, 15, N'''') AS Rest,
                    CASE WHEN CHARINDEX(N''-'', REVERSE(a.Ident)) > 1 THEN RIGHT(a.Ident, CHARINDEX(N''-'', REVERSE(a.Ident)) - 1) END AS Tail) AS r;');

IF OBJECT_ID(N'bi.DimModel', N'U') IS NULL
BEGIN
    CREATE TABLE bi.DimModel
    (
        ModelKey       INT IDENTITY(1, 1) NOT NULL CONSTRAINT PK_DimModel PRIMARY KEY,
        ModelProvider  NVARCHAR(200) NOT NULL,
        ModelName      NVARCHAR(200) NOT NULL,
        FirstSeenUtc   DATETIME2(0)  NOT NULL CONSTRAINT DF_DimModel_FirstSeen DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT UQ_DimModel UNIQUE (ModelProvider, ModelName)
    );
    INSERT INTO bi.DimModel (ModelProvider, ModelName) VALUES (N'(not reported)', N'(not reported)');
END;

IF OBJECT_ID(N'bi.DimSku', N'U') IS NULL
    CREATE TABLE bi.DimSku
    (
        SkuKey        INT IDENTITY(1, 1) NOT NULL CONSTRAINT PK_DimSku PRIMARY KEY,
        Sku           NVARCHAR(200) NOT NULL CONSTRAINT UQ_DimSku_Sku UNIQUE,   -- SkuPartNumber, e.g. Microsoft_365_Copilot
        IsCopilot     BIT           NOT NULL,
        FirstSeenUtc  DATETIME2(0)  NOT NULL CONSTRAINT DF_DimSku_FirstSeen DEFAULT (SYSUTCDATETIME())
    );

/* License held at the time of use. Unknown is kept apart from Not licensed:
   a user who has left the directory, with no license history, is not known
   to have been unlicensed. */
IF OBJECT_ID(N'bi.DimLicenseStatus', N'U') IS NULL
BEGIN
    CREATE TABLE bi.DimLicenseStatus
    (
        LicenseStatusKey  TINYINT      NOT NULL CONSTRAINT PK_DimLicenseStatus PRIMARY KEY,
        LicenseStatus     NVARCHAR(20) NOT NULL
    );
    INSERT INTO bi.DimLicenseStatus (LicenseStatusKey, LicenseStatus)
    VALUES (0, N'Unknown'), (1, N'Licensed'), (2, N'Not licensed');
END;

/* ---- 2.2.0: what the rest of the record says ----------------------------- */

/* CopilotEventData.LicenseType: the license the record itself says the user
   had (Premium, Starter ...). Beside LicenseStatus, which comes from the
   license history, not the record. */
IF OBJECT_ID(N'bi.DimLicenseType', N'U') IS NULL
BEGIN
    CREATE TABLE bi.DimLicenseType
    (
        LicenseTypeKey  INT IDENTITY(1, 1) NOT NULL CONSTRAINT PK_DimLicenseType PRIMARY KEY,
        LicenseType     NVARCHAR(32) NOT NULL CONSTRAINT UQ_DimLicenseType UNIQUE
    );
    INSERT INTO bi.DimLicenseType (LicenseType) VALUES (N'(not reported)');
END;

/* Sensitivity labels, by id: on the interaction (CopilotEventData.SensitivityLabelId)
   and on the resources it read. Names are not in the audit data; the
   dashboard's Admin > Reference data holds them (dash.SensitivityLabel). */
IF OBJECT_ID(N'bi.DimSensitivityLabel', N'U') IS NULL
BEGIN
    CREATE TABLE bi.DimSensitivityLabel
    (
        SensitivityLabelKey  INT IDENTITY(1, 1) NOT NULL CONSTRAINT PK_DimSensitivityLabel PRIMARY KEY,
        LabelId              UNIQUEIDENTIFIER NULL,   -- NULL: the '(none)' row
        FirstSeenUtc         DATETIME2(0) NOT NULL CONSTRAINT DF_DimSensitivityLabel_FirstSeen DEFAULT (SYSUTCDATETIME())
    );
    CREATE UNIQUE INDEX UQ_DimSensitivityLabel ON bi.DimSensitivityLabel (LabelId);
    INSERT INTO bi.DimSensitivityLabel (LabelId) VALUES (NULL);
END;

/* What a resource Copilot read was, as a person would name it (2.4.6): a file's extension (docx, xlsx, pptx, csv ...)
   from AccessedResources[].Name, and for anything else its Type (EmailMessage, TeamsMessage, StreamVideo, whiteboard ...). */
IF OBJECT_ID(N'bi.DimFileType', N'U') IS NULL
BEGIN
    CREATE TABLE bi.DimFileType
    (
        FileTypeKey  INT IDENTITY(1, 1) NOT NULL CONSTRAINT PK_DimFileType PRIMARY KEY,
        FileType     NVARCHAR(64) NOT NULL CONSTRAINT UQ_DimFileType UNIQUE
    );
    INSERT INTO bi.DimFileType (FileType) VALUES (N'(not reported)');
END;

/* AccessedResources[].Type: File, EmailMessage, TeamsMessage, WebSearchQuery, a URL ... */
IF OBJECT_ID(N'bi.DimResourceType', N'U') IS NULL
BEGIN
    CREATE TABLE bi.DimResourceType
    (
        ResourceTypeKey  INT IDENTITY(1, 1) NOT NULL CONSTRAINT PK_DimResourceType PRIMARY KEY,
        ResourceType     NVARCHAR(200) NOT NULL CONSTRAINT UQ_DimResourceType UNIQUE
    );
    INSERT INTO bi.DimResourceType (ResourceType) VALUES (N'(not reported)');
END;

/* AccessedResources[].Action: Read, Create ... */
IF OBJECT_ID(N'bi.DimResourceAction', N'U') IS NULL
BEGIN
    CREATE TABLE bi.DimResourceAction
    (
        ResourceActionKey  INT IDENTITY(1, 1) NOT NULL CONSTRAINT PK_DimResourceAction PRIMARY KEY,
        ResourceAction     NVARCHAR(450) NOT NULL CONSTRAINT UQ_DimResourceAction UNIQUE   -- as wide as audit.RawLogAccessedResource.[Action]
    );
    INSERT INTO bi.DimResourceAction (ResourceAction) VALUES (N'(not reported)');
END;
/* 2.4.1: 200 was narrower than the flattened [Action] (450), so a longer action failed the refresh. Widened in place. */
IF COL_LENGTH(N'bi.DimResourceAction', N'ResourceAction') < 900
BEGIN
    ALTER TABLE bi.DimResourceAction DROP CONSTRAINT UQ_DimResourceAction;
    ALTER TABLE bi.DimResourceAction ALTER COLUMN ResourceAction NVARCHAR(450) NOT NULL;
    ALTER TABLE bi.DimResourceAction ADD CONSTRAINT UQ_DimResourceAction UNIQUE (ResourceAction);
END;

/* The site a resource lives on: https://host/sites/name (or /teams/, /personal/),
   otherwise the host. Whole URLs are left out on purpose: they are nearly one
   per row and would cost more than the rest of the model. */
IF OBJECT_ID(N'bi.DimSite', N'U') IS NULL
BEGIN
    CREATE TABLE bi.DimSite
    (
        SiteKey       INT IDENTITY(1, 1) NOT NULL CONSTRAINT PK_DimSite PRIMARY KEY,
        Site          NVARCHAR(400) NOT NULL CONSTRAINT UQ_DimSite UNIQUE,
        Host          NVARCHAR(400) NOT NULL,
        IsPersonal    BIT           NOT NULL,   -- a OneDrive (/personal/) site: it names its owner
        FirstSeenUtc  DATETIME2(0)  NOT NULL CONSTRAINT DF_DimSite_FirstSeen DEFAULT (SYSUTCDATETIME())
    );
    INSERT INTO bi.DimSite (Site, Host, IsPersonal) VALUES (N'(none)', N'(none)', 0);
END;

/* ---- Facts --------------------------------------------------------------- */

/*
    One row per interaction. Clustered columnstore: rows arrive in time order,
    so a refresh's DELETE of its window and the model's partition queries both
    touch only the row groups for that period.
*/
IF OBJECT_ID(N'bi.FactInteraction', N'U') IS NULL
BEGIN
    CREATE TABLE bi.FactInteraction
    (
        RecordId                 NVARCHAR(450) NOT NULL,   -- lineage; not imported
        CreatedUtc               DATETIME2(0)  NOT NULL,   -- lineage and the refresh window; not imported
        /* Local time in the reporting time zone, truncated to the hour: the
           column for the model's incremental-refresh RangeStart/RangeEnd
           filter. An hour, not a second, so it has ~8,760 values a year
           rather than millions, and costs almost nothing in the model. */
        ActivityHour             DATETIME2(0)  NOT NULL,
        DateKey                  INT           NOT NULL,
        HourOfDay                TINYINT       NOT NULL,
        UserKey                  INT           NOT NULL,
        AppKey                   INT           NOT NULL,
        AgentKey                 INT           NOT NULL,
        ModelKey                 INT           NOT NULL,
        LicenseStatusKey         TINYINT       NOT NULL,
        PromptCount              INT           NOT NULL,
        AccessedResourceCount    INT           NOT NULL,
        UsedWebSearch            BIT           NOT NULL,
        JailbreakDetected        BIT           NOT NULL,
        PromptInjectionDetected  BIT           NOT NULL,
        HasBlockedResource       BIT           NOT NULL,
        HasSensitivityLabel      BIT           NOT NULL
    );
    CREATE CLUSTERED COLUMNSTORE INDEX CCI_FactInteraction ON bi.FactInteraction;
END;

/*
    PremiumUse and UseCaseKey (2.4.0), set by bi.usp_RefreshBi and nowhere else: every other place
    (the dashboard's rollups and pages, the bi views, Power BI) reads these columns, so each rule
    has one definition.
      PremiumUse  a licensed interaction that needed the Premium license (see UseKind, below).
      UseCaseKey  AppKey x 4 + web search x 2 + agent: the key into bi.UseCases.
    An app's category changes PremiumUse, so set categories before a build, or rebuild bi after.

    UseKind (2.4.8): what the record itself says about where Copilot ran, from its AppIdentity, and
    whether it read work content. NULL on a row written before 2.4.8 (the installer rebuilds bi once).
      0  not licensed use: web Copilot Chat (Copilot.M365Copilot.WebChat) that read no work content,
         or a Copilot Studio agent (Copilot.Studio.*), which can be metered without a license
      1  licensed use: Copilot inside an app (Copilot.M365Copilot.Apps), Copilot Chat in work mode
         (Copilot.M365Copilot.Bizchat) whether or not it read anything, a Microsoft agent
         (MicrosoftAgent.*), or web Copilot Chat that read work content
      2  the record names no such identity, and it read no work content: the app's category decides
         (In-App Copilot other than Copilot Studio is licensed use; Copilot Chat is not)
      3  as 2, but it read work content: licensed use in Copilot Chat too
    Work content is a resource in SharePoint or OneDrive, Outlook or Teams, or a mail, a Teams
    message, a meeting or a connector item. The web pages web search cites are listed as resources
    too, which is why "read a resource" alone says nothing.
    PremiumUse = licensed at the time AND (UseKind 1, or UseKind 2 or 3 in an in-app category, or
    UseKind 3 in Copilot Chat).
*/
IF COL_LENGTH(N'bi.FactInteraction', N'PremiumUse') IS NULL
    ALTER TABLE bi.FactInteraction ADD
        PremiumUse  BIT NOT NULL CONSTRAINT DF_FactInteraction_Premium DEFAULT (0),
        UseCaseKey  INT NOT NULL CONSTRAINT DF_FactInteraction_UseCase DEFAULT (0);
IF COL_LENGTH(N'bi.FactInteraction', N'UseKind') IS NULL
    ALTER TABLE bi.FactInteraction ADD UseKind TINYINT NULL;

/* 2.2.0 columns, added in place; rows written before them carry the defaults
   until the next full rebuild. */
IF COL_LENGTH(N'bi.FactInteraction', N'LicenseTypeKey') IS NULL
    ALTER TABLE bi.FactInteraction ADD
        LicenseTypeKey         INT     NOT NULL CONSTRAINT DF_FactInteraction_LicenseType DEFAULT (1),
        SensitivityLabelKey    INT     NOT NULL CONSTRAINT DF_FactInteraction_Label DEFAULT (1),   -- the interaction's own label
        MessageCount           INT     NOT NULL CONSTRAINT DF_FactInteraction_Messages DEFAULT (0),
        MemoryUpdated          BIT     NOT NULL CONSTRAINT DF_FactInteraction_Memory DEFAULT (0),
        DlpEvaluationDeferred  BIT     NOT NULL CONSTRAINT DF_FactInteraction_Dlp DEFAULT (0),
        CalledAnotherAgent     BIT     NOT NULL CONSTRAINT DF_FactInteraction_Target DEFAULT (0);   -- TargetAgentName set

/* The agent called, by name: the dashboard's hand-off report reads it here rather
   than joining 40M facts back to audit.RawLogFlattened. */
IF COL_LENGTH(N'bi.FactInteraction', N'TargetAgentName') IS NULL
    ALTER TABLE bi.FactInteraction ADD TargetAgentName NVARCHAR(400) NULL;

/*
    One row per resource an interaction read (CopilotEventData.AccessedResources),
    from audit.RawLogAccessedResource: what kind, which site, which label,
    whether it was blocked or carried a prompt injection. Clustered
    columnstore, refreshed with the interactions it belongs to.
*/
IF OBJECT_ID(N'bi.FactAccessedResource', N'U') IS NULL
BEGIN
    CREATE TABLE bi.FactAccessedResource
    (
        RecordId             NVARCHAR(450) NOT NULL,   -- lineage; not imported
        Ordinal              SMALLINT      NOT NULL,   -- lineage; not imported
        CreatedUtc           DATETIME2(0)  NOT NULL,   -- lineage and the refresh window; not imported
        ActivityHour         DATETIME2(0)  NOT NULL,
        DateKey              INT           NOT NULL,
        UserKey              INT           NOT NULL,
        AppKey               INT           NOT NULL,
        ResourceTypeKey      INT           NOT NULL,
        ResourceActionKey    INT           NOT NULL,
        SiteKey              INT           NOT NULL,
        SensitivityLabelKey  INT           NOT NULL,
        IsBlocked            BIT           NOT NULL,
        XPIADetected         BIT           NOT NULL,
        FileTypeKey          INT           NOT NULL CONSTRAINT DF_FactAccessedResource_FileType DEFAULT (1)   -- 2.4.6
    );
    CREATE CLUSTERED COLUMNSTORE INDEX CCI_FactAccessedResource ON bi.FactAccessedResource;
END;
/* The file type (2.4.6): rows loaded before it are '(not reported)' until the installer refreshes bi in full once. */
IF COL_LENGTH(N'bi.FactAccessedResource', N'FileTypeKey') IS NULL
    ALTER TABLE bi.FactAccessedResource ADD FileTypeKey INT NOT NULL CONSTRAINT DF_FactAccessedResource_FileType DEFAULT (1);

/* One row per session in audit.CopilotSession. A session never crosses
   midnight (a new day starts a new one), so its start date is its only date. */
IF OBJECT_ID(N'bi.FactSession', N'U') IS NULL
    CREATE TABLE bi.FactSession
    (
        SessionKey           BIGINT        NOT NULL CONSTRAINT PK_FactSession PRIMARY KEY,
        SessionStart         DATETIME2(0)  NOT NULL,   -- reporting time zone
        SessionEnd           DATETIME2(0)  NOT NULL,
        DateKey              INT           NOT NULL,
        UserKey              INT           NOT NULL,
        AppKey               INT           NOT NULL,   -- the first prompt's app
        LicenseStatusKey     TINYINT       NOT NULL,
        PromptCount          INT           NOT NULL,
        SpanMinutes          DECIMAL(12, 2) NOT NULL,
        IsMultiTurn          BIT           NOT NULL
    );

IF OBJECT_ID(N'bi.FactLicenseChange', N'U') IS NULL
    CREATE TABLE bi.FactLicenseChange
    (
        RecordId     VARCHAR(64)    NOT NULL,   -- lineage; not imported
        ChangedUtc   DATETIME2(0)   NOT NULL,
        DateKey      INT            NOT NULL,
        UserKey      INT            NOT NULL,   -- whose license changed
        SkuKey       INT            NOT NULL,
        Change       SMALLINT       NOT NULL,   -- +1 added, -1 removed
        ChangedBy    NVARCHAR(512)  NULL,       -- the admin who made it
        CONSTRAINT PK_FactLicenseChange PRIMARY KEY (RecordId, SkuKey, Change)
    );

/* The adoption denominator: one row per user per day they held a Copilot
   license at the end of that day. Active licensed users / licensed users,
   and licensed users with no interactions, both come from this. */
IF OBJECT_ID(N'bi.FactLicensedDay', N'U') IS NULL
    CREATE TABLE bi.FactLicensedDay
    (
        DateKey  INT NOT NULL,
        UserKey  INT NOT NULL,
        CONSTRAINT PK_FactLicensedDay PRIMARY KEY (DateKey, UserKey)
    );

/* One row per refresh, with the settings it ran under. */
IF OBJECT_ID(N'bi.RefreshRun', N'U') IS NULL
    CREATE TABLE bi.RefreshRun
    (
        RunId                 INT IDENTITY(1, 1) NOT NULL CONSTRAINT PK_BiRefreshRun PRIMARY KEY,
        StartedUtc            DATETIME2(0)   NOT NULL,
        EndedUtc              DATETIME2(0)   NOT NULL,
        SinceUtc              DATETIME2(0)   NULL,       -- NULL = a full rebuild
        ReportingTimeZone     SYSNAME        NOT NULL,
        LicenseEventDatabase  NVARCHAR(1000) NULL,
        InteractionRows       BIGINT         NOT NULL,   -- written by this run
        InteractionsSkipped   BIGINT         NOT NULL,   -- no timestamp
        SessionRows           INT            NOT NULL,   -- written by this run
        LicenseChangeRows     INT            NOT NULL,
        LicensedDayRows       INT            NOT NULL,
        SessionRunId          INT            NULL        -- the latest audit.CopilotSessionRun it reflects
    );

/*
    2.4.9: a full rebuild empties bi.FactInteraction and fills it month by month, outside a transaction (it is
    too large for one). Stopped part way, it leaves the months it did not reach missing, and nothing said so.
    bi.usp_RefreshBi puts a row here before it empties the fact and takes it out when it has finished; while
    a row is here, every refresh is a full one.
*/
IF OBJECT_ID(N'bi.RebuildInProgress', N'U') IS NULL
    CREATE TABLE bi.RebuildInProgress (StartedUtc DATETIME2(0) NOT NULL);

/* Incremental sessions: which session build a refresh caught up to. */
IF COL_LENGTH(N'bi.RefreshRun', N'SessionRunId') IS NULL
    ALTER TABLE bi.RefreshRun ADD SessionRunId INT NULL;

/* ---- What the model imports ---------------------------------------------- */

IF COL_LENGTH(N'bi.RefreshRun', N'AccessedResourceRows') IS NULL
    ALTER TABLE bi.RefreshRun ADD AccessedResourceRows BIGINT NULL;

/* 2.4.0: the first day with an interaction in an app whose category this run changed (NULL: none).
   Premium use and the dashboard's Copilot Chat counts depend on the category, so the dashboard's
   next refresh rolls up again from that day. */
IF COL_LENGTH(N'bi.RefreshRun', N'RecategorizedFromDateKey') IS NULL
    ALTER TABLE bi.RefreshRun ADD RecategorizedFromDateKey INT NULL;

/*
    PremiumUse (2.3.0; the rule is bi.FactInteraction.UseKind's since 2.4.8): a licensed interaction that needed the Premium license - Copilot inside Word, Excel,
    PowerPoint, Outlook or Teams (Copilot Studio agents can be metered without one), or Copilot Chat reading
    work content (files, mail, sites) rather than the web. The same rule as dash.UserDay.PremiumUse, which the
    dashboard's train and revoke recommendations use. UseCaseKey (2.3.0): the app, whether it searched the web,
    and whether an agent answered, as one key into bi.UseCases (the dashboard's use-case rules).
*/
EXEC (N'CREATE OR ALTER VIEW bi.Interactions AS
SELECT f.ActivityHour, f.DateKey, f.HourOfDay, f.UserKey, f.AppKey, f.AgentKey, f.ModelKey, f.LicenseStatusKey,
       f.LicenseTypeKey, f.SensitivityLabelKey,
       f.PromptCount, f.MessageCount, f.AccessedResourceCount, f.UsedWebSearch, f.JailbreakDetected, f.PromptInjectionDetected,
       f.HasBlockedResource, f.HasSensitivityLabel, f.MemoryUpdated, f.DlpEvaluationDeferred, f.CalledAnotherAgent,
       f.PremiumUse, f.UseCaseKey
FROM bi.FactInteraction AS f;');

EXEC (N'CREATE OR ALTER VIEW bi.AccessedResources AS
SELECT ActivityHour, DateKey, UserKey, AppKey, ResourceTypeKey, ResourceActionKey, SiteKey, SensitivityLabelKey,
       IsBlocked, XPIADetected, FileTypeKey
FROM bi.FactAccessedResource;');

EXEC (N'CREATE OR ALTER VIEW bi.FileTypes AS
SELECT FileTypeKey, FileType FROM bi.DimFileType;');

EXEC (N'CREATE OR ALTER VIEW bi.LicenseTypes AS
SELECT LicenseTypeKey, LicenseType FROM bi.DimLicenseType;');

EXEC (N'CREATE OR ALTER VIEW bi.SensitivityLabels AS
SELECT SensitivityLabelKey, COALESCE(LOWER(CAST(LabelId AS NVARCHAR(36))), N''(none)'') AS LabelId FROM bi.DimSensitivityLabel;');

EXEC (N'CREATE OR ALTER VIEW bi.ResourceTypes AS
SELECT ResourceTypeKey, ResourceType FROM bi.DimResourceType;');

EXEC (N'CREATE OR ALTER VIEW bi.ResourceActions AS
SELECT ResourceActionKey, ResourceAction FROM bi.DimResourceAction;');

/* A OneDrive site is named after its owner (/personal/firstname_lastname_...): hidden in privacy mode. */
EXEC (N'CREATE OR ALTER VIEW bi.Sites AS
SELECT SiteKey, CASE WHEN IsPersonal = 1 AND EXISTS (SELECT 1 FROM audit.PipelineSetting WHERE Name = N''PrivacyMode'' AND Value = N''Pseudonymize'') THEN N''OneDrive (personal)'' ELSE Site END AS Site, Host, IsPersonal
FROM bi.DimSite;');

EXEC (N'CREATE OR ALTER VIEW bi.Sessions AS
SELECT SessionKey, SessionStart, SessionEnd, DateKey, UserKey, AppKey, LicenseStatusKey,
       PromptCount, SpanMinutes, IsMultiTurn
FROM bi.FactSession;');

/* Copilot Studio authoring: one row per create, publish, share, update, delete or component
   change, copied whole from every license database's audit.AgentEvent at each refresh (2.3.0). */
IF OBJECT_ID(N'bi.FactAgentEvent', N'U') IS NULL
    CREATE TABLE bi.FactAgentEvent
    (
        RecordId      VARCHAR(64)     NOT NULL CONSTRAINT PK_FactAgentEvent PRIMARY KEY,
        EventUtc      DATETIME2(0)    NOT NULL,
        DateKey       INT             NOT NULL,
        Action        VARCHAR(16)     NOT NULL,
        Operation     NVARCHAR(100)   NOT NULL,
        ActorUpn      NVARCHAR(512)   NULL,
        ActorKey      INT             NULL,       -- bi.DimUser, where the maker is in it
        BotId         NVARCHAR(100)   NULL,
        BotSchemaName NVARCHAR(200)   NULL,
        ComponentName NVARCHAR(200)   NULL,
        ComponentType NVARCHAR(100)   NULL,
        INDEX IX_FactAgentEvent_Date (DateKey)
    );
EXEC (N'CREATE OR ALTER VIEW bi.AgentEvents AS
SELECT RecordId, EventUtc, DateKey, Action, Operation, ActorKey, BotId, BotSchemaName, ComponentName, ComponentType
FROM bi.FactAgentEvent;');

/* ChangedBy names the administrator: blank in privacy mode. */
EXEC (N'CREATE OR ALTER VIEW bi.LicenseChanges AS
SELECT ChangedUtc, DateKey, UserKey, SkuKey, Change, CASE WHEN EXISTS (SELECT 1 FROM audit.PipelineSetting WHERE Name = N''PrivacyMode'' AND Value = N''Pseudonymize'') THEN NULL ELSE ChangedBy END AS ChangedBy
FROM bi.FactLicenseChange;');

EXEC (N'CREATE OR ALTER VIEW bi.LicensedDays AS
SELECT DateKey, UserKey FROM bi.FactLicensedDay;');

EXEC (N'CREATE OR ALTER VIEW bi.Calendar AS
SELECT DateKey, [Date], [Year], [Quarter], MonthNumber, [MonthName], YearMonth, DayOfWeekNumber,
       DayOfWeekName, IsWeekday, WeekEnding, WeekYear, WeekNumber, WeekLabel
FROM bi.DimDate;');

/*
    People, with every name and sign-in pseudonymized when privacy mode is on
    (audit.PipelineSetting PrivacyMode = 'Pseudonymize', set in the dashboard's
    Admin > Privacy and retention): a salted hash, the same for the same person
    everywhere, so counts and org slicing still work but nobody is named.
*/
EXEC (N'CREATE OR ALTER VIEW bi.Users AS
SELECT u.UserKey,
       CASE WHEN pm.Pseudonymize = 1 AND u.UserPrincipalName IS NOT NULL THEN N''P-'' + CONVERT(NVARCHAR(16), HASHBYTES(''SHA2_256'', pm.Salt + LOWER(u.UserPrincipalName)), 2) ELSE u.UserPrincipalName END AS UserPrincipalName,
       CASE WHEN pm.Pseudonymize = 1 AND u.DisplayName IS NOT NULL THEN N''P-'' + CONVERT(NVARCHAR(16), HASHBYTES(''SHA2_256'', pm.Salt + LOWER(u.DisplayName)), 2) ELSE u.DisplayName END AS DisplayName,
       CASE WHEN pm.Pseudonymize = 1 AND u.ManagerUpn IS NOT NULL THEN N''P-'' + CONVERT(NVARCHAR(16), HASHBYTES(''SHA2_256'', pm.Salt + LOWER(u.ManagerUpn)), 2) ELSE u.ManagerUpn END AS ManagerUpn,
       CASE WHEN pm.Pseudonymize = 1 AND u.ManagerName IS NOT NULL THEN N''P-'' + CONVERT(NVARCHAR(16), HASHBYTES(''SHA2_256'', pm.Salt + LOWER(u.ManagerName)), 2) ELSE u.ManagerName END AS ManagerName,
       u.Division, u.Department, u.JobTitle, u.OfficeLocation, u.City,
       u.Country, u.CompanyName, u.EmployeeType, u.IsManager, u.AccountEnabled, u.OrgDepth,
       CASE WHEN pm.Pseudonymize = 1 AND u.OrgLevel1 IS NOT NULL THEN N''P-'' + CONVERT(NVARCHAR(16), HASHBYTES(''SHA2_256'', pm.Salt + LOWER(u.OrgLevel1)), 2) ELSE u.OrgLevel1 END AS OrgLevel1,
       CASE WHEN pm.Pseudonymize = 1 AND u.OrgLevel2 IS NOT NULL THEN N''P-'' + CONVERT(NVARCHAR(16), HASHBYTES(''SHA2_256'', pm.Salt + LOWER(u.OrgLevel2)), 2) ELSE u.OrgLevel2 END AS OrgLevel2,
       CASE WHEN pm.Pseudonymize = 1 AND u.OrgLevel3 IS NOT NULL THEN N''P-'' + CONVERT(NVARCHAR(16), HASHBYTES(''SHA2_256'', pm.Salt + LOWER(u.OrgLevel3)), 2) ELSE u.OrgLevel3 END AS OrgLevel3,
       CASE WHEN pm.Pseudonymize = 1 AND u.OrgLevel4 IS NOT NULL THEN N''P-'' + CONVERT(NVARCHAR(16), HASHBYTES(''SHA2_256'', pm.Salt + LOWER(u.OrgLevel4)), 2) ELSE u.OrgLevel4 END AS OrgLevel4,
       CASE WHEN pm.Pseudonymize = 1 AND u.OrgLevel5 IS NOT NULL THEN N''P-'' + CONVERT(NVARCHAR(16), HASHBYTES(''SHA2_256'', pm.Salt + LOWER(u.OrgLevel5)), 2) ELSE u.OrgLevel5 END AS OrgLevel5,
       CASE WHEN pm.Pseudonymize = 1 AND u.OrgLevel6 IS NOT NULL THEN N''P-'' + CONVERT(NVARCHAR(16), HASHBYTES(''SHA2_256'', pm.Salt + LOWER(u.OrgLevel6)), 2) ELSE u.OrgLevel6 END AS OrgLevel6,
       CASE WHEN pm.Pseudonymize = 1 AND u.OrgLevel7 IS NOT NULL THEN N''P-'' + CONVERT(NVARCHAR(16), HASHBYTES(''SHA2_256'', pm.Salt + LOWER(u.OrgLevel7)), 2) ELSE u.OrgLevel7 END AS OrgLevel7,
       CASE WHEN pm.Pseudonymize = 1 AND u.OrgLevel8 IS NOT NULL THEN N''P-'' + CONVERT(NVARCHAR(16), HASHBYTES(''SHA2_256'', pm.Salt + LOWER(u.OrgLevel8)), 2) ELSE u.OrgLevel8 END AS OrgLevel8,
       CASE WHEN pm.Pseudonymize = 1 AND u.OrgLevel9 IS NOT NULL THEN N''P-'' + CONVERT(NVARCHAR(16), HASHBYTES(''SHA2_256'', pm.Salt + LOWER(u.OrgLevel9)), 2) ELSE u.OrgLevel9 END AS OrgLevel9,
       CASE WHEN pm.Pseudonymize = 1 AND u.OrgLevel10 IS NOT NULL THEN N''P-'' + CONVERT(NVARCHAR(16), HASHBYTES(''SHA2_256'', pm.Salt + LOWER(u.OrgLevel10)), 2) ELSE u.OrgLevel10 END AS OrgLevel10,
       CASE WHEN pm.Pseudonymize = 1 AND u.OrgLevel11 IS NOT NULL THEN N''P-'' + CONVERT(NVARCHAR(16), HASHBYTES(''SHA2_256'', pm.Salt + LOWER(u.OrgLevel11)), 2) ELSE u.OrgLevel11 END AS OrgLevel11,
       CASE WHEN pm.Pseudonymize = 1 AND u.OrgLevel12 IS NOT NULL THEN N''P-'' + CONVERT(NVARCHAR(16), HASHBYTES(''SHA2_256'', pm.Salt + LOWER(u.OrgLevel12)), 2) ELSE u.OrgLevel12 END AS OrgLevel12,
       CASE WHEN pm.Pseudonymize = 1 AND u.OrgLevel13 IS NOT NULL THEN N''P-'' + CONVERT(NVARCHAR(16), HASHBYTES(''SHA2_256'', pm.Salt + LOWER(u.OrgLevel13)), 2) ELSE u.OrgLevel13 END AS OrgLevel13,
       CASE WHEN pm.Pseudonymize = 1 AND u.OrgLevel14 IS NOT NULL THEN N''P-'' + CONVERT(NVARCHAR(16), HASHBYTES(''SHA2_256'', pm.Salt + LOWER(u.OrgLevel14)), 2) ELSE u.OrgLevel14 END AS OrgLevel14,
       CASE WHEN pm.Pseudonymize = 1 AND u.OrgLevel15 IS NOT NULL THEN N''P-'' + CONVERT(NVARCHAR(16), HASHBYTES(''SHA2_256'', pm.Salt + LOWER(u.OrgLevel15)), 2) ELSE u.OrgLevel15 END AS OrgLevel15,
       u.DirectoryStatus, u.CopilotLicenseToday, u.CopilotSku, u.LicensedSinceUtc
FROM bi.DimUser AS u
CROSS JOIN (SELECT CAST(CASE WHEN EXISTS (SELECT 1 FROM audit.PipelineSetting WHERE Name = N''PrivacyMode'' AND Value = N''Pseudonymize'') THEN 1 ELSE 0 END AS BIT) AS Pseudonymize,
                   COALESCE((SELECT Value FROM audit.PipelineSetting WHERE Name = N''PseudonymSalt''), N'''') AS Salt) AS pm;');

EXEC (N'CREATE OR ALTER VIEW bi.Apps AS
SELECT AppKey, App, AppCategory FROM bi.DimApp;');

EXEC (N'CREATE OR ALTER VIEW bi.Agents AS
SELECT AgentKey, AgentId, AgentName, AgentSource, NamedInAudit FROM bi.DimAgent;');

EXEC (N'CREATE OR ALTER VIEW bi.Models AS
SELECT ModelKey, ModelProvider, ModelName FROM bi.DimModel;');

EXEC (N'CREATE OR ALTER VIEW bi.Skus AS
SELECT SkuKey, Sku, IsCopilot FROM bi.DimSku;');

EXEC (N'CREATE OR ALTER VIEW bi.LicenseStatuses AS
SELECT LicenseStatusKey, LicenseStatus FROM bi.DimLicenseStatus;');
```

### C.11 `sql/Create-CopilotBiProcedure.sql`

Copilot database. `bi.usp_RefreshBi`, which fills the `bi` schema. 964 lines.

```sql
/*
    bi.usp_RefreshBi
    ------------------------------------------------------------------------
    Fills the bi star schema (Create-CopilotBiSchema.sql) from the audit
    tables. -Incremental calls it after each load; run it by hand after a
    backfill, or after changing a setting.

      @SinceUtc              Interactions from this UTC time are deleted and
                             rebuilt; older ones are left as they are. NULL
                             (the default) rebuilds them all. The first run
                             always rebuilds, whatever is passed.
      @ReportingTimeZone     The zone dates and hours are reported in. Use the
                             one the sessions were built with. Changing it
                             needs a full rebuild (@SinceUtc = NULL); a partial
                             refresh in a different zone is refused.
      @LicenseEventDatabase  One database holding the license events
                             (audit.vLicenseSkuChange), or several
                             comma-separated. The same value as
                             usp_BuildCopilotSessions takes.

    What it does, in order:

      1. License history: every SKU change from @LicenseEventDatabase, and
         from it, for each user, the periods they held a Copilot SKU.
      2. Sessions, directory profiles, and keys for any user, app or SKU
         they bring that has not been seen before.
      3. bi.DimDate, extended to cover the data.
      4. bi.FactInteraction: the window from @SinceUtc deleted, then written
         again from audit.RawLogFlattened a month at a time, adding keys for
         new users, apps, agents, models, license types and labels as it goes.
         bi.FactAccessedResource the same way, alongside, from
         audit.RawLogAccessedResource (2.2.0), with keys for new resource
         types, actions, sites and labels.
      5. Dimension attributes: directory profile, today's license, app
         category. Small; rewritten every run.
      6. bi.FactSession: only what the session builds since the last refresh
         replaced - every session after a full session build, else the days
         the incremental ones rebuilt (audit.CopilotSessionRun.RebuiltFrom).
         bi.FactLicenseChange and bi.FactLicensedDay: rebuilt whole; they are
         small next to the interactions.

    License at the time of use (FactInteraction.LicenseStatusKey,
    FactLicensedDay):

      * A user with Copilot SKU changes in the license history held a license
        at a moment if any Copilot SKU's latest change at or before it was an
        addition - or, before their first change, if that first change was a
        removal. The same rule as usp_BuildCopilotSessions.
      * A user with no change in the history falls back to the enrichment
        (audit.CopilotLicense): licensed from LicensedSinceUtc if they hold
        one today, else not licensed. Sessions use today's flag throughout
        instead, ignoring LicensedSinceUtc, so for such a user a session
        before that date can say Licensed where its interactions say not.
      * Where the enrichment looked a user up after their last change in the
        history and contradicts it (2.4.12): a user it finds licensed whom
        the history leaves unlicensed (only another Copilot SKU added and
        taken away, say) is licensed from LicensedSinceUtc; and an account it
        no longer finds, which the history still has licensed, is licensed in
        bi.FactLicensedDay only to the last day it was used. The history
        knows a person by the sign-in of the time, so a renamed or deleted
        account has no removal there. A renamed account is still two users
        to bi, one per sign-in: the days between the old one's last use and
        the new one's LicensedSinceUtc, if that is earlier, count under both.
      * Neither: Unknown. So is a user the directory no longer has, unless
        the history has them.

    A row's license status is fixed when the row is written. A license change
    that reaches the history later corrects only rows rebuilt after it; run
    with @SinceUtc = NULL (and fully refresh the model) to correct them all.

    One refresh at a time (2.4.12): a second one waits for the first to
    finish (sp_getapplock on 'bi.usp_RefreshBi', held by the session).

    Reads audit.RawLogFlattened, audit.CopilotLicense, audit.CopilotSession
    and audit.SessionAppCategory, so run Create-CopilotFlattenedSchema.sql
    and Create-CopilotSessionSchema.sql first (the session tables may be
    empty). audit.UserDirectory (on-premises AD, loaded by
    tools/Export-UsersWithHierarchy-ToSql.ps1) is optional: without it users
    have no directory attributes. Requires SQL Server 2017+.
*/
SET NOCOUNT ON;

/*
    bi.usp_NameAgents (2.4.11): the name of every agent in bi.DimAgent.

    The friendly name the audit log gave the agent on its latest record that
    carries one, from audit.AgentCatalog, which the flatten keeps from every
    record ever flattened: so a refresh of two days names an agent last used
    a month ago as well as one used today, and a maker's rename shows the
    night its first record arrives. Where the log never named the agent, the
    name read from its id (bi.fnAgentIdentity). NamedInAudit says which.

    The name is a label on the agent's key, never the key: a rename changes
    what the same AgentKey is called, in every period, and nothing else. The
    dashboard shows an administrator's own name for the agent first, where
    one was given (dash.AgentAlias).

    bi.usp_RefreshBi runs it at the end of every refresh; on its own it takes
    a moment, and Install-CopilotAudit.ps1 runs it after reading the names of
    records flattened before 2.4.11.
*/
IF OBJECT_ID(N'bi.usp_NameAgents', N'P') IS NULL
    EXEC (N'CREATE PROCEDURE bi.usp_NameAgents AS BEGIN SET NOCOUNT ON; END;');
GO

ALTER PROCEDURE bi.usp_NameAgents
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    IF OBJECT_ID(N'bi.DimAgent', N'U') IS NULL RETURN;

    /* One name for each agent as bi keys it: several catalog rows can be one agent (records with and without an AgentId). */
    CREATE TABLE #names (AgentId NVARCHAR(400) COLLATE DATABASE_DEFAULT NOT NULL PRIMARY KEY, AgentName NVARCHAR(400) COLLATE DATABASE_DEFAULT NOT NULL);
    IF OBJECT_ID(N'audit.AgentCatalog', N'U') IS NOT NULL
        INSERT INTO #names (AgentId, AgentName)
        SELECT x.AgentId, x.AgentName
        FROM (SELECT i.AgentId, c.AgentName,
                     ROW_NUMBER() OVER (PARTITION BY i.AgentId ORDER BY c.NameSeenUtc DESC, c.AgentCatalogId DESC) AS rn
              FROM audit.AgentCatalog AS c
              CROSS APPLY bi.fnAgentIdentity(c.AppIdentity, c.AgentId) AS i
              WHERE c.AgentName IS NOT NULL AND i.AgentId <> N'(none)') AS x
        WHERE x.rn = 1;

    /* 2.5.6: the agent inventory's name first, where the workbook loaded by tools/Import-AgentWorkbook.ps1 has one;
       the audit log's for an agent it does not list (one since deleted) or leaves unnamed. */
    CREATE TABLE #inv (AgentKey INT NOT NULL PRIMARY KEY, AgentName NVARCHAR(400) COLLATE DATABASE_DEFAULT NOT NULL);
    IF OBJECT_ID(N'dash.AgentInventory', N'U') IS NOT NULL
        EXEC (N'INSERT INTO #inv (AgentKey, AgentName)
                SELECT AgentKey, AgentName
                FROM (SELECT AgentKey, LTRIM(RTRIM(AgentName)) AS AgentName,
                             ROW_NUMBER() OVER (PARTITION BY AgentKey ORDER BY InProduction DESC, LastPublishedAt DESC, InventoryAgentId) AS rn
                      FROM dash.AgentInventory WHERE AgentKey IS NOT NULL AND NULLIF(LTRIM(RTRIM(AgentName)), N'''') IS NOT NULL) AS x
                WHERE rn = 1;');

    UPDATE d
    SET AgentName    = v.AgentName,
        NamedInAudit = v.NamedInAudit,
        AgentSource  = i.AgentSource
    FROM bi.DimAgent AS d
    CROSS APPLY bi.fnAgentIdentity(d.AgentId, d.AgentId) AS i
    LEFT JOIN #names AS n ON n.AgentId = d.AgentId
    LEFT JOIN #inv AS w ON w.AgentKey = d.AgentKey
    CROSS APPLY (SELECT COALESCE(w.AgentName, n.AgentName, i.DerivedName) AS AgentName,
                        CAST(CASE WHEN n.AgentName IS NULL THEN 0 ELSE 1 END AS BIT) AS NamedInAudit) AS v
    WHERE d.AgentId <> N'(none)'
      AND (ISNULL(d.AgentName, N'') <> ISNULL(v.AgentName, N'') COLLATE Latin1_General_100_BIN2
           OR d.NamedInAudit <> v.NamedInAudit OR ISNULL(d.AgentSource, N'') <> i.AgentSource);

    DECLARE @msg NVARCHAR(200) = CONCAT(N'Agents: ', (SELECT COUNT(*) FROM bi.DimAgent WHERE NamedInAudit = 1), N' of ',
                                        (SELECT COUNT(*) FROM bi.DimAgent WHERE AgentId <> N'(none)'), N' named by the audit log.');
    RAISERROR(@msg, 0, 1) WITH NOWAIT;
END;
GO

IF OBJECT_ID(N'bi.usp_RefreshBi', N'P') IS NULL
    EXEC (N'CREATE PROCEDURE bi.usp_RefreshBi AS BEGIN SET NOCOUNT ON; END;');
GO

ALTER PROCEDURE bi.usp_RefreshBi
    @SinceUtc             DATETIME2(0)   = NULL,
    @ReportingTimeZone    SYSNAME        = NULL,
    @LicenseEventDatabase NVARCHAR(1000) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    SET @ReportingTimeZone = COALESCE(@ReportingTimeZone, audit.ReportingTimeZone());   -- the zone Install-CopilotAudit.ps1 -ReportingTimeZone stored

    DECLARE @started DATETIME2(0) = SYSUTCDATETIME(), @msg NVARCHAR(1000), @sql NVARCHAR(MAX);

    IF OBJECT_ID(N'bi.FactInteraction', N'U') IS NULL
        THROW 50010, N'The bi schema is missing - run sql/Create-CopilotBiSchema.sql first.', 1;
    IF COL_LENGTH(N'bi.DimUser', N'OrgDepth') IS NULL
        THROW 50016, N'bi.DimUser predates 2.0.0 - run sql/Create-CopilotBiSchema.sql again to upgrade it.', 1;
    IF COL_LENGTH(N'bi.RefreshRun', N'SessionRunId') IS NULL
        THROW 50017, N'bi.RefreshRun predates incremental sessions - run sql/Create-CopilotBiSchema.sql again to upgrade it.', 1;
    IF COL_LENGTH(N'audit.CopilotSessionRun', N'RebuiltFrom') IS NULL
        THROW 50018, N'audit.CopilotSessionRun predates incremental sessions - run sql/Create-CopilotSessionSchema.sql again.', 1;
    IF OBJECT_ID(N'audit.RawLogFlattened', N'U') IS NULL OR OBJECT_ID(N'audit.CopilotLicense', N'U') IS NULL
        THROW 50011, N'audit.RawLogFlattened is missing - run sql/Create-CopilotFlattenedSchema.sql and audit.usp_FlattenCopilotAudit first.', 1;
    IF OBJECT_ID(N'audit.CopilotSession', N'U') IS NULL OR OBJECT_ID(N'audit.SessionAppCategory', N'U') IS NULL
       OR OBJECT_ID(N'audit.CopilotSessionRun', N'U') IS NULL
        THROW 50012, N'audit.CopilotSession is missing - run sql/Create-CopilotSessionSchema.sql first (the tables may stay empty).', 1;
    IF NOT EXISTS (SELECT 1 FROM sys.time_zone_info WHERE name = @ReportingTimeZone)
        THROW 50013, N'@ReportingTimeZone is not a name in sys.time_zone_info.', 1;

    DECLARE @licDbs TABLE (Name SYSNAME NOT NULL PRIMARY KEY);
    IF @LicenseEventDatabase IS NOT NULL
        INSERT INTO @licDbs (Name)
        SELECT DISTINCT LTRIM(RTRIM(value)) FROM STRING_SPLIT(@LicenseEventDatabase, N',')
        WHERE LTRIM(RTRIM(value)) <> N'';
    IF EXISTS (SELECT 1 FROM @licDbs WHERE OBJECT_ID(QUOTENAME(Name) + N'.audit.vLicenseSkuChange', N'V') IS NULL)
        THROW 50014, N'A database in @LicenseEventDatabase has no audit.vLicenseSkuChange - run sql/Create-LicenseEventSchema.sql there.', 1;

    /* 2.4.12: one refresh at a time. A full rebuild commits month by month and holds nothing on the fact between
       months, so a refresh that started meanwhile found its mark, emptied the fact under it and filled it again
       alongside it: every month after that point was in the fact twice, and in the rollups and the model after it.
       A second refresh now waits for the first to finish, then finds no mark and refreshes its own window. The lock
       is the session's, as a full rebuild commits as it goes: released at the end, on an error (the CATCH at the
       end), or when the session closes. */
    DECLARE @lock INT;
    EXEC @lock = sys.sp_getapplock @Resource = N'bi.usp_RefreshBi', @LockMode = 'Exclusive', @LockOwner = 'Session', @LockTimeout = 0;
    IF @lock < 0
    BEGIN
        RAISERROR('Another refresh of the bi schema is running: waiting for it to finish.', 0, 1) WITH NOWAIT;
        EXEC @lock = sys.sp_getapplock @Resource = N'bi.usp_RefreshBi', @LockMode = 'Exclusive', @LockOwner = 'Session', @LockTimeout = -1;
    END;
    IF @lock < 0
        THROW 50019, N'Another refresh of the bi schema is running, and this one could not wait for it to finish.', 1;

    BEGIN TRY
    /* The first refresh builds everything, whatever window was asked for:
       an empty fact refreshed from yesterday would have nothing before it. */
    IF NOT EXISTS (SELECT 1 FROM bi.RefreshRun)
    BEGIN
        IF @SinceUtc IS NOT NULL
            RAISERROR('First refresh of the bi schema: building every interaction, not only the window asked for.', 0, 1) WITH NOWAIT;
        SET @SinceUtc = NULL;
    END;

    /* 2.4.9: a full rebuild that was stopped part way left months missing: this run finishes it. One that is still
       running holds the lock above (2.4.12), so the mark found here is always one that was stopped. */
    IF @SinceUtc IS NOT NULL AND OBJECT_ID(N'bi.RebuildInProgress', N'U') IS NOT NULL AND EXISTS (SELECT 1 FROM bi.RebuildInProgress)
    BEGIN
        RAISERROR('A full rebuild of the bi schema did not finish: building every interaction, not only the window asked for.', 0, 1) WITH NOWAIT;
        SET @SinceUtc = NULL;
    END;

    /* Dates and hours already written were computed in the last run's zone;
       mixing zones inside one table would misplace every row near midnight. */
    DECLARE @lastZone SYSNAME = (SELECT TOP (1) ReportingTimeZone FROM bi.RefreshRun ORDER BY RunId DESC);
    IF @SinceUtc IS NOT NULL AND @lastZone <> @ReportingTimeZone
    BEGIN
        SET @msg = CONCAT(N'bi.FactInteraction was built in ', @lastZone, N'. Changing to ', @ReportingTimeZone,
                          N' needs a full rebuild: run with @SinceUtc = NULL.');
        THROW 50015, @msg, 1;
    END;

    DECLARE @sessionZone SYSNAME = (SELECT TOP (1) ReportingTimeZone FROM audit.CopilotSessionRun ORDER BY RunId DESC);
    IF @sessionZone IS NOT NULL AND @sessionZone <> @ReportingTimeZone
    BEGIN
        SET @msg = CONCAT(N'Warning: the sessions were built in ', @sessionZone, N', the interactions are being built in ',
                          @ReportingTimeZone, N'. Their dates will not line up near midnight.');
        RAISERROR(@msg, 0, 1) WITH NOWAIT;
    END;

    /* ---- 1. License history ---------------------------------------------- */

    /* Every SKU change, from every license database. The databases can
       overlap (each license backfill covers its own range), hence DISTINCT. */
    DROP TABLE IF EXISTS #chgAll;
    CREATE TABLE #chgAll
    (
        RecordId    VARCHAR(64)   COLLATE DATABASE_DEFAULT NOT NULL,
        ChangedUtc  DATETIME2(0)  NOT NULL,
        Upn         NVARCHAR(512) COLLATE DATABASE_DEFAULT NOT NULL,
        Sku         NVARCHAR(200) COLLATE DATABASE_DEFAULT NOT NULL,
        Added       BIT           NOT NULL,
        ActorUpn    NVARCHAR(512) COLLATE DATABASE_DEFAULT NULL
    );
    DECLARE @licDb SYSNAME;
    DECLARE licDbs CURSOR LOCAL FAST_FORWARD FOR SELECT Name FROM @licDbs;
    OPEN licDbs;
    FETCH NEXT FROM licDbs INTO @licDb;
    WHILE @@FETCH_STATUS = 0
    BEGIN
        SET @sql = N'INSERT INTO #chgAll (RecordId, ChangedUtc, Upn, Sku, Added, ActorUpn)
            SELECT c.RecordId, CAST(c.CreatedDateTime AS DATETIME2(0)), LEFT(c.TargetUpn, 512), LEFT(c.Sku, 200),
                   CASE WHEN c.Change = N''Added'' THEN 1 ELSE 0 END, LEFT(c.ActorUpn, 512)
            FROM ' + QUOTENAME(@licDb) + N'.audit.vLicenseSkuChange AS c
            WHERE c.TargetUpn IS NOT NULL AND c.Sku IS NOT NULL;';
        EXEC (@sql);
        FETCH NEXT FROM licDbs INTO @licDb;
    END;
    CLOSE licDbs;
    DEALLOCATE licDbs;

    DROP TABLE IF EXISTS #chg;
    SELECT DISTINCT RecordId, ChangedUtc, Upn, Sku, Added, ActorUpn INTO #chg FROM #chgAll;

    /* Today's license from the enrichment. SkuPartNumber, LicensedSinceUtc
       and DirectoryStatus arrive with Create-CopilotEnrichmentSchema.sql;
       a database without it has only the flag. */
    DROP TABLE IF EXISTS #cur;
    CREATE TABLE #cur
    (
        Upn              NVARCHAR(512) COLLATE DATABASE_DEFAULT NOT NULL PRIMARY KEY NONCLUSTERED,
        IsLic            BIT           NULL,
        Sku              NVARCHAR(100) COLLATE DATABASE_DEFAULT NULL,
        SinceUtc         DATETIME2(0)  NULL,
        DirectoryStatus  NVARCHAR(20)  COLLATE DATABASE_DEFAULT NULL,
        LookedUpUtc      DATETIME2(0)  NULL   -- when the enrichment last looked the user up (UpdatedUtc)
    );
    SET @sql = N'INSERT INTO #cur (Upn, IsLic, Sku, SinceUtc, DirectoryStatus, LookedUpUtc)
        SELECT UserPrincipalName, IsCopilotLic, '
        + CASE WHEN COL_LENGTH(N'audit.CopilotLicense', N'SkuPartNumber')    IS NULL THEN N'NULL' ELSE N'SkuPartNumber'    END + N', '
        + CASE WHEN COL_LENGTH(N'audit.CopilotLicense', N'LicensedSinceUtc') IS NULL THEN N'NULL' ELSE N'LicensedSinceUtc' END + N', '
        + CASE WHEN COL_LENGTH(N'audit.CopilotLicense', N'DirectoryStatus')  IS NULL THEN N'NULL' ELSE N'DirectoryStatus'  END + N', '
        + CASE WHEN COL_LENGTH(N'audit.CopilotLicense', N'UpdatedUtc')       IS NULL THEN N'NULL' ELSE N'UpdatedUtc'       END + N'
        FROM audit.CopilotLicense;';
    EXEC (@sql);

    /*
        The periods each user held a Copilot SKU, as [FromUtc, ToUtc).

        With history: per SKU, each change starts a period that lasts until
        the SKU's next change, held if the change was an addition; before the
        first change, held if that first change was a removal. Only the held
        periods are kept - a user in #known is licensed at a moment if a held
        period covers it, and not licensed otherwise.

        Without history, from the enrichment: licensed from LicensedSinceUtc
        (or throughout, if it is not known) when licensed today; never, when
        not. A user in neither is not in #known: Unknown.
    */
    DROP TABLE IF EXISTS #ev;
    SELECT DISTINCT Upn, Sku, ChangedUtc AS Ts, Added INTO #ev FROM #chg WHERE Sku LIKE N'%COPILOT%';

    DROP TABLE IF EXISTS #held;
    CREATE TABLE #held
    (
        Upn      NVARCHAR(512) COLLATE DATABASE_DEFAULT NOT NULL,
        FromUtc  DATETIME2(0)  NOT NULL,
        ToUtc    DATETIME2(0)  NOT NULL
    );
    INSERT INTO #held (Upn, FromUtc, ToUtc)
    SELECT q.Upn, q.FromUtc, q.ToUtc
    FROM (
        SELECT e.Upn, e.Ts AS FromUtc,
               LEAD(e.Ts, 1, CAST('99991231' AS DATETIME2(0))) OVER (PARTITION BY e.Upn, e.Sku ORDER BY e.Ts, e.Added) AS ToUtc,
               e.Added AS Held
        FROM #ev AS e
        UNION ALL
        SELECT f.Upn, CAST('00010101' AS DATETIME2(0)), f.Ts, 1 - CAST(f.Added AS INT)
        FROM (SELECT e.Upn, e.Ts, e.Added,
                     ROW_NUMBER() OVER (PARTITION BY e.Upn, e.Sku ORDER BY e.Ts, e.Added) AS rn
              FROM #ev AS e) AS f
        WHERE f.rn = 1
    ) AS q
    WHERE q.Held = 1 AND q.ToUtc > q.FromUtc;

    INSERT INTO #held (Upn, FromUtc, ToUtc)
    SELECT c.Upn, COALESCE(c.SinceUtc, CAST('00010101' AS DATETIME2(0))), CAST('99991231' AS DATETIME2(0))
    FROM #cur AS c
    WHERE c.IsLic = 1
      AND NOT EXISTS (SELECT 1 FROM #ev AS e WHERE e.Upn = c.Upn);

    /* 2.4.12: a user the enrichment finds licensed, whose history leaves nothing held now, was not licensed from
       the history's last change on: their only Copilot changes can be another SKU added and taken away, while the
       license they hold was assigned before the history begins. Where the lookup came after that change, it is the
       newer evidence: licensed from LicensedSinceUtc, or from that change when it is not known. */
    INSERT INTO #held (Upn, FromUtc, ToUtc)
    SELECT c.Upn, COALESCE(c.SinceUtc, e.LastTs), CAST('99991231' AS DATETIME2(0))
    FROM #cur AS c
    JOIN (SELECT Upn, MAX(Ts) AS LastTs FROM #ev GROUP BY Upn) AS e ON e.Upn = c.Upn
    WHERE c.IsLic = 1 AND c.LookedUpUtc > e.LastTs
      AND NOT EXISTS (SELECT 1 FROM #held AS h WHERE h.Upn = c.Upn AND h.ToUtc = CAST('99991231' AS DATETIME2(0)));

    /* Nonclustered: a UPN can be 1,024 bytes, over a clustered key's 900. */
    CREATE INDEX IX_held ON #held (Upn, FromUtc) INCLUDE (ToUtc);

    DROP TABLE IF EXISTS #known;
    CREATE TABLE #known (Upn NVARCHAR(512) COLLATE DATABASE_DEFAULT NOT NULL PRIMARY KEY NONCLUSTERED);
    INSERT INTO #known (Upn)
    SELECT Upn FROM #ev
    UNION
    SELECT Upn FROM #cur WHERE IsLic IS NOT NULL;

    /* ---- 2. Everything else the facts are built from ------------------------ */

    /*
        Which sessions changed since the last refresh. A full session build
        renumbers every SessionKey, so after one - or on a full refresh, or
        the first - bi.FactSession is rebuilt whole ('all'). After only
        incremental builds, just the days they rebuilt ('window', from the
        earliest RebuiltFrom); after none, nothing ('none').
    */
    DECLARE @sessRun INT = (SELECT MAX(RunId) FROM audit.CopilotSessionRun),
            @lastSessRun INT = (SELECT TOP (1) SessionRunId FROM bi.RefreshRun ORDER BY RunId DESC),
            @sessMode NVARCHAR(6), @sessFrom DATETIME2(0) = NULL;
    IF @SinceUtc IS NULL OR @lastSessRun IS NULL
       OR EXISTS (SELECT 1 FROM audit.CopilotSessionRun WHERE RunId > @lastSessRun AND RebuiltFrom IS NULL)
        SET @sessMode = N'all';
    ELSE IF @sessRun IS NULL OR @sessRun <= @lastSessRun
        SET @sessMode = N'none';
    ELSE
        SELECT @sessMode = N'window', @sessFrom = MIN(RebuiltFrom)
        FROM audit.CopilotSessionRun WHERE RunId > @lastSessRun;

    DROP TABLE IF EXISTS #sess;
    SELECT s.SessionKey, s.SessionStart_PromptSubmittedDateTime AS StartTs, s.SessionEnd_PromptSubmittedDateTime AS EndTs,
           s.UserUPN AS Upn, COALESCE(NULLIF(LTRIM(RTRIM(s.PrimaryApp)), N''), N'(unknown)') AS App,
           s.IsM365CopilotLicensed AS IsLic, s.PromptCount, s.InferredSessionSpanMinutes AS SpanMinutes
    INTO #sess
    FROM audit.CopilotSession AS s
    WHERE @sessMode = N'all' OR (@sessMode = N'window' AND s.SessionStart_PromptSubmittedDateTime >= @sessFrom)
    OPTION (RECOMPILE);

    /* Directory attributes from on-premises AD (audit.UserDirectory), when
       it has been loaded. Dynamic, so a database without it still compiles. */
    DROP TABLE IF EXISTS #profile;
    CREATE TABLE #profile
    (
        Upn             NVARCHAR(512) COLLATE DATABASE_DEFAULT NOT NULL PRIMARY KEY NONCLUSTERED,
        DisplayName     NVARCHAR(400) NULL, Division NVARCHAR(256) NULL, Department NVARCHAR(200) NULL,
        JobTitle        NVARCHAR(200) NULL, OfficeLocation NVARCHAR(200) NULL, City NVARCHAR(200) NULL,
        Country         NVARCHAR(200) NULL, CompanyName NVARCHAR(200) NULL, EmployeeType NVARCHAR(32) NULL,
        IsManager       BIT NULL, AccountEnabled BIT NULL, ManagerUpn NVARCHAR(512) NULL,
        ManagerName     NVARCHAR(256) NULL, OrgDepth TINYINT NULL,
        Mgr1 NVARCHAR(256) NULL, Mgr2 NVARCHAR(256) NULL, Mgr3 NVARCHAR(256) NULL, Mgr4 NVARCHAR(256) NULL, Mgr5 NVARCHAR(256) NULL, Mgr6 NVARCHAR(256) NULL, Mgr7 NVARCHAR(256) NULL, Mgr8 NVARCHAR(256) NULL, Mgr9 NVARCHAR(256) NULL, Mgr10 NVARCHAR(256) NULL, Mgr11 NVARCHAR(256) NULL, Mgr12 NVARCHAR(256) NULL, Mgr13 NVARCHAR(256) NULL, Mgr14 NVARCHAR(256) NULL, Mgr15 NVARCHAR(256) NULL
    );
    IF OBJECT_ID(N'audit.UserDirectory', N'U') IS NOT NULL
        EXEC (N'INSERT INTO #profile (Upn, DisplayName, Division, Department, JobTitle, OfficeLocation, City,
                                      Country, CompanyName, EmployeeType, IsManager, AccountEnabled, ManagerUpn,
                                      ManagerName, OrgDepth, Mgr1, Mgr2, Mgr3, Mgr4, Mgr5, Mgr6, Mgr7, Mgr8, Mgr9, Mgr10, Mgr11, Mgr12, Mgr13, Mgr14, Mgr15)
                SELECT UserPrincipalName, FullName, Division, Department, JobTitle, Office, City,
                       Country, Company, EmployeeType, IsManager, AccountEnabled, ManagerUPN,
                       ManagerName, OrgDepth, Mgr1, Mgr2, Mgr3, Mgr4, Mgr5, Mgr6, Mgr7, Mgr8, Mgr9, Mgr10, Mgr11, Mgr12, Mgr13, Mgr14, Mgr15
                FROM audit.UserDirectory;');

    /* Keys for users, apps and SKUs from those. The interactions add their
       own in step 4, a month at a time. */
    INSERT INTO bi.DimUser (UserPrincipalName)
    SELECT n.Upn
    FROM (SELECT Upn FROM #sess
          UNION SELECT Upn FROM #chg
          UNION SELECT Upn FROM #cur
          UNION SELECT Upn FROM #profile) AS n
    WHERE NOT EXISTS (SELECT 1 FROM bi.DimUser AS d WHERE d.UserPrincipalName = n.Upn);

    INSERT INTO bi.DimApp (App)
    SELECT n.App
    FROM (SELECT App FROM #sess
          UNION SELECT LEFT(App, 200) FROM audit.SessionAppCategory) AS n
    WHERE NOT EXISTS (SELECT 1 FROM bi.DimApp AS d WHERE d.App = n.App);

    INSERT INTO bi.DimSku (Sku, IsCopilot)
    SELECT DISTINCT c.Sku, CASE WHEN c.Sku LIKE N'%COPILOT%' THEN 1 ELSE 0 END FROM #chg AS c
    WHERE NOT EXISTS (SELECT 1 FROM bi.DimSku AS d WHERE d.Sku = c.Sku);

    /* ---- 3. Calendar ------------------------------------------------------- */
    /*
        From 1 January of the earliest year with data to 31 December of this
        year, in the reporting time zone. Dates are only ever added.
        1900-01-01 was a Monday and 1900-01-06 a Saturday, so the day and week
        arithmetic is right whatever DATEFIRST and the language say.
    */
    DECLARE @today DATE = CAST(SYSUTCDATETIME() AT TIME ZONE 'UTC' AT TIME ZONE @ReportingTimeZone AS DATE);
    DECLARE @firstUtc DATETIME2(0) = (SELECT MIN(v) FROM (VALUES
            ((SELECT MIN(UtcTimeStamp) FROM audit.RawLogFlattened)),
            ((SELECT MIN(ChangedUtc) FROM #chg))) AS x(v));
    DECLARE @firstDate DATE = COALESCE(CAST(CAST(@firstUtc AS DATETIME2(0)) AT TIME ZONE 'UTC' AT TIME ZONE @ReportingTimeZone AS DATE), @today);
    DECLARE @firstSession DATE = (SELECT CAST(MIN(SessionStart_PromptSubmittedDateTime) AS DATE) FROM audit.CopilotSession);
    IF @firstSession < @firstDate
        SET @firstDate = @firstSession;
    IF @firstDate > @today SET @firstDate = @today;
    DECLARE @d0 DATE = DATEFROMPARTS(YEAR(@firstDate), 1, 1), @d1 DATE = DATEFROMPARTS(YEAR(@today), 12, 31);

    WITH n AS (
        SELECT TOP (DATEDIFF(DAY, @d0, @d1) + 1)
               ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) - 1 AS i
        FROM sys.all_columns AS a CROSS JOIN sys.all_columns AS b
    )
    INSERT INTO bi.DimDate
        (DateKey, [Date], [Year], [Quarter], MonthNumber, [MonthName], YearMonth, DayOfWeekNumber, DayOfWeekName,
         IsWeekday, WeekEnding, WeekYear, WeekNumber, WeekLabel)
    SELECT YEAR(d.D) * 10000 + MONTH(d.D) * 100 + DAY(d.D), d.D, YEAR(d.D), DATEPART(QUARTER, d.D), MONTH(d.D),
           CHOOSE(MONTH(d.D), N'January', N'February', N'March', N'April', N'May', N'June', N'July',
                  N'August', N'September', N'October', N'November', N'December'),
           CONVERT(CHAR(7), d.D, 126),
           w.Dow,
           CHOOSE(w.Dow, N'Monday', N'Tuesday', N'Wednesday', N'Thursday', N'Friday', N'Saturday', N'Sunday'),
           CASE WHEN w.Dow <= 5 THEN 1 ELSE 0 END,
           w.WeekEnding, YEAR(w.WeekEnding), k.WeekNumber,
           CONCAT(YEAR(w.WeekEnding), N'-W', RIGHT(CONCAT(N'0', k.WeekNumber), 2))
    FROM n
    CROSS APPLY (SELECT DATEADD(DAY, n.i, @d0) AS D) AS d
    CROSS APPLY (SELECT DATEDIFF(DAY, '19000101', d.D) % 7 + 1 AS Dow,
                        DATEADD(DAY, 6 - DATEDIFF(DAY, '19000106', d.D) % 7, d.D) AS WeekEnding) AS w
    CROSS APPLY (SELECT DATEFROMPARTS(YEAR(w.WeekEnding), 1, 1) AS Jan1) AS j
    CROSS APPLY (SELECT DATEDIFF(DAY, DATEADD(DAY, 6 - DATEDIFF(DAY, '19000106', j.Jan1) % 7, j.Jan1), w.WeekEnding) / 7 + 1
                        AS WeekNumber) AS k
    WHERE NOT EXISTS (SELECT 1 FROM bi.DimDate AS x WHERE x.[Date] = d.D);

    /* ---- 4. Interactions --------------------------------------------------- */
    /*
        A month of audit.RawLogFlattened at a time: resolve its values once,
        add keys for anything new, write its fact rows. Month by month keeps
        tempdb and the log flat over tens of millions of rows.

        A partial refresh replaces its window in one transaction, so a model
        refreshing at the same time never sees it half-written. A full
        rebuild commits month by month instead; don't refresh the model while
        one runs.
    */
    DECLARE @interactions BIGINT = 0, @lo DATETIME2(0), @hi DATETIME2(0), @next DATETIME2(0);
    DECLARE @skipped BIGINT = (SELECT COUNT_BIG(*) FROM audit.RawLogFlattened WHERE UtcTimeStamp IS NULL);

    /* Two statements, not one with "@SinceUtc IS NULL OR ...": that form
       cannot seek the timestamp index, and scanned 40M rows every night. */
    IF @SinceUtc IS NULL
    BEGIN
        SELECT @lo = MIN(UtcTimeStamp), @hi = MAX(UtcTimeStamp) FROM audit.RawLogFlattened;
        IF @lo IS NOT NULL
            SET @lo = DATETIME2FROMPARTS(YEAR(@lo), MONTH(@lo), 1, 0, 0, 0, 0, 0);
    END
    ELSE
        SELECT @lo = @SinceUtc, @hi = MAX(UtcTimeStamp) FROM audit.RawLogFlattened WHERE UtcTimeStamp >= @SinceUtc;

    DECLARE @resources BIGINT = 0;
    DECLARE @hasResources BIT = CASE WHEN OBJECT_ID(N'audit.RawLogAccessedResource', N'U') IS NULL THEN 0 ELSE 1 END;

    IF @SinceUtc IS NOT NULL
    BEGIN
        BEGIN TRANSACTION;
        DELETE FROM bi.FactInteraction WHERE CreatedUtc >= @SinceUtc;
        DELETE FROM bi.FactAccessedResource WHERE CreatedUtc >= @SinceUtc;
    END
    ELSE
    BEGIN
        IF OBJECT_ID(N'bi.RebuildInProgress', N'U') IS NOT NULL AND NOT EXISTS (SELECT 1 FROM bi.RebuildInProgress)
            INSERT INTO bi.RebuildInProgress (StartedUtc) VALUES (SYSUTCDATETIME());
        TRUNCATE TABLE bi.FactInteraction;
        TRUNCATE TABLE bi.FactAccessedResource;
    END;

    WHILE @lo <= @hi
    BEGIN
        SET @next = DATEADD(MONTH, 1, @lo);

        DROP TABLE IF EXISTS #src;
        SELECT f.RecordId,
               f.UtcTimeStamp                                                                     AS Utc,
               COALESCE(NULLIF(LTRIM(RTRIM(LEFT(f.[User], 512))), N''), N'(unknown user)')          AS Upn,
               COALESCE(NULLIF(LTRIM(RTRIM(LEFT(f.App, 200))), N''), N'(unknown)')                 AS App,
               ag.AgentId, ag.AgentSource,
               COALESCE(NULLIF(LTRIM(RTRIM(f.ModelProvider)), N''), N'(not reported)')             AS ModelProvider,
               COALESCE(NULLIF(LTRIM(RTRIM(f.ModelName)), N''), N'(not reported)')                 AS ModelName,
               ISNULL(f.PromptCount, 0)                                                            AS PromptCount,
               ISNULL(f.AccessedResourceCount, 0)                                                  AS AccessedResourceCount,
               CAST(CASE WHEN f.AIPlugin LIKE N'%BingWebSearch%' THEN 1 ELSE 0 END AS BIT)           AS UsedWebSearch,
               CAST(CASE WHEN f.JailbreakDetected = N'true' THEN 1 ELSE 0 END AS BIT)                AS JailbreakDetected,
               CAST(CASE WHEN f.PromptInjectionDetected = N'true' THEN 1 ELSE 0 END AS BIT)          AS PromptInjectionDetected,
               CAST(CASE WHEN NULLIF(f.BlockedResourceUrls, N'') IS NOT NULL THEN 1 ELSE 0 END AS BIT) AS HasBlockedResource,
               CAST(CASE WHEN NULLIF(f.SensitivityLabelIds, N'') IS NOT NULL THEN 1 ELSE 0 END AS BIT) AS HasSensitivityLabel,
               COALESCE(NULLIF(LTRIM(RTRIM(f.CopilotEventData_LicenseType)), N''), N'(not reported)')  AS LicenseType,
               f.CopilotEventData_SensitivityLabelId                                               AS EventLabelId,
               ISNULL(f.MessageCount, 0)                                                           AS MessageCount,
               CAST(ISNULL(f.CopilotEventData_MemoryUpdated, 0) AS BIT)                             AS MemoryUpdated,
               CAST(CASE WHEN f.CopilotEventData_DLPEvaluationDeferred > 0 THEN 1 ELSE 0 END AS BIT)  AS DlpEvaluationDeferred,
               CAST(CASE WHEN NULLIF(f.CopilotEventData_TargetAgentName, N'') IS NOT NULL THEN 1 ELSE 0 END AS BIT) AS CalledAnotherAgent,
               NULLIF(LTRIM(RTRIM(LEFT(f.CopilotEventData_TargetAgentName, 400))), N'')              AS TargetAgentName,
               NULLIF(LTRIM(RTRIM(f.AppIdentity)), N'')                                            AS Ident
        INTO #src
        FROM audit.RawLogFlattened AS f
        /* The agent: the one AppIdentity names, else the record's AgentId
           (bi.fnAgentIdentity in Create-CopilotBiSchema.sql). Its name is set
           after the months, from every record's, not this window's. */
        CROSS APPLY bi.fnAgentIdentity(f.AppIdentity, f.AgentId) AS ag
        WHERE f.UtcTimeStamp >= @lo AND f.UtcTimeStamp < @next
        /* Planned per window: a plan cached from a month-sized full rebuild
           would scan the table for a two-day refresh. */
        OPTION (RECOMPILE);

        INSERT INTO bi.DimUser (UserPrincipalName)
        SELECT DISTINCT s.Upn FROM #src AS s
        WHERE NOT EXISTS (SELECT 1 FROM bi.DimUser AS d WHERE d.UserPrincipalName = s.Upn);

        INSERT INTO bi.DimApp (App)
        SELECT DISTINCT s.App FROM #src AS s
        WHERE NOT EXISTS (SELECT 1 FROM bi.DimApp AS d WHERE d.App = s.App);

        INSERT INTO bi.DimAgent (AgentId, AgentSource)
        SELECT s.AgentId, MIN(s.AgentSource) FROM #src AS s
        WHERE NOT EXISTS (SELECT 1 FROM bi.DimAgent AS d WHERE d.AgentId = s.AgentId)
        GROUP BY s.AgentId;

        INSERT INTO bi.DimModel (ModelProvider, ModelName)
        SELECT DISTINCT s.ModelProvider, s.ModelName FROM #src AS s
        WHERE NOT EXISTS (SELECT 1 FROM bi.DimModel AS d WHERE d.ModelProvider = s.ModelProvider AND d.ModelName = s.ModelName);

        INSERT INTO bi.DimLicenseType (LicenseType)
        SELECT DISTINCT s.LicenseType FROM #src AS s
        WHERE NOT EXISTS (SELECT 1 FROM bi.DimLicenseType AS d WHERE d.LicenseType = s.LicenseType);

        /* The accessed resources of this month's interactions, from the element table. In the database's collation
           (2.4.12), as every temp table here is: in tempdb's, a database whose collation differs from the server's
           failed every refresh at the first comparison with a dimension. */
        DROP TABLE IF EXISTS #rsrc;
        CREATE TABLE #rsrc (RecordId NVARCHAR(450) COLLATE DATABASE_DEFAULT NOT NULL, Ordinal SMALLINT NOT NULL,
                            ResourceType NVARCHAR(200) COLLATE DATABASE_DEFAULT NOT NULL, ResourceAction NVARCHAR(450) COLLATE DATABASE_DEFAULT NOT NULL,
                            Site NVARCHAR(400) COLLATE DATABASE_DEFAULT NOT NULL, LabelId UNIQUEIDENTIFIER NULL,
                            IsBlocked BIT NOT NULL, XPIADetected BIT NOT NULL, FileType NVARCHAR(64) COLLATE DATABASE_DEFAULT NOT NULL);
        IF @hasResources = 1
            INSERT INTO #rsrc
            SELECT r.RecordId, r.Ordinal,
                   LEFT(COALESCE(NULLIF(LTRIM(RTRIM(r.[Type])), N''), N'(not reported)'), 200),   -- each cut to the width it is stored in
                   LEFT(COALESCE(NULLIF(LTRIM(RTRIM(r.[Action])), N''), N'(not reported)'), 450),
                   LEFT(COALESCE(NULLIF(r.Site, N''), N'(none)'), 400),
                   r.SensitivityLabelId, r.IsBlocked, ISNULL(r.XPIADetected, 0),
                   /* 2.4.6: a file's extension, 1 to 8 letters and digits after the last dot of its name (not the first character); else its Type. */
                   CASE WHEN CHARINDEX(N'.', REVERSE(nm.N)) BETWEEN 2 AND 9 AND CHARINDEX(N'.', REVERSE(nm.N)) < LEN(nm.N)
                             AND RIGHT(nm.N, CHARINDEX(N'.', REVERSE(nm.N)) - 1) NOT LIKE N'%[^A-Za-z0-9]%'
                            THEN LOWER(RIGHT(nm.N, CHARINDEX(N'.', REVERSE(nm.N)) - 1))
                            ELSE LEFT(COALESCE(NULLIF(LTRIM(RTRIM(r.[Type])), N''), N'(not reported)'), 64) END
            FROM #src AS s
            /* 2.5.4: sought, an interaction at a time. Once the table is large its statistics are sampled, and a
               sample counts far too few distinct records in a key where one record's rows sit together (68,000
               for 11.6 million, in the lab). Left to choose on that, a night's 220,480 interactions were joined
               by reading all 70 million rows: four to five minutes, where seeking them takes seven seconds. */
            JOIN audit.RawLogAccessedResource AS r WITH (FORCESEEK) ON r.RecordId = s.RecordId
            CROSS APPLY (SELECT RTRIM(r.Name) AS N) AS nm;

        /* 2.4.8: the interactions that read work content - a resource in SharePoint or OneDrive, Outlook or Teams,
           or a mail, a Teams message, a meeting or a connector item. The web pages a web search cites are listed as
           resources too, so "read a resource" alone does not say work content. Without the element table (a load
           from a CSV export), the count and the web search flag are all there is, as before 2.4.8. */
        DROP TABLE IF EXISTS #work;
        CREATE TABLE #work (RecordId NVARCHAR(450) COLLATE DATABASE_DEFAULT NOT NULL PRIMARY KEY);
        IF @hasResources = 1
            INSERT INTO #work (RecordId)
            SELECT DISTINCT r.RecordId FROM #rsrc AS r
            WHERE r.Site LIKE N'https://%.sharepoint.%' OR r.Site LIKE N'https://%.sharepoint-mil.%'
               OR r.Site LIKE N'https://outlook.office%' OR r.Site LIKE N'https://teams.microsoft.%'
               OR r.ResourceType IN (N'EmailMessage', N'TeamsMessage', N'Event', N'External');
        ELSE
            INSERT INTO #work (RecordId)
            SELECT s.RecordId FROM #src AS s WHERE s.AccessedResourceCount > 0 AND s.UsedWebSearch = 0;

        INSERT INTO bi.DimFileType (FileType)
        SELECT DISTINCT r.FileType FROM #rsrc AS r
        WHERE NOT EXISTS (SELECT 1 FROM bi.DimFileType AS d WHERE d.FileType = r.FileType);

        INSERT INTO bi.DimSensitivityLabel (LabelId)
        SELECT l.LabelId FROM (SELECT EventLabelId AS LabelId FROM #src UNION SELECT LabelId FROM #rsrc) AS l
        WHERE l.LabelId IS NOT NULL
          AND NOT EXISTS (SELECT 1 FROM bi.DimSensitivityLabel AS d WHERE d.LabelId = l.LabelId);

        INSERT INTO bi.DimResourceType (ResourceType)
        SELECT DISTINCT r.ResourceType FROM #rsrc AS r
        WHERE NOT EXISTS (SELECT 1 FROM bi.DimResourceType AS d WHERE d.ResourceType = r.ResourceType);

        INSERT INTO bi.DimResourceAction (ResourceAction)
        SELECT DISTINCT r.ResourceAction FROM #rsrc AS r
        WHERE NOT EXISTS (SELECT 1 FROM bi.DimResourceAction AS d WHERE d.ResourceAction = r.ResourceAction);

        INSERT INTO bi.DimSite (Site, Host, IsPersonal)
        SELECT x.Site,
               /* https://host */
               LEFT(x.Site, CASE WHEN CHARINDEX(N'/', x.Site + N'/', 9) > 0 THEN CHARINDEX(N'/', x.Site + N'/', 9) - 1 ELSE LEN(x.Site) END),
               CASE WHEN x.Site LIKE N'%/personal/%' THEN 1 ELSE 0 END
        FROM (SELECT DISTINCT Site FROM #rsrc) AS x
        WHERE NOT EXISTS (SELECT 1 FROM bi.DimSite AS d WHERE d.Site = x.Site);

        /* An agent keeps its key when renamed: bi.usp_NameAgents, after the months, sets the names. */

        INSERT INTO bi.FactInteraction WITH (TABLOCK)
            (RecordId, CreatedUtc, ActivityHour, DateKey, HourOfDay, UserKey, AppKey, AgentKey, ModelKey, LicenseStatusKey,
             PromptCount, AccessedResourceCount, UsedWebSearch, JailbreakDetected, PromptInjectionDetected,
             HasBlockedResource, HasSensitivityLabel,
             LicenseTypeKey, SensitivityLabelKey, MessageCount, MemoryUpdated, DlpEvaluationDeferred, CalledAnotherAgent, TargetAgentName,
             PremiumUse, UseCaseKey, UseKind)
        SELECT s.RecordId, s.Utc,
               DATEADD(HOUR, DATEPART(HOUR, t.LocalTs), CAST(CAST(t.LocalTs AS DATE) AS DATETIME2(0))),
               YEAR(t.LocalTs) * 10000 + MONTH(t.LocalTs) * 100 + DAY(t.LocalTs),
               DATEPART(HOUR, t.LocalTs),
               u.UserKey, a.AppKey, g.AgentKey, m.ModelKey,
               ls.LicenseStatusKey,
               s.PromptCount, s.AccessedResourceCount, s.UsedWebSearch, s.JailbreakDetected,
               s.PromptInjectionDetected, s.HasBlockedResource, s.HasSensitivityLabel,
               lt.LicenseTypeKey, ISNULL(sl.SensitivityLabelKey, 1), s.MessageCount, s.MemoryUpdated,
               s.DlpEvaluationDeferred, s.CalledAnotherAgent, s.TargetAgentName,
               /* Premium use and the use-case key: defined here once (see bi.FactInteraction). */
               CASE WHEN ls.LicenseStatusKey = 1
                     AND (uk.UseKind = 1
                          OR (uk.UseKind IN (2, 3) AND a.AppCategory = N'In-App Copilot' AND a.App <> N'Copilot Studio')
                          OR (uk.UseKind = 3 AND a.AppCategory = N'Copilot Chat'))
                    THEN 1 ELSE 0 END,
               a.AppKey * 4 + CAST(s.UsedWebSearch AS INT) * 2 + CASE WHEN g.AgentId = N'(none)' THEN 0 ELSE 1 END,
               uk.UseKind
        FROM #src AS s
        CROSS APPLY (SELECT CAST(CAST(s.Utc AS DATETIME2(0)) AT TIME ZONE 'UTC' AT TIME ZONE @ReportingTimeZone AS DATETIME2(0)) AS LocalTs) AS t
        JOIN bi.DimUser  AS u ON u.UserPrincipalName = s.Upn
        JOIN bi.DimApp   AS a ON a.App = s.App
        JOIN bi.DimAgent AS g ON g.AgentId = s.AgentId
        JOIN bi.DimModel AS m ON m.ModelProvider = s.ModelProvider AND m.ModelName = s.ModelName
        JOIN bi.DimLicenseType AS lt ON lt.LicenseType = s.LicenseType
        LEFT JOIN bi.DimSensitivityLabel AS sl ON sl.LabelId = s.EventLabelId
        LEFT JOIN #known AS k ON k.Upn = s.Upn
        LEFT JOIN #work AS w ON w.RecordId = s.RecordId
        OUTER APPLY (SELECT TOP (1) 1 AS Held FROM #held AS x
                     WHERE x.Upn = s.Upn AND x.FromUtc <= s.Utc AND x.ToUtc > s.Utc) AS h
        /* 2.4.8: a record Copilot itself marked Premium was written under a license, whatever the dates say: the
           date Entra gives is when the current assignment began, which a reassignment moves forward. */
        CROSS APPLY (SELECT CAST(CASE WHEN s.LicenseType = N'Premium' THEN 1 WHEN k.Upn IS NULL THEN 0 WHEN h.Held = 1 THEN 1 ELSE 2 END AS TINYINT) AS LicenseStatusKey) AS ls
        CROSS APPLY (SELECT CAST(CASE WHEN s.Ident LIKE N'Copilot.Studio.%' THEN 0
                                      WHEN s.Ident LIKE N'Copilot.%.WebChat%' THEN CASE WHEN w.RecordId IS NOT NULL THEN 1 ELSE 0 END
                                      WHEN s.Ident LIKE N'Copilot.%.Bizchat%' OR s.Ident LIKE N'Copilot.%.Apps%' OR s.Ident LIKE N'MicrosoftAgent.%' THEN 1
                                      WHEN w.RecordId IS NOT NULL THEN 3 ELSE 2 END AS TINYINT) AS UseKind) AS uk
        ORDER BY s.Utc;
        SET @interactions += @@ROWCOUNT;

        INSERT INTO bi.FactAccessedResource WITH (TABLOCK)
            (RecordId, Ordinal, CreatedUtc, ActivityHour, DateKey, UserKey, AppKey,
             ResourceTypeKey, ResourceActionKey, SiteKey, SensitivityLabelKey, IsBlocked, XPIADetected, FileTypeKey)
        SELECT r.RecordId, r.Ordinal, s.Utc,
               DATEADD(HOUR, DATEPART(HOUR, t.LocalTs), CAST(CAST(t.LocalTs AS DATE) AS DATETIME2(0))),
               YEAR(t.LocalTs) * 10000 + MONTH(t.LocalTs) * 100 + DAY(t.LocalTs),
               u.UserKey, a.AppKey,
               rt.ResourceTypeKey, ra.ResourceActionKey, st.SiteKey, ISNULL(sl.SensitivityLabelKey, 1), r.IsBlocked, r.XPIADetected, ft.FileTypeKey
        FROM #rsrc AS r
        JOIN #src AS s ON s.RecordId = r.RecordId
        CROSS APPLY (SELECT CAST(CAST(s.Utc AS DATETIME2(0)) AT TIME ZONE 'UTC' AT TIME ZONE @ReportingTimeZone AS DATETIME2(0)) AS LocalTs) AS t
        JOIN bi.DimUser AS u ON u.UserPrincipalName = s.Upn
        JOIN bi.DimApp  AS a ON a.App = s.App
        JOIN bi.DimResourceType AS rt ON rt.ResourceType = r.ResourceType
        JOIN bi.DimResourceAction AS ra ON ra.ResourceAction = r.ResourceAction
        JOIN bi.DimSite AS st ON st.Site = r.Site
        JOIN bi.DimFileType AS ft ON ft.FileType = r.FileType
        LEFT JOIN bi.DimSensitivityLabel AS sl ON sl.LabelId = r.LabelId
        ORDER BY s.Utc;
        SET @resources += @@ROWCOUNT;

        SET @lo = @next;
    END;

    IF @SinceUtc IS NOT NULL
        COMMIT TRANSACTION;

    /* ---- 5. Dimension attributes ------------------------------------------ */

    /* Every agent's name, from the whole history's records rather than this window's (2.4.11). */
    EXEC bi.usp_NameAgents;

    UPDATE d
    SET DisplayName         = p.DisplayName,
        Division            = p.Division,
        Department          = p.Department,
        JobTitle            = p.JobTitle,
        OfficeLocation      = p.OfficeLocation,
        City                = p.City,
        Country             = p.Country,
        CompanyName         = p.CompanyName,
        EmployeeType        = p.EmployeeType,
        IsManager           = p.IsManager,
        AccountEnabled      = p.AccountEnabled,
        ManagerUpn          = p.ManagerUpn,
        ManagerName         = p.ManagerName,
        OrgDepth            = p.OrgDepth,
        OrgLevel1           = p.Mgr1,
        OrgLevel2           = p.Mgr2,
        OrgLevel3           = p.Mgr3,
        OrgLevel4           = p.Mgr4,
        OrgLevel5           = p.Mgr5,
        OrgLevel6           = p.Mgr6,
        OrgLevel7           = p.Mgr7,
        OrgLevel8           = p.Mgr8,
        OrgLevel9           = p.Mgr9,
        OrgLevel10          = p.Mgr10,
        OrgLevel11          = p.Mgr11,
        OrgLevel12          = p.Mgr12,
        OrgLevel13          = p.Mgr13,
        OrgLevel14          = p.Mgr14,
        OrgLevel15          = p.Mgr15,
        /* In AD: Found. Otherwise what the license lookup found in Entra. */
        DirectoryStatus     = COALESCE(CASE WHEN p.Upn IS NOT NULL THEN N'Found' END, c.DirectoryStatus, N'Not looked up'),
        CopilotLicenseToday = CASE c.IsLic WHEN 1 THEN N'Licensed' WHEN 0 THEN N'Not licensed' ELSE N'Unknown' END,
        CopilotSku          = c.Sku,
        LicensedSinceUtc    = c.SinceUtc
    FROM bi.DimUser AS d
    LEFT JOIN #profile AS p ON p.Upn = d.UserPrincipalName
    LEFT JOIN #cur     AS c ON c.Upn = d.UserPrincipalName;

    /* The same mapping the sessions use: unmapped apps are In-App Copilot. */
    DECLARE @recat TABLE (AppKey INT PRIMARY KEY);
    UPDATE d
    SET AppCategory = COALESCE(m.AppCategory, N'In-App Copilot')
    OUTPUT inserted.AppKey INTO @recat
    FROM bi.DimApp AS d
    LEFT JOIN audit.SessionAppCategory AS m ON m.App = d.App
    WHERE d.AppCategory <> COALESCE(m.AppCategory, N'In-App Copilot');

    /* Premium use depends on the category, so an app that changed category (or was new in this run
       and mapped to a category other than the default) has its stored flags set again, on every day,
       and the dashboard is told to roll up again from its first day. */
    DECLARE @recatFrom INT = NULL;
    IF EXISTS (SELECT 1 FROM @recat)
    BEGIN
        SET @recatFrom = (SELECT MIN(DateKey) FROM bi.FactInteraction WHERE AppKey IN (SELECT AppKey FROM @recat));
        UPDATE f
        SET PremiumUse = p.PremiumUse
        FROM bi.FactInteraction AS f
        JOIN bi.DimApp AS a ON a.AppKey = f.AppKey
        /* A row from before 2.4.8 has no UseKind: the category decides, as it did (the installer rebuilds bi once). */
        CROSS APPLY (SELECT ISNULL(f.UseKind, CASE WHEN f.AccessedResourceCount > 0 AND f.UsedWebSearch = 0 THEN 3 ELSE 2 END) AS UseKind) AS uk
        CROSS APPLY (SELECT CAST(CASE WHEN f.LicenseStatusKey = 1
                     AND (uk.UseKind = 1
                          OR (uk.UseKind IN (2, 3) AND a.AppCategory = N'In-App Copilot' AND a.App <> N'Copilot Studio')
                          OR (uk.UseKind = 3 AND a.AppCategory = N'Copilot Chat'))
                    THEN 1 ELSE 0 END AS BIT) AS PremiumUse) AS p
        WHERE f.AppKey IN (SELECT AppKey FROM @recat) AND f.PremiumUse <> p.PremiumUse;
    END;

    /* ---- 6. The smaller facts, rebuilt whole ------------------------------- */

    DECLARE @sessions INT, @changes INT, @days INT;

    BEGIN TRANSACTION;

    IF @sessMode = N'all'
        TRUNCATE TABLE bi.FactSession;
    ELSE IF @sessMode = N'window'
        DELETE FROM bi.FactSession WHERE SessionStart >= @sessFrom;
    INSERT INTO bi.FactSession
        (SessionKey, SessionStart, SessionEnd, DateKey, UserKey, AppKey, LicenseStatusKey, PromptCount, SpanMinutes, IsMultiTurn)
    SELECT s.SessionKey, s.StartTs, s.EndTs,
           YEAR(s.StartTs) * 10000 + MONTH(s.StartTs) * 100 + DAY(s.StartTs),
           u.UserKey, a.AppKey,
           CASE s.IsLic WHEN 1 THEN 1 WHEN 0 THEN 2 ELSE 0 END,
           s.PromptCount, s.SpanMinutes,
           CASE WHEN s.PromptCount > 1 THEN 1 ELSE 0 END
    FROM #sess AS s
    JOIN bi.DimUser AS u ON u.UserPrincipalName = s.Upn
    JOIN bi.DimApp  AS a ON a.App = s.App;
    SET @sessions = @@ROWCOUNT;

    TRUNCATE TABLE bi.FactLicenseChange;
    /* One row per record, SKU and direction, even if two license databases
       resolved the same record slightly differently. */
    INSERT INTO bi.FactLicenseChange (RecordId, ChangedUtc, DateKey, UserKey, SkuKey, Change, ChangedBy)
    SELECT r.RecordId, r.ChangedUtc,
           YEAR(t.LocalTs) * 10000 + MONTH(t.LocalTs) * 100 + DAY(t.LocalTs),
           r.UserKey, r.SkuKey, r.Change, r.ActorUpn
    FROM (SELECT c.RecordId, c.ChangedUtc, u.UserKey, k.SkuKey, c.ActorUpn,
                 CAST(CASE WHEN c.Added = 1 THEN 1 ELSE -1 END AS SMALLINT) AS Change,
                 ROW_NUMBER() OVER (PARTITION BY c.RecordId, k.SkuKey, c.Added ORDER BY c.ChangedUtc, u.UserKey) AS rn
          FROM #chg AS c
          JOIN bi.DimUser AS u ON u.UserPrincipalName = c.Upn
          JOIN bi.DimSku  AS k ON k.Sku = c.Sku) AS r
    CROSS APPLY (SELECT CAST(r.ChangedUtc AT TIME ZONE 'UTC' AT TIME ZONE @ReportingTimeZone AS DATETIME2(0)) AS LocalTs) AS t
    WHERE r.rn = 1;
    SET @changes = @@ROWCOUNT;

    /* Copilot Studio authoring events, from every license database that has them (2.3.0). */
    IF OBJECT_ID(N'bi.FactAgentEvent', N'U') IS NOT NULL
    BEGIN
        TRUNCATE TABLE bi.FactAgentEvent;
        DECLARE @agentDb SYSNAME, @agentSql NVARCHAR(MAX);
        DECLARE agentDbs CURSOR LOCAL FAST_FORWARD FOR SELECT Name FROM @licDbs;
        OPEN agentDbs;
        FETCH NEXT FROM agentDbs INTO @agentDb;
        WHILE @@FETCH_STATUS = 0
        BEGIN
            IF OBJECT_ID(QUOTENAME(@agentDb) + N'.audit.AgentEvent', N'U') IS NOT NULL
            BEGIN
                SET @agentSql = N'INSERT INTO bi.FactAgentEvent (RecordId, EventUtc, DateKey, Action, Operation, ActorUpn, ActorKey, BotId, BotSchemaName, ComponentName, ComponentType)
                    SELECT e.RecordId, e.CreatedUtc, YEAR(t.LocalTs) * 10000 + MONTH(t.LocalTs) * 100 + DAY(t.LocalTs), e.Action, e.Operation, e.ActorUpn,
                           u.UserKey, e.BotId, e.BotSchemaName, e.ComponentName, e.ComponentType
                    FROM ' + QUOTENAME(@agentDb) + N'.audit.AgentEvent AS e
                    CROSS APPLY (SELECT CAST(e.CreatedUtc AT TIME ZONE ''UTC'' AT TIME ZONE @z AS DATETIME2(0)) AS LocalTs) AS t
                    LEFT JOIN bi.DimUser AS u ON u.UserPrincipalName = e.ActorUpn
                    WHERE NOT EXISTS (SELECT 1 FROM bi.FactAgentEvent AS x WHERE x.RecordId = e.RecordId);';
                EXEC sys.sp_executesql @agentSql, N'@z SYSNAME', @z = @ReportingTimeZone;
            END;
            FETCH NEXT FROM agentDbs INTO @agentDb;
        END;
        CLOSE agentDbs;
        DEALLOCATE agentDbs;
    END;

    /* Licensed at the end of each day, from the first day with data to
       today: held at the instant before local midnight. */
    /*
        Held at the end of day d means held at the instant before local
        midnight: FromUtc before it and ToUtc at or after it - that is,
        d from FromUtc's local date to the day before ToUtc's. So each held
        period is turned into its range of DateKeys and seeks bi.DimDate,
        rather than every date being tested against every period.
        The ends are clamped first: '0001-01-01' and '9999-12-31' would
        overflow on conversion to a zone either side of UTC.
    */
    DECLARE @lowUtc  DATETIME2(0) = CAST(CAST(DATEADD(DAY, -1, @firstDate) AS DATETIME2(0)) AT TIME ZONE @ReportingTimeZone AT TIME ZONE 'UTC' AS DATETIME2(0)),
            @highUtc DATETIME2(0) = CAST(CAST(DATEADD(DAY,  2, @today)     AS DATETIME2(0)) AT TIME ZONE @ReportingTimeZone AT TIME ZONE 'UTC' AS DATETIME2(0)),
            @firstKey INT = YEAR(@firstDate) * 10000 + MONTH(@firstDate) * 100 + DAY(@firstDate),
            @todayKey INT = YEAR(@today) * 10000 + MONTH(@today) * 100 + DAY(@today);

    /* 2.4.12: an account the enrichment no longer finds, which the history still has holding a license, is licensed
       to the last day it was used under that sign-in (or of its last change, if later), where the lookup came after
       that. The account was renamed or deleted, and the history, which knows a person only by the sign-in of the
       time, has no removal for it: before, it stayed licensed every day to today, a license to reclaim on an account
       that no longer exists, and after a rename the same person was licensed twice. Its interactions keep the
       license they were used under; only its licensed days end. */
    DROP TABLE IF EXISTS #gone;
    SELECT DISTINCT h.Upn INTO #gone
    FROM #held AS h JOIN #cur AS c ON c.Upn = h.Upn
    WHERE h.ToUtc = CAST('99991231' AS DATETIME2(0)) AND c.DirectoryStatus = N'NotFound';
    IF EXISTS (SELECT 1 FROM #gone)
        UPDATE h
        SET ToUtc = e.EndUtc
        FROM #held AS h
        JOIN #cur AS c ON c.Upn = h.Upn
        JOIN (SELECT Upn, MAX(ChangedUtc) AS LastChange FROM #chg GROUP BY Upn) AS g ON g.Upn = h.Upn
        LEFT JOIN (SELECT u.UserPrincipalName AS Upn, MAX(f.CreatedUtc) AS LastUse
                   FROM bi.FactInteraction AS f
                   JOIN bi.DimUser AS u ON u.UserKey = f.UserKey
                   JOIN #gone AS x ON x.Upn = u.UserPrincipalName
                   GROUP BY u.UserPrincipalName) AS l ON l.Upn = h.Upn
        CROSS APPLY (SELECT CASE WHEN l.LastUse > g.LastChange THEN l.LastUse ELSE g.LastChange END AS LastSeen) AS s
        /* Local midnight after that day, in UTC: held at the end of it, and not of the next. */
        CROSS APPLY (SELECT CAST(CAST(DATEADD(DAY, 1, CAST(CAST(s.LastSeen AT TIME ZONE 'UTC' AT TIME ZONE @ReportingTimeZone AS DATETIME2(0)) AS DATE)) AS DATETIME2(0))
                                 AT TIME ZONE @ReportingTimeZone AT TIME ZONE 'UTC' AS DATETIME2(0)) AS EndUtc) AS e
        WHERE h.ToUtc = CAST('99991231' AS DATETIME2(0)) AND c.DirectoryStatus = N'NotFound' AND c.LookedUpUtc > s.LastSeen;

    TRUNCATE TABLE bi.FactLicensedDay;
    INSERT INTO bi.FactLicensedDay (DateKey, UserKey)
    SELECT DISTINCT d.DateKey, u.UserKey
    FROM #held AS h
    CROSS APPLY (SELECT CASE WHEN h.FromUtc < @lowUtc  THEN @lowUtc  ELSE h.FromUtc END AS F,
                        CASE WHEN h.ToUtc   > @highUtc THEN @highUtc ELSE h.ToUtc   END AS T) AS c
    CROSS APPLY (SELECT CAST(CAST(c.F AT TIME ZONE 'UTC' AT TIME ZONE @ReportingTimeZone AS DATETIME2(0)) AS DATE) AS FirstDay,
                        DATEADD(DAY, -1, CAST(CAST(c.T AT TIME ZONE 'UTC' AT TIME ZONE @ReportingTimeZone AS DATETIME2(0)) AS DATE)) AS LastDay) AS r
    CROSS APPLY (SELECT YEAR(r.FirstDay) * 10000 + MONTH(r.FirstDay) * 100 + DAY(r.FirstDay) AS FirstKey,
                        YEAR(r.LastDay)  * 10000 + MONTH(r.LastDay)  * 100 + DAY(r.LastDay)  AS LastKey) AS k
    JOIN bi.DimDate AS d ON d.DateKey BETWEEN k.FirstKey AND k.LastKey
                        AND d.DateKey BETWEEN @firstKey AND @todayKey
    JOIN bi.DimUser AS u ON u.UserPrincipalName = h.Upn
    WHERE c.T > c.F;
    SET @days = @@ROWCOUNT;

    INSERT INTO bi.RefreshRun
        (StartedUtc, EndedUtc, SinceUtc, ReportingTimeZone, LicenseEventDatabase, InteractionRows, InteractionsSkipped,
         SessionRows, LicenseChangeRows, LicensedDayRows, SessionRunId, AccessedResourceRows, RecategorizedFromDateKey)
    VALUES (@started, SYSUTCDATETIME(), @SinceUtc, @ReportingTimeZone, @LicenseEventDatabase, @interactions, @skipped,
            @sessions, @changes, @days, @sessRun, @resources, @recatFrom);
    IF OBJECT_ID(N'bi.RebuildInProgress', N'U') IS NOT NULL AND @SinceUtc IS NULL
        DELETE FROM bi.RebuildInProgress;

    COMMIT TRANSACTION;
    EXEC sys.sp_releaseapplock @Resource = N'bi.usp_RefreshBi', @LockOwner = 'Session';

    SET @msg = CONCAT(N'bi refreshed', CASE WHEN @SinceUtc IS NULL THEN N' (full rebuild)'
                                           ELSE CONCAT(N' from ', CONVERT(NVARCHAR(16), @SinceUtc, 120), N'Z') END,
                      N': ', FORMAT(@interactions, 'N0'), N' interaction(s), ', FORMAT(@resources, 'N0'), N' accessed resource(s), ',
                      FORMAT(@sessions, 'N0'), N' session(s), ',
                      FORMAT(@changes, 'N0'), N' license change(s), ', FORMAT(@days, 'N0'), N' licensed user-day(s).');
    RAISERROR(@msg, 0, 1) WITH NOWAIT;
    IF @skipped > 0
    BEGIN
        SET @msg = CONCAT(N'  ', FORMAT(@skipped, 'N0'), N' flattened row(s) have no timestamp and are not in bi.FactInteraction.');
        RAISERROR(@msg, 0, 1) WITH NOWAIT;
    END;
    IF @LicenseEventDatabase IS NULL
        RAISERROR('  No @LicenseEventDatabase: license at the time of use comes from the enrichment''s LicensedSinceUtc only.', 0, 1) WITH NOWAIT;
    END TRY
    BEGIN CATCH
        /* The lock is the session's: a pooled connection would otherwise keep it, and the next refresh wait. */
        IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
        IF APPLOCK_MODE(N'public', N'bi.usp_RefreshBi', N'Session') <> N'NoLock'
            EXEC sys.sp_releaseapplock @Resource = N'bi.usp_RefreshBi', @LockOwner = 'Session';
        THROW;
    END CATCH;
END;
GO
```

### C.12 `sql/Create-CopilotDashboardSchema.sql`

Copilot database, with `-Dashboard`. The `dash` schema: settings, roles, the org chart, the daily rollups and the agent inventory. 359 lines.

```sql
/*
    Copilot Insights dashboard - schema
    ------------------------------------------------------------------------
    What the web dashboard (dashboard/CopilotInsights) needs on top of the bi
    star schema: who may see what, the settings and reference data its admin
    module edits, an audit log, and two things built by
    dash.usp_RefreshDashboard (Create-CopilotDashboardProcedure.sql):

      dash.Person       one row per person in audit.UserDirectory, with their
                        level below the configured top of the organization
      dash.OrgClosure   every (manager, person under them) pair at any depth,
                        by UPN - the security model's subtree lookup
      dash.UserDay      per person per day they used Copilot: interactions,
                        prompts, sessions, flags - so the pages read a small
                        rollup instead of scanning the facts, for any period
                        of 7 to 365 days

    The security model (enforced by the app, on every query):
      - A person granted Organization (Admin alone is not enough), the top of the organization
        (Setting OrgTopUpn) and anyone reporting directly to them see the
        whole organization.
      - Everyone else sees themselves and everyone under them, at any depth;
        someone with no reports sees only themselves.
      - Named or sensitive detail also needs the page's persona role
        (Adoption, Licensing, Security, Compliance, Agents, Enablement).
    OrgTopUpn is set in the admin module. Until it is, nobody is treated as
    Level 1 or 2 automatically: an account with no manager in AD is as often
    a service account as the CEO.

    Idempotent. Single batch. Requires SQL Server 2017+, and the bi schema
    (Create-CopilotBiSchema.sql) for dash.fnScopeUsers.
*/
SET NOCOUNT ON;

IF SCHEMA_ID(N'dash') IS NULL
    EXEC (N'CREATE SCHEMA dash AUTHORIZATION dbo;');

/* Settings the admin module edits. */
IF OBJECT_ID(N'dash.Setting', N'U') IS NULL
    CREATE TABLE dash.Setting
    (
        Name        VARCHAR(64)     NOT NULL CONSTRAINT PK_DashSetting PRIMARY KEY,
        Value       NVARCHAR(400)   NULL,
        Description NVARCHAR(400)   NULL,
        UpdatedBy   NVARCHAR(256)   NULL,
        UpdatedUtc  DATETIME2(0)    NOT NULL CONSTRAINT DF_DashSetting_Updated DEFAULT (SYSUTCDATETIME())
    );
MERGE dash.Setting AS t
USING (VALUES
    ('OrgTopUpn',    CAST(NULL AS NVARCHAR(400)), N'UPN of the top of the organization (the CEO). They and their direct reports see the whole organization. Empty: taken from the org chart (OrgTopUpnDetected).'),
    ('MinGroupSize', N'5',  N'Breakdowns hide groups with fewer people than this, folding them into "Smaller groups", so a group total cannot reveal one person.'),
    ('DefaultDays',  N'30', N'Days shown when a page opens: 7, 14, 30, 90, 180 or 365.'),
    ('InactiveDays', N'30', N'A licensed person with no Copilot use for this many days counts as not using their license.'),
    ('LicenseMonthlyCost', N'30', N'What one Copilot license costs a month, for the license recommendations.'),
    ('LicenseCurrency', N'USD', N'The currency LicenseMonthlyCost is in.'),
    ('MaxViewerLevel', N'0', N'Who can open the dashboard (2.4.4): 0 everyone in the directory; N levels 1 to N below the top, and anyone granted a role other than Admin. Set in Admin > Access.'),
    ('ProtectedLevels', N'5', N'Levels 1 to N of the organization are never on a revoke, reclaim, pay-as-you-go or training list (2.4.6); 0 for none. Set in Admin > Access.')
) AS s (Name, Value, Description)
ON t.Name = s.Name
WHEN NOT MATCHED THEN INSERT (Name, Value, Description) VALUES (s.Name, s.Value, s.Description);

/* Roles granted to people (by UPN) or AD groups (DOMAIN\Group). */
IF OBJECT_ID(N'dash.RoleGrant', N'U') IS NULL
    CREATE TABLE dash.RoleGrant
    (
        GrantId       INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_DashRoleGrant PRIMARY KEY,
        Principal     NVARCHAR(256)   NOT NULL,   -- a UPN, or DOMAIN\Group
        PrincipalType VARCHAR(8)      NOT NULL CONSTRAINT CK_DashRoleGrant_Type CHECK (PrincipalType IN ('User', 'Group')),
        Role          VARCHAR(32)     NOT NULL CONSTRAINT CK_DashRoleGrant_Role CHECK (Role IN
                          ('Admin', 'Organization', 'Adoption', 'Licensing', 'Security', 'Compliance', 'Agents', 'Enablement')),
        Note          NVARCHAR(400)   NULL,
        GrantedBy     NVARCHAR(256)   NOT NULL,
        GrantedUtc    DATETIME2(0)    NOT NULL CONSTRAINT DF_DashRoleGrant_Granted DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT UQ_DashRoleGrant UNIQUE (Principal, Role)
    );

/* Friendly names and owners for agents, set by an administrator: they come before the name the audit log gives. */
IF OBJECT_ID(N'dash.AgentAlias', N'U') IS NULL
    CREATE TABLE dash.AgentAlias
    (
        AgentKey     INT             NOT NULL CONSTRAINT PK_DashAgentAlias PRIMARY KEY,   -- bi.DimAgent, whose keys are stable
        FriendlyName NVARCHAR(200)   NOT NULL,
        Owner        NVARCHAR(256)   NULL,
        Notes        NVARCHAR(400)   NULL,
        UpdatedBy    NVARCHAR(256)   NOT NULL,
        UpdatedUtc   DATETIME2(0)    NOT NULL CONSTRAINT DF_DashAgentAlias_Updated DEFAULT (SYSUTCDATETIME())
    );

/* Names for sensitivity labels, which the audit log records by ID only. */
IF OBJECT_ID(N'dash.SensitivityLabel', N'U') IS NULL
    CREATE TABLE dash.SensitivityLabel
    (
        LabelId     NVARCHAR(64)    NOT NULL CONSTRAINT PK_DashSensitivityLabel PRIMARY KEY,   -- the label GUID
        Name        NVARCHAR(200)   NOT NULL,
        Rank        TINYINT         NULL,       -- ordering, least to most sensitive
        UpdatedBy   NVARCHAR(256)   NOT NULL,
        UpdatedUtc  DATETIME2(0)    NOT NULL CONSTRAINT DF_DashSensitivityLabel_Updated DEFAULT (SYSUTCDATETIME())
    );

/* Every page view, denial, export and admin change. */
IF OBJECT_ID(N'dash.AuditLog', N'U') IS NULL
    CREATE TABLE dash.AuditLog
    (
        LogId     BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_DashAuditLog PRIMARY KEY,
        AtUtc     DATETIME2(0)    NOT NULL CONSTRAINT DF_DashAuditLog_At DEFAULT (SYSUTCDATETIME()),
        Viewer    NVARCHAR(256)   NOT NULL,   -- the resolved UPN, or the Windows name if none
        Kind      VARCHAR(12)     NOT NULL,   -- View | Denied | Export | Admin
        Page      NVARCHAR(200)   NULL,
        Scope     NVARCHAR(400)   NULL,       -- Organization, or the subtree root shown
        Detail    NVARCHAR(2000)  NULL,       -- filters, or what an admin change did
        ClientIp  VARCHAR(64)     NULL,
        INDEX IX_DashAuditLog_At (AtUtc),
        INDEX IX_DashAuditLog_Viewer (Viewer, AtUtc)
    );

/* Built by dash.usp_RefreshDashboard. */
IF OBJECT_ID(N'dash.Person', N'U') IS NULL
    CREATE TABLE dash.Person
    (
        Upn            NVARCHAR(256)   NOT NULL CONSTRAINT PK_DashPerson PRIMARY KEY,
        DisplayName    NVARCHAR(256)   NULL,
        ManagerUpn     NVARCHAR(256)   NULL,
        Department     NVARCHAR(200)   NULL,
        AccountEnabled BIT             NULL,
        Level          TINYINT         NULL,   -- 1 = OrgTopUpn, 2 = reports to them, ...; NULL when not under the top (or no top set)
        DirectReports  INT             NOT NULL,
        PeopleBelow    INT             NOT NULL,   -- everyone under them, at any depth
        INDEX IX_DashPerson_Manager (ManagerUpn)
    );

/* The person's Windows account SID, from audit.UserDirectory: how a sign-in is matched to a person. */
IF COL_LENGTH(N'dash.Person', N'Sid') IS NULL
    ALTER TABLE dash.Person ADD Sid VARCHAR(184) NULL;
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dash.Person') AND name = N'IX_DashPerson_Sid')
    EXEC (N'CREATE INDEX IX_DashPerson_Sid ON dash.Person (Sid);');

IF OBJECT_ID(N'dash.OrgClosure', N'U') IS NULL
    CREATE TABLE dash.OrgClosure
    (
        AncestorUpn   NVARCHAR(256)   NOT NULL,
        DescendantUpn NVARCHAR(256)   NOT NULL,
        Depth         TINYINT         NOT NULL,   -- 0 = the person themselves
        /* Nonclustered: two UPNs can exceed a clustered key's 900 bytes. It
           covers the lookup (ancestor to the people under them) on its own. */
        CONSTRAINT PK_DashOrgClosure PRIMARY KEY NONCLUSTERED (AncestorUpn, DescendantUpn)
    );

IF OBJECT_ID(N'dash.UserDay', N'U') IS NULL
    CREATE TABLE dash.UserDay
    (
        DateKey           INT     NOT NULL,   -- bi.DimDate, in the reporting time zone
        UserKey           INT     NOT NULL,   -- bi.DimUser
        Interactions      INT     NOT NULL,
        Prompts           INT     NOT NULL,
        AppsUsed          TINYINT NOT NULL,
        AgentInteractions INT     NOT NULL,   -- interactions with an agent, not Copilot itself
        WebGrounded       INT     NOT NULL,
        ChatInteractions  INT     NOT NULL,   -- in Copilot Chat apps (bi.DimApp.AppCategory)
        LicensedUse       INT     NOT NULL,   -- interactions made while licensed
        Jailbreak         INT     NOT NULL,
        PromptInjection   INT     NOT NULL,
        BlockedResource   INT     NOT NULL,
        LabeledContent   INT     NOT NULL,   -- interactions that touched sensitivity-labeled content
        Sessions          INT     NOT NULL,   -- sessions that started that day
        MultiTurnSessions INT     NOT NULL,
        PremiumUse        INT     NOT NULL CONSTRAINT DF_DashUserDay_Premium DEFAULT (0),   -- licensed interactions that needed the license (2.3.0)
        CONSTRAINT PK_DashUserDay PRIMARY KEY (DateKey, UserKey),
        INDEX IX_DashUserDay_User (UserKey, DateKey)
    );

/* 2.3.0: use that needs the Premium license. Zero until the rollups are rebuilt once (dash.usp_RefreshDashboard with no date). */
IF COL_LENGTH(N'dash.UserDay', N'PremiumUse') IS NULL
    ALTER TABLE dash.UserDay ADD PremiumUse INT NOT NULL CONSTRAINT DF_DashUserDay_Premium DEFAULT (0);

/*
    Sensitivity labels and SharePoint sites Copilot touched, per person per day:
    Label (a label id on content an interaction read), Site (the site of labeled
    content it read) and Blocked (the site of content it was blocked from). Sites
    are cut to https://host/sites/name, or /personal/name for OneDrive.
*/
IF OBJECT_ID(N'dash.ResourceDay', N'U') IS NULL
    CREATE TABLE dash.ResourceDay
    (
        DateKey      INT            NOT NULL,
        UserKey      INT            NOT NULL,
        Kind         VARCHAR(8)     NOT NULL CONSTRAINT CK_DashResourceDay_Kind CHECK (Kind IN ('Label', 'Site', 'Blocked')),
        Item         NVARCHAR(400)  NOT NULL,
        Interactions INT            NOT NULL,
        CONSTRAINT PK_DashResourceDay PRIMARY KEY (DateKey, Kind, UserKey, Item)
    );

/*
    2.4.7: the interactions with a security signal, a few in every thousand, each with the record id the triage
    is kept under. Security > Flagged interactions lists them from here: reading the record ids of the flagged
    rows out of bi.FactInteraction costs a pass over that column for the whole fact (4 seconds at 40 million).
*/
IF OBJECT_ID(N'dash.FlaggedInteraction', N'U') IS NULL
BEGIN
    CREATE TABLE dash.FlaggedInteraction
    (
        DateKey                 INT           NOT NULL,
        UserKey                 INT           NOT NULL,
        RecordId                NVARCHAR(450) NOT NULL,
        ActivityHour            DATETIME2(0)  NOT NULL,
        AppKey                  INT           NOT NULL,
        AgentKey                INT           NOT NULL,
        JailbreakDetected       BIT           NOT NULL,
        PromptInjectionDetected BIT           NOT NULL,
        HasBlockedResource      BIT           NOT NULL
    );
    CREATE CLUSTERED INDEX CX_DashFlaggedInteraction ON dash.FlaggedInteraction (DateKey, UserKey);
END;

/*
    2.4.2: columnstore copies of the two rollups. The pages read a year of them
    for a whole organization at a time (6 and 12 million rows at 70,000 people),
    which the row-store keys scan in seconds and a columnstore in a fraction of
    one. The refresh truncates and bulk-loads both, which a columnstore takes.
*/
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dash.UserDay') AND name = N'NCCI_DashUserDay')
    EXEC (N'CREATE NONCLUSTERED COLUMNSTORE INDEX NCCI_DashUserDay ON dash.UserDay
          (DateKey, UserKey, Interactions, Prompts, AppsUsed, AgentInteractions, WebGrounded, ChatInteractions, LicensedUse,
           Jailbreak, PromptInjection, BlockedResource, LabeledContent, Sessions, MultiTurnSessions, PremiumUse);');
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dash.ResourceDay') AND name = N'NCCI_DashResourceDay')
    EXEC (N'CREATE NONCLUSTERED COLUMNSTORE INDEX NCCI_DashResourceDay ON dash.ResourceDay (DateKey, UserKey, Kind, Item, Interactions);');

/* When each agent was first and last used: bi.DimAgent.FirstSeenUtc is when a refresh first saw it. */
IF OBJECT_ID(N'dash.AgentUse', N'U') IS NULL
    CREATE TABLE dash.AgentUse
    (
        AgentKey     INT     NOT NULL CONSTRAINT PK_DashAgentUse PRIMARY KEY,
        FirstDateKey INT     NOT NULL,
        LastDateKey  INT     NOT NULL,
        Interactions BIGINT  NOT NULL
    );

IF OBJECT_ID(N'dash.RefreshRun', N'U') IS NULL
    CREATE TABLE dash.RefreshRun
    (
        RunId        INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_DashRefreshRun PRIMARY KEY,
        StartedUtc   DATETIME2(0)  NOT NULL,
        EndedUtc     DATETIME2(0)  NOT NULL,
        SinceUtc     DATETIME2(0)  NULL,       -- NULL: every day rebuilt
        FromDateKey  INT           NULL,
        People       INT           NOT NULL,
        ClosureRows  INT           NOT NULL,
        UserDayRows  INT           NOT NULL
    );
/* 2.4.0: the latest bi.RefreshRun the rollups reflect, so the next refresh sees what bi changed since. */
IF COL_LENGTH(N'dash.RefreshRun', N'BiRunId') IS NULL
    ALTER TABLE dash.RefreshRun ADD BiRunId INT NULL;

/*
    The people a scope covers, as bi.DimUser keys: @RootUpn and everyone under
    them. The app passes the viewer's own UPN, or a manager inside their scope
    when they drill in; a whole-organization view with no drill-in does not
    call it at all.
*/
EXEC (N'
CREATE OR ALTER FUNCTION dash.fnScopeUsers (@RootUpn NVARCHAR(256))
RETURNS TABLE
AS
RETURN
SELECT u.UserKey
FROM dash.OrgClosure AS c
JOIN bi.DimUser AS u ON u.UserPrincipalName = c.DescendantUpn
WHERE c.AncestorUpn = @RootUpn;');

/*
    2.5.6: the agent inventory, from the Excel workbook tools/Import-AgentWorkbook.ps1 loads each night. One row per
    agent the workbook lists (the current agents), matched to the agent the audit log knows (AgentKey) where it can
    be. The workbook's sheets themselves are in dbo, as they come; these are what the reports read.
*/
IF OBJECT_ID(N'dash.AgentInventory', N'U') IS NULL
    CREATE TABLE dash.AgentInventory
    (
        InventoryAgentId  NVARCHAR(100)  NOT NULL CONSTRAINT PK_DashAgentInventory PRIMARY KEY,   -- the workbook's AgentId
        AgentKey          INT            NULL,           -- bi.DimAgent, when the audit log knows the agent
        AgentName         NVARCHAR(400)  NULL,
        OwnerUpn          NVARCHAR(256)  NULL,
        Environment       NVARCHAR(400)  NULL,
        SchemaName        NVARCHAR(400)  NULL,
        CreatedAt         DATETIME2(0)   NULL,
        LastPublishedAt   DATETIME2(0)   NULL,
        Channels          NVARCHAR(400)  NULL,
        ViewerUsers       INT            NULL,
        ViewerGroups      INT            NULL,
        EditorUsers       INT            NULL,
        EditorGroups      INT            NULL,
        IsShared          AS CAST(CASE WHEN ViewerUsers > 0 OR ViewerGroups > 0 THEN 1 ELSE 0 END AS BIT),
        InProduction      BIT            NOT NULL CONSTRAINT DF_DashAgentInventory_Prod DEFAULT (0),   -- listed on AgentsInProd
        LoadedUtc         DATETIME2(0)   NOT NULL CONSTRAINT DF_DashAgentInventory_Loaded DEFAULT (SYSUTCDATETIME()),
        INDEX IX_DashAgentInventory_AgentKey (AgentKey)
    );

/* Connected agents: which parent agent calls which child, from the workbook's ParentChildAgent sheet. */
IF OBJECT_ID(N'dash.AgentChildLink', N'U') IS NULL
    CREATE TABLE dash.AgentChildLink
    (
        LinkId                  INT IDENTITY(1, 1) NOT NULL CONSTRAINT PK_DashAgentChildLink PRIMARY KEY,
        ChildAgentName          NVARCHAR(400)  NOT NULL,
        ParentInventoryAgentId  NVARCHAR(100)  NULL,
        ParentAgentName         NVARCHAR(400)  NULL,
        ParentAgentKey          INT            NULL,     -- bi.DimAgent, through the parent's inventory row
        RelationshipType        NVARCHAR(200)  NULL,
        IsValid                 BIT            NULL
    );

/* 2.5.7: which child it is. A name alone is not: Copilot Studio calls a new child agent "Agent", in every parent. A
   child agent lives inside its parent (its own component id); a connected agent is an agent of its own, which several
   parents can call (its environment and schema name, and its row in the inventory where it has one). */
IF COL_LENGTH(N'dash.AgentChildLink', N'ChildKey') IS NULL ALTER TABLE dash.AgentChildLink ADD ChildKey NVARCHAR(450) NULL;
IF COL_LENGTH(N'dash.AgentChildLink', N'ChildKind') IS NULL ALTER TABLE dash.AgentChildLink ADD ChildKind NVARCHAR(20) NULL;   -- Child, Connected
IF COL_LENGTH(N'dash.AgentChildLink', N'ChildInventoryAgentId') IS NULL ALTER TABLE dash.AgentChildLink ADD ChildInventoryAgentId NVARCHAR(100) NULL;
IF COL_LENGTH(N'dash.AgentChildLink', N'ParentEnvironment') IS NULL ALTER TABLE dash.AgentChildLink ADD ParentEnvironment NVARCHAR(400) NULL;

/* 2.5.8: the settings the agent checks compare with the agents' use, and whether an agent runs on its own. An agent
   with triggers or flows (an autonomous agent) can work with nobody chatting to it, so no interaction is not "unused". */
IF COL_LENGTH(N'dash.AgentInventory', N'EnvironmentSku') IS NULL ALTER TABLE dash.AgentInventory ADD EnvironmentSku NVARCHAR(100) NULL;
IF COL_LENGTH(N'dash.AgentInventory', N'Authentication') IS NULL ALTER TABLE dash.AgentInventory ADD Authentication NVARCHAR(200) NULL;
IF COL_LENGTH(N'dash.AgentInventory', N'IsQuarantined') IS NULL ALTER TABLE dash.AgentInventory ADD IsQuarantined BIT NULL;
IF COL_LENGTH(N'dash.AgentInventory', N'IsWebSearchEnabled') IS NULL ALTER TABLE dash.AgentInventory ADD IsWebSearchEnabled BIT NULL;
IF COL_LENGTH(N'dash.AgentInventory', N'IsAutonomous') IS NULL
    ALTER TABLE dash.AgentInventory ADD IsAutonomous BIT NOT NULL CONSTRAINT DF_DashAgentInventory_Autonomous DEFAULT (0);

/* Each workbook load, for Admin > Health: when, which file, and what reached the tables. */
IF OBJECT_ID(N'dash.AgentInventoryRun', N'U') IS NULL
    CREATE TABLE dash.AgentInventoryRun
    (
        RunId           INT IDENTITY(1, 1) NOT NULL CONSTRAINT PK_DashAgentInventoryRun PRIMARY KEY,
        StartedUtc      DATETIME2(0)    NOT NULL CONSTRAINT DF_DashAgentInventoryRun_Started DEFAULT (SYSUTCDATETIME()),
        EndedUtc        DATETIME2(0)    NULL,
        Source          NVARCHAR(800)   NULL,            -- the file, and where it came from
        FileCreatedUtc  DATETIME2(0)    NULL,
        InventoryRows   INT             NULL,
        ProductionRows  INT             NULL,
        ChildLinkRows   INT             NULL,
        Matched         INT             NULL,            -- inventory agents the audit log knows
        Status          NVARCHAR(20)    NOT NULL CONSTRAINT DF_DashAgentInventoryRun_Status DEFAULT (N'Running'),
        Message         NVARCHAR(2000)  NULL
    );

/* Agents with their friendly names (2.5.6): the agent workbook's first, as the list of the current agents; then one set
   by hand in Admin, for an agent the workbook does not name (one since deleted); then the audit log's. */
EXEC (N'
CREATE OR ALTER VIEW dash.Agent
AS
SELECT a.AgentKey, a.AgentId, a.AgentSource,
       COALESCE(NULLIF(inv.AgentName, N''''), al.FriendlyName, a.AgentName, a.AgentId) AS AgentName,
       a.AgentName AS AuditName, COALESCE(inv.OwnerUpn, al.Owner) AS Owner,
       COALESCE(CAST(fd.[Date] AS DATETIME2(0)), a.FirstSeenUtc) AS FirstSeenUtc,   -- first used, in the reporting zone
       inv.InventoryAgentId, CAST(ISNULL(inv.IsShared, 0) AS BIT) AS IsShared, CAST(ISNULL(inv.InProduction, 0) AS BIT) AS InProduction
FROM bi.DimAgent AS a
LEFT JOIN dash.AgentAlias AS al ON al.AgentKey = a.AgentKey
LEFT JOIN dash.AgentUse AS u ON u.AgentKey = a.AgentKey
LEFT JOIN bi.DimDate AS fd ON fd.DateKey = u.FirstDateKey
OUTER APPLY (SELECT TOP (1) i.InventoryAgentId, i.AgentName, i.OwnerUpn, i.IsShared, i.InProduction
             FROM dash.AgentInventory AS i WHERE i.AgentKey = a.AgentKey
             ORDER BY i.InProduction DESC, i.LastPublishedAt DESC, i.InventoryAgentId) AS inv;');
```

### C.13 `sql/Create-CopilotDashboardProcedure.sql`

Copilot database, with `-Dashboard`. `dash.usp_RefreshDashboard`, which rebuilds the org chart and the rollups. 322 lines.

```sql
/*
    dash.usp_RefreshDashboard
    ------------------------------------------------------------------------
    Rebuilds what the Copilot Insights dashboard reads besides the bi schema:

      1. dash.Person and dash.OrgClosure, whole, from audit.UserDirectory
         (on-premises AD) - so the security model follows today's
         organization. Small: one row per person, and one per (manager,
         person under them) pair.
      2. dash.UserDay from the bi facts, and dash.ResourceDay (labels and
         sites) from bi.FactAccessedResource, for the days from
         @SinceUtc's day on (every day when NULL), replaced in one transaction.
         Every day, too, when a full bi refresh has run since the last
         rollup (2.4.12), and from the first day of an app whose category a
         bi refresh changed.
      3. dash.AgentUse, whole: when each agent was first and last used.

    Invoke-CopilotAuditBackfill.ps1 -Incremental and the CSV importer call it
    last, after bi.usp_RefreshBi, with the same @SinceUtc.

    While a full bi rebuild has not finished (bi.RebuildInProgress has a row:
    it is running, or was stopped part way), steps 2 and 3 are skipped and
    the rollups left as they are (2.4.12): the facts are half written. The
    first rollup after the rebuild has finished builds every day.

    The closure follows ManagerUPN up from each person. A manager missing from
    the directory (another employee type, say) still becomes an ancestor, so
    they can see their people when they sign in. A loop in the manager
    attributes stops at 40 levels and each pair keeps its shortest distance.

    The dashboard's app-pool account may run it (Admin > Health > Refresh)
    without ALTER: it truncates a table only when the caller may, and deletes
    its rows otherwise, which ownership chaining allows. That account needs
    EXECUTE on it, SELECT on bi (its first check looks for bi.FactInteraction)
    and SELECT on audit.UserDirectory, which it reads through dynamic SQL
    because the table is optional. Install-CopilotInsights.ps1 grants all three.

    Requires Create-CopilotDashboardSchema.sql and the bi schema.
*/
SET NOCOUNT ON;

IF OBJECT_ID(N'dash.usp_RefreshDashboard', N'P') IS NULL
    EXEC (N'CREATE PROCEDURE dash.usp_RefreshDashboard AS BEGIN SET NOCOUNT ON; END;');
GO

ALTER PROCEDURE dash.usp_RefreshDashboard
    @SinceUtc DATETIME2(0) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @started DATETIME2(0) = SYSUTCDATETIME(), @msg NVARCHAR(600);
    IF OBJECT_ID(N'bi.FactInteraction', N'U') IS NULL
        THROW 50201, N'The bi schema is missing: run sql/Create-CopilotBiSchema.sql and bi.usp_RefreshBi first.', 1;

    /* ---- 1. People, levels and the org closure ----------------------------- */

    DROP TABLE IF EXISTS #dir;
    CREATE TABLE #dir (Upn NVARCHAR(256) COLLATE DATABASE_DEFAULT NOT NULL PRIMARY KEY,
                       ManagerUpn NVARCHAR(256) COLLATE DATABASE_DEFAULT NULL, OrgDepth INT NULL);
    IF OBJECT_ID(N'audit.UserDirectory', N'U') IS NOT NULL
        EXEC (N'INSERT INTO #dir (Upn, ManagerUpn, OrgDepth)
                SELECT UserPrincipalName, NULLIF(NULLIF(ManagerUPN, N''''), UserPrincipalName), OrgDepth FROM audit.UserDirectory;');

    DROP TABLE IF EXISTS #clo;
    WITH c AS
    (
        SELECT d.Upn AS AncestorUpn, d.Upn AS DescendantUpn, 0 AS Depth
        FROM #dir AS d
        UNION ALL
        /* One step up: the current ancestor's own manager. A manager missing
           from #dir becomes an ancestor here and the walk stops there. */
        SELECT a.ManagerUpn, c.DescendantUpn, c.Depth + 1
        FROM c
        JOIN #dir AS a ON a.Upn = c.AncestorUpn
        WHERE a.ManagerUpn IS NOT NULL AND c.Depth < 40
    )
    SELECT AncestorUpn, DescendantUpn, MIN(Depth) AS Depth
    INTO #clo
    FROM c
    GROUP BY AncestorUpn, DescendantUpn
    OPTION (MAXRECURSION 41);

    DECLARE @top NVARCHAR(256) = (SELECT NULLIF(LTRIM(RTRIM(Value)), N'') FROM dash.Setting WHERE Name = 'OrgTopUpn');

    /* No top set: take it from the org chart - the one person with no manager
       who has at least half the directory under them - and record it, so the
       dashboard works with nothing configured. OrgTopUpn, when set, wins. */
    DECLARE @detected NVARCHAR(256) =
    (
        SELECT TOP (1) c.AncestorUpn
        FROM #clo AS c
        JOIN #dir AS d ON d.Upn = c.AncestorUpn AND d.ManagerUpn IS NULL
        GROUP BY c.AncestorUpn
        HAVING COUNT(*) * 2 >= (SELECT COUNT(*) FROM #dir)
        ORDER BY COUNT(*) DESC
    );
    MERGE dash.Setting AS t
    USING (SELECT 'OrgTopUpnDetected' AS Name, @detected AS Value) AS s ON t.Name = s.Name
    WHEN MATCHED THEN UPDATE SET Value = s.Value, UpdatedBy = N'dash.usp_RefreshDashboard', UpdatedUtc = SYSUTCDATETIME()
    WHEN NOT MATCHED THEN INSERT (Name, Value, Description, UpdatedBy)
        VALUES (s.Name, s.Value, N'The top of the organization found in the org chart, used while OrgTopUpn is empty. Written by each refresh.', N'dash.usp_RefreshDashboard');
    SET @top = COALESCE(@top, @detected);

    DROP TABLE IF EXISTS #person;
    CREATE TABLE #person (Upn NVARCHAR(256) COLLATE DATABASE_DEFAULT NOT NULL PRIMARY KEY, DisplayName NVARCHAR(256) NULL,
                          ManagerUpn NVARCHAR(256) NULL, Department NVARCHAR(200) NULL, AccountEnabled BIT NULL,
                          Sid VARCHAR(184) NULL);
    /* The SID column arrives with the 2.2.0 enrichment schema; a directory table from before it has none. */
    DECLARE @sidCol NVARCHAR(4) = CASE WHEN COL_LENGTH(N'audit.UserDirectory', N'Sid') IS NULL THEN N'NULL' ELSE N'Sid' END;
    IF OBJECT_ID(N'audit.UserDirectory', N'U') IS NOT NULL
        EXEC (N'INSERT INTO #person (Upn, DisplayName, ManagerUpn, Department, AccountEnabled, Sid)
                SELECT UserPrincipalName, FullName, NULLIF(NULLIF(ManagerUPN, N''''), UserPrincipalName), Department, AccountEnabled, ' + @sidCol + N'
                FROM audit.UserDirectory;');
    /* Managers outside the directory table still get a row, so they can sign in and see their people. */
    INSERT INTO #person (Upn)
    SELECT DISTINCT c.AncestorUpn FROM #clo AS c WHERE NOT EXISTS (SELECT 1 FROM #person AS p WHERE p.Upn = c.AncestorUpn);

    /* A level counts every manager between a person and the top, those without a UPN too (2.4.12). The closure runs
       on ManagerUPN, which passes over a manager without a UPN to the one above, so a person under such a manager
       right below the top came out at level 2, which sees the whole organization. The AD load's OrgDepth counts them
       all; the deeper of the two is taken, so a level is never nearer the top than the chart puts it. */
    DECLARE @topDepth INT = (SELECT OrgDepth FROM #dir WHERE Upn = @top);

    BEGIN TRANSACTION;
    DELETE FROM dash.OrgClosure;
    INSERT INTO dash.OrgClosure (AncestorUpn, DescendantUpn, Depth)
    SELECT AncestorUpn, DescendantUpn, Depth FROM #clo;

    DELETE FROM dash.Person;
    INSERT INTO dash.Person (Upn, DisplayName, ManagerUpn, Department, AccountEnabled, Sid, Level, DirectReports, PeopleBelow)
    SELECT p.Upn, p.DisplayName, p.ManagerUpn, p.Department, p.AccountEnabled, p.Sid,
           CASE WHEN @top IS NULL OR t.DescendantUpn IS NULL THEN NULL
                WHEN d.OrgDepth - @topDepth > t.Depth THEN d.OrgDepth - @topDepth + 1
                ELSE t.Depth + 1 END,
           (SELECT COUNT(*) FROM #clo AS c WHERE c.AncestorUpn = p.Upn AND c.Depth = 1),
           (SELECT COUNT(*) FROM #clo AS c WHERE c.AncestorUpn = p.Upn AND c.Depth >= 1)
    FROM #person AS p
    LEFT JOIN #clo AS t ON t.AncestorUpn = @top AND t.DescendantUpn = p.Upn
    LEFT JOIN #dir AS d ON d.Upn = p.Upn;
    COMMIT TRANSACTION;

    DECLARE @people INT = (SELECT COUNT(*) FROM dash.Person), @closure INT = (SELECT COUNT(*) FROM dash.OrgClosure);

    /* ---- 2. The daily rollup ------------------------------------------------- */

    /* 2.4.12: not while a full bi rebuild has not finished, running or stopped part way: the facts are half written.
       A rollup over them took every month the rebuild had not reached yet out of the rollups (the dashboard
       installer's refresh during an upgrade's rebuild, say), and the windowed rollups after it never put them back.
       The run that finishes the rebuild is a full one, so the first rollup after it builds every day (below). */
    DECLARE @rebuilding BIT = 0;
    IF OBJECT_ID(N'bi.RebuildInProgress', N'U') IS NOT NULL
        SET @rebuilding = CASE WHEN EXISTS (SELECT 1 FROM bi.RebuildInProgress) THEN 1 ELSE 0 END;
    IF @rebuilding = 1
    BEGIN
        SET @msg = CONCAT(N'Dashboard: org chart refreshed (', FORMAT(@people, 'N0'), N' people). The rollups are left as they are: a full ',
                          N'refresh of the bi schema has not finished (it is running, or was stopped part way). The next bi refresh finishes it, ',
                          N'and the first dashboard refresh after that rolls up every day.');
        RAISERROR(@msg, 0, 1) WITH NOWAIT;
        RETURN;
    END;

    DECLARE @fromKey INT = NULL;
    IF @SinceUtc IS NOT NULL
    BEGIN
        /* The bi facts are dated in the reporting zone of the last bi refresh. */
        DECLARE @zone SYSNAME = (SELECT TOP (1) ReportingTimeZone FROM bi.RefreshRun ORDER BY RunId DESC);
        DECLARE @localDay DATE = CAST(CAST(@SinceUtc AS DATETIMEOFFSET) AT TIME ZONE ISNULL(@zone, N'UTC') AS DATE);
        SET @fromKey = YEAR(@localDay) * 10000 + MONTH(@localDay) * 100 + DAY(@localDay);
    END;

    /* The bi refreshes since the last rollup, read before the facts are: a full rebuild that starts while this reads
       them is one of the next rollup's, which then builds every day. */
    DECLARE @biRun INT = (SELECT MAX(RunId) FROM bi.RefreshRun);
    DECLARE @lastBiRun INT = ISNULL((SELECT TOP (1) r.BiRunId FROM dash.RefreshRun AS r WHERE r.BiRunId IS NOT NULL ORDER BY r.RunId DESC), 0);

    /* 2.4.12: a full bi refresh since the last rollup rewrote every interaction (a new rule for Premium or licensed
       use, license history that arrived late, another time zone, a stopped rebuild the nightly run finished), so
       every day is rolled up again. Before, only the window asked for was, and every older day kept the figures of
       the facts before it, for good: the dashboard and Power BI disagreed, and an upgrade's new Premium rule never
       reached the days before it (Forecasting's revoke list among them). */
    IF @fromKey IS NOT NULL AND EXISTS (SELECT 1 FROM bi.RefreshRun WHERE RunId > @lastBiRun AND SinceUtc IS NULL)
    BEGIN
        RAISERROR('A full refresh of the bi schema ran since the last rollup: rolling up every day.', 0, 1) WITH NOWAIT;
        SET @fromKey = NULL;
    END;

    /* A bi refresh since the last rollup that changed an app's category moves the start back to the
       first day that app was used: Premium use and ChatInteractions depend on the category. */
    IF @fromKey IS NOT NULL
    BEGIN
        DECLARE @recatFrom INT = (SELECT MIN(b.RecategorizedFromDateKey) FROM bi.RefreshRun AS b WHERE b.RunId > @lastBiRun);
        IF @recatFrom < @fromKey SET @fromKey = @recatFrom;
    END;

    DECLARE @noAgent INT = (SELECT AgentKey FROM bi.DimAgent WHERE AgentId = N'(none)');

    DROP TABLE IF EXISTS #day;
    WITH i AS
    (
        SELECT f.DateKey, f.UserKey,
               COUNT_BIG(*)                                                          AS Interactions,
               SUM(f.PromptCount)                                                    AS Prompts,
               COUNT(DISTINCT f.AppKey)                                              AS AppsUsed,
               SUM(CASE WHEN f.AgentKey <> ISNULL(@noAgent, -1) THEN 1 ELSE 0 END)   AS AgentInteractions,
               SUM(CAST(f.UsedWebSearch AS INT))                                     AS WebGrounded,
               SUM(CASE WHEN a.AppCategory = N'Copilot Chat' THEN 1 ELSE 0 END)      AS ChatInteractions,
               SUM(CASE WHEN f.LicenseStatusKey = 1 THEN 1 ELSE 0 END)               AS LicensedUse,
               SUM(CAST(f.PremiumUse AS INT))                                        AS PremiumUse,   -- set by bi.usp_RefreshBi, the rule's one definition
               SUM(CAST(f.JailbreakDetected AS INT))                                 AS Jailbreak,
               SUM(CAST(f.PromptInjectionDetected AS INT))                           AS PromptInjection,
               SUM(CAST(f.HasBlockedResource AS INT))                                AS BlockedResource,
               SUM(CAST(f.HasSensitivityLabel AS INT))                               AS LabeledContent
        FROM bi.FactInteraction AS f
        JOIN bi.DimApp AS a ON a.AppKey = f.AppKey
        WHERE @fromKey IS NULL OR f.DateKey >= @fromKey
        GROUP BY f.DateKey, f.UserKey
    ),
    s AS
    (
        SELECT s.DateKey, s.UserKey, COUNT(*) AS Sessions, SUM(CAST(s.IsMultiTurn AS INT)) AS MultiTurnSessions
        FROM bi.FactSession AS s
        WHERE @fromKey IS NULL OR s.DateKey >= @fromKey
        GROUP BY s.DateKey, s.UserKey
    )
    SELECT COALESCE(i.DateKey, s.DateKey) AS DateKey, COALESCE(i.UserKey, s.UserKey) AS UserKey,
           CAST(ISNULL(i.Interactions, 0) AS INT) AS Interactions, ISNULL(i.Prompts, 0) AS Prompts,
           CAST(ISNULL(i.AppsUsed, 0) AS TINYINT) AS AppsUsed,
           ISNULL(i.AgentInteractions, 0) AS AgentInteractions, ISNULL(i.WebGrounded, 0) AS WebGrounded,
           ISNULL(i.ChatInteractions, 0) AS ChatInteractions, ISNULL(i.LicensedUse, 0) AS LicensedUse, ISNULL(i.PremiumUse, 0) AS PremiumUse,
           ISNULL(i.Jailbreak, 0) AS Jailbreak, ISNULL(i.PromptInjection, 0) AS PromptInjection,
           ISNULL(i.BlockedResource, 0) AS BlockedResource, ISNULL(i.LabeledContent, 0) AS LabeledContent,
           ISNULL(s.Sessions, 0) AS Sessions, ISNULL(s.MultiTurnSessions, 0) AS MultiTurnSessions
    INTO #day
    FROM i
    FULL JOIN s ON s.DateKey = i.DateKey AND s.UserKey = i.UserKey
    OPTION (RECOMPILE);

    /* Labels and sites, from bi.FactAccessedResource: the label on each resource
       read, the site of labeled content, and the site of content Copilot was
       blocked from. Sites are already cut to https://host/sites/name there.
       Interactions, not resources (2.4.12): the fact has a row per resource, and
       each was counted, so an answer drawn from six files with one label added
       six to that label and its site, where the page's own count of labeled
       interactions added one, and the label alert fired on a single interaction. */
    DROP TABLE IF EXISTS #res;
    WITH r AS
    (
        SELECT f.DateKey, f.UserKey, f.RecordId, f.IsBlocked, l.LabelId, s.Site
        FROM bi.FactAccessedResource AS f
        JOIN bi.DimSensitivityLabel AS l ON l.SensitivityLabelKey = f.SensitivityLabelKey
        JOIN bi.DimSite AS s ON s.SiteKey = f.SiteKey
        WHERE (@fromKey IS NULL OR f.DateKey >= @fromKey)
          AND (l.LabelId IS NOT NULL OR f.IsBlocked = 1)
    ),
    items AS
    (
        SELECT DateKey, UserKey, RecordId, 'Label' AS Kind, LOWER(CAST(LabelId AS NVARCHAR(36))) AS Item FROM r WHERE LabelId IS NOT NULL
        UNION ALL
        SELECT DateKey, UserKey, RecordId, 'Site', Site FROM r WHERE LabelId IS NOT NULL AND Site <> N'(none)'
        UNION ALL
        SELECT DateKey, UserKey, RecordId, 'Blocked', Site FROM r WHERE IsBlocked = 1 AND Site <> N'(none)'
    )
    SELECT DateKey, UserKey, Kind, CAST(LEFT(Item, 400) AS NVARCHAR(400)) AS Item, COUNT(DISTINCT RecordId) AS Interactions
    INTO #res
    FROM items
    GROUP BY DateKey, UserKey, Kind, LEFT(Item, 400)
    OPTION (RECOMPILE);

    BEGIN TRANSACTION;
    IF @fromKey IS NULL AND HAS_PERMS_BY_NAME(N'dash.UserDay', N'OBJECT', N'ALTER') = 1
        TRUNCATE TABLE dash.UserDay;
    ELSE IF @fromKey IS NULL
        DELETE FROM dash.UserDay;
    ELSE
        DELETE FROM dash.UserDay WHERE DateKey >= @fromKey;
    INSERT INTO dash.UserDay WITH (TABLOCK)
        (DateKey, UserKey, Interactions, Prompts, AppsUsed, AgentInteractions, WebGrounded, ChatInteractions,
         LicensedUse, PremiumUse, Jailbreak, PromptInjection, BlockedResource, LabeledContent, Sessions, MultiTurnSessions)
    SELECT DateKey, UserKey, Interactions, Prompts, AppsUsed, AgentInteractions, WebGrounded, ChatInteractions,
           LicensedUse, PremiumUse, Jailbreak, PromptInjection, BlockedResource, LabeledContent, Sessions, MultiTurnSessions
    FROM #day;
    DECLARE @dayRows INT = @@ROWCOUNT;
    IF @fromKey IS NULL AND HAS_PERMS_BY_NAME(N'dash.ResourceDay', N'OBJECT', N'ALTER') = 1
        TRUNCATE TABLE dash.ResourceDay;
    ELSE IF @fromKey IS NULL
        DELETE FROM dash.ResourceDay;
    ELSE
        DELETE FROM dash.ResourceDay WHERE DateKey >= @fromKey;
    INSERT INTO dash.ResourceDay WITH (TABLOCK) (DateKey, UserKey, Kind, Item, Interactions)
    SELECT DateKey, UserKey, Kind, Item, Interactions FROM #res;
    /* 2.4.7: the flagged interactions, for Security > Flagged interactions. From @fromKey on, or all of them when
       the table is empty, as it is the first time after an upgrade (a refresh of today alone would leave history out). */
    IF OBJECT_ID(N'dash.FlaggedInteraction', N'U') IS NOT NULL
    BEGIN
        DECLARE @flagFrom INT = CASE WHEN EXISTS (SELECT 1 FROM dash.FlaggedInteraction) THEN @fromKey ELSE NULL END;
        DELETE FROM dash.FlaggedInteraction WHERE @flagFrom IS NULL OR DateKey >= @flagFrom;
        INSERT INTO dash.FlaggedInteraction (DateKey, UserKey, RecordId, ActivityHour, AppKey, AgentKey, JailbreakDetected, PromptInjectionDetected, HasBlockedResource)
        SELECT f.DateKey, f.UserKey, f.RecordId, f.ActivityHour, f.AppKey, f.AgentKey, f.JailbreakDetected, f.PromptInjectionDetected, f.HasBlockedResource
        FROM bi.FactInteraction AS f
        WHERE (@flagFrom IS NULL OR f.DateKey >= @flagFrom)
          AND (f.JailbreakDetected = 1 OR f.PromptInjectionDetected = 1 OR f.HasBlockedResource = 1);
    END;
    DELETE FROM dash.AgentUse;
    INSERT INTO dash.AgentUse (AgentKey, FirstDateKey, LastDateKey, Interactions)
    SELECT AgentKey, MIN(DateKey), MAX(DateKey), COUNT_BIG(*) FROM bi.FactInteraction GROUP BY AgentKey;
    COMMIT TRANSACTION;

    INSERT INTO dash.RefreshRun (StartedUtc, EndedUtc, SinceUtc, FromDateKey, People, ClosureRows, UserDayRows, BiRunId)
    VALUES (@started, SYSUTCDATETIME(), @SinceUtc, @fromKey, @people, @closure, @dayRows, @biRun);

    SET @msg = CONCAT(N'Dashboard refreshed', CASE WHEN @fromKey IS NULL THEN N' (every day)' ELSE CONCAT(N' from ', @fromKey) END,
                      N': ', FORMAT(@people, 'N0'), N' people, ', FORMAT(@closure, 'N0'), N' org pairs, ',
                      FORMAT(@dayRows, 'N0'), N' person-days written.',
                      CASE WHEN @top IS NULL THEN N' No top of the organization: none is set, and no one person has half the directory under them, so nobody sees the whole organization by level.'
                           WHEN @detected = @top AND NOT EXISTS (SELECT 1 FROM dash.Setting WHERE Name = 'OrgTopUpn' AND NULLIF(LTRIM(RTRIM(Value)), N'') IS NOT NULL)
                                THEN CONCAT(N' Top of the organization, from the org chart: ', @top, N'.')
                           ELSE N'' END);
    RAISERROR(@msg, 0, 1) WITH NOWAIT;
END;
GO
```

### C.14 `sql/Create-CopilotOperationsSchema.sql`

Copilot database, with `-Dashboard`. The dashboard's operations tables, its database role and grants, privacy mode and retention, and seven more `bi` views. 725 lines.

```sql
/*
    Copilot Insights - operations schema
    ------------------------------------------------------------------------
    What the dashboard's operations features need, on top of the dashboard
    schema (Create-CopilotDashboardSchema.sql, run first):

      settings          the new dash.Setting rows: email, alert thresholds,
                        recipients, assumptions, retention
      dash.Secret       passwords and client secrets, encrypted in the database
                        (a certificate and symmetric key kept by SQL Server)
      dash.Subscription scheduled digests by email
      dash.NotificationLog, dash.AlertState
                        what was sent, and what an alert last fired on
      dash.Target       adoption targets by unit
      dash.TimeSaving   minutes saved per interaction, by app (the value report)
      dash.TrainingAttendance
                        who attended which training, for the effect report
      dash.ValueName    readable names for the codes in audit.ValueCatalog
      dash.usp_ApplyRetention, dash.usp_SetPrivacyMode, dash.usp_SetSecret,
      dash.usp_GetSecret
      CopilotInsightsApp
                        the database role the dashboard's account belongs to:
                        every grant the site needs, kept here so an upgrade
                        needs no grant script
      bi.OrgSecurity, bi.ValueNames, bi.LicensePool, bi.DepartmentWeek
                        for Power BI: row-level security from the org chart,
                        value names, the license pool, and department figures
                        with small groups hidden

    Idempotent. Single batch. Requires SQL Server 2017+.
*/
SET NOCOUNT ON;

IF OBJECT_ID(N'dash.Setting', N'U') IS NULL
    THROW 50001, N'Run Create-CopilotDashboardSchema.sql first.', 1;

/* ---- Settings ---------------------------------------------------------- */

MERGE dash.Setting AS t
USING (VALUES
    ('DashboardUrl',            CAST(NULL AS NVARCHAR(400)), N'The dashboard''s address, for links in email (https://insights.contoso.com).'),
    ('MailMode',                N'Off',   N'How email is sent: Off, ExchangeOnline or Smtp.'),
    ('MailFrom',                NULL,     N'The From address. For Exchange Online, the mailbox that sends.'),
    ('MailReplyTo',             NULL,     N'Optional Reply-To address.'),
    ('SmtpHost',                NULL,     N'SMTP server (Smtp mode). Exchange Online uses smtp.office365.com.'),
    ('SmtpPort',                N'587',   N'SMTP port: any, such as 25, 465, 587 or 2525.'),
    ('SmtpSecurity',            N'StartTls', N'StartTls, Implicit (TLS from the first byte, usually 465), Auto or None.'),
    ('SmtpUser',                NULL,     N'User name for SMTP authentication; empty for none. The password is kept encrypted (dash.Secret).'),
    ('SmtpAcceptAnyCertificate', N'0',    N'1 accepts the SMTP server''s certificate without checking it (a lab relay with a self-signed certificate).'),
    ('ExoAuth',                 N'Password', N'Exchange Online sign-in: Password (SMTP AUTH with the mailbox''s password) or OAuth (an app with SMTP.SendAsApp).'),
    ('ExoTenantId',             NULL,     N'Tenant for Exchange Online OAuth.'),
    ('ExoClientId',             NULL,     N'App (client) id for Exchange Online OAuth. The client secret is kept encrypted (dash.Secret).'),
    ('AlertAdminTo',            NULL,     N'Who hears about pipeline failures and stale data (addresses separated by ;).'),
    ('AlertSecurityTo',         NULL,     N'Who gets security alerts.'),
    ('AlertLicensingTo',        NULL,     N'Who gets license alerts: the pool running out, licenses on disabled accounts.'),
    ('AlertAdoptionTo',         NULL,     N'Who gets usage drops and spikes.'),
    ('AlertAgentsTo',           NULL,     N'Who hears about agents seen for the first time.'),
    ('EventLogAlerts',          N'1',     N'1 also writes each alert to the Windows Application log (source Copilot Insights), for a SIEM to collect.'),
    ('FreshnessHours',          N'36',    N'Data older than this is stale: a banner on every page and an alert.'),
    ('AlertJailbreakPerDay',    N'1',     N'Jailbreak attempts in a day that raise a security alert; 0 turns it off.'),
    ('AlertInjectionPerDay',    N'1',     N'Prompt injections in a day that raise a security alert; 0 turns it off.'),
    ('AlertBlockedPerDay',      N'25',    N'Interactions with a blocked resource in a day that raise a security alert; 0 turns it off.'),
    ('AlertLabelRank',          NULL,     N'Label order (Admin > Reference data) at or above which labeled content is alert-worthy; empty turns it off.'),
    ('AlertLabelPerDay',        N'1',     N'Interactions touching content at or above AlertLabelRank in a day that raise a security alert.'),
    ('AlertSpikeFactor',        N'3',     N'A signal also alerts when a day is this many times its 28-day daily average.'),
    ('AnomalyDropPercent',      N'50',    N'A unit''s day this far below its usual level for that weekday is a drop.'),
    ('AnomalySpikePercent',     N'200',   N'A unit''s day this far above its usual level is a spike.'),
    ('PoolWarnPercent',         N'95',    N'Alert when this share of the Copilot seats bought is assigned.'),
    ('NewAgentAlert',           N'1',     N'1 sends an alert when an agent is used for the first time.'),
    ('MinutesSavedDefault',     N'3',     N'Minutes one interaction saves, for apps without their own figure (Admin > Targets and assumptions).'),
    ('HourlyCost',              N'50',    N'What an hour of an employee''s time is worth, in LicenseCurrency, for the value report.'),
    ('ChampionsPerDepartment',  N'3',     N'How many champions to suggest per department.'),
    ('LicenseGroupId',          NULL,     N'Object id of the Entra group that assigns Copilot licenses, if licenses are group-based: the license export adds and removes its members.'),
    ('AuditLogRetentionDays',   N'730',   N'Dashboard audit-log rows older than this are deleted each night; 0 keeps them.'),
    ('NotificationRetentionDays', N'365', N'Notification-log rows older than this are deleted each night; 0 keeps them.'),
    ('JsonRetentionDays',       N'0',     N'Clear the stored JSON of records older than this each night (the reports keep working); 0 keeps it. At least 200.'),
    ('ApiEnabled',              N'1',     N'1 answers the read-only data API (/api/v1).')
) AS s (Name, Value, Description)
ON t.Name = s.Name
WHEN MATCHED AND ISNULL(t.Description, N'') <> s.Description THEN UPDATE SET Description = s.Description
WHEN NOT MATCHED THEN INSERT (Name, Value, Description) VALUES (s.Name, s.Value, s.Description);

/* ---- Secrets ------------------------------------------------------------
    Encrypted with a symmetric key protected by a certificate, whose private
    key the database master key protects. The master key is created here with
    a random password nobody needs: SQL Server also protects it with the
    instance's service master key and opens it itself. Restored to another
    instance, the secrets cannot be read: run this again, then enter them again
    in Admin > Email and Admin > Alerts and digests.

    2.4.12: on another instance neither the service master key nor the
    password opens the master key, so the secrets could be neither read nor
    stored again, and re-running this kept the keys that could not be opened.
    Now a chain that cannot be opened is made again, and the secrets it can no
    longer read are removed. It is left as it is when the master key protects
    anything else in the database, which only its owner can recover. */
IF CERT_ID(N'CopilotInsightsSecretCert') IS NOT NULL AND KEY_ID(N'CopilotInsightsSecretKey') IS NOT NULL
BEGIN
    BEGIN TRY
        OPEN SYMMETRIC KEY CopilotInsightsSecretKey DECRYPTION BY CERTIFICATE CopilotInsightsSecretCert;
        CLOSE SYMMETRIC KEY CopilotInsightsSecretKey;
    END TRY
    BEGIN CATCH
        IF ERROR_NUMBER() NOT IN (15581, 15329, 15466)
            THROW;
        IF EXISTS (SELECT 1 FROM sys.certificates WHERE pvt_key_encryption_type = 'MK' AND name <> N'CopilotInsightsSecretCert')
           OR EXISTS (SELECT 1 FROM sys.asymmetric_keys WHERE pvt_key_encryption_type = 'MK')
           OR EXISTS (SELECT 1 FROM sys.database_scoped_credentials)
            RAISERROR('The stored secrets cannot be read on this SQL Server instance, and its database master key protects other keys too, so it is left as it is: open the master key with its password, or restore the original instance''s service master key, then run this again.', 0, 1) WITH NOWAIT;
        ELSE
        BEGIN
            IF OBJECT_ID(N'dash.Secret', N'U') IS NOT NULL EXEC (N'DELETE FROM dash.Secret;');
            EXEC (N'DROP SYMMETRIC KEY CopilotInsightsSecretKey;');
            EXEC (N'DROP CERTIFICATE CopilotInsightsSecretCert;');
            EXEC (N'DROP MASTER KEY;');
            RAISERROR('The stored secrets were encrypted on another SQL Server instance and cannot be read here: their keys are made again. Enter the email password or client secret, and the webhook address, again in Admin.', 0, 1) WITH NOWAIT;
        END;
    END CATCH;
END;
IF NOT EXISTS (SELECT 1 FROM sys.symmetric_keys WHERE name = N'##MS_DatabaseMasterKey##')
BEGIN
    DECLARE @mk NVARCHAR(200) = CONCAT(CONVERT(NVARCHAR(64), CRYPT_GEN_RANDOM(32), 2), N'aA1!');
    EXEC (N'CREATE MASTER KEY ENCRYPTION BY PASSWORD = ''' + @mk + N''';');
END;
IF CERT_ID(N'CopilotInsightsSecretCert') IS NULL
    EXEC (N'CREATE CERTIFICATE CopilotInsightsSecretCert WITH SUBJECT = N''Copilot Insights secrets'', EXPIRY_DATE = ''20991231'';');
IF KEY_ID(N'CopilotInsightsSecretKey') IS NULL
    EXEC (N'CREATE SYMMETRIC KEY CopilotInsightsSecretKey WITH ALGORITHM = AES_256 ENCRYPTION BY CERTIFICATE CopilotInsightsSecretCert;');

IF OBJECT_ID(N'dash.Secret', N'U') IS NULL
    CREATE TABLE dash.Secret
    (
        Name        VARCHAR(64)     NOT NULL CONSTRAINT PK_DashSecret PRIMARY KEY,
        Cipher      VARBINARY(8000) NOT NULL,
        UpdatedBy   NVARCHAR(256)   NOT NULL,
        UpdatedUtc  DATETIME2(0)    NOT NULL CONSTRAINT DF_DashSecret_Updated DEFAULT (SYSUTCDATETIME())
    );

EXEC (N'CREATE OR ALTER PROCEDURE dash.usp_SetSecret
    @Name VARCHAR(64), @Value NVARCHAR(2000), @By NVARCHAR(256)
AS
BEGIN
    SET NOCOUNT ON;
    IF @Value IS NULL OR @Value = N''''
    BEGIN
        DELETE FROM dash.Secret WHERE Name = @Name;
        RETURN;
    END;
    OPEN SYMMETRIC KEY CopilotInsightsSecretKey DECRYPTION BY CERTIFICATE CopilotInsightsSecretCert;
    /* The name is the authenticator: a cipher copied onto another name will not decrypt. */
    DECLARE @c VARBINARY(8000) = ENCRYPTBYKEY(KEY_GUID(N''CopilotInsightsSecretKey''), @Value, 1, CONVERT(VARBINARY(64), @Name));
    CLOSE SYMMETRIC KEY CopilotInsightsSecretKey;
    UPDATE dash.Secret SET Cipher = @c, UpdatedBy = @By, UpdatedUtc = SYSUTCDATETIME() WHERE Name = @Name;
    IF @@ROWCOUNT = 0 INSERT INTO dash.Secret (Name, Cipher, UpdatedBy) VALUES (@Name, @c, @By);
END;');

EXEC (N'CREATE OR ALTER PROCEDURE dash.usp_GetSecret
    @Name VARCHAR(64), @Value NVARCHAR(2000) OUTPUT
AS
BEGIN
    SET NOCOUNT ON;
    SELECT @Value = CONVERT(NVARCHAR(2000), DECRYPTBYKEYAUTOCERT(CERT_ID(N''CopilotInsightsSecretCert''), NULL, Cipher, 1, CONVERT(VARBINARY(64), @Name)))
    FROM dash.Secret WHERE Name = @Name;
END;');

/* ---- Email: subscriptions, what was sent, alert state -------------------- */

IF OBJECT_ID(N'dash.Subscription', N'U') IS NULL
    CREATE TABLE dash.Subscription
    (
        SubscriptionId INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_DashSubscription PRIMARY KEY,
        Name        NVARCHAR(200)   NOT NULL,
        Report      VARCHAR(32)     NOT NULL,   -- Leadership | Adoption | Licensing | Security | Value
        Recipients  NVARCHAR(2000)  NOT NULL,   -- addresses separated by ;
        Frequency   VARCHAR(8)      NOT NULL CONSTRAINT CK_DashSubscription_Frequency CHECK (Frequency IN ('Daily', 'Weekly', 'Monthly')),
        DayOfWeek   TINYINT         NULL,       -- Weekly: 0 Sunday to 6 Saturday
        DayOfMonth  TINYINT         NULL,       -- Monthly: 1 to 28
        HourLocal   TINYINT         NOT NULL CONSTRAINT DF_DashSubscription_Hour DEFAULT (7),   -- in the reporting time zone
        RootUpn     NVARCHAR(256)   NULL,       -- a manager's organization; NULL for the whole organization
        Days        INT             NOT NULL CONSTRAINT DF_DashSubscription_Days DEFAULT (30),
        Enabled     BIT             NOT NULL CONSTRAINT DF_DashSubscription_Enabled DEFAULT (1),
        LastSentUtc DATETIME2(0)    NULL,
        LastStatus  NVARCHAR(400)   NULL,
        CreatedBy   NVARCHAR(256)   NOT NULL,
        CreatedUtc  DATETIME2(0)    NOT NULL CONSTRAINT DF_DashSubscription_Created DEFAULT (SYSUTCDATETIME())
    );

IF OBJECT_ID(N'dash.NotificationLog', N'U') IS NULL
    CREATE TABLE dash.NotificationLog
    (
        LogId       BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_DashNotificationLog PRIMARY KEY,
        AtUtc       DATETIME2(0)    NOT NULL CONSTRAINT DF_DashNotificationLog_At DEFAULT (SYSUTCDATETIME()),
        Kind        VARCHAR(32)     NOT NULL,   -- Pipeline | Freshness | Security | Anomaly | Pool | Leavers | NewAgent | Digest | Test
        Subject     NVARCHAR(400)   NOT NULL,
        Recipients  NVARCHAR(2000)  NULL,
        Status      VARCHAR(16)     NOT NULL,   -- Sent | Failed | NoRecipients | MailOff
        Error       NVARCHAR(2000)  NULL,
        INDEX IX_DashNotificationLog_At (AtUtc)
    );

/* Which alert fired last on what, so an alert is sent once per occurrence, not every few minutes. */
IF OBJECT_ID(N'dash.AlertState', N'U') IS NULL
    CREATE TABLE dash.AlertState
    (
        AlertKey     NVARCHAR(200)  NOT NULL CONSTRAINT PK_DashAlertState PRIMARY KEY,
        LastValue    NVARCHAR(400)  NULL,
        LastFiredUtc DATETIME2(0)   NOT NULL CONSTRAINT DF_DashAlertState_Fired DEFAULT (SYSUTCDATETIME())
    );

/* ---- Targets and assumptions -------------------------------------------- */

IF OBJECT_ID(N'dash.Target', N'U') IS NULL
    CREATE TABLE dash.Target
    (
        TargetId    INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_DashTarget PRIMARY KEY,
        RootUpn     NVARCHAR(256)   NULL,       -- a manager's organization; NULL for the whole organization
        Metric      VARCHAR(16)     NOT NULL CONSTRAINT CK_DashTarget_Metric CHECK (Metric IN ('Adoption', 'ActivePeople', 'ActiveShare')),
        TargetValue DECIMAL(12, 2)  NOT NULL,   -- a percentage for Adoption and ActiveShare, a count for ActivePeople
        StartDate   DATE            NOT NULL,
        DueDate     DATE            NOT NULL,
        Note        NVARCHAR(400)   NULL,
        CreatedBy   NVARCHAR(256)   NOT NULL,
        CreatedUtc  DATETIME2(0)    NOT NULL CONSTRAINT DF_DashTarget_Created DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT CK_DashTarget_Dates CHECK (DueDate > StartDate)
    );

IF OBJECT_ID(N'dash.TimeSaving', N'U') IS NULL
    CREATE TABLE dash.TimeSaving
    (
        App         NVARCHAR(200)   NOT NULL CONSTRAINT PK_DashTimeSaving PRIMARY KEY,   -- bi.DimApp.App
        Minutes     DECIMAL(6, 2)   NOT NULL CONSTRAINT CK_DashTimeSaving_Minutes CHECK (Minutes >= 0 AND Minutes <= 240),
        UpdatedBy   NVARCHAR(256)   NOT NULL,
        UpdatedUtc  DATETIME2(0)    NOT NULL CONSTRAINT DF_DashTimeSaving_Updated DEFAULT (SYSUTCDATETIME())
    );

IF OBJECT_ID(N'dash.TrainingAttendance', N'U') IS NULL
    CREATE TABLE dash.TrainingAttendance
    (
        Upn         NVARCHAR(256)   NOT NULL,
        Session     NVARCHAR(190)   NOT NULL,   -- short enough for the key (900 bytes)
        AttendedOn  DATE            NOT NULL,
        ImportedBy  NVARCHAR(256)   NOT NULL,
        ImportedUtc DATETIME2(0)    NOT NULL CONSTRAINT DF_DashTraining_Imported DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT PK_DashTrainingAttendance PRIMARY KEY (Upn, Session, AttendedOn),
        INDEX IX_DashTraining_Session (Session, AttendedOn)
    );

/* ---- Value names ---------------------------------------------------------- */

IF OBJECT_ID(N'dash.ValueName', N'U') IS NULL
    CREATE TABLE dash.ValueName
    (
        ColumnId    SMALLINT        NOT NULL,   -- audit.ValueCatalogColumn
        Value       NVARCHAR(400)   NOT NULL,
        Name        NVARCHAR(200)   NOT NULL,
        Note        NVARCHAR(400)   NULL,
        UpdatedBy   NVARCHAR(256)   NOT NULL,
        UpdatedUtc  DATETIME2(0)    NOT NULL CONSTRAINT DF_DashValueName_Updated DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT PK_DashValueName PRIMARY KEY (ColumnId, Value)
    );

/* A value's name where an administrator gave one, else the value itself: for the pages that show codes. */
EXEC (N'CREATE OR ALTER FUNCTION dash.fnValueName (@ColumnId SMALLINT, @Value NVARCHAR(400))
RETURNS NVARCHAR(400)
AS
BEGIN
    RETURN COALESCE((SELECT Name FROM dash.ValueName WHERE ColumnId = @ColumnId AND Value = @Value), @Value);
END;');

/* ---- 2.3.0, second part: triage, preferences, actions, contracts, views, classifications ---- */

/* Security's triage of each flagged interaction. */
IF OBJECT_ID(N'dash.FlagCase', N'U') IS NULL
    CREATE TABLE dash.FlagCase
    (
        RecordId    VARCHAR(64)     NOT NULL CONSTRAINT PK_DashFlagCase PRIMARY KEY,
        Status      VARCHAR(16)     NOT NULL CONSTRAINT CK_DashFlagCase_Status CHECK (Status IN ('New', 'Investigating', 'Closed', 'FalsePositive')),
        Owner       NVARCHAR(256)   NULL,
        Note        NVARCHAR(2000)  NULL,
        UpdatedBy   NVARCHAR(256)   NOT NULL,
        UpdatedUtc  DATETIME2(0)    NOT NULL CONSTRAINT DF_DashFlagCase_Updated DEFAULT (SYSUTCDATETIME())
    );

/* Managers who do not get their team digest, kept by administrators (Admin > Alerts and digests). PersonalDigest is unused since 2.4.4. */
IF OBJECT_ID(N'dash.DigestPreference', N'U') IS NULL
    CREATE TABLE dash.DigestPreference
    (
        Upn                 NVARCHAR(256)   NOT NULL CONSTRAINT PK_DashDigestPreference PRIMARY KEY,
        ManagerDigestOptOut BIT             NOT NULL CONSTRAINT DF_DashDigestPreference_Out DEFAULT (0),
        PersonalDigest      BIT             NOT NULL CONSTRAINT DF_DashDigestPreference_Personal DEFAULT (0),
        UpdatedUtc          DATETIME2(0)    NOT NULL CONSTRAINT DF_DashDigestPreference_Updated DEFAULT (SYSUTCDATETIME())
    );

/* One send per person, kind and period: a restart mid-round never sends twice. */
IF OBJECT_ID(N'dash.DigestSend', N'U') IS NULL
    CREATE TABLE dash.DigestSend
    (
        Upn         NVARCHAR(256)   NOT NULL,
        Kind        VARCHAR(16)     NOT NULL,   -- Manager | Personal
        PeriodKey   VARCHAR(16)     NOT NULL,   -- 2026-W40, 2026-10
        SentUtc     DATETIME2(0)    NOT NULL CONSTRAINT DF_DashDigestSend_Sent DEFAULT (SYSUTCDATETIME()),
        Status      VARCHAR(16)     NOT NULL,
        CONSTRAINT PK_DashDigestSend PRIMARY KEY (Upn, Kind, PeriodKey)
    );

/* The license changes the dashboard exported for someone to apply, to measure what happened next. */
IF OBJECT_ID(N'dash.LicenseAction', N'U') IS NULL
    CREATE TABLE dash.LicenseAction
    (
        ActionId    BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_DashLicenseAction PRIMARY KEY,
        Kind        VARCHAR(16)     NOT NULL,   -- upgrade | revoke | leaver | wave
        Upn         NVARCHAR(256)   NOT NULL,
        ExportedUtc DATETIME2(0)    NOT NULL CONSTRAINT DF_DashLicenseAction_Exported DEFAULT (SYSUTCDATETIME()),
        ExportedBy  NVARCHAR(256)   NOT NULL,
        INDEX IX_DashLicenseAction_Upn (Upn, ExportedUtc)
    );

/* License contracts, for the budget and renewal planner. */
IF OBJECT_ID(N'dash.LicenseContract', N'U') IS NULL
    CREATE TABLE dash.LicenseContract
    (
        ContractId    INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_DashLicenseContract PRIMARY KEY,
        Name          NVARCHAR(200)   NOT NULL,
        Seats         INT             NOT NULL CONSTRAINT CK_DashLicenseContract_Seats CHECK (Seats >= 0),
        MonthlyPrice  DECIMAL(12, 2)  NOT NULL,
        StartDate     DATE            NOT NULL,
        RenewalDate   DATE            NOT NULL,
        Note          NVARCHAR(400)   NULL,
        UpdatedBy     NVARCHAR(256)   NOT NULL,
        UpdatedUtc    DATETIME2(0)    NOT NULL CONSTRAINT DF_DashLicenseContract_Updated DEFAULT (SYSUTCDATETIME())
    );

/* Accounts a non-human rule matches that an administrator confirmed are a person, with the reason (2.4.5): a Copilot license
   on a non-human account is a policy breach, and these are not. One row per sign-in; removing it flags the account again. */
IF OBJECT_ID(N'dash.NonHumanException', N'U') IS NULL
    CREATE TABLE dash.NonHumanException
    (
        Upn          NVARCHAR(256)   NOT NULL CONSTRAINT PK_DashNonHumanException PRIMARY KEY,
        Reason       NVARCHAR(400)   NOT NULL,
        ConfirmedBy  NVARCHAR(256)   NOT NULL,
        ConfirmedUtc DATETIME2(0)    NOT NULL CONSTRAINT DF_DashNonHumanException_Confirmed DEFAULT (SYSUTCDATETIME())
    );

/* Each person's saved views; one may be their start page. Unused since 2.4.4, which removed My Copilot; kept, with its rows. */
IF OBJECT_ID(N'dash.SavedView', N'U') IS NULL
    CREATE TABLE dash.SavedView
    (
        ViewId      INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_DashSavedView PRIMARY KEY,
        Owner       NVARCHAR(256)   NOT NULL,   -- the viewer's UPN or Windows name
        Name        NVARCHAR(100)   NOT NULL,
        Url         NVARCHAR(600)   NOT NULL,   -- a path and query on this site
        IsStart     BIT             NOT NULL CONSTRAINT DF_DashSavedView_Start DEFAULT (0),
        CreatedUtc  DATETIME2(0)    NOT NULL CONSTRAINT DF_DashSavedView_Created DEFAULT (SYSUTCDATETIME()),
        INDEX IX_DashSavedView_Owner (Owner)
    );

/* Job titles to job families, first matching rule wins (LIKE patterns). */
IF OBJECT_ID(N'dash.JobFamilyRule', N'U') IS NULL
    CREATE TABLE dash.JobFamilyRule
    (
        RuleId      INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_DashJobFamilyRule PRIMARY KEY,
        Pattern     NVARCHAR(200)   NOT NULL,   -- a LIKE pattern on bi.DimUser.JobTitle, such as %engineer%
        Family      NVARCHAR(100)   NOT NULL,
        Priority    INT             NOT NULL CONSTRAINT DF_DashJobFamilyRule_Priority DEFAULT (100),
        UpdatedBy   NVARCHAR(256)   NOT NULL,
        UpdatedUtc  DATETIME2(0)    NOT NULL CONSTRAINT DF_DashJobFamilyRule_Updated DEFAULT (SYSUTCDATETIME())
    );

/* Use cases from the app and what the interaction did, first matching rule wins. */
IF OBJECT_ID(N'dash.UseCaseRule', N'U') IS NULL
BEGIN
    CREATE TABLE dash.UseCaseRule
    (
        RuleId       INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_DashUseCaseRule PRIMARY KEY,
        AppPattern   NVARCHAR(200)   NOT NULL,   -- a LIKE pattern on bi.DimApp.App
        WebGrounded  BIT             NULL,       -- NULL: either
        WithAgent    BIT             NULL,       -- NULL: either
        UseCase      NVARCHAR(100)   NOT NULL,
        Priority     INT             NOT NULL CONSTRAINT DF_DashUseCaseRule_Priority DEFAULT (100),
        UpdatedBy    NVARCHAR(256)   NOT NULL,
        UpdatedUtc   DATETIME2(0)    NOT NULL CONSTRAINT DF_DashUseCaseRule_Updated DEFAULT (SYSUTCDATETIME())
    );
    INSERT INTO dash.UseCaseRule (AppPattern, WebGrounded, WithAgent, UseCase, Priority, UpdatedBy) VALUES
        (N'%', NULL, 1, N'Working with an agent', 10, N'default'),
        (N'%Teams%', NULL, NULL, N'Meetings and chats', 20, N'default'),
        (N'Outlook', NULL, NULL, N'Email', 20, N'default'),
        (N'Word', NULL, NULL, N'Writing documents', 20, N'default'),
        (N'Excel', NULL, NULL, N'Analyzing data', 20, N'default'),
        (N'PowerPoint', NULL, NULL, N'Presentations', 20, N'default'),
        (N'%Loop%', NULL, NULL, N'Collaborative notes', 20, N'default'),
        (N'%OneNote%', NULL, NULL, N'Collaborative notes', 20, N'default'),
        (N'%', 1, NULL, N'Research on the web', 50, N'default'),
        (N'%', NULL, NULL, N'Asking questions (Copilot Chat)', 999, N'default');
END;

/* 2.4.0: one retry policy. A scheduled digest or extract that fails is tried again an hour later, three tries in all,
   before it waits for its next period (as manager digests do, through dash.DigestSend). dash.Extract's
   columns follow its CREATE TABLE below. */
IF COL_LENGTH(N'dash.Subscription', N'FailedTries') IS NULL
    ALTER TABLE dash.Subscription ADD FailedTries TINYINT NOT NULL CONSTRAINT DF_DashSubscription_Failed DEFAULT (0), RetryAfterUtc DATETIME2(0) NULL;

/* Markers on trend charts: a rollout wave, a training push, an outage, a policy change. */
IF OBJECT_ID(N'dash.EventMarker', N'U') IS NULL
    CREATE TABLE dash.EventMarker
    (
        MarkerId    INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_DashEventMarker PRIMARY KEY,
        Day         DATE            NOT NULL,
        Label       NVARCHAR(100)   NOT NULL,
        Kind        VARCHAR(16)     NOT NULL CONSTRAINT CK_DashEventMarker_Kind CHECK (Kind IN ('Rollout', 'Training', 'Outage', 'Policy', 'Other')),
        UpdatedBy   NVARCHAR(256)   NOT NULL,
        INDEX IX_DashEventMarker_Day (Day)
    );

/* Scheduled CSV extracts to a folder or share. */
IF OBJECT_ID(N'dash.Extract', N'U') IS NULL
    CREATE TABLE dash.Extract
    (
        ExtractId   INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_DashExtract PRIMARY KEY,
        Name        NVARCHAR(100)   NOT NULL,
        Dataset     VARCHAR(32)     NOT NULL,
        Folder      NVARCHAR(400)   NOT NULL,   -- a local path or \\server\share\folder the site's account may write
        Frequency   VARCHAR(8)      NOT NULL CONSTRAINT CK_DashExtract_Frequency CHECK (Frequency IN ('Daily', 'Weekly', 'Monthly')),
        DayOfWeek   TINYINT         NULL,
        DayOfMonth  TINYINT         NULL,
        HourLocal   TINYINT         NOT NULL CONSTRAINT DF_DashExtract_Hour DEFAULT (6),
        Days        INT             NOT NULL CONSTRAINT DF_DashExtract_Days DEFAULT (30),
        Enabled     BIT             NOT NULL CONSTRAINT DF_DashExtract_Enabled DEFAULT (1),
        LastRunUtc  DATETIME2(0)    NULL,
        LastStatus  NVARCHAR(400)   NULL,
        CreatedBy   NVARCHAR(256)   NOT NULL
    );
/* The retry policy's columns (2.4.0), after the table exists. */
IF COL_LENGTH(N'dash.Extract', N'FailedTries') IS NULL
    ALTER TABLE dash.Extract ADD FailedTries TINYINT NOT NULL CONSTRAINT DF_DashExtract_Failed DEFAULT (0), RetryAfterUtc DATETIME2(0) NULL;

MERGE dash.Setting AS t
USING (VALUES
    ('AlertWebhookFormat',     N'Teams', N'How alerts are posted to the webhook (its address is stored encrypted): Teams (a Workflows or incoming-webhook URL) or Json (any endpoint).'),
    ('ManagerDigest',          N'Off',   N'Off, Weekly or Monthly: every manager with enough people under them gets their team''s leadership digest.'),
    ('ManagerDigestMinPeople', N'10',    N'Managers with at least this many people under them get the manager digest.'),
    ('ManagerDigestDay',       N'1',     N'The manager digest''s day: 0 Sunday to 6 Saturday (weekly), or 1 to 28 (monthly).'),
    ('DigestHour',             N'7',     N'The hour the manager digests go out, in the reporting time zone.'),
    ('CertificateWarnDays',    N'30',    N'Alert when the pipeline''s app certificate expires within this many days (and again at 14 and 3).'),
    ('RolloutWaveSize',        N'50',    N'The default number of people in a rollout wave.'),
    ('NonHumanGuests',         N'On',    N'A Copilot license on a guest is a non-human-account breach: On or Off (Admin > Reference data).'),
    ('NonHumanNames',          N'On',    N'Sign-ins and names that read like a service, shared, test or room account count as non-human: On or Off.'),
    ('NonHumanNoChart',        N'On',    N'Accounts with no manager and outside the org chart count as non-human: On or Off.'),
    ('NonHumanPatterns',       NULL,     N'More sign-in patterns that mark a non-human account, comma-separated, * for any text (bot-*, *-svc).'),
    ('NonHumanEmployeeTypes',  NULL,     N'Employee types that mark a non-human account, comma-separated (SERVICE, RESOURCE).'),
    ('NonHumanGroups',         NULL,     N'AD groups (DOMAIN\Group) whose members are non-human accounts, comma-separated; the directory load reads their members.'),
    ('NonHumanAlert',          N'1',     N'1 alerts Security and Licensing the first time a Copilot license is found on a non-human account; 0 does not.')
) AS src (Name, Value, Description)
ON t.Name = src.Name
WHEN MATCHED AND ISNULL(t.Description, N'') <> src.Description THEN UPDATE SET Description = src.Description
WHEN NOT MATCHED THEN INSERT (Name, Value, Description) VALUES (src.Name, src.Value, src.Description);
/* 2.4.4 removed the personal weekly email, and with it its setting. */
DELETE FROM dash.Setting WHERE Name = 'PersonalDigest';

/* ---- Privacy and retention ------------------------------------------------ */

/* Privacy mode lives in audit.PipelineSetting, which bi.Users reads. Owned by dbo like the
   table, so the dashboard's account needs EXECUTE here to change it. It reads it with SELECT
   on audit.PipelineSetting (granted below): without that, the dashboard treats privacy mode as
   on (2.4.12), where a read that failed used to turn it off. */
EXEC (N'CREATE OR ALTER PROCEDURE dash.usp_SetPrivacyMode
    @Mode NVARCHAR(16), @By NVARCHAR(256)
AS
BEGIN
    SET NOCOUNT ON;
    IF @Mode NOT IN (N''Off'', N''Pseudonymize'') THROW 50002, N''@Mode is Off or Pseudonymize.'', 1;
    IF NOT EXISTS (SELECT 1 FROM audit.PipelineSetting WHERE Name = N''PseudonymSalt'')
        INSERT INTO audit.PipelineSetting (Name, Value, UpdatedBy) VALUES (N''PseudonymSalt'', CONVERT(NVARCHAR(64), CRYPT_GEN_RANDOM(32), 2), @By);
    UPDATE audit.PipelineSetting SET Value = @Mode, UpdatedBy = @By, UpdatedUtc = SYSUTCDATETIME() WHERE Name = N''PrivacyMode'';
    IF @@ROWCOUNT = 0 INSERT INTO audit.PipelineSetting (Name, Value, UpdatedBy) VALUES (N''PrivacyMode'', @Mode, @By);
END;');

EXEC (N'CREATE OR ALTER PROCEDURE dash.usp_ApplyRetention
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @audit INT = TRY_CAST((SELECT Value FROM dash.Setting WHERE Name = ''AuditLogRetentionDays'') AS INT),
            @notes INT = TRY_CAST((SELECT Value FROM dash.Setting WHERE Name = ''NotificationRetentionDays'') AS INT),
            @json  INT = TRY_CAST((SELECT Value FROM dash.Setting WHERE Name = ''JsonRetentionDays'') AS INT),
            @a INT = 0, @n INT = 0, @j NVARCHAR(200) = N''JSON kept'';
    /* The audit log is kept at least a year (0 keeps it): a stored 1-364 counts as 365 (2.4.12).
       Before, only the admin form held that floor, so a value set in SQL deleted it sooner. */
    IF @audit BETWEEN 1 AND 364 SET @audit = 365;
    IF @audit > 0
    BEGIN
        DELETE FROM dash.AuditLog WHERE AtUtc < DATEADD(DAY, -@audit, SYSUTCDATETIME());
        SET @a = @@ROWCOUNT;
    END;
    IF @notes > 0
    BEGIN
        DELETE FROM dash.NotificationLog WHERE AtUtc < DATEADD(DAY, -@notes, SYSUTCDATETIME());
        SET @n = @@ROWCOUNT;
    END;
    /* Past Purview''s own 180 days the JSON cannot be fetched again, so never less than 200. */
    IF @json >= 200 AND OBJECT_ID(N''audit.usp_PurgeCopilotAuditJson'', N''P'') IS NOT NULL
    BEGIN
        EXEC audit.usp_PurgeCopilotAuditJson @RetainDays = @json, @WhatIf = 0;
        SET @j = CONCAT(N''JSON cleared past '', @json, N'' days'');
    END;
    SELECT CONCAT(@a, N'' audit-log row(s) and '', @n, N'' notification row(s) past retention deleted; '', @j, N''.'');
END;');

/* ---- The dashboard's database role ---------------------------------------- */

IF DATABASE_PRINCIPAL_ID(N'CopilotInsightsApp') IS NULL
    CREATE ROLE CopilotInsightsApp AUTHORIZATION dbo;
GRANT SELECT ON SCHEMA::bi TO CopilotInsightsApp;
GRANT SELECT ON SCHEMA::dash TO CopilotInsightsApp;
DENY SELECT ON dash.Secret TO CopilotInsightsApp;   -- read only through dash.usp_GetSecret
GRANT INSERT, UPDATE, DELETE ON dash.Setting TO CopilotInsightsApp;
GRANT INSERT, UPDATE, DELETE ON dash.RoleGrant TO CopilotInsightsApp;
GRANT INSERT, UPDATE, DELETE ON dash.AgentAlias TO CopilotInsightsApp;
GRANT INSERT, UPDATE, DELETE ON dash.SensitivityLabel TO CopilotInsightsApp;
GRANT INSERT, UPDATE, DELETE ON dash.Subscription TO CopilotInsightsApp;
GRANT INSERT, UPDATE, DELETE ON dash.AlertState TO CopilotInsightsApp;
GRANT INSERT, UPDATE, DELETE ON dash.Target TO CopilotInsightsApp;
GRANT INSERT, UPDATE, DELETE ON dash.TimeSaving TO CopilotInsightsApp;
GRANT INSERT, UPDATE, DELETE ON dash.TrainingAttendance TO CopilotInsightsApp;
GRANT INSERT, UPDATE, DELETE ON dash.ValueName TO CopilotInsightsApp;
GRANT INSERT, UPDATE, DELETE ON dash.FlagCase TO CopilotInsightsApp;
GRANT INSERT, UPDATE, DELETE ON dash.DigestPreference TO CopilotInsightsApp;
GRANT INSERT, UPDATE, DELETE ON dash.DigestSend TO CopilotInsightsApp;
GRANT INSERT, UPDATE, DELETE ON dash.LicenseAction TO CopilotInsightsApp;
GRANT INSERT, UPDATE, DELETE ON dash.LicenseContract TO CopilotInsightsApp;
GRANT INSERT, UPDATE, DELETE ON dash.SavedView TO CopilotInsightsApp;
GRANT INSERT, UPDATE, DELETE ON dash.NonHumanException TO CopilotInsightsApp;
GRANT INSERT, UPDATE, DELETE ON dash.JobFamilyRule TO CopilotInsightsApp;
GRANT INSERT, UPDATE, DELETE ON dash.UseCaseRule TO CopilotInsightsApp;
GRANT INSERT, UPDATE, DELETE ON dash.EventMarker TO CopilotInsightsApp;
GRANT INSERT, UPDATE, DELETE ON dash.Extract TO CopilotInsightsApp;
GRANT INSERT ON dash.AuditLog TO CopilotInsightsApp;
GRANT INSERT ON dash.NotificationLog TO CopilotInsightsApp;
GRANT EXECUTE ON dash.usp_RefreshDashboard TO CopilotInsightsApp;
GRANT EXECUTE ON dash.usp_SetSecret TO CopilotInsightsApp;
GRANT EXECUTE ON dash.usp_GetSecret TO CopilotInsightsApp;
GRANT EXECUTE ON dash.usp_SetPrivacyMode TO CopilotInsightsApp;
GRANT EXECUTE ON dash.usp_ApplyRetention TO CopilotInsightsApp;
GRANT EXECUTE ON dash.fnValueName TO CopilotInsightsApp;
/* Opening the secrets key checks the caller's own rights on it; ownership chaining does not cover keys. */
GRANT VIEW DEFINITION ON SYMMETRIC KEY::CopilotInsightsSecretKey TO CopilotInsightsApp;
GRANT CONTROL ON CERTIFICATE::CopilotInsightsSecretCert TO CopilotInsightsApp;
IF OBJECT_ID(N'audit.SessionAppCategory', N'U') IS NOT NULL
    GRANT SELECT, INSERT, UPDATE ON audit.SessionAppCategory TO CopilotInsightsApp;
/* The pipeline tables Admin > Health, the license reports and the value catalog read, and the members of the groups that mark non-human accounts (2.4.5). */
DECLARE @t SYSNAME, @g NVARCHAR(400);
DECLARE t CURSOR LOCAL FAST_FORWARD FOR
    SELECT name FROM (VALUES (N'CopilotLoad'), (N'CopilotCsvImport'), (N'CopilotSessionRun'), (N'UserDirectory'), (N'PipelineRun'),
                             (N'PipelineRunStep'), (N'PipelineSetting'), (N'LicenseSku'), (N'LicenseSkuDay'), (N'CopilotLicense'), (N'UserLicenseSku'),
                             (N'ValueCatalog'), (N'ValueCatalogColumn'), (N'CopilotLoadWindow'), (N'CopilotBackfillWindow'), (N'GroupMember')) AS v (name)
    WHERE OBJECT_ID(N'audit.' + name, N'U') IS NOT NULL;
OPEN t;
FETCH NEXT FROM t INTO @t;
WHILE @@FETCH_STATUS = 0
BEGIN
    SET @g = N'GRANT SELECT ON audit.' + QUOTENAME(@t) + N' TO CopilotInsightsApp;';
    EXEC (@g);
    FETCH NEXT FROM t INTO @t;
END;
CLOSE t;
DEALLOCATE t;
IF OBJECT_ID(N'audit.ReportingTimeZone', N'FN') IS NOT NULL
    GRANT EXECUTE ON audit.ReportingTimeZone TO CopilotInsightsApp;

/* ---- For Power BI ----------------------------------------------------------- */

/*
    Row-level security from the org chart: one row per (viewer, person they may
    see). A manager sees everyone under them; the top of the organization, the
    people reporting to them and anyone granted Organization see everyone. The
    Power BI model in powerbi/ uses it in its Organization role:
    Users[UserKey] IN the viewer's rows, by USERPRINCIPALNAME(). Organization
    granted to an AD group counts for its members too (2.4.0): the nightly
    directory load expands the group into audit.GroupMember.

    2.4.12: a grant by DOMAIN\name counts whatever type it was recorded with,
    as the dashboard matches it at sign-in. The directory load expands every
    one into audit.GroupMember: a group recorded as a user (GrantRole records
    one so when it cannot ask AD) into its members, and a person into their
    own UPN, which is what USERPRINCIPALNAME() gives. Before, both saw only
    their own people in Power BI, though the dashboard showed them everyone.
    A user grant by UPN (name@domain) names the viewer directly.

    WholeOrganization (2.4.12) marks the viewers who see everyone, so the role
    can show the tables of whole-organization figures (bi.LicensePool,
    bi.ValueNames, bi.DepartmentWeek) to them alone. Before, every member of
    the role read those tables, and this one, whole.

    Empty in privacy mode: each row pairs a real sign-in with a user key, which
    would undo the pseudonyms in bi.Users. With privacy mode on, share
    bi.DepartmentWeek rather than the person-level model (the model's
    Departments role).
*/
IF OBJECT_ID(N'audit.GroupMember', N'U') IS NULL
    CREATE TABLE audit.GroupMember (GroupName NVARCHAR(256) NOT NULL, UserPrincipalName NVARCHAR(256) NOT NULL,
        LoadedUtc DATETIME2(0) NOT NULL CONSTRAINT DF_GroupMember_Loaded DEFAULT (SYSUTCDATETIME()), CONSTRAINT PK_GroupMember PRIMARY KEY (GroupName, UserPrincipalName));
EXEC (N'CREATE OR ALTER VIEW bi.OrgSecurity AS
WITH whole AS (   -- everyone who sees the whole organization: the top two levels, and Organization granted to them or to a group they are in
    SELECT Upn FROM dash.Person WHERE Level IN (1, 2)
    UNION SELECT Principal FROM dash.RoleGrant WHERE PrincipalType = ''User'' AND Role = ''Organization'' AND Principal LIKE N''%@%''
    UNION SELECT gm.UserPrincipalName FROM audit.GroupMember AS gm
          JOIN dash.RoleGrant AS g ON g.Principal = gm.GroupName AND g.Role = ''Organization''
)
SELECT oc.AncestorUpn AS ViewerUpn, u.UserKey, CAST(0 AS BIT) AS WholeOrganization
FROM dash.OrgClosure AS oc
JOIN bi.DimUser AS u ON u.UserPrincipalName = oc.DescendantUpn
WHERE NOT EXISTS (SELECT 1 FROM whole WHERE whole.Upn = oc.AncestorUpn)
  AND NOT EXISTS (SELECT 1 FROM audit.PipelineSetting WHERE Name = N''PrivacyMode'' AND Value = N''Pseudonymize'')
UNION ALL
SELECT w.Upn, u.UserKey, CAST(1 AS BIT)
FROM whole AS w
CROSS JOIN bi.DimUser AS u
WHERE NOT EXISTS (SELECT 1 FROM audit.PipelineSetting WHERE Name = N''PrivacyMode'' AND Value = N''Pseudonymize'');');

/* Readable names for the cataloged codes; Name is NULL until someone names it. */
EXEC (N'CREATE OR ALTER VIEW bi.ValueNames AS
SELECT c.TableName, c.ColumnName, c.Label AS ColumnLabel, v.Value,
       COALESCE(n.Name, CASE WHEN c.ColumnId IN (11, 30) THEN sl.Name END) AS Name,
       v.Records, v.FirstSeenUtc, v.LastSeenUtc
FROM audit.ValueCatalog AS v
JOIN audit.ValueCatalogColumn AS c ON c.ColumnId = v.ColumnId
LEFT JOIN dash.ValueName AS n ON n.ColumnId = v.ColumnId AND n.Value = v.Value
LEFT JOIN dash.SensitivityLabel AS sl ON sl.LabelId = v.Value;');

/* Seats bought and assigned, per SKU per day. */
IF OBJECT_ID(N'audit.LicenseSkuDay', N'U') IS NOT NULL
    EXEC (N'CREATE OR ALTER VIEW bi.LicensePool AS
SELECT SnapshotDate, SkuPartNumber, IsCopilot, Enabled + Warning AS Bought, Consumed AS Assigned,
       Enabled + Warning - Consumed AS Available, Warning AS InGracePeriod, Suspended
FROM audit.LicenseSkuDay;');

/* 2.3.0: the dashboard's classifications for Power BI. Every combination of app, web search and agent
   with the use case the rules in dash.UseCaseRule give it (bi.Interactions.UseCaseKey points here). */
EXEC (N'CREATE OR ALTER VIEW bi.UseCases AS
SELECT a.AppKey * 4 + w.Web * 2 + g.WithAgent AS UseCaseKey, a.App, CAST(w.Web AS BIT) AS WebGrounded, CAST(g.WithAgent AS BIT) AS WithAgent,
       ISNULL(r.UseCase, N''Other'') AS UseCase
FROM bi.DimApp AS a
CROSS JOIN (VALUES (0), (1)) AS w (Web)
CROSS JOIN (VALUES (0), (1)) AS g (WithAgent)
OUTER APPLY (SELECT TOP (1) ur.UseCase FROM dash.UseCaseRule AS ur
             WHERE a.App LIKE ur.AppPattern AND (ur.WebGrounded IS NULL OR ur.WebGrounded = w.Web) AND (ur.WithAgent IS NULL OR ur.WithAgent = g.WithAgent)
             ORDER BY ur.Priority, ur.RuleId) AS r;');

/* Each person's job family from dash.JobFamilyRule (first matching pattern by priority). */
EXEC (N'CREATE OR ALTER VIEW bi.JobFamilies AS
SELECT u.UserKey, ISNULL(r.Family, N''(no family)'') AS JobFamily
FROM bi.DimUser AS u
OUTER APPLY (SELECT TOP (1) jr.Family FROM dash.JobFamilyRule AS jr WHERE u.JobTitle LIKE jr.Pattern ORDER BY jr.Priority, jr.RuleId) AS r;');

/* Events marked on the dashboard's trends (a rollout wave, a training push, an outage), for chart annotations. */
EXEC (N'CREATE OR ALTER VIEW bi.EventMarkers AS
SELECT Day, YEAR(Day) * 10000 + MONTH(Day) * 100 + DAY(Day) AS DateKey, Label, Kind FROM dash.EventMarker;');

/* Weekly figures by department with any group under the minimum size hidden,
   for a model that must not show individuals (a works council''s condition).

   2.4.12: a row for every department and every week of the data, used or not,
   so whether a row exists never depends on anyone's use. Before, a row existed
   only where someone was active, so a hidden row for a one-person department
   still told whether that person used Copilot that week. Departments with
   fewer enabled people than the minimum are combined as (smaller departments);
   a row is hidden (figures blank) when fewer people than the minimum were
   active that week, none included. */
EXEC (N'CREATE OR ALTER VIEW bi.DepartmentWeek AS
WITH m AS (SELECT ISNULL(TRY_CAST((SELECT Value FROM dash.Setting WHERE Name = ''MinGroupSize'') AS INT), 5) AS MinGroup),
dept AS
(
    SELECT ISNULL(NULLIF(Department, N''''), N''(no department)'') AS Department, SUM(CASE WHEN AccountEnabled = 1 THEN 1 ELSE 0 END) AS Headcount
    FROM bi.DimUser
    GROUP BY ISNULL(NULLIF(Department, N''''), N''(no department)'')
),
grp AS
(
    SELECT dept.Department, CASE WHEN dept.Headcount >= m.MinGroup THEN dept.Department ELSE N''(smaller departments)'' END AS ShownAs
    FROM dept CROSS JOIN m
),
wk AS
(
    SELECT DISTINCT d.WeekEnding
    FROM bi.DimDate AS d
    JOIN (SELECT MIN(DateKey) AS FirstKey, MAX(DateKey) AS LastKey FROM dash.UserDay) AS r ON d.DateKey BETWEEN r.FirstKey AND r.LastKey
),
used AS
(
    SELECT d.WeekEnding, g.ShownAs,
           COUNT(DISTINCT ud.UserKey) AS Active, SUM(CAST(ud.Interactions AS BIGINT)) AS Interactions,
           SUM(CAST(ud.Sessions AS BIGINT)) AS Sessions
    FROM dash.UserDay AS ud
    JOIN bi.DimDate AS d ON d.DateKey = ud.DateKey
    JOIN bi.DimUser AS u ON u.UserKey = ud.UserKey
    JOIN grp AS g ON g.Department = ISNULL(NULLIF(u.Department, N''''), N''(no department)'')
    WHERE ud.Interactions > 0
    GROUP BY d.WeekEnding, g.ShownAs
)
SELECT wk.WeekEnding, s.ShownAs AS Department,
       CASE WHEN ISNULL(x.Active, 0) >= m.MinGroup THEN x.Active END AS ActivePeople,
       CASE WHEN ISNULL(x.Active, 0) >= m.MinGroup THEN x.Interactions END AS Interactions,
       CASE WHEN ISNULL(x.Active, 0) >= m.MinGroup THEN x.Sessions END AS Sessions,
       CAST(CASE WHEN ISNULL(x.Active, 0) < m.MinGroup THEN 1 ELSE 0 END AS BIT) AS Hidden
FROM wk
CROSS JOIN (SELECT DISTINCT ShownAs FROM grp) AS s
CROSS JOIN m
LEFT JOIN used AS x ON x.WeekEnding = wk.WeekEnding AND x.ShownAs = s.ShownAs;');

/* The reporting accounts' role (Install-CopilotAudit.ps1 -ReportingAccount) reads every bi view, these
   included (2.4.12). The installer grants the views that exist when it runs, and the dashboard installer
   runs this script after it, so OrgSecurity, DepartmentWeek and the others here were left out. */
IF DATABASE_PRINCIPAL_ID(N'CopilotReporting') IS NOT NULL
BEGIN
    DECLARE @rv SYSNAME, @rg NVARCHAR(400);
    DECLARE rv CURSOR LOCAL FAST_FORWARD FOR SELECT name FROM sys.views WHERE schema_id = SCHEMA_ID(N'bi');
    OPEN rv;
    FETCH NEXT FROM rv INTO @rv;
    WHILE @@FETCH_STATUS = 0
    BEGIN
        SET @rg = N'GRANT SELECT ON bi.' + QUOTENAME(@rv) + N' TO CopilotReporting;';
        EXEC (@rg);
        FETCH NEXT FROM rv INTO @rv;
    END;
    CLOSE rv;
    DEALLOCATE rv;
END;
```

### C.15 `sql/Create-LicenseEventSchema.sql`

License-event database. License events, their properties, the SKU changes and the license views. 354 lines.

```sql
/*
    License lifecycle events
    ------------------------------------------------------------------------
    Requirement 3 asks for license type, license CHANGE dates, and when access
    was provisioned and revoked. The Graph user object carries current state
    only - assignedPlans gives the date the CURRENT assignment began and
    nothing about one that was removed - so history has to come from the audit
    log instead.

    It is in the same unified audit log the Copilot backfill already reads,
    under the AzureActiveDirectory workload, with the same 180-day retention.
    Invoke-CopilotAuditBackfill.ps1 already takes -Operations and passes it
    straight to the service's operationFilters, so collecting these needs a
    separate database and a different -Operations list, not new ingestion code:

        .\Invoke-CopilotAuditBackfill.ps1 -SqlServer SQLPROD01 `
            -Database CopilotLicenseEvents -TenantId contoso.onmicrosoft.com `
            -Operations 'Change user license','Update user','Add user','Delete user' `
            -StartDate (Get-Date).ToUniversalTime().Date.AddDays(-180) -Finalize

    Then audit.usp_ShredLicenseEvents turns audit.CopilotAudit in THAT database
    into the two tables below.

    ---- Why the properties are a child table, not columns -------------------

    Which property names appear varies by how the change was made. Shredding
    ModifiedProperties into rows rather than guessing at column names survives
    that: whatever Microsoft records is queryable.

    ---- Chunked events -------------------------------------------------------

    Most license changes do not carry their detail in ModifiedProperties at
    all. When the change is too large for one audit record - any user holding
    a few SKUs, because AssignedPlan lists every service plan - Entra splits it
    across several records. Each has empty ModifiedProperties and an
    ExtendedProperties 'additionalDetails' entry of the form

        {"id": "<group>", "seq": "1".."n", "b": "<10,000-character fragment>"}

    and the fragments, joined in seq order, form one JSON document whose
    targetUpdatedProperties holds the real Name/OldValue/NewValue list. On a
    live tenant 775 of 1,042 directory records were chunks, in groups of up to
    seven, and 400 of 422 'Change user license' records had no detail of their
    own. audit.LicenseEventChunk keeps the fragments, because one group's
    records can land in different shredder batches; usp_ShredLicenseEvents
    reassembles each complete group and files its properties against the
    seq = 1 record with Source = 'chunks'.

    A 'b' fragment is longer than 4,000 characters, so it must be read with
    OPENJSON ... WITH (... NVARCHAR(MAX)). JSON_VALUE returns NULL for it.

    The earlier model - a detail-free 'Change user license' paired with an
    'Update user' sibling on InterSystemsId - did not match live data (every
    record in a chunk group shares one InterSystemsId, and no sibling carried
    the lists). The pairing is kept in vLicenseChange as a fallback, but it
    never borrows from a record in the same chunk group.

    Idempotent. Single batch (no GO). Requires SQL Server 2016+.
*/
SET NOCOUNT ON;

IF SCHEMA_ID(N'audit') IS NULL
    EXEC (N'CREATE SCHEMA audit AUTHORIZATION dbo;');

/* One row per audit record. */
IF OBJECT_ID(N'audit.LicenseEvent', N'U') IS NULL
    CREATE TABLE audit.LicenseEvent
    (
        RecordId         VARCHAR(64)     NOT NULL
                             CONSTRAINT PK_LicenseEvent PRIMARY KEY,
        CreatedDateTime  DATETIME2(0)    NOT NULL,   -- UTC
        ActivityDate     DATE            NOT NULL,
        Operation        NVARCHAR(200)   NULL,       -- 'Change user license', 'Update user', ...
        ResultStatus     NVARCHAR(50)    NULL,
        ActorUpn         NVARCHAR(512)   NULL,       -- who made the change
        TargetUpn        NVARCHAR(512)   NULL,       -- whose license changed
        TargetObjectId   NVARCHAR(200)   NULL,
        /*
            Records raised for the same underlying change share this, which is
            what pairs a detail-free 'Change user license' with the
            'Update user' that carries the license lists.
        */
        CorrelationId    NVARCHAR(200)   NULL,
        ShreddedUtc      DATETIME2(0)    NOT NULL
                             CONSTRAINT DF_LicenseEvent_Shredded DEFAULT (SYSUTCDATETIME())
    );

IF OBJECT_ID(N'audit.LicenseEvent', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes
                   WHERE object_id = OBJECT_ID(N'audit.LicenseEvent') AND name = N'IX_LicenseEvent_Target')
    EXEC (N'CREATE INDEX IX_LicenseEvent_Target ON audit.LicenseEvent (TargetUpn, CreatedDateTime)
            INCLUDE (Operation, CorrelationId);');

IF OBJECT_ID(N'audit.LicenseEvent', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes
                   WHERE object_id = OBJECT_ID(N'audit.LicenseEvent') AND name = N'IX_LicenseEvent_Correlation')
    EXEC (N'CREATE INDEX IX_LicenseEvent_Correlation ON audit.LicenseEvent (CorrelationId);');

/* Chunk membership. NULL for an ordinary, self-contained record. */
IF COL_LENGTH(N'audit.LicenseEvent', N'ChunkGroupId') IS NULL
    ALTER TABLE audit.LicenseEvent ADD ChunkGroupId NVARCHAR(100) NULL;
IF COL_LENGTH(N'audit.LicenseEvent', N'ChunkSeq') IS NULL
    ALTER TABLE audit.LicenseEvent ADD ChunkSeq INT NULL;

IF OBJECT_ID(N'audit.LicenseEvent', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes
                   WHERE object_id = OBJECT_ID(N'audit.LicenseEvent') AND name = N'IX_LicenseEvent_Chunk')
    EXEC (N'CREATE INDEX IX_LicenseEvent_Chunk ON audit.LicenseEvent (ChunkGroupId, ChunkSeq);');

/* What audit.usp_RefreshLicenseSkuChange asks after every shred: the events shredded since it last ran (2.5.4). */
IF OBJECT_ID(N'audit.LicenseEvent', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes
                   WHERE object_id = OBJECT_ID(N'audit.LicenseEvent') AND name = N'IX_LicenseEvent_Shredded')
    EXEC (N'CREATE INDEX IX_LicenseEvent_Shredded ON audit.LicenseEvent (ShreddedUtc);');

/* One row per chunk fragment, kept until its group can be reassembled. */
IF OBJECT_ID(N'audit.LicenseEventChunk', N'U') IS NULL
    CREATE TABLE audit.LicenseEventChunk
    (
        ChunkGroupId NVARCHAR(100)  NOT NULL,
        Seq          INT            NOT NULL,
        RecordId     VARCHAR(64)    NOT NULL,
        Body         NVARCHAR(MAX)  NULL,
        CONSTRAINT PK_LicenseEventChunk PRIMARY KEY (ChunkGroupId, Seq)
    );

/*
    One row per ModifiedProperties entry - or, for a chunked event, per entry
    of its reassembled targetUpdatedProperties (Source = 'chunks', Ordinal
    from 10000). Names are whatever the service wrote.
*/
IF OBJECT_ID(N'audit.LicenseEventProperty', N'U') IS NULL
    CREATE TABLE audit.LicenseEventProperty
    (
        RecordId     VARCHAR(64)    NOT NULL,
        Ordinal      INT            NOT NULL,
        [Name]       NVARCHAR(400)  NULL,
        OldValue     NVARCHAR(MAX)  NULL,
        NewValue     NVARCHAR(MAX)  NULL,
        CONSTRAINT PK_LicenseEventProperty PRIMARY KEY (RecordId, Ordinal)
    );

IF COL_LENGTH(N'audit.LicenseEventProperty', N'Source') IS NULL
    ALTER TABLE audit.LicenseEventProperty
        ADD Source VARCHAR(10) NOT NULL CONSTRAINT DF_LicenseEventProperty_Source DEFAULT ('record');

/*
    The SKUs each change added and removed, worked out once and kept.

    audit.vLicenseSkuChangeFromRecords, below, works them out from the events
    and their properties each time it is read. That is right, and at size it
    is slow: the session build and the bi refresh each read every SKU change
    every night, and over 4,000,000 license events each read took 11 minutes in
    the lab, however small the night's load.
    audit.usp_RefreshLicenseSkuChange (Create-LicenseEventProcedure.sql) works
    out the same rows a set at a time and keeps them here: after each shred,
    for the events shredded since it last ran and the records raised with
    them. One row per record, SKU and direction. audit.vLicenseSkuChange reads
    this table, so its readers need no change.
*/
IF OBJECT_ID(N'audit.LicenseSkuChange', N'U') IS NULL
    CREATE TABLE audit.LicenseSkuChange
    (
        RecordId         VARCHAR(64)    NOT NULL,
        Sku              NVARCHAR(200)  NOT NULL,
        Change           NVARCHAR(7)    NOT NULL,   -- 'Added' | 'Removed'
        CreatedDateTime  DATETIME2(0)   NOT NULL,   -- UTC
        ActivityDate     DATE           NOT NULL,
        ActorUpn         NVARCHAR(512)  NULL,
        TargetUpn        NVARCHAR(512)  NULL,       -- the record's own, or the one a record raised with it carries
        FromChunks       INT            NOT NULL,   -- 1: read from a reassembled chunk group
        CONSTRAINT PK_LicenseSkuChange PRIMARY KEY (RecordId, Sku, Change)
    );

/* One row per run of audit.usp_RefreshLicenseSkuChange. No row: the changes have never been worked out here. */
IF OBJECT_ID(N'audit.LicenseSkuChangeRun', N'U') IS NULL
    CREATE TABLE audit.LicenseSkuChangeRun
    (
        RunId       INT IDENTITY(1, 1) NOT NULL CONSTRAINT PK_LicenseSkuChangeRun PRIMARY KEY,
        StartedUtc  DATETIME2(0)  NOT NULL,
        EndedUtc    DATETIME2(0)  NOT NULL,
        SinceUtc    DATETIME2(0)  NULL,       -- events shredded from then on, and the records raised with them; NULL: every event
        Events      INT           NOT NULL,   -- events worked out again
        Changes     INT           NOT NULL    -- SKU changes kept for them
    );

/* ---- Views ------------------------------------------------------------- */

/*
    License changes only: one row per license-bearing property per change.

    A chunked change is one change however many records carried it, so only
    its seq = 1 record appears (the other records are the same event). And
    the 'Update user' that Entra raises alongside every license change, with
    the same detail and CorrelationId, is dropped in favor of the
    'Change user license'. An
    event with no license property of its own may still borrow one from a
    record sharing its CorrelationId, marked FromPairedRecord = 1 - but never
    from its own chunk group, whose records all share that id. Events with no
    license detail from either source are not license changes as far as the
    record can show, and are left out; audit.LicenseEvent still has them.
*/
EXEC (N'
CREATE OR ALTER VIEW audit.vLicenseChange
AS
SELECT e.RecordId,
       e.CreatedDateTime,
       e.ActivityDate,
       e.Operation,
       e.ActorUpn,
       COALESCE(e.TargetUpn, paired.TargetUpn)                  AS TargetUpn,
       e.CorrelationId,
       p.[Name]                                                 AS PropertyName,
       p.OldValue,
       p.NewValue,
       CASE WHEN p.RecordId <> e.RecordId THEN 1 ELSE 0 END     AS FromPairedRecord,
       CASE WHEN p.Source = ''chunks'' THEN 1 ELSE 0 END        AS FromChunks
FROM audit.LicenseEvent AS e
CROSS APPLY (
    SELECT own.RecordId, own.[Name], own.OldValue, own.NewValue, own.Source
    FROM audit.LicenseEventProperty AS own
    WHERE own.RecordId = e.RecordId
      AND (own.[Name] LIKE N''%License%'' OR own.[Name] LIKE N''%Plan%'' OR own.[Name] LIKE N''%Sku%'')
    UNION ALL
    SELECT borrowed.RecordId, borrowed.[Name], borrowed.OldValue, borrowed.NewValue, borrowed.Source
    FROM (
        SELECT TOP (1) WITH TIES p2.RecordId, p2.[Name], p2.OldValue, p2.NewValue, p2.Source
        FROM audit.LicenseEventProperty AS p2
        JOIN audit.LicenseEvent AS e2 ON e2.RecordId = p2.RecordId
        WHERE e.CorrelationId IS NOT NULL
          AND e2.CorrelationId = e.CorrelationId
          AND e2.RecordId <> e.RecordId
          AND (e.ChunkGroupId IS NULL OR e2.ChunkGroupId IS NULL OR e2.ChunkGroupId <> e.ChunkGroupId)
          AND (p2.[Name] LIKE N''%License%'' OR p2.[Name] LIKE N''%Plan%'' OR p2.[Name] LIKE N''%Sku%'')
        ORDER BY e2.CreatedDateTime, e2.RecordId
    ) AS borrowed
    WHERE NOT EXISTS (SELECT 1 FROM audit.LicenseEventProperty AS o
                      WHERE o.RecordId = e.RecordId
                        AND (o.[Name] LIKE N''%License%'' OR o.[Name] LIKE N''%Plan%'' OR o.[Name] LIKE N''%Sku%''))
) AS p
OUTER APPLY (
    SELECT TOP (1) e3.TargetUpn
    FROM audit.LicenseEvent AS e3
    WHERE e.CorrelationId IS NOT NULL AND e3.CorrelationId = e.CorrelationId
      AND e3.TargetUpn IS NOT NULL
    ORDER BY e3.CreatedDateTime
) AS paired
WHERE (e.ChunkSeq IS NULL OR e.ChunkSeq = 1)
  /* Entra raises the same change as a ''Change user license'' and an
     ''Update user'', each with the full detail, under one CorrelationId.
     Report it once, as the license change. */
  AND NOT (e.Operation = N''Update user''
           AND e.CorrelationId IS NOT NULL
           AND EXISTS (SELECT 1 FROM audit.LicenseEvent AS x
                       WHERE x.CorrelationId = e.CorrelationId
                         AND x.Operation = N''Change user license''
                         AND (x.ChunkSeq IS NULL OR x.ChunkSeq = 1)));');

/*
    SKUs added and removed, per change: the AssignedLicense lists diffed.
    This answers "when was this user given Copilot, and when was it taken
    away" directly. Each list entry reads
    [SkuName=Microsoft_365_Copilot, AccountId=..., SkuId=..., DisabledPlans=[...]]
    and the lists arrive either as a JSON array (chunked events) or as a
    string holding one (ModifiedProperties); both parse the same way here.

    This view is the rule, read from the records each time: the reference
    audit.LicenseSkuChange is checked against (tests/Test-SqlIntegration.ps1),
    and what audit.vLicenseSkuChange reads until that table has been worked
    out. It was audit.vLicenseSkuChange itself before the table.
*/
EXEC (N'
CREATE OR ALTER VIEW audit.vLicenseSkuChangeFromRecords
AS
WITH lists AS (
    SELECT c.RecordId, c.CreatedDateTime, c.ActivityDate, c.ActorUpn, c.TargetUpn, c.FromChunks,
           c.OldValue, c.NewValue
    FROM audit.vLicenseChange AS c
    WHERE c.PropertyName = N''AssignedLicense''
),
skus AS (
    SELECT l.RecordId, N''old'' AS Side, s.Sku
    FROM lists AS l
    CROSS APPLY OPENJSON(CASE WHEN ISJSON(l.OldValue) = 1 THEN l.OldValue ELSE N''[]'' END) AS j
    CROSS APPLY (SELECT CHARINDEX(N''SkuName='', j.[value]) AS At) AS a
    CROSS APPLY (SELECT CASE WHEN a.At > 0 THEN SUBSTRING(j.[value], a.At + 8,
                     CHARINDEX(N'','', j.[value] + N'','', a.At + 8) - a.At - 8) END AS Sku) AS s
    WHERE s.Sku IS NOT NULL
    UNION
    SELECT l.RecordId, N''new'', s.Sku
    FROM lists AS l
    CROSS APPLY OPENJSON(CASE WHEN ISJSON(l.NewValue) = 1 THEN l.NewValue ELSE N''[]'' END) AS j
    CROSS APPLY (SELECT CHARINDEX(N''SkuName='', j.[value]) AS At) AS a
    CROSS APPLY (SELECT CASE WHEN a.At > 0 THEN SUBSTRING(j.[value], a.At + 8,
                     CHARINDEX(N'','', j.[value] + N'','', a.At + 8) - a.At - 8) END AS Sku) AS s
    WHERE s.Sku IS NOT NULL
)
SELECT l.RecordId, l.CreatedDateTime, l.ActivityDate, l.ActorUpn, l.TargetUpn, l.FromChunks,
       d.Sku,
       d.Change
FROM lists AS l
CROSS APPLY (
    SELECT n.Sku, N''Added'' AS Change FROM skus AS n
    WHERE n.RecordId = l.RecordId AND n.Side = N''new''
      AND NOT EXISTS (SELECT 1 FROM skus AS o WHERE o.RecordId = l.RecordId AND o.Side = N''old'' AND o.Sku = n.Sku)
    UNION ALL
    SELECT o.Sku, N''Removed'' FROM skus AS o
    WHERE o.RecordId = l.RecordId AND o.Side = N''old''
      AND NOT EXISTS (SELECT 1 FROM skus AS n WHERE n.RecordId = l.RecordId AND n.Side = N''new'' AND n.Sku = o.Sku)
) AS d;');

/*
    What the session build and the bi refresh read: the kept rows.

    In a database that has license events whose changes have never been worked
    out - an upgrade, between this script and Create-LicenseEventProcedure.sql,
    which works them out and then points this view at the table - it still
    reads the records, so a reader is never handed an empty history.
*/
IF EXISTS (SELECT 1 FROM audit.LicenseSkuChangeRun) OR NOT EXISTS (SELECT 1 FROM audit.LicenseEvent)
    EXEC (N'
CREATE OR ALTER VIEW audit.vLicenseSkuChange
AS
SELECT RecordId, CreatedDateTime, ActivityDate, ActorUpn, TargetUpn, FromChunks, Sku, Change
FROM audit.LicenseSkuChange;');
ELSE
    EXEC (N'
CREATE OR ALTER VIEW audit.vLicenseSkuChange
AS
SELECT RecordId, CreatedDateTime, ActivityDate, ActorUpn, TargetUpn, FromChunks, Sku, Change
FROM audit.vLicenseSkuChangeFromRecords;');

/*
    Provisioning and revocation per user. "Revoked" is inferred from the
    license disappearing or the account being deleted, so it is a reading of
    the events rather than a field the service provides - treat it as such.
    A chunked change counts once: only its seq = 1 record is counted.
    Operations match exactly: the service's operation filter for 'Add user'
    also returns 'Add user sponsor', which is not an account being created.
*/
EXEC (N'
CREATE OR ALTER VIEW audit.vLicenseLifecycle
AS
SELECT TargetUpn,
       MIN(CASE WHEN Operation = N''Add user'' THEN CreatedDateTime END)        AS AccountCreatedUtc,
       MAX(CASE WHEN Operation = N''Delete user'' THEN CreatedDateTime END)     AS AccountDeletedUtc,
       MIN(CASE WHEN Operation LIKE N''Change user license%'' THEN CreatedDateTime END) AS FirstLicenseChangeUtc,
       MAX(CASE WHEN Operation LIKE N''Change user license%'' THEN CreatedDateTime END) AS LastLicenseChangeUtc,
       COUNT(CASE WHEN Operation LIKE N''Change user license%'' THEN 1 END)     AS LicenseChangeCount,
       COUNT(*)                                                                AS EventCount
FROM audit.LicenseEvent
WHERE TargetUpn IS NOT NULL
  AND (ChunkSeq IS NULL OR ChunkSeq = 1)
GROUP BY TargetUpn;');
```

### C.16 `sql/Create-LicenseEventProcedure.sql`

License-event database. `audit.usp_ShredLicenseEvents` and `audit.usp_RefreshLicenseSkuChange`. 709 lines.

```sql
/*
    audit.usp_ShredLicenseEvents
    ------------------------------------------------------------------------
    Turns the AzureActiveDirectory records in audit.CopilotAudit into
    audit.LicenseEvent and audit.LicenseEventProperty.

    Run it in the database the license-operations backfill loaded - a
    different database from the Copilot one, because the backfill refuses to
    mix operation sets in a single database.

    Parses each document once into a temp table, the same way
    audit.usp_FlattenCopilotAudit does and for the same reason: a JSON_VALUE
    or OPENJSON call parses the whole document, so reading a dozen fields with
    a dozen calls parses each record a dozen times.

    Re-runnable: rows are keyed on RecordId and MERGEd in place.

    Requires SQL Server 2017+. Run sql/Create-LicenseEventSchema.sql first.
*/
SET NOCOUNT ON;

/*
    audit.usp_RefreshLicenseSkuChange
    ------------------------------------------------------------------------
    Keeps audit.LicenseSkuChange: the SKUs each license change added and
    removed, as audit.vLicenseSkuChangeFromRecords reads them from the records,
    worked out a set at a time and stored. The session build and the bi refresh
    read every one of them every night. Read from the records, over 4,000,000
    license events in the lab, that took 11 minutes each time; worked out here,
    two minutes once, and seconds a night after.

      @All  0 (the default): the events shredded since this last ran, with
            every record of their chunk groups and every record raised with
            any of those (one CorrelationId). A change can take its license
            lists or its user from, or give way to, a record raised with it,
            so a family is always worked out whole.
            1: every event, the table replaced. Also what happens the first
            time, and when more than a tenth of the events were shredded since
            the last run: reading them all once is then the quicker way.

      @SinceShreddedUtc  The caller's own shred began then: its events are
            taken whatever ran since. Another session's run in between, made
            while this shred was still reassembling its chunk groups, would
            otherwise have moved "since this last ran" past them.

    audit.usp_ShredLicenseEvents runs it at the end of every shred, so nothing
    else needs to. "Since this last ran" is read from audit.LicenseSkuChangeRun
    and each event's ShreddedUtc, so a run that failed leaves nothing behind:
    the next one takes its events too. After changing audit.LicenseEvent or its
    properties any other way, run it with @All = 1. While a shred is running,
    or after one was stopped part way (its mark, LicenseShredInProgress, is in
    audit.PipelineSetting), a run asked for by hand works out nothing unless
    @All = 1: that shred's events can still lack their properties.

    The rule, step by step, is the view's:
      * Only a record that is not a later fragment of a chunk group, and not
        an 'Update user' raised with a 'Change user license'.
      * Its license lists are its own AssignedLicense properties when it has
        any license-bearing property; else those of the first record raised
        with it, outside its chunk group, that has one.
      * Its user is its own, else the first one a record raised with it has.
      * A SKU in the new list and not the old was added; in the old and not
        the new, removed.
*/
IF OBJECT_ID(N'audit.usp_RefreshLicenseSkuChange', N'P') IS NULL
    EXEC (N'CREATE PROCEDURE audit.usp_RefreshLicenseSkuChange AS BEGIN SET NOCOUNT ON; END;');
GO

ALTER PROCEDURE audit.usp_RefreshLicenseSkuChange
    @All              BIT          = 0,
    @SinceShreddedUtc DATETIME2(0) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @started DATETIME2(0) = SYSUTCDATETIME(), @t0 DATETIME2(3) = SYSUTCDATETIME(),
            @since DATETIME2(0), @events INT, @changes INT, @msg NVARCHAR(400);
    SET @All = ISNULL(@All, 0);

    /* A shred that is running, or was stopped part way, has events without their properties yet: worked out now,
       their changes would be taken away. The shred runs this itself once it has finished (it says when it began),
       and @All = 1 is taken at its word. Until then the changes stay as the last shred that finished left them. */
    IF @All = 0 AND @SinceShreddedUtc IS NULL
       AND EXISTS (SELECT 1 FROM audit.PipelineSetting WHERE Name = N'LicenseShredInProgress')
    BEGIN
        RAISERROR('License SKU changes: left as they are, because a license shred is running or did not finish. The shred that finishes it works them out.', 0, 1) WITH NOWAIT;
        RETURN;
    END;

    /* Shredded since the last run began, with five minutes to spare: an event stamped as that run started is taken
       again rather than missed. Or since the caller's shred began, when that is earlier. No run yet: every event. */
    SET @since = (SELECT DATEADD(MINUTE, -5, MAX(StartedUtc)) FROM audit.LicenseSkuChangeRun);
    IF @since IS NULL
        SET @All = 1;
    ELSE IF @SinceShreddedUtc < @since
        SET @since = @SinceShreddedUtc;

    /* More than a tenth of the history shredded since (a full shred, a first load): every event is read once instead,
       which is quicker than seeking each family. Both counts are cheap: an index range, and the table's own row count. */
    IF @All = 0
       AND (SELECT COUNT_BIG(*) FROM audit.LicenseEvent AS e WHERE e.ShreddedUtc >= @since) * 10
           > ISNULL((SELECT SUM(p.rows) FROM sys.partitions AS p
                     WHERE p.object_id = OBJECT_ID(N'audit.LicenseEvent') AND p.index_id IN (0, 1)), 0)
        SET @All = 1;

    /* ---- 1. The events to work out, whole families at a time ------------- */

    /* In the database's collation throughout, as the shred's temp tables are. Each has a name of its own (#sku...):
       this is called from other batches, and a statement here would be compiled against a caller's temp table of
       the same name (the demo's own #ev, the first time). */
    DROP TABLE IF EXISTS #skuEvent;
    CREATE TABLE #skuEvent
    (
        RecordId        VARCHAR(64)   COLLATE DATABASE_DEFAULT NOT NULL PRIMARY KEY,
        CreatedDateTime DATETIME2(0)  NOT NULL,
        ActivityDate    DATE          NOT NULL,
        Operation       NVARCHAR(200) COLLATE DATABASE_DEFAULT NULL,
        ActorUpn        NVARCHAR(512) COLLATE DATABASE_DEFAULT NULL,
        TargetUpn       NVARCHAR(512) COLLATE DATABASE_DEFAULT NULL,
        CorrelationId   NVARCHAR(200) COLLATE DATABASE_DEFAULT NULL,
        ChunkGroupId    NVARCHAR(100) COLLATE DATABASE_DEFAULT NULL,
        ChunkSeq        INT           NULL
    );

    IF @All = 1
        INSERT INTO #skuEvent WITH (TABLOCK)
            (RecordId, CreatedDateTime, ActivityDate, Operation, ActorUpn, TargetUpn, CorrelationId, ChunkGroupId, ChunkSeq)
        SELECT e.RecordId, e.CreatedDateTime, e.ActivityDate, e.Operation, e.ActorUpn, e.TargetUpn, e.CorrelationId, e.ChunkGroupId, e.ChunkSeq
        FROM audit.LicenseEvent AS e;
    ELSE
    BEGIN
        INSERT INTO #skuEvent (RecordId, CreatedDateTime, ActivityDate, Operation, ActorUpn, TargetUpn, CorrelationId, ChunkGroupId, ChunkSeq)
        SELECT e.RecordId, e.CreatedDateTime, e.ActivityDate, e.Operation, e.ActorUpn, e.TargetUpn, e.CorrelationId, e.ChunkGroupId, e.ChunkSeq
        FROM audit.LicenseEvent AS e WITH (FORCESEEK)
        WHERE e.ShreddedUtc >= @since;

        /* Every record of their chunk groups: a group's properties are filed against its first record, which an
           earlier run may have shredded. Sought by its index: the night's events are few against the history. */
        INSERT INTO #skuEvent (RecordId, CreatedDateTime, ActivityDate, Operation, ActorUpn, TargetUpn, CorrelationId, ChunkGroupId, ChunkSeq)
        SELECT m.RecordId, m.CreatedDateTime, m.ActivityDate, m.Operation, m.ActorUpn, m.TargetUpn, m.CorrelationId, m.ChunkGroupId, m.ChunkSeq
        FROM (SELECT DISTINCT e.ChunkGroupId FROM #skuEvent AS e WHERE e.ChunkGroupId IS NOT NULL) AS g
        JOIN audit.LicenseEvent AS m WITH (FORCESEEK) ON m.ChunkGroupId = g.ChunkGroupId
        WHERE NOT EXISTS (SELECT 1 FROM #skuEvent AS x WHERE x.RecordId = m.RecordId);

        /* Every record raised with any of those. */
        INSERT INTO #skuEvent (RecordId, CreatedDateTime, ActivityDate, Operation, ActorUpn, TargetUpn, CorrelationId, ChunkGroupId, ChunkSeq)
        SELECT m.RecordId, m.CreatedDateTime, m.ActivityDate, m.Operation, m.ActorUpn, m.TargetUpn, m.CorrelationId, m.ChunkGroupId, m.ChunkSeq
        FROM (SELECT DISTINCT e.CorrelationId FROM #skuEvent AS e WHERE e.CorrelationId IS NOT NULL) AS k
        JOIN audit.LicenseEvent AS m WITH (FORCESEEK) ON m.CorrelationId = k.CorrelationId
        WHERE NOT EXISTS (SELECT 1 FROM #skuEvent AS x WHERE x.RecordId = m.RecordId);
    END;
    SET @events = (SELECT COUNT(*) FROM #skuEvent);

    CREATE INDEX IX_ev_Correlation ON #skuEvent (CorrelationId) INCLUDE (Operation, ChunkSeq, ChunkGroupId, CreatedDateTime, TargetUpn);

    /* ---- 2. Their license-bearing properties ----------------------------- */

    /* Every one marks its record as having license detail of its own; only AssignedLicense carries the lists, so only its
       values are copied (AssignedPlan's run to tens of kilobytes a record). */
    DROP TABLE IF EXISTS #skuProperty;
    CREATE TABLE #skuProperty
    (
        RecordId   VARCHAR(64)   COLLATE DATABASE_DEFAULT NOT NULL,
        Ordinal    INT           NOT NULL,
        IsList     BIT           NOT NULL,
        FromChunks INT           NOT NULL,
        OldValue   NVARCHAR(MAX) COLLATE DATABASE_DEFAULT NULL,
        NewValue   NVARCHAR(MAX) COLLATE DATABASE_DEFAULT NULL
    );
    /* Two statements: every property read once in a scan, or the families' properties sought by their key (left to
       choose, the second scanned every property for a night's few thousand events: two minutes, not seconds). */
    IF @All = 1
        INSERT INTO #skuProperty WITH (TABLOCK) (RecordId, Ordinal, IsList, FromChunks, OldValue, NewValue)
        SELECT p.RecordId, p.Ordinal,
               CASE WHEN p.[Name] = N'AssignedLicense' THEN 1 ELSE 0 END,
               CASE WHEN p.Source = 'chunks' THEN 1 ELSE 0 END,
               CASE WHEN p.[Name] = N'AssignedLicense' THEN p.OldValue END,
               CASE WHEN p.[Name] = N'AssignedLicense' THEN p.NewValue END
        FROM audit.LicenseEventProperty AS p
        WHERE p.[Name] LIKE N'%License%' OR p.[Name] LIKE N'%Plan%' OR p.[Name] LIKE N'%Sku%';
    ELSE
        INSERT INTO #skuProperty (RecordId, Ordinal, IsList, FromChunks, OldValue, NewValue)
        SELECT p.RecordId, p.Ordinal,
               CASE WHEN p.[Name] = N'AssignedLicense' THEN 1 ELSE 0 END,
               CASE WHEN p.Source = 'chunks' THEN 1 ELSE 0 END,
               CASE WHEN p.[Name] = N'AssignedLicense' THEN p.OldValue END,
               CASE WHEN p.[Name] = N'AssignedLicense' THEN p.NewValue END
        FROM #skuEvent AS e
        JOIN audit.LicenseEventProperty AS p WITH (FORCESEEK) ON p.RecordId = e.RecordId
        WHERE p.[Name] LIKE N'%License%' OR p.[Name] LIKE N'%Plan%' OR p.[Name] LIKE N'%Sku%';

    CREATE CLUSTERED INDEX CIX_lp ON #skuProperty (RecordId, Ordinal);

    /* ---- 3. The records that are changes --------------------------------- */

    DROP TABLE IF EXISTS #skuCandidate;
    SELECT e.RecordId, e.CreatedDateTime, e.ActivityDate, e.ActorUpn, e.TargetUpn, e.CorrelationId, e.ChunkGroupId,
           CAST(CASE WHEN EXISTS (SELECT 1 FROM #skuProperty AS p WHERE p.RecordId = e.RecordId) THEN 1 ELSE 0 END AS BIT) AS HasOwn
    INTO #skuCandidate
    FROM #skuEvent AS e
    WHERE (e.ChunkSeq IS NULL OR e.ChunkSeq = 1)
      /* Entra raises one change as a 'Change user license' and an 'Update user': it is the license change. */
      AND NOT (e.Operation = N'Update user'
               AND e.CorrelationId IS NOT NULL
               AND EXISTS (SELECT 1 FROM #skuEvent AS x
                           WHERE x.CorrelationId = e.CorrelationId
                             AND x.Operation = N'Change user license'
                             AND (x.ChunkSeq IS NULL OR x.ChunkSeq = 1)));

    CREATE UNIQUE CLUSTERED INDEX CIX_cand ON #skuCandidate (RecordId);

    /* ---- 4. Each one's license lists -------------------------------------- */

    DROP TABLE IF EXISTS #skuList;
    CREATE TABLE #skuList
    (
        RecordId   VARCHAR(64)   COLLATE DATABASE_DEFAULT NOT NULL,
        FromChunks INT           NOT NULL,
        OldValue   NVARCHAR(MAX) COLLATE DATABASE_DEFAULT NULL,
        NewValue   NVARCHAR(MAX) COLLATE DATABASE_DEFAULT NULL
    );

    /* Its own. */
    INSERT INTO #skuList (RecordId, FromChunks, OldValue, NewValue)
    SELECT c.RecordId, p.FromChunks, p.OldValue, p.NewValue
    FROM #skuCandidate AS c
    JOIN #skuProperty AS p ON p.RecordId = c.RecordId AND p.IsList = 1
    WHERE c.HasOwn = 1;

    /* Or, with no license-bearing property of its own, those of the first record raised with it that has one: never one
       of its own chunk group, whose records all share the CorrelationId. */
    INSERT INTO #skuList (RecordId, FromChunks, OldValue, NewValue)
    SELECT d.RecordId, p.FromChunks, p.OldValue, p.NewValue
    FROM (SELECT c.RecordId, o.RecordId AS DonorId,
                 ROW_NUMBER() OVER (PARTITION BY c.RecordId ORDER BY o.CreatedDateTime, o.RecordId) AS rn
          FROM #skuCandidate AS c
          JOIN #skuEvent AS o ON o.CorrelationId = c.CorrelationId
                       AND o.RecordId <> c.RecordId
                       AND (c.ChunkGroupId IS NULL OR o.ChunkGroupId IS NULL OR o.ChunkGroupId <> c.ChunkGroupId)
          WHERE c.HasOwn = 0
            AND c.CorrelationId IS NOT NULL
            AND EXISTS (SELECT 1 FROM #skuProperty AS h WHERE h.RecordId = o.RecordId)) AS d
    JOIN #skuProperty AS p ON p.RecordId = d.DonorId AND p.IsList = 1
    WHERE d.rn = 1;

    CREATE CLUSTERED INDEX CIX_lists ON #skuList (RecordId);

    /* ---- 5. The SKUs on each side ----------------------------------------- */

    /* Each list entry reads [SkuName=Microsoft_365_Copilot, AccountId=..., SkuId=..., DisabledPlans=[...]]. */
    DROP TABLE IF EXISTS #skuSide;
    CREATE TABLE #skuSide
    (
        RecordId VARCHAR(64)   COLLATE DATABASE_DEFAULT NOT NULL,
        IsNew    BIT           NOT NULL,
        Sku      NVARCHAR(200) COLLATE DATABASE_DEFAULT NOT NULL
    );
    INSERT INTO #skuSide (RecordId, IsNew, Sku)
    SELECT l.RecordId, 0, LEFT(s.Sku, 200)
    FROM #skuList AS l
    CROSS APPLY OPENJSON(CASE WHEN ISJSON(l.OldValue) = 1 THEN l.OldValue ELSE N'[]' END) AS j
    CROSS APPLY (SELECT CHARINDEX(N'SkuName=', j.[value]) AS At) AS a
    CROSS APPLY (SELECT CASE WHEN a.At > 0 THEN SUBSTRING(j.[value], a.At + 8,
                     CHARINDEX(N',', j.[value] + N',', a.At + 8) - a.At - 8) END AS Sku) AS s
    WHERE s.Sku IS NOT NULL
    UNION
    SELECT l.RecordId, 1, LEFT(s.Sku, 200)
    FROM #skuList AS l
    CROSS APPLY OPENJSON(CASE WHEN ISJSON(l.NewValue) = 1 THEN l.NewValue ELSE N'[]' END) AS j
    CROSS APPLY (SELECT CHARINDEX(N'SkuName=', j.[value]) AS At) AS a
    CROSS APPLY (SELECT CASE WHEN a.At > 0 THEN SUBSTRING(j.[value], a.At + 8,
                     CHARINDEX(N',', j.[value] + N',', a.At + 8) - a.At - 8) END AS Sku) AS s
    WHERE s.Sku IS NOT NULL;

    CREATE UNIQUE CLUSTERED INDEX CIX_sku ON #skuSide (RecordId, IsNew, Sku);

    /* ---- 6. What changed, and for whom ------------------------------------ */

    /* The user a record raised with it carries, for a change that carries none: the first, as the view takes it. */
    DROP TABLE IF EXISTS #skuPaired;
    SELECT x.CorrelationId, x.TargetUpn
    INTO #skuPaired
    FROM (SELECT o.CorrelationId, o.TargetUpn,
                 ROW_NUMBER() OVER (PARTITION BY o.CorrelationId ORDER BY o.CreatedDateTime, o.RecordId) AS rn
          FROM #skuEvent AS o
          WHERE o.TargetUpn IS NOT NULL
            AND o.CorrelationId IN (SELECT c.CorrelationId FROM #skuCandidate AS c
                                    WHERE c.TargetUpn IS NULL AND c.CorrelationId IS NOT NULL)) AS x
    WHERE x.rn = 1;

    DROP TABLE IF EXISTS #skuOut;
    SELECT c.RecordId, d.Sku, d.Change, c.CreatedDateTime, c.ActivityDate, c.ActorUpn,
           COALESCE(c.TargetUpn, pr.TargetUpn) AS TargetUpn, f.FromChunks
    INTO #skuOut
    FROM #skuCandidate AS c
    JOIN (SELECT l.RecordId, MAX(l.FromChunks) AS FromChunks FROM #skuList AS l GROUP BY l.RecordId) AS f ON f.RecordId = c.RecordId
    LEFT JOIN #skuPaired AS pr ON pr.CorrelationId = c.CorrelationId
    CROSS APPLY (
        SELECT n.Sku, N'Added' AS Change
        FROM #skuSide AS n
        WHERE n.RecordId = c.RecordId AND n.IsNew = 1
          AND NOT EXISTS (SELECT 1 FROM #skuSide AS o WHERE o.RecordId = c.RecordId AND o.IsNew = 0 AND o.Sku = n.Sku)
        UNION ALL
        SELECT o.Sku, N'Removed'
        FROM #skuSide AS o
        WHERE o.RecordId = c.RecordId AND o.IsNew = 0
          AND NOT EXISTS (SELECT 1 FROM #skuSide AS n WHERE n.RecordId = c.RecordId AND n.IsNew = 1 AND n.Sku = o.Sku)
    ) AS d;

    /* ---- 7. Kept: the families' rows replaced in one transaction ---------- */

    BEGIN TRANSACTION;
    IF @All = 1 AND HAS_PERMS_BY_NAME(N'audit.LicenseSkuChange', N'OBJECT', N'ALTER') = 1
        TRUNCATE TABLE audit.LicenseSkuChange;
    ELSE IF @All = 1
        DELETE FROM audit.LicenseSkuChange;
    ELSE
        DELETE k
        FROM audit.LicenseSkuChange AS k
        JOIN #skuEvent AS e ON e.RecordId = k.RecordId;

    INSERT INTO audit.LicenseSkuChange (RecordId, Sku, Change, CreatedDateTime, ActivityDate, ActorUpn, TargetUpn, FromChunks)
    SELECT o.RecordId, o.Sku, o.Change, o.CreatedDateTime, o.ActivityDate, o.ActorUpn, o.TargetUpn, o.FromChunks
    FROM #skuOut AS o;
    SET @changes = @@ROWCOUNT;

    INSERT INTO audit.LicenseSkuChangeRun (StartedUtc, EndedUtc, SinceUtc, Events, Changes)
    VALUES (@started, SYSUTCDATETIME(), CASE WHEN @All = 1 THEN NULL ELSE @since END, @events, @changes);
    COMMIT TRANSACTION;

    SET @msg = CONCAT(N'License SKU changes: ', FORMAT(@changes, 'N0'), N' kept for ', FORMAT(@events, 'N0'),
                      CASE WHEN @All = 1 THEN N' event(s), every one' ELSE N' event(s) shredded since the last run or raised with one' END,
                      N', in ', FORMAT(DATEDIFF(MILLISECOND, @t0, SYSUTCDATETIME()) / 1000.0, 'N1'), N' s.');
    RAISERROR(@msg, 0, 1) WITH NOWAIT;
END;
GO

IF OBJECT_ID(N'audit.usp_ShredLicenseEvents', N'P') IS NULL
    EXEC (N'CREATE PROCEDURE audit.usp_ShredLicenseEvents AS BEGIN SET NOCOUNT ON; END;');
GO

ALTER PROCEDURE audit.usp_ShredLicenseEvents
    @SinceUtc       DATETIME2(0) = NULL,
    @RebuildAll     BIT          = 0,
    @BatchSize      INT          = 200000,
    @ReportingTimeZone SYSNAME   = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    SET @ReportingTimeZone = COALESCE(@ReportingTimeZone, audit.ReportingTimeZone());   -- the zone Install-CopilotAudit.ps1 -ReportingTimeZone stored

    IF @BatchSize IS NULL OR @BatchSize < 1000 SET @BatchSize = 1000;

    DECLARE @msg NVARCHAR(400), @rows BIGINT = 0, @done BIGINT = 0, @batch INT = 1;

    IF @RebuildAll = 1
        SET @SinceUtc = NULL;

    /* 2.5.4: one shred at a time. Two at once would each take the other's mark (below) for a stopped shred's, and
       the first to finish would take away the mark the other still needs. A second shred waits for the first to
       finish, as a second bi refresh does. The lock is the session's, as a shred commits as it goes: released at
       the end, on an error (the CATCH at the end), or when the session closes. */
    DECLARE @lock INT;
    EXEC @lock = sys.sp_getapplock @Resource = N'audit.usp_ShredLicenseEvents', @LockMode = 'Exclusive', @LockOwner = 'Session', @LockTimeout = 0;
    IF @lock < 0
    BEGIN
        RAISERROR('Another shred of the license events is running: waiting for it to finish.', 0, 1) WITH NOWAIT;
        EXEC @lock = sys.sp_getapplock @Resource = N'audit.usp_ShredLicenseEvents', @LockMode = 'Exclusive', @LockOwner = 'Session', @LockTimeout = -1;
    END;
    IF @lock < 0
        THROW 50020, N'Another shred of the license events is running, and this one could not wait for it to finish.', 1;

    BEGIN TRY
    /* Every event this run writes is stamped at or after this (ShreddedUtc): the SKU changes at the end are told. */
    DECLARE @shredFrom DATETIME2(0) = DATEADD(SECOND, -1, SYSUTCDATETIME());

    /*
        2.5.4: a shred says that it is running (LicenseShredInProgress in audit.PipelineSetting, with the time it
        shreds from; empty: every record) and takes the mark away once it has finished and the SKU changes are
        worked out. Its statements commit one by one, so one that is stopped part way - between a batch's events
        and their properties, or before the chunk groups are reassembled - leaves events without their properties,
        and leaves its mark. One that is still running holds the lock above, so a mark found here is always one of
        a shred that was stopped: this one, whatever it was asked for, shreds what that one was shredding too.
        Until then the SKU changes sessions and bi read are those of the last shred that finished.
    */
    DECLARE @owed NVARCHAR(400), @stopped BIT = 0;
    SELECT @owed = Value, @stopped = 1 FROM audit.PipelineSetting WHERE Name = N'LicenseShredInProgress';
    IF @stopped = 1
    BEGIN
        IF TRY_CONVERT(DATETIME2(0), NULLIF(@owed, N''), 126) IS NULL
            SET @SinceUtc = NULL;
        ELSE IF TRY_CONVERT(DATETIME2(0), @owed, 126) < @SinceUtc
            SET @SinceUtc = TRY_CONVERT(DATETIME2(0), @owed, 126);
        SET @msg = CONCAT(N'An earlier shred did not finish: shredding ',
                          CASE WHEN @SinceUtc IS NULL THEN N'every record' ELSE CONCAT(N'from ', CONVERT(NVARCHAR(16), @SinceUtc, 120), N'Z') END,
                          N', to finish it.');
        RAISERROR(@msg, 0, 1) WITH NOWAIT;
        /* Its time stays: when the shred that was stopped began, which is how long events have been left so. */
        UPDATE audit.PipelineSetting SET Value = ISNULL(CONVERT(NVARCHAR(30), @SinceUtc, 126), N'')
        WHERE Name = N'LicenseShredInProgress';
    END
    ELSE
        INSERT INTO audit.PipelineSetting (Name, Value) VALUES (N'LicenseShredInProgress', ISNULL(CONVERT(NVARCHAR(30), @SinceUtc, 126), N''));

    IF @RebuildAll = 1
    BEGIN
        RAISERROR('Rebuilding the license event tables from scratch.', 0, 1) WITH NOWAIT;
        TRUNCATE TABLE audit.LicenseEventProperty;
        TRUNCATE TABLE audit.LicenseEventChunk;
        TRUNCATE TABLE audit.LicenseEvent;
    END;

    /* Chunk groups this run saw, reassembled once every batch is in. In the database's collation (2.4.12): in
       tempdb's, a database whose collation differs from the server's failed every shred at the first comparison. */
    DROP TABLE IF EXISTS #touched;
    CREATE TABLE #touched (ChunkGroupId NVARCHAR(100) COLLATE DATABASE_DEFAULT NOT NULL PRIMARY KEY);

    DECLARE @cursor VARCHAR(64) = '', @maxId VARCHAR(64);

    WHILE 1 = 1
    BEGIN
        SET @maxId = NULL;

        ;WITH nextBatch AS
        (
            SELECT TOP (@BatchSize) a.RecordId
            FROM audit.CopilotAudit AS a
            WHERE a.RecordId > @cursor
              AND (@SinceUtc IS NULL OR a.CreatedDateTime >= @SinceUtc)
            ORDER BY a.RecordId
        )
        SELECT @maxId = MAX(RecordId) FROM nextBatch;

        IF @maxId IS NULL BREAK;

        DROP TABLE IF EXISTS #evt;

        /* One parse per record: scalars, plus the arrays as fragments. */
        SELECT
            a.RecordId,
            a.CreatedDateTime,
            CAST((a.CreatedDateTime AT TIME ZONE 'UTC') AT TIME ZONE @ReportingTimeZone AS DATE) AS ActivityDate,
            /* Operation is sometimes recorded with a trailing period
               ('Change user license.'), so it is trimmed here rather than
               every query having to allow for both. */
            LEFT(RTRIM(REPLACE(COALESCE(t.Operation, a.Operation), N'.', N'')), 200)  AS Operation,
            LEFT(t.ResultStatus, 50)                                                  AS ResultStatus,
            LEFT(COALESCE(t.JsonUserId, a.UserPrincipalName, a.UserId), 512)          AS ActorUpn,
            LEFT(t.ObjectId, 512)                                                     AS TargetUpnRaw,
            /* Records raised for one change share InterSystemsId. */
            LEFT(COALESCE(t.InterSystemsId, t.IntraSystemId), 200)                    AS CorrelationId,
            t.ModifiedProperties,
            t.[Target],
            t.ExtendedProperties
        INTO #evt
        FROM audit.CopilotAudit AS a
        OUTER APPLY OPENJSON(
            CASE WHEN ISJSON(CAST(DECOMPRESS(a.AuditDataGz) AS NVARCHAR(MAX))) = 1
                 THEN CAST(DECOMPRESS(a.AuditDataGz) AS NVARCHAR(MAX)) ELSE N'{}' END)
            WITH (
                Operation          NVARCHAR(200)  '$.Operation',
                ResultStatus       NVARCHAR(50)   '$.ResultStatus',
                JsonUserId         NVARCHAR(512)  '$.UserId',
                ObjectId           NVARCHAR(512)  '$.ObjectId',
                InterSystemsId     NVARCHAR(200)  '$.InterSystemsId',
                IntraSystemId      NVARCHAR(200)  '$.IntraSystemId',
                ModifiedProperties NVARCHAR(MAX)  '$.ModifiedProperties' AS JSON,
                [Target]           NVARCHAR(MAX)  '$.Target'             AS JSON,
                ExtendedProperties NVARCHAR(MAX)  '$.ExtendedProperties' AS JSON
            ) AS t
        WHERE a.RecordId > @cursor AND a.RecordId <= @maxId
          AND (@SinceUtc IS NULL OR a.CreatedDateTime >= @SinceUtc)
          /* Copilot Studio's authoring records share the load (2.3.0): audit.usp_ShredAgentEvents takes them. */
          AND a.Operation NOT LIKE N'Bot%';

        CREATE CLUSTERED INDEX CIX_evt ON #evt (RecordId);

        /*
            The affected user. ObjectId usually carries it, but when the change
            was made through a path that leaves ObjectId as a GUID, the UPN is
            in Target[] instead - the entry whose ID looks like a UPN.
        */
        DROP TABLE IF EXISTS #tgt;
        SELECT e.RecordId,
               MAX(CASE WHEN tg.ID LIKE N'%@%' THEN LEFT(tg.ID, 512) END) AS TargetUpn
        INTO #tgt
        FROM #evt AS e
        CROSS APPLY OPENJSON(COALESCE(e.[Target], N'[]'))
             WITH (ID NVARCHAR(512) '$.ID') AS tg
        GROUP BY e.RecordId;

        /*
            Chunk fragments: an 'additionalDetails' entry shaped
            {"id", "seq", "b"}. The fragment is read NVARCHAR(MAX) through
            OPENJSON WITH - it is 10,000 characters, and JSON_VALUE would
            return NULL for anything over 4,000.
        */
        DROP TABLE IF EXISTS #chunk;
        SELECT e.RecordId,
               LEFT(c.GroupId, 100)       AS ChunkGroupId,
               TRY_CAST(c.Seq AS INT)     AS Seq,
               c.Body
        INTO #chunk
        FROM #evt AS e
        CROSS APPLY OPENJSON(COALESCE(e.ExtendedProperties, N'[]'))
             WITH ([Name] NVARCHAR(200) '$.Name', [Value] NVARCHAR(MAX) '$.Value') AS x
        CROSS APPLY OPENJSON(CASE WHEN ISJSON(x.[Value]) = 1 THEN x.[Value] ELSE N'{}' END)
             WITH (GroupId NVARCHAR(200) '$.id',
                   Seq     NVARCHAR(20)  '$.seq',
                   Body    NVARCHAR(MAX) '$.b') AS c
        WHERE x.[Name] = N'additionalDetails'
          AND c.GroupId IS NOT NULL AND TRY_CAST(c.Seq AS INT) IS NOT NULL AND c.Body IS NOT NULL;

        /* Both MERGEs below need unique source rows: one fragment per record,
           and one record per (group, seq). */
        ;WITH d AS (SELECT ROW_NUMBER() OVER (PARTITION BY RecordId ORDER BY Seq) AS rn FROM #chunk)
        DELETE FROM d WHERE rn > 1;
        ;WITH d AS (SELECT ROW_NUMBER() OVER (PARTITION BY ChunkGroupId, Seq ORDER BY RecordId) AS rn FROM #chunk)
        DELETE FROM d WHERE rn > 1;

        MERGE audit.LicenseEvent WITH (HOLDLOCK) AS t
        USING (
            SELECT e.RecordId, e.CreatedDateTime, e.ActivityDate, e.Operation, e.ResultStatus,
                   e.ActorUpn,
                   CASE WHEN e.TargetUpnRaw LIKE N'%@%' THEN e.TargetUpnRaw
                        ELSE g.TargetUpn END                          AS TargetUpn,
                   LEFT(e.TargetUpnRaw, 200)                          AS TargetObjectId,
                   e.CorrelationId,
                   ch.ChunkGroupId,
                   ch.Seq                                             AS ChunkSeq
            FROM #evt AS e
            LEFT JOIN #tgt AS g ON g.RecordId = e.RecordId
            LEFT JOIN #chunk AS ch ON ch.RecordId = e.RecordId
        ) AS s
        ON t.RecordId = s.RecordId
        WHEN MATCHED THEN UPDATE SET
            t.CreatedDateTime = s.CreatedDateTime, t.ActivityDate = s.ActivityDate,
            t.Operation = s.Operation, t.ResultStatus = s.ResultStatus,
            t.ActorUpn = s.ActorUpn, t.TargetUpn = s.TargetUpn,
            t.TargetObjectId = s.TargetObjectId, t.CorrelationId = s.CorrelationId,
            t.ChunkGroupId = s.ChunkGroupId, t.ChunkSeq = s.ChunkSeq,
            t.ShreddedUtc = SYSUTCDATETIME()
        WHEN NOT MATCHED BY TARGET THEN INSERT
            (RecordId, CreatedDateTime, ActivityDate, Operation, ResultStatus,
             ActorUpn, TargetUpn, TargetObjectId, CorrelationId, ChunkGroupId, ChunkSeq)
        VALUES
            (s.RecordId, s.CreatedDateTime, s.ActivityDate, s.Operation, s.ResultStatus,
             s.ActorUpn, s.TargetUpn, s.TargetObjectId, s.CorrelationId, s.ChunkGroupId, s.ChunkSeq);

        SET @rows = @@ROWCOUNT;

        MERGE audit.LicenseEventChunk WITH (HOLDLOCK) AS t
        USING #chunk AS s
        ON t.ChunkGroupId = s.ChunkGroupId AND t.Seq = s.Seq
        WHEN MATCHED THEN UPDATE SET t.RecordId = s.RecordId, t.Body = s.Body
        WHEN NOT MATCHED BY TARGET THEN INSERT (ChunkGroupId, Seq, RecordId, Body)
             VALUES (s.ChunkGroupId, s.Seq, s.RecordId, s.Body);

        INSERT INTO #touched (ChunkGroupId)
        SELECT DISTINCT c.ChunkGroupId FROM #chunk AS c
        WHERE NOT EXISTS (SELECT 1 FROM #touched AS t WHERE t.ChunkGroupId = c.ChunkGroupId);

        /* Properties are replaced wholesale for the batch: an audit record is
           immutable, so a re-run must not accumulate duplicates. Reassembled
           chunk properties are left alone here and replaced after the loop. */
        DELETE p
        FROM audit.LicenseEventProperty AS p
        JOIN #evt AS e ON e.RecordId = p.RecordId
        WHERE p.Source = 'record';

        INSERT INTO audit.LicenseEventProperty (RecordId, Ordinal, [Name], OldValue, NewValue)
        SELECT e.RecordId,
               CAST(mp.[key] AS INT),      -- the array index, which WITH cannot give
               LEFT(mpv.[Name], 400),
               mpv.OldValue,
               mpv.NewValue
        FROM #evt AS e
        CROSS APPLY OPENJSON(COALESCE(e.ModifiedProperties, N'[]')) AS mp
        CROSS APPLY OPENJSON(mp.[value])
             WITH ([Name]   NVARCHAR(400) '$.Name',
                   OldValue NVARCHAR(MAX) '$.OldValue',
                   NewValue NVARCHAR(MAX) '$.NewValue') AS mpv;

        SET @done += @rows;
        SET @cursor = @maxId;

        DROP TABLE IF EXISTS #evt;
        DROP TABLE IF EXISTS #tgt;
        DROP TABLE IF EXISTS #chunk;

        SET @msg = CONCAT(N'  batch ', @batch, N': ', FORMAT(@rows, 'N0'), N' event(s), ',
                          FORMAT(@done, 'N0'), N' so far.');
        RAISERROR(@msg, 0, 1) WITH NOWAIT;
        SET @batch += 1;
    END;

    /*
        Reassemble every chunk group this run touched, using all of its stored
        fragments - including ones an earlier run shredded. A group is complete
        when its seqs run 1..n without a gap; an incomplete one (a fragment
        outside the loaded range) is counted and left alone rather than parsed
        from a partial document.
    */
    DROP TABLE IF EXISTS #whole;
    SELECT c.ChunkGroupId,
           MAX(CASE WHEN c.Seq = 1 THEN c.RecordId END)                              AS CarrierRecordId,
           STRING_AGG(CAST(c.Body AS NVARCHAR(MAX)), N'') WITHIN GROUP (ORDER BY c.Seq) AS Payload
    INTO #whole
    FROM audit.LicenseEventChunk AS c
    JOIN #touched AS t ON t.ChunkGroupId = c.ChunkGroupId
    GROUP BY c.ChunkGroupId
    HAVING MIN(c.Seq) = 1 AND MAX(c.Seq) = COUNT(*);

    DECLARE @groups INT = (SELECT COUNT(*) FROM #touched),
            @complete INT = (SELECT COUNT(*) FROM #whole),
            @parsed INT;

    DELETE p
    FROM audit.LicenseEventProperty AS p
    JOIN #whole AS w ON w.CarrierRecordId = p.RecordId
    WHERE p.Source = 'chunks';

    /*
        The joined document's targetUpdatedProperties is itself a JSON string
        holding the Name/OldValue/NewValue list. OldValue and NewValue are
        arrays there rather than strings, so each entry is read key by key:
        OPENJSON's [value] gives an array back as its JSON text, which is the
        same form ModifiedProperties stores.
    */
    INSERT INTO audit.LicenseEventProperty (RecordId, Ordinal, [Name], OldValue, NewValue, Source)
    SELECT w.CarrierRecordId,
           10000 + CAST(item.[key] AS INT),
           LEFT(MAX(CASE WHEN f.[key] = N'Name' THEN f.[value] END), 400),
           MAX(CASE WHEN f.[key] = N'OldValue' THEN f.[value] END),
           MAX(CASE WHEN f.[key] = N'NewValue' THEN f.[value] END),
           'chunks'
    FROM #whole AS w
    CROSS APPLY OPENJSON(CASE WHEN ISJSON(w.Payload) = 1 THEN w.Payload ELSE N'{}' END)
         WITH (Tup NVARCHAR(MAX) '$.targetUpdatedProperties') AS d
    CROSS APPLY OPENJSON(CASE WHEN ISJSON(d.Tup) = 1 THEN d.Tup ELSE N'[]' END) AS item
    CROSS APPLY OPENJSON(item.[value]) AS f
    WHERE w.CarrierRecordId IS NOT NULL
    GROUP BY w.CarrierRecordId, item.[key];

    SET @parsed = @@ROWCOUNT;

    SET @msg = CONCAT(N'Shred complete: ', FORMAT(@done, 'N0'), N' license event(s); ',
                      FORMAT(@complete, 'N0'), N' of ', FORMAT(@groups, 'N0'),
                      N' chunk group(s) reassembled into ', FORMAT(@parsed, 'N0'), N' propert(ies).');
    RAISERROR(@msg, 0, 1) WITH NOWAIT;

    IF @groups > @complete
    BEGIN
        SET @msg = CONCAT(N'  ', FORMAT(@groups - @complete, 'N0'),
                          N' chunk group(s) are missing a fragment - part of the change lies outside the loaded range. They are kept and will complete on a later run.');
        RAISERROR(@msg, 0, 1) WITH NOWAIT;
    END;

    /* The SKUs each change added and removed, kept for the session build and the bi refresh to read: for what was
       shredded since they were last worked out and the records raised with it, or for every event after a full pass. */
    DECLARE @everyEvent BIT = CASE WHEN @SinceUtc IS NULL THEN 1 ELSE 0 END;
    EXEC audit.usp_RefreshLicenseSkuChange @All = @everyEvent, @SinceShreddedUtc = @shredFrom;

    /* Finished: the mark goes. A shred of every record leaves one of its own (LicenseShredCompleteUtc), which
       Install-CopilotAudit.ps1 looks for before it shreds every record again. */
    DELETE FROM audit.PipelineSetting WHERE Name = N'LicenseShredInProgress';
    IF @SinceUtc IS NULL
    BEGIN
        UPDATE audit.PipelineSetting SET Value = CONVERT(NVARCHAR(30), SYSUTCDATETIME(), 126), UpdatedUtc = SYSUTCDATETIME()
        WHERE Name = N'LicenseShredCompleteUtc';
        IF @@ROWCOUNT = 0
            INSERT INTO audit.PipelineSetting (Name, Value) VALUES (N'LicenseShredCompleteUtc', CONVERT(NVARCHAR(30), SYSUTCDATETIME(), 126));
    END;
    EXEC sys.sp_releaseapplock @Resource = N'audit.usp_ShredLicenseEvents', @LockOwner = 'Session';
    END TRY
    BEGIN CATCH
        /* The lock is the session's: a pooled connection would otherwise keep it, and the next shred wait. The mark
           stays: this shred did not finish, and the next one finishes it. */
        IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
        IF APPLOCK_MODE(N'public', N'audit.usp_ShredLicenseEvents', N'Session') <> N'NoLock'
            EXEC sys.sp_releaseapplock @Resource = N'audit.usp_ShredLicenseEvents', @LockOwner = 'Session';
        THROW;
    END CATCH;
END;
GO

/*
    An upgrade: license events shredded before audit.LicenseSkuChange existed have their SKU changes worked out here,
    once, without shredding them again. Then audit.vLicenseSkuChange reads the table; until this point, in such a
    database, it still read the records (Create-LicenseEventSchema.sql).
*/
IF NOT EXISTS (SELECT 1 FROM audit.LicenseSkuChangeRun) AND EXISTS (SELECT 1 FROM audit.LicenseEvent)
BEGIN
    RAISERROR('Working out the SKU changes of the license events already here, once (2.5.4): about two minutes for 4,000,000 events in the lab...', 0, 1) WITH NOWAIT;
    EXEC audit.usp_RefreshLicenseSkuChange @All = 1;
END;
GO

/* On the table from here on, when its rows are there: worked out above, or no event yet. Where that step failed and
   the script went on to this batch all the same (SSMS, and sqlcmd without -b, do), the view stays on the records. */
IF EXISTS (SELECT 1 FROM audit.LicenseSkuChangeRun) OR NOT EXISTS (SELECT 1 FROM audit.LicenseEvent)
    EXEC (N'
CREATE OR ALTER VIEW audit.vLicenseSkuChange
AS
SELECT RecordId, CreatedDateTime, ActivityDate, ActorUpn, TargetUpn, FromChunks, Sku, Change
FROM audit.LicenseSkuChange;');
GO
```

### C.17 `sql/Create-AgentEventProcedure.sql`

License-event database. `audit.AgentEvent` and `audit.usp_ShredAgentEvents`: Copilot Studio authoring records. 112 lines.

```sql
/*
    Copilot audit backfill - agent build and publish activity
    ------------------------------------------------------------------------
    audit.AgentEvent and audit.usp_ShredAgentEvents, in the license-event
    database(s). The nightly license-event load also asks for Copilot Studio's
    authoring operations (BotCreate, BotUpdateOperation-BotPublish,
    BotUpdateOperation-BotShare, BotDelete, BotComponent*...), in the same
    audit searches, so they cost no extra search. This turns each into one row:
    who did what to which agent, and when.

      audit.AgentEvent   one row per authoring record: the action, the agent
                         (BotId, BotSchemaName), the maker, the component,
                         and the update details

    bi.usp_RefreshBi copies them into bi.FactAgentEvent from every license
    database, for the dashboard's Agents > Build and publish and Power BI.

    Operation names: https://learn.microsoft.com/microsoft-copilot-studio/admin-logging-copilot-studio

    Idempotent. Requires SQL Server 2017+ and Create-LicenseEventSchema.sql's
    audit.CopilotAudit.
*/
SET NOCOUNT ON;

IF OBJECT_ID(N'audit.AgentEvent', N'U') IS NULL
    CREATE TABLE audit.AgentEvent
    (
        RecordId         VARCHAR(64)     NOT NULL CONSTRAINT PK_AgentEvent PRIMARY KEY,
        CreatedUtc       DATETIME2(0)    NOT NULL,
        Operation        NVARCHAR(100)   NOT NULL,
        Action           VARCHAR(16)     NOT NULL,   -- Created | Published | Shared | Updated | Deleted | Component
        ActorUpn         NVARCHAR(512)   NULL,
        BotId            NVARCHAR(100)   NULL,
        BotSchemaName    NVARCHAR(200)   NULL,
        ComponentName    NVARCHAR(200)   NULL,       -- BotComponentSchemaName
        ComponentType    NVARCHAR(100)   NULL,
        ResultStatus     NVARCHAR(50)    NULL,
        UpdateDetails    NVARCHAR(4000)  NULL,       -- BotUpdateDetails / BotComponentUpdateDetails, as JSON
        ShreddedUtc      DATETIME2(0)    NOT NULL CONSTRAINT DF_AgentEvent_Shredded DEFAULT (SYSUTCDATETIME()),
        INDEX IX_AgentEvent_Created (CreatedUtc),
        INDEX IX_AgentEvent_Bot (BotSchemaName, CreatedUtc)
    );
GO

IF OBJECT_ID(N'audit.usp_ShredAgentEvents', N'P') IS NULL
    EXEC (N'CREATE PROCEDURE audit.usp_ShredAgentEvents AS RETURN 0;');
GO

ALTER PROCEDURE audit.usp_ShredAgentEvents
    @SinceUtc   DATETIME2(0) = NULL,
    @RebuildAll BIT          = 0
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    IF @RebuildAll = 1
    BEGIN
        TRUNCATE TABLE audit.AgentEvent;
        SET @SinceUtc = NULL;
    END;

    DROP TABLE IF EXISTS #ae;
    SELECT a.RecordId,
           CAST(a.CreatedDateTime AS DATETIME2(0))                                       AS CreatedUtc,
           LEFT(COALESCE(t.Operation, a.Operation), 100)                                 AS Operation,
           LEFT(COALESCE(t.UserId, a.UserPrincipalName, a.UserId), 512)                  AS ActorUpn,
           LEFT(t.BotId, 100)                                                            AS BotId,
           LEFT(t.BotSchemaName, 200)                                                    AS BotSchemaName,
           LEFT(t.BotComponentSchemaName, 200)                                           AS ComponentName,
           LEFT(t.BotComponentType, 100)                                                 AS ComponentType,
           LEFT(t.ResultStatus, 50)                                                      AS ResultStatus,
           LEFT(COALESCE(t.BotUpdateDetails, t.BotComponentUpdateDetails), 4000)          AS UpdateDetails
    INTO #ae
    FROM audit.CopilotAudit AS a
    CROSS APPLY (SELECT CAST(DECOMPRESS(a.AuditDataGz) AS NVARCHAR(MAX)) AS Doc) AS d
    OUTER APPLY OPENJSON(CASE WHEN ISJSON(d.Doc) = 1 THEN d.Doc ELSE N'{}' END)
        WITH (
            Operation                 NVARCHAR(100)  '$.Operation',
            UserId                    NVARCHAR(512)  '$.UserId',
            ResultStatus              NVARCHAR(50)   '$.ResultStatus',
            BotId                     NVARCHAR(100)  '$.BotId',
            BotSchemaName             NVARCHAR(200)  '$.BotSchemaName',
            BotComponentSchemaName    NVARCHAR(200)  '$.BotComponentSchemaName',
            BotComponentType          NVARCHAR(100)  '$.BotComponentType',
            BotUpdateDetails          NVARCHAR(MAX)  '$.BotUpdateDetails'          AS JSON,
            BotComponentUpdateDetails NVARCHAR(MAX)  '$.BotComponentUpdateDetails' AS JSON
        ) AS t
    WHERE a.Operation LIKE N'Bot%'
      AND (@SinceUtc IS NULL OR a.CreatedDateTime >= @SinceUtc);

    MERGE audit.AgentEvent WITH (HOLDLOCK) AS t
    USING (
        SELECT e.*,
               CASE WHEN e.Operation = N'BotCreate' THEN 'Created'
                    WHEN e.Operation LIKE N'%BotPublish%' THEN 'Published'
                    WHEN e.Operation LIKE N'%BotShare%' THEN 'Shared'
                    WHEN e.Operation IN (N'BotDelete', N'BotDeleteCleanup') THEN 'Deleted'
                    WHEN e.Operation LIKE N'BotComponent%' THEN 'Component'
                    ELSE 'Updated' END AS Action
        FROM #ae AS e
    ) AS s
    ON t.RecordId = s.RecordId
    WHEN MATCHED THEN UPDATE SET CreatedUtc = s.CreatedUtc, Operation = s.Operation, Action = s.Action, ActorUpn = s.ActorUpn,
        BotId = s.BotId, BotSchemaName = s.BotSchemaName, ComponentName = s.ComponentName, ComponentType = s.ComponentType,
        ResultStatus = s.ResultStatus, UpdateDetails = s.UpdateDetails, ShreddedUtc = SYSUTCDATETIME()
    WHEN NOT MATCHED THEN INSERT (RecordId, CreatedUtc, Operation, Action, ActorUpn, BotId, BotSchemaName, ComponentName, ComponentType, ResultStatus, UpdateDetails)
        VALUES (s.RecordId, s.CreatedUtc, s.Operation, s.Action, s.ActorUpn, s.BotId, s.BotSchemaName, s.ComponentName, s.ComponentType, s.ResultStatus, s.UpdateDetails);

    DECLARE @msg NVARCHAR(200) = CONCAT(N'Agent events: ', FORMAT(@@ROWCOUNT, 'N0'), N' record(s) shredded.');
    RAISERROR(@msg, 0, 1) WITH NOWAIT;
END;
GO
```

### C.18 `sql/Finalize-CopilotAudit.sql`

Both databases. Run by the backfill's `-Finalize`, not by the installer: builds `audit.CopilotAudit` and `audit.CopilotInteraction` from staging. 290 lines.

```sql
/*
    Copilot audit backfill - finalize
    ------------------------------------------------------------------------
    1. Refuses to run unless every window is Loaded and the range has no gaps.
    2. Builds audit.CopilotAudit (clustered columnstore) from each Loaded
       window's FINAL attempt only - rows from interrupted attempts are ignored.
    3. Removes duplicates by RecordId (records sitting exactly on a window
       boundary can be returned by both neighbouring searches).
    4. Builds audit.CopilotInteraction: a slim, shredded table for adoption
       reporting (Power BI etc.), plus audit.vCopilotDailyUsage.
    5. Creates audit.vCopilotAuditRecord in the Purview export's shape
       (RecordId, CreationDate, RecordType, Operation, UserId, AuditData,
       AssociatedAdminUnits, AssociatedAdminUnitsNames) so SQL written against
       the portal CSV import can point at it unchanged.
       AssociatedAdminUnitsNames is always NULL: the Graph auditLogRecord
       resource exposes one administrativeUnits collection, of IDs only, with
       no display names. The portal export resolves those IDs itself. The
       column is present so the shape matches; resolve the names from Entra
       if you need them.

    ActivityDate uses @ReportingTimeZone (below) for day boundaries; all
    other timestamps stay UTC.

    Re-runnable: both tables are rebuilt from staging every time, as long as
    staging still holds every row the windows loaded. Once it has been dropped
    (as the end of this script suggests) or purged, finalize refuses: it would
    rebuild audit.CopilotAudit from what is left, which is nothing (2.4.12).
    Single batch (no GO), so an error stops everything in SSMS too.
    Requires SQL Server 2017+ (NVARCHAR(MAX) in a columnstore).
    Expect tens of minutes for tens of millions of rows; it is a one-off.
*/
SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @ReportingTimeZone SYSNAME = N'Eastern Standard Time';   -- Windows zone name; handles EST/EDT
-- The zone Install-CopilotAudit.ps1 -ReportingTimeZone stored, when there is one.
IF OBJECT_ID(N'audit.ReportingTimeZone', N'FN') IS NOT NULL
    EXEC sys.sp_executesql N'SET @z = audit.ReportingTimeZone();', N'@z SYSNAME OUTPUT', @z = @ReportingTimeZone OUTPUT;
DECLARE @msg NVARCHAR(400);

/* ---- 1. Guard ---------------------------------------------------------- */

IF (SELECT compatibility_level FROM sys.databases WHERE name = DB_NAME()) < 130
BEGIN
    RAISERROR('Finalize needs database compatibility level 130+ (for OPENJSON). ALTER DATABASE ... SET COMPATIBILITY_LEVEL = 130 or higher.', 16, 1);
    RETURN;
END;

DECLARE @rangeStart DATETIME2(0), @rangeEnd DATETIME2(0), @completed DATETIME2(0);
SELECT @rangeStart = RangeStartUtc, @rangeEnd = RangeEndUtc, @completed = CompletedUtc
FROM audit.CopilotBackfillRange WHERE Id = 1;

IF @rangeStart IS NULL
BEGIN
    RAISERROR('Finalize refused: no backfill has been started in this database.', 16, 1);
    RETURN;
END;
IF @completed IS NULL
BEGIN
    RAISERROR('Finalize refused: the backfill has not completed. Re-run Invoke-CopilotAuditBackfill.ps1 to finish it.', 16, 1);
    RETURN;
END;

/* Finalize rebuilds from the backfill's staging table only. Once incremental
   loads have been appended, a rebuild would silently drop every day they added. */
IF OBJECT_ID(N'audit.CopilotLoad', N'U') IS NOT NULL
   AND EXISTS (SELECT 1 FROM audit.CopilotLoad WHERE MergedUtc IS NOT NULL)
BEGIN
    RAISERROR('Finalize refused: incremental loads have been merged into audit.CopilotAudit, and rebuilding from the backfill''s staging table would drop them. The tables are already final.', 16, 1);
    RETURN;
END;

/* The same for CSV imports (tools/Import-CopilotAuditCsv.ps1). Nested, not
   ANDed: those tables exist only where the importer has run, and a statement
   naming a missing table fails when it runs, whatever its other conditions. */
IF OBJECT_ID(N'audit.CopilotCsvImport', N'U') IS NOT NULL
BEGIN
    IF EXISTS (SELECT 1 FROM audit.CopilotCsvImport WHERE MergedUtc IS NOT NULL)
    BEGIN
        RAISERROR('Finalize refused: CSV imports have been merged into audit.CopilotAudit, and rebuilding from the backfill''s staging table would drop them. The tables are already final.', 16, 1);
        RETURN;
    END;
END;

IF OBJECT_ID(N'audit.fnShredCopilotInteraction', N'IF') IS NULL
BEGIN
    RAISERROR('Finalize refused: audit.fnShredCopilotInteraction is missing. Run sql/Create-CopilotAuditSchema.sql first (Invoke-CopilotAuditBackfill.ps1 does).', 16, 1);
    RETURN;
END;

DECLARE @unfinished INT =
    (SELECT COUNT(*) FROM audit.CopilotBackfillWindow WHERE Status NOT IN ('Loaded', 'Split'));
IF @unfinished > 0
BEGIN
    RAISERROR('Finalize refused: %d window(s) are not Loaded. Re-run the backfill to finish them first.', 16, 1, @unfinished);
    RETURN;
END;

DECLARE @gaps INT;
WITH w AS
(
    SELECT StartUtc, LAG(EndUtc) OVER (ORDER BY StartUtc) AS PrevEnd
    FROM audit.CopilotBackfillWindow
    WHERE Status = 'Loaded'
)
SELECT @gaps = COUNT(*) FROM w WHERE PrevEnd IS NOT NULL AND PrevEnd <> StartUtc;

-- ...and the loaded windows must reach both ends of the requested range.
IF ISNULL((SELECT MIN(StartUtc) FROM audit.CopilotBackfillWindow WHERE Status = 'Loaded'), '19000101') <> @rangeStart
   OR ISNULL((SELECT MAX(EndUtc) FROM audit.CopilotBackfillWindow WHERE Status = 'Loaded'), '19000101') <> @rangeEnd
    SET @gaps += 1;

IF @gaps > 0
BEGIN
    RAISERROR('Finalize refused: the Loaded windows have %d gap(s)/overlap(s). Inspect audit.CopilotBackfillWindow.', 16, 1, @gaps);
    RETURN;
END;

RAISERROR('Coverage verified: all windows loaded, no gaps.', 0, 1) WITH NOWAIT;

/* Staging must still hold what the windows loaded (2.4.12). The guards above read the
   window table only, and staging is dropped once finalize has run (the advice at the
   end of this script) or purged by -Task PurgeJson; the backfill creates it again,
   empty, on its next run. A finalize then dropped audit.CopilotAudit and rebuilt it
   from nothing, and said "Coverage verified". */
DECLARE @expected BIGINT, @unknown INT, @staged BIGINT, @existing BIGINT;
SELECT @expected = SUM(ISNULL(RowsLoaded, 0)), @unknown = COUNT(CASE WHEN RowsLoaded IS NULL THEN 1 END)
FROM audit.CopilotBackfillWindow WHERE Status = 'Loaded';
SELECT @staged = COUNT_BIG(*)
FROM audit.CopilotAuditStage AS s
JOIN audit.CopilotBackfillWindow AS w ON w.WindowId = s.WindowId AND w.Status = 'Loaded' AND w.Attempts = s.Attempt;
/* Counted, not read from sys.partitions: after a reorganize, a columnstore's row groups that were compressed are
   counted there twice until the tuple mover clears them. Dynamic, for a database where the table is not there yet. */
SET @existing = 0;
IF OBJECT_ID(N'audit.CopilotAudit', N'U') IS NOT NULL
    EXEC sys.sp_executesql N'SELECT @n = COUNT_BIG(*) FROM audit.CopilotAudit;', N'@n BIGINT OUTPUT', @n = @existing OUTPUT;

IF (@unknown = 0 AND @staged < @expected) OR (@staged = 0 AND @expected > 0) OR @existing > @staged
BEGIN
    RAISERROR('Finalize refused: the staging table holds %I64d of the %I64d rows the windows loaded, and audit.CopilotAudit holds %I64d. Staging was dropped or purged after an earlier finalize, and rebuilding from it would empty audit.CopilotAudit. The tables are final already: nothing needs doing.', 16, 1, @staged, @expected, @existing);
    RETURN;
END;

/* ---- 2. Final attempt of every Loaded window -> columnstore ------------ */

IF OBJECT_ID(N'audit.CopilotInteraction', N'U') IS NOT NULL DROP TABLE audit.CopilotInteraction;
IF OBJECT_ID(N'audit.CopilotAudit', N'U') IS NOT NULL DROP TABLE audit.CopilotAudit;

CREATE TABLE audit.CopilotAudit
(
    RecordId            VARCHAR(64)     NOT NULL,
    CreatedDateTime     DATETIME2(7)    NOT NULL,
    RecordType          VARCHAR(128)    NULL,
    Operation           VARCHAR(128)    NULL,
    OrganizationId      VARCHAR(64)     NULL,
    UserType            VARCHAR(64)     NULL,
    UserId              NVARCHAR(512)   NULL,
    UserPrincipalName   NVARCHAR(512)   NULL,
    Service             VARCHAR(128)    NULL,
    ObjectId            NVARCHAR(4000)  NULL,
    ClientIp            VARCHAR(128)    NULL,
    AdministrativeUnits NVARCHAR(4000)  NULL,
    AuditDataGz         VARBINARY(MAX)  NULL,       -- CAST(DECOMPRESS(AuditDataGz) AS NVARCHAR(MAX)); or use audit.vCopilotAuditRecord
    WindowId            BIGINT          NOT NULL,   -- lineage back to the control table
    LoadId              INT             NULL,       -- NULL: the backfill. Otherwise audit.CopilotLoad, and
                                                    -- WindowId is then a row of audit.CopilotLoadWindow
    CsvImportId         INT             NULL,       -- set: the row came from a CSV import (audit.CopilotCsvImport),
                                                    -- and WindowId is then its audit.CopilotCsvFile
    INDEX CCI_CopilotAudit CLUSTERED COLUMNSTORE
);

RAISERROR('Loading audit.CopilotAudit from staging (final attempts only)...', 0, 1) WITH NOWAIT;

INSERT INTO audit.CopilotAudit WITH (TABLOCK)
(
    RecordId, CreatedDateTime, RecordType, Operation, OrganizationId, UserType, UserId,
    UserPrincipalName, Service, ObjectId, ClientIp, AdministrativeUnits, AuditDataGz, WindowId
)
SELECT s.RecordId, s.CreatedDateTime, s.RecordType, s.Operation, s.OrganizationId, s.UserType, s.UserId,
       s.UserPrincipalName, s.Service, s.ObjectId, s.ClientIp, s.AdministrativeUnits, s.AuditDataGz, s.WindowId
FROM audit.CopilotAuditStage AS s
JOIN audit.CopilotBackfillWindow AS w
  ON  w.WindowId = s.WindowId
  AND w.Status   = 'Loaded'
  AND w.Attempts = s.Attempt;

DECLARE @loaded BIGINT = @@ROWCOUNT;
SET @msg = CONCAT(N'Loaded ', FORMAT(@loaded, 'N0'), N' rows.');
RAISERROR(@msg, 0, 1) WITH NOWAIT;

/* ---- 3. Boundary duplicates (the columnstore reads only RecordId) ------- */

WITH d AS
(
    SELECT ROW_NUMBER() OVER (PARTITION BY RecordId ORDER BY WindowId DESC) AS rn
    FROM audit.CopilotAudit
    WHERE RecordId IN (SELECT RecordId FROM audit.CopilotAudit GROUP BY RecordId HAVING COUNT(*) > 1)
)
DELETE FROM d WHERE rn > 1;

DECLARE @dupes BIGINT = @@ROWCOUNT;
SET @msg = CONCAT(N'Removed ', FORMAT(@dupes, 'N0'), N' duplicate row(s).');
RAISERROR(@msg, 0, 1) WITH NOWAIT;

ALTER INDEX CCI_CopilotAudit ON audit.CopilotAudit REORGANIZE WITH (COMPRESS_ALL_ROW_GROUPS = ON);

/* ---- 4. Reporting table -------------------------------------------------- */

CREATE TABLE audit.CopilotInteraction
(
    RecordId              VARCHAR(64)    NOT NULL,
    CreatedDateTime       DATETIME2(0)   NOT NULL,   -- UTC
    ActivityDate          DATE           NOT NULL,   -- calendar day in @ReportingTimeZone
    UserPrincipalName     NVARCHAR(512)  NULL,
    AppHost               NVARCHAR(100)  NULL,   -- Word, Teams, BizChat, Outlook ...
    AppIdentity           NVARCHAR(200)  NULL,   -- e.g. Copilot.MicrosoftCopilot.BizChat, Copilot.Studio.<agent>
    ClientRegion          NVARCHAR(20)   NULL,
    ModelName             NVARCHAR(200)  NULL,
    ModelProvider         NVARCHAR(200)  NULL,
    ThreadId              NVARCHAR(400)  NULL,
    PromptCount           INT            NULL,
    AccessedResourceCount INT            NULL,
    UsedWebSearch         BIT            NULL,
    INDEX CCI_CopilotInteraction CLUSTERED COLUMNSTORE
);

RAISERROR('Shredding AuditData into audit.CopilotInteraction...', 0, 1) WITH NOWAIT;

/* audit.fnShredCopilotInteraction (Create-CopilotAuditSchema.sql) holds the
   JSON paths; incremental loads and CSV imports shred through it too. */

INSERT INTO audit.CopilotInteraction WITH (TABLOCK)
(
    RecordId, CreatedDateTime, ActivityDate, UserPrincipalName, AppHost, AppIdentity, ClientRegion,
    ModelName, ModelProvider, ThreadId, PromptCount, AccessedResourceCount, UsedWebSearch
)
SELECT
    a.RecordId,
    CAST(a.CreatedDateTime AS DATETIME2(0)),
    CAST((a.CreatedDateTime AT TIME ZONE 'UTC') AT TIME ZONE @ReportingTimeZone AS DATE),
    COALESCE(a.UserPrincipalName, s.JsonUserId),
    s.AppHost, s.AppIdentity, s.ClientRegion, s.ModelName, s.ModelProvider, s.ThreadId,
    s.PromptCount, s.AccessedResourceCount, s.UsedWebSearch
FROM audit.CopilotAudit AS a
CROSS APPLY audit.fnShredCopilotInteraction(a.AuditDataGz) AS s;

EXEC (N'
CREATE OR ALTER VIEW audit.vCopilotDailyUsage
AS
SELECT ActivityDate,
       AppHost,
       COUNT(*)                          AS Interactions,
       COUNT(DISTINCT UserPrincipalName) AS ActiveUsers,
       SUM(PromptCount)                  AS Prompts,
       SUM(CAST(UsedWebSearch AS INT))   AS WebGroundedInteractions
FROM audit.CopilotInteraction
GROUP BY ActivityDate, AppHost;');

/* ---- 5. Purview-export-shaped view ------------------------------------------ */

EXEC (N'
CREATE OR ALTER VIEW audit.vCopilotAuditRecord
AS
SELECT RecordId,
       CreatedDateTime                                            AS CreationDate,          -- UTC
       UPPER(LEFT(RecordType, 1)) + SUBSTRING(RecordType, 2, 127) AS RecordType,            -- copilotInteraction -> CopilotInteraction
       Operation,
       COALESCE(UserPrincipalName, UserId)                        AS UserId,
       CAST(DECOMPRESS(AuditDataGz) AS NVARCHAR(MAX))             AS AuditData,
       AdministrativeUnits                                        AS AssociatedAdminUnits,  -- semicolon-separated IDs
       CAST(NULL AS NVARCHAR(MAX))                                AS AssociatedAdminUnitsNames
FROM audit.CopilotAudit;');

/* ---- Summary --------------------------------------------------------------- */

SELECT
    (SELECT COUNT_BIG(*) FROM audit.CopilotAudit)                        AS FinalRows,
    @dupes                                                               AS DuplicatesRemoved,
    (SELECT MIN(CreatedDateTime) FROM audit.CopilotAudit)                AS OldestRecordUtc,
    (SELECT MAX(CreatedDateTime) FROM audit.CopilotAudit)                AS NewestRecordUtc,
    (SELECT COUNT(DISTINCT UserPrincipalName) FROM audit.CopilotInteraction) AS DistinctUsers,
    (SELECT COUNT_BIG(*) FROM audit.CopilotAuditStage)                   AS StagingRows;

/* The mark the backfill reads before finalizing again (2.4.12): finalized, so a re-run leaves the tables alone. */
MERGE audit.PipelineSetting AS t
USING (SELECT N'FinalizedUtc' AS Name, CONVERT(NVARCHAR(30), SYSUTCDATETIME(), 126) AS Value) AS s ON t.Name = s.Name
WHEN MATCHED THEN UPDATE SET Value = s.Value, UpdatedUtc = SYSUTCDATETIME(), UpdatedBy = SUSER_SNAME()
WHEN NOT MATCHED THEN INSERT (Name, Value) VALUES (s.Name, s.Value);

RAISERROR('Finalize complete. Once verified, reclaim space with: DROP TABLE audit.CopilotAuditStage;', 0, 1) WITH NOWAIT;
```

### C.19 `sql/Create-CopilotCsvImport.sql`

Run by `tools/Import-CopilotAuditCsv.ps1`, not by the installer: the CSV import tables and `audit.usp_MergeCopilotCsv`. 400 lines.

```sql
/*
    Copilot audit - CSV import
    ------------------------------------------------------------------------
    Loads audit log exports from the Purview portal (CSV) into the same tables
    the Graph backfill fills, so everything downstream - the flattened table,
    sessions, the bi schema - works unchanged. tools/Import-CopilotAuditCsv.ps1
    runs this script, bulk-loads a folder of CSV files into
    audit.CopilotCsvStage, then calls audit.usp_MergeCopilotCsv.

      audit.CopilotCsvImport   one row per run of the importer
      audit.CopilotCsvFile     one row per file loaded, with its SHA-256, so a
                               file already imported is skipped next time
      audit.CopilotCsvReject   rows that could not be loaded, and why
      audit.CopilotCsvStage    the rows of the import being merged (a heap,
                               emptied after each merge)

    Exports can overlap in range and repeat each other, and they can overlap
    what the Graph backfill or -Incremental already loaded: the merge keeps one
    row per RecordId, and a record audit.CopilotAudit already holds wins.

    A database can start from CSV files alone. Then this script creates
    audit.CopilotAudit, audit.CopilotInteraction and their views - the same
    objects Finalize-CopilotAudit.sql builds - and the merge records the range
    the exports cover in audit.CopilotBackfillRange (Source = 'CSV'), so
    Invoke-CopilotAuditBackfill.ps1 -Incremental can carry on from its end.

    Idempotent. Requires SQL Server 2017+ and sql/Create-CopilotAuditSchema.sql
    (for audit.fnShredCopilotInteraction); the importer runs both.
*/
SET NOCOUNT ON;

IF SCHEMA_ID(N'audit') IS NULL
    EXEC (N'CREATE SCHEMA audit AUTHORIZATION dbo;');

IF OBJECT_ID(N'audit.CopilotCsvImport', N'U') IS NULL
    CREATE TABLE audit.CopilotCsvImport
    (
        ImportId            INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_CopilotCsvImport PRIMARY KEY,
        FolderPath          NVARCHAR(1024)  NOT NULL,
        Operations          NVARCHAR(400)   NOT NULL,   -- the database's operation set, as the backfill records it
        StartedUtc          DATETIME2(0)    NOT NULL CONSTRAINT DF_CopilotCsvImport_Started DEFAULT (SYSUTCDATETIME()),
        MergedUtc           DATETIME2(0)    NULL,
        FilesLoaded         INT             NULL,
        RowsRead            BIGINT          NULL,       -- data rows in the files
        RowsRejected        BIGINT          NULL,       -- see audit.CopilotCsvReject
        RowsOtherOperation  BIGINT          NULL,       -- valid, but not an operation this database holds
        RowsStaged          BIGINT          NULL,
        RowsDuplicate       BIGINT          NULL,       -- the same RecordId more than once across the files
        RowsAlreadyLoaded   BIGINT          NULL,       -- already in audit.CopilotAudit
        RowsAdded           BIGINT          NULL,
        EarliestAddedUtc    DATETIME2(0)    NULL,
        LatestAddedUtc      DATETIME2(0)    NULL,
        CoveredFromUtc      DATETIME2(0)    NULL,       -- the search range the exports were made for, if given
        CoveredToUtc        DATETIME2(0)    NULL,
        DerivedRefreshedUtc DATETIME2(0)    NULL        -- the derived tables brought up to date with it; see below
    );

/* When the derived tables were brought up to date with an import (2.4.12).
   The import is marked merged before the importer refreshes the flattened
   table, sessions, bi and the dashboard, and nothing recorded whether that
   refresh finished: one that failed or was skipped was never made up, as a
   second run skips the files merged and the nightly refreshes only its own
   days. NULL on a merged import that added rows is a refresh still owed; the
   importer and -Incremental make it up from its earliest record. Imports
   merged before the column existed are taken as refreshed. */
IF COL_LENGTH(N'audit.CopilotCsvImport', N'DerivedRefreshedUtc') IS NULL
BEGIN
    ALTER TABLE audit.CopilotCsvImport ADD DerivedRefreshedUtc DATETIME2(0) NULL;
    EXEC (N'UPDATE audit.CopilotCsvImport SET DerivedRefreshedUtc = MergedUtc WHERE MergedUtc IS NOT NULL;');
END;

IF OBJECT_ID(N'audit.CopilotCsvFile', N'U') IS NULL
    CREATE TABLE audit.CopilotCsvFile
    (
        FileId              INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_CopilotCsvFile PRIMARY KEY,
        ImportId            INT             NOT NULL,
        FileName            NVARCHAR(260)   NOT NULL,
        FullPath            NVARCHAR(1024)  NOT NULL,
        Sha256              CHAR(64)        NOT NULL,
        SizeBytes           BIGINT          NOT NULL,
        LastWriteUtc        DATETIME2(0)    NULL,
        RowsRead            BIGINT          NULL,
        RowsRejected        BIGINT          NULL,
        RowsOtherOperation  BIGINT          NULL,
        RowsStaged          BIGINT          NULL,
        EarliestRecordUtc   DATETIME2(0)    NULL,
        LatestRecordUtc     DATETIME2(0)    NULL,
        LoadedUtc           DATETIME2(0)    NOT NULL CONSTRAINT DF_CopilotCsvFile_Loaded DEFAULT (SYSUTCDATETIME()),
        INDEX IX_CopilotCsvFile_Sha256 (Sha256)
    );

IF OBJECT_ID(N'audit.CopilotCsvReject', N'U') IS NULL
    CREATE TABLE audit.CopilotCsvReject
    (
        FileId              INT             NOT NULL,
        LineNumber          BIGINT          NOT NULL,   -- where the row starts in the file
        RecordId            VARCHAR(64)     NULL,
        Reason              NVARCHAR(400)   NOT NULL,
        INDEX IX_CopilotCsvReject_File CLUSTERED (FileId, LineNumber)
    );

/* The shape of audit.CopilotAuditStage, with the file in place of the window. */
IF OBJECT_ID(N'audit.CopilotCsvStage', N'U') IS NULL
    CREATE TABLE audit.CopilotCsvStage
    (
        FileId              INT             NOT NULL,
        LineNumber          BIGINT          NOT NULL,
        RecordId            VARCHAR(64)     NOT NULL,
        CreatedDateTime     DATETIME2(7)    NOT NULL,
        RecordType          VARCHAR(128)    NULL,
        Operation           VARCHAR(128)    NULL,
        OrganizationId      VARCHAR(64)     NULL,
        UserType            VARCHAR(64)     NULL,
        UserId              NVARCHAR(512)   NULL,
        UserPrincipalName   NVARCHAR(512)   NULL,
        Service             VARCHAR(128)    NULL,
        ObjectId            NVARCHAR(4000)  NULL,
        ClientIp            VARCHAR(128)    NULL,
        AdministrativeUnits NVARCHAR(4000)  NULL,
        AuditDataGz         VARBINARY(MAX)  NULL
    );

/* Which path recorded the backfill range: NULL is the Graph backfill, 'CSV' an
   import. Only a CSV-recorded range is widened by later imports. */
IF COL_LENGTH(N'audit.CopilotBackfillRange', N'Source') IS NULL
    ALTER TABLE audit.CopilotBackfillRange ADD Source VARCHAR(8) NULL;
/* The next batch reads Source, so it must compile after the column exists. */
GO

/*
    The final tables, for a database that starts from CSV files. Same DDL as
    Finalize-CopilotAudit.sql. Not created while a Graph backfill is started
    but not finalized: finalize builds them itself, from its staging table.
*/
IF OBJECT_ID(N'audit.CopilotAudit', N'U') IS NULL
   AND NOT EXISTS (SELECT 1 FROM audit.CopilotBackfillRange WHERE Id = 1 AND ISNULL(Source, '') <> 'CSV')
BEGIN
    CREATE TABLE audit.CopilotAudit
    (
        RecordId            VARCHAR(64)     NOT NULL,
        CreatedDateTime     DATETIME2(7)    NOT NULL,
        RecordType          VARCHAR(128)    NULL,
        Operation           VARCHAR(128)    NULL,
        OrganizationId      VARCHAR(64)     NULL,
        UserType            VARCHAR(64)     NULL,
        UserId              NVARCHAR(512)   NULL,
        UserPrincipalName   NVARCHAR(512)   NULL,
        Service             VARCHAR(128)    NULL,
        ObjectId            NVARCHAR(4000)  NULL,
        ClientIp            VARCHAR(128)    NULL,
        AdministrativeUnits NVARCHAR(4000)  NULL,
        AuditDataGz         VARBINARY(MAX)  NULL,
        WindowId            BIGINT          NOT NULL,
        LoadId              INT             NULL,
        CsvImportId         INT             NULL,
        INDEX CCI_CopilotAudit CLUSTERED COLUMNSTORE
    );

    CREATE TABLE audit.CopilotInteraction
    (
        RecordId              VARCHAR(64)    NOT NULL,
        CreatedDateTime       DATETIME2(0)   NOT NULL,
        ActivityDate          DATE           NOT NULL,
        UserPrincipalName     NVARCHAR(512)  NULL,
        AppHost               NVARCHAR(100)  NULL,
        AppIdentity           NVARCHAR(200)  NULL,
        ClientRegion          NVARCHAR(20)   NULL,
        ModelName             NVARCHAR(200)  NULL,
        ModelProvider         NVARCHAR(200)  NULL,
        ThreadId              NVARCHAR(400)  NULL,
        PromptCount           INT            NULL,
        AccessedResourceCount INT            NULL,
        UsedWebSearch         BIT            NULL,
        INDEX CCI_CopilotInteraction CLUSTERED COLUMNSTORE
    );

    EXEC (N'
    CREATE OR ALTER VIEW audit.vCopilotDailyUsage
    AS
    SELECT ActivityDate,
           AppHost,
           COUNT(*)                          AS Interactions,
           COUNT(DISTINCT UserPrincipalName) AS ActiveUsers,
           SUM(PromptCount)                  AS Prompts,
           SUM(CAST(UsedWebSearch AS INT))   AS WebGroundedInteractions
    FROM audit.CopilotInteraction
    GROUP BY ActivityDate, AppHost;');

    EXEC (N'
    CREATE OR ALTER VIEW audit.vCopilotAuditRecord
    AS
    SELECT RecordId,
           CreatedDateTime                                            AS CreationDate,
           UPPER(LEFT(RecordType, 1)) + SUBSTRING(RecordType, 2, 127) AS RecordType,
           Operation,
           COALESCE(UserPrincipalName, UserId)                        AS UserId,
           CAST(DECOMPRESS(AuditDataGz) AS NVARCHAR(MAX))             AS AuditData,
           AdministrativeUnits                                        AS AssociatedAdminUnits,
           CAST(NULL AS NVARCHAR(MAX))                                AS AssociatedAdminUnitsNames
    FROM audit.CopilotAudit;');
END;

/* Lineage for imported rows, on a table finalized by an earlier version. */
IF OBJECT_ID(N'audit.CopilotAudit', N'U') IS NOT NULL AND COL_LENGTH(N'audit.CopilotAudit', N'CsvImportId') IS NULL
    ALTER TABLE audit.CopilotAudit ADD CsvImportId INT NULL;
IF OBJECT_ID(N'audit.CopilotAudit', N'U') IS NOT NULL AND COL_LENGTH(N'audit.CopilotAudit', N'LoadId') IS NULL
    ALTER TABLE audit.CopilotAudit ADD LoadId INT NULL;
GO

IF OBJECT_ID(N'audit.usp_MergeCopilotCsv', N'P') IS NULL
    EXEC (N'CREATE PROCEDURE audit.usp_MergeCopilotCsv AS BEGIN SET NOCOUNT ON; END;');
GO

/*
    audit.usp_MergeCopilotCsv - appends one import's staged rows.

      1. Keeps one row per RecordId across the import's files (exports that
         overlap repeat a record; which copy is kept does not matter, they are
         the same record).
      2. Drops the records audit.CopilotAudit already holds, from the Graph
         backfill, an -Incremental load or an earlier import.
      3. Appends the rest to audit.CopilotAudit (CsvImportId for lineage;
         WindowId holds the audit.CopilotCsvFile) and their shredded rows to
         audit.CopilotInteraction through audit.fnShredCopilotInteraction, the
         same as the other two paths.
      4. Records the range the exports cover, when given, so -Incremental can
         continue from its end: creates audit.CopilotBackfillRange for a
         database that starts from CSV, or widens one an earlier import made.
         A range the Graph backfill recorded is left alone.
      5. Marks the import merged and empties the staging table - all but the
         last in one transaction.

    Refuses while a Graph backfill is started but not finalized: finalize
    rebuilds audit.CopilotAudit from its own staging table and would drop the
    imported rows.

    One merge at a time: the transaction holds the exclusive lock that
    audit.usp_MergeCopilotLoad takes too, from before the import is read, so
    an import beside the nightly merge never appends a record twice.
*/
ALTER PROCEDURE audit.usp_MergeCopilotCsv
    @ImportId           INT,
    @CoveredFromUtc     DATETIME2(0) = NULL,
    @CoveredToUtc       DATETIME2(0) = NULL,
    @ReportingTimeZone  SYSNAME      = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    SET @ReportingTimeZone = COALESCE(@ReportingTimeZone, audit.ReportingTimeZone());   -- the zone Install-CopilotAudit.ps1 -ReportingTimeZone stored

    DECLARE @msg NVARCHAR(800), @ops NVARCHAR(400), @merged DATETIME2(0), @lock INT;

    /* 2.4.12: the lock audit.usp_MergeCopilotLoad takes, for the whole merge.
       The check for records already loaded ran outside any lock, so a load
       merged at the same moment could append the same records, stored twice. */
    BEGIN TRANSACTION;
    EXEC @lock = sys.sp_getapplock @Resource = N'audit.CopilotAudit:append', @LockMode = N'Exclusive',
                                   @LockOwner = N'Transaction', @LockTimeout = -1;
    IF @lock < 0
        THROW 50110, N'Merge refused: the merge lock on audit.CopilotAudit was not granted. Run the import again.', 1;

    SELECT @ops = Operations, @merged = MergedUtc FROM audit.CopilotCsvImport WHERE ImportId = @ImportId;

    IF @ops IS NULL
        THROW 50101, N'Merge refused: no such CSV import.', 1;
    IF @merged IS NOT NULL
        THROW 50102, N'Merge refused: this import is already merged.', 1;
    IF OBJECT_ID(N'audit.fnShredCopilotInteraction', N'IF') IS NULL
        THROW 50103, N'Merge refused: audit.fnShredCopilotInteraction is missing. Run sql/Create-CopilotAuditSchema.sql first.', 1;
    IF NOT EXISTS (SELECT 1 FROM sys.time_zone_info WHERE name = @ReportingTimeZone)
        THROW 50104, N'Merge refused: @ReportingTimeZone is not a name in sys.time_zone_info.', 1;

    DECLARE @rangeOps NVARCHAR(400), @rangeSource VARCHAR(8), @rangeDone DATETIME2(0), @hasRange BIT = 0;
    SELECT @hasRange = 1, @rangeOps = Operations, @rangeSource = Source, @rangeDone = CompletedUtc
    FROM audit.CopilotBackfillRange WHERE Id = 1;

    IF @hasRange = 1 AND ISNULL(@rangeSource, '') <> 'CSV'
       AND (@rangeDone IS NULL OR OBJECT_ID(N'audit.CopilotAudit', N'U') IS NULL)
        THROW 50105, N'Merge refused: a Graph backfill has started in this database but is not finalized. Finish it and run -FinalizeOnly first; finalize rebuilds audit.CopilotAudit and would drop imported rows.', 1;
    IF @hasRange = 1 AND @rangeOps <> @ops
    BEGIN
        SET @msg = CONCAT(N'Merge refused: this database holds ''', @rangeOps, N''', and the import is of ''', @ops, N'''.');
        THROW 50106, @msg, 1;
    END;
    IF OBJECT_ID(N'audit.CopilotAudit', N'U') IS NULL OR OBJECT_ID(N'audit.CopilotInteraction', N'U') IS NULL
        THROW 50107, N'Merge refused: audit.CopilotAudit does not exist. Run sql/Create-CopilotCsvImport.sql.', 1;
    IF @CoveredToUtc IS NOT NULL AND @CoveredToUtc > SYSUTCDATETIME()
        THROW 50108, N'Merge refused: @CoveredToUtc is in the future.', 1;
    IF @CoveredFromUtc IS NOT NULL AND @CoveredToUtc IS NOT NULL AND @CoveredFromUtc >= @CoveredToUtc
        THROW 50109, N'Merge refused: @CoveredFromUtc must be before @CoveredToUtc.', 1;

    /* ---- 1. One row per RecordId across the import's files ------------------ */

    DROP TABLE IF EXISTS #cand;
    SELECT s.RecordId, s.CreatedDateTime, s.RecordType, s.Operation, s.OrganizationId, s.UserType, s.UserId,
           s.UserPrincipalName, s.Service, s.ObjectId, s.ClientIp, s.AdministrativeUnits, s.AuditDataGz,
           CAST(s.FileId AS BIGINT) AS WindowId,
           ROW_NUMBER() OVER (PARTITION BY s.RecordId ORDER BY s.FileId DESC, s.LineNumber DESC) AS rn
    INTO #cand
    FROM audit.CopilotCsvStage AS s
    JOIN audit.CopilotCsvFile AS f ON f.FileId = s.FileId AND f.ImportId = @ImportId;

    DECLARE @staged BIGINT = @@ROWCOUNT;
    DELETE FROM #cand WHERE rn > 1;
    DECLARE @duplicate BIGINT = @@ROWCOUNT;

    /* ---- 2. Skip what audit.CopilotAudit already holds ---------------------- */
    /*
        A record's creation time is fixed, so a copy already loaded has one in
        the import's range. Bounding the probe by it (with a day's margin) lets
        the columnstore skip row groups outside it.
    */
    CREATE CLUSTERED INDEX CIX_cand ON #cand (RecordId);

    DECLARE @lo DATETIME2(7), @hi DATETIME2(7);
    SELECT @lo = DATEADD(DAY, -1, MIN(CreatedDateTime)), @hi = DATEADD(DAY, 1, MAX(CreatedDateTime)) FROM #cand;

    DELETE c
    FROM #cand AS c
    WHERE EXISTS (SELECT 1 FROM audit.CopilotAudit AS a
                  WHERE a.RecordId = c.RecordId
                    AND a.CreatedDateTime >= @lo
                    AND a.CreatedDateTime <  @hi);
    DECLARE @already BIGINT = @@ROWCOUNT;
    DECLARE @added BIGINT = (SELECT COUNT_BIG(*) FROM #cand);
    /* The refresh that follows an import reads from @first on. To the whole second at or before the earliest record
       (2.5.4): rounded, a record half a second or more past the second fell before it, and was left out. */
    DECLARE @first DATETIME2(0), @last DATETIME2(0);
    SELECT @first = DATEADD(SECOND, DATEDIFF(SECOND, '2000-01-01', MIN(CreatedDateTime)), '2000-01-01'), @last = MAX(CreatedDateTime) FROM #cand;

    /* ---- 3 and 4. Append, record coverage, in the same transaction ---------- */

    INSERT INTO audit.CopilotAudit
        (RecordId, CreatedDateTime, RecordType, Operation, OrganizationId, UserType, UserId,
         UserPrincipalName, Service, ObjectId, ClientIp, AdministrativeUnits, AuditDataGz, WindowId, LoadId, CsvImportId)
    SELECT c.RecordId, c.CreatedDateTime, c.RecordType, c.Operation, c.OrganizationId, c.UserType, c.UserId,
           c.UserPrincipalName, c.Service, c.ObjectId, c.ClientIp, c.AdministrativeUnits, c.AuditDataGz, c.WindowId,
           NULL, @ImportId
    FROM #cand AS c;

    INSERT INTO audit.CopilotInteraction
        (RecordId, CreatedDateTime, ActivityDate, UserPrincipalName, AppHost, AppIdentity, ClientRegion,
         ModelName, ModelProvider, ThreadId, PromptCount, AccessedResourceCount, UsedWebSearch)
    SELECT
        c.RecordId,
        CAST(c.CreatedDateTime AS DATETIME2(0)),
        CAST((c.CreatedDateTime AT TIME ZONE 'UTC') AT TIME ZONE @ReportingTimeZone AS DATE),
        COALESCE(c.UserPrincipalName, s.JsonUserId),
        s.AppHost, s.AppIdentity, s.ClientRegion, s.ModelName, s.ModelProvider, s.ThreadId,
        s.PromptCount, s.AccessedResourceCount, s.UsedWebSearch
    FROM #cand AS c
    CROSS APPLY audit.fnShredCopilotInteraction(c.AuditDataGz) AS s;

    /* Coverage. With no range recorded yet, the import's own data gives the
       start when @CoveredFromUtc is not passed: the day of its first record. */
    IF @CoveredToUtc IS NOT NULL
    BEGIN
        IF @hasRange = 0
            INSERT INTO audit.CopilotBackfillRange (Id, RangeStartUtc, RangeEndUtc, Operations, CompletedUtc, Source)
            VALUES (1,
                    COALESCE(@CoveredFromUtc, CAST(CAST(@first AS DATE) AS DATETIME2(0)), @CoveredToUtc),
                    @CoveredToUtc, @ops, SYSUTCDATETIME(), 'CSV');
        ELSE IF @rangeSource = 'CSV'
            UPDATE audit.CopilotBackfillRange
            SET RangeStartUtc = CASE WHEN @CoveredFromUtc < RangeStartUtc THEN @CoveredFromUtc ELSE RangeStartUtc END,
                RangeEndUtc   = CASE WHEN @CoveredToUtc   > RangeEndUtc   THEN @CoveredToUtc   ELSE RangeEndUtc   END,
                CompletedUtc  = SYSUTCDATETIME()
            WHERE Id = 1;
    END;

    UPDATE audit.CopilotCsvImport
    SET MergedUtc = SYSUTCDATETIME(),
        FilesLoaded = (SELECT COUNT(*) FROM audit.CopilotCsvFile WHERE ImportId = @ImportId),
        RowsRead = (SELECT SUM(RowsRead) FROM audit.CopilotCsvFile WHERE ImportId = @ImportId),
        RowsRejected = (SELECT SUM(RowsRejected) FROM audit.CopilotCsvFile WHERE ImportId = @ImportId),
        RowsOtherOperation = (SELECT SUM(RowsOtherOperation) FROM audit.CopilotCsvFile WHERE ImportId = @ImportId),
        RowsStaged = @staged, RowsDuplicate = @duplicate, RowsAlreadyLoaded = @already, RowsAdded = @added,
        EarliestAddedUtc = @first, LatestAddedUtc = @last,
        CoveredFromUtc = @CoveredFromUtc, CoveredToUtc = @CoveredToUtc
    WHERE ImportId = @ImportId AND MergedUtc IS NULL;
    IF @@ROWCOUNT = 0   -- cannot happen under the lock; if it ever does, nothing is appended twice
        THROW 50102, N'Merge refused: this import is already merged.', 1;

    COMMIT TRANSACTION;

    TRUNCATE TABLE audit.CopilotCsvStage;

    SET @msg = CONCAT(N'CSV import ', @ImportId, N' merged: ', FORMAT(@staged, 'N0'), N' row(s) staged, ',
                      FORMAT(@duplicate, 'N0'), N' duplicate(s) across files, ', FORMAT(@already, 'N0'),
                      N' already loaded, ', FORMAT(@added, 'N0'), N' added.');
    RAISERROR(@msg, 0, 1) WITH NOWAIT;

    SELECT @ImportId AS ImportId, @staged AS RowsStaged, @duplicate AS RowsDuplicate, @already AS RowsAlreadyLoaded,
           @added AS RowsAdded, @first AS EarliestAddedUtc, @last AS LatestAddedUtc,
           (SELECT RangeStartUtc FROM audit.CopilotBackfillRange WHERE Id = 1) AS RangeStartUtc,
           (SELECT RangeEndUtc FROM audit.CopilotBackfillRange WHERE Id = 1) AS RangeEndUtc,
           (SELECT Source FROM audit.CopilotBackfillRange WHERE Id = 1) AS RangeSource;
END;
GO
```

### C.20 `Invoke-CopilotPipeline.ps1`

The nightly run and the first load of history: the order in which every table is filled. 402 lines.

```powershell
<#
.SYNOPSIS
    Runs the pipeline from the job server's settings: the nightly run, or,
    with -History, the first load of everything.

.DESCRIPTION
    Reads copilot-audit.settings.psd1 (written by tools\Install-CopilotJobServer.ps1),
    so nothing here needs a parameter. Each step runs in its own PowerShell
    process and writes to a log of its own for the run (2.5.6) in the settings' LogPath. It stops at
    the first failure and exits 1, so Task Scheduler's history shows it; the
    next run resumes the same load.

    Nightly (the scheduled task), in this order:
      1. User directory from AD, so tonight's builds see today's organization.
      2. Copilot licenses from Graph.
      3. License events: -Incremental into the license database.
      4. Copilot interactions: -Incremental, which then refreshes the
         flattened table, sessions, bi and the dashboard.

    Then, after a successful night, the retention the dashboard's
    administrators set (dash.usp_ApplyRetention, where the dashboard is
    installed).

    Every run and step is recorded in audit.PipelineRun and
    audit.PipelineRunStep, which Admin > Health shows and the dashboard's
    pipeline alert watches. If SQL cannot be reached to record it, the run
    goes on and the log still has everything.

    -History (once, from a console; hours):
      1. Copilot interactions for the last HistoryDays days, finalized.
      2. License events for the same days, finalized.
      3. User directory from AD.
      4. Copilot licenses from Graph.
      5. Install-CopilotAudit.ps1: shreds the license events and builds
         everything in full.
    Every step resumes where it stopped, so after Ctrl+C or a reboot the same
    command carries on.

.PARAMETER History
    Load the history instead of the nightly run.

.PARAMETER Days
    How many days of history (-History). Default: HistoryDays in the settings, 180.

.PARAMETER Tenant
    With several tenants (Tenants in the settings), run only this one, by
    its Name: for example to load a newly added tenant's history.

.PARAMETER SettingsPath
    Default: copilot-audit.settings.psd1 next to this script.

.NOTES
    Several Microsoft 365 tenants: each has its own app registration and its
    own pair of databases, listed under Tenants in the settings (each entry
    overrides the top-level values it names; tools\Add-CopilotTenant.ps1 adds
    one). The top-level values are the first tenant. Each tenant runs in turn,
    with its own run record; one tenant's failure does not stop the next, and
    the exit code is 1 if any failed.

    One run at a time on a database (2.4.12): each tenant's run holds its
    database while it works, as Install-CopilotAudit.ps1 and the maintenance
    rebuilds do. A run that finds another holding it stops at once, saying so.

.EXAMPLE
    .\Invoke-CopilotPipeline.ps1 -History
.EXAMPLE
    .\Invoke-CopilotPipeline.ps1          # what the nightly task runs
#>
#Requires -Version 7.2
[CmdletBinding()]
param(
    [switch] $History,
    [ValidateRange(1, 3650)][int] $Days,
    [string] $Tenant,
    [string] $SettingsPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# The license-event load's operations. Copilot Studio's authoring operations ride along in the same
# searches (no extra search), for Agents > Build and publish (audit.usp_ShredAgentEvents).
$script:AgentOperations = 'BotCreate', 'BotDelete', 'BotDeleteCleanup', 'BotUpdateOperation-BotPublish', 'BotUpdateOperation-BotShare',
                          'BotUpdateOperation-BotNameUpdate', 'BotUpdateOperation-BotAuthUpdate', 'BotUpdateOperation-BotIconUpdate',
                          'BotAppInsightsUpdate', 'BotComponentCreate', 'BotComponentUpdate', 'BotComponentDelete',
                          'BotComponentCollectionCreate', 'BotComponentCollectionDelete'
$script:LicenseOperations = @('Change user license', 'Update user', 'Add user', 'Delete user') + $script:AgentOperations
if (-not $SettingsPath) { $SettingsPath = Join-Path $PSScriptRoot 'copilot-audit.settings.psd1' }

function Get-PipelineSetting {
    param([string] $Path)
    if (-not (Test-Path -LiteralPath $Path)) { throw "No settings at $Path. Run tools\Install-CopilotJobServer.ps1 first." }
    $s = Import-PowerShellDataFile -LiteralPath $Path
    foreach ($k in 'SqlServer', 'Database', 'TenantId', 'ClientId', 'CertificateThumbprint') {
        if (-not ($s.Contains($k) -and $s[$k])) { throw "$k is not set in $Path. Run tools\Install-CopilotJobServer.ps1 again (with -RegisterApp, or -TenantId and -ClientId)." }
    }
    $s
}

function Get-PipelineTenant {
    <#
        Every tenant to run, as its own complete settings: the top-level
        values (the first tenant, named by TenantName or 'Default'), then each
        entry of Tenants overlaid on them. Pure, for the tests.
    #>
    param([System.Collections.IDictionary] $Setting, [string] $Only)
    $first = [ordered]@{}
    foreach ($k in $Setting.Keys) { if ($k -ne 'Tenants') { $first[$k] = $Setting[$k] } }
    $first.Name = if ($Setting.Contains('TenantName') -and $Setting.TenantName) { [string] $Setting.TenantName } else { 'Default' }
    $all = [System.Collections.Generic.List[object]]::new()
    $all.Add($first)
    if ($Setting.Contains('Tenants')) {
        foreach ($t in @($Setting.Tenants)) {
            if (-not $t) { continue }
            $m = [ordered]@{}
            foreach ($k in $first.Keys) { $m[$k] = $first[$k] }
            foreach ($k in $t.Keys) { $m[$k] = $t[$k] }
            foreach ($need in 'Name', 'TenantId', 'ClientId', 'Database') {
                if (-not ($t.Contains($need) -and $t[$need])) { throw "A tenant under Tenants has no $need. Each needs Name, TenantId, ClientId and its own Database." }
            }
            if ($m.Database -eq $first.Database) { throw "Tenant $($m.Name) uses the first tenant's database ($($m.Database)): each tenant needs its own." }
            $all.Add($m)
        }
    }
    $names = @($all | ForEach-Object { $_.Name })
    if (@($names | Select-Object -Unique).Count -ne $names.Count) { throw 'Two tenants have the same Name.' }
    if ($Only) {
        $pick = @($all | Where-Object { $_.Name -eq $Only })
        if (-not $pick) { throw "No tenant '$Only'. The tenants are: $($names -join ', ')." }
        return , $pick
    }
    , $all.ToArray()
}

function Get-PipelineStep {
    <#
        The steps to run, as (Name, Script, Arguments). Pure: the settings in,
        the plan out, so the plan can be tested without running anything.
    #>
    param([System.Collections.IDictionary] $Setting, [switch] $History, [int] $Days, [datetime] $Today = [datetime]::UtcNow.Date,
          [string] $Root = $PSScriptRoot)
    function Has($k) { $Setting.Contains($k) -and $Setting[$k] }
    $app = @{ TenantId = $Setting.TenantId; ClientId = $Setting.ClientId; CertificateThumbprint = $Setting.CertificateThumbprint }
    $sql = @{ SqlServer = $Setting.SqlServer }
    if (Has 'TrustServerCertificate') { $sql.TrustServerCertificate = $true }
    $lic = if (Has 'LicenseDatabase') { [string] $Setting.LicenseDatabase } else { $null }
    # The license databases sessions and bi read (2.4.12): SessionLicenseDatabases, a list added to the settings by
    # hand when an older license database is read beside the one loaded each night, else that one. Only that one
    # could be passed, so a database built with several (as Install-CopilotAudit.ps1 suggests) failed every night.
    $readLic = @(@(if (Has 'SessionLicenseDatabases') { $Setting.SessionLicenseDatabases } elseif ($lic) { $lic }) |
                 ForEach-Object { "$_" -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
    $steps = [System.Collections.Generic.List[object]]::new()
    function Add($name, $script, $argsTable) { $steps.Add([pscustomobject]@{ Name = $name; Script = [System.IO.Path]::Combine($Root, $script); Arguments = $argsTable }) }

    $directory = {
        if (Has 'TargetDomain') {
            $a = $sql + @{ Database = $Setting.Database; TargetDomain = $Setting.TargetDomain }
            if ($Setting.Contains('EmployeeTypes')) { $a.EmployeeTypes = @($Setting.EmployeeTypes) }
            if (Has 'DepartmentAttribute') { $a.DepartmentAttribute = @($Setting.DepartmentAttribute) }   # 2.5.0: where the department is
            Add 'User directory from AD' 'tools/Export-UsersWithHierarchy-ToSql.ps1' $a
        }
    }
    $licenses = {
        $l = $sql + $app + @{ Database = $Setting.Database; AllLicensedUsers = $true }
        if (Has 'IncludeLeaveDates') { $l.IncludeLeaveDates = $true }   # 2.4.3: needs User-LifeCycleInfo.Read.All
        Add 'Copilot licenses from Graph' 'tools/Update-CopilotAuditEnrichment.ps1' $l
    }

    if ($History) {
        $n = if ($Days) { $Days } elseif (Has 'HistoryDays') { [int] $Setting.HistoryDays } else { 180 }
        $start = $Today.AddDays(-$n)
        Add 'Copilot interactions (history)' 'Invoke-CopilotAuditBackfill.ps1' ($sql + $app + @{ Database = $Setting.Database; StartDate = $start; Finalize = $true })
        if ($lic) { Add 'License events (history)' 'Invoke-CopilotAuditBackfill.ps1' ($sql + $app + @{ Database = $lic; StartDate = $start; Finalize = $true; Operations = $script:LicenseOperations }) }
        & $directory
        & $licenses
        $build = $sql + @{ Database = $Setting.Database }
        if ($lic) { $build.LicenseDatabase = $lic } else { $build.SkipLicenseDatabase = $true }
        if ($readLic.Count) { $build.LicenseEventDatabases = $readLic }
        if (Has 'Dashboard') { $build.Dashboard = $true }
        Add 'Build everything' 'Install-CopilotAudit.ps1' $build
    }
    else {
        & $directory
        & $licenses
        # An Excel workbook of agents, reloaded each night when the settings name it.
        # From SharePoint Online (the newest file in a library, as the pipeline's app) or from an address or a share.
        if (Has 'AgentWorkbookSiteUrl') {
            # Its own app registration and certificate (2.5.6), in the tenant the workbook's site is in.
            $w = $sql + @{ Database = $Setting.Database; SiteUrl = $Setting.AgentWorkbookSiteUrl
                           TenantId = $(if (Has 'AgentWorkbookTenantId') { $Setting.AgentWorkbookTenantId } else { $Setting.TenantId })
                           ClientId = $Setting.AgentWorkbookClientId; CertificateThumbprint = $Setting.AgentWorkbookCertificateThumbprint }
            if (Has 'AgentWorkbookFolder') { $w.Folder = $Setting.AgentWorkbookFolder }
            if (Has 'AgentWorkbookFilter') { $w.FileFilter = $Setting.AgentWorkbookFilter }
            Add 'Agent workbook from Excel' 'tools/Import-AgentWorkbook.ps1' $w
        }
        elseif (Has 'AgentWorkbookUrl') { Add 'Agent workbook from Excel' 'tools/Import-AgentWorkbook.ps1' ($sql + @{ Database = $Setting.Database; Url = $Setting.AgentWorkbookUrl }) }
        if ($lic) { Add 'License events' 'Invoke-CopilotAuditBackfill.ps1' ($sql + $app + @{ Database = $lic; Incremental = $true; Operations = $script:LicenseOperations }) }
        $inter = $sql + $app + @{ Database = $Setting.Database; Incremental = $true }
        if ($readLic.Count) { $inter.SessionLicenseDatabase = $readLic -join ',' }
        Add 'Copilot interactions' 'Invoke-CopilotAuditBackfill.ps1' $inter
    }
    , $steps.ToArray()
}

function Invoke-PipelineStep {
    <# Runs one step in its own process, teeing its output to the log. Returns its exit code. #>
    param([pscustomobject] $Step, [string] $Log)
    "$(Get-Date -Format s) == $($Step.Name)" | Tee-Object -FilePath $Log -Append | Write-Information -InformationAction Continue
    # A separate process per step, so one step's modules and state cannot affect the next.
    & pwsh -NoProfile -NonInteractive -Command { param($s, $a) $global:LASTEXITCODE = 0; & $s @a; exit $LASTEXITCODE } -args $Step.Script, $Step.Arguments 2>&1 |
        Tee-Object -FilePath $Log -Append | Write-Information -InformationAction Continue
    $LASTEXITCODE
}

function Get-PipelineConnectionString {
    <# The Copilot database's connection string, from the settings. #>
    param([System.Collections.IDictionary] $Setting)
    $b = [System.Data.SqlClient.SqlConnectionStringBuilder]::new()
    $b['Data Source'] = $Setting.SqlServer; $b['Initial Catalog'] = $Setting.Database; $b['Integrated Security'] = $true
    $b['Encrypt'] = $true; $b['TrustServerCertificate'] = [bool] ($Setting.Contains('TrustServerCertificate') -and $Setting.TrustServerCertificate)
    # A whole connection string in the settings wins (the tests, or SQL authentication).
    if ($Setting.Contains('ConnectionString') -and $Setting.ConnectionString) { [string] $Setting.ConnectionString } else { $b.ConnectionString }
}

function Invoke-PipelineSql {
    <# One command against the Copilot database: a scalar back, or nothing. #>
    param([System.Collections.IDictionary] $Setting, [string] $Sql, [hashtable] $Parameters = @{}, [int] $Timeout = 60)
    $c = [System.Data.SqlClient.SqlConnection]::new((Get-PipelineConnectionString $Setting))
    $c.Open()
    try {
        $cmd = $c.CreateCommand()
        $cmd.CommandText = $Sql
        $cmd.CommandTimeout = $Timeout
        foreach ($k in $Parameters.Keys) { [void] $cmd.Parameters.AddWithValue($k, $(if ($null -eq $Parameters[$k]) { [DBNull]::Value } else { $Parameters[$k] })) }
        $cmd.ExecuteScalar()
    }
    finally { $c.Dispose() }
}

function Test-HistoryLoaded {
    param([System.Collections.IDictionary] $Setting)
    # The table only through EXEC (2.4.12): a statement naming a missing table fails when it runs, whatever its other
    # conditions, so a database whose history was not loaded yet threw here instead of saying to load it.
    [bool] (Invoke-PipelineSql $Setting "IF OBJECT_ID(N'audit.CopilotAudit', N'U') IS NULL SELECT 0; ELSE EXEC (N'SELECT CASE WHEN EXISTS (SELECT 1 FROM audit.CopilotAudit) THEN 1 ELSE 0 END');")
}

function Enter-PipelineRun {
    <#
        Holds the Copilot database for this run alone (2.4.12): an exclusive application lock, on a connection of its
        own kept open until the run ends, that Install-CopilotAudit.ps1 and the maintenance rebuilds take too. Nothing
        kept runs apart: a run by hand during the nightly, or the nightly during an upgrade's full rebuild, loaded the
        same windows twice or counted interactions twice. Returns that connection (dispose it to let go), or nothing
        when another run holds the database. The steps this run starts know it holds the lock by CopilotAuditRunLock,
        so the history's build step does not wait on its own run.
    #>
    param([System.Collections.IDictionary] $Setting)
    $b = [System.Data.SqlClient.SqlConnectionStringBuilder]::new((Get-PipelineConnectionString $Setting))
    # Not pooled: closing a pooled connection keeps its session, and the session's lock with it.
    $b['Pooling'] = $false
    $c = [System.Data.SqlClient.SqlConnection]::new($b.ConnectionString)
    try {
        $c.Open()
        $cmd = $c.CreateCommand()
        $cmd.CommandText = "DECLARE @r INT; EXEC @r = sp_getapplock @Resource = N'CopilotAudit.Run', @LockMode = 'Exclusive', @LockOwner = 'Session', @LockTimeout = 0; SELECT @r;"
        if ([int] $cmd.ExecuteScalar() -lt 0) { $c.Dispose(); return $null }
    }
    catch { $c.Dispose(); throw }
    $env:CopilotAuditRunLock = "$($Setting.SqlServer)/$($Setting.Database)"
    $c
}

function Exit-PipelineRun {
    param($Lock)
    if ($Lock) { $Lock.Dispose() }
    Remove-Item Env:\CopilotAuditRunLock -ErrorAction SilentlyContinue
}

function Write-PipelineRecord {
    <#
        Records the run in audit.PipelineRun / PipelineRunStep. Never fails the
        run: a database that cannot be reached, or one without the tables yet,
        leaves only a warning in the log.
    #>
    param([System.Collections.IDictionary] $Setting, [string] $Sql, [hashtable] $Parameters, [string] $Log)
    try { Invoke-PipelineSql $Setting ("IF OBJECT_ID(N'audit.PipelineRun', N'U') IS NOT NULL BEGIN " + $Sql + " END") $Parameters }
    catch { "$(Get-Date -Format s) (not recorded in audit.PipelineRun: $($_.Exception.Message))" | Out-File -FilePath $Log -Append }
}

function Get-LogTail {
    <# The last lines of the log, for the failed run's detail. #>
    param([string] $Log, [int] $Lines = 25)
    if (-not (Test-Path -LiteralPath $Log)) { return $null }
    $t = (Get-Content -LiteralPath $Log -Tail $Lines) -join "`n"
    if ($t.Length -gt 3900) { $t = $t.Substring($t.Length - 3900) }
    $t
}

function Invoke-Retention {
    <#
        The retention the dashboard's administrators set (Admin > Privacy and
        retention): old audit-log and notification rows, and the stored JSON of
        old records. A failure is logged, not fatal: the data is loaded either way.
    #>
    param([System.Collections.IDictionary] $Setting, [string] $Log)
    try {
        $r = Invoke-PipelineSql $Setting -Timeout 3600 -Sql "IF OBJECT_ID(N'dash.usp_ApplyRetention', N'P') IS NOT NULL EXEC dash.usp_ApplyRetention;"
        if ($r) { "$(Get-Date -Format s) Retention: $r" | Tee-Object -FilePath $Log -Append | Write-Information -InformationAction Continue }
    }
    catch { "$(Get-Date -Format s) Retention was not applied: $($_.Exception.Message)" | Tee-Object -FilePath $Log -Append | Write-Information -InformationAction Continue }
}

function Get-PipelineCertificate {
    <# The app certificate in LocalMachine\My (or CurrentUser\My), or nothing. #>
    param([System.Collections.IDictionary] $T)
    if (-not $T.Contains('CertificateThumbprint') -or -not $T.CertificateThumbprint) { return }
    foreach ($store in 'Cert:\LocalMachine\My', 'Cert:\CurrentUser\My') {
        $c = Get-Item -LiteralPath "$store\$($T.CertificateThumbprint)" -ErrorAction SilentlyContinue
        if ($c) { return $c }
    }
}

function Invoke-TenantRun {
    <# One tenant's run: its steps in order, recorded in its own database. Returns $true when every step succeeded. #>
    param([System.Collections.IDictionary] $T, [string] $Log, [string] $Label)
    if (-not $History -and -not (Test-HistoryLoaded $T)) {
        "$(Get-Date -Format s) $Label No Copilot records in $($T.Database) yet. Load the history first: .\Invoke-CopilotPipeline.ps1 -History$(if ($Label) { " -Tenant '$($T.Name)'" })" |
            Tee-Object -FilePath $Log -Append | Write-Information -InformationAction Continue
        return $false
    }
    $runId = Write-PipelineRecord $T -Log $Log -Parameters @{ '@k' = $(if ($History) { 'History' } else { 'Nightly' }); '@h' = [Environment]::MachineName; '@u' = "$env:USERDOMAIN\$env:USERNAME" } -Sql `
        "INSERT INTO audit.PipelineRun (Kind, Host, RunAs) VALUES (@k, @h, @u); SELECT CAST(SCOPE_IDENTITY() AS INT);"
    # The app's certificate and when it expires, for the dashboard's expiry alert.
    $cert = Get-PipelineCertificate $T
    if ($runId -and $cert) {
        [void] (Write-PipelineRecord $T -Log $Log -Parameters @{ '@r' = $runId; '@t' = $cert.Thumbprint; '@e' = $cert.NotAfter.ToUniversalTime(); '@s' = $cert.Subject } -Sql `
            "IF COL_LENGTH(N'audit.PipelineRun', N'CertificateExpiresUtc') IS NOT NULL EXEC sp_executesql N'UPDATE audit.PipelineRun SET CertificateThumbprint = @t, CertificateExpiresUtc = @e, CertificateSubject = LEFT(@s, 400) WHERE RunId = @r;', N'@r INT, @t VARCHAR(64), @e DATETIME2(0), @s NVARCHAR(4000)', @r = @r, @t = @t, @e = @e, @s = @s;")
    }
    $n = 0
    foreach ($step in (Get-PipelineStep -Setting $T -History:$History -Days $Days)) {
        $n++
        if ($Label) { $step.Name = "$Label $($step.Name)" }
        if ($runId) { [void] (Write-PipelineRecord $T -Log $Log -Parameters @{ '@r' = $runId; '@n' = $n; '@s' = $step.Name } -Sql 'INSERT INTO audit.PipelineRunStep (RunId, StepNo, Name) VALUES (@r, @n, @s);') }
        $code = Invoke-PipelineStep -Step $step -Log $Log
        if ($runId) { [void] (Write-PipelineRecord $T -Log $Log -Parameters @{ '@r' = $runId; '@n' = $n; '@c' = $code } -Sql 'UPDATE audit.PipelineRunStep SET EndedUtc = SYSUTCDATETIME(), ExitCode = @c WHERE RunId = @r AND StepNo = @n;') }
        if ($code -ne 0) {
            "$(Get-Date -Format s) $($step.Name) failed (exit code $code). Run the same command again to resume." |
                Tee-Object -FilePath $Log -Append | Write-Information -InformationAction Continue
            if ($runId) {
                [void] (Write-PipelineRecord $T -Log $Log -Parameters @{ '@r' = $runId; '@f' = $step.Name; '@d' = (Get-LogTail $Log) } `
                    -Sql "UPDATE audit.PipelineRun SET EndedUtc = SYSUTCDATETIME(), Status = 'Failed', FailedStep = @f, Detail = @d WHERE RunId = @r;")
            }
            return $false
        }
    }
    if (-not $History) { Invoke-Retention -Setting $T -Log $Log }
    if ($runId) { [void] (Write-PipelineRecord $T -Log $Log -Parameters @{ '@r' = $runId } -Sql "UPDATE audit.PipelineRun SET EndedUtc = SYSUTCDATETIME(), Status = 'Succeeded' WHERE RunId = @r;") }
    $true
}

function Invoke-Main {
    $s = Get-PipelineSetting $SettingsPath
    $logDir = if ($s.Contains('LogPath') -and $s.LogPath) { $s.LogPath } else { Join-Path $PSScriptRoot 'logs' }
    New-Item -ItemType Directory -Path $logDir -Force | Out-Null
    # A log of its own for each run (2.5.6): a second run on one day appended to the first's.
    $log = Join-Path $logDir ("{0}-{1:yyyy-MM-dd-HHmmss}.log" -f $(if ($History) { 'history' } else { 'nightly' }), (Get-Date))
    $keep = if ($s.Contains('LogRetentionDays') -and $s.LogRetentionDays) { [int] $s.LogRetentionDays } else { 90 }
    Get-ChildItem -LiteralPath $logDir -Filter '*.log' | Where-Object LastWriteTime -lt (Get-Date).AddDays(-$keep) | Remove-Item -ErrorAction SilentlyContinue

    $tenants = Get-PipelineTenant -Setting $s -Only $Tenant
    $several = $s.Contains('Tenants') -and @($s.Tenants).Count -gt 0
    $failed = @()
    foreach ($t in $tenants) {
        $label = if ($several) { "[$($t.Name)]" } else { '' }
        # A tenant that cannot run is a failure of that tenant, and the next one runs (2.4.12): one whose database was
        # offline, restoring or had lost the service account's login threw out of this loop, so the tenants after it
        # did not run, every night until it was fixed, and no "Finished with failures" line was written.
        $ok = $false; $lock = $null
        try {
            $lock = Enter-PipelineRun $t
            if ($lock) { $ok = Invoke-TenantRun -T $t -Log $log -Label $label }
            else {
                "$(Get-Date -Format s) $label Another run is using $($t.Database) now (the nightly task, an upgrade or a rebuild): nothing was run. Run this again when it has finished." |
                    Tee-Object -FilePath $log -Append | Write-Information -InformationAction Continue
            }
        }
        catch {
            "$(Get-Date -Format s) $label Could not run against $($t.Database): $($_.Exception.Message)" |
                Tee-Object -FilePath $log -Append | Write-Information -InformationAction Continue
        }
        finally { Exit-PipelineRun $lock }
        if (-not $ok) { $failed += $t.Name }
    }
    if ($failed.Count) {
        "$(Get-Date -Format s) Finished with failures$(if ($several) { ": $($failed -join ', ')" })." | Tee-Object -FilePath $log -Append | Write-Information -InformationAction Continue
        exit 1
    }
    "$(Get-Date -Format s) Done." | Tee-Object -FilePath $log -Append | Write-Information -InformationAction Continue
    if ($History) { Write-Information 'History loaded and built. The nightly task keeps it current from tonight.' -InformationAction Continue }
}

# Dot-sourcing (tests) loads the functions without running anything.
if ($MyInvocation.InvocationName -ne '.') { Invoke-Main }
```

### C.21 `tools/Import-AgentWorkbook.ps1`

Creates and fills the three `dbo` tables of the agent workbook, then `dash.AgentInventory` and `dash.AgentChildLink`. 328 lines.

```powershell
<#
.SYNOPSIS
    Loads the agent inventory workbook into the Copilot database: each sheet into its own table, emptied and
    loaded again on every run, then the inventory the agent reports read.

.DESCRIPTION
    1. Fetches the workbook. From SharePoint Online (-SiteUrl): the most recently created file matching
       -FileFilter in -Folder, with PnP.PowerShell, as its own app registration by its certificate. That app
       needs SharePoint's Sites.Selected application permission and read on the site
       (Grant-PnPAzureADAppSitePermission), or Sites.Read.All. Or from an intranet address or a file (-Url).
    2. Loads each sheet as it is: by default AgentInventory, AgentsInProd and ParentChildAgent into the dbo
       tables of those names. A sheet's first row names its columns; a table that does not exist is made with a
       text column for each, and a heading added later becomes a column. Each table is emptied and loaded in one
       transaction, so a reader never sees it empty. A sheet that is empty or missing leaves its table as it was.
    3. Fills dash.AgentInventory (one row per agent the workbook lists, the current agents) and
       dash.AgentChildLink, matching each agent to the one the audit log knows: by the schema name its identity
       ends in, then the bot id, the title id or the Entra agent id it holds.
    4. Names the agents in the report tables (bi.usp_NameAgents): the workbook's name, or the audit log's for an
       agent the workbook does not list or leaves unnamed.
    5. Records the load in dash.AgentInventoryRun, which Admin > Health shows.

    With no -SiteUrl or -Url, everything comes from the settings file beside the scripts
    (copilot-audit.settings.psd1): AgentWorkbookSiteUrl, AgentWorkbookFolder, AgentWorkbookFilter,
    AgentWorkbookTenantId (else TenantId), AgentWorkbookClientId and AgentWorkbookCertificateThumbprint, or
    AgentWorkbookUrl; and SqlServer, Database and TrustServerCertificate.

    Reads .xlsx with the ImportExcel module, which the offline zip carries in its Modules folder (2.5.7). For
    SharePoint Online, put PnP.PowerShell there too (Save-Module PnP.PowerShell -Path <scripts>\Modules; it needs
    PowerShell 7.4.6 or later), so the nightly task's account finds it.

.EXAMPLE
    .\tools\Import-AgentWorkbook.ps1          # as the nightly run does, from the settings file
.EXAMPLE
    .\tools\Import-AgentWorkbook.ps1 -Url '\\fileserver\Copilot\Agents.xlsx' -SqlServer SQLPROD01
#>
#Requires -Version 7.2
param(
    [string] $Url,                            # an intranet address, or a file or UNC path
    [string] $SiteUrl,                        # a SharePoint Online site: its newest workbook is taken
    [string] $Folder = 'Shared Documents',    # the Documents library, or a folder in it, site-relative
    [string] $FileFilter = '*.xlsx',
    [string] $TenantId, [string] $ClientId, [string] $CertificateThumbprint,
    [string] $SqlServer,
    [string] $Database = 'CopilotAudit',
    [switch] $TrustServerCertificate,
    [string] $SettingsPath = (Join-Path (Split-Path $PSScriptRoot -Parent) 'copilot-audit.settings.psd1'),
    [hashtable] $Sheets = @{ 'AgentInventory' = 'dbo.AgentInventory'; 'AgentsInProd' = 'dbo.AgentsInProd'; 'ParentChildAgent' = 'dbo.ParentChildAgent' }
)
$ErrorActionPreference = 'Stop'
$modules = Join-Path (Split-Path $PSScriptRoot -Parent) 'Modules'
if (Test-Path $modules) { $env:PSModulePath = "$modules;$env:PSModulePath" }

# What the command line does not give comes from the settings file.
if (-not $SiteUrl -and -not $Url) {
    if (-not (Test-Path -LiteralPath $SettingsPath)) { throw "Give -SiteUrl or -Url, or put AgentWorkbookSiteUrl or AgentWorkbookUrl in $SettingsPath." }
    $s = Import-PowerShellDataFile -LiteralPath $SettingsPath
    function Setting($k) { if ($s.Contains($k) -and $s[$k]) { $s[$k] } }
    $SiteUrl = Setting 'AgentWorkbookSiteUrl'; $Url = Setting 'AgentWorkbookUrl'
    if (Setting 'AgentWorkbookFolder') { $Folder = Setting 'AgentWorkbookFolder' }
    if (Setting 'AgentWorkbookFilter') { $FileFilter = Setting 'AgentWorkbookFilter' }
    if (-not $TenantId) { $TenantId = if (Setting 'AgentWorkbookTenantId') { Setting 'AgentWorkbookTenantId' } else { Setting 'TenantId' } }
    if (-not $ClientId) { $ClientId = Setting 'AgentWorkbookClientId' }
    if (-not $CertificateThumbprint) { $CertificateThumbprint = Setting 'AgentWorkbookCertificateThumbprint' }
    if (-not $SqlServer) { $SqlServer = Setting 'SqlServer' }
    if (-not $PSBoundParameters.ContainsKey('Database') -and (Setting 'Database')) { $Database = Setting 'Database' }
    if (Setting 'TrustServerCertificate') { $TrustServerCertificate = $true }
    if (-not $SiteUrl -and -not $Url) { throw "Neither AgentWorkbookSiteUrl nor AgentWorkbookUrl is in $SettingsPath." }
}
if (-not $SqlServer) { throw 'Give -SqlServer, or SqlServer in the settings file.' }
if ($SiteUrl -and -not ($ClientId -and $CertificateThumbprint -and $TenantId)) {
    throw 'SharePoint Online needs the workbook app: -TenantId, -ClientId and -CertificateThumbprint (AgentWorkbookClientId and AgentWorkbookCertificateThumbprint in the settings).'
}

$conn = [System.Data.SqlClient.SqlConnection]::new("Server=$SqlServer;Database=$Database;Integrated Security=true;Encrypt=true;TrustServerCertificate=$([bool] $TrustServerCertificate)")
$conn.Open()
function Sql([string] $Text, $Tx, [switch] $Scalar) {
    $c = $conn.CreateCommand(); $c.CommandText = $Text; $c.Transaction = $Tx; $c.CommandTimeout = 0
    if ($Scalar) { $c.ExecuteScalar() } else { [void] $c.ExecuteNonQuery() }
}
function Q([string] $s) { if ($null -eq $s) { 'NULL' } else { "N'" + $s.Replace("'", "''") + "'" } }

# The load is recorded for Admin > Health, from its start, so a failure shows too.
$runId = Sql "IF OBJECT_ID(N'dash.AgentInventoryRun', N'U') IS NOT NULL BEGIN INSERT INTO dash.AgentInventoryRun DEFAULT VALUES; SELECT CAST(SCOPE_IDENTITY() AS INT); END" $null -Scalar
$file = Join-Path ([IO.Path]::GetTempPath()) "agent-workbook-$PID.xlsx"
try {
    Import-Module ImportExcel
    $source = $Url; $created = $null
    if ($SiteUrl) {
        Import-Module PnP.PowerShell
        Connect-PnPOnline -Url $SiteUrl -ClientId $ClientId -Tenant $TenantId -Thumbprint $CertificateThumbprint
        $latest = Get-PnPFolderItem -FolderSiteRelativeUrl $Folder -ItemType File |
            Where-Object Name -like $FileFilter | Sort-Object TimeCreated -Descending | Select-Object -First 1
        if (-not $latest) { throw "No $FileFilter file in $Folder on $SiteUrl." }
        $source = "$SiteUrl/$Folder/$($latest.Name)"; $created = $latest.TimeCreated
        Write-Host "Newest workbook: $($latest.Name), created $($created.ToString('yyyy-MM-dd HH:mm')) UTC."
        Get-PnPFile -Url $latest.ServerRelativeUrl -Path (Split-Path $file) -Filename (Split-Path $file -Leaf) -AsFile -Force
        Disconnect-PnPOnline
    }
    elseif ($Url -match '^https?://') { Invoke-WebRequest -Uri $Url -OutFile $file -UseDefaultCredentials }
    else { Copy-Item -LiteralPath $Url -Destination $file; $created = (Get-Item -LiteralPath $Url).CreationTimeUtc }

    # 1. Each sheet as it is, into its own table.
    $loaded = @{}
    foreach ($sheet in $Sheets.Keys) {
        $table = $Sheets[$sheet]
        $rows = @(Import-Excel -Path $file -WorksheetName $sheet)
        if (-not $rows) { Write-Warning "Sheet $sheet is empty or missing: $table left as it is."; continue }
        $dt = [System.Data.DataTable]::new()
        $cols = $rows[0].PSObject.Properties.Name
        foreach ($c in $cols) { [void] $dt.Columns.Add($c, [string]) }
        foreach ($r in $rows) {
            [void] $dt.Rows.Add(@($cols | ForEach-Object {
                $v = $r.$_
                if ($null -eq $v) { [DBNull]::Value } elseif ($v -is [datetime]) { $v.ToString('yyyy-MM-dd HH:mm:ss') } else { "$v" } }))
        }
        $q = ($table -split '\.' | ForEach-Object { "[$($_.Trim('[]'))]" }) -join '.'
        Sql "IF OBJECT_ID(N'$table', N'U') IS NULL CREATE TABLE $q ([$($cols[0].Replace(']', ']]'))] NVARCHAR(MAX) NULL);" $null
        foreach ($c in $cols) { Sql "IF COL_LENGTH(N'$table', N'$($c.Replace("'", "''"))') IS NULL ALTER TABLE $q ADD [$($c.Replace(']', ']]'))] NVARCHAR(MAX) NULL;" $null }
        $tx = $conn.BeginTransaction()
        try {
            Sql "DELETE FROM $q;" $tx
            $bulk = [System.Data.SqlClient.SqlBulkCopy]::new($conn, [System.Data.SqlClient.SqlBulkCopyOptions]::TableLock, $tx)
            $bulk.DestinationTableName = $q; $bulk.BulkCopyTimeout = 0
            foreach ($c in $cols) { [void] $bulk.ColumnMappings.Add($c, $c) }
            $bulk.WriteToServer($dt)
            $tx.Commit()
        }
        catch { $tx.Rollback(); throw }
        # The dashboard reads the sheets as they are (the agent's every column on Agent detail).
        Sql "IF DATABASE_PRINCIPAL_ID(N'CopilotInsightsApp') IS NOT NULL GRANT SELECT ON $q TO CopilotInsightsApp;" $null
        $loaded[$sheet] = $dt.Rows.Count
        Write-Host "$sheet -> $table`: $($dt.Rows.Count) row(s)."
    }

    # 2. The inventory the reports read, matched to the agents the audit log knows. A column the sheet lacks is NULL.
    $inv = $Sheets['AgentInventory']; $prod = $Sheets['AgentsInProd']; $pc = $Sheets['ParentChildAgent']
    if ($inv -and (Sql "SELECT CASE WHEN OBJECT_ID(N'$inv', N'U') IS NULL OR OBJECT_ID(N'dash.AgentInventory', N'U') IS NULL THEN 0 ELSE 1 END" $null -Scalar)) {
        function Columns($t) { $c = $conn.CreateCommand(); $c.CommandText = "SELECT name FROM sys.columns WHERE object_id = OBJECT_ID(N'$t')"; $r = $c.ExecuteReader(); $n = @(); while ($r.Read()) { $n += $r.GetString(0) }; $r.Dispose(); $n }
        $ic = Columns $inv
        function T($name) { if ($ic -contains $name) { "NULLIF(LTRIM(RTRIM(CAST(w.[$name] AS NVARCHAR(4000)))), N'')" } else { 'CAST(NULL AS NVARCHAR(4000))' } }
        function D($name) { "TRY_CONVERT(DATETIME2(0), $(T $name))" }
        function N($name) { "TRY_CONVERT(INT, TRY_CONVERT(FLOAT, $(T $name)))" }
        function B($name) { "CASE WHEN $(T $name) IN (N'True', N'Yes', N'1') THEN 1 WHEN $(T $name) IN (N'False', N'No', N'0') THEN 0 END" }
        $inProd = if ($prod -and (Sql "SELECT CASE WHEN COL_LENGTH(N'$prod', N'AgentId') IS NULL THEN 0 ELSE 1 END" $null -Scalar)) {
            "CASE WHEN EXISTS (SELECT 1 FROM $prod AS p WHERE LTRIM(RTRIM(CAST(p.AgentId AS NVARCHAR(100)))) = x.Id) THEN 1 ELSE 0 END" } else { '0' }
        $pcCols = if ($pc -and (Sql "SELECT CASE WHEN OBJECT_ID(N'$pc', N'U') IS NULL OR OBJECT_ID(N'dash.AgentChildLink', N'U') IS NULL THEN 0 ELSE 1 END" $null -Scalar)) { Columns $pc }
        if ($pcCols -and -not (Sql "SELECT CASE WHEN COL_LENGTH(N'dash.AgentChildLink', N'ChildKey') IS NULL THEN 0 ELSE 1 END" $null -Scalar)) {
            throw 'dash.AgentChildLink predates 2.5.7: run Install-CopilotAudit.ps1 -Dashboard from this release first.'
        }
        if (-not (Sql "SELECT CASE WHEN COL_LENGTH(N'dash.AgentInventory', N'IsAutonomous') IS NULL THEN 0 ELSE 1 END" $null -Scalar)) {
            throw 'dash.AgentInventory predates 2.5.8: run Install-CopilotAudit.ps1 -Dashboard from this release first.'
        }
        $tx = $conn.BeginTransaction()
        try {
            Sql @"
SELECT CAST(LEFT($(T 'AgentId'), 100) AS NVARCHAR(100)) AS Id, $(T 'AgentName') AS AgentName, $(T 'OwnerIdUPN') AS OwnerUpn,
       $(T 'EnvironmentDisplayName') AS Environment, $(T 'SchemaName') AS SchemaName, $(D 'CreatedAt') AS CreatedAt,
       $(D 'LastPublishedAt') AS LastPublishedAt, $(T 'Channels') AS Channels, $(N 'ViewerUsers') AS ViewerUsers,
       $(N 'ViewerGroups') AS ViewerGroups, $(N 'EditorUsers') AS EditorUsers, $(N 'EditorGroups') AS EditorGroups,
       $(T 'TitleId') AS TitleId, $(T 'EntraAgentId') AS EntraAgentId, $(T 'EnvironmentId') AS EnvironmentId,
       $(T 'EnvironmentSku') AS EnvironmentSku, $(T 'Authentication') AS Authentication, $(B 'IsQuarantined') AS IsQuarantined,
       $(B 'IsWebSearchEnabled') AS IsWebSearchEnabled,
       CASE WHEN COALESCE($(T 'Triggers'), $(T 'Flows')) IS NOT NULL AND COALESCE($(T 'Triggers'), $(T 'Flows')) NOT IN (N'0', N'[]', N'None') THEN 1 ELSE 0 END AS IsAutonomous
INTO #w FROM $inv AS w;
DELETE FROM #w WHERE Id IS NULL;

/*
    2.5.7: how the audit log names an agent (bi.DimAgent.AgentId, its AppIdentity), and so how each agent is matched:
      1  Copilot Studio            Copilot.Studio.<EnvironmentId>-<SchemaName>  (the default environment's id is Default-<tenant>)
      2  Agent Builder, Studio lite Copilot.Studio.Declarative.<TitleId>         (T_<guid>; the T_ added where the workbook leaves it off)
      3  The same as 1, with the schema name Copilot Studio's own authoring records give the agent's bot id
         (audit.AgentEvent BotId -> BotSchemaName), where the workbook's SchemaName is not the one the agent runs under
      4  The schema name alone, at the end of a Copilot Studio identity, in any environment
      5  The bot id, or 6 the Entra agent id, anywhere in the identity (records keyed by AgentId alone)
    1-3 are exact. 4-6 are taken only where one agent of the workbook claims the audit log's agent: a schema name
    used in two environments matches neither. Microsoft's own agents (MicrosoftAgent.*) are not in the workbook.
*/
UPDATE #w SET TitleId = N'T_' + TitleId WHERE TitleId IS NOT NULL AND TitleId NOT LIKE N'T[_]%';
DECLARE @g NVARCHAR(200) = N'%' + REPLICATE(N'[0-9a-fA-F]', 8) + N'-' + REPLICATE(N'[0-9a-fA-F]', 4) + N'-' + REPLICATE(N'[0-9a-fA-F]', 4)
                         + N'-' + REPLICATE(N'[0-9a-fA-F]', 4) + N'-' + REPLICATE(N'[0-9a-fA-F]', 12) + N'%';
SELECT d.AgentKey, d.AgentId, i.DerivedName AS KSchema,
       CASE WHEN PATINDEX(@g, d.AgentId) > 0 THEN SUBSTRING(d.AgentId, PATINDEX(@g, d.AgentId), 36) END AS KGuid,
       CAST(CASE WHEN d.AgentId LIKE N'Copilot.Studio.%' AND d.AgentId NOT LIKE N'Copilot.Studio.Declarative.%' THEN 1 ELSE 0 END AS BIT) AS Studio
INTO #d FROM bi.DimAgent AS d CROSS APPLY bi.fnAgentIdentity(d.AgentId, d.AgentId) AS i WHERE d.AgentId <> N'(none)';
CREATE INDEX IX_d ON #d (AgentId);

CREATE TABLE #b (BotId NVARCHAR(100) COLLATE DATABASE_DEFAULT NOT NULL, SchemaName NVARCHAR(400) COLLATE DATABASE_DEFAULT NOT NULL);
IF COL_LENGTH(N'audit.AgentEvent', N'BotSchemaName') IS NOT NULL
    EXEC (N'INSERT INTO #b (BotId, SchemaName) SELECT DISTINCT LEFT(CAST(BotId AS NVARCHAR(100)), 100), BotSchemaName
            FROM audit.AgentEvent WHERE BotId IS NOT NULL AND NULLIF(BotSchemaName, N'''') IS NOT NULL;');

SELECT x.Id, x.AgentKey, MIN(x.Rnk) AS Rnk INTO #c
FROM (SELECT w.Id, d.AgentKey, 1 AS Rnk FROM #w AS w JOIN #d AS d ON d.AgentId = N'Copilot.Studio.' + w.EnvironmentId + N'-' + w.SchemaName
      UNION ALL SELECT w.Id, d.AgentKey, 2 FROM #w AS w JOIN #d AS d ON d.AgentId = N'Copilot.Studio.Declarative.' + w.TitleId OR d.AgentId = w.TitleId
      UNION ALL SELECT w.Id, d.AgentKey, 3 FROM #w AS w JOIN #b AS b ON b.BotId = w.Id
                JOIN #d AS d ON d.AgentId = N'Copilot.Studio.' + w.EnvironmentId + N'-' + b.SchemaName
      UNION ALL SELECT w.Id, d.AgentKey, 4 FROM #w AS w JOIN #d AS d ON d.Studio = 1 AND d.KSchema = w.SchemaName
      UNION ALL SELECT w.Id, d.AgentKey, 4 FROM #w AS w JOIN #d AS d ON d.Studio = 1 AND w.SchemaName LIKE N'%-%'
                AND RIGHT(d.AgentId, LEN(w.SchemaName) + 1) = N'-' + w.SchemaName
      UNION ALL SELECT w.Id, d.AgentKey, 4 FROM #w AS w JOIN #b AS b ON b.BotId = w.Id JOIN #d AS d ON d.Studio = 1 AND d.KSchema = b.SchemaName
      UNION ALL SELECT w.Id, d.AgentKey, 5 FROM #w AS w JOIN #d AS d ON d.KGuid = w.Id OR d.AgentId = w.Id
      UNION ALL SELECT w.Id, d.AgentKey, 6 FROM #w AS w JOIN #d AS d ON d.KGuid = w.EntraAgentId OR d.AgentId = w.EntraAgentId) AS x
GROUP BY x.Id, x.AgentKey;

/* Each workbook agent's best match; then each audit agent's best claimants, kept unless a loose match is shared. */
SELECT Id, AgentKey, Rnk INTO #p
FROM (SELECT Id, AgentKey, Rnk, ROW_NUMBER() OVER (PARTITION BY Id ORDER BY Rnk, AgentKey) AS rn FROM #c) AS y WHERE rn = 1;
SELECT p.Id, p.AgentKey, p.Rnk INTO #m
FROM #p AS p
JOIN (SELECT AgentKey, MIN(Rnk) AS Best FROM #p GROUP BY AgentKey) AS k ON k.AgentKey = p.AgentKey AND p.Rnk = k.Best
WHERE p.Rnk <= 3 OR (SELECT COUNT(*) FROM #p AS q WHERE q.AgentKey = p.AgentKey AND q.Rnk = p.Rnk) = 1;

DELETE FROM dash.AgentInventory;
INSERT INTO dash.AgentInventory (InventoryAgentId, AgentKey, AgentName, OwnerUpn, Environment, SchemaName, CreatedAt, LastPublishedAt,
                                 Channels, ViewerUsers, ViewerGroups, EditorUsers, EditorGroups, InProduction,
                                 EnvironmentSku, Authentication, IsQuarantined, IsWebSearchEnabled, IsAutonomous)
SELECT x.Id, m.AgentKey, LEFT(x.AgentName, 400), LEFT(x.OwnerUpn, 256), LEFT(x.Environment, 400), LEFT(x.SchemaName, 400), x.CreatedAt,
       x.LastPublishedAt, LEFT(x.Channels, 400), x.ViewerUsers, x.ViewerGroups, x.EditorUsers, x.EditorGroups, $inProd,
       LEFT(x.EnvironmentSku, 100), LEFT(x.Authentication, 200), x.IsQuarantined, x.IsWebSearchEnabled, x.IsAutonomous
FROM (SELECT *, ROW_NUMBER() OVER (PARTITION BY Id ORDER BY LastPublishedAt DESC) AS rn FROM #w) AS x
LEFT JOIN #m AS m ON m.Id = x.Id
WHERE x.rn = 1;

/* 2.5.7: agents of one name are told apart by their environment, and where they share that too, by their schema
   name: "HR Assistant (Sales Dev)". The name the workbook gives stays in its own table. */
WITH n AS (SELECT InventoryAgentId, AgentName, Environment, SchemaName,
                  COUNT(*) OVER (PARTITION BY LTRIM(RTRIM(AgentName))) AS SameName,
                  COUNT(*) OVER (PARTITION BY LTRIM(RTRIM(AgentName)), ISNULL(Environment, N'')) AS SameNameAndPlace
           FROM dash.AgentInventory WHERE NULLIF(LTRIM(RTRIM(AgentName)), N'') IS NOT NULL)
UPDATE n SET AgentName = LEFT(CONCAT(LTRIM(RTRIM(AgentName)), N' (', ISNULL(Environment, N'no environment'),
                                     CASE WHEN SameNameAndPlace > 1 THEN CONCAT(N', ', ISNULL(SchemaName, InventoryAgentId)) END, N')'), 400)
WHERE SameName > 1;
"@ $tx
            if ($pcCols) {
                $ic = $pcCols
                Sql @"
/* 2.5.7: each child by what it is, not by its name, which Copilot Studio gives every new child agent ("Agent").
   A child agent (RelationshipType without "Connected") lives inside its one parent: its component id. A connected
   agent is an agent of its own that several parents may call: its row in the inventory, found by its environment
   and the schema name SchemaName ends in (parent.InvokeConnectedAgentTaskAction.child), else that pair. */
SELECT COALESCE($(T 'ChildAgentName'), $(T 'ComponentName')) AS Child, $(T 'ParentAgentId') AS ParentId,
       $(T 'ParentAgentName') AS ParentName, $(T 'RelationshipType') AS Rel, $(T 'IsValidParentChildAgent') AS Valid,
       $(T 'ComponentId') AS CompId, $(T 'EnvironmentId') AS EnvId, $(T 'SchemaName') AS FullSchema, $(T 'EnvironmentSku') AS EnvSku
INTO #pc FROM $pc AS w;
DELETE FROM #pc WHERE Child IS NULL;

/* The parent's environment by name: the inventory's, else the sheet's SKU and the start of its id, between them a
   middle dot, NCHAR(183) ("Developer . 015d2539"), never a bare GUID. The script stays ASCII, as the analyzer asks. */
SELECT p.*, CASE WHEN p.Rel LIKE N'%connected%' THEN N'Connected' ELSE N'Child' END AS Kind,
       CASE WHEN p.FullSchema LIKE N'%.%' THEN RIGHT(p.FullSchema, CHARINDEX(N'.', REVERSE(p.FullSchema)) - 1) END AS ChildSchema,
       i.AgentKey AS ParentKey, COALESCE(NULLIF(i.AgentName, N''), p.ParentName, p.ParentId) AS ParentLabel,
       COALESCE(NULLIF(i.Environment, N''),
                NULLIF(CONCAT(p.EnvSku, CASE WHEN p.EnvSku IS NOT NULL AND p.EnvId IS NOT NULL THEN N' ' + NCHAR(183) + N' ' END, LEFT(p.EnvId, 8)), N''),
                N'no environment') AS ParentEnv
INTO #pk FROM #pc AS p LEFT JOIN dash.AgentInventory AS i ON i.InventoryAgentId = p.ParentId;
/* Parents of one name that are different agents (one Advisor in a developer and one in a production environment) are told apart
   by their environment, where the inventory has not already done so. */
UPDATE k SET ParentLabel = LEFT(CONCAT(k.ParentLabel, N' (', k.ParentEnv, N')'), 400)
FROM #pk AS k
WHERE k.ParentLabel IN (SELECT ParentLabel FROM #pk GROUP BY ParentLabel HAVING COUNT(DISTINCT ISNULL(ParentId, N'')) > 1);

SELECT k.*, ci.Id AS ChildInv,
       CASE WHEN k.Kind = N'Connected' THEN COALESCE(N'agent:' + ci.Id, N'connected:' + ISNULL(k.EnvId, N'') + N'/' + COALESCE(k.ChildSchema, k.Child))
            ELSE N'child:' + COALESCE(k.CompId, ISNULL(k.ParentId, N'') + N'/' + COALESCE(k.ChildSchema, k.Child)) END AS ChildKey
INTO #pl
FROM #pk AS k
OUTER APPLY (SELECT TOP (1) w.Id FROM #w AS w WHERE k.Kind = N'Connected' AND w.EnvironmentId = k.EnvId AND w.SchemaName = k.ChildSchema) AS ci;

/* One name for each child, told apart where children share one: a child agent by its parent, a connected agent by
   its environment, and where that is not enough, by its component id or schema name. */
SELECT l.ChildKey, MIN(COALESCE(NULLIF(i.AgentName, N''), l.Child)) AS BaseName, MIN(l.Kind) AS Kind,
       MIN(l.ParentLabel) AS ParentLabel, MIN(l.ParentEnv) AS ParentEnv, MIN(COALESCE(l.CompId, l.ChildSchema, l.ChildKey)) AS Extra,
       CAST(NULL AS NVARCHAR(400)) AS Display
INTO #ck FROM #pl AS l LEFT JOIN dash.AgentInventory AS i ON i.InventoryAgentId = l.ChildInv GROUP BY l.ChildKey;
WITH n AS (SELECT Display, BaseName, Kind, ParentLabel, ParentEnv, COUNT(*) OVER (PARTITION BY BaseName) AS SameName FROM #ck)
UPDATE n SET Display = LEFT(CASE WHEN SameName = 1 THEN BaseName
                                 WHEN Kind = N'Child' THEN CONCAT(BaseName, N' (in ', ParentLabel, N')')
                                 ELSE CONCAT(BaseName, N' (', ISNULL(ParentEnv, N'no environment'), N')') END, 400);
WITH n AS (SELECT Display, Extra, COUNT(*) OVER (PARTITION BY Display) AS SameDisplay FROM #ck)
UPDATE n SET Display = LEFT(CONCAT(Display, N' ' + NCHAR(183) + N' ', Extra), 400) WHERE SameDisplay > 1;

DELETE FROM dash.AgentChildLink;
INSERT INTO dash.AgentChildLink (ChildAgentName, ParentInventoryAgentId, ParentAgentName, ParentAgentKey, RelationshipType, IsValid,
                                 ChildKey, ChildKind, ChildInventoryAgentId, ParentEnvironment)
SELECT c.Display, LEFT(l.ParentId, 100), LEFT(l.ParentLabel, 400), l.ParentKey, LEFT(l.Rel, 200),
       CASE WHEN l.Valid IN (N'Yes', N'True', N'1') THEN 1 WHEN l.Valid IN (N'No', N'False', N'0') THEN 0 END,
       LEFT(l.ChildKey, 450), l.Kind, l.ChildInv, LEFT(l.ParentEnv, 400)
FROM #pl AS l JOIN #ck AS c ON c.ChildKey = l.ChildKey;
"@ $tx
            }
            # 2.5.5 named the agents in Admin's own table; from 2.5.6 the inventory does.
            Sql "DELETE FROM dash.AgentAlias WHERE UpdatedBy = N'Agent workbook';" $tx
            $tx.Commit()
        }
        catch { $tx.Rollback(); throw }
        Sql "IF OBJECT_ID(N'bi.usp_NameAgents', N'P') IS NOT NULL EXEC bi.usp_NameAgents;" $null
        $matched = Sql 'SELECT COUNT(*) FROM dash.AgentInventory WHERE AgentKey IS NOT NULL' $null -Scalar
        $total = Sql 'SELECT COUNT(*) FROM dash.AgentInventory' $null -Scalar
        Write-Host "Agent inventory: $total agent(s), $matched of them in the audit log."
        # How each was matched, for checking the rules against the data (2.5.7).
        $how = Sql @"
SELECT STRING_AGG(CONCAT(CASE Rnk WHEN 1 THEN N'environment and schema name' WHEN 2 THEN N'title id (declarative)'
                                  WHEN 3 THEN N'schema name from Copilot Studio''s authoring records' WHEN 4 THEN N'schema name alone'
                                  WHEN 5 THEN N'bot id' ELSE N'Entra agent id' END, N' ', FORMAT(n, 'N0')), N'; ') WITHIN GROUP (ORDER BY Rnk)
FROM (SELECT Rnk, COUNT(*) AS n FROM #m GROUP BY Rnk) AS x;
"@ $null -Scalar
        if ($how -and $how -isnot [DBNull]) { Write-Host "  matched by: $how." }
        $loose = Sql "SELECT COUNT(DISTINCT p.Id) FROM #p AS p WHERE NOT EXISTS (SELECT 1 FROM #m AS m WHERE m.Id = p.Id);" $null -Scalar
        if ($loose) { Write-Host "  $loose agent(s) left unmatched: their schema name is in more than one environment, or another agent of the workbook claims the same one." }
    }

    if ($runId) {
        Sql @"
UPDATE dash.AgentInventoryRun SET EndedUtc = SYSUTCDATETIME(), Status = N'Succeeded', Source = $(Q $source),
       FileCreatedUtc = $(if ($created) { "'" + $created.ToUniversalTime().ToString('yyyy-MM-dd HH:mm:ss') + "'" } else { 'NULL' }),
       InventoryRows = $(if ($null -ne $loaded['AgentInventory']) { $loaded['AgentInventory'] } else { 'NULL' }),
       ProductionRows = $(if ($null -ne $loaded['AgentsInProd']) { $loaded['AgentsInProd'] } else { 'NULL' }),
       ChildLinkRows = $(if ($null -ne $loaded['ParentChildAgent']) { $loaded['ParentChildAgent'] } else { 'NULL' }),
       Matched = $(if ($null -ne $matched) { $matched } else { 'NULL' })
WHERE RunId = $runId;
"@ $null
    }
}
catch {
    if ($runId) { Sql "UPDATE dash.AgentInventoryRun SET EndedUtc = SYSUTCDATETIME(), Status = N'Failed', Message = $(Q ([string] $_.Exception.Message).Substring(0, [Math]::Min(2000, ([string] $_.Exception.Message).Length))) WHERE RunId = $runId;" $null }
    throw
}
finally { $conn.Dispose(); Remove-Item -LiteralPath $file -ErrorAction SilentlyContinue }
```

### C.22 `dashboard/Install-CopilotInsights.ps1`

The dashboard's installer on the web server. With `-GrantDatabaseAccess` it runs the three dashboard scripts and adds the site's account to its database role. 532 lines.

```powershell
<#
.SYNOPSIS
    Installs or updates the Copilot Insights dashboard on IIS (Windows Server 2022 or later).

.DESCRIPTION
    Idempotent: run it again to update the site files or change its settings.
    A setting not given again keeps what the site has (2.4.12): the app pool's
    account, the database and TrustServerCertificate in the connection string,
    the tenants, the administrators, the folder and the bindings.

      1. Installs the IIS role services the site needs (Windows authentication,
         static content, logging, request filtering, the management console).
      2. Checks for the ASP.NET Core 10 Hosting Bundle, installing it from
         -HostingBundlePath when given.
      3. Publishes the app (from source, when -PackagePath is a folder holding
         CopilotInsights.csproj) or copies a published folder or zip into
         -PhysicalPath, keeping appsettings.Production.json.
      4. Writes appsettings.Production.json: the connection string and the
         bootstrap administrators (-AdminPrincipal), the first people who can
         open Admin and grant everyone else.
      5. Creates the app pool (no managed code, the given identity) and the
         site, with an HTTPS binding when -CertificateThumbprint is given.
      6. Turns Windows authentication on and anonymous authentication off.
      7. With -GrantDatabaseAccess (you need to be able to create logins):
         installs or upgrades the dashboard's dash schema in the database,
         builds its org chart and rollups, and grants the app-pool account
         what it needs. Without it, writes the grants for a DBA instead.
      8. Schedules the daily pre-cache (2.5.1): Invoke-InsightsPrecache.ps1,
         copied into the site's folder, builds every report page ahead at
         -PrecacheAt in -PrecacheTimeZone, 07:00 Eastern Time unless given.

    The site is offline from step 3 to step 6, and back online whether they
    succeed or fail. Its inputs are checked before it goes offline.

    The dashboard reads the database the pipeline fills (Install-CopilotAudit.ps1).
    The top of the organization is found in the org chart; -OrgTopUpn or
    Admin > Access overrides it. Nothing needs setting by hand in SQL.

.PARAMETER PackagePath
    The app to install: a folder holding CopilotInsights.csproj (published with
    dotnet publish, which needs the .NET 10 SDK), a published folder, or a zip of one.

.PARAMETER Tenant
    For a group with several Microsoft 365 tenants (tools\Add-CopilotTenant.ps1):
    each as Name=Database, on -SqlServer, the first being -Database's own name,
    such as 'Contoso=CopilotAudit','Fabrikam=CopilotAudit_Fabrikam'. Written to
    Insights:Tenants; the dashboard shows a tenant switcher and a group overview.
    With -GrantDatabaseAccess, the site's account is given access in each.

.PARAMETER AdminPrincipal
    Bootstrap administrators: UPNs, DOMAIN\user or, better, DOMAIN\Group. They are
    administrators whatever the database says; keep the list short.

.PARAMETER AppPoolIdentity
    The account the site runs as, and signs in to SQL Server as. A group managed
    service account (DOMAIN\name$, no password) is best. Omitted: the account the
    app pool runs as already, or for a new pool its own identity, which reaches a
    remote SQL Server as the machine account (DOMAIN\HOST$). An empty value
    (-AppPoolIdentity '') goes back to the pool's own identity.

.PARAMETER PrecacheAt
    When the scheduled task 'Copilot Insights pre-cache' builds every report page ahead each day, as HH:mm
    in -PrecacheTimeZone (2.5.1). Omitted: a task already scheduled is kept as it is, and a new one runs at
    07:00. An empty value (-PrecacheAt '') removes the task.

.PARAMETER PrecacheTimeZone
    The Windows time zone of -PrecacheAt. Default 'Eastern Standard Time': Eastern Time, EST in winter
    and EDT in summer, whatever zone the server's clock is in.

.EXAMPLE
    .\Install-CopilotInsights.ps1 -PackagePath .\CopilotInsights -SqlServer sql01 -Database CopilotAudit `
        -AdminPrincipal 'CONTOSO\Copilot-Insights-Admins' -AppPoolIdentity 'CONTOSO\gmsa-insights$' `
        -HostName insights.contoso.com -CertificateThumbprint 3F2A... -GrantDatabaseAccess
.EXAMPLE
    .\Install-CopilotInsights.ps1 -PackagePath .\copilot-insights-v2.5.4.zip -SqlServer sql01   # an update: the rest is kept
#>
#Requires -Version 5.1
#Requires -RunAsAdministrator
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $PackagePath,
    [Parameter(Mandatory)][string] $SqlServer,
    [string] $Database = 'CopilotAudit',
    [string[]] $AdminPrincipal = @(),
    [string] $SiteName = 'CopilotInsights',
    [string] $PhysicalPath = 'C:\inetpub\CopilotInsights',
    [string] $HostName = '',
    [int] $Port = 0,
    [string] $CertificateThumbprint,
    [string] $AppPoolIdentity,
    [pscredential] $AppPoolCredential,
    [string] $HostingBundlePath,
    [switch] $TrustServerCertificate,
    [switch] $GrantDatabaseAccess,
    [string] $OrgTopUpn,
    [string[]] $Tenant = @(),
    [switch] $SkipFeatureInstall,
    [string] $PrecacheAt = '07:00',
    [string] $PrecacheTimeZone = 'Eastern Standard Time'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Write-Step([string] $Text) { Write-Host "==> $Text" -ForegroundColor Cyan }

function ConvertTo-Thumbprint {
    <#
        A certificate thumbprint as the store names it (2.4.12): hex digits only, in capitals. The certificate dialog
        copies it with spaces and an invisible mark in front, which made the lookup fail. Pure, for the tests.
    #>
    param([string] $Value)
    ($Value -replace '[^0-9A-Fa-f]', '').ToUpperInvariant()
}

function ConvertTo-TenantList {
    <# -Tenant's Name=Database pairs as Insights:Tenants entries and their databases, or an error naming the one that is not. Pure, for the tests. #>
    param([string[]] $Tenant, [string] $SqlServer, [string] $Trust)
    $entries = @(); $databases = @()
    foreach ($t in $Tenant) {
        if ($t -notmatch '^\s*([^=]+?)\s*=\s*([A-Za-z0-9_\-]+)\s*$') { throw "-Tenant '$t' is not Name=Database." }
        $databases += $Matches[2]
        $entries += [pscustomobject] @{ Name = $Matches[1]; ConnectionString = "Server=$SqlServer;Database=$($Matches[2]);Integrated Security=true;Encrypt=true;TrustServerCertificate=$Trust" }
    }
    if (@($entries | ForEach-Object { $_.Name } | Sort-Object -Unique).Count -ne $entries.Count) { throw '-Tenant names must be different.' }
    [pscustomobject] @{ Entries = $entries; Databases = $databases }
}

function Resolve-SiteSetting {
    <#
        What this run installs with (2.4.12): each setting given now, else what the site has already, else the
        default. A re-run without every first-run parameter reset them, though the documents said the settings were
        kept: the app pool went back to its own identity (DOMAIN\HOST$ at a remote SQL Server, which had no grants, or
        was given the site's), the connection string to CopilotAudit without TrustServerCertificate, Kerberos off, and
        a plain HTTP binding on port 80 appeared beside the HTTPS one. Pure, for the tests.
    #>
    param(
        [string[]] $Given,                 # the parameters given this time
        [string] $ConnectionString,        # the site's, from appsettings.Production.json; empty for a new site
        [string] $PoolIdentityType,        # the app pool's processModel.identityType; empty for a new pool
        [string] $PoolUserName,
        [bool] $SiteExists,
        [string] $Database, [bool] $TrustServerCertificate, [string] $AppPoolIdentity
    )
    $r = [ordered] @{ Database = $Database; TrustServerCertificate = $TrustServerCertificate; AppPoolIdentity = $AppPoolIdentity
                      KeepPoolIdentity = $false; NewBinding = $true }
    if ($ConnectionString) {
        $b = New-Object System.Data.SqlClient.SqlConnectionStringBuilder $ConnectionString
        if ($Given -notcontains 'Database' -and $b.InitialCatalog) { $r.Database = $b.InitialCatalog }
        if ($Given -notcontains 'TrustServerCertificate') { $r.TrustServerCertificate = [bool] $b.TrustServerCertificate }
    }
    # The pool's own account when none is given; -AppPoolIdentity '' asks for the pool's own identity.
    if ($Given -notcontains 'AppPoolIdentity' -and "$PoolIdentityType" -in 'SpecificUser', '3' -and $PoolUserName) {
        $r.AppPoolIdentity = $PoolUserName
        $r.KeepPoolIdentity = $true
    }
    # A binding for a new site, or when one is asked for: a certificate, a host name or a port.
    $r.NewBinding = -not $SiteExists -or $Given -contains 'CertificateThumbprint' -or $Given -contains 'HostName' -or $Given -contains 'Port'
    [pscustomobject] $r
}

# ---- 0. Where we are ------------------------------------------------------------------------
# The IIS:\ drive exists only in Windows PowerShell; PowerShell 7 loads WebAdministration without it.
if ($PSVersionTable.PSEdition -ne 'Desktop') { throw 'Run this in Windows PowerShell 5.1 (powershell.exe), not PowerShell 7: the IIS provider it uses is not available there.' }
$os = Get-CimInstance Win32_OperatingSystem
if ($os.ProductType -eq 1) { throw 'This installs on Windows Server (2022 or later); for a workstation, run the app with dotnet run.' }
if ([int] $os.BuildNumber -lt 20348) { Write-Warning "Windows Server 2022 (build 20348) or later is supported; this is build $($os.BuildNumber)." }
if ($AppPoolIdentity -and -not $AppPoolIdentity.EndsWith('$') -and -not $AppPoolCredential) {
    throw "-AppPoolIdentity $AppPoolIdentity is not a managed service account (DOMAIN\name$): give its password with -AppPoolCredential."
}

# ---- 1. IIS -----------------------------------------------------------------------------------
if (-not $SkipFeatureInstall) {
    Write-Step 'IIS role services'
    # Web-AppInit starts the site with IIS, so email alerts and digests go out with nobody signed in.
    $features = 'Web-Server', 'Web-Default-Doc', 'Web-Static-Content', 'Web-Http-Errors', 'Web-Http-Logging',
                'Web-Filtering', 'Web-Windows-Auth', 'Web-Mgmt-Console', 'Web-AppInit'
    $r = Install-WindowsFeature -Name $features
    if ($r.RestartNeeded -eq 'Yes') { Write-Warning 'Windows asks for a restart to finish installing IIS. Restart, then run this again.' }
}
Import-Module WebAdministration

# ---- What the site has, and the inputs, before anything changes --------------------------------
# A setting not given again keeps what the site has (Resolve-SiteSetting). Every input is checked here, before step 3
# takes the site offline (2.4.12): a thumbprint the store does not hold, a -Tenant that is not Name=Database, a
# settings file that does not read, threw after the files were copied, with the site left offline.
$poolPath = "IIS:\AppPools\$SiteName"
$siteBefore = Get-Website -Name $SiteName
if ($siteBefore -and -not $PSBoundParameters.ContainsKey('PhysicalPath')) { $PhysicalPath = [Environment]::ExpandEnvironmentVariables($siteBefore.physicalPath) }
$settingsPath = Join-Path $PhysicalPath 'appsettings.Production.json'
$settings = if (Test-Path $settingsPath) { Get-Content $settingsPath -Raw | ConvertFrom-Json } else { [pscustomobject] @{} }
$before = if ($settings.PSObject.Properties['Insights'] -and $settings.Insights.PSObject.Properties['ConnectionString']) { [string] $settings.Insights.ConnectionString } else { '' }
$poolType = ''; $poolUser = ''
if (Test-Path $poolPath) {
    $pm = Get-ItemProperty $poolPath -Name processModel
    $poolType = [string] $pm.identityType; $poolUser = [string] $pm.userName
}
$site = Resolve-SiteSetting -Given @($PSBoundParameters.Keys) -ConnectionString $before -PoolIdentityType $poolType -PoolUserName $poolUser `
    -SiteExists ([bool] $siteBefore) -Database $Database -TrustServerCertificate ([bool] $TrustServerCertificate) -AppPoolIdentity $AppPoolIdentity
$Database = $site.Database
$AppPoolIdentity = $site.AppPoolIdentity
$trust = if ($site.TrustServerCertificate) { 'true' } else { 'false' }
if ($siteBefore) {
    Write-Host ("    keeping what is not given again: database {0}, TrustServerCertificate {1}, app pool account {2}" -f $Database, $trust,
                $(if ($AppPoolIdentity) { $AppPoolIdentity } else { 'its own identity' }))
}
if ($AppPoolIdentity -and -not $site.KeepPoolIdentity) {
    try { [void] ([System.Security.Principal.NTAccount] $AppPoolIdentity).Translate([System.Security.Principal.SecurityIdentifier]) }
    catch { throw "-AppPoolIdentity ${AppPoolIdentity}: no such account here ($($_.Exception.Message)). Nothing was changed." }
}
if ($CertificateThumbprint) {
    $CertificateThumbprint = ConvertTo-Thumbprint $CertificateThumbprint
    if (-not (Test-Path "Cert:\LocalMachine\My\$CertificateThumbprint")) { throw "No certificate $CertificateThumbprint in LocalMachine\My. Nothing was changed." }
}
# The daily pre-cache (2.5.1): its time and zone, and the script it runs, which this copies into the site.
$precacheScript = Join-Path $PSScriptRoot 'Invoke-InsightsPrecache.ps1'
if ($PrecacheAt -and $PrecacheAt -notmatch '^([01]?\d|2[0-3]):[0-5]\d$') { throw "-PrecacheAt '$PrecacheAt' is not a time of day: give it as HH:mm, such as 07:00, or '' for no pre-cache. Nothing was changed." }
try { [void] [TimeZoneInfo]::FindSystemTimeZoneById($PrecacheTimeZone) }
catch { throw "-PrecacheTimeZone '$PrecacheTimeZone' is not a Windows time zone here (Get-TimeZone -ListAvailable lists them). Nothing was changed." }
if (-not (Test-Path $precacheScript)) { Write-Warning "No $precacheScript beside this installer: the daily pre-cache is not scheduled." }
# Several tenants: Name=Database on the same server. Without -Tenant, the list already there is kept, and so are the
# databases the grants go to.
$tenantList = $null
$tenantDbs = @()
if ($Tenant.Count -gt 0) {
    $tenantList = ConvertTo-TenantList -Tenant $Tenant -SqlServer $SqlServer -Trust $trust
    $tenantDbs = @($tenantList.Databases)
}
elseif ($settings.PSObject.Properties['Insights'] -and $settings.Insights.PSObject.Properties['Tenants']) {
    $tenantDbs = @(@($settings.Insights.Tenants) | Where-Object { $_ -and $_.PSObject.Properties['ConnectionString'] } |
                   ForEach-Object { (New-Object System.Data.SqlClient.SqlConnectionStringBuilder ([string] $_.ConnectionString)).InitialCatalog } | Where-Object { $_ })
}

# ---- 2. ASP.NET Core Hosting Bundle -------------------------------------------------------------
Write-Step 'ASP.NET Core 10 Hosting Bundle'
function Test-HostingBundle {
    $module = Join-Path $env:ProgramFiles 'IIS\Asp.Net Core Module\V2\aspnetcorev2.dll'
    $fw = Join-Path $env:ProgramFiles 'dotnet\shared\Microsoft.AspNetCore.App'
    (Test-Path $module) -and (Test-Path $fw) -and [bool] (Get-ChildItem $fw -Directory | Where-Object Name -like '10.*')
}
if (-not (Test-HostingBundle)) {
    if (-not $HostingBundlePath) {
        throw 'The ASP.NET Core 10 Hosting Bundle is not installed. Download "Hosting Bundle" for .NET 10 from https://dotnet.microsoft.com/download/dotnet/10.0 and pass it with -HostingBundlePath, or install it and run this again.'
    }
    $p = Start-Process -FilePath $HostingBundlePath -ArgumentList '/install', '/quiet', '/norestart' -Wait -PassThru
    if ($p.ExitCode -notin 0, 3010) { throw "The Hosting Bundle installer failed with exit code $($p.ExitCode)." }
    if (-not (Test-HostingBundle)) { throw 'The Hosting Bundle installed, but the ASP.NET Core module or the 10.x runtime is still missing.' }
    & net stop was /y | Out-Null; & net start w3svc | Out-Null
}

# ---- 3. The files -----------------------------------------------------------------------------
Write-Step "Site files in $PhysicalPath"
$staging = Join-Path ([IO.Path]::GetTempPath()) ("CopilotInsights-" + [guid]::NewGuid().ToString('N'))
$offline = Join-Path $PhysicalPath 'app_offline.htm'
New-Item -ItemType Directory -Force $staging | Out-Null
try {
    $src = (Resolve-Path $PackagePath).Path
    if ((Get-Item $src).PSIsContainer -and (Test-Path (Join-Path $src 'CopilotInsights.csproj'))) {
        if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw 'Publishing from source needs the .NET 10 SDK; or pass a published folder or zip.' }
        & dotnet publish (Join-Path $src 'CopilotInsights.csproj') -c Release -o $staging --nologo
        if ($LASTEXITCODE) { throw 'dotnet publish failed.' }
    }
    elseif ($src -like '*.zip') { Expand-Archive -Path $src -DestinationPath $staging -Force }
    else { Copy-Item -Path (Join-Path $src '*') -Destination $staging -Recurse -Force }
    # The release's zip holds the app in a CopilotInsights folder: use the folder the app is in.
    if (-not (Test-Path (Join-Path $staging 'CopilotInsights.dll'))) {
        $inner = Get-ChildItem $staging -Directory | Where-Object { Test-Path (Join-Path $_.FullName 'CopilotInsights.dll') } | Select-Object -First 1
        if ($inner) {
            Get-ChildItem $inner.FullName -Force | Move-Item -Destination $staging -Force
            Remove-Item $inner.FullName -Recurse -Force
        }
    }
    if (-not (Test-Path (Join-Path $staging 'CopilotInsights.dll'))) { throw "No CopilotInsights.dll in ${PackagePath}: is it the published app?" }
    # The daily pre-cache's script goes into the site's folder with the app (2.5.1), where the app pool only reads it.
    if (Test-Path $precacheScript) { Copy-Item -LiteralPath $precacheScript -Destination $staging -Force }

    New-Item -ItemType Directory -Force $PhysicalPath, (Join-Path $PhysicalPath 'logs'), (Join-Path $PhysicalPath 'precache') | Out-Null
    # Stop the site while its files change: app_offline.htm makes the module unload the app.
    Set-Content $offline '<p>Copilot Insights is being updated. Try again in a minute.</p>'
    Start-Sleep -Seconds 2
    & robocopy $staging $PhysicalPath /MIR /XF appsettings.Production.json app_offline.htm /XD logs precache /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "Copying the site files failed (robocopy $LASTEXITCODE)." }

    # ---- 4. appsettings.Production.json -------------------------------------------------------
    Write-Step 'Settings'
    function Set-Prop($Object, [string] $Name, $Value) {
        if ($Object.PSObject.Properties[$Name]) { $Object.$Name = $Value } else { $Object | Add-Member -NotePropertyName $Name -NotePropertyValue $Value }
    }
    if (-not $settings.PSObject.Properties['Insights']) { Set-Prop $settings 'Insights' ([pscustomobject] @{}) }
    Set-Prop $settings.Insights 'ConnectionString' "Server=$SqlServer;Database=$Database;Integrated Security=true;Encrypt=true;TrustServerCertificate=$trust"
    # The site connects the same way, so a certificate SQL Server's clients do not trust shows here, not as
    # a "Something went wrong" page later. Signed in as whoever runs this, so only the handshake is judged.
    $probe = New-Object System.Data.SqlClient.SqlConnection "Server=$SqlServer;Database=master;Integrated Security=true;Encrypt=true;TrustServerCertificate=$trust;Connect Timeout=15"
    try { $probe.Open() }
    catch {
        if ($_.Exception.Message -match 'certificate|SSL Provider|TLS|principal name') {
            Write-Warning "The encrypted connection to $SqlServer failed: $($_.Exception.GetBaseException().Message) The site would fail the same way. If SQL Server uses its own self-signed certificate, run this again with -TrustServerCertificate."
        }
    }
    finally { $probe.Dispose() }
    if ($AdminPrincipal.Count -gt 0) { Set-Prop $settings.Insights 'BootstrapAdmins' @($AdminPrincipal) }
    elseif (-not $settings.Insights.PSObject.Properties['BootstrapAdmins'] -or @($settings.Insights.BootstrapAdmins).Count -eq 0) {
        Write-Warning 'No -AdminPrincipal: nobody can open Admin until a row is added to dash.RoleGrant (see the RUNBOOK).'
    }
    if ($tenantList) { Set-Prop $settings.Insights 'Tenants' @($tenantList.Entries) }
    # Before 2.3.0 a Development-only setting let an administrator view as someone else; the site no longer has it.
    if ($settings.Insights.PSObject.Properties['AllowViewAs']) { $settings.Insights.PSObject.Properties.Remove('AllowViewAs') }
    $settings | ConvertTo-Json -Depth 5 | Set-Content $settingsPath -Encoding utf8

    # ---- 5. App pool and site -----------------------------------------------------------------
    Write-Step "App pool and site '$SiteName'"
    if (-not (Test-Path $poolPath)) { New-WebAppPool -Name $SiteName | Out-Null }
    Set-ItemProperty $poolPath -Name managedRuntimeVersion -Value ''
    Set-ItemProperty $poolPath -Name managedPipelineMode -Value 'Integrated'
    Set-ItemProperty $poolPath -Name startMode -Value 'AlwaysRunning'
    Set-ItemProperty $poolPath -Name processModel.loadUserProfile -Value $true
    Set-ItemProperty $poolPath -Name processModel.idleTimeout -Value ([TimeSpan]::Zero)
    if ($site.KeepPoolIdentity) {
        # Left as it is, its stored password with it.
        $poolAccount = $AppPoolIdentity
    }
    elseif ($AppPoolIdentity) {
        $pw = if ($AppPoolCredential) { $AppPoolCredential.GetNetworkCredential().Password } else { '' }
        Set-ItemProperty $poolPath -Name processModel -Value @{ userName = $AppPoolIdentity; password = $pw; identityType = 3 }
        $poolAccount = $AppPoolIdentity
    }
    else {
        Set-ItemProperty $poolPath -Name processModel.identityType -Value 4   # ApplicationPoolIdentity
        $poolAccount = "IIS APPPOOL\$SiteName"
    }

    $https = [bool] $CertificateThumbprint
    if (-not $Port) { $Port = if ($https) { 443 } else { 80 } }
    if (-not $siteBefore) {
        New-Website -Name $SiteName -PhysicalPath $PhysicalPath -ApplicationPool $SiteName -Port $Port -HostHeader $HostName -Ssl:$https | Out-Null
    }
    else {
        Set-ItemProperty "IIS:\Sites\$SiteName" -Name physicalPath -Value $PhysicalPath
        Set-ItemProperty "IIS:\Sites\$SiteName" -Name applicationPool -Value $SiteName
    }
    # Start the app with IIS, not at the first visit: its background sender runs the alerts and digests.
    Set-ItemProperty "IIS:\Sites\$SiteName" -Name applicationDefaults.preloadEnabled -Value $true

    # Alerts also go to the Application log under this source; creating a source needs an administrator.
    if (-not [System.Diagnostics.EventLog]::SourceExists('Copilot Insights')) {
        New-EventLog -LogName Application -Source 'Copilot Insights'
    }
    if ($https) {
        $proto = 'https'
        if (-not (Get-WebBinding -Name $SiteName -Protocol https -Port $Port -HostHeader $HostName)) {
            New-WebBinding -Name $SiteName -Protocol https -Port $Port -HostHeader $HostName -SslFlags $(if ($HostName) { 1 } else { 0 })
        }
        elseif ($HostName -and (Get-WebBinding -Name $SiteName -Protocol https -Port $Port -HostHeader $HostName).sslFlags -ne 1) {
            # New-Website -Ssl makes the binding without SNI; a host-name binding needs it to share port 443.
            Set-WebBinding -Name $SiteName -BindingInformation "*:${Port}:$HostName" -PropertyName sslFlags -Value 1
        }
        (Get-WebBinding -Name $SiteName -Protocol https -Port $Port -HostHeader $HostName).AddSslCertificate($CertificateThumbprint, 'My')
    }
    elseif ($site.NewBinding) {
        $proto = 'http'
        # New-Website makes the first binding; a site first installed with HTTPS has none for HTTP.
        if (-not (Get-WebBinding -Name $SiteName -Protocol http -Port $Port -HostHeader $HostName)) {
            New-WebBinding -Name $SiteName -Protocol http -Port $Port -HostHeader $HostName
        }
        Write-Warning 'No -CertificateThumbprint: the site answers on HTTP only. Windows authentication still protects it, but use HTTPS in production.'
    }
    else {
        # No binding asked for: the site keeps its own (2.4.12; a re-run without -CertificateThumbprint added HTTP on
        # port 80 beside the HTTPS one). The address below is its HTTPS binding's, or its HTTP one's.
        $kept = @(Get-WebBinding -Name $SiteName | Sort-Object { if ($_.protocol -eq 'https') { 0 } else { 1 } })
        $proto = 'http'
        if ($kept) {
            $parts = ([string] $kept[0].bindingInformation).Split(':')
            $proto = [string] $kept[0].protocol; $Port = [int] $parts[1]; $HostName = $parts[2]
        }
        Write-Host "    bindings kept: $((@($kept) | ForEach-Object { "$($_.protocol) $($_.bindingInformation)" }) -join ', ')"
    }
    $https = $proto -eq 'https'

    # ---- 6. Authentication --------------------------------------------------------------------
    Write-Step 'Windows authentication on, anonymous off'
    Set-WebConfigurationProperty -PSPath 'IIS:\' -Location $SiteName -Filter 'system.webServer/security/authentication/anonymousAuthentication' -Name enabled -Value $false
    Set-WebConfigurationProperty -PSPath 'IIS:\' -Location $SiteName -Filter 'system.webServer/security/authentication/windowsAuthentication' -Name enabled -Value $true
    # Kerberos for a host name registered on the app-pool account (the SPN below) needs the pool's own credentials.
    Set-WebConfigurationProperty -PSPath 'IIS:\' -Location $SiteName -Filter 'system.webServer/security/authentication/windowsAuthentication' -Name useAppPoolCredentials -Value ([bool] $AppPoolIdentity)

    # The app pool writes its logs; everyone else only reads the site.
    $acl = Get-Acl (Join-Path $PhysicalPath 'logs')
    $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule $poolAccount, 'Modify', 'ContainerInherit,ObjectInherit', 'None', 'Allow'))
    Set-Acl (Join-Path $PhysicalPath 'logs') $acl
    # The pre-cache folder (2.5.1): the scheduled task, as LOCAL SERVICE, leaves a request and its log there; the app
    # pool takes the request and writes its answer. Neither can change the script the task runs, in the site's folder.
    $acl = Get-Acl (Join-Path $PhysicalPath 'precache')
    foreach ($who in $poolAccount, 'NT AUTHORITY\LOCAL SERVICE') {
        $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule $who, 'Modify', 'ContainerInherit,ObjectInherit', 'None', 'Allow'))
    }
    Set-Acl (Join-Path $PhysicalPath 'precache') $acl
    $acl = Get-Acl $PhysicalPath
    $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule $poolAccount, 'ReadAndExecute', 'ContainerInherit,ObjectInherit', 'None', 'Allow'))
    Set-Acl $PhysicalPath $acl
}
finally {
    Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue
    # Back online whether steps 3 to 6 finished or not (2.4.12): only the last step removed it, so a run that failed
    # after the files were copied left every user on "being updated" until a run succeeded.
    Remove-Item $offline -ErrorAction SilentlyContinue
}
Start-WebAppPool -Name $SiteName -ErrorAction SilentlyContinue
Restart-WebAppPool -Name $SiteName -ErrorAction SilentlyContinue

# ---- 7. Database access -----------------------------------------------------------------------
Write-Step 'Database access'
$sqlLogin = if ($AppPoolIdentity) { $AppPoolIdentity }
            elseif ($SqlServer -match '^(\.|localhost|\(local\)|' + [regex]::Escape($env:COMPUTERNAME) + ')(\\|,|$)') { $poolAccount }
            else { "$env:USERDOMAIN\$env:COMPUTERNAME`$" }
$grants = @"
-- Copilot Insights: the app-pool account $sqlLogin reads the reports and keeps the dash tables.
USE [master];
IF SUSER_ID(N'$sqlLogin') IS NULL CREATE LOGIN [$sqlLogin] FROM WINDOWS;
USE [$Database];
-- The login may already be in the database under another name: as dbo when it owns the
-- database, or as a user a DBA added. The grants go to that user; the owner needs none.
DECLARE @u sysname = (SELECT name FROM sys.database_principals WHERE sid = SUSER_SID(N'$sqlLogin'));
IF @u IS NULL BEGIN CREATE USER [$sqlLogin] FOR LOGIN [$sqlLogin]; SET @u = N'$sqlLogin'; END;
IF @u = N'dbo' PRINT N'$sqlLogin owns this database, so it already has every right the site needs.';
ELSE
BEGIN
    DECLARE @g nvarchar(max) = N'
GRANT SELECT ON SCHEMA::bi TO {u};
GRANT SELECT ON SCHEMA::dash TO {u};
GRANT INSERT, UPDATE, DELETE ON dash.Setting TO {u};
GRANT INSERT, UPDATE, DELETE ON dash.RoleGrant TO {u};
GRANT INSERT, UPDATE, DELETE ON dash.AgentAlias TO {u};
GRANT INSERT, UPDATE, DELETE ON dash.SensitivityLabel TO {u};
GRANT INSERT ON dash.AuditLog TO {u};
GRANT EXECUTE ON dash.usp_RefreshDashboard TO {u};
GRANT SELECT, INSERT, UPDATE ON audit.SessionAppCategory TO {u};
-- Every grant the site needs, kept up to date by Create-CopilotOperationsSchema.sql.
IF DATABASE_PRINCIPAL_ID(N''CopilotInsightsApp'') IS NOT NULL ALTER ROLE CopilotInsightsApp ADD MEMBER {u};';
    SET @g = REPLACE(@g, N'{u}', QUOTENAME(@u));
    EXEC (@g);
    -- Admin > Health reads the pipeline's run logs, where they exist.
    DECLARE @t sysname, @sql nvarchar(400);
    DECLARE t CURSOR LOCAL FAST_FORWARD FOR
        SELECT name FROM (VALUES (N'CopilotLoad'), (N'CopilotCsvImport'), (N'CopilotSessionRun'), (N'UserDirectory')) AS v(name)
        WHERE OBJECT_ID(N'audit.' + name, N'U') IS NOT NULL;
    OPEN t; FETCH NEXT FROM t INTO @t;
    WHILE @@FETCH_STATUS = 0
    BEGIN
        SET @sql = N'GRANT SELECT ON audit.' + QUOTENAME(@t) + N' TO ' + QUOTENAME(@u) + N';';
        EXEC (@sql);
        FETCH NEXT FROM t INTO @t;
    END;
END;
"@
# The same grants in every tenant's database.
$databases = @(@($Database) + $tenantDbs | Select-Object -Unique)
$grantsFor = { param($db) $grants.Replace("USE [$Database];", "USE [$db];") }
$grantsFile = Join-Path $PhysicalPath 'Grant-CopilotInsightsAccess.sql'
# GO between databases: each one's grants declare the same variables.
Set-Content $grantsFile (($databases | ForEach-Object { & $grantsFor $_ }) -join "`r`nGO`r`n") -Encoding utf8
if ($GrantDatabaseAccess) {
  foreach ($db in $databases) {
    # The dash schema first: the grants below name its tables.
    $repoSql = Join-Path (Split-Path $PSScriptRoot -Parent) 'sql'
    $dbConnString = "Server=$SqlServer;Database=$db;Integrated Security=true;Encrypt=true;TrustServerCertificate=$trust"
    # One run at a time (2.4.12), as the nightly pipeline, Install-CopilotAudit.ps1 and the maintenance rebuilds hold it:
    # the schema scripts and a full dashboard refresh below ran beside a nightly's refresh of the same tables.
    $runLock = $null
    if ($env:CopilotAuditRunLock -ne "$SqlServer/$db") {
        $lb = New-Object System.Data.SqlClient.SqlConnectionStringBuilder $dbConnString
        $lb['Pooling'] = $false   # closing a pooled connection keeps its session, and the session's lock with it
        $runLock = New-Object System.Data.SqlClient.SqlConnection $lb.ConnectionString
        try {
            $runLock.Open()
            $lc = $runLock.CreateCommand()
            $lc.CommandText = "DECLARE @r INT; EXEC @r = sp_getapplock @Resource = N'CopilotAudit.Run', @LockMode = 'Exclusive', @LockOwner = 'Session', @LockTimeout = 0; SELECT @r;"
            $granted = [int] $lc.ExecuteScalar() -ge 0
        }
        catch { $runLock.Dispose(); throw }
        if (-not $granted) {
            $runLock.Dispose()
            throw "Another run is using $db now: the nightly pipeline, Install-CopilotAudit.ps1 or a maintenance rebuild. The site is installed; $db's dashboard schema and grants were not changed. Let it finish (Admin > Health shows the pipeline's runs), then run this again with -GrantDatabaseAccess."
        }
    }
    $dbConn = New-Object System.Data.SqlClient.SqlConnection $dbConnString
    try {
        $dbConn.Open()
        foreach ($f in 'Create-CopilotDashboardSchema.sql', 'Create-CopilotDashboardProcedure.sql', 'Create-CopilotOperationsSchema.sql') {
            $text = Get-Content -LiteralPath (Join-Path $repoSql $f) -Raw
            foreach ($batch in [regex]::Split($text, '(?im)^[ \t]*GO[ \t]*(?:--[^\r\n]*)?\r?$')) {
                if ($batch.Trim()) { $c = $dbConn.CreateCommand(); $c.CommandText = $batch; $c.CommandTimeout = 0; [void] $c.ExecuteNonQuery() }
            }
        }
        if ($OrgTopUpn) {
            $c = $dbConn.CreateCommand(); $c.CommandText = "UPDATE dash.Setting SET Value = @v, UpdatedBy = @by, UpdatedUtc = SYSUTCDATETIME() WHERE Name = 'OrgTopUpn';"
            [void] $c.Parameters.AddWithValue('@v', $OrgTopUpn.Trim()); [void] $c.Parameters.AddWithValue('@by', "$env:USERDOMAIN\$env:USERNAME"); [void] $c.ExecuteNonQuery()
        }
        $c = $dbConn.CreateCommand(); $c.CommandText = "IF OBJECT_ID(N'bi.FactInteraction', N'U') IS NOT NULL EXEC dash.usp_RefreshDashboard;"; $c.CommandTimeout = 0; [void] $c.ExecuteNonQuery()
        Write-Host "Dashboard schema installed in $db and its rollups built."
    }
    finally { $dbConn.Dispose(); if ($runLock) { $runLock.Dispose() } }
    $conn = New-Object System.Data.SqlClient.SqlConnection "Server=$SqlServer;Database=master;Integrated Security=true;Encrypt=true;TrustServerCertificate=$trust"
    $conn.Open()
    try { $cmd = $conn.CreateCommand(); $cmd.CommandText = (& $grantsFor $db); $cmd.CommandTimeout = 120; [void] $cmd.ExecuteNonQuery() }
    finally { $conn.Dispose() }
    Write-Host "Granted $sqlLogin access to $db."
  }
}
else {
    Write-Host "Database grants written to $grantsFile. Have a DBA run them, or run this again with -GrantDatabaseAccess."
}

# ---- 8. The daily pre-cache -------------------------------------------------------------------
# Every report page built ahead each morning (2.5.1). A task already scheduled is kept unless -PrecacheAt or
# -PrecacheTimeZone is given, as the site's other settings are; -PrecacheAt '' removes it.
$sitePrecache = Join-Path $PhysicalPath 'Invoke-InsightsPrecache.ps1'
if (Test-Path $sitePrecache) {
    Write-Step 'Daily pre-cache'
    $task = Get-ScheduledTask -TaskName 'Copilot Insights pre-cache' -ErrorAction SilentlyContinue
    if (-not $PrecacheAt) { if ($task) { & $sitePrecache -Unregister } else { Write-Host '    not scheduled (-PrecacheAt is empty).' } }
    elseif ($task -and -not $PSBoundParameters.ContainsKey('PrecacheAt') -and -not $PSBoundParameters.ContainsKey('PrecacheTimeZone')) {
        Write-Host "    kept as scheduled: $(@($task.Triggers | ForEach-Object { ([DateTime] $_.StartBoundary).ToString('HH:mm') }) -join ' and ') on this server's clock."
    }
    else { & $sitePrecache -Register -PhysicalPath $PhysicalPath -At $PrecacheAt -TimeZone $PrecacheTimeZone }
}

$url = "{0}://{1}{2}/" -f $proto, $(if ($HostName) { $HostName } else { $env:COMPUTERNAME }), $(if (($https -and $Port -eq 443) -or (-not $https -and $Port -eq 80)) { '' } else { ":$Port" })
Write-Host ''
Write-Host "Copilot Insights is at $url" -ForegroundColor Green
Write-Host 'Next: sign in as a bootstrap administrator, open Admin > Access, set the top of the organization, and grant roles.'
if ($HostName) { Write-Host "For Kerberos sign-in on $HostName, register its SPN on the app-pool account: setspn -S HTTP/$HostName $(if ($AppPoolIdentity) { $AppPoolIdentity } else { "$env:USERDOMAIN\$env:COMPUTERNAME`$" })" }
```

<!-- generated:scripts:end -->
