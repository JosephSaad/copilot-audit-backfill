<#
.SYNOPSIS
    Installs or upgrades the Workbench web application on IIS: its own app pool under a gMSA, the published
    files, web.config (in-process hosting), the production settings file, certificate key access and the
    HTTPS binding. Safe to rerun; supports -WhatIf.

.DESCRIPTION
    Two shapes:
      - a site of its own:        -SiteName 'Copilot Governance Workbench' -HostName workbench.corp.example
      - an application in a site: -SiteName 'Default Web Site' -ApplicationPath /Workbench
    The application path must not be named like a page of the Workbench (/Admin, /Requests ...).

    Refuses, with a plain message, when the server lacks IIS, the ASP.NET Core 10 Hosting Bundle, the
    certificates or an elevated session. The files are mirrored with robocopy only into an empty folder or a
    folder this kit installed before (.cgw-install marker); appsettings.Production.json is never deleted.

    Anonymous access: IIS anonymous authentication stays on and Windows authentication off, because the
    application signs people in with Entra ID itself. Every page requires sign-in except /health/live and
    /health/ready (the application's fallback policy enforces this).

.EXAMPLE
    pwsh -File deploy\Install-WorkbenchWeb.ps1 -PublishPath D:\Drop\Web -InstallPath D:\CopilotGovernance\Web `
        -SiteName 'Copilot Governance Workbench' -HostName workbench.corp.example -TlsCertificateThumbprint <thumbprint> `
        -GmsaAccount 'CORP\gmsa-cgw-web$' -SettingsFile D:\Drop\appsettings.Web.Production.json -WhatIf
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)] [string] $PublishPath,
    [Parameter(Mandatory)] [string] $InstallPath,
    [Parameter(Mandatory)] [string] $SettingsFile,
    [Parameter(Mandatory)] [string] $GmsaAccount,
    [string] $SiteName = 'Copilot Governance Workbench',
    [string] $ApplicationPath = '',
    [string] $HostName = '',
    [int] $Port = 443,
    [string] $TlsCertificateThumbprint = '',
    [string] $AppPoolName = '',
    [string] $EnvironmentName = 'Production',
    [long] $MaxRequestBytes = 26214400
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'WorkbenchDeploy.Common.ps1')

# ------------------------------------------------------------------------------------------------
# Inputs
# ------------------------------------------------------------------------------------------------
if (-not $IsWindows) { throw 'The web application installs on Windows Server with IIS.' }
if (-not (Test-GmsaName $GmsaAccount)) { throw "-GmsaAccount '$GmsaAccount' must be a group managed service account: DOMAIN\name`$." }
if ($ApplicationPath) {
    if ($ApplicationPath -notmatch '^/[A-Za-z0-9][A-Za-z0-9\-_]{0,60}$') { throw "-ApplicationPath '$ApplicationPath' must look like /Workbench (one segment, letters, digits, - or _)." }
    if ($script:WorkbenchRoutes -contains $ApplicationPath.TrimStart('/').ToLowerInvariant()) {
        throw "-ApplicationPath '$ApplicationPath' is named like a Workbench page; links could not be told apart. Choose another name, such as /Workbench."
    }
}
elseif (-not $HostName) { throw 'A site of its own needs -HostName (the name people browse to and the HTTPS binding uses).' }
if (-not $AppPoolName) { $AppPoolName = if ($ApplicationPath) { 'CopilotGovernanceWeb' + ($ApplicationPath.TrimStart('/')) } else { 'CopilotGovernanceWeb' } }

if (-not (Test-Path -LiteralPath (Join-Path $PublishPath 'Governance.Web.dll'))) {
    throw "'$PublishPath' does not hold a published web application (Governance.Web.dll missing). Run: dotnet publish src\Governance.Web -c Release -o <folder>."
}
$settings = Assert-SettingsFile -Path $SettingsFile -RequiredKeys @(
    'AzureAd:TenantId', 'AzureAd:ClientId', 'AzureAd:ClientCertificates', 'ConnectionStrings:Governance',
    'DataProtection:KeyRingPath', 'DataProtection:CertificateThumbprint', 'Workbench:BaseUrl')

# ------------------------------------------------------------------------------------------------
# Prerequisites (refuse with a plain message)
# ------------------------------------------------------------------------------------------------
Write-DeployStep 'Prerequisites'
$problems = [System.Collections.Generic.List[string]]::new()
if (-not (Test-IsAdministrator)) { $problems.Add('Run from an elevated PowerShell 7 session (IIS and certificate changes need administrator rights).') }
if (-not (Get-Service -Name W3SVC -ErrorAction SilentlyContinue)) { $problems.Add('IIS is not installed on this server. Add the Web Server role first.') }
if (-not (Get-Module -ListAvailable IISAdministration)) { $problems.Add('The IISAdministration module is missing. Install the IIS management tools (Web-Mgmt-Tools).') }
$hb = Get-HostingBundleStatus
if (-not ($hb.ModulePresent -and $hb.AspNetCore10)) { $problems.Add('The ASP.NET Core 10 Hosting Bundle is not installed. Install it, then restart IIS (net stop was /y; net start w3svc).') }

$oidcSubject = @($settings['AzureAd']['ClientCertificates'])[0]['CertificateDistinguishedName']
$oidcStore = @($settings['AzureAd']['ClientCertificates'])[0]['CertificateStorePath']
if ($oidcStore -ne 'LocalMachine/My') { $problems.Add("AzureAd:ClientCertificates must use CertificateStorePath LocalMachine/My in production (found '$oidcStore').") }
$oidcCert = if ($oidcSubject) { Get-WorkbenchCertificate -Subject $oidcSubject -StoreLocation LocalMachine } else { $null }
if ($null -eq $oidcCert) { $problems.Add("The sign-in certificate '$oidcSubject' is not in LocalMachine\My with a private key.") }
elseif (($oidcCert.NotAfter - (Get-Date)).TotalDays -lt 30) { $problems.Add("The sign-in certificate '$oidcSubject' expires on $($oidcCert.NotAfter.ToString('yyyy-MM-dd')): rotate it first.") }

$dpThumb = ($settings['DataProtection']['CertificateThumbprint'] -replace '\s', '')
$dpCert = Get-Item -LiteralPath "Cert:\LocalMachine\My\$dpThumb" -ErrorAction SilentlyContinue
if ($null -eq $dpCert -or -not $dpCert.HasPrivateKey) { $problems.Add("The Data Protection certificate $dpThumb is not in LocalMachine\My with a private key.") }
$keyRing = $settings['DataProtection']['KeyRingPath']
if (-not ($keyRing -like '\\*')) { $problems.Add("DataProtection:KeyRingPath '$keyRing' must be a UNC share so every web node reads the same keys.") }
elseif (-not (Test-Path -LiteralPath $keyRing)) { $problems.Add("The key ring share '$keyRing' is not reachable from this server.") }

if (-not $ApplicationPath -or $TlsCertificateThumbprint) {
    $tls = Get-Item -LiteralPath "Cert:\LocalMachine\My\$($TlsCertificateThumbprint -replace '\s', '')" -ErrorAction SilentlyContinue
    if (-not $TlsCertificateThumbprint) { $problems.Add('A site of its own needs -TlsCertificateThumbprint for the HTTPS binding.') }
    elseif ($null -eq $tls) { $problems.Add("The TLS certificate $TlsCertificateThumbprint is not in LocalMachine\My.") }
}
if ($problems.Count -gt 0) {
    throw ("This server is not ready for the Workbench web application:`n - " + ($problems -join "`n - ") + "`nRun deploy\Test-WorkbenchPrerequisites.ps1 -Role Web for the full list.")
}
Write-DeployOk "IIS, Hosting Bundle $($hb.RuntimeVersion), certificates and key ring share are in place"

Import-Module IISAdministration
$sm = Get-IISServerManager

# ------------------------------------------------------------------------------------------------
# Site or application: refuse collisions before changing anything
# ------------------------------------------------------------------------------------------------
$site = $sm.Sites[$SiteName]
if ($ApplicationPath) {
    if ($null -eq $site) { throw "Site '$SiteName' does not exist. To install as an application, name an existing site." }
    $existingApp = $site.Applications[$ApplicationPath]
    if ($existingApp -and $existingApp.ApplicationPoolName -ne $AppPoolName) {
        throw "Application '$ApplicationPath' in '$SiteName' already uses pool '$($existingApp.ApplicationPoolName)', not '$AppPoolName'. Pass -AppPoolName to keep it, or choose another path."
    }
}
else {
    if ($site) {
        $rootApp = $site.Applications['/']
        $rootPath = [Environment]::ExpandEnvironmentVariables($rootApp.VirtualDirectories['/'].PhysicalPath)
        if (-not [string]::Equals([IO.Path]::GetFullPath($rootPath).TrimEnd('\'), [IO.Path]::GetFullPath($InstallPath).TrimEnd('\'), [StringComparison]::OrdinalIgnoreCase)) {
            throw "Site '$SiteName' exists and points to '$rootPath', not '$InstallPath'. Rerun with the same -InstallPath, or choose another -SiteName."
        }
        if ($site.Applications.Count -gt 1 -and $rootApp.ApplicationPoolName -ne $AppPoolName) {
            throw "Site '$SiteName' already holds other applications under pool '$($rootApp.ApplicationPoolName)'. Install as an application with -ApplicationPath instead."
        }
    }
}
$pool = $sm.ApplicationPools[$AppPoolName]
if ($pool) {
    $users = @($sm.Sites | ForEach-Object { $s = $_; $s.Applications | Where-Object ApplicationPoolName -eq $AppPoolName | ForEach-Object { "$($s.Name)$($_.Path)" } })
    $mine = if ($ApplicationPath) { "$SiteName$ApplicationPath" } else { "$SiteName/" }
    $others = @($users | Where-Object { $_ -ne $mine })
    if ($others.Count -gt 0) { throw "App pool '$AppPoolName' is used by $($others -join ', '). The Workbench needs a pool of its own: pass another -AppPoolName." }
}

# ------------------------------------------------------------------------------------------------
# Files (app_offline.htm while copying, then web.config and settings)
# ------------------------------------------------------------------------------------------------
Write-DeployStep "Files to $InstallPath"
Assert-SafeMirrorTarget -Source $PublishPath -Target $InstallPath -Kind Web
$offline = Join-Path $InstallPath 'app_offline.htm'
if ((Test-Path -LiteralPath $InstallPath) -and $PSCmdlet.ShouldProcess($offline, 'Take the application offline while files are copied')) {
    Set-Content -LiteralPath $offline -Value '<!doctype html><title>Updating</title><p>The Workbench is being updated. Try again in a minute.</p>' -Encoding utf8
}
try {
    Invoke-Mirror -Source $PublishPath -Target $InstallPath -Kind Web -KeepFiles @('appsettings.Production.json', 'app_offline.htm')

    # A self-contained publish carries the .NET host (hostfxr.dll) next to the application.
    $selfContained = (Test-Path -LiteralPath (Join-Path $PublishPath 'Governance.Web.exe')) -and (Test-Path -LiteralPath (Join-Path $PublishPath 'hostfxr.dll'))
    $processPath = if ($selfContained) { '.\Governance.Web.exe' } else { 'dotnet' }
    $arguments = if ($selfContained) { '' } else { '.\Governance.Web.dll' }
    $webConfig = @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <location path="." inheritInChildApplications="false">
    <system.webServer>
      <handlers>
        <add name="aspNetCore" path="*" verb="*" modules="AspNetCoreModuleV2" resourceType="Unspecified" />
      </handlers>
      <aspNetCore processPath="$processPath" arguments="$arguments" stdoutLogEnabled="false" stdoutLogFile=".\logs\stdout" hostingModel="inprocess">
        <environmentVariables>
          <environmentVariable name="ASPNETCORE_ENVIRONMENT" value="$EnvironmentName" />
        </environmentVariables>
      </aspNetCore>
      <security>
        <requestFiltering removeServerHeader="true">
          <requestLimits maxAllowedContentLength="$MaxRequestBytes" />
        </requestFiltering>
      </security>
      <httpProtocol>
        <customHeaders>
          <remove name="X-Powered-By" />
        </customHeaders>
      </httpProtocol>
    </system.webServer>
  </location>
</configuration>
"@
    if ($PSCmdlet.ShouldProcess((Join-Path $InstallPath 'web.config'), "Write web.config (in-process, ASPNETCORE_ENVIRONMENT=$EnvironmentName)")) {
        Set-Content -LiteralPath (Join-Path $InstallPath 'web.config') -Value $webConfig -Encoding utf8
        New-Item -ItemType Directory -Force -Path (Join-Path $InstallPath 'logs') | Out-Null
    }
    if ($PSCmdlet.ShouldProcess((Join-Path $InstallPath 'appsettings.Production.json'), "Place the settings file from $SettingsFile")) {
        Copy-Item -LiteralPath $SettingsFile -Destination (Join-Path $InstallPath 'appsettings.Production.json') -Force
        # Only administrators and the app pool identity read the settings file.
        $acl = Get-Acl -LiteralPath (Join-Path $InstallPath 'appsettings.Production.json')
        $acl.SetAccessRuleProtection($true, $false)
        foreach ($id in @('BUILTIN\Administrators', 'NT AUTHORITY\SYSTEM')) { $acl.AddAccessRule([System.Security.AccessControl.FileSystemAccessRule]::new($id, 'FullControl', 'Allow')) }
        $acl.AddAccessRule([System.Security.AccessControl.FileSystemAccessRule]::new($GmsaAccount, 'Read', 'Allow'))
        Set-Acl -LiteralPath (Join-Path $InstallPath 'appsettings.Production.json') -AclObject $acl
    }
    if ($PSCmdlet.ShouldProcess($InstallPath, "Grant $GmsaAccount read and execute on the application folder, modify on logs")) {
        $acl = Get-Acl -LiteralPath $InstallPath
        $acl.AddAccessRule([System.Security.AccessControl.FileSystemAccessRule]::new($GmsaAccount, 'ReadAndExecute', 'ContainerInherit,ObjectInherit', 'None', 'Allow'))
        Set-Acl -LiteralPath $InstallPath -AclObject $acl
        $logs = Join-Path $InstallPath 'logs'
        $acl = Get-Acl -LiteralPath $logs
        $acl.AddAccessRule([System.Security.AccessControl.FileSystemAccessRule]::new($GmsaAccount, 'Modify', 'ContainerInherit,ObjectInherit', 'None', 'Allow'))
        Set-Acl -LiteralPath $logs -AclObject $acl
    }
}
finally {
    if ((Test-Path -LiteralPath $offline) -and -not $WhatIfPreference) { Remove-Item -LiteralPath $offline -Force }
}

# ------------------------------------------------------------------------------------------------
# Certificate private keys readable by the pool identity
# ------------------------------------------------------------------------------------------------
Write-DeployStep 'Certificate key access'
foreach ($cert in @($oidcCert, $dpCert)) {
    $key = Get-CertificateKeyFile -Certificate $cert -StoreLocation LocalMachine
    if ($null -eq $key) { Write-DeployNote "key of $($cert.Subject) not found as a file (hardware module?): grant $GmsaAccount access with the vendor tool"; continue }
    Grant-KeyFileRead -KeyFile $key -Account $GmsaAccount
}

# ------------------------------------------------------------------------------------------------
# App pool, site or application, binding, authentication
# ------------------------------------------------------------------------------------------------
Write-DeployStep "IIS: pool '$AppPoolName', $(if ($ApplicationPath) { "application $SiteName$ApplicationPath" } else { "site '$SiteName'" })"
if ($PSCmdlet.ShouldProcess($AppPoolName, "Create or update the app pool (No Managed Code, AlwaysRunning, idle timeout 0, identity $GmsaAccount)")) {
    $sm = Get-IISServerManager
    $pool = $sm.ApplicationPools[$AppPoolName]
    if ($null -eq $pool) { $pool = $sm.ApplicationPools.Add($AppPoolName) }
    $pool.ManagedRuntimeVersion = ''
    $pool.StartMode = 'AlwaysRunning'
    $pool.AutoStart = $true
    $pool.ProcessModel.IdleTimeout = [TimeSpan]::Zero
    $pool.ProcessModel.LoadUserProfile = $true
    $pool.ProcessModel.IdentityType = 'SpecificUser'
    $pool.ProcessModel.UserName = $GmsaAccount
    $pool.ProcessModel.Password = ''          # a gMSA has no password; Windows retrieves it
    $pool.Recycling.PeriodicRestart.Time = [TimeSpan]::Zero
    $sm.CommitChanges()
    Write-DeployOk 'app pool ready'
}

if ($PSCmdlet.ShouldProcess($(if ($ApplicationPath) { "$SiteName$ApplicationPath" } else { $SiteName }), 'Create or update the site or application')) {
    $sm = Get-IISServerManager
    if ($ApplicationPath) {
        $site = $sm.Sites[$SiteName]
        $app = $site.Applications[$ApplicationPath]
        if ($null -eq $app) { $app = $site.Applications.Add($ApplicationPath, $InstallPath) }
        else { $app.VirtualDirectories['/'].PhysicalPath = $InstallPath }
        $app.ApplicationPoolName = $AppPoolName
        $app['preloadEnabled'] = $true
    }
    else {
        $site = $sm.Sites[$SiteName]
        $bindingInfo = "*:${Port}:$HostName"
        $hash = [Convert]::FromHexString(($TlsCertificateThumbprint -replace '\s', ''))
        if ($null -eq $site) {
            $site = $sm.Sites.Add($SiteName, 'https', $bindingInfo, $InstallPath)
            $site.Bindings[0].CertificateHash = $hash
            $site.Bindings[0].CertificateStoreName = 'My'
            $site.Bindings[0]['sslFlags'] = 1   # SNI
        }
        else {
            $b = $site.Bindings | Where-Object { $_.Protocol -eq 'https' -and $_.BindingInformation -eq $bindingInfo } | Select-Object -First 1
            if ($null -eq $b) { $b = $site.Bindings.Add($bindingInfo, $hash, 'My', [Microsoft.Web.Administration.SslFlags]::Sni) }
            else { $b.CertificateHash = $hash; $b.CertificateStoreName = 'My' }
        }
        $site.Applications['/'].ApplicationPoolName = $AppPoolName
        $site.Applications['/']['preloadEnabled'] = $true
        $site.ServerAutoStart = $true
    }
    $sm.CommitChanges()
    Write-DeployOk 'site or application ready'
}

$location = if ($ApplicationPath) { "$SiteName$ApplicationPath" } else { $SiteName }
if ($PSCmdlet.ShouldProcess($location, 'Anonymous authentication on, Windows authentication off (the application signs people in with Entra ID)')) {
    $sm = Get-IISServerManager
    $cfg = $sm.GetApplicationHostConfiguration()
    $anon = $cfg.GetSection('system.webServer/security/authentication/anonymousAuthentication', $location)
    $anon['enabled'] = $true
    $anon['userName'] = ''          # run anonymous requests as the pool identity
    $win = $cfg.GetSection('system.webServer/security/authentication/windowsAuthentication', $location)
    $win['enabled'] = $false
    $sm.CommitChanges()
    Write-DeployOk 'authentication set'
}

# ------------------------------------------------------------------------------------------------
# Smoke test
# ------------------------------------------------------------------------------------------------
if (-not $WhatIfPreference) {
    $base = if ($ApplicationPath) { "https://localhost$ApplicationPath" } else { "https://$HostName" }
    try {
        $r = Invoke-WebRequest -Uri "$base/health/live" -TimeoutSec 60 -SkipCertificateCheck -SkipHttpErrorCheck
        if ([int]$r.StatusCode -eq 200) { Write-DeployOk "$base/health/live answered 200" }
        else { Write-DeployNote "$base/health/live answered $([int]$r.StatusCode); see $InstallPath\logs and the Application event log" }
    }
    catch { Write-DeployNote "$base/health/live did not answer: $($_.Exception.Message)" }
}
Write-DeployStep 'Done'
