#Requires -Version 5.1
#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Schedules the weekly Copilot report: a task that runs every hour and sends
    each week once, on the first run after Tuesday 08:00 (the settings' SendDay
    and SendTime) in the reporting time zone.

.DESCRIPTION
    Creates the task 'Copilot audit - weekly report', which runs

        powershell.exe -NoProfile -NonInteractive -File tools\Send-CopilotWeeklyReport.ps1 -Scheduled

    every hour, every day. The script itself decides whether a week is due, so
    the send follows the reporting time zone through clock changes, and a
    Tuesday the server was down is caught up on the next run. A run that has
    nothing to do ends in under a second.

    The task runs as the nightly task's account by default (it already reads
    the Copilot database and writes its logs folder). Run this elevated, from
    the folder the scripts are in. Run it again to change the account; it
    replaces the task.

.PARAMETER ServiceAccount
    The account to run as. Default: the account 'Copilot audit - nightly' runs
    as. A gMSA (ending in $) needs no password.

.PARAMETER ServiceAccountCredential
    For an account that is not a gMSA: its user name and password.

.PARAMETER Unregister
    Remove the task.

.EXAMPLE
    .\tools\Register-CopilotWeeklyReport.ps1

.EXAMPLE
    .\tools\Register-CopilotWeeklyReport.ps1 -ServiceAccount 'CORP\gmsa-copilot$'
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]       $ServiceAccount,
    [pscredential] $ServiceAccountCredential,
    [switch]       $Unregister
)

$ErrorActionPreference = 'Stop'
$taskName = 'Copilot audit - weekly report'
$nightlyName = 'Copilot audit - nightly'
$root = Split-Path $PSScriptRoot -Parent
$script = Join-Path $PSScriptRoot 'Send-CopilotWeeklyReport.ps1'

if ($Unregister) {
    if (Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue) {
        if ($PSCmdlet.ShouldProcess($taskName, 'Remove the task')) {
            Unregister-ScheduledTask -TaskName $taskName -Confirm:$false
            Write-Host "Removed '$taskName'."
        }
    }
    else { Write-Host "'$taskName' is not registered." }
    return
}

if (-not (Test-Path -LiteralPath $script)) { throw "Send-CopilotWeeklyReport.ps1 is not next to this script ($PSScriptRoot)." }
if (-not (Test-Path -LiteralPath (Join-Path $root 'CopilotWeeklyReport.settings.psd1'))) {
    throw "No CopilotWeeklyReport.settings.psd1 in $root. Copy CopilotWeeklyReport.settings.example.psd1 there, fill it in and run -Preview first."
}

if (-not $ServiceAccount) {
    if ($ServiceAccountCredential) { $ServiceAccount = $ServiceAccountCredential.UserName }
    else {
        $nightly = Get-ScheduledTask -TaskName $nightlyName -ErrorAction SilentlyContinue
        if (-not $nightly) { throw "'$nightlyName' is not registered here: pass -ServiceAccount." }
        $ServiceAccount = $nightly.Principal.UserId
        Write-Host "Running as $ServiceAccount, the account '$nightlyName' runs as."
    }
}

$ps = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
$action = New-ScheduledTaskAction -Execute $ps -WorkingDirectory $root `
    -Argument "-NoProfile -NonInteractive -File `"$script`" -Scheduled"

# Every hour, all day, every day: a daily trigger repeated hourly for 23 hours 59 minutes.
$trigger = New-ScheduledTaskTrigger -Daily -At '00:07'
$repeat = New-ScheduledTaskTrigger -Once -At '00:07' -RepetitionInterval (New-TimeSpan -Hours 1) -RepetitionDuration (New-TimeSpan -Hours 23 -Minutes 59)
$trigger.Repetition = $repeat.Repetition

$settings = New-ScheduledTaskSettingsSet -StartWhenAvailable -MultipleInstances IgnoreNew -ExecutionTimeLimit (New-TimeSpan -Minutes 50)

if (-not $PSCmdlet.ShouldProcess($taskName, "Schedule hourly as $ServiceAccount")) { return }
if ($ServiceAccount.EndsWith('$')) {
    # LogonType Password is how Task Scheduler is told to fetch a gMSA's password from AD.
    $principal = New-ScheduledTaskPrincipal -UserId $ServiceAccount -LogonType Password -RunLevel Limited
    Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger -Settings $settings -Principal $principal -Force | Out-Null
}
else {
    if (-not $ServiceAccountCredential) { throw "$ServiceAccount is not a gMSA: pass -ServiceAccountCredential so the task can store its password." }
    Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger -Settings $settings `
        -User $ServiceAccountCredential.UserName -Password $ServiceAccountCredential.GetNetworkCredential().Password -RunLevel Limited -Force | Out-Null
}
Write-Host "'$taskName' runs every hour as $ServiceAccount and sends each week once, after its send day and time."
