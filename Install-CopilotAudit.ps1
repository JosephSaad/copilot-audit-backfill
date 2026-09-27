<#
.SYNOPSIS
    Sets up, upgrades and builds the Copilot audit databases in one command.

.DESCRIPTION
    Run it once before the first backfill, and again after it: it does what the
    state of the databases calls for, and is safe to run any number of times.

      1. Creates the Copilot and licence-event databases if they do not exist
         (compatibility level 160 or the server's highest below it, SIMPLE
         recovery so the bulk loads are minimally logged), and with
         -ServiceAccount gives the nightly job's account a login and db_owner
         in both.
      2. Installs or upgrades every table, view and procedure, in the order
         they depend on each other. Nothing is dropped; data is kept.
      3. Builds whatever needs building from the data already loaded:
           * the licence events shredded, once the licence database has records;
           * every record flattened, the first time there are records to
             flatten - and again once after an upgrade that adds columns (2.2.0),
             so history gets them too;
           * sessions and the bi schema, in full, the first time or after that
             re-flatten; otherwise nothing: the nightly -Incremental run keeps
             them current;
           * the dashboard's org chart and rollups, when -Dashboard is given.
      4. Reports what it did and what is left to do.

    Nothing here is typed into SQL by hand: settings are parameters, and
    day-to-day tasks are tools\Invoke-CopilotMaintenance.ps1.

.PARAMETER Dashboard
    Also install the Copilot Insights dashboard's schema and build its rollups.
    The site itself is installed on the web server with
    dashboard\Install-CopilotInsights.ps1.

.PARAMETER OrgTopUpn
    The top of the organisation for the dashboard (usually the CEO). Leave it
    out and the dashboard uses the one person the org chart has most people
    under, or keeps what an administrator already set.

.PARAMETER ReportingTimeZone
    The zone days and Saturday-to-Friday weeks are cut in: a name from
    sys.time_zone_info, such as 'GMT Standard Time'. Default Eastern Standard
    Time. Stored in both databases, where every procedure reads it; set it
    before the first load, as changing it later would leave history cut in
    the old zone (it is refused then).

.PARAMETER Rebuild
    Rebuild the flattened table, sessions, bi and the dashboard in full even
    if nothing calls for it.

.EXAMPLE
    .\Install-CopilotAudit.ps1 -SqlServer SQLPROD01
.EXAMPLE
    .\Install-CopilotAudit.ps1 -SqlServer SQLPROD01 -Dashboard -LicenceEventDatabases CopilotLicenseEvents,CopilotLicenseLive
#>
#Requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $SqlServer,
    [string] $Database = 'CopilotAudit',
    [string] $LicenceDatabase = 'CopilotLicenseEvents',
    # Databases holding licence events, for licence at the time of use (the backfill's -SessionLicenceDatabase). Defaults to -LicenceDatabase.
    [string[]] $LicenceEventDatabases,
    [pscredential] $SqlCredential,
    [switch] $TrustServerCertificate,
    [switch] $Dashboard,
    [string] $OrgTopUpn,
    [switch] $Rebuild,
    [string] $ReportingTimeZone,
    [switch] $SkipLicenceDatabase,
    # The account the nightly job runs as (DOMAIN\gmsa$ or DOMAIN\user): given a login and db_owner in both databases.
    [string] $ServiceAccount,
    # Accounts that read the bi views and nothing else, such as the Power BI gateway's (DOMAIN\name).
    [string[]] $ReportingAccount = @()
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$sqlDir = Join-Path $PSScriptRoot 'sql'
if (-not $LicenceEventDatabases) { $LicenceEventDatabases = if ($SkipLicenceDatabase) { @() } else { @($LicenceDatabase) } }
$licList = if ($LicenceEventDatabases) { @($LicenceEventDatabases) -join ',' } else { $null }

function Write-Step([string] $Text) { Write-Host "==> $Text" -ForegroundColor Cyan }
function Write-Done([string] $Text) { Write-Host "    $Text" }

function New-ConnString([string] $Catalog) {
    $b = New-Object System.Data.SqlClient.SqlConnectionStringBuilder
    $b['Data Source'] = $SqlServer
    $b['Initial Catalog'] = $Catalog
    if ($SqlCredential) { $b['User ID'] = $SqlCredential.UserName; $b['Password'] = $SqlCredential.GetNetworkCredential().Password }
    else { $b['Integrated Security'] = $true }
    $b['Encrypt'] = $true
    $b['TrustServerCertificate'] = [bool] $TrustServerCertificate
    $b['Application Name'] = 'Install-CopilotAudit'
    return $b.ConnectionString
}

function Invoke-Sql {
    param([string] $Catalog, [string] $Sql, [hashtable] $Parameters = @{}, [switch] $Scalar, [int] $Timeout = 0)
    $conn = New-Object System.Data.SqlClient.SqlConnection (New-ConnString $Catalog)
    $conn.add_InfoMessage({ param($s, $e) foreach ($m in $e.Errors) { if ($m.Class -le 10 -and $m.Message -notlike 'Warning: Null value*' -and $m.Message -notlike 'Warning! The maximum key length*') { Write-Host "      $($m.Message)" -ForegroundColor DarkGray } } })
    $conn.Open()
    try {
        $cmd = $conn.CreateCommand(); $cmd.CommandText = $Sql; $cmd.CommandTimeout = $Timeout
        foreach ($k in $Parameters.Keys) { [void] $cmd.Parameters.AddWithValue($k, $(if ($null -eq $Parameters[$k]) { [DBNull]::Value } else { $Parameters[$k] })) }
        if ($Scalar) { return $cmd.ExecuteScalar() }
        [void] $cmd.ExecuteNonQuery()
    }
    finally { $conn.Dispose() }
}

function Invoke-SqlFile([string] $Catalog, [string] $Name) {
    $text = Get-Content -LiteralPath (Join-Path $sqlDir $Name) -Raw
    # Batches split on GO alone on a line, as sqlcmd does.
    foreach ($batch in [regex]::Split($text, '(?im)^[ \t]*GO[ \t]*(?:--[^\r\n]*)?\r?$')) {
        if ($batch.Trim()) { Invoke-Sql -Catalog $Catalog -Sql $batch }
    }
    Write-Done $Name
}

function Test-Object([string] $Catalog, [string] $Name, [string] $Type = 'U') {
    [bool] (Invoke-Sql -Catalog $Catalog -Scalar -Sql "SELECT CASE WHEN OBJECT_ID(@n, @t) IS NULL THEN 0 ELSE 1 END" -Parameters @{ '@n' = $Name; '@t' = $Type })
}
function Get-Count([string] $Catalog, [string] $Table) {
    if (-not (Test-Object $Catalog $Table)) { return [long] 0 }
    [long] (Invoke-Sql -Catalog $Catalog -Scalar -Sql "SELECT SUM(row_count) FROM sys.dm_db_partition_stats WHERE object_id = OBJECT_ID(@t) AND index_id IN (0, 1)" -Parameters @{ '@t' = $Table })
}

# ---- 1. Databases -------------------------------------------------------------------------------
Write-Step "Databases on $SqlServer"
$targetLevel = [int] (Invoke-Sql -Catalog master -Scalar -Sql "SELECT CASE WHEN CAST(SERVERPROPERTY('ProductMajorVersion') AS INT) >= 16 THEN 160 ELSE CAST(SERVERPROPERTY('ProductMajorVersion') AS INT) * 10 END")
if ($targetLevel -lt 140) { throw 'SQL Server 2017 or later is needed.' }
$dbs = @($Database) + $(if ($SkipLicenceDatabase) { @() } else { @($LicenceDatabase) })
foreach ($db in $dbs) {
    $exists = [bool] (Invoke-Sql -Catalog master -Scalar -Sql 'SELECT CASE WHEN DB_ID(@d) IS NULL THEN 0 ELSE 1 END' -Parameters @{ '@d' = $db })
    if (-not $exists) {
        Invoke-Sql -Catalog master -Sql "CREATE DATABASE [$($db.Replace(']', ']]'))]; ALTER DATABASE [$($db.Replace(']', ']]'))] SET RECOVERY SIMPLE;"
        Write-Done "created $db (SIMPLE recovery)"
    }
    $level = [int] (Invoke-Sql -Catalog master -Scalar -Sql 'SELECT compatibility_level FROM sys.databases WHERE name = @d' -Parameters @{ '@d' = $db })
    if ($level -lt 130) {
        Invoke-Sql -Catalog master -Sql "ALTER DATABASE [$($db.Replace(']', ']]'))] SET COMPATIBILITY_LEVEL = $targetLevel;"
        Write-Done "$db compatibility level $level -> $targetLevel"
    }
    elseif ($exists) { Write-Done "$db exists (level $level)" }
}

if ($ServiceAccount) {
    $sa = $ServiceAccount.Replace(']', ']]')
    Invoke-Sql -Catalog master -Sql "IF SUSER_ID(@a) IS NULL EXEC (N'CREATE LOGIN [$sa] FROM WINDOWS');" -Parameters @{ '@a' = $ServiceAccount }
    foreach ($db in $dbs) {
        Invoke-Sql -Catalog $db -Parameters @{ '@a' = $ServiceAccount } -Sql @"
IF USER_ID(@a) IS NULL EXEC (N'CREATE USER [$sa] FOR LOGIN [$sa]');
IF IS_ROLEMEMBER(N'db_owner', @a) = 0 EXEC (N'ALTER ROLE db_owner ADD MEMBER [$sa]');
"@
    }
    Write-Done "$ServiceAccount has a login and db_owner in $($dbs -join ' and ')"
}

# ---- 2. Objects ---------------------------------------------------------------------------------
Write-Step "Tables, views and procedures in $Database"
$copilotScripts = 'Create-CopilotAuditSchema.sql', 'Create-CopilotLoadProcedure.sql',
                  'Create-CopilotFlattenedSchema.sql', 'Create-CopilotFlattenProcedure.sql', 'Create-CopilotPurgeProcedure.sql',
                  'Create-CopilotEnrichmentSchema.sql', 'Create-CopilotSessionSchema.sql', 'Create-CopilotSessionProcedure.sql',
                  'Create-CopilotBiSchema.sql', 'Create-CopilotBiProcedure.sql'
if ($Dashboard) { $copilotScripts += 'Create-CopilotDashboardSchema.sql', 'Create-CopilotDashboardProcedure.sql', 'Create-CopilotOperationsSchema.sql' }
foreach ($s in $copilotScripts) { Invoke-SqlFile $Database $s }
if (-not $SkipLicenceDatabase) {
    Write-Step "Tables, views and procedures in $LicenceDatabase"
    foreach ($s in 'Create-CopilotAuditSchema.sql', 'Create-CopilotLoadProcedure.sql', 'Create-LicenseEventSchema.sql', 'Create-LicenseEventProcedure.sql', 'Create-AgentEventProcedure.sql') { Invoke-SqlFile $LicenceDatabase $s }
}

# The reporting time zone, stored where every procedure reads it (audit.ReportingTimeZone()).
$zoneDbs = @($Database) + $(if ($SkipLicenceDatabase) { @() } else { @($LicenceDatabase) })
if ($ReportingTimeZone) {
    $known = [int] (Invoke-Sql -Catalog master -Scalar -Sql 'SELECT COUNT(*) FROM sys.time_zone_info WHERE name = @z' -Parameters @{ '@z' = $ReportingTimeZone })
    if (-not $known) { throw "'$ReportingTimeZone' is not a time zone SQL Server knows. SELECT name FROM sys.time_zone_info lists them." }
    foreach ($db in $zoneDbs) {
        $current = [string] (Invoke-Sql -Catalog $db -Scalar -Sql 'SELECT audit.ReportingTimeZone();')
        if ($current -eq $ReportingTimeZone) { continue }
        if ((Get-Count $db 'audit.CopilotAudit') -gt 0 -or (Get-Count $db 'audit.CopilotInteraction') -gt 0) {
            throw "$db already holds records cut in $current. The reporting time zone is set before the first load; to change it, load into new databases."
        }
        Invoke-Sql -Catalog $db -Parameters @{ '@z' = $ReportingTimeZone } -Sql @"
MERGE audit.PipelineSetting AS t USING (SELECT N'ReportingTimeZone' AS Name, @z AS Value) AS s ON t.Name = s.Name
WHEN MATCHED THEN UPDATE SET Value = s.Value, UpdatedUtc = SYSUTCDATETIME(), UpdatedBy = SUSER_SNAME()
WHEN NOT MATCHED THEN INSERT (Name, Value) VALUES (s.Name, s.Value);
"@
    }
}
Write-Done ("reporting time zone: {0}" -f (Invoke-Sql -Catalog $Database -Scalar -Sql 'SELECT audit.ReportingTimeZone();'))

foreach ($acct in $ReportingAccount) {
    $ra = $acct.Replace(']', ']]')
    Invoke-Sql -Catalog master -Sql "IF SUSER_ID(@a) IS NULL EXEC (N'CREATE LOGIN [$ra] FROM WINDOWS');" -Parameters @{ '@a' = $acct }
    Invoke-Sql -Catalog $Database -Parameters @{ '@a' = $acct } -Sql "IF USER_ID(@a) IS NULL EXEC (N'CREATE USER [$ra] FOR LOGIN [$ra]'); EXEC (N'GRANT SELECT ON SCHEMA::bi TO [$ra]');"
    Write-Done "$acct can read the bi views in $Database"
}

if ($Dashboard -and $OrgTopUpn) {
    Invoke-Sql -Catalog $Database -Parameters @{ '@v' = $OrgTopUpn.Trim(); '@by' = "$env:USERDOMAIN\$env:USERNAME" } -Sql @"
UPDATE dash.Setting SET Value = @v, UpdatedBy = @by, UpdatedUtc = SYSUTCDATETIME() WHERE Name = 'OrgTopUpn';
"@
    Write-Done "top of the organisation: $OrgTopUpn"
}

# ---- 3. Build what needs building ---------------------------------------------------------------
Write-Step 'Building from the data loaded so far'
$records = Get-Count $Database 'audit.CopilotAudit'
$flattened = Get-Count $Database 'audit.RawLogFlattened'
$todo = @()

if (-not $SkipLicenceDatabase) {
    $licRecords = Get-Count $LicenceDatabase 'audit.CopilotAudit'
    if ($licRecords -gt 0) {
        Write-Done ("licence events: shredding {0:N0} record(s)..." -f $licRecords)
        Invoke-Sql -Catalog $LicenceDatabase -Sql 'EXEC audit.usp_ShredLicenseEvents;'
        Invoke-Sql -Catalog $LicenceDatabase -Sql 'EXEC audit.usp_ShredAgentEvents;'
    }
    else { $todo += "Backfill the licence history into $LicenceDatabase (deployment step 7), then run this again." }
}

# Flattened by a version before 2.2.0: rows, but no record shapes - only the 2.2.0
# flatten counts them. Upgraded scripts alone do not fill history; this does, once.
$upgrade = $flattened -gt 0 -and (Get-Count $Database 'audit.RawLogRecordShape') -eq 0
$fullBuild = $Rebuild -or ($records -gt 0 -and $flattened -eq 0) -or $upgrade
if ($records -eq 0) {
    $todo += "Load Copilot records into $Database (the backfill, deployment step 6, or a CSV import), then run this again."
}
elseif ($fullBuild) {
    $why = if ($upgrade) { 'the flattened table gained columns (2.2.0), so every record is flattened again' } elseif ($Rebuild) { '-Rebuild' } else { 'first build' }
    Write-Done ("flattening {0:N0} record(s) ({1})..." -f $records, $why)
    Invoke-Sql -Catalog $Database -Sql 'EXEC audit.usp_FlattenCopilotAudit;'
    Write-Done 'sessions, in full...'
    Invoke-Sql -Catalog $Database -Sql 'EXEC audit.usp_BuildCopilotSessions @LicenceEventDatabase = @l;' -Parameters @{ '@l' = $licList }
    Write-Done 'bi, in full...'
    Invoke-Sql -Catalog $Database -Sql 'EXEC bi.usp_RefreshBi @LicenceEventDatabase = @l;' -Parameters @{ '@l' = $licList }
    if ($upgrade) { $todo += 'Refresh the Power BI model in full once, and add the new bi views (AccessedResources, LicenseTypes, SensitivityLabels, ResourceTypes, ResourceActions, Sites) if you want them.' }
}
else { Write-Done 'sessions and bi are built; the nightly -Incremental run keeps them current.' }

if ($Dashboard -and (Test-Object $Database 'bi.FactInteraction')) {
    $since = if ($fullBuild -or (Get-Count $Database 'dash.UserDay') -eq 0) { $null } else { [datetime]::UtcNow.Date }
    Write-Done $(if ($since) { 'dashboard: org chart, and today''s rollups...' } else { 'dashboard: org chart and every day''s rollups...' })
    Invoke-Sql -Catalog $Database -Sql 'EXEC dash.usp_RefreshDashboard @SinceUtc = @s;' -Parameters @{ '@s' = $since }
    if ((Get-Count $Database 'audit.UserDirectory') -eq 0) { $todo += 'Load the user directory from AD (deployment step 9): the dashboard''s scopes come from it.' }
}
if ((Get-Count $Database 'audit.CopilotLicense') -eq 0 -and $records -gt 0) { $todo += 'Load current licences (deployment step 10, tools\Update-CopilotAuditEnrichment.ps1).' }

# ---- 4. Summary -------------------------------------------------------------------------------
Write-Host ''
Write-Host 'Done.' -ForegroundColor Green
if ($todo) { Write-Host 'Still to do:'; $todo | ForEach-Object { Write-Host "  - $_" } }
