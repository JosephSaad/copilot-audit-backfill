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
    captured. Leave the ongoing feed (Management Activity API) to cover today on.

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

.EXAMPLE
    .\Invoke-CopilotAuditBackfill.ps1 -SqlServer SQLPROD01 -Database CopilotAudit -TenantId contoso.onmicrosoft.com

.EXAMPLE
    # Resume after Ctrl+C / reboot: the same command again.
    .\Invoke-CopilotAuditBackfill.ps1 -SqlServer SQLPROD01 -Database CopilotAudit -TenantId contoso.onmicrosoft.com -Finalize

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

    [string[]] $Operations = @('CopilotInteraction'),

    [switch]   $Finalize,
    [switch]   $FinalizeOnly,
    [switch]   $Status,

    # --- tuning (defaults are deliberately inside Microsoft's published limits)
    [ValidateRange(1, 32)]        [int]    $MaxDownloadStreams     = 8,
    [ValidateRange(1, 32)]        [int]    $InitialDownloadStreams = 4,
    [ValidateRange(1, 40)]        [int]    $MaxQueriesInFlight     = 8,
    [ValidateRange(1, 200)]       [int]    $MaxSubmissionsPer24h   = 150,
    [ValidateRange(10000, 5000000)][long]  $TargetRecordsPerWindow = 600000,
    [ValidateRange(0.25, 168)]    [double] $InitialWindowHours     = 24,
    [ValidateRange(1, 16)]        [int]    $SqlWriters             = 4,
    [ValidateRange(100, 50000)]   [int]    $BatchRows              = 2000,
    [ValidateRange(10, 1000)]     [int]    $PageSize               = 1000,

    [string]   $LogPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# A 'Modules' folder next to this script takes precedence over the machine's
# module paths. That is how the offline release bundle ships
# Microsoft.Graph.Authentication for servers with no PSGallery access; when the
# folder is absent this does nothing and module resolution is unchanged.
$script:BundledModules = Join-Path $PSScriptRoot 'Modules'
if (Test-Path -LiteralPath $script:BundledModules -PathType Container) {
    $sep = [System.IO.Path]::PathSeparator
    $existing = ($env:PSModulePath -split [regex]::Escape($sep)) | Where-Object { $_ -ne $script:BundledModules }
    $env:PSModulePath = (@($script:BundledModules) + $existing) -join $sep
}

. (Join-Path $PSScriptRoot 'Import-CopilotAuditEngine.ps1')

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
    $csb = [System.Data.SqlClient.SqlConnectionStringBuilder]::new()
    if ($ConnectionString) {
        $csb.ConnectionString = $ConnectionString
    }
    else {
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
    $conn.add_InfoMessage({ param($sender, $e) foreach ($m in $e.Errors) { Write-Log ("SQL: " + $m.Message) } })
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

function ConvertTo-UtcDate {
    param([Parameter(Mandatory)][datetime] $Value)
    switch ($Value.Kind) {
        'Utc'   { return $Value }
        'Local' { return $Value.ToUniversalTime() }
        default { return [datetime]::SpecifyKind($Value, [DateTimeKind]::Utc) }   # -StartDate 2025-09-22 means UTC
    }
}

function Resolve-BackfillRange {
    <#
        First run: records the requested range. Later runs: the stored range
        wins (so a resume never needs the original dates re-typed), except that
        an earlier -StartDate extends it.
    #>
    $ops = ($Operations | Sort-Object) -join ','
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
}

function Invoke-CopilotAuditFinalize {
    Write-Log 'Finalizing: verifying coverage, building audit.CopilotAudit and audit.CopilotInteraction...'
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
                "A Modules folder is present at $script:BundledModules but does not provide $cmd - the bundle may be incomplete."
            }
            else {
                'Install it first:  Install-Module Microsoft.Graph.Authentication -Scope CurrentUser. On a server with no gallery access, use the offline release bundle, which ships the module in a Modules folder next to this script.'
            }
            throw "$cmd not found. $hint"
        }
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
        try { Set-MgGraphOption -DisableLoginByWAM $true -ErrorAction Stop } catch { }
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
            try { [void] $task.Wait([timespan]::FromSeconds(90)) } catch { }
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

function Invoke-Main {
    if (-not $LogPath) { $LogPath = Join-Path (Get-Location) ('CopilotAuditBackfill_{0:yyyyMMdd_HHmmss}.log' -f [datetime]::UtcNow) }
    $script:LogFile = $LogPath
    Write-Log "Log file: $LogPath"

    Import-CopilotAuditEngine 3>$null
    $script:SqlConnStr = New-SqlConnectionString
    Initialize-CopilotAuditDatabase

    if ($Status) { Show-BackfillState; return }
    if ($FinalizeOnly) { Invoke-CopilotAuditFinalize; return }

    $range = Resolve-BackfillRange
    if ($range.Completed) {
        Write-Log 'This backfill already completed. Nothing to download.'
        if ($Finalize) { Invoke-CopilotAuditFinalize }
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

    $o = [CopilotAudit.EngineOptions]::new()
    $o.RangeStartUtc          = $range.Start
    $o.RangeEndUtc            = $range.End
    $o.Operations             = [string[]] $Operations
    $o.InitialWindowHours     = $InitialWindowHours
    $o.TargetRecordsPerWindow = $TargetRecordsPerWindow
    $o.MaxQueriesInFlight     = $MaxQueriesInFlight
    $o.MaxSubmissionsPer24h   = $MaxSubmissionsPer24h
    $o.InitialDownloadStreams = [math]::Min($InitialDownloadStreams, $MaxDownloadStreams)
    $o.MaxDownloadStreams     = $MaxDownloadStreams
    $o.SqlWriters             = $SqlWriters
    $o.BatchRows              = $BatchRows
    $o.PageSize               = $PageSize

    $store = [CopilotAudit.SqlWindowStore]::new($script:SqlConnStr, '[audit].[CopilotBackfillWindow]')
    $sink  = [CopilotAudit.SqlBatchSink]::new($script:SqlConnStr, '[audit].[CopilotAuditStage]')
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
    if ($final.OldestRecordUtc -and ($final.OldestRecordUtc - $range.Start).TotalDays -gt 2) {
        Write-Log ('Oldest record found is {0:yyyy-MM-dd}; nothing exists before that - the retention boundary.' -f $final.OldestRecordUtc) -Level WARN
    }

    Show-BackfillState
    if ($final.WindowsFailed -gt 0) {
        Write-Log "$($final.WindowsFailed) window(s) failed after retries. Run the same command again to retry them." -Level ERROR
        return
    }

    Invoke-Sql -Sql 'UPDATE audit.CopilotBackfillRange SET CompletedUtc = SYSUTCDATETIME() WHERE Id = 1'
    Write-Log 'Backfill complete.'
    if ($Finalize) { Invoke-CopilotAuditFinalize }
    else { Write-Log 'Next: re-run with -FinalizeOnly to build the deduplicated and reporting tables.' }
}

# Dot-sourcing (tests) loads the functions without running anything.
if ($MyInvocation.InvocationName -ne '.') { Invoke-Main }
