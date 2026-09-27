<#
    Shared helpers for Test-Engine.ps1 and Test-Orchestrator.ps1: mock Graph
    server lifecycle, engine options, expected-record generation, and the
    finalize-rule verifier.
#>

$script:Failures = [System.Collections.Generic.List[string]]::new()
function Assert-That {
    param([bool] $Condition, [string] $Message)
    if ($Condition) { Write-Host "  PASS  $Message" -ForegroundColor Green }
    else            { Write-Host "  FAIL  $Message" -ForegroundColor Red; $script:Failures.Add($Message) }
}

# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------

function Start-MockGraph {
    param([hashtable] $Config = @{})
    $portFile = [System.IO.Path]::GetTempFileName()
    Remove-Item $portFile
    $psi = [System.Diagnostics.ProcessStartInfo]::new('python3')
    $psi.ArgumentList.Add((Join-Path $PSScriptRoot 'mock_graph.py'))
    $psi.ArgumentList.Add($portFile)
    $psi.ArgumentList.Add(($Config | ConvertTo-Json -Compress))
    $psi.UseShellExecute = $false
    $proc = [System.Diagnostics.Process]::Start($psi)
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while (-not (Test-Path $portFile) -or -not (Get-Content $portFile -Raw)) {
        if ($sw.Elapsed.TotalSeconds -gt 15) { throw 'mock server did not start' }
        Start-Sleep -Milliseconds 100
    }
    $port = (Get-Content $portFile -Raw).Trim()
    return [pscustomobject]@{ Process = $proc; Base = "http://127.0.0.1:$port" }
}

function Stop-MockGraph($Mock) { if ($Mock -and -not $Mock.Process.HasExited) { $Mock.Process.Kill() } }

function Get-MockToken($Mock) { (Invoke-RestMethod "$($Mock.Base)/_control/token").token }
function Get-MockStatistic($Mock) { Invoke-RestMethod "$($Mock.Base)/_control/stats" }

function New-EngineOption {
    param($Mock, [datetime] $Start, [datetime] $End, [hashtable] $Overrides = @{})
    $o = [CopilotAudit.EngineOptions]::new()
    $o.GraphBaseUrl           = $Mock.Base
    $o.RangeStartUtc          = $Start
    $o.RangeEndUtc            = $End
    $o.InitialWindowHours     = 48
    $o.TargetRecordsPerWindow = 30000
    $o.AssumedRecordCap       = 50000
    $o.MaxQueriesInFlight     = 6
    $o.StatusPollSeconds      = 1
    $o.InitialDownloadStreams = 4
    $o.MaxDownloadStreams     = 8
    $o.IncreaseAfterCleanPages = 10
    $o.BatchRows              = 2000
    $o.SqlWriters             = 3
    $o.BatchQueueCapacity     = 8
    foreach ($k in $Overrides.Keys) { $o.$k = $Overrides[$k] }
    return $o
}

function Invoke-EngineRun {
    param($Mock, $Options, $Store, $Sink, [scriptblock] $OnTick, [int] $TimeoutSec = 240, [System.Collections.Generic.List[string]] $Log,
          [switch] $NoTokenRefresh)
    $engine = [CopilotAudit.BackfillEngine]::new($Options, $Store, $Sink, $null)
    $engine.SetAccessToken((Get-MockToken $Mock))
    $task = $engine.Start()
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $refreshes = 0
    while (-not $task.IsCompleted) {
        Start-Sleep -Milliseconds 200
        foreach ($l in $engine.DrainLog()) { $Log.Add($l) }
        if ($engine.TokenRefreshRequested -and -not $NoTokenRefresh) { $engine.SetAccessToken((Get-MockToken $Mock)); $refreshes++ }
        if ($OnTick) { & $OnTick $engine }
        if ($sw.Elapsed.TotalSeconds -gt $TimeoutSec) { $engine.RequestStop(); Start-Sleep 2; throw "engine run exceeded $TimeoutSec s" }
    }
    try { [void]$task.Wait(5000) } catch { Write-Verbose "The engine stopped with: $($_.Exception.Message)" }
    foreach ($l in $engine.DrainLog()) { $Log.Add($l) }
    $status = $engine.GetStatus()
    $engine.Dispose()
    return [pscustomobject]@{ Task = $task; Status = $status; Seconds = $sw.Elapsed.TotalSeconds; Refreshes = $refreshes }
}

function Get-ExpectedId($Mock) {
    $dc = Invoke-RestMethod "$($Mock.Base)/_control/daycounts"
    $start = [datetime]::Parse($dc.range_start, [cultureinfo]::InvariantCulture, [System.Globalization.DateTimeStyles]::AdjustToUniversal)
    $set = [System.Collections.Generic.HashSet[string]]::new()
    for ($i = 0; $i -lt $dc.counts.Count; $i++) {
        $prefix = $start.AddDays($i).ToString('yyyyMMdd')
        for ($j = 0; $j -lt $dc.counts[$i]; $j++) { [void]$set.Add(('{0}-{1:D6}' -f $prefix, $j)) }
    }
    return ,$set
}

function Test-FinalSet {
    <#
        Applies the finalize rule (Loaded windows, final attempt only, dedupe by
        RecordId) and compares with the expected record set.
    #>
    param($Store, $Sink, $ExpectedIds, [datetime] $Start, [datetime] $End, [string] $Label)

    $windows = $Store.Snapshot()
    $byId = @{}
    foreach ($w in $windows) { $byId[$w.WindowId] = $w }

    $loaded = @($windows | Where-Object Status -eq 'Loaded' | Sort-Object StartUtc)
    $failed = @($windows | Where-Object Status -eq 'Failed')
    $gaps = 0
    $cursor = $Start
    foreach ($w in $loaded) {
        if ($w.StartUtc -ne $cursor) { $gaps++ }
        $cursor = $w.EndUtc
    }
    if ($cursor -ne $End) { $gaps++ }

    $final = [System.Collections.Generic.HashSet[string]]::new()
    $finalRows = 0
    $abandoned = 0
    foreach ($r in $Sink.Rows) {
        $w = $byId[$r.WindowId]
        if ($w.Status -eq 'Loaded' -and $r.Attempt -eq $w.Attempts) {
            $finalRows++
            [void]$final.Add($r.RecordId)
        }
        else { $abandoned++ }
    }
    $missing = 0; foreach ($id in $ExpectedIds) { if (-not $final.Contains($id)) { $missing++ } }
    $extra   = 0; foreach ($id in $final)       { if (-not $ExpectedIds.Contains($id)) { $extra++ } }

    Assert-That ($failed.Count -eq 0) "[$Label] no windows ended Failed ($($failed.Count))"
    Assert-That ($gaps -eq 0) "[$Label] $($loaded.Count) loaded windows tile the range with no gaps or overlaps"
    Assert-That ($missing -eq 0) ("[$Label] every expected record is in the final set ({0:N0} expected, {1:N0} missing)" -f $ExpectedIds.Count, $missing)
    Assert-That ($extra -eq 0) "[$Label] no unexpected records ($extra)"
    Assert-That ($finalRows -eq $final.Count) ("[$Label] no duplicates within final attempts ({0:N0} rows, {1:N0} distinct)" -f $finalRows, $final.Count)
    Assert-That ($Sink.Violations.Count -eq 0) "[$Label] every row passed SQL-style validation ($($Sink.Violations.Count) violations)"
    return [pscustomobject]@{ Windows = $windows; Loaded = $loaded; Abandoned = $abandoned; FinalRows = $finalRows }
}
