<#
.SYNOPSIS
    Carries out the agent actions an administrator approved on the dashboard: quarantine a Copilot Studio agent,
    release it from quarantine, or reassign an orphaned agent to a new owner, through the Power Platform API.
    Optional, and off until dash.Setting AgentActionsEnabled is 1.

.DESCRIPTION
    The dashboard proposes actions on agents into dash.AgentAction (Governance > Agent policies) and an
    administrator approves or rejects each one. Run nightly after the proposals are made, this tool:

      1. Stops at once, with a note and no call to Microsoft 365, unless dash.Setting AgentActionsEnabled is 1.
      2. Takes the rows with Status Approved and ActionKind Quarantine, Release or Reassign, oldest decision first,
         at most -MaxActions of them. Notify is the dashboard's own (an email to the owner): never touched here.
         Any other kind is refused. There is no deletion of an agent, ever: the tool has no code for one.
      3. Signs in with its own app registration (-TenantId, -ClientId, -CertificateThumbprint: a certificate in
         LocalMachine\My or CurrentUser\My) to the Power Platform API, and to Microsoft Graph when a Reassign
         names its new owner by sign-in (the API takes the owner's Entra object id).
      4. Finds each agent's environment id from its environment's name (dash.AgentAction.Environment, as the
         agent workbook names it); an id is used as it is. The environments are listed once a run.
      5. Calls the documented endpoint, and writes back on the row: Status Done or Failed, DoneUtc, and Result
         (what the call answered, short; never a token). A row no longer Approved when its turn comes (rejected
         meanwhile, or done by another run) is left alone. A failed row is not tried again: approve it again on
         the dashboard to retry.

    One failure does not stop the others; the run ends Partial. Throttling (429) is waited out as the API asks
    (Retry-After), up to -MaxRetries times a call. Each run is one row in audit.ExternalUsageRun, Source
    AgentActions, which Admin > Health shows.

    -Check reads, for the agents quarantined or released in the last -CheckDays days, whether the agent is
    still as it was left (the latest such action of each agent), and sets CheckedUtc. A mismatch (an agent
    released by hand in Copilot Studio, say) is written to the row's Result and reported; nothing is changed
    in Microsoft 365. -Check runs the check only, not the actions.

    -WhatIf lists what would be done, makes no call and changes nothing. -CheckOnly signs in and tries each
    read once, writing nothing.

    THE APP'S ACCESS. A separate app registration from the pipeline's, so the right to change agents is held
    apart from the right to read the audit log. Under API permissions, Power Platform API:
    CopilotStudio.AdminActions.Invoke, admin consent granted; for Reassign by sign-in, Microsoft Graph
    User.Read.All (application). The service principal must also be allowed to call the Power Platform API as
    an administrator (see the Power Platform API authentication page).

    Exit codes: 0 done (or nothing to do, or turned off, or -WhatIf); 1 failed (sign-in, the database, every
    action failed, or -CheckOnly found a part the tool needs cannot be read); 2 partial (some actions failed,
    or -Check found an agent not as it was left).

    Endpoints (Power Platform API, https://api.powerplatform.com, api-version -ApiVersion):
      GET  /copilotstudio/environments/{env}/bots/{bot}/api/botQuarantine
      POST /copilotstudio/environments/{env}/bots/{bot}/api/botQuarantine/SetAsQuarantined
      POST /copilotstudio/environments/{env}/bots/{bot}/api/botQuarantine/SetAsUnquarantined
      POST /copilotstudio/environments/{env}/bots/{bot}/api/botAdminOperations/reassign  {"NewOwnerAadUserId": "..."}
      GET  /environmentmanagement/environments
    https://learn.microsoft.com/microsoft-copilot-studio/admin-api-quarantine
    https://learn.microsoft.com/microsoft-copilot-studio/admin-api-reassign-ownership-orphaned-agent
    https://learn.microsoft.com/rest/api/power-platform/copilotstudio/bots

    With no -SqlServer or -ConnectionString, SqlServer, Database and TrustServerCertificate come from
    copilot-audit.settings.psd1, and the sign-in from AgentActionsTenantId (else TenantId), AgentActionsClientId
    and AgentActionsCertificateThumbprint. The pipeline's own ClientId is never used: this tool signs in as its
    own app or not at all.

.PARAMETER MaxActions
    At most this many actions a run (default 50): a safety cap. The rest wait for the next run.

.PARAMETER Check
    Check the agents quarantined or released in the last -CheckDays days instead of carrying out actions.

.PARAMETER CheckDays
    How many days back -Check looks (default 7).

.PARAMETER CheckOnly
    Sign in and try each read once, writing nothing: what the app may do, and what is missing.

.EXAMPLE
    .\tools\Invoke-AgentActions.ps1 -TenantId <tenant> -ClientId <app-id> -CertificateThumbprint <thumbprint> -SqlServer SQLPROD01
.EXAMPLE
    .\tools\Invoke-AgentActions.ps1 -WhatIf          # what tonight's run would do, from the settings file
.EXAMPLE
    .\tools\Invoke-AgentActions.ps1 -Check           # are the agents still as they were left
#>
#Requires -Version 7.2
[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Medium')]
param(
    [string] $TenantId,
    [string] $ClientId,
    [string] $CertificateThumbprint,
    [string] $SqlServer,
    [string] $Database = 'CopilotAudit',
    [switch] $TrustServerCertificate,
    [string] $ConnectionString,
    [ValidateRange(1, 500)][int] $MaxActions = 50,
    [switch] $Check,
    [ValidateRange(1, 90)][int] $CheckDays = 7,
    [switch] $CheckOnly,
    [string] $SettingsPath = (Join-Path (Split-Path $PSScriptRoot -Parent) 'copilot-audit.settings.psd1'),
    # Tokens already in hand, in place of signing in (the tests); and the endpoints, for a sovereign cloud or a mock.
    [securestring] $AccessToken,
    [securestring] $GraphAccessToken,
    [string] $ApiEndpoint = 'https://api.powerplatform.com',
    [string] $GraphEndpoint = 'https://graph.microsoft.com',
    [string] $LoginEndpoint = 'https://login.microsoftonline.com',
    [string] $ApiVersion = '2024-10-01',
    [ValidateRange(0, 10)][int] $MaxRetries = 3
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ExternalUsage.Common.ps1')
$script:Given = @{} + $PSBoundParameters   # read here: in Invoke-Main, $PSBoundParameters is its own

# The only kinds this tool carries out. Anything else is refused, deletion above all.
$script:Kinds = 'Quarantine', 'Release', 'Reassign'
$script:MaxWaitSeconds = 120

function Write-Line([string] $Text) { Write-Information ("{0:u} {1}" -f [datetime]::UtcNow, $Text) -InformationAction Continue }

function ConvertTo-Base64Url([byte[]] $Bytes) { [Convert]::ToBase64String($Bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_') }

function New-ClientAssertion {
    <#
        The signed statement an app signs in to Entra with in place of a secret: a JWT signed with the
        certificate's private key (PS256), naming the certificate by the SHA-256 thumbprint of its DER encoding
        (x5t#S256), good for ten minutes. As tools\Import-FoundryUsage.ps1 signs in.
    #>
    param([System.Security.Cryptography.X509Certificates.X509Certificate2] $Certificate, [string] $Tenant, [string] $Client, [string] $Login,
          [datetime] $Now = [datetime]::UtcNow)
    $rsa = [System.Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($Certificate)
    if ($null -eq $rsa) { throw "The certificate $($Certificate.Thumbprint) has no RSA private key this account can use." }
    $epoch = [datetime]::new(1970, 1, 1, 0, 0, 0, [System.DateTimeKind]::Utc)
    $nbf = [long] ($Now.ToUniversalTime() - $epoch).TotalSeconds
    $thumb = ConvertTo-Base64Url ([System.Security.Cryptography.SHA256]::HashData($Certificate.RawData))
    $header = [ordered]@{ alg = 'PS256'; typ = 'JWT'; 'x5t#S256' = $thumb } | ConvertTo-Json -Compress
    $claims = [ordered]@{ aud = "$($Login.TrimEnd('/'))/$Tenant/oauth2/v2.0/token"; iss = $Client; sub = $Client; jti = [guid]::NewGuid().ToString()
                          nbf = $nbf; iat = $nbf; exp = $nbf + 600 } | ConvertTo-Json -Compress
    $unsigned = (ConvertTo-Base64Url ([System.Text.Encoding]::UTF8.GetBytes($header))) + '.' + (ConvertTo-Base64Url ([System.Text.Encoding]::UTF8.GetBytes($claims)))
    $signature = $rsa.SignData([System.Text.Encoding]::ASCII.GetBytes($unsigned), [System.Security.Cryptography.HashAlgorithmName]::SHA256, [System.Security.Cryptography.RSASignaturePadding]::Pss)
    $unsigned + '.' + (ConvertTo-Base64Url $signature)
}

function Get-AppToken {
    <# An app-only token for -Resource (its .default scope), by the certificate in LocalMachine\My or CurrentUser\My. #>
    param([string] $Tenant, [string] $Client, [string] $Thumbprint, [string] $Login, [string] $Resource)
    $clean = ("$Thumbprint" -replace '[^0-9A-Fa-f]', '').ToUpperInvariant()
    $cert = $null
    foreach ($store in 'Cert:\LocalMachine\My', 'Cert:\CurrentUser\My') { $cert = Get-Item -LiteralPath "$store\$clean" -ErrorAction SilentlyContinue; if ($cert) { break } }
    if (-not $cert) { throw "No certificate $clean in LocalMachine\My or CurrentUser\My." }
    try {
        $r = Invoke-RestMethod -Method Post -Uri "$($Login.TrimEnd('/'))/$Tenant/oauth2/v2.0/token" -ContentType 'application/x-www-form-urlencoded' -TimeoutSec 60 -Body @{
            client_id = $Client; scope = "$($Resource.TrimEnd('/'))/.default"; grant_type = 'client_credentials'
            client_assertion_type = 'urn:ietf:params:oauth:client-assertion-type:jwt-bearer'
            client_assertion = (New-ClientAssertion -Certificate $cert -Tenant $Tenant -Client $Client -Login $Login)
        }
    }
    catch { throw "Signing in to $Resource as $Client failed: $(Get-ShortText $_.Exception.Message 300)" }
    $r.access_token
}

function Get-TokenRole {
    <# The application permissions (roles) a token carries, read from its claims; nothing of the token is shown. #>
    param([string] $Token)
    $parts = "$Token".Split('.')
    if ($parts.Count -lt 2) { return @() }
    try {
        $p = $parts[1].Replace('-', '+').Replace('_', '/'); $p += '=' * ((4 - $p.Length % 4) % 4)
        $claims = [System.Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($p)) | ConvertFrom-Json
        $roles = if ($claims.PSObject.Properties['roles']) { @($claims.roles) } else { @() }
        $roles | ForEach-Object { "$_" }
    }
    catch { @() }
}

function Get-ShortText {
    <# A message cut to -Max characters on one line, with anything that looks like a bearer token taken out. #>
    param([string] $Text, [int] $Max = 400)
    $t = ("$Text" -replace '\s+', ' ').Trim()
    $t = $t -replace '(?i)bearer\s+[A-Za-z0-9\-_\.=]+', 'Bearer ***' -replace 'eyJ[A-Za-z0-9\-_]{10,}\.[A-Za-z0-9\-_]+\.[A-Za-z0-9\-_]*', '***'
    if ($t.Length -gt $Max) { $t = $t.Substring(0, $Max - 3) + '...' }
    $t
}

function Get-RetryAfterSecond {
    <# How long Retry-After asks to wait: seconds, or an HTTP date; 10 when it says neither; at most -Cap. Pure, for the tests. #>
    param([string] $Value, [int] $Cap = 120, [datetime] $Now = [datetime]::UtcNow)
    $n = 0
    if ([int]::TryParse("$Value".Trim(), [ref] $n)) { return [Math]::Min([Math]::Max($n, 1), $Cap) }
    $when = [datetimeoffset]::MinValue
    if ([datetimeoffset]::TryParse("$Value", [cultureinfo]::InvariantCulture, [System.Globalization.DateTimeStyles]::AssumeUniversal, [ref] $when)) {
        return [int] [Math]::Min([Math]::Max([Math]::Ceiling(($when.UtcDateTime - $Now.ToUniversalTime()).TotalSeconds), 1), $Cap)
    }
    [Math]::Min(10, $Cap)
}

function Test-EnvironmentId {
    <# Whether a value is already an environment id (a GUID, or Default-<tenant GUID>) rather than a name. #>
    param([string] $Value)
    $v = "$Value".Trim()
    $v -match '^(?i)(default-)?[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$'
}

function Resolve-EnvironmentId {
    <#
        The environment id for an agent's environment as the workbook names it: an id as it is; else the one
        environment whose display name (or domain name) it is, ignoring case. None, or more than one, is refused
        by name. Pure, for the tests: -Environment is the list the API gave.
    #>
    param([string] $Name, [AllowEmptyCollection()][object[]] $Environment)
    $n = "$Name".Trim()
    if (-not $n) { throw 'The action names no environment.' }
    if (Test-EnvironmentId $n) { return $n }
    $hit = @($Environment | Where-Object { $_ -and ("$($_.displayName)" -ieq $n -or ($_.PSObject.Properties['domainName'] -and "$($_.domainName)" -ieq $n)) })
    if ($hit.Count -eq 0) { throw "No environment named '$n' among the $(@($Environment).Count) this app can see." }
    if ($hit.Count -gt 1) { throw "$($hit.Count) environments are named '$n'; put the environment id in the action instead." }
    "$($hit[0].id)"
}

function Get-AgentActionRequest {
    <#
        The call for one action: method, address and body. Quarantine, Release or Reassign only; any other kind
        (Notify, which is the dashboard's, or anything that would delete) is refused here, before any call. Pure,
        for the tests.
    #>
    param([string] $Kind, [string] $Api, [string] $EnvironmentId, [string] $AgentId, [string] $Version, [string] $NewOwnerId)
    if ($Kind -notin $script:Kinds) { throw "Action kind '$Kind' is not one this tool carries out (only $($script:Kinds -join ', '))." }
    $g = [guid]::Empty
    if (-not [guid]::TryParse("$AgentId", [ref] $g)) { throw "The agent id '$AgentId' is not a Copilot Studio agent id." }
    $bot = "$($Api.TrimEnd('/'))/copilotstudio/environments/$([uri]::EscapeDataString($EnvironmentId))/bots/$($g.ToString())/api"
    $v = "api-version=$([uri]::EscapeDataString($Version))"
    switch ($Kind) {
        'Quarantine' { [pscustomobject]@{ Method = 'Post'; Uri = "$bot/botQuarantine/SetAsQuarantined?$v"; Body = $null } }
        'Release'    { [pscustomobject]@{ Method = 'Post'; Uri = "$bot/botQuarantine/SetAsUnquarantined?$v"; Body = $null } }
        'Reassign'   {
            if (-not [guid]::TryParse("$NewOwnerId", [ref] [guid]::Empty)) { throw "The new owner's Entra object id '$NewOwnerId' is not a GUID." }
            [pscustomobject]@{ Method = 'Post'; Uri = "$bot/botAdminOperations/reassign?$v"; Body = [ordered]@{ NewOwnerAadUserId = "$NewOwnerId" } }
        }
    }
}

function Get-QuarantineStatusUri {
    param([string] $Api, [string] $EnvironmentId, [string] $AgentId, [string] $Version)
    "$($Api.TrimEnd('/'))/copilotstudio/environments/$([uri]::EscapeDataString($EnvironmentId))/bots/$([uri]::EscapeDataString($AgentId))/api/botQuarantine?api-version=$([uri]::EscapeDataString($Version))"
}

function Invoke-Api {
    <#
        One call with the token. Throttling (429) is waited out as the service asks (Retry-After), up to
        -MaxRetries times; any other failure throws with the status and the service's own message, short.
        Returns @{ Status; Body } (Body parsed when it is JSON).
    #>
    param([string] $Uri, [string] $BearerToken, [string] $Method = 'Get', $Body)
    for ($try = 0; ; $try++) {
        $p = @{ Uri = $Uri; Method = $Method; Headers = @{ Authorization = "Bearer $BearerToken"; Accept = 'application/json' }; SkipHttpErrorCheck = $true; TimeoutSec = 120 }
        if ($null -ne $Body) { $p.Body = ($Body | ConvertTo-Json -Depth 10 -Compress); $p.ContentType = 'application/json' }
        elseif ($Method -ne 'Get') { $p.Body = ''; $p.ContentType = 'application/json' }
        $r = Invoke-WebRequest @p
        $status = [int] $r.StatusCode
        if ($status -eq 429 -and $try -lt $MaxRetries) {
            $h = if ($r.Headers.ContainsKey('Retry-After')) { "$(@($r.Headers['Retry-After'])[0])" } else { '' }
            $wait = Get-RetryAfterSecond -Value $h -Cap $script:MaxWaitSeconds
            Write-Line "Throttled (429): waiting $wait s, as asked, then trying again ($($try + 1) of $MaxRetries)."
            Start-Sleep -Seconds $wait; continue
        }
        $parsed = $null
        $text = if ($r.Content -is [byte[]]) { [System.Text.Encoding]::UTF8.GetString($r.Content) } else { "$($r.Content)" }
        if ($status -ne 204 -and $text) { try { $parsed = $text | ConvertFrom-Json -Depth 32 } catch { $parsed = $null } }
        if ($status -ge 400) {
            $message = ''
            if ($parsed -and $parsed.PSObject.Properties['error'] -and $parsed.error) {
                $e = $parsed.error
                $message = if ($e -is [string]) { $e } else { "$(if ($e.PSObject.Properties['code']) { $e.code }): $(if ($e.PSObject.Properties['message']) { $e.message })" }
            }
            elseif ($parsed -and $parsed.PSObject.Properties['message']) { $message = "$($parsed.message)" }
            elseif ($text) { $message = $text }
            throw (Get-ShortText "HTTP $status for $(([uri] $Uri).AbsolutePath)$(if ($message) { ": $message" })" 400)
        }
        return [pscustomobject]@{ Status = $status; Body = $parsed }
    }
}

function Get-EnvironmentList {
    <# Every environment the app can see, a page at a time (value[] and @odata.nextLink). #>
    param([string] $Api, [string] $BearerToken, [string] $Version)
    $all = [System.Collections.Generic.List[object]]::new()
    $next = "$($Api.TrimEnd('/'))/environmentmanagement/environments?api-version=$([uri]::EscapeDataString($Version))"
    for ($page = 0; $next -and $page -lt 100; $page++) {
        $b = (Invoke-Api -Uri $next -BearerToken $BearerToken).Body
        if ($null -eq $b) { break }
        foreach ($v in @($b.value)) { if ($v) { $all.Add($v) } }
        $next = $null
        foreach ($k in '@odata.nextLink', '@odata.nextlink', 'nextLink') { if ($b.PSObject.Properties[$k] -and $b.$k) { $next = "$($b.$k)"; break } }
    }
    $all.ToArray()
}

function Get-SqlRow {
    <# The rows of a query, as objects. #>
    param([System.Data.SqlClient.SqlConnection] $Connection, [string] $Sql, [hashtable] $Parameters = @{})
    $cmd = $Connection.CreateCommand()
    $cmd.CommandText = $Sql; $cmd.CommandTimeout = 120
    foreach ($k in $Parameters.Keys) { [void] $cmd.Parameters.AddWithValue($k, $(if ($null -eq $Parameters[$k]) { [DBNull]::Value } else { $Parameters[$k] })) }
    $t = [System.Data.DataTable]::new()
    $reader = $cmd.ExecuteReader()
    try { $t.Load($reader) } finally { $reader.Dispose() }
    foreach ($row in $t.Rows) {
        $o = [ordered]@{}
        foreach ($c in $t.Columns) { $v = $row[$c.ColumnName]; $o[$c.ColumnName] = if ($v -is [DBNull]) { $null } else { $v } }
        [pscustomobject]$o
    }
}

function Get-ActionRow {
    <# The approved actions this tool carries out, oldest decision first, at most -Max. #>
    param([System.Data.SqlClient.SqlConnection] $Connection, [int] $Max)
    Get-SqlRow $Connection @'
SELECT TOP (@max) ActionId, InventoryAgentId, AgentName, Environment, ActionKind, NewOwnerUpn
FROM dash.AgentAction
WHERE Status = 'Approved' AND ActionKind IN ('Quarantine', 'Release', 'Reassign') AND DoneUtc IS NULL
ORDER BY ISNULL(DecidedUtc, ProposedUtc), ActionId;
'@ @{ '@max' = $Max }
}

function Get-CheckRow {
    <# Each agent's latest quarantine or release done in the last -Days days, at most -Max. #>
    param([System.Data.SqlClient.SqlConnection] $Connection, [int] $Days, [int] $Max)
    Get-SqlRow $Connection @'
WITH done AS (
    SELECT ActionId, InventoryAgentId, AgentName, Environment, ActionKind, Result, DoneUtc,
           ROW_NUMBER() OVER (PARTITION BY InventoryAgentId ORDER BY DoneUtc DESC, ActionId DESC) AS n
    FROM dash.AgentAction
    WHERE Status = 'Done' AND ActionKind IN ('Quarantine', 'Release') AND DoneUtc >= DATEADD(DAY, -@days, SYSUTCDATETIME()))
SELECT TOP (@max) ActionId, InventoryAgentId, AgentName, Environment, ActionKind, Result, DoneUtc FROM done WHERE n = 1 ORDER BY DoneUtc, ActionId;
'@ @{ '@days' = $Days; '@max' = $Max }
}

function Set-ActionResult {
    <# Writes one action's outcome, only while it is still Approved: a row decided again meanwhile is not overwritten. #>
    param([System.Data.SqlClient.SqlConnection] $Connection, [int] $ActionId, [ValidateSet('Done', 'Failed')][string] $Status, [string] $Result)
    [int] (Invoke-UsageSql $Connection @'
UPDATE dash.AgentAction SET Status = @s, DoneUtc = SYSUTCDATETIME(), Result = LEFT(@r, 2000) WHERE ActionId = @id AND Status = 'Approved';
SELECT @@ROWCOUNT;
'@ @{ '@s' = $Status; '@r' = (Get-ShortText $Result 2000); '@id' = $ActionId })
}

function Get-CheckedResult {
    <# A row's Result with the latest check's mismatch on it, an earlier check's note replaced. Pure, for the tests. #>
    param([string] $Result, [string] $Mismatch, [datetime] $Now = [datetime]::UtcNow)
    $base = ("$Result" -replace '\s*\| Check [^|]*$', '').Trim()
    if (-not $Mismatch) { return $base }
    $note = "| Check $($Now.ToString('yyyy-MM-dd HH:mm'))Z: $Mismatch"
    $keep = [Math]::Max(0, 2000 - $note.Length - 1)
    if ($base.Length -gt $keep) { $base = $base.Substring(0, $keep) }
    ("$base $note").Trim()
}

function Start-ActionRun {
    <# The run's row in audit.ExternalUsageRun, from its start, so a failure shows too. As the usage loaders write it. #>
    param([System.Data.SqlClient.SqlConnection] $Connection, [string] $Scope)
    [int] (Invoke-UsageSql $Connection @'
INSERT INTO audit.ExternalUsageRun (Source, Scope, FromDay, ToDay, Host) VALUES ('AgentActions', LEFT(@scope, 400), CAST(SYSUTCDATETIME() AS DATE), CAST(SYSUTCDATETIME() AS DATE), @h);
SELECT CAST(SCOPE_IDENTITY() AS INT);
'@ @{ '@scope' = $Scope; '@h' = [Environment]::MachineName })
}

function Test-ActionSchema {
    param([System.Data.SqlClient.SqlConnection] $Connection)
    [int] (Invoke-UsageSql $Connection @'
SELECT CASE WHEN OBJECT_ID(N'dash.AgentAction', N'U') IS NOT NULL AND OBJECT_ID(N'dash.Setting', N'U') IS NOT NULL
             AND OBJECT_ID(N'audit.ExternalUsageRun', N'U') IS NOT NULL THEN 1 ELSE 0 END;
'@) -eq 1
}

function Get-ActionSetting {
    param([System.Data.SqlClient.SqlConnection] $Connection)
    "$(Invoke-UsageSql $Connection "SELECT LTRIM(RTRIM(Value)) FROM dash.Setting WHERE Name = 'AgentActionsEnabled';")"
}

function Confirm-AgentStep {
    <# -WhatIf and -Confirm for one step: whether to go ahead. Takes the script's -WhatIf and -Confirm. #>
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Medium')]
    param([string] $Target, [string] $Action)
    $PSCmdlet.ShouldProcess($Target, $Action)
}

function Get-OwnerId {
    <# The new owner's Entra object id: the value itself when it is one, else looked up in Microsoft Graph by sign-in. #>
    param([string] $Upn, [scriptblock] $GraphToken, [hashtable] $Cache)
    $u = "$Upn".Trim()
    if (-not $u) { throw 'The Reassign action names no new owner (NewOwnerUpn).' }
    if ([guid]::TryParse($u, [ref] [guid]::Empty)) { return $u }
    if ($Cache.ContainsKey($u.ToLowerInvariant())) { return $Cache[$u.ToLowerInvariant()] }
    $r = Invoke-Api -Uri "$($GraphEndpoint.TrimEnd('/'))/v1.0/users/$([uri]::EscapeDataString($u))?`$select=id,accountEnabled" -BearerToken (& $GraphToken)
    if (-not $r.Body -or -not $r.Body.PSObject.Properties['id'] -or -not $r.Body.id) { throw "Microsoft Graph gave no object id for $u." }
    if ($r.Body.PSObject.Properties['accountEnabled'] -and $r.Body.accountEnabled -eq $false) { throw "The new owner $u has a disabled account." }
    $Cache[$u.ToLowerInvariant()] = "$($r.Body.id)"
    "$($r.Body.id)"
}

function Invoke-Main {
    $setting = @{}
    if (Test-Path -LiteralPath $SettingsPath) { $setting = Import-PowerShellDataFile -LiteralPath $SettingsPath }
    function Setting($k) { if ($setting.Contains($k) -and $setting[$k]) { $setting[$k] } }
    if (-not $TenantId) { $script:TenantId = if (Setting 'AgentActionsTenantId') { Setting 'AgentActionsTenantId' } else { Setting 'TenantId' } }
    if (-not $ClientId) { $script:ClientId = Setting 'AgentActionsClientId' }
    if (-not $CertificateThumbprint) { $script:CertificateThumbprint = Setting 'AgentActionsCertificateThumbprint' }
    if (-not $ConnectionString) {
        if (-not $SqlServer) { $script:SqlServer = Setting 'SqlServer' }
        if (-not $script:Given.ContainsKey('Database') -and (Setting 'Database')) { $script:Database = Setting 'Database' }
        if (Setting 'TrustServerCertificate') { $script:TrustServerCertificate = $true }
    }
    $api = $ApiEndpoint.TrimEnd('/')

    # Tokens are asked for once, when first needed, and never written anywhere.
    $tokens = @{}
    $ppToken = {
        if (-not $tokens.ContainsKey('pp')) {
            $tokens['pp'] = if ($AccessToken) { [System.Net.NetworkCredential]::new('', $AccessToken).Password }
                            else {
                                if (-not ($TenantId -and $ClientId -and $CertificateThumbprint)) { throw 'Signing in needs -TenantId, -ClientId and -CertificateThumbprint (or AgentActionsClientId and AgentActionsCertificateThumbprint in the settings file): this tool''s own app, not the pipeline''s.' }
                                Get-AppToken -Tenant $TenantId -Client $ClientId -Thumbprint $CertificateThumbprint -Login $LoginEndpoint -Resource $ApiEndpoint
                            }
        }
        $tokens['pp']
    }
    $graphToken = {
        if (-not $tokens.ContainsKey('graph')) {
            $tokens['graph'] = if ($GraphAccessToken) { [System.Net.NetworkCredential]::new('', $GraphAccessToken).Password }
                               else {
                                   if (-not ($TenantId -and $ClientId -and $CertificateThumbprint)) { throw 'Signing in to Microsoft Graph needs -TenantId, -ClientId and -CertificateThumbprint.' }
                                   Get-AppToken -Tenant $TenantId -Client $ClientId -Thumbprint $CertificateThumbprint -Login $LoginEndpoint -Resource $GraphEndpoint
                               }
        }
        $tokens['graph']
    }

    # ---- -CheckOnly: sign in and try each read once, writing nothing ----
    if ($CheckOnly) {
        $lines = [System.Collections.Generic.List[object]]::new()
        function Add-Line([bool] $Ok, [bool] $Needed, [string] $Text) { $lines.Add([pscustomobject]@{ Ok = $Ok; Needed = $Needed; Text = $Text }) }
        $pending = @()
        if ($ConnectionString -or $SqlServer) {
            try {
                $c = New-UsageConnection -SqlServer $SqlServer -Database $Database -TrustServerCertificate:$TrustServerCertificate -ConnectionString $ConnectionString
                try {
                    if (Test-ActionSchema $c) {
                        $on = Get-ActionSetting $c
                        $pending = @(Get-ActionRow $c $MaxActions)
                        Add-Line $true $false "Database $($c.Database): AgentActionsEnabled is '$on'$(if ($on -ne '1') { ' (actions are off)' }); $($pending.Count) approved action(s) waiting."
                    }
                    else { Add-Line $false $true "Database $($c.Database): dash.AgentAction, dash.Setting or audit.ExternalUsageRun is missing; run Install-CopilotAudit.ps1 from this release." }
                }
                finally { $c.Dispose() }
            }
            catch { Add-Line $false $true "Database: $(Get-ShortText $_.Exception.Message 300)" }
        }
        try {
            $t = & $ppToken
            $roles = @(Get-TokenRole $t)
            Add-Line $true $true "Signed in to the Power Platform API$(if ($ClientId) { " as $ClientId" })."
            if ($roles.Count) {
                $has = $roles -contains 'CopilotStudio.AdminActions.Invoke'
                Add-Line $has $true "Permission CopilotStudio.AdminActions.Invoke $(if ($has) { 'granted' } else { 'not in the token (granted: ' + ($roles -join ', ') + ')' })."
            }
            else { Add-Line $false $false 'The token names no application permission; CopilotStudio.AdminActions.Invoke could not be confirmed from it.' }
            try { $envs = @(Get-EnvironmentList -Api $api -BearerToken $t -Version $ApiVersion); Add-Line $true $true "Environments: $($envs.Count) visible." }
            catch { Add-Line $false $true "Environments: $(Get-ShortText $_.Exception.Message 300)"; $envs = @() }
            $probe = @($pending | Select-Object -First 1)
            if ($probe.Count) {
                try {
                    $envId = Resolve-EnvironmentId -Name $probe[0].Environment -Environment $envs
                    $s = (Invoke-Api -Uri (Get-QuarantineStatusUri $api $envId $probe[0].InventoryAgentId $ApiVersion) -BearerToken $t).Body
                    Add-Line $true $true "Quarantine status of agent $($probe[0].InventoryAgentId) readable (isBotQuarantined=$(if ($s -and $s.PSObject.Properties['isBotQuarantined']) { $s.isBotQuarantined }))."
                }
                catch { Add-Line $false $true "Quarantine status of agent $($probe[0].InventoryAgentId): $(Get-ShortText $_.Exception.Message 300)" }
            }
        }
        catch { Add-Line $false $true (Get-ShortText $_.Exception.Message 300) }
        $byUpn = @($pending | Where-Object { $_.ActionKind -eq 'Reassign' -and $_.NewOwnerUpn -and -not [guid]::TryParse("$($_.NewOwnerUpn)", [ref] [guid]::Empty) })
        try {
            [void] (Invoke-Api -Uri "$($GraphEndpoint.TrimEnd('/'))/v1.0/users?`$top=1&`$select=id" -BearerToken (& $graphToken))
            Add-Line $true $false 'Microsoft Graph: users readable (a Reassign by sign-in finds the new owner).'
        }
        catch { Add-Line $false ($byUpn.Count -gt 0) "Microsoft Graph users (User.Read.All, for a Reassign by sign-in): $(Get-ShortText $_.Exception.Message 300)" }
        foreach ($l in $lines) { Write-Line ("{0,-7} {1}" -f $(if ($l.Ok) { 'OK' } elseif ($l.Needed) { 'MISSING' } else { 'NOTE' }), $l.Text) }
        $missing = @($lines | Where-Object { -not $_.Ok -and $_.Needed }).Count
        Write-Line $(if ($missing) { "$missing part(s) the tool needs cannot be read: grant what is named above. Nothing was changed." } else { 'Every part the tool needs can be read. Nothing was changed.' })
        if ($missing) { exit 1 }
        return
    }

    if (-not $ConnectionString -and -not $SqlServer) { throw 'Give -SqlServer, or SqlServer in the settings file.' }
    $conn = New-UsageConnection -SqlServer $SqlServer -Database $Database -TrustServerCertificate:$TrustServerCertificate -ConnectionString $ConnectionString
    try {
        if (-not (Test-ActionSchema $conn)) { throw "dash.AgentAction, dash.Setting or audit.ExternalUsageRun is not in $($conn.Database) yet: run Install-CopilotAudit.ps1 from this release first." }
        $on = Get-ActionSetting $conn
        if ($on -ne '1') {
            Write-Line "Agent actions are off (dash.Setting AgentActionsEnabled is '$on', not 1): nothing done, nothing called."
            return
        }

        # ---- -Check: are the agents still as they were left ----
        if ($Check) {
            $rows = @(Get-CheckRow $conn $CheckDays $MaxActions)
            if ($WhatIfPreference) {
                foreach ($r in $rows) { [void] (Confirm-AgentStep -Target "agent $($r.InventoryAgentId) ($($r.AgentName)) in $($r.Environment)" -Action "Read its quarantine status (expected $(if ($r.ActionKind -eq 'Quarantine') { 'quarantined' } else { 'not quarantined' }))") }
                Write-Line "What if: $($rows.Count) agent(s) would be checked. Nothing called, nothing changed."
                return
            }
            $runId = Start-ActionRun $conn "check: last $CheckDays day(s)"
            $ok = 0; $mismatch = [System.Collections.Generic.List[string]]::new(); $unread = [System.Collections.Generic.List[string]]::new()
            try {
                $t = & $ppToken
                $envs = $null
                foreach ($r in $rows) {
                    try {
                        $envId = if (Test-EnvironmentId $r.Environment) { "$($r.Environment)".Trim() } else { if ($null -eq $envs) { $envs = @(Get-EnvironmentList -Api $api -BearerToken $t -Version $ApiVersion) }; Resolve-EnvironmentId -Name $r.Environment -Environment $envs }
                        $s = (Invoke-Api -Uri (Get-QuarantineStatusUri $api $envId $r.InventoryAgentId $ApiVersion) -BearerToken $t).Body
                        if (-not $s -or -not $s.PSObject.Properties['isBotQuarantined']) { throw 'the answer has no isBotQuarantined' }
                        $want = $r.ActionKind -eq 'Quarantine'
                        $is = [bool] $s.isBotQuarantined
                        $note = if ($is -ne $want) { "expected $(if ($want) { 'quarantined' } else { 'not quarantined' }), found $(if ($is) { 'quarantined' } else { 'not quarantined' })" } else { '' }
                        [void] (Invoke-UsageSql $conn 'UPDATE dash.AgentAction SET CheckedUtc = SYSUTCDATETIME(), Result = LEFT(@r, 2000) WHERE ActionId = @id;' @{ '@id' = [int] $r.ActionId; '@r' = (Get-CheckedResult $r.Result $note) })
                        if ($note) { $mismatch.Add("action $($r.ActionId) agent $($r.InventoryAgentId) ($($r.AgentName)): $note"); Write-Warning "Agent $($r.InventoryAgentId) ($($r.AgentName)): $note." }
                        else { $ok++ }
                    }
                    catch { $unread.Add("action $($r.ActionId): $(Get-ShortText $_.Exception.Message 200)"); Write-Warning "Action $($r.ActionId) not checked: $($_.Exception.Message)" }
                }
                $status = if ($mismatch.Count -eq 0 -and $unread.Count -eq 0) { 'Succeeded' } elseif ($ok + $mismatch.Count -gt 0) { 'Partial' } else { 'Failed' }
                $detail = (@("Checked $($ok + $mismatch.Count) of $($rows.Count) agent(s) acted on in the last $CheckDays day(s): $ok as left.",
                             $(if ($mismatch.Count) { "Not as left: $($mismatch -join '; ')." }), $(if ($unread.Count) { "Not read: $($unread -join '; ')." })) | Where-Object { $_ }) -join ' '
                Complete-UsageRun $conn -RunId $runId -Status $status -Rows ($ok + $mismatch.Count) -Detail $detail
                Write-Line "$status. $detail"
                if ($status -eq 'Failed') { exit 1 }
                if ($status -eq 'Partial') { exit 2 }
                return
            }
            catch { Complete-UsageRun $conn -RunId $runId -Status Failed -Rows ($ok + $mismatch.Count) -Detail (Get-ShortText $_.Exception.Message 2000); throw }
        }

        # ---- The approved actions ----
        $rows = @(Get-ActionRow $conn $MaxActions)
        $waiting = [int] (Invoke-UsageSql $conn "SELECT COUNT(*) FROM dash.AgentAction WHERE Status = 'Approved' AND ActionKind IN ('Quarantine', 'Release', 'Reassign') AND DoneUtc IS NULL;")
        $notify = [int] (Invoke-UsageSql $conn "SELECT COUNT(*) FROM dash.AgentAction WHERE Status = 'Approved' AND ActionKind NOT IN ('Quarantine', 'Release', 'Reassign');")
        $more = [Math]::Max(0, $waiting - $rows.Count)
        $extra = @($(if ($more) { "$more more wait for the next run (-MaxActions $MaxActions)." }), $(if ($notify) { "$notify approved Notify action(s) are the dashboard's: not touched." })) | Where-Object { $_ }
        if ($WhatIfPreference) {
            foreach ($r in $rows) {
                $what = switch ("$($r.ActionKind)") { 'Quarantine' { 'Quarantine' } 'Release' { 'Release from quarantine' } 'Reassign' { "Reassign to $($r.NewOwnerUpn)" } }
                [void] (Confirm-AgentStep -Target "agent $($r.InventoryAgentId) ($($r.AgentName)) in $($r.Environment), action $($r.ActionId)" -Action $what)
            }
            Write-Line ("What if: $($rows.Count) action(s) would be carried out. Nothing called, nothing changed. " + ($extra -join ' ')).Trim()
            return
        }
        $runId = Start-ActionRun $conn "$($rows.Count) approved action(s)"
        $done = 0; $failed = [System.Collections.Generic.List[string]]::new(); $skipped = 0
        try {
            $envs = $null; $owners = @{}
            foreach ($r in $rows) {
                $id = [int] $r.ActionId
                if ("$(Invoke-UsageSql $conn 'SELECT Status FROM dash.AgentAction WHERE ActionId = @id;' @{ '@id' = $id })" -ne 'Approved') { $skipped++; continue }   # decided again meanwhile
                $target = "agent $($r.InventoryAgentId) ($($r.AgentName)) in $($r.Environment), action $id"
                if (-not (Confirm-AgentStep -Target $target -Action "$($r.ActionKind)")) { $skipped++; continue }
                try {
                    $t = & $ppToken
                    $envId = if (Test-EnvironmentId $r.Environment) { "$($r.Environment)".Trim() } else { if ($null -eq $envs) { $envs = @(Get-EnvironmentList -Api $api -BearerToken $t -Version $ApiVersion) }; Resolve-EnvironmentId -Name $r.Environment -Environment $envs }
                    $owner = if ("$($r.ActionKind)" -eq 'Reassign') { Get-OwnerId -Upn $r.NewOwnerUpn -GraphToken $graphToken -Cache $owners } else { $null }
                    $req = Get-AgentActionRequest -Kind $r.ActionKind -Api $api -EnvironmentId $envId -AgentId $r.InventoryAgentId -Version $ApiVersion -NewOwnerId $owner
                    $answer = Invoke-Api -Uri $req.Uri -BearerToken $t -Method $req.Method -Body $req.Body
                    $b = $answer.Body
                    $q = if ($b -and $b.PSObject.Properties['isBotQuarantined']) { [bool] $b.isBotQuarantined } else { $null }
                    $when = if ($b -and $b.PSObject.Properties['lastUpdateTimeUtc']) { " at $($b.lastUpdateTimeUtc)" } else { '' }
                    switch ("$($r.ActionKind)") {
                        'Quarantine' { if ($q -eq $false) { throw "HTTP $($answer.Status), but the agent is not quarantined (isBotQuarantined=false)" }; $text = "Quarantined: HTTP $($answer.Status)$(if ($null -ne $q) { ", isBotQuarantined=$q" })$when." }
                        'Release'    { if ($q -eq $true) { throw "HTTP $($answer.Status), but the agent is still quarantined (isBotQuarantined=true)" }; $text = "Released: HTTP $($answer.Status)$(if ($null -ne $q) { ", isBotQuarantined=$q" })$when." }
                        'Reassign'   { $text = "Reassigned to $($r.NewOwnerUpn)$(if ("$owner" -ne "$($r.NewOwnerUpn)") { " ($owner)" }): HTTP $($answer.Status)." }
                    }
                    if (Set-ActionResult $conn $id 'Done' $text) { $done++; Write-Line "Action $id, $($r.ActionKind) of $($r.InventoryAgentId): $text" }
                    else { $skipped++ }
                }
                catch {
                    $why = Get-ShortText $_.Exception.Message 400
                    [void] (Set-ActionResult $conn $id 'Failed' "Failed: $why")
                    $failed.Add("action $id ($why)")
                    Write-Warning "Action $id, $($r.ActionKind) of $($r.InventoryAgentId), failed: $why"
                }
            }
            $status = if ($failed.Count -eq 0) { 'Succeeded' } elseif ($done -gt 0) { 'Partial' } else { 'Failed' }
            $detail = (@("Done $done of $($rows.Count) approved action(s); failed $($failed.Count); skipped $skipped.", $(if ($failed.Count) { "Failed: $($failed -join '; ')." })) + $extra | Where-Object { $_ }) -join ' '
            Complete-UsageRun $conn -RunId $runId -Status $status -Rows $done -Detail $detail
            Write-Line "$status. $detail"
            if ($status -eq 'Failed') { exit 1 }
            if ($status -eq 'Partial') { exit 2 }
        }
        catch { Complete-UsageRun $conn -RunId $runId -Status Failed -Rows $done -Detail (Get-ShortText $_.Exception.Message 2000); throw }
    }
    finally { $conn.Dispose() }
}

# Dot-sourcing (tests) loads the functions without running anything.
if ($MyInvocation.InvocationName -ne '.') { Invoke-Main }
