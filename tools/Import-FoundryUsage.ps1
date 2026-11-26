<#
.SYNOPSIS
    Loads the usage and cost of Microsoft Foundry and Azure OpenAI model deployments, for the subscriptions
    named, into the Copilot database. First plumbing (2.6.2): the tables and the load; no report reads them yet.

.DESCRIPTION
    Signs in to Azure Resource Manager as an app registration with a certificate (by default the pipeline's
    own app and certificate) and writes three tables (sql\Create-CopilotExternalUsageSchema.sql, which
    Install-CopilotAudit.ps1 runs):

      1. Deployments (audit.FoundryDeployment): every model deployment of every Azure OpenAI (kind OpenAI) and
         Foundry (kind AIServices) account in each subscription, as it stands: model, version, SKU, capacity.
      2. Usage (audit.FoundryUsageDay): requests and input, output and total tokens, per deployment and model
         per UTC day, from Azure Monitor's metrics of each account. The current metrics are read first
         (ModelRequests, InputTokens, OutputTokens, TotalTokens); an account that reports none is read from
         the older Azure OpenAI ones (AzureOpenAIRequests, ProcessedPromptTokens, GeneratedTokens,
         TokenTransaction).
      3. Cost (audit.FoundryCostDay): the actual cost of each account per meter per day, from Cost Management.

    And, from 3.3.0, what the account is and how it is run, for the Foundry pages: each account's network
    access, key sign-in, logging (its diagnostic settings) and tags, owner, cost center and app among them
    (audit.FoundryAccount); its content filters, and whether any is looser than Microsoft's default
    (audit.FoundryContentFilter, and the filter each deployment uses); when each deployed model retires, from
    the location's model list (audit.FoundryModelCatalog); how much of a provisioned (PTU) deployment's capacity
    was used each day (on audit.FoundryUsageDay); and every Azure OpenAI and Foundry account the app can see in
    any subscription, from Azure Resource Graph, marking those in subscriptions this load does not read
    (audit.FoundryAccountSeen). Each of these is read on its own; one Azure refuses is named in the run's
    detail without changing how the run ends, so an install with only the 2.6.2 roles still ends Succeeded.

    And, from 3.4.0, the wider AI bill (audit.AiCostDay, audit.AiBudget, audit.AiCostForecastDay): what every
    AI service costs in Azure, not only these accounts (Copilot Studio and the Power Platform billed to Azure,
    Microsoft 365 Copilot pay-as-you-go, AI Search, Machine Learning, Bot Service; the resource types are the
    AiCostResourceTypes setting), as actual cost and as amortized cost (a reservation spread over the days it
    covers); each scope's budgets; and Cost Management's forecast of the month's days still ahead. Read at each
    scope of AiCostScopes (a subscription, a billing account, an EA enrollment or department), by default each
    subscription above. Each is optional: one Azure refuses is named in the run's detail.

    What these cannot give: who made a request. Azure Monitor has no user, key or application on these
    metrics; usage is by account, deployment and model. Cost is by account and meter, not by deployment.

    Azure Monitor keeps 93 days, and cost for an open month is an estimate until five days after it ends, so
    the nightly run reads a trailing window (-Days, 10 by default) and writes those days again. For a first
    load give -Days 90.

    Each part is read on its own: one the app may not read is left out and named in the run's detail, and the
    run ends Partial. The run is recorded in audit.ExternalUsageRun, which Admin > Health shows.

    THE APP'S ACCESS. On each subscription, the app registration needs Monitoring Reader (the accounts, their
    deployments, their metrics and their diagnostic settings) and Cost Management Reader (cost); add Reader if
    listing the accounts or their content filters is refused. Reader on other subscriptions lets the load see
    AI accounts outside the list. Nothing here needs a Microsoft Graph permission or a key of the accounts.

    -CheckOnly signs in, tries each read once and writes nothing: one line per part, OK or what is missing and
    where, and exit code 0 when every part the load needs can be read.

    With no -SubscriptionId, everything comes from copilot-audit.settings.psd1: FoundrySubscriptions, and
    FoundryTenantId, FoundryClientId and FoundryCertificateThumbprint where the app is not the pipeline's
    (else TenantId, ClientId and CertificateThumbprint), and SqlServer, Database and TrustServerCertificate.

.PARAMETER SubscriptionId
    The Azure subscriptions to read, by id.

.PARAMETER Days
    How many complete UTC days to read, ending yesterday. Default 10; at most 90 (Azure Monitor keeps 93).

.PARAMETER MaxDropPercent
    The delete guard (2.6.5). A subscription's deployments are replaced whole each run. When Azure lists more
    than this percentage fewer than the table holds for it (FoundryDeploymentMaxDropPercent in the settings file,
    15 by default), its deployments are not replaced and the run ends Partial, saying why; its usage and cost
    load as usual. 0 turns the guard off.

.PARAMETER AllowDrop
    Replace the deployments even when the guard would refuse, once: after deployments were really removed.

.PARAMETER CheckOnly
    Sign in and try each read once, writing nothing (no database is needed): what the app may read, and what
    is missing where. Exit code 0 when every part the load needs can be read, 1 when one cannot.

.EXAMPLE
    .\tools\Import-FoundryUsage.ps1 -SubscriptionId 00000000-0000-0000-0000-000000000000 -TenantId contoso.onmicrosoft.com `
        -ClientId <app-id> -CertificateThumbprint <thumbprint> -SqlServer SQLPROD01 -Days 90
.EXAMPLE
    .\tools\Import-FoundryUsage.ps1          # as the nightly run does, from the settings file
#>
#Requires -Version 7.2
[CmdletBinding()]
param(
    [string[]] $SubscriptionId,
    [ValidateRange(1, 90)][int] $Days = 10,
    [string] $TenantId,
    [string] $ClientId,
    [string] $CertificateThumbprint,
    [string] $SqlServer,
    [string] $Database = 'CopilotAudit',
    [switch] $TrustServerCertificate,
    [string] $ConnectionString,
    [string] $SettingsPath = (Join-Path (Split-Path $PSScriptRoot -Parent) 'copilot-audit.settings.psd1'),
    # A token already in hand, in place of signing in (the tests); and the endpoints, for a sovereign cloud or a mock.
    [securestring] $AccessToken,
    [string] $ArmEndpoint = 'https://management.azure.com',
    [string] $LoginEndpoint = 'https://login.microsoftonline.com',
    [ValidateRange(0, 100)][double] $MaxDropPercent = 15,
    [switch] $AllowDrop,
    [switch] $CheckOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ExternalUsage.Common.ps1')
$script:MaxDropGiven = $PSBoundParameters.ContainsKey('MaxDropPercent')   # read here: in Invoke-Main, $PSBoundParameters is its own

$script:AccountsVersion = '2024-10-01'
$script:MetricsVersion = '2023-10-01'
$script:CostVersion = '2023-11-01'
$script:DiagnosticsVersion = '2021-05-01-preview'
$script:GraphVersion = '2022-10-01'
$script:BudgetVersion = '2023-05-01'
$script:PtuMetric = 'AzureOpenAIProvisionedManagedUtilizationV2'
$script:CurrentMetrics = 'ModelRequests', 'InputTokens', 'OutputTokens', 'TotalTokens'
$script:OlderMetrics = 'AzureOpenAIRequests', 'ProcessedPromptTokens', 'GeneratedTokens', 'TokenTransaction'
# Which column of audit.FoundryUsageDay each metric fills.
$script:MetricColumn = @{
    ModelRequests = 'Requests'; InputTokens = 'InputTokens'; OutputTokens = 'OutputTokens'; TotalTokens = 'TotalTokens'
    AzureOpenAIRequests = 'Requests'; ProcessedPromptTokens = 'InputTokens'; GeneratedTokens = 'OutputTokens'; TokenTransaction = 'TotalTokens'
}

function Write-Line([string] $Text) { Write-Information ("{0:u} {1}" -f [datetime]::UtcNow, $Text) -InformationAction Continue }

function ConvertTo-Base64Url([byte[]] $Bytes) { [Convert]::ToBase64String($Bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_') }

function New-ClientAssertion {
    <#
        The signed statement an app signs in to Entra with in place of a secret: a JWT signed with the
        certificate's private key (PS256), naming the certificate by the SHA-256 thumbprint of its DER encoding
        (x5t#S256), good for ten minutes. Needs the certificate with its private key; nothing leaves the machine
        but the signature.
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

function Get-ArmToken {
    <# An app-only token for Azure Resource Manager, by the certificate in LocalMachine\My or CurrentUser\My. #>
    param([string] $Tenant, [string] $Client, [string] $Thumbprint, [string] $Login, [string] $Arm)
    $clean = ("$Thumbprint" -replace '[^0-9A-Fa-f]', '').ToUpperInvariant()
    $cert = $null
    foreach ($store in 'Cert:\LocalMachine\My', 'Cert:\CurrentUser\My') { $cert = Get-Item -LiteralPath "$store\$clean" -ErrorAction SilentlyContinue; if ($cert) { break } }
    if (-not $cert) { throw "No certificate $clean in LocalMachine\My or CurrentUser\My." }
    $r = Invoke-RestMethod -Method Post -Uri "$($Login.TrimEnd('/'))/$Tenant/oauth2/v2.0/token" -ContentType 'application/x-www-form-urlencoded' -TimeoutSec 60 -Body @{
        client_id = $Client; scope = "$($Arm.TrimEnd('/'))/.default"; grant_type = 'client_credentials'
        client_assertion_type = 'urn:ietf:params:oauth:client-assertion-type:jwt-bearer'
        client_assertion = (New-ClientAssertion -Certificate $cert -Tenant $Tenant -Client $Client -Login $Login)
    }
    $r.access_token
}

function Invoke-Arm {
    <#
        One call of Azure Resource Manager with the token. Throttling (429) is waited out, for as long as Azure
        says, up to three times. Any other failure throws with Azure's own message.
    #>
    param([string] $Uri, [string] $BearerToken, [string] $Method = 'Get', $Body)
    for ($try = 1; ; $try++) {
        $p = @{ Uri = $Uri; Method = $Method; Headers = @{ Authorization = "Bearer $BearerToken" }; SkipHttpErrorCheck = $true; TimeoutSec = 180 }
        if ($null -ne $Body) { $p.Body = ($Body | ConvertTo-Json -Depth 20 -Compress); $p.ContentType = 'application/json' }
        $r = Invoke-WebRequest @p
        $status = [int] $r.StatusCode
        if ($status -eq 429 -and $try -le 3) {
            $wait = 30
            foreach ($h in 'x-ms-ratelimit-microsoft.costmanagement-qpu-retry-after', 'Retry-After') {
                if ($r.Headers.ContainsKey($h)) { $n = 0; if ([int]::TryParse("$(@($r.Headers[$h])[0])", [ref] $n) -and $n -gt 0) { $wait = [Math]::Min($n, 300); break } }
            }
            Write-Line "Azure asked to wait $wait s (throttled)."; Start-Sleep -Seconds $wait; continue
        }
        $parsed = $null
        if ($status -ne 204 -and $r.Content) { try { $parsed = $r.Content | ConvertFrom-Json -Depth 64 } catch { $parsed = $null } }
        if ($status -ge 400) {
            $message = "HTTP $status"
            if ($parsed -and $parsed.PSObject.Properties['error'] -and $parsed.error) { $message = "$($parsed.error.code): $($parsed.error.message)" }
            throw "Azure answered $status for $(([uri] $Uri).AbsolutePath): $message"
        }
        return $parsed
    }
}

function Get-ArmList {
    <# Every item of a list that comes a page at a time (value[] and nextLink). #>
    param([string] $Uri, [string] $BearerToken)
    $all = [System.Collections.Generic.List[object]]::new()
    for ($next = $Uri; $next; ) {
        $page = Invoke-Arm -Uri $next -BearerToken $BearerToken
        if ($null -eq $page) { break }
        foreach ($v in @($page.value)) { if ($v) { $all.Add($v) } }
        $next = if ($page.PSObject.Properties['nextLink']) { $page.nextLink } else { $null }
    }
    $all.ToArray()
}

function Split-ResourceId {
    <# An account's resource id as its subscription, resource group and name; $null for anything else. Pure, for the tests. #>
    param([string] $ResourceId)
    if ("$ResourceId" -match '(?i)^/subscriptions/(?<s>[^/]+)/resourceGroups/(?<g>[^/]+)/providers/Microsoft\.CognitiveServices/accounts/(?<a>[^/]+)') {
        [pscustomobject]@{ SubscriptionId = $Matches.s.ToLowerInvariant(); ResourceGroup = $Matches.g; Account = $Matches.a }
    }
}

function ConvertFrom-ArmMetric {
    <#
        Azure Monitor's answer as one flat row per metric, time series and day that has a total: the metric's
        name, the series' dimensions (their names in lower case, as Azure returns them in either case), the UTC
        day and the total. A metric Azure reports an error for is left out. Pure, for the tests.
    #>
    param($Response)
    $rows = [System.Collections.Generic.List[object]]::new()
    if ($null -eq $Response -or -not $Response.PSObject.Properties['value']) { return , @() }
    foreach ($m in @($Response.value)) {
        if ($null -eq $m) { continue }
        if ($m.PSObject.Properties['errorCode'] -and $m.errorCode -and $m.errorCode -ne 'Success') { continue }
        foreach ($series in @($m.timeseries)) {
            if ($null -eq $series) { continue }
            $dims = @{}
            if ($series.PSObject.Properties['metadatavalues']) {
                foreach ($d in @($series.metadatavalues)) { if ($d) { $dims["$($d.name.value)".ToLowerInvariant()] = "$($d.value)" } }
            }
            foreach ($point in @($series.data)) {
                if ($null -eq $point -or -not $point.PSObject.Properties['total'] -or $null -eq $point.total) { continue }
                $at = if ($point.timeStamp -is [datetime]) { $point.timeStamp.ToUniversalTime() } else { [datetimeoffset]::Parse("$($point.timeStamp)", [cultureinfo]::InvariantCulture).UtcDateTime }
                $rows.Add([pscustomobject]@{ Metric = "$($m.name.value)"; Dimensions = $dims; Day = $at.Date; Total = [double] $point.total })
            }
        }
    }
    , $rows.ToArray()
}

function Merge-FoundryUsage {
    <#
        Those flat rows as rows of audit.FoundryUsageDay for one account: one per deployment, model, version and
        day, with each metric in its column. A series that does not name its model (the older token metrics)
        takes the model its deployment has now. Pure, for the tests.
    #>
    param([AllowEmptyCollection()][object[]] $Metric, $Where, [hashtable] $ModelOf = @{})
    $byKey = [ordered]@{}
    foreach ($m in $Metric) {
        $column = $script:MetricColumn[$m.Metric]
        if (-not $column) { continue }
        $deployment = "$($m.Dimensions['modeldeploymentname'])"
        if (-not $deployment) { continue }
        $known = $ModelOf[$deployment.ToLowerInvariant()]
        $model = if ($m.Dimensions['modelname']) { "$($m.Dimensions['modelname'])" } elseif ($known) { "$($known.ModelName)" } else { '' }
        $version = if ($m.Dimensions['modelversion']) { "$($m.Dimensions['modelversion'])" } elseif ($known) { "$($known.ModelVersion)" } else { '' }
        $key = "$deployment|$model|$version|$($m.Day.ToString('yyyy-MM-dd'))".ToLowerInvariant()
        if (-not $byKey.Contains($key)) {
            $byKey[$key] = [pscustomobject]@{ SubscriptionId = $Where.SubscriptionId; ResourceGroup = $Where.ResourceGroup; Account = $Where.Account
                                              Deployment = $deployment; ModelName = $model; ModelVersion = $version; Day = $m.Day
                                              Requests = $null; InputTokens = $null; OutputTokens = $null; TotalTokens = $null }
        }
        # Several series can fall on one row (the same deployment by region, or by status code): they add up.
        $byKey[$key].$column = [long] ([double] "0$($byKey[$key].$column)" + $m.Total)
    }
    , @($byKey.Values)
}

function ConvertFrom-CostQuery {
    <#
        Cost Management's answer as rows: the resource, the meter, the day, the cost and its currency. Its
        columns are found by name, never by place: the cost column is Cost or PreTaxCost, and the day is a number
        (20261005) or a date. Pure, for the tests.
    #>
    param($Response)
    if ($null -eq $Response -or -not $Response.PSObject.Properties['properties'] -or $null -eq $Response.properties) { return , @() }
    $names = @($Response.properties.columns | ForEach-Object { "$($_.name)" })
    function At([string[]] $any) { foreach ($n in $any) { $i = [array]::FindIndex([string[]] $names, [Predicate[string]] { param($x) $x -ieq $n }); if ($i -ge 0) { return $i } }; -1 }
    $cost = At 'Cost', 'PreTaxCost', 'CostUSD'; $date = At 'UsageDate', 'Date'; $currency = At 'Currency'; $resource = At 'ResourceId'; $meter = At 'Meter', 'MeterName'
    if ($cost -lt 0 -or $date -lt 0 -or $resource -lt 0) { throw "Cost Management's answer has no cost, date or resource column (it has: $($names -join ', '))." }
    $rows = [System.Collections.Generic.List[object]]::new()
    foreach ($r in @($Response.properties.rows)) {
        if ($null -eq $r) { continue }
        $d = "$($r[$date])"
        $day = if ($d -match '^\d{8}$') { [datetime]::ParseExact($d, 'yyyyMMdd', [cultureinfo]::InvariantCulture) } else { ([datetime] $r[$date]).Date }
        $rows.Add([pscustomobject]@{ ResourceId = "$($r[$resource])"; Meter = $(if ($meter -ge 0) { "$($r[$meter])" } else { '' }); Day = $day
                                     Cost = [decimal] $r[$cost]; Currency = $(if ($currency -ge 0) { "$($r[$currency])" }) })
    }
    , $rows.ToArray()
}

function Get-CostQueryBody {
    <# The question asked of Cost Management: actual cost a day, by resource and meter, for Cognitive Services accounts. Pure, for the tests. #>
    param([datetime] $From, [datetime] $To)
    [ordered]@{
        type = 'ActualCost'; timeframe = 'Custom'
        timePeriod = [ordered]@{ from = $From.ToString('yyyy-MM-ddT00:00:00Z'); to = $To.ToString('yyyy-MM-ddT23:59:59Z') }
        dataset = [ordered]@{
            granularity = 'Daily'
            aggregation = [ordered]@{ totalCost = [ordered]@{ name = 'Cost'; function = 'Sum' } }
            grouping = @([ordered]@{ type = 'Dimension'; name = 'ResourceId' }, [ordered]@{ type = 'Dimension'; name = 'Meter' })
            filter = [ordered]@{ dimensions = [ordered]@{ name = 'ResourceType'; operator = 'In'; values = @('microsoft.cognitiveservices/accounts') } }
        }
    }
}

# The AI services' resource types the wider bill reads (3.4.0), and the name each is shown by.
$script:AiCostTypes = [ordered]@{
    'microsoft.cognitiveservices/accounts'         = 'Foundry and Azure OpenAI'
    'microsoft.machinelearningservices/workspaces' = 'Foundry hubs and Machine Learning'
    'microsoft.powerplatform/accounts'             = 'Power Platform'
    'microsoft.syntex/accounts'                    = 'Microsoft 365 Copilot pay-as-you-go'
    'microsoft.search/searchservices'              = 'Azure AI Search'
    'microsoft.botservice/botservices'             = 'Azure Bot Service'
}

function Get-AiService {
    <#
        The service a cost line is shown under: by its resource type, and, for the Power Platform's billing
        policies, by its meter (Copilot Studio's messages and credits are metered apart from Dataverse and the
        rest). Pure, for the tests.
    #>
    param([string] $ResourceType, [string] $Meter)
    $type = "$ResourceType".ToLowerInvariant()
    if ($type -eq 'microsoft.powerplatform/accounts' -and "$Meter" -match 'Copilot Studio|Copilot Credit|Copilot message|Virtual Agent') { return 'Copilot Studio' }
    if ($type -eq 'microsoft.syntex/accounts' -and "$Meter" -notmatch 'Copilot') { return 'SharePoint and Syntex pay-as-you-go' }
    if ($script:AiCostTypes.Contains($type)) { return $script:AiCostTypes[$type] }
    'Other AI services'
}

function Get-AiCostQueryBody {
    <#
        The question asked of Cost Management for the wider bill (3.4.0): cost a day, by resource and meter, of
        the AI services' resource types; ActualCost as billed, or AmortizedCost with reservations spread over
        their days. Pure, for the tests.
    #>
    param([datetime] $From, [datetime] $To, [ValidateSet('ActualCost', 'AmortizedCost')][string] $Type = 'ActualCost', [string[]] $ResourceTypes)
    [ordered]@{
        type = $Type; timeframe = 'Custom'
        timePeriod = [ordered]@{ from = $From.ToString('yyyy-MM-ddT00:00:00Z'); to = $To.ToString('yyyy-MM-ddT23:59:59Z') }
        dataset = [ordered]@{
            granularity = 'Daily'
            aggregation = [ordered]@{ totalCost = [ordered]@{ name = 'Cost'; function = 'Sum' } }
            grouping = @([ordered]@{ type = 'Dimension'; name = 'ResourceId' }, [ordered]@{ type = 'Dimension'; name = 'Meter' })
            filter = [ordered]@{ dimensions = [ordered]@{ name = 'ResourceType'; operator = 'In'; values = @($ResourceTypes) } }
        }
    }
}

function Get-AiForecastBody {
    <# The forecast asked for (3.4.0): the AI services' actual cost a day, from -From to -To, forecast only. Pure, for the tests. #>
    param([datetime] $From, [datetime] $To, [string[]] $ResourceTypes)
    [ordered]@{
        type = 'ActualCost'; timeframe = 'Custom'
        timePeriod = [ordered]@{ from = $From.ToString('yyyy-MM-ddT00:00:00Z'); to = $To.ToString('yyyy-MM-ddT23:59:59Z') }
        dataset = [ordered]@{
            granularity = 'Daily'
            aggregation = [ordered]@{ totalCost = [ordered]@{ name = 'Cost'; function = 'Sum' } }
            filter = [ordered]@{ dimensions = [ordered]@{ name = 'ResourceType'; operator = 'In'; values = @($ResourceTypes) } }
        }
        includeActualCost = $false; includeFreshPartialCost = $false
    }
}

function ConvertTo-AiCostRow {
    <#
        A cost line of the wider bill (ConvertFrom-CostQuery's) as a row of audit.AiCostDay: its scope, cost type,
        service, and the resource's subscription, group and name where its id gives them. Pure, for the tests.
    #>
    param($Line, [string] $Scope, [string] $CostType)
    $id = "$($Line.ResourceId)"
    $type = if ($id -match '/providers/([^/]+/[^/]+)/[^/]+$') { $Matches[1].ToLowerInvariant() } else { $null }
    $sub = if ($id -match '^/subscriptions/([^/]+)') { $Matches[1].ToLowerInvariant() } else { $null }
    $rg = if ($id -match '/resourcegroups/([^/]+)') { $Matches[1] } else { $null }
    $name = if ($id -match '/([^/]+)$') { $Matches[1] } else { $null }
    [pscustomobject]@{ Scope = $Scope; Day = $Line.Day; CostType = $CostType; Service = (Get-AiService $type $Line.Meter); ResourceType = $type
                       SubscriptionId = $sub; ResourceGroup = $rg; ResourceName = $name; Meter = $Line.Meter; Cost = [decimal] $Line.Cost; Currency = $Line.Currency }
}

function ConvertFrom-AiBudget {
    <# A scope's budgets (Microsoft.Consumption) as rows of audit.AiBudget. Pure, for the tests. #>
    param($Response, [string] $Scope)
    $rows = [System.Collections.Generic.List[object]]::new()
    foreach ($b in @(Get-Prop $Response 'value')) {
        if ($null -eq $b) { continue }
        $pr = Get-Prop $b 'properties'
        $tp = Get-Prop $pr 'timePeriod'; $cur = Get-Prop $pr 'currentSpend'; $fc = Get-Prop $pr 'forecastSpend'
        $start = Get-Prop $tp 'startDate'; $end = Get-Prop $tp 'endDate'
        $filter = Get-Prop $pr 'filter'
        $rows.Add([pscustomobject]@{ Scope = $Scope; Budget = "$(Get-Prop $b 'name')"; Category = "$(Get-Prop $pr 'category')"
                                     Amount = $(if ($null -ne (Get-Prop $pr 'amount')) { [decimal] (Get-Prop $pr 'amount') }); TimeGrain = "$(Get-Prop $pr 'timeGrain')"
                                     StartDate = $(if ($start) { ([datetime] $start).Date }); EndDate = $(if ($end) { ([datetime] $end).Date })
                                     CurrentSpend = $(if ($null -ne (Get-Prop $cur 'amount')) { [decimal] (Get-Prop $cur 'amount') })
                                     ForecastSpend = $(if ($null -ne (Get-Prop $fc 'amount')) { [decimal] (Get-Prop $fc 'amount') })
                                     Currency = $(if (Get-Prop $cur 'unit') { "$(Get-Prop $cur 'unit')" })
                                     Filter = $(if ($filter) { $j = $filter | ConvertTo-Json -Depth 8 -Compress; if ($j.Length -gt 4000) { $j.Substring(0, 3990) + ' (cut)' } else { $j } }) })
    }
    , $rows.ToArray()
}

function ConvertFrom-AiForecast {
    <# Cost Management's forecast as rows of audit.AiCostForecastDay: a day and its cost, the days' lines added up. Pure, for the tests. #>
    param($Response, [string] $Scope)
    if ($null -eq (Get-Prop $Response 'properties')) { return , @() }
    $names = @($Response.properties.columns | ForEach-Object { "$($_.name)" })
    $cost = [array]::FindIndex([string[]] $names, [Predicate[string]] { param($x) $x -in 'Cost', 'PreTaxCost', 'CostUSD' })
    $date = [array]::FindIndex([string[]] $names, [Predicate[string]] { param($x) $x -in 'UsageDate', 'Date' })
    $currency = [array]::FindIndex([string[]] $names, [Predicate[string]] { param($x) $x -eq 'Currency' })
    $status = [array]::FindIndex([string[]] $names, [Predicate[string]] { param($x) $x -eq 'CostStatus' })
    if ($cost -lt 0 -or $date -lt 0) { throw "Cost Management's forecast has no cost or date column (it has: $($names -join ', '))." }
    $byDay = [ordered]@{}
    foreach ($r in @($Response.properties.rows)) {
        if ($null -eq $r) { continue }
        if ($status -ge 0 -and "$($r[$status])" -ine 'Forecast') { continue }
        $d = "$($r[$date])"
        $day = if ($d -match '^\d{8}$') { [datetime]::ParseExact($d, 'yyyyMMdd', [cultureinfo]::InvariantCulture) } else { ([datetime] $r[$date]).Date }
        $k = $day.ToString('yyyyMMdd')
        if (-not $byDay.Contains($k)) { $byDay[$k] = [pscustomobject]@{ Scope = $Scope; Day = $day; Cost = [decimal] 0; Currency = $(if ($currency -ge 0) { "$($r[$currency])" }) } }
        $byDay[$k].Cost += [decimal] $r[$cost]
    }
    , @($byDay.Values)
}

function Get-AiCostScope {
    <#
        The scopes the wider bill is read at: the AiCostScopes setting (each an ARM scope: /subscriptions/<id>,
        /providers/Microsoft.Billing/billingAccounts/<id>, .../enrollmentAccounts/<id>, .../departments/<id>), else
        each subscription the load reads. A scope given without its leading / gets one. Pure, for the tests.
    #>
    param([string[]] $Configured, [string[]] $Subs)
    $list = if (@($Configured | Where-Object { $_ }).Count) { @($Configured | Where-Object { $_ } | ForEach-Object { '/' + "$_".Trim().Trim('/') }) }
            else { @($Subs | ForEach-Object { "/subscriptions/$_" }) }
    @($list | Select-Object -Unique)
}

function Get-FoundryMetric {
    <# One set of metrics for one account over the days, a series for every deployment, model and version. #>
    param([string] $Arm, [string] $ResourceId, [string[]] $Names, [string] $Filter, [datetime] $From, [datetime] $To, [string] $BearerToken)
    $q = "api-version=$script:MetricsVersion&metricnames=$([uri]::EscapeDataString($Names -join ','))&interval=P1D&aggregation=total&top=1000" +
         "&timespan=$([uri]::EscapeDataString($From.ToString('yyyy-MM-ddT00:00:00Z') + '/' + $To.AddDays(1).ToString('yyyy-MM-ddT00:00:00Z')))" +
         "&`$filter=$([uri]::EscapeDataString($Filter))"
    # Unrolled, a row at a time: the caller gathers them with @().
    $rows = ConvertFrom-ArmMetric (Invoke-Arm -Uri "$($Arm.TrimEnd('/'))$ResourceId/providers/Microsoft.Insights/metrics?$q" -BearerToken $BearerToken)
    foreach ($r in $rows) { $r }
}

function Get-TagValue {
    <# A tag's value by any of its usual names, whatever their case ("Owner", "owner", "cost-center", "CostCenter"). Pure, for the tests. #>
    param($Tags, [string[]] $Names)
    if ($null -eq $Tags) { return $null }
    foreach ($prop in $Tags.PSObject.Properties) {
        $key = ($prop.Name -replace '[^A-Za-z0-9]', '').ToLowerInvariant()
        foreach ($n in $Names) { if ($key -eq ($n -replace '[^A-Za-z0-9]', '').ToLowerInvariant() -and "$($prop.Value)") { return "$($prop.Value)" } }
    }
    $null
}

function Get-Prop {
    <# A property of an ARM object, or $null where it has none. #>
    param($Object, [string] $Name)
    if ($null -eq $Object) { return $null }
    $pp = $Object.PSObject.Properties[$Name]
    if ($pp) { $pp.Value } else { $null }
}

function ConvertTo-FoundryAccountRow {
    <#
        An account as Azure lists it, as a row of audit.FoundryAccount: how it is reached (public network,
        the default for networks not listed, private endpoints), whether keys are refused, its managed identity,
        and its tags, owner, cost center and app read from them. Logging is added by the caller. Pure, for the tests.
    #>
    param($Account, $Where)
    $pr = Get-Prop $Account 'properties'
    $acl = Get-Prop $pr 'networkAcls'
    $tags = Get-Prop $Account 'tags'
    $local = Get-Prop $pr 'disableLocalAuth'
    $endpoints = Get-Prop $pr 'privateEndpointConnections'
    [pscustomobject]@{
        SubscriptionId = $Where.SubscriptionId; ResourceGroup = $Where.ResourceGroup; Account = $Where.Account
        Kind = "$(Get-Prop $Account 'kind')"; Location = "$(Get-Prop $Account 'location')"; SkuName = "$(Get-Prop (Get-Prop $Account 'sku') 'name')"
        PublicNetworkAccess = "$(Get-Prop $pr 'publicNetworkAccess')"; NetworkDefaultAction = "$(Get-Prop $acl 'defaultAction')"
        PrivateEndpoints = @($endpoints | Where-Object { $_ }).Count
        DisableLocalAuth = $(if ($null -ne $local) { [bool] $local })
        IdentityType = "$(Get-Prop (Get-Prop $Account 'identity') 'type')"
        Owner = Get-TagValue $tags 'owner', 'createdby', 'contact'; CostCenter = Get-TagValue $tags 'costcenter', 'cost-center', 'cc'
        App = Get-TagValue $tags 'app', 'application', 'appname', 'workload'
        Tags = $(if ($tags -and @($tags.PSObject.Properties).Count) { $tags | ConvertTo-Json -Depth 4 -Compress })
        DiagnosticSettings = $null; LogCategories = $null
    }
}

function ConvertFrom-DiagnosticSetting {
    <# An account's diagnostic settings: how many there are, and the log categories (or groups) they send. Pure, for the tests. #>
    param($Response)
    $settings = @(Get-Prop $Response 'value' | Where-Object { $_ })
    $cats = [System.Collections.Generic.SortedSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($st in $settings) {
        foreach ($l in @(Get-Prop (Get-Prop $st 'properties') 'logs')) {
            if ($l -and (Get-Prop $l 'enabled')) { $c = "$((Get-Prop $l 'category'), (Get-Prop $l 'categoryGroup') | Where-Object { $_ } | Select-Object -First 1)"; if ($c) { [void] $cats.Add($c) } }
        }
    }
    [pscustomobject]@{ Count = $settings.Count; Categories = $(if ($cats.Count) { @($cats) -join ', ' }) }
}

function Test-RelaxedFilter {
    <#
        Whether a content filter (RAI policy) is looser than Microsoft's default: a filter turned off or not
        blocking, or one of the four harm categories (hate, sexual, violence, self-harm) blocking only high
        severity, where the default blocks from medium. Pure, for the tests.
    #>
    param($Policy)
    foreach ($f in @(Get-Prop (Get-Prop $Policy 'properties') 'contentFilters')) {
        if ($null -eq $f) { continue }
        if ((Get-Prop $f 'enabled') -eq $false -or (Get-Prop $f 'blocking') -eq $false) { return $true }
        $name = "$(Get-Prop $f 'name')".ToLowerInvariant() -replace '[^a-z]', ''
        if ($name -in 'hate', 'sexual', 'violence', 'selfharm' -and "$(Get-Prop $f 'severityThreshold')" -ieq 'High') { return $true }
    }
    $false
}

function ConvertTo-FoundryFilterRow {
    <# A content filter (RAI policy) of an account, as a row of audit.FoundryContentFilter. Pure, for the tests. #>
    param($Policy, $Where)
    $pr = Get-Prop $Policy 'properties'
    [pscustomobject]@{
        SubscriptionId = $Where.SubscriptionId; ResourceGroup = $Where.ResourceGroup; Account = $Where.Account; Policy = "$(Get-Prop $Policy 'name')"
        BasePolicy = "$(Get-Prop $pr 'basePolicyName')"; Mode = "$(Get-Prop $pr 'mode')"; Relaxed = Test-RelaxedFilter $Policy
        Detail = $(if ($pr) { @(Get-Prop $pr 'contentFilters') | ConvertTo-Json -Depth 6 -Compress })
    }
}

function ConvertFrom-FoundryModelList {
    <#
        A location's model list as rows of audit.FoundryModelCatalog, for the models deployed there only (-Deployed:
        "name|version" in lower case): the model's lifecycle and the day inference stops, and a row for each SKU that
        retires on a date of its own. Pure, for the tests.
    #>
    param($Response, [string] $Location, [System.Collections.Generic.HashSet[string]] $Deployed)
    $rows = [ordered]@{}
    foreach ($item in @(Get-Prop $Response 'value')) {
        $m = Get-Prop $item 'model'
        if ($null -eq $m) { continue }
        $name = "$(Get-Prop $m 'name')"; $version = "$(Get-Prop $m 'version')"
        if (-not $name -or -not $Deployed.Contains("$name|$version".ToLowerInvariant())) { continue }
        $dep = Get-Prop $m 'deprecation'
        $inference = Get-Prop $dep 'inference'
        $key = "$name|$version|".ToLowerInvariant()
        $rows[$key] = [pscustomobject]@{ Location = $Location; ModelName = $name; ModelVersion = $version; SkuName = ''
                                         LifecycleStatus = "$(Get-Prop $m 'lifecycleStatus')"; RetiresDate = $(if ($inference) { ([datetime] $inference).Date }) }
        foreach ($sku in @(Get-Prop $m 'skus')) {
            $when = Get-Prop $sku 'deprecationDate'
            if ($null -eq $sku -or -not $when) { continue }
            $sk = "$(Get-Prop $sku 'name')"
            $rows["$name|$version|$sk".ToLowerInvariant()] = [pscustomobject]@{ Location = $Location; ModelName = $name; ModelVersion = $version; SkuName = $sk
                                                                                LifecycleStatus = "$(Get-Prop $m 'lifecycleStatus')"; RetiresDate = ([datetime] $when).Date }
        }
    }
    , @($rows.Values)
}

function ConvertFrom-ArmMetricStat {
    <# Azure Monitor's answer for a metric asked with average and maximum: one row per deployment and day. Pure, for the tests. #>
    param($Response)
    $out = [System.Collections.Generic.List[object]]::new()
    foreach ($m in @(Get-Prop $Response 'value')) {
        if ($null -eq $m) { continue }
        if ((Get-Prop $m 'errorCode') -and (Get-Prop $m 'errorCode') -ne 'Success') { continue }
        foreach ($series in @(Get-Prop $m 'timeseries')) {
            if ($null -eq $series) { continue }
            $deployment = ''
            foreach ($d in @(Get-Prop $series 'metadatavalues')) { if ($d -and "$((Get-Prop $d 'name').value)" -ieq 'modeldeploymentname') { $deployment = "$(Get-Prop $d 'value')" } }
            foreach ($point in @(Get-Prop $series 'data')) {
                if ($null -eq $point) { continue }
                $avg = Get-Prop $point 'average'; $max = Get-Prop $point 'maximum'
                if ($null -eq $avg -and $null -eq $max) { continue }
                $at = if ($point.timeStamp -is [datetime]) { $point.timeStamp.ToUniversalTime() } else { [datetimeoffset]::Parse("$($point.timeStamp)", [cultureinfo]::InvariantCulture).UtcDateTime }
                $out.Add([pscustomobject]@{ Deployment = $deployment; Day = $at.Date; Average = $(if ($null -ne $avg) { [double] $avg }); Maximum = $(if ($null -ne $max) { [double] $max }) })
            }
        }
    }
    , $out.ToArray()
}

function Add-PtuUtilization {
    <#
        Puts each provisioned deployment's daily utilization on its usage rows: on the row of that deployment and
        day, or on a new row (the deployment's model) where the day had no requests. Pure, for the tests.
    #>
    param([System.Collections.Generic.List[object]] $Rows, [object[]] $Stat, $Where, [hashtable] $ModelOf = @{})
    foreach ($s in $Stat) {
        if (-not $s.Deployment) { continue }
        $match = @($Rows | Where-Object { $_.Account -eq $Where.Account -and $_.Deployment -ieq $s.Deployment -and $_.Day -eq $s.Day })
        if ($match.Count -eq 0) {
            $known = $ModelOf[$s.Deployment.ToLowerInvariant()]
            $row = [pscustomobject]@{ SubscriptionId = $Where.SubscriptionId; ResourceGroup = $Where.ResourceGroup; Account = $Where.Account; Deployment = $s.Deployment
                                      ModelName = $(if ($known) { "$($known.ModelName)" } else { '' }); ModelVersion = $(if ($known) { "$($known.ModelVersion)" } else { '' }); Day = $s.Day
                                      Requests = $null; InputTokens = $null; OutputTokens = $null; TotalTokens = $null; PtuUtilizationAvg = $null; PtuUtilizationMax = $null }
            $Rows.Add($row); $match = @($row)
        }
        foreach ($r in $match) {
            if (-not $r.PSObject.Properties['PtuUtilizationAvg']) { $r | Add-Member -NotePropertyName PtuUtilizationAvg -NotePropertyValue $null; $r | Add-Member -NotePropertyName PtuUtilizationMax -NotePropertyValue $null }
            $r.PtuUtilizationAvg = $(if ($null -ne $s.Average) { [math]::Round($s.Average, 4) }); $r.PtuUtilizationMax = $(if ($null -ne $s.Maximum) { [math]::Round($s.Maximum, 4) })
        }
    }
}

function Get-ResourceGraphBody {
    <# The question asked of Azure Resource Graph: every Azure OpenAI and Foundry account the app can see. Pure, for the tests. #>
    param([string] $SkipToken)
    $options = [ordered]@{ resultFormat = 'objectArray'; '$top' = 1000 }
    if ($SkipToken) { $options['$skipToken'] = $SkipToken }
    [ordered]@{
        query = "resources | where type =~ 'microsoft.cognitiveservices/accounts' and kind in~ ('OpenAI', 'AIServices') | project id, name, kind, location, subscriptionId"
        options = $options
    }
}

function ConvertFrom-ResourceGraph {
    <# Resource Graph's answer as rows of audit.FoundryAccountSeen, each marked Loaded when its subscription is one this load reads. Pure, for the tests. #>
    param($Response, [string[]] $Loaded)
    $set = [System.Collections.Generic.HashSet[string]]::new([string[]] @($Loaded | ForEach-Object { "$_".ToLowerInvariant() }))
    $rows = [System.Collections.Generic.List[object]]::new()
    foreach ($r in @(Get-Prop $Response 'data')) {
        $where = Split-ResourceId "$(Get-Prop $r 'id')"
        if ($null -eq $where) { continue }
        $rows.Add([pscustomobject]@{ SubscriptionId = $where.SubscriptionId; ResourceGroup = $where.ResourceGroup; Account = $where.Account
                                     Kind = "$(Get-Prop $r 'kind')"; Location = "$(Get-Prop $r 'location')"; Loaded = $set.Contains($where.SubscriptionId) })
    }
    , $rows.ToArray()
}

function Invoke-FoundryCheck {
    <#
        -CheckOnly: each read the load makes, tried once for each subscription, nothing written. One line a part:
        OK, or what is missing and where. Returns the lines; each has Ok, Needed (whether the load needs it) and Text.
    #>
    param([string[]] $Subs, [string] $Arm, [string] $BearerToken, [string[]] $AiScopes = @(), [string[]] $AiTypes = @())
    $lines = [System.Collections.Generic.List[object]]::new()
    function Line([bool] $ok, [bool] $needed, [string] $text) { $lines.Add([pscustomobject]@{ Ok = $ok; Needed = $needed; Text = $text }) }
    $yesterday = [datetime]::UtcNow.Date.AddDays(-1)
    foreach ($sub in $Subs) {
        $accounts = @()
        try {
            $accounts = @(Get-ArmList -Uri "$Arm/subscriptions/$sub/providers/Microsoft.CognitiveServices/accounts?api-version=$script:AccountsVersion" -BearerToken $BearerToken | Where-Object { "$($_.kind)" -in 'OpenAI', 'AIServices' })
            Line $true $true "Accounts in ${sub}: $($accounts.Count) Azure OpenAI or Foundry account(s)."
        }
        catch { Line $false $true "Accounts in ${sub}: not readable. Give the app Monitoring Reader (or Reader) on the subscription. Azure said: $($_.Exception.Message)"; continue }
        if ($accounts.Count -gt 0) {
            $a = $accounts[0]
            try { [void] (Get-ArmList -Uri "$Arm$($a.id)/deployments?api-version=$script:AccountsVersion" -BearerToken $BearerToken); Line $true $true "Deployments of $($a.name): readable." }
            catch { Line $false $true "Deployments of $($a.name): not readable. Monitoring Reader (or Reader) on $sub. Azure said: $($_.Exception.Message)" }
            try { [void] @(Get-FoundryMetric -Arm $Arm -ResourceId $a.id -Names 'ModelRequests' -Filter "ModelDeploymentName eq '*'" -From $yesterday -To $yesterday -BearerToken $BearerToken); Line $true $true "Usage metrics of $($a.name): readable." }
            catch { Line $false $true "Usage metrics of $($a.name): not readable. Monitoring Reader on $sub. Azure said: $($_.Exception.Message)" }
            try { [void] (Invoke-Arm -Uri "$Arm$($a.id)/providers/Microsoft.Insights/diagnosticSettings?api-version=$script:DiagnosticsVersion" -BearerToken $BearerToken); Line $true $false "Logging (diagnostic settings) of $($a.name): readable." }
            catch { Line $false $false "Logging of $($a.name): not readable (optional: the posture page shows it as unknown). Monitoring Reader on $sub." }
            try { [void] (Get-ArmList -Uri "$Arm$($a.id)/raiPolicies?api-version=$script:AccountsVersion" -BearerToken $BearerToken); Line $true $false "Content filters of $($a.name): readable." }
            catch { Line $false $false "Content filters of $($a.name): not readable (optional). Reader on $sub." }
        }
        try { [void] (Invoke-Arm -Uri "$Arm/subscriptions/$sub/providers/Microsoft.CostManagement/query?api-version=$script:CostVersion" -BearerToken $BearerToken -Method Post -Body (Get-CostQueryBody -From $yesterday -To $yesterday)); Line $true $true "Cost of ${sub}: readable." }
        catch { Line $false $true "Cost of ${sub}: not readable. Give the app Cost Management Reader on the subscription. Azure said: $($_.Exception.Message)" }
    }
    foreach ($scope in $AiScopes) {
        try { [void] (Invoke-Arm -Uri "$Arm$scope/providers/Microsoft.CostManagement/query?api-version=$script:CostVersion" -BearerToken $BearerToken -Method Post -Body (Get-AiCostQueryBody -From $yesterday -To $yesterday -Type AmortizedCost -ResourceTypes $AiTypes)); Line $true $false "The wider AI bill at ${scope}: readable." }
        catch { Line $false $false "The wider AI bill at ${scope}: not readable (optional: Cost Management Reader there, or Billing account reader for a billing scope). Azure said: $($_.Exception.Message)" }
        try { [void] (Invoke-Arm -Uri "$Arm$scope/providers/Microsoft.Consumption/budgets?api-version=$script:BudgetVersion" -BearerToken $BearerToken); Line $true $false "Budgets at ${scope}: readable." }
        catch { Line $false $false "Budgets at ${scope}: not readable (optional)." }
    }
    try {
        $seen = ConvertFrom-ResourceGraph (Invoke-Arm -Uri "$Arm/providers/Microsoft.ResourceGraph/resources?api-version=$script:GraphVersion" -BearerToken $BearerToken -Method Post -Body (Get-ResourceGraphBody)) $Subs
        $outside = @($seen | Where-Object { -not $_.Loaded })
        Line $true $false "AI accounts the app can see: $($seen.Count), $($outside.Count) in subscriptions not in the list$(if ($outside.Count) { ' (' + ((@($outside | ForEach-Object SubscriptionId | Select-Object -Unique)) -join ', ') + ')' })."
    }
    catch { Line $false $false "AI accounts across subscriptions: not readable (optional: Reader on more subscriptions shows accounts outside the list)." }
    , $lines.ToArray()
}

function Invoke-Main {
    $setting = @{}
    if (Test-Path -LiteralPath $SettingsPath) { $setting = Import-PowerShellDataFile -LiteralPath $SettingsPath }
    function Setting($k) { if ($setting.Contains($k) -and $setting[$k]) { $setting[$k] } }
    $script:MaxDropPercent = Resolve-MaxDropPercent -What 'Foundry deployments' -Given $MaxDropPercent -WasGiven $script:MaxDropGiven -Settings $setting
    if (-not $SubscriptionId) { $script:SubscriptionId = @(Setting 'FoundrySubscriptions') }
    $subs = @($SubscriptionId | ForEach-Object { "$_".Trim().ToLowerInvariant() } | Where-Object { $_ } | Select-Object -Unique)
    if ($subs.Count -eq 0) { throw 'Give -SubscriptionId, or set FoundrySubscriptions in the settings file.' }
    if (-not $TenantId) { $script:TenantId = if (Setting 'FoundryTenantId') { Setting 'FoundryTenantId' } else { Setting 'TenantId' } }
    if (-not $ClientId) { $script:ClientId = if (Setting 'FoundryClientId') { Setting 'FoundryClientId' } else { Setting 'ClientId' } }
    if (-not $CertificateThumbprint) { $script:CertificateThumbprint = if (Setting 'FoundryCertificateThumbprint') { Setting 'FoundryCertificateThumbprint' } else { Setting 'CertificateThumbprint' } }
    if (-not $ConnectionString) {
        if (-not $SqlServer) { $script:SqlServer = Setting 'SqlServer' }
        if (-not $PSBoundParameters.ContainsKey('Database') -and (Setting 'Database')) { $script:Database = Setting 'Database' }
        if (Setting 'TrustServerCertificate') { $script:TrustServerCertificate = $true }
    }

    $aiScopes = @(Get-AiCostScope -Configured @(Setting 'AiCostScopes') -Subs $subs)   # @(): one scope is still a list
    $aiTypes = if (@(Setting 'AiCostResourceTypes').Count) { @(Setting 'AiCostResourceTypes' | ForEach-Object { "$_".ToLowerInvariant() }) } else { @($script:AiCostTypes.Keys) }
    if ($CheckOnly) {
        $bearer = if ($AccessToken) { [System.Net.NetworkCredential]::new('', $AccessToken).Password }
                  else {
                      if (-not ($TenantId -and $ClientId -and $CertificateThumbprint)) { throw 'Signing in needs -TenantId, -ClientId and -CertificateThumbprint (or the settings file''s).' }
                      Get-ArmToken -Tenant $TenantId -Client $ClientId -Thumbprint $CertificateThumbprint -Login $LoginEndpoint -Arm $ArmEndpoint
                  }
        Write-Line "Signed in to Azure$(if ($ClientId) { " as $ClientId" }). Checking $($subs.Count) subscription(s); nothing is written."
        $lines = Invoke-FoundryCheck -Subs $subs -Arm $ArmEndpoint.TrimEnd('/') -BearerToken $bearer -AiScopes $aiScopes -AiTypes $aiTypes
        foreach ($l in $lines) { Write-Line ("{0,-7} {1}" -f $(if ($l.Ok) { 'OK' } elseif ($l.Needed) { 'MISSING' } else { 'NOTE' }), $l.Text) }
        $missing = @($lines | Where-Object { -not $_.Ok -and $_.Needed }).Count
        Write-Line $(if ($missing) { "$missing part(s) the load needs cannot be read: grant what is named above." } else { 'Every part the load needs can be read.' })
        if ($missing) { exit 1 }
        return
    }
    if (-not $ConnectionString -and -not $SqlServer) { throw 'Give -SqlServer, or SqlServer in the settings file.' }

    $conn = New-UsageConnection -SqlServer $SqlServer -Database $Database -TrustServerCertificate:$TrustServerCertificate -ConnectionString $ConnectionString
    try {
        if (-not (Test-UsageSchema $conn)) { throw "The tables are not in $($conn.Database) yet: run Install-CopilotAudit.ps1 from this release first." }
        $days = Get-UsageDay -Days $Days
        $from = $days[0]; $to = $days[-1]
        $runId = Start-UsageRun $conn -Source Foundry -Scope ($subs -join ', ') -FromDay $from -ToDay $to
        $written = 0; $done = [System.Collections.Generic.List[string]]::new(); $skipped = [System.Collections.Generic.List[string]]::new()
        try {
            $bearer = if ($AccessToken) { [System.Net.NetworkCredential]::new('', $AccessToken).Password }
                      else {
                          if (-not ($TenantId -and $ClientId -and $CertificateThumbprint)) { throw 'Signing in needs -TenantId, -ClientId and -CertificateThumbprint (or the settings file''s).' }
                          Get-ArmToken -Tenant $TenantId -Client $ClientId -Thumbprint $CertificateThumbprint -Login $LoginEndpoint -Arm $ArmEndpoint
                      }
            $arm = $ArmEndpoint.TrimEnd('/')
            $deployments = 0; $usage = 0; $cost = 0; $accountsRead = 0
            $notes = [System.Collections.Generic.List[string]]::new()   # the 3.3.0 parts not read: named, the run's end unchanged
            $postured = 0; $filters = 0; $cataloged = 0
            $deployedAt = @{}   # location -> "model|version" of what is deployed there
            foreach ($sub in $subs) {
                # 1. The accounts and their deployments.
                $accounts = @()
                try {
                    $accounts = @(Get-ArmList -Uri "$arm/subscriptions/$sub/providers/Microsoft.CognitiveServices/accounts?api-version=$script:AccountsVersion" -BearerToken $bearer |
                                  Where-Object { "$($_.kind)" -in 'OpenAI', 'AIServices' })
                    $depRows = [System.Collections.Generic.List[object]]::new()
                    $modelOf = @{}   # account id -> deployment -> its model, for the metrics that do not name it
                    foreach ($a in $accounts) {
                        $where = Split-ResourceId $a.id
                        if (-not $where) { continue }
                        $modelOf[$a.id] = @{}
                        foreach ($d in @(Get-ArmList -Uri "$arm$($a.id)/deployments?api-version=$script:AccountsVersion" -BearerToken $bearer)) {
                            $model = if ($d.PSObject.Properties['properties'] -and $d.properties.PSObject.Properties['model']) { $d.properties.model } else { $null }
                            $rai = Get-Prop (Get-Prop $d 'properties') 'raiPolicyName'
                            $sku = if ($d.PSObject.Properties['sku']) { $d.sku } else { $null }
                            $row = [pscustomobject]@{ SubscriptionId = $where.SubscriptionId; ResourceGroup = $where.ResourceGroup; Account = $where.Account; Deployment = "$($d.name)"
                                                      Kind = "$($a.kind)"; Location = "$($a.location)"
                                                      ModelName = $(if ($model) { "$($model.name)" }); ModelVersion = $(if ($model -and $model.PSObject.Properties['version']) { "$($model.version)" })
                                                      ModelPublisher = $(if ($model -and $model.PSObject.Properties['publisher']) { "$($model.publisher)" })
                                                      SkuName = $(if ($sku) { "$($sku.name)" }); Capacity = $(if ($sku -and $sku.PSObject.Properties['capacity']) { $sku.capacity })
                                                      RaiPolicy = $(if ($rai) { "$rai" }) }
                            $depRows.Add($row); $modelOf[$a.id]["$($d.name)".ToLowerInvariant()] = $row
                            if ($row.ModelName) {
                                $loc = "$($a.location)".ToLowerInvariant()
                                if (-not $deployedAt.ContainsKey($loc)) { $deployedAt[$loc] = [System.Collections.Generic.HashSet[string]]::new() }
                                [void] $deployedAt[$loc].Add("$($row.ModelName)|$($row.ModelVersion)".ToLowerInvariant())
                            }
                        }
                    }
                    $deployments += Write-UsageTable $conn 'audit.FoundryDeployment' (ConvertTo-UsageTable $depRows.ToArray() ([ordered]@{
                            SubscriptionId = [string]; ResourceGroup = [string]; Account = [string]; Deployment = [string]; Kind = [string]; Location = [string]; ModelName = [string]
                            ModelVersion = [string]; ModelPublisher = [string]; SkuName = [string]; Capacity = [int]; RaiPolicy = [string] })) -ReplaceWhere 'SubscriptionId = @s' -ReplaceParameters @{ '@s' = $sub } `
                        -MaxDropPercent $MaxDropPercent -What "Foundry deployments of $sub" -Setting 'FoundryDeploymentMaxDropPercent' -AllowDrop:$AllowDrop
                    $accountsRead += $accounts.Count
                }
                catch {
                    $skipped.Add("deployments of $sub ($($_.Exception.Message))"); Write-Warning "Deployments of $sub were not loaded: $($_.Exception.Message)"
                    # Refused by the delete guard, the accounts were read: their usage and cost still load.
                    if (-not (Test-DropRefusal $_)) { continue }
                    $accountsRead += $accounts.Count
                }

                # 1b. What each account is and how it is run (3.3.0): posture, tags, logging and content filters.
                try {
                    $accRows = [System.Collections.Generic.List[object]]::new(); $filterRows = [System.Collections.Generic.List[object]]::new(); $noLogs = 0; $noFilters = 0
                    foreach ($a in $accounts) {
                        $where = Split-ResourceId $a.id
                        if (-not $where) { continue }
                        $row = ConvertTo-FoundryAccountRow $a $where
                        try { $diag = ConvertFrom-DiagnosticSetting (Invoke-Arm -Uri "$arm$($a.id)/providers/Microsoft.Insights/diagnosticSettings?api-version=$script:DiagnosticsVersion" -BearerToken $bearer); $row.DiagnosticSettings = $diag.Count; $row.LogCategories = $diag.Categories }
                        catch { $noLogs++; Write-Verbose "Logging of $($a.name) not read: $($_.Exception.Message)" }
                        $accRows.Add($row)
                        try { foreach ($pol in @(Get-ArmList -Uri "$arm$($a.id)/raiPolicies?api-version=$script:AccountsVersion" -BearerToken $bearer)) { if ($pol) { $filterRows.Add((ConvertTo-FoundryFilterRow $pol $where)) } } }
                        catch { $noFilters++; Write-Verbose "Content filters of $($a.name) not read: $($_.Exception.Message)" }
                    }
                    $postured += Write-UsageTable $conn 'audit.FoundryAccount' (ConvertTo-UsageTable $accRows.ToArray() ([ordered]@{
                            SubscriptionId = [string]; ResourceGroup = [string]; Account = [string]; Kind = [string]; Location = [string]; SkuName = [string]; PublicNetworkAccess = [string]
                            NetworkDefaultAction = [string]; PrivateEndpoints = [int]; DisableLocalAuth = [bool]; IdentityType = [string]; Owner = [string]; CostCenter = [string]; App = [string]
                            Tags = [string]; DiagnosticSettings = [int]; LogCategories = [string] })) -ReplaceWhere 'SubscriptionId = @s' -ReplaceParameters @{ '@s' = $sub }
                    $filters += Write-UsageTable $conn 'audit.FoundryContentFilter' (ConvertTo-UsageTable $filterRows.ToArray() ([ordered]@{
                            SubscriptionId = [string]; ResourceGroup = [string]; Account = [string]; Policy = [string]; BasePolicy = [string]; Mode = [string]; Relaxed = [bool]; Detail = [string] })) `
                        -ReplaceWhere 'SubscriptionId = @s' -ReplaceParameters @{ '@s' = $sub }
                    if ($noLogs) { $notes.Add("logging of $noLogs account(s) in $sub") }
                    if ($noFilters) { $notes.Add("content filters of $noFilters account(s) in $sub") }
                }
                catch { $notes.Add("posture of $sub ($($_.Exception.Message))"); Write-Warning "The accounts' posture in $sub was not loaded: $($_.Exception.Message)" }

                # 2. Usage, account by account: the current metrics, else the older ones.
                try {
                    $rows = [System.Collections.Generic.List[object]]::new()
                    foreach ($a in $accounts) {
                        $where = Split-ResourceId $a.id
                        if (-not $where) { continue }
                        $flat = @()
                        try { $flat = @(Get-FoundryMetric -Arm $arm -ResourceId $a.id -Names $script:CurrentMetrics -Filter "ModelDeploymentName eq '*' and ModelName eq '*' and ModelVersion eq '*'" -From $from -To $to -BearerToken $bearer) }
                        catch { Write-Verbose "$($a.name) has no current model metrics: $($_.Exception.Message)" }
                        if (@($flat | Where-Object { $_.Total -gt 0 }).Count -eq 0) {
                            $flat = @(Get-FoundryMetric -Arm $arm -ResourceId $a.id -Names $script:OlderMetrics[0] -Filter "ModelDeploymentName eq '*' and ModelName eq '*' and ModelVersion eq '*'" -From $from -To $to -BearerToken $bearer) +
                                    @(Get-FoundryMetric -Arm $arm -ResourceId $a.id -Names $script:OlderMetrics[1..3] -Filter "ModelDeploymentName eq '*' and ModelVersion eq '*'" -From $from -To $to -BearerToken $bearer)
                        }
                        foreach ($r in (Merge-FoundryUsage -Metric $flat -Where $where -ModelOf $modelOf[$a.id])) { $rows.Add($r) }
                        # A provisioned (PTU) deployment's daily use of its capacity (3.3.0).
                        if (@($modelOf[$a.id].Values | Where-Object { "$($_.SkuName)" -like 'Provisioned*' }).Count) {
                            try {
                                $q = "api-version=$script:MetricsVersion&metricnames=$script:PtuMetric&interval=P1D&aggregation=average,maximum&top=1000" +
                                     "&timespan=$([uri]::EscapeDataString($from.ToString('yyyy-MM-ddT00:00:00Z') + '/' + $to.AddDays(1).ToString('yyyy-MM-ddT00:00:00Z')))" +
                                     "&`$filter=$([uri]::EscapeDataString("ModelDeploymentName eq '*'"))"
                                Add-PtuUtilization -Rows $rows -Stat (ConvertFrom-ArmMetricStat (Invoke-Arm -Uri "$arm$($a.id)/providers/Microsoft.Insights/metrics?$q" -BearerToken $bearer)) -Where $where -ModelOf $modelOf[$a.id]
                            }
                            catch { $notes.Add("provisioned use of $($a.name)"); Write-Verbose "PTU use of $($a.name) not read: $($_.Exception.Message)" }
                        }
                    }
                    $usage += Write-UsageTable $conn 'audit.FoundryUsageDay' (ConvertTo-UsageTable $rows.ToArray() ([ordered]@{
                            SubscriptionId = [string]; ResourceGroup = [string]; Account = [string]; Deployment = [string]; ModelName = [string]; ModelVersion = [string]; Day = [datetime]
                            Requests = [long]; InputTokens = [long]; OutputTokens = [long]; TotalTokens = [long]; PtuUtilizationAvg = [decimal]; PtuUtilizationMax = [decimal] })) `
                        -ReplaceWhere 'SubscriptionId = @s AND [Day] BETWEEN @f AND @t' -ReplaceParameters @{ '@s' = $sub; '@f' = $from; '@t' = $to }
                }
                catch { $skipped.Add("usage of $sub ($($_.Exception.Message))"); Write-Warning "Usage of $sub was not loaded: $($_.Exception.Message)" }

                # 3. Cost: one question for the subscription and the days, its pages followed.
                try {
                    $mine = [System.Collections.Generic.HashSet[string]]::new([string[]] @($accounts | ForEach-Object { "$($_.id)".ToLowerInvariant() }))
                    $rows = [System.Collections.Generic.List[object]]::new()
                    $body = Get-CostQueryBody -From $from -To $to
                    for ($next = "$arm/subscriptions/$sub/providers/Microsoft.CostManagement/query?api-version=$script:CostVersion"; $next; ) {
                        $page = Invoke-Arm -Uri $next -BearerToken $bearer -Method Post -Body $body
                        foreach ($c in (ConvertFrom-CostQuery $page)) {
                            # Only the accounts read above: the same resource type holds Speech, Vision and the rest.
                            if (-not $mine.Contains($c.ResourceId.ToLowerInvariant())) { continue }
                            $where = Split-ResourceId $c.ResourceId
                            $rows.Add([pscustomobject]@{ SubscriptionId = $where.SubscriptionId; ResourceGroup = $where.ResourceGroup; Account = $where.Account
                                                         Meter = $c.Meter; Day = $c.Day; Cost = $c.Cost; Currency = $c.Currency })
                        }
                        $next = if ($page -and $page.PSObject.Properties['properties'] -and $page.properties.PSObject.Properties['nextLink']) { $page.properties.nextLink } else { $null }
                    }
                    # A resource and meter billed in two lines on one day is one row: its costs add up.
                    $summed = @($rows | Group-Object { "$($_.ResourceGroup)|$($_.Account)|$($_.Meter)|$($_.Day.ToString('yyyyMMdd'))".ToLowerInvariant() } | ForEach-Object {
                            $first = $_.Group[0]
                            [pscustomobject]@{ SubscriptionId = $first.SubscriptionId; ResourceGroup = $first.ResourceGroup; Account = $first.Account; Meter = $first.Meter; Day = $first.Day
                                               Cost = [decimal] ($_.Group | Measure-Object Cost -Sum).Sum; Currency = $first.Currency } })
                    $cost += Write-UsageTable $conn 'audit.FoundryCostDay' (ConvertTo-UsageTable $summed ([ordered]@{
                            SubscriptionId = [string]; ResourceGroup = [string]; Account = [string]; Meter = [string]; Day = [datetime]; Cost = [decimal]; Currency = [string] })) `
                        -ReplaceWhere 'SubscriptionId = @s AND [Day] BETWEEN @f AND @t' -ReplaceParameters @{ '@s' = $sub; '@f' = $from; '@t' = $to }
                }
                catch { $skipped.Add("cost of $sub ($($_.Exception.Message))"); Write-Warning "Cost of $sub was not loaded: $($_.Exception.Message)" }
            }

            # 4. When each deployed model retires, from each location's model list (3.3.0).
            foreach ($loc in $deployedAt.Keys) {
                try {
                    $listSub = $subs[0]
                    $cat = ConvertFrom-FoundryModelList (Invoke-Arm -Uri "$arm/subscriptions/$listSub/providers/Microsoft.CognitiveServices/locations/$loc/models?api-version=$script:AccountsVersion" -BearerToken $bearer) $loc $deployedAt[$loc]
                    $cataloged += Write-UsageTable $conn 'audit.FoundryModelCatalog' (ConvertTo-UsageTable $cat ([ordered]@{
                            Location = [string]; ModelName = [string]; ModelVersion = [string]; SkuName = [string]; LifecycleStatus = [string]; RetiresDate = [datetime] })) `
                        -ReplaceWhere 'Location = @l' -ReplaceParameters @{ '@l' = $loc }
                }
                catch { $notes.Add("model retirement in $loc"); Write-Verbose "The model list of $loc not read: $($_.Exception.Message)" }
            }
            # 4b. The wider AI bill (3.4.0): every AI service's actual and amortized cost, budgets and the forecast, at each scope.
            $aiCost = 0; $budgets = 0; $forecast = 0
            foreach ($scope in $aiScopes) {
                foreach ($type in 'ActualCost', 'AmortizedCost') {
                    $kind = $type.Replace('Cost', '')
                    try {
                        $rows = [System.Collections.Generic.List[object]]::new()
                        $body = Get-AiCostQueryBody -From $from -To $to -Type $type -ResourceTypes $aiTypes
                        for ($next = "$arm$scope/providers/Microsoft.CostManagement/query?api-version=$script:CostVersion"; $next; ) {
                            $page = Invoke-Arm -Uri $next -BearerToken $bearer -Method Post -Body $body
                            foreach ($c in (ConvertFrom-CostQuery $page)) { $rows.Add((ConvertTo-AiCostRow $c $scope $kind)) }
                            $next = if ($page -and $page.PSObject.Properties['properties'] -and $page.properties.PSObject.Properties['nextLink']) { $page.properties.nextLink } else { $null }
                        }
                        $aiCost += Write-UsageTable $conn 'audit.AiCostDay' (ConvertTo-UsageTable $rows.ToArray() ([ordered]@{
                                Scope = [string]; Day = [datetime]; CostType = [string]; Service = [string]; ResourceType = [string]; SubscriptionId = [string]; ResourceGroup = [string]
                                ResourceName = [string]; Meter = [string]; Cost = [decimal]; Currency = [string] })) `
                            -ReplaceWhere 'Scope = @s AND CostType = @c AND [Day] BETWEEN @f AND @t' -ReplaceParameters @{ '@s' = $scope; '@c' = $kind; '@f' = $from; '@t' = $to }
                    }
                    catch { $notes.Add("$($kind.ToLowerInvariant()) cost of the AI services at $scope"); Write-Verbose "The wider bill ($type) at $scope not read: $($_.Exception.Message)" }
                }
                try {
                    $budgets += Write-UsageTable $conn 'audit.AiBudget' (ConvertTo-UsageTable (ConvertFrom-AiBudget (Invoke-Arm -Uri "$arm$scope/providers/Microsoft.Consumption/budgets?api-version=$script:BudgetVersion" -BearerToken $bearer) $scope) ([ordered]@{
                            Scope = [string]; Budget = [string]; Category = [string]; Amount = [decimal]; TimeGrain = [string]; StartDate = [datetime]; EndDate = [datetime]
                            CurrentSpend = [decimal]; ForecastSpend = [decimal]; Currency = [string]; Filter = [string] })) -ReplaceWhere 'Scope = @s' -ReplaceParameters @{ '@s' = $scope }
                }
                catch { $notes.Add("budgets at $scope"); Write-Verbose "Budgets at $scope not read: $($_.Exception.Message)" }
                try {
                    $today = [datetime]::UtcNow.Date; $monthEnd = [datetime]::new($today.Year, $today.Month, 1).AddMonths(1).AddDays(-1)
                    $fc = ConvertFrom-AiForecast (Invoke-Arm -Uri "$arm$scope/providers/Microsoft.CostManagement/forecast?api-version=$script:CostVersion" -BearerToken $bearer -Method Post -Body (Get-AiForecastBody -From $today -To $monthEnd -ResourceTypes $aiTypes)) $scope
                    $forecast += Write-UsageTable $conn 'audit.AiCostForecastDay' (ConvertTo-UsageTable $fc ([ordered]@{ Scope = [string]; Day = [datetime]; Cost = [decimal]; Currency = [string] })) `
                        -ReplaceWhere 'Scope = @s' -ReplaceParameters @{ '@s' = $scope }
                }
                catch { $notes.Add("the forecast at $scope"); Write-Verbose "The forecast at $scope not read: $($_.Exception.Message)" }
            }

            # 5. Every AI account the app can see, in any subscription (3.3.0).
            $seenCount = $null; $outside = 0
            try {
                $seen = [System.Collections.Generic.List[object]]::new(); $skip = $null
                do {
                    $page = Invoke-Arm -Uri "$arm/providers/Microsoft.ResourceGraph/resources?api-version=$script:GraphVersion" -BearerToken $bearer -Method Post -Body (Get-ResourceGraphBody -SkipToken $skip)
                    foreach ($r in (ConvertFrom-ResourceGraph $page $subs)) { $seen.Add($r) }
                    $skip = Get-Prop $page '$skipToken'
                } while ($skip)
                $unique = @($seen | Group-Object { "$($_.SubscriptionId)|$($_.ResourceGroup)|$($_.Account)".ToLowerInvariant() } | ForEach-Object { $_.Group[0] })
                [void] (Write-UsageTable $conn 'audit.FoundryAccountSeen' (ConvertTo-UsageTable $unique ([ordered]@{
                        SubscriptionId = [string]; ResourceGroup = [string]; Account = [string]; Kind = [string]; Location = [string]; Loaded = [bool] })) -ReplaceWhere '1 = 1')
                $seenCount = $unique.Count; $outside = @($unique | Where-Object { -not $_.Loaded }).Count
            }
            catch { $notes.Add('AI accounts across subscriptions (Reader on them)'); Write-Verbose "Resource Graph not read: $($_.Exception.Message)" }
            $written = $deployments + $usage + $cost
            $done.Add("$accountsRead account(s), $deployments deployment(s), $usage usage row(s), $cost cost row(s)")
            $done.Add("the posture of $postured account(s), $filters content filter(s), $cataloged model lifecycle row(s)" +
                      $(if ($null -ne $seenCount) { "; $seenCount AI account(s) seen, $outside in subscriptions not loaded" }))
            $done.Add("the wider AI bill: $aiCost cost row(s), $budgets budget(s), $forecast forecast day(s) at $($aiScopes.Count) scope(s)")
            $status = if ($skipped.Count -eq 0) { 'Succeeded' } elseif ($written -gt 0) { 'Partial' } else { 'Failed' }
            $detail = (@("Loaded $($done -join ', ') for $($days.Count) day(s).", $(if ($skipped.Count) { 'Not loaded: ' + ($skipped -join '; ') + '.' }),
                         $(if ($notes.Count) { 'Not read (optional): ' + ($notes -join '; ') + '.' })) | Where-Object { $_ }) -join ' '
            Complete-UsageRun $conn -RunId $runId -Status $status -Rows $written -Detail $detail
            Write-Line "$status. $detail"
            if ($status -eq 'Failed') { exit 1 }
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
