<#
    Tests the provisioning scripts' own logic, dot-sourced so nothing touches a
    tenant, the certificate store, Task Scheduler or SQL Server:

      1  tools\New-CopilotAuditApp.ps1 against a simulated tenant: the app, its
         three permissions and nothing else, consent, the certificate; a second
         run changes nothing; a renewed certificate keeps the old one; an
         existing app keeps its other permissions
      2  Invoke-CopilotPipeline.ps1: the nightly and history plans, and a step
         in its own process - arguments across, exit codes back
      3  tools\Install-CopilotJobServer.ps1: settings merged and written so they
         read back the same, and the batch-logon right line
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent

$script:Failures = [System.Collections.Generic.List[string]]::new()
function Assert-That {
    param([bool] $Condition, [string] $Message)
    if ($Condition) { Write-Host "  PASS  $Message" -ForegroundColor Green }
    else            { Write-Host "  FAIL  $Message" -ForegroundColor Red; $script:Failures.Add($Message) }
}

$tmp = Join-Path ([System.IO.Path]::GetTempPath()) "copilot-provisioning-$PID"
New-Item -ItemType Directory -Path $tmp -Force | Out-Null

function New-TestCer([string] $Path, [string] $Cn) {
    $rsa = [System.Security.Cryptography.RSA]::Create(2048)
    $req = [System.Security.Cryptography.X509Certificates.CertificateRequest]::new("CN=$Cn", $rsa,
        [System.Security.Cryptography.HashAlgorithmName]::SHA256, [System.Security.Cryptography.RSASignaturePadding]::Pkcs1)
    $c = $req.CreateSelfSigned([datetimeoffset]::UtcNow.AddDays(-1), [datetimeoffset]::UtcNow.AddYears(1))
    [System.IO.File]::WriteAllBytes($Path, $c.RawData)
    $c
}

try {
# ---------------------------------------------------------------------------
Write-Host "`n1. App registration against a simulated tenant" -ForegroundColor Cyan

$graphAppId = '00000003-0000-0000-c000-000000000000'
$roles = [ordered]@{
    'AuditLogsQuery.Read.All'    = '5e1e9171-754d-478c-812c-f1755a9a4c2d'
    'User.Read.All'              = 'df021288-bdef-4463-88db-98f22de89214'
    'LicenseAssignment.Read.All' = 'e2f98668-2877-4f38-a2f4-8202e0717aa1'
}
function New-Tenant {
    $appRoles = @($roles.Keys | ForEach-Object { @{ id = $roles[$_]; value = $_; allowedMemberTypes = @('Application') } }) +
                @(@{ id = '11111111-1111-1111-1111-111111111111'; value = 'Directory.ReadWrite.All'; allowedMemberTypes = @('Application') },
                  @{ id = '22222222-2222-2222-2222-222222222222'; value = 'User.Read.All'; allowedMemberTypes = @('User') })
    $script:Tenant = @{
        Apps = [System.Collections.Generic.List[hashtable]]::new(); Sps = [System.Collections.Generic.List[hashtable]]::new()
        Assignments = [System.Collections.Generic.List[hashtable]]::new(); Writes = [System.Collections.Generic.List[string]]::new()
        GraphSp = @{ id = 'graph-sp'; appId = $graphAppId; appRoles = $appRoles }
    }
}
function ConvertTo-Obj($h) { $h | ConvertTo-Json -Depth 10 | ConvertFrom-Json }

function global:Connect-MgGraph { param($TenantId, $Scopes, [switch] $NoWelcome, [switch] $UseDeviceCode) $script:SignedInScopes = $Scopes }
function global:Get-MgContext { [pscustomobject]@{ TenantId = 'tenant-guid' } }
function global:Invoke-MgGraphRequest {
    param($Method, $Uri, $OutputType, $Body, $ContentType)
    $t = $script:Tenant
    $b = if ($Body) { $Body | ConvertFrom-Json -AsHashtable } else { $null }
    if ($Method -ne 'GET') { $t.Writes.Add("$Method $Uri") }
    switch -Regex ($Uri) {
        "^/v1.0/servicePrincipals\?\`$filter=appId eq '$graphAppId'" { return ConvertTo-Obj @{ value = @($t.GraphSp) } }
        "^/v1.0/servicePrincipals\?\`$filter=appId eq '([^']+)'" {
            $id = $Matches[1]; return ConvertTo-Obj @{ value = @($t.Sps | Where-Object { $_.appId -eq $id }) } }
        "^/v1.0/applications\?\`$filter=displayName eq '(.+)'&" {
            $n = $Matches[1].Replace("''", "'"); return ConvertTo-Obj @{ value = @($t.Apps | Where-Object { $_.displayName -eq $n }) } }
        '^/v1.0/applications$' {
            $a = @{ id = "obj-$($t.Apps.Count)"; appId = "client-$($t.Apps.Count)"; displayName = $b.displayName
                    requiredResourceAccess = $b.requiredResourceAccess; keyCredentials = @() }
            $t.Apps.Add($a); return ConvertTo-Obj $a }
        '^/v1.0/applications/([^/?]+)$' {
            $a = $t.Apps | Where-Object { $_.id -eq $Matches[1] }
            if ($b.Contains('keyCredentials')) {
                # Graph keeps a credential sent back by keyId with no key; a new one needs its key.
                $a.keyCredentials = @($b.keyCredentials | ForEach-Object {
                    if ($_.ContainsKey('keyId') -and $_.keyId) { $k = $_.keyId; $old = @($a.keyCredentials | Where-Object { $_.keyId -eq $k }); if (-not $old) { throw 'unknown keyId' }; $old[0] }
                    else { if (-not ($_.ContainsKey('key') -and $_.key)) { throw 'a new credential needs its key' }
                           @{ keyId = [guid]::NewGuid().ToString(); type = $_.type; usage = $_.usage; displayName = $_.displayName
                              customKeyIdentifier = [Convert]::ToBase64String([System.Security.Cryptography.SHA1]::HashData([Convert]::FromBase64String($_.key))) } } })
            }
            if ($b.Contains('requiredResourceAccess')) { $a.requiredResourceAccess = $b.requiredResourceAccess }
            return $null }
        '^/v1.0/servicePrincipals$' {
            $sp = @{ id = "sp-$($b.appId)"; appId = $b.appId }; $t.Sps.Add($sp); return ConvertTo-Obj $sp }
        '^/v1.0/servicePrincipals/([^/]+)/appRoleAssignments$' {
            $sid = $Matches[1]
            if ($Method -eq 'POST') { $t.Assignments.Add(@{ principalId = $b.principalId; resourceId = $b.resourceId; appRoleId = $b.appRoleId }); return $null }
            return ConvertTo-Obj @{ value = @($t.Assignments | Where-Object { $_.principalId -eq $sid }) } }
        default { throw "unexpected Graph call: $Method $Uri" }
    }
}

. (Join-Path $root 'tools' 'New-CopilotAuditApp.ps1') -TenantId 'contoso.onmicrosoft.com'   # functions only
$InformationPreference = 'SilentlyContinue'

New-Tenant
$cer1 = Join-Path $tmp 'one.cer'; $c1 = New-TestCer $cer1 'one'
$settings = Join-Path $tmp 'app.settings.psd1'
Set-Content -LiteralPath $settings -Value "@{`r`n    SqlServer = 'sql01'`r`n    TenantId = ''`r`n}"
$CertificatePath = $cer1; $SettingsPath = $settings
$out = Invoke-Main 6>$null
$app = $script:Tenant.Apps[0]
$graphEntry = @($app.requiredResourceAccess | Where-Object { $_.resourceAppId -eq $graphAppId })
Assert-That ($script:Tenant.Apps.Count -eq 1 -and $app.displayName -eq 'Copilot audit pipeline') 'creates one app, named as documented'
Assert-That (@($app.requiredResourceAccess).Count -eq 1 -and @($graphEntry[0].resourceAccess).Count -eq 3 -and
             -not (@($graphEntry[0].resourceAccess | Where-Object { $_.type -ne 'Role' -or $roles.Values -notcontains $_.id }))) `
    'asks for exactly the three Graph application permissions, and nothing else'
Assert-That ($script:Tenant.Sps.Count -eq 1 -and $script:Tenant.Assignments.Count -eq 3 -and
             -not (@($script:Tenant.Assignments | Where-Object { $_.resourceId -ne 'graph-sp' }))) 'creates the service principal and consents to all three'
Assert-That (@($app.keyCredentials).Count -eq 1 -and $app.keyCredentials[0].customKeyIdentifier -eq [Convert]::ToBase64String($c1.GetCertHash())) 'uploads the certificate'
Assert-That ($out.ClientId -eq 'client-0' -and $out.TenantId -eq 'tenant-guid' -and $out.CertificateThumbprint -eq $c1.Thumbprint) 'returns the tenant, client id and thumbprint'
$saved = Import-PowerShellDataFile $settings
Assert-That ($saved.TenantId -eq 'tenant-guid' -and $saved.ClientId -eq 'client-0' -and $saved.SqlServer -eq 'sql01') 'writes the ids into the settings file, keeping the rest'
Assert-That ((@($script:SignedInScopes) -join ',') -eq 'Application.ReadWrite.All,AppRoleAssignment.ReadWrite.All') 'signs in with the two admin scopes only'

$script:Tenant.Writes.Clear()
$null = Invoke-Main 6>$null
Assert-That ($script:Tenant.Writes.Count -eq 0) 'a second run with the same certificate writes nothing'

$cer2 = Join-Path $tmp 'two.cer'; $c2 = New-TestCer $cer2 'two'
$CertificatePath = $cer2
$null = Invoke-Main 6>$null
$ids = @($app.keyCredentials | ForEach-Object { $_.customKeyIdentifier })
Assert-That ($ids.Count -eq 2 -and $ids -contains [Convert]::ToBase64String($c1.GetCertHash()) -and $ids -contains [Convert]::ToBase64String($c2.GetCertHash())) `
    'a renewed certificate is added and the old one kept'

New-Tenant
$script:Tenant.Apps.Add(@{ id = 'obj-x'; appId = 'client-x'; displayName = "Copilot audit pipeline"; keyCredentials = @()
    requiredResourceAccess = @(@{ resourceAppId = 'other-api'; resourceAccess = @(@{ id = 'r1'; type = 'Role' }) },
                               @{ resourceAppId = $graphAppId; resourceAccess = @(@{ id = 'scope-1'; type = 'Scope' }, @{ id = $roles['User.Read.All']; type = 'Role' }) }) })
$CertificatePath = $null; $SettingsPath = $null
$out = Invoke-Main 6>$null
$a = $script:Tenant.Apps[0]
$g = @(@($a.requiredResourceAccess | Where-Object { $_.resourceAppId -eq $graphAppId })[0].resourceAccess)
Assert-That (@($a.requiredResourceAccess | Where-Object { $_.resourceAppId -eq 'other-api' }).Count -eq 1 -and
             @($g | Where-Object { $_.id -eq 'scope-1' -and $_.type -eq 'Scope' }).Count -eq 1 -and @($g).Count -eq 4) `
    'an existing app keeps its other permissions and gains the missing two'
Assert-That ($out.ClientId -eq 'client-x' -and $script:Tenant.Assignments.Count -eq 3) 'and gets consent for all three'

# ---------------------------------------------------------------------------
Write-Host "`n2. The pipeline's plan, and a step in its own process" -ForegroundColor Cyan

. (Join-Path $root 'Invoke-CopilotPipeline.ps1')
$s = @{ SqlServer = 'sql01'; Database = 'CopilotAudit'; LicenseDatabase = 'CopilotLicenseEvents'; TrustServerCertificate = $true
        TenantId = 't'; ClientId = 'c'; CertificateThumbprint = 'AB'; TargetDomain = 'users.contoso.com'; EmployeeTypes = @('EMP')
        Dashboard = $true; HistoryDays = 90 }
$today = [datetime]::new(2026, 9, 26, 0, 0, 0, [System.DateTimeKind]::Utc)

$n = Get-PipelineStep -Setting $s -Today $today -Root 'R:'
Assert-That (($n.Name -join '|') -eq 'User directory from AD|Copilot licenses from Graph|License events|Copilot interactions') 'nightly: the four steps, in the documented order'
$i = $n[3].Arguments
Assert-That ($i.Incremental -and $i.SessionLicenseDatabase -eq 'CopilotLicenseEvents' -and $i.Database -eq 'CopilotAudit' -and
             $i.ClientId -eq 'c' -and $i.CertificateThumbprint -eq 'AB' -and $i.TrustServerCertificate -and $n[3].Script -eq [System.IO.Path]::Combine('R:', 'Invoke-CopilotAuditBackfill.ps1')) `
    'nightly interactions: incremental, license-aware, signed in as the app'
$licOps = @($n[2].Arguments.Operations)
Assert-That ((($licOps | Select-Object -First 4) -join ',') -eq 'Change user license,Update user,Add user,Delete user' -and
             'BotCreate' -in $licOps -and 'BotUpdateOperation-BotPublish' -in $licOps -and $n[2].Arguments.Database -eq 'CopilotLicenseEvents') `
    'nightly license events: the four license operations, and Copilot Studio''s authoring ones (2.3.0)'
# 2.4.12: DEPLOYMENT.md step 7, the license history by hand, passes the same list. It gave the four license operations
# alone, and a backfill refuses a different list, so a hand run and -History stopped each other.
$guide = [System.IO.File]::ReadAllText((Join-Path $root 'docs' 'DEPLOYMENT.md'))
$listed = [regex]::Match($guide, '(?s)\$licenseOps = (.+?)\r?\n\.\\Invoke-CopilotAuditBackfill\.ps1')
$docOps = @([regex]::Matches($listed.Groups[1].Value, "'([^']+)'") | ForEach-Object { $_.Groups[1].Value })
$hOps = @((Get-PipelineStep -Setting $s -History -Today $today)[1].Arguments.Operations)
Assert-That ($listed.Success -and (($docOps | Sort-Object) -join ',') -eq (($licOps | Sort-Object) -join ',') -and (($hOps | Sort-Object) -join ',') -eq (($licOps | Sort-Object) -join ',')) `
    "DEPLOYMENT.md step 7 loads the license history with the pipeline's own $($licOps.Count) operations, as -History does ($($docOps.Count) listed)"
Assert-That ((@($n[0].Arguments.EmployeeTypes) -join ',') -eq 'EMP' -and $n[0].Arguments.TargetDomain -eq 'users.contoso.com' -and -not $n[0].Arguments.ContainsKey('ClientId')) `
    'the directory step reads AD with no Graph sign-in'

$h = Get-PipelineStep -Setting $s -History -Today $today -Root 'R:'
Assert-That (($h.Name -join '|') -eq 'Copilot interactions (history)|License events (history)|User directory from AD|Copilot licenses from Graph|Build everything') 'history: load, directory, licenses, then build'
Assert-That ($h[0].Arguments.StartDate -eq $today.AddDays(-90) -and $h[0].Arguments.Finalize -and $h[1].Arguments.StartDate -eq $today.AddDays(-90)) 'history: HistoryDays back, finalized'
Assert-That ((Get-PipelineStep -Setting $s -History -Days 30 -Today $today)[0].Arguments.StartDate -eq $today.AddDays(-30)) '-Days overrides the settings'
$b = $h[4].Arguments
Assert-That ($b.Dashboard -and (@($b.LicenseEventDatabases) -join ',') -eq 'CopilotLicenseEvents' -and $h[4].Script -eq [System.IO.Path]::Combine('R:', 'Install-CopilotAudit.ps1')) 'history ends with the one-command build'

$lean = @{ SqlServer = 'sql01'; Database = 'CopilotAudit'; TenantId = 't'; ClientId = 'c'; CertificateThumbprint = 'AB' }
$l = Get-PipelineStep -Setting $lean -Today $today
Assert-That (($l.Name -join '|') -eq 'Copilot licenses from Graph|Copilot interactions' -and -not $l[1].Arguments.ContainsKey('SessionLicenseDatabase')) 'no users domain or license database: those steps are left out'
$lh = Get-PipelineStep -Setting $lean -History -Today $today
Assert-That ($lh[-1].Arguments.SkipLicenseDatabase -and $lh[0].Arguments.StartDate -eq $today.AddDays(-180)) 'and history defaults to 180 days'

$bad = Join-Path $tmp 'bad.psd1'; Set-Content $bad "@{ SqlServer = 'x'; Database = 'y' }"
$err = $null; try { Get-PipelineSetting $bad } catch { $err = $_.Exception.Message }
Assert-That ($err -like '*TenantId is not set*Install-CopilotJobServer.ps1*') 'settings with no app say how to fix them'

$probe = Join-Path $tmp 'probe.ps1'
Set-Content $probe @'
param([string] $Database, [datetime] $StartDate, [string[]] $Operations, [switch] $Finalize, [int] $ExitWith = 0)
"$Database|$($StartDate.ToString('yyyy-MM-dd'))|$($Operations -join ';')|$([bool] $Finalize)" | Set-Content (Join-Path $PSScriptRoot 'probe.out')
exit $ExitWith
'@
$log = Join-Path $tmp 'step.log'
$code = Invoke-PipelineStep -Step ([pscustomobject]@{ Name = 'probe'; Script = $probe
    Arguments = @{ Database = 'Db'; StartDate = $today; Operations = @('A b', 'C'); Finalize = $true } }) -Log $log 6>$null
Assert-That ($code -eq 0 -and (Get-Content (Join-Path $tmp 'probe.out')) -eq 'Db|2026-09-26|A b;C|True') 'a step gets its arguments in its own process, switches and dates included'
$code = Invoke-PipelineStep -Step ([pscustomobject]@{ Name = 'fails'; Script = $probe; Arguments = @{ StartDate = $today; ExitWith = 3 } }) -Log $log 6>$null
Assert-That ($code -eq 3 -and (Get-Content $log -Raw) -like '*== probe*== fails*') 'its exit code comes back, and both steps are in the log'

# ---------------------------------------------------------------------------
Write-Host "`n3. Job server settings and the batch-logon right" -ForegroundColor Cyan

. (Join-Path $root 'tools' 'Install-CopilotJobServer.ps1')
$m = Merge-Setting -Saved @{ SqlServer = 'old'; TenantId = 't'; Dashboard = $true } -Given @{ SqlServer = 'new'; TargetDomain = 'users.contoso.com' }
Assert-That ($m.SqlServer -eq 'new' -and $m.TenantId -eq 't' -and $m.Dashboard -and $m.Database -eq 'CopilotAudit' -and $m.HistoryDays -eq 180) `
    'given values win over saved ones, which win over defaults'
$m.TenantId = "o'brien"
$file = Join-Path $tmp 'round.psd1'
Set-Content -LiteralPath $file -Value (ConvertTo-SettingText $m)
$back = Import-PowerShellDataFile $file
Assert-That ($back.TenantId -eq "o'brien" -and $back.Dashboard -is [bool] -and $back.Dashboard -and $back.HistoryDays -eq 180 -and
             (@($back.EmployeeTypes) -join ',') -eq 'EMP,NONW' -and $back.TrustServerCertificate -eq $false) 'the settings file reads back as the same values'
$fromPipeline = Get-PipelineStep -Setting $back -Today $today
Assert-That ($fromPipeline[0].Name -eq 'User directory from AD') 'and the pipeline plans from it'

# 2.4.12: a re-run keeps what was saved, the keys this script does not set and the empty values too.
$saved = @{ SqlServer = 'SQL1'; EmployeeTypes = @(); IncludeLeaveDates = $true; At = '03:30'; LicenseDatabase = '' }
$again = Merge-Setting -Saved $saved -Given @{ TenantId = 'contoso.onmicrosoft.com' }
Assert-That (@($again.EmployeeTypes).Count -eq 0 -and $again.IncludeLeaveDates -eq $true -and $again.At -eq '03:30' -and $again.LicenseDatabase -eq '' -and
             $again.TenantId -eq 'contoso.onmicrosoft.com') 'a re-run keeps EmployeeTypes = @(), IncludeLeaveDates, the task time and an empty license database'
Assert-That ((@((Merge-Setting -Saved @{ SqlServer = 'SQL1' } -Given @{}).EmployeeTypes) -join ',') -eq 'EMP,NONW') 'a default fills only a key that is missing'
Assert-That (@((Merge-Setting -Saved @{ SqlServer = 'SQL1'; EmployeeTypes = @('EMP') } -Given @{ EmployeeTypes = [string[]] @() }).EmployeeTypes).Count -eq 0) '-EmployeeTypes @() given is every type'
$f2 = Join-Path $tmp 'again.psd1'
Set-Content -LiteralPath $f2 -Value (ConvertTo-SettingText ($again + @{ Custom = 'kept' }))
$read2 = Import-PowerShellDataFile -LiteralPath $f2
Assert-That (@($read2.EmployeeTypes).Count -eq 0 -and $read2.IncludeLeaveDates -eq $true -and $read2.At -eq '03:30' -and $read2.Custom -eq 'kept') 'and writes them all back, a key it does not know included'

Assert-That ((Add-SidToRightLine -Line 'SeBatchLogonRight = *S-1-5-32-544,*S-1-5-32-551' -Right 'SeBatchLogonRight' -Sid 'S-1-5-21-9') -eq
             'SeBatchLogonRight = *S-1-5-32-544,*S-1-5-32-551,*S-1-5-21-9') 'the batch-logon right gains the account, keeping who had it'
Assert-That ($null -eq (Add-SidToRightLine -Line 'SeBatchLogonRight = *S-1-5-21-9' -Right 'SeBatchLogonRight' -Sid 'S-1-5-21-9')) 'and is left alone when the account has it'
Assert-That ((Add-SidToRightLine -Line '' -Right 'SeBatchLogonRight' -Sid 'S-1-5-21-9') -eq 'SeBatchLogonRight = *S-1-5-21-9') 'or starts the line when nobody had it'

Write-Host "`nSeveral tenants (Get-PipelineTenant, tools/Add-CopilotTenant.ps1)" -ForegroundColor Cyan
$multi = @{
    SqlServer = 'SQL1'; Database = 'CopilotAudit'; LicenseDatabase = 'CopilotLicenseEvents'; TenantId = 'contoso.onmicrosoft.com'; ClientId = 'c1'
    CertificateThumbprint = 'AB12'; TargetDomain = 'contoso.local'; TenantName = 'Contoso'
    Tenants = @(@{ Name = 'Fabrikam'; TenantId = 'fabrikam.onmicrosoft.com'; ClientId = 'f1'; Database = 'CopilotAudit_Fabrikam'; LicenseDatabase = 'CopilotLicenseEvents_Fabrikam'; TargetDomain = '' })
}
$ts = Get-PipelineTenant -Setting $multi
Assert-That ($ts.Count -eq 2 -and $ts[0].Name -eq 'Contoso' -and $ts[1].Name -eq 'Fabrikam') 'each tenant is run: the top-level one first, then each listed'
Assert-That ($ts[1].SqlServer -eq 'SQL1' -and $ts[1].CertificateThumbprint -eq 'AB12' -and $ts[1].Database -eq 'CopilotAudit_Fabrikam') 'a listed tenant inherits the shared values and overrides its own'
$fab = Get-PipelineStep -Setting $ts[1] -Today $today -Root 'R:'
Assert-That (@($fab | Where-Object { $_.Name -eq 'User directory from AD' }).Count -eq 0) 'an empty TargetDomain turns the directory step off for that tenant'
Assert-That (@($fab | Where-Object { $_.Arguments.Database -eq 'CopilotAudit' }).Count -eq 0 -and $fab[0].Arguments.TenantId -eq 'fabrikam.onmicrosoft.com') 'and its steps load its own databases with its own app'
Assert-That ((Get-PipelineTenant -Setting $multi -Only 'Fabrikam').Count -eq 1) '-Tenant runs one'
$bad = try { Get-PipelineTenant -Setting $multi -Only 'Nobody'; '' } catch { $_.Exception.Message }
Assert-That ($bad -like '*No tenant*Contoso, Fabrikam*') 'an unknown -Tenant lists the tenants there are'
$shared = @{} + $multi; $shared.Tenants = @(@{ Name = 'Clash'; TenantId = 'x'; ClientId = 'y'; Database = 'CopilotAudit' })
$bad = try { Get-PipelineTenant -Setting $shared; '' } catch { $_.Exception.Message }
Assert-That ($bad -like '*its own*') 'two tenants cannot share a database'
& {
    . (Join-Path $root 'tools' 'Add-CopilotTenant.ps1') -Name Dummy -TenantId dummy
    $more = Add-TenantSetting -Setting $multi -Tenant ([ordered]@{ Name = 'Northwind'; TenantId = 'nw'; ClientId = 'n1'; Database = 'CopilotAudit_Northwind' })
    Assert-That (@($more.Tenants).Count -eq 2 -and $more.Tenants[1].Name -eq 'Northwind') 'adding a tenant keeps the ones there'
    $dup = try { Add-TenantSetting -Setting $multi -Tenant @{ Name = 'Fabrikam'; Database = 'Other' }; '' } catch { $_.Exception.Message }
    Assert-That ($dup -like '*already a tenant*') 'and refuses a name in use'
    $text = & { param($n) . (Join-Path $root 'tools' 'Install-CopilotJobServer.ps1'); ConvertTo-SettingText $n } $more
    $f = Join-Path $tmp 'multi.psd1'; Set-Content -LiteralPath $f -Value $text
    $read = Import-PowerShellDataFile -LiteralPath $f
    Assert-That (@($read.Tenants).Count -eq 2 -and $read.Tenants[1].Database -eq 'CopilotAudit_Northwind' -and $read.TenantName -eq 'Contoso') 'the settings file keeps every tenant through a rewrite'
}
# 2.4.12: the guides' upgrade ran the installer once, which upgrades only the databases it is given (the first
# tenant's), and nothing else upgrades the others. The command they now give, run against a settings file with two
# further tenants, installs into each of their pairs.
$two = Join-Path $tmp 'two.psd1'
Set-Content -LiteralPath $two -Value "@{ Database = 'CopilotAudit'; Tenants = @(@{ Name = 'Fabrikam'; Database = 'CopilotAudit_Fabrikam'; LicenseDatabase = 'CopilotLicenseEvents_Fabrikam' }, @{ Name = 'Northwind'; Database = 'CopilotAudit_Northwind'; LicenseDatabase = 'CopilotLicenseEvents_Northwind' }) }"
foreach ($guide in (Join-Path $root 'docs' 'OPERATIONS.md'), (Join-Path $root 'docs' 'DEPLOYMENT.md')) {
    $loop = [regex]::Match([System.IO.File]::ReadAllText($guide), '`(foreach \(\$t in \(Import-PowerShellDataFile \.\\copilot-audit\.settings\.psd1\)\.Tenants\) \{[^`]+\})`').Groups[1].Value
    $script:installed = [System.Collections.Generic.List[string]]::new()
    function Install-Stub { param($SqlServer, $Database, $LicenseDatabase, [switch] $Dashboard) $script:installed.Add("$Database/$LicenseDatabase") }
    if ($loop) { & ([scriptblock]::Create($loop.Replace('.\copilot-audit.settings.psd1', "'$two'").Replace('.\Install-CopilotAudit.ps1', 'Install-Stub'))) }
    Assert-That (($script:installed -join ',') -eq 'CopilotAudit_Fabrikam/CopilotLicenseEvents_Fabrikam,CopilotAudit_Northwind/CopilotLicenseEvents_Northwind') `
        "$(Split-Path $guide -Leaf)'s upgrade installs into every further tenant's databases ($($script:installed -join ', '))"
}
}
finally { Remove-Item -LiteralPath $tmp -Recurse -Force -ErrorAction SilentlyContinue }

Write-Host ''
if ($script:Failures.Count) { Write-Host "$($script:Failures.Count) assertion(s) failed." -ForegroundColor Red; exit 1 }
Write-Host 'All provisioning tests passed.' -ForegroundColor Green
