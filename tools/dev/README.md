# Development tools

These tools are for whoever builds a release. They are not deployed and nothing in production runs them.

| Tool | What it does |
|---|---|
| `ui/ui_audit.py` | Opens every dashboard page in headless Edge at 1440 and 1024 pixels wide and reports layout faults: text that is clipped or spills out of its box, chart labels that overlap or fall outside the chart, text cut short with an ellipsis and no hover text, links and buttons with no space between them, overlapping boxes, chart text outside 9–16px, and horizontal page scroll. It writes `ui/ui-audit/report.json` and a screenshot of each page that has an issue. It exits with code 1 if it finds anything. |
| `ui/Test-FigureLinks.ps1` | Opens the page behind each headline figure that is a link and checks the same number appears on it, as written or as its digits. A few figures lead on purpose to a related page in another month or unit; they are listed in the script and reported as related, not failed. It exits with code 1 if a figure is missing from its page. Run it at more than one period: `pwsh tools/dev/ui/Test-FigureLinks.ps1 -Url http://localhost:5391 -Days 30,90,365`. `-ActAs <persona PublicId>` runs it as a Workbench test persona (Development, `Testing:AllowActAs`), for the figures that open Workbench pages. |
| `schema/extract.ps1`, `schema/scripts.py`, `schema/build.py`, `schema/build_md.py` | Regenerate [docs/SCHEMA.md](../../docs/SCHEMA.md). They combine an installed database's real columns, types, keys and indexes with the column comments in `sql/` and the descriptions in `build.py`. `build.py` also writes an HTML version to `schema/out/`. |

| `schema/build_docx.py` | Writes the Word versions of the two reporting guides, `REPORTING.docx` and `REPORTING-REFERENCE.docx`, from their Markdown in a folder given (the release workflow's), or `docs/`. Needs `python-docx`. |
| `schema/Build-ReportingReference.ps1` | Writes the full reporting reference to `-OutFile`: the guide in [docs/REPORTING-REFERENCE.md](../../docs/REPORTING-REFERENCE.md), the column reference copied from `docs/SCHEMA.md`, and the full text of the scripts that create and fill the schema. The release workflow runs it and attaches the result. `-Check` writes nothing and exits 1 when the committed guide carries generated text. |

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

## Schema compatibility

From 2.6.3 a release may add to the schema and may not take away from it. A report, a Grafana panel or an older dashboard written against one release keeps working against the next, and an install over a database in use loses nothing. [tests/SchemaCompatibility.ps1](../../tests/SchemaCompatibility.ps1) has the rules:

- no table, view, procedure, function, column, parameter or grant to a role goes away, and none changes kind;
- a column or parameter keeps its type or widens it (a longer string, a larger integer, a decimal with as many digits on both sides of the point);
- a table's column that took NULL still does, and a new one takes NULL, has a default or fills itself;
- a view's columns keep their places, and new ones go after them;
- a parameter keeps its place and direction, and a new one has a default.

[tests/schema-baseline.tsv](../../tests/schema-baseline.tsv) is the shape of the last release: both databases, installed with the dashboard, the first load's tables and a reporting account. Three things hold a change to it:

1. `tests/Test-SchemaCompatibility.ps1` proves the rules without a server, and fails when the baseline is not of the release `CHANGELOG.md` names last.
2. Section 20 of `tests/Test-SqlIntegration.ps1` installs the checkout and compares it with the baseline, and with the baseline the last release's tag carries, so a baseline rewritten to fit a break does not pass.
3. The same section installs 2.6.2 and the last release from their tags, installs the checkout over each, and fails unless the databases end with the schema a new install has. This is what catches a column added to a `CREATE TABLE` and never to the tables already there. It needs the release tags: CI checks out the whole history for it, and a checkout without tags skips it and says so.

When a change breaks the baseline, change the schema, not the baseline: keep what was there, add the new thing beside it, and mark the old one as replaced in `docs/SCHEMA.md`.

**When cutting a release,** take the baseline again once the changelog names the release, and commit it:

```powershell
pwsh tools/dev/schema/Update-SchemaBaseline.ps1 -SqlServer localhost
```

It installs into two databases of its own and drops them. It writes nothing when the checkout breaks the baseline in place, and lists what broke. `-Check` compares without writing. Until the release has its tag, section 20 also fails unless the baseline is, line for line, what the release installs.

What it does not see: the meaning of a column whose name and type stay the same; indexes and keys; `audit.CopilotAudit` as `sql/Finalize-CopilotAudit.sql` builds it after a backfill (the baseline has it as the first nightly load or CSV import makes it); and the two tables left over from 1.x.
## Reporting reference

[docs/REPORTING.md](../../docs/REPORTING.md) and [docs/REPORTING-REFERENCE.md](../../docs/REPORTING-REFERENCE.md) (the reference's Part A, its guide) are written by hand, for a team building its own reports. The rest of the reference is generated when a release is made: the release workflow writes the full reference and both Word guides and attaches them to the release (2.7.6; before, the 18,000 generated lines were committed). To read it before a release:

```powershell
pwsh tools/dev/schema/Build-ReportingReference.ps1 -OutFile tools/dev/schema/out/REPORTING-REFERENCE.md
```

1. **Commit `docs/SCHEMA.md` when the schema changed:** the column reference is copied from it. The repository's reference keeps its generated parts' marks empty; `tests/Test-ReportingReference.ps1` fails when they are not, and when the full reference cannot be made.
2. **When a script is new in `sql/`,** add it to the list at the top of the tool, with a line on what it is for. The tool stops and names a script that is missing.
3. **When a view is new in `bi`,** describe it under its own heading in the reference's section 6 and name it in the brief guide. The test names a view that is in neither.
4. **The Word versions are made by the release workflow**, from the same folder as the full reference. To look at them first: copy `docs/REPORTING.md` beside the full reference, then `pip install python-docx` and `python tools/dev/schema/build_docx.py tools/dev/schema/out`. Nothing in CI reads a Word file, so open them after a release.
5. **Run the guides' queries again when a `bi` view changed.** Every `sql` block of both guides ran against the demo database as a member of `CopilotReporting`, with `$__timeFrom()`, `$__timeTo()` and `'$tz'` replaced by fixed values. CI has no database to do this.

## Release checklist

Work through every step, in this order, before tagging.

1. **Version.** Update `CHANGELOG.md` with the version, date and a summary. If the release needs a fresh install rather than an upgrade, say so at the top of the entry.
   - Then take the schema baseline again: `pwsh tools/dev/schema/Update-SchemaBaseline.ps1 -SqlServer localhost` (see [Schema compatibility](#schema-compatibility)). If it refuses, the release breaks the schema: fix that first.
2. **Unit and script tests:**
   - `dotnet test dashboard/CopilotInsights.Tests`;
   - the test scripts `tests/Test-Engine.ps1`, `Test-Orchestrator.ps1`, `Test-UserDirectory.ps1`, `Test-DataHealth.ps1`, `Test-Precache.ps1`, `Test-Provisioning.ps1`, `Test-ExternalUsage.ps1`, `Test-DropGuard.ps1`, `Test-SchemaCompatibility.ps1`, `Test-OperationsGuide.ps1`, `Test-Spelling.ps1`, `Test-SqlScriptOrder.ps1`, `Test-PowerBiModel.ps1`, `Test-Workflows.ps1`, `Test-CsvImport.ps1`, `Test-NightlyProfile.ps1` and `Test-ReportingReference.ps1`.
3. **SQL integration.** Run `tests/Test-SqlIntegration.ps1` against a local SQL Server, with `SQLIT_SERVER` and `SQLIT_INTEGRATED=1` set.
4. **Analyzer.** `Invoke-ScriptAnalyzer -Path . -Recurse -Settings ./PSScriptAnalyzerSettings.psd1` must report nothing, because CI fails on any finding.
5. **Demo and UI.**
   1. Rebuild the demo database.
   2. Run the dashboard.
   3. Run `ui/ui_audit.py` until it reports 0 issues.
   4. Run `ui/Test-FigureLinks.ps1` at 30, 90 and 365 days until no figure is missing from its page. With the
      Governance Workbench set up, run it again as a test persona who sees every line of business, so the seat
      figures that open Workbench pages are checked too: `-Pages /leadership/reservations,/licensing/reservation-holders -ActAs <persona PublicId>`.
   5. Look at the screenshots of any page whose layout changed.
6. **Power BI.**
   1. Run `python powerbi/generate_model.py`.
   2. Open the project in Power BI Desktop and refresh it against the demo database.
   3. Check that its measures match the dashboard's figures.
7. **Schema reference.** Regenerate `docs/SCHEMA.md` as described above whenever a table, view or column changed. Then run `schema/Build-ReportingReference.ps1`, whenever `docs/SCHEMA.md` or a script changed, and `schema/build_docx.py` after it.
8. **Guides.** Update every guide the release affects:
   - `docs/OPERATIONS.md`, for every new Admin page or task (`Test-OperationsGuide.ps1` checks the pages);
   - `docs/DASHBOARD.md`;
   - `docs/DEPLOYMENT.md`, and `docs/GITHUB-COPILOT-USAGE.md` and `docs/FOUNDRY-USAGE.md` when a loader's parameters, settings or permissions changed;
   - `docs/REPORTING.md` and Part A of `docs/REPORTING-REFERENCE.md`, when a `bi` view, a figure's definition or the reporting role changed;
   - `README.md`;
   - `RUNBOOK.md`.
9. **Release.**
   1. Open the pull request and wait for CI to pass.
   2. Squash-merge it.
   3. Tag `vX.Y.Z` on `main`. The release workflow builds and publishes the zips and their `.sha256` files.
   4. Check that every asset is attached to the release.
