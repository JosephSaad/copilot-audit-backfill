<#
    Tests the provisioning scripts' own logic, dot-sourced so nothing touches a
    tenant, the certificate store, Task Scheduler or SQL Server:

      1  tools\New-CopilotAuditApp.ps1 against a simulated tenant: the app, its
         three permissions and nothing else, consent, the certificate; a second
         run changes nothing; a renewed certificate keeps the old one; an app
         of the same name is taken only when no one else can sign in as it, and
         one named by its client id keeps its other permissions
      2  Invoke-CopilotPipeline.ps1: the nightly and history plans, a step in
         its own process - arguments across, exit codes back - and a tenant
         that cannot run failing alone
      3  tools\Install-CopilotJobServer.ps1: settings merged and written so they
         read back the same, the batch-logon right line, and the certificate
         renewed only with -RegisterApp, for every tenant's app
      4  dashboard\Install-CopilotInsights.ps1: what a re-run keeps, and the
         site back online after a failed run
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
        "^/v1.0/applications\?\`$filter=appId eq '([^']+)'&" {
            $id = $Matches[1]; return ConvertTo-Obj @{ value = @($t.Apps | Where-Object { $_.appId -eq $id }) } }
        '^/v1.0/applications/([^/?]+)/owners' {
            $a = $t.Apps | Where-Object { $_.id -eq $Matches[1] }
            return ConvertTo-Obj @{ value = @(@($(if ($a.ContainsKey('owners')) { $a.owners })) | Where-Object { $_ } | ForEach-Object { @{ id = $_ } }) } }
        '^/v1.0/me\?' { return ConvertTo-Obj @{ id = 'admin-1' } }
        '^/v1.0/applications$' {
            # Graph makes the signed-in administrator the owner of an app they create.
            $a = @{ id = "obj-$($t.Apps.Count)"; appId = "client-$($t.Apps.Count)"; displayName = $b.displayName
                    requiredResourceAccess = $b.requiredResourceAccess; keyCredentials = @(); passwordCredentials = @(); owners = @('admin-1') }
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
Assert-That ((@($script:SignedInScopes) -join ',') -eq 'Application.ReadWrite.All,AppRoleAssignment.ReadWrite.All,User.Read') 'signs in with the two admin scopes, and User.Read to know who you are'

$script:Tenant.Writes.Clear()
$null = Invoke-Main 6>$null
Assert-That ($script:Tenant.Writes.Count -eq 0) 'a second run with the same certificate writes nothing'

$cer2 = Join-Path $tmp 'two.cer'; $c2 = New-TestCer $cer2 'two'
$CertificatePath = $cer2
$null = Invoke-Main 6>$null
$ids = @($app.keyCredentials | ForEach-Object { $_.customKeyIdentifier })
Assert-That ($ids.Count -eq 2 -and $ids -contains [Convert]::ToBase64String($c1.GetCertHash()) -and $ids -contains [Convert]::ToBase64String($c2.GetCertHash())) `
    'a renewed certificate is added and the old one kept'

# 2.4.12: an app of the same name is not taken on its name alone. Any member can register an app, and one someone else
# had named 'Copilot audit pipeline', with a secret of their own, was given admin consent to the whole audit log and
# became the pipeline's app. By name, only the one app of that name with no secret and no owner but you is taken.
New-Tenant
$script:Tenant.Apps.Add(@{ id = 'obj-x'; appId = 'client-x'; displayName = "Copilot audit pipeline"; keyCredentials = @(); passwordCredentials = @()
    owners = @('someone-else')
    requiredResourceAccess = @(@{ resourceAppId = 'other-api'; resourceAccess = @(@{ id = 'r1'; type = 'Role' }) },
                               @{ resourceAppId = $graphAppId; resourceAccess = @(@{ id = 'scope-1'; type = 'Scope' }, @{ id = $roles['User.Read.All']; type = 'Role' }) }) })
$CertificatePath = $cer1; $SettingsPath = $null; $ClientId = $null
$refused = try { $null = Invoke-Main 6>$null; '' } catch { $_.Exception.Message }
Assert-That ($refused -like '*owners other than you*-ClientId*' -and $script:Tenant.Writes.Count -eq 0 -and $script:Tenant.Assignments.Count -eq 0) `
    "2.4.12: an app of that name someone else owns is not taken by name: no permission, consent or certificate ($($script:Tenant.Writes.Count) writes)"
$script:Tenant.Apps[0].owners = @('admin-1'); $script:Tenant.Apps[0].passwordCredentials = @(@{ keyId = 'pw-1'; displayName = 'mine' })
$refused = try { $null = Invoke-Main 6>$null; '' } catch { $_.Exception.Message }
Assert-That ($refused -like '*has a client secret*' -and $script:Tenant.Writes.Count -eq 0) 'nor one with a client secret, which the pipeline never uses'
$script:Tenant.Apps[0].passwordCredentials = @()
$script:Tenant.Apps.Add(@{ id = 'obj-y'; appId = 'client-y'; displayName = "Copilot audit pipeline"; keyCredentials = @(); passwordCredentials = @(); owners = @('admin-1'); requiredResourceAccess = @() })
$refused = try { $null = Invoke-Main 6>$null; '' } catch { $_.Exception.Message }
Assert-That ($refused -like '*2 app registrations are named*client-x, client-y*' -and $script:Tenant.Writes.Count -eq 0) 'nor either of two apps of that name'
$ClientId = 'client-nope'
$refused = try { $null = Invoke-Main 6>$null; '' } catch { $_.Exception.Message }
Assert-That ($refused -like '*no app registration with client id client-nope*' -and $script:Tenant.Writes.Count -eq 0) 'and a client id that names no app is refused'

# By its client id, as the job server setup passes the one in its settings, the app is taken whoever owns it.
$script:Tenant.Apps[0].owners = @('someone-else')
$ClientId = 'client-x'
$out = Invoke-Main 6>$null
$a = $script:Tenant.Apps[0]
$g = @(@($a.requiredResourceAccess | Where-Object { $_.resourceAppId -eq $graphAppId })[0].resourceAccess)
Assert-That (@($a.requiredResourceAccess | Where-Object { $_.resourceAppId -eq 'other-api' }).Count -eq 1 -and
             @($g | Where-Object { $_.id -eq 'scope-1' -and $_.type -eq 'Scope' }).Count -eq 1 -and @($g).Count -eq 4) `
    'an app named by -ClientId keeps its other permissions and gains the missing two'
Assert-That ($out.ClientId -eq 'client-x' -and $script:Tenant.Assignments.Count -eq 3 -and @($script:Tenant.Apps[1].keyCredentials).Count -eq 0) 'and gets consent for all three, and the certificate; the other app of that name gets nothing'
$ClientId = $null

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

# 2.4.12: sessions and bi may read several license databases. The nightly could pass only LicenseDatabase, so a
# database built with two failed every night on 'different settings'; SessionLicenseDatabases is the list it reads.
$two = @{} + $s; $two.SessionLicenseDatabases = @('CopilotLicenseEvents', ' CopilotLicenseArchive ')
$tn = Get-PipelineStep -Setting $two -Today $today
$th = Get-PipelineStep -Setting $two -History -Today $today
Assert-That ($tn[3].Arguments.SessionLicenseDatabase -eq 'CopilotLicenseEvents,CopilotLicenseArchive' -and $tn[2].Arguments.Database -eq 'CopilotLicenseEvents') `
    "2.4.12: the nightly reads every database in SessionLicenseDatabases, and still loads the license events into LicenseDatabase ($($tn[3].Arguments.SessionLicenseDatabase))"
Assert-That ((@($th[4].Arguments.LicenseEventDatabases) -join ',') -eq 'CopilotLicenseEvents,CopilotLicenseArchive' -and $th[4].Arguments.LicenseDatabase -eq 'CopilotLicenseEvents') `
    'and the history build reads the same list, so the two match'

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
param([string] $Database, [datetime] $StartDate, [string[]] $Operations, [switch] $Finalize, [int] $ExitWith = 0, [switch] $Throw)
$ErrorActionPreference = 'Stop'
"$Database|$($StartDate.ToString('yyyy-MM-dd'))|$($Operations -join ';')|$([bool] $Finalize)" | Set-Content (Join-Path $PSScriptRoot 'probe.out')
if ($Throw) { throw '2 window(s) failed after retries.' }
exit $ExitWith
'@
$log = Join-Path $tmp 'step.log'
$code = Invoke-PipelineStep -Step ([pscustomobject]@{ Name = 'probe'; Script = $probe
    Arguments = @{ Database = 'Db'; StartDate = $today; Operations = @('A b', 'C'); Finalize = $true } }) -Log $log 6>$null
Assert-That ($code -eq 0 -and (Get-Content (Join-Path $tmp 'probe.out')) -eq 'Db|2026-09-26|A b;C|True') 'a step gets its arguments in its own process, switches and dates included'
$code = Invoke-PipelineStep -Step ([pscustomobject]@{ Name = 'fails'; Script = $probe; Arguments = @{ StartDate = $today; ExitWith = 3 } }) -Log $log 6>$null
Assert-That ($code -eq 3 -and (Get-Content $log -Raw) -like '*== probe*== fails*') 'its exit code comes back, and both steps are in the log'
# 2.4.12: a backfill that gave up on a window throws now; the step must come back failed, or -History carries on.
$code = Invoke-PipelineStep -Step ([pscustomobject]@{ Name = 'throws'; Script = $probe; Arguments = @{ StartDate = $today; Throw = $true } }) -Log $log 6>$null
Assert-That ($code -ne 0) "a step that throws comes back failed (exit code $code), so the pipeline stops there"

& {
    # 2.4.12: Export-CopilotSessionCsv -Build rebuilds the production sessions; it builds them as the last build did.
    . (Join-Path $root 'tools' 'Export-CopilotSessionCsv.ps1')
    $last = [pscustomobject]@{ ContextFallback = 'User'; LicenseEventDatabase = 'CopilotLicenseEvents,CopilotLicenseArchive' }
    $same = Resolve-BuildSetting -Last $last -Given @{}
    Assert-That ($same.ContextFallback -eq 'User' -and $same.LicenseEventDatabase -eq 'CopilotLicenseArchive,CopilotLicenseEvents' -and $same.Changed.Count -eq 0) `
        "2.4.12: the session export's -Build takes the last build's settings when none are given ($($same.LicenseEventDatabase))"
    $reordered = Resolve-BuildSetting -Last $last -Given @{ LicenseEventDatabase = ' CopilotLicenseArchive , CopilotLicenseEvents' }
    Assert-That ($reordered.Changed.Count -eq 0) 'the same databases in another order or spacing are the same settings'
    $loc = try { [void] (Resolve-BuildSetting -Last $last -Given @{ ContextFallback = 'Location' }); '' } catch { $_.Exception.Message }
    $none = try { [void] (Resolve-BuildSetting -Last $last -Given @{ LicenseEventDatabase = '' }); '' } catch { $_.Exception.Message }
    Assert-That ($loc -like "*different settings*" -and $none -like '*different settings*') 'another context rule, or no license history, is refused'
    $forced = Resolve-BuildSetting -Last $last -Given @{ ContextFallback = 'Location' } -Force
    Assert-That ($forced.ContextFallback -eq 'Location' -and $forced.Changed.Count -eq 1) 'unless -Force, which builds with it and says what changed'
    $first = Resolve-BuildSetting -Last $null -Given @{}
    Assert-That ($first.ContextFallback -eq 'User' -and $null -eq $first.LicenseEventDatabase) 'a first build takes User and no license history, as before'
}

# 2.4.12: a tenant that cannot run is that tenant's failure, and the next tenant still runs. One whose database was
# offline or restoring threw out of the loop: the tenants after it did not run and no failure line was written. Here
# every tenant's database is a port nothing listens on, as an unreachable one is.
$downDir = Join-Path $tmp 'down'; New-Item -ItemType Directory -Path $downDir -Force | Out-Null
$downSettings = Join-Path $downDir 'down.psd1'
Set-Content -LiteralPath $downSettings -Value @"
@{
    SqlServer = 'sql01'; Database = 'CopilotAudit'; TenantId = 't'; ClientId = 'c'; CertificateThumbprint = 'AB'; TenantName = 'Contoso'
    ConnectionString = 'Server=tcp:127.0.0.1,1;Database=CopilotAudit;Integrated Security=true;Connect Timeout=2'
    LogPath = '$($downDir.Replace("'", "''"))'
    Tenants = @(@{ Name = 'Fabrikam'; TenantId = 'f'; ClientId = 'fc'; Database = 'CopilotAudit_Fabrikam' })
}
"@
& pwsh -NoProfile -NonInteractive -File (Join-Path $root 'Invoke-CopilotPipeline.ps1') -SettingsPath $downSettings *> $null
$downExit = $LASTEXITCODE
$downLog = @(Get-ChildItem -LiteralPath $downDir -Filter 'nightly-*.log' | ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw }) -join ''
Assert-That ($downExit -eq 1 -and $downLog -match '\[Contoso\] Could not run' -and $downLog -match '\[Fabrikam\] Could not run' -and
             $downLog -match 'Finished with failures: Contoso, Fabrikam') `
    "2.4.12: one tenant's unreachable database fails that tenant, the next still runs, and the run ends with the failures named (exit $downExit)"

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

# 2.4.12: the certificate is renewed only with -RegisterApp, which uploads the new one. Any run within 30 days of expiry
# (moving the task with -At, say) made a new certificate and put it in the settings, uploaded nowhere, so the nightly
# sign-in failed while the old certificate still worked. The store is stood in for here, in a scope of its own.
& {
    $near = [pscustomobject]@{ Subject = 'CN=Copilot audit pipeline'; NotAfter = (Get-Date).AddDays(20); Thumbprint = 'NEAR'; HasPrivateKey = $true }
    $gone = [pscustomobject]@{ Subject = 'CN=Copilot audit pipeline'; NotAfter = (Get-Date).AddDays(-1); Thumbprint = 'GONE'; HasPrivateKey = $true }
    $script:StoreHolds = $near
    function Get-Item { param([string] $LiteralPath, $ErrorAction) $script:StoreHolds }
    function Get-ChildItem { param($Path) }
    $kept = Get-PipelineCertificate -Thumbprint 'NEAR'
    $renewed = Get-PipelineCertificate -Thumbprint 'NEAR' -Renew
    $script:StoreHolds = $gone
    $expired = Get-PipelineCertificate -Thumbprint 'GONE'
    Assert-That ($kept.Thumbprint -eq 'NEAR' -and $null -eq $renewed -and $null -eq $expired) `
        '2.4.12: a certificate within 30 days of expiry is kept without -RegisterApp, and renewed with it (or once expired)'
}
# And -RegisterApp puts it on every tenant's app that signs in with it, by its client id: only the first tenant's had it.
$apps = Get-AppToRegister @{ TenantId = 't1'; ClientId = 'c1'; Tenants = @(@{ Name = 'Fabrikam'; TenantId = 't2'; ClientId = 'c2' },
                                                                       @{ Name = 'Own'; TenantId = 't3'; ClientId = 'c3'; CertificateThumbprint = 'OWN' }) }
Assert-That ((@($apps | ForEach-Object { "$($_.TenantId)/$($_.ClientId)" }) -join ',') -eq 't1/c1,t2/c2' -and $apps[0].First -and -not $apps[1].First) `
    '2.4.12: -RegisterApp uploads the certificate to the app of every tenant that uses it, each by its client id'
Assert-That (@(Get-AppToRegister @{ TenantId = 't1'; ClientId = '' }).Count -eq 1) 'and to the first tenant''s alone when there are no others, found by name the first time'

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

# ---------------------------------------------------------------------------
Write-Host "`n4. The dashboard installer: a re-run keeps the site's settings, and a failed one its site (2.4.12)" -ForegroundColor Cyan

# It needs IIS and an elevated Windows PowerShell 5.1, so its own functions are taken out of it and tested, and the
# shape of the run is checked from its syntax tree.
$dashInstaller = Join-Path $root 'dashboard' 'Install-CopilotInsights.ps1'
$dashAst = [System.Management.Automation.Language.Parser]::ParseFile($dashInstaller, [ref] $null, [ref] $null)
foreach ($f in $dashAst.FindAll({ param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] }, $false)) {
    if ($f.Name -in 'ConvertTo-Thumbprint', 'ConvertTo-TenantList', 'Resolve-SiteSetting') { . ([scriptblock]::Create($f.Extent.Text)) }
}

# A re-run with the package and the server alone: the site's database, TrustServerCertificate and gMSA are kept, and
# no binding is added. They went back to CopilotAudit, false and the pool's own identity, and HTTP on port 80 appeared.
$gmsaSite = 'Server=SQLPROD01;Database=CopilotAudit_Main;Integrated Security=true;Encrypt=true;TrustServerCertificate=true'
$re = Resolve-SiteSetting -Given @('PackagePath', 'SqlServer') -ConnectionString $gmsaSite -PoolIdentityType 'SpecificUser' -PoolUserName 'CORP\gmsa-insights$' `
        -SiteExists $true -Database 'CopilotAudit' -TrustServerCertificate $false -AppPoolIdentity ''
Assert-That ($re.Database -eq 'CopilotAudit_Main' -and $re.TrustServerCertificate -and $re.AppPoolIdentity -eq 'CORP\gmsa-insights$' -and $re.KeepPoolIdentity -and -not $re.NewBinding) `
    '2.4.12: a re-run keeps the database, TrustServerCertificate, the app pool''s account and the bindings it is not given'
$given = Resolve-SiteSetting -Given @('PackagePath', 'SqlServer', 'Database', 'TrustServerCertificate', 'AppPoolIdentity', 'CertificateThumbprint') -ConnectionString $gmsaSite `
        -PoolIdentityType 'SpecificUser' -PoolUserName 'CORP\gmsa-insights$' -SiteExists $true -Database 'CopilotAudit_Other' -TrustServerCertificate $false -AppPoolIdentity 'CORP\gmsa-other$'
Assert-That ($given.Database -eq 'CopilotAudit_Other' -and -not $given.TrustServerCertificate -and $given.AppPoolIdentity -eq 'CORP\gmsa-other$' -and -not $given.KeepPoolIdentity -and $given.NewBinding) `
    'what is given wins, -TrustServerCertificate:$false included'
$own = Resolve-SiteSetting -Given @('PackagePath', 'SqlServer', 'AppPoolIdentity') -ConnectionString $gmsaSite -PoolIdentityType 'SpecificUser' -PoolUserName 'CORP\gmsa-insights$' `
        -SiteExists $true -Database 'CopilotAudit' -TrustServerCertificate $false -AppPoolIdentity ''
Assert-That (-not $own.AppPoolIdentity -and -not $own.KeepPoolIdentity) "-AppPoolIdentity '' goes back to the pool's own identity"
$fresh = Resolve-SiteSetting -Given @('PackagePath', 'SqlServer') -ConnectionString '' -PoolIdentityType '' -PoolUserName '' -SiteExists $false `
        -Database 'CopilotAudit' -TrustServerCertificate $false -AppPoolIdentity ''
Assert-That ($fresh.Database -eq 'CopilotAudit' -and -not $fresh.TrustServerCertificate -and -not $fresh.AppPoolIdentity -and $fresh.NewBinding) 'and a first install takes the defaults, with its binding'
Assert-That ((ConvertTo-Thumbprint "$([char] 0x200E)3f 2a 9B 0c") -eq '3F2A9B0C') 'a thumbprint copied from the certificate dialog loses its spaces and invisible mark'
$badTenant = try { [void] (ConvertTo-TenantList -Tenant 'Contoso=CopilotAudit', 'Fabrikam=Copilot.Audit' -SqlServer 's' -Trust 'false'); '' } catch { $_.Exception.Message }
$twice = try { [void] (ConvertTo-TenantList -Tenant 'A=X', 'a=Y' -SqlServer 's' -Trust 'false'); '' } catch { $_.Exception.Message }
$okTenants = ConvertTo-TenantList -Tenant 'Contoso=CopilotAudit', 'Fabrikam=CopilotAudit_Fabrikam' -SqlServer 's' -Trust 'true'
Assert-That ($badTenant -like "*'Fabrikam=Copilot.Audit' is not Name=Database*" -and $twice -like '*must be different*' -and
             ($okTenants.Databases -join ',') -eq 'CopilotAudit,CopilotAudit_Fabrikam' -and $okTenants.Entries[1].ConnectionString -like '*Database=CopilotAudit_Fabrikam*TrustServerCertificate=true') `
    '-Tenant is read, and refused when it is not Name=Database or names a tenant twice'

# A failed run brings the site back: the offline page is put up inside a try whose finally takes it down, and the
# inputs that used to fail after it went up (the certificate, -Tenant, the settings file) are checked before it.
$offlineAt = @($dashAst.FindAll({ param($n) $n -is [System.Management.Automation.Language.CommandAst] -and $n.GetCommandName() -eq 'Set-Content' -and $n.Extent.Text -like '*$offline*' }, $true))
$tryOf = if ($offlineAt) { $p = $offlineAt[0].Parent; while ($p -and $p -isnot [System.Management.Automation.Language.TryStatementAst]) { $p = $p.Parent }; $p }
Assert-That ($offlineAt.Count -eq 1 -and $tryOf -and $tryOf.Finally -and $tryOf.Finally.Extent.Text -like '*Remove-Item $offline*') `
    '2.4.12: the site goes offline inside a try whose finally always brings it back'
$checks = @($dashAst.FindAll({ param($n) ($n -is [System.Management.Automation.Language.CommandAst] -and $n.GetCommandName() -in 'ConvertTo-TenantList', 'ConvertFrom-Json') -or
                                         ($n -is [System.Management.Automation.Language.ThrowStatementAst] -and $n.Extent.Text -like '*No certificate*') }, $true))
Assert-That ($checks.Count -eq 3 -and @($checks | Where-Object { $_.Extent.StartOffset -gt $offlineAt[0].Extent.StartOffset }).Count -eq 0) `
    'and the certificate, -Tenant and the settings file are checked before it goes offline'

# 2.4.12: the guides' upgrade ran the installer once, which upgrades only the databases it is given (the first
# tenant's), and nothing else upgrades the others. The command they now give, run against a settings file with two
# further tenants, installs into each of their pairs. A tenant without its own LicenseDatabase or
# SessionLicenseDatabases takes the top-level one, as Get-PipelineTenant gives the nightly run, and a blank
# license database is skipped (-SkipLicenseDatabase), not created with an empty name.
$two = Join-Path $tmp 'two.psd1'
Set-Content -LiteralPath $two -Value ("@{ Database = 'CopilotAudit'; LicenseDatabase = 'CopilotLicenseEvents'; SessionLicenseDatabases = @('CopilotLicenseEvents', 'CopilotLicenseArchive'); Tenants = @(" +
    "@{ Name = 'Fabrikam'; Database = 'CopilotAudit_Fabrikam'; LicenseDatabase = 'CopilotLicenseEvents_Fabrikam'; SessionLicenseDatabases = @('CopilotLicenseEvents_Fabrikam') }, " +
    "@{ Name = 'Northwind'; Database = 'CopilotAudit_Northwind' }, " +
    "@{ Name = 'Tailspin'; Database = 'CopilotAudit_Tailspin'; LicenseDatabase = ''; SessionLicenseDatabases = @() }) }")
$want = 'CopilotAudit_Fabrikam/CopilotLicenseEvents_Fabrikam/False/CopilotLicenseEvents_Fabrikam,' +
        'CopilotAudit_Northwind/CopilotLicenseEvents/False/CopilotLicenseEvents+CopilotLicenseArchive,' +
        'CopilotAudit_Tailspin//True/'
foreach ($guide in (Join-Path $root 'docs' 'OPERATIONS.md'), (Join-Path $root 'docs' 'DEPLOYMENT.md')) {
    $loop = [regex]::Match([System.IO.File]::ReadAllText($guide),
        '(\$s = Import-PowerShellDataFile \.\\copilot-audit\.settings\.psd1[;\s]+foreach \(\$t in \$s\.Tenants\) \{.+?\.\\Install-CopilotAudit\.ps1 @a\s*\})',
        [System.Text.RegularExpressions.RegexOptions]::Singleline).Groups[1].Value
    $script:installed = [System.Collections.Generic.List[string]]::new()
    function Install-Stub { param($SqlServer, $Database, $LicenseDatabase, [switch] $SkipLicenseDatabase, [string[]] $LicenseEventDatabases, [switch] $Dashboard)
        $script:installed.Add("$Database/$LicenseDatabase/$([bool] $SkipLicenseDatabase)/$($LicenseEventDatabases -join '+')") }
    if ($loop) { & ([scriptblock]::Create($loop.Replace('.\copilot-audit.settings.psd1', "'$two'").Replace('.\Install-CopilotAudit.ps1', 'Install-Stub'))) }
    Assert-That (($script:installed -join ',') -eq $want) `
        "$(Split-Path $guide -Leaf)'s upgrade installs into every further tenant's databases, with the license databases the nightly run reads ($($script:installed -join ', '))"
}
}
finally { Remove-Item -LiteralPath $tmp -Recurse -Force -ErrorAction SilentlyContinue }

Write-Host ''
if ($script:Failures.Count) { Write-Host "$($script:Failures.Count) assertion(s) failed." -ForegroundColor Red; exit 1 }
Write-Host 'All provisioning tests passed.' -ForegroundColor Green
