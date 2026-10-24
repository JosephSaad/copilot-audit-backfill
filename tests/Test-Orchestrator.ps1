<#
    Tests the PowerShell orchestrator's own logic (Invoke-CopilotAuditBackfill.ps1),
    dot-sourced so nothing runs against SQL or Graph for real:

      1  Get-GraphAccessToken reads the bearer token off an SDK response
      2  ConvertTo-UtcDate treats unzoned dates as UTC
      3  Wait-BackfillEngine end to end: progress lines, and the stale-token path -
         Graph rejects the token, the cache keeps returning it, the monitor
         signs in again and the run completes
      4  Ctrl+C: stopping the pipeline mid-run (a real PipelineStoppedException in
         a separate runspace) stops the engine cleanly; a second run resumes and
         the final record set is exact
      5  a backfill whose download gave up on a window fails the run, and neither
         completes nor finalizes the range

    Requires python3 on PATH.
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Split-Path $PSScriptRoot -Parent
$orchestrator = Join-Path $root 'Invoke-CopilotAuditBackfill.ps1'
. $orchestrator                                      # functions only - the guard skips Invoke-Main
Import-CopilotAuditEngine -ExtraSource (Join-Path $PSScriptRoot 'TestDoubles.cs') 3>$null
. (Join-Path $PSScriptRoot 'TestHelpers.ps1')

# ---------------------------------------------------------------------------
Write-Host "`n1. Token hand-off from the Graph SDK" -ForegroundColor Cyan

function global:Invoke-MgGraphRequest {
    param($Method, $Uri, $OutputType, [switch] $SkipHttpErrorCheck)
    $req = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::Get, "https://graph.microsoft.com$Uri")
    $req.Headers.Authorization = [System.Net.Http.Headers.AuthenticationHeaderValue]::new('Bearer', 'eyJ.fake.token')
    $resp = [System.Net.Http.HttpResponseMessage]::new([System.Net.HttpStatusCode]::BadRequest)   # status must not matter
    $resp.RequestMessage = $req
    $script:LastMgCall = @{ Uri = $Uri; OutputType = $OutputType; Skip = [bool]$SkipHttpErrorCheck }
    return $resp
}
$tok = Get-GraphAccessToken
Assert-That ($tok -eq 'eyJ.fake.token') 'bearer token read from the request message'
Assert-That ($script:LastMgCall.OutputType -eq 'HttpResponseMessage' -and $script:LastMgCall.Skip) 'asked the SDK for the raw response and skipped the HTTP error check'
Remove-Item function:\Invoke-MgGraphRequest

# ---------------------------------------------------------------------------
Write-Host "`n1b. Connection string building" -ForegroundColor Cyan

# -ConnectionString threw for every release up to 1.1.1: the builder is a
# DbConnectionStringBuilder, and PowerShell routes an assignment to
# .ConnectionString through its keyword indexer, which rejects
# 'ConnectionString' as a keyword.
$ConnectionString = 'Data Source=SQLDEV01;Initial Catalog=CopilotAuditTest;Integrated Security=True'
$built = New-SqlConnectionString
Assert-That ($built -match 'SQLDEV01' -and $built -match 'CopilotAuditTest') `
    '-ConnectionString is honored rather than throwing'
Assert-That ($built -match 'Application Name=CopilotAuditBackfill') `
    'the application name is still stamped onto a supplied connection string'

$ConnectionString = $null
$SqlServer = 'SQLPROD01'; $Database = 'CopilotAudit'; $TrustServerCertificate = $true
$built2 = New-SqlConnectionString
Assert-That ($built2 -match 'SQLPROD01' -and $built2 -match 'CopilotAudit') `
    '-SqlServer/-Database still build a connection string'
Assert-That ($built2 -match 'TrustServerCertificate=True') '-TrustServerCertificate is applied'
$SqlServer = $null; $Database = $null; $TrustServerCertificate = $false

# ---------------------------------------------------------------------------
Write-Host "`n2. Date handling" -ForegroundColor Cyan

$u = ConvertTo-UtcDate ([datetime]::new(2025, 9, 22))
Assert-That ($u.Kind -eq 'Utc' -and $u.Hour -eq 0) 'an unzoned date is taken as UTC midnight, not local'
$l = ConvertTo-UtcDate ([datetime]::new(2025, 9, 22, 8, 0, 0, [DateTimeKind]::Local))
Assert-That ($l.Kind -eq 'Utc' -and $l -eq [datetime]::new(2025, 9, 22, 8, 0, 0, [DateTimeKind]::Local).ToUniversalTime()) 'a local time is converted'

# ---------------------------------------------------------------------------
Write-Host "`n2b. Incremental load range" -ForegroundColor Cyan

# The base comes back from SQL with Kind Unspecified; it is UTC.
$base = [datetime]::new(2026, 9, 23, 0, 0, 0, [DateTimeKind]::Unspecified)
$now  = [datetime]::new(2026, 9, 24, 13, 37, 12, [DateTimeKind]::Utc)

$r = Resolve-LoadRange -BaseEndUtc $base -NowUtc $now -LookbackHours 48
Assert-That ($r.Start -eq [datetime]::new(2026, 9, 21, 0, 0, 0, [DateTimeKind]::Utc) -and $r.Start.Kind -eq 'Utc') `
    "a load starts the lookback before where the data ends ($($r.Start.ToString('u')))"
Assert-That ($r.End -eq [datetime]::new(2026, 9, 24, 13, 0, 0, [DateTimeKind]::Utc)) `
    "and ends at the start of the current hour, not mid-hour ($($r.End.ToString('u')))"

$r0 = Resolve-LoadRange -BaseEndUtc $base -NowUtc $now -LookbackHours 0
Assert-That ($r0.Start -eq [datetime]::new(2026, 9, 23, 0, 0, 0, [DateTimeKind]::Utc)) 'a zero lookback starts exactly where the data ends'

$rEnd = Resolve-LoadRange -BaseEndUtc $base -NowUtc $now -LookbackHours 48 -EndUtc ([datetime]::new(2026, 9, 24, 6, 30, 0, [DateTimeKind]::Utc))
Assert-That ($rEnd.End -eq [datetime]::new(2026, 9, 24, 6, 30, 0, [DateTimeKind]::Utc)) 'an explicit end is honored to the minute'

$none = Resolve-LoadRange -BaseEndUtc $base -NowUtc ([datetime]::new(2026, 9, 23, 0, 40, 0, [DateTimeKind]::Utc)) -LookbackHours 48
Assert-That ($null -eq $none) 'nothing to load when the current hour has not moved past the data'

# Upgrading: a license database backfilled with the four license operations, loaded by 2.3.0 with Copilot Studio's too.
$licOps = 'Add user,Change user license,Delete user,Update user'
$wider = Get-OperationsChange -Recorded $licOps -Requested @('Change user license', 'Update user', 'Add user', 'Delete user', 'BotCreate', 'BotUpdateOperation-BotPublish')
Assert-That ($null -ne $wider -and ($wider -join ',') -eq 'BotCreate,BotUpdateOperation-BotPublish') "a load may ask for more operations than the database recorded ($($wider -join ', '))"
$same = Get-OperationsChange -Recorded $licOps -Requested @('Update user', 'Delete user', 'Change user license', 'Add user')
Assert-That ($null -ne $same -and $same.Count -eq 0) 'the same operations in another order add nothing'
Assert-That ($null -eq (Get-OperationsChange -Recorded $licOps -Requested @('Add user', 'Update user'))) 'but never fewer: a load that would drop one is refused'
Assert-That ($null -eq (Get-OperationsChange -Recorded 'CopilotInteraction' -Requested @('Add user'))) 'nor different ones'

# A database loaded by record type records each as rt:<type>, so it is never resumed or loaded with operations.
$rt = Get-AuditFilterSet -Operations @() -RecordTypes @('PowerBIAudit', 'ConnectedAIAppInteraction', 'PowerAppsApp')
Assert-That (($rt -join ',') -eq 'rt:ConnectedAIAppInteraction,rt:PowerAppsApp,rt:PowerBIAudit') "record types are recorded as rt:<type>, in order ($($rt -join ','))"
Assert-That (((Get-AuditFilterSet -Operations @('CopilotInteraction') -RecordTypes $null) -join ',') -eq 'CopilotInteraction') 'a Copilot database records what it always did'
Assert-That ($null -eq (Get-OperationsChange -Recorded 'CopilotInteraction' -Requested $rt)) 'a record-type load into a Copilot database is refused'
Assert-That ($null -eq (Get-OperationsChange -Recorded ($rt -join ',') -Requested @('CopilotInteraction'))) 'and a Copilot load into a record-type database'
$more = Get-OperationsChange -Recorded 'rt:PowerBIAudit' -Requested (Get-AuditFilterSet -RecordTypes @('PowerBIAudit', 'PowerAppsApp'))
Assert-That (($more -join ',') -eq 'rt:PowerAppsApp') 'a record type can be added to an activity database, from then on'
$refused = try { Get-AuditFilterSet -Operations @() -RecordTypes @() | Out-Null; $false } catch { $true }
Assert-That $refused 'nothing to load is refused'

# ---------------------------------------------------------------------------
Write-Host "`n3. Monitor loop: progress + stale-token re-authentication" -ForegroundColor Cyan

$mock = Start-MockGraph @{ days = 8; rotate_after_pages = 40; fail_first_query_covering = '2030-01-01T00:00:00Z' }
try {
    $start = [datetime]::new(2026, 8, 1, 0, 0, 0, [DateTimeKind]::Utc)
    $end   = [datetime]::new(2026, 8, 9, 0, 0, 0, [DateTimeKind]::Utc)
    $expected = Get-ExpectedId $mock
    $store = [CopilotAudit.Tests.MemoryWindowStore]::new()
    $sink  = [CopilotAudit.Tests.MemorySink]::new()
    $engine = [CopilotAudit.BackfillEngine]::new((New-EngineOption $mock $start $end @{ InitialWindowHours = 24 }), $store, $sink, $null)

    # The "token cache" keeps handing back the old token until we sign in again -
    # exactly what happens when a session is revoked but the cached token has not expired.
    $script:TokenCache = Get-MockToken $mock
    $script:Reauths = 0
    $script:CurrentGraphToken = $script:TokenCache
    $engine.SetAccessToken($script:TokenCache)

    $out = Wait-BackfillEngine -Engine $engine -ProgressSeconds 2 -PollMilliseconds 200 `
            -GetToken { $script:TokenCache } `
            -Reauthenticate { $script:Reauths++; $script:TokenCache = Get-MockToken $mock } 6>&1
    $engine.Dispose()

    $lines  = @($out | Where-Object { $_ -is [System.Management.Automation.InformationRecord] } | ForEach-Object { "$($_.MessageData)" })
    $finalStatus = @($out | Where-Object { $_ -is [CopilotAudit.EngineStatus] })[-1]
    $progress = @($lines | Where-Object { $_ -match 'covered \|' })

    Assert-That ($script:Reauths -eq 1) "re-authenticated exactly once after the token went stale ($script:Reauths)"
    Assert-That (@($lines | Where-Object { $_ -match 'keeps rejecting the cached token' }).Count -eq 1) 'explained why it signed in again'
    Assert-That ($progress.Count -ge 2) "printed progress lines ($($progress.Count))"
    Assert-That (@($progress | Where-Object { $_ -match '\{\d' }).Count -eq 0) 'progress lines fully formatted'
    Assert-That ($null -ne $finalStatus -and $finalStatus.Phase -eq 'Complete') "returned the final status ($($finalStatus.Phase))"
    [void](Test-FinalSet -Store $store -Sink $sink -ExpectedIds $expected -Start $start -End $end -Label '3')
}
finally { Stop-MockGraph $mock }

# ---------------------------------------------------------------------------
Write-Host "`n4. Ctrl+C mid-run, then resume" -ForegroundColor Cyan

$mock = Start-MockGraph @{ days = 12; fail_first_query_covering = '2030-01-01T00:00:00Z'; page_latency_ms = 40 }
try {
    $start = [datetime]::new(2026, 8, 1, 0, 0, 0, [DateTimeKind]::Utc)
    $end   = [datetime]::new(2026, 8, 13, 0, 0, 0, [DateTimeKind]::Utc)
    $expected = Get-ExpectedId $mock
    $store = [CopilotAudit.Tests.MemoryWindowStore]::new()
    $sink  = [CopilotAudit.Tests.MemorySink]::new()
    $engine1 = [CopilotAudit.BackfillEngine]::new((New-EngineOption $mock $start $end @{ InitialWindowHours = 24 }), $store, $sink, $null)
    $engine1.SetAccessToken((Get-MockToken $mock))

    # Run the real monitor loop in its own runspace, then stop that pipeline -
    # the same PipelineStoppedException Ctrl+C raises.
    $child = [powershell]::Create()
    [void] $child.AddScript({
        param($Engine, $Base, $ScriptPath)
        . $ScriptPath
        $script:CurrentGraphToken = (Invoke-RestMethod "$Base/_control/token").token
        Wait-BackfillEngine -Engine $Engine -ProgressSeconds 60 -PollMilliseconds 200 `
            -GetToken { (Invoke-RestMethod "$Base/_control/token").token }
    }).AddArgument($engine1).AddArgument($mock.Base).AddArgument($orchestrator)
    $handle = $child.BeginInvoke()

    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($engine1.GetStatus().RowsWritten -lt 60000 -and $sw.Elapsed.TotalSeconds -lt 120) { Start-Sleep -Milliseconds 200 }
    $rowsAtStop = $engine1.GetStatus().RowsWritten
    $child.Stop()                                   # <- Ctrl+C
    $stopLines = @($child.Streams.Information | ForEach-Object { "$($_.MessageData)" })
    $child.Dispose()

    Start-Sleep -Milliseconds 1500      # let the mock finish any response the engine already hung up on
    $pagesAfterStop = (Get-MockStatistic $mock).pages_served
    Start-Sleep -Seconds 3
    $pagesLater = (Get-MockStatistic $mock).pages_served
    $snap = $store.Snapshot()

    Assert-That ($rowsAtStop -ge 60000) ("stopped mid-run at {0:N0} rows" -f $rowsAtStop)
    Assert-That (@($stopLines | Where-Object { $_ -match 'Stopping - letting in-flight SQL batches finish' }).Count -eq 1) 'announced a clean stop'
    Assert-That ($pagesLater -eq $pagesAfterStop) "engine really stopped downloading ($pagesAfterStop -> $pagesLater pages)"
    Assert-That (@($snap | Where-Object Status -ne 'Loaded').Count -gt 0) 'work was left for the resume'
    $engine1.Dispose()

    $engine2 = [CopilotAudit.BackfillEngine]::new((New-EngineOption $mock $start $end @{ InitialWindowHours = 24 }), $store, $sink, $null)
    $script:CurrentGraphToken = Get-MockToken $mock
    $engine2.SetAccessToken($script:CurrentGraphToken)
    $final = Wait-BackfillEngine -Engine $engine2 -ProgressSeconds 60 -PollMilliseconds 200 -GetToken { Get-MockToken $mock } 6>$null
    $engine2.Dispose()

    Assert-That ($final.Phase -eq 'Complete') 'resume ran to completion'
    [void](Test-FinalSet -Store $store -Sink $sink -ExpectedIds $expected -Start $start -End $end -Label '4')
}
finally { Stop-MockGraph $mock }

# ---------------------------------------------------------------------------
Write-Host "`n5. A backfill that gave up on a window fails the run (2.4.12)" -ForegroundColor Cyan

# Invoke-Main with everything around the download stubbed, so only its own decision is under test. It logged the
# failed windows and returned, so the script exited 0 and -History carried on as if the history had loaded.
& {
    function Import-CopilotAuditEngine { }
    function New-SqlConnectionString { 'stub' }
    function Initialize-CopilotAuditDatabase { }
    function Enter-RunLock { }
    function Resolve-BackfillRange { [pscustomobject]@{ Start = [datetime]::new(2026, 8, 1, 0, 0, 0, [DateTimeKind]::Utc); End = [datetime]::new(2026, 8, 9, 0, 0, 0, [DateTimeKind]::Utc); Resumed = $true; Completed = $false } }
    function Connect-AuditGraph { }
    function Get-GraphAccessToken { 'tok' }
    function Show-BackfillState { }
    function Invoke-AuditDownload { [pscustomobject]@{ WindowsFailed = $script:Failed; OldestRecordUtc = $null } }
    function Invoke-CopilotAuditFinalize { $script:Finalized++ }
    function Invoke-Sql { param([string] $Sql, [string] $As, [hashtable] $Parameters, [int] $TimeoutSeconds) $script:SqlRun.Add($Sql) }
    $LogPath = Join-Path ([System.IO.Path]::GetTempPath()) "orchestrator-$PID.log"
    $TenantId = 'contoso.onmicrosoft.com'; $Finalize = $true
    try {
        $script:Failed = 2; $script:Finalized = 0; $script:SqlRun = [System.Collections.Generic.List[string]]::new()
        $err = try { Invoke-Main 6>$null; '' } catch { $_.Exception.Message }
        Assert-That ($err -like '2 window(s) failed after retries*Run the same command again*') "the run throws, so the step exits non-zero and the pipeline stops there ($err)"
        Assert-That (@($script:SqlRun | Where-Object { $_ -like '*CompletedUtc*' }).Count -eq 0 -and $script:Finalized -eq 0) 'and the range is neither marked complete nor finalized'

        $script:Failed = 0; $script:Finalized = 0; $script:SqlRun.Clear()
        Invoke-Main 6>$null
        Assert-That (@($script:SqlRun | Where-Object { $_ -like '*SET CompletedUtc*' }).Count -eq 1 -and $script:Finalized -eq 1) 'with every window loaded it completes and finalizes, as before'
    }
    finally {
        $script:LogFile = $null
        Remove-Item -LiteralPath $LogPath -ErrorAction SilentlyContinue
    }
}

# ---------------------------------------------------------------------------
Write-Host ''
if ($script:Failures.Count) {
    Write-Host "$($script:Failures.Count) assertion(s) failed." -ForegroundColor Red
    exit 1
}
Write-Host 'All orchestrator tests passed.' -ForegroundColor Green
