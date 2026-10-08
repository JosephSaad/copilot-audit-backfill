<#
    Tests tools/Export-UsersWithHierarchy-ToSql.ps1's rules - the management
    chain, the filters, the employee-type map - without a directory or SQL:
    ConvertTo-UserDirectoryTable takes plain objects shaped like the directory
    search results. The SQL load is covered by Test-SqlIntegration.ps1 section 8.
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot 'TestHelpers.ps1')
. (Join-Path (Join-Path $root 'tools') 'Export-UsersWithHierarchy-ToSql.ps1')   # functions only - the guard skips Invoke-Main

function New-Entry {
    param([string] $Cn, [string] $Manager, [string] $Type = 'EMP', [switch] $Disabled, [string] $Upn = "$Cn@contoso.com")
    [pscustomobject]@{
        dn = "CN=$Cn,OU=Users,DC=contoso,DC=com"; upn = $Upn; mail = "$Cn@mail.contoso.com"; disp = "Name $Cn"
        title = "Title $Cn"; dept = 'Finance'; div = 'Corporate'; off = 'HQ-4'; city = 'Toronto'; country = 'Canada'
        company = 'Contoso'; etype = $Type
        mgr = $(if ($Manager) { "CN=$Manager,OU=Users,DC=contoso,DC=com" })
        uac = $(if ($Disabled) { 514 } else { 512 })
        sid = $(if ($Cn -eq 'ic') { 'S-1-5-21-1111-2222-3333-1001' })
        expires = $(if ($Cn -eq 'ic2') { ConvertFrom-AccountExpiry ([DateTime]::new(2026, 12, 31, 0, 0, 0, [DateTimeKind]::Utc).ToFileTimeUtc().ToString()) })
    }
}

# ceo (type EXEC, not loaded) <- vp <- dir (disabled) <- mgr <- ic ; ic2 reports to mgr too
$entries = @(
    New-Entry 'ceo' $null 'EXEC'
    New-Entry 'vp'  'ceo'
    New-Entry 'dir' 'vp' -Disabled
    New-Entry 'mgr' 'dir'
    New-Entry 'ic'  'mgr'
    New-Entry 'ic2' 'mgr' 'NONW'
    New-Entry 'svc' 'mgr' 'SVC'                                # another type
    New-Entry 'noupn' 'mgr' -Upn ''                            # no UPN: cannot be joined
    New-Entry 'dup' 'mgr' -Upn 'ic@contoso.com'                # a second object with ic's UPN
    New-Entry 'orphan' 'nobody'                                # manager not in the directory
    New-Entry 'loopa' 'loopb'
    New-Entry 'loopb' 'loopa'                                  # a loop in the manager attributes
)

$map = @{ 'EMP' = 'EMPLOYEE'; 'NONW' = 'CONTRACTOR' }

# ---------------------------------------------------------------------------
Write-Host "`n1. The chain, the manager, the filters" -ForegroundColor Cyan

$r = ConvertTo-UserDirectoryTable -Entries $entries -EmployeeTypeMap $map -EmployeeTypes @('EMP', 'NONW')
$rows = @{}; foreach ($x in $r.Table.Rows) { $rows[$x.UserPrincipalName] = $x }

$ic = $rows['ic@contoso.com']
Assert-That ($null -ne $ic -and $ic.OrgDepth -eq 4) "ic has four managers above them ($($ic.OrgDepth))"
Assert-That ($ic.Mgr1 -eq 'Name ceo' -and $ic.Mgr2 -eq 'Name vp' -and $ic.Mgr3 -eq 'Name dir' -and $ic.Mgr4 -eq 'Name mgr' -and $ic.Mgr5 -is [DBNull]) `
    'the chain runs from the top (Mgr1) down to the direct manager (Mgr4), then empty'
Assert-That ($ic.ManagerUPN -eq 'mgr@contoso.com' -and $ic.ManagerName -eq 'Name mgr') "the direct manager's UPN and name ($($ic.ManagerUPN))"
Assert-That (-not $rows.ContainsKey('ceo@contoso.com') -and $ic.Mgr1 -eq 'Name ceo') `
    'a manager of a type not loaded (the CEO, EXEC) is still in the chain'
Assert-That ($rows['mgr@contoso.com'].Mgr2 -eq 'Name vp') 'a chain runs through a disabled manager (dir)'
Assert-That ($rows['dir@contoso.com'].AccountEnabled -eq $false -and $rows['ic@contoso.com'].AccountEnabled -eq $true) `
    'disabled accounts are loaded, flagged AccountEnabled = 0 - there is no filter on it'
Assert-That ($ic.Sid -eq 'S-1-5-21-1111-2222-3333-1001' -and $rows['mgr@contoso.com'].Sid -is [DBNull]) 'the account SID is carried, for matching a sign-in from another domain'
Assert-That ($rows['mgr@contoso.com'].IsManager -eq $true -and $ic.IsManager -eq $false) 'IsManager: someone reports to them'
Assert-That ($rows['ic2@contoso.com'].EmployeeType -eq 'CONTRACTOR' -and $ic.EmployeeType -eq 'EMPLOYEE') 'employeeType codes are mapped (NONW -> CONTRACTOR)'
Assert-That ($ic.Division -eq 'Corporate' -and $ic.Office -eq 'HQ-4' -and $ic.Country -eq 'Canada' -and $ic.Mail -eq 'ic@mail.contoso.com') `
    'division, office, country and mail come across'
Assert-That ($rows['orphan@contoso.com'].OrgDepth -eq 0 -and $rows['orphan@contoso.com'].ManagerUPN -is [DBNull]) `
    'a manager outside the directory ends the chain, with no manager recorded'
Assert-That ($rows['loopa@contoso.com'].OrgDepth -eq 2) "a loop in the manager attributes ends rather than hanging ($($rows['loopa@contoso.com'].OrgDepth))"
Assert-That ($rows['ic2@contoso.com'].AccountExpiresUtc -eq [DateTime]::new(2026, 12, 31) -and $ic.AccountExpiresUtc -is [DBNull]) `
    'AD accountExpires is carried as a date, and left empty for an account that never expires (2.4.3)'
Assert-That ($null -eq (ConvertFrom-AccountExpiry '0') -and $null -eq (ConvertFrom-AccountExpiry '9223372036854775807') -and $null -eq (ConvertFrom-AccountExpiry '') -and $null -eq (ConvertFrom-AccountExpiry 'x')) `
    'accountExpires of 0, the largest Int64, empty or not a number all mean never'
Assert-That ((ConvertFrom-AccountExpiry '134116992000000000') -eq [DateTime]::new(2026, 1, 1)) 'a FILETIME converts to its UTC date (134116992000000000 is 2026-01-01)'

$s = $r.Skipped
Assert-That ($s.OtherType -eq 2 -and $s.NoUpn -eq 1 -and $s.DuplicateUpn -eq 1) `
    "skips are counted: 2 of another type (EXEC, SVC), 1 with no UPN, 1 duplicate UPN ($($s.OtherType)/$($s.NoUpn)/$($s.DuplicateUpn))"
Assert-That ($r.Table.Rows.Count -eq 8) "eight rows loaded ($($r.Table.Rows.Count))"
Assert-That ($rows['ic@contoso.com'].FullName -eq 'Name ic') 'the first object with a UPN wins over a later duplicate'

# ---------------------------------------------------------------------------
Write-Host "`n2. Options" -ForegroundColor Cyan

$t = ConvertTo-UserDirectoryTable -Entries $entries -EmployeeTypeMap $map -EmployeeTypes @('EMP', 'NONW') -MaxLevels 2
$icT = @($t.Table.Rows | Where-Object UserPrincipalName -eq 'ic@contoso.com')[0]
Assert-That ($icT.Mgr1 -eq 'Name ceo' -and $icT.Mgr2 -eq 'Name vp' -and $icT.Mgr3 -is [DBNull] -and $icT.OrgDepth -eq 4) `
    '-MaxLevels keeps the top levels; OrgDepth is still the full depth'

$a = ConvertTo-UserDirectoryTable -Entries $entries -EmployeeTypeMap $map -EmployeeTypes @()
Assert-That ($a.Skipped.OtherType -eq 0 -and @($a.Table.Rows | Where-Object UserPrincipalName -eq 'svc@contoso.com').Count -eq 1) `
    'an empty -EmployeeTypes loads every type, unmapped codes passing through'

$none = ConvertTo-UserDirectoryTable -Entries @()
Assert-That ($none.Table.Rows.Count -eq 0) 'an empty directory gives an empty table, not an error'

# ---------------------------------------------------------------------------
Write-Host "`n3. Groups granted Organization (2.4.0)" -ForegroundColor Cyan

Assert-That ((ConvertTo-LdapFilterValue 'Execs (EMEA)*\x') -eq 'Execs \28EMEA\29\2a\5cx') 'a group name is escaped for the LDAP filter (RFC 4515)'
Assert-That ((ConvertTo-LdapFilterValue 'CN=Execs,OU=Groups,DC=contoso,DC=com') -eq 'CN=Execs,OU=Groups,DC=contoso,DC=com') 'a plain distinguished name passes unchanged'

$gt = ConvertTo-GroupMemberTable -Groups @(
    [pscustomobject]@{ Group = 'CONTOSO\Execs'; Members = @('a@contoso.com', 'A@contoso.com', 'b@contoso.com', '', ('x' * 257)) }
    [pscustomobject]@{ Group = 'CONTOSO\Board'; Members = @('a@contoso.com') }
)
Assert-That ($gt.Rows.Count -eq 3) "each member once per group, blanks and over-long UPNs dropped ($($gt.Rows.Count) rows)"
Assert-That (@($gt.Rows | Where-Object { $_.GroupName -eq 'CONTOSO\Board' -and $_.UserPrincipalName -eq 'a@contoso.com' }).Count -eq 1) 'a person in two groups is recorded under each'
Assert-That ((ConvertTo-GroupMemberTable -Groups @()).Rows.Count -eq 0) 'no groups gives an empty table, which clears last night''s members'

# ---------------------------------------------------------------------------
Write-Host "`n4. People without a UPN are left out of the chain too (2.4.1)" -ForegroundColor Cyan

# top <- boss <- lead (no UPN) <- worker ; lone (no UPN manager at the top) <- orphan
$e2 = @(
    New-Entry 'top' $null
    New-Entry 'boss' 'top'
    New-Entry 'lead' 'boss' -Upn ''
    New-Entry 'worker' 'lead'
    New-Entry 'lone' $null -Upn ''
    New-Entry 'orphan' 'lone'
)
$r2 = ConvertTo-UserDirectoryTable -Entries $e2
$by = @{}; foreach ($x in $r2.Table.Rows) { $by[$x.UserPrincipalName] = $x }
Assert-That (-not $by.ContainsKey('') -and $r2.Skipped.NoUpn -eq 2 -and $r2.Table.Rows.Count -eq 4) "people without a UPN are not loaded ($($r2.Skipped.NoUpn) skipped, $($r2.Table.Rows.Count) loaded)"
Assert-That ($by['worker@contoso.com'].ManagerUPN -eq 'boss@contoso.com' -and $by['worker@contoso.com'].ManagerName -eq 'Name boss') `
    "a person whose manager has no UPN reports to the nearest manager above with one ($($by['worker@contoso.com'].ManagerUPN))"
Assert-That ($by['worker@contoso.com'].Mgr1 -eq 'Name top' -and $by['worker@contoso.com'].Mgr2 -eq 'Name boss' -and $by['worker@contoso.com'].Mgr3 -is [DBNull]) `
    'the chain leaves out the manager without a UPN'
# 2.4.12: OrgDepth still counts that manager. It counted only the chain's, so the worker looked a level higher than they
# are, and the dashboard takes levels from it: under a manager without a UPN right below the top, that was level 2,
# which sees the whole organization.
Assert-That ($by['worker@contoso.com'].OrgDepth -eq 3 -and $by['boss@contoso.com'].OrgDepth -eq 1) "but OrgDepth counts every manager above, with a UPN or not ($($by['worker@contoso.com'].OrgDepth))"
Assert-That ($by['boss@contoso.com'].IsManager -eq $true -and $by['top@contoso.com'].IsManager -eq $true) 'a manager whose report comes through a manager without a UPN is still a manager'
Assert-That ($by['orphan@contoso.com'].ManagerUPN -is [DBNull] -and $by['orphan@contoso.com'].OrgDepth -eq 1) 'with no one above who has a UPN, there is no manager, though there is a level above'

# ---------------------------------------------------------------------------
Write-Host "`n5. Department from a custom attribute, country from c (2.5.0)" -ForegroundColor Cyan

# A directory search result's properties: lower-case keys, each a list of values; an attribute with no value is absent.
function New-PropertyBag([hashtable] $Values) { $p = @{}; foreach ($k in $Values.Keys) { $p[$k] = @($Values[$k]) }; $p }
$person = @{ distinguishedname = 'CN=pl1,DC=corp'; userprincipalname = 'pl1@contoso.com'; displayname = 'Pat Lee'; title = 'Architect'
             division = 'Shared Services'; employeetype = 'NONW'; useraccountcontrol = '512'; accountexpires = '0' }
function Get-Entry([hashtable] $Extra, [string[]] $Attribute) {
    $props = New-PropertyBag ($person + $Extra)
    if ($Attribute) { ConvertTo-DirectoryEntry -Properties $props -DepartmentAttribute $Attribute } else { ConvertTo-DirectoryEntry -Properties $props }
}

# Department: the first attribute named that has a value.
Assert-That ((Get-Entry @{ department = 'Finance' }).dept -eq 'Finance') 'by default the department is AD''s department'
Assert-That ($null -eq (Get-Entry @{ pncucustom08 = 'Applied Research' }).dept) 'by default a custom attribute is not read'
Assert-That ((Get-Entry @{ pncucustom08 = 'Applied Research' } 'pncUCustom08').dept -eq 'Applied Research') '-DepartmentAttribute pncUCustom08 reads the custom attribute (any case)'
Assert-That ((Get-Entry @{ pncucustom08 = 'Applied Research'; department = 'Finance' } 'pncUCustom08', 'department').dept -eq 'Applied Research') 'the first attribute with a value wins'
Assert-That ((Get-Entry @{ department = 'Finance' } 'pncUCustom08', 'department').dept -eq 'Finance') 'a missing first attribute falls back to the next'
Assert-That ((Get-Entry @{ pncucustom08 = '   '; department = 'Finance' } 'pncUCustom08', 'department').dept -eq 'Finance') 'a first attribute that is only spaces falls back too'
Assert-That ($null -eq (Get-Entry @{ company = 'x' } 'pncUCustom08', 'department').dept) 'no attribute with a value gives no department'
Assert-That ((Get-Entry @{ extensionattribute3 = 'Risk' } 'extensionAttribute3').dept -eq 'Risk' -and (Get-Entry @{ 'x-y' = 'Ops' } 'x-y').dept -eq 'Ops') 'any attribute name, hyphens included'

# Country: co, else the English name of the two-letter code in c.
Assert-That ((Get-Entry @{ c = 'CA' }).country -eq 'Canada') 'with only c, the country is its English name (CA is Canada)'
Assert-That ((Get-Entry @{ co = 'Atlantis'; c = 'GB' }).country -eq 'Atlantis') 'co wins over c'
Assert-That ((Get-Entry @{ co = '  '; c = 'GB' }).country -eq 'United Kingdom') 'a blank co falls back to c'
Assert-That ($null -eq (Get-Entry @{ company = 'x' }).country) 'neither co nor c gives no country'
Assert-That ((ConvertFrom-CountryCode 'ca') -eq 'Canada' -and (ConvertFrom-CountryCode ' GB ') -eq 'United Kingdom') 'a code in any case, with spaces round it'
Assert-That ((ConvertFrom-CountryCode 'CAN') -eq 'CAN' -and (ConvertFrom-CountryCode 'Canada') -eq 'Canada' -and (ConvertFrom-CountryCode 'C1') -eq 'C1') 'a value that is not a two-letter code stays as it is'
Assert-That ($null -eq (ConvertFrom-CountryCode '') -and $null -eq (ConvertFrom-CountryCode $null) -and $null -eq (ConvertFrom-CountryCode '  ')) 'a blank code is nothing'

# The rest of the entry is unchanged, and the row carries both.
$e = Get-Entry @{ pncucustom08 = 'Applied Research'; c = 'CA'; mail = 'pl1@mail.contoso.com' } 'pncUCustom08'
Assert-That ($e.upn -eq 'pl1@contoso.com' -and $e.disp -eq 'Pat Lee' -and $e.title -eq 'Architect' -and $e.div -eq 'Shared Services' -and $e.etype -eq 'NONW' -and
             $e.uac -eq 512 -and $null -eq $e.expires -and $null -eq $e.sid -and $e.mail -eq 'pl1@mail.contoso.com') 'the other attributes read as before'
$row = (ConvertTo-UserDirectoryTable -Entries @($e) -EmployeeTypeMap $map -EmployeeTypes @('EMP', 'NONW')).Table.Rows[0]
Assert-That ($row.Department -eq 'Applied Research' -and $row.Country -eq 'Canada' -and $row.EmployeeType -eq 'CONTRACTOR') 'the row has the custom department, the country name and the mapped type'

# A custom attribute can be longer than the column (AD's own department stops at 64): cut at 200, not a failed load.
$long = New-Entry 'l' $null; $long.dept = 'd' * 250
$mid = New-Entry 'm' $null;  $mid.dept = 'm' * 200
$wide = New-Entry 'w' $null; $wide.dept = 'w' * 120
$cl = ConvertTo-UserDirectoryTable -Entries @($long, $mid, $wide) -EmployeeTypes @()
$by5 = @{}; foreach ($x in $cl.Table.Rows) { $by5[$x.UserPrincipalName] = $x }
Assert-That ($by5['l@contoso.com'].Department.Length -eq 200 -and $by5['m@contoso.com'].Department.Length -eq 200 -and $by5['w@contoso.com'].Department.Length -eq 120 -and $cl.Clipped -eq 1) `
    "a department over 200 characters is cut and counted; 120 and 200 are kept ($($cl.Clipped) cut)"
Assert-That ((ConvertTo-UserDirectoryTable -Entries $entries -EmployeeTypeMap $map -EmployeeTypes @('EMP', 'NONW')).Clipped -eq 0) 'normal departments are cut never'

# The parameter takes an attribute name and nothing else: it is read into a directory search.
$cmd = Get-Command (Join-Path (Join-Path $root 'tools') 'Export-UsersWithHierarchy-ToSql.ps1')
$pattern = @($cmd.Parameters['DepartmentAttribute'].Attributes | Where-Object { $_ -is [System.Management.Automation.ValidatePatternAttribute] })[0].RegexPattern
Assert-That (@('pncUCustom08', 'department', 'extensionAttribute3', 'x-y' | Where-Object { $_ -notmatch $pattern }).Count -eq 0 -and
             @('bad name', 'a;b', '1abc', '(x)', '*', 'a=b' | Where-Object { $_ -match $pattern }).Count -eq 0) `
    '-DepartmentAttribute accepts attribute names, and not a space, a separator, a leading digit or a filter character'
# ---------------------------------------------------------------------------
Write-Host "`n6. One name for a country, however the directory spells it" -ForegroundColor Cyan

# A client's directory had the United States three ways, and the reports showed three countries.
foreach ($spelling in 'USA', 'United States', 'United States of America (the)', 'U.S.A.', 'us', 'united states of america', '  USA  ') {
    Assert-That ((ConvertTo-CountryName $spelling) -eq 'United States') "'$spelling' is United States"
}
Assert-That ((ConvertTo-CountryName 'UK') -eq 'United Kingdom' -and (ConvertTo-CountryName 'Great Britain') -eq 'United Kingdom' -and (ConvertTo-CountryName 'GBR') -eq 'United Kingdom') `
    'UK, Great Britain and the three-letter code GBR are United Kingdom'
Assert-That ((ConvertTo-CountryName 'Netherlands (the)') -eq (ConvertTo-CountryName 'NL') -and (ConvertTo-CountryName 'Korea, Republic of') -eq (ConvertTo-CountryName 'KR')) `
    'the names ISO''s own list writes, with (the) or turned round, are the country of their code'
Assert-That ((ConvertTo-CountryName 'Canada') -eq 'Canada' -and (ConvertTo-CountryName 'can') -eq 'Canada' -and (ConvertTo-CountryName 'India') -eq 'India') 'a name already right stays, and a three-letter code is its country'
Assert-That ((ConvertTo-CountryName 'Atlantis') -eq 'Atlantis' -and (ConvertTo-CountryName ' Head Office ') -eq 'Head Office') 'a value that names no country known here stays as it is, trimmed'
Assert-That ($null -eq (ConvertTo-CountryName '') -and $null -eq (ConvertTo-CountryName $null) -and $null -eq (ConvertTo-CountryName '   ')) 'blank is nothing'
# The administrator's own names win, and reach the script's own name for a country too.
Assert-That ((ConvertTo-CountryName 'usa' -Names @{ 'United States' = 'USA' }) -eq 'USA' -and (ConvertTo-CountryName 'United States of America (the)' -Names @{ 'united states' = 'USA' }) -eq 'USA') `
    'CountryNames renames a country in every spelling of it'
Assert-That ((ConvertTo-CountryName 'Atlantis' -Names @{ 'ATLANTIS' = 'Lost' }) -eq 'Lost' -and (ConvertTo-CountryName 'Canada' -Names @{ 'Atlantis' = 'Lost'; 'Nowhere' = '' }) -eq 'Canada') `
    'CountryNames names a spelling the script does not know, whatever its case, and leaves the others alone'
$script:CountryRenamed = @{}
Assert-That ((Get-Entry @{ co = 'USA' }).country -eq 'United States' -and (Get-Entry @{ c = 'us' }).country -eq 'United States' -and (Get-Entry @{ co = 'United States' }).country -eq 'United States') `
    'co and c both come out as the one name'
Assert-That ($script:CountryRenamed.Count -eq 1 -and $script:CountryRenamed['USA -> United States'] -eq 1) "each spelling changed is counted for the log, and one already right is not ($(($script:CountryRenamed.Keys | Sort-Object) -join '; '))"
Assert-That ((ConvertTo-DirectoryEntry -Properties (New-PropertyBag ($person + @{ co = 'usa' })) -CountryNames @{ 'United States' = 'US' }).country -eq 'US') 'an entry takes the names it is given'

Write-Host ''
if ($script:Failures.Count) { Write-Host "$($script:Failures.Count) assertion(s) failed." -ForegroundColor Red; exit 1 }
Write-Host 'All user directory tests passed.' -ForegroundColor Green
