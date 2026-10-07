<#
.SYNOPSIS
    Fills the two generated parts of docs/REPORTING-REFERENCE.md: the column reference, copied from
    docs/SCHEMA.md, and the transcripts of the scripts that create and fill the schema.

.DESCRIPTION
    The guide at the top of docs/REPORTING-REFERENCE.md is written by hand. Two parts of it are not:

      * Between the "generated:columns" marks: every table and view, column by column. It is the body of
        docs/SCHEMA.md (from its first section of objects to its end) with each heading one level lower.
      * Between the "generated:scripts" marks: each script below, whole, in the order the installer runs them.

    Run it after docs/SCHEMA.md or any of the scripts changed, and commit the result. With -Check nothing is
    written: it exits 1 when the committed file is not what this would write, which tests/Test-ReportingReference.ps1
    runs in CI, so the document cannot fall behind the scripts.

    A script added to sql/ must be added to the list here: the run stops and names one that is missing.

.EXAMPLE
    pwsh tools/dev/schema/Build-ReportingReference.ps1
.EXAMPLE
    pwsh tools/dev/schema/Build-ReportingReference.ps1 -Check
#>
#Requires -Version 7.2
[CmdletBinding()]
param([switch] $Check)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..' '..' '..')).Path
$target = Join-Path $root 'docs' 'REPORTING-REFERENCE.md'

# The scripts, in the order they run on a new install, with what each is for.
$scripts = @(
    @{ Path = 'Install-CopilotAudit.ps1';                    Note = 'The installer. Creates both databases, runs the SQL scripts below in order, sets the reporting time zone, grants the reporting accounts their role, and builds everything from the records already loaded.' }
    @{ Path = 'sql/Create-CopilotAuditSchema.sql';           Note = 'Both databases. Loading state and staging, the pipeline''s settings and run log, and the shared function that reads an interaction out of the record JSON.' }
    @{ Path = 'sql/Create-CopilotLoadProcedure.sql';         Note = 'Both databases. `audit.usp_MergeCopilotLoad`, which appends each nightly load to the final records.' }
    @{ Path = 'sql/Create-CopilotFlattenedSchema.sql';       Note = 'Copilot database. The flattened record tables and the value and agent catalogs.' }
    @{ Path = 'sql/Create-CopilotFlattenProcedure.sql';      Note = 'Copilot database. `audit.usp_FlattenCopilotAudit`, which writes every field of a record into columns.' }
    @{ Path = 'sql/Create-CopilotPurgeProcedure.sql';        Note = 'Copilot database. `audit.usp_PurgeCopilotAuditJson`, which clears the stored JSON of old records.' }
    @{ Path = 'sql/Create-CopilotEnrichmentSchema.sql';      Note = 'Copilot database. Current licenses, the user directory and the adoption views.' }
    @{ Path = 'sql/Create-CopilotSessionSchema.sql';         Note = 'Copilot database. App categories, inferred sessions and the weekly rollup.' }
    @{ Path = 'sql/Create-CopilotSessionProcedure.sql';      Note = 'Copilot database. `audit.usp_BuildCopilotSessions`.' }
    @{ Path = 'sql/Create-CopilotBiSchema.sql';              Note = 'Copilot database. The `bi` star schema: dimensions, facts and the views a reporting tool reads.' }
    @{ Path = 'sql/Create-CopilotBiProcedure.sql';           Note = 'Copilot database. `bi.usp_RefreshBi`, which fills the `bi` schema.' }
    @{ Path = 'sql/Create-CopilotDashboardSchema.sql';       Note = 'Copilot database, with `-Dashboard`. The `dash` schema: settings, roles, the org chart, the daily rollups and the agent inventory.' }
    @{ Path = 'sql/Create-CopilotDashboardProcedure.sql';    Note = 'Copilot database, with `-Dashboard`. `dash.usp_RefreshDashboard`, which rebuilds the org chart and the rollups.' }
    @{ Path = 'sql/Create-CopilotOperationsSchema.sql';      Note = 'Copilot database, with `-Dashboard`. The dashboard''s operations tables, its database role and grants, privacy mode and retention, and seven more `bi` views.' }
    @{ Path = 'sql/Create-LicenseEventSchema.sql';           Note = 'License-event database. License events, their properties, the SKU changes and the license views.' }
    @{ Path = 'sql/Create-LicenseEventProcedure.sql';        Note = 'License-event database. `audit.usp_ShredLicenseEvents` and `audit.usp_RefreshLicenseSkuChange`.' }
    @{ Path = 'sql/Create-AgentEventProcedure.sql';          Note = 'License-event database. `audit.AgentEvent` and `audit.usp_ShredAgentEvents`: Copilot Studio authoring records.' }
    @{ Path = 'sql/Finalize-CopilotAudit.sql';               Note = 'Both databases. Run by the backfill''s `-Finalize`, not by the installer: builds `audit.CopilotAudit` and `audit.CopilotInteraction` from staging.' }
    @{ Path = 'sql/Create-CopilotCsvImport.sql';             Note = 'Run by `tools/Import-CopilotAuditCsv.ps1`, not by the installer: the CSV import tables and `audit.usp_MergeCopilotCsv`.' }
    @{ Path = 'Invoke-CopilotPipeline.ps1';                  Note = 'The nightly run and the first load of history: the order in which every table is filled.' }
    @{ Path = 'tools/Import-AgentWorkbook.ps1';              Note = 'Creates and fills the three `dbo` tables of the agent workbook, then `dash.AgentInventory` and `dash.AgentChildLink`.' }
    @{ Path = 'dashboard/Install-CopilotInsights.ps1';       Note = 'The dashboard''s installer on the web server. With `-GrantDatabaseAccess` it runs the three dashboard scripts and adds the site''s account to its database role.' }
)

function Read-Text([string] $Path) { [System.IO.File]::ReadAllText($Path).Replace("`r`n", "`n").TrimStart([char] 0xFEFF) }

# Every script in sql/ is in the list.
$listed = @($scripts | ForEach-Object { $_.Path })
$missing = @(Get-ChildItem -LiteralPath (Join-Path $root 'sql') -Filter '*.sql' | ForEach-Object { "sql/$($_.Name)" } | Where-Object { $_ -notin $listed })
if ($missing.Count) { throw "Not in the script list of Build-ReportingReference.ps1: $($missing -join ', ')." }

# ---- The column reference: the body of docs/SCHEMA.md, one heading level down --------------------
$schema = Read-Text (Join-Path $root 'docs' 'SCHEMA.md')
$from = $schema.IndexOf("`n## Loading and state`n")
if ($from -lt 0) { throw 'docs/SCHEMA.md has no "## Loading and state" section to start the column reference from.' }
$columns = (($schema.Substring($from + 1).TrimEnd() -split "`n") | ForEach-Object { if ($_ -match '^#{2,5} ') { "#$_" } else { $_ } }) -join "`n"

# ---- The transcripts ---------------------------------------------------------------------------
$out = [System.Text.StringBuilder]::new()
$n = 0
foreach ($s in $scripts) {
    $n++
    $text = (Read-Text (Join-Path $root $s.Path)).TrimEnd()
    $lines = ($text -split "`n").Count
    # A fence longer than any run of backticks in the script, so the script cannot close it.
    $longest = 0
    foreach ($m in [regex]::Matches($text, '`+')) { if ($m.Length -gt $longest) { $longest = $m.Length } }
    $fence = '`' * [Math]::Max(3, $longest + 1)
    $lang = if ($s.Path.EndsWith('.sql')) { 'sql' } else { 'powershell' }
    [void] $out.Append("### C.$n ``$($s.Path)```n`n$($s.Note) $lines lines.`n`n$fence$lang`n$text`n$fence`n`n")
}
$transcripts = $out.ToString().TrimEnd()

# ---- Into the document, between its marks --------------------------------------------------------
function Set-Region([string] $Document, [string] $Name, [string] $Body) {
    $start = "<!-- generated:$Name`:start -->"
    $end = "<!-- generated:$Name`:end -->"
    $a = $Document.IndexOf($start); $b = $Document.IndexOf($end)
    if ($a -lt 0 -or $b -lt $a) { throw "docs/REPORTING-REFERENCE.md has no $start ... $end marks." }
    $Document.Substring(0, $a + $start.Length) + "`n`n" + $Body + "`n`n" + $Document.Substring($b)
}

$current = Read-Text $target
$new = Set-Region (Set-Region $current 'columns' $columns) 'scripts' $transcripts

if ($Check) {
    if ($new -ne $current) {
        Write-Host 'docs/REPORTING-REFERENCE.md is behind docs/SCHEMA.md or the scripts. Run tools/dev/schema/Build-ReportingReference.ps1 and commit the result.' -ForegroundColor Red
        exit 1
    }
    Write-Host "docs/REPORTING-REFERENCE.md matches docs/SCHEMA.md and its $($scripts.Count) scripts."
    exit 0
}
[System.IO.File]::WriteAllText($target, $new, [System.Text.UTF8Encoding]::new($false))
Write-Host ("docs/REPORTING-REFERENCE.md: {0:N0} lines, {1:N0} KB, {2} scripts." -f ($new -split "`n").Count, ($new.Length / 1KB), $scripts.Count)
