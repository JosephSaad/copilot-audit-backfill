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
    '-ConnectionString is honoured rather than throwing'
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
Write-Host "`n3. Monitor loop: progress + stale-token re-authentication" -ForegroundColor Cyan

$mock = Start-MockGraph @{ days = 8; rotate_after_pages = 40; fail_first_query_covering = '2030-01-01T00:00:00Z' }
try {
    $start = [datetime]::new(2026, 8, 1, 0, 0, 0, [DateTimeKind]::Utc)
    $end   = [datetime]::new(2026, 8, 9, 0, 0, 0, [DateTimeKind]::Utc)
    $expected = Get-ExpectedIds $mock
    $store = [CopilotAudit.Tests.MemoryWindowStore]::new()
    $sink  = [CopilotAudit.Tests.MemorySink]::new()
    $engine = [CopilotAudit.BackfillEngine]::new((New-Options $mock $start $end @{ InitialWindowHours = 24 }), $store, $sink, $null)

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
    $expected = Get-ExpectedIds $mock
    $store = [CopilotAudit.Tests.MemoryWindowStore]::new()
    $sink  = [CopilotAudit.Tests.MemorySink]::new()
    $engine1 = [CopilotAudit.BackfillEngine]::new((New-Options $mock $start $end @{ InitialWindowHours = 24 }), $store, $sink, $null)
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
    $pagesAfterStop = (Get-MockStats $mock).pages_served
    Start-Sleep -Seconds 3
    $pagesLater = (Get-MockStats $mock).pages_served
    $snap = $store.Snapshot()

    Assert-That ($rowsAtStop -ge 60000) ("stopped mid-run at {0:N0} rows" -f $rowsAtStop)
    Assert-That (@($stopLines | Where-Object { $_ -match 'Stopping - letting in-flight SQL batches finish' }).Count -eq 1) 'announced a clean stop'
    Assert-That ($pagesLater -eq $pagesAfterStop) "engine really stopped downloading ($pagesAfterStop -> $pagesLater pages)"
    Assert-That (@($snap | Where-Object Status -ne 'Loaded').Count -gt 0) 'work was left for the resume'
    $engine1.Dispose()

    $engine2 = [CopilotAudit.BackfillEngine]::new((New-Options $mock $start $end @{ InitialWindowHours = 24 }), $store, $sink, $null)
    $script:CurrentGraphToken = Get-MockToken $mock
    $engine2.SetAccessToken($script:CurrentGraphToken)
    $final = Wait-BackfillEngine -Engine $engine2 -ProgressSeconds 60 -PollMilliseconds 200 -GetToken { Get-MockToken $mock } 6>$null
    $engine2.Dispose()

    Assert-That ($final.Phase -eq 'Complete') 'resume ran to completion'
    [void](Test-FinalSet -Store $store -Sink $sink -ExpectedIds $expected -Start $start -End $end -Label '4')
}
finally { Stop-MockGraph $mock }

# ---------------------------------------------------------------------------
Write-Host ''
if ($script:Failures.Count) {
    Write-Host "$($script:Failures.Count) assertion(s) failed." -ForegroundColor Red
    exit 1
}
Write-Host 'All orchestrator tests passed.' -ForegroundColor Green
