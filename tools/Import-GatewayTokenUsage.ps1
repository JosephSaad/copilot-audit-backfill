<#
.SYNOPSIS
    Loads the model tokens that go through the organization's AI gateway (Azure API Management), and the token
    limits the gateway sets, into the Copilot database. Counts only: nothing here is money.

.DESCRIPTION
    Signs in as an app registration with a certificate (by default the pipeline's own app and certificate) and
    writes two tables (sql\Create-CopilotExternalUsageSchema.sql, which Install-CopilotAudit.ps1 runs):

      1. Tokens (audit.GatewayTokenDay). The gateway's llm-emit-token-metric (or azure-openai-emit-token-metric)
         policy sends the metrics "Total Tokens", "Prompt Tokens" and "Completion Tokens" to Application Insights,
         with the dimensions the policy names. They are read from the Log Analytics workspace behind Application
         Insights, through the Log Analytics query API: table AppMetrics (workspace-based Application Insights), or
         customMetrics (asked of the Application Insights resource itself, for one with no workspace). They are
         summed per UTC day, gateway, business unit, model, agent and API: prompt, completion and total tokens, and
         calls (the metric records, one a call). The days loaded are written again for the gateways seen.

         The gateway is the "Service Name" dimension API Management stamps on its metrics, else the last part of
         "Service ID", else the one gateway named in -GatewayService, else the role name Application Insights
         records. The business unit, the model and the agent are the dimensions named by -BusinessUnitDimension,
         -ModelDimension and -AgentDimension ('' where a record has none), and the API is the default "API ID"
         dimension. A dimension is found whatever its case and spacing ("Business unit", "business-unit" and
         "BusinessUnit" are one name).

      2. Token limits (audit.SpendLimitSource, Source 'Gateway'). Through Azure Resource Manager, the policies of
         each gateway at service (all APIs), product and API scope, and every llm-token-limit (or
         azure-openai-token-limit) element in them: a token quota (token-quota over token-quota-period) is a row of
         Kind GatewayQuota, a rate (tokens-per-minute) a row of Kind GatewayRate, each per counter key. An amount
         or period given as a policy expression is left empty; the element itself is kept in Raw. A gateway's rows
         are replaced whole each run, behind the delete guard (tools\DropGuard.ps1). Policies at operation scope
         are not read.

    Each part is read on its own: one Azure refuses (a 403 on the workspace, say) is left out and named in the
    run's detail, and the run ends Partial. The run is recorded in audit.ExternalUsageRun (Source 'Gateway'),
    which Admin > Health shows.

    THE APP'S ACCESS. Log Analytics Reader (or Reader) on the workspace for the tokens, and Reader on the
    Application Insights resource when it is given by -AppInsights; API Management Service Reader (or Reader) on
    each gateway for the limits. No key of the gateway, the workspace or Application Insights is used.

    -CheckOnly signs in, tries each read once and writes nothing (no database is needed): one line per part, OK
    or what is missing and where, and exit code 0 when every part can be read.

    With no -GatewayService, -WorkspaceId or -AppInsights, everything comes from copilot-audit.settings.psd1:
    GatewayServices, GatewayWorkspaceId, GatewayAppInsights, GatewayTable, GatewayBusinessUnitDimension,
    GatewayModelDimension, GatewayAgentDimension, GatewayLimitMaxDropPercent; GatewayTenantId, GatewayClientId
    and GatewayCertificateThumbprint where the app is not the pipeline's (else TenantId, ClientId and
    CertificateThumbprint); and SqlServer, Database and TrustServerCertificate.

    Exit codes: 0 the run ended Succeeded or Partial; 1 it failed (or -CheckOnly found a part it cannot read);
    3 the delete guard kept a gateway's limits (the tokens still loaded), which the nightly run records and goes on.

.PARAMETER GatewayService
    The API Management services, by resource id (/subscriptions/<id>/resourceGroups/<rg>/providers/
    Microsoft.ApiManagement/service/<name>). Their limits are read; their names are the gateways the tokens are
    written for.

.PARAMETER WorkspaceId
    The Log Analytics workspace's id (its workspace id, a GUID, not its resource id).

.PARAMETER AppInsights
    The Application Insights resource, by resource id, in place of -WorkspaceId: its workspace is looked up; one
    with no workspace (classic) is asked itself, for customMetrics.

.PARAMETER Table
    AppMetrics, customMetrics, or Auto (the default): AppMetrics from a workspace, customMetrics from an
    Application Insights resource with no workspace.

.PARAMETER Days
    How many complete UTC days to read, ending yesterday. Default 10; at most 90.

.PARAMETER MaxDropPercent
    The delete guard. A gateway's limits are replaced whole each run. When the policies give more than this
    percentage fewer than the table holds for it (GatewayLimitMaxDropPercent in the settings file, 15 by
    default), they are not replaced, the run ends Partial and the exit code is 3. 0 turns the guard off.

.PARAMETER AllowDrop
    Replace the limits even when the guard would refuse, once: after limits were really removed.

.PARAMETER CheckOnly
    Sign in and try each read once, writing nothing.

.EXAMPLE
    .\tools\Import-GatewayTokenUsage.ps1 -GatewayService /subscriptions/<id>/resourceGroups/rg-ai/providers/Microsoft.ApiManagement/service/ai-gateway `
        -AppInsights /subscriptions/<id>/resourceGroups/rg-ai/providers/Microsoft.Insights/components/ai-gateway-insights `
        -TenantId <tenant> -ClientId <app-id> -CertificateThumbprint <thumbprint> -SqlServer SQLPROD01 -Days 90
.EXAMPLE
    .\tools\Import-GatewayTokenUsage.ps1          # as the nightly run does, from the settings file
#>
#Requires -Version 7.2
[CmdletBinding()]
param(
    [string[]] $GatewayService,
    [string] $WorkspaceId,
    [string] $AppInsights,
    [ValidateSet('Auto', 'AppMetrics', 'customMetrics')][string] $Table = 'Auto',
    [string] $BusinessUnitDimension = 'Business unit',
    [string] $ModelDimension,
    [string] $AgentDimension,
    [ValidateRange(1, 90)][int] $Days = 10,
    [string] $TenantId,
    [string] $ClientId,
    [string] $CertificateThumbprint,
    [string] $SqlServer,
    [string] $Database = 'CopilotAudit',
    [switch] $TrustServerCertificate,
    [string] $ConnectionString,
    [string] $SettingsPath = (Join-Path (Split-Path $PSScriptRoot -Parent) 'copilot-audit.settings.psd1'),
    # A token already in hand, in place of signing in (the tests; used for every service); and the endpoints,
    # for a sovereign cloud or a mock.
    [securestring] $AccessToken,
    [string] $ArmEndpoint = 'https://management.azure.com',
    [string] $LoginEndpoint = 'https://login.microsoftonline.com',
    [string] $LogAnalyticsEndpoint = 'https://api.loganalytics.azure.com',
    [ValidateRange(0, 100)][double] $MaxDropPercent = 15,
    [switch] $AllowDrop,
    [switch] $CheckOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ExternalUsage.Common.ps1')
# This loader's percentage, by the name Resolve-MaxDropPercent looks it up by.
$script:DropGuardSetting['Gateway limits'] = 'GatewayLimitMaxDropPercent'
$script:Bound = @{} + $PSBoundParameters   # read here: in Invoke-Main, $PSBoundParameters is its own

$script:ApimVersion = '2024-05-01'
$script:InsightsVersion = '2020-02-02'
$script:WorkspaceVersion = '2023-09-01'
$script:TokenMetrics = 'Total Tokens', 'Prompt Tokens', 'Completion Tokens'
$script:LimitElements = 'llm-token-limit', 'azure-openai-token-limit'
$script:QuotaPeriods = 'Hourly', 'Daily', 'Weekly', 'Monthly', 'Yearly'
# The widths of audit.GatewayTokenDay's text columns, so a long value is cut, not refused by the bulk copy.
$script:Width = @{ Gateway = 260; BusinessUnit = 256; Model = 200; Agent = 200; Api = 200 }

function Write-Line([string] $Text) { Write-Information ("{0:u} {1}" -f [datetime]::UtcNow, $Text) -InformationAction Continue }

function ConvertTo-Base64Url([byte[]] $Bytes) { [Convert]::ToBase64String($Bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_') }

function New-ClientAssertion {
    <#
        The signed statement an app signs in to Entra with in place of a secret: a JWT signed with the
        certificate's private key (PS256), naming the certificate by the SHA-256 thumbprint of its DER encoding
        (x5t#S256), good for ten minutes.
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
    <# An app-only token for -Audience (ARM, or the Log Analytics API), by the certificate in LocalMachine\My or CurrentUser\My. #>
    param([string] $Tenant, [string] $Client, [string] $Thumbprint, [string] $Login, [string] $Audience)
    $clean = ("$Thumbprint" -replace '[^0-9A-Fa-f]', '').ToUpperInvariant()
    $cert = $null
    foreach ($store in 'Cert:\LocalMachine\My', 'Cert:\CurrentUser\My') { $cert = Get-Item -LiteralPath "$store\$clean" -ErrorAction SilentlyContinue; if ($cert) { break } }
    if (-not $cert) { throw "No certificate $clean in LocalMachine\My or CurrentUser\My." }
    $r = Invoke-RestMethod -Method Post -Uri "$($Login.TrimEnd('/'))/$Tenant/oauth2/v2.0/token" -ContentType 'application/x-www-form-urlencoded' -TimeoutSec 60 -Body @{
        client_id = $Client; scope = "$($Audience.TrimEnd('/'))/.default"; grant_type = 'client_credentials'
        client_assertion_type = 'urn:ietf:params:oauth:client-assertion-type:jwt-bearer'
        client_assertion = (New-ClientAssertion -Certificate $cert -Tenant $Tenant -Client $Client -Login $Login)
    }
    $r.access_token
}

function Invoke-Azure {
    <#
        One call of an Azure API (ARM or Log Analytics) with the token. Throttling (429) is waited out, for as
        long as Azure says, up to three times. Any other failure throws with Azure's own message, the HTTP status
        in the exception's Data['Status'].
    #>
    param([string] $Uri, [string] $BearerToken, [string] $Method = 'Get', $Body)
    for ($try = 1; ; $try++) {
        $p = @{ Uri = $Uri; Method = $Method; Headers = @{ Authorization = "Bearer $BearerToken" }; SkipHttpErrorCheck = $true; TimeoutSec = 180 }
        if ($null -ne $Body) { $p.Body = ($Body | ConvertTo-Json -Depth 20 -Compress); $p.ContentType = 'application/json' }
        $r = Invoke-WebRequest @p
        $status = [int] $r.StatusCode
        if ($status -eq 429 -and $try -le 3) {
            $wait = 30
            if ($r.Headers.ContainsKey('Retry-After')) { $n = 0; if ([int]::TryParse("$(@($r.Headers['Retry-After'])[0])", [ref] $n) -and $n -gt 0) { $wait = [Math]::Min($n, 300) } }
            Write-Line "Azure asked to wait $wait s (throttled)."; Start-Sleep -Seconds $wait; continue
        }
        $parsed = $null
        if ($status -ne 204 -and $r.Content) { try { $parsed = $r.Content | ConvertFrom-Json -Depth 64 } catch { $parsed = $null } }
        if ($status -ge 400) {
            $message = "HTTP $status"
            $err = Get-Prop $parsed 'error'
            if ($err) { $message = "$(Get-Prop $err 'code'): $(Get-Prop $err 'message')" }
            $ex = [System.InvalidOperationException]::new("Azure answered $status for $(([uri] $Uri).AbsolutePath): $message")
            $ex.Data['Status'] = $status
            throw $ex
        }
        return $parsed
    }
}

function Get-AzureHttpCode {
    <# The HTTP status an error of Invoke-Azure carries, or 0. #>
    param($ErrorRecord)
    $e = if ($ErrorRecord -is [System.Management.Automation.ErrorRecord]) { $ErrorRecord.Exception } else { $ErrorRecord }
    if ($e -and $e.Data -and $e.Data.Contains('Status')) { [int] $e.Data['Status'] } else { 0 }
}

function Get-ArmList {
    <# Every item of a list that comes a page at a time (value[] and nextLink). #>
    param([string] $Uri, [string] $BearerToken)
    $all = [System.Collections.Generic.List[object]]::new()
    for ($next = $Uri; $next; ) {
        $page = Invoke-Azure -Uri $next -BearerToken $BearerToken
        if ($null -eq $page) { break }
        foreach ($v in @(Get-Prop $page 'value')) { if ($v) { $all.Add($v) } }
        $next = Get-Prop $page 'nextLink'
    }
    , $all.ToArray()
}

function Get-Prop {
    <# A property of an object from JSON, or $null where it has none. #>
    param($Object, [string] $Name)
    if ($null -eq $Object) { return $null }
    $pp = $Object.PSObject.Properties[$Name]
    if ($pp) { $pp.Value } else { $null }
}

function Split-GatewayId {
    <# An API Management service's resource id as its subscription, group, name and id (in lower case); $null for anything else. Pure, for the tests. #>
    param([string] $ResourceId)
    if ("$ResourceId".Trim() -match '(?i)^/?subscriptions/(?<s>[^/]+)/resourceGroups/(?<g>[^/]+)/providers/Microsoft\.ApiManagement/service/(?<n>[^/]+)/?$') {
        [pscustomobject]@{ SubscriptionId = $Matches.s.ToLowerInvariant(); ResourceGroup = $Matches.g; Name = $Matches.n
                           Id = "/subscriptions/$($Matches.s)/resourceGroups/$($Matches.g)/providers/Microsoft.ApiManagement/service/$($Matches.n)".ToLowerInvariant() }
    }
}

function Get-NameKey([string] $Name) { ("$Name" -replace '[^A-Za-z0-9]', '').ToLowerInvariant() }

function Get-DimensionValue {
    <#
        A dimension's value from a record's dimensions (a hashtable or an object), its name matched whatever its
        case and spacing; '' where the record has none or no name is given. Pure, for the tests.
    #>
    param($Dimensions, [string] $Name)
    if (-not $Name -or $null -eq $Dimensions) { return '' }
    $want = Get-NameKey $Name
    if ($Dimensions -is [System.Collections.IDictionary]) {
        foreach ($k in $Dimensions.Keys) { if ((Get-NameKey "$k") -eq $want -and $null -ne $Dimensions[$k]) { return "$($Dimensions[$k])" } }
    }
    else {
        foreach ($pp in $Dimensions.PSObject.Properties) { if ((Get-NameKey $pp.Name) -eq $want -and $null -ne $pp.Value) { return "$($pp.Value)" } }
    }
    ''
}

function Get-TokenQuery {
    <#
        The question asked of Log Analytics: the three token metrics over the days, summed per UTC day, metric,
        role and set of dimensions (the dimensions as one JSON text, read apart here). AppMetrics and
        customMetrics name their columns differently. Pure, for the tests.
    #>
    param([ValidateSet('AppMetrics', 'customMetrics')][string] $Table, [datetime] $From, [datetime] $To)
    $c = if ($Table -eq 'AppMetrics') { @{ Time = 'TimeGenerated'; Name = 'Name'; Sum = 'Sum'; Count = 'ItemCount'; Role = 'AppRoleName'; Dims = 'Properties' } }
         else { @{ Time = 'timestamp'; Name = 'name'; Sum = 'valueSum'; Count = 'valueCount'; Role = 'cloud_RoleName'; Dims = 'customDimensions' } }
    $names = ($script:TokenMetrics | ForEach-Object { "'$_'" }) -join ', '
    $start = $From.Date.ToString('yyyy-MM-dd'); $end = $To.Date.AddDays(1).ToString('yyyy-MM-dd')
    @"
$Table
| where $($c.Time) >= datetime($start) and $($c.Time) < datetime($end)
| where $($c.Name) in ($names)
| summarize Total = sum($($c.Sum)), Records = sum($($c.Count)) by Day = format_datetime(startofday($($c.Time)), 'yyyy-MM-dd'), Metric = $($c.Name), Role = tostring($($c.Role)), Dimensions = tostring($($c.Dims))
"@
}

function ConvertFrom-LogQuery {
    <#
        The Log Analytics query API's answer (tables[0], its columns and rows) as one object per row, each value
        under its column's name. Pure, for the tests.
    #>
    param($Response)
    $tables = @(Get-Prop $Response 'tables')
    if ($tables.Count -eq 0 -or $null -eq $tables[0]) { return , @() }
    $names = @(Get-Prop $tables[0] 'columns' | ForEach-Object { "$(Get-Prop $_ 'name')" })
    $out = [System.Collections.Generic.List[object]]::new()
    foreach ($r in @(Get-Prop $tables[0] 'rows')) {
        if ($null -eq $r) { continue }
        $o = [ordered]@{}
        for ($i = 0; $i -lt $names.Count; $i++) { $o[$names[$i]] = $(if ($i -lt @($r).Count) { @($r)[$i] }) }
        $out.Add([pscustomobject] $o)
    }
    , $out.ToArray()
}

function ConvertTo-UtcDay($Value) {
    <# A day as the query sends it, a 'yyyy-MM-dd' text (or a date, where the JSON reader made it one). #>
    if ($Value -is [datetime]) { return $Value.Date }
    [datetime]::ParseExact("$Value".Substring(0, 10), 'yyyy-MM-dd', [cultureinfo]::InvariantCulture)
}

function Get-ClippedText([string] $Text, [int] $Width) { if ($Text.Length -gt $Width) { $Text.Substring(0, $Width) } else { $Text } }

function Merge-GatewayToken {
    <#
        The query's rows as rows of audit.GatewayTokenDay: one per gateway, UTC day, business unit, model, agent
        and API, each metric in its column and Calls the records of Total Tokens (else of Prompt Tokens). A row
        with prompt and completion tokens but no total gets their sum as its total. -DefaultGateway is the
        gateway of a record that names none. Pure, for the tests.
    #>
    param([AllowEmptyCollection()][object[]] $Rows, [string] $BusinessUnitDimension, [string] $ModelDimension, [string] $AgentDimension, [string] $DefaultGateway = '')
    $byKey = [ordered]@{}
    foreach ($r in $Rows) {
        $metric = "$(Get-Prop $r 'Metric')"
        if ($metric -notin $script:TokenMetrics) { continue }
        $dims = $null
        $text = "$(Get-Prop $r 'Dimensions')"
        if ($text) { try { $dims = $text | ConvertFrom-Json -Depth 8 } catch { $dims = $null } }
        $gateway = Get-DimensionValue $dims 'Service Name'
        if (-not $gateway) { $sid = Get-DimensionValue $dims 'Service ID'; if ($sid) { $gateway = @($sid.TrimEnd('/') -split '/')[-1] } }
        if (-not $gateway) { $gateway = $DefaultGateway }
        if (-not $gateway) { $gateway = "$(Get-Prop $r 'Role')" }
        $row = [ordered]@{
            Gateway = Get-ClippedText $gateway $script:Width.Gateway; Day = (ConvertTo-UtcDay (Get-Prop $r 'Day'))
            BusinessUnit = Get-ClippedText (Get-DimensionValue $dims $BusinessUnitDimension) $script:Width.BusinessUnit
            Model = Get-ClippedText (Get-DimensionValue $dims $ModelDimension) $script:Width.Model
            Agent = Get-ClippedText (Get-DimensionValue $dims $AgentDimension) $script:Width.Agent
            Api = Get-ClippedText (Get-DimensionValue $dims 'API ID') $script:Width.Api
        }
        $key = "$($row.Gateway)|$($row.Day.ToString('yyyyMMdd'))|$($row.BusinessUnit)|$($row.Model)|$($row.Agent)|$($row.Api)".ToLowerInvariant()
        if (-not $byKey.Contains($key)) {
            $row.PromptTokens = $null; $row.CompletionTokens = $null; $row.TotalTokens = $null; $row.Calls = $null; $row.PromptCalls = $null
            $byKey[$key] = [pscustomobject] $row
        }
        $t = $byKey[$key]
        $sum = [long] [math]::Round([double] "0$(Get-Prop $r 'Total')")
        $records = [long] [math]::Round([double] "0$(Get-Prop $r 'Records')")
        switch ($metric) {
            'Total Tokens' { $t.TotalTokens = [long] "0$($t.TotalTokens)" + $sum; $t.Calls = [long] "0$($t.Calls)" + $records }
            'Prompt Tokens' { $t.PromptTokens = [long] "0$($t.PromptTokens)" + $sum; $t.PromptCalls = [long] "0$($t.PromptCalls)" + $records }
            'Completion Tokens' { $t.CompletionTokens = [long] "0$($t.CompletionTokens)" + $sum }
        }
    }
    foreach ($t in $byKey.Values) {
        if ($null -eq $t.Calls) { $t.Calls = $t.PromptCalls }
        if ($null -eq $t.TotalTokens -and ($null -ne $t.PromptTokens -or $null -ne $t.CompletionTokens)) { $t.TotalTokens = [long] "0$($t.PromptTokens)" + [long] "0$($t.CompletionTokens)" }
    }
    , @($byKey.Values)
}

function ConvertFrom-TokenLimitPolicy {
    <#
        Every llm-token-limit and azure-openai-token-limit element of one policy document, as rows of
        audit.SpendLimitSource: a GatewayQuota row for a token-quota (its period), a GatewayRate row for a
        tokens-per-minute (period Minute), each per counter key. -ScopeId is the policy's resource id, which with
        the element's place and the kind makes ExternalId; -Name names the gateway and the scope. An amount or
        period that is a policy expression is left empty. Pure, for the tests.
    #>
    param([string] $PolicyXml, [string] $ScopeId, [string] $Name)
    $rows = [System.Collections.Generic.List[object]]::new()
    if (-not "$PolicyXml".Trim()) { return , @() }
    $doc = [System.Xml.XmlDocument]::new()
    $doc.XmlResolver = $null
    $doc.LoadXml($PolicyXml)
    $index = 0
    foreach ($el in $doc.SelectNodes("//*[local-name()='llm-token-limit' or local-name()='azure-openai-token-limit']")) {
        $at = { param($n) $a = $el.Attributes[$n]; if ($a) { "$($a.Value)".Trim() } else { '' } }
        $key = & $at 'counter-key'
        $common = @{ Source = 'Gateway'; Name = (Get-ClippedText $Name 400); ScopeType = 'CounterKey'; Scope = $(if ($key) { Get-ClippedText $key 1000 }); Unit = 'Tokens'; PerUser = $true; Raw = $el.OuterXml }
        $rate = & $at 'tokens-per-minute'
        if ($rate) { $rows.Add([pscustomobject] ($common + @{ ExternalId = "$ScopeId#$index#GatewayRate"; Kind = 'GatewayRate'; Amount = (ConvertTo-Amount $rate); Period = 'Minute' })) }
        $quota = & $at 'token-quota'
        if ($quota) {
            $given = & $at 'token-quota-period'
            $period = $script:QuotaPeriods | Where-Object { $_ -ieq $given } | Select-Object -First 1
            $rows.Add([pscustomobject] ($common + @{ ExternalId = "$ScopeId#$index#GatewayQuota"; Kind = 'GatewayQuota'; Amount = (ConvertTo-Amount $quota); Period = $period }))
        }
        $index++
    }
    , $rows.ToArray()
}

function ConvertTo-Amount([string] $Text) {
    <# A number as the policy gives it, or $null for a policy expression. #>
    $v = [decimal] 0
    if ([decimal]::TryParse($Text, [System.Globalization.NumberStyles]::Number, [cultureinfo]::InvariantCulture, [ref] $v)) { $v } else { $null }
}

function Get-GatewayPolicyScope {
    <#
        The policy scopes of one gateway: the service (all APIs), each product and each API (its current revision),
        each with the address of its policies and a readable name. -Products and -Apis are ARM's lists. Pure, for the tests.
    #>
    param($Gateway, [object[]] $Products = @(), [object[]] $Apis = @())
    $list = [System.Collections.Generic.List[object]]::new()
    $list.Add([pscustomobject]@{ Path = "$($Gateway.Id)/policies"; Name = "$($Gateway.Name) / all APIs" })
    foreach ($p in $Products) {
        $n = "$(Get-Prop $p 'name')"; if (-not $n) { continue }
        $shown = "$(Get-Prop (Get-Prop $p 'properties') 'displayName')"
        $list.Add([pscustomobject]@{ Path = "$($Gateway.Id)/products/$($n.ToLowerInvariant())/policies"; Name = "$($Gateway.Name) / product $(if ($shown) { $shown } else { $n })" })
    }
    foreach ($a in $Apis) {
        $n = "$(Get-Prop $a 'name')"; if (-not $n -or $n -like '*;rev=*') { continue }
        $shown = "$(Get-Prop (Get-Prop $a 'properties') 'displayName')"
        $list.Add([pscustomobject]@{ Path = "$($Gateway.Id)/apis/$($n.ToLowerInvariant())/policies"; Name = "$($Gateway.Name) / API $(if ($shown) { $shown } else { $n })" })
    }
    , $list.ToArray()
}

function Read-GatewayLimit {
    <# Every token limit of one gateway, read through ARM. A scope with no policy (404) has none; any other refusal throws. #>
    param($Gateway, [string] $Arm, [string] $BearerToken)
    $products = Get-ArmList -Uri "$Arm$($Gateway.Id)/products?api-version=$script:ApimVersion" -BearerToken $BearerToken
    $apis = Get-ArmList -Uri "$Arm$($Gateway.Id)/apis?api-version=$script:ApimVersion" -BearerToken $BearerToken
    $rows = [System.Collections.Generic.List[object]]::new()
    foreach ($scope in (Get-GatewayPolicyScope $Gateway $products $apis)) {
        $policies = @()
        try { $policies = Get-ArmList -Uri "$Arm$($scope.Path)?api-version=$script:ApimVersion" -BearerToken $BearerToken }
        catch { if ((Get-AzureHttpCode $_) -eq 404) { continue }; throw }
        foreach ($pol in $policies) {
            $pr = Get-Prop $pol 'properties'
            $xml = "$(Get-Prop $pr 'value')"
            if (-not $xml) { continue }
            $format = "$(Get-Prop $pr 'format')"
            if ($format -and $format -notin 'xml', 'rawxml') { continue }   # a policy held at a link is not read
            $polName = "$(Get-Prop $pol 'name')"; if (-not $polName) { $polName = 'policy' }
            try { foreach ($r in (ConvertFrom-TokenLimitPolicy -PolicyXml $xml -ScopeId "$($scope.Path)/$($polName.ToLowerInvariant())" -Name $scope.Name)) { $rows.Add($r) } }
            catch { throw "The policy at $($scope.Path) could not be read as XML: $($_.Exception.Message)" }
        }
    }
    , $rows.ToArray()
}

function Resolve-TokenSource {
    <#
        Where the tokens are asked: the Log Analytics workspace (by -WorkspaceId, or the one behind the Application
        Insights resource -AppInsights), else, for Application Insights with no workspace, the resource itself.
        Returns the query address and the table to read.
    #>
    param([string] $Workspace, [string] $Insights, [string] $Table, [string] $Arm, [string] $LogApi, [string] $BearerToken)
    $id = $Workspace
    if (-not $id) {
        $component = Invoke-Azure -Uri "$Arm$($Insights.TrimEnd('/'))?api-version=$script:InsightsVersion" -BearerToken $BearerToken
        $wsResource = "$(Get-Prop (Get-Prop $component 'properties') 'WorkspaceResourceId')"
        if ($wsResource) {
            $ws = Invoke-Azure -Uri "$Arm$($wsResource.TrimEnd('/'))?api-version=$script:WorkspaceVersion" -BearerToken $BearerToken
            $id = "$(Get-Prop (Get-Prop $ws 'properties') 'customerId')"
            if (-not $id) { throw "The workspace $wsResource gave no workspace id." }
        }
        else {
            $t = if ($Table -eq 'Auto') { 'customMetrics' } else { $Table }
            return [pscustomobject]@{ Uri = "$LogApi/v1/$($Insights.Trim('/'))/query"; Table = $t; Where = "Application Insights $(@($Insights.TrimEnd('/') -split '/')[-1])" }
        }
    }
    $t = if ($Table -eq 'Auto') { 'AppMetrics' } else { $Table }
    [pscustomobject]@{ Uri = "$LogApi/v1/workspaces/$id/query"; Table = $t; Where = "workspace $id" }
}

function Read-GatewayToken {
    <# The token metrics of the days, from where Resolve-TokenSource says, as the query's rows. #>
    param($Source, [datetime] $From, [datetime] $To, [string] $BearerToken)
    $body = [ordered]@{ query = (Get-TokenQuery -Table $Source.Table -From $From -To $To)
                        timespan = "$($From.Date.ToString('yyyy-MM-ddT00:00:00Z'))/$($To.Date.AddDays(1).ToString('yyyy-MM-ddT00:00:00Z'))" }
    $answer = Invoke-Azure -Uri $Source.Uri -BearerToken $BearerToken -Method Post -Body $body
    $err = Get-Prop $answer 'error'
    if ($err) { throw "Log Analytics answered only in part: $(Get-Prop $err 'code'): $(Get-Prop $err 'message')" }
    ConvertFrom-LogQuery $answer
}

function Start-GatewayRun {
    <# Records the run from its start, so a failure shows too. Returns its id. #>
    param([System.Data.SqlClient.SqlConnection] $Connection, [string] $Scope, $FromDay, $ToDay)
    [int] (Invoke-UsageSql $Connection @'
INSERT INTO audit.ExternalUsageRun (Source, Scope, FromDay, ToDay, Host) VALUES ('Gateway', LEFT(@scope, 400), @f, @t, @h);
SELECT CAST(SCOPE_IDENTITY() AS INT);
'@ @{ '@scope' = $Scope; '@f' = $FromDay; '@t' = $ToDay; '@h' = [Environment]::MachineName })
}

function Get-GatewayRunOutcome {
    <# How a run ends: Succeeded when nothing was left out, Failed when every part asked was, else Partial. Pure, for the tests. #>
    param([int] $Parts, [int] $Skipped)
    if ($Skipped -eq 0) { 'Succeeded' } elseif ($Skipped -ge $Parts) { 'Failed' } else { 'Partial' }
}

function Invoke-GatewayCheck {
    <# -CheckOnly: each read the load makes, tried once, nothing written. Returns the lines (Ok, Text). #>
    param([object[]] $Gateways, [string] $Workspace, [string] $Insights, [string] $Table, [string] $Arm, [string] $LogApi, [string] $ArmBearer, [string] $LogBearer)
    $lines = [System.Collections.Generic.List[object]]::new()
    function Line([bool] $ok, [string] $text) { $lines.Add([pscustomobject]@{ Ok = $ok; Text = $text }) }
    if ($Workspace -or $Insights) {
        $source = $null
        try { $source = Resolve-TokenSource -Workspace $Workspace -Insights $Insights -Table $Table -Arm $Arm -LogApi $LogApi -BearerToken $ArmBearer; Line $true "Token metrics: read from $($source.Where), table $($source.Table)." }
        catch { Line $false "Token metrics: the Application Insights resource or its workspace is not readable. Give the app Reader on it. Azure said: $($_.Exception.Message)" }
        if ($source) {
            $y = [datetime]::UtcNow.Date.AddDays(-1)
            try { $n = (Read-GatewayToken -Source $source -From $y -To $y -BearerToken $LogBearer).Count; Line $true "Token metrics of yesterday: readable ($n summed row(s))." }
            catch { Line $false "Token metrics: not readable from $($source.Where). Give the app Log Analytics Reader there. Azure said: $($_.Exception.Message)" }
        }
    }
    foreach ($g in $Gateways) {
        try { $n = (Read-GatewayLimit -Gateway $g -Arm $Arm -BearerToken $ArmBearer).Count; Line $true "Token limits of $($g.Name): readable ($n limit(s))." }
        catch { Line $false "Token limits of $($g.Name): not readable. Give the app API Management Service Reader (or Reader) on it. Azure said: $($_.Exception.Message)" }
    }
    , $lines.ToArray()
}

function Invoke-Main {
    $setting = @{}
    if (Test-Path -LiteralPath $SettingsPath) { $setting = Import-PowerShellDataFile -LiteralPath $SettingsPath }
    function Setting($k) { if ($setting.Contains($k) -and $setting[$k]) { $setting[$k] } }
    $script:MaxDropPercent = Resolve-MaxDropPercent -What 'Gateway limits' -Given $MaxDropPercent -WasGiven $script:Bound.ContainsKey('MaxDropPercent') -Settings $setting
    if (-not $GatewayService -and -not $WorkspaceId -and -not $AppInsights) {
        $script:GatewayService = @(Setting 'GatewayServices'); $script:WorkspaceId = Setting 'GatewayWorkspaceId'; $script:AppInsights = Setting 'GatewayAppInsights'
    }
    foreach ($pair in @(@('Table', 'GatewayTable'), @('BusinessUnitDimension', 'GatewayBusinessUnitDimension'), @('ModelDimension', 'GatewayModelDimension'), @('AgentDimension', 'GatewayAgentDimension'))) {
        if (-not $script:Bound.ContainsKey($pair[0]) -and (Setting $pair[1])) { Set-Variable -Scope Script -Name $pair[0] -Value "$(Setting $pair[1])" }
    }
    if ($Table -notin 'Auto', 'AppMetrics', 'customMetrics') { throw "GatewayTable is '$Table': it is Auto, AppMetrics or customMetrics." }
    if ($WorkspaceId -and $AppInsights) { throw 'Give -WorkspaceId or -AppInsights, not both.' }
    $gateways = @(foreach ($g in @($GatewayService | Where-Object { $_ })) {
            $s = Split-GatewayId $g
            if (-not $s) { throw "Not an API Management service's resource id: $g" }
            $s
        }) | Group-Object Id | ForEach-Object { $_.Group[0] }
    $gateways = @($gateways)
    $readTokens = [bool] ($WorkspaceId -or $AppInsights)
    if (-not $readTokens -and $gateways.Count -eq 0) { throw 'Give -GatewayService, -WorkspaceId or -AppInsights, or set GatewayServices and GatewayWorkspaceId (or GatewayAppInsights) in the settings file.' }
    if (-not $TenantId) { $script:TenantId = if (Setting 'GatewayTenantId') { Setting 'GatewayTenantId' } else { Setting 'TenantId' } }
    if (-not $ClientId) { $script:ClientId = if (Setting 'GatewayClientId') { Setting 'GatewayClientId' } else { Setting 'ClientId' } }
    if (-not $CertificateThumbprint) { $script:CertificateThumbprint = if (Setting 'GatewayCertificateThumbprint') { Setting 'GatewayCertificateThumbprint' } else { Setting 'CertificateThumbprint' } }
    if (-not $ConnectionString) {
        if (-not $SqlServer) { $script:SqlServer = Setting 'SqlServer' }
        if (-not $script:Bound.ContainsKey('Database') -and (Setting 'Database')) { $script:Database = Setting 'Database' }
        if (Setting 'TrustServerCertificate') { $script:TrustServerCertificate = $true }
    }
    $arm = $ArmEndpoint.TrimEnd('/'); $logApi = $LogAnalyticsEndpoint.TrimEnd('/')
    $signIn = {
        param([string] $Audience)
        if ($AccessToken) { return [System.Net.NetworkCredential]::new('', $AccessToken).Password }
        if (-not ($TenantId -and $ClientId -and $CertificateThumbprint)) { throw 'Signing in needs -TenantId, -ClientId and -CertificateThumbprint (or the settings file''s).' }
        Get-AppToken -Tenant $TenantId -Client $ClientId -Thumbprint $CertificateThumbprint -Login $LoginEndpoint -Audience $Audience
    }

    if ($CheckOnly) {
        $armBearer = & $signIn $arm
        $logBearer = if ($readTokens) { & $signIn $logApi }
        Write-Line "Signed in to Azure$(if ($ClientId) { " as $ClientId" }). Checking $($gateways.Count) gateway(s)$(if ($readTokens) { ' and the token metrics' }); nothing is written."
        $lines = Invoke-GatewayCheck -Gateways $gateways -Workspace $WorkspaceId -Insights $AppInsights -Table $Table -Arm $arm -LogApi $logApi -ArmBearer $armBearer -LogBearer $logBearer
        foreach ($l in $lines) { Write-Line ("{0,-7} {1}" -f $(if ($l.Ok) { 'OK' } else { 'MISSING' }), $l.Text) }
        $missing = @($lines | Where-Object { -not $_.Ok }).Count
        Write-Line $(if ($missing) { "$missing part(s) cannot be read: grant what is named above." } else { 'Every part can be read.' })
        if ($missing) { exit 1 }
        return
    }
    if (-not $ConnectionString -and -not $SqlServer) { throw 'Give -SqlServer, or SqlServer in the settings file.' }

    $conn = New-UsageConnection -SqlServer $SqlServer -Database $Database -TrustServerCertificate:$TrustServerCertificate -ConnectionString $ConnectionString
    $refused = $false
    try {
        $ready = [int] (Invoke-UsageSql $conn "SELECT CASE WHEN OBJECT_ID(N'audit.GatewayTokenDay', N'U') IS NULL OR OBJECT_ID(N'audit.SpendLimitSource', N'U') IS NULL OR OBJECT_ID(N'audit.ExternalUsageRun', N'U') IS NULL THEN 0 ELSE 1 END;")
        if ($ready -ne 1) { throw "The tables are not in $($conn.Database) yet: run Install-CopilotAudit.ps1 from this release first." }
        $days = Get-UsageDay -Days $Days
        $from = $days[0]; $to = $days[-1]
        $scope = @(@($gateways | ForEach-Object Name) + @($(if ($WorkspaceId) { "workspace $WorkspaceId" } elseif ($AppInsights) { "Application Insights $(@($AppInsights.TrimEnd('/') -split '/')[-1])" }))) -join ', '
        $runId = Start-GatewayRun $conn -Scope $scope -FromDay $from -ToDay $to
        $written = 0; $parts = 0
        $done = [System.Collections.Generic.List[string]]::new(); $skipped = [System.Collections.Generic.List[string]]::new()
        try {
            $armBearer = & $signIn $arm
            # 1. Tokens, per day, gateway, business unit, model, agent and API.
            if ($readTokens) {
                $parts++
                try {
                    $logBearer = & $signIn $logApi
                    $source = Resolve-TokenSource -Workspace $WorkspaceId -Insights $AppInsights -Table $Table -Arm $arm -LogApi $logApi -BearerToken $armBearer
                    $default = if ($gateways.Count -eq 1) { $gateways[0].Name } else { '' }
                    $rows = Merge-GatewayToken -Rows (Read-GatewayToken -Source $source -From $from -To $to -BearerToken $logBearer) -BusinessUnitDimension $BusinessUnitDimension `
                        -ModelDimension $ModelDimension -AgentDimension $AgentDimension -DefaultGateway $default
                    # The days are written again for every gateway seen, and for those named that sent nothing.
                    $names = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
                    foreach ($r in $rows) { [void] $names.Add($r.Gateway) }
                    foreach ($g in $gateways) { [void] $names.Add($g.Name) }
                    $n = 0
                    if ($names.Count) {
                        $p = @{ '@f' = $from; '@t' = $to }; $i = 0
                        foreach ($name in $names) { $p["@g$i"] = $name; $i++ }
                        $in = @(0..($i - 1) | ForEach-Object { "@g$_" }) -join ', '
                        $n = Write-UsageTable $conn 'audit.GatewayTokenDay' (ConvertTo-UsageTable $rows ([ordered]@{
                                Gateway = [string]; Day = [datetime]; BusinessUnit = [string]; Model = [string]; Agent = [string]; Api = [string]
                                PromptTokens = [long]; CompletionTokens = [long]; TotalTokens = [long]; Calls = [long] })) -ReplaceWhere "[Day] BETWEEN @f AND @t AND Gateway IN ($in)" -ReplaceParameters $p
                    }
                    $written += $n
                    $done.Add("$n token row(s) from $($source.Where) ($($source.Table)) for $($names.Count) gateway(s)")
                }
                catch {
                    $why = if ((Get-AzureHttpCode $_) -eq 403) { "refused (403): give the app Log Analytics Reader on the workspace" } else { $_.Exception.Message }
                    $skipped.Add("token metrics ($why)"); Write-Warning "The token metrics were not loaded: $($_.Exception.Message)"
                }
            }
            # 2. Token limits, gateway by gateway, each replaced whole behind the delete guard.
            $limits = 0
            foreach ($g in $gateways) {
                $parts++
                try {
                    $rows = Read-GatewayLimit -Gateway $g -Arm $arm -BearerToken $armBearer
                    $limits += Write-UsageTable $conn 'audit.SpendLimitSource' (ConvertTo-UsageTable $rows ([ordered]@{
                            Source = [string]; ExternalId = [string]; Kind = [string]; Name = [string]; ScopeType = [string]; Scope = [string]; Unit = [string]
                            Amount = [decimal]; Period = [string]; PerUser = [bool]; Raw = [string] })) `
                        -ReplaceWhere "Source = 'Gateway' AND LEFT(ExternalId, LEN(@g) + 1) = @g + N'/'" -ReplaceParameters @{ '@g' = $g.Id } `
                        -MaxDropPercent $MaxDropPercent -What "Gateway limits of $($g.Name)" -Setting 'GatewayLimitMaxDropPercent' -AllowDrop:$AllowDrop
                }
                catch {
                    if (Test-DropRefusal $_) { $refused = $true }
                    $why = if ((Get-AzureHttpCode $_) -eq 403) { 'refused (403): give the app API Management Service Reader on it' } else { $_.Exception.Message }
                    $skipped.Add("limits of $($g.Name) ($why)"); Write-Warning "The token limits of $($g.Name) were not loaded: $($_.Exception.Message)"
                }
            }
            $written += $limits
            if ($gateways.Count) { $done.Add("$limits token limit(s) of $($gateways.Count) gateway(s)") }
            $status = Get-GatewayRunOutcome -Parts $parts -Skipped $skipped.Count
            $detail = (@($(if ($done.Count) { "Loaded $($done -join ', ') for $($days.Count) day(s)." }), $(if ($skipped.Count) { 'Not loaded: ' + ($skipped -join '; ') + '.' })) | Where-Object { $_ }) -join ' '
            Complete-UsageRun $conn -RunId $runId -Status $status -Rows $written -Detail $detail
            Write-Line "$status. $detail"
            if ($status -eq 'Failed' -and -not $refused) { exit 1 }
        }
        catch {
            Complete-UsageRun $conn -RunId $runId -Status Failed -Rows $written -Detail $_.Exception.Message
            throw
        }
    }
    finally { $conn.Dispose() }
    # Refused by the delete guard: the limits kept their last good load, and the nightly run goes on.
    if ($refused) { exit $script:DropRefusedExitCode }
}

# Dot-sourcing (tests) loads the functions without running anything.
if ($MyInvocation.InvocationName -ne '.') { Invoke-Main }
