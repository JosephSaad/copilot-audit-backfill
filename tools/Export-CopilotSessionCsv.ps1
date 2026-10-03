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
    Rebuild every session and the weekly rollup before exporting. These are
    the tables the nightly run keeps current and the dashboard and bi read, so
    the build uses the settings the last build used - its license databases
    and context rule - unless they are given here. A value different from the
    last build's is refused without -Force: the nightly run's next session
    build would stop with "different settings" until
    Invoke-CopilotMaintenance.ps1 -Task RebuildSessions. To compare against
    the old script's rule (-ContextFallback Location), build in a copy of the
    database.

.PARAMETER LicenseEventDatabase
    With -Build: the license-event database(s), comma-separated, so the
    license flag is the one held at the time of use. Default: the ones the
    last build read. See sql/Create-CopilotSessionProcedure.sql.

.PARAMETER ContextFallback
    With -Build: User, Location (the old script's rule) or None. Default: the
    last build's, or User for a first build.

.PARAMETER Force
    With -Build: build with a -LicenseEventDatabase or -ContextFallback other
    than the last build's, knowing the nightly run will then stop until the
    sessions are rebuilt with its own settings.

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
    [string]       $ContextFallback,
    [switch]       $Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$script:Bound = $PSBoundParameters

function Write-Log([string] $Message) {
    Write-Host ('{0:yyyy-MM-dd HH:mm:ss}Z [INFO ] {1}' -f [datetime]::UtcNow, $Message)
}

function Format-LicenseList([string] $List) {
    # As the session build compares and records it: each database once, trimmed, in order.
    @($List -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ } | Sort-Object -Unique) -join ','
}

function Resolve-BuildSetting {
    <#
        The settings -Build uses (2.4.12): those given, else the last build's.
        It passed its own defaults - User, and no license database when none was
        given - and rebuilt the production sessions under them, so the nightly
        run's next build stopped with 'different settings' every night until a
        RebuildSessions. A value given that differs from the last build's is
        refused without -Force. Pure, for the tests: $Last is the last
        audit.CopilotSessionRun row, or $null.
    #>
    param($Last, [System.Collections.IDictionary] $Given, [switch] $Force)
    $fallback = if ($Given.ContainsKey('ContextFallback') -and $Given.ContextFallback) { [string] $Given.ContextFallback }
                elseif ($Last) { [string] $Last.ContextFallback } else { 'User' }
    $lastLic = if ($Last -and $Last.LicenseEventDatabase -isnot [DBNull]) { Format-LicenseList ([string] $Last.LicenseEventDatabase) } else { '' }
    $lic = if ($Given.ContainsKey('LicenseEventDatabase')) { Format-LicenseList ([string] $Given.LicenseEventDatabase) } else { $lastLic }
    $changed = @()
    if ($Last) {
        if ($fallback -ne [string] $Last.ContextFallback) { $changed += "-ContextFallback $fallback (the last build used $($Last.ContextFallback))" }
        if ($lic -ne $lastLic) {
            $changed += "-LicenseEventDatabase '$lic' (the last build read $(if ($lastLic) { "'$lastLic'" } else { 'none' }))"
        }
    }
    if ($changed.Count -and -not $Force) {
        throw ("-Build rebuilds the sessions the nightly run keeps current, and $($changed -join ' and ') would make its next build stop with " +
               "'different settings' until Invoke-CopilotMaintenance.ps1 -Task RebuildSessions. Leave them out to build as the last build did, " +
               'build in a copy of the database to compare, or add -Force.')
    }
    [pscustomobject]@{ ContextFallback = $fallback; LicenseEventDatabase = $(if ($lic) { $lic } else { $null }); Changed = $changed }
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

function Invoke-Main {
    $conn = [System.Data.SqlClient.SqlConnection]::new((New-ExportConnectionString))
    $conn.Open()
    try {
        if ($Build) {
            $q = $conn.CreateCommand()
            $q.CommandText = @"
IF OBJECT_ID(N'audit.CopilotSessionRun', N'U') IS NULL
    SELECT CAST(NULL AS NVARCHAR(20)) AS ContextFallback, CAST(NULL AS NVARCHAR(1000)) AS LicenseEventDatabase WHERE 1 = 0;
ELSE
    SELECT TOP (1) ContextFallback, LicenseEventDatabase FROM audit.CopilotSessionRun ORDER BY RunId DESC;
"@
            $t = [System.Data.DataTable]::new()
            $r = $q.ExecuteReader()
            try { $t.Load($r) } finally { $r.Dispose() }
            $use = Resolve-BuildSetting -Last $(if ($t.Rows.Count) { $t.Rows[0] } else { $null }) -Given $script:Bound -Force:$Force
            foreach ($c in $use.Changed) { Write-Warning "Building with $c. The nightly run stops at its session build until Invoke-CopilotMaintenance.ps1 -Task RebuildSessions." }
            Write-Log ("Rebuilding every session: context rule {0}, license history {1}." -f $use.ContextFallback, $(if ($use.LicenseEventDatabase) { $use.LicenseEventDatabase } else { 'none' }))

            $conn.add_InfoMessage({ param($s, $e) foreach ($m in $e.Errors) { Write-Log ('SQL: ' + $m.Message) } })
            $cmd = $conn.CreateCommand()
            $cmd.CommandText = 'audit.usp_BuildCopilotSessions'
            $cmd.CommandType = [System.Data.CommandType]::StoredProcedure
            $cmd.CommandTimeout = 0
            [void] $cmd.Parameters.AddWithValue('@ContextFallback', $use.ContextFallback)
            if ($use.LicenseEventDatabase) { [void] $cmd.Parameters.AddWithValue('@LicenseEventDatabase', $use.LicenseEventDatabase) }
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
}

# Dot-sourcing (tests) loads the functions without running anything.
if ($MyInvocation.InvocationName -ne '.') { Invoke-Main }
