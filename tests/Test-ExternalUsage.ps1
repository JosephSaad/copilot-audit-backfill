<#
    Tests the two loaders of AI usage outside Microsoft 365 Copilot, dot-sourced so nothing touches
    GitHub, Azure, the certificate store or SQL Server:

      1  tools\ExternalUsage.Common.ps1: the days a load covers, and rows as the table the bulk copy takes
      2  tools\Import-GitHubCopilotUsage.ps1: an enterprise's and an organization's endpoints, a report file
         in each shape GitHub may send it, and a seat, a day, a person's day and a billing line as rows
      3  tools\Import-FoundryUsage.ps1: the signed statement the app signs in with, Azure Monitor's answer
         as rows (current metrics and the older ones), Cost Management's answer by column name
      4  Invoke-CopilotPipeline.ps1: the two optional steps of the nightly run, and Foundry's own certificate

    Both loaders run end to end, against a stand-in for each service, in Test-SqlIntegration.ps1.
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent

$script:Failures = [System.Collections.Generic.List[string]]::new()
function Assert-That {
    param([bool] $Condition, [string] $Message)
    if ($Condition) { Write-Host "  PASS  $Message" -ForegroundColor Green }
    else            { Write-Host "  FAIL  $Message" -ForegroundColor Red; $script:Failures.Add($Message) }
}
function Test-Throw([scriptblock] $Do) { try { & $Do | Out-Null; $false } catch { $true } }

# ---- 1. What the two share -----------------------------------------------------------------------
Write-Host "`n1. tools\ExternalUsage.Common.ps1" -ForegroundColor Cyan
. (Join-Path $root 'tools' 'ExternalUsage.Common.ps1')

$days = Get-UsageDay -Days 3 -Today ([datetime] '2026-10-08')
Assert-That ($days.Count -eq 3 -and $days[0] -eq [datetime] '2026-10-05' -and $days[-1] -eq [datetime] '2026-10-07') 'a load covers whole days, oldest first, ending yesterday'

$t = ConvertTo-UsageTable @(
    [pscustomobject]@{ Name = 'a'; N = 5; When = [datetime] '2026-10-01'; Flag = $true; Extra = 'ignored' }
    [pscustomobject]@{ Name = ''; N = $null; When = ''; Flag = $null }
    [pscustomobject]@{ Name = 'c' }
) ([ordered]@{ Name = [string]; N = [long]; When = [datetime]; Flag = [bool] })
Assert-That ($t.Rows.Count -eq 3 -and (@($t.Columns | ForEach-Object ColumnName) -join ',') -eq 'Name,N,When,Flag') 'rows become a table of the columns named, in order, and no others'
Assert-That ($t.Rows[0].N -eq 5 -and $t.Rows[0].Flag -eq $true -and $t.Columns['N'].DataType -eq [long]) 'each value in its column''s type'
Assert-That ($t.Rows[1].N -is [DBNull] -and $t.Rows[1].When -is [DBNull] -and $t.Rows[1].Name -eq '' -and $t.Rows[2].N -is [DBNull]) `
    'a missing or null value, or an empty one for a number or a date, is NULL; an empty string stays a string'
Assert-That ((ConvertTo-UsageTable @() ([ordered]@{ A = [string] })).Rows.Count -eq 0) 'no rows is an empty table, not an error'
Assert-That (Test-Throw { Write-UsageTable $null 'dbo.Users; DROP TABLE x' $t }) 'a table that is not one of these tools'' is refused before anything is sent'

# ---- 2. GitHub Copilot ---------------------------------------------------------------------------
Write-Host "`n2. tools\Import-GitHubCopilotUsage.ps1" -ForegroundColor Cyan
. (Join-Path $root 'tools' 'Import-GitHubCopilotUsage.ps1')

$e = Get-GitHubScope -Enterprise 'contoso inc'
Assert-That ($e.Name -eq 'enterprise:contoso inc' -and $e.Seats -eq '/enterprises/contoso%20inc/copilot/billing/seats' -and $e.Report -eq 'enterprise-1-day' -and
             $e.Metrics -eq '/enterprises/contoso%20inc/copilot/metrics/reports' -and $e.Billing -eq '/enterprises/contoso%20inc/settings/billing/usage') `
    'an enterprise: its seats, its usage metrics reports and its billing usage'
$o = Get-GitHubScope -Organization 'octo'
Assert-That ($o.Name -eq 'org:octo' -and $o.Seats -eq '/orgs/octo/copilot/billing/seats' -and $o.Report -eq 'organization-1-day' -and $o.Billing -eq '/organizations/octo/settings/billing/usage') `
    'an organization: the same three, at its own addresses'
Assert-That ((Test-Throw { Get-GitHubScope -Enterprise 'a' -Organization 'b' }) -and (Test-Throw { Get-GitHubScope })) 'both, or neither, is refused'

$nd = ConvertFrom-GitHubReportFile "{`"day`":`"2026-10-05`",`"daily_active_users`":7}`r`n`r`n{`"day`":`"2026-10-06`",`"daily_active_users`":9}`n"
Assert-That ($nd.Count -eq 2 -and $nd[1].daily_active_users -eq 9) 'a report file of one object a line, blank lines skipped'
Assert-That ((ConvertFrom-GitHubReportFile '[{"user_id":1},{"user_id":2},{"user_id":3}]').Count -eq 3) 'or one array'
$pretty = ConvertFrom-GitHubReportFile "{`n  `"day`": `"2026-10-05`",`n  `"daily_active_users`": 4`n}"
Assert-That ($pretty.Count -eq 1 -and $pretty[0].daily_active_users -eq 4) 'or one object over several lines'
Assert-That ((ConvertFrom-GitHubReportFile '').Count -eq 0 -and (ConvertFrom-GitHubReportFile $null).Count -eq 0) 'and an empty file is no records'

Assert-That ((ConvertTo-UtcTime '2026-12-31 00:00:00 UTC') -eq [datetime]::new(2026, 12, 31, 0, 0, 0, [System.DateTimeKind]::Utc) -and
             (ConvertTo-UtcTime '2026-10-05T10:00:00+02:00').Hour -eq 8 -and $null -eq (ConvertTo-UtcTime '') -and $null -eq (ConvertTo-UtcTime 'soon')) `
    'a time in either of GitHub''s forms, in UTC; nothing for one that cannot be read'

$seat = '{"created_at":"2025-11-02T09:30:00Z","assignee":{"login":"mona","id":42},"organization":{"login":"octo"},"assigning_team":{"slug":"platform","name":"Platform"},
          "plan_type":"enterprise","last_activity_at":"2026-10-06T18:00:00+00:00","last_activity_editor":"vscode/1.99","last_authenticated_at":null,"pending_cancellation_date":"2026-11-01"}' | ConvertFrom-Json
$sr = ConvertTo-GitHubSeatRow $seat 'enterprise:contoso'
Assert-That ($sr.Login -eq 'mona' -and $sr.UserId -eq 42 -and $sr.Organization -eq 'octo' -and $sr.AssigningTeam -eq 'platform' -and $sr.PlanType -eq 'enterprise' -and
             $sr.CreatedUtc -eq [datetime] '2025-11-02T09:30:00' -and $sr.LastActivityEditor -eq 'vscode/1.99' -and $null -eq $sr.LastAuthenticatedUtc -and
             $sr.PendingCancellationDate -eq [datetime] '2026-11-01') 'a seat: who holds it, the organization and team that gave it, the plan, and when it was last used'
$bare = ConvertTo-GitHubSeatRow ('{"assignee":{"login":"hubot","id":7}}' | ConvertFrom-Json) 'org:octo'
Assert-That ($bare.Login -eq 'hubot' -and $null -eq $bare.Organization -and $null -eq $bare.AssigningTeam -and $null -eq $bare.CreatedUtc) 'a seat with no more than its holder is still a row'

$rec = '{"day":"2026-10-05","daily_active_users":120,"weekly_active_users":300,"monthly_active_users":410,"monthly_active_chat_users":350,"monthly_active_agent_users":90,
         "user_initiated_interaction_count":5400,"code_generation_activity_count":9000,"code_acceptance_activity_count":3100,"loc_suggested_to_add_sum":20000,
         "loc_suggested_to_delete_sum":1500,"loc_added_sum":8000,"loc_deleted_sum":700,"totals_by_ide":[{"ide":"vscode","user_initiated_interaction_count":5000}]}' | ConvertFrom-Json
$dr = ConvertTo-GitHubDayRow $rec 'enterprise:contoso' ([datetime] '2026-10-01')
Assert-That ($dr.Day -eq [datetime] '2026-10-05' -and $dr.DailyActiveUsers -eq 120 -and $dr.MonthlyActiveAgentUsers -eq 90 -and $dr.Interactions -eq 5400 -and
             $dr.CodeAcceptances -eq 3100 -and $dr.LocAdded -eq 8000 -and $dr.LocSuggestedToDelete -eq 1500) 'a day of the enterprise report: its own day, the people active and what they did'
Assert-That ($dr.Json -like '*totals_by_ide*vscode*') 'with the whole record kept beside the columns, for the breakdowns no column holds yet'
$noDay = ConvertTo-GitHubDayRow ('{"daily_active_users":1}' | ConvertFrom-Json) 's' ([datetime] '2026-10-01')
Assert-That ($noDay.Day -eq [datetime] '2026-10-01' -and $null -eq $noDay.Interactions) 'a record that does not say its day takes the day asked for, and a field it lacks is empty'

$ur = ConvertTo-GitHubUserDayRow ('{"day":"2026-10-05","user_id":42,"user_login":"mona","user_initiated_interaction_count":31,"code_generation_activity_count":80,
      "code_acceptance_activity_count":22,"loc_added_sum":140,"loc_deleted_sum":9,"ai_credits_used":412.5,"used_chat":true,"used_agent":false,"used_cli":true}' | ConvertFrom-Json) 's' ([datetime] '2026-10-05')
Assert-That ($ur.UserId -eq 42 -and $ur.Login -eq 'mona' -and $ur.Interactions -eq 31 -and $ur.AiCreditsUsed -eq 412.5 -and $ur.UsedChat -eq $true -and $ur.UsedAgent -eq $false -and $ur.UsedCli -eq $true) `
    'a person''s day: what they did, the AI credits they used, and which of chat, agent and command line'
Assert-That ($null -eq (ConvertTo-GitHubUserDayRow ('{"user_login":"ghost"}' | ConvertFrom-Json) 's' ([datetime] '2026-10-05'))) 'a record with no user id is left out'

$line = '{"date":"2026-10-05T00:00:00Z","product":"copilot","sku":"Copilot Enterprise","quantity":410,"unitType":"UserMonths","pricePerUnit":39,"grossAmount":530.32,
          "discountAmount":30.32,"netAmount":500,"organizationName":"octo","repositoryName":""}' | ConvertFrom-Json
$cr = ConvertTo-GitHubCostRow $line 'enterprise:contoso'
Assert-That ($cr.UsageDate -eq [datetime] '2026-10-05' -and $cr.Product -eq 'copilot' -and $cr.Sku -eq 'Copilot Enterprise' -and $cr.Quantity -eq 410 -and $cr.PricePerUnit -eq 39 -and
             $cr.GrossAmount -eq 530.32 -and $cr.DiscountAmount -eq 30.32 -and $cr.NetAmount -eq 500 -and $cr.Organization -eq 'octo') 'a billing line for Copilot: the day, the SKU, the quantity and the three amounts'
Assert-That ($null -eq (ConvertTo-GitHubCostRow ('{"date":"2026-10-05T00:00:00Z","product":"actions","sku":"Actions Linux","quantity":1,"netAmount":1}' | ConvertFrom-Json) 's')) 'a line of another product is left out'
Assert-That ($null -ne (ConvertTo-GitHubCostRow ('{"date":"2026-10-05T00:00:00Z","product":"ai_credits","sku":"Copilot AI Credits","quantity":1,"netAmount":0.01}' | ConvertFrom-Json) 's')) 'one that is Copilot''s by its SKU alone is kept'
Assert-That ($null -eq (ConvertTo-GitHubCostRow ('{"product":"copilot","sku":"x"}' | ConvertFrom-Json) 's')) 'and one with no date is left out'

# 3.3.0: who each login is (SCIM, Enterprise Managed Users) and each organization's Copilot settings.
Assert-That ((Get-ManagedLogin 'joseph.saad@cloudsconnected.com' 'cloudsc') -eq 'joseph-saad_cloudsc' -and (Get-ManagedLogin 'Ann_Lee+x@contoso.com' 'CON') -eq 'ann-lee-x_con' -and
             $null -eq (Get-ManagedLogin 'a@b.com' '') -and $null -eq (Get-ManagedLogin '' 'x')) 'a managed user''s login: the sign-in before the @, other characters as hyphens, _ and the short code, in lower case'
Assert-That ((Find-Shortcode @('mona_contoso', 'hubot_contoso', 'octocat', 'x_other')) -eq 'contoso' -and $null -eq (Find-Shortcode @('mona', 'hubot'))) 'the short code most managed logins share; none among personal logins'
Assert-That ((Get-GitHubScope -Enterprise 'contoso').Scim -eq '/scim/v2/enterprises/contoso/Users' -and $null -eq (Get-GitHubScope -Organization 'octo').Scim -and (Get-GitHubScope -Organization 'octo').Organization -eq 'octo') `
    'an enterprise reads its SCIM identities; an organization has none, and is its own organization for the settings'
$idr = ConvertTo-GitHubIdentityRow ('{"userName":"ann.lee@contoso.com","externalId":"e-1","displayName":"Ann Lee","active":true}' | ConvertFrom-Json) 'enterprise:contoso' 'contoso'
Assert-That ($idr.Upn -eq 'ann.lee@contoso.com' -and $idr.Login -eq 'ann-lee_contoso' -and $idr.ExternalId -eq 'e-1' -and $idr.DisplayName -eq 'Ann Lee' -and $idr.Active -eq $true -and $idr.Source -eq 'SCIM') `
    'a SCIM user: the UPN, the login it gives, its id in Entra, its name and whether it is active'
Assert-That ((ConvertTo-GitHubIdentityRow ('{"userName":"x@y.com","name":{"formatted":"X Y"}}' | ConvertFrom-Json) 's' $null).DisplayName -eq 'X Y' -and $null -eq (ConvertTo-GitHubIdentityRow ('{"active":true}' | ConvertFrom-Json) 's' 'c')) `
    'the name from name.formatted where there is no display name; a user with no user name is left out'
$pol = ConvertTo-GitHubPolicyRow ('{"seat_breakdown":{"total":3},"seat_management_setting":"assign_selected","ide_chat":"enabled","platform_chat":"disabled","cli":"enabled","public_code_suggestions":"block","plan_type":"business"}' | ConvertFrom-Json) 'enterprise:contoso' 'octo'
Assert-That ($pol.Count -eq 6 -and ($pol | Where-Object Policy -eq 'public_code_suggestions').State -eq 'block' -and -not ($pol | Where-Object Policy -eq 'seat_breakdown') -and $pol[0].Organization -eq 'octo') `
    'an organization''s Copilot settings: one row per setting that is a word, the seat counts left out'

# ---- 3. Foundry ----------------------------------------------------------------------------------
Write-Host "`n3. tools\Import-FoundryUsage.ps1" -ForegroundColor Cyan
. (Join-Path $root 'tools' 'Import-FoundryUsage.ps1')

$rsa = [System.Security.Cryptography.RSA]::Create(2048)
$req = [System.Security.Cryptography.X509Certificates.CertificateRequest]::new('CN=foundry-test', $rsa,
    [System.Security.Cryptography.HashAlgorithmName]::SHA256, [System.Security.Cryptography.RSASignaturePadding]::Pkcs1)
$cert = $req.CreateSelfSigned([datetimeoffset]::UtcNow.AddDays(-1), [datetimeoffset]::UtcNow.AddDays(30))
$now = [datetime]::new(2026, 10, 8, 12, 0, 0, [System.DateTimeKind]::Utc)
$jwt = New-ClientAssertion -Certificate $cert -Tenant 'contoso.onmicrosoft.com' -Client 'app-1' -Login 'https://login.microsoftonline.com/' -Now $now
$parts = $jwt.Split('.')
function ConvertFrom-Base64Url([string] $s) { $p = $s.Replace('-', '+').Replace('_', '/'); [Convert]::FromBase64String($p + ('=' * ((4 - $p.Length % 4) % 4))) }
$head = [System.Text.Encoding]::UTF8.GetString((ConvertFrom-Base64Url $parts[0])) | ConvertFrom-Json
$claim = [System.Text.Encoding]::UTF8.GetString((ConvertFrom-Base64Url $parts[1])) | ConvertFrom-Json
Assert-That ($parts.Count -eq 3 -and $jwt -notmatch '[+/=]') 'the signed statement is three parts, each base64url'
Assert-That ($head.alg -eq 'PS256' -and $head.typ -eq 'JWT' -and $head.'x5t#S256' -eq (ConvertTo-Base64Url ([System.Security.Cryptography.SHA256]::HashData($cert.RawData)))) `
    'its header names PS256 and the certificate by its SHA-256 thumbprint'
Assert-That ($claim.aud -eq 'https://login.microsoftonline.com/contoso.onmicrosoft.com/oauth2/v2.0/token' -and $claim.iss -eq 'app-1' -and $claim.sub -eq 'app-1' -and
             $claim.exp - $claim.nbf -eq 600 -and $claim.iat -eq $claim.nbf -and [guid]::TryParse("$($claim.jti)", [ref] [guid]::Empty)) `
    'it is from the app, to the tenant''s token endpoint, good for ten minutes, with an id of its own'
$public = [System.Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPublicKey($cert)
Assert-That ($public.VerifyData([System.Text.Encoding]::ASCII.GetBytes($parts[0] + '.' + $parts[1]), (ConvertFrom-Base64Url $parts[2]),
        [System.Security.Cryptography.HashAlgorithmName]::SHA256, [System.Security.Cryptography.RSASignaturePadding]::Pss)) 'and the certificate''s public key verifies its signature'
$publicOnly = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new($cert.RawData)
Assert-That (Test-Throw { New-ClientAssertion -Certificate $publicOnly -Tenant 't' -Client 'c' -Login 'https://login' }) 'a certificate without its private key is refused, by name'

$id = '/subscriptions/AAAA1111-0000-0000-0000-000000000000/resourceGroups/rg-ai/providers/Microsoft.CognitiveServices/accounts/foundry-east'
$where = Split-ResourceId ($id + '/deployments/gpt')
Assert-That ($where.SubscriptionId -eq 'aaaa1111-0000-0000-0000-000000000000' -and $where.ResourceGroup -eq 'rg-ai' -and $where.Account -eq 'foundry-east') 'an account''s resource id as its subscription, resource group and name'
Assert-That ($null -eq (Split-ResourceId '/subscriptions/x/resourceGroups/rg/providers/Microsoft.Storage/storageAccounts/s')) 'anything else is not an account'

$answer = @'
{"value":[
 {"name":{"value":"ModelRequests"},"timeseries":[
   {"metadatavalues":[{"name":{"value":"modeldeploymentname"},"value":"chat"},{"name":{"value":"modelname"},"value":"gpt-4o"},{"name":{"value":"ModelVersion"},"value":"2024-11-20"}],
    "data":[{"timeStamp":"2026-10-05T00:00:00Z","total":100},{"timeStamp":"2026-10-06T00:00:00Z","total":40},{"timeStamp":"2026-10-07T00:00:00Z"}]},
   {"metadatavalues":[{"name":{"value":"ModelDeploymentName"},"value":"chat"},{"name":{"value":"ModelName"},"value":"gpt-4o"},{"name":{"value":"modelversion"},"value":"2024-11-20"}],
    "data":[{"timeStamp":"2026-10-05T00:00:00Z","total":11}]}]},
 {"name":{"value":"InputTokens"},"timeseries":[
   {"metadatavalues":[{"name":{"value":"modeldeploymentname"},"value":"chat"},{"name":{"value":"modelname"},"value":"gpt-4o"},{"name":{"value":"modelversion"},"value":"2024-11-20"}],
    "data":[{"timeStamp":"2026-10-05T00:00:00Z","total":50000}]}]},
 {"name":{"value":"OutputTokens"},"errorCode":"InvalidSeries","timeseries":[{"metadatavalues":[],"data":[{"timeStamp":"2026-10-05T00:00:00Z","total":1}]}]},
 {"name":{"value":"TotalTokens"},"timeseries":[]}
]}
'@ | ConvertFrom-Json
$flat = ConvertFrom-ArmMetric $answer
Assert-That ($flat.Count -eq 4 -and $flat[0].Metric -eq 'ModelRequests' -and $flat[0].Dimensions['modelname'] -eq 'gpt-4o' -and $flat[0].Day -eq [datetime] '2026-10-05' -and $flat[0].Total -eq 100) `
    "Azure Monitor's answer as a row per metric, series and day that has a total ($($flat.Count) rows)"
Assert-That (@($flat | Where-Object Metric -eq 'OutputTokens').Count -eq 0) 'a metric Azure reports an error for is left out'
Assert-That ((ConvertFrom-ArmMetric $null).Count -eq 0 -and (ConvertFrom-ArmMetric ('{"value":[]}' | ConvertFrom-Json)).Count -eq 0) 'and no answer is no rows'
$usage = Merge-FoundryUsage -Metric $flat -Where $where
$d5 = $usage | Where-Object { $_.Day -eq [datetime] '2026-10-05' }
Assert-That ($usage.Count -eq 2 -and $d5.Deployment -eq 'chat' -and $d5.ModelName -eq 'gpt-4o' -and $d5.ModelVersion -eq '2024-11-20' -and $d5.Requests -eq 111 -and $d5.InputTokens -eq 50000 -and
             $null -eq $d5.OutputTokens -and $d5.Account -eq 'foundry-east') 'the rows of one deployment and day are one row: two series of one metric add up, each metric in its column'

# The older Azure OpenAI metrics: requests name the model, the token ones do not and take the deployment's.
$older = @(
    [pscustomobject]@{ Metric = 'AzureOpenAIRequests'; Dimensions = @{ modeldeploymentname = 'Legacy'; modelname = 'gpt-35-turbo'; modelversion = '0125' }; Day = [datetime] '2026-10-05'; Total = 9.0 }
    [pscustomobject]@{ Metric = 'ProcessedPromptTokens'; Dimensions = @{ modeldeploymentname = 'Legacy' }; Day = [datetime] '2026-10-05'; Total = 700.0 }
    [pscustomobject]@{ Metric = 'GeneratedTokens'; Dimensions = @{ modeldeploymentname = 'Legacy' }; Day = [datetime] '2026-10-05'; Total = 300.0 }
    [pscustomobject]@{ Metric = 'TokenTransaction'; Dimensions = @{ modeldeploymentname = 'Legacy' }; Day = [datetime] '2026-10-05'; Total = 1000.0 }
    [pscustomobject]@{ Metric = 'SomethingElse'; Dimensions = @{ modeldeploymentname = 'Legacy' }; Day = [datetime] '2026-10-05'; Total = 5.0 }
    [pscustomobject]@{ Metric = 'AzureOpenAIRequests'; Dimensions = @{}; Day = [datetime] '2026-10-05'; Total = 5.0 }
)
$old = Merge-FoundryUsage -Metric $older -Where $where -ModelOf @{ legacy = [pscustomobject]@{ ModelName = 'gpt-35-turbo'; ModelVersion = '0125' } }
Assert-That ($old.Count -eq 1 -and $old[0].Requests -eq 9 -and $old[0].InputTokens -eq 700 -and $old[0].OutputTokens -eq 300 -and $old[0].TotalTokens -eq 1000 -and $old[0].ModelName -eq 'gpt-35-turbo') `
    'the older metrics fill the same columns; a series that does not name its model takes its deployment''s; one with no deployment, or of another metric, is left out'
Assert-That ((Merge-FoundryUsage -Metric @() -Where $where).Count -eq 0) 'and no usage is no rows'

$body = Get-CostQueryBody -From ([datetime] '2026-10-05') -To ([datetime] '2026-10-07')
Assert-That ($body.type -eq 'ActualCost' -and $body.timePeriod.from -eq '2026-10-05T00:00:00Z' -and $body.timePeriod.to -eq '2026-10-07T23:59:59Z' -and $body.dataset.granularity -eq 'Daily' -and
             (@($body.dataset.grouping | ForEach-Object { $_.name }) -join ',') -eq 'ResourceId,Meter' -and $body.dataset.filter.dimensions.values[0] -eq 'microsoft.cognitiveservices/accounts') `
    'the question for Cost Management: actual cost a day, by resource and meter, for Cognitive Services accounts, over whole days'
$cost = ConvertFrom-CostQuery (@"
{"properties":{"nextLink":null,"columns":[{"name":"PreTaxCost","type":"Number"},{"name":"UsageDate","type":"Number"},{"name":"ResourceId","type":"String"},{"name":"Meter","type":"String"},{"name":"Currency","type":"String"}],
 "rows":[[12.3456,20261005,"$($id.ToLowerInvariant())","gpt-4o Input Tokens","USD"],[0.5,20261006,"$($id.ToLowerInvariant())","gpt-4o Output Tokens","USD"]]}}
"@ | ConvertFrom-Json)
Assert-That ($cost.Count -eq 2 -and $cost[0].Cost -eq 12.3456 -and $cost[0].Day -eq [datetime] '2026-10-05' -and $cost[0].Meter -eq 'gpt-4o Input Tokens' -and $cost[0].Currency -eq 'USD' -and
             (Split-ResourceId $cost[0].ResourceId).Account -eq 'foundry-east') 'its answer by column name, wherever each column is: the cost, the day from its number, the resource, the meter and the currency'
$dated = ConvertFrom-CostQuery ('{"properties":{"columns":[{"name":"ResourceId"},{"name":"Cost"},{"name":"UsageDate"}],"rows":[["/x",3,"2026-10-05T00:00:00"]]}}' | ConvertFrom-Json)
Assert-That ($dated[0].Day -eq [datetime] '2026-10-05' -and $dated[0].Cost -eq 3 -and $dated[0].Meter -eq '' -and $null -eq $dated[0].Currency) 'a day sent as a date is read too, and a column it lacks is empty'
Assert-That (Test-Throw { ConvertFrom-CostQuery ('{"properties":{"columns":[{"name":"Meter"}],"rows":[]}}' | ConvertFrom-Json) }) 'an answer with no cost, day or resource column is refused, naming the columns it has'
Assert-That ((ConvertFrom-CostQuery $null).Count -eq 0) 'and no answer is no rows'

# 3.3.0: what each account is and how it is run.
$acc = ('{"id":"' + $id + '","name":"foundry-east","kind":"AIServices","location":"eastus","sku":{"name":"S0"},"identity":{"type":"SystemAssigned"},
          "tags":{"Owner":"ann.lee@contoso.com","Cost-Center":"CC-42","Application":"claims-bot","env":"prod"},
          "properties":{"publicNetworkAccess":"Enabled","disableLocalAuth":false,"networkAcls":{"defaultAction":"Allow"},"privateEndpointConnections":[{"id":"pe1"}]}}') | ConvertFrom-Json
$ar = ConvertTo-FoundryAccountRow $acc $where
Assert-That ($ar.Account -eq 'foundry-east' -and $ar.PublicNetworkAccess -eq 'Enabled' -and $ar.NetworkDefaultAction -eq 'Allow' -and $ar.PrivateEndpoints -eq 1 -and $ar.DisableLocalAuth -eq $false -and
             $ar.IdentityType -eq 'SystemAssigned' -and $ar.SkuName -eq 'S0' -and $ar.Owner -eq 'ann.lee@contoso.com' -and $ar.CostCenter -eq 'CC-42' -and $ar.App -eq 'claims-bot' -and $ar.Tags -like '*"env":"prod"*') `
    'an account: its network access, key sign-in, identity, and owner, cost center and app from its tags, whatever their case'
$bareAcc = ConvertTo-FoundryAccountRow ('{"id":"x","name":"a","kind":"OpenAI"}' | ConvertFrom-Json) $where
Assert-That ($null -eq $bareAcc.DisableLocalAuth -and $bareAcc.PrivateEndpoints -eq 0 -and $null -eq $bareAcc.Owner -and $null -eq $bareAcc.Tags) 'an account Azure says little of: unknown stays empty, not false'
$diag = ConvertFrom-DiagnosticSetting ('{"value":[{"properties":{"logs":[{"categoryGroup":"audit","enabled":true},{"category":"RequestResponse","enabled":true},{"category":"Trace","enabled":false}]}}]}' | ConvertFrom-Json)
Assert-That ($diag.Count -eq 1 -and $diag.Categories -eq 'audit, RequestResponse' -and (ConvertFrom-DiagnosticSetting ('{"value":[]}' | ConvertFrom-Json)).Count -eq 0) 'its logging: how many settings send logs, and which categories are on'
$strict = '{"name":"Default","properties":{"basePolicyName":"Microsoft.Default","mode":"Default","contentFilters":[{"name":"hate","severityThreshold":"Medium","blocking":true,"enabled":true,"source":"Prompt"}]}}' | ConvertFrom-Json
$loose = '{"name":"Loose","properties":{"basePolicyName":"Microsoft.Default","mode":"Default","contentFilters":[{"name":"selfharm","severityThreshold":"High","blocking":true,"enabled":true,"source":"Completion"}]}}' | ConvertFrom-Json
$off = '{"name":"Off","properties":{"contentFilters":[{"name":"jailbreak","blocking":false,"enabled":true,"source":"Prompt"}]}}' | ConvertFrom-Json
$fr = ConvertTo-FoundryFilterRow $loose $where
Assert-That (-not (Test-RelaxedFilter $strict) -and (Test-RelaxedFilter $loose) -and (Test-RelaxedFilter $off) -and $fr.Policy -eq 'Loose' -and $fr.Relaxed -and $fr.BasePolicy -eq 'Microsoft.Default' -and $fr.Detail -like '*selfharm*') `
    'a content filter is relaxed when a harm category blocks only high severity, or a filter is off or not blocking; the default is not'
$deployedSet = [System.Collections.Generic.HashSet[string]]::new([string[]] @('gpt-4o|2024-11-20'))
$models = ConvertFrom-FoundryModelList ('{"value":[{"model":{"name":"gpt-4o","version":"2024-11-20","lifecycleStatus":"GenerallyAvailable","deprecation":{"inference":"2026-12-01T00:00:00Z"},
    "skus":[{"name":"Standard","deprecationDate":"2026-11-15T00:00:00Z"},{"name":"GlobalStandard"}]}},{"model":{"name":"gpt-35-turbo","version":"0125","deprecation":{"inference":"2026-01-01T00:00:00Z"}}}]}' | ConvertFrom-Json) 'eastus' $deployedSet
Assert-That ($models.Count -eq 2 -and $models[0].SkuName -eq '' -and $models[0].RetiresDate -eq [datetime] '2026-12-01' -and $models[1].SkuName -eq 'Standard' -and $models[1].RetiresDate -eq [datetime] '2026-11-15') `
    'when each deployed model retires, and a SKU of it that retires sooner; models not deployed there are left out'
$stat = ConvertFrom-ArmMetricStat ('{"value":[{"name":{"value":"AzureOpenAIProvisionedManagedUtilizationV2"},"timeseries":[{"metadatavalues":[{"name":{"value":"ModelDeploymentName"},"value":"ptu"}],
    "data":[{"timeStamp":"2026-10-05T00:00:00Z","average":21.5,"maximum":64.25},{"timeStamp":"2026-10-06T00:00:00Z"}]}]}]}' | ConvertFrom-Json)
Assert-That ($stat.Count -eq 1 -and $stat[0].Deployment -eq 'ptu' -and $stat[0].Average -eq 21.5 -and $stat[0].Maximum -eq 64.25) 'a provisioned deployment''s daily use: its average and highest, a day with neither left out'
$urows = [System.Collections.Generic.List[object]]::new(); $urows.Add([pscustomobject]@{ Account = 'foundry-east'; Deployment = 'chat'; Day = [datetime] '2026-10-05'; Requests = 5 })
Add-PtuUtilization -Rows $urows -Stat $stat -Where $where -ModelOf @{ ptu = [pscustomobject]@{ ModelName = 'gpt-4o'; ModelVersion = '2024-11-20' } }
Assert-That ($urows.Count -eq 2 -and $urows[1].Deployment -eq 'ptu' -and $urows[1].ModelName -eq 'gpt-4o' -and $urows[1].PtuUtilizationAvg -eq 21.5 -and $null -eq $urows[1].Requests) 'it goes on the deployment''s day, a new row where that day had no requests'
$gb = Get-ResourceGraphBody -SkipToken 'next'
Assert-That ($gb.query -like "*microsoft.cognitiveservices/accounts*OpenAI*AIServices*" -and $gb.options['$skipToken'] -eq 'next' -and -not (Get-ResourceGraphBody).options.Contains('$skipToken')) 'Resource Graph is asked for every Azure OpenAI and Foundry account, page by page'
$seen = ConvertFrom-ResourceGraph ('{"data":[{"id":"' + $id + '","kind":"AIServices","location":"eastus"},{"id":"/subscriptions/CCCC/resourceGroups/rg/providers/Microsoft.CognitiveServices/accounts/other","kind":"OpenAI"},{"id":"/x"}]}' | ConvertFrom-Json) @('AAAA1111-0000-0000-0000-000000000000')
Assert-That ($seen.Count -eq 2 -and $seen[0].Loaded -and -not $seen[1].Loaded -and $seen[1].Account -eq 'other') 'each account seen, marked loaded when its subscription is one the load reads'

# The wider AI bill (3.4.0).
Assert-That ((Get-AiService 'Microsoft.CognitiveServices/accounts' 'gpt-4o Input Tokens') -eq 'Foundry and Azure OpenAI' -and
             (Get-AiService 'microsoft.powerplatform/accounts' 'Copilot Studio Messages') -eq 'Copilot Studio' -and
             (Get-AiService 'microsoft.powerplatform/accounts' 'Dataverse Database Capacity') -eq 'Power Platform' -and
             (Get-AiService 'microsoft.syntex/accounts' 'Microsoft 365 Copilot Chat Messages') -eq 'Microsoft 365 Copilot pay-as-you-go' -and
             (Get-AiService 'microsoft.syntex/accounts' 'SharePoint Archive') -eq 'SharePoint and Syntex pay-as-you-go' -and
             (Get-AiService 'microsoft.web/sites' 'x') -eq 'Other AI services') 'each cost line under its service: Copilot Studio apart from the rest of the Power Platform by its meter'
$ab = Get-AiCostQueryBody -From ([datetime] '2026-10-01') -To ([datetime] '2026-10-03') -Type AmortizedCost -ResourceTypes 'microsoft.cognitiveservices/accounts', 'microsoft.powerplatform/accounts'
Assert-That ($ab.type -eq 'AmortizedCost' -and $ab.dataset.granularity -eq 'Daily' -and $ab.dataset.grouping.Count -eq 2 -and $ab.dataset.filter.dimensions.values.Count -eq 2 -and
             $ab.timePeriod.to -eq '2026-10-03T23:59:59Z') 'the wider bill asks for amortized cost a day, by resource and meter, of the AI resource types'
Assert-That (Test-Throw { Get-AiCostQueryBody -From ([datetime] '2026-10-01') -To ([datetime] '2026-10-01') -Type Usage }) 'only actual or amortized cost'
$fb = Get-AiForecastBody -From ([datetime] '2026-10-20') -To ([datetime] '2026-10-31') -ResourceTypes 'microsoft.cognitiveservices/accounts'
Assert-That ($fb.includeActualCost -eq $false -and $fb.timePeriod.from -eq '2026-10-20T00:00:00Z' -and -not $fb.dataset.Contains('grouping')) 'the forecast: the days ahead only, not grouped'
$line = [pscustomobject]@{ ResourceId = '/subscriptions/AAAA/resourceGroups/rg-pp/providers/Microsoft.PowerPlatform/accounts/billing-hr'; Meter = 'Copilot Studio Messages'; Day = [datetime] '2026-10-05'; Cost = [decimal] 12.5; Currency = 'USD' }
$acr = ConvertTo-AiCostRow $line '/providers/Microsoft.Billing/billingAccounts/123' 'Amortized'
Assert-That ($acr.Service -eq 'Copilot Studio' -and $acr.ResourceType -eq 'microsoft.powerplatform/accounts' -and $acr.SubscriptionId -eq 'aaaa' -and $acr.ResourceGroup -eq 'rg-pp' -and
             $acr.ResourceName -eq 'billing-hr' -and $acr.CostType -eq 'Amortized' -and $acr.Scope -like '*billingAccounts/123') 'a cost line as a row: its service, subscription, group and name from its id'
$noId = ConvertTo-AiCostRow ([pscustomobject]@{ ResourceId = ''; Meter = 'x'; Day = [datetime] '2026-10-05'; Cost = [decimal] 1; Currency = 'USD' }) '/subscriptions/a' 'Actual'
Assert-That ($null -eq $noId.ResourceType -and $null -eq $noId.SubscriptionId -and $noId.Service -eq 'Other AI services') 'a line with no resource is still a row'
$budget = ConvertFrom-AiBudget ('{"value":[{"name":"ai-monthly","properties":{"category":"Cost","amount":5000,"timeGrain":"Monthly","timePeriod":{"startDate":"2026-10-01T00:00:00Z"},
    "currentSpend":{"amount":3120.4,"unit":"USD"},"forecastSpend":{"amount":5480,"unit":"USD"},"filter":{"dimensions":{"name":"ResourceType","operator":"In","values":["microsoft.cognitiveservices/accounts"]}}}},
    {"name":"bare","properties":{}}]}' | ConvertFrom-Json) '/subscriptions/a'
Assert-That ($budget.Count -eq 2 -and $budget[0].Amount -eq 5000 -and $budget[0].CurrentSpend -eq [decimal] 3120.4 -and $budget[0].ForecastSpend -eq 5480 -and $budget[0].Currency -eq 'USD' -and
             $budget[0].StartDate -eq [datetime] '2026-10-01' -and $null -eq $budget[0].EndDate -and $budget[0].Filter -like '*cognitiveservices*' -and $null -eq $budget[1].Amount) `
    'budgets: amount, spent, forecast and filter; a budget with nothing set is still listed'
$fc = ConvertFrom-AiForecast ('{"properties":{"columns":[{"name":"Cost"},{"name":"UsageDate"},{"name":"CostStatus"},{"name":"Currency"}],
    "rows":[[10.5,20261020,"Forecast","USD"],[2,20261020,"Forecast","USD"],[99,20261019,"Actual","USD"],[11,20261021,"Forecast","USD"]]}}' | ConvertFrom-Json) '/subscriptions/a'
Assert-That ($fc.Count -eq 2 -and $fc[0].Day -eq [datetime] '2026-10-20' -and $fc[0].Cost -eq [decimal] 12.5 -and $fc[1].Cost -eq 11) 'the forecast a day, its lines added up, actual days left out'
Assert-That ((ConvertFrom-AiForecast $null 's').Count -eq 0) 'no forecast is no rows'
Assert-That ((@(Get-AiCostScope -Configured @() -Subs 'aaaa', 'bbbb') -join ',') -eq '/subscriptions/aaaa,/subscriptions/bbbb' -and
             (@(Get-AiCostScope -Configured 'providers/Microsoft.Billing/billingAccounts/123/', '/subscriptions/aaaa' -Subs 'x') -join ',') -eq '/providers/Microsoft.Billing/billingAccounts/123,/subscriptions/aaaa') `
    'the scopes: each subscription read by default, else those set, each starting with /'

# ---- 4. The nightly run --------------------------------------------------------------------------
Write-Host "`n4. Invoke-CopilotPipeline.ps1" -ForegroundColor Cyan
. (Join-Path $root 'Invoke-CopilotPipeline.ps1')
$s = @{ TenantId = 't'; ClientId = 'c'; CertificateThumbprint = 'AB'; SqlServer = 'SQL1'; Database = 'CopilotAudit'; TrustServerCertificate = $true }
$today = [datetime] '2026-10-08'
$plain = Get-PipelineStep -Setting $s -Today $today -Root 'R:'
Assert-That (@($plain | Where-Object { $_.Name -match 'GitHub|Foundry' }).Count -eq 0 -and @($plain | Where-Object Optional).Count -eq 0) 'with neither source in the settings, the nightly run has neither step, and no optional one'

$notes = Get-PipelineSkipNote $s
Assert-That ($notes.Count -eq 3 -and $notes[2] -like 'AI apps, Power BI and Power Apps activity: skipped, not configured*ActivityDatabase*' -and $notes[0] -like 'GitHub Copilot usage: skipped, not configured*GitHubEnterprise*' -and $notes[1] -like 'Foundry usage and cost: skipped, not configured*FoundrySubscriptions*') `
    'and the log gets a note for each that it was skipped, naming the setting that turns it on'
Assert-That ((Get-PipelineSkipNote ($s + @{ GitHubOrganization = 'octo'; FoundrySubscriptions = 'x'; ActivityDatabase = 'CopilotActivity' })).Count -eq 0) 'no note for a loader that is configured'
$noCert = @{ TenantId = 't'; ClientId = 'c'; CertificateThumbprint = ''; SqlServer = 'SQL1'; Database = 'CopilotAudit'; FoundrySubscriptions = 'x'; GitHubEnterprise = 'contoso' }
Assert-That (@(Get-PipelineStep -Setting $noCert -Today $today | Where-Object { $_.Name -match 'Foundry' }).Count -eq 0 -and (Get-PipelineSkipNote $noCert)[0] -like '*no certificate to sign in with*') `
    'subscriptions with no certificate to sign in with: skipped with a note as well, not a failing step'

$gh = Get-PipelineStep -Setting ($s + @{ GitHubEnterprise = 'contoso'; GitHubTokenPath = 'D:\secrets\gh.token' }) -Today $today -Root 'R:'
$g = $gh[-1]
Assert-That ($g.Name -eq 'GitHub Copilot usage' -and $g.Optional -and $g.Script -eq [System.IO.Path]::Combine('R:', 'tools/Import-GitHubCopilotUsage.ps1') -and $gh[-2].Name -eq 'Copilot interactions') `
    'GitHubEnterprise adds the GitHub step, optional, after the Copilot load'
Assert-That ($g.Arguments.Enterprise -eq 'contoso' -and -not $g.Arguments.ContainsKey('Organization') -and $g.Arguments.TokenPath -eq 'D:\secrets\gh.token' -and $g.Arguments.SqlServer -eq 'SQL1' -and
             $g.Arguments.Database -eq 'CopilotAudit' -and $g.Arguments.TrustServerCertificate -and -not $g.Arguments.ContainsKey('Token')) 'for the enterprise, into the Copilot database, with where its token is and never the token'
$org = (Get-PipelineStep -Setting ($s + @{ GitHubOrganization = 'octo' }) -Today $today)[-1].Arguments
Assert-That ($org.Organization -eq 'octo' -and -not $org.ContainsKey('Enterprise')) 'GitHubOrganization loads an organization instead'

$subs = 'aaaa1111-0000-0000-0000-000000000000', 'bbbb2222-0000-0000-0000-000000000000'
$fo = Get-PipelineStep -Setting ($s + @{ GitHubEnterprise = 'contoso'; FoundrySubscriptions = $subs }) -Today $today -Root 'R:'
$f = $fo[-1]
Assert-That ($f.Name -eq 'Foundry usage and cost' -and $f.Optional -and $fo[-2].Name -eq 'GitHub Copilot usage' -and (@($f.Arguments.SubscriptionId) -join ',') -eq ($subs -join ',')) `
    'FoundrySubscriptions adds the Foundry step, optional and last, for every subscription named'
Assert-That ($f.Arguments.TenantId -eq 't' -and $f.Arguments.ClientId -eq 'c' -and $f.Arguments.CertificateThumbprint -eq 'AB') 'signed in as the pipeline''s own app where the settings name no other'
$own = @{ FoundrySubscriptions = $subs[0]; FoundryTenantId = 'ai.onmicrosoft.com'; FoundryClientId = 'f'; FoundryCertificateThumbprint = 'cd ef' }
$fa = (Get-PipelineStep -Setting ($s + $own) -Today $today)[-1].Arguments
Assert-That ($fa.TenantId -eq 'ai.onmicrosoft.com' -and $fa.ClientId -eq 'f' -and $fa.CertificateThumbprint -eq 'cd ef' -and @($fa.SubscriptionId).Count -eq 1) 'or as an app of its own, in a tenant of its own'
Assert-That (@(Get-PipelineStep -Setting ($s + @{ GitHubEnterprise = 'contoso'; FoundrySubscriptions = $subs }) -History -Today $today | Where-Object { $_.Name -match 'GitHub|Foundry' }).Count -eq 0) `
    'the first load of history has neither: each loader takes -Days for its own history'

$uses = Get-PipelineCertificateUse ($s + $own)
Assert-That (($uses.Purpose -join '|') -eq 'Purview|Foundry' -and $uses[1].Thumbprint -eq 'CDEF' -and $uses[1].ClientId -eq 'f' -and $uses[1].TenantId -eq 'ai.onmicrosoft.com') `
    'a certificate of Foundry''s own is tracked with the others, for Admin > Health and the expiry alert'
Assert-That ((Get-PipelineCertificateUse ($s + @{ FoundrySubscriptions = $subs })).Count -eq 1) 'signed in with the audit app''s certificate, there is no other to track'

# The activity database, loaded by record type, after the Copilot load and optional; its history on its own.
$act = $s + @{ ActivityDatabase = 'CopilotActivity' }
$an = Get-PipelineStep -Setting $act -Today $today -Root 'R:'
$load = $an | Where-Object Name -eq 'AI apps, Power BI and Power Apps'
$roll = $an | Where-Object Name -eq 'AI apps, Power BI and Power Apps rollup'
Assert-That ($load.Optional -and $roll.Optional -and $load.Arguments.Database -eq 'CopilotActivity' -and $load.Arguments.Incremental -and
             (@($load.Arguments.RecordTypes) -join ',') -eq 'ConnectedAIAppInteraction,PowerBIAudit,PowerAppsApp,TeamCopilotInteraction' -and -not $load.Arguments.ContainsKey('Operations')) `
    'ActivityDatabase adds an optional incremental load of the three record types into it, naming no operation'
Assert-That ([array]::IndexOf(@($an.Name), 'Copilot interactions') -lt [array]::IndexOf(@($an.Name), 'AI apps, Power BI and Power Apps') -and
             $roll.Script -eq [System.IO.Path]::Combine('R:', 'tools/Update-ActivityRollup.ps1') -and $roll.Arguments.Database -eq 'CopilotAudit' -and $roll.Arguments.ActivityDatabase -eq 'CopilotActivity') `
    'after the Copilot load, then its rollup into the Copilot database'
$ah = Get-PipelineStep -Setting $act -ActivityHistory -Today $today -Root 'R:'
Assert-That ($ah.Count -eq 2 -and $ah[0].Arguments.StartDate -eq $today.AddDays(-180) -and $ah[0].Arguments.Finalize -and $ah[1].Arguments.Full -and -not ($ah | Where-Object Name -match 'Copilot interactions')) `
    '-ActivityHistory loads only the activity history, 180 days, finalized, and rolls up every day: Copilot interactions are not loaded again'
Assert-That (Test-Throw { Get-PipelineStep -Setting $s -ActivityHistory -Today $today }) '-ActivityHistory without an activity database is refused'
$typed = Get-PipelineStep -Setting ($act + @{ ActivityRecordTypes = @('PowerBIAudit') }) -Today $today
$types = ($typed | Where-Object Name -eq 'AI apps, Power BI and Power Apps').Arguments.RecordTypes
Assert-That ((@($types) -join ',') -eq 'PowerBIAudit') 'ActivityRecordTypes in the settings replaces the list'
$hist = Get-PipelineStep -Setting $act -History -Today $today
Assert-That (($hist | Where-Object Name -eq 'AI apps, Power BI and Power Apps (history)').Optional -and $hist[-1].Name -eq 'AI apps, Power BI and Power Apps rollup') 'the first load of history loads it too, optional, last'

. (Join-Path $root 'tools' 'Update-ActivityRollup.ps1')
Assert-That ($null -eq (Get-ActivityRollupSince -Full) -and (Get-ActivityRollupSince -Today $today) -eq $today.AddDays(-3) -and (Get-ActivityRollupSince -SinceUtc ([datetime] '2026-09-01T05:00') -Today $today) -eq [datetime] '2026-09-01') `
    'the rollup: every day with -Full, else three days back, or the day given'

Write-Host ''
if ($script:Failures.Count) { Write-Host "$($script:Failures.Count) assertion(s) failed." -ForegroundColor Red; exit 1 }
Write-Host 'All external usage tests passed.' -ForegroundColor Green
