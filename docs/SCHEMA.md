# Schema reference

Every table and view copilot-audit-backfill 2.3.0 creates in SQL Server, column by column, and the script behind each. Generated from the metadata of copies of the lab's Copilot and license-event databases upgraded by the 2.3.0 installer (27 September 2026), with the notes the scripts' DDL carries. Types are SQL Server's; **no** under Null means NOT NULL.

130 tables (plus 2 left over from 1.x), 39 views, 1597 columns, from 22 scripts.

**Compatibility.** From 2.6.3, a release adds to this schema and takes nothing away. No table, view, procedure, column, parameter or grant to a role is dropped or renamed; a column keeps its type or widens it; a view keeps its columns in their places, with new ones after them; and a new column or parameter never has to be given. A report or query written against one release runs against the next, and installing over a database in use loses nothing. Every build is checked against the last release's schema, and by installing over 2.6.2 and over the last release ([tools/dev/README.md](../tools/dev/README.md#schema-compatibility)). What replaces something is added beside it, and the old one is marked as replaced here.

## Contents

- [The scripts](#the-scripts)
- [Naming conventions](#naming-conventions)
- [The bi schema: what each table and view is for, and what uses it](#the-bi-schema-what-each-table-and-view-is-for-and-what-uses-it)
- [Governance Workbench database](#governance-workbench-database)
- [Loading and state](#loading-and-state): [`audit.CopilotBackfillRange`](#auditcopilotbackfillrange), [`audit.CopilotBackfillWindow`](#auditcopilotbackfillwindow), [`audit.CopilotAuditStage`](#auditcopilotauditstage), [`audit.CopilotLoad`](#auditcopilotload), [`audit.CopilotLoadWindow`](#auditcopilotloadwindow), [`audit.CopilotLoadStage`](#auditcopilotloadstage), [`audit.PipelineSetting`](#auditpipelinesetting), [`audit.PipelineRun`](#auditpipelinerun), [`audit.PipelineRunStep`](#auditpipelinerunstep), [`audit.PipelineCertificate`](#auditpipelinecertificate)
- [Final records](#final-records): [`audit.CopilotAudit`](#auditcopilotaudit), [`audit.CopilotInteraction`](#auditcopilotinteraction), [`audit.vCopilotAuditRecord`](#auditvcopilotauditrecord), [`audit.vCopilotDailyUsage`](#auditvcopilotdailyusage)
- [Flattened records](#flattened-records): [`audit.RawLogFlattened`](#auditrawlogflattened), [`audit.RawLogAccessedResource`](#auditrawlogaccessedresource), [`audit.RawLogContext`](#auditrawlogcontext), [`audit.ValueCatalog`](#auditvaluecatalog), [`audit.ValueCatalogColumn`](#auditvaluecatalogcolumn), [`audit.AgentCatalog`](#auditagentcatalog), [`audit.RawLogRecordShape`](#auditrawlogrecordshape)
- [Licenses and the user directory](#licenses-and-the-user-directory): [`audit.CopilotLicense`](#auditcopilotlicense), [`audit.UserDirectory`](#audituserdirectory), [`audit.GroupMember`](#auditgroupmember), [`audit.ReportGroupMember`](#auditreportgroupmember), [`audit.ReportGroupDay`](#auditreportgroupday), [`audit.LicenseSku`](#auditlicensesku), [`audit.LicenseSkuDay`](#auditlicenseskuday), [`audit.UserLicenseSku`](#audituserlicensesku), [`audit.vCopilotAdoption`](#auditvcopilotadoption), [`audit.vCopilotUsageByDepartment`](#auditvcopilotusagebydepartment)
- [Sessions and the weekly rollup](#sessions-and-the-weekly-rollup): [`audit.SessionAppCategory`](#auditsessionappcategory), [`audit.CopilotSession`](#auditcopilotsession), [`audit.CopilotWeeklyRollup`](#auditcopilotweeklyrollup), [`audit.CopilotSessionRun`](#auditcopilotsessionrun), [`audit.vCopilotSessionSummary`](#auditvcopilotsessionsummary), [`audit.vCopilotWeeklyRollup`](#auditvcopilotweeklyrollup)
- [CSV import](#csv-import): [`audit.CopilotCsvImport`](#auditcopilotcsvimport), [`audit.CopilotCsvFile`](#auditcopilotcsvfile), [`audit.CopilotCsvReject`](#auditcopilotcsvreject), [`audit.CopilotCsvStage`](#auditcopilotcsvstage)
- [AI usage outside Microsoft 365 Copilot (2.6.2), and Fabric and the Power Platform (2.6.6)](#ai-usage-outside-microsoft-365-copilot-262-and-fabric-and-the-power-platform-266): [`audit.GitHubCopilotSeat`](#auditgithubcopilotseat), [`audit.GitHubCopilotDay`](#auditgithubcopilotday), [`audit.GitHubCopilotUserDay`](#auditgithubcopilotuserday), [`audit.GitHubCopilotCost`](#auditgithubcopilotcost), [`audit.FoundryDeployment`](#auditfoundrydeployment), [`audit.FoundryUsageDay`](#auditfoundryusageday), [`audit.FoundryCostDay`](#auditfoundrycostday), [`audit.ExternalUsageRun`](#auditexternalusagerun), [`audit.FabricCapacityDay`](#auditfabriccapacityday), [`audit.FabricWorkloadDay`](#auditfabricworkloadday), [`audit.PowerAutomateFlow`](#auditpowerautomateflow), [`audit.PowerPlatformUserActivity`](#auditpowerplatformuseractivity), [`audit.PowerPlatformCapacityDay`](#auditpowerplatformcapacityday), [`audit.PowerPlatformCreditDay`](#auditpowerplatformcreditday)
- [AI apps, Power BI and Power Apps (2.6.9)](#ai-apps-power-bi-and-power-apps-269): [`audit.AIAppUserDay`](#auditaiappuserday), [`audit.AIAppResource`](#auditaiappresource), [`audit.AIAppCall`](#auditaiappcall), [`audit.PlatformActivity`](#auditplatformactivity), [`audit.ActivityUserDay`](#auditactivityuserday), [`audit.DlpDay`](#auditdlpday), [`audit.DlpMatch`](#auditdlpmatch)
- [The weekly report (3.1.2)](#the-weekly-report-312): [`audit.WeeklyReportSend`](#auditweeklyreportsend)
- [License events (license-event database)](#license-events-license-event-database): [`audit.LicenseEvent`](#auditlicenseevent), [`audit.LicenseEventChunk`](#auditlicenseeventchunk), [`audit.LicenseEventProperty`](#auditlicenseeventproperty), [`audit.vLicenseChange`](#auditvlicensechange), [`audit.LicenseSkuChange`](#auditlicenseskuchange), [`audit.LicenseSkuChangeRun`](#auditlicenseskuchangerun), [`audit.vLicenseSkuChangeFromRecords`](#auditvlicenseskuchangefromrecords), [`audit.vLicenseSkuChange`](#auditvlicenseskuchange), [`audit.AgentEvent`](#auditagentevent), [`audit.vLicenseLifecycle`](#auditvlicenselifecycle)
- [bi: dimensions](#bi-dimensions): [`bi.DimDate`](#bidimdate), [`bi.DimUser`](#bidimuser), [`bi.DimApp`](#bidimapp), [`bi.DimAgent`](#bidimagent), [`bi.DimModel`](#bidimmodel), [`bi.DimSku`](#bidimsku), [`bi.DimLicenseStatus`](#bidimlicensestatus), [`bi.DimLicenseType`](#bidimlicensetype), [`bi.DimSensitivityLabel`](#bidimsensitivitylabel), [`bi.DimFileType`](#bidimfiletype), [`bi.DimResourceType`](#bidimresourcetype), [`bi.DimResourceAction`](#bidimresourceaction), [`bi.DimSite`](#bidimsite)
- [bi: facts and refresh log](#bi-facts-and-refresh-log): [`bi.FactInteraction`](#bifactinteraction), [`bi.FactSession`](#bifactsession), [`bi.FactLicenseChange`](#bifactlicensechange), [`bi.FactLicensedDay`](#bifactlicensedday), [`bi.FactAccessedResource`](#bifactaccessedresource), [`bi.FactAgentEvent`](#bifactagentevent), [`bi.RebuildInProgress`](#birebuildinprogress), [`bi.RefreshRun`](#birefreshrun)
- [bi: views Power BI imports](#bi-views-power-bi-imports): [`bi.Interactions`](#biinteractions), [`bi.Sessions`](#bisessions), [`bi.LicenseChanges`](#bilicensechanges), [`bi.LicensedDays`](#bilicenseddays), [`bi.Calendar`](#bicalendar), [`bi.Users`](#biusers), [`bi.Apps`](#biapps), [`bi.Agents`](#biagents), [`bi.Models`](#bimodels), [`bi.Skus`](#biskus), [`bi.LicenseStatuses`](#bilicensestatuses), [`bi.AccessedResources`](#biaccessedresources), [`bi.LicenseTypes`](#bilicensetypes), [`bi.SensitivityLabels`](#bisensitivitylabels), [`bi.ResourceTypes`](#biresourcetypes), [`bi.FileTypes`](#bifiletypes), [`bi.ResourceActions`](#biresourceactions), [`bi.Sites`](#bisites), [`bi.AgentEvents`](#biagentevents), [`bi.OrgSecurity`](#biorgsecurity), [`bi.ValueNames`](#bivaluenames), [`bi.LicensePool`](#bilicensepool), [`bi.DepartmentWeek`](#bidepartmentweek), [`bi.UseCases`](#biusecases), [`bi.JobFamilies`](#bijobfamilies), [`bi.EventMarkers`](#bieventmarkers)
- [dash: the Copilot Insights dashboard](#dash-the-copilot-insights-dashboard): [`dash.Setting`](#dashsetting), [`dash.CostPrice`](#dashcostprice), [`dash.CostSuite`](#dashcostsuite), [`dash.SkuName`](#dashskuname), [`dash.CopilotSuiteOnly`](#dashcopilotsuiteonly), [`dash.CopilotSuiteHolder`](#dashcopilotsuiteholder), [`dash.RoleGrant`](#dashrolegrant), [`dash.AccessProfile`](#dashaccessprofile), [`dash.AccessProfileRole`](#dashaccessprofilerole), [`dash.ProfileGrant`](#dashprofilegrant), [`dash.AgentAlias`](#dashagentalias), [`dash.SensitivityLabel`](#dashsensitivitylabel), [`dash.AuditLog`](#dashauditlog), [`dash.Person`](#dashperson), [`dash.OrgClosure`](#dashorgclosure), [`dash.UserDay`](#dashuserday), [`dash.FlaggedInteraction`](#dashflaggedinteraction), [`dash.ResourceDay`](#dashresourceday), [`dash.AgentUse`](#dashagentuse), [`dash.RefreshRun`](#dashrefreshrun), [`dash.AgentInventory`](#dashagentinventory), [`dash.AgentChildLink`](#dashagentchildlink), [`dash.AgentInventoryRun`](#dashagentinventoryrun), [`dash.Agent`](#dashagent)
- [dash: operations (2.3.0)](#dash-operations-230): [`dash.Secret`](#dashsecret), [`dash.Subscription`](#dashsubscription), [`dash.NotificationLog`](#dashnotificationlog), [`dash.AlertState`](#dashalertstate), [`dash.Target`](#dashtarget), [`dash.TimeSaving`](#dashtimesaving), [`dash.TrainingAttendance`](#dashtrainingattendance), [`dash.ValueName`](#dashvaluename), [`dash.FlagCase`](#dashflagcase), [`dash.DigestPreference`](#dashdigestpreference), [`dash.DigestSend`](#dashdigestsend), [`dash.LicenseAction`](#dashlicenseaction), [`dash.LicenseContract`](#dashlicensecontract), [`dash.SavedView`](#dashsavedview), [`dash.NonHumanException`](#dashnonhumanexception), [`dash.Reservation`](#dashreservation), [`dash.ReservationRate`](#dashreservationrate), [`dash.TechnologyAllocation`](#dashtechnologyallocation), [`dash.ReservationSnapshot`](#dashreservationsnapshot), [`dash.CreditRoute`](#dashcreditroute), [`dash.CreditChargeSnapshot`](#dashcreditchargesnapshot), [`dash.CreditCommitment`](#dashcreditcommitment), [`dash.JobFamilyRule`](#dashjobfamilyrule), [`dash.UseCaseRule`](#dashusecaserule), [`dash.EventMarker`](#dasheventmarker), [`dash.Extract`](#dashextract)
- [Left over from 1.x](#left-over-from-1x)

## The scripts

Every table, view, procedure and function comes from a script in `sql/`. Each is idempotent: it creates what is missing, adds columns a newer version needs, and never drops data. Three run themselves; the rest are run once, in the order [DEPLOYMENT.md](DEPLOYMENT.md) gives (steps 7 and 8).

| Script | Database | Run | Creates |
|---|---|---|---|
| `sql/Create-CopilotAuditSchema.sql` | Both | Automatically, at the start of every run of Invoke-CopilotAuditBackfill.ps1 and of the CSV importer | Backfill and load state and staging: CopilotBackfillRange, CopilotBackfillWindow, CopilotAuditStage, CopilotLoad, CopilotLoadWindow, CopilotLoadStage. The pipeline's settings, runs, steps and certificates: PipelineSetting, PipelineRun, PipelineRunStep, PipelineCertificate. The shared function fnShredCopilotInteraction. |
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
| `sql/Create-LicenseEventSchema.sql` | License events | By hand, once (step 7) | LicenseEvent, LicenseEventChunk, LicenseEventProperty, LicenseSkuChange and LicenseSkuChangeRun (2.5.4), vLicenseChange, vLicenseSkuChangeFromRecords, vLicenseSkuChange, vLicenseLifecycle. |
| `sql/Create-LicenseEventProcedure.sql` | License events | By hand, once (step 7) | usp_ShredLicenseEvents, and usp_RefreshLicenseSkuChange (2.5.4), which the shred runs at its end. Installed over events shredded before 2.5.4, it works out their SKU changes once. |
| `sql/Create-CopilotExternalUsageSchema.sql` | Copilot | By Install-CopilotAudit.ps1 | AI usage outside Microsoft 365 Copilot (2.6.2): GitHubCopilotSeat, GitHubCopilotDay, GitHubCopilotUserDay and GitHubCopilotCost, filled by tools/Import-GitHubCopilotUsage.ps1; FoundryDeployment, FoundryUsageDay and FoundryCostDay, filled by tools/Import-FoundryUsage.ps1; and ExternalUsageRun, each run of either. From 2.6.6, FabricCapacityDay, FabricWorkloadDay, PowerAutomateFlow, PowerPlatformUserActivity, PowerPlatformCapacityDay and PowerPlatformCreditDay, filled by tools/Import-CostCsv.ps1, for Leadership > Costs and savings. |
| `sql/Create-CopilotDashboardSchema.sql` | Copilot | By hand, once, for the dashboard (step 22) | The dash schema: Setting (seeded), RoleGrant, AgentAlias, SensitivityLabel, AuditLog, Person, OrgClosure, UserDay, ResourceDay, AgentUse, RefreshRun, CostPrice and CostSuite (2.6.6, seeded); the views dash.Agent, dash.CopilotSuiteOnly and dash.CopilotSuiteHolder (2.6.6) and the function dash.fnScopeUsers. |
| `sql/Create-CopilotOperationsSchema.sql` | Copilot | By Install-CopilotAudit.ps1 -Dashboard, or the dashboard installer with -GrantDatabaseAccess | The dashboard's operations (2.3.0): encrypted secrets, digests, alerts, targets, time saved, training, value names, flag triage, digest preferences and sends, license actions and contracts, Copilot reservations, their rate, the Technology-funded pool and month-end snapshots (2.6.8), saved views, job-family and use-case rules, events and extracts; the role CopilotInsightsApp and its grants; privacy mode and retention procedures; the bi views OrgSecurity, ValueNames, LicensePool, DepartmentWeek, UseCases, JobFamilies and EventMarkers. |
| `sql/Create-CopilotActivityRollup.sql` | Copilot | By Install-CopilotAudit.ps1 | AIAppUserDay, AIAppResource, the function fnAIAppResourceGuess and usp_RollupActivity (2.6.9), which reads the activity database into them and into PowerPlatformUserActivity. |
| `sql/Create-WeeklyReport.sql` | Copilot | By Install-CopilotAudit.ps1 (3.1.2) | WeeklyReportSend and bi.usp_WeeklyCopilotReport: the weekly Copilot report's send log and its figures, which tools/Send-CopilotWeeklyReport.ps1 reads. |
| `sql/Create-ActivitySchema.sql` | Activity | By Install-CopilotAudit.ps1 -ActivityDatabase | AIAppCall and PlatformActivity (2.6.9): the activity database's records, a row each. |
| `sql/Create-ActivityProcedure.sql` | Activity | By Install-CopilotAudit.ps1 -ActivityDatabase | usp_ShredActivity (2.6.9), which the incremental load runs after each merge. |
| `sql/Create-AgentEventProcedure.sql` | License events | By Install-CopilotAudit.ps1, and after each license-event load | audit.AgentEvent and audit.usp_ShredAgentEvents (2.3.0): Copilot Studio authoring records, one row each. |
| `sql/Create-CopilotDashboardProcedure.sql` | Copilot | By hand, once, for the dashboard (step 22) | dash.usp_RefreshDashboard, which the backfill's -Incremental run and the CSV importer call after bi.usp_RefreshBi. |

**Two databases, or three.** The Copilot database (`CopilotAudit`) holds everything below except the license-event section and the activity database's two tables. The license-event database (`CopilotLicenseEvents`) holds the same loading, state and final-record tables, filled with directory events instead, plus the license-event tables and views. The activity database (2.6.9, optional, `ActivityDatabase` in the settings) holds them too, filled by Purview record type (AI app calls, Power BI and Power Apps), plus `audit.AIAppCall` and `audit.PlatformActivity`.

**Procedures and function.** `audit.fnShredCopilotInteraction` (inline; JSON to interaction columns), `audit.usp_MergeCopilotLoad`, `audit.usp_MergeCopilotCsv`, `audit.usp_FlattenCopilotAudit`, `audit.usp_PurgeCopilotAuditJson`, `audit.usp_BuildCopilotSessions`, `audit.usp_ShredLicenseEvents`, `audit.usp_RefreshLicenseSkuChange`, `bi.usp_RefreshBi` and `dash.usp_RefreshDashboard`, from the scripts above. `dash.fnScopeUsers(@RootUpn)` (inline) returns the `bi.DimUser` keys of a person and everyone under them: the dashboard joins every query for a part of the organization to it. `dash.fnReservationMember(@AsOf)` (inline, 2.6.8) returns each reservation's people on a day: everyone under its leader, a person under two listed leaders counted with the nearer one; `dash.usp_TakeReservationSnapshot` (2.6.8), which the nightly run calls, keeps the last month-end's reservation position once. `audit.usp_ShredActivity` (activity database) and `audit.usp_RollupActivity` (Copilot database, reading the activity database by name), 2.6.9.

## Naming conventions

A name says who an object is for, what kind of object it is, and what one row of it is. New objects follow the same rules.

**The schema says who the object is for.**

| Schema | What lives there | Who reads it |
|---|---|---|
| `audit` | What the pipeline loads and works out: the records, the flattened records, licenses, the directory, sessions, and the loads' own state | The pipeline; an analyst who needs record-level detail |
| `bi` | The star schema for reporting | Power BI, Grafana and other reporting tools, through the views and the `CopilotReporting` role; the dashboard |
| `dash` | What the dashboard itself keeps: settings, roles, rollups, logs and what its Admin pages edit | The dashboard only |
| `dbo` | The agent workbook's sheets, as they are in the workbook | The dashboard's agent pages |

**The prefix says what kind of object it is.**

| Prefix | Meaning | Example |
|---|---|---|
| `v` (in `audit`) | A view | `audit.vCopilotAdoption`, `audit.vLicenseChange` |
| `usp_` | A stored procedure | `audit.usp_FlattenCopilotAudit`, `bi.usp_RefreshBi` |
| `fn` | A function | `audit.fnShredCopilotInteraction`, `bi.fnAgentIdentity` |
| `Dim` (in `bi`) | A dimension table: one row per thing interactions are cut by | `bi.DimUser`, `bi.DimAgent` |
| `Fact` (in `bi`) | A fact table: one row per thing that happened | `bi.FactInteraction`, `bi.FactSession` |
| None, a plural (in `bi`) | The view a report reads, over the `Dim` or `Fact` of that name | `bi.Users` over `bi.DimUser`, `bi.Interactions` over `bi.FactInteraction` |

Views in `bi` and `dash` carry no `v`: in `bi` the views are what a report is meant to read, and the tables are the part with the prefix.

**The suffix says what one row is.**

| Suffix | One row is | Example |
|---|---|---|
| `Day` | One thing on one day | `dash.UserDay`, `audit.FoundryUsageDay`, `audit.LicenseSkuDay` |
| `Week` | One thing in one week | `bi.DepartmentWeek` |
| `Run` | One run of a job, with its start, end and status | `audit.PipelineRun`, `bi.RefreshRun`, `audit.ExternalUsageRun` |
| `Stage` | Rows waiting to be merged, emptied afterwards | `audit.CopilotLoadStage`, `audit.CopilotCsvStage` |
| `Window`, `Range` | A slice of time a load works through | `audit.CopilotBackfillWindow`, `audit.CopilotBackfillRange` |
| `Catalog` | Every distinct value seen | `audit.ValueCatalog`, `audit.AgentCatalog` |
| `Log` | A record of what happened, only added to | `dash.AuditLog`, `dash.NotificationLog` |
| `Rule` | A classification an administrator defines | `dash.JobFamilyRule`, `dash.UseCaseRule` |
| `Setting` | A name and its value | `audit.PipelineSetting`, `dash.Setting` |

**The stem says the source or the subject.** `Copilot…` is Microsoft 365 Copilot's audit records (`CopilotAudit`, `CopilotInteraction`, `CopilotSession`); `RawLog…` is their flattened form; `License…` is license events and SKUs; `Pipeline…` is the nightly run itself; `GitHubCopilot…` and `Foundry…` are the two sources added in 2.6.2.

**Columns.**

| Pattern | Meaning | Example |
|---|---|---|
| `…Utc` | A time in UTC | `StartedUtc`, `LoadedUtc`, `FirstSeenUtc` |
| `Day`, `DateKey` | A date in the reporting time zone; `DateKey` is that date as a number, `yyyymmdd` | `20261008` |
| `…Key` (in `bi`) | The number that joins a fact to its dimension. It means nothing by itself and is never shown | `UserKey`, `AgentKey` |
| `…Id` | An identifier that comes from the source | `RecordId`, `AgentId`, `SubscriptionId` |
| `Is…`, `Has…`, `Used…` | Yes or no | `IsShared`, `HasBlockedResource`, `UsedWebSearch` |
| `Rows…`, `…Count`, `Interactions` | A count | `RowsWritten`, `PromptCount` |

Scripts follow the same idea: `Create-…Schema.sql` creates tables and views, `Create-…Procedure.sql` creates the procedure that fills them, and a PowerShell tool is named for what it does to what (`Import-AgentWorkbook.ps1`, `Test-CopilotDataHealth.ps1`). [SCRIPTS.md](SCRIPTS.md) summarizes every script.

## The bi schema: what each table and view is for, and what uses it

`bi` is a star schema: narrow fact tables with number keys, small dimension tables, and a view over each for reports. `bi.usp_RefreshBi` fills the tables every night from the `audit` tables, and nothing else writes to them. Three kinds of reader use it:

- **The dashboard** reads the `Dim` and `Fact` tables directly, and `dash.usp_RefreshDashboard` builds the dashboard's rollups from the facts.
- **The Power BI model** (`powerbi\generate_model.py`) imports the views, and only the views.
- **Reporting accounts** (Grafana, a report of your own) are members of `CopilotReporting`, which is granted the views and not the tables, so privacy mode's pseudonyms cannot be read around.

So write a report against the views. Join a fact view to a dimension view on the `…Key` column they share.

**Dimension tables.** One row per thing; filled as new values arrive, and a row keeps its key for good.

| Table | One row per | View over it | Read by |
|---|---|---|---|
| `bi.DimDate` | Day, with its week, month, quarter and year | `bi.Calendar` | Every dashboard report that has a period; the model |
| `bi.DimUser` | Person, with department, title, location, manager chain, license today | `bi.Users` | Nearly every dashboard report; the model; `bi.OrgSecurity` |
| `bi.DimApp` | App Copilot ran in, with its category | `bi.Apps` | The dashboard's app, adoption, agent, security and region reports; the model |
| `bi.DimAgent` | Identity the audit log gives an agent. A row with no `MergedIntoKey` is an agent (2.6.3) | `bi.Agents` | The dashboard's agent reports and agent workbook pages; `dash.Agent`; the model |
| `bi.DimModel` | AI model | `bi.Models` | The dashboard's governance report; the model |
| `bi.DimSku` | License SKU | `bi.Skus` | The dashboard's licensing reports; the model |
| `bi.DimLicenseStatus` | License status at the time of use: licensed, not licensed, unknown | `bi.LicenseStatuses` | The model |
| `bi.DimLicenseType` | License type | `bi.LicenseTypes` | The dashboard's licensing and forecasting reports; the model |
| `bi.DimSensitivityLabel` | Sensitivity label | `bi.SensitivityLabels` | The dashboard's security and compliance reports and its label rollup; the model |
| `bi.DimFileType` | File type of a resource Copilot read | `bi.FileTypes` | The dashboard's use-by-license report; the model |
| `bi.DimResourceType` | Kind of resource: a file, a mail, a meeting | `bi.ResourceTypes` | The dashboard's app and compliance reports; the model |
| `bi.DimResourceAction` | What was done with a resource | `bi.ResourceActions` | The model |
| `bi.DimSite` | SharePoint site or OneDrive | `bi.Sites` | The dashboard's site report and its site rollup; the model |

**Fact tables.** One row per thing that happened; a nightly refresh rewrites the days since the last one.

| Table | One row per | View over it | Read by |
|---|---|---|---|
| `bi.FactInteraction` | Copilot interaction: who, when, in which app, with which agent and model, under which license | `bi.Interactions` | Most dashboard reports; the rollups `dash.UserDay`, `dash.FlaggedInteraction` and `dash.AgentUse`; the model |
| `bi.FactSession` | Session: a run of one person's prompts close together | `bi.Sessions` | The dashboard's session and use-by-license reports; the model |
| `bi.FactLicenseChange` | License assigned or removed | `bi.LicenseChanges` | The dashboard's license changes report; the model |
| `bi.FactLicensedDay` | Person and day on which they held a license | `bi.LicensedDays` | The dashboard's adoption, licensing and forecasting reports and its data API; the model |
| `bi.FactAccessedResource` | Resource an interaction read | `bi.AccessedResources` | The dashboard's compliance, app and resource reports; the rollup `dash.ResourceDay`; the model |
| `bi.FactAgentEvent` | Agent created, changed, published, shared or deleted | `bi.AgentEvents` | The dashboard's agent build report; the health check; the model |

**Housekeeping tables.** No view; not for reports.

| Table | What it holds | Read by |
|---|---|---|
| `bi.RefreshRun` | One row per refresh of the schema: when, what it covered, how many rows | Admin › Health; `dash.usp_RefreshDashboard`, to know what changed; the maintenance and nightly-profile tools |
| `bi.RebuildInProgress` | A row while a full rebuild is running, so a reader knows the facts are incomplete | `bi.usp_RefreshBi`, `dash.usp_RefreshDashboard`, the health check |

**Views with no table of their own.** Each is built from other tables and views.

| View | What it gives | Built from | Read by |
|---|---|---|---|
| `bi.OrgSecurity` | One row per viewer and person they may see: what Power BI's row-level security filters by. Empty in privacy mode | The org chart and the dashboard's role grants | The model |
| `bi.DepartmentWeek` | Active people, interactions and sessions by department and week, with small groups hidden: for a report that must not show individuals | The dashboard's daily rollup, `dash.UserDay` | The dashboard's delivery report; the model |
| `bi.UseCases` | The use case of an interaction: the app, whether it searched the web, whether an agent answered | `bi.DimApp` and the dashboard's use-case rules | The dashboard's board pack; the model |
| `bi.JobFamilies` | Each person's job family | The dashboard's job-family rules | The model |
| `bi.LicensePool` | The tenant's seats, one row per SKU per night | The nightly license snapshot from Microsoft Graph, `audit.LicenseSkuDay` | The model |
| `bi.ValueNames` | Each distinct value of the coded columns of the flattened records, with the name an administrator gave it | `audit.ValueCatalog` and `dash.ValueName` | Admin › Value names; the model |
| `bi.EventMarkers` | Dates marked on trend charts: a launch, a training wave | `dash.EventMarker` | The model |

**Procedures and the function.** `bi.usp_RefreshBi` fills the schema; `bi.usp_NameAgents` names the agents at the end of every refresh; `bi.usp_MergeAgents` (2.6.3) makes one agent of the identities the audit log gives the same agent; `bi.fnAgentIdentity` is the one rule for which agent a record belongs to.

## Governance Workbench database

The Governance Workbench (3.0.0) keeps its data in a database of its own, `CopilotGovernance` by default, beside the Copilot database on the same SQL Server. Nothing on this page is in it, and nothing of it is in the Copilot database: the two share no table names. It is needed only when the Workbench is turned on ([OPERATIONS.md](OPERATIONS.md#governance-workbench)).

- **SQL Server 2022 or later.** It uses ledger tables (decisions, evidence metadata, attestations, verifications and the audit trail: rows are added, never changed or deleted, and a digest proves it), system-versioned temporal tables (in `sec` and `core`, the seat tables included: every earlier version of a row is kept), and row-level security (each line of business sees only its own rows; each connection carries the person and whether they hold a central role in `SESSION_CONTEXT`).
- **Its scripts** are in `governance/src/Governance.Database/Scripts`, run in name order by `governance/src/Governance.Database/Deploy-Database.ps1`. Each is safe to rerun and only adds, and from 3.0.0 the database follows the same compatibility rule as this one: nothing is dropped, renamed or narrowed.
- **Its schemas**, from `001-schemas.sql`:

| Schema | Holds |
|---|---|
| `ref` | Reference data and settings (`ref.Setting`) |
| `core` | The registry: funding owners, cost centers, services, control templates, workloads, envelopes, commercial inventory, Copilot seat reservations |
| `sec` | People, roles, delegations and the RACI matrix |
| `wf` | Requests, tasks, decisions and ITSM tickets |
| `evd` | Evidence |
| `ntf` | Notifications: the outbox, deliveries and digests |
| `intg` | Integration snapshots and job runs (`intg.JobRun`) |
| `ops` | Consumption events and findings |
| `aud` | The audit trail |
| `rpt` | Reporting views; `rpt.vSeatReservationCurrent` gives today's Copilot seats by line of business (reserved, assigned, over, unused, the last certified chargeback) for Power BI and Grafana |

- **Its database roles:** `app_web` for the dashboard's account, `app_worker` for the worker's, `gov_reader` for reporting and audit export (read only). None is a database owner.

**Roles for the Workbench in this database.** The role check on `dash.RoleGrant` and `dash.AccessProfileRole` (`sql/Create-CopilotDashboardSchema.sql`) takes three more roles from 3.0.0: `Governance`, `GovernanceOperator` and `GovernanceAuditor`, the Workbench's page roles. Run on an existing database, the script widens the check; nothing already granted changes.

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
| 4 | `Operations` | nvarchar(2000) | **no** |  | the operation set, as recorded (400 before 2.7.6) |
| 5 | `StartedUtc` | datetime2(0) | **no** | `sysutcdatetime` |  |
| 6 | `CompletedUtc` | datetime2(0) | yes |  |  |
| 7 | `Source` | varchar(8) | yes |  | 'CSV' when a CSV import wrote the row |

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

*Table.* One row per -Incremental load: its range, operations, when it finished downloading and was merged, how many records were new or already there, and how many audit searches it took.

**Script** `Create-CopilotAuditSchema.sql` · **In** Copilot and license-event databases · **Columns added by** `Create-CopilotLoadProcedure.sql`

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `LoadId` | int | **no** | **PK** identity |  |
| 2 | `RangeStartUtc` | datetime2(0) | **no** |  |  |
| 3 | `RangeEndUtc` | datetime2(0) | **no** |  |  |
| 4 | `Operations` | nvarchar(2000) | **no** |  | the operation set, as recorded (400 before 2.7.6) |
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

### audit.PipelineCertificate

*Table.*One row for each certificate the pipeline signs in with, as the job server held it at the start of its last run: Purview, for the app that reads the audit log and the licenses through Microsoft Graph, AgentWorkbook, for the app that fetches the agent workbook from SharePoint Online, where the settings name one, and Foundry (2.6.2), where the Foundry loader signs in with a certificate of its own. Invoke-CopilotPipeline.ps1 rewrites it every run and removes the row of a certificate no longer in use. Admin &gt; Health shows each with its details, and the dashboard's alert watches each one's expiry. audit.PipelineRun still keeps the audit app's certificate with each run.

**Script** `Create-CopilotAuditSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `Purpose` | varchar(32) | **no** | **PK** | Purview \| AgentWorkbook \| Foundry |
| 2 | `Thumbprint` | varchar(64) | **no** |  | as the settings file names it |
| 3 | `Found` | bit | **no** |  | 0: no certificate of that thumbprint on the job server |
| 4 | `Subject` | nvarchar(400) | yes |  |  |
| 5 | `Issuer` | nvarchar(400) | yes |  |  |
| 6 | `NotBeforeUtc` | datetime2(0) | yes |  |  |
| 7 | `ExpiresUtc` | datetime2(0) | yes |  |  |
| 8 | `Store` | varchar(32) | yes |  | LocalMachine\\My \| CurrentUser\\My |
| 9 | `ClientId` | nvarchar(64) | yes |  | the app registration it signs in as |
| 10 | `TenantId` | nvarchar(256) | yes |  |  |
| 11 | `Host` | nvarchar(128) | yes |  | the job server that holds it |
| 12 | `SeenUtc` | datetime2(0) | **no** | `sysutcdatetime` |  |

Indexes and constraints:

- `PK_PipelineCertificate`: primary key, clustered (Purpose)

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

### audit.AgentCatalog

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
| 12 | `EntraObjectId` | uniqueidentifier | yes |  | the account's Entra ID object ID, from the same lookup (2.8.0): the dashboard's refresh copies it to dash.Person, where a sign-in through Entra ID is matched by it first |

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

### audit.GroupMember

*Table.* The members of each AD group granted a role in the dashboard, nested groups included (2.4.0; every role since 2.7.7): the group as granted (DOMAIN\\Group) and the member's UPN. Rewritten by each AD export, so bi.OrgSecurity gives the members of Organization groups the whole organization in Power BI as the dashboard does at sign-in, and Admin › Access › Check someone's access lists the roles a person holds through a group.

**Script** `Create-CopilotEnrichmentSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `GroupName` | nvarchar(256) | **no** | **PK** | as granted: DOMAIN\\Group |
| 2 | `UserPrincipalName` | nvarchar(256) | **no** | **PK** |  |
| 3 | `LoadedUtc` | datetime2(0) | **no** | `sysutcdatetime` |  |

Indexes and constraints:

- `PK_GroupMember`: primary key, clustered (GroupName, UserPrincipalName)

### audit.ReportGroupMember

*Table.* The members now of the two AD groups the weekly Copilot report counts as Users Enabled (3.1.3), nested groups included: CopilotLicenseGroup and CopilotChatGroup in copilot-audit.settings.psd1. Replaced whole by each AD export. The report's note under its table reads it: who used Copilot outside the group of their column.

**Script** `Create-CopilotEnrichmentSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `Role` | varchar(16) | **no** | **PK** | CopilotLicense \| CopilotChat |
| 2 | `GroupName` | nvarchar(256) | **no** |  | as set: DOMAIN\\Group |
| 3 | `UserPrincipalName` | nvarchar(256) | **no** | **PK** |  |
| 4 | `LoadedUtc` | datetime2(0) | **no** | `sysutcdatetime` |  |

Indexes and constraints:

- `PK_ReportGroupMember`: primary key, clustered (Role, UserPrincipalName)

### audit.ReportGroupDay

*Table.* One row per group per day the AD export ran (3.1.3): the two groups' user members, nested groups included, and how many of those accounts are enabled in AD (audit.UserDirectory). Kept, so the weekly report's trend has the count each week had; a second load on one day replaces that day's rows. bi.usp_WeeklyCopilotReport takes a week's Users Enabled from the last day in it.

**Script** `Create-CopilotEnrichmentSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `SnapshotDate` | date | **no** | **PK** | the day of the load, in the reporting time zone |
| 2 | `Role` | varchar(16) | **no** | **PK** | CopilotLicense \| CopilotChat |
| 3 | `GroupName` | nvarchar(256) | **no** |  |  |
| 4 | `Members` | int | **no** |  | user members, enabled or not |
| 5 | `EnabledMembers` | int | **no** |  | of those, accounts enabled in AD |
| 6 | `LoadedUtc` | datetime2(0) | **no** | `sysutcdatetime` |  |

Indexes and constraints:

- `PK_ReportGroupDay`: primary key, clustered (SnapshotDate, Role)

### audit.LicenseSku

*Table.* The tenant's subscriptions as of the license tool's last run (2.3.0): per SKU, seats enabled, in the grace period, suspended and assigned, and whether it is a Copilot SKU.

**Script** `Create-CopilotEnrichmentSchema.sql` · **In** Copilot database · **Columns added by** `Create-CopilotDashboardSchema.sql`

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
| 11 | `CopilotInSuite` | bit | yes |  | 2.6.6: a suite with Microsoft 365 Copilot inside (E7). NULL before 2.6.6. |

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

### audit.vCopilotUsageByDepartment

*View.* Interactions and active users per department, day and app.

**Script** `Create-CopilotEnrichmentSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `Department` | nvarchar(200) | **no** |  |  |
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
| 3 | `Operations` | nvarchar(2000) | **no** |  | the database's operation set, as the backfill records it (400 before 2.7.6) |
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

## AI usage outside Microsoft 365 Copilot (2.6.2), and Fabric and the Power Platform (2.6.6)

Usage and cost of GitHub Copilot and of Microsoft Foundry and Azure OpenAI deployments, each loaded by a tool of its own (tools/Import-GitHubCopilotUsage.ps1, tools/Import-FoundryUsage.ps1) into tables of its own, beside the Copilot audit data and not mixed into it. First plumbing: no report, `bi` view or `dash` table reads them yet; a reporting tool can, with SELECT on the tables. Days are UTC days, as each service reports them, not the reporting time zone's. A table stays empty until its loader is set up ([GITHUB-COPILOT-USAGE.md](GITHUB-COPILOT-USAGE.md), [FOUNDRY-USAGE.md](FOUNDRY-USAGE.md)).

### audit.GitHubCopilotSeat

*Table.* One row per GitHub Copilot seat of the enterprise or organization loaded, as GitHub lists it now: who holds it, the organization and team it was assigned through, the plan, when it was assigned and when it was last used. tools/Import-GitHubCopilotUsage.ps1 replaces a scope's seats whole on each run. A person here is a GitHub login, which is not a Microsoft 365 sign-in; nothing joins the two yet.

**Script** `Create-CopilotExternalUsageSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `Scope` | nvarchar(120) | **no** | **PK** | enterprise:<slug> or org:<name> |
| 2 | `Login` | nvarchar(100) | **no** | **PK** | the GitHub login: not a Microsoft 365 sign-in |
| 3 | `UserId` | bigint | yes |  |  |
| 4 | `Organization` | nvarchar(100) | yes |  | the organization the seat was assigned through |
| 5 | `AssigningTeam` | nvarchar(200) | yes |  |  |
| 6 | `PlanType` | varchar(16) | yes |  | business \| enterprise \| unknown |
| 7 | `CreatedUtc` | datetime2(0) | yes |  | when the seat was assigned |
| 8 | `LastActivityUtc` | datetime2(0) | yes |  | NULL: never used, or telemetry is off in the editor |
| 9 | `LastActivityEditor` | nvarchar(200) | yes |  |  |
| 10 | `LastAuthenticatedUtc` | datetime2(0) | yes |  |  |
| 11 | `PendingCancellationDate` | date | yes |  |  |
| 12 | `LoadedUtc` | datetime2(0) | **no** | `sysutcdatetime` |  |

Indexes and constraints:

- `PK_GitHubCopilotSeat`: primary key, clustered (Scope, Login)

### audit.GitHubCopilotDay

*Table.* One row per UTC day of GitHub Copilot use by the whole enterprise or organization, from GitHub's usage metrics report for that day (enterprise-1-day or organization-1-day): people active, interactions, code generated and accepted, lines of code. Json keeps the whole record, with its breakdowns by editor, feature, language and model. A day is written again each time it is loaded; GitHub's reports run up to three days behind.

**Script** `Create-CopilotExternalUsageSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `Scope` | nvarchar(120) | **no** | **PK** |  |
| 2 | `Day` | date | **no** | **PK** | a UTC day |
| 3 | `DailyActiveUsers` | int | yes |  |  |
| 4 | `WeeklyActiveUsers` | int | yes |  |  |
| 5 | `MonthlyActiveUsers` | int | yes |  |  |
| 6 | `MonthlyActiveChatUsers` | int | yes |  |  |
| 7 | `MonthlyActiveAgentUsers` | int | yes |  |  |
| 8 | `Interactions` | bigint | yes |  | user_initiated_interaction_count |
| 9 | `CodeGenerations` | bigint | yes |  | code_generation_activity_count |
| 10 | `CodeAcceptances` | bigint | yes |  | code_acceptance_activity_count |
| 11 | `LocSuggestedToAdd` | bigint | yes |  |  |
| 12 | `LocSuggestedToDelete` | bigint | yes |  |  |
| 13 | `LocAdded` | bigint | yes |  |  |
| 14 | `LocDeleted` | bigint | yes |  |  |
| 15 | `Json` | nvarchar(max) | yes |  |  |
| 16 | `LoadedUtc` | datetime2(0) | **no** | `sysutcdatetime` |  |

Indexes and constraints:

- `PK_GitHubCopilotDay`: primary key, clustered (Scope, Day)

### audit.GitHubCopilotUserDay

*Table.* One row per person per UTC day they used GitHub Copilot, from the users-1-day report: what they did, the AI credits they used, and whether they used chat, an agent or the command line.

**Script** `Create-CopilotExternalUsageSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `Scope` | nvarchar(120) | **no** | **PK** |  |
| 2 | `Day` | date | **no** | **PK** |  |
| 3 | `UserId` | bigint | **no** | **PK** |  |
| 4 | `Login` | nvarchar(100) | yes |  |  |
| 5 | `Interactions` | bigint | yes |  |  |
| 6 | `CodeGenerations` | bigint | yes |  |  |
| 7 | `CodeAcceptances` | bigint | yes |  |  |
| 8 | `LocAdded` | bigint | yes |  |  |
| 9 | `LocDeleted` | bigint | yes |  |  |
| 10 | `AiCreditsUsed` | decimal(19,4) | yes |  | 1 AI credit is 0.01 USD at GitHub's list price |
| 11 | `UsedChat` | bit | yes |  |  |
| 12 | `UsedAgent` | bit | yes |  |  |
| 13 | `UsedCli` | bit | yes |  |  |
| 14 | `LoadedUtc` | datetime2(0) | **no** | `sysutcdatetime` |  |

Indexes and constraints:

- `PK_GitHubCopilotUserDay`: primary key, clustered (Scope, Day, UserId)

### audit.GitHubCopilotCost

*Table.* What GitHub billed for Copilot: the lines of the enterprise's or organization's billing usage whose product or SKU is Copilot, one per day, product, SKU, model and organization. Amounts are in US dollars. This month and the last are replaced whole on each run, since an open month's lines change until it closes.

**Script** `Create-CopilotExternalUsageSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `CostId` | bigint | **no** | **PK** identity |  |
| 2 | `Scope` | nvarchar(120) | **no** |  |  |
| 3 | `UsageDate` | date | **no** |  |  |
| 4 | `Product` | nvarchar(100) | yes |  |  |
| 5 | `Sku` | nvarchar(200) | yes |  |  |
| 6 | `Model` | nvarchar(200) | yes |  |  |
| 7 | `Organization` | nvarchar(100) | yes |  |  |
| 8 | `UnitType` | nvarchar(50) | yes |  |  |
| 9 | `PricePerUnit` | decimal(19,6) | yes |  |  |
| 10 | `Quantity` | decimal(19,4) | yes |  |  |
| 11 | `GrossAmount` | decimal(19,4) | yes |  |  |
| 12 | `DiscountAmount` | decimal(19,4) | yes |  |  |
| 13 | `NetAmount` | decimal(19,4) | yes |  | what was charged, in USD |
| 14 | `LoadedUtc` | datetime2(0) | **no** | `sysutcdatetime` |  |

Indexes and constraints:

- `PK_GitHubCopilotCost`: primary key, clustered (CostId)
- `IX_GitHubCopilotCost_Date`: nonclustered (Scope, UsageDate)

### audit.FoundryDeployment

*Table.* One row per model deployment of every Azure OpenAI (kind OpenAI) and Microsoft Foundry (kind AIServices) account in the subscriptions loaded, as it stands: model, version, publisher, SKU and capacity. tools/Import-FoundryUsage.ps1 replaces a subscription's deployments whole on each run.

**Script** `Create-CopilotExternalUsageSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `SubscriptionId` | varchar(36) | **no** | **PK** |  |
| 2 | `ResourceGroup` | nvarchar(90) | **no** | **PK** |  |
| 3 | `Account` | nvarchar(64) | **no** | **PK** | the Microsoft.CognitiveServices account |
| 4 | `Deployment` | nvarchar(64) | **no** | **PK** |  |
| 5 | `Kind` | nvarchar(50) | yes |  | OpenAI \| AIServices |
| 6 | `Location` | nvarchar(50) | yes |  |  |
| 7 | `ModelName` | nvarchar(100) | yes |  |  |
| 8 | `ModelVersion` | nvarchar(32) | yes |  |  |
| 9 | `ModelPublisher` | nvarchar(100) | yes |  |  |
| 10 | `SkuName` | nvarchar(50) | yes |  | Standard, GlobalStandard, ProvisionedManaged ... |
| 11 | `Capacity` | int | yes |  |  |
| 12 | `LoadedUtc` | datetime2(0) | **no** | `sysutcdatetime` |  |

Indexes and constraints:

- `PK_FoundryDeployment`: primary key, clustered (SubscriptionId, ResourceGroup, Account, Deployment)

### audit.FoundryUsageDay

*Table.* One row per deployment, model, version and UTC day: requests and input, output and total tokens, from Azure Monitor's metrics of the account. Azure Monitor keeps 93 days, so what is loaded here is the history. There is no user on these metrics: use is by account, deployment and model.

**Script** `Create-CopilotExternalUsageSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `SubscriptionId` | varchar(36) | **no** | **PK** |  |
| 2 | `ResourceGroup` | nvarchar(90) | **no** | **PK** |  |
| 3 | `Account` | nvarchar(64) | **no** | **PK** |  |
| 4 | `Deployment` | nvarchar(64) | **no** | **PK** |  |
| 5 | `ModelName` | nvarchar(100) | **no** | **PK** | '' where the metric does not say |
| 6 | `ModelVersion` | nvarchar(32) | **no** | **PK** |  |
| 7 | `Day` | date | **no** | **PK** |  |
| 8 | `Requests` | bigint | yes |  |  |
| 9 | `InputTokens` | bigint | yes |  |  |
| 10 | `OutputTokens` | bigint | yes |  |  |
| 11 | `TotalTokens` | bigint | yes |  |  |
| 12 | `LoadedUtc` | datetime2(0) | **no** | `sysutcdatetime` |  |

Indexes and constraints:

- `PK_FoundryUsageDay`: primary key, clustered (SubscriptionId, ResourceGroup, Account, Deployment, ModelName, ModelVersion, Day)

### audit.FoundryCostDay

*Table.* One row per account, meter and UTC day: the actual cost Azure reports, before tax, from Cost Management, in the currency the subscription is billed in. Cost is by account and meter (such as "gpt-4o Input Tokens"), not by deployment. A day is written again each time it is loaded: an open month's figures are estimates until five days after it ends.

**Script** `Create-CopilotExternalUsageSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `SubscriptionId` | varchar(36) | **no** | **PK** |  |
| 2 | `ResourceGroup` | nvarchar(90) | **no** | **PK** |  |
| 3 | `Account` | nvarchar(64) | **no** | **PK** |  |
| 4 | `Meter` | nvarchar(200) | **no** | **PK** | such as "gpt-4o Input Tokens"; '' where none |
| 5 | `Day` | date | **no** | **PK** |  |
| 6 | `Cost` | decimal(19,6) | **no** |  |  |
| 7 | `Currency` | nvarchar(8) | yes |  |  |
| 8 | `LoadedUtc` | datetime2(0) | **no** | `sysutcdatetime` |  |

Indexes and constraints:

- `PK_FoundryCostDay`: primary key, clustered (SubscriptionId, ResourceGroup, Account, Meter, Day)

### audit.ExternalUsageRun

*Table.* One row per run of tools/Import-GitHubCopilotUsage.ps1 or tools/Import-FoundryUsage.ps1, written from its start: the source, what it covered, how it ended (Succeeded, Partial when a part could not be read, or Failed), the rows written, and what was loaded or why not. Admin &gt; Health shows the last run of each source.

**Script** `Create-CopilotExternalUsageSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `RunId` | int | **no** | **PK** identity |  |
| 2 | `Source` | varchar(16) | **no** |  | GitHub \| Foundry |
| 3 | `Scope` | nvarchar(400) | yes |  | the enterprise or organization, or the subscriptions |
| 4 | `StartedUtc` | datetime2(0) | **no** | `sysutcdatetime` |  |
| 5 | `EndedUtc` | datetime2(0) | yes |  |  |
| 6 | `Status` | varchar(16) | **no** | `'Running'` | Running \| Succeeded \| Partial \| Failed |
| 7 | `FromDay` | date | yes |  |  |
| 8 | `ToDay` | date | yes |  |  |
| 9 | `RowsWritten` | int | yes |  |  |
| 10 | `Detail` | nvarchar(2000) | yes |  | what was loaded, or why it failed |
| 11 | `CredentialExpiresUtc` | datetime2(0) | yes |  |  |
| 12 | `Host` | nvarchar(128) | yes |  |  |

Indexes and constraints:

- `PK_ExternalUsageRun`: primary key, clustered (RunId)
- `IX_ExternalUsageRun_Source`: nonclustered (Source, RunId)

### audit.FabricCapacityDay

*Table.* One row per Fabric capacity per day (2.6.6), from the Capacity Metrics app and the capacity's cost: its size, the capacity unit seconds used, the day's peak, the minutes it throttled, the hours it ran and what it cost. Leadership &gt; Costs and savings reads each capacity's last 30 days. Loaded with tools/Import-CostCsv.ps1.

**Script** `Create-CopilotExternalUsageSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `CapacityId` | nvarchar(200) | **no** | **PK** | the capacity's id, or its name where the export has no id |
| 2 | `Day` | date | **no** | **PK** |  |
| 3 | `CapacityName` | nvarchar(200) | yes |  |  |
| 4 | `Sku` | varchar(16) | yes |  | F2 ... F2048, or P1 ... P5 |
| 5 | `CapacityUnits` | int | yes |  | the SKU's capacity units: F64 is 64 |
| 6 | `Region` | nvarchar(64) | yes |  |  |
| 7 | `Billing` | varchar(16) | yes |  | PayAsYouGo \| Reserved |
| 8 | `ActiveHours` | decimal(5,2) | yes |  | hours it ran: 24, or fewer on a day it was paused |
| 9 | `CuSecondsUsed` | decimal(19,2) | yes |  | capacity unit seconds used, after smoothing |
| 10 | `PeakPercent` | decimal(9,2) | yes |  | the day's highest utilization, as a percentage of the capacity |
| 11 | `ThrottledMinutes` | int | yes |  | minutes interactive work was delayed or rejected |
| 12 | `Cost` | decimal(19,4) | yes |  | what the day cost, in Currency |
| 13 | `Currency` | nvarchar(8) | yes |  |  |
| 14 | `LoadedUtc` | datetime2(0) | **no** | `sysutcdatetime` |  |

Indexes and constraints:

- `PK_FabricCapacityDay`: primary key, clustered (CapacityId, Day)

### audit.FabricWorkloadDay

*Table.* One row per capacity, day and workload (2.6.6): the capacity units each kind of work used, its operations and people. A workload whose name starts with Copilot is Copilot in Power BI or Fabric, whose share of the capacity, and so of its cost, the costs page shows.

**Script** `Create-CopilotExternalUsageSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `CapacityId` | nvarchar(200) | **no** | **PK** |  |
| 2 | `Day` | date | **no** | **PK** |  |
| 3 | `Workload` | nvarchar(64) | **no** | **PK** | Power BI, Copilot in Power BI, Data Engineering, Data Warehouse ... |
| 4 | `CuSeconds` | decimal(19,2) | yes |  |  |
| 5 | `Operations` | bigint | yes |  | queries, refreshes, notebook runs, Copilot requests |
| 6 | `Users` | int | yes |  | distinct people behind them that day |
| 7 | `LoadedUtc` | datetime2(0) | **no** | `sysutcdatetime` |  |

Indexes and constraints:

- `PK_FabricWorkloadDay`: primary key, clustered (CapacityId, Day, Workload)

### audit.PowerAutomateFlow

*Table.* Every cloud flow as a flow inventory last listed it (2.6.6): owner, premium or not, how it is licensed, its state and its runs in the last 30 days. The costs page finds Process licenses on quiet flows, failing flows, and the value of the runs. Usually loaded with -Replace.

**Script** `Create-CopilotExternalUsageSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `EnvironmentName` | nvarchar(200) | **no** | **PK** |  |
| 2 | `FlowId` | nvarchar(100) | **no** | **PK** |  |
| 3 | `FlowName` | nvarchar(400) | yes |  |  |
| 4 | `OwnerUpn` | nvarchar(256) | yes |  |  |
| 5 | `IsPremium` | bit | yes |  | uses a premium connector, so its owner needs a premium license |
| 6 | `LicenseModel` | varchar(16) | yes |  | PerUser \| Process \| Standard |
| 7 | `State` | varchar(16) | yes |  | Started \| Stopped \| Suspended |
| 8 | `CreatedUtc` | datetime2(0) | yes |  |  |
| 9 | `LastRunUtc` | datetime2(0) | yes |  |  |
| 10 | `Runs30` | int | yes |  | runs in the last 30 days |
| 11 | `FailedRuns30` | int | yes |  |  |
| 12 | `MinutesSavedPerRun` | decimal(9,2) | yes |  |  |
| 13 | `LoadedUtc` | datetime2(0) | **no** | `sysutcdatetime` |  |

Indexes and constraints:

- `PK_PowerAutomateFlow`: primary key, clustered (EnvironmentName, FlowId)

### audit.PowerPlatformUserActivity

*Table.* Who used Power BI, Power Apps, Power Automate and Copilot Studio lately (2.6.6), from the admin center's usage exports; from 2.6.9 the Power BI and Power Apps rows from the activity database's records, by `audit.usp_RollupActivity`. With audit.UserLicenseSku, a premium license held by someone who does not use the product.

**Script** `Create-CopilotExternalUsageSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `UserPrincipalName` | nvarchar(256) | **no** | **PK** |  |
| 2 | `Product` | varchar(32) | **no** | **PK** | Power Apps \| Power Automate \| Copilot Studio |
| 3 | `LastActiveUtc` | datetime2(0) | yes |  |  |
| 4 | `ActiveDays30` | int | yes |  |  |
| 5 | `LoadedUtc` | datetime2(0) | **no** | `sysutcdatetime` |  |

Indexes and constraints:

- `PK_PowerPlatformUserActivity`: primary key, clustered (UserPrincipalName, Product)

### audit.PowerPlatformCapacityDay

*Table.* The tenant's Dataverse database, file and log capacity each day it was read (2.6.6): entitled and used. Used over entitled is overage, billed as add-on capacity.

**Script** `Create-CopilotExternalUsageSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `Day` | date | **no** | **PK** |  |
| 2 | `CapacityType` | varchar(16) | **no** | **PK** | Database \| File \| Log |
| 3 | `EntitledGb` | decimal(19,2) | yes |  |  |
| 4 | `UsedGb` | decimal(19,2) | yes |  |  |
| 5 | `LoadedUtc` | datetime2(0) | **no** | `sysutcdatetime` |  |

Indexes and constraints:

- `PK_PowerPlatformCapacityDay`: primary key, clustered (Day, CapacityType)

### audit.PowerPlatformCreditDay

*Table.* Copilot Credits (Copilot Studio's agents) and AI Builder credits per environment and day (2.6.6), and how they were paid: from a prepaid pack, or pay-as-you-go with its cost.

**Script** `Create-CopilotExternalUsageSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `Day` | date | **no** | **PK** |  |
| 2 | `EnvironmentName` | nvarchar(200) | **no** | **PK** |  |
| 3 | `Product` | varchar(32) | **no** | **PK** | Copilot Studio \| AI Builder |
| 4 | `Billing` | varchar(16) | **no** | **PK** | Prepaid \| PayAsYouGo |
| 5 | `Credits` | decimal(19,2) | yes |  |  |
| 6 | `Cost` | decimal(19,4) | yes |  | what pay-as-you-go charged; for Prepaid, NULL (the pack is a license) |
| 7 | `Currency` | nvarchar(8) | yes |  |  |
| 8 | `LoadedUtc` | datetime2(0) | **no** | `sysutcdatetime` |  |

Indexes and constraints:

- `PK_PowerPlatformCreditDay`: primary key, clustered (Day, EnvironmentName, Product, Billing)

## AI apps, Power BI and Power Apps (2.6.9)

The activity database's records, and what the Copilot database keeps of them for the dashboard. `audit.usp_RollupActivity` copies the second into the first each night; the dashboard reads only the Copilot database.

### audit.AIAppUserDay

*Table.* AI app calls (ConnectedAIAppInteraction: Azure AI Foundry and Azure OpenAI from the organization's own apps) by day, resource, app, subscription and caller (2.6.9), for Governance &gt; AI apps beyond Copilot. Days are the reporting time zone's. Rewritten from the rollup's day by `audit.usp_RollupActivity`.

**Script** `Create-CopilotActivityRollup.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `Day` | date | **no** | **PK** | a day in the reporting time zone |
| 2 | `Resource` | nvarchar(200) | **no** | **PK** | the Azure resource, or the Entra app id |
| 3 | `ApplicationName` | nvarchar(200) | **no** | **PK** | '' where the record names none |
| 4 | `SubscriptionId` | varchar(64) | **no** | **PK** | '' where the record names none |
| 5 | `CallerKind` | varchar(16) | **no** |  | Person \| Unattributed |
| 6 | `Caller` | nvarchar(256) | **no** | **PK** | the sign-in, or 'unattributed' |
| 7 | `Calls` | int | **no** |  | records: a prompt or a response each |
| 8 | `Prompts` | int | **no** |  |  |
| 9 | `Threads` | int | **no** |  | distinct conversations that day |
| 10 | `Failures` | int | **no** |  |  |
| 11 | `LoadedUtc` | datetime2(0) | **no** | `sysutcdatetime` |  |

Indexes and constraints:

- `PK_AIAppUserDay`: primary key, clustered (Day, Resource, ApplicationName, SubscriptionId, Caller)

### audit.AIAppResource

*Table.* Each AI resource seen (2.6.9): its line of business, environment and owner, guessed from its name the first time it is seen (SetBy Name) and kept once an administrator sets them on the page (SetBy Admin).

**Script** `Create-CopilotActivityRollup.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `Resource` | nvarchar(200) | **no** | **PK** |  |
| 2 | `LineOfBusiness` | nvarchar(100) | yes |  |  |
| 3 | `Environment` | varchar(16) | **no** | `'Unknown'` | Production \| Development \| Test \| Unknown |
| 4 | `Owner` | nvarchar(256) | yes |  |  |
| 5 | `Notes` | nvarchar(400) | yes |  |  |
| 6 | `SetBy` | varchar(8) | **no** | `'Name'` | Name (guessed) \| Admin |
| 7 | `FirstSeen` | date | yes |  |  |
| 8 | `UpdatedBy` | nvarchar(256) | yes |  |  |
| 9 | `UpdatedUtc` | datetime2(0) | **no** | `sysutcdatetime` |  |

Indexes and constraints:

- `PK_AIAppResource`: primary key, clustered (Resource)

### audit.AIAppCall

*Table.* One row per ConnectedAIAppInteraction record (2.6.9), shredded by `audit.usp_ShredActivity`. A record is one message, a prompt or a response; no prompt or response text exists in it, and client IP addresses are not copied.

**Script** `Create-ActivitySchema.sql` · **In** activity database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `RecordId` | varchar(64) | **no** |  |  |
| 2 | `CreatedDateTime` | datetime2(0) | **no** |  | UTC |
| 3 | `UserId` | nvarchar(256) | yes |  | a sign-in, or Guest |
| 4 | `UserType` | int | yes |  | 0 a person; 10 Guest |
| 5 | `UserKey` | nvarchar(128) | yes |  |  |
| 6 | `CallerKind` | varchar(16) | **no** |  | Person \| Unattributed |
| 7 | `Caller` | nvarchar(256) | **no** |  |  |
| 8 | `AppIdentity` | nvarchar(400) | yes |  | ConnectedAIApp.AzureAI.&lt;resource&gt; or ConnectedAIApp.Entra.&lt;app id&gt; |
| 9 | `AppHost` | nvarchar(200) | yes |  |  |
| 10 | `ApplicationName` | nvarchar(200) | yes |  |  |
| 11 | `Resource` | nvarchar(200) | **no** |  |  |
| 12 | `ContextId` | nvarchar(400) | yes |  | the endpoint |
| 13 | `SubscriptionId` | varchar(64) | yes |  |  |
| 14 | `ThreadId` | nvarchar(400) | yes |  |  |
| 15 | `Prompts` | int | **no** |  |  |
| 16 | `Responses` | int | **no** |  |  |
| 17 | `Failed` | bit | **no** |  | a resource reached reported Failure |
| 18 | `ShreddedUtc` | datetime2(0) | **no** | `sysutcdatetime` |  |

Indexes and constraints:

- `CCI_AIAppCall`: clustered columnstore
- `UX_AIAppCall_Record`: unique (RecordId)

### audit.PlatformActivity

*Table.* One row per PowerBIAudit or PowerAppsApp record (2.6.9), shredded by `audit.usp_ShredActivity`: who, what, and on which item.

**Script** `Create-ActivitySchema.sql` · **In** activity database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `RecordId` | varchar(64) | **no** |  |  |
| 2 | `CreatedDateTime` | datetime2(0) | **no** |  | UTC |
| 3 | `Product` | varchar(32) | **no** |  | Power BI \| Power Apps |
| 4 | `Operation` | nvarchar(128) | yes |  | ViewReport, ExportReport, LaunchPowerApp ... |
| 5 | `UserId` | nvarchar(256) | yes |  |  |
| 6 | `UserType` | int | yes |  |  |
| 7 | `ItemName` | nvarchar(400) | yes |  | the report, dashboard, dataset or app |
| 8 | `Container` | nvarchar(400) | yes |  | the workspace, or the environment |
| 9 | `ShreddedUtc` | datetime2(0) | **no** | `sysutcdatetime` |  |

Indexes and constraints:

- `CCI_PlatformActivity`: clustered columnstore
- `UX_PlatformActivity_Record`: unique (RecordId)

### audit.ActivityUserDay

*Table.* Who used Power BI, Power Apps and Teams meeting AI notes each day (2.7.0), from the activity database's records, by `audit.usp_RollupActivity`: for Apps' trend and any part of the organization. Days are the reporting time zone's.

**Script** `Create-CopilotActivityRollup.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `Day` | date | **no** | **PK** | a day in the reporting time zone |
| 2 | `Product` | varchar(32) | **no** | **PK** | Power BI \| Power Apps \| Teams AI notes |
| 3 | `Upn` | nvarchar(256) | **no** | **PK** |  |
| 4 | `Events` | int | **no** |  |  |
| 5 | `LoadedUtc` | datetime2(0) | **no** | `sysutcdatetime` |  |

Indexes and constraints:

- `PK_ActivityUserDay`: primary key, clustered (Day, Product, Upn)

### audit.DlpDay

*Table.* Data loss prevention matches each day (2.7.0), by workload, policy, rule, severity and person, where the activity database loads the DLP record types; for Compliance > Sensitive content. No file or message is named.

**Script** `Create-CopilotActivityRollup.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `Day` | date | **no** |  | a day in the reporting time zone |
| 2 | `Workload` | nvarchar(64) | **no** |  | SharePoint, OneDrive, Exchange, Endpoint; '' where none |
| 3 | `PolicyName` | nvarchar(400) | **no** |  |  |
| 4 | `RuleName` | nvarchar(400) | **no** |  |  |
| 5 | `Severity` | nvarchar(32) | **no** |  |  |
| 6 | `Upn` | nvarchar(256) | **no** |  | '' where the record names no one |
| 7 | `Matches` | int | **no** |  |  |
| 8 | `Undone` | int | **no** |  | DlpRuleUndo or DlpInfo: overridden, no longer matching, or reported as a false positive |
| 9 | `LoadedUtc` | datetime2(0) | **no** | `sysutcdatetime` |  |

Indexes and constraints:

- `IX_DlpDay_Day`: nonclustered (Day)

### audit.DlpMatch

*Table.* One row per data loss prevention record (2.7.0), shredded by `audit.usp_ShredActivity`: the first policy and rule it names, its severity, the workload and the person.

**Script** `Create-ActivitySchema.sql` · **In** activity database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `RecordId` | varchar(64) | **no** |  |  |
| 2 | `CreatedDateTime` | datetime2(0) | **no** |  | UTC |
| 3 | `Workload` | nvarchar(64) | yes |  |  |
| 4 | `Operation` | nvarchar(128) | yes |  | DlpRuleMatch, DlpRuleUndo, DlpInfo, or an endpoint activity that matched a policy |
| 5 | `UserId` | nvarchar(256) | yes |  |  |
| 6 | `PolicyName` | nvarchar(400) | yes |  |  |
| 7 | `RuleName` | nvarchar(400) | yes |  |  |
| 8 | `Severity` | nvarchar(32) | yes |  |  |
| 9 | `Policies` | int | **no** |  | the policies the record names |
| 10 | `ShreddedUtc` | datetime2(0) | **no** | `sysutcdatetime` |  |

Indexes and constraints:

- `CCI_DlpMatch`: clustered columnstore
- `UX_DlpMatch_Record`: unique (RecordId)

## The weekly report (3.1.2)

What tools/Send-CopilotWeeklyReport.ps1 keeps. Its figures come from `bi.usp_WeeklyCopilotReport`, which reads the bi facts and keeps nothing; [WEEKLY-REPORT.md](WEEKLY-REPORT.md) defines each figure.

### audit.WeeklyReportSend

*Table.* One row per week the weekly Copilot report was sent or tried, by the Friday that ends it: whether it went, how many tries, to whom, and whether the data had reached the Monday after the week. The script sends a week once and tries a failed send again up to MaxAttempts; a test send with -To is not recorded.

**Script** `Create-WeeklyReport.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `WeekEnding` | date | **no** | **PK** | the Friday the week ends on |
| 2 | `Status` | varchar(16) | **no** |  | Sent \| Failed |
| 3 | `Attempts` | int | **no** | `(0)` |  |
| 4 | `FirstTryUtc` | datetime2(0) | **no** | `sysutcdatetime` |  |
| 5 | `LastTryUtc` | datetime2(0) | **no** | `sysutcdatetime` |  |
| 6 | `SentUtc` | datetime2(0) | yes |  |  |
| 7 | `Recipients` | nvarchar(4000) | yes |  | To, Cc and Bcc, separated by semicolons |
| 8 | `DataComplete` | bit | yes |  | the data reached the Monday after the week when it was sent |
| 9 | `Detail` | nvarchar(4000) | yes |  | the mail server's error, or a note |

Indexes and constraints:

- `PK_WeeklyReportSend`: primary key, clustered (WeekEnding)

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
- `IX_LicenseEvent_Shredded`: nonclustered (ShreddedUtc)

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

### audit.LicenseSkuChange

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

### audit.LicenseSkuChangeRun

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

### audit.vLicenseSkuChangeFromRecords

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

### audit.vLicenseSkuChange

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

### audit.AgentEvent

*Table.* One row per Copilot Studio authoring record (2.3.0): the operation (BotCreate, BotUpdateOperation-BotPublish, BotUpdateOperation-BotShare, BotDelete, BotComponent...), the action it means (Created, Published, Shared, Updated, Component, Deleted), the maker, the agent and the component, and the update details. Shredded by usp_ShredAgentEvents after each license-event load, which asks for these operations too.

**Script** `Create-AgentEventProcedure.sql` · **In** License-event database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `RecordId` | varchar(64) | **no** | **PK** |  |
| 2 | `CreatedUtc` | datetime2(0) | **no** |  |  |
| 3 | `Operation` | nvarchar(100) | **no** |  |  |
| 4 | `Action` | varchar(16) | **no** |  | Created \| Published \| Shared \| Updated \| Deleted \| Component; 2.7.0, an administrator's: Blocked \| Unblocked \| Deployed \| Removed \| AdminDeleted \| AdminUpdated \| Settings |
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

*Table.* One row per identity the audit log gives an agent: the one the record's AppIdentity names (a Copilot Studio or Agent Builder agent, one of Microsoft's), else its AgentId (2.4.11; bi.fnAgentIdentity is the rule). AgentName is a label on the key: the friendly name the audit log gave the agent last, else one read from its id, so a renamed agent keeps its key and its history. (no agent) is Copilot itself. From 2.6.3 an agent the log names two ways is one agent: the row of its second identity carries MergedIntoKey, its interactions are kept under the agent's key, and bi.Agents and dash.Agent list the agent once. The rows with no MergedIntoKey are the agents.

**Script** `Create-CopilotBiSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `AgentKey` | int | **no** | **PK** identity |  |
| 2 | `AgentId` | nvarchar(400) | **no** |  | the AppIdentity that names the agent, else AgentId |
| 3 | `AgentName` | nvarchar(400) | yes |  | the latest friendly name the audit log gave it, else one read from the id |
| 5 | `FirstSeenUtc` | datetime2(0) | **no** | `sysutcdatetime` |  |
| 6 | `NamedInAudit` | bit | **no** | `0` | 1 when AgentName is the audit log's own name for the agent, 0 when it was read from the id (2.4.11) |
| 7 | `AgentSource` | nvarchar(30) | yes |  | Copilot Studio \| Microsoft \| Agent ID \| (no agent) |
| 8 | `MergedIntoKey` | int | yes |  | 2.6.3: the AgentKey of the agent this row is a second identity of; NULL on an agent's own row. Set by tools\Import-AgentWorkbook.ps1 (the workbook's row holds both ids of an agent) and by bi.usp_MergeAgents (an id written with and without its prefix; the variants of one of Microsoft's agents) |

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

### bi.DimFileType

*Table.* What a resource Copilot read was (2.4.6): a file's extension (docx, xlsx, pptx, csv ...) from AccessedResources[].Name, and for anything else its Type (EmailMessage, TeamsMessage, StreamVideo ...).

**Script** `Create-CopilotBiSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `FileTypeKey` | int | **no** | **PK** identity | 1 is (not reported) |
| 2 | `FileType` | nvarchar(64) | **no** |  |  |

Indexes and constraints:

- `PK_DimFileType`: primary key, clustered (FileTypeKey)
- `UQ_DimFileType`: unique, nonclustered (FileType)

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
| 2 | `ResourceAction` | nvarchar(450) | **no** |  | as wide as audit.RawLogAccessedResource.[Action] |

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
| 27 | `UseKind` | tinyint | yes |  | what the record says about licensed use (2.4.8): 0 web Copilot Chat or a Copilot Studio agent, 1 inside an app, Copilot Chat in work mode, a Microsoft agent, or work content read, 2 no identity and no work content, 3 no identity, work content read; NULL before 2.4.8 |

Indexes and constraints:

- `CCI_FactInteraction`: clustered columnstore include (RecordId, CreatedUtc, ActivityHour, DateKey, HourOfDay, UserKey, AppKey, AgentKey, ModelKey, LicenseStatusKey, PromptCount, AccessedResourceCount, UsedWebSearch, JailbreakDetected, PromptInjectionDetected, HasBlockedResource, HasSensitivityLabel, PremiumUse, UseCaseKey, LicenseTypeKey, SensitivityLabelKey, MessageCount, MemoryUpdated, DlpEvaluationDeferred, CalledAnotherAgent, TargetAgentName, UseKind)

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
| 14 | `FileTypeKey` | int | **no** | `1` | the file type (2.4.6); 1 for rows loaded before it, until bi is refreshed in full |

Indexes and constraints:

- `CCI_FactAccessedResource`: clustered columnstore include (RecordId, Ordinal, CreatedUtc, ActivityHour, DateKey, UserKey, AppKey, ResourceTypeKey, ResourceActionKey, SiteKey, SensitivityLabelKey, IsBlocked, XPIADetected, FileTypeKey)

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

### bi.RebuildInProgress

*Table.* A row while a full rebuild of bi.FactInteraction runs (2.4.9): bi.usp_RefreshBi puts it there before it empties the fact and takes it out when it has finished. While a row is here every refresh is a full one, so a rebuild stopped part way is finished by the next run.

**Script** `Create-CopilotBiSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `StartedUtc` | datetime2(0) | **no** |  |  |

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

*View.* bi.DimAgent for the model: the agents, one row each. From 2.6.3 the second identity of an agent the audit log names two ways is not listed; its interactions are the agent's.

**Script** `Create-CopilotBiSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `AgentKey` | int | **no** | identity |  |
| 2 | `AgentId` | nvarchar(400) | **no** |  |  |
| 3 | `AgentName` | nvarchar(400) | yes |  |  |
| 4 | `AgentSource` | nvarchar(30) | yes |  |  |
| 5 | `NamedInAudit` | bit | **no** |  |  |

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
| 11 | `FileTypeKey` | int | **no** |  |  |

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

### bi.FileTypes

*View.* bi.DimFileType for the model (2.4.6).

**Script** `Create-CopilotBiSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `FileTypeKey` | int | **no** | identity |  |
| 2 | `FileType` | nvarchar(64) | **no** |  |  |

### bi.ResourceActions

*View.* bi.DimResourceAction for the model.

**Script** `Create-CopilotBiSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `ResourceActionKey` | int | **no** | identity |  |
| 2 | `ResourceAction` | nvarchar(450) | **no** |  |  |

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

*View.* Who may see whom (2.3.0): each viewer's sign-in with the user key of everyone at or under them, and everyone for the top of the organization, the people reporting to them and people granted Organization. The Power BI model's Organization role filters Users by it, and shows each viewer only their own rows. Empty in privacy mode.

**Script** `Create-CopilotOperationsSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `ViewerUpn` | nvarchar(256) | **no** |  |  |
| 2 | `UserKey` | int | **no** |  |  |
| 3 | `WholeOrganization` | bit | yes |  | 2.4.12: true when the viewer sees the whole organization. The model's Organization role shows LicensePool, ValueNames and DepartmentWeek to them alone. |

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

### dash.CostPrice

*Table.* What a license or a unit of capacity costs (2.6.6), for Leadership &gt; Costs and savings: a license matched to audit.LicenseSku by its SKU part number, and the units the page prices (a GB over the Dataverse entitlement, a Copilot Credit, a capacity pack, a GitHub Copilot seat). Seeded with list prices in USD; an administrator sets them in Admin &gt; Targets and assumptions, and a changed row is kept on every install.

**Script** `Create-CopilotDashboardSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `Item` | varchar(64) | **no** | **PK** |  |
| 2 | `Area` | nvarchar(40) | **no** |  | Microsoft 365 Copilot \| GitHub Copilot \| Power Automate \| Power Apps \| Power BI \| Copilot Studio \| Dataverse |
| 3 | `Name` | nvarchar(200) | **no** |  |  |
| 4 | `Unit` | nvarchar(40) | **no** |  | user, flow, GB, credit, pack, seat |
| 5 | `MonthlyPrice` | decimal(19,4) | **no** |  | a month, for a license; for a credit, each |
| 6 | `Currency` | nvarchar(8) | yes |  | NULL: LicenseCurrency |
| 7 | `Note` | nvarchar(400) | yes |  |  |
| 8 | `UpdatedBy` | nvarchar(256) | yes |  |  |
| 9 | `UpdatedUtc` | datetime2(0) | **no** | `sysutcdatetime` |  |
| 10 | `TierFromSeats` | int | yes |  | 2.6.9: from this many seats every seat costs TierPrice |
| 11 | `TierPrice` | decimal(19,4) | yes |  | 2.6.9: the volume price (Power Apps Premium: 12 from 2,000) |
| 12 | `DatabaseGbPerSeat` | decimal(9,3) | yes |  | 2.6.9: Dataverse database capacity a seat brings |
| 13 | `FileGbPerSeat` | decimal(9,3) | yes |  | 2.6.9: Dataverse file capacity a seat brings |
| 14 | `SparePercent` | decimal(5,2) | yes |  | 2.6.9: share of seats bought kept unassigned on purpose; NULL: SpareSeatPercent |
| 15 | `ForCapacity` | bit | yes |  | 2.6.9: bought for its capacity or volume price: unassigned and idle seats are no saving |

Indexes and constraints:

- `PK_DashCostPrice`: primary key, clustered (Item)

### dash.CostSuite

*Table.* The suites (2.6.6): Microsoft 365 and Office 365 E3, E5 and E7 by SKU part number pattern, with family, tier, price a month, and the priced items each includes (COPILOT, POWER_BI_PRO). Costs and savings counts none of a suite's price as AI spend and nothing it includes again; Licensing &gt; Suite fit compares each holder's suite with the next one down. Set in Admin &gt; Targets and assumptions.

**Script** `Create-CopilotDashboardSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `SkuPattern` | nvarchar(100) | **no** | **PK** | a LIKE pattern on the SKU part number |
| 2 | `Suite` | nvarchar(100) | **no** |  |  |
| 3 | `Family` | nvarchar(40) | **no** |  | Microsoft 365 \| Office 365: a move down stays in the family |
| 4 | `Tier` | tinyint | **no** |  | 3, 5, 7 |
| 5 | `MonthlyPrice` | decimal(19,4) | **no** |  | a user a month; 0: not known, so never a move's target |
| 6 | `Currency` | nvarchar(8) | yes |  | NULL: LicenseCurrency |
| 7 | `Includes` | nvarchar(400) | **no** | `N''` |  |
| 8 | `Covers` | nvarchar(400) | yes |  |  |
| 9 | `Enabled` | bit | **no** | `1` |  |
| 10 | `UpdatedBy` | nvarchar(256) | yes |  |  |
| 11 | `UpdatedUtc` | datetime2(0) | **no** | `sysutcdatetime` |  |

Indexes and constraints:

- `PK_DashCostSuite`: primary key, clustered (SkuPattern)

### dash.SkuName

*Table.* The product name for each license part number (2.7.1): every page shows a license as *Microsoft 365 E5*, not `SPE_E5`. Seeded with Microsoft's names for 35 common licenses; Admin &gt; Value names &gt; License product names names the rest or renames one, and a name set there is kept on every install. A part number with no row is shown by its price list name, or as it is.

**Script** `Create-CopilotDashboardSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `SkuPartNumber` | nvarchar(100) | **no** | **PK** |  |
| 2 | `ProductName` | nvarchar(200) | **no** |  |  |
| 3 | `UpdatedBy` | nvarchar(256) | yes |  | install: Microsoft's name, as the install seeded it |
| 4 | `UpdatedUtc` | datetime2(0) | **no** | `sysutcdatetime` |  |

Indexes and constraints:

- `PK_DashSkuName`: primary key, clustered (SkuPartNumber)

### dash.CopilotSuiteOnly

*View.* A view (2.6.6): the people who hold Microsoft 365 Copilot only through a suite (E7), and not the add-on as well. Never on a list to revoke: their Copilot cannot be taken off alone. Empty until the license pool and its suite mark exist.

**Script** `Create-CopilotDashboardSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `Upn` | nvarchar(512) | **no** |  |  |

### dash.CopilotSuiteHolder

*View.* A view (2.6.6): everyone who holds a suite with Microsoft 365 Copilot inside (E7), with the add-on beside it or not. Costs and savings leaves them out of the move to pay-as-you-go, as the add-on beside E7 is a copy, counted as such. Empty until the license pool and its suite mark exist.

**Script** `Create-CopilotDashboardSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `Upn` | nvarchar(512) | **no** |  |  |
### dash.RoleGrant

*Table.* Roles granted to people (UPN or DOMAIN\\user), AD groups (DOMAIN\\Group) or Entra groups (entra:&lt;object ID&gt;, 2.8.0). From 3.1.0 every menu section needs a grant that opens it: a Sections grant names them (the Sections column). Admin runs the admin module, or only the Admin pages its grant names (2.6.4); Organization sees the whole organization; the persona roles unlock named detail, and with OrgWide show their pages for the whole organization (2.8.0); Governance, GovernanceOperator and GovernanceAuditor open Governance Workbench pages and no usage data (3.0.0).

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
| 8 | `Pages` | nvarchar(1000) | yes |  | 2.6.4: the Admin pages an Admin grant is limited to, as their paths, comma-separated (/admin/health,/admin/audit); NULL is the whole role, and what every other role has |
| 9 | `PrincipalObjectId` | uniqueidentifier | yes |  | 2.8.0: an Entra group's object ID; NULL for a UPN or DOMAIN\\name |
| 10 | `PrincipalName` | nvarchar(256) | yes |  | 2.8.0: the Entra group's name when it was granted, for the lists |
| 11 | `OrgWide` | bit | **no** | `0` | 2.8.0: a persona role across the whole organization: its pages show the whole organization to the holder; 0 on every grant before. 3.1.0: on a Sections grant, its sections for the whole organization |
| 12 | `Sections` | nvarchar(1000) | yes |  | 3.1.0: on a Sections grant, the menu sections it opens, comma-separated keys in the menu's order (leadership,licensing,wb-ownership); NULL on every other grant |
| 13 | `NamedPeople` | bit | **no** | `0` | 3.1.0: on a Sections grant, whether the lists that name people show in its sections; 0 on every other grant |

Indexes and constraints:

- `PK_DashRoleGrant`: primary key, clustered (GrantId)
- `UQ_DashRoleGrant`: unique, nonclustered (Principal, Role)
- `CK_DashRoleGrant_Type`: check `([PrincipalType]='Group' OR [PrincipalType]='User')`
- `CK_DashRoleGrant_Role`: check `([Role]='Enablement' OR [Role]='Agents' OR [Role]='Compliance' OR [Role]='Security' OR [Role]='Licensing' OR [Role]='Adoption' OR [Role]='Organization' OR [Role]='Admin')`; from 3.0.0 also `Governance`, `GovernanceOperator` and `GovernanceAuditor`, the Governance Workbench's page roles; from 3.0.3 `Restricted` (withdrawn before use: accepted, never read), and from 3.1.0 `Sections`
- `DF_DashRoleGrant_OrgWide`: default `(0)` on OrgWide
- `DF_DashRoleGrant_NamedPeople`: default `(0)` on NamedPeople (3.1.0)

### dash.AccessProfile

*Table.* Access profiles (2.8.0): named bundles of roles and Admin pages, granted to people and groups as one, each with its owner, its approver, how long access lasts and where to ask for it (a My Access link). Edited in Admin &gt; Access profiles.

**Script** `Create-CopilotDashboardSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `ProfileId` | int | **no** | **PK** identity |  |
| 2 | `Name` | nvarchar(100) | **no** |  |  |
| 3 | `Description` | nvarchar(400) | yes |  |  |
| 4 | `OwnerUpn` | nvarchar(256) | yes |  | who answers for it |
| 5 | `ApproverPrincipal` | nvarchar(256) | yes |  | who approves a request for it, as told to the person asking |
| 6 | `MaxDays` | int | yes |  | how long access lasts, as the access package that grants it says |
| 7 | `RequestUrl` | nvarchar(1000) | yes |  | where to ask for it: a My Access link to its access package |
| 8 | `UpdatedBy` | nvarchar(256) | **no** |  |  |
| 9 | `UpdatedUtc` | datetime2(0) | **no** | `sysutcdatetime` |  |

Indexes and constraints:

- `PK_DashAccessProfile`: primary key, clustered (ProfileId)
- `UQ_DashAccessProfile_Name`: unique, nonclustered (Name)

### dash.AccessProfileRole

*Table.* What each access profile gives (2.8.0): a role, across the whole organization or not, and for Admin its pages; from 3.1.0, a Sections row with its sections and named people.

**Script** `Create-CopilotDashboardSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `ProfileId` | int | **no** | **PK**, FK dash.AccessProfile |  |
| 2 | `Role` | varchar(32) | **no** | **PK** |  |
| 3 | `OrgWide` | bit | **no** | `0` |  |
| 4 | `Pages` | nvarchar(1000) | yes |  | for Admin: the Admin pages, as on dash.RoleGrant; NULL is every page |
| 5 | `Sections` | nvarchar(1000) | yes |  | 3.1.0: on a Sections row, the menu sections the profile opens, as on dash.RoleGrant |
| 6 | `NamedPeople` | bit | **no** | `0` | 3.1.0: on a Sections row, whether the lists that name people show in them |

Indexes and constraints:

- `PK_DashAccessProfileRole`: primary key, clustered (ProfileId, Role)
- `DF_DashAccessProfileRole_NamedPeople`: default `(0)` on NamedPeople (3.1.0)
- `FK_DashAccessProfileRole_Profile`: foreign key (ProfileId) to dash.AccessProfile, on delete cascade
- `CK_DashAccessProfileRole_Role`: check, the roles of dash.RoleGrant (the eight, from 3.0.0 Governance, GovernanceOperator and GovernanceAuditor, from 3.0.3 Restricted, and from 3.1.0 Sections)

### dash.ProfileGrant

*Table.* Access profiles granted to people, AD groups (DOMAIN\\Group) and Entra groups (entra:&lt;object ID&gt;) (2.8.0). Sign-in expands each to its profile's roles.

**Script** `Create-CopilotDashboardSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `GrantId` | int | **no** | **PK** identity |  |
| 2 | `Principal` | nvarchar(256) | **no** |  | a UPN, DOMAIN\\name, or entra:&lt;object ID&gt;, as on dash.RoleGrant |
| 3 | `PrincipalType` | varchar(8) | **no** |  |  |
| 4 | `PrincipalObjectId` | uniqueidentifier | yes |  |  |
| 5 | `PrincipalName` | nvarchar(256) | yes |  |  |
| 6 | `ProfileId` | int | **no** |  | FK dash.AccessProfile |
| 7 | `Note` | nvarchar(400) | yes |  |  |
| 8 | `GrantedBy` | nvarchar(256) | **no** |  |  |
| 9 | `GrantedUtc` | datetime2(0) | **no** | `sysutcdatetime` |  |

Indexes and constraints:

- `PK_DashProfileGrant`: primary key, clustered (GrantId)
- `UQ_DashProfileGrant`: unique, nonclustered (Principal, ProfileId)
- `FK_DashProfileGrant_Profile`: foreign key (ProfileId) to dash.AccessProfile
- `CK_DashProfileGrant_Type`: check `([PrincipalType]='Group' OR [PrincipalType]='User')`

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
| 9 | `Change` | nvarchar(max) | yes |  | an admin change's record, as JSON: what was submitted (secrets and addresses' queries masked), and the settings and role grants it changed, before and after (2.7.6) |

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
| 10 | `ObjectId` | uniqueidentifier | yes |  | the person's Entra ID object ID, from audit.CopilotLicense (2.8.0): how a sign-in through Entra ID is matched first |

Indexes and constraints:

- `PK_DashPerson`: primary key, clustered (Upn)
- `IX_DashPerson_Manager`: nonclustered (ManagerUpn)
- `IX_DashPerson_Sid`: nonclustered (Sid)
- `IX_DashPerson_ObjectId`: nonclustered (ObjectId)

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
- `NCCI_DashUserDay`: nonclustered columnstore include (DateKey, UserKey, Interactions, Prompts, AppsUsed, AgentInteractions, WebGrounded, ChatInteractions, LicensedUse, Jailbreak, PromptInjection, BlockedResource, LabeledContent, Sessions, MultiTurnSessions, PremiumUse)

### dash.FlaggedInteraction

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
- `NCCI_DashResourceDay`: nonclustered columnstore include (DateKey, UserKey, Kind, Item, Interactions)

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

### dash.AgentInventory

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

### dash.AgentChildLink

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

### dash.AgentInventoryRun

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

### dash.Agent

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

*Table.* Every email and webhook post, sent or not, with the reason when not, and from 2.6.2 the conversation with the mail server (Detail), which Admin &gt; Email shows for troubleshooting.

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
| 8 | `Detail` | nvarchar(max) | yes |  | 2.6.2: what was said between the dashboard and the mail server, for a test message and for any message not sent; never the password or the message |

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

### dash.NonHumanException

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

### dash.Reservation

*Table.* The Copilot reservation register (2.6.8, Leadership &gt; Copilot reservations): the Microsoft 365 Copilot seats each persistent chargeback organization reserved, effective from one date to another. The people a row covers are everyone under its leader in the org chart (`dash.fnReservationMember`).

**Script** `Create-CopilotOperationsSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `ReservationId` | int | **no** | **PK** identity |  |
| 2 | `OrgId` | nvarchar(64) | **no** |  | the persistent chargeback organization ID |
| 3 | `Name` | nvarchar(200) | **no** |  |  |
| 4 | `LeaderUpn` | nvarchar(256) | yes |  | whose people it covers; NULL: a pool with no one under it |
| 5 | `CostCenter` | nvarchar(100) | yes |  |  |
| 6 | `Reserved` | int | **no** |  |  |
| 7 | `EffectiveFrom` | date | **no** |  |  |
| 8 | `EffectiveTo` | date | yes |  | NULL: open |
| 9 | `Note` | nvarchar(400) | yes |  |  |
| 10 | `UpdatedBy` | nvarchar(256) | **no** |  |  |
| 11 | `UpdatedUtc` | datetime2(0) | **no** | `sysutcdatetime` |  |

Indexes and constraints:

- `PK_DashReservation`: primary key, clustered (ReservationId)
- `IX_DashReservation_Org`: nonclustered (OrgId, EffectiveFrom)
- `CK_DashReservation_Reserved`: check `([Reserved]>=(0))`

### dash.ReservationRate

*Table.* The rate a reserved seat is charged a month, from a date (2.6.8): Finance's, after any discount. No row in force: the page shows Pending Finance Decision.

**Script** `Create-CopilotOperationsSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `EffectiveFrom` | date | **no** | **PK** |  |
| 2 | `MonthlyRate` | decimal(12,2) | **no** |  |  |
| 3 | `Currency` | nvarchar(8) | yes |  | NULL: LicenseCurrency |
| 4 | `Note` | nvarchar(400) | yes |  |  |
| 5 | `UpdatedBy` | nvarchar(256) | **no** |  |  |
| 6 | `UpdatedUtc` | datetime2(0) | **no** | `sysutcdatetime` |  |

Indexes and constraints:

- `PK_DashReservationRate`: primary key, clustered (EffectiveFrom)
- `CK_DashReservationRate_Rate`: check `([MonthlyRate]>=(0))`

### dash.TechnologyAllocation

*Table.* Technology-funded allocations (2.6.8): approved executive, service, test, shared, migration and exception accounts whose license the Technology pool funds, never a line of business.

**Script** `Create-CopilotOperationsSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `Upn` | nvarchar(256) | **no** | **PK** |  |
| 2 | `Category` | nvarchar(40) | **no** |  | Executive, Service, Test, Shared, Migration or Exception |
| 3 | `Owner` | nvarchar(256) | **no** |  | the accountable owner |
| 4 | `ApprovalRef` | nvarchar(200) | yes |  |  |
| 5 | `EffectiveFrom` | date | **no** |  |  |
| 6 | `ReviewDate` | date | yes |  |  |
| 7 | `Note` | nvarchar(400) | yes |  |  |
| 8 | `UpdatedBy` | nvarchar(256) | **no** |  |  |
| 9 | `UpdatedUtc` | datetime2(0) | **no** | `sysutcdatetime` |  |

Indexes and constraints:

- `PK_DashTechnologyAllocation`: primary key, clustered (Upn)

### dash.ReservationSnapshot

*Table.* Each month-end's reservation position, kept as it was (2.6.8): one row per organization, plus `~unmapped` (add-on holders under no listed leader) and `~technology` (the Technology pool), written once by `dash.usp_TakeReservationSnapshot` and never rewritten. The inputs are kept; the dashboard works out unused, over, billable and the obligations from them.

**Script** `Create-CopilotOperationsSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `SnapshotDate` | date | **no** | **PK** | the month-end day |
| 2 | `OrgId` | nvarchar(64) | **no** | **PK** |  |
| 3 | `Name` | nvarchar(200) | **no** |  |  |
| 4 | `LeaderUpn` | nvarchar(256) | yes |  |  |
| 5 | `CostCenter` | nvarchar(100) | yes |  |  |
| 6 | `Reserved` | int | **no** |  |  |
| 7 | `Allocated` | int | **no** |  | add-on licenses held that day |
| 8 | `MonthlyRate` | decimal(12,2) | yes |  | NULL: no rate was in force |
| 9 | `State` | varchar(16) | **no** | `'Operational'` |  |
| 10 | `TakenUtc` | datetime2(0) | **no** | `sysutcdatetime` |  |

Indexes and constraints:

- `PK_DashReservationSnapshot`: primary key, clustered (SnapshotDate, OrgId)

### dash.CreditRoute

*Table.* Each Power Platform environment's Copilot Credit route (2.7.2): LoB Productivity or LoB Business Application (charged to the line of business named), Enterprise Shared (funded centrally, never charged to one) or Innovation (its sponsor's, where it names one), with its cost center, financial owner and billing policy, from a date. A change is a new row from its date; the open row is ended the day before. Kept on Leadership › Credit chargeback.

**Script** `Create-CopilotOperationsSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `RouteId` | int | **no** | **PK**, identity |  |
| 2 | `EnvironmentName` | nvarchar(200) | **no** |  | as the consumption report names it |
| 3 | `Path` | varchar(24) | **no** |  | Productivity \| BusinessApplication \| EnterpriseShared \| Innovation |
| 4 | `OrgId` | nvarchar(64) | yes |  | the line of business charged (dash.Reservation); NULL for Enterprise Shared |
| 5 | `CostCenter` | nvarchar(100) | yes |  |  |
| 6 | `FinancialOwner` | nvarchar(256) | yes |  |  |
| 7 | `BillingPolicy` | nvarchar(200) | yes |  | the Power Platform billing policy, as a label |
| 8 | `EffectiveFrom` | date | **no** |  |  |
| 9 | `EffectiveTo` | date | yes |  |  |
| 10 | `Note` | nvarchar(400) | yes |  |  |
| 11 | `UpdatedBy` | nvarchar(256) | yes |  |  |
| 12 | `UpdatedUtc` | datetime2(0) | **no** | `sysutcdatetime` |  |

Indexes and constraints:

- `PK_DashCreditRoute`: primary key, clustered (RouteId)
- `IX_DashCreditRoute_Env`: index (EnvironmentName, EffectiveFrom)
- `CK_DashCreditRoute_Path`, `CK_DashCreditRoute_Dates`, `CK_DashCreditRoute_Org`: a known path; the end on or after the start; the two LoB paths name an Org ID

### dash.CreditChargeSnapshot

*Table.* Each month's Copilot Credit chargeback, kept as it was (2.7.2): one row per environment and the route it ran under, `Unmapped` where none was in effect, written once by `dash.usp_TakeCreditChargeSnapshot` once the consumption report reaches the month's last day, and never rewritten.

**Script** `Create-CopilotOperationsSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `SnapshotId` | int | **no** | **PK**, identity |  |
| 2 | `SnapshotMonth` | date | **no** |  | the month's first day |
| 3 | `EnvironmentName` | nvarchar(200) | **no** |  |  |
| 4 | `Path` | varchar(24) | **no** |  | as dash.CreditRoute, or Unmapped |
| 5 | `OrgId` | nvarchar(64) | yes |  |  |
| 6 | `OrgName` | nvarchar(200) | yes |  |  |
| 7 | `CostCenter` | nvarchar(100) | yes |  |  |
| 8 | `Credits` | decimal(19,2) | **no** |  | Copilot Studio's credits; AI Builder's are not Copilot Credits |
| 9 | `PaygCredits` | decimal(19,2) | **no** |  |  |
| 10 | `PaygCost` | decimal(19,4) | yes |  | what pay-as-you-go billed |
| 11 | `Currency` | nvarchar(8) | yes |  |  |
| 12 | `ChargeRate` | decimal(12,6) | yes |  | what one credit was charged back at; NULL: none in force |
| 13 | `TakenUtc` | datetime2(0) | **no** | `sysutcdatetime` |  |

Indexes and constraints:

- `PK_DashCreditChargeSnapshot`: primary key, clustered (SnapshotId)
- `IX_DashCreditChargeSnapshot_Month`: index (SnapshotMonth)

### dash.CreditCommitment

*Table.* A prepaid Copilot Credit commitment (2.7.2), such as the Pre-Purchase Plan (P3): its amount, what one credit draws from it, its term and Azure scope. Forecasting › Credit commitment sets the metered credits against it.

**Script** `Create-CopilotOperationsSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `CommitmentId` | int | **no** | **PK**, identity |  |
| 2 | `Name` | nvarchar(200) | **no** |  |  |
| 3 | `Amount` | decimal(19,2) | **no** |  | in its currency |
| 4 | `Currency` | nvarchar(8) | yes |  | NULL: LicenseCurrency |
| 5 | `DrawRate` | decimal(12,6) | **no** | `0.01` | what one credit draws from it |
| 6 | `StartDate` | date | **no** |  |  |
| 7 | `EndDate` | date | **no** |  |  |
| 8 | `Scope` | nvarchar(200) | yes |  | the Azure scope, as a label |
| 9 | `Note` | nvarchar(400) | yes |  |  |
| 10 | `UpdatedBy` | nvarchar(256) | yes |  |  |
| 11 | `UpdatedUtc` | datetime2(0) | **no** | `sysutcdatetime` |  |

Indexes and constraints:

- `PK_DashCreditCommitment`: primary key, clustered (CommitmentId)
- `CK_DashCreditCommitment_Term`, `CK_DashCreditCommitment_Amount`: the term ends after it starts; the amount and the draw are above 0

### dash.QueryCacheShared

*Table.* The report cache the web servers share (2.7.6): a report query's result, kept by one server as compressed JSON and read by the others. Kept under the data's stamp and the site's build; cleared of other stamps' results by the morning build and of expired ones every half hour. Never read by a report.

**Script** `Create-CopilotOperationsSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `KeyHash` | binary(32) | **no** | **PK** | SHA-256 of the build, tenant, stamp, query and its arguments |
| 2 | `StampHash` | binary(32) | **no** |  | SHA-256 of the build, tenant and stamp it was read under |
| 3 | `Payload` | varbinary(max) | **no** |  | the result, gzipped JSON |
| 4 | `Bytes` | int | **no** |  | the payload's size |
| 5 | `CreatedUtc` | datetime2(0) | **no** | `sysutcdatetime` |  |
| 6 | `ExpiresUtc` | datetime2(0) | **no** |  | Insights:QueryCacheMinutes after it was kept |
| 7 | `Server` | nvarchar(128) | yes |  | the web server that kept it |

Indexes and constraints:

- `PK_DashQueryCacheShared`: primary key, clustered (KeyHash)
- `IX_DashQueryCacheShared_Stamp`: nonclustered (StampHash, CreatedUtc)

### dash.CacheGeneration

*Table.* One row: how many changes the dashboard has made, on any web server, that can alter a report (2.7.6). Part of the data's stamp, so a change on one server ends what every server kept.

**Script** `Create-CopilotOperationsSchema.sql` · **In** Copilot database

| # | Column | Type | Null | Default / key | Notes |
|--:|---|---|---|---|---|
| 1 | `Id` | tinyint | **no** | **PK** | always 1 |
| 2 | `N` | bigint | **no** |  | the count |
| 3 | `ChangedUtc` | datetime2(0) | **no** | `sysutcdatetime` | the last change |

Indexes and constraints:

- `PK_DashCacheGeneration`: primary key, clustered (Id)
- `CK_DashCacheGeneration_One`: check (Id = 1)

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
