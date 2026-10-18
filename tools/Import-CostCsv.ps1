<#
.SYNOPSIS
    Loads a CSV export into one of the tables Leadership > Costs and savings reads (2.6.6): Fabric capacity and
    workload days, the cloud flow inventory, Power Platform activity, Dataverse capacity and Copilot Credits.

.DESCRIPTION
    Each source has its own export, and none of them needs an API permission: the Fabric Capacity Metrics app's
    tables, the Power Platform admin center's capacity, consumption and usage reports, and a flow inventory (the
    CoE Starter Kit's, or the admin center's). Shape the export so its headings are the table's column names
    (docs\OPERATIONS.md, Costs and savings, lists them for each table, with the export that fills it), then:

        .\tools\Import-CostCsv.ps1 -Table FabricCapacityDay -Path .\capacity.csv

    How a load writes:
      - Every row is matched to the table by its key columns (a capacity and a day, a flow, a person and a
        product...). A row already there is replaced; a new one is added; nothing else changes. So a day, a week or
        six months can be loaded, and loaded again.
      - With -Replace, the table is replaced whole by the file, for the inventories that are a picture of now
        (the flows, who used what): what the file does not list is gone. The delete guard (2.6.5) holds a file
        more than -MaxDropPercent smaller than the table (CostImportMaxDropPercent in the settings file, 15 by
        default); -AllowDrop lets a real drop through once.
      - A value is read as the column's type: dates as yyyy-MM-dd (or with a time), numbers with a point for the
        decimals, True or False, 1 or 0 for yes and no. A blank is NULL. A row whose key cannot be read is left
        out and counted; a key given twice keeps its last row.
      - Headings are matched to columns whatever their case; a heading the table has no column for is ignored and
        named, and so is a missing column that is not part of the key.

    With no -SqlServer, SqlServer, Database and TrustServerCertificate come from copilot-audit.settings.psd1.

.PARAMETER Table
    FabricCapacityDay, FabricWorkloadDay, PowerAutomateFlow, PowerPlatformUserActivity, PowerPlatformCapacityDay or
    PowerPlatformCreditDay (all in the audit schema).

.PARAMETER Path
    The CSV file: a heading row, then the rows.

.EXAMPLE
    .\tools\Import-CostCsv.ps1 -Table PowerAutomateFlow -Path .\flows.csv -Replace
.EXAMPLE
    .\tools\Import-CostCsv.ps1 -Table PowerPlatformCreditDay -Path .\credits-september.csv -SqlServer SQLPROD01
#>
#Requires -Version 7.2
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('FabricCapacityDay', 'FabricWorkloadDay', 'PowerAutomateFlow', 'PowerPlatformUserActivity', 'PowerPlatformCapacityDay', 'PowerPlatformCreditDay')]
    [string] $Table,
    [Parameter(Mandatory)][string] $Path,
    [switch] $Replace,
    [string] $SqlServer,
    [string] $Database = 'CopilotAudit',
    [switch] $TrustServerCertificate,
    [string] $ConnectionString,
    [string] $SettingsPath = (Join-Path (Split-Path $PSScriptRoot -Parent) 'copilot-audit.settings.psd1'),
    [ValidateRange(0, 100)][double] $MaxDropPercent = 15,
    [switch] $AllowDrop
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'DropGuard.ps1')

function Get-CostCsvPlan {
    <#
        Which of the file's headings load which of the table's columns, and what is wrong: a key column the file
        lacks (it cannot load), headings with no column, columns with no heading. Pure, for the tests.
    #>
    param([string[]] $Headings, [string[]] $Columns, [string[]] $Key)
    $map = [ordered]@{}
    $extra = [System.Collections.Generic.List[string]]::new()
    foreach ($h in $Headings) {
        $c = $Columns | Where-Object { $_ -ieq $h.Trim() } | Select-Object -First 1
        if ($c -and -not $map.Contains($c)) { $map[$c] = $h } elseif (-not $c) { $extra.Add($h) }
    }
    [pscustomobject]@{
        Map        = $map
        MissingKey = @($Key | Where-Object { -not $map.Contains($_) })
        Extra      = $extra.ToArray()
        Unfilled   = @($Columns | Where-Object { -not $map.Contains($_) -and $_ -notin $Key })
    }
}

function Invoke-Main {
    $setting = if (Test-Path -LiteralPath $SettingsPath) { Import-PowerShellDataFile -LiteralPath $SettingsPath } else { @{} }
    if (-not $ConnectionString) {
        if (-not $SqlServer) { $script:SqlServer = $setting['SqlServer'] }
        if (-not $script:DatabaseGiven -and $setting.Contains('Database') -and $setting['Database']) { $script:Database = $setting['Database'] }
        if ($setting['TrustServerCertificate']) { $script:TrustServerCertificate = $true }
        if (-not $SqlServer) { throw 'Give -SqlServer, or SqlServer in the settings file.' }
    }
    $pct = Resolve-MaxDropPercent -What 'Cost import' -Given $MaxDropPercent -WasGiven $script:MaxDropGiven -Settings $setting
    if (-not (Test-Path -LiteralPath $Path)) { throw "No file at $Path." }
    $rows = @(Import-Csv -LiteralPath $Path)
    $name = "audit.$Table"

    $b = [System.Data.SqlClient.SqlConnectionStringBuilder]::new()
    if ($ConnectionString) { $b = [System.Data.SqlClient.SqlConnectionStringBuilder]::new($ConnectionString) }
    else { $b['Data Source'] = $SqlServer; $b['Initial Catalog'] = $Database; $b['Integrated Security'] = $true; $b['Encrypt'] = $true; $b['TrustServerCertificate'] = [bool] $TrustServerCertificate }
    $b['Application Name'] = 'Copilot cost import'
    $conn = [System.Data.SqlClient.SqlConnection]::new($b.ConnectionString)
    $conn.Open()
    function Sql([string] $Text, $Tx, [switch] $Scalar, [switch] $Rows) {
        $c = $conn.CreateCommand(); $c.CommandText = $Text; $c.Transaction = $Tx; $c.CommandTimeout = 600
        if ($Scalar) { return $c.ExecuteScalar() }
        if ($Rows) { $t = [System.Data.DataTable]::new(); $t.Load($c.ExecuteReader()); return , $t }
        [void] $c.ExecuteNonQuery()
    }
    try {
        if ([int] (Sql "SELECT CASE WHEN OBJECT_ID(N'$name', N'U') IS NULL THEN 0 ELSE 1 END" $null -Scalar) -eq 0) {
            throw "$name is not in $($conn.Database): run Install-CopilotAudit.ps1 from 2.6.6 or later first."
        }
        $cols = Sql @"
SELECT c.name AS Name, TYPE_NAME(c.user_type_id) AS TypeName, c.max_length AS MaxLength, c.precision AS [Precision], c.scale AS Scale,
       CASE WHEN ic.column_id IS NULL THEN 0 ELSE 1 END AS IsKey
FROM sys.columns AS c
LEFT JOIN sys.indexes AS i ON i.object_id = c.object_id AND i.is_primary_key = 1
LEFT JOIN sys.index_columns AS ic ON ic.object_id = c.object_id AND ic.index_id = i.index_id AND ic.column_id = c.column_id
WHERE c.object_id = OBJECT_ID(N'$name') AND c.name <> N'LoadedUtc' ORDER BY c.column_id;
"@ $null -Rows
        $all = @($cols.Rows | ForEach-Object { [string] $_.Name })
        $key = @($cols.Rows | Where-Object { $_.IsKey -eq 1 } | ForEach-Object { [string] $_.Name })
        $headings = if ($rows.Count) { @($rows[0].PSObject.Properties.Name) } else { @() }
        $plan = Get-CostCsvPlan -Headings $headings -Columns $all -Key $key
        if ($plan.MissingKey.Count) { throw "$Path has no $($plan.MissingKey -join ', ') heading: $name is keyed by $($key -join ', '), and every row needs them." }
        if ($plan.Extra.Count) { Write-Warning "Headings with no column in $name, ignored: $($plan.Extra -join ', ')." }
        if ($plan.Unfilled.Count) { Write-Warning "Columns the file does not fill, left NULL: $($plan.Unfilled -join ', ')." }

        # The rows as text, then read as the columns' types in SQL Server, whatever this machine's culture.
        $mapped = @($plan.Map.Keys)
        $dt = [System.Data.DataTable]::new()
        [void] $dt.Columns.Add('RowNo', [int])
        foreach ($c in $mapped) { [void] $dt.Columns.Add($c, [string]) }
        $n = 0
        foreach ($r in $rows) { $n++; $vals = @($n) + @($mapped | ForEach-Object { $v = $r.($plan.Map[$_]); if ($null -eq $v -or "$v".Trim() -eq '') { [DBNull]::Value } else { "$v".Trim() } }); [void] $dt.Rows.Add($vals) }
        Sql ("CREATE TABLE #raw (RowNo INT NOT NULL, " + (($mapped | ForEach-Object { "[$_] NVARCHAR(4000) NULL" }) -join ', ') + ');') $null
        if ($dt.Rows.Count) {
            $bulk = [System.Data.SqlClient.SqlBulkCopy]::new($conn); $bulk.DestinationTableName = '#raw'; $bulk.BulkCopyTimeout = 0
            foreach ($c in $dt.Columns) { [void] $bulk.ColumnMappings.Add($c.ColumnName, $c.ColumnName) }
            $bulk.WriteToServer($dt); $bulk.Close()
        }
        function TypeOf($c) {
            $t = [string] $c.TypeName
            switch -regex ($t) {
                '^(n?varchar|n?char)$' { if ([int] $c.MaxLength -eq -1) { "$t(MAX)" } elseif ($t -like 'n*') { "$t($([int] $c.MaxLength / 2))" } else { "$t($($c.MaxLength))" } }
                '^(decimal|numeric)$' { "$t($($c.Precision), $($c.Scale))" }
                '^datetime2$' { "datetime2($($c.Scale))" }
                default { $t }
            }
        }
        $typed = @($cols.Rows | Where-Object { $_.Name -in $mapped } | ForEach-Object {
            $t = TypeOf $_
            # Text is cut to the column's width, as the other loads do; a date may carry a time or a trailing Z.
            if ($t -match '^n?(var)?char') { "CAST(LEFT(r.[$($_.Name)], $(if ([int] $_.MaxLength -eq -1) { 4000 } elseif ($t -like 'n*') { [int] $_.MaxLength / 2 } else { [int] $_.MaxLength })) AS $t) AS [$($_.Name)]" }
            elseif ($t -eq 'bit') { "CASE WHEN r.[$($_.Name)] IN (N'1', N'True', N'Yes', N'Y') THEN CAST(1 AS BIT) WHEN r.[$($_.Name)] IN (N'0', N'False', N'No', N'N') THEN CAST(0 AS BIT) END AS [$($_.Name)]" }
            elseif ($t -match '^date') { "TRY_CONVERT($t, REPLACE(r.[$($_.Name)], N'Z', N''), 126) AS [$($_.Name)]" }
            else { "TRY_CONVERT($t, REPLACE(r.[$($_.Name)], N',', N'')) AS [$($_.Name)]" }
        })
        $keyNotNull = ($key | ForEach-Object { "[$_] IS NOT NULL" }) -join ' AND '
        Sql @"
SELECT r.RowNo, $($typed -join ', ') INTO #typed FROM #raw AS r;
DELETE FROM #typed WHERE NOT ($keyNotNull);
WITH d AS (SELECT ROW_NUMBER() OVER (PARTITION BY $(($key | ForEach-Object { "[$_]" }) -join ', ') ORDER BY RowNo DESC) AS n FROM #typed) DELETE FROM d WHERE n > 1;
"@ $null
        $good = [int] (Sql 'SELECT COUNT(*) FROM #typed' $null -Scalar)
        $skipped = $rows.Count - $good
        $before = [long] (Sql "SELECT COUNT_BIG(*) FROM $name" $null -Scalar)
        if ($Replace) {
            Assert-DropGuard -What "Cost import ($name)" -Before $before -After $good -MaxDropPercent $pct -Setting 'CostImportMaxDropPercent' -AllowDrop:$AllowDrop
        }
        $list = ($mapped | ForEach-Object { "[$_]" }) -join ', '
        $on = ($key | ForEach-Object { "t.[$_] = s.[$_]" }) -join ' AND '
        $tx = $conn.BeginTransaction()
        try {
            if ($Replace) { Sql "DELETE FROM $name;" $tx } else { Sql "DELETE t FROM $name AS t JOIN #typed AS s ON $on;" $tx }
            Sql "INSERT INTO $name ($list) SELECT $list FROM #typed;" $tx
            $tx.Commit()
        }
        catch { $tx.Rollback(); throw }
        $after = [long] (Sql "SELECT COUNT_BIG(*) FROM $name" $null -Scalar)
        Write-Host ("{0}: {1:N0} row(s) loaded from {2}{3}; the table now holds {4:N0} (it held {5:N0})." -f $name, $good, (Split-Path $Path -Leaf),
            $(if ($skipped) { ", $skipped left out (a key that could not be read, or given again)" } else { '' }), $after, $before)
    }
    finally { $conn.Dispose() }
}

$script:MaxDropGiven = $PSBoundParameters.ContainsKey('MaxDropPercent')
$script:DatabaseGiven = $PSBoundParameters.ContainsKey('Database')
# Dot-sourcing (tests) loads the functions without running anything.
if ($MyInvocation.InvocationName -ne '.') {
    try { Invoke-Main }
    catch {
        if (-not (Test-DropRefusal $_)) { throw }
        Write-Host "Refused: $($_.Exception.Message)"
        exit $script:DropRefusedExitCode
    }
}
