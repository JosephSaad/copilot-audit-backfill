<#
.SYNOPSIS
    Shows where a nightly run's time goes: each step, each stage inside the two loads, and the
    statements that ran longest. Read-only.

.DESCRIPTION
    Runs SELECT statements only, and changes nothing. It reads uncommitted data, so it takes no
    row or page locks and does not wait for the nightly task's writes: it is safe to run while the
    task is running, and then it also shows what the task is doing at that moment.

    Everything comes from what each stage already records as it finishes, so a run from last week
    can be read as well as tonight's:

      1. The recent runs and how long each step took (audit.PipelineRun, audit.PipelineRunStep).
      2. One run in detail: inside the two loads, the Purview download, the merge, the flatten,
         the license SKU changes, the sessions, the report tables (bi) and the dashboard totals,
         each from the table that stage writes (audit.CopilotLoad, audit.LicenseSkuChangeRun,
         audit.CopilotSessionRun, bi.RefreshRun, dash.RefreshRun). Time that no table accounts
         for is shown as its own line, so the lines add up to the step.
      3. The statements that took longest in that run's window, from Query Store, when it is on.
      4. What the task is running now, when a run is under way.

    The stage that took longest is named at the end, with what to look at next.

.PARAMETER Runs
    How many recent runs to list (default 7).

.PARAMETER RunId
    The run to show in detail. Default: the last one that finished; a run under way when there is one.

.PARAMETER TopStatements
    How many statements to list from Query Store (default 15; 0 leaves the section out).

.PARAMETER LogPath
    The folder with the nightly logs (LogPath in copilot-audit.settings.psd1). When given, the
    lines the run wrote for its stages are listed with the time between them, which splits what
    the tables cannot: the flatten from the agent and license shreds, and each SQL message.

.EXAMPLE
    .\tools\Measure-CopilotNightly.ps1 -SqlServer SQLPROD01

.EXAMPLE
    .\tools\Measure-CopilotNightly.ps1 -SqlServer SQLPROD01 -RunId 412 -LogPath D:\CopilotAudit\logs -TopStatements 25
#>
[CmdletBinding()]
param(
    [string] $SqlServer,
    [string] $Database = 'CopilotAudit',
    [string] $LicenseDatabase = 'CopilotLicenseEvents',
    [switch] $TrustServerCertificate,
    [ValidateRange(1, 60)][int] $Runs = 7,
    [int] $RunId,
    [ValidateRange(0, 100)][int] $TopStatements = 15,
    [string] $LogPath,
    [int] $TimeoutSeconds = 300
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Format-Span {
    <# A length of time as people say it: 6 h 12 min, 4 min 19 s, 26 s. Empty for nothing. #>
    param([Nullable[double]] $Seconds)
    if ($null -eq $Seconds) { return '' }
    $s = [math]::Max(0, [math]::Round([double] $Seconds))
    if ($s -ge 3600) { return ('{0} h {1} min' -f [math]::Floor($s / 3600), [math]::Floor(($s % 3600) / 60)) }
    if ($s -ge 60) { return ('{0} min {1} s' -f [math]::Floor($s / 60), ($s % 60)) }
    '{0} s' -f $s
}

function New-Stage {
    param([string] $Name, [Nullable[datetime]] $StartUtc, [Nullable[datetime]] $EndUtc, [string] $Detail = '')
    $seconds = if ($null -ne $StartUtc -and $null -ne $EndUtc) { [math]::Max(0, ([datetime] $EndUtc - [datetime] $StartUtc).TotalSeconds) } else { $null }
    [pscustomobject]@{ Stage = $Name; StartUtc = $StartUtc; EndUtc = $EndUtc; Seconds = $seconds; Detail = $Detail }
}

function Get-LoadStage {
    <#
        The stages of one load step, from what each stage recorded. Pure, for the tests.

        A step runs from StepStart to StepEnd (now, for a step still running). Inside it, in order: the
        Purview download (the load's StartedUtc to DownloadedUtc; from the step's start when the load was
        begun by an earlier run and resumed), the merge (to MergedUtc), then whatever the database has of the
        flatten, the sessions, the report tables and the dashboard totals. Time between two stages, before the
        first or after the last is a line of its own, named for what runs there, so the lines add up to the step.
    #>
    param(
        [Parameter(Mandatory)][datetime] $StepStart,
        [Parameter(Mandatory)][datetime] $StepEnd,
        $Load, $Session, $Bi, $Dash,
        # The license step: the run of audit.usp_RefreshLicenseSkuChange that ended its shred (2.5.4).
        $Sku,
        [string] $AfterMerge = 'Flatten, and agent names',
        [string] $Closing = 'Closing: marking the load refreshed'
    )
    $stages = [System.Collections.Generic.List[object]]::new()
    # Nothing recorded in the step's time (nothing new to load): the step's own line says all there is.
    if (-not $Load -and -not $Session -and -not $Bi -and -not $Dash -and -not $Sku) { return }
    $at = $StepStart
    function Add-Gap([string] $Name, [datetime] $To) {
        if (($To - $at).TotalSeconds -ge 1) { $stages.Add((New-Stage $Name $at $To)) }
    }
    if ($Load) {
        $began = [datetime] $Load.StartedUtc
        if ($began -gt $at) { Add-Gap 'Before the download: settings, sign-in, the load''s range' $began; $at = $began }
        if ($null -ne $Load.DownloadedUtc -and [datetime] $Load.DownloadedUtc -ge $at) {
            $stages.Add((New-Stage 'Purview download' $at ([datetime] $Load.DownloadedUtc) ('{0:N0} records staged' -f [long] $Load.RowsStaged)))
            $at = [datetime] $Load.DownloadedUtc
        }
        if ($null -ne $Load.MergedUtc -and [datetime] $Load.MergedUtc -ge $at) {
            $stages.Add((New-Stage 'Merge into audit.CopilotAudit' $at ([datetime] $Load.MergedUtc) ('{0:N0} new, {1:N0} already loaded' -f [long] $Load.RowsAdded, [long] $Load.RowsAlreadyLoaded)))
            $at = [datetime] $Load.MergedUtc
        }
    }
    if ($Sku) {
        Add-Gap $AfterMerge ([datetime] $Sku.StartedUtc)
        $stages.Add((New-Stage 'License SKU changes (audit.usp_RefreshLicenseSkuChange)' ([datetime] $Sku.StartedUtc) ([datetime] $Sku.EndedUtc) ('{0:N0} events worked out, {1:N0} changes kept{2}' -f [long] $Sku.Events, [long] $Sku.Changes, $(if ($null -eq $Sku.SinceUtc) { ': every event' } else { '' }))))
        $at = [datetime] $Sku.EndedUtc
        $AfterMerge = $Closing
    }
    if ($Session) {
        Add-Gap $AfterMerge ([datetime] $Session.RunStartUtc)
        $stages.Add((New-Stage 'Sessions' ([datetime] $Session.RunStartUtc) ([datetime] $Session.RunEndUtc) ('{0:N0} records read, {1:N0} sessions written{2}' -f [long] $Session.RowsRead, [long] $Session.SessionsWritten, $(if ($null -eq $Session.RebuiltFrom) { ': a full build' } else { ', from ' + ([datetime] $Session.RebuiltFrom).ToString('yyyy-MM-dd') }))))
        $at = [datetime] $Session.RunEndUtc
        $AfterMerge = 'Between the sessions and the report tables'
    }
    if ($Bi) {
        Add-Gap $AfterMerge ([datetime] $Bi.StartedUtc)
        $stages.Add((New-Stage 'Report tables (bi.usp_RefreshBi)' ([datetime] $Bi.StartedUtc) ([datetime] $Bi.EndedUtc) ('{0:N0} interactions, {1:N0} sessions, {2:N0} license changes, {3:N0} licensed days written{4}' -f [long] $Bi.InteractionRows, [long] $Bi.SessionRows, [long] $Bi.LicenseChangeRows, [long] $Bi.LicensedDayRows, $(if ($null -eq $Bi.SinceUtc) { ': a full rebuild' } else { '' }))))
        $at = [datetime] $Bi.EndedUtc
        $AfterMerge = 'Between the report tables and the dashboard totals'
    }
    if ($Dash) {
        Add-Gap $AfterMerge ([datetime] $Dash.StartedUtc)
        $stages.Add((New-Stage 'Dashboard totals (dash.usp_RefreshDashboard)' ([datetime] $Dash.StartedUtc) ([datetime] $Dash.EndedUtc) ('{0:N0} person-days written{1}' -f [long] $Dash.UserDayRows, $(if ($null -eq $Dash.FromDateKey) { ': every day' } else { '' }))))
        $at = [datetime] $Dash.EndedUtc
        $AfterMerge = $Closing
    }
    elseif ($Session -or $Bi) { $AfterMerge = $Closing }
    Add-Gap $AfterMerge $StepEnd
    $stages
}

function ConvertFrom-NightlyLog {
    <#
        The lines of a nightly log that carry a time, as (TimeUtc, Text). Pure, for the tests. A step's own lines start
        'yyyy-MM-dd HH:mm:ssZ [INFO ]' (UTC); the pipeline's lines between steps start 'yyyy-MM-ddTHH:mm:ss' in the
        job server's own time, which -UtcOffset turns to UTC.
    #>
    param([string[]] $Line, [timespan] $UtcOffset = [timespan]::Zero)
    foreach ($l in $Line) {
        if ($l -match '^(\d{4}-\d{2}-\d{2}) (\d{2}:\d{2}:\d{2})Z \[(\w+)\s*\] (.*)$') {
            [pscustomobject]@{ TimeUtc = [datetime]::ParseExact("$($Matches[1]) $($Matches[2])", 'yyyy-MM-dd HH:mm:ss', [cultureinfo]::InvariantCulture); Level = $Matches[3]; Text = $Matches[4] }
        }
        elseif ($l -match '^(\d{4}-\d{2}-\d{2})T(\d{2}:\d{2}:\d{2}) (.*)$') {
            [pscustomobject]@{ TimeUtc = [datetime]::ParseExact("$($Matches[1]) $($Matches[2])", 'yyyy-MM-dd HH:mm:ss', [cultureinfo]::InvariantCulture) - $UtcOffset; Level = 'STEP'; Text = $Matches[3] }
        }
    }
}

function Get-LogStage {
    <#
        From a run's log lines, the ones that open a stage, each with the time to the next: where the tables cannot
        split a stage, the log can. Pure, for the tests.
    #>
    param([object[]] $Entry, [datetime] $FromUtc, [datetime] $ToUtc)
    $opens = '^(== |New load |Resuming load |Flattening records|Shredding license events|Shredding agent build|Rebuilding sessions|Rebuilding every session|Refreshing the bi star schema|Refreshing the dashboard rollups|Incremental load .* complete|Nothing to load|The derived tables were not brought up to date|Retention)'
    $marks = @($Entry | Where-Object { $_.TimeUtc -ge $FromUtc.AddSeconds(-1) -and $_.TimeUtc -le $ToUtc.AddSeconds(1) -and $_.Text -match $opens } | Sort-Object TimeUtc)
    for ($i = 0; $i -lt $marks.Count; $i++) {
        $next = if ($i + 1 -lt $marks.Count) { $marks[$i + 1].TimeUtc } else { $ToUtc }
        New-Stage $marks[$i].Text $marks[$i].TimeUtc $next
    }
}

if ($MyInvocation.InvocationName -eq '.') { return }   # dot-sourced by the tests: the functions only

if (-not $SqlServer) { throw 'Give -SqlServer: the SQL Server instance that holds the Copilot database.' }

function New-Conn([string] $Db) {
    $b = New-Object System.Data.SqlClient.SqlConnectionStringBuilder
    $b['Data Source'] = $SqlServer
    $b['Initial Catalog'] = $Db
    $b['Integrated Security'] = $true
    $b['Application Name'] = 'Copilot nightly profile (read only)'
    $b['ApplicationIntent'] = 'ReadOnly'
    if ($TrustServerCertificate) { $b['TrustServerCertificate'] = $true }
    $c = New-Object System.Data.SqlClient.SqlConnection $b.ConnectionString
    $c.Open()
    $c
}

function Get-Row($Conn, [string] $Sql, [hashtable] $Parameters = @{}) {
    $cmd = $Conn.CreateCommand()
    $cmd.CommandTimeout = $TimeoutSeconds
    # Uncommitted reads: nothing here waits on the nightly run's row and page locks.
    $cmd.CommandText = "SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;`n" + $Sql
    foreach ($k in $Parameters.Keys) { [void] $cmd.Parameters.AddWithValue($k, $(if ($null -eq $Parameters[$k]) { [DBNull]::Value } else { $Parameters[$k] })) }
    $t = New-Object System.Data.DataTable
    $r = $cmd.ExecuteReader()
    try { $t.Load($r) } finally { $r.Dispose() }
    , @(foreach ($row in $t.Rows) {
        $o = [ordered]@{}
        foreach ($c in $t.Columns) { $v = $row[$c]; $o[$c.ColumnName] = $(if ($v -is [DBNull]) { $null } else { $v }) }
        [pscustomobject] $o
    })
}

function Test-Object($Conn, [string] $Name) { [bool] (Get-Row $Conn "SELECT CASE WHEN OBJECT_ID(N'$Name') IS NULL THEN 0 ELSE 1 END AS X")[0].X }
function Write-Head([string] $Text) { Write-Host ''; Write-Host "== $Text ==" -ForegroundColor Cyan }
function Fmt($v) { if ($null -eq $v) { '' } else { ([datetime] $v).ToString('yyyy-MM-dd HH:mm:ss') + 'Z' } }
function Write-Stage($Stages, [double] $Whole) {
    foreach ($s in $Stages) {
        $share = if ($Whole -gt 0 -and $null -ne $s.Seconds) { '{0,4:P0}' -f ($s.Seconds / $Whole) } else { '    ' }
        Write-Host ('    {0,-56} {1,12}  {2}  {3}' -f $s.Stage, (Format-Span $s.Seconds), $share, $s.Detail)
    }
}

$cp = New-Conn $Database
$lic = $null
if ($LicenseDatabase) {
    try { $lic = New-Conn $LicenseDatabase } catch { Write-Host "The license database $LicenseDatabase could not be opened ($($_.Exception.Message)): its load is not broken down." -ForegroundColor Yellow }
}
$now = [datetime] (Get-Row $cp 'SELECT SYSUTCDATETIME() AS NowUtc')[0].NowUtc

# ---------------------------------------------------------------------------------------------
Write-Head "1. The last $Runs runs"
if (-not (Test-Object $cp 'audit.PipelineRun')) { throw "$Database has no audit.PipelineRun: the nightly task has not run against it (tools\Install-CopilotJobServer.ps1 sets it up)." }
$runRows = Get-Row $cp "SELECT TOP ($Runs) RunId, Kind, StartedUtc, EndedUtc, Status, FailedStep, Host FROM audit.PipelineRun ORDER BY RunId DESC"
if ($runRows.Count -eq 0) { Write-Host '  No run is recorded yet.'; $cp.Dispose(); if ($lic) { $lic.Dispose() }; return }
$stepRows = Get-Row $cp ("SELECT RunId, StepNo, Name, StartedUtc, EndedUtc, ExitCode FROM audit.PipelineRunStep WHERE RunId >= {0} ORDER BY RunId, StepNo" -f ($runRows | Measure-Object RunId -Minimum).Minimum)
foreach ($r in $runRows) {
    $end = if ($null -eq $r.EndedUtc) { $now } else { [datetime] $r.EndedUtc }
    Write-Host ('  Run {0}  {1,-8} started {2}  {3,-9} {4,12}{5}' -f $r.RunId, $r.Kind, (Fmt $r.StartedUtc), $r.Status, (Format-Span ($end - [datetime] $r.StartedUtc).TotalSeconds), $(if ($r.FailedStep) { "  failed at: $($r.FailedStep)" } else { '' }))
    foreach ($s in @($stepRows | Where-Object RunId -eq $r.RunId)) {
        $se = if ($null -eq $s.EndedUtc) { $end } else { [datetime] $s.EndedUtc }
        Write-Host ('      {0}. {1,-36} {2,12}{3}' -f $s.StepNo, $s.Name, (Format-Span ($se - [datetime] $s.StartedUtc).TotalSeconds), $(if ($null -eq $s.EndedUtc -and $r.Status -eq 'Running') { '  (running)' } elseif ($s.ExitCode) { "  (exit code $($s.ExitCode))" } else { '' }))
    }
}

# ---------------------------------------------------------------------------------------------
$run = if ($RunId) { $runRows | Where-Object RunId -eq $RunId | Select-Object -First 1 } else { $null }
if ($RunId -and -not $run) {
    $run = (Get-Row $cp 'SELECT RunId, Kind, StartedUtc, EndedUtc, Status, FailedStep, Host FROM audit.PipelineRun WHERE RunId = @r' @{ '@r' = $RunId }) | Select-Object -First 1
    if (-not $run) { throw "Run $RunId is not in audit.PipelineRun." }
    $stepRows = Get-Row $cp 'SELECT RunId, StepNo, Name, StartedUtc, EndedUtc, ExitCode FROM audit.PipelineRunStep WHERE RunId = @r ORDER BY StepNo' @{ '@r' = $RunId }
}
if (-not $run) { $run = $runRows | Where-Object Status -eq 'Running' | Select-Object -First 1 }
if (-not $run) { $run = $runRows | Where-Object { $null -ne $_.EndedUtc } | Select-Object -First 1 }
if (-not $run) { $run = $runRows[0] }
$runStart = [datetime] $run.StartedUtc
$runEnd = if ($null -eq $run.EndedUtc) { $now } else { [datetime] $run.EndedUtc }
$whole = ($runEnd - $runStart).TotalSeconds

Write-Head "2. Run $($run.RunId) in detail: $(Fmt $runStart) to $(if ($null -eq $run.EndedUtc) { 'now (still running)' } else { Fmt $runEnd }), $(Format-Span $whole)"
$window = @{ '@s' = $runStart.AddSeconds(-5); '@e' = $runEnd.AddSeconds(5) }
function Get-InWindow($Conn, [string] $Table, [string] $Columns, [string] $StartColumn, [string] $Order) {
    if (-not (Test-Object $Conn $Table)) { return $null }
    (Get-Row $Conn "SELECT TOP (1) $Columns FROM $Table WHERE $StartColumn >= @s AND $StartColumn <= @e ORDER BY $Order DESC" $window) | Select-Object -First 1
}
# A load begun by an earlier run and finished by this one has its merge in the window, not its start.
$loadSql = 'SELECT TOP (1) LoadId, RangeStartUtc, RangeEndUtc, StartedUtc, DownloadedUtc, MergedUtc, RowsStaged, RowsAdded, RowsAlreadyLoaded FROM audit.CopilotLoad
            WHERE (StartedUtc >= @s AND StartedUtc <= @e) OR (MergedUtc >= @s AND MergedUtc <= @e) OR (DownloadedUtc >= @s AND DownloadedUtc <= @e) ORDER BY LoadId DESC'
$load = (Get-Row $cp $loadSql $window) | Select-Object -First 1
$rebuilt = if ((Get-Row $cp "SELECT COL_LENGTH(N'audit.CopilotSessionRun', N'RebuiltFrom') AS L")[0].L) { 'RebuiltFrom' } else { 'CAST(NULL AS DATETIME2(0)) AS RebuiltFrom' }
$session = Get-InWindow $cp 'audit.CopilotSessionRun' "RunId, RunStartUtc, RunEndUtc, RowsRead, SessionsWritten, $rebuilt" 'RunStartUtc' 'RunId'
$bi = Get-InWindow $cp 'bi.RefreshRun' 'RunId, StartedUtc, EndedUtc, SinceUtc, InteractionRows, SessionRows, LicenseChangeRows, LicensedDayRows' 'StartedUtc' 'RunId'
$dash = Get-InWindow $cp 'dash.RefreshRun' 'RunId, StartedUtc, EndedUtc, SinceUtc, FromDateKey, UserDayRows' 'StartedUtc' 'RunId'
$licLoad = if ($lic -and (Test-Object $lic 'audit.CopilotLoad')) { (Get-Row $lic $loadSql $window) | Select-Object -First 1 } else { $null }
$sku = if ($lic) { Get-InWindow $lic 'audit.LicenseSkuChangeRun' 'RunId, StartedUtc, EndedUtc, SinceUtc, Events, Changes' 'StartedUtc' 'RunId' } else { $null }

$all = [System.Collections.Generic.List[object]]::new()
foreach ($s in @($stepRows | Where-Object RunId -eq $run.RunId)) {
    $ss = [datetime] $s.StartedUtc
    $se = if ($null -eq $s.EndedUtc) { $runEnd } else { [datetime] $s.EndedUtc }
    $secs = ($se - $ss).TotalSeconds
    Write-Host ''
    Write-Host ('  {0}. {1}: {2}  ({3:P0} of the run)' -f $s.StepNo, $s.Name, (Format-Span $secs), $(if ($whole -gt 0) { $secs / $whole } else { 0 })) -ForegroundColor White
    $stages = @()
    if ($s.Name -like '*Copilot interactions*') {
        $stages = @(Get-LoadStage -StepStart $ss -StepEnd $se -Load $load -Session $session -Bi $bi -Dash $dash)
    }
    elseif ($s.Name -like '*License events*') {
        $stages = @(Get-LoadStage -StepStart $ss -StepEnd $se -Load $licLoad -Sku $sku -Closing 'Shredding agent events, and closing' `
                        -AfterMerge $(if ($sku) { 'Shredding license events' } else { 'Shredding license and agent events' }))
    }
    if ($stages.Count) { Write-Stage $stages $whole; foreach ($x in $stages) { $all.Add([pscustomobject]@{ Step = $s.Name; Stage = $x.Stage; Seconds = $x.Seconds }) } }
    else { $all.Add([pscustomobject]@{ Step = $s.Name; Stage = $s.Name; Seconds = $secs }) }
}
if (@($stepRows | Where-Object RunId -eq $run.RunId).Count -eq 0) { Write-Host '  No step is recorded for this run.' }

# ---------------------------------------------------------------------------------------------
if ($LogPath) {
    Write-Head "The run's own lines, from its log"
    $offset = [System.TimeZoneInfo]::Local.GetUtcOffset($runStart.ToLocalTime())
    $files = @(Get-ChildItem -LiteralPath $LogPath -Filter 'nightly-*.log' -ErrorAction SilentlyContinue |
               Where-Object { $_.LastWriteTimeUtc -ge $runStart.AddMinutes(-1) -and $_.CreationTimeUtc -le $runEnd.AddDays(1) } | Sort-Object Name)
    if ($files.Count -eq 0) { Write-Host "  No nightly-*.log written during this run is in $LogPath (run this on the job server, or give its log folder)." -ForegroundColor Yellow }
    else {
        $entries = @(ConvertFrom-NightlyLog -Line ($files | ForEach-Object { Get-Content -LiteralPath $_.FullName }) -UtcOffset $offset)
        Write-Stage @(Get-LogStage -Entry $entries -FromUtc $runStart -ToUtc $runEnd) $whole
        Write-Host '    Times between the pipeline''s own "==" lines are in this computer''s time zone: run this on the job server for them to line up.'
    }
}

# ---------------------------------------------------------------------------------------------
if ($TopStatements -gt 0) {
    $qsSql = @"
SELECT TOP ($TopStatements)
       ISNULL(OBJECT_SCHEMA_NAME(q.object_id) + N'.' + OBJECT_NAME(q.object_id), N'(not in a procedure)') AS Module,
       CAST(SUM(rs.avg_duration * rs.count_executions) / 1000000.0 AS DECIMAL(18, 1)) AS Seconds,
       CAST(SUM(rs.avg_cpu_time * rs.count_executions) / 1000000.0 AS DECIMAL(18, 1)) AS CpuSeconds,
       SUM(rs.count_executions) AS Executions,
       CAST(SUM(rs.avg_logical_io_reads * rs.count_executions) AS BIGINT) AS PagesRead,
       CAST(SUM(rs.avg_rowcount * rs.count_executions) AS BIGINT) AS RowsOut,
       MAX(rs.max_dop) AS Dop,
       CAST(MAX(rs.max_query_max_used_memory) * 8 / 1024.0 AS DECIMAL(18, 0)) AS MemoryMb,
       CAST(MAX(rs.max_tempdb_space_used) * 8 / 1024.0 AS DECIMAL(18, 0)) AS TempdbMb,
       q.query_id AS QueryId,
       LEFT(REPLACE(REPLACE(REPLACE(qt.query_sql_text, CHAR(13), N' '), CHAR(10), N' '), CHAR(9), N' '), 220) AS Statement
FROM sys.query_store_runtime_stats AS rs
JOIN sys.query_store_plan AS p ON p.plan_id = rs.plan_id
JOIN sys.query_store_query AS q ON q.query_id = p.query_id
JOIN sys.query_store_query_text AS qt ON qt.query_text_id = q.query_text_id
WHERE rs.last_execution_time >= @s AND rs.first_execution_time <= @e
GROUP BY q.object_id, q.query_id, qt.query_sql_text
ORDER BY SUM(rs.avg_duration * rs.count_executions) DESC;
"@
    # One database can hold both (a small install, the demo): its statements are listed once.
    foreach ($db in @(@{ Name = $Database; Conn = $cp }, @{ Name = $LicenseDatabase; Conn = $(if ($LicenseDatabase -ne $Database) { $lic }) }) | Where-Object { $_.Conn }) {
        Write-Head "3. The statements that took longest in $($db.Name) during the run (Query Store)"
        try {
            $state = (Get-Row $db.Conn 'SELECT actual_state_desc AS State FROM sys.database_query_store_options')[0].State
            if ($state -notin 'READ_WRITE', 'READ_ONLY') {
                Write-Host "  Query Store is $state in $($db.Name), so the statements of a run that has ended are not kept. docs\OPERATIONS.md, ""A slow nightly run"", has the statement that turns it on for the next run." -ForegroundColor Yellow
                continue
            }
            $top = Get-Row $db.Conn $qsSql @{ '@s' = [datetimeoffset]::new($runStart, [timespan]::Zero); '@e' = [datetimeoffset]::new($runEnd, [timespan]::Zero) }
            if ($top.Count -eq 0) { Write-Host '  Query Store has no statement in that window (it was turned on later, or its data was cleared).'; continue }
            Write-Host ('    {0,10} {1,9} {2,6} {3,13} {4,4} {5,9} {6,9}  {7,-34} {8}' -f 'Time', 'CPU', 'Runs', 'Pages read', 'DOP', 'Memory MB', 'Tempdb MB', 'Procedure', 'Statement (query id)')
            foreach ($q in $top) {
                Write-Host ('    {0,10} {1,9} {2,6} {3,13:N0} {4,4} {5,9:N0} {6,9:N0}  {7,-34} {8} ({9})' -f (Format-Span ([double] $q.Seconds)), (Format-Span ([double] $q.CpuSeconds)), $q.Executions, $q.PagesRead, $q.Dop, $q.MemoryMb, $q.TempdbMb, $q.Module, $q.Statement, $q.QueryId)
            }
        }
        catch { Write-Host "  Query Store could not be read in $($db.Name): $($_.Exception.Message)" -ForegroundColor Yellow }
    }
}

# ---------------------------------------------------------------------------------------------
if ($run.Status -eq 'Running') {
    Write-Head '4. What the task is running now'
    try {
        $live = Get-Row $cp @"
SELECT r.session_id AS Spid, s.program_name AS Program, DB_NAME(r.database_id) AS Db, r.status AS Status, ISNULL(r.wait_type, N'') AS WaitType,
       r.blocking_session_id AS BlockedBy, r.total_elapsed_time / 1000 AS ElapsedSeconds, r.cpu_time / 1000 AS CpuSeconds,
       r.logical_reads AS PagesRead, r.granted_query_memory * 8 / 1024 AS MemoryMb, r.dop AS Dop,
       ISNULL(OBJECT_SCHEMA_NAME(t.objectid, t.dbid) + N'.' + OBJECT_NAME(t.objectid, t.dbid), N'') AS Module,
       LEFT(REPLACE(REPLACE(SUBSTRING(t.text, r.statement_start_offset / 2 + 1,
            (CASE r.statement_end_offset WHEN -1 THEN DATALENGTH(t.text) ELSE r.statement_end_offset END - r.statement_start_offset) / 2 + 1), CHAR(13), N' '), CHAR(10), N' '), 300) AS Statement
FROM sys.dm_exec_requests AS r
JOIN sys.dm_exec_sessions AS s ON s.session_id = r.session_id
CROSS APPLY sys.dm_exec_sql_text(r.sql_handle) AS t
WHERE s.program_name LIKE N'CopilotAudit%' AND r.session_id <> @@SPID
ORDER BY r.total_elapsed_time DESC;
"@
        if ($live.Count -eq 0) { Write-Host '  No statement of the task is running in SQL Server at this moment: it is between statements, downloading from Purview, or reading the directory.' }
        foreach ($q in $live) {
            Write-Host ('  Session {0} in {1}: {2} for {3} (CPU {4}), {5:N0} pages read, DOP {6}, {7:N0} MB granted{8}{9}' -f $q.Spid, $q.Db, $q.Status, (Format-Span ([double] $q.ElapsedSeconds)), (Format-Span ([double] $q.CpuSeconds)), $q.PagesRead, $q.Dop, $q.MemoryMb, $(if ($q.WaitType) { ", waiting on $($q.WaitType)" } else { '' }), $(if ($q.BlockedBy) { ", BLOCKED by session $($q.BlockedBy)" } else { '' }))
            Write-Host ('      {0}  {1}' -f $q.Module, $q.Statement)
            Write-Host ("      Its plan as it runs: SELECT query_plan FROM sys.dm_exec_query_statistics_xml($($q.Spid));")
        }
    }
    catch { Write-Host "  The running statements could not be read (VIEW SERVER STATE is needed): $($_.Exception.Message)" -ForegroundColor Yellow }
}

# ---------------------------------------------------------------------------------------------
Write-Head 'Where the time went'
$ranked = @($all | Where-Object { $null -ne $_.Seconds } | Sort-Object Seconds -Descending)
foreach ($x in $ranked | Select-Object -First 5) {
    Write-Host ('  {0,12}  {1,4:P0}  {2}' -f (Format-Span $x.Seconds), $(if ($whole -gt 0) { $x.Seconds / $whole } else { 0 }), $x.Stage)
}
if ($ranked.Count) {
    $first = $ranked[0].Stage
    $hint = switch -Wildcard ($first) {
        'Purview download*' { 'Purview sets this pace: the search runs in the service, and the tenant shares about 200 searches a day. It is the same whatever SQL Server does.' }
        'Merge*' { 'audit.usp_MergeCopilotLoad: it checks each staged record against audit.CopilotAudit. Section 3 shows its statements.' }
        'Flatten*' { 'audit.usp_FlattenCopilotAudit reads each record of the window once (the lookback, 48 hours, and the new hours). Section 3 shows which of its statements took the time.' }
        'Sessions*' { 'audit.usp_BuildCopilotSessions rebuilds the sessions from the window''s first day. A full build (said above) rebuilds every session: that follows an upgrade or a changed setting, and is not a night''s work. Section 3 shows which of its statements took the time.' }
        'Report tables*' { 'Beside the window''s interactions, every run rebuilds the license facts (the changes and the licensed days) and the people. Section 3 shows which statement took the time.' }
        'Dashboard totals*' { 'It rebuilds the org chart, then the rollups from the window''s first day (every day after a full bi rebuild).' }
        'License SKU changes*' { '"Every event" (said above) follows a full shred, or a night that shredded more than a tenth of the events; a usual night works out only its own.' }
        'Shredding license*' { 'audit.usp_ShredLicenseEvents (and audit.usp_ShredAgentEvents) in the license database: each record of the window is read once.' }
        default { 'See its lines above.' }
    }
    Write-Host ''
    Write-Host "  Longest: $first. $hint"
    Write-Host '  For one statement''s plan and waits: docs\OPERATIONS.md, "A slow nightly run".'
}
else { Write-Host '  This run has no step recorded, so there is nothing to rank.' }
# 2.5.4 took out of every night two costs that grew with the license history, not with the night's load.
if ($lic -and (Test-Object $lic 'audit.LicenseEvent') -and -not (Test-Object $lic 'audit.LicenseSkuChange')) {
    $events = (Get-Row $lic "SELECT ISNULL(SUM(p.rows), 0) AS N FROM sys.partitions AS p WHERE p.object_id = OBJECT_ID(N'audit.LicenseEvent') AND p.index_id IN (0, 1)")[0].N
    Write-Host ''
    Write-Host ("  $LicenseDatabase is on a release before 2.5.4. There, the session build and the report-table refresh each work out every license SKU change from the records every night ({0:N0} license events here), and the session build looks each session up in them without an index. Both grow with the license history, whatever the night's load: 11 minutes a read and 11 for the lookups over 4,000,000 events in the lab, seconds from 2.5.4 on. Upgrade both databases (CHANGELOG.md, 2.5.4)." -f [long] $events) -ForegroundColor Yellow
}
$cp.Dispose(); if ($lic) { $lic.Dispose() }
