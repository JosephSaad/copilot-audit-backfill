"""Builds the schema reference page from meta.json (live metadata) and scripts.json (DDL comments)."""
import html, json, os, re

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, 'out')
meta = json.load(open(os.path.join(OUT, 'meta.json'), encoding='utf-8-sig'))
scr = json.load(open(os.path.join(OUT, 'scripts.json'), encoding='utf-8'))
notes = scr['notes']
E = html.escape

# ---------------------------------------------------------------- scripts
SCRIPTS = [
    ('Create-CopilotAuditSchema.sql', 'Both', 'Automatically, at the start of every run of Invoke-CopilotAuditBackfill.ps1 and of the CSV importer',
     'Backfill and load state and staging: CopilotBackfillRange, CopilotBackfillWindow, CopilotAuditStage, CopilotLoad, CopilotLoadWindow, CopilotLoadStage. The shared function fnShredCopilotInteraction.'),
    ('Finalize-CopilotAudit.sql', 'Both', 'By -Finalize or -FinalizeOnly, once, after the backfill',
     'CopilotAudit and CopilotInteraction (dropped and rebuilt from staging), vCopilotDailyUsage, vCopilotAuditRecord.'),
    ('Create-CopilotLoadProcedure.sql', 'Both', 'Automatically, by every -Incremental run',
     'usp_MergeCopilotLoad, which appends each nightly load.'),
    ('Create-CopilotCsvImport.sql', 'Copilot (or license, if importing there)', 'Automatically, by tools/Import-CopilotAuditCsv.ps1',
     'CopilotCsvImport, CopilotCsvFile, CopilotCsvReject, CopilotCsvStage, usp_MergeCopilotCsv. In a database with no backfill, also CopilotAudit, CopilotInteraction and their two views. Adds CopilotAudit.CsvImportId and CopilotBackfillRange.Source.'),
    ('Create-CopilotFlattenedSchema.sql', 'Copilot', 'By hand, once (deployment step 8)',
     'RawLogFlattened, and CopilotLicense if it does not exist yet.'),
    ('Create-CopilotFlattenProcedure.sql', 'Copilot', 'By hand, once (step 8)', 'usp_FlattenCopilotAudit.'),
    ('Create-CopilotPurgeProcedure.sql', 'Copilot', 'By hand, optional (step 8)', 'usp_PurgeCopilotAuditJson.'),
    ('Create-CopilotEnrichmentSchema.sql', 'Copilot', 'Automatically, by both enrichment tools; or by hand (step 8)',
     'CopilotLicense, UserDirectory, vCopilotAdoption, vCopilotUsageByDepartment (the views only once CopilotInteraction exists).'),
    ('Create-CopilotSessionSchema.sql', 'Copilot', 'By hand, once (step 8)',
     'SessionAppCategory (seeded), CopilotSession, CopilotWeeklyRollup, CopilotSessionRun, vCopilotSessionSummary, vCopilotWeeklyRollup.'),
    ('Create-CopilotSessionProcedure.sql', 'Copilot', 'By hand, once (step 8)', 'usp_BuildCopilotSessions.'),
    ('Create-CopilotBiSchema.sql', 'Copilot', 'By hand, once (step 8)',
     'The bi schema: the dimensions (DimDate, DimUser, DimApp, DimAgent, DimModel, DimSku, DimLicenseStatus, DimLicenseType, DimSensitivityLabel, DimResourceType, DimResourceAction, DimSite), the facts (FactInteraction, FactSession, FactLicenseChange, FactLicensedDay, FactAccessedResource, FactAgentEvent), RefreshRun, and the views Power BI imports. bi.FactInteraction stores PremiumUse and UseCaseKey, set once by the refresh (2.4.0), and bi.Interactions passes them on.'),
    ('Create-CopilotBiProcedure.sql', 'Copilot', 'By hand, once (step 8)', 'bi.usp_RefreshBi.'),
    ('Create-LicenseEventSchema.sql', 'License events', 'By hand, once (step 7)',
     'LicenseEvent, LicenseEventChunk, LicenseEventProperty, LicenseSkuChange and LicenseSkuChangeRun (2.5.4), vLicenseChange, vLicenseSkuChangeFromRecords, vLicenseSkuChange, vLicenseLifecycle.'),
    ('Create-LicenseEventProcedure.sql', 'License events', 'By hand, once (step 7)',
     'usp_ShredLicenseEvents, and usp_RefreshLicenseSkuChange (2.5.4), which the shred runs at its end. Installed over events shredded before 2.5.4, it works out their SKU changes once.'),
    ('Create-CopilotExternalUsageSchema.sql', 'Copilot', 'By Install-CopilotAudit.ps1',
     'AI usage outside Microsoft 365 Copilot (2.6.2): GitHubCopilotSeat, GitHubCopilotDay, GitHubCopilotUserDay and GitHubCopilotCost, filled by tools/Import-GitHubCopilotUsage.ps1; FoundryDeployment, FoundryUsageDay and FoundryCostDay, filled by tools/Import-FoundryUsage.ps1; and ExternalUsageRun, each run of either.'),
    ('Create-CopilotDashboardSchema.sql', 'Copilot', 'By hand, once, for the dashboard (step 22)',
     'The dash schema: Setting (seeded), RoleGrant, AgentAlias, SensitivityLabel, AuditLog, Person, OrgClosure, UserDay, ResourceDay, AgentUse, RefreshRun; the view dash.Agent and the function dash.fnScopeUsers.'),
    ('Create-CopilotOperationsSchema.sql', 'Copilot', 'By Install-CopilotAudit.ps1 -Dashboard, or the dashboard installer with -GrantDatabaseAccess',
     'The dashboard\'s operations (2.3.0): encrypted secrets, digests, alerts, targets, time saved, training, value names, flag triage, digest preferences and sends, license actions and contracts, saved views, job-family and use-case rules, events and extracts; the role CopilotInsightsApp and its grants; privacy mode and retention procedures; the bi views OrgSecurity, ValueNames, LicensePool, DepartmentWeek, UseCases, JobFamilies and EventMarkers.'),
    ('Create-AgentEventProcedure.sql', 'License events', 'By Install-CopilotAudit.ps1, and after each license-event load',
     'audit.AgentEvent and audit.usp_ShredAgentEvents (2.3.0): Copilot Studio authoring records, one row each.'),
    ('Create-CopilotDashboardProcedure.sql', 'Copilot', 'By hand, once, for the dashboard (step 22)',
     'dash.usp_RefreshDashboard, which the backfill\'s -Incremental run and the CSV importer call after bi.usp_RefreshBi.'),
]

PRIMARY = {  # objects several scripts can create: the one that normally does
    'audit.CopilotAudit': 'Finalize-CopilotAudit.sql', 'audit.CopilotInteraction': 'Finalize-CopilotAudit.sql',
    'audit.vCopilotAuditRecord': 'Finalize-CopilotAudit.sql', 'audit.vCopilotDailyUsage': 'Finalize-CopilotAudit.sql',
    'audit.CopilotLicense': 'Create-CopilotEnrichmentSchema.sql',
    'audit.vLicenseSkuChange': 'Create-LicenseEventSchema.sql',
}
ALSO = {
    'audit.CopilotAudit': 'Create-CopilotCsvImport.sql, for a database that starts from CSV',
    'audit.CopilotInteraction': 'Create-CopilotCsvImport.sql, for a database that starts from CSV',
    'audit.vCopilotAuditRecord': 'Create-CopilotCsvImport.sql, for a database that starts from CSV',
    'audit.vCopilotDailyUsage': 'Create-CopilotCsvImport.sql, for a database that starts from CSV',
    'audit.CopilotLicense': 'Create-CopilotFlattenedSchema.sql, if run first',
    'audit.vLicenseSkuChange': 'Create-LicenseEventProcedure.sql, which points it at the table once the changes of events shredded before 2.5.4 are worked out',
}

# ---------------------------------------------------------------- areas and purposes
AREAS = [
    ('load', 'Loading and state', 'What the backfill and the nightly loads write while they run. The engine resumes from these after any interruption.', [
        ('audit.CopilotBackfillRange', 'One row (Id = 1): the range and operation set this database holds, and when the backfill completed. -Incremental continues from its end. A CSV import that starts a database writes it with Source = \'CSV\'.'),
        ('audit.CopilotBackfillWindow', 'One row per search window of the backfill: its time range, status (Pending, Submitted, Ready, Downloading, Loaded, Split, Failed), the Graph query, attempts and rows loaded. The resume state; finalize checks it covers the range with no gap.'),
        ('audit.CopilotAuditStage', 'Every row the backfill downloaded, per window attempt, with the record JSON gzipped in AuditDataGz. Finalize keeps each window\'s final attempt. A heap; drop it once the final tables are checked.'),
        ('audit.CopilotLoad', 'One row per -Incremental load: its range, operations, when it finished downloading and was merged, how many records were new or already there, and how many audit searches it took.'),
        ('audit.CopilotLoadWindow', 'The search windows of the load in progress, as CopilotBackfillWindow. Emptied after each merge.'),
        ('audit.CopilotLoadStage', 'The rows of the load in progress, as CopilotAuditStage. Emptied after each merge.'),
        ('audit.PipelineSetting', 'Settings the procedures read, one row each: ReportingTimeZone, the Windows time zone name every procedure cuts days and hours in (read through audit.ReportingTimeZone(); Eastern Standard Time when unset; written by Install-CopilotAudit.ps1 -ReportingTimeZone, which refuses a change once records are loaded), and from 2.3.0 PrivacyMode and PseudonymSalt (written by dash.usp_SetPrivacyMode from Admin > Privacy and retention). It also holds the marks a long job leaves: FullFlattenUtc when a flatten of every record has finished (2.4.12) and, in the license-event database, LicenseShredInProgress while a license shred runs (the time it shreds from; still there after a shred that was stopped, with the time that one began) and LicenseShredCompleteUtc when a shred of every record last finished, or the installer was told that one had (2.5.4).'),
        ('audit.PipelineRun', 'One row per run of Invoke-CopilotPipeline.ps1 (2.3.0): kind (Nightly or History), host and account, status, the step that failed and the log\'s last lines, and the app certificate it signed in with and its expiry, for the dashboard\'s alerts.'),
        ('audit.PipelineRunStep', 'One row per step of a pipeline run (2.3.0): its name, start, end and exit code.'),
        ('audit.PipelineCertificate', 'One row for each certificate the pipeline signs in with, as the job server held it at the start of its last run: Purview, for the app that reads the audit log and the licenses through Microsoft Graph, AgentWorkbook, for the app that fetches the agent workbook from SharePoint Online, where the settings name one, and Foundry (2.6.2), where the Foundry loader signs in with a certificate of its own. Invoke-CopilotPipeline.ps1 rewrites it every run and removes the row of a certificate no longer in use. Admin > Health shows each with its details, and the dashboard\'s alert watches each one\'s expiry. audit.PipelineRun still keeps the audit app\'s certificate with each run.'),
    ]),
    ('final', 'Final records', 'Every audit record once, and the slim reporting table. Built by finalize, then appended to by each load and CSV import.', [
        ('audit.CopilotAudit', 'One row per audit record, deduplicated by RecordId. Clustered columnstore; the full JSON is in AuditDataGz (read it through vCopilotAuditRecord). Lineage: WindowId and LoadId for Graph loads, CsvImportId for CSV imports.'),
        ('audit.CopilotInteraction', 'One row per Copilot interaction, shredded from the JSON by fnShredCopilotInteraction: day, user, app, agent identity, model, thread, prompt and resource counts, web grounding. Clustered columnstore.'),
        ('audit.vCopilotAuditRecord', 'CopilotAudit in the Purview portal export\'s shape (RecordId, CreationDate, RecordType, Operation, UserId, AuditData, ...), with AuditData decompressed. SQL written against the portal CSV can point here. AssociatedAdminUnitsNames is always NULL.'),
        ('audit.vCopilotDailyUsage', 'Interactions, active users, prompts and web-grounded interactions per day per app.'),
    ]),
    ('flat', 'Flattened records', 'One row per record with the Copilot event data shredded into columns, for reports written against the old RawLogFlattened table.', [
        ('audit.RawLogFlattened', 'One row per audit record. The first fifteen columns match dbo.RawLogFlattened, so existing reports can point here; multi-valued parts (accessed resources, sensitivity labels, plugins) are \'; \'-joined lists there. Since 2.2.0 every other field of the record that carries information has its own typed column, named after its JSON path; the small arrays are JSON text, NULL when empty. Filled by usp_FlattenCopilotAudit, which MERGEs.'),
        ('audit.RawLogAccessedResource', 'One row per element of CopilotEventData.AccessedResources (2.2.0): what Copilot read, its type, site, label and policy, and whether it was blocked (a failed Status, or a Purview policy outcome other than None). Page-compressed.'),
        ('audit.RawLogContext', 'One row per element of CopilotEventData.Contexts (2.2.0): where the user was during the interaction.'),
        ('audit.ValueCatalog', 'Every distinct value of 23 low-cardinality columns (2.3.0): label ids, tenant, app, agent and platform ids, models, capacities, license types, resource types. How often and when first and last seen; a column stops collecting at 10,000 values. Filled by each flatten batch; names are in dash.ValueName.'),
        ('audit.ValueCatalogColumn', 'The cataloged columns (2.3.0): which table and column, a label, how many distinct values, and whether the column passed 10,000 and was capped.'),
        ('audit.AgentCatalog', 'Every agent the audit log names (2.4.11), one row per pair of AppIdentity and AgentId as records carry them, with the friendly name ($.AgentName) of the latest record that has one and when the agent was first and last seen. Kept by the flatten for each batch, so an agent\'s name is known from its whole history; bi.usp_NameAgents names bi.DimAgent from it.'),
        ('audit.RawLogRecordShape', 'The seventeen fields that carry no information in a 19-million-record production profile (one value where present, or always empty), counted by combination instead of stored per row (2.2.0). The flattening reports if one of them ever takes a second value.'),
    ]),
    ('enrich', 'Licenses and the user directory', 'Who holds a license (Microsoft Graph) and where they sit in the organization (on-premises AD).', [
        ('audit.CopilotLicense', 'One row per user looked up by the license tool: whether they hold a Copilot license, the SKU, since when, and whether they still exist in Entra. IsCopilotLic NULL means not known, which is not the same as unlicensed.'),
        ('audit.UserDirectory', 'One row per user in the trusted AD domain, enabled or not: identity, job and location attributes, manager, and the management chain Mgr1 (the top) to Mgr<OrgDepth> (the direct manager). Replaced whole in one transaction by the AD export.'),
        ('audit.GroupMember', 'The members of each AD group granted Organization in the dashboard, nested groups included (2.4.0): the group as granted (DOMAIN\\Group) and the member\'s UPN. Rewritten by each AD export, so bi.OrgSecurity gives them the whole organization in Power BI as the dashboard does at sign-in.'),
        ('audit.LicenseSku', 'The tenant\'s subscriptions as of the license tool\'s last run (2.3.0): per SKU, seats enabled, in the grace period, suspended and assigned, and whether it is a Copilot SKU.'),
        ('audit.LicenseSkuDay', 'The same per night (2.3.0), for the license pool over time.'),
        ('audit.UserLicenseSku', 'Every SKU each person the license tool looked up holds (2.4.3), from assignedLicenses; replaced whole by each run. Licensing > Overlapping licenses reads it, with names from audit.LicenseSku.'),
        ('audit.vCopilotAdoption', 'Licensed users against actual use: first and last interaction, interactions, NeverUsed, DaysToFirstUse, with directory attributes.'),
        ('audit.vCopilotUsageByDepartment', 'Interactions and active users per department, day and app.'),
    ]),
    ('sessions', 'Sessions and the weekly rollup', 'The SQL port of the session script: sessions inferred from the flattened records, rolled up by Saturday-to-Friday week.', [
        ('audit.SessionAppCategory', 'App name to category (Copilot Chat or In-App Copilot). Seeded once; your edits survive re-running the script. Unmapped apps count as In-App Copilot.'),
        ('audit.CopilotSession', 'One row per inferred session: a person\'s prompts with less than 60 minutes between them, within one reporting-time-zone day. Primary app and license flag come from its first prompt.'),
        ('audit.CopilotWeeklyRollup', 'One row per week x user x primary app x category x license, by the week a session\'s last prompt falls in.'),
        ('audit.CopilotSessionRun', 'One row per session build: the settings used, rows read and skipped, sessions written, and RebuiltFrom (NULL for a full build, the day an incremental build started from).'),
        ('audit.vCopilotSessionSummary', 'CopilotSession with the old copilot_session_summary.csv column names and text formats (yyyy-MM-dd HH:mm:ss, TRUE/FALSE).'),
        ('audit.vCopilotWeeklyRollup', 'CopilotWeeklyRollup with the old weekly_rollup.csv column names and formats.'),
    ]),
    ('csv', 'CSV import', 'Created by tools/Import-CopilotAuditCsv.ps1 (2.1.0).', [
        ('audit.CopilotCsvImport', 'One row per run of the importer: folder, operations, files and rows read, rejected, duplicates, already loaded, added, and the covered range if given.'),
        ('audit.CopilotCsvFile', 'One row per file loaded, with its SHA-256, so a file already imported is skipped next time. Its FileId is the WindowId of the rows it added.'),
        ('audit.CopilotCsvReject', 'Rows that could not be loaded: file, the line the row starts on, the record ID if known, and why.'),
        ('audit.CopilotCsvStage', 'The staged rows of the import being merged, in the shape of CopilotAuditStage with the file in place of the window. A heap, emptied after each merge.'),
    ]),
    ('external', 'AI usage outside Microsoft 365 Copilot (2.6.2)', 'Usage and cost of GitHub Copilot and of Microsoft Foundry and Azure OpenAI deployments, each loaded by a tool of its own (tools/Import-GitHubCopilotUsage.ps1, tools/Import-FoundryUsage.ps1) into tables of its own, beside the Copilot audit data and not mixed into it. First plumbing: no report, bi view or dash table reads them yet. Days are UTC days, as each service reports them.', [
        ('audit.GitHubCopilotSeat', 'One row per GitHub Copilot seat of the enterprise or organization loaded, as GitHub lists it now: who holds it, the organization and team it was assigned through, the plan, when it was assigned and when it was last used. Replaced whole for a scope on each run. A person here is a GitHub login, which is not a Microsoft 365 sign-in.'),
        ('audit.GitHubCopilotDay', 'One row per UTC day of GitHub Copilot use by the whole enterprise or organization, from GitHub\'s usage metrics report for that day: people active, interactions, code generated and accepted, lines of code. Json keeps the whole record.'),
        ('audit.GitHubCopilotUserDay', 'One row per person per UTC day they used GitHub Copilot: what they did, the AI credits they used, and whether they used chat, an agent or the command line.'),
        ('audit.GitHubCopilotCost', 'What GitHub billed for Copilot: the lines of the billing usage whose product or SKU is Copilot, one per day, product, SKU, model and organization, in US dollars. This month and the last are replaced whole on each run.'),
        ('audit.FoundryDeployment', 'One row per model deployment of every Azure OpenAI and Microsoft Foundry account in the subscriptions loaded, as it stands: model, version, publisher, SKU and capacity.'),
        ('audit.FoundryUsageDay', 'One row per deployment, model, version and UTC day: requests and input, output and total tokens, from Azure Monitor\'s metrics of the account. No user: use is by account, deployment and model.'),
        ('audit.FoundryCostDay', 'One row per account, meter and UTC day: the actual cost Azure reports, before tax, from Cost Management, in the billing currency. By account and meter, not by deployment.'),
        ('audit.ExternalUsageRun', 'One row per run of either loader, from its start: the source, what it covered, how it ended (Succeeded, Partial or Failed), the rows written, and what was loaded or why not. Admin > Health shows the last run of each source.'),
    ]),
    ('licdb', 'License events (license-event database)', 'In the separate database the license-operations backfill fills. It also holds the loading, state and final-record tables above; these are added on top.', [
        ('audit.LicenseEvent', 'One row per license-related directory audit record (Change user license, Update user, Add user, Delete user): who changed whom, when, and whether the detail came from chunks.'),
        ('audit.LicenseEventChunk', 'Fragments of changes too large for one audit record, kept until every fragment of a group has arrived and can be reassembled.'),
        ('audit.LicenseEventProperty', 'One row per modified property of an event: Name, OldValue, NewValue. Whatever Microsoft records is queryable, whatever the property names.'),
        ('audit.vLicenseChange', 'One row per license change, deduplicated: a chunked change counts once, and the Update user raised alongside every license change is dropped in favor of the Change user license.'),
        ('audit.LicenseSkuChange', 'The SKUs each license change added and removed, kept (2.5.4): one row per record, SKU and direction. Worked out by usp_RefreshLicenseSkuChange at the end of every shred, for the events shredded since it last ran and the records raised with them. Sessions and bi read every row each night, through vLicenseSkuChange.'),
        ('audit.LicenseSkuChangeRun', 'One row per run of usp_RefreshLicenseSkuChange (2.5.4): when, the events worked out and the changes kept for them. SinceUtc is empty when every event was worked out. No row: the changes have never been worked out in this database.'),
        ('audit.vLicenseSkuChangeFromRecords', 'The rule the kept rows follow, read from the events and their properties each time: SKUs added and removed per change, from the old and new AssignedLicense lists. It was vLicenseSkuChange before 2.5.4, and is what the tests check the table against. Slow on a large history: 11 minutes a read over 4,000,000 events in the lab.'),
        ('audit.vLicenseSkuChange', 'SKUs added and removed per change: "when was this user given Copilot, and when was it taken away". Sessions and bi read it for license at the time of use. From 2.5.4 it reads audit.LicenseSkuChange.'),
        ('audit.AgentEvent', 'One row per Copilot Studio authoring record (2.3.0): the operation (BotCreate, BotUpdateOperation-BotPublish, BotUpdateOperation-BotShare, BotDelete, BotComponent...), the action it means (Created, Published, Shared, Updated, Component, Deleted), the maker, the agent and the component, and the update details. Shredded by usp_ShredAgentEvents after each license-event load, which asks for these operations too.'),
        ('audit.vLicenseLifecycle', 'Provisioning and revocation per user. "Revoked" is inferred from a license disappearing or the account being deleted: a reading of the events, not a field the service provides.'),
    ]),
    ('bidims', 'bi: dimensions', 'The star schema Power BI imports (through the bi views below). Integer surrogate keys, stable across refreshes and rebuilds.', [
        ('bi.DimDate', 'One row per date, with the Saturday-to-Friday week the rollup uses. Key: DateKey (yyyymmdd).'),
        ('bi.DimUser', 'One row per person seen in the audit data, licenses or license history: directory attributes from AD, the org chain as OrgLevel1..15, and today\'s license. Restated from the sources on every refresh.'),
        ('bi.DimApp', 'One row per app, with its category from SessionAppCategory.'),
        ('bi.DimAgent', 'One row per agent: the one the record\'s AppIdentity names (a Copilot Studio or Agent Builder agent, one of Microsoft\'s), else its AgentId (2.4.11; bi.fnAgentIdentity is the rule). AgentName is a label on the key: the friendly name the audit log gave the agent last, else one read from its id, so a renamed agent keeps its key and its history. (no agent) is Copilot itself.'),
        ('bi.DimModel', 'One row per model provider and name.'),
        ('bi.DimSku', 'One row per license SKU seen in the license history, flagged when it is a Copilot SKU.'),
        ('bi.DimLicenseStatus', 'Three rows: 0 Unknown, 1 Licensed, 2 Not licensed.'),
        ('bi.DimLicenseType', 'The license type the audit record itself reports (CopilotEventData.LicenseType), 2.2.0. Key 1 is (not reported).'),
        ('bi.DimSensitivityLabel', 'One row per sensitivity label id seen on an interaction or a resource it read (2.2.0). Key 1 is (none). Names are in dash.SensitivityLabel.'),
        ('bi.DimFileType', 'What a resource Copilot read was (2.4.6): a file\'s extension (docx, xlsx, pptx, csv ...) from AccessedResources[].Name, and for anything else its Type (EmailMessage, TeamsMessage, StreamVideo ...).'),
        ('bi.DimResourceType', 'Accessed-resource types: File, EmailMessage, TeamsMessage, WebSearchQuery ... (2.2.0).'),
        ('bi.DimResourceAction', 'Accessed-resource actions: Read, Create ... (2.2.0).'),
        ('bi.DimSite', 'The site a resource lives on: https://host/sites/name (or /teams/, /personal/), otherwise the host; IsPersonal marks OneDrive (2.2.0).'),
    ]),
    ('bifacts', 'bi: facts and refresh log', 'Narrow facts with integer keys. FactInteraction is a clustered columnstore and the only one refreshed incrementally in Power BI.', [
        ('bi.FactInteraction', 'One row per Copilot interaction: date, hour, user, app, agent, model, license status at the time of use, counts and safety flags. RecordId and CreatedUtc are kept for lineage and left out of the view the model imports.'),
        ('bi.FactSession', 'One row per inferred session, following the session builds.'),
        ('bi.FactLicenseChange', 'One row per Copilot SKU added (+1) or removed (-1) from a person.'),
        ('bi.FactLicensedDay', 'One row per person per day on which they held a Copilot license at the end of the day.'),
        ('bi.FactAccessedResource', 'One row per resource an interaction read (2.2.0), from audit.RawLogAccessedResource: type, action, site, label, blocked and prompt-injection flags. Clustered columnstore, refreshed with the interactions.'),
        ('bi.FactAgentEvent', 'One row per Copilot Studio authoring event (2.3.0), copied from audit.AgentEvent in every license-event database by each refresh, with the day in the reporting time zone and the maker\'s user key.'),
        ('bi.RebuildInProgress', 'A row while a full rebuild of bi.FactInteraction runs (2.4.9): bi.usp_RefreshBi puts it there before it empties the fact and takes it out when it has finished. While a row is here every refresh is a full one, so a rebuild stopped part way is finished by the next run.'),
        ('bi.RefreshRun', 'One row per bi.usp_RefreshBi run: window (SinceUtc NULL is a full rebuild), time zone, license databases, rows written, the session build it followed, and from 2.4.0 the first day with use of an app whose category it changed (RecategorizedFromDateKey), from which the dashboard rolls up again.'),
    ]),
    ('biviews', 'bi: views Power BI imports', 'One view per table above, leaving out the lineage columns. The model imports these, not the tables.', [
        ('bi.Interactions', 'bi.FactInteraction for the model.'), ('bi.Sessions', 'bi.FactSession for the model.'),
        ('bi.LicenseChanges', 'bi.FactLicenseChange for the model.'), ('bi.LicensedDays', 'bi.FactLicensedDay for the model.'),
        ('bi.Calendar', 'bi.DimDate for the model; mark it as the date table.'), ('bi.Users', 'bi.DimUser for the model.'),
        ('bi.Apps', 'bi.DimApp for the model.'), ('bi.Agents', 'bi.DimAgent for the model.'), ('bi.Models', 'bi.DimModel for the model.'),
        ('bi.Skus', 'bi.DimSku for the model.'), ('bi.LicenseStatuses', 'bi.DimLicenseStatus for the model.'),
        ('bi.AccessedResources', 'bi.FactAccessedResource for the model.'), ('bi.LicenseTypes', 'bi.DimLicenseType for the model.'),
        ('bi.SensitivityLabels', 'bi.DimSensitivityLabel for the model, with the id as lower-case text.'), ('bi.ResourceTypes', 'bi.DimResourceType for the model.'), ('bi.FileTypes', 'bi.DimFileType for the model (2.4.6).'),
        ('bi.ResourceActions', 'bi.DimResourceAction for the model.'), ('bi.Sites', 'bi.DimSite for the model; in privacy mode a OneDrive site is named OneDrive (personal).'),
        ('bi.AgentEvents', 'bi.FactAgentEvent for the model (2.3.0).'),
        ('bi.OrgSecurity', 'Who may see whom (2.3.0): each viewer\'s sign-in with the user key of everyone at or under them, and everyone for the top of the organization, the people reporting to them and people granted Organization. The Power BI model\'s Organization role filters Users by it. Empty in privacy mode.'),
        ('bi.ValueNames', 'Each cataloged value with the name an administrator gave it (2.3.0), from audit.ValueCatalog and dash.ValueName.'),
        ('bi.LicensePool', 'Seats bought, assigned and available per SKU per day (2.3.0), from audit.LicenseSkuDay.'),
        ('bi.DepartmentWeek', 'Active people, interactions and sessions by department and week (2.3.0), for a model that must not show individuals. From 2.4.12 every department has a row for every week of the data, used or not; departments with fewer enabled people than the minimum group size are combined as (smaller departments), and a week with fewer active people than the minimum is blank.'),
        ('bi.UseCases', 'Every combination of app, web search and agent (UseCaseKey = AppKey x 4 + web x 2 + agent) with the use case the rules in dash.UseCaseRule give it (2.3.0). bi.Interactions.UseCaseKey points here.'),
        ('bi.JobFamilies', 'Each person\'s job family from the rules in dash.JobFamilyRule (2.3.0); (no family) when none matches.'),
        ('bi.EventMarkers', 'The events marked on the dashboard\'s trends (2.3.0), with their day key.'),
    ]),
    ('dash', 'dash: the Copilot Insights dashboard', 'What the dashboard (dashboard/CopilotInsights, see DASHBOARD.md) keeps besides the bi schema it reads: its settings, roles, reference names and audit log, which the admin module edits, and the org chart and daily rollups dash.usp_RefreshDashboard rebuilds after each bi refresh.', [
        ('dash.Setting', 'Settings the admin module edits: OrgTopUpn (the top of the organization), MinGroupSize, DefaultDays, InactiveDays. Seeded; edits survive re-running the script.'),
        ('dash.RoleGrant', 'Roles granted to people (UPN or DOMAIN\\user) or AD groups (DOMAIN\\Group). Admin runs the admin module; Organization sees the whole organization; the persona roles unlock named detail.'),
        ('dash.AgentAlias', 'Friendly names and owners for agents, which the audit log names by schema name. Edited in Admin > Reference data.'),
        ('dash.SensitivityLabel', 'Names and order for sensitivity labels, which the audit log records by id only. Edited in Admin > Reference data.'),
        ('dash.AuditLog', 'Every page view, refusal, export and admin change: who, what page, what scope, what filters or change, and from where.'),
        ('dash.Person', 'One row per person in the user directory (and each manager missing from it), with their level below the top and how many people are under them. Rebuilt whole by each refresh.'),
        ('dash.OrgClosure', 'Every (manager, person under them) pair at any depth, and each person with themselves at depth 0: the security model\'s scope. Rebuilt whole by each refresh.'),
        ('dash.UserDay', 'One row per person per day with any use or session: the daily rollup behind every page, so the 7- to 365-day periods are exact.'),
        ('dash.FlaggedInteraction', 'The interactions with a security signal (2.4.7), a few in every thousand, each with the record id its triage is kept under: Security > Flagged interactions lists them from here. Kept by dash.usp_RefreshDashboard from bi.FactInteraction.'),
        ('dash.ResourceDay', 'Labels on content Copilot read, the sites of labeled content, and the sites it was blocked from, per person per day, from bi.FactAccessedResource.'),
        ('dash.AgentUse', 'When each agent was first and last used, from the facts. Rebuilt whole by each refresh.'),
        ('dash.RefreshRun', 'One row per dashboard refresh: when, from which day, how many people, org pairs and person-days it wrote, and from 2.4.0 the latest bi refresh it reflects (BiRunId).'),
        ('dash.Agent', 'Agents with their friendly name, owner and first use.'),
    ]),
    ('ops', 'dash: operations (2.3.0)', 'What the dashboard\'s operations features keep, from Create-CopilotOperationsSchema.sql: every table is edited through the Admin pages or the pages named, never by hand.', [
        ('dash.Secret', 'Passwords, client secrets and the webhook address, encrypted by SQL Server (a symmetric key protected by a certificate and the database master key). The site\'s role reads them only through dash.usp_GetSecret and cannot select the table.'),
        ('dash.Subscription', 'Scheduled digests (Admin > Alerts and digests): report, recipients, frequency, day and hour in the reporting time zone, period, optionally one manager\'s organization, and the last send and its result.'),
        ('dash.NotificationLog', 'Every email and webhook post, sent or not, with the reason when not, and from 2.6.2 the conversation with the mail server (Detail), which Admin > Email shows for troubleshooting.'),
        ('dash.AlertState', 'What each alert check last saw, so an alert fires once per occurrence.'),
        ('dash.Target', 'Adoption targets (Admin > Targets and assumptions): measure, value, start and due dates, for the whole organization or one manager\'s.'),
        ('dash.TimeSaving', 'Minutes one interaction saves in each app, where it differs from the default.'),
        ('dash.TrainingAttendance', 'Who attended which training session, from a CSV (Admin > Training records).'),
        ('dash.ValueName', 'Names and notes administrators gave cataloged values (Admin > Value names).'),
        ('dash.FlagCase', 'Security\'s triage of a flagged interaction: status (New, Investigating, Closed, FalsePositive), owner and note, by record id.'),
        ('dash.DigestPreference', 'Managers who do not get their team digest, kept by administrators (Admin > Alerts and digests). PersonalDigest is unused since 2.4.4.'),
        ('dash.DigestSend', 'One row per person, kind (Manager or Personal) and period: sent, or the try that failed (Fail1 to Fail3), so a digest is never sent twice and a failure is retried.'),
        ('dash.LicenseAction', 'Each person in each license export (upgrade, revoke, leaver, wave), with who exported it and when, for Forecasting > License action outcomes.'),
        ('dash.LicenseContract', 'License contracts (Forecasting > Budget and renewal): seats, price per seat a month, start and renewal dates.'),
        ('dash.NonHumanException', 'Accounts a non-human rule matches that an administrator confirmed are a person, with the reason (2.4.5): a Copilot license on a non-human account is a policy breach, and these are not. Removing a row flags the account again.'),
        ('dash.SavedView', 'Unused since 2.4.4, which removed saved views; kept, with its rows. Each was a page and its query, one of which could be the start page.'),
        ('dash.JobFamilyRule', 'Job-title patterns (SQL LIKE) to job families, first matching by priority (Admin > Classifications and events).'),
        ('dash.UseCaseRule', 'App patterns, web search and agent to use cases, first matching by priority; seeded with defaults.'),
        ('dash.EventMarker', 'Events drawn on the trend charts: day, name and kind.'),
        ('dash.Extract', 'Scheduled CSV extracts (Admin > Scheduled extracts): dataset, folder, schedule, days covered, and the last run and its result.'),
    ]),
]
LEGACY = [('audit.UserProfile', 'Entra profiles written by the 1.x license tool. No 2.x script creates, fills or reads it; only the optional ASP.NET dashboard still reads it for names. Safe to drop once that no longer matters.'),
          ('audit.AdministrativeUnit', 'Administrative-unit names written by the 1.x license tool. Not used since 2.0.0; safe to drop.')]

LIC_ONLY = {'audit.AgentEvent', 'audit.LicenseEvent', 'audit.LicenseEventChunk', 'audit.LicenseEventProperty', 'audit.LicenseSkuChange', 'audit.LicenseSkuChangeRun',
            'audit.vLicenseChange', 'audit.vLicenseSkuChangeFromRecords', 'audit.vLicenseSkuChange', 'audit.vLicenseLifecycle'}
BOTH = {'audit.CopilotBackfillRange', 'audit.CopilotBackfillWindow', 'audit.CopilotAuditStage', 'audit.CopilotLoad', 'audit.CopilotLoadWindow',
        'audit.CopilotLoadStage', 'audit.CopilotAudit', 'audit.CopilotInteraction', 'audit.vCopilotAuditRecord', 'audit.vCopilotDailyUsage'}


def cols_of(name):
    sch, obj = name.split('.')
    db = 'CopilotLicenseEvents' if name in LIC_ONLY else 'CopilotAudit'
    rows = [c for c in meta[db]['columns'] if c['sch'] == sch and c['obj'] == obj]
    idx = [i for i in meta[db]['indexes'] if i['sch'] == sch and i['obj'] == obj]
    chk = [k for k in meta[db]['checks'] if k['sch'] == sch and k['obj'] == obj]
    return rows, idx, chk


def typ(c):
    t, n = c['typ'], c['len']
    if t in ('nvarchar', 'nchar'):
        return f"{t}({'max' if n == -1 else n // 2})"
    if t in ('varchar', 'char', 'varbinary', 'binary'):
        return f"{t}({'max' if n == -1 else n})"
    if t in ('decimal', 'numeric'):
        return f"{t}({c['prec']},{c['scale']})"
    if t in ('datetime2', 'time', 'datetimeoffset'):
        return f"{t}({c['scale']})"
    return t


OVERRIDE = {
    ('audit.CopilotAudit', 'LoadId'): 'NULL: the backfill or a CSV import. Otherwise the audit.CopilotLoad that appended the row, and WindowId is then a row of audit.CopilotLoadWindow.',
    ('audit.CopilotAudit', 'CsvImportId'): 'Set when the row came from a CSV import (audit.CopilotCsvImport); WindowId is then its audit.CopilotCsvFile.',
    ('audit.RawLogFlattened', 'OrganizationId'): '$.OrganizationId: the tenant. One value in a single-tenant database; kept on every row so a database can hold more than one.',
    ('audit.RawLogFlattened', 'UserKey'): '$.UserKey',
    ('audit.RawLogFlattened', 'ClientIP'): '$.ClientIP',
    ('audit.RawLogFlattened', 'ApplicationName'): '$.ApplicationName',
    ('audit.RawLogFlattened', 'PlatformAgentId'): '$.PlatformAgentId',
    ('audit.RawLogFlattened', 'ResultStatus'): '$.ResultStatus',
    ('audit.RawLogFlattened', 'AppAccessContext_APIId'): '$.AppAccessContext.APIId',
    ('audit.RawLogFlattened', 'CopilotEventData_LicenseType'): '$.CopilotEventData.LicenseType: the license the record reports (Premium, Starter ...)',
    ('audit.RawLogFlattened', 'CopilotEventData_ConversationId'): '$.CopilotEventData.ConversationId',
    ('audit.RawLogFlattened', 'CopilotEventData_CorrelationId'): '$.CopilotEventData.CorrelationId',
    ('audit.RawLogFlattened', 'CopilotEventData_SensitivityLabelId'): "$.CopilotEventData.SensitivityLabelId: the interaction's own label",
    ('audit.RawLogFlattened', 'CopilotEventData_TargetAgentName'): '$.CopilotEventData.TargetAgentName: an agent this one handed work to',
    ('audit.RawLogFlattened', 'CopilotEventData_TargetPlatformAgentId'): '$.CopilotEventData.TargetPlatformAgentId',
    ('audit.RawLogFlattened', 'CopilotEventData_DLPEvaluationDeferred'): '$.CopilotEventData.DLPEvaluationDeferred',
    ('audit.RawLogFlattened', 'CopilotEventData_MemoryUpdated'): '$.CopilotEventData.MemoryUpdated',
    ('audit.RawLogFlattened', 'CopilotEventData_MemoryCorrelationId'): '$.CopilotEventData.MemoryCorrelationId',
    ('audit.RawLogFlattened', 'CopilotEventData_FabricCapacityId'): '$.CopilotEventData.FabricCapacityId',
    ('audit.RawLogFlattened', 'CopilotEventData_PlatformAgentId'): '$.CopilotEventData.PlatformAgentId',
    ('audit.RawLogFlattened', 'CopilotEventData_TaskId'): '$.CopilotEventData.TaskId',
    ('audit.RawLogFlattened', 'ModelVersion'): 'ModelTransparencyDetails[0].ModelVersion',
    ('audit.RawLogFlattened', 'MessageCount'): 'Messages[*]: prompts and responses',
    ('audit.RawLogFlattened', 'CopilotEventData_AISystemPlugin'): '$.CopilotEventData.AISystemPlugin as JSON text; NULL when empty',
    ('audit.RawLogFlattened', 'CopilotEventData_ModelTransparencyDetails'): '$.CopilotEventData.ModelTransparencyDetails as JSON text; NULL when empty',
    ('audit.RawLogFlattened', 'CopilotEventData_ParticipatingAgents'): '$.CopilotEventData.ParticipatingAgents as JSON text; NULL when empty',
    ('audit.RawLogFlattened', 'CopilotEventData_SourceThreadIds'): '$.CopilotEventData.SourceThreadIds as JSON text; NULL when empty',
    ('bi.FactInteraction', 'LicenseTypeKey'): 'bi.DimLicenseType: the license the record reports (2.2.0)',
    ('bi.FactInteraction', 'SensitivityLabelKey'): "bi.DimSensitivityLabel: the interaction's own label (2.2.0)",
    ('bi.FactInteraction', 'MessageCount'): 'prompts and responses (2.2.0)',
    ('bi.FactInteraction', 'MemoryUpdated'): '2.2.0',
    ('bi.FactInteraction', 'DlpEvaluationDeferred'): '2.2.0',
    ('bi.FactInteraction', 'CalledAnotherAgent'): 'TargetAgentName set (2.2.0)',
}
AD_ATTR = {'displayName', 'title', 'department', 'l', 'co', 'company', 'division', 'mail', 'manager', 'physicalDeliveryOfficeName', 'employeeType', 'userPrincipalName'}


def clean(note):
    note = re.sub(r'\s*/?\*+/?\s*', ' ', note)
    note = re.sub(r'\s+', ' ', note).strip(' -')
    return note


def card(name, purpose, legacy=False):
    rows, idx, chk = cols_of(name)
    if not rows:
        return ''
    kind = 'View' if rows[0]['otype'] == 'V' else 'Table'
    script = PRIMARY.get(name, scr['created'].get(name, '1.x (no current script)' if legacy else ''))
    added = [s for s in scr['altered'].get(name, []) if s != script]
    dbs = 'Copilot and license-event databases' if name in BOTH else ('License-event database' if name in LIC_ONLY else 'Copilot database')
    pk = next((i for i in idx if i['pk']), None)
    pkcols = set(pk['keys'].split(', ')) if pk else set()
    n = notes.get(name, {})
    anchor = name.replace('.', '-')
    out = [f'<article class="obj{" legacy" if legacy else ""}" id="{anchor}" data-search="{E(name.lower())} {E(" ".join(c["col"].lower() for c in rows))}">']
    out.append(f'<header><h3><span class="sch">{E(name.split(".")[0])}.</span>{E(name.split(".")[1])}</h3>'
               f'<span class="kind k-{kind.lower()}">{kind}</span></header>')
    out.append(f'<p class="purpose">{E(purpose)}</p>')
    meta_bits = [f'<span><b>Script</b> {E(script)}</span>', f'<span><b>In</b> {E(dbs)}</span>']
    if name in ALSO:
        meta_bits.append(f'<span><b>Also</b> {E(ALSO[name])}</span>')
    if added:
        meta_bits.append(f'<span><b>Columns added by</b> {E(", ".join(added))}</span>')
    meta_bits.append(f'<span><b>Columns</b> {len(rows)}</span>')
    out.append('<div class="meta">' + ''.join(meta_bits) + '</div>')
    out.append('<div class="table-wrap"><table class="cols"><thead><tr><th class="n">#</th><th>Column</th><th>Type</th><th>Null</th><th>Default / key</th><th>Notes</th></tr></thead><tbody>')
    for c in rows:
        flags = []
        if c['col'] in pkcols:
            flags.append('<span class="pk">PK</span>')
        if c['identity']:
            flags.append('identity')
        if c['dflt']:
            flags.append(f'<code>{E(c["dflt"].strip("()"))}</code>')
        if c['computed']:
            flags.append(f'computed <code>{E(c["computed"])}</code>')
        note = OVERRIDE.get((name, c['col'])) or clean(n.get(c['col'], ''))
        if name == 'audit.UserDirectory' and note in AD_ATTR:
            note = 'AD attribute: ' + note
        out.append(f'<tr><td class="n">{c["id"]}</td><td class="c">{E(c["col"])}</td><td class="t">{E(typ(c))}</td>'
                   f'<td class="nl">{"yes" if c["nullable"] else "<b>no</b>"}</td><td>{" ".join(flags)}</td><td class="note">{E(note)}</td></tr>')
    out.append('</tbody></table></div>')
    extras = []
    for i in sorted(idx, key=lambda i: (not i['pk'], i['idx'])):
        what = i['type'].replace('_', ' ').lower()
        keys = f' ({E(i["keys"])})' if i['keys'] else ''
        incl = f' include ({E(i["incl"])})' if i['incl'] else ''
        filt = f' where {E(i["filter"])}' if i['filter'] else ''
        tag = 'primary key, ' if i['pk'] else ('unique, ' if i['unique'] else '')
        extras.append(f'<li><code>{E(i["idx"])}</code>: {tag}{what}{keys}{incl}{filt}</li>')
    for k in chk:
        extras.append(f'<li><code>{E(k["chk"])}</code>: check <code>{E(k["def"])}</code></li>')
    if extras:
        out.append('<ul class="idx">' + ''.join(extras) + '</ul>')
    out.append('</article>')
    return '\n'.join(out)


# ---------------------------------------------------------------- page
counts = {'tables': 0, 'views': 0, 'columns': 0}
body, nav = [], []
for key, title, intro, objs in AREAS:
    nav.append(f'<li><a class="area" href="#{key}">{E(title)}</a><ol>' +
               ''.join(f'<li><a href="#{o.replace(".", "-")}">{E(o)}</a></li>' for o, _ in objs) + '</ol></li>')
    body.append(f'<section id="{key}" class="area-sec"><h2>{E(title)}</h2><p class="intro">{E(intro)}</p>')
    for o, p in objs:
        c = card(o, p)
        if c:
            rows = cols_of(o)[0]
            counts['views' if rows[0]['otype'] == 'V' else 'tables'] += 1
            counts['columns'] += len(rows)
        body.append(c)
    body.append('</section>')
nav.append('<li><a class="area" href="#legacy">Left over from 1.x</a><ol>' + ''.join(f'<li><a href="#{o.replace(".", "-")}">{E(o)}</a></li>' for o, _ in LEGACY) + '</ol></li>')
body.append('<section id="legacy" class="area-sec"><h2>Left over from 1.x</h2><p class="intro">Present in a database upgraded from 1.x, as the lab is. No current script creates them.</p>')
body += [card(o, p, legacy=True) for o, p in LEGACY]
legacy_tables = len(LEGACY)
body.append('</section>')

script_rows = ''.join(
    f'<tr><td><code>sql/{E(s)}</code></td><td>{E(db)}</td><td>{E(when)}</td><td>{E(what)}</td></tr>' for s, db, when, what in SCRIPTS)

tmpl = open(os.path.join(HERE, 'template.html'), encoding='utf-8').read()
page = (tmpl.replace('%%SCRIPTS%%', script_rows).replace('%%NAV%%', ''.join(nav)).replace('%%BODY%%', '\n'.join(body))
        .replace('%%TABLES%%', str(counts['tables'])).replace('%%VIEWS%%', str(counts['views'])).replace('%%COLUMNS%%', str(counts['columns'])))
open(os.path.join(OUT, 'copilot-audit-schema.html'), 'w', encoding='utf-8').write(page)
print(counts)
