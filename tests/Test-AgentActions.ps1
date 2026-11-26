<#
    Tests tools\Invoke-AgentActions.ps1, the tool that carries out approved actions on Copilot Studio agents.

      1  Its pure parts, dot-sourced: the call for each kind (and no other kind), the environment from its
         name, Retry-After, a check's note on the row, a message kept short and free of tokens
      2  End to end, against a stand-in for the Power Platform API and Microsoft Graph on this machine and a
         throwaway database of its own (AaTestAgentActions_<process id>, dropped at the end): the setting off,
         approved actions each called once with the right address and body, rows not approved left alone, a
         Notify row skipped, a failure that does not stop the others, throttling waited out, -WhatIf, the
         -MaxActions cap, and -Check

    Part 2 needs a SQL Server: AATEST_SQLSERVER (default localhost), Windows authentication, or AATEST_SQLUSER
    and AATEST_SQLPASSWORD for a SQL login. Without one it is skipped, saying so.

    pwsh -NoProfile -File tests/Test-AgentActions.ps1
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$tool = Join-Path $root 'tools' 'Invoke-AgentActions.ps1'

$script:Failures = [System.Collections.Generic.List[string]]::new()
function Assert-That {
    param([bool] $Condition, [string] $Message)
    if ($Condition) { Write-Host "  PASS  $Message" -ForegroundColor Green }
    else            { Write-Host "  FAIL  $Message" -ForegroundColor Red; $script:Failures.Add($Message) }
}
function Test-Throw([scriptblock] $Do) { try { & $Do | Out-Null; $false } catch { $true } }

# ---- 1. The pure parts ---------------------------------------------------------------------------
Write-Host "`n1. tools\Invoke-AgentActions.ps1, its parts" -ForegroundColor Cyan
. $tool

$api = 'https://api.powerplatform.com'
$env1 = 'aaaaaaaa-0000-0000-0000-000000000001'
$bot = '11111111-2222-3333-4444-555555555555'
$q = Get-AgentActionRequest -Kind Quarantine -Api $api -EnvironmentId $env1 -AgentId $bot -Version '2024-10-01'
Assert-That ($q.Method -eq 'Post' -and $q.Uri -eq "$api/copilotstudio/environments/$env1/bots/$bot/api/botQuarantine/SetAsQuarantined?api-version=2024-10-01" -and $null -eq $q.Body) 'Quarantine: a POST to SetAsQuarantined, no body'
$rl = Get-AgentActionRequest -Kind Release -Api $api -EnvironmentId $env1 -AgentId $bot -Version '2024-10-01'
Assert-That ($rl.Uri -like '*/api/botQuarantine/SetAsUnquarantined?api-version=2024-10-01' -and $null -eq $rl.Body) 'Release: a POST to SetAsUnquarantined'
$owner = '99999999-0000-0000-0000-000000000009'
$ra = Get-AgentActionRequest -Kind Reassign -Api $api -EnvironmentId $env1 -AgentId $bot -Version '2024-10-01' -NewOwnerId $owner
Assert-That ($ra.Uri -like '*/bots/11111111-2222-3333-4444-555555555555/api/botAdminOperations/reassign?api-version=2024-10-01' -and $ra.Body.NewOwnerAadUserId -eq $owner) 'Reassign: a POST to botAdminOperations/reassign with NewOwnerAadUserId'
Assert-That ((Test-Throw { Get-AgentActionRequest -Kind Notify -Api $api -EnvironmentId $env1 -AgentId $bot -Version 'v' }) -and
             (Test-Throw { Get-AgentActionRequest -Kind Delete -Api $api -EnvironmentId $env1 -AgentId $bot -Version 'v' })) 'any other kind, Notify or a deletion, is refused before any call'
Assert-That ((Test-Throw { Get-AgentActionRequest -Kind Quarantine -Api $api -EnvironmentId $env1 -AgentId '../../x' -Version 'v' }) -and
             (Test-Throw { Get-AgentActionRequest -Kind Reassign -Api $api -EnvironmentId $env1 -AgentId $bot -Version 'v' -NewOwnerId 'ann@example.com' })) 'an agent id or a new owner that is not a GUID is refused'

$envs = @([pscustomobject]@{ id = $env1; displayName = 'Sales'; domainName = 'sales-org' }, [pscustomobject]@{ id = 'e2'; displayName = 'HR'; domainName = 'hr' },
          [pscustomobject]@{ id = 'e3'; displayName = 'Twin'; domainName = 'a' }, [pscustomobject]@{ id = 'e4'; displayName = 'twin'; domainName = 'b' })
Assert-That ((Resolve-EnvironmentId 'sales' $envs) -eq $env1 -and (Resolve-EnvironmentId 'HR' $envs) -eq 'e2' -and (Resolve-EnvironmentId 'sales-org' $envs) -eq $env1) 'an environment by its display name or domain name, ignoring case'
Assert-That ((Resolve-EnvironmentId 'Default-aaaaaaaa-0000-0000-0000-000000000000' @()) -eq 'Default-aaaaaaaa-0000-0000-0000-000000000000' -and (Resolve-EnvironmentId $env1 @()) -eq $env1) 'an id is used as it is, with no list'
Assert-That ((Test-Throw { Resolve-EnvironmentId 'Nowhere' $envs }) -and (Test-Throw { Resolve-EnvironmentId 'Twin' $envs }) -and (Test-Throw { Resolve-EnvironmentId '' $envs })) 'none, two of the name, or no name is refused'

$now = [datetime]::new(2026, 11, 26, 12, 0, 0, [System.DateTimeKind]::Utc)
Assert-That ((Get-RetryAfterSecond '5') -eq 5 -and (Get-RetryAfterSecond '900') -eq 120 -and (Get-RetryAfterSecond '') -eq 10 -and
             (Get-RetryAfterSecond 'Thu, 26 Nov 2026 12:00:30 GMT' -Now $now) -eq 30) 'Retry-After in seconds or as a date, capped, 10 s when it says nothing'

Assert-That ((Get-CheckedResult 'Quarantined: HTTP 200.' 'expected quarantined, found not quarantined' -Now $now) -eq 'Quarantined: HTTP 200. | Check 2026-11-26 12:00Z: expected quarantined, found not quarantined') 'a mismatch goes on the row''s result'
Assert-That ((Get-CheckedResult 'Quarantined: HTTP 200. | Check 2026-11-25 01:00Z: expected quarantined, found not quarantined' '' -Now $now) -eq 'Quarantined: HTTP 200.') 'and a later check that finds it as left takes the earlier note off'
Assert-That ((Get-CheckedResult ('x' * 2500) 'm' -Now $now).Length -le 2000) 'and the result stays within its column'
$short = Get-ShortText ("HTTP 401 Authorization: Bearer eyJhbGciOiJSUzI1NiJ9.eyJzdWIiOiIxMjMifQ.abc`n more " + ('y' * 600)) 200
Assert-That ($short.Length -le 200 -and $short -notmatch 'eyJ' -and $short -notmatch "`n") 'a message is one short line, and never carries a token'
Assert-That (@(Get-TokenRole 'x.eyJyb2xlcyI6WyJDb3BpbG90U3R1ZGlvLkFkbWluQWN0aW9ucy5JbnZva2UiXX0.y') -contains 'CopilotStudio.AdminActions.Invoke' -and @(Get-TokenRole 'nonsense').Count -eq 0) 'the permissions a token carries are read from its claims'

# ---- 2. End to end -------------------------------------------------------------------------------
Write-Host "`n2. tools\Invoke-AgentActions.ps1, end to end" -ForegroundColor Cyan
$sqlServer = if ($env:AATEST_SQLSERVER) { $env:AATEST_SQLSERVER } else { 'localhost' }
$db = "AaTestAgentActions_$PID"
function New-TestConnString([string] $Catalog) {
    $b = [System.Data.SqlClient.SqlConnectionStringBuilder]::new()
    $b['Data Source'] = $sqlServer; $b['Initial Catalog'] = $Catalog; $b['Encrypt'] = $true; $b['TrustServerCertificate'] = $true; $b['Connect Timeout'] = 10
    if ($env:AATEST_SQLUSER) { $b['User ID'] = $env:AATEST_SQLUSER; $b['Password'] = $env:AATEST_SQLPASSWORD } else { $b['Integrated Security'] = $true }
    $b.ConnectionString
}
function Invoke-TestSql([string] $Sql, [string] $Catalog = $db, [switch] $Scalar, [switch] $Table) {
    $c = [System.Data.SqlClient.SqlConnection]::new((New-TestConnString $Catalog))
    try {
        $c.Open(); $cmd = $c.CreateCommand(); $cmd.CommandText = $Sql; $cmd.CommandTimeout = 120
        if ($Scalar) { return $cmd.ExecuteScalar() }
        if ($Table) { $t = [System.Data.DataTable]::new(); $r = $cmd.ExecuteReader(); try { $t.Load($r) } finally { $r.Dispose() }; return , $t }
        [void] $cmd.ExecuteNonQuery()
    }
    finally { $c.Dispose() }
}

$sqlUp = try { [void] (Invoke-TestSql 'SELECT 1' -Catalog master -Scalar); $true } catch { Write-Host "  SKIP  no SQL Server at $sqlServer ($($_.Exception.Message)): part 2 not run." -ForegroundColor Yellow; $false }
if ($sqlUp) {
    # The stand-in: the documented addresses only; it records each request it is sent.
    $probe = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0); $probe.Start(); $port = $probe.LocalEndpoint.Port; $probe.Stop()
    $state = [hashtable]::Synchronized(@{
        Log = [System.Collections.Concurrent.ConcurrentQueue[object]]::new()
        Quarantined = [hashtable]::Synchronized(@{}); Fail = [hashtable]::Synchronized(@{}); Throttle = [hashtable]::Synchronized(@{})
    })
    $mockScript = {
        param([int] $Port, $State)
        $listener = [System.Net.HttpListener]::new(); $listener.Prefixes.Add("http://localhost:$Port/"); $listener.Start()
        function Send($ctx, [int] $status, [string] $body = '', [hashtable] $headers = @{}) {
            $ctx.Response.StatusCode = $status
            foreach ($k in $headers.Keys) { $ctx.Response.Headers[$k] = "$($headers[$k])" }
            $bytes = [System.Text.Encoding]::UTF8.GetBytes($body)
            $ctx.Response.ContentType = 'application/json'; $ctx.Response.ContentLength64 = $bytes.Length
            if ($bytes.Length) { $ctx.Response.OutputStream.Write($bytes, 0, $bytes.Length) }
            $ctx.Response.Close()
        }
        while ($listener.IsListening) {
            $ctx = $listener.GetContext(); $rq = $ctx.Request; $path = $rq.Url.AbsolutePath; $auth = "$($rq.Headers['Authorization'])"
            $body = ''; if ($rq.HasEntityBody) { $sr = [System.IO.StreamReader]::new($rq.InputStream); $body = $sr.ReadToEnd(); $sr.Dispose() }
            try {
                if ($path -eq '/stop') { Send $ctx 200 '{}'; break }
                if ($path -eq '/ping') { Send $ctx 200 '{}'; continue }
                $State.Log.Enqueue([pscustomobject]@{ Method = $rq.HttpMethod; Path = $path; Query = $rq.Url.Query; Body = $body; Auth = $auth })
                if ($path -like '/graph/*') {
                    if ($auth -ne 'Bearer graph-test-token') { Send $ctx 401 '{"error":{"code":"InvalidAuthenticationToken","message":"no"}}'; continue }
                    if ($path -eq '/graph/v1.0/users/ann%40example.com' -or $path -eq '/graph/v1.0/users/ann@example.com') { Send $ctx 200 '{"id":"99999999-0000-0000-0000-000000000009","accountEnabled":true}'; continue }
                    Send $ctx 404 '{"error":{"code":"Request_ResourceNotFound","message":"no such user"}}'; continue
                }
                if ($auth -ne 'Bearer pp-test-token') { Send $ctx 401 '{"error":{"code":"Unauthorized","message":"bad token"}}'; continue }
                if ($rq.QueryString['api-version'] -ne '2024-10-01') { Send $ctx 400 '{"error":{"code":"BadVersion","message":"api-version"}}'; continue }
                if ($path -eq '/pp/environmentmanagement/environments') {
                    if ($rq.QueryString['page'] -eq '2') { Send $ctx 200 '{"value":[{"id":"aaaaaaaa-0000-0000-0000-000000000002","displayName":"HR"}]}' }
                    else { Send $ctx 200 ('{"value":[{"id":"aaaaaaaa-0000-0000-0000-000000000001","displayName":"Sales"}],"@odata.nextLink":"http://localhost:' + $Port + '/pp/environmentmanagement/environments?api-version=2024-10-01&page=2"}') }
                    continue
                }
                if ($path -match '^/pp/copilotstudio/environments/([^/]+)/bots/([^/]+)/api/(.+)$') {
                    $b = $Matches[2]; $op = $Matches[3]
                    if ($State.Throttle.ContainsKey($b) -and $State.Throttle[$b] -gt 0) { $State.Throttle[$b]--; Send $ctx 429 '{"error":{"code":"TooManyRequests","message":"slow down"}}' @{ 'Retry-After' = '1' }; continue }
                    if ($State.Fail.ContainsKey($b)) { Send $ctx 500 '{"error":{"code":"InternalServerError","message":"something broke"}}'; continue }
                    $stamp = [datetime]::UtcNow.ToString('o')
                    switch ($op) {
                        'botQuarantine' { if ($rq.HttpMethod -ne 'GET') { Send $ctx 405 '{}'; break }; Send $ctx 200 ("{`"isBotQuarantined`":$("$([bool] $State.Quarantined[$b])".ToLowerInvariant()),`"lastUpdateTimeUtc`":`"$stamp`"}") }
                        'botQuarantine/SetAsQuarantined' { if ($rq.HttpMethod -ne 'POST') { Send $ctx 405 '{}'; break }; $State.Quarantined[$b] = $true; Send $ctx 200 "{`"isBotQuarantined`":true,`"lastUpdateTimeUtc`":`"$stamp`"}" }
                        'botQuarantine/SetAsUnquarantined' { if ($rq.HttpMethod -ne 'POST') { Send $ctx 405 '{}'; break }; $State.Quarantined[$b] = $false; Send $ctx 200 "{`"isBotQuarantined`":false,`"lastUpdateTimeUtc`":`"$stamp`"}" }
                        'botAdminOperations/reassign' {
                            $j = try { $body | ConvertFrom-Json } catch { $null }
                            if ($rq.HttpMethod -ne 'POST' -or -not $j -or -not $j.PSObject.Properties['NewOwnerAadUserId']) { Send $ctx 400 '{"error":{"code":"BadRequest","message":"NewOwnerAadUserId"}}'; break }
                            Send $ctx 204 ''
                        }
                        default { Send $ctx 404 '{}' }
                    }
                    continue
                }
                Send $ctx 404 '{}'
            }
            catch { try { Send $ctx 500 (@{ error = @{ code = 'Mock'; message = "$_" } } | ConvertTo-Json) } catch { Write-Verbose "$_" } }
        }
        $listener.Stop()
    }
    $mock = Start-ThreadJob -ScriptBlock $mockScript -ArgumentList $port, $state
    $base = "http://localhost:$port"
    try {
        $mockUp = $false
        foreach ($try in 1..50) { try { [void] (Invoke-WebRequest -Uri "$base/ping" -SkipHttpErrorCheck -TimeoutSec 2); $mockUp = $true; break } catch { Start-Sleep -Milliseconds 200 } }
        Assert-That $mockUp 'the stand-in for the Power Platform API and Graph is listening'

        # The throwaway database: the three tables, as the install scripts make them.
        Invoke-TestSql -Catalog master "IF DB_ID(N'$db') IS NOT NULL BEGIN ALTER DATABASE [$db] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [$db]; END; CREATE DATABASE [$db];"
        function Get-Ddl([string] $File, [string] $Table) {
            $text = Get-Content -LiteralPath (Join-Path $root 'sql' $File) -Raw
            $m = [regex]::Match($text, "(?s)IF OBJECT_ID\(N'$([regex]::Escape($Table))', N'U'\) IS NULL\s+CREATE TABLE $([regex]::Escape($Table))\s*\(.*?\r?\n    \);")
            if (-not $m.Success) { throw "No CREATE TABLE $Table in $File" }
            $m.Value
        }
        Invoke-TestSql "EXEC (N'CREATE SCHEMA dash AUTHORIZATION dbo;'); EXEC (N'CREATE SCHEMA audit AUTHORIZATION dbo;');"
        Invoke-TestSql (Get-Ddl 'Create-CopilotDashboardSchema.sql' 'dash.Setting')
        Invoke-TestSql (Get-Ddl 'Create-CopilotOperationsSchema.sql' 'dash.AgentAction')
        Invoke-TestSql (Get-Ddl 'Create-CopilotExternalUsageSchema.sql' 'audit.ExternalUsageRun')
        Invoke-TestSql "INSERT INTO dash.Setting (Name, Value) VALUES ('AgentActionsEnabled', N'0');"

        $cs = New-TestConnString $db
        $tok = [securestring]::new(); foreach ($ch in 'pp-test-token'.ToCharArray()) { $tok.AppendChar($ch) }
        $gtok = [securestring]::new(); foreach ($ch in 'graph-test-token'.ToCharArray()) { $gtok.AppendChar($ch) }
        $noSettings = Join-Path ([System.IO.Path]::GetTempPath()) "no-settings-aa-$PID.psd1"
        function Invoke-Tool([hashtable] $Extra = @{}) {
            $global:LASTEXITCODE = 0
            $a = @{ ConnectionString = $cs; AccessToken = $tok; GraphAccessToken = $gtok; ApiEndpoint = "$base/pp"; GraphEndpoint = "$base/graph"; SettingsPath = $noSettings } + $Extra
            $out = try { & $tool @a 6>&1 3>&1 2>&1 | Out-String } catch { "THREW: $($_.Exception.Message)"; $global:LASTEXITCODE = 99 }
            [pscustomobject]@{ Out = "$out"; Code = $global:LASTEXITCODE }
        }
        function Get-Request { $all = @($state.Log.ToArray()); $x = $null; while ($state.Log.TryDequeue([ref] $x)) { }; , $all }
        function Get-Action([int] $Id) { (Invoke-TestSql "SELECT ActionId, Status, DoneUtc, Result, CheckedUtc FROM dash.AgentAction WHERE ActionId = $Id;" -Table).Rows[0] }
        function Get-RunCount { [int] (Invoke-TestSql "SELECT COUNT(*) FROM audit.ExternalUsageRun WHERE Source = 'AgentActions';" -Scalar) }
        $b = @{}; foreach ($i in 1..13) { $b[$i] = '{0:D8}-0000-0000-0000-00000000000{1:X1}' -f $i, ($i % 16) }
        $e1 = 'aaaaaaaa-0000-0000-0000-000000000001'; $e2 = 'aaaaaaaa-0000-0000-0000-000000000002'
        function Add-Action([string] $Bot, [string] $Env, [string] $Kind, [string] $Status, [string] $Owner = $null, [string] $Extra = '') {
            $o = if ($Owner) { "N'$Owner'" } else { 'NULL' }
            [int] (Invoke-TestSql "INSERT INTO dash.AgentAction (InventoryAgentId, AgentName, Environment, ActionKind, Status, NewOwnerUpn, DecidedUtc$(if ($Extra) { ', DoneUtc, Result' }))
                                   VALUES (N'$Bot', N'Agent $Bot', N'$Env', '$Kind', '$Status', $o, '2026-11-25T10:00:00'$(if ($Extra) { ", $Extra" }));
                                   SELECT CAST(SCOPE_IDENTITY() AS INT);" -Scalar)
        }

        # -- the setting off: nothing called, nothing changed
        $a1 = Add-Action $b[1] 'Sales' 'Quarantine' 'Approved'
        $r = Invoke-Tool
        Assert-That ($r.Code -eq 0 -and (Get-Request).Count -eq 0 -and "$((Get-Action $a1).Status)" -eq 'Approved' -and (Get-RunCount) -eq 0 -and $r.Out -like '*AgentActionsEnabled*') `
            "with AgentActionsEnabled not 1: a note, exit 0, no call, the row still Approved, no run row ($($r.Code))"

        # -- the run
        Invoke-TestSql "UPDATE dash.Setting SET Value = N'1' WHERE Name = 'AgentActionsEnabled';"
        $state.Quarantined[$b[2]] = $true
        $state.Fail[$b[7]] = $true
        $state.Throttle[$b[8]] = 1
        $a2 = Add-Action $b[2] 'hr' 'Release' 'Approved'
        $a3 = Add-Action $b[3] $e1 'Reassign' 'Approved' 'ann@example.com'
        $a4 = Add-Action $b[4] 'Sales' 'Quarantine' 'Proposed'
        $a5 = Add-Action $b[5] 'Sales' 'Quarantine' 'Rejected'
        $a6 = Add-Action $b[6] 'Sales' 'Notify' 'Approved'
        $a7 = Add-Action $b[7] 'Sales' 'Quarantine' 'Approved'
        $a8 = Add-Action $b[8] 'Sales' 'Quarantine' 'Approved'
        $a9 = Add-Action $b[9] 'Sales' 'Quarantine' 'Done' $null "DATEADD(DAY, -1, SYSUTCDATETIME()), N'Quarantined: HTTP 200.'"
        $old = Add-Action $b[12] 'Sales' 'Quarantine' 'Done' $null "DATEADD(DAY, -30, SYSUTCDATETIME()), N'Quarantined: HTTP 200.'"
        $r = Invoke-Tool
        $req = Get-Request
        $posts = @($req | Where-Object Method -eq 'POST')
        function Get-Call([string] $Bot) { , @($posts | Where-Object { $_.Path -like "*/bots/$Bot/*" }) }
        Assert-That ($r.Code -eq 2) "one failure among them: the run ends Partial, exit 2 ($($r.Code)) $(if ($r.Code -ne 2) { $r.Out })"
        $c1 = Get-Call $b[1]
        Assert-That ($c1.Count -eq 1 -and $c1[0].Path -eq "/pp/copilotstudio/environments/$e1/bots/$($b[1])/api/botQuarantine/SetAsQuarantined" -and $c1[0].Query -like '*api-version=2024-10-01*') `
            'Quarantine: called once, at SetAsQuarantined in the environment found by its name'
        $c2 = Get-Call $b[2]
        Assert-That ($c2.Count -eq 1 -and $c2[0].Path -eq "/pp/copilotstudio/environments/$e2/bots/$($b[2])/api/botQuarantine/SetAsUnquarantined" -and -not $state.Quarantined[$b[2]]) `
            'Release: called once, at SetAsUnquarantined, in an environment on the list''s second page'
        $c3 = Get-Call $b[3]
        Assert-That ($c3.Count -eq 1 -and $c3[0].Path -eq "/pp/copilotstudio/environments/$e1/bots/$($b[3])/api/botAdminOperations/reassign" -and ($c3[0].Body | ConvertFrom-Json).NewOwnerAadUserId -eq '99999999-0000-0000-0000-000000000009') `
            'Reassign: called once, with the new owner''s object id from Graph as NewOwnerAadUserId'
        Assert-That (@($req | Where-Object { $_.Path -like '/graph/*' }).Count -eq 1 -and @($req | Where-Object { $_.Path -eq '/pp/environmentmanagement/environments' }).Count -eq 2) 'Graph asked once; the environments listed once a run (both pages)'
        Assert-That ((Get-Call $b[4]).Count -eq 0 -and (Get-Call $b[5]).Count -eq 0 -and "$((Get-Action $a4).Status)" -eq 'Proposed' -and "$((Get-Action $a5).Status)" -eq 'Rejected' -and
                     (Get-Action $a4).DoneUtc -is [DBNull]) 'Proposed and Rejected rows: not called, untouched'
        Assert-That ((Get-Call $b[6]).Count -eq 0 -and "$((Get-Action $a6).Status)" -eq 'Approved' -and (Get-Action $a6).DoneUtc -is [DBNull]) 'an approved Notify row is the dashboard''s: skipped, left Approved'
        Assert-That (@($req | Where-Object { $_.Path -like "*/bots/$($b[9])/*" }).Count -eq 0 -and "$((Get-Action $a9).Result)" -eq 'Quarantined: HTTP 200.') 'a row already Done is not called again'
        $f7 = Get-Action $a7
        Assert-That ((Get-Call $b[7]).Count -eq 1 -and "$($f7.Status)" -eq 'Failed' -and $f7.DoneUtc -isnot [DBNull] -and "$($f7.Result)" -like '*500*something broke*') `
            "a 500 marks that row Failed, with what the API said ($($f7.Result))"
        Assert-That ((Get-Call $b[8]).Count -eq 2 -and "$((Get-Action $a8).Status)" -eq 'Done' -and $r.Out -like '*429*') 'a 429 is waited out (Retry-After) and tried again: then Done'
        $done = @($a1, $a2, $a3, $a8 | ForEach-Object { Get-Action $_ })
        Assert-That (@($done | Where-Object { "$($_.Status)" -eq 'Done' -and $_.DoneUtc -isnot [DBNull] }).Count -eq 4 -and "$($done[0].Result)" -like 'Quarantined: HTTP 200*isBotQuarantined=True*' -and
                     "$($done[2].Result)" -like 'Reassigned to ann@example.com (99999999-*): HTTP 204*') 'the others still ran: Done, DoneUtc, and what the call answered'
        Assert-That (@($req | Where-Object { $_.Path -like '/pp/*' -and $_.Auth -ne 'Bearer pp-test-token' }).Count -eq 0 -and
                     [int] (Invoke-TestSql "SELECT COUNT(*) FROM dash.AgentAction WHERE Result LIKE '%test-token%';" -Scalar) -eq 0) 'every call carries the token, and no row does'
        $run = (Invoke-TestSql "SELECT TOP (1) Source, Status, RowsWritten, Detail, EndedUtc FROM audit.ExternalUsageRun ORDER BY RunId DESC;" -Table).Rows[0]
        Assert-That ("$($run.Source)" -eq 'AgentActions' -and "$($run.Status)" -eq 'Partial' -and [int] $run.RowsWritten -eq 4 -and $run.EndedUtc -isnot [DBNull] -and "$($run.Detail)" -like '*Notify*') `
            "one run row, Source AgentActions, Partial, 4 done ($($run.Status), $($run.Detail))"

        # -- -WhatIf: no call, no change
        $a10 = Add-Action $b[10] 'Sales' 'Quarantine' 'Approved'
        $runs = Get-RunCount
        $r = Invoke-Tool @{ WhatIf = $true }
        Assert-That ($r.Code -eq 0 -and (Get-Request).Count -eq 0 -and "$((Get-Action $a10).Status)" -eq 'Approved' -and (Get-RunCount) -eq $runs -and $r.Out -like '*What if*') `
            '-WhatIf: says what it would do; no call, the row still Approved, no run row'

        # -- -MaxActions: the cap, oldest first
        $a11 = Add-Action $b[11] 'Sales' 'Quarantine' 'Approved'
        $a13 = Add-Action $b[13] 'Sales' 'Quarantine' 'Approved'
        $r = Invoke-Tool @{ MaxActions = 2 }
        $posts = @((Get-Request) | Where-Object Method -eq 'POST')
        Assert-That ($r.Code -eq 0 -and $posts.Count -eq 2 -and "$((Get-Action $a10).Status)" -eq 'Done' -and "$((Get-Action $a11).Status)" -eq 'Done' -and "$((Get-Action $a13).Status)" -eq 'Approved' -and
                     "$((Get-Action $a7).Status)" -eq 'Failed' -and $r.Out -like '*1 more wait*') "-MaxActions 2: two calls, the oldest two; the rest wait for the next run ($($posts.Count))"

        # -- -Check: CheckedUtc set, a mismatch flagged
        $state.Quarantined[$b[9]] = $false   # quarantined a day ago, released by hand since
        $r = Invoke-Tool @{ Check = $true }
        $req = Get-Request
        $checked = @(Invoke-TestSql "SELECT ActionId FROM dash.AgentAction WHERE CheckedUtc IS NOT NULL ORDER BY ActionId;" -Table | ForEach-Object { $_.Rows } | ForEach-Object { [int] $_.ActionId })
        Assert-That (@($req | Where-Object Method -ne 'GET').Count -eq 0 -and @($req | Where-Object { $_.Path -like '*/api/botQuarantine' }).Count -eq 6) "-Check only reads: the quarantine status of each agent quarantined or released in the last 7 days ($(@($req).Count) requests)"
        Assert-That (($checked -join ',') -eq (@($a1, $a2, $a8, $a9, $a10, $a11) -join ',')) "CheckedUtc set on those, not on a failed, a reassigned or a 30-day-old action ($($checked -join ','))"
        $m9 = Get-Action $a9
        Assert-That ($r.Code -eq 2 -and "$($m9.Result)" -like 'Quarantined: HTTP 200. | Check *: expected quarantined, found not quarantined' -and $r.Out -like "*$($b[9])*expected quarantined, found not quarantined*" -and
                     "$((Get-Action $a9).Status)" -eq 'Done') "a mismatch is written to the row and reported, exit 2; nothing changed in Copilot Studio ($($r.Code))"
        Assert-That ("$((Get-Action $a1).Result)" -notlike '*| Check*' -and (Get-Action $old).CheckedUtc -is [DBNull]) 'an agent as it was left keeps its result as it was'
        $r = Invoke-Tool @{ Check = $true; WhatIf = $true }
        Assert-That ($r.Code -eq 0 -and (Get-Request).Count -eq 0) '-Check -WhatIf: no call'

        # -- -CheckOnly: reads once, writes nothing
        $runs = Get-RunCount
        $r = Invoke-Tool @{ CheckOnly = $true }
        $req = Get-Request
        Assert-That ($r.Code -eq 0 -and @($req | Where-Object Method -ne 'GET').Count -eq 0 -and @($req | Where-Object { $_.Path -like "*/bots/$($b[13])/api/botQuarantine" }).Count -eq 1 -and
                     "$((Get-Action $a13).Status)" -eq 'Approved' -and (Get-RunCount) -eq $runs -and $r.Out -like '*Environments: 2 visible*') "-CheckOnly: signs in, reads the environments and a waiting agent's status, changes nothing ($($r.Code))"
    }
    finally {
        try { [void] (Invoke-WebRequest -Uri "$base/stop" -SkipHttpErrorCheck -TimeoutSec 5) } catch { Write-Verbose "$_" }
        Stop-Job $mock -ErrorAction SilentlyContinue; Remove-Job $mock -Force -ErrorAction SilentlyContinue
        try { Invoke-TestSql -Catalog master "IF DB_ID(N'$db') IS NOT NULL BEGIN ALTER DATABASE [$db] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [$db]; END;" } catch { Write-Host "  (could not drop $db)" }
    }
}

Write-Host ''
if ($script:Failures.Count) { Write-Host "$($script:Failures.Count) assertion(s) failed." -ForegroundColor Red; exit 1 }
Write-Host 'All agent action tests passed.' -ForegroundColor Green
