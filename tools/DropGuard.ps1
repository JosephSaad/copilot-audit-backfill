<#
.SYNOPSIS
    The delete guard (2.6.5): a load that empties a table and fills it again is refused when it would leave the
    table more than a set percentage smaller than the last load left it.

.DESCRIPTION
    Dot-sourced by the loaders that replace a whole table each run: the agent workbook
    (tools\Import-AgentWorkbook.ps1), the user directory from AD (tools\Export-UsersWithHierarchy-ToSql.ps1), the
    licenses each person holds (tools\Update-CopilotAuditEnrichment.ps1), GitHub Copilot's seats
    (tools\Import-GitHubCopilotUsage.ps1) and Foundry's deployments (tools\Import-FoundryUsage.ps1). It runs
    nothing by itself, and works in Windows PowerShell 5.1 as well as PowerShell 7, as the directory load does.

    Each loader counts the rows the table holds now and the rows the new load would put in, before it deletes
    anything. When the new load is more than its percentage smaller, it stops with the reason and replaces
    nothing: a source that answered with half its rows (an AD query cut short, a workbook saved half-filled, an
    API that returned one page) leaves the last good load in place. Each loader has its own percentage, a setting
    of its own in copilot-audit.settings.psd1 (see $DropGuardSetting), and its own -MaxDropPercent; 0 turns its
    guard off. A drop that is real (a reorganization, an agent clean-up) is let through once with -AllowDrop.

    The rule: with B rows before and A after, the load is refused when B > 0 and (B - A) / B * 100 is more than the
    percentage. A first load (B = 0), a load that grows or stays level, and a drop of exactly the percentage go
    ahead. 10,000 rows before, 8,000 after and 15%: a 20% drop, refused.
#>

$script:DefaultMaxDropPercent = 15

# The exit code of a loader whose load the guard refused (2.6.5). Invoke-CopilotPipeline.ps1 records it and goes on
# with the night: the table kept its last good load, so what comes after it can still run.
$script:DropRefusedExitCode = 3

# The setting each loader's percentage comes from, by the loader's name for what it replaces.
$script:DropGuardSetting = [ordered]@{
    'Agent workbook'      = 'AgentWorkbookMaxDropPercent'
    'User directory'      = 'UserDirectoryMaxDropPercent'
    'Licenses held'       = 'LicenseMaxDropPercent'
    'GitHub Copilot seats' = 'GitHubSeatMaxDropPercent'
    'Foundry deployments' = 'FoundryDeploymentMaxDropPercent'
}

function Get-DropRefusal {
    <#
        Why a load is refused, or $null when it may go ahead. -What names the table or sheet for the message,
        -Setting the setting that holds the percentage. Pure, for the tests.
    #>
    param([string] $What, [long] $Before, [long] $After, [double] $MaxDropPercent, [string] $Setting)
    if ($MaxDropPercent -le 0 -or $Before -le 0 -or $After -ge $Before) { return $null }
    $drop = 100.0 * ($Before - $After) / $Before
    if ($drop -le $MaxDropPercent) { return $null }
    $inv = [cultureinfo]::InvariantCulture
    ("{0}: the new load has {1} row(s) where the last load left {2}, {3}% fewer; the delete guard allows at most {4}% fewer, so nothing was replaced. " +
     "If the drop is real, run the load again with -AllowDrop, or raise {5} in copilot-audit.settings.psd1.") -f
        $What, $After.ToString('N0', $inv), $Before.ToString('N0', $inv), [math]::Round($drop, 1).ToString($inv), $MaxDropPercent.ToString($inv), $Setting
}

function Assert-DropGuard {
    <#
        Stops the load (throws) when Get-DropRefusal refuses it, unless -AllowDrop: then the drop is let through
        and said, as a warning. The exception carries Data['DropGuard'] = $true, so a loader that goes on with its
        other parts (Foundry's usage and cost) can tell a refusal from a failure (Test-DropRefusal).
    #>
    param([string] $What, [long] $Before, [long] $After, [double] $MaxDropPercent, [string] $Setting, [switch] $AllowDrop)
    $why = Get-DropRefusal -What $What -Before $Before -After $After -MaxDropPercent $MaxDropPercent -Setting $Setting
    if (-not $why) { return }
    if ($AllowDrop) {
        Write-Warning ("{0}: {1:N0} row(s) where the last load left {2:N0}; let through by -AllowDrop." -f $What, $After, $Before)
        return
    }
    $ex = [System.InvalidOperationException]::new($why)
    $ex.Data['DropGuard'] = $true
    throw $ex
}

function Test-DropRefusal {
    <# Whether an error is the delete guard's refusal. #>
    param($ErrorRecord)
    $e = if ($ErrorRecord -is [System.Management.Automation.ErrorRecord]) { $ErrorRecord.Exception } else { $ErrorRecord }
    [bool] ($e -and $e.Data -and $e.Data['DropGuard'])
}

function Resolve-MaxDropPercent {
    <#
        The percentage a loader uses: -MaxDropPercent where it was given on the command line (-Given, with
        -WasGiven), else its setting in the settings file read (-Settings), else 15. A setting that is not a number
        from 0 to 100 stops the load, saying so. Pure, for the tests.
    #>
    param([string] $What, [double] $Given, [bool] $WasGiven, [System.Collections.IDictionary] $Settings)
    if ($WasGiven) { return $Given }
    $name = $script:DropGuardSetting[$What]
    if ($Settings -and $name -and $Settings.Contains($name) -and $null -ne $Settings[$name] -and "$($Settings[$name])" -ne '') {
        $v = 0.0
        if (-not [double]::TryParse("$($Settings[$name])", [System.Globalization.NumberStyles]::Float, [cultureinfo]::InvariantCulture, [ref] $v) -or $v -lt 0 -or $v -gt 100) {
            throw "$name in the settings file is '$($Settings[$name])': it is a percentage from 0 to 100 (0 turns the delete guard off)."
        }
        return $v
    }
    $script:DefaultMaxDropPercent
}
