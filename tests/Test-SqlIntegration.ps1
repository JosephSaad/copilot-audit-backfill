<#
    Integration test: the real SqlWindowStore / SqlBatchSink and both SQL
    scripts against a real SQL Server.

    Test-Engine.ps1 exercises the pipeline against in-memory stand-ins, so the
    SqlBulkCopy path, the DDL and Finalize-CopilotAudit.sql are never actually
    executed there. This runs them for real:

      1  create a database at compatibility level 160, run
         sql/Create-CopilotAuditSchema.sql
      2  run the engine against mock_graph.py (8 days, default config) with
         [CopilotAudit.SqlWindowStore] and [CopilotAudit.SqlBatchSink], stop it
         part-way, then resume it to completion
      3  write the audit.CopilotBackfillRange row the orchestrator would write,
         mark it complete, run sql/Finalize-CopilotAudit.sql
      4  assert the finalized tables and views against the mock's known record set
      5  re-run finalize (idempotent), then prove it refuses a range that is not
         fully Loaded

    Connection is taken from the environment:

      SQLIT_SERVER     host,port   (default 127.0.0.1,1433)
      SQLIT_USER       (default sa)
      SQLIT_PASSWORD   required
      SQLIT_DATABASE   (default CopilotAuditIntegration)

    Requires python3 and a reachable SQL Server 2017+. CI starts one as a
    service container; locally, docker run mcr.microsoft.com/mssql/server:2022-latest.
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Split-Path $PSScriptRoot -Parent
. (Join-Path $root 'Import-CopilotAuditEngine.ps1')
Import-CopilotAuditEngine 3>$null
. (Join-Path $PSScriptRoot 'TestHelpers.ps1')

$server   = if ($env:SQLIT_SERVER)   { $env:SQLIT_SERVER }   else { '127.0.0.1,1433' }
$user     = if ($env:SQLIT_USER)     { $env:SQLIT_USER }     else { 'sa' }
$password = $env:SQLIT_PASSWORD
$database = if ($env:SQLIT_DATABASE) { $env:SQLIT_DATABASE } else { 'CopilotAuditIntegration' }

# Bound to this file's scope, and read that way inside the helpers below.
# PowerShell looks variables up dynamically, through the CALLER's scope chain,
# so a script dot-sourced later in this suite can shadow a plain $database
# with its own param of the same name - names are case-insensitive - and every
# query would then silently run against the wrong catalogue. The $script:
# qualifier resolves here regardless of who is calling.
$script:ITServer   = $server
$script:ITUser     = $user
$script:ITPassword = $password
$script:ITDatabase = $database

if (-not $password) {
    Write-Host 'SQLIT_PASSWORD is not set - no SQL Server to test against.' -ForegroundColor Yellow
    Write-Host 'Start one and re-run, e.g.:' -ForegroundColor Yellow
    Write-Host '  docker run -d --name sqlit -e ACCEPT_EULA=Y -e MSSQL_SA_PASSWORD=<pw> -p 1433:1433 mcr.microsoft.com/mssql/server:2022-latest' -ForegroundColor DarkGray
    Write-Host '  SQLIT_PASSWORD=<pw> pwsh -NoProfile -File tests/Test-SqlIntegration.ps1' -ForegroundColor DarkGray
    exit 2
}

function New-ConnString([string] $InitialCatalog) {
    # Mirrors New-SqlConnectionString in Invoke-CopilotAuditBackfill.ps1
    # (-TrustServerCertificate), which is what a self-signed instance needs.
    $csb = [System.Data.SqlClient.SqlConnectionStringBuilder]::new()
    $csb['Data Source'] = $script:ITServer
    $csb['Initial Catalog'] = $InitialCatalog
    $csb['User ID'] = $script:ITUser
    $csb['Password'] = $script:ITPassword
    $csb['Encrypt'] = $true
    $csb['TrustServerCertificate'] = $true
    $csb['Application Name'] = 'CopilotAuditBackfill'
    $csb['Max Pool Size'] = 40
    $csb['Connect Timeout'] = 30
    return $csb.ConnectionString
}

function Invoke-SqlText {
    param(
        [Parameter(Mandatory)][string] $Sql,
        [string] $Catalog = $script:ITDatabase,
        [ValidateSet('NonQuery', 'Scalar', 'Table')][string] $As = 'NonQuery',
        [hashtable] $Parameters = @{},
        [int] $TimeoutSeconds = 600,
        [switch] $ShowMessages
    )
    $conn = [System.Data.SqlClient.SqlConnection]::new((New-ConnString $Catalog))
    if ($ShowMessages) {
        $conn.add_InfoMessage({ param($s, $e) foreach ($m in $e.Errors) {
            if ($m.Class -le 10) { Write-Host ('    SQL: ' + $m.Message) -ForegroundColor DarkGray } } })
        $conn.FireInfoMessageEventOnUserErrors = $false
    }
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
            'Scalar'   { return $cmd.ExecuteScalar() }
            'Table'    {
                $dt = [System.Data.DataTable]::new()
                $reader = $cmd.ExecuteReader()
                try { $dt.Load($reader) } finally { $reader.Dispose() }
                return ,$dt        # the comma stops PowerShell unrolling it into DataRows
            }
            default    { [void] $cmd.ExecuteNonQuery(); return $null }
        }
    }
    finally { $conn.Dispose() }
}

function Invoke-SqlScript {
    <#
        Runs a script that uses GO batch separators (the stored procedures do -
        CREATE/ALTER PROCEDURE has to start a batch). Splits on GO exactly the
        way sqlcmd does: the word alone on a line.
    #>
    param([Parameter(Mandatory)][string] $Sql, [switch] $ShowMessages, [int] $TimeoutSeconds = 600)
    $batches = [regex]::Split($Sql, '(?im)^[ \t]*GO[ \t]*(?:--.*)?$')
    foreach ($b in $batches) {
        if ([string]::IsNullOrWhiteSpace($b)) { continue }
        Invoke-SqlText -Sql $b -ShowMessages:$ShowMessages -TimeoutSeconds $TimeoutSeconds
    }
}

function Wait-ForSqlServer([int] $TimeoutSeconds = 180) {
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $lastErr = $null
    while ($sw.Elapsed.TotalSeconds -lt $TimeoutSeconds) {
        try {
            [void] (Invoke-SqlText -Catalog 'master' -As Scalar -Sql 'SELECT 1' -TimeoutSeconds 10)
            Write-Host ("  SQL Server reachable after {0:N0}s." -f $sw.Elapsed.TotalSeconds) -ForegroundColor DarkGray
            return
        }
        catch { $lastErr = $_.Exception.Message; Start-Sleep -Seconds 3 }
    }
    throw "SQL Server at $server did not accept connections within ${TimeoutSeconds}s. Last error: $lastErr"
}

# ---------------------------------------------------------------------------
Write-Host "`n1. Database and schema" -ForegroundColor Cyan

Wait-ForSqlServer

# SERVERPROPERTY returns sql_variant, which CONCAT will not convert implicitly.
$version = Invoke-SqlText -Catalog 'master' -As Scalar -Sql @"
SELECT CONCAT(CAST(SERVERPROPERTY('ProductVersion') AS NVARCHAR(128)), N' ',
              CAST(SERVERPROPERTY('Edition') AS NVARCHAR(128)));
"@
Write-Host "  $version" -ForegroundColor DarkGray

# Rebuild from scratch so a re-run is always clean.
Invoke-SqlText -Catalog 'master' -Sql @"
IF DB_ID(N'$database') IS NOT NULL
BEGIN
    ALTER DATABASE [$database] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
    DROP DATABASE [$database];
END;
CREATE DATABASE [$database];
ALTER DATABASE [$database] SET COMPATIBILITY_LEVEL = 160;
ALTER DATABASE [$database] SET RECOVERY SIMPLE;
"@

$compat = Invoke-SqlText -Catalog 'master' -As Scalar -Sql "SELECT compatibility_level FROM sys.databases WHERE name = N'$database'"
Assert-That ([int] $compat -eq 160) "database created at compatibility level 160 (got $compat)"

$schemaSql = Get-Content -LiteralPath (Join-Path $root 'sql' 'Create-CopilotAuditSchema.sql') -Raw
Invoke-SqlText -Sql $schemaSql
# Idempotent: the orchestrator runs it on every start.
Invoke-SqlText -Sql $schemaSql

$objects = Invoke-SqlText -As Table -Sql @"
SELECT name FROM sys.tables WHERE SCHEMA_NAME(schema_id) = 'audit' ORDER BY name;
"@
$names = @($objects.Rows | ForEach-Object { $_.name })
Assert-That (($names -join ',') -eq 'CopilotAuditStage,CopilotBackfillRange,CopilotBackfillWindow') `
    "Create-CopilotAuditSchema.sql built the three tables, and is idempotent ($($names -join ', '))"

$heap = Invoke-SqlText -As Scalar -Sql @"
SELECT COUNT(*) FROM sys.indexes
WHERE object_id = OBJECT_ID(N'audit.CopilotAuditStage') AND type <> 0;
"@
Assert-That ([int] $heap -eq 0) 'audit.CopilotAuditStage is a heap (so parallel bulk writers take BU locks)'

# ---------------------------------------------------------------------------
Write-Host "`n2. Real engine -> SqlWindowStore + SqlBatchSink, stopped and resumed" -ForegroundColor Cyan

$connStr = New-ConnString $database
$mock = Start-MockGraph @{ days = 8 }
try {
    $expected = Get-ExpectedIds $mock
    $expectedTotal = $expected.Count
    Write-Host ("  mock serves {0:N0} records over 8 days" -f $expectedTotal) -ForegroundColor DarkGray

    $start = [datetime]::new(2026, 8, 1, 0, 0, 0, [DateTimeKind]::Utc)
    $end   = [datetime]::new(2026, 8, 9, 0, 0, 0, [DateTimeKind]::Utc)

    $store = [CopilotAudit.SqlWindowStore]::new($connStr, '[audit].[CopilotBackfillWindow]')
    $sink  = [CopilotAudit.SqlBatchSink]::new($connStr, '[audit].[CopilotAuditStage]')
    $log   = [System.Collections.Generic.List[string]]::new()

    # Stop once roughly a third is in, so the resume has real work and at least
    # one window is left mid-download (its rows become an abandoned attempt).
    $stopAt = [math]::Max(1, [int] ($expectedTotal / 3))
    $tick = { param($e) if ($e.GetStatus().RowsWritten -ge $stopAt) { $e.RequestStop() } }

    $run1 = Invoke-EngineRun -Mock $mock -Options (New-Options $mock $start $end) -Store $store -Sink $sink -OnTick $tick -Log $log -TimeoutSec 900
    $stagedAfter1 = [long] (Invoke-SqlText -As Scalar -Sql 'SELECT COUNT_BIG(*) FROM audit.CopilotAuditStage')
    $windows1 = Invoke-SqlText -As Table -Sql 'SELECT Status, COUNT(*) AS n FROM audit.CopilotBackfillWindow GROUP BY Status'
    $partial1 = @($windows1.Rows | Where-Object { $_.Status -in 'Downloading', 'Ready', 'Submitted', 'Pending' }).Count

    Write-Host ("  run 1 stopped at {0:N0} rows written, {1:N0} rows in staging" -f $run1.Status.RowsWritten, $stagedAfter1) -ForegroundColor DarkGray

    Assert-That ($run1.Task.IsCanceled) 'run 1 stopped mid-flight (Canceled)'
    Assert-That ($stagedAfter1 -gt 0) "SqlBulkCopy really wrote to SQL Server ($('{0:N0}' -f $stagedAfter1) rows staged)"
    Assert-That ($partial1 -gt 0) 'run 1 left unfinished windows in audit.CopilotBackfillWindow'

    # A fresh store/sink, exactly as a second process would build them: all the
    # resume state has to come back out of SQL.
    $store2 = [CopilotAudit.SqlWindowStore]::new($connStr, '[audit].[CopilotBackfillWindow]')
    $sink2  = [CopilotAudit.SqlBatchSink]::new($connStr, '[audit].[CopilotAuditStage]')
    $run2 = Invoke-EngineRun -Mock $mock -Options (New-Options $mock $start $end) -Store $store2 -Sink $sink2 -Log $log -TimeoutSec 900

    Assert-That ($run2.Task.Status -eq 'RanToCompletion') "run 2 resumed from SQL state and completed ($($run2.Task.Status))"

    $notLoaded = [int] (Invoke-SqlText -As Scalar -Sql "SELECT COUNT(*) FROM audit.CopilotBackfillWindow WHERE Status NOT IN ('Loaded','Split')")
    Assert-That ($notLoaded -eq 0) "every window ended Loaded or Split ($notLoaded left over)"

    $stagedTotal = [long] (Invoke-SqlText -As Scalar -Sql 'SELECT COUNT_BIG(*) FROM audit.CopilotAuditStage')
    $abandoned = [long] (Invoke-SqlText -As Scalar -Sql @"
SELECT COUNT_BIG(*) FROM audit.CopilotAuditStage AS s
JOIN audit.CopilotBackfillWindow AS w ON w.WindowId = s.WindowId
WHERE NOT (w.Status = 'Loaded' AND w.Attempts = s.Attempt);
"@)
    Write-Host ("  {0:N0} rows staged, of which {1:N0} are from abandoned attempts" -f $stagedTotal, $abandoned) -ForegroundColor DarkGray
    Assert-That ($abandoned -gt 0) "staging holds rows from the interrupted attempt ($('{0:N0}' -f $abandoned)) for finalize to discard"

    $errors = @($log | Where-Object { $_ -match '\[ERROR\]' })
    Assert-That ($errors.Count -eq 0) "no ERROR log lines ($($errors.Count))"

    # -----------------------------------------------------------------------
    Write-Host "`n3. Finalize" -ForegroundColor Cyan

    # The row Invoke-CopilotAuditBackfill.ps1 writes in Resolve-BackfillRange,
    # then the completion stamp it sets once every window is Loaded.
    Invoke-SqlText -Sql 'INSERT INTO audit.CopilotBackfillRange (Id, RangeStartUtc, RangeEndUtc, Operations) VALUES (1, @s, @e, @o)' `
                   -Parameters @{ '@s' = $start; '@e' = $end; '@o' = 'CopilotInteraction' }
    Invoke-SqlText -Sql 'UPDATE audit.CopilotBackfillRange SET CompletedUtc = SYSUTCDATETIME() WHERE Id = 1'

    $finalizeSql = Get-Content -LiteralPath (Join-Path $root 'sql' 'Finalize-CopilotAudit.sql') -Raw
    $swf = [System.Diagnostics.Stopwatch]::StartNew()
    Invoke-SqlText -Sql $finalizeSql -ShowMessages
    Write-Host ("  finalize took {0:N1}s" -f $swf.Elapsed.TotalSeconds) -ForegroundColor DarkGray

    # -----------------------------------------------------------------------
    Write-Host "`n4. The finalized tables" -ForegroundColor Cyan

    $counts = Invoke-SqlText -As Table -Sql @"
SELECT COUNT_BIG(*)                AS Rows_,
       COUNT(DISTINCT RecordId)    AS DistinctIds
FROM audit.CopilotAudit;
"@
    $rows = [long] $counts.Rows[0].Rows_
    $distinct = [long] $counts.Rows[0].DistinctIds

    Assert-That ($rows -eq $expectedTotal) ("audit.CopilotAudit holds every record once ({0:N0} rows, {1:N0} expected)" -f $rows, $expectedTotal)
    Assert-That ($distinct -eq $expectedTotal) ("{0:N0} distinct RecordId, {1:N0} expected" -f $distinct, $expectedTotal)
    Assert-That ($rows -eq $distinct) 'zero duplicate RecordId survived finalize'

    # The record ids are the mock's, so this catches wrong rows as well as missing ones.
    $idCheck = Invoke-SqlText -As Scalar -Sql @"
SELECT COUNT_BIG(*) FROM audit.CopilotAudit
WHERE RecordId NOT LIKE '202608[0-9][0-9]-[0-9][0-9][0-9][0-9][0-9][0-9]';
"@
    Assert-That ([long] $idCheck -eq 0) 'every RecordId has the shape the mock generates'

    $cci = Invoke-SqlText -As Scalar -Sql @"
SELECT COUNT(*) FROM sys.indexes
WHERE object_id = OBJECT_ID(N'audit.CopilotAudit') AND type_desc = 'CLUSTERED COLUMNSTORE';
"@
    Assert-That ([int] $cci -eq 1) 'audit.CopilotAudit is a clustered columnstore'

    # The view decompresses AuditDataGz. ISJSON = 1 for every row proves the
    # gzip the engine wrote is exactly what SQL's DECOMPRESS reads back.
    $json = Invoke-SqlText -As Table -TimeoutSeconds 900 -Sql @"
SELECT COUNT_BIG(*)                                           AS Rows_,
       SUM(CASE WHEN ISJSON(AuditData) = 1 THEN 1 ELSE 0 END) AS Valid,
       SUM(CASE WHEN AuditData IS NULL THEN 1 ELSE 0 END)     AS Nulls
FROM audit.vCopilotAuditRecord;
"@
    $vRows  = [long] $json.Rows[0].Rows_
    $vValid = [long] $json.Rows[0].Valid
    $vNulls = [long] $json.Rows[0].Nulls
    Assert-That ($vRows -eq $expectedTotal) ("audit.vCopilotAuditRecord returns every record ({0:N0})" -f $vRows)
    Assert-That ($vNulls -eq 0) "no row has a NULL AuditData ($vNulls)"
    Assert-That ($vValid -eq $expectedTotal) ("AuditData parses with ISJSON = 1 for every row ({0:N0} of {1:N0})" -f $vValid, $expectedTotal)

    # The export-shaped view must also re-case RecordType and expose the columns
    # a portal CSV import has.
    $shape = Invoke-SqlText -As Table -Sql @"
SELECT TOP (1) RecordId, CreationDate, RecordType, Operation, UserId, AssociatedAdminUnits,
       JSON_VALUE(AuditData, '$.Id') AS JsonId
FROM audit.vCopilotAuditRecord ORDER BY CreationDate;
"@
    $r0 = $shape.Rows[0]
    Assert-That ($r0.RecordType -eq 'CopilotInteraction') "RecordType is re-cased for the export shape ($($r0.RecordType))"
    Assert-That ($r0.JsonId -eq $r0.RecordId) 'AuditData JSON is the record it is attached to (Id matches RecordId)'

    # The view's column list has to match dbo.PurviewRawLog's, in order, so SQL
    # written against a portal CSV import can point at it unchanged.
    $viewCols = Invoke-SqlText -As Table -Sql @"
SELECT name FROM sys.columns WHERE object_id = OBJECT_ID(N'audit.vCopilotAuditRecord') ORDER BY column_id;
"@
    $viewNames = @($viewCols.Rows | ForEach-Object { $_.name }) -join '|'
    $expectShape = 'RecordId|CreationDate|RecordType|Operation|UserId|AuditData|AssociatedAdminUnits|AssociatedAdminUnitsNames'
    Assert-That ($viewNames -eq $expectShape) "audit.vCopilotAuditRecord matches the Purview export's column list ($viewNames)"

    # It is present for shape only: Graph carries administrative unit IDs, never names.
    $auNames = [long] (Invoke-SqlText -As Scalar -TimeoutSeconds 900 -Sql @"
SELECT COUNT_BIG(*) FROM audit.vCopilotAuditRecord WHERE AssociatedAdminUnitsNames IS NOT NULL;
"@)
    $auIds = [long] (Invoke-SqlText -As Scalar -TimeoutSeconds 900 -Sql @"
SELECT COUNT_BIG(*) FROM audit.vCopilotAuditRecord WHERE AssociatedAdminUnits IS NOT NULL;
"@)
    Assert-That ($auNames -eq 0) 'AssociatedAdminUnitsNames is always NULL - Graph does not supply admin unit names'
    Assert-That ($auIds -gt 0) ("AssociatedAdminUnits does carry the IDs Graph supplies ({0:N0} rows)" -f $auIds)

    $interaction = Invoke-SqlText -As Table -Sql @"
SELECT COUNT_BIG(*)                                              AS Rows_,
       SUM(CASE WHEN AppHost IS NOT NULL THEN 1 ELSE 0 END)      AS WithHost,
       COUNT(DISTINCT AppHost)                                   AS Hosts,
       SUM(CASE WHEN PromptCount = 1 THEN 1 ELSE 0 END)          AS OnePrompt,
       SUM(CASE WHEN UserPrincipalName IS NOT NULL THEN 1 ELSE 0 END) AS WithUpn
FROM audit.CopilotInteraction;
"@
    $iRows = [long] $interaction.Rows[0].Rows_
    $iHost = [long] $interaction.Rows[0].WithHost
    Assert-That ($iRows -eq $expectedTotal) ("audit.CopilotInteraction has the same row count ({0:N0})" -f $iRows)
    Assert-That ($iHost -eq $expectedTotal) ("AppHost is non-null on every row ({0:N0})" -f $iHost)
    Assert-That ([int] $interaction.Rows[0].Hosts -eq 4) "the four mock AppHosts all shredded out ($($interaction.Rows[0].Hosts))"
    Assert-That ([long] $interaction.Rows[0].OnePrompt -eq $expectedTotal) 'PromptCount counted the one isPrompt message on every record'
    Assert-That ([long] $interaction.Rows[0].WithUpn -eq $expectedTotal) 'UserPrincipalName is populated on every row'

    $daily = Invoke-SqlText -As Table -Sql @"
SELECT SUM(Interactions) AS Interactions, COUNT(*) AS Groups_
FROM audit.vCopilotDailyUsage;
"@
    Assert-That ([long] $daily.Rows[0].Interactions -eq $expectedTotal) ("audit.vCopilotDailyUsage adds back up to {0:N0}" -f $expectedTotal)
    Assert-That ([int] $daily.Rows[0].Groups_ -gt 0) "vCopilotDailyUsage groups by day and app ($($daily.Rows[0].Groups_) groups)"

    # -----------------------------------------------------------------------
    Write-Host "`n5. Finalize is re-runnable, and refuses an incomplete range" -ForegroundColor Cyan

    Invoke-SqlText -Sql $finalizeSql -TimeoutSeconds 900
    $again = Invoke-SqlText -As Table -Sql @"
SELECT (SELECT COUNT_BIG(*) FROM audit.CopilotAudit)       AS A,
       (SELECT COUNT_BIG(*) FROM audit.CopilotInteraction) AS I,
       (SELECT COUNT(DISTINCT RecordId) FROM audit.CopilotAudit) AS D;
"@
    Assert-That ([long] $again.Rows[0].A -eq $rows -and [long] $again.Rows[0].I -eq $iRows -and [long] $again.Rows[0].D -eq $distinct) `
        ("a second finalize succeeded and gave the same counts ({0:N0} / {1:N0})" -f $again.Rows[0].A, $again.Rows[0].I)

    # Put one window back to Pending: finalize must refuse rather than build
    # a table that silently misses a window.
    $victim = [long] (Invoke-SqlText -As Scalar -Sql "SELECT TOP (1) WindowId FROM audit.CopilotBackfillWindow WHERE Status = 'Loaded' ORDER BY WindowId")
    Invoke-SqlText -Sql "UPDATE audit.CopilotBackfillWindow SET Status = 'Pending' WHERE WindowId = @w" -Parameters @{ '@w' = $victim }
    $refused = $false
    $refusal = ''
    try { Invoke-SqlText -Sql $finalizeSql -TimeoutSeconds 900 }
    catch { $refused = $true; $refusal = $_.Exception.Message }
    Invoke-SqlText -Sql "UPDATE audit.CopilotBackfillWindow SET Status = 'Loaded' WHERE WindowId = @w" -Parameters @{ '@w' = $victim }

    Assert-That $refused 'finalize refused to run with a window left Pending'
    Assert-That ($refusal -match 'not Loaded') "the refusal says why: $($refusal -replace '\s+', ' ')"

    # It refused before dropping anything, so the finalized tables are still there.
    $survived = [long] (Invoke-SqlText -As Scalar -Sql "SELECT COUNT_BIG(*) FROM audit.CopilotAudit")
    Assert-That ($survived -eq $rows) ("the refusal left the finalized tables intact ({0:N0} rows)" -f $survived)

    # -----------------------------------------------------------------------
    Write-Host "`n6. Flattening the JSON (audit.usp_FlattenCopilotAudit)" -ForegroundColor Cyan

    Invoke-SqlScript -Sql (Get-Content -LiteralPath (Join-Path $root 'sql' 'Create-CopilotFlattenedSchema.sql') -Raw)
    Invoke-SqlScript -Sql (Get-Content -LiteralPath (Join-Path $root 'sql' 'Create-CopilotFlattenProcedure.sql') -Raw)
    Invoke-SqlScript -Sql (Get-Content -LiteralPath (Join-Path $root 'sql' 'Create-CopilotPurgeProcedure.sql') -Raw)

    $flatCols = Invoke-SqlText -As Table -Sql @"
SELECT c.name, t.name AS TypeName, c.max_length
FROM sys.columns AS c JOIN sys.types AS t ON t.user_type_id = c.user_type_id
WHERE c.object_id = OBJECT_ID(N'audit.RawLogFlattened') ORDER BY c.column_id;
"@
    $flatNames = @($flatCols.Rows | ForEach-Object { $_.name })
    # The column list dbo.RawLogFlattened has, in its order.
    $required = @('RecordId', 'TimeStamp', 'User', 'App', 'Location', 'App context',
                  'Accessed Resources', 'Accessed Resource Locations', 'Action', 'AIPlugin',
                  'JailbreakDetected', 'PromptInjectionDetected', 'BlockedResourceUrls',
                  'SensitivityLabelIds', 'IsCopilotLic')
    $missingCols = @($required | Where-Object { $_ -notin $flatNames })
    Assert-That ($missingCols.Count -eq 0) "audit.RawLogFlattened carries every RawLogFlattened column ($($missingCols -join ', '))"
    $sharedOrder = ($flatNames | Select-Object -First $required.Count) -join '|'
    Assert-That ($sharedOrder -eq ($required -join '|')) 'the shared columns are in the same order as the source table'

    $swl = [System.Diagnostics.Stopwatch]::StartNew()
    Invoke-SqlText -Sql 'EXEC audit.usp_FlattenCopilotAudit @RebuildAll = 1, @BatchSize = 50000' -ShowMessages -TimeoutSeconds 1800
    Write-Host ("  flatten took {0:N1}s" -f $swl.Elapsed.TotalSeconds) -ForegroundColor DarkGray

    # What the mock put in the JSON, counted from the day counts it reports.
    $dc = Invoke-RestMethod "$($mock.Base)/_control/daycounts"
    function Get-EveryNth([int] $Modulus) {
        $t = 0
        foreach ($n in $dc.counts) { $t += [math]::Ceiling($n / $Modulus) }
        return [long] $t
    }
    $expJailbreak = Get-EveryNth 13
    $expXpia      = Get-EveryNth 7
    $expBlocked   = Get-EveryNth 11
    $expWeb       = Get-EveryNth 3

    $flat = Invoke-SqlText -As Table -Sql @"
SELECT COUNT_BIG(*)                                                              AS Rows_,
       SUM(CASE WHEN JailbreakDetected = 'true' THEN 1 ELSE 0 END)               AS Jailbreak,
       SUM(CASE WHEN PromptInjectionDetected = 'true' THEN 1 ELSE 0 END)         AS Xpia,
       SUM(CASE WHEN BlockedResourceUrls IS NOT NULL THEN 1 ELSE 0 END)          AS Blocked,
       SUM(CASE WHEN AIPlugin = 'BingWebSearch' THEN 1 ELSE 0 END)               AS Web,
       SUM(CASE WHEN [Accessed Resources] IS NOT NULL THEN 1 ELSE 0 END)         AS WithRes,
       SUM(CASE WHEN [Accessed Resource Locations] LIKE 'https://%' THEN 1 ELSE 0 END) AS WithUrl,
       SUM(CASE WHEN [App context] LIKE 'https://%' THEN 1 ELSE 0 END)           AS WithCtx,
       SUM(CASE WHEN [Action] = 'Read' THEN 1 ELSE 0 END)                        AS Reads,
       SUM(CASE WHEN SensitivityLabelIds IS NOT NULL THEN 1 ELSE 0 END)          AS WithLabel,
       SUM(CASE WHEN UtcTimeStamp IS NOT NULL THEN 1 ELSE 0 END)                 AS WithTs,
       SUM(CASE WHEN IsCopilotLic IS NOT NULL THEN 1 ELSE 0 END)                 AS WithLic,
       COUNT(DISTINCT App)                                                       AS Apps,
       COUNT(DISTINCT [User])                                                    AS Users
FROM audit.RawLogFlattened;
"@
    $f = $flat.Rows[0]
    Assert-That ([long] $f.Rows_ -eq $expectedTotal) ("audit.RawLogFlattened has one row per record ({0:N0})" -f $f.Rows_)
    Assert-That ([long] $f.WithRes -eq $expectedTotal) 'Accessed Resources shredded on every row'
    Assert-That ([long] $f.WithUrl -eq $expectedTotal) 'Accessed Resource Locations shredded on every row'
    Assert-That ([long] $f.WithCtx -eq $expectedTotal) 'App context shredded from Contexts[].Id on every row'
    Assert-That ([long] $f.Reads -eq $expectedTotal) 'Action shredded on every row'
    Assert-That ([long] $f.WithLabel -eq $expectedTotal) 'SensitivityLabelIds shredded on every row'
    Assert-That ([long] $f.WithTs -eq $expectedTotal) 'CreationTime parsed into a real datetime on every row'
    Assert-That ([int] $f.Apps -eq 4) "all four AppHost values flattened ($($f.Apps))"
    Assert-That ([int] $f.Users -eq 50) "all 50 mock users flattened ($($f.Users))"
    Assert-That ([long] $f.Jailbreak -eq $expJailbreak) ("JailbreakDetected true on exactly the records that carry it ({0:N0} of {1:N0})" -f $f.Jailbreak, $expJailbreak)
    Assert-That ([long] $f.Xpia -eq $expXpia) ("PromptInjectionDetected true on exactly the XPIADetected records ({0:N0} of {1:N0})" -f $f.Xpia, $expXpia)
    Assert-That ([long] $f.Blocked -eq $expBlocked) ("BlockedResourceUrls set on exactly the policy-blocked records ({0:N0} of {1:N0})" -f $f.Blocked, $expBlocked)
    Assert-That ([long] $f.Web -eq $expWeb) ("AIPlugin captured BingWebSearch on exactly the web-grounded records ({0:N0} of {1:N0})" -f $f.Web, $expWeb)
    Assert-That ([long] $f.WithLic -eq 0) 'IsCopilotLic is NULL while audit.CopilotLicense is empty'

    # A spot check that a single row carries the right values, not just the right counts.
    $spot = Invoke-SqlText -As Table -Sql @"
SELECT TOP (1) * FROM audit.RawLogFlattened WHERE RecordId = '20260801-000000';
"@
    $sp = $spot.Rows[0]
    Assert-That ($sp['Accessed Resources'] -eq 'Q3, "final".xlsx') "a quoted resource name survived the shred ($($sp['Accessed Resources']))"
    Assert-That ($sp.JailbreakDetected -eq 'true' -and $sp.PromptInjectionDetected -eq 'true') 'record 0 has both security flags, as the mock built it'
    Assert-That ($sp.Location -eq 'CA') "Location came from ClientRegion ($($sp.Location))"
    Assert-That ($sp.App -eq 'Word') "App came from AppHost ($($sp.App))"

    # IsCopilotLic fills in once the licence table has the user.
    Invoke-SqlText -Sql "INSERT INTO audit.CopilotLicense (UserPrincipalName, IsCopilotLic) VALUES (N'user0@contoso.com', 1)"
    Invoke-SqlText -Sql 'EXEC audit.usp_FlattenCopilotAudit @BatchSize = 50000' -TimeoutSeconds 1800
    $lic = Invoke-SqlText -As Table -Sql @"
SELECT COUNT_BIG(*) AS Rows_, SUM(CASE WHEN IsCopilotLic = 1 THEN 1 ELSE 0 END) AS Licensed
FROM audit.RawLogFlattened;
"@
    Assert-That ([long] $lic.Rows[0].Rows_ -eq $expectedTotal) ("a second flatten MERGEd rather than duplicated ({0:N0} rows)" -f $lic.Rows[0].Rows_)
    Assert-That ([long] $lic.Rows[0].Licensed -gt 0) ("IsCopilotLic filled from audit.CopilotLicense ({0:N0} rows)" -f $lic.Rows[0].Licensed)

    # -----------------------------------------------------------------------
    Write-Host "`n7. JSON retention (audit.usp_PurgeCopilotAuditJson)" -ForegroundColor Cyan

    $before = [long] (Invoke-SqlText -As Scalar -Sql 'SELECT COUNT_BIG(*) FROM audit.CopilotAudit')

    $whatIf = Invoke-SqlText -As Table -TimeoutSeconds 900 -Sql 'EXEC audit.usp_PurgeCopilotAuditJson @RetainDays = 1, @WhatIf = 1'
    $afterWhatIf = [long] (Invoke-SqlText -As Scalar -Sql 'SELECT COUNT_BIG(*) FROM audit.CopilotAudit WHERE AuditDataGz IS NOT NULL')
    Assert-That ([long] $whatIf.Rows[0].RecordsInScope -eq $expectedTotal) ("@WhatIf reported the full scope ({0:N0})" -f $whatIf.Rows[0].RecordsInScope)
    Assert-That ($afterWhatIf -eq $expectedTotal) '@WhatIf = 1 changed nothing'

    $refusedPurge = $false
    try { Invoke-SqlText -Sql 'EXEC audit.usp_PurgeCopilotAuditJson @RetainDays = 0, @WhatIf = 0' }
    catch { $refusedPurge = $true }
    Assert-That $refusedPurge 'purge refuses @RetainDays = 0'

    # Partial retention. The procedure reports the cutoff it used, so the split
    # can be verified exactly without re-deriving SYSUTCDATETIME() here.
    $daysToMidRange = [int] ([math]::Floor(([datetime]::UtcNow - [datetime]::new(2026, 8, 5, 0, 0, 0, [DateTimeKind]::Utc)).TotalDays))
    $purge = Invoke-SqlText -As Table -TimeoutSeconds 1800 -ShowMessages `
        -Sql "EXEC audit.usp_PurgeCopilotAuditJson @RetainDays = $daysToMidRange, @WhatIf = 0"
    $cutoff = [datetime] $purge.Rows[0].CutoffUtc
    $purged = [long] $purge.Rows[0].RecordsPurged
    # Passed as an ISO-8601 string and CAST server-side: AddWithValue would bind
    # a DateTime as SqlDbType.DateTime, whose 1/300 s resolution could move the
    # boundary and misclassify a record sitting next to it.
    $cutoffIso = $cutoff.ToString('yyyy-MM-ddTHH:mm:ss.fffffff')

    $split = Invoke-SqlText -As Table -Parameters @{ '@c' = $cutoffIso } -Sql @"
DECLARE @cut DATETIME2(7) = CAST(@c AS DATETIME2(7));
SELECT COUNT_BIG(*)                                                             AS Total,
       SUM(CASE WHEN CreatedDateTime <  @cut AND AuditDataGz IS NOT NULL THEN 1 ELSE 0 END) AS OldWithJson,
       SUM(CASE WHEN CreatedDateTime >= @cut AND AuditDataGz IS NULL     THEN 1 ELSE 0 END) AS NewWithoutJson,
       SUM(CASE WHEN AuditDataGz IS NOT NULL THEN 1 ELSE 0 END)                 AS StillHoldingJson
FROM audit.CopilotAudit;
"@
    $sp2 = $split.Rows[0]
    Write-Host ("  cutoff {0:yyyy-MM-dd HH:mm}Z, {1:N0} record(s) had their JSON cleared" -f $cutoff, $purged) -ForegroundColor DarkGray
    Assert-That ($purged -gt 0 -and $purged -lt $expectedTotal) ("a partial retention purged only part of the range ({0:N0} of {1:N0})" -f $purged, $expectedTotal)
    Assert-That ([long] $sp2.Total -eq $before) ("no rows were removed, only their JSON ({0:N0})" -f $sp2.Total)
    Assert-That ([long] $sp2.OldWithJson -eq 0) 'every record older than the cutoff lost its JSON'
    Assert-That ([long] $sp2.NewWithoutJson -eq 0) 'every record at or after the cutoff kept its JSON'
    Assert-That ([long] $sp2.StillHoldingJson -eq ($before - $purged)) 'the surviving JSON count matches exactly'

    $flatSurvived = [long] (Invoke-SqlText -As Scalar -Sql 'SELECT COUNT_BIG(*) FROM audit.RawLogFlattened')
    $intSurvived  = [long] (Invoke-SqlText -As Scalar -Sql 'SELECT COUNT_BIG(*) FROM audit.CopilotInteraction')
    Assert-That ($flatSurvived -eq $expectedTotal -and $intSurvived -eq $expectedTotal) `
        'the flattened and reporting tables survived the JSON purge intact - the point of the default mode'

    # The view still returns every row; the purged ones just have no AuditData.
    $viewAfter = Invoke-SqlText -As Table -TimeoutSeconds 900 -Sql @"
SELECT COUNT_BIG(*) AS Rows_, SUM(CASE WHEN AuditData IS NULL THEN 1 ELSE 0 END) AS NullJson
FROM audit.vCopilotAuditRecord;
"@
    Assert-That ([long] $viewAfter.Rows[0].Rows_ -eq $expectedTotal) 'the export view still returns every record after a purge'
    Assert-That ([long] $viewAfter.Rows[0].NullJson -eq $purged) 'AuditData is NULL on exactly the purged records'

    # Row deletion mode.
    Invoke-SqlText -Sql 'EXEC audit.usp_PurgeCopilotAuditJson @RetainDays = 1, @DeleteRows = 1, @PurgeFlattened = 1, @WhatIf = 0' `
                   -TimeoutSeconds 1800
    $gone = Invoke-SqlText -As Table -Sql @"
SELECT (SELECT COUNT_BIG(*) FROM audit.CopilotAudit)       AS A,
       (SELECT COUNT_BIG(*) FROM audit.RawLogFlattened)    AS F,
       (SELECT COUNT_BIG(*) FROM audit.CopilotAuditStage)  AS S;
"@
    Assert-That ([long] $gone.Rows[0].A -eq 0) "@DeleteRows = 1 removed every aged record ($($gone.Rows[0].A) left)"
    Assert-That ([long] $gone.Rows[0].F -eq 0) "@PurgeFlattened = 1 removed the matching flattened rows ($($gone.Rows[0].F) left)"
    Assert-That ([long] $gone.Rows[0].S -eq 0) "@PurgeStaging cleared the second copy in staging ($($gone.Rows[0].S) left)"

    $refusedFlat = $false
    try { Invoke-SqlText -Sql 'EXEC audit.usp_PurgeCopilotAuditJson @RetainDays = 1, @PurgeFlattened = 1, @WhatIf = 0' }
    catch { $refusedFlat = $true }
    Assert-That $refusedFlat 'purge refuses @PurgeFlattened without @DeleteRows'

    # -----------------------------------------------------------------------
    Write-Host "`n8. Directory enrichment (tools/Update-CopilotAuditEnrichment.ps1)" -ForegroundColor Cyan

    # Stand in for the Graph SDK, pointing at mock_graph.py. Same approach as
    # Test-Orchestrator: shadow the cmdlets with global functions.
    $script:MockBase = $mock.Base
    function global:Connect-MgGraph { param([hashtable] $Splat) }
    function global:Invoke-MgGraphRequest {
        param([string] $Method, [string] $Uri, $OutputType, $Body)
        $full = if ($Uri -match '^https?://') { $Uri } else { "$script:MockBase$Uri" }
        $r = Invoke-WebRequest -Uri $full -Method $Method -SkipHttpErrorCheck -ErrorAction Stop
        if ([int] $r.StatusCode -ge 400) {
            # The script treats a 404 as "this user has left" and anything else
            # as a real failure, so the message has to say which.
            throw ("Graph returned {0} for {1}: {2}" -f [int] $r.StatusCode, $Uri, $r.Content)
        }
        return ($r.Content | ConvertFrom-Json)
    }

    try {
      # A child scope on purpose. Dot-sourcing pulls the script's entire param
      # block into the current scope, and PowerShell variable names are
      # case-insensitive, so its $Database silently blanks this suite's
      # $database - after which every query runs against the wrong catalogue
      # and fails with 'Invalid object name'. Running it in a child scope keeps
      # its parameters to itself.
      & {
        . (Join-Path $root 'tools' 'Update-CopilotAuditEnrichment.ps1')
        $ConnectionString = $connStr
        $TenantId = 'contoso.onmicrosoft.com'
        Invoke-Main

        $lic = Invoke-SqlText -As Table -Sql @"
SELECT COUNT(*)                                                          AS Rows_,
       SUM(CASE WHEN IsCopilotLic = 1 THEN 1 ELSE 0 END)                 AS Licensed,
       SUM(CASE WHEN DirectoryStatus = 'NotFound' THEN 1 ELSE 0 END)     AS Gone,
       SUM(CASE WHEN DirectoryStatus = 'NotFound' AND IsCopilotLic IS NULL THEN 1 ELSE 0 END) AS GoneUnknown,
       SUM(CASE WHEN SkuPartNumber = 'Microsoft_365_Copilot' THEN 1 ELSE 0 END) AS WithSku,
       SUM(CASE WHEN IsCopilotLic = 1 AND LicensedSinceUtc IS NOT NULL THEN 1 ELSE 0 END) AS WithSince,
       SUM(CASE WHEN IsCopilotLic = 1 AND CapabilityStatus = 'Enabled' THEN 1 ELSE 0 END)  AS Enabled_
FROM audit.CopilotLicense;
"@
        $l = $lic.Rows[0]
        Assert-That ([int] $l.Rows_ -eq 50) "audit.CopilotLicense covers every user in the audit data ($($l.Rows_))"
        Assert-That ([int] $l.Licensed -eq 17) "exactly the licensed users are marked ($($l.Licensed) of 17)"
        Assert-That ([int] $l.WithSku -eq 17) "SkuPartNumber recorded for each ($($l.WithSku))"
        Assert-That ([int] $l.WithSince -eq 17) "LicensedSinceUtc read from assignedPlans for each ($($l.WithSince))"
        Assert-That ([int] $l.Enabled_ -eq 17) "CapabilityStatus captured ($($l.Enabled_))"
        Assert-That ([int] $l.Gone -eq 1) "the departed user is recorded as NotFound ($($l.Gone))"
        Assert-That ([int] $l.GoneUnknown -eq 1) 'a departed user is left unknown, NOT reported as unlicensed'

        $au = Invoke-SqlText -As Table -Sql 'SELECT Id, DisplayName FROM audit.AdministrativeUnit'
        Assert-That ($au.Rows.Count -eq 1) "administrative units loaded ($($au.Rows.Count))"
        Assert-That ($au.Rows[0].DisplayName -eq 'AU-East') "the GUID in the audit data resolves to a name ($($au.Rows[0].DisplayName))"
        $auJoin = [long] (Invoke-SqlText -As Scalar -Sql @"
SELECT COUNT_BIG(*) FROM audit.CopilotInteraction AS i
WHERE EXISTS (SELECT 1 FROM audit.AdministrativeUnit AS a
              WHERE a.Id = '00000000-0000-0000-0000-0000000000ae');
"@)
        Assert-That ($auJoin -gt 0) 'the administrative unit id in the records matches the one Graph returned'

        $prof = Invoke-SqlText -As Table -Sql @"
SELECT COUNT(*)                                                      AS Rows_,
       COUNT(DISTINCT Department)                                    AS Depts,
       SUM(CASE WHEN UserType = 'Guest' THEN 1 ELSE 0 END)           AS Guests,
       SUM(CASE WHEN AccountEnabled = 0 THEN 1 ELSE 0 END)           AS Disabled_,
       SUM(CASE WHEN ManagerUpn IS NOT NULL THEN 1 ELSE 0 END)       AS WithManager,
       SUM(CASE WHEN CreatedDateTimeUtc IS NOT NULL THEN 1 ELSE 0 END) AS WithCreated,
       SUM(CASE WHEN DirectoryStatus = 'Found' THEN 1 ELSE 0 END)    AS Found_
FROM audit.UserProfile;
"@
        $pr = $prof.Rows[0]
        Assert-That ([int] $pr.Rows_ -eq 50) "audit.UserProfile covers every user ($($pr.Rows_))"
        Assert-That ([int] $pr.Found_ -eq 49) "49 resolved in the directory, 1 had left ($($pr.Found_))"
        Assert-That ([int] $pr.Depts -eq 4) "all four departments captured ($($pr.Depts))"
        Assert-That ([int] $pr.Guests -eq 2) "guest accounts flagged ($($pr.Guests))"
        Assert-That ([int] $pr.Disabled_ -eq 3) "disabled accounts flagged ($($pr.Disabled_))"
        Assert-That ([int] $pr.WithManager -eq 48) "manager resolved for everyone who has one ($($pr.WithManager) of 48)"
        Assert-That ([int] $pr.WithCreated -eq 49) "createdDateTime parsed ($($pr.WithCreated))"

        # The views that make this worth having.
        $adopt = Invoke-SqlText -As Table -Sql @"
SELECT COUNT(*)                                          AS Rows_,
       SUM(CASE WHEN NeverUsed = 1 THEN 1 ELSE 0 END)    AS Shelfware,
       SUM(CASE WHEN Department IS NOT NULL THEN 1 ELSE 0 END) AS WithDept,
       SUM(CASE WHEN Interactions > 0 THEN 1 ELSE 0 END) AS Active
FROM audit.vCopilotAdoption;
"@
        $ad = $adopt.Rows[0]
        Assert-That ([int] $ad.Rows_ -eq 17) "vCopilotAdoption lists the licensed users ($($ad.Rows_))"
        Assert-That ([int] $ad.Active -eq 17) 'every licensed mock user has interactions'
        Assert-That ([int] $ad.Shelfware -eq 0) 'no licensed user is shelfware in this fixture'
        Assert-That ([int] $ad.WithDept -eq 17) 'adoption rows carry the department from the profile'

        $dept = Invoke-SqlText -As Table -Sql @"
SELECT COUNT(DISTINCT Department) AS Depts, SUM(Interactions) AS Interactions
FROM audit.vCopilotUsageByDepartment;
"@
        Assert-That ([long] $dept.Rows[0].Interactions -eq $expectedTotal) `
            ("vCopilotUsageByDepartment accounts for every interaction ({0:N0})" -f $dept.Rows[0].Interactions)
        Assert-That ([int] $dept.Rows[0].Depts -ge 4) "usage split across departments ($($dept.Rows[0].Depts))"

        # Re-running must update in place, not duplicate.
        Invoke-Main
        $again = Invoke-SqlText -As Table -Sql @"
SELECT (SELECT COUNT(*) FROM audit.CopilotLicense)     AS L,
       (SELECT COUNT(*) FROM audit.UserProfile)        AS P,
       (SELECT COUNT(*) FROM audit.AdministrativeUnit) AS A;
"@
        Assert-That ([int] $again.Rows[0].L -eq 50 -and [int] $again.Rows[0].P -eq 50 -and [int] $again.Rows[0].A -eq 1) `
            "a second run updated in place rather than duplicating ($($again.Rows[0].L)/$($again.Rows[0].P)/$($again.Rows[0].A))"

        # -AllLicensedUsers exercises the licence-holder enumeration path.
        $AllLicensedUsers = $true
        Invoke-Main
        $withAll = [int] (Invoke-SqlText -As Scalar -Sql 'SELECT COUNT(*) FROM audit.CopilotLicense')
        Assert-That ($withAll -eq 50) "-AllLicensedUsers added no phantom users here ($withAll)"
      }
    }
    finally {
        Remove-Item function:\Connect-MgGraph -ErrorAction SilentlyContinue
        Remove-Item function:\Invoke-MgGraphRequest -ErrorAction SilentlyContinue
    }
}
finally { Stop-MockGraph $mock }

# ---------------------------------------------------------------------------
Write-Host ''
if ($script:Failures.Count) {
    Write-Host "$($script:Failures.Count) assertion(s) failed." -ForegroundColor Red
    exit 1
}
Write-Host 'All SQL integration tests passed.' -ForegroundColor Green
