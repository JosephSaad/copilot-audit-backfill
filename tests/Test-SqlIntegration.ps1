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
# query would then silently run against the wrong catalog. The $script:
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
Assert-That (($names -join ',') -eq 'CopilotAuditStage,CopilotBackfillRange,CopilotBackfillWindow,CopilotLoad,CopilotLoadStage,CopilotLoadWindow,PipelineCertificate,PipelineRun,PipelineRunStep,PipelineSetting') `
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
    $expected = Get-ExpectedId $mock
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

    $run1 = Invoke-EngineRun -Mock $mock -Options (New-EngineOption $mock $start $end) -Store $store -Sink $sink -OnTick $tick -Log $log -TimeoutSec 900
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
    $run2 = Invoke-EngineRun -Mock $mock -Options (New-EngineOption $mock $start $end) -Store $store2 -Sink $sink2 -Log $log -TimeoutSec 900

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

    # 2.4.12: one run at a time on a window table. Two at once planned the same windows twice and wrote over each
    # other's rows; the second now stops before touching the table, and the table is free once the first has ended.
    # On the load window table, which section 11 starts from empty.
    $none = [System.Threading.CancellationToken]::None
    $holder = [CopilotAudit.SqlWindowStore]::new($connStr, '[audit].[CopilotLoadWindow]')
    $held = $holder.ClaimAsync($none).GetAwaiter().GetResult()
    $other = [CopilotAudit.SqlWindowStore]::new($connStr, '[audit].[CopilotLoadWindow]')
    $logBusy = [System.Collections.Generic.List[string]]::new()
    $busy = Invoke-EngineRun -Mock $mock -Options (New-EngineOption $mock $start $end) -Store $other -Sink $sink2 -Log $logBusy -TimeoutSec 60
    $busyWhy = if ($busy.Task.Exception) { $busy.Task.Exception.GetBaseException().Message } else { '' }
    $planned = [int] (Invoke-SqlText -As Scalar -Sql 'SELECT COUNT(*) FROM audit.CopilotLoadWindow;')
    Assert-That ($busy.Task.IsFaulted -and $busyWhy.StartsWith('Another run is loading [audit].[CopilotLoadWindow]') -and $planned -eq 0) `
        "2.4.12: a second run on a window table in use stops before planning anything ($busyWhy)"
    $held.Dispose()
    $freed = try { $other.ClaimAsync($none).GetAwaiter().GetResult().Dispose(); $true } catch { $false }
    Assert-That $freed 'and the table is free once the first run lets it go'

    # 2.4.12: a split is one transaction. The parent marked Split and its two halves were three writes, and a run that
    # stopped between them left a hole nothing planned again. A split whose second half cannot be written (an end
    # before its start, which the table's check refuses) now leaves the parent as it was, and no half.
    $parent = [CopilotAudit.AuditWindow]@{ StartUtc = $start; EndUtc = $start.AddHours(2); Status = 'Submitted'; QueryId = 'q-split' }
    [void] $holder.InsertAsync($parent, $none).GetAwaiter().GetResult()
    $parent.Status = 'Split'
    $older = [CopilotAudit.AuditWindow]@{ ParentWindowId = $parent.WindowId; StartUtc = $start; EndUtc = $start.AddHours(1) }
    $bad   = [CopilotAudit.AuditWindow]@{ ParentWindowId = $parent.WindowId; StartUtc = $start.AddHours(2); EndUtc = $start.AddHours(1) }
    $splitErr = try { [void] $holder.SplitAsync($parent, $older, $bad, $none).GetAwaiter().GetResult(); '' } catch { $_.Exception.Message }
    $kept = (Invoke-SqlText -As Table -Sql 'SELECT (SELECT Status FROM audit.CopilotLoadWindow WHERE ParentWindowId IS NULL) AS Parent, (SELECT COUNT(*) FROM audit.CopilotLoadWindow WHERE ParentWindowId IS NOT NULL) AS Halves;').Rows[0]
    Assert-That ($splitErr -ne '' -and "$($kept.Parent)" -eq 'Submitted' -and [int] $kept.Halves -eq 0) "2.4.12: a split that fails part way writes nothing: the parent is still $($kept.Parent), with $($kept.Halves) halves"
    $newer = [CopilotAudit.AuditWindow]@{ ParentWindowId = $parent.WindowId; StartUtc = $start.AddHours(1); EndUtc = $start.AddHours(2) }
    [void] $holder.SplitAsync($parent, $older, $newer, $none).GetAwaiter().GetResult()
    $done = (Invoke-SqlText -As Table -Sql 'SELECT (SELECT Status FROM audit.CopilotLoadWindow WHERE ParentWindowId IS NULL) AS Parent, (SELECT COUNT(*) FROM audit.CopilotLoadWindow WHERE ParentWindowId IS NOT NULL) AS Halves;').Rows[0]
    Assert-That ("$($done.Parent)" -eq 'Split' -and [int] $done.Halves -eq 2 -and $older.WindowId -gt 0 -and $newer.WindowId -gt $older.WindowId) 'and one that succeeds writes all three'
    Invoke-SqlText -Sql 'TRUNCATE TABLE audit.CopilotLoadWindow;'

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

    # 2.4.12: finalize again, with the staging table dropped as its last message suggests (the backfill then makes it
    # again, empty): it refuses, where it emptied audit.CopilotAudit and said "Coverage verified". Then, with staging
    # back, it is re-runnable as before.
    Invoke-SqlText -Sql "EXEC sp_rename N'audit.CopilotAuditStage', N'CopilotAuditStage_kept'; SELECT TOP (0) * INTO audit.CopilotAuditStage FROM audit.CopilotAuditStage_kept;"
    try {
        $refusal = try { [void] (Invoke-SqlText -As Table -Sql $finalizeSql); '' } catch { $_.Exception.GetBaseException().Message }
        $after = [long] (Invoke-SqlText -As Scalar -Sql 'SELECT COUNT_BIG(*) FROM audit.CopilotAudit;')
        Assert-That ($refusal -like 'Finalize refused: the staging table holds 0 of*' -and $after -eq $expectedTotal) `
            ("2.4.12: finalize refuses once staging has been dropped, and audit.CopilotAudit keeps its {0:N0} rows" -f $after)
    }
    finally { Invoke-SqlText -Sql "DROP TABLE audit.CopilotAuditStage; EXEC sp_rename N'audit.CopilotAuditStage_kept', N'CopilotAuditStage';" }
    Invoke-SqlText -Sql $finalizeSql
    $mark = [int] (Invoke-SqlText -As Scalar -Sql "SELECT COUNT(*) FROM audit.PipelineSetting WHERE Name = N'FinalizedUtc';")
    Assert-That ([long] (Invoke-SqlText -As Scalar -Sql 'SELECT COUNT_BIG(*) FROM audit.CopilotAudit;') -eq $expectedTotal -and $mark -eq 1) 'with staging whole it runs again as before, and leaves the mark that it finished'

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

    # 2.3.0: each batch's codes go into the value catalog, for Admin > Value names.
    $vc = (Invoke-SqlText -As Table -Sql @"
SELECT (SELECT DistinctValues FROM audit.ValueCatalogColumn WHERE ColumnId = 2) AS Apps,
       (SELECT COUNT(DISTINCT App) FROM audit.RawLogFlattened WHERE NULLIF(App, N'') IS NOT NULL) AS AppsInData,
       (SELECT SUM(Records) FROM audit.ValueCatalog WHERE ColumnId = 1) AS TenantRecords,
       (SELECT COUNT(*) FROM audit.RawLogFlattened WHERE OrganizationId IS NOT NULL) AS WithTenant,
       (SELECT COUNT(*) FROM audit.ValueCatalog WHERE ColumnId = 1 AND Value = LOWER(Value)) AS LowerGuids;
"@).Rows[0]
    Assert-That ([int] $vc.Apps -gt 0 -and [int] $vc.Apps -eq [int] $vc.AppsInData) "the flatten catalogs every distinct app, batch by batch ($($vc.Apps) of $($vc.AppsInData))"
    Assert-That ([long] $vc.TenantRecords -eq [long] $vc.WithTenant -and [int] $vc.LowerGuids -ge 1) "and counts each record once in a full rebuild, with GUIDs in lower case ($($vc.TenantRecords) of $($vc.WithTenant))"
    $before = [string] (Invoke-SqlText -As Scalar -Sql "SELECT STRING_AGG(CONCAT(ColumnId, ':', Value, ':', Records), '|') WITHIN GROUP (ORDER BY ColumnId, Value) FROM audit.ValueCatalog;")
    Invoke-SqlText -Sql 'EXEC audit.usp_CatalogValues @Rebuild = 1;'
    $after = [string] (Invoke-SqlText -As Scalar -Sql "SELECT STRING_AGG(CONCAT(ColumnId, ':', Value, ':', Records), '|') WITHIN GROUP (ORDER BY ColumnId, Value) FROM audit.ValueCatalog;")
    Assert-That ($before -eq $after -and $after.Length -gt 0) 'a rebuild of the catalog from every record gives the same values and counts as the batches did'

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
       (SELECT COUNT(*) FROM audit.RawLogAccessedResource WHERE SensitivityLabelId IS NOT NULL) AS LabeledElements,
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
    Assert-That ([long] $x.LabeledElements -eq $expectedTotal -and [long] $x.Contexts -eq $expectedTotal) 'element labels parsed as GUIDs, and one Contexts row per context'

    # 2.4.11: an agent's id and friendly name sit at the top of the record. Every 23rd mock record is a declarative
    # agent's, renamed on the 5th.
    $exp23 = Get-EveryNth 23
    $agentSql = @"
SELECT (SELECT COUNT(*) FROM audit.RawLogFlattened WHERE AgentName IS NOT NULL) AS Named,
       (SELECT COUNT(*) FROM audit.RawLogFlattened WHERE AgentId LIKE N'CopilotStudio.Declarative.T[_]%') AS WithId,
       (SELECT COUNT(*) FROM audit.RawLogFlattened WHERE AppIdentity LIKE N'Copilot.Studio.Declarative.%') AS AgentRecords,
       (SELECT COUNT(DISTINCT AgentName) FROM audit.RawLogFlattened) AS Names,
       (SELECT TOP (1) AgentName FROM audit.RawLogFlattened WHERE AgentName IS NOT NULL ORDER BY UtcTimeStamp DESC, RecordId DESC) AS LatestName,
       (SELECT COUNT(*) FROM audit.AgentCatalog) AS Agents,
       (SELECT MAX(AgentName) FROM audit.AgentCatalog) AS CatalogName,
       (SELECT CASE WHEN MAX(NameSeenUtc) = (SELECT MAX(UtcTimeStamp) FROM audit.RawLogFlattened WHERE AgentName IS NOT NULL)
                     AND MIN(FirstSeenUtc) = (SELECT MIN(UtcTimeStamp) FROM audit.RawLogFlattened WHERE AppIdentity LIKE N'Copilot.Studio.%') THEN 1 ELSE 0 END
        FROM audit.AgentCatalog) AS Times,
       (SELECT COUNT(*) FROM audit.PipelineSetting WHERE Name = N'AgentNamesReadUtc') AS Mark;
"@
    $ag = (Invoke-SqlText -As Table -Sql $agentSql).Rows[0]
    Assert-That ([long] $ag.Named -eq $exp23 -and [long] $ag.WithId -eq $exp23 -and [long] $ag.AgentRecords -eq $exp23) `
        ("2.4.11: the agent's id and friendly name are read from the top of the record ({0:N0} named, {1:N0} with an id, of {2:N0} agent records)" -f $ag.Named, $ag.WithId, $ag.AgentRecords)
    Assert-That ([int] $ag.Agents -eq 1 -and [int] $ag.Names -eq 2 -and "$($ag.CatalogName)" -eq 'Expense Policy Helper' -and "$($ag.CatalogName)" -eq "$($ag.LatestName)" -and [int] $ag.Times -eq 1) `
        "the agent catalog holds the agent once, under the name its latest record gave it, though the batches came in no order of time ($($ag.CatalogName))"
    Assert-That ([int] $ag.Mark -eq 1) 'and a flatten of every record leaves the mark that no record is left to read for agent names'

    # Records flattened before 2.4.11 have neither: the one-time read fills them from the stored JSON, and only them.
    $flatStamp = { [string] (Invoke-SqlText -As Scalar -Sql "SELECT CONCAT(COUNT_BIG(*), N':', CHECKSUM_AGG(CHECKSUM(RecordId, [User], App, ThreadId, FlattenedUtc, AccessedResourceCount, UserKey))) FROM audit.RawLogFlattened;") }
    $stamp0 = & $flatStamp
    Invoke-SqlText -Sql "UPDATE audit.RawLogFlattened SET AgentId = NULL, AgentName = NULL; DELETE FROM audit.AgentCatalog; DELETE FROM audit.PipelineSetting WHERE Name = N'AgentNamesReadUtc';"
    Invoke-SqlText -Sql 'EXEC audit.usp_BackfillAgentNames @BatchSize = 1000;' -ShowMessages
    $ag2 = (Invoke-SqlText -As Table -Sql $agentSql).Rows[0]
    Assert-That ([long] $ag2.Named -eq $exp23 -and [long] $ag2.WithId -eq $exp23 -and "$($ag2.CatalogName)" -eq 'Expense Policy Helper' -and [int] $ag2.Agents -eq 1 -and [int] $ag2.Times -eq 1 -and [int] $ag2.Mark -eq 1) `
        ("audit.usp_BackfillAgentNames gives records flattened before 2.4.11 their agent's id and name, from the JSON ({0:N0} of {1:N0})" -f $ag2.Named, $exp23)
    Assert-That ((& $flatStamp) -eq $stamp0) 'and touches nothing else of any record'
    Invoke-SqlText -Sql "UPDATE audit.RawLogFlattened SET AgentName = N'kept' WHERE RecordId = (SELECT MIN(RecordId) FROM audit.RawLogFlattened WHERE AgentName = N'Expense helper');"
    Invoke-SqlText -Sql 'EXEC audit.usp_BackfillAgentNames;'
    $kept = [int] (Invoke-SqlText -As Scalar -Sql "SELECT COUNT(*) FROM audit.RawLogFlattened WHERE AgentName = N'kept';")
    Assert-That ($kept -eq 1) 'run again, it reads only what is still empty: a name already there is kept'
    Invoke-SqlText -Sql "UPDATE audit.RawLogFlattened SET AgentName = N'Expense helper' WHERE AgentName = N'kept'; UPDATE audit.AgentCatalog SET AgentName = N'Expense Policy Helper';"

    # 2.4.12: a read stopped in a batch's catalog step keeps nothing of that batch, so the next run reads it again and
    # catalogs its agents. Before, the records kept their new names, the next run skipped them as filled, and their
    # agents never reached the catalog. A constraint the catalog cannot meet stands in for the stop.
    Invoke-SqlText -Sql "UPDATE audit.RawLogFlattened SET AgentId = NULL, AgentName = NULL; DELETE FROM audit.AgentCatalog; DELETE FROM audit.PipelineSetting WHERE Name = N'AgentNamesReadUtc'; ALTER TABLE audit.AgentCatalog WITH NOCHECK ADD CONSTRAINT CK_it_StopCatalog CHECK (AgentId = N'-');"
    $stopped = try { Invoke-SqlText -Sql 'EXEC audit.usp_BackfillAgentNames @BatchSize = 1000;'; $false } catch { $true }
    $keptOfStopped = [long] (Invoke-SqlText -As Scalar -Sql 'SELECT COUNT(*) FROM audit.RawLogFlattened WHERE AgentId IS NOT NULL OR AgentName IS NOT NULL;')
    Invoke-SqlText -Sql 'ALTER TABLE audit.AgentCatalog DROP CONSTRAINT CK_it_StopCatalog;'
    Invoke-SqlText -Sql 'EXEC audit.usp_BackfillAgentNames @BatchSize = 1000;'
    $ag3 = (Invoke-SqlText -As Table -Sql $agentSql).Rows[0]
    Assert-That ($stopped -and $keptOfStopped -eq 0) "2.4.12: a read stopped in its catalog step keeps nothing of that batch ($keptOfStopped record(s) kept)"
    Assert-That ([long] $ag3.Named -eq $exp23 -and "$($ag3.CatalogName)" -eq 'Expense Policy Helper' -and [int] $ag3.Agents -eq 1 -and [int] $ag3.Times -eq 1 -and [int] $ag3.Mark -eq 1) `
        "and the next run reads it again and catalogs its agent under its latest name ($($ag3.CatalogName))"
    Assert-That ((& $flatStamp) -eq $stamp0) 'with nothing else of any record changed'

    # 2.4.12: a read stopped after some batches carries on from where it got to. It started from the first record again,
    # which with @Others is most of Copilot Chat's history. A constraint on the saved position stands in for the stop,
    # after the first batch of 1,000; a record of that batch emptied after the stop shows the next run does not read it.
    Invoke-SqlText -Sql @"
UPDATE audit.RawLogFlattened SET AgentId = NULL, AgentName = NULL; DELETE FROM audit.AgentCatalog; DELETE FROM audit.PipelineSetting WHERE Name = N'AgentNamesReadUtc';
DECLARE @mid NVARCHAR(450) = (SELECT RecordId FROM audit.RawLogFlattened WHERE AppIdentity LIKE N'Copilot.Studio.%' ORDER BY RecordId OFFSET 1000 ROWS FETCH NEXT 1 ROWS ONLY);
DECLARE @c NVARCHAR(MAX) = N'ALTER TABLE audit.PipelineSetting WITH NOCHECK ADD CONSTRAINT CK_it_StopCursor CHECK (Name <> N''AgentNamesReadUtcCursor'' OR Value < N''' + REPLACE(@mid, N'''', N'''''') + N''');';
EXEC (@c);
"@
    $stoppedAt = try { Invoke-SqlText -Sql 'EXEC audit.usp_BackfillAgentNames @BatchSize = 1000;'; $null } catch { 'stopped' }
    $saved = (Invoke-SqlText -As Table -Sql @"
SELECT (SELECT Value FROM audit.PipelineSetting WHERE Name = N'AgentNamesReadUtcCursor') AS Cursor_,
       (SELECT COUNT(*) FROM audit.RawLogFlattened WHERE AgentName IS NOT NULL) AS NamedSoFar;
"@).Rows[0]
    Invoke-SqlText -Sql @"
ALTER TABLE audit.PipelineSetting DROP CONSTRAINT CK_it_StopCursor;
UPDATE audit.RawLogFlattened SET AgentName = NULL WHERE RecordId = (SELECT MIN(RecordId) FROM audit.RawLogFlattened WHERE AgentName IS NOT NULL);
"@
    Invoke-SqlText -Sql 'EXEC audit.usp_BackfillAgentNames @BatchSize = 1000;'
    $resumed = (Invoke-SqlText -As Table -Sql @"
SELECT (SELECT COUNT(*) FROM audit.RawLogFlattened WHERE AppIdentity LIKE N'Copilot.Studio.%' AND AgentName IS NULL) AS Unread,
       (SELECT COUNT(*) FROM audit.PipelineSetting WHERE Name IN (N'AgentNamesReadUtcCursor', N'AgentNamesReadUtcGained')) AS Left_,
       (SELECT COUNT(*) FROM audit.PipelineSetting WHERE Name = N'AgentNamesReadUtc') AS Mark;
"@).Rows[0]
    Assert-That ($stoppedAt -eq 'stopped' -and $saved.Cursor_ -isnot [DBNull] -and [long] $saved.NamedSoFar -eq 1000) `
        "2.4.12: a read stopped after its first batch keeps that batch and where it got to ($($saved.NamedSoFar) named)"
    Assert-That ([int] $resumed.Unread -eq 1 -and [int] $resumed.Left_ -eq 0 -and [int] $resumed.Mark -eq 1) `
        "and the next run carries on from there, not from the first record, and clears the position when done ($($resumed.Unread) left unread)"
    Invoke-SqlText -Sql 'EXEC audit.usp_BackfillAgentNames;'
    $ag4 = (Invoke-SqlText -As Table -Sql $agentSql).Rows[0]
    Assert-That ([long] $ag4.Named -eq $exp23 -and "$($ag4.CatalogName)" -eq 'Expense Policy Helper' -and [int] $ag4.Agents -eq 1) 'and a run after it reads what is still empty, from the first record'

    # IsCopilotLic fills in once the license table has the user.
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
    # 2.4.12: but a rebuild that empties the tables first is refused: it left nothing to skip the purged records by,
    # and flattened each one again blank, its columns NULL and its elements gone, with no JSON to do better from.
    $refusedRebuild = try { Invoke-SqlText -Sql 'EXEC audit.usp_FlattenCopilotAudit @RebuildAll = 1, @BatchSize = 50000' -TimeoutSeconds 1800; '' } catch { $_.Exception.GetBaseException().Message }
    $k2 = & $keep
    Assert-That ($refusedRebuild -like '*Rebuild refused*' -and [long] $k2.UserKey -eq [long] $k0.UserKey -and [long] $k2.Res -eq [long] $k0.Res -and [long] $k2.Elements -eq [long] $k0.Elements) `
        "2.4.12: @RebuildAll = 1 is refused after a JSON purge, and every purged record keeps its columns and elements ($refusedRebuild)"

    # The view still returns every row; the purged ones just have no AuditData.
    $viewAfter = Invoke-SqlText -As Table -TimeoutSeconds 900 -Sql @"
SELECT COUNT_BIG(*) AS Rows_, SUM(CASE WHEN AuditData IS NULL THEN 1 ELSE 0 END) AS NullJson
FROM audit.vCopilotAuditRecord;
"@
    Assert-That ([long] $viewAfter.Rows[0].Rows_ -eq $expectedTotal) 'the export view still returns every record after a purge'
    Assert-That ([long] $viewAfter.Rows[0].NullJson -eq $purged) 'AuditData is NULL on exactly the purged records'

    # Row deletion mode. Section 8 looks up the users audit.CopilotInteraction names, so its rows are kept aside
    # here and put back after.
    Invoke-SqlText -Sql 'DROP TABLE IF EXISTS dbo.KeepInteraction; SELECT * INTO dbo.KeepInteraction FROM audit.CopilotInteraction;' -TimeoutSeconds 900
    Invoke-SqlText -Sql 'EXEC audit.usp_PurgeCopilotAuditJson @RetainDays = 1, @DeleteRows = 1, @PurgeFlattened = 1, @WhatIf = 0' `
                   -TimeoutSeconds 1800
    $gone = Invoke-SqlText -As Table -Sql @"
SELECT (SELECT COUNT_BIG(*) FROM audit.CopilotAudit)       AS A,
       (SELECT COUNT_BIG(*) FROM audit.RawLogFlattened)    AS F,
       (SELECT COUNT_BIG(*) FROM audit.CopilotAuditStage)  AS S,
       (SELECT COUNT_BIG(*) FROM audit.CopilotInteraction) AS I,
       (SELECT COUNT_BIG(*) FROM audit.RawLogAccessedResource) AS E,
       (SELECT COUNT_BIG(*) FROM audit.RawLogContext)      AS C;
"@
    Assert-That ([long] $gone.Rows[0].A -eq 0) "@DeleteRows = 1 removed every aged record ($($gone.Rows[0].A) left)"
    Assert-That ([long] $gone.Rows[0].F -eq 0) "@PurgeFlattened = 1 removed the matching flattened rows ($($gone.Rows[0].F) left)"
    Assert-That ([long] $gone.Rows[0].S -eq 0) "@PurgeStaging cleared the second copy in staging ($($gone.Rows[0].S) left)"
    # 2.4.12: every row made from the records goes with them. Before, each record's shredded row (user, app, thread,
    # time) and the resources and contexts it read (file names, sites, links) were all kept.
    Assert-That ([long] $gone.Rows[0].I -eq 0) "2.4.12: @DeleteRows = 1 removed the records' shredded rows from audit.CopilotInteraction ($($gone.Rows[0].I) left)"
    Assert-That ([long] $gone.Rows[0].E -eq 0 -and [long] $gone.Rows[0].C -eq 0) "and @PurgeFlattened = 1 the resources and contexts they read ($($gone.Rows[0].E) and $($gone.Rows[0].C) left)"
    Invoke-SqlText -Sql 'INSERT INTO audit.CopilotInteraction SELECT * FROM dbo.KeepInteraction; DROP TABLE dbo.KeepInteraction;' -TimeoutSeconds 900

    $refusedFlat = $false
    try { Invoke-SqlText -Sql 'EXEC audit.usp_PurgeCopilotAuditJson @RetainDays = 1, @PurgeFlattened = 1, @WhatIf = 0' }
    catch { $refusedFlat = $true }
    Assert-That $refusedFlat 'purge refuses @PurgeFlattened without @DeleteRows'

    # -----------------------------------------------------------------------
    Write-Host "`n8. Licenses from Graph, users from AD (tools/Update-CopilotAuditEnrichment.ps1, tools/Export-UsersWithHierarchy-ToSql.ps1)" -ForegroundColor Cyan

    # Stand in for the Graph SDK, pointing at mock_graph.py. Same approach as
    # Test-Orchestrator: shadow the cmdlets with global functions. Every path
    # asked for is recorded, to prove the license tool asks for nothing else.
    $script:MockBase = $mock.Base
    $script:GraphPaths = [System.Collections.Generic.List[string]]::new()
    $script:GraphBatches = 0
    function global:Connect-MgGraph { param([hashtable] $Splat) }
    function global:Invoke-MgGraphRequest {
        param([string] $Method, [string] $Uri, $OutputType, $Body, $ContentType)
        if ($Uri -eq '/v1.0/$batch') {
            # 2.4.2 looks users up twenty to a batch: record what each item asks for.
            foreach ($q in ($Body | ConvertFrom-Json).requests) { $script:GraphPaths.Add((('/v1.0' + $q.url) -replace '\?.*$', '')) }
            $script:GraphBatches++
        }
        else { $script:GraphPaths.Add(($Uri -replace '\?.*$', '')) }
        $full = if ($Uri -match '^https?://') { $Uri } else { "$script:MockBase$Uri" }
        $extra = if ($Body) { @{ Body = $Body; ContentType = 'application/json' } } else { @{} }
        $r = Invoke-WebRequest -Uri $full -Method $Method -SkipHttpErrorCheck -ErrorAction Stop @extra
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
      # $database - after which every query runs against the wrong catalog
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
        # 2.4.8: by the Copilot SKU's own plan ids - their services are not named for Copilot - and the earliest of them:
        # not a deleted assignment, and not a plan another SKU carries too.
        $sinceDays = "$(Invoke-SqlText -As Scalar -Sql "SELECT STRING_AGG(CAST(d AS varchar(2)), ',') WITHIN GROUP (ORDER BY d) FROM (SELECT DISTINCT DAY(LicensedSinceUtc) AS d, YEAR(LicensedSinceUtc) AS y FROM audit.CopilotLicense WHERE IsCopilotLic = 1) AS x WHERE y = 2026;")"
        $sinceOther = [int] (Invoke-SqlText -As Scalar -Sql 'SELECT COUNT(*) FROM audit.CopilotLicense WHERE LicensedSinceUtc IS NOT NULL AND (IsCopilotLic = 0 OR YEAR(LicensedSinceUtc) <> 2026);')
        Assert-That ($sinceDays -eq '1' -and $sinceOther -eq 0) "licensed since is the earliest live plan of the Copilot SKU's own (day $sinceDays of the month; $sinceOther from a deleted or shared plan)"
        Assert-That ([int] $l.Enabled_ -eq 17) "CapabilityStatus captured ($($l.Enabled_))"
        Assert-That ([int] $l.Gone -eq 1) "the departed user is recorded as NotFound ($($l.Gone))"
        $held = (Invoke-SqlText -As Table -Sql "SELECT COUNT(*) AS Rows_, COUNT(DISTINCT UserPrincipalName) AS People, SUM(CASE WHEN s.IsCopilot = 1 THEN 1 ELSE 0 END) AS Copilot FROM audit.UserLicenseSku AS u JOIN audit.LicenseSku AS s ON s.SkuId = u.SkuId;").Rows[0]
        Assert-That ([int] $held.Rows_ -eq 66 -and [int] $held.People -eq 49 -and [int] $held.Copilot -eq 17) "every SKU each person holds is kept: E3 for the 49 found, Copilot for 17 ($($held.Rows_) rows, $($held.People) people)"

        # 2.3.0: the seat counts /subscribedSkus already returns, and each person's Entra account state and hire date.
        $pool = (Invoke-SqlText -As Table -Sql @"
SELECT (SELECT COUNT(*) FROM audit.LicenseSku) AS Skus,
       (SELECT Enabled + Warning FROM audit.LicenseSku WHERE IsCopilot = 1) AS Bought,
       (SELECT Consumed FROM audit.LicenseSku WHERE IsCopilot = 1) AS Assigned,
       (SELECT COUNT(*) FROM audit.LicenseSkuDay WHERE SnapshotDate = CAST(SYSUTCDATETIME() AS DATE)) AS Today,
       (SELECT COUNT(*) FROM audit.CopilotLicense WHERE EntraAccountEnabled = 0) AS Disabled,
       (SELECT COUNT(*) FROM audit.CopilotLicense WHERE EmployeeHireDate IS NOT NULL) AS Hired;
"@).Rows[0]
        Assert-That ([int] $pool.Skus -eq 2 -and [int] $pool.Bought -eq 105 -and [int] $pool.Assigned -eq 17) "the license pool is kept per SKU: 105 Copilot seats bought (100 + 5 in grace), 17 assigned ($($pool.Bought), $($pool.Assigned))"
        Assert-That ([int] $pool.Today -eq 2) 'and a snapshot per SKU for today, for the trend'
        Assert-That ([int] $pool.Disabled -eq 3 -and [int] $pool.Hired -gt 0) "Entra's accountEnabled and employeeHireDate are recorded ($($pool.Disabled) disabled, $($pool.Hired) with a hire date)"
        Assert-That ([int] $l.GoneUnknown -eq 1) 'a departed user is left unknown, NOT reported as unlicensed'

        # Licenses only: nothing that needs more than User.Read.All and
        # LicenseAssignment.Read.All, and no Entra profile tables.
        $asked = @($script:GraphPaths | Sort-Object -Unique)
        $other = @($asked | Where-Object { $_ -ne '/v1.0/subscribedSkus' -and $_ -notmatch '^/v1\.0/users/[^/]+$' })
        Assert-That ($other.Count -eq 0) "the license tool calls only /subscribedSkus and /users/{upn} - no manager, profile or administrative-unit call ($($other -join ', '))"
        Assert-That ($script:GraphBatches -eq 5) "the 50 users are looked up twenty to a `$batch request, a dropped request and a throttled item retried ($($script:GraphBatches) batches: 3, and 2 retries)"
        $entraTables = [int] (Invoke-SqlText -As Scalar -Sql "SELECT COUNT(*) FROM sys.tables WHERE SCHEMA_NAME(schema_id) = 'audit' AND name IN ('UserProfile', 'AdministrativeUnit');")
        Assert-That ($entraTables -eq 0) 'no Entra profile or administrative-unit table is created'

        # Re-running must update in place, not duplicate.
        Invoke-Main
        $again = [int] (Invoke-SqlText -As Scalar -Sql 'SELECT COUNT(*) FROM audit.CopilotLicense')
        Assert-That ($again -eq 50) "a second run updated in place rather than duplicating ($again)"

        # -AllLicensedUsers exercises the license-holder enumeration path.
        $AllLicensedUsers = $true
        Invoke-Main
        $withAll = [int] (Invoke-SqlText -As Scalar -Sql 'SELECT COUNT(*) FROM audit.CopilotLicense')
        Assert-That ($withAll -eq 50) "-AllLicensedUsers added no phantom users here ($withAll)"

        # 2.4.3: the leave date is written only when asked for, and a run without it keeps it.
        $leaves = [int] (Invoke-SqlText -As Scalar -Sql 'SELECT COUNT(*) FROM audit.CopilotLicense WHERE EmployeeLeaveUtc IS NOT NULL')
        $IncludeLeaveDates = $true
        Invoke-Main
        $leavesOn = [int] (Invoke-SqlText -As Scalar -Sql 'SELECT COUNT(*) FROM audit.CopilotLicense WHERE EmployeeLeaveUtc IS NOT NULL')
        $IncludeLeaveDates = $false
        Invoke-Main
        $leavesKept = [int] (Invoke-SqlText -As Scalar -Sql 'SELECT COUNT(*) FROM audit.CopilotLicense WHERE EmployeeLeaveUtc IS NOT NULL')
        Assert-That ($leaves -eq 0 -and $leavesOn -eq 5 -and $leavesKept -eq 5) "-IncludeLeaveDates writes Entra's leave dates, and a run without it keeps them ($leaves, $leavesOn, $leavesKept)"

        # 2.4.12: a license taken back from someone who never used Copilot is recorded, and so is a person who left.
        # Only the users in the audit data and today's license holders were looked up, so their rows said Licensed for
        # good. user50 has no interactions and, in the mock, no Copilot license now; gone@ is no longer in the directory.
        Invoke-SqlText -Sql @"
INSERT INTO audit.CopilotLicense (UserPrincipalName, IsCopilotLic, SkuPartNumber, LicensedSinceUtc, DirectoryStatus) VALUES
    (N'user50@contoso.com', 1, N'Microsoft_365_Copilot', '2026-01-01T00:00:00', N'Found'),
    (N'gone@contoso.com',   1, N'Microsoft_365_Copilot', '2026-01-01T00:00:00', N'Found');
"@
        Invoke-Main
        $reclaim = (Invoke-SqlText -As Table -Sql @"
SELECT (SELECT CONCAT(ISNULL(CAST(IsCopilotLic AS VARCHAR(4)), 'NULL'), '/', DirectoryStatus) FROM audit.CopilotLicense WHERE UserPrincipalName = N'user50@contoso.com') AS Reclaimed,
       (SELECT CONCAT(ISNULL(CAST(IsCopilotLic AS VARCHAR(4)), 'NULL'), '/', DirectoryStatus) FROM audit.CopilotLicense WHERE UserPrincipalName = N'gone@contoso.com') AS Gone_;
"@).Rows[0]
        Assert-That ("$($reclaim.Reclaimed)" -eq '0/Found' -and "$($reclaim.Gone_)" -eq 'NULL/NotFound') `
            "2.4.12: a reclaimed license is recorded as unlicensed ($($reclaim.Reclaimed)), and a person who left as not found ($($reclaim.Gone_))"
        Invoke-SqlText -Sql "DELETE FROM audit.CopilotLicense WHERE UserPrincipalName IN (N'user50@contoso.com', N'gone@contoso.com'); DELETE FROM audit.UserLicenseSku WHERE UserPrincipalName = N'user50@contoso.com';"
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

        # 2.5.0: Department holds 200 characters, so a department from a custom attribute, longer than AD's 64, loads; and a
        # table of the 2.4.12 width (64) is widened by the next load. The 50 users are put back after: later sections read them.
        Invoke-SqlText -Sql 'ALTER TABLE audit.UserDirectory ALTER COLUMN Department NVARCHAR(64) NULL;'
        $oldBytes = [int] (Invoke-SqlText -As Scalar -Sql "SELECT COL_LENGTH(N'audit.UserDirectory', N'Department')")
        $mid = $entries[0].PSObject.Copy(); $mid.dept = 'm' * 150
        $big = $entries[1].PSObject.Copy(); $big.dept = 'b' * 250
        [void] (Write-UserDirectory -ConnString $connStr -Table (ConvertTo-UserDirectoryTable -Entries @($mid, $big) -EmployeeTypes @()).Table)
        $newBytes = [int] (Invoke-SqlText -As Scalar -Sql "SELECT COL_LENGTH(N'audit.UserDirectory', N'Department')")
        $lens = @((Invoke-SqlText -As Table -Sql 'SELECT LEN(Department) AS L FROM audit.UserDirectory ORDER BY L').Rows | ForEach-Object { [int] $_.L })
        Assert-That ($oldBytes -eq 128 -and $newBytes -eq 400) "an older audit.UserDirectory (Department 64) is widened to 200 by the next load ($oldBytes -> $newBytes bytes)"
        Assert-That (($lens -join ',') -eq '150,200') "a 150-character department loads, and one over 200 is cut to 200 ($($lens -join ','))"
        [void] (Write-UserDirectory -ConnString $connStr -Table $rows.Table)
    }

    # The views that make this worth having: licenses from Graph, departments from AD.
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
    Write-Host "`n9. License lifecycle events (audit.usp_ShredLicenseEvents)" -ForegroundColor Cyan

    Invoke-SqlScript -Sql (Get-Content -LiteralPath (Join-Path $root 'sql' 'Create-LicenseEventSchema.sql') -Raw)
    Invoke-SqlScript -Sql (Get-Content -LiteralPath (Join-Path $root 'sql' 'Create-LicenseEventProcedure.sql') -Raw)

    # Section 7 emptied audit.CopilotAudit, so it is free for AzureActiveDirectory
    # records - which is what that table holds in a license-operations database.
    #
    # The fixtures reproduce the awkward part of the real data: 'Change user
    # license' carries no ModifiedProperties at all, and the license lists
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
        # detail-free change, paired with the update that carries the licenses
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
    Assert-That ([int] $v.Changes -eq 2) "the trailing period on 'Change user license.' is normalized away ($($v.Changes) of 2)"
    Assert-That ([int] $v.Adds -eq 1 -and [int] $v.Deletes -eq 1) 'provisioning and revocation events are distinguishable'
    Assert-That ([int] $v.WithTarget -eq 5) "the affected user resolved on every event, including the GUID ObjectId one ($($v.WithTarget))"
    Assert-That ([int] $v.WithActor -eq 5) 'the actor who made each change is recorded'

    $guidTarget = Invoke-SqlText -As Scalar -Sql "SELECT TargetUpn FROM audit.LicenseEvent WHERE RecordId = 'evt-0003'"
    Assert-That ("$guidTarget" -eq 'user2@contoso.com') "a GUID ObjectId falls back to Target[] for the UPN ($guidTarget)"

    $props = Invoke-SqlText -As Table -Sql @"
SELECT COUNT(*) AS Rows_, COUNT(DISTINCT RecordId) AS Records,
       SUM(CASE WHEN [Name] = 'AssignedLicense' THEN 1 ELSE 0 END) AS Licenses
FROM audit.LicenseEventProperty;
"@
    Assert-That ([int] $props.Rows[0].Rows_ -eq 4) "ModifiedProperties shredded generically, whatever the names ($($props.Rows[0].Rows_))"
    Assert-That ([int] $props.Rows[0].Licenses -eq 2) 'the license property came through on both records that carry it'

    # The point of the view: the detail-free record still reports its licenses,
    # taken from the paired record.
    # Every license property of the pair comes across (both AssignedLicense and
    # AssignedPlan), not just the first.
    $paired = Invoke-SqlText -As Table -Sql @"
SELECT PropertyName, NewValue, FromPairedRecord, TargetUpn
FROM audit.vLicenseChange WHERE RecordId = 'evt-0001' ORDER BY PropertyName;
"@
    $pairedLicense = @($paired.Rows | Where-Object PropertyName -eq 'AssignedLicense')
    Assert-That ($paired.Rows.Count -eq 2 -and $pairedLicense.Count -eq 1) `
        "a detail-free 'Change user license' picks up all its license properties from the paired record ($(@($paired.Rows | ForEach-Object PropertyName) -join ', '))"
    Assert-That (@($paired.Rows | Where-Object { [int] $_.FromPairedRecord -ne 1 }).Count -eq 0) 'and they are marked as coming from the pair, not invented'
    Assert-That ("$($pairedLicense[0].NewValue)" -like '*Copilot*') "the new license value is readable ($($pairedLicense[0].NewValue))"

    $life = Invoke-SqlText -As Table -Sql @"
SELECT TargetUpn, AccountCreatedUtc, AccountDeletedUtc, LicenseChangeCount
FROM audit.vLicenseLifecycle WHERE TargetUpn = 'user3@contoso.com';
"@
    Assert-That ($life.Rows.Count -eq 1 -and $life.Rows[0].AccountCreatedUtc -isnot [DBNull] -and $life.Rows[0].AccountDeletedUtc -isnot [DBNull]) `
        'vLicenseLifecycle reports both provisioning and revocation for a user'

    # -----------------------------------------------------------------------
    # 9b. The shapes a live tenant actually produced (1,042 records, 180 days).
    #
    # Most license changes arrive CHUNKED: too large for one record, so Entra
    # writes several with empty ModifiedProperties and an additionalDetails of
    # {"id","seq","b"}, where the b fragments joined in seq order are one JSON
    # document whose targetUpdatedProperties holds the change. Each change is
    # also raised twice - 'Change user license' and 'Update user' - with the
    # same detail and InterSystemsId. And 'Update user' is mostly not about
    # licenses at all (Authenticator devices and the like), a failed
    # assignment records only MethodExecutionResult, and the operation filter
    # for 'Add user' also returns 'Add user sponsor'.
    Write-Host "`n9b. Chunked, duplicated and non-license directory events" -ForegroundColor Cyan

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
        "every license property is reported, not just the first ($($u4.Rows.Count) rows)"
    Assert-That (@($u4.Rows | Where-Object { [int] $_.FromChunks -eq 1 }).Count -eq 2) 'and marked as reassembled from chunks'
    Assert-That ("$(($u4.Rows | Where-Object PropertyName -eq 'AssignedLicense').OldValue)" -like '*Microsoft_365_Copilot*') `
        'the old license list from the reassembled document is readable'

    $sku = Invoke-SqlText -As Table -Sql "SELECT Sku, Change FROM audit.vLicenseSkuChange WHERE TargetUpn = 'user4@contoso.com';"
    Assert-That ($sku.Rows.Count -eq 1 -and "$($sku.Rows[0].Sku)" -eq 'Microsoft_365_Copilot' -and "$($sku.Rows[0].Change)" -eq 'Removed') `
        "vLicenseSkuChange reads the license lists as 'Copilot removed', with the retained SKU unchanged ($(@($sku.Rows | ForEach-Object { "$($_.Sku) $($_.Change)" }) -join '; '))"

    $noise = Invoke-SqlText -As Scalar -Sql "SELECT COUNT(*) FROM audit.vLicenseChange WHERE RecordId IN ('evt-0106', 'evt-0107', 'evt-0108', 'evt-0109');"
    Assert-That ([int] $noise -eq 0) "an Authenticator update, a failed assignment and an incomplete group are not license changes ($noise row(s))"

    $life4 = Invoke-SqlText -As Table -Sql @"
SELECT (SELECT LicenseChangeCount FROM audit.vLicenseLifecycle WHERE TargetUpn = 'user4@contoso.com') AS U4Changes,
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
    # 9c. 2.5.4: the SKU changes are kept in a table. The session build and the bi refresh each read every one of them
    # every night, and audit.vLicenseSkuChange worked them out from the records each time: 11 minutes over 4,000,000
    # license events in the lab, whatever the night's load. audit.usp_RefreshLicenseSkuChange works them out after each
    # shred instead, for the events shredded since it last ran and the records raised with them. The rule itself is
    # kept as audit.vLicenseSkuChangeFromRecords, and every check here is against it.
    Write-Host "`n9c. The SKU changes kept in a table (audit.usp_RefreshLicenseSkuChange)" -ForegroundColor Cyan

    # Rows the rule gives and rows kept that the other side lacks. The rule gives a record's change once per
    # AssignedLicense property it has (its own and its chunks'); the table keeps it once, FromChunks when either was.
    $skuDiffSql = @'
WITH r AS (SELECT RecordId, CAST(CreatedDateTime AS DATETIME2(0)) AS CreatedDateTime, ActivityDate, ActorUpn, TargetUpn, MAX(CAST(FromChunks AS INT)) AS FromChunks,
                  CAST(LEFT(Sku, 200) AS NVARCHAR(200)) AS Sku, CAST(Change AS NVARCHAR(7)) AS Change
           FROM audit.vLicenseSkuChangeFromRecords
           GROUP BY RecordId, CAST(CreatedDateTime AS DATETIME2(0)), ActivityDate, ActorUpn, TargetUpn, CAST(LEFT(Sku, 200) AS NVARCHAR(200)), CAST(Change AS NVARCHAR(7))),
     k AS (SELECT RecordId, CreatedDateTime, ActivityDate, ActorUpn, TargetUpn, FromChunks, Sku, Change FROM audit.LicenseSkuChange)
SELECT (SELECT COUNT(*) FROM (SELECT * FROM r EXCEPT SELECT * FROM k) AS a) + (SELECT COUNT(*) FROM (SELECT * FROM k EXCEPT SELECT * FROM r) AS b) AS Differ,
       (SELECT COUNT(*) FROM k) AS Kept,
       (SELECT COUNT(*) FROM audit.vLicenseSkuChange) AS ViewRows,
       (SELECT CASE WHEN OBJECT_DEFINITION(OBJECT_ID(N'audit.vLicenseSkuChange')) LIKE N'%vLicenseSkuChangeFromRecords%' THEN 'records' ELSE 'table' END) AS ViewReads;
'@
    $skuState = { param($Catalog) (Invoke-SqlText -Catalog $Catalog -As Table -Sql $skuDiffSql).Rows[0] }
    $skuRun = { param($Catalog) (Invoke-SqlText -Catalog $Catalog -As Table -Sql 'SELECT TOP (1) RunId, SinceUtc, Events, Changes FROM audit.LicenseSkuChangeRun ORDER BY RunId DESC;').Rows[0] }
    $skuRows = { param($Catalog, $RecordId) "$(Invoke-SqlText -Catalog $Catalog -As Scalar -Sql "SELECT STRING_AGG(CONCAT(Sku, N' ', Change, N' ', TargetUpn, N' ', FromChunks), N'; ') WITHIN GROUP (ORDER BY Change, Sku) FROM audit.LicenseSkuChange WHERE RecordId = '$RecordId';")" }

    # This database, after the shreds above: user4's chunked change is the one SKU change in it.
    $st = & $skuState $script:ITDatabase
    $run = & $skuRun $script:ITDatabase
    Assert-That ([int] $st.Differ -eq 0 -and [int] $st.Kept -eq 1 -and "$($st.ViewReads)" -eq 'table' -and [int] $st.ViewRows -eq 1) `
        "2.5.4: a shred keeps the SKU changes the rule reads from the records, and the view reads them from the table ($($st.Kept) kept, $($st.Differ) differ, the view reads the $($st.ViewReads))"
    Assert-That ((& $skuRows $script:ITDatabase 'evt-0102') -eq 'Microsoft_365_Copilot Removed user4@contoso.com 1' -and $run.SinceUtc -is [DBNull] -and [int] $run.Events -eq 15) `
        "a shred of every record works out every event ($($run.Events) events: $(& $skuRows $script:ITDatabase 'evt-0102'))"

    # The rest in a license database of its own, so the events added here are in no later section's counts.
    $skuDb = "$($script:ITDatabase)_Sku"
    $runIn = { param($Db, $File) foreach ($b in [regex]::Split((Get-Content -LiteralPath (Join-Path $root 'sql' $File) -Raw), '(?im)^[ \t]*GO[ \t]*(?:--[^\r\n]*)?\r?$')) { if ($b.Trim()) { Invoke-SqlText -Catalog $Db -Sql $b } } }
    try {
        Invoke-SqlText -Catalog 'master' -Sql "IF DB_ID(N'$skuDb') IS NOT NULL BEGIN ALTER DATABASE [$skuDb] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [$skuDb]; END; CREATE DATABASE [$skuDb]; ALTER DATABASE [$skuDb] SET RECOVERY SIMPLE;"
        foreach ($f in 'Create-CopilotAuditSchema.sql', 'Create-CopilotCsvImport.sql', 'Create-LicenseEventSchema.sql', 'Create-LicenseEventProcedure.sql') { & $runIn $skuDb $f }
        $st = & $skuState $skuDb
        Assert-That ("$($st.ViewReads)" -eq 'table' -and [int] (Invoke-SqlText -Catalog $skuDb -As Scalar -Sql 'SELECT COUNT(*) FROM audit.LicenseSkuChangeRun;') -eq 0) `
            'a new license database reads the table from the start, with nothing to work out yet'

        # Every case of the rule, written as events: 35 that matter and 70 that are not license changes.
        Invoke-SqlText -Catalog $skuDb -Sql @'
DECLARE @e5 NVARCHAR(200) = N'[SkuName=SPE_E5, AccountId=a, SkuId=1, DisabledPlans=[]]',
        @cop NVARCHAR(200) = N'[SkuName=Microsoft_365_Copilot, AccountId=a, SkuId=2, DisabledPlans=[]]',
        @viz NVARCHAR(200) = N'[SkuName=VISIOCLIENT, AccountId=a, SkuId=3, DisabledPlans=[]]';
DECLARE @old NVARCHAR(MAX) = CONCAT(N'["', @e5, N'"]'), @new NVARCHAR(MAX) = CONCAT(N'["', @e5, N'","', @cop, N'"]'),
        @swapOld NVARCHAR(MAX) = CONCAT(N'["', @e5, N'","', @viz, N'"]'), @swapNew NVARCHAR(MAX) = CONCAT(N'["', @e5, N'","', @cop, N'"]');

INSERT INTO audit.LicenseEvent (RecordId, CreatedDateTime, ActivityDate, Operation, ResultStatus, ActorUpn, TargetUpn, CorrelationId, ChunkGroupId, ChunkSeq, ShreddedUtc) VALUES
 /* 1. a license change with its own lists */
 ('c01',  '2026-09-01T10:00:00', '2026-09-01', N'Change user license', N'Success', N'admin@c.com', N'u01@c.com', N'k01', NULL, NULL, '2026-01-01'),
 /* 2. a detail-free license change borrows from the Update user raised with it, which gives way */
 ('c02',  '2026-09-01T10:00:00', '2026-09-01', N'Change user license', N'Success', N'admin@c.com', N'u02@c.com', N'k02', NULL, NULL, '2026-01-01'),
 ('u02',  '2026-09-01T10:00:01', '2026-09-01', N'Update user',         N'Success', N'admin@c.com', N'u02@c.com', N'k02', NULL, NULL, '2026-01-01'),
 /* 3. an Update user alone, with lists: it is the change */
 ('u03',  '2026-09-01T10:00:00', '2026-09-01', N'Update user',         N'Success', N'admin@c.com', N'u03@c.com', N'k03', NULL, NULL, '2026-01-01'),
 /* 4. a chunk group of three: its first record carries the reassembled lists */
 ('c04a', '2026-09-01T10:00:00', '2026-09-01', N'Change user license', N'Success', N'admin@c.com', N'u04@c.com', N'k04', N'g04', 1, '2026-01-01'),
 ('c04b', '2026-09-01T10:00:00', '2026-09-01', N'Change user license', N'Success', N'admin@c.com', N'u04@c.com', N'k04', N'g04', 2, '2026-01-01'),
 ('c04c', '2026-09-01T10:00:00', '2026-09-01', N'Change user license', N'Success', N'admin@c.com', N'u04@c.com', N'k04', N'g04', 3, '2026-01-01'),
 /* 5. two chunk groups under one CorrelationId: the license change's carries nothing and borrows from the Update user's */
 ('c05a', '2026-09-01T10:00:00', '2026-09-01', N'Change user license', N'Success', N'admin@c.com', N'u05@c.com', N'k05', N'g05c', 1, '2026-01-01'),
 ('c05b', '2026-09-01T10:00:00', '2026-09-01', N'Change user license', N'Success', N'admin@c.com', N'u05@c.com', N'k05', N'g05c', 2, '2026-01-01'),
 ('u05a', '2026-09-01T10:00:02', '2026-09-01', N'Update user',         N'Success', N'admin@c.com', N'u05@c.com', N'k05', N'g05u', 1, '2026-01-01'),
 ('u05b', '2026-09-01T10:00:02', '2026-09-01', N'Update user',         N'Success', N'admin@c.com', N'u05@c.com', N'k05', N'g05u', 2, '2026-01-01'),
 /* 6. it must not borrow from its own chunk group */
 ('c06a', '2026-09-01T10:00:00', '2026-09-01', N'Change user license', N'Success', N'admin@c.com', N'u06@c.com', N'k06', N'g06', 1, '2026-01-01'),
 ('c06b', '2026-09-01T10:00:00', '2026-09-01', N'Change user license', N'Success', N'admin@c.com', N'u06@c.com', N'k06', N'g06', 2, '2026-01-01'),
 /* 7. no user on the change: the first one a record raised with it carries */
 ('c07',  '2026-09-01T10:00:05', '2026-09-01', N'Change user license', N'Success', N'admin@c.com', NULL,           N'k07', NULL, NULL, '2026-01-01'),
 ('x07',  '2026-09-01T10:00:01', '2026-09-01', N'Add user',            N'Success', N'admin@c.com', N'u07@c.com',   N'k07', NULL, NULL, '2026-01-01'),
 ('y07',  '2026-09-01T10:00:03', '2026-09-01', N'Add user',            N'Success', N'admin@c.com', N'other@c.com', N'k07', NULL, NULL, '2026-01-01'),
 /* 8 and 9. two records to borrow from: the earlier; on a tie, the lower id */
 ('c08',  '2026-09-01T10:00:00', '2026-09-01', N'Change user license', N'Success', N'admin@c.com', N'u08@c.com', N'k08', NULL, NULL, '2026-01-01'),
 ('d08b', '2026-09-01T10:00:09', '2026-09-01', N'Add user',            N'Success', N'admin@c.com', N'u08@c.com', N'k08', NULL, NULL, '2026-01-01'),
 ('d08a', '2026-09-01T10:00:04', '2026-09-01', N'Add user',            N'Success', N'admin@c.com', N'u08@c.com', N'k08', NULL, NULL, '2026-01-01'),
 ('c09',  '2026-09-01T10:00:00', '2026-09-01', N'Change user license', N'Success', N'admin@c.com', N'u09@c.com', N'k09', NULL, NULL, '2026-01-01'),
 ('d09b', '2026-09-01T10:00:04', '2026-09-01', N'Add user',            N'Success', N'admin@c.com', N'u09@c.com', N'k09', NULL, NULL, '2026-01-01'),
 ('d09a', '2026-09-01T10:00:04', '2026-09-01', N'Add user',            N'Success', N'admin@c.com', N'u09@c.com', N'k09', NULL, NULL, '2026-01-01'),
 /* 10. a license-bearing property that is not a list: detail of its own, so it does not borrow, and has no SKU change */
 ('c10',  '2026-09-01T10:00:00', '2026-09-01', N'Change user license', N'Success', N'admin@c.com', N'u10@c.com', N'k10', NULL, NULL, '2026-01-01'),
 ('d10',  '2026-09-01T10:00:01', '2026-09-01', N'Add user',            N'Success', N'admin@c.com', N'u10@c.com', N'k10', NULL, NULL, '2026-01-01'),
 /* 11 and 12. lists that are not JSON; an entry with no SkuName */
 ('c11',  '2026-09-01T10:00:00', '2026-09-01', N'Change user license', N'Success', N'admin@c.com', N'u11@c.com', N'k11', NULL, NULL, '2026-01-01'),
 ('c12',  '2026-09-01T10:00:00', '2026-09-01', N'Change user license', N'Success', N'admin@c.com', N'u12@c.com', N'k12', NULL, NULL, '2026-01-01'),
 /* 13. one SKU out, another in */
 ('c13',  '2026-09-01T10:00:00', '2026-09-01', N'Change user license', N'Success', N'admin@c.com', N'u13@c.com', N'k13', NULL, NULL, '2026-01-01'),
 /* 14. two list properties on one record, one from the record and one from its chunks */
 ('c14',  '2026-09-01T10:00:00', '2026-09-01', N'Change user license', N'Success', N'admin@c.com', N'u14@c.com', N'k14', N'g14', 1, '2026-01-01'),
 /* 15 and 16. no CorrelationId: its own lists only */
 ('c15',  '2026-09-01T10:00:00', '2026-09-01', N'Change user license', N'Success', N'admin@c.com', N'u15@c.com', NULL, NULL, NULL, '2026-01-01'),
 ('c16',  '2026-09-01T10:00:00', '2026-09-01', N'Change user license', N'Success', N'admin@c.com', N'u16@c.com', NULL, NULL, NULL, '2026-01-01'),
 /* 17. a later fragment whose first record is not loaded: not a change */
 ('c17b', '2026-09-01T10:00:00', '2026-09-01', N'Change user license', N'Success', N'admin@c.com', N'u17@c.com', N'k17', N'g17', 2, '2026-01-01'),
 /* 18. an Update user whose only license-change sibling is a later fragment: it does not give way */
 ('u18',  '2026-09-01T10:00:00', '2026-09-01', N'Update user',         N'Success', N'admin@c.com', N'u18@c.com', N'k18', NULL, NULL, '2026-01-01'),
 ('c18b', '2026-09-01T10:00:00', '2026-09-01', N'Change user license', N'Success', N'admin@c.com', N'u18@c.com', N'k18', N'g18', 2, '2026-01-01'),
 /* 19 and 20. records that are not license changes */
 ('n19',  '2026-09-01T10:00:00', '2026-09-01', N'Update user',         N'Success', N'sync@c.com',  N'u19@c.com', N'k19', NULL, NULL, '2026-01-01'),
 ('n20',  '2026-09-01T10:00:00', '2026-09-01', N'Delete user',         N'Success', N'admin@c.com', N'u20@c.com', N'k20', NULL, NULL, '2026-01-01');

INSERT INTO audit.LicenseEventProperty (RecordId, Ordinal, [Name], OldValue, NewValue, Source) VALUES
 ('c01',  0, N'AssignedLicense', @old, @new, 'record'), ('c01', 1, N'AssignedPlan', N'[]', N'["p"]', 'record'),
 ('u02',  0, N'AssignedLicense', @old, @new, 'record'), ('u02', 1, N'Included Updated Properties', N'', N'AssignedLicense', 'record'),
 ('u03',  0, N'AssignedLicense', @new, @old, 'record'),
 ('c04a', 10000, N'AssignedLicense', @old, @new, 'chunks'), ('c04a', 10001, N'AssignedPlan', N'[]', N'["p"]', 'chunks'),
 ('u05a', 10000, N'AssignedLicense', @old, @new, 'chunks'),
 ('c06b', 0, N'AssignedLicense', @old, @new, 'record'),
 ('c07',  0, N'AssignedLicense', @old, @new, 'record'),
 ('d08a', 0, N'AssignedLicense', @old, @new, 'record'), ('d08b', 0, N'AssignedLicense', @new, @old, 'record'),
 ('d09a', 0, N'AssignedLicense', @old, @new, 'record'), ('d09b', 0, N'AssignedLicense', @new, @old, 'record'),
 ('c10',  0, N'LicenseAssignmentDetail', N'[]', N'["x"]', 'record'), ('d10', 0, N'AssignedLicense', @old, @new, 'record'),
 ('c11',  0, N'AssignedLicense', N'not json', N'[SkuName=SPE_E5, AccountId=a]', 'record'),
 ('c12',  0, N'AssignedLicense', N'["[AccountId=a, SkuId=1]"]', CONCAT(N'["[AccountId=a, SkuId=1]","', @cop, N'"]'), 'record'),
 ('c13',  0, N'AssignedLicense', @swapOld, @swapNew, 'record'),
 ('c14',  0, N'AssignedLicense', @old, @new, 'record'), ('c14', 10000, N'AssignedLicense', @swapOld, @swapNew, 'chunks'),
 ('c15',  0, N'AssignedLicense', @old, @new, 'record'),
 ('c18b', 0, N'AssignedLicense', @old, @new, 'record'), ('u18', 0, N'AssignedLicense', @old, @new, 'record'),
 ('n19',  0, N'LastDirSyncTime', N'a', N'b', 'record'), ('n19', 1, N'TargetId.UserType', N'', N'Member', 'record');

INSERT INTO audit.LicenseEvent (RecordId, CreatedDateTime, ActivityDate, Operation, ResultStatus, ActorUpn, TargetUpn, CorrelationId, ShreddedUtc)
SELECT CONCAT('z', 100 + v.n), '2026-08-01T08:00:00', '2026-08-01', N'Update user', N'Success', N'sync@c.com', CONCAT(N'z', v.n, N'@c.com'), CONCAT(N'kz', v.n), '2026-01-01'
FROM (SELECT TOP (70) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS n FROM sys.all_columns) AS v;
INSERT INTO audit.LicenseEventProperty (RecordId, Ordinal, [Name], OldValue, NewValue, Source)
SELECT e.RecordId, 0, N'LastDirSyncTime', N'a', N'b', 'record' FROM audit.LicenseEvent AS e WHERE e.RecordId LIKE 'z%';

EXEC audit.usp_RefreshLicenseSkuChange;
'@
        $st = & $skuState $skuDb
        $run = & $skuRun $skuDb
        Assert-That ([int] $st.Differ -eq 0 -and [int] $st.Kept -eq 22 -and $run.SinceUtc -is [DBNull] -and [int] $run.Events -eq 105) `
            "the first run works out every event, and keeps the rule's rows ($($st.Kept) kept for $($run.Events) events, $($st.Differ) differ)"
        $pins = [ordered]@{
            c01 = 'Microsoft_365_Copilot Added u01@c.com 0';  c02 = 'Microsoft_365_Copilot Added u02@c.com 0';  u02 = ''
            u03 = 'Microsoft_365_Copilot Removed u03@c.com 0'; c04a = 'Microsoft_365_Copilot Added u04@c.com 1'; c04b = ''
            c05a = 'Microsoft_365_Copilot Added u05@c.com 1'; u05a = '';  c06a = '';  c07 = 'Microsoft_365_Copilot Added u07@c.com 0'
            c08 = 'Microsoft_365_Copilot Added u08@c.com 0';  c09 = 'Microsoft_365_Copilot Added u09@c.com 0';  c10 = '';  c11 = ''
            c12 = 'Microsoft_365_Copilot Added u12@c.com 0';  c13 = 'Microsoft_365_Copilot Added u13@c.com 0; VISIOCLIENT Removed u13@c.com 0'
            c14 = 'Microsoft_365_Copilot Added u14@c.com 1; VISIOCLIENT Removed u14@c.com 1'; c16 = '';  c17b = ''
            u18 = 'Microsoft_365_Copilot Added u18@c.com 0';  n19 = ''
        }
        $wrongPins = @(foreach ($k in $pins.Keys) { $got = & $skuRows $skuDb $k; if ($got -ne $pins[$k]) { "${k}: '$got'" } })
        Assert-That ($wrongPins.Count -eq 0) "each case of the rule comes out as the view reads it: own lists, borrowed lists, the Update user giving way, chunk groups, the paired user, lists that are not JSON, two list properties kept once ($($wrongPins -join '; '))"

        # A night: records arrive that change records of earlier nights. u03 counted alone; its license change arrives,
        # detail-free, so u03 gives way and the change borrows its lists. c17b's first fragment arrives with the lists.
        # c01 is shredded again unchanged, and c13 with lists that no longer differ.
        Invoke-SqlText -Catalog $skuDb -Sql @'
DECLARE @e5 NVARCHAR(200) = N'[SkuName=SPE_E5, AccountId=a, SkuId=1, DisabledPlans=[]]', @cop NVARCHAR(200) = N'[SkuName=Microsoft_365_Copilot, AccountId=a, SkuId=2, DisabledPlans=[]]';
DECLARE @old NVARCHAR(MAX) = CONCAT(N'["', @e5, N'"]'), @new NVARCHAR(MAX) = CONCAT(N'["', @e5, N'","', @cop, N'"]');
INSERT INTO audit.LicenseEvent (RecordId, CreatedDateTime, ActivityDate, Operation, ResultStatus, ActorUpn, TargetUpn, CorrelationId, ChunkGroupId, ChunkSeq) VALUES
 ('c03',  '2026-09-01T10:00:02', '2026-09-01', N'Change user license', N'Success', N'admin@c.com', N'u03@c.com', N'k03', NULL, NULL),
 ('c17a', '2026-09-01T10:00:00', '2026-09-01', N'Change user license', N'Success', N'admin@c.com', N'u17@c.com', N'k17', N'g17', 1);
INSERT INTO audit.LicenseEventProperty (RecordId, Ordinal, [Name], OldValue, NewValue, Source) VALUES ('c17a', 10000, N'AssignedLicense', @old, @new, 'chunks');
UPDATE audit.LicenseEvent SET ShreddedUtc = SYSUTCDATETIME() WHERE RecordId IN ('c01', 'c13');
UPDATE audit.LicenseEventProperty SET OldValue = @old, NewValue = @old WHERE RecordId = 'c13';
EXEC audit.usp_RefreshLicenseSkuChange;
'@
        $st = & $skuState $skuDb
        $run = & $skuRun $skuDb
        Assert-That ([int] $st.Differ -eq 0 -and [int] $st.Kept -eq 21 -and $run.SinceUtc -isnot [DBNull] -and [int] $run.Events -eq 6) `
            "a night works out only the events shredded since the last run and the records raised with them, and still keeps the rule's rows ($($run.Events) of 107 events worked out, $($st.Kept) kept, $($st.Differ) differ)"
        Assert-That ((& $skuRows $skuDb 'u03') -eq '' -and (& $skuRows $skuDb 'c03') -eq 'Microsoft_365_Copilot Removed u03@c.com 0') `
            "an Update user gives way to the license change that arrived a night later, which takes its lists ($(& $skuRows $skuDb 'c03'))"
        Assert-That ((& $skuRows $skuDb 'c17a') -eq 'Microsoft_365_Copilot Added u17@c.com 1' -and (& $skuRows $skuDb 'c13') -eq '' -and (& $skuRows $skuDb 'c01') -eq $pins.c01) `
            'a chunk group whose first record arrived is a change now, a record whose lists no longer differ has no rows left, and one shredded again unchanged keeps its row'

        # The same through the shred itself. Night 1: a chunked change with its second fragment missing, and an Update
        # user that carries the lists. Night 2: the fragment, and the license change the Update user was raised with.
        $nightDoc = {
            param($RecordId, $When, $Op, $Obj, $Corr, $Mod, $Ext)
            $doc = [ordered]@{ CreationTime = $When; Id = $RecordId; Operation = $Op; RecordType = 8; ResultStatus = 'Success'; Workload = 'AzureActiveDirectory'
                               UserId = 'admin@contoso.com'; ObjectId = $Obj; InterSystemsId = $Corr; ExtendedProperties = $Ext; ModifiedProperties = $Mod; Target = @() }
            Invoke-SqlText -Catalog $skuDb -Parameters @{ '@id' = $RecordId; '@at' = $When; '@op' = $Op; '@json' = ($doc | ConvertTo-Json -Depth 10 -Compress) } -Sql @"
INSERT INTO audit.CopilotAudit (RecordId, CreatedDateTime, RecordType, Operation, UserId, UserPrincipalName, AuditDataGz, WindowId)
VALUES (@id, @at, 'azureActiveDirectory', @op, 'admin@contoso.com', 'admin@contoso.com', COMPRESS(CAST(@json AS NVARCHAR(MAX))), 1);
"@
        }
        $nightChunks = @(& $chunkify $payload 'grp-night' 2)
        $e3 = '[SkuName=ENTERPRISEPACK, AccountId=t1, SkuId=6fd2c87f, DisabledPlans=[]]'; $cop = '[SkuName=Microsoft_365_Copilot, AccountId=t1, SkuId=639dec6b, DisabledPlans=[]]'
        $gain = @(@{ Name = 'AssignedLicense'; OldValue = (ConvertTo-Json @($e3) -Compress); NewValue = (ConvertTo-Json @($e3, $cop) -Compress) })
        & $nightDoc 's-01' '2026-09-20T10:00:00' 'Change user license.' 'user20@contoso.com' 'corr-s1' @() $nightChunks[0]
        & $nightDoc 's-03' '2026-09-20T10:00:00' 'Update user.' 'user21@contoso.com' 'corr-s2' $gain @()
        Invoke-SqlText -Catalog $skuDb -Sql 'EXEC audit.usp_ShredLicenseEvents;'
        $st = & $skuState $skuDb
        Assert-That ([int] $st.Differ -eq 0 -and (& $skuRows $skuDb 's-03') -eq 'Microsoft_365_Copilot Added user21@contoso.com 0' -and (& $skuRows $skuDb 's-01') -eq '') `
            "night 1: the Update user is the change, and the chunk group missing a fragment is none yet ($(& $skuRows $skuDb 's-03'))"

        Invoke-SqlText -Catalog $skuDb -Sql "UPDATE audit.LicenseEvent SET ShreddedUtc = '2026-01-01';"   # shredded on earlier nights
        & $nightDoc 's-02' '2026-09-21T10:00:00' 'Change user license.' 'user20@contoso.com' 'corr-s1' @() $nightChunks[1]
        & $nightDoc 's-04' '2026-09-21T10:00:00' 'Change user license.' 'user21@contoso.com' 'corr-s2' @() @()
        Invoke-SqlText -Catalog $skuDb -Sql "EXEC audit.usp_ShredLicenseEvents @SinceUtc = '2026-09-21';"
        $st = & $skuState $skuDb
        $run = & $skuRun $skuDb
        Assert-That ([int] $st.Differ -eq 0 -and $run.SinceUtc -isnot [DBNull] -and [int] $run.Events -eq 4 -and [int] $run.Changes -eq 2) `
            "night 2: the nightly shred works out its two records' families, four events of 111 ($($run.Events) events, $($run.Changes) changes, $($st.Differ) differ)"
        Assert-That ((& $skuRows $skuDb 's-01') -eq 'Microsoft_365_Copilot Removed user20@contoso.com 1' -and (& $skuRows $skuDb 's-03') -eq '' -and (& $skuRows $skuDb 's-04') -eq 'Microsoft_365_Copilot Added user21@contoso.com 0') `
            "the fragment that arrived completes the change on the group's first record, shredded the night before, and the license change takes over from the Update user ($(& $skuRows $skuDb 's-01'); $(& $skuRows $skuDb 's-04'))"

        # A shred that is stopped part way. Its statements commit one by one, so it can leave a batch's events without
        # their properties, and it leaves its mark. Here a shred fails as it writes a batch's properties: a trigger
        # stands in for a task that is ended, a restart, a full disk. Readers still see the SKU changes of the last
        # shred that finished; the next shred, whatever it was asked for, finishes the stopped one.
        $keptSql = "SELECT STRING_AGG(CONVERT(NVARCHAR(MAX), CONCAT(RecordId, N' ', Sku, N' ', Change)), N'; ') WITHIN GROUP (ORDER BY RecordId, Sku, Change) FROM audit.LicenseSkuChange;"
        $shredState = { (Invoke-SqlText -Catalog $skuDb -As Table -Sql @"
SELECT (SELECT COUNT(*) FROM audit.LicenseEventProperty WHERE RecordId = 's-03') AS S03Props,
       (SELECT COUNT(*) FROM audit.LicenseEventProperty WHERE RecordId = 's-01' AND Source = 'chunks') AS S01Chunks,
       (SELECT STRING_AGG(CONVERT(NVARCHAR(MAX), RecordId), N',') WITHIN GROUP (ORDER BY RecordId) FROM audit.LicenseEvent WHERE RecordId LIKE 's-0[1-4]' AND ShreddedUtc > '2026-06-01') AS Stamped,
       (SELECT COUNT(*) FROM audit.PipelineSetting WHERE Name = N'LicenseShredInProgress') AS InProgress,
       (SELECT Value FROM audit.PipelineSetting WHERE Name = N'LicenseShredInProgress') AS MarkValue,
       (SELECT CONVERT(NVARCHAR(19), UpdatedUtc, 120) FROM audit.PipelineSetting WHERE Name = N'LicenseShredInProgress') AS MarkSince,
       (SELECT COUNT(*) FROM audit.PipelineSetting WHERE Name = N'LicenseShredCompleteUtc') AS Complete;
"@).Rows[0] }
        # Asked from another session, with no pool: the session that ran a shred could take its own lock again.
        $lockFree = {
            $probe = [System.Data.SqlClient.SqlConnection]::new((New-ConnString $skuDb) + ';Pooling=false')
            $probe.Open()
            try { $q = $probe.CreateCommand(); $q.CommandText = "SELECT APPLOCK_TEST(N'public', N'audit.usp_ShredLicenseEvents', N'Exclusive', N'Session');"; [int] $q.ExecuteScalar() }
            finally { $probe.Dispose() }
        }
        $stopShred = "CREATE TRIGGER audit.trStopShred ON audit.LicenseEventProperty AFTER INSERT AS BEGIN SET NOCOUNT ON; THROW 50999, N'stopped here by the test', 1; END;"
        $letShred = 'DROP TRIGGER audit.trStopShred;'
        $failing = { param($Sql) try { Invoke-SqlText -Catalog $skuDb -Sql $Sql; '' } catch { $_.Exception.Message } }
        $asOld = "UPDATE audit.LicenseEvent SET ShreddedUtc = '2026-01-01';"
        $keptBefore = "$(Invoke-SqlText -Catalog $skuDb -As Scalar -Sql $keptSql)"
        $was = & $shredState
        $runBefore = [int] (& $skuRun $skuDb).RunId
        Assert-That ([int] $was.S03Props -gt 0 -and [int] $was.S01Chunks -gt 0 -and [int] $was.InProgress -eq 0 -and [int] $was.Complete -eq 1 -and (& $lockFree) -eq 1) `
            "a shred that finished leaves no mark of one running and lets its lock go, and the shred of every record left its own mark ($($was.S03Props) and $($was.S01Chunks) properties to lose below)"

        Invoke-SqlText -Catalog $skuDb -Sql "$asOld DELETE FROM audit.PipelineSetting WHERE Name = N'LicenseShredCompleteUtc';"
        Invoke-SqlText -Catalog $skuDb -Sql $stopShred
        $msg = & $failing 'EXEC audit.usp_ShredLicenseEvents;'
        Invoke-SqlText -Catalog $skuDb -Sql $letShred
        $ss = & $shredState
        Assert-That ($msg -like '*stopped here by the test*' -and [int] $ss.InProgress -eq 1 -and "$($ss.MarkValue)" -eq '' -and [int] $ss.S03Props -eq 0 -and "$($ss.Stamped)" -eq 's-01,s-02,s-03,s-04') `
            "a shred of every record that stops as it writes a batch's properties leaves its events without them, and its mark: every record is owed (events written: $($ss.Stamped); their properties: $($ss.S03Props))"
        Assert-That ((& $lockFree) -eq 1 -and [int] $ss.Complete -eq 0) 'it lets its lock go, though its pooled connection stays open, and leaves no mark that every record is shredded'
        Invoke-SqlText -Catalog $skuDb -Sql 'EXEC audit.usp_RefreshLicenseSkuChange;'   # the changes asked for by hand meanwhile
        Assert-That ("$(Invoke-SqlText -Catalog $skuDb -As Scalar -Sql $keptSql)" -eq $keptBefore -and [int] (& $skuRun $skuDb).RunId -eq $runBefore) `
            'the kept SKU changes stay as the last finished shred made them: asked for by hand, they are not worked out from its half-written events'

        # The next shred stops too. The mark keeps what is owed and the time the first one began: how long events have
        # been left so, which the health check reads.
        Invoke-SqlText -Catalog $skuDb -Sql "UPDATE audit.PipelineSetting SET UpdatedUtc = '2026-01-02T03:04:05' WHERE Name = N'LicenseShredInProgress';"
        Invoke-SqlText -Catalog $skuDb -Sql $stopShred
        $msg = & $failing "EXEC audit.usp_ShredLicenseEvents @SinceUtc = '2026-12-01';"
        Invoke-SqlText -Catalog $skuDb -Sql $letShred
        $ss = & $shredState
        Assert-That ($msg -like '*stopped here by the test*' -and [int] $ss.InProgress -eq 1 -and "$($ss.MarkValue)" -eq '' -and "$($ss.MarkSince)" -eq '2026-01-02 03:04:05' -and (& $lockFree) -eq 1) `
            "a shred that takes a stopped one over and stops too leaves the mark as it found it: every record owed, since the first began ($($ss.MarkSince))"

        Invoke-SqlText -Catalog $skuDb -Sql "$asOld EXEC audit.usp_ShredLicenseEvents @SinceUtc = '2026-12-01';"   # a night with nothing new
        $ss = & $shredState
        $run = & $skuRun $skuDb
        $st = & $skuState $skuDb
        Assert-That ([int] $ss.S03Props -eq [int] $was.S03Props -and [int] $ss.S01Chunks -eq [int] $was.S01Chunks -and "$($ss.Stamped)" -eq 's-01,s-02,s-03,s-04' -and [int] $ss.InProgress -eq 0) `
            "the next shred, though asked for one night, shreds every record to finish it: the properties are back and the mark is gone ($($ss.S03Props), $($ss.S01Chunks); shredded again: $($ss.Stamped))"
        Assert-That ("$(Invoke-SqlText -Catalog $skuDb -As Scalar -Sql $keptSql)" -eq $keptBefore -and [int] $st.Differ -eq 0 -and $run.SinceUtc -is [DBNull] -and [int] $ss.Complete -eq 1) `
            'and it works out every event again, and marks that a shred of every record finished'

        # One stopped while shredding from a day: its mark says from when, and the next shreds from that day, not only
        # from its own.
        Invoke-SqlText -Catalog $skuDb -Sql $asOld
        Invoke-SqlText -Catalog $skuDb -Sql $stopShred
        $msg = & $failing "EXEC audit.usp_ShredLicenseEvents @SinceUtc = '2026-09-21';"
        Invoke-SqlText -Catalog $skuDb -Sql $letShred
        $ss = & $shredState
        Assert-That ($msg -like '*stopped here by the test*' -and [int] $ss.InProgress -eq 1 -and "$($ss.MarkValue)" -eq '2026-09-21T00:00:00' -and "$($ss.Stamped)" -eq 's-02,s-04') `
            "a night's shred that stops leaves in its mark the time it was shredding from ($($ss.MarkValue))"
        Invoke-SqlText -Catalog $skuDb -Sql "$asOld EXEC audit.usp_ShredLicenseEvents @SinceUtc = '2026-12-01';"
        $ss = & $shredState
        $st = & $skuState $skuDb
        Assert-That ("$($ss.Stamped)" -eq 's-02,s-04' -and [int] $ss.InProgress -eq 0 -and [int] $st.Differ -eq 0 -and "$(Invoke-SqlText -Catalog $skuDb -As Scalar -Sql $keptSql)" -eq $keptBefore) `
            "and the next shred, asked for a later day, shreds from 21 September to finish it ($($ss.Stamped))"

        # One shred at a time. Two at once would each take the other's mark for a stopped shred's, and the first to
        # finish would take away the mark the other still needs. A session holding the shred's lock stands in for one
        # running: a second shred waits for it, writing nothing, and runs once it has let go.
        $holder = [System.Data.SqlClient.SqlConnection]::new((New-ConnString $skuDb) + ';Pooling=false')
        $second = [System.Data.SqlClient.SqlConnection]::new((New-ConnString $skuDb) + ';Pooling=false')
        try {
            $holder.Open(); $second.Open()
            $holdCmd = $holder.CreateCommand()
            $holdCmd.CommandText = "DECLARE @r INT; EXEC @r = sp_getapplock @Resource = N'audit.usp_ShredLicenseEvents', @LockMode = 'Exclusive', @LockOwner = 'Session', @LockTimeout = 0; SELECT @r;"
            $took = [int] $holdCmd.ExecuteScalar()
            $runs0 = [int] (& $skuRun $skuDb).RunId
            $shredCmd = $second.CreateCommand(); $shredCmd.CommandTimeout = 600
            $shredCmd.CommandText = "EXEC audit.usp_ShredLicenseEvents @SinceUtc = '2026-09-21';"
            $shredTask = $shredCmd.ExecuteNonQueryAsync()
            Start-Sleep -Seconds 3
            $waited = -not $shredTask.IsCompleted
            $held = & $shredState
            $heldLock = & $lockFree
            $holdCmd.CommandText = "EXEC sp_releaseapplock @Resource = N'audit.usp_ShredLicenseEvents', @LockOwner = 'Session';"
            [void] $holdCmd.ExecuteNonQuery()
            [void] $shredTask.Wait(600000)
            $runsAfter = [int] (& $skuRun $skuDb).RunId
        }
        finally { $second.Dispose(); $holder.Dispose() }
        $ss = & $shredState
        Assert-That ($took -eq 0 -and $waited -and [int] $held.InProgress -eq 0 -and $heldLock -eq 0 -and $runsAfter -eq $runs0 + 1 -and [int] $ss.InProgress -eq 0 -and (& $lockFree) -eq 1) `
            "one shred at a time: a shred that starts while another runs waits for it, with no mark of its own yet, and runs once it has finished (runs $runs0, then $runsAfter)"

        # The run is told when a shred began, and takes the events stamped from then on whatever ran since. c13 was
        # stamped twenty minutes ago and has its lists only now; the runs above were minutes later.
        Invoke-SqlText -Catalog $skuDb -Sql @'
DECLARE @e5 NVARCHAR(200) = N'[SkuName=SPE_E5, AccountId=a, SkuId=1, DisabledPlans=[]]', @cop NVARCHAR(200) = N'[SkuName=Microsoft_365_Copilot, AccountId=a, SkuId=2, DisabledPlans=[]]',
        @viz NVARCHAR(200) = N'[SkuName=VISIOCLIENT, AccountId=a, SkuId=3, DisabledPlans=[]]';
UPDATE audit.LicenseEvent SET ShreddedUtc = '2026-01-01';
UPDATE audit.LicenseEvent SET ShreddedUtc = DATEADD(MINUTE, -20, SYSUTCDATETIME()) WHERE RecordId = 'c13';
UPDATE audit.LicenseEventProperty SET OldValue = CONCAT(N'["', @e5, N'","', @viz, N'"]'), NewValue = CONCAT(N'["', @e5, N'","', @cop, N'"]') WHERE RecordId = 'c13';
EXEC audit.usp_RefreshLicenseSkuChange;
'@
        $missed = & $skuRows $skuDb 'c13'
        Invoke-SqlText -Catalog $skuDb -Sql "DECLARE @from DATETIME2(0) = DATEADD(MINUTE, -30, SYSUTCDATETIME()); EXEC audit.usp_RefreshLicenseSkuChange @SinceShreddedUtc = @from;"
        $st = & $skuState $skuDb
        Assert-That ($missed -eq '' -and (& $skuRows $skuDb 'c13') -eq $pins.c13 -and [int] $st.Differ -eq 0) `
            "told when a shred began, the run takes its events though a run came in between (c13 not told: '$missed'; told: $(& $skuRows $skuDb 'c13'))"

        # And the shred tells it when it began, not when it ended: its events are stamped in between. A run recorded
        # as an hour from now stands in for another session's that came in between, and a trigger holds the shred for
        # three seconds after its events are stamped.
        & $nightDoc 's-05' '2026-09-22T10:00:00' 'Change user license.' 'user25@contoso.com' 'corr-s5' $gain @()
        Invoke-SqlText -Catalog $skuDb -Sql @"
INSERT INTO audit.LicenseSkuChangeRun (StartedUtc, EndedUtc, SinceUtc, Events, Changes)
VALUES (DATEADD(HOUR, 1, SYSUTCDATETIME()), DATEADD(HOUR, 1, SYSUTCDATETIME()), SYSUTCDATETIME(), 0, 0);
"@
        Invoke-SqlText -Catalog $skuDb -Sql "CREATE TRIGGER audit.trSlowShred ON audit.LicenseEventProperty AFTER INSERT AS BEGIN SET NOCOUNT ON; WAITFOR DELAY '00:00:03'; END;"
        Invoke-SqlText -Catalog $skuDb -Sql "EXEC audit.usp_ShredLicenseEvents @SinceUtc = '2026-09-22';"
        Invoke-SqlText -Catalog $skuDb -Sql "DROP TRIGGER audit.trSlowShred; DELETE FROM audit.LicenseSkuChangeRun WHERE StartedUtc > DATEADD(MINUTE, 30, SYSUTCDATETIME());"
        $st = & $skuState $skuDb
        Assert-That ((& $skuRows $skuDb 's-05') -eq 'Microsoft_365_Copilot Added user25@contoso.com 0' -and [int] $st.Differ -eq 0) `
            "a shred's events are worked out though it ran on for seconds after stamping them, and a run is recorded after them ($(& $skuRows $skuDb 's-05'))"

        # A tenth of the events or fewer shredded since the last run are sought, family by family; more, and every
        # event is read once. Of 112 events here, 11 and 12.
        Invoke-SqlText -Catalog $skuDb -Sql "$asOld UPDATE audit.LicenseEvent SET ShreddedUtc = SYSUTCDATETIME() WHERE RecordId BETWEEN 'z101' AND 'z111'; EXEC audit.usp_RefreshLicenseSkuChange;"
        $run = & $skuRun $skuDb
        Assert-That ($run.SinceUtc -isnot [DBNull] -and [int] $run.Events -eq 11) "a tenth of the events or fewer are sought: 11 of 112 ($($run.Events) worked out)"
        Invoke-SqlText -Catalog $skuDb -Sql "$asOld UPDATE audit.LicenseEvent SET ShreddedUtc = SYSUTCDATETIME() WHERE RecordId BETWEEN 'z101' AND 'z112'; EXEC audit.usp_RefreshLicenseSkuChange;"
        $run = & $skuRun $skuDb
        $st = & $skuState $skuDb
        Assert-That ($run.SinceUtc -is [DBNull] -and [int] $run.Events -eq 112 -and [int] $st.Differ -eq 0) "over a tenth, every event is worked out: 12 of 112 ($($run.Events) worked out)"

        # An upgrade from 2.5.3: events already shredded, no table. Between the two scripts the view still reads the
        # records, so a reader is never handed an empty history; the procedure script works the changes out once.
        $ruleRows = [int] (Invoke-SqlText -Catalog $skuDb -As Scalar -Sql 'SELECT COUNT(*) FROM audit.vLicenseSkuChangeFromRecords;')
        Invoke-SqlText -Catalog $skuDb -Sql 'DROP VIEW audit.vLicenseSkuChange; DROP VIEW audit.vLicenseSkuChangeFromRecords; DROP PROCEDURE audit.usp_RefreshLicenseSkuChange; DROP TABLE audit.LicenseSkuChange; DROP TABLE audit.LicenseSkuChangeRun; DROP INDEX IX_LicenseEvent_Shredded ON audit.LicenseEvent;'
        & $runIn $skuDb 'Create-LicenseEventSchema.sql'
        $st = & $skuState $skuDb
        Assert-That ("$($st.ViewReads)" -eq 'records' -and [int] $st.ViewRows -eq $ruleRows -and [int] $st.Kept -eq 0) `
            "an upgrade: until the changes are worked out, the view reads the records as before ($($st.ViewRows) rows from the $($st.ViewReads))"
        # Had working them out failed and the script run on (SSMS does, and sqlcmd without -b): every batch but that one.
        foreach ($b in [regex]::Split((Get-Content -LiteralPath (Join-Path $root 'sql' 'Create-LicenseEventProcedure.sql') -Raw), '(?im)^[ \t]*GO[ \t]*(?:--[^\r\n]*)?\r?$')) {
            if ($b.Trim() -and $b -notlike '*already here, once*') { Invoke-SqlText -Catalog $skuDb -Sql $b }
        }
        $st = & $skuState $skuDb
        Assert-That ("$($st.ViewReads)" -eq 'records' -and [int] $st.ViewRows -eq $ruleRows -and [int] $st.Kept -eq 0) `
            "and it stays on the records where they were not: the procedure script moves the view to the table only once its rows are there ($($st.ViewRows) rows from the $($st.ViewReads))"
        & $runIn $skuDb 'Create-LicenseEventProcedure.sql'
        $st = & $skuState $skuDb
        $runs = (Invoke-SqlText -Catalog $skuDb -As Table -Sql "SELECT COUNT(*) AS N, SUM(CASE WHEN SinceUtc IS NULL THEN 1 ELSE 0 END) AS Every, (SELECT COUNT(*) FROM sys.indexes WHERE object_id = OBJECT_ID(N'audit.LicenseEvent') AND name = N'IX_LicenseEvent_Shredded') AS Ix FROM audit.LicenseSkuChangeRun;").Rows[0]
        Assert-That ("$($st.ViewReads)" -eq 'table' -and [int] $st.Differ -eq 0 -and [int] $st.Kept -gt 0 -and [int] $runs.N -eq 1 -and [int] $runs.Every -eq 1 -and [int] $runs.Ix -eq 1) `
            "the procedure script works them out once, without a shred, and the view reads the table from then on ($($st.Kept) kept, $($st.Differ) differ)"
        & $runIn $skuDb 'Create-LicenseEventSchema.sql'; & $runIn $skuDb 'Create-LicenseEventProcedure.sql'
        $st = & $skuState $skuDb
        Assert-That ("$($st.ViewReads)" -eq 'table' -and [int] (Invoke-SqlText -Catalog $skuDb -As Scalar -Sql 'SELECT COUNT(*) FROM audit.LicenseSkuChangeRun;') -eq 1) 'and only once: installing again works out nothing and leaves the view on the table'
    }
    finally {
        try { Invoke-SqlText -Catalog 'master' -Sql "IF DB_ID(N'$skuDb') IS NOT NULL BEGIN ALTER DATABASE [$skuDb] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [$skuDb]; END;" } catch { Write-Host "  (could not drop $skuDb)" }
    }

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
    Assert-That ("$($wa[0].Lic)" -eq 'TRUE') "the view writes the license flag the way the CSV did ($($wa[0].Lic))"
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
    # Licensed at the time of use. User A is licensed today, but the license
    # was assigned at 10:00 local on the 14th - after the first session began
    # (09:00) and before the next (11:29). Section 9 put the license-event
    # schema in this same database, so it serves as @LicenseEventDatabase.
    # The event is written here, not shredded from a record, so its SKU change
    # is worked out here too (2.5.4: the shred does that for its own).
    Invoke-SqlText -Sql @"
INSERT INTO audit.LicenseEvent (RecordId, CreatedDateTime, ActivityDate, Operation, ActorUpn, TargetUpn, CorrelationId)
VALUES ('lic-a1', '2026-09-14T14:00:00', '2026-09-14', 'Change user license', 'admin@contoso.com', 'a@contoso.com', 'corr-a1');
INSERT INTO audit.LicenseEventProperty (RecordId, Ordinal, [Name], OldValue, NewValue, Source)
VALUES ('lic-a1', 0, 'AssignedLicense', '[]', '["[SkuName=Microsoft_365_Copilot, AccountId=t, SkuId=x, DisabledPlans=[]]"]', 'record');
EXEC audit.usp_RefreshLicenseSkuChange @All = 1;
"@
    Invoke-SqlText -Sql "EXEC audit.usp_BuildCopilotSessions @LicenseEventDatabase = N'$($script:ITDatabase)';" -ShowMessages
    $la = Invoke-SqlText -As Table -Sql "SELECT IsM365CopilotLicensed AS Lic FROM audit.CopilotSession WHERE UserUPN = 'a@contoso.com' ORDER BY SessionOrdinal_Derived;"
    $flags = @($la.Rows | ForEach-Object { [int] $_.Lic }) -join ''
    Assert-That ($flags -eq '0111') "the license flag is the license held when each session began, not today's ($flags)"
    $lw = Invoke-SqlText -As Table -Sql "SELECT IsM365CopilotLicensed AS Lic, WeeklySessionCount AS S FROM audit.CopilotWeeklyRollup WHERE UserUPN = 'a@contoso.com' ORDER BY IsM365CopilotLicensed;"
    Assert-That ($lw.Rows.Count -eq 2 -and [int] $lw.Rows[0].S -eq 1 -and [int] $lw.Rows[1].S -eq 3) `
        "the week splits into its unlicensed and licensed sessions ($(@($lw.Rows | ForEach-Object { "$($_.Lic):$($_.S)" }) -join ', '))"
    $lb = Invoke-SqlText -As Scalar -Sql "SELECT MAX(CAST(IsM365CopilotLicensed AS int)) FROM audit.CopilotSession WHERE UserUPN = 'b@contoso.com';"
    Assert-That ([int] $lb -eq 0) "a user with no license history keeps today's flag ($lb)"
    $lr = Invoke-SqlText -As Scalar -Sql 'SELECT TOP (1) LicenseEventDatabase FROM audit.CopilotSessionRun ORDER BY RunId DESC;'
    Assert-That ("$lr" -eq $script:ITDatabase) "the run log records which license history was used ($lr)"

    # --- Incremental builds (@SinceUtc) ---------------------------------------
    # A new prompt from user A at noon Eastern on the 15th, then a build from
    # the 15th only. It must equal a full rebuild over the same rows - sessions,
    # numbering and weeks - and the sessions before the 15th must keep their keys.
    $licArg = "@LicenseEventDatabase = N'$($script:ITDatabase)'"
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

    # --- 2.4.12: daylight saving ---------------------------------------------
    # Order and the 60-minute gap are taken in UTC, the day in Eastern time. On
    # 1 Nov 2026 01:00-01:59 happens twice; on 14 Mar 2027 02:00-02:59 never does.
    Invoke-SqlText -Sql @"
INSERT INTO audit.RawLogFlattened (RecordId, [TimeStamp], UtcTimeStamp, [User], App, IsCopilotLic, FlattenedUtc) VALUES
    (N'd-01', N'2026-11-01T05:05:00Z', '2026-11-01T05:05:00', N'dst1@contoso.com', N'Teams', 1, SYSUTCDATETIME()),  -- 01:05 EDT
    (N'd-02', N'2026-11-01T06:55:00Z', '2026-11-01T06:55:00', N'dst1@contoso.com', N'Word',  1, SYSUTCDATETIME()),  -- 01:55 EST, 110 minutes on
    (N'd-03', N'2026-11-01T05:50:00Z', '2026-11-01T05:50:00', N'dst2@contoso.com', N'Teams', 1, SYSUTCDATETIME()),  -- 01:50 EDT
    (N'd-04', N'2026-11-01T06:05:00Z', '2026-11-01T06:05:00', N'dst2@contoso.com', N'Word',  1, SYSUTCDATETIME()),  -- 01:05 EST, 15 minutes on
    (N'd-05', N'2027-03-14T06:55:00Z', '2027-03-14T06:55:00', N'dst3@contoso.com', N'Teams', 1, SYSUTCDATETIME()),  -- 01:55 EST
    (N'd-06', N'2027-03-14T07:05:00Z', '2027-03-14T07:05:00', N'dst3@contoso.com', N'Word',  1, SYSUTCDATETIME());  -- 03:05 EDT, 10 minutes on
"@
    Invoke-SqlText -Sql "EXEC audit.usp_BuildCopilotSessions $licArg;"
    $dst = Invoke-SqlText -As Table -Sql @"
SELECT UserUPN, PromptCount AS P, PrimaryApp, InferredSessionSpanMinutes AS Span,
       CONVERT(varchar(16), SessionStart_PromptSubmittedDateTime, 120) AS StartLocal,
       CONVERT(varchar(16), SessionEnd_PromptSubmittedDateTime, 120) AS EndLocal
FROM audit.CopilotSession WHERE UserUPN LIKE N'dst%' ORDER BY UserUPN, SessionOrdinal_Derived;
"@
    $d1 = @($dst.Rows | Where-Object UserUPN -eq 'dst1@contoso.com')
    $d2 = @($dst.Rows | Where-Object UserUPN -eq 'dst2@contoso.com')
    $d3 = @($dst.Rows | Where-Object UserUPN -eq 'dst3@contoso.com')
    Assert-That ($d1.Count -eq 2 -and [int] $d1[0].P -eq 1 -and [int] $d1[1].P -eq 1) `
        "2.4.12: in the repeated hour, prompts 110 minutes apart that read 01:05 and 01:55 are two sessions, not one ($($d1.Count))"
    Assert-That ($d2.Count -eq 1 -and "$($d2[0].PrimaryApp)" -eq 'Teams' -and [decimal] $d2[0].Span -eq 15 -and "$($d2[0].StartLocal)" -eq '2026-11-01 01:50' -and "$($d2[0].EndLocal)" -eq '2026-11-01 01:50') `
        "a prompt at 01:05 EST after one at 01:50 EDT comes second: the first prompt's app, a 15-minute span, and an end not shown before its start (2.4.12) ($($d2[0].PrimaryApp), $($d2[0].Span) min, $($d2[0].StartLocal) to $($d2[0].EndLocal))"
    Assert-That ($d3.Count -eq 1 -and [int] $d3[0].P -eq 2 -and [decimal] $d3[0].Span -eq 10) `
        "across the skipped hour, 01:55 and 03:05 are 10 minutes apart: one session ($($d3.Count) sessions, $($d3[0].Span) min)"
    $dstWeeks = "$(Invoke-SqlText -As Scalar -Sql "SELECT STRING_AGG(CONCAT(LEFT(UserUPN, 4), '=', WeeklyInferredSessionSpanMinutes), ',') WITHIN GROUP (ORDER BY UserUPN) FROM audit.CopilotWeeklyRollup WHERE UserUPN IN (N'dst2@contoso.com', N'dst3@contoso.com');")"
    Assert-That ($dstWeeks -eq 'dst2=15.00,dst3=10.00') "and the weeks sum the spans in UTC ($dstWeeks)"

    # --- 2.4.12: the license databases as a list --------------------------------
    # The run log kept the list as given and the check compared it as text, so the same databases in another order or
    # spacing counted as different settings, and a nightly passing them its own way was refused every night.
    $lic2 = "$($script:ITDatabase)_Lic2"
    try {
        Invoke-SqlText -Catalog 'master' -Sql "IF DB_ID(N'$lic2') IS NOT NULL BEGIN ALTER DATABASE [$lic2] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [$lic2]; END; CREATE DATABASE [$lic2];"
        Invoke-SqlText -Catalog $lic2 -Sql "EXEC (N'CREATE SCHEMA audit AUTHORIZATION dbo;'); EXEC (N'CREATE VIEW audit.vLicenseSkuChange AS SELECT CAST(NULL AS NVARCHAR(512)) AS TargetUpn, CAST(NULL AS NVARCHAR(200)) AS Sku, CAST(NULL AS DATETIME2(7)) AS CreatedDateTime, CAST(NULL AS NVARCHAR(20)) AS Change WHERE 1 = 0;');"
        Invoke-SqlText -Sql "EXEC audit.usp_BuildCopilotSessions @LicenseEventDatabase = N'$lic2,$($script:ITDatabase)';"
        $logged = "$(Invoke-SqlText -As Scalar -Sql 'SELECT TOP (1) LicenseEventDatabase FROM audit.CopilotSessionRun ORDER BY RunId DESC;')"
        Assert-That ($logged -eq "$($script:ITDatabase),$lic2") "2.4.12: the run log keeps the license databases each once, in order ($logged)"
        $sameList = try { [void] (Invoke-SqlText -Sql "EXEC audit.usp_BuildCopilotSessions @LicenseEventDatabase = N' $($script:ITDatabase) , $lic2,$($script:ITDatabase)', @SinceUtc = '2026-09-15';"); '' } catch { $_.Exception.GetBaseException().Message }
        Assert-That ($sameList -eq '') "the same databases in another order, spaced and repeated, build incrementally ($sameList)"
        $fewer = try { [void] (Invoke-SqlText -Sql "EXEC audit.usp_BuildCopilotSessions $licArg, @SinceUtc = '2026-09-15';"); '' } catch { $_.Exception.GetBaseException().Message }
        Assert-That ($fewer -like '*different settings*') 'while one database fewer is still a different setting'
    }
    finally {
        try { Invoke-SqlText -Catalog 'master' -Sql "IF DB_ID(N'$lic2') IS NOT NULL BEGIN ALTER DATABASE [$lic2] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [$lic2]; END;" } catch { Write-Host "  (could not drop $lic2)" }
    }

    # --- 2.4.12: the session export's -Build ------------------------------------
    # It rebuilt the production sessions under its own defaults (User, no license database), after which the nightly
    # run's incremental build was refused every night. It now builds as the last build did, and refuses a change.
    Invoke-SqlText -Sql "EXEC audit.usp_BuildCopilotSessions $licArg;"
    $exportTool = Join-Path $root 'tools' 'Export-CopilotSessionCsv.ps1'
    $exportDir = Join-Path ([System.IO.Path]::GetTempPath()) ('sessit-' + [guid]::NewGuid().ToString('N'))
    try {
        $runsBefore = [int] (Invoke-SqlText -As Scalar -Sql 'SELECT COUNT(*) FROM audit.CopilotSessionRun;')
        $refusedExport = try { & $exportTool -ConnectionString (New-ConnString $script:ITDatabase) -Build -ContextFallback Location -OutputFolder $exportDir 6>$null; '' } catch { $_.Exception.Message }
        $runsAfter = [int] (Invoke-SqlText -As Scalar -Sql 'SELECT COUNT(*) FROM audit.CopilotSessionRun;')
        Assert-That ($refusedExport -like '*different settings*' -and $runsAfter -eq $runsBefore) "2.4.12: -Build -ContextFallback Location is refused before it builds ($refusedExport)"
        & $exportTool -ConnectionString (New-ConnString $script:ITDatabase) -Build -OutputFolder $exportDir 6>$null
        $built = (Invoke-SqlText -As Table -Sql 'SELECT TOP (1) ContextFallback, LicenseEventDatabase FROM audit.CopilotSessionRun ORDER BY RunId DESC;').Rows[0]
        $after = try { [void] (Invoke-SqlText -Sql "EXEC audit.usp_BuildCopilotSessions $licArg, @SinceUtc = '2026-09-15';"); '' } catch { $_.Exception.GetBaseException().Message }
        Assert-That ("$($built.ContextFallback)" -eq 'User' -and "$($built.LicenseEventDatabase)" -eq $script:ITDatabase -and $after -eq '' -and (Test-Path (Join-Path $exportDir 'copilot_session_summary.csv'))) `
            "a plain -Build builds with the last build's settings, so the nightly's next build is not refused ($($built.ContextFallback), $($built.LicenseEventDatabase))"
    }
    finally { Remove-Item -LiteralPath $exportDir -Recurse -Force -ErrorAction SilentlyContinue }

    # Back to the fixture section 12 expects.
    Invoke-SqlText -Sql "DELETE FROM audit.RawLogFlattened WHERE RecordId = N's-12' OR RecordId LIKE N'd-0_'; DROP TABLE dbo.IncSessions; DROP TABLE dbo.IncWeeks;"
    Invoke-SqlText -Sql "EXEC audit.usp_BuildCopilotSessions $licArg;"

    # --- 2.4.12: a database whose collation is not tempdb's -------------------------
    # #lic took tempdb's collation, and its comparison with the sessions failed every build there (Msg 468).
    $collDb = "$($script:ITDatabase)_Coll"
    $collation = if ("$(Invoke-SqlText -Catalog 'master' -As Scalar -Sql "SELECT CONVERT(sysname, DATABASEPROPERTYEX(N'tempdb', 'Collation'));")" -eq 'Latin1_General_100_CI_AS') { 'SQL_Latin1_General_CP1_CI_AS' } else { 'Latin1_General_100_CI_AS' }
    $mainDb = $script:ITDatabase
    try {
        Invoke-SqlText -Catalog 'master' -Sql "IF DB_ID(N'$collDb') IS NOT NULL BEGIN ALTER DATABASE [$collDb] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [$collDb]; END; CREATE DATABASE [$collDb] COLLATE $collation;"
        $script:ITDatabase = $collDb
        foreach ($f in 'Create-CopilotAuditSchema.sql', 'Create-CopilotFlattenedSchema.sql', 'Create-CopilotSessionSchema.sql', 'Create-CopilotSessionProcedure.sql') {
            Invoke-SqlScript -Sql (Get-Content -LiteralPath (Join-Path $root 'sql' $f) -Raw)
        }
        Invoke-SqlText -Sql @"
INSERT INTO audit.RawLogFlattened (RecordId, [TimeStamp], UtcTimeStamp, [User], App, IsCopilotLic, FlattenedUtc) VALUES
    (N'k-01', N'2026-09-14T13:00:00Z', '2026-09-14T13:00:00', N'a@contoso.com', N'Word', 1, SYSUTCDATETIME()),
    (N'k-02', N'2026-09-14T13:20:00Z', '2026-09-14T13:20:00', N'a@contoso.com', N'Word', 1, SYSUTCDATETIME());
"@
        $collErr = try { [void] (Invoke-SqlText -Sql "EXEC audit.usp_BuildCopilotSessions @LicenseEventDatabase = N'$mainDb';"); '' } catch { $_.Exception.GetBaseException().Message }
        $collSessions = if ($collErr) { -1 } else { [int] (Invoke-SqlText -As Scalar -Sql 'SELECT COUNT(*) FROM audit.CopilotSession;') }
        Assert-That ($collErr -eq '' -and $collSessions -eq 1) "2.4.12: sessions build in a database collated $collation, unlike tempdb, with license history ($collSessions session; $collErr)"
    }
    finally {
        $script:ITDatabase = $mainDb
        try { Invoke-SqlText -Catalog 'master' -Sql "IF DB_ID(N'$collDb') IS NOT NULL BEGIN ALTER DATABASE [$collDb] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [$collDb]; END;" } catch { Write-Host "  (could not drop $collDb)" }
    }
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
    $allIds = Get-ExpectedId $loadMock
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
    $bRun = Invoke-EngineRun -Mock $loadMock -Options (New-EngineOption $loadMock (& $d 1) (& $d 5)) -Store $bStore -Sink $bSink -Log $bLog -TimeoutSec 600
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
        $run = Invoke-EngineRun -Mock $loadMock -Options (New-EngineOption $loadMock $From $To) -Store $st -Sink $sk -Log $lg -TimeoutSec 600
        [void] (Invoke-SqlText -Sql 'UPDATE audit.CopilotLoad SET DownloadedUtc = SYSUTCDATETIME() WHERE LoadId = @id' -Parameters @{ '@id' = $id })
        $m = Invoke-SqlText -As Table -Sql 'EXEC audit.usp_MergeCopilotLoad;' -ShowMessages
        return [pscustomobject]@{ LoadId = [int] $id; Run = $run; Merge = $m.Rows[0] }
    }

    $l1 = & $runLoad (& $d 4) (& $d 9)
    $overlap = (& $idsIn (& $d 4) (& $d 5)).Count
    $newDays = (& $idsIn (& $d 5) (& $d 9)).Count
    Assert-That ($l1.Run.Task.Status -eq 'RanToCompletion') "load 1 (Aug 4-9) downloaded ($($l1.Run.Task.Status))"
    Assert-That ([long] $l1.Merge.RowsAdded -eq $newDays -and [long] $l1.Merge.RowsAlreadyLoaded -eq $overlap) `
        "the lookback day was recognized, not re-added: $($l1.Merge.RowsAdded) new (of $newDays), $($l1.Merge.RowsAlreadyLoaded) already loaded (of $overlap)"

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

    # --- 2.4.12: two merges at once append nothing twice ---------------------
    # Fifty Aug 6 records go back to being new, staged as a downloaded load. Another merge - here a connection holding
    # the merge lock - is about to append the same records when this one starts. The merge looked for records already
    # there with no lock, so it appended them too, and every flatten over Aug 6 then failed on the duplicates.
    Invoke-SqlText -Sql @"
SELECT TOP (50) * INTO dbo.HeldAudit FROM audit.CopilotAudit WHERE RecordId LIKE '20260806-%' ORDER BY RecordId;
SELECT i.* INTO dbo.HeldInteraction FROM audit.CopilotInteraction AS i WHERE i.RecordId IN (SELECT RecordId FROM dbo.HeldAudit);
DELETE FROM audit.CopilotAudit WHERE RecordId IN (SELECT RecordId FROM dbo.HeldAudit);
DELETE FROM audit.CopilotInteraction WHERE RecordId IN (SELECT RecordId FROM dbo.HeldAudit);
DECLARE @load INT, @window BIGINT;
INSERT INTO audit.CopilotLoad (RangeStartUtc, RangeEndUtc, Operations, DownloadedUtc) VALUES ('2026-08-06', '2026-08-07', N'CopilotInteraction', SYSUTCDATETIME());
SET @load = SCOPE_IDENTITY();
INSERT INTO audit.CopilotLoadWindow (StartUtc, EndUtc, Status, Attempts) VALUES ('2026-08-06', '2026-08-07', 'Loaded', 1);
SET @window = SCOPE_IDENTITY();
INSERT INTO audit.CopilotLoadStage (WindowId, Attempt, RecordId, CreatedDateTime, RecordType, Operation, OrganizationId, UserType, UserId,
                                    UserPrincipalName, Service, ObjectId, ClientIp, AdministrativeUnits, AuditDataGz)
SELECT @window, 1, RecordId, CreatedDateTime, RecordType, Operation, OrganizationId, UserType, UserId,
       UserPrincipalName, Service, ObjectId, ClientIp, AdministrativeUnits, AuditDataGz
FROM dbo.HeldAudit;
"@
    $other = [System.Data.SqlClient.SqlConnection]::new($loadConn)
    $merging = [System.Data.SqlClient.SqlConnection]::new($loadConn)
    try {
        $other.Open(); $merging.Open()
        $hold = $other.CreateCommand()
        $hold.CommandText = "BEGIN TRANSACTION; EXEC sys.sp_getapplock @Resource = N'audit.CopilotAudit:append', @LockMode = N'Exclusive', @LockOwner = N'Transaction';"
        [void] $hold.ExecuteNonQuery()
        $mergeCmd = $merging.CreateCommand(); $mergeCmd.CommandText = 'EXEC audit.usp_MergeCopilotLoad;'; $mergeCmd.CommandTimeout = 300
        $task = $mergeCmd.ExecuteNonQueryAsync()
        Start-Sleep -Seconds 3
        $waited = -not $task.IsCompleted
        $hold.CommandText = @"
INSERT INTO audit.CopilotAudit SELECT * FROM dbo.HeldAudit;
INSERT INTO audit.CopilotInteraction SELECT * FROM dbo.HeldInteraction;
COMMIT TRANSACTION;
"@
        [void] $hold.ExecuteNonQuery()
        $finished = try { $task.Wait([timespan]::FromSeconds(300)) } catch { Write-Host "  (the merge failed: $($_.Exception.GetBaseException().Message))"; $false }
    }
    finally { $other.Dispose(); $merging.Dispose() }
    $race = (Invoke-SqlText -As Table -Sql @"
SELECT (SELECT COUNT_BIG(*) FROM audit.CopilotAudit) AS Rows_, (SELECT COUNT(DISTINCT RecordId) FROM audit.CopilotAudit) AS Distinct_,
       (SELECT COUNT_BIG(*) FROM audit.CopilotInteraction) AS Interactions,
       (SELECT TOP (1) CONCAT(RowsAdded, '/', RowsAlreadyLoaded) FROM audit.CopilotLoad ORDER BY LoadId DESC) AS Merge_;
"@).Rows[0]
    Assert-That ($waited -and $finished) '2.4.12: a merge waits while another holds the merge lock'
    Assert-That ([long] $race.Rows_ -eq $allIds.Count -and [long] $race.Distinct_ -eq $allIds.Count -and [long] $race.Interactions -eq $allIds.Count -and "$($race.Merge_)" -eq '0/50') `
        "and then finds the other's records there: nothing appended twice ($($race.Rows_) rows, $($race.Distinct_) distinct; $($race.Merge_) new/already loaded)"
    Invoke-SqlText -Sql 'DROP TABLE dbo.HeldAudit; DROP TABLE dbo.HeldInteraction;'

    # --- The searches a load took are kept with it ---------------------------
    # The merge empties the load's windows, so nothing said what a night that finished had asked of Microsoft Purview:
    # Admin > Health read "0 submitted" after every run that succeeded. A load of three searches here: one over the
    # service's cap and split in two, and one of the halves asked for three times.
    Invoke-SqlText -Sql @"
DECLARE @parent BIGINT;
INSERT INTO audit.CopilotLoad (RangeStartUtc, RangeEndUtc, Operations, DownloadedUtc) VALUES ('2026-08-08', '2026-08-09', N'CopilotInteraction', SYSUTCDATETIME());
INSERT INTO audit.CopilotLoadWindow (StartUtc, EndUtc, Status, Attempts, Submissions) VALUES ('2026-08-08', '2026-08-09', 'Split', 1, 1);
SET @parent = SCOPE_IDENTITY();
INSERT INTO audit.CopilotLoadWindow (ParentWindowId, StartUtc, EndUtc, Status, Attempts, Submissions) VALUES
    (@parent, '2026-08-08', '2026-08-08T12:00:00', 'Loaded', 1, 1), (@parent, '2026-08-08T12:00:00', '2026-08-09', 'Loaded', 3, 3);
"@
    [void] (Invoke-SqlText -As Table -Sql 'EXEC audit.usp_MergeCopilotLoad;')
    $kept = (Invoke-SqlText -As Table -Sql 'SELECT TOP (1) Searches, SearchesResubmitted, (SELECT COUNT(*) FROM audit.CopilotLoadWindow) AS WindowsLeft FROM audit.CopilotLoad ORDER BY LoadId DESC;').Rows[0]
    Assert-That ([int] $kept.Searches -eq 3 -and [int] $kept.SearchesResubmitted -eq 1 -and [int] $kept.WindowsLeft -eq 0) `
        "the merge keeps how many searches a load took and how many were resubmitted, before it empties the windows ($($kept.Searches) searches, $($kept.SearchesResubmitted) resubmitted)"
    $engineKept = [int] (Invoke-SqlText -As Scalar -Parameters @{ '@l' = $l1.LoadId } -Sql 'SELECT ISNULL(Searches, 0) FROM audit.CopilotLoad WHERE LoadId = @l;')
    Assert-That ($engineKept -ge 1) "and a load the engine downloaded has its own count ($engineKept search(es) for Aug 4-9)"

    # --- 2.4.12: a refresh that fails after the merge is made up by the next run --------
    # The load was marked merged before the derived tables were refreshed, and each run refreshed from its own load's
    # start only: a failure past the lookback, or a re-run that found nothing new to load, left those days out for
    # good. A flatten stand-in here records where each refresh starts, and fails when told to.
    Invoke-SqlText -Sql @"
CREATE TABLE dbo.FlattenCall (CallId INT IDENTITY(1, 1) NOT NULL, SinceUtc DATETIME2(0) NULL, Failed BIT NOT NULL);
CREATE TABLE dbo.FlattenFail (Fail BIT NOT NULL);
EXEC (N'CREATE PROCEDURE audit.usp_FlattenCopilotAudit @SinceUtc DATETIME2(0) = NULL AS
BEGIN
    DECLARE @f BIT = CASE WHEN EXISTS (SELECT 1 FROM dbo.FlattenFail) THEN 1 ELSE 0 END;
    INSERT INTO dbo.FlattenCall (SinceUtc, Failed) VALUES (@SinceUtc, @f);
    IF @f = 1 THROW 50999, N''The flatten stopped (test).'', 1;
END;');
"@
    $incremental = {
        # -Incremental as the nightly runs it, with the data already reaching Aug 9: nothing new to download.
        & {
            . (Join-Path $root 'Invoke-CopilotAuditBackfill.ps1')
            $ConnectionString = New-ConnString $script:ITDatabase
            $EndDate = [datetime]::new(2026, 8, 9, 0, 0, 0, [DateTimeKind]::Utc)
            $script:SqlConnStr = New-SqlConnectionString
            Invoke-IncrementalLoad 6>$null
        }
    }
    $lastCall = { (Invoke-SqlText -As Table -Sql 'SELECT TOP (1) CONVERT(varchar(16), SinceUtc, 120) AS SinceUtc, Failed FROM dbo.FlattenCall ORDER BY CallId DESC;').Rows[0] }

    # The loads above were merged by calling the procedure, with no refresh: all four are owed one, from Aug 4.
    & $incremental
    $c = & $lastCall
    $owedNow = [int] (Invoke-SqlText -As Scalar -Sql 'SELECT COUNT(*) FROM audit.CopilotLoad WHERE MergedUtc IS NOT NULL AND DerivedRefreshedUtc IS NULL;')
    Assert-That ("$($c.SinceUtc)" -eq '2026-08-04 00:00' -and $owedNow -eq 0) "2.4.12: with nothing new to load, a run still makes up the refresh merged loads owe, from the first one's start ($($c.SinceUtc))"

    # A downloaded load (Aug 7 - 9, nothing new in it) whose refresh fails after its merge.
    Invoke-SqlText -Sql @"
INSERT INTO audit.CopilotLoad (RangeStartUtc, RangeEndUtc, Operations, DownloadedUtc) VALUES ('2026-08-07', '2026-08-09', N'CopilotInteraction', SYSUTCDATETIME());
INSERT INTO audit.CopilotLoadWindow (StartUtc, EndUtc, Status, Attempts) VALUES ('2026-08-07', '2026-08-09', 'Loaded', 1);
INSERT INTO dbo.FlattenFail (Fail) VALUES (1);
"@
    $failed = try { & $incremental; '' } catch { $_.Exception.GetBaseException().Message }
    $stuck = (Invoke-SqlText -As Table -Sql 'SELECT TOP (1) CASE WHEN MergedUtc IS NULL THEN 0 ELSE 1 END AS Merged, CASE WHEN DerivedRefreshedUtc IS NULL THEN 0 ELSE 1 END AS Refreshed FROM audit.CopilotLoad ORDER BY LoadId DESC;').Rows[0]
    Assert-That ($failed -like '*flatten stopped*' -and [int] $stuck.Merged -eq 1 -and [int] $stuck.Refreshed -eq 0) "the load merges, the refresh fails, and the refresh is recorded as owed ($failed)"
    Invoke-SqlText -Sql 'DELETE FROM dbo.FlattenFail;'
    & $incremental
    $c = & $lastCall
    $owedNow = [int] (Invoke-SqlText -As Scalar -Sql 'SELECT COUNT(*) FROM audit.CopilotLoad WHERE MergedUtc IS NOT NULL AND DerivedRefreshedUtc IS NULL;')
    Assert-That ("$($c.SinceUtc)" -eq '2026-08-07 00:00' -and [int] $c.Failed -eq 0 -and $owedNow -eq 0) "and the next run, with nothing new, refreshes from that load's start ($($c.SinceUtc))"
    & $incremental
    $calls = [int] (Invoke-SqlText -As Scalar -Sql 'SELECT COUNT(*) FROM dbo.FlattenCall;')
    Assert-That ($calls -eq 3) "then nothing is owed, and a run with nothing to load refreshes nothing ($calls refreshes in all)"
    Invoke-SqlText -Sql 'DROP PROCEDURE audit.usp_FlattenCopilotAudit; DROP TABLE dbo.FlattenCall; DROP TABLE dbo.FlattenFail;'
}
finally {
    Stop-MockGraph $loadMock
    $script:ITDatabase = $mainDatabase
}

# ---------------------------------------------------------------------------
# 12. The bi star schema, over what sections 8-10 left in the main database:
# section 10's session rows in audit.RawLogFlattened and the sessions built
# from them, sections 9 and 10's license events, section 8's enrichment.
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

# User C has no license history, only today's enrichment: licensed since
# 12:00Z on the 16th (08:00 Eastern). One interaction before, one after.
# User A's license comes from the history section 10 added (14:00Z on the
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
-- An agent the record names only by AgentId, with its friendly name.
UPDATE audit.RawLogFlattened SET AgentId = N'agent-1', AgentName = N'Old name' WHERE RecordId = N's-08';
-- A Copilot Studio agent named in AppIdentity, as on a live tenant. Its earlier record (s-09, 14:05) carries an
-- AgentId too and the name its maker first gave it; its later one (s-10, 14:20) no id, and the name after a rename.
UPDATE audit.RawLogFlattened SET AppIdentity = N'Copilot.Studio.Default-7d53f522-72db-4957-aa26-2d1fe476b96f-crd20_expenseAgent' WHERE RecordId IN (N's-10', N's-09');
UPDATE audit.RawLogFlattened SET AgentId = N'agent-1', AgentName = N'Expense bot' WHERE RecordId = N's-09';
UPDATE audit.RawLogFlattened SET AgentName = N'Expense agent' WHERE RecordId = N's-10';
-- One of Microsoft's, which the log never names here.
UPDATE audit.RawLogFlattened SET AppIdentity = N'MicrosoftAgent.OutlookDraftsChatSteering.BuiltIn_DraftsAgent' WHERE RecordId = N's-04';
UPDATE audit.RawLogFlattened SET AppIdentity = N'Copilot.M365Copilot.WebChat' WHERE RecordId = N's-01';
-- The flatten catalogs each batch's agents; these rows were written here, so the catalog is read from them.
DELETE FROM audit.AgentCatalog;
EXEC audit.usp_CatalogAgents;
"@

$biRefused = { param([string] $Sql) try { [void] (Invoke-SqlText -As Table -Sql $Sql); return '' } catch { return $_.Exception.GetBaseException().Message } }
$licDb = $script:ITDatabase

# A window on the first run is widened to everything: there is nothing before it yet.
Invoke-SqlText -Sql "EXEC bi.usp_RefreshBi @SinceUtc = '2026-09-19', @LicenseEventDatabase = N'$licDb';" -ShowMessages
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
JOIN bi.Models AS m ON m.ModelKey = f.ModelKey JOIN bi.LicenseStatuses AS l ON l.LicenseStatusKey = f.LicenseStatusKey;
"@)
Assert-That ($joined -eq $flatRows) "every interaction finds its date, user, app, agent, model and license status ($joined of $flatRows)"
$lineage = [int] (Invoke-SqlText -As Scalar -Sql "SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID(N'bi.Interactions') AND name IN (N'RecordId', N'CreatedUtc');")
Assert-That ($lineage -eq 0) 'the view the model imports leaves out the lineage columns'

$byUser = Invoke-SqlText -As Table -Sql @"
SELECT u.UserPrincipalName AS Upn,
       STRING_AGG(CAST(f.LicenseStatusKey AS varchar(1)), '') WITHIN GROUP (ORDER BY f.CreatedUtc) AS Statuses
FROM bi.FactInteraction AS f JOIN bi.DimUser AS u ON u.UserKey = f.UserKey
GROUP BY u.UserPrincipalName;
"@
$st = @{}; foreach ($r in $byUser.Rows) { $st["$($r.Upn)"] = "$($r.Statuses)" }
Assert-That ($st['a@contoso.com'] -eq '2221111') `
    "license at the time of use from the history: not licensed before 14:00Z on the 14th, licensed after ($($st['a@contoso.com']))"
Assert-That ($st['b@contoso.com'] -eq '222') "no history, not licensed today: not licensed throughout ($($st['b@contoso.com']))"
Assert-That ($st['c@contoso.com'] -eq '21') "no history, licensed today: licensed from LicensedSinceUtc only ($($st['c@contoso.com']))"
Assert-That ($st['(unknown user)'] -eq '0') "an interaction with no user is kept, under '(unknown user)', license Unknown ($($st['(unknown user)']))"

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
       (SELECT CONCAT(AgentName, '/', AgentSource, '/', CAST(NamedInAudit AS INT)) FROM bi.DimAgent WHERE AgentId = N'agent-1') AS IdAgent,
       (SELECT CONCAT(g.AgentName, '/', g.AgentSource, '/', CAST(g.NamedInAudit AS INT)) FROM bi.FactInteraction AS f JOIN bi.DimAgent AS g ON g.AgentKey = f.AgentKey WHERE f.RecordId = N's-10') AS StudioAgent,
       (SELECT CONCAT(g.AgentName, '/', g.AgentSource, '/', CAST(g.NamedInAudit AS INT)) FROM bi.FactInteraction AS f JOIN bi.DimAgent AS g ON g.AgentKey = f.AgentKey WHERE f.RecordId = N's-04') AS MsAgent,
       (SELECT CONCAT(g.AgentName, '/', g.AgentSource) FROM bi.FactInteraction AS f JOIN bi.DimAgent AS g ON g.AgentKey = f.AgentKey WHERE f.RecordId = N's-09') AS S09Agent,
       (SELECT COUNT(DISTINCT f.AgentKey) FROM bi.FactInteraction AS f WHERE f.RecordId IN (N's-09', N's-10')) AS StudioKeys,
       (SELECT STRING_AGG(CONCAT(i.AgentId, '|', i.AgentSource, '|', ISNULL(i.DerivedName, N'-')), ' ; ') WITHIN GROUP (ORDER BY v.n)
        FROM (VALUES (1, N'Copilot.Studio.Declarative.T_0a1b2c3d-1111-2222-3333-444455556666', CAST(NULL AS NVARCHAR(400))),
                     (2, N'Copilot.MicrosoftCopilot.BizChat', N'CopilotStudio.Declarative.9f8e7d6c'),
                     (3, N'MicrosoftAgent.Researcher', NULL), (4, NULL, NULL)) AS v (n, App, Id)
        CROSS APPLY bi.fnAgentIdentity(v.App, v.Id) AS i) AS Identities,
       (SELECT COUNT(*) FROM bi.FactInteraction AS f JOIN bi.DimAgent AS g ON g.AgentKey = f.AgentKey WHERE g.AgentId = N'(none)') AS NoAgent,
       (SELECT m.ModelName FROM bi.FactInteraction AS f JOIN bi.DimModel AS m ON m.ModelKey = f.ModelKey WHERE f.RecordId = N's-01') AS S01Model,
       (SELECT Department FROM bi.DimUser WHERE UserPrincipalName = N'a@contoso.com') AS ADept,
       (SELECT CONCAT(OrgDepth, ':', OrgLevel1, '>', OrgLevel2, '|', ManagerName, '|', EmployeeType, '|', DirectoryStatus)
        FROM bi.DimUser WHERE UserPrincipalName = N'a@contoso.com') AS AOrg,
       (SELECT CopilotLicenseToday FROM bi.DimUser WHERE UserPrincipalName = N'b@contoso.com') AS BToday,
       (SELECT DirectoryStatus FROM bi.DimUser WHERE UserPrincipalName = N'(unknown user)') AS UnknownDir;
"@).Rows[0]
Assert-That ("$($dims.OfficeCat)" -eq 'Copilot Chat' -and "$($dims.WordCat)" -eq 'In-App Copilot') `
    "apps carry the sessions' category mapping ($($dims.OfficeCat), $($dims.WordCat))"
Assert-That ("$($dims.IdAgent)" -eq 'Old name/Agent ID/1' -and [int] $dims.NoAgent -eq ($flatRows - 4)) `
    "an agent the record names only by AgentId is keyed on it and carries its friendly name; interactions without one - a non-agent AppIdentity included - map to '(no agent)' ($($dims.IdAgent), $($dims.NoAgent))"
Assert-That ("$($dims.StudioAgent)" -eq 'Expense agent/Copilot Studio/1') `
    "2.4.11: a Copilot Studio agent shows the friendly name the audit log gave it last, not its schema name ($($dims.StudioAgent))"
Assert-That ([int] $dims.StudioKeys -eq 1 -and "$($dims.S09Agent)" -eq 'Expense agent/Copilot Studio') `
    "the agent AppIdentity names stays one agent whether or not a record carries an AgentId, and its earlier record shows the later name ($($dims.S09Agent))"
Assert-That ("$($dims.MsAgent)" -eq 'OutlookDraftsChatSteering/Microsoft/0') "an agent the log never names is named from its id: Microsoft's own name for it ($($dims.MsAgent))"
Assert-That ("$($dims.Identities)" -eq 'Copilot.Studio.Declarative.T_0a1b2c3d-1111-2222-3333-444455556666|Copilot Studio|Declarative.T_0a1b2c3d-1111-2222-3333-444455556666 ; CopilotStudio.Declarative.9f8e7d6c|Agent ID|- ; MicrosoftAgent.Researcher|Microsoft|Researcher ; (none)|(no agent)|-') `
    "bi.fnAgentIdentity: an identity ending in an id is not cut to its last twelve characters; an AgentId names the agent only where AppIdentity names none ($($dims.Identities))"

# A rename: the agent's next record carries a new name. The catalog takes it, and the same key is relabeled, in facts
# no refresh touches; an older record arriving late changes nothing.
$studioKey = [int] (Invoke-SqlText -As Scalar -Sql "SELECT AgentKey FROM bi.FactInteraction WHERE RecordId = N's-10';")
Invoke-SqlText -Sql @"
UPDATE audit.RawLogFlattened SET AgentName = N'Expense desk' WHERE RecordId = N's-10';
UPDATE audit.RawLogFlattened SET AgentName = N'Ancient name' WHERE RecordId = N's-09';
EXEC audit.usp_CatalogAgents;
EXEC bi.usp_NameAgents;
"@
$ren = (Invoke-SqlText -As Table -Sql @"
SELECT (SELECT STRING_AGG(CONCAT(g.AgentKey, ':', g.AgentName), ',') WITHIN GROUP (ORDER BY f.RecordId)
        FROM bi.FactInteraction AS f JOIN bi.Agents AS g ON g.AgentKey = f.AgentKey WHERE f.RecordId IN (N's-09', N's-10')) AS Named,
       (SELECT COUNT(*) FROM bi.DimAgent) AS Agents;
"@).Rows[0]
Assert-That ("$($ren.Named)" -eq "${studioKey}:Expense desk,${studioKey}:Expense desk" -and [int] $ren.Agents -eq 4) `
    "a renamed agent keeps its key and all its history takes the new name; a name on an older record does not undo it ($($ren.Named); $($ren.Agents) agents)"
Assert-That ("$($dims.S01Model)" -eq 'gpt-5') "the model dimension carries provider and name ($($dims.S01Model))"
Assert-That ("$($dims.ADept)" -eq 'Finance' -and "$($dims.BToday)" -eq 'Not licensed' -and "$($dims.UnknownDir)" -eq 'Not looked up') `
    "users carry the AD attributes and today's license ($($dims.ADept), $($dims.BToday), $($dims.UnknownDir))"
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
       (SELECT STRING_AGG(CAST(f.LicenseStatusKey AS varchar(1)), '') WITHIN GROUP (ORDER BY f.SessionStart)
        FROM bi.FactSession AS f JOIN bi.DimUser AS u ON u.UserKey = f.UserKey WHERE u.UserPrincipalName = N'a@contoso.com') AS AStatuses
FROM bi.FactSession;
"@).Rows[0]
Assert-That ([int] $fs.N -eq $sessCount -and "$($fs.AStatuses)" -eq '2111') `
    "sessions come across with their license at the time ($($fs.N) of $sessCount, user A $($fs.AStatuses))"

$skuChanges = [int] (Invoke-SqlText -As Scalar -Sql 'SELECT COUNT(*) FROM (SELECT DISTINCT RecordId, Sku, Change FROM audit.vLicenseSkuChange WHERE TargetUpn IS NOT NULL) AS x;')
$fc = (Invoke-SqlText -As Table -Sql @"
SELECT COUNT(*) AS N,
       (SELECT CONCAT(f.DateKey, ':', f.Change, ':', k.Sku) FROM bi.FactLicenseChange AS f
        JOIN bi.DimSku AS k ON k.SkuKey = f.SkuKey WHERE f.RecordId = 'lic-a1') AS A
FROM bi.FactLicenseChange;
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
Assert-That ($ldd['a@contoso.com'] -eq '20260914,20260915,20260916,20260917') "user A counts as licensed from the day the license was assigned ($($ldd['a@contoso.com']))"
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
Invoke-SqlText -Sql "EXEC bi.usp_RefreshBi @SinceUtc = '2026-09-19', @LicenseEventDatabase = N'$licDb';" -ShowMessages
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
Invoke-SqlText -Sql "EXEC audit.usp_BuildCopilotSessions @LicenseEventDatabase = N'$licDb', @SinceUtc = '2026-09-19';" -ShowMessages
Invoke-SqlText -Sql "EXEC bi.usp_RefreshBi @SinceUtc = '2026-09-19', @LicenseEventDatabase = N'$licDb';" -ShowMessages
$ss = & $sessSync
Assert-That ([int] $ss.Missing -eq 0 -and [int] $ss.Extra -eq 0 -and [int] $ss.Written -lt [int] $ss.Total) `
    "after an incremental session build, bi.FactSession matches by copying only the rebuilt days ($($ss.Written) of $($ss.Total) written)"
Invoke-SqlText -Sql "EXEC audit.usp_BuildCopilotSessions @LicenseEventDatabase = N'$licDb';"
Invoke-SqlText -Sql "EXEC bi.usp_RefreshBi @SinceUtc = '2026-09-19', @LicenseEventDatabase = N'$licDb';"
$ss = & $sessSync
Assert-That ([int] $ss.Missing -eq 0 -and [int] $ss.Extra -eq 0 -and [int] $ss.Written -eq [int] $ss.Total) `
    "after a full session build renumbers every key, the next refresh copies them all ($($ss.Written) of $($ss.Total))"
Invoke-SqlText -Sql "EXEC bi.usp_RefreshBi @SinceUtc = '2026-09-19', @LicenseEventDatabase = N'$licDb';"
$ss = & $sessSync
Assert-That ([int] $ss.Written -eq 0 -and [int] $ss.Missing -eq 0) "with no session build since, a refresh copies no sessions ($($ss.Written))"

$msg = & $biRefused "EXEC bi.usp_RefreshBi @SinceUtc = '2026-09-19', @ReportingTimeZone = N'UTC';"
Assert-That ($msg -like '*needs a full rebuild*') "a partial refresh in a different time zone is refused ($msg)"
# 2.4.12: the refresh holds a lock while it runs, the session's. One that fails lets it go, though its pooled
# connection stays open: asked from another session, with no pool, the lock is free.
$lockFree = {
    $probe = [System.Data.SqlClient.SqlConnection]::new((New-ConnString $script:ITDatabase) + ';Pooling=false')
    $probe.Open()
    try { $q = $probe.CreateCommand(); $q.CommandText = "SELECT APPLOCK_TEST(N'public', N'bi.usp_RefreshBi', N'Exclusive', N'Session');"; [int] $q.ExecuteScalar() }
    finally { $probe.Dispose() }
}
Assert-That ((& $lockFree) -eq 1) '2.4.12: a refused refresh lets its lock go, though its pooled connection stays open'
$msg = & $biRefused "EXEC bi.usp_RefreshBi @LicenseEventDatabase = N'NoSuchDatabase';"
Assert-That ($msg -like '*no audit.vLicenseSkuChange*') "a license database without the license views is refused ($msg)"

# 2.4.12: one refresh at a time. A full rebuild holds nothing on the fact between its months, so a refresh that started
# meanwhile emptied the fact under it and both filled it: those months twice. A session holding the refresh's lock
# stands in for one running here: a second refresh waits for it, and runs once it has let go.
$holder = [System.Data.SqlClient.SqlConnection]::new((New-ConnString $script:ITDatabase) + ';Pooling=false')
$second = [System.Data.SqlClient.SqlConnection]::new((New-ConnString $script:ITDatabase) + ';Pooling=false')
try {
    $holder.Open(); $second.Open()
    $holdCmd = $holder.CreateCommand()
    $holdCmd.CommandText = "DECLARE @r INT; EXEC @r = sp_getapplock @Resource = N'bi.usp_RefreshBi', @LockMode = 'Exclusive', @LockOwner = 'Session', @LockTimeout = 0; SELECT @r;"
    $took = [int] $holdCmd.ExecuteScalar()
    $runs0 = [int] (Invoke-SqlText -As Scalar -Sql 'SELECT COUNT(*) FROM bi.RefreshRun;')
    $refreshCmd = $second.CreateCommand(); $refreshCmd.CommandTimeout = 600
    $refreshCmd.CommandText = "EXEC bi.usp_RefreshBi @SinceUtc = '2026-09-19', @LicenseEventDatabase = N'$licDb';"
    $refresh = $refreshCmd.ExecuteNonQueryAsync()
    Start-Sleep -Seconds 3
    $waited = -not $refresh.IsCompleted
    $runsHeld = [int] (Invoke-SqlText -As Scalar -Sql 'SELECT COUNT(*) FROM bi.RefreshRun;')
    $holdCmd.CommandText = "EXEC sp_releaseapplock @Resource = N'bi.usp_RefreshBi', @LockOwner = 'Session';"
    [void] $holdCmd.ExecuteNonQuery()
    [void] $refresh.Wait(600000)
    $runsAfter = [int] (Invoke-SqlText -As Scalar -Sql 'SELECT COUNT(*) FROM bi.RefreshRun;')
}
finally { $second.Dispose(); $holder.Dispose() }
Assert-That ($took -eq 0 -and $waited -and $runsHeld -eq $runs0 -and $runsAfter -eq $runs0 + 1) `
    "2.4.12: a bi refresh that starts while another runs waits for it, and runs once it has finished ($runsHeld, then $runsAfter refreshes)"

# --- A full rebuild picks up everything, keys unchanged ---------------------
Invoke-SqlText -Sql "EXEC bi.usp_RefreshBi @LicenseEventDatabase = N'$licDb';" -ShowMessages
$r = (Invoke-SqlText -As Table -Sql @"
SELECT (SELECT COUNT_BIG(*) FROM bi.FactInteraction) AS Rows_,
       (SELECT a.App FROM bi.FactInteraction AS f JOIN bi.DimApp AS a ON a.AppKey = f.AppKey WHERE f.RecordId = N's-01') AS S01,
       (SELECT COUNT(*) FROM bi.DimUser) AS Users,
       (SELECT UserKey FROM bi.DimUser WHERE UserPrincipalName = N'a@contoso.com') AS AKey;
"@).Rows[0]
Assert-That ([long] $r.Rows_ -eq ($flatRows + 1) -and "$($r.S01)" -eq 'Excel') "a full rebuild rewrites every row ($($r.Rows_), s-01 now $($r.S01))"
Assert-That ([int] $r.Users -eq $usersBefore -and [int] $r.AKey -eq $aKey) "and re-registers no user: keys survive a rebuild ($($r.Users) users)"

# --- 2.4.12: today's license lookup, where it is newer than the history and contradicts it ------------------
# An account renamed from renamed.old to renamed.new: the history has its license under the old sign-in and no removal,
# and the lookup no longer finds that sign-in. Its licensed days end with the last day it was used; before, it was
# licensed every day to today, the same person twice. And twosku, whose only Copilot changes are a second SKU added
# and taken away, whom the lookup finds licensed since January: licensed throughout, where before not after the removal.
Invoke-SqlText -Sql @"
INSERT INTO audit.LicenseEvent (RecordId, CreatedDateTime, ActivityDate, Operation, ActorUpn, TargetUpn, CorrelationId) VALUES
    ('lic-r1', '2026-09-14T14:00:00', '2026-09-14', 'Change user license', 'admin@contoso.com', 'renamed.old@contoso.com', 'corr-r1'),
    ('lic-t1', '2026-09-14T14:00:00', '2026-09-14', 'Change user license', 'admin@contoso.com', 'twosku@contoso.com', 'corr-t1'),
    ('lic-t2', '2026-09-15T14:00:00', '2026-09-15', 'Change user license', 'admin@contoso.com', 'twosku@contoso.com', 'corr-t2');
INSERT INTO audit.LicenseEventProperty (RecordId, Ordinal, [Name], OldValue, NewValue, Source) VALUES
    ('lic-r1', 0, 'AssignedLicense', '[]', '["[SkuName=Microsoft_365_Copilot, AccountId=t, SkuId=x, DisabledPlans=[]]"]', 'record'),
    ('lic-t1', 0, 'AssignedLicense', '[]', '["[SkuName=Copilot_Studio_User, AccountId=t, SkuId=y, DisabledPlans=[]]"]', 'record'),
    ('lic-t2', 0, 'AssignedLicense', '["[SkuName=Copilot_Studio_User, AccountId=t, SkuId=y, DisabledPlans=[]]"]', '[]', 'record');
EXEC audit.usp_RefreshLicenseSkuChange @All = 1;   -- written here, not shredded (2.5.4)
INSERT INTO audit.CopilotLicense (UserPrincipalName, IsCopilotLic, SkuPartNumber, LicensedSinceUtc, DirectoryStatus) VALUES
    (N'renamed.old@contoso.com', NULL, NULL, NULL, N'NotFound'),
    (N'renamed.new@contoso.com', 1, N'Microsoft_365_Copilot', '2026-09-17T12:00:00', N'Found'),
    (N'twosku@contoso.com', 1, N'Microsoft_365_Copilot', '2026-01-01T00:00:00', N'Found');
INSERT INTO audit.RawLogFlattened (RecordId, [TimeStamp], UtcTimeStamp, [User], App, FlattenedUtc) VALUES
    (N'r-01', N'2026-09-16T15:00:00Z', '2026-09-16T15:00:00', N'renamed.old@contoso.com', N'Word', SYSUTCDATETIME()),
    (N'r-02', N'2026-09-18T15:00:00Z', '2026-09-18T15:00:00', N'renamed.new@contoso.com', N'Word', SYSUTCDATETIME()),
    (N't-01', N'2026-09-17T15:00:00Z', '2026-09-17T15:00:00', N'twosku@contoso.com', N'Word', SYSUTCDATETIME());
"@
Invoke-SqlText -Sql "EXEC bi.usp_RefreshBi @LicenseEventDatabase = N'$licDb';"
$lk = (Invoke-SqlText -As Table -Sql @"
WITH d AS (SELECT u.UserPrincipalName AS Upn, l.DateKey FROM bi.FactLicensedDay AS l JOIN bi.DimUser AS u ON u.UserKey = l.UserKey)
SELECT (SELECT STRING_AGG(CAST(DateKey AS varchar(8)), ',') WITHIN GROUP (ORDER BY DateKey) FROM d WHERE Upn = N'renamed.old@contoso.com') AS OldDays,
       (SELECT CONCAT(MIN(DateKey), '-', MAX(DateKey)) FROM d WHERE Upn = N'renamed.new@contoso.com') AS NewDays,
       (SELECT CONCAT(MIN(DateKey), '-', MAX(DateKey)) FROM d WHERE Upn = N'twosku@contoso.com') AS TwoDays,
       (SELECT MIN(DateKey) FROM bi.FactLicensedDay) AS FirstDay,
       (SELECT MAX(DateKey) FROM bi.FactLicensedDay) AS Today,
       (SELECT CAST(LicenseStatusKey AS INT) FROM bi.FactInteraction WHERE RecordId = N'r-01') AS R01,
       (SELECT CAST(LicenseStatusKey AS INT) FROM bi.FactInteraction WHERE RecordId = N't-01') AS T01;
"@).Rows[0]
Assert-That ("$($lk.OldDays)" -eq '20260914,20260915,20260916' -and [int] $lk.R01 -eq 1 -and "$($lk.NewDays)" -eq "20260917-$($lk.Today)") `
    "2.4.12: a renamed account's old sign-in is licensed to the last day it was used, its interactions still licensed, and the new one from its own date ($($lk.OldDays); $($lk.NewDays))"
Assert-That ([int] $lk.T01 -eq 1 -and "$($lk.TwoDays)" -eq "$($lk.FirstDay)-$($lk.Today)") `
    "and a user the lookup finds licensed, whose history leaves nothing held, is licensed from the date it gives, January, so from the first day with data ($($lk.TwoDays))"
Invoke-SqlText -Sql @"
DELETE FROM audit.LicenseEventProperty WHERE RecordId IN ('lic-r1', 'lic-t1', 'lic-t2');
DELETE FROM audit.LicenseEvent WHERE RecordId IN ('lic-r1', 'lic-t1', 'lic-t2');
EXEC audit.usp_RefreshLicenseSkuChange @All = 1;
DELETE FROM audit.CopilotLicense WHERE UserPrincipalName IN (N'renamed.old@contoso.com', N'renamed.new@contoso.com', N'twosku@contoso.com');
DELETE FROM audit.RawLogFlattened WHERE RecordId IN (N'r-01', N'r-02', N't-01');
EXEC bi.usp_RefreshBi @LicenseEventDatabase = N'$licDb';
"@

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

    # --- 2.4.12: an import's refresh that was skipped or failed is made up ------
    # The import was marked merged before its refresh, and a second run skipped the merged files and returned: an
    # import with -SkipPostProcessing, or whose refresh failed, never reached sessions, bi or the dashboard. And with
    # no -SessionLicenseDatabase the session build read no license history, which a database built with some refused.
    # Stand-ins here record where each refresh starts and which license history the session build is given.
    Invoke-SqlText -Catalog $csvDb -Sql @"
CREATE TABLE dbo.RefreshCall (CallId INT IDENTITY(1, 1) NOT NULL, Step VARCHAR(10) NOT NULL, SinceUtc DATETIME2(0) NULL, Lic NVARCHAR(1000) NULL);
CREATE TABLE dbo.FlattenFail (Fail BIT NOT NULL);
CREATE TABLE audit.CopilotSessionRun (RunId INT IDENTITY(1, 1) NOT NULL, LicenseEventDatabase NVARCHAR(1000) NULL);
INSERT INTO audit.CopilotSessionRun (LicenseEventDatabase) VALUES (N'CopilotLicenseTest');
EXEC (N'CREATE PROCEDURE audit.usp_FlattenCopilotAudit @SinceUtc DATETIME2(0) = NULL AS
BEGIN
    INSERT INTO dbo.RefreshCall (Step, SinceUtc) VALUES (''flatten'', @SinceUtc);
    IF EXISTS (SELECT 1 FROM dbo.FlattenFail) THROW 50999, N''The flatten stopped (test).'', 1;
END;');
EXEC (N'CREATE PROCEDURE audit.usp_BuildCopilotSessions @LicenseEventDatabase NVARCHAR(1000) = NULL, @SinceUtc DATETIME2(0) = NULL AS
    INSERT INTO dbo.RefreshCall (Step, SinceUtc, Lic) VALUES (''sessions'', @SinceUtc, @LicenseEventDatabase);');
"@
    $owedFile = {
        param([string] $Id, [string] $At)
        $o = $all[0].AuditData | ConvertFrom-Json
        $o.Id = $Id; $o.CreationTime = $At
        @([pscustomobject]@{ RecordId = $Id; CreationDate = ''; RecordType = 'CopilotInteraction'; Operation = 'CopilotInteraction'
                             UserId = $all[0].UserId; AuditData = ($o | ConvertTo-Json -Depth 20 -Compress)
                             AssociatedAdminUnits = ''; AssociatedAdminUnitsNames = '' }) |
            Export-Csv (Join-Path $csvDir "export-$Id.csv") -NoTypeInformation -Encoding utf8BOM
    }
    $calls = { @((Invoke-SqlText -Catalog $csvDb -As Table -Sql "SELECT CONCAT(Step, '@', CONVERT(varchar(16), SinceUtc, 120), CASE WHEN Lic IS NULL THEN '' ELSE CONCAT('/', Lic) END) AS C FROM dbo.RefreshCall ORDER BY CallId;").Rows | ForEach-Object { $_.C }) -join ';' }
    $owedImports = { [int] (Invoke-SqlText -Catalog $csvDb -As Scalar -Sql 'SELECT COUNT(*) FROM audit.CopilotCsvImport WHERE MergedUtc IS NOT NULL AND RowsAdded > 0 AND DerivedRefreshedUtc IS NULL;') }
    try {
        & $owedFile 'csv-owed-000001' '2026-08-09T15:00:00'
        & $runTool $csvDb @{ SkipPostProcessing = $true }
        Assert-That ((& $calls) -eq '' -and (& $owedImports) -eq 1) '2.4.12: an import with -SkipPostProcessing merges and owes its refresh'
        # What the nightly -Incremental finds owed: the import's first record (it folds it into its own refresh).
        $nightlyOwes = & {
            . (Join-Path $root 'Invoke-CopilotAuditBackfill.ps1')
            $ConnectionString = New-ConnString $csvDb
            $script:SqlConnStr = New-SqlConnectionString
            Invoke-SqlScript -Path (Join-Path $root 'sql' 'Create-CopilotLoadProcedure.sql')
            Get-OwedRefresh
        }
        Assert-That ($null -ne $nightlyOwes -and $nightlyOwes.SinceUtc -eq [datetime]::new(2026, 8, 9, 15, 0, 0, [DateTimeKind]::Utc) -and $nightlyOwes.ImportId -gt 0) `
            "and the nightly run sees it owed, from the import's first record ($(if ($nightlyOwes) { $nightlyOwes.SinceUtc.ToString('u') }))"
        & $runTool $csvDb
        Assert-That ((& $calls) -eq 'flatten@2026-08-09 15:00;sessions@2026-08-09 15:00/CopilotLicenseTest' -and (& $owedImports) -eq 0) `
            "the next run, with nothing new to import, makes it up, with the license history of the last build ($(& $calls))"

        Invoke-SqlText -Catalog $csvDb -Sql 'TRUNCATE TABLE dbo.RefreshCall; INSERT INTO dbo.FlattenFail (Fail) VALUES (1);'
        & $owedFile 'csv-owed-000002' '2026-08-09T16:00:00'
        $failedImport = try { & $runTool $csvDb; '' } catch { $_.Exception.GetBaseException().Message }
        Assert-That ($failedImport -like '*flatten stopped*' -and (& $owedImports) -eq 1) "an import whose refresh fails after its merge owes it ($failedImport)"
        Invoke-SqlText -Catalog $csvDb -Sql 'TRUNCATE TABLE dbo.RefreshCall; DELETE FROM dbo.FlattenFail;'
        & $runTool $csvDb @{ SessionLicenseDatabase = '' }
        Assert-That ((& $calls) -eq 'flatten@2026-08-09 16:00;sessions@2026-08-09 16:00' -and (& $owedImports) -eq 0) `
            "and running the same command again makes it up; -SessionLicenseDatabase '' reads no license history ($(& $calls))"
    }
    finally {
        Remove-Item -LiteralPath (Join-Path $csvDir 'export-csv-owed-000001.csv'), (Join-Path $csvDir 'export-csv-owed-000002.csv') -ErrorAction SilentlyContinue
    }

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
Invoke-SqlText -Sql "EXEC bi.usp_RefreshBi @LicenseEventDatabase = N'$licDb';"
Invoke-SqlText -Sql 'EXEC dash.usp_RefreshDashboard;' -ShowMessages
# 2.4.7: the refresh keeps the flagged interactions for Security > Flagged interactions, each once, and again on a rerun.
Invoke-SqlText -Sql 'EXEC dash.usp_RefreshDashboard;'
$fl = (Invoke-SqlText -As Table -Sql @"
SELECT (SELECT COUNT(*) FROM dash.FlaggedInteraction) AS Kept,
       (SELECT COUNT(*) FROM bi.FactInteraction WHERE JailbreakDetected = 1 OR PromptInjectionDetected = 1 OR HasBlockedResource = 1) AS Flagged,
       (SELECT COUNT(DISTINCT RecordId) FROM dash.FlaggedInteraction) AS Records;
"@).Rows[0]
Assert-That ([int] $fl.Kept -eq [int] $fl.Flagged -and [int] $fl.Records -eq [int] $fl.Kept) "2.4.7: dash.FlaggedInteraction holds every flagged interaction once ($($fl.Kept) of $($fl.Flagged))"

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
    "labels, sites of labeled content and blocked sites come from bi.FactAccessedResource ($($res -join ' | '))"

# 2.4.12: interactions, not resources. A third file read in the same interaction, with the first one's label and on its
# site, adds nothing to either: before, each resource counted, against one in the page's count of labeled interactions.
Invoke-SqlText -Sql @"
INSERT INTO audit.RawLogAccessedResource (RecordId, Ordinal, [Action], [Type], Name, SiteUrl, Site, SensitivityLabelId, XPIADetected, [Status], IsBlocked) VALUES
    (N's-05', 2, N'Read', N'File', N'c.docx', N'https://contoso.sharepoint.com/sites/fin/Shared Documents/c.docx',
     N'https://contoso.sharepoint.com/sites/fin', '11111111-0000-0000-0000-000000000001', 0, NULL, 0);
EXEC bi.usp_RefreshBi @LicenseEventDatabase = N'$licDb';
EXEC dash.usp_RefreshDashboard;
"@
$perInteraction = (Invoke-SqlText -As Table -Sql @"
SELECT (SELECT COUNT(*) FROM bi.FactAccessedResource WHERE RecordId = N's-05') AS Read_,
       (SELECT SUM(Interactions) FROM dash.ResourceDay WHERE Kind = 'Label' AND Item = N'11111111-0000-0000-0000-000000000001') AS Label1,
       (SELECT SUM(Interactions) FROM dash.ResourceDay WHERE Kind = 'Site' AND Item = N'https://contoso.sharepoint.com/sites/fin') AS Fin;
"@).Rows[0]
Invoke-SqlText -Sql "DELETE FROM audit.RawLogAccessedResource WHERE RecordId = N's-05' AND Ordinal = 2; EXEC bi.usp_RefreshBi @LicenseEventDatabase = N'$licDb'; EXEC dash.usp_RefreshDashboard;"
Assert-That ([int] $perInteraction.Read_ -eq 3 -and [int] $perInteraction.Label1 -eq 1 -and [int] $perInteraction.Fin -eq 1) `
    "2.4.12: dash.ResourceDay counts interactions: two files with one label in one interaction count once for the label and its site ($($perInteraction.Label1), $($perInteraction.Fin))"

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
$ft = [string] (Invoke-SqlText -As Scalar -Sql @"
SELECT STRING_AGG(CONCAT(t.FileType, N'=', x.N), N' | ') WITHIN GROUP (ORDER BY t.FileType)
FROM (SELECT FileTypeKey, COUNT(*) AS N FROM bi.AccessedResources GROUP BY FileTypeKey) AS x JOIN bi.FileTypes AS t ON t.FileTypeKey = x.FileTypeKey;
"@)
Assert-That ($ft -eq 'docx=2 | xlsx=1') "2.4.6: each resource read carries its file type, from its name ($ft)"
Assert-That ("$($bx.LicType)" -eq 'Starter' -and [int] $bx.Memory -eq 1 -and "$($bx.EvLabel)" -eq '11111111-0000-0000-0000-000000000003') `
    "the interaction fact carries the record's license type, memory flag and its own label ($($bx.LicType), $($bx.EvLabel))"

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

# 2.4.12: a full bi rebuild stopped part way, as an upgrade's can be. While it has not finished the rollups are left as
# they are: the dashboard installer's refresh then took the months the rebuild had not reached out of every rollup. The
# nightly run finishes it (its window becomes a full rebuild) and rolls up from its load's start, which now covers every
# day: before, the days before the window kept the figures of the facts before the rebuild, for good. Days rolled up
# from facts the rebuild has since rewritten stand in for those figures here (a thousand interactions more on the first).
$premiumSync = "SELECT (SELECT ISNULL(SUM(CAST(PremiumUse AS BIGINT)), 0) FROM dash.UserDay) AS Rolled, (SELECT ISNULL(SUM(CAST(PremiumUse AS BIGINT)), 0) FROM bi.FactInteraction) AS Stored;"
$dashRuns = 'SELECT COUNT(*) FROM dash.RefreshRun;'
$runsBefore = [int] (Invoke-SqlText -As Scalar -Sql $dashRuns)
Invoke-SqlText -Sql "INSERT INTO bi.RebuildInProgress (StartedUtc) VALUES (SYSUTCDATETIME()); DELETE FROM bi.FactInteraction WHERE RecordId IN (SELECT TOP (3) RecordId FROM bi.FactInteraction ORDER BY CreatedUtc);"
Invoke-SqlText -Sql "IF OBJECT_ID(N'bi.FactInteraction', N'U') IS NOT NULL EXEC dash.usp_RefreshDashboard;"   # as dashboard\Install-CopilotInsights.ps1 runs it
$whileRebuilding = & $daySync
$runsWhile = [int] (Invoke-SqlText -As Scalar -Sql $dashRuns)
Invoke-SqlText -Sql "UPDATE dash.UserDay SET Interactions = Interactions + 1000, PremiumUse = PremiumUse + 1000, LicensedUse = LicensedUse + 1000 WHERE DateKey = (SELECT MIN(DateKey) FROM dash.UserDay);"
Invoke-SqlText -Sql "EXEC bi.usp_RefreshBi @SinceUtc = @s, @LicenseEventDatabase = N'$licDb'; EXEC dash.usp_RefreshDashboard @SinceUtc = @s;" -Parameters @{ '@s' = $since }
$finished = & $daySync
$prem = (Invoke-SqlText -As Table -Sql $premiumSync).Rows[0]
$every = (Invoke-SqlText -As Table -Sql 'SELECT TOP (1) FromDateKey FROM dash.RefreshRun ORDER BY RunId DESC;').Rows[0]
Assert-That ([long] $whileRebuilding.Days_ -eq [long] $ds.Days_ -and $runsWhile -eq $runsBefore) `
    "2.4.12: while a full bi rebuild has not finished, a dashboard refresh leaves the rollups as they are ($($whileRebuilding.Days_) of $($ds.Days_) interactions)"
Assert-That ([long] $finished.Days_ -eq [long] $finished.Facts -and [long] $finished.Facts -eq [long] $ds.Facts -and [long] $prem.Rolled -eq [long] $prem.Stored -and $every.FromDateKey -is [DBNull]) `
    "and the rollup after the run that finishes it covers every day, though asked for its window ($($finished.Days_) of $($finished.Facts) interactions, Premium $($prem.Rolled) of $($prem.Stored))"
Invoke-SqlText -Sql 'EXEC dash.usp_RefreshDashboard @SinceUtc = @s;' -Parameters @{ '@s' = $since }
$windowAgain = (Invoke-SqlText -As Table -Sql 'SELECT TOP (1) FromDateKey FROM dash.RefreshRun ORDER BY RunId DESC;').Rows[0]
Assert-That ($windowAgain.FromDateKey -isnot [DBNull]) "and only that once: the next rollup takes its window again ($($windowAgain.FromDateKey))"

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

# 2.4.12: a manager without a UPN right below the top. The AD load reports their people to the top (ManagerUPN), so the
# chart stays joined, and records the real depth (OrgDepth). Levels came from the chart alone, which made the director
# level 2, and level 2 sees the whole organization; they are level 3, and the clerk under them level 4.
$skip = (Invoke-SqlText -As Table -Sql @"
INSERT INTO audit.UserDirectory (UserPrincipalName, FullName, Department, EmployeeType, IsManager, AccountEnabled, ManagerUPN, ManagerName, OrgDepth) VALUES
    (N'dirx@contoso.com',   N'Director X', N'Risk', N'EMPLOYEE', 1, 1, N'ceo@contoso.com',  N'The CEO',    2),
    (N'clerkx@contoso.com', N'Clerk X',    N'Risk', N'EMPLOYEE', 0, 1, N'dirx@contoso.com', N'Director X', 3);
EXEC dash.usp_RefreshDashboard;
SELECT (SELECT CAST(Level AS INT) FROM dash.Person WHERE Upn = N'dirx@contoso.com') AS Dir,
       (SELECT CAST(Level AS INT) FROM dash.Person WHERE Upn = N'clerkx@contoso.com') AS Clerk,
       (SELECT CAST(Level AS INT) FROM dash.Person WHERE Upn = N'boss@contoso.com') AS Boss,
       (SELECT COUNT(*) FROM dash.OrgClosure WHERE AncestorUpn = N'ceo@contoso.com' AND DescendantUpn = N'clerkx@contoso.com') AS UnderTop;
"@).Rows[0]
Invoke-SqlText -Sql "DELETE FROM audit.UserDirectory WHERE UserPrincipalName IN (N'dirx@contoso.com', N'clerkx@contoso.com'); EXEC dash.usp_RefreshDashboard;"
Assert-That ([int] $skip.Dir -eq 3 -and [int] $skip.Clerk -eq 4 -and [int] $skip.Boss -eq 2 -and [int] $skip.UnderTop -eq 1) `
    "2.4.12: under a manager without a UPN below the top, levels count that manager (director $($skip.Dir), clerk $($skip.Clerk)), and the top still sees them"

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
    # 2.4.10: with no zone named, databases with no records take this server's own time zone; naming one after, while
    # they are still empty, moves them.
    & $installer @conn -Database $freshDb -LicenseDatabase $freshLic 6>$null | Out-Null
    $serverZone = [TimeZoneInfo]::Local.Id
    if ([TimeZoneInfo]::Local.HasIanaId) { $w = $null; $serverZone = if ([TimeZoneInfo]::TryConvertIanaIdToWindowsId($serverZone, [ref] $w)) { $w } else { 'Eastern Standard Time' } }
    if (-not [int] (Invoke-SqlText -Catalog master -As Scalar -Sql "SELECT COUNT(*) FROM sys.time_zone_info WHERE name = N'$($serverZone.Replace("'", "''"))';")) { $serverZone = 'Eastern Standard Time' }
    $zones0 = @($freshDb, $freshLic | ForEach-Object { [string] (Invoke-SqlText -Catalog $_ -As Scalar -Sql 'SELECT audit.ReportingTimeZone();') })
    Assert-That (($zones0 | Select-Object -Unique) -join ',' -eq $serverZone) "2.4.10: with no zone named, new databases take the server's time zone ($($zones0 -join ','); the server is in $serverZone)"
    $out = & $installer @conn -Database $freshDb -LicenseDatabase $freshLic -Dashboard -ReportingTimeZone 'GMT Standard Time' 6>&1 | Out-String
    $made = Invoke-SqlText -Catalog master -As Table -Sql "SELECT name, recovery_model_desc AS Recovery FROM sys.databases WHERE name IN (N'$freshDb', N'$freshLic');"
    Assert-That ($made.Rows.Count -eq 2 -and @($made.Rows | Where-Object Recovery -eq 'SIMPLE').Count -eq 2) 'a fresh install creates both databases, in SIMPLE recovery'
    $objs = [int] (Invoke-SqlText -Catalog $freshDb -As Scalar -Sql "SELECT COUNT(*) FROM sys.objects WHERE OBJECT_ID(N'audit.usp_FlattenCopilotAudit') IS NOT NULL AND OBJECT_ID(N'bi.usp_RefreshBi') IS NOT NULL AND OBJECT_ID(N'dash.usp_RefreshDashboard') IS NOT NULL AND object_id = OBJECT_ID(N'audit.RawLogAccessedResource');")
    $licObjs = [int] (Invoke-SqlText -Catalog $freshLic -As Scalar -Sql "SELECT CASE WHEN OBJECT_ID(N'audit.usp_ShredLicenseEvents') IS NOT NULL THEN 1 ELSE 0 END;")
    Assert-That ($objs -eq 1 -and $licObjs -eq 1) 'and installs every procedure and table in both'
    Assert-That ($out -like '*Load Copilot records into*' -and $out -like '*Backfill the license history*') 'and says what is left to do'
    $zones = @($freshDb, $freshLic | ForEach-Object { [string] (Invoke-SqlText -Catalog $_ -As Scalar -Sql 'SELECT audit.ReportingTimeZone();') })
    Assert-That (($zones -join ',') -eq 'GMT Standard Time,GMT Standard Time' -and $out -like '*reporting time zone: GMT Standard Time*') `
        "-ReportingTimeZone is stored in both databases, where the procedures read it ($($zones -join ','))"
    $badZone = try { & $installer @conn -Database $freshDb -LicenseDatabase $freshLic -ReportingTimeZone 'Mars Standard Time' 6>$null | Out-Null; '' } catch { $_.Exception.Message }
    Assert-That ($badZone -like '*not a time zone SQL Server knows*') 'an unknown zone is refused'
    $again = & $installer @conn -Database $freshDb -LicenseDatabase $freshLic -Dashboard 6>&1 | Out-String
    Assert-That ($again -like '*Done.*' -and $again -notlike '*flattening*') 'running it again changes nothing and builds nothing'

    # 2.4.12: a reporting account reads the bi views and nothing else. It was given SELECT on the whole bi schema, the
    # tables under the views included, so bi.DimUser's real names sat beside privacy mode's pseudonyms. it_report stands
    # in for an account given -ReportingAccount before 2.4.12 (a user without a login: no server login is made here),
    # it_other for another that reads the whole schema.
    Invoke-SqlText -Catalog $freshDb -Sql 'CREATE USER it_report WITHOUT LOGIN; GRANT SELECT ON SCHEMA::bi TO it_report; CREATE USER it_other WITHOUT LOGIN; GRANT SELECT ON SCHEMA::bi TO it_other;'
    $rep = & $installer @conn -Database $freshDb -LicenseDatabase $freshLic -Dashboard -ReportingAccount 'it_report' -WarningVariable repWarn 3>$null 6>&1 | Out-String
    $reads = (Invoke-SqlText -Catalog $freshDb -As Table -Sql @"
DECLARE @list TABLE (name SYSNAME);
INSERT INTO @list SELECT name FROM sys.views WHERE schema_id = SCHEMA_ID(N'bi');   -- listed before: it_report sees only what it may read
DECLARE @views NVARCHAR(MAX) = N'read', @table NVARCHAR(400) = N'read', @n INT, @v SYSNAME, @s NVARCHAR(400);
EXECUTE AS USER = N'it_report';
DECLARE v CURSOR LOCAL FAST_FORWARD FOR SELECT name FROM @list;
OPEN v; FETCH NEXT FROM v INTO @v;
WHILE @@FETCH_STATUS = 0
BEGIN
    SET @s = N'DECLARE @c INT = (SELECT COUNT(*) FROM (SELECT TOP (1) * FROM bi.' + QUOTENAME(@v) + N') AS x);';
    BEGIN TRY EXEC (@s); END TRY BEGIN CATCH SET @views = CONCAT(NULLIF(@views, N'read'), @v, N': ', ERROR_MESSAGE(), N' '); END CATCH;
    FETCH NEXT FROM v INTO @v;
END;
CLOSE v; DEALLOCATE v;
BEGIN TRY SET @n = (SELECT COUNT(*) FROM bi.DimUser); END TRY BEGIN CATCH SET @table = ERROR_MESSAGE(); END CATCH;
REVERT;
SELECT @views AS Views_, @table AS Table_,
       (SELECT COUNT(*) FROM sys.views WHERE schema_id = SCHEMA_ID(N'bi')) AS BiViews,
       (SELECT COUNT(*) FROM sys.database_permissions AS p JOIN sys.views AS v ON v.object_id = p.major_id
        WHERE p.class = 1 AND p.permission_name = N'SELECT' AND p.state = 'G' AND v.schema_id = SCHEMA_ID(N'bi')
          AND p.grantee_principal_id = DATABASE_PRINCIPAL_ID(N'CopilotReporting')) AS Granted;
"@).Rows[0]
    Assert-That ("$($reads.Views_)" -eq 'read' -and "$($reads.Table_)" -like '*permission was denied*') "2.4.12: a reporting account reads every bi view, those the Power BI model imports among them, and not the tables under them ($($reads.Views_); $($reads.Table_))"
    Assert-That ([int] $reads.Granted -eq [int] $reads.BiViews -and [int] $reads.BiViews -gt 20) "every bi view is granted to CopilotReporting ($($reads.Granted) of $($reads.BiViews))"
    Assert-That ($rep -like '*it_report can read the bi views*and nothing else*' -and "$repWarn" -like '*does not hide names from them: it_other. Give*') `
        'an account given -ReportingAccount before is narrowed to them; another that reads the whole schema is named, not changed'
    # 2.4.12: the dashboard installer runs the operations schema after Install-CopilotAudit.ps1, so the views it makes
    # (OrgSecurity, DepartmentWeek and the rest) were not granted to CopilotReporting. The script grants them itself now.
    Invoke-SqlText -Catalog $freshDb -Sql 'REVOKE SELECT ON bi.DepartmentWeek FROM CopilotReporting; REVOKE SELECT ON bi.OrgSecurity FROM CopilotReporting;'
    Invoke-SqlText -Catalog $freshDb -Sql (Get-Content -LiteralPath (Join-Path $root 'sql' 'Create-CopilotOperationsSchema.sql') -Raw) -TimeoutSeconds 600
    $opsGrants = [int] (Invoke-SqlText -Catalog $freshDb -As Scalar -Sql @"
SELECT COUNT(*) FROM sys.database_permissions AS p
WHERE p.class = 1 AND p.permission_name = N'SELECT' AND p.state = 'G' AND p.grantee_principal_id = DATABASE_PRINCIPAL_ID(N'CopilotReporting')
  AND p.major_id IN (OBJECT_ID(N'bi.DepartmentWeek'), OBJECT_ID(N'bi.OrgSecurity'));
"@)
    Assert-That ($opsGrants -eq 2) "2.4.12: the operations schema grants its bi views to CopilotReporting when the role exists ($opsGrants of 2)"

    # 2.5.4: license records are shredded once, not again on every run. The installer read and parsed every record of
    # the license database each time, an upgrade's longest step on a large history, to write the rows already there.
    foreach ($b in [regex]::Split((Get-Content -LiteralPath (Join-Path $root 'sql' 'Create-CopilotCsvImport.sql') -Raw), '(?im)^[ \t]*GO[ \t]*(?:--[^\r\n]*)?\r?$')) { if ($b.Trim()) { Invoke-SqlText -Catalog $freshLic -Sql $b } }
    $freshChunks = @(& $chunkify $payload 'grp-fresh' 2)
    $licDocs = @(
        @{ Id = 'fl-01'; Op = 'Change user license.'; Json = '{"CreationTime":"2026-09-20T10:00:00","Id":"fl-01","Operation":"Change user license.","ResultStatus":"Success","UserId":"admin@contoso.com","ObjectId":"fresh1@contoso.com","InterSystemsId":"corr-f1","ModifiedProperties":[{"Name":"AssignedLicense","OldValue":"[]","NewValue":"[\"[SkuName=Microsoft_365_Copilot, AccountId=t, SkuId=x, DisabledPlans=[]]\"]"}],"Target":[]}' }
        @{ Id = 'fl-02'; Op = 'Update user.'; Json = '{"CreationTime":"2026-09-20T10:05:00","Id":"fl-02","Operation":"Update user.","ResultStatus":"Success","UserId":"sync@contoso.com","ObjectId":"fresh2@contoso.com","InterSystemsId":"corr-f2","ModifiedProperties":[{"Name":"LastDirSyncTime","OldValue":"a","NewValue":"b"}],"Target":[]}' }
        @{ Id = 'fl-03'; Op = 'BotCreate'; Json = '{"CreationTime":"2026-09-20T10:10:00","Id":"fl-03","Operation":"BotCreate","ResultStatus":"Success","UserId":"maker@contoso.com","BotId":"b1","BotSchemaName":"cr1_helper"}' }
        # A chunked change, two days later: fresh4 loses Copilot. Its lists are reassembled onto fl-04, the first fragment's record.
        @{ Id = 'fl-04'; Op = 'Change user license.'; At = '2026-09-22T10:00:00'; Json = (& $mkDoc 'fl-04' 'Change user license.' 'admin@contoso.com' 'fresh4@contoso.com' 'corr-f4' @() $freshChunks[0]) }
        @{ Id = 'fl-05'; Op = 'Change user license.'; At = '2026-09-22T10:00:00'; Json = (& $mkDoc 'fl-05' 'Change user license.' 'admin@contoso.com' 'fresh4@contoso.com' 'corr-f4' @() $freshChunks[1]) }
    )
    foreach ($d in $licDocs) {
        Invoke-SqlText -Catalog $freshLic -Parameters @{ '@id' = $d.Id; '@op' = $d.Op; '@json' = $d.Json; '@at' = $(if ($d['At']) { $d['At'] } else { '2026-09-20T10:00:00' }) } -Sql @"
INSERT INTO audit.CopilotAudit (RecordId, CreatedDateTime, RecordType, Operation, UserId, UserPrincipalName, AuditDataGz, WindowId)
VALUES (@id, @at, 'azureActiveDirectory', @op, 'admin@contoso.com', 'admin@contoso.com', COMPRESS(CAST(@json AS NVARCHAR(MAX))), 0);
"@
    }
    # Kept: events and agent events still stamped as below, so not shredded again. Again: the license events that were.
    $licState = { (Invoke-SqlText -Catalog $freshLic -As Table -Sql @"
SELECT (SELECT COUNT(*) FROM audit.LicenseEvent) AS Events, (SELECT COUNT(*) FROM audit.AgentEvent) AS Agents,
       (SELECT STRING_AGG(CONCAT(TargetUpn, N' ', Sku, N' ', Change), N'; ') WITHIN GROUP (ORDER BY TargetUpn, Sku) FROM audit.vLicenseSkuChange) AS Sku,
       (SELECT COUNT(*) FROM audit.LicenseEvent WHERE ShreddedUtc = '2026-01-01') + (SELECT COUNT(*) FROM audit.AgentEvent WHERE ShreddedUtc = '2026-01-01') AS Kept,
       (SELECT STRING_AGG(CONVERT(NVARCHAR(MAX), RecordId), N',') WITHIN GROUP (ORDER BY RecordId) FROM audit.LicenseEvent WHERE ShreddedUtc > '2026-01-01') AS Again,
       (SELECT COUNT(*) FROM audit.LicenseEventProperty WHERE RecordId = 'fl-04' AND Source = 'chunks') AS ChunkProps,
       (SELECT COUNT(*) FROM audit.PipelineSetting WHERE Name = N'LicenseShredInProgress') AS InProgress,
       (SELECT COUNT(*) FROM audit.PipelineSetting WHERE Name = N'LicenseShredCompleteUtc') AS Complete;
"@).Rows[0] }
    $asOld = "UPDATE audit.LicenseEvent SET ShreddedUtc = '2026-01-01'; UPDATE audit.AgentEvent SET ShreddedUtc = '2026-01-01';"
    $noMark = "DELETE FROM audit.PipelineSetting WHERE Name = N'LicenseShredCompleteUtc';"
    $unassemble = "DELETE FROM audit.LicenseEventProperty WHERE RecordId = 'fl-04' AND Source = 'chunks';"
    $bothChanges = 'fresh1@contoso.com Microsoft_365_Copilot Added; fresh4@contoso.com Microsoft_365_Copilot Removed'

    $shred1 = & $installer @conn -Database $freshDb -LicenseDatabase $freshLic 6>&1 | Out-String
    $s = & $licState
    Assert-That ($shred1 -like '*license events: shredding 5 record(s) (4 not shredded yet)*' -and [int] $s.Events -eq 4 -and [int] $s.Agents -eq 1 -and "$($s.Sku)" -eq $bothChanges) `
        "2.5.4: the installer shreds the license records that are not shredded yet, and keeps their SKU changes ($($s.Events) events, $($s.Agents) agent event; $($s.Sku))"
    Assert-That ([int] $s.InProgress -eq 0 -and [int] $s.Complete -eq 1 -and [int] $s.ChunkProps -eq 2) `
        "a shred of every record that finished leaves its mark, and none of a shred still running ($($s.ChunkProps) properties reassembled from the chunk group)"
    Invoke-SqlText -Catalog $freshLic -Sql $asOld
    $shred2 = & $installer @conn -Database $freshDb -LicenseDatabase $freshLic 6>&1 | Out-String
    $s = & $licState
    Assert-That ($shred2 -like '*every one of 5 record(s) is shredded already*' -and $shred2 -notlike '*shredding*' -and $shred2 -notlike '*shredded again*' -and [int] $s.Kept -eq 5) `
        "and reads none of them again once they are: the next run leaves all $($s.Kept) as they were"
    $shred3 = & $installer @conn -Database $freshDb -LicenseDatabase $freshLic -Rebuild 6>&1 | Out-String
    $s = & $licState
    Assert-That ($shred3 -like '*license events: shredding 5 record(s) (-Rebuild)*' -and [int] $s.Kept -eq 0) 'while -Rebuild shreds every one again'

    # A nightly shred that was stopped part way: its mark (it was shredding from 21 September) and its chunk group not
    # reassembled. The installer has the procedure finish it: the two records of that night, not all five.
    Invoke-SqlText -Catalog $freshLic -Sql "$asOld $unassemble INSERT INTO audit.PipelineSetting (Name, Value) VALUES (N'LicenseShredInProgress', N'2026-09-21T00:00:00');"
    $shred4 = & $installer @conn -Database $freshDb -LicenseDatabase $freshLic 6>&1 | Out-String
    $s = & $licState
    Assert-That ($shred4 -like '*an earlier shred did not finish, so what it was shredding is shredded again*' -and $shred4 -notlike '*shredding 5 record(s)*' -and "$($s.Again)" -eq 'fl-04,fl-05') `
        "a shred that was stopped part way is finished by the next install: what it was shredding is shredded again, and no more ($($s.Again))"
    Assert-That ([int] $s.ChunkProps -eq 2 -and [int] $s.InProgress -eq 0 -and "$($s.Sku)" -eq $bothChanges) 'its chunk group is reassembled, its mark taken away, and the SKU changes are as they were'
    Invoke-SqlText -Catalog $freshLic -Sql "$asOld INSERT INTO audit.PipelineSetting (Name, Value) VALUES (N'LicenseShredInProgress', N'');"
    $shred4b = & $installer @conn -Database $freshDb -LicenseDatabase $freshLic 6>&1 | Out-String
    $s = & $licState
    Assert-That ($shred4b -like '*an earlier shred did not finish, so what it was shredding is shredded again*' -and "$($s.Again)" -eq 'fl-01,fl-02,fl-04,fl-05' -and [int] $s.InProgress -eq 0) `
        "and one that was shredding every record has every record shredded again ($($s.Again))"

    # A license database an earlier release shredded has no mark that its shred finished, and a shred stopped between
    # a batch's events and their properties shows in no count. So: every record once more, unless told that it did finish.
    Invoke-SqlText -Catalog $freshLic -Sql "$asOld $noMark"
    $shred5 = & $installer @conn -Database $freshDb -LicenseDatabase $freshLic 6>&1 | Out-String
    $s = & $licState
    Assert-That ($shred5 -like '*license events: shredding 5 record(s) (once more, to record that every one is shredded*' -and [int] $s.Kept -eq 1 -and [int] $s.Complete -eq 1) `
        "a database shredded before 2.5.4 has its license records shredded once more, and the mark is left ($($s.Kept) agent event left as it was)"
    # With a chunk group whose document holds no property: none to show for it, and no sign of a shred that stopped.
    Invoke-SqlText -Catalog $freshLic -Sql "$asOld $noMark INSERT INTO audit.LicenseEventChunk (ChunkGroupId, Seq, RecordId, Body) VALUES (N'grp-none', 1, 'fl-01', N'{`"additionalDetails`":`"x`"}');"
    $shred6 = & $installer @conn -Database $freshDb -LicenseDatabase $freshLic -LicenseEventsShredded 6>&1 | Out-String
    $s = & $licState
    Assert-That ($shred6 -like '*recorded as all shredded by an earlier run that finished (-LicenseEventsShredded)*' -and $shred6 -like '*every one of 5 record(s) is shredded already*' -and $shred6 -notlike '*shredding*' -and [int] $s.Kept -eq 5 -and [int] $s.Complete -eq 1) `
        "-LicenseEventsShredded says that the shred did finish: the mark is written and no record is read again ($($s.Kept) left as they were)"
    Invoke-SqlText -Catalog $freshLic -Sql "$asOld $noMark $unassemble"
    $shred7 = & $installer @conn -Database $freshDb -LicenseDatabase $freshLic -LicenseEventsShredded 6>&1 | Out-String
    $s = & $licState
    Assert-That ($shred7 -like '*shredding 5 record(s) (1 chunk group(s) were never reassembled*-LicenseEventsShredded is not taken*' -and [int] $s.ChunkProps -eq 2 -and [int] $s.Complete -eq 1 -and "$($s.Sku)" -eq $bothChanges) `
        'but it is not taken where the tables show a shred that stopped: a chunk group never reassembled has every record shredded again'

    # A record that arrived since, as a load that was merged and not shredded leaves it: the shred starts at that
    # record's time (the second before it: the records keep fractions of a second), not at the first record.
    Invoke-SqlText -Catalog $freshLic -Sql $asOld
    Invoke-SqlText -Catalog $freshLic -Parameters @{ '@json' = '{"CreationTime":"2026-09-23T10:00:00","Id":"fl-06","Operation":"Change user license.","ResultStatus":"Success","UserId":"admin@contoso.com","ObjectId":"fresh6@contoso.com","InterSystemsId":"corr-f6","ModifiedProperties":[{"Name":"AssignedLicense","OldValue":"[]","NewValue":"[\"[SkuName=Microsoft_365_Copilot, AccountId=t, SkuId=x, DisabledPlans=[]]\"]"}],"Target":[]}' } -Sql @"
INSERT INTO audit.CopilotAudit (RecordId, CreatedDateTime, RecordType, Operation, UserId, UserPrincipalName, AuditDataGz, WindowId)
VALUES ('fl-06', '2026-09-23T10:00:00.7', 'azureActiveDirectory', 'Change user license.', 'admin@contoso.com', 'admin@contoso.com', COMPRESS(CAST(@json AS NVARCHAR(MAX))), 0);
"@
    $shred8 = & $installer @conn -Database $freshDb -LicenseDatabase $freshLic 6>&1 | Out-String
    $s = & $licState
    Assert-That ($shred8 -like '*shredding the records from 2026-09-23 09:59:59Z on (1 not shredded yet)*' -and $shred8 -notlike '*shredding 6 record(s)*' -and "$($s.Again)" -eq 'fl-06' -and [int] $s.Events -eq 5) `
        "a record that arrived since is shredded from its own time on, and the records before it are not read again (shredded: $($s.Again))"
    Assert-That ("$($s.Sku)" -eq "$bothChanges; fresh6@contoso.com Microsoft_365_Copilot Added" -and [int] $s.InProgress -eq 0 -and [int] $s.Kept -eq 5) 'and its SKU change is kept beside the others'

    # Another record arrives, and a shred that was shredding from 23 September was stopped: the installer asks for
    # the new record's time on, and the procedure goes back to where the stopped one began.
    Invoke-SqlText -Catalog $freshLic -Sql "$asOld INSERT INTO audit.PipelineSetting (Name, Value) VALUES (N'LicenseShredInProgress', N'2026-09-23T00:00:00');"
    Invoke-SqlText -Catalog $freshLic -Parameters @{ '@json' = '{"CreationTime":"2026-09-24T10:00:00","Id":"fl-07","Operation":"Update user.","ResultStatus":"Success","UserId":"sync@contoso.com","ObjectId":"fresh7@contoso.com","InterSystemsId":"corr-f7","ModifiedProperties":[{"Name":"LastDirSyncTime","OldValue":"a","NewValue":"b"}],"Target":[]}' } -Sql @"
INSERT INTO audit.CopilotAudit (RecordId, CreatedDateTime, RecordType, Operation, UserId, UserPrincipalName, AuditDataGz, WindowId)
VALUES ('fl-07', '2026-09-24T10:00:00', 'azureActiveDirectory', 'Update user.', 'admin@contoso.com', 'admin@contoso.com', COMPRESS(CAST(@json AS NVARCHAR(MAX))), 0);
"@
    $shred9 = & $installer @conn -Database $freshDb -LicenseDatabase $freshLic 6>&1 | Out-String
    $s = & $licState
    Assert-That ($shred9 -like '*shredding the records from 2026-09-24 09:59:59Z on (1 not shredded yet)*' -and "$($s.Again)" -eq 'fl-06,fl-07' -and [int] $s.InProgress -eq 0) `
        "a record not shredded yet beside a stopped shred: both are shredded, from where the stopped one began ($($s.Again))"

    # A license event changed by hand, after the SKU changes were last worked out (two seconds after: both times
    # are kept to the second): no record is shredded, and the installer works its change out. fl-06's lists no
    # longer differ, so its SKU change goes.
    Invoke-SqlText -Catalog $freshLic -Sql "$asOld UPDATE audit.LicenseEventProperty SET NewValue = OldValue WHERE RecordId = 'fl-06'; WAITFOR DELAY '00:00:02'; UPDATE audit.LicenseEvent SET ShreddedUtc = SYSUTCDATETIME() WHERE RecordId = 'fl-06';"
    $shred10 = & $installer @conn -Database $freshDb -LicenseDatabase $freshLic 6>&1 | Out-String
    $s = & $licState
    Assert-That ($shred10 -like '*every one of 7 record(s) is shredded already*' -and $shred10 -like '*license SKU changes: working out those of the events shredded since they were last worked out*' -and "$($s.Sku)" -eq $bothChanges) `
        "an event changed by hand has its SKU changes worked out by the installer, with no record shredded ($($s.Sku))"

    # The health check on the shred's mark: none, one of a shred that was stopped today, one a day old, one of a
    # shred running now (a session holding the shred's lock stands in for it).
    if ($script:ITIntegrated) {
        $health = { (& (Join-Path $root 'tools' 'Test-CopilotDataHealth.ps1') -SqlServer $script:ITServer -Database $freshDb -LicenseDatabase $freshLic -TrustServerCertificate 6>&1 | Out-String) }
        $h0 = & $health
        Invoke-SqlText -Catalog $freshLic -Sql "INSERT INTO audit.PipelineSetting (Name, Value) VALUES (N'LicenseShredInProgress', N'');"
        $h1 = & $health
        Invoke-SqlText -Catalog $freshLic -Sql "UPDATE audit.PipelineSetting SET UpdatedUtc = DATEADD(HOUR, -30, SYSUTCDATETIME()) WHERE Name = N'LicenseShredInProgress';"
        $h2 = & $health
        $holder = [System.Data.SqlClient.SqlConnection]::new((New-ConnString $freshLic) + ';Pooling=false')
        try {
            $holder.Open()
            $holdCmd = $holder.CreateCommand()
            $holdCmd.CommandText = "DECLARE @r INT; EXEC @r = sp_getapplock @Resource = N'audit.usp_ShredLicenseEvents', @LockMode = 'Exclusive', @LockOwner = 'Session', @LockTimeout = 0; SELECT @r;"
            [void] $holdCmd.ExecuteScalar()
            $h3 = & $health
        }
        finally { $holder.Dispose() }
        Invoke-SqlText -Catalog $freshLic -Sql "DELETE FROM audit.PipelineSetting WHERE Name = N'LicenseShredInProgress';"
        Assert-That ($h0 -like '*OK*The SKU changes sessions and bi read are worked out for every event shredded*' -and $h0 -notlike '*shred of the license events*') 'the health check: with no shred under way, the kept SKU changes are reported as current'
        Assert-That ($h1 -like '*was stopped part way: events it had begun can lack their properties until the next license load*' -and $h1 -notlike '*ACTION  A shred*') 'a shred stopped today is said, and left to the next license load'
        Assert-That ($h2 -like '*ACTION  A shred of the license events that began*was stopped part way, and none has finished since*') 'one that no shred has finished for a day asks for action'
        Assert-That ($h3 -like '*A shred of the license events is running now.*' -and $h3 -notlike '*ACTION  A shred*') 'and one that is running, which holds the shred lock, is said to be running'
    }
    else { Write-Host '  SKIP  Test-CopilotDataHealth.ps1 connects with Windows authentication only' -ForegroundColor Yellow }
}
finally {
    foreach ($db in $freshDb, $freshLic) {
        try { Invoke-SqlText -Catalog master -Sql "IF DB_ID(N'$db') IS NOT NULL BEGIN ALTER DATABASE [$db] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [$db]; END;" } catch { Write-Host "  (could not drop $db)" }
    }
}

# 2.4.12: databases a DBA created with a collation other than the server's, which the installer takes as they are. The
# temp tables the flatten, the license shred and the bi refresh compare with their own tables took tempdb's collation,
# and each of them failed at once with a collation conflict. One record, flattened and built through to the dashboard.
$collDb = "$($script:ITDatabase)_Collated"; $collLic = "$($script:ITDatabase)_CollatedLic"
$serverCollation = [string] (Invoke-SqlText -Catalog master -As Scalar -Sql "SELECT CONVERT(NVARCHAR(128), SERVERPROPERTY('Collation'));")
$otherCollation = if ($serverCollation -eq 'Latin1_General_100_CI_AS') { 'SQL_Latin1_General_CP1_CI_AS' } else { 'Latin1_General_100_CI_AS' }
try {
    foreach ($db in $collDb, $collLic) {
        Invoke-SqlText -Catalog master -Sql "IF DB_ID(N'$db') IS NOT NULL BEGIN ALTER DATABASE [$db] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [$db]; END; CREATE DATABASE [$db] COLLATE $otherCollation; ALTER DATABASE [$db] SET RECOVERY SIMPLE;"
    }
    & $installer @conn -Database $collDb -LicenseDatabase $collLic -Dashboard -ReportingTimeZone 'Eastern Standard Time' 6>$null | Out-Null
    $csvSchema = Get-Content -LiteralPath (Join-Path $root 'sql' 'Create-CopilotCsvImport.sql') -Raw
    foreach ($db in $collDb, $collLic) {
        foreach ($b in [regex]::Split($csvSchema, '(?im)^[ \t]*GO[ \t]*(?:--[^\r\n]*)?\r?$')) { if ($b.Trim()) { Invoke-SqlText -Catalog $db -Sql $b } }
    }
    $collJson = '{"CreationTime":"2026-09-15T15:00:00Z","Id":"q-01","Operation":"CopilotInteraction","UserId":"q@contoso.com","AppIdentity":"Copilot.Studio.Declarative.T_0a1b2c3d","AgentId":"CopilotStudio.Declarative.T_0a1b2c3d.1","AgentName":"Helper","CopilotEventData":{"AppHost":"Teams","AccessedResources":[{"Name":"a.docx","SiteUrl":"https://contoso.sharepoint.com/sites/fin/a.docx","Type":"File","Action":"Read","SensitivityLabelId":"11111111-0000-0000-0000-000000000001"}],"Contexts":[{"Id":"https://contoso.sharepoint.com/sites/fin/a.docx","Type":"docx"}]}}'
    Invoke-SqlText -Catalog $collDb -Parameters @{ '@j' = $collJson } -Sql @"
INSERT INTO audit.CopilotAudit (RecordId, CreatedDateTime, RecordType, Operation, UserId, UserPrincipalName, AuditDataGz, WindowId)
VALUES ('q-01', '2026-09-15T15:00:00', 'copilotInteraction', 'CopilotInteraction', N'q@contoso.com', N'q@contoso.com', COMPRESS(CAST(@j AS NVARCHAR(MAX))), 0);
"@
    $collSteps = [ordered]@{
        flatten = { Invoke-SqlText -Catalog $collDb -Sql 'EXEC audit.usp_FlattenCopilotAudit;' }
        'license shred' = { Invoke-SqlText -Catalog $collLic -Sql 'EXEC audit.usp_ShredLicenseEvents;' }
        bi = { Invoke-SqlText -Catalog $collDb -Sql "EXEC bi.usp_RefreshBi @LicenseEventDatabase = N'$collLic';" }
        dashboard = { Invoke-SqlText -Catalog $collDb -Sql 'EXEC dash.usp_RefreshDashboard;' }
    }
    $collFailed = @(foreach ($k in $collSteps.Keys) { try { [void] (& $collSteps[$k]) } catch { "${k}: $($_.Exception.GetBaseException().Message)" } })
    $collBuilt = (Invoke-SqlText -Catalog $collDb -As Table -Sql @"
SELECT (SELECT COUNT(*) FROM bi.FactInteraction) AS Facts, (SELECT COUNT(*) FROM bi.FactAccessedResource) AS Resources,
       (SELECT COUNT(*) FROM audit.AgentCatalog WHERE AgentName = N'Helper') AS Agents, (SELECT COUNT(*) FROM dash.ResourceDay) AS ResourceDays;
"@).Rows[0]
    Assert-That ($collFailed.Count -eq 0 -and [int] $collBuilt.Facts -eq 1 -and [int] $collBuilt.Resources -eq 1 -and [int] $collBuilt.Agents -eq 1 -and [int] $collBuilt.ResourceDays -gt 0) `
        "2.4.12: databases in $otherCollation on a $serverCollation server flatten, shred license events and build bi and the dashboard ($($collFailed -join '; '))"
}
finally {
    foreach ($db in $collDb, $collLic) {
        try { Invoke-SqlText -Catalog master -Sql "IF DB_ID(N'$db') IS NOT NULL BEGIN ALTER DATABASE [$db] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [$db]; END;" } catch { Write-Host "  (could not drop $db)" }
    }
}

# Upgrading from 2.1.0: flattened rows without the 2.2.0 fields are flattened again, and bi rebuilt.
# A 2.1.0 database: flattened rows, but no record shapes (only the 2.2.0 flatten counts them).
Invoke-SqlText -Sql 'TRUNCATE TABLE audit.RawLogRecordShape;'
$withJson = [long] (Invoke-SqlText -As Scalar -Sql 'SELECT COUNT(*) FROM audit.CopilotAudit WHERE AuditDataGz IS NOT NULL;')
$up = & $installer @conn -Database $script:ITDatabase -SkipLicenseDatabase -LicenseEventDatabases $licDb -Dashboard 6>&1 | Out-String
$shaped = [long] (Invoke-SqlText -As Scalar -Sql 'SELECT ISNULL(SUM(Records), 0) FROM audit.RawLogRecordShape;')
Assert-That ($up -like '*gained columns (2.2.0)*' -and $shaped -eq $withJson) "an upgrade from 2.1.0 is detected and every record with its JSON flattened again ($shaped of $withJson)"
$once = & $installer @conn -Database $script:ITDatabase -SkipLicenseDatabase -LicenseEventDatabases $licDb -Dashboard 6>&1 | Out-String
Assert-That ($once -notlike '*flattening*') 'and only once: the next run finds nothing to rebuild'
Assert-That ($up -like '*bi, in full*' -and $up -like '*Refresh the Power BI model in full once*') 'and bi rebuilt in full, with the model refresh left to do'
$moved = try { & $installer @conn -Database $script:ITDatabase -SkipLicenseDatabase -ReportingTimeZone 'Pacific Standard Time' 6>$null | Out-Null; '' } catch { $_.Exception.Message }
$still = [string] (Invoke-SqlText -As Scalar -Sql 'SELECT audit.ReportingTimeZone();')
Assert-That ($moved -like '*already holds records cut in Eastern Standard Time*' -and $still -eq 'Eastern Standard Time') 'the time zone cannot be changed once records are loaded'

# Upgrading from 2.4.10: the records of agents were flattened without their id and friendly name. The installer reads
# them once, from those records' JSON, and names the agents in bi; the next run has nothing to read.
# The state 2.4.10 leaves: two records of a declarative agent with their JSON, flattened with AppIdentity alone, and
# the agent in bi under a name read from its id. (c-01 and c-02 stand in for them, and are put back after.)
$declId = 'Copilot.Studio.Declarative.T_0a1b2c3d-1111-2222-3333-444455556666'
Invoke-SqlText -Sql @"
INSERT INTO audit.CopilotAudit (RecordId, CreatedDateTime, RecordType, Operation, UserId, UserPrincipalName, AuditDataGz, WindowId) VALUES
    ('c-01', '2026-09-15T15:00:00', 'copilotInteraction', 'CopilotInteraction', N'c@contoso.com', N'c@contoso.com',
     COMPRESS(N'{"CreationTime":"2026-09-15T15:00:00Z","Id":"c-01","Operation":"CopilotInteraction","UserId":"c@contoso.com","AppIdentity":"$declId","AgentId":"CopilotStudio.Declarative.T_0a1b2c3d-1111-2222-3333-444455556666.9f8e7d6c","AgentName":"Expense helper","CopilotEventData":{"AppHost":"Teams"}}'), 0),
    ('c-02', '2026-09-17T15:00:00', 'copilotInteraction', 'CopilotInteraction', N'c@contoso.com', N'c@contoso.com',
     COMPRESS(N'{"CreationTime":"2026-09-17T15:00:00Z","Id":"c-02","Operation":"CopilotInteraction","UserId":"c@contoso.com","AppIdentity":"$declId","AgentId":"CopilotStudio.Declarative.T_0a1b2c3d-1111-2222-3333-444455556666.9f8e7d6c","AgentName":"Expense Policy Helper","CopilotEventData":{"AppHost":"Teams"}}'), 0);
UPDATE audit.RawLogFlattened SET AppIdentity = N'$declId', AgentId = NULL, AgentName = NULL WHERE RecordId IN (N'c-01', N'c-02');
INSERT INTO bi.DimAgent (AgentId, AgentSource) VALUES (N'$declId', N'Copilot Studio');
DELETE FROM audit.PipelineSetting WHERE Name = N'AgentNamesReadUtc';
EXEC bi.usp_NameAgents;
"@
$declSql = "SELECT CONCAT(AgentName, '/', CAST(NamedInAudit AS INT)) FROM bi.DimAgent WHERE AgentId = N'$declId';"
$unnamed = [string] (Invoke-SqlText -As Scalar -Sql $declSql)
$others0 = [string] (Invoke-SqlText -As Scalar -Sql "SELECT CONCAT(COUNT(*), ':', COUNT(AgentName), ':', COUNT(AgentId)) FROM audit.RawLogFlattened WHERE RecordId NOT IN (N'c-01', N'c-02');")
$names = & $installer @conn -Database $script:ITDatabase -SkipLicenseDatabase -LicenseEventDatabases $licDb -Dashboard 6>&1 | Out-String
$named = [string] (Invoke-SqlText -As Scalar -Sql "SELECT STRING_AGG(CONCAT(RecordId, '=', AgentName, '/', RIGHT(AgentId, 8)), ';') WITHIN GROUP (ORDER BY RecordId) FROM audit.RawLogFlattened WHERE RecordId IN (N'c-01', N'c-02');")
$renamed = [string] (Invoke-SqlText -As Scalar -Sql $declSql)
$others1 = [string] (Invoke-SqlText -As Scalar -Sql "SELECT CONCAT(COUNT(*), ':', COUNT(AgentName), ':', COUNT(AgentId)) FROM audit.RawLogFlattened WHERE RecordId NOT IN (N'c-01', N'c-02');")
Assert-That ($unnamed -eq 'Declarative.T_0a1b2c3d-1111-2222-3333-444455556666/0') "before the upgrade a declarative agent is named from its id, whole, not by its last twelve characters ($unnamed)"
Assert-That ($names -like '*agent names: reading*' -and $names -notlike '*flattening*' -and $named -eq 'c-01=Expense helper/9f8e7d6c;c-02=Expense Policy Helper/9f8e7d6c' -and $others1 -eq $others0) `
    "an upgrade from 2.4.10 reads the agents' ids and friendly names once, from their records' JSON, without flattening again ($named)"
Assert-That ($renamed -eq 'Expense Policy Helper/1') "and bi shows the name the agent's latest record gave it ($renamed)"
Assert-That ($names -like '*Refresh the Power BI model in full once (docs/DEPLOYMENT.md, step 16): agents carry the friendly names*') 'with the model refresh left to do'
$namesOnce = & $installer @conn -Database $script:ITDatabase -SkipLicenseDatabase -LicenseEventDatabases $licDb -Dashboard 6>&1 | Out-String
Assert-That ($namesOnce -notlike '*agent names: reading*') 'and only once'
Invoke-SqlText -Sql @"
DELETE FROM audit.CopilotAudit WHERE RecordId IN ('c-01', 'c-02');
UPDATE audit.RawLogFlattened SET AppIdentity = NULL, AgentId = NULL, AgentName = NULL WHERE RecordId IN (N'c-01', N'c-02');
DELETE FROM audit.AgentCatalog WHERE AppIdentity = N'$declId';
DELETE FROM bi.DimAgent WHERE AgentId = N'$declId';
"@

# Upgrading from 2.4.11 (2.4.12): another app's records that carry an agent's id, Copilot Chat running a declarative
# agent here, are keyed on that id, which 2.4.11 read only from its upgrade night on. The installer reads them once,
# from the apps the newest records show carrying one, and rebuilds bi so their interactions move to the agent.
$bizApp = 'Copilot.MicrosoftCopilot.BizChat'; $bizId = 'CopilotStudio.Declarative.T_9f8e7d6c-aaaa-bbbb-cccc-0123456789ab.5e6f'
Invoke-SqlText -Sql @"
INSERT INTO audit.CopilotAudit (RecordId, CreatedDateTime, RecordType, Operation, UserId, UserPrincipalName, AuditDataGz, WindowId) VALUES
    ('c-01', '2026-09-15T15:00:00', 'copilotInteraction', 'CopilotInteraction', N'c@contoso.com', N'c@contoso.com',
     COMPRESS(N'{"CreationTime":"2026-09-15T15:00:00Z","Id":"c-01","Operation":"CopilotInteraction","UserId":"c@contoso.com","AppIdentity":"$bizApp","AgentId":"$bizId","AgentName":"Travel helper","CopilotEventData":{"AppHost":"BizChat"}}'), 0),
    ('c-02', '2026-09-17T15:00:00', 'copilotInteraction', 'CopilotInteraction', N'c@contoso.com', N'c@contoso.com',
     COMPRESS(N'{"CreationTime":"2026-09-17T15:00:00Z","Id":"c-02","Operation":"CopilotInteraction","UserId":"c@contoso.com","AppIdentity":"$bizApp","AgentId":"$bizId","AgentName":"Travel helper","CopilotEventData":{"AppHost":"BizChat"}}'), 0);
UPDATE audit.RawLogFlattened SET AppIdentity = N'$bizApp', AgentId = NULL, AgentName = NULL WHERE RecordId IN (N'c-01', N'c-02');
DELETE FROM audit.PipelineSetting WHERE Name = N'AgentIdsReadUtc';
"@
$ids = & $installer @conn -Database $script:ITDatabase -SkipLicenseDatabase -LicenseEventDatabases $licDb -Dashboard 6>&1 | Out-String
$keyed = (Invoke-SqlText -As Table -Sql @"
SELECT (SELECT COUNT(*) FROM audit.RawLogFlattened WHERE RecordId IN (N'c-01', N'c-02') AND AgentId = N'$bizId') AS Read_,
       (SELECT COUNT(*) FROM bi.FactInteraction AS f JOIN bi.DimAgent AS ag ON ag.AgentKey = f.AgentKey
        WHERE f.RecordId IN (N'c-01', N'c-02') AND ag.AgentId = N'$bizId' AND ag.AgentSource = N'Agent ID') AS Keyed,
       (SELECT CONCAT(AgentName, '/', CAST(NamedInAudit AS INT)) FROM bi.DimAgent WHERE AgentId = N'$bizId') AS Agent;
"@).Rows[0]
Assert-That ($ids -like '*agent ids: reading*' -and $ids -like '*2 interactions gain the agent*' -and $ids -notlike '*flattening*' -and [int] $keyed.Read_ -eq 2 -and [int] $keyed.Keyed -eq 2) `
    "2.4.12: an upgrade reads the agent ids of other apps' records once, and bi moves their interactions to the agent ($($keyed.Read_) read, $($keyed.Keyed) keyed)"
Assert-That ("$($keyed.Agent)" -eq 'Travel helper/1' -and $ids -like '*Refresh the Power BI model in full once*interactions of every day moved to the agents*') "named as the log names it ($($keyed.Agent)), with the model refresh left to do"
$idsOnce = & $installer @conn -Database $script:ITDatabase -SkipLicenseDatabase -LicenseEventDatabases $licDb -Dashboard 6>&1 | Out-String
Assert-That ($idsOnce -notlike '*agent ids: reading*' -and $idsOnce -notlike '*bi, in full*') 'and only once'
# 2.4.12: records flattened after AgentNamesReadUtc (by 2.4.11 or later, which read the id where it is) are not read
# again: without one they have none. Before, every record of such an app without an id was read, years of Copilot Chat.
Invoke-SqlText -Sql @"
UPDATE audit.RawLogFlattened SET AgentId = NULL, AgentName = NULL WHERE RecordId IN (N'c-01', N'c-02');
UPDATE audit.RawLogFlattened SET FlattenedUtc = DATEADD(MINUTE, 1, (SELECT TRY_CONVERT(DATETIME2(0), Value, 126) FROM audit.PipelineSetting WHERE Name = N'AgentNamesReadUtc'))
WHERE RecordId = N'c-02';
DELETE FROM audit.PipelineSetting WHERE Name = N'AgentIdsReadUtc';
"@
$skipGained = [long] (Invoke-SqlText -As Scalar -Sql 'EXEC audit.usp_BackfillAgentNames @Others = 1;')
$skipRead = [string] (Invoke-SqlText -As Scalar -Sql "SELECT STRING_AGG(CONCAT(RecordId, '=', CASE WHEN AgentId IS NULL THEN 'none' ELSE 'read' END), ';') WITHIN GROUP (ORDER BY RecordId) FROM audit.RawLogFlattened WHERE RecordId IN (N'c-01', N'c-02');")
Assert-That ($skipGained -eq 1 -and $skipRead -eq 'c-01=read;c-02=none') "2.4.12: records flattened since 2.4.11 are not read again for an agent id ($skipRead)"
Invoke-SqlText -Sql @"
DELETE FROM audit.CopilotAudit WHERE RecordId IN ('c-01', 'c-02');
UPDATE audit.RawLogFlattened SET AppIdentity = NULL, AgentId = NULL, AgentName = NULL WHERE RecordId IN (N'c-01', N'c-02');
DELETE FROM audit.AgentCatalog WHERE AgentId = N'$bizId';
"@
Invoke-SqlText -Sql 'EXEC bi.usp_RefreshBi @SinceUtc = @s, @LicenseEventDatabase = @l; EXEC dash.usp_RefreshDashboard @SinceUtc = @s;' -Parameters @{ '@s' = [datetime] '2026-09-14'; '@l' = $licDb } -TimeoutSeconds 1800
Invoke-SqlText -Sql "DELETE FROM bi.DimAgent WHERE AgentId = N'$bizId' AND NOT EXISTS (SELECT 1 FROM bi.FactInteraction AS f WHERE f.AgentKey = bi.DimAgent.AgentKey);"

# 2.4.12: built is a flatten of every record that finished. A database from before has no mark: the installer counts
# once that every record is flattened and gives it one. With a record never flattened (a first flatten stopped part
# way, or only a nightly's days flattened), it flattens and builds everything, where any flattened row counted as built.
Invoke-SqlText -Sql "DELETE FROM audit.PipelineSetting WHERE Name = N'FullFlattenUtc';"
$seeded = & $installer @conn -Database $script:ITDatabase -SkipLicenseDatabase -LicenseEventDatabases $licDb -Dashboard 6>&1 | Out-String
$markNow = [int] (Invoke-SqlText -As Scalar -Sql "SELECT COUNT(*) FROM audit.PipelineSetting WHERE Name = N'FullFlattenUtc';")
Assert-That ($seeded -like '*checking that every record has been flattened*' -and $seeded -notlike '*flattening*' -and $markNow -eq 1) '2.4.12: a database flattened in full before the mark gets one, without flattening again'
$gone = [string] (Invoke-SqlText -As Scalar -Sql @"
DECLARE @id NVARCHAR(450) = (SELECT TOP (1) RecordId FROM audit.RawLogFlattened AS f WHERE EXISTS (SELECT 1 FROM audit.CopilotAudit AS a WHERE a.RecordId = f.RecordId) ORDER BY RecordId DESC);
DELETE FROM audit.RawLogFlattened WHERE RecordId = @id;
DELETE FROM audit.PipelineSetting WHERE Name = N'FullFlattenUtc';
SELECT @id;
"@)
$rebuilt = & $installer @conn -Database $script:ITDatabase -SkipLicenseDatabase -LicenseEventDatabases $licDb -Dashboard 6>&1 | Out-String
$back = [int] (Invoke-SqlText -As Scalar -Sql "SELECT COUNT(*) FROM audit.RawLogFlattened WHERE RecordId = @id;" -Parameters @{ '@id' = $gone })
$markAgain = [int] (Invoke-SqlText -As Scalar -Sql "SELECT COUNT(*) FROM audit.PipelineSetting WHERE Name = N'FullFlattenUtc';")
Assert-That ($rebuilt -like '*1 were never flattened*' -and $rebuilt -like '*sessions, in full*' -and $rebuilt -like '*bi, in full*' -and $back -eq 1 -and $markAgain -eq 1) `
    "and one with a record never flattened ($gone) is flattened and built in full, and marked"

$status = & $maint @conn -Database $script:ITDatabase -Task Status | Out-String
$topNow = [string] (Invoke-SqlText -As Scalar -Sql "SELECT COALESCE(MAX(CASE WHEN Name = N'OrgTopUpn' THEN NULLIF(Value, N'') END), MAX(CASE WHEN Name = N'OrgTopUpnDetected' THEN Value END)) FROM dash.Setting;")
Assert-That ($status -like '*Flattened*' -and $status -like "*Top of the organization*$topNow*") "Status reports what is loaded, built, and the top of the organization ($topNow)"
& $maint @conn -Database $script:ITDatabase -Task SetAppCategory -App 'Teams' -Category 'Copilot Chat' | Out-Null
& $maint @conn -Database $script:ITDatabase -Task GrantRole -Principal 'CONTOSO\Insights-Admins' -PrincipalType Group -Role Admin -Note 'test' | Out-Null
& $maint @conn -Database $script:ITDatabase -Task SetDashboardSetting -Name LicenseMonthlyCost -Value '31.5' | Out-Null
& $maint @conn -Database $script:ITDatabase -Task SetDashboardSetting -Name PaygInteractionCost -Value '0.15' | Out-Null
& $maint @conn -Database $script:ITDatabase -Task SetOrgTop -Upn 'boss@contoso.com' | Out-Null
$mt = (Invoke-SqlText -As Table -Sql @"
SELECT (SELECT AppCategory FROM audit.SessionAppCategory WHERE App = N'Teams') AS Teams,
       (SELECT PrincipalType + N'/' + Role FROM dash.RoleGrant WHERE Principal = N'CONTOSO\Insights-Admins') AS Grant_,
       (SELECT Value FROM dash.Setting WHERE Name = N'LicenseMonthlyCost') AS Cost,
       (SELECT Value FROM dash.Setting WHERE Name = N'PaygInteractionCost') AS Payg,
       (SELECT CAST(Level AS INT) FROM dash.Person WHERE Upn = N'a@contoso.com') AS ALevel,
       (SELECT COUNT(*) FROM dash.AuditLog WHERE Page = N'Invoke-CopilotMaintenance') AS Logged;
"@).Rows[0]
Assert-That ("$($mt.Teams)" -eq 'Copilot Chat' -and "$($mt.Grant_)" -eq 'Group/Admin' -and "$($mt.Cost)" -eq '31.5' -and "$($mt.Payg)" -eq '0.15') 'maintenance tasks set an app category, grant a role and change settings (the pay-as-you-go cost too), without SQL'
Assert-That ([int] $mt.ALevel -eq 2 -and [int] $mt.Logged -ge 3) "a new top of the organization takes effect at once (user A now level $($mt.ALevel)), and every change is audit-logged"
$refused = try { & $maint @conn -Database $script:ITDatabase -Task SetOrgTop -Upn 'nobody@contoso.com'; '' } catch { $_.Exception.Message }
Assert-That ($refused -like '*not in the directory*') 'a top of the organization not in the directory is refused'
# 2.4.12: with no -LicenseEventDatabases, maintenance reads the license databases the database's last build read, not
# CopilotLicenseEvents, which is the first tenant's.
$rebi = & $maint @conn -Database $script:ITDatabase -Task RebuildBi 6>&1 | Out-String
$biLic = [string] (Invoke-SqlText -As Scalar -Sql 'SELECT TOP (1) LicenseEventDatabase FROM bi.RefreshRun ORDER BY RunId DESC;')
Assert-That ($rebi -like "*License history from $licDb, as $($script:ITDatabase)'s last build*" -and $biLic -eq $licDb) "2.4.12: maintenance reads the license history this database was built with ($biLic)"

# ---------------------------------------------------------------------------
# 16. Operations (2.3.0): the operations schema over section 14's dashboard,
# its secrets, privacy mode, retention, the value catalog's cap, the
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

# 2.4.12: restored to another instance, the master key opens with neither that instance's service master key nor the
# password nobody kept. Dropping its service-master-key copy here is the same state. Run again, the script makes the keys
# again and removes the secrets it can no longer read, so entering them again in Admin works.
Invoke-SqlText -Sql "EXEC dash.usp_SetSecret @Name = 'AlertWebhookUrl', @Value = N'https://contoso.webhook.office.com/it', @By = N'test'; ALTER MASTER KEY DROP ENCRYPTION BY SERVICE MASTER KEY;"
$moved = try { Invoke-SqlText -Sql "EXEC dash.usp_SetSecret @Name = 'SmtpPassword', @Value = N'x', @By = N'test';"; 'stored' } catch { $_.Exception.Message }
Assert-That ("$moved" -like '*master key*') "with a master key this instance cannot open, a secret cannot be stored ($moved)"
Invoke-SqlScript -Sql (Get-Content -LiteralPath (Join-Path $root 'sql' 'Create-CopilotOperationsSchema.sql') -Raw)
$again = (Invoke-SqlText -As Table -Sql @"
DECLARE @v NVARCHAR(2000);
EXEC dash.usp_SetSecret @Name = 'SmtpPassword', @Value = N'entered again', @By = N'test';
EXECUTE AS USER = 'it_app';
EXEC dash.usp_GetSecret @Name = 'SmtpPassword', @Value = @v OUTPUT;
REVERT;
SELECT @v AS Value, (SELECT COUNT(*) FROM dash.Secret WHERE Name = 'AlertWebhookUrl') AS Unreadable,
       (SELECT is_master_key_encrypted_by_server FROM sys.databases WHERE name = DB_NAME()) AS ByServer;
"@).Rows[0]
Assert-That ("$($again.Value)" -eq 'entered again' -and [int] $again.Unreadable -eq 0 -and [bool] $again.ByServer) 'run again, the script makes the keys again and removes the secrets it can no longer read, and a secret entered again is stored and read'
Invoke-SqlText -Sql "EXEC dash.usp_SetSecret @Name = 'SmtpPassword', @Value = NULL, @By = N'test';"

# Privacy mode: bi.Users pseudonymized, the same hash for the same person, counts unchanged.
$plain = (Invoke-SqlText -As Table -Sql "SELECT COUNT(*) AS N, COUNT(DISTINCT UserPrincipalName) AS D FROM bi.Users;").Rows[0]
Invoke-SqlText -Sql "EXECUTE AS USER = 'it_app'; EXEC dash.usp_SetPrivacyMode @Mode = N'Pseudonymize', @By = N'test'; REVERT;"
$hidden = (Invoke-SqlText -As Table -Sql @"
SELECT COUNT(*) AS N, COUNT(DISTINCT UserPrincipalName) AS D,
       SUM(CASE WHEN UserPrincipalName LIKE N'%@%' OR DisplayName LIKE N'User %' OR ManagerUpn LIKE N'%@%' THEN 1 ELSE 0 END) AS Named,
       SUM(CASE WHEN UserPrincipalName LIKE N'P-%' THEN 1 ELSE 0 END) AS Hashed
FROM bi.Users;
"@).Rows[0]
Assert-That ([int] $hidden.Named -eq 0 -and [int] $hidden.Hashed -gt 0) "privacy mode names nobody in bi.Users ($($hidden.Hashed) pseudonyms)"
Assert-That ([int] $hidden.N -eq [int] $plain.N -and [int] $hidden.D -eq [int] $plain.D) 'with the same rows and one distinct pseudonym per person'
$leak = (Invoke-SqlText -As Table -Sql @"
SELECT (SELECT COUNT(*) FROM bi.OrgSecurity) AS SecurityRows,
       (SELECT COUNT(*) FROM bi.LicenseChanges WHERE ChangedBy IS NOT NULL) AS Admins,
       (SELECT COUNT(*) FROM bi.Sites WHERE IsPersonal = 1 AND Site <> N'OneDrive (personal)') AS OneDrives;
"@).Rows[0]
Assert-That ([int] $leak.SecurityRows -eq 0 -and [int] $leak.Admins -eq 0 -and [int] $leak.OneDrives -eq 0) 'and nothing else in bi names anyone: bi.OrgSecurity is empty, administrators and OneDrive owners are hidden'
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
# 2.4.12: the audit log is kept at least a year whatever the stored value says (0 keeps it). Only the admin form held
# that floor, so 30 set in SQL deleted the log past 30 days.
$keptAudit = [string] (Invoke-SqlText -As Scalar -Sql @"
DECLARE @was NVARCHAR(400) = (SELECT Value FROM dash.Setting WHERE Name = 'AuditLogRetentionDays');
INSERT INTO dash.AuditLog (AtUtc, Viewer, Kind, Page) VALUES (DATEADD(DAY, -100, SYSUTCDATETIME()), N'floor-in', 'View', N'/'), (DATEADD(DAY, -400, SYSUTCDATETIME()), N'floor-out', 'View', N'/');
UPDATE dash.Setting SET Value = N'30' WHERE Name = 'AuditLogRetentionDays';
DECLARE @r TABLE (m NVARCHAR(400)); INSERT INTO @r EXEC dash.usp_ApplyRetention;
UPDATE dash.Setting SET Value = @was WHERE Name = 'AuditLogRetentionDays';
SELECT CONCAT((SELECT COUNT(*) FROM dash.AuditLog WHERE Viewer = N'floor-in'), N'/', (SELECT COUNT(*) FROM dash.AuditLog WHERE Viewer = N'floor-out'));
"@)
Assert-That ($keptAudit -eq '1/0') "2.4.12: a stored audit-log retention under 365 days counts as 365 ($keptAudit kept: 100 days old / 400 days old)"
Invoke-SqlText -Sql "DELETE FROM dash.AuditLog WHERE Viewer = N'floor-in';"

# The value catalog's cap: a column stops collecting at 10,000 distinct values.
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
    [void] (Write-PipelineRecord $setting -Log $log -Parameters @{ '@r' = $id; '@n' = 1; '@s' = 'Copilot licenses from Graph' } -Sql 'INSERT INTO audit.PipelineRunStep (RunId, StepNo, Name) VALUES (@r, @n, @s);')
    [void] (Write-PipelineRecord $setting -Log $log -Parameters @{ '@r' = $id; '@n' = 1; '@c' = 1 } -Sql 'UPDATE audit.PipelineRunStep SET EndedUtc = SYSUTCDATETIME(), ExitCode = @c WHERE RunId = @r AND StepNo = @n;')
    [void] (Write-PipelineRecord $setting -Log $log -Parameters @{ '@r' = $id; '@f' = 'Copilot licenses from Graph'; '@d' = (Get-LogTail $log) } -Sql "UPDATE audit.PipelineRun SET EndedUtc = SYSUTCDATETIME(), Status = 'Failed', FailedStep = @f, Detail = @d WHERE RunId = @r;")
    $run = (Invoke-SqlText -As Table -Sql "SELECT r.Status, r.FailedStep, r.Detail, s.ExitCode FROM audit.PipelineRun AS r JOIN audit.PipelineRunStep AS s ON s.RunId = r.RunId WHERE r.RunId = $id;").Rows[0]
    Assert-That ("$($run.Status)" -eq 'Failed' -and [int] $run.ExitCode -eq 1 -and "$($run.Detail)" -like '*certificate has expired*') 'the pipeline records its run, each step''s exit code, and the failed step''s last lines'
    $bad = @{ SqlServer = 'no-such-server.invalid'; Database = 'x'; ConnectionString = 'Server=no-such-server.invalid;Database=x;Integrated Security=true;Connect Timeout=2' }
    $threw = try { [void] (Write-PipelineRecord $bad -Log $log -Parameters @{} -Sql 'SELECT 1;'); $false } catch { $true }
    Assert-That (-not $threw -and (Get-Content $log -Raw) -like '*not recorded in audit.PipelineRun*') 'and a database it cannot reach costs a line in the log, not the run'
    Remove-Item $log -ErrorAction SilentlyContinue

    # Both certificates the run signs in with are recorded for Admin > Health: neither thumbprint is on this machine, so
    # each is recorded as not found; with the workbook moved to a share, its row goes.
    $both = $setting + @{ TenantId = 't'; ClientId = 'audit-app'; CertificateThumbprint = '00 11 22'; AgentWorkbookSiteUrl = 'https://contoso.sharepoint.com/sites/AI'
                          AgentWorkbookClientId = 'workbook-app'; AgentWorkbookCertificateThumbprint = 'AABBCC' }
    Write-PipelineCertificate $both -Log $log
    $certs = Invoke-SqlText -As Table -Sql 'SELECT Purpose, Thumbprint, Found, ClientId, TenantId, Host, ExpiresUtc FROM audit.PipelineCertificate ORDER BY Purpose;'
    Assert-That ($certs.Rows.Count -eq 2 -and "$($certs.Rows[0].Purpose)|$($certs.Rows[1].Purpose)" -eq 'AgentWorkbook|Purview') "the pipeline records both certificates ($($certs.Rows.Count) row(s))"
    Assert-That ("$($certs.Rows[1].Thumbprint)" -eq '001122' -and "$($certs.Rows[1].ClientId)" -eq 'audit-app' -and "$($certs.Rows[0].ClientId)" -eq 'workbook-app' -and "$($certs.Rows[0].TenantId)" -eq 't') `
        'each with its cleaned thumbprint, and the app and tenant it signs in as'
    Assert-That (-not [bool] $certs.Rows[0].Found -and -not [bool] $certs.Rows[1].Found -and $certs.Rows[0].ExpiresUtc -is [DBNull] -and "$($certs.Rows[0].Host)" -eq [Environment]::MachineName) `
        'a thumbprint this machine holds no certificate for is recorded as not found, with the machine that looked'
    Write-PipelineCertificate $both -Log $log
    Assert-That ([int] (Invoke-SqlText -As Scalar -Sql 'SELECT COUNT(*) FROM audit.PipelineCertificate;') -eq 2) 'a second run rewrites the two rows, and adds none'
    $both.Remove('AgentWorkbookSiteUrl'); $both.AgentWorkbookUrl = '\\fileserver\agents.xlsx'
    Write-PipelineCertificate $both -Log $log
    Assert-That ("$(Invoke-SqlText -As Scalar -Sql "SELECT STRING_AGG(Purpose, ',') FROM audit.PipelineCertificate;")" -eq 'Purview') 'a workbook moved to a share no longer has a certificate watched'
    Assert-That (-not (Test-Path $log)) 'and nothing failed on the way: a failure would have left a line in the log'
    Remove-Item $log -ErrorAction SilentlyContinue

    # 2.4.12: a database with no audit.CopilotAudit yet (its history not loaded) is "not loaded". The check named the
    # missing table in the statement that tested for it, and threw instead of saying to load the history. master has none.
    $notLoaded = try { -not (Test-HistoryLoaded @{ SqlServer = $script:ITServer; Database = 'master'; ConnectionString = (New-ConnString 'master') }) } catch { $false }
    Assert-That $notLoaded '2.4.12: a database whose history is not loaded yet is reported as such, not as an error'

    # 2.4.12: one run at a time. The pipeline, the installer and the maintenance rebuilds each hold the database while
    # they work; one that finds it held stops at once and changes nothing. Before, a manual run during the nightly, or
    # the nightly during an upgrade's full bi rebuild, loaded windows twice or counted interactions twice.
    $first = Enter-PipelineRun $setting
    $second = Enter-PipelineRun $setting
    Assert-That ($null -ne $first -and $null -eq $second) '2.4.12: a second pipeline run on a database in use is refused'
    $heldFor = $env:CopilotAuditRunLock
    Remove-Item Env:\CopilotAuditRunLock      # as another process sees it
    $instRefused = try { & $installer @conn -Database $script:ITDatabase -SkipLicenseDatabase -LicenseEventDatabases $licDb -Dashboard 6>$null | Out-Null; '' } catch { $_.Exception.Message }
    $maintRefused = try { & $maint @conn -Database $script:ITDatabase -Task RebuildBi 6>$null | Out-Null; '' } catch { $_.Exception.Message }
    Assert-That ($instRefused -like 'Another run is using*Nothing was changed*' -and $maintRefused -like 'Another run is using*Nothing was changed*') `
        'and so are the installer and a maintenance rebuild, which stop before changing anything'
    # 2.4.12: and a load or a CSV import run by hand, which took no lock: one beside the nightly loaded the same windows
    # twice, and two imports at once emptied each other's stage.
    $emptyDir = Join-Path ([IO.Path]::GetTempPath()) ('lockit-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $emptyDir | Out-Null
    $loadRefused = try { & (Join-Path $root 'Invoke-CopilotAuditBackfill.ps1') -ConnectionString (New-ConnString $script:ITDatabase) -Incremental -LogPath (Join-Path $emptyDir 'load.log') 6>$null | Out-Null; '' } catch { $_.Exception.Message }
    $csvRefused = try { & (Join-Path $root 'tools' 'Import-CopilotAuditCsv.ps1') -Path $emptyDir -ConnectionString (New-ConnString $script:ITDatabase) 6>$null | Out-Null; '' } catch { $_.Exception.Message }
    Assert-That ($loadRefused -like 'Another run is using*Nothing was loaded*' -and $csvRefused -like 'Another run is using*Nothing was imported*') `
        "2.4.12: so are a load and a CSV import run by hand ($loadRefused | $csvRefused)"
    $env:CopilotAuditRunLock = $heldFor
    $csvUnder = try { & (Join-Path $root 'tools' 'Import-CopilotAuditCsv.ps1') -Path $emptyDir -ConnectionString (New-ConnString $script:ITDatabase) 6>&1 | Out-String } catch { $_.Exception.Message }
    Assert-That ($csvUnder -like '*No files matching*') "but one the pipeline starts runs under its lock ($csvUnder)"
    Remove-Item -LiteralPath $emptyDir -Recurse -Force -ErrorAction SilentlyContinue
    $env:CopilotAuditRunLock = $heldFor       # the history's build step, started by the run that holds the lock
    $underRun = & $installer @conn -Database $script:ITDatabase -SkipLicenseDatabase -LicenseEventDatabases $licDb -Dashboard 6>&1 | Out-String
    Assert-That ($underRun -like '*Done.*') "but the installer the pipeline's own history step starts runs under its lock"
    Exit-PipelineRun $first
    $after = Enter-PipelineRun $setting
    Exit-PipelineRun $after
    Assert-That ($null -ne $after -and -not (Test-Path Env:\CopilotAuditRunLock)) 'and once the run ends, the database is free'
}

# Power BI: row-level security from the org chart, and department figures with small groups hidden.
# 2.4.12: against a manager below the top two levels with people under them, and with exact counts. Section 15 had
# moved the top to boss, so user A was level 2 and saw everyone as a whole-organization viewer, and the check
# (C sees at most 1, A at least as many) passed when the view gave such a manager nobody, or everyone.
& $maint @conn -Database $script:ITDatabase -Task SetOrgTop -Upn 'ceo@contoso.com' | Out-Null
$sec = (Invoke-SqlText -As Table -Sql @"
SELECT (SELECT COUNT(*) FROM bi.OrgSecurity WHERE ViewerUpn = N'a@contoso.com') AS AUnder,
       (SELECT COUNT(DISTINCT u.UserPrincipalName) FROM bi.OrgSecurity AS s JOIN bi.DimUser AS u ON u.UserKey = s.UserKey WHERE s.ViewerUpn = N'a@contoso.com') AS ADistinct,
       (SELECT STRING_AGG(u.UserPrincipalName, ',') WITHIN GROUP (ORDER BY u.UserPrincipalName)
        FROM bi.OrgSecurity AS s JOIN bi.DimUser AS u ON u.UserKey = s.UserKey WHERE s.ViewerUpn = N'a@contoso.com') AS ASees,
       (SELECT COUNT(*) FROM dash.OrgClosure AS oc JOIN bi.DimUser AS u ON u.UserPrincipalName = oc.DescendantUpn WHERE oc.AncestorUpn = N'a@contoso.com') AS AClosure,
       (SELECT CAST(Level AS INT) FROM dash.Person WHERE Upn = N'a@contoso.com') AS ALevel,
       (SELECT COUNT(*) FROM bi.OrgSecurity WHERE ViewerUpn = N'c@contoso.com') AS CSelf,
       (SELECT COUNT(*) FROM bi.DimUser) AS Everyone,
       (SELECT COUNT(*) FROM bi.OrgSecurity AS s WHERE s.ViewerUpn = (SELECT TOP (1) Upn FROM dash.Person WHERE Level = 1)) AS TopSees,
       (SELECT COUNT(*) FROM bi.OrgSecurity WHERE ViewerUpn IN (N'a@contoso.com', N'c@contoso.com') AND WholeOrganization = 1) AS PartWhole,
       (SELECT MIN(CAST(WholeOrganization AS INT)) FROM bi.OrgSecurity AS s WHERE s.ViewerUpn = (SELECT TOP (1) Upn FROM dash.Person WHERE Level = 1)) AS TopWhole,
       (SELECT COUNT(*) FROM bi.DepartmentWeek WHERE Hidden = 1 AND ActivePeople IS NOT NULL) AS Leaks;
"@).Rows[0]
Assert-That ([int] $sec.ALevel -ge 3 -and [int] $sec.AClosure -ge 2 -and [int] $sec.AClosure -lt [int] $sec.Everyone) "the fixture has a manager below the top two levels with people under them (user A: level $($sec.ALevel), $($sec.AClosure) in their part, of $($sec.Everyone))"
Assert-That ([int] $sec.AUnder -eq [int] $sec.AClosure -and [int] $sec.ADistinct -eq [int] $sec.AUnder -and "$($sec.ASees)" -eq 'a@contoso.com,c@contoso.com') "bi.OrgSecurity: a manager sees exactly themselves and the people under them, once each, and nobody else ($($sec.ASees))"
Assert-That ([int] $sec.CSelf -eq 1) "a person with nobody under them sees themselves alone ($($sec.CSelf))"
Assert-That ([int] $sec.TopSees -eq [int] $sec.Everyone) "the top of the organization sees everyone ($($sec.TopSees) of $($sec.Everyone))"
Assert-That ([int] $sec.PartWhole -eq 0 -and [int] $sec.TopWhole -eq 1) '2.4.12: WholeOrganization marks the viewers who see everyone, not those who see a part'
Assert-That ([int] $sec.Leaks -eq 0) 'bi.DepartmentWeek hides every figure of a group under the minimum size'
# 2.4.12: a row for every department and every week, used or not, so a row's presence says nothing about anyone's use;
# departments with fewer enabled people than the minimum are combined. Before, a hidden row of a one-person department
# existed only in the weeks that person used Copilot.
$dw = (Invoke-SqlText -As Table -Sql @"
DECLARE @min INT = ISNULL(TRY_CAST((SELECT Value FROM dash.Setting WHERE Name = 'MinGroupSize') AS INT), 5);
SELECT (SELECT COUNT(*) FROM bi.DepartmentWeek) AS Rows_,
       (SELECT COUNT(DISTINCT WeekEnding) FROM bi.DepartmentWeek) AS Weeks,
       (SELECT COUNT(DISTINCT Department) FROM bi.DepartmentWeek) AS Departments,
       (SELECT COUNT(DISTINCT d.WeekEnding) FROM bi.DimDate AS d
        WHERE d.DateKey BETWEEN (SELECT MIN(DateKey) FROM dash.UserDay) AND (SELECT MAX(DateKey) FROM dash.UserDay)) AS DataWeeks,
       (SELECT COUNT(*) FROM (SELECT DISTINCT Department FROM bi.DepartmentWeek) AS v
        WHERE v.Department <> N'(smaller departments)'
          AND (SELECT COUNT(*) FROM bi.DimUser AS u WHERE u.AccountEnabled = 1 AND ISNULL(NULLIF(u.Department, N''), N'(no department)') = v.Department) < @min) AS SmallNamed,
       (SELECT COUNT(*) FROM bi.DepartmentWeek WHERE Hidden = 0 AND ActivePeople < @min) AS ShownSmall,
       (SELECT COUNT(*) FROM bi.DepartmentWeek WHERE Hidden = 1 AND (Interactions IS NOT NULL OR Sessions IS NOT NULL)) AS HiddenFigures;
"@).Rows[0]
Assert-That ([int] $dw.Rows_ -gt 0 -and [int] $dw.Rows_ -eq [int] $dw.Weeks * [int] $dw.Departments -and [int] $dw.Weeks -eq [int] $dw.DataWeeks) `
    "2.4.12: bi.DepartmentWeek has a row for every department and every week of the data ($($dw.Rows_) rows, $($dw.Departments) departments x $($dw.Weeks) of $($dw.DataWeeks) weeks)"
Assert-That ([int] $dw.SmallNamed -eq 0 -and [int] $dw.ShownSmall -eq 0 -and [int] $dw.HiddenFigures -eq 0) `
    "and names no department under the minimum, shows no group under it, and blanks every figure of a hidden row ($($dw.SmallNamed) named, $($dw.ShownSmall) shown, $($dw.HiddenFigures) with figures)"
# 2.4.12: every column the Power BI model imports is in its bi view. Nothing compared them before, so a column taken
# out of a view would have failed only at the customer's refresh.
$modelGaps = foreach ($tm in Get-ChildItem -LiteralPath (Join-Path $root 'powerbi' 'CopilotInsights' 'CopilotInsights.SemanticModel' 'definition' 'tables') -Filter *.tmdl) {
    $tmdl = [System.IO.File]::ReadAllText($tm.FullName)
    $view = [regex]::Match($tmdl, 'Item = "(\w+)"').Groups[1].Value
    $have = @((Invoke-SqlText -As Table -Sql 'SELECT name FROM sys.columns WHERE object_id = OBJECT_ID(N''bi.'' + @v);' -Parameters @{ '@v' = $view }).Rows | ForEach-Object { $_.name })
    if (-not $have.Count) { "bi.$view (missing)"; continue }
    foreach ($c in [regex]::Matches($tmdl, 'sourceColumn: (\w+)') | ForEach-Object { $_.Groups[1].Value }) {
        if ($c -notin $have) { "bi.$view.$c" }
    }
}
Assert-That (@($modelGaps).Count -eq 0) "every column of the Power BI model is in its bi view ($(@($modelGaps) -join ', '))"
# 2.4.0: Organization granted to an AD group reaches its members, once each, through audit.GroupMember.
$grp = (Invoke-SqlText -As Table -Sql @"
INSERT INTO dash.RoleGrant (Principal, PrincipalType, Role, Note, GrantedBy) VALUES (N'CONTOSO\it-execs', 'Group', 'Organization', N'test', N'test');
INSERT INTO audit.GroupMember (GroupName, UserPrincipalName) VALUES (N'CONTOSO\it-execs', N'c@contoso.com'), (N'CONTOSO\it-other', N'd@contoso.com');
SELECT (SELECT COUNT(*) FROM bi.OrgSecurity WHERE ViewerUpn = N'c@contoso.com') AS CSees,
       (SELECT COUNT(DISTINCT UserKey) FROM bi.OrgSecurity WHERE ViewerUpn = N'c@contoso.com') AS CDistinct,
       (SELECT COUNT(*) FROM bi.OrgSecurity WHERE ViewerUpn = N'd@contoso.com') AS DSees,
       (SELECT MIN(CAST(WholeOrganization AS INT)) FROM bi.OrgSecurity WHERE ViewerUpn = N'c@contoso.com') AS CWhole,
       (SELECT COUNT(*) FROM bi.DimUser) AS Everyone;
DELETE FROM audit.GroupMember WHERE GroupName LIKE N'CONTOSO\it-%';
DELETE FROM dash.RoleGrant WHERE Principal = N'CONTOSO\it-execs';
"@).Rows[0]
Assert-That ([int] $grp.CSees -eq [int] $grp.Everyone -and [int] $grp.CDistinct -eq [int] $grp.Everyone -and [int] $grp.CWhole -eq 1) "a member of a group granted Organization sees everyone, once each, as a whole-organization viewer ($($grp.CSees) of $($grp.Everyone))"
Assert-That ([int] $grp.DSees -eq 0) 'a member of a group with no Organization grant, and nobody in the org chart, sees nobody'
# 2.4.12: Organization granted by DOMAIN\name counts whatever type it was recorded with, as the dashboard matches it: a
# group recorded as a user (GrantRole records one so when it cannot ask AD) through its members, and a person through
# the UPN the directory load records for them under that name. Before, both saw only their own people in Power BI.
$byName = (Invoke-SqlText -As Table -Sql @"
INSERT INTO dash.RoleGrant (Principal, PrincipalType, Role, Note, GrantedBy) VALUES
    (N'CONTOSO\risk-team', 'User', 'Organization', N'test', N'test'), (N'CONTOSO\cperson', 'User', 'Organization', N'test', N'test');
INSERT INTO audit.GroupMember (GroupName, UserPrincipalName) VALUES (N'CONTOSO\risk-team', N'b@contoso.com'), (N'CONTOSO\cperson', N'c@contoso.com');
SELECT (SELECT COUNT(DISTINCT UserKey) FROM bi.OrgSecurity WHERE ViewerUpn = N'b@contoso.com') AS BSees,
       (SELECT COUNT(DISTINCT UserKey) FROM bi.OrgSecurity WHERE ViewerUpn = N'c@contoso.com') AS CSees,
       (SELECT COUNT(*) FROM bi.OrgSecurity WHERE ViewerUpn LIKE N'CONTOSO\%') AS ByWindowsName,
       (SELECT COUNT(*) FROM bi.DimUser) AS Everyone;
"@).Rows[0]
[string[]] $loaded = & { . (Join-Path $root 'tools' 'Export-UsersWithHierarchy-ToSql.ps1'); Get-OrganizationGroup -ConnString (New-ConnString $script:ITDatabase) }   # one array, as Invoke-Main takes it
Invoke-SqlText -Sql "DELETE FROM audit.GroupMember WHERE GroupName IN (N'CONTOSO\risk-team', N'CONTOSO\cperson'); DELETE FROM dash.RoleGrant WHERE Principal IN (N'CONTOSO\risk-team', N'CONTOSO\cperson');"
Assert-That ([int] $byName.BSees -eq [int] $byName.Everyone -and [int] $byName.CSees -eq [int] $byName.Everyone -and [int] $byName.ByWindowsName -eq 0) `
    "2.4.12: Organization granted by DOMAIN\name as a user reaches Power BI, for a group and for a person ($($byName.BSees) and $($byName.CSees) of $($byName.Everyone))"
Assert-That ($loaded -contains 'CONTOSO\risk-team' -and $loaded -contains 'CONTOSO\cperson') "and the nightly directory load reads both grants, to record who they are ($($loaded -join ', '))"
& $maint @conn -Database $script:ITDatabase -Task SetOrgTop -Upn 'boss@contoso.com' | Out-Null   # as section 15 left it
Invoke-SqlText -Sql "ALTER ROLE CopilotInsightsApp DROP MEMBER it_app; DROP USER it_app;"

# ---------------------------------------------------------------------------
# 17. Agent build and publish activity, and the second part of 2.3.0's
# operations tables: Copilot Studio authoring records shredded into
# audit.AgentEvent (and kept out of the license events), copied to
# bi.FactAgentEvent, and the tables behind triage, digests, saved views,
# contracts, classifications, events and extracts.
# ---------------------------------------------------------------------------
Write-Host "`n17. Agent build and publish activity, and the operations tables (audit.usp_ShredAgentEvents)" -ForegroundColor Cyan

Invoke-SqlScript -Sql (Get-Content -LiteralPath (Join-Path $root 'sql' 'Create-AgentEventProcedure.sql') -Raw)
Invoke-SqlScript -Sql (Get-Content -LiteralPath (Join-Path $root 'sql' 'Create-AgentEventProcedure.sql') -Raw)   # re-runnable

$maker = [string] (Invoke-SqlText -As Scalar -Sql "SELECT TOP (1) UserPrincipalName FROM bi.DimUser WHERE UserPrincipalName LIKE N'%@%' ORDER BY UserPrincipalName;")
$bots = @(
    @{ Id = 'bot-0001'; Op = 'BotCreate'; At = '2026-09-11T09:00:00' }
    @{ Id = 'bot-0002'; Op = 'BotComponentCreate'; At = '2026-09-11T09:05:00'; Component = 'cr1_helper.topic.Greeting' }
    @{ Id = 'bot-0003'; Op = 'BotUpdateOperation-BotPublish'; At = '2026-09-12T10:00:00' }
    @{ Id = 'bot-0004'; Op = 'BotUpdateOperation-BotShare'; At = '2026-09-12T10:30:00' }
    @{ Id = 'bot-0005'; Op = 'BotUpdateOperation-BotNameUpdate'; At = '2026-09-13T08:00:00' }
    @{ Id = 'bot-0006'; Op = 'BotDelete'; At = '2026-09-18T16:00:00' }
)
foreach ($b in $bots) {
    $doc = [ordered]@{ CreationTime = $b.At; Id = $b.Id; Operation = $b.Op; Workload = 'PowerPlatform'; UserId = $maker; ResultStatus = 'Succeeded'
                       BotId = '00000000-0000-4000-9000-000000000001'; BotSchemaName = 'cr1_helper' }
    if ($b.Contains('Component')) { $doc.BotComponentSchemaName = $b.Component; $doc.BotComponentType = 'Topic' }
    Invoke-SqlText -Parameters @{ '@id' = $b.Id; '@op' = $b.Op; '@at' = $b.At; '@u' = $maker; '@json' = ($doc | ConvertTo-Json -Compress) } -Sql @"
INSERT INTO audit.CopilotAudit (RecordId, CreatedDateTime, RecordType, Operation, UserId, UserPrincipalName, AuditDataGz, WindowId)
VALUES (@id, @at, 'powerPlatformAdministratorActivity', @op, @u, @u, COMPRESS(CAST(@json AS NVARCHAR(MAX))), 1);
"@
}
Invoke-SqlText -Sql 'EXEC audit.usp_ShredAgentEvents;'
Invoke-SqlText -Sql 'EXEC audit.usp_ShredAgentEvents;'   # a second pass merges, never duplicates
$ae = (Invoke-SqlText -As Table -Sql @"
SELECT COUNT(*) AS N,
       STRING_AGG(CONCAT(RecordId, '=', Action), ',') WITHIN GROUP (ORDER BY RecordId) AS Actions,
       SUM(CASE WHEN BotSchemaName = N'cr1_helper' AND ActorUpn = N'$maker' THEN 1 ELSE 0 END) AS Attributed,
       MAX(CASE WHEN RecordId = 'bot-0002' THEN ComponentName END) AS Component
FROM audit.AgentEvent WHERE RecordId LIKE 'bot-%';
"@).Rows[0]
Assert-That ([int] $ae.N -eq 6) "every Copilot Studio authoring record is shredded once, on a re-run too ($($ae.N))"
Assert-That ("$($ae.Actions)" -eq 'bot-0001=Created,bot-0002=Component,bot-0003=Published,bot-0004=Shared,bot-0005=Updated,bot-0006=Deleted') "and each operation maps to its action ($($ae.Actions))"
Assert-That ([int] $ae.Attributed -eq 6 -and "$($ae.Component)" -eq 'cr1_helper.topic.Greeting') 'with the agent, the maker and the component from the record'

Invoke-SqlText -Sql 'EXEC audit.usp_ShredLicenseEvents' -TimeoutSeconds 600
Assert-That ([int] (Invoke-SqlText -As Scalar -Sql "SELECT COUNT(*) FROM audit.LicenseEvent WHERE RecordId LIKE 'bot-%' OR Operation LIKE N'Bot%'") -eq 0) 'the license-event shredder leaves the authoring records alone'

Invoke-SqlText -Sql "EXEC bi.usp_RefreshBi @SinceUtc = '2026-09-19', @LicenseEventDatabase = N'$script:ITDatabase';" -TimeoutSeconds 600
$fe = (Invoke-SqlText -As Table -Sql @"
SELECT COUNT(*) AS N, COUNT(ActorKey) AS WithUser, MIN(DateKey) AS FirstKey,
       (SELECT COUNT(*) FROM bi.AgentEvents WHERE RecordId LIKE 'bot-%') AS InView
FROM bi.FactAgentEvent WHERE RecordId LIKE 'bot-%';
"@).Rows[0]
Assert-That ([int] $fe.N -eq 6 -and [int] $fe.InView -eq 6) "bi.usp_RefreshBi copies the authoring records into bi.FactAgentEvent and bi.AgentEvents ($($fe.N))"
Assert-That ([int] $fe.WithUser -eq 6 -and [int] $fe.FirstKey -eq 20260911) 'each with its maker''s user key and its local day'

# The operations tables of 2.3.0's second part, their defaults, and their rules.
Invoke-SqlScript -Sql (Get-Content -LiteralPath (Join-Path $root 'sql' 'Create-CopilotOperationsSchema.sql') -Raw)   # once more: nothing doubles
$t2 = (Invoke-SqlText -As Table -Sql @"
SELECT (SELECT COUNT(*) FROM sys.tables WHERE SCHEMA_NAME(schema_id) = 'dash' AND name IN ('FlagCase', 'DigestPreference', 'DigestSend', 'LicenseAction', 'LicenseContract', 'SavedView', 'JobFamilyRule', 'UseCaseRule', 'EventMarker', 'Extract')) AS Tables_,
       (SELECT COUNT(*) FROM dash.UseCaseRule WHERE UpdatedBy = N'default') AS Rules,
       (SELECT COUNT(*) FROM dash.Setting WHERE Name IN ('AlertWebhookFormat', 'ManagerDigest', 'CertificateWarnDays', 'RolloutWaveSize')) AS Settings_,
       (SELECT COUNT(*) FROM dash.Setting WHERE Name = 'PersonalDigest') AS Personal,
       (SELECT Value FROM dash.Setting WHERE Name = 'MaxViewerLevel') AS MaxLevel,
       (SELECT COUNT(*) FROM dash.Setting WHERE Name LIKE 'NonHuman%') AS NonHumanSettings,
       (SELECT COUNT(*) FROM dash.Setting WHERE Name IN ('NonHumanGuests', 'NonHumanNames', 'NonHumanNoChart') AND Value = N'On') AS NonHumanOn,
       OBJECT_ID(N'dash.NonHumanException', N'U') AS NonHumanTable,
       COL_LENGTH(N'audit.PipelineRun', N'CertificateExpiresUtc') AS CertCol,
       COL_LENGTH(N'audit.CopilotLicense', N'UserType') AS UserTypeCol;
"@).Rows[0]
Assert-That ([int] $t2.Tables_ -eq 10 -and [int] $t2.Rules -eq 10 -and [int] $t2.Settings_ -eq 4) "the ten new tables, the ten default use-case rules and the new settings install once ($($t2.Tables_), $($t2.Rules), $($t2.Settings_))"
Assert-That ([int] $t2.NonHumanSettings -eq 7 -and [int] $t2.NonHumanOn -eq 3 -and $t2.NonHumanTable -isnot [DBNull]) "2.4.5: the non-human rules' seven settings, the three built-in rules on, and the confirmations table ($($t2.NonHumanSettings), $($t2.NonHumanOn))"
Assert-That ([int] $t2.Personal -eq 0 -and [string] $t2.MaxLevel -eq '0') "2.4.4: the personal email's setting is gone, and the dashboard opens to everyone until a level is chosen ($($t2.Personal), $($t2.MaxLevel))"
Assert-That ($t2.CertCol -isnot [DBNull] -and $t2.UserTypeCol -isnot [DBNull]) 'the pipeline run records the certificate, and the license record the user type'

function Test-Refused([string] $Sql) { try { Invoke-SqlText -Sql $Sql; $false } catch { $true } }
Assert-That (Test-Refused "INSERT INTO dash.FlagCase (RecordId, Status, UpdatedBy) VALUES ('x', 'Ignored', N'test');") 'a flag''s triage status must be one of the four'
Assert-That (Test-Refused "INSERT INTO dash.Extract (Name, Dataset, Folder, Frequency, CreatedBy) VALUES (N'x', 'DepartmentWeek', N'D:\x', 'Hourly', N'test');") 'an extract runs daily, weekly or monthly only'
Invoke-SqlText -Sql "INSERT INTO dash.DigestSend (Upn, Kind, PeriodKey, Status) VALUES (N'a@contoso.com', 'Manager', '2026-10-05', 'Sent');"
Assert-That (Test-Refused "INSERT INTO dash.DigestSend (Upn, Kind, PeriodKey, Status) VALUES (N'a@contoso.com', 'Manager', '2026-10-05', 'Sent');") 'a digest is recorded once per person, kind and period'

# The dashboard's role can write the new tables (it cannot read dash.Secret, as section 16 showed).
$w = [string] (Invoke-SqlText -As Scalar -Sql @"
IF USER_ID(N'it_app2') IS NULL CREATE USER it_app2 WITHOUT LOGIN;
ALTER ROLE CopilotInsightsApp ADD MEMBER it_app2;
DECLARE @r NVARCHAR(400) = N'ok';
EXECUTE AS USER = 'it_app2';
BEGIN TRY
    INSERT INTO dash.DigestPreference (Upn, ManagerDigestOptOut) VALUES (N'a@contoso.com', 1);
    UPDATE dash.DigestPreference SET ManagerDigestOptOut = 0 WHERE Upn = N'a@contoso.com';
    INSERT INTO dash.NonHumanException (Upn, Reason, ConfirmedBy) VALUES (N'svc-a@contoso.com', N'a person', N'test');
    DELETE FROM dash.NonHumanException WHERE Upn = N'svc-a@contoso.com';
    DECLARE @gm INT = (SELECT COUNT(*) FROM audit.GroupMember);
    INSERT INTO dash.EventMarker (Day, Label, Kind, UpdatedBy) VALUES ('2026-10-01', N'x', 'Other', N'test');
    INSERT INTO dash.LicenseAction (Kind, Upn, ExportedBy) VALUES ('revoke', N'a@contoso.com', N'test');
    UPDATE dash.DigestSend SET Status = 'Fail1' WHERE Upn = N'a@contoso.com';
    DELETE FROM dash.DigestPreference WHERE Upn = N'a@contoso.com';
END TRY BEGIN CATCH SET @r = ERROR_MESSAGE(); END CATCH;
REVERT;
ALTER ROLE CopilotInsightsApp DROP MEMBER it_app2; DROP USER it_app2;
SELECT @r;
"@)
Assert-That ($w -eq 'ok') "the dashboard's role writes manager opt-outs, non-human confirmations, events, license actions and digest sends, and reads group members ($w)"

# 2.4.1: every bi column is at least as wide as the flattened column it copies (a 450-character action failed the refresh).
$widths = (Invoke-SqlText -As Table -Sql @"
SELECT COL_LENGTH('bi.DimResourceAction', 'ResourceAction') - COL_LENGTH('audit.RawLogAccessedResource', 'Action') AS ActionGap,
       COL_LENGTH('bi.DimResourceType', 'ResourceType') - COL_LENGTH('audit.RawLogAccessedResource', 'Type') AS TypeGap,
       COL_LENGTH('bi.DimSite', 'Site') - COL_LENGTH('audit.RawLogAccessedResource', 'Site') AS SiteGap;
"@).Rows[0]
Assert-That ([int] $widths.ActionGap -ge 0 -and [int] $widths.TypeGap -ge 0 -and [int] $widths.SiteGap -ge 0) 'bi resource dimensions are as wide as the flattened columns they copy'
# 2.4.9: a full rebuild stopped part way is finished by the next refresh, whatever window that one asks for.
Invoke-SqlText -Sql "INSERT INTO bi.RebuildInProgress (StartedUtc) VALUES (SYSUTCDATETIME()); DELETE FROM bi.FactInteraction WHERE RecordId IN (SELECT TOP (3) RecordId FROM bi.FactInteraction ORDER BY CreatedUtc);"
Invoke-SqlText -Sql "EXEC bi.usp_RefreshBi @SinceUtc = '2999-01-01', @LicenseEventDatabase = N'$licDb';"
$cut = (Invoke-SqlText -As Table -Sql 'SELECT (SELECT COUNT_BIG(*) FROM bi.FactInteraction) AS F, (SELECT COUNT_BIG(*) FROM audit.RawLogFlattened) AS R, (SELECT COUNT(*) FROM bi.RebuildInProgress) AS M, (SELECT TOP (1) CASE WHEN SinceUtc IS NULL THEN 1 ELSE 0 END FROM bi.RefreshRun ORDER BY RunId DESC) AS Full_;').Rows[0]
Assert-That ([long] $cut.F -eq [long] $cut.R -and [int] $cut.M -eq 0 -and [int] $cut.Full_ -eq 1) "2.4.9: a full rebuild that was cut short is finished by the next refresh ($($cut.F) of $($cut.R) interactions), which then clears the mark"

# Premium use: the rollup's count of licensed interactions that needed the license, recomputed from the facts.
# Teams moved to Copilot Chat above (SetAppCategory, without -Apply): the next bi refresh corrects the stored
# flags on every day, and the next dashboard refresh, however recent its start, rolls those days up again.
Invoke-SqlText -Sql "EXEC bi.usp_RefreshBi @SinceUtc = '2999-01-01', @LicenseEventDatabase = N'$licDb';"
Invoke-SqlText -Sql "EXEC dash.usp_RefreshDashboard @SinceUtc = '2999-01-01';"
$prem = (Invoke-SqlText -As Table -Sql @"
SELECT (SELECT ISNULL(SUM(CAST(PremiumUse AS BIGINT)), 0) FROM dash.UserDay) AS Rolled,
       (SELECT ISNULL(SUM(CAST(PremiumUse AS BIGINT)), 0) FROM bi.FactInteraction) AS Stored,
       (SELECT ISNULL(SUM(CAST(PremiumUse AS BIGINT)), 0) FROM bi.Interactions) AS Viewed,
       (SELECT COUNT_BIG(*) FROM bi.FactInteraction AS f JOIN bi.DimApp AS a ON a.AppKey = f.AppKey
        WHERE f.UseKind IS NULL OR f.PremiumUse <> CASE WHEN f.LicenseStatusKey = 1
                     AND (f.UseKind = 1
                          OR (f.UseKind IN (2, 3) AND a.AppCategory = N'In-App Copilot' AND a.App <> N'Copilot Studio')
                          OR (f.UseKind = 3 AND a.AppCategory = N'Copilot Chat'))
                    THEN 1 ELSE 0 END) AS Wrong,
       (SELECT COUNT(*) FROM dash.UserDay WHERE PremiumUse > LicensedUse) AS TooMany,
       (SELECT ISNULL(SUM(CAST(ChatInteractions AS BIGINT)), 0) FROM dash.UserDay) AS ChatRolled,
       (SELECT COUNT_BIG(*) FROM bi.FactInteraction AS f JOIN bi.DimApp AS a ON a.AppKey = f.AppKey WHERE a.AppCategory = N'Copilot Chat') AS ChatFacts,
       (SELECT COUNT_BIG(*) FROM bi.FactInteraction AS f JOIN bi.DimAgent AS ag ON ag.AgentKey = f.AgentKey
        WHERE f.UseCaseKey <> f.AppKey * 4 + CAST(f.UsedWebSearch AS INT) * 2 + CASE WHEN ag.AgentId = N'(none)' THEN 0 ELSE 1 END) AS WrongKey,
       (SELECT COUNT_BIG(*) FROM bi.FactInteraction AS f WHERE NOT EXISTS (SELECT 1 FROM bi.UseCases AS u WHERE u.UseCaseKey = f.UseCaseKey)) AS NoUseCase;
"@).Rows[0]
Assert-That ([long] $prem.Wrong -eq 0 -and [long] $prem.Stored -eq [long] $prem.Viewed) "bi.FactInteraction.PremiumUse follows the rule on every interaction ($($prem.Stored) Premium), and bi.Interactions passes it on"
Assert-That ([long] $prem.ChatRolled -eq [long] $prem.ChatFacts) "an app's new category reaches the rollups' Copilot Chat counts on every day, not only new ones ($($prem.ChatRolled) of $($prem.ChatFacts))"
Assert-That ([long] $prem.Rolled -eq [long] $prem.Stored -and [int] $prem.TooMany -eq 0) "dash.UserDay.PremiumUse adds up the stored flag ($($prem.Rolled)), never more than the licensed use"
Assert-That ([long] $prem.WrongKey -eq 0 -and [long] $prem.NoUseCase -eq 0) "every interaction carries its use-case key, and bi.UseCases has a use case for each"

# ---------------------------------------------------------------------------
# 18. A slow night (2.5.4). tools/Trace-CopilotNightly.sql shows what the task's connections are running and records
# the statements that run a second or longer; tools/Measure-CopilotNightly.ps1 takes a recorded run apart. The trace is
# run as SSMS runs it, with @Do set in the script (sqlcmd puts -v Do= in its place); this suite's own connections carry
# the task's application name, so a probe procedure run here is a statement of the task's.
# ---------------------------------------------------------------------------
Write-Host "`n18. A slow night (tools/Trace-CopilotNightly.sql, tools/Measure-CopilotNightly.ps1)" -ForegroundColor Cyan

$traceSql = Get-Content -LiteralPath (Join-Path $root 'tools' 'Trace-CopilotNightly.sql') -Raw
$doDefault = "@Do         NVARCHAR(10) = N'now'"
Assert-That ($traceSql.Contains($doDefault) -and $traceSql.Contains("IF LEFT(N'`$(Do)', 1) <> N'`$' SET @Do = N'`$(Do)';")) 'the trace script reads its action from @Do, or from sqlcmd -v Do='
$trace = { param($Do, $As = 'NonQuery') Invoke-SqlText -Catalog 'master' -As $As -Sql $traceSql.Replace($doDefault, "@Do         NVARCHAR(10) = N'$Do'") }
if ([int] (Invoke-SqlText -Catalog 'master' -As Scalar -Sql "SELECT COUNT(*) FROM sys.server_event_sessions WHERE name = N'CopilotNightly';") -gt 0) {
    Write-Host '  SKIP  a recording named CopilotNightly is running on this server, and is left alone' -ForegroundColor Yellow
}
else {
    Invoke-SqlText -Sql "CREATE OR ALTER PROCEDURE dbo.usp_TraceProbe AS BEGIN SET NOCOUNT ON; WAITFOR DELAY '00:00:03'; END;"
    try {
        [void] (& $trace 'start')
        $again = try { [void] (& $trace 'start'); '' } catch { $_.Exception.GetBaseException().Message }
        Assert-That ($again -like '*exists already*') "start: a second recording is refused while one runs ($again)"
        # The probe runs for three seconds; 'now' is asked while it does.
        $probe = [System.Data.SqlClient.SqlConnection]::new((New-ConnString $script:ITDatabase) + ';Pooling=false')
        try {
            $probe.Open()
            $probeCmd = $probe.CreateCommand(); $probeCmd.CommandText = 'EXEC dbo.usp_TraceProbe;'; $probeCmd.CommandTimeout = 60
            $running = $probeCmd.ExecuteNonQueryAsync()
            Start-Sleep -Milliseconds 800
            $now = & $trace 'now' 'Table'
            [void] $running.Wait(60000)
        }
        finally { $probe.Dispose() }
        $seen = @($now.Rows | Where-Object { "$($_.Procedure)" -eq 'dbo.usp_TraceProbe' -and "$($_.Program)" -eq 'CopilotAuditBackfill' })
        Assert-That ($seen.Count -eq 1 -and "$($seen[0].Statement)" -like 'WAITFOR*' -and "$($seen[0].Database)" -eq $script:ITDatabase) `
            "now: the statement the task is running, with its procedure and database ($($seen.Count) row(s))"
        [void] (& $trace 'stop')
        $read = & $trace 'read' 'Table'
        $rec = @($read.Rows | Where-Object { "$($_.Database)" -eq $script:ITDatabase -and "$($_.Procedure)" -eq 'usp_TraceProbe' })
        Assert-That ($rec.Count -eq 1 -and [decimal] $rec[0].Seconds -ge 3) "read: the recording has the three-second statement, under its procedure ($(@($read.Rows).Count) procedure row(s))"
        $left = [int] (Invoke-SqlText -Catalog 'master' -As Scalar -Sql "SELECT COUNT(*) FROM sys.server_event_sessions WHERE name = N'CopilotNightly';")
        Assert-That ($left -eq 0) 'stop: the session is removed, and what it recorded can still be read'
    }
    finally {
        try { [void] (& $trace 'stop') } catch { Write-Host "  (could not stop the recording: $($_.Exception.Message))" }
        Invoke-SqlText -Sql 'DROP PROCEDURE IF EXISTS dbo.usp_TraceProbe;'
    }
}

# The profile needs Windows authentication, as the health check does: run where this suite has it.
if ($script:ITIntegrated) {
    $profileOut = & (Join-Path $root 'tools' 'Measure-CopilotNightly.ps1') -SqlServer $script:ITServer -Database $script:ITDatabase -LicenseDatabase $script:ITDatabase -TrustServerCertificate 6>&1 | Out-String
    Assert-That ($profileOut -like '*1. The last 7 runs*' -and $profileOut -like '*in detail*' -and $profileOut -like '*Where the time went*' -and $profileOut -like '*Copilot licenses from Graph*') `
        'Measure-CopilotNightly.ps1 reads this database''s recorded run and its step'
}
else { Write-Host '  SKIP  Measure-CopilotNightly.ps1 connects with Windows authentication only' -ForegroundColor Yellow }

# ---------------------------------------------------------------------------
Write-Host ''
if ($script:Failures.Count) {
    Write-Host "$($script:Failures.Count) assertion(s) failed." -ForegroundColor Red
    exit 1
}
Write-Host 'All SQL integration tests passed.' -ForegroundColor Green
