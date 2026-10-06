<#
.SYNOPSIS
    Loads the agent inventory workbook into the Copilot database: each sheet into its own table, emptied and
    loaded again on every run, then the inventory the agent reports read.

.DESCRIPTION
    1. Fetches the workbook. From SharePoint Online (-SiteUrl): the most recently created file matching
       -FileFilter in -Folder, with PnP.PowerShell, as its own app registration by its certificate. That app
       needs SharePoint's Sites.Selected application permission and read on the site
       (Grant-PnPAzureADAppSitePermission), or Sites.Read.All. Or from an intranet address or a file (-Url).
    2. Loads each sheet as it is: by default AgentInventory, AgentsInProd and ParentChildAgent into the dbo
       tables of those names. A sheet's first row names its columns; a table that does not exist is made with a
       text column for each, and a heading added later becomes a column. Each table is emptied and loaded in one
       transaction, so a reader never sees it empty. A sheet that is empty or missing leaves its table as it was.
    3. Fills dash.AgentInventory (one row per agent the workbook lists, the current agents) and
       dash.AgentChildLink, matching each agent to the one the audit log knows: by the schema name its identity
       ends in, then the bot id, the title id or the Entra agent id it holds.
    4. Names the agents in the report tables (bi.usp_NameAgents): the workbook's name, or the audit log's for an
       agent the workbook does not list or leaves unnamed.
    5. Records the load in dash.AgentInventoryRun, which Admin > Health shows.

    With no -SiteUrl or -Url, everything comes from the settings file beside the scripts
    (copilot-audit.settings.psd1): AgentWorkbookSiteUrl, AgentWorkbookFolder, AgentWorkbookFilter,
    AgentWorkbookTenantId (else TenantId), AgentWorkbookClientId and AgentWorkbookCertificateThumbprint, or
    AgentWorkbookUrl; and SqlServer, Database and TrustServerCertificate.

    Reads .xlsx with the ImportExcel module. Put it, and PnP.PowerShell for SharePoint Online (PowerShell 7.4.6
    or later), in a Modules folder beside the scripts (Save-Module ImportExcel, PnP.PowerShell -Path
    <scripts>\Modules), so the nightly task's account finds them.

.EXAMPLE
    .\tools\Import-AgentWorkbook.ps1          # as the nightly run does, from the settings file
.EXAMPLE
    .\tools\Import-AgentWorkbook.ps1 -Url '\\fileserver\Copilot\Agents.xlsx' -SqlServer SQLPROD01
#>
#Requires -Version 7.2
param(
    [string] $Url,                            # an intranet address, or a file or UNC path
    [string] $SiteUrl,                        # a SharePoint Online site: its newest workbook is taken
    [string] $Folder = 'Shared Documents',    # the Documents library, or a folder in it, site-relative
    [string] $FileFilter = '*.xlsx',
    [string] $TenantId, [string] $ClientId, [string] $CertificateThumbprint,
    [string] $SqlServer,
    [string] $Database = 'CopilotAudit',
    [switch] $TrustServerCertificate,
    [string] $SettingsPath = (Join-Path (Split-Path $PSScriptRoot -Parent) 'copilot-audit.settings.psd1'),
    [hashtable] $Sheets = @{ 'AgentInventory' = 'dbo.AgentInventory'; 'AgentsInProd' = 'dbo.AgentsInProd'; 'ParentChildAgent' = 'dbo.ParentChildAgent' }
)
$ErrorActionPreference = 'Stop'
$modules = Join-Path (Split-Path $PSScriptRoot -Parent) 'Modules'
if (Test-Path $modules) { $env:PSModulePath = "$modules;$env:PSModulePath" }

# What the command line does not give comes from the settings file.
if (-not $SiteUrl -and -not $Url) {
    if (-not (Test-Path -LiteralPath $SettingsPath)) { throw "Give -SiteUrl or -Url, or put AgentWorkbookSiteUrl or AgentWorkbookUrl in $SettingsPath." }
    $s = Import-PowerShellDataFile -LiteralPath $SettingsPath
    function Setting($k) { if ($s.Contains($k) -and $s[$k]) { $s[$k] } }
    $SiteUrl = Setting 'AgentWorkbookSiteUrl'; $Url = Setting 'AgentWorkbookUrl'
    if (Setting 'AgentWorkbookFolder') { $Folder = Setting 'AgentWorkbookFolder' }
    if (Setting 'AgentWorkbookFilter') { $FileFilter = Setting 'AgentWorkbookFilter' }
    if (-not $TenantId) { $TenantId = if (Setting 'AgentWorkbookTenantId') { Setting 'AgentWorkbookTenantId' } else { Setting 'TenantId' } }
    if (-not $ClientId) { $ClientId = Setting 'AgentWorkbookClientId' }
    if (-not $CertificateThumbprint) { $CertificateThumbprint = Setting 'AgentWorkbookCertificateThumbprint' }
    if (-not $SqlServer) { $SqlServer = Setting 'SqlServer' }
    if (-not $PSBoundParameters.ContainsKey('Database') -and (Setting 'Database')) { $Database = Setting 'Database' }
    if (Setting 'TrustServerCertificate') { $TrustServerCertificate = $true }
    if (-not $SiteUrl -and -not $Url) { throw "Neither AgentWorkbookSiteUrl nor AgentWorkbookUrl is in $SettingsPath." }
}
if (-not $SqlServer) { throw 'Give -SqlServer, or SqlServer in the settings file.' }
if ($SiteUrl -and -not ($ClientId -and $CertificateThumbprint -and $TenantId)) {
    throw 'SharePoint Online needs the workbook app: -TenantId, -ClientId and -CertificateThumbprint (AgentWorkbookClientId and AgentWorkbookCertificateThumbprint in the settings).'
}

$conn = [System.Data.SqlClient.SqlConnection]::new("Server=$SqlServer;Database=$Database;Integrated Security=true;Encrypt=true;TrustServerCertificate=$([bool] $TrustServerCertificate)")
$conn.Open()
function Sql([string] $Text, $Tx, [switch] $Scalar) {
    $c = $conn.CreateCommand(); $c.CommandText = $Text; $c.Transaction = $Tx; $c.CommandTimeout = 0
    if ($Scalar) { $c.ExecuteScalar() } else { [void] $c.ExecuteNonQuery() }
}
function Q([string] $s) { if ($null -eq $s) { 'NULL' } else { "N'" + $s.Replace("'", "''") + "'" } }

# The load is recorded for Admin > Health, from its start, so a failure shows too.
$runId = Sql "IF OBJECT_ID(N'dash.AgentInventoryRun', N'U') IS NOT NULL BEGIN INSERT INTO dash.AgentInventoryRun DEFAULT VALUES; SELECT CAST(SCOPE_IDENTITY() AS INT); END" $null -Scalar
$file = Join-Path ([IO.Path]::GetTempPath()) "agent-workbook-$PID.xlsx"
try {
    Import-Module ImportExcel
    $source = $Url; $created = $null
    if ($SiteUrl) {
        Import-Module PnP.PowerShell
        Connect-PnPOnline -Url $SiteUrl -ClientId $ClientId -Tenant $TenantId -Thumbprint $CertificateThumbprint
        $latest = Get-PnPFolderItem -FolderSiteRelativeUrl $Folder -ItemType File |
            Where-Object Name -like $FileFilter | Sort-Object TimeCreated -Descending | Select-Object -First 1
        if (-not $latest) { throw "No $FileFilter file in $Folder on $SiteUrl." }
        $source = "$SiteUrl/$Folder/$($latest.Name)"; $created = $latest.TimeCreated
        Write-Host "Newest workbook: $($latest.Name), created $($created.ToString('yyyy-MM-dd HH:mm')) UTC."
        Get-PnPFile -Url $latest.ServerRelativeUrl -Path (Split-Path $file) -Filename (Split-Path $file -Leaf) -AsFile -Force
        Disconnect-PnPOnline
    }
    elseif ($Url -match '^https?://') { Invoke-WebRequest -Uri $Url -OutFile $file -UseDefaultCredentials }
    else { Copy-Item -LiteralPath $Url -Destination $file; $created = (Get-Item -LiteralPath $Url).CreationTimeUtc }

    # 1. Each sheet as it is, into its own table.
    $loaded = @{}
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
        # The dashboard reads the sheets as they are (the agent's every column on Agent detail).
        Sql "IF DATABASE_PRINCIPAL_ID(N'CopilotInsightsApp') IS NOT NULL GRANT SELECT ON $q TO CopilotInsightsApp;" $null
        $loaded[$sheet] = $dt.Rows.Count
        Write-Host "$sheet -> $table`: $($dt.Rows.Count) row(s)."
    }

    # 2. The inventory the reports read, matched to the agents the audit log knows. A column the sheet lacks is NULL.
    $inv = $Sheets['AgentInventory']; $prod = $Sheets['AgentsInProd']; $pc = $Sheets['ParentChildAgent']
    if ($inv -and (Sql "SELECT CASE WHEN OBJECT_ID(N'$inv', N'U') IS NULL OR OBJECT_ID(N'dash.AgentInventory', N'U') IS NULL THEN 0 ELSE 1 END" $null -Scalar)) {
        function Columns($t) { $c = $conn.CreateCommand(); $c.CommandText = "SELECT name FROM sys.columns WHERE object_id = OBJECT_ID(N'$t')"; $r = $c.ExecuteReader(); $n = @(); while ($r.Read()) { $n += $r.GetString(0) }; $r.Dispose(); $n }
        $ic = Columns $inv
        function T($name) { if ($ic -contains $name) { "NULLIF(LTRIM(RTRIM(CAST(w.[$name] AS NVARCHAR(4000)))), N'')" } else { 'CAST(NULL AS NVARCHAR(4000))' } }
        function D($name) { "TRY_CONVERT(DATETIME2(0), $(T $name))" }
        function N($name) { "TRY_CONVERT(INT, TRY_CONVERT(FLOAT, $(T $name)))" }
        $inProd = if ($prod -and (Sql "SELECT CASE WHEN COL_LENGTH(N'$prod', N'AgentId') IS NULL THEN 0 ELSE 1 END" $null -Scalar)) {
            "CASE WHEN EXISTS (SELECT 1 FROM $prod AS p WHERE LTRIM(RTRIM(CAST(p.AgentId AS NVARCHAR(100)))) = x.Id) THEN 1 ELSE 0 END" } else { '0' }
        $pcCols = if ($pc -and (Sql "SELECT CASE WHEN OBJECT_ID(N'$pc', N'U') IS NULL OR OBJECT_ID(N'dash.AgentChildLink', N'U') IS NULL THEN 0 ELSE 1 END" $null -Scalar)) { Columns $pc }
        $tx = $conn.BeginTransaction()
        try {
            Sql @"
SELECT CAST(LEFT($(T 'AgentId'), 100) AS NVARCHAR(100)) AS Id, $(T 'AgentName') AS AgentName, $(T 'OwnerIdUPN') AS OwnerUpn,
       $(T 'EnvironmentDisplayName') AS Environment, $(T 'SchemaName') AS SchemaName, $(D 'CreatedAt') AS CreatedAt,
       $(D 'LastPublishedAt') AS LastPublishedAt, $(T 'Channels') AS Channels, $(N 'ViewerUsers') AS ViewerUsers,
       $(N 'ViewerGroups') AS ViewerGroups, $(N 'EditorUsers') AS EditorUsers, $(N 'EditorGroups') AS EditorGroups,
       $(T 'TitleId') AS TitleId, $(T 'EntraAgentId') AS EntraAgentId
INTO #w FROM $inv AS w;
DELETE FROM #w WHERE Id IS NULL;

/* The keys an audit identity offers: the schema name it ends in, the first GUID it holds, a title id. */
DECLARE @g NVARCHAR(200) = N'%' + REPLICATE(N'[0-9a-fA-F]', 8) + N'-' + REPLICATE(N'[0-9a-fA-F]', 4) + N'-' + REPLICATE(N'[0-9a-fA-F]', 4)
                         + N'-' + REPLICATE(N'[0-9a-fA-F]', 4) + N'-' + REPLICATE(N'[0-9a-fA-F]', 12) + N'%';
SELECT d.AgentKey, d.AgentId, i.DerivedName AS KSchema,
       CASE WHEN PATINDEX(@g, d.AgentId) > 0 THEN SUBSTRING(d.AgentId, PATINDEX(@g, d.AgentId), 36) END AS KGuid,
       CASE WHEN d.AgentId LIKE N'T[_]%' THEN LEFT(d.AgentId, 38) END AS KTitle
INTO #d FROM bi.DimAgent AS d CROSS APPLY bi.fnAgentIdentity(d.AgentId, d.AgentId) AS i WHERE d.AgentId <> N'(none)';

SELECT Id, AgentKey INTO #m
FROM (SELECT x.Id, x.AgentKey, ROW_NUMBER() OVER (PARTITION BY x.Id ORDER BY x.Rnk, x.AgentKey) AS rn
      FROM (SELECT w.Id, d.AgentKey, 1 AS Rnk FROM #w AS w JOIN #d AS d ON d.KSchema = w.SchemaName OR d.AgentId = w.SchemaName
            UNION ALL SELECT w.Id, d.AgentKey, 2 FROM #w AS w JOIN #d AS d ON d.KGuid = w.Id
            UNION ALL SELECT w.Id, d.AgentKey, 3 FROM #w AS w JOIN #d AS d ON d.AgentId = w.TitleId OR d.KTitle = w.TitleId
            UNION ALL SELECT w.Id, d.AgentKey, 4 FROM #w AS w JOIN #d AS d ON d.KGuid = w.EntraAgentId) AS x) AS y
WHERE rn = 1;

DELETE FROM dash.AgentInventory;
INSERT INTO dash.AgentInventory (InventoryAgentId, AgentKey, AgentName, OwnerUpn, Environment, SchemaName, CreatedAt, LastPublishedAt,
                                 Channels, ViewerUsers, ViewerGroups, EditorUsers, EditorGroups, InProduction)
SELECT x.Id, m.AgentKey, LEFT(x.AgentName, 400), LEFT(x.OwnerUpn, 256), LEFT(x.Environment, 400), LEFT(x.SchemaName, 400), x.CreatedAt,
       x.LastPublishedAt, LEFT(x.Channels, 400), x.ViewerUsers, x.ViewerGroups, x.EditorUsers, x.EditorGroups, $inProd
FROM (SELECT *, ROW_NUMBER() OVER (PARTITION BY Id ORDER BY LastPublishedAt DESC) AS rn FROM #w) AS x
LEFT JOIN #m AS m ON m.Id = x.Id
WHERE x.rn = 1;
"@ $tx
            if ($pcCols) {
                $ic = $pcCols
                Sql @"
DELETE FROM dash.AgentChildLink;
INSERT INTO dash.AgentChildLink (ChildAgentName, ParentInventoryAgentId, ParentAgentName, ParentAgentKey, RelationshipType, IsValid)
SELECT LEFT(c.Child, 400), LEFT(c.ParentId, 100), LEFT(COALESCE(c.ParentName, i.AgentName), 400), i.AgentKey, LEFT(c.Rel, 200),
       CASE WHEN c.Valid IN (N'Yes', N'True', N'1') THEN 1 WHEN c.Valid IN (N'No', N'False', N'0') THEN 0 END
FROM (SELECT COALESCE($(T 'ChildAgentName'), $(T 'ComponentName')) AS Child, $(T 'ParentAgentId') AS ParentId,
             $(T 'ParentAgentName') AS ParentName, $(T 'RelationshipType') AS Rel, $(T 'IsValidParentChildAgent') AS Valid
      FROM $pc AS w) AS c
LEFT JOIN dash.AgentInventory AS i ON i.InventoryAgentId = c.ParentId
WHERE c.Child IS NOT NULL;
"@ $tx
            }
            # 2.5.5 named the agents in Admin's own table; from 2.5.6 the inventory does.
            Sql "DELETE FROM dash.AgentAlias WHERE UpdatedBy = N'Agent workbook';" $tx
            $tx.Commit()
        }
        catch { $tx.Rollback(); throw }
        Sql "IF OBJECT_ID(N'bi.usp_NameAgents', N'P') IS NOT NULL EXEC bi.usp_NameAgents;" $null
        $matched = Sql 'SELECT COUNT(*) FROM dash.AgentInventory WHERE AgentKey IS NOT NULL' $null -Scalar
        $total = Sql 'SELECT COUNT(*) FROM dash.AgentInventory' $null -Scalar
        Write-Host "Agent inventory: $total agent(s), $matched of them in the audit log."
    }

    if ($runId) {
        Sql @"
UPDATE dash.AgentInventoryRun SET EndedUtc = SYSUTCDATETIME(), Status = N'Succeeded', Source = $(Q $source),
       FileCreatedUtc = $(if ($created) { "'" + $created.ToUniversalTime().ToString('yyyy-MM-dd HH:mm:ss') + "'" } else { 'NULL' }),
       InventoryRows = $(if ($null -ne $loaded['AgentInventory']) { $loaded['AgentInventory'] } else { 'NULL' }),
       ProductionRows = $(if ($null -ne $loaded['AgentsInProd']) { $loaded['AgentsInProd'] } else { 'NULL' }),
       ChildLinkRows = $(if ($null -ne $loaded['ParentChildAgent']) { $loaded['ParentChildAgent'] } else { 'NULL' }),
       Matched = $(if ($null -ne $matched) { $matched } else { 'NULL' })
WHERE RunId = $runId;
"@ $null
    }
}
catch {
    if ($runId) { Sql "UPDATE dash.AgentInventoryRun SET EndedUtc = SYSUTCDATETIME(), Status = N'Failed', Message = $(Q ([string] $_.Exception.Message).Substring(0, [Math]::Min(2000, ([string] $_.Exception.Message).Length))) WHERE RunId = $runId;" $null }
    throw
}
finally { $conn.Dispose(); Remove-Item -LiteralPath $file -ErrorAction SilentlyContinue }
