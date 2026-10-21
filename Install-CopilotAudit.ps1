<#
.SYNOPSIS
    Sets up, upgrades and builds the Copilot audit databases in one command.

.DESCRIPTION
    Run it once before the first backfill, and again after it: it does what the
    state of the databases calls for, and is safe to run any number of times.

      1. Creates the Copilot and license-event databases if they do not exist
         (compatibility level 160 or the server's highest below it, SIMPLE
         recovery so the bulk loads are minimally logged), and with
         -ServiceAccount gives the nightly job's account a login and db_owner
         in both.
      2. Installs or upgrades every table, view and procedure, in the order
         they depend on each other. Nothing is dropped; data is kept.
      3. Builds whatever needs building from the data already loaded:
           * the license events shredded: the records not shredded yet, and
             what a shred that was stopped part way was shredding (2.5.4: not
             every record on every run; once more over a database an earlier
             release shredded, unless -LicenseEventsShredded);
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
    The top of the organization for the dashboard (usually the CEO). Leave it
    out and the dashboard uses the one person the org chart has most people
    under, or keeps what an administrator already set.

.PARAMETER ReportingTimeZone
    The zone days and Saturday-to-Friday weeks are cut in: a name from
    sys.time_zone_info, such as 'GMT Standard Time'. Default: this server's
    own zone, for a database with no records and no zone yet (2.4.10); a
    database no installer set reads Eastern Standard Time. Stored in both
    databases, where every procedure reads it; set it
    before the first load, as changing it later would leave history cut in
    the old zone (it is refused then).

.PARAMETER Rebuild
    Rebuild the flattened table, sessions, bi and the dashboard in full even
    if nothing calls for it, and shred every license record again.

.PARAMETER LicenseEventsShredded
    For the first run of 2.5.4 or later over a license database an earlier
    release shredded. Such a database carries no mark that its last shred
    finished, so this run shreds every license record once more and leaves
    the mark; later runs then shred only what is new. With this switch you
    say that the last shred did finish (the installer run that made it
    ended with Done.): the mark is written and nothing is shredded again.
    Leave it out when unsure: a shred that was stopped part way left events
    without their properties, and only shredding them all repairs that.

.PARAMETER ActivityDatabase
    2.6.9: the database that holds AI app calls outside Copilot, Power BI and
    Power Apps activity (the backfill's -RecordTypes). Created and installed
    like the license database; its records are shredded the first time and
    rolled up into the Copilot database (audit.usp_RollupActivity). Leave it
    out where there is none.

.PARAMETER MaxDop
    2.7.3: the parallelism each database it creates runs its queries at (ALTER DATABASE SCOPED CONFIGURATION SET
    MAXDOP): by default the SQL Server's logical processors less two, at least 1, so a large history's shred and the
    nightly refresh use the server and leave room for the rest. Given, it is set on every database it installs,
    existing ones included; left out, a database that exists keeps its own. 0 leaves every database as it is.

.EXAMPLE
    .\Install-CopilotAudit.ps1 -SqlServer SQLPROD01
.EXAMPLE
    .\Install-CopilotAudit.ps1 -SqlServer SQLPROD01 -Dashboard -ActivityDatabase CopilotActivity
.EXAMPLE
    .\Install-CopilotAudit.ps1 -SqlServer SQLPROD01 -Dashboard -LicenseEventDatabases CopilotLicenseEvents,CopilotLicenseLive
#>
#Requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $SqlServer,
    [string] $Database = 'CopilotAudit',
    [string] $LicenseDatabase = 'CopilotLicenseEvents',
    # Databases holding license events, for license at the time of use (the backfill's -SessionLicenseDatabase). Defaults to -LicenseDatabase.
    [string[]] $LicenseEventDatabases,
    [pscredential] $SqlCredential,
    [switch] $TrustServerCertificate,
    [switch] $Dashboard,
    [string] $OrgTopUpn,
    [switch] $Rebuild,
    # The license records were all shredded by an earlier release, in a run that finished: do not shred them once more (2.5.4).
    [switch] $LicenseEventsShredded,
    [string] $ReportingTimeZone,
    [switch] $SkipLicenseDatabase,
    # 2.6.9: the activity database (AI apps outside Copilot, Power BI and Power Apps), where there is one. None by default.
    [string] $ActivityDatabase,
    # The account the nightly job runs as (DOMAIN\gmsa$ or DOMAIN\user): given a login and db_owner in both databases.
    [string] $ServiceAccount,
    # Accounts that read the bi views and nothing else, such as the Power BI gateway's (DOMAIN\name).
    [string[]] $ReportingAccount = @(),
    # 2.7.3: MAXDOP for the databases (see .PARAMETER MaxDop): left out, the server's processors less two on the ones it creates.
    [ValidateRange(0, 64)][int] $MaxDop = -1
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$sqlDir = Join-Path $PSScriptRoot 'sql'
if (-not $LicenseEventDatabases) { $LicenseEventDatabases = if ($SkipLicenseDatabase) { @() } else { @($LicenseDatabase) } }
$licList = if ($LicenseEventDatabases) { @($LicenseEventDatabases) -join ',' } else { $null }

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

function Enter-RunLock([string] $Catalog) {
    <#
        Holds the database for this run alone (2.4.12), as the nightly pipeline and the maintenance rebuilds do: an
        exclusive application lock, on a connection of its own kept open to the end of the build. Nothing kept them
        apart, and a nightly that started during an upgrade's full bi rebuild rebuilt the fact under it, counting
        interactions twice. As the pipeline's history build step, the pipeline holds it already (CopilotAuditRunLock).
    #>
    if ($env:CopilotAuditRunLock -eq "$SqlServer/$Catalog") { return $null }
    $b = New-Object System.Data.SqlClient.SqlConnectionStringBuilder (New-ConnString $Catalog)
    $b['Pooling'] = $false   # closing a pooled connection keeps its session, and the session's lock with it
    $c = New-Object System.Data.SqlClient.SqlConnection $b.ConnectionString
    try {
        $c.Open()
        $cmd = $c.CreateCommand()
        $cmd.CommandText = "DECLARE @r INT; EXEC @r = sp_getapplock @Resource = N'CopilotAudit.Run', @LockMode = 'Exclusive', @LockOwner = 'Session', @LockTimeout = 0; SELECT @r;"
        $granted = [int] $cmd.ExecuteScalar() -ge 0
    }
    catch { $c.Dispose(); throw }
    if (-not $granted) {
        $c.Dispose()
        throw "Another run is using $Catalog now: the nightly pipeline, a maintenance rebuild, or this installer in another window. Nothing was changed. Let it finish (Admin > Health shows the pipeline's runs), disable the nightly task while you upgrade, then run this again."
    }
    $c
}

# ---- 1. Databases -------------------------------------------------------------------------------
Write-Step "Databases on $SqlServer"
$targetLevel = [int] (Invoke-Sql -Catalog master -Scalar -Sql "SELECT CASE WHEN CAST(SERVERPROPERTY('ProductMajorVersion') AS INT) >= 16 THEN 160 ELSE CAST(SERVERPROPERTY('ProductMajorVersion') AS INT) * 10 END")
if ($targetLevel -lt 140) { throw 'SQL Server 2017 or later is needed.' }
$dbs = @($Database) + $(if ($SkipLicenseDatabase) { @() } else { @($LicenseDatabase) }) + $(if ($ActivityDatabase) { @($ActivityDatabase) } else { @() })
$created = @()
foreach ($db in $dbs) {
    $exists = [bool] (Invoke-Sql -Catalog master -Scalar -Sql 'SELECT CASE WHEN DB_ID(@d) IS NULL THEN 0 ELSE 1 END' -Parameters @{ '@d' = $db })
    if (-not $exists) {
        Invoke-Sql -Catalog master -Sql "CREATE DATABASE [$($db.Replace(']', ']]'))]; ALTER DATABASE [$($db.Replace(']', ']]'))] SET RECOVERY SIMPLE;"
        Write-Done "created $db (SIMPLE recovery)"
        $created += $db
    }
    $level = [int] (Invoke-Sql -Catalog master -Scalar -Sql 'SELECT compatibility_level FROM sys.databases WHERE name = @d' -Parameters @{ '@d' = $db })
    if ($level -lt 130) {
        Invoke-Sql -Catalog master -Sql "ALTER DATABASE [$($db.Replace(']', ']]'))] SET COMPATIBILITY_LEVEL = $targetLevel;"
        Write-Done "$db compatibility level $level -> $targetLevel"
    }
    elseif ($exists) { Write-Done "$db exists (level $level)" }
}

# 2.7.3: each database it creates runs its queries at the server's logical processors less two (at least 1); -MaxDop
# sets it on every database it installs instead, and 0 leaves them all as they are.
if ($MaxDop -ne 0) {
    $targets = @(if ($MaxDop -gt 0) { $dbs } else { $created })
    if ($targets.Count -gt 0) {
        $dop = $MaxDop
        if ($dop -lt 0) {
            $cpus = 0
            try { $cpus = [int] (Invoke-Sql -Catalog master -Scalar -Sql 'SELECT cpu_count FROM sys.dm_os_sys_info;') } catch { $cpus = 0 }
            $dop = if ($cpus -gt 0) { [Math]::Max(1, $cpus - 2) } else { 0 }
            if ($dop -eq 0) { Write-Done "MAXDOP not set: the server's processor count could not be read (it needs VIEW SERVER STATE); give -MaxDop to set it." }
        }
        if ($dop -gt 0) {
            foreach ($db in $targets) {
                Invoke-Sql -Catalog $db -Sql "ALTER DATABASE SCOPED CONFIGURATION SET MAXDOP = $dop;"
                Write-Done "$db MAXDOP $dop"
            }
        }
    }
}

if ($ServiceAccount) {
    $sa = $ServiceAccount.Replace(']', ']]')
    Invoke-Sql -Catalog master -Sql "IF SUSER_ID(@a) IS NULL EXEC (N'CREATE LOGIN [$sa] FROM WINDOWS');" -Parameters @{ '@a' = $ServiceAccount }
    foreach ($db in $dbs) {
        # The login may already be in the database under another name: as dbo when it owns the
        # database (it created it), or as a user a DBA added. That user is the one to use.
        Invoke-Sql -Catalog $db -Parameters @{ '@a' = $ServiceAccount } -Sql @"
DECLARE @u sysname = (SELECT name FROM sys.database_principals WHERE sid = SUSER_SID(@a));
IF @u IS NULL BEGIN EXEC (N'CREATE USER [$sa] FOR LOGIN [$sa]'); SET @u = @a; END;
IF @u <> N'dbo' AND IS_ROLEMEMBER(N'db_owner', @u) = 0
BEGIN
    DECLARE @s nvarchar(400) = N'ALTER ROLE db_owner ADD MEMBER ' + QUOTENAME(@u) + N';';
    EXEC (@s);
END;
"@
    }
    Write-Done "$ServiceAccount has a login and db_owner in $($dbs -join ' and ')"
}

# One run at a time (2.4.12): held from here to the end of step 3, and let go in the finally there.
$runLock = Enter-RunLock $Database
try {

# ---- 2. Objects ---------------------------------------------------------------------------------
Write-Step "Tables, views and procedures in $Database"
$copilotScripts = 'Create-CopilotAuditSchema.sql', 'Create-CopilotLoadProcedure.sql',
                  'Create-CopilotFlattenedSchema.sql', 'Create-CopilotFlattenProcedure.sql', 'Create-CopilotPurgeProcedure.sql',
                  'Create-CopilotEnrichmentSchema.sql', 'Create-CopilotSessionSchema.sql', 'Create-CopilotSessionProcedure.sql',
                  'Create-CopilotBiSchema.sql', 'Create-CopilotBiProcedure.sql', 'Create-CopilotExternalUsageSchema.sql',
                  'Create-CopilotActivityRollup.sql'
if ($Dashboard) { $copilotScripts += 'Create-CopilotDashboardSchema.sql', 'Create-CopilotDashboardProcedure.sql', 'Create-CopilotOperationsSchema.sql' }
foreach ($s in $copilotScripts) { Invoke-SqlFile $Database $s }
if (-not $SkipLicenseDatabase) {
    Write-Step "Tables, views and procedures in $LicenseDatabase"
    foreach ($s in 'Create-CopilotAuditSchema.sql', 'Create-CopilotLoadProcedure.sql', 'Create-LicenseEventSchema.sql', 'Create-LicenseEventProcedure.sql', 'Create-AgentEventProcedure.sql') { Invoke-SqlFile $LicenseDatabase $s }
}
if ($ActivityDatabase) {
    Write-Step "Tables, views and procedures in $ActivityDatabase"
    foreach ($s in 'Create-CopilotAuditSchema.sql', 'Create-CopilotLoadProcedure.sql', 'Create-ActivitySchema.sql', 'Create-ActivityProcedure.sql') { Invoke-SqlFile $ActivityDatabase $s }
}

# The reporting time zone, stored where every procedure reads it (audit.ReportingTimeZone()).
$zoneDbs = @($Database) + $(if ($SkipLicenseDatabase) { @() } else { @($LicenseDatabase) }) + $(if ($ActivityDatabase) { @($ActivityDatabase) } else { @() })
if ($ReportingTimeZone) {
    $known = [int] (Invoke-Sql -Catalog master -Scalar -Sql 'SELECT COUNT(*) FROM sys.time_zone_info WHERE name = @z' -Parameters @{ '@z' = $ReportingTimeZone })
    if (-not $known) { throw "'$ReportingTimeZone' is not a time zone SQL Server knows. SELECT name FROM sys.time_zone_info lists them." }
    foreach ($db in $zoneDbs) {
        $current = [string] (Invoke-Sql -Catalog $db -Scalar -Sql 'SELECT audit.ReportingTimeZone();')
        # 2.6.9: the zone named is recorded even where it is the default a database with no row reads (Eastern), or a
        # later run with no zone named gave an empty database the server's own zone in its place.
        $recorded = [int] (Invoke-Sql -Catalog $db -Scalar -Sql "SELECT COUNT(*) FROM audit.PipelineSetting WHERE Name = N'ReportingTimeZone';") -gt 0
        if ($current -eq $ReportingTimeZone -and $recorded) { continue }
        if ($current -ne $ReportingTimeZone -and ((Get-Count $db 'audit.CopilotAudit') -gt 0 -or (Get-Count $db 'audit.CopilotInteraction') -gt 0)) {
            throw "$db already holds records cut in $current. The reporting time zone is set before the first load; to change it, load into new databases."
        }
        Invoke-Sql -Catalog $db -Parameters @{ '@z' = $ReportingTimeZone } -Sql @"
MERGE audit.PipelineSetting AS t USING (SELECT N'ReportingTimeZone' AS Name, @z AS Value) AS s ON t.Name = s.Name
WHEN MATCHED THEN UPDATE SET Value = s.Value, UpdatedUtc = SYSUTCDATETIME(), UpdatedBy = SUSER_SNAME()
WHEN NOT MATCHED THEN INSERT (Name, Value) VALUES (s.Name, s.Value);
"@
    }
}
else {
    # 2.4.10: with no zone named, a database that holds no records yet takes this server's own time zone (it was
    # Eastern whatever the server). One that holds records, or that has a zone set, keeps it: its days are already cut.
    $serverZone = [TimeZoneInfo]::Local.Id
    # .NET 6 and later only (PowerShell 7): Windows PowerShell 5.1's zone ids are Windows ones already, and reading the
    # property there stopped the script under strict mode, before any rebuild ran (2.4.12).
    if ($PSVersionTable.PSEdition -eq 'Core' -and [TimeZoneInfo]::Local.HasIanaId) {
        $windowsId = $null
        $serverZone = if ([TimeZoneInfo]::TryConvertIanaIdToWindowsId($serverZone, [ref] $windowsId)) { $windowsId } else { $null }
    }
    if ($serverZone -and [int] (Invoke-Sql -Catalog master -Scalar -Sql 'SELECT COUNT(*) FROM sys.time_zone_info WHERE name = @z' -Parameters @{ '@z' = $serverZone })) {
        foreach ($db in $zoneDbs) {
            if ([int] (Invoke-Sql -Catalog $db -Scalar -Sql "SELECT COUNT(*) FROM audit.PipelineSetting WHERE Name = N'ReportingTimeZone';") -gt 0) { continue }
            if ((Get-Count $db 'audit.CopilotAudit') -gt 0 -or (Get-Count $db 'audit.CopilotInteraction') -gt 0) { continue }
            Invoke-Sql -Catalog $db -Parameters @{ '@z' = $serverZone } -Sql "INSERT INTO audit.PipelineSetting (Name, Value) VALUES (N'ReportingTimeZone', @z);"
        }
    }
}
Write-Done ("reporting time zone: {0}" -f (Invoke-Sql -Catalog $Database -Scalar -Sql 'SELECT audit.ReportingTimeZone();'))

# Reporting accounts read the bi views and nothing else (2.4.12). They were given SELECT on the whole bi schema, which
# holds the tables under the views too: the Power BI account read bi.DimUser's real names beside privacy mode's
# pseudonyms, and every column the views leave out. They are members of CopilotReporting now, which each run grants
# every bi view to, so a view a release adds is readable too; the schema-wide grant is taken back.
$reporting = @($ReportingAccount | Where-Object { $_ })
if ($reporting.Count -gt 0 -or [bool] (Invoke-Sql -Catalog $Database -Scalar -Sql "SELECT CASE WHEN DATABASE_PRINCIPAL_ID(N'CopilotReporting') IS NULL THEN 0 ELSE 1 END")) {
    Invoke-Sql -Catalog $Database -Sql @"
IF DATABASE_PRINCIPAL_ID(N'CopilotReporting') IS NULL CREATE ROLE CopilotReporting AUTHORIZATION dbo;
DECLARE @v sysname, @s nvarchar(400);
DECLARE v CURSOR LOCAL FAST_FORWARD FOR SELECT name FROM sys.views WHERE schema_id = SCHEMA_ID(N'bi');
OPEN v; FETCH NEXT FROM v INTO @v;
WHILE @@FETCH_STATUS = 0
BEGIN
    SET @s = N'GRANT SELECT ON bi.' + QUOTENAME(@v) + N' TO CopilotReporting;';
    EXEC (@s);
    FETCH NEXT FROM v INTO @v;
END;
"@
}
foreach ($acct in $reporting) {
    $ra = $acct.Replace(']', ']]')
    # A database user of that name already there (this installer's from before, or a DBA's) is used as it is.
    if (-not [bool] (Invoke-Sql -Catalog $Database -Scalar -Sql 'SELECT CASE WHEN USER_ID(@a) IS NULL THEN 0 ELSE 1 END' -Parameters @{ '@a' = $acct })) {
        Invoke-Sql -Catalog master -Sql "IF SUSER_ID(@a) IS NULL EXEC (N'CREATE LOGIN [$ra] FROM WINDOWS');" -Parameters @{ '@a' = $acct }
        Invoke-Sql -Catalog $Database -Sql "CREATE USER [$ra] FOR LOGIN [$ra];"
    }
    Invoke-Sql -Catalog $Database -Sql "REVOKE SELECT ON SCHEMA::bi FROM [$ra]; ALTER ROLE CopilotReporting ADD MEMBER [$ra];"
    Write-Done "$acct can read the bi views in $Database, and nothing else"
}
# Others that read every table of bi (an account given -ReportingAccount before 2.4.12, and not again): said, not changed.
$wide = Invoke-Sql -Catalog $Database -Scalar -Sql @"
SELECT STRING_AGG(CONVERT(NVARCHAR(MAX), pr.name), N', ')
FROM sys.database_permissions AS p
JOIN sys.database_principals AS pr ON pr.principal_id = p.grantee_principal_id
WHERE p.class = 3 AND p.major_id = SCHEMA_ID(N'bi') AND p.permission_name = N'SELECT' AND p.state IN ('G', 'W')
  AND pr.name <> N'CopilotInsightsApp' AND ISNULL(IS_ROLEMEMBER(N'CopilotInsightsApp', pr.name), 0) = 0
  AND NOT EXISTS (SELECT 1 FROM sys.database_permissions AS d WHERE d.grantee_principal_id = pr.principal_id AND d.class = 3 AND d.major_id = SCHEMA_ID(N'dash'));
"@
if ($wide -isnot [DBNull] -and $wide) {
    Write-Warning "These accounts can read every table of the bi schema in $Database, not only its views, so privacy mode does not hide names from them: $wide. Give a reporting account to -ReportingAccount again to narrow it to the views."
}

if ($Dashboard -and $OrgTopUpn) {
    Invoke-Sql -Catalog $Database -Parameters @{ '@v' = $OrgTopUpn.Trim(); '@by' = "$env:USERDOMAIN\$env:USERNAME" } -Sql @"
UPDATE dash.Setting SET Value = @v, UpdatedBy = @by, UpdatedUtc = SYSUTCDATETIME() WHERE Name = 'OrgTopUpn';
"@
    Write-Done "top of the organization: $OrgTopUpn"
}

# ---- 3. Build what needs building ---------------------------------------------------------------
Write-Step 'Building from the data loaded so far'
$records = Get-Count $Database 'audit.CopilotAudit'
$flattened = Get-Count $Database 'audit.RawLogFlattened'
$todo = @()

if (-not $SkipLicenseDatabase) {
    $licRecords = Get-Count $LicenseDatabase 'audit.CopilotAudit'
    if ($licRecords -gt 0) {
        # 2.5.4: only when something calls for it. Every record was read and parsed again on every run, an upgrade's
        # longest step on a large license history, to write the rows already there. Every record is shredded now for
        # -Rebuild and, once, for a database with no mark that a shred of every record finished
        # (LicenseShredCompleteUtc), which is every database an earlier release shredded - unless
        # -LicenseEventsShredded says that one did. After that: records with no event of their own (counted: two
        # columns of the columnstore against a key) are shredded from the earliest of them on; and a shred that was
        # stopped part way, which leaves its mark (LicenseShredInProgress) and events without their properties, has
        # what it was shredding shredded again, and no more.
        $unshredded = [long] (Invoke-Sql -Catalog $LicenseDatabase -Scalar -Sql @"
SELECT COUNT_BIG(*) FROM audit.CopilotAudit AS a
WHERE a.Operation NOT LIKE N'Bot%' AND a.Operation NOT IN (N'BlockedAgent', N'UnblockedAgent', N'DeployedAgent', N'RemovedAgent', N'DeletedAgent', N'UpdatedAgent', N'UpdatedTenantSettings') AND NOT EXISTS (SELECT 1 FROM audit.LicenseEvent AS e WHERE e.RecordId = a.RecordId);
"@)
        $unshreddedAgents = [long] (Invoke-Sql -Catalog $LicenseDatabase -Scalar -Sql @"
SELECT COUNT_BIG(*) FROM audit.CopilotAudit AS a
WHERE (a.Operation LIKE N'Bot%' OR a.Operation IN (N'BlockedAgent', N'UnblockedAgent', N'DeployedAgent', N'RemovedAgent', N'DeletedAgent', N'UpdatedAgent', N'UpdatedTenantSettings')) AND NOT EXISTS (SELECT 1 FROM audit.AgentEvent AS e WHERE e.RecordId = a.RecordId);
"@)
        $marks = [string] (Invoke-Sql -Catalog $LicenseDatabase -Scalar -Sql "SELECT ISNULL(STRING_AGG(CONVERT(NVARCHAR(MAX), Name), N','), N'') FROM audit.PipelineSetting WHERE Name IN (N'LicenseShredInProgress', N'LicenseShredCompleteUtc');")
        $stopped = $marks -like '*LicenseShredInProgress*'
        $complete = $marks -like '*LicenseShredCompleteUtc*'
        # The one damage that shows in the tables themselves: a chunk group with every fragment, and none of the
        # properties it reassembles into on its first record. A shred stopped before its last step leaves them so.
        # Only a group whose document does hold properties counts: one that holds none has none to show.
        $unassembled = [long] 0
        if (-not $complete -and $LicenseEventsShredded) {
            $unassembled = [long] (Invoke-Sql -Catalog $LicenseDatabase -Scalar -Sql @"
SELECT COUNT_BIG(*)
FROM (SELECT c.ChunkGroupId, MAX(CASE WHEN c.Seq = 1 THEN c.RecordId END) AS Carrier
      FROM audit.LicenseEventChunk AS c GROUP BY c.ChunkGroupId HAVING MIN(c.Seq) = 1 AND MAX(c.Seq) = COUNT(*)) AS g
WHERE NOT EXISTS (SELECT 1 FROM audit.LicenseEventProperty AS p WHERE p.RecordId = g.Carrier AND p.Source = 'chunks')
  AND EXISTS (SELECT 1
              FROM (SELECT STRING_AGG(CAST(b.Body AS NVARCHAR(MAX)), N'') WITHIN GROUP (ORDER BY b.Seq) AS Payload
                    FROM audit.LicenseEventChunk AS b WHERE b.ChunkGroupId = g.ChunkGroupId) AS w
              CROSS APPLY OPENJSON(CASE WHEN ISJSON(w.Payload) = 1 THEN w.Payload ELSE N'{}' END)
                   WITH (Tup NVARCHAR(MAX) '`$.targetUpdatedProperties') AS d
              CROSS APPLY OPENJSON(CASE WHEN ISJSON(d.Tup) = 1 THEN d.Tup ELSE N'[]' END) AS item);
"@)
        }
        $why = if ($Rebuild) { '-Rebuild' }
               elseif ($unassembled -gt 0) { '{0:N0} chunk group(s) were never reassembled, so a shred did stop part way: -LicenseEventsShredded is not taken' -f $unassembled }
               elseif (-not $complete -and -not $LicenseEventsShredded) {
                   if ($unshredded -gt 0) { '{0:N0} not shredded yet' -f $unshredded }
                   else { 'once more, to record that every one is shredded: later runs skip this, and -LicenseEventsShredded skips it now' }
               }
               else { $null }
        if ($why) {
            Write-Done ("license events: shredding {0:N0} record(s) ({1})..." -f $licRecords, $why)
            Invoke-Sql -Catalog $LicenseDatabase -Sql 'EXEC audit.usp_ShredLicenseEvents;'
        }
        else {
            if ($unshredded -gt 0) {
                # Every record is shredded but these, which arrived since: from the earliest of them on (a second
                # before it, as the shred counts in whole seconds). Where a stopped shred's mark says to go further
                # back, the procedure does.
                $from = [string] (Invoke-Sql -Catalog $LicenseDatabase -Scalar -Sql @"
SELECT CONVERT(NVARCHAR(19), DATEADD(SECOND, -1, MIN(a.CreatedDateTime)), 126) FROM audit.CopilotAudit AS a
WHERE a.Operation NOT LIKE N'Bot%' AND a.Operation NOT IN (N'BlockedAgent', N'UnblockedAgent', N'DeployedAgent', N'RemovedAgent', N'DeletedAgent', N'UpdatedAgent', N'UpdatedTenantSettings') AND NOT EXISTS (SELECT 1 FROM audit.LicenseEvent AS e WHERE e.RecordId = a.RecordId);
"@)
                Write-Done ("license events: shredding the records from {0}Z on ({1:N0} not shredded yet)..." -f $from.Replace('T', ' '), $unshredded)
                Invoke-Sql -Catalog $LicenseDatabase -Sql 'EXEC audit.usp_ShredLicenseEvents @SinceUtc = @s;' -Parameters @{ '@s' = $from }
            }
            elseif ($stopped) {
                # From no date of its own: the procedure reads the mark, and shreds from where the stopped one began.
                Write-Done 'license events: an earlier shred did not finish, so what it was shredding is shredded again...'
                Invoke-Sql -Catalog $LicenseDatabase -Sql "EXEC audit.usp_ShredLicenseEvents @SinceUtc = '9999-12-31';"
            }
            else { Write-Done ("license events: every one of {0:N0} record(s) is shredded already, so none is read again (-Rebuild reads them all)." -f $licRecords) }
            # Not when the shred just above was one of every record: that leaves the mark itself.
            if (-not $complete -and [int] (Invoke-Sql -Catalog $LicenseDatabase -Scalar -Sql "SELECT COUNT(*) FROM audit.PipelineSetting WHERE Name = N'LicenseShredCompleteUtc';") -eq 0) {
                Invoke-Sql -Catalog $LicenseDatabase -Sql "INSERT INTO audit.PipelineSetting (Name, Value) VALUES (N'LicenseShredCompleteUtc', CONVERT(NVARCHAR(30), SYSUTCDATETIME(), 126));"
                Write-Done 'license events: recorded as all shredded by an earlier run that finished (-LicenseEventsShredded).'
            }
        }
        if ($Rebuild -or $unshreddedAgents -gt 0) { Invoke-Sql -Catalog $LicenseDatabase -Sql 'EXEC audit.usp_ShredAgentEvents;' }
    }
    else { $todo += "Backfill the license history into $LicenseDatabase (deployment step 7), then run this again." }
    # 2.5.4: the SKU changes sessions and bi read are worked out at the end of each shred. Events written since some
    # other way (by hand, or by a shred of a build before the marks above) are worked out here.
    if ([int] (Invoke-Sql -Catalog $LicenseDatabase -Scalar -Sql 'SELECT CASE WHEN EXISTS (SELECT 1 FROM audit.LicenseEvent WHERE ShreddedUtc > (SELECT MAX(StartedUtc) FROM audit.LicenseSkuChangeRun)) THEN 1 ELSE 0 END;') -eq 1) {
        Write-Done 'license SKU changes: working out those of the events shredded since they were last worked out...'
        Invoke-Sql -Catalog $LicenseDatabase -Sql 'EXEC audit.usp_RefreshLicenseSkuChange;'
    }
}

# Flattened by a version before 2.2.0: rows, but no record shapes - only the 2.2.0
# flatten counts them. Upgraded scripts alone do not fill history; this does, once.
$upgrade = $flattened -gt 0 -and (Get-Count $Database 'audit.RawLogRecordShape') -eq 0
# 2.4.12: built is a flatten of every record that finished, which leaves a mark (FullFlattenUtc). Any flattened row
# counted as built, so a first flatten stopped part way, or a nightly that flattened only its own few days, left the
# rest of history unflattened for good. A database flattened before 2.4.12 has no mark: it is given one here when every
# record is in the flattened table (counted once, minutes at 40 million), and flattened again when some are not.
$unflattened = [long] 0
if ($records -gt 0 -and $flattened -gt 0 -and -not $Rebuild -and -not $upgrade -and
    [long] (Invoke-Sql -Catalog $Database -Scalar -Sql "SELECT COUNT(*) FROM audit.PipelineSetting WHERE Name = N'FullFlattenUtc';") -eq 0) {
    Write-Done 'checking that every record has been flattened (once)...'
    $unflattened = [long] (Invoke-Sql -Catalog $Database -Scalar -Sql @"
SELECT COUNT_BIG(*) FROM audit.CopilotAudit AS a
WHERE NOT EXISTS (SELECT 1 FROM audit.RawLogFlattened AS f WHERE f.RecordId = a.RecordId);
"@)
    if ($unflattened -eq 0) {
        Invoke-Sql -Catalog $Database -Sql "INSERT INTO audit.PipelineSetting (Name, Value) VALUES (N'FullFlattenUtc', CONVERT(NVARCHAR(30), SYSUTCDATETIME(), 126));"
    }
}
$fullBuild = $Rebuild -or ($records -gt 0 -and $flattened -eq 0) -or $upgrade -or $unflattened -gt 0
if ($records -eq 0) {
    $todo += "Load Copilot records into $Database (the backfill, deployment step 6, or a CSV import), then run this again."
}
elseif ($fullBuild) {
    $why = if ($upgrade) { 'the flattened table gained columns (2.2.0), so every record is flattened again' } elseif ($Rebuild) { '-Rebuild' }
           elseif ($unflattened -gt 0) { '{0:N0} were never flattened: an earlier flatten stopped part way, or only the nightly''s days were' -f $unflattened } else { 'first build' }
    Write-Done ("flattening {0:N0} record(s) ({1})..." -f $records, $why)
    Invoke-Sql -Catalog $Database -Sql 'EXEC audit.usp_FlattenCopilotAudit;'
    Write-Done 'sessions, in full...'
    Invoke-Sql -Catalog $Database -Sql 'EXEC audit.usp_BuildCopilotSessions @LicenseEventDatabase = @l;' -Parameters @{ '@l' = $licList }
    Write-Done 'bi, in full...'
    Invoke-Sql -Catalog $Database -Sql 'EXEC bi.usp_RefreshBi @LicenseEventDatabase = @l;' -Parameters @{ '@l' = $licList }
    if ($upgrade) { $todo += 'Refresh the Power BI model in full once. The ready-made model in powerbi\CopilotInsights has every bi view (docs/DEPLOYMENT.md, step 15).' }
}
else { Write-Done 'sessions and bi are built; the nightly -Incremental run keeps them current.' }

# 2.4.11: the flatten reads an agent's id and friendly name where the audit log puts them, at the top of the record.
# Records flattened before have neither. They are read here once, from the records of agents only (minutes, where a
# re-flatten takes hours), decided from the mark the read leaves, so an install stopped part way reads them the next time.
if ($flattened -gt 0 -and -not $fullBuild -and (Test-Object $Database 'audit.usp_BackfillAgentNames' 'P') -and
    [long] (Invoke-Sql -Catalog $Database -Scalar -Sql "SELECT COUNT(*) FROM audit.PipelineSetting WHERE Name = N'AgentNamesReadUtc';") -eq 0) {
    Write-Done 'agent names: reading the id and friendly name from the records of agents, once (2.4.11)...'
    Invoke-Sql -Catalog $Database -Sql 'EXEC audit.usp_BackfillAgentNames;'
    if (Test-Object $Database 'bi.usp_NameAgents' 'P') { Invoke-Sql -Catalog $Database -Sql 'EXEC bi.usp_NameAgents;' }
    $todo += 'Refresh the Power BI model in full once (docs/DEPLOYMENT.md, step 16): agents carry the friendly names the audit log gives them, and bi.Agents gained NamedInAudit.'
}

# 2.4.12: the records of other apps that carry an agent's id (Copilot Chat running a declarative agent), which bi keys
# on that id. 2.4.11 read it from its upgrade night on, so the same agent's history started then. Read once here, from
# the apps whose records since show one; a record that gains an id changes agent, so bi is rebuilt once (below).
$agentIds = $false
if ($flattened -gt 0 -and -not $fullBuild -and (Test-Object $Database 'audit.usp_BackfillAgentNames' 'P') -and
    [long] (Invoke-Sql -Catalog $Database -Scalar -Sql "SELECT COUNT(*) FROM audit.PipelineSetting WHERE Name = N'AgentIdsReadUtc';") -eq 0) {
    Write-Done 'agent ids: reading them from the records of other apps that carry them, once (2.4.12)...'
    $gainedIds = [long] (Invoke-Sql -Catalog $Database -Scalar -Sql 'EXEC audit.usp_BackfillAgentNames @Others = 1;')
    if ($gainedIds -gt 0) {
        $agentIds = $true
        if (Test-Object $Database 'bi.usp_NameAgents' 'P') { Invoke-Sql -Catalog $Database -Sql 'EXEC bi.usp_NameAgents;' }
    }
}

# 2.6.3: an agent the audit log names two ways is one agent (an id written with and without its prefix, the variants of
# one of Microsoft's own, the two ids the agent workbook holds for one agent). The nightly refresh keeps it so; this
# does it at the upgrade, so the reports do not wait a night. A moment once done: it then finds nothing to move.
if (-not $fullBuild -and (Test-Object $Database 'bi.usp_MergeAgents' 'P') -and (Test-Object $Database 'bi.FactInteraction')) {
    $secondBefore = [long] (Invoke-Sql -Catalog $Database -Scalar -Sql 'SELECT COUNT(*) FROM bi.DimAgent WHERE MergedIntoKey IS NOT NULL;')
    Invoke-Sql -Catalog $Database -Sql 'EXEC bi.usp_MergeAgents; EXEC bi.usp_NameAgents;'
    $secondNow = [long] (Invoke-Sql -Catalog $Database -Scalar -Sql 'SELECT COUNT(*) FROM bi.DimAgent WHERE MergedIntoKey IS NOT NULL;')
    if ($secondNow -gt $secondBefore) {
        Write-Done "agents: $($secondNow - $secondBefore) second identit$(if ($secondNow - $secondBefore -eq 1) { 'y' } else { 'ies' }) of an agent the audit log names two ways now counted with the agent (2.6.3)."
        $todo += 'Refresh the Power BI model in full once (docs/DEPLOYMENT.md, step 16): interactions kept under the second identity of an agent moved to the agent.'
    }
}

# 2.4.6: each resource Copilot read carries its file type. Resources loaded before have none, so bi is refreshed in full
# once - decided from the data, not from this run, so an install stopped part way finishes it the next time.
# 2.4.8: each interaction carries what its record says about licensed use (UseKind), which Premium use is now decided
# from. Interactions loaded before have none: the same one full refresh, and every day of the rollups after it.
$noFileType = $flattened -gt 0 -and -not $fullBuild -and (Get-Count $Database 'bi.FactAccessedResource') -gt 0 -and
    [long] (Invoke-Sql -Catalog $Database -Scalar -Sql 'SELECT CASE WHEN EXISTS (SELECT 1 FROM bi.FactAccessedResource WHERE FileTypeKey > 1) THEN 1 ELSE 0 END;') -eq 0
$noUseKind = $flattened -gt 0 -and -not $fullBuild -and
    [long] (Invoke-Sql -Catalog $Database -Scalar -Sql 'SELECT CASE WHEN EXISTS (SELECT 1 FROM bi.FactInteraction WHERE UseKind IS NULL) THEN 1 ELSE 0 END;') -eq 1
# 2.4.9: a full refresh that was stopped part way (bi.usp_RefreshBi leaves a mark while one runs), or, for one stopped
# before 2.4.9, a fact with fewer than 99 in 100 of the flattened records: finished here, with every day of the rollups.
$cutShort = $flattened -gt 0 -and -not $fullBuild -and (Test-Object $Database 'bi.FactInteraction') -and
    ((Get-Count $Database 'bi.RebuildInProgress') -gt 0 -or (Get-Count $Database 'bi.FactInteraction') * 100 -lt $flattened * 99)
if ($noFileType -or $noUseKind -or $cutShort -or $agentIds) {
    $gain = @($(if ($noFileType) { 'the resources Copilot read gain their file type (2.4.6)' }), $(if ($noUseKind) { 'Premium use is decided from each record''s own identity (2.4.8)' }),
        $(if ($cutShort) { ('an earlier full refresh did not finish ({0:N0} of {1:N0} interactions are there)' -f (Get-Count $Database 'bi.FactInteraction'), $flattened) }),
        $(if ($agentIds) { ('{0:N0} interactions gain the agent the audit log names by id (2.4.12)' -f $gainedIds) })) | Where-Object { $_ }
    Write-Done "bi, in full once: $($gain -join '; '). This takes as long as a full bi refresh..."
    Invoke-Sql -Catalog $Database -Sql 'EXEC bi.usp_RefreshBi @LicenseEventDatabase = @l;' -Parameters @{ '@l' = $licList }
    if ($noFileType) { $todo += 'Refresh the Power BI model in full once (docs/DEPLOYMENT.md, step 16): bi.AccessedResources gained FileTypeKey, with names in the new bi.FileTypes.' }
    if ($agentIds) { $todo += 'Refresh the Power BI model in full once (docs/DEPLOYMENT.md, step 16): interactions of every day moved to the agents the audit log names by id, and a scheduled refresh redoes only the last 4 days.' }
}

# 2.3.0: the value catalog fills as records are flattened; records flattened before it existed are cataloged once here.
if ($flattened -gt 0 -and -not $fullBuild -and (Test-Object $Database 'audit.ValueCatalog') -and (Get-Count $Database 'audit.ValueCatalog') -eq 0) {
    Write-Done ("value catalog: reading {0:N0} flattened record(s) once..." -f $flattened)
    Invoke-Sql -Catalog $Database -Sql 'EXEC audit.usp_CatalogValues @Rebuild = 1;'
}

if ($Dashboard -and (Test-Object $Database 'bi.FactInteraction')) {
    # 2.3.0 added Premium-feature use to the rollups: rebuilt in full once, when they have days but none of it.
    $premiumEmpty = (Get-Count $Database 'dash.UserDay') -gt 0 -and
        [long] (Invoke-Sql -Catalog $Database -Scalar -Sql 'SELECT CASE WHEN EXISTS (SELECT 1 FROM dash.UserDay WHERE PremiumUse > 0) OR NOT EXISTS (SELECT 1 FROM dash.UserDay WHERE LicensedUse > 0) THEN 1 ELSE 0 END;') -eq 0
    $since = if ($fullBuild -or $premiumEmpty -or $noUseKind -or $cutShort -or $agentIds -or (Get-Count $Database 'dash.UserDay') -eq 0) { $null } else { [datetime]::UtcNow.Date }
    Write-Done $(if ($since) { 'dashboard: org chart, and today''s rollups...' } else { 'dashboard: org chart and every day''s rollups...' })
    Invoke-Sql -Catalog $Database -Sql 'EXEC dash.usp_RefreshDashboard @SinceUtc = @s;' -Parameters @{ '@s' = $since }
    if ((Get-Count $Database 'audit.UserDirectory') -eq 0) { $todo += 'Load the user directory from AD (deployment step 9): the dashboard''s scopes come from it.' }
}

# 2.6.9: the activity database's records shredded where they have not been (the first time, after a shred stopped part
# way, or with -Rebuild), then rolled up into this database for the dashboard.
if ($ActivityDatabase) {
    $actRecords = Get-Count $ActivityDatabase 'audit.CopilotAudit'
    if ($actRecords -gt 0) {
        $actMarks = [string] (Invoke-Sql -Catalog $ActivityDatabase -Scalar -Sql "SELECT ISNULL(STRING_AGG(CONVERT(NVARCHAR(MAX), Name), N','), N'') FROM audit.PipelineSetting WHERE Name IN (N'ActivityShredInProgress', N'ActivityShredCompleteUtc');")
        if ($Rebuild -or $actMarks -notlike '*ActivityShredCompleteUtc*' -or $actMarks -like '*ActivityShredInProgress*') {
            Write-Done ("activity: shredding {0:N0} record(s)..." -f $actRecords)
            Invoke-Sql -Catalog $ActivityDatabase -Sql 'EXEC audit.usp_ShredActivity;'
        }
        Write-Done 'activity: AI apps, Power BI and Power Apps rolled up into this database...'
        Invoke-Sql -Catalog $Database -Sql 'EXEC audit.usp_RollupActivity @ActivityDatabase = @a;' -Parameters @{ '@a' = $ActivityDatabase }
    }
    else { $todo += "Load the activity history into $ActivityDatabase (Invoke-CopilotPipeline.ps1 -ActivityHistory), then run this again." }
}
if ((Get-Count $Database 'audit.CopilotLicense') -eq 0 -and $records -gt 0) { $todo += 'Load current licenses (deployment step 10, tools\Update-CopilotAuditEnrichment.ps1).' }

}
finally { if ($runLock) { $runLock.Dispose() } }

# ---- 4. Summary -------------------------------------------------------------------------------
Write-Host ''
Write-Host 'Done.' -ForegroundColor Green
if ($todo) { Write-Host 'Still to do:'; $todo | ForEach-Object { Write-Host "  - $_" } }
