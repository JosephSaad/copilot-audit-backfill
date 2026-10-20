<#
.SYNOPSIS
    Rolls the activity database's AI app calls, Power BI and Power Apps activity up into the Copilot database (2.6.9).

.DESCRIPTION
    Runs audit.usp_RollupActivity in the Copilot database: the AI apps' days by resource and caller
    (audit.AIAppUserDay, for Governance > AI apps beyond Copilot), the resources seen (audit.AIAppResource), and who
    used Power BI and Power Apps lately (audit.PowerPlatformUserActivity, for Costs and savings and Suite fit). The
    nightly pipeline runs it after the activity database's own load, where the settings name one (ActivityDatabase);
    run it by hand after a history load, or after the activity records were shredded again.

    With no -SqlServer, SqlServer, Database, ActivityDatabase and TrustServerCertificate come from
    copilot-audit.settings.psd1.

.PARAMETER SinceUtc
    The days from then on. Default: three days back, the nightly load's lookback and one more. -Full: every day.

.EXAMPLE
    .\tools\Update-ActivityRollup.ps1 -Full
.EXAMPLE
    .\tools\Update-ActivityRollup.ps1 -SqlServer SQLPROD01 -Database CopilotAudit -ActivityDatabase CopilotActivity
#>
#Requires -Version 7.2
[CmdletBinding()]
param(
    [string] $SqlServer,
    [string] $Database,
    [string] $ActivityDatabase,
    [switch] $TrustServerCertificate,
    [string] $ConnectionString,
    [datetime] $SinceUtc,
    [switch] $Full,
    [string] $SettingsPath = (Join-Path (Split-Path $PSScriptRoot -Parent) 'copilot-audit.settings.psd1')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-ActivityRollupSince {
    <# The day the rollup starts from: $null for every day, else the day given, or three days back. Pure, for the tests. #>
    param([Nullable[datetime]] $SinceUtc, [switch] $Full, [datetime] $Today = [datetime]::UtcNow.Date)
    if ($Full) { return $null }
    if ($null -ne $SinceUtc) { return ([datetime] $SinceUtc).Date }
    $Today.AddDays(-3)
}

if ($MyInvocation.InvocationName -eq '.') { return }   # dot-sourced by the tests: functions only

if (-not $SqlServer -and -not $ConnectionString) {
    if (-not (Test-Path -LiteralPath $SettingsPath)) { throw "Give -SqlServer, or run this on the job server (no settings at $SettingsPath)." }
    $s = Import-PowerShellDataFile -LiteralPath $SettingsPath
    $SqlServer = $s.SqlServer
    if (-not $Database) { $Database = $s.Database }
    if (-not $ActivityDatabase -and $s.Contains('ActivityDatabase')) { $ActivityDatabase = $s.ActivityDatabase }
    if ($s.Contains('TrustServerCertificate') -and $s.TrustServerCertificate) { $TrustServerCertificate = $true }
}
if (-not $Database) { $Database = 'CopilotAudit' }
if (-not $ActivityDatabase) { throw 'No activity database: give -ActivityDatabase, or set ActivityDatabase in the settings file.' }

$b = [System.Data.SqlClient.SqlConnectionStringBuilder]::new()
if ($ConnectionString) { $b.ConnectionString = $ConnectionString }
else {
    $b['Data Source'] = $SqlServer; $b['Initial Catalog'] = $Database; $b['Integrated Security'] = $true
    $b['Encrypt'] = $true; $b['TrustServerCertificate'] = [bool] $TrustServerCertificate
}
$b['Application Name'] = 'Update-ActivityRollup'

$since = Get-ActivityRollupSince -SinceUtc $(if ($PSBoundParameters.ContainsKey('SinceUtc')) { $SinceUtc } else { $null }) -Full:$Full
$c = [System.Data.SqlClient.SqlConnection]::new($b.ConnectionString)
$c.add_InfoMessage({ param($x, $e) foreach ($m in $e.Errors) { Write-Host $m.Message } })
$c.Open()
try {
    $cmd = $c.CreateCommand()
    $cmd.CommandTimeout = 0
    $cmd.CommandText = 'EXEC audit.usp_RollupActivity @ActivityDatabase = @a, @SinceUtc = @s;'
    [void] $cmd.Parameters.AddWithValue('@a', $ActivityDatabase)
    [void] $cmd.Parameters.AddWithValue('@s', $(if ($null -eq $since) { [DBNull]::Value } else { $since }))
    Write-Host ("Rolling up {0} into {1}, {2}..." -f $ActivityDatabase, $Database, $(if ($null -eq $since) { 'every day' } else { 'from {0:yyyy-MM-dd}' -f $since }))
    [void] $cmd.ExecuteNonQuery()
}
finally { $c.Dispose() }
