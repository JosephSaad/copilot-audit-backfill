<#
.SYNOPSIS
    Keeps dashboard/Invoke-InsightsPrecache.ps1 honest (2.5.1): when the daily pre-cache runs, what it says
    when it ends, and how the installer and the site are wired to it.

.DESCRIPTION
    The script needs IIS, Task Scheduler and the running site, so these tests run its own pieces without
    them. Its functions are taken from the script itself, by its syntax tree, so a change to the script is a
    change to what is tested:

      1. The schedule: from a server whose clock is in Eastern Time, in UTC, in London, on the US west
         coast or in Tokyo, the task's triggers, each checked against the hour after 07:00 Eastern, start
         exactly one run on every day of a year, through both changes of summer time.
      2. A time of day is HH:mm; anything else is refused with the reason.
      3. What it says at the end, and its exit code: 0 built, 2 built with pages over a second from memory,
         1 not built (a failed page, a tenant that failed, the cache off).
      4. The wiring: the installer copies the script into the site, keeps the precache folder through an
         update, lets the app pool and LOCAL SERVICE change it, and schedules the task; the site reads the
         file names the script writes.

.EXAMPLE
    pwsh -NoProfile -File tests/Test-Precache.ps1
#>
#Requires -Version 7.2
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot 'TestHelpers.ps1')

$scriptPath = Join-Path $root 'dashboard' 'Invoke-InsightsPrecache.ps1'
$text = [System.IO.File]::ReadAllText($scriptPath)
$tokens = $null; $parseErrors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile($scriptPath, [ref] $tokens, [ref] $parseErrors)
Assert-That ($parseErrors.Count -eq 0) "the script parses ($($parseErrors.Count) error(s))"
foreach ($name in 'ConvertTo-TimeOfDay', 'Get-PrecacheTriggerTime', 'Test-PrecacheWindow', 'Get-PrecacheSummary', 'Get-PrecacheProgress') {
    $found = $ast.Find({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name }, $true)
    if (-not $found) { throw "function $name is not in $scriptPath" }
    . ([scriptblock]::Create($found.Extent.Text))
}

# ---------------------------------------------------------------------------
Write-Host "`n1. One run a day at 07:00 Eastern, whatever the server's clock" -ForegroundColor Cyan

$eastern = 'Eastern Standard Time'
$zones = [ordered] @{ 'Eastern' = $eastern; 'UTC' = 'UTC'; 'London' = 'GMT Standard Time'; 'US west coast' = 'Pacific Standard Time'; 'Tokyo' = 'Tokyo Standard Time' }
$from = [DateTime]::new(2026, 1, 1)
foreach ($label in $zones.Keys) {
    $local = [TimeZoneInfo]::FindSystemTimeZoneById($zones[$label])
    $times = @(Get-PrecacheTriggerTime -At '07:00' -TimeZone $eastern -LocalZone $local -From $from)
    $runs = @{}
    for ($d = 0; $d -lt 365; $d++) {
        $day = $from.AddDays($d)
        foreach ($t in $times) {
            # A trigger fires at that time on the server's clock; the run goes ahead only within the hour after 07:00 Eastern.
            $wall = [DateTime]::SpecifyKind($day + $t, [DateTimeKind]::Unspecified)
            if ($local.IsInvalidTime($wall)) { continue }
            $utc = [TimeZoneInfo]::ConvertTimeToUtc($wall, $local)
            if (Test-PrecacheWindow -At '07:00' -TimeZone $eastern -NowUtc $utc) {
                $key = [TimeZoneInfo]::ConvertTimeFromUtc($utc, [TimeZoneInfo]::FindSystemTimeZoneById($eastern)).Date
                $runs[$key] = 1 + $(if ($runs.ContainsKey($key)) { $runs[$key] } else { 0 })
            }
        }
    }
    $twice = @($runs.Keys | Where-Object { $runs[$_] -gt 1 })
    $want = [DateTime]::new(2026, 1, 2)
    $missed = @(0..362 | ForEach-Object { $want.AddDays($_) } | Where-Object { -not $runs.ContainsKey($_) })
    Assert-That ($twice.Count -eq 0 -and $missed.Count -eq 0) `
        ("from a server in {0} ({1} trigger(s): {2}), every day of 2026 has exactly one run at 07:00 Eastern{3}" -f $label, $times.Count,
            (($times | ForEach-Object { '{0:hh\:mm}' -f $_ }) -join ', '), $(if ($twice -or $missed) { " - twice: $($twice.Count), missed: $($missed.Count)" } else { '' }))
}
$easternTimes = @(Get-PrecacheTriggerTime -At '07:00' -TimeZone $eastern -LocalZone ([TimeZoneInfo]::FindSystemTimeZoneById($eastern)) -From $from)
Assert-That ($easternTimes.Count -eq 1 -and $easternTimes[0] -eq [TimeSpan]::FromHours(7)) 'a server on Eastern Time has one trigger, at 07:00'
Assert-That ((Test-PrecacheWindow -At '07:00' -TimeZone $eastern -NowUtc ([DateTime]::new(2026, 7, 15, 11, 2, 0))) -and
             -not (Test-PrecacheWindow -At '07:00' -TimeZone $eastern -NowUtc ([DateTime]::new(2026, 7, 15, 12, 0, 0))) -and
             (Test-PrecacheWindow -At '07:00' -TimeZone $eastern -NowUtc ([DateTime]::new(2026, 1, 15, 12, 0, 0))) -and
             -not (Test-PrecacheWindow -At '07:00' -TimeZone $eastern -NowUtc ([DateTime]::new(2026, 1, 15, 11, 0, 0)))) `
    '11:00 UTC is 07:00 in July (EDT) and 06:00 in January (EST); 12:00 UTC is the other way round'

# ---------------------------------------------------------------------------
Write-Host "`n2. A time of day" -ForegroundColor Cyan

Assert-That ((ConvertTo-TimeOfDay '07:00') -eq [TimeSpan]::FromHours(7) -and (ConvertTo-TimeOfDay '7:30') -eq [TimeSpan]::new(7, 30, 0) -and
             (ConvertTo-TimeOfDay '23:59') -eq [TimeSpan]::new(23, 59, 0)) '07:00, 7:30 and 23:59 are read'
foreach ($bad in '7', '24:00', '07:60', 'seven', '', '07:00:00') {
    $why = try { [void] (ConvertTo-TimeOfDay $bad); '' } catch { $_.Exception.Message }
    Assert-That ($why -like '*is not a time of day: give it as HH:mm*') "'$bad' is refused, with the form a time takes"
}

# ---------------------------------------------------------------------------
Write-Host "`n3. What it says at the end" -ForegroundColor Cyan

function New-Answer([string] $Outcome = 'built', [object[]] $Tenants = @(), [string] $Message = $null) {
    [pscustomobject] @{ id = 'x'; outcome = $Outcome; message = $Message; tenants = $Tenants }
}
function New-TenantAnswer([string] $Name = 'Default', [int] $Pages = 432, [int] $Failed = 0, [double] $Slowest = 0.42, [int] $Over = 0, [object[]] $Slow = @(), [string] $Problem = $null,
                          [object[]] $Build = @()) {
    [pscustomobject] @{ tenant = $Name; pages = $Pages; failed = $Failed; buildSeconds = 630.4; builtAgain = 0; slowestToBuild = $Build; checkedPages = $Pages; checkSeconds = 61.0
                        slowestFromMemorySeconds = $Slowest; overOneSecond = $Over; slowest = $Slow; error = $Problem }
}
$ok = Get-PrecacheSummary -Status (New-Answer -Tenants @(New-TenantAnswer))
Assert-That ($ok.Code -eq 0 -and $ok.Lines[0] -like '432 page views built again in 630 s; from memory the slowest took 0.42 s.') 'built, every page under a second: 0, and how many and how fast'
$built = Get-PrecacheSummary -Status (New-Answer -Tenants @(New-TenantAnswer -Build @([pscustomobject] @{ path = '/compliance/sites'; days = 365; seconds = 11.73 }, [pscustomobject] @{ path = '/'; days = 7; seconds = 0.2 })))
Assert-That ($built.Code -eq 0 -and $built.Lines[1] -eq 'The slowest to build from the database: /compliance/sites (365 days), 11.7 s.') 'and the page that took longest to build from the database'
$slowPage = [pscustomobject] @{ path = '/compliance'; days = 365; seconds = 1.31 }
$slow = Get-PrecacheSummary -Status (New-Answer -Tenants @(New-TenantAnswer -Slowest 1.31 -Over 1 -Slow @($slowPage, [pscustomobject] @{ path = '/'; days = 30; seconds = 0.4 })))
Assert-That ($slow.Code -eq 2 -and ($slow.Lines -join "`n") -match '1 page views took more than a second' -and ($slow.Lines -join "`n") -match '1\.31 s  /compliance \(365 days\)' -and
             ($slow.Lines -join "`n") -notmatch '/ \(30 days\)') 'built, a page over a second from memory: 2, naming that page and only that one'
$failedPage = Get-PrecacheSummary -Status (New-Answer -Outcome 'failed' -Tenants @(New-TenantAnswer -Failed 3))
Assert-That ($failedPage.Code -eq 1 -and $failedPage.Lines[0] -like '*, 3 failed;*') 'a page that did not build: 1, with how many'
$two = Get-PrecacheSummary -Status (New-Answer -Outcome 'failed' -Tenants @((New-TenantAnswer -Name 'Contoso'), (New-TenantAnswer -Name 'Fabrikam' -Problem 'Login failed')))
Assert-That ($two.Code -eq 1 -and $two.Lines[0] -like 'Contoso: 432 page views*' -and $two.Lines[1] -eq 'Fabrikam: not built: Login failed') 'with several tenants, each is named, and one that failed fails the run'
$off = Get-PrecacheSummary -Status (New-Answer -Outcome 'off' -Message 'The query cache is off on this web server')
Assert-That ($off.Code -eq 1 -and $off.Lines[0] -like 'Not built: The query cache is off*') 'the cache off: 1, and why'
$stopped = Get-PrecacheSummary -Status (New-Answer -Outcome 'stopped' -Tenants @(New-TenantAnswer))
Assert-That ($stopped.Code -eq 1) 'a build the site stopped (it shut down): 1'

# While it builds: how far it has got, and nothing before the site says how many it builds.
$building = [pscustomobject] @{ id = 'x'; outcome = 'building'; tenants = @([pscustomobject] @{ tenant = 'A'; pages = 120; planned = 432 }, [pscustomobject] @{ tenant = 'B'; pages = 0; planned = 432 }) }
Assert-That ((Get-PrecacheProgress -Status $building) -eq '120 of 864 page views built' -and
             (Get-PrecacheProgress -Status ([pscustomobject] @{ id = 'x'; outcome = 'building'; tenants = @() })) -eq '') 'while it builds: how many of how many, added up over the tenants; nothing before it knows'

# The scheduled task runs the script in Windows PowerShell 5.1, with strict mode on: its functions are run there too, on
# the answer as the site writes it, read by 5.1's own JSON reader. Before 2.5.1 shipped, the line that says how far the
# build has got failed there, on an answer with no tenant yet, and stopped the script.
if ($IsWindows -and (Get-Command powershell.exe -ErrorAction SilentlyContinue)) {
    $answers = @(
        '{"id":"a","outcome":"building","message":null,"tenants":[]}',
        '{"id":"a","outcome":"building","message":null,"tenants":[{"tenant":"Default","pages":176,"planned":432,"failed":0,"buildSeconds":0,"builtAgain":0,"slowestToBuild":[],"checkedPages":0,"checkSeconds":0,"slowestFromMemorySeconds":0,"overOneSecond":0,"slowest":[],"error":null}]}',
        '{"id":"a","outcome":"built","message":null,"tenants":[{"tenant":"Default","pages":432,"planned":432,"failed":0,"buildSeconds":843.2,"builtAgain":0,"slowestToBuild":[{"path":"/leadership","days":365,"seconds":7.68}],"checkedPages":432,"checkSeconds":0.6,"slowestFromMemorySeconds":0.053,"overOneSecond":0,"slowest":[{"path":"/adoption/retention","days":180,"seconds":0.053}],"error":null}]}')
    $tmp51 = Join-Path ([System.IO.Path]::GetTempPath()) ("precache51-" + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $tmp51 | Out-Null
    try {
        for ($i = 0; $i -lt $answers.Count; $i++) { Set-Content -LiteralPath (Join-Path $tmp51 "a$i.json") -Value $answers[$i] -Encoding utf8 }
        $probe = @"
Set-StrictMode -Version Latest; `$ErrorActionPreference = 'Stop'
. '$($scriptPath.Replace("'", "''"))'
foreach (`$f in Get-ChildItem '$tmp51' -Filter *.json | Sort-Object Name) {
    `$s = Get-Content -LiteralPath `$f.FullName -Raw | ConvertFrom-Json
    'progress=' + (Get-PrecacheProgress -Status `$s)
    if (`$s.outcome -ne 'building') { `$r = Get-PrecacheSummary -Status `$s; 'code=' + `$r.Code; `$r.Lines }
}
"@
        Set-Content -LiteralPath (Join-Path $tmp51 'probe.ps1') -Value $probe -Encoding utf8
        $out = (& powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $tmp51 'probe.ps1') 2>&1 | Out-String)
        Assert-That ($LASTEXITCODE -eq 0 -and $out -match 'progress=\r?\n' -and $out -match 'progress=176 of 432 page views built' -and $out -match 'code=0' -and
                     $out -match 'from memory the slowest took 0\.05 s' -and $out -match 'The slowest to build from the database: /leadership \(365 days\), 7\.7 s\.') `
            "in Windows PowerShell 5.1, with strict mode, the script reads the site's answers: before a tenant, while it builds and when it is done$(if ($LASTEXITCODE -ne 0 -or $out -match 'Exception|cannot be found') { ": $out" })"
    }
    finally { Remove-Item -LiteralPath $tmp51 -Recurse -Force -ErrorAction SilentlyContinue }
}
else { Write-Host '  SKIP  Windows PowerShell 5.1 is not on this machine' }

# ---------------------------------------------------------------------------
Write-Host "`n4. The installer and the site" -ForegroundColor Cyan

$installer = [System.IO.File]::ReadAllText((Join-Path $root 'dashboard' 'Install-CopilotInsights.ps1'))
Assert-That ($installer -match "\[string\] \`$PrecacheAt = '07:00'" -and $installer -match "\[string\] \`$PrecacheTimeZone = 'Eastern Standard Time'") 'the installer schedules 07:00 Eastern Time unless told otherwise'
Assert-That ($installer -match 'Copy-Item -LiteralPath \$precacheScript -Destination \$staging') 'it copies the script into the site, where the app pool only reads it'
Assert-That ($installer -match '/XD logs precache') 'an update keeps the precache folder, its log and the last answer'
Assert-That ($installer -match "foreach \(\`$who in \`$poolAccount, 'NT AUTHORITY\\LOCAL SERVICE'\)") 'the app pool and LOCAL SERVICE may change the precache folder'
Assert-That ($installer -match '& \$sitePrecache -Register -PhysicalPath \$PhysicalPath -At \$PrecacheAt -TimeZone \$PrecacheTimeZone' -and
             $installer -match '& \$sitePrecache -Unregister') 'it schedules the task through the script, and -PrecacheAt '''' removes it'
$checkAt = $installer.IndexOf('-PrecacheAt ''$PrecacheAt'' is not a time of day'); $offlineAt = $installer.IndexOf('Set-Content $offline')
Assert-That ($checkAt -gt 0 -and $checkAt -lt $offlineAt) 'a -PrecacheAt that is no time is refused before the site goes offline'

$warmer = [System.IO.File]::ReadAllText((Join-Path $root 'dashboard' 'CopilotInsights' 'Infrastructure' 'PageWarmer.cs'))
Assert-That ($warmer -match 'RequestFile = "precache\.request", StatusFile = "precache\.json"' -and $text -match "'precache\.request'" -and $text -match "'precache\.json'") 'the site reads the request the script writes, and writes the answer it waits for'
Assert-That ($text -match "Join-Path \`$site 'precache'" -and $warmer -match 'Path\.Combine\(env\.ContentRootPath, "precache"\)') 'both find the folder in the site''s folder unless told otherwise'
Assert-That ($text -match 'Move-Item -LiteralPath "\$request\.part" -Destination \$request' -and $warmer -match 'File\.Move\(part, file, overwrite: true\)') 'each writes its file whole, through a file beside it'
Assert-That ($text -match "New-ScheduledTaskPrincipal -UserId 'NT AUTHORITY\\LOCAL SERVICE' -LogonType ServiceAccount") 'the task runs as LOCAL SERVICE, not SYSTEM'

Write-Host ''
if ($script:Failures.Count) { Write-Host "$($script:Failures.Count) assertion(s) failed." -ForegroundColor Red; exit 1 }
Write-Host 'All pre-cache tests passed.' -ForegroundColor Green
