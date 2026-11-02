<#
.SYNOPSIS
    Lets an account (the web or worker gMSA) read the private key of a certificate in LocalMachine\My.

.DESCRIPTION
    Finds the newest valid certificate with the subject (the same choice Microsoft.Identity.Web makes when it
    selects by distinguished name), locates its private key file (CNG or legacy CSP) and adds Read for the
    account. Run it again after every certificate rotation: a new certificate has a new key file.

.EXAMPLE
    pwsh -File deploy\Grant-CertificateKeyAccess.ps1 -Subject 'CN=CopilotGovernanceWeb' -Account 'CORP\gmsa-cgw-web$' -WhatIf
#>
[CmdletBinding(SupportsShouldProcess, DefaultParameterSetName = 'Subject')]
param(
    [Parameter(Mandatory, ParameterSetName = 'Subject')] [string] $Subject,
    [Parameter(Mandatory, ParameterSetName = 'Thumbprint')] [string] $Thumbprint,
    [Parameter(Mandatory)] [string] $Account,
    [ValidateSet('LocalMachine', 'CurrentUser')] [string] $StoreLocation = 'LocalMachine'
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'WorkbenchDeploy.Common.ps1')

if (-not $IsWindows) { throw 'This script runs on Windows only.' }
if ($StoreLocation -eq 'LocalMachine' -and -not (Test-IsAdministrator) -and -not $WhatIfPreference) {
    throw 'Run this from an elevated PowerShell 7 session: changing a machine key ACL needs administrator rights.'
}

$cert = if ($PSCmdlet.ParameterSetName -eq 'Thumbprint') {
    Get-Item -LiteralPath "Cert:\$StoreLocation\My\$($Thumbprint -replace '\s', '')" -ErrorAction SilentlyContinue
}
else {
    Get-WorkbenchCertificate -Subject $Subject -StoreLocation $StoreLocation
}
if ($null -eq $cert) { throw "No valid certificate with a private key was found in Cert:\$StoreLocation\My for '$Subject$Thumbprint'." }
if (-not $cert.HasPrivateKey) { throw "Certificate $($cert.Thumbprint) has no private key on this machine." }

$keyFile = Get-CertificateKeyFile -Certificate $cert -StoreLocation $StoreLocation
if ($null -eq $keyFile) { throw "The private key file of certificate $($cert.Thumbprint) was not found. It may be held in a hardware module; grant access with the vendor's tool." }

Write-DeployStep "Certificate $($cert.Subject) ($($cert.Thumbprint)), expires $($cert.NotAfter.ToString('yyyy-MM-dd'))"
Write-DeployNote "private key file: $keyFile"
Grant-KeyFileRead -KeyFile $keyFile -Account $Account
