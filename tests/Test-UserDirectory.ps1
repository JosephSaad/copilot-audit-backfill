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
Assert-That ($by['worker@contoso.com'].Mgr1 -eq 'Name top' -and $by['worker@contoso.com'].Mgr2 -eq 'Name boss' -and $by['worker@contoso.com'].Mgr3 -is [DBNull] -and $by['worker@contoso.com'].OrgDepth -eq 2) `
    'the chain leaves out the manager without a UPN'
Assert-That ($by['boss@contoso.com'].IsManager -eq $true -and $by['top@contoso.com'].IsManager -eq $true) 'a manager whose report comes through a manager without a UPN is still a manager'
Assert-That ($by['orphan@contoso.com'].ManagerUPN -is [DBNull] -and $by['orphan@contoso.com'].OrgDepth -eq 0) 'with no one above who has a UPN, there is no manager'

Write-Host ''
if ($script:Failures.Count) { Write-Host "$($script:Failures.Count) assertion(s) failed." -ForegroundColor Red; exit 1 }
Write-Host 'All user directory tests passed.' -ForegroundColor Green
