<#
.SYNOPSIS
    Creates the Teams queue channels and the Power Automate flow that posts the Workbench's Adaptive
    Cards to them (architecture X11), with the HTTP trigger restricted by OAuth to the Worker app.

.DESCRIPTION
    Supported route only: the flow is created through the Dataverse Web API (workflow table, category 5),
    as Microsoft documents in "Work with cloud flows using code". The unsupported api.flow.microsoft.com
    API is not used. Safe to rerun: everything is found by display name or logical name first.

    Steps (run in order; each can be rerun alone with -Steps):
      Team        Graph: the team "CGW Test - Governance queues" and its five channels (you are owner).
      Reference   Dataverse: a Teams connection reference in the Default solution.
                  MANUAL afterwards: attach your Teams connection to it in Power Automate (see CHANNEL-FLOW.md).
      Flow        Dataverse: the flow from channel-flow\flow-definition.json, trigger restricted to the
                  Worker service principal's object ID, turned on. Refuses until the reference has a connection.
      CoOwner     Dataverse: shares the flow with -CoOwnerUpn as a second owner (flows are linked to people).
      TriggerUrl  Asks you to paste the trigger's HTTP URL (copied from the flow in Edge) without echoing it,
                  and stores it only in config\appsettings.Local.json (git-ignored). The URL carries a
                  signature; it is never printed or written anywhere else.

    Every sign-in opens Microsoft Edge on this PC (setup\EdgeSignIn.ps1). No device code.

.EXAMPLE
    pwsh -NoProfile -File setup\Setup-ChannelFlow.ps1 -TenantDomain cloudsconnected.com -AdminUpn joseph.saad@cloudsconnected.com -Steps Team,Reference
    pwsh -NoProfile -File setup\Setup-ChannelFlow.ps1 -TenantDomain cloudsconnected.com -AdminUpn joseph.saad@cloudsconnected.com -Steps Flow,TriggerUrl
#>
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingWriteHost', '', Justification = 'Interactive setup script: colored progress for the person running it.')]
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSReviewUnusedParameter', '', Justification = 'Parameters are read inside the helper functions.')]
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $TenantDomain,
    [Parameter(Mandatory)] [string] $AdminUpn,
    [string] $EnvironmentUrl = '',
    [string] $EnvironmentName = '',
    [string] $CoOwnerUpn = '',
    [string[]] $Steps = @('Team', 'Reference', 'Flow', 'CoOwner', 'TriggerUrl'),
    [string] $Suffix = 'Test'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$known = 'Team', 'Reference', 'Flow', 'CoOwner', 'TriggerUrl'
$Steps = @($Steps | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
foreach ($s in $Steps) { if ($known -notcontains $s) { throw "Unknown step '$s'. Steps: $($known -join ', ')" } }

$repo = Split-Path -Parent $PSScriptRoot
$configPath = Join-Path $repo 'config\tenant.test.json'
$localSettingsPath = Join-Path $repo 'config\appsettings.Local.json'
$definitionPath = Join-Path $PSScriptRoot 'channel-flow\flow-definition.json'

$TeamName = "CGW $Suffix - Governance queues"
$FlowName = "CGW $Suffix - Post governance card to queue channel"
$ConnRefDisplayName = "CGW $Suffix - Teams queues"
$Queues = [ordered]@{
    INTAKE       = 'Intake'
    ARCHITECTURE = 'Architecture'
    OPERATIONS   = 'Operations'
    FINOPS       = 'FinOps'
    RISK         = 'Risk'
}

# First-party public clients that already hold the delegated permissions used here.
$GraphPowerShellClientId = '14d82eec-204b-4c2f-b7e8-296a70dab67e'
$AzurePowerShellClientId = '1950a258-227b-4e31-a9cf-717495945fc2'

. (Join-Path $PSScriptRoot 'EdgeSignIn.ps1')

function Write-Step([string] $text) { Write-Host "`n== $text" -ForegroundColor Cyan }
function Write-Done([string] $text) { Write-Host "   ok  $text" -ForegroundColor Green }
function Write-Note([string] $text) { Write-Host "   --  $text" -ForegroundColor Yellow }

function Read-JsonFile([string] $path) {
    if (Test-Path $path) { return (Get-Content $path -Raw | ConvertFrom-Json -AsHashtable) }
    return @{}
}
function Save-JsonFile([string] $path, [hashtable] $data) {
    New-Item -ItemType Directory -Force (Split-Path $path) | Out-Null
    $data | ConvertTo-Json -Depth 10 | Set-Content -Path $path -Encoding utf8
}

$cfg = Read-JsonFile $configPath
if (-not $cfg.ContainsKey('workerServicePrincipalId')) {
    throw "config\tenant.test.json has no workerServicePrincipalId. Run setup\Setup-TestTenant.ps1 first."
}
if (-not $cfg.ContainsKey('channelFlow')) { $cfg['channelFlow'] = @{} }
$cf = $cfg['channelFlow']

# ------------------------------------------------------------------------------------------------
# Graph helpers (delegated, Graph PowerShell client through Edge)
# ------------------------------------------------------------------------------------------------
$script:GraphHeaders = $null
function Connect-GraphInEdge {
    if ($script:GraphHeaders) { return }
    $scopes = 'Team.Create', 'Channel.Create', 'Channel.ReadBasic.All', 'Group.Read.All', 'User.Read.All' |
        ForEach-Object { "https://graph.microsoft.com/$_" }
    $tok = Get-EdgeToken -ClientId $GraphPowerShellClientId -Tenant $TenantDomain -Scopes $scopes -LoginHint $AdminUpn
    $script:GraphHeaders = @{ Authorization = "Bearer $($tok.AccessToken)" }
}
function Invoke-Graph {
    param([string] $Method = 'GET', [string] $Uri, $Body = $null)
    $full = if ($Uri -like 'https://*') { $Uri } else { "https://graph.microsoft.com/v1.0$Uri" }
    $p = @{ Method = $Method; Uri = $full; Headers = $script:GraphHeaders; ResponseHeadersVariable = 'rh'; StatusCodeVariable = 'sc' }
    if ($null -ne $Body) { $p['Body'] = ($Body | ConvertTo-Json -Depth 10); $p['ContentType'] = 'application/json' }
    $result = Invoke-RestMethod @p
    return [pscustomobject]@{ Body = $result; Headers = $rh; Status = $sc }
}

# ------------------------------------------------------------------------------------------------
# Dataverse helpers (delegated, Azure PowerShell client through Edge)
# ------------------------------------------------------------------------------------------------
$script:DvHeaders = $null
$script:DvBase = $null
function Resolve-EnvironmentUrl {
    if ($EnvironmentUrl) { return $EnvironmentUrl.TrimEnd('/') }
    if ($cf.ContainsKey('environmentUrl') -and $cf['environmentUrl']) { return $cf['environmentUrl'] }
    # Global Discovery Service lists the Dataverse environments you can reach.
    $tok = Get-EdgeToken -ClientId $AzurePowerShellClientId -Tenant $TenantDomain -Scopes @('https://globaldisco.crm.dynamics.com/.default') -LoginHint $AdminUpn
    $inst = (Invoke-RestMethod -Uri 'https://globaldisco.crm.dynamics.com/api/discovery/v2.0/Instances' -Headers @{ Authorization = "Bearer $($tok.AccessToken)" }).value
    if ($EnvironmentName) { $inst = @($inst | Where-Object { $_.FriendlyName -eq $EnvironmentName }) }
    $inst = @($inst)
    if ($inst.Count -eq 1) { return $inst[0].Url.TrimEnd('/') }
    $list = ($inst | ForEach-Object { "$($_.FriendlyName) ($($_.Url))" }) -join '; '
    throw "Choose the Dataverse environment with -EnvironmentUrl or -EnvironmentName. Found: $list"
}
function Connect-DataverseInEdge {
    if ($script:DvHeaders) { return }
    $script:DvBase = Resolve-EnvironmentUrl
    $tok = Get-EdgeToken -ClientId $AzurePowerShellClientId -Tenant $TenantDomain -Scopes @("$($script:DvBase)/.default") -LoginHint $AdminUpn
    $script:DvHeaders = @{
        Authorization      = "Bearer $($tok.AccessToken)"
        'OData-MaxVersion' = '4.0'
        'OData-Version'    = '4.0'
        Accept             = 'application/json'
    }
    $cf['environmentUrl'] = $script:DvBase
    Write-Done "Dataverse environment $($script:DvBase)"
}
function Invoke-Dataverse {
    param([string] $Method = 'GET', [string] $Path, $Body = $null, [hashtable] $ExtraHeaders = @{})
    $h = $script:DvHeaders.Clone()
    foreach ($k in $ExtraHeaders.Keys) { $h[$k] = $ExtraHeaders[$k] }
    $p = @{ Method = $Method; Uri = "$($script:DvBase)/api/data/v9.2/$Path"; Headers = $h; ResponseHeadersVariable = 'rh' }
    if ($null -ne $Body) { $p['Body'] = ($Body | ConvertTo-Json -Depth 20 -Compress); $p['ContentType'] = 'application/json; charset=utf-8' }
    $result = Invoke-RestMethod @p
    return [pscustomobject]@{ Body = $result; Headers = $rh }
}
function Get-IdFromEntityHeader($headers) {
    $loc = @($headers['OData-EntityId'])[0]
    if ($loc -match '\(([0-9a-fA-F-]{36})\)') { return $Matches[1] }
    throw 'Dataverse did not return the new record ID.'
}
function ConvertTo-ODataLiteral([string] $s) { return $s.Replace("'", "''") }

# ------------------------------------------------------------------------------------------------
# Team and channels
# ------------------------------------------------------------------------------------------------
if ($Steps -contains 'Team') {
    Write-Step "Team '$TeamName' and its five queue channels"
    Connect-GraphInEdge
    $filter = [uri]::EscapeDataString("displayName eq '$(ConvertTo-ODataLiteral $TeamName)' and resourceProvisioningOptions/Any(x:x eq 'Team')")
    $existing = @((Invoke-Graph -Uri "/groups?`$filter=$filter&`$select=id,displayName").Body.value)
    if ($existing.Count -gt 0) {
        $teamId = $existing[0].id
        Write-Done "team exists ($teamId)"
    }
    else {
        $me = (Invoke-Graph -Uri "/users/$([uri]::EscapeDataString($AdminUpn))?`$select=id").Body
        $create = Invoke-Graph -Method POST -Uri '/teams' -Body @{
            'template@odata.bind' = "https://graph.microsoft.com/v1.0/teamsTemplates('standard')"
            displayName           = $TeamName
            description           = 'Copilot Governance Workbench queues: the Workbench worker posts cards here through a Power Automate flow. Decisions happen in the Workbench, never here.'
            visibility            = 'Private'
            members               = @(@{
                    '@odata.type'     = '#microsoft.graph.aadUserConversationMember'
                    roles             = @('owner')
                    'user@odata.bind' = "https://graph.microsoft.com/v1.0/users('$($me.id)')"
                })
        }
        # 202 Accepted: Content-Location is /teams('{id}'); provisioning takes a little while.
        $loc = @($create.Headers['Content-Location'])[0]
        if ($loc -notmatch "([0-9a-fA-F-]{36})") { throw 'Team creation did not return the team ID.' }
        $teamId = $Matches[1]
        $opLoc = @($create.Headers['Location'])[0]
        for ($i = 0; $i -lt 30; $i++) {
            [System.Threading.Thread]::Sleep(5000)
            try {
                $op = (Invoke-Graph -Uri "https://graph.microsoft.com/v1.0$opLoc").Body
                if ($op.status -eq 'succeeded') { break }
                if ($op.status -eq 'failed') { throw "Team creation failed: $($op.error.message)" }
            }
            catch { if ($i -ge 29) { throw } }
        }
        Write-Done "team created ($teamId)"
    }
    $cf['teamId'] = $teamId

    $channels = @((Invoke-Graph -Uri "/teams/$teamId/channels?`$select=id,displayName").Body.value)
    $map = [ordered]@{}
    foreach ($q in $Queues.Keys) {
        $name = $Queues[$q]
        $ch = $channels | Where-Object { $_.displayName -eq $name } | Select-Object -First 1
        if (-not $ch) {
            $ch = (Invoke-Graph -Method POST -Uri "/teams/$teamId/channels" -Body @{
                    displayName    = $name
                    description    = "Workbench $name queue: new tasks and findings for this team."
                    membershipType = 'standard'
                }).Body
            Write-Done "channel '$name' created"
        }
        $map[$q] = $ch.id
    }
    $cf['channels'] = $map
    Write-Done "five queue channels ready"
    Save-JsonFile $configPath $cfg
}

# ------------------------------------------------------------------------------------------------
# Connection reference (Teams)
# ------------------------------------------------------------------------------------------------
function Get-ConnectionReference {
    $f = [uri]::EscapeDataString("connectionreferencedisplayname eq '$(ConvertTo-ODataLiteral $ConnRefDisplayName)'")
    return @((Invoke-Dataverse -Path "connectionreferences?`$filter=$f&`$select=connectionreferenceid,connectionreferencelogicalname,connectionid,connectorid").Body.value) | Select-Object -First 1
}

if ($Steps -contains 'Reference') {
    Write-Step "Teams connection reference '$ConnRefDisplayName'"
    Connect-DataverseInEdge
    $ref = Get-ConnectionReference
    if (-not $ref) {
        # Logical names carry the publisher prefix of the Default solution.
        $sol = (Invoke-Dataverse -Path "solutions?`$filter=uniquename eq 'Default'&`$select=solutionid&`$expand=publisherid(`$select=customizationprefix)").Body.value[0]
        $prefix = $sol.publisherid.customizationprefix
        $logical = "${prefix}_cgw$($Suffix.ToLowerInvariant())teamsqueues"
        $created = Invoke-Dataverse -Method POST -Path 'connectionreferences' -Body @{
            connectionreferencedisplayname = $ConnRefDisplayName
            connectionreferencelogicalname = $logical
            connectorid                    = '/providers/Microsoft.PowerApps/apis/shared_teams'
            description                    = 'Teams connection the Workbench queue flow posts with (Flow bot). Owned by the Platform Owner.'
        }
        $cf['connectionReferenceId'] = Get-IdFromEntityHeader $created.Headers
        $cf['connectionReferenceLogicalName'] = $logical
        Write-Done "connection reference created ($logical)"
    }
    else {
        $cf['connectionReferenceId'] = $ref.connectionreferenceid
        $cf['connectionReferenceLogicalName'] = $ref.connectionreferencelogicalname
        Write-Done "connection reference exists ($($ref.connectionreferencelogicalname))"
    }
    Save-JsonFile $configPath $cfg
    $ref = Get-ConnectionReference
    if (-not $ref.connectionid) {
        Write-Note 'MANUAL: attach a Microsoft Teams connection to this connection reference in Power Automate (CHANNEL-FLOW.md, step 2), then run -Steps Flow.'
        Open-InEdge 'https://make.powerautomate.com/solutions'
    }
    else { Write-Done 'connection reference already has a Teams connection' }
}

# ------------------------------------------------------------------------------------------------
# Flow
# ------------------------------------------------------------------------------------------------
if ($Steps -contains 'Flow') {
    Write-Step "Flow '$FlowName'"
    Connect-DataverseInEdge
    foreach ($k in 'teamId', 'channels', 'connectionReferenceLogicalName') {
        if (-not $cf.ContainsKey($k)) { throw "channelFlow.$k is missing: run -Steps Team,Reference first." }
    }
    $ref = Get-ConnectionReference
    if (-not $ref -or -not $ref.connectionid) {
        throw 'The Teams connection reference has no connection yet. Do CHANNEL-FLOW.md step 2, then rerun -Steps Flow.'
    }

    # Fill the template and check it is still valid JSON before it goes anywhere.
    $template = Get-Content $definitionPath -Raw
    $channelMapJson = ($cf['channels'] | ConvertTo-Json -Compress)
    $filled = $template.
        Replace('"__CHANNEL_MAP__"', $channelMapJson).
        Replace('__TEAM_ID__', [string]$cf['teamId']).
        Replace('__ALLOWED_OBJECT_ID__', [string]$cfg['workerServicePrincipalId']).
        Replace('__CONNREF_LOGICAL_NAME__', [string]$cf['connectionReferenceLogicalName'])
    if ($filled -match '__[A-Z_]+__') { throw "Template placeholder left unfilled: $($Matches[0])" }
    $clientdata = ($filled | ConvertFrom-Json -AsHashtable | ConvertTo-Json -Depth 50 -Compress)

    $f = [uri]::EscapeDataString("name eq '$(ConvertTo-ODataLiteral $FlowName)' and category eq 5")
    $flow = @((Invoke-Dataverse -Path "workflows?`$filter=$f&`$select=workflowid,statecode").Body.value) | Select-Object -First 1
    if (-not $flow) {
        $created = Invoke-Dataverse -Method POST -Path 'workflows' -Body @{
            category      = 5
            name          = $FlowName
            type          = 1
            primaryentity = 'none'
            description   = 'Posts Copilot Governance Workbench cards to the queue channels. HTTP trigger restricted by OAuth to the Workbench Worker service principal. Keep at least two co-owners.'
            clientdata    = $clientdata
        }
        $flowId = Get-IdFromEntityHeader $created.Headers
        Write-Done "flow created ($flowId)"
    }
    else {
        $flowId = $flow.workflowid
        if ($flow.statecode -eq 1) {
            Invoke-Dataverse -Method PATCH -Path "workflows($flowId)" -Body @{ statecode = 0 } -ExtraHeaders @{ 'If-Match' = '*' } | Out-Null
        }
        Invoke-Dataverse -Method PATCH -Path "workflows($flowId)" -Body @{ clientdata = $clientdata } -ExtraHeaders @{ 'If-Match' = '*' } | Out-Null
        Write-Done "flow updated from the template ($flowId)"
    }
    Invoke-Dataverse -Method PATCH -Path "workflows($flowId)" -Body @{ statecode = 1 } -ExtraHeaders @{ 'If-Match' = '*' } | Out-Null
    Write-Done 'flow turned on'
    $cf['workflowId'] = $flowId
    $cf['allowedCallerObjectId'] = $cfg['workerServicePrincipalId']
    $cf['tokenAudience'] = 'https://service.flow.microsoft.com/'
    Save-JsonFile $configPath $cfg
    Write-Note "MANUAL: open the flow in Edge and check 'Who can trigger the flow' (CHANNEL-FLOW.md, step 4), then copy its HTTP URL for -Steps TriggerUrl."
    Open-InEdge 'https://make.powerautomate.com/solutions'
}

# ------------------------------------------------------------------------------------------------
# Second co-owner (flows are linked to people and can become orphaned)
# ------------------------------------------------------------------------------------------------
if ($Steps -contains 'CoOwner') {
    Write-Step 'Co-owners'
    if (-not $CoOwnerUpn) {
        Write-Note 'No -CoOwnerUpn given: add a second co-owner before production (CHANNEL-FLOW.md, step 5). You are the first owner.'
    }
    else {
        Connect-DataverseInEdge
        if (-not $cf.ContainsKey('workflowId')) { throw 'Create the flow first (-Steps Flow).' }
        $f = [uri]::EscapeDataString("domainname eq '$(ConvertTo-ODataLiteral $CoOwnerUpn)'")
        $user = @((Invoke-Dataverse -Path "systemusers?`$filter=$f&`$select=systemuserid").Body.value) | Select-Object -First 1
        if (-not $user) { throw "$CoOwnerUpn is not a user in this Dataverse environment yet." }
        Invoke-Dataverse -Method POST -Path 'GrantAccess' -Body @{
            Target          = @{ workflowid = $cf['workflowId']; '@odata.type' = 'Microsoft.Dynamics.CRM.workflow' }
            PrincipalAccess = @{
                Principal  = @{ systemuserid = $user.systemuserid; '@odata.type' = 'Microsoft.Dynamics.CRM.systemuser' }
                AccessMask = 'ReadAccess, WriteAccess, AppendAccess, AppendToAccess, AssignAccess, ShareAccess, DeleteAccess'
            }
        } | Out-Null
        $cf['coOwners'] = @($AdminUpn, $CoOwnerUpn)
        Save-JsonFile $configPath $cfg
        Write-Done "flow shared with $CoOwnerUpn as co-owner"
    }
}

# ------------------------------------------------------------------------------------------------
# Trigger URL: pasted, never echoed, stored only in the git-ignored local settings
# ------------------------------------------------------------------------------------------------
if ($Steps -contains 'TriggerUrl') {
    Write-Step 'Trigger URL'
    Write-Host '   Paste the HTTP URL from the flow''s trigger (it is not shown as you paste), then press Enter.' -ForegroundColor Yellow
    $secure = Read-Host -AsSecureString 'HTTP URL'
    $url = [System.Net.NetworkCredential]::new('', $secure).Password.Trim()
    $u = $null
    if (-not [uri]::TryCreate($url, [UriKind]::Absolute, [ref]$u) -or $u.Scheme -ne 'https') { throw 'That is not an https URL.' }
    $settings = Read-JsonFile $localSettingsPath
    if (-not $settings.ContainsKey('Notifications')) { $settings['Notifications'] = @{} }
    $settings['Notifications']['ChannelFlowUrl'] = $url
    $settings['Notifications']['ChannelFlowAudience'] = 'https://service.flow.microsoft.com/'
    Save-JsonFile $localSettingsPath $settings
    # Only the non-secret parts go to the shared settings file.
    $cf['triggerHost'] = $u.Host
    $cf['triggerPath'] = $u.AbsolutePath
    $cf['triggerUrlStoredIn'] = 'config\appsettings.Local.json (Notifications:ChannelFlowUrl)'
    Save-JsonFile $configPath $cfg
    Write-Done "trigger URL stored (host $($u.Host)); its signature was not printed"
}

Write-Step 'Still manual (see setup\CHANNEL-FLOW.md)'
Write-Host '   - Attach a Microsoft Teams connection to the connection reference (once).'
Write-Host "   - Check the trigger's 'Who can trigger the flow' = Specific users in my tenant, allowed = the Worker's object ID."
Write-Host '   - Copy the trigger HTTP URL and run -Steps TriggerUrl.'
if (-not ($cf.ContainsKey('coOwners'))) { Write-Host '   - Add a second co-owner (-CoOwnerUpn, or Share in Power Automate).' }
