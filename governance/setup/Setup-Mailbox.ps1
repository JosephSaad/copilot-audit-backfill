<#
.SYNOPSIS
    Creates the shared notification mailbox and scopes the Worker's Application Mail.Send to it
    through Exchange RBAC for Applications. Called by Setup-TestTenant.ps1 in a process of its own.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $TenantDomain,
    [Parameter(Mandatory)] [string] $AdminUpn,
    [Parameter(Mandatory)] [string] $Mailbox,
    [Parameter(Mandatory)] [string] $ConfigPath,
    [string] $Suffix = 'Test'
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'EdgeSignIn.ps1')

function Write-Done([string] $text) { Write-Host "   ok  $text" -ForegroundColor Green }
function Write-Note([string] $text) { Write-Host "   --  $text" -ForegroundColor Yellow }

$cfg = Get-Content $ConfigPath -Raw | ConvertFrom-Json -AsHashtable
Import-Module ExchangeOnlineManagement
# Exchange Online PowerShell's own first-party client.
$tok = Get-EdgeToken -ClientId 'fb78d390-0c51-40cd-8e17-fdbfab77341b' -Tenant $TenantDomain -Scopes @('https://outlook.office365.com/.default') -LoginHint $AdminUpn
Connect-ExchangeOnline -AccessToken $tok.AccessToken -Organization $TenantDomain -UserPrincipalName $AdminUpn -ShowBanner:$false
try {
    $mbx = Get-EXOMailbox -Identity $Mailbox -ErrorAction SilentlyContinue
    if (-not $mbx) {
        $alias = $Mailbox.Split('@')[0]
        New-Mailbox -Shared -Name "Copilot Governance Workbench ($Suffix)" -Alias $alias -PrimarySmtpAddress $Mailbox | Out-Null
        Write-Done "shared mailbox $Mailbox created"
    }
    else { Write-Done "mailbox $Mailbox exists" }

    $exSp = Get-ServicePrincipal -Identity $cfg['workerClientId'] -ErrorAction SilentlyContinue
    if (-not $exSp) {
        New-ServicePrincipal -AppId $cfg['workerClientId'] -ObjectId $cfg['workerServicePrincipalId'] -DisplayName "Copilot Governance Workbench - Worker ($Suffix)" | Out-Null
        Write-Done 'Exchange service principal created'
    }
    $scopeName = "CGW $Suffix notification mailbox"
    if (-not (Get-ManagementScope -Identity $scopeName -ErrorAction SilentlyContinue)) {
        New-ManagementScope -Name $scopeName -RecipientRestrictionFilter "PrimarySmtpAddress -eq '$Mailbox'" | Out-Null
        Write-Done "management scope '$scopeName' created"
    }
    $raName = "CGW $Suffix Worker Mail.Send"
    if (-not (Get-ManagementRoleAssignment -Identity $raName -ErrorAction SilentlyContinue)) {
        New-ManagementRoleAssignment -Name $raName -App $cfg['workerServicePrincipalId'] -Role 'Application Mail.Send' -CustomResourceScope $scopeName | Out-Null
        Write-Done 'Application Mail.Send assigned, scoped to the mailbox only (30 minutes to 2 hours to apply)'
        $cfg['mailboxScopedAtUtc'] = (Get-Date).ToUniversalTime().ToString('o')
    }
    else { Write-Done 'Application Mail.Send already scoped to the mailbox' }
    $test = Test-ServicePrincipalAuthorization -Identity $cfg['workerServicePrincipalId'] -Resource $Mailbox -ErrorAction SilentlyContinue
    if ($test) { Write-Note ('authorization check now: ' + (($test | ForEach-Object { "$($_.RoleName) in scope=$($_.InScope)" }) -join ', ')) }
}
finally { Disconnect-ExchangeOnline -Confirm:$false | Out-Null }
$cfg | ConvertTo-Json -Depth 8 | Set-Content -Path $ConfigPath -Encoding utf8
