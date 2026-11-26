<#
    Tests tools\Import-SpendLimits.ps1: the spending limits set where AI spend happens, read from Microsoft
    Graph (Copilot spending policies, beta) and GitHub (an enterprise's budgets) into audit.SpendLimitSource.

      1  dot-sourced, nothing sent anywhere: a policy for all users and one for selected groups as rows, an
         unknown extra field kept in Raw, a changed shape as a note and never an error, a budget of each
         scope as a row, how a run ends and its exit code
      2  end to end, against a stand-in for Graph and GitHub on this machine and a throwaway database of its
         own (SlTest<random>, dropped after): both sources loaded, GitHub's budgets over two pages, a GitHub
         403 as a note, a changed shape as a note, the delete guard on each source, -AllowDrop, and
         -CheckOnly writing nothing

    The SQL Server is the one Test-SqlIntegration.ps1 uses: SQLIT_SERVER (default localhost), SQLIT_USER and
    SQLIT_PASSWORD, or Windows authentication when SQLIT_PASSWORD is not set. Without a reachable server,
    part 2 is skipped and said so.

    pwsh -NoProfile -File tests/Test-SpendLimitSources.ps1
#>
#Requires -Version 7.2
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot 'TestHelpers.ps1')
function Get-ErrorOf([scriptblock] $Do) { try { & $Do | Out-Null; $null } catch { $_ } }

$loader = Join-Path $root 'tools' 'Import-SpendLimits.ps1'

# ---- 1. Pure ------------------------------------------------------------------------------------
Write-Host "`n1. tools\Import-SpendLimits.ps1, nothing sent" -ForegroundColor Cyan
. $loader

$allUsers = '{"@odata.type":"#microsoft.graph.allUsersSpendingPolicy","id":"p-all","displayName":"Tenant wide baseline","status":"enabled",' +
            '"periodLimit":{"@odata.type":"#microsoft.graph.limitedCostManagementQuantity","value":250000,"unit":"copilotCredit"},' +
            '"perUserLimit":{"@odata.type":"#microsoft.graph.unlimitedCostManagementQuantity","unit":"copilotCredit"},"billingMethods":"notSpecified","createdDateTime":"2026-06-02T11:00:00Z"}'
$r = ConvertTo-SpendPolicyRow -Raw $allUsers -Groups $null
$perUser = @($r.Rows | Where-Object ExternalId -eq 'p-all')[0]; $period = @($r.Rows | Where-Object ExternalId -eq 'p-all/period')[0]
Assert-That ($r.Rows.Count -eq 2 -and $r.Notes.Count -eq 0) 'a policy for all users: a row for its per-user limit and one for its capped period limit, no notes'
Assert-That ($perUser.Source -eq 'Graph' -and $perUser.Kind -eq 'CopilotUser' -and $perUser.ScopeType -eq 'AllUsers' -and $null -eq $perUser.Scope -and $perUser.PerUser -eq $true -and
             $null -eq $perUser.Amount -and $perUser.Unit -eq 'Credits' -and $perUser.Period -eq 'Monthly' -and $perUser.Name -eq 'Tenant wide baseline') `
    'its per-user row: CopilotUser, AllUsers, PerUser 1, no amount where there is no per-user cap, Credits a month'
Assert-That ($period.PerUser -eq $false -and $period.Amount -eq 250000 -and $period.Raw -eq $allUsers) 'its period row: the whole audience''s cap, PerUser 0, and Raw exactly as Graph sent it (dates untouched)'

$groupsPolicy = '{"@odata.type":"#microsoft.graph.selectedGroupsSpendingPolicy","id":"p-pilot","displayName":"Pilot teams monthly cap","status":"enabled",' +
                '"periodLimit":{"@odata.type":"#microsoft.graph.unlimitedCostManagementQuantity","unit":"copilotCredit"},' +
                '"perUserLimit":{"@odata.type":"#microsoft.graph.limitedCostManagementQuantity","value":500,"unit":"copilotCredit"},"futureField":{"x":1}}'
$groups = @([pscustomobject]@{ id = 'g-1'; displayName = 'Pilot group A' }, [pscustomobject]@{ id = 'g-2' })
$g = ConvertTo-SpendPolicyRow -Raw $groupsPolicy -Groups $groups
Assert-That ($g.Rows.Count -eq 1 -and $g.Rows[0].ScopeType -eq 'Group' -and $g.Rows[0].Scope -eq 'Pilot group A (g-1); g-2' -and $g.Rows[0].Amount -eq 500 -and $g.Rows[0].PerUser -eq $true) `
    'a policy for selected groups: its groups by name and id as the Scope, its per-user limit; an uncapped period has no row'
Assert-That ($g.Notes.Count -eq 0 -and $g.Rows[0].Raw -like '*futureField*') 'an unknown extra field is no note, and is kept in Raw'
Assert-That ($null -eq (ConvertTo-SpendPolicyRow -Raw $groupsPolicy -Groups $null).Rows[0].Scope) 'groups not read: the Scope is NULL, the limit still loads'

$odd = ConvertTo-SpendPolicyRow -Raw '{"@odata.type":"#microsoft.graph.departmentSpendingPolicy","id":"p-odd","perUserLimit":"lots","periodLimit":{"value":"12.5"}}' -Groups $null
Assert-That ($odd.Rows.Count -eq 2 -and $null -eq $odd.Rows[0].ScopeType -and $null -eq $odd.Rows[0].Amount -and $odd.Rows[1].Amount -eq 12.5) `
    'a changed shape (an unknown type, a limit that is a word): still rows, the unreadable amount NULL, a number sent as text read'
Assert-That (@($odd.Notes | Where-Object { $_ -like '*type this load does not know*' }).Count -eq 1 -and @($odd.Notes | Where-Object { $_ -like '*per-user limit*no amount*' }).Count -eq 1) `
    'and each oddity is a note'
$env1 = ConvertTo-SpendPolicyRow -Raw '{"@odata.type":"#microsoft.graph.environmentSpendingPolicy","id":"p-env","environmentId":"env-42","periodLimit":{"value":900}}' -Groups $null
Assert-That ($env1.Rows[0].Kind -eq 'CopilotResource' -and $env1.Rows[0].ScopeType -eq 'Environment' -and $env1.Rows[0].Scope -eq 'env-42' -and $env1.Notes.Count -ge 1) `
    'a policy for an environment, should Graph add one: CopilotResource, the environment as its Scope, and a note'
Assert-That ((ConvertTo-SpendPolicyRow -Raw '[1,2]' -Groups $null).Rows.Count -eq 0 -and (ConvertTo-SpendPolicyRow -Raw '{"displayName":"no id"}' -Groups $null).Notes[0] -like '*no id*' -and
             (ConvertTo-SpendPolicyRow -Raw 'not json' -Groups $null).Notes.Count -eq 1) 'a policy that is not an object, or has no id, is left out with a note, not an error'
$unit = ConvertTo-SpendPolicyRow -Raw '{"@odata.type":"#microsoft.graph.allUsersSpendingPolicy","id":"p-u","perUserLimit":{"value":3,"unit":"token"}}' -Groups $null
Assert-That ($unit.Rows[0].Amount -eq 3 -and @($unit.Notes | Where-Object { $_ -like "*'token'*" }).Count -eq 1) 'a unit other than Copilot Credits is a note'

$page = Split-JsonList '{"@odata.nextLink":"https://next","value":[{"id":"a","when":"2026-06-02T11:00:00Z"},{"id":"b"}]}' 'value'
Assert-That ($page.Items.Count -eq 2 -and $page.Items[0] -eq '{"id":"a","when":"2026-06-02T11:00:00Z"}' -and $page.Meta.'@odata.nextLink' -eq 'https://next') 'a page: each item''s JSON exactly as sent, and the next page''s address'
Assert-That ((Get-SourceErrorKind (Get-ErrorOf { Split-JsonList '{"policies":[]}' 'value' })) -eq 'Shape' -and (Get-SourceErrorKind (Get-ErrorOf { Split-JsonList '<html>' 'value' })) -eq 'Shape' -and
             (Get-SourceErrorKind (Get-ErrorOf { Split-JsonList '{"value":{}}' 'value' })) -eq 'Shape') 'a page with no list, not JSON, or a list that is not one, is a shape error, which the run names'

$b = ConvertTo-GitHubBudgetRow -Raw '{"id":"b-1","budget_type":"ProductPricing","budget_amount":1500,"prevent_further_usage":true,"budget_scope":"organization","budget_entity_name":"octo","budget_product_sku":"copilot","budget_alerting":{"will_alert":true,"alert_recipients":["mona"]},"new_thing":1}' -Enterprise 'example-ent'
Assert-That ($b.Row.Source -eq 'GitHub' -and $b.Row.Kind -eq 'GitHubBudget' -and $b.Row.ExternalId -eq 'b-1' -and $b.Row.ScopeType -eq 'Organization' -and $b.Row.Scope -eq 'octo' -and
             $b.Row.Amount -eq 1500 -and $b.Row.Unit -eq 'USD' -and $b.Row.Period -eq 'Monthly' -and $b.Row.PerUser -eq $false -and $b.Row.Name -eq 'copilot (ProductPricing)' -and
             $b.Notes.Count -eq 0 -and $b.Row.Raw -like '*new_thing*') 'a GitHub budget: its scope and entity, the amount in USD a month, its SKU, Raw kept whole'
$scopes = @{ enterprise = 'Enterprise'; cost_center = 'CostCenter'; user = 'User'; repository = 'Repository'; multi_user_cost_center = 'CostCenter' }
$ok = $true
foreach ($k in $scopes.Keys) { $x = (ConvertTo-GitHubBudgetRow -Raw "{`"id`":`"$k`",`"budget_scope`":`"$k`",`"budget_amount`":1}" -Enterprise 'example-ent').Row; if ($x.ScopeType -ne $scopes[$k]) { $ok = $false } }
Assert-That $ok 'each of GitHub''s budget scopes as its ScopeType'
Assert-That ((ConvertTo-GitHubBudgetRow -Raw '{"id":"e","budget_scope":"enterprise","budget_amount":5}' -Enterprise 'example-ent').Row.Scope -eq 'example-ent' -and
             (ConvertTo-GitHubBudgetRow -Raw '{"id":"m","budget_scope":"multi_user_customer","budget_amount":5}' -Enterprise 'x').Row.PerUser -eq $true) `
    'a budget of the whole enterprise names it; a multi-user budget is each person''s'
$weird = ConvertTo-GitHubBudgetRow -Raw '{"id":"w","budget_scope":"galaxy","budget_amount":"a lot"}' -Enterprise 'x'
Assert-That ($weird.Row.ScopeType -eq 'galaxy' -and $null -eq $weird.Row.Amount -and $weird.Notes.Count -eq 2) 'an unknown scope or an unreadable amount: still a row, with notes'
Assert-That ($null -eq (ConvertTo-GitHubBudgetRow -Raw '{"budget_amount":5}' -Enterprise 'x').Row) 'a budget with no id is left out'

$u = Get-UniqueLimit @([pscustomobject]@{ ExternalId = 'A'; N = 1 }, [pscustomobject]@{ ExternalId = 'a'; N = 2 }, [pscustomobject]@{ ExternalId = 'b'; N = 3 })
Assert-That ($u.Rows.Count -eq 2 -and $u.Repeats -eq 1 -and $u.Rows[0].N -eq 2) 'an id given twice is kept once, the last'

$o = Get-RunOutcome @{ Graph = 'Loaded'; GitHub = 'Loaded' } $false
Assert-That ($o.Status -eq 'Succeeded' -and $o.ExitCode -eq 0) 'both loaded, no notes: Succeeded, exit 0'
Assert-That ((Get-RunOutcome @{ Graph = 'Loaded'; GitHub = 'Refused' } $false).Status -eq 'Partial' -and (Get-RunOutcome @{ Graph = 'Loaded'; GitHub = 'Refused' } $false).ExitCode -eq 0) 'one refused: Partial, exit 0'
Assert-That ((Get-RunOutcome @{ Graph = 'Loaded'; GitHub = 'NotConfigured' } $false).Status -eq 'Succeeded') 'one not configured: the run ends as the other does'
Assert-That ((Get-RunOutcome @{ Graph = 'Guarded'; GitHub = 'Loaded' } $false).ExitCode -eq 3 -and (Get-RunOutcome @{ Graph = 'Guarded'; GitHub = 'Error' } $false).Status -eq 'Partial') 'the guard held one: Partial, exit 3'
Assert-That ((Get-RunOutcome @{ Graph = 'Error'; GitHub = 'Refused' } $false).Status -eq 'Failed' -and (Get-RunOutcome @{ Graph = 'Error' } $false).ExitCode -eq 1) 'a failure with nothing loaded: Failed, exit 1'
Assert-That ((Get-RunOutcome @{ Graph = 'NotConfigured'; GitHub = 'NotConfigured' } $false).Status -eq 'Partial' -and (Get-RunOutcome @{ Graph = 'Loaded' } $true).Status -eq 'Partial') 'nothing configured, or a note made: Partial'
Assert-That ($script:DropGuardSetting['Spend limits'] -eq 'SpendLimitMaxDropPercent' -and (Resolve-MaxDropPercent -What 'Spend limits' -Given 15 -WasGiven $false -Settings @{ SpendLimitMaxDropPercent = 40 }) -eq 40) `
    'the delete guard''s percentage comes from SpendLimitMaxDropPercent'
$text = [System.IO.File]::ReadAllText($loader)
Assert-That (-not ($text -match '[^\x00-\x7F]')) 'the loader is ASCII'

# ---- 2. End to end ------------------------------------------------------------------------------
Write-Host "`n2. End to end: a stand-in for Graph and GitHub, and a throwaway database" -ForegroundColor Cyan
$server = if ($env:SQLIT_SERVER) { $env:SQLIT_SERVER } else { 'localhost' }
function New-SlConnString([string] $Catalog) {
    $b = [System.Data.SqlClient.SqlConnectionStringBuilder]::new()
    $b['Data Source'] = $server; $b['Initial Catalog'] = $Catalog; $b['Encrypt'] = $true; $b['TrustServerCertificate'] = $true; $b['Connect Timeout'] = 10
    if ($env:SQLIT_PASSWORD) { $b['User ID'] = $(if ($env:SQLIT_USER) { $env:SQLIT_USER } else { 'sa' }); $b['Password'] = $env:SQLIT_PASSWORD } else { $b['Integrated Security'] = $true }
    $b.ConnectionString
}
function Invoke-Sl([string] $Sql, [string] $Catalog, [switch] $Scalar, [switch] $Table) {
    $c = [System.Data.SqlClient.SqlConnection]::new((New-SlConnString $Catalog))
    try {
        $c.Open(); $cmd = $c.CreateCommand(); $cmd.CommandText = $Sql; $cmd.CommandTimeout = 120
        if ($Scalar) { return $cmd.ExecuteScalar() }
        if ($Table) { $t = [System.Data.DataTable]::new(); $rd = $cmd.ExecuteReader(); try { $t.Load($rd) } finally { $rd.Dispose() }; return , $t }
        [void] $cmd.ExecuteNonQuery()
    }
    finally { $c.Dispose() }
}

$reachable = try { [void] (Invoke-Sl 'SELECT 1' 'master' -Scalar); $true } catch { Write-Host "  SKIP  no SQL Server at ${server}: $($_.Exception.Message)" -ForegroundColor Yellow; $false }
if ($reachable) {
    $db = 'SlTest' + [guid]::NewGuid().ToString('N').Substring(0, 10)
    $probe = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0); $probe.Start(); $port = $probe.LocalEndpoint.Port; $probe.Stop()
    $modeFile = Join-Path ([System.IO.Path]::GetTempPath()) "sltest-mode-$PID.json"
    function Set-Mode([string] $Graph = 'normal', [string] $GitHub = 'normal') { Set-Content -LiteralPath $modeFile -Value (@{ graph = $Graph; gh = $GitHub } | ConvertTo-Json -Compress) -Encoding utf8 }
    Set-Mode
    $mock = {
        param([int] $Port, [string] $ModeFile)
        $base = "http://localhost:$Port"
        $listener = [System.Net.HttpListener]::new(); $listener.Prefixes.Add("$base/"); $listener.Start()
        function Send($ctx, [int] $status, [string] $body = '{}') {
            $ctx.Response.StatusCode = $status
            $bytes = [System.Text.Encoding]::UTF8.GetBytes($body)
            $ctx.Response.ContentType = 'application/json'; $ctx.Response.ContentLength64 = $bytes.Length
            if ($bytes.Length) { $ctx.Response.OutputStream.Write($bytes, 0, $bytes.Length) }
            $ctx.Response.Close()
        }
        $q = '"periodLimit":{"@odata.type":"#microsoft.graph.limitedCostManagementQuantity","value":QQ,"unit":"copilotCredit"}'
        $allUsers = '{"@odata.type":"#microsoft.graph.allUsersSpendingPolicy","id":"p-all","displayName":"Tenant wide baseline","status":"enabled",' + $q.Replace('QQ', '250000') +
                    ',"perUserLimit":{"@odata.type":"#microsoft.graph.unlimitedCostManagementQuantity","unit":"copilotCredit"},"createdDateTime":"2026-06-02T11:00:00Z"}'
        $pilot = '{"@odata.type":"#microsoft.graph.selectedGroupsSpendingPolicy","id":"p-pilot","displayName":"Pilot teams monthly cap","status":"enabled",' + $q.Replace('QQ', '10000') +
                 ',"perUserLimit":{"@odata.type":"#microsoft.graph.limitedCostManagementQuantity","value":500,"unit":"copilotCredit"},"brandNewField":"kept"}'
        $odd = '{"@odata.type":"#microsoft.graph.someNewSpendingPolicy","id":"p-odd","displayName":"Odd one","perUserLimit":"lots"}'
        function Budget([string] $id, [string] $scope, [string] $entity, [int] $amount) { "{`"id`":`"$id`",`"budget_type`":`"ProductPricing`",`"budget_amount`":$amount,`"prevent_further_usage`":false,`"budget_scope`":`"$scope`",`"budget_entity_name`":`"$entity`",`"budget_product_sku`":`"copilot`",`"budget_alerting`":{`"will_alert`":false,`"alert_recipients`":[]}}" }
        while ($listener.IsListening) {
            $ctx = $listener.GetContext(); $rq = $ctx.Request; $path = $rq.Url.AbsolutePath; $qs = $rq.QueryString; $auth = "$($rq.Headers['Authorization'])"
            try {
                if ($path -eq '/stop') { Send $ctx 200; break }
                $mode = Get-Content -LiteralPath $ModeFile -Raw | ConvertFrom-Json
                if ($path -like '/graph/*') {
                    if ($auth -ne 'Bearer graph-token') { Send $ctx 401 '{"error":{"code":"InvalidAuthenticationToken","message":"no"}}'; continue }
                    if ($mode.graph -eq '403') { Send $ctx 403 '{"error":{"code":"Authorization_RequestDenied","message":"Insufficient privileges."}}'; continue }
                    if ($path -eq '/graph/beta/copilot/costManagement/spendingPolicies') {
                        if ($mode.graph -eq 'shape') { Send $ctx 200 "{`"policies`":[$allUsers]}"; continue }
                        if ($mode.graph -eq 'fewer') { Send $ctx 200 "{`"value`":[$pilot]}"; continue }
                        if ($qs['$skiptoken'] -eq '2') { Send $ctx 200 "{`"value`":[$pilot$(if ($mode.graph -eq 'itemshape') { ",$odd" })]}"; continue }
                        Send $ctx 200 "{`"@odata.nextLink`":`"$base/graph/beta/copilot/costManagement/spendingPolicies?`$skiptoken=2`",`"value`":[$allUsers]}"; continue
                    }
                    if ($path -eq '/graph/beta/copilot/costManagement/spendingPolicies/p-pilot/microsoft.graph.selectedGroupsSpendingPolicy/assignedGroups') {
                        Send $ctx 200 '{"value":[{"id":"11112222-3333-4444-5555-666677778888","displayName":"Pilot group A"}]}'; continue
                    }
                    Send $ctx 404 '{"error":{"code":"notFound","message":"no such path"}}'; continue
                }
                if ($path -like '/gh/*') {
                    if ($auth -ne 'Bearer gh-secret-token' -or $rq.Headers['X-GitHub-Api-Version'] -ne '2026-03-10') { Send $ctx 401 '{"message":"Bad credentials"}'; continue }
                    if ($mode.gh -eq '403') { Send $ctx 403 '{"message":"Must have admin rights to the enterprise."}'; continue }
                    if ($path -eq '/gh/enterprises/example-ent/settings/billing/budgets' -and $qs['per_page'] -eq '100') {
                        if ($mode.gh -eq 'fewer') { Send $ctx 200 "{`"budgets`":[$(Budget 'b-1' 'enterprise' '' 5000)],`"has_next_page`":false,`"total_count`":1}"; continue }
                        if ($qs['page'] -eq '1') { Send $ctx 200 "{`"budgets`":[$(Budget 'b-1' 'enterprise' '' 5000),$(Budget 'b-2' 'organization' 'octo' 1500)],`"has_next_page`":true,`"total_count`":3}"; continue }
                        if ($qs['page'] -eq '2') { Send $ctx 200 "{`"budgets`":[$(Budget 'b-3' 'cost_center' 'Research' 800)],`"has_next_page`":false,`"total_count`":3}"; continue }
                        Send $ctx 200 '{"budgets":[],"has_next_page":false}'; continue
                    }
                    Send $ctx 404 '{"message":"Not Found"}'; continue
                }
                Send $ctx 404
            }
            catch { try { Send $ctx 500 (@{ message = "$_" } | ConvertTo-Json) } catch { Write-Verbose "$_" } }
        }
        $listener.Stop()
    }
    $job = Start-ThreadJob -ScriptBlock $mock -ArgumentList $port, $modeFile
    try {
        $up = $false
        foreach ($try in 1..50) { try { [void] (Invoke-WebRequest -Uri "http://localhost:$port/nothing" -SkipHttpErrorCheck -TimeoutSec 2); $up = $true; break } catch { Start-Sleep -Milliseconds 200 } }
        Assert-That $up 'the stand-in for Graph and GitHub is listening'
        Invoke-Sl "CREATE DATABASE [$db];" 'master'
        Invoke-Sl ([System.IO.File]::ReadAllText((Join-Path $root 'sql' 'Create-CopilotExternalUsageSchema.sql'))) $db
        $conn = New-SlConnString $db
        $noSettings = Join-Path ([System.IO.Path]::GetTempPath()) "no-settings-sl-$PID.psd1"
        $common = @{ ConnectionString = $conn; SettingsPath = $noSettings; GraphEndpoint = "http://localhost:$port/graph"; GraphAccessToken = [System.Net.NetworkCredential]::new('', 'graph-token').SecurePassword
                     Enterprise = 'example-ent'; Token = [System.Net.NetworkCredential]::new('', 'gh-secret-token').SecurePassword; ApiBase = "http://localhost:$port/gh" }
        function Invoke-Loader([hashtable] $Extra = @{}) {
            $p = $common.Clone()
            foreach ($k in $Extra.Keys) { $p[$k] = $Extra[$k] }
            $global:LASTEXITCODE = 0
            $out = & $loader @p 6>&1 3>&1 2>&1 | Out-String
            [pscustomobject]@{ Exit = $LASTEXITCODE; Out = $out }
        }
        function Get-LastRun { (Invoke-Sl "SELECT TOP (1) Status, Scope, RowsWritten, Detail, EndedUtc FROM audit.ExternalUsageRun WHERE Source = 'SpendLimits' ORDER BY RunId DESC;" $db -Table).Rows[0] }
        function Get-Count([string] $Src) { [int] (Invoke-Sl "SELECT COUNT(*) FROM audit.SpendLimitSource WHERE Source = '$Src';" $db -Scalar) }

        # Both sources, as they should answer.
        $a = Invoke-Loader
        $run = Get-LastRun
        Assert-That ($a.Exit -eq 0 -and "$($run.Status)" -eq 'Succeeded' -and [int] $run.RowsWritten -eq 7 -and $run.EndedUtc -isnot [DBNull]) "both sources load: Succeeded, 7 rows, exit 0 ($($run.Status), $($run.RowsWritten): $($run.Detail)) $(if ($a.Exit) { $a.Out })"
        Assert-That ("$($run.Scope)" -eq 'Graph, GitHub enterprise:example-ent') 'the run names what it read'
        $graphRows = Invoke-Sl "SELECT ExternalId, Kind, Name, ScopeType, Scope, Unit, Amount, Period, PerUser, Raw FROM audit.SpendLimitSource WHERE Source = 'Graph' ORDER BY ExternalId;" $db -Table
        Assert-That ((@($graphRows.Rows | ForEach-Object ExternalId) -join ',') -eq 'p-all,p-all/period,p-pilot,p-pilot/period') 'Graph: both pages of policies, a row for each per-user limit and each capped period'
        $pilotRow = @($graphRows.Rows | Where-Object { $_.ExternalId -eq 'p-pilot' })[0]
        Assert-That ("$($pilotRow.ScopeType)" -eq 'Group' -and "$($pilotRow.Scope)" -eq 'Pilot group A (11112222-3333-4444-5555-666677778888)' -and [decimal] $pilotRow.Amount -eq 500 -and [bool] $pilotRow.PerUser -and
                     "$($pilotRow.Unit)" -eq 'Credits' -and "$($pilotRow.Period)" -eq 'Monthly' -and "$($pilotRow.Raw)" -like '*brandNewField*') 'the selected-groups policy: its group, 500 credits each a month, its unknown field kept in Raw'
        $allRow = @($graphRows.Rows | Where-Object { $_.ExternalId -eq 'p-all' })[0]
        Assert-That ("$($allRow.ScopeType)" -eq 'AllUsers' -and $allRow.Amount -is [DBNull] -and $allRow.Scope -is [DBNull] -and "$($allRow.Raw)" -like '*2026-06-02T11:00:00Z*') 'the all-users policy: no per-user cap is NULL, and Raw as sent'
        $ghRows = Invoke-Sl "SELECT ExternalId, Kind, ScopeType, Scope, Unit, Amount, PerUser FROM audit.SpendLimitSource WHERE Source = 'GitHub' ORDER BY ExternalId;" $db -Table
        Assert-That ((@($ghRows.Rows | ForEach-Object { "$($_.ExternalId):$($_.ScopeType):$($_.Scope):$([int] $_.Amount)" }) -join ',') -eq 'b-1:Enterprise:example-ent:5000,b-2:Organization:octo:1500,b-3:CostCenter:Research:800' -and
                     "$($ghRows.Rows[0].Kind)" -eq 'GitHubBudget' -and "$($ghRows.Rows[0].Unit)" -eq 'USD') 'GitHub: the budgets of both pages, each with its scope, entity and amount in USD'
        Assert-That ($a.Out -notlike '*gh-secret-token*' -and $a.Out -notlike '*graph-token*' -and "$($run.Detail)" -notlike '*secret*') 'neither token is printed or recorded'

        # -CheckOnly: reads, writes nothing.
        $before = "$(Invoke-Sl "SELECT CONCAT((SELECT COUNT(*) FROM audit.SpendLimitSource), '/', (SELECT COUNT(*) FROM audit.ExternalUsageRun), '/', (SELECT MAX(LoadedUtc) FROM audit.SpendLimitSource));" $db -Scalar)"
        Set-Mode -Graph 'fewer'
        $c = Invoke-Loader @{ CheckOnly = $true; ConnectionString = 'Server=no-such-server.invalid;Database=x;Integrated Security=true;Connect Timeout=1' }
        $after = "$(Invoke-Sl "SELECT CONCAT((SELECT COUNT(*) FROM audit.SpendLimitSource), '/', (SELECT COUNT(*) FROM audit.ExternalUsageRun), '/', (SELECT MAX(LoadedUtc) FROM audit.SpendLimitSource));" $db -Scalar)"
        Assert-That ($c.Exit -eq 0 -and $c.Out -match 'OK\s+Graph: readable' -and $c.Out -match 'OK\s+GitHub: readable, 3 budget' -and $before -eq $after) "-CheckOnly reads each source and writes nothing, needing no database ($before, $after)"
        Set-Mode -GitHub '403'
        $c2 = Invoke-Loader @{ CheckOnly = $true }
        Assert-That ($c2.Exit -eq 1 -and $c2.Out -match 'MISSING\s+GitHub' -and $c2.Out -like '*billing manager*') '-CheckOnly: a source that refuses is MISSING, with what to grant, exit 1'

        # The delete guard, Graph: one policy where there were two.
        Set-Mode -Graph 'fewer'
        $f = Invoke-Loader
        $run = Get-LastRun
        Assert-That ($f.Exit -eq 3 -and "$($run.Status)" -eq 'Partial' -and (Get-Count 'Graph') -eq 4 -and "$($run.Detail)" -like '*Spend limits from Graph*delete guard*') `
            "the guard keeps Graph's last load when it would drop by half: Partial, exit 3 ($($f.Exit): $($run.Detail))"
        Assert-That ((Get-Count 'GitHub') -eq 3 -and "$($run.Detail)" -like '*Loaded GitHub 3 limit*') 'and GitHub, read in full, is replaced as usual'
        $f2 = Invoke-Loader @{ AllowDrop = $true }
        Assert-That ($f2.Exit -eq 0 -and (Get-Count 'Graph') -eq 2 -and "$((Get-LastRun).Status)" -eq 'Succeeded') '-AllowDrop lets the real drop through once'

        # The delete guard, GitHub; and SpendLimitMaxDropPercent through -MaxDropPercent 0 (off).
        Set-Mode -GitHub 'fewer'
        $f3 = Invoke-Loader
        Assert-That ($f3.Exit -eq 3 -and (Get-Count 'GitHub') -eq 3 -and (Get-Count 'Graph') -eq 4) 'the guard keeps GitHub''s last load on its own, Graph loaded'
        $f4 = Invoke-Loader @{ MaxDropPercent = 0 }
        Assert-That ($f4.Exit -eq 0 -and (Get-Count 'GitHub') -eq 1) 'a percentage of 0 turns the guard off'

        # GitHub refuses: a note, not a failure.
        Set-Mode -GitHub 'normal'; [void] (Invoke-Loader)
        Set-Mode -GitHub '403'
        $h = Invoke-Loader
        $run = Get-LastRun
        Assert-That ($h.Exit -eq 0 -and "$($run.Status)" -eq 'Partial' -and (Get-Count 'GitHub') -eq 3 -and "$($run.Detail)" -like '*GitHub (refused*403*admin rights*last load kept*') `
            "GitHub's 403 is a note: Partial, exit 0, its last load kept ($($run.Detail))"

        # A changed shape: the whole answer (kept, a note), and one policy within it (loaded, a note).
        Set-Mode -Graph 'shape'
        $s = Invoke-Loader
        $run = Get-LastRun
        Assert-That ($s.Exit -eq 0 -and "$($run.Status)" -eq 'Partial' -and (Get-Count 'Graph') -eq 4 -and "$($run.Detail)" -like "*Graph (an answer in a shape this load cannot read*'value'*") `
            "Graph answering with no 'value' list: a note, not a crash, its last load kept ($($run.Detail))"
        Set-Mode -Graph 'itemshape'
        $s2 = Invoke-Loader
        $run = Get-LastRun
        $oddRow = Invoke-Sl "SELECT ScopeType, Amount, Raw FROM audit.SpendLimitSource WHERE Source = 'Graph' AND ExternalId = 'p-odd';" $db -Table
        Assert-That ($s2.Exit -eq 0 -and "$($run.Status)" -eq 'Partial' -and (Get-Count 'Graph') -eq 5 -and $oddRow.Rows.Count -eq 1 -and $oddRow.Rows[0].ScopeType -is [DBNull] -and
                     $oddRow.Rows[0].Amount -is [DBNull] -and "$($run.Detail)" -like '*does not know*') "a policy of a new shape loads with its JSON and a note ($($run.Detail))"

        # Graph refuses, GitHub fine; and -Source limits what is read.
        Set-Mode -Graph '403' -GitHub 'normal'
        $g4 = Invoke-Loader
        $run = Get-LastRun
        Assert-That ($g4.Exit -eq 0 -and "$($run.Status)" -eq 'Partial' -and (Get-Count 'Graph') -eq 5 -and "$($run.Detail)" -like '*CopilotCostManagement-Policy.Read.All*') 'Graph''s 403 names the permission to grant, its last load kept'
        Set-Mode
        $one = Invoke-Loader @{ Source = 'GitHub'; GraphAccessToken = $null }
        $run = Get-LastRun
        Assert-That ($one.Exit -eq 0 -and "$($run.Status)" -eq 'Succeeded' -and "$($run.Scope)" -eq 'GitHub enterprise:example-ent' -and (Get-Count 'Graph') -eq 5) '-Source GitHub reads GitHub only'
        $none = Invoke-Loader @{ GraphAccessToken = $null; Enterprise = $null }
        $run = Get-LastRun
        Assert-That ($none.Exit -eq 0 -and "$($run.Status)" -eq 'Partial' -and "$($run.Detail)" -like '*Skipped: Graph not configured*GitHub not configured*' -and (Get-Count 'Graph') -eq 5 -and (Get-Count 'GitHub') -eq 3) `
            'a source not configured is skipped and named; with neither, nothing changes and the run is Partial'
    }
    finally {
        try { [void] (Invoke-WebRequest -Uri "http://localhost:$port/stop" -TimeoutSec 5) } catch { Write-Verbose 'the stand-in had stopped' }
        $job | Wait-Job -Timeout 10 | Out-Null; $job | Remove-Job -Force
        Remove-Item -LiteralPath $modeFile -ErrorAction SilentlyContinue
        try { Invoke-Sl "IF DB_ID(N'$db') IS NOT NULL BEGIN ALTER DATABASE [$db] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [$db]; END;" 'master' } catch { Write-Host "  (could not drop $db)" -ForegroundColor Yellow }
    }
}

Write-Host ''
if ($script:Failures.Count) { Write-Host "$($script:Failures.Count) failure(s)." -ForegroundColor Red; exit 1 }
Write-Host 'All spend-limit loader tests passed.' -ForegroundColor Green
