#Requires -Version 7.0
<#
.SYNOPSIS
    Lab tool: opens every dashboard page at every period and scope and records how long each takes.

.DESCRIPTION
    The pages are read from dashboard\CopilotInsights\Pages\Nav.cs, so a page added there is measured without a change
    here. Report pages are opened at each period (-Periods; 0 is All data) for each scope (-Org: '' is the whole
    organization, else a manager's sign-in name, as ?org= on the pages). Admin pages are opened once. Where a page links
    to itself with a pick (an agent, an app, a person), the first such link is measured too, at each period.

    For first visits, run the dashboard with its query cache off (Insights__QueryCacheMinutes=0): every request then
    reads the database as a first visit does. That counts a query twice where a page asks for it twice in one
    request, which a first visit with the cache on would not, so it can only overstate a page's time.

    Writes, under -OutDir: pages.csv (one row a visit), summary.md (the slowest, and every visit over -Bar seconds),
    and, with -SqlServer and -Database, queries.csv: the statements that took longest in that database during the run
    (sys.dm_exec_query_stats, after clearing the database's plan cache at the start).

.EXAMPLE
    pwsh tools/dev/speed/Measure-PageSpeed.ps1 -BaseUrl http://localhost:5394 -OutDir C:\temp\speed `
        -Org '', 'vp.sales@contoso.com', 'team.lead@contoso.com' -SqlServer localhost -Database CopilotScaleTest
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $BaseUrl,
    [Parameter(Mandatory)] [string] $OutDir,
    [int[]] $Periods = @(7, 14, 30, 90, 180, 365, 730, 0),
    [string[]] $Org = @(''),
    [string] $Pages,
    [string] $Repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path,
    [string] $SqlServer,
    [string] $Database,
    [double] $Bar = 1.0,
    [int] $TimeoutSec = 600,
    [switch] $NoPicks
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# ---- the pages, from the menu ------------------------------------------------------------------
$nav = Get-Content -LiteralPath (Join-Path $Repo 'dashboard\CopilotInsights\Pages\Nav.cs') -Raw
$nonHuman = [regex]::Match($nav, 'const string NonHuman = "([^"]+)"').Groups[1].Value
$list = [System.Collections.Generic.List[object]]::new()
foreach ($m in [regex]::Matches($nav, 'new\("(?<t>[^"]+)",\s*(?:"(?<p>/[^"]*)"|(?<nh>NonHuman))(?<rest>[^)]*)\)')) {
    $path = if ($m.Groups['nh'].Success) { $nonHuman } else { $m.Groups['p'].Value }
    $rest = $m.Groups['rest'].Value
    if ($rest -match 'ManyTenants:\s*true') { continue }
    if ($list | Where-Object { $_.Path -eq $path }) { continue }
    $list.Add([pscustomobject]@{ Title = $m.Groups['t'].Value; Path = $path; Admin = [bool] ($rest -match 'Admin:\s*true') })
}
$list.Insert(0, [pscustomobject]@{ Title = 'Start page'; Path = '/'; Admin = $false })
if ($Pages) { $list = @($list | Where-Object { $_.Path -match $Pages }) }
Write-Host ("{0} pages, {1} periods, {2} scope(s)" -f $list.Count, $Periods.Count, $Org.Count)

# ---- the client ---------------------------------------------------------------------------------
$handler = [System.Net.Http.HttpClientHandler]::new()
$handler.UseDefaultCredentials = $true
$handler.AllowAutoRedirect = $false
$http = [System.Net.Http.HttpClient]::new($handler)
$http.Timeout = [TimeSpan]::FromSeconds($TimeoutSec)

function Open-Page([string] $url) {
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    try {
        $resp = $http.GetAsync($BaseUrl.TrimEnd('/') + $url).GetAwaiter().GetResult()
        $body = $resp.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        $sw.Stop()
        $incident = [regex]::Match($body, '(?i)reference\s+<?[^A-Za-z0-9]{0,20}([A-Z0-9]{6})\b').Groups[1].Value
        [pscustomobject]@{ Status = [int] $resp.StatusCode; Seconds = [math]::Round($sw.Elapsed.TotalSeconds, 3); Bytes = $body.Length; Body = $body; Note = $incident }
    }
    catch {
        $sw.Stop()
        [pscustomobject]@{ Status = 0; Seconds = [math]::Round($sw.Elapsed.TotalSeconds, 3); Bytes = 0; Body = ''; Note = $_.Exception.GetBaseException().Message }
    }
}

function Join-Query([string] $path, [hashtable] $q) {
    $parts = foreach ($k in $q.Keys) { if ($null -ne $q[$k] -and "$($q[$k])" -ne '') { '{0}={1}' -f $k, [uri]::EscapeDataString("$($q[$k])") } }
    if (-not $parts) { return $path }
    $sep = if ($path.Contains('?')) { '&' } else { '?' }
    $path + $sep + ($parts -join '&')
}

# ---- SQL: a clean plan cache, so the statements kept are this run's ----------------------------
$sqlCs = if ($SqlServer -and $Database) { "Server=$SqlServer;Database=$Database;Integrated Security=True;TrustServerCertificate=True;Application Name=Measure-PageSpeed" }
function Invoke-LabSql([string] $sql) {
    $c = New-Object System.Data.SqlClient.SqlConnection $sqlCs
    $c.Open()
    try { $cmd = $c.CreateCommand(); $cmd.CommandTimeout = 300; $cmd.CommandText = $sql; $t = New-Object System.Data.DataTable; $t.Load($cmd.ExecuteReader()); , $t }
    finally { $c.Dispose() }
}
if ($sqlCs) { [void] (Invoke-LabSql 'ALTER DATABASE SCOPED CONFIGURATION CLEAR PROCEDURE_CACHE; SELECT 1 AS ok;') }

# ---- warm the site itself (JIT, sign-in), not counted -------------------------------------------
[void] (Open-Page '/healthz'); [void] (Open-Page '/admin/health')

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$rows = [System.Collections.Generic.List[object]]::new()
$started = Get-Date
$i = 0
foreach ($p in $list) {
    $i++
    $visits = if ($p.Admin) { @(@{ d = $null; org = $null }) }
              else { foreach ($o in $Org) { foreach ($d in $Periods) { @{ d = $d; org = $o } } } }
    $pickUrl = $null
    foreach ($v in $visits) {
        $url = Join-Query $p.Path @{ d = $v.d; org = $v.org }
        $r = Open-Page $url
        $rows.Add([pscustomobject]@{ Page = $p.Path; Title = $p.Title; Pick = ''; Days = $v.d; Org = $v.org; Status = $r.Status; Seconds = $r.Seconds; Bytes = $r.Bytes; Note = $r.Note })
        if (-not $NoPicks -and -not $p.Admin -and -not $pickUrl -and $r.Status -eq 200 -and -not $v.org -and $p.Path -ne '/') {
            $esc = [regex]::Escape($p.Path)
            foreach ($h in [regex]::Matches($r.Body, "href=""($esc\?[^""]+)""")) {
                $u = [System.Net.WebUtility]::HtmlDecode($h.Groups[1].Value)
                $keys = @(([uri] ('http://x' + $u)).Query.TrimStart('?').Split('&') | ForEach-Object { $_.Split('=')[0] })
                # A download (?handler=csv and the like) is not a pick: downloads are measured apart.
                if ($keys -contains 'handler' -or $keys -contains 'format') { continue }
                if ($keys | Where-Object { $_ -notin 'd', 'org', 'sort', 'dir', 'page', 'q', 'csv', 'view', 'tab', 'by', 'size', 'n' }) {
                    $pickUrl = ($u -replace '([?&])(d|org)=[^&]*&?', '$1').TrimEnd('&', '?'); break
                }
            }
        }
    }
    if ($pickUrl) {
        foreach ($d in $Periods) {
            $url = Join-Query $pickUrl @{ d = $d }
            $r = Open-Page $url
            $rows.Add([pscustomobject]@{ Page = $p.Path; Title = $p.Title; Pick = $pickUrl; Days = $d; Org = ''; Status = $r.Status; Seconds = $r.Seconds; Bytes = $r.Bytes; Note = $r.Note })
        }
    }
    $mine = @($rows | Where-Object Page -eq $p.Path)
    Write-Host ("{0:HH:mm:ss} [{1}/{2}] {3}: {4} visits, slowest {5:N2} s{6}" -f (Get-Date), $i, $list.Count, $p.Path, $mine.Count,
        ($mine | Measure-Object Seconds -Maximum).Maximum, $(if ($pickUrl) { " (pick $pickUrl)" } else { '' }))
}
$rows | Export-Csv -LiteralPath (Join-Path $OutDir 'pages.csv') -NoTypeInformation -Encoding utf8

# ---- the statements that took longest ------------------------------------------------------------
if ($sqlCs) {
    $q = Invoke-LabSql @"
SELECT TOP (60) qs.execution_count AS Runs,
       CAST(qs.total_elapsed_time / 1e6 AS DECIMAL(12, 2)) AS TotalSeconds,
       CAST(qs.max_elapsed_time / 1e6 AS DECIMAL(12, 2)) AS MaxSeconds,
       CAST(qs.total_worker_time / 1e6 AS DECIMAL(12, 2)) AS CpuSeconds,
       qs.total_logical_reads / NULLIF(qs.execution_count, 0) AS ReadsPerRun,
       REPLACE(REPLACE(LEFT(SUBSTRING(t.text, qs.statement_start_offset / 2 + 1,
           (CASE qs.statement_end_offset WHEN -1 THEN DATALENGTH(t.text) ELSE qs.statement_end_offset END - qs.statement_start_offset) / 2 + 1), 600), CHAR(13), ' '), CHAR(10), ' ') AS Statement
FROM sys.dm_exec_query_stats AS qs
CROSS APPLY sys.dm_exec_sql_text(qs.sql_handle) AS t
WHERE t.dbid = DB_ID() OR t.dbid IS NULL
ORDER BY qs.total_elapsed_time DESC;
"@
    $q | Select-Object Runs, TotalSeconds, MaxSeconds, CpuSeconds, ReadsPerRun, Statement |
        Export-Csv -LiteralPath (Join-Path $OutDir 'queries.csv') -NoTypeInformation -Encoding utf8
}

# ---- summary ------------------------------------------------------------------------------------
$ok = @($rows | Where-Object Status -eq 200)
$sorted = @($ok | Sort-Object Seconds)
function Get-Pct([double] $p) { if ($sorted.Count -eq 0) { return 0 } $sorted[[math]::Min($sorted.Count - 1, [int] [math]::Floor($p * $sorted.Count))].Seconds }
$over = @($ok | Where-Object Seconds -gt $Bar | Sort-Object Seconds -Descending)
$bad = @($rows | Where-Object { $_.Status -ne 200 })
$scope = { param($o) if ($o) { $o } else { 'whole organization' } }
$period = { param($d) if ($null -eq $d -or "$d" -eq '') { '-' } elseif ([int] $d -eq 0) { 'All data' } else { "$d days" } }
$md = [System.Text.StringBuilder]::new()
[void] $md.AppendLine("# Page speed, $(Get-Date -Format 'yyyy-MM-dd HH:mm')")
[void] $md.AppendLine()
[void] $md.AppendLine("$($rows.Count) visits to $($list.Count) pages at $BaseUrl in $([math]::Round(((Get-Date) - $started).TotalMinutes)) min. Bar: $Bar s.")
[void] $md.AppendLine()
[void] $md.AppendLine("| | Seconds |")
[void] $md.AppendLine("|---|---:|")
[void] $md.AppendLine("| Median | $(Get-Pct 0.5) |")
[void] $md.AppendLine("| 90th percentile | $(Get-Pct 0.9) |")
[void] $md.AppendLine("| 99th percentile | $(Get-Pct 0.99) |")
[void] $md.AppendLine("| Slowest | $(($sorted | Select-Object -Last 1).Seconds) |")
[void] $md.AppendLine("| Visits over the bar | $($over.Count) of $($ok.Count) |")
[void] $md.AppendLine("| Visits that failed | $($bad.Count) |")
[void] $md.AppendLine()
[void] $md.AppendLine('## Each page at its slowest')
[void] $md.AppendLine()
[void] $md.AppendLine('| Page | Slowest (s) | At | Visits over the bar |')
[void] $md.AppendLine('|---|---:|---|---:|')
foreach ($g in ($ok | Group-Object Page | Sort-Object { ($_.Group | Measure-Object Seconds -Maximum).Maximum } -Descending)) {
    $top = $g.Group | Sort-Object Seconds -Descending | Select-Object -First 1
    $where = "$(& $period $top.Days), $(& $scope $top.Org)$(if ($top.Pick) { ", pick $($top.Pick)" })"
    [void] $md.AppendLine("| $($g.Name) | $($top.Seconds) | $where | $(@($g.Group | Where-Object Seconds -gt $Bar).Count) of $($g.Count) |")
}
if ($bad.Count) {
    [void] $md.AppendLine()
    [void] $md.AppendLine('## Visits that failed')
    [void] $md.AppendLine()
    foreach ($b in $bad) { [void] $md.AppendLine("- $($b.Page)$(if ($b.Pick) { " ($($b.Pick))" }) at $(& $period $b.Days), $(& $scope $b.Org): status $($b.Status) $($b.Note)") }
}
Set-Content -LiteralPath (Join-Path $OutDir 'summary.md') -Value $md.ToString() -Encoding utf8
Write-Host ("{0:HH:mm:ss} ALL DONE: {1} visits, median {2} s, {3} over {4} s, {5} failed. {6}" -f (Get-Date), $rows.Count, (Get-Pct 0.5), $over.Count, $Bar, $bad.Count, (Join-Path $OutDir 'summary.md'))
