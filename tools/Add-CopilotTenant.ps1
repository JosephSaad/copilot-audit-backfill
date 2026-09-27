<#
.SYNOPSIS
    Adds another Microsoft 365 tenant to the job server: its app registration,
    its two databases, and its entry in the settings.

.DESCRIPTION
    For a group with several tenants. Each tenant gets its own app registration
    (with the same three Microsoft Graph application permissions, consented in
    that tenant, using the job server's certificate) and its own pair of
    databases, so no tenant's records mix with another's. The nightly run then
    loads every tenant in turn, and the dashboard (Insights:Tenants) spans them.

    Run it on the job server as an administrator, after
    tools\Install-CopilotJobServer.ps1 has set up the first tenant. It:

      1. registers the app in the new tenant (tools\New-CopilotAuditApp.ps1),
         unless -ClientId is given;
      2. creates and installs its databases (Install-CopilotAudit.ps1), with
         the service account's access;
      3. adds the tenant to copilot-audit.settings.psd1.

    Then load its history once: .\Invoke-CopilotPipeline.ps1 -History -Tenant <Name>

.PARAMETER Name
    A short name for the tenant, shown in the logs and the dashboard (Fabrikam).

.PARAMETER TenantId
    The tenant: its id or its onmicrosoft.com name.

.PARAMETER ClientId
    An app already registered in that tenant. Without it, one is registered.

.PARAMETER Database
    Its Copilot database. Default: CopilotAudit_<Name>.

.PARAMETER LicenceDatabase
    Its licence-event database. Default: CopilotLicenseEvents_<Name>.

.PARAMETER TargetDomain
    Its users' Active Directory domain, if its directory is loaded from AD.

.EXAMPLE
    .\tools\Add-CopilotTenant.ps1 -Name Fabrikam -TenantId fabrikam.onmicrosoft.com -TargetDomain fabrikam.local
#>
#Requires -Version 7.2
[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9][A-Za-z0-9 _-]{0,39}$')][string] $Name,
    [Parameter(Mandatory)][string] $TenantId,
    [string] $ClientId,
    [string] $Database,
    [string] $LicenceDatabase,
    [string] $TargetDomain,
    [switch] $UseDeviceCode,
    [string] $SettingsPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if (-not $SettingsPath) { $SettingsPath = Join-Path $root 'copilot-audit.settings.psd1' }

function Add-TenantSetting {
    <# The settings with one more tenant. Pure, for the tests. #>
    param([System.Collections.IDictionary] $Setting, [System.Collections.IDictionary] $Tenant)
    $existing = @(if ($Setting.Contains('Tenants')) { @($Setting.Tenants) | Where-Object { $_ } })
    $firstName = if ($Setting.Contains('TenantName') -and $Setting.TenantName) { $Setting.TenantName } else { 'Default' }
    if ($Tenant.Name -eq $firstName -or @($existing | Where-Object { $_.Name -eq $Tenant.Name }).Count) { throw "There is already a tenant called $($Tenant.Name)." }
    $dbs = @($Setting.Database) + @($existing | ForEach-Object { $_.Database })
    if ($Tenant.Database -in $dbs) { throw "The database $($Tenant.Database) already belongs to another tenant." }
    $out = [ordered]@{}
    foreach ($k in $Setting.Keys) { $out[$k] = $Setting[$k] }
    $out.Tenants = @($existing) + @($Tenant)
    $out
}

function Invoke-Main {
    [CmdletBinding(SupportsShouldProcess)] param()
    if (-not (Test-Path -LiteralPath $SettingsPath)) { throw "No settings at $SettingsPath. Set up the first tenant with tools\Install-CopilotJobServer.ps1." }
    $s = Import-PowerShellDataFile -LiteralPath $SettingsPath
    $db = if ($Database) { $Database } else { "CopilotAudit_$($Name -replace '[^A-Za-z0-9_]', '')" }
    $lic = if ($LicenceDatabase) { $LicenceDatabase } else { "CopilotLicenseEvents_$($Name -replace '[^A-Za-z0-9_]', '')" }

    # 1. The app, in the new tenant, with the job server's certificate.
    $client = $ClientId
    if (-not $client) {
        $cert = Get-Item -LiteralPath "Cert:\LocalMachine\My\$($s.CertificateThumbprint)" -ErrorAction SilentlyContinue
        if (-not $cert) { throw "The job server's certificate $($s.CertificateThumbprint) is not in LocalMachine\My." }
        $cer = Join-Path ([IO.Path]::GetTempPath()) "copilot-audit-$($s.CertificateThumbprint).cer"
        [IO.File]::WriteAllBytes($cer, $cert.RawData)
        if ($PSCmdlet.ShouldProcess($TenantId, 'Register the Copilot audit app')) {
            $app = & (Join-Path $PSScriptRoot 'New-CopilotAuditApp.ps1') -TenantId $TenantId -CertificatePath $cer -UseDeviceCode:$UseDeviceCode
            $client = $app.ClientId; $TenantId = $app.TenantId
        }
    }

    # 2. Its databases.
    $install = @{ SqlServer = $s.SqlServer; Database = $db; LicenceDatabase = $lic }
    if ($s.Contains('ServiceAccount') -and $s.ServiceAccount) { $install.ServiceAccount = $s.ServiceAccount }
    if ($s.Contains('TrustServerCertificate') -and $s.TrustServerCertificate) { $install.TrustServerCertificate = $true }
    if ($s.Contains('Dashboard') -and $s.Dashboard) { $install.Dashboard = $true }
    if ($PSCmdlet.ShouldProcess("$($s.SqlServer): $db, $lic", 'Install-CopilotAudit.ps1')) { & (Join-Path $root 'Install-CopilotAudit.ps1') @install }

    # 3. The settings.
    $tenant = [ordered]@{ Name = $Name; TenantId = $TenantId; ClientId = $client; Database = $db; LicenceDatabase = $lic }
    if ($TargetDomain) { $tenant.TargetDomain = $TargetDomain }
    $new = Add-TenantSetting -Setting $s -Tenant $tenant
    if ($PSCmdlet.ShouldProcess($SettingsPath, "Add tenant $Name")) {
        # In a child scope: that script's parameters share names with this one's.
        $text = & { param($n) . (Join-Path $PSScriptRoot 'Install-CopilotJobServer.ps1'); ConvertTo-SettingText $n } $new
        if ($s.Contains('ServiceAccount')) { $text = $text.Replace("`r`n}", "`r`n    ServiceAccount = '$(([string] $s.ServiceAccount).Replace("'", "''"))'`r`n}") }
        Set-Content -LiteralPath $SettingsPath -Value $text -Encoding utf8
    }

    Write-Host ''
    Write-Host "Tenant $Name added: $db and $lic on $($s.SqlServer), app $client." -ForegroundColor Green
    Write-Host 'Next:'
    Write-Host "  1. Load its history once: .\Invoke-CopilotPipeline.ps1 -History -Tenant '$Name'"
    Write-Host '  2. From tonight the nightly task loads it with the others.'
    Write-Host '  3. For the dashboard, run the dashboard installer again on the web server with every tenant, the first one first:'
    Write-Host "     .\dashboard\Install-CopilotInsights.ps1 ... -Tenant '<first tenant>=$($s.Database)','$Name=$db' -GrantDatabaseAccess"
}

# Dot-sourcing (tests) loads the functions without running anything.
if ($MyInvocation.InvocationName -ne '.') { Invoke-Main }
