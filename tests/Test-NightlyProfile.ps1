<#
.SYNOPSIS
    Keeps tools/Measure-CopilotNightly.ps1 honest: how it splits a run's time into stages, how it reads
    a nightly log, and that it only reads.

.DESCRIPTION
    The script needs SQL Server and a database the nightly task has run against, so these tests run its
    own pieces without them. Its functions are dot-sourced from the script itself, so a change to the
    script is a change to what is tested:

      1. A length of time, as it is printed.
      2. The stages of the Copilot interactions step, from what each stage recorded: in order, each
         with its time, and adding up to the step. A load an earlier run began, a full session build,
         a step with nothing to load, and the license step.
      3. A nightly log: its two kinds of dated line, and the lines that open a stage with the time to
         the next one.
      4. It only reads: a read-only connection, uncommitted reads, and nothing that writes.

.EXAMPLE
    pwsh -NoProfile -File tests/Test-NightlyProfile.ps1
#>
#Requires -Version 7.2
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot 'TestHelpers.ps1')

$scriptPath = Join-Path (Join-Path $root 'tools') 'Measure-CopilotNightly.ps1'
$text = [System.IO.File]::ReadAllText($scriptPath)
$tokens = $null; $parseErrors = $null
[void] [System.Management.Automation.Language.Parser]::ParseFile($scriptPath, [ref] $tokens, [ref] $parseErrors)
Assert-That ($parseErrors.Count -eq 0) "the script parses ($($parseErrors.Count) error(s))"
. $scriptPath          # dot-sourced: its functions only

function T([string] $Time) { [datetime]::ParseExact("2026-10-04 $Time", 'yyyy-MM-dd HH:mm:ss', [cultureinfo]::InvariantCulture) }

# ---------------------------------------------------------------------------
Write-Host "`n1. A length of time" -ForegroundColor Cyan

Assert-That ((Format-Span 26) -eq '26 s') '26 seconds is "26 s"'
Assert-That ((Format-Span 259) -eq '4 min 19 s') '259 seconds is "4 min 19 s"'
Assert-That ((Format-Span 22320) -eq '6 h 12 min') '22,320 seconds is "6 h 12 min"'
Assert-That ((Format-Span 3600) -eq '1 h 0 min') 'an hour is "1 h 0 min"'
Assert-That ((Format-Span $null) -eq '') 'no time is an empty cell'

# ---------------------------------------------------------------------------
Write-Host "`n2. The stages of a load step" -ForegroundColor Cyan

$load = [pscustomobject]@{ StartedUtc = T '12:11:20'; DownloadedUtc = T '13:05:00'; MergedUtc = T '13:12:00'; RowsStaged = 1540000; RowsAdded = 498000; RowsAlreadyLoaded = 1042000 }
$session = [pscustomobject]@{ RunStartUtc = T '13:40:00'; RunEndUtc = T '15:55:00'; RowsRead = 1540000; SessionsWritten = 82000; RebuiltFrom = [datetime]::new(2026, 10, 1) }
$bi = [pscustomobject]@{ StartedUtc = T '15:55:05'; EndedUtc = T '18:30:00'; SinceUtc = T '11:00:00'; InteractionRows = 1540000; SessionRows = 82000; LicenseChangeRows = 310000; LicensedDayRows = 5400000 }
$dash = [pscustomobject]@{ StartedUtc = T '18:30:02'; EndedUtc = T '19:09:00'; SinceUtc = T '11:00:00'; FromDateKey = 20261001; UserDayRows = 190000 }
$stages = @(Get-LoadStage -StepStart (T '12:11:00') -StepEnd (T '19:10:00') -Load $load -Session $session -Bi $bi -Dash $dash)
$names = $stages | ForEach-Object Stage
Assert-That (($names -join ' | ') -eq (@('Before the download: settings, sign-in, the load''s range', 'Purview download', 'Merge into audit.CopilotAudit', 'Flatten, and agent names',
    'Sessions', 'Between the sessions and the report tables', 'Report tables (bi.usp_RefreshBi)', 'Between the report tables and the dashboard totals',
    'Dashboard totals (dash.usp_RefreshDashboard)', 'Closing: marking the load refreshed') -join ' | ')) "the stages come in the order they run ($($names.Count))"
function Seconds([string] $Name) { ($stages | Where-Object Stage -eq $Name).Seconds }
Assert-That ((Seconds 'Purview download') -eq 3220 -and (Seconds 'Merge into audit.CopilotAudit') -eq 420) 'the download runs to DownloadedUtc and the merge to MergedUtc'
Assert-That ((Seconds 'Flatten, and agent names') -eq 1680) 'what runs between the merge and the sessions is the flatten'
Assert-That ((Seconds 'Sessions') -eq 8100 -and (Seconds 'Report tables (bi.usp_RefreshBi)') -eq 9295 -and (Seconds 'Dashboard totals (dash.usp_RefreshDashboard)') -eq 2338) 'sessions, report tables and dashboard totals each take their own run''s time'
$sum = ($stages | Measure-Object Seconds -Sum).Sum
Assert-That ($sum -eq ((T '19:10:00') - (T '12:11:00')).TotalSeconds) "the stages add up to the step ($sum seconds)"
Assert-That (($stages | Where-Object Stage -eq 'Sessions').Detail -eq '1,540,000 records read, 82,000 sessions written, from 2026-10-01') 'an incremental session build says the day it rebuilt from'
Assert-That (($stages | Where-Object Stage -eq 'Merge into audit.CopilotAudit').Detail -eq '498,000 new, 1,042,000 already loaded') 'the merge says how many records were new and how many the lookback had already'

# A load an earlier run began: its download is counted from this step's start, not from the earlier night.
$resumed = [pscustomobject]@{ StartedUtc = T '02:00:00'; DownloadedUtc = T '12:40:00'; MergedUtc = T '12:45:00'; RowsStaged = 10; RowsAdded = 10; RowsAlreadyLoaded = 0 }
$r = @(Get-LoadStage -StepStart (T '12:11:00') -StepEnd (T '12:50:00') -Load $resumed)
Assert-That ($r[0].Stage -eq 'Purview download' -and $r[0].Seconds -eq 1740) 'a load begun by an earlier run is timed from this step''s start'

# A full session build and a full bi rebuild say so: neither should happen on an ordinary night.
$full = @(Get-LoadStage -StepStart (T '12:11:00') -StepEnd (T '19:10:00') -Load $load `
    -Session ([pscustomobject]@{ RunStartUtc = T '13:40:00'; RunEndUtc = T '15:55:00'; RowsRead = 90000000; SessionsWritten = 4940295; RebuiltFrom = $null }) `
    -Bi ([pscustomobject]@{ StartedUtc = T '15:55:05'; EndedUtc = T '18:30:00'; SinceUtc = $null; InteractionRows = 90000000; SessionRows = 4940295; LicenseChangeRows = 310000; LicensedDayRows = 5400000 }))
Assert-That (($full | Where-Object Stage -eq 'Sessions').Detail -like '*4,940,295 sessions written: a full build') 'a full session build is named as one'
Assert-That (($full | Where-Object Stage -like 'Report tables*').Detail -like '*: a full rebuild') 'a full rebuild of the report tables is named as one'
Assert-That ($full[-1].Stage -eq 'Closing: marking the load refreshed') 'with no dashboard, what follows the report tables is the closing'

# Nothing to load: no stage, the step's own line says all there is.
Assert-That (@(Get-LoadStage -StepStart (T '12:11:00') -StepEnd (T '12:11:30')).Count -eq 0) 'a step that loaded nothing has no stages'

# The license step: after its merge, the shred.
$licLoad = [pscustomobject]@{ StartedUtc = T '12:05:10'; DownloadedUtc = T '12:08:00'; MergedUtc = T '12:08:30'; RowsStaged = 66000; RowsAdded = 22000; RowsAlreadyLoaded = 44000 }
$lic = @(Get-LoadStage -StepStart (T '12:05:00') -StepEnd (T '12:11:00') -Load $licLoad -AfterMerge 'Shredding license and agent events' -Closing 'Shredding agent events, and closing')
Assert-That ($lic[-1].Stage -eq 'Shredding license and agent events' -and $lic[-1].Seconds -eq 150) 'in the license step, what follows the merge is the shred'

# The shred ends by working out the SKU changes of what it shredded, and records that run: a stage of its own.
$sku = [pscustomobject]@{ StartedUtc = T '12:10:20'; EndedUtc = T '12:10:29'; SinceUtc = T '12:00:00'; Events = 44405; Changes = 3335 }
$lic = @(Get-LoadStage -StepStart (T '12:05:00') -StepEnd (T '12:11:00') -Load $licLoad -Sku $sku -AfterMerge 'Shredding license events' -Closing 'Shredding agent events, and closing')
Assert-That ((($lic | ForEach-Object Stage) -join ' | ') -eq (@('Before the download: settings, sign-in, the load''s range', 'Purview download', 'Merge into audit.CopilotAudit', 'Shredding license events',
    'License SKU changes (audit.usp_RefreshLicenseSkuChange)', 'Shredding agent events, and closing') -join ' | ')) 'the license step shows the shred, the SKU changes worked out after it, then the agent shred'
$skuStage = $lic | Where-Object Stage -like 'License SKU changes*'
Assert-That ($skuStage.Seconds -eq 9 -and $skuStage.Detail -eq '44,405 events worked out, 3,335 changes kept' -and ($lic | Where-Object Stage -eq 'Shredding license events').Seconds -eq 110) 'each with its own time, and what the SKU run worked out'
Assert-That (($lic | Measure-Object Seconds -Sum).Sum -eq 360) 'and they add up to the step'
$every = @(Get-LoadStage -StepStart (T '12:05:00') -StepEnd (T '12:11:00') -Sku ([pscustomobject]@{ StartedUtc = T '12:06:00'; EndedUtc = T '12:08:00'; SinceUtc = $null; Events = 4000000; Changes = 300000 }) -AfterMerge 'Shredding license events')
Assert-That (($every | Where-Object Stage -like 'License SKU changes*').Detail -eq '4,000,000 events worked out, 300,000 changes kept: every event') 'a run that worked out every event says so'

# ---------------------------------------------------------------------------
Write-Host "`n3. A nightly log" -ForegroundColor Cyan

$log = @(
    '2026-10-04T08:11:02 == Copilot interactions',
    '2026-10-04 12:11:20Z [INFO ] New load 31: 2026-10-01 11:00Z -> 2026-10-04 12:00Z (73 h, the first 48 h a lookback over data already loaded).',
    '2026-10-04 13:12:00Z [INFO ] Flattening records from 2026-10-01 11:00Z...',
    '2026-10-04 13:38:00Z [INFO ] SQL: Flattened 1,540,000 record(s).',
    '2026-10-04 13:38:30Z [INFO ] Shredding agent build and publish events from 2026-10-01 11:00Z...',
    '2026-10-04 13:40:00Z [INFO ] Rebuilding sessions and the weekly rollup from 2026-10-01...',
    '2026-10-04 15:55:05Z [WARN ] something to look at',
    '2026-10-04 15:55:05Z [INFO ] Refreshing the bi star schema from 2026-10-01 11:00Z...',
    'a line with no time',
    '2026-10-04 18:30:02Z [INFO ] Refreshing the dashboard rollups from 2026-10-01...',
    '2026-10-04 19:09:30Z [INFO ] Incremental load 31 complete: the data now reaches 2026-10-04 12:00Z.')
$entries = @(ConvertFrom-NightlyLog -Line $log -UtcOffset ([timespan]::FromHours(-4)))
Assert-That ($entries.Count -eq 10) "every dated line is read, and the one with no time is left out ($($entries.Count))"
Assert-That ($entries[0].TimeUtc -eq (T '12:11:02') -and $entries[0].Level -eq 'STEP') 'a pipeline line, in the job server''s time, is turned to UTC'
Assert-That ($entries[1].TimeUtc -eq (T '12:11:20') -and $entries[1].Level -eq 'INFO') 'a step''s own line is already UTC'
$marks = @(Get-LogStage -Entry $entries -FromUtc (T '12:11:00') -ToUtc (T '19:10:00'))
Assert-That ($marks.Count -eq 8) "the lines that open a stage are kept, the SQL message and the warning are not ($($marks.Count))"
Assert-That (($marks | Where-Object Stage -like 'Flattening records*').Seconds -eq 1590) 'the flatten runs to the agent shred''s line: the log splits what the tables cannot'
Assert-That (($marks | Where-Object Stage -like 'Shredding agent*').Seconds -eq 90) 'the agent shred has its own time'
Assert-That (($marks | Where-Object Stage -like 'Refreshing the bi*').Seconds -eq 9297) 'the report tables run to the dashboard''s line'
Assert-That ($marks[-1].Seconds -eq 30) 'the last line runs to the end of the run'

# ---------------------------------------------------------------------------
Write-Host "`n4. It only reads" -ForegroundColor Cyan

Assert-That ($text.Contains("`$b['ApplicationIntent'] = 'ReadOnly'")) 'the connection says it is read only'
Assert-That ($text.Contains('SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED')) 'it reads uncommitted data, so it takes no row or page locks'
Assert-That (-not $text.Contains('ExecuteNonQuery')) 'it never executes a command that writes'
$writes = @([regex]::Matches($text, '(?i)\b(INSERT\s+INTO|UPDATE\s+[\w\.\[\]]+\s+SET|DELETE\s+FROM|TRUNCATE\s+TABLE|DROP\s+(TABLE|INDEX|VIEW|PROCEDURE)|ALTER\s+(TABLE|DATABASE|EVENT)|CREATE\s+(TABLE|INDEX|VIEW|PROCEDURE|EVENT)|\bEXEC(UTE)?\s+\w)') | ForEach-Object Value)
Assert-That ($writes.Count -eq 0) "nothing in it writes or runs a procedure ($($writes -join '; '))"
Assert-That ($text -match "'Copilot nightly profile \(read only\)'") 'SQL Server sees it under its own application name'
$backfill = [System.IO.File]::ReadAllText((Join-Path $root 'Invoke-CopilotAuditBackfill.ps1'))
Assert-That ($backfill.Contains("`$csb['Application Name'] = 'CopilotAuditBackfill'") -and $text.Contains("s.program_name LIKE N'CopilotAudit%'")) `
    'it finds the task''s running statements by the application name the task connects with'
foreach ($opens in 'Flattening records', 'Shredding license events', 'Shredding agent build', 'Rebuilding sessions', 'Refreshing the bi star schema', 'Refreshing the dashboard rollups') {
    Assert-That ($backfill.Contains("Write-Log ('$opens") -and $text.Contains($opens)) "the load still writes its ""$opens"" line, which the log reading looks for"
}

Write-Host ''
if ($script:Failures.Count) { Write-Host "$($script:Failures.Count) assertion(s) failed." -ForegroundColor Red; exit 1 }
Write-Host 'All nightly profile tests passed.' -ForegroundColor Green
