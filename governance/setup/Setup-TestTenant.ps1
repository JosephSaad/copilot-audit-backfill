<#
.SYNOPSIS
    Sets up a Microsoft 365 tenant for the Copilot Governance Workbench test environment.

.DESCRIPTION
    Creates or updates, by display name (safe to rerun):
      - two certificates in Cert:\CurrentUser\My (no client secrets);
      - the Web app registration (OIDC sign-in, five app roles, delegated Power Platform API scopes);
      - the Worker app registration (Graph application permissions, no Mail.Send in Entra);
      - test security groups: central role groups and one policy scope group per LOB tier;
      - an entitlement management catalog with one access package per LOB tier, and the Worker
        as catalog reader on that catalog only;
      - a shared mailbox scoped to the Worker through Exchange RBAC for Applications;
      - the Power Platform reader role for the Worker (preview API);
      - the Teams app (activity feed) uploaded to the org catalog and installed for you.
      - Copilot seats: LicenseAssignment.Read.All and Reports.Read.All for the Worker, and cloud
        stand-in groups for the AD seat groups (GSGu_MCO_CopilotM365 and one tracking group per LOB).
    Admin consent is not granted by the script: it opens the consent page so you approve it yourself.
    Writes the IDs it made to config\tenant.test.json (git-ignored). Nothing secret is written.

.PARAMETER Steps
    Which parts to run. Default: all.

.EXAMPLE
    pwsh -File setup\Setup-TestTenant.ps1 -TenantDomain cloudsconnected.com -AdminUpn joseph.saad@cloudsconnected.com
#>
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingConvertToSecureStringWithPlainText', '', Justification = 'Connect-MgGraph takes the access token MSAL just returned as a SecureString; the token is not a stored secret.')]
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $TenantDomain,
    [Parameter(Mandatory)] [string] $AdminUpn,
    [string] $Mailbox = '',
    [string] $WebBaseUrl = 'http://localhost:5291',
    [string[]] $Steps = @('Certificates', 'Apps', 'Groups', 'AccessPackages', 'Consent', 'Mailbox', 'PowerPlatform', 'Teams', 'Seats'),
    [string] $Suffix = 'Test'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# pwsh -File passes "A,B" as one string: split, then check every name.
$known = 'Certificates', 'Apps', 'Groups', 'AccessPackages', 'Consent', 'Mailbox', 'PowerPlatform', 'Teams', 'Seats'
$Steps = @($Steps | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
foreach ($s in $Steps) { if ($known -notcontains $s) { throw "Unknown step '$s'. Steps: $($known -join ', ')" } }

$repo = Split-Path -Parent $PSScriptRoot
$configPath = Join-Path $repo 'config\tenant.test.json'
if (-not $Mailbox) { $Mailbox = "workbench@$TenantDomain" }

$GraphAppId = '00000003-0000-0000-c000-000000000000'
$PowerPlatformApiAppId = '8578e004-a5c6-46e7-913e-12f58912df43'
$PowerPlatformReaderRoleId = 'c886ad2e-27f7-4874-8381-5849b8d8a090'

# --------------------------------------------------------------------------------------------
# Helpers
# --------------------------------------------------------------------------------------------
function Write-Step([string] $text) { Write-Host "`n== $text" -ForegroundColor Cyan }
. (Join-Path $PSScriptRoot 'EdgeSignIn.ps1')

# First-party public clients that already hold the delegated permissions this script uses.
$GraphPowerShellClientId = '14d82eec-204b-4c2f-b7e8-296a70dab67e'
$AzurePowerShellClientId = '1950a258-227b-4e31-a9cf-717495945fc2'
function Write-Done([string] $text) { Write-Host "   ok  $text" -ForegroundColor Green }
function Write-Note([string] $text) { Write-Host "   --  $text" -ForegroundColor Yellow }

function Read-Config {
    if (Test-Path $configPath) { return (Get-Content $configPath -Raw | ConvertFrom-Json -AsHashtable) }
    return @{}
}
function Save-Config([hashtable] $cfg) {
    New-Item -ItemType Directory -Force (Split-Path $configPath) | Out-Null
    $cfg | ConvertTo-Json -Depth 8 | Set-Content -Path $configPath -Encoding utf8
}

function Invoke-Graph {
    param([string] $Method = 'GET', [string] $Uri, $Body = $null, [string] $ContentType = 'application/json')
    $full = if ($Uri -like 'https://*') { $Uri } else { "https://graph.microsoft.com/v1.0$Uri" }
    if ($null -ne $Body) {
        $payload = if ($Body -is [string] -or $Body -is [byte[]]) { $Body } else { $Body | ConvertTo-Json -Depth 20 }
        return Invoke-MgGraphRequest -Method $Method -Uri $full -Body $payload -ContentType $ContentType -OutputType Hashtable
    }
    return Invoke-MgGraphRequest -Method $Method -Uri $full -OutputType Hashtable
}
function Get-GraphAll([string] $Uri) {
    $items = [System.Collections.Generic.List[object]]::new()
    $next = $Uri
    while ($next) {
        $page = Invoke-Graph -Uri $next
        foreach ($v in $page['value']) { $items.Add($v) }
        $next = if ($page.ContainsKey('@odata.nextLink')) { $page['@odata.nextLink'] } else { $null }
    }
    return , $items
}
function Find-One([string] $Uri) {
    $r = Invoke-Graph -Uri $Uri
    if ($r['value'].Count -gt 0) { return $r['value'][0] }
    return $null
}
function ConvertTo-ODataLiteral([string] $s) { return $s.Replace("'", "''") }

function Get-OrCreateCertificate([string] $subject) {
    $cert = Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Subject -eq $subject -and $_.NotAfter -gt (Get-Date).AddDays(30) -and $_.HasPrivateKey } |
        Sort-Object NotAfter -Descending | Select-Object -First 1
    if ($cert) { Write-Done "certificate $subject exists ($($cert.Thumbprint))"; return $cert }
    $cert = New-SelfSignedCertificate -Subject $subject -CertStoreLocation Cert:\CurrentUser\My -KeyExportPolicy NonExportable `
        -KeySpec Signature -KeyLength 2048 -KeyAlgorithm RSA -HashAlgorithm SHA256 -NotAfter (Get-Date).AddYears(1)
    Write-Done "certificate $subject created ($($cert.Thumbprint))"
    return $cert
}

function Get-ServicePrincipalByAppId([string] $appId) {
    return Find-One "/servicePrincipals?`$filter=appId eq '$appId'"
}
function Get-OrCreateServicePrincipal([string] $appId) {
    $sp = Get-ServicePrincipalByAppId $appId
    if ($sp) { return $sp }
    return Invoke-Graph -Method POST -Uri '/servicePrincipals' -Body @{ appId = $appId }
}

function Set-AppRegistration {
    param([string] $DisplayName, [hashtable] $Definition)
    $existing = Find-One "/applications?`$filter=displayName eq '$(ConvertTo-ODataLiteral $DisplayName)'"
    if ($existing) {
        Invoke-Graph -Method PATCH -Uri "/applications/$($existing['id'])" -Body $Definition | Out-Null
        Write-Done "app registration '$DisplayName' updated ($($existing['appId']))"
        return Invoke-Graph -Uri "/applications/$($existing['id'])"
    }
    $Definition['displayName'] = $DisplayName
    $Definition['signInAudience'] = 'AzureADMyOrg'
    $app = Invoke-Graph -Method POST -Uri '/applications' -Body $Definition
    Write-Done "app registration '$DisplayName' created ($($app['appId']))"
    return $app
}

function Get-KeyCredential($cert, [string] $name) {
    return @{
        type        = 'AsymmetricX509Cert'
        usage       = 'Verify'
        key         = [Convert]::ToBase64String($cert.RawData)
        displayName = $name
    }
}

function Get-OrCreateGroup([string] $name, [string] $description) {
    $g = Find-One "/groups?`$filter=displayName eq '$(ConvertTo-ODataLiteral $name)'"
    if ($g) { return $g }
    $nick = ($name -replace '[^A-Za-z0-9]', '').ToLowerInvariant()
    if ($nick.Length -gt 56) { $nick = $nick.Substring(0, 56) }
    $g = Invoke-Graph -Method POST -Uri '/groups' -Body @{
        displayName     = $name
        description     = $description
        mailEnabled     = $false
        mailNickname    = $nick
        securityEnabled = $true
    }
    Write-Done "group '$name' created"
    return $g
}
function Add-GroupMember([string] $groupId, [string] $userId) {
    $members = Get-GraphAll "/groups/$groupId/members?`$select=id"
    if ($members | Where-Object { $_['id'] -eq $userId }) { return }
    Invoke-Graph -Method POST -Uri "/groups/$groupId/members/`$ref" -Body @{ '@odata.id' = "https://graph.microsoft.com/v1.0/directoryObjects/$userId" } | Out-Null
}

# --------------------------------------------------------------------------------------------
# Sign in
# --------------------------------------------------------------------------------------------
$cfg = Read-Config
$cfg['tenantDomain'] = $TenantDomain
$cfg['adminUpn'] = $AdminUpn
$cfg['webBaseUrl'] = $WebBaseUrl
$cfg['mailbox'] = $Mailbox

$needGraph = $Steps | Where-Object { $_ -ne 'Certificates' -and $_ -ne 'Mailbox' }
if ($needGraph) {
    Write-Step 'Signing in to Microsoft Graph in Microsoft Edge'
    Import-Module Microsoft.Graph.Authentication
    $graphScopes = @(
        'Application.ReadWrite.All', 'AppRoleAssignment.ReadWrite.All', 'Group.ReadWrite.All', 'User.Read.All',
        'EntitlementManagement.ReadWrite.All', 'AppCatalog.ReadWrite.All', 'TeamsAppInstallation.ReadWriteAndConsentForUser',
        'Organization.Read.All', 'DelegatedPermissionGrant.Read.All') | ForEach-Object { "https://graph.microsoft.com/$_" }
    $graphToken = Get-EdgeToken -ClientId $GraphPowerShellClientId -Tenant $TenantDomain -Scopes $graphScopes -LoginHint $AdminUpn
    Connect-MgGraph -AccessToken (ConvertTo-SecureString $graphToken.AccessToken -AsPlainText -Force) -NoWelcome
    $org = (Invoke-Graph -Uri '/organization?$select=id,displayName')['value'][0]
    $cfg['tenantId'] = $org['id']
    $me = Invoke-Graph -Uri "/users/$([uri]::EscapeDataString($AdminUpn))?`$select=id,displayName,userPrincipalName"
    $cfg['adminObjectId'] = $me['id']
    Write-Done "signed in to $($org['displayName']) ($($org['id'])) as $($me['userPrincipalName'])"
}

# --------------------------------------------------------------------------------------------
# Certificates
# --------------------------------------------------------------------------------------------
if ($Steps -contains 'Certificates' -or $Steps -contains 'Apps') {
    Write-Step 'Certificates (CurrentUser\My, private key not exportable)'
    $webCert = Get-OrCreateCertificate "CN=CopilotGovernanceWeb-$Suffix"
    $workerCert = Get-OrCreateCertificate "CN=CopilotGovernanceWorker-$Suffix"
    $cfg['webCertificateSubject'] = $webCert.Subject
    $cfg['webCertificateThumbprint'] = $webCert.Thumbprint
    $cfg['workerCertificateSubject'] = $workerCert.Subject
    $cfg['workerCertificateThumbprint'] = $workerCert.Thumbprint
    $cfg['workerCertificateNotAfter'] = $workerCert.NotAfter.ToString('o')
    Save-Config $cfg
}

# --------------------------------------------------------------------------------------------
# App registrations
# --------------------------------------------------------------------------------------------
if ($Steps -contains 'Apps') {
    Write-Step 'App registrations'
    $graphSp = Get-ServicePrincipalByAppId $GraphAppId
    $ppSp = Get-OrCreateServicePrincipal $PowerPlatformApiAppId

    $graphScope = { param($v) ($graphSp['oauth2PermissionScopes'] | Where-Object { $_['value'] -eq $v })['id'] }
    $graphRole = { param($v) ($graphSp['appRoles'] | Where-Object { $_['value'] -eq $v })['id'] }
    $ppScope = {
        param($v)
        $s = $ppSp['oauth2PermissionScopes'] | Where-Object { $_['value'] -eq $v }
        if (-not $s) { throw "Power Platform API has no delegated scope '$v' in this tenant." }
        $s['id']
    }

    # Stable IDs for the five app roles so reruns never orphan role assignments.
    $roleIds = [ordered]@{
        'Workbench.User'     = '7b0c2d1e-1a51-4c6e-9a2e-1f6a0b9d0c01'
        'Workbench.Central'  = '7b0c2d1e-1a51-4c6e-9a2e-1f6a0b9d0c02'
        'Workbench.Operator' = '7b0c2d1e-1a51-4c6e-9a2e-1f6a0b9d0c03'
        'Workbench.Admin'    = '7b0c2d1e-1a51-4c6e-9a2e-1f6a0b9d0c04'
        'Workbench.Auditor'  = '7b0c2d1e-1a51-4c6e-9a2e-1f6a0b9d0c05'
    }
    $roleText = @{
        'Workbench.User'     = 'Any assigned employee: start page and own requests.'
        'Workbench.Central'  = 'Central functions: queues and reviews (function and scope resolved in SQL).'
        'Workbench.Operator' = 'Platform Operations: fulfillment, verification, inventory sync, attestation.'
        'Workbench.Admin'    = 'Workbench administrators: configuration only, no business approvals.'
        'Workbench.Auditor'  = 'Read-only access to decisions, evidence and audit.'
    }
    $appRoles = foreach ($k in $roleIds.Keys) {
        @{
            id                 = $roleIds[$k]
            value              = $k
            displayName        = $k
            description        = $roleText[$k]
            allowedMemberTypes = @('User')
            isEnabled          = $true
        }
    }

    $webDef = @{
        web                    = @{
            redirectUris          = @("$WebBaseUrl/signin-oidc")
            logoutUrl             = "$WebBaseUrl/signout-oidc"
            implicitGrantSettings = @{ enableIdTokenIssuance = $false; enableAccessTokenIssuance = $false }
        }
        appRoles               = @($appRoles)
        keyCredentials         = @(Get-KeyCredential $webCert "CopilotGovernanceWeb-$Suffix")
        requiredResourceAccess = @(
            @{
                resourceAppId  = $GraphAppId
                resourceAccess = @(
                    @{ id = (& $graphScope 'User.Read'); type = 'Scope' },
                    @{ id = (& $graphScope 'openid'); type = 'Scope' },
                    @{ id = (& $graphScope 'profile'); type = 'Scope' },
                    @{ id = (& $graphScope 'offline_access'); type = 'Scope' }
                )
            },
            @{
                resourceAppId  = $PowerPlatformApiAppId
                resourceAccess = @(
                    @{ id = (& $ppScope 'ResourceQuery.Resources.Read'); type = 'Scope' },
                    @{ id = (& $ppScope 'EnvironmentManagement.Environments.Read'); type = 'Scope' },
                    @{ id = (& $ppScope 'Licensing.Allocations.Read'); type = 'Scope' },
                    @{ id = (& $ppScope 'Licensing.BillingPolicies.Read'); type = 'Scope' }
                )
            }
        )
    }
    $webApp = Set-AppRegistration -DisplayName "Copilot Governance Workbench - Web ($Suffix)" -Definition $webDef
    $webSp = Get-OrCreateServicePrincipal $webApp['appId']
    Invoke-Graph -Method PATCH -Uri "/servicePrincipals/$($webSp['id'])" -Body @{ appRoleAssignmentRequired = $true; tags = @('WindowsAzureActiveDirectoryIntegratedApp') } | Out-Null
    $cfg['webClientId'] = $webApp['appId']
    $cfg['webServicePrincipalId'] = $webSp['id']
    $cfg['appRoleIds'] = $roleIds

    $workerDef = @{
        keyCredentials         = @(Get-KeyCredential $workerCert "CopilotGovernanceWorker-$Suffix")
        requiredResourceAccess = @(
            @{
                resourceAppId  = $GraphAppId
                resourceAccess = @(
                    @{ id = (& $graphRole 'User.Read.All'); type = 'Role' },
                    @{ id = (& $graphRole 'GroupMember.Read.All'); type = 'Role' },
                    @{ id = (& $graphRole 'AccessReview.Read.All'); type = 'Role' },
                    # Copilot seats (X13): license states and subscribed SKUs, and the Copilot usage report.
                    @{ id = (& $graphRole 'LicenseAssignment.Read.All'); type = 'Role' },
                    @{ id = (& $graphRole 'Reports.Read.All'); type = 'Role' }
                )
            }
        )
    }
    $workerApp = Set-AppRegistration -DisplayName "Copilot Governance Workbench - Worker ($Suffix)" -Definition $workerDef
    if (-not ($workerApp['identifierUris'] -contains "api://$($workerApp['appId'])")) {
        Invoke-Graph -Method PATCH -Uri "/applications/$($workerApp['id'])" -Body @{ identifierUris = @("api://$($workerApp['appId'])") } | Out-Null
    }
    $workerSp = Get-OrCreateServicePrincipal $workerApp['appId']
    $cfg['workerClientId'] = $workerApp['appId']
    $cfg['workerServicePrincipalId'] = $workerSp['id']
    Save-Config $cfg

    # Assign the admin to every Web app role (test only: personas are switched inside the app).
    $assigned = Get-GraphAll "/servicePrincipals/$($webSp['id'])/appRoleAssignedTo"
    foreach ($k in $roleIds.Keys) {
        if ($assigned | Where-Object { $_['principalId'] -eq $cfg['adminObjectId'] -and $_['appRoleId'] -eq $roleIds[$k] }) { continue }
        Invoke-Graph -Method POST -Uri "/servicePrincipals/$($webSp['id'])/appRoleAssignedTo" -Body @{
            principalId = $cfg['adminObjectId']; resourceId = $webSp['id']; appRoleId = $roleIds[$k]
        } | Out-Null
        Write-Done "assigned $AdminUpn to $k"
    }
}

# --------------------------------------------------------------------------------------------
# Groups
# --------------------------------------------------------------------------------------------
$lobTiers = @(
    @{ Code = 'RETAIL'; Name = 'Retail Banking'; Service = 'Copilot Chat'; Tier = 'Standard' },
    @{ Code = 'WEALTH'; Name = 'Wealth Management'; Service = 'Copilot Chat'; Tier = 'Standard' },
    @{ Code = 'WEALTH'; Name = 'Wealth Management'; Service = 'Cowork'; Tier = 'Standard' },
    @{ Code = 'COMMERCIAL'; Name = 'Commercial Banking'; Service = 'Copilot Chat'; Tier = 'Standard' },
    @{ Code = 'ES-IT'; Name = 'Enterprise Shared IT'; Service = 'Copilot Chat'; Tier = 'Extended' }
)
if ($Steps -contains 'Groups') {
    Write-Step 'Test security groups'
    $central = [ordered]@{
        PLATFORM_OWNER = 'Platform Owner'; INTAKE = 'Intake and entitlement'; PLATFORM_OPS = 'Platform Operations'
        ARCHITECT = 'Architecture'; RISK = 'Security and Risk'; FINOPS = 'FinOps'; COMMERCIAL = 'Commercial Authority'
        SOURCING = 'Sourcing'; AUDITOR = 'Auditors'; ADMIN = 'Workbench administrators'
    }
    $cfg['centralGroups'] = [ordered]@{}
    foreach ($k in $central.Keys) {
        $g = Get-OrCreateGroup "CGW $Suffix - $($central[$k])" "Copilot Governance Workbench ($Suffix): members hold the $($central[$k]) role."
        Add-GroupMember $g['id'] $cfg['adminObjectId']
        $cfg['centralGroups'][$k] = $g['id']
    }
    $cfg['policyGroups'] = @()
    foreach ($t in $lobTiers) {
        $g = Get-OrCreateGroup "CGW $Suffix - $($t.Service) - $($t.Name) - $($t.Tier)" "Policy scope group: $($t.Service) for $($t.Name), $($t.Tier) tier. Members are added by the access package."
        $cfg['policyGroups'] += @{ lob = $t.Code; service = $t.Service; tier = $t.Tier; groupId = $g['id']; name = $g['displayName'] }
    }
    Write-Done "$($central.Count) central groups and $($lobTiers.Count) policy scope groups ready"
    Save-Config $cfg
}

# --------------------------------------------------------------------------------------------
# Entitlement management: catalog, access packages, catalog reader for the Worker
# --------------------------------------------------------------------------------------------
if ($Steps -contains 'AccessPackages') {
    Write-Step 'Entitlement management catalog and access packages'
    $em = 'https://graph.microsoft.com/v1.0/identityGovernance/entitlementManagement'
    $catName = "Copilot Governance Workbench ($Suffix)"
    $cat = Find-One "$em/catalogs?`$filter=displayName eq '$(ConvertTo-ODataLiteral $catName)'"
    if (-not $cat) {
        $cat = Invoke-Graph -Method POST -Uri "$em/catalogs" -Body @{ displayName = $catName; description = 'Governed Copilot service tiers per LOB.'; isExternallyVisible = $false }
        Write-Done "catalog created"
    }
    $cfg['catalogId'] = $cat['id']

    $resources = Get-GraphAll "$em/catalogs/$($cat['id'])/resources?`$select=id,originId"
    $packages = Get-GraphAll "$em/accessPackages?`$filter=catalog/id eq '$($cat['id'])'&`$expand=catalog"
    foreach ($pg in $cfg['policyGroups']) {
        $res = $resources | Where-Object { $_['originId'] -eq $pg['groupId'] } | Select-Object -First 1
        if (-not $res) {
            # A group created a moment ago may not have reached entitlement management yet: retry.
            for ($attempt = 1; ; $attempt++) {
                try {
                    Invoke-Graph -Method POST -Uri "$em/resourceRequests" -Body @{
                        requestType = 'adminAdd'
                        resource    = @{ originId = $pg['groupId']; originSystem = 'AadGroup' }
                        catalog     = @{ id = $cat['id'] }
                    } | Out-Null
                    break
                }
                catch {
                    if ($attempt -ge 12 -or "$_" -notmatch 'ResourceNotFoundInOriginSystem') { throw }
                    Write-Note "group $($pg['name']) not visible to entitlement management yet; retrying in 15 s"
                    [System.Threading.Thread]::Sleep(15000)
                }
            }
            for ($i = 0; $i -lt 10 -and -not $res; $i++) {
                $res = Find-One "$em/catalogs/$($cat['id'])/resources?`$filter=originId eq '$($pg['groupId'])'"
                if (-not $res) { [System.Threading.Thread]::Sleep(3000) }
            }
            if (-not $res) { throw "Group $($pg['name']) did not appear in the catalog." }
        }
        $pkgName = "$($pg['service']) - $($pg['lob']) - $($pg['tier'])"
        $pkg = $packages | Where-Object { $_['displayName'] -eq $pkgName } | Select-Object -First 1
        if (-not $pkg) {
            $pkg = Invoke-Graph -Method POST -Uri "$em/accessPackages" -Body @{
                displayName = $pkgName
                description = "Routine access to $($pg['service']) for $($pg['lob']), $($pg['tier']) tier. Approved by the LOB's delegated approvers."
                isHidden    = $false
                catalog     = @{ id = $cat['id'] }
            }
            Write-Done "access package '$pkgName' created"
        }
        $pg['accessPackageId'] = $pkg['id']

        $scopes = Get-GraphAll "$em/accessPackages/$($pkg['id'])/resourceRoleScopes?`$expand=role,scope"
        if (-not $scopes) {
            $role = Find-One "$em/catalogs/$($cat['id'])/resourceRoles?`$filter=originSystem eq 'AadGroup' and resource/id eq '$($res['id'])' and displayName eq 'Member'&`$expand=resource"
            Invoke-Graph -Method POST -Uri "$em/accessPackages/$($pkg['id'])/resourceRoleScopes" -Body @{
                role  = @{ id = $role['id']; displayName = 'Member'; originSystem = 'AadGroup'; originId = $role['originId']; resource = @{ id = $res['id']; originId = $pg['groupId']; originSystem = 'AadGroup' } }
                scope = @{ originId = $pg['groupId']; originSystem = 'AadGroup' }
            } | Out-Null
        }

        $pols = Get-GraphAll "$em/assignmentPolicies?`$filter=accessPackage/id eq '$($pkg['id'])'"
        if (-not $pols) {
            Invoke-Graph -Method POST -Uri "$em/assignmentPolicies" -Body @{
                displayName       = 'Employees request, approver decides'
                description       = 'Employees in the directory request; a named approver decides. Expires after one year.'
                allowedTargetScope = 'allMemberUsers'
                expiration        = @{ type = 'afterDuration'; duration = 'P365D' }
                requestorSettings = @{ enableTargetsToSelfAddAccess = $true; enableTargetsToSelfUpdateAccess = $false; enableTargetsToSelfRemoveAccess = $true; allowCustomAssignmentSchedule = $false; enableOnBehalfRequestorsToAddAccess = $false }
                requestApprovalSettings = @{
                    isApprovalRequiredForAdd = $true; isApprovalRequiredForUpdate = $false
                    stages = @(@{
                        durationBeforeAutomaticDenial = 'P14D'; isApproverJustificationRequired = $true; isEscalationEnabled = $false
                        primaryApprovers = @(@{ '@odata.type' = '#microsoft.graph.singleUser'; userId = $cfg['adminObjectId'] })
                    })
                }
                accessPackage = @{ id = $pkg['id'] }
            } | Out-Null
            Write-Done "policy added to '$pkgName'"
        }
    }

    # Catalog reader role for the Worker, on this catalog only (narrower than EntitlementManagement.Read.All).
    $rm = 'https://graph.microsoft.com/v1.0/roleManagement/entitlementManagement'
    $readerDef = Find-One "$rm/roleDefinitions?`$filter=displayName eq 'Catalog reader'"
    $existingRa = Find-One "$rm/roleAssignments?`$filter=principalId eq '$($cfg['workerServicePrincipalId'])' and roleDefinitionId eq '$($readerDef['id'])'"
    if (-not $existingRa) {
        try {
            Invoke-Graph -Method POST -Uri "$rm/roleAssignments" -Body @{
                principalId = $cfg['workerServicePrincipalId']; roleDefinitionId = $readerDef['id']; appScopeId = "/AccessPackageCatalog/$($cat['id'])"
            } | Out-Null
            Write-Done 'Worker is catalog reader on the governed catalog'
            $cfg['entitlementReadMode'] = 'CatalogReader'
        }
        catch {
            Write-Note "catalog reader assignment refused ($($_.Exception.Message)); the Worker needs EntitlementManagement.Read.All instead"
            $cfg['entitlementReadMode'] = 'ApplicationPermission'
        }
    }
    else { $cfg['entitlementReadMode'] = 'CatalogReader' }
    Save-Config $cfg
}

# --------------------------------------------------------------------------------------------
# Admin consent: opened for you to approve
# --------------------------------------------------------------------------------------------
if ($Steps -contains 'Consent') {
    Write-Step 'Admin consent (you approve it in the browser)'
    foreach ($pair in @(@('Web', $cfg['webClientId']), @('Worker', $cfg['workerClientId']))) {
        # The app's API permissions page in the Entra admin center: no reply address needed (the
        # Worker deliberately has none). Click "Grant admin consent for <tenant>".
        $url = "https://entra.microsoft.com/#view/Microsoft_AAD_RegisteredApps/ApplicationMenuBlade/~/CallAnAPI/appId/$($pair[1])/isMSAApp~/false"
        Write-Note "$($pair[0]) app: click 'Grant admin consent' on $url"
        Open-InEdge $url
    }
    # The consent redirect lands on a blank page; that is expected. Wait until both grants appear.
    Write-Host '   Approve both pages in the browser. Waiting up to 15 minutes...' -ForegroundColor Yellow
    $graphSp = Get-ServicePrincipalByAppId $GraphAppId
    $deadline = (Get-Date).AddMinutes(15)
    do {
        $granted = Get-GraphAll "/servicePrincipals/$($cfg['workerServicePrincipalId'])/appRoleAssignments"
        $webGrant = Get-GraphAll "/oauth2PermissionGrants?`$filter=clientId eq '$($cfg['webServicePrincipalId'])' and consentType eq 'AllPrincipals'"
        if ($granted.Count -ge 3 -and $webGrant.Count -ge 1) { break }
        [System.Threading.Thread]::Sleep(10000)
    } while ((Get-Date) -lt $deadline)
    if ($webGrant.Count -lt 1) { Write-Note 'Web app consent not seen yet; rerun with -Steps Consent after approving.' }
    $names = foreach ($g in $granted) { ($graphSp['appRoles'] | Where-Object { $_['id'] -eq $g['appRoleId'] })['value'] }
    Write-Done "Worker application permissions granted: $($names -join ', ')"
    $cfg['workerGrantedRoles'] = @($names)
    Save-Config $cfg
}

# --------------------------------------------------------------------------------------------
# Mailbox and Exchange RBAC for Applications (Application Mail.Send scoped to one mailbox)
# --------------------------------------------------------------------------------------------
if ($Steps -contains 'Mailbox') {
    Write-Step "Notification mailbox $Mailbox"
    # Exchange Online ships its own MSAL, which clashes with Graph's in one process: run it in its own.
    Save-Config $cfg
    & pwsh -NoProfile -File (Join-Path $PSScriptRoot 'Setup-Mailbox.ps1') -TenantDomain $TenantDomain -AdminUpn $AdminUpn -Mailbox $Mailbox -Suffix $Suffix -ConfigPath $configPath
    if ($LASTEXITCODE -ne 0) { Write-Note 'mailbox step failed; rerun with -Steps Mailbox' }
    $cfg = Read-Config
}

# --------------------------------------------------------------------------------------------
# Power Platform reader role for the Worker (preview API)
# --------------------------------------------------------------------------------------------
if ($Steps -contains 'PowerPlatform') {
    Write-Step 'Power Platform reader role for the Worker'
    if (-not ('Microsoft.Identity.Client.PublicClientApplicationBuilder' -as [type])) { Import-Module Microsoft.Graph.Authentication }
    $ppToken = Get-EdgeToken -ClientId $AzurePowerShellClientId -Tenant $TenantDomain -Scopes @('https://api.powerplatform.com/.default') -LoginHint $AdminUpn
    $h = @{ Authorization = "Bearer $($ppToken.AccessToken)" }
    try {
        $existing = Invoke-RestMethod -Uri 'https://api.powerplatform.com/authorization/roleAssignments?api-version=2024-10-01' -Headers $h
        $mine = $existing.value | Where-Object { $_.principalObjectId -eq $cfg['workerServicePrincipalId'] -and $_.roleDefinitionId -eq $PowerPlatformReaderRoleId }
        if (-not $mine) {
            $body = @{ roleDefinitionId = $PowerPlatformReaderRoleId; principalObjectId = $cfg['workerServicePrincipalId']; principalType = 'ApplicationUser'; scope = "/tenants/$($cfg['tenantId'])" } | ConvertTo-Json
            Invoke-RestMethod -Method Post -Uri 'https://api.powerplatform.com/authorization/roleAssignments?api-version=2024-10-01' -Headers $h -Body $body -ContentType 'application/json' | Out-Null
            Write-Done 'Power Platform reader assigned at tenant scope'
        }
        else { Write-Done 'Power Platform reader already assigned' }
        $cfg['powerPlatformReader'] = $true
    }
    catch {
        Write-Note "Power Platform role assignment failed: $($_.Exception.Message). The Workbench falls back to operator-run licensing reads."
        $cfg['powerPlatformReader'] = $false
    }
    Save-Config $cfg
}

# --------------------------------------------------------------------------------------------
# Teams app (activity feed, resource-specific consent)
# --------------------------------------------------------------------------------------------
if ($Steps -contains 'Teams') {
    Write-Step 'Teams app'
    $pkgDir = Join-Path $repo 'setup\teams-app'
    $out = Join-Path $repo "config\teams-app-$Suffix.zip"
    & (Join-Path $PSScriptRoot 'New-TeamsAppPackage.ps1') -WorkerClientId $cfg['workerClientId'] -WebBaseUrl $WebBaseUrl -Suffix $Suffix -OutFile $out -TemplateDir $pkgDir
    $externalId = $cfg['workerClientId']
    $existing = Find-One "/appCatalogs/teamsApps?`$filter=externalId eq '$externalId'"
    $bytes = [IO.File]::ReadAllBytes($out)
    if (-not $existing) {
        $app = Invoke-MgGraphRequest -Method POST -Uri 'https://graph.microsoft.com/v1.0/appCatalogs/teamsApps' -Body $bytes -ContentType 'application/zip' -OutputType Hashtable
        Write-Done "Teams app uploaded to the org catalog ($($app['id']))"
        $cfg['teamsAppId'] = $app['id']
    }
    else {
        $cfg['teamsAppId'] = $existing['id']
        Write-Done "Teams app already in the catalog ($($existing['id']))"
    }
    $installed = Get-GraphAll "/users/$($cfg['adminObjectId'])/teamwork/installedApps?`$expand=teamsApp"
    if (-not ($installed | Where-Object { $_['teamsApp']['id'] -eq $cfg['teamsAppId'] })) {
        try {
            Invoke-Graph -Method POST -Uri "/users/$($cfg['adminObjectId'])/teamwork/installedApps" -Body @{
                'teamsApp@odata.bind' = "https://graph.microsoft.com/v1.0/appCatalogs/teamsApps/$($cfg['teamsAppId'])"
                consentedPermissionSet = @{ resourceSpecificPermissions = @(@{ permissionValue = 'TeamsActivity.Send.User'; permissionType = 'application' }) }
            } | Out-Null
            Write-Done "Teams app installed for $AdminUpn with TeamsActivity.Send.User"
        }
        catch { Write-Note "install for $AdminUpn failed: $($_.Exception.Message)" }
    }
    Save-Config $cfg
}

# --------------------------------------------------------------------------------------------
# Copilot seats (X13): two more Worker permissions and cloud stand-ins for the AD seat groups
# --------------------------------------------------------------------------------------------
if ($Steps -contains 'Seats') {
    Write-Step 'Copilot seats: Worker permissions and stand-in groups'
    if (-not $cfg['workerClientId']) { throw 'Run the Apps step first: the Worker app registration is missing.' }

    # 1. LicenseAssignment.Read.All (subscribed SKUs, users' license states) and Reports.Read.All (Copilot usage report).
    $graphSp = Get-ServicePrincipalByAppId $GraphAppId
    $wanted = foreach ($v in 'LicenseAssignment.Read.All', 'Reports.Read.All') { ($graphSp['appRoles'] | Where-Object { $_['value'] -eq $v })['id'] }
    $workerApp = Find-One "/applications?`$filter=appId eq '$($cfg['workerClientId'])'"
    $access = @($workerApp['requiredResourceAccess'])
    $graphAccess = $access | Where-Object { $_['resourceAppId'] -eq $GraphAppId } | Select-Object -First 1
    $have = if ($graphAccess) { @($graphAccess['resourceAccess'] | ForEach-Object { $_['id'] }) } else { @() }
    $missing = @($wanted | Where-Object { $have -notcontains $_ })
    if ($missing.Count -gt 0) {
        $newGraph = @{ resourceAppId = $GraphAppId; resourceAccess = @(@($have | ForEach-Object { @{ id = $_; type = 'Role' } }) + @($missing | ForEach-Object { @{ id = $_; type = 'Role' } })) }
        $others = @($access | Where-Object { $_['resourceAppId'] -ne $GraphAppId })
        Invoke-Graph -Method PATCH -Uri "/applications/$($workerApp['id'])" -Body @{ requiredResourceAccess = @($others + $newGraph) } | Out-Null
        Write-Done "Worker app now asks for LicenseAssignment.Read.All and Reports.Read.All"
    }
    $granted = @((Get-GraphAll "/servicePrincipals/$($cfg['workerServicePrincipalId'])/appRoleAssignments") | ForEach-Object { $_['appRoleId'] })
    if (@($wanted | Where-Object { $granted -notcontains $_ }).Count -gt 0) {
        $url = "https://entra.microsoft.com/#view/Microsoft_AAD_RegisteredApps/ApplicationMenuBlade/~/CallAnAPI/appId/$($cfg['workerClientId'])/isMSAApp~/false"
        Write-Note "Worker app: click 'Grant admin consent' on $url"
        Open-InEdge $url
        Write-Host '   Approve the page in the browser. Waiting up to 15 minutes...' -ForegroundColor Yellow
        $deadline = (Get-Date).AddMinutes(15)
        do {
            [System.Threading.Thread]::Sleep(10000)
            $granted = @((Get-GraphAll "/servicePrincipals/$($cfg['workerServicePrincipalId'])/appRoleAssignments") | ForEach-Object { $_['appRoleId'] })
        } while (@($wanted | Where-Object { $granted -notcontains $_ }).Count -gt 0 -and (Get-Date) -lt $deadline)
        if (@($wanted | Where-Object { $granted -notcontains $_ }).Count -gt 0) { Write-Note 'Consent not seen yet; rerun with -Steps Seats after approving.' }
        else { Write-Done 'LicenseAssignment.Read.All and Reports.Read.All granted' }
    }
    else { Write-Done 'LicenseAssignment.Read.All and Reports.Read.All already granted' }
    Write-Note 'For reclaim proposals, turn off "Display concealed user, group, and site names in all reports" (Microsoft 365 admin center > Settings > Org settings > Reports).'

    # 2. Cloud security groups standing in for the on-premises AD groups (GSGu_MCO_*). No license is assigned to
    #    them: the Workbench only reads, and a real Copilot license group is managed by OIM and group-based licensing.
    $seatGroups = @()
    $lic = Get-OrCreateGroup "CGW $Suffix - GSGu_MCO_CopilotM365" "Test stand-in for the shared Copilot license group (GSGu_MCO_CopilotM365). No license is assigned to it."
    Add-GroupMember $lic['id'] $cfg['adminObjectId']
    $seatGroups += @{ purpose = 'LICENSE'; lob = $null; name = $lic['displayName']; groupId = $lic['id'] }
    foreach ($code in @($lobTiers | ForEach-Object { $_.Code } | Select-Object -Unique)) {
        $short = ($code -replace '[^A-Za-z0-9]', '')
        $g = Get-OrCreateGroup "CGW $Suffix - GSGu_MCO_$($short)Tracking" "Test stand-in for the $code LOB tracking group (GSGu_MCO_<LOB>Tracking): members belong to $code for Copilot seats."
        if ($code -eq 'RETAIL') { Add-GroupMember $g['id'] $cfg['adminObjectId'] }
        $seatGroups += @{ purpose = 'LOB_TRACKING'; lob = $code; name = $g['displayName']; groupId = $g['id'] }
    }
    $cfg['seatGroups'] = $seatGroups
    Write-Done "$($seatGroups.Count) seat stand-in groups ready. Record them on the Workbench's seat groups page (purpose and LOB) so the seat read picks them up."
    Save-Config $cfg
}

Write-Step "Done. Settings written to $configPath"
