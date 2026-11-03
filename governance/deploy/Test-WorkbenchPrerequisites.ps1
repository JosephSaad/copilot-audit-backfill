<#
.SYNOPSIS
    Checks a server is ready for the Workbench part of Copilot Insights: a web server, the worker's server, or
    both. Changes nothing.

.DESCRIPTION
    Prints one line per check (PASS, WARN, FAIL, SKIP) and exits with 1 when any check fails.
    Checks: Windows and PowerShell 7, elevation, IIS and the ASP.NET Core 10 Hosting Bundle (web), the .NET 10
    runtime (worker), the gMSA on this host, certificates in LocalMachine\My (present, private key, more than
    30 days left, key readable by the gMSA), SQL Server 2022 or later reachable with Windows authentication and
    encryption, outbound HTTPS through the proxy to Microsoft and the ITSM, the form-key share (web), and Windows
    time synchronization.

    dashboard\Install-CopilotInsights.ps1 -GovernanceDatabase runs it before it changes the site, and stops when
    a check fails. SQL is tested as the account running this script, in master, so it works before the
    governance database exists. To test as the gMSA itself, run this script from a scheduled task that runs as
    the gMSA (governance\deploy\Deploy-Production.md, step 4).

.EXAMPLE
    pwsh -File governance\deploy\Test-WorkbenchPrerequisites.ps1 -Role Worker -SqlServer SQLPROD01 -Database CopilotGovernance `
        -GmsaAccount 'CORP\gmsa-cgw-worker$' -WorkerCertificateSubject 'CN=CopilotGovernanceWorker' `
        -ProxyUrl http://proxy.corp.example:8080 -ItsmUrl https://corp.service-now.com
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [ValidateSet('Web', 'Worker', 'Both')] [string] $Role,
    [string] $SqlServer = '',
    [string] $Database = 'CopilotGovernance',
    [string] $GmsaAccount = '',
    [string] $CertificateSubject = '',
    [string] $WorkerCertificateSubject = '',
    [string] $KeyRingPath = '',
    [string] $ProxyUrl = '',
    [string] $ItsmUrl = '',
    [int] $MinimumCertificateDays = 30,
    # When SQL Server has its own self-signed certificate, as for the installer.
    [switch] $TrustServerCertificate
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'WorkbenchDeploy.Common.ps1')

$results = [System.Collections.Generic.List[object]]::new()
function Add-Result([string] $Name, [string] $Status, [string] $Detail) { $results.Add((ConvertTo-CheckResult -Name $Name -Status $Status -Detail $Detail)) }

# Platform -------------------------------------------------------------------------------------------
if (-not $IsWindows) { Add-Result 'Windows' 'FAIL' 'The Workbench runs on Windows Server 2022 or 2025.' }
else {
    $os = (Get-CimInstance Win32_OperatingSystem).Caption
    Add-Result 'Windows' ($(if ($os -match 'Server 20(22|25)') { 'PASS' } else { 'WARN' })) $os
}
Add-Result 'PowerShell 7' ($(if ($PSVersionTable.PSVersion.Major -ge 7) { 'PASS' } else { 'FAIL' })) "$($PSVersionTable.PSVersion)"
Add-Result 'Elevated session' ($(if (Test-IsAdministrator) { 'PASS' } else { 'WARN' })) 'Installers need an elevated session; this check does not.'

# Web: IIS and the Hosting Bundle ---------------------------------------------------------------------
if ($Role -in 'Web', 'Both') {
    $w3svc = Get-Service -Name W3SVC -ErrorAction SilentlyContinue
    Add-Result 'IIS (W3SVC)' ($(if ($w3svc) { 'PASS' } else { 'FAIL' })) ($(if ($w3svc) { "service $($w3svc.Status)" } else { 'IIS is not installed. Add the Web Server role with the default features.' }))
    $hb = Get-HostingBundleStatus
    $hbOk = $hb.ModulePresent -and $hb.AspNetCore10
    Add-Result 'ASP.NET Core 10 Hosting Bundle' ($(if ($hbOk) { 'PASS' } else { 'FAIL' })) ($(if ($hbOk) { "runtime $($hb.RuntimeVersion)" } else { "module present: $($hb.ModulePresent); ASP.NET Core 10 runtime: $($hb.AspNetCore10). Install the .NET 10 Hosting Bundle, then run 'net stop was /y' and 'net start w3svc'." }))
    if ($KeyRingPath) {
        if (-not $KeyRingPath.StartsWith('\\')) { Add-Result 'Key ring share' 'WARN' "'$KeyRingPath' is not a UNC path; several web servers must share one set of form keys (Insights:DataProtectionKeysPath)." }
        elseif (Test-Path -LiteralPath $KeyRingPath) {
            $detail = 'reachable from this session'
            if ($GmsaAccount) { $detail += $(if (Test-AccountHasRead -Path $KeyRingPath -Account $GmsaAccount) { "; $GmsaAccount has NTFS access" } else { "; no explicit NTFS entry for $GmsaAccount found (it may come through a group)" }) }
            Add-Result 'Key ring share' 'PASS' $detail
        }
        else { Add-Result 'Key ring share' 'FAIL' "'$KeyRingPath' is not reachable from this session." }
    }
    else { Add-Result 'Key ring share' 'SKIP' 'Pass -KeyRingPath to check it.' }
}

# Worker: .NET 10 runtime ------------------------------------------------------------------------------
if ($Role -in 'Worker', 'Both') {
    $rt = Get-DotNetRuntime10
    Add-Result '.NET 10 runtime' ($(if ($rt) { 'PASS' } else { 'FAIL' })) ($(if ($rt) { $rt } else { 'Install the .NET 10 runtime (or publish the worker self-contained).' }))
}

# gMSA on this host ------------------------------------------------------------------------------------
if ($GmsaAccount) {
    if (-not (Test-GmsaName $GmsaAccount)) { Add-Result 'gMSA name' 'FAIL' "'$GmsaAccount' is not DOMAIN\name`$." }
    elseif (Get-Module -ListAvailable ActiveDirectory) {
        $sam = $GmsaAccount.Split('\')[1].TrimEnd('$')
        try {
            $ok = Test-ADServiceAccount -Identity $sam
            Add-Result 'gMSA usable on this host' ($(if ($ok) { 'PASS' } else { 'FAIL' })) ($(if ($ok) { $GmsaAccount } else { "Allow this computer in PrincipalsAllowedToRetrieveManagedPassword, then run Install-ADServiceAccount $sam." }))
        }
        catch { Add-Result 'gMSA usable on this host' 'FAIL' "Test-ADServiceAccount failed: $($_.Exception.Message)" }
    }
    else { Add-Result 'gMSA usable on this host' 'SKIP' 'ActiveDirectory module not installed (RSAT-AD-PowerShell).' }
}

# Certificates -----------------------------------------------------------------------------------------
$certChecks = @()
if ($CertificateSubject) { $certChecks += , @('Web certificate', $CertificateSubject) }
if ($WorkerCertificateSubject) { $certChecks += , @('Worker certificate', $WorkerCertificateSubject) }
foreach ($pair in $certChecks) {
    $label, $subject = $pair
    $cert = Get-WorkbenchCertificate -Subject $subject -StoreLocation LocalMachine
    if ($null -eq $cert) { Add-Result $label 'FAIL' "No valid certificate with a private key and subject '$subject' in LocalMachine\My."; continue }
    $days = [int]($cert.NotAfter - (Get-Date)).TotalDays
    Add-Result $label ($(if ($days -ge $MinimumCertificateDays) { 'PASS' } else { 'FAIL' })) "$($cert.Thumbprint), $days days left"
    if ($GmsaAccount) {
        $key = Get-CertificateKeyFile -Certificate $cert -StoreLocation LocalMachine
        if ($null -eq $key) { Add-Result "$label key access" 'WARN' 'Private key file not found (hardware module?); check access with the vendor tool.' }
        else {
            try { Add-Result "$label key access" ($(if (Test-AccountHasRead -Path $key -Account $GmsaAccount) { 'PASS' } else { 'FAIL' })) ($(if (Test-AccountHasRead -Path $key -Account $GmsaAccount) { "$GmsaAccount reads the key" } else { "Run governance\deploy\Grant-CertificateKeyAccess.ps1 -Subject '$subject' -Account '$GmsaAccount'." })) }
            catch { Add-Result "$label key access" 'WARN' 'Key ACL not readable from this session (run elevated).' }
        }
    }
}

# SQL Server -------------------------------------------------------------------------------------------
if ($SqlServer) {
    $sqlcmd = Get-Command sqlcmd -ErrorAction SilentlyContinue
    if (-not $sqlcmd) { Add-Result 'SQL Server' 'SKIP' 'sqlcmd is not installed on this server.' }
    else {
        # -N: encrypt; the certificate is validated unless -TrustServerCertificate (-C). In master: the governance
        # database may not exist yet.
        $trustArgs = [string[]]@()
        if ($TrustServerCertificate) { $trustArgs = [string[]]@('-C') }
        $db = $Database.Replace("'", "''")
        $out = & $sqlcmd.Source -S $SqlServer -d master -E -N @trustArgs -b -l 15 -h -1 -W -Q "SET NOCOUNT ON; SELECT CAST(SERVERPROPERTY('ProductMajorVersion') AS varchar(5)) + '|' + SUSER_SNAME() + '|' + CAST(encrypt_option AS varchar(10)) + '|' + CASE WHEN DB_ID(N'$db') IS NULL THEN 'no' ELSE 'yes' END FROM sys.dm_exec_connections WHERE session_id = @@SPID" 2>&1
        if ($LASTEXITCODE -eq 0) {
            $parts = ("$out".Trim()) -split '\|'
            $verdict = Get-SqlVersionVerdict -Major ([int] $parts[0])
            $exists = if ($parts.Count -gt 3 -and $parts[3] -eq 'yes') { "$Database exists" } else { "$Database will be created" }
            Add-Result 'SQL Server' $verdict.Status "$($verdict.Detail) Signed in as $($parts[1]); encrypted: $($parts[2]); $exists."
        }
        else { Add-Result 'SQL Server' 'FAIL' "Cannot connect to $SqlServer with Windows authentication and an encrypted connection: $("$out".Trim())" }
    }
}
else { Add-Result 'SQL Server' 'SKIP' 'Pass -SqlServer to check it.' }

# Outbound HTTPS through the proxy ---------------------------------------------------------------------
$endpoints = @('https://login.microsoftonline.com', 'https://graph.microsoft.com', 'https://api.powerplatform.com')
if ($ItsmUrl) { $endpoints += $ItsmUrl }
foreach ($url in $endpoints) {
    $splat = @{ Uri = $url; Method = 'Head'; TimeoutSec = 15; SkipHttpErrorCheck = $true; MaximumRedirection = 0; ErrorAction = 'Stop' }
    if ($ProxyUrl) { $splat['Proxy'] = $ProxyUrl; $splat['ProxyUseDefaultCredentials'] = $true }
    try {
        $resp = Invoke-WebRequest @splat
        # Any HTTP answer, even 401 or 404, proves the path through the proxy works.
        Add-Result "HTTPS $url" 'PASS' "HTTP $([int]$resp.StatusCode)$(if ($ProxyUrl) { ' via proxy' })"
    }
    catch { Add-Result "HTTPS $url" 'FAIL' $_.Exception.Message }
}
if (-not $ItsmUrl) { Add-Result 'HTTPS ITSM' 'SKIP' 'Pass -ItsmUrl to check it.' }

# Time synchronization ---------------------------------------------------------------------------------
if ($IsWindows) {
    $status = & w32tm.exe /query /status 2>&1
    $sourceLine = $status | Where-Object { $_ -match '^Source:' } | Select-Object -First 1
    $lastLine = $status | Where-Object { $_ -match '^Last Successful Sync Time:' } | Select-Object -First 1
    if ($LASTEXITCODE -ne 0 -or -not $sourceLine) { Add-Result 'Time sync' 'WARN' 'w32tm did not report a status (Windows Time service stopped?).' }
    elseif ($sourceLine -match 'Local CMOS Clock|Free-running') { Add-Result 'Time sync' 'FAIL' "$sourceLine. Sign-in tokens and SLA times need a synchronized clock." }
    else { Add-Result 'Time sync' 'PASS' "$($sourceLine.Trim()); $("$lastLine".Trim())" }
}

$results | Format-Table -AutoSize -Wrap | Out-String -Width 200 | Write-Information -InformationAction Continue
$failed = @($results | Where-Object Status -eq 'FAIL').Count
if ($failed -gt 0) {
    Write-Information "$failed check(s) failed. Fix them before installing." -InformationAction Continue
    exit 1
}
Write-Information 'All required checks passed.' -InformationAction Continue
