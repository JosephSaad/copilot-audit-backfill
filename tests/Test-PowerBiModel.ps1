<#
.SYNOPSIS
    Checks the Power BI project in powerbi/ (2.4.12): that it is what powerbi/generate_model.py
    writes, that its row-level security reaches every table a role member can read, that its
    refresh policy and period-end measures are there, and that the generator never overwrites
    work saved in a project.

.DESCRIPTION
    Nothing tested the model before 2.4.12: the generator dropped a table for five releases, the
    Organization role filtered the people alone (so every member could list every site, and read
    OrgSecurity whole), and the generator deleted report pages saved in the project. This reads
    the TMDL and runs the generator in a temporary folder; it needs Python 3, not Power BI.
    bi views against the model's columns are checked in Test-SqlIntegration.ps1, which has a
    database.

.EXAMPLE
    pwsh -NoProfile -File tests/Test-PowerBiModel.ps1
#>
#Requires -Version 7.2
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Split-Path $PSScriptRoot -Parent
$script:Failures = [System.Collections.Generic.List[string]]::new()
function Assert-That {
    param([bool] $Condition, [string] $Message)
    if ($Condition) { Write-Host "  PASS  $Message" -ForegroundColor Green }
    else            { Write-Host "  FAIL  $Message" -ForegroundColor Red; $script:Failures.Add($Message) }
}

$project = Join-Path $root 'powerbi' 'CopilotInsights'
$def = Join-Path $project 'CopilotInsights.SemanticModel' 'definition'
function Read-Text([string] $Path) { [System.IO.File]::ReadAllText($Path).Replace("`r`n", "`n") }

# Every file of a project, by its path under it, with its text (line endings aside). Desktop's .pbi folders are not the project.
function Get-ProjectFile([string] $Folder) {
    $out = @{}
    foreach ($f in Get-ChildItem -LiteralPath $Folder -Recurse -File -Force) {
        $rel = [System.IO.Path]::GetRelativePath($Folder, $f.FullName).Replace('\', '/')
        if ($rel -match '(^|/)\.pbi/') { continue }
        $out[$rel] = Read-Text $f.FullName
    }
    $out
}

$py = @('python', 'python3') | Where-Object { Get-Command $_ -ErrorAction SilentlyContinue } | Select-Object -First 1
$tmp = Join-Path ([System.IO.Path]::GetTempPath()) "copilot-pbi-$PID"
New-Item -ItemType Directory -Path $tmp -Force | Out-Null
try {
# ---------------------------------------------------------------------------
Write-Host "`n1. The project is what the generator writes" -ForegroundColor Cyan
Assert-That ([bool] $py) "Python 3 is on the path ($py)"
Copy-Item -LiteralPath (Join-Path $root 'powerbi') -Destination $tmp -Recurse
$gen = Join-Path $tmp 'powerbi' 'generate_model.py'
$fresh = Join-Path $tmp 'fresh'
$said = & $py $gen --out $fresh 2>&1 | Out-String
Assert-That ($LASTEXITCODE -eq 0) "it writes a new project into an empty folder ($($said.Trim()))"
$committed = Get-ProjectFile $project
$written = Get-ProjectFile $fresh
$differ = @($written.Keys | Where-Object { -not $committed.ContainsKey($_) -or $committed[$_] -ne $written[$_] }) +
          @($committed.Keys | Where-Object { -not $written.ContainsKey($_) })
Assert-That ($differ.Count -eq 0) "the committed project is exactly its output, $($written.Count) files: run python powerbi/generate_model.py after changing it ($($differ -join ', '))"
Assert-That ($written.ContainsKey('CopilotInsights.SemanticModel/definition/tables/FileTypes.tmdl')) 'FileTypes, added by hand in 2.4.6, is part of it'

# ---------------------------------------------------------------------------
Write-Host "`n2. Row-level security reaches every table a role member can read" -ForegroundColor Cyan
$tables = @(Get-ChildItem -LiteralPath (Join-Path $def 'tables') -Filter *.tmdl | ForEach-Object { $_.BaseName })
$rels = foreach ($block in ((Read-Text (Join-Path $def 'relationships.tmdl')) -split "(?m)^relationship ") | Where-Object { $_.Trim() }) {
    [pscustomobject]@{
        From     = [regex]::Match($block, 'fromColumn: (\w+)\.').Groups[1].Value
        To       = [regex]::Match($block, 'toColumn: (\w+)\.').Groups[1].Value
        Security = $block -match 'securityFilteringBehavior: bothDirections'
        Cross    = $block -match 'crossFilteringBehavior: bothDirections'
    }
}
function Get-RolePermission([string] $Role) {
    $text = Read-Text (Join-Path $def 'roles' "$Role.tmdl")
    $p = @{}
    foreach ($m in [regex]::Matches($text, '(?m)^\ttablePermission (\w+) = (.+)$')) { $p[$m.Groups[1].Value] = $m.Groups[2].Value.Trim() }
    $p
}
$org = Get-RolePermission 'Organization'
# Where a role's filters reach: a filtered table filters the facts on its many side, and a relationship that applies
# the security filter both ways carries it back from the fact to the dimension too.
$reached = [System.Collections.Generic.HashSet[string]]::new([string[]] @($org.Keys))
do {
    $grew = $false
    foreach ($r in $rels) {
        if ($reached.Contains($r.To) -and $reached.Add($r.From)) { $grew = $true }
        if ($r.Security -and $reached.Contains($r.From) -and $reached.Add($r.To)) { $grew = $true }
    }
} while ($grew)
# Lists of things, not of people or of what they did: open to every member, as the dashboard shows them to everyone.
$reference = 'Calendar', 'Apps', 'Models', 'Skus', 'LicenseTypes', 'LicenseStatuses', 'SensitivityLabels',
             'ResourceTypes', 'FileTypes', 'ResourceActions', 'UseCases', 'EventMarkers'
$open = @($tables | Where-Object { $_ -notin $reference -and -not $reached.Contains($_) })
Assert-That ($open.Count -eq 0) "every table but the reference lists follows the Organization role ($($reached.Count) of $($tables.Count); open: $($open -join ', '))"
foreach ($t in 'Sites', 'Agents', 'OrgSecurity', 'LicensePool', 'ValueNames', 'DepartmentWeek', 'Interactions', 'AccessedResources', 'JobFamilies') {
    Assert-That ($reached.Contains($t)) "the role reaches $t"
}
Assert-That ($org['OrgSecurity'] -eq 'OrgSecurity[ViewerUpn] = USERPRINCIPALNAME ()') 'OrgSecurity shows each viewer only their own rows'
Assert-That ($org['Users'] -like '*OrgSecurity`[ViewerUpn`] = USERPRINCIPALNAME ()*') 'and Users only the people in those rows'
foreach ($t in 'LicensePool', 'ValueNames', 'DepartmentWeek') {
    Assert-That ($org[$t] -like '*OrgSecurity`[ViewerUpn`] = USERPRINCIPALNAME ()*' -and $org[$t] -like '*OrgSecurity`[WholeOrganization`] = TRUE ()*') "$t, figures for the whole organization, shows only to viewers who see it whole"
}
$secured = @($rels | Where-Object Security)
Assert-That ((@($secured | Where-Object { -not $_.Cross }).Count -eq 0) -and $secured.Count -eq 2) "the two relationships that carry security back (to $(($secured.To) -join ' and ')) filter both ways, as Power BI requires"
foreach ($t in @($secured.From) + @($secured.To)) {
    Assert-That (@($secured | Where-Object { $_.From -eq $t -or $_.To -eq $t }).Count -eq 1) "$t takes part in one such relationship only, as Power BI allows"
}
$dep = Get-RolePermission 'Departments'
$depReached = [System.Collections.Generic.HashSet[string]]::new([string[]] @($dep.Keys))
do {
    $grew = $false
    foreach ($r in $rels) {
        if ($depReached.Contains($r.To) -and $depReached.Add($r.From)) { $grew = $true }
        if ($r.Security -and $depReached.Contains($r.From) -and $depReached.Add($r.To)) { $grew = $true }
    }
} while ($grew)
$blocked = @($dep.Keys | Where-Object { $dep[$_] -ne 'FALSE ()' })
$depOpen = @($tables | Where-Object { $_ -notin $reference -and $_ -ne 'DepartmentWeek' -and -not $depReached.Contains($_) })
Assert-That ($blocked.Count -eq 0 -and $depOpen.Count -eq 0 -and -not $dep.ContainsKey('DepartmentWeek')) "the Departments role shows DepartmentWeek and nothing about anyone (open: $($depOpen -join ', '))"
$model = Read-Text (Join-Path $def 'model.tmdl')
Assert-That ($model -match '(?m)^ref role Organization$' -and $model -match '(?m)^ref role Departments$') 'the model lists both roles'

# ---------------------------------------------------------------------------
Write-Host "`n3. Incremental refresh is part of the model" -ForegroundColor Cyan
foreach ($t in 'Interactions', 'AccessedResources') {
    $text = Read-Text (Join-Path $def 'tables' "$t.tmdl")
    $policy = [regex]::Match($text, "(?ms)^\trefreshPolicy\n(.*?)\n\n")
    $source = [regex]::Match($text, "(?ms)^\t\tsource =\n(.*?)\n\n").Groups[1].Value
    Assert-That ($policy.Success -and $policy.Value -match 'rollingWindowGranularity: year' -and $policy.Value -match 'rollingWindowPeriods: 2' -and
                 $policy.Value -match 'incrementalGranularity: day' -and $policy.Value -match 'incrementalPeriods: 4') "$t has its refresh policy: 2 years kept, the last 4 days refreshed"
    Assert-That ($source -and $policy.Value.Contains($source) -and $source -match 'RangeStart' -and $source -match 'RangeEnd') "and the policy's source is the partition's, filtered on RangeStart and RangeEnd"
}
$expr = Read-Text (Join-Path $def 'expressions.tmdl')
$end = [regex]::Match($expr, 'expression RangeEnd = #datetime\((\d{4}),')
Assert-That ($end.Success -and [int] $end.Groups[1].Value -ge 2100) "RangeEnd cuts nothing off: $($end.Groups[1].Value) (it was 1 January 2027, three months after 2.4.11)"

# ---------------------------------------------------------------------------
Write-Host "`n4. Licenses are counted at one period end for every row" -ForegroundColor Cyan
$all = ($tables | ForEach-Object { Read-Text (Join-Path $def 'tables' "$_.tmdl") }) -join "`n"
Assert-That ($all -notmatch 'LASTNONBLANK') 'no measure takes the end of the period from the rows'' own last licensed day'
foreach ($m in "'Licensed users'", "'Active licensed users'", "'Licensed users not using Copilot'") {
    $body = [regex]::Match($all, "(?ms)^\tmeasure $([regex]::Escape($m)) =(.*?)\n\t\tformatString").Groups[1].Value
    Assert-That ($body -match '\[Period end\]' -and $body -match 'Calendar\[Date\] = EndDate') "$m counts licenses on [Period end]"
}
Assert-That ($all -match "(?m)^\tmeasure 'Period end' = .*Calendar\[IsLoaded\]") '[Period end] is the last day of the period the data covers'
Assert-That ($all -match '(?m)^\tcolumn IsLoaded = Calendar\[DateKey\] <= MAX \( LicensedDays\[DateKey\] \)') 'and Calendar[IsLoaded] is a calculated column, worked out at refresh, outside row-level security'

# ---------------------------------------------------------------------------
Write-Host "`n5. The generator writes only a project it wrote itself" -ForegroundColor Cyan
$copy = Join-Path $tmp 'powerbi' 'CopilotInsights'
$before = Get-ProjectFile $copy
$said = & $py $gen 2>&1 | Out-String
Assert-That ($LASTEXITCODE -eq 0) 'over its own unchanged output it runs'
$after = Get-ProjectFile $copy
Assert-That (@($before.Keys | Where-Object { $after[$_] -ne $before[$_] }).Count -eq 0 -and $after.Count -eq $before.Count) 'and changes nothing'

# What step 15 saves in the project: a report page, the parameters, a measure.
$pages = Join-Path $copy 'CopilotInsights.Report' 'definition' 'pages'
New-Item -ItemType Directory -Path (Join-Path $pages 'usage') -Force | Out-Null
Set-Content -LiteralPath (Join-Path $pages 'usage' 'page.json') -Value '{ "name": "usage", "displayName": "Usage" }'
$pj = Join-Path $pages 'pages.json'
Set-Content -LiteralPath $pj -Value ((Get-Content -LiteralPath $pj -Raw) -replace '"overview"(\s*)\]', '"overview", "usage"$1]') -NoNewline
$ex = Join-Path $copy 'CopilotInsights.SemanticModel' 'definition' 'expressions.tmdl'
Set-Content -LiteralPath $ex -Value ((Get-Content -LiteralPath $ex -Raw).Replace('"localhost"', '"SQLPROD01"')) -NoNewline
$sessions = Join-Path $copy 'CopilotInsights.SemanticModel' 'definition' 'tables' 'Sessions.tmdl'
Add-Content -LiteralPath $sessions -Value "`tmeasure 'My measure' = 1"
$said = & $py $gen 2>&1 | Out-String
Assert-That ($LASTEXITCODE -eq 1) 'over a project with a page and model changes saved in it, it stops'
Assert-That ($said -like '*pages/usage/page.json*' -and $said -like '*expressions.tmdl*' -and $said -like '*Sessions.tmdl*' -and $said -like '*--out*') 'naming what it would lose, and how to bring the new model across'
Assert-That ((Test-Path (Join-Path $pages 'usage' 'page.json')) -and (Read-Text $ex).Contains('"SQLPROD01"') -and (Read-Text $sessions).Contains('My measure') -and (Read-Text $pj).Contains('"usage"')) 'and changes nothing: the page, the parameters and the measure are all still there'
$beside = Join-Path $tmp 'beside'
$null = & $py $gen --out $beside 2>&1
Assert-That ($LASTEXITCODE -eq 0 -and (Test-Path (Join-Path $beside 'CopilotInsights.pbip'))) 'the new model can be written beside it with --out'
$null = & $py $gen --out $copy --force 2>&1
Assert-That ($LASTEXITCODE -eq 0 -and -not (Test-Path (Join-Path $pages 'usage')) -and -not (Read-Text $ex).Contains('SQLPROD01')) 'and --force replaces the project, as it says'
Remove-Item -LiteralPath (Join-Path $copy 'generated-files.json')
$null = & $py $gen 2>&1
Assert-That ($LASTEXITCODE -eq 0) 'a project with no list of what was written, whose files are all the generator''s output, is accepted'
$stale = Join-Path $copy 'CopilotInsights.SemanticModel' 'definition' 'tables' 'Retired.tmdl'
Set-Content -LiteralPath $stale -Value 'table Retired' -NoNewline
$list = Join-Path $copy 'generated-files.json'
$m = Get-Content -LiteralPath $list -Raw | ConvertFrom-Json -AsHashtable
$m.files['CopilotInsights.SemanticModel/definition/tables/Retired.tmdl'] = (Get-FileHash -LiteralPath $stale -Algorithm SHA256).Hash.ToLowerInvariant()
$m | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $list
$null = & $py $gen 2>&1
Assert-That ($LASTEXITCODE -eq 0 -and -not (Test-Path $stale)) 'a file an earlier run wrote, and this one does not, is removed'
}
finally { Remove-Item -LiteralPath $tmp -Recurse -Force -ErrorAction SilentlyContinue }

Write-Host ''
if ($script:Failures.Count) { Write-Host "$($script:Failures.Count) assertion(s) failed." -ForegroundColor Red; exit 1 }
Write-Host 'All Power BI model tests passed.' -ForegroundColor Green
