<#
.SYNOPSIS
    Registers the Entra app the pipeline signs in as: the three Graph
    permissions, their admin consent, and the job server's certificate - in
    one command, instead of the portal.

.DESCRIPTION
    Signs in to Microsoft Graph as you (a Global Administrator or Privileged
    Role Administrator, who can grant admin consent), then:

      1. Finds the app by -DisplayName, or creates it (single tenant, no
         redirect URI, no secret).
      2. Asks for the application permissions AuditLogsQuery.Read.All,
         User.Read.All and LicenseAssignment.Read.All, and nothing else.
      3. Creates its service principal and grants admin consent for each
         permission it does not have yet.
      4. Adds the certificate (-CertificatePath, the .cer the job server
         setup exported) to the app, keeping any certificate already there.
      5. Prints the tenant and client ids, and writes them into the job
         server's settings file when -SettingsPath is given.

    Safe to run again: whatever is already in place is left alone, so a second
    run only adds a renewed certificate.

    Uses Microsoft.Graph.Authentication only, from the Modules folder next to
    the scripts when present (the offline bundle).

.PARAMETER TenantId
    The tenant: its id or a verified domain (contoso.onmicrosoft.com).

.PARAMETER CertificatePath
    The public certificate (.cer) to sign in with. tools\Install-CopilotJobServer.ps1
    exports it. Without it the app is registered with no certificate, and
    this runs again later to add one.

.PARAMETER SettingsPath
    The job server's settings file (copilot-audit.settings.psd1): TenantId and
    ClientId are written into it.

.PARAMETER UseDeviceCode
    Sign in with a code on another device, for a server with no browser.

.EXAMPLE
    .\tools\New-CopilotAuditApp.ps1 -TenantId contoso.onmicrosoft.com -CertificatePath D:\CopilotAudit\copilot-audit.cer
#>
#Requires -Version 7.2
[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)][string] $TenantId,
    [string] $DisplayName = 'Copilot audit pipeline',
    [string] $CertificatePath,
    [string] $SettingsPath,
    [switch] $UseDeviceCode
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:GraphAppId = '00000003-0000-0000-c000-000000000000'   # Microsoft Graph
$script:Permissions = 'AuditLogsQuery.Read.All', 'User.Read.All', 'LicenseAssignment.Read.All'

$script:ToolRoot = Split-Path $PSScriptRoot -Parent
$script:BundledModules = Join-Path $script:ToolRoot 'Modules'
if (Test-Path -LiteralPath $script:BundledModules -PathType Container) {
    $sep = [System.IO.Path]::PathSeparator
    $existing = ($env:PSModulePath -split [regex]::Escape($sep)) | Where-Object { $_ -ne $script:BundledModules }
    $env:PSModulePath = (@($script:BundledModules) + $existing) -join $sep
}

function Write-Step([string] $Text) { Write-Information "==> $Text" -InformationAction Continue }
function Write-Done([string] $Text) { Write-Information "    $Text" -InformationAction Continue }

function Invoke-Graph {
    param([string] $Method = 'GET', [Parameter(Mandatory)][string] $Uri, $Body)
    $a = @{ Method = $Method; Uri = $Uri; OutputType = 'PSObject' }
    if ($null -ne $Body) { $a.Body = ($Body | ConvertTo-Json -Depth 10 -Compress); $a.ContentType = 'application/json' }
    Invoke-MgGraphRequest @a
}

function Get-Single($Result) {
    $v = @($Result.value)
    if ($v.Count -gt 0) { $v[0] } else { $null }
}

function Get-GraphRoleId {
    <# The Graph service principal and the ids of the three app roles, looked up rather than hard-coded. #>
    $graph = Get-Single (Invoke-Graph -Uri "/v1.0/servicePrincipals?`$filter=appId eq '$script:GraphAppId'&`$select=id,appRoles")
    if (-not $graph) { throw 'Microsoft Graph has no service principal in this tenant.' }
    $ids = [ordered]@{}
    foreach ($p in $script:Permissions) {
        $role = @($graph.appRoles | Where-Object { $_.value -eq $p -and @($_.allowedMemberTypes) -contains 'Application' })
        if (-not $role) { throw "Microsoft Graph offers no application permission named $p." }
        $ids[$p] = [string] $role[0].id
    }
    [pscustomobject]@{ GraphSpId = [string] $graph.id; RoleIds = $ids }
}

function Get-CertificateCredential([string] $Path) {
    $cert = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new((Resolve-Path -LiteralPath $Path).ProviderPath)
    try {
        [pscustomobject]@{
            Thumbprint = $cert.Thumbprint
            # Graph reports customKeyIdentifier as the thumbprint's bytes in base64, and defaults it to them.
            KeyIdentifier = [Convert]::ToBase64String($cert.GetCertHash())
            NotAfter   = $cert.NotAfter
            Credential = [ordered]@{
                type        = 'AsymmetricX509Cert'
                usage       = 'Verify'
                key         = [Convert]::ToBase64String($cert.RawData)
                displayName = $cert.Subject
            }
        }
    }
    finally { $cert.Dispose() }
}

function Set-SettingsValue {
    <# Writes Key = 'value' into a .psd1 settings file, replacing the line if it is there. #>
    param([string] $Path, [string] $Key, [string] $Value)
    $text = Get-Content -LiteralPath $Path -Raw
    $line = "    $Key = '$($Value.Replace("'", "''"))'"
    $pattern = "(?m)^\s*$([regex]::Escape($Key))\s*=.*$"
    $text = if ($text -match $pattern) { [regex]::Replace($text, $pattern, $line.Replace('$', '$$')) }
            else { $text -replace '\}\s*$', "$line`r`n}`r`n" }
    Set-Content -LiteralPath $Path -Value $text.TrimEnd() -NoNewline -Encoding utf8
}

function Invoke-Main {
    [CmdletBinding(SupportsShouldProcess)] param()
    foreach ($cmd in 'Connect-MgGraph', 'Invoke-MgGraphRequest', 'Get-MgContext') {
        if (-not (Get-Command $cmd -ErrorAction SilentlyContinue)) {
            throw "$cmd not found. Install Microsoft.Graph.Authentication, or run this from the offline bundle, which has it in its Modules folder."
        }
    }
    $cert = if ($CertificatePath) { Get-CertificateCredential $CertificatePath } else { $null }

    Write-Step "Signing in to $TenantId (an administrator who can grant admin consent)"
    $connect = @{ TenantId = $TenantId; Scopes = 'Application.ReadWrite.All', 'AppRoleAssignment.ReadWrite.All'; NoWelcome = $true }
    if ($UseDeviceCode) { $connect.UseDeviceCode = $true }
    Connect-MgGraph @connect
    $tenant = (Get-MgContext).TenantId

    $graph = Get-GraphRoleId
    $required = @([ordered]@{
        resourceAppId  = $script:GraphAppId
        resourceAccess = @($graph.RoleIds.Values | ForEach-Object { [ordered]@{ id = $_; type = 'Role' } })
    })

    # ---- 1-2. The app and its permissions ---------------------------------------------------
    Write-Step "App registration '$DisplayName'"
    $filterName = $DisplayName.Replace("'", "''")
    $app = Get-Single (Invoke-Graph -Uri "/v1.0/applications?`$filter=displayName eq '$filterName'&`$select=id,appId,requiredResourceAccess,keyCredentials")
    if (-not $app) {
        if ($PSCmdlet.ShouldProcess($DisplayName, 'Create the app registration')) {
            $app = Invoke-Graph -Method POST -Uri '/v1.0/applications' -Body ([ordered]@{
                displayName = $DisplayName; signInAudience = 'AzureADMyOrg'; requiredResourceAccess = $required })
            Write-Done "created (client id $($app.appId))"
        }
    }
    else {
        Write-Done "found (client id $($app.appId))"
        $have = @(@($app.requiredResourceAccess) | Where-Object { $_.resourceAppId -eq $script:GraphAppId } |
                  ForEach-Object { @($_.resourceAccess) } | ForEach-Object { [string] $_.id })
        $missing = @($graph.RoleIds.Values | Where-Object { $have -notcontains $_ })
        if ($missing -and $PSCmdlet.ShouldProcess($DisplayName, 'Add the Graph permissions')) {
            # Keep every other API's permissions; replace only Graph's entry with its old roles plus the three.
            $others = @(@($app.requiredResourceAccess) | Where-Object { $_.resourceAppId -ne $script:GraphAppId })
            $graphEntry = [ordered]@{ resourceAppId = $script:GraphAppId
                resourceAccess = @(@($have + $missing | Select-Object -Unique) | ForEach-Object {
                    $id = $_; $old = @(@($app.requiredResourceAccess) | Where-Object { $_.resourceAppId -eq $script:GraphAppId } |
                                   ForEach-Object { @($_.resourceAccess) } | Where-Object { [string] $_.id -eq $id })
                    [ordered]@{ id = $id; type = $(if ($old) { [string] $old[0].type } else { 'Role' }) } }) }
            Invoke-Graph -Method PATCH -Uri "/v1.0/applications/$($app.id)" -Body @{ requiredResourceAccess = @($others + $graphEntry) } | Out-Null
            Write-Done "added $($missing.Count) permission(s)"
        }
    }
    if (-not $app) { return }   # -WhatIf on a new app: nothing further to show

    # ---- 3. Service principal and admin consent ---------------------------------------------
    Write-Step 'Admin consent'
    $sp = Get-Single (Invoke-Graph -Uri "/v1.0/servicePrincipals?`$filter=appId eq '$($app.appId)'&`$select=id")
    if (-not $sp -and $PSCmdlet.ShouldProcess($DisplayName, 'Create the service principal')) {
        $sp = Invoke-Graph -Method POST -Uri '/v1.0/servicePrincipals' -Body @{ appId = $app.appId }
    }
    if ($sp) {
        $granted = @(@((Invoke-Graph -Uri "/v1.0/servicePrincipals/$($sp.id)/appRoleAssignments").value) |
                     Where-Object { $_.resourceId -eq $graph.GraphSpId } | ForEach-Object { [string] $_.appRoleId })
        foreach ($p in $script:Permissions) {
            $roleId = $graph.RoleIds[$p]
            if ($granted -contains $roleId) { Write-Done "$p already consented"; continue }
            if ($PSCmdlet.ShouldProcess($p, 'Grant admin consent')) {
                Invoke-Graph -Method POST -Uri "/v1.0/servicePrincipals/$($sp.id)/appRoleAssignments" -Body ([ordered]@{
                    principalId = $sp.id; resourceId = $graph.GraphSpId; appRoleId = $roleId }) | Out-Null
                Write-Done "$p consented"
            }
        }
    }

    # ---- 4. Certificate -----------------------------------------------------------------------
    Write-Step 'Certificate'
    if (-not $cert) { Write-Done 'none given: run this again with -CertificatePath to add one.' }
    else {
        $keys = @($app.keyCredentials)
        if (@($keys | Where-Object { $_ -and [string] $_.customKeyIdentifier -eq $cert.KeyIdentifier }).Count -gt 0) {
            Write-Done "$($cert.Thumbprint) is already on the app"
        }
        elseif ($PSCmdlet.ShouldProcess($cert.Thumbprint, 'Add the certificate to the app')) {
            # keyCredentials is replaced whole. The ones already there go back as Graph returned them,
            # identified by keyId with no key (as az ad app credential reset --append does), so they are kept.
            $keep = @($keys | Where-Object { $_ } | ForEach-Object {
                $k = [ordered]@{}
                foreach ($p in $_.PSObject.Properties) { if ($p.Name -ne 'key') { $k[$p.Name] = $p.Value } }
                $k.key = $null
                $k })
            Invoke-Graph -Method PATCH -Uri "/v1.0/applications/$($app.id)" -Body @{ keyCredentials = @($keep + $cert.Credential) } | Out-Null
            Write-Done "$($cert.Thumbprint) added (valid until $($cert.NotAfter.ToString('yyyy-MM-dd')))"
        }
    }

    # ---- 5. Where to use it -----------------------------------------------------------------
    if ($SettingsPath -and (Test-Path -LiteralPath $SettingsPath) -and $PSCmdlet.ShouldProcess($SettingsPath, 'Write the tenant and client ids')) {
        Set-SettingsValue -Path $SettingsPath -Key 'TenantId' -Value $tenant
        Set-SettingsValue -Path $SettingsPath -Key 'ClientId' -Value $app.appId
        Write-Done "tenant and client ids written to $SettingsPath"
    }
    [pscustomobject]@{ TenantId = $tenant; ClientId = [string] $app.appId; CertificateThumbprint = $(if ($cert) { $cert.Thumbprint }) }
}

# Dot-sourcing (tests) loads the functions without running anything.
if ($MyInvocation.InvocationName -ne '.') { Invoke-Main }
