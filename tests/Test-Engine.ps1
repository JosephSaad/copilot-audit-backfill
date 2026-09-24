<#
    End-to-end tests of the backfill engine against a mock Graph Audit Search
    service (mock_graph.py) and in-memory SQL stand-ins (TestDoubles.cs).

      0  RowBatchReader honours the IDataReader contract (loaded via DataTable.Load)
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
    $expected = Get-ExpectedIds $mock
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

    $run1 = Invoke-EngineRun -Mock $mock -Options (New-Options $mock $start $end) -Store $store -Sink $sink -OnTick $tick -Log $logA
    $afterRun1 = $store.Snapshot()
    $loaded1 = @($afterRun1 | Where-Object Status -eq 'Loaded').Count
    $partial1 = @($afterRun1 | Where-Object { $_.Status -in 'Downloading', 'Ready', 'Submitted' }).Count

    $run2 = Invoke-EngineRun -Mock $mock -Options (New-Options $mock $start $end) -Store $store -Sink $sink -Log $logA
    $stats = Get-MockStats $mock

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
    $expected = Get-ExpectedIds $mock
    $store = [CopilotAudit.Tests.MemoryWindowStore]::new()
    $sink  = [CopilotAudit.Tests.MemorySink]::new()
    $logB = [System.Collections.Generic.List[string]]::new()
    $run = Invoke-EngineRun -Mock $mock -Options (New-Options $mock $start $end) -Store $store -Sink $sink -Log $logB

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
    $opts = New-Options $mock ([datetime]::new(2026, 8, 1, 0, 0, 0, [DateTimeKind]::Utc)) ([datetime]::new(2026, 8, 31, 0, 0, 0, [DateTimeKind]::Utc)) @{ MaxSubmissionsPer24h = 4; InitialWindowHours = 12 }
    $script:t0 = [datetime]::UtcNow
    $run = Invoke-EngineRun -Mock $mock -Options $opts -Store $store -Sink $sink -Log $logC `
            -OnTick { param($e) if (([datetime]::UtcNow - $script:t0).TotalSeconds -gt 12) { $e.RequestStop() } }
    $stats = Get-MockStats $mock
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
    $opts = New-Options $mock ([datetime]::new(2026, 8, 1, 0, 0, 0, [DateTimeKind]::Utc)) ([datetime]::new(2026, 8, 31, 0, 0, 0, [DateTimeKind]::Utc))
    $script:t0 = [datetime]::UtcNow
    $run = Invoke-EngineRun -Mock $mock -Options $opts -Store $store -Sink $sink -Log $logD `
            -OnTick { param($e) if (([datetime]::UtcNow - $script:t0).TotalSeconds -gt 12) { $e.RequestStop() } }
    $stats = Get-MockStats $mock
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
    $expected = Get-ExpectedIds $mock
    $store = [CopilotAudit.Tests.MemoryWindowStore]::new()
    $sink  = [CopilotAudit.Tests.MemorySink]::new()
    $logE = [System.Collections.Generic.List[string]]::new()
    $opts = New-Options $mock $start $end @{ TokenWaitMinutes = 0.05; InitialWindowHours = 24 }
    $run = Invoke-EngineRun -Mock $mock -Options $opts -Store $store -Sink $sink -Log $logE -NoTokenRefresh -TimeoutSec 60
    $snap = $store.Snapshot()
    $fault = if ($run.Task.Exception) { $run.Task.Exception.GetBaseException().Message } else { '' }

    Assert-That ($run.Task.IsFaulted) ("pipeline stopped instead of churning ({0}, {1:N1}s)" -f $run.Task.Status, $run.Seconds)
    Assert-That ($fault -match 'no new sign-in completed') 'the reason is stated plainly'
    Assert-That (@($snap | Where-Object Status -eq 'Failed').Count -eq 0) 'no window was written off as Failed'

    $opts2 = New-Options $mock $start $end @{ InitialWindowHours = 24 }
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

    $expected = Get-ExpectedIds $mock
    $store = [CopilotAudit.Tests.MemoryWindowStore]::new()
    $sink  = [CopilotAudit.Tests.MemorySink]::new()
    $logF  = [System.Collections.Generic.List[string]]::new()

    $opts = New-Options $mock $start $end @{ InitialWindowHours = 24 }
    $runF = Invoke-EngineRun -Mock $mock -Options $opts -Store $store -Sink $sink -Log $logF
    $statsF = Get-MockStats $mock

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

Write-Host ''
if ($script:Failures.Count) {
    Write-Host "$($script:Failures.Count) assertion(s) failed." -ForegroundColor Red
    exit 1
}
Write-Host 'All engine tests passed.' -ForegroundColor Green
