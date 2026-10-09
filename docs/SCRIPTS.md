# The scripts, in summary

Every script in this repository, what it does, who runs it and when. It is a map: each script's own header has the detail, its parameters and its examples, and the guides say how the scripts fit together ([DEPLOYMENT.md](DEPLOYMENT.md) to install, [OPERATIONS.md](OPERATIONS.md) to run day to day, [SCHEMA.md](SCHEMA.md) for the tables).

## Contents

- [How they fit together](#how-they-fit-together)
- [Setting up and upgrading](#setting-up-and-upgrading)
- [The nightly run](#the-nightly-run)
- [Loaders the nightly run calls](#loaders-the-nightly-run-calls)
- [Day-to-day tools](#day-to-day-tools)
- [SQL scripts](#sql-scripts)
- [The dashboard](#the-dashboard)
- [Power BI](#power-bi)
- [Development and release tools](#development-and-release-tools)
- [Tests](#tests)

## How they fit together

Three computers, each with its own scripts:

| Where | Scripts | What they do |
|---|---|---|
| The job server | The four in the repository's root, and `tools\` | Read Microsoft Purview, Microsoft Graph and Active Directory, and load SQL Server. `Invoke-CopilotPipeline.ps1` runs them every night as a scheduled task. |
| SQL Server | `sql\` | Create the tables, views and procedures. `Install-CopilotAudit.ps1` runs them; nobody runs them by hand in a normal install. |
| The web server | `dashboard\` | Install the Copilot Insights dashboard on IIS and build its report pages ahead each morning. |

A new install is three commands: `tools\Install-CopilotJobServer.ps1` on the job server, `Install-CopilotAudit.ps1` for the databases (the first runs it for you), and `dashboard\Install-CopilotInsights.ps1` on the web server. An upgrade is the last two again, over what is there.

## Setting up and upgrading

| Script | What it does | Run by |
|---|---|---|
| `tools\Install-CopilotJobServer.ps1` | Sets up the job server in one command: the certificate, the service account, both databases, the app registration, the settings file and the nightly task. Running it again keeps every setting. With `-RegisterApp` it renews the certificate. | An administrator, once, and to renew the certificate |
| `Install-CopilotAudit.ps1` | Creates, upgrades and builds the Copilot database and the license-event database: runs every script in `sql\` in order, then whatever one-off work a release needs (a rebuild, a backfill of a new column). Safe to run again; a second run changes nothing. | An administrator, at install and at every upgrade |
| `tools\New-CopilotAuditApp.ps1` | Registers the Entra app the pipeline signs in as: three Microsoft Graph permissions, their admin consent, and the job server's certificate. | `Install-CopilotJobServer.ps1`, or an administrator by hand |
| `tools\Add-CopilotTenant.ps1` | Adds another Microsoft 365 tenant to the job server: its app registration, its two databases and its entry in the settings. | An administrator, for a group with several tenants |
| `tools\Save-CopilotAuditModules.ps1` | Downloads the PowerShell modules the pipeline needs into a `Modules` folder, checking Microsoft's signatures, for a server with no access to the PowerShell Gallery. The offline release zip is built with it. | The release build; an administrator preparing an offline server |
| `dashboard\Install-CopilotInsights.ps1` | Installs or updates the dashboard on IIS: the site, its account, its database access, its bindings, and the scheduled morning pre-cache. An update keeps what the site has. | An administrator, at install and at every upgrade |

## The nightly run

| Script | What it does | Run by |
|---|---|---|
| `Invoke-CopilotPipeline.ps1` | Runs the whole pipeline from the settings file (`copilot-audit.settings.psd1`): the nightly run, or with `-History` the first load of everything. It calls the scripts below in order, records each step in `audit.PipelineRun`, and writes a log. A step marked optional that fails is recorded and the run goes on. | The scheduled task *Copilot audit - nightly* |
| `Invoke-CopilotAuditBackfill.ps1` | Reads Copilot audit records from Microsoft Purview through the Microsoft Graph audit search API straight into SQL Server: the first backfill of months of history, resumable and within Microsoft's limits, and with `-Incremental` each night's new records. Also loads the license records into the license-event database. | `Invoke-CopilotPipeline.ps1` |
| `Import-CopilotAuditEngine.ps1` | Compiles the engine the backfill runs on (`CopilotAuditEngine.cs`) into the PowerShell session. | `Invoke-CopilotAuditBackfill.ps1` and the tests |

## Loaders the nightly run calls

Each loads one source into the Copilot database. All can be run by hand with the same result.

| Script | What it loads | From |
|---|---|---|
| `tools\Export-UsersWithHierarchy-ToSql.ps1` | The user directory, `audit.UserDirectory`: each person's title, department, division, office, country, manager and management chain, which every report is cut by. | On-premises Active Directory |
| `tools\Update-CopilotAuditEnrichment.ps1` | Who holds a Copilot license today, and the tenant's license pool. | Microsoft Graph |
| `tools\Import-AgentWorkbook.ps1` | The agent inventory workbook: each sheet into its own table, then `dash.AgentInventory`, matched to the agents the audit log knows. From 2.6.3 it also marks the second identity of an agent the audit log names two ways. | An Excel workbook on SharePoint Online or a file share |
| `tools\Import-GitHubCopilotUsage.ps1` | GitHub Copilot's seats, daily usage, each person's day and billed cost, for an enterprise or an organization. Optional: skipped, with a note in the log, when the settings do not name one. See [GITHUB-COPILOT-USAGE.md](GITHUB-COPILOT-USAGE.md). | GitHub's REST API |
| `tools\Import-FoundryUsage.ps1` | Microsoft Foundry and Azure OpenAI deployments, their daily requests and tokens, and their daily cost. Optional, as the GitHub load is. See [FOUNDRY-USAGE.md](FOUNDRY-USAGE.md). | Azure Resource Manager, Azure Monitor and Cost Management |
| `tools\ExternalUsage.Common.ps1` | Not run by itself: what the two loaders above share (the database connection, writing rows to a table, the run's own log). | The two loaders |

## Day-to-day tools

| Script | What it does | Run by |
|---|---|---|
| `tools\Invoke-CopilotMaintenance.ps1` | Day-to-day tasks on the database through `-Task`, so nobody types SQL by hand: the status of the loads, grant or revoke a role, name an agent, set an app's category, rebuild the sessions or the reporting tables, and the rest. [OPERATIONS.md](OPERATIONS.md) lists every task. | An administrator |
| `tools\Test-CopilotDataHealth.ps1` | A read-only health check of both databases: what has been loaded and from where, what is missing or behind, and what to run to put it right. | An administrator, when figures look wrong |
| `tools\Measure-CopilotNightly.ps1` | Shows where a nightly run's time goes: each step, each stage inside the two loads, and the statements that ran longest. Read-only. | An administrator, when a night runs long |
| `tools\Trace-CopilotNightly.sql` | For the database administrator: what the nightly task is running in SQL Server now, and a recording of every statement that takes a second or longer. The companion of the tool above. | A database administrator |
| `tools\Import-CopilotAuditCsv.ps1` | Imports a folder of audit log exports from the Purview portal (CSV) into the same tables the Graph backfill fills, for history the API no longer holds or a tenant with no app. | An administrator, as needed |
| `tools\Export-CopilotSessionCsv.ps1` | Writes the session summary and the weekly rollup as the CSV files the old session script produced, so anything that read those files keeps working. | An administrator, or a scheduled task of your own |

## SQL scripts

`Install-CopilotAudit.ps1` runs these, in this order, apart from the two the table says another tool runs. Every one is idempotent: it creates what is missing, adds the columns a newer release needs, and never drops data. From 2.6.3 a release only adds to what they create ([SCHEMA.md](SCHEMA.md), *Compatibility*).

| Script | Database | What it creates |
|---|---|---|
| `Create-CopilotAuditSchema.sql` | Both | The tables the loads write to and keep their place in: the backfill's ranges, windows and staging, the nightly loads, and the pipeline's settings, runs, steps and certificates. |
| `Finalize-CopilotAudit.sql` | Both | Not run by the installer: the backfill runs it once when the first load is complete. Builds `audit.CopilotAudit` and `audit.CopilotInteraction` from staging, removing duplicates, and refuses a range that is not fully loaded. |
| `Create-CopilotLoadProcedure.sql` | Both | `audit.usp_MergeCopilotLoad`, which appends each night's load to the final tables instead of rebuilding them. |
| `Create-CopilotCsvImport.sql` | Copilot | The CSV import's tables and `audit.usp_MergeCopilotCsv`. In a database with no backfill it also creates the final tables. Run by `tools\Import-CopilotAuditCsv.ps1`, not by the installer. |
| `Create-CopilotFlattenedSchema.sql` | Copilot | `audit.RawLogFlattened` and its companions: one row per audit record with the record's JSON in columns. |
| `Create-CopilotFlattenProcedure.sql` | Copilot | `audit.usp_FlattenCopilotAudit`, which reads each record's JSON once into the flattened tables, and the catalog of every value and every agent seen. |
| `Create-CopilotPurgeProcedure.sql` | Copilot | `audit.usp_PurgeCopilotAuditJson`: retention for the bulky part, the compressed JSON, leaving the reporting rows. |
| `Create-CopilotEnrichmentSchema.sql` | Copilot | What the records cannot carry about a person: `audit.CopilotLicense`, `audit.UserDirectory`, group members, license SKUs, and the adoption views over them. |
| `Create-CopilotSessionSchema.sql` | Copilot | The inferred sessions and the Saturday-to-Friday weekly rollup, with the app categories they use. |
| `Create-CopilotSessionProcedure.sql` | Copilot | `audit.usp_BuildCopilotSessions`, which builds them from the flattened records. |
| `Create-CopilotBiSchema.sql` | Copilot | The `bi` schema: the star schema reports read. Dimension and fact tables, and the views over them. |
| `Create-CopilotBiProcedure.sql` | Copilot | `bi.usp_RefreshBi`, which fills it each night; `bi.usp_NameAgents`, which names the agents; and from 2.6.3 `bi.usp_MergeAgents`, which makes one agent of the identities the audit log gives the same agent. |
| `Create-CopilotExternalUsageSchema.sql` | Copilot | The eight tables for AI usage outside Microsoft 365 Copilot (2.6.2): GitHub Copilot and Microsoft Foundry. |
| `Create-CopilotDashboardSchema.sql` | Copilot | The `dash` schema, with `-Dashboard`: who may see what, settings and reference data, the audit log, the agent inventory, and the rollups the dashboard reads. |
| `Create-CopilotDashboardProcedure.sql` | Copilot | `dash.usp_RefreshDashboard`, which rebuilds the org chart and the rollups after each `bi` refresh. |
| `Create-CopilotOperationsSchema.sql` | Copilot | What the dashboard's operations features keep, with `-Dashboard`: email and alerts, digests, targets, license actions, saved views, classifications, extracts, and the `bi` views built on them. |
| `Create-LicenseEventSchema.sql` | License | License history from the audit log: each license assigned or removed, and the changes worked out from them. |
| `Create-LicenseEventProcedure.sql` | License | `audit.usp_ShredLicenseEvents`, which turns the directory's audit records into those rows. |
| `Create-AgentEventProcedure.sql` | License | `audit.AgentEvent` and `audit.usp_ShredAgentEvents`: who created, changed, published, shared or deleted which Copilot Studio agent. |

## The dashboard

| Script | What it does | Run by |
|---|---|---|
| `dashboard\Install-CopilotInsights.ps1` | Installs or updates the site on IIS (see [Setting up and upgrading](#setting-up-and-upgrading)). | An administrator |
| `dashboard\Invoke-InsightsPrecache.ps1` | Builds every report page ahead on the web server and waits until it is done, so the first reader of the day does not wait. | The scheduled task *Copilot Insights pre-cache*, each morning |
| `dashboard\demo\New-InsightsDemo.ps1` | Builds a demo database: a synthetic company of 2,317 people with six months of Copilot use, to try the dashboard without real data. | Anyone evaluating or developing |
| `dashboard\demo\New-InsightsDemoData.sql`, `New-InsightsDemoOperations.sql` | The demo's data: the people, licenses and interactions, then the data for the operations pages. | `New-InsightsDemo.ps1` |

## Power BI

| Script | What it does | Run by |
|---|---|---|
| `powerbi\generate_model.py` | Writes the Power BI project (`powerbi\CopilotInsights`): a semantic model over the `bi` views, with row-level security and incremental refresh, and a report to build on. It never overwrites a project that holds work of your own. | A developer, after a `bi` view changed; a report author, once, to start |

## Development and release tools

In `tools\dev\`; [tools/dev/README.md](../tools/dev/README.md) has the steps and the release checklist. None runs in production.

| Script | What it does |
|---|---|
| `schema\extract.ps1`, `scripts.py`, `build.py`, `build_md.py` | Regenerate the schema reference from installed databases and the scripts' own comments: read the metadata, map each table to its script, then write the page and `docs/SCHEMA.md`. |
| `schema\Build-ReportingReference.ps1` | Fills the generated parts of `docs/REPORTING-REFERENCE.md`: the column reference, copied from `docs/SCHEMA.md`, and the text of the scripts that create and fill the schema. |
| `schema\build_docx.py` | Writes the Word versions of the two reporting guides, `docs/REPORTING.docx` and `docs/REPORTING-REFERENCE.docx`. |
| `schema\Update-SchemaBaseline.ps1` | Takes `tests/schema-baseline.tsv` again at a release: the shape of the schema, which the next release must stay compatible with. Refuses when the release breaks the one in place. |
| `ui\ui_audit.py` | Opens every dashboard page in a headless browser and reports layout faults: clipped or overlapping text, labels cut short, page scroll. |

## Tests

In `tests\`. CI runs all of them on every push, and the release build runs them again before it publishes.

| Script | What it proves |
|---|---|
| `Test-Engine.ps1` | The backfill engine end to end against a stand-in for the Graph audit search API (`mock_graph.py`): adaptive windows, throttling, failures, a hard stop and a resume, every record landing once. |
| `Test-Orchestrator.ps1` | The backfill script's own logic: tokens, dates, progress, stopping and resuming. |
| `Test-SqlIntegration.ps1` | Everything against a real SQL Server: the engine's bulk loads, every SQL script, the installer and an upgrade, the reporting tables, the dashboard's rollups, the loaders, schema compatibility, and from 2.6.3 the merging of an agent's identities. |
| `Test-SchemaCompatibility.ps1`, `SchemaCompatibility.ps1` | The rules that keep the schema backward compatible, and the baseline file they are checked against. |
| `Test-SqlScriptOrder.ps1` | That no script adds a column before it creates the table. |
| `Test-Provisioning.ps1` | The setup scripts' logic against a simulated tenant: the app registration, the pipeline's steps, the job server's settings. |
| `Test-UserDirectory.ps1` | The directory load's rules: the management chain, the filters, the country names. |
| `Test-CsvImport.ps1` | How a Purview CSV export is parsed and mapped. |
| `Test-ExternalUsage.ps1` | The GitHub Copilot and Foundry loaders, without calling either service. |
| `Test-DataHealth.ps1`, `Test-NightlyProfile.ps1`, `Test-Precache.ps1` | That the health check, the nightly profile and the pre-cache say the right thing and only read. |
| `Test-PowerBiModel.ps1` | That the Power BI project is what the generator writes, with its security and refresh policy. |
| `Test-OperationsGuide.ps1`, `Test-ReportingReference.ps1`, `Test-Spelling.ps1` | That the guides are complete and match the scripts, in American spelling. |
| `Test-Workflows.ps1` | How the release is built: least-privilege tokens, pinned actions, the same tests as CI. |
| `TestHelpers.ps1`, `TestDoubles.cs`, `mock_graph.py` | Shared by the tests: helpers, in-memory stand-ins for SQL, and the stand-in audit search service. |
| `dashboard\CopilotInsights.Tests` | The dashboard's own unit tests (`dotnet test`). |
