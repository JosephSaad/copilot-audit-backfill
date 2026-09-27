<#
.SYNOPSIS
    Writes the session summary and weekly rollup as the CSV files the old
    session script produced, so anything that read those files keeps working.

.DESCRIPTION
    Reads audit.vCopilotSessionSummary and audit.vCopilotWeeklyRollup - the
    views carry the files' exact column names and text formats - and writes

        <OutputFolder>\copilot_session_summary.csv
        <OutputFolder>\weekly_rollup.csv

    Every field is quoted, as Export-Csv writes them, and the files are UTF-8.
    Rows are streamed from SQL, so the size of the tables does not matter.
    Each file is written to a temporary name and renamed at the end, so a
    reader never sees half a file.

    With -Build, audit.usp_BuildCopilotSessions runs first. Without it, the
    files reflect the last build.

.PARAMETER SqlServer / -Database / -SqlCredential / -TrustServerCertificate / -ConnectionString
    Where the audit database lives. Same shape as Invoke-CopilotAuditBackfill.ps1.

.PARAMETER OutputFolder
    Where to write the files. Created if missing. Default: .\Output

.PARAMETER Build
    Rebuild the sessions and weekly rollup before exporting.

.PARAMETER LicenseEventDatabase
    With -Build: the license-event database(s), comma-separated, so the
    license flag is the one held at the time of use. See
    sql/Create-CopilotSessionProcedure.sql.

.PARAMETER ContextFallback
    With -Build: User (default), Location (the old script's rule) or None.

.EXAMPLE
    .\tools\Export-CopilotSessionCsv.ps1 -SqlServer SQLPROD01 -Database CopilotAudit -OutputFolder D:\Copilot\Output

.EXAMPLE
    .\tools\Export-CopilotSessionCsv.ps1 -SqlServer SQLPROD01 -Database CopilotAudit -Build `
        -LicenseEventDatabase 'CopilotLicenseEvents' -OutputFolder D:\Copilot\Output
#>
[CmdletBinding()]
param(
    [string]       $SqlServer,
    [string]       $Database,
    [pscredential] $SqlCredential,
    [switch]       $TrustServerCertificate,
    [string]       $ConnectionString,

    [string]       $OutputFolder = (Join-Path (Get-Location) 'Output'),
    [switch]       $Build,
    [string]       $LicenseEventDatabase,
    [ValidateSet('User', 'Location', 'None')]
    [string]       $ContextFallback = 'User'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Write-Log([string] $Message) {
    Write-Host ('{0:yyyy-MM-dd HH:mm:ss}Z [INFO ] {1}' -f [datetime]::UtcNow, $Message)
}

function New-ExportConnectionString {
    # The constructor overload: assigning .ConnectionString goes through the
    # builder's keyword indexer and throws (see Invoke-CopilotAuditBackfill.ps1).
    if ($ConnectionString) {
        return [System.Data.SqlClient.SqlConnectionStringBuilder]::new($ConnectionString).ConnectionString
    }
    if (-not $SqlServer -or -not $Database) { throw 'Specify -SqlServer and -Database (or -ConnectionString).' }
    $csb = [System.Data.SqlClient.SqlConnectionStringBuilder]::new()
    $csb['Data Source'] = $SqlServer
    $csb['Initial Catalog'] = $Database
    if ($SqlCredential) {
        $csb['User ID'] = $SqlCredential.UserName
        $csb['Password'] = $SqlCredential.GetNetworkCredential().Password
    }
    else {
        $csb['Integrated Security'] = $true
    }
    $csb['Encrypt'] = $true
    $csb['TrustServerCertificate'] = [bool] $TrustServerCertificate
    $csb['Application Name'] = 'CopilotSessionExport'
    return $csb.ConnectionString
}

function ConvertTo-CsvField([object] $Value) {
    if ($null -eq $Value -or $Value -is [DBNull]) { return '""' }
    $s = if ($Value -is [decimal] -or $Value -is [double]) {
        ([decimal] $Value).ToString([System.Globalization.CultureInfo]::InvariantCulture)
    } else { [string] $Value }
    return '"' + $s.Replace('"', '""') + '"'
}

function Export-View {
    param([System.Data.SqlClient.SqlConnection] $Connection, [string] $View, [string] $Path)

    $tmp = "$Path.tmp"
    $cmd = $Connection.CreateCommand()
    $cmd.CommandText = "SELECT * FROM $View"
    $cmd.CommandTimeout = 0
    $reader = $cmd.ExecuteReader([System.Data.CommandBehavior]::SequentialAccess)
    $writer = [System.IO.StreamWriter]::new($tmp, $false, [System.Text.UTF8Encoding]::new($false))
    $rows = 0
    try {
        $names = for ($i = 0; $i -lt $reader.FieldCount; $i++) { ConvertTo-CsvField $reader.GetName($i) }
        $writer.WriteLine(($names -join ','))
        $values = [object[]]::new($reader.FieldCount)
        while ($reader.Read()) {
            [void] $reader.GetValues($values)
            $writer.WriteLine((($values | ForEach-Object { ConvertTo-CsvField $_ }) -join ','))
            $rows++
        }
    }
    finally {
        $writer.Dispose()
        $reader.Dispose()
    }
    Move-Item -LiteralPath $tmp -Destination $Path -Force
    return $rows
}

$conn = [System.Data.SqlClient.SqlConnection]::new((New-ExportConnectionString))
$conn.Open()
try {
    if ($Build) {
        $conn.add_InfoMessage({ param($s, $e) foreach ($m in $e.Errors) { Write-Log ('SQL: ' + $m.Message) } })
        $cmd = $conn.CreateCommand()
        $cmd.CommandText = 'audit.usp_BuildCopilotSessions'
        $cmd.CommandType = [System.Data.CommandType]::StoredProcedure
        $cmd.CommandTimeout = 0
        [void] $cmd.Parameters.AddWithValue('@ContextFallback', $ContextFallback)
        if ($LicenseEventDatabase) { [void] $cmd.Parameters.AddWithValue('@LicenseEventDatabase', $LicenseEventDatabase) }
        [void] $cmd.ExecuteNonQuery()
    }

    if (-not (Test-Path -LiteralPath $OutputFolder)) { [void] (New-Item -ItemType Directory -Path $OutputFolder) }

    $sessionFile = Join-Path $OutputFolder 'copilot_session_summary.csv'
    $weeklyFile = Join-Path $OutputFolder 'weekly_rollup.csv'
    $n = Export-View -Connection $conn -View 'audit.vCopilotSessionSummary' -Path $sessionFile
    Write-Log ("{0:N0} session(s) -> {1}" -f $n, $sessionFile)
    $n = Export-View -Connection $conn -View 'audit.vCopilotWeeklyRollup' -Path $weeklyFile
    Write-Log ("{0:N0} weekly row(s) -> {1}" -f $n, $weeklyFile)
}
finally {
    $conn.Dispose()
}
