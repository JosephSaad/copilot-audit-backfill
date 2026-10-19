<#
    Tests the Power Apps activity load (2.6.9), tools\Import-PowerAppsActivity.ps1, dot-sourced so nothing touches
    Entra, the certificate store or SQL Server:

      1  the range a load searches, the search it submits, a window split in two, a record's time
      2  records as rows: one a person whatever the capitals, system actions left out, the last time, the days
      3  against a stand-in for Graph's audit search (mock_graph.py): every record read, a page at a time; a
         window over the record cap split until each half fits, with the same people and records; no beta status
      4  Invoke-CopilotPipeline.ps1: the optional nightly step, -PowerAppsActivity alone, and the skip note
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot 'TestHelpers.ps1')
function Test-Throw([scriptblock] $Do) { try { & $Do | Out-Null; $false } catch { $true } }

# ---- 1. The searches -----------------------------------------------------------------------------
Write-Host "`n1. The range and the searches" -ForegroundColor Cyan
. (Join-Path $root 'tools' 'Import-PowerAppsActivity.ps1')
$utc = [System.DateTimeKind]::Utc
$range = Get-ActivityRange -Days 30 -Today ([datetime] '2026-10-20 15:30')
Assert-That ($range.From -eq [datetime]::new(2026, 9, 20, 0, 0, 0, $utc) -and $range.To -eq [datetime]::new(2026, 10, 20, 0, 0, 0, $utc) -and $range.To.Kind -eq $utc) `
    '30 days are the 30 whole UTC days ending yesterday'
$body = Get-ActivityQueryBody -From $range.From -To $range.To
Assert-That ((@($body.recordTypeFilters) -join ',') -eq 'powerAppsApp' -and -not $body.Contains('operationFilters') -and
             $body.filterStartDateTime -eq '2026-09-20T00:00:00Z' -and $body.filterEndDateTime -eq '2026-10-20T00:00:00Z' -and
             $body.'@odata.type' -eq '#microsoft.graph.security.auditLogQuery') `
    'the search asks for Power Apps'' records only, every operation, for exactly the range'
$halves = Split-ActivityWindow -From $range.From -To $range.To
Assert-That ($halves.Count -eq 2 -and $halves[0].From -eq $range.From -and $halves[0].To -eq $halves[1].From -and $halves[1].To -eq $range.To -and
             $halves[0].To -eq [datetime]::new(2026, 10, 5, 0, 0, 0, $utc)) 'a window over the cap splits into two halves that meet, on the hour'
$odd = Split-ActivityWindow -From ([datetime] '2026-10-01 00:00') -To ([datetime] '2026-10-01 03:00')
Assert-That ($odd.Count -eq 2 -and $odd[0].To -eq [datetime] '2026-10-01 01:00') 'three hours split as one and two'
Assert-That ((Split-ActivityWindow -From ([datetime] '2026-10-01 00:00') -To ([datetime] '2026-10-01 01:00')).Count -eq 0) 'an hour is not split further'
$t1 = ConvertTo-UtcTime '2026-10-05T23:30:00Z'
$t2 = ConvertTo-UtcTime '2026-10-06T01:30:00+02:00'
Assert-That ($t1 -eq [datetime]::new(2026, 10, 5, 23, 30, 0, $utc) -and $t1.Kind -eq $utc -and $t2 -eq $t1) 'a record''s time is read as UTC, whatever zone it is written in'
Assert-That ((ConvertTo-UtcTime ([datetime]::SpecifyKind([datetime] '2026-10-05 23:30', $utc))) -eq $t1 -and $null -eq (ConvertTo-UtcTime 'not a time')) `
    'one already read as a date stays as it is, and text that is no time is nothing'
Assert-That ((Get-GraphError -Status 403 -Body ([pscustomobject]@{ error = [pscustomobject]@{ code = 'Forbidden'; message = 'no' } }) -What 'the audit search') -like '*Forbidden: no.*AuditLogsQuery.Read.All*') `
    'a refusal names Graph''s reason and the permission the app needs'

# ---- 2. Records as rows --------------------------------------------------------------------------
Write-Host "`n2. Records as rows" -ForegroundColor Cyan
$since = [datetime]::new(2026, 9, 20, 0, 0, 0, $utc)
$agg = New-ActivityAggregate
$recs = @(
    [pscustomobject]@{ userPrincipalName = 'Ana@Contoso.com'; createdDateTime = '2026-10-01T08:00:00Z' }
    [pscustomobject]@{ userPrincipalName = 'ana@contoso.com'; createdDateTime = '2026-10-01T17:00:00Z' }
    [pscustomobject]@{ userPrincipalName = 'ana@contoso.com'; createdDateTime = '2026-10-03T09:15:42.678Z' }
    [pscustomobject]@{ userPrincipalName = $null; userId = 'bo@contoso.com'; createdDateTime = '2026-09-01T10:00:00Z' }
    [pscustomobject]@{ userPrincipalName = $null; userId = 'PowerApps Service'; createdDateTime = '2026-10-02T10:00:00Z' }
    [pscustomobject]@{ userPrincipalName = 'cy@contoso.com' }
)
$counted = @($recs | ForEach-Object { Add-ActivityRecord -Aggregate $agg -Record $_ -Since30 $since })
Assert-That (($counted -join ',') -eq 'True,True,True,True,False,False') 'a record naming nobody with an @, or with no time, is left out'
$rows = ConvertTo-ActivityRow $agg
Assert-That ($rows.Count -eq 2 -and $rows[0].UserPrincipalName -eq 'ana@contoso.com' -and $rows[1].UserPrincipalName -eq 'bo@contoso.com' -and
             @($rows | Where-Object Product -ne 'Power Apps').Count -eq 0) 'one row a person, whatever the capitals, by userId where there is no userPrincipalName, product Power Apps'
Assert-That ($rows[0].LastActiveUtc -eq [datetime]::new(2026, 10, 3, 9, 15, 42, $utc) -and $rows[0].ActiveDays30 -eq 2) 'the last time, to the second, and two days active from three records'
Assert-That ($rows[1].LastActiveUtc -eq [datetime]::new(2026, 9, 1, 10, 0, 0, $utc) -and $rows[1].ActiveDays30 -eq 0) `
    'someone seen only before the last 30 days keeps when, with no day active in the 30: an idle license for the cost page'
Assert-That ((ConvertTo-ActivityRow (New-ActivityAggregate)).Count -eq 0) 'and no records are no rows'
$table = ConvertTo-UsageTable $rows ([ordered]@{ UserPrincipalName = [string]; Product = [string]; LastActiveUtc = [datetime]; ActiveDays30 = [int] })
Assert-That ($table.Rows.Count -eq 2 -and $table.Columns.Count -eq 4 -and $table.Rows[0]['ActiveDays30'] -eq 2) 'as the table the shared writer bulk-copies'

# ---- 3. Against Graph ----------------------------------------------------------------------------
Write-Host "`n3. Against a stand-in for Graph's audit search" -ForegroundColor Cyan
$from = [datetime]::new(2026, 8, 1, 0, 0, 0, $utc); $to = [datetime]::new(2026, 8, 7, 0, 0, 0, $utc)
$cfg = @{ days = 6; first_day_count = 4000; last_day_count = 8000; page_503_rate = 0; polls_to_succeed = 2; page_latency_ms = 0 }
$whole = $null; $mock = $null
try {
    $mock = Start-MockGraph -Config $cfg
    $script:UseBeta = $true
    $whole = Invoke-ActivitySearch -Graph $mock.Base -BearerToken (Get-MockToken $mock) -From $from -To $to -MaxSearches 5 -PollSeconds 1 -TimeoutMinutes 5 6>$null
    $stats = Get-MockStatistic $mock
    $people = ConvertTo-ActivityRow $whole.Aggregate
    Assert-That ($whole.Searches -eq 1 -and $stats.powerapps_searches -eq 1) 'a range under the cap is one search'
    Assert-That ($whole.Records -gt 1000 -and $whole.Records -eq $stats.records_served) "every record is read, a page at a time ($($whole.Records) records)"
    Assert-That ($people.Count -eq 30 -and @($people | Where-Object { $_.UserPrincipalName -cne $_.UserPrincipalName.ToLowerInvariant() }).Count -eq 0) `
        'thirty people, each once, the system actions left out'
    Assert-That (@($people | Where-Object { $_.ActiveDays30 -lt 1 -or $_.ActiveDays30 -gt 6 -or $_.LastActiveUtc -ge $to -or $_.LastActiveUtc -lt $from }).Count -eq 0) `
        'each active on 1 to 6 of the 6 days, last seen inside the range'
    Assert-That ($whole.Notes.Count -eq 0) 'and nothing to note'
}
finally { Stop-MockGraph $mock }

$mock = $null
try {
    $mock = Start-MockGraph -Config ($cfg + @{ powerapps_cap = 400 })
    $script:UseBeta = $true
    $split = Invoke-ActivitySearch -Graph $mock.Base -BearerToken (Get-MockToken $mock) -From $from -To $to -MaxSearches 60 -PollSeconds 1 -TimeoutMinutes 5 6>$null
    $sp = ConvertTo-ActivityRow $split.Aggregate
    Assert-That ($split.Searches -gt 3) "a window over the cap is split until each part fits ($($split.Searches) searches)"
    Assert-That ($split.Records -eq $whole.Records -and ($sp.UserPrincipalName -join ',') -eq ($people.UserPrincipalName -join ',') -and
                 ($sp.LastActiveUtc -join ',') -eq ($people.LastActiveUtc -join ',') -and ($sp.ActiveDays30 -join ',') -eq ($people.ActiveDays30 -join ',')) `
        'and finds the same records and the same people, last seen and days active, as one search did'
    $script:UseBeta = $true
    $tooMany = $null
    try { $null = Invoke-ActivitySearch -Graph $mock.Base -BearerToken (Get-MockToken $mock) -From $from -To $to -MaxSearches 2 -PollSeconds 1 -TimeoutMinutes 5 6>$null } catch { $tooMany = "$_" }
    Assert-That ($tooMany -like '*more than 2 audit searches*') 'a range that needs more searches than -MaxSearches stops, loading nothing'
}
finally { Stop-MockGraph $mock }

$mock = $null
try {
    $mock = Start-MockGraph -Config ($cfg + @{ beta_supported = $false })
    $script:UseBeta = $true
    $v1 = Invoke-ActivitySearch -Graph $mock.Base -BearerToken (Get-MockToken $mock) -From $from -To $to -MaxSearches 5 -PollSeconds 1 -TimeoutMinutes 5 6>$null
    Assert-That ($v1.Records -eq $whole.Records -and $v1.Aggregate.Count -eq 30 -and ($v1.Notes -join ';') -like '*record cap could not be checked*') `
        'without the beta status the records still load, and the run says the cap could not be checked'
    $denied = $null
    try { $null = Invoke-ActivitySearch -Graph $mock.Base -BearerToken 'not-the-token' -From $from -To $to -MaxSearches 5 -PollSeconds 1 -TimeoutMinutes 5 6>$null } catch { $denied = "$_" }
    Assert-That ($denied -like 'Graph answered 401 for the audit search*') 'a token Graph refuses stops the run with Graph''s answer'
}
finally { Stop-MockGraph $mock }

# ---- 4. The pipeline -----------------------------------------------------------------------------
Write-Host "`n4. Invoke-CopilotPipeline.ps1" -ForegroundColor Cyan
. (Join-Path $root 'Invoke-CopilotPipeline.ps1')
$s = @{ TenantId = 't'; ClientId = 'c'; CertificateThumbprint = 'AB'; SqlServer = 'SQL1'; Database = 'CopilotAudit'; TrustServerCertificate = $true }
$today = [datetime] '2026-10-20'
Assert-That (@(Get-PipelineStep -Setting $s -Today $today -Root 'R:' | Where-Object { $_.Name -like 'Power Apps*' }).Count -eq 0) 'not in the settings, the nightly run has no Power Apps step'
Assert-That (@(Get-PipelineSkipNote $s | Where-Object { $_ -like 'Power Apps activity: skipped, not configured*PowerAppsActivity = $true*' }).Count -eq 1) `
    'and the log says it was skipped, naming the setting that turns it on'
$night = Get-PipelineStep -Setting ($s + @{ PowerAppsActivity = $true }) -Today $today -Root 'R:'
$p = $night[-1]
Assert-That ($p.Name -eq 'Power Apps activity' -and $p.Optional -and $p.Script -eq [System.IO.Path]::Combine('R:', 'tools/Import-PowerAppsActivity.ps1')) `
    'PowerAppsActivity = $true adds it to the nightly run, optional and last'
Assert-That ($p.Arguments.TenantId -eq 't' -and $p.Arguments.ClientId -eq 'c' -and $p.Arguments.CertificateThumbprint -eq 'AB' -and $p.Arguments.Database -eq 'CopilotAudit' -and
             $p.Arguments.SqlServer -eq 'SQL1' -and $p.Arguments.TrustServerCertificate -and -not $p.Arguments.Contains('Days')) `
    'signed in as the tenant''s own app, into its own database; the loader''s 30 days unless the settings say'
$withDays = (Get-PipelineStep -Setting ($s + @{ PowerAppsActivity = $true; PowerAppsActivityDays = 90 }) -Today $today -Root 'R:')[-1]
Assert-That ($withDays.Arguments.Days -eq 90) 'PowerAppsActivityDays sets the nightly days'
Assert-That (@(Get-PipelineStep -Setting ($s + @{ PowerAppsActivity = $true }) -History -Today $today | Where-Object { $_.Name -like 'Power Apps*' }).Count -eq 0) `
    'the history load leaves it out: -PowerAppsActivity loads its own'
$only = @(Get-PipelineStep -Setting $s -PowerAppsActivity -Days 180 -Today $today -Root 'R:')
Assert-That ($only.Count -eq 1 -and $only[0].Name -eq 'Power Apps activity' -and $only[0].Arguments.Days -eq 180) `
    '-PowerAppsActivity -Days 180 is that load alone, 180 days back, whether or not the nightly run has it'
$onlySet = @(Get-PipelineStep -Setting ($s + @{ PowerAppsActivityDays = 60; PowerAppsActivityMaxDropPercent = 25 }) -PowerAppsActivity -Today $today -Root 'R:')
Assert-That ($onlySet[0].Arguments.Days -eq 60 -and $onlySet[0].Arguments.MaxDropPercent -eq 25) 'without -Days, the settings'' days, and the delete guard''s percentage'
$tenants = Get-PipelineTenant -Setting ($s + @{ Tenants = @(@{ Name = 'Fabrikam'; TenantId = 'ft'; ClientId = 'fc'; Database = 'CopilotAudit_Fabrikam' }) })
$second = @(Get-PipelineStep -Setting $tenants[1] -PowerAppsActivity -Days 30 -Today $today -Root 'R:')[0]
Assert-That ($second.Arguments.TenantId -eq 'ft' -and $second.Arguments.ClientId -eq 'fc' -and $second.Arguments.Database -eq 'CopilotAudit_Fabrikam') `
    'each tenant loads its own, as its own app'
$both = $null
try { $History = $true; $PowerAppsActivity = $true; Invoke-Main } catch { $both = "$_" } finally { $History = $false; $PowerAppsActivity = $false }
Assert-That ($both -like '*-History or -PowerAppsActivity, not both*') '-History and -PowerAppsActivity together are refused'

# ---------------------------------------------------------------------------------------------------
Write-Host ''
if ($script:Failures.Count) {
    Write-Host "$($script:Failures.Count) Power Apps activity test(s) failed:" -ForegroundColor Red
    $script:Failures | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
    exit 1
}
Write-Host 'All Power Apps activity tests passed.' -ForegroundColor Green
