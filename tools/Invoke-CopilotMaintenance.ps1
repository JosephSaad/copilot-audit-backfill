<#
.SYNOPSIS
    Day-to-day tasks on the Copilot audit database, so nobody types SQL by hand.

.DESCRIPTION
    -Task Status               What is loaded and built, and when each step last ran.
    -Task Reflatten            Flatten every record again, then rebuild sessions, bi and
                               the dashboard (after a flattening change).
    -Task RebuildSessions      Rebuild every session (after a session setting changes), then
                               bring bi and the dashboard up to date.
    -Task RebuildBi            Rebuild bi in full (after a time-zone change, license history
                               arriving late, or a purge of flattened rows), then the dashboard.
    -Task RefreshDashboard     Rebuild the dashboard's org chart, and its rollups from -SinceUtc
                               (every day when omitted).
    -Task SetAppCategory       Put -App in -Category (Copilot Chat or In-App Copilot). Add -Apply
                               to rebuild sessions and bi with it now rather than on the next load.
    -Task GrantRole            Give -Principal (a UPN, DOMAIN\user or DOMAIN\Group) a dashboard -Role.
    -Task RevokeRole           Take it away again.
    -Task SetOrgTop            Make -Upn the top of the organization for the dashboard, or -Clear
                               to go back to the one found in the org chart. Rebuilds the org chart.
    -Task SetDashboardSetting  Set MinGroupSize, DefaultDays, InactiveDays, LicenseMonthlyCost,
                               LicenseCurrency, PaygInteractionCost, MaxViewerLevel or ProtectedLevels to -Value.
                               MaxViewerLevel 0 opens the dashboard to everyone; 1 to 15 to
                               levels 1 to that below the top, and anyone granted a role.
                               ProtectedLevels N keeps levels 1 to N off every revoke and
                               train list (5 by default; 0 for none).
    -Task NameAgent            Give the agent -AgentId (its id in the audit log) a -FriendlyName
                               (and -Owner). An empty -FriendlyName removes the name.
    -Task NameLabel            Give the sensitivity label -LabelId a -Name (and -Rank, 0 least sensitive).
    -Task PurgeJson            Clear the stored JSON of records older than -RetainDays (with
                               -DeleteRows, remove those records altogether: the record, its
                               shredded and flattened rows, and the resources and contexts it
                               read; -Task RebuildBi then takes them out of bi and the
                               dashboard). Reports only, unless -Apply.
    -Task RebuildValueCatalog  List every distinct value of the cataloged columns again, from
                               every flattened record: once, for data flattened before 2.3.0.
    -Task ListValues           The cataloged columns, or with -ColumnId one column's values
                               and names; -Path writes them to a CSV to fill in.
    -Task ImportValueNames     Readable names for cataloged values from -Path, a CSV of
                               ColumnId, Value, Name[, Note] (what ListValues -Path writes).
                               An empty Name removes the name.

    Most of these are also in the dashboard's Admin pages; this is the same for
    servers without it, and for scripts.

.EXAMPLE
    .\tools\Invoke-CopilotMaintenance.ps1 -SqlServer SQLPROD01 -Task Status
.EXAMPLE
    .\tools\Invoke-CopilotMaintenance.ps1 -SqlServer SQLPROD01 -Task SetAppCategory -App 'Copilot Notebooks' -Category 'Copilot Chat' -Apply
.EXAMPLE
    .\tools\Invoke-CopilotMaintenance.ps1 -SqlServer SQLPROD01 -Task GrantRole -Principal 'CONTOSO\Copilot-Insights-Admins' -Role Admin
#>
#Requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $SqlServer,
    [string] $Database = 'CopilotAudit',
    # The license databases sessions and bi read. Left out: the ones this database's last build used (2.4.12).
    [string[]] $LicenseEventDatabases,
    [pscredential] $SqlCredential,
    [switch] $TrustServerCertificate,
    [Parameter(Mandatory)]
    [ValidateSet('Status', 'Reflatten', 'RebuildSessions', 'RebuildBi', 'RefreshDashboard', 'SetAppCategory', 'GrantRole',
                 'RevokeRole', 'SetOrgTop', 'SetDashboardSetting', 'NameAgent', 'NameLabel', 'PurgeJson',
                 'RebuildValueCatalog', 'ListValues', 'ImportValueNames')]
    [string] $Task,
    [string] $App,
    [ValidateSet('Copilot Chat', 'In-App Copilot')][string] $Category,
    [string] $Principal,
    [ValidateSet('Admin', 'Organization', 'Adoption', 'Licensing', 'Security', 'Compliance', 'Agents', 'Enablement')][string] $Role,
    [string] $Note,
    [ValidateSet('User', 'Group')][string] $PrincipalType,
    [string] $Upn,
    [switch] $Clear,
    [ValidateSet('MinGroupSize', 'DefaultDays', 'InactiveDays', 'LicenseMonthlyCost', 'LicenseCurrency', 'PaygInteractionCost', 'MaxViewerLevel', 'ProtectedLevels')][string] $Name,
    [string] $Value,
    [string] $AgentId,
    [string] $FriendlyName,
    [string] $Owner,
    [string] $LabelId,
    [ValidateRange(0, 250)][int] $Rank = -1,
    [datetime] $SinceUtc,
    [ValidateRange(1, 3650)][int] $RetainDays = 365,
    [switch] $Apply,
    # PurgeJson: remove the old records altogether, not only their JSON.
    [switch] $DeleteRows,
    # ListValues: one column of audit.ValueCatalogColumn.
    [int] $ColumnId,
    # ListValues writes, ImportValueNames reads: a CSV of ColumnId, Value, Name, Note.
    [string] $Path
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$me = "$env:USERDOMAIN\$env:USERNAME"

function Invoke-Sql {
    param([string] $Sql, [hashtable] $Parameters = @{}, [switch] $Table, [switch] $Scalar)
    $b = New-Object System.Data.SqlClient.SqlConnectionStringBuilder
    $b['Data Source'] = $SqlServer; $b['Initial Catalog'] = $Database
    if ($SqlCredential) { $b['User ID'] = $SqlCredential.UserName; $b['Password'] = $SqlCredential.GetNetworkCredential().Password }
    else { $b['Integrated Security'] = $true }
    $b['Encrypt'] = $true; $b['TrustServerCertificate'] = [bool] $TrustServerCertificate; $b['Application Name'] = 'Invoke-CopilotMaintenance'
    $conn = New-Object System.Data.SqlClient.SqlConnection $b.ConnectionString
    $conn.add_InfoMessage({ param($s, $e) foreach ($m in $e.Errors) { if ($m.Class -le 10 -and $m.Message -notlike 'Warning: Null value*') { Write-Host "  $($m.Message)" -ForegroundColor DarkGray } } })
    $conn.Open()
    try {
        $cmd = $conn.CreateCommand(); $cmd.CommandText = $Sql; $cmd.CommandTimeout = 0
        foreach ($k in $Parameters.Keys) { [void] $cmd.Parameters.AddWithValue($k, $(if ($null -eq $Parameters[$k]) { [DBNull]::Value } else { $Parameters[$k] })) }
        if ($Scalar) { return $cmd.ExecuteScalar() }
        if ($Table) { $t = New-Object System.Data.DataTable; $r = $cmd.ExecuteReader(); try { $t.Load($r) } finally { $r.Dispose() }; return , $t }
        [void] $cmd.ExecuteNonQuery()
    }
    finally { $conn.Dispose() }
}
function Test-Proc([string] $n) { [bool] (Invoke-Sql -Scalar -Sql "SELECT CASE WHEN OBJECT_ID(@n, N'P') IS NULL THEN 0 ELSE 1 END" -Parameters @{ '@n' = $n }) }
function Need([string] $what, $value) { if (-not $value) { throw "-Task $Task needs -$what." } }
function Update-Dashboard([object] $since) {
    if (Test-Proc 'dash.usp_RefreshDashboard') { Invoke-Sql -Sql 'EXEC dash.usp_RefreshDashboard @SinceUtc = @s;' -Parameters @{ '@s' = $since } }
}
# The license databases (2.4.12): those given, else the ones this database's last session build used, else its last
# bi refresh's, else none. The default was CopilotLicenseEvents, which is the first tenant's: another tenant's
# maintenance read that tenant's license history, and its nightly then failed every night on the change.
if (-not $PSBoundParameters.ContainsKey('LicenseEventDatabases')) {
    $recorded = Invoke-Sql -Scalar -Sql @"
DECLARE @l NVARCHAR(1000), @found BIT = 0;
IF OBJECT_ID(N'audit.CopilotSessionRun', N'U') IS NOT NULL
    SELECT TOP (1) @l = LicenseEventDatabase, @found = 1 FROM audit.CopilotSessionRun ORDER BY RunId DESC;
IF @found = 0 AND OBJECT_ID(N'bi.RefreshRun', N'U') IS NOT NULL
    SELECT TOP (1) @l = LicenseEventDatabase FROM bi.RefreshRun ORDER BY RunId DESC;
SELECT @l;
"@
    $LicenseEventDatabases = if ($recorded -is [DBNull] -or [string]::IsNullOrWhiteSpace([string] $recorded)) { @() }
                             else { @(([string] $recorded) -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ }) }
    if ($Task -in 'Reflatten', 'RebuildSessions', 'RebuildBi', 'SetAppCategory') {
        Write-Host $(if ($LicenseEventDatabases) { "  License history from $($LicenseEventDatabases -join ', '), as $Database's last build (-LicenseEventDatabases to change it)." }
                     else { "  No license history: $Database's last build read none (-LicenseEventDatabases to name it)." }) -ForegroundColor DarkGray
    }
}
$licList = if ($LicenseEventDatabases) { $LicenseEventDatabases -join ',' } else { $null }
function Update-Bi([object] $since) { Invoke-Sql -Sql 'EXEC bi.usp_RefreshBi @SinceUtc = @s, @LicenseEventDatabase = @l;' -Parameters @{ '@s' = $since; '@l' = $licList } }

# The tasks that rebuild hold the database while they work (2.4.12), as the nightly pipeline and Install-CopilotAudit.ps1
# do: an exclusive application lock, on a connection of its own kept open until the task ends. A rebuild that ran beside
# the nightly rebuilt bi under the nightly's own refresh, counting interactions twice.
$runLock = $null
if ($Task -in 'Reflatten', 'RebuildSessions', 'RebuildBi', 'RebuildValueCatalog' -or ($Apply -and $Task -in 'SetAppCategory', 'PurgeJson')) {
    $lb = New-Object System.Data.SqlClient.SqlConnectionStringBuilder
    $lb['Data Source'] = $SqlServer; $lb['Initial Catalog'] = $Database
    if ($SqlCredential) { $lb['User ID'] = $SqlCredential.UserName; $lb['Password'] = $SqlCredential.GetNetworkCredential().Password }
    else { $lb['Integrated Security'] = $true }
    $lb['Encrypt'] = $true; $lb['TrustServerCertificate'] = [bool] $TrustServerCertificate; $lb['Application Name'] = 'Invoke-CopilotMaintenance'
    $lb['Pooling'] = $false   # closing a pooled connection keeps its session, and the session's lock with it
    $runLock = New-Object System.Data.SqlClient.SqlConnection $lb.ConnectionString
    $runLock.Open()
    $lc = $runLock.CreateCommand()
    $lc.CommandText = "DECLARE @r INT; EXEC @r = sp_getapplock @Resource = N'CopilotAudit.Run', @LockMode = 'Exclusive', @LockOwner = 'Session', @LockTimeout = 0; SELECT @r;"
    if ([int] $lc.ExecuteScalar() -lt 0) {
        $runLock.Dispose()
        throw "Another run is using $Database now: the nightly pipeline, the installer or another rebuild. Nothing was changed. Let it finish (Admin > Health shows the pipeline's runs), then run this again."
    }
}

try {
switch ($Task) {
    'Status' {
        $t = Invoke-Sql -Table -Sql @"
SELECT 'Records' AS Item, CAST((SELECT SUM(row_count) FROM sys.dm_db_partition_stats WHERE object_id = OBJECT_ID(N'audit.CopilotAudit') AND index_id IN (0,1)) AS NVARCHAR(40)) AS Value
UNION ALL SELECT 'Flattened', CAST((SELECT SUM(row_count) FROM sys.dm_db_partition_stats WHERE object_id = OBJECT_ID(N'audit.RawLogFlattened') AND index_id IN (0,1)) AS NVARCHAR(40))
UNION ALL SELECT 'Last load merged (UTC)', CASE WHEN OBJECT_ID(N'audit.CopilotLoad') IS NOT NULL THEN (SELECT CONVERT(NVARCHAR(20), MAX(MergedUtc), 120) FROM audit.CopilotLoad) END
UNION ALL SELECT 'Last session build (UTC)', CASE WHEN OBJECT_ID(N'audit.CopilotSessionRun') IS NOT NULL THEN (SELECT CONVERT(NVARCHAR(20), MAX(RunEndUtc), 120) FROM audit.CopilotSessionRun) END
UNION ALL SELECT 'Last bi refresh (UTC)', CASE WHEN OBJECT_ID(N'bi.RefreshRun') IS NOT NULL THEN (SELECT CONVERT(NVARCHAR(20), MAX(EndedUtc), 120) FROM bi.RefreshRun) END
UNION ALL SELECT 'Last dashboard refresh (UTC)', CASE WHEN OBJECT_ID(N'dash.RefreshRun') IS NOT NULL THEN (SELECT CONVERT(NVARCHAR(20), MAX(EndedUtc), 120) FROM dash.RefreshRun) END
UNION ALL SELECT 'Directory people', CASE WHEN OBJECT_ID(N'audit.UserDirectory') IS NOT NULL THEN CAST((SELECT COUNT(*) FROM audit.UserDirectory) AS NVARCHAR(40)) END
UNION ALL SELECT 'Top of the organization', CASE WHEN OBJECT_ID(N'dash.Setting') IS NOT NULL THEN
    (SELECT COALESCE(MAX(CASE WHEN Name = 'OrgTopUpn' THEN NULLIF(Value, N'') END), MAX(CASE WHEN Name = 'OrgTopUpnDetected' THEN Value + N' (from the org chart)' END)) FROM dash.Setting) END
UNION ALL SELECT 'Record shapes', CASE WHEN OBJECT_ID(N'audit.RawLogRecordShape') IS NOT NULL THEN CAST((SELECT COUNT(*) FROM audit.RawLogRecordShape) AS NVARCHAR(40)) END;
"@
        $t.Rows | ForEach-Object { '{0,-30} {1}' -f $_.Item, $(if ($_.Value -is [DBNull]) { '-' } else { $_.Value }) }
    }
    'Reflatten' {
        Invoke-Sql -Sql 'EXEC audit.usp_FlattenCopilotAudit;'
        Invoke-Sql -Sql 'EXEC audit.usp_BuildCopilotSessions @LicenseEventDatabase = @l;' -Parameters @{ '@l' = $licList }
        Update-Bi $null; Update-Dashboard $null
    }
    'RebuildSessions' {
        Invoke-Sql -Sql 'EXEC audit.usp_BuildCopilotSessions @LicenseEventDatabase = @l;' -Parameters @{ '@l' = $licList }
        # After a full session build bi copies every session again; interactions from today on only.
        Update-Bi ([datetime]::UtcNow.Date); Update-Dashboard $null
    }
    'RebuildBi' { Update-Bi $null; Update-Dashboard $null }
    'RefreshDashboard' { Update-Dashboard $(if ($PSBoundParameters.ContainsKey('SinceUtc')) { $SinceUtc } else { $null }) }
    'SetAppCategory' {
        Need 'App' $App; Need 'Category' $Category
        Invoke-Sql -Parameters @{ '@a' = $App; '@c' = $Category } -Sql @"
UPDATE audit.SessionAppCategory SET AppCategory = @c WHERE App = @a;
IF @@ROWCOUNT = 0 INSERT INTO audit.SessionAppCategory (App, AppCategory) VALUES (@a, @c);
"@
        Write-Host "$App is $Category."
        if ($Apply) {
            Invoke-Sql -Sql 'EXEC audit.usp_BuildCopilotSessions @LicenseEventDatabase = @l;' -Parameters @{ '@l' = $licList }
            Update-Bi $null; Update-Dashboard $null
        }
        else { Write-Host 'It applies from the next load; add -Apply to rebuild sessions and bi with it now.' }
    }
    'GrantRole' {
        Need 'Principal' $Principal; Need 'Role' $Role
        if (-not $Principal.Contains('@') -and -not $Principal.Contains('\')) { throw '-Principal is a UPN, DOMAIN\user or DOMAIN\Group.' }
        # DOMAIN\name: a group if AD says so, else a user.
        $type = if ($PrincipalType) { $PrincipalType } else { 'User' }
        if (-not $PrincipalType -and $Principal.Contains('\')) {
            try {
                Add-Type -AssemblyName System.DirectoryServices.AccountManagement
                $pc = New-Object System.DirectoryServices.AccountManagement.PrincipalContext ([System.DirectoryServices.AccountManagement.ContextType]::Domain), $Principal.Split('\')[0]
                if ([System.DirectoryServices.AccountManagement.GroupPrincipal]::FindByIdentity($pc, $Principal)) { $type = 'Group' }
            }
            catch { Write-Warning "Could not ask AD whether $Principal is a group; recorded as a user (-PrincipalType Group to say otherwise). The dashboard matches it either way." }
        }
        Invoke-Sql -Parameters @{ '@p' = $Principal; '@t' = $type; '@r' = $Role; '@n' = $Note; '@by' = $me } -Sql @"
IF NOT EXISTS (SELECT 1 FROM dash.RoleGrant WHERE Principal = @p AND Role = @r)
    INSERT INTO dash.RoleGrant (Principal, PrincipalType, Role, Note, GrantedBy) VALUES (@p, @t, @r, @n, @by);
INSERT INTO dash.AuditLog (Viewer, Kind, Page, Detail) VALUES (@by, 'Admin', N'Invoke-CopilotMaintenance', CONCAT(N'Granted ', @r, N' to ', @p));
"@
        Write-Host "$Principal ($type) has $Role."
    }
    'RevokeRole' {
        Need 'Principal' $Principal; Need 'Role' $Role
        Invoke-Sql -Parameters @{ '@p' = $Principal; '@r' = $Role; '@by' = $me } -Sql @"
DELETE FROM dash.RoleGrant WHERE Principal = @p AND Role = @r;
INSERT INTO dash.AuditLog (Viewer, Kind, Page, Detail) VALUES (@by, 'Admin', N'Invoke-CopilotMaintenance', CONCAT(N'Removed ', @r, N' from ', @p));
"@
        Write-Host "$Principal no longer has $Role."
    }
    'SetOrgTop' {
        if (-not $Clear) {
            Need 'Upn' $Upn
            $known = [int] (Invoke-Sql -Scalar -Sql 'SELECT COUNT(*) FROM dash.Person WHERE Upn = @u' -Parameters @{ '@u' = $Upn.Trim() })
            if ($known -eq 0) { throw "$Upn is not in the directory the dashboard knows (dash.Person). Load the directory first, or check the UPN." }
        }
        Invoke-Sql -Parameters @{ '@v' = $(if ($Clear) { $null } else { $Upn.Trim() }); '@by' = $me } -Sql @"
UPDATE dash.Setting SET Value = @v, UpdatedBy = @by, UpdatedUtc = SYSUTCDATETIME() WHERE Name = 'OrgTopUpn';
INSERT INTO dash.AuditLog (Viewer, Kind, Page, Detail) VALUES (@by, 'Admin', N'Invoke-CopilotMaintenance', CONCAT(N'OrgTopUpn -> ', ISNULL(@v, N'(from the org chart)')));
"@
        Update-Dashboard ([datetime]::UtcNow.Date)
    }
    'SetDashboardSetting' {
        Need 'Name' $Name; Need 'Value' $Value
        if ($Name -eq 'MaxViewerLevel' -and $Value -notmatch '^(0|[1-9]|1[0-5])$') { throw 'MaxViewerLevel is 0 (everyone) or a level from 1 to 15.' }
        if ($Name -eq 'ProtectedLevels' -and $Value -notmatch '^(0|[1-9]|1[0-5])$') { throw 'ProtectedLevels is 0 (none) or a level from 1 to 15.' }
        Invoke-Sql -Parameters @{ '@n' = $Name; '@v' = $Value; '@by' = $me } -Sql @"
UPDATE dash.Setting SET Value = @v, UpdatedBy = @by, UpdatedUtc = SYSUTCDATETIME() WHERE Name = @n;
IF @@ROWCOUNT = 0 INSERT INTO dash.Setting (Name, Value, UpdatedBy) VALUES (@n, @v, @by);
INSERT INTO dash.AuditLog (Viewer, Kind, Page, Detail) VALUES (@by, 'Admin', N'Invoke-CopilotMaintenance', CONCAT(@n, N' -> ', @v));
"@
        Write-Host "$Name = $Value."
    }
    'NameAgent' {
        Need 'AgentId' $AgentId
        $key = Invoke-Sql -Scalar -Sql 'SELECT AgentKey FROM bi.DimAgent WHERE AgentId = @a' -Parameters @{ '@a' = $AgentId }
        if ($null -eq $key -or $key -is [DBNull]) { throw "No agent $AgentId in bi.DimAgent." }
        Invoke-Sql -Parameters @{ '@k' = $key; '@n' = $FriendlyName; '@o' = $Owner; '@by' = $me } -Sql @"
IF NULLIF(@n, N'') IS NULL DELETE FROM dash.AgentAlias WHERE AgentKey = @k;
ELSE BEGIN
    UPDATE dash.AgentAlias SET FriendlyName = @n, Owner = @o, UpdatedBy = @by, UpdatedUtc = SYSUTCDATETIME() WHERE AgentKey = @k;
    IF @@ROWCOUNT = 0 INSERT INTO dash.AgentAlias (AgentKey, FriendlyName, Owner, UpdatedBy) VALUES (@k, @n, @o, @by);
END;
"@
        Write-Host "Agent $AgentId named."
    }
    'NameLabel' {
        Need 'LabelId' $LabelId; Need 'Name' $FriendlyName
        Invoke-Sql -Parameters @{ '@i' = $LabelId.ToLowerInvariant(); '@n' = $FriendlyName; '@r' = $(if ($Rank -ge 0) { $Rank } else { $null }); '@by' = $me } -Sql @"
UPDATE dash.SensitivityLabel SET Name = @n, Rank = @r, UpdatedBy = @by, UpdatedUtc = SYSUTCDATETIME() WHERE LabelId = @i;
IF @@ROWCOUNT = 0 INSERT INTO dash.SensitivityLabel (LabelId, Name, Rank, UpdatedBy) VALUES (@i, @n, @r, @by);
"@
        Write-Host "Label $LabelId is $FriendlyName."
    }
    'RebuildValueCatalog' {
        if (-not (Test-Proc 'audit.usp_CatalogValues')) { throw 'audit.usp_CatalogValues is not installed: run Install-CopilotAudit.ps1 from this release first.' }
        Write-Host 'Reading every flattened record; on a large database this takes a while.'
        Invoke-Sql -Sql 'EXEC audit.usp_CatalogValues @Rebuild = 1;'
        (Invoke-Sql -Table -Sql 'SELECT ColumnId, Label, DistinctValues, Capped FROM audit.ValueCatalogColumn WHERE DistinctValues > 0 ORDER BY ColumnId;').Rows |
            Format-Table -AutoSize | Out-String | Write-Host
    }
    'ListValues' {
        if (-not $PSBoundParameters.ContainsKey('ColumnId')) {
            (Invoke-Sql -Table -Sql 'SELECT ColumnId, Label, TableName + N''.'' + ColumnName AS [Column], DistinctValues, CASE WHEN Capped = 1 THEN N''10,000 or more'' ELSE N'''' END AS Capped FROM audit.ValueCatalogColumn ORDER BY ColumnId;').Rows |
                Format-Table -AutoSize | Out-String | Write-Host
            Write-Host 'Add -ColumnId to list one column''s values, and -Path to write them to a CSV.'
            break
        }
        $t = Invoke-Sql -Table -Parameters @{ '@c' = $ColumnId } -Sql @"
SELECT v.ColumnId, v.Value, COALESCE(n.Name, CASE WHEN v.ColumnId IN (11, 30) THEN sl.Name END) AS Name, n.Note, v.Records
FROM audit.ValueCatalog AS v
LEFT JOIN dash.ValueName AS n ON n.ColumnId = v.ColumnId AND n.Value = v.Value
LEFT JOIN dash.SensitivityLabel AS sl ON sl.LabelId = v.Value
WHERE v.ColumnId = @c ORDER BY v.Records DESC;
"@
        if ($Path) {
            $t.Rows | Select-Object ColumnId, Value, Name, Note | Export-Csv -LiteralPath $Path -NoTypeInformation -Encoding utf8
            Write-Host "$($t.Rows.Count) value(s) written to $Path. Fill in Name, then run -Task ImportValueNames -Path $Path."
        }
        else { $t.Rows | Format-Table -AutoSize | Out-String | Write-Host }
    }
    'ImportValueNames' {
        Need 'Path' $Path
        $n = 0; $cleared = 0
        # Not $name: PowerShell names are case-insensitive, and -Name only takes the setting names.
        foreach ($r in Import-Csv -LiteralPath $Path) {
            $col = [int] $r.ColumnId
            $valueName = if ($r.PSObject.Properties['Name'] -and "$($r.Name)".Trim()) { "$($r.Name)".Trim() } else { $null }
            $valueNote = if ($r.PSObject.Properties['Note'] -and "$($r.Note)".Trim()) { "$($r.Note)".Trim() } else { $null }
            $known = [int] (Invoke-Sql -Scalar -Sql 'SELECT COUNT(*) FROM audit.ValueCatalog WHERE ColumnId = @c AND Value = @v' -Parameters @{ '@c' = $col; '@v' = $r.Value })
            if (-not $known) { Write-Warning "Skipped: '$($r.Value)' is not a value of column $col."; continue }
            Invoke-Sql -Parameters @{ '@c' = $col; '@v' = $r.Value; '@n' = $valueName; '@note' = $valueNote; '@by' = $me } -Sql @"
IF @n IS NULL DELETE FROM dash.ValueName WHERE ColumnId = @c AND Value = @v;
ELSE BEGIN
    UPDATE dash.ValueName SET Name = @n, Note = @note, UpdatedBy = @by, UpdatedUtc = SYSUTCDATETIME() WHERE ColumnId = @c AND Value = @v;
    IF @@ROWCOUNT = 0 INSERT INTO dash.ValueName (ColumnId, Value, Name, Note, UpdatedBy) VALUES (@c, @v, @n, @note, @by);
END;
/* Label names also live with their order in dash.SensitivityLabel. */
IF @c IN (11, 30) AND @n IS NOT NULL
BEGIN
    UPDATE dash.SensitivityLabel SET Name = @n, UpdatedBy = @by, UpdatedUtc = SYSUTCDATETIME() WHERE LabelId = @v;
    IF @@ROWCOUNT = 0 INSERT INTO dash.SensitivityLabel (LabelId, Name, UpdatedBy) VALUES (@v, @n, @by);
END;
"@
            if ($valueName) { $n++ } else { $cleared++ }
        }
        Write-Host "$n name(s) saved, $cleared cleared."
    }
    'PurgeJson' {
        # 2.4.12: -DeleteRows removes the flattened rows too, with the resources and contexts they read. It kept them,
        # each naming the user and the files and links they read, though the records were said to be gone.
        $t = Invoke-Sql -Table -Sql 'EXEC audit.usp_PurgeCopilotAuditJson @RetainDays = @d, @WhatIf = @w, @DeleteRows = @r, @PurgeFlattened = @r;' `
            -Parameters @{ '@d' = $RetainDays; '@w' = [int] (-not $Apply); '@r' = [int] [bool] $DeleteRows }
        $t.Rows | Format-Table -AutoSize | Out-String | Write-Host
        if (-not $Apply) { Write-Host 'Nothing was cleared; add -Apply to clear it.' }
    }
}
}
finally { if ($runLock) { $runLock.Dispose() } }
