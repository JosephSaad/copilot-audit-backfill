#Requires -Version 7.0
<#
.SYNOPSIS
    Lab tool: adds an older year to the synthetic stress database, so the 730-day and All data periods read two years.

.DESCRIPTION
    Runs Add-ScaleHistory.sql (see its header for how the rows are made), then dash.usp_RefreshDashboard over every
    day, so the dashboard's daily totals, flagged interactions and agent first and last use include the older year.
    It writes copies of the database's own synthetic rows; it is never for a database that holds real records, and
    refuses unless -LabDatabase is given.

    The defaults add about 40 million interactions for 2024-09-26 to 2025-09-24 to CopilotScaleTest (120 million,
    2025-09-25 to 2026-09-24): a rollout from a seventh of the people at a quarter of their use to everyone at four
    fifths of it.

.EXAMPLE
    pwsh tools/dev/scale/Add-ScaleHistory.ps1 -SqlServer localhost -Database CopilotScaleTest -LabDatabase
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $SqlServer,
    [Parameter(Mandatory)] [string] $Database,
    [datetime] $From = '2024-09-26',
    [datetime] $To = '2025-09-24',
    [int] $ShiftDays = 364,
    [double] $PeopleFrom = 0.143,
    [double] $RowsFrom = 0.25,
    [double] $RowsTo = 0.80,
    [switch] $SkipDashboardRefresh,
    [switch] $LabDatabase
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not $LabDatabase) { throw 'This writes synthetic copies into the database. Give -LabDatabase to say it is a lab stress database.' }

$cs = "Server=$SqlServer;Database=$Database;Integrated Security=True;TrustServerCertificate=True;Application Name=Add-ScaleHistory"
$conn = New-Object System.Data.SqlClient.SqlConnection $cs
# Left false: true would show each progress line as it is sent, but would also turn the script's own errors
# (THROW, severity 16) into messages, so a failed run would look finished. The month lines arrive when the batch ends.
$conn.FireInfoMessageEventOnUserErrors = $false
$conn.add_InfoMessage({ param($s, $e) foreach ($m in $e.Errors) { Write-Host ("{0:HH:mm:ss} {1}" -f (Get-Date), $m.Message) } })
$conn.Open()
try {
    $sql = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Add-ScaleHistory.sql') -Raw
    $cmd = $conn.CreateCommand()
    $cmd.CommandTimeout = 0
    $cmd.CommandText = $sql
    [void] $cmd.Parameters.Add('@From', [System.Data.SqlDbType]::Date); $cmd.Parameters['@From'].Value = $From.Date
    [void] $cmd.Parameters.Add('@To', [System.Data.SqlDbType]::Date); $cmd.Parameters['@To'].Value = $To.Date
    [void] $cmd.Parameters.AddWithValue('@ShiftDays', $ShiftDays)
    [void] $cmd.Parameters.AddWithValue('@PeopleFrom', $PeopleFrom)
    [void] $cmd.Parameters.AddWithValue('@RowsFrom', $RowsFrom)
    [void] $cmd.Parameters.AddWithValue('@RowsTo', $RowsTo)
    Write-Host ("{0:HH:mm:ss} Adding {1:yyyy-MM-dd} to {2:yyyy-MM-dd} to {3} on {4}..." -f (Get-Date), $From, $To, $Database, $SqlServer)
    [void] $cmd.ExecuteNonQuery()

    if (-not $SkipDashboardRefresh) {
        Write-Host ("{0:HH:mm:ss} Dashboard totals, every day (dash.usp_RefreshDashboard)..." -f (Get-Date))
        $r = $conn.CreateCommand(); $r.CommandTimeout = 0
        $r.CommandText = 'EXEC dash.usp_RefreshDashboard @SinceUtc = NULL;'
        [void] $r.ExecuteNonQuery()
    }
    Write-Host ("{0:HH:mm:ss} ALL DONE" -f (Get-Date))
}
finally { $conn.Dispose() }
