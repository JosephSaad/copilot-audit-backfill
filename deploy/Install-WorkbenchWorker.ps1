<#
.SYNOPSIS
    Installs or upgrades the Workbench worker as a Windows Service under its own gMSA: files, settings,
    service with delayed automatic start and recovery actions, worker certificate key access, outbound proxy.
    Safe to rerun; supports -WhatIf.

.DESCRIPTION
    Two worker nodes may run at once: every singleton job takes a SQL application lock (sp_getapplock), so
    a job never runs on both. Install the same version on both nodes.

    The service is stopped while the files are mirrored and started again afterwards. Files are mirrored
    with robocopy only into an empty folder or a folder this kit installed before (.cgw-install marker);
    appsettings.Production.json is never deleted by the mirror.

.EXAMPLE
    pwsh -File deploy\Install-WorkbenchWorker.ps1 -PublishPath D:\Drop\Worker -InstallPath D:\CopilotGovernance\Worker `
        -GmsaAccount 'CORP\gmsa-cgw-worker$' -SettingsFile D:\Drop\appsettings.Worker.Production.json `
        -ProxyUrl http://proxy.corp.example:8080 -WhatIf
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)] [string] $PublishPath,
    [Parameter(Mandatory)] [string] $InstallPath,
    [Parameter(Mandatory)] [string] $SettingsFile,
    [Parameter(Mandatory)] [string] $GmsaAccount,
    [string] $ServiceName = 'CopilotGovernanceWorker',
    [string] $DisplayName = 'Copilot Governance Workbench worker',
    [string] $ProxyUrl = '',
    [string] $NoProxy = 'localhost,127.0.0.1',
    [string] $EnvironmentName = 'Production'
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'WorkbenchDeploy.Common.ps1')

if (-not $IsWindows) { throw 'The worker installs on Windows Server.' }
if (-not (Test-GmsaName $GmsaAccount)) { throw "-GmsaAccount '$GmsaAccount' must be a group managed service account: DOMAIN\name`$." }
if ($ServiceName -notmatch '^[A-Za-z][A-Za-z0-9_]{2,60}$') { throw "-ServiceName '$ServiceName' must be letters, digits and underscores." }
if ($ProxyUrl -and $ProxyUrl -notmatch '^https?://[^\s/]+(:\d+)?/?$') { throw "-ProxyUrl '$ProxyUrl' must look like http://proxy.corp.example:8080." }

$exe = Join-Path $PublishPath 'Governance.Worker.exe'
if (-not (Test-Path -LiteralPath $exe)) {
    throw "'$PublishPath' does not hold a published worker (Governance.Worker.exe missing). Run: dotnet publish src\Governance.Worker -c Release -r win-x64 -o <folder>."
}
$settings = Assert-SettingsFile -Path $SettingsFile -RequiredKeys @(
    'ConnectionStrings:GovernanceWorker', 'Tenant:TenantId', 'Tenant:WorkerClientId', 'Tenant:WorkerCertificateSubject',
    'Notifications:Mailbox', 'Workbench:BaseUrl')

# ------------------------------------------------------------------------------------------------
# Prerequisites (refuse with a plain message)
# ------------------------------------------------------------------------------------------------
Write-DeployStep 'Prerequisites'
$problems = [System.Collections.Generic.List[string]]::new()
if (-not (Test-IsAdministrator)) { $problems.Add('Run from an elevated PowerShell 7 session (services and certificate keys need administrator rights).') }
$selfContained = Test-Path -LiteralPath (Join-Path $PublishPath 'hostfxr.dll')
if (-not $selfContained -and -not (Get-DotNetRuntime10)) { $problems.Add('The .NET 10 runtime is not installed, and the worker was not published self-contained.') }
$subject = $settings['Worker']['CertificateDistinguishedName']
$cert = Get-WorkbenchCertificate -Subject $subject -StoreLocation LocalMachine
if ($null -eq $cert) { $problems.Add("The worker certificate '$subject' is not in LocalMachine\My with a private key.") }
elseif (($cert.NotAfter - (Get-Date)).TotalDays -lt 30) { $problems.Add("The worker certificate '$subject' expires on $($cert.NotAfter.ToString('yyyy-MM-dd')): rotate it first.") }
if ($problems.Count -gt 0) {
    throw ("This server is not ready for the Workbench worker:`n - " + ($problems -join "`n - ") + "`nRun deploy\Test-WorkbenchPrerequisites.ps1 -Role Worker for the full list.")
}
Write-DeployOk "runtime and certificate $($cert.Thumbprint) in place"

# ------------------------------------------------------------------------------------------------
# Stop, copy, settings
# ------------------------------------------------------------------------------------------------
$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($service) {
    $existing = Get-CimInstance Win32_Service -Filter "Name='$ServiceName'"
    $existingDir = Split-Path ($existing.PathName.Trim('"') -replace '"\s.*$', '')
    if (-not [string]::Equals([IO.Path]::GetFullPath($existingDir).TrimEnd('\'), [IO.Path]::GetFullPath($InstallPath).TrimEnd('\'), [StringComparison]::OrdinalIgnoreCase)) {
        throw "Service '$ServiceName' already runs from '$existingDir', not '$InstallPath'. Rerun with the same -InstallPath, or choose another -ServiceName."
    }
}

Write-DeployStep "Files to $InstallPath"
Assert-SafeMirrorTarget -Source $PublishPath -Target $InstallPath -Kind Worker
if ($service -and $service.Status -ne 'Stopped' -and $PSCmdlet.ShouldProcess($ServiceName, 'Stop the service while files are copied')) {
    Stop-Service -Name $ServiceName -Force
    (Get-Service -Name $ServiceName).WaitForStatus('Stopped', [TimeSpan]::FromMinutes(2))
}
Invoke-Mirror -Source $PublishPath -Target $InstallPath -Kind Worker -KeepFiles @('appsettings.Production.json')

$target = Join-Path $InstallPath 'appsettings.Production.json'
if ($PSCmdlet.ShouldProcess($target, "Place the settings file from $SettingsFile, readable by $GmsaAccount only")) {
    Copy-Item -LiteralPath $SettingsFile -Destination $target -Force
    $acl = Get-Acl -LiteralPath $target
    $acl.SetAccessRuleProtection($true, $false)
    foreach ($id in @('BUILTIN\Administrators', 'NT AUTHORITY\SYSTEM')) { $acl.AddAccessRule([System.Security.AccessControl.FileSystemAccessRule]::new($id, 'FullControl', 'Allow')) }
    $acl.AddAccessRule([System.Security.AccessControl.FileSystemAccessRule]::new($GmsaAccount, 'Read', 'Allow'))
    Set-Acl -LiteralPath $target -AclObject $acl
}
if ($PSCmdlet.ShouldProcess($InstallPath, "Grant $GmsaAccount read and execute on the worker folder, modify on logs")) {
    $acl = Get-Acl -LiteralPath $InstallPath
    $acl.AddAccessRule([System.Security.AccessControl.FileSystemAccessRule]::new($GmsaAccount, 'ReadAndExecute', 'ContainerInherit,ObjectInherit', 'None', 'Allow'))
    Set-Acl -LiteralPath $InstallPath -AclObject $acl
    $logs = Join-Path $InstallPath 'logs'
    New-Item -ItemType Directory -Force -Path $logs | Out-Null
    $acl = Get-Acl -LiteralPath $logs
    $acl.AddAccessRule([System.Security.AccessControl.FileSystemAccessRule]::new($GmsaAccount, 'Modify', 'ContainerInherit,ObjectInherit', 'None', 'Allow'))
    Set-Acl -LiteralPath $logs -AclObject $acl
}

# ------------------------------------------------------------------------------------------------
# Certificate key access
# ------------------------------------------------------------------------------------------------
Write-DeployStep 'Certificate key access'
$key = Get-CertificateKeyFile -Certificate $cert -StoreLocation LocalMachine
if ($null -eq $key) { Write-DeployNote "key of $subject not found as a file (hardware module?): grant $GmsaAccount access with the vendor tool" }
else { Grant-KeyFileRead -KeyFile $key -Account $GmsaAccount }

# ------------------------------------------------------------------------------------------------
# Service: create or update, gMSA logon, delayed auto start, recovery, environment
# ------------------------------------------------------------------------------------------------
Write-DeployStep "Service '$ServiceName'"
$binPath = "`"$(Join-Path $InstallPath 'Governance.Worker.exe')`""
function Invoke-Sc([string[]] $ScArgs) {
    $out = & sc.exe @ScArgs 2>&1
    if ($LASTEXITCODE -ne 0) { throw "sc.exe $($ScArgs[0]) failed ($LASTEXITCODE): $("$out".Trim())" }
}
if ($PSCmdlet.ShouldProcess($ServiceName, "Create or update the service (binPath $binPath, logon $GmsaAccount, delayed auto start)")) {
    if (-not (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue)) {
        Invoke-Sc @('create', $ServiceName, "binPath=", $binPath, "start=", 'delayed-auto', "obj=", $GmsaAccount, "password=", '', "DisplayName=", $DisplayName)
        Write-DeployOk 'service created'
    }
    else {
        Invoke-Sc @('config', $ServiceName, "binPath=", $binPath, "start=", 'delayed-auto', "obj=", $GmsaAccount, "password=", '', "DisplayName=", $DisplayName)
        Write-DeployOk 'service updated'
    }
    Invoke-Sc @('description', $ServiceName, 'Background jobs of the Copilot Governance Workbench: outbox, SLA timers, Entra and Power Platform reads, ITSM status, campaigns, digests.')
    # Restart after 1 and 2 minutes, then after 5; reset the failure count after a day.
    Invoke-Sc @('failure', $ServiceName, 'reset=', '86400', 'actions=', 'restart/60000/restart/120000/restart/300000')
    Invoke-Sc @('failureflag', $ServiceName, '1')
}

# Environment variables for the service only (REG_MULTI_SZ "Environment" under the service key).
$envVars = [System.Collections.Generic.List[string]]::new()
$envVars.Add("DOTNET_ENVIRONMENT=$EnvironmentName")
if ($ProxyUrl) {
    $envVars.Add("HTTPS_PROXY=$ProxyUrl")
    $envVars.Add("HTTP_PROXY=$ProxyUrl")
    $envVars.Add("NO_PROXY=$NoProxy")
}
$regPath = "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName"
if ($PSCmdlet.ShouldProcess($regPath, "Set service environment: $($envVars -join '; ')")) {
    New-ItemProperty -Path $regPath -Name 'Environment' -PropertyType MultiString -Value $envVars.ToArray() -Force | Out-Null
    Write-DeployOk 'service environment set'
}

# "Log on as a service": granted by Windows when sc.exe sets a gMSA logon on most builds; verify.
if (-not $WhatIfPreference) {
    $tmp = [IO.Path]::Combine([IO.Path]::GetTempPath(), "cgw-secpol-$([guid]::NewGuid().ToString('n')).inf")
    try {
        & secedit.exe /export /cfg $tmp /areas USER_RIGHTS | Out-Null
        $line = Select-String -LiteralPath $tmp -Pattern '^SeServiceLogonRight' | Select-Object -First 1
        $sid = ([Security.Principal.NTAccount]$GmsaAccount).Translate([Security.Principal.SecurityIdentifier]).Value
        if (-not $line -or ($line.Line -notmatch [regex]::Escape($sid) -and $line.Line -notmatch [regex]::Escape($GmsaAccount.Split('\')[1]))) {
            Write-DeployNote "$GmsaAccount may lack 'Log on as a service'. Grant it by Group Policy (Computer Configuration > Windows Settings > Security Settings > Local Policies > User Rights Assignment) if the service fails to start with error 1069."
        }
    }
    catch { Write-DeployNote "Could not read user rights: $($_.Exception.Message)" }
    finally { if (Test-Path -LiteralPath $tmp) { Remove-Item -LiteralPath $tmp -Force } }
}

if ($PSCmdlet.ShouldProcess($ServiceName, 'Start the service')) {
    Start-Service -Name $ServiceName
    (Get-Service -Name $ServiceName).WaitForStatus('Running', [TimeSpan]::FromMinutes(2))
    Write-DeployOk 'service running; job runs appear on Admin > Integrations within a few minutes'
}
Write-DeployStep 'Done'
