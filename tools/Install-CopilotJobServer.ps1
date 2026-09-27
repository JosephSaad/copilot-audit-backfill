<#
.SYNOPSIS
    Sets up the job server in one command: certificate, service account,
    databases, app registration, settings and the nightly task.

.DESCRIPTION
    Run it elevated on the job server, from the folder the release was
    extracted to. It does each step only if it is not done already, so it is
    safe to run again, and a second run needs no parameters: they are saved in
    copilot-audit.settings.psd1 next to the scripts.

      1. Certificate. Finds the pipeline's certificate in LocalMachine\My (by
         -CertificateThumbprint, or the subject 'CN=Copilot audit pipeline'),
         or creates a self-signed one: 2048-bit RSA, private key not
         exportable, valid 2 years. Exports its public half as
         copilot-audit.cer for the app registration.
      2. Service account. Checks a gMSA is installed on this server (and
         installs it when the AD module is present), gives the account Read on
         the certificate's private key, and the "Log on as a batch job" right.
      3. Connectivity: login.microsoftonline.com and graph.microsoft.com on
         443, and SQL Server.
      4. Databases: Install-CopilotAudit.ps1, with the service account's login,
         the reporting time zone and, with -Dashboard, the dashboard's schema.
      5. App registration, with -RegisterApp: tools\New-CopilotAuditApp.ps1
         signs you in to Microsoft Graph once, registers the app with its three
         permissions and admin consent, and uploads the certificate. Without
         it, pass -TenantId and -ClientId of an app registered already.
      6. Settings: writes copilot-audit.settings.psd1, which
         Invoke-CopilotPipeline.ps1 reads.
      7. The nightly task: 'Copilot audit - nightly', running
         Invoke-CopilotPipeline.ps1 daily as the service account.

    It ends by printing what is left: loading the history, one command.

.PARAMETER ServiceAccount
    The account the nightly task runs as and SQL Server knows: a gMSA
    (DOMAIN\name$, no password) or DOMAIN\user with -ServiceAccountCredential.

.PARAMETER TargetDomain
    The users' domain, read by the user-directory load. It must be another
    domain from this server's, reachable over the trust.

.PARAMETER ReportingTimeZone
    The zone days and weeks are cut in (a name from sys.time_zone_info).
    Default Eastern Standard Time. Set it before the history is loaded.

.PARAMETER RegisterApp
    Register the Entra app too: you sign in as an administrator who can grant
    admin consent. -UseDeviceCode for a server with no browser.

.EXAMPLE
    .\tools\Install-CopilotJobServer.ps1 -SqlServer SQLPROD01 -ServiceAccount 'CORP\gmsa-copilot$' `
        -TargetDomain users.contoso.com -TenantId contoso.onmicrosoft.com -RegisterApp

.EXAMPLE
    .\tools\Install-CopilotJobServer.ps1      # again, later: same settings, only what is missing
#>
#Requires -Version 7.2
[CmdletBinding(SupportsShouldProcess)]
param(
    [string] $SqlServer,
    [string] $Database,
    [string] $LicenceDatabase,
    [switch] $TrustServerCertificate,
    [string] $ServiceAccount,
    [pscredential] $ServiceAccountCredential,
    [string] $TargetDomain,
    [string[]] $EmployeeTypes,
    [string] $TenantId,
    [string] $ClientId,
    [string] $CertificateThumbprint,
    [switch] $RegisterApp,
    [switch] $UseDeviceCode,
    [string] $ReportingTimeZone,
    [switch] $Dashboard,
    [string] $At,
    [switch] $SkipSchedule,
    [switch] $SkipDatabase,
    [string] $SettingsPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:Root = Split-Path $PSScriptRoot -Parent
$script:TaskName = 'Copilot audit - nightly'
$script:CertSubject = 'CN=Copilot audit pipeline'
if (-not $SettingsPath) { $SettingsPath = Join-Path $script:Root 'copilot-audit.settings.psd1' }

function Write-Step([string] $Text) { Write-Information "==> $Text" -InformationAction Continue }
function Write-Done([string] $Text) { Write-Information "    $Text" -InformationAction Continue }

# ===========================================================================
# Settings: what the pipeline reads, kept between runs
# ===========================================================================

$script:SettingOrder = 'SqlServer', 'Database', 'LicenceDatabase', 'TrustServerCertificate', 'TenantId', 'ClientId',
                       'CertificateThumbprint', 'TargetDomain', 'EmployeeTypes', 'Dashboard', 'HistoryDays',
                       'LogPath', 'LogRetentionDays'

function Get-DefaultSetting {
    [ordered]@{
        SqlServer = ''; Database = 'CopilotAudit'; LicenceDatabase = 'CopilotLicenseEvents'; TrustServerCertificate = $false
        TenantId = ''; ClientId = ''; CertificateThumbprint = ''; TargetDomain = ''; EmployeeTypes = @('EMP', 'NONW')
        Dashboard = $false; HistoryDays = 180; LogPath = (Join-Path $script:Root 'logs'); LogRetentionDays = 90
    }
}

function Merge-Setting {
    <# The saved settings, overlaid with the parameters given this time. Pure. #>
    param([System.Collections.IDictionary] $Saved, [System.Collections.IDictionary] $Given)
    $s = Get-DefaultSetting
    foreach ($src in $Saved, $Given) {
        if (-not $src) { continue }
        foreach ($k in @($src.Keys)) { if ($s.Contains($k) -and $null -ne $src[$k] -and "$($src[$k])" -ne '') { $s[$k] = $src[$k] } }
    }
    $s
}

function ConvertTo-SettingText {
    <# A .psd1 that Import-PowerShellDataFile reads back as the same values. Pure. #>
    param([System.Collections.IDictionary] $Setting)
    function Q($v) {
        if ($v -is [bool]) { return $(if ($v) { '$true' } else { '$false' }) }
        if ($v -is [int] -or $v -is [long]) { return "$v" }
        if ($v -is [array]) { return '@(' + ((@($v) | ForEach-Object { Q $_ }) -join ', ') + ')' }
        "'" + ([string] $v).Replace("'", "''") + "'"
    }
    $lines = @('# Written by tools\Install-CopilotJobServer.ps1 and read by Invoke-CopilotPipeline.ps1.',
               '# Run the installer again to change a value; editing by hand works too.', '@{')
    foreach ($k in $script:SettingOrder) { if ($Setting.Contains($k)) { $lines += "    $k = $(Q $Setting[$k])" } }
    ($lines + '}') -join "`r`n"
}

# ===========================================================================
# The certificate and its private key
# ===========================================================================

function Get-PipelineCertificate {
    param([string] $Thumbprint)
    $store = 'Cert:\LocalMachine\My'
    if ($Thumbprint) {
        $c = Get-Item -LiteralPath (Join-Path $store $Thumbprint) -ErrorAction SilentlyContinue
        if (-not $c) { throw "No certificate $Thumbprint in LocalMachine\My." }
        # One this script made, within 30 days of expiry: renewed below. Your own PKI's: kept, and flagged.
        if ($c.Subject -ne $script:CertSubject -or $c.NotAfter -gt (Get-Date).AddDays(30)) { return $c }
    }
    Get-ChildItem $store | Where-Object { $_.Subject -eq $script:CertSubject -and $_.HasPrivateKey -and $_.NotAfter -gt (Get-Date).AddDays(30) } |
        Sort-Object NotAfter -Descending | Select-Object -First 1
}

function Get-PrivateKeyFile {
    <# The file holding a machine certificate's private key: CNG or the legacy CSP. #>
    param([System.Security.Cryptography.X509Certificates.X509Certificate2] $Certificate)
    $rsa = [System.Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($Certificate)
    if (-not $rsa) { throw "Certificate $($Certificate.Thumbprint) has no RSA private key." }
    # .NET opens a legacy CSP key (what New-SelfSignedCertificate -KeySpec Signature makes) as RSACng too,
    # so the provider type does not say which folder holds it: look in both.
    $name = if ($rsa -is [System.Security.Cryptography.RSACng]) { $rsa.Key.UniqueName }
            elseif ($rsa -is [System.Security.Cryptography.RSACryptoServiceProvider]) { $rsa.CspKeyContainerInfo.UniqueKeyContainerName }
    if (-not $name) { throw "The private key of $($Certificate.Thumbprint) is not in a file (a TPM or HSM key?): grant the service account access with your provider's tools." }
    $data = [Environment]::GetFolderPath('CommonApplicationData')
    foreach ($dir in 'Microsoft\Crypto\Keys', 'Microsoft\Crypto\RSA\MachineKeys') {
        $p = Join-Path (Join-Path $data $dir) $name
        if (Test-Path -LiteralPath $p) { return $p }
    }
    throw "The private key file $name of $($Certificate.Thumbprint) is in neither machine key folder. Is the certificate in LocalMachine\My?"
}

function Grant-PrivateKeyRead {
    param([System.Security.Cryptography.X509Certificates.X509Certificate2] $Certificate, [string] $Account)
    $file = Get-PrivateKeyFile $Certificate
    $acl = Get-Acl -LiteralPath $file
    $sid = ([System.Security.Principal.NTAccount] $Account).Translate([System.Security.Principal.SecurityIdentifier])
    $has = @($acl.Access | Where-Object {
        $_.AccessControlType -eq 'Allow' -and ($_.FileSystemRights -band [System.Security.AccessControl.FileSystemRights]::Read) -eq [System.Security.AccessControl.FileSystemRights]::Read -and
        $(try { $_.IdentityReference.Translate([System.Security.Principal.SecurityIdentifier]) -eq $sid } catch { $false }) })
    if ($has) { Write-Done "$Account can already read the private key"; return }
    if ($PSCmdlet.ShouldProcess($file, "Give $Account Read on the private key")) {
        $acl.AddAccessRule([System.Security.AccessControl.FileSystemAccessRule]::new($sid, 'Read', 'Allow'))
        Set-Acl -LiteralPath $file -AclObject $acl
        Write-Done "$Account can read the private key"
    }
}

# ===========================================================================
# The service account's right to run a scheduled task
# ===========================================================================

function Add-SidToRightLine {
    <# 'SeBatchLogonRight = *S-1-5-32-544,*S-1-...' with the SID added once. Pure. #>
    param([string] $Line, [string] $Right, [string] $Sid)
    $entries = @()
    if ($Line -match '=\s*(.*)$') { $entries = @($Matches[1].Split(',') | ForEach-Object { $_.Trim() } | Where-Object { $_ }) }
    if ($entries -contains "*$Sid") { return $null }
    "$Right = " + (@($entries) + "*$Sid" -join ',')
}

function Grant-BatchLogonRight {
    param([string] $Account)
    $sid = ([System.Security.Principal.NTAccount] $Account).Translate([System.Security.Principal.SecurityIdentifier]).Value
    $dir = Join-Path ([System.IO.Path]::GetTempPath()) "copilot-secedit-$PID"
    New-Item -ItemType Directory -Path $dir -Force | Out-Null
    try {
        $export = Join-Path $dir 'export.inf'
        & secedit.exe /export /cfg $export /areas USER_RIGHTS /quiet | Out-Null
        $current = @(Get-Content -LiteralPath $export -ErrorAction SilentlyContinue | Where-Object { $_ -match '^\s*SeBatchLogonRight\s*=' })
        $line = Add-SidToRightLine -Line $(if ($current) { $current[0] } else { '' }) -Right 'SeBatchLogonRight' -Sid $sid
        if (-not $line) { Write-Done "$Account can already log on as a batch job"; return }
        if ($PSCmdlet.ShouldProcess($Account, 'Grant Log on as a batch job')) {
            $inf = Join-Path $dir 'grant.inf'
            Set-Content -LiteralPath $inf -Encoding Unicode -Value @('[Unicode]', 'Unicode=yes', '[Version]', 'signature="$CHICAGO$"', 'Revision=1', '[Privilege Rights]', $line)
            & secedit.exe /configure /db (Join-Path $dir 'grant.sdb') /cfg $inf /areas USER_RIGHTS /quiet | Out-Null
            if ($LASTEXITCODE -ne 0) { throw "secedit could not grant the right (exit $LASTEXITCODE)." }
            Write-Done "$Account can log on as a batch job (a domain Group Policy that sets this right replaces it at the next refresh: add the account there instead)"
        }
    }
    finally { Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction SilentlyContinue }
}

function Test-ServiceAccount {
    param([string] $Account)
    if (-not $Account.EndsWith('$')) { return }
    $name = $Account.Split('\')[-1]
    if (-not (Get-Command Test-ADServiceAccount -ErrorAction SilentlyContinue)) {
        Write-Warning "The ActiveDirectory module is not installed, so the gMSA could not be checked. Install-ADServiceAccount $name and Test-ADServiceAccount $name should return True."
        return
    }
    if (Test-ADServiceAccount -Identity $name) { Write-Done "$Account is installed on this server"; return }
    if ($PSCmdlet.ShouldProcess($name, 'Install-ADServiceAccount')) { Install-ADServiceAccount -Identity $name }
    if (-not (Test-ADServiceAccount -Identity $name)) {
        throw "$Account cannot be used on this server. Add $env:COMPUTERNAME`$ to the gMSA's PrincipalsAllowedToRetrieveManagedPassword, then run this again."
    }
    Write-Done "$Account installed on this server"
}

# ===========================================================================
# Connectivity
# ===========================================================================

function Test-Endpoint {
    param([string] $HostName, [int] $Port = 443)
    $c = [System.Net.Sockets.TcpClient]::new()
    try { return $c.ConnectAsync($HostName, $Port).Wait(5000) -and $c.Connected }
    catch { return $false }
    finally { $c.Dispose() }
}

# ===========================================================================
# The nightly task
# ===========================================================================

function Register-NightlyTask {
    param([string] $Account, [pscredential] $Credential, [string] $Time)
    $pipeline = Join-Path $script:Root 'Invoke-CopilotPipeline.ps1'
    $pwsh = (Get-Command pwsh -ErrorAction Stop).Source
    $action = New-ScheduledTaskAction -Execute $pwsh -WorkingDirectory $script:Root `
        -Argument "-NoProfile -NonInteractive -File `"$pipeline`""
    $trigger = New-ScheduledTaskTrigger -Daily -At $Time
    $settings = New-ScheduledTaskSettingsSet -StartWhenAvailable -MultipleInstances IgnoreNew -ExecutionTimeLimit (New-TimeSpan -Hours 20)
    if (-not $PSCmdlet.ShouldProcess($script:TaskName, "Schedule daily at $Time as $Account")) { return }
    if ($Account.EndsWith('$')) {
        # LogonType Password is how Task Scheduler is told to fetch a gMSA's password from AD.
        $principal = New-ScheduledTaskPrincipal -UserId $Account -LogonType Password -RunLevel Limited
        Register-ScheduledTask -TaskName $script:TaskName -Action $action -Trigger $trigger -Settings $settings -Principal $principal -Force | Out-Null
    }
    else {
        if (-not $Credential) { throw "$Account is not a gMSA: pass -ServiceAccountCredential so the task can store its password." }
        Register-ScheduledTask -TaskName $script:TaskName -Action $action -Trigger $trigger -Settings $settings `
            -User $Credential.UserName -Password $Credential.GetNetworkCredential().Password -RunLevel Limited -Force | Out-Null
    }
    Write-Done "'$script:TaskName' runs daily at $Time as $Account"
}

# ===========================================================================
# Main
# ===========================================================================

function Invoke-Main {
    [CmdletBinding(SupportsShouldProcess)] param()
    $isAdmin = ([System.Security.Principal.WindowsPrincipal] [System.Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
        [System.Security.Principal.WindowsBuiltInRole]::Administrator)
    if (-not $isAdmin -and -not $WhatIfPreference) { throw 'Run this in an elevated PowerShell 7 window: it writes the machine certificate store and schedules a task.' }

    $saved = if (Test-Path -LiteralPath $SettingsPath) { Import-PowerShellDataFile -LiteralPath $SettingsPath } else { @{} }
    $given = @{}
    foreach ($k in 'SqlServer', 'Database', 'LicenceDatabase', 'TenantId', 'ClientId', 'CertificateThumbprint', 'TargetDomain', 'EmployeeTypes') {
        $v = Get-Variable -Name $k -ValueOnly
        if ($v) { $given[$k] = $v }
    }
    if ($TrustServerCertificate) { $given.TrustServerCertificate = $true }
    if ($Dashboard) { $given.Dashboard = $true }
    $s = Merge-Setting -Saved $saved -Given $given
    if (-not $s.SqlServer) { throw 'Pass -SqlServer the first time (it is saved for later runs).' }
    $account = if ($ServiceAccount) { $ServiceAccount } elseif ($saved.Contains('ServiceAccount')) { $saved.ServiceAccount } else { $null }
    $taskAt = if ($At) { $At } else { '02:00' }
    $todo = @()

    # ---- 1. Certificate --------------------------------------------------------------------
    Write-Step 'Certificate'
    $cert = Get-PipelineCertificate -Thumbprint $s.CertificateThumbprint
    if (-not $cert -and $PSCmdlet.ShouldProcess($script:CertSubject, 'Create a self-signed certificate in LocalMachine\My')) {
        $cert = New-SelfSignedCertificate -Subject $script:CertSubject -CertStoreLocation Cert:\LocalMachine\My `
            -KeySpec Signature -KeyLength 2048 -KeyExportPolicy NonExportable -NotAfter (Get-Date).AddYears(2)
        Write-Done "created $($cert.Thumbprint), valid until $($cert.NotAfter.ToString('yyyy-MM-dd'))"
    }
    elseif ($cert) { Write-Done "using $($cert.Thumbprint), valid until $($cert.NotAfter.ToString('yyyy-MM-dd'))" }
    $cerPath = Join-Path $script:Root 'copilot-audit.cer'
    if ($cert) {
        $s.CertificateThumbprint = $cert.Thumbprint
        if ($PSCmdlet.ShouldProcess($cerPath, 'Export the public certificate')) {
            [System.IO.File]::WriteAllBytes($cerPath, $cert.RawData)
            Write-Done "public half: $cerPath"
        }
        if ($cert.NotAfter -lt (Get-Date).AddDays(60)) { $todo += "The certificate expires on $($cert.NotAfter.ToString('yyyy-MM-dd')): within 30 days of that, run this again with -RegisterApp: it makes a new one and uploads it." }
    }

    # ---- 2. Service account ----------------------------------------------------------------
    Write-Step 'Service account'
    if (-not $account) { throw 'Pass -ServiceAccount the first time: the account the nightly task runs as (a gMSA such as CORP\gmsa-copilot$).' }
    Test-ServiceAccount $account
    if ($cert) { Grant-PrivateKeyRead -Certificate $cert -Account $account }
    Grant-BatchLogonRight $account

    # ---- 3. Connectivity -------------------------------------------------------------------
    Write-Step 'Connectivity'
    foreach ($h in 'login.microsoftonline.com', 'graph.microsoft.com') {
        if (Test-Endpoint $h) { Write-Done "$h reachable on 443" }
        else { Write-Warning "$h is not reachable on 443 directly. Through a proxy that is fine if the system proxy (netsh winhttp show proxy) is set; otherwise open it." }
    }

    # ---- 4. Databases ----------------------------------------------------------------------
    if (-not $SkipDatabase) {
        Write-Step "Databases on $($s.SqlServer)"
        $install = @{ SqlServer = $s.SqlServer; Database = $s.Database; LicenceDatabase = $s.LicenceDatabase; ServiceAccount = $account }
        if ($s.TrustServerCertificate) { $install.TrustServerCertificate = $true }
        if ($s.Dashboard) { $install.Dashboard = $true }
        if ($ReportingTimeZone) { $install.ReportingTimeZone = $ReportingTimeZone }
        if ($PSCmdlet.ShouldProcess($s.SqlServer, 'Install-CopilotAudit.ps1')) { & (Join-Path $script:Root 'Install-CopilotAudit.ps1') @install }
    }

    # ---- 5. App registration ---------------------------------------------------------------
    Write-Step 'App registration'
    if ($RegisterApp) {
        if (-not $s.TenantId) { throw 'Pass -TenantId with -RegisterApp.' }
        $app = & (Join-Path $PSScriptRoot 'New-CopilotAuditApp.ps1') -TenantId $s.TenantId -CertificatePath $cerPath -UseDeviceCode:$UseDeviceCode
        if ($app) { $s.TenantId = $app.TenantId; $s.ClientId = $app.ClientId }
    }
    elseif ($s.TenantId -and $s.ClientId) { Write-Done "using client id $($s.ClientId) in $($s.TenantId)" }
    else { $todo += "Register the app: run this again with -TenantId <tenant> -RegisterApp (or register it by hand, upload $cerPath, and pass -TenantId and -ClientId)." }

    # ---- 6. Settings -------------------------------------------------------------------------
    Write-Step 'Settings'
    $out = [ordered]@{}; foreach ($k in $s.Keys) { $out[$k] = $s[$k] }
    if ($PSCmdlet.ShouldProcess($SettingsPath, 'Write the settings')) {
        $text = ConvertTo-SettingText $out
        # The service account is kept for later runs of this script; the pipeline ignores it.
        $text = $text.Replace("`r`n}", "`r`n    ServiceAccount = '$($account.Replace("'", "''"))'`r`n}")
        Set-Content -LiteralPath $SettingsPath -Value $text -Encoding utf8
        New-Item -ItemType Directory -Path $s.LogPath -Force | Out-Null
        Write-Done $SettingsPath
    }
    if (-not $s.TargetDomain) { $todo += 'Name the users'' domain for the directory load: run this again with -TargetDomain <users domain>.' }

    # ---- 7. The nightly task -----------------------------------------------------------------
    if (-not $SkipSchedule) {
        Write-Step 'Nightly task'
        Register-NightlyTask -Account $account -Credential $ServiceAccountCredential -Time $taskAt
    }

    Write-Information '' -InformationAction Continue
    Write-Information $(if ($WhatIfPreference) { 'Dry run (-WhatIf): nothing was changed.' } else { 'Job server ready.' }) -InformationAction Continue
    $todo += "Load the history, from this window: .\Invoke-CopilotPipeline.ps1 -History   (hours; resumes where it stopped if interrupted)"
    Write-Information 'Still to do:' -InformationAction Continue
    $todo | ForEach-Object { Write-Information "  - $_" -InformationAction Continue }
}

# Dot-sourcing (tests) loads the functions without running anything.
if ($MyInvocation.InvocationName -ne '.') { Invoke-Main }
