<#
    End-to-end tests of the backfill engine against a mock Graph Audit Search
    service (mock_graph.py) and in-memory SQL stand-ins (TestDoubles.cs).

      0  RowBatchReader honors the IDataReader contract (loaded via DataTable.Load)
      A  30 days / ~630K records: adaptive windows, beta cap detection + splits,
         a server-side failed search, page 429s under a concurrency ceiling,
         random 503s, token rotation (401), transient SQL failures, a hard stop
         mid-run, then resume. Proves every record lands exactly once in the
         finalized set.
      B  v1.0-only status (no cap properties): truncation caught by row count
      C  engine's own submission budget stops it before the service's
      D  service-side submission 429s: engine backs off instead of hammering
      F  a range reaching past the data: empty windows widen instead of
         resetting to InitialWindowHours and exhausting the search budget
      G  status with no approximate count on a sparse tenant: windows still
         widen, from the rows actually loaded

    Requires python3 on PATH.
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Split-Path $PSScriptRoot -Parent
. (Join-Path $root 'Import-CopilotAuditEngine.ps1')
Import-CopilotAuditEngine -ExtraSource (Join-Path $PSScriptRoot 'TestDoubles.cs') 3>$null

. (Join-Path $PSScriptRoot 'TestHelpers.ps1')

# ---------------------------------------------------------------------------
# 0. RowBatchReader contract
# ---------------------------------------------------------------------------

Write-Host "`n0. RowBatchReader (the SqlBulkCopy source)" -ForegroundColor Cyan
$rows = [System.Collections.Generic.List[object[]]]::new()
$rows.Add([object[]]@([long]7, [int]2, 'rec-1', [datetime]::new(2026,9,1,0,0,0,[DateTimeKind]::Utc), 'copilotInteraction', 'CopilotInteraction',
                      'org', 'regular', 'u@c.com', 'u@c.com', 'Copilot', 'obj', '1.2.3.4', 'AU1;AU2', [byte[]](31,139,8,0)))
$rows.Add([object[]]@([long]7, [int]2, 'rec-2', [datetime]::new(2026,9,1,0,0,1,[DateTimeKind]::Utc), $null, $null, $null, $null, $null, $null, $null, $null, $null, $null, $null))
$reader = [CopilotAudit.RowBatchReader]::new($rows)
$dt = [System.Data.DataTable]::new()
$dt.Load($reader)
Assert-That ($dt.Rows.Count -eq 2) 'DataTable.Load reads both rows'
Assert-That ($dt.Columns.Count -eq 15) 'all 15 columns exposed'
Assert-That ($dt.Columns['CreatedDateTime'].DataType -eq [datetime]) 'CreatedDateTime typed as DateTime'
Assert-That ($dt.Columns['WindowId'].DataType -eq [long]) 'WindowId typed as Int64'
Assert-That ($dt.Rows[1]['AuditDataGz'] -is [DBNull]) 'nulls surface as DBNull'
Assert-That ($dt.Columns['AuditDataGz'].DataType -eq [byte[]]) 'AuditDataGz typed as byte[] (VARBINARY)'
Assert-That ($dt.Rows[0]['AdministrativeUnits'] -eq 'AU1;AU2') 'values surface unchanged'

# ---------------------------------------------------------------------------
# A. Full scenario with crash + resume
# ---------------------------------------------------------------------------

Write-Host "`nA. 30 days, beta cap detection, throttling, 401, transient SQL errors, stop + resume" -ForegroundColor Cyan
$mock = Start-MockGraph @{}
try {
    $start = [datetime]::new(2026, 8, 1, 0, 0, 0, [DateTimeKind]::Utc)
    $end   = [datetime]::new(2026, 8, 31, 0, 0, 0, [DateTimeKind]::Utc)
    $expected = Get-ExpectedId $mock
    $store = [CopilotAudit.Tests.MemoryWindowStore]::new()
    $sink  = [CopilotAudit.Tests.MemorySink]::new()
    $sink.FailEvery = 29
    $sink.DelayMs = 5
    $logA = [System.Collections.Generic.List[string]]::new()

    $script:rotated = $false
    $tick = {
        param($e)
        $s = $e.GetStatus()
        if (-not $script:rotated -and $s.RowsWritten -ge 120000) {
            Invoke-RestMethod -Method Post "$($mock.Base)/_control/rotate" | Out-Null   # old token now rejected
            $script:rotated = $true
        }
        if ($s.RowsWritten -ge 260000) { $e.RequestStop() }                            # simulated crash / Ctrl+C
    }

    $run1 = Invoke-EngineRun -Mock $mock -Options (New-EngineOption $mock $start $end) -Store $store -Sink $sink -OnTick $tick -Log $logA
    $afterRun1 = $store.Snapshot()
    $loaded1 = @($afterRun1 | Where-Object Status -eq 'Loaded').Count
    $partial1 = @($afterRun1 | Where-Object { $_.Status -in 'Downloading', 'Ready', 'Submitted' }).Count

    $run2 = Invoke-EngineRun -Mock $mock -Options (New-EngineOption $mock $start $end) -Store $store -Sink $sink -Log $logA
    $stats = Get-MockStatistic $mock

    Write-Host ("  run 1: {0:N1}s, stopped at {1:N0} rows | run 2: {2:N1}s | {3:N0} rows written, {4:N0} pages" -f `
                $run1.Seconds, $run1.Status.RowsWritten, $run2.Seconds, ($run1.Status.RowsWritten + $run2.Status.RowsWritten),
                $stats.pages_served) -ForegroundColor DarkGray

    Assert-That ($run1.Task.IsCanceled) 'run 1 stopped mid-flight (Canceled)'
    Assert-That ($loaded1 -gt 0 -and $partial1 -gt 0) "run 1 left $loaded1 windows loaded and $partial1 partially processed"
    Assert-That ($run2.Task.Status -eq 'RanToCompletion') "run 2 resumed and completed ($($run2.Task.Status))"

    $res = Test-FinalSet -Store $store -Sink $sink -ExpectedIds $expected -Start $start -End $end -Label 'A'
    Assert-That ($res.Abandoned -gt 0) ("rows from interrupted attempts exist ({0:N0}) and are excluded by the attempt tag" -f $res.Abandoned)

    $splits = @($res.Windows | Where-Object { $_.Status -eq 'Split' -and $_.LastError -match 'cap' })
    Assert-That ($splits.Count -ge 1) "service-reported cap exceedance split $($splits.Count) window(s) before download"
    Assert-That (@($logA | Where-Object { $_ -match 'audit search failed' }).Count -ge 1) 'server-side failed search was retried'

    $ratio = if ($stats.pages_served) { $stats.pages_429 / $stats.pages_served } else { 1 }
    Assert-That ($stats.pages_429 -gt 0) "page throttling happened ($($stats.pages_429) x 429)"
    Assert-That ($ratio -lt 0.10) ("429s stayed under 10% of pages ({0:P1}) - the limiter adapted" -f $ratio)
    Assert-That ($stats.max_pages_in_flight -le 8) "never more than MaxDownloadStreams (8) page requests at once (max $($stats.max_pages_in_flight))"
    Assert-That ($stats.unauthorized -gt 0 -and ($run1.Refreshes + $run2.Refreshes) -ge 1) "token rotation: $($stats.unauthorized) x 401 answered with a refresh"
    Assert-That ($sink.InjectedFailures -gt 0) "transient SQL failures retried ($($sink.InjectedFailures))"
    Assert-That ($sink.MaxConcurrentWrites -ge 2) "SQL writes ran in parallel (max $($sink.MaxConcurrentWrites) concurrent)"
    Assert-That ($sink.AuditPayloadsVerified -gt 600000) ("every AuditData payload round-tripped gzip -> UTF-16 -> JSON ({0:N0})" -f $sink.AuditPayloadsVerified)
    $ratio2 = $run2.Status.AuditBytesRaw / [math]::Max(1, $run2.Status.AuditBytesCompressed)
    # The mock's records are small (~700 chars), the worst case for gzip; real ones
    # measured 2.4x-7.2x. Assert the floor.
    Assert-That ($ratio2 -gt 2) ("AuditData compressed {0:N1}x before reaching SQL (small synthetic records)" -f $ratio2)
    Assert-That ($stats.submits_429 -eq 0) 'no submission was ever throttled'
    Assert-That ($stats.queries_created -le 60) "submissions stayed economical ($($stats.queries_created) searches for 30 days)"

    # Density ramps 2K -> 40K/day, so windows in the older half should be larger.
    # The window touching the range start is a clipped remnant - leave it out.
    $sized = @($res.Loaded | Where-Object { $_.StartUtc -gt $start })
    $hours = @($sized | ForEach-Object { ($_.EndUtc - $_.StartUtc).TotalHours })
    Write-Host ("  window hours, oldest -> newest: " + ($hours -join ' ')) -ForegroundColor DarkGray
    $half = [math]::Floor($hours.Count / 2)
    $older = $hours[0..($half - 1)] | Measure-Object -Average -Maximum
    $newer = $hours[$half..($hours.Count - 1)] | Measure-Object -Average -Maximum
    Assert-That ($older.Average -gt $newer.Average -and $older.Maximum -gt $newer.Maximum) `
        ("windows adapt to density: older half avg {0:N1}h / max {1:N0}h vs newer half avg {2:N1}h / max {3:N0}h" -f `
         $older.Average, $older.Maximum, $newer.Average, $newer.Maximum)

    $errors = @($logA | Where-Object { $_ -match '\[ERROR\]' })
    Assert-That ($errors.Count -eq 0) "no ERROR log lines ($($errors.Count))"
    $logA | Set-Content (Join-Path ([System.IO.Path]::GetTempPath()) 'engine_test_A.log')
}
finally { Stop-MockGraph $mock }

# ---------------------------------------------------------------------------
# B. v1.0-only status: fallback truncation detection by row count
# ---------------------------------------------------------------------------

Write-Host "`nB. Beta status unavailable - truncation caught by row count" -ForegroundColor Cyan
$mock = Start-MockGraph @{ days = 8; beta_supported = $false; fail_first_query_covering = '2030-01-01T00:00:00Z'; page_503_rate = 0 }
try {
    $start = [datetime]::new(2026, 8, 1, 0, 0, 0, [DateTimeKind]::Utc)
    $end   = [datetime]::new(2026, 8, 9, 0, 0, 0, [DateTimeKind]::Utc)
    $expected = Get-ExpectedId $mock
    $store = [CopilotAudit.Tests.MemoryWindowStore]::new()
    $sink  = [CopilotAudit.Tests.MemorySink]::new()
    $logB = [System.Collections.Generic.List[string]]::new()
    $run = Invoke-EngineRun -Mock $mock -Options (New-EngineOption $mock $start $end) -Store $store -Sink $sink -Log $logB

    Assert-That ($run.Task.Status -eq 'RanToCompletion') "completed ($($run.Task.Status))"
    Assert-That (@($logB | Where-Object { $_ -match 'using v1.0' }).Count -eq 1) 'switched to v1.0 status once'
    Assert-That (@($logB | Where-Object { $_ -match 'did not report truncation' }).Count -ge 1) 'a capped window was detected by row count and split'
    [void](Test-FinalSet -Store $store -Sink $sink -ExpectedIds $expected -Start $start -End $end -Label 'B')
}
finally { Stop-MockGraph $mock }

# ---------------------------------------------------------------------------
# C. Engine budget stops submissions before the service has to
# ---------------------------------------------------------------------------

Write-Host "`nC. Own submission budget" -ForegroundColor Cyan
$mock = Start-MockGraph @{ days = 30; fail_first_query_covering = '2030-01-01T00:00:00Z' }
try {
    $store = [CopilotAudit.Tests.MemoryWindowStore]::new()
    $sink  = [CopilotAudit.Tests.MemorySink]::new()
    $logC = [System.Collections.Generic.List[string]]::new()
    $opts = New-EngineOption $mock ([datetime]::new(2026, 8, 1, 0, 0, 0, [DateTimeKind]::Utc)) ([datetime]::new(2026, 8, 31, 0, 0, 0, [DateTimeKind]::Utc)) @{ MaxSubmissionsPer24h = 4; InitialWindowHours = 12 }
    $script:t0 = [datetime]::UtcNow
    $run = Invoke-EngineRun -Mock $mock -Options $opts -Store $store -Sink $sink -Log $logC `
            -OnTick { param($e) if (([datetime]::UtcNow - $script:t0).TotalSeconds -gt 12) { $e.RequestStop() } }
    $stats = Get-MockStatistic $mock
    Assert-That ($stats.queries_created -eq 4) "stopped at its own budget: $($stats.queries_created) submissions"
    Assert-That ($stats.submits_429 -eq 0) 'service never had to throttle a submission'
    Assert-That (@($logC | Where-Object { $_ -match 'Submission budget reached' }).Count -eq 1) 'budget pause announced once'
    Assert-That ($run.Status.RowsWritten -gt 0) 'downloads continued while submissions were paused'
}
finally { Stop-MockGraph $mock }

# ---------------------------------------------------------------------------
# D. Service-side submission throttling
# ---------------------------------------------------------------------------

Write-Host "`nD. Service throttles submissions" -ForegroundColor Cyan
$mock = Start-MockGraph @{ days = 30; submission_budget = 3; fail_first_query_covering = '2030-01-01T00:00:00Z' }
try {
    $store = [CopilotAudit.Tests.MemoryWindowStore]::new()
    $sink  = [CopilotAudit.Tests.MemorySink]::new()
    $logD = [System.Collections.Generic.List[string]]::new()
    $opts = New-EngineOption $mock ([datetime]::new(2026, 8, 1, 0, 0, 0, [DateTimeKind]::Utc)) ([datetime]::new(2026, 8, 31, 0, 0, 0, [DateTimeKind]::Utc))
    $script:t0 = [datetime]::UtcNow
    $run = Invoke-EngineRun -Mock $mock -Options $opts -Store $store -Sink $sink -Log $logD `
            -OnTick { param($e) if (([datetime]::UtcNow - $script:t0).TotalSeconds -gt 12) { $e.RequestStop() } }
    $stats = Get-MockStatistic $mock
    Assert-That ($stats.queries_created -eq 3) "3 searches accepted"
    Assert-That ($stats.submits_429 -ge 1 -and $stats.submits_429 -le 7) "backed off on 429 instead of hammering ($($stats.submits_429) rejected in ~12s with Retry-After 2s)"
    Assert-That (@($logD | Where-Object { $_ -match 'submission throttled' }).Count -ge 1) 'throttle logged'
}
finally { Stop-MockGraph $mock }

# ---------------------------------------------------------------------------
# E. Sign-in never completes after a 401: stop cleanly, then resume
# ---------------------------------------------------------------------------

Write-Host "`nE. Forced re-sign-in nobody answers" -ForegroundColor Cyan
$mock = Start-MockGraph @{ days = 6; rotate_after_pages = 25; fail_first_query_covering = '2030-01-01T00:00:00Z' }
try {
    $start = [datetime]::new(2026, 8, 1, 0, 0, 0, [DateTimeKind]::Utc)
    $end   = [datetime]::new(2026, 8, 7, 0, 0, 0, [DateTimeKind]::Utc)
    $expected = Get-ExpectedId $mock
    $store = [CopilotAudit.Tests.MemoryWindowStore]::new()
    $sink  = [CopilotAudit.Tests.MemorySink]::new()
    $logE = [System.Collections.Generic.List[string]]::new()
    $opts = New-EngineOption $mock $start $end @{ TokenWaitMinutes = 0.05; InitialWindowHours = 24 }
    $run = Invoke-EngineRun -Mock $mock -Options $opts -Store $store -Sink $sink -Log $logE -NoTokenRefresh -TimeoutSec 60
    $snap = $store.Snapshot()
    $fault = if ($run.Task.Exception) { $run.Task.Exception.GetBaseException().Message } else { '' }

    Assert-That ($run.Task.IsFaulted) ("pipeline stopped instead of churning ({0}, {1:N1}s)" -f $run.Task.Status, $run.Seconds)
    Assert-That ($fault -match 'no new sign-in completed') 'the reason is stated plainly'
    Assert-That (@($snap | Where-Object Status -eq 'Failed').Count -eq 0) 'no window was written off as Failed'

    $opts2 = New-EngineOption $mock $start $end @{ InitialWindowHours = 24 }
    $run2 = Invoke-EngineRun -Mock $mock -Options $opts2 -Store $store -Sink $sink -Log $logE
    Assert-That ($run2.Task.Status -eq 'RanToCompletion') 'next run with a valid sign-in resumed and completed'
    [void](Test-FinalSet -Store $store -Sink $sink -ExpectedIds $expected -Start $start -End $end -Label 'E')
}
finally { Stop-MockGraph $mock }

# ---------------------------------------------------------------------------
# F. Empty windows past the retention horizon
# ---------------------------------------------------------------------------

Write-Host "`nF. A range reaching past the data: empty windows must widen, not reset" -ForegroundColor Cyan

# The mock only has records from 2026-08-20. Asking for 2026-08-01 onward makes
# the first 19 days empty, which is what a real range past the 180-day retention
# horizon looks like - and what a sparse tenant looks like all the way through.
#
# The planner walks backwards from RangeEndUtc, so it meets the records first
# and the empty span last. An empty window used to be read the same as "no
# measurement yet" and reset the next window to InitialWindowHours, so the empty
# span cost one search per day and burned the 150-per-24h submission budget.
$mock = Start-MockGraph @{ range_start = '2026-08-20T00:00:00Z'; days = 5 }
try {
    $start = [datetime]::new(2026, 8, 1, 0, 0, 0, [DateTimeKind]::Utc)     # 19 days with nothing
    $end   = [datetime]::new(2026, 8, 25, 0, 0, 0, [DateTimeKind]::Utc)    # 5 days with records
    $dataStart = [datetime]::new(2026, 8, 20, 0, 0, 0, [DateTimeKind]::Utc)
    $emptyHours = ($dataStart - $start).TotalHours                          # 456

    $expected = Get-ExpectedId $mock
    $store = [CopilotAudit.Tests.MemoryWindowStore]::new()
    $sink  = [CopilotAudit.Tests.MemorySink]::new()
    $logF  = [System.Collections.Generic.List[string]]::new()

    $opts = New-EngineOption $mock $start $end @{ InitialWindowHours = 24 }
    $runF = Invoke-EngineRun -Mock $mock -Options $opts -Store $store -Sink $sink -Log $logF
    $statsF = Get-MockStatistic $mock

    Assert-That ($runF.Task.Status -eq 'RanToCompletion') "completed ($($runF.Task.Status))"
    [void](Test-FinalSet -Store $store -Sink $sink -ExpectedIds $expected -Start $start -End $end -Label 'F')

    # Windows lying entirely before the first record: the empty span.
    $res = $store.Snapshot()
    $empty = @($res | Where-Object { $_.Status -eq 'Loaded' -and $_.EndUtc -le $dataStart })
    $emptyHoursEach = @($empty | ForEach-Object { ($_.EndUtc - $_.StartUtc).TotalHours })
    Write-Host ("  empty-span windows, oldest -> newest: " + ($emptyHoursEach -join ' ')) -ForegroundColor DarkGray

    # Thresholds chosen against the broken planner, not the fixed one: reverting
    # the fix gives 18 windows of which only one exceeds 24 h, so a bar like
    # "fewer than one per day" or "some window exceeded 24 h" would pass while
    # broken and prove nothing. Widening gives 5 windows, 4 of them over 24 h.
    $oneSearchPerDay = [int] ($emptyHours / 24)
    $widened = @($emptyHoursEach | Where-Object { $_ -gt 24 })
    Assert-That ($empty.Count -le 8) `
        ("the empty span cost $($empty.Count) window(s), against $oneSearchPerDay if it reset each time")
    Assert-That ($widened.Count -ge 3) `
        ("widening applied repeatedly, not once: $($widened.Count) window(s) exceed InitialWindowHours")
    Assert-That (($emptyHoursEach | Measure-Object -Maximum).Maximum -ge 96) `
        ("widening kept compounding rather than stopping after one step (max {0:N0}h)" -f ($emptyHoursEach | Measure-Object -Maximum).Maximum)
    Assert-That (($emptyHoursEach | Measure-Object -Maximum).Maximum -le $opts.MaxWindowHours) `
        ("never exceeded MaxWindowHours ({0:N0}h)" -f $opts.MaxWindowHours)

    # The whole run, not just the empty span, has to stay inside the budget.
    Assert-That ($statsF.queries_created -lt 24) `
        "the 24-day range cost $($statsF.queries_created) searches, well inside the 150/24h budget"
    Assert-That ($statsF.submits_429 -eq 0) 'never hit the service submission limit'

    $errors = @($logF | Where-Object { $_ -match '\[ERROR\]' })
    Assert-That ($errors.Count -eq 0) "no ERROR log lines ($($errors.Count))"
}
finally { Stop-MockGraph $mock }

# ---------------------------------------------------------------------------
# G. No approximate count in the status response
# ---------------------------------------------------------------------------

Write-Host "`nG. Status without an approximate count: windows must still widen" -ForegroundColor Cyan

# A live tenant returned no approximateReturnedRecordCount for any search, Copilot
# or Entra. The planner only learned density from that field, so a fresh run
# planned every window at InitialWindowHours - one search per day for the whole
# range - and only a restart, which reads the last loaded window, let them grow.
# A sparse tenant with a handful of records every day is the shape that exposed
# it: nothing is empty, so the empty-window rule never applies either.
$mock = Start-MockGraph @{ range_start = '2026-08-01T00:00:00Z'; days = 30; first_day_count = 12; last_day_count = 30; report_approx_count = $false; fail_first_query_covering = '2000-01-01T00:00:00Z' }
try {
    $start = [datetime]::new(2026, 8, 1, 0, 0, 0, [DateTimeKind]::Utc)
    $end   = [datetime]::new(2026, 8, 31, 0, 0, 0, [DateTimeKind]::Utc)

    $expected = Get-ExpectedId $mock
    $store = [CopilotAudit.Tests.MemoryWindowStore]::new()
    $sink  = [CopilotAudit.Tests.MemorySink]::new()
    $logG  = [System.Collections.Generic.List[string]]::new()

    $opts = New-EngineOption $mock $start $end @{ InitialWindowHours = 24 }
    $runG = Invoke-EngineRun -Mock $mock -Options $opts -Store $store -Sink $sink -Log $logG
    $statsG = Get-MockStatistic $mock

    Assert-That ($runG.Task.Status -eq 'RanToCompletion') "completed ($($runG.Task.Status))"
    [void](Test-FinalSet -Store $store -Sink $sink -ExpectedIds $expected -Start $start -End $end -Label 'G')

    $hoursEach = @($store.Snapshot() | Where-Object Status -eq 'Loaded' | Sort-Object StartUtc |
                   ForEach-Object { ($_.EndUtc - $_.StartUtc).TotalHours })
    Write-Host ("  window hours, oldest -> newest: " + ($hoursEach -join ' ')) -ForegroundColor DarkGray

    $approxLines = @($logG | Where-Object { $_ -match 'search complete, ~' })
    Assert-That ($approxLines.Count -eq 0) 'the mock really withheld the approximate count'
    Assert-That ($statsG.queries_created -le 18) `
        "the 30-day range cost $($statsG.queries_created) searches, against 30 at one per day"
    Assert-That (($hoursEach | Measure-Object -Maximum).Maximum -ge 96) `
        ("windows grew from the loaded row counts (max {0:N0}h)" -f ($hoursEach | Measure-Object -Maximum).Maximum)
}
finally { Stop-MockGraph $mock }

# ---------------------------------------------------------------------------
# H. A page that goes silent after its headers
# ---------------------------------------------------------------------------

Write-Host "`nH. A page body that stalls is given up on and fetched again" -ForegroundColor Cyan

# The mock sends the 5th page's headers and half its body, then nothing for ten minutes, as a proxy that drops a
# connection without a reset. HttpClient's timeout ends with the headers, so the engine used to wait on that body for
# good; each page now has a limit of its own, body included, and a page that runs past it is fetched again.
$mock = Start-MockGraph @{ days = 3; stall_page_body = 5; stall_seconds = 600; page_503_rate = 0; fail_first_query_covering = '2030-01-01T00:00:00Z' }
try {
    $start = [datetime]::new(2026, 8, 1, 0, 0, 0, [DateTimeKind]::Utc)
    $end   = [datetime]::new(2026, 8, 4, 0, 0, 0, [DateTimeKind]::Utc)
    $expected = Get-ExpectedId $mock
    $store = [CopilotAudit.Tests.MemoryWindowStore]::new()
    $sink  = [CopilotAudit.Tests.MemorySink]::new()
    $logH  = [System.Collections.Generic.List[string]]::new()
    $runH = Invoke-EngineRun -Mock $mock -Options (New-EngineOption $mock $start $end @{ PageTimeoutMinutes = 0.1 }) -Store $store -Sink $sink -Log $logH -TimeoutSec 120
    $statsH = Get-MockStatistic $mock
    Assert-That ($statsH.pages_stalled -eq 1) 'the mock stalled a page body mid-way'
    Assert-That ($runH.Task.Status -eq 'RanToCompletion' -and $runH.Seconds -lt 100) ("the run did not wait on it: completed in {0:N0}s ({1})" -f $runH.Seconds, $runH.Task.Status)
    Assert-That (@($logH | Where-Object { $_ -match 'Page request timed out - retrying' }).Count -ge 1) 'the stalled page timed out and was fetched again'
    [void](Test-FinalSet -Store $store -Sink $sink -ExpectedIds $expected -Start $start -End $end -Label 'H')
}
finally { Stop-MockGraph $mock }

# ---------------------------------------------------------------------------
# I. A split that stopped part way before 2.4.12, resumed
# ---------------------------------------------------------------------------

Write-Host "`nI. A split stopped between its writes is finished on resume" -ForegroundColor Cyan

# A split was three writes: the parent marked Split, then each half inserted. A run that stopped between them left
# the parent Split with a half missing, and resuming skipped it and planned only below the lowest window, so the hole
# was never loaded and merge or finalize refused it for good. The table as such a run left it: Aug 3-5 split with
# neither half written, Aug 1-3 split with only its older half.
$mock = Start-MockGraph @{ days = 4; page_503_rate = 0; fail_first_query_covering = '2030-01-01T00:00:00Z' }
try {
    $start = [datetime]::new(2026, 8, 1, 0, 0, 0, [DateTimeKind]::Utc)
    $end   = [datetime]::new(2026, 8, 5, 0, 0, 0, [DateTimeKind]::Utc)
    $expected = Get-ExpectedId $mock
    $store = [CopilotAudit.Tests.MemoryWindowStore]::new()
    $none = [System.Threading.CancellationToken]::None
    $p1 = [CopilotAudit.AuditWindow]@{ StartUtc = $start.AddDays(2); EndUtc = $end; Status = 'Split'; Submissions = 1; LastError = 'cap exceeded' }
    $p2 = [CopilotAudit.AuditWindow]@{ StartUtc = $start; EndUtc = $start.AddDays(2); Status = 'Split'; Submissions = 1; LastError = 'cap exceeded' }
    $store.InsertAsync($p1, $none).Wait(); $store.InsertAsync($p2, $none).Wait()
    $half = [CopilotAudit.AuditWindow]@{ ParentWindowId = $p2.WindowId; StartUtc = $start; EndUtc = $start.AddDays(1) }
    $store.InsertAsync($half, $none).Wait()
    $sink  = [CopilotAudit.Tests.MemorySink]::new()
    $logI  = [System.Collections.Generic.List[string]]::new()
    $runI = Invoke-EngineRun -Mock $mock -Options (New-EngineOption $mock $start $end @{ InitialWindowHours = 24 }) -Store $store -Sink $sink -Log $logI
    $finished = @($logI | Where-Object { $_ -match 'its split stopped part way' })
    Assert-That ($runI.Task.Status -eq 'RanToCompletion') "the resumed run completed ($($runI.Task.Status))"
    Assert-That ($finished.Count -eq 3) "the three missing halves were planned again ($($finished.Count))"
    [void](Test-FinalSet -Store $store -Sink $sink -ExpectedIds $expected -Start $start -End $end -Label 'I')
}
finally { Stop-MockGraph $mock }

# ---------------------------------------------------------------------------
# J. Two runs on one window table
# ---------------------------------------------------------------------------

Write-Host "`nJ. A second run on the same window table stops at once" -ForegroundColor Cyan

# Nothing claimed a window: a second run beside the first planned the same windows from the same cursor and wrote over
# the first one's rows. The table is now held for one run at a time; the second stops before touching it.
$mock = Start-MockGraph @{ days = 4; page_503_rate = 0; page_latency_ms = 40; fail_first_query_covering = '2030-01-01T00:00:00Z' }
try {
    $start = [datetime]::new(2026, 8, 1, 0, 0, 0, [DateTimeKind]::Utc)
    $end   = [datetime]::new(2026, 8, 5, 0, 0, 0, [DateTimeKind]::Utc)
    $expected = Get-ExpectedId $mock
    $store = [CopilotAudit.Tests.MemoryWindowStore]::new()
    $sink  = [CopilotAudit.Tests.MemorySink]::new()
    $logJ  = [System.Collections.Generic.List[string]]::new()
    $script:second = $null
    $tickJ = {
        param($e)
        if (-not $script:second -and $e.GetStatus().RowsWritten -gt 0) {
            $script:second = Invoke-EngineRun -Mock $mock -Options (New-EngineOption $mock $start $end) -Store $store -Sink $sink -Log $logJ -TimeoutSec 30
        }
    }
    $first = Invoke-EngineRun -Mock $mock -Options (New-EngineOption $mock $start $end) -Store $store -Sink $sink -Log $logJ -OnTick $tickJ
    $why = if ($script:second -and $script:second.Task.Exception) { $script:second.Task.Exception.GetBaseException().Message } else { '' }
    Assert-That ($null -ne $script:second -and $script:second.Task.IsFaulted -and $why -match 'Another run is loading') "the second run stopped before planning anything ($why)"
    Assert-That ($first.Task.Status -eq 'RanToCompletion') "the first ran to completion ($($first.Task.Status))"
    [void](Test-FinalSet -Store $store -Sink $sink -ExpectedIds $expected -Start $start -End $end -Label 'J')
    $after = Invoke-EngineRun -Mock $mock -Options (New-EngineOption $mock $start $end) -Store $store -Sink $sink -Log $logJ
    Assert-That ($after.Task.Status -eq 'RanToCompletion') 'and once it ended, the table was free for the next run'
}
finally { Stop-MockGraph $mock }

# ---------------------------------------------------------------------------
# K. Microsoft marks every search failed, yet each reports and serves its records
# ---------------------------------------------------------------------------

Write-Host "`nK. Every search ends 'failed' but still has its records" -ForegroundColor Cyan
$mock = Start-MockGraph @{ days = 4; fail_every_query = $true; failed_with_records = $true; page_503_rate = 0 }
try {
    $start = [datetime]::new(2026, 8, 1, 0, 0, 0, [DateTimeKind]::Utc)
    $end   = [datetime]::new(2026, 8, 5, 0, 0, 0, [DateTimeKind]::Utc)
    $expected = Get-ExpectedId $mock
    $store = [CopilotAudit.Tests.MemoryWindowStore]::new()
    $sink  = [CopilotAudit.Tests.MemorySink]::new()
    $logK = [System.Collections.Generic.List[string]]::new()
    $run = Invoke-EngineRun -Mock $mock -Options (New-EngineOption $mock $start $end) -Store $store -Sink $sink -Log $logK

    Assert-That ($run.Task.Status -eq 'RanToCompletion') "completed ($($run.Task.Status))"
    Assert-That (@($logK | Where-Object { $_ -match 'marked the search failed but it found' }).Count -ge 1) 'a failed search that found records is taken as complete, and the log says so'
    $retried = @($logK | Where-Object { $_ -match 'audit search failed' })
    Assert-That ($retried.Count -eq 0) "none of them is retried; one over the record cap is split as usual ($(@($store.Snapshot() | Where-Object Status -eq 'Split').Count) split) ($($retried | Select-Object -First 1))"
    $noted = @($store.Snapshot() | Where-Object { $_.Status -eq 'Loaded' -and $_.LastError -match 'loaded from a search Microsoft marked failed' })
    Assert-That ($noted.Count -ge 1 -and $noted.Count -eq @($store.Snapshot() | Where-Object Status -eq 'Loaded').Count) `
        "each window loaded from a failed search keeps a note in LastError ($($noted.Count))"
    [void](Test-FinalSet -Store $store -Sink $sink -ExpectedIds $expected -Start $start -End $end -Label 'K')
}
finally { Stop-MockGraph $mock }

# ---------------------------------------------------------------------------
# L. A failed search reports records but refuses to serve them
# ---------------------------------------------------------------------------

Write-Host "`nL. A failed search reports records, then refuses the download" -ForegroundColor Cyan
$mock = Start-MockGraph @{ days = 4; failed_with_records = $true; failed_download_refused = $true; page_503_rate = 0; fail_first_query_covering = '2026-08-02T12:00:00Z' }
try {
    $start = [datetime]::new(2026, 8, 1, 0, 0, 0, [DateTimeKind]::Utc)
    $end   = [datetime]::new(2026, 8, 5, 0, 0, 0, [DateTimeKind]::Utc)
    $expected = Get-ExpectedId $mock
    $store = [CopilotAudit.Tests.MemoryWindowStore]::new()
    $sink  = [CopilotAudit.Tests.MemorySink]::new()
    $logL = [System.Collections.Generic.List[string]]::new()
    $run = Invoke-EngineRun -Mock $mock -Options (New-EngineOption $mock $start $end) -Store $store -Sink $sink -Log $logL

    Assert-That ($run.Task.Status -eq 'RanToCompletion') "the load is not stopped: it completed ($($run.Task.Status)) $(if ($run.Task.Exception) { $run.Task.Exception.GetBaseException().Message })"
    Assert-That (@($logL | Where-Object { $_ -match 'records could not be downloaded.*searching it again' }).Count -ge 1) 'the window is searched again, and the log says why'
    [void](Test-FinalSet -Store $store -Sink $sink -ExpectedIds $expected -Start $start -End $end -Label 'L')
}
finally { Stop-MockGraph $mock }
# ---------------------------------------------------------------------------

Write-Host ''
if ($script:Failures.Count) {
    Write-Host "$($script:Failures.Count) assertion(s) failed." -ForegroundColor Red
    exit 1
}
Write-Host 'All engine tests passed.' -ForegroundColor Green
