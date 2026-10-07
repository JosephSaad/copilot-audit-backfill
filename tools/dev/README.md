# Development tools

These tools are for whoever builds a release. They are not deployed and nothing in production runs them.

| Tool | What it does |
|---|---|
| `ui/ui_audit.py` | Opens every dashboard page in headless Edge at 1440 and 1024 pixels wide and reports layout faults: text that is clipped or spills out of its box, chart labels that overlap or fall outside the chart, text cut short with an ellipsis and no hover text, links and buttons with no space between them, overlapping boxes, chart text outside 9–16px, and horizontal page scroll. It writes `ui/ui-audit/report.json` and a screenshot of each page that has an issue. It exits with code 1 if it finds anything. |
| `schema/extract.ps1`, `schema/scripts.py`, `schema/build.py`, `schema/build_md.py` | Regenerate [docs/SCHEMA.md](../../docs/SCHEMA.md). They combine an installed database's real columns, types, keys and indexes with the column comments in `sql/` and the descriptions in `build.py`. `build.py` also writes an HTML version to `schema/out/`. |

| `schema/Build-ReportingReference.ps1` | Fills the two generated parts of [docs/REPORTING-REFERENCE.md](../../docs/REPORTING-REFERENCE.md): the column reference, copied from `docs/SCHEMA.md`, and the full text of the scripts that create and fill the schema. `-Check` writes nothing and exits 1 when the document is behind. |

The first two are also run by hand: CI has no dashboard or Edge to audit, and no installed database to read. The third needs neither, and CI runs its check (`tests/Test-ReportingReference.ps1`).

## UI audit

```bash
pip install websocket-client
python tools/dev/ui/ui_audit.py
```

- **What it needs.** A dashboard running on this machine with the demo data (`dashboard/demo/New-InsightsDemo.ps1`). Your Windows account must be an administrator of it, so that every page opens.
- **Other widths.** Pass them as arguments, for example `python tools/dev/ui/ui_audit.py 1280`.
- **Environment variables:**

| Variable | Default | Use |
|---|---|---|
| `INSIGHTS_URL` | `https://localhost:7291` | The dashboard to audit. |
| `AUDIT_PAGES` | Every page in `Nav.cs` | A comma-separated list of paths to check instead. |
| `AUDIT_SHOT_ALL` | Not set | Set to 1 to screenshot every page, not only those with issues. |
| `EDGE_PATH` | The usual Edge path | Where `msedge.exe` is, if it is somewhere else. |

## Schema reference

The reference describes every table and view, including some that only exist once data is in:

- `audit.CopilotAudit` exists after the first finalize;
- the `audit.CopilotCsv*` tables exist after a CSV import;
- `audit.AdministrativeUnit` and `audit.UserProfile` exist only in a database upgraded from 1.x.

So read it from databases where all of that has happened:

1. Run `tests/Test-SqlIntegration.ps1` on the release. It leaves behind the loaded database `CopilotAuditIntegration`, and `CopilotAuditIntegration_Load`, which has the CSV tables.
2. Do a fresh install for the license-event database: `.\Install-CopilotAudit.ps1 -SqlServer localhost -Database <name> -LicenseDatabase <name> -Dashboard`.
3. Use any database upgraded from 1.x for the two leftover tables.

Then run the steps in order:

```powershell
pwsh -Command "& tools/dev/schema/extract.ps1 -CopilotDatabase CopilotAuditIntegration -LicenseDatabase <license db> -Supplement CopilotAuditIntegration_Load, '<1.x db>=audit.AdministrativeUnit,audit.UserProfile'"
python tools/dev/schema/scripts.py
python tools/dev/schema/build_md.py
```

1. **When a table or view is new,** describe it in `build.py`, in the group it belongs to. `build_md.py` stops and names any installed object without a description, and lists any it describes that the databases don't have.
2. **When a script is new,** add it to the script list in `build.py`.
3. **Do not commit the generated file as it comes.** The committed `docs/SCHEMA.md` carries column notes written by hand, which the scripts' DDL does not have, and the column order of a database upgraded release by release. Compare the two (`git diff docs/SCHEMA.md`), keep the committed file, and carry into it what the release changed: the counts, new sections, new columns, types and indexes.

## Reporting reference

[docs/REPORTING.md](../../docs/REPORTING.md) and the guide at the top of [docs/REPORTING-REFERENCE.md](../../docs/REPORTING-REFERENCE.md) (its Part A) are written by hand, for a team building its own reports. The rest of the reference is generated:

```powershell
pwsh tools/dev/schema/Build-ReportingReference.ps1
```

1. **Run it after `docs/SCHEMA.md` or any script in its list changed**, and commit the result. `tests/Test-ReportingReference.ps1` fails until you do.
2. **When a script is new in `sql/`,** add it to the list at the top of the tool, with a line on what it is for. The tool stops and names a script that is missing.
3. **When a view is new in `bi`,** describe it under its own heading in the reference's section 6 and name it in the brief guide. The test names a view that is in neither.
4. **Run the guides' queries again when a `bi` view changed.** Every `sql` block of both guides ran against the demo database as a member of `CopilotReporting`, with `$__timeFrom()`, `$__timeTo()` and `'$tz'` replaced by fixed values. CI has no database to do this.

## Release checklist

Work through every step, in this order, before tagging.

1. **Version.** Update `CHANGELOG.md` with the version, date and a summary. If the release needs a fresh install rather than an upgrade, say so at the top of the entry.
2. **Unit and script tests:**
   - `dotnet test dashboard/CopilotInsights.Tests`;
   - the test scripts `tests/Test-Engine.ps1`, `Test-Orchestrator.ps1`, `Test-UserDirectory.ps1`, `Test-DataHealth.ps1`, `Test-Precache.ps1`, `Test-Provisioning.ps1`, `Test-OperationsGuide.ps1`, `Test-Spelling.ps1`, `Test-SqlScriptOrder.ps1`, `Test-PowerBiModel.ps1`, `Test-Workflows.ps1`, `Test-CsvImport.ps1`, `Test-NightlyProfile.ps1` and `Test-ReportingReference.ps1`.
3. **SQL integration.** Run `tests/Test-SqlIntegration.ps1` against a local SQL Server, with `SQLIT_SERVER` and `SQLIT_INTEGRATED=1` set.
4. **Analyzer.** `Invoke-ScriptAnalyzer -Path . -Recurse -Settings ./PSScriptAnalyzerSettings.psd1` must report nothing, because CI fails on any finding.
5. **Demo and UI.**
   1. Rebuild the demo database.
   2. Run the dashboard.
   3. Run `ui/ui_audit.py` until it reports 0 issues.
   4. Look at the screenshots of any page whose layout changed.
6. **Power BI.**
   1. Run `python powerbi/generate_model.py`.
   2. Open the project in Power BI Desktop and refresh it against the demo database.
   3. Check that its measures match the dashboard's figures.
7. **Schema reference.** Regenerate `docs/SCHEMA.md` as described above whenever a table, view or column changed. Then run `schema/Build-ReportingReference.ps1`, whenever `docs/SCHEMA.md` or a script changed.
8. **Guides.** Update every guide the release affects:
   - `docs/OPERATIONS.md`, for every new Admin page or task (`Test-OperationsGuide.ps1` checks the pages);
   - `docs/DASHBOARD.md`;
   - `docs/DEPLOYMENT.md`;
   - `docs/REPORTING.md` and Part A of `docs/REPORTING-REFERENCE.md`, when a `bi` view, a figure's definition or the reporting role changed;
   - `README.md`;
   - `RUNBOOK.md`.
9. **Release.**
   1. Open the pull request and wait for CI to pass.
   2. Squash-merge it.
   3. Tag `vX.Y.Z` on `main`. The release workflow builds and publishes the zips and their `.sha256` files.
   4. Check that every asset is attached to the release.
