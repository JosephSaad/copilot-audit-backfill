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

    What these cannot give: who made a request. Azure Monitor has no user, key or application on these
    metrics; usage is by account, deployment and model. Cost is by account and meter, not by deployment.

    Azure Monitor keeps 93 days, and cost for an open month is an estimate until five days after it ends, so
    the nightly run reads a trailing window (-Days, 10 by default) and writes those days again. For a first
    load give -Days 90.

    Each part is read on its own: one the app may not read is left out and named in the run's detail, and the
    run ends Partial. The run is recorded in audit.ExternalUsageRun, which Admin > Health shows.

    THE APP'S ACCESS. On each subscription, the app registration needs Monitoring Reader (the accounts, their
    deployments and their metrics) and Cost Management Reader (cost); add Reader if listing the accounts is
    refused. Nothing here needs a Microsoft Graph permission or a key of the accounts.

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
    [switch] $AllowDrop
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ExternalUsage.Common.ps1')
$script:MaxDropGiven = $PSBoundParameters.ContainsKey('MaxDropPercent')   # read here: in Invoke-Main, $PSBoundParameters is its own

$script:AccountsVersion = '2024-10-01'
$script:MetricsVersion = '2023-10-01'
$script:CostVersion = '2023-11-01'
$script:CurrentMetrics = 'ModelRequests', 'InputTokens', 'OutputTokens', 'TotalTokens'
$script:OlderMetrics = 'AzureOpenAIRequests', 'ProcessedPromptTokens', 'GeneratedTokens', 'TokenTransaction'
# Which column of audit.FoundryUsageDay each metric fills.
$script:MetricColumn = @{
    ModelRequests = 'Requests'; InputTokens = 'InputTokens'; OutputTokens = 'OutputTokens'; TotalTokens = 'TotalTokens'
    AzureOpenAIRequests = 'Requests'; ProcessedPromptTokens = 'InputTokens'; GeneratedTokens = 'OutputTokens'; TokenTransaction = 'TotalTokens'
}

function Write-Line([string] $Text) { Write-Information ("{0:u} {1}" -f [datetime]::UtcNow, $Text) -InformationAction Continue }

function Get-ArmToken {
    <# An app-only token for Azure Resource Manager, by the certificate in LocalMachine\My or CurrentUser\My. #>
    param([string] $Tenant, [string] $Client, [string] $Thumbprint, [string] $Login, [string] $Arm)
    Get-AppOnlyToken -Tenant $Tenant -Client $Client -Thumbprint $Thumbprint -Login $Login -Resource $Arm
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
        if (-not $SqlServer) { throw 'Give -SqlServer, or SqlServer in the settings file.' }
    }

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
                            $sku = if ($d.PSObject.Properties['sku']) { $d.sku } else { $null }
                            $row = [pscustomobject]@{ SubscriptionId = $where.SubscriptionId; ResourceGroup = $where.ResourceGroup; Account = $where.Account; Deployment = "$($d.name)"
                                                      Kind = "$($a.kind)"; Location = "$($a.location)"
                                                      ModelName = $(if ($model) { "$($model.name)" }); ModelVersion = $(if ($model -and $model.PSObject.Properties['version']) { "$($model.version)" })
                                                      ModelPublisher = $(if ($model -and $model.PSObject.Properties['publisher']) { "$($model.publisher)" })
                                                      SkuName = $(if ($sku) { "$($sku.name)" }); Capacity = $(if ($sku -and $sku.PSObject.Properties['capacity']) { $sku.capacity }) }
                            $depRows.Add($row); $modelOf[$a.id]["$($d.name)".ToLowerInvariant()] = $row
                        }
                    }
                    $deployments += Write-UsageTable $conn 'audit.FoundryDeployment' (ConvertTo-UsageTable $depRows.ToArray() ([ordered]@{
                            SubscriptionId = [string]; ResourceGroup = [string]; Account = [string]; Deployment = [string]; Kind = [string]; Location = [string]; ModelName = [string]
                            ModelVersion = [string]; ModelPublisher = [string]; SkuName = [string]; Capacity = [int] })) -ReplaceWhere 'SubscriptionId = @s' -ReplaceParameters @{ '@s' = $sub } `
                        -MaxDropPercent $MaxDropPercent -What "Foundry deployments of $sub" -Setting 'FoundryDeploymentMaxDropPercent' -AllowDrop:$AllowDrop
                    $accountsRead += $accounts.Count
                }
                catch {
                    $skipped.Add("deployments of $sub ($($_.Exception.Message))"); Write-Warning "Deployments of $sub were not loaded: $($_.Exception.Message)"
                    # Refused by the delete guard (2.6.5), the accounts were read: their usage and cost still load.
                    if (-not (Test-DropRefusal $_)) { continue }
                    $accountsRead += $accounts.Count
                }

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
                    }
                    $usage += Write-UsageTable $conn 'audit.FoundryUsageDay' (ConvertTo-UsageTable $rows.ToArray() ([ordered]@{
                            SubscriptionId = [string]; ResourceGroup = [string]; Account = [string]; Deployment = [string]; ModelName = [string]; ModelVersion = [string]; Day = [datetime]
                            Requests = [long]; InputTokens = [long]; OutputTokens = [long]; TotalTokens = [long] })) `
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
            $written = $deployments + $usage + $cost
            $done.Add("$accountsRead account(s), $deployments deployment(s), $usage usage row(s), $cost cost row(s)")
            $status = if ($skipped.Count -eq 0) { 'Succeeded' } elseif ($written -gt 0) { 'Partial' } else { 'Failed' }
            $detail = (@("Loaded $($done -join ', ') for $($days.Count) day(s).", $(if ($skipped.Count) { 'Not loaded: ' + ($skipped -join '; ') + '.' })) | Where-Object { $_ }) -join ' '
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
