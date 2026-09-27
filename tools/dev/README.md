# Development tools

These tools are for whoever builds a release. They are not deployed and nothing in production runs them.

| Tool | What it does |
|---|---|
| `ui/ui_audit.py` | Opens every dashboard page in headless Edge at 1440 and 1024 pixels wide and reports layout faults: text that is clipped or spills out of its box, chart labels that overlap or fall outside the chart, text cut short with an ellipsis and no hover text, links and buttons with no space between them, overlapping boxes, chart text outside 9–16px, and horizontal page scroll. It writes `ui/ui-audit/report.json` and a screenshot of each page that has an issue. It exits with code 1 if it finds anything. |
| `schema/extract.ps1`, `schema/scripts.py`, `schema/build.py`, `schema/build_md.py` | Regenerate [docs/SCHEMA.md](../../docs/SCHEMA.md). They combine an installed database's real columns, types, keys and indexes with the column comments in `sql/` and the descriptions in `build.py`. `build.py` also writes an HTML version to `schema/out/`. |

Both are also run by hand: CI has no dashboard or Edge to audit, and no installed database to read.

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

Before you start, do a fresh install of the release to document, with the dashboard's objects included:

```powershell
.\Install-CopilotAudit.ps1 -Dashboard
```

Then run the steps in order:

```powershell
pwsh tools/dev/schema/extract.ps1 -SqlServer localhost -CopilotDatabase CopilotAudit -LicenseDatabase CopilotLicenseEvents
python tools/dev/schema/scripts.py
python tools/dev/schema/build_md.py
```

1. **When a table or view is new,** describe it in `build.py`, in the group it belongs to. `build_md.py` stops and names any object without a description.
2. **When a script is new,** add it to the script list in `build.py`.

## Release checklist

Work through every step, in this order, before tagging.

1. **Version.** Update `CHANGELOG.md` with the version, date and a summary. If the release needs a fresh install rather than an upgrade, say so at the top of the entry.
2. **Unit and script tests:**
   - `dotnet test dashboard/CopilotInsights.Tests`;
   - the test scripts `tests/Test-Engine.ps1`, `Test-Orchestrator.ps1`, `Test-UserDirectory.ps1`, `Test-Provisioning.ps1`, `Test-OperationsGuide.ps1` and `Test-CsvImport.ps1`.
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
7. **Schema reference.** Regenerate `docs/SCHEMA.md` as described above whenever a table, view or column changed.
8. **Guides.** Update every guide the release affects:
   - `docs/OPERATIONS.md`, for every new Admin page or task (`Test-OperationsGuide.ps1` checks the pages);
   - `docs/DASHBOARD.md`;
   - `docs/DEPLOYMENT.md`;
   - `README.md`;
   - `RUNBOOK.md`.
9. **Release.**
   1. Open the pull request and wait for CI to pass.
   2. Squash-merge it.
   3. Tag `vX.Y.Z` on `main`. The release workflow builds and publishes the zips and their `.sha256` files.
   4. Check that every asset is attached to the release.
