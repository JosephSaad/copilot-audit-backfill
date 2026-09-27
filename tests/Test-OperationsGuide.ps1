<#
.SYNOPSIS
    Keeps docs/OPERATIONS.md complete: every Admin page in the dashboard's
    navigation, and every -Task of tools/Invoke-CopilotMaintenance.ps1, must be
    in its reference section, and every one the guide names must still exist.

.DESCRIPTION
    Reads the sources, not a list kept by hand, so adding an Admin page or a
    maintenance task without documenting it fails the build.

.EXAMPLE
    pwsh -NoProfile -File tests/Test-OperationsGuide.ps1
#>
#Requires -Version 7.2
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Split-Path $PSScriptRoot -Parent
$guide = Get-Content -LiteralPath (Join-Path $root 'docs' 'OPERATIONS.md') -Raw
$marker = '## Every Admin page and maintenance task'
$at = $guide.IndexOf($marker)
$script:Failures = 0
function Assert-That([bool] $Condition, [string] $Message) {
    if ($Condition) { Write-Host "  PASS  $Message" } else { Write-Host "  FAIL  $Message" -ForegroundColor Red; $script:Failures++ }
}

Assert-That ($at -ge 0) "docs/OPERATIONS.md has its reference section ('$marker')"
$reference = if ($at -ge 0) { $guide.Substring($at) } else { '' }

# The Admin pages, as the navigation lists them.
$nav = Get-Content -LiteralPath (Join-Path $root 'dashboard' 'CopilotInsights' 'Pages' 'Nav.cs') -Raw
$pages = [regex]::Matches($nav, 'new\("([^"]+)", "/admin[^"]*", Admin: true\)') | ForEach-Object { $_.Groups[1].Value }
Assert-That ($pages.Count -ge 5) "found the Admin pages in Nav.cs ($($pages.Count))"
foreach ($p in $pages) {
    Assert-That ($reference.Contains("Admin › $p |")) "Admin › $p is in the reference"
}
$documented = [regex]::Matches($reference, '\| Admin › ([^|]+?) \|') | ForEach-Object { $_.Groups[1].Value.Trim() }
foreach ($d in $documented) {
    Assert-That ($d -in $pages) "Admin › $d, in the guide, is still a page"
}

# The maintenance tasks, from the script's ValidateSet.
$maint = Get-Content -LiteralPath (Join-Path $root 'tools' 'Invoke-CopilotMaintenance.ps1') -Raw
$set = [regex]::Match($maint, "\[ValidateSet\(((?:'[^']+',?\s*)+)\)\]\s*\[string\]\s*\`$Task")
Assert-That $set.Success 'found the -Task ValidateSet in Invoke-CopilotMaintenance.ps1'
$tasks = [regex]::Matches($set.Groups[1].Value, "'([^']+)'") | ForEach-Object { $_.Groups[1].Value }
foreach ($t in $tasks) {
    Assert-That ($reference.Contains("| ``$t`` |")) "-Task $t is in the reference"
}
$named = [regex]::Matches($reference, '\| `([A-Za-z]+)` \|') | ForEach-Object { $_.Groups[1].Value }
foreach ($n in $named) {
    Assert-That ($n -in $tasks) "-Task $n, in the guide, still exists"
}

# 2.4.0: every report page in the navigation is in DASHBOARD.md's page table (| Persona | Page | ...),
# and every page that table names is still in the navigation. Admin pages are checked above.
$dash = Get-Content -LiteralPath (Join-Path $root 'docs' 'DASHBOARD.md') -Raw
$table = $dash.Substring($dash.IndexOf('## What each persona gets'))
$table = $table.Substring(0, $table.IndexOf("`n## ", 5))
$reportPages = [regex]::Matches($nav, 'new\("([^"]+)", "(/[^"]*)"(?![^)]*Admin: true)') | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique
Assert-That ($reportPages.Count -ge 40) "found the report pages in Nav.cs ($($reportPages.Count))"
$rows = [regex]::Matches($table, '(?m)^\|[^|
]*\|\s*([^|
]+?)\s*\|') | ForEach-Object { $_.Groups[1].Value.Trim() }
foreach ($p in $reportPages) {
    Assert-That ($p -in $rows) "$p is in DASHBOARD.md's page table"
}
foreach ($r in $rows | Where-Object { $_ -notin 'Page', '---' -and $_ -notlike 'Health,*' }) {
    Assert-That ($r -in $reportPages) "$r, in DASHBOARD.md's page table, is still a page"
}

Write-Host ''
if ($script:Failures) { Write-Host "$($script:Failures) check(s) failed: update docs/OPERATIONS.md or docs/DASHBOARD.md." -ForegroundColor Red; exit 1 }
Write-Host 'The operations guide covers every Admin page and maintenance task, and the dashboard guide every report page.' -ForegroundColor Green
