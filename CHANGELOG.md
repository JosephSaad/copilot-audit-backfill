# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project
adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

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

[1.1.1]: https://github.com/JosephSaad/copilot-audit-backfill/releases/tag/v1.1.1
[1.1.0]: https://github.com/JosephSaad/copilot-audit-backfill/releases/tag/v1.1.0
[1.0.0]: https://github.com/JosephSaad/copilot-audit-backfill/releases/tag/v1.0.0
