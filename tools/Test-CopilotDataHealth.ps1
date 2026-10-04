<#
.SYNOPSIS
    Read-only health check of the Copilot audit databases (release 2.5.0): what has been
    loaded, from where, what is missing or behind, and what to run.

.DESCRIPTION
    Runs SELECT statements only. It changes nothing. It reads uncommitted data, so it takes no
    row or page locks and does not wait for the nightly task's writes, and it is safe to run
    beside the task. A query still holds a schema-stability lock while it runs, so one on a
    table the task is truncating or rebuilding waits until the task finishes (up to
    -TimeoutSeconds), and it can delay such a truncate by the length of the query. It checks:

      1. Coverage of the Copilot database: the ranges CSV imports and API loads covered, and
         any gap between them; days in bi with no interactions.
      2. The refresh chain: loads and imports still owed a refresh, flattening, sessions,
         bi and the dashboard rollups, each against the data end.
      3. The license-event database: which operations it is loaded with, license events and
         Copilot Studio's agent records (makers), and whether they were shredded.
      4. Agents: names, makers, and owners (owners are set by hand only).
      5. The directory and the nightly runs.

    Each finding ends with OK, WARN or ACTION, and the script closes with the commands to run,
    in order. It says every check passed only when none warned and every section ran to the
    end. Run it from the scripts folder: the commands it gives are relative to it.

.EXAMPLE
    .\tools\Test-CopilotDataHealth.ps1 -SqlServer SQLPROD01

.EXAMPLE
    .\tools\Test-CopilotDataHealth.ps1 -SqlServer SQLPROD01 -Database CopilotAudit -LicenseDatabase CopilotLicenseEvents -TrustServerCertificate
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $SqlServer,
    [string] $Database = 'CopilotAudit',
    [string] $LicenseDatabase = 'CopilotLicenseEvents',
    [switch] $TrustServerCertificate,
    [int]    $TimeoutSeconds = 900
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:Actions = New-Object System.Collections.Generic.List[string]
$script:Warnings = 0
$script:NotRun = 0
$script:DataEnd = $null
$script:botSince = $null
# Every installer finding comes to the same one run, which does all of them (upgrade, shred, flatten, agent ids).
$script:Install = ".\Install-CopilotAudit.ps1 -SqlServer $SqlServer -Dashboard -LicenseEventDatabases $LicenseDatabase, with the nightly task disabled. It brings both databases to this release where they aren't, shreds the license and Copilot Studio records not shredded yet (and again those a shred that was stopped part way was shredding), works out the license SKU changes still owed, flattens any record never flattened and reads the agent ids, as the findings above need. Add -ReportingAccount <gateway account> on the first 2.4.12 run."

function New-Conn([string] $Db) {
    $b = New-Object System.Data.SqlClient.SqlConnectionStringBuilder
    $b['Data Source'] = $SqlServer
    $b['Initial Catalog'] = $Db
    $b['Integrated Security'] = $true
    $b['Application Name'] = 'Copilot data health check (read only)'
    $b['ApplicationIntent'] = 'ReadOnly'
    if ($TrustServerCertificate) { $b['TrustServerCertificate'] = $true }
    $c = New-Object System.Data.SqlClient.SqlConnection $b.ConnectionString
    $c.Open()
    $c
}

function Get-Table($Conn, [string] $Sql) {
    $cmd = $Conn.CreateCommand()
    $cmd.CommandTimeout = $TimeoutSeconds
    # Read committed snapshot is not assumed: read uncommitted keeps a check from waiting on the nightly run's row and page locks.
    $cmd.CommandText = "SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;`n" + $Sql
    $t = New-Object System.Data.DataTable
    $r = $cmd.ExecuteReader()
    try { $t.Load($r) } finally { $r.Dispose() }
    # Rows as plain objects, a database NULL as $null, so an empty table counts as 0 and prints as (none).
    $rows = @(foreach ($row in $t.Rows) {
        $o = [ordered]@{}
        foreach ($c in $t.Columns) { $v = $row[$c]; $o[$c.ColumnName] = $(if ($v -is [DBNull]) { $null } else { $v }) }
        [pscustomobject]$o
    })
    [pscustomobject]@{ Rows = $rows }
}

function Get-Value($Conn, [string] $Sql) {
    $t = Get-Table $Conn $Sql
    if ($t.Rows.Count -eq 0) { return $null }
    @($t.Rows[0].PSObject.Properties)[0].Value
}

function Test-Object($Conn, [string] $Name) {
    [bool] (Get-Value $Conn "SELECT CASE WHEN OBJECT_ID(N'$Name') IS NULL THEN 0 ELSE 1 END")
}

function Write-Head([string] $Text) { Write-Host ''; Write-Host "== $Text ==" -ForegroundColor Cyan }
function Write-Ok([string] $Text) { Write-Host "  OK      $Text" -ForegroundColor Green }
function Write-Info([string] $Text) { Write-Host "          $Text" }
function Write-Warn([string] $Text) { $script:Warnings++; Write-Host "  WARN    $Text" -ForegroundColor Yellow }
function Write-Act([string] $Text, [string] $Command) {
    Write-Host "  ACTION  $Text" -ForegroundColor Red
    if ($Command -and -not $script:Actions.Contains($Command)) { $script:Actions.Add($Command) }
}
function Fmt($v) { if ($null -eq $v) { '(none)' } elseif ($v -is [datetime]) { $v.ToString('yyyy-MM-dd HH:mm') + 'Z' } else { "$v" } }
function Age([object] $Utc) {
    if ($null -eq $Utc) { return [double]::PositiveInfinity }
    ([datetime]::UtcNow - [datetime] $Utc).TotalHours
}

function Invoke-Section([string] $Title, [scriptblock] $Body) {
    Write-Head $Title
    # Dot-sourced, so what a section finds is there for the next; a query that fails ends its section only.
    try { . $Body } catch { $script:NotRun++; Write-Warn ("This section stopped: {0}" -f $_.Exception.Message) }
}

function Test-Column($Conn, [string] $Table, [string] $Column) {
    [bool] (Get-Value $Conn "SELECT CASE WHEN COL_LENGTH(N'$Table', N'$Column') IS NULL THEN 0 ELSE 1 END")
}

# Merges [From, To] intervals and returns the gaps between them longer than $MinHours.
function Get-CoverageGap($Intervals, [double] $MinHours) {
    $sorted = @($Intervals | Where-Object { $_.From -and $_.To } | Sort-Object From)
    $gaps = @()
    if ($sorted.Count -eq 0) { return , $gaps }
    $end = $sorted[0].To
    foreach ($i in $sorted | Select-Object -Skip 1) {
        if (($i.From - $end).TotalHours -gt $MinHours) { $gaps += [pscustomobject]@{ From = $end; To = $i.From } }
        if ($i.To -gt $end) { $end = $i.To }
    }
    , $gaps
}

Write-Host "Copilot data health check: $SqlServer / $Database and $LicenseDatabase, $([datetime]::UtcNow.ToString('yyyy-MM-dd HH:mm'))Z. Read only."

$cp = New-Conn $Database
$lic = $null
try { $lic = New-Conn $LicenseDatabase } catch { $script:NotRun++; Write-Warn "Cannot open $LicenseDatabase ($($_.Exception.Message)); its checks are skipped." }

# ---------------------------------------------------------------------------------------------
Invoke-Section "1. Coverage of $Database" {
$range = Get-Table $cp 'SELECT RangeStartUtc, RangeEndUtc, Operations, CompletedUtc FROM audit.CopilotBackfillRange WHERE Id = 1'
if ($range.Rows.Count) {
    $r = $range.Rows[0]
    Write-Info ("Graph backfill: {0} -> {1}, operations {2}, completed {3}" -f (Fmt $r.RangeStartUtc), (Fmt $r.RangeEndUtc), $r.Operations, (Fmt ($(if (($null -eq $r.CompletedUtc)) { $null } else { $r.CompletedUtc }))))
} else { Write-Info 'No Graph backfill recorded (the history came from CSV exports).' }

$intervals = @()
if (Test-Object $cp 'audit.CopilotCsvFile') {
    $files = Get-Table $cp @'
SELECT f.EarliestRecordUtc AS [From], f.LatestRecordUtc AS [To], f.RowsStaged
FROM audit.CopilotCsvFile AS f JOIN audit.CopilotCsvImport AS i ON i.ImportId = f.ImportId
WHERE i.MergedUtc IS NOT NULL AND f.EarliestRecordUtc IS NOT NULL;
'@
    $imp = Get-Table $cp @'
SELECT COUNT(*) AS Imports, SUM(FilesLoaded) AS Files, SUM(RowsAdded) AS RowsAdded, SUM(RowsRejected) AS Rejected,
       MIN(EarliestAddedUtc) AS FirstRec, MAX(LatestAddedUtc) AS LastRec, MAX(CoveredToUtc) AS CoveredTo,
       SUM(CASE WHEN MergedUtc IS NULL THEN 1 ELSE 0 END) AS Unmerged
FROM audit.CopilotCsvImport;
'@
    $x = $imp.Rows[0]
    Write-Info ("CSV imports: {0} import(s), {1} file(s), {2:N0} rows added, {3:N0} rejected; records {4} -> {5}; covered through {6}" -f
        [int]$x.Imports, [int]$x.Files, [long]$x.RowsAdded, [long]$x.Rejected, (Fmt $x.FirstRec), (Fmt $x.LastRec), (Fmt ($(if (($null -eq $x.CoveredTo)) { $null } else { $x.CoveredTo }))))
    if ([int]$x.Rejected -gt 0) { Write-Warn "$($x.Rejected) CSV row(s) were rejected: audit.CopilotCsvReject lists them with their line numbers." }
    if ([int]$x.Unmerged -gt 0) { Write-Act "$($x.Unmerged) CSV import(s) were never merged (stopped part way)." "Run the same Import-CopilotAuditCsv.ps1 command again: files already loaded are skipped, and an unmerged import is finished." }
    foreach ($f in $files.Rows) { $intervals += [pscustomobject]@{ From = [datetime]$f.From; To = [datetime]$f.To; Src = 'CSV' } }
}
$loads = Get-Table $cp 'SELECT RangeStartUtc AS [From], RangeEndUtc AS [To] FROM audit.CopilotLoad WHERE MergedUtc IS NOT NULL'
foreach ($l in $loads.Rows) { $intervals += [pscustomobject]@{ From = [datetime]$l.From; To = [datetime]$l.To; Src = 'API' } }
if ($range.Rows.Count -and -not (($null -eq $range.Rows[0].CompletedUtc))) {
    $intervals += [pscustomobject]@{ From = [datetime]$range.Rows[0].RangeStartUtc; To = [datetime]$range.Rows[0].RangeEndUtc; Src = 'Backfill' }
}
$loadSum = Get-Table $cp 'SELECT COUNT(*) AS Loads, MIN(RangeStartUtc) AS F, MAX(RangeEndUtc) AS T, SUM(RowsAdded) AS Added FROM audit.CopilotLoad WHERE MergedUtc IS NOT NULL'
$ls = $loadSum.Rows[0]
Write-Info ("API loads (-Incremental): {0} merged, {1} -> {2}, {3:N0} rows added" -f $ls.Loads,
    (Fmt ($(if (($null -eq $ls.F)) { $null } else { $ls.F }))), (Fmt ($(if (($null -eq $ls.T)) { $null } else { $ls.T }))), [long]$ls.Added)

$gaps = Get-CoverageGap $intervals 12
if ($intervals.Count -eq 0) {
    $flatRows = [long](Get-Value $cp "SELECT SUM(p.row_count) FROM sys.dm_db_partition_stats AS p WHERE p.object_id = OBJECT_ID(N'audit.RawLogFlattened') AND p.index_id IN (0, 1)")
    if ($flatRows -gt 0) { Write-Warn ("No CSV import or API load is recorded, though {0:N0} flattened rows exist: they were loaded another way (a release before 2.4, or demo data), so coverage can't be checked here." -f $flatRows) }
    else { Write-Act 'Nothing has been loaded into the Copilot database.' "Load the history: a Purview CSV import (RUNBOOK section 5e) or .\Invoke-CopilotPipeline.ps1 -History." }
}
elseif ($gaps.Count -eq 0) {
    $first = ($intervals | Sort-Object From | Select-Object -First 1).From
    $last = ($intervals | Sort-Object To -Descending | Select-Object -First 1).To
    Write-Ok ("Covered without a gap over 12 hours from {0} to {1}." -f (Fmt $first), (Fmt $last))
    $script:DataEnd = $last
} else {
    $script:DataEnd = ($intervals | Sort-Object To -Descending | Select-Object -First 1).To
    foreach ($g in $gaps) {
        $days = [math]::Round(($g.To - $g.From).TotalDays, 1)
        Write-Act ("No CSV file or API load covers {0} -> {1} ({2} days)." -f (Fmt $g.From), (Fmt $g.To), $days) `
            ("Export Copilot interactions for each gap from Purview (Audit > Search, activity 'Interacted with Copilot') and import them: .\tools\Import-CopilotAuditCsv.ps1 -Path <folder> -SqlServer $SqlServer -Database $Database -SessionLicenseDatabase $LicenseDatabase. A gap within the last 180 days can instead be loaded from Graph into a separate database; a CSV import is simpler.")
    }
}
$ageEnd = Age $script:DataEnd
if ($null -eq $script:DataEnd) { }
elseif ($ageEnd -gt 36) { Write-Act ("The data ends {0}, {1:N0} hours ago." -f (Fmt $script:DataEnd), $ageEnd) "Check the nightly task and Admin > Health: Get-ScheduledTaskInfo -TaskName 'Copilot audit - nightly'; then Start-ScheduledTask -TaskName 'Copilot audit - nightly'." }
elseif ($null -ne $script:DataEnd) { Write-Ok ("Data ends {0} ({1:N0} hours ago)." -f (Fmt $script:DataEnd), $ageEnd) }

if (Test-Object $cp 'bi.FactInteraction') {
    $days = Get-Table $cp 'SELECT DateKey, COUNT_BIG(*) AS N FROM bi.FactInteraction GROUP BY DateKey ORDER BY DateKey'
    if ($days.Rows.Count -gt 1) {
        $map = @{}; foreach ($d in $days.Rows) { $map[[int]$d.DateKey] = [long]$d.N }
        $firstDay = [datetime]::ParseExact([string]$days.Rows[0].DateKey, 'yyyyMMdd', $null)
        $lastDay = [datetime]::ParseExact([string]$days.Rows[$days.Rows.Count - 1].DateKey, 'yyyyMMdd', $null)
        $weekday = @($days.Rows | Where-Object { ([datetime]::ParseExact([string]$_.DateKey, 'yyyyMMdd', $null)).DayOfWeek -notin 'Saturday', 'Sunday' } | ForEach-Object { [long]$_.N } | Sort-Object)
        $median = if ($weekday.Count) { $weekday[[int]($weekday.Count / 2)] } else { 0 }
        $empty = @(); $low = @()
        for ($d = $firstDay; $d -le $lastDay; $d = $d.AddDays(1)) {
            $k = [int]$d.ToString('yyyyMMdd')
            $weekend = $d.DayOfWeek -in 'Saturday', 'Sunday'
            if (-not $map.ContainsKey($k)) { $empty += $d.ToString('yyyy-MM-dd') }
            elseif (-not $weekend -and $median -gt 0 -and $map[$k] -lt 0.1 * $median) { $low += ('{0} ({1:N0})' -f $d.ToString('yyyy-MM-dd'), $map[$k]) }
        }
        Write-Info ("bi.FactInteraction: {0} -> {1}, a median weekday of {2:N0} interactions." -f $firstDay.ToString('yyyy-MM-dd'), $lastDay.ToString('yyyy-MM-dd'), $median)
        if ($empty.Count -and $median -lt 100) { Write-Info ("{0} day(s) with no interaction; normal at {1:N0} interactions a weekday." -f $empty.Count, $median) }
        elseif ($empty.Count) { Write-Act ("{0} day(s) with no interaction at all: {1}" -f $empty.Count, (($empty | Select-Object -First 15) -join ', ')) "Days with no interaction in bi: if section 1 shows no gap for them, rebuild bi (.\tools\Invoke-CopilotMaintenance.ps1 -SqlServer $SqlServer -Database $Database -Task RebuildBi); if it does, fill the gap first." }
        else { Write-Ok 'Every day in the range has interactions.' }
        if ($low.Count) { Write-Warn ("{0} weekday(s) under a tenth of the median, a possible partial export or load: {1}" -f $low.Count, (($low | Select-Object -First 10) -join ', ')) }
    }
}

}
# ---------------------------------------------------------------------------------------------
Invoke-Section '2. The refresh chain (flatten, sessions, bi, dashboard)' {
if (-not (Test-Column $cp 'audit.CopilotLoad' 'DerivedRefreshedUtc')) {
    Write-Act "$Database is not upgraded to 2.4.12 yet (audit.CopilotLoad has no DerivedRefreshedUtc)." $script:Install
    $owedLoads = 0
} else { $owedLoads = [int](Get-Value $cp 'SELECT COUNT(*) FROM audit.CopilotLoad WHERE MergedUtc IS NOT NULL AND DerivedRefreshedUtc IS NULL') }
$pending = [int](Get-Value $cp 'SELECT COUNT(*) FROM audit.CopilotLoad WHERE MergedUtc IS NULL')
$owedCsv = 0
if ((Test-Object $cp 'audit.CopilotCsvImport') -and (Test-Column $cp 'audit.CopilotCsvImport' 'DerivedRefreshedUtc')) { $owedCsv = [int](Get-Value $cp 'SELECT COUNT(*) FROM audit.CopilotCsvImport WHERE MergedUtc IS NOT NULL AND DerivedRefreshedUtc IS NULL') }
if ($pending) { Write-Warn "$pending load(s) downloaded but not merged: the next -Incremental run (the nightly task) merges them first." }
if ($owedLoads + $owedCsv) {
    Write-Act "$owedLoads load(s) and $owedCsv CSV import(s) are merged but still owed their refresh of the flattened table, sessions and bi." `
        "Run the nightly task once, which makes owed refreshes up: Start-ScheduledTask -TaskName 'Copilot audit - nightly'. For a CSV import, running the same import command again with no new files does it too."
} else { Write-Ok 'No load or CSV import is owed a refresh.' }

if (-not (Test-Object $cp 'audit.PipelineSetting')) { Write-Act 'audit.PipelineSetting is missing: the database predates 2.4.' $script:Install; return }
$marks = Get-Table $cp "SELECT Name, Value FROM audit.PipelineSetting WHERE Name IN (N'FinalizedUtc', N'FullFlattenUtc', N'AgentNamesReadUtc', N'AgentIdsReadUtc', N'AgentIdsReadUtcCursor')"
$m = @{}; foreach ($row in $marks.Rows) { $m[[string]$row.Name] = [string]$row.Value }
foreach ($n in 'FullFlattenUtc', 'AgentNamesReadUtc', 'AgentIdsReadUtc') { Write-Info ("{0,-18} {1}" -f $n, $(if ($m.ContainsKey($n)) { $m[$n] } else { '(not set)' })) }

$counts = Get-Table $cp @'
SELECT (SELECT SUM(p.row_count) FROM sys.dm_db_partition_stats AS p WHERE p.object_id = OBJECT_ID(N'audit.CopilotAudit') AND p.index_id IN (0, 1)) AS Records,
       (SELECT SUM(p.row_count) FROM sys.dm_db_partition_stats AS p WHERE p.object_id = OBJECT_ID(N'audit.RawLogFlattened') AND p.index_id IN (0, 1)) AS Flattened;
'@
$rec = [long]$counts.Rows[0].Records; $flat = [long]$counts.Rows[0].Flattened
Write-Info ("audit.CopilotAudit {0:N0} records; audit.RawLogFlattened {1:N0} rows." -f $rec, $flat)
if ($rec -eq 0) {
    Write-Info 'No stored records (their JSON) in audit.CopilotAudit: nothing to flatten or read agent ids from, and a rebuild can only rebuild from the flattened rows.'
} elseif (-not $m.ContainsKey('FullFlattenUtc')) {
    Write-Act 'No record that every record was flattened once (FullFlattenUtc).' $script:Install
} elseif ($rec -gt 0 -and ($rec - $flat) -gt [math]::Max(1000, $rec * 0.001)) {
    Write-Warn ("{0:N0} more records than flattened rows (an estimate from the row counts). A record can flatten to nothing, but a large difference means records were left out." -f ($rec - $flat))
    $script:Actions.Add("If the flattened table is behind: .\tools\Invoke-CopilotMaintenance.ps1 -SqlServer $SqlServer -Database $Database -Task Reflatten (with the nightly task disabled; it takes hours at 40 million records).")
} else { Write-Ok 'Flattened, and the row counts agree.' }

if ($rec -eq 0) { }
elseif ($m.ContainsKey('AgentIdsReadUtcCursor')) {
    Write-Act 'The one-off read of agent ids stopped part way.' $script:Install
} elseif (-not $m.ContainsKey('AgentIdsReadUtc')) {
    Write-Act 'The 2.4.12 one-off read of agent ids has not run, so some agents used in Copilot Chat have no id or name.' $script:Install
} else { Write-Ok 'Agent ids and names have been read from the records already loaded.' }

$sess = Get-Table $cp 'SELECT TOP (1) RunEndUtc, LicenseEventDatabase, SessionsWritten FROM audit.CopilotSessionRun ORDER BY RunId DESC'
if ($sess.Rows.Count) {
    $s = $sess.Rows[0]
    Write-Info ("Sessions last built {0}, {1:N0} written, license databases '{2}'." -f (Fmt $s.RunEndUtc), $s.SessionsWritten, $s.LicenseEventDatabase)
    if (($null -eq $s.LicenseEventDatabase) -or [string]::IsNullOrEmpty([string]$s.LicenseEventDatabase)) {
        Write-Act 'Sessions were built without a license database, so "licensed at the time of use" falls back to today''s licenses.' "Rebuild the sessions with the license history: .\tools\Invoke-CopilotMaintenance.ps1 -SqlServer $SqlServer -Database $Database -Task RebuildSessions -LicenseEventDatabases $LicenseDatabase"
    }
} else { Write-Act 'Sessions have never been built.' "Run the nightly task, or: .\tools\Invoke-CopilotMaintenance.ps1 -SqlServer $SqlServer -Database $Database -Task RebuildSessions -LicenseEventDatabases $LicenseDatabase" }

$bi = Get-Table $cp 'SELECT TOP (1) RunId, StartedUtc, EndedUtc, SinceUtc, LicenseEventDatabase, InteractionRows FROM bi.RefreshRun ORDER BY RunId DESC'
$rebuilding = 0
if (Test-Object $cp 'bi.RebuildInProgress') { $rebuilding = [int](Get-Value $cp 'SELECT COUNT(*) FROM bi.RebuildInProgress') }
$biMax = Get-Value $cp 'SELECT MAX(CreatedUtc) FROM bi.FactInteraction'
if ($rebuilding) { Write-Act 'A bi rebuild started and did not finish (bi.RebuildInProgress has a row): pages may be incomplete.' "Finish the bi rebuild: .\tools\Invoke-CopilotMaintenance.ps1 -SqlServer $SqlServer -Database $Database -Task RebuildBi" }
if ($bi.Rows.Count) {
    $b = $bi.Rows[0]
    Write-Info ("bi last refreshed {0} ({1}), license databases '{2}', latest interaction {3}." -f (Fmt $b.EndedUtc), $(if (($null -eq $b.SinceUtc)) { 'in full' } else { 'from ' + (Fmt $b.SinceUtc) }), $b.LicenseEventDatabase, (Fmt $biMax))
    if (($null -eq $b.EndedUtc)) { Write-Warn 'The latest bi refresh has no end time: it is running now, or it stopped.' }
    if ($null -ne $script:DataEnd -and $null -ne $biMax -and ($script:DataEnd - [datetime]$biMax).TotalHours -gt 48) {
        Write-Act ("bi ends {0}, {1:N0} hours before the loaded data." -f (Fmt $biMax), ($script:DataEnd - [datetime]$biMax).TotalHours) "Bring bi up to date: Start-ScheduledTask -TaskName 'Copilot audit - nightly' (owed refreshes are made up), or .\tools\Invoke-CopilotMaintenance.ps1 -SqlServer $SqlServer -Database $Database -Task RebuildBi"
    } else { Write-Ok 'bi is up to date with the loaded data.' }
} else { Write-Act 'bi has never been refreshed.' $script:Install }

if (Test-Object $cp 'dash.RefreshRun') {
    $dr = Get-Table $cp 'SELECT TOP (1) EndedUtc, BiRunId, People FROM dash.RefreshRun ORDER BY RunId DESC'
    if ($dr.Rows.Count) {
        $d = $dr.Rows[0]
        Write-Info ("Dashboard rollups last refreshed {0}, from bi run {1}, {2:N0} people." -f (Fmt $d.EndedUtc), $d.BiRunId, $d.People)
        if ($bi.Rows.Count -and -not (($null -eq $d.BiRunId)) -and [int]$d.BiRunId -lt [int]$bi.Rows[0].RunId) {
            Write-Act 'The dashboard rollups are behind bi.' "Refresh them: .\tools\Invoke-CopilotMaintenance.ps1 -SqlServer $SqlServer -Database $Database -Task RefreshDashboard"
        } else { Write-Ok 'The dashboard rollups are up to date with bi.' }
    } else { Write-Act 'The dashboard rollups were never built.' ".\tools\Invoke-CopilotMaintenance.ps1 -SqlServer $SqlServer -Database $Database -Task RefreshDashboard" }
}

}
# ---------------------------------------------------------------------------------------------
Invoke-Section "3. License-event database $LicenseDatabase" {
$script:botSince = $null
if ($lic) {
    if (-not (Test-Object $lic 'audit.LicenseEvent')) {
        Write-Act "$LicenseDatabase has no audit.LicenseEvent: it is not a license-event database, or the installer has not run on it." "Check -LicenseDatabase names the database the nightly loads license events into (LicenseDatabase in copilot-audit.settings.psd1), then run the check again."
        return
    }
    $lr = Get-Table $lic 'SELECT RangeStartUtc, RangeEndUtc, Operations, CompletedUtc FROM audit.CopilotBackfillRange WHERE Id = 1'
    $recorded = ''
    if ($lr.Rows.Count) {
        $recorded = [string]$lr.Rows[0].Operations
        Write-Info ("Graph backfill: {0} -> {1}, completed {2}." -f (Fmt $lr.Rows[0].RangeStartUtc), (Fmt $lr.Rows[0].RangeEndUtc), (Fmt ($(if (($null -eq $lr.Rows[0].CompletedUtc)) { $null } else { $lr.Rows[0].CompletedUtc }))))
        $n = @($recorded -split ',').Count
        Write-Info "Operations recorded: $n."
        if ($recorded -notmatch 'BotCreate') {
            Write-Act 'The database is loaded with the license operations only: Copilot Studio''s authoring records (who built and published each agent) are not fetched.' `
                "Run the nightly task once, from release 2.4.12 or later (Start-ScheduledTask -TaskName 'Copilot audit - nightly'): its license step passes all 18 operations, and the database takes the larger list from then on. Do not run -Incremental by hand with the 4 license operations: a database holding the 18 refuses fewer, and one holding 4 stays without the agents' records."
        } else { Write-Ok 'Loaded with the license and Copilot Studio operations (the pipeline''s 18).' }
    } else { Write-Info 'No Graph backfill recorded here.' }

    $last = Get-Table $lic 'SELECT TOP (1) RangeEndUtc, MergedUtc, Operations FROM audit.CopilotLoad WHERE MergedUtc IS NOT NULL ORDER BY LoadId DESC'
    if ($last.Rows.Count) {
        Write-Info ("Last -Incremental load merged {0}, up to {1}." -f (Fmt $last.Rows[0].MergedUtc), (Fmt $last.Rows[0].RangeEndUtc))
        if ((Age $last.Rows[0].MergedUtc) -gt 36) { Write-Act 'No license-event load in the last 36 hours.' "Check the nightly task's last run in Admin > Health: the license step runs before the Copilot step." }
        else { Write-Ok 'Loaded within the last 36 hours.' }
    }
    if (Test-Object $lic 'audit.CopilotCsvImport') {
        $li = Get-Table $lic 'SELECT COUNT(*) AS Imports, MIN(EarliestAddedUtc) AS F, MAX(LatestAddedUtc) AS T, MAX(Operations) AS Ops FROM audit.CopilotCsvImport WHERE MergedUtc IS NOT NULL'
        if ([int]$li.Rows[0].Imports -gt 0) { Write-Info ("CSV imports: {0}, records {1} -> {2}." -f $li.Rows[0].Imports, (Fmt $li.Rows[0].F), (Fmt $li.Rows[0].T)) }
    }

    $raw = Get-Table $lic @"
SELECT k.Kind, COUNT_BIG(*) AS Records, MIN(a.CreatedDateTime) AS F, MAX(a.CreatedDateTime) AS T
FROM audit.CopilotAudit AS a
-- Entra records an operation with a trailing period ('Update user.'): audit.usp_ShredLicenseEvents strips it the same way.
CROSS APPLY (SELECT RTRIM(REPLACE(a.Operation, N'.', N'')) AS Op) AS n
CROSS APPLY (SELECT CASE WHEN a.Operation LIKE N'Bot%' THEN 'Copilot Studio'
                         WHEN n.Op IN (N'Change user license', N'Update user', N'Add user', N'Delete user') THEN 'License'
                         ELSE 'Other' END AS Kind) AS k
GROUP BY k.Kind;
"@
    $rawBot = 0; $rawLic = 0
    foreach ($x in $raw.Rows) {
        Write-Info ("Raw {0} records: {1:N0}, {2} -> {3}." -f $x.Kind, $x.Records, (Fmt $x.F), (Fmt $x.T))
        if ($x.Kind -eq 'Other') { Write-Warn ("{0:N0} record(s) of other operations: is -LicenseDatabase the license-event database?" -f $x.Records) }
        if ($x.Kind -eq 'Copilot Studio') { $rawBot = [long]$x.Records; $script:botSince = [datetime]$x.F }
        elseif ($x.Kind -eq 'License') { $rawLic = [long]$x.Records }
    }
    $le = Get-Table $lic 'SELECT COUNT_BIG(*) AS N, MIN(CreatedDateTime) AS F, MAX(CreatedDateTime) AS T FROM audit.LicenseEvent'
    Write-Info ("audit.LicenseEvent: {0:N0} events, {1} -> {2}." -f $le.Rows[0].N, (Fmt ($(if (($null -eq $le.Rows[0].F)) { $null } else { $le.Rows[0].F }))), (Fmt ($(if (($null -eq $le.Rows[0].T)) { $null } else { $le.Rows[0].T }))))
    if ($rawLic -gt 0 -and [long]$le.Rows[0].N -eq 0) { Write-Act 'License records are loaded but none shredded into audit.LicenseEvent.' $script:Install }

    # 2.5.4: a shred marks that it is running, and takes the mark away when it has finished.
    $shredMark = $false
    if (Test-Object $lic 'audit.PipelineSetting') {
        $mark = Get-Table $lic "SELECT UpdatedUtc FROM audit.PipelineSetting WHERE Name = N'LicenseShredInProgress'"
        if ($mark.Rows.Count) {
            $shredMark = $true
            if ((Age $mark.Rows[0].UpdatedUtc) -ge 24) {
                Write-Act ("A shred of the license events began {0} and never finished: events it had begun can lack their properties. Sessions and bi still read the SKU changes of the last shred that finished. The next license load shreds what it was shredding again; the installer does it now." -f (Fmt $mark.Rows[0].UpdatedUtc)) $script:Install
            }
            else { Write-Info ("A shred of the license events began {0} and has not finished: it is running now, or it was stopped part way. The next license load finishes one that was stopped." -f (Fmt $mark.Rows[0].UpdatedUtc)) }
        }
    }

    # 2.5.4: the SKU changes sessions and bi read are kept in a table, worked out at the end of every shred.
    if (-not (Test-Object $lic 'audit.LicenseSkuChangeRun')) {
        Write-Act 'This license database is on a release before 2.5.4: sessions and bi work out every license SKU change from the records each night, which takes long on a large license history.' $script:Install
    }
    elseif ([long]$le.Rows[0].N -gt 0) {
        $sk = (Get-Table $lic @"
SELECT (SELECT COUNT_BIG(*) FROM audit.LicenseSkuChange) AS Kept,
       (SELECT MAX(StartedUtc) FROM audit.LicenseSkuChangeRun) AS LastRun,
       CASE WHEN EXISTS (SELECT 1 FROM audit.LicenseEvent WHERE ShreddedUtc > (SELECT MAX(StartedUtc) FROM audit.LicenseSkuChangeRun)) THEN 1 ELSE 0 END AS Behind,
       CASE WHEN OBJECT_DEFINITION(OBJECT_ID(N'audit.vLicenseSkuChange')) LIKE N'%vLicenseSkuChangeFromRecords%' THEN 1 ELSE 0 END AS FromRecords
"@).Rows[0]
        Write-Info ("audit.LicenseSkuChange: {0:N0} SKU changes kept, last worked out {1}." -f $sk.Kept, (Fmt $sk.LastRun))
        if ($null -eq $sk.LastRun -or [int]$sk.FromRecords -eq 1) {
            Write-Act 'The license SKU changes were never worked out here, so sessions and bi still read them from the records each night.' $script:Install
        }
        elseif ([int]$sk.Behind -eq 1 -and $shredMark) {
            Write-Info 'The events that shred wrote have their SKU changes worked out when it finishes, or by the shred that finishes it.'
        }
        elseif ([int]$sk.Behind -eq 1) {
            Write-Act 'License events were written after the SKU changes were last worked out, and not by a shred, which works them out itself: sessions and bi do not see their changes yet. The next license load works them out; the installer does it now.' $script:Install
        }
        else { Write-Ok 'The SKU changes sessions and bi read are worked out for every event shredded.' }
    }

    if (Test-Object $lic 'audit.AgentEvent') {
        $ae = Get-Table $lic 'SELECT COUNT_BIG(*) AS N, MIN(CreatedUtc) AS F, MAX(CreatedUtc) AS T, COUNT(DISTINCT BotSchemaName) AS Agents, COUNT(DISTINCT ActorUpn) AS Makers FROM audit.AgentEvent'
        $a = $ae.Rows[0]
        Write-Info ("audit.AgentEvent: {0:N0} events, {1} agents, {2} makers, {3} -> {4}." -f $a.N, $a.Agents, $a.Makers, (Fmt ($(if (($null -eq $a.F)) { $null } else { $a.F }))), (Fmt ($(if (($null -eq $a.T)) { $null } else { $a.T }))))
        if ($rawBot -gt [long]$a.N) {
            # A CSV import into the license database shreds license events only; agent records wait for a full shred.
            Write-Act ("{0:N0} Copilot Studio record(s) loaded but not shredded into audit.AgentEvent (a CSV import shreds license events only)." -f ($rawBot - [long]$a.N)) `
                $script:Install
        } elseif ($rawBot -gt 0) { Write-Ok 'Every Copilot Studio record is shredded.' }
    } else { Write-Act 'audit.AgentEvent does not exist in the license database.' $script:Install }
}

}
# ---------------------------------------------------------------------------------------------
Invoke-Section '4. Agents' {
$named = if (Test-Column $cp 'bi.DimAgent' 'NamedInAudit') { 'SUM(CASE WHEN NamedInAudit = 1 THEN 1 ELSE 0 END)' } else { 'CAST(NULL AS INT)' }
$ag = Get-Table $cp "SELECT ISNULL(AgentSource, N'(unknown)') AS Source, COUNT(*) AS Agents, $named AS Named FROM bi.DimAgent GROUP BY AgentSource ORDER BY COUNT(*) DESC"
foreach ($x in $ag.Rows) { Write-Info ("{0}: {1} agent(s), {2} named by the audit log." -f $x.Source, $x.Agents, $(if ($null -eq $x.Named) { 'none' } else { $x.Named })) }
if (-not (Test-Object $cp 'dash.AgentAlias')) { Write-Act 'The dashboard objects are missing (dash.AgentAlias).' $script:Install; return }
$owners = Get-Table $cp 'SELECT (SELECT COUNT(*) FROM bi.DimAgent) AS Agents, COUNT(*) AS Aliased, SUM(CASE WHEN NULLIF(Owner, N'''') IS NOT NULL THEN 1 ELSE 0 END) AS Owned FROM dash.AgentAlias'
$o = $owners.Rows[0]
Write-Info ("Owners set: {0} of {1} agents ({2} have a friendly name)." -f $o.Owned, $o.Agents, $o.Aliased)
if ([int]$o.Owned -lt [int]$o.Agents) {
    Write-Warn 'The Owner column is filled by hand only: no audit record names an agent''s owner. The makers (who created, published and changed it) come from Copilot Studio''s records, section 3.'
    $script:Actions.Add("Set owners by hand, in Admin > Reference data > Agents, or: .\tools\Invoke-CopilotMaintenance.ps1 -SqlServer $SqlServer -Database $Database -Task NameAgent -AgentId '<id in bi.DimAgent>' -FriendlyName '<name>' -Owner '<owner UPN>'. The query at the end of this script lists each agent's last maker, as a starting point.")
}
if (Test-Object $cp 'bi.FactAgentEvent') {
    $mk = Get-Table $cp @"
SELECT COUNT(*) AS Studio, SUM(m.HasMaker) AS WithMaker
FROM bi.DimAgent AS a
CROSS APPLY (SELECT CASE WHEN EXISTS (SELECT 1 FROM bi.FactAgentEvent AS e
                                      WHERE e.BotSchemaName = a.AgentId OR RIGHT(a.AgentId, LEN(e.BotSchemaName) + 1) = N'-' + e.BotSchemaName)
                         THEN 1 ELSE 0 END AS HasMaker) AS m
WHERE a.AgentSource = N'Copilot Studio';
"@
    $k = $mk.Rows[0]
    if ([int]$k.Studio -gt 0) {
        Write-Info ("Copilot Studio agents with a known maker: {0} of {1}." -f $k.WithMaker, $k.Studio)
        if ([int]$k.WithMaker -lt [int]$k.Studio) {
            $since = if ($script:botSince) { ' Authoring records start ' + (Fmt $script:botSince) + ': agents built before then have none.' } else { '' }
            Write-Warn ("{0} Copilot Studio agent(s) used in Copilot but with no authoring record.{1}" -f ([int]$k.Studio - [int]$k.WithMaker), $since)
            $script:Actions.Add("For agents built before the authoring records start: once section 3 shows the 18 operations, export the 14 Copilot Studio operations (BotCreate, BotUpdateOperation-BotPublish and the others in DEPLOYMENT.md step 7) from Purview for that period, and import them into the license database with no -Operations, so it uses the list the database holds: .\tools\Import-CopilotAuditCsv.ps1 -Path <folder> -SqlServer $SqlServer -Database $LicenseDatabase. Then run the installer once to shred them. Purview keeps audit records 180 days (Audit Standard) or a year (Audit Premium), so agents built before that have no record.")
        } else { Write-Ok 'Every Copilot Studio agent has a maker.' }
    }
}

}
# ---------------------------------------------------------------------------------------------
Invoke-Section '5. Directory and nightly runs' {
$noSid = if (Test-Column $cp 'audit.UserDirectory' 'Sid') { 'SUM(CASE WHEN AccountEnabled = 1 AND Sid IS NULL THEN 1 ELSE 0 END)' } else { '0' }
$dir = Get-Table $cp "SELECT COUNT(*) AS People, SUM(CASE WHEN AccountEnabled = 1 THEN 1 ELSE 0 END) AS Enabled, $noSid AS NoSid, MAX(LoadedUtc) AS Loaded FROM audit.UserDirectory"
$dd = $dir.Rows[0]
Write-Info ("audit.UserDirectory: {0:N0} people, {1:N0} enabled, loaded {2}." -f $dd.People, $dd.Enabled, (Fmt ($(if (($null -eq $dd.Loaded)) { $null } else { $dd.Loaded }))))
if ((Age ($(if (($null -eq $dd.Loaded)) { $null } else { $dd.Loaded }))) -gt 36) { Write-Act 'The directory was not loaded in the last 36 hours.' 'Check the nightly task''s directory step in Admin > Health.' } else { Write-Ok 'The directory is current.' }
if ([int]$dd.NoSid -gt 0) { Write-Warn "$($dd.NoSid) enabled people have no SID: from 2.4.12 they cannot sign in to the dashboard (Admin > Directory quality > No SID recorded)." }

if (Test-Object $cp 'audit.PipelineRun') {
    $runs = Get-Table $cp 'SELECT TOP (7) RunId, Kind, StartedUtc, EndedUtc, Status, FailedStep FROM audit.PipelineRun ORDER BY RunId DESC'
    foreach ($x in $runs.Rows) { Write-Info ("Run {0} {1,-11} {2} {3,-9} {4}" -f $x.RunId, $x.Kind, (Fmt $x.StartedUtc), $x.Status, $(if (-not $x.FailedStep) { '' } else { 'failed at: ' + $x.FailedStep })) }
    $bad = @($runs.Rows | Where-Object { $_.Status -ne 'Succeeded' -and $_.Status -ne 'Running' }).Count
    if ($bad) { Write-Warn "$bad of the last $($runs.Rows.Count) runs did not succeed: Admin > Health shows each step's last lines." } else { Write-Ok 'The recent nightly runs succeeded.' }
}

}
# ---------------------------------------------------------------------------------------------
Write-Head 'Recommended, in order'
if ($script:Actions.Count -eq 0) {
    if ($script:NotRun) { Write-Host ("  No command to run, but {0} check(s) did not run to the end (see the warnings above), so this is not an all clear." -f $script:NotRun) -ForegroundColor Yellow }
    elseif ($script:Warnings) { Write-Host ("  No command to run, but {0} warning(s) above need a look." -f $script:Warnings) -ForegroundColor Yellow }
    else { Write-Host '  Nothing to do: every check passed.' -ForegroundColor Green }
}
else {
    Write-Host '  Disable the nightly task first for any installer or maintenance step: Disable-ScheduledTask -TaskName ''Copilot audit - nightly'''
    $i = 1; foreach ($a in $script:Actions) { Write-Host ("  {0}. {1}" -f $i++, $a) }
    Write-Host '  Then enable it again (Enable-ScheduledTask), run this check again, and refresh the Power BI model in full if bi was rebuilt (DEPLOYMENT.md, step 16).'
    if ($script:NotRun) { Write-Host ("  {0} check(s) did not run to the end (see the warnings above), so there may be more to do." -f $script:NotRun) -ForegroundColor Yellow }
}
Write-Host ''
Write-Host "To list each agent's last maker (a starting point for owners), in $Database :"
Write-Host @"
  SELECT a.AgentId, a.AgentName, x.LastMaker, x.LastChangeUtc
  FROM bi.DimAgent AS a
  OUTER APPLY (SELECT TOP (1) e.ActorUpn AS LastMaker, e.EventUtc AS LastChangeUtc FROM bi.FactAgentEvent AS e
               WHERE e.BotSchemaName = a.AgentId OR RIGHT(a.AgentId, LEN(e.BotSchemaName) + 1) = N'-' + e.BotSchemaName
               ORDER BY e.EventUtc DESC) AS x
  WHERE a.AgentSource = N'Copilot Studio' ORDER BY a.AgentName;
"@

$cp.Dispose(); if ($lic) { $lic.Dispose() }
