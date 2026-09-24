# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project
adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

User attributes now come from on-premises Active Directory, and Graph is used
for licences only - one fewer Graph permission, and none for profiles.

### Added

- **Incremental sessions.** `audit.usp_BuildCopilotSessions @SinceUtc` rebuilds
  only from local midnight of that day: a session never crosses midnight,
  so nothing earlier can change. Those sessions are replaced, each stream's
  numbering carries on from its last kept session, and every week containing
  a rebuilt day is recomputed from `audit.CopilotSession` (so a week that
  straddles the day counts its kept sessions). The result equals a full
  rebuild over the same rows, except that kept sessions keep the directory
  attributes and licence flag they were built with. `-Incremental` passes
  the load's start; an incremental build is refused if the last build used
  different settings. `audit.CopilotSessionRun.RebuiltFrom` records the day
  (NULL for a full build).
- **`bi.FactSession` follows the session builds** instead of being rebuilt
  whole on every refresh: the days incremental builds rebuilt, or every
  session after a full build renumbered them (`bi.RefreshRun.SessionRunId`).

### Fixed

- `bi.usp_RefreshBi` scanned all of `audit.RawLogFlattened` on every partial
  refresh: once to find its window (an `@SinceUtc IS NULL OR ...` predicate
  that cannot seek), and again to read it (a plan cached from the first,
  month-sized full build). Both now seek.
- `bi.FactLicensedDay` is built by turning each licence period into its range
  of days, instead of testing every date against every period.

### Performance

Measured on 40M synthetic interactions (70,000 users, 18,000 licensed, a year
of data; 8 cores, 31 GB RAM):

| Nightly step | Before | After |
|---|---|---|
| Sessions and weekly rollup | 1,697 s (full rebuild) | 44-47 s (last 48 h) |
| `bi` 48-hour refresh | 158 s | 27 s |
| Largest single statement's tempdb (sessions) | 11.9 GB (23.6 GB in all) | 1.9 GB |

An incremental build over a full one left all 7,382,589 sessions and
5,545,894 weekly rows identical. The nightly cost now follows the window,
not the history kept. The new indexes on `audit.CopilotSession` take 0.75 GB
at that size.

### Changed (breaking)

- **User attributes come from on-premises AD, not Entra.** The new
  `tools/Export-UsersWithHierarchy-ToSql.ps1` reads every user in a trusted
  domain and replaces `audit.UserDirectory` in one transaction: display
  name, mail, employee type, title, department, division, office, city,
  country, company, manager, whether they manage anyone, whether the account
  is enabled (every account is loaded; there is no enabled filter), and the
  management chain from the top down (`Mgr1`..`Mgr15`, `OrgDepth`).
  Sessions, `bi.DimUser` and the adoption views read it instead of
  `audit.UserProfile`. It binds as the identity running it, needs no Graph
  permission, and runs on Windows PowerShell 5.1 or PowerShell 7. After
  loading it reports how many audit users it matched by UPN, and by mail.
- **`tools/Update-CopilotAuditEnrichment.ps1` reads licences only.** No
  profiles, managers or administrative units, so it no longer writes
  `audit.UserProfile` or `audit.AdministrativeUnit` (left in place on
  existing databases; nothing reads them). Its default scopes are
  `User.Read.All` and `LicenseAssignment.Read.All`; `-SkipProfiles` is
  accepted and ignored.
- **The app registration needs three permissions, not four:**
  `AuditLogsQuery.Read.All`, `User.Read.All`, `LicenseAssignment.Read.All`.
  Remove `Organization.Read.All` and `AdministrativeUnit.Read.All`.
- **`bi.DimUser` (`bi.Users`)** gains `EmployeeType`, `IsManager`,
  `ManagerName`, `OrgDepth` and `OrgLevel1`..`OrgLevel15` - the org chain as a
  ragged hierarchy for the model - and loses Entra's `UserType`. Re-run
  `sql/Create-CopilotBiSchema.sql` to upgrade in place; the procedure refuses
  to run against the older table. `DirectoryStatus` is `Found` for users in
  AD.
- `audit.vCopilotAdoption` returns `Division`, `Office`, `EmployeeType` and
  `AccountEnabled` from AD in place of `OfficeLocation` and `UserType`.

### Tests

- `tests/Test-UserDirectory.ps1` (new, in CI): the chain from the top down,
  a chain through a disabled manager and through a manager of a type not
  loaded, loops and missing managers, the type filter and map, disabled
  accounts loaded and flagged, skipped rows counted, `-MaxLevels`. Runs on
  Windows PowerShell 5.1 and PowerShell 7.
- `Test-SqlIntegration.ps1` section 8: the licence tool calls only
  `/subscribedSkus` and `/users/{upn}` and creates no Entra profile table;
  the AD rows load through the tool's own writer, a second load replaces the
  first, the match report counts UPN and mail matches, and the adoption
  views take departments from AD. Section 12 checks the org chain reaching
  `bi.DimUser`.

## [1.6.0] - 2026-09-24

A star schema for a Power BI semantic model, kept current by every
incremental load.

### Added

- **The `bi` schema: a star schema for a Power BI semantic model.**
  `sql/Create-CopilotBiSchema.sql` and `sql/Create-CopilotBiProcedure.sql`
  add four facts - interactions, sessions, licence changes, and licensed
  user-days for the adoption denominator - and their dimensions (date with
  the Saturday-Friday week, user, app with its category, agent, model, SKU,
  licence status), with integer keys that stay stable across refreshes. A
  model imports the views (`bi.Interactions`, `bi.Users`, `bi.Calendar` ...),
  which leave out lineage; the columns that are one value per row (RecordId,
  ThreadId, resources, URLs, labels, JSON) are left in `audit.*`.
  - `bi.usp_RefreshBi` rebuilds the interactions from `@SinceUtc`, a month at
    a time, and the smaller facts whole. `-Incremental` calls it after the
    other derived tables, with the load's start.
  - Each interaction carries the licence held at the time: from the licence
    history (`@LicenceEventDatabase`, the same rule as sessions), else from
    the enrichment's `LicensedSinceUtc`, else Unknown.
  - `ActivityHour`, local time to the hour, is the column for the model's
    incremental refresh.
  - A partial refresh in a different reporting time zone from the last one is
    refused; it would mix zones in one table.
  - The agent dimension reads `CopilotEventData.AgentId` when a record has
    one, and otherwise the agent named in `AppIdentity`
    (`Copilot.Studio.<env>-<tenant>-<schema name>`, `MicrosoftAgent.<...>`).
    On a live tenant AgentId was empty on every row, Copilot Studio agents
    included, so without this every agent interaction would have read
    "(no agent)". `AgentSource` says which it came from.

### Tests

- `Test-SqlIntegration.ps1` section 12 builds the schema over sections 8-10's
  data: every key resolves; licence at the time of use from history, from
  `LicensedSinceUtc`, and Unknown; local dates and hours either side of
  midnight; the calendar's week; sessions, licence changes and licensed days;
  a partial refresh rewrites its window only and keeps keys stable; a full
  rebuild re-registers nothing; and the time-zone and licence-database
  refusals. It also covers agents named only in `AppIdentity` (Copilot Studio
  and Microsoft), an `AgentId` taking precedence, and the schema script
  upgrading an older `bi.DimAgent` in place - the path a fresh test database
  never takes, which failed on the first live install.

## [1.5.0] - 2026-09-24

Incremental loads: a finalized database kept current on a schedule, so a
semantic model refreshing daily has something new to refresh.

### Added

- **`-Incremental`: keep a finalized database current on a schedule.** The
  backfill covers one fixed range, and finalize rebuilds everything from it;
  nothing kept the tables current after that, so a semantic model refreshing
  daily had nothing new to refresh. Each `-Incremental` run loads from where
  the data ends - the last load's end, or the backfill's - up to the start of
  the current hour, and appends instead of rebuilding.
  - Every load re-searches the `-LookbackHours` (default 48) before its start.
    Microsoft commits to no delivery time for audit records (core services
    typically 60-90 minutes), so a record that arrives late is picked up by the
    next load. Records already present are counted, never duplicated.
  - The engine runs unchanged against its own `audit.CopilotLoadWindow` and
    `audit.CopilotLoadStage`; `audit.CopilotLoad` logs each load. A run that
    stops part-way resumes the same load next time, and a run with failed
    windows exits non-zero rather than leaving a silent hole.
  - `audit.usp_MergeCopilotLoad` (`sql/Create-CopilotLoadProcedure.sql`,
    installed automatically) checks the load covers its range with no gap, then
    appends the new records to `audit.CopilotAudit` (with a `LoadId` for
    lineage, added in place on older databases) and `audit.CopilotInteraction`
    in one transaction, and empties the load tables.
  - Afterwards it refreshes whichever derived tables are installed: the
    flattened table and licence events from the load's start, then sessions
    (`-SessionLicenceDatabase` passes the licence history). `-SkipPostProcessing`
    stops after the merge.
- **App-only sign-in** with `-ClientId` and `-CertificateThumbprint`, so a
  scheduled task can run with nobody to sign in. Needs the Microsoft Graph
  application permission `AuditLogsQuery.Read.All`.
- RUNBOOK section 5b: the app registration and the scheduled task, with every
  permission one app registration needs to run all of it. All are Microsoft
  Graph application permissions, admin-consented and read-only:

  | Permission | Needed for |
  |---|---|
  | `AuditLogsQuery.Read.All` | The backfill and `-Incremental` loads (Copilot records and licence events). Enough on its own without the enrichment tool. |
  | `User.Read.All` | Enrichment: profiles, Division, Copilot licences, managers. |
  | `Organization.Read.All` | Enrichment: finding the Copilot SKUs (`LicenseAssignment.Read.All` also works). |
  | `AdministrativeUnit.Read.All` | Enrichment: administrative-unit names. |

  Sign-in is by certificate. The app needs no Purview audit role; that applies
  to people signing in.

### Changed

- `-Finalize` refuses once an incremental load has been merged: it rebuilds from
  the backfill's staging table only, and would drop every day the loads added.
- `-Status` lists the latest incremental loads.

### Tests

- `Test-SqlIntegration.ps1` section 11 runs the real engine: a backfill of five
  days, finalized, then a load over four more with a day of lookback. Every
  record ends up present exactly once, the lookback day is counted as already
  loaded, a record removed to simulate late delivery is added back by the next
  load and nothing else is, merging with nothing waiting changes nothing, and
  finalize, an unfinished load and a load with a coverage gap are all refused.
- `Test-Orchestrator.ps1` section 2b covers the load range: the lookback, the
  whole-hour end, an explicit end, and nothing to load.

## [1.4.0] - 2026-09-24

Sessions and the weekly rollup in SQL, licence history that carries its
detail, and a planner that sizes windows from what it actually loads.

### Added

- **Inferred sessions and the Saturday-Friday weekly rollup, in SQL.** A port
  of the session script that read the Purview CSV export (v5.7.5).
  `sql/Create-CopilotSessionSchema.sql` and `sql/Create-CopilotSessionProcedure.sql`
  add `audit.usp_BuildCopilotSessions`, which rebuilds `audit.CopilotSession`
  (one row per session) and `audit.CopilotWeeklyRollup` (per week, user,
  primary app, category and licence) from `audit.RawLogFlattened` in one
  transaction. `audit.vCopilotSessionSummary` and `audit.vCopilotWeeklyRollup`
  carry the old CSVs' exact column names and formats.
  - The script's rules: a new session after 60 minutes idle or on a new
    calendar day in the reporting time zone; primary app and licence from the
    first prompt; weeks run Saturday to Friday, labelled by the Friday, week 1
    ending on the year's first Friday; a session counts in the week of its
    last prompt.
  - Two settings where the Graph data differs from the CSV. The script's
    'Copilot Chat' test matched the portal export's App label; the Graph data
    has host names, so `audit.SessionAppCategory` maps them (`Office`,
    `OfficeCopilotSearchAnswer`, `BizChat`, `Edge`) and is editable. And the
    script keyed sessions on app context, falling back to Location, skipping
    rows with neither: on a live tenant that skipped every Copilot Chat,
    Outlook and Teams row, 507 of 1,145. The procedure keys on the user by
    default; `@ContextFallback = 'Location'` reproduces the script exactly.
    `audit.CopilotSessionRun` logs each build's rule and every skipped row.
  - `@LicenceEventDatabase` (one or several, comma-separated) sets each
    session's licence flag to the licence held when it began, from the
    licence-event history, rather than today's. On the live tenant this
    changed the flag on 65 of 240 sessions.
  - 240 sessions and 128 weekly groups from 1,145 records in 0.7 s.
- `tools/Export-CopilotSessionCsv.ps1` writes `copilot_session_summary.csv` and
  `weekly_rollup.csv` from those views, quoted and UTF-8 as before, streamed
  from SQL and renamed into place when complete. `-Build` rebuilds first.
- `audit.vLicenseSkuChange` diffs each licence change's old and new
  `AssignedLicense` lists into one row per SKU added or removed, which is the
  question the licence-event feature exists to answer: when was this user
  given Copilot, and when was it taken away.
- **Division** in `audit.UserProfile`, from Entra's `employeeOrgData.division`,
  and in the session outputs. Existing databases are altered in place.
### Fixed
- **Windows now grow on a fresh run when the service reports no record count.**
  The planner learned density only from `approximateReturnedRecordCount` on the
  search status, and a live tenant returned none for any search, Copilot or
  Entra. Density stayed unknown, every window was planned at
  `InitialWindowHours`, and a 180-day licence-event backfill submitted one search
  per day, heading for the submission budget. The 1.2.0 empty-window fix looked
  verified live only because that run had been restarted: a resume reads the last
  loaded window's `RowsLoaded`, which a fresh run never did. A loaded window now
  feeds its actual row count back to the planner whenever the status carried no
  approximate count. The write happens on a SQL writer task, so it is volatile.

- **Licence changes now carry their detail: most of them arrive chunked.**
  1.3.0 assumed a detail-free `Change user license` had an `Update user`
  sibling holding the licence lists. On a live tenant (1,042 directory records
  over 180 days) that was not the shape: 400 of 422 `Change user license`
  records had no detail, and none was recovered by pairing. When a change is
  too large for one record - anyone with a few SKUs, because `AssignedPlan`
  lists every service plan - Entra splits it across up to seven records, each
  with empty `ModifiedProperties` and an `additionalDetails` of
  `{"id","seq","b"}`. The `b` fragments, joined in `seq` order, are one JSON
  document whose `targetUpdatedProperties` holds the change. 775 of the 1,042
  records were fragments.
  - `audit.usp_ShredLicenseEvents` keeps the fragments in a new
    `audit.LicenseEventChunk` table (a group can span shredder batches),
    reassembles every complete group after the batches, and files its
    properties against the `seq = 1` record with `Source = 'chunks'`. A group
    missing a fragment is reported and kept for a later run, never parsed from
    a partial document. On the live data: 152 of 152 groups, 760 properties.
  - Fragments are 10,000 characters, so they are read with
    `OPENJSON ... WITH (... NVARCHAR(MAX))`: `JSON_VALUE` returns NULL past
    4,000.
- **`audit.vLicenseChange` returns licence changes only.** It returned every
  directory event - 998 of its 1,042 rows had no licence detail, most of them
  `Update user` events for things like Authenticator devices. It now returns
  one row per licence property per change: each chunk group once, on its
  `seq = 1` record; every licence property rather than `TOP (1)`; and the
  `Update user` that Entra raises alongside each licence change, with the same
  detail and `InterSystemsId`, folded into the `Change user license`. Failed
  assignments (`MethodExecutionResult` only) are no longer reported as
  changes. The live tenant's 1,042 rows become 98 changes. Pairing is kept as a
  fallback and never borrows from the record's own chunk group.
- **`audit.vLicenseLifecycle` counts a change once.** It counted every
  fragment, so one user showed 24 licence changes rather than 4. It also
  matches `Add user` and `Delete user` exactly, because the service's operation
  filter for `Add user` returns `Add user sponsor` too, which is not an account
  being created.


### Tests

- `mock_graph.py` gains `report_approx_count` (default on) to withhold
  `approximateReturnedRecordCount`. `Test-Engine.ps1` section G runs a sparse
  30-day tenant without it: 30 one-day searches before the fix, 16 after, with
  windows reaching 168 h.
- `Test-SqlIntegration.ps1` section 10 puts fixture rows on each session rule's
  edge (59 and 60 minutes, midnight, a Saturday, a row with no context and
  one with no user) and checks sessions, weeks and the run log under both the
  default and the script's rule, plus the licence held at each session's start.
  Section 8 checks Division.
- `Test-SqlIntegration.ps1` section 9b reproduces the live shapes: a change
  chunked into three fragments inserted out of order, its chunked
  `Update user` twin, an Authenticator update, a failed assignment, a group
  missing a fragment and an `Add user sponsor`. Section 9's pairing assertion
  now expects every licence property from the pair, not one.
- The suite runs against a local instance with Windows authentication
  (`SQLIT_INTEGRATED=1`), and its `GO` splitter handles CRLF, so it passes
  from a Windows checkout rather than only on the Linux CI runner.

## [1.3.0] - 2026-09-24

### Added

- **Licence lifecycle from the audit log, not from the directory.** Microsoft
  Graph reports only the *current* licence assignment, so "when was this user
  given Copilot" is answerable and "when was it taken away" is not. The
  unified audit log carries both, as Entra directory events, for 180 days.
  Two new scripts read them:
  - `sql/Create-LicenseEventSchema.sql` — `audit.LicenseEvent` and
    `audit.LicenseEventProperty` for the shredded events, `audit.vLicenseChange`
    for one row per licence change, and `audit.vLicenseLifecycle` for one row
    per user with the provisioning and revocation dates.
  - `sql/Create-LicenseEventProcedure.sql` — `audit.usp_ShredLicenseEvents`,
    batched and MERGE-based like the flatten procedure, so it is safe to run
    incrementally.
- Collect the source records by adding the operations to a backfill run:
  `-Operations 'Change user license','Add user','Delete user','Update user'`.
  `-Operations` was already a passthrough to the API's `operationFilters`; no
  engine change was needed.
- Two details that cost real debugging time, handled in the shredder rather
  than left to whoever queries the tables. Entra writes the same operation
  both with and without a trailing period (`Change user license.`), which is
  normalised away. And a `Change user license` record often carries no licence
  detail at all — its detail sits on a sibling record written in the same
  transaction, so the shredder pairs records on `InterSystemsId` and marks the
  borrowed rows `FromPairedRecord = 1` rather than silently merging them.
- Target resolution prefers the UPN on the event itself and falls back to the
  paired record's, so a rename between the event and the report does not lose
  the user.

### Tests

- `tests/Test-SqlIntegration.ps1` gains section 9: the trailing-period
  normalisation, the licence property on both carrying records, the
  detail-free record picking up its licences from its pair, the new licence
  value being readable, and `vLicenseLifecycle` reporting both provisioning
  and revocation for one user.

## [1.2.0] - 2026-09-24

### Added

- **Directory enrichment**, so adoption can be measured rather than only
  counted. `tools/Update-CopilotAuditEnrichment.ps1` reads Microsoft Graph and
  writes only to its own lookup tables, on its own schedule, and cannot touch
  the audit archive:
  - `audit.CopilotLicense` — who holds a Copilot licence, which SKU, since when
    (`assignedPlans[].assignedDateTime`) and its capability status.
  - `audit.AdministrativeUnit` — administrative unit id to display name, which
    is what makes the GUIDs in `audit.CopilotAudit.AdministrativeUnits` readable.
  - `audit.UserProfile` — department, job title, office, city, country, usage
    location, member or guest, account enabled, manager and creation date.
  - `audit.vCopilotAdoption` — licensed users against actual usage, including
    `NeverUsed` and `DaysToFirstUse`. The shelfware view.
  - `audit.vCopilotUsageByDepartment` — usage per department, day and app.
- It looks up only the users that appear in `audit.CopilotInteraction` rather
  than the whole directory; `-AllLicensedUsers` adds licence holders who have
  never interacted, which the shelfware report needs. `-SkipProfiles` limits it
  to licences and units. App-only sign-in is supported with
  `-ClientId` and `-CertificateThumbprint`, so it can run unattended.
- Two limits are recorded rather than papered over. Graph gives the date the
  *current* licence assignment began, so "licensed since March" is answerable
  and "was this user licensed last March" is not. And audit records outlive
  directory objects, so a departed user is stored with
  `DirectoryStatus = 'NotFound'` and `IsCopilotLic` left NULL — "not known",
  which is a different claim from "unlicensed" and avoids understating
  licensed usage.

### Fixed

- **An empty search window no longer shrinks the next one back to a day.** The
  planner treated a window that returned no records the same as having no
  measurement yet, so every empty window reset the next to
  `InitialWindowHours`. On a sparse tenant, and across every day past the
  180-day retention horizon, a 365-day run submitted one search per day and
  hit the 150-per-24h budget part-way through, pausing for up to a day. An
  empty window now widens the next one, still at most doubling per step and
  capped at `MaxWindowHours`. On a live tenant the windows went
  24 → 48 → 96 → 168 h, so a year needs about 75 searches rather than 365.
- The warning about ranges longer than the 180-day retention printed a literal
  `{0:N0}` instead of the day count.
- **`-ConnectionString` never worked, in any release up to 1.1.1.** It threw
  `Keyword not supported: 'ConnectionString'` before anything ran.
  `SqlConnectionStringBuilder` is a `DbConnectionStringBuilder`, so PowerShell
  routes an assignment to `.ConnectionString` through its keyword indexer
  rather than the CLR property, and the indexer rejects that name. The
  constructor overload is the only form that works. The parameter is optional
  and no test exercised the branch, which is why it survived three releases;
  `Test-Orchestrator` now covers both branches of `New-SqlConnectionString`.

### Changed

- `audit.CopilotLicense.IsCopilotLic` is nullable. It shipped as `BIT NOT NULL`
  from before there was a third answer to record. Existing databases are
  altered in place by `sql/Create-CopilotEnrichmentSchema.sql`.
## [1.1.1] - 2026-09-23

### Changed

- **`audit.usp_FlattenCopilotAudit` is about 8x faster.** Measured on the
  integration suite: 168,000 records in 52 s rather than 413 s, so roughly
  3,200 records/s rather than 407. A 180-day 50M-record backfill goes from about
  34 hours to about 4.

  It used to read twenty-odd fields with twenty-odd `JSON_VALUE` and `OPENJSON`
  calls, and each of those parses the whole document. Each batch now parses in
  stages and keeps the results: one `OPENJSON` over the document for the
  top-level scalars and the `CopilotEventData` fragment, a second over that
  fragment for its scalars and the nested arrays, and `AccessedResources`
  exploded once into a temp table. Everything after that, including the
  `DISTINCT` lists `STRING_AGG` cannot express directly, is ordinary relational
  aggregation over rows that are already parsed.

  Temp tables rather than CTEs on purpose: SQL Server does not materialise a
  CTE, so referencing one repeatedly re-runs the `OPENJSON` underneath it.

  Output is unchanged. The one visible difference is ordering within the
  semicolon-separated lists: the resource name, location and blocked-URL lists
  now share a single ordering, so the Nth entry of each refers to the same
  resource, which the previous per-subquery orderings did not guarantee.

## [1.1.0] - 2026-09-23

### Added

- **Offline module support, for servers with no PowerShell Gallery access.**
  `Invoke-CopilotAuditBackfill.ps1` now puts a `Modules` folder sitting beside it
  at the front of `PSModulePath` at startup. Nothing is installed on the server
  and nothing is written outside the tool's folder; when the folder is absent,
  module resolution is unchanged.
- `tools/Save-CopilotAuditModules.ps1` builds that folder on any machine with
  gallery access: it pins `Microsoft.Graph.Authentication` 2.40.0, verifies the
  saved copy resolves with `PSModulePath` pointing at the bundle alone (so a
  copy already installed on the build machine cannot mask a broken bundle), and
  writes `MODULES-MANIFEST.csv` with a SHA-256 for every file.
- Releases now also carry `copilot-audit-backfill-<version>-offline-win-x64.zip`
  and its checksum, built on a Windows runner by running that same script. It
  includes `NOTICE-BUNDLED-MODULES.txt` recording what is bundled, its version,
  its source and its licence.

### Changed

- The "`Connect-MgGraph` not found" error now points at the offline bundle when
  no `Modules` folder is present, and says the bundle looks incomplete when one is.

`Microsoft.Graph.Authentication` is the only external module; everything else the
tool uses, including `System.Data.SqlClient`, ships inside PowerShell 7.

## [1.0.0] - 2026-09-23

First release.

### Backfill pipeline

- Pulls `CopilotInteraction` audit records for any date range from the Microsoft
  Purview Audit Search Graph API into SQL Server. Audit searches, page downloads
  and SQL loading run concurrently, with no CSV files in between.
- **Adaptive windowing.** Each audit search is sized from observed record density
  toward `-TargetRecordsPerWindow` (default 600,000), well inside the service's
  ~1,000,000-record-per-search cap. A search the service reports as over the cap
  (`isRecordCountLimitExceeded`) is split *before* anything is downloaded; where
  that flag is unavailable, a capped result is detected by row count instead.
- **Stays inside the published limits.** Caps itself at 150 search submissions per
  rolling 24 h against the tenant-wide ~200 (`-MaxSubmissionsPer24h`, counted
  across restarts), 8 searches in flight against the ~50 allowed, and honours
  `Retry-After` on every 429.
- **Adaptive download concurrency.** Starts at 4 streams, adds one after a run of
  clean pages, halves on a 429 and pauses every stream for `Retry-After` (AIMD).
- **Parallel bulk loading.** `SqlBulkCopy` with `TABLOCK` into a heap staging
  table, so several writers take bulk-update locks and load concurrently with
  minimal logging.
- **Resumable.** Every window's state lives in `audit.CopilotBackfillWindow`, so
  Ctrl+C, a crash or a reboot costs only the windows in flight. Searches that
  already completed server-side are reused rather than resubmitted. A window
  re-downloaded after an interruption gets a new attempt number, and finalize
  keeps only each window's final attempt, so a partial load never double-counts.
- **Token handling.** Silent refresh through the Graph SDK, and a clean stop if a
  Conditional Access policy forces a re-sign-in that nobody answers.
- **Compressed at the source.** Each record's JSON is gzipped to the exact bytes
  SQL Server's `COMPRESS()` produces for `NVARCHAR`, so
  `CAST(DECOMPRESS(AuditDataGz) AS NVARCHAR(MAX))` reads it back. Microsoft's
  documented sample records compress 2.4x–7.2x.
- The engine is C# compiled in memory at start — no binaries are shipped.

### SQL

- `sql/Create-CopilotAuditSchema.sql` — the control tables and the heap staging
  table. Idempotent; run automatically on every start.
- `sql/Finalize-CopilotAudit.sql` — refuses to run unless every window is loaded
  with no gaps, then builds `audit.CopilotAudit` (clustered columnstore, boundary
  duplicates removed), the slim `audit.CopilotInteraction` reporting table,
  `audit.vCopilotDailyUsage`, and `audit.vCopilotAuditRecord` in the Purview
  export's column shape. Re-runnable.
- `sql/Create-CopilotFlattenedSchema.sql` — `audit.RawLogFlattened`, one row per
  record with `CopilotEventData` shredded into columns, plus the optional
  `audit.CopilotLicense` lookup that fills `IsCopilotLic`.
- `sql/Create-CopilotFlattenProcedure.sql` — `audit.usp_FlattenCopilotAudit`.
  Shreds accessed resources and their locations, actions, sensitivity labels,
  plugins, contexts, agents, models, and the `JailbreakDetected` and
  `XPIADetected` security flags. Batched and MERGE-based, so it is safe to run
  incrementally on a schedule.
- `sql/Create-CopilotPurgeProcedure.sql` — `audit.usp_PurgeCopilotAuditJson`.
  Retention for the compressed JSON, which is what costs the disk: by default it
  clears `AuditDataGz` past the horizon and leaves the reporting and flattened
  rows intact; `@DeleteRows = 1` removes the records instead. `@WhatIf` defaults
  to 1 and only reports.

### Tests

- `tests/Test-Engine.ps1` — the engine end to end against `tests/mock_graph.py`,
  a mock of the Audit Search API: adaptive windowing, cap detection and splits, a
  server-side search failure, page 429s under a concurrency ceiling, random 503s,
  token rotation, transient SQL failures, a hard stop mid-run and a resume. Proves
  every record lands exactly once in the finalized set.
- `tests/Test-Orchestrator.ps1` — the PowerShell orchestrator's own logic,
  including a real `PipelineStoppedException` in a separate runspace.
- `tests/Test-SqlIntegration.ps1` — the real `SqlWindowStore`, `SqlBatchSink` and
  every SQL script against a real SQL Server: creates the database at
  compatibility level 160, backfills 8 days through the engine, stops and resumes
  it, finalizes, and checks row counts, duplicates, `ISJSON` on every decompressed
  payload, the flattened columns and both purge modes. Also proves finalize is
  re-runnable and that it refuses a range that is not fully loaded.

### CI

- `.github/workflows/ci.yml` — the engine and orchestrator suites on
  windows-latest and ubuntu-latest, PSScriptAnalyzer (failing on errors, reporting
  warnings), and the SQL integration suite against a SQL Server 2022 service
  container.
- `.github/workflows/release.yml` — runs every suite, then publishes the release
  archive and its SHA-256 checksum on a `v*` tag.

[1.6.0]: https://github.com/JosephSaad/copilot-audit-backfill/releases/tag/v1.6.0
[1.5.0]: https://github.com/JosephSaad/copilot-audit-backfill/releases/tag/v1.5.0
[1.4.0]: https://github.com/JosephSaad/copilot-audit-backfill/releases/tag/v1.4.0
[1.3.0]: https://github.com/JosephSaad/copilot-audit-backfill/releases/tag/v1.3.0
[1.2.0]: https://github.com/JosephSaad/copilot-audit-backfill/releases/tag/v1.2.0
[1.1.1]: https://github.com/JosephSaad/copilot-audit-backfill/releases/tag/v1.1.1
[1.1.0]: https://github.com/JosephSaad/copilot-audit-backfill/releases/tag/v1.1.0
[1.0.0]: https://github.com/JosephSaad/copilot-audit-backfill/releases/tag/v1.0.0
