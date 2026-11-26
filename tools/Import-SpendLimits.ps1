<#
.SYNOPSIS
    Loads the spending limits set where AI spend happens, Microsoft 365 Copilot's spending policies (Microsoft
    Graph beta) and GitHub's budgets, into audit.SpendLimitSource. Read only: it never sets or changes a limit.

.DESCRIPTION
    Two sources, each read on its own and each replaced whole in audit.SpendLimitSource (its rows deleted and
    the new ones inserted, in one transaction), and only when it was read in full:

      1. Graph: the Copilot Credits spending policies, GET /beta/copilot/costManagement/spendingPolicies,
         every page (@odata.nextLink). A policy is for all users (allUsersSpendingPolicy) or for selected
         groups (selectedGroupsSpendingPolicy); a group policy's groups are read from its assignedGroups.
         Each policy is a row of Kind CopilotUser: its per-user limit (PerUser 1, Amount NULL when it has no
         per-user cap), ExternalId the policy's id. A policy whose period limit is capped has a second row,
         ExternalId "<id>/period", PerUser 0: the most its whole audience may use in a period. A period is a
         UTC calendar month (Period Monthly). Unit Credits. Raw is the policy exactly as Graph sent it.
         A policy type this load does not know is kept with its JSON and named in the run's detail; one that
         names an environment or an agent is a row of Kind CopilotResource.
      2. GitHub: the enterprise's budgets, GET /enterprises/{enterprise}/settings/billing/budgets, a page of
         100 at a time. Kind GitHubBudget, ScopeType from the budget's scope (Enterprise, Organization,
         Repository, CostCenter, User; the multi-user scopes are per person, PerUser 1), Scope the entity's
         name, Unit USD, Amount the budget, Period Monthly, ExternalId the budget's id.

    A source that refuses (403 or 404: a permission or role not granted) or answers in a shape this load cannot
    read is named in the run's detail and keeps its last load; the run ends Partial. A source that is not
    configured is skipped and named. The run is recorded in audit.ExternalUsageRun (Source SpendLimits).

    THE DELETE GUARD. When a source's new rows are more than -MaxDropPercent fewer than it holds (the
    SpendLimitMaxDropPercent setting, 15 by default), that source is not replaced, the run ends Partial and
    the exit code is 3, so the nightly run goes on. -AllowDrop lets a real drop through once.

    THE GRAPH APP. Signs in as an app registration with a certificate (by default the pipeline's own app:
    TenantId, ClientId and CertificateThumbprint in copilot-audit.settings.psd1). It needs the application
    permissions CopilotCostManagement-Policy.Read.All (the policies) and CopilotCostManagement-Assignment.Read.All
    (a group policy's groups), with administrator consent. These are Microsoft Graph beta permissions and may
    not show in the Entra permission picker yet.

    THE GITHUB TOKEN. As tools\Import-GitHubCopilotUsage.ps1 reads it: -Token, else the environment variable
    GITHUB_COPILOT_TOKEN, else the file -TokenPath (GitHubTokenPath in the settings file) that loader's -SaveToken
    wrote. It needs an enterprise owner or billing manager, with read access to enterprise billing (fine-grained:
    "Enterprise billing: read"). The token is sent to GitHub's API only and is never written out.

    Exit codes: 0 done (Succeeded or Partial); 1 Failed (no source could be read for a reason other than a
    refusal), or with -CheckOnly a configured source cannot be read; 3 the delete guard kept a source's last load.

.PARAMETER Source
    Which sources to read: Graph, GitHub, or both (the default).

.PARAMETER CheckOnly
    Sign in and read each configured source once, writing nothing (no database is needed). One line a source:
    OK with what it found, or what is missing.

.PARAMETER MaxDropPercent
    The delete guard's percentage, for each source on its own. 0 turns the guard off.

.PARAMETER AllowDrop
    Replace a source even when the delete guard would refuse, once.

.EXAMPLE
    .\tools\Import-SpendLimits.ps1 -CheckOnly
.EXAMPLE
    .\tools\Import-SpendLimits.ps1 -Source GitHub -Enterprise <enterprise> -SqlServer <server>
.EXAMPLE
    .\tools\Import-SpendLimits.ps1          # as the nightly run does, from the settings file
#>
#Requires -Version 7.2
[CmdletBinding()]
param(
    [ValidateSet('Graph', 'GitHub')][string[]] $Source = @('Graph', 'GitHub'),
    [string] $SqlServer,
    [string] $Database = 'CopilotAudit',
    [switch] $TrustServerCertificate,
    [string] $ConnectionString,
    [string] $SettingsPath = (Join-Path (Split-Path $PSScriptRoot -Parent) 'copilot-audit.settings.psd1'),
    # Microsoft Graph: the app and its certificate.
    [string] $TenantId,
    [string] $ClientId,
    [string] $CertificateThumbprint,
    # A Graph token already in hand, in place of signing in (the tests); and the endpoints, for a sovereign cloud or a mock.
    [securestring] $GraphAccessToken,
    [string] $GraphEndpoint = 'https://graph.microsoft.com',
    [string] $LoginEndpoint = 'https://login.microsoftonline.com',
    # GitHub, named as tools\Import-GitHubCopilotUsage.ps1 names them.
    [string] $Enterprise,
    [securestring] $Token,
    [string] $TokenPath,
    [string] $ApiBase = 'https://api.github.com',
    [string] $ApiVersion = '2026-03-10',
    [ValidateRange(0, 100)][double] $MaxDropPercent = 15,
    [switch] $AllowDrop,
    [switch] $CheckOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ExternalUsage.Common.ps1')
$script:MaxDropGiven = $PSBoundParameters.ContainsKey('MaxDropPercent')   # read here: in Invoke-Main, $PSBoundParameters is its own
$script:Given = @{} + $PSBoundParameters
# This loader's own setting for the delete guard's percentage.
if (-not $script:DropGuardSetting.Contains('Spend limits')) { $script:DropGuardSetting['Spend limits'] = 'SpendLimitMaxDropPercent' }

$script:PolicyPath = '/beta/copilot/costManagement/spendingPolicies'
$script:GraphPermissions = 'CopilotCostManagement-Policy.Read.All'
$script:GroupPermissions = 'CopilotCostManagement-Assignment.Read.All'

function Write-Line([string] $Text) { Write-Information ("{0:u} {1}" -f [datetime]::UtcNow, $Text) -InformationAction Continue }

function ConvertTo-Base64Url([byte[]] $Bytes) { [Convert]::ToBase64String($Bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_') }

function New-ClientAssertion {
    <# The signed statement the app signs in with in place of a secret (PS256, x5t#S256), good for ten minutes; as tools\Import-FoundryUsage.ps1 makes it. #>
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

function Get-GraphToken {
    <# An app-only token for Microsoft Graph, by the certificate in LocalMachine\My or CurrentUser\My. #>
    param([string] $Tenant, [string] $Client, [string] $Thumbprint, [string] $Login, [string] $Graph)
    $clean = ("$Thumbprint" -replace '[^0-9A-Fa-f]', '').ToUpperInvariant()
    $cert = $null
    foreach ($store in 'Cert:\LocalMachine\My', 'Cert:\CurrentUser\My') { $cert = Get-Item -LiteralPath "$store\$clean" -ErrorAction SilentlyContinue; if ($cert) { break } }
    if (-not $cert) { throw "No certificate $clean in LocalMachine\My or CurrentUser\My." }
    $r = Invoke-RestMethod -Method Post -Uri "$($Login.TrimEnd('/'))/$Tenant/oauth2/v2.0/token" -ContentType 'application/x-www-form-urlencoded' -TimeoutSec 60 -Body @{
        client_id = $Client; scope = "$($Graph.TrimEnd('/'))/.default"; grant_type = 'client_credentials'
        client_assertion_type = 'urn:ietf:params:oauth:client-assertion-type:jwt-bearer'
        client_assertion = (New-ClientAssertion -Certificate $cert -Tenant $Tenant -Client $Client -Login $Login)
    }
    $r.access_token
}

function Unprotect-GitHubToken {
    <# The token file tools\Import-GitHubCopilotUsage.ps1 -SaveToken wrote, read back (Windows data protection, machine scope). #>
    param([byte[]] $Bytes)
    Add-Type -AssemblyName System.Security
    [System.Text.Encoding]::UTF8.GetString([System.Security.Cryptography.ProtectedData]::Unprotect($Bytes, $null, [System.Security.Cryptography.DataProtectionScope]::LocalMachine))
}

function Get-Prop {
    <# A property of an object, or $null where it has none: the sources gain and lose fields. #>
    param($Object, [string] $Name)
    if ($null -eq $Object -or $Object -is [string] -or $Object -is [ValueType]) { return $null }
    $p = $Object.PSObject.Properties[$Name]
    if ($p) { $p.Value } else { $null }
}

function New-SourceError {
    <# An error that says the source refused (Refused: a permission or role) or answered in a shape this load cannot read (Shape). #>
    param([string] $Message, [ValidateSet('Refused', 'Shape')][string] $Kind)
    $ex = [System.InvalidOperationException]::new($Message)
    $ex.Data['SpendLimitNote'] = $Kind
    $ex
}

function Get-SourceErrorKind {
    <# Refused or Shape for an error New-SourceError made; $null for any other. #>
    param($ErrorRecord)
    $e = if ($ErrorRecord -is [System.Management.Automation.ErrorRecord]) { $ErrorRecord.Exception } else { $ErrorRecord }
    if ($e -and $e.Data -and $e.Data['SpendLimitNote']) { "$($e.Data['SpendLimitNote'])" } else { $null }
}

function Invoke-SpendHttp {
    <#
        One GET with the token. Returns the status, the body as text and the headers. Throttling (429, or
        GitHub's 403 with its rate-limit headers) is waited out, for as long as the service says up to five
        minutes, up to three times. 403 and 404 throw a refusal; any other failure throws with the service's
        own message. The token goes in the Authorization header only.
    #>
    param([string] $Uri, [string] $BearerToken, [hashtable] $Headers = @{}, [string] $Service)
    $h = @{ Authorization = "Bearer $BearerToken" } + $Headers
    for ($try = 1; ; $try++) {
        $r = Invoke-WebRequest -Uri $Uri -Headers $h -SkipHttpErrorCheck -TimeoutSec 120
        $status = [int] $r.StatusCode
        if ($status -in 403, 429 -and $try -le 3) {
            $wait = 0
            if ($r.Headers.ContainsKey('Retry-After')) { $n = 0; if ([int]::TryParse("$(@($r.Headers['Retry-After'])[0])", [ref] $n)) { $wait = $n } }
            elseif ($r.Headers.ContainsKey('X-RateLimit-Remaining') -and "$(@($r.Headers['X-RateLimit-Remaining'])[0])" -eq '0') { $wait = 60 }
            elseif ($status -eq 429) { $wait = 30 }
            if ($wait -gt 0 -and $wait -le 300) { Write-Line "$Service asked to wait $wait s (throttled)."; Start-Sleep -Seconds $wait; continue }
        }
        $text = if ($r.Content -is [byte[]]) { [System.Text.Encoding]::UTF8.GetString($r.Content) } else { [string] $r.Content }
        if ($status -ge 400) {
            $message = "HTTP $status"
            try {
                $b = $text | ConvertFrom-Json -Depth 64
                $err = Get-Prop $b 'error'
                if ($err -and (Get-Prop $err 'message')) { $message = "$(Get-Prop $err 'code'): $(Get-Prop $err 'message')" }
                elseif (Get-Prop $b 'message') { $message = "$(Get-Prop $b 'message')" }
            }
            catch { Write-Verbose "The error's body is not JSON." }
            $what = "$Service answered $status for $(([uri] $Uri).AbsolutePath): $message"
            if ($status -in 401, 403, 404) { throw (New-SourceError $what 'Refused') }
            throw $what
        }
        return [pscustomobject]@{ Status = $status; Content = $text; Headers = $r.Headers }
    }
}

function Split-JsonList {
    <#
        A page of a list as the raw JSON of each item, exactly as the service sent it, from the array named
        -Property; and the page's other top-level values (nextLink, has_next_page ...) as an object. A page
        that is not JSON, or has no such array, throws a shape error. Pure, for the tests.
    #>
    param([AllowEmptyString()][string] $Text, [string] $Property)
    $doc = $null
    try { $doc = [System.Text.Json.JsonDocument]::Parse($(if ($Text) { $Text } else { '' })) }
    catch { throw (New-SourceError "the answer is not JSON" 'Shape') }
    try {
        $root = $doc.RootElement
        $list = [System.Text.Json.JsonElement]::new()
        if ($root.ValueKind -ne [System.Text.Json.JsonValueKind]::Object -or -not $root.TryGetProperty($Property, [ref] $list) -or
            $list.ValueKind -ne [System.Text.Json.JsonValueKind]::Array) {
            throw (New-SourceError "the answer has no '$Property' list" 'Shape')
        }
        $items = [System.Collections.Generic.List[string]]::new()
        foreach ($el in $list.EnumerateArray()) { $items.Add($el.GetRawText()) }
        $meta = [ordered]@{}
        foreach ($p in $root.EnumerateObject()) {
            if ($p.Name -eq $Property) { continue }
            $meta[$p.Name] = switch ($p.Value.ValueKind) {
                'String' { $p.Value.GetString() } 'True' { $true } 'False' { $false } 'Number' { $p.Value.GetDouble() } default { $null }
            }
        }
        [pscustomobject]@{ Items = $items.ToArray(); Meta = [pscustomobject] $meta }
    }
    finally { $doc.Dispose() }
}

function ConvertFrom-CreditQuantity {
    <#
        A Copilot Credits quantity (costManagementQuantity) as its amount: limited (a value) or unlimited (no
        cap, Amount $null), with a note where its shape or unit is not one this load knows. Pure, for the tests.
    #>
    param($Quantity, [string] $What)
    if ($null -eq $Quantity) { return [pscustomobject]@{ Limited = $false; Amount = $null; Note = $null; Missing = $true } }
    $type = "$(Get-Prop $Quantity '@odata.type')"
    $value = Get-Prop $Quantity 'value'
    $unit = "$(Get-Prop $Quantity 'unit')"
    $note = $null
    if ($unit -and $unit -notmatch '(?i)credit') { $note = "$What is in '$unit', not Copilot Credits" }
    if ($type -match '(?i)unlimited') { return [pscustomobject]@{ Limited = $false; Amount = $null; Note = $note; Missing = $false } }
    $amount = $null
    if ($null -ne $value) {
        $d = 0.0
        if ($value -is [ValueType] -and -not ($value -is [bool])) { $amount = [decimal] $value }
        elseif ([double]::TryParse("$value", [System.Globalization.NumberStyles]::Float, [cultureinfo]::InvariantCulture, [ref] $d)) { $amount = [decimal] $d }
    }
    if ($null -eq $amount) {
        return [pscustomobject]@{ Limited = $false; Amount = $null; Missing = $false
                                  Note = $(if ($note) { $note } else { "$What has no amount this load can read ($(if ($type) { $type } else { 'no type' }))" }) }
    }
    [pscustomobject]@{ Limited = $true; Amount = $amount; Note = $note; Missing = $false }
}

function Limit-Text([string] $Text, [int] $Max) { if ($null -eq $Text -or $Text.Length -le $Max) { $Text } else { $Text.Substring(0, $Max - 6) + ' (cut)' } }

function Format-GroupScope {
    <# A group policy's groups as one Scope: "name (id)" each, joined by "; ". Pure, for the tests. #>
    param([object[]] $Groups)
    $parts = @($Groups | Where-Object { $_ } | ForEach-Object {
            $id = "$(Get-Prop $_ 'id')"; $name = "$(Get-Prop $_ 'displayName')"
            if ($name -and $id) { "$name ($id)" } elseif ($id) { $id } elseif ($name) { $name } })
    if ($parts.Count -eq 0) { return $null }
    Limit-Text ($parts -join '; ') 1000
}

function ConvertTo-SpendPolicyRow {
    <#
        A spending policy as rows of audit.SpendLimitSource, with notes for what could not be read. -Raw is the
        policy's JSON as Graph sent it; -Groups the groups of a selected-groups policy ($null when they were not
        read). Unknown fields stay in Raw; a missing one is NULL; a shape this load does not know is a note,
        never an error. Pure, for the tests.
    #>
    param([string] $Raw, [AllowNull()][object[]] $Groups)
    $notes = [System.Collections.Generic.List[string]]::new()
    $rows = [System.Collections.Generic.List[object]]::new()
    $p = $null
    try { $p = $Raw | ConvertFrom-Json -Depth 64 } catch { $p = $null }
    if ($null -eq $p -or $p -is [string] -or $p -is [ValueType] -or $p -is [array]) {
        $notes.Add('a policy that is not a JSON object was left out')
        return [pscustomobject]@{ Rows = @(); Notes = $notes.ToArray() }
    }
    $id = "$(Get-Prop $p 'id')"
    if (-not $id) { $notes.Add('a policy with no id was left out'); return [pscustomobject]@{ Rows = @(); Notes = $notes.ToArray() } }
    $name = Limit-Text "$(Get-Prop $p 'displayName')" 400
    $type = "$(Get-Prop $p '@odata.type')"
    $kind = 'CopilotUser'; $scopeType = $null; $scope = $null
    switch -Regex ($type) {
        '(?i)allUsersSpendingPolicy$' { $scopeType = 'AllUsers'; break }
        '(?i)selectedGroupsSpendingPolicy$' {
            $scopeType = 'Group'
            if ($null -ne $Groups) { $scope = Format-GroupScope $Groups }
            break
        }
        '(?i)environment' { $kind = 'CopilotResource'; $scopeType = 'Environment'; break }
        '(?i)agent' { $kind = 'CopilotResource'; $scopeType = 'Agent'; break }
        default { $notes.Add("policy $id is of a type this load does not know ($(if ($type) { $type } else { 'none given' })): kept with its JSON") }
    }
    if ($kind -eq 'CopilotResource') {
        $target = @('environmentId', 'environmentName', 'agentId', 'agentName', 'resourceId', 'resourceName' | ForEach-Object { Get-Prop $p $_ } | Where-Object { $_ } | Select-Object -First 1)
        $scope = if ($target.Count) { Limit-Text "$($target[0])" 1000 } else { $null }
        $notes.Add("policy $id is for $(if ($scopeType -eq 'Agent') { 'an agent' } else { 'an environment' }), a kind Graph's documentation does not describe yet: kept with its JSON")
    }
    $perUser = ConvertFrom-CreditQuantity (Get-Prop $p 'perUserLimit') "the per-user limit of policy $id"
    $period = ConvertFrom-CreditQuantity (Get-Prop $p 'periodLimit') "the period limit of policy $id"
    foreach ($q in $perUser, $period) { if ($q.Note) { $notes.Add($q.Note) } }
    if ($perUser.Missing -and $period.Missing) { $notes.Add("policy $id has neither a per-user nor a period limit this load can read: kept with its JSON") }
    $common = @{ Source = 'Graph'; Kind = $kind; Name = $(if ($name) { $name }); ScopeType = $scopeType; Scope = $scope; Unit = 'Credits'; Period = 'Monthly'; Raw = $Raw }
    $rows.Add([pscustomobject] (@{ ExternalId = Limit-Text $id 400; Amount = $perUser.Amount; PerUser = $true } + $common))
    if ($period.Limited) { $rows.Add([pscustomobject] (@{ ExternalId = Limit-Text "$id/period" 400; Amount = $period.Amount; PerUser = $false } + $common)) }
    [pscustomobject]@{ Rows = $rows.ToArray(); Notes = $notes.ToArray() }
}

# GitHub's budget scopes, as ScopeType, and whether the amount is each person's.
$script:BudgetScope = @{
    enterprise = @('Enterprise', $false); organization = @('Organization', $false); repository = @('Repository', $false)
    cost_center = @('CostCenter', $false); user = @('User', $false)
    multi_user_customer = @('Enterprise', $true); multi_user_cost_center = @('CostCenter', $true)
}

function ConvertTo-GitHubBudgetRow {
    <#
        A GitHub budget as a row of audit.SpendLimitSource, with notes. -Raw is its JSON as GitHub sent it;
        -Enterprise names the enterprise for a budget of the whole enterprise. Pure, for the tests.
    #>
    param([string] $Raw, [string] $Enterprise)
    $notes = [System.Collections.Generic.List[string]]::new()
    $b = $null
    try { $b = $Raw | ConvertFrom-Json -Depth 64 } catch { $b = $null }
    if ($null -eq $b -or $b -is [string] -or $b -is [ValueType] -or $b -is [array]) { return [pscustomobject]@{ Row = $null; Notes = @('a budget that is not a JSON object was left out') } }
    $id = "$(Get-Prop $b 'id')"
    if (-not $id) { return [pscustomobject]@{ Row = $null; Notes = @('a budget with no id was left out') } }
    $scopeName = "$(Get-Prop $b 'budget_scope')".ToLowerInvariant()
    $map = $script:BudgetScope[$scopeName]
    $scopeType = if ($map) { $map[0] } elseif ($scopeName) { Limit-Text $scopeName 24 } else { $null }
    $perUser = [bool] ($map -and $map[1])
    if (-not $map) { $notes.Add("budget $id has a scope this load does not know ($(if ($scopeName) { $scopeName } else { 'none given' }))") }
    $entity = "$(Get-Prop $b 'budget_entity_name')"
    $scope = if ($entity) { $entity } elseif ("$(Get-Prop $b 'user')") { "$(Get-Prop $b 'user')" } elseif ($scopeType -eq 'Enterprise') { $Enterprise } else { $null }
    $given = Get-Prop $b 'budget_amount'
    $amount = $null; $d = 0.0
    if ($null -ne $given -and [double]::TryParse("$given", [System.Globalization.NumberStyles]::Float, [cultureinfo]::InvariantCulture, [ref] $d)) { $amount = [decimal] $d }
    elseif ($null -ne $given) { $notes.Add("budget $id has an amount this load cannot read") }
    $sku = "$(Get-Prop $b 'budget_product_sku')"
    if (-not $sku) { $sku = "$(@(Get-Prop $b 'budget_product_skus') | Where-Object { $_ } | Select-Object -First 1)" }
    $type = "$(Get-Prop $b 'budget_type')"
    $name = if ($sku -and $type) { "$sku ($type)" } elseif ($sku) { $sku } elseif ($type) { $type } else { $null }
    $row = [pscustomobject]@{ Source = 'GitHub'; ExternalId = Limit-Text $id 400; Kind = 'GitHubBudget'; Name = Limit-Text $name 400; ScopeType = $scopeType
                              Scope = Limit-Text $scope 1000; Unit = 'USD'; Amount = $amount; Period = 'Monthly'; PerUser = $perUser; Raw = $Raw }
    [pscustomobject]@{ Row = $row; Notes = $notes.ToArray() }
}

function Read-GraphSpendLimit {
    <#
        Every spending policy, and each group policy's groups. Returns the rows and the notes; throws a refusal
        or a shape error when the policies themselves cannot be read (the source then keeps its last load).
        A group list that cannot be read is a note: that policy's Scope is NULL.
    #>
    param([string] $Graph, [string] $BearerToken)
    $rows = [System.Collections.Generic.List[object]]::new(); $notes = [System.Collections.Generic.List[string]]::new()
    $policies = 0; $groupsRefused = 0
    for ($next = "$Graph$script:PolicyPath"; $next; ) {
        $page = Split-JsonList (Invoke-SpendHttp -Uri $next -BearerToken $BearerToken -Service 'Microsoft Graph').Content 'value'
        foreach ($raw in $page.Items) {
            $policies++
            $groups = $null
            $p = try { $raw | ConvertFrom-Json -Depth 64 } catch { $null }
            if ("$(Get-Prop $p '@odata.type')" -match '(?i)selectedGroupsSpendingPolicy$' -and (Get-Prop $p 'id')) {
                try {
                    $list = [System.Collections.Generic.List[object]]::new()
                    $id = [uri]::EscapeDataString("$(Get-Prop $p 'id')")
                    for ($g = "$Graph$script:PolicyPath/$id/microsoft.graph.selectedGroupsSpendingPolicy/assignedGroups"; $g; ) {
                        $gp = Split-JsonList (Invoke-SpendHttp -Uri $g -BearerToken $BearerToken -Service 'Microsoft Graph').Content 'value'
                        foreach ($gr in $gp.Items) { $list.Add(($gr | ConvertFrom-Json -Depth 8)) }
                        $g = Get-Prop $gp.Meta '@odata.nextLink'
                    }
                    $groups = $list.ToArray()
                }
                catch { $groupsRefused++; Write-Verbose "The groups of policy $(Get-Prop $p 'id') were not read: $($_.Exception.Message)" }
            }
            $r = ConvertTo-SpendPolicyRow -Raw $raw -Groups $groups
            foreach ($row in $r.Rows) { $rows.Add($row) }
            foreach ($n in $r.Notes) { $notes.Add($n) }
        }
        $next = Get-Prop $page.Meta '@odata.nextLink'
    }
    if ($groupsRefused) { $notes.Add("the groups of $groupsRefused group policy(ies) (the app needs $script:GroupPermissions)") }
    [pscustomobject]@{ Rows = $rows.ToArray(); Notes = $notes.ToArray(); Read = "$policies policy(ies)" }
}

function Read-GitHubBudget {
    <# Every budget of the enterprise, 100 a page, until GitHub says there are no more. Throws a refusal or a shape error when they cannot be read. #>
    param([string] $Base, [string] $Enterprise, [string] $BearerToken, [string] $Version)
    $rows = [System.Collections.Generic.List[object]]::new(); $notes = [System.Collections.Generic.List[string]]::new()
    $headers = @{ Accept = 'application/vnd.github+json'; 'X-GitHub-Api-Version' = $Version; 'User-Agent' = 'copilot-audit-backfill' }
    $path = "/enterprises/$([uri]::EscapeDataString($Enterprise))/settings/billing/budgets"
    $budgets = 0
    for ($n = 1; $n -le 1000; $n++) {
        $page = Split-JsonList (Invoke-SpendHttp -Uri ("{0}{1}?per_page=100&page={2}" -f $Base, $path, $n) -BearerToken $BearerToken -Headers $headers -Service 'GitHub').Content 'budgets'
        foreach ($raw in $page.Items) {
            $budgets++
            $r = ConvertTo-GitHubBudgetRow -Raw $raw -Enterprise $Enterprise
            if ($r.Row) { $rows.Add($r.Row) }
            foreach ($x in $r.Notes) { $notes.Add($x) }
        }
        $more = Get-Prop $page.Meta 'has_next_page'
        if ($page.Items.Count -eq 0 -or $more -eq $false -or ($null -eq $more -and $page.Items.Count -lt 100)) { break }
    }
    [pscustomobject]@{ Rows = $rows.ToArray(); Notes = $notes.ToArray(); Read = "$budgets budget(s)" }
}

function Get-UniqueLimit {
    <# The rows with each ExternalId once (the last given), and how many repeats were dropped. Pure, for the tests. #>
    param([AllowEmptyCollection()][object[]] $Rows)
    $by = [ordered]@{}
    foreach ($r in $Rows) { $by["$($r.ExternalId)".ToLowerInvariant()] = $r }
    [pscustomobject]@{ Rows = @($by.Values); Repeats = $Rows.Count - $by.Count }
}

$script:LimitColumns = [ordered]@{
    Source = [string]; ExternalId = [string]; Kind = [string]; Name = [string]; ScopeType = [string]; Scope = [string]; Unit = [string]
    Amount = [decimal]; Period = [string]; PerUser = [bool]; Raw = [string]
}

function Save-SpendLimitSource {
    <# One source's rows in place of its last load, in one transaction, behind the delete guard. Returns how many were written. #>
    param([System.Data.SqlClient.SqlConnection] $Connection, [string] $Name, [AllowEmptyCollection()][object[]] $Rows, [double] $MaxDrop, [switch] $AllowDrop)
    Write-UsageTable $Connection 'audit.SpendLimitSource' (ConvertTo-UsageTable $Rows $script:LimitColumns) -ReplaceWhere 'Source = @s' -ReplaceParameters @{ '@s' = $Name } `
        -MaxDropPercent $MaxDrop -What "Spend limits from $Name" -Setting 'SpendLimitMaxDropPercent' -AllowDrop:$AllowDrop
}

function Start-SpendLimitRun {
    <# Records the run from its start, so a failure shows too. Returns its id. #>
    param([System.Data.SqlClient.SqlConnection] $Connection, [string] $Scope)
    [int] (Invoke-UsageSql $Connection @'
INSERT INTO audit.ExternalUsageRun (Source, Scope, Host) VALUES ('SpendLimits', LEFT(@scope, 400), @h);
SELECT CAST(SCOPE_IDENTITY() AS INT);
'@ @{ '@scope' = $Scope; '@h' = [Environment]::MachineName })
}

function Get-RunOutcome {
    <#
        How the run ends, from each source's outcome (Loaded, Guarded, Refused, Shape, Error, NotConfigured) and
        whether any note was made: Failed when a source failed outright and none was loaded or held by the
        guard; Partial when any source was not loaded, or a note was made; else Succeeded. The exit code: 3
        when the guard held a source, 1 when Failed, else 0. Pure, for the tests.
    #>
    param([hashtable] $Outcome, [bool] $Notes)
    $values = @($Outcome.Values)
    $loaded = @($values | Where-Object { $_ -eq 'Loaded' }).Count
    $guarded = @($values | Where-Object { $_ -eq 'Guarded' }).Count
    $errors = @($values | Where-Object { $_ -eq 'Error' }).Count
    $tried = @($values | Where-Object { $_ -ne 'NotConfigured' }).Count
    $status = if ($errors -gt 0 -and $loaded -eq 0 -and $guarded -eq 0) { 'Failed' }
              elseif ($tried -eq 0 -or $loaded -lt $tried -or $Notes) { 'Partial' }
              else { 'Succeeded' }
    $exit = if ($guarded -gt 0) { 3 } elseif ($status -eq 'Failed') { 1 } else { 0 }
    [pscustomobject]@{ Status = $status; ExitCode = $exit }
}

function Invoke-Main {
    $setting = @{}
    if (Test-Path -LiteralPath $SettingsPath) { $setting = Import-PowerShellDataFile -LiteralPath $SettingsPath }
    function Setting($k) { if ($setting.Contains($k) -and $setting[$k]) { $setting[$k] } }
    $maxDrop = Resolve-MaxDropPercent -What 'Spend limits' -Given $MaxDropPercent -WasGiven $script:MaxDropGiven -Settings $setting
    $tenant = if ($TenantId) { $TenantId } else { Setting 'TenantId' }
    $client = if ($ClientId) { $ClientId } else { Setting 'ClientId' }
    $thumb = if ($CertificateThumbprint) { $CertificateThumbprint } else { Setting 'CertificateThumbprint' }
    $ent = if ($Enterprise) { $Enterprise } else { Setting 'GitHubEnterprise' }
    $tokenFile = if ($TokenPath) { $TokenPath } elseif (Setting 'GitHubTokenPath') { Setting 'GitHubTokenPath' } else { Join-Path (Split-Path $SettingsPath -Parent) 'github-copilot.token' }
    $base = if ($script:Given.ContainsKey('ApiBase')) { $ApiBase } elseif (Setting 'GitHubApiBase') { [string] (Setting 'GitHubApiBase') } else { $ApiBase }
    $base = $base.TrimEnd('/'); $graph = $GraphEndpoint.TrimEnd('/')
    $server = if ($SqlServer) { $SqlServer } else { Setting 'SqlServer' }
    $db = if ($script:Given.ContainsKey('Database') -or -not (Setting 'Database')) { $Database } else { Setting 'Database' }
    $trust = $TrustServerCertificate -or [bool] (Setting 'TrustServerCertificate')

    # What each source needs, and why one is skipped.
    $plan = [ordered]@{}
    if ('Graph' -in $Source) {
        $plan.Graph = if ($GraphAccessToken -or ($tenant -and $client -and $thumb)) { $null } else { 'not configured (it needs TenantId, ClientId and CertificateThumbprint)' }
    }
    if ('GitHub' -in $Source) {
        $plan.GitHub = if (-not $ent) { "not configured (it needs -Enterprise or GitHubEnterprise: budgets are an enterprise's$(if (Setting 'GitHubOrganization') { ', and GitHubOrganization alone has none' }))" }
                       elseif (-not ($Token -or $env:GITHUB_COPILOT_TOKEN -or (Test-Path -LiteralPath $tokenFile))) { "not configured (no token: run tools\Import-GitHubCopilotUsage.ps1 -SaveToken, or set GITHUB_COPILOT_TOKEN)" }
                       else { $null }
    }
    function Get-Bearer([string] $Which) {
        if ($Which -eq 'Graph') {
            if ($GraphAccessToken) { return [System.Net.NetworkCredential]::new('', $GraphAccessToken).Password }
            return Get-GraphToken -Tenant $tenant -Client $client -Thumbprint $thumb -Login $LoginEndpoint -Graph $graph
        }
        if ($Token) { return [System.Net.NetworkCredential]::new('', $Token).Password }
        if ($env:GITHUB_COPILOT_TOKEN) { return $env:GITHUB_COPILOT_TOKEN }
        Unprotect-GitHubToken ([System.IO.File]::ReadAllBytes($tokenFile))
    }
    function Read-Source([string] $Which) {
        $bearer = Get-Bearer $Which
        if ($Which -eq 'Graph') { return Read-GraphSpendLimit -Graph $graph -BearerToken $bearer }
        Read-GitHubBudget -Base $base -Enterprise $ent -BearerToken $bearer -Version $ApiVersion
    }

    if ($CheckOnly) {
        Write-Line "Checking $($plan.Keys -join ' and '); nothing is written."
        $missing = 0
        foreach ($which in $plan.Keys) {
            if ($plan[$which]) { Write-Line ("{0,-7} {1}: {2}." -f 'NOTE', $which, $plan[$which]); continue }
            try {
                $r = Read-Source $which
                Write-Line ("{0,-7} {1}: readable, {2}, {3} row(s){4}." -f 'OK', $which, $r.Read, $r.Rows.Count, $(if ($r.Notes.Count) { '; ' + ($r.Notes -join '; ') }))
            }
            catch {
                $missing++
                $hint = if ($which -eq 'Graph') { "Grant the app $script:GraphPermissions and $script:GroupPermissions (application, Graph beta), with consent" }
                        else { 'The token needs an enterprise owner or billing manager, with Enterprise billing: read' }
                Write-Line ("{0,-7} {1}: not readable. {2}. It said: {3}" -f 'MISSING', $which, $hint, $_.Exception.Message)
            }
        }
        Write-Line $(if ($missing) { "$missing source(s) configured cannot be read: grant what is named above." } else { 'Every source configured can be read.' })
        if ($missing) { exit 1 }
        return
    }
    if (-not $ConnectionString -and -not $server) { throw 'Give -SqlServer, or SqlServer in the settings file.' }

    $conn = New-UsageConnection -SqlServer $server -Database $db -TrustServerCertificate:$trust -ConnectionString $ConnectionString
    try {
        if ([int] (Invoke-UsageSql $conn "SELECT CASE WHEN OBJECT_ID(N'audit.SpendLimitSource', N'U') IS NULL OR OBJECT_ID(N'audit.ExternalUsageRun', N'U') IS NULL THEN 0 ELSE 1 END;") -ne 1) {
            throw "The tables are not in $($conn.Database) yet: run Install-CopilotAudit.ps1 from this release first."
        }
        $scopeText = @($plan.Keys | ForEach-Object { if ($_ -eq 'GitHub' -and $ent) { "GitHub enterprise:$ent" } else { $_ } }) -join ', '
        $runId = Start-SpendLimitRun $conn -Scope $scopeText
        $written = 0
        $outcome = @{}
        $done = [System.Collections.Generic.List[string]]::new(); $skipped = [System.Collections.Generic.List[string]]::new(); $notes = [System.Collections.Generic.List[string]]::new()
        $unset = [System.Collections.Generic.List[string]]::new()
        try {
            foreach ($which in $plan.Keys) {
                if ($plan[$which]) { $outcome[$which] = 'NotConfigured'; $unset.Add("$which $($plan[$which])"); continue }
                try {
                    $r = Read-Source $which
                    $u = Get-UniqueLimit $r.Rows
                    foreach ($n in $r.Notes) { $notes.Add("${which}: $n") }
                    if ($u.Repeats) { $notes.Add("${which}: $($u.Repeats) repeated id(s), kept once") }
                    $n = Save-SpendLimitSource $conn $which $u.Rows -MaxDrop $maxDrop -AllowDrop:$AllowDrop
                    $written += $n; $outcome[$which] = 'Loaded'; $done.Add("$which $n limit(s) from $($r.Read)")
                    Write-Line "${which}: $n limit(s) from $($r.Read)."
                }
                catch {
                    $kind = Get-SourceErrorKind $_
                    $outcome[$which] = if (Test-DropRefusal $_) { 'Guarded' } elseif ($kind) { $kind } else { 'Error' }
                    $why = switch ($outcome[$which]) {
                        'Refused' { if ($which -eq 'Graph') { "refused, the app needs $script:GraphPermissions" } else { 'refused, the token needs an enterprise owner or billing manager' } }
                        'Shape' { 'an answer in a shape this load cannot read' }
                        default { $null }
                    }
                    $skipped.Add("$which ($(if ($why) { "${why}: " })$($_.Exception.Message)); its last load kept")
                    Write-Warning "$which was not loaded: $($_.Exception.Message)"
                }
            }
            # A source not configured is named, without changing how the run ends; none configured at all is Partial.
            $end = Get-RunOutcome -Outcome $outcome -Notes ($notes.Count -gt 0)
            $detail = (@($(if ($done.Count) { 'Loaded ' + ($done -join ', ') + '.' }), $(if ($skipped.Count) { 'Not loaded: ' + ($skipped -join '; ') + '.' }),
                         $(if ($notes.Count) { 'Notes: ' + ($notes -join '; ') + '.' }), $(if ($unset.Count) { 'Skipped: ' + ($unset -join '; ') + '.' })) | Where-Object { $_ }) -join ' '
            Complete-UsageRun $conn -RunId $runId -Status $end.Status -Rows $written -Detail $detail
            Write-Line "$($end.Status). $detail"
            if ($end.ExitCode -ne 0) { exit $end.ExitCode }
        }
        catch {
            Complete-UsageRun $conn -RunId $runId -Status Failed -Rows $written -Detail $_.Exception.Message
            throw
        }
    }
    finally { $conn.Dispose() }
}

# Dot-sourcing (tests) loads the functions without running anything.
if ($MyInvocation.InvocationName -ne '.') { Invoke-Main }
