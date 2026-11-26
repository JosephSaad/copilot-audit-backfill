<#
    Tests tools\Import-GatewayTokenUsage.ps1, the loader of the AI gateway's token counts and token limits.

      1  Dot-sourced, nothing touched: the gateway's resource id, dimensions found whatever their case and
         spacing, the question asked of Log Analytics for AppMetrics and customMetrics, its answer as rows,
         token rows with and without dimensions, the token-limit elements of a policy, and how a run ends.
      2  End to end, against a stand-in for Azure Resource Manager and the Log Analytics query API on this
         machine, into a throwaway database of its own (GwTest_<process id>) on a local SQL Server, dropped
         afterwards: a workspace (AppMetrics), Application Insights with and without a workspace (customMetrics),
         a 403 on the workspace (Partial), the delete guard (exit code 3, then -AllowDrop) and -CheckOnly writing
         nothing. Part 2 is skipped, saying so, when no SQL Server answers.

    The SQL Server: SQLIT_SERVER (default localhost) with Windows authentication, or SQL authentication as sa
    when SQLIT_PASSWORD is set (as tests/Test-SqlIntegration.ps1 takes it). No existing database is touched.

        pwsh -NoProfile -File tests/Test-GatewayUsage.ps1
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot 'TestHelpers.ps1')
function Test-Throw([scriptblock] $Do) { try { & $Do | Out-Null; $false } catch { $true } }
$loader = Join-Path $root 'tools' 'Import-GatewayTokenUsage.ps1'

# ---- 1. The loader's pieces ----------------------------------------------------------------------
Write-Host "`n1. tools\Import-GatewayTokenUsage.ps1, dot-sourced" -ForegroundColor Cyan
. $loader

$gwId = '/subscriptions/AAAA1111-0000-0000-0000-000000000000/resourceGroups/rg-ai/providers/Microsoft.ApiManagement/service/GW-East'
$gw = Split-GatewayId $gwId
Assert-That ($gw.Name -eq 'GW-East' -and $gw.SubscriptionId -eq 'aaaa1111-0000-0000-0000-000000000000' -and $gw.Id -eq $gwId.ToLowerInvariant()) 'a gateway''s resource id as its subscription, name and id (in lower case)'
Assert-That ($null -eq (Split-GatewayId '/subscriptions/x/resourceGroups/rg/providers/Microsoft.Web/sites/app') -and $null -ne (Split-GatewayId ($gwId + '/'))) 'anything but an API Management service is not one; a trailing / is'

$dims = '{"business-unit":"Finance","API ID":"chat","Service Name":"gw-east","n":5}' | ConvertFrom-Json
Assert-That ((Get-DimensionValue $dims 'Business unit') -eq 'Finance' -and (Get-DimensionValue $dims 'api id') -eq 'chat' -and (Get-DimensionValue @{ BusinessUnit = 'HR' } 'Business unit') -eq 'HR') `
    'a dimension is found whatever its case and spacing, in an object or a hashtable'
Assert-That ((Get-DimensionValue $dims 'Agent') -eq '' -and (Get-DimensionValue $dims '') -eq '' -and (Get-DimensionValue $null 'x') -eq '' -and (Get-DimensionValue $dims 'n') -eq '5') 'one not there, or not named, is empty; a number is text'

$from = [datetime] '2026-11-20'; $to = [datetime] '2026-11-22'
$qa = Get-TokenQuery -Table AppMetrics -From $from -To $to
$qc = Get-TokenQuery -Table customMetrics -From $from -To $to
Assert-That ($qa -match '^AppMetrics' -and $qa -like '*TimeGenerated >= datetime(2026-11-20) and TimeGenerated < datetime(2026-11-23)*' -and $qa -like '*sum(Sum)*sum(ItemCount)*tostring(Properties)*' -and
             $qa -like "*Name in ('Total Tokens', 'Prompt Tokens', 'Completion Tokens')*") 'AppMetrics is asked for the three token metrics over whole UTC days, summed with their records, by its dimensions'
Assert-That ($qc -match '^customMetrics' -and $qc -like '*timestamp >= datetime(2026-11-20)*' -and $qc -like '*sum(valueSum)*sum(valueCount)*' -and $qc -like '*tostring(cloud_RoleName)*tostring(customDimensions)*') `
    'customMetrics by its own column names'
Assert-That (Test-Throw { Get-TokenQuery -Table Requests -From $from -To $to }) 'and no other table'

$answer = @'
{"tables":[{"name":"PrimaryResult","columns":[{"name":"Day","type":"string"},{"name":"Metric","type":"string"},{"name":"Role","type":"string"},{"name":"Dimensions","type":"string"},{"name":"Total","type":"real"},{"name":"Records","type":"long"}],
 "rows":[["2026-11-20","Total Tokens","westeurope","{\"Service Name\":\"gw-east\",\"Business unit\":\"Finance\",\"Model\":\"gpt-4o\",\"Agent\":\"claims-bot\",\"API ID\":\"chat\"}",1500,3],
         ["2026-11-20","Prompt Tokens","westeurope","{\"Service Name\":\"gw-east\",\"Business unit\":\"Finance\",\"Model\":\"gpt-4o\",\"Agent\":\"claims-bot\",\"API ID\":\"chat\"}",1000,3],
         ["2026-11-20","Completion Tokens","westeurope","{\"Service Name\":\"gw-east\",\"Business unit\":\"Finance\",\"Model\":\"gpt-4o\",\"Agent\":\"claims-bot\",\"API ID\":\"chat\"}",500,3],
         ["2026-11-20","Total Tokens","westeurope","{\"service name\":\"GW-EAST\",\"BusinessUnit\":\"Finance\",\"model\":\"gpt-4o\",\"agent\":\"claims-bot\",\"api id\":\"chat\"}",100,1],
         ["2026-11-21","Total Tokens","westeurope","{\"Service ID\":\"/subscriptions/a/resourceGroups/r/providers/Microsoft.ApiManagement/service/gw-west\"}",42,2],
         ["2026-11-21","Requests","westeurope","{}",7,7]]}]}
'@ | ConvertFrom-Json
$flat = ConvertFrom-LogQuery $answer
Assert-That ($flat.Count -eq 6 -and $flat[0].Metric -eq 'Total Tokens' -and $flat[0].Total -eq 1500 -and $flat[0].Records -eq 3) "the query's answer as a row per row, each value under its column's name ($($flat.Count))"
Assert-That ((ConvertFrom-LogQuery $null).Count -eq 0 -and (ConvertFrom-LogQuery ('{"tables":[]}' | ConvertFrom-Json)).Count -eq 0) 'no answer is no rows'

$rows = Merge-GatewayToken -Rows $flat -BusinessUnitDimension 'Business unit' -ModelDimension 'Model' -AgentDimension 'Agent'
$fin = @($rows | Where-Object { $_.BusinessUnit -eq 'Finance' })
Assert-That ($rows.Count -eq 2 -and $fin.Count -eq 1 -and $fin[0].Gateway -eq 'gw-east' -and $fin[0].Model -eq 'gpt-4o' -and $fin[0].Agent -eq 'claims-bot' -and $fin[0].Api -eq 'chat' -and
             $fin[0].Day -eq [datetime] '2026-11-20') 'with dimensions: a row per gateway, day, business unit, model, agent and API, the same names in any case being one row'
Assert-That ($fin[0].PromptTokens -eq 1000 -and $fin[0].CompletionTokens -eq 500 -and $fin[0].TotalTokens -eq 1600 -and $fin[0].Calls -eq 4) 'each metric in its column, two records of one row adding up, Calls the records of Total Tokens'
$west = @($rows | Where-Object { $_.Gateway -eq 'gw-west' })
Assert-That ($west.Count -eq 1 -and $west[0].BusinessUnit -eq '' -and $west[0].Model -eq '' -and $west[0].Agent -eq '' -and $west[0].Api -eq '' -and $west[0].TotalTokens -eq 42 -and
             $null -eq $west[0].PromptTokens -and $west[0].Calls -eq 2) 'without dimensions: empty business unit, model, agent and API, the gateway from the last part of Service ID; a metric of another name is left out'
$bare = Merge-GatewayToken -Rows @([pscustomobject]@{ Day = '2026-11-20'; Metric = 'Prompt Tokens'; Role = 'gw-role'; Dimensions = ''; Total = 30; Records = 2 },
                                   [pscustomobject]@{ Day = '2026-11-20'; Metric = 'Completion Tokens'; Role = 'gw-role'; Dimensions = $null; Total = 12; Records = 2 }) -BusinessUnitDimension 'Business unit' -DefaultGateway 'gw-only'
Assert-That ($bare.Count -eq 1 -and $bare[0].Gateway -eq 'gw-only' -and $bare[0].TotalTokens -eq 42 -and $bare[0].Calls -eq 2) 'a record naming no gateway takes the one gateway given; with no Total Tokens the total is prompt plus completion, and Calls the prompt records'
$role = Merge-GatewayToken -Rows @([pscustomobject]@{ Day = [datetime] '2026-11-20'; Metric = 'Total Tokens'; Role = 'gw-role'; Dimensions = '{not json'; Total = 1; Records = 1 }) -BusinessUnitDimension 'Business unit'
Assert-That ($role.Count -eq 1 -and $role[0].Gateway -eq 'gw-role' -and $role[0].Day -eq [datetime] '2026-11-20') 'else the role name; dimensions that are not JSON are none, and a day the reader made a date is read too'
$long = Merge-GatewayToken -Rows @([pscustomobject]@{ Day = '2026-11-20'; Metric = 'Total Tokens'; Role = ''; Dimensions = ('{"Business unit":"' + ('x' * 300) + '"}'); Total = 1; Records = 1 }) -BusinessUnitDimension 'Business unit'
Assert-That ($long[0].BusinessUnit.Length -eq 256 -and $long[0].Gateway -eq '') 'a value longer than its column is cut to the column; no gateway at all is empty'
Assert-That ((Merge-GatewayToken -Rows @() -BusinessUnitDimension 'x').Count -eq 0) 'and no rows are no rows'

$policy = @'
<policies>
  <inbound>
    <base />
    <llm-token-limit counter-key="@(context.Request.Headers.GetValueOrDefault(&quot;x-business-unit&quot;,&quot;none&quot;))" tokens-per-minute="10000" token-quota="5000000" token-quota-period="monthly" estimate-prompt-tokens="false" />
    <llm-emit-token-metric namespace="ai"><dimension name="Business unit" value="@(context.Request.Headers.GetValueOrDefault(&quot;x-business-unit&quot;,&quot;&quot;))" /><dimension name="API ID" /></llm-emit-token-metric>
    <azure-openai-token-limit counter-key="@(context.Subscription.Id)" token-quota="@(context.Variables.GetValueOrDefault&lt;int&gt;(&quot;q&quot;))" token-quota-period="Daily" estimate-prompt-tokens="true" />
  </inbound>
  <outbound><base /></outbound>
</policies>
'@
$scopeId = "$($gw.Id)/policies/policy"
$limits = ConvertFrom-TokenLimitPolicy -PolicyXml $policy -ScopeId $scopeId -Name 'GW-East / all APIs'
$rate = @($limits | Where-Object Kind -eq 'GatewayRate'); $quota = @($limits | Where-Object Kind -eq 'GatewayQuota')
Assert-That ($limits.Count -eq 3 -and $rate.Count -eq 1 -and $quota.Count -eq 2) "a policy with two token-limit elements: a rate and a quota from the first, a quota from the second ($($limits.Count))"
Assert-That ($rate[0].Amount -eq 10000 -and $rate[0].Period -eq 'Minute' -and $rate[0].ExternalId -eq "$scopeId#0#GatewayRate" -and $rate[0].Source -eq 'Gateway' -and $rate[0].Unit -eq 'Tokens' -and
             $rate[0].PerUser -eq $true -and $rate[0].ScopeType -eq 'CounterKey' -and $rate[0].Name -eq 'GW-East / all APIs') 'the rate: tokens per minute, per counter key, its id the policy''s, the element''s place and the kind'
Assert-That ($rate[0].Scope -eq '@(context.Request.Headers.GetValueOrDefault("x-business-unit","none"))' -and $rate[0].Raw -like '<llm-token-limit*tokens-per-minute="10000"*') 'the counter key as its expression reads, the element itself kept'
Assert-That ($quota[0].Amount -eq 5000000 -and $quota[0].Period -eq 'Monthly' -and $quota[0].ExternalId -eq "$scopeId#0#GatewayQuota") 'the quota over its period, the period in its documented spelling'
Assert-That ($quota[1].ExternalId -eq "$scopeId#1#GatewayQuota" -and $null -eq $quota[1].Amount -and $quota[1].Period -eq 'Daily' -and $quota[1].Scope -eq '@(context.Subscription.Id)' -and $quota[1].Raw -like '<azure-openai-token-limit*') `
    'azure-openai-token-limit too; an amount that is an expression is left empty'
Assert-That ((ConvertFrom-TokenLimitPolicy -PolicyXml '<policies><inbound><base /></inbound></policies>' -ScopeId 's' -Name 'n').Count -eq 0 -and (ConvertFrom-TokenLimitPolicy -PolicyXml '' -ScopeId 's' -Name 'n').Count -eq 0) 'a policy with no token limit, or none at all, is no rows'
Assert-That (Test-Throw { ConvertFrom-TokenLimitPolicy -PolicyXml '<policies><inbound>' -ScopeId 's' -Name 'n' }) 'a policy that is not XML is refused'
$odd = ConvertFrom-TokenLimitPolicy -PolicyXml '<policies><inbound><llm-token-limit counter-key="k" token-quota="10" token-quota-period="@(&quot;Daily&quot;)" /></inbound></policies>' -ScopeId 's' -Name 'n'
Assert-That ($odd.Count -eq 1 -and $null -eq $odd[0].Period -and $odd[0].Amount -eq 10) 'a period given as an expression is left empty'

$scopes = Get-GatewayPolicyScope $gw @(('{"name":"Starter","properties":{"displayName":"Starter plan"}}' | ConvertFrom-Json), ('{"name":"bare"}' | ConvertFrom-Json)) @(('{"name":"chat","properties":{"displayName":"Chat API"}}' | ConvertFrom-Json), ('{"name":"chat;rev=2"}' | ConvertFrom-Json))
Assert-That (($scopes.Name -join '|') -eq 'GW-East / all APIs|GW-East / product Starter plan|GW-East / product bare|GW-East / API Chat API' -and $scopes[1].Path -eq "$($gw.Id)/products/starter/policies" -and $scopes[3].Path -eq "$($gw.Id)/apis/chat/policies") `
    'the scopes read: the service, each product and each API (not an older revision), each with a readable name'
Assert-That ((Get-GatewayRunOutcome -Parts 2 -Skipped 0) -eq 'Succeeded' -and (Get-GatewayRunOutcome -Parts 2 -Skipped 1) -eq 'Partial' -and (Get-GatewayRunOutcome -Parts 1 -Skipped 1) -eq 'Failed') `
    'a run succeeds with nothing left out, is partial with a part left out, fails with every part left out'
Assert-That ($script:DropGuardSetting['Gateway limits'] -eq 'GatewayLimitMaxDropPercent' -and (Resolve-MaxDropPercent -What 'Gateway limits' -Given 15 -WasGiven $false -Settings @{ GatewayLimitMaxDropPercent = 40 }) -eq 40) `
    'its delete guard''s percentage is GatewayLimitMaxDropPercent in the settings file'
$src = Get-Content -LiteralPath $loader -Raw
Assert-That ($src -match 'exit \$script:DropRefusedExitCode' -and -not ($src.ToCharArray() | Where-Object { [int] $_ -gt 127 })) 'it ends with the guard''s exit code when refused, and is ASCII'

# ---- 2. End to end -------------------------------------------------------------------------------
Write-Host "`n2. End to end, against a stand-in for Azure, into a throwaway database" -ForegroundColor Cyan
$server = if ($env:SQLIT_SERVER) { $env:SQLIT_SERVER } else { 'localhost' }
$testDb = "GwTest_$PID"
function New-TestConnString([string] $Catalog) {
    $b = [System.Data.SqlClient.SqlConnectionStringBuilder]::new()
    $b['Data Source'] = $server; $b['Initial Catalog'] = $Catalog; $b['Encrypt'] = $true; $b['TrustServerCertificate'] = $true; $b['Connect Timeout'] = 5
    if ($env:SQLIT_PASSWORD) { $b['User ID'] = $(if ($env:SQLIT_USER) { $env:SQLIT_USER } else { 'sa' }); $b['Password'] = $env:SQLIT_PASSWORD } else { $b['Integrated Security'] = $true }
    $b.ConnectionString
}
function Invoke-TestSql([string] $Sql, [string] $Catalog = $testDb, [switch] $Table) {
    $c = [System.Data.SqlClient.SqlConnection]::new((New-TestConnString $Catalog)); $c.Open()
    try {
        $cmd = $c.CreateCommand(); $cmd.CommandText = $Sql; $cmd.CommandTimeout = 120
        if ($Table) { $t = [System.Data.DataTable]::new(); $t.Load($cmd.ExecuteReader()); , $t } else { $cmd.ExecuteScalar() }
    }
    finally { $c.Dispose() }
}
$sqlUp = $false
try { [void] (Invoke-TestSql 'SELECT 1' -Catalog 'master'); $sqlUp = $true } catch { Write-Host "  SKIP  no SQL Server answers at $server ($($_.Exception.Message)): part 2 not run." -ForegroundColor Yellow }

if ($sqlUp) {
    $probe = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0); $probe.Start(); $port = $probe.LocalEndpoint.Port; $probe.Stop()
    $extDays = @(0..2 | ForEach-Object { [datetime]::UtcNow.Date.AddDays(-3 + $_).ToString('yyyy-MM-dd') })   # the three days a -Days 3 load covers
    $state = [hashtable]::Synchronized(@{ FewerLimits = $false; Queries = [System.Collections.ArrayList]::Synchronized([System.Collections.ArrayList]::new()) })
    $gwPath = "/subscriptions/aaaa1111-0000-0000-0000-000000000000/resourceGroups/rg-ai/providers/Microsoft.ApiManagement/service/gw-east"
    $deniedGw = "/subscriptions/bbbb2222-0000-0000-0000-000000000000/resourceGroups/rg-ai/providers/Microsoft.ApiManagement/service/gw-locked"
    $aiBase = '/subscriptions/aaaa1111-0000-0000-0000-000000000000/resourceGroups/rg-ai/providers/Microsoft.Insights/components'
    $mockScript = {
        param([int] $Port, [string[]] $Days, $State, [string] $Gw, [string] $Ai, [string] $Policy)
        $listener = [System.Net.HttpListener]::new(); $listener.Prefixes.Add("http://localhost:$Port/"); $listener.Start()
        function Send($ctx, [int] $status, [string] $body = '{}') {
            $ctx.Response.StatusCode = $status
            $bytes = [System.Text.Encoding]::UTF8.GetBytes($body)
            $ctx.Response.ContentType = 'application/json'; $ctx.Response.ContentLength64 = $bytes.Length
            if ($bytes.Length) { $ctx.Response.OutputStream.Write($bytes, 0, $bytes.Length) }
            $ctx.Response.Close()
        }
        function Answer([object[]] $Rows) {
            @{ tables = @(@{ name = 'PrimaryResult'; columns = @(@{ name = 'Day'; type = 'string' }, @{ name = 'Metric'; type = 'string' }, @{ name = 'Role'; type = 'string' },
                                                                   @{ name = 'Dimensions'; type = 'string' }, @{ name = 'Total'; type = 'real' }, @{ name = 'Records'; type = 'long' }); rows = $Rows }) } | ConvertTo-Json -Depth 9
        }
        $denied = '{"error":{"code":"AuthorizationFailed","message":"The client does not have authorization."}}'
        while ($listener.IsListening) {
            $ctx = $listener.GetContext(); $rq = $ctx.Request; $path = $rq.Url.AbsolutePath
            try {
                if ($path -eq '/stop') { Send $ctx 200; break }
                if ("$($rq.Headers['Authorization'])" -ne 'Bearer gw-token') { Send $ctx 401 '{"error":{"code":"InvalidAuthenticationToken","message":"no"}}'; continue }
                if ($path -like '/la/*') {
                    $body = [System.IO.StreamReader]::new($rq.InputStream).ReadToEnd() | ConvertFrom-Json
                    [void] $State.Queries.Add("$path|$($body.query)|$($body.timespan)")
                    if ($path -eq '/la/v1/workspaces/ws-denied/query') { Send $ctx 403 '{"error":{"code":"InsufficientAccessError","message":"The provided credentials have insufficient access."}}'; continue }
                    if ($path -eq '/la/v1/workspaces/ws-good/query' -and $body.query -like 'AppMetrics*') {
                        $d = '{"Service Name":"gw-east","Business unit":"Finance","Model":"gpt-4o","API ID":"chat","Region":"West Europe"}'
                        $h = '{"Service Name":"gw-east","Business unit":"HR","API ID":"chat"}'
                        Send $ctx 200 (Answer @(@($Days[0], 'Total Tokens', 'r', $d, 1500, 3), @($Days[0], 'Prompt Tokens', 'r', $d, 1000, 3), @($Days[0], 'Completion Tokens', 'r', $d, 500, 3),
                                                @($Days[1], 'Total Tokens', 'r', $h, 70, 1)))
                        continue
                    }
                    if ($path -eq "/la/v1$Ai/classic/query" -and $body.query -like 'customMetrics*') {
                        Send $ctx 200 (Answer @(@($Days[1], 'Total Tokens', 'gw-east', '{}', 900, 9), @($Days[1], 'Prompt Tokens', 'gw-east', '{}', 600, 9)))
                        continue
                    }
                    Send $ctx 400 '{"error":{"code":"BadArgumentError","message":"Failed to resolve table"}}'; continue
                }
                if ($path -like '/arm/*') {
                    $p = $path.Substring(4)
                    if ($p -like '/subscriptions/bbbb*') { Send $ctx 403 $denied; continue }
                    if ($p -eq "$Ai/classic") { Send $ctx 200 '{"properties":{"ApplicationId":"classic","IngestionMode":"ApplicationInsights"}}'; continue }
                    if ($p -eq "$Ai/modern") { Send $ctx 200 '{"properties":{"WorkspaceResourceId":"/subscriptions/aaaa1111-0000-0000-0000-000000000000/resourceGroups/rg-ai/providers/Microsoft.OperationalInsights/workspaces/law-ai"}}'; continue }
                    if ($p -eq '/subscriptions/aaaa1111-0000-0000-0000-000000000000/resourceGroups/rg-ai/providers/Microsoft.OperationalInsights/workspaces/law-ai') { Send $ctx 200 '{"properties":{"customerId":"ws-good"}}'; continue }
                    $g = $Gw.ToLowerInvariant()
                    if ($p -eq "$g/policies") {
                        $xml = if ($State.FewerLimits) { '<policies><inbound><base /><llm-token-limit counter-key="@(context.Subscription.Id)" tokens-per-minute="500" estimate-prompt-tokens="false" /></inbound></policies>' } else { $Policy }
                        Send $ctx 200 (@{ value = @(@{ id = "$Gw/policies/policy"; name = 'policy'; properties = @{ format = 'xml'; value = $xml } }); count = 1 } | ConvertTo-Json -Depth 9); continue
                    }
                    if ($p -eq "$g/products") { Send $ctx 200 '{"value":[{"name":"starter","properties":{"displayName":"Starter"}},{"name":"unlimited","properties":{"displayName":"Unlimited"}}]}'; continue }
                    if ($p -eq "$g/products/starter/policies") {
                        Send $ctx 200 (@{ value = @(@{ name = 'policy'; properties = @{ format = 'xml'; value = '<policies><inbound><base /><azure-openai-token-limit counter-key="@(context.Subscription.Id)" tokens-per-minute="2000" estimate-prompt-tokens="false" /></inbound></policies>' } }) } | ConvertTo-Json -Depth 9); continue
                    }
                    if ($p -eq "$g/products/unlimited/policies") { Send $ctx 404 '{"error":{"code":"ResourceNotFound","message":"Policy not found"}}'; continue }
                    if ($p -eq "$g/apis") { Send $ctx 200 '{"value":[{"name":"chat","properties":{"displayName":"Chat"}},{"name":"chat;rev=2","properties":{"displayName":"Chat"}}]}'; continue }
                    if ($p -eq "$g/apis/chat/policies") { Send $ctx 200 '{"value":[{"name":"policy","properties":{"format":"xml","value":"<policies><inbound><base /></inbound></policies>"}}]}'; continue }
                    Send $ctx 404 '{"error":{"code":"NotFound","message":"no such thing"}}'; continue
                }
                Send $ctx 404
            }
            catch { try { Send $ctx 500 (@{ message = "$_" } | ConvertTo-Json) } catch { Write-Verbose "$_" } }
        }
        $listener.Stop()
    }
    $mock = Start-ThreadJob -ScriptBlock $mockScript -ArgumentList $port, $extDays, $state, $gwPath, $aiBase, $policy
    $created = $false
    try {
        $up = $false
        foreach ($try in 1..50) { try { [void] (Invoke-WebRequest -Uri "http://localhost:$port/nothing" -SkipHttpErrorCheck -TimeoutSec 2); $up = $true; break } catch { Start-Sleep -Milliseconds 200 } }
        Assert-That $up 'the stand-in for Azure is listening'
        [void] (Invoke-TestSql "IF DB_ID(N'$testDb') IS NULL CREATE DATABASE [$testDb];" -Catalog 'master'); $created = $true
        [void] (Invoke-TestSql (Get-Content -LiteralPath (Join-Path $root 'sql' 'Create-CopilotExternalUsageSchema.sql') -Raw))
        $conn = New-TestConnString $testDb
        $token = [securestring]::new(); foreach ($ch in 'gw-token'.ToCharArray()) { $token.AppendChar($ch) }
        $noSettings = Join-Path ([System.IO.Path]::GetTempPath()) "no-settings-gw-$PID.psd1"   # not there: everything comes from the command line
        $common = @{ ConnectionString = $conn; AccessToken = $token; ArmEndpoint = "http://localhost:$port/arm"; LogAnalyticsEndpoint = "http://localhost:$port/la"; SettingsPath = $noSettings; Days = 3 }
        function Get-LastRun { (Invoke-TestSql "SELECT TOP (1) Status, Scope, RowsWritten, Detail, FromDay, ToDay FROM audit.ExternalUsageRun WHERE Source = 'Gateway' ORDER BY RunId DESC;" -Table).Rows[0] }
        function Invoke-Loader([hashtable] $Extra) { $a = @{} + $common; foreach ($k in $Extra.Keys) { $a[$k] = $Extra[$k] }; $global:LASTEXITCODE = 0; $out = & $loader @a 6>&1 3>&1 2>&1 | Out-String -Width 4096; [pscustomobject]@{ Code = $LASTEXITCODE; Out = $out } }

        # A workspace (AppMetrics), with the gateway's limits.
        $r1 = Invoke-Loader @{ GatewayService = $gwPath; WorkspaceId = 'ws-good'; ModelDimension = 'Model' }
        $run = Get-LastRun
        Assert-That ($r1.Code -eq 0 -and "$($run.Status)" -eq 'Succeeded' -and [int] $run.RowsWritten -eq 6 -and "$($run.Scope)" -eq 'gw-east, workspace ws-good' -and ([datetime] $run.FromDay).ToString('yyyy-MM-dd') -eq $extDays[0]) `
            "a workspace and a gateway: succeeded, the run says so ($($run.Status), $($run.RowsWritten) rows: $($run.Detail)) $(if ($r1.Code -ne 0) { $r1.Out })"
        $q = "$($state.Queries[-1])"
        Assert-That ($q -like '/la/v1/workspaces/ws-good/query|AppMetrics*' -and $q -like "*$($extDays[0])T00:00:00Z/$([datetime]::UtcNow.Date.ToString('yyyy-MM-dd'))T00:00:00Z") 'the workspace is asked for AppMetrics over the three days'
        $tok = Invoke-TestSql 'SELECT Gateway, [Day], BusinessUnit, Model, Agent, Api, PromptTokens, CompletionTokens, TotalTokens, Calls FROM audit.GatewayTokenDay ORDER BY [Day], BusinessUnit;' -Table
        $f = $tok.Rows[0]; $h = $tok.Rows[1]
        Assert-That ($tok.Rows.Count -eq 2 -and $f.Gateway -eq 'gw-east' -and $f.BusinessUnit -eq 'Finance' -and $f.Model -eq 'gpt-4o' -and $f.Agent -eq '' -and $f.Api -eq 'chat' -and
                     [long] $f.PromptTokens -eq 1000 -and [long] $f.CompletionTokens -eq 500 -and [long] $f.TotalTokens -eq 1500 -and [long] $f.Calls -eq 3) 'the tokens of a day: by business unit, model and API, each count in its column'
        Assert-That ($h.BusinessUnit -eq 'HR' -and $h.Model -eq '' -and [long] $h.TotalTokens -eq 70 -and $h.PromptTokens -is [DBNull]) 'a record without the model dimension: model empty, a count not sent is NULL'
        $lim = Invoke-TestSql "SELECT ExternalId, Kind, Name, ScopeType, Scope, Unit, Amount, Period, PerUser, Raw FROM audit.SpendLimitSource WHERE Source = 'Gateway' ORDER BY ExternalId;" -Table
        $ids = @($lim.Rows | ForEach-Object { "$($_.ExternalId)".Substring($gwPath.Length) })
        Assert-That ($lim.Rows.Count -eq 4 -and ($ids -join ',') -eq '/policies/policy#0#GatewayQuota,/policies/policy#0#GatewayRate,/policies/policy#1#GatewayQuota,/products/starter/policies/policy#0#GatewayRate') `
            "the limits: two elements at service scope (a rate and two quotas) and one at a product; a product with no policy and an API with no limit add none ($($ids -join ', '))"
        $prod = @($lim.Rows | Where-Object { $_.ExternalId -like '*/products/*' })[0]
        Assert-That ($prod.Name -eq 'gw-east / product Starter' -and $prod.Kind -eq 'GatewayRate' -and [decimal] $prod.Amount -eq 2000 -and $prod.Period -eq 'Minute' -and $prod.Scope -eq '@(context.Subscription.Id)' -and
                     $prod.PerUser -eq $true -and $prod.Unit -eq 'Tokens' -and $prod.ScopeType -eq 'CounterKey' -and $prod.Raw -like '<azure-openai-token-limit*') 'a product''s rate: named for the gateway and the product, per counter key, the element kept'
        $svc = @($lim.Rows | Where-Object { $_.ExternalId -like '*#0#GatewayQuota' })[0]
        Assert-That ($svc.Name -eq 'gw-east / all APIs' -and [decimal] $svc.Amount -eq 5000000 -and $svc.Period -eq 'Monthly' -and $svc.Scope -like '@(context.Request.Headers.GetValueOrDefault("x-business-unit"*') 'the service''s quota: all APIs, its period, the counter key''s expression'

        # The same days again: written over, not added to.
        $r1b = Invoke-Loader @{ GatewayService = $gwPath; WorkspaceId = 'ws-good'; ModelDimension = 'Model' }
        Assert-That ($r1b.Code -eq 0 -and [int] (Invoke-TestSql 'SELECT COUNT(*) FROM audit.GatewayTokenDay;') -eq 2 -and [int] (Invoke-TestSql 'SELECT COUNT(*) FROM audit.SpendLimitSource;') -eq 4) 'loaded again, the same days and limits are written over, not doubled'

        # Application Insights with no workspace (classic): customMetrics of the resource; no dimensions.
        $r2 = Invoke-Loader @{ GatewayService = $gwPath; AppInsights = "$aiBase/classic" }
        $run = Get-LastRun
        $cl = (Invoke-TestSql "SELECT COUNT(*) AS N, MAX(BusinessUnit) AS BU, MAX(Api) AS Api, SUM(TotalTokens) AS T, SUM(PromptTokens) AS P, SUM(Calls) AS C FROM audit.GatewayTokenDay WHERE [Day] = '$($extDays[1])';" -Table).Rows[0]
        Assert-That ($r2.Code -eq 0 -and "$($run.Status)" -eq 'Succeeded' -and "$($state.Queries[-1])" -like "/la/v1$aiBase/classic/query|customMetrics*" -and [int] $cl.N -eq 1 -and $cl.BU -eq '' -and $cl.Api -eq '' -and
                     [long] $cl.T -eq 900 -and [long] $cl.P -eq 600 -and [long] $cl.C -eq 9) "classic Application Insights: customMetrics asked of the resource, records without dimensions under empty ones, the gateway's days written over ($($run.Detail))"
        Assert-That ([int] (Invoke-TestSql "SELECT COUNT(*) FROM audit.GatewayTokenDay WHERE BusinessUnit = N'Finance';") -eq 0) 'the gateway''s days loaded are replaced whole (the workspace''s rows of those days are gone)'

        # Application Insights on a workspace: the workspace is found and asked for AppMetrics.
        $r3 = Invoke-Loader @{ GatewayService = $gwPath; AppInsights = "$aiBase/modern" }
        Assert-That ($r3.Code -eq 0 -and "$((Get-LastRun).Status)" -eq 'Succeeded' -and "$($state.Queries[-1])" -like '/la/v1/workspaces/ws-good/query|AppMetrics*' -and [int] (Invoke-TestSql "SELECT COUNT(*) FROM audit.GatewayTokenDay WHERE BusinessUnit = N'Finance';") -eq 1) `
            'Application Insights on a workspace: its workspace id is looked up and AppMetrics read there'

        # A 403 on the workspace: Partial, named in the detail; the limits still load and the tokens stay.
        $before = [int] (Invoke-TestSql 'SELECT COUNT(*) FROM audit.GatewayTokenDay;')
        $r4 = Invoke-Loader @{ GatewayService = $gwPath; WorkspaceId = 'ws-denied' }
        $run = Get-LastRun
        Assert-That ($r4.Code -eq 0 -and "$($run.Status)" -eq 'Partial' -and "$($run.Detail)" -like '*Not loaded: token metrics (refused (403)*Log Analytics Reader*' -and "$($run.Detail)" -like '*4 token limit(s)*' -and
                     [int] (Invoke-TestSql 'SELECT COUNT(*) FROM audit.GatewayTokenDay;') -eq $before) "a 403 on the workspace: partial, saying so, exit code 0, the limits loaded and the tokens kept ($($run.Detail))"
        $r4b = Invoke-Loader @{ GatewayService = $gwPath, $deniedGw; WorkspaceId = 'ws-good' }
        $run = Get-LastRun
        Assert-That ($r4b.Code -eq 0 -and "$($run.Status)" -eq 'Partial' -and "$($run.Detail)" -like '*limits of gw-locked (refused (403)*') 'a gateway Azure refuses: partial too, the other gateway loaded'
        $r4c = Invoke-Loader @{ WorkspaceId = 'ws-denied' }
        Assert-That ($r4c.Code -eq 1 -and "$((Get-LastRun).Status)" -eq 'Failed') 'with nothing else asked, a refused workspace is a failed run and exit code 1'

        # The delete guard: the policies give half the limits. Refused (exit code 3), kept; then let through by -AllowDrop.
        $state.FewerLimits = $true
        $r5 = Invoke-Loader @{ GatewayService = $gwPath; WorkspaceId = 'ws-good' }
        $run = Get-LastRun
        Assert-That ($r5.Code -eq 3 -and "$($run.Status)" -eq 'Partial' -and "$($run.Detail)" -like '*Gateway limits of gw-east*GatewayLimitMaxDropPercent*' -and [int] (Invoke-TestSql 'SELECT COUNT(*) FROM audit.SpendLimitSource;') -eq 4) `
            "the delete guard: 2 limits where 4 were is refused, exit code 3, the last limits kept, the tokens loaded ($($r5.Code): $($run.Detail))"
        $r5b = Invoke-Loader @{ GatewayService = $gwPath; WorkspaceId = 'ws-good'; MaxDropPercent = 60 }
        Assert-That ($r5b.Code -eq 0 -and [int] (Invoke-TestSql 'SELECT COUNT(*) FROM audit.SpendLimitSource;') -eq 2) 'a percentage above the drop lets it through'
        $state.FewerLimits = $false
        [void] (Invoke-Loader @{ GatewayService = $gwPath })
        $state.FewerLimits = $true
        $r5c = Invoke-Loader @{ GatewayService = $gwPath; AllowDrop = $true }
        Assert-That ($r5c.Code -eq 0 -and "$((Get-LastRun).Status)" -eq 'Succeeded' -and [int] (Invoke-TestSql "SELECT COUNT(*) FROM audit.SpendLimitSource WHERE Source = 'Gateway';") -eq 2) 'and so does -AllowDrop, once'
        $state.FewerLimits = $false

        # -CheckOnly: signs in, tries each read, writes nothing.
        $runs = [int] (Invoke-TestSql 'SELECT COUNT(*) FROM audit.ExternalUsageRun;'); $rowsNow = [int] (Invoke-TestSql 'SELECT (SELECT COUNT(*) FROM audit.GatewayTokenDay) + (SELECT COUNT(*) FROM audit.SpendLimitSource);')
        $c1 = Invoke-Loader @{ GatewayService = $gwPath; AppInsights = "$aiBase/modern"; CheckOnly = $true }
        Assert-That ($c1.Code -eq 0 -and $c1.Out -like '*OK*Token metrics: read from workspace ws-good, table AppMetrics*' -and $c1.Out -like '*OK*Token limits of gw-east: readable (4 limit(s))*' -and $c1.Out -like '*Every part can be read*') `
            "-CheckOnly: each part OK, exit code 0 $(if ($c1.Code -ne 0 -or $c1.Out -notlike '*Every part*') { $c1.Out })"
        $c2 = Invoke-Loader @{ GatewayService = $gwPath, $deniedGw; WorkspaceId = 'ws-denied'; CheckOnly = $true }
        Assert-That ($c2.Code -eq 1 -and $c2.Out -like '*MISSING*Token metrics: not readable from workspace ws-denied*Log Analytics Reader*' -and $c2.Out -like '*MISSING*Token limits of gw-locked*' -and $c2.Out -like '*2 part(s) cannot be read*') `
            '-CheckOnly: what is missing, and where, exit code 1'
        $c3 = Invoke-Loader @{ WorkspaceId = 'ws-good'; CheckOnly = $true; ConnectionString = 'Server=no-such-server.invalid;Database=x;Integrated Security=true;Connect Timeout=1' }
        Assert-That ($c3.Code -eq 0 -and [int] (Invoke-TestSql 'SELECT COUNT(*) FROM audit.ExternalUsageRun;') -eq $runs -and
                     [int] (Invoke-TestSql 'SELECT (SELECT COUNT(*) FROM audit.GatewayTokenDay) + (SELECT COUNT(*) FROM audit.SpendLimitSource);') -eq $rowsNow) '-CheckOnly writes nothing, and needs no database'
        Assert-That (-not ((@($r1, $r2, $r4, $r5, $c1, $c2) | ForEach-Object Out) -join "`n").Contains('gw-token')) 'the token is never printed'

        # Mistakes stop the load before anything is read.
        Assert-That ((Test-Throw { & $loader -GatewayService '/subscriptions/x/resourceGroups/r/providers/Microsoft.Web/sites/s' -ConnectionString $conn -AccessToken $token -SettingsPath $noSettings }) -and
                     (Test-Throw { & $loader -WorkspaceId 'a' -AppInsights 'b' -ConnectionString $conn -AccessToken $token -SettingsPath $noSettings }) -and
                     (Test-Throw { & $loader -ConnectionString $conn -AccessToken $token -SettingsPath $noSettings })) 'a resource that is not a gateway, both a workspace and Application Insights, or nothing to read, is refused'
    }
    finally {
        try { [void] (Invoke-WebRequest -Uri "http://localhost:$port/stop" -SkipHttpErrorCheck -TimeoutSec 5) } catch { Write-Verbose "$_" }
        Stop-Job $mock -ErrorAction SilentlyContinue; Remove-Job $mock -Force -ErrorAction SilentlyContinue
        if ($created) {
            [System.Data.SqlClient.SqlConnection]::ClearAllPools()
            [void] (Invoke-TestSql "IF DB_ID(N'$testDb') IS NOT NULL BEGIN ALTER DATABASE [$testDb] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [$testDb]; END;" -Catalog 'master')
        }
    }
}

Write-Host ''
if ($script:Failures.Count) { Write-Host "$($script:Failures.Count) assertion(s) failed." -ForegroundColor Red; exit 1 }
Write-Host 'All gateway usage tests passed.' -ForegroundColor Green
