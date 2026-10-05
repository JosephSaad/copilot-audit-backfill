<#
.SYNOPSIS
    Downloads an Excel workbook and loads each named sheet into its own table in the Copilot
    database: the rows are deleted and loaded again on every run.

.DESCRIPTION
    A sheet's first row names its columns. A table that does not exist is created with one
    NVARCHAR(MAX) column per heading; a heading that is new since is added to it. Each table is
    emptied and reloaded in one transaction, so a reader never sees it empty. Reads .xlsx with
    the ImportExcel module: put it in a Modules folder beside the scripts
    (Save-Module ImportExcel -Path <scripts>\Modules), so the nightly task's account finds it.

.EXAMPLE
    .\tools\Import-AgentWorkbook.ps1 -Url 'https://intranet/agents.xlsx' -SqlServer SQLPROD01
#>
#Requires -Version 7.2
param(
    [Parameter(Mandatory)][string] $Url,   # http(s) address, or a file or UNC path
    [Parameter(Mandatory)][string] $SqlServer,
    [string] $Database = 'CopilotAudit',
    [switch] $TrustServerCertificate,
    [hashtable] $Sheets = @{ 'AgentInventory' = 'dbo.AgentInventory'; 'AgentsInProd' = 'dbo.AgentsInProd'; 'ParentChildAgent' = 'dbo.ParentChildAgent' }
)
$ErrorActionPreference = 'Stop'
$modules = Join-Path (Split-Path $PSScriptRoot -Parent) 'Modules'
if (Test-Path $modules) { $env:PSModulePath = "$modules;$env:PSModulePath" }
Import-Module ImportExcel

$file = Join-Path ([IO.Path]::GetTempPath()) "agent-workbook-$PID.xlsx"
if ($Url -match '^https?://') { Invoke-WebRequest -Uri $Url -OutFile $file -UseDefaultCredentials }
else { Copy-Item -LiteralPath $Url -Destination $file }

$conn = [System.Data.SqlClient.SqlConnection]::new("Server=$SqlServer;Database=$Database;Integrated Security=true;Encrypt=true;TrustServerCertificate=$([bool] $TrustServerCertificate)")
$conn.Open()
function Sql([string] $Text, $Tx) { $c = $conn.CreateCommand(); $c.CommandText = $Text; $c.Transaction = $Tx; $c.CommandTimeout = 0; [void] $c.ExecuteNonQuery() }
try {
    foreach ($sheet in $Sheets.Keys) {
        $table = $Sheets[$sheet]
        $rows = @(Import-Excel -Path $file -WorksheetName $sheet)
        if (-not $rows) { Write-Warning "Sheet $sheet is empty or missing: $table left as it is."; continue }
        $dt = [System.Data.DataTable]::new()
        $cols = $rows[0].PSObject.Properties.Name
        foreach ($c in $cols) { [void] $dt.Columns.Add($c, [string]) }
        foreach ($r in $rows) {
            [void] $dt.Rows.Add(@($cols | ForEach-Object {
                $v = $r.$_
                if ($null -eq $v) { [DBNull]::Value } elseif ($v -is [datetime]) { $v.ToString('yyyy-MM-dd HH:mm:ss') } else { "$v" } }))
        }

        # The table, and any heading new since it was made, as text columns.
        $q = ($table -split '\.' | ForEach-Object { "[$($_.Trim('[]'))]" }) -join '.'
        Sql "IF OBJECT_ID(N'$table', N'U') IS NULL CREATE TABLE $q ([$($cols[0].Replace(']', ']]'))] NVARCHAR(MAX) NULL);" $null
        foreach ($c in $cols) { Sql "IF COL_LENGTH(N'$table', N'$($c.Replace("'", "''"))') IS NULL ALTER TABLE $q ADD [$($c.Replace(']', ']]'))] NVARCHAR(MAX) NULL;" $null }

        $tx = $conn.BeginTransaction()
        try {
            Sql "DELETE FROM $q;" $tx
            $bulk = [System.Data.SqlClient.SqlBulkCopy]::new($conn, [System.Data.SqlClient.SqlBulkCopyOptions]::TableLock, $tx)
            $bulk.DestinationTableName = $q; $bulk.BulkCopyTimeout = 0
            foreach ($c in $cols) { [void] $bulk.ColumnMappings.Add($c, $c) }
            $bulk.WriteToServer($dt)
            $tx.Commit()
        }
        catch { $tx.Rollback(); throw }
        Write-Host "$sheet -> $table`: $($dt.Rows.Count) row(s)."
    }

    # The dashboard's agent names from the inventory, matched on the audit log's agent identity: the schema name
    # it ends in first, then the bot, title or Entra id it holds. Names set by hand in Admin are left alone.
    $c = $conn.CreateCommand(); $c.CommandTimeout = 0
    $c.CommandText = @"
IF OBJECT_ID(N'dbo.AgentInventory', N'U') IS NULL OR OBJECT_ID(N'dash.AgentAlias', N'U') IS NULL OR OBJECT_ID(N'bi.DimAgent', N'U') IS NULL RETURN;
WITH w AS (
    SELECT LEFT(LTRIM(RTRIM(AgentName)), 200) AS AgentName, LEFT(NULLIF(LTRIM(RTRIM(OwnerUPN)), N''), 256) AS OwnerUPN,
           NULLIF(LTRIM(RTRIM(SchemaName)), N'') AS s, NULLIF(LTRIM(RTRIM(AgentId)), N'') AS g,
           NULLIF(LTRIM(RTRIM(TitleId)), N'') AS t, NULLIF(LTRIM(RTRIM(EntraAgentId)), N'') AS e
    FROM dbo.AgentInventory WHERE NULLIF(LTRIM(RTRIM(AgentName)), N'') IS NOT NULL),
m AS (
    SELECT d.AgentKey, w.AgentName, w.OwnerUPN, ROW_NUMBER() OVER (PARTITION BY d.AgentKey ORDER BY k.Rank) AS rn
    FROM bi.DimAgent AS d
    CROSS APPLY bi.fnAgentIdentity(d.AgentId, d.AgentId) AS i
    JOIN w ON 1 = 1
    CROSS APPLY (SELECT CASE WHEN i.DerivedName = w.s OR RIGHT(d.AgentId, LEN(w.s)) = w.s THEN 1
                             WHEN CHARINDEX(w.g, d.AgentId) > 0 THEN 2
                             WHEN CHARINDEX(w.t, d.AgentId) > 0 THEN 3
                             WHEN CHARINDEX(w.e, d.AgentId) > 0 THEN 4 END AS Rank) AS k
    WHERE d.AgentId <> N'(none)' AND k.Rank IS NOT NULL)
MERGE dash.AgentAlias AS a
USING (SELECT AgentKey, AgentName, OwnerUPN FROM m WHERE rn = 1) AS s ON a.AgentKey = s.AgentKey
WHEN MATCHED AND a.UpdatedBy = N'Agent workbook'
     AND (a.FriendlyName <> s.AgentName OR ISNULL(a.Owner, N'') <> ISNULL(s.OwnerUPN, N'')) THEN
    UPDATE SET FriendlyName = s.AgentName, Owner = s.OwnerUPN, UpdatedUtc = SYSUTCDATETIME()
WHEN NOT MATCHED BY TARGET THEN
    INSERT (AgentKey, FriendlyName, Owner, UpdatedBy) VALUES (s.AgentKey, s.AgentName, s.OwnerUPN, N'Agent workbook')
WHEN NOT MATCHED BY SOURCE AND a.UpdatedBy = N'Agent workbook' THEN DELETE;
SELECT COUNT(*) FROM dash.AgentAlias WHERE UpdatedBy = N'Agent workbook';
"@
    $n = $c.ExecuteScalar()
    if ($null -ne $n) { Write-Host "Agent names from the workbook: $n agent(s) named." }
}
finally { $conn.Dispose(); Remove-Item -LiteralPath $file -ErrorAction SilentlyContinue }
