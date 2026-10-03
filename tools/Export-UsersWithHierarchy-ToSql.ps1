<#
.SYNOPSIS
    Loads the user directory from on-premises Active Directory into
    audit.UserDirectory: each user's title, department, division, office,
    manager and management chain, which reporting on the audit data is sliced
    by.

.DESCRIPTION
    One pass over a TRUSTED domain, loaded straight into SQL Server with
    SqlBulkCopy. Per user:
      - identity, job and location attributes (see audit.UserDirectory in
        sql/Create-CopilotEnrichmentSchema.sql)
      - the direct manager, and the management chain Mgr1 (the top, e.g. the
        CEO) ... Mgr<OrgDepth> (the direct manager)

    Every account is loaded, enabled or not, with AccountEnabled saying
    which: the audit data covers months, and a user who has since left still
    has interactions to attribute.

    Needs no Microsoft Graph permission. The AD bind uses the identity running
    the script, over the domain trust (no credentials); SQL uses the same
    identity by default. Runs on Windows PowerShell 5.1 or PowerShell 7 - .NET
    only, no module.

    The table is replaced whole, in one transaction, so a reader never sees it
    empty or half-loaded. Licenses are not here: they come from Microsoft
    Graph, by tools/Update-CopilotAuditEnrichment.ps1.

    After loading it reports how many of the users in the audit data it found,
    by UserPrincipalName and by mail. The audit data is joined on the UPN, so
    a low match means AD's UPN is not the one users sign in to Microsoft 365
    with.

.PARAMETER TargetDomain
    The trusted domain's DNS name, or a domain controller in it. Must not be
    the domain the script runs in.

.PARAMETER SqlServer / -Database / -ConnectionString / -SqlCredential / -TrustServerCertificate
    Where the audit database lives. Same shape as Invoke-CopilotAuditBackfill.ps1.

.PARAMETER MaxLevels
    How many levels of the management chain to keep, from the top (1-15,
    default 15). OrgDepth always records the full depth.

.PARAMETER EmployeeTypeMap
    employeeType code -> the value written. Codes not listed pass through.

.PARAMETER EmployeeTypes
    Load only these employeeType codes. An empty array loads every type.
    Managers are resolved for the chain whatever their type.

.EXAMPLE
    .\tools\Export-UsersWithHierarchy-ToSql.ps1 -TargetDomain trusted.example.com `
        -SqlServer SQLPROD01 -Database CopilotAudit
#>
[CmdletBinding()]
param(
    [string]   $TargetDomain,

    [string]   $SqlServer,
    [string]   $Database,
    [string]   $ConnectionString,
    [pscredential] $SqlCredential,
    [switch]   $TrustServerCertificate,

    [ValidateRange(1, 15)] [int] $MaxLevels = 15,
    [int]      $BatchSize = 10000,

    [hashtable] $EmployeeTypeMap = @{ 'EMP' = 'EMPLOYEE'; 'NONW' = 'CONTRACTOR' },
    [string[]] $EmployeeTypes = @('EMP', 'NONW')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$script:ToolRoot = Split-Path $PSScriptRoot -Parent
$script:ChainColumns = 15      # Mgr1..Mgr15 in audit.UserDirectory

function Write-Log {
    param([Parameter(Mandatory)][string] $Message,
          [ValidateSet('INFO', 'WARN', 'ERROR')][string] $Level = 'INFO')
    $line = '{0:yyyy-MM-dd HH:mm:ss}Z [{1,-5}] {2}' -f [datetime]::UtcNow, $Level, $Message
    switch ($Level) {
        'ERROR' { Write-Host $line -ForegroundColor Red }
        'WARN'  { Write-Host $line -ForegroundColor Yellow }
        default { Write-Host $line }
    }
}

# ===========================================================================
# 1. Active Directory
# ===========================================================================

function Get-DirectoryEntry {
    <#
        Every user object in the domain, with the attributes the table needs.
        Returns plain objects, so everything after this can be tested without
        a directory.
    #>
    param([Parameter(Mandatory)][string] $Domain)

    $current = $env:USERDNSDOMAIN
    if ($current -and ($Domain -ieq $current)) {
        throw "TargetDomain ($Domain) is the domain this script runs in. Point it at the trusted domain."
    }
    $base = ([ADSI]"LDAP://$Domain/RootDSE").defaultNamingContext
    if (-not $base) { throw "Could not read RootDSE from $Domain. Check the name and the trust." }

    $searcher = [System.DirectoryServices.DirectorySearcher]::new(
        [ADSI]"LDAP://$Domain/$base",
        '(&(objectCategory=person)(objectClass=user))')
    $searcher.PageSize     = 1000
    $searcher.SizeLimit    = 0
    $searcher.CacheResults = $false
    foreach ($a in 'userPrincipalName', 'mail', 'displayName', 'title', 'department', 'division',
                   'physicalDeliveryOfficeName', 'l', 'co', 'company', 'employeeType', 'manager',
                   'distinguishedName', 'userAccountControl', 'objectSid', 'accountExpires') {
        [void] $searcher.PropertiesToLoad.Add($a)
    }

    function Val($p, $n) { if ($p.Contains($n)) { [string] $p[$n][0] } else { $null } }
    function Sid($p) {
        if (-not $p.Contains('objectsid')) { return $null }
        ([System.Security.Principal.SecurityIdentifier]::new([byte[]] $p['objectsid'][0], 0)).Value
    }

    $all = [System.Collections.Generic.List[object]]::new(100000)
    $results = $searcher.FindAll()
    try {
        foreach ($r in $results) {
            $p = $r.Properties
            $all.Add([pscustomobject]@{
                dn      = Val $p 'distinguishedname'
                upn     = Val $p 'userprincipalname'
                mail    = Val $p 'mail'
                disp    = Val $p 'displayname'
                title   = Val $p 'title'
                dept    = Val $p 'department'
                div     = Val $p 'division'
                off     = Val $p 'physicaldeliveryofficename'
                city    = Val $p 'l'
                country = Val $p 'co'
                company = Val $p 'company'
                etype   = Val $p 'employeetype'
                mgr     = Val $p 'manager'
                uac     = [int] (Val $p 'useraccountcontrol')
                sid     = Sid $p
                expires = ConvertFrom-AccountExpiry (Val $p 'accountexpires')
            })
        }
    }
    finally { $results.Dispose() }
    return , $all
}

function ConvertFrom-AccountExpiry {
    <# AD's accountExpires (a FILETIME, as text) as a UTC date, or $null for never (0 or the largest Int64). Pure, for the tests. #>
    param([AllowNull()][AllowEmptyString()][string] $Value)
    $ticks = 0L
    if (-not [long]::TryParse($Value, [ref] $ticks) -or $ticks -le 0 -or $ticks -eq [long]::MaxValue) { return $null }
    try { [DateTime]::FromFileTimeUtc($ticks) } catch { $null }
}

function ConvertTo-LdapFilterValue {
    <# A value escaped for an LDAP filter (RFC 4515). Pure, for the tests. #>
    param([Parameter(Mandatory)][AllowEmptyString()][string] $Value)
    $sb = [System.Text.StringBuilder]::new()
    foreach ($ch in $Value.ToCharArray()) {
        switch ($ch) {
            '\' { [void] $sb.Append('\5c') }
            '*' { [void] $sb.Append('\2a') }
            '(' { [void] $sb.Append('\28') }
            ')' { [void] $sb.Append('\29') }
            ([char] 0) { [void] $sb.Append('\00') }
            default { [void] $sb.Append($ch) }
        }
    }
    $sb.ToString()
}

function Get-GroupMemberUpn {
    <#
        The UPNs of every user in an AD group, nested groups included (the in-chain matching
        rule). The group is DOMAIN\Name as the dashboard grants it; it is looked up by its
        account name in the target domain. Returns $null when there is no such group.
    #>
    param([Parameter(Mandatory)][string] $Domain, [Parameter(Mandatory)][string] $Group)
    $name = if ($Group.Contains('\')) { $Group.Substring($Group.IndexOf('\') + 1) } else { $Group }
    $base = ([ADSI]"LDAP://$Domain/RootDSE").defaultNamingContext
    $find = [System.DirectoryServices.DirectorySearcher]::new([ADSI]"LDAP://$Domain/$base",
        "(&(objectCategory=group)(sAMAccountName=$(ConvertTo-LdapFilterValue $name)))")
    [void] $find.PropertiesToLoad.Add('distinguishedName')
    $g = $find.FindOne()
    if (-not $g) { return $null }
    $dn = [string] $g.Properties['distinguishedname'][0]
    $members = [System.DirectoryServices.DirectorySearcher]::new([ADSI]"LDAP://$Domain/$base",
        "(&(objectCategory=person)(objectClass=user)(memberOf:1.2.840.113556.1.4.1941:=$(ConvertTo-LdapFilterValue $dn)))")
    $members.PageSize = 1000
    $members.SizeLimit = 0
    [void] $members.PropertiesToLoad.Add('userPrincipalName')
    $upns = [System.Collections.Generic.List[string]]::new()
    $results = $members.FindAll()
    try { foreach ($r in $results) { if ($r.Properties.Contains('userprincipalname')) { $upns.Add([string] $r.Properties['userprincipalname'][0]) } } }
    finally { $results.Dispose() }
    , $upns.ToArray()
}

function ConvertTo-GroupMemberTable {
    <# (group, UPNs) pairs to audit.GroupMember rows, each pair once. Pure, for the tests. #>
    param([Parameter(Mandatory)][AllowEmptyCollection()][object[]] $Groups)
    $t = [System.Data.DataTable]::new()
    [void] $t.Columns.Add('GroupName', [string])
    [void] $t.Columns.Add('UserPrincipalName', [string])
    $seen = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($g in $Groups) {
        foreach ($u in @($g.Members)) {
            if ([string]::IsNullOrWhiteSpace($u) -or $u.Length -gt 256) { continue }
            if ($seen.Add("$($g.Group)|$u")) { [void] $t.Rows.Add($g.Group, $u) }
        }
    }
    , $t
}

# ===========================================================================
# 2. Rows: filters, employee type, the management chain
# ===========================================================================

function ConvertTo-UserDirectoryTable {
    <#
        Turns directory entries into the rows of audit.UserDirectory, as a
        DataTable ready for SqlBulkCopy. Pure: no directory, no SQL.

        A user is loaded when they have a UPN (the join key to the audit data)
        and pass the employee-type filter, enabled or not. Managers are looked
        up among ALL entries, so a chain runs through a manager of a type that
        is not loaded. Someone with no UPN cannot have used Copilot and is left
        out of the chain too (2.4.1): a person whose manager has no UPN reports
        to the nearest manager above who has one, so the org chart stays joined.
        OrgDepth still counts that manager (2.4.12), so the person keeps their
        real level.
    #>
    param(
        [Parameter(Mandatory)][AllowEmptyCollection()][object[]] $Entries,
        [ValidateRange(1, 15)][int] $MaxLevels = 15,
        [hashtable] $EmployeeTypeMap = @{},
        [string[]] $EmployeeTypes = @()
    )

    $byDn = [System.Collections.Generic.Dictionary[string, object]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($e in $Entries) { if ($e.dn) { $byDn[$e.dn] = $e } }
    $hasUpn = { param($e) -not [string]::IsNullOrWhiteSpace($e.upn) }
    # The manager a person reports to in the org chart: the nearest one up the chain with a UPN.
    $upnManager = {
        param($e)
        $at = $e.mgr; $been = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        while ($at -and $been.Add($at)) {
            $n = $null
            if (-not $byDn.TryGetValue($at, [ref] $n)) { return $null }
            if (& $hasUpn $n) { return $n }
            $at = $n.mgr
        }
        $null
    }
    # Managers in the org chart: whoever someone with a UPN reports to, over any managers without one.
    $managerDns = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($e in $Entries) {
        if (& $hasUpn $e) { $mm = & $upnManager $e; if ($mm) { [void] $managerDns.Add([string] $mm.dn) } }
    }

    $dt = [System.Data.DataTable]::new()
    foreach ($c in 'UserPrincipalName', 'Mail', 'FullName', 'EmployeeType', 'Sid') { [void] $dt.Columns.Add($c, [string]) }
    [void] $dt.Columns.Add('IsManager', [bool])
    [void] $dt.Columns.Add('AccountEnabled', [bool])
    foreach ($c in 'JobTitle', 'Department', 'Division', 'Office', 'City', 'Country', 'Company', 'ManagerUPN', 'ManagerName') {
        [void] $dt.Columns.Add($c, [string])
    }
    [void] $dt.Columns.Add('OrgDepth', [byte])
    [void] $dt.Columns.Add('AccountExpiresUtc', [datetime])
    for ($i = 1; $i -le $script:ChainColumns; $i++) { [void] $dt.Columns.Add("Mgr$i", [string]) }

    $wantTypes = [System.Collections.Generic.HashSet[string]]::new([string[]] @($EmployeeTypes), [StringComparer]::OrdinalIgnoreCase)
    $loaded    = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $chain     = [System.Collections.Generic.List[string]]::new()
    $seen      = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $skipped   = [ordered]@{ OtherType = 0; NoUpn = 0; UpnTooLong = 0; DuplicateUpn = 0 }

    function NZ($v) { if ([string]::IsNullOrEmpty([string] $v)) { [DBNull]::Value } else { [string] $v } }

    foreach ($u in $Entries) {
        $enabled = (($u.uac -band 2) -eq 0)      # ACCOUNTDISABLE
        if ($wantTypes.Count -gt 0 -and -not ($u.etype -and $wantTypes.Contains($u.etype))) { $skipped.OtherType++; continue }
        if ([string]::IsNullOrWhiteSpace($u.upn)) { $skipped.NoUpn++; continue }
        if ($u.upn.Length -gt 256) { $skipped.UpnTooLong++; continue }
        if (-not $loaded.Add($u.upn)) { $skipped.DuplicateUpn++; continue }

        $m = & $upnManager $u

        $et = if ($u.etype -and $EmployeeTypeMap.ContainsKey($u.etype)) { $EmployeeTypeMap[$u.etype] } else { $u.etype }

        # The chain, walked up from the direct manager and reversed so the top
        # comes first. $seen stops a loop in the manager attributes.
        # OrgDepth counts every manager above, those without a UPN too (2.4.12): it counted only the chain's, so a
        # person under a manager without a UPN looked a level higher than they are, and the dashboard takes levels
        # from that depth, where level 2 sees the whole organization.
        $chain.Clear(); $seen.Clear(); $depth = 0
        $cur = $u.mgr
        while ($cur -and $seen.Add($cur) -and $depth -lt 100) {
            $node = $null
            if (-not $byDn.TryGetValue($cur, [ref] $node)) { break }
            $depth++
            if (& $hasUpn $node) { $chain.Add($node.disp) }   # no UPN: not in the org chart
            $cur = $node.mgr
        }
        $chain.Reverse()

        $row = $dt.NewRow()
        $row['UserPrincipalName'] = $u.upn
        $row['AccountExpiresUtc'] = if ($u.PSObject.Properties['expires'] -and $u.expires) { [datetime] $u.expires } else { [DBNull]::Value }
        $row['Mail']              = NZ $u.mail
        $row['FullName']          = NZ $u.disp
        $row['EmployeeType']      = NZ $et
        $row['Sid']               = NZ $(if ($u.PSObject.Properties['sid']) { $u.sid })
        $row['IsManager']         = $managerDns.Contains([string] $u.dn)
        $row['AccountEnabled']    = $enabled
        $row['JobTitle']          = NZ $u.title
        $row['Department']        = NZ $u.dept
        $row['Division']          = NZ $u.div
        $row['Office']            = NZ $u.off
        $row['City']              = NZ $u.city
        $row['Country']           = NZ $u.country
        $row['Company']           = NZ $u.company
        $row['ManagerUPN']        = NZ $(if ($m) { $m.upn })
        $row['ManagerName']       = NZ $(if ($m) { $m.disp })
        $row['OrgDepth']          = [byte] [math]::Min($depth, 255)
        for ($i = 0; $i -lt $MaxLevels -and $i -lt $chain.Count; $i++) { $row["Mgr$($i + 1)"] = NZ $chain[$i] }
        $dt.Rows.Add($row)
    }

    return [pscustomobject]@{ Table = $dt; Skipped = $skipped }
}

# ===========================================================================
# 3. SQL
# ===========================================================================

function New-DirectoryConnectionString {
    # See Invoke-CopilotAuditBackfill.ps1: assigning .ConnectionString goes
    # through the builder's keyword indexer and throws.
    $csb = if ($ConnectionString) {
        [System.Data.SqlClient.SqlConnectionStringBuilder]::new($ConnectionString)
    }
    else {
        [System.Data.SqlClient.SqlConnectionStringBuilder]::new()
    }
    if (-not $ConnectionString) {
        if (-not $SqlServer -or -not $Database) { throw 'Specify -SqlServer and -Database (or -ConnectionString).' }
        $csb['Data Source'] = $SqlServer
        $csb['Initial Catalog'] = $Database
        if ($SqlCredential) {
            $csb['User ID'] = $SqlCredential.UserName
            $csb['Password'] = $SqlCredential.GetNetworkCredential().Password
        }
        else { $csb['Integrated Security'] = $true }
        $csb['Encrypt'] = $true
        $csb['TrustServerCertificate'] = [bool] $TrustServerCertificate
    }
    $csb['Application Name'] = 'CopilotAuditUserDirectory'
    return $csb.ConnectionString
}

function Write-UserDirectory {
    <#
        Creates the enrichment tables if needed, then replaces
        audit.UserDirectory with the rows given - TRUNCATE and bulk load in
        one transaction, so a reader sees the old table or the new one, never
        an empty or partial one.
    #>
    param(
        [Parameter(Mandatory)][string] $ConnString,
        [Parameter(Mandatory)][System.Data.DataTable] $Table,
        [int] $BatchRows = 10000
    )
    $conn = [System.Data.SqlClient.SqlConnection]::new($ConnString)
    $conn.Open()
    try {
        $cmd = $conn.CreateCommand()
        $cmd.CommandTimeout = 300
        $cmd.CommandText = Get-Content -LiteralPath (Join-Path (Join-Path $script:ToolRoot 'sql') 'Create-CopilotEnrichmentSchema.sql') -Raw
        [void] $cmd.ExecuteNonQuery()

        $tx = $conn.BeginTransaction()
        try {
            $cmd = $conn.CreateCommand()
            $cmd.Transaction = $tx
            $cmd.CommandText = 'TRUNCATE TABLE audit.UserDirectory;'
            [void] $cmd.ExecuteNonQuery()

            $bulk = [System.Data.SqlClient.SqlBulkCopy]::new($conn, [System.Data.SqlClient.SqlBulkCopyOptions]::TableLock, $tx)
            $bulk.DestinationTableName = 'audit.UserDirectory'
            $bulk.BatchSize            = $BatchRows
            $bulk.BulkCopyTimeout      = 0
            foreach ($c in $Table.Columns) { [void] $bulk.ColumnMappings.Add($c.ColumnName, $c.ColumnName) }
            $bulk.WriteToServer($Table)
            $bulk.Close()
            $tx.Commit()
        }
        catch { $tx.Rollback(); throw }
    }
    finally { $conn.Dispose() }
    return $Table.Rows.Count
}

function Get-OrganizationGroup {
    <#
        The AD groups whose members the dashboard needs, if it is installed: those granted Organization (for
        Power BI's row-level security), and those an administrator listed as marking non-human accounts (2.4.5,
        the NonHumanGroups setting, separated by commas, semicolons or lines).
    #>
    param([Parameter(Mandatory)][string] $ConnString)
    $conn = [System.Data.SqlClient.SqlConnection]::new($ConnString)
    $conn.Open()
    try {
        $cmd = $conn.CreateCommand()
        $cmd.CommandText = @'
IF OBJECT_ID(N'dash.RoleGrant', N'U') IS NOT NULL
    EXEC (N'SELECT DISTINCT Principal FROM dash.RoleGrant WHERE PrincipalType = ''Group'' AND Role = ''Organization''
            UNION SELECT LTRIM(RTRIM(g.value)) FROM dash.Setting AS s
            CROSS APPLY STRING_SPLIT(REPLACE(REPLACE(ISNULL(s.Value, N''''), N'';'', N'',''), CHAR(10), N'',''), N'','') AS g
            WHERE s.Name = N''NonHumanGroups'' AND LTRIM(RTRIM(g.value)) <> N'''';');
'@
        $r = $cmd.ExecuteReader()
        $list = [System.Collections.Generic.List[string]]::new()
        try { while ($r.Read()) { $list.Add($r.GetString(0)) } } finally { $r.Dispose() }
        , $list.ToArray()
    }
    finally { $conn.Dispose() }
}

function Write-GroupMember {
    <# Replaces audit.GroupMember with the rows given, in one transaction. #>
    param([Parameter(Mandatory)][string] $ConnString, [Parameter(Mandatory)][System.Data.DataTable] $Table)
    $conn = [System.Data.SqlClient.SqlConnection]::new($ConnString)
    $conn.Open()
    try {
        $tx = $conn.BeginTransaction()
        try {
            $cmd = $conn.CreateCommand(); $cmd.Transaction = $tx
            $cmd.CommandText = 'DELETE FROM audit.GroupMember;'
            [void] $cmd.ExecuteNonQuery()
            $bulk = [System.Data.SqlClient.SqlBulkCopy]::new($conn, [System.Data.SqlClient.SqlBulkCopyOptions]::Default, $tx)
            $bulk.DestinationTableName = 'audit.GroupMember'
            foreach ($c in $Table.Columns) { [void] $bulk.ColumnMappings.Add($c.ColumnName, $c.ColumnName) }
            $bulk.WriteToServer($Table)
            $bulk.Close()
            $tx.Commit()
        }
        catch { $tx.Rollback(); throw }
    }
    finally { $conn.Dispose() }
    $Table.Rows.Count
}

function Get-DirectoryMatch {
    <#
        How many of the users in the audit data the directory has a row for,
        by UPN (what everything joins on) and, for the rest, by mail.
    #>
    param([Parameter(Mandatory)][string] $ConnString)
    $conn = [System.Data.SqlClient.SqlConnection]::new($ConnString)
    $conn.Open()
    try {
        $cmd = $conn.CreateCommand()
        $cmd.CommandTimeout = 600
        $cmd.CommandText = @'
IF OBJECT_ID(N'audit.CopilotInteraction', N'U') IS NULL
    SELECT CAST(0 AS INT) AS AuditUsers, CAST(0 AS INT) AS ByUpn, CAST(0 AS INT) AS ByMailOnly;
ELSE
    EXEC (N'
    SELECT COUNT(*) AS AuditUsers, ISNULL(SUM(x.ByUpn), 0) AS ByUpn, ISNULL(SUM(x.ByMailOnly), 0) AS ByMailOnly
    FROM (
        SELECT CASE WHEN d.UserPrincipalName IS NOT NULL THEN 1 ELSE 0 END AS ByUpn,
               CASE WHEN d.UserPrincipalName IS NULL
                         AND EXISTS (SELECT 1 FROM audit.UserDirectory AS m WHERE m.Mail = u.Upn) THEN 1 ELSE 0 END AS ByMailOnly
        FROM (SELECT DISTINCT UserPrincipalName AS Upn FROM audit.CopilotInteraction WHERE UserPrincipalName IS NOT NULL) AS u
        LEFT JOIN audit.UserDirectory AS d ON d.UserPrincipalName = u.Upn
    ) AS x;');
'@
        $r = $cmd.ExecuteReader()
        try {
            [void] $r.Read()
            return [pscustomobject]@{ AuditUsers = [int] $r['AuditUsers']; ByUpn = [int] $r['ByUpn']; ByMailOnly = [int] $r['ByMailOnly'] }
        }
        finally { $r.Dispose() }
    }
    finally { $conn.Dispose() }
}

# ===========================================================================
# Main
# ===========================================================================

function Invoke-Main {
    if (-not $TargetDomain) { throw 'Specify -TargetDomain: the trusted domain to read users from.' }
    $connStr = New-DirectoryConnectionString

    Write-Log "Reading users from $TargetDomain..."
    $entries = Get-DirectoryEntry -Domain $TargetDomain
    Write-Log ("{0:N0} user object(s) read." -f $entries.Count)

    $rows = ConvertTo-UserDirectoryTable -Entries $entries -MaxLevels $MaxLevels -EmployeeTypeMap $EmployeeTypeMap `
                                         -EmployeeTypes $EmployeeTypes
    $s = $rows.Skipped
    $disabled = @($rows.Table.Rows | Where-Object { -not $_.AccountEnabled }).Count
    Write-Log ("{0:N0} row(s) to load, {1:N0} of them disabled accounts; skipped {2:N0} of another employee type, {3:N0} with no UPN, {4:N0} duplicate UPN, {5:N0} UPN over 256 characters." -f `
               $rows.Table.Rows.Count, $disabled, $s.OtherType, $s.NoUpn, $s.DuplicateUpn, $s.UpnTooLong)

    $n = Write-UserDirectory -ConnString $connStr -Table $rows.Table -BatchRows $BatchSize
    Write-Log ("Loaded {0:N0} row(s) into audit.UserDirectory." -f $n)

    # Organization granted to AD groups: their members, for Power BI's row-level security (bi.OrgSecurity);
    # and the groups that mark non-human accounts (2.4.5), for the dashboard's policy-breach check.
    # Written every night, even with no groups, so a revoked grant's members do not linger.
    # Get-OrganizationGroup returns its array whole (unary comma), so @() here would wrap it in a second array.
    [string[]] $groups = Get-OrganizationGroup -ConnString $connStr
    $expanded = @(foreach ($g in $groups) {
        $members = Get-GroupMemberUpn -Domain $TargetDomain -Group $g
        if ($null -eq $members) { Write-Log "Group $g, granted Organization or listed as marking non-human accounts, is not in ${TargetDomain}: its members are not recorded." -Level WARN; continue }
        [pscustomobject]@{ Group = $g; Members = $members }
    })
    $gm = Write-GroupMember -ConnString $connStr -Table (ConvertTo-GroupMemberTable -Groups $expanded)
    if ($groups.Count -gt 0) { Write-Log ("{0:N0} member(s) of {1} group(s) recorded (granted Organization, or marking non-human accounts)." -f $gm, $expanded.Count) }

    $m = Get-DirectoryMatch -ConnString $connStr
    if ($m.AuditUsers -gt 0) {
        $pct = [math]::Round(100.0 * $m.ByUpn / $m.AuditUsers, 1)
        Write-Log ("{0:N0} of {1:N0} user(s) in the audit data found by UPN ({2}%)." -f $m.ByUpn, $m.AuditUsers, $pct)
        if ($m.ByMailOnly -gt 0) {
            Write-Log ("{0:N0} more match only by mail: AD's UPN differs from the sign-in name for them, so reports will not attribute their usage." -f $m.ByMailOnly) -Level WARN
        }
        if ($pct -lt 90) {
            Write-Log 'Under 90% matched. Check that this is the domain users sign in to Microsoft 365 from, and that its UPNs are their sign-in names.' -Level WARN
        }
    }
    Write-Log 'Sessions and the bi schema pick the attributes up on their next build (the next -Incremental run).'
}

# Dot-sourcing (tests) loads the functions without running anything.
if ($MyInvocation.InvocationName -ne '.') { Invoke-Main }
