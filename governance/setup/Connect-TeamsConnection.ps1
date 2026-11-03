<#
.SYNOPSIS
    Attaches your existing Microsoft Teams connection to the channel flow's connection reference, through the
    Power Apps and Dataverse APIs (no clicks in Power Automate). Run after Setup-ChannelFlow.ps1 -Steps Team,Reference.

.DESCRIPTION
    Finds a Microsoft Teams connection in the flow's environment that is Connected and was created by the admin,
    and sets it on the connection reference recorded in config\tenant.test.json (channelFlow). If there is none,
    it says so and opens the connections page in Edge: a new connection needs the person's own consent.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $TenantDomain,
    [Parameter(Mandatory)] [string] $AdminUpn
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'EdgeSignIn.ps1')

function Write-Done([string] $text) { Write-Host "   ok  $text" -ForegroundColor Green }
function Write-Note([string] $text) { Write-Host "   --  $text" -ForegroundColor Yellow }

$AzurePowerShellClientId = '1950a258-227b-4e31-a9cf-717495945fc2'
$configPath = Join-Path (Split-Path -Parent $PSScriptRoot) 'config\tenant.test.json'
$cfg = Get-Content $configPath -Raw | ConvertFrom-Json -AsHashtable
$cf = $cfg['channelFlow']
if (-not $cf -or -not $cf['connectionReferenceId']) { throw 'Run Setup-ChannelFlow.ps1 -Steps Team,Reference first.' }
$envUrl = $cf['environmentUrl'].TrimEnd('/')

Import-Module Microsoft.Graph.Authentication
$pa = Get-EdgeToken -ClientId $AzurePowerShellClientId -Tenant $TenantDomain -Scopes @('https://service.powerapps.com//.default') -LoginHint $AdminUpn
$paH = @{ Authorization = "Bearer $($pa.AccessToken)" }

# The environment's name (GUID-like ID) from its Dataverse URL.
$envs = (Invoke-RestMethod -Headers $paH -Uri 'https://api.bap.microsoft.com/providers/Microsoft.BusinessAppPlatform/environments?api-version=2020-10-01').value
$env = $envs | Where-Object { $_.properties.linkedEnvironmentMetadata.instanceUrl -and $_.properties.linkedEnvironmentMetadata.instanceUrl.TrimEnd('/') -eq $envUrl } | Select-Object -First 1
if (-not $env) { throw "No Power Platform environment has the Dataverse URL $envUrl." }
Write-Done "environment $($env.properties.displayName) ($($env.name))"

$filter = [uri]::EscapeDataString("environment eq '$($env.name)'")
$conns = (Invoke-RestMethod -Headers $paH -Uri "https://api.powerapps.com/providers/Microsoft.PowerApps/apis/shared_teams/connections?api-version=2016-11-01&`$filter=$filter").value
$mine = @($conns | Where-Object {
        ($_.properties.statuses | Where-Object { $_.status -eq 'Connected' }) -and
        ($_.properties.createdBy.userPrincipalName -eq $AdminUpn -or $_.properties.createdBy.email -eq $AdminUpn)
    })
Write-Note "$(@($conns).Count) Teams connection(s) in the environment, $($mine.Count) connected and yours"
if ($mine.Count -eq 0) {
    Write-Note 'No connected Teams connection of yours here. A new one needs your consent: Edge opens the Connections page; choose + New connection > Microsoft Teams, sign in, then rerun this script.'
    Start-Process 'msedge.exe' -ArgumentList @("https://make.powerautomate.com/environments/$($env.name)/connections")
    exit 2
}
$conn = $mine | Sort-Object { $_.properties.createdTime } -Descending | Select-Object -First 1

$dv = Get-EdgeToken -ClientId $AzurePowerShellClientId -Tenant $TenantDomain -Scopes @("$envUrl/.default") -LoginHint $AdminUpn
$dvH = @{ Authorization = "Bearer $($dv.AccessToken)"; 'OData-MaxVersion' = '4.0'; 'OData-Version' = '4.0'; Accept = 'application/json' }
$body = @{ connectionid = $conn.name } | ConvertTo-Json
Invoke-RestMethod -Method Patch -Headers $dvH -ContentType 'application/json' -Body $body -Uri "$envUrl/api/data/v9.2/connectionreferences($($cf['connectionReferenceId']))" | Out-Null
$check = Invoke-RestMethod -Headers $dvH -Uri "$envUrl/api/data/v9.2/connectionreferences($($cf['connectionReferenceId']))?`$select=connectionid,connectionreferencedisplayname"
if ($check.connectionid -ne $conn.name) { throw 'The connection reference did not keep the connection.' }
Write-Done "Teams connection '$($conn.properties.displayName)' attached to '$($check.connectionreferencedisplayname)'"
