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
      SQLIT_PASSWORD   required, unless SQLIT_INTEGRATED is set
      SQLIT_INTEGRATED 1 = Windows authentication instead of a SQL login,
                       for a local instance with no sa password to hand
      SQLIT_DATABASE   (default CopilotAuditIntegration) - dropped and recreated

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
$script:ITIntegrated = $env:SQLIT_INTEGRATED -eq '1'

if (-not $password -and -not $script:ITIntegrated) {
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
    if ($script:ITIntegrated) {
        $csb['Integrated Security'] = $true
    }
    else {
        $csb['User ID'] = $script:ITUser
        $csb['Password'] = $script:ITPassword
    }
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
    # \r? because a Windows checkout has CRLF endings, and .NET's multiline $
    # matches before \n only - 'GO\r' would otherwise never split.
    $batches = [regex]::Split($Sql, '(?im)^[ \t]*GO[ \t]*(?:--[^\r\n]*)?\r?$')
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
Assert-That (($names -join ',') -eq 'CopilotAuditStage,CopilotBackfillRange,CopilotBackfillWindow,CopilotLoad,CopilotLoadStage,CopilotLoadWindow,PipelineRun,PipelineRunStep,PipelineSetting') `
    "Create-CopilotAuditSchema.sql built the backfill and incremental-load tables, and is idempotent ($($names -join ', '))"

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

    # 2.3.0: each batch's codes go into the value catalogue, for Admin > Value names.
    $vc = (Invoke-SqlText -As Table -Sql @"
SELECT (SELECT DistinctValues FROM audit.ValueCatalogColumn WHERE ColumnId = 2) AS Apps,
       (SELECT COUNT(DISTINCT App) FROM audit.RawLogFlattened WHERE NULLIF(App, N'') IS NOT NULL) AS AppsInData,
       (SELECT SUM(Records) FROM audit.ValueCatalog WHERE ColumnId = 1) AS TenantRecords,
       (SELECT COUNT(*) FROM audit.RawLogFlattened WHERE OrganizationId IS NOT NULL) AS WithTenant,
       (SELECT COUNT(*) FROM audit.ValueCatalog WHERE ColumnId = 1 AND Value = LOWER(Value)) AS LowerGuids;
"@).Rows[0]
    Assert-That ([int] $vc.Apps -gt 0 -and [int] $vc.Apps -eq [int] $vc.AppsInData) "the flatten catalogues every distinct app, batch by batch ($($vc.Apps) of $($vc.AppsInData))"
    Assert-That ([long] $vc.TenantRecords -eq [long] $vc.WithTenant -and [int] $vc.LowerGuids -ge 1) "and counts each record once in a full rebuild, with GUIDs in lower case ($($vc.TenantRecords) of $($vc.WithTenant))"
    $before = [string] (Invoke-SqlText -As Scalar -Sql "SELECT STRING_AGG(CONCAT(ColumnId, ':', Value, ':', Records), '|') WITHIN GROUP (ORDER BY ColumnId, Value) FROM audit.ValueCatalog;")
    Invoke-SqlText -Sql 'EXEC audit.usp_CatalogValues @Rebuild = 1;'
    $after = [string] (Invoke-SqlText -As Scalar -Sql "SELECT STRING_AGG(CONCAT(ColumnId, ':', Value, ':', Records), '|') WITHIN GROUP (ORDER BY ColumnId, Value) FROM audit.ValueCatalog;")
    Assert-That ($before -eq $after -and $after.Length -gt 0) 'a rebuild of the catalogue from every record gives the same values and counts as the batches did'

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
    # Blocked by a failed Status (every 11th) or, since 2.2.0, by a Purview policy outcome (every 19th).
    $expBlockedAny = $expBlocked + (Get-EveryNth 19) - (Get-EveryNth 209)

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
    Assert-That ([long] $f.Blocked -eq $expBlockedAny) ("BlockedResourceUrls set on exactly the blocked records ({0:N0} of {1:N0})" -f $f.Blocked, $expBlockedAny)
    Assert-That ([long] $f.Web -eq $expWeb) ("AIPlugin captured BingWebSearch on exactly the web-grounded records ({0:N0} of {1:N0})" -f $f.Web, $expWeb)
    Assert-That ([long] $f.WithLic -eq 0) 'IsCopilotLic is NULL while audit.CopilotLicense is empty'

    # A spot check that a single row carries the right values, not just the right counts.
    $spot = Invoke-SqlText -As Table -Sql @"
SELECT TOP (1) * FROM audit.RawLogFlattened WHERE RecordId = '20260801-000000';
"@
    $sp = $spot.Rows[0]
    # Record 0 is also a 19th record, so it read a second resource, listed after the first.
    Assert-That ($sp['Accessed Resources'] -eq 'Q3, "final".xlsx; Payroll') "a quoted resource name survived the shred ($($sp['Accessed Resources']))"
    Assert-That ($sp.JailbreakDetected -eq 'true' -and $sp.PromptInjectionDetected -eq 'true') 'record 0 has both security flags, as the mock built it'
    Assert-That ($sp.Location -eq 'CA') "Location came from ClientRegion ($($sp.Location))"
    Assert-That ($sp.App -eq 'Word') "App came from AppHost ($($sp.App))"

    # 2.2.0: every other field of the record, and the element tables.
    $exp19 = Get-EveryNth 19; $exp209 = Get-EveryNth 209
    $expMem = Get-EveryNth 5; $expDlp = Get-EveryNth 17; $expEvLabel = Get-EveryNth 4; $expStarter = Get-EveryNth 2
    $x = (Invoke-SqlText -As Table -Sql @"
SELECT (SELECT COUNT(*) FROM audit.RawLogRecordShape)                                AS Shapes,
       (SELECT SUM(Records) FROM audit.RawLogRecordShape
        WHERE RecordType = 261 AND Operation = 'CopilotInteraction' AND Workload = 'Copilot' AND UserType = 0
          AND [Version] = 1 AND CopilotLogVersion = '1.0.0.0')                       AS ShapeRecords,
       (SELECT SUM(Records) FROM audit.RawLogRecordShape WHERE HasAdminUnits = 1)   AS WithAdminUnits,
       (SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID(N'audit.RawLogFlattened')
          AND name IN (N'RecordType', N'Operation', N'Workload', N'UserType', N'Version', N'CopilotLogVersion')) AS ConstCols,
       COUNT(OrganizationId) AS OrgId, COUNT(UserKey) AS UserKey, COUNT(ClientIP) AS ClientIP,
       SUM(CASE WHEN CopilotEventData_LicenseType = 'Starter' THEN 1 ELSE 0 END)       AS Starter,
       SUM(CASE WHEN CopilotEventData_LicenseType = 'Premium' THEN 1 ELSE 0 END)       AS Premium,
       COUNT(CopilotEventData_ConversationId)                                          AS Conv,
       SUM(CASE WHEN CopilotEventData_MemoryUpdated = 1 THEN 1 ELSE 0 END)             AS Memory,
       SUM(CASE WHEN CopilotEventData_DLPEvaluationDeferred = 1 THEN 1 ELSE 0 END)     AS Dlp,
       COUNT(CopilotEventData_SensitivityLabelId)                                      AS EvLabel,
       SUM(CASE WHEN ModelVersion = '2025-08-07' THEN 1 ELSE 0 END)                    AS ModelVer,
       SUM(CASE WHEN ISJSON(CopilotEventData_ModelTransparencyDetails) = 1 THEN 1 ELSE 0 END) AS MtdJson,
       SUM(CASE WHEN MessageCount = 2 THEN 1 ELSE 0 END)                               AS Msgs,
       SUM(CASE WHEN BlockedResourceUrls IS NOT NULL THEN 1 ELSE 0 END)                AS BlockedRecords,
       (SELECT COUNT_BIG(*) FROM audit.RawLogAccessedResource)                         AS Elements,
       (SELECT SUM(CAST(IsBlocked AS INT)) FROM audit.RawLogAccessedResource)          AS BlockedElements,
       (SELECT COUNT(*) FROM audit.RawLogAccessedResource WHERE PolicyOutcomes = N'Blocked') AS PolicyBlocked,
       (SELECT COUNT(*) FROM audit.RawLogAccessedResource WHERE Site = N'https://contoso.sharepoint.com/sites/finance') AS FinanceSite,
       (SELECT COUNT(*) FROM audit.RawLogAccessedResource WHERE Site = N'https://outlook.office365.com' AND [Type] = N'EmailMessage') AS MailSite,
       (SELECT COUNT(*) FROM audit.RawLogAccessedResource WHERE SensitivityLabelId IS NOT NULL) AS LabelledElements,
       (SELECT COUNT(*) FROM audit.RawLogContext WHERE [Type] = N'xlsx') AS Contexts
FROM audit.RawLogFlattened;
"@).Rows[0]
    # The mock gives every third record an administrative unit, so two shapes: with and without.
    Assert-That ([int] $x.Shapes -eq 2 -and [long] $x.ShapeRecords -eq $expectedTotal -and [int] $x.ConstCols -eq 0) `
        ("the fields that carry no information are counted by shape in audit.RawLogRecordShape, not stored per row ({0:N0} records, {1} shapes)" -f $x.ShapeRecords, $x.Shapes)
    Assert-That ([long] $x.WithAdminUnits -eq (Get-EveryNth 3)) ("and a field that starts to carry something is seen there ({0:N0} records with an administrative unit)" -f $x.WithAdminUnits)
    Assert-That ([long] $x.OrgId -eq $expectedTotal -and [long] $x.UserKey -eq $expectedTotal -and [long] $x.ClientIP -eq $expectedTotal) 'OrganizationId and UserKey parsed as GUIDs, and ClientIP, on every row'
    Assert-That ([long] $x.Starter -eq $expStarter -and [long] $x.Premium -eq ($expectedTotal - $expStarter)) ("CopilotEventData.LicenseType as recorded ({0:N0} Starter, {1:N0} Premium)" -f $x.Starter, $x.Premium)
    Assert-That ([long] $x.Conv -eq $expectedTotal -and [long] $x.ModelVer -eq $expectedTotal -and [long] $x.MtdJson -eq $expectedTotal) 'ConversationId, ModelVersion and the model details JSON on every row'
    Assert-That ([long] $x.Memory -eq $expMem -and [long] $x.Dlp -eq $expDlp -and [long] $x.EvLabel -eq $expEvLabel) `
        ("MemoryUpdated, DLPEvaluationDeferred and the interaction's own label on exactly the records that carry them ({0:N0}, {1:N0}, {2:N0})" -f $x.Memory, $x.Dlp, $x.EvLabel)
    Assert-That ([long] $x.Msgs -eq $expectedTotal) 'MessageCount counts prompts and responses (2 per record)'
    Assert-That ([long] $x.Elements -eq ($expectedTotal + $exp19)) ("one element row per accessed resource ({0:N0})" -f $x.Elements)
    Assert-That ([long] $x.PolicyBlocked -eq $exp19) ("a Purview outcome other than None, in PolicyDetails as JSON text, is read ({0:N0} of {1:N0})" -f $x.PolicyBlocked, $exp19)
    Assert-That ([long] $x.BlockedElements -eq ($expBlocked + $exp19)) ("blocked: a failed Status or a policy outcome ({0:N0} elements)" -f $x.BlockedElements)
    Assert-That ([long] $x.BlockedRecords -eq ($expBlocked + $exp19 - $exp209)) ("BlockedResourceUrls now includes policy-blocked resources ({0:N0} records)" -f $x.BlockedRecords)
    Assert-That ([long] $x.FinanceSite -eq $expectedTotal -and [long] $x.MailSite -eq $exp19) 'sites cut to https://host/sites/name, and to the host elsewhere'
    Assert-That ([long] $x.LabelledElements -eq $expectedTotal -and [long] $x.Contexts -eq $expectedTotal) 'element labels parsed as GUIDs, and one Contexts row per context'

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

    # A full re-flatten after the purge (what the 2.2.0 upgrade asks for) skips the
    # records whose JSON is gone, rather than blanking what was shredded from it.
    $keep = { (Invoke-SqlText -As Table -Sql @"
SELECT COUNT(UserKey) AS UserKey, COUNT([Accessed Resources]) AS Res, (SELECT COUNT_BIG(*) FROM audit.RawLogAccessedResource) AS Elements
FROM audit.RawLogFlattened;
"@).Rows[0] }
    $k0 = & $keep
    Invoke-SqlText -Sql 'EXEC audit.usp_FlattenCopilotAudit @BatchSize = 50000' -TimeoutSeconds 1800
    $k1 = & $keep
    Assert-That ([long] $k1.UserKey -eq [long] $k0.UserKey -and [long] $k1.Res -eq [long] $k0.Res -and [long] $k1.Elements -eq [long] $k0.Elements) `
        ("re-flattening after the purge keeps every purged record's columns and elements ({0:N0} elements)" -f $k1.Elements)

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
    Write-Host "`n8. Licences from Graph, users from AD (tools/Update-CopilotAuditEnrichment.ps1, tools/Export-UsersWithHierarchy-ToSql.ps1)" -ForegroundColor Cyan

    # Stand in for the Graph SDK, pointing at mock_graph.py. Same approach as
    # Test-Orchestrator: shadow the cmdlets with global functions. Every path
    # asked for is recorded, to prove the licence tool asks for nothing else.
    $script:MockBase = $mock.Base
    $script:GraphPaths = [System.Collections.Generic.List[string]]::new()
    function global:Connect-MgGraph { param([hashtable] $Splat) }
    function global:Invoke-MgGraphRequest {
        param([string] $Method, [string] $Uri, $OutputType, $Body)
        $script:GraphPaths.Add(($Uri -replace '\?.*$', ''))
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

        # 2.3.0: the seat counts /subscribedSkus already returns, and each person's Entra account state and hire date.
        $pool = (Invoke-SqlText -As Table -Sql @"
SELECT (SELECT COUNT(*) FROM audit.LicenseSku) AS Skus,
       (SELECT Enabled + Warning FROM audit.LicenseSku WHERE IsCopilot = 1) AS Bought,
       (SELECT Consumed FROM audit.LicenseSku WHERE IsCopilot = 1) AS Assigned,
       (SELECT COUNT(*) FROM audit.LicenseSkuDay WHERE SnapshotDate = CAST(SYSUTCDATETIME() AS DATE)) AS Today,
       (SELECT COUNT(*) FROM audit.CopilotLicense WHERE EntraAccountEnabled = 0) AS Disabled,
       (SELECT COUNT(*) FROM audit.CopilotLicense WHERE EmployeeHireDate IS NOT NULL) AS Hired;
"@).Rows[0]
        Assert-That ([int] $pool.Skus -eq 2 -and [int] $pool.Bought -eq 105 -and [int] $pool.Assigned -eq 17) "the licence pool is kept per SKU: 105 Copilot seats bought (100 + 5 in grace), 17 assigned ($($pool.Bought), $($pool.Assigned))"
        Assert-That ([int] $pool.Today -eq 2) 'and a snapshot per SKU for today, for the trend'
        Assert-That ([int] $pool.Disabled -eq 3 -and [int] $pool.Hired -gt 0) "Entra's accountEnabled and employeeHireDate are recorded ($($pool.Disabled) disabled, $($pool.Hired) with a hire date)"
        Assert-That ([int] $l.GoneUnknown -eq 1) 'a departed user is left unknown, NOT reported as unlicensed'

        # Licences only: nothing that needs more than User.Read.All and
        # LicenseAssignment.Read.All, and no Entra profile tables.
        $asked = @($script:GraphPaths | Sort-Object -Unique)
        $other = @($asked | Where-Object { $_ -ne '/v1.0/subscribedSkus' -and $_ -notmatch '^/v1\.0/users/[^/]+$' })
        Assert-That ($other.Count -eq 0) "the licence tool calls only /subscribedSkus and /users/{upn} - no manager, profile or administrative-unit call ($($other -join ', '))"
        $entraTables = [int] (Invoke-SqlText -As Scalar -Sql "SELECT COUNT(*) FROM sys.tables WHERE SCHEMA_NAME(schema_id) = 'audit' AND name IN ('UserProfile', 'AdministrativeUnit');")
        Assert-That ($entraTables -eq 0) 'no Entra profile or administrative-unit table is created'

        # Re-running must update in place, not duplicate.
        Invoke-Main
        $again = [int] (Invoke-SqlText -As Scalar -Sql 'SELECT COUNT(*) FROM audit.CopilotLicense')
        Assert-That ($again -eq 50) "a second run updated in place rather than duplicating ($again)"

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

    # The user directory, as the AD export writes it. The directory search
    # itself needs a domain; everything after it - rows, the transactional
    # replace, the match report - runs here, through the tool's own functions.
    #   user0 is the top; user1..user3 report to user0; everyone else to
    #   user1..user3 in turn. user48's AD UPN is not their sign-in name (only
    #   their mail is), and user49 has left: a disabled account.
    & {
        . (Join-Path $root 'tools' 'Export-UsersWithHierarchy-ToSql.ps1')
        $depts = 'Retail Banking', 'Risk', 'Technology', 'Operations'
        $entries = foreach ($n in 0..49) {
            $mgr = if ($n -eq 0) { $null } elseif ($n -le 3) { 0 } else { 1 + ($n % 3) }
            [pscustomobject]@{
                dn = "CN=user$n,DC=corp"; upn = $(if ($n -eq 48) { 'user48@corp.contoso.com' } else { "user$n@contoso.com" })
                mail = "user$n@contoso.com"; disp = "User $n"; title = 'Analyst'; dept = $depts[$n % 4]
                div = $(if ($n % 4 -eq 0) { 'Retail' } else { 'Corporate' }); off = "Floor $($n % 5)"; city = 'Toronto'
                country = 'Canada'; company = 'Contoso'; etype = $(if ($n % 10 -eq 5) { 'NONW' } else { 'EMP' })
                mgr = $(if ($null -ne $mgr) { "CN=user$mgr,DC=corp" }); uac = $(if ($n -eq 49) { 514 } else { 512 })
            }
        }
        $rows = ConvertTo-UserDirectoryTable -Entries $entries -EmployeeTypeMap @{ 'EMP' = 'EMPLOYEE'; 'NONW' = 'CONTRACTOR' } -EmployeeTypes @('EMP', 'NONW')
        $n = Write-UserDirectory -ConnString $connStr -Table $rows.Table
        $n2 = Write-UserDirectory -ConnString $connStr -Table $rows.Table
        $stored = [int] (Invoke-SqlText -As Scalar -Sql 'SELECT COUNT(*) FROM audit.UserDirectory')
        Assert-That ($n -eq 50 -and $stored -eq 50) "audit.UserDirectory holds every user, and a second load replaces rather than appends ($stored)"

        $d = (Invoke-SqlText -As Table -Sql @"
SELECT (SELECT CONCAT(OrgDepth, ':', Mgr1, '>', Mgr2, '|', ManagerUPN) FROM audit.UserDirectory WHERE UserPrincipalName = 'user7@contoso.com') AS U7,
       (SELECT COUNT(*) FROM audit.UserDirectory WHERE AccountEnabled = 0) AS Disabled_,
       (SELECT COUNT(*) FROM audit.UserDirectory WHERE IsManager = 1) AS Managers,
       (SELECT COUNT(*) FROM audit.UserDirectory WHERE EmployeeType = 'CONTRACTOR') AS Contractors;
"@).Rows[0]
        Assert-That ("$($d.U7)" -eq '2:User 0>User 2|user2@contoso.com') "the chain and manager land in SQL (user7: $($d.U7))"
        Assert-That ([int] $d.Disabled_ -eq 1 -and [int] $d.Managers -eq 4 -and [int] $d.Contractors -eq 5) `
            "disabled, manager and contractor flags ($($d.Disabled_)/$($d.Managers)/$($d.Contractors))"

        $m = Get-DirectoryMatch -ConnString $connStr
        Assert-That ($m.AuditUsers -eq 50 -and $m.ByUpn -eq 49 -and $m.ByMailOnly -eq 1) `
            "the match report: 49 of 50 audit users by UPN, 1 more only by mail ($($m.ByUpn)/$($m.AuditUsers), $($m.ByMailOnly))"
    }

    # The views that make this worth having: licences from Graph, departments from AD.
    $licensedMatched = [int] (Invoke-SqlText -As Scalar -Sql "SELECT COUNT(*) FROM audit.CopilotLicense WHERE IsCopilotLic = 1 AND UserPrincipalName <> 'user48@contoso.com';")
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
    Assert-That ([int] $ad.WithDept -eq $licensedMatched) "adoption rows carry the department from AD where the UPN matches ($($ad.WithDept) of $licensedMatched)"

    $dept = Invoke-SqlText -As Table -Sql @"
SELECT COUNT(DISTINCT Department) AS Depts, SUM(Interactions) AS Interactions
FROM audit.vCopilotUsageByDepartment;
"@
    Assert-That ([long] $dept.Rows[0].Interactions -eq $expectedTotal) `
        ("vCopilotUsageByDepartment accounts for every interaction ({0:N0})" -f $dept.Rows[0].Interactions)
    Assert-That ([int] $dept.Rows[0].Depts -ge 4) "usage split across departments ($($dept.Rows[0].Depts))"

    # -----------------------------------------------------------------------
    Write-Host "`n9. Licence lifecycle events (audit.usp_ShredLicenseEvents)" -ForegroundColor Cyan

    Invoke-SqlScript -Sql (Get-Content -LiteralPath (Join-Path $root 'sql' 'Create-LicenseEventSchema.sql') -Raw)
    Invoke-SqlScript -Sql (Get-Content -LiteralPath (Join-Path $root 'sql' 'Create-LicenseEventProcedure.sql') -Raw)

    # Section 7 emptied audit.CopilotAudit, so it is free for AzureActiveDirectory
    # records - which is what that table holds in a licence-operations database.
    #
    # The fixtures reproduce the awkward part of the real data: 'Change user
    # license' carries no ModifiedProperties at all, and the licence lists
    # arrive on a separate 'Update user' record sharing its InterSystemsId.
    # A shredder that only read the first record would report every change with
    # no detail, which is exactly the trap this pairs back together.
    $mkEvent = {
        param($RecordId, $Op, $Actor, $ObjectId, $Correlation, $Modified, $TargetJson)
        $doc = [ordered]@{
            CreationTime   = '2026-09-10T12:00:00'
            Id             = $RecordId
            Operation      = $Op
            RecordType     = 8
            ResultStatus   = 'Success'
            Workload       = 'AzureActiveDirectory'
            UserId         = $Actor
            ObjectId       = $ObjectId
            InterSystemsId = $Correlation
            ModifiedProperties = $Modified
            Target         = $TargetJson
        }
        return ($doc | ConvertTo-Json -Depth 10 -Compress)
    }

    $licJson = '[{"Name":"AssignedLicense","OldValue":"[]","NewValue":"[Microsoft_365_Copilot]"},{"Name":"AssignedPlan","OldValue":"[]","NewValue":"[Microsoft365Copilot]"}]'
    $events = @(
        # detail-free change, paired with the update that carries the licences
        @{ Id = 'evt-0001'; Op = 'Change user license.'; Actor = 'admin@contoso.com'; ObjectId = 'user1@contoso.com'; Corr = 'corr-a'; Mod = @(); Tgt = @() }
        @{ Id = 'evt-0002'; Op = 'Update user';          Actor = 'admin@contoso.com'; ObjectId = 'user1@contoso.com'; Corr = 'corr-a'; Mod = (ConvertFrom-Json $licJson); Tgt = @() }
        # a change whose ObjectId is a GUID, so the UPN is only in Target[]
        @{ Id = 'evt-0003'; Op = 'Change user license';  Actor = 'admin@contoso.com'; ObjectId = '11111111-2222-3333-4444-555555555555'; Corr = 'corr-b'; Mod = (ConvertFrom-Json $licJson); Tgt = @(@{ ID = 'user2@contoso.com'; Type = 5 }) }
        # provisioning and revocation
        @{ Id = 'evt-0004'; Op = 'Add user.';            Actor = 'admin@contoso.com'; ObjectId = 'user3@contoso.com'; Corr = 'corr-c'; Mod = @(); Tgt = @() }
        @{ Id = 'evt-0005'; Op = 'Delete user.';         Actor = 'admin@contoso.com'; ObjectId = 'user3@contoso.com'; Corr = 'corr-d'; Mod = @(); Tgt = @() }
    )

    foreach ($e in $events) {
        $json = & $mkEvent $e.Id $e.Op $e.Actor $e.ObjectId $e.Corr $e.Mod $e.Tgt
        Invoke-SqlText -Parameters @{ '@id' = $e.Id; '@op' = $e.Op; '@json' = $json } -Sql @"
INSERT INTO audit.CopilotAudit
    (RecordId, CreatedDateTime, RecordType, Operation, UserId, UserPrincipalName, AuditDataGz, WindowId)
VALUES (@id, '2026-09-10T12:00:00', 'azureActiveDirectory', @op, 'admin@contoso.com', 'admin@contoso.com',
        COMPRESS(CAST(@json AS NVARCHAR(MAX))), 1);
"@
    }

    Invoke-SqlText -Sql 'EXEC audit.usp_ShredLicenseEvents @RebuildAll = 1' -ShowMessages -TimeoutSeconds 600

    $ev = Invoke-SqlText -As Table -Sql @"
SELECT COUNT(*)                                                        AS Rows_,
       SUM(CASE WHEN Operation = 'Change user license' THEN 1 ELSE 0 END) AS Changes,
       SUM(CASE WHEN Operation = 'Add user' THEN 1 ELSE 0 END)         AS Adds,
       SUM(CASE WHEN Operation = 'Delete user' THEN 1 ELSE 0 END)      AS Deletes,
       SUM(CASE WHEN TargetUpn LIKE '%@%' THEN 1 ELSE 0 END)           AS WithTarget,
       SUM(CASE WHEN ActorUpn = 'admin@contoso.com' THEN 1 ELSE 0 END) AS WithActor
FROM audit.LicenseEvent;
"@
    $v = $ev.Rows[0]
    Assert-That ([int] $v.Rows_ -eq 5) "every event shredded ($($v.Rows_))"
    Assert-That ([int] $v.Changes -eq 2) "the trailing period on 'Change user license.' is normalised away ($($v.Changes) of 2)"
    Assert-That ([int] $v.Adds -eq 1 -and [int] $v.Deletes -eq 1) 'provisioning and revocation events are distinguishable'
    Assert-That ([int] $v.WithTarget -eq 5) "the affected user resolved on every event, including the GUID ObjectId one ($($v.WithTarget))"
    Assert-That ([int] $v.WithActor -eq 5) 'the actor who made each change is recorded'

    $guidTarget = Invoke-SqlText -As Scalar -Sql "SELECT TargetUpn FROM audit.LicenseEvent WHERE RecordId = 'evt-0003'"
    Assert-That ("$guidTarget" -eq 'user2@contoso.com') "a GUID ObjectId falls back to Target[] for the UPN ($guidTarget)"

    $props = Invoke-SqlText -As Table -Sql @"
SELECT COUNT(*) AS Rows_, COUNT(DISTINCT RecordId) AS Records,
       SUM(CASE WHEN [Name] = 'AssignedLicense' THEN 1 ELSE 0 END) AS Licences
FROM audit.LicenseEventProperty;
"@
    Assert-That ([int] $props.Rows[0].Rows_ -eq 4) "ModifiedProperties shredded generically, whatever the names ($($props.Rows[0].Rows_))"
    Assert-That ([int] $props.Rows[0].Licences -eq 2) 'the licence property came through on both records that carry it'

    # The point of the view: the detail-free record still reports its licences,
    # taken from the paired record.
    # Every licence property of the pair comes across (both AssignedLicense and
    # AssignedPlan), not just the first.
    $paired = Invoke-SqlText -As Table -Sql @"
SELECT PropertyName, NewValue, FromPairedRecord, TargetUpn
FROM audit.vLicenseChange WHERE RecordId = 'evt-0001' ORDER BY PropertyName;
"@
    $pairedLicence = @($paired.Rows | Where-Object PropertyName -eq 'AssignedLicense')
    Assert-That ($paired.Rows.Count -eq 2 -and $pairedLicence.Count -eq 1) `
        "a detail-free 'Change user license' picks up all its licence properties from the paired record ($(@($paired.Rows | ForEach-Object PropertyName) -join ', '))"
    Assert-That (@($paired.Rows | Where-Object { [int] $_.FromPairedRecord -ne 1 }).Count -eq 0) 'and they are marked as coming from the pair, not invented'
    Assert-That ("$($pairedLicence[0].NewValue)" -like '*Copilot*') "the new licence value is readable ($($pairedLicence[0].NewValue))"

    $life = Invoke-SqlText -As Table -Sql @"
SELECT TargetUpn, AccountCreatedUtc, AccountDeletedUtc, LicenceChangeCount
FROM audit.vLicenseLifecycle WHERE TargetUpn = 'user3@contoso.com';
"@
    Assert-That ($life.Rows.Count -eq 1 -and $life.Rows[0].AccountCreatedUtc -isnot [DBNull] -and $life.Rows[0].AccountDeletedUtc -isnot [DBNull]) `
        'vLicenseLifecycle reports both provisioning and revocation for a user'

    # -----------------------------------------------------------------------
    # 9b. The shapes a live tenant actually produced (1,042 records, 180 days).
    #
    # Most licence changes arrive CHUNKED: too large for one record, so Entra
    # writes several with empty ModifiedProperties and an additionalDetails of
    # {"id","seq","b"}, where the b fragments joined in seq order are one JSON
    # document whose targetUpdatedProperties holds the change. Each change is
    # also raised twice - 'Change user license' and 'Update user' - with the
    # same detail and InterSystemsId. And 'Update user' is mostly not about
    # licences at all (Authenticator devices and the like), a failed
    # assignment records only MethodExecutionResult, and the operation filter
    # for 'Add user' also returns 'Add user sponsor'.
    Write-Host "`n9b. Chunked, duplicated and non-licence directory events" -ForegroundColor Cyan

    $mkDoc = {
        param($RecordId, $Op, $Actor, $ObjectId, $Correlation, $Modified, $Extended)
        $doc = [ordered]@{
            CreationTime       = '2026-09-11T09:30:00'
            Id                 = $RecordId
            Operation          = $Op
            RecordType         = 8
            ResultStatus       = 'Success'
            Workload           = 'AzureActiveDirectory'
            UserId             = $Actor
            ObjectId           = $ObjectId
            InterSystemsId     = $Correlation
            ExtendedProperties = $Extended
            ModifiedProperties = $Modified
            Target             = @()
        }
        return ($doc | ConvertTo-Json -Depth 10 -Compress)
    }
    $insertDoc = {
        param($RecordId, $Op, $Json)
        Invoke-SqlText -Parameters @{ '@id' = $RecordId; '@op' = $Op; '@json' = $Json } -Sql @"
INSERT INTO audit.CopilotAudit
    (RecordId, CreatedDateTime, RecordType, Operation, UserId, UserPrincipalName, AuditDataGz, WindowId)
VALUES (@id, '2026-09-11T09:30:00', 'azureActiveDirectory', @op, 'admin@contoso.com', 'admin@contoso.com',
        COMPRESS(CAST(@json AS NVARCHAR(MAX))), 1);
"@
    }
    # Split one payload into $Parts additionalDetails fragments, the way Entra does.
    $chunkify = {
        param([string] $Payload, [string] $GroupId, [int] $Parts)
        $size = [int] [math]::Ceiling($Payload.Length / $Parts)
        for ($i = 0; $i -lt $Parts; $i++) {
            $piece = $Payload.Substring($i * $size, [math]::Min($size, $Payload.Length - $i * $size))
            $value = [ordered]@{ id = $GroupId; seq = "$($i + 1)"; b = $piece } | ConvertTo-Json -Compress
            ,@(@{ Name = 'additionalDetails'; Value = $value }, @{ Name = 'extendedAuditEventCategory'; Value = 'User' })
        }
    }

    # user4 loses Copilot and keeps E3. The AssignedPlan list is padded the way
    # a real one is (every service plan of every SKU), which is what forces chunking.
    $plans = 1..60 | ForEach-Object { [ordered]@{ SubscribedPlanId = [guid]::NewGuid().ToString(); ServiceInstance = "Plan$_/NA001"; CapabilityStatus = 0 } }
    $change = @(
        [ordered]@{ Name = 'AssignedLicense'
                    OldValue = @('[SkuName=Microsoft_365_Copilot, AccountId=t1, SkuId=639dec6b, DisabledPlans=[]]',
                                 '[SkuName=ENTERPRISEPACK, AccountId=t1, SkuId=6fd2c87f, DisabledPlans=[]]')
                    NewValue = @('[SkuName=ENTERPRISEPACK, AccountId=t1, SkuId=6fd2c87f, DisabledPlans=[]]') }
        [ordered]@{ Name = 'AssignedPlan'; OldValue = $plans; NewValue = @($plans | Select-Object -First 40) }
    )
    $payload = [ordered]@{
        targetUpdatedProperties = ($change | ConvertTo-Json -Depth 10 -Compress)
        additionalDetails       = '{"User-Agent":"MCAPICommercialProductLicensing"}'
    } | ConvertTo-Json -Compress

    # Record ids deliberately out of seq order: reassembly must follow seq.
    $licChunks = @(& $chunkify $payload 'grp-lic' 3)
    $updChunks = @(& $chunkify $payload 'grp-upd' 2)
    $fixtures = @(
        @{ Id = 'evt-0102'; Op = 'Change user license.'; Obj = 'user4@contoso.com'; Corr = 'corr-f'; Mod = @(); Ext = $licChunks[0] }
        @{ Id = 'evt-0101'; Op = 'Change user license.'; Obj = 'user4@contoso.com'; Corr = 'corr-f'; Mod = @(); Ext = $licChunks[1] }
        @{ Id = 'evt-0103'; Op = 'Change user license.'; Obj = 'user4@contoso.com'; Corr = 'corr-f'; Mod = @(); Ext = $licChunks[2] }
        @{ Id = 'evt-0104'; Op = 'Update user.';         Obj = 'user4@contoso.com'; Corr = 'corr-f'; Mod = @(); Ext = $updChunks[0] }
        @{ Id = 'evt-0105'; Op = 'Update user.';         Obj = 'user4@contoso.com'; Corr = 'corr-f'; Mod = @(); Ext = $updChunks[1] }
        @{ Id = 'evt-0106'; Op = 'Update user.';         Obj = 'user4@contoso.com'; Corr = 'corr-g'
           Mod = @(@{ Name = 'StrongAuthenticationPhoneAppDetail'; OldValue = '[]'; NewValue = '[{"DeviceName":"Phone"}]' })
           Ext = @(@{ Name = 'additionalDetails'; Value = '{"UserType":"Member"}' }) }
        @{ Id = 'evt-0107'; Op = 'Change user license.'; Obj = 'user6@contoso.com'; Corr = 'corr-h'
           Mod = @(@{ Name = 'MethodExecutionResult.'; OldValue = ''; NewValue = 'Microsoft.Online.Provisioning.SubscriptionManagement.InvalidUserServicePlanConfigurationException' })
           Ext = @(@{ Name = 'additionalDetails'; Value = '{}' }) }
        # seq 2 of 3 never arrives - the rest of the change is outside the loaded range
        @{ Id = 'evt-0108'; Op = 'Change user license.'; Obj = 'user6@contoso.com'; Corr = 'corr-i'; Mod = @(); Ext = (@(& $chunkify $payload 'grp-part' 3))[0] }
        @{ Id = 'evt-0109'; Op = 'Change user license.'; Obj = 'user6@contoso.com'; Corr = 'corr-i'; Mod = @(); Ext = (@(& $chunkify $payload 'grp-part' 3))[2] }
        @{ Id = 'evt-0110'; Op = 'Add user sponsor.';    Obj = 'user5@contoso.com'; Corr = 'corr-j'; Mod = @(); Ext = @() }
    )
    foreach ($f in $fixtures) {
        & $insertDoc $f.Id $f.Op (& $mkDoc $f.Id $f.Op 'admin@contoso.com' $f.Obj $f.Corr $f.Mod $f.Ext)
    }

    # Incremental, not a rebuild: new records on top of section 9's.
    Invoke-SqlText -Sql 'EXEC audit.usp_ShredLicenseEvents' -ShowMessages -TimeoutSeconds 600

    $ck = Invoke-SqlText -As Table -Sql @"
SELECT (SELECT COUNT(*) FROM audit.LicenseEvent)                                   AS Events,
       (SELECT COUNT(*) FROM audit.LicenseEvent WHERE ChunkGroupId IS NOT NULL)    AS ChunkRecords,
       (SELECT ChunkSeq FROM audit.LicenseEvent WHERE RecordId = 'evt-0101')        AS Seq0101,
       (SELECT COUNT(*) FROM audit.LicenseEventProperty WHERE Source = 'chunks')   AS ChunkProps,
       (SELECT COUNT(*) FROM audit.LicenseEventProperty
         WHERE Source = 'chunks' AND RecordId IN ('evt-0108', 'evt-0109'))         AS PartialProps;
"@
    $c = $ck.Rows[0]
    Assert-That ([int] $c.Events -eq 15) "all ten new records shredded ($($c.Events) events)"
    Assert-That ([int] $c.ChunkRecords -eq 7) "chunk membership recorded on every fragment's record ($($c.ChunkRecords) of 7)"
    Assert-That ([int] $c.Seq0101 -eq 2) "seq read from the fragment, not from record order (evt-0101 is seq $($c.Seq0101))"
    Assert-That ([int] $c.ChunkProps -eq 4) "both complete groups reassembled into their two properties each ($($c.ChunkProps) of 4)"
    Assert-That ([int] $c.PartialProps -eq 0) 'a group missing a fragment is not parsed from a partial document'

    $u4 = Invoke-SqlText -As Table -Sql @"
SELECT RecordId, Operation, PropertyName, FromChunks, OldValue, NewValue
FROM audit.vLicenseChange WHERE TargetUpn = 'user4@contoso.com' ORDER BY PropertyName;
"@
    $u4Ids = @($u4.Rows | ForEach-Object { "$($_.RecordId)" } | Select-Object -Unique)
    Assert-That ($u4Ids.Count -eq 1 -and $u4Ids[0] -eq 'evt-0102') `
        "the chunked change is reported once, on its seq 1 record, not per fragment or again as 'Update user' ($($u4Ids -join ', '))"
    Assert-That ($u4.Rows.Count -eq 2 -and ($u4.Rows | Where-Object PropertyName -eq 'AssignedLicense')) `
        "every licence property is reported, not just the first ($($u4.Rows.Count) rows)"
    Assert-That (@($u4.Rows | Where-Object { [int] $_.FromChunks -eq 1 }).Count -eq 2) 'and marked as reassembled from chunks'
    Assert-That ("$(($u4.Rows | Where-Object PropertyName -eq 'AssignedLicense').OldValue)" -like '*Microsoft_365_Copilot*') `
        'the old licence list from the reassembled document is readable'

    $sku = Invoke-SqlText -As Table -Sql "SELECT Sku, Change FROM audit.vLicenseSkuChange WHERE TargetUpn = 'user4@contoso.com';"
    Assert-That ($sku.Rows.Count -eq 1 -and "$($sku.Rows[0].Sku)" -eq 'Microsoft_365_Copilot' -and "$($sku.Rows[0].Change)" -eq 'Removed') `
        "vLicenseSkuChange reads the licence lists as 'Copilot removed', with the retained SKU unchanged ($(@($sku.Rows | ForEach-Object { "$($_.Sku) $($_.Change)" }) -join '; '))"

    $noise = Invoke-SqlText -As Scalar -Sql "SELECT COUNT(*) FROM audit.vLicenseChange WHERE RecordId IN ('evt-0106', 'evt-0107', 'evt-0108', 'evt-0109');"
    Assert-That ([int] $noise -eq 0) "an Authenticator update, a failed assignment and an incomplete group are not licence changes ($noise row(s))"

    $life4 = Invoke-SqlText -As Table -Sql @"
SELECT (SELECT LicenceChangeCount FROM audit.vLicenseLifecycle WHERE TargetUpn = 'user4@contoso.com') AS U4Changes,
       (SELECT COUNT(*) FROM audit.vLicenseLifecycle WHERE TargetUpn = 'user5@contoso.com' AND AccountCreatedUtc IS NOT NULL) AS U5Created;
"@
    Assert-That ([int] $life4.Rows[0].U4Changes -eq 1) "vLicenseLifecycle counts a chunked change once, not per fragment ($($life4.Rows[0].U4Changes))"
    Assert-That ([int] $life4.Rows[0].U5Created -eq 0) "'Add user sponsor' is not reported as the account being created"

    # Re-running must update in place, not accumulate duplicate properties -
    # including the reassembled ones, which live outside the per-batch replace.
    $before = Invoke-SqlText -As Table -Sql @"
SELECT (SELECT COUNT(*) FROM audit.LicenseEvent)         AS E,
       (SELECT COUNT(*) FROM audit.LicenseEventProperty) AS P;
"@
    Invoke-SqlText -Sql 'EXEC audit.usp_ShredLicenseEvents' -TimeoutSeconds 600
    $again = Invoke-SqlText -As Table -Sql @"
SELECT (SELECT COUNT(*) FROM audit.LicenseEvent)         AS E,
       (SELECT COUNT(*) FROM audit.LicenseEventProperty) AS P;
"@
    Assert-That ([int] $again.Rows[0].E -eq [int] $before.Rows[0].E -and [int] $again.Rows[0].P -eq [int] $before.Rows[0].P) `
        "a second shred updated in place rather than duplicating ($($again.Rows[0].E)/$($again.Rows[0].P), was $($before.Rows[0].E)/$($before.Rows[0].P))"

    # -----------------------------------------------------------------------
    Write-Host "`n10. Sessions and the Saturday-Friday weekly rollup (audit.usp_BuildCopilotSessions)" -ForegroundColor Cyan

    Invoke-SqlScript -Sql (Get-Content -LiteralPath (Join-Path $root 'sql' 'Create-CopilotSessionSchema.sql') -Raw)
    Invoke-SqlScript -Sql (Get-Content -LiteralPath (Join-Path $root 'sql' 'Create-CopilotSessionProcedure.sql') -Raw)

    # A fixture on the rules' exact edges, replacing section 6's rows. Times are
    # UTC; the reporting zone is Eastern, which is UTC-4 in September.
    Invoke-SqlText -Sql 'DELETE FROM audit.RawLogFlattened;'
    $rows = @(
        # user A, context docA: 09:00, 09:30, 10:29 local (59 min gap: same
        # session), 11:29 (exactly 60: new), 23:50 (new), then 00:10 the next
        # day - 20 minutes on, but a new calendar day: new
        @{ Id = 's-01'; U = 'a@contoso.com'; Utc = '2026-09-14T13:00:00'; App = 'Office';         Ctx = 'docA'; Loc = $null; Lic = 1 }
        @{ Id = 's-02'; U = 'a@contoso.com'; Utc = '2026-09-14T13:30:00'; App = 'Word';           Ctx = 'docA'; Loc = $null; Lic = 1 }
        @{ Id = 's-03'; U = 'a@contoso.com'; Utc = '2026-09-14T14:29:00'; App = 'Office';         Ctx = 'docA'; Loc = $null; Lic = 1 }
        @{ Id = 's-04'; U = 'a@contoso.com'; Utc = '2026-09-14T15:29:00'; App = 'Office';         Ctx = 'docA'; Loc = $null; Lic = 1 }
        @{ Id = 's-05'; U = 'a@contoso.com'; Utc = '2026-09-15T03:50:00'; App = 'Office';         Ctx = 'docA'; Loc = $null; Lic = 1 }
        @{ Id = 's-06'; U = 'a@contoso.com'; Utc = '2026-09-15T04:10:00'; App = 'Office';         Ctx = 'docA'; Loc = $null; Lic = 1 }
        # user A, context docB, inside the first docA session's span
        @{ Id = 's-07'; U = 'a@contoso.com'; Utc = '2026-09-14T13:10:00'; App = 'Office';         Ctx = 'docB'; Loc = $null; Lic = 1 }
        # user B on Saturday 19 Sep: no context, Location 'prd' - and one row with neither
        @{ Id = 's-08'; U = 'b@contoso.com'; Utc = '2026-09-19T14:00:00'; App = 'Copilot Studio'; Ctx = $null;  Loc = 'prd'; Lic = 0 }
        @{ Id = 's-09'; U = 'b@contoso.com'; Utc = '2026-09-19T14:05:00'; App = 'Copilot Studio'; Ctx = $null;  Loc = 'prd'; Lic = 0 }
        @{ Id = 's-10'; U = 'b@contoso.com'; Utc = '2026-09-19T14:20:00'; App = 'Copilot Studio'; Ctx = $null;  Loc = $null; Lic = 0 }
        # no user at all
        @{ Id = 's-11'; U = $null;           Utc = '2026-09-19T14:30:00'; App = 'Office';         Ctx = 'docC'; Loc = $null; Lic = $null }
    )
    foreach ($r in $rows) {
        Invoke-SqlText -Parameters @{ '@id' = $r.Id; '@u' = $r.U; '@utc' = [datetime] $r.Utc; '@app' = $r.App; '@ctx' = $r.Ctx; '@loc' = $r.Loc; '@lic' = $r.Lic } -Sql @"
INSERT INTO audit.RawLogFlattened (RecordId, [TimeStamp], UtcTimeStamp, [User], App, Location, [App context], IsCopilotLic, FlattenedUtc)
VALUES (@id, CONVERT(nvarchar(30), @utc, 126) + 'Z', @utc, @u, @app, @loc, @ctx, @lic, SYSUTCDATETIME());
"@
    }

    # The script's rule first: context, else Location, else skip.
    Invoke-SqlText -Sql "EXEC audit.usp_BuildCopilotSessions @ContextFallback = N'Location';" -ShowMessages
    $run = (Invoke-SqlText -As Table -Sql 'SELECT TOP (1) * FROM audit.CopilotSessionRun ORDER BY RunId DESC;').Rows[0]
    Assert-That ([int] $run.RowsRead -eq 11 -and [int] $run.RowsSkippedMissingContext -eq 1 -and [int] $run.RowsSkippedMissingUser -eq 1) `
        "Location rule: the row with no context and the row with no user are skipped and counted ($($run.RowsSkippedMissingContext)/$($run.RowsSkippedMissingUser))"
    Assert-That ([int] $run.RowsRecoveredFromLocation -eq 2) "and the two with only a Location took it as their context ($($run.RowsRecoveredFromLocation))"

    $s = Invoke-SqlText -As Table -Sql @"
SELECT AppContext, UserUPN, SessionOrdinal_Derived AS Ord, PromptCount, InferredSessionSpanMinutes AS Span,
       SessionType, PrimaryApp, AppCategory, IsM365CopilotLicensed AS Lic, CalendarDaysTouched_InSessionSpan AS Days,
       AvgMinutesPerPrompt_InferredSessionSpan AS AvgMin,
       CONVERT(varchar(16), SessionStart_PromptSubmittedDateTime, 120) AS StartLocal
FROM audit.CopilotSession ORDER BY UserUPN, AppContext, Ord;
"@
    $docA = @($s.Rows | Where-Object AppContext -eq 'docA')
    Assert-That ($s.Rows.Count -eq 6) "six sessions: four in docA, one in docB, one for user B ($($s.Rows.Count))"
    Assert-That ($docA.Count -eq 4 -and [int] $docA[0].PromptCount -eq 3) `
        "a 59-minute gap continues the session, exactly 60 starts a new one ($($docA.Count) docA sessions, first has $($docA[0].PromptCount) prompts)"
    Assert-That ("$($docA[0].StartLocal)" -eq '2026-09-14 09:00') "times are in the reporting time zone ($($docA[0].StartLocal))"
    Assert-That ([decimal] $docA[0].Span -eq 89 -and [decimal] $docA[0].AvgMin -eq 29.67 -and "$($docA[0].SessionType)" -eq 'Multi-Turn') `
        "span, minutes per prompt and type ($($docA[0].Span) min, $($docA[0].AvgMin), $($docA[0].SessionType))"
    Assert-That ("$($docA[3].StartLocal)" -eq '2026-09-15 00:10' -and "$($docA[3].SessionType)" -eq 'Single-Turn') `
        "20 minutes after 23:50 but on the next calendar day is a new session ($($docA[3].StartLocal))"
    Assert-That ("$($docA[0].PrimaryApp)" -eq 'Office' -and "$($docA[0].AppCategory)" -eq 'Copilot Chat' -and [int] $docA[0].Lic -eq 1) `
        "primary app is the first prompt's, and 'Office' maps to Copilot Chat ($($docA[0].PrimaryApp) / $($docA[0].AppCategory))"
    $bSess = @($s.Rows | Where-Object UserUPN -eq 'b@contoso.com')
    Assert-That ($bSess.Count -eq 1 -and "$($bSess[0].AppContext)" -eq 'prd' -and [int] $bSess[0].PromptCount -eq 2 -and "$($bSess[0].AppCategory)" -eq 'In-App Copilot') `
        "an unmapped app is In-App Copilot, keyed on its Location ($($bSess[0].AppContext), $($bSess[0].PromptCount) prompts)"

    $w = Invoke-SqlText -As Table -Sql @"
SELECT WeekEnding, WeekLabel, UserUPN, WeeklySessionCount AS S, WeeklyPromptCount AS P,
       ActiveDaysWithUsageInWeek AS D, DistinctAppContextsInWeek AS C, IsM365CopilotLicensed AS Lic
FROM audit.vCopilotWeeklyRollup ORDER BY WeekEnding, UserUPN;
"@
    $wa = @($w.Rows | Where-Object UserUPN -eq 'a@contoso.com')
    $wb = @($w.Rows | Where-Object UserUPN -eq 'b@contoso.com')
    Assert-That ($wa.Count -eq 1 -and "$($wa[0].WeekEnding)" -eq '2026-09-18' -and "$($wa[0].WeekLabel)" -eq '2026-W38') `
        "Monday and Tuesday roll up into the week ending Friday 18 Sep, 2026-W38 ($($wa[0].WeekEnding) $($wa[0].WeekLabel))"
    Assert-That ([int] $wa[0].S -eq 5 -and [int] $wa[0].P -eq 7 -and [int] $wa[0].D -eq 2 -and [int] $wa[0].C -eq 2) `
        "weekly sessions, prompts, active days and distinct contexts ($($wa[0].S)/$($wa[0].P)/$($wa[0].D)/$($wa[0].C))"
    Assert-That ("$($wa[0].Lic)" -eq 'TRUE') "the view writes the licence flag the way the CSV did ($($wa[0].Lic))"
    Assert-That ($wb.Count -eq 1 -and "$($wb[0].WeekEnding)" -eq '2026-09-25' -and "$($wb[0].WeekLabel)" -eq '2026-W39') `
        "a Saturday starts the next week, not the one ending the day before ($($wb[0].WeekEnding) $($wb[0].WeekLabel))"

    # The default: one session stream per user, nothing skipped for context.
    Invoke-SqlText -Sql 'EXEC audit.usp_BuildCopilotSessions;' -ShowMessages
    $run2 = (Invoke-SqlText -As Table -Sql 'SELECT TOP (1) * FROM audit.CopilotSessionRun ORDER BY RunId DESC;').Rows[0]
    Assert-That ("$($run2.ContextFallback)" -eq 'User' -and [int] $run2.RowsSkippedMissingContext -eq 0 -and [int] $run2.RowsSkippedMissingUser -eq 1) `
        "default: no row skipped for context, the user-less row still skipped ($($run2.ContextFallback): $($run2.RowsSkippedMissingContext)/$($run2.RowsSkippedMissingUser))"
    $s2 = Invoke-SqlText -As Table -Sql "SELECT UserUPN, PromptCount FROM audit.CopilotSession ORDER BY UserUPN, SessionOrdinal_Derived;"
    $a2 = @($s2.Rows | Where-Object UserUPN -eq 'a@contoso.com')
    $b2 = @($s2.Rows | Where-Object UserUPN -eq 'b@contoso.com')
    Assert-That ($a2.Count -eq 4 -and [int] $a2[0].PromptCount -eq 4) `
        "per user, docB's prompt joins the docA session it falls inside ($($a2.Count) sessions, first has $($a2[0].PromptCount) prompts)"
    Assert-That ($b2.Count -eq 1 -and [int] $b2[0].PromptCount -eq 3) "and the context-less row is kept ($($b2[0].PromptCount) prompts for user B)"

    # A rebuild replaces rather than appends.
    Invoke-SqlText -Sql 'EXEC audit.usp_BuildCopilotSessions;'
    $n = Invoke-SqlText -As Scalar -Sql 'SELECT COUNT(*) FROM audit.CopilotSession;'
    Assert-That ([int] $n -eq 5) "a rebuild replaces the tables rather than appending ($n sessions)"
    # Licensed at the time of use. User A is licensed today, but the licence
    # was assigned at 10:00 local on the 14th - after the first session began
    # (09:00) and before the next (11:29). Section 9 put the licence-event
    # schema in this same database, so it serves as @LicenceEventDatabase.
    Invoke-SqlText -Sql @"
INSERT INTO audit.LicenseEvent (RecordId, CreatedDateTime, ActivityDate, Operation, ActorUpn, TargetUpn, CorrelationId)
VALUES ('lic-a1', '2026-09-14T14:00:00', '2026-09-14', 'Change user license', 'admin@contoso.com', 'a@contoso.com', 'corr-a1');
INSERT INTO audit.LicenseEventProperty (RecordId, Ordinal, [Name], OldValue, NewValue, Source)
VALUES ('lic-a1', 0, 'AssignedLicense', '[]', '["[SkuName=Microsoft_365_Copilot, AccountId=t, SkuId=x, DisabledPlans=[]]"]', 'record');
"@
    Invoke-SqlText -Sql "EXEC audit.usp_BuildCopilotSessions @LicenceEventDatabase = N'$($script:ITDatabase)';" -ShowMessages
    $la = Invoke-SqlText -As Table -Sql "SELECT IsM365CopilotLicensed AS Lic FROM audit.CopilotSession WHERE UserUPN = 'a@contoso.com' ORDER BY SessionOrdinal_Derived;"
    $flags = @($la.Rows | ForEach-Object { [int] $_.Lic }) -join ''
    Assert-That ($flags -eq '0111') "the licence flag is the licence held when each session began, not today's ($flags)"
    $lw = Invoke-SqlText -As Table -Sql "SELECT IsM365CopilotLicensed AS Lic, WeeklySessionCount AS S FROM audit.CopilotWeeklyRollup WHERE UserUPN = 'a@contoso.com' ORDER BY IsM365CopilotLicensed;"
    Assert-That ($lw.Rows.Count -eq 2 -and [int] $lw.Rows[0].S -eq 1 -and [int] $lw.Rows[1].S -eq 3) `
        "the week splits into its unlicensed and licensed sessions ($(@($lw.Rows | ForEach-Object { "$($_.Lic):$($_.S)" }) -join ', '))"
    $lb = Invoke-SqlText -As Scalar -Sql "SELECT MAX(CAST(IsM365CopilotLicensed AS int)) FROM audit.CopilotSession WHERE UserUPN = 'b@contoso.com';"
    Assert-That ([int] $lb -eq 0) "a user with no licence history keeps today's flag ($lb)"
    $lr = Invoke-SqlText -As Scalar -Sql 'SELECT TOP (1) LicenceEventDatabase FROM audit.CopilotSessionRun ORDER BY RunId DESC;'
    Assert-That ("$lr" -eq $script:ITDatabase) "the run log records which licence history was used ($lr)"

    # --- Incremental builds (@SinceUtc) ---------------------------------------
    # A new prompt from user A at noon Eastern on the 15th, then a build from
    # the 15th only. It must equal a full rebuild over the same rows - sessions,
    # numbering and weeks - and the sessions before the 15th must keep their keys.
    $licArg = "@LicenceEventDatabase = N'$($script:ITDatabase)'"
    $sessionCols = 'AppContext, UserUPN, SessionOrdinal_Derived, SessionStart_PromptSubmittedDateTime, SessionEnd_PromptSubmittedDateTime, InferredSessionSpanMinutes, InferredSessionSpanHours, InferredSessionSpanDays, SessionTimeoutMinutes, CalendarDaysTouched_InSessionSpan, PromptCount, SessionType, PrimaryApp, AppCategory, IsM365CopilotLicensed, Division, Department, JobTitle, ManagerUPN, AvgMinutesPerPrompt_InferredSessionSpan, AvgMinutesPerPromptPerCalendarDay_InferredSpan, AvgMinutesPerPrompt_NormalizedByCalendarDays, MetricVersion'
    $keysBefore = Invoke-SqlText -As Scalar -Sql "SELECT STRING_AGG(CAST(SessionKey AS varchar(20)), ',') WITHIN GROUP (ORDER BY SessionKey) FROM audit.CopilotSession WHERE SessionStart_PromptSubmittedDateTime < '2026-09-15';"
    Invoke-SqlText -Sql @"
INSERT INTO audit.RawLogFlattened (RecordId, [TimeStamp], UtcTimeStamp, [User], App, IsCopilotLic, FlattenedUtc)
VALUES (N's-12', N'2026-09-15T16:00:00Z', '2026-09-15T16:00:00', N'a@contoso.com', N'Word', 1, SYSUTCDATETIME());
"@
    Invoke-SqlText -Sql "EXEC audit.usp_BuildCopilotSessions $licArg, @SinceUtc = '2026-09-15T12:00:00';" -ShowMessages
    $inc = (Invoke-SqlText -As Table -Sql 'SELECT TOP (1) CONVERT(varchar(19), RebuiltFrom, 120) AS RebuiltFrom, RowsRead, SessionsWritten FROM audit.CopilotSessionRun ORDER BY RunId DESC;').Rows[0]
    Assert-That ("$($inc.RebuiltFrom)" -eq '2026-09-15 00:00:00' -and [int] $inc.RowsRead -lt 12) `
        "an incremental build starts at local midnight of @SinceUtc's day and reads only from there ($($inc.RebuiltFrom), $($inc.RowsRead) rows)"
    $keysAfter = Invoke-SqlText -As Scalar -Sql "SELECT STRING_AGG(CAST(SessionKey AS varchar(20)), ',') WITHIN GROUP (ORDER BY SessionKey) FROM audit.CopilotSession WHERE SessionStart_PromptSubmittedDateTime < '2026-09-15';"
    Assert-That ("$keysAfter" -eq "$keysBefore") "the sessions before that day are kept, keys and all ($keysAfter)"
    $aOrd = Invoke-SqlText -As Scalar -Sql "SELECT STRING_AGG(CAST(SessionOrdinal_Derived AS varchar(5)), ',') WITHIN GROUP (ORDER BY SessionStart_PromptSubmittedDateTime) FROM audit.CopilotSession WHERE UserUPN = 'a@contoso.com';"
    Assert-That ("$aOrd" -eq '1,2,3,4,5') "numbering carries on from the last kept session ($aOrd)"

    Invoke-SqlText -Sql "DROP TABLE IF EXISTS dbo.IncSessions; DROP TABLE IF EXISTS dbo.IncWeeks; SELECT $sessionCols INTO dbo.IncSessions FROM audit.CopilotSession; SELECT * INTO dbo.IncWeeks FROM audit.CopilotWeeklyRollup;"
    Invoke-SqlText -Sql "EXEC audit.usp_BuildCopilotSessions $licArg;"
    $diff = (Invoke-SqlText -As Table -Sql @"
SELECT (SELECT COUNT(*) FROM (SELECT $sessionCols FROM dbo.IncSessions EXCEPT SELECT $sessionCols FROM audit.CopilotSession) AS a) +
       (SELECT COUNT(*) FROM (SELECT $sessionCols FROM audit.CopilotSession EXCEPT SELECT $sessionCols FROM dbo.IncSessions) AS b) AS SessionDiff,
       (SELECT COUNT(*) FROM (SELECT * FROM dbo.IncWeeks EXCEPT SELECT * FROM audit.CopilotWeeklyRollup) AS c) +
       (SELECT COUNT(*) FROM (SELECT * FROM audit.CopilotWeeklyRollup EXCEPT SELECT * FROM dbo.IncWeeks) AS d) AS WeekDiff,
       (SELECT COUNT(*) FROM dbo.IncSessions) AS IncSessions, (SELECT COUNT(*) FROM audit.CopilotSession) AS FullSessions,
       (SELECT COUNT(*) FROM dbo.IncWeeks) AS IncWeeks, (SELECT COUNT(*) FROM audit.CopilotWeeklyRollup) AS FullWeeks;
"@).Rows[0]
    Assert-That ([int] $diff.SessionDiff -eq 0 -and [int] $diff.IncSessions -eq [int] $diff.FullSessions) `
        "the incremental sessions are identical to a full rebuild's, column for column ($($diff.IncSessions) sessions, $($diff.SessionDiff) differences)"
    Assert-That ([int] $diff.WeekDiff -eq 0 -and [int] $diff.IncWeeks -eq [int] $diff.FullWeeks) `
        "and so are the weeks, including the one that straddles the rebuilt day ($($diff.IncWeeks) weekly rows, $($diff.WeekDiff) differences)"

    $refusedInc = ''
    try { Invoke-SqlText -Sql "EXEC audit.usp_BuildCopilotSessions $licArg, @SessionTimeoutMinutes = 30, @SinceUtc = '2026-09-15';" }
    catch { $refusedInc = $_.Exception.GetBaseException().Message }
    Assert-That ($refusedInc -like '*different settings*') "an incremental build under different settings is refused ($refusedInc)"

    # Back to the fixture section 12 expects.
    Invoke-SqlText -Sql "DELETE FROM audit.RawLogFlattened WHERE RecordId = N's-12'; DROP TABLE dbo.IncSessions; DROP TABLE dbo.IncWeeks;"
    Invoke-SqlText -Sql "EXEC audit.usp_BuildCopilotSessions $licArg;"
}
finally { Stop-MockGraph $mock }

# ---------------------------------------------------------------------------
# 11. Incremental loads: the real engine into the load tables, then the merge
# ---------------------------------------------------------------------------
Write-Host "`n11. Incremental loads (audit.usp_MergeCopilotLoad)" -ForegroundColor Cyan

# A database of its own: the sections above leave audit.CopilotAudit holding
# directory events, and this needs a finalized Copilot backfill to append to.
$mainDatabase = $script:ITDatabase
$script:ITDatabase = "$mainDatabase" + '_Load'
$loadMock = Start-MockGraph @{ days = 8; first_day_count = 300; last_day_count = 600; fail_first_query_covering = '2000-01-01T00:00:00Z' }
try {
    Invoke-SqlText -Catalog 'master' -Sql @"
IF DB_ID(N'$($script:ITDatabase)') IS NOT NULL
BEGIN
    ALTER DATABASE [$($script:ITDatabase)] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
    DROP DATABASE [$($script:ITDatabase)];
END;
CREATE DATABASE [$($script:ITDatabase)];
"@
    Invoke-SqlText -Catalog 'master' -Sql "ALTER DATABASE [$($script:ITDatabase)] SET RECOVERY SIMPLE;"
    Invoke-SqlScript -Sql (Get-Content -LiteralPath (Join-Path $root 'sql' 'Create-CopilotAuditSchema.sql') -Raw)
    Invoke-SqlScript -Sql (Get-Content -LiteralPath (Join-Path $root 'sql' 'Create-CopilotLoadProcedure.sql') -Raw)
    $loadConn = New-ConnString $script:ITDatabase

    # Expected record ids per day, from the mock: 'yyyyMMdd-NNNNNN'.
    $allIds = Get-ExpectedIds $loadMock
    $idsIn = {
        param([datetime] $From, [datetime] $To)
        @($allIds | Where-Object {
            $day = [datetime]::ParseExact($_.Substring(0, 8), 'yyyyMMdd', [cultureinfo]::InvariantCulture)
            $day -ge $From.Date -and $day -lt $To.Date })
    }
    $d = { param([int] $day) [datetime]::new(2026, 8, $day, 0, 0, 0, [DateTimeKind]::Utc) }

    # --- The backfill: Aug 1 - 5, finalized --------------------------------
    $bStore = [CopilotAudit.SqlWindowStore]::new($loadConn, '[audit].[CopilotBackfillWindow]')
    $bSink  = [CopilotAudit.SqlBatchSink]::new($loadConn, '[audit].[CopilotAuditStage]')
    $bLog  = [System.Collections.Generic.List[string]]::new()
    $bRun = Invoke-EngineRun -Mock $loadMock -Options (New-Options $loadMock (& $d 1) (& $d 5)) -Store $bStore -Sink $bSink -Log $bLog -TimeoutSec 600
    Assert-That ($bRun.Task.Status -eq 'RanToCompletion') "backfill of Aug 1-5 completed ($($bRun.Task.Status))"
    Invoke-SqlText -Parameters @{ '@s' = (& $d 1); '@e' = (& $d 5) } -Sql @"
INSERT INTO audit.CopilotBackfillRange (Id, RangeStartUtc, RangeEndUtc, Operations, CompletedUtc)
VALUES (1, @s, @e, N'CopilotInteraction', SYSUTCDATETIME());
"@
    [void] (Invoke-SqlText -As Table -Sql (Get-Content -LiteralPath (Join-Path $root 'sql' 'Finalize-CopilotAudit.sql') -Raw))
    $afterBackfill = [long] (Invoke-SqlText -As Scalar -Sql 'SELECT COUNT_BIG(*) FROM audit.CopilotAudit')
    $expectBackfill = (& $idsIn (& $d 1) (& $d 5)).Count
    Assert-That ($afterBackfill -eq $expectBackfill) "the finalized backfill holds Aug 1-5 ($afterBackfill of $expectBackfill)"

    # --- Load 1: Aug 4 - 9, a day of lookback over the backfill --------------
    # What -Incremental does: record the load, run the engine into the load
    # tables, mark it downloaded, merge.
    $runLoad = {
        param([datetime] $From, [datetime] $To)
        $id = Invoke-SqlText -As Scalar -Parameters @{ '@s' = $From; '@e' = $To } -Sql @"
INSERT INTO audit.CopilotLoad (RangeStartUtc, RangeEndUtc, Operations) OUTPUT INSERTED.LoadId VALUES (@s, @e, N'CopilotInteraction');
"@
        $st = [CopilotAudit.SqlWindowStore]::new($loadConn, '[audit].[CopilotLoadWindow]')
        $sk = [CopilotAudit.SqlBatchSink]::new($loadConn, '[audit].[CopilotLoadStage]')
        $lg = [System.Collections.Generic.List[string]]::new()
        $run = Invoke-EngineRun -Mock $loadMock -Options (New-Options $loadMock $From $To) -Store $st -Sink $sk -Log $lg -TimeoutSec 600
        [void] (Invoke-SqlText -Sql 'UPDATE audit.CopilotLoad SET DownloadedUtc = SYSUTCDATETIME() WHERE LoadId = @id' -Parameters @{ '@id' = $id })
        $m = Invoke-SqlText -As Table -Sql 'EXEC audit.usp_MergeCopilotLoad;' -ShowMessages
        return [pscustomobject]@{ LoadId = [int] $id; Run = $run; Merge = $m.Rows[0] }
    }

    $l1 = & $runLoad (& $d 4) (& $d 9)
    $overlap = (& $idsIn (& $d 4) (& $d 5)).Count
    $newDays = (& $idsIn (& $d 5) (& $d 9)).Count
    Assert-That ($l1.Run.Task.Status -eq 'RanToCompletion') "load 1 (Aug 4-9) downloaded ($($l1.Run.Task.Status))"
    Assert-That ([long] $l1.Merge.RowsAdded -eq $newDays -and [long] $l1.Merge.RowsAlreadyLoaded -eq $overlap) `
        "the lookback day was recognised, not re-added: $($l1.Merge.RowsAdded) new (of $newDays), $($l1.Merge.RowsAlreadyLoaded) already loaded (of $overlap)"

    $state = Invoke-SqlText -As Table -Sql @"
SELECT (SELECT COUNT_BIG(*) FROM audit.CopilotAudit)                          AS Rows_,
       (SELECT COUNT(DISTINCT RecordId) FROM audit.CopilotAudit)             AS Distinct_,
       (SELECT COUNT_BIG(*) FROM audit.CopilotInteraction)                    AS Interactions,
       (SELECT COUNT_BIG(*) FROM audit.CopilotAudit WHERE LoadId = 1)         AS FromLoad1,
       (SELECT COUNT_BIG(*) FROM audit.CopilotLoadWindow)                     AS LoadWindows,
       (SELECT COUNT_BIG(*) FROM audit.CopilotLoadStage)                      AS LoadStage,
       (SELECT COUNT(*) FROM audit.CopilotInteraction WHERE AppHost IS NULL OR PromptCount <> 1) AS BadShred
"@
    $s = $state.Rows[0]
    Assert-That ([long] $s.Rows_ -eq $allIds.Count -and [long] $s.Distinct_ -eq $allIds.Count) `
        "after the merge every record of Aug 1-9 is present exactly once ($($s.Rows_) rows, $($s.Distinct_) distinct, $($allIds.Count) expected)"
    Assert-That ([long] $s.Interactions -eq [long] $s.Rows_) "audit.CopilotInteraction gained the same records ($($s.Interactions))"
    Assert-That ([int] $s.BadShred -eq 0) 'the merged records shredded like the finalized ones (AppHost set, one prompt each)'
    Assert-That ([long] $s.FromLoad1 -eq $newDays) "the appended rows carry their LoadId ($($s.FromLoad1))"
    Assert-That ([long] $s.LoadWindows -eq 0 -and [long] $s.LoadStage -eq 0) 'the load tables are emptied for the next load'

    # --- A second merge with nothing waiting changes nothing ----------------
    [void] (Invoke-SqlText -As Table -Sql 'EXEC audit.usp_MergeCopilotLoad;')
    $again = [long] (Invoke-SqlText -As Scalar -Sql 'SELECT COUNT_BIG(*) FROM audit.CopilotAudit')
    Assert-That ($again -eq $allIds.Count) "merging again with nothing waiting is a no-op ($again)"

    # --- Refusals -------------------------------------------------------------
    $refused = { param([string] $Sql) try { [void] (Invoke-SqlText -As Table -Sql $Sql); return '' } catch { return $_.Exception.GetBaseException().Message } }

    $msg = & $refused (Get-Content -LiteralPath (Join-Path $root 'sql' 'Finalize-CopilotAudit.sql') -Raw)
    Assert-That ($msg -like '*incremental loads have been merged*') "finalize refuses once loads are merged, rather than dropping them ($msg)"

    Invoke-SqlText -Parameters @{ '@s' = (& $d 8); '@e' = (& $d 9) } -Sql "INSERT INTO audit.CopilotLoad (RangeStartUtc, RangeEndUtc, Operations) VALUES (@s, @e, N'CopilotInteraction');"
    $msg = & $refused 'EXEC audit.usp_MergeCopilotLoad;'
    Assert-That ($msg -like '*has not finished downloading*') "a load still downloading is not merged ($msg)"
    Invoke-SqlText -Sql 'UPDATE audit.CopilotLoad SET DownloadedUtc = SYSUTCDATETIME() WHERE MergedUtc IS NULL;'
    $msg = & $refused 'EXEC audit.usp_MergeCopilotLoad;'
    Assert-That ($msg -like '*gap*') "a load whose windows do not cover its range is not merged ($msg)"
    Invoke-SqlText -Sql 'DELETE FROM audit.CopilotLoad WHERE MergedUtc IS NULL;'

    # --- A record Purview delivered late -------------------------------------
    # Take one Aug 8 record out, as if it had not reached the audit log when
    # load 1 ran. The next load's lookback reaches back over Aug 8 and must add
    # exactly that record.
    $late = Invoke-SqlText -As Scalar -Sql "SELECT TOP (1) RecordId FROM audit.CopilotAudit WHERE RecordId LIKE '20260808-%' ORDER BY RecordId;"
    Invoke-SqlText -Parameters @{ '@r' = $late } -Sql 'DELETE FROM audit.CopilotAudit WHERE RecordId = @r; DELETE FROM audit.CopilotInteraction WHERE RecordId = @r;'
    $l2 = & $runLoad (& $d 7) (& $d 9)
    Assert-That ([long] $l2.Merge.RowsAdded -eq 1) "the lookback picked up the late record and nothing else ($($l2.Merge.RowsAdded) added, $($l2.Merge.RowsAlreadyLoaded) already loaded)"
    $back = [long] (Invoke-SqlText -As Scalar -Parameters @{ '@r' = $late; '@l' = $l2.LoadId } -Sql 'SELECT COUNT_BIG(*) FROM audit.CopilotAudit WHERE RecordId = @r AND LoadId = @l;')
    $final = [long] (Invoke-SqlText -As Scalar -Sql 'SELECT COUNT_BIG(*) FROM audit.CopilotAudit')
    Assert-That ($back -eq 1 -and $final -eq $allIds.Count) "the late record is back, once, attributed to its load (total $final of $($allIds.Count))"
}
finally {
    Stop-MockGraph $loadMock
    $script:ITDatabase = $mainDatabase
}

# ---------------------------------------------------------------------------
# 12. The bi star schema, over what sections 8-10 left in the main database:
# section 10's session rows in audit.RawLogFlattened and the sessions built
# from them, sections 9 and 10's licence events, section 8's enrichment.
Write-Host "`n12. The bi star schema (bi.usp_RefreshBi)" -ForegroundColor Cyan

Invoke-SqlScript -Sql (Get-Content -LiteralPath (Join-Path $root 'sql' 'Create-CopilotBiSchema.sql') -Raw)
Invoke-SqlScript -Sql (Get-Content -LiteralPath (Join-Path $root 'sql' 'Create-CopilotBiProcedure.sql') -Raw)
Invoke-SqlScript -Sql (Get-Content -LiteralPath (Join-Path $root 'sql' 'Create-CopilotBiSchema.sql') -Raw)   # re-runnable

# Upgrading a database whose bi schema predates AgentSource: the script is one
# batch, so a static reference to the new column would not even compile there.
Invoke-SqlText -Sql 'ALTER TABLE bi.DimAgent DROP COLUMN AgentSource;'
Invoke-SqlScript -Sql (Get-Content -LiteralPath (Join-Path $root 'sql' 'Create-CopilotBiSchema.sql') -Raw)
$upgraded = Invoke-SqlText -As Scalar -Sql "SELECT AgentSource FROM bi.DimAgent WHERE AgentId = N'(none)';"
Assert-That ("$upgraded" -eq '(no agent)') "the schema script upgrades an older bi.DimAgent in place ($upgraded)"

# User C has no licence history, only today's enrichment: licensed since
# 12:00Z on the 16th (08:00 Eastern). One interaction before, one after.
# User A's licence comes from the history section 10 added (14:00Z on the
# 14th), whatever the enrichment says.
Invoke-SqlText -Sql @"
DELETE FROM audit.CopilotLicense WHERE UserPrincipalName IN (N'a@contoso.com', N'b@contoso.com', N'c@contoso.com');
INSERT INTO audit.CopilotLicense (UserPrincipalName, IsCopilotLic, SkuPartNumber, LicensedSinceUtc, DirectoryStatus) VALUES
    (N'a@contoso.com', 1, N'Microsoft_365_Copilot', '2026-01-01T00:00:00', N'Found'),
    (N'b@contoso.com', 0, NULL, NULL, N'Found'),
    (N'c@contoso.com', 1, N'Microsoft_365_Copilot', '2026-09-16T12:00:00', N'Found');
DELETE FROM audit.UserDirectory WHERE UserPrincipalName = N'a@contoso.com';
INSERT INTO audit.UserDirectory (UserPrincipalName, FullName, Department, EmployeeType, IsManager, AccountEnabled,
                                 ManagerUPN, ManagerName, OrgDepth, Mgr1, Mgr2)
VALUES (N'a@contoso.com', N'User A', N'Finance', N'EMPLOYEE', 0, 1, N'boss@contoso.com', N'The Boss', 2, N'The CEO', N'The Boss');
INSERT INTO audit.RawLogFlattened (RecordId, [TimeStamp], UtcTimeStamp, [User], App, IsCopilotLic, FlattenedUtc) VALUES
    (N'c-01', N'2026-09-15T15:00:00Z', '2026-09-15T15:00:00', N'c@contoso.com', N'Teams', 1, SYSUTCDATETIME()),
    (N'c-02', N'2026-09-17T15:00:00Z', '2026-09-17T15:00:00', N'c@contoso.com', N'Teams', 1, SYSUTCDATETIME());
UPDATE audit.RawLogFlattened SET ModelProvider = N'OpenAI', ModelName = N'gpt-5' WHERE RecordId = N's-01';
UPDATE audit.RawLogFlattened SET AIPlugin = N'BingWebSearch' WHERE RecordId = N's-02';
UPDATE audit.RawLogFlattened SET JailbreakDetected = N'true', PromptInjectionDetected = N'false' WHERE RecordId = N's-03';
UPDATE audit.RawLogFlattened SET AgentId = N'agent-1', AgentName = N'Old name' WHERE RecordId = N's-08';
-- A Copilot Studio agent named only in AppIdentity, as on a live tenant; and
-- an AgentId, where there is one, wins over AppIdentity.
UPDATE audit.RawLogFlattened SET AppIdentity = N'Copilot.Studio.Default-7d53f522-72db-4957-aa26-2d1fe476b96f-crd20_expenseAgent' WHERE RecordId IN (N's-10', N's-09');
UPDATE audit.RawLogFlattened SET AppIdentity = N'MicrosoftAgent.OutlookDraftsChatSteering.BuiltIn_DraftsAgent' WHERE RecordId = N's-04';
UPDATE audit.RawLogFlattened SET AppIdentity = N'Copilot.M365Copilot.WebChat' WHERE RecordId = N's-01';
UPDATE audit.RawLogFlattened SET AgentId = N'agent-1', AgentName = N'New name' WHERE RecordId = N's-09';
"@

$biRefused = { param([string] $Sql) try { [void] (Invoke-SqlText -As Table -Sql $Sql); return '' } catch { return $_.Exception.GetBaseException().Message } }
$licDb = $script:ITDatabase

# A window on the first run is widened to everything: there is nothing before it yet.
Invoke-SqlText -Sql "EXEC bi.usp_RefreshBi @SinceUtc = '2026-09-19', @LicenceEventDatabase = N'$licDb';" -ShowMessages
$flatRows = [long] (Invoke-SqlText -As Scalar -Sql 'SELECT COUNT_BIG(*) FROM audit.RawLogFlattened;')
$fi = (Invoke-SqlText -As Table -Sql 'SELECT COUNT_BIG(*) AS Rows_, COUNT(DISTINCT RecordId) AS Ids FROM bi.FactInteraction;').Rows[0]
Assert-That ([long] $fi.Rows_ -eq $flatRows -and [long] $fi.Ids -eq $flatRows) `
    "the first refresh builds every interaction, once each, though a window was asked for ($($fi.Rows_) of $flatRows)"
$firstRun = (Invoke-SqlText -As Table -Sql 'SELECT TOP (1) SinceUtc FROM bi.RefreshRun ORDER BY RunId DESC;').Rows[0]
Assert-That ($firstRun.SinceUtc -is [DBNull]) 'and the run log records it as a full rebuild'

# Every key resolves: the model's relationships will have no blank rows.
$joined = [long] (Invoke-SqlText -As Scalar -Sql @"
SELECT COUNT_BIG(*) FROM bi.Interactions AS f
JOIN bi.Calendar AS d ON d.DateKey = f.DateKey JOIN bi.Users AS u ON u.UserKey = f.UserKey
JOIN bi.Apps AS a ON a.AppKey = f.AppKey JOIN bi.Agents AS g ON g.AgentKey = f.AgentKey
JOIN bi.Models AS m ON m.ModelKey = f.ModelKey JOIN bi.LicenceStatuses AS l ON l.LicenceStatusKey = f.LicenceStatusKey;
"@)
Assert-That ($joined -eq $flatRows) "every interaction finds its date, user, app, agent, model and licence status ($joined of $flatRows)"
$lineage = [int] (Invoke-SqlText -As Scalar -Sql "SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID(N'bi.Interactions') AND name IN (N'RecordId', N'CreatedUtc');")
Assert-That ($lineage -eq 0) 'the view the model imports leaves out the lineage columns'

$byUser = Invoke-SqlText -As Table -Sql @"
SELECT u.UserPrincipalName AS Upn,
       STRING_AGG(CAST(f.LicenceStatusKey AS varchar(1)), '') WITHIN GROUP (ORDER BY f.CreatedUtc) AS Statuses
FROM bi.FactInteraction AS f JOIN bi.DimUser AS u ON u.UserKey = f.UserKey
GROUP BY u.UserPrincipalName;
"@
$st = @{}; foreach ($r in $byUser.Rows) { $st["$($r.Upn)"] = "$($r.Statuses)" }
Assert-That ($st['a@contoso.com'] -eq '2221111') `
    "licence at the time of use from the history: not licensed before 14:00Z on the 14th, licensed after ($($st['a@contoso.com']))"
Assert-That ($st['b@contoso.com'] -eq '222') "no history, not licensed today: not licensed throughout ($($st['b@contoso.com']))"
Assert-That ($st['c@contoso.com'] -eq '21') "no history, licensed today: licensed from LicensedSinceUtc only ($($st['c@contoso.com']))"
Assert-That ($st['(unknown user)'] -eq '0') "an interaction with no user is kept, under '(unknown user)', licence Unknown ($($st['(unknown user)']))"

$t = Invoke-SqlText -As Table -Sql @"
SELECT RecordId, DateKey, HourOfDay, CONVERT(varchar(16), ActivityHour, 120) AS Hr, UsedWebSearch, JailbreakDetected
FROM bi.FactInteraction WHERE RecordId IN (N's-02', N's-03', N's-05', N's-06') ORDER BY RecordId;
"@
$tr = @{}; foreach ($r in $t.Rows) { $tr["$($r.RecordId)"] = $r }
Assert-That ("$($tr['s-02'].Hr)" -eq '2026-09-14 09:00') "ActivityHour is local time truncated to the hour ($($tr['s-02'].Hr))"
Assert-That ([int] $tr['s-05'].DateKey -eq 20260914 -and [int] $tr['s-05'].HourOfDay -eq 23 -and [int] $tr['s-06'].DateKey -eq 20260915 -and [int] $tr['s-06'].HourOfDay -eq 0) `
    "dates are local: 03:50Z is 23:50 on the 14th, 04:10Z is 00:10 on the 15th ($($tr['s-05'].DateKey) $($tr['s-05'].HourOfDay)h, $($tr['s-06'].DateKey) $($tr['s-06'].HourOfDay)h)"
$flags = (Invoke-SqlText -As Table -Sql 'SELECT SUM(CAST(UsedWebSearch AS int)) AS Web, SUM(CAST(JailbreakDetected AS int)) AS Jb FROM bi.FactInteraction;').Rows[0]
Assert-That ([int] $tr['s-02'].UsedWebSearch -eq 1 -and [int] $flags.Web -eq 1 -and [int] $tr['s-03'].JailbreakDetected -eq 1 -and [int] $flags.Jb -eq 1) `
    "web grounding and jailbreak flags on exactly the rows that carry them ($($flags.Web), $($flags.Jb))"

$dims = (Invoke-SqlText -As Table -Sql @"
SELECT (SELECT AppCategory FROM bi.DimApp WHERE App = N'Office') AS OfficeCat,
       (SELECT AppCategory FROM bi.DimApp WHERE App = N'Word') AS WordCat,
       (SELECT AgentName FROM bi.DimAgent WHERE AgentId = N'agent-1') AS AgentName,
       (SELECT CONCAT(g.AgentName, '/', g.AgentSource) FROM bi.FactInteraction AS f JOIN bi.DimAgent AS g ON g.AgentKey = f.AgentKey WHERE f.RecordId = N's-10') AS StudioAgent,
       (SELECT CONCAT(g.AgentName, '/', g.AgentSource) FROM bi.FactInteraction AS f JOIN bi.DimAgent AS g ON g.AgentKey = f.AgentKey WHERE f.RecordId = N's-04') AS MsAgent,
       (SELECT CONCAT(g.AgentId, '/', g.AgentSource) FROM bi.FactInteraction AS f JOIN bi.DimAgent AS g ON g.AgentKey = f.AgentKey WHERE f.RecordId = N's-09') AS S09Agent,
       (SELECT COUNT(*) FROM bi.FactInteraction AS f JOIN bi.DimAgent AS g ON g.AgentKey = f.AgentKey WHERE g.AgentId = N'(none)') AS NoAgent,
       (SELECT m.ModelName FROM bi.FactInteraction AS f JOIN bi.DimModel AS m ON m.ModelKey = f.ModelKey WHERE f.RecordId = N's-01') AS S01Model,
       (SELECT Department FROM bi.DimUser WHERE UserPrincipalName = N'a@contoso.com') AS ADept,
       (SELECT CONCAT(OrgDepth, ':', OrgLevel1, '>', OrgLevel2, '|', ManagerName, '|', EmployeeType, '|', DirectoryStatus)
        FROM bi.DimUser WHERE UserPrincipalName = N'a@contoso.com') AS AOrg,
       (SELECT CopilotLicenceToday FROM bi.DimUser WHERE UserPrincipalName = N'b@contoso.com') AS BToday,
       (SELECT DirectoryStatus FROM bi.DimUser WHERE UserPrincipalName = N'(unknown user)') AS UnknownDir;
"@).Rows[0]
Assert-That ("$($dims.OfficeCat)" -eq 'Copilot Chat' -and "$($dims.WordCat)" -eq 'In-App Copilot') `
    "apps carry the sessions' category mapping ($($dims.OfficeCat), $($dims.WordCat))"
Assert-That ("$($dims.AgentName)" -eq 'New name' -and [int] $dims.NoAgent -eq ($flatRows - 4)) `
    "an agent is named by its latest name, and interactions without one - a non-agent AppIdentity included - map to '(no agent)' ($($dims.AgentName), $($dims.NoAgent))"
Assert-That ("$($dims.StudioAgent)" -eq 'crd20_expenseAgent/Copilot Studio' -and "$($dims.MsAgent)" -eq 'BuiltIn_DraftsAgent/Microsoft') `
    "with no AgentId, the agent comes from AppIdentity: Copilot Studio and Microsoft agents ($($dims.StudioAgent), $($dims.MsAgent))"
Assert-That ("$($dims.S09Agent)" -eq 'agent-1/Agent ID') "an AgentId wins over AppIdentity ($($dims.S09Agent))"
Assert-That ("$($dims.S01Model)" -eq 'gpt-5') "the model dimension carries provider and name ($($dims.S01Model))"
Assert-That ("$($dims.ADept)" -eq 'Finance' -and "$($dims.BToday)" -eq 'Not licensed' -and "$($dims.UnknownDir)" -eq 'Not looked up') `
    "users carry the AD attributes and today's licence ($($dims.ADept), $($dims.BToday), $($dims.UnknownDir))"
Assert-That ("$($dims.AOrg)" -eq '2:The CEO>The Boss|The Boss|EMPLOYEE|Found') "the org chain from AD becomes OrgLevel1..n ($($dims.AOrg))"

$cal = Invoke-SqlText -As Table -Sql @"
SELECT DateKey, DayOfWeekName, CONVERT(varchar(10), WeekEnding, 120) AS WeekEnding, WeekLabel
FROM bi.DimDate WHERE DateKey IN (20260914, 20260918, 20260919) ORDER BY DateKey;
"@
$c = @($cal.Rows)
Assert-That ($c.Count -eq 3 -and "$($c[0].DayOfWeekName)" -eq 'Monday' -and "$($c[1].WeekEnding)" -eq '2026-09-18' -and "$($c[1].WeekLabel)" -eq '2026-W38' -and "$($c[2].WeekEnding)" -eq '2026-09-25' -and "$($c[2].WeekLabel)" -eq '2026-W39') `
    "the calendar carries the rollup's Saturday-Friday week: Friday the 18th ends W38, Saturday the 19th starts W39"
$calSpan = (Invoke-SqlText -As Table -Sql 'SELECT MIN(DateKey) AS F, COUNT(*) AS N, COUNT(DISTINCT [Date]) AS D FROM bi.DimDate;').Rows[0]
Assert-That ([int] $calSpan.F -eq 20260101 -and [int] $calSpan.N -eq [int] $calSpan.D) "the calendar starts on 1 January of the first year with data, one row per date ($($calSpan.F))"

$sessCount = [int] (Invoke-SqlText -As Scalar -Sql 'SELECT COUNT(*) FROM audit.CopilotSession;')
$fs = (Invoke-SqlText -As Table -Sql @"
SELECT COUNT(*) AS N,
       (SELECT STRING_AGG(CAST(f.LicenceStatusKey AS varchar(1)), '') WITHIN GROUP (ORDER BY f.SessionStart)
        FROM bi.FactSession AS f JOIN bi.DimUser AS u ON u.UserKey = f.UserKey WHERE u.UserPrincipalName = N'a@contoso.com') AS AStatuses
FROM bi.FactSession;
"@).Rows[0]
Assert-That ([int] $fs.N -eq $sessCount -and "$($fs.AStatuses)" -eq '2111') `
    "sessions come across with their licence at the time ($($fs.N) of $sessCount, user A $($fs.AStatuses))"

$skuChanges = [int] (Invoke-SqlText -As Scalar -Sql 'SELECT COUNT(*) FROM (SELECT DISTINCT RecordId, Sku, Change FROM audit.vLicenseSkuChange WHERE TargetUpn IS NOT NULL) AS x;')
$fc = (Invoke-SqlText -As Table -Sql @"
SELECT COUNT(*) AS N,
       (SELECT CONCAT(f.DateKey, ':', f.Change, ':', k.Sku) FROM bi.FactLicenceChange AS f
        JOIN bi.DimSku AS k ON k.SkuKey = f.SkuKey WHERE f.RecordId = 'lic-a1') AS A
FROM bi.FactLicenceChange;
"@).Rows[0]
Assert-That ([int] $fc.N -eq $skuChanges -and "$($fc.A)" -eq '20260914:1:Microsoft_365_Copilot') `
    "every SKU change is a row, +1 or -1 ($($fc.N) of $skuChanges; user A $($fc.A))"

$ld = Invoke-SqlText -As Table -Sql @"
SELECT u.UserPrincipalName AS Upn, STRING_AGG(CAST(l.DateKey AS varchar(8)), ',') WITHIN GROUP (ORDER BY l.DateKey) AS Days
FROM bi.FactLicensedDay AS l JOIN bi.DimUser AS u ON u.UserKey = l.UserKey
WHERE l.DateKey BETWEEN 20260913 AND 20260917 AND u.UserPrincipalName IN (N'a@contoso.com', N'b@contoso.com', N'c@contoso.com')
GROUP BY u.UserPrincipalName;
"@
$ldd = @{}; foreach ($r in $ld.Rows) { $ldd["$($r.Upn)"] = "$($r.Days)" }
Assert-That ($ldd['a@contoso.com'] -eq '20260914,20260915,20260916,20260917') "user A counts as licensed from the day the licence was assigned ($($ldd['a@contoso.com']))"
Assert-That ($ldd['c@contoso.com'] -eq '20260916,20260917') "user C from the day of LicensedSinceUtc ($($ldd['c@contoso.com']))"
Assert-That (-not $ldd.ContainsKey('b@contoso.com')) 'user B, unlicensed, never counts'

# --- A partial refresh replaces its window and nothing before it ------------
$aKey = [int] (Invoke-SqlText -As Scalar -Sql "SELECT UserKey FROM bi.DimUser WHERE UserPrincipalName = N'a@contoso.com';")
$usersBefore = [int] (Invoke-SqlText -As Scalar -Sql 'SELECT COUNT(*) FROM bi.DimUser;')
Invoke-SqlText -Sql @"
UPDATE audit.RawLogFlattened SET App = N'Excel' WHERE RecordId = N's-01';   -- before the window: must not change
UPDATE audit.RawLogFlattened SET App = N'Outlook' WHERE RecordId = N's-09'; -- inside it: must change
INSERT INTO audit.RawLogFlattened (RecordId, [TimeStamp], UtcTimeStamp, [User], App, FlattenedUtc)
VALUES (N'n-01', N'2026-09-20T12:00:00Z', '2026-09-20T12:00:00', N'a@contoso.com', N'Word', SYSUTCDATETIME());
"@
Invoke-SqlText -Sql "EXEC bi.usp_RefreshBi @SinceUtc = '2026-09-19', @LicenceEventDatabase = N'$licDb';" -ShowMessages
$p = (Invoke-SqlText -As Table -Sql @"
SELECT (SELECT COUNT_BIG(*) FROM bi.FactInteraction) AS Rows_,
       (SELECT COUNT(DISTINCT RecordId) FROM bi.FactInteraction) AS Ids,
       (SELECT a.App FROM bi.FactInteraction AS f JOIN bi.DimApp AS a ON a.AppKey = f.AppKey WHERE f.RecordId = N's-01') AS S01,
       (SELECT a.App FROM bi.FactInteraction AS f JOIN bi.DimApp AS a ON a.AppKey = f.AppKey WHERE f.RecordId = N's-09') AS S09,
       (SELECT UserKey FROM bi.FactInteraction WHERE RecordId = N'n-01') AS N01User,
       (SELECT TOP (1) CONVERT(varchar(10), SinceUtc, 120) FROM bi.RefreshRun ORDER BY RunId DESC) AS Since;
"@).Rows[0]
Assert-That ([long] $p.Rows_ -eq ($flatRows + 1) -and [long] $p.Ids -eq ($flatRows + 1)) "a partial refresh adds the new interaction and duplicates nothing ($($p.Rows_))"
Assert-That ("$($p.S09)" -eq 'Outlook' -and "$($p.S01)" -eq 'Office') "it rewrites rows inside its window and leaves the ones before it ($($p.S09), $($p.S01))"
Assert-That ([int] $p.N01User -eq $aKey -and "$($p.Since)" -eq '2026-09-19') "keys are stable across refreshes (user A is still key $aKey)"

# --- Sessions: only what the session builds changed ---------------------------
# An incremental session build picks up n-01; the next refresh copies just the
# days it rebuilt. After a full session build (every key renumbered) it copies
# them all.
$sessSync = {
    (Invoke-SqlText -As Table -Sql @"
SELECT (SELECT COUNT(*) FROM (SELECT SessionKey, SessionStart_PromptSubmittedDateTime, PromptCount FROM audit.CopilotSession
                              EXCEPT SELECT SessionKey, SessionStart, PromptCount FROM bi.FactSession) AS a) AS Missing,
       (SELECT COUNT(*) FROM (SELECT SessionKey, SessionStart, PromptCount FROM bi.FactSession
                              EXCEPT SELECT SessionKey, SessionStart_PromptSubmittedDateTime, PromptCount FROM audit.CopilotSession) AS b) AS Extra,
       (SELECT TOP (1) SessionRows FROM bi.RefreshRun ORDER BY RunId DESC) AS Written,
       (SELECT COUNT(*) FROM audit.CopilotSession) AS Total;
"@).Rows[0]
}
Invoke-SqlText -Sql "EXEC audit.usp_BuildCopilotSessions @LicenceEventDatabase = N'$licDb', @SinceUtc = '2026-09-19';" -ShowMessages
Invoke-SqlText -Sql "EXEC bi.usp_RefreshBi @SinceUtc = '2026-09-19', @LicenceEventDatabase = N'$licDb';" -ShowMessages
$ss = & $sessSync
Assert-That ([int] $ss.Missing -eq 0 -and [int] $ss.Extra -eq 0 -and [int] $ss.Written -lt [int] $ss.Total) `
    "after an incremental session build, bi.FactSession matches by copying only the rebuilt days ($($ss.Written) of $($ss.Total) written)"
Invoke-SqlText -Sql "EXEC audit.usp_BuildCopilotSessions @LicenceEventDatabase = N'$licDb';"
Invoke-SqlText -Sql "EXEC bi.usp_RefreshBi @SinceUtc = '2026-09-19', @LicenceEventDatabase = N'$licDb';"
$ss = & $sessSync
Assert-That ([int] $ss.Missing -eq 0 -and [int] $ss.Extra -eq 0 -and [int] $ss.Written -eq [int] $ss.Total) `
    "after a full session build renumbers every key, the next refresh copies them all ($($ss.Written) of $($ss.Total))"
Invoke-SqlText -Sql "EXEC bi.usp_RefreshBi @SinceUtc = '2026-09-19', @LicenceEventDatabase = N'$licDb';"
$ss = & $sessSync
Assert-That ([int] $ss.Written -eq 0 -and [int] $ss.Missing -eq 0) "with no session build since, a refresh copies no sessions ($($ss.Written))"

$msg = & $biRefused "EXEC bi.usp_RefreshBi @SinceUtc = '2026-09-19', @ReportingTimeZone = N'UTC';"
Assert-That ($msg -like '*needs a full rebuild*') "a partial refresh in a different time zone is refused ($msg)"
$msg = & $biRefused "EXEC bi.usp_RefreshBi @LicenceEventDatabase = N'NoSuchDatabase';"
Assert-That ($msg -like '*no audit.vLicenseSkuChange*') "a licence database without the licence views is refused ($msg)"

# --- A full rebuild picks up everything, keys unchanged ---------------------
Invoke-SqlText -Sql "EXEC bi.usp_RefreshBi @LicenceEventDatabase = N'$licDb';" -ShowMessages
$r = (Invoke-SqlText -As Table -Sql @"
SELECT (SELECT COUNT_BIG(*) FROM bi.FactInteraction) AS Rows_,
       (SELECT a.App FROM bi.FactInteraction AS f JOIN bi.DimApp AS a ON a.AppKey = f.AppKey WHERE f.RecordId = N's-01') AS S01,
       (SELECT COUNT(*) FROM bi.DimUser) AS Users,
       (SELECT UserKey FROM bi.DimUser WHERE UserPrincipalName = N'a@contoso.com') AS AKey;
"@).Rows[0]
Assert-That ([long] $r.Rows_ -eq ($flatRows + 1) -and "$($r.S01)" -eq 'Excel') "a full rebuild rewrites every row ($($r.Rows_), s-01 now $($r.S01))"
Assert-That ([int] $r.Users -eq $usersBefore -and [int] $r.AKey -eq $aKey) "and re-registers no user: keys survive a rebuild ($($r.Users) users)"

# ---------------------------------------------------------------------------
# 13. CSV import: section 11's records, exported in the Purview portal's
# shape as overlapping files, imported by tools/Import-CopilotAuditCsv.ps1
# into a new database and into section 11's own.
# ---------------------------------------------------------------------------
Write-Host "`n13. CSV import (tools/Import-CopilotAuditCsv.ps1)" -ForegroundColor Cyan

$csvTool  = Join-Path $root 'tools' 'Import-CopilotAuditCsv.ps1'
$loadDb   = "$($script:ITDatabase)_Load"
$csvDb    = "$($script:ITDatabase)_Csv"
$guardDb  = "$($script:ITDatabase)_CsvGuard"
$csvDir   = Join-Path ([System.IO.Path]::GetTempPath()) ('csvit-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $csvDir | Out-Null
$newDb = {
    param([string] $Name)
    Invoke-SqlText -Catalog 'master' -Sql @"
IF DB_ID(N'$Name') IS NOT NULL BEGIN ALTER DATABASE [$Name] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [$Name]; END;
CREATE DATABASE [$Name];
"@
    Invoke-SqlText -Catalog 'master' -Sql "ALTER DATABASE [$Name] SET RECOVERY SIMPLE;"
}
$runTool = {
    param([string] $Catalog, [hashtable] $Extra = @{})
    & $csvTool -Path $csvDir -ConnectionString (New-ConnString $Catalog) @Extra
}
$compare = {
    # Rows of $Left's audit.CopilotInteraction missing from, or different in, $Right.
    param([string] $Left, [string] $Right)
    [long] (Invoke-SqlText -Catalog 'master' -As Scalar -Sql @"
SELECT COUNT_BIG(*) FROM (
    SELECT RecordId, CreatedDateTime, ActivityDate, UserPrincipalName, AppHost, AppIdentity, ClientRegion,
           ModelName, ModelProvider, ThreadId, PromptCount, AccessedResourceCount, UsedWebSearch
    FROM [$Left].audit.CopilotInteraction
    EXCEPT
    SELECT RecordId, CreatedDateTime, ActivityDate, UserPrincipalName, AppHost, AppIdentity, ClientRegion,
           ModelName, ModelProvider, ThreadId, PromptCount, AccessedResourceCount, UsedWebSearch
    FROM [$Right].audit.CopilotInteraction) AS x;
"@)
}
try {
    $recs = Invoke-SqlText -Catalog $loadDb -As Table -Sql @"
SELECT RecordId, CONVERT(varchar(19), CreationDate, 126) AS CreationDate, RecordType, Operation, UserId, AuditData,
       AssociatedAdminUnits
FROM audit.vCopilotAuditRecord ORDER BY CreationDate, RecordId;
"@
    $all = @($recs.Rows | ForEach-Object {
        [pscustomobject]@{ RecordId = $_.RecordId; CreationDate = $_.CreationDate; RecordType = $_.RecordType
                           Operation = $_.Operation; UserId = $_.UserId; AuditData = $_.AuditData
                           AssociatedAdminUnits = "$($_.AssociatedAdminUnits)"; AssociatedAdminUnitsNames = '' } })
    $n = $all.Count
    $parts = @(, $all[0..([int]($n * 0.5))]) + @(, $all[([int]($n * 0.35))..([int]($n * 0.8))]) + @(, $all[([int]($n * 0.7))..($n - 1)])
    for ($i = 0; $i -lt 3; $i++) { $parts[$i] | Export-Csv (Join-Path $csvDir "export-$i.csv") -NoTypeInformation -Encoding utf8BOM }
    Copy-Item (Join-Path $csvDir 'export-0.csv') (Join-Path $csvDir 'export-0 (copy).csv')
    $csvRows = [long] (($parts | ForEach-Object { $_.Count } | Measure-Object -Sum).Sum)
    $firstDay = [datetime]::ParseExact($all[0].CreationDate.Substring(0, 10), 'yyyy-MM-dd', [cultureinfo]::InvariantCulture)

    # --- A database that starts from CSV files --------------------------------
    & $newDb $csvDb
    & $runTool $csvDb @{ CoveredThroughUtc = (& $d 9) }
    $c = (Invoke-SqlText -Catalog $csvDb -As Table -Sql @"
SELECT (SELECT COUNT_BIG(*) FROM audit.CopilotAudit)                AS Rows_,
       (SELECT COUNT(DISTINCT RecordId) FROM audit.CopilotAudit)    AS Distinct_,
       (SELECT COUNT_BIG(*) FROM audit.CopilotInteraction)          AS Interactions,
       i.FilesLoaded, i.RowsStaged, i.RowsDuplicate, i.RowsAdded,
       r.RangeStartUtc, r.RangeEndUtc, r.Source
FROM audit.CopilotCsvImport AS i CROSS JOIN audit.CopilotBackfillRange AS r;
"@).Rows[0]
    Assert-That ([long] $c.Rows_ -eq $n -and [long] $c.Distinct_ -eq $n -and [long] $c.Interactions -eq $n) `
        "every record once from $csvRows overlapping CSV rows ($($c.Rows_) rows, $($c.Distinct_) distinct, $n expected)"
    Assert-That ([int] $c.FilesLoaded -eq 3 -and [long] $c.RowsDuplicate -eq ($csvRows - $n)) `
        "the identical copy was skipped and the overlaps removed ($($c.FilesLoaded) files, $($c.RowsDuplicate) duplicates)"
    $diff = (& $compare $loadDb $csvDb) + (& $compare $csvDb $loadDb)
    Assert-That ($diff -eq 0) "audit.CopilotInteraction is the same as the Graph path built ($diff rows differ)"
    $same = [long] (Invoke-SqlText -Catalog 'master' -As Scalar -Sql @"
SELECT COUNT_BIG(*) FROM [$csvDb].audit.CopilotAudit AS c
JOIN [$loadDb].audit.CopilotAudit AS l ON l.RecordId = c.RecordId
WHERE l.CreatedDateTime = c.CreatedDateTime AND l.UserPrincipalName = c.UserPrincipalName
  AND CAST(DECOMPRESS(l.AuditDataGz) AS NVARCHAR(MAX)) = CAST(DECOMPRESS(c.AuditDataGz) AS NVARCHAR(MAX));
"@)
    Assert-That ($same -eq $n) "each record's time, UPN and JSON match the Graph path's ($same of $n)"
    Assert-That ("$($c.Source)" -eq 'CSV' -and $c.RangeEndUtc -eq (& $d 9) -and $c.RangeStartUtc -eq $firstDay) `
        ("the covered range is recorded for -Incremental ({0:yyyy-MM-dd} to {1:yyyy-MM-dd}, {2})" -f $c.RangeStartUtc, $c.RangeEndUtc, $c.Source)

    & $runTool $csvDb
    $imports = [int] (Invoke-SqlText -Catalog $csvDb -As Scalar -Sql 'SELECT COUNT(*) FROM audit.CopilotCsvImport;')
    Assert-That ($imports -eq 1) "running again over the same folder imports nothing ($imports import)"

    # --- A new record, a cut-off row, a later coverage end --------------------
    $j = $all[0].AuditData | ConvertFrom-Json
    $j.Id = 'csv-new-000001'; $j.CreationTime = '2026-08-09T12:00:00'
    $bad = $all[1].PSObject.Copy(); $bad.RecordId = 'csv-bad-000001'; $bad.AuditData = $bad.AuditData.Substring(0, 50)
    @([pscustomobject]@{ RecordId = $j.Id; CreationDate = ''; RecordType = 'CopilotInteraction'; Operation = 'CopilotInteraction'
                         UserId = $all[0].UserId; AuditData = ($j | ConvertTo-Json -Depth 20 -Compress)
                         AssociatedAdminUnits = ''; AssociatedAdminUnitsNames = '' },
      $bad) | Export-Csv (Join-Path $csvDir 'export-late.csv') -NoTypeInformation -Encoding utf8BOM
    & $runTool $csvDb @{ CoveredThroughUtc = (& $d 10) }
    $c = (Invoke-SqlText -Catalog $csvDb -As Table -Sql @"
SELECT i.RowsAdded, i.RowsRejected,
       (SELECT COUNT(*) FROM audit.CopilotCsvReject AS r JOIN audit.CopilotCsvFile AS f ON f.FileId = r.FileId
        WHERE f.ImportId = i.ImportId AND r.RecordId = 'csv-bad-000001' AND r.Reason LIKE '%not valid JSON%') AS Listed,
       (SELECT AppHost FROM audit.CopilotInteraction WHERE RecordId = 'csv-new-000001') AS NewAppHost,
       (SELECT RangeEndUtc FROM audit.CopilotBackfillRange) AS RangeEndUtc
FROM audit.CopilotCsvImport AS i WHERE i.ImportId = (SELECT MAX(ImportId) FROM audit.CopilotCsvImport);
"@).Rows[0]
    Assert-That ([long] $c.RowsAdded -eq 1 -and "$($c.NewAppHost)" -ne '') "only the new file is read, and its new record is added and shredded ($($c.RowsAdded) added, AppHost $($c.NewAppHost))"
    Assert-That ([long] $c.RowsRejected -eq 1 -and [int] $c.Listed -eq 1) 'the cut-off row is rejected and listed with its reason'
    Assert-That ($c.RangeEndUtc -eq (& $d 10)) ("a later coverage end widens the CSV-recorded range ({0:yyyy-MM-dd})" -f $c.RangeEndUtc)

    $msg = try { [void] (Invoke-SqlText -Catalog $csvDb -As Table -Sql (Get-Content -LiteralPath (Join-Path $root 'sql' 'Finalize-CopilotAudit.sql') -Raw)); '' } catch { $_.Exception.GetBaseException().Message }
    Assert-That ($msg -like '*CSV imports have been merged*') "finalize refuses once CSV rows are merged, rather than dropping them ($msg)"

    # --- Into section 11's database: Graph backfill and loads already there ----
    Remove-Item -LiteralPath (Join-Path $csvDir 'export-late.csv')
    $before = [long] (Invoke-SqlText -Catalog $loadDb -As Scalar -Sql 'SELECT COUNT_BIG(*) FROM audit.CopilotAudit;')
    & $runTool $loadDb @{ CoveredThroughUtc = (& $d 12) }
    $c = (Invoke-SqlText -Catalog $loadDb -As Table -Sql @"
SELECT i.RowsAdded, i.RowsAlreadyLoaded, (SELECT COUNT_BIG(*) FROM audit.CopilotAudit) AS Rows_,
       r.RangeEndUtc, r.Source
FROM audit.CopilotCsvImport AS i CROSS JOIN audit.CopilotBackfillRange AS r;
"@).Rows[0]
    Assert-That ([long] $c.RowsAdded -eq 0 -and [long] $c.RowsAlreadyLoaded -eq $n -and [long] $c.Rows_ -eq $before) `
        "over the Graph path's own records the import adds nothing ($($c.RowsAlreadyLoaded) already loaded, $($c.Rows_) rows)"
    Assert-That ($c.RangeEndUtc -eq (& $d 5) -and $c.Source -is [DBNull]) 'the Graph backfill''s range is left alone'

    # --- Refused while a Graph backfill is started but not finalized ----------
    & $newDb $guardDb
    Invoke-SqlText -Catalog $guardDb -Sql (Get-Content -LiteralPath (Join-Path $root 'sql' 'Create-CopilotAuditSchema.sql') -Raw)
    Invoke-SqlText -Catalog $guardDb -Parameters @{ '@s' = (& $d 1); '@e' = (& $d 5) } -Sql @"
INSERT INTO audit.CopilotBackfillRange (Id, RangeStartUtc, RangeEndUtc, Operations) VALUES (1, @s, @e, N'CopilotInteraction');
"@
    $msg = try { & $runTool $guardDb; '' } catch { $_.Exception.GetBaseException().Message }
    Assert-That ($msg -like '*not finalized*') "an import waits for an unfinished Graph backfill to be finalized ($msg)"
    $stray = [int] (Invoke-SqlText -Catalog $guardDb -As Scalar -Sql "SELECT CASE WHEN OBJECT_ID(N'audit.CopilotAudit', N'U') IS NULL THEN 0 ELSE 1 END;")
    Assert-That ($stray -eq 0) 'and creates no final table that finalize would then have to replace'
}
finally {
    Remove-Item -LiteralPath $csvDir -Recurse -Force -ErrorAction SilentlyContinue
    foreach ($db in $csvDb, $guardDb) {
        try { Invoke-SqlText -Catalog 'master' -Sql "IF DB_ID(N'$db') IS NOT NULL BEGIN ALTER DATABASE [$db] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [$db]; END;" } catch { Write-Host "  (could not drop $db)" }
    }
}

# ---------------------------------------------------------------------------
# 14. The Copilot Insights dashboard's rollups (dash.usp_RefreshDashboard),
# over section 12's bi schema in the main database, with an org chart of our
# own: a CEO who is their own manager, two levels under them, a manager loop
# and a person whose manager is not in the directory.
# ---------------------------------------------------------------------------
Write-Host "`n14. Dashboard rollups (dash.usp_RefreshDashboard)" -ForegroundColor Cyan

Invoke-SqlScript -Sql (Get-Content -LiteralPath (Join-Path $root 'sql' 'Create-CopilotDashboardSchema.sql') -Raw)
Invoke-SqlScript -Sql (Get-Content -LiteralPath (Join-Path $root 'sql' 'Create-CopilotDashboardProcedure.sql') -Raw)
Invoke-SqlScript -Sql (Get-Content -LiteralPath (Join-Path $root 'sql' 'Create-CopilotDashboardSchema.sql') -Raw)   # re-runnable

Invoke-SqlText -Sql @"
DELETE FROM audit.UserDirectory;
INSERT INTO audit.UserDirectory (UserPrincipalName, FullName, Department, EmployeeType, IsManager, AccountEnabled, ManagerUPN, ManagerName, OrgDepth) VALUES
    (N'ceo@contoso.com',    N'The CEO',  N'Office',  N'EMPLOYEE', 1, 1, N'ceo@contoso.com',   N'The CEO',  0),
    (N'boss@contoso.com',   N'The Boss', N'Finance', N'EMPLOYEE', 1, 1, N'ceo@contoso.com',   N'The CEO',  1),
    (N'b@contoso.com',      N'User B',   N'Sales',   N'EMPLOYEE', 0, 1, N'ceo@contoso.com',   N'The CEO',  1),
    (N'a@contoso.com',      N'User A',   N'Finance', N'EMPLOYEE', 1, 1, N'boss@contoso.com',  N'The Boss', 2),
    (N'c@contoso.com',      N'User C',   N'Finance', N'EMPLOYEE', 0, 1, N'a@contoso.com',     N'User A',   3),
    (N'loop1@contoso.com',  N'Loop 1',   NULL,       N'EMPLOYEE', 1, 1, N'loop2@contoso.com', N'Loop 2',   1),
    (N'loop2@contoso.com',  N'Loop 2',   NULL,       N'EMPLOYEE', 1, 1, N'loop1@contoso.com', N'Loop 1',   1),
    (N'orphan@contoso.com', N'Orphan',   NULL,       N'EMPLOYEE', 0, 1, N'ghost@contoso.com', N'Ghost',    1);
UPDATE audit.UserDirectory SET Sid = 'S-1-5-21-1111-2222-3333-1001' WHERE UserPrincipalName = N'a@contoso.com';
UPDATE dash.Setting SET Value = N'ceo@contoso.com' WHERE Name = N'OrgTopUpn';
-- Labels, sites and a blocked resource, to be cut to their sites.
UPDATE audit.RawLogFlattened SET SensitivityLabelIds = N'label-1; label-2',
       [Accessed Resource Locations] = N'https://contoso.sharepoint.com/sites/fin/Shared Documents/a.docx; https://contoso-my.sharepoint.com/personal/x_contoso_com/Documents/b.docx'
WHERE RecordId = N's-05';
UPDATE audit.RawLogFlattened SET BlockedResourceUrls = N'https://contoso.sharepoint.com/sites/hr/Payroll/pay.xlsx' WHERE RecordId = N's-06';
UPDATE audit.RawLogFlattened SET CopilotEventData_LicenseType = N'Starter', CopilotEventData_MemoryUpdated = 1,
       CopilotEventData_SensitivityLabelId = '11111111-0000-0000-0000-000000000003' WHERE RecordId = N's-05';
-- The same resources as the flattening writes them, one row per element.
DELETE FROM audit.RawLogAccessedResource;
INSERT INTO audit.RawLogAccessedResource (RecordId, Ordinal, [Action], [Type], Name, SiteUrl, Site, SensitivityLabelId, XPIADetected, [Status], IsBlocked) VALUES
    (N's-05', 0, N'Read', N'File', N'a.docx', N'https://contoso.sharepoint.com/sites/fin/Shared Documents/a.docx',
     N'https://contoso.sharepoint.com/sites/fin', '11111111-0000-0000-0000-000000000001', 0, NULL, 0),
    (N's-05', 1, N'Read', N'File', N'b.docx', N'https://contoso-my.sharepoint.com/personal/x_contoso_com/Documents/b.docx',
     N'https://contoso-my.sharepoint.com/personal/x_contoso_com', '11111111-0000-0000-0000-000000000002', 0, NULL, 0),
    (N's-06', 0, N'Read', N'File', N'pay.xlsx', N'https://contoso.sharepoint.com/sites/hr/Payroll/pay.xlsx',
     N'https://contoso.sharepoint.com/sites/hr', NULL, 0, 'Failure', 1);
"@
Invoke-SqlText -Sql "EXEC bi.usp_RefreshBi @LicenceEventDatabase = N'$licDb';"
Invoke-SqlText -Sql 'EXEC dash.usp_RefreshDashboard;' -ShowMessages

$p = @{}
foreach ($row in (Invoke-SqlText -As Table -Sql 'SELECT Upn, ISNULL(CAST(Level AS INT), -1) AS Lvl, PeopleBelow, DirectReports FROM dash.Person;').Rows) { $p[$row.Upn] = $row }
Assert-That ($p['ceo@contoso.com'].Lvl -eq 1 -and $p['boss@contoso.com'].Lvl -eq 2 -and $p['b@contoso.com'].Lvl -eq 2 -and
             $p['a@contoso.com'].Lvl -eq 3 -and $p['c@contoso.com'].Lvl -eq 4) `
    'levels count down from the top, and a top who is their own manager is level 1'
Assert-That ($p['ceo@contoso.com'].PeopleBelow -eq 4 -and $p['boss@contoso.com'].PeopleBelow -eq 2 -and $p['a@contoso.com'].PeopleBelow -eq 1 -and $p['c@contoso.com'].PeopleBelow -eq 0) `
    "people below count everyone under, at any depth (CEO $($p['ceo@contoso.com'].PeopleBelow), boss $($p['boss@contoso.com'].PeopleBelow))"
Assert-That ($p['loop1@contoso.com'].Lvl -eq -1 -and $p['orphan@contoso.com'].Lvl -eq -1) 'people outside the top''s tree have no level'
Assert-That ($p.ContainsKey('ghost@contoso.com') -and $p['ghost@contoso.com'].PeopleBelow -eq 1) 'a manager missing from the directory is added, so they can see their people'
$bySid = [string] (Invoke-SqlText -As Scalar -Sql "SELECT Upn FROM dash.Person WHERE Sid = 'S-1-5-21-1111-2222-3333-1001';")
Assert-That ($bySid -eq 'a@contoso.com') "each person carries their account SID from the directory, so a sign-in from another domain is matched without a lookup ($bySid)"

$oc = (Invoke-SqlText -As Table -Sql @"
SELECT (SELECT Depth FROM dash.OrgClosure WHERE AncestorUpn = N'boss@contoso.com' AND DescendantUpn = N'c@contoso.com') AS BossC,
       (SELECT COUNT(*) FROM dash.OrgClosure WHERE AncestorUpn = N'ceo@contoso.com') AS UnderCeo,
       (SELECT COUNT(*) FROM dash.OrgClosure WHERE DescendantUpn = N'ceo@contoso.com') AS AboveCeo,
       (SELECT COUNT(*) FROM dash.OrgClosure WHERE AncestorUpn IN (N'loop1@contoso.com', N'loop2@contoso.com')) AS LoopPairs;
"@).Rows[0]
Assert-That ([int] $oc.BossC -eq 2 -and [int] $oc.UnderCeo -eq 5 -and [int] $oc.AboveCeo -eq 1) `
    "the closure holds each (manager, person under them) pair once with its distance, and the self-managed top has only themselves above ($($oc.UnderCeo), $($oc.AboveCeo))"
Assert-That ([int] $oc.LoopPairs -eq 4) "a manager loop ends: each of the two is under the other, once ($($oc.LoopPairs) pairs)"

$scope = @((Invoke-SqlText -As Table -Sql @"
SELECT u.UserPrincipalName FROM dash.fnScopeUsers(N'boss@contoso.com') AS s JOIN bi.DimUser AS u ON u.UserKey = s.UserKey ORDER BY 1;
"@).Rows | ForEach-Object { $_.UserPrincipalName })
Assert-That (($scope -join ',') -eq 'a@contoso.com,boss@contoso.com,c@contoso.com') "a manager's scope is themselves and the people under them who are in the data ($($scope -join ','))"
$bScope = (Invoke-SqlText -As Table -Sql "SELECT UserKey FROM dash.fnScopeUsers(N'b@contoso.com');").Rows.Count
Assert-That ($bScope -le 1) "someone with nobody under them sees at most themselves ($bScope)"

$daySync = {
    (Invoke-SqlText -As Table -Sql @"
SELECT (SELECT COUNT_BIG(*) FROM bi.FactInteraction) AS Facts, (SELECT SUM(CAST(Interactions AS BIGINT)) FROM dash.UserDay) AS Days_,
       (SELECT COUNT_BIG(*) FROM bi.FactSession) AS FactSessions, (SELECT SUM(CAST(Sessions AS BIGINT)) FROM dash.UserDay) AS DaySessions,
       (SELECT SUM(CAST(JailbreakDetected AS INT)) FROM bi.FactInteraction) AS FactJb, (SELECT SUM(Jailbreak) FROM dash.UserDay) AS DayJb,
       (SELECT COUNT(DISTINCT AgentKey) FROM bi.FactInteraction) AS Agents, (SELECT COUNT(*) FROM dash.AgentUse) AS AgentUse;
"@).Rows[0]
}
$ds = & $daySync
Assert-That ([long] $ds.Days_ -eq [long] $ds.Facts -and [long] $ds.DaySessions -eq [long] $ds.FactSessions -and [long] $ds.DayJb -eq [long] $ds.FactJb) `
    "the person-day rollup adds up to the facts ($($ds.Days_) of $($ds.Facts) interactions, $($ds.DaySessions) of $($ds.FactSessions) sessions)"
Assert-That ([int] $ds.AgentUse -eq [int] $ds.Agents) "every agent used has its first and last use ($($ds.AgentUse))"

$res = [string[]] @((Invoke-SqlText -As Table -Sql 'SELECT Kind, Item FROM dash.ResourceDay GROUP BY Kind, Item;').Rows | ForEach-Object { "$($_.Kind)=$($_.Item)" })
[Array]::Sort($res, [StringComparer]::Ordinal)
Assert-That (($res -join ' | ') -eq 'Blocked=https://contoso.sharepoint.com/sites/hr | Label=11111111-0000-0000-0000-000000000001 | Label=11111111-0000-0000-0000-000000000002 | Site=https://contoso-my.sharepoint.com/personal/x_contoso_com | Site=https://contoso.sharepoint.com/sites/fin') `
    "labels, sites of labelled content and blocked sites come from bi.FactAccessedResource ($($res -join ' | '))"

$bx = (Invoke-SqlText -As Table -Sql @"
SELECT (SELECT COUNT(*) FROM bi.FactAccessedResource) AS Resources,
       (SELECT COUNT(*) FROM bi.AccessedResources AS r JOIN bi.Sites AS s ON s.SiteKey = r.SiteKey WHERE s.IsPersonal = 1) AS Personal,
       (SELECT SUM(CAST(IsBlocked AS INT)) FROM bi.FactAccessedResource) AS Blocked,
       (SELECT l.LicenseType FROM bi.FactInteraction AS f JOIN bi.DimLicenseType AS l ON l.LicenseTypeKey = f.LicenseTypeKey WHERE f.RecordId = N's-05') AS LicType,
       (SELECT CAST(f.MemoryUpdated AS INT) FROM bi.FactInteraction AS f WHERE f.RecordId = N's-05') AS Memory,
       (SELECT s.LabelId FROM bi.FactInteraction AS f JOIN bi.SensitivityLabels AS s ON s.SensitivityLabelKey = f.SensitivityLabelKey WHERE f.RecordId = N's-05') AS EvLabel,
       (SELECT TOP (1) AccessedResourceRows FROM bi.RefreshRun ORDER BY RunId DESC) AS Logged;
"@).Rows[0]
Assert-That ([int] $bx.Resources -eq 3 -and [int] $bx.Personal -eq 1 -and [int] $bx.Blocked -eq 1 -and [long] $bx.Logged -eq 3) `
    "bi.FactAccessedResource has one row per element, with its site and blocked flag ($($bx.Resources) rows)"
Assert-That ("$($bx.LicType)" -eq 'Starter' -and [int] $bx.Memory -eq 1 -and "$($bx.EvLabel)" -eq '11111111-0000-0000-0000-000000000003') `
    "the interaction fact carries the record's licence type, memory flag and its own label ($($bx.LicType), $($bx.EvLabel))"

# Incremental: the days from the window on are replaced, the totals unchanged.
$lastDay = [int] (Invoke-SqlText -As Scalar -Sql 'SELECT MAX(DateKey) FROM dash.UserDay;')
$since = [datetime]::ParseExact("$lastDay", 'yyyyMMdd', $null).AddDays(-1)
Invoke-SqlText -Sql 'EXEC dash.usp_RefreshDashboard @SinceUtc = @s;' -Parameters @{ '@s' = $since }
$ds2 = & $daySync
$run = (Invoke-SqlText -As Table -Sql 'SELECT TOP (1) FromDateKey, UserDayRows FROM dash.RefreshRun ORDER BY RunId DESC;').Rows[0]
Assert-That ([long] $ds2.Days_ -eq [long] $ds.Days_ -and [long] $ds2.DaySessions -eq [long] $ds.DaySessions -and $run.FromDateKey -isnot [DBNull]) `
    "an incremental refresh rewrites only the days from its window ($($run.UserDayRows) person-days from $($run.FromDateKey)) and the totals still match"
Invoke-SqlText -Sql 'EXEC dash.usp_RefreshDashboard @SinceUtc = @s;' -Parameters @{ '@s' = $since }
Assert-That ([long] (& $daySync).Days_ -eq [long] $ds.Days_) 'and running it twice changes nothing'

# The dashboard's account, with what Install-CopilotInsights.ps1 grants it that the refresh
# touches: EXECUTE on it, SELECT on bi, dash and audit.UserDirectory. No ALTER, so no TRUNCATE.
$lowPriv = (Invoke-SqlText -As Table -Sql @"
IF USER_ID(N'dash_refresher') IS NULL CREATE USER dash_refresher WITHOUT LOGIN;
GRANT EXECUTE ON dash.usp_RefreshDashboard TO dash_refresher;
GRANT SELECT ON SCHEMA::bi TO dash_refresher;
GRANT SELECT ON SCHEMA::dash TO dash_refresher;
GRANT SELECT ON audit.UserDirectory TO dash_refresher;
EXECUTE AS USER = N'dash_refresher';
EXEC dash.usp_RefreshDashboard;
REVERT;
SELECT (SELECT SUM(CAST(Interactions AS BIGINT)) FROM dash.UserDay) AS Days_, (SELECT COUNT(*) FROM dash.Person) AS People;
"@).Rows[0]
Assert-That ([long] $lowPriv.Days_ -eq [long] $ds.Days_ -and [int] $lowPriv.People -eq $p.Count) `
    "the dashboard's account can run a full refresh without ALTER, deleting where it may not truncate ($($lowPriv.People) people)"

# With no top set, the refresh takes it from the org chart: the one person with
# no manager who has at least half the directory under them.
Invoke-SqlText -Sql "UPDATE dash.Setting SET Value = NULL WHERE Name = N'OrgTopUpn'; EXEC dash.usp_RefreshDashboard;"
$auto = (Invoke-SqlText -As Table -Sql @"
SELECT (SELECT Value FROM dash.Setting WHERE Name = N'OrgTopUpnDetected') AS Detected,
       (SELECT CAST(Level AS INT) FROM dash.Person WHERE Upn = N'c@contoso.com') AS CLevel;
"@).Rows[0]
Assert-That ("$($auto.Detected)" -eq 'ceo@contoso.com' -and [int] $auto.CLevel -eq 4) "with no top set, the org chart's top is found and levels follow it ($($auto.Detected))"

# ---------------------------------------------------------------------------
# 15. Provisioning and maintenance without SQL: Install-CopilotAudit.ps1 on a
# new server path and on section 14's database, and Invoke-CopilotMaintenance.ps1.
# ---------------------------------------------------------------------------
Write-Host "`n15. Provisioning and maintenance (Install-CopilotAudit.ps1, tools/Invoke-CopilotMaintenance.ps1)" -ForegroundColor Cyan

$installer = Join-Path $root 'Install-CopilotAudit.ps1'
$maint = Join-Path $root 'tools' 'Invoke-CopilotMaintenance.ps1'
$conn = @{ SqlServer = $script:ITServer; TrustServerCertificate = $true }
if (-not $script:ITIntegrated) {
    $conn.SqlCredential = [pscredential]::new($script:ITUser, [Net.NetworkCredential]::new('', $script:ITPassword).SecurePassword)
}
$freshDb = "$($script:ITDatabase)_Fresh"; $freshLic = "$($script:ITDatabase)_FreshLic"
try {
    $out = & $installer @conn -Database $freshDb -LicenceDatabase $freshLic -Dashboard -ReportingTimeZone 'GMT Standard Time' 6>&1 | Out-String
    $made = Invoke-SqlText -Catalog master -As Table -Sql "SELECT name, recovery_model_desc AS Recovery FROM sys.databases WHERE name IN (N'$freshDb', N'$freshLic');"
    Assert-That ($made.Rows.Count -eq 2 -and @($made.Rows | Where-Object Recovery -eq 'SIMPLE').Count -eq 2) 'a fresh install creates both databases, in SIMPLE recovery'
    $objs = [int] (Invoke-SqlText -Catalog $freshDb -As Scalar -Sql "SELECT COUNT(*) FROM sys.objects WHERE OBJECT_ID(N'audit.usp_FlattenCopilotAudit') IS NOT NULL AND OBJECT_ID(N'bi.usp_RefreshBi') IS NOT NULL AND OBJECT_ID(N'dash.usp_RefreshDashboard') IS NOT NULL AND object_id = OBJECT_ID(N'audit.RawLogAccessedResource');")
    $licObjs = [int] (Invoke-SqlText -Catalog $freshLic -As Scalar -Sql "SELECT CASE WHEN OBJECT_ID(N'audit.usp_ShredLicenseEvents') IS NOT NULL THEN 1 ELSE 0 END;")
    Assert-That ($objs -eq 1 -and $licObjs -eq 1) 'and installs every procedure and table in both'
    Assert-That ($out -like '*Load Copilot records into*' -and $out -like '*Backfill the licence history*') 'and says what is left to do'
    $zones = @($freshDb, $freshLic | ForEach-Object { [string] (Invoke-SqlText -Catalog $_ -As Scalar -Sql 'SELECT audit.ReportingTimeZone();') })
    Assert-That (($zones -join ',') -eq 'GMT Standard Time,GMT Standard Time' -and $out -like '*reporting time zone: GMT Standard Time*') `
        "-ReportingTimeZone is stored in both databases, where the procedures read it ($($zones -join ','))"
    $badZone = try { & $installer @conn -Database $freshDb -LicenceDatabase $freshLic -ReportingTimeZone 'Mars Standard Time' 6>$null | Out-Null; '' } catch { $_.Exception.Message }
    Assert-That ($badZone -like '*not a time zone SQL Server knows*') 'an unknown zone is refused'
    $again = & $installer @conn -Database $freshDb -LicenceDatabase $freshLic -Dashboard 6>&1 | Out-String
    Assert-That ($again -like '*Done.*' -and $again -notlike '*flattening*') 'running it again changes nothing and builds nothing'
}
finally {
    foreach ($db in $freshDb, $freshLic) {
        try { Invoke-SqlText -Catalog master -Sql "IF DB_ID(N'$db') IS NOT NULL BEGIN ALTER DATABASE [$db] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [$db]; END;" } catch { Write-Host "  (could not drop $db)" }
    }
}

# Upgrading from 2.1.0: flattened rows without the 2.2.0 fields are flattened again, and bi rebuilt.
# A 2.1.0 database: flattened rows, but no record shapes (only the 2.2.0 flatten counts them).
Invoke-SqlText -Sql 'TRUNCATE TABLE audit.RawLogRecordShape;'
$withJson = [long] (Invoke-SqlText -As Scalar -Sql 'SELECT COUNT(*) FROM audit.CopilotAudit WHERE AuditDataGz IS NOT NULL;')
$up = & $installer @conn -Database $script:ITDatabase -SkipLicenceDatabase -LicenceEventDatabases $licDb -Dashboard 6>&1 | Out-String
$shaped = [long] (Invoke-SqlText -As Scalar -Sql 'SELECT ISNULL(SUM(Records), 0) FROM audit.RawLogRecordShape;')
Assert-That ($up -like '*gained columns (2.2.0)*' -and $shaped -eq $withJson) "an upgrade from 2.1.0 is detected and every record with its JSON flattened again ($shaped of $withJson)"
$once = & $installer @conn -Database $script:ITDatabase -SkipLicenceDatabase -LicenceEventDatabases $licDb -Dashboard 6>&1 | Out-String
Assert-That ($once -notlike '*flattening*') 'and only once: the next run finds nothing to rebuild'
Assert-That ($up -like '*bi, in full*' -and $up -like '*Refresh the Power BI model in full once*') 'and bi rebuilt in full, with the model refresh left to do'
$moved = try { & $installer @conn -Database $script:ITDatabase -SkipLicenceDatabase -ReportingTimeZone 'Pacific Standard Time' 6>$null | Out-Null; '' } catch { $_.Exception.Message }
$still = [string] (Invoke-SqlText -As Scalar -Sql 'SELECT audit.ReportingTimeZone();')
Assert-That ($moved -like '*already holds records cut in Eastern Standard Time*' -and $still -eq 'Eastern Standard Time') 'the time zone cannot be changed once records are loaded'

$status = & $maint @conn -Database $script:ITDatabase -Task Status | Out-String
$topNow = [string] (Invoke-SqlText -As Scalar -Sql "SELECT COALESCE(MAX(CASE WHEN Name = N'OrgTopUpn' THEN NULLIF(Value, N'') END), MAX(CASE WHEN Name = N'OrgTopUpnDetected' THEN Value END)) FROM dash.Setting;")
Assert-That ($status -like '*Flattened*' -and $status -like "*Top of the organisation*$topNow*") "Status reports what is loaded, built, and the top of the organisation ($topNow)"
& $maint @conn -Database $script:ITDatabase -Task SetAppCategory -App 'Teams' -Category 'Copilot Chat' | Out-Null
& $maint @conn -Database $script:ITDatabase -Task GrantRole -Principal 'CONTOSO\Insights-Admins' -PrincipalType Group -Role Admin -Note 'test' | Out-Null
& $maint @conn -Database $script:ITDatabase -Task SetDashboardSetting -Name LicenceMonthlyCost -Value '31.5' | Out-Null
& $maint @conn -Database $script:ITDatabase -Task SetOrgTop -Upn 'boss@contoso.com' | Out-Null
$mt = (Invoke-SqlText -As Table -Sql @"
SELECT (SELECT AppCategory FROM audit.SessionAppCategory WHERE App = N'Teams') AS Teams,
       (SELECT PrincipalType + N'/' + Role FROM dash.RoleGrant WHERE Principal = N'CONTOSO\Insights-Admins') AS Grant_,
       (SELECT Value FROM dash.Setting WHERE Name = N'LicenceMonthlyCost') AS Cost,
       (SELECT CAST(Level AS INT) FROM dash.Person WHERE Upn = N'a@contoso.com') AS ALevel,
       (SELECT COUNT(*) FROM dash.AuditLog WHERE Page = N'Invoke-CopilotMaintenance') AS Logged;
"@).Rows[0]
Assert-That ("$($mt.Teams)" -eq 'Copilot Chat' -and "$($mt.Grant_)" -eq 'Group/Admin' -and "$($mt.Cost)" -eq '31.5') 'maintenance tasks set an app category, grant a role and change a setting, without SQL'
Assert-That ([int] $mt.ALevel -eq 2 -and [int] $mt.Logged -ge 3) "a new top of the organisation takes effect at once (user A now level $($mt.ALevel)), and every change is audit-logged"
$refused = try { & $maint @conn -Database $script:ITDatabase -Task SetOrgTop -Upn 'nobody@contoso.com'; '' } catch { $_.Exception.Message }
Assert-That ($refused -like '*not in the directory*') 'a top of the organisation not in the directory is refused'

# ---------------------------------------------------------------------------
# 16. Operations (2.3.0): the operations schema over section 14's dashboard,
# its secrets, privacy mode, retention, the value catalogue's cap, the
# pipeline's run record, and the Power BI views.
# ---------------------------------------------------------------------------
Write-Host "`n16. Operations (Create-CopilotOperationsSchema.sql, Invoke-CopilotPipeline.ps1 run records)" -ForegroundColor Cyan

Invoke-SqlScript -Sql (Get-Content -LiteralPath (Join-Path $root 'sql' 'Create-CopilotOperationsSchema.sql') -Raw)
Invoke-SqlScript -Sql (Get-Content -LiteralPath (Join-Path $root 'sql' 'Create-CopilotOperationsSchema.sql') -Raw)   # re-runnable
$ops = (Invoke-SqlText -As Table -Sql @"
SELECT (SELECT COUNT(*) FROM sys.tables WHERE SCHEMA_NAME(schema_id) = 'dash' AND name IN ('Secret', 'Subscription', 'NotificationLog', 'AlertState', 'Target', 'TimeSaving', 'TrainingAttendance', 'ValueName')) AS Tables_,
       (SELECT COUNT(*) FROM dash.Setting WHERE Name IN ('MailMode', 'SmtpPort', 'AlertAdminTo', 'FreshnessHours', 'JsonRetentionDays', 'ApiEnabled')) AS Settings_,
       (SELECT COUNT(*) FROM sys.views WHERE SCHEMA_NAME(schema_id) = 'bi' AND name IN ('OrgSecurity', 'ValueNames', 'LicensePool', 'DepartmentWeek')) AS Views_,
       DATABASE_PRINCIPAL_ID(N'CopilotInsightsApp') AS RoleId;
"@).Rows[0]
Assert-That ([int] $ops.Tables_ -eq 8 -and [int] $ops.Settings_ -eq 6 -and [int] $ops.Views_ -eq 4 -and $ops.RoleId -isnot [DBNull]) 'the operations schema installs its tables, settings, Power BI views and the dashboard''s role, and re-runs'

# Secrets: encrypted, and readable by the dashboard's role only through the procedures.
Invoke-SqlText -Sql @"
IF USER_ID(N'it_app') IS NULL CREATE USER it_app WITHOUT LOGIN;
ALTER ROLE CopilotInsightsApp ADD MEMBER it_app;
EXEC dash.usp_SetSecret @Name = 'SmtpPassword', @Value = N'p@ss wörd 2525', @By = N'test';
"@
$sec = (Invoke-SqlText -As Table -Sql @"
DECLARE @v NVARCHAR(2000), @direct NVARCHAR(400) = N'allowed';
EXECUTE AS USER = 'it_app';
EXEC dash.usp_GetSecret @Name = 'SmtpPassword', @Value = @v OUTPUT;
BEGIN TRY DECLARE @n INT = (SELECT COUNT(*) FROM dash.Secret); END TRY BEGIN CATCH SET @direct = ERROR_MESSAGE(); END CATCH;
REVERT;
SELECT @v AS Value, @direct AS Direct, (SELECT CAST(Cipher AS VARBINARY(20)) FROM dash.Secret WHERE Name = 'SmtpPassword') AS Stored;
"@).Rows[0]
Assert-That ("$($sec.Value)" -eq 'p@ss wörd 2525') 'a secret round-trips through dash.usp_SetSecret and dash.usp_GetSecret, as the dashboard''s role'
Assert-That ("$($sec.Direct)" -like '*permission was denied*') 'but the role cannot read dash.Secret itself'
Assert-That (-not ([Text.Encoding]::Unicode.GetString([byte[]] $sec.Stored)).Contains('p@ss')) 'and it is stored encrypted'
Invoke-SqlText -Sql "EXEC dash.usp_SetSecret @Name = 'SmtpPassword', @Value = NULL, @By = N'test';"
Assert-That ([int] (Invoke-SqlText -As Scalar -Sql "SELECT COUNT(*) FROM dash.Secret WHERE Name = 'SmtpPassword'") -eq 0) 'an empty value removes the secret'

# Privacy mode: bi.Users pseudonymised, the same hash for the same person, counts unchanged.
$plain = (Invoke-SqlText -As Table -Sql "SELECT COUNT(*) AS N, COUNT(DISTINCT UserPrincipalName) AS D FROM bi.Users;").Rows[0]
Invoke-SqlText -Sql "EXECUTE AS USER = 'it_app'; EXEC dash.usp_SetPrivacyMode @Mode = N'Pseudonymise', @By = N'test'; REVERT;"
$hidden = (Invoke-SqlText -As Table -Sql @"
SELECT COUNT(*) AS N, COUNT(DISTINCT UserPrincipalName) AS D,
       SUM(CASE WHEN UserPrincipalName LIKE N'%@%' OR DisplayName LIKE N'User %' OR ManagerUpn LIKE N'%@%' THEN 1 ELSE 0 END) AS Named,
       SUM(CASE WHEN UserPrincipalName LIKE N'P-%' THEN 1 ELSE 0 END) AS Hashed
FROM bi.Users;
"@).Rows[0]
Assert-That ([int] $hidden.Named -eq 0 -and [int] $hidden.Hashed -gt 0) "privacy mode names nobody in bi.Users ($($hidden.Hashed) pseudonyms)"
Assert-That ([int] $hidden.N -eq [int] $plain.N -and [int] $hidden.D -eq [int] $plain.D) 'with the same rows and one distinct pseudonym per person'
Invoke-SqlText -Sql "EXEC dash.usp_SetPrivacyMode @Mode = N'Off', @By = N'test';"
Assert-That ([int] (Invoke-SqlText -As Scalar -Sql "SELECT COUNT(*) FROM bi.Users WHERE UserPrincipalName LIKE N'%@%'") -gt 0) 'and off again, names come back'

# Retention: logs past their days go; JSON is never cleared under 200 days.
Invoke-SqlText -Sql @"
INSERT INTO dash.AuditLog (AtUtc, Viewer, Kind, Page) VALUES (DATEADD(DAY, -800, SYSUTCDATETIME()), N'old', 'View', N'/'), (SYSUTCDATETIME(), N'new', 'View', N'/');
INSERT INTO dash.NotificationLog (AtUtc, Kind, Subject, Status) VALUES (DATEADD(DAY, -400, SYSUTCDATETIME()), 'Test', N'old', 'Sent');
UPDATE dash.Setting SET Value = N'90' WHERE Name = 'JsonRetentionDays';
"@
$ret = [string] (Invoke-SqlText -As Scalar -Sql 'EXEC dash.usp_ApplyRetention;')
$left = (Invoke-SqlText -As Table -Sql "SELECT (SELECT COUNT(*) FROM dash.AuditLog WHERE Viewer = N'old') AS OldAudit, (SELECT COUNT(*) FROM dash.AuditLog WHERE Viewer = N'new') AS NewAudit, (SELECT COUNT(*) FROM dash.NotificationLog WHERE Subject = N'old') AS OldNote;").Rows[0]
Assert-That ([int] $left.OldAudit -eq 0 -and [int] $left.NewAudit -eq 1 -and [int] $left.OldNote -eq 0) "retention deletes the audit-log and notification rows past their days, and nothing newer ($ret)"
Assert-That ($ret -like '*JSON kept*') 'and refuses to clear JSON with retention under 200 days'
Invoke-SqlText -Sql "UPDATE dash.Setting SET Value = N'0' WHERE Name = 'JsonRetentionDays';"

# The value catalogue's cap: a column stops collecting at 10,000 distinct values.
Invoke-SqlText -Sql @"
DELETE FROM audit.ValueCatalog WHERE ColumnId = 2;
INSERT INTO audit.ValueCatalog (ColumnId, Value, Records) SELECT TOP (9999) 2, CONCAT(N'fake-app-', ROW_NUMBER() OVER (ORDER BY (SELECT NULL))), 1 FROM sys.all_objects a CROSS JOIN sys.all_objects b;
UPDATE audit.ValueCatalogColumn SET DistinctValues = 9999, Capped = 0 WHERE ColumnId = 2;
EXEC audit.usp_CatalogValues;
"@
$cap = (Invoke-SqlText -As Table -Sql "SELECT DistinctValues, Capped, (SELECT COUNT(*) FROM audit.ValueCatalog WHERE ColumnId = 2) AS Rows_ FROM audit.ValueCatalogColumn WHERE ColumnId = 2;").Rows[0]
Assert-That ([int] $cap.Rows_ -eq 10000 -and [bool] $cap.Capped) "a column stops at 10,000 values and is marked capped ($($cap.Rows_), capped: $($cap.Capped))"
Invoke-SqlText -Sql 'EXEC audit.usp_CatalogValues @Rebuild = 1;'
Assert-That (-not [bool] (Invoke-SqlText -As Scalar -Sql 'SELECT Capped FROM audit.ValueCatalogColumn WHERE ColumnId = 2')) 'and a rebuild starts it again'
Invoke-SqlText -Sql "INSERT INTO dash.ValueName (ColumnId, Value, Name, UpdatedBy) SELECT TOP (1) 2, Value, N'Named app', N'test' FROM audit.ValueCatalog WHERE ColumnId = 2 ORDER BY Value;"
$named = (Invoke-SqlText -As Table -Sql "SELECT (SELECT COUNT(*) FROM bi.ValueNames WHERE ColumnName = N'App' AND Name = N'Named app') AS InView, dash.fnValueName(2, (SELECT TOP (1) Value FROM audit.ValueCatalog WHERE ColumnId = 2 ORDER BY Value)) AS Fn, dash.fnValueName(2, N'unnamed') AS Unnamed;").Rows[0]
Assert-That ([int] $named.InView -eq 1 -and "$($named.Fn)" -eq 'Named app' -and "$($named.Unnamed)" -eq 'unnamed') 'a name shows in bi.ValueNames and through dash.fnValueName, and an unnamed value stays itself'

# The pipeline's run record, through Invoke-CopilotPipeline.ps1's own functions.
& {
    . (Join-Path $root 'Invoke-CopilotPipeline.ps1')
    $setting = @{ SqlServer = $script:ITServer; Database = $script:ITDatabase; ConnectionString = (New-ConnString $script:ITDatabase) }
    $log = Join-Path ([IO.Path]::GetTempPath()) "pipeline-it-$([guid]::NewGuid().ToString('N')).log"
    Set-Content $log "line one`nConnect-MgGraph: the certificate has expired."
    $id = Write-PipelineRecord $setting -Log $log -Parameters @{ '@k' = 'Nightly'; '@h' = 'TEST'; '@u' = 'me' } -Sql 'INSERT INTO audit.PipelineRun (Kind, Host, RunAs) VALUES (@k, @h, @u); SELECT CAST(SCOPE_IDENTITY() AS INT);'
    [void] (Write-PipelineRecord $setting -Log $log -Parameters @{ '@r' = $id; '@n' = 1; '@s' = 'Copilot licences from Graph' } -Sql 'INSERT INTO audit.PipelineRunStep (RunId, StepNo, Name) VALUES (@r, @n, @s);')
    [void] (Write-PipelineRecord $setting -Log $log -Parameters @{ '@r' = $id; '@n' = 1; '@c' = 1 } -Sql 'UPDATE audit.PipelineRunStep SET EndedUtc = SYSUTCDATETIME(), ExitCode = @c WHERE RunId = @r AND StepNo = @n;')
    [void] (Write-PipelineRecord $setting -Log $log -Parameters @{ '@r' = $id; '@f' = 'Copilot licences from Graph'; '@d' = (Get-LogTail $log) } -Sql "UPDATE audit.PipelineRun SET EndedUtc = SYSUTCDATETIME(), Status = 'Failed', FailedStep = @f, Detail = @d WHERE RunId = @r;")
    $run = (Invoke-SqlText -As Table -Sql "SELECT r.Status, r.FailedStep, r.Detail, s.ExitCode FROM audit.PipelineRun AS r JOIN audit.PipelineRunStep AS s ON s.RunId = r.RunId WHERE r.RunId = $id;").Rows[0]
    Assert-That ("$($run.Status)" -eq 'Failed' -and [int] $run.ExitCode -eq 1 -and "$($run.Detail)" -like '*certificate has expired*') 'the pipeline records its run, each step''s exit code, and the failed step''s last lines'
    $bad = @{ SqlServer = 'no-such-server.invalid'; Database = 'x'; ConnectionString = 'Server=no-such-server.invalid;Database=x;Integrated Security=true;Connect Timeout=2' }
    $threw = try { [void] (Write-PipelineRecord $bad -Log $log -Parameters @{} -Sql 'SELECT 1;'); $false } catch { $true }
    Assert-That (-not $threw -and (Get-Content $log -Raw) -like '*not recorded in audit.PipelineRun*') 'and a database it cannot reach costs a line in the log, not the run'
    Remove-Item $log -ErrorAction SilentlyContinue
}

# Power BI: row-level security from the org chart, and department figures with small groups hidden.
$sec = (Invoke-SqlText -As Table -Sql @"
SELECT (SELECT COUNT(*) FROM bi.OrgSecurity WHERE ViewerUpn = N'a@contoso.com') AS AUnder,
       (SELECT COUNT(DISTINCT u.UserPrincipalName) FROM bi.OrgSecurity AS s JOIN bi.DimUser AS u ON u.UserKey = s.UserKey WHERE s.ViewerUpn = N'a@contoso.com') AS ADistinct,
       (SELECT COUNT(*) FROM bi.OrgSecurity WHERE ViewerUpn = N'c@contoso.com') AS CSelf,
       (SELECT COUNT(*) FROM bi.DimUser) AS Everyone,
       (SELECT COUNT(*) FROM bi.OrgSecurity AS s WHERE s.ViewerUpn = (SELECT TOP (1) Upn FROM dash.Person WHERE Level = 1)) AS TopSees,
       (SELECT COUNT(*) FROM bi.DepartmentWeek WHERE Hidden = 1 AND ActivePeople IS NOT NULL) AS Leaks;
"@).Rows[0]
Assert-That ([int] $sec.CSelf -le 1 -and [int] $sec.AUnder -ge [int] $sec.CSelf) "bi.OrgSecurity: a person sees themselves, a manager their people ($($sec.AUnder) for user A)"
Assert-That ([int] $sec.TopSees -eq [int] $sec.Everyone) "the top of the organisation sees everyone ($($sec.TopSees) of $($sec.Everyone))"
Assert-That ([int] $sec.Leaks -eq 0) 'bi.DepartmentWeek hides every figure of a group under the minimum size'
Invoke-SqlText -Sql "ALTER ROLE CopilotInsightsApp DROP MEMBER it_app; DROP USER it_app;"

# ---------------------------------------------------------------------------
Write-Host ''
if ($script:Failures.Count) {
    Write-Host "$($script:Failures.Count) assertion(s) failed." -ForegroundColor Red
    exit 1
}
Write-Host 'All SQL integration tests passed.' -ForegroundColor Green
