<#
.SYNOPSIS
    Backfills Microsoft 365 Copilot audit records (CopilotInteraction) from
    Microsoft Purview straight into SQL Server - fast, resumable, and within
    Microsoft's published Audit Search API limits.

.DESCRIPTION
    Pipeline: Purview audit searches -> parallel page downloads -> parallel
    SqlBulkCopy. Nothing is written to CSV.

      * Sign-in: Microsoft Graph PowerShell SDK, delegated, in a browser
        (username, password, MFA). The SDK's token is handed to a compiled
        .NET engine and refreshed automatically for the rest of the run.
      * Windows are sized from observed record density to stay well under the
        ~1,000,000-records-per-search cap; any search the service flags as
        over the cap is split BEFORE it is downloaded.
      * Stays under the tenant's rolling daily submission budget (~200 per 24h,
        shared tenant-wide) and the concurrent-search limit.
      * Page downloads run in parallel under an adaptive limit: it steps up
        while clean, halves on any 429, and every stream waits out Retry-After.
      * SQL loading runs alongside the downloads - several SqlBulkCopy
        writers into a heap with TABLOCK (bulk-update locks, minimal logging).
      * All state lives in SQL (audit.CopilotBackfillWindow). Ctrl+C, a crash
        or a reboot loses nothing: run the same command again to resume.

    After the backfill, -Finalize builds audit.CopilotAudit (columnstore,
    deduplicated) and audit.CopilotInteraction (shredded, for reporting).

.PARAMETER SqlServer
    SQL Server instance, e.g. SQLPROD01 or SQLPROD01\INST1 or sql.contoso.com,1433.

.PARAMETER Database
    Target database. The script creates the 'audit' schema objects it needs.

.PARAMETER SqlCredential
    SQL authentication. Omit to use Windows integrated authentication.

.PARAMETER TrustServerCertificate
    Encrypt the SQL connection without validating the server certificate. Use
    only when the instance presents a self-signed certificate.

.PARAMETER ConnectionString
    Full connection string; overrides -SqlServer/-Database/-SqlCredential.

.PARAMETER StartDate
    UTC. Defaults to 365 days before -EndDate. Copilot audit records are kept
    for 180 days unless a custom audit retention policy covering them was in
    place - check Get-UnifiedAuditLogRetentionPolicy before asking for more.

.PARAMETER EndDate
    UTC. Defaults to the start of today (UTC), so no partially ingested day is
    captured. Keep the data current from there with scheduled -Incremental runs.

.PARAMETER TenantId
    Tenant GUID or contoso.onmicrosoft.com. Strongly recommended: pins sign-in
    to the right tenant.

.PARAMETER AuthMode
    Browser (default) or DeviceCode (server with no usable browser: sign in
    from any other device at https://microsoft.com/devicelogin).

.PARAMETER Finalize
    After the backfill completes, build the final and reporting tables.

.PARAMETER FinalizeOnly
    Skip the backfill; just run the finalize step.

.PARAMETER Status
    Print the backfill's saved progress and exit (safe while a run is active).

.PARAMETER Incremental
    Keep a finalized database current: load from the end of the previous load
    (or of the backfill) up to the start of the current hour, then append the
    new records to audit.CopilotAudit and audit.CopilotInteraction with
    audit.usp_MergeCopilotLoad - no rebuild. Meant to run on a schedule. A load
    interrupted part-way resumes on the next run. -StartDate is ignored; pass
    -EndDate only to stop short of now.

.PARAMETER LookbackHours
    With -Incremental: how far before the previous load's end each load starts
    again (default 48). Purview can deliver a record hours after the event, so
    the overlap is searched again; records already loaded are counted and
    skipped, never duplicated.

.PARAMETER SkipPostProcessing
    With -Incremental: only load and merge. By default the run then brings the
    derived tables up to date as well, for each one installed: the flattened
    table (usp_FlattenCopilotAudit, from the load's start), license events
    (usp_ShredLicenseEvents), sessions (usp_BuildCopilotSessions) and the bi
    star schema (bi.usp_RefreshBi, from the load's start). A refresh that is
    skipped, or stops part way, stays owed (audit.CopilotLoad.DerivedRefreshedUtc
    is left empty): the next run without this switch refreshes from the
    earliest load or CSV import that owes one, even when there is nothing new
    to load.

.PARAMETER SessionLicenseDatabase
    With -Incremental: passed to usp_BuildCopilotSessions and bi.usp_RefreshBi
    as @LicenseEventDatabase (one database, or several comma-separated), so
    each session and interaction carries the license held at the time.

.PARAMETER RecordTypes
    Purview record types to load whole (2.6.9), for example
    ConnectedAIAppInteraction, PowerBIAudit and PowerAppsApp: the activity
    database. Without -Operations, no operation filter is sent. The database
    records them as rt:<type> beside its operations, so it keeps one set: use
    a database of its own, never the Copilot or license database.

.PARAMETER CertificateThumbprint
    App-only sign-in, for a scheduled run with nobody to sign in: the
    thumbprint of a certificate in the CurrentUser or LocalMachine store whose
    public key is uploaded to the app registration given by -ClientId. The app
    needs the Microsoft Graph APPLICATION permission AuditLogsQuery.Read.All,
    admin-consented. Requires -ClientId and -TenantId.

.EXAMPLE
    .\Invoke-CopilotAuditBackfill.ps1 -SqlServer SQLPROD01 -Database CopilotAudit -TenantId contoso.onmicrosoft.com

.EXAMPLE
    # Resume after Ctrl+C / reboot: the same command again.
    .\Invoke-CopilotAuditBackfill.ps1 -SqlServer SQLPROD01 -Database CopilotAudit -TenantId contoso.onmicrosoft.com -Finalize

.EXAMPLE
    # Daily, from Task Scheduler, once the backfill is finalized: app-only sign-in.
    .\Invoke-CopilotAuditBackfill.ps1 -SqlServer SQLPROD01 -Database CopilotAudit -Incremental `
        -TenantId contoso.onmicrosoft.com -ClientId <app-id> -CertificateThumbprint <thumbprint>

.EXAMPLE
    # Smoke test first: one day.
    .\Invoke-CopilotAuditBackfill.ps1 -SqlServer SQLDEV01 -Database CopilotAuditTest -TenantId contoso.onmicrosoft.com `
        -StartDate (Get-Date).ToUniversalTime().Date.AddDays(-2) -EndDate (Get-Date).ToUniversalTime().Date.AddDays(-1) -Finalize
#>

#Requires -Version 7.2

[CmdletBinding(DefaultParameterSetName = 'Server')]
param(
    [Parameter(ParameterSetName = 'Server')] [string] $SqlServer,
    [Parameter(ParameterSetName = 'Server')] [string] $Database,
    [Parameter(ParameterSetName = 'Server')] [pscredential] $SqlCredential,
    [Parameter(ParameterSetName = 'Server')] [switch] $TrustServerCertificate,
    [Parameter(ParameterSetName = 'ConnStr')] [string] $ConnectionString,

    [datetime] $StartDate,
    [datetime] $EndDate,

    [string]   $TenantId,
    [ValidateSet('Browser', 'DeviceCode')]
    [string]   $AuthMode = 'Browser',
    [string]   $ClientId,
    [string]   $CertificateThumbprint,

    [string[]] $Operations = @('CopilotInteraction'),
    [string[]] $RecordTypes,

    [switch]   $Finalize,
    [switch]   $FinalizeOnly,
    [switch]   $Status,

    [switch]   $Incremental,
    [ValidateRange(0, 168)]       [int]    $LookbackHours          = 48,
    [switch]   $SkipPostProcessing,
    [string]   $SessionLicenseDatabase,

    # --- tuning (defaults are deliberately inside Microsoft's published limits)
    [ValidateRange(1, 32)]        [int]    $MaxDownloadStreams     = 8,
    [ValidateRange(1, 32)]        [int]    $InitialDownloadStreams = 4,
    [ValidateRange(1, 40)]        [int]    $MaxQueriesInFlight     = 8,
    [ValidateRange(1, 200)]       [int]    $MaxSubmissionsPer24h   = 150,
    [ValidateRange(30, 1440)]     [int]    $QueryTimeoutMinutes    = 240,
    [ValidateRange(10000, 5000000)][long]  $TargetRecordsPerWindow = 600000,
    [ValidateRange(0.25, 168)]    [double] $InitialWindowHours     = 24,
    [ValidateRange(1, 16)]        [int]    $SqlWriters             = 4,
    [ValidateRange(100, 50000)]   [int]    $BatchRows              = 2000,
    [ValidateRange(10, 1000)]     [int]    $PageSize               = 1000,

    [string]   $LogPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# A record-type load asks for whole record types, and for no operation unless -Operations is given too.
if ($RecordTypes -and -not $PSBoundParameters.ContainsKey('Operations')) { $Operations = @() }

# A 'Modules' folder next to this script takes precedence over the machine's
# module paths. That is how a server with no PSGallery access gets
# Microsoft.Graph.Authentication (tools\Save-CopilotAuditModules.ps1); when the
# folder is absent this does nothing and module resolution is unchanged.
$script:BundledModules = Join-Path $PSScriptRoot 'Modules'
if (Test-Path -LiteralPath $script:BundledModules -PathType Container) {
    $sep = [System.IO.Path]::PathSeparator
    $existing = ($env:PSModulePath -split [regex]::Escape($sep)) | Where-Object { $_ -ne $script:BundledModules }
    $env:PSModulePath = (@($script:BundledModules) + $existing) -join $sep
}

. (Join-Path $PSScriptRoot 'Import-CopilotAuditEngine.ps1')
. (Join-Path $PSScriptRoot 'tools' 'RunLock.ps1')   # who holds the run lock, when it is held (3.1.3)

# ===========================================================================
# Logging
# ===========================================================================

$script:LogFile = $null

function Write-LogLine {
    param([string] $Line)
    $color = if ($Line -match '\[ERROR\]') { 'Red' } elseif ($Line -match '\[WARN') { 'Yellow' } else { $null }
    if ($Line -match '\[DEBUG\]') { Write-Verbose $Line }
    elseif ($color) { Write-Host $Line -ForegroundColor $color }
    else { Write-Host $Line }
    if ($script:LogFile) { [System.IO.File]::AppendAllText($script:LogFile, $Line + [Environment]::NewLine) }
}

function Write-Log {
    param([Parameter(Mandatory)][string] $Message, [ValidateSet('INFO', 'WARN', 'ERROR', 'DEBUG')][string] $Level = 'INFO')
    Write-LogLine ('{0:yyyy-MM-dd HH:mm:ss}Z [{1,-5}] {2}' -f [datetime]::UtcNow, $Level, $Message)
}

# ===========================================================================
# SQL helpers
# ===========================================================================

function New-SqlConnectionString {
    # Built from the string, not assigned afterwards: the builder is a
    # DbConnectionStringBuilder, and PowerShell routes $b.ConnectionString = x
    # through its keyword indexer, which rejects 'ConnectionString' as a
    # keyword. The constructor overload is the only form that works.
    $csb = if ($ConnectionString) {
        [System.Data.SqlClient.SqlConnectionStringBuilder]::new($ConnectionString)
    }
    else {
        [System.Data.SqlClient.SqlConnectionStringBuilder]::new()
    }
    if (-not $ConnectionString) {
        if (-not $SqlServer -or -not $Database) { throw 'Specify -SqlServer and -Database (or -ConnectionString).' }
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
    }
    $csb['Application Name'] = 'CopilotAuditBackfill'
    if ($csb['Max Pool Size'] -lt ($SqlWriters * 2 + 10)) { $csb['Max Pool Size'] = $SqlWriters * 2 + 10 }
    return $csb.ConnectionString
}

function Invoke-Sql {
    <#
        Runs one batch. -As NonQuery | Scalar | Table. PRINT / RAISERROR(0)
        messages are logged live.
    #>
    param(
        [Parameter(Mandatory)][string] $Sql,
        [ValidateSet('NonQuery', 'Scalar', 'Table')][string] $As = 'NonQuery',
        [hashtable] $Parameters = @{},
        [int] $TimeoutSeconds = 120
    )
    $conn = [System.Data.SqlClient.SqlConnection]::new($script:SqlConnStr)
    $conn.add_InfoMessage({ param($source, $e) foreach ($m in $e.Errors) { Write-Log ("SQL: " + $m.Message) } })
    try {
        $conn.Open()
        $cmd = $conn.CreateCommand()
        $cmd.CommandText = $Sql
        $cmd.CommandTimeout = $TimeoutSeconds
        foreach ($k in $Parameters.Keys) {
            $v = $Parameters[$k]
            [void] $cmd.Parameters.AddWithValue($k, $(if ($null -eq $v) { [DBNull]::Value } else { $v }))
        }
        switch ($As) {
            'Scalar' { return $cmd.ExecuteScalar() }
            'Table'  {
                $dt = [System.Data.DataTable]::new()
                $reader = $cmd.ExecuteReader()
                try { $dt.Load($reader) } finally { $reader.Dispose() }
                return ,$dt
            }
            default  { [void] $cmd.ExecuteNonQuery() }
        }
    }
    finally { $conn.Dispose() }
}

function Invoke-SqlScript {
    <#
        Runs a script file that uses GO batch separators, as the stored
        procedure scripts do (CREATE/ALTER PROCEDURE must start a batch).
        Splits the way sqlcmd does: GO alone on a line. \r? because a Windows
        checkout has CRLF endings and .NET's multiline $ matches before \n only.
    #>
    param([Parameter(Mandatory)][string] $Path, [int] $TimeoutSeconds = 600)
    $text = Get-Content -LiteralPath $Path -Raw
    foreach ($batch in [regex]::Split($text, '(?im)^[ \t]*GO[ \t]*(?:--[^\r\n]*)?\r?$')) {
        if (-not [string]::IsNullOrWhiteSpace($batch)) { Invoke-Sql -Sql $batch -TimeoutSeconds $TimeoutSeconds }
    }
}

function Initialize-CopilotAuditDatabase {
    try {
        Invoke-Sql -Sql 'SELECT 1' -As Scalar | Out-Null
    }
    catch {
        $m = $_.Exception.GetBaseException().Message
        if ($m -match 'certificate') {
            throw "Could not connect to SQL Server: $m`nIf the instance uses a self-signed certificate, add -TrustServerCertificate."
        }
        throw "Could not connect to SQL Server: $m"
    }
    Invoke-Sql -Sql (Get-Content -LiteralPath (Join-Path $PSScriptRoot 'sql' 'Create-CopilotAuditSchema.sql') -Raw)
    Write-Log 'SQL schema ready (audit.CopilotBackfillRange / CopilotBackfillWindow / CopilotAuditStage).'
}

function Enter-RunLock {
    <#
        Holds the database for this run alone, as the nightly pipeline, Install-CopilotAudit.ps1 and the
        maintenance rebuilds do: an exclusive application lock, on a connection of its own kept open to the end of the
        run. A load run by hand (-Incremental, or a history range) took none, so it could run beside the nightly and
        load the same windows twice. As one of the pipeline's own steps, the pipeline holds it already
        (CopilotAuditRunLock). Returns the connection (dispose it to let go), or nothing under the pipeline's lock.
    #>
    $b = [System.Data.SqlClient.SqlConnectionStringBuilder]::new($script:SqlConnStr)
    if ($env:CopilotAuditRunLock -eq "$($b['Data Source'])/$($b['Initial Catalog'])") { return $null }
    $b['Pooling'] = $false   # closing a pooled connection keeps its session, and the session's lock with it
    $c = [System.Data.SqlClient.SqlConnection]::new($b.ConnectionString)
    try {
        $c.Open()
        $cmd = $c.CreateCommand()
        $cmd.CommandText = "DECLARE @r INT; EXEC @r = sp_getapplock @Resource = N'CopilotAudit.Run', @LockMode = 'Exclusive', @LockOwner = 'Session', @LockTimeout = 0; SELECT @r;"
        $granted = [int] $cmd.ExecuteScalar() -ge 0
    }
    catch { $c.Dispose(); throw }
    if (-not $granted) {
        $c.Dispose()
        throw "Another run is using $($b['Initial Catalog']) now: the nightly pipeline, the installer, a maintenance rebuild or another load. Nothing was loaded. $(Get-RunLockHolder $b.ConnectionString)"
    }
    $c
}

function ConvertTo-UtcDate {
    param([Parameter(Mandatory)][datetime] $Value)
    switch ($Value.Kind) {
        'Utc'   { return $Value }
        'Local' { return $Value.ToUniversalTime() }
        default { return [datetime]::SpecifyKind($Value, [DateTimeKind]::Utc) }   # -StartDate 2025-09-22 means UTC
    }
}

function Get-AuditFilterSet {
    <#
        What a database records it loads: its operations, and each
        whole record type as rt:<type>, so a record-type database can never be
        resumed or loaded with operations, nor the other way round. Pure, for
        the tests.
    #>
    param([string[]] $Operations, [string[]] $RecordTypes)
    $set = @($Operations | Where-Object { $_ } | ForEach-Object { $_.Trim() }) +
           @($RecordTypes | Where-Object { $_ } | ForEach-Object { 'rt:' + $_.Trim() })
    if ($set.Count -eq 0) { throw 'Nothing to load: give -Operations or -RecordTypes.' }
    , [string[]] @($set | Sort-Object -Unique)
}

function Resolve-BackfillRange {
    <#
        First run: records the requested range. Later runs: the stored range
        wins (so a resume never needs the original dates re-typed), except that
        an earlier -StartDate extends it.
    #>
    $ops = (Get-AuditFilterSet -Operations $Operations -RecordTypes $RecordTypes) -join ','
    $existing = Invoke-Sql -As Table -Sql 'SELECT RangeStartUtc, RangeEndUtc, Operations, CompletedUtc FROM audit.CopilotBackfillRange WHERE Id = 1'

    if ($existing.Rows.Count -eq 0) {
        $end = if ($EndDate) { ConvertTo-UtcDate $EndDate } else { [datetime]::UtcNow.Date }
        $start = if ($StartDate) { ConvertTo-UtcDate $StartDate } else { $end.AddDays(-365) }
        $start = [datetime]::new($start.Year, $start.Month, $start.Day, $start.Hour, $start.Minute, 0, [DateTimeKind]::Utc)
        $end   = [datetime]::new($end.Year, $end.Month, $end.Day, $end.Hour, $end.Minute, 0, [DateTimeKind]::Utc)
        if ($start -ge $end) { throw 'StartDate must be earlier than EndDate.' }

        Invoke-Sql -Sql 'INSERT INTO audit.CopilotBackfillRange (Id, RangeStartUtc, RangeEndUtc, Operations) VALUES (1, @s, @e, @o)' `
                   -Parameters @{ '@s' = $start; '@e' = $end; '@o' = $ops }
        Write-Log ('New backfill: {0:yyyy-MM-dd HH:mm}Z -> {1:yyyy-MM-dd HH:mm}Z ({2:N0} days), operations {3}.' -f $start, $end, ($end - $start).TotalDays, $ops)
        return [pscustomobject]@{ Start = $start; End = $end; Resumed = $false; Completed = $false }
    }

    $row = $existing.Rows[0]
    $start = [datetime]::SpecifyKind($row.RangeStartUtc, [DateTimeKind]::Utc)
    $end   = [datetime]::SpecifyKind($row.RangeEndUtc, [DateTimeKind]::Utc)
    if ($row.Operations -ne $ops) {
        throw "This database holds a backfill of '$($row.Operations)', not '$ops'. Use a separate database for other operations."
    }
    if ($StartDate -and (ConvertTo-UtcDate $StartDate) -lt $start) {
        $start = ConvertTo-UtcDate $StartDate
        Invoke-Sql -Sql 'UPDATE audit.CopilotBackfillRange SET RangeStartUtc = @s, CompletedUtc = NULL WHERE Id = 1' -Parameters @{ '@s' = $start }
        Write-Log ('Extending the backfill back to {0:yyyy-MM-dd HH:mm}Z.' -f $start)
        $completed = $false
    }
    else {
        if ($StartDate -and (ConvertTo-UtcDate $StartDate) -ne $start) {
            Write-Log ('-StartDate ignored: this backfill started at {0:yyyy-MM-dd HH:mm}Z (only an earlier start can be added).' -f $start) -Level WARN
        }
        $completed = $row.CompletedUtc -isnot [DBNull]
    }
    if ($EndDate -and (ConvertTo-UtcDate $EndDate) -ne $end) {
        Write-Log ('-EndDate ignored: this backfill ends at {0:yyyy-MM-dd HH:mm}Z. Use a separate database for a different end.' -f $end) -Level WARN
    }
    Write-Log ('Resuming backfill {0:yyyy-MM-dd HH:mm}Z -> {1:yyyy-MM-dd HH:mm}Z.' -f $start, $end)
    return [pscustomobject]@{ Start = $start; End = $end; Resumed = $true; Completed = $completed }
}

function Show-BackfillState {
    $range = Invoke-Sql -As Table -Sql 'SELECT RangeStartUtc, RangeEndUtc, Operations, StartedUtc, CompletedUtc FROM audit.CopilotBackfillRange WHERE Id = 1'
    if ($range.Rows.Count -eq 0) { Write-Log 'No backfill recorded in this database yet.'; return }
    $r = $range.Rows[0]
    Write-Log ('Range {0:yyyy-MM-dd HH:mm}Z -> {1:yyyy-MM-dd HH:mm}Z | {2} | {3}' -f $r.RangeStartUtc, $r.RangeEndUtc, $r.Operations,
               $(if ($r.CompletedUtc -is [DBNull]) { 'not complete' } else { 'completed {0:yyyy-MM-dd HH:mm}Z' -f $r.CompletedUtc }))

    $w = Invoke-Sql -As Table -Sql @'
SELECT Status, COUNT(*) AS Windows, SUM(ISNULL(RowsLoaded, 0)) AS RowsLoaded,
       CAST(SUM(DATEDIFF(MINUTE, StartUtc, EndUtc)) / 60.0 AS DECIMAL(12,1)) AS Hours
FROM audit.CopilotBackfillWindow GROUP BY Status ORDER BY Status
'@
    foreach ($row in $w.Rows) {
        Write-Log ('  {0,-12} {1,6:N0} windows  {2,7:N1} h  {3,14:N0} rows' -f $row.Status, $row.Windows, $row.Hours, $row.RowsLoaded)
    }
    $stage = Invoke-Sql -As Scalar -Sql "SELECT SUM(rows) FROM sys.partitions WHERE object_id = OBJECT_ID(N'audit.CopilotAuditStage') AND index_id IN (0, 1)"
    Write-Log ('  staging table holds ~{0:N0} rows (including superseded attempts)' -f $stage)

    $loads = Invoke-Sql -As Table -Sql @'
SELECT TOP (5) LoadId, RangeStartUtc, RangeEndUtc, DownloadedUtc, MergedUtc, RowsAdded, RowsAlreadyLoaded
FROM audit.CopilotLoad ORDER BY LoadId DESC
'@
    if ($loads.Rows.Count -gt 0) {
        Write-Log 'Incremental loads (latest first):'
        foreach ($l in $loads.Rows) {
            $state = if ($l.MergedUtc -isnot [DBNull]) { 'merged {0:yyyy-MM-dd HH:mm}Z, {1:N0} new, {2:N0} already loaded' -f $l.MergedUtc, $l.RowsAdded, $l.RowsAlreadyLoaded }
                     elseif ($l.DownloadedUtc -isnot [DBNull]) { 'downloaded, not yet merged' }
                     else { 'in progress' }
            Write-Log ('  #{0,-4} {1:yyyy-MM-dd HH:mm}Z -> {2:yyyy-MM-dd HH:mm}Z  {3}' -f $l.LoadId, $l.RangeStartUtc, $l.RangeEndUtc, $state)
        }
    }
}

function Invoke-CopilotAuditFinalize {
    # An activity database builds no audit.CopilotInteraction (Finalize-CopilotAudit.sql); its own shred follows.
    $activity = [bool] (Invoke-Sql -As Scalar -Sql "SELECT CASE WHEN OBJECT_ID(N'audit.usp_ShredActivity', N'P') IS NULL THEN 0 ELSE 1 END")
    Write-Log ('Finalizing: verifying coverage, building audit.CopilotAudit' + $(if ($activity) { ' (an activity database: its own shred follows)...' } else { ' and audit.CopilotInteraction...' }))
    $sql = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'sql' 'Finalize-CopilotAudit.sql') -Raw
    try {
        $summary = Invoke-Sql -As Table -Sql $sql -TimeoutSeconds 0
    }
    catch {
        throw ('Finalize stopped: ' + $_.Exception.GetBaseException().Message)
    }
    if ($summary.Rows.Count -gt 0) {
        $s = $summary.Rows[0]
        Write-Log ('Final: {0:N0} records | {1:N0} duplicates removed | {2:yyyy-MM-dd} -> {3:yyyy-MM-dd} | {4:N0} users' -f `
                   $s.FinalRows, $s.DuplicatesRemoved, $s.OldestRecordUtc, $s.NewestRecordUtc, $s.DistinctUsers)
    }
    # An activity database's history is shredded once it is final, so the rollup that follows has it.
    if ([bool] (Invoke-Sql -As Scalar -Sql "SELECT CASE WHEN OBJECT_ID(N'audit.usp_ShredActivity', N'P') IS NULL THEN 0 ELSE 1 END")) {
        Write-Log 'Shredding every AI app, Power BI and Power Apps record...'
        Invoke-Sql -Sql 'EXEC audit.usp_ShredActivity;' -TimeoutSeconds 0
    }
}

# ===========================================================================
# Incremental loads
# ===========================================================================

function Get-OperationsChange {
    <#
        Whether a load may ask for these operations in a database that recorded
        others: the operations it adds (none when they are the same), or $null
        when it would drop one. Pure, for the tests.
    #>
    param([string] $Recorded, [string[]] $Requested)
    $had = @($Recorded -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ })
    $want = @($Requested | ForEach-Object { $_.Trim() } | Where-Object { $_ } | Sort-Object -Unique)
    if (@($had | Where-Object { $_ -notin $want }).Count -gt 0) { return $null }
    , @($want | Where-Object { $_ -notin $had })
}

function Resolve-LoadRange {
    <#
        The next incremental load's range, from where the data ends. It starts
        LookbackHours earlier, because Purview can deliver a record hours after
        the event and the overlap must be searched again. It ends at EndUtc if
        given, else at the start of the current hour - a whole hour, so a load
        never catches an hour half-ingested and misses its tail. Returns $null
        when there is nothing after BaseEndUtc to load.
    #>
    param(
        [Parameter(Mandatory)][datetime] $BaseEndUtc,
        [Parameter(Mandatory)][datetime] $NowUtc,
        [int] $LookbackHours = 48,
        [Nullable[datetime]] $EndUtc
    )
    # PowerShell hands a [Nullable[datetime]] over already unwrapped: no .Value.
    $e = if ($null -ne $EndUtc) { [datetime] $EndUtc } else { $NowUtc }
    $end = if ($null -ne $EndUtc) {
        [datetime]::new($e.Year, $e.Month, $e.Day, $e.Hour, $e.Minute, 0, [DateTimeKind]::Utc)
    } else {
        [datetime]::new($e.Year, $e.Month, $e.Day, $e.Hour, 0, 0, [DateTimeKind]::Utc)
    }
    $base = [datetime]::SpecifyKind($BaseEndUtc, [DateTimeKind]::Utc)
    if ($end -le $base) { return $null }
    return [pscustomobject]@{ Start = $base.AddHours(-$LookbackHours); End = $end }
}

function Get-IncrementalLoad {
    <#
        The load to work on: the unfinished one if a previous run stopped, else
        a new one continuing from the last merged load - or, the first time,
        from the end of the finalized backfill.
    #>
    $filters = Get-AuditFilterSet -Operations $Operations -RecordTypes $RecordTypes
    $ops = $filters -join ','

    $pending = Invoke-Sql -As Table -Sql 'SELECT TOP (1) LoadId, RangeStartUtc, RangeEndUtc, Operations, DownloadedUtc FROM audit.CopilotLoad WHERE MergedUtc IS NULL ORDER BY LoadId'
    if ($pending.Rows.Count -gt 0) {
        $p = $pending.Rows[0]
        $widen = Get-OperationsChange -Recorded $p.Operations -Requested $filters
        if ($null -eq $widen) { throw "Load $($p.LoadId) in this database is of '$($p.Operations)', not '$ops'." }
        if ($widen.Count -gt 0) {
            Invoke-Sql -Sql 'UPDATE audit.CopilotLoad SET Operations = @o WHERE LoadId = @id' -Parameters @{ '@o' = $ops; '@id' = [int] $p.LoadId }
            Write-Log "Load $($p.LoadId) now also asks for: $($widen -join ', ')."
        }
        $downloaded = $p.DownloadedUtc -isnot [DBNull]
        $load = [pscustomobject]@{
            LoadId = [int] $p.LoadId
            Start = [datetime]::SpecifyKind($p.RangeStartUtc, [DateTimeKind]::Utc)
            End = [datetime]::SpecifyKind($p.RangeEndUtc, [DateTimeKind]::Utc)
            Downloaded = $downloaded
        }
        Write-Log ('Resuming load {0}: {1:yyyy-MM-dd HH:mm}Z -> {2:yyyy-MM-dd HH:mm}Z{3}.' -f $load.LoadId, $load.Start, $load.End,
                   $(if ($downloaded) { ', already downloaded - merging' } else { '' }))
        return $load
    }

    $backfill = Invoke-Sql -As Table -Sql 'SELECT RangeEndUtc, Operations, CompletedUtc FROM audit.CopilotBackfillRange WHERE Id = 1'
    if ($backfill.Rows.Count -eq 0 -or $backfill.Rows[0].CompletedUtc -is [DBNull]) {
        throw 'Run a backfill to completion in this database first: incremental loads continue from where it ends.'
    }
    $added = Get-OperationsChange -Recorded $backfill.Rows[0].Operations -Requested $filters
    if ($null -eq $added) {
        throw "This database holds a backfill of '$($backfill.Rows[0].Operations)', not '$ops'. Pass the same -Operations, or more of them."
    }
    if ($added.Count -gt 0) {
        # 2.3.0 added Copilot Studio's authoring operations to the license-event load: they load from now on.
        Invoke-Sql -Sql 'UPDATE audit.CopilotBackfillRange SET Operations = @o WHERE Id = 1' -Parameters @{ '@o' = $ops }
        Write-Log "Loading $($added -join ', ') too from now on. Earlier records of them are not loaded into this database."
    }
    if ((Invoke-Sql -As Scalar -Sql "SELECT CASE WHEN OBJECT_ID(N'audit.CopilotAudit', N'U') IS NULL THEN 0 ELSE 1 END") -eq 0) {
        throw 'The backfill has not been finalized. Run -FinalizeOnly first: loads are appended to audit.CopilotAudit.'
    }

    $lastMerged = Invoke-Sql -As Scalar -Sql 'SELECT MAX(RangeEndUtc) FROM audit.CopilotLoad WHERE MergedUtc IS NOT NULL'
    $base = if ($null -eq $lastMerged -or $lastMerged -is [DBNull]) { $backfill.Rows[0].RangeEndUtc } else { $lastMerged }
    $endArg = if ($EndDate) { ConvertTo-UtcDate $EndDate } else { $null }
    $range = Resolve-LoadRange -BaseEndUtc $base -NowUtc ([datetime]::UtcNow) -LookbackHours $LookbackHours -EndUtc $endArg
    if (-not $range) {
        Write-Log ('Nothing to load: the data already reaches {0:yyyy-MM-dd HH:mm}Z.' -f $base)
        return $null
    }

    # Leftovers from a load whose merge committed but whose clean-up did not.
    Invoke-Sql -Sql 'TRUNCATE TABLE audit.CopilotLoadStage; TRUNCATE TABLE audit.CopilotLoadWindow;'
    $id = Invoke-Sql -As Scalar -Parameters @{ '@s' = $range.Start; '@e' = $range.End; '@o' = $ops } -Sql @'
INSERT INTO audit.CopilotLoad (RangeStartUtc, RangeEndUtc, Operations) OUTPUT INSERTED.LoadId VALUES (@s, @e, @o);
'@
    Write-Log ('New load {0}: {1:yyyy-MM-dd HH:mm}Z -> {2:yyyy-MM-dd HH:mm}Z ({3:N0} h, the first {4} h a lookback over data already loaded).' -f `
               $id, $range.Start, $range.End, ($range.End - $range.Start).TotalHours, $LookbackHours)
    return [pscustomobject]@{ LoadId = [int] $id; Start = $range.Start; End = $range.End; Downloaded = $false }
}

function Invoke-DerivedTableRefresh {
    <#
        After a merge, brings each installed derived table up to date: the
        flattened table and license events from the load's start (both MERGE,
        so the lookback's records are updated in place), then sessions from
        the load's start's day (one transaction), then the bi star schema from
        the load's start, then the dashboard's rollups.
    #>
    param([Parameter(Mandatory)][datetime] $SinceUtc)
    function Test-Proc([string] $Name) {
        return [bool] (Invoke-Sql -As Scalar -Sql "SELECT CASE WHEN OBJECT_ID(N'$Name', N'P') IS NULL THEN 0 ELSE 1 END")
    }
    if (Test-Proc 'audit.usp_FlattenCopilotAudit') {
        Write-Log ('Flattening records from {0:yyyy-MM-dd HH:mm}Z...' -f $SinceUtc)
        Invoke-Sql -Sql 'EXEC audit.usp_FlattenCopilotAudit @SinceUtc = @s;' -Parameters @{ '@s' = $SinceUtc } -TimeoutSeconds 0
    }
    if (Test-Proc 'audit.usp_ShredLicenseEvents') {
        Write-Log ('Shredding license events from {0:yyyy-MM-dd HH:mm}Z...' -f $SinceUtc)
        Invoke-Sql -Sql 'EXEC audit.usp_ShredLicenseEvents @SinceUtc = @s;' -Parameters @{ '@s' = $SinceUtc } -TimeoutSeconds 0
    }
    if (Test-Proc 'audit.usp_ShredAgentEvents') {
        Write-Log ('Shredding agent build and publish events from {0:yyyy-MM-dd HH:mm}Z...' -f $SinceUtc)
        Invoke-Sql -Sql 'EXEC audit.usp_ShredAgentEvents @SinceUtc = @s;' -Parameters @{ '@s' = $SinceUtc } -TimeoutSeconds 0
    }
    if (Test-Proc 'audit.usp_ShredActivity') {
        Write-Log ('Shredding AI app, Power BI and Power Apps activity from {0:yyyy-MM-dd HH:mm}Z...' -f $SinceUtc)
        Invoke-Sql -Sql 'EXEC audit.usp_ShredActivity @SinceUtc = @s;' -Parameters @{ '@s' = $SinceUtc } -TimeoutSeconds 0
    }
    if (Test-Proc 'audit.usp_BuildCopilotSessions') {
        $licDb = $(if ($SessionLicenseDatabase) { $SessionLicenseDatabase } else { $null })
        # Incremental from the load's start where the installed procedure can
        # (it rebuilds only the days from then on); a version before it
        # rebuilds everything.
        $canIncrement = [bool] (Invoke-Sql -As Scalar -Sql "SELECT CASE WHEN EXISTS (SELECT 1 FROM sys.parameters WHERE object_id = OBJECT_ID(N'audit.usp_BuildCopilotSessions') AND name = N'@SinceUtc') THEN 1 ELSE 0 END")
        if ($canIncrement) {
            Write-Log ('Rebuilding sessions and the weekly rollup from {0:yyyy-MM-dd}...' -f $SinceUtc)
            Invoke-Sql -Sql 'EXEC audit.usp_BuildCopilotSessions @LicenseEventDatabase = @l, @SinceUtc = @s;' `
                -Parameters @{ '@l' = $licDb; '@s' = $SinceUtc } -TimeoutSeconds 0
        }
        else {
            Write-Log 'Rebuilding every session and the weekly rollup (the installed procedure predates incremental builds)...'
            Invoke-Sql -Sql 'EXEC audit.usp_BuildCopilotSessions @LicenseEventDatabase = @l;' -Parameters @{ '@l' = $licDb } -TimeoutSeconds 0
        }
    }
    # Last: it reads the flattened rows, the sessions and the license events.
    if (Test-Proc 'bi.usp_RefreshBi') {
        Write-Log ('Refreshing the bi star schema from {0:yyyy-MM-dd HH:mm}Z...' -f $SinceUtc)
        Invoke-Sql -Sql 'EXEC bi.usp_RefreshBi @SinceUtc = @s, @LicenseEventDatabase = @l;' `
            -Parameters @{ '@s' = $SinceUtc; '@l' = $(if ($SessionLicenseDatabase) { $SessionLicenseDatabase } else { $null }) } -TimeoutSeconds 0
    }
    # After bi: the Copilot Insights dashboard's org chart and daily rollups.
    if (Test-Proc 'dash.usp_RefreshDashboard') {
        Write-Log ('Refreshing the dashboard rollups from {0:yyyy-MM-dd}...' -f $SinceUtc)
        Invoke-Sql -Sql 'EXEC dash.usp_RefreshDashboard @SinceUtc = @s;' -Parameters @{ '@s' = $SinceUtc } -TimeoutSeconds 0
    }
}

function Get-OwedRefresh {
    <#
        Where the derived tables still owe a refresh: the start of the
        first merged load whose refresh did not finish (it failed after the
        merge, or ran with -SkipPostProcessing), or the earliest record of a CSV
        import whose own refresh did not. With the last load and import that
        covers, to mark them done after. $null when nothing is owed.
    #>
    $l = (Invoke-Sql -As Table -Sql 'SELECT MIN(RangeStartUtc) AS SinceUtc, MAX(LoadId) AS LastId FROM audit.CopilotLoad WHERE MergedUtc IS NOT NULL AND DerivedRefreshedUtc IS NULL').Rows[0]
    $since = if ($l.SinceUtc -is [DBNull]) { $null } else { [datetime]::SpecifyKind($l.SinceUtc, [DateTimeKind]::Utc) }
    $loadId = if ($l.LastId -is [DBNull]) { 0 } else { [int] $l.LastId }
    $importId = 0
    if ([int] (Invoke-Sql -As Scalar -Sql "SELECT CASE WHEN COL_LENGTH(N'audit.CopilotCsvImport', N'DerivedRefreshedUtc') IS NULL THEN 0 ELSE 1 END") -eq 1) {
        $c = (Invoke-Sql -As Table -Sql 'SELECT MIN(EarliestAddedUtc) AS SinceUtc, MAX(ImportId) AS LastId FROM audit.CopilotCsvImport WHERE MergedUtc IS NOT NULL AND RowsAdded > 0 AND DerivedRefreshedUtc IS NULL').Rows[0]
        if ($c.SinceUtc -isnot [DBNull]) {
            $csvSince = [datetime]::SpecifyKind($c.SinceUtc, [DateTimeKind]::Utc)
            if ($null -eq $since -or $csvSince -lt $since) { $since = $csvSince }
            $importId = [int] $c.LastId
        }
    }
    if ($null -eq $since) { return $null }
    [pscustomobject]@{ SinceUtc = $since; LoadId = $loadId; ImportId = $importId }
}

function Complete-OwedRefresh {
    <# Marks the merged loads up to LoadId, and the imports up to ImportId, as refreshed. #>
    param([int] $LoadId, [int] $ImportId)
    Invoke-Sql -Sql 'UPDATE audit.CopilotLoad SET DerivedRefreshedUtc = SYSUTCDATETIME() WHERE MergedUtc IS NOT NULL AND DerivedRefreshedUtc IS NULL AND LoadId <= @l' `
        -Parameters @{ '@l' = $LoadId }
    if ($ImportId -gt 0) {
        Invoke-Sql -Sql 'UPDATE audit.CopilotCsvImport SET DerivedRefreshedUtc = SYSUTCDATETIME() WHERE MergedUtc IS NOT NULL AND DerivedRefreshedUtc IS NULL AND ImportId <= @i' `
            -Parameters @{ '@i' = $ImportId }
    }
}

function Invoke-IncrementalLoad {
    if ($Finalize -or $FinalizeOnly) { Write-Log '-Finalize is not used with -Incremental: each load is merged as it completes.' -Level WARN }
    if ($StartDate) { Write-Log '-StartDate is ignored with -Incremental: each load starts where the data ends, less the lookback.' -Level WARN }

    Invoke-SqlScript -Path (Join-Path $PSScriptRoot 'sql' 'Create-CopilotLoadProcedure.sql')
    # A refresh an earlier run owes is made up from where it was owed. Each run refreshed from its own load's
    # start only, so after a refresh failed past the lookback (three nights, or one long catch-up load), or a re-run
    # found nothing new to load, those days stayed out of sessions, bi and the dashboard for good.
    $owed = Get-OwedRefresh
    $load = Get-IncrementalLoad
    if (-not $load) {
        if ($owed -and -not $SkipPostProcessing) {
            Write-Log ('The derived tables were not brought up to date after an earlier load or import: refreshing them from {0:yyyy-MM-dd HH:mm}Z.' -f $owed.SinceUtc) -Level WARN
            Invoke-DerivedTableRefresh -SinceUtc $owed.SinceUtc
            Complete-OwedRefresh -LoadId $owed.LoadId -ImportId $owed.ImportId
        }
        return
    }

    if (-not $load.Downloaded) {
        if (-not $TenantId) { Write-Log 'No -TenantId given; sign-in will use the account''s home tenant. Pass -TenantId to pin it.' -Level WARN }
        Connect-AuditGraph
        $script:CurrentGraphToken = Get-GraphAccessToken
        $final = Invoke-AuditDownload -Start $load.Start -End $load.End -WindowTable '[audit].[CopilotLoadWindow]' -StageTable '[audit].[CopilotLoadStage]'
        if ($final.WindowsFailed -gt 0) {
            # A scheduled run must fail visibly, not exit clean with a hole in the data.
            throw "$($final.WindowsFailed) window(s) of load $($load.LoadId) failed after retries. The next run resumes this load and retries them."
        }
        Invoke-Sql -Sql 'UPDATE audit.CopilotLoad SET DownloadedUtc = SYSUTCDATETIME() WHERE LoadId = @id' -Parameters @{ '@id' = $load.LoadId }
    }

    [void] (Invoke-Sql -As Table -Sql 'EXEC audit.usp_MergeCopilotLoad;' -TimeoutSeconds 0)
    if ($SkipPostProcessing) { Write-Log 'Derived tables not updated (-SkipPostProcessing). The next run without it brings them up to date.' }
    else {
        $since = $load.Start
        if ($owed -and $owed.SinceUtc -lt $since) {
            Write-Log ('The derived tables were not brought up to date after an earlier load or import: refreshing them from {0:yyyy-MM-dd HH:mm}Z, not from this load''s start.' -f $owed.SinceUtc) -Level WARN
            $since = $owed.SinceUtc
        }
        Invoke-DerivedTableRefresh -SinceUtc $since
        Complete-OwedRefresh -LoadId $load.LoadId -ImportId $(if ($owed) { $owed.ImportId } else { 0 })
    }
    Write-Log ('Incremental load {0} complete: the data now reaches {1:yyyy-MM-dd HH:mm}Z.' -f $load.LoadId, $load.End)
}

# ===========================================================================
# Graph sign-in and token hand-off
# ===========================================================================

$script:GraphScope = 'AuditLogsQuery.Read.All'
$script:CurrentGraphToken = $null

function Connect-AuditGraph {
    <#
        Delegated sign-in through the Microsoft Graph PowerShell SDK. The
        account needs BOTH the AuditLogsQuery.Read.All delegated permission
        (admin-consented) AND a Purview audit role (Audit Reader/Manager).
    #>
    foreach ($cmd in 'Connect-MgGraph', 'Invoke-MgGraphRequest', 'Get-MgContext') {
        if (-not (Get-Command -Name $cmd -ErrorAction SilentlyContinue)) {
            $hint = if (Test-Path -LiteralPath $script:BundledModules -PathType Container) {
                "A Modules folder is present at $script:BundledModules but does not provide $cmd - the folder may be incomplete: save the module into it again."
            }
            else {
                'Install it first:  Install-Module Microsoft.Graph.Authentication -Scope CurrentUser. On a server with no gallery access, save it into a Modules folder next to this script: tools\Save-CopilotAuditModules.ps1 on a machine that can reach the gallery, or Save-Module from your own feed.'
            }
            throw "$cmd not found. $hint"
        }
    }
    if ($CertificateThumbprint) {
        # App-only: no prompt, so a scheduled run can sign in by itself. The
        # permission is the application one, granted to the app, not a scope.
        if (-not $ClientId -or -not $TenantId) { throw '-CertificateThumbprint needs -ClientId and -TenantId.' }
        Connect-MgGraph -ClientId $ClientId -TenantId $TenantId -CertificateThumbprint $CertificateThumbprint -NoWelcome
        $ctx = Get-MgContext
        if (-not $ctx) { throw 'App-only sign-in did not complete.' }
        if (@($ctx.Scopes).Count -gt 0 -and $ctx.Scopes -notcontains $script:GraphScope) {
            throw ("App $ClientId signed in, but its token lacks the application permission $($script:GraphScope). " +
                   'Add it under API permissions (Application, not Delegated) and grant admin consent.')
        }
        Write-Log "Signed in app-only as app $ClientId (tenant $($ctx.TenantId))."
        return
    }

    $connect = @{ Scopes = $script:GraphScope; NoWelcome = $true }
    if ($TenantId) { $connect['TenantId'] = $TenantId }
    if ($ClientId) { $connect['ClientId'] = $ClientId }

    if ($AuthMode -eq 'DeviceCode') {
        $connect['UseDeviceAuthentication'] = $true
        Write-Log 'Signing in with a device code - follow the prompt below.'
    }
    else {
        # On Windows the SDK defaults to the WAM account picker; switch to a real
        # browser window. (Persists for this Windows profile.)
        try { Set-MgGraphOption -DisableLoginByWAM $true -ErrorAction Stop } catch { Write-Verbose "Older Graph SDK without the WAM option: $($_.Exception.Message)" }
        Write-Log 'Signing in - a browser window will open (username, password, MFA).'
    }

    Connect-MgGraph @connect
    $ctx = Get-MgContext
    if (-not $ctx) { throw 'Sign-in did not complete.' }
    if ($ctx.Scopes -notcontains $script:GraphScope) {
        throw ("Signed in as $($ctx.Account), but the token lacks $($script:GraphScope). An administrator must grant " +
               "consent for it on app $($ctx.ClientId).")
    }
    Write-Log "Signed in as $($ctx.Account) (tenant $($ctx.TenantId))."
}

function Get-GraphAccessToken {
    <#
        The engine makes its own HTTP calls, so it needs the bearer token the
        Graph SDK holds. Any SDK request carries it on the request message; a
        cheap call exposes it. The SDK refreshes it silently via its token cache.
    #>
    $resp = Invoke-MgGraphRequest -Method GET -Uri '/v1.0/security/auditLog/queries?$top=1' `
                -OutputType HttpResponseMessage -SkipHttpErrorCheck
    try {
        $auth = $resp.RequestMessage.Headers.Authorization
        if ($auth -and $auth.Scheme -eq 'Bearer' -and $auth.Parameter) { return $auth.Parameter }
    }
    finally { $resp.Dispose() }
    throw 'Could not read the access token from the Graph session. Update Microsoft.Graph.Authentication and retry.'
}

# ===========================================================================
# Monitor loop
# ===========================================================================

function Format-Duration {
    param([timespan] $Span)
    if ($Span.TotalHours -ge 1) { return '{0}h{1:D2}m' -f [int][math]::Floor($Span.TotalHours), $Span.Minutes }
    return '{0}m{1:D2}s' -f $Span.Minutes, $Span.Seconds
}

function Write-BackfillProgress {
    param([Parameter(Mandatory)] $Status, [Parameter(Mandatory)] [hashtable] $Tracker)
    $now = [datetime]::UtcNow
    $cov = if ($Status.HoursTotal -gt 0) { $Status.HoursLoaded / $Status.HoursTotal } else { 0 }
    $dt = ($now - $Tracker.Time).TotalSeconds
    $rate = if ($dt -gt 0) { ($Status.RowsWritten - $Tracker.Rows) / $dt } else { 0 }

    $eta = '-'
    $gained = $cov - $Tracker.StartCoverage
    if ($gained -gt 0.01 -and $cov -lt 1) {
        $eta = Format-Duration ([timespan]::FromSeconds(($now - $Tracker.StartTime).TotalSeconds * (1 - $cov) / $gained))
    }
    $note = ''
    if ($Status.PausedUntilUtc -gt $now) { $note += (' | throttled, resuming in {0:N0}s' -f ($Status.PausedUntilUtc - $now).TotalSeconds) }
    if ($Status.SubmitBlockedUntilUtc -gt $now) { $note += (' | submissions paused until {0:HH:mm}Z' -f $Status.SubmitBlockedUntilUtc) }

    Write-Log ('{0,4:P0} covered | windows {1} loaded, {2} searching, {3} downloading | {4} streams | {5,7:N0} rows/s | {6:N0} rows in SQL | 429s {7} | ETA {8}{9}' -f `
        $cov, $Status.WindowsLoaded, $Status.QueriesInFlight, ($Status.ReadyToDownload + $Status.Downloading),
        $Status.DownloadStreams, $rate, $Status.RowsWritten, ($Status.PageThrottles + $Status.SubmitThrottles), $eta, $note)

    $Tracker.Time = $now
    $Tracker.Rows = $Status.RowsWritten
}

function Wait-BackfillEngine {
    <#
        Runs the engine to completion: drains its log, keeps its access token
        fresh, prints progress. Ctrl+C stops it cleanly - state is in SQL.
    #>
    param(
        [Parameter(Mandatory)] $Engine,
        [Parameter(Mandatory)] [scriptblock] $GetToken,
        [scriptblock] $Reauthenticate,
        [int] $ProgressSeconds = 30,
        [int] $PollMilliseconds = 500
    )

    $task = $Engine.Start()
    $s0 = $Engine.GetStatus()
    $tracker = @{
        Time = [datetime]::UtcNow; Rows = 0; StartTime = [datetime]::UtcNow
        StartCoverage = $(if ($s0.HoursTotal -gt 0) { $s0.HoursLoaded / $s0.HoursTotal } else { 0 })
    }
    $lastProgress = [datetime]::UtcNow
    $lastTokenAttempt = [datetime]::MinValue
    $staleWhileRequested = 0
    $current = $script:CurrentGraphToken

    try {
        while (-not $task.IsCompleted) {
            Start-Sleep -Milliseconds $PollMilliseconds
            foreach ($line in $Engine.DrainLog()) { Write-LogLine $line }

            $now = [datetime]::UtcNow
            $requested = $Engine.TokenRefreshRequested
            $expiring = $now -gt $Engine.TokenExpiresUtc.AddMinutes(-4)
            $minGap = if ($requested) { 5 } else { 30 }
            if (($requested -or $expiring) -and ($now - $lastTokenAttempt).TotalSeconds -ge $minGap) {
                $lastTokenAttempt = $now
                try {
                    $token = & $GetToken
                    if ($requested -and $token -eq $current) {
                        # Graph rejected a token the cache still considers valid:
                        # revoked session or a sign-in-frequency policy.
                        if (++$staleWhileRequested -ge 2 -and $Reauthenticate) {
                            Write-Log 'Graph keeps rejecting the cached token - signing in again.' -Level WARN
                            & $Reauthenticate
                            $token = & $GetToken
                            $staleWhileRequested = 0
                        }
                    }
                    if ($token -ne $current -or $requested) {
                        $Engine.SetAccessToken($token)
                        $current = $token
                    }
                }
                catch {
                    Write-Log ('Token refresh failed: ' + $_.Exception.Message) -Level WARN
                }
            }

            if (($now - $lastProgress).TotalSeconds -ge $ProgressSeconds) {
                Write-BackfillProgress -Status $Engine.GetStatus() -Tracker $tracker
                $lastProgress = $now
            }
        }
    }
    finally {
        if (-not $task.IsCompleted) {
            Write-Log 'Stopping - letting in-flight SQL batches finish. Progress is saved; run the same command to resume.' -Level WARN
            $Engine.RequestStop()
            try { [void] $task.Wait([timespan]::FromSeconds(90)) } catch { Write-Verbose "The engine stopped with: $($_.Exception.Message)" }
        }
        foreach ($line in $Engine.DrainLog()) { Write-LogLine $line }
        $script:CurrentGraphToken = $current
    }

    if ($task.IsFaulted) {
        $ex = $task.Exception.GetBaseException()
        if ($ex.InnerException) { $ex = $ex.InnerException }
        throw $ex
    }
    return $Engine.GetStatus()
}

# ===========================================================================
# Main
# ===========================================================================

function Invoke-AuditDownload {
    <#
        Runs the engine over one range into one pair of tables: the backfill's,
        or an incremental load's. Returns the engine's final status.
    #>
    param(
        [Parameter(Mandatory)][datetime] $Start,
        [Parameter(Mandatory)][datetime] $End,
        [Parameter(Mandatory)][string] $WindowTable,
        [Parameter(Mandatory)][string] $StageTable
    )
    $o = [CopilotAudit.EngineOptions]::new()
    $o.RangeStartUtc          = $Start
    $o.RangeEndUtc            = $End
    $o.Operations             = [string[]] @($Operations | Where-Object { $_ })
    if ($RecordTypes) { $o.RecordTypes = [string[]] @($RecordTypes | Where-Object { $_ }) }
    $o.InitialWindowHours     = $InitialWindowHours
    $o.TargetRecordsPerWindow = $TargetRecordsPerWindow
    $o.MaxQueriesInFlight     = $MaxQueriesInFlight
    $o.MaxSubmissionsPer24h   = $MaxSubmissionsPer24h
    $o.QueryTimeoutMinutes    = $QueryTimeoutMinutes
    $o.InitialDownloadStreams = [math]::Min($InitialDownloadStreams, $MaxDownloadStreams)
    $o.MaxDownloadStreams     = $MaxDownloadStreams
    $o.SqlWriters             = $SqlWriters
    $o.BatchRows              = $BatchRows
    $o.PageSize               = $PageSize

    $store = [CopilotAudit.SqlWindowStore]::new($script:SqlConnStr, $WindowTable)
    $sink  = [CopilotAudit.SqlBatchSink]::new($script:SqlConnStr, $StageTable)
    $engine = [CopilotAudit.BackfillEngine]::new($o, $store, $sink, $null)
    $engine.SetAccessToken($script:CurrentGraphToken)

    Write-Log ('Starting: up to {0} searches in flight, {1}->{2} download streams, {3} SQL writers, target ~{4:N0} records per search.' -f `
               $MaxQueriesInFlight, $o.InitialDownloadStreams, $MaxDownloadStreams, $SqlWriters, $TargetRecordsPerWindow)

    $started = [datetime]::UtcNow
    try {
        $final = Wait-BackfillEngine -Engine $engine -GetToken { Get-GraphAccessToken } -Reauthenticate { Connect-AuditGraph }
    }
    finally {
        $engine.Dispose()
    }

    $elapsed = [datetime]::UtcNow - $started
    Write-Log ('Download finished in {0}: {1:N0} rows written ({2:N0}/s), {3:N0} searches, {4} throttled requests.' -f `
               (Format-Duration $elapsed), $final.RowsWritten, ($final.RowsWritten / [math]::Max(1, $elapsed.TotalSeconds)),
               $final.Submissions, ($final.PageThrottles + $final.SubmitThrottles))
    if ($final.AuditBytesCompressed -gt 0) {
        Write-Log ('AuditData compressed {0:N1}x before reaching SQL ({1:N0} MB raw -> {2:N0} MB).' -f `
                   ($final.AuditBytesRaw / $final.AuditBytesCompressed), ($final.AuditBytesRaw / 1MB), ($final.AuditBytesCompressed / 1MB))
    }
    if ($final.TruncatedValues -gt 0) { Write-Log "$($final.TruncatedValues) oversized field value(s) were truncated to fit the column sizes." -Level WARN }
    if ($final.SkippedRecords -gt 0)  { Write-Log "$($final.SkippedRecords) record(s) had no id or timestamp and were skipped." -Level WARN }
    return $final
}

function Invoke-Main {
    if (-not $LogPath) { $LogPath = Join-Path (Get-Location) ('CopilotAuditBackfill_{0:yyyyMMdd_HHmmss}.log' -f [datetime]::UtcNow) }
    $script:LogFile = $LogPath
    Write-Log "Log file: $LogPath"

    Import-CopilotAuditEngine 3>$null
    $script:SqlConnStr = New-SqlConnectionString
    Initialize-CopilotAuditDatabase

    if ($Status) { Show-BackfillState; return }
    # One run at a time: held to the end of the run, and let go in the finally below.
    $runLock = Enter-RunLock
    try { Invoke-Run }
    finally { if ($runLock) { $runLock.Dispose() } }
}

function Invoke-Run {
    if ($Incremental) { Invoke-IncrementalLoad; return }
    if ($FinalizeOnly) { Invoke-CopilotAuditFinalize; return }

    $range = Resolve-BackfillRange
    if ($range.Completed) {
        Write-Log 'This backfill already completed. Nothing to download.'
        if ($Finalize) {
            # Finalized before: leave the tables alone. A re-run (-History carrying on after a later step
            # failed) finalized again, rebuilding audit.CopilotAudit from staging, which by then may have been dropped.
            # -FinalizeOnly still builds them again, and finalize refuses when staging no longer holds the load.
            $built = [long] (Invoke-Sql -As Scalar -Sql "SELECT ISNULL(SUM(rows), 0) FROM sys.partitions WHERE object_id = OBJECT_ID(N'audit.CopilotAudit', N'U') AND index_id IN (0, 1)")
            $marked = [int] (Invoke-Sql -As Scalar -Sql "SELECT COUNT(*) FROM audit.PipelineSetting WHERE Name = N'FinalizedUtc'") -gt 0
            # A finalize from before 2.4.12 left no mark: the view it creates last is the sign of one.
            $viewed = [int] (Invoke-Sql -As Scalar -Sql "SELECT CASE WHEN OBJECT_ID(N'audit.vCopilotAuditRecord', N'V') IS NULL THEN 0 ELSE 1 END") -eq 1
            if ($built -gt 0 -and ($marked -or $viewed)) {
                Write-Log ('Already finalized: audit.CopilotAudit holds {0:N0} records. Nothing to do; -FinalizeOnly builds it again from staging.' -f $built)
            }
            else { Invoke-CopilotAuditFinalize }
        }
        return
    }
    if (($range.End - $range.Start).TotalDays -gt 180) {
        Write-Log (('The range covers {0:N0} days. Copilot audit records are retained 180 days unless a custom audit ' +
                    'retention policy covered them at the time - older windows will simply come back empty.') -f ($range.End - $range.Start).TotalDays) -Level WARN
    }
    if (-not $TenantId) {
        Write-Log 'No -TenantId given; sign-in will use the account''s home tenant. Pass -TenantId to pin it.' -Level WARN
    }

    Connect-AuditGraph
    $script:CurrentGraphToken = Get-GraphAccessToken

    $final = Invoke-AuditDownload -Start $range.Start -End $range.End -WindowTable '[audit].[CopilotBackfillWindow]' -StageTable '[audit].[CopilotAuditStage]'
    if ($final.OldestRecordUtc -and ($final.OldestRecordUtc - $range.Start).TotalDays -gt 2) {
        Write-Log ('Oldest record found is {0:yyyy-MM-dd}; nothing exists before that - the retention boundary.' -f $final.OldestRecordUtc) -Level WARN
    }

    Show-BackfillState
    if ($final.WindowsFailed -gt 0) {
        # Thrown, as an incremental load does. It was logged and the script returned, so it exited 0: -History
        # carried on with the range not complete and not finalized, and reported the history loaded and built.
        throw "$($final.WindowsFailed) window(s) failed after retries. Run the same command again to retry them."
    }

    Invoke-Sql -Sql 'UPDATE audit.CopilotBackfillRange SET CompletedUtc = SYSUTCDATETIME() WHERE Id = 1'
    Write-Log 'Backfill complete.'
    if ($Finalize) { Invoke-CopilotAuditFinalize }
    else { Write-Log 'Next: re-run with -FinalizeOnly to build the deduplicated and reporting tables.' }
}

# Dot-sourcing (tests) loads the functions without running anything.
if ($MyInvocation.InvocationName -ne '.') { Invoke-Main }
