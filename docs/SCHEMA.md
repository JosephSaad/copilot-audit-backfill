# Schema reference

Every table and view copilot-audit-backfill 2.3.0 creates in SQL Server, column by column, and the script behind each. Generated from the metadata of copies of the lab's Copilot and license-event databases upgraded by the 2.3.0 installer (27 September 2026), with the notes the scripts' DDL carries. Types are SQL Server's; **no** under Null means NOT NULL.

82 tables (plus 2 left over from 1.x), 35 views, 1101 columns, from 18 scripts.

## Contents

- [The scripts](#the-scripts)
- [Loading and state](#loading-and-state): [`audit.CopilotBackfillRange`](#auditcopilotbackfillrange), [`audit.CopilotBackfillWindow`](#auditcopilotbackfillwindow), [`audit.CopilotAuditStage`](#auditcopilotauditstage), [`audit.CopilotLoad`](#auditcopilotload), [`audit.CopilotLoadWindow`](#auditcopilotloadwindow), [`audit.CopilotLoadStage`](#auditcopilotloadstage), [`audit.PipelineSetting`](#auditpipelinesetting), [`audit.PipelineRun`](#auditpipelinerun), [`audit.PipelineRunStep`](#auditpipelinerunstep)
- [Final records](#final-records): [`audit.CopilotAudit`](#auditcopilotaudit), [`audit.CopilotInteraction`](#auditcopilotinteraction), [`audit.vCopilotAuditRecord`](#auditvcopilotauditrecord), [`audit.vCopilotDailyUsage`](#auditvcopilotdailyusage)
- [Flattened records](#flattened-records): [`audit.RawLogFlattened`](#auditrawlogflattened), [`audit.RawLogAccessedResource`](#auditrawlogaccessedresource), [`audit.RawLogContext`](#auditrawlogcontext), [`audit.ValueCatalog`](#auditvaluecatalog), [`audit.ValueCatalogColumn`](#auditvaluecatalogcolumn), [`audit.RawLogRecordShape`](#auditrawlogrecordshape)
- [Licenses and the user directory](#licenses-and-the-user-directory): [`audit.CopilotLicense`](#auditcopilotlicense), [`audit.UserDirectory`](#audituserdirectory), [`audit.GroupMember`](#auditgroupmember), [`audit.LicenseSku`](#auditlicensesku), [`audit.LicenseSkuDay`](#auditlicenseskuday), [`audit.UserLicenseSku`](#audituserlicensesku), [`audit.vCopilotAdoption`](#auditvcopilotadoption), [`audit.vCopilotUsageByDepartment`](#auditvcopilotusagebydepartment)
- [Sessions and the weekly rollup](#sessions-and-the-weekly-rollup): [`audit.SessionAppCategory`](#auditsessionappcategory), [`audit.CopilotSession`](#auditcopilotsession), [`audit.CopilotWeeklyRollup`](#auditcopilotweeklyrollup), [`audit.CopilotSessionRun`](#auditcopilotsessionrun), [`audit.vCopilotSessionSummary`](#auditvcopilotsessionsummary), [`audit.vCopilotWeeklyRollup`](#auditvcopilotweeklyrollup)
- [CSV import](#csv-import): [`audit.CopilotCsvImport`](#auditcopilotcsvimport), [`audit.CopilotCsvFile`](#auditcopilotcsvfile), [`audit.CopilotCsvReject`](#auditcopilotcsvreject), [`audit.CopilotCsvStage`](#auditcopilotcsvstage)
- [License events (license-event database)](#license-events-license-event-database): [`audit.LicenseEvent`](#auditlicenseevent), [`audit.LicenseEventChunk`](#auditlicenseeventchunk), [`audit.LicenseEventProperty`](#auditlicenseeventproperty), [`audit.vLicenseChange`](#auditvlicensechange), [`audit.vLicenseSkuChange`](#auditvlicenseskuchange), [`audit.AgentEvent`](#auditagentevent), [`audit.vLicenseLifecycle`](#auditvlicenselifecycle)
- [bi: dimensions](#bi-dimensions): [`bi.DimDate`](#bidimdate), [`bi.DimUser`](#bidimuser), [`bi.DimApp`](#bidimapp), [`bi.DimAgent`](#bidimagent), [`bi.DimModel`](#bidimmodel), [`bi.DimSku`](#bidimsku), [`bi.DimLicenseStatus`](#bidimlicensestatus), [`bi.DimLicenseType`](#bidimlicensetype), [`bi.DimSensitivityLabel`](#bidimsensitivitylabel), [`bi.DimResourceType`](#bidimresourcetype), [`bi.DimResourceAction`](#bidimresourceaction), [`bi.DimSite`](#bidimsite)
- [bi: facts and refresh log](#bi-facts-and-refresh-log): [`bi.FactInteraction`](#bifactinteraction), [`bi.FactSession`](#bifactsession), [`bi.FactLicenseChange`](#bifactlicensechange), [`bi.FactLicensedDay`](#bifactlicensedday), [`bi.FactAccessedResource`](#bifactaccessedresource), [`bi.FactAgentEvent`](#bifactagentevent), [`bi.RefreshRun`](#birefreshrun)
- [bi: views Power BI imports](#bi-views-power-bi-imports): [`bi.Interactions`](#biinteractions), [`bi.Sessions`](#bisessions), [`bi.LicenseChanges`](#bilicensechanges), [`bi.LicensedDays`](#bilicenseddays), [`bi.Calendar`](#bicalendar), [`bi.Users`](#biusers), [`bi.Apps`](#biapps), [`bi.Agents`](#biagents), [`bi.Models`](#bimodels), [`bi.Skus`](#biskus), [`bi.LicenseStatuses`](#bilicensestatuses), [`bi.AccessedResources`](#biaccessedresources), [`bi.LicenseTypes`](#bilicensetypes), [`bi.SensitivityLabels`](#bisensitivitylabels), [`bi.ResourceTypes`](#biresourcetypes), [`bi.ResourceActions`](#biresourceactions), [`bi.Sites`](#bisites), [`bi.AgentEvents`](#biagentevents), [`bi.OrgSecurity`](#biorgsecurity), [`bi.ValueNames`](#bivaluenames), [`bi.LicensePool`](#bilicensepool), [`bi.DepartmentWeek`](#bidepartmentweek), [`bi.UseCases`](#biusecases), [`bi.JobFamilies`](#bijobfamilies), [`bi.EventMarkers`](#bieventmarkers)
- [dash: the Copilot Insights dashboard](#dash-the-copilot-insights-dashboard): [`dash.Setting`](#dashsetting), [`dash.RoleGrant`](#dashrolegrant), [`dash.AgentAlias`](#dashagentalias), [`dash.SensitivityLabel`](#dashsensitivitylabel), [`dash.AuditLog`](#dashauditlog), [`dash.Person`](#dashperson), [`dash.OrgClosure`](#dashorgclosure), [`dash.UserDay`](#dashuserday), [`dash.ResourceDay`](#dashresourceday), [`dash.AgentUse`](#dashagentuse), [`dash.RefreshRun`](#dashrefreshrun), [`dash.Agent`](#dashagent)
- [dash: operations (2.3.0)](#dash-operations-230): [`dash.Secret`](#dashsecret), [`dash.Subscription`](#dashsubscription), [`dash.NotificationLog`](#dashnotificationlog), [`dash.AlertState`](#dashalertstate), [`dash.Target`](#dashtarget), [`dash.TimeSaving`](#dashtimesaving), [`dash.TrainingAttendance`](#dashtrainingattendance), [`dash.ValueName`](#dashvaluename), [`dash.FlagCase`](#dashflagcase), [`dash.DigestPreference`](#dashdigestpreference), [`dash.DigestSend`](#dashdigestsend), [`dash.LicenseAction`](#dashlicenseaction), [`dash.LicenseContract`](#dashlicensecontract), [`dash.SavedView`](#dashsavedview), [`dash.JobFamilyRule`](#dashjobfamilyrule), [`dash.UseCaseRule`](#dashusecaserule), [`dash.EventMarker`](#dasheventmarker), [`dash.Extract`](#dashextract)
- [Left over from 1.x](#left-over-from-1x)

## The scripts

Every table, view, procedure and function comes from a script in `sql/`. Each is idempotent: it creates what is missing, adds columns a newer version needs, and never drops data. Three run themselves; the rest are run once, in the order [DEPLOYMENT.md](DEPLOYMENT.md) gives (steps 7 and 8).

| Script | Database | Run | Creates |
|---|---|---|---|
| `sql/Create-CopilotAuditSchema.sql` | Both | Automatically, at the start of every run of Invoke-CopilotAuditBackfill.ps1 and of the CSV importer | Backfill and load state and staging: CopilotBackfillRange, CopilotBackfillWindow, CopilotAuditStage, CopilotLoad, CopilotLoadWindow, CopilotLoadStage. The shared function fnShredCopilotInteraction. |
| `sql/Finalize-CopilotAudit.sql` | Both | By -Finalize or -FinalizeOnly, once, after the backfill | CopilotAudit and CopilotInteraction (dropped and rebuilt from staging), vCopilotDailyUsage, vCopilotAuditRecord. |
| `sql/Create-CopilotLoadProcedure.sql` | Both | Automatically, by every -Incremental run | usp_MergeCopilotLoad, which appends each nightly load. |
| `sql/Create-CopilotCsvImport.sql` | Copilot (or license, if importing there) | Automatically, by tools/Import-CopilotAuditCsv.ps1 | CopilotCsvImport, CopilotCsvFile, CopilotCsvReject, CopilotCsvStage, usp_MergeCopilotCsv. In a database with no backfill, also CopilotAudit, CopilotInteraction and their two views. Adds CopilotAudit.CsvImportId and CopilotBackfillRange.Source. |
| `sql/Create-CopilotFlattenedSchema.sql` | Copilot | By hand, once (deployment step 8) | RawLogFlattened, and CopilotLicense if it does not exist yet. |
| `sql/Create-CopilotFlattenProcedure.sql` | Copilot | By hand, once (step 8) | usp_FlattenCopilotAudit. |
| `sql/Create-CopilotPurgeProcedure.sql` | Copilot | By hand, optional (step 8) | usp_PurgeCopilotAuditJson. |
| `sql/Create-CopilotEnrichmentSchema.sql` | Copilot | Automatically, by both enrichment tools; or by hand (step 8) | CopilotLicense, UserDirectory, vCopilotAdoption, vCopilotUsageByDepartment (the views only once CopilotInteraction exists). |
| `sql/Create-CopilotSessionSchema.sql` | Copilot | By hand, once (step 8) | SessionAppCategory (seeded), CopilotSession, CopilotWeeklyRollup, CopilotSessionRun, vCopilotSessionSummary, vCopilotWeeklyRollup. |
| `sql/Create-CopilotSessionProcedure.sql` | Copilot | By hand, once (step 8) | usp_BuildCopilotSessions. |
| `sql/Create-CopilotBiSchema.sql` | Copilot | By hand, once (step 8) | The bi schema: the dimensions (DimDate, DimUser, DimApp, DimAgent, DimModel, DimSku, DimLicenseStatus, DimLicenseType, DimSensitivityLabel, DimResourceType, DimResourceAction, DimSite), the facts (FactInteraction, FactSession, FactLicenseChange, FactLicensedDay, FactAccessedResource, FactAgentEvent), RefreshRun, and the views Power BI imports. bi.FactInteraction stores PremiumUse and UseCaseKey, set once by the refresh (2.4.0), and bi.Interactions passes them on. |
| `sql/Create-CopilotBiProcedure.sql` | Copilot | By hand, once (step 8) | bi.usp_RefreshBi. |
| `sql/Create-LicenseEventSchema.sql` | License events | By hand, once (step 7) | LicenseEvent, LicenseEventChunk, LicenseEventProperty, vLicenseChange, vLicenseSkuChange, vLicenseLifecycle. |
| `sql/Create-LicenseEventProcedure.sql` | License events | By hand, once (step 7) | usp_ShredLicenseEvents. |
| `sql/Create-CopilotDashboardSchema.sql` | Copilot | By hand, once, for the dashboard (step 22) | The dash schema: Setting (seeded), RoleGrant, AgentAlias, SensitivityLabel, AuditLog, Person, OrgClosure, UserDay, ResourceDay, AgentUse, RefreshRun; the view dash.Agent and the function dash.fnScopeUsers. |
| `sql/Create-CopilotOperationsSchema.sql` | Copilot | By Install-CopilotAudit.ps1 -Dashboard, or the dashboard installer with -GrantDatabaseAccess | The dashboard's operations (2.3.0): encrypted secrets, digests, alerts, targets, time saved, training, value names, flag triage, digest preferences and sends, license actions and contracts, saved views, job-family and use-case rules, events and extracts; the role CopilotInsightsApp and its grants; privacy mode and retention procedures; the bi views OrgSecurity, ValueNames, LicensePool, DepartmentWeek, UseCases, JobFamilies and EventMarkers. |
| `sql/Create-AgentEventProcedure.sql` | License events | By Install-CopilotAudit.ps1, and after each license-event load | audit.AgentEvent and audit.usp_ShredAgentEvents (2.3.0): Copilot Studio authoring records, one row each. |
| `sql/Create-CopilotDashboardProcedure.sql` | Copilot | By hand, once, for the dashboard (step 22) | dash.usp_RefreshDashboard, which the backfill's -Incremental run and the CSV importer call after bi.usp_RefreshBi. |

**Two databases.** The Copilot database (`CopilotAudit`) holds everything below except the license-event section. The license-event database (`CopilotLicenseEvents`) holds the same loading, state and final-record tables, filled with directory events instead, plus the license-event tables and views.

**Procedures and function.** `audit.fnShredCopilotInteraction` (inline; JSON to interaction columns), `audit.usp_MergeCopilotLoad`, `audit.usp_MergeCopilotCsv`, `audit.usp_FlattenCopilotAudit`, `audit.usp_PurgeCopilotAuditJson`, `audit.usp_BuildCopilotSessions`, `audit.usp_ShredLicenseEvents`, `bi.usp_RefreshBi` and `dash.usp_RefreshDashboard`, from the scripts above. `dash.fnScopeUsers(@RootUpn)` (inline) returns the `bi.DimUser` keys of a person and everyone under them: the dashboard joins every query for a part of the organization to it.

## Loading and state

What the backfill and the nightly loads write while they run. The engine resumes from these after any interruption.

### audit.CopilotBackfillRange

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

Indexes and constraints:

- `PK_CopilotBackfillRange`: primary key, clustered (Id)
- `CK_CopilotBackfillRange_Single`: check `([Id]=(1))`

### audit.CopilotBackfillWindow

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

### audit.CopilotAuditStage

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

### audit.CopilotLoad

*Table.* One row per -Incremental load: its range, operations, when it finished downloading and was merged, and how many records were new or already there.

**Script** `Create-CopilotAuditSchema.sql` · **In** Copilot and license-event databases

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

Indexes and constraints:

- `PK_CopilotLoad`: primary key, clustered (LoadId)
- `CK_CopilotLoad_Range`: check `([RangeEndUtc]>[RangeStartUtc])`

### audit.CopilotLoadWindow

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

### audit.CopilotLoadStage

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

### audit.PipelineSetting

*Table.* Settings the procedures read, one row each: ReportingTimeZone, the Windows time zone name every procedure cuts days and hours in (read through audit.ReportingTimeZone(); Eastern Standard Time when unset; written by Install-CopilotAudit.ps1 -ReportingTimeZone, which refuses a change once records are loaded), and from 2.3.0 PrivacyMode and PseudonymSalt (written by dash.usp_SetPrivacyMode from Admin &gt; Privacy and retention).

**Script** `Create-CopilotAuditSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `Name` | sysname | **no** | **PK** |  |
| 2 | `Value` | nvarchar(400) | yes |  |  |
| 3 | `UpdatedUtc` | datetime2(0) | **no** | `sysutcdatetime` |  |
| 4 | `UpdatedBy` | nvarchar(256) | **no** | `suser_sname` |  |

Indexes and constraints:

- `PK_PipelineSetting`: primary key, clustered (Name)

### audit.PipelineRun

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

### audit.PipelineRunStep

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

## Final records

Every audit record once, and the slim reporting table. Built by finalize, then appended to by each load and CSV import.

### audit.CopilotAudit

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

### audit.CopilotInteraction

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

### audit.vCopilotAuditRecord

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

### audit.vCopilotDailyUsage

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

## Flattened records

One row per record with the Copilot event data shredded into columns, for reports written against the old RawLogFlattened table.

### audit.RawLogFlattened

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
| 19 | `AgentId` | nvarchar(400) | yes |  | $.CopilotEventData.AgentId |
| 20 | `AgentName` | nvarchar(400) | yes |  | $.CopilotEventData.AgentName |
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

### audit.RawLogAccessedResource

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

### audit.RawLogContext

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

### audit.ValueCatalog

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

### audit.ValueCatalogColumn

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

### audit.RawLogRecordShape

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

## Licenses and the user directory

Who holds a license (Microsoft Graph) and where they sit in the organization (on-premises AD).

### audit.CopilotLicense

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

### audit.UserDirectory

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
| 8 | `Department` | nvarchar(64) | yes |  | AD attribute: department |
| 9 | `Division` | nvarchar(256) | yes |  | division: the line of business |
| 10 | `Office` | nvarchar(128) | yes |  | AD attribute: physicalDeliveryOfficeName |
| 11 | `City` | nvarchar(128) | yes |  | AD attribute: l |
| 12 | `Country` | nvarchar(128) | yes |  | AD attribute: co |
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

### audit.GroupMember

*Table.* The members of each AD group granted Organization in the dashboard, nested groups included (2.4.0): the group as granted (DOMAIN\\Group) and the member's UPN. Rewritten by each AD export, so bi.OrgSecurity gives them the whole organization in Power BI as the dashboard does at sign-in.

**Script** `Create-CopilotEnrichmentSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `GroupName` | nvarchar(256) | **no** | **PK** | as granted: DOMAIN\\Group |
| 2 | `UserPrincipalName` | nvarchar(256) | **no** | **PK** |  |
| 3 | `LoadedUtc` | datetime2(0) | **no** | `sysutcdatetime` |  |

Indexes and constraints:

- `PK_GroupMember`: primary key, clustered (GroupName, UserPrincipalName)

### audit.LicenseSku

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

### audit.UserLicenseSku

*Table.* Every SKU each person the license tool looked up holds (2.4.3), from assignedLicenses; replaced whole by each run. Licensing > Overlapping licenses reads it, with names from audit.LicenseSku.

**Script** `Create-CopilotEnrichmentSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `UserPrincipalName` | nvarchar(512) | **no** | **PK** |  |
| 2 | `SkuId` | uniqueidentifier | **no** | **PK** |  |
| 3 | `CapturedUtc` | datetime2(0) | **no** | `sysutcdatetime` |  |

Indexes and constraints:

- `PK_UserLicenseSku`: primary key, clustered (UserPrincipalName, SkuId)

### audit.LicenseSkuDay

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

### audit.vCopilotAdoption

*View.* Licensed users against actual use: first and last interaction, interactions, NeverUsed, DaysToFirstUse, with directory attributes.

**Script** `Create-CopilotEnrichmentSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `UserPrincipalName` | nvarchar(512) | **no** |  |  |
| 2 | `IsCopilotLic` | bit | yes |  |  |
| 3 | `SkuPartNumber` | nvarchar(100) | yes |  |  |
| 4 | `LicensedSinceUtc` | datetime2(0) | yes |  |  |
| 5 | `Division` | nvarchar(256) | yes |  |  |
| 6 | `Department` | nvarchar(64) | yes |  |  |
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

### audit.vCopilotUsageByDepartment

*View.* Interactions and active users per department, day and app.

**Script** `Create-CopilotEnrichmentSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `Department` | nvarchar(64) | **no** |  |  |
| 2 | `ActivityDate` | date | **no** |  |  |
| 3 | `AppHost` | nvarchar(100) | yes |  |  |
| 4 | `Interactions` | bigint | yes |  |  |
| 5 | `ActiveUsers` | int | yes |  |  |

## Sessions and the weekly rollup

The SQL port of the session script: sessions inferred from the flattened records, rolled up by Saturday-to-Friday week.

### audit.SessionAppCategory

*Table.* App name to category (Copilot Chat or In-App Copilot). Seeded once; your edits survive re-running the script. Unmapped apps count as In-App Copilot.

**Script** `Create-CopilotSessionSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `App` | nvarchar(200) | **no** | **PK** |  |
| 2 | `AppCategory` | nvarchar(50) | **no** |  |  |

Indexes and constraints:

- `PK_SessionAppCategory`: primary key, clustered (App)

### audit.CopilotSession

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

### audit.CopilotWeeklyRollup

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

### audit.CopilotSessionRun

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

### audit.vCopilotSessionSummary

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

### audit.vCopilotWeeklyRollup

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

## CSV import

Created by tools/Import-CopilotAuditCsv.ps1 (2.1.0).

### audit.CopilotCsvImport

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

Indexes and constraints:

- `PK_CopilotCsvImport`: primary key, clustered (ImportId)

### audit.CopilotCsvFile

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

### audit.CopilotCsvReject

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

### audit.CopilotCsvStage

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

## License events (license-event database)

In the separate database the license-operations backfill fills. It also holds the loading, state and final-record tables above; these are added on top.

### audit.LicenseEvent

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

### audit.LicenseEventChunk

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

### audit.LicenseEventProperty

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

### audit.vLicenseChange

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

### audit.vLicenseSkuChange

*View.* SKUs added and removed per change, from the old and new AssignedLicense lists: "when was this user given Copilot, and when was it taken away". Sessions and bi read it for license at the time of use.

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

### audit.AgentEvent

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

### audit.vLicenseLifecycle

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

## bi: dimensions

The star schema Power BI imports (through the bi views below). Integer surrogate keys, stable across refreshes and rebuilds.

### bi.DimDate

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

### bi.DimUser

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

### bi.DimApp

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

### bi.DimAgent

*Table.* One row per agent: AgentId when the record carries one, otherwise the identity parsed from AppIdentity. (no agent) is Copilot itself.

**Script** `Create-CopilotBiSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `AgentKey` | int | **no** | **PK** identity |  |
| 2 | `AgentId` | nvarchar(400) | **no** |  | AgentId, else AppIdentity |
| 3 | `AgentName` | nvarchar(400) | yes |  | the latest name seen; agents get renamed |
| 5 | `FirstSeenUtc` | datetime2(0) | **no** | `sysutcdatetime` |  |
| 6 | `AgentSource` | nvarchar(30) | yes |  | Copilot Studio \| Microsoft \| Agent ID \| (no agent) |

Indexes and constraints:

- `PK_DimAgent`: primary key, clustered (AgentKey)
- `UQ_DimAgent_Id`: unique, nonclustered (AgentId)

### bi.DimModel

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

### bi.DimSku

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

### bi.DimLicenseStatus

*Table.* Three rows: 0 Unknown, 1 Licensed, 2 Not licensed.

**Script** `Create-CopilotBiSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `LicenseStatusKey` | tinyint | **no** | **PK** |  |
| 2 | `LicenseStatus` | nvarchar(20) | **no** |  |  |

Indexes and constraints:

- `PK_DimLicenseStatus`: primary key, clustered (LicenseStatusKey)

### bi.DimLicenseType

*Table.* The license type the audit record itself reports (CopilotEventData.LicenseType), 2.2.0. Key 1 is (not reported).

**Script** `Create-CopilotBiSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `LicenseTypeKey` | int | **no** | **PK** identity |  |
| 2 | `LicenseType` | nvarchar(32) | **no** |  |  |

Indexes and constraints:

- `PK_DimLicenseType`: primary key, clustered (LicenseTypeKey)
- `UQ_DimLicenseType`: unique, nonclustered (LicenseType)

### bi.DimSensitivityLabel

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

### bi.DimResourceType

*Table.* Accessed-resource types: File, EmailMessage, TeamsMessage, WebSearchQuery ... (2.2.0).

**Script** `Create-CopilotBiSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `ResourceTypeKey` | int | **no** | **PK** identity |  |
| 2 | `ResourceType` | nvarchar(200) | **no** |  |  |

Indexes and constraints:

- `PK_DimResourceType`: primary key, clustered (ResourceTypeKey)
- `UQ_DimResourceType`: unique, nonclustered (ResourceType)

### bi.DimResourceAction

*Table.* Accessed-resource actions: Read, Create ... (2.2.0).

**Script** `Create-CopilotBiSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `ResourceActionKey` | int | **no** | **PK** identity |  |
| 2 | `ResourceAction` | nvarchar(200) | **no** |  |  |

Indexes and constraints:

- `PK_DimResourceAction`: primary key, clustered (ResourceActionKey)
- `UQ_DimResourceAction`: unique, nonclustered (ResourceAction)

### bi.DimSite

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

## bi: facts and refresh log

Narrow facts with integer keys. FactInteraction is a clustered columnstore and the only one refreshed incrementally in Power BI.

### bi.FactInteraction

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

Indexes and constraints:

- `CCI_FactInteraction`: clustered columnstore include (RecordId, CreatedUtc, ActivityHour, DateKey, HourOfDay, UserKey, AppKey, AgentKey, ModelKey, LicenseStatusKey, PromptCount, AccessedResourceCount, UsedWebSearch, JailbreakDetected, PromptInjectionDetected, HasBlockedResource, HasSensitivityLabel, PremiumUse, UseCaseKey, LicenseTypeKey, SensitivityLabelKey, MessageCount, MemoryUpdated, DlpEvaluationDeferred, CalledAnotherAgent, TargetAgentName)

### bi.FactSession

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

### bi.FactLicenseChange

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

### bi.FactLicensedDay

*Table.* One row per person per day on which they held a Copilot license at the end of the day.

**Script** `Create-CopilotBiSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `DateKey` | int | **no** | **PK** |  |
| 2 | `UserKey` | int | **no** | **PK** |  |

Indexes and constraints:

- `PK_FactLicensedDay`: primary key, clustered (DateKey, UserKey)

### bi.FactAccessedResource

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

Indexes and constraints:

- `CCI_FactAccessedResource`: clustered columnstore include (RecordId, Ordinal, CreatedUtc, ActivityHour, DateKey, UserKey, AppKey, ResourceTypeKey, ResourceActionKey, SiteKey, SensitivityLabelKey, IsBlocked, XPIADetected)

### bi.FactAgentEvent

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

### bi.RefreshRun

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

## bi: views Power BI imports

One view per table above, leaving out the lineage columns. The model imports these, not the tables.

### bi.Interactions

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

### bi.Sessions

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

### bi.LicenseChanges

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

### bi.LicensedDays

*View.* bi.FactLicensedDay for the model.

**Script** `Create-CopilotBiSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `DateKey` | int | **no** |  |  |
| 2 | `UserKey` | int | **no** |  |  |

### bi.Calendar

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

### bi.Users

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

### bi.Apps

*View.* bi.DimApp for the model.

**Script** `Create-CopilotBiSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `AppKey` | int | **no** | identity |  |
| 2 | `App` | nvarchar(200) | **no** |  |  |
| 3 | `AppCategory` | nvarchar(50) | **no** |  |  |

### bi.Agents

*View.* bi.DimAgent for the model.

**Script** `Create-CopilotBiSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `AgentKey` | int | **no** | identity |  |
| 2 | `AgentId` | nvarchar(400) | **no** |  |  |
| 3 | `AgentName` | nvarchar(400) | yes |  |  |
| 4 | `AgentSource` | nvarchar(30) | yes |  |  |

### bi.Models

*View.* bi.DimModel for the model.

**Script** `Create-CopilotBiSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `ModelKey` | int | **no** | identity |  |
| 2 | `ModelProvider` | nvarchar(200) | **no** |  |  |
| 3 | `ModelName` | nvarchar(200) | **no** |  |  |

### bi.Skus

*View.* bi.DimSku for the model.

**Script** `Create-CopilotBiSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `SkuKey` | int | **no** | identity |  |
| 2 | `Sku` | nvarchar(200) | **no** |  |  |
| 3 | `IsCopilot` | bit | **no** |  |  |

### bi.LicenseStatuses

*View.* bi.DimLicenseStatus for the model.

**Script** `Create-CopilotBiSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `LicenseStatusKey` | tinyint | **no** |  |  |
| 2 | `LicenseStatus` | nvarchar(20) | **no** |  |  |

### bi.AccessedResources

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

### bi.LicenseTypes

*View.* bi.DimLicenseType for the model.

**Script** `Create-CopilotBiSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `LicenseTypeKey` | int | **no** | identity |  |
| 2 | `LicenseType` | nvarchar(32) | **no** |  |  |

### bi.SensitivityLabels

*View.* bi.DimSensitivityLabel for the model, with the id as lower-case text.

**Script** `Create-CopilotBiSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `SensitivityLabelKey` | int | **no** | identity |  |
| 2 | `LabelId` | nvarchar(36) | yes |  |  |

### bi.ResourceTypes

*View.* bi.DimResourceType for the model.

**Script** `Create-CopilotBiSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `ResourceTypeKey` | int | **no** | identity |  |
| 2 | `ResourceType` | nvarchar(200) | **no** |  |  |

### bi.ResourceActions

*View.* bi.DimResourceAction for the model.

**Script** `Create-CopilotBiSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `ResourceActionKey` | int | **no** | identity |  |
| 2 | `ResourceAction` | nvarchar(200) | **no** |  |  |

### bi.Sites

*View.* bi.DimSite for the model; in privacy mode a OneDrive site is named OneDrive (personal).

**Script** `Create-CopilotBiSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `SiteKey` | int | **no** | identity |  |
| 2 | `Site` | nvarchar(400) | **no** |  |  |
| 3 | `Host` | nvarchar(400) | **no** |  |  |
| 4 | `IsPersonal` | bit | **no** |  |  |

### bi.AgentEvents

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

### bi.OrgSecurity

*View.* Who may see whom (2.3.0): each viewer's sign-in with the user key of everyone at or under them, and everyone for the top of the organization, the people reporting to them and people granted Organization. The Power BI model's Organization role filters Users by it. Empty in privacy mode.

**Script** `Create-CopilotOperationsSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `ViewerUpn` | nvarchar(256) | **no** |  |  |
| 2 | `UserKey` | int | **no** |  |  |

### bi.ValueNames

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

### bi.LicensePool

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

### bi.DepartmentWeek

*View.* Active people, interactions and sessions by department and week (2.3.0), with every figure of a department under the minimum group size blank: for a model that must not show individuals.

**Script** `Create-CopilotOperationsSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `WeekEnding` | date | **no** |  |  |
| 2 | `Department` | nvarchar(200) | **no** |  |  |
| 3 | `ActivePeople` | int | yes |  |  |
| 4 | `Interactions` | bigint | yes |  |  |
| 5 | `Sessions` | bigint | yes |  |  |
| 6 | `Hidden` | bit | yes |  |  |

### bi.UseCases

*View.* Every combination of app, web search and agent (UseCaseKey = AppKey x 4 + web x 2 + agent) with the use case the rules in dash.UseCaseRule give it (2.3.0). bi.Interactions.UseCaseKey points here.

**Script** `Create-CopilotOperationsSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `UseCaseKey` | int | yes |  |  |
| 2 | `App` | nvarchar(200) | **no** |  |  |
| 3 | `WebGrounded` | bit | yes |  |  |
| 4 | `WithAgent` | bit | yes |  |  |
| 5 | `UseCase` | nvarchar(100) | **no** |  |  |

### bi.JobFamilies

*View.* Each person's job family from the rules in dash.JobFamilyRule (2.3.0); (no family) when none matches.

**Script** `Create-CopilotOperationsSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `UserKey` | int | **no** |  |  |
| 2 | `JobFamily` | nvarchar(100) | **no** |  |  |

### bi.EventMarkers

*View.* The events marked on the dashboard's trends (2.3.0), with their day key.

**Script** `Create-CopilotOperationsSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `Day` | date | **no** |  |  |
| 2 | `DateKey` | int | yes |  |  |
| 3 | `Label` | nvarchar(100) | **no** |  |  |
| 4 | `Kind` | varchar(16) | **no** |  |  |

## dash: the Copilot Insights dashboard

What the dashboard (dashboard/CopilotInsights, see DASHBOARD.md) keeps besides the bi schema it reads: its settings, roles, reference names and audit log, which the admin module edits, and the org chart and daily rollups dash.usp_RefreshDashboard rebuilds after each bi refresh.

### dash.Setting

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

### dash.RoleGrant

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

### dash.AgentAlias

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

### dash.SensitivityLabel

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

### dash.AuditLog

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

### dash.Person

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

### dash.OrgClosure

*Table.* Every (manager, person under them) pair at any depth, and each person with themselves at depth 0: the security model's scope. Rebuilt whole by each refresh.

**Script** `Create-CopilotDashboardSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `AncestorUpn` | nvarchar(256) | **no** | **PK** |  |
| 2 | `DescendantUpn` | nvarchar(256) | **no** | **PK** |  |
| 3 | `Depth` | tinyint | **no** |  | 0 = the person themselves |

Indexes and constraints:

- `PK_DashOrgClosure`: primary key, nonclustered (AncestorUpn, DescendantUpn)

### dash.UserDay

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

### dash.ResourceDay

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

### dash.AgentUse

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

### dash.RefreshRun

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

### dash.Agent

*View.* Agents with their friendly name, owner and first use.

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

## dash: operations (2.3.0)

What the dashboard's operations features keep, from Create-CopilotOperationsSchema.sql: every table is edited through the Admin pages or the pages named, never by hand.

### dash.Secret

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

### dash.Subscription

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

### dash.NotificationLog

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

### dash.AlertState

*Table.* What each alert check last saw, so an alert fires once per occurrence.

**Script** `Create-CopilotOperationsSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `AlertKey` | nvarchar(200) | **no** | **PK** |  |
| 2 | `LastValue` | nvarchar(400) | yes |  |  |
| 3 | `LastFiredUtc` | datetime2(0) | **no** | `sysutcdatetime` |  |

Indexes and constraints:

- `PK_DashAlertState`: primary key, clustered (AlertKey)

### dash.Target

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

### dash.TimeSaving

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

### dash.TrainingAttendance

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

### dash.ValueName

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

### dash.FlagCase

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

### dash.DigestPreference

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

### dash.DigestSend

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

### dash.LicenseAction

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

### dash.LicenseContract

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

### dash.SavedView

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

### dash.JobFamilyRule

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

### dash.UseCaseRule

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

### dash.EventMarker

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

### dash.Extract

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

## Left over from 1.x

Present in a database upgraded from 1.x, as the lab is. No current script creates them.

### audit.UserProfile

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

### audit.AdministrativeUnit

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
