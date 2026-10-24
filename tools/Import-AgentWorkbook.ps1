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
       ends in, then the bot id, the title id or the Entra agent id it holds. An agent the audit log names two
       ways (2.6.3) becomes one: its second identity is marked in bi.DimAgent and bi.usp_MergeAgents moves its
       interactions to the agent.
    4. Names the agents in the report tables (bi.usp_NameAgents): the workbook's name, or the audit log's for an
       agent the workbook does not list or leaves unnamed.
    5. Records the load in dash.AgentInventoryRun, which Admin > Health shows.

    THE DELETE GUARD (2.6.5). Before anything is replaced, each sheet's rows are counted against its table's. When
    a sheet has more than -MaxDropPercent fewer rows than its table holds (AgentWorkbookMaxDropPercent in the
    settings file, 15 by default), the whole workbook is refused: no table changes, the run is recorded as failed
    with the reason, and the nightly run says so. 10,000 agents last night and 8,000 tonight is a 20% drop: refused
    at 15. If the drop is real, run this once with -AllowDrop. -MaxDropPercent 0 turns the guard off.

    With no -SiteUrl or -Url, everything comes from the settings file beside the scripts
    (copilot-audit.settings.psd1): AgentWorkbookSiteUrl, AgentWorkbookFolder, AgentWorkbookFilter,
    AgentWorkbookTenantId (else TenantId), AgentWorkbookClientId and AgentWorkbookCertificateThumbprint, or
    AgentWorkbookUrl; and SqlServer, Database and TrustServerCertificate.

    Reads .xlsx with the ImportExcel module: install it for the nightly task's account, or put it in a Modules
    folder beside the scripts, where it is found first. For SharePoint Online, PnP.PowerShell too (it needs
    PowerShell 7.4.6 or later).

.EXAMPLE
    .\tools\Import-AgentWorkbook.ps1          # as the nightly run does, from the settings file
.EXAMPLE
    .\tools\Import-AgentWorkbook.ps1 -Url '\\fileserver\Copilot\Agents.xlsx' -SqlServer SQLPROD01
.EXAMPLE
    .\tools\Import-AgentWorkbook.ps1 -AllowDrop        # tonight's workbook really has fewer agents: load it anyway
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
    [string] $ConnectionString,               # for the tests: used as it is, in place of -SqlServer and -Database
    [string] $SettingsPath = (Join-Path (Split-Path $PSScriptRoot -Parent) 'copilot-audit.settings.psd1'),
    [hashtable] $Sheets = @{ 'AgentInventory' = 'dbo.AgentInventory'; 'AgentsInProd' = 'dbo.AgentsInProd'; 'ParentChildAgent' = 'dbo.ParentChildAgent' },
    [ValidateRange(0, 100)][double] $MaxDropPercent = 15,   # the delete guard: the most a sheet may shrink, in %; 0 is off
    [switch] $AllowDrop                                      # load a workbook the guard would refuse, once
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'DropGuard.ps1')
$modules = Join-Path (Split-Path $PSScriptRoot -Parent) 'Modules'
if (Test-Path $modules) { $env:PSModulePath = "$modules;$env:PSModulePath" }

# What the command line does not give comes from the settings file.
$s = if (Test-Path -LiteralPath $SettingsPath) { Import-PowerShellDataFile -LiteralPath $SettingsPath } else { @{} }
$MaxDropPercent = Resolve-MaxDropPercent -What 'Agent workbook' -Given $MaxDropPercent -WasGiven $PSBoundParameters.ContainsKey('MaxDropPercent') -Settings $s
if (-not $SiteUrl -and -not $Url) {
    if (-not (Test-Path -LiteralPath $SettingsPath)) { throw "Give -SiteUrl or -Url, or put AgentWorkbookSiteUrl or AgentWorkbookUrl in $SettingsPath." }
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
if (-not $SqlServer -and -not $ConnectionString) { throw 'Give -SqlServer, or SqlServer in the settings file.' }
if ($SiteUrl -and -not ($ClientId -and $CertificateThumbprint -and $TenantId)) {
    throw 'SharePoint Online needs the workbook app: -TenantId, -ClientId and -CertificateThumbprint (AgentWorkbookClientId and AgentWorkbookCertificateThumbprint in the settings).'
}

$conn = [System.Data.SqlClient.SqlConnection]::new($(if ($ConnectionString) { $ConnectionString } else { "Server=$SqlServer;Database=$Database;Integrated Security=true;Encrypt=true;TrustServerCertificate=$([bool] $TrustServerCertificate)" }))
$conn.Open()
function Sql([string] $Text, $Tx, [switch] $Scalar) {
    $c = $conn.CreateCommand(); $c.CommandText = $Text; $c.Transaction = $Tx; $c.CommandTimeout = 0
    if ($Scalar) { $c.ExecuteScalar() } else { [void] $c.ExecuteNonQuery() }
}
function Q([string] $s) { if ($null -eq $s) { 'NULL' } else { "N'" + $s.Replace("'", "''") + "'" } }

# The load is recorded for Admin > Health, from its start, so a failure shows too.
$runId = Sql "IF OBJECT_ID(N'dash.AgentInventoryRun', N'U') IS NOT NULL BEGIN INSERT INTO dash.AgentInventoryRun DEFAULT VALUES; SELECT CAST(SCOPE_IDENTITY() AS INT); END" $null -Scalar
$file = Join-Path ([IO.Path]::GetTempPath()) "agent-workbook-$PID.xlsx"
$refused = $false
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

    # 1. Each sheet as it is, read whole before any table is touched.
    $read = [ordered]@{}
    foreach ($sheet in @($Sheets.Keys | Sort-Object)) {
        $table = $Sheets[$sheet]
        $rows = @(Import-Excel -Path $file -WorksheetName $sheet)
        if (-not $rows) { Write-Warning "Sheet $sheet is empty or missing: $table left as it is."; continue }
        $dt = [System.Data.DataTable]::new()
        $cols = @($rows[0].PSObject.Properties.Name)   # a list even for a sheet of one column, whose name alone would be a string
        foreach ($c in $cols) { [void] $dt.Columns.Add($c, [string]) }
        foreach ($r in $rows) {
            [void] $dt.Rows.Add(@($cols | ForEach-Object {
                $v = $r.$_
                if ($null -eq $v) { [DBNull]::Value } elseif ($v -is [datetime]) { $v.ToString('yyyy-MM-dd HH:mm:ss') } else { "$v" } }))
        }
        $read[$sheet] = [pscustomobject]@{ Table = $table; Data = $dt; Columns = $cols }
    }

    # 2. The delete guard: every sheet against what its table holds, before any is replaced.
    foreach ($sheet in $read.Keys) {
        $table = $read[$sheet].Table
        $q = ($table -split '\.' | ForEach-Object { "[$($_.Trim('[]'))]" }) -join '.'
        $before = [long] (Sql "IF OBJECT_ID(N'$table', N'U') IS NULL SELECT CAST(0 AS BIGINT); ELSE EXEC (N'SELECT COUNT_BIG(*) FROM $q;');" $null -Scalar)
        Assert-DropGuard -What "Sheet $sheet ($table)" -Before $before -After $read[$sheet].Data.Rows.Count -MaxDropPercent $MaxDropPercent `
                         -Setting 'AgentWorkbookMaxDropPercent' -AllowDrop:$AllowDrop
    }

    # 3. Each sheet into its own table.
    $loaded = @{}
    foreach ($sheet in $read.Keys) {
        $table = $read[$sheet].Table; $dt = $read[$sheet].Data; $cols = $read[$sheet].Columns
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

    # 4. The inventory the reports read, matched to the agents the audit log knows. A column the sheet lacks is NULL.
    $inv = $Sheets['AgentInventory']; $prod = $Sheets['AgentsInProd']; $pc = $Sheets['ParentChildAgent']
    if ($inv -and (Sql "SELECT CASE WHEN OBJECT_ID(N'$inv', N'U') IS NULL OR OBJECT_ID(N'dash.AgentInventory', N'U') IS NULL THEN 0 ELSE 1 END" $null -Scalar)) {
        function Columns($t) { $c = $conn.CreateCommand(); $c.CommandText = "SELECT name FROM sys.columns WHERE object_id = OBJECT_ID(N'$t')"; $r = $c.ExecuteReader(); $n = @(); while ($r.Read()) { $n += $r.GetString(0) }; $r.Dispose(); $n }
        $ic = Columns $inv
        function T($name) { if ($ic -contains $name) { "NULLIF(LTRIM(RTRIM(CAST(w.[$name] AS NVARCHAR(4000)))), N'')" } else { 'CAST(NULL AS NVARCHAR(4000))' } }
        function D($name) { "TRY_CONVERT(DATETIME2(0), $(T $name))" }
        function N($name) { "TRY_CONVERT(INT, TRY_CONVERT(FLOAT, $(T $name)))" }
        function B($name) { "CASE WHEN $(T $name) IN (N'True', N'Yes', N'1') THEN 1 WHEN $(T $name) IN (N'False', N'No', N'0') THEN 0 END" }
        $inProd = if ($prod -and (Sql "SELECT CASE WHEN COL_LENGTH(N'$prod', N'AgentId') IS NULL THEN 0 ELSE 1 END" $null -Scalar)) {
            "CASE WHEN EXISTS (SELECT 1 FROM $prod AS p WHERE LTRIM(RTRIM(CAST(p.AgentId AS NVARCHAR(100)))) = x.Id) THEN 1 ELSE 0 END" } else { '0' }
        $pcCols = if ($pc -and (Sql "SELECT CASE WHEN OBJECT_ID(N'$pc', N'U') IS NULL OR OBJECT_ID(N'dash.AgentChildLink', N'U') IS NULL THEN 0 ELSE 1 END" $null -Scalar)) { Columns $pc }
        if ($pcCols -and -not (Sql "SELECT CASE WHEN COL_LENGTH(N'dash.AgentChildLink', N'ChildKey') IS NULL THEN 0 ELSE 1 END" $null -Scalar)) {
            throw 'dash.AgentChildLink predates 2.5.7: run Install-CopilotAudit.ps1 -Dashboard from this release first.'
        }
        if (-not (Sql "SELECT CASE WHEN COL_LENGTH(N'dash.AgentInventory', N'IsAutonomous') IS NULL THEN 0 ELSE 1 END" $null -Scalar)) {
            throw 'dash.AgentInventory predates 2.5.8: run Install-CopilotAudit.ps1 -Dashboard from this release first.'
        }
        # Second identities are folded into their agent where bi.DimAgent can say so (a database upgraded to 2.6.3).
        $canMerge = [bool] (Sql "SELECT CASE WHEN COL_LENGTH(N'bi.DimAgent', N'MergedIntoKey') IS NULL THEN 0 ELSE 1 END" $null -Scalar)
        $mainKey = if ($canMerge) { 'COALESCE(d.MergedIntoKey, d.AgentKey)' } else { 'd.AgentKey' }
        $markMerged = if ($canMerge) { 'UPDATE d SET MergedIntoKey = g.MainKey FROM bi.DimAgent AS d JOIN #merge AS g ON g.OwnKey = d.AgentKey WHERE d.MergedIntoKey IS NULL;' } else { '' }
        $tx = $conn.BeginTransaction()
        try {
            Sql @"
SELECT CAST(LEFT($(T 'AgentId'), 100) AS NVARCHAR(100)) AS Id, $(T 'AgentName') AS AgentName, $(T 'OwnerIdUPN') AS OwnerUpn,
       $(T 'EnvironmentDisplayName') AS Environment, $(T 'SchemaName') AS SchemaName, $(D 'CreatedAt') AS CreatedAt,
       $(D 'LastPublishedAt') AS LastPublishedAt, $(T 'Channels') AS Channels, $(N 'ViewerUsers') AS ViewerUsers,
       $(N 'ViewerGroups') AS ViewerGroups, $(N 'EditorUsers') AS EditorUsers, $(N 'EditorGroups') AS EditorGroups,
       $(T 'TitleId') AS TitleId, $(T 'EntraAgentId') AS EntraAgentId, $(T 'EnvironmentId') AS EnvironmentId,
       $(T 'EnvironmentSku') AS EnvironmentSku, $(T 'Authentication') AS Authentication, $(B 'IsQuarantined') AS IsQuarantined,
       $(B 'IsWebSearchEnabled') AS IsWebSearchEnabled,
       CASE WHEN COALESCE($(T 'Triggers'), $(T 'Flows')) IS NOT NULL AND COALESCE($(T 'Triggers'), $(T 'Flows')) NOT IN (N'0', N'[]', N'None') THEN 1 ELSE 0 END AS IsAutonomous
INTO #w FROM $inv AS w;
DELETE FROM #w WHERE Id IS NULL;

/*
    2.5.7: how the audit log names an agent (bi.DimAgent.AgentId, its AppIdentity), and so how each agent is matched:
      1  Copilot Studio            Copilot.Studio.<EnvironmentId>-<SchemaName>  (the default environment's id is Default-<tenant>)
      2  Agent Builder, Studio lite Copilot.Studio.Declarative.<TitleId>         (T_<guid>; the T_ added where the workbook leaves it off)
      3  The same as 1, with the schema name Copilot Studio's own authoring records give the agent's bot id
         (audit.AgentEvent BotId -> BotSchemaName), where the workbook's SchemaName is not the one the agent runs under
      4  The schema name alone, at the end of a Copilot Studio identity, in any environment
      5  The bot id, or 6 the Entra agent id, anywhere in the identity (records keyed by AgentId alone)
    1-3 are exact. 4-6 are taken only where one agent of the workbook claims the audit log's agent: a schema name
    used in two environments matches neither. Microsoft's own agents (MicrosoftAgent.*) are not in the workbook.

    2.6.3: the audit log names some agents two ways (an agent made in Microsoft 365 Copilot is rule 1's identity on
    some records and rule 2's on others), and the workbook's one row holds both ids. An agent of the workbook keeps
    one link, the one it had, and every other identity it matches exactly (1-3) is marked as another name for that
    agent (bi.DimAgent.MergedIntoKey); bi.usp_MergeAgents then moves its interactions there. Marked only where no
    other agent of the workbook claims the identity, and never undone here. AgentKey below is the agent an identity
    stands for; OwnKey is the identity's own row.
*/
UPDATE #w SET TitleId = N'T_' + TitleId WHERE TitleId IS NOT NULL AND TitleId NOT LIKE N'T[_]%';
DECLARE @g NVARCHAR(200) = N'%' + REPLICATE(N'[0-9a-fA-F]', 8) + N'-' + REPLICATE(N'[0-9a-fA-F]', 4) + N'-' + REPLICATE(N'[0-9a-fA-F]', 4)
                         + N'-' + REPLICATE(N'[0-9a-fA-F]', 4) + N'-' + REPLICATE(N'[0-9a-fA-F]', 12) + N'%';
SELECT $mainKey AS AgentKey, d.AgentKey AS OwnKey, d.AgentId, i.DerivedName AS KSchema,
       CASE WHEN PATINDEX(@g, d.AgentId) > 0 THEN SUBSTRING(d.AgentId, PATINDEX(@g, d.AgentId), 36) END AS KGuid,
       CAST(CASE WHEN d.AgentId LIKE N'Copilot.Studio.%' AND d.AgentId NOT LIKE N'Copilot.Studio.Declarative.%' THEN 1 ELSE 0 END AS BIT) AS Studio
INTO #d FROM bi.DimAgent AS d CROSS APPLY bi.fnAgentIdentity(d.AgentId, d.AgentId) AS i WHERE d.AgentId <> N'(none)';
CREATE INDEX IX_d ON #d (AgentId);

CREATE TABLE #b (BotId NVARCHAR(100) COLLATE DATABASE_DEFAULT NOT NULL, SchemaName NVARCHAR(400) COLLATE DATABASE_DEFAULT NOT NULL);
IF COL_LENGTH(N'audit.AgentEvent', N'BotSchemaName') IS NOT NULL
    EXEC (N'INSERT INTO #b (BotId, SchemaName) SELECT DISTINCT LEFT(CAST(BotId AS NVARCHAR(100)), 100), BotSchemaName
            FROM audit.AgentEvent WHERE BotId IS NOT NULL AND NULLIF(BotSchemaName, N'''') IS NOT NULL;');

SELECT x.Id, x.AgentKey, x.OwnKey, MIN(x.Rnk) AS Rnk INTO #cx
FROM (SELECT w.Id, d.AgentKey, d.OwnKey, 1 AS Rnk FROM #w AS w JOIN #d AS d ON d.AgentId = N'Copilot.Studio.' + w.EnvironmentId + N'-' + w.SchemaName
      UNION ALL SELECT w.Id, d.AgentKey, d.OwnKey, 2 FROM #w AS w JOIN #d AS d ON d.AgentId = N'Copilot.Studio.Declarative.' + w.TitleId OR d.AgentId = w.TitleId
      UNION ALL SELECT w.Id, d.AgentKey, d.OwnKey, 3 FROM #w AS w JOIN #b AS b ON b.BotId = w.Id
                JOIN #d AS d ON d.AgentId = N'Copilot.Studio.' + w.EnvironmentId + N'-' + b.SchemaName
      UNION ALL SELECT w.Id, d.AgentKey, d.OwnKey, 4 FROM #w AS w JOIN #d AS d ON d.Studio = 1 AND d.KSchema = w.SchemaName
      UNION ALL SELECT w.Id, d.AgentKey, d.OwnKey, 4 FROM #w AS w JOIN #d AS d ON d.Studio = 1 AND w.SchemaName LIKE N'%-%'
                AND RIGHT(d.AgentId, LEN(w.SchemaName) + 1) = N'-' + w.SchemaName
      UNION ALL SELECT w.Id, d.AgentKey, d.OwnKey, 4 FROM #w AS w JOIN #b AS b ON b.BotId = w.Id JOIN #d AS d ON d.Studio = 1 AND d.KSchema = b.SchemaName
      UNION ALL SELECT w.Id, d.AgentKey, d.OwnKey, 5 FROM #w AS w JOIN #d AS d ON d.KGuid = w.Id OR d.AgentId = w.Id
      UNION ALL SELECT w.Id, d.AgentKey, d.OwnKey, 6 FROM #w AS w JOIN #d AS d ON d.KGuid = w.EntraAgentId OR d.AgentId = w.EntraAgentId) AS x
GROUP BY x.Id, x.AgentKey, x.OwnKey;
SELECT Id, AgentKey, MIN(Rnk) AS Rnk INTO #c FROM #cx GROUP BY Id, AgentKey;

/* Each workbook agent's best match, the exact one it was linked to before coming first, so an agent keeps its key
   when a second identity of it turns up; then each audit agent's best claimants, kept unless a loose match is shared. */
SELECT Id, AgentKey, Rnk INTO #p
FROM (SELECT c.Id, c.AgentKey, c.Rnk,
             ROW_NUMBER() OVER (PARTITION BY c.Id ORDER BY CASE WHEN c.Rnk <= 3 AND prev.AgentKey = c.AgentKey THEN 0 ELSE 1 END, c.Rnk, c.AgentKey) AS rn
      FROM #c AS c LEFT JOIN dash.AgentInventory AS prev ON prev.InventoryAgentId = c.Id) AS y WHERE rn = 1;
SELECT p.Id, p.AgentKey, p.Rnk INTO #m
FROM #p AS p
JOIN (SELECT AgentKey, MIN(Rnk) AS Best FROM #p GROUP BY AgentKey) AS k ON k.AgentKey = p.AgentKey AND p.Rnk = k.Best
WHERE p.Rnk <= 3 OR (SELECT COUNT(*) FROM #p AS q WHERE q.AgentKey = p.AgentKey AND q.Rnk = p.Rnk) = 1;

/* 2.6.3: the other identities of each linked agent. */
SELECT x.OwnKey, MIN(m.AgentKey) AS MainKey INTO #merge
FROM #cx AS x JOIN #m AS m ON m.Id = x.Id
WHERE x.Rnk <= 3 AND m.Rnk <= 3 AND x.AgentKey <> m.AgentKey AND x.AgentKey = x.OwnKey
  AND NOT EXISTS (SELECT 1 FROM #m AS o WHERE o.AgentKey = x.OwnKey)
  AND (SELECT COUNT(DISTINCT y.Id) FROM #cx AS y WHERE y.OwnKey = x.OwnKey AND y.Rnk <= 3) = 1
GROUP BY x.OwnKey;
$markMerged

DELETE FROM dash.AgentInventory;
INSERT INTO dash.AgentInventory (InventoryAgentId, AgentKey, AgentName, OwnerUpn, Environment, SchemaName, CreatedAt, LastPublishedAt,
                                 Channels, ViewerUsers, ViewerGroups, EditorUsers, EditorGroups, InProduction,
                                 EnvironmentSku, Authentication, IsQuarantined, IsWebSearchEnabled, IsAutonomous)
SELECT x.Id, m.AgentKey, LEFT(x.AgentName, 400), LEFT(x.OwnerUpn, 256), LEFT(x.Environment, 400), LEFT(x.SchemaName, 400), x.CreatedAt,
       x.LastPublishedAt, LEFT(x.Channels, 400), x.ViewerUsers, x.ViewerGroups, x.EditorUsers, x.EditorGroups, $inProd,
       LEFT(x.EnvironmentSku, 100), LEFT(x.Authentication, 200), x.IsQuarantined, x.IsWebSearchEnabled, x.IsAutonomous
FROM (SELECT *, ROW_NUMBER() OVER (PARTITION BY Id ORDER BY LastPublishedAt DESC) AS rn FROM #w) AS x
LEFT JOIN #m AS m ON m.Id = x.Id
WHERE x.rn = 1;

/* 2.5.7: agents of one name are told apart by their environment, and where they share that too, by their schema
   name: "HR Assistant (Sales Dev)". The name the workbook gives stays in its own table. */
WITH n AS (SELECT InventoryAgentId, AgentName, Environment, SchemaName,
                  COUNT(*) OVER (PARTITION BY LTRIM(RTRIM(AgentName))) AS SameName,
                  COUNT(*) OVER (PARTITION BY LTRIM(RTRIM(AgentName)), ISNULL(Environment, N'')) AS SameNameAndPlace
           FROM dash.AgentInventory WHERE NULLIF(LTRIM(RTRIM(AgentName)), N'') IS NOT NULL)
UPDATE n SET AgentName = LEFT(CONCAT(LTRIM(RTRIM(AgentName)), N' (', ISNULL(Environment, N'no environment'),
                                     CASE WHEN SameNameAndPlace > 1 THEN CONCAT(N', ', ISNULL(SchemaName, InventoryAgentId)) END, N')'), 400)
WHERE SameName > 1;
"@ $tx
            if ($pcCols) {
                $ic = $pcCols
                Sql @"
/* 2.5.7: each child by what it is, not by its name, which Copilot Studio gives every new child agent ("Agent").
   A child agent (RelationshipType without "Connected") lives inside its one parent: its component id. A connected
   agent is an agent of its own that several parents may call: its row in the inventory, found by its environment
   and the schema name SchemaName ends in (parent.InvokeConnectedAgentTaskAction.child), else that pair. */
SELECT COALESCE($(T 'ChildAgentName'), $(T 'ComponentName')) AS Child, $(T 'ParentAgentId') AS ParentId,
       $(T 'ParentAgentName') AS ParentName, $(T 'RelationshipType') AS Rel, $(T 'IsValidParentChildAgent') AS Valid,
       $(T 'ComponentId') AS CompId, $(T 'EnvironmentId') AS EnvId, $(T 'SchemaName') AS FullSchema, $(T 'EnvironmentSku') AS EnvSku
INTO #pc FROM $pc AS w;
DELETE FROM #pc WHERE Child IS NULL;

/* The parent's environment by name: the inventory's, else the sheet's SKU and the start of its id, between them a
   middle dot, NCHAR(183) ("Developer . 015d2539"), never a bare GUID. The script stays ASCII, as the analyzer asks. */
SELECT p.*, CASE WHEN p.Rel LIKE N'%connected%' THEN N'Connected' ELSE N'Child' END AS Kind,
       CASE WHEN p.FullSchema LIKE N'%.%' THEN RIGHT(p.FullSchema, CHARINDEX(N'.', REVERSE(p.FullSchema)) - 1) END AS ChildSchema,
       i.AgentKey AS ParentKey, COALESCE(NULLIF(i.AgentName, N''), p.ParentName, p.ParentId) AS ParentLabel,
       COALESCE(NULLIF(i.Environment, N''),
                NULLIF(CONCAT(p.EnvSku, CASE WHEN p.EnvSku IS NOT NULL AND p.EnvId IS NOT NULL THEN N' ' + NCHAR(183) + N' ' END, LEFT(p.EnvId, 8)), N''),
                N'no environment') AS ParentEnv
INTO #pk FROM #pc AS p LEFT JOIN dash.AgentInventory AS i ON i.InventoryAgentId = p.ParentId;
/* Parents of one name that are different agents (one Advisor in a developer and one in a production environment) are told apart
   by their environment, where the inventory has not already done so. */
UPDATE k SET ParentLabel = LEFT(CONCAT(k.ParentLabel, N' (', k.ParentEnv, N')'), 400)
FROM #pk AS k
WHERE k.ParentLabel IN (SELECT ParentLabel FROM #pk GROUP BY ParentLabel HAVING COUNT(DISTINCT ISNULL(ParentId, N'')) > 1);

SELECT k.*, ci.Id AS ChildInv,
       CASE WHEN k.Kind = N'Connected' THEN COALESCE(N'agent:' + ci.Id, N'connected:' + ISNULL(k.EnvId, N'') + N'/' + COALESCE(k.ChildSchema, k.Child))
            ELSE N'child:' + COALESCE(k.CompId, ISNULL(k.ParentId, N'') + N'/' + COALESCE(k.ChildSchema, k.Child)) END AS ChildKey
INTO #pl
FROM #pk AS k
OUTER APPLY (SELECT TOP (1) w.Id FROM #w AS w WHERE k.Kind = N'Connected' AND w.EnvironmentId = k.EnvId AND w.SchemaName = k.ChildSchema) AS ci;

/* One name for each child, told apart where children share one: a child agent by its parent, a connected agent by
   its environment, and where that is not enough, by its component id or schema name. */
SELECT l.ChildKey, MIN(COALESCE(NULLIF(i.AgentName, N''), l.Child)) AS BaseName, MIN(l.Kind) AS Kind,
       MIN(l.ParentLabel) AS ParentLabel, MIN(l.ParentEnv) AS ParentEnv, MIN(COALESCE(l.CompId, l.ChildSchema, l.ChildKey)) AS Extra,
       CAST(NULL AS NVARCHAR(400)) AS Display
INTO #ck FROM #pl AS l LEFT JOIN dash.AgentInventory AS i ON i.InventoryAgentId = l.ChildInv GROUP BY l.ChildKey;
WITH n AS (SELECT Display, BaseName, Kind, ParentLabel, ParentEnv, COUNT(*) OVER (PARTITION BY BaseName) AS SameName FROM #ck)
UPDATE n SET Display = LEFT(CASE WHEN SameName = 1 THEN BaseName
                                 WHEN Kind = N'Child' THEN CONCAT(BaseName, N' (in ', ParentLabel, N')')
                                 ELSE CONCAT(BaseName, N' (', ISNULL(ParentEnv, N'no environment'), N')') END, 400);
WITH n AS (SELECT Display, Extra, COUNT(*) OVER (PARTITION BY Display) AS SameDisplay FROM #ck)
UPDATE n SET Display = LEFT(CONCAT(Display, N' ' + NCHAR(183) + N' ', Extra), 400) WHERE SameDisplay > 1;

DELETE FROM dash.AgentChildLink;
INSERT INTO dash.AgentChildLink (ChildAgentName, ParentInventoryAgentId, ParentAgentName, ParentAgentKey, RelationshipType, IsValid,
                                 ChildKey, ChildKind, ChildInventoryAgentId, ParentEnvironment)
SELECT c.Display, LEFT(l.ParentId, 100), LEFT(l.ParentLabel, 400), l.ParentKey, LEFT(l.Rel, 200),
       CASE WHEN l.Valid IN (N'Yes', N'True', N'1') THEN 1 WHEN l.Valid IN (N'No', N'False', N'0') THEN 0 END,
       LEFT(l.ChildKey, 450), l.Kind, l.ChildInv, LEFT(l.ParentEnv, 400)
FROM #pl AS l JOIN #ck AS c ON c.ChildKey = l.ChildKey;
"@ $tx
            }
            # 2.5.5 named the agents in Admin's own table; from 2.5.6 the inventory does.
            Sql "DELETE FROM dash.AgentAlias WHERE UpdatedBy = N'Agent workbook';" $tx
            $tx.Commit()
        }
        catch { $tx.Rollback(); throw }
        if ($canMerge) { Sql "IF OBJECT_ID(N'bi.usp_MergeAgents', N'P') IS NOT NULL EXEC bi.usp_MergeAgents;" $null }
        Sql "IF OBJECT_ID(N'bi.usp_NameAgents', N'P') IS NOT NULL EXEC bi.usp_NameAgents;" $null
        $matched = Sql 'SELECT COUNT(*) FROM dash.AgentInventory WHERE AgentKey IS NOT NULL' $null -Scalar
        $total = Sql 'SELECT COUNT(*) FROM dash.AgentInventory' $null -Scalar
        Write-Host "Agent inventory: $total agent(s), $matched of them in the audit log."
        # How each was matched, for checking the rules against the data.
        $how = Sql @"
SELECT STRING_AGG(CONCAT(CASE Rnk WHEN 1 THEN N'environment and schema name' WHEN 2 THEN N'title id (declarative)'
                                  WHEN 3 THEN N'schema name from Copilot Studio''s authoring records' WHEN 4 THEN N'schema name alone'
                                  WHEN 5 THEN N'bot id' ELSE N'Entra agent id' END, N' ', FORMAT(n, 'N0')), N'; ') WITHIN GROUP (ORDER BY Rnk)
FROM (SELECT Rnk, COUNT(*) AS n FROM #m GROUP BY Rnk) AS x;
"@ $null -Scalar
        if ($how -and $how -isnot [DBNull]) { Write-Host "  matched by: $how." }
        $loose = Sql "SELECT COUNT(DISTINCT p.Id) FROM #p AS p WHERE NOT EXISTS (SELECT 1 FROM #m AS m WHERE m.Id = p.Id);" $null -Scalar
        if ($loose) { Write-Host "  $loose agent(s) left unmatched: their schema name is in more than one environment, or another agent of the workbook claims the same one." }
        if ($canMerge) {
            $folded = Sql 'SELECT COUNT(*) FROM #merge' $null -Scalar
            $second = Sql 'SELECT COUNT(*) FROM bi.DimAgent WHERE MergedIntoKey IS NOT NULL' $null -Scalar
            if ($second) { Write-Host "  $second second identit$(if ($second -eq 1) { 'y' } else { 'ies' }) of an agent the audit log names two ways $(if ($second -eq 1) { 'is' } else { 'are' }) counted with the agent ($folded found in this workbook)." }
        }
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
    $refused = Test-DropRefusal $_
    if (-not $refused) { throw }
    # Written as the rest of the run is, so the nightly log has it (a warning from the step's process is not kept there).
    Write-Host "Refused: $($_.Exception.Message)"
}
finally { $conn.Dispose(); Remove-Item -LiteralPath $file -ErrorAction SilentlyContinue }
# Refused by the delete guard: every table kept the last workbook, and the nightly run goes on.
if ($refused) { exit $script:DropRefusedExitCode }
