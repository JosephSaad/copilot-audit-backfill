<#
.SYNOPSIS
    Builds every Copilot Insights report page ahead on this web server, and waits until it is done (2.5.1).

.DESCRIPTION
    The site keeps each report page's query results in memory, and builds every report page for the whole
    organization at every period after each data change. This asks it to build all of them again now, reading
    every query afresh, so each one is in memory, and kept for its full time, when people arrive. Whoever opens a
    page meanwhile is answered from what was kept before: nobody waits for the build. The site then builds every
    page once more from memory, timed, and says which took longer than a second.

    It works through a file, not a web request, so it needs no sign-in to the dashboard: it leaves
    precache.request in the site's precache folder, the site takes it within 15 seconds, and answers in
    precache.json, which this waits for. Only an account that can write that folder (an administrator of the
    server; the scheduled task's LOCAL SERVICE) can ask. Each web server keeps its own memory, so run it on each.

    Install-CopilotInsights.ps1 copies this script into the site's folder and schedules it every day at 07:00
    Eastern Time (-Register). Each run is added to precache.log in the precache folder.

    Exit code: 0 built, and every page came from memory in under a second; 2 built, but some pages took longer;
    1 not built (the site did not take the request, a page or a tenant failed, or it ran out of time).

.PARAMETER PhysicalPath
    The site's folder. Default: the folder this script is in when the site is there (the installer puts it
    there), else C:\inetpub\CopilotInsights.

.PARAMETER Folder
    The folder requests are left in, when the site's Insights:PrecacheFolder names another. Default:
    <PhysicalPath>\precache.

.PARAMETER At
    With -TimeZone: run only within the hour that starts at this time (HH:mm) in that zone, and skip
    otherwise. The scheduled task passes them; a run by hand leaves them out and runs at once. With
    -Register: the time to schedule. Default 07:00.

.PARAMETER TimeZone
    The Windows time zone -At is in. Default 'Eastern Standard Time': Eastern Time, EST in winter and EDT in
    summer. Get-TimeZone -ListAvailable lists the others.

.PARAMETER TimeoutMinutes
    How long to wait for the build. Default 240.

.PARAMETER Register
    Schedules this script every day at -At in -TimeZone, as LOCAL SERVICE, instead of running it. On a server
    whose clock is in another zone the task has one trigger at each local time that can be -At there (summer
    time starts on different days in different zones), and each run goes ahead only within the hour after -At.

.PARAMETER Unregister
    Removes the scheduled task.

.EXAMPLE
    .\Invoke-InsightsPrecache.ps1                      # build every page now, and wait
.EXAMPLE
    .\Invoke-InsightsPrecache.ps1 -Register -At 07:00 -TimeZone 'Eastern Standard Time'
#>
#Requires -Version 5.1
[CmdletBinding(DefaultParameterSetName = 'Run', SupportsShouldProcess)]
param(
    [string] $PhysicalPath,
    [string] $Folder,
    [Parameter(ParameterSetName = 'Run')][Parameter(ParameterSetName = 'Register')][string] $At,
    [Parameter(ParameterSetName = 'Run')][Parameter(ParameterSetName = 'Register')][string] $TimeZone = 'Eastern Standard Time',
    [Parameter(ParameterSetName = 'Run')][int] $TimeoutMinutes = 240,
    [Parameter(ParameterSetName = 'Register', Mandatory)][switch] $Register,
    [Parameter(ParameterSetName = 'Unregister', Mandatory)][switch] $Unregister
)

$script:TaskName = 'Copilot Insights pre-cache'

function Get-PrecacheTriggerTime {
    <#
        The times of day on this server's clock at which it is -At in -TimeZone, over a year from -From: one where the
        two clocks change for summer together (or never), two where they do not. Pure, for the tests.
    #>
    param([Parameter(Mandatory)][string] $At, [Parameter(Mandatory)][string] $TimeZone, [TimeZoneInfo] $LocalZone = [TimeZoneInfo]::Local,
          [DateTime] $From = [DateTime]::Today)
    $zone = [TimeZoneInfo]::FindSystemTimeZoneById($TimeZone)
    $time = ConvertTo-TimeOfDay $At
    $seen = New-Object 'System.Collections.Generic.SortedSet[TimeSpan]'
    for ($d = 0; $d -lt 366; $d++) {
        $wall = [DateTime]::SpecifyKind($From.Date.AddDays($d) + $time, [DateTimeKind]::Unspecified)
        if ($zone.IsInvalidTime($wall)) { continue }
        [void] $seen.Add([TimeZoneInfo]::ConvertTime($wall, $zone, $LocalZone).TimeOfDay)
    }
    foreach ($t in $seen) { $t }
}

function Test-PrecacheWindow {
    <# Whether -NowUtc is within the hour that starts at -At in -TimeZone (from 5 minutes before). Pure, for the tests. #>
    param([Parameter(Mandatory)][string] $At, [Parameter(Mandatory)][string] $TimeZone, [DateTime] $NowUtc = [DateTime]::UtcNow)
    $zone = [TimeZoneInfo]::FindSystemTimeZoneById($TimeZone)
    $now = [TimeZoneInfo]::ConvertTimeFromUtc([DateTime]::SpecifyKind($NowUtc, [DateTimeKind]::Utc), $zone)
    $start = $now.Date + (ConvertTo-TimeOfDay $At)
    $now -ge $start.AddMinutes(-5) -and $now -lt $start.AddMinutes(55)
}

function ConvertTo-TimeOfDay {
    <# "07:00" or "7:00" as a time of day; anything else is refused with the reason. Pure, for the tests. #>
    param([string] $Text)
    $t = [TimeSpan]::Zero
    if (-not $Text -or $Text -notmatch '^\d{1,2}:\d{2}$' -or -not [TimeSpan]::TryParse($Text, [Globalization.CultureInfo]::InvariantCulture, [ref] $t) -or $t.TotalHours -ge 24) {
        throw "'$Text' is not a time of day: give it as HH:mm, such as 07:00."
    }
    $t
}

function Get-PrecacheSummary {
    <# The lines a finished answer is reported as, and the exit code it means (0, 2 or 1). Pure, for the tests. #>
    param([Parameter(Mandatory)] $Status)
    $lines = New-Object System.Collections.Generic.List[string]
    $code = 0
    if ($Status.outcome -eq 'off') { return @{ Lines = @("Not built: $($Status.message)"); Code = 1 } }
    foreach ($t in @($Status.tenants)) {
        $name = if (@($Status.tenants).Count -gt 1) { "$($t.tenant): " } else { '' }
        if ($t.error) { $lines.Add("${name}not built: $($t.error)"); $code = 1; continue }
        $lines.Add(('{0}{1:N0} page views built again in {2:N0} s{3}; from memory the slowest took {4:N2} s.' -f $name, $t.pages, $t.buildSeconds,
            $(if ($t.failed -gt 0) { ", $($t.failed) failed" } else { '' }), $t.slowestFromMemorySeconds))
        $first = @($t.slowestToBuild) | Select-Object -First 1
        if ($first) { $lines.Add(('{0}The slowest to build from the database: {1} ({2} days), {3:N1} s.' -f $name, $first.path, $first.days, $first.seconds)) }
        if ($t.failed -gt 0) { $code = 1 }
        if ($t.overOneSecond -gt 0) {
            $lines.Add(('{0}{1:N0} page views took more than a second from memory:' -f $name, $t.overOneSecond))
            foreach ($p in @($t.slowest | Where-Object { $_.seconds -gt 1 })) { $lines.Add(('    {0,6:N2} s  {1} ({2} days)' -f $p.seconds, $p.path, $p.days)) }
            if ($code -eq 0) { $code = 2 }
        }
    }
    if ($Status.outcome -in 'failed', 'stopped' -and $code -ne 1) { $code = 1; $lines.Add("The site says: $($Status.outcome).") }
    @{ Lines = @($lines); Code = $code }
}

function Get-PrecacheProgress {
    <#
        How far a build has got, "120 of 432 page views built", or empty before the site knows how many it builds.
        Added up here: Measure-Object gives nothing for an empty list in Windows PowerShell. Pure, for the tests.
    #>
    param([Parameter(Mandatory)] $Status)
    $done = 0; $planned = 0
    foreach ($t in @($Status.tenants)) { $done += [int] $t.pages; $planned += [int] $t.planned }
    if ($planned -le 0) { return '' }
    '{0:N0} of {1:N0} page views built' -f $done, $planned
}

function Resolve-PrecacheFolder {
    if ($Folder) { return $Folder }
    $site = if ($PhysicalPath) { $PhysicalPath }
            elseif (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'CopilotInsights.dll')) { $PSScriptRoot }
            else { 'C:\inetpub\CopilotInsights' }
    Join-Path $site 'precache'
}

function Write-Log([string] $Text) {
    $line = '{0:yyyy-MM-dd HH:mm:ss}  {1}' -f (Get-Date), $Text
    Write-Host $line
    if ($script:LogFile) { try { Add-Content -LiteralPath $script:LogFile -Value $line -Encoding UTF8 } catch { Write-Verbose "The log could not be written: $($_.Exception.Message)" } }
}

function Invoke-Precache {
    [CmdletBinding(SupportsShouldProcess)] param()
    $dir = Resolve-PrecacheFolder
    if (-not (Test-Path -LiteralPath $dir -PathType Container)) {
        throw "No precache folder at ${dir}: install Copilot Insights 2.5.1 or later with dashboard\Install-CopilotInsights.ps1, which makes it and lets the site use it, or pass -PhysicalPath."
    }
    $script:LogFile = Join-Path $dir 'precache.log'
    if ($At -and -not (Test-PrecacheWindow -At $At -TimeZone $TimeZone)) {
        Write-Verbose "Not within the hour after $At in $TimeZone; the other trigger runs it."
        return 0
    }
    $id = [guid]::NewGuid().ToString('N')
    $request = Join-Path $dir 'precache.request'
    $answer = Join-Path $dir 'precache.json'
    $by = "$env:USERDOMAIN\$env:USERNAME on $env:COMPUTERNAME"
    $body = @{ id = $id; requestedUtc = [DateTime]::UtcNow.ToString('o'); requestedBy = $by } | ConvertTo-Json
    if (-not $PSCmdlet.ShouldProcess($dir, 'Ask the site to build every report page again')) { return 0 }
    # Written beside it and then renamed, so the site never reads half a request.
    Set-Content -LiteralPath "$request.part" -Value $body -Encoding UTF8
    Move-Item -LiteralPath "$request.part" -Destination $request -Force
    Write-Log "Asked the site to build every report page again ($id)."

    $sw = [Diagnostics.Stopwatch]::StartNew()
    $taken = $false
    $said = ''; $saidAt = [TimeSpan]::Zero
    while ($true) {
        Start-Sleep -Seconds 10
        $status = $null
        if (Test-Path -LiteralPath $answer) {
            try { $status = Get-Content -LiteralPath $answer -Raw | ConvertFrom-Json } catch { $status = $null }
        }
        if ($status -and $status.id -eq $id) {
            if (-not $taken) { $taken = $true; Write-Log 'The site took the request and is building.' }
            if ($status.endedUtc) { break }
            # How far it has got, at most once a minute.
            $now = Get-PrecacheProgress -Status $status
            if ($now -and $now -ne $said -and ($sw.Elapsed - $saidAt).TotalSeconds -ge 60) { Write-Log "Building: $now."; $said = $now; $saidAt = $sw.Elapsed }
        }
        elseif (-not $taken -and $sw.Elapsed.TotalMinutes -ge 5) {
            Write-Log "Not built: the site did not take the request in 5 minutes. Is its app pool running, and does it read $dir (Insights:PrecacheFolder)?"
            Remove-Item -LiteralPath $request -ErrorAction SilentlyContinue
            return 1
        }
        if ($sw.Elapsed.TotalMinutes -ge $TimeoutMinutes) {
            Write-Log "Not finished after $TimeoutMinutes minutes: the site goes on building; precache.json says how far it got."
            return 1
        }
    }
    $summary = Get-PrecacheSummary -Status $status
    foreach ($l in $summary.Lines) { Write-Log $l }
    Write-Log ('Done in {0:N0} s: {1}.' -f $sw.Elapsed.TotalSeconds, $(switch ($summary.Code) { 0 { 'built' } 2 { 'built, with pages over a second' } default { 'not built' } }))
    $summary.Code
}

function Register-PrecacheTask {
    [CmdletBinding(SupportsShouldProcess)] param()
    $site = if ($PhysicalPath) { $PhysicalPath } else { Split-Path -Parent (Resolve-PrecacheFolder) }
    $self = Join-Path $site 'Invoke-InsightsPrecache.ps1'
    if (-not (Test-Path -LiteralPath $self)) { throw "$self is not there: Install-CopilotInsights.ps1 copies this script into the site's folder." }
    $when = if ($At) { $At } else { '07:00' }
    [void] (ConvertTo-TimeOfDay $when)
    [void] [TimeZoneInfo]::FindSystemTimeZoneById($TimeZone)
    $times = @(Get-PrecacheTriggerTime -At $when -TimeZone $TimeZone)
    $triggers = @($times | ForEach-Object { New-ScheduledTaskTrigger -Daily -At ([DateTime]::Today + $_) })
    $ps = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File `"$self`" -PhysicalPath `"$site`" -At $when -TimeZone `"$TimeZone`""
    if ($Folder) { $arguments += " -Folder `"$Folder`"" }
    $action = New-ScheduledTaskAction -Execute $ps -Argument $arguments -WorkingDirectory $site
    $settings = New-ScheduledTaskSettingsSet -StartWhenAvailable -MultipleInstances IgnoreNew -ExecutionTimeLimit (New-TimeSpan -Hours 5)
    $principal = New-ScheduledTaskPrincipal -UserId 'NT AUTHORITY\LOCAL SERVICE' -LogonType ServiceAccount -RunLevel Limited
    $local = ($times | ForEach-Object { '{0:hh\:mm}' -f $_ }) -join ' and '
    if (-not $PSCmdlet.ShouldProcess($script:TaskName, "Schedule daily at $when $TimeZone ($local on this server's clock)")) { return }
    Register-ScheduledTask -TaskName $script:TaskName -TaskPath '\' -Action $action -Trigger $triggers -Settings $settings -Principal $principal `
        -Description "Builds every Copilot Insights report page ahead at $when $TimeZone (dashboard\Invoke-InsightsPrecache.ps1)." -Force | Out-Null
    Write-Host "'$script:TaskName' runs daily at $when $TimeZone ($local on this server's clock), as LOCAL SERVICE."
}

function Unregister-PrecacheTask {
    [CmdletBinding(SupportsShouldProcess)] param()
    if ((Get-ScheduledTask -TaskName $script:TaskName -ErrorAction SilentlyContinue) -and $PSCmdlet.ShouldProcess($script:TaskName, 'Remove the scheduled task')) {
        Unregister-ScheduledTask -TaskName $script:TaskName -Confirm:$false
        Write-Host "'$script:TaskName' removed."
    }
}

if ($MyInvocation.InvocationName -eq '.') { return }   # dot-sourced by the tests: the functions only
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
switch ($PSCmdlet.ParameterSetName) {
    'Register' { Register-PrecacheTask }
    'Unregister' { Unregister-PrecacheTask }
    default {
        if ($At) { [void] (ConvertTo-TimeOfDay $At); [void] [TimeZoneInfo]::FindSystemTimeZoneById($TimeZone) }
        exit (Invoke-Precache)
    }
}
