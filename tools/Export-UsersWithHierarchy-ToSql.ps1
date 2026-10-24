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

    Department is read from -DepartmentAttribute (default: department), and
    Country from co, else the English name of the two-letter code in c, so a
    directory that sets only c reads like one that sets co (2.5.0). A country
    is then given one name however the directory spells it: USA, U.S.A.,
    United States and "United States of America (the)" are all United States,
    so the reports count them as one country (see -CountryNames).

    Needs no Microsoft Graph permission. The AD bind uses the identity running
    the script, over the domain trust (no credentials); SQL uses the same
    identity by default. Runs on Windows PowerShell 5.1 or PowerShell 7 - .NET
    only, no module.

    The table is replaced whole, in one transaction, so a reader never sees it
    empty or half-loaded. Licenses are not here: they come from Microsoft
    Graph, by tools/Update-CopilotAuditEnrichment.ps1.

    THE DELETE GUARD (2.6.5). Before the table is replaced, the users read are
    counted against the rows it holds. With more than -MaxDropPercent fewer
    (UserDirectoryMaxDropPercent in the settings file, 15 by default), the load
    stops and the table keeps the last good directory: an AD query cut short
    must not take most of the organization out of every report. A real drop is
    let through once with -AllowDrop; -MaxDropPercent 0 turns the guard off.

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

.PARAMETER DepartmentAttribute
    The AD attributes the department is read from, in order: the first one
    with a value is used (2.5.0). Default: department. For a directory that
    keeps it in a custom attribute: -DepartmentAttribute pncUCustom08, department.
    Set DepartmentAttribute the same in copilot-audit.settings.psd1 so the
    nightly run agrees. A department is kept to 200 characters.

.PARAMETER CountryNames
    Your own names for countries, as spelling = name, on top of the ones the
    script knows: @{ 'Britain' = 'United Kingdom'; 'United States' = 'USA' }.
    The spelling is matched whatever its case, dots or a trailing "(the)". A
    name given here wins, and is applied to the script's own name for a
    country too: 'United States' = 'USA' makes every spelling of it USA. Set
    CountryNames the same in copilot-audit.settings.psd1 so the nightly run
    agrees.

.PARAMETER MaxDropPercent
    The delete guard (2.6.5): the most the directory may shrink from one load
    to the next, in percent. Default 15; 0 turns the guard off.

.PARAMETER AllowDrop
    Load a directory the guard would refuse, once: after a reorganization or a
    clean-up of accounts that really did remove them.

.EXAMPLE
    .\tools\Export-UsersWithHierarchy-ToSql.ps1 -TargetDomain trusted.example.com `
        -SqlServer SQLPROD01 -Database CopilotAudit

.EXAMPLE
    # The department is in a custom attribute, and AD's own department is the fallback.
    .\tools\Export-UsersWithHierarchy-ToSql.ps1 -TargetDomain trusted.example.com `
        -SqlServer SQLPROD01 -Database CopilotAudit -DepartmentAttribute pncUCustom08, department
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
    [string[]] $EmployeeTypes = @('EMP', 'NONW'),

    [ValidateNotNullOrEmpty()]
    [ValidatePattern('^[A-Za-z][A-Za-z0-9-]*$')]
    [string[]] $DepartmentAttribute = @('department'),

    [hashtable] $CountryNames = @{},

    [ValidateRange(0, 100)] [double] $MaxDropPercent = 15,
    [switch]   $AllowDrop
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$script:ToolRoot = Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot 'DropGuard.ps1')
$script:ChainColumns = 15      # Mgr1..Mgr15 in audit.UserDirectory
$script:DepartmentWidth = 200  # audit.UserDirectory.Department, as wide as bi.DimUser's and dash.Person's

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
    param([Parameter(Mandatory)][string] $Domain, [string[]] $DepartmentAttribute = @('department'), [hashtable] $CountryNames = @{})

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
    $load = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($a in @('userPrincipalName', 'mail', 'displayName', 'title', 'division',
                     'physicalDeliveryOfficeName', 'l', 'co', 'c', 'company', 'employeeType', 'manager',
                     'distinguishedName', 'userAccountControl', 'objectSid', 'accountExpires') + @($DepartmentAttribute)) {
        if ($load.Add($a)) { [void] $searcher.PropertiesToLoad.Add($a) }
    }

    $all = [System.Collections.Generic.List[object]]::new(100000)
    $results = $searcher.FindAll()
    try {
        foreach ($r in $results) { $all.Add((ConvertTo-DirectoryEntry -Properties $r.Properties -DepartmentAttribute $DepartmentAttribute -CountryNames $CountryNames)) }
    }
    finally { $results.Dispose() }
    return , $all
}

function Get-DirectoryValue($p, $n) {
    <# The first value of a search result's attribute, as text, or $null. A result keys its attributes in lower case. #>
    $k = $n.ToLowerInvariant()
    if ($p.Contains($k)) { [string] $p[$k][0] } else { $null }
}

function Get-DirectorySid($p) {
    if (-not $p.Contains('objectsid')) { return $null }
    ([System.Security.Principal.SecurityIdentifier]::new([byte[]] $p['objectsid'][0], 0)).Value
}

function ConvertFrom-CountryCode {
    <#
        AD's c attribute, an ISO 3166 two-letter code, as the English name of the country (CA is Canada), so a user
        who has only c reads like one who has co. A value that is not a two-letter code, or names no country, stays
        as it is; blank is $null. Pure, for the tests.
    #>
    param([AllowNull()][AllowEmptyString()][string] $Code)
    if ([string]::IsNullOrWhiteSpace($Code)) { return $null }
    $c = $Code.Trim()
    if ($c -notmatch '^[A-Za-z]{2}$') { return $c }
    try {
        $name = [System.Globalization.RegionInfo]::new($c.ToUpperInvariant()).EnglishName
        if ($name) { $name } else { $c }
    }
    catch { $c }
}

<#
    Spellings of a country in common use that are neither its English name nor an ISO code, as the ISO 3166
    two-letter code of the country they mean. Keys are as Get-CountryKey leaves a spelling: lower case, no dots,
    no "(the)". The name shown is .NET's English name for that code, the one a two-letter code in c already gets.
#>
$script:CountryAliases = @{
    'united states of america' = 'US'; 'america' = 'US'; 'us of a' = 'US'
    'uk' = 'GB'; 'great britain' = 'GB'; 'britain' = 'GB'; 'united kingdom of great britain and northern ireland' = 'GB'
    'republic of korea' = 'KR'; 'korea, republic of' = 'KR'; 'korea (republic of)' = 'KR'; 'south korea' = 'KR'; 'korea' = 'KR'
    'russian federation' = 'RU'; 'russia' = 'RU'
    'viet nam' = 'VN'; 'vietnam' = 'VN'
    'czech republic' = 'CZ'; 'czechia' = 'CZ'
    'uae' = 'AE'; 'holland' = 'NL'; 'netherlands' = 'NL'
    'turkey' = 'TR'; 'turkiye' = 'TR'
    'hong kong' = 'HK'; 'hong kong sar' = 'HK'; 'hong kong sar china' = 'HK'; 'macao' = 'MO'; 'macau' = 'MO'
    'taiwan' = 'TW'; 'taiwan, province of china' = 'TW'
    'china' = 'CN'; 'prc' = 'CN'; 'peoples republic of china' = 'CN'
    'iran' = 'IR'; 'iran, islamic republic of' = 'IR'; 'iran (islamic republic of)' = 'IR'
    'ivory coast' = 'CI'; 'republic of ireland' = 'IE'; 'eire' = 'IE'
    'brasil' = 'BR'; 'deutschland' = 'DE'; 'espana' = 'ES'; 'mexico' = 'MX'
}
$script:CountryLookup = $null   # filled on first use: an English name or a three-letter code, as its two-letter code
$script:CountryRenamed = @{}    # what this run renamed, as "spelling -> name" = how many people, for the log

function Get-CountryKey {
    <# A spelling as it is matched: lower case, accents and dots dropped, no "(the)" or ", the" at its end, no "the " at its start, one space between words. #>
    param([string] $Value)
    $k = $Value.Trim().ToLowerInvariant().Normalize([System.Text.NormalizationForm]::FormD)
    $k = -join ($k.ToCharArray() | Where-Object { [System.Globalization.CharUnicodeInfo]::GetUnicodeCategory($_) -ne [System.Globalization.UnicodeCategory]::NonSpacingMark })
    $k = $k.Replace('.', '').Replace([string] [char] 0x2019, '').Replace("'", '')
    $k = $k -replace '\s*\(the\)\s*$', '' -replace ',\s*the\s*$', '' -replace '^the\s+', ''
    ($k -replace '\s+', ' ').Trim()
}

function ConvertTo-CountryName {
    <#
        One name for a country however the directory spells it: its two- or three-letter ISO code, its English name,
        that name with "(the)" after it as ISO's own list writes it, or a spelling in $script:CountryAliases. The
        name is .NET's English name for the country ("United States", "United Kingdom"). -Names is the
        administrator's own list, spelling = name: it wins, and is applied to the name found here too. A value that
        names no country known here stays as it is, trimmed; blank is $null. Pure, for the tests.
    #>
    param([AllowNull()][AllowEmptyString()][string] $Value, [hashtable] $Names = @{})
    if ([string]::IsNullOrWhiteSpace($Value)) { return $null }
    $raw = $Value.Trim()
    $key = Get-CountryKey $raw
    $own = @{}
    foreach ($n in $Names.Keys) { if (-not [string]::IsNullOrWhiteSpace("$($Names[$n])")) { $own[(Get-CountryKey "$n")] = "$($Names[$n])".Trim() } }
    if ($own.ContainsKey($key)) { return $own[$key] }

    if ($null -eq $script:CountryLookup) {
        $lookup = @{}
        foreach ($culture in [System.Globalization.CultureInfo]::GetCultures([System.Globalization.CultureTypes]::SpecificCultures)) {
            try { $r = [System.Globalization.RegionInfo]::new($culture.Name) } catch { continue }
            if ($r.TwoLetterISORegionName -notmatch '^[A-Z]{2}$') { continue }
            $lookup[(Get-CountryKey $r.EnglishName)] = $r.TwoLetterISORegionName
            if ($r.ThreeLetterISORegionName -match '^[A-Z]{3}$') { $lookup[$r.ThreeLetterISORegionName.ToLowerInvariant()] = $r.TwoLetterISORegionName }
        }
        $script:CountryLookup = $lookup
    }
    # A spelling in common use first: UK is two letters and is not the ISO code, which is GB.
    $code = if ($script:CountryAliases.ContainsKey($key)) { $script:CountryAliases[$key] }
            elseif ($key -match '^[a-z]{2}$') { $key.ToUpperInvariant() }
            elseif ($script:CountryLookup.ContainsKey($key)) { $script:CountryLookup[$key] }
            else { $null }
    $name = $raw
    if ($code) {
        try { $english = [System.Globalization.RegionInfo]::new($code).EnglishName; if ($english) { $name = $english } } catch { $name = $raw }
    }
    $nameKey = Get-CountryKey $name
    if ($own.ContainsKey($nameKey)) { $name = $own[$nameKey] }
    $name
}

function ConvertTo-DirectoryEntry {
    <#
        One search result's properties as the plain object the rest of the script reads. Pure: the properties are
        anything with Contains(name) and an indexer, keyed in lower case as a search result keys them, so the tests
        need no directory.

        Department is the first of -DepartmentAttribute that has a value. Country is co, else the English
        name of c, AD's two-letter code, so a directory that sets only c reads like one that sets co; then one
        name for the country however it is spelled (ConvertTo-CountryName), counted in $script:CountryRenamed.
    #>
    param([Parameter(Mandatory)] $Properties, [string[]] $DepartmentAttribute = @('department'), [hashtable] $CountryNames = @{})
    $p = $Properties
    $dept = $null
    foreach ($a in $DepartmentAttribute) {
        $v = Get-DirectoryValue $p $a
        if (-not [string]::IsNullOrWhiteSpace($v)) { $dept = $v; break }
    }
    $co = Get-DirectoryValue $p 'co'
    $spelled = if ([string]::IsNullOrWhiteSpace($co)) { ConvertFrom-CountryCode (Get-DirectoryValue $p 'c') } else { "$co".Trim() }
    $country = ConvertTo-CountryName $spelled -Names $CountryNames
    if ($country -and $spelled -and $country -cne $spelled) {
        $script:CountryRenamed["$spelled -> $country"] = 1 + [int] $script:CountryRenamed["$spelled -> $country"]
    }
    [pscustomobject]@{
        dn      = Get-DirectoryValue $p 'distinguishedname'
        upn     = Get-DirectoryValue $p 'userprincipalname'
        mail    = Get-DirectoryValue $p 'mail'
        disp    = Get-DirectoryValue $p 'displayname'
        title   = Get-DirectoryValue $p 'title'
        dept    = $dept
        div     = Get-DirectoryValue $p 'division'
        off     = Get-DirectoryValue $p 'physicaldeliveryofficename'
        city    = Get-DirectoryValue $p 'l'
        country = $country
        company = Get-DirectoryValue $p 'company'
        etype   = Get-DirectoryValue $p 'employeetype'
        mgr     = Get-DirectoryValue $p 'manager'
        uac     = [int] (Get-DirectoryValue $p 'useraccountcontrol')
        sid     = Get-DirectorySid $p
        expires = ConvertFrom-AccountExpiry (Get-DirectoryValue $p 'accountexpires')
    }
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
        A person granted by DOMAIN\name is one too: their own UPN, which Power BI's
        row-level security knows them by (bi.OrgSecurity), as the dashboard knows them by
        their sign-in.
    #>
    param([Parameter(Mandatory)][string] $Domain, [Parameter(Mandatory)][string] $Group)
    $name = if ($Group.Contains('\')) { $Group.Substring($Group.IndexOf('\') + 1) } else { $Group }
    $base = ([ADSI]"LDAP://$Domain/RootDSE").defaultNamingContext
    $find = [System.DirectoryServices.DirectorySearcher]::new([ADSI]"LDAP://$Domain/$base",
        "(&(|(objectCategory=group)(&(objectCategory=person)(objectClass=user)))(sAMAccountName=$(ConvertTo-LdapFilterValue $name)))")
    foreach ($p in 'distinguishedName', 'objectClass', 'userPrincipalName') { [void] $find.PropertiesToLoad.Add($p) }
    $g = $find.FindOne()
    if (-not $g) { return $null }
    if (@($g.Properties['objectclass']) -notcontains 'group') { return , [string[]] @($g.Properties['userprincipalname'] | ForEach-Object { [string] $_ }) }
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
        out of the chain too: a person whose manager has no UPN reports
        to the nearest manager above who has one, so the org chart stays joined.
        OrgDepth still counts that manager, so the person keeps their
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
    $clipped   = 0      # departments cut to the column's width

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
        # OrgDepth counts every manager above, those without a UPN too: it counted only the chain's, so a
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
        # A custom attribute can be longer than the column (AD's own department stops at 64): cut, not fail the load.
        $dept = [string] $u.dept
        if ($dept.Length -gt $script:DepartmentWidth) { $dept = $dept.Substring(0, $script:DepartmentWidth); $clipped++ }
        $row['Department']        = NZ $dept
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

    return [pscustomobject]@{ Table = $dt; Skipped = $skipped; Clipped = $clipped }
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
        an empty or partial one. First the delete guard: with more than
        -MaxDropPercent fewer rows than the table holds, nothing is written.
    #>
    param(
        [Parameter(Mandatory)][string] $ConnString,
        [Parameter(Mandatory)][System.Data.DataTable] $Table,
        [int] $BatchRows = 10000,
        [double] $MaxDropPercent = 0,
        [switch] $AllowDrop
    )
    $conn = [System.Data.SqlClient.SqlConnection]::new($ConnString)
    $conn.Open()
    try {
        $cmd = $conn.CreateCommand()
        $cmd.CommandTimeout = 300
        $cmd.CommandText = Get-Content -LiteralPath (Join-Path (Join-Path $script:ToolRoot 'sql') 'Create-CopilotEnrichmentSchema.sql') -Raw
        [void] $cmd.ExecuteNonQuery()

        $cmd = $conn.CreateCommand()
        $cmd.CommandTimeout = 300
        $cmd.CommandText = 'SELECT COUNT_BIG(*) FROM audit.UserDirectory;'
        Assert-DropGuard -What 'User directory (audit.UserDirectory)' -Before ([long] $cmd.ExecuteScalar()) -After $Table.Rows.Count `
                         -MaxDropPercent $MaxDropPercent -Setting 'UserDirectoryMaxDropPercent' -AllowDrop:$AllowDrop

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
        Power BI's row-level security), and those an administrator listed as marking non-human accounts (the NonHumanGroups setting, separated by commas, semicolons or lines). An Organization grant by
        DOMAIN\name is read whatever type it was recorded with, as the dashboard matches it: a group
        recorded as a user, and a person, whom Get-GroupMemberUpn gives by their UPN.
    #>
    param([Parameter(Mandatory)][string] $ConnString)
    $conn = [System.Data.SqlClient.SqlConnection]::new($ConnString)
    $conn.Open()
    try {
        $cmd = $conn.CreateCommand()
        $cmd.CommandText = @'
IF OBJECT_ID(N'dash.RoleGrant', N'U') IS NOT NULL
    EXEC (N'SELECT DISTINCT Principal FROM dash.RoleGrant WHERE Role = ''Organization'' AND (PrincipalType = ''Group'' OR Principal LIKE N''%\%'')
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

    Write-Log "Reading users from $TargetDomain (department from $($DepartmentAttribute -join ', then '))..."
    $script:CountryRenamed = @{}
    $entries = Get-DirectoryEntry -Domain $TargetDomain -DepartmentAttribute $DepartmentAttribute -CountryNames $CountryNames
    Write-Log ("{0:N0} user object(s) read." -f $entries.Count)
    # Said each night, so a spelling nobody expected to be changed is seen, and one that was missed is not in the list.
    if ($script:CountryRenamed.Count -gt 0) {
        $renamed = @($script:CountryRenamed.GetEnumerator() | Sort-Object Value -Descending | ForEach-Object { "$($_.Key) ($($_.Value.ToString('N0')))" })
        Write-Log ("Countries given one name: {0}{1}. To name one differently, set CountryNames in copilot-audit.settings.psd1." -f (($renamed | Select-Object -First 25) -join '; '), $(if ($renamed.Count -gt 25) { "; and $($renamed.Count - 25) more" } else { '' }))
    }

    $rows = ConvertTo-UserDirectoryTable -Entries $entries -MaxLevels $MaxLevels -EmployeeTypeMap $EmployeeTypeMap `
                                         -EmployeeTypes $EmployeeTypes
    $s = $rows.Skipped
    $disabled = @($rows.Table.Rows | Where-Object { -not $_.AccountEnabled }).Count
    Write-Log ("{0:N0} row(s) to load, {1:N0} of them disabled accounts; skipped {2:N0} of another employee type, {3:N0} with no UPN, {4:N0} duplicate UPN, {5:N0} UPN over 256 characters." -f `
               $rows.Table.Rows.Count, $disabled, $s.OtherType, $s.NoUpn, $s.DuplicateUpn, $s.UpnTooLong)
    # What the attributes gave: AD returns nothing for an attribute the account running this may not read, so a
    # department that is empty for everyone means the attribute is misnamed or unreadable, not that nobody has one.
    $withDepartment = @($rows.Table.Rows | Where-Object { $_.Department -isnot [DBNull] }).Count
    $withCountry = @($rows.Table.Rows | Where-Object { $_.Country -isnot [DBNull] }).Count
    Write-Log ("Department filled for {0:N0} of {1:N0} row(s), country for {2:N0}." -f $withDepartment, $rows.Table.Rows.Count, $withCountry)
    if ($rows.Table.Rows.Count -gt 0 -and $withDepartment -eq 0) {
        Write-Log "No one has a department. Check that the account running this can read $($DepartmentAttribute -join ', ') in ${TargetDomain}, and that the attribute names are right." -Level WARN
    }
    if ($rows.Clipped -gt 0) {
        Write-Log ("{0:N0} department(s) were longer than {1} characters and were cut." -f $rows.Clipped, $script:DepartmentWidth) -Level WARN
    }

    $n = Write-UserDirectory -ConnString $connStr -Table $rows.Table -BatchRows $BatchSize -MaxDropPercent $MaxDropPercent -AllowDrop:$AllowDrop
    Write-Log ("Loaded {0:N0} row(s) into audit.UserDirectory." -f $n)

    # Organization granted to AD groups: their members, for Power BI's row-level security (bi.OrgSecurity);
    # and the groups that mark non-human accounts, for the dashboard's policy-breach check.
    # Written every night, even with no groups, so a revoked grant's members do not linger.
    # Get-OrganizationGroup returns its array whole (unary comma), so @() here would wrap it in a second array.
    [string[]] $groups = Get-OrganizationGroup -ConnString $connStr
    $expanded = @(foreach ($g in $groups) {
        $members = Get-GroupMemberUpn -Domain $TargetDomain -Group $g
        if ($null -eq $members) { Write-Log "$g, granted Organization or listed as marking non-human accounts, is not a group or person in ${TargetDomain}: its members are not recorded." -Level WARN; continue }
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

# Dot-sourcing (tests) loads the functions without running anything. Refused by the delete guard, the table
# kept the last directory: a code of its own, which the nightly run records and goes on from.
if ($MyInvocation.InvocationName -ne '.') {
    try { Invoke-Main }
    catch {
        if (-not (Test-DropRefusal $_)) { throw }
        Write-Log $_.Exception.Message -Level ERROR
        exit $script:DropRefusedExitCode
    }
}
